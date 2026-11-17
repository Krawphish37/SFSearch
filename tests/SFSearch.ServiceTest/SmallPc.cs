using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SFSearch.Service;

namespace SFSearch.ServiceTest;

/// <summary>
/// `smallpc --exe &lt;SFSearch.exe&gt; [--records N]` (2026-11-12, fix S17 of design\agent-first\GAPS.md; J37 2026-11-15:
/// on the real PC and real settings, no pretend PC). Two parts:
///   1. The memory defaults a PC gets (<see cref="ServiceConfig.PcDefaults"/>), called with the numbers of an 8, 16, 32
///      and 64 GB PC and of one whose memory can't be read; and `Service config` on this PC says what they give here.
///   2. The limits, as real sfsearch.json settings of its own home (test Data folder: the indexer test's walk plugin; the
///      test holds the walk's lock from before the start, as the indexer test does, so the start's link walk job is in
///      the indexer lane meanwhile): a value set is kept as set; a command past the limit for one command
///      (workerMaxMB) is stopped; and with memoryFloorMB raised above what Windows has available while two commands
///      hold memory and the link walk runs, the indexer gives way first, then the bigger command is stopped with the one
///      clear line and exit code 8, the other finishes; with the floor back, the stopped one can be asked again and the
///      walk finishes. It takes about 2.8 GB of real memory at most.
/// Stops its service at the end.
/// </summary>
public static partial class T
{
    const string ShortLine = @"^Error: the PC was short of memory \(\d+(\.\d)? GB free\), so this command was stopped\. Fix: ask again; close other programs if it happens again\.\s*$";

    sealed record PcCase(long TotalMB, int Cores, int Workers, int WorkerMaxMB, int RecycleMB, int ServiceMaxMB, int FloorMB);

