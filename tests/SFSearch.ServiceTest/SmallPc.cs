using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SFSearch.Service;

namespace SFSearch.ServiceTest;

/// <summary>
/// `smallpc` (2026-11-12, fix S17 of design\agent-first\GAPS.md): the service on a pretend 8 GB and a pretend 16 GB PC.
/// It starts and stops its own service on the test home, with SFSEARCH_TEST_HOOKS=1 and SFSEARCH_TEST_MEMORY naming a
/// small file (MemoryInfo.cs): the PC's memory, what other programs hold, and a scale. The service counts each worker's
/// real memory times the scale, so the test takes little real memory (at most about 0.8 GB at once): the pretend numbers
/// are scaled down, not the PC up. Checked: the defaults it picks from the PC; a command past the limit for one command
/// is stopped; with memory made to fall under the floor while two commands run, the indexer gives way first, then the
/// bigger command is stopped with the one clear line and exit code 8, the other finishes, and the stopped one can be
/// asked again. Also: a set value is kept, and on the real PC (64 GB) the defaults are what was fixed before.
/// </summary>
public static partial class T
{
    const int Scale = 4;
    const string ShortLine = @"^Error: the PC was short of memory \(\d+(\.\d)? GB free\), so this command was stopped\. Fix: ask again; close other programs if it happens again\.\s*$";

    sealed record PcCase(int Gb, int Workers, string CapGb, int CapMB, string BudgetGb, string RecycleGb, string FloorGb, int FloorMB);

    static string _pretendFile = "";

    static async Task SmallPc()
    {
        if (!File.Exists(Exe)) { Check("smallpc: --exe names SFSearch.exe", false, Exe); return; }
        _pretendFile = Path.Combine(Home, "pretend-pc.txt");
        await RunService(["Service", "stop"], pretend: false);

        // The real PC: the defaults must be what they were before (8 workers, 6 GB, 3 GB) on a PC of 48 GB or more
        string real = (await RunService(["Service", "config"], pretend: false)).Out;
        Say("this PC: " + Line(real, "Memory settings:"));
        if (TotalMB() >= 48 * 1024 && Environment.ProcessorCount >= 10)
            Check("smallpc: on this PC the defaults are what they were before", real.Contains("Workers: 8 at once (workers, from this PC") && real.Contains("One command: 6 GB (workerMaxMB, from this PC")
                && real.Contains("above 3 GB (workerRecycleMB, from this PC") && Regex.IsMatch(real, @"The workers together: [\d.]+ GB \(serviceMaxMB, from this PC: half its memory\)"),
                string.Join(" / ", real.Replace("\r", "").Split('\n').Where(l => l.StartsWith("  ")).Select(l => l.Trim()).Take(4)));
        else Say($"this PC has {TotalMB()} MB and {Environment.ProcessorCount} cores: the check of the 64 GB PC's defaults is skipped");

        var cases = new[]
        {
            new PcCase(8, 2, "1.5", 1536, "4", "1", "1.5", 1536),
            new PcCase(16, 4, "2.7", 2730, "8", "1.3", "1.5", 1536),
        };
        foreach (var pc in cases)
        {
            Console.WriteLine($"--- a pretend {pc.Gb} GB PC (16 cores; each worker's real memory counted {Scale} times)");
            await OnePc(pc);
        }
        await RunService(["Service", "stop"], pretend: false);
        try { File.Delete(_pretendFile); } catch (IOException) { }
    }

    static void Pretend(long totalMB, long othersMB) =>
        File.WriteAllText(_pretendFile, $"total={totalMB} others={othersMB} cores=16 scale={Scale}\n");

    /// <summary>SFSearch.exe with the pretend PC (or without), and none of the memory settings set by the environment.</summary>
    static async Task<(string Out, string Err, int Code)> RunService(string[] args, bool pretend, Dictionary<string, string>? extra = null)
    {
        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["SFSEARCH_HOME"] = Home;
        foreach (var name in new[] { "SFSEARCH_WORKERS", "SFSEARCH_WORKER_MAX_MB", "SFSEARCH_WORKER_RECYCLE_MB", "SFSEARCH_SERVICE_MAX_MB", "SFSEARCH_MEMORY_FLOOR_MB", "SFSEARCH_TEST_MEMORY" })
            psi.Environment.Remove(name);
        psi.Environment["SFSEARCH_TEST_HOOKS"] = "1";
        psi.Environment["SFSEARCH_WARMUP"] = "0";
        psi.Environment["SFSEARCH_INDEX"] = "0"; // the indexer lane gets the test's own job, not a real index build
        if (pretend) psi.Environment["SFSEARCH_TEST_MEMORY"] = _pretendFile;
        foreach (var kv in extra ?? new()) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (await o, await e, p.ExitCode);
    }

