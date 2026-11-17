using System.Text.RegularExpressions;
using SFSearch.Service;

namespace SFSearch.ServiceTest;

/// <summary>
/// `olderfile --exe &lt;SFSearch.exe&gt; [--older &lt;sfsearch.json of an older version&gt;]` (2026-11-13, fix S20 of
/// design\agent-first\GAPS.md; J37 2026-11-15: on the real PC, no pretend PC and no SFSEARCH_KEEP_SETTINGS_FILE): an
/// sfsearch.json written by a version before the memory defaults came from the PC holds fixed numbers (8 workers,
/// workerRecycleMB 3072, workerMaxMB 12288 or 6144). Those must be taken from this PC
/// (<see cref="ServiceConfig.PcDefaults"/>), `Service config` must say where each came from, the file must be rewritten
/// once (those three lines to 0 with a comment, "settingsVersion": 2 put in, every other line as it was, the old file kept
/// beside it), a file that can't be rewritten (a number not on a line of its own) must keep its text and still have the
/// numbers taken from the PC, and a number the user chose must stay theirs. Without --older it uses the default file of
/// the version before S17 (from the git history). The file is copied into its own home; the one given is never changed.
/// Its own test Data folder holds one small plugin Mutagen writes. Starts and stops its own service once.
/// </summary>
public static partial class T
{
    /// <summary>The start of the sfsearch.json the versions before S17 (2026-11-12) wrote, from git (2a8a3c5~1).</summary>
    const string OlderDefaultFile = """
        // SFSearch settings: the service, and the warm-up for the older commands. Edit and save: SFSearch reads this again by itself.
        // Delete a line to use its default. Environment variables of the same setting (SFSEARCH_WORKERS, SFSEARCH_WARMUP=0, ...)
        // override this file. 'sfs.cmd Service config' shows what's in effect.
        {
          // How many commands run at once (each in its own worker program).
          "workers": 8,
          // A command running longer than this is stopped with a clear error.
          "jobTimeoutMinutes": 10,
          // A worker is replaced after this much memory (MB) or this many commands, and stopped mid-command above workerMaxMB.
          "workerRecycleMB": 3072,
          "workerMaxMB": 6144,
          "workerRecycleCommands": 500,
          // All workers together may hold this much memory (MB). Above it no new command starts until some is given back.
          // 0 = half of this PC's memory.
          "serviceMaxMB": 0,
          "heavyMinMB": 2048,
          "heavySpareMB": 4096,
          "heavyJobTimeoutMinutes": 30,
          // More waiting commands than this are turned away with "busy, try again".
          "queueLimit": 500,
          "dataCapGB": 40,
          "champollion": ""
        }

        """;

    static async Task OlderFile(string? older)
    {
        if (!File.Exists(Exe)) { Check("olderfile: --exe names SFSearch.exe", false, Exe); return; }
        if (older != null && !File.Exists(older)) { Check("olderfile: --older names an sfsearch.json of an older version", false, older); return; }
        // CRLF whatever line ends this source file has (a checkout with core.autocrlf gives the raw string CRLF already)
        string original = older != null ? File.ReadAllText(older) : OlderDefaultFile.Replace("\r\n", "\n").Replace("\n", "\r\n");
        if (original.Contains("settingsVersion")) { Check("olderfile: the file given is an older version's (no settingsVersion)", false, older ?? ""); return; }
        string root = Path.Combine(Home, "olderfile"), home = Path.Combine(root, "home"), data = Path.Combine(root, "plugins");
        if (Path.GetFullPath(data).Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)) { Check("olderfile: the test Data folder is outside the game's", false, data); return; }
        string outer = Home;
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(home); Directory.CreateDirectory(data);
        WriteMaster(data, small: false);
        File.WriteAllLines(Path.Combine(root, "plugins.txt"), new[] { "*" + KMaster });
        var env = new Dictionary<string, string> { ["SFSEARCH_HOME"] = home, ["SFSEARCH_DATA"] = data, ["SFSEARCH_PLUGINS_TXT"] = Path.Combine(root, "plugins.txt") };

