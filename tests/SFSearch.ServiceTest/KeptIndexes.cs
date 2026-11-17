using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SFSearch.Service;
using MutagenStyle = Mutagen.Bethesda.Plugins.MasterStyle;

namespace SFSearch.ServiceTest;

/// <summary>
/// `keptindexes --exe &lt;SFSearch.exe&gt;` (J31, 2026-11-15): the header index and the link index are kept between service
/// starts (keepStartupIndexes, on by default) and brought up to date plugin by plugin. Its own home and test Data folder
/// under the test home, with three plugins Mutagen writes there (never into the game's Data): a master, a patch that
/// links the master's keywords from a form list, and a plugin of 6,000 keywords big enough that its middle is outside
/// its quick stamp's first and last 64 KB. Real restarts and the real setting only; no test hook. Checks:
///   1. a restart reuses both indexes (no header pass, no plugin walked, the same files);
///   2. one changed plugin is rebuilt alone, and its old parts are deleted;
///   3. a master's size change (Full to Small) rebuilds the master and its dependent, not the third plugin;
///   4. a change in the middle of a file that keeps its size and date is caught by the start's full hash;
///   5. indexes drop and indexes build through the service, while workers have the files open;
///   6. with keepStartupIndexes false the indexes are emptied at start and made again.
/// Stops its service at the end.
/// </summary>
public static partial class T
{
    const string KMaster = "j31_master.esm", KPatch = "j31_patch.esm", KBig = "j31_big.esm";

