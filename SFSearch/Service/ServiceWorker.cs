using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
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
                                if (o.Job == _currentJob) HoldHeapUnder(o.LimitMB);
                                _heavyAnswer = o; _heavyGate.Set(); break;
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
                HoldHeapUnder(order.LimitMB);
                var clock = Stopwatch.StartNew();
                var (o, e, code) = RunOnce(order, stdout, stderr);
                // SQLite can report a momentary "disk I/O error" when a reader opens a database just as the last writer
                // closes it and removes its -wal/-shm files. Commands only read, so run it again once.
                if (IsTransientSqlite(o.ToString()) || IsTransientSqlite(e.ToString()))
                {
                    Thread.Sleep(250);
                    (o, e, code) = RunOnce(order, stdout, stderr);
                }
                // A mod's or a patch's file is held only while a command reads it (GAPS S215)
                try { Engine.EngineRecords.ReleaseOthers(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                long mem;
                using (var me = Process.GetCurrentProcess()) mem = me.PrivateMemorySize64 / 1048576;
                var done = new WorkerEvent { Evt = "done", Job = order.Job, Out = o.ToString(), Err = e.ToString(), Code = code, Ms = clock.ElapsedMilliseconds, MemMB = mem };
                JobContext.Fill(done);
                done.OpenPlugins = Engine.EngineRecords.OpenPlugins;
                _currentJob = "";
                try { Send(done); }
                catch (IOException) { return; } // the coordinator is gone
                if (_outOfMemory) return; // a fresh worker takes its place
            }
        }

        /// <summary>
        /// What is kept between the heap's limit and the command's allowance for the worker's memory outside the heap (the
        /// runtime and the server collector's own, Mutagen's and SQLite's: private bytes less the heap's committed bytes),
        /// MB: at least this, and at least what it is now plus <see cref="OutsideGrowthMB"/>. Every cell of Starfield.esm at
        /// an 8 GB PC's 1.5 GB finished in every run held 320 MB under (the worker at 1.41-1.47 GB), and was stopped in one
        /// of two runs held only what was outside at the start plus 128 MB (J57).
        /// </summary>
        const long OutsideMinMB = 320;
        /// <summary>Room for the memory outside the heap to grow before the next look (MB).</summary>
        const long OutsideGrowthMB = 96;
        /// <summary>The heap limit is never set under what the heap has committed now plus this (MB): .NET refuses a limit
        /// under what is committed (InvalidOperationException), and a command can't fail at once by it.</summary>
        const long HeapRoomMB = 64;
        /// <summary>How often the limit follows the memory outside the heap while a command runs (ms).</summary>
        const int HoldEveryMs = 250;
        static long _limitMB, _heldMB, _allowanceMB;
        static readonly object _holdLock = new();

        /// <summary>What the coordinator lets the running command hold (MB): workerMaxMB, a heavy command's own allowance,
        /// the indexer's; 0 before the first order.</summary>
        internal static long AllowanceMB { get { lock (_holdLock) return _allowanceMB; } }
        static Timer? _holdTimer;
        static volatile bool _outOfMemory;

        /// <summary>
        /// Holds .NET's heap under what the coordinator lets the running command hold (J57, GAPS S84), on a worker with the
        /// server collector and an allowance under 2.5 GB (<see cref="ServiceConfig.ServerGcMinWorkerMB"/>: an 8 GB PC's
        /// 1.5 GB a command), where an unheld server worker passes it: every cell of Starfield.esm did in 4 to 7 s. The
        /// limit is the allowance less what the worker holds outside the heap (<see cref="OutsideMinMB"/>), set when the
        /// command starts and when a heavy part is granted, and looked at again four times a second while it runs, since
        /// the memory outside the heap grows with the read. Near its limit the garbage collector collects harder instead of
        /// growing; held, every cell finishes at 1.4-1.5 GB. A command that needs more ends with the same error the
        /// coordinator's watchdog gives. With a larger allowance, or the workstation collector, nothing is held (J44:
        /// a server worker read every cell at 2.0-2.3 GB, under a 16 GB PC's 2.7 GB).
        /// </summary>
        static void HoldHeapUnder(long limitMB)
        {
            lock (_holdLock)
            {
                if (limitMB > 0) _allowanceMB = limitMB;
                bool hold = limitMB > 0 && limitMB < ServiceConfig.ServerGcMinWorkerMB && System.Runtime.GCSettings.IsServerGC;
                if (!hold)
                {
                    if (_limitMB > 0) { AppContext.SetData("GCHeapHardLimit", 0UL); GC.RefreshMemoryLimit(); }
                    _limitMB = 0; _heldMB = 0;
                    return;
                }
                _limitMB = limitMB;
                _heldMB = 0;
                Hold();
                _holdTimer ??= new Timer(_ =>
                {
                    // A timer's exception would end the worker: a look that fails leaves the limit as it was
                    try { lock (_holdLock) Hold(); }
                    catch (Exception e) when (e is InvalidOperationException or OutOfMemoryException or ArgumentException) { }
                }, null, HoldEveryMs, HoldEveryMs);
            }
        }

        /// <summary>Sets the heap's limit for the allowance in effect (call with _holdLock held).</summary>
        static void Hold()
        {
            if (_limitMB <= 0) return;
            var info = GC.GetGCMemoryInfo();
            long privateMB;
            using (var me = Process.GetCurrentProcess()) privateMB = me.PrivateMemorySize64 >> 20;
            long outsideMB = Math.Max(OutsideMinMB, Math.Max(0, privateMB - (info.TotalCommittedBytes >> 20)) + OutsideGrowthMB);
            long heapMB = Math.Max(_limitMB - outsideMB, Math.Max(info.HeapSizeBytes, info.TotalCommittedBytes) / 1048576 + HeapRoomMB);
            if (Math.Abs(heapMB - _heldMB) < 16) return;
            AppContext.SetData("GCHeapHardLimit", (ulong)heapMB << 20);
            try { GC.RefreshMemoryLimit(); _heldMB = heapMB; }
            catch (InvalidOperationException)
            {
                // Under what the heap has committed since the last look: kept as it was, looked at again in a moment
                AppContext.SetData("GCHeapHardLimit", (ulong)_heldMB << 20);
            }
        }

        /// <summary>The command needed more than its heap may hold: the coordinator's own words for a command over its limit.</summary>
        static string OverLimitLine() =>
            $"Error: the command was stopped: it used more than {MemoryInfo.GB(_allowanceMB)} GB of memory, this PC's limit for one command (workerMaxMB). Fix: ask for less at once (a narrower question, or the new tools), or raise workerMaxMB in sfsearch.json if this PC has the memory to spare.";

        static bool IsOutOfMemory(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
                if (e is OutOfMemoryException || (e is AggregateException a && a.InnerExceptions.Any(IsOutOfMemory))) return true;
            return false;
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
                catch (Exception ex) when (IsOutOfMemory(ex))
                {
                    // Over the heap's limit (HoldHeapUnder): as if the watchdog had stopped it; this worker is replaced after
                    _outOfMemory = true;
                    e.WriteLine(OverLimitLine());
                    code = 5;
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
