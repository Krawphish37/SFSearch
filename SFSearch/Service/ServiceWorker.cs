using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;

namespace SFSearch.Service
{
    /// <summary>
    /// One worker: a normal SFSearch program that runs one command at a time for the coordinator. Its shared state and
    /// console are its own, so N workers run N commands side by side. If it crashes, only its command is affected; the
    /// coordinator starts a new worker and runs that command once more.
    /// </summary>
    public static class ServiceWorker
    {
        static StreamWriter? _w;
        static readonly object _wLock = new();

        public static string DataFolder(string home) => Path.Combine(home, "Service", "data");

        /// <summary>Tells the coordinator something mid-command (waiting for another worker's build). Does nothing outside a worker.</summary>
        public static void Notify(string evt, string? on)
        {
            if (_w == null) return;
            Send(new WorkerEvent { Evt = evt, On = on });
        }

        static void Send(WorkerEvent e)
        {
            lock (_wLock)
            {
                _w!.WriteLine(Wire.Serialize(e));
                _w.Flush();
            }
        }

        public static void Run(string pipeName)
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            pipe.Connect(30000);
            using var reader = Wire.Reader(pipe);
            _w = Wire.Writer(pipe);
            SessionServer.Active = true;
            LiveDb.Enabled = true;
            Sfs.DbFolder = DataFolder(Sfs.HomeDir);
            Directory.CreateDirectory(Sfs.DbFolder);
            var stdout = Console.Out;
            var stderr = Console.Error;
            Send(new WorkerEvent { Evt = "ready" });
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                WorkOrder? order;
                try { order = Wire.Deserialize<WorkOrder>(line); }
                catch (System.Text.Json.JsonException) { continue; }
                if (order == null) continue;
                var clock = Stopwatch.StartNew();
                var (o, e, code, used) = RunOnce(order, stdout, stderr);
                // SQLite can report a momentary "disk I/O error" when a reader opens a database just as the last writer
                // closes it and removes its -wal/-shm files. Commands only read, so run it again once.
                if (LiveDb.BuildFailed || IsTransientSqlite(o.ToString()) || IsTransientSqlite(e.ToString()))
                {
                    System.Threading.Thread.Sleep(250);
                    (o, e, code, used) = RunOnce(order, stdout, stderr);
                }
                long mem;
                using (var me = Process.GetCurrentProcess()) mem = me.PrivateMemorySize64 / 1048576;
                if (mem > 2048)
                {
                    LiveDb.TrimMemory();
                    using var me2 = Process.GetCurrentProcess();
                    mem = me2.PrivateMemorySize64 / 1048576;
                }
                Send(new WorkerEvent { Evt = "done", Job = order.Job, Out = o.ToString(), Err = e.ToString(), Code = code, Ms = clock.ElapsedMilliseconds, MemMB = mem, Used = used });
            }
        }

        static bool IsTransientSqlite(string text) =>
            text.Contains("SQLite Error 10:", StringComparison.Ordinal) || text.Contains("disk I/O error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("SQLite Error 5: 'database is locked'", StringComparison.Ordinal);

        static (StringWriter Out, StringWriter Err, int Code, string[] Used) RunOnce(WorkOrder order, TextWriter stdout, TextWriter stderr)
        {
            {
                var o = new StringWriter();
                var e = new StringWriter();
                int code;
                string[] used = Array.Empty<string>();
                try
                {
                    LiveDb.ResetForCommand();
                    Sfs.ResetCaches();
                    ServiceDb.ResetForCommand();
                    Directory.SetCurrentDirectory(!string.IsNullOrEmpty(order.Cwd) && Directory.Exists(order.Cwd) ? order.Cwd : Sfs.HomeDir);
                    Console.SetOut(o);
                    Console.SetError(e);
                    Environment.ExitCode = 0;
                    Program.Main(order.Args);
                    code = Environment.ExitCode;
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException ?? ex;
                    e.WriteLine($"Error: {inner.GetType().Name}: {inner.Message}");
                    code = 1;
                }
                finally
                {
                    Console.SetOut(stdout);
                    Console.SetError(stderr);
                    try { Sfs.CloseResolver(); } catch (Exception) { }
                    used = ServiceDb.UsedThisCommand();
                }
                return (o, e, code, used);
            }
        }
    }
}