    static async Task SmallPc(int records)
    {
        if (!File.Exists(Exe)) { Check("smallpc: --exe names SFSearch.exe", false, Exe); return; }

        // 1. The defaults, from the numbers alone
        var cases = new[]
        {
            new PcCase(8192, 16, 2, 1536, 1024, 4096, 1536),
            new PcCase(16384, 16, 4, 2730, 1365, 8192, 1536),
            new PcCase(32768, 4, 2, 5461, 2730, 16384, 1638),
            new PcCase(65536, 16, 8, 6144, 3072, 32768, 3276),
            new PcCase(-1, 8, 6, 5461, 2730, 16384, 1536),
        };
        foreach (var pc in cases)
        {
            int budget = ServiceConfig.PcDefaults.ServiceMaxMB(pc.TotalMB), workers = ServiceConfig.PcDefaults.Workers(budget, pc.Cores);
            int max = ServiceConfig.PcDefaults.WorkerMaxMB(budget), recycle = ServiceConfig.PcDefaults.WorkerRecycleMB(max), floor = ServiceConfig.PcDefaults.MemoryFloorMB(pc.TotalMB);
            string name = pc.TotalMB > 0 ? $"a {pc.TotalMB / 1024} GB PC with {pc.Cores} cores" : $"a PC whose memory can't be read ({pc.Cores} cores)";
            Check($"smallpc: the defaults of {name}", (budget, workers, max, recycle, floor) == (pc.ServiceMaxMB, pc.Workers, pc.WorkerMaxMB, pc.RecycleMB, pc.FloorMB),
                $"serviceMaxMB {budget}, workers {workers}, workerMaxMB {max}, workerRecycleMB {recycle}, memoryFloorMB {floor} (expected {pc.ServiceMaxMB}, {pc.Workers}, {pc.WorkerMaxMB}, {pc.RecycleMB}, {pc.FloorMB})");
        }

        string root = Path.Combine(Home, "smallpc"), home = Path.Combine(root, "home"), data = Path.Combine(root, "plugins"), txt = Path.Combine(root, "plugins.txt");
        if (Path.GetFullPath(data).Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)) { Check("smallpc: the test Data folder is outside the game's", false, data); return; }
        string outer = Home;
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(home); Directory.CreateDirectory(data);
        WriteWalkPlugin(Path.Combine(data, WalkPlugin), records);
        File.WriteAllLines(txt, new[] { "*" + WalkPlugin });
        InstallAddOn(home);
        var env = new Dictionary<string, string> { ["SFSEARCH_HOME"] = home, ["SFSEARCH_DATA"] = data, ["SFSEARCH_PLUGINS_TXT"] = txt };
        string cfgPath = Path.Combine(home, "sfsearch.json");
        Home = home;
        var hold = new WalkHold(home);
        try
        {
            // This PC's defaults, as Service config words them (a settings file with no memory number: each from the PC)
            File.WriteAllText(cfgPath, "{ \"settingsVersion\": 2 }");
            string real = (await RunService(env, "Service", "config")).Out;
            long total = TotalMB();
            int budget = ServiceConfig.PcDefaults.ServiceMaxMB(total), pcWorkers = ServiceConfig.PcDefaults.Workers(budget, Environment.ProcessorCount);
            int pcMax = ServiceConfig.PcDefaults.WorkerMaxMB(budget);
            Check("smallpc: Service config gives this PC's defaults, and says they come from the PC",
                real.Contains($"Workers: {pcWorkers} at once (workers, from this PC") && real.Contains($"One command: {MemoryInfo.GB(pcMax)} GB (workerMaxMB, from this PC")
                && real.Contains($"The workers together: {MemoryInfo.GB(budget)} GB (serviceMaxMB, from this PC: half its memory)"),
                $"this PC: {total} MB, {Environment.ProcessorCount} cores: {Line(real, "Workers:")} / {Line(real, "One command:")}");

            // 2. Real settings: 3 workers, 2.5 GB a command
            const int capMB = 2560;
            Settings(cfgPath, capMB, floorMB: 0);
            string set = (await RunService(env, "Service", "config")).Out;
            Check("smallpc: a value that is set is kept as set", set.Contains("Workers: 3 at once (workers, set in sfsearch.json)") && set.Contains("One command: 2.5 GB (workerMaxMB, set in sfsearch.json)")
                && set.Contains("above 1.3 GB (workerRecycleMB, from this PC"), Line(set, "Workers: 3") + " / " + Line(set, "One command:"));
            var started = await RunService(env, "Service", "start");
            if (!started.Out.Contains("Service started")) { Check("smallpc: the service starts", false, started.Out + started.Err); return; }
            string status = await Status();
            Check("smallpc: Service status says what is in effect and where it came from",
                status.Contains("Workers: 3 at once (workers, set in sfsearch.json)") && status.Contains("One command: 2.5 GB (workerMaxMB, set in sfsearch.json)") && status.Contains("Since start: the PC ran short 0 time(s)"),
                Line(status, "Memory:"));

            // Memory falls under the floor while two commands hold memory and the start's link walk runs
            if (await UntilStatus(s => Regex.IsMatch(s, @"\[~index\][^\n]*__links build"), 30000) < 0) Say("the link walk isn't running: the indexer's part of this check is left out");
            const int bigMB = 2048, smallMB = 64;
            var big = Call(new Request { Op = "run", Args = ["__test", "alloc", bigMB.ToString(), "12000"], Agent = "big", WaitMs = -1, Structured = true });
            var small = Call(new Request { Op = "run", Args = ["__test", "alloc", smallMB.ToString(), "12000"], Agent = "small", WaitMs = -1 });
            long waited = await UntilStatus(s => Regex.Matches(s, @"running \d+ s, \d+ done, (\d+) MB — \[(big|small)\]").Count(m => long.Parse(m.Groups[1].Value) >= (m.Groups[2].Value == "big" ? bigMB : smallMB)) == 2, 10000);
            status = await Status();
            bool walking = Regex.IsMatch(status, @"\[~index\][^\n]*__links build");
            long avail = AvailableMB();
            // The floor at the PC's whole memory: Windows never has that much available, so the PC stays "short" until the
            // floor is put back, whatever other programs do meanwhile. (A floor just above what is available, as before,
            // was crossed back when another program let go of a few GB, and then rightly nothing was stopped.)
            long floor = TotalMB();
            Say($"both commands hold their memory after {waited} ms{(walking ? ", the link walk running" : ", the link walk not running")}; Windows has {avail} MB available: memoryFloorMB set to {floor}");
            var shortAt = DateTime.Now;
            Settings(cfgPath, capMB, floor);
            var bigReply = await big;
            long stoppedAfter = (long)(DateTime.Now - shortAt).TotalMilliseconds;
            string bigText = bigReply.Failure?.Message ?? "";
            Check("smallpc: the bigger command is stopped with the clear line, exit code 8", bigReply.Code == 8 && Regex.IsMatch(bigText, ShortLine) && bigReply.Failure?.Fix == "ask again; close other programs if it happens again.",
                $"{stoppedAfter} ms after the floor was raised: \"{bigText.Trim()}\" (fix \"{bigReply.Failure?.Fix}\")");
            var smallReply = await small;
            Check("smallpc: the other command finishes", smallReply.Code == 0 && smallReply.Out.Trim() == $"held {smallMB} MB", $"exit {smallReply.Code}: {smallReply.Out.Trim()}{smallReply.Err.Trim()}");
            status = await Status();
            Check("smallpc: one command stopped, not two", Regex.IsMatch(status, @"the PC ran short \d+ time\(s\).*, 1 command\(s\) stopped for it"), Line(status, "Memory:"));
            if (walking)
            {
                // The order, from the service's log: the indexer was asked to give way before the command was stopped
                string log = ReadLog();
                // The stop, and the indexer's give-way before it (it may be asked again after the stop, while the floor stays up)
                var stopping = Regex.Matches(log, @"(\d\d:\d\d:\d\d\.\d\d\d) Memory: \d+ MB available, under the floor of \d+ MB: stopping job \S+ \[big\]").LastOrDefault() ?? Match.Empty;
                var gaveWay = Regex.Matches(log, @"(\d\d:\d\d:\d\d\.\d\d\d) job \S+ \[~index\] asked to give way: the PC is short of memory")
                    .LastOrDefault(m => stopping.Success && string.CompareOrdinal(m.Groups[1].Value, stopping.Groups[1].Value) <= 0) ?? Match.Empty;
                Check("smallpc: the indexer gives way first", gaveWay.Success && stopping.Success && string.CompareOrdinal(gaveWay.Groups[1].Value, stopping.Groups[1].Value) <= 0 && Regex.IsMatch(status, @"the indexer gave way [1-9]\d* time\(s\) for it"),
                    $"asked to give way at {gaveWay.Groups[1].Value}, the command stopped at {stopping.Groups[1].Value}; {Line(status, "Memory:")}");
            }

            // The floor back as it was: the stopped command can be asked again, and the walk, its lock let go, finishes
            Settings(cfgPath, capMB, floorMB: 0);
            hold.Dispose();
            await Task.Delay(1500);
            var r = await Call(Cmd("__test", "alloc", bigMB.ToString(), "500"));
            Check("smallpc: the stopped command can be asked again", r.Code == 0 && r.Out.Trim() == $"held {bigMB} MB", $"exit {r.Code}: {r.Out.Trim()} {r.Err.Trim()}");
            string linked = "";
            var until = Stopwatch.StartNew();
            while (until.Elapsed.TotalSeconds < 120 && (linked = Regex.Match(ReadLog(), @"Indexer: link index: 1 of 1 plugins have a link file[^\n]*").Value).Length == 0) await Task.Delay(200);
            Check("smallpc: the link walk goes on once memory is back, and finishes", linked.Contains(" 0 left"), Cut(linked, 300));

            // A command that grows past the limit for one command is stopped
            var sw = Stopwatch.StartNew();
            r = await Call(Cmd("__test", "alloc", (capMB + 256).ToString(), "8000"));
            Check("smallpc: a command past the limit for one command (2.5 GB) is stopped", r.Code == 5 && r.Err.Contains("it used more than 2.5 GB of memory, this PC's limit for one command") && sw.ElapsedMilliseconds < 7000,
                $"{capMB + 256} MB: after {sw.ElapsedMilliseconds} ms, exit {r.Code}: {r.Err.Trim()}");
        }
        finally
        {
            hold.Dispose();
            await RunService(env, "Service", "stop");
            Home = outer;
        }
    }

    /// <summary>The test home's sfsearch.json: 3 workers, one command's limit, and the floor (0: from this PC).</summary>
    static void Settings(string path, int workerMaxMB, long floorMB) =>
        File.WriteAllText(path, $"{{ \"settingsVersion\": 2, \"workers\": 3, \"workerMaxMB\": {workerMaxMB}, \"memoryFloorMB\": {floorMB}, \"keepStartupIndexes\": false }}");

    /// <summary>SFSearch.exe with these environment variables, and none of the memory settings set by the environment.</summary>
    static async Task<(string Out, string Err, int Code)> RunService(Dictionary<string, string> env, params string[] args)
    {
        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var name in new[] { "SFSEARCH_WORKERS", "SFSEARCH_WORKER_MAX_MB", "SFSEARCH_WORKER_RECYCLE_MB", "SFSEARCH_SERVICE_MAX_MB", "SFSEARCH_MEMORY_FLOOR_MB" })
            psi.Environment.Remove(name);
        foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (await o, await e, p.ExitCode);
    }

    static string Line(string text, string start) => text.Replace("\r", "").Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(start))?.Trim() ?? "";

    static string ReadLog()
    {
        string dir = Path.Combine(Home, "Service", "logs");
        var f = Directory.Exists(dir) ? Directory.GetFiles(dir, "service-*.log").OrderBy(x => x).LastOrDefault() : null;
        if (f == null) return "";
        using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
