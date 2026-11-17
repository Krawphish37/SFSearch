using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SFSearch.Service
{
    /// <summary>
    /// What a command can tell the service about its own answer, and ask of it (2026-11-11, milestone M4): where the data
    /// came from, what had to be read, a table's header, an error with its fix, the heavy lane, the scratch folder, and
    /// giving way in the indexer lane. SFSearch's verbs and add-ons use it alike (AddOnApi.cs lists it for add-ons).
    /// Outside a service worker (a plain command-line run) the calls still work: notes are kept but go nowhere, and a
    /// heavy command checks the PC's memory itself.
    /// </summary>
    public static class JobContext
    {
        /// <summary>Exit code of a heavy command the caller didn't allow.</summary>
        public const int NotAllowedCode = 7;
        /// <summary>Exit code of a heavy command refused because memory was short.</summary>
        public const int NoMemoryCode = 8;

        static readonly List<SourceInfo> _source = new();
        static readonly List<string> _read = new();
        static int _headerLines;
        static FailInfo? _failure;
        static HeavyInfo? _heavy;
        static MoreInfo? _more;
        static int _grantedMB;
        static volatile bool _yield;

        /// <summary>True when the command line had --allow-heavy (a plain run, no service).</summary>
        public static bool AllowHeavyLocal;

        /// <summary>True when the command line had --allow-heavy=save-index (a plain run, no service; J33).</summary>
        public static bool SaveIndexLocal;

        /// <summary>
        /// True when the caller took the third answer at a permission point (J33): allow_heavy="save_index" (command line:
        /// --allow-heavy=save-index). It is a yes to the heavy work too; a tool that knows an index which would make the
        /// question fast records the choice (<see cref="SaveIndexAfter"/>), and one that doesn't says so.
        /// </summary>
        public static bool SaveIndexAsked => SaveIndexLocal || _saveIndex;
        static bool _saveIndex;

        /// <summary>The index to make once the answer is given ("world"), set by a tool that took the third answer; null when none.</summary>
        public static string? IndexAfter { get; private set; }

        /// <summary>
        /// The caller answered "save_index" and the tool recorded which plugins (WorldLinks.Want): have the index made after
        /// the answer. In a service worker the coordinator queues it in the indexer lane, at low priority, when the job
        /// ends; a plain command line makes it right after printing the answer (Program.Main).
        /// </summary>
        public static void SaveIndexAfter(string index) => IndexAfter = index;

        /// <summary>
        /// The service's scratch folder: working files that are only good while this service runs. The coordinator empties
        /// it at every start. Null outside a service worker.
        /// </summary>
        public static string? ScratchDir =>
            SessionServer.Active && Environment.GetEnvironmentVariable("SFSEARCH_SCRATCH") is { Length: > 0 } s ? s : null;

        /// <summary>
        /// True when the coordinator asked this job to stop early because an agent is waiting. Only indexer jobs are asked.
        /// A job that sees it should save what it has and return; it is run again later. One that doesn't is stopped after 2 s.
        /// </summary>
        public static bool YieldRequested => _yield;

        /// <summary>The command stopped early on YieldRequested: tell the coordinator to queue it again.</summary>
        public static bool Yielded { get; set; }

        /// <summary>
        /// The command didn't do its work: it asks the coordinator to run it again on a worker with .NET's workstation
        /// garbage collector (J58: the link walk). Only a service worker on the server collector can ask; the coordinator
        /// moves a job once.
        /// </summary>
        public static bool RunOnWorkstation { get; set; }

        /// <summary>True in a service worker that runs .NET's server garbage collector (workerGarbageCollector).</summary>
        public static bool OnServerWorker => ServiceWorker.Connected && System.Runtime.GCSettings.IsServerGC;

        /// <summary>One plugin copy this answer was read from.</summary>
        public static void AddSource(string plugin, long bytes, DateTime modifiedUtc, string key)
        {
            lock (_source)
                if (!_source.Any(s => s.Plugin.Equals(plugin, StringComparison.OrdinalIgnoreCase) && s.Key == key))
                    _source.Add(new SourceInfo { Plugin = plugin, Bytes = bytes, Modified = modifiedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"), Key = key });
        }

        /// <summary>One plugin copy this answer was read from, as the tool describes it (the new tools: size and content key).</summary>
        public static void AddSource(SourceInfo source)
        {
            lock (_source)
                if (!_source.Any(s => s.Plugin.Equals(source.Plugin, StringComparison.OrdinalIgnoreCase) && s.Key == source.Key))
                    _source.Add(source);
        }

        /// <summary>
        /// The tool cut its own answer: these rows of that many, and the cursor to pass back to the same tool for the
        /// next ones. Unlike the service's own pages, such a cursor isn't tied to a job: it works after a restart too.
        /// </summary>
        public static void SetMore(MoreInfo more) => _more = more;

        /// <summary>Something that had to be read for this answer, for the cost line: "Weapon (456) from darkstar.esm".</summary>
        public static void AddRead(string what) { lock (_read) _read.Add(what); }

        /// <summary>The answer is one table and its first lines are the header: pages repeat them, and rows are counted without them.</summary>
        public static void TableHeader(int lines) => _headerLines = Math.Max(0, lines);

        /// <summary>Prints the one-line error with its fix, and sets the exit code. The fix is also kept for the structured answer.</summary>
        public static void Fail(string what, string fix, int code = 1)
        {
            what = what.TrimEnd('.', ' ');
            fix = fix.TrimEnd('.', ' ');
            Console.WriteLine($"Error: {what}. Fix: {fix}.");
            _failure = new FailInfo { Message = $"Error: {what}. Fix: {fix}.", Fix = fix + "." };
            Environment.ExitCode = code;
        }

        // The 2.0.1 signatures of RequireHeavy, NotRun and NotAllowedLine (J81, the user 2026-11-15: "Go with your
        // recommendations."). 2.1 gave each a new optional last parameter; an add-on built for 2.0 calls the old signature
        // by name, so without these it is refused as "built against a different SFSearch". Each forwards to the new one with
        // null. A call in source with two or three arguments picks these (C# prefers the overload that needs no default
        // filled in); a call that names or gives the new parameter picks the new one. Either way the answer is the same.

        /// <summary>RequireHeavy as SFSearch 2.0 had it: the same as <c>RequireHeavy(estimateMB, what, null)</c>.</summary>
        public static bool RequireHeavy(int estimateMB, string what) => RequireHeavy(estimateMB, what, noIndex: null);

        /// <summary>NotRun as SFSearch 2.0 had it: the same as <c>NotRun(line, estimateMB, what, null)</c>.</summary>
        public static void NotRun(string line, long estimateMB, string? what) => NotRun(line, estimateMB, what, saveIndex: null);

        /// <summary>NotAllowedLine as SFSearch 2.0 had it: the same as <c>NotAllowedLine(what, estimateMB, null)</c>.</summary>
        public static string NotAllowedLine(string? what, int estimateMB) => NotAllowedLine(what, estimateMB, noIndex: null);

        /// <summary>
        /// Call before the part of a command that needs a lot of memory, with an honest estimate in MB. Under the heavy
        /// limit (sfsearch.json, 2 GB) it returns true at once. Above it the answer is true only when the caller allowed a
        /// heavy run, no other heavy command is running (it waits its turn for that), and Windows has the estimate plus
        /// the spare available. When it returns false it has printed why and set the exit code: return without doing the work.
        /// </summary>
        public static bool RequireHeavy(int estimateMB, string what, string? noIndex = null)
        {
            _noIndex = noIndex;
            var cfg = ServiceConfig.Load(Home.Dir);
            if (estimateMB < cfg.HeavyMinMB || estimateMB <= _grantedMB) return true;
            if (!ServiceWorker.Connected)
            {
                // A plain run: nothing to queue behind, so only the caller's OK and the PC's memory count
                if (!AllowHeavyLocal) return Refuse(new HeavyInfo { EstimateMB = estimateMB, What = what, Reason = "not_allowed", AvailableMB = MemoryInfo.AvailableMB() }, 0);
                long avail = MemoryInfo.AvailableMB();
                if (avail >= 0 && avail < estimateMB + (long)cfg.HeavySpareMB)
                    return Refuse(new HeavyInfo { EstimateMB = estimateMB, What = what, Reason = "no_memory", AvailableMB = avail }, estimateMB + (long)cfg.HeavySpareMB);
                _grantedMB = estimateMB;
                return true;
            }
            var answer = ServiceWorker.AskHeavy(estimateMB, what);
            if (answer.Kind == "grant") { _grantedMB = estimateMB; return true; }
            return Refuse(new HeavyInfo { EstimateMB = estimateMB, What = what, Reason = answer.Reason ?? "no_memory", AvailableMB = answer.AvailableMB }, answer.NeedMB);
        }

        /// <summary>
        /// A command that is heavy by its time or by a library's own estimate, and that the caller didn't allow: prints
        /// the line that says what it would cost (it starts with "Not run."), keeps the estimate for the structured
        /// reply, and sets the exit code. (Milestone M8: the SQL layer and the reports word their own estimates.)
        /// </summary>
        public static void NotRun(string line, long estimateMB, string? what, SaveIndexInfo? saveIndex = null)
        {
            _heavy = new HeavyInfo { EstimateMB = (int)Math.Min(int.MaxValue, estimateMB), What = what, Reason = "not_allowed", AvailableMB = MemoryInfo.AvailableMB(), SaveIndex = saveIndex };
            Console.WriteLine(line.TrimEnd());
            Environment.ExitCode = NotAllowedCode;
        }

        /// <summary>
        /// The memory this job's reading may hold at once, for the record pipeline's limit (SFSearch.Index\RecordPipeline.cs):
        /// what <see cref="RequireHeavy(int, string, string?)"/> granted it, or else the heavy limit (sfsearch.json, 2 GB), under which a job
        /// needs no grant. A pipeline run held to it never makes a job heavy by itself.
        /// </summary>
        public static long MemoryBudgetMB => Math.Max(_grantedMB, ServiceConfig.Load(Home.Dir).HeavyMinMB);

        /// <summary>
        /// The memory this command may hold (MB, the process's private bytes), which the SQL layer holds a statement to
        /// (GAPS S104): in a service worker the coordinator's allowance for the command (workerMaxMB; a heavy command its
        /// estimate and half again, the spare at most; the indexer's jobs at least 3 GB); in a run without the service the
        /// same rule from sfsearch.json. Settings that allow all memory let a statement use all of it.
        /// </summary>
        internal static long CommandMemoryMB
        {
            get
            {
                if (ServiceWorker.Connected && ServiceWorker.AllowanceMB > 0) return ServiceWorker.AllowanceMB;
                var cfg = ServiceConfig.Load(Home.Dir);
                long granted = _grantedMB;
                return granted > 0 ? Math.Max(cfg.WorkerMaxMB, granted + Math.Min(granted / 2, (long)cfg.HeavySpareMB)) : cfg.WorkerMaxMB;
            }
        }

        /// <summary>True when the caller said yes to heavy work: on the command line, or as the job's own setting in the service.</summary>
        public static bool HeavyAllowed => AllowHeavyLocal || _allowed;
        static bool _allowed;

        static bool Refuse(HeavyInfo h, long needMB)
        {
            _heavy = h;
            if (h.Reason == "not_allowed")
            {
                Console.WriteLine(NotAllowedLine(h.What, h.EstimateMB, _noIndex));
                Environment.ExitCode = NotAllowedCode;
            }
            else
            {
                string line = NoMemoryLine(needMB, h.AvailableMB);
                Console.WriteLine(line);
                _failure = new FailInfo { Message = line, Fix = "try again later, or close other programs." };
                Environment.ExitCode = NoMemoryCode;
            }
            return false;
        }

        static string? _noIndex;

        /// <summary>The answer of a heavy command the caller didn't allow: the estimate, how to allow it, and why no index
        /// would make it faster (J33; the work that asks the heavy lane reads records itself).</summary>
        public static string NotAllowedLine(string? what, int estimateMB, string? noIndex = null) =>
            $"Not run. {(string.IsNullOrWhiteSpace(what) ? "This" : what!.TrimEnd('.', ' '))} needs about {MemoryInfo.GB(estimateMB)} GB of memory."
            + SFSearch.Records.HeavyAnswers.NotRunTail(null, noIndex ?? "it reads the records themselves, and SFSearch keeps indexes only of where records and links are");

        /// <summary>The one line a heavy command fails with when memory is short. needMB is the estimate plus the spare.</summary>
        public static string NoMemoryLine(long needMB, long availableMB) =>
            $"Error: needs about {MemoryInfo.GB(needMB)} GB, {MemoryInfo.GB(Math.Max(0, availableMB))} GB is free. Fix: try again later, or close other programs.";

        // ---------------------------------------------------------------- The worker's side

        internal static void Reset(WorkOrder? order)
        {
            lock (_source) _source.Clear();
            lock (_read) _read.Clear();
            _headerLines = 0; _failure = null; _heavy = null; _more = null; _yield = false; Yielded = false; RunOnWorkstation = false;
            _grantedMB = order?.HeavyMB ?? 0;
            AllowHeavyLocal = false;
            SaveIndexLocal = false;
            _noIndex = null;
            IndexAfter = null;
            _allowed = order?.AllowHeavy ?? false;
            // The third answer (J33): allow_heavy="save_index" as a tool's argument, or --allow-heavy=save-index in its command line
            _saveIndex = order?.Params != null && order.Params.Any(kv => kv.Key.Equals("allow_heavy", StringComparison.OrdinalIgnoreCase)
                    && kv.Value.ValueKind == System.Text.Json.JsonValueKind.String && SFSearch.Records.HeavyAnswers.IsSave(kv.Value.GetString()))
                || (order?.Params != null && order.Params.TryGetValue("args", out var given) && given.ValueKind == System.Text.Json.JsonValueKind.Array
                    && given.EnumerateArray().Any(a => a.ValueKind == System.Text.Json.JsonValueKind.String && IsSaveFlag(a.GetString())));
            Engine.EngineAnswer.ToolMode = false;
            Engine.EngineAnswer.JsonMode = false;
            // The global options of the command before (--json, --csv, --maxwidth, --raw) are that command's only: a worker
            // runs many commands, and one's --json made the next one on the same worker answer in JSON too (GAPS S78)
            CommandLine.Format = OutputFormat.Markdown;
            CommandLine.MaxWidth = 0;
            CommandLine.ResolveNames = true;
        }

        /// <summary>--allow-heavy=save-index (or save_index): the third answer on a command line (J33).</summary>
        public static bool IsSaveFlag(string? arg) =>
            arg != null && arg.StartsWith("--allow-heavy=", StringComparison.OrdinalIgnoreCase) && SFSearch.Records.HeavyAnswers.IsSave(arg["--allow-heavy=".Length..]);

        internal static void AskedToYield() => _yield = true;

        /// <summary>Fills a done event with what the command gave.</summary>
        internal static void Fill(WorkerEvent e)
        {
            var src = new List<SourceInfo>();
            lock (_source) src.AddRange(_source);
            e.Source = src.Count > 0 ? src.ToArray() : null;
            lock (_read) e.Read = _read.Count > 0 ? _read.ToArray() : null;
            e.HeaderLines = _headerLines;
            e.Failure = _failure;
            e.Heavy = _heavy;
            e.More = _more;
            e.Yielded = Yielded;
            e.IndexAfter = IndexAfter;
            e.Workstation = RunOnWorkstation;
        }
    }
}