        // What this PC gives (the same arithmetic the service uses, from the numbers of this PC)
        long total = TotalMB();
        int budget = ServiceConfig.PcDefaults.ServiceMaxMB(total), pcWorkers = ServiceConfig.PcDefaults.Workers(budget, Environment.ProcessorCount);
        int pcMax = ServiceConfig.PcDefaults.WorkerMaxMB(budget), pcRecycle = ServiceConfig.PcDefaults.WorkerRecycleMB(pcMax);
        string W = $"Workers: {pcWorkers} at once (workers, from this PC", M = $"One command: {MemoryInfo.GB(pcMax)} GB (workerMaxMB, from this PC", R = $"above {MemoryInfo.GB(pcRecycle)} GB (workerRecycleMB, from this PC";
        Say($"this PC ({total} MB, {Environment.ProcessorCount} cores): {pcWorkers} workers, {MemoryInfo.GB(pcMax)} GB a command, {MemoryInfo.GB(pcRecycle)} GB to recycle");

        string cfgPath = Path.Combine(home, "sfsearch.json"), bak = cfgPath + ".before-v2.bak";
        void Fresh(string text) { File.WriteAllText(cfgPath, text); if (File.Exists(bak)) File.Delete(bak); }
        static string Lines(string cfg) => string.Join(" / ", cfg.Replace("\r", "").Split('\n').Where(l => l.StartsWith("  ") && Regex.IsMatch(l, @"^\s+(Workers|One command|A worker is replaced|sfsearch\.json)")).Select(l => l.Trim()));
        string olderWorkers = Regex.Match(original, @"""workers""\s*:\s*(\d+)").Groups[1].Value, olderMax = Regex.Match(original, @"""workerMaxMB""\s*:\s*(\d+)").Groups[1].Value, olderRecycle = Regex.Match(original, @"""workerRecycleMB""\s*:\s*(\d+)").Groups[1].Value;
        Say($"the older file: workers {olderWorkers}, workerMaxMB {olderMax}, workerRecycleMB {olderRecycle}");
        Home = home;
        try
        {
            // 1. A file that can't be rewritten (its workers line shares a line with another setting): kept as it is, and
            //    the numbers are still taken from the PC, and config says why
            string oneLine = Regex.Replace(original, @"^(\s*)""workers""", "$1\"queueLimit\": 500, \"workers\"", RegexOptions.Multiline);
            oneLine = Regex.Replace(oneLine, @"^\s*""queueLimit""\s*:\s*500,\s*\r?\n", "", RegexOptions.Multiline);
            Fresh(oneLine);
            string cfg = (await RunService(env, "Service", "config")).Out;
            bool fromPc = cfg.Contains(W) && cfg.Contains($"sfsearch.json has {olderWorkers}, the number older versions wrote there by themselves, so it is taken from this PC)")
                && cfg.Contains(M) && cfg.Contains($"sfsearch.json has {olderMax}, the number older versions wrote there by themselves")
                && cfg.Contains(R) && cfg.Contains($"sfsearch.json has {olderRecycle}, the number older versions wrote there by themselves")
                && cfg.Contains("sfsearch.json wasn't changed: its \"workers\" line isn't on a line of its own");
            Check("olderfile: a file that can't be rewritten is kept as it is, and the older numbers are taken from the PC", fromPc && File.ReadAllText(cfgPath) == oneLine && !File.Exists(bak), Lines(cfg));

            // 2. The first read rewrites the file once: three lines to 0, settingsVersion 2, every other line as it was
            Fresh(original);
            cfg = (await RunService(env, "Service", "config")).Out;
            string after = File.ReadAllText(cfgPath);
            var before = original.Replace("\r\n", "\n").Split('\n').ToList();
            var now = after.Replace("\r\n", "\n").Split('\n').ToList();
            var changed = now.Where(l => Regex.IsMatch(l, @"^\s*""(workers|workerMaxMB|workerRecycleMB)""\s*:\s*0, // was \d+, what older versions of SFSearch wrote here by themselves; set to 0 on \d{4}-\d\d-\d\d \(0 = from this PC\)\. A number you put here is kept\.$")).ToList();
            var removed = before.Where(l => Regex.IsMatch(l, @"^\s*""(workers|workerMaxMB|workerRecycleMB)""\s*:")).ToList();
            bool sameRest = before.Where(l => !removed.Contains(l)).SequenceEqual(now.Where(l => !changed.Contains(l) && l.Trim() != "\"settingsVersion\": 2," && !l.Contains("The version of this file's settings.")));
            Check("olderfile: the file is rewritten once: those 3 lines to 0, settingsVersion 2, every other line as it was",
                changed.Count == 3 && removed.Count == 3 && sameRest && now.Count == before.Count + 2 && Regex.IsMatch(after, @"\{\r?\n  // The version of this file's settings\. 2: [^\n]*\r?\n  ""settingsVersion"": 2,")
                && (original.Contains("\r\n") == after.Contains("\r\n")),
                $"{changed.Count} lines set to 0, {now.Count - before.Count} added; e.g. {changed.FirstOrDefault()?.Trim()}");
            Check("olderfile: the file as it was is kept beside it", File.Exists(bak) && File.ReadAllText(bak) == original, Path.GetFileName(bak));
            Check("olderfile: config says what was done, and each number comes from the PC",
                cfg.Contains("sfsearch.json was written by an older version: workers") && cfg.Contains(W)
                && cfg.Contains($"sfsearch.json had {olderWorkers}, an older version's number, until ") && cfg.Contains(M) && cfg.Contains(R),
                Lines(cfg));
            string again = (await RunService(env, "Service", "config")).Out;
            Check("olderfile: read again, nothing more changes", File.ReadAllText(cfgPath) == after && again.Contains(W) && !again.Contains("was written by an older version"), Lines(again));

            // 3. A number the user puts in afterwards is theirs, the older default number included
            File.WriteAllText(cfgPath, Regex.Replace(after, @"""workers"": 0, // was[^\n]*", @"""workers"": 8,"));
            cfg = (await RunService(env, "Service", "config")).Out;
            Check("olderfile: after the rewrite, 8 written by the user is kept", cfg.Contains("Workers: 8 at once (workers, set in sfsearch.json)"), Lines(cfg));

            // 4. In an older file, a number that isn't an older default is the user's; only the older ones are rewritten
            Fresh(Regex.Replace(original, @"""workers""\s*:\s*\d+", @"""workers"": 6"));
            cfg = (await RunService(env, "Service", "config")).Out;
            after = File.ReadAllText(cfgPath);
            Check("olderfile: an older file's own choice (workers 6) is kept; the other two are rewritten",
                cfg.Contains("Workers: 6 at once (workers, set in sfsearch.json)") && after.Contains("\"workers\": 6,") && cfg.Contains(M) && Regex.Matches(after, @": 0, // was ").Count == 2,
                Lines(cfg));

            // 5. The service on this PC with the older file runs this PC's number of workers, and its status says so
            Fresh(original);
            var started = await RunService(env, "Service", "start");
            try
            {
                string status = started.Out.Contains("Service started") ? await Status() : started.Out + started.Err;
                Check($"olderfile: the service with the older file runs this PC's {pcWorkers} workers", status.Contains($"up to {pcWorkers} running at once") && status.Contains(W),
                    Line(status, "Workers:") + " / " + Line(status, $"Workers: {pcWorkers}"));
            }
            finally { await RunService(env, "Service", "stop"); }
        }
        finally { Home = outer; }
    }
}
