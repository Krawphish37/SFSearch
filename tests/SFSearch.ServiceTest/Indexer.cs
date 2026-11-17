using System.Diagnostics;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SFSearch.Service;
using MutagenStyle = Mutagen.Bethesda.Plugins.MasterStyle;

namespace SFSearch.ServiceTest;

/// <summary>
/// `indexer --exe &lt;SFSearch.exe&gt; [--records N]`: the indexer lane, with the service's own indexer jobs (J37: until
/// then a test command stood in for them). Its own home and test Data folder under the test home, with one plugin Mutagen
/// writes there (N keywords, 50,000 by default, and a form list per 10 of them, linking them); the test add-on in the
/// home's extensions folder for the agents' commands; keepStartupIndexes false and 4 workers in its sfsearch.json.
/// The real link walk takes a fraction of a second over that plugin, so the test holds the walk's own lock (the named
/// mutex one program at a time walks under, <see cref="WalkHold"/>) from before the service starts: the start's
/// `__links build` job then waits in the indexer lane, listening for the coordinator, as it does behind another walk.
/// Checked, while that job runs:
///   1. the indexer's jobs run when no agent is waiting (the start's header index job, then the link walk);
///   2. the indexer's worker runs at low priority;
///   3. a job that listens gives way at once: the walk stops for a wave of agent commands, one per worker;
///   4. agents go first: the walk runs again only once no agent is left waiting;
///   5. the lock let go, the walk then finishes the link index;
///   6. status counts the times the indexer gave way.
/// Stops its service at the end. A job that doesn't listen, stopped by the coordinator 2 s after it was asked, is the
/// `giveway` test: the rule as plain code (J50; it was checked here through the request field `lane`, which only this
/// test used, and which is gone).
/// </summary>
public static partial class T
{
    const string WalkPlugin = "j37_walk.esm";

