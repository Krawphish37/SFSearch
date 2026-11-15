using System.Text.RegularExpressions;

namespace SFSearch.ServiceTest;

/// <summary>
/// `olderfile --older &lt;sfsearch.json of an older version&gt;` (2026-11-13, fix S20 of design\agent-first\GAPS.md): an
/// sfsearch.json written by a version before the memory defaults came from the PC holds fixed numbers (8 workers,
/// workerRecycleMB 3072, workerMaxMB 12288 or 6144). On a pretend 8 GB PC those must be taken from the PC (2 workers,
/// 1.5 GB a command, 1 GB to recycle), `Service config` must say where each came from, the file must be rewritten once
/// (those three lines to 0 with a comment, "settingsVersion": 2 put in, every other line as it was, the old file kept
/// beside it), and a number the user chose must stay theirs. The file given is copied into the test home; it is never
/// changed itself. Starts and stops its own service (with the pretend PC) once, to see the service run 2 workers.
/// </summary>
public static partial class T
{
    static async Task OlderFile(string? older)
    {
        if (!File.Exists(Exe)) { Check("olderfile: --exe names SFSearch.exe", false, Exe); return; }
        if (older == null || !File.Exists(older)) { Check("olderfile: --older names an sfsearch.json of an older version", false, older ?? "(none)"); return; }
        string original = File.ReadAllText(older);
        if (original.Contains("settingsVersion")) { Check("olderfile: the file given is an older version's (no settingsVersion)", false, older); return; }
        _pretendFile = Path.Combine(Home, "pretend-pc.txt");
        await RunService(["Service", "stop"], pretend: false);
        Pretend(8192, 2048);
        string cfgPath = Path.Combine(Home, "sfsearch.json"), bak = cfgPath + ".before-v2.bak";
        void Fresh(string text) { File.WriteAllText(cfgPath, text); if (File.Exists(bak)) File.Delete(bak); }
        static string Lines(string cfg) => string.Join(" / ", cfg.Replace("\r", "").Split('\n').Where(l => l.StartsWith("  ") && Regex.IsMatch(l, @"^\s+(Workers|One command|A worker is replaced|sfsearch\.json)")).Select(l => l.Trim()));
        string olderWorkers = Regex.Match(original, @"""workers""\s*:\s*(\d+)").Groups[1].Value, olderMax = Regex.Match(original, @"""workerMaxMB""\s*:\s*(\d+)").Groups[1].Value, olderRecycle = Regex.Match(original, @"""workerRecycleMB""\s*:\s*(\d+)").Groups[1].Value;
        Say($"the older file: workers {olderWorkers}, workerMaxMB {olderMax}, workerRecycleMB {olderRecycle}");

        // 1. Kept as it is (SFSEARCH_KEEP_SETTINGS_FILE=1): the numbers are still taken from the PC, and config says why
        Fresh(original);
        string cfg = (await RunService(["Service", "config"], pretend: true, new() { ["SFSEARCH_KEEP_SETTINGS_FILE"] = "1" })).Out;
        bool fromPc = cfg.Contains("Workers: 2 at once (workers, from this PC") && cfg.Contains($"sfsearch.json has {olderWorkers}, the number older versions wrote there by themselves, so it is taken from this PC)")
            && cfg.Contains("One command: 1.5 GB (workerMaxMB, from this PC") && cfg.Contains($"sfsearch.json has {olderMax}, the number older versions wrote there by themselves")
            && cfg.Contains("above 1 GB (workerRecycleMB, from this PC") && cfg.Contains($"sfsearch.json has {olderRecycle}, the number older versions wrote there by themselves");
        Check("olderfile: the older numbers are taken from the PC (file kept as it is)", fromPc && File.ReadAllText(cfgPath) == original && !File.Exists(bak), Lines(cfg));

        // 2. The first read rewrites the file once: three lines to 0, settingsVersion 2, every other line as it was
        Fresh(original);
        cfg = (await RunService(["Service", "config"], pretend: true)).Out;
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
            cfg.Contains("sfsearch.json was written by an older version: workers") && cfg.Contains("Workers: 2 at once (workers, from this PC")
            && cfg.Contains($"sfsearch.json had {olderWorkers}, an older version's number, until ") && cfg.Contains("One command: 1.5 GB (workerMaxMB, from this PC") && cfg.Contains("above 1 GB (workerRecycleMB, from this PC"),
            Lines(cfg));
        string again = (await RunService(["Service", "config"], pretend: true)).Out;
        Check("olderfile: read again, nothing more changes", File.ReadAllText(cfgPath) == after && again.Contains("Workers: 2 at once (workers, from this PC") && !again.Contains("was written by an older version"), Lines(again));

        // 3. A number the user puts in afterwards is theirs, the older default number included
        File.WriteAllText(cfgPath, Regex.Replace(after, @"""workers"": 0, // was[^\n]*", @"""workers"": 8,"));
        cfg = (await RunService(["Service", "config"], pretend: true)).Out;
        Check("olderfile: after the rewrite, 8 written by the user is kept", cfg.Contains("Workers: 8 at once (workers, set in sfsearch.json)"), Lines(cfg));

        // 4. In an older file, a number that isn't an older default is the user's; only the older ones are rewritten
        Fresh(Regex.Replace(original, @"""workers""\s*:\s*\d+", @"""workers"": 6"));
        cfg = (await RunService(["Service", "config"], pretend: true)).Out;
        after = File.ReadAllText(cfgPath);
        Check("olderfile: an older file's own choice (workers 6) is kept; the other two are rewritten",
            cfg.Contains("Workers: 6 at once (workers, set in sfsearch.json)") && after.Contains("\"workers\": 6,") && cfg.Contains("One command: 1.5 GB (workerMaxMB, from this PC") && Regex.Matches(after, @": 0, // was ").Count == 2,
            Lines(cfg));

        // 5. The service on that PC with the older file runs 2 workers, and its status says so
        Fresh(original);
        var started = await RunService(["Service", "start"], pretend: true);
        try
        {
            string status = started.Out.Contains("Service started") ? await Status() : started.Out + started.Err;
            Check("olderfile: the service on an 8 GB PC with the older file runs 2 workers", status.Contains("up to 2 running at once") && status.Contains("Workers: 2 at once (workers, from this PC"),
                Line(status, "Workers:") + " / " + Line(status, "Workers: 2"));
        }
        finally { await RunService(["Service", "stop"], pretend: true); }
        Fresh(original);
        try { File.Delete(_pretendFile); } catch (IOException) { }
    }
}
