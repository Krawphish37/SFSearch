using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SFSearch.Service
{
    /// <summary>
    /// SFSearch's settings (2026-11-07): `sfsearch.json` in SFSearch's home folder: the service, made with every setting
    /// explained the first time the service starts. Comments and trailing commas are allowed; a missing line keeps its
    /// default, and a line this version no longer uses (warmup and dataCapGB, for the older commands removed in 2.0) is
    /// read past. An environment variable of the same setting (SFSEARCH_WORKERS, ...) overrides the file. Read again
    /// whenever it changes, so most edits apply without a restart (the worker count applies to new workers).
    /// The memory settings left at 0 are worked out from the PC (<see cref="PcDefaults"/>, 2026-11-12).
    /// </summary>
    public sealed class ServiceConfig
    {
        /// <summary>How many commands run at once. 0 = from this PC (<see cref="PcDefaults"/>).</summary>
        public int Workers { get; set; }
        public int JobTimeoutMinutes { get; set; } = 10;
        /// <summary>A worker is replaced after a command that left it above this (MB). 0 = from this PC.</summary>
        public int WorkerRecycleMB { get; set; }
        /// <summary>One command is stopped above this (MB). 0 = from this PC.</summary>
        public int WorkerMaxMB { get; set; }
        public int WorkerRecycleCommands { get; set; } = 500;
        public int QueueLimit { get; set; } = 500;
        /// <summary>The workers together may hold this much memory (MB): above it no new work starts. 0 = half the PC's memory.</summary>
        public int ServiceMaxMB { get; set; }
        /// <summary>While commands run and Windows has less than this available (MB), the biggest is stopped. 0 = from this PC.</summary>
        public int MemoryFloorMB { get; set; }
        /// <summary>Where each memory setting in effect came from: the file, an environment variable, or this PC.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public Origins From { get; private set; } = new();

        public sealed class Origins
        {
            public string Workers = "", WorkerMaxMB = "", WorkerRecycleMB = "", ServiceMaxMB = "", MemoryFloorMB = "";
        }

        /// <summary>
        /// 2 (2026-11-13, fix S20): the memory numbers in this file are the user's. A file without it (written by a version
        /// before S17) may hold the fixed numbers those versions wrote by themselves (<see cref="OlderDefaults"/>): they
        /// are taken from the PC instead, and the file is rewritten once to say 0.
        /// </summary>
        public int SettingsVersion { get; set; }
        /// <summary>A command that estimates itself at this much memory (MB) or more is heavy: one at a time, and only when memory is there.</summary>
        public int HeavyMinMB { get; set; } = 2048;
        /// <summary>A heavy command starts only when Windows has its estimate plus this much (MB) available.</summary>
        public int HeavySpareMB { get; set; } = 4096;
        public int HeavyJobTimeoutMinutes { get; set; } = 30;
        /// <summary>
        /// The service looks for changed plugins by itself, for changes2's history: at its start and at every idle check
        /// of the header index (every 10 minutes while idle). False (or SFSEARCH_HISTORY=0): only changes2 looks. (2026-11-13, fix S11.)
        /// </summary>
        public bool HistoryLook { get; set; } = true;
        /// <summary>plugins.txt to read the load order from; empty = Starfield's own (in the current Windows user's AppData).</summary>
        public string? PluginsTxt { get; set; }
        /// <summary>Champollion.exe (Papyrus decompiler) for scripts2; empty = look for it by itself.</summary>
        public string? Champollion { get; set; }

        public static string PathFor(string home) => System.IO.Path.Combine(home, "sfsearch.json");

        static readonly JsonSerializerOptions Read = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        static ServiceConfig? _cached;
        static DateTime _cachedStamp;
        public static string? LastProblem { get; private set; }

        /// <summary>
        /// The numbers versions before S17 (2026-11-12) wrote into a new sfsearch.json by themselves, whatever the PC:
        /// 8 workers, workerRecycleMB 3072, workerMaxMB 12288 (1.0.x and 2.0.0) or 6144 (the M4 build). serviceMaxMB was
        /// already 0. Read from the git history of DefaultText.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, int[]> OlderDefaults = new Dictionary<string, int[]>
        {
            ["workers"] = new[] { 8 },
            ["workerRecycleMB"] = new[] { 3072 },
            ["workerMaxMB"] = new[] { 12288, 6144 },
        };

        /// <summary>A line only S17's new file has: a file with it was written after the memory defaults came from the PC.</summary>
        const string S17Marker = "0 means SFSearch works the number out from this PC";

        /// <summary>The file is an older version's (no settingsVersion, not written by S17): its older default numbers are taken from the PC.</summary>
        static bool _olderFile;
        /// <summary>Each memory setting this version set from an older default to 0 in the file: its name, the old number, the day.</summary>
        static Dictionary<string, (int Was, string Day)> _rewritten = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>What the last rewrite of an older file did, or why it couldn't (for `Service config` and the service log).</summary>
        public static string? RewriteNote { get; private set; }

        /// <summary>The settings now: the file (made if missing, read again when it changes), then environment overrides.</summary>
        public static ServiceConfig Load(string home)
        {
            string path = PathFor(home);
            try { if (!File.Exists(path)) File.WriteAllText(path, DefaultText); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (_cached == null || stamp != _cachedStamp)
            {
                ServiceConfig c;
                try
                {
                    string text = File.Exists(path) ? File.ReadAllText(path) : "";
                    c = text.Length > 0 ? JsonSerializer.Deserialize<ServiceConfig>(text, Read) ?? new() : new();
                    LastProblem = null;
                    _olderFile = text.Length > 0 && c.SettingsVersion < 2 && !text.Contains(S17Marker);
                    if (_olderFile && RewriteOlder(path, text, c) is { } newer)
                    {
                        // Read what was written, so the numbers in effect and the file agree
                        text = newer;
                        c = JsonSerializer.Deserialize<ServiceConfig>(text, Read) ?? new();
                        _olderFile = c.SettingsVersion < 2;
                        stamp = File.GetLastWriteTimeUtc(path);
                    }
                    _rewritten = RewrittenLines(text);
                }
                catch (Exception e) when (e is JsonException or IOException)
                {
                    c = _cached ?? new ServiceConfig(); // a broken edit keeps the last good settings
                    LastProblem = $"{path} couldn't be read ({e.Message}); using the last good settings.";
                }
                _cached = c;
                _cachedStamp = stamp;
            }
            return WithEnvironment(_cached);
        }

        static readonly System.Text.RegularExpressions.Regex RewrittenLine = new(
            @"^\s*""(?<name>workers|workerRecycleMB|workerMaxMB)""\s*:\s*0\s*,?\s*//\s*was (?<was>\d+), what older versions of SFSearch wrote here by themselves; set to 0 on (?<day>\d{4}-\d\d-\d\d)",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        static Dictionary<string, (int, string)> RewrittenLines(string text)
        {
            var d = new Dictionary<string, (int, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Text.RegularExpressions.Match m in RewrittenLine.Matches(text))
                d[m.Groups["name"].Value] = (int.Parse(m.Groups["was"].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups["day"].Value);
            return d;
        }

        /// <summary>
        /// An older version's file, once (2026-11-13, fix S20): each memory line that holds the number that version wrote
        /// by itself is set to 0 with a comment saying what it was, and "settingsVersion": 2 is put in after the opening
        /// brace, so a number the user puts in later is theirs. Only those lines change; every other line and comment
        /// stays as it was. The file as it was is kept beside it (sfsearch.json.before-v2.bak). Written to a new file
        /// and moved over the old one, so a reader never sees half of it. Two programs doing it at once write the same
        /// text. Not done when a line isn't on its own (a file written by hand on one line): the numbers are then taken
        /// from the PC all the same, and `Service config` says why the file wasn't changed. Returns the new text, or null.
        /// </summary>
        static string? RewriteOlder(string path, string text, ServiceConfig c)
        {
            var olderNow = new List<(string Name, int Was)>();
            if (OlderDefaults["workers"].Contains(c.Workers)) olderNow.Add(("workers", c.Workers));
            if (OlderDefaults["workerRecycleMB"].Contains(c.WorkerRecycleMB)) olderNow.Add(("workerRecycleMB", c.WorkerRecycleMB));
            if (OlderDefaults["workerMaxMB"].Contains(c.WorkerMaxMB)) olderNow.Add(("workerMaxMB", c.WorkerMaxMB));
            if (olderNow.Count == 0) return null; // nothing to change: the numbers in it are the user's
            if (Environment.GetEnvironmentVariable("SFSEARCH_KEEP_SETTINGS_FILE") == "1")
            { RewriteNote = "sfsearch.json wasn't changed (SFSEARCH_KEEP_SETTINGS_FILE=1)."; return null; }
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            string day = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var done = new List<string>();
            foreach (var (name, was) in olderNow)
            {
                var re = new System.Text.RegularExpressions.Regex($@"^(?<indent>\s*)""{name}""\s*:\s*{was}\s*(?<comma>,?)\s*(//.*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                int at = lines.FindIndex(l => re.IsMatch(l));
                if (at < 0) { RewriteNote = $"sfsearch.json wasn't changed: its \"{name}\" line isn't on a line of its own. The older default numbers in it are taken from this PC all the same; write 0 there to say so in the file."; return null; }
                var m = re.Match(lines[at]);
                lines[at] = $"{m.Groups["indent"].Value}\"{name}\": 0{m.Groups["comma"].Value} // was {was}, what older versions of SFSearch wrote here by themselves; set to 0 on {day} (0 = from this PC). A number you put here is kept.";
                done.Add($"{name} {was} → 0");
            }
            int open = lines.FindIndex(l => l.Trim() == "{");
            if (open < 0) { RewriteNote = "sfsearch.json wasn't changed: it has no line with only the opening {. The older default numbers in it are taken from this PC all the same."; return null; }
            lines.Insert(open + 1, "  \"settingsVersion\": 2,");
            lines.Insert(open + 1, "  // The version of this file's settings. 2: every number below is yours (versions before 2026-11-13 wrote fixed memory numbers by themselves).");
            string newer = string.Join(nl, lines);
            try
            {
                string bak = path + ".before-v2.bak";
                if (!File.Exists(bak)) File.WriteAllText(bak, text);
                string tmp = $"{path}.{Environment.ProcessId}.tmp";
                File.WriteAllText(tmp, newer);
                File.Move(tmp, path, true);
                RewriteNote = $"sfsearch.json was written by an older version: {string.Join(", ", done)} on {day}, so they come from this PC (the file as it was: {Path.GetFileName(bak)}).";
                return newer;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                RewriteNote = $"sfsearch.json couldn't be changed ({e.Message.TrimEnd('.')}): its older default numbers are taken from this PC all the same.";
                return null;
            }
        }

        static ServiceConfig WithEnvironment(ServiceConfig f)
        {
            int I(string name, int v) => int.TryParse(Environment.GetEnvironmentVariable(name), out int e) && e > 0 ? e : v;
            // The memory settings: an environment variable, else the file, else this PC. Each says where it came from.
            // A number an older version wrote into the file by itself counts as 0 (fix S20).
            var from = new Origins();
            int? Set(string env, string name, int fileValue, out string origin)
            {
                if (int.TryParse(Environment.GetEnvironmentVariable(env), out int e) && e > 0) { origin = $"set by {env}"; return e; }
                if (fileValue > 0 && _olderFile && OlderDefaults.TryGetValue(name, out var older) && older.Contains(fileValue))
                {
                    origin = $"; sfsearch.json has {fileValue}, the number older versions wrote there by themselves, so it is taken from this PC";
                    return null;
                }
                if (fileValue > 0) { origin = "set in sfsearch.json"; return fileValue; }
                origin = _rewritten.TryGetValue(name, out var r) ? $"; sfsearch.json had {r.Was}, an older version's number, until {r.Day}" : "";
                return null;
            }
            // "from this PC: how" and what the file said, when it said something
            static string Pc(string how, string note) => "from this PC: " + how + note;
            long total = MemoryInfo.TotalMB();
            int cores = MemoryInfo.Cores();
            int budget = Set("SFSEARCH_SERVICE_MAX_MB", "serviceMaxMB", f.ServiceMaxMB, out from.ServiceMaxMB) is int sm ? Math.Max(1024, sm) : PcDefaults.ServiceMaxMB(total);
            if (!from.ServiceMaxMB.StartsWith("set ")) from.ServiceMaxMB = total > 0 ? Pc("half its memory", from.ServiceMaxMB) : "the PC's memory couldn't be read: 16 GB";
            int workers = Set("SFSEARCH_WORKERS", "workers", f.Workers, out from.Workers) is int wk ? Math.Max(1, wk) : PcDefaults.Workers(budget, cores);
            if (!from.Workers.StartsWith("set ")) from.Workers = Pc($"one per 2 GB of the service's {MemoryInfo.GB(budget)} GB, at least 2, at most 8 and at most its {cores} cores less 2", from.Workers);
            int workerMax = Set("SFSEARCH_WORKER_MAX_MB", "workerMaxMB", f.WorkerMaxMB, out from.WorkerMaxMB) is int wm ? Math.Max(512, wm) : PcDefaults.WorkerMaxMB(budget);
            if (!from.WorkerMaxMB.StartsWith("set ")) from.WorkerMaxMB = Pc($"a third of the service's {MemoryInfo.GB(budget)} GB, at least 1.5 GB, at most 6 GB", from.WorkerMaxMB);
            int recycle = Set("SFSEARCH_WORKER_RECYCLE_MB", "workerRecycleMB", f.WorkerRecycleMB, out from.WorkerRecycleMB) is int rc ? Math.Max(256, rc) : PcDefaults.WorkerRecycleMB(workerMax);
            if (!from.WorkerRecycleMB.StartsWith("set ")) from.WorkerRecycleMB = Pc($"half of one command's {MemoryInfo.GB(workerMax)} GB, at least 1 GB, at most 3 GB", from.WorkerRecycleMB);
            int floor = Set("SFSEARCH_MEMORY_FLOOR_MB", "memoryFloorMB", f.MemoryFloorMB, out from.MemoryFloorMB) is int fl ? Math.Max(256, fl) : PcDefaults.MemoryFloorMB(total);
            if (!from.MemoryFloorMB.StartsWith("set ")) from.MemoryFloorMB = Pc("the larger of 1.5 GB and 5% of its memory", from.MemoryFloorMB);
            return new ServiceConfig
            {
                Workers = workers,
                JobTimeoutMinutes = Math.Max(1, I("SFSEARCH_JOB_MINUTES", f.JobTimeoutMinutes)),
                WorkerRecycleMB = recycle,
                WorkerMaxMB = workerMax,
                WorkerRecycleCommands = Math.Max(1, I("SFSEARCH_WORKER_JOBS", f.WorkerRecycleCommands)),
                QueueLimit = Math.Max(1, I("SFSEARCH_QUEUE_LIMIT", f.QueueLimit)),
                ServiceMaxMB = budget,
                MemoryFloorMB = floor,
                From = from,
                HeavyMinMB = Math.Max(1, I("SFSEARCH_HEAVY_MIN_MB", f.HeavyMinMB)),
                HeavySpareMB = Math.Max(0, I("SFSEARCH_HEAVY_SPARE_MB", f.HeavySpareMB)),
                HeavyJobTimeoutMinutes = Math.Max(1, I("SFSEARCH_HEAVY_JOB_MINUTES", f.HeavyJobTimeoutMinutes)),
                HistoryLook = Environment.GetEnvironmentVariable("SFSEARCH_HISTORY") != "0" && f.HistoryLook,
                Champollion = f.Champollion,
                PluginsTxt = Environment.GetEnvironmentVariable("SFSEARCH_PLUGINS_TXT") is string ptx && ptx.Length > 0 ? ptx : f.PluginsTxt,
            };
        }

        /// <summary>
        /// The memory settings a PC gets when sfsearch.json leaves them at 0 (2026-11-12, fix S17). Measured on the 64 GB
        /// PC (design\agent-first\notes\fix-mem.md): an ordinary command of the new tools holds 30 to 230 MB at its peak,
        /// the older commands (removed in 2.0) 80 to 260 MB, the indexer's jobs 1.8 to 2.3 GB; the biggest older
        /// commands 3.9 GB (with no service). On that PC these give what was fixed before: 8 workers, 6 GB a command, 3 GB to recycle.
        /// </summary>
        public static class PcDefaults
        {
            /// <summary>The workers together: half the PC's memory, at least 1 GB (16 GB when it can't be read).</summary>
            public static int ServiceMaxMB(long totalMB) => totalMB > 0 ? (int)Math.Max(1024, totalMB / 2) : 16384;

            /// <summary>Workers: one per 2 GB of the service's memory, at least 2, at most 8 and at most the cores less 2.</summary>
            public static int Workers(int budgetMB, int cores) => Math.Clamp(budgetMB / 2048, 2, Math.Max(2, Math.Min(8, cores - 2)));

            /// <summary>One command: a third of the service's memory, at least 1.5 GB, at most 6 GB.</summary>
            public static int WorkerMaxMB(int budgetMB) => Math.Clamp(budgetMB / 3, 1536, 6144);

            /// <summary>A worker is replaced after a command above half of one command's limit, at least 1 GB, at most 3 GB.</summary>
            public static int WorkerRecycleMB(int workerMaxMB) => Math.Clamp(workerMaxMB / 2, 1024, 3072);

            /// <summary>The floor: the larger of 1.5 GB and 5% of the PC's memory.</summary>
            public static int MemoryFloorMB(long totalMB) => (int)Math.Max(1536, totalMB > 0 ? totalMB / 20 : 0);
        }

        /// <summary>The memory settings in effect, one per line, each with where it came from (for `Service config` and `Service status`).</summary>
        public string MemoryLines(string indent = "  ")
        {
            long total = MemoryInfo.TotalMB();
            var sb = new System.Text.StringBuilder();
            sb.Append($"Memory settings: this PC has {(total > 0 ? MemoryInfo.GB(total) + " GB" : "an unknown amount of memory")} and {MemoryInfo.Cores()} cores{(MemoryInfo.Pretending ? " (a test's pretend PC: SFSEARCH_TEST_MEMORY)" : "")}. In effect:");
            sb.Append($"\n{indent}The workers together: {MemoryInfo.GB(ServiceMaxMB)} GB (serviceMaxMB, {From.ServiceMaxMB}). Above it no new command starts.");
            sb.Append($"\n{indent}Workers: {Workers} at once (workers, {From.Workers}).");
            sb.Append($"\n{indent}One command: {MemoryInfo.GB(WorkerMaxMB)} GB (workerMaxMB, {From.WorkerMaxMB}). Above it the command is stopped. A heavy command has its own allowance; the indexer's jobs may use {MemoryInfo.GB(Math.Max(WorkerMaxMB, IndexAllowanceMB))} GB.");
            sb.Append($"\n{indent}A worker is replaced after a command that left it above {MemoryInfo.GB(WorkerRecycleMB)} GB (workerRecycleMB, {From.WorkerRecycleMB}).");
            sb.Append($"\n{indent}The floor: {MemoryInfo.GB(MemoryFloorMB)} GB available (memoryFloorMB, {From.MemoryFloorMB}). Under it, while commands run, the indexer gives way, then the biggest command is stopped with a clear error and can be asked again. New commands wait until Windows has {MemoryInfo.GB(HoldNewWorkBelowMB)} GB available.");
            if (RewriteNote != null) sb.Append($"\n{indent}{RewriteNote}");
            return sb.ToString();
        }

        /// <summary>The indexer's jobs may hold this much (MB) even when one command's limit is lower: the header index peaks at 1.9 GB, the link index at 2.3 GB.</summary>
        public const int IndexAllowanceMB = 3072;

        /// <summary>No new command starts (while others run) when Windows has this much available or less: the floor and half a GB, at least 2 GB.</summary>
        public int HoldNewWorkBelowMB => Math.Max(2048, MemoryFloorMB + 512);

        /// <summary>For `Service config`: where the file is and what's in effect.</summary>
        public static string Describe(string home)
        {
            var c = Load(home);
            return $@"Settings file: {PathFor(home)}{(LastProblem != null ? "\nWARNING: " + LastProblem : "")}
Workers: {c.Workers} at once. Each command may take {c.JobTimeoutMinutes} min. A worker is replaced after {c.WorkerRecycleCommands} commands or {c.WorkerRecycleMB} MB, and stopped above {c.WorkerMaxMB} MB.
{c.MemoryLines()}
Heavy commands (estimated at {c.HeavyMinMB} MB or more): one at a time, each may take {c.HeavyJobTimeoutMinutes} min, and one starts only when Windows has its estimate plus {c.HeavySpareMB} MB available.
Changed plugins (changes2's history): {(c.HistoryLook ? "the service looks by itself, at its start and every 10 min while idle (historyLook)" : "only changes2 looks (historyLook is off, or SFSEARCH_HISTORY=0)")}.
Queue limit: {c.QueueLimit}.{UnusedLines(home)}
Environment variables (SFSEARCH_WORKERS, SFSEARCH_HISTORY=0, ...) override the file.";
        }

        /// <summary>
        /// Settings an older sfsearch.json may still hold that this version reads past: the warm-up and dataCapGB were for
        /// the older commands' tables, removed in 2.0. One line for `Service config`, or "" when the file has neither.
        /// </summary>
        static string UnusedLines(string home)
        {
            string text;
            try { text = File.Exists(PathFor(home)) ? File.ReadAllText(PathFor(home)) : ""; }
            catch (IOException) { return ""; }
            var unused = new[] { "warmup", "dataCapGB" }.Where(k => System.Text.RegularExpressions.Regex.IsMatch(text, "\"" + k + "\"\\s*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
            return unused.Count == 0 ? "" : $"\nNo longer used: {string.Join(" and ", unused)} in sfsearch.json (they were for the older commands' tables, removed in 2.0). They change nothing; delete them when you like.";
        }

        const string DefaultText = @"// SFSearch settings: the service. Edit and save: SFSearch reads this again by itself.
// Delete a line to use its default. Environment variables of the same setting (SFSEARCH_WORKERS, SFSEARCH_HISTORY=0, ...)
// override this file. 'sfs.cmd Service config' shows what's in effect.
{
  // The version of this file's settings. 2: every number below is yours (versions before 2026-11-13 wrote fixed memory numbers by themselves).
  ""settingsVersion"": 2,
  // Memory: 0 means SFSearch works the number out from this PC's memory and cores when it starts, so the same file
  // suits an 8 GB laptop and a 64 GB PC. A number you put in is kept as you set it. 'sfs.cmd Service config' shows
  // each number in effect and where it came from.
  // How many commands run at once (each in its own worker program). 0 = one per 2 GB of serviceMaxMB, at least 2,
  // at most 8 and at most the PC's cores less 2 (8 on a 32 GB PC or bigger).
  ""workers"": 0,
  // A command running longer than this is stopped with a clear error.
  ""jobTimeoutMinutes"": 10,
  // A worker is replaced after a command that left it above workerRecycleMB, or after workerRecycleCommands commands.
  // A command is stopped mid-way above workerMaxMB.
  // 0 = workerMaxMB is a third of serviceMaxMB (at least 1536, at most 6144: 2.7 GB on a 16 GB PC, 6 GB on a 64 GB PC),
  // and workerRecycleMB half of workerMaxMB (at least 1024, at most 3072).
  ""workerRecycleMB"": 0,
  ""workerMaxMB"": 0,
  ""workerRecycleCommands"": 500,
  // All workers together may hold this much memory (MB). Above it no new command starts until some is given back.
  // 0 = half of this PC's memory.
  ""serviceMaxMB"": 0,
  // While commands run and Windows has less than this available (MB), the indexer gives way, then the biggest
  // command is stopped with a clear error (it can be asked again), before Windows has to page.
  // 0 = the larger of 1536 and 5% of this PC's memory.
  ""memoryFloorMB"": 0,
  // Heavy commands: one that estimates itself at heavyMinMB or more runs one at a time, and starts only when Windows
  // has its estimate plus heavySpareMB available. If not, it fails at once with a clear line: it never waits, and it
  // never starts and makes the PC page. A heavy command may hold more than workerMaxMB: up to its estimate and half
  // again (heavySpareMB more at most), and has its own time limit.
  ""heavyMinMB"": 2048,
  ""heavySpareMB"": 4096,
  ""heavyJobTimeoutMinutes"": 30,
  // changes2 tells what changed in Data (plugins added, removed, updated; patches at risk). The service looks by itself
  // at its start and every 10 minutes while idle, so a change is dated near when it happened and changes2 compares
  // with the service's last look. A look when nothing changed takes a few milliseconds. The history it keeps (kept\history)
  // holds each plugin copy's FormKeys and record types, no record contents. false = only changes2 looks.
  ""historyLook"": true,
  // More waiting commands than this are turned away with ""busy, try again"".
  ""queueLimit"": 500,

  // Champollion.exe, the Papyrus decompiler scripts2 uses (version 1.3.2 or newer). Leave empty to look for it in
  // SFSearch's tools\champollion folder.
  ""champollion"": """",

  // The load order (plugins.txt) to use. Leave empty for Starfield's own, in your Windows user's AppData folder. Set it to
  // read another one, e.g. a second Windows account's: C:\\Users\\Other\\AppData\\Local\\Starfield\\Plugins.txt
  ""pluginsTxt"": """"
}
";
    }
}
