using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SFSearch.Service
{
    /// <summary>
    /// What a command can tell the service about its own answer, and ask of it (2026-11-11, milestone M4). Today's
    /// commands use none of it and print as before. The commands of the later milestones use it for the structured
    /// answer: where the data came from, what had to be read, a table's header, an error with its fix, and the heavy lane.
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

        /// <summary>
        /// Call before the part of a command that needs a lot of memory, with an honest estimate in MB. Under the heavy
        /// limit (sfsearch.json, 2 GB) it returns true at once. Above it the answer is true only when the caller allowed a
        /// heavy run, no other heavy command is running (it waits its turn for that), and Windows has the estimate plus
        /// the spare available. When it returns false it has printed why and set the exit code: return without doing the work.
        /// </summary>
        public static bool RequireHeavy(int estimateMB, string what)
        {
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
        public static void NotRun(string line, long estimateMB, string? what)
        {
            _heavy = new HeavyInfo { EstimateMB = (int)Math.Min(int.MaxValue, estimateMB), What = what, Reason = "not_allowed", AvailableMB = MemoryInfo.AvailableMB() };
            Console.WriteLine(line.TrimEnd());
            Environment.ExitCode = NotAllowedCode;
        }

        /// <summary>True when the caller said yes to heavy work: on the command line, or as the job's own setting in the service.</summary>
        public static bool HeavyAllowed => AllowHeavyLocal || _allowed;
        static bool _allowed;

        static bool Refuse(HeavyInfo h, long needMB)
        {
            _heavy = h;
            if (h.Reason == "not_allowed")
            {
                Console.WriteLine(NotAllowedLine(h.What, h.EstimateMB));
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

        /// <summary>The answer of a heavy command the caller didn't allow: the estimate, and how to allow it.</summary>
        public static string NotAllowedLine(string? what, int estimateMB) =>
            $"Not run. {(string.IsNullOrWhiteSpace(what) ? "This" : what!.TrimEnd('.', ' '))} needs about {MemoryInfo.GB(estimateMB)} GB of memory. Call again with allow_heavy=true (command line: --allow-heavy).";

        /// <summary>The one line a heavy command fails with when memory is short. needMB is the estimate plus the spare.</summary>
        public static string NoMemoryLine(long needMB, long availableMB) =>
            $"Error: needs about {MemoryInfo.GB(needMB)} GB, {MemoryInfo.GB(Math.Max(0, availableMB))} GB is free. Fix: try again later, or close other programs.";

        // ---------------------------------------------------------------- The worker's side

        internal static void Reset(WorkOrder? order)
        {
            lock (_source) _source.Clear();
            lock (_read) _read.Clear();
            _headerLines = 0; _failure = null; _heavy = null; _more = null; _yield = false; Yielded = false;
            _grantedMB = order?.HeavyMB ?? 0;
            AllowHeavyLocal = false;
            _allowed = order?.AllowHeavy ?? false;
            Engine.EngineAnswer.ToolMode = false;
            Engine.EngineAnswer.JsonMode = false;
        }

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
        }
    }
}