    static async Task Indexer(int records)
    {
        if (!File.Exists(Exe)) { Check("indexer: --exe names SFSearch.exe", false, Exe); return; }
        const int workers = 4;
        string root = Path.Combine(Home, "indexer"), home = Path.Combine(root, "home"), data = Path.Combine(root, "plugins"), txt = Path.Combine(root, "plugins.txt");
        if (Path.GetFullPath(data).Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)) { Check("indexer: the test Data folder is outside the game's", false, data); return; }
        string outer = Home;
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(home); Directory.CreateDirectory(data);
        var made = Stopwatch.StartNew();
        WriteWalkPlugin(Path.Combine(data, WalkPlugin), records);
        Say($"{WalkPlugin}: {records:N0} keywords and {records / 10:N0} form lists written by Mutagen in {made.Elapsed.TotalSeconds:0.0} s ({new FileInfo(Path.Combine(data, WalkPlugin)).Length / 1048576.0:0.0} MB)");
        File.WriteAllLines(txt, new[] { "*" + WalkPlugin });
        File.WriteAllText(Path.Combine(home, "sfsearch.json"), $"{{ \"settingsVersion\": 2, \"workers\": {workers}, \"keepStartupIndexes\": false }}");
        InstallAddOn(home);
        var env = new Dictionary<string, string> { ["SFSEARCH_HOME"] = home, ["SFSEARCH_DATA"] = data, ["SFSEARCH_PLUGINS_TXT"] = txt };
        Home = home;
        var hold = new WalkHold(home);
        try
        {
            var started = await RunWith(env, "Service", "start");
            if (!started.Out.Contains("Service started")) { Check("indexer: the service starts", false, started.Out + started.Err); return; }

            // 1. The start's jobs run with no agent waiting: the header index's, then the link walk
            long walking = await UntilStatus(s => Regex.IsMatch(s, @"\[~index\][^\n]*__links build"), 60000);
            string built = Regex.Match(ReadLog(), @"job \S+ \[~index\] w\d+ \d+ ms exit 0 [^\n]*: __index build[^\n]*").Value;
            Check("indexer: its jobs run when no agent is waiting (the header index, then the link walk)", walking >= 0 && built.Length > 0,
                $"{(walking >= 0 ? $"the link walk running {walking / 1000.0:0.0} s after the service started" : "the link walk didn't start within 60 s")}; {Cut(built, 160)}");
            if (walking < 0) return;

            // 2. Its worker runs at low priority
            int pid = (await Workers()).FirstOrDefault(x => x.Line.Contains("[~index]")).Pid;
            string prio = "";
            try { prio = Process.GetProcessById(pid).PriorityClass.ToString(); } catch (Exception) { }
            Check("indexer: its worker runs at low priority", prio == "BelowNormal", $"process {pid}: {prio}");
            await Task.Delay(2000);
            Check("indexer: the walk waits while another program holds the walk's lock", Regex.IsMatch(await Status(), @"\[~index\][^\n]*__links build"), "still running 2 s later");

            // 3 and 4. A wave of agent commands, one per worker and three more, while the walk runs: when an agent has to
            // wait, the walk is asked to give way and does so at once (not stopped by the coordinator 2 s later), and it runs
            // again only when no agent is left waiting
            var wave = Enumerable.Range(0, workers + 3).Select(i => Call(new Request { Op = "run", Args = ["__test", "sleep", "2500"], Agent = $"b{i % 3}" })).ToList();
            // The walk leaves its worker, then comes back: when, as the status shows it (looked at every 20 ms)
            static bool Walking(string s) => Regex.IsMatch(s, @"\[~index\][^\n]*__links build");
            await UntilStatus(s => !Walking(s), 5000);
            DateTime? walkBack = null;
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 30000)
            {
                if (Walking(await Status())) { walkBack = DateTime.UtcNow; break; }
                if (wave.All(t => t.IsCompleted)) break;
                await Task.Delay(20);
            }
            var spans = (await Task.WhenAll(wave)).Select(r => Interval(r.Out)).ToList();
            if (spans.Any(s => s == null)) { Check("indexer: the agents' commands ran", false, "an answer without its times"); return; }
            var starts = spans.Select(s => s!.Value.From).OrderBy(t => t).ToList();
            string log = ReadLog();
            var askedAt = Regex.Match(log, @"(\d\d:\d\d:\d\d\.\d\d\d) job (\S+) \[~index\] asked to give way: an agent is waiting");
            var gaveAt = askedAt.Success ? Regex.Match(log, @"(\d\d:\d\d:\d\d\.\d\d\d) job " + Regex.Escape(askedAt.Groups[2].Value) + @" \[~index\] gave way after \d+ ms; queued again: __links build") : Match.Empty;
            double giveMs = gaveAt.Success ? (TimeSpan.Parse(gaveAt.Groups[1].Value) - TimeSpan.Parse(askedAt.Groups[1].Value)).TotalMilliseconds : -1;
            Check("indexer: the link walk gives way at once when an agent has to wait", gaveAt.Success && giveMs < 1500 && !log.Contains("stopped to let an agent through; queued again: __links build"),
                gaveAt.Success ? $"{workers + 3} agent commands of 2.5 s for {workers} workers, one of them walking: asked to give way at {askedAt.Groups[1].Value}, gave way {giveMs:0} ms later (the coordinator stops a job that hasn't after 2,000)" : "the log doesn't show the walk asked to give way and giving way");
            var lastStart = starts[^1];
            Check("indexer: agents go first: the walk runs again only when no agent is waiting", walkBack == null || walkBack.Value >= lastStart.AddMilliseconds(-50),
                walkBack == null ? "the walk hadn't come back while the agents' commands ran" : $"the last agent command started at {lastStart:HH:mm:ss.fff}, the walk was back at {walkBack:HH:mm:ss.fff} ({(walkBack.Value - lastStart).TotalMilliseconds:0} ms later)");

            // 5. The lock let go: the walk finishes the link index
            hold.Dispose();
            string linked = "";
            var until = Stopwatch.StartNew();
            while (until.Elapsed.TotalSeconds < 120 && (linked = Regex.Match(ReadLog(), @"Indexer: link index: 1 of 1 plugins have a link file[^\n]*").Value).Length == 0) await Task.Delay(200);
            Check("indexer: the walk goes on afterwards and finishes the link index", linked.Contains(" 0 left") && linked.Contains($"{records * 11 / 10:N0} records"), Cut(linked, 300));

            // 6. Status counts it
            string line = Line(await Status(), "Indexer:");
            Check("indexer: status counts the times it gave way", Regex.IsMatch(line, @"gave way to an agent [1-9]\d* time\(s\)"), line);
        }
        finally
        {
            hold.Dispose();
            await RunWith(env, "Service", "stop");
            Home = outer;
        }
    }

    /// <summary>
    /// Holds the link walk's own lock of a home (EngineLinks.Build: the named mutex "Local\sfsearch_links_" and the hash
    /// of the home's indexes\links folder) on a thread of its own, from now until disposed: a walk of that home waits
    /// meanwhile, and gives way when the coordinator asks, as behind another program's walk.
    /// </summary>
    sealed class WalkHold : IDisposable
    {
        readonly Thread _thread;
        readonly ManualResetEventSlim _taken = new(), _letGo = new();
        bool _done;

        public WalkHold(string home)
        {
            string name = @"Local\sfsearch_links_" + Wire.Hash(Path.Combine(Path.GetFullPath(home), "indexes", "links"));
            _thread = new Thread(() =>
            {
                using var m = new Mutex(false, name);
                try { m.WaitOne(); } catch (AbandonedMutexException) { }
                _taken.Set();
                _letGo.Wait();
                m.ReleaseMutex();
            }) { IsBackground = true, Name = "walk hold" };
            _thread.Start();
            _taken.Wait();
        }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _letGo.Set();
            _thread.Join();
        }
    }

    static string Cut(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    /// <summary>A plugin for the link walk to take a while over: keywords, and a form list per 10 of them that links them.</summary>
    static void WriteWalkPlugin(string path, int keywords)
    {
        if (Path.GetFullPath(path).Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("never into the game's Data folder");
        var m = new StarfieldMod(ModKey.FromFileName(Path.GetFileName(path)), StarfieldRelease.Starfield);
        var batch = new List<IFormLinkGetter<IStarfieldMajorRecordGetter>>();
        for (int i = 0; i < keywords; i++)
        {
            var k = m.Keywords.AddNew($"j37_kw_{i:D7}");
            batch.Add(new FormLink<IStarfieldMajorRecordGetter>(k.FormKey));
            if (batch.Count == 10)
            {
                var list = m.FormLists.AddNew($"j37_list_{i / 10:D6}");
                foreach (var l in batch) list.Items.Add(l);
                batch.Clear();
            }
        }
        WriteMod(m, path, MutagenStyle.Full, null);
    }
}