    static string Line(string text, string start) => text.Replace("\r", "").Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(start))?.Trim() ?? "";

    static long Together(string status) => Regex.Match(status, @"the workers together hold (\d+) MB") is { Success: true } m ? long.Parse(m.Groups[1].Value) : -1;

    static async Task OnePc(PcCase pc)
    {
        long total = pc.Gb * 1024L;
        long others = total / 4;
        Pretend(total, others);
        string tag = $"{pc.Gb} GB PC";

        // 1. The defaults it picks, and a set value kept as set
        string cfg = (await RunService(["Service", "config"], pretend: true)).Out;
        bool defaults = cfg.Contains($"Memory settings: this PC has {pc.Gb} GB and 16 cores (a test's pretend PC")
            && cfg.Contains($"The workers together: {pc.BudgetGb} GB (serviceMaxMB, from this PC: half its memory)")
            && cfg.Contains($"Workers: {pc.Workers} at once (workers, from this PC")
            && cfg.Contains($"One command: {pc.CapGb} GB (workerMaxMB, from this PC")
            && cfg.Contains($"above {pc.RecycleGb} GB (workerRecycleMB, from this PC")
            && cfg.Contains($"The floor: {pc.FloorGb} GB available (memoryFloorMB, from this PC");
        Check($"smallpc {tag}: the defaults it picks", defaults, string.Join(" / ", cfg.Replace("\r", "").Split('\n').Where(l => l.StartsWith("  ") && !l.Contains("Plugins:") && !l.Contains("Types:") && !l.Contains("After a game")).Select(l => Regex.Replace(l.Trim(), @" \(\w+, from this PC[^)]*\)", " (from this PC)"))));
        string set = (await RunService(["Service", "config"], pretend: true, new() { ["SFSEARCH_WORKERS"] = "3", ["SFSEARCH_WORKER_MAX_MB"] = "5000" })).Out;
        Check($"smallpc {tag}: a value that is set is kept as set", set.Contains("Workers: 3 at once (workers, set by SFSEARCH_WORKERS)") && set.Contains("One command: 4.9 GB (workerMaxMB, set by SFSEARCH_WORKER_MAX_MB)")
            && set.Contains($"above 2.4 GB (workerRecycleMB, from this PC"), Line(set, "Workers: 3") + " / " + Line(set, "One command:"));

        // The service, on the pretend PC
        var started = await RunService(["Service", "start"], pretend: true);
        if (!started.Out.Contains("Service started")) { Check($"smallpc {tag}: the service starts", false, started.Out + started.Err); return; }
        try
        {
            string status = await Status();
            Check($"smallpc {tag}: Service status says what is in effect and where it came from",
                status.Contains($"Workers: {pc.Workers} at once (workers, from this PC") && status.Contains($"One command: {pc.CapGb} GB (workerMaxMB, from this PC") && status.Contains("Since start: the PC ran short 0 time(s)"),
                Line(status, "Memory:"));

            // 2. A command that grows past the limit for one command is stopped
            int capReal = pc.CapMB / Scale + 128;
            var sw = Stopwatch.StartNew();
            var r = await Call(Cmd("__test", "alloc", capReal.ToString(), "8000"));
            Check($"smallpc {tag}: a command past the limit for one command ({pc.CapGb} GB) is stopped", r.Code == 5 && r.Err.Contains($"it used more than {pc.CapGb} GB of memory, this PC's limit for one command") && sw.ElapsedMilliseconds < 6000,
                $"{capReal} MB real, counted as {capReal * Scale} MB: after {sw.ElapsedMilliseconds} ms, exit {r.Code}: {r.Err.Trim()}");

            // 3. Memory falls under the floor while two commands run (and, on the PC with 4 workers, the indexer's job)
            bool withIndexer = pc.Workers >= 4;
            int bigReal = pc.Gb == 8 ? 256 : 384, smallReal = 64;
            var big = Call(new Request { Op = "run", Args = ["__test", "alloc", bigReal.ToString(), "10000"], Agent = "big", WaitMs = -1, Structured = pc.Gb != 8 });
            var small = Call(new Request { Op = "run", Args = ["__test", "alloc", smallReal.ToString(), "10000"], Agent = "small", WaitMs = -1 });
            // Both running and holding their memory
            long waited = await UntilStatus(s => Regex.Matches(s, @"running \d+ s, \d+ done, (\d+) MB — \[(big|small)\]").Count(m => long.Parse(m.Groups[1].Value) >= (m.Groups[2].Value == "big" ? bigReal : smallReal) * Scale) == 2, 8000);
            Task<Reply>? idx = null;
            if (withIndexer)
            {
                // The indexer's job, on a worker of its own (no agent is waiting, so it isn't asked to give way for one)
                idx = Call(new Request { Op = "run", Args = ["__test", "yieldable", "8000"], Lane = "index", WaitMs = -1 });
                if (await UntilStatus(s => s.Contains("Indexer: running"), 8000) < 0) Say("the indexer's job didn't show as running");
                await Task.Delay(500);
            }
            status = await Status();
            long held = Together(status);
            long floor = pc.FloorMB;
            long newOthers = total - held - (floor - 400);
            Say($"both commands hold their memory after {waited} ms; the workers together hold {held} MB (counted); other programs now take {newOthers} MB, so {floor - 400} MB is left, under the floor of {floor} MB");
            var shortAt = DateTime.Now;
            Pretend(total, newOthers);
            var bigReply = await big;
            long stoppedAfter = (long)(DateTime.Now - shortAt).TotalMilliseconds;
            string bigText = pc.Gb == 8 ? bigReply.Err : bigReply.Failure?.Message ?? "";
            Check($"smallpc {tag}: the bigger command is stopped with the clear line, exit code 8", bigReply.Code == 8 && Regex.IsMatch(bigText, ShortLine)
                && (pc.Gb == 8 || bigReply.Failure?.Fix == "ask again; close other programs if it happens again."),
                $"{stoppedAfter} ms after memory fell: \"{bigText.Trim()}\"{(pc.Gb == 8 ? " (old form: in err)" : $" (structured: fix \"{bigReply.Failure?.Fix}\")")}");
            var smallReply = await small;
            Check($"smallpc {tag}: the other command finishes", smallReply.Code == 0 && smallReply.Out.Trim() == $"held {smallReal} MB", $"exit {smallReply.Code}: {smallReply.Out.Trim()}{smallReply.Err.Trim()}");
            status = await Status();
            Check($"smallpc {tag}: one command stopped, not two", Regex.IsMatch(status, @"the PC ran short 1 time\(s\).*, 1 command\(s\) stopped for it"), Line(status, "Memory:"));
            if (withIndexer)
            {
                // The order, from the service's log: the indexer was asked to give way before the command was stopped
                string log = ReadLog();
                // The log is the day's, shared with the earlier pretend PC: the last of each line is this run's
                var gaveWay = Regex.Matches(log, @"(\d\d:\d\d:\d\d\.\d\d\d) job \S+ \[~index\] asked to give way: the PC is short of memory").LastOrDefault() ?? Match.Empty;
                var stopping = Regex.Matches(log, @"(\d\d:\d\d:\d\d\.\d\d\d) Memory: \d+ MB available, under the floor of \d+ MB: stopping job \S+ \[big\]").LastOrDefault() ?? Match.Empty;
                Check($"smallpc {tag}: the indexer gives way first", gaveWay.Success && stopping.Success && string.CompareOrdinal(gaveWay.Groups[1].Value, stopping.Groups[1].Value) < 0 && status.Contains("the indexer or warm-up gave way 1 time(s)"),
                    $"asked to give way at {gaveWay.Groups[1].Value}, the command stopped at {stopping.Groups[1].Value}");
            }

            // Memory comes back: the stopped command can be asked again, and the indexer's job runs again and finishes
            Pretend(total, others);
            r = await Call(Cmd("__test", "alloc", bigReal.ToString(), "500"));
            Check($"smallpc {tag}: the stopped command can be asked again", r.Code == 0 && r.Out.Trim() == $"held {bigReal} MB", $"exit {r.Code}: {r.Out.Trim()}");
            if (idx != null)
            {
                var ir = await idx;
                Check($"smallpc {tag}: the indexer's job runs again once memory is back, and finishes", ir.Code == 0 && ir.Out.StartsWith("indexed for 8000 ms"), ir.Out.Trim());
            }
        }
        finally
        {
            Pretend(total, others);
            await RunService(["Service", "stop"], pretend: true);
        }
    }

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