    static async Task KeptIndexes()
    {
        if (!File.Exists(Exe)) { Check("keptindexes: --exe names SFSearch.exe", false, Exe); return; }
        string root = Path.Combine(Home, "keptindexes"), home = Path.Combine(root, "home"), data = Path.Combine(root, "plugins"), txt = Path.Combine(root, "plugins.txt");
        if (Path.GetFullPath(data).Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)) { Check("keptindexes: the test Data folder is outside the game's", false, data); return; }
        string outer = Home;
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(home); Directory.CreateDirectory(data);
        WriteMaster(data, small: false);
        WritePatch(data);
        WriteBig(data, extra: 0);
        File.WriteAllLines(txt, new[] { KMaster, KPatch, KBig }.Select(p => "*" + p));
        var env = new Dictionary<string, string> { ["SFSEARCH_HOME"] = home, ["SFSEARCH_DATA"] = data, ["SFSEARCH_PLUGINS_TXT"] = txt, ["SFSEARCH_WORKERS"] = "2" };
        string header = Path.Combine(home, "indexes", "header"), links = Path.Combine(home, "indexes", "links");
        Home = home;
        try
        {
            // 0. The first start makes both
            var (ok, startMs, log) = await StartAndSettle(env);
            if (!ok) { Check("keptindexes: the first start makes both indexes and checks them", false, log); return; }
            var seg0 = Files(header, "*.seg"); var lnk0 = Files(links, "*.links");
            Check("keptindexes: the first start makes 3 segments and 3 link files in indexes\\", seg0.Count == 3 && lnk0.Count == 3, $"{seg0.Count} segments, {lnk0.Count} link files; ready in {startMs / 1000.0:0.0} s");

            // 1. A restart reuses both
            await RunWith(env, "Service", "stop");
            (ok, startMs, log) = await StartAndSettle(env);
            var seg1 = Files(header, "*.seg"); var lnk1 = Files(links, "*.links");
            Check("keptindexes: a restart reuses both indexes (no header pass, no walk, the same files)",
                ok && Same(seg0, seg1) && Same(lnk0, lnk1) && Regex.IsMatch(log, @"This build: [\d,]+ ms, 0 header pass\(es\), 0 file\(s\) hashed, 3 segment\(s\) already there") && Regex.IsMatch(log, @"This build: 0 plugin\(s\) walked"),
                $"ready in {startMs / 1000.0:0.0} s; {Grab(log, @"This build: [^.]*header pass[^.]*")}; {Grab(log, @"This build: 0 plugin[^;]*|This build: \d+ plugin[^;]*")}");

            // 2. One changed plugin is rebuilt alone, and its old parts go
            await RunWith(env, "Service", "stop");
            WriteBig(data, extra: 1);
            (ok, startMs, log) = await StartAndSettle(env);
            var seg2 = Files(header, "*.seg"); var lnk2 = Files(links, "*.links");
            Check("keptindexes: one changed plugin is rebuilt alone; its old parts are deleted",
                ok && seg2.Count == 3 && lnk2.Count == 3 && Kept(seg1, seg2) == 2 && Kept(lnk1, lnk2) == 2 && Regex.IsMatch(log, @"1 header pass\(es\)") && Regex.IsMatch(log, @"This build: 1 plugin\(s\) walked") && log.Contains("Removed as no plugin uses them any more: 1 segment file(s), 1 link file(s)"),
                $"segments kept {Kept(seg1, seg2)} of 3, link files kept {Kept(lnk1, lnk2)} of 3, {seg2.Count} + {lnk2.Count} files now; {Grab(log, @"This build: [^.]*header pass[^.]*")}; {Grab(log, @"Removed as no plugin uses them[^.]*")}");

            // 3. The master becomes Small: it and its dependent are rebuilt (the patch's own file is unchanged), the third is not
            await RunWith(env, "Service", "stop");
            byte[] patchBytes = File.ReadAllBytes(Path.Combine(data, KPatch));
            WriteMaster(data, small: true);
            (ok, startMs, log) = await StartAndSettle(env);
            var seg3 = Files(header, "*.seg"); var lnk3 = Files(links, "*.links");
            bool patchSame = File.ReadAllBytes(Path.Combine(data, KPatch)).AsSpan().SequenceEqual(patchBytes);
            Check("keptindexes: a master's size change rebuilds the master and its dependent, not the other plugin",
                ok && patchSame && seg3.Count == 3 && lnk3.Count == 3 && Kept(seg2, seg3) == 1 && Kept(lnk2, lnk3) == 1 && Regex.IsMatch(log, @"2 header pass\(es\)") && Regex.IsMatch(log, @"This build: 2 plugin\(s\) walked") && log.Contains("Removed as no plugin uses them any more: 2 segment file(s), 2 link file(s)"),
                $"the patch file unchanged: {patchSame}; segments kept {Kept(seg2, seg3)} of 3, link files kept {Kept(lnk2, lnk3)} of 3; {Grab(log, @"This build: [^.]*header pass[^.]*")}; {Grab(log, @"Removed as no plugin uses them[^.]*")}");

            // 4. A change in the middle of the big plugin that keeps its size and date: the start's full hash catches it
            await RunWith(env, "Service", "stop");
            string big = Path.Combine(data, KBig);
            var date = File.GetLastWriteTimeUtc(big);
            byte[] bytes = File.ReadAllBytes(big);
            byte[] old = Encoding.ASCII.GetBytes("j31_kw_03000\0"), now = Encoding.ASCII.GetBytes("j31_kw_0300Q\0");
            int at = bytes.AsSpan().IndexOf(old);
            bool middle = at > 65536 && at < bytes.Length - 65536;
            now.CopyTo(bytes, at);
            File.WriteAllBytes(big, bytes);
            File.SetLastWriteTimeUtc(big, date);
            Check("keptindexes: the change keeps the file's size and date and is outside its first and last 64 KB", middle && new FileInfo(big).Length == bytes.Length && File.GetLastWriteTimeUtc(big) == date, $"at byte {at:N0} of {bytes.Length:N0}");
            (ok, startMs, log) = await StartAndSettle(env);
            var found = await Call(Cmd("find", "j31_kw_0300Q"));
            var seg4 = Files(header, "*.seg"); var lnk4 = Files(links, "*.links");
            Check("keptindexes: the start's full hash catches a change in the middle of a file; the new EditorID is found, the old parts are deleted",
                ok && found.Code == 0 && found.Out.Contains("j31_kw_0300Q") && Regex.IsMatch(log, @"This sweep: [^.]*1 changed") && seg4.Count == 3 && lnk4.Count == 3 && Kept(seg3, seg4) == 2 && Kept(lnk3, lnk4) == 2,
                $"find: exit {found.Code}, {found.Out.Split('\n').FirstOrDefault(l => l.Contains("j31_kw_0300Q"))?.Trim()}; {Grab(log, @"This sweep: [^.]*")}; segments kept {Kept(seg3, seg4)} of 3, link files kept {Kept(lnk3, lnk4)} of 3; {Grab(log, @"Removed as no plugin uses them[^.]*")}");

            // 5. indexes, drop and build through the service, while the workers have the files open
            var show = await Call(Cmd("indexes"));
            Check("keptindexes: indexes shows both, kept between starts, and how to drop them",
                show.Code == 0 && Regex.IsMatch(show.Out, @"\| header \| [\d.,]+ (bytes|KB) \| 3 of 3 plugins \|") && Regex.IsMatch(show.Out, @"\| links \| [\d.,]+ (bytes|KB) \| 3 of 3 plugins that can be walked \|") && show.Out.Contains("kept between service starts") && show.Out.Contains("indexes drop all"),
                Line(show.Out, "| header |") + " / " + Line(show.Out, "| links |"));
            var drop = await Call(Cmd("indexes", "drop", "links"));
            int left = Files(links, "*.links").Count;
            Check("keptindexes: indexes drop links deletes the link files while the service runs", drop.Code == 0 && left == 0 && drop.Out.Contains("Deleted the link index: 3 file(s)"), $"{left} left; {Line(drop.Out, "Deleted")}");
            var build = await Call(Cmd("indexes", "build", "links"));
            Check("keptindexes: indexes build links makes them again", build.Code == 0 && Files(links, "*.links").Count == 3 && build.Out.Contains("Link index: 3 plugin(s) walked"), Line(build.Out, "Link index:"));
            var status = await Call(Cmd("status"));
            Check("keptindexes: status has the kept indexes' line", Regex.IsMatch(status.Out, @"(?m)^Kept indexes: header [\d.,]+ (bytes|KB), links [\d.,]+ (bytes|KB), world (none \(made only when asked: indexes build world\)|[\d.,]+ (bytes|KB|MB)) in .*indexes; kept between service starts"), Line(status.Out, "Kept indexes:"));

            // 6. keepStartupIndexes false: emptied at start and made again
            await RunWith(env, "Service", "stop");
            string cfg = Path.Combine(home, "sfsearch.json");
            File.WriteAllText(cfg, File.ReadAllText(cfg).Replace("\"keepStartupIndexes\": true", "\"keepStartupIndexes\": false"));
            var seg5 = Files(header, "*.seg");
            (ok, startMs, log) = await StartAndSettle(env, sweep: false);
            var seg6 = Files(header, "*.seg");
            Check("keptindexes: with keepStartupIndexes false the indexes are emptied at start and made again",
                ok && log.Contains("emptied at start (keepStartupIndexes is false)") && Regex.IsMatch(log, @"3 header pass\(es\)") && Regex.IsMatch(log, @"This build: 3 plugin\(s\) walked") && !log.Contains("This sweep") && Kept(seg5, seg6) == 0 && seg6.Count == 3,
                $"{Grab(log, @"Indexes folder [^\n]*")}; {Grab(log, @"This build: [^.]*header pass[^.]*")}; segments kept {Kept(seg5, seg6)}");
            show = await Call(Cmd("indexes"));
            Check("keptindexes: indexes says they are emptied at every start", show.Out.Contains("emptied at every service start"), Line(show.Out, "emptied"));
            await RunWith(env, "Service", "stop");
        }
        finally
        {
            await RunWith(env, "Service", "stop");
            Home = outer;
        }
    }

    /// <summary>
    /// `keptrestart --exe &lt;SFSearch.exe&gt; [--data &lt;Data folder&gt;]` (J31): the start-up indexes of a whole Data folder (only
    /// read), on a home of its own: the time from `Service start` until status says the link index is complete, three
    /// times: the first start (nothing kept), a restart (both kept: under 6 s), and a start with keepStartupIndexes false
    /// (emptied and made again, as SFSearch 2.0 did every time). Its walk of Starfield.esm needs about 2 GB: run it
    /// through heavy.sh. Stops its service at the end.
    /// </summary>
    static async Task KeptRestart(string? dataFrom)
    {
        if (!File.Exists(Exe)) { Check("keptrestart: --exe names SFSearch.exe", false, Exe); return; }
        dataFrom ??= Environment.GetEnvironmentVariable("SFSEARCH_DATA") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data";
        string home = Path.Combine(Home, "keptrestart");
        string outer = Home;
        if (Directory.Exists(home)) Directory.Delete(home, true);
        Directory.CreateDirectory(home);
        var env = new Dictionary<string, string> { ["SFSEARCH_HOME"] = home, ["SFSEARCH_DATA"] = dataFrom };
        Home = home;
        try
        {
            var (ok, cold, log) = await StartAndSettle(env);
            var size = Directory.GetFiles(Path.Combine(home, "indexes"), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            Check("keptrestart: the first start makes both indexes", ok, $"link index complete {cold / 1000.0:0.0} s after Service start; {size / 1048576.0:N0} MB in indexes\\; {Grab(log, @"This build: [^.]*header pass[^.]*")}; {Grab(log, @"This build: \d+ plugin\(s\) walked in [\d.]+ s")}");
            await RunWith(env, "Service", "stop");
            (ok, var kept, log) = await StartAndSettle(env);
            Check("keptrestart: a restart reuses both: the link index is complete in under 6 s", ok && kept >= 0 && kept < 6000 && Regex.IsMatch(log, @"0 header pass\(es\)") && Regex.IsMatch(log, @"This build: 0 plugin\(s\) walked"),
                $"{kept / 1000.0:0.0} s after Service start; {Grab(log, @"This build: [^.]*header pass[^.]*")}; {Grab(log, @"This build: \d+ plugin\(s\) walked in [\d.]+ s")}; the full check: {Grab(log, @"This sweep: [\d,]+ ms[^.]*")}");
            await RunWith(env, "Service", "stop");
            string cfg = Path.Combine(home, "sfsearch.json");
            File.WriteAllText(cfg, File.ReadAllText(cfg).Replace("\"keepStartupIndexes\": true", "\"keepStartupIndexes\": false"));
            (ok, var off, log) = await StartAndSettle(env, sweep: false);
            Check("keptrestart: with keepStartupIndexes false a start makes both again, as before", ok && log.Contains("emptied at start"), $"link index complete {off / 1000.0:0.0} s after Service start; {Grab(log, @"Indexes folder [^\n]*")}");
            Say($"start to link index complete: first {cold / 1000.0:0.0} s, kept {kept / 1000.0:0.0} s, setting off {off / 1000.0:0.0} s; kept indexes {size / 1048576.0:N0} MB");
            await RunWith(env, "Service", "stop");
        }
        finally
        {
            await RunWith(env, "Service", "stop");
            Home = outer;
        }
    }

    /// <summary>
    /// Starts the service and waits until the indexer has made the link index and is idle (with the start's full check
    /// when the indexes are kept), asking only the coordinator meanwhile, so that no agent's command makes an index part
    /// before the indexer does. Returns the milliseconds from the service's "Service started" log line to its first link
    /// job's line (the link index ready), and the log lines this start wrote. Then sfs_status must say the link index is
    /// complete.
    /// </summary>
    static async Task<(bool Ok, long ReadyMs, string Log)> StartAndSettle(Dictionary<string, string> env, bool sweep = true)
    {
        string logDir = Path.Combine(Home, "Service", "logs");
        int before = NewLog(logDir, 0).Length;
        var clock = Stopwatch.StartNew();
        var started = await RunWith(env, "Service", "start");
        if (!started.Out.Contains("Service started") && !started.Out.Contains("already")) return (false, -1, started.Out + started.Err);
        string log = "";
        bool settled = false;
        while (clock.Elapsed.TotalSeconds < 180)
        {
            log = NewLog(logDir, before);
            bool idle = Regex.IsMatch(await Status(), @"Indexer: idle; 0 waiting");
            if (idle && log.Contains("Indexer: link index:") && (!sweep || log.Contains("This sweep"))) { settled = true; break; }
            await Task.Delay(200);
        }
        long ready = LogMs(log, "Service started", "Indexer: link index:");
        if (!settled) return (false, ready, log);
        var s = await Call(Cmd("status"));
        bool complete = Regex.IsMatch(s.Out, @"(?m)^Link index: complete\.");
        return (complete, ready, complete ? log : log + "\nstatus: " + Line(s.Out, "Link index:"));
    }

    /// <summary>The milliseconds between the first log lines holding each text (each line starts with HH:mm:ss.fff); -1 when one isn't there.</summary>
    static long LogMs(string log, string from, string to)
    {
        TimeSpan? At(string text)
        {
            var line = log.Split('\n').FirstOrDefault(l => l.Contains(text));
            return line != null && line.Length > 12 && TimeSpan.TryParseExact(line[..12], @"hh\:mm\:ss\.fff", null, out var t) ? t : null;
        }
        if (At(from) is not TimeSpan a || At(to) is not TimeSpan b) return -1;
        var d = b - a;
        if (d < TimeSpan.Zero) d += TimeSpan.FromDays(1);
        return (long)d.TotalMilliseconds;
    }

    static string NewLog(string logDir, long before)
    {
        if (!Directory.Exists(logDir)) return "";
        var sb = new StringBuilder();
        foreach (var f in Directory.GetFiles(logDir, "service-*.log").OrderBy(f => f))
        {
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            sb.Append(r.ReadToEnd());
        }
        string all = sb.ToString();
        return before < all.Length ? all[(int)before..] : all;
    }

    static Dictionary<string, DateTime> Files(string dir, string pattern) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).ToDictionary(f => Path.GetFileName(f), File.GetLastWriteTimeUtc, StringComparer.OrdinalIgnoreCase) : new();

    static bool Same(Dictionary<string, DateTime> a, Dictionary<string, DateTime> b) => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var t) && t == kv.Value);

    /// <summary>How many files of the first look are still there, untouched.</summary>
    static int Kept(Dictionary<string, DateTime> a, Dictionary<string, DateTime> b) => a.Count(kv => b.TryGetValue(kv.Key, out var t) && t == kv.Value);

    static string Grab(string text, string pattern) => Regex.Match(text, pattern) is { Success: true } m ? m.Value : "(not in the log)";

    // ---------------------------------------------------------------- The plugins, written by Mutagen into the test Data folder
    static void WriteMaster(string data, bool small)
    {
        var m = new StarfieldMod(ModKey.FromFileName(KMaster), StarfieldRelease.Starfield);
        if (small) m.IsSmallMaster = true;
        for (int i = 0; i < 3; i++) m.Keywords.AddNew($"j31_master_kw_{i}");
        WriteMod(m, Path.Combine(data, KMaster), small ? MutagenStyle.Small : MutagenStyle.Full, null);
    }

    static void WritePatch(string data)
    {
        var master = new StarfieldMod(ModKey.FromFileName(KMaster), StarfieldRelease.Starfield);
        for (int i = 0; i < 3; i++) master.Keywords.AddNew($"j31_master_kw_{i}");
        var p = new StarfieldMod(ModKey.FromFileName(KPatch), StarfieldRelease.Starfield);
        var list = p.FormLists.AddNew("j31_patch_list");
        foreach (var k in master.Keywords) list.Items.Add(new FormLink<IStarfieldMajorRecordGetter>(k.FormKey));
        WriteMod(p, Path.Combine(data, KPatch), MutagenStyle.Full, MutagenStyle.Full);
    }

    static void WriteBig(string data, int extra)
    {
        var m = new StarfieldMod(ModKey.FromFileName(KBig), StarfieldRelease.Starfield);
        for (int i = 0; i < 6000 + extra; i++) m.Keywords.AddNew($"j31_kw_{i:D5}");
        WriteMod(m, Path.Combine(data, KBig), MutagenStyle.Full, null);
    }

    static void WriteMod(StarfieldMod mod, string path, MutagenStyle self, MutagenStyle? master)
    {
        if (Path.GetFullPath(path).Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("never into the game's Data folder");
        var order = new List<KeyedMasterStyle>();
        if (master != null) order.Add(new KeyedMasterStyle(ModKey.FromFileName(KMaster), master.Value));
        order.Add(new KeyedMasterStyle(mod.ModKey, self));
        var lookup = new LoadOrder<IModMasterStyledGetter>(order);
        mod.WriteToBinary(path, new BinaryWriteParameters
        {
            MasterFlagsLookup = lookup, ModKey = ModKeyOption.CorrectToPath, MastersListOrdering = new MastersListOrderingByLoadOrder(lookup),
            LowerRangeDisallowedHandler = new NoCheckIfLowerRangeDisallowed(), FormIDCompaction = FormIDCompactionOption.NoCheck, HeaderSizeFlags = HeaderSizeFlagsOption.NoCheck,
        });
    }
}
