using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using MutagenStyle = Mutagen.Bethesda.Plugins.MasterStyle;

namespace SFSearch.ServiceTest;

/// <summary>
/// `keptremoved --exe &lt;SFSearch.exe&gt;` (J43, 2026-11-15): the kept index parts of plugins taken out of Data, by the
/// setting keepRemovedPluginIndexMB. Its own home and test Data folder under the test home, with three plugins Mutagen
/// writes there (never into the game's Data): a small master and two plugins of 12,000 keywords each (each one's segment is
/// about 0.6 MB, so a cap of whole MB can hold one and not both). Taking a plugin out of Data is moving its file to a
/// folder beside it; putting it back moves it back (size and date kept). Real restarts and the real setting only. Checks:
///   1. the default (0): a plugin taken out loses its segment and link file at the next start;
///   2. put back, it is indexed again (a header pass and a walk): what the setting saves;
///   3. keepRemovedPluginIndexMB 100: a plugin taken out keeps its parts at the next start, and indexes lists them;
///   4. put back, it isn't indexed again (no header pass, no walk, the same files);
///   5. over the size, a plugin's parts go together, the plugin out of Data longest first (J60, GAPS S86): a taken out,
///      then b; a cap that holds one; the files' write times set so that the order they were made in says otherwise
///      (a's segment the newest, a's link file the oldest, b's between). a's segment and link file both go, b's both stay,
///      and indexes listed a with when it was last in Data.
///   6. a plugin's world link file (J32) goes with its segment and link file: b put back, a plugin with world data (the
///      worldindex test's j32_world.esm, 20,000 placed objects) added and its world link file made, then that plugin
///      taken out, then b; a cap that holds one; write times set against the order. j32_world.esm's three files go, b's
///      two stay.
/// Stops its service at the end.
/// </summary>
public static partial class T
{
    const string RA = "j43_a.esm", RB = "j43_b.esm";

