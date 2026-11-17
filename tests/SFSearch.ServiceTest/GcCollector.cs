using System.Text.RegularExpressions;

namespace SFSearch.ServiceTest;

/// <summary>
/// `gccollector --exe &lt;SFSearch.exe&gt;` (J44, 2026-11-15): the workers' garbage collector (workerGarbageCollector in
/// sfsearch.json). Its own home and test Data folder under the test home, with one small plugin Mutagen writes there.
/// The collector is read from a real command's answer: `status` run by a worker says which collector that worker runs
/// (it reads System.Runtime.GCSettings.IsServerGC in its own process). No test hook: the memory a command may use is
/// set by SFSEARCH_WORKER_MAX_MB, the real setting's variable. Checks:
///   1. a command run without the service follows the setting too (J59): auto on this PC (one command may use 2.5 GB
///      or more: about 15 GB of memory or more) runs server; auto with 1.5 GB a command (an 8 GB PC) and the setting
///      "workstation" run workstation (the command starts itself again on it), and its exit code comes through;
///      "server" with 1.5 GB a command runs server;
///   2. the file the service writes explains the setting and has it at "auto";
///   3. the setting left out (auto) where one command may use 2.5 GB or more (4 GB here): the workers run the server
///      collector, and `Service config` says why;
///   4. auto where one command may use 1.5 GB (an 8 GB PC's default): the workers server too (J57: a server worker
///      allowed under 2.5 GB holds its heap under that limit), a command without the service workstation;
///   5. "workstation" with 4 GB a command: workstation; "server" with 1.5 GB a command: server (the user's word is kept);
///   6. a word that is none of them: read as auto, and `Service config` says what it was;
///   7. the service itself runs workstation whatever the setting (its log's start line, checked with 3 to 5);
///   8. the link walk of a million records or more runs on the workstation collector (J58, GAPS S85): with server
///      workers the start's link job hands it on, the coordinator starts a worker on workstation for it, that worker
///      walks it and is retired after; with "workstation" workers it is walked where it lands. Its own test Data folder
///      with a plugin of 1,000,000 keywords (about 40 MB) Mutagen writes there.
/// Stops its service at the end.
/// </summary>
public static partial class T
{
    static async Task GcCollector()
    {
        if (!File.Exists(Exe)) { Check("gccollector: --exe names SFSearch.exe", false, Exe); return; }
        string root = Path.Combine(Home, "gccollector"), home = Path.Combine(root, "home"), data = Path.Combine(root, "plugins"), txt = Path.Combine(root, "plugins.txt");
        if (Path.GetFullPath(data).Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)) { Check("gccollector: the test Data folder is outside the game's", false, data); return; }
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(home); Directory.CreateDirectory(data);
        WriteMaster(data, small: false);
        File.WriteAllLines(txt, new[] { "*" + KMaster });
        var env = new Dictionary<string, string> { ["SFSEARCH_HOME"] = home, ["SFSEARCH_DATA"] = data, ["SFSEARCH_PLUGINS_TXT"] = txt, ["SFSEARCH_WORKERS"] = "2" };
        var big = new Dictionary<string, string>(env) { ["SFSEARCH_WORKER_MAX_MB"] = "4096" };
        var small = new Dictionary<string, string>(env) { ["SFSEARCH_WORKER_MAX_MB"] = "1536" };
        string settings = Path.Combine(home, "sfsearch.json");
        string outer = Home;
        Home = home;
        try
        {
            // 1. No service: the command runs in its own process, on the collector the setting picks (J59)
            string here = TotalMB() >= 15 * 1024 ? "server" : "workstation";
            var plain = await RunWith(env, "status");
            Check($"gccollector: a command run without the service, auto on this PC ({TotalMB() / 1024.0:0.0} GB): {here}",
                plain.Code == 0 && OpenIn(plain.Out, "this program", here), Line(plain.Out, "Open in"));
            var plainSmall = await RunWith(small, "status");
            Check("gccollector: a command run without the service, auto with 1.5 GB a command (an 8 GB PC): workstation",
                plainSmall.Code == 0 && OpenIn(plainSmall.Out, "this program", "workstation"), Line(plainSmall.Out, "Open in"));
            File.WriteAllText(settings, "{ \"workerGarbageCollector\": \"workstation\" }");
            var plainWs = await RunWith(big, "status");
            var plainBad = await RunWith(big, "nosuchverb");
            Check("gccollector: a command run without the service, \"workstation\" with 4 GB a command: workstation; its exit code comes through",
                plainWs.Code == 0 && OpenIn(plainWs.Out, "this program", "workstation") && plainBad.Code == 2 && (plainBad.Out + plainBad.Err).Contains("there is no verb or command named 'nosuchverb'"),
                $"{Line(plainWs.Out, "Open in")}; nosuchverb: exit {plainBad.Code}");
            File.WriteAllText(settings, "{ \"workerGarbageCollector\": \"server\" }");
            var plainSv = await RunWith(small, "status");
            Check("gccollector: a command run without the service, \"server\" with 1.5 GB a command: server",
                plainSv.Code == 0 && OpenIn(plainSv.Out, "this program", "server"), Line(plainSv.Out, "Open in"));
            File.Delete(settings);

            // 2. The file the service writes: the setting explained, at auto
            await RunWith(env, "Service", "config"); // writes the default file when there is none
            string text = File.ReadAllText(settings);
            Check("gccollector: the settings file the service writes explains the setting and has it at auto",
                Regex.IsMatch(text, @"""workerGarbageCollector"":\s*""auto""") && Regex.IsMatch(text, @"// workerGarbageCollector: how a command cleans up the memory") && text.Contains("Values: \"server\""), Line(text, "\"workerGarbageCollector\""));

            // 3. Left out, where one command may use 4 GB: server
            File.WriteAllText(settings, Regex.Replace(text, @"(?m)^\s*""workerGarbageCollector"".*\r?\n", ""));
            await Collector(big, "server", "server", "the setting left out, 4 GB a command", "auto: the workers server (under 2.5 GB a command, each holds .NET's memory under it); a command without the service server, as one command may use 4 GB here, at least the 2.5 GB it needs unheld");

            // 4. Auto where one command may use 1.5 GB: workstation
            File.WriteAllText(settings, Set(File.ReadAllText(settings), "auto"));
            await Collector(small, "server", "workstation", "auto, 1.5 GB a command", "auto: the workers server (under 2.5 GB a command, each holds .NET's memory under it); a command without the service workstation, as one command may use 1.5 GB here, under the 2.5 GB it needs unheld");

            // 5. The user's word is kept whatever the memory
            File.WriteAllText(settings, Set(File.ReadAllText(settings), "workstation"));
            await Collector(big, "workstation", "workstation", "the setting \"workstation\", 4 GB a command", "set in sfsearch.json");
            File.WriteAllText(settings, Set(File.ReadAllText(settings), "server"));
            await Collector(small, "server", "server", "the setting \"server\", 1.5 GB a command", "set in sfsearch.json");

            // 6. A word that is none of them: auto, and the config says what it was
            await RunWith(env, "Service", "stop");
            File.WriteAllText(settings, Set(File.ReadAllText(settings), "fast"));
            var cfg = await RunWith(small, "Service", "config");
            Check("gccollector: a word that is none of them is read as auto, and Service config says so",
                cfg.Out.Contains("Commands' garbage collector: server in the workers, workstation in a command run without the service (workerGarbageCollector, \"fast\" isn't auto, server or workstation, so auto: the workers server (under 2.5 GB a command, each holds .NET's memory under it); a command without the service workstation, as one command may use 1.5 GB here, under the 2.5 GB it needs unheld)"),
                Line(cfg.Out, "Commands' garbage collector"));
            await RunWith(env, "Service", "stop");
            Home = outer;
            await BigWalk(root);
        }
        finally
        {
            await RunWith(env, "Service", "stop");
            Home = outer;
        }
    }

    /// <summary>Check 8: a link walk of a million records goes to a worker on the workstation collector (J58).</summary>
    static async Task BigWalk(string root)
    {
        string home = Path.Combine(root, "bighome"), data = Path.Combine(root, "bigplugins"), txt = Path.Combine(root, "bigplugins.txt");
        Directory.CreateDirectory(home); Directory.CreateDirectory(data);
        WriteMaster(data, small: false);
        WriteKeywords(data, "j58_big.esm", "j58_kw_", 1_000_000);
        GC.Collect();
        File.WriteAllLines(txt, new[] { "*" + KMaster, "*j58_big.esm" });
        var env = new Dictionary<string, string> { ["SFSEARCH_HOME"] = home, ["SFSEARCH_DATA"] = data, ["SFSEARCH_PLUGINS_TXT"] = txt, ["SFSEARCH_WORKERS"] = "2", ["SFSEARCH_WORKER_MAX_MB"] = "4096" };
        string settings = Path.Combine(home, "sfsearch.json"), outer = Home;
        Home = home;
        try
        {
            await RunWith(env, "Service", "config");
            File.WriteAllText(settings, Set(File.ReadAllText(settings), "server"));
            var (ok, _, log) = await StartAndSettle(env);
            var ws = Regex.Match(log, @"Worker (\d+) started on the workstation garbage collector for job (\S+): __links build");
            string w = ws.Success ? ws.Groups[1].Value : "?";
            bool asked = Regex.IsMatch(log, @"\[~index\] asked for a worker on the workstation garbage collector after \d+ ms; queued again: __links build");
            bool walked = Regex.IsMatch(log, $@"\[~index\] w{w} \d+ ms exit 0 [^\n]*: __links build") && Regex.IsMatch(log, @"Indexer: link index: [^\n]*This build: 2 plugin\(s\) walked");
            bool retired = Regex.IsMatch(log, $@"Worker {w} retired: started on the workstation garbage collector for job {Regex.Escape(ws.Groups[2].Value)}, which is done");
            Check("gccollector: with server workers, a link walk of a million records goes to a worker started on the workstation collector, which walks it and is retired after",
                ok && asked && ws.Success && walked && retired,
                $"asked: {asked}; {Grab(log, @"Worker \d+ started on the workstation[^\n]*")}; walked there: {walked}; retired: {retired}; {Grab(log, @"Indexer: link index:[^\n]*")}");
            await RunWith(env, "Service", "stop");
            await RunWith(env, "indexes", "drop", "links");
            File.WriteAllText(settings, Set(File.ReadAllText(settings), "workstation"));
            (ok, _, log) = await StartAndSettle(env);
            Check("gccollector: with workstation workers the same walk runs where it lands (nothing handed on)",
                ok && !log.Contains("asked for a worker on the workstation") && !log.Contains("started on the workstation garbage collector") && Regex.IsMatch(log, @"Indexer: link index: [^\n]*This build: 2 plugin\(s\) walked"),
                Grab(log, @"Indexer: link index:[^\n]*"));
        }
        finally
        {
            await RunWith(env, "Service", "stop");
            Home = outer;
        }
    }

    /// <summary>status's "Open in ..." line names the collector of the process that answered.</summary>
    static bool OpenIn(string text, string where, string collector) =>
        Regex.IsMatch(text, $@"(?m)^Open in {where}: .*with \.NET's {collector} garbage collector\.");

    /// <summary>The setting's line in sfsearch.json set to a word, put in after the opening brace when the file has none.</summary>
    static string Set(string text, string word) =>
        Regex.IsMatch(text, @"""workerGarbageCollector""\s*:")
            ? Regex.Replace(text, @"""workerGarbageCollector""\s*:\s*""[^""]*""", $"\"workerGarbageCollector\": \"{word}\"")
            : new Regex(@"\{").Replace(text, "{\n  \"workerGarbageCollector\": \"" + word + "\",", 1);

    /// <summary>A fresh service on the settings file as it is: Service config and a worker's own answer both name the collector.</summary>
    static async Task Collector(Dictionary<string, string> env, string expect, string plain, string what, string why)
    {
        await RunWith(env, "Service", "stop");
        var (ok, _, log) = await StartAndSettle(env);
        if (!ok) { Check($"gccollector: {what}: the service starts", false, log); return; }
        var cfg = await RunWith(env, "Service", "config");
        var status = await Call(Cmd("status"));
        bool workerSays = Regex.IsMatch(status.Out, $@"(?m)^Open in the worker that answered this: .*with \.NET's {expect} garbage collector\.");
        bool serviceWs = Regex.IsMatch(log, @"Service started: process \d+, \d+ workers; the service itself on \.NET's workstation garbage collector");
        Check($"gccollector: {what}: the worker runs the {expect} collector, the service itself workstation, and Service config says why",
            status.Code == 0 && workerSays && serviceWs && cfg.Out.Contains($"Commands' garbage collector: {expect} in the workers, {plain} in a command run without the service (workerGarbageCollector, {why})"),
            $"{Line(status.Out, "Open in")} / {Line(cfg.Out, "Commands' garbage collector")} / {Grab(log, @"Service started:[^\n]*")}");
    }
}
