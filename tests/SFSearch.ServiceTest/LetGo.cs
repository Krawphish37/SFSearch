using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using MutagenStyle = Mutagen.Bethesda.Plugins.MasterStyle;

namespace SFSearch.ServiceTest;

/// <summary>
/// `letgo --exe &lt;SFSearch.exe&gt;` (GAPS S215, 2026-11-19): a mod's or a patch's file is held only while a command reads
/// it, so another program that wants the file for itself alone (the All-in-One Patcher tests its old patch with
/// File.Open(…, FileShare.None) before it writes the new one) gets it while the service runs. Its own home and test
/// Data folder under the test home, with three plugins Mutagen writes there (never into the game's Data): a master, a
/// patch of it, and a plugin named as one of Bethesda's Creations are (sfbgs…), which stays open. Checks:
///   1. after the start's indexing, and after each kind of read (a query of the patch on both workers at once, its
///      checks, a record of the load order, a query of every plugin), the patch and its master open exclusively;
///   2. the plugin named as one of the game's own stays open, and status counts it alone;
///   3. the patcher's own steps (the exclusive open, then the new file moved over the old one) work, and the next
///      query reads the new file.
/// Stops its service at the end.
/// </summary>
public static partial class T
{
    const string LOfficial = "sfbgs_j147.esm";

    static async Task LetGo()
    {
        if (!File.Exists(Exe)) { Check("letgo: --exe names SFSearch.exe", false, Exe); return; }
        string root = Path.Combine(Home, "letgo"), home = Path.Combine(root, "home"), data = Path.Combine(root, "plugins"), txt = Path.Combine(root, "plugins.txt");
        if (Path.GetFullPath(data).Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)) { Check("letgo: the test Data folder is outside the game's", false, data); return; }
        string outer = Home;
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(home); Directory.CreateDirectory(data);
        string master = Path.Combine(data, KMaster), patch = Path.Combine(data, KPatch), official = Path.Combine(data, LOfficial);
        WriteMaster(data, small: false);
        WriteLetGoPatch(patch, "j147_list_old");
        var o = new StarfieldMod(ModKey.FromFileName(LOfficial), StarfieldRelease.Starfield);
        for (int i = 0; i < 3; i++) o.Keywords.AddNew($"j147_official_kw_{i}");
        WriteMod(o, official, MutagenStyle.Full, null);
        File.WriteAllLines(txt, new[] { KMaster, KPatch, LOfficial }.Select(p => "*" + p));
        var env = new Dictionary<string, string> { ["SFSEARCH_HOME"] = home, ["SFSEARCH_DATA"] = data, ["SFSEARCH_PLUGINS_TXT"] = txt, ["SFSEARCH_WORKERS"] = "2" };
        Home = home;
        try
        {
            var (ok, _, log) = await StartAndSettle(env);
            if (!ok) { Check("letgo: the service starts and makes its indexes", false, log); return; }
            Both("letgo: after the start's indexing the patch and its master open exclusively");

            // 1. Each kind of read, then the exclusive open
            var two = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Call(Cmd("query2", KPatch, "SELECT EditorID FROM FormList;"))));
            Check("letgo: four queries of the patch at once answer", two.All(r => r.Code == 0 && r.Out.Contains("j147_list_old")), string.Join(" / ", two.Select(r => $"exit {r.Code}")));
            Both("letgo: after queries of the patch on both workers");
            var check = await Call(Cmd("check2", KPatch));
            Check("letgo: the patch's checks run", check.Code == 0, $"exit {check.Code}: {check.Out.Split('\n').FirstOrDefault()?.Trim()}");
            Both("letgo: after the patch's checks");
            var rec = await Call(Cmd("record", "j147_list_old"));
            Check("letgo: the patch's record is shown", rec.Code == 0 && rec.Out.Contains("j31_master_kw_0"), $"exit {rec.Code}");
            Both("letgo: after a record of the load order");
            var all = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Call(Cmd("query2", "SELECT Plugin, count(*) AS N FROM Keyword GROUP BY Plugin;"))));
            Check("letgo: four queries of every plugin at once answer", all.All(r => r.Code == 0 && r.Out.Contains(KMaster) && r.Out.Contains(LOfficial)), string.Join(" / ", all.Select(r => $"exit {r.Code}")));
            Both("letgo: after queries of every plugin");

            // 2. The plugin named as one of the game's own stays open; status counts it alone
            string held = Exclusive(official) ?? "";
            Check("letgo: a plugin named as one of the game's own stays open", held.Length > 0, held.Length > 0 ? held : "it opened exclusively");
            var status = await Call(Cmd("status"));
            var counts = Regex.Match(status.Out, @"(?m)^Open plugins \(the game's own plugins a worker has read stay open; a mod's or a patch's file is let go when its command ends\), by worker: (.*)\.\s*$");
            var each = Regex.Matches(counts.Groups[1].Value, @"worker \d+: (\d+)").Select(m => int.Parse(m.Groups[1].Value)).ToList();
            Check("letgo: status counts the game's own plugin alone (0 or 1 a worker, 1 at least)", counts.Success && each.Count == 2 && each.All(n => n <= 1) && each.Sum() >= 1, counts.Success ? counts.Groups[1].Value : Line(status.Out, "Open plugins"));

            // 3. The patcher's steps: the exclusive open of the old patch, then the new file moved over it
            string tmp = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "staged")).FullName, KPatch);
            WriteLetGoPatch(tmp, "j147_list_new");
            string? refused = Exclusive(patch);
            string moved = "";
            try { File.Move(tmp, patch, overwrite: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { moved = ex.Message; }
            Check("letgo: the patcher's exclusive open and its move of the new patch over the old one work", refused == null && moved.Length == 0, refused ?? moved);
            var again = await Call(Cmd("query2", KPatch, "SELECT EditorID FROM FormList;"));
            Check("letgo: the next query reads the new patch", again.Code == 0 && again.Out.Contains("j147_list_new") && !again.Out.Contains("j147_list_old"), $"exit {again.Code}: {Line(again.Out, "j147_list")}");
            Both("letgo: after the query of the new patch");
        }
        finally
        {
            await RunWith(env, "Service", "stop");
            Home = outer;
        }

        void Both(string name)
        {
            string? p = Exclusive(patch), m = Exclusive(master);
            Check(name, p == null && m == null, p == null && m == null ? "both opened with FileShare.None" : $"{KPatch}: {p ?? "opened"}; {KMaster}: {m ?? "opened"}");
        }
    }

    /// <summary>The All-in-One Patcher's test of a file it is about to replace: null when the file opens for this program alone, or why it doesn't.</summary>
    static string? Exclusive(string path)
    {
        try { using var f = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
    }

    static void WriteLetGoPatch(string path, string listName)
    {
        var master = new StarfieldMod(ModKey.FromFileName(KMaster), StarfieldRelease.Starfield);
        for (int i = 0; i < 3; i++) master.Keywords.AddNew($"j31_master_kw_{i}");
        var p = new StarfieldMod(ModKey.FromFileName(KPatch), StarfieldRelease.Starfield);
        var list = p.FormLists.AddNew(listName);
        foreach (var k in master.Keywords) list.Items.Add(new FormLink<IStarfieldMajorRecordGetter>(k.FormKey));
        WriteMod(p, path, MutagenStyle.Full, MutagenStyle.Full);
    }
}
