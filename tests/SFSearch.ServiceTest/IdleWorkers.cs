using System.Diagnostics;
using System.Text.RegularExpressions;
using SFSearch.Service;

namespace SFSearch.ServiceTest;

/// <summary>
/// `idleworkers` (2026-11-13, fix S21 of design\agent-first\GAPS.md): workers started while others wait for the heavy
/// lane are retired once idle, and never more commands run at once than the setting. A service must run on the test
/// home (this program puts the test add-on into it for the run); SFSEARCH_WORKERS in this program's environment says how many it was started with
/// (default 4). Three commands that find themselves heavy hold the heavy lane in turn (two wait, each on its worker),
/// while ordinary commands arrive: the service starts more workers for them. Then: while the extra workers are still
/// there, a wave of more commands than the setting runs at most the setting at once; and within the idle time (10 s)
/// plus a few seconds the workers are back to the setting, the service's log saying why.
/// </summary>
public static partial class T
{
    static async Task IdleWorkers()
    {
        int workers = int.TryParse(Environment.GetEnvironmentVariable("SFSEARCH_WORKERS"), out int w) ? w : 4;
        long avail = AvailableMB();
        if (avail < 3000 + 4096 + 2048) { Check("idleworkers: enough memory to run this test", false, $"{avail} MB available, under 9 GB"); return; }
        await UntilStatus(s => s.Contains("Indexer: idle; 0 waiting"), 60000);
        int at0 = (await Workers()).Count;
        Say($"the service runs {workers} commands at once; {at0} workers now");

        // Heavy-lane waits: 3 commands that ask for the lane (two wait on their workers), ordinary commands meanwhile
        var heavy = Enumerable.Range(0, 3).Select(i => Call(new Request { Op = "run", Args = ["__test", "heavy", "3000", "1500"], Agent = $"h{i}", AllowHeavy = true })).ToList();
        await UntilStatus(s => Regex.Matches(s, @"waiting for the heavy lane").Count >= 2, 8000);
        var ordinary = Enumerable.Range(0, workers).Select(i => Call(new Request { Op = "run", Args = ["__test", "sleep", "1500"], Agent = $"o{i}" })).ToList();
        await Task.Delay(400);
        int most = (await Workers()).Count;
        await Task.WhenAll(heavy.Concat(ordinary));
        int afterWaits = (await Workers()).Count;
        Check("idleworkers: workers are started while others wait for the heavy lane", most > workers, $"{most} workers while 2 waited for the heavy lane ({workers} set); {afterWaits} when all were done");

        // While the extra workers are idle: a wave of twice the setting runs at most the setting at once
        var clock = Stopwatch.StartNew();
        var wave = Enumerable.Range(0, workers * 2).Select(i => Call(new Request { Op = "run", Args = ["__test", "sleep", "800"], Agent = $"w{i % 3}" })).ToList();
        var spans = (await Task.WhenAll(wave)).Select(r => Interval(r.Out)).Where(s => s != null).Select(s => s!.Value).ToList();
        int atOnce = spans.Max(a => spans.Count(b => b.From < a.To && a.From < b.To && b.From <= a.From));
        Check("idleworkers: never more commands at once than the setting, extra workers or not", spans.Count == workers * 2 && atOnce <= workers,
            $"{workers * 2} commands of 0.8 s: at most {atOnce} ran at once, {clock.Elapsed.TotalSeconds:0.0} s in all; {(await Workers()).Count} workers alive");

        // Idle: back to the setting within the idle time and a few seconds
        clock.Restart();
        long back = await UntilStatus(s => Regex.Matches(s, @"^\s+worker \d+ \(process \d+\)", RegexOptions.Multiline).Count <= workers, 25000);
        string status = await Status();
        string log = ReadLog();
        int retired = Regex.Matches(log, @"Worker \d+ retired: idle for \d+ s, one more than the \d+ workers set").Count;
        Check("idleworkers: the extra workers are retired once idle", back >= 0 && retired >= 1 && status.Contains("started while others waited retired once idle"),
            $"{(back >= 0 ? $"{workers} workers after {back / 1000.0:0.0} s" : "still above the setting after 25 s")}; the log names {retired} retired as idle; {Line(status, "Workers:")}");

        // And the service still answers on the workers left
        var r = await Call(Cmd("__test", "sleep", "100"));
        Check("idleworkers: a command afterwards runs", r.Code == 0 && r.Out.StartsWith("slept 100 ms"), r.Out.Trim());
    }
}
