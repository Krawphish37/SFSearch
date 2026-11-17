using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SFSearch.ServiceTest;

/// <summary>
/// `smallgc --exe &lt;SFSearch.exe&gt; --data &lt;Starfield's Data folder&gt;` (J57, GAPS S84, 2026-11-15): an 8 GB PC's
/// settings on the server garbage collector. Its own home; the Data folder is only read. The service runs with the
/// numbers an 8 GB PC gets from ServiceConfig.PcDefaults (SFSEARCH_WORKER_MAX_MB 1536, SFSEARCH_SERVICE_MAX_MB 4096,
/// SFSEARCH_WORKERS 2, SFSEARCH_WORKER_RECYCLE_MB 1024: the real settings' variables) and on 8 logical cores (started
/// through `start /affinity FF`; the workers inherit it), workerGarbageCollector left at auto. Checks:
///   1. Service config says the workers run server and a command without the service workstation;
///   2. with workerGarbageCollector "server" (so that the check holds whatever auto picks), every cell of Starfield.esm
///      (`query2 ... FROM Cell ...`) finishes on a server worker: the coordinator stops a command above 1.5 GB, and a
///      worker holds its heap under that (J57). Before J57 such a worker was stopped in 4 to 7 s (1.56 to 1.77 GB).
/// About 2 to 3 minutes (the first start makes the header and link index of the Data folder). Run it through heavy.sh.
/// Stops its service at the end.
/// </summary>
public static partial class T
{
    static async Task SmallGc(string? data)
    {
        if (!File.Exists(Exe)) { Check("smallgc: --exe names SFSearch.exe", false, Exe); return; }
        if (data == null || !File.Exists(Path.Combine(data, "Starfield.esm"))) { Check("smallgc: --data names Starfield's Data folder", false, data ?? "(none)"); return; }
        string home = Path.Combine(Home, "smallgc"), outer = Home;
        Directory.CreateDirectory(home);
        var env = new Dictionary<string, string>
        {
            ["SFSEARCH_HOME"] = home, ["SFSEARCH_DATA"] = data,
            ["SFSEARCH_WORKER_MAX_MB"] = "1536", ["SFSEARCH_SERVICE_MAX_MB"] = "4096", ["SFSEARCH_WORKERS"] = "2", ["SFSEARCH_WORKER_RECYCLE_MB"] = "1024",
        };
        Home = home;
        try
        {
            await RunWith(env, "Service", "stop");
            string settings = Path.Combine(home, "sfsearch.json");
            if (File.Exists(settings)) File.WriteAllText(settings, Set(File.ReadAllText(settings), "auto")); // a home used before
            var cfg = await RunWith(env, "Service", "config");
            Check("smallgc: with an 8 GB PC's 1.5 GB a command, auto runs server in the workers and workstation in a command without the service",
                cfg.Out.Contains("Commands' garbage collector: server in the workers, workstation in a command run without the service (workerGarbageCollector, auto:"),
                Line(cfg.Out, "Commands' garbage collector"));
            File.WriteAllText(settings, Set(File.ReadAllText(settings), "server"));

            // The service on 8 cores: `start /affinity` gives the starting process the mask, and the service and its workers inherit it
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "/c", "start", "\"\"", "/b", "/wait", "/affinity", "FF", Exe, "Service", "start" }) psi.ArgumentList.Add(a);
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
            using (var p = Process.Start(psi)!) { await p.StandardOutput.ReadToEndAsync(); await p.WaitForExitAsync(); }
            var clock = Stopwatch.StartNew();
            string status = "";
            while (clock.Elapsed.TotalSeconds < 240)
            {
                status = await Status();
                if (Regex.IsMatch(status, @"Indexer: idle; 0 waiting") && !status.Contains("starting")) break;
                await Task.Delay(500);
            }
            var who = await Call(Cmd("status"));
            if (!OpenIn(who.Out, "the worker that answered this", "server")) { Check("smallgc: the workers run the server collector", false, Line(who.Out, "Open in")); return; }
            var cells = Stopwatch.StartNew();
            var r = await Call(Cmd("query2", "Starfield.esm", "SELECT count(*) FROM Cell WHERE Flags IS NOT NULL;", "--allow-heavy"));
            Check("smallgc: every cell of Starfield.esm on a server worker with 1.5 GB a command finishes (the worker holds its heap under the limit)",
                r.Code == 0 && Regex.IsMatch(r.Out, @"(?m)^\| \d+ \|"),
                $"exit {r.Code} in {cells.Elapsed.TotalSeconds:0.0} s; {Line(r.Out, "|")} {Line(r.Out, "[cost]")} {r.Err.Trim()}");
        }
        finally
        {
            await RunWith(env, "Service", "stop");
            Home = outer;
        }
    }
}
