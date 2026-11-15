using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace SFSearch.Service
{
    /// <summary>
    /// One worker: a normal SFSearch program that runs one command at a time for the coordinator. Its shared state and
    /// console are its own, so N workers run N commands side by side. If it crashes, only its command is affected; the
    /// coordinator starts a new worker and runs that command once more.
    /// A second thread reads the coordinator's pipe, so a message can arrive while a command runs: the answer to a
    /// heavy-lane request, or "an agent is waiting" for an indexer job.
    /// </summary>
    public static class ServiceWorker
    {
        static StreamWriter? _w;
        static readonly object _wLock = new();
        static readonly ManualResetEventSlim _heavyGate = new(false);
        static WorkOrder? _heavyAnswer;
        static string _currentJob = "";
        static volatile bool _closed;

        /// <summary>The service's scratch folder for a home: emptied by the coordinator at every start.</summary>
        public static string ScratchFolder(string home) => Path.Combine(home, "scratch");

        /// <summary>True in a worker that is connected to its coordinator.</summary>
        public static bool Connected => _w != null;

        /// <summary>Tells the coordinator something mid-command ("blocked" / "unblocked": waiting for another worker). Does nothing outside a worker.</summary>
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

        /// <summary>Asks the coordinator to admit the running command as heavy, and waits for its answer (grant or deny).</summary>
        internal static WorkOrder AskHeavy(int estimateMB, string what)
        {
            _heavyGate.Reset();
            _heavyAnswer = null;
            if (_closed) return new WorkOrder { Kind = "deny", Reason = "no_memory" };
            Send(new WorkerEvent { Evt = "heavy", Job = _currentJob, EstimateMB = estimateMB, What = what });
            while (true)
            {
                _heavyGate.Wait();
                var a = _heavyAnswer;
                if (a != null && a.Job == _currentJob) return a;
                if (_closed) return new WorkOrder { Kind = "deny", Reason = "no_memory" };
                _heavyGate.Reset(); // an answer for an earlier command: keep waiting
                if (_heavyAnswer is { } late && late.Job == _currentJob) return late;
            }
        }

        public static void Run(string pipeName)
        {
            // Asynchronous: a blocking pipe lets one thread at a time through, so the listening thread's read would hold up every send
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(30000);
            using var reader = Wire.Reader(pipe);
            _w = Wire.Writer(pipe);
            SessionServer.Active = true;
            // Before this program's first SQLite connection: the SQL layer's reading threads would queue for SQLite's statistics lock
            Engine.EngineLibs.TuneSqlite();
            // Mutagen's one-time start-up (about 0.9 s on fixes.13) here, before the first command, not inside it
            SFSearch.Records.MutagenStart.Run();
            var stdout = Console.Out;
            var stderr = Console.Error;
            var orders = new BlockingCollection<WorkOrder>();
            var listen = new Thread(() =>
            {
                try
                {
                    string? l;
                    while ((l = reader.ReadLine()) != null)
                    {
                        WorkOrder? o;
                        try { o = Wire.Deserialize<WorkOrder>(l); }
                        catch (System.Text.Json.JsonException) { continue; }
                        if (o == null) continue;
                        switch (o.Kind)
                        {
                            case "grant":
                            case "deny": _heavyAnswer = o; _heavyGate.Set(); break;
                            case "yield": if (o.Job == _currentJob) JobContext.AskedToYield(); break;
                            default: orders.Add(o); break;
                        }
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                // The coordinator closed the pipe (this worker is retired) or is gone: finish and exit
                _closed = true;
                _heavyGate.Set();
                orders.CompleteAdding();
            }) { IsBackground = true, Name = "coordinator pipe" };
            listen.Start();
            Send(new WorkerEvent { Evt = "ready" });
            foreach (var order in orders.GetConsumingEnumerable())
            {
                _currentJob = order.Job;
                var clock = Stopwatch.StartNew();
                var (o, e, code) = RunOnce(order, stdout, stderr);
                // SQLite can report a momentary "disk I/O error" when a reader opens a database just as the last writer
                // closes it and removes its -wal/-shm files. Commands only read, so run it again once.
                if (IsTransientSqlite(o.ToString()) || IsTransientSqlite(e.ToString()))
                {
                    Thread.Sleep(250);
                    (o, e, code) = RunOnce(order, stdout, stderr);
                }
                long mem;
                using (var me = Process.GetCurrentProcess()) mem = me.PrivateMemorySize64 / 1048576;
                var done = new WorkerEvent { Evt = "done", Job = order.Job, Out = o.ToString(), Err = e.ToString(), Code = code, Ms = clock.ElapsedMilliseconds, MemMB = mem };
                JobContext.Fill(done);
                done.OpenPlugins = Engine.EngineRecords.OpenPlugins;
                _currentJob = "";
                try { Send(done); }
                catch (IOException) { return; } // the coordinator is gone
            }
        }

        static bool IsTransientSqlite(string text) =>
            text.Contains("SQLite Error 10:", StringComparison.Ordinal) || text.Contains("disk I/O error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("SQLite Error 5: 'database is locked'", StringComparison.Ordinal);

        static (StringWriter Out, StringWriter Err, int Code) RunOnce(WorkOrder order, TextWriter stdout, TextWriter stderr)
        {
            {
                var o = new StringWriter();
                var e = new StringWriter();
                int code;
                try
                {
                    JobContext.Reset(order);
                    Directory.SetCurrentDirectory(!string.IsNullOrEmpty(order.Cwd) && Directory.Exists(order.Cwd) ? order.Cwd : Home.Dir);
                    Console.SetOut(o);
                    Console.SetError(e);
                    Environment.ExitCode = 0;
                    if (order.Tool != null)
                    {
                        // Said once per home, with the first answer after this version is put in (a command line says it in Program.Main)
                        if (!order.Tool.StartsWith("__", StringComparison.Ordinal)) Home.OldDataNote();
                        ToolCalls.Run(order.Tool, order.Params);
                    }
                    else Program.Main(order.Args);
                    code = Environment.ExitCode;
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException ?? ex;
                    e.WriteLine($"Error: {inner.GetType().Name}: {inner.Message}");
                    code = 2; // an error, as for every verb (S43); a command line's crash is caught in Program.Main
                }
                finally
                {
                    Console.SetOut(stdout);
                    Console.SetError(stderr);
                }
                return (o, e, code);
            }
        }
    }
}
