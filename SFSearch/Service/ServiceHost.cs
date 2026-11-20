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
        /// <summary>
        /// An idle worker above <see cref="Workers"/> is retired after this long (2026-11-13, fix S21). Such workers are
        /// started only while others wait for the heavy lane or for another's build; once none waits, they are spare.
        /// </summary>
        static readonly TimeSpan ExtraIdle = TimeSpan.FromSeconds(10);
        static long _extraRetired;
        static TimeSpan JobTimeout => TimeSpan.FromMinutes(Cfg.JobTimeoutMinutes);
        static long RecycleMB => Cfg.WorkerRecycleMB;
        static long HardMB => Cfg.WorkerMaxMB;
        static int RecycleJobs => Cfg.WorkerRecycleCommands;
        static int QueueLimit => Cfg.QueueLimit;
        static long ServiceMaxMB => Cfg.ServiceMaxMB;
        static TimeSpan HeavyTimeout => TimeSpan.FromMinutes(Cfg.HeavyJobTimeoutMinutes);
        static long FloorMB => Cfg.MemoryFloorMB;

        // The memory guard (2026-11-12, fix S17): Windows' available memory is looked at four times a second. Under the
        // floor while agents' commands run, the indexer gives way first, then the biggest ordinary command is
        // stopped with one clear line. One at a time: the next only once that worker is gone and either memory came back
        // or it is still short 5 s later.
        static bool _short;
        static DateTime _shortSince = DateTime.MinValue, _bgPausedAt = DateTime.MinValue, _nextStopAllowed = DateTime.MinValue;
        static Worker? _lastStopped;
        static long _shortStops, _shortPauses, _shortEpisodes, _lowestAvailMB = long.MaxValue;
        /// <summary>A command holding less than this (MB) isn't stopped for the PC's sake: it would free next to nothing.</summary>
        const long ShortStopMinMB = 256;
        /// <summary>A page of a long answer when the caller asked for a cursor's next page without saying how big.</summary>
        const int DefaultPageChars = 20000;

        static string _home = "", _logDir = "", _exe = "", _scratch = "", _scratchNote = "", _indexesNote = "";
        static readonly DateTime Started = DateTime.Now;
        static readonly object Gate = new();
        static readonly CancellationTokenSource Stopping = new();
        static readonly Dictionary<string, Job> _jobs = new();
        static readonly Dictionary<string, LinkedList<Job>> _lanes = new(StringComparer.OrdinalIgnoreCase);
        static readonly List<string> _laneOrder = new();
        static int _rr, _workerSeq;
        static long _jobSeq;
        static readonly List<Worker> _workers = new();
        static long _served, _failed, _retried, _killed, _crashed, _rejected;

        static DateTime _lastAgent = DateTime.Now;
        static IntPtr _jobObject;

        // The indexer: one more low-priority lane, for the header pass, content hashes and link index of the later
        // milestones. One job at a time, only when no agent is waiting; a running one gives way when an agent has to wait.
        const string IndexLane = GiveWay.IndexLane;
        static long _indexRan, _indexGaveWay;
        // The header index of the new tools (milestone M5): built in the indexer lane when the service starts. While the
        // service is idle it is checked again every pluginCheckSeconds (sfsearch.json, 10 minutes by default: every
        // plugin's quick stamp, about 30 ms), and once an hour every file is hashed again (about half a second with the
        // files in Windows' cache). (SFSEARCH_INDEX=0, which turned all of it off, went in J37.)
        static readonly TimeSpan IndexSweepEvery = TimeSpan.FromMinutes(60);
        // The idle check's interval (pluginCheckSeconds); it comes after this long without an agent's command (IndexQuiet):
        // 30 s, or the interval when that is shorter
        static TimeSpan IndexCheckEvery => TimeSpan.FromSeconds(Cfg.PluginCheckSeconds);
        static TimeSpan IndexQuiet => TimeSpan.FromSeconds(Math.Min(30, Cfg.PluginCheckSeconds));
        // changes2's history (2026-11-13, fix S11): the header index's jobs also take a look for it, unless historyLook is off
        static bool HistoryOn => Cfg.HistoryLook;
        static string _historyState = "no look yet";
        static DateTime _indexLast = DateTime.MinValue, _indexLastSweep = DateTime.Now;
        static string _indexState = "not built yet";
        // The link index of the refs tool (milestone M5, second half): after every job of the header index, the plugins
        // that have no link file yet are walked, in the same lane. (SFSEARCH_LINKS=0, which turned it off, went in J37.)
        static string _linksState = "not built yet";
        // The kept indexes (J31, 2026-11-15): with keepStartupIndexes on, the header index and the link index are kept in
        // indexes\ between starts. The start's build maps what is there and takes each plugin's content key from the
        // stamps it remembers (a plugin whose quick stamp changed is hashed and read again); then one full check hashes
        // every plugin, in this lane, after the link index's job: it catches a change in the middle of a file that kept its
        // size, date and ends, and deletes the parts no plugin uses any more. Commands answer meanwhile.
        static bool KeepIndexes => Cfg.KeepStartupIndexes;
        static bool _startCheckQueued;
        static readonly DateTime StartedUtc = DateTime.UtcNow;
        static bool IsBackground(string lane) => lane == IndexLane;

        // The heavy lane: one heavy command at a time, and only when Windows has its estimate plus the spare available
        static Job? _heavy;
        static readonly List<Worker> _heavyWaiters = new();
        static long _heavyRan, _heavyNoMemory, _heavyNotAllowed;
        /// <summary>Calls cancelled by their key before the service had made their job (kept a minute).</summary>
        static readonly Dictionary<string, DateTime> _cancelledKeys = new();

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
            // The structured form
            public string? Tool;
            public Dictionary<string, System.Text.Json.JsonElement>? Params;
            public string? Key;
            // Heavy: the caller's OK, the estimate (the caller's, or the one the command asked with), and whether it has the lane
            public bool AllowHeavy;
            public int HeavyMB;
            public bool HeavyHeld;
            public DateTime? HeavySince;
            // Indexer: when it was asked to give way, and how often it has
            public DateTime? YieldAsked;
            public int GaveWay;
            /// <summary>Runs on a worker with the workstation garbage collector: the job asked for it on a server one (J58).</summary>
            public bool Workstation;
            // What the command said about its answer, for the structured reply
            public SourceInfo[]? Source;
            public string[]? Read;
            public int HeaderLines = -1;
            public FailInfo? Failure;
            public HeavyInfo? Heavy;
            /// <summary>The tool's own "more": rows it left out, and the cursor it takes for the next ones.</summary>
            public MoreInfo? ToolMore;
            public long MemMB, QueuedMs;
            public int[]? LineStarts;
            string Text => Tool != null ? Tool + " " + (Params == null ? "" : System.Text.Json.JsonSerializer.Serialize(Params)) : string.Join(' ', Args);
            public string Short => Text.Replace('\n', ' ') is var s && s.Length > 90 ? s[..90] + "…" : Text.Replace('\n', ' ');
        }

        sealed class Worker
        {
            public int Id;
            public Process Proc = null!;
            public NamedPipeServerStream Pipe = null!;
            public StreamWriter? W;
            public bool Ready, Blocked, Retiring;
            /// <summary>Plugins this worker held open as Mutagen overlays when its last command ended.</summary>
            public int OpenPlugins;
            public string? BlockedOn;
            public Job? Current;
            public int Served;
            public long MemMB;
            public string? KillReason;
            public readonly Queue<string> Tail = new();
            /// <summary>Set while its command waits for the heavy lane: the estimate it asked with.</summary>
            public int? HeavyAsk;
            /// <summary>Stopped so an agent's command could run: its indexer job is queued again, not counted as a crash.</summary>
            public bool Preempted;
            public bool LowPriority;
            /// <summary>Its garbage collector: server (workerGarbageCollector) or workstation.</summary>
            public bool ServerGc;
            /// <summary>Started for a job that asked for the workstation collector (J58): retired when that job is done.</summary>
            public bool ForWorkstationJob;
            /// <summary>When it last finished a command (or became ready): the idle workers above the setting go longest idle first.</summary>
            public DateTime IdleSince = DateTime.Now;
            /// <summary>Set when its command was stopped because the PC was short of memory: what Windows had available then (MB).</summary>
            public long? ShortOfMemoryMB;
        }

        // ---------------------------------------------------------------- Start
        public static void Run()
        {
            // Many callers wait on pipes at once: start with enough threads that none waits for the pool to grow
            ThreadPool.SetMinThreads(128, 128);
            _home = Home.Dir;
            _logDir = Path.Combine(_home, "Service", "logs");
            _exe = Environment.ProcessPath ?? "SFSearch.exe";
            Directory.CreateDirectory(_logDir);
            Directory.CreateDirectory(Path.GetDirectoryName(Wire.MarkerPath(_home))!); // an empty home: the marker file below lives there
            using var single = new Mutex(false, @"Local\sfsearch_service_" + Wire.Hash(_home));
            bool owner;
            try { owner = single.WaitOne(0); } catch (AbandonedMutexException) { owner = true; }
            if (!owner) return; // another service already runs for this home
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("CRASH (coordinator): " + e.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (_, e) => { Log("Unobserved: " + e.Exception.GetBaseException().Message); e.SetObserved(); };
            _jobObject = CreateKillOnCloseJob();
            CleanOldLogs();
            PrepareScratch();
            PrepareIndexes();
            File.WriteAllText(Wire.MarkerPath(_home), $"{Environment.ProcessId}|{DateTime.Now:o}|{_exe}");
            Log($"Service started: process {Environment.ProcessId}, {Workers} workers; the service itself on .NET's {(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")} garbage collector");
            if (Home.OldDataLine(_home) is { } oldData) Log(oldData);
            Log($"Scratch folder {_scratch}: {_scratchNote}. Workers together may hold {ServiceMaxMB} MB; a worker is stopped above {HardMB} MB.");
            Log($"Indexes folder {Path.Combine(_home, "indexes")}: {_indexesNote}.");
            Log(Cfg.MemoryLines()); // with the rewrite of an older sfsearch.json, when this start made it (fix S20)
            try
            {
                lock (Gate)
                {
                    QueueIndex("build");
                    Pump();
                }
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
            try { var m = Wire.MarkerPath(_home); if (File.Exists(m)) File.Delete(m); } catch (IOException) { }
            Log("Service stopped.");
        }

        // ---------------------------------------------------------------- The scratch folder
        internal const string ScratchMarker = ".sfsearch-scratch";

        /// <summary>
        /// Makes `scratch\` under the home and empties it. It holds working files that are only good while this service
        /// runs. A folder with files in it and no marker of ours wasn't made by SFSearch: it is left exactly as it is.
        /// </summary>
        static void PrepareScratch()
        {
            _scratch = ServiceWorker.ScratchFolder(_home);
            try
            {
                Directory.CreateDirectory(_scratch);
                string marker = Path.Combine(_scratch, ScratchMarker);
                var entries = Directory.GetFileSystemEntries(_scratch);
                if (entries.Length > 0 && !File.Exists(marker))
                {
                    _scratchNote = "it holds files SFSearch didn't put there, so it was NOT emptied (move them, and it will be at the next start)";
                    return;
                }
                int files = 0, left = 0;
                long bytes = 0;
                var inside = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
                foreach (var entry in entries)
                {
                    if (Path.GetFileName(entry).Equals(ScratchMarker, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if (Directory.Exists(entry))
                        {
                            foreach (var f in new DirectoryInfo(entry).EnumerateFiles("*", inside)) { files++; bytes += f.Length; }
                            Directory.Delete(entry, true);
                        }
                        else { bytes += new FileInfo(entry).Length; File.Delete(entry); files++; }
                    }
                    catch (IOException) { left++; }
                    catch (UnauthorizedAccessException) { left++; }
                }
                File.WriteAllText(marker, "SFSearch's scratch folder. The service empties it every time it starts: don't keep anything here.\n");
                _scratchNote = $"emptied at start ({files} file(s), {bytes / 1048576} MB removed{(left > 0 ? $"; {left} item(s) couldn't be removed" : "")})";
            }
            catch (IOException e) { _scratchNote = "couldn't be prepared: " + e.Message; }
            catch (UnauthorizedAccessException e) { _scratchNote = "couldn't be prepared: " + e.Message; }
        }

        /// <summary>
        /// Makes `indexes\` under the home (J31). With keepStartupIndexes on, what is in it is kept: the start's build maps
        /// it again and its full check deletes the parts no plugin uses any more. Off, `header\` and `links\` are emptied
        /// here, as the scratch folder is. A folder with files in it and no marker of ours wasn't made by SFSearch: nothing
        /// in it is deleted.
        /// </summary>
        static void PrepareIndexes()
        {
            string root = Path.Combine(_home, "indexes");
            try
            {
                Directory.CreateDirectory(root);
                string marker = Path.Combine(root, Home.IndexesMarker);
                var entries = Directory.GetFileSystemEntries(root);
                if (entries.Length > 0 && !File.Exists(marker))
                {
                    _indexesNote = "it holds files SFSearch didn't put there, so nothing in it was deleted";
                    return;
                }
                if (!File.Exists(marker)) File.WriteAllText(marker, Home.IndexesMarkerText);
                var (bytes, files) = FolderBytes(root);
                if (KeepIndexes)
                {
                    _indexesNote = $"kept from before ({files} file(s), {bytes / 1048576} MB; keepStartupIndexes): brought up to date plugin by plugin, then checked by a full hash";
                    return;
                }
                int left = 0;
                foreach (var part in new[] { "header", "links" })
                {
                    string dir = Path.Combine(root, part);
                    if (!Directory.Exists(dir)) continue;
                    try { Directory.Delete(dir, true); }
                    catch (IOException) { left++; }
                    catch (UnauthorizedAccessException) { left++; }
                }
                var (after, _) = FolderBytes(root);
                _indexesNote = $"emptied at start (keepStartupIndexes is false): {(bytes - after) / 1048576} MB removed{(left > 0 ? $"; {left} folder(s) couldn't be removed whole" : "")}";
            }
            catch (IOException e) { _indexesNote = "couldn't be prepared: " + e.Message; }
            catch (UnauthorizedAccessException e) { _indexesNote = "couldn't be prepared: " + e.Message; }
        }

        static (long Bytes, int Files) FolderBytes(string dir)
        {
            long bytes = 0; int files = 0;
            if (!Directory.Exists(dir)) return (0, 0);
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
            { bytes += f.Length; files++; }
            return (bytes, files);
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
                // The structured form asks for the fuller status of the new tools (sfs_status): today's lines, then the indexes and what each worker holds open
                case "status": return new Reply { Done = true, Out = Status(req.Structured) };
                case "stop": return new Reply { Done = true, Out = "Service stopped. Commands still run without it: each reads what it needs from the plugin files.\n" };
                case "cancel": return Cancel(req.Job, req.Key);
                case "more": return More(req);
                case "job":
                    {
                        if (req.Cursor != null) return More(req);
                        Job? j;
                        lock (Gate) _jobs.TryGetValue(req.Job ?? "", out j);
                        if (j == null) return Shape(null, new Reply { Done = true, Code = 2, Error = $"No job {req.Job} (results are kept for an hour)." }, req);
                        return Shape(j, await Wait(j, req.WaitMs), req);
                    }
                case "run":
                    {
                        if (req.Cursor != null) return More(req);
                        bool hasArgs = req.Args is { Length: > 0 };
                        if (!hasArgs && string.IsNullOrWhiteSpace(req.Tool)) return Shape(null, new Reply { Done = true, Code = 2, Error = "No command given." }, req);
                        Job job;
                        lock (Gate)
                        {
                            if (req.Key != null && _cancelledKeys.Remove(req.Key))
                                return Shape(null, new Reply { Done = true, Code = 4, Err = "Cancelled before it started.\n" }, req);
                            int queued = _lanes.Values.Sum(l => l.Count);
                            if (queued >= QueueLimit)
                            {
                                _rejected++;
                                return Shape(null, new Reply { Done = true, Code = 3, Error = $"The service is busy: {queued} commands are waiting. Try again shortly." }, req);
                            }
                            _lastAgent = DateTime.Now;
                            job = new Job
                            {
                                Id = $"j{Environment.ProcessId:x}-{Interlocked.Increment(ref _jobSeq)}",
                                Agent = string.IsNullOrWhiteSpace(req.Agent) ? "cli" : req.Agent!,
                                Args = req.Args ?? Array.Empty<string>(), Cwd = req.Cwd,
                                Tool = string.IsNullOrWhiteSpace(req.Tool) ? null : req.Tool, Params = req.Params, Key = req.Key,
                                // A caller that gives the estimate itself has said yes to a heavy run
                                AllowHeavy = req.AllowHeavy || req.HeavyMB > 0 || (req.Args?.Any(a => a.Equals("--allow-heavy", StringComparison.OrdinalIgnoreCase) || JobContext.IsSaveFlag(a)) ?? false)
                                    // The run tool's own command line (sfs_run: args) may carry the flag
                                    || (req.Params != null && req.Params.Any(kv => kv.Key.Equals("args", StringComparison.OrdinalIgnoreCase) && kv.Value.ValueKind == System.Text.Json.JsonValueKind.Array
                                        && kv.Value.EnumerateArray().Any(a => a.ValueKind == System.Text.Json.JsonValueKind.String && (string.Equals(a.GetString(), "--allow-heavy", StringComparison.OrdinalIgnoreCase) || JobContext.IsSaveFlag(a.GetString())))))
                                    // A tool's own argument (sfs_record: allow_heavy) says yes too
                                    || (req.Params != null && req.Params.Any(kv => kv.Key.Equals("allow_heavy", StringComparison.OrdinalIgnoreCase) && (kv.Value.ValueKind == System.Text.Json.JsonValueKind.True || kv.Value.ValueKind == System.Text.Json.JsonValueKind.String && (kv.Value.GetString() is "true" or "1" or "yes" || SFSearch.Records.HeavyAnswers.IsSave(kv.Value.GetString()))))),
                                HeavyMB = req.HeavyMB >= Cfg.HeavyMinMB ? req.HeavyMB : 0,
                            };
                            _jobs[job.Id] = job;
                            // Heavy by the caller's own estimate, with the lane free: memory is checked now, so it fails at
                            // once. With the lane taken it waits its turn, and memory is checked when its turn comes.
                            if (job.HeavyMB > 0 && _heavy == null && !HeavyFits(job.HeavyMB, out long avail)) RefuseHeavy(job, avail);
                            else
                            {
                                Enqueue(job, front: false);
                                Pump();
                            }
                        }
                        return Shape(job, await Wait(job, req.WaitMs), req);
                    }
                default: return Shape(null, new Reply { Done = true, Code = 2, Error = $"Unknown request '{req.Op}'." }, req);
            }
        }

        // ---------------------------------------------------------------- The structured form: parts and pages
        /// <summary>The reply as the caller asked for it: as it always was, or in parts (and one page of the answer).</summary>
        static Reply Shape(Job? job, Reply r, Request req) => req.WantsParts && r.Done ? Page(job, r, 0, req.PageChars) : r;

        /// <summary>The next page of a finished job's answer. A cursor is the job and the line to go on from.</summary>
        static Reply More(Request req)
        {
            string cursor = req.Cursor ?? "";
            int at = cursor.LastIndexOf('@');
            Job? j = null;
            int line = 0;
            lock (Gate)
                if (at > 0 && int.TryParse(cursor[(at + 1)..], out line) && line >= 0) _jobs.TryGetValue(cursor[..at], out j);
            if (j == null || !j.Done.Task.IsCompletedSuccessfully)
            {
                const string gone = "Error: that cursor is no longer kept (answers are kept for an hour, and not across a service restart). Fix: run the command again.";
                return new Reply { Done = true, Code = 2, Error = gone, Failure = new FailInfo { Message = gone, Fix = "run the command again." } };
            }
            return Page(j, j.Done.Task.Result, line, req.PageChars > 0 ? req.PageChars : DefaultPageChars);
        }

        static Reply Page(Job? job, Reply r, int start, int pageChars)
        {
            var s = new Reply { Done = true, Job = r.Job, Code = r.Code, Ms = r.Ms, Error = r.Error };
            var notes = r.Err.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToArray();
            s.Notes = notes.Length > 0 ? notes : null;
            s.Failure = job?.Failure ?? FailureOf(r);
            string text = r.Out;
            if (job == null) { s.Answer = text; return s; }
            s.Source = job.Source;
            s.Heavy = job.Heavy;
            s.Cost = new CostInfo { Ms = r.Ms, QueuedMs = job.QueuedMs, MemMB = job.MemMB, Read = job.Read };
            if (pageChars <= 0 && start == 0) { s.Answer = text; s.More = job.ToolMore; return s; }
            if (pageChars <= 0) pageChars = DefaultPageChars;
            int[] starts;
            int header;
            lock (job)
            {
                starts = job.LineStarts ??= LineStarts(text);
                if (job.HeaderLines < 0) job.HeaderLines = TableHeaderLines(text, starts);
                header = Math.Min(job.HeaderLines, starts.Length - 1);
            }
            int lines = starts.Length - 1;
            int first = Math.Min(Math.Max(start, header), lines);
            string head = header > 0 ? text[..starts[header]] : "";
            int end = first, len = head.Length;
            // Whole lines only, and at least one: a line longer than the page is a page by itself
            while (end < lines && (end == first || len + starts[end + 1] - starts[end] <= pageChars)) { len += starts[end + 1] - starts[end]; end++; }
            s.Answer = head + text[starts[first]..starts[end]];
            if (first > header || end < lines)
            {
                s.More = new MoreInfo { From = first - header + 1, Shown = end - first, Total = lines - header, Unit = header > 0 ? "rows" : "lines", Cursor = end < lines ? $"{job.Id}@{end}" : null };
                // One cursor for the caller (milestone M8). The tool itself left rows out too: this page is counted in the
                // tool's own rows, and when the service's pages end, the next cursor is the tool's. A caller passes
                // whichever cursor it was given back to the same tool and never has to tell the two kinds apart.
                if (job.ToolMore is { } tm)
                {
                    bool sameUnit = tm.Unit == s.More.Unit;
                    s.More = new MoreInfo
                    {
                        From = sameUnit ? tm.From + s.More.From - 1 : s.More.From, Shown = s.More.Shown, Total = sameUnit ? tm.Total : s.More.Total, Unit = s.More.Unit,
                        Cursor = end < lines ? s.More.Cursor : tm.Cursor,
                    };
                    if (!sameUnit && end >= lines && tm.Cursor != null)
                        s.Notes = (s.Notes ?? Array.Empty<string>()).Append($"That is the end of this page of the answer ({tm.Shown:N0} of {tm.Total:N0} {tm.Unit}). The cursor gives the next ones.").ToArray();
                }
            }
            else s.More = job.ToolMore;
            return s;
        }

        /// <summary>Where each line of the text starts, with the text's length last. No lines for an empty text.</summary>
        static int[] LineStarts(string text)
        {
            if (text.Length == 0) return new[] { 0 };
            var starts = new List<int> { 0 };
            for (int i = text.IndexOf('\n'); i >= 0 && i + 1 < text.Length; i = text.IndexOf('\n', i + 1)) starts.Add(i + 1);
            starts.Add(text.Length);
            return starts.ToArray();
        }

        /// <summary>2 when the whole answer is one Markdown table (a header line, a |---| line, then rows), else 0.</summary>
        static int TableHeaderLines(string text, int[] starts)
        {
            int lines = starts.Length - 1;
            if (lines < 3 || text[0] != '|') return 0;
            var rule = text.AsSpan(starts[1], starts[2] - starts[1]).Trim();
            if (rule.Length < 3 || rule[0] != '|' || rule.IndexOf('-') < 0 || rule.IndexOfAnyExcept("|-: ") >= 0) return 0;
            for (int i = 2; i < lines; i++)
                if (text[starts[i]] != '|') return 0;
            return 2;
        }

        /// <summary>The error of a reply, with its fix when the line has one ("Error: ... Fix: ...").</summary>
        static FailInfo? FailureOf(Reply r)
        {
            static FailInfo Parse(string line)
            {
                line = line.Trim();
                int f = line.IndexOf(" Fix: ", StringComparison.Ordinal);
                return new FailInfo { Message = line, Fix = f >= 0 ? line[(f + 6)..].Trim() : null };
            }
            if (r.Error != null) return Parse(r.Error);
            if (r.Code == 0) return null;
            foreach (var text in new[] { r.Out, r.Err })
                foreach (var line in text.Split('\n'))
                    if (line.TrimStart().StartsWith("Error:", StringComparison.Ordinal)) return Parse(line);
            return null;
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
                string state = job.Started == null ? (job.HeavyMB > 0 && _heavy != null ? "queued behind another heavy command" : "queued")
                    : job.On?.HeavyAsk != null ? "waiting for the heavy lane (one heavy command at a time)"
                    : job.On?.Blocked == true ? $"waiting for another command's first read of {job.On.BlockedOn}" : "running";
                return new Reply { Done = false, Job = job.Id, Ahead = Math.Max(0, ahead), State = state, Ms = (long)(DateTime.Now - job.Created).TotalMilliseconds };
            }
        }

        static Reply Cancel(string? id, string? key)
        {
            lock (Gate)
            {
                Job? j = null;
                if (id != null) _jobs.TryGetValue(id, out j);
                if (j == null && key != null)
                {
                    j = _jobs.Values.FirstOrDefault(x => x.Key == key);
                    // The call may not have reached the service yet: remember the key, and refuse it when it does
                    if (j == null) { _cancelledKeys[key] = DateTime.Now; return new Reply { Done = true, Out = "Cancelled.\n" }; }
                    id = j.Id;
                }
                if (j == null) return new Reply { Done = true, Code = 2, Error = $"No job {id}." };
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

        static bool AgentsWaiting() => _lanes.Any(kv => !IsBackground(kv.Key) && kv.Value.Count > 0);
        static bool IndexRunning() => _workers.Any(w => w.Current?.Agent == IndexLane);
        static bool AgentRunning() => _workers.Any(w => w.Current is { } j && !IsBackground(j.Agent));

        /// <summary>
        /// May this lane hand out a job now? Agents always. The indexer only when no agent is waiting, one job at a time.
        /// </summary>
        static bool LaneOpen(string name, bool agentsWaiting, bool indexRunning)
        {
            if (!IsBackground(name)) return true;
            if (agentsWaiting) return false;
            // The PC is short of memory and agents' commands run: the indexer waits until memory is back
            if (_short && AgentRunning()) return false;
            if (name == IndexLane) return !indexRunning;
            return !indexRunning && !(_lanes.TryGetValue(IndexLane, out var il) && il.Count > 0);
        }

        /// <summary>Jobs that could start now if a worker were free: not those waiting their turn for the heavy lane or behind agents.</summary>
        static int Startable()
        {
            bool agents = AgentsWaiting(), index = IndexRunning();
            int n = 0;
            foreach (var kv in _lanes)
                if (LaneOpen(kv.Key, agents, index))
                    n += kv.Key == IndexLane ? Math.Min(1, kv.Value.Count) : kv.Value.Count(j => j.HeavyMB == 0 || _heavy == null);
            return n;
        }

        static Job? TakeNext()
        {
            // Agents first: the indexer's lane only when every agent's line is empty
            bool agentsWaiting = AgentsWaiting(), indexRunning = IndexRunning();
            for (int k = 0; k < _laneOrder.Count; k++)
            {
                _rr = (_rr + 1) % _laneOrder.Count;
                var lane = _lanes[_laneOrder[_rr]];
                if (lane.Count == 0 || !LaneOpen(_laneOrder[_rr], agentsWaiting, indexRunning)) continue;
                for (var node = lane.First; node != null;)
                {
                    var next = node.Next;
                    var job = node.Value;
                    if (job.HeavyMB == 0) { lane.Remove(node); return job; }
                    // Heavy by the caller's estimate. One at a time: while the lane is taken it keeps its place and the
                    // agent's other commands go ahead. When its turn comes, Windows must have the memory, or it fails now.
                    if (_heavy == null)
                    {
                        lane.Remove(node);
                        if (HeavyFits(job.HeavyMB, out long avail)) { TakeHeavy(job); return job; }
                        RefuseHeavy(job, avail);
                    }
                    node = next;
                }
            }
            return null;
        }

        static long WorkersMB() => _workers.Where(w => !w.Retiring).Sum(w => w.MemMB);

        static void Pump()
        {
            if (Stopping.IsCancellationRequested) return;
            while (Startable() > 0)
            {
                bool busy = _workers.Any(w => w.Current != null);
                if (!MemoryOk() && busy) break;
                if (WorkersMB() > ServiceMaxMB)
                {
                    // The workers together are over the service's limit. Idle ones give their memory back first, biggest
                    // first. If the busy ones alone are over it, nothing new starts until one finishes.
                    foreach (var idle in _workers.Where(w => w.Ready && w.Current == null && !w.Retiring && w.MemMB > 256).OrderByDescending(w => w.MemMB).ToList())
                    {
                        if (WorkersMB() <= ServiceMaxMB) break;
                        Retire(idle, $"the workers together held more than the service's {ServiceMaxMB} MB");
                    }
                    if (WorkersMB() > ServiceMaxMB && busy) break;
                }
                // Never more commands at once than the setting (fix S21): workers above it exist only while others wait
                // for the heavy lane or another's build, and don't count while they wait
                if (_workers.Count(w => w.Current != null && !w.Blocked) >= Workers) break;
                // The worker idle the shortest time: the others stay idle, and those above the setting are retired
                var free = _workers.Where(w => w.Ready && w.Current == null && !w.Retiring).OrderByDescending(w => w.IdleSince).FirstOrDefault();
                if (free == null) break;
                var job = TakeNext();
                if (job == null) break;
                // A job that asked for the workstation garbage collector (J58: a big link walk) waits for a worker on it, which
                // is started for it; every other job takes a worker of the setting's collector first
                var kind = _workers.Where(w => w.Ready && w.Current == null && !w.Retiring && w.ServerGc == (!job.Workstation && Cfg.ServerGcWorkers))
                    .OrderByDescending(w => w.IdleSince).FirstOrDefault();
                if (job.Workstation && kind == null)
                {
                    if (!_workers.Any(w => !w.ServerGc && !w.Retiring)) // else one is starting, or busy
                    {
                        int before = _workers.Count;
                        Spawn(gc: false, forWorkstationJob: true);
                        if (_workers.Count > before) Log($"Worker {_workers[^1].Id} started on the workstation garbage collector for job {job.Id}: {job.Short}");
                        else job.Workstation = false; // none could be started: it runs where it can, and isn't moved again
                    }
                    if (job.Workstation) { Enqueue(job, front: true); break; }
                }
                Assign(kind ?? free, job);
            }
            int queued = Startable();
            int running = _workers.Count(w => w.Current != null && !w.Blocked);
            int spare = _workers.Count(w => w.Current == null && !w.Retiring);
            while ((queued > spare || spare < MinIdle) && running + spare < Workers && _workers.Count < MaxWorkersWithWaiting && MemoryOk()
                && (WorkersMB() <= ServiceMaxMB || !_workers.Any(w => w.Current != null)))
            {
                Spawn();
                spare++;
            }
            // An agent has to wait and no worker is free or on its way: the indexer's job gives way. It is asked first
            // (a job that saves its work and returns is queued again); the watchdog stops one that hasn't after 2 s.
            if ((spare == 0 || running >= Workers) && AgentsWaiting() && _lanes.Any(kv => !IsBackground(kv.Key) && kv.Value.Any(j => j.HeavyMB == 0 || _heavy == null)))
            {
                var idx = _workers.FirstOrDefault(w => w.Current is { } j && j.Agent == IndexLane && j.YieldAsked == null);
                if (idx != null)
                {
                    idx.Current!.YieldAsked = DateTime.Now;
                    Tell(idx, new WorkOrder { Kind = "yield", Job = idx.Current.Id });
                    Log($"job {idx.Current.Id} [{IndexLane}] asked to give way: an agent is waiting.");
                }
            }
        }


        /// <summary>One line to a worker. A worker that can't be reached is stopped: Gone() then deals with its command.</summary>
        static void Tell(Worker w, WorkOrder order)
        {
            try
            {
                if (w.W == null) throw new IOException("the worker isn't connected");
                w.W.WriteLine(Wire.Serialize(order));
                w.W.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                try { w.Proc.Kill(true); } catch (Exception) { } // like a crash: Gone() runs the command again on another worker
            }
        }

        static void Retire(Worker w, string why)
        {
            w.Retiring = true;
            Log($"Worker {w.Id} retired: {why} ({w.MemMB} MB, {w.Served} commands).");
            try { w.Pipe.Dispose(); } catch (Exception) { } // its loop ends and it exits
        }

        static void SetPriority(Worker w, bool low)
        {
            if (w.LowPriority == low) return;
            w.LowPriority = low;
            try { w.Proc.PriorityClass = low ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal; }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }

        static void Assign(Worker w, Job job)
        {
            job.On = w;
            job.Started = DateTime.Now;
            if (job.Attempts == 0) job.QueuedMs = (long)(DateTime.Now - job.Created).TotalMilliseconds;
            job.Attempts++;
            job.YieldAsked = null;
            w.Current = job;
            if (job.Agent == IndexLane) { _indexRan++; SetPriority(w, low: true); }
            Tell(w, new WorkOrder { Job = job.Id, Args = job.Args, Cwd = job.Cwd, Tool = job.Tool, Params = job.Params, AllowHeavy = job.AllowHeavy, HeavyMB = job.HeavyHeld ? job.HeavyMB : 0, LimitMB = HardFor(job) });
        }

        // ---------------------------------------------------------------- The heavy lane (call with Gate held)
        /// <summary>What Windows must have available for a job of this estimate: the estimate plus the spare.</summary>
        static long HeavyNeed(int estimateMB) => estimateMB + (long)Cfg.HeavySpareMB;

        /// <summary>True when Windows has the memory now. Uses "available" (free plus standby), the figure Task Manager shows.</summary>
        static bool HeavyFits(int estimateMB, out long availableMB)
        {
            availableMB = MemoryInfo.AvailableMB();
            return availableMB < 0 || availableMB >= HeavyNeed(estimateMB);
        }

        static void TakeHeavy(Job job)
        {
            _heavy = job;
            job.HeavyHeld = true;
            job.HeavySince = DateTime.Now;
            _heavyRan++;
            Log($"job {job.Id} [{job.Agent}] has the heavy lane: about {job.HeavyMB} MB, {MemoryInfo.AvailableMB()} MB available.");
        }

        /// <summary>A job that is heavy by its caller's estimate, and Windows doesn't have the memory: it fails now, with one line.</summary>
        static void RefuseHeavy(Job job, long availableMB)
        {
            _heavyNoMemory++;
            string line = JobContext.NoMemoryLine(HeavyNeed(job.HeavyMB), availableMB);
            job.Heavy = new HeavyInfo { EstimateMB = job.HeavyMB, Reason = "no_memory", AvailableMB = availableMB };
            job.Failure = new FailInfo { Message = line, Fix = "try again later, or close other programs." };
            Log($"job {job.Id} [{job.Agent}] refused: about {job.HeavyMB} MB plus {Cfg.HeavySpareMB} MB spare, {availableMB} MB available: {job.Short}");
            Finish(job, new Reply { Done = true, Job = job.Id, Code = JobContext.NoMemoryCode, Out = line + "\n" });
        }

        /// <summary>A running command says its next part is heavy. Answer grant or deny, or keep it waiting its turn.</summary>
        static void AskHeavy(Worker w, Job job, int estimateMB, string? what)
        {
            if (job.HeavyHeld)
            {
                // It has the lane already. A bigger estimate than it was admitted with needs the memory checked again.
                if (estimateMB <= job.HeavyMB || HeavyFits(estimateMB, out _)) { job.HeavyMB = Math.Max(job.HeavyMB, estimateMB); Tell(w, new WorkOrder { Kind = "grant", Job = job.Id, HeavyMB = estimateMB, LimitMB = HardFor(job) }); }
                else DenyHeavy(w, job, estimateMB, "no_memory");
                return;
            }
            if (!job.AllowHeavy && Cfg.AskBeforeHeavy) { DenyHeavy(w, job, estimateMB, "not_allowed"); return; }
            if (_heavy != null)
            {
                w.Blocked = true;
                w.BlockedOn = $"the heavy lane (job {_heavy.Id} has it)";
                w.HeavyAsk = estimateMB;
                _heavyWaiters.Add(w);
                return;
            }
            DecideHeavy(w, job, estimateMB);
        }

        static void DecideHeavy(Worker w, Job job, int estimateMB)
        {
            w.HeavyAsk = null; w.Blocked = false; w.BlockedOn = null;
            if (!HeavyFits(estimateMB, out _)) { DenyHeavy(w, job, estimateMB, "no_memory"); return; }
            job.HeavyMB = estimateMB;
            TakeHeavy(job);
            Tell(w, new WorkOrder { Kind = "grant", Job = job.Id, HeavyMB = estimateMB, LimitMB = HardFor(job) });
        }

        static void DenyHeavy(Worker w, Job job, int estimateMB, string reason)
        {
            long avail = MemoryInfo.AvailableMB();
            if (reason == "not_allowed") _heavyNotAllowed++; else _heavyNoMemory++;
            Log($"job {job.Id} [{job.Agent}] heavy part not run ({reason}): about {estimateMB} MB, {avail} MB available: {job.Short}");
            Tell(w, new WorkOrder { Kind = "deny", Job = job.Id, HeavyMB = estimateMB, Reason = reason, AvailableMB = avail, NeedMB = HeavyNeed(estimateMB) });
        }

        /// <summary>The job is over (finished, stopped, or about to run again): free the lane and let the next one in.</summary>
        static void ReleaseHeavy(Job job)
        {
            if (!job.HeavyHeld) return;
            job.HeavyHeld = false;
            if (_heavy != job) return;
            _heavy = null;
            while (_heavy == null && _heavyWaiters.Count > 0)
            {
                var w = _heavyWaiters[0];
                _heavyWaiters.RemoveAt(0);
                if (w.Current != null && w.HeavyAsk is { } est && _workers.Contains(w)) DecideHeavy(w, w.Current, est);
            }
        }

        static void Spawn(bool? gc = null, bool forWorkstationJob = false)
        {
            var w = new Worker { Id = ++_workerSeq, ForWorkstationJob = forWorkstationJob };
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
            psi.Environment["SFSEARCH_SCRATCH"] = _scratch;
            // The worker's garbage collector (workerGarbageCollector, J44): server with DATAS (its heaps grow and shrink
            // with the work), or workstation. Set either way, so a DOTNET_ variable of the PC's can't pick it instead.
            w.ServerGc = gc ?? Cfg.ServerGcWorkers;
            bool serverGc = w.ServerGc;
            psi.Environment["DOTNET_gcServer"] = serverGc ? "1" : "0";
            if (serverGc) psi.Environment["DOTNET_GCDynamicAdaptationMode"] = "1"; else psi.Environment.Remove("DOTNET_GCDynamicAdaptationMode");
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
                case "ready": w.Ready = true; w.IdleSince = DateTime.Now; break;
                case "blocked": w.Blocked = true; w.BlockedOn = e.On; break;
                case "unblocked": w.Blocked = false; w.BlockedOn = null; break;
                case "heavy":
                    if (w.Current is { } asking && asking.Id == e.Job) AskHeavy(w, asking, e.EstimateMB, e.What);
                    break;
                case "done":
                    {
                        var job = w.Current;
                        w.Current = null; w.Blocked = false; w.Served++; w.IdleSince = DateTime.Now; w.MemMB = e.MemMB; w.OpenPlugins = e.OpenPlugins;
                        w.HeavyAsk = null; _heavyWaiters.Remove(w);
                        SetPriority(w, low: false);
                        if (job != null && job.Id == e.Job && e.Yielded && job.Agent == IndexLane && !job.Cancelled)
                        {
                            // An indexer job that stopped early for an agent: back to the front of its lane, to go on later
                            ReleaseHeavy(job);
                            job.Started = null; job.On = null; job.YieldAsked = null; job.GaveWay++; job.Attempts = Math.Max(0, job.Attempts - 1);
                            _indexGaveWay++;
                            Enqueue(job, front: true);
                            Log($"job {job.Id} [{job.Agent}] gave way after {e.Ms} ms; queued again: {job.Short}");
                        }
                        else if (job != null && job.Id == e.Job && e.Workstation && w.ServerGc && !job.Workstation && !job.Cancelled)
                        {
                            // It asked for the workstation garbage collector (J58: a big link walk) and did nothing here:
                            // queued again at the front, for a worker on that collector. Once: there it runs.
                            job.Workstation = true;
                            ReleaseHeavy(job);
                            job.Started = null; job.On = null; job.YieldAsked = null; job.Attempts = Math.Max(0, job.Attempts - 1);
                            Enqueue(job, front: true);
                            Log($"job {job.Id} [{job.Agent}] asked for a worker on the workstation garbage collector after {e.Ms} ms; queued again: {job.Short}");
                        }
                        else if (job != null && job.Id == e.Job)
                        {
                            job.Source = e.Source; job.Read = e.Read; job.Failure = e.Failure; job.Heavy = e.Heavy; job.MemMB = e.MemMB; job.ToolMore = e.More;
                            if (e.HeaderLines > 0) job.HeaderLines = e.HeaderLines;
                            string err = e.Err ?? "";
                            if (Sf1EditOpen()) err += "[session] SF1Edit is open. Plugins it saves are read again on the next command; tell the user if a result looks out of date.\n";
                            Finish(job, new Reply { Done = true, Job = job.Id, Out = e.Out ?? "", Err = err, Code = e.Code, Ms = e.Ms });
                            _served++;
                            if (e.Code != 0) _failed++;
                            Log($"job {job.Id} [{job.Agent}] w{w.Id} {e.Ms} ms exit {e.Code} mem {e.MemMB} MB: {job.Short}");
                            // The caller answered "save_index" (J33): the index is made in the indexer lane now that the answer is given
                            if (e.IndexAfter == "world")
                            {
                                if (QueueWorldIndex()) Log($"Indexer: the world link index asked for by job {job.Id} (save_index) is queued.");
                            }
                            if (job.Agent == IndexLane && job.Args.Length > 0 && job.Args[0] == "__index")
                            {
                                _indexState = (e.Out ?? "").Split((char)10)[0].Trim();
                                _indexLast = DateTime.Now;
                                Log("Indexer: " + _indexState);
                                // The look for changes2's history, when it was asked for (fix S11)
                                if ((e.Out ?? "").Split((char)10).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("history:")) is { } look)
                                {
                                    _historyState = look["history:".Length..].Trim();
                                    Log("History: " + _historyState);
                                }
                                // The header index is up to date: now the link index, for every plugin that has no link file yet
                                if (e.Code == 0) QueueLinks();
                                // Kept indexes: after the start's build and its link job, one full check of every plugin (J31)
                                if (e.Code == 0 && KeepIndexes && !_startCheckQueued && job.Args.Length > 1 && job.Args[1] == "build")
                                {
                                    _startCheckQueued = true;
                                    // No history look: the build's look at start is the start's (changes2 dates a change it finds by the next look)
                                    QueueIndex("sweep", history: false, "--unused-before", StartedUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
                                }
                                // After every look of the header index: the material paths materials2 tells from what Data
                                // names (fix S33h), gathered again when an archive, a loose .mat file or a plugin changed
                                // (S33g: the plugins' records are read too); near-instant when the scratch folder already
                                // holds them for the files as they are. One at a time.
                                if (e.Code == 0 && !MaterialPathsPending()) QueueIndexerJob("__materialpaths");
                            }
                            else if (job.Agent == IndexLane && job.Args.Length > 0 && job.Args[0] == "__materialpaths")
                                Log("Indexer: " + (e.Out ?? "").Split((char)10)[0].Trim());
                            else if (job.Agent == IndexLane && job.Args.Length > 0 && job.Args[0] == "__links")
                            {
                                _linksState = (e.Out ?? "").Split((char)10)[0].Trim();
                                Log("Indexer: " + _linksState);
                                // The world link index the user asked to keep, when the job made some of it (J32)
                                foreach (var line in (e.Out ?? "").Split((char)10).Select(l => l.Trim()).Where(l => l.StartsWith("world link index:", StringComparison.Ordinal)))
                                    Log("Indexer: " + line);
                            }
                        }
                        if (w.ForWorkstationJob && job != null && job.Workstation && job.Finished != null && Cfg.ServerGcWorkers)
                            Retire(w, $"started on the workstation garbage collector for job {job.Id}, which is done");
                        else if (w.MemMB > RecycleMB || w.Served >= RecycleJobs)
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

        /// <summary>Queues the header index's job in the indexer lane (call with Gate held): "build" at start, "sweep" while idle and after the start's build.</summary>
        static void QueueIndex(string what, bool history = true, params string[] more)
        {
            var args = new List<string> { "__index", what };
            args.AddRange(more);
            if (history && HistoryOn) args.Add("--history");
            var job = new Job { Id = $"j{Environment.ProcessId:x}-{Interlocked.Increment(ref _jobSeq)}", Agent = IndexLane, Args = args.ToArray(), Cwd = _home };
            _jobs[job.Id] = job;
            _indexLast = DateTime.Now;
            Enqueue(job, front: false);
        }

        /// <summary>A material-path job is queued or running (call with Gate held): another isn't queued beside it.</summary>
        static bool MaterialPathsPending() => _jobs.Values.Any(j => j.Finished == null && j.Agent == IndexLane && j.Args.Length > 0 && j.Args[0] == "__materialpaths");

        /// <summary>Queues a one-off job in the indexer lane (call with Gate held).</summary>
        static void QueueIndexerJob(params string[] args)
        {
            var job = new Job { Id = $"j{Environment.ProcessId:x}-{Interlocked.Increment(ref _jobSeq)}", Agent = IndexLane, Args = args, Cwd = _home };
            _jobs[job.Id] = job;
            Enqueue(job, front: false);
        }

        /// <summary>
        /// Queues the link index's job in the indexer lane (call with Gate held), unless one is waiting or running. It
        /// walks the plugins that have no link file yet, gives way when an agent has to wait, and is queued again then.
        /// </summary>
        static void QueueLinks()
        {
            static bool IsLinks(Job j) => j.Args.Length > 0 && j.Args[0] == "__links";
            if (_lanes.TryGetValue(IndexLane, out var lane) && lane.Any(IsLinks)) return;
            if (_workers.Any(w => w.Current is { } j && j.Agent == IndexLane && IsLinks(j))) return;
            var job = new Job { Id = $"j{Environment.ProcessId:x}-{Interlocked.Increment(ref _jobSeq)}", Agent = IndexLane, Args = new[] { "__links", "build" }, Cwd = _home };
            _jobs[job.Id] = job;
            Enqueue(job, front: false);
        }

        /// <summary>
        /// Queues `__links world` in the indexer lane (call with Gate held): it makes the world link files of the plugins
        /// the user asked for (WorldLinks.Want) that have none. Not when one is already waiting: it reads what is asked
        /// for when it starts. False when none was queued.
        /// </summary>
        static bool QueueWorldIndex()
        {
            static bool IsWorld(Job j) => j.Args.Length > 1 && j.Args[0] == "__links" && j.Args[1] == "world";
            if (_lanes.TryGetValue(IndexLane, out var lane) && lane.Any(IsWorld)) return false;
            QueueIndexerJob("__links", "world");
            return true;
        }

        /// <summary>While nothing is asked: check the header index again every pluginCheckSeconds (10 minutes by default), and hash every file again once an hour (call with Gate held).</summary>
        static void IndexSweep()
        {
            if (DateTime.Now - _indexLast < IndexCheckEvery || DateTime.Now - _lastAgent < IndexQuiet) return;
            if (_lanes.Values.Any(l => l.Count > 0) || _workers.Any(w => w.Current != null)) return;
            bool sweep = DateTime.Now - _indexLastSweep >= IndexSweepEvery;
            if (sweep) _indexLastSweep = DateTime.Now;
            QueueIndex(sweep ? "sweep" : "check");
        }

        static void Gone(Worker w)
        {
            _workers.Remove(w);
            _heavyWaiters.Remove(w);
            w.HeavyAsk = null;
            try { w.Pipe.Dispose(); } catch (Exception) { }
            var job = w.Current;
            w.Current = null;
            if (job != null && !job.Done.Task.IsCompleted)
            {
                string tail;
                lock (w.Tail) tail = string.Join("\n", w.Tail.TakeLast(12));
                int exit = -1;
                try { exit = w.Proc.HasExited ? w.Proc.ExitCode : -1; } catch (Exception) { }
                if (w.Preempted && !job.Cancelled)
                {
                    // An indexer job stopped so an agent's command could run: it goes back to the front of its lane
                    ReleaseHeavy(job);
                    job.Started = null; job.On = null; job.YieldAsked = null; job.GaveWay++; job.Attempts = Math.Max(0, job.Attempts - 1);
                    _indexGaveWay++;
                    Enqueue(job, front: true);
                    Log($"job {job.Id} [{job.Agent}] stopped to let an agent through; queued again: {job.Short}");
                }
                else if (w.ShortOfMemoryMB is { } availMB && !job.Cancelled)
                {
                    // Stopped because the PC as a whole ran short: one clear line, the memory exit code, and it can be asked again
                    _killed++;
                    string line = ShortOfMemoryLine(availMB);
                    job.Failure = new FailInfo { Message = line, Fix = "ask again; close other programs if it happens again." };
                    Log($"job {job.Id} [{job.Agent}] stopped: the PC was short of memory ({availMB} MB available, the floor {FloorMB} MB): {job.Short}");
                    Finish(job, new Reply { Done = true, Job = job.Id, Code = JobContext.NoMemoryCode, Err = line + "\n" });
                }
                else if (w.KillReason != null)
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
                        ReleaseHeavy(job); // it asks for the lane again on its second run
                        job.Started = null; job.On = null;
                        Enqueue(job, front: true);
                    }
                    else
                    {
                        Finish(job, new Reply { Done = true, Job = job.Id, Code = 6, Err = $"Error: SFSearch crashed on this command twice (exit code {exit}). Last output:\n{tail}\n" });
                    }
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
            ReleaseHeavy(job);
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
            long tick = 0;
            while (!Stopping.IsCancellationRequested)
            {
                // Memory four times a second; time limits, clean-up and the indexer's idle checks once a second
                try { await Task.Delay(250, Stopping.Token); } catch (OperationCanceledException) { break; }
                bool second = ++tick % 4 == 0;
                try
                {
                    lock (Gate)
                    {
                        foreach (var w in _workers.ToList())
                        {
                            if (w.Current != null)
                            {
                                long mb = MemoryInfo.PrivateMB(w.Proc);
                                if (mb < 0 || w.KillReason != null) continue;
                                w.MemMB = mb;
                                long hard = HardFor(w.Current);
                                if (mb > hard)
                                    Kill(w, w.Current.HeavyHeld ? $"it used more than {MemoryInfo.GB(hard)} GB of memory (its estimate was {MemoryInfo.GB(w.Current.HeavyMB)} GB)"
                                        : $"it used more than {MemoryInfo.GB(hard)} GB of memory, this PC's limit for one command (workerMaxMB). Fix: ask for less at once (a narrower question, or the new tools), or raise workerMaxMB in sfsearch.json if this PC has the memory to spare");
                            }
                            else if (w.Ready && !w.Retiring && second && MemoryInfo.PrivateMB(w.Proc) is var idle and >= 0)
                                w.MemMB = idle; // idle workers count toward the service's total too
                        }
                        Guard();
                        if (!second) { Pump(); continue; }
                        foreach (var w in _workers.ToList())
                        {
                            // A heavy command has the heavy lane's own time limit, counted from when it got the lane
                            var limit = w.Current is { HeavyHeld: true } ? HeavyTimeout : JobTimeout;
                            if (w.Current is { } job && (job.HeavyHeld ? job.HeavySince ?? job.Started : job.Started) is { } st && DateTime.Now - st > limit)
                                Kill(w, $"it ran longer than the {limit.TotalMinutes:0}-minute limit");
                        }
                        // An indexer job that didn't give way by itself: stopped 2 s after it was asked (GiveWay.cs)
                        var busy = _workers.Where(w => w.Current != null).ToList();
                        foreach (int i in GiveWay.ToStop(busy.Select(w => new GiveWay.Running(w.Current!.Agent, w.Current.YieldAsked, w.Current.Cancelled || w.KillReason != null)).ToList(), DateTime.Now, AgentsWaiting()))
                        {
                            busy[i].Preempted = true;
                            Kill(busy[i], "an agent was waiting");
                        }
                        foreach (var k in _cancelledKeys.Where(kv => DateTime.Now - kv.Value > TimeSpan.FromMinutes(1)).Select(kv => kv.Key).ToList())
                            _cancelledKeys.Remove(k);
                        foreach (var id in _jobs.Where(kv => kv.Value.Finished is { } f && DateTime.Now - f > TimeSpan.FromHours(1)).Select(kv => kv.Key).ToList())
                            _jobs.Remove(id);
                        foreach (var lane in _lanes.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList())
                            if (!_jobs.Values.Any(j => j.Agent == lane && !j.Done.Task.IsCompleted)) { _lanes.Remove(lane); _laneOrder.Remove(lane); }
                        if (_rr >= _laneOrder.Count) _rr = 0;
                        RetireExtraIdle();
                        Pump();
                    }
                    if (DateTime.Now - lastGc > TimeSpan.FromSeconds(60)) { lastGc = DateTime.Now; CleanFiles(); }
                    lock (Gate) { IndexSweep(); Pump(); }
                }
                catch (Exception e) { Log("Watchdog: " + e); }
            }
        }

        /// <summary>
        /// Workers above the setting that have been idle <see cref="ExtraIdle"/> are retired, longest idle first, down to
        /// the setting (call with Gate held; 2026-11-13, fix S21). Not while a worker waits for the heavy lane or another's
        /// build: the extra workers are there for that. Before, they stayed for the rest of the run (a service set to 4
        /// kept 7), each holding 14 to 300 MB, and took commands above the setting.
        /// </summary>
        static void RetireExtraIdle()
        {
            if (_workers.Any(w => w.Blocked) || _heavyWaiters.Count > 0) return;
            int live = _workers.Count(w => !w.Retiring);
            if (live <= Workers) return;
            foreach (var w in _workers.Where(w => w.Ready && w.Current == null && !w.Retiring && DateTime.Now - w.IdleSince >= ExtraIdle).OrderBy(w => w.IdleSince).ToList())
            {
                if (live <= Workers) break;
                _extraRetired++;
                Retire(w, $"idle for {(DateTime.Now - w.IdleSince).TotalSeconds:0} s, one more than the {Workers} workers set (started while others waited)");
                live--;
            }
        }

        /// <summary>Answers the MCP adapter saved to files, and --stdin files: deleted after a week.</summary>
        static void CleanFiles()
        {
            foreach (var dir in new[] { Path.Combine(_home, "Service", "results"), Path.Combine(_home, "Service", "tmp") })
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir))
                        if (DateTime.Now - File.GetLastWriteTime(f) > TimeSpan.FromDays(7)) TryDelete(f);
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

        // ---------------------------------------------------------------- Status, log, memory
        static string Status(bool full = false)
        {
            var sb = new StringBuilder();
            lock (Gate)
            {
                sb.AppendLine($"Service running since {Started:yyyy-MM-dd HH:mm} (process {Environment.ProcessId}). Answered {_served} command(s): {_failed} with errors, {_crashed} worker crash(es), {_retried} retried, {_killed} stopped (time, memory or cancel), {_rejected} turned away (busy).");
                sb.AppendLine($"Workers: {_workers.Count} (up to {Workers} running at once; {MinIdle} kept ready{(_extraRetired > 0 ? $"; {_extraRetired} started while others waited retired once idle" : "")}). Each command may take {JobTimeout.TotalMinutes:0} min; a worker is replaced after {RecycleJobs} commands or {RecycleMB / 1024.0:0.#} GB.");
                foreach (var w in _workers.OrderBy(w => w.Id))
                {
                    string state = !w.Ready ? "starting" : w.Current == null ? "idle" : w.Blocked ? $"waiting for {w.BlockedOn}" : $"running {(DateTime.Now - w.Current.Started!.Value).TotalSeconds:0} s";
                    sb.AppendLine($"  worker {w.Id} (process {w.Proc.Id}): {state}, {w.Served} done, {w.MemMB} MB{(w.Current != null ? $" — [{w.Current.Agent}] {w.Current.Short}" : "")}");
                }
                int queued = _lanes.Values.Sum(l => l.Count);
                sb.AppendLine($"Queue: {queued} waiting{(queued > 0 ? ": " + string.Join(", ", _lanes.Where(kv => kv.Value.Count > 0).Select(kv => $"{kv.Key} {kv.Value.Count}")) : "")}.");
                int heavyQueued = _heavyWaiters.Count + _lanes.Values.Sum(l => l.Count(j => j.HeavyMB > 0));
                sb.AppendLine($"Heavy lane: {(_heavy == null ? "free" : $"job {_heavy.Id} [{_heavy.Agent}], about {MemoryInfo.GB(_heavy.HeavyMB)} GB, {(DateTime.Now - (_heavy.HeavySince ?? DateTime.Now)).TotalSeconds:0} s so far")}; {heavyQueued} waiting their turn. "
                    + $"Since start: {_heavyRan} run, {_heavyNoMemory} refused (memory short), {_heavyNotAllowed} not run (the caller hadn't allowed a heavy run). A heavy command may take {HeavyTimeout.TotalMinutes:0} min.");
                var idx = _workers.FirstOrDefault(w => w.Current?.Agent == IndexLane);
                int idxQueued = _lanes.TryGetValue(IndexLane, out var il) ? il.Count : 0;
                sb.AppendLine($"Indexer: {(idx != null ? $"running on worker {idx.Id}" : "idle")}; {idxQueued} waiting. Since start: {_indexRan} run(s), gave way to an agent {_indexGaveWay} time(s).");
                sb.AppendLine($"History (changes2): {(!HistoryOn ? "no look by the service (historyLook is off in sfsearch.json): only changes2 takes one" : $"looked at with the header index, at the start and {ServiceConfig.EveryText(Cfg.PluginCheckSeconds)} while idle (pluginCheckSeconds); the last: {_historyState}")}");
                long total = MemoryInfo.TotalMB(), together = WorkersMB();
                long avail = MemoryInfo.AvailableMB();
                sb.AppendLine($"Memory: the workers together hold {together} MB of the {ServiceMaxMB} MB the service may use{(together > ServiceMaxMB ? " (over: no new command starts until some is given back)" : "")}. "
                    + $"A worker is stopped above {HardMB / 1024.0:0.#} GB. Windows has {avail} MB available of {total} MB{(avail >= 0 && avail < FloorMB ? $", under the floor of {FloorMB} MB" : _short ? ", just above the floor: new commands wait" : "")}. "
                    + $"Since start: the PC ran short {_shortEpisodes} time(s){(_shortEpisodes > 0 ? $" (lowest {_lowestAvailMB} MB available)" : "")}, the indexer gave way {_shortPauses} time(s) for it, {_shortStops} command(s) stopped for it.");
                foreach (var line in Cfg.MemoryLines().Split('\n')) sb.AppendLine(line);
            }
            sb.AppendLine($"Scratch folder: {_scratch}, {_scratchNote}.");
            if (full)
            {
                lock (Gate)
                {
                    sb.AppendLine($"Header index job: {_indexState}");
                    sb.AppendLine($"Link index job: {_linksState}");
                    sb.AppendLine($"Open plugins (the game's own plugins a worker has read stay open; a mod's or a patch's file is let go when its command ends), by worker: {(_workers.Count == 0 ? "no worker" : string.Join(", ", _workers.OrderBy(w => w.Id).Select(w => $"worker {w.Id}: {w.OpenPlugins}")))}.");
                }
            }
            sb.AppendLine($"Log: {LogFile}");
            if (Home.OldDataLine(_home) is { } oldData) sb.AppendLine(oldData);
            string dll = Path.Combine(_home, "bin", "Release", "net10.0", "SFSearch.dll");
            var mine = File.GetLastWriteTime(Path.Combine(AppContext.BaseDirectory, "SFSearch.dll"));
            if (File.Exists(dll) && File.GetLastWriteTime(dll) > mine.AddSeconds(5))
                sb.AppendLine("SFSearch has been rebuilt since this service started: 'Service restart' to use the new build.");
            if (Extensions.Summary() is string addOns) sb.AppendLine(addOns);
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

        /// <summary>False when the PC is short of memory: no new work starts (while other work runs) until some frees up.</summary>
        static bool MemoryOk()
        {
            long available = MemoryInfo.AvailableMB();
            return available < 0 || available > Cfg.HoldNewWorkBelowMB;
        }

        /// <summary>
        /// What a command may hold before it is stopped (MB): one command's limit; a heavy command its estimate and half
        /// again (the spare at most); the indexer's jobs at least 3 GB (the header index peaks at 1.9 GB, the link index at
        /// 2.3 GB), so a small PC's lower limit doesn't stop them.
        /// </summary>
        static long HardFor(Job job) =>
            job.HeavyHeld ? Math.Max(HardMB, job.HeavyMB + Math.Min(job.HeavyMB / 2, (long)Cfg.HeavySpareMB))
            : job.Agent == IndexLane ? Math.Max(HardMB, ServiceConfig.IndexAllowanceMB)
            : HardMB;

        /// <summary>The one line a command fails with when it was stopped because the PC as a whole ran short of memory.</summary>
        static string ShortOfMemoryLine(long availableMB) =>
            $"Error: the PC was short of memory ({MemoryInfo.GB(Math.Max(0, availableMB))} GB free), so this command was stopped. Fix: ask again; close other programs if it happens again.";

        /// <summary>
        /// The memory guard (call with Gate held, four times a second). Under the floor while agents' commands run: first
        /// the indexer's jobs give way (asked, then stopped after a second; they are queued again), then the
        /// biggest ordinary command is stopped. Heavy commands are judged against their own allowance, not here. One stop
        /// at a time: the next waits until that worker is gone, and until memory came back or 5 s have passed still short.
        /// </summary>
        static void Guard()
        {
            long avail = MemoryInfo.AvailableMB();
            if (avail < 0) return;
            long floor = FloorMB;
            bool agents = AgentRunning();
            if (avail >= floor)
            {
                // Back above the floor: a later shortage may stop a command at once. Background lanes open again with some room.
                if (_lastStopped == null || !_workers.Contains(_lastStopped)) { _nextStopAllowed = DateTime.MinValue; _lastStopped = null; }
                if (_short && avail >= Cfg.HoldNewWorkBelowMB) { _short = false; Log($"Memory: back to {avail} MB available (the floor is {floor} MB)."); }
                return;
            }
            if (!_short) { _short = true; _shortSince = DateTime.Now; _shortEpisodes++; Log($"Memory: {avail} MB available, under the floor of {floor} MB{(agents ? "" : "; only the indexer runs, so nothing is stopped")}."); }
            _lowestAvailMB = Math.Min(_lowestAvailMB, avail);
            if (!agents) return; // only background work runs: the indexer has its own allowance; nothing is stopped for it alone

            // 1. The indexer gives way first
            bool background = false;
            foreach (var w in _workers.Where(w => w.Current is { } j && IsBackground(j.Agent) && w.KillReason == null))
            {
                background = true;
                var job = w.Current!;
                if (job.YieldAsked == null)
                {
                    job.YieldAsked = DateTime.Now;
                    _shortPauses++;
                    Tell(w, new WorkOrder { Kind = "yield", Job = job.Id });
                    Log($"job {job.Id} [{job.Agent}] asked to give way: the PC is short of memory ({avail} MB available).");
                }
                else if (DateTime.Now - job.YieldAsked.Value > TimeSpan.FromSeconds(1))
                {
                    w.Preempted = true;
                    Kill(w, "the PC was short of memory");
                }
                _bgPausedAt = DateTime.Now;
            }
            if (background) return;
            // Give Windows a moment to count what the indexer gave back before judging again
            if (DateTime.Now - _bgPausedAt < TimeSpan.FromSeconds(1)) return;

            // 2. One stop at a time
            if (_lastStopped != null && _workers.Contains(_lastStopped)) return;
            if (DateTime.Now < _nextStopAllowed) return;

            // 3. The biggest ordinary command. Not one holding the heavy lane (its own allowance), not one already ending.
            var biggest = _workers
                .Where(w => w.Current is { } j && !IsBackground(j.Agent) && !j.HeavyHeld && !j.Cancelled && w.KillReason == null && w.MemMB >= ShortStopMinMB)
                .OrderByDescending(w => w.MemMB).FirstOrDefault();
            if (biggest == null) return;
            _shortStops++;
            biggest.ShortOfMemoryMB = avail;
            Log($"Memory: {avail} MB available, under the floor of {floor} MB: stopping job {biggest.Current!.Id} [{biggest.Current.Agent}], the biggest command ({biggest.MemMB} MB).");
            Kill(biggest, "the PC was short of memory");
            _lastStopped = biggest;
            _nextStopAllowed = DateTime.Now.AddSeconds(5);
        }

        // ---------------------------------------------------------------- Windows: workers die with the service
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
