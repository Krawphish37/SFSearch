using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SFSearch.Service
{
    /// <summary>
    /// The SFSearch service's coordinator (2026-11-07). It never reads a plugin. It takes requests from any number of
    /// agents (MCP adapters and sfs.cmd), queues them fairly (one line per agent, taken in turn), hands each to a free
    /// worker program, and returns the answer. Workers that crash are replaced and their command is run once more; a
    /// command past its time limit or memory limit is stopped. Commands from several agents run at the same time; only
    /// commands that need the same plugin's record type built for the first time wait for each other.
    /// </summary>
    public static class ServiceHost
    {
        // ---------------------------------------------------------------- Settings (environment variables override)
        // From sfsearch.json (ServiceConfig: read again when it changes; environment variables override it)
        static ServiceConfig Cfg => ServiceConfig.Load(_home);
        static int Workers => Cfg.Workers;
        static int MaxWorkersWithWaiting => Workers * 2;            // workers waiting on another's build don't count against Workers
        static int MinIdle => Math.Min(2, Workers);
        static TimeSpan JobTimeout => TimeSpan.FromMinutes(Cfg.JobTimeoutMinutes);
        static long RecycleMB => Cfg.WorkerRecycleMB;
        static long HardMB => Cfg.WorkerMaxMB;
        static int RecycleJobs => Cfg.WorkerRecycleCommands;
        static int QueueLimit => Cfg.QueueLimit;
        static long DataCapMB => Cfg.DataCapGB * 1024L;

        static string _home = "", _dataDir = "", _logDir = "", _exe = "";
        static readonly DateTime Started = DateTime.Now;
        static readonly object Gate = new();
        static readonly CancellationTokenSource Stopping = new();
        static readonly Dictionary<string, Job> _jobs = new();
        static readonly Dictionary<string, LinkedList<Job>> _lanes = new(StringComparer.OrdinalIgnoreCase);
        static readonly List<string> _laneOrder = new();
        static int _rr, _workerSeq;
        static long _jobSeq;
        static readonly List<Worker> _workers = new();
        static readonly Dictionary<string, DateTime> _lastUsed = new(StringComparer.OrdinalIgnoreCase);
        static long _served, _failed, _retried, _killed, _crashed, _rejected;

        // Warm-up: low-priority jobs in their own lane, taken only when no agent is waiting
        const string WarmLane = "~warmup";
        static bool WarmupOn => Cfg.Warmup.Enabled;
        static DateTime _warmDoneAt = DateTime.MaxValue;
        static DateTime _lastAgent = DateTime.Now;
        static string _warmState = "waiting for a quiet moment";
        static readonly Queue<string> _warmTargets = new();
        static int _warmTotal, _warmDone;
        static IntPtr _jobObject;

        sealed class Job
        {
            public string Id = "";
            public string Agent = "";
            public string[] Args = Array.Empty<string>();
            public string? Cwd;
            public DateTime Created = DateTime.Now;
            public DateTime? Started, Finished;
            public int Attempts;
            public bool Cancelled;
            public Worker? On;
            public readonly TaskCompletionSource<Reply> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public string Short => string.Join(' ', Args).Replace('\n', ' ') is var s && s.Length > 90 ? s[..90] + "…" : string.Join(' ', Args).Replace('\n', ' ');
        }

        sealed class Worker
        {
            public int Id;
            public Process Proc = null!;
            public NamedPipeServerStream Pipe = null!;
            public StreamWriter? W;
            public bool Ready, Blocked, Retiring;
            public string? BlockedOn;
            public Job? Current;
            public int Served;
            public long MemMB;
            public string? KillReason;
            public readonly Queue<string> Tail = new();
        }

        // ---------------------------------------------------------------- Start
        public static void Run()
        {
            // Many callers wait on pipes at once: start with enough threads that none waits for the pool to grow
            ThreadPool.SetMinThreads(128, 128);
            _home = Sfs.HomeDir;
            _dataDir = ServiceWorker.DataFolder(_home);
            _logDir = Path.Combine(_home, "Service", "logs");
            _exe = Environment.ProcessPath ?? "SFSearch.exe";
            Directory.CreateDirectory(_dataDir);
            Directory.CreateDirectory(_logDir);
            using var single = new Mutex(false, @"Local\sfsearch_service_" + Wire.Hash(_home));
            bool owner;
            try { owner = single.WaitOne(0); } catch (AbandonedMutexException) { owner = true; }
            if (!owner) return; // another service already runs for this home
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("CRASH (coordinator): " + e.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (_, e) => { Log("Unobserved: " + e.Exception.GetBaseException().Message); e.SetObserved(); };
            _jobObject = CreateKillOnCloseJob();
            LoadUsage();
            CleanOldLogs();
            File.WriteAllText(Path.Combine(_home, "Cache", "service.txt"), $"{Environment.ProcessId}|{DateTime.Now:o}|{_exe}");
            Log($"Service started: process {Environment.ProcessId}, {Workers} workers, data in {_dataDir}");
            try
            {
                lock (Gate) Pump();
                var accept = Task.Run(AcceptLoop);
                var watch = Task.Run(Watchdog);
                Task.WaitAny(new[] { accept, watch }.Select(t => t.ContinueWith(_ => { })).ToArray());
                Stopping.Cancel();
                try { Task.WaitAll(new[] { accept, watch }, 5000); } catch (AggregateException) { }
            }
            finally
            {
                Shutdown();
                single.ReleaseMutex();
            }
        }

        static void Shutdown()
        {
            // Let running commands finish (up to 15 s); queued ones are answered "stopped" and the callers ask again
            var drain = Stopwatch.StartNew();
            while (drain.Elapsed.TotalSeconds < 15) { lock (Gate) if (!_workers.Any(w => w.Current != null)) break; Thread.Sleep(100); }
            lock (Gate)
            {
                foreach (var j in _jobs.Values.Where(j => !j.Done.Task.IsCompleted))
                    j.Done.TrySetResult(new Reply { Done = true, Job = j.Id, Code = 2, Err = "Error: the service stopped before this command finished.\n" });
                foreach (var w in _workers.ToList()) { w.KillReason = "service stopping"; try { w.Proc.Kill(true); } catch (Exception) { } }
            }
            SaveUsage();
            try { var m = Path.Combine(_home, "Cache", "service.txt"); if (File.Exists(m)) File.Delete(m); } catch (IOException) { }
            Log("Service stopped.");
        }

        // ---------------------------------------------------------------- Requests
        static PipeSecurity? Security()
        {
            if (!OperatingSystem.IsWindows()) return null;
            var sec = new PipeSecurity();
            var me = WindowsIdentity.GetCurrent().User!;
            sec.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
            sec.SetOwner(me);
            return sec;
        }

        static NamedPipeServerStream NewPipe(string name, int instances)
        {
            if (OperatingSystem.IsWindows())
                return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, instances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, Security());
            return new NamedPipeServerStream(name, PipeDirection.InOut, instances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }

        static async Task AcceptLoop()
        {
            string name = Wire.PipeName(_home);
            while (!Stopping.IsCancellationRequested)
            {
                NamedPipeServerStream server;
                try { server = NewPipe(name, NamedPipeServerStream.MaxAllowedServerInstances); }
                catch (IOException e) { Log("Pipe: " + e.Message); await Task.Delay(500); continue; }
                try { await server.WaitForConnectionAsync(Stopping.Token); }
                catch (OperationCanceledException) { server.Dispose(); break; }
                catch (IOException) { server.Dispose(); continue; }
                _ = Task.Run(() => Handle(server));
            }
        }

        static async Task Handle(NamedPipeServerStream pipe)
        {
            try
            {
                using (pipe)
                {
                    using var r = Wire.Reader(pipe);
                    var readLine = r.ReadLineAsync();
                    if (await Task.WhenAny(readLine, Task.Delay(30000)) != readLine) return; // a caller that never sends
                    string? line = await readLine;
                    Reply reply;
                    Request? req = null;
                    try { req = line == null ? null : Wire.Deserialize<Request>(line); }
                    catch (System.Text.Json.JsonException) { }
                    reply = req == null ? new Reply { Done = true, Code = 2, Error = "The request couldn't be read." } : await Answer(req);
                    using var w = Wire.Writer(pipe);
                    await w.WriteLineAsync(Wire.Serialize(reply));
                    await w.FlushAsync();
                    if (req?.Op == "stop") { await Task.Delay(200); Stopping.Cancel(); }
                }
            }
            catch (IOException) { } // the caller went away; its job keeps running
            catch (ObjectDisposedException) { }
            catch (Exception e) { Log("Handle: " + e); }
        }

        static async Task<Reply> Answer(Request req)
        {
            switch ((req.Op ?? "").ToLowerInvariant())
            {
                case "ping": return new Reply { Done = true, Out = $"{Environment.ProcessId}" };
                case "status": return new Reply { Done = true, Out = Status() };
                case "stop": return new Reply { Done = true, Out = "Service stopped. Commands use the stored databases again.\n" };
                case "cancel": return Cancel(req.Job);
                case "job":
                    {
                        Job? j;
                        lock (Gate) _jobs.TryGetValue(req.Job ?? "", out j);
                        if (j == null) return new Reply { Done = true, Code = 2, Error = $"No job {req.Job} (results are kept for an hour)." };
                        return await Wait(j, req.WaitMs);
                    }
                case "run":
                    {
                        if (req.Args == null || req.Args.Length == 0) return new Reply { Done = true, Code = 2, Error = "No command given." };
                        Job job;
                        lock (Gate)
                        {
                            int queued = _lanes.Values.Sum(l => l.Count);
                            if (queued >= QueueLimit)
                            {
                                _rejected++;
                                return new Reply { Done = true, Code = 3, Error = $"The service is busy: {queued} commands are waiting. Try again shortly." };
                            }
                            _lastAgent = DateTime.Now;
                            job = new Job { Id = $"j{Environment.ProcessId:x}-{Interlocked.Increment(ref _jobSeq)}", Agent = string.IsNullOrWhiteSpace(req.Agent) ? "cli" : req.Agent!, Args = req.Args, Cwd = req.Cwd };
                            _jobs[job.Id] = job;
                            Enqueue(job, front: false);
                            Pump();
                        }
                        return await Wait(job, req.WaitMs);
                    }
                default: return new Reply { Done = true, Code = 2, Error = $"Unknown request '{req.Op}'." };
            }
        }

        static async Task<Reply> Wait(Job job, int waitMs)
        {
            var t = job.Done.Task;
            if (waitMs < 0) return await t;
            if (await Task.WhenAny(t, Task.Delay(waitMs)) == t) return await t;
            lock (Gate)
            {
                int ahead = 0;
                if (job.Started == null && _lanes.TryGetValue(job.Agent, out var lane))
                    ahead = _lanes.Values.Sum(l => l.Count) - 1;
                string state = job.Started == null ? "queued" : job.On?.Blocked == true ? $"waiting for another command's first read of {job.On.BlockedOn}" : "running";
                return new Reply { Done = false, Job = job.Id, Ahead = Math.Max(0, ahead), State = state, Ms = (long)(DateTime.Now - job.Created).TotalMilliseconds };
            }
        }

        static Reply Cancel(string? id)
        {
            lock (Gate)
            {
                if (id == null || !_jobs.TryGetValue(id, out var j)) return new Reply { Done = true, Code = 2, Error = $"No job {id}." };
                if (j.Done.Task.IsCompleted) return new Reply { Done = true, Job = id, Out = "Already finished.\n" };
                j.Cancelled = true;
                if (j.Started == null)
                {
                    foreach (var lane in _lanes.Values) lane.Remove(j);
                    Finish(j, new Reply { Done = true, Job = j.Id, Code = 4, Err = "Cancelled before it started.\n" });
                }
                else if (j.On != null) Kill(j.On, "cancelled by the caller");
                return new Reply { Done = true, Job = id, Out = "Cancelled.\n" };
            }
        }

        // ---------------------------------------------------------------- Queue and workers (call with Gate held)
        static void Enqueue(Job job, bool front)
        {
            if (!_lanes.TryGetValue(job.Agent, out var lane)) { _lanes[job.Agent] = lane = new LinkedList<Job>(); _laneOrder.Add(job.Agent); }
            if (front) lane.AddFirst(job); else lane.AddLast(job);
        }

        static Job? TakeNext()
        {
            // Agents first: the warm-up lane only when every other line is empty
            bool agentsWaiting = _lanes.Any(kv => kv.Key != WarmLane && kv.Value.Count > 0);
            for (int k = 0; k < _laneOrder.Count; k++)
            {
                _rr = (_rr + 1) % _laneOrder.Count;
                var lane = _lanes[_laneOrder[_rr]];
                if (lane.Count == 0 || (agentsWaiting && _laneOrder[_rr] == WarmLane)) continue;
                var job = lane.First!.Value;
                lane.RemoveFirst();
                return job;
            }
            return null;
        }

        static void Pump()
        {
            if (Stopping.IsCancellationRequested) return;
            int queued = _lanes.Values.Sum(l => l.Count);
            while (queued > 0)
            {
                var free = _workers.FirstOrDefault(w => w.Ready && w.Current == null && !w.Retiring);
                if (free == null) break;
                if (!MemoryOk() && _workers.Any(w => w.Current != null)) break;
                var job = TakeNext();
                if (job == null) break;
                queued--;
                Assign(free, job);
            }
            int running = _workers.Count(w => w.Current != null && !w.Blocked);
            int spare = _workers.Count(w => w.Current == null && !w.Retiring);
            while ((queued > spare || spare < MinIdle) && running + spare < Workers && _workers.Count < MaxWorkersWithWaiting && MemoryOk())
            {
                Spawn();
                spare++;
            }
        }

        static void Assign(Worker w, Job job)
        {
            job.On = w;
            job.Started = DateTime.Now;
            job.Attempts++;
            w.Current = job;
            var order = new WorkOrder { Job = job.Id, Args = job.Args, Cwd = job.Cwd };
            try
            {
                w.W!.WriteLine(Wire.Serialize(order));
                w.W.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                try { w.Proc.Kill(true); } catch (Exception) { } // like a crash: Gone() runs the command again on another worker
            }
        }

        static void Spawn()
        {
            var w = new Worker { Id = ++_workerSeq };
            string pipeName = $"sfsearch_w_{Wire.Hash(_home)}_{Environment.ProcessId}_{w.Id}";
            w.Pipe = NewPipe(pipeName, 1);
            var psi = new ProcessStartInfo(_exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _home,
            };
            foreach (var a in new[] { "service", "worker", _home, pipeName }) psi.ArgumentList.Add(a);
            psi.Environment["SFSEARCH_HOME"] = _home;
            try { w.Proc = Process.Start(psi)!; }
            catch (Exception e) { Log("Couldn't start a worker: " + e.Message); w.Pipe.Dispose(); return; }
            try { w.Proc.StandardInput.Close(); } catch (Exception) { } // nothing a command does can wait on input
            if (_jobObject != IntPtr.Zero) AssignProcessToJobObject(_jobObject, w.Proc.Handle);
            w.Proc.OutputDataReceived += (_, e) => TailLine(w, e.Data);
            w.Proc.ErrorDataReceived += (_, e) => TailLine(w, e.Data);
            w.Proc.BeginOutputReadLine();
            w.Proc.BeginErrorReadLine();
            _workers.Add(w);
            _ = Task.Run(() => WorkerLoop(w));
        }

        static void TailLine(Worker w, string? line)
        {
            if (line == null) return;
            lock (w.Tail) { w.Tail.Enqueue(line); while (w.Tail.Count > 40) w.Tail.Dequeue(); }
        }

        static async Task WorkerLoop(Worker w)
        {
            try
            {
                var connect = w.Pipe.WaitForConnectionAsync(Stopping.Token);
                if (await Task.WhenAny(connect, Task.Delay(60000)) != connect) throw new TimeoutException("the worker didn't connect within 60 s");
                await connect;
                using var r = Wire.Reader(w.Pipe);
                lock (Gate) w.W = Wire.Writer(w.Pipe);
                string? line;
                while ((line = await r.ReadLineAsync()) != null)
                {
                    WorkerEvent? e;
                    try { e = Wire.Deserialize<WorkerEvent>(line); } catch (System.Text.Json.JsonException) { continue; }
                    if (e == null) continue;
                    lock (Gate) OnEvent(w, e);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or TimeoutException)
            {
                if (w.KillReason == null && e is TimeoutException) w.KillReason = e.Message;
            }
            catch (Exception e) { Log($"Worker {w.Id} loop: {e}"); }
            try { if (!w.Proc.WaitForExit(5000)) w.Proc.Kill(true); } catch (Exception) { }
            lock (Gate) Gone(w);
        }

        static void OnEvent(Worker w, WorkerEvent e)
        {
            switch (e.Evt)
            {
                case "ready": w.Ready = true; break;
                case "blocked": w.Blocked = true; w.BlockedOn = e.On; break;
                case "unblocked": w.Blocked = false; w.BlockedOn = null; break;
                case "done":
                    {
                        var job = w.Current;
                        w.Current = null; w.Blocked = false; w.Served++; w.MemMB = e.MemMB;
                        foreach (var u in e.Used ?? Array.Empty<string>()) _lastUsed[u] = DateTime.Now;
                        if (job != null && job.Id == e.Job)
                        {
                            string err = e.Err ?? "";
                            if (Sf1EditOpen()) err += "[session] SF1Edit is open. Plugins it saves are read again on the next command; tell the user if a result looks out of date.\n";
                            Finish(job, new Reply { Done = true, Job = job.Id, Out = e.Out ?? "", Err = err, Code = e.Code, Ms = e.Ms });
                            _served++;
                            if (e.Code != 0) _failed++;
                            Log($"job {job.Id} [{job.Agent}] w{w.Id} {e.Ms} ms exit {e.Code} mem {e.MemMB} MB: {job.Short}");
                            if (job.Agent == WarmLane) WarmNext(job, e.Out ?? "");
                        }
                        if (w.MemMB > RecycleMB || w.Served >= RecycleJobs)
                        {
                            w.Retiring = true;
                            Log($"Worker {w.Id} retired after {w.Served} commands ({w.MemMB} MB).");
                            try { w.Pipe.Dispose(); } catch (Exception) { } // its loop ends and it exits
                        }
                        break;
                    }
            }
            Pump();
        }

        /// <summary>After 30 s with no agent command and nothing queued, plan the warm-up (call with Gate held).</summary>
        static void WarmStart()
        {
            var wc = Cfg.Warmup;
            if (!wc.Enabled) return;
            // After a game or mod update the changed files are read again: the plan is made again every few quiet minutes
            // (plugins already read cost a few milliseconds each)
            bool again = _warmState.StartsWith("done") && wc.RewarmAfterUpdates && DateTime.Now - _warmDoneAt > TimeSpan.FromMinutes(wc.RewarmCheckMinutes);
            if (_warmState != "waiting for a quiet moment" && !again) return;
            if (DateTime.Now - _lastAgent < TimeSpan.FromSeconds(wc.IdleSeconds) || _lanes.Values.Any(l => l.Count > 0) || _workers.Any(w => w.Current != null)) return;
            _warmTargets.Clear(); _warmTotal = 0; _warmDone = 0; _warmDoneAt = DateTime.MaxValue;
            _warmState = "planning (bringing the load-order index up to date)";
            // The plugins agents asked about most recently (remembered across restarts in usage.txt), newest first
            var recent = _lastUsed.OrderByDescending(kv => kv.Value).Select(kv => ServiceDb.PluginOf(kv.Key))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(wc.RecentCount);
            QueueWarm(new[] { "__warmup", "plan" }.Concat(recent).ToArray());
        }


        static void QueueWarm(string[] args)
        {
            var job = new Job { Id = $"j{Environment.ProcessId:x}-{Interlocked.Increment(ref _jobSeq)}", Agent = WarmLane, Args = args, Cwd = _home };
            _jobs[job.Id] = job;
            Enqueue(job, front: false);
        }

        /// <summary>One warm-up job finished: queue the next plugin, one at a time.</summary>
        static void WarmNext(Job job, string output)
        {
            if (job.Args.Length > 1 && job.Args[1] == "plan")
            {
                foreach (var line in output.Split((char)10))
                    if (line.StartsWith("warm: ")) _warmTargets.Enqueue(line[6..].Trim());
                _warmTotal = _warmTargets.Count;
            }
            else _warmDone++;
            if (_warmTargets.Count > 0)
            {
                string next = _warmTargets.Dequeue();
                _warmState = $"reading {next} ({_warmDone + 1} of {_warmTotal})";
                QueueWarm(new[] { "__warmup", next });
            }
            else
            {
                _warmState = $"done: {_warmDone} plugin(s) read, plus the load-order index";
                _warmDoneAt = DateTime.Now;
                Log("Warm-up " + _warmState);
            }
        }

        static void Gone(Worker w)
        {
            _workers.Remove(w);
            try { w.Pipe.Dispose(); } catch (Exception) { }
            var job = w.Current;
            w.Current = null;
            if (job != null && !job.Done.Task.IsCompleted)
            {
                string tail;
                lock (w.Tail) tail = string.Join("\n", w.Tail.TakeLast(12));
                int exit = -1;
                try { exit = w.Proc.HasExited ? w.Proc.ExitCode : -1; } catch (Exception) { }
                if (w.KillReason != null)
                {
                    _killed++;
                    Log($"job {job.Id} [{job.Agent}] stopped ({w.KillReason}): {job.Short}");
                    Finish(job, new Reply { Done = true, Job = job.Id, Code = job.Cancelled ? 4 : 5, Err = $"Error: the command was stopped: {w.KillReason}.\n" });
                }
                else
                {
                    _crashed++;
                    Log($"CRASH worker {w.Id} (exit {exit}) on job {job.Id} [{job.Agent}] attempt {job.Attempts}: {job.Short}\n{tail}");
                    if (job.Attempts < 2)
                    {
                        _retried++;
                        job.Started = null; job.On = null;
                        Enqueue(job, front: true);
                    }
                    else
                        Finish(job, new Reply { Done = true, Job = job.Id, Code = 6, Err = $"Error: SFSearch crashed on this command twice (exit code {exit}). Last output:\n{tail}\n" });
                }
            }
            else if (!w.Retiring && w.KillReason == null && !Stopping.IsCancellationRequested)
                Log($"Worker {w.Id} ended while idle.");
            try { w.Proc.Dispose(); } catch (Exception) { }
            Pump();
        }

        static void Finish(Job job, Reply reply)
        {
            job.Finished = DateTime.Now;
            job.On = null;
            job.Done.TrySetResult(reply);
        }

        static void Kill(Worker w, string reason)
        {
            w.KillReason ??= reason;
            try { w.Proc.Kill(true); } catch (Exception) { }
        }

        // ---------------------------------------------------------------- Watchdog: time and memory limits, clean-up
        static async Task Watchdog()
        {
            var lastGc = DateTime.MinValue;
            while (!Stopping.IsCancellationRequested)
            {
                try { await Task.Delay(1000, Stopping.Token); } catch (OperationCanceledException) { break; }
                try
                {
                    lock (Gate)
                    {
                        foreach (var w in _workers.ToList())
                        {
                            if (w.Current is { } job && job.Started is { } st && DateTime.Now - st > JobTimeout)
                                Kill(w, $"it ran longer than the {JobTimeout.TotalMinutes:0}-minute limit");
                            else if (w.Current != null)
                            {
                                try
                                {
                                    w.Proc.Refresh();
                                    long mb = w.Proc.PrivateMemorySize64 / 1048576;
                                    w.MemMB = mb;
                                    if (mb > HardMB) Kill(w, $"it used more than {HardMB / 1024} GB of memory");
                                }
                                catch (InvalidOperationException) { }
                            }
                        }
                        foreach (var id in _jobs.Where(kv => kv.Value.Finished is { } f && DateTime.Now - f > TimeSpan.FromHours(1)).Select(kv => kv.Key).ToList())
                            _jobs.Remove(id);
                        foreach (var lane in _lanes.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList())
                            if (!_jobs.Values.Any(j => j.Agent == lane && !j.Done.Task.IsCompleted)) { _lanes.Remove(lane); _laneOrder.Remove(lane); }
                        if (_rr >= _laneOrder.Count) _rr = 0;
                        Pump();
                    }
                    if (DateTime.Now - lastGc > TimeSpan.FromSeconds(60)) { lastGc = DateTime.Now; CleanData(); }
                    lock (Gate) WarmStart();
                }
                catch (Exception e) { Log("Watchdog: " + e); }
            }
        }

        /// <summary>Deletes databases of plugin copies that are gone or changed, half-written builds, and the least recently used past the size cap. Files still open are left for the next round.</summary>
        static void CleanData()
        {
            // Answers the MCP adapter saved to files, and --stdin files: a week is enough
            foreach (var dir in new[] { Path.Combine(_home, "Service", "results"), Path.Combine(_home, "Service", "tmp") })
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir))
                        if (DateTime.Now - File.GetLastWriteTime(f) > TimeSpan.FromDays(7)) TryDelete(f);
            var files = Directory.GetFiles(_dataDir);
            string data = Program.GetDataFolderPathPublic();
            long total = 0;
            var keep = new List<FileInfo>();
            foreach (var f in files)
            {
                var fi = new FileInfo(f);
                string name = fi.Name;
                if (name.Contains(".tmp-") && DateTime.Now - fi.LastWriteTime > TimeSpan.FromHours(1)) { TryDelete(f); continue; }
                if (!name.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || !name.Contains('@')) { total += fi.Length; continue; }
                string plugin = ServiceDb.PluginOf(name);
                string? current = ServiceDb.StampedName(plugin, Path.Combine(data, plugin));
                // SQLite lets Windows delete a file that's open, so a database is only removed once no command has used it for
                // 15 minutes (the longest command is 10): a command still reading the old copy of a changed plugin finishes first.
                bool recent = _lastUsed.TryGetValue(name, out var usedAt) && DateTime.Now - usedAt < TimeSpan.FromMinutes(15);
                if (!name.Equals(current, StringComparison.OrdinalIgnoreCase) && !recent) { if (TryDelete(f)) { Log($"Removed {name}: its plugin changed or is gone."); continue; } }
                total += fi.Length;
                keep.Add(fi);
            }
            long cap = DataCapMB * 1048576;
            if (total <= cap) return;
            foreach (var fi in keep.OrderBy(f => _lastUsed.TryGetValue(f.Name, out var t) ? t : f.LastWriteTime))
            {
                if (total <= cap) break;
                if (_lastUsed.TryGetValue(fi.Name, out var used) && DateTime.Now - used < TimeSpan.FromMinutes(10)) continue;
                long len = fi.Length;
                if (TryDelete(fi.FullName)) { total -= len; Log($"Removed {fi.Name} ({len / 1048576} MB): least recently used, data folder over {DataCapMB / 1024} GB."); }
            }
            SaveUsage();
        }

        static bool TryDelete(string f)
        {
            try
            {
                File.Delete(f);
                foreach (var side in new[] { f + "-wal", f + "-shm" }) if (File.Exists(side)) File.Delete(side);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        static string UsageFile => Path.Combine(_home, "Service", "usage.txt");

        static void LoadUsage()
        {
            try
            {
                if (!File.Exists(UsageFile)) return;
                foreach (var line in File.ReadAllLines(UsageFile))
                {
                    var p = line.Split('|');
                    if (p.Length == 2 && DateTime.TryParse(p[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)) _lastUsed[p[0]] = t;
                }
            }
            catch (IOException) { }
        }

        static void SaveUsage()
        {
            try
            {
                List<string> lines;
                lock (Gate) lines = _lastUsed.Select(kv => $"{kv.Key}|{kv.Value:o}").ToList();
                File.WriteAllLines(UsageFile, lines);
            }
            catch (IOException) { }
        }

        // ---------------------------------------------------------------- Status, log, memory
        static string Status()
        {
            var sb = new StringBuilder();
            lock (Gate)
            {
                sb.AppendLine($"Service running since {Started:yyyy-MM-dd HH:mm} (process {Environment.ProcessId}). Answered {_served} command(s): {_failed} with errors, {_crashed} worker crash(es), {_retried} retried, {_killed} stopped (time, memory or cancel), {_rejected} turned away (busy).");
                sb.AppendLine($"Workers: {_workers.Count} (up to {Workers} running at once; {MinIdle} kept ready). Each command may take {JobTimeout.TotalMinutes:0} min; a worker is replaced after {RecycleJobs} commands or {RecycleMB / 1024.0:0.#} GB.");
                foreach (var w in _workers.OrderBy(w => w.Id))
                {
                    string state = !w.Ready ? "starting" : w.Current == null ? "idle" : w.Blocked ? $"waiting for {w.BlockedOn}" : $"running {(DateTime.Now - w.Current.Started!.Value).TotalSeconds:0} s";
                    sb.AppendLine($"  worker {w.Id} (process {w.Proc.Id}): {state}, {w.Served} done, {w.MemMB} MB{(w.Current != null ? $" — [{w.Current.Agent}] {w.Current.Short}" : "")}");
                }
                int queued = _lanes.Values.Sum(l => l.Count);
                sb.AppendLine($"Warm-up: {(WarmupOn ? _warmState : "off (sfsearch.json, or SFSEARCH_WARMUP=0)")}.");
                sb.AppendLine($"Queue: {queued} waiting{(queued > 0 ? ": " + string.Join(", ", _lanes.Where(kv => kv.Value.Count > 0).Select(kv => $"{kv.Key} {kv.Value.Count}")) : "")}.");
            }
            long bytes = 0; int dbs = 0;
            foreach (var f in Directory.GetFiles(_dataDir)) { bytes += new FileInfo(f).Length; if (f.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) dbs++; }
            sb.AppendLine($"Data folder: {_dataDir}, {dbs} database(s), {bytes / 1048576} MB (cap {DataCapMB / 1024} GB). Log: {LogFile}");
            string dll = Path.Combine(_home, "bin", "Release", "net10.0", "SFSearch.dll");
            var mine = File.GetLastWriteTime(Path.Combine(AppContext.BaseDirectory, "SFSearch.dll"));
            if (File.Exists(dll) && File.GetLastWriteTime(dll) > mine.AddSeconds(5))
                sb.AppendLine("SFSearch has been rebuilt since this service started: 'Service restart' to use the new build.");
            if (Sf1EditOpen()) sb.AppendLine("SF1Edit is open: plugins it saves are read again on the next command.");
            return sb.ToString();
        }

        static string LogFile => Path.Combine(_logDir, $"service-{DateTime.Now:yyyyMMdd}.log");
        static readonly object LogLock = new();

        public static void Log(string text)
        {
            try { lock (LogLock) File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss.fff} {text}\n"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static void CleanOldLogs()
        {
            foreach (var f in Directory.GetFiles(_logDir, "service-*.log"))
                if (DateTime.Now - File.GetLastWriteTime(f) > TimeSpan.FromDays(7)) TryDelete(f);
        }

        static DateTime _sfCheck = DateTime.MinValue;
        static bool _sfOpen;

        static bool Sf1EditOpen()
        {
            if (DateTime.Now - _sfCheck < TimeSpan.FromSeconds(5)) return _sfOpen;
            _sfCheck = DateTime.Now;
            try { _sfOpen = Process.GetProcesses().Any(p => p.ProcessName.StartsWith("SF1Edit", StringComparison.OrdinalIgnoreCase) || p.ProcessName.StartsWith("xEdit", StringComparison.OrdinalIgnoreCase)); }
            catch (InvalidOperationException) { _sfOpen = false; }
            return _sfOpen;
        }

        /// <summary>False when the PC is short of memory: no new work starts until some frees up.</summary>
        static bool MemoryOk()
        {
            if (!OperatingSystem.IsWindows()) return true;
            var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return !GlobalMemoryStatusEx(ref m) || m.AvailPhys > 2L * 1024 * 1024 * 1024;
        }

        // ---------------------------------------------------------------- Windows: workers die with the service
        [StructLayout(LayoutKind.Sequential)]
        struct MemoryStatusEx { public uint Length, MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, uint length);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimits { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }

        static IntPtr CreateKillOnCloseJob()
        {
            if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimits>());
            return job;
        }
    }
}