    static async Task KeptRemoved()
    {
        if (!File.Exists(Exe)) { Check("keptremoved: --exe names SFSearch.exe", false, Exe); return; }
        string root = Path.Combine(Home, "keptremoved"), home = Path.Combine(root, "home"), data = Path.Combine(root, "plugins"), aside = Path.Combine(root, "aside"), txt = Path.Combine(root, "plugins.txt");
        if (Path.GetFullPath(data).Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)) { Check("keptremoved: the test Data folder is outside the game's", false, data); return; }
        string outer = Home;
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(home); Directory.CreateDirectory(data); Directory.CreateDirectory(aside);
        WriteMaster(data, small: false);
        WriteKeywords(data, RA, "j43_a_kw_");
        WriteKeywords(data, RB, "j43_b_kw_");
        File.WriteAllLines(txt, new[] { KMaster, RA, RB }.Select(p => "*" + p));
        var env = new Dictionary<string, string> { ["SFSEARCH_HOME"] = home, ["SFSEARCH_DATA"] = data, ["SFSEARCH_PLUGINS_TXT"] = txt, ["SFSEARCH_WORKERS"] = "2" };
        string header = Path.Combine(home, "indexes", "header"), links = Path.Combine(home, "indexes", "links"), cfg = Path.Combine(home, "sfsearch.json");
        void Out(string p) => File.Move(Path.Combine(data, p), Path.Combine(aside, p));
        void Back(string p) => File.Move(Path.Combine(aside, p), Path.Combine(data, p));
        Home = home;
        try
        {
            // 0. The first start: 3 segments, 3 link files
            var (ok, _, log) = await StartAndSettle(env);
            var seg0 = Files(header, "*.seg"); var lnk0 = Files(links, "*.links");
            if (!ok || seg0.Count != 3 || lnk0.Count != 3) { Check("keptremoved: the first start makes 3 segments and 3 link files", false, $"{seg0.Count} + {lnk0.Count}; {log}"); return; }
            Check("keptremoved: the written sfsearch.json has keepRemovedPluginIndexMB at 0", Regex.IsMatch(File.ReadAllText(cfg), @"""keepRemovedPluginIndexMB"":\s*0\b"), Line(File.ReadAllText(cfg), "\"keepRemovedPluginIndexMB\""));

            // 1. The default: a plugin taken out loses its parts at the next start
            await RunWith(env, "Service", "stop");
            Out(RA);
            (ok, _, log) = await StartAndSettle(env);
            var seg1 = Files(header, "*.seg"); var lnk1 = Files(links, "*.links");
            var aSeg = seg0.Keys.Except(seg1.Keys, StringComparer.OrdinalIgnoreCase).ToList();
            var aLnk = lnk0.Keys.Except(lnk1.Keys, StringComparer.OrdinalIgnoreCase).ToList();
            Check("keptremoved: the default (0): a plugin taken out of Data loses its segment and link file at the next start",
                ok && seg1.Count == 2 && lnk1.Count == 2 && aSeg.Count == 1 && aLnk.Count == 1 && log.Contains("Removed as no plugin uses them any more: 1 segment file(s), 1 link file(s)"),
                $"{seg1.Count} segments, {lnk1.Count} link files; {Grab(log, @"Removed as no plugin uses them[^.]*")}");

            // 2. Put back: indexed again (the cost the setting saves)
            await RunWith(env, "Service", "stop");
            Back(RA);
            (ok, _, log) = await StartAndSettle(env);
            Check("keptremoved: the default: put back, it is indexed again (1 header pass, 1 plugin walked)",
                ok && Regex.IsMatch(log, @"This build: [^.]*1 header pass\(es\)") && Regex.IsMatch(log, @"This build: 1 plugin\(s\) walked"),
                $"{Grab(log, @"This build: [^.]*header pass[^.]*")}; {Grab(log, @"This build: \d+ plugin\(s\) walked")}");

            // 3. keepRemovedPluginIndexMB 100: taken out, its parts stay; indexes lists them
            await RunWith(env, "Service", "stop");
            File.WriteAllText(cfg, Regex.Replace(File.ReadAllText(cfg), @"""keepRemovedPluginIndexMB"":\s*0", "\"keepRemovedPluginIndexMB\": 100"));
            var seg2 = Files(header, "*.seg"); var lnk2 = Files(links, "*.links");
            Out(RA);
            (ok, _, log) = await StartAndSettle(env);
            var seg3 = Files(header, "*.seg"); var lnk3 = Files(links, "*.links");
            Check("keptremoved: keepRemovedPluginIndexMB 100: a plugin taken out keeps its segment and link file at the next start",
                ok && Same(seg2, seg3) && Same(lnk2, lnk3) && Regex.IsMatch(log, @"Kept for plugins not in Data now \(keepRemovedPluginIndexMB 100\): 2 file\(s\)"),
                $"{seg3.Count} segments, {lnk3.Count} link files, all as they were: {Same(seg2, seg3) && Same(lnk2, lnk3)}; {Grab(log, @"Kept for plugins not in Data now[^.]*\.[^.]*")}");
            var show = await Call(Cmd("indexes"));
            Check("keptremoved: indexes lists what is kept for plugins not in Data, its size and the setting",
                show.Code == 0 && Regex.IsMatch(show.Out, @"Kept for plugins not in Data now: [\d.,]+ (bytes|KB|MB) of up to 100 MB \(keepRemovedPluginIndexMB in sfsearch.json\): 1 segment\(s\), of 1 plugin\(s\) not in Data: j43_a\.esm \((made|last in Data) \d{4}-\d\d-\d\d \d\d:\d\d\), and 1 link file\(s\)"),
                Line(show.Out, "Kept for plugins not in Data"));

            // 4. Put back: not indexed again
            await RunWith(env, "Service", "stop");
            Back(RA);
            (ok, _, log) = await StartAndSettle(env);
            var seg4 = Files(header, "*.seg"); var lnk4 = Files(links, "*.links");
            Check("keptremoved: keepRemovedPluginIndexMB 100: put back, it isn't indexed again (no header pass, no walk, the same files)",
                ok && Same(seg3, seg4) && Same(lnk3, lnk4) && Regex.IsMatch(log, @"This build: [^.]*0 header pass\(es\)") && Regex.IsMatch(log, @"This build: 0 plugin\(s\) walked"),
                $"{Grab(log, @"This build: [^.]*header pass[^.]*")}; {Grab(log, @"This build: \d+ plugin\(s\) walked")}; the same files: {Same(seg3, seg4) && Same(lnk3, lnk4)}");

            // 5. Over the size a plugin's parts go together, the plugin out of Data longest first (J60): a out, then b
            await RunWith(env, "Service", "stop");
            Out(RA);
            (ok, _, log) = await StartAndSettle(env);
            show = await Call(Cmd("indexes"));
            Check("keptremoved: indexes names a plugin taken out with when it was last in Data",
                ok && Regex.IsMatch(show.Out, @"1 plugin\(s\) not in Data: j43_a\.esm \(last in Data \d{4}-\d\d-\d\d \d\d:\d\d\)"),
                Line(show.Out, "Kept for plugins not in Data"));
            await RunWith(env, "Service", "stop");
            var seg5 = Files(header, "*.seg"); var lnk5 = Files(links, "*.links");
            var all = seg5.Keys.Select(f => Path.Combine(header, f)).Concat(lnk5.Keys.Select(f => Path.Combine(links, f))).ToList();
            var aFiles = all.Where(f => aSeg.Contains(Path.GetFileName(f)) || aLnk.Contains(Path.GetFileName(f))).ToList();
            // b's: of the other two plugins, the bigger segment and link file (the master holds 3 keywords)
            var bFiles = new[] { header, links }.Select(dir => all.Where(f => Path.GetDirectoryName(f) == dir && !aFiles.Contains(f)).OrderByDescending(f => new FileInfo(f).Length).First()).ToList();
            long aBytes = aFiles.Sum(f => new FileInfo(f).Length), bBytes = bFiles.Sum(f => new FileInfo(f).Length);
            int cap = (int)Math.Ceiling(Math.Max(aBytes, bBytes) / 1048576.0);
            bool fits = aFiles.Count == 2 && (long)cap * 1048576 < aBytes + bBytes;
            // By when they were made, a's link file would go first and a's segment last: by plugin, a goes whole
            foreach (var f in aFiles) File.SetLastWriteTimeUtc(f, f.EndsWith(".links", StringComparison.OrdinalIgnoreCase) ? DateTime.UtcNow.AddDays(-4) : DateTime.UtcNow.AddMinutes(-10));
            foreach (var f in bFiles) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-2));
            File.WriteAllText(cfg, Regex.Replace(File.ReadAllText(cfg), @"""keepRemovedPluginIndexMB"":\s*\d+", $"\"keepRemovedPluginIndexMB\": {cap}"));
            Out(RB);
            (ok, _, log) = await StartAndSettle(env);
            bool aGone = aFiles.All(f => !File.Exists(f)), bKept = bFiles.All(File.Exists);
            Check($"keptremoved: over keepRemovedPluginIndexMB {cap} a plugin's parts go together, the one out of Data longest first: a's segment and link file deleted, b's both kept",
                ok && fits && aGone && bKept && Regex.IsMatch(log, $@"Removed the parts of the plugins out of Data longest to stay under {cap} MB: 2 file\(s\) of 1 plugin\(s\) \(j43_a\.esm\)"),
                $"a {aBytes / 1024:N0} KB (out first; its segment made 10 minutes ago, its link file 4 days ago) gone: {aGone} ({string.Join(", ", aFiles.Select(f => Path.GetExtension(f) + (File.Exists(f) ? " kept" : " gone")))}); b {bBytes / 1024:N0} KB (out last, made 2 days ago) kept: {bKept}; {Grab(log, @"Kept for plugins not in Data now[^\n]*")}");
            await RunWith(env, "Service", "stop");

            // 6. A plugin's world link file (J32) goes with its segment and link file: w (world data) out first, then b
            Back(RB);
            var stat = WriteWorldMaster(data);
            WriteWorld(data, stat, placed: 20000);
            File.WriteAllLines(txt, new[] { KMaster, RA, RB, WMaster, WWorld }.Select(p => "*" + p));
            File.WriteAllText(cfg, Regex.Replace(File.ReadAllText(cfg), @"""keepRemovedPluginIndexMB"":\s*\d+", "\"keepRemovedPluginIndexMB\": 100"));
            (ok, _, log) = await StartAndSettle(env);
            var built = await Call(Cmd("indexes", "build", "world"));
            string worldDir = Path.Combine(home, "indexes", "world");
            var wld = Files(worldDir, "*.wlinks");
            await RunWith(env, "Service", "stop");
            // w's world link file, and its segment and link file: all three are named by its reading key
            string wKey = wld.Keys.FirstOrDefault()?.Split('-')[0] ?? "?";
            var wOwn = Files(header, "*.seg").Keys.Where(f => f.StartsWith(wKey + ".", StringComparison.OrdinalIgnoreCase)).Select(f => Path.Combine(header, f))
                .Concat(Files(links, "*.links").Keys.Where(f => f.StartsWith(wKey + "-", StringComparison.OrdinalIgnoreCase)).Select(f => Path.Combine(links, f)))
                .Concat(wld.Keys.Select(f => Path.Combine(worldDir, f))).ToList();
            if (!ok || built.Code != 0 || wld.Count != 1 || wOwn.Count != 3)
            {
                Check("keptremoved: a plugin with world data gets a segment, a link file and a world link file", false, $"{wOwn.Count} file(s), {wld.Count} world; exit {built.Code}: {Line(built.Out, "Deleted")}{built.Err.Trim()}");
                return;
            }
            Out(WWorld);
            (ok, _, log) = await StartAndSettle(env);
            await RunWith(env, "Service", "stop");
            bool wKept = wOwn.All(File.Exists);
            long wBytes = wOwn.Sum(f => new FileInfo(f).Length);
            bBytes = bFiles.Sum(f => new FileInfo(f).Length);
            cap = (int)Math.Ceiling(Math.Max(wBytes, bBytes) / 1048576.0);
            fits = (long)cap * 1048576 < wBytes + bBytes;
            // By when they were made, w's link and world files would go first and its segment last: by plugin, w goes whole
            foreach (var f in wOwn) File.SetLastWriteTimeUtc(f, f.EndsWith(".seg", StringComparison.OrdinalIgnoreCase) ? DateTime.UtcNow.AddMinutes(-10) : f.EndsWith(".wlinks", StringComparison.OrdinalIgnoreCase) ? DateTime.UtcNow.AddDays(-3) : DateTime.UtcNow.AddDays(-4));
            foreach (var f in bFiles) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-2));
            File.WriteAllText(cfg, Regex.Replace(File.ReadAllText(cfg), @"""keepRemovedPluginIndexMB"":\s*\d+", $"\"keepRemovedPluginIndexMB\": {cap}"));
            Out(RB);
            (ok, _, log) = await StartAndSettle(env);
            bool wGone = wOwn.All(f => !File.Exists(f));
            bKept = bFiles.All(File.Exists);
            Check($"keptremoved: over keepRemovedPluginIndexMB {cap} a plugin's world link file goes with its segment and link file: j32_world.esm's three deleted, b's two kept",
                ok && wKept && fits && wGone && bKept && Regex.IsMatch(log, $@"Removed the parts of the plugins out of Data longest to stay under {cap} MB: 3 file\(s\) of 1 plugin\(s\) \(j32_world\.esm\)"),
                $"kept while under the cap: {wKept}; w {wBytes / 1024:N0} KB ({string.Join(", ", wOwn.Select(f => Path.GetExtension(f) + (File.Exists(f) ? " kept" : " gone")))}); b {bBytes / 1024:N0} KB kept: {bKept}; cap holds one: {fits}; {Grab(log, @"Kept for plugins not in Data now[^\n]*")}");
            await RunWith(env, "Service", "stop");
        }
        finally
        {
            await RunWith(env, "Service", "stop");
            Home = outer;
        }
    }

    /// <summary>A plugin of 12,000 keywords (its segment and link file together about 0.6 MB), or of as many as asked.</summary>
    static void WriteKeywords(string data, string name, string prefix, int count = 12000)
    {
        var m = new StarfieldMod(ModKey.FromFileName(name), StarfieldRelease.Starfield);
        for (int i = 0; i < count; i++) m.Keywords.AddNew($"{prefix}{i:D7}");
        WriteMod(m, Path.Combine(data, name), MutagenStyle.Full, null);
    }
}
