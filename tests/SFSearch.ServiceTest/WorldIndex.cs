using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using MutagenStyle = Mutagen.Bethesda.Plugins.MasterStyle;

namespace SFSearch.ServiceTest;

/// <summary>
/// `worldindex --exe &lt;SFSearch.exe&gt;` (J32, 2026-11-15): the world link index, made only when asked, through the
/// service. Its own home and test Data folder under the test home, with two plugins Mutagen writes there (never into the
/// game's Data): a master with one static, and a plugin with an interior cell whose placed objects use it. Real restarts
/// only; no test hook. Checks:
///   1. not made by itself: indexes says "not asked for", refs reads world data by its bytes;
///   2. indexes build world makes it (one file, the plugin asked for kept in wanted.txt), and indexes shows it;
///   3. refs answers from it, with the same rows as the byte search; query2's Links WHERE ToKey gets the world rows;
///   4. kept across a restart (the same file, not made again);
///   5. a changed plugin gets a new file at the next start (the old one deleted), and refs sees the change;
///   6. indexes drop world deletes it and forgets the choice: refs reads the bytes again, a restart doesn't make it.
/// Stops its service at the end.
/// </summary>
public static partial class T
{
    const string WMaster = "j32_master.esm", WWorld = "j32_world.esm";

    static async Task WorldIndex()
    {
        if (!File.Exists(Exe)) { Check("worldindex: --exe names SFSearch.exe", false, Exe); return; }
        string root = Path.Combine(Home, "worldindex"), home = Path.Combine(root, "home"), data = Path.Combine(root, "plugins"), txt = Path.Combine(root, "plugins.txt");
        if (Path.GetFullPath(data).Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)) { Check("worldindex: the test Data folder is outside the game's", false, data); return; }
        string outer = Home;
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(home); Directory.CreateDirectory(data);
        var stat = WriteWorldMaster(data);
        WriteWorld(data, stat, placed: 30);
        string key = $"{stat.ID:X6}:{WMaster}";
        File.WriteAllLines(txt, new[] { WMaster, WWorld }.Select(p => "*" + p));
        var env = new Dictionary<string, string> { ["SFSEARCH_HOME"] = home, ["SFSEARCH_DATA"] = data, ["SFSEARCH_PLUGINS_TXT"] = txt, ["SFSEARCH_WORKERS"] = "2" };
        string world = Path.Combine(home, "indexes", "world"), wanted = Path.Combine(world, "wanted.txt");
        static List<string> Rows(string answer) => answer.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("| ") && !l.StartsWith("| Record") && !l.StartsWith("| :")).ToList();
        Home = home;
        try
        {
            // 1. Not made by itself
            var (ok, _, log) = await StartAndSettle(env);
            if (!ok) { Check("worldindex: the service starts and makes the header and link indexes", false, log); return; }
            var show = await Call(Cmd("indexes"));
            Check("worldindex: not made by itself: indexes says the world link index isn't asked for, and no file is made",
                show.Code == 0 && Regex.IsMatch(show.Out, @"\| world \| [^|]+ \| 0 of 1 plugins with world data \(not asked for\)") && show.Out.Contains("World link index: not asked for.") && Files(world, "*.wlinks").Count == 0,
                $"{Line(show.Out, "| world |")}; {Files(world, "*.wlinks").Count} file(s)");
            var bytes = await Call(Cmd("refs2", key, "--world", "--limit", "100"));
            var bytesRows = Rows(bytes.Out);
            Check("worldindex: refs without it reads world data by its bytes (30 placed objects)",
                bytes.Code == 0 && bytes.Out.Contains("30 records point at j32_stat") && bytes.Out.Contains("by its bytes") && bytesRows.Count == 30,
                $"{Line(bytes.Out, "point at")}; {Line(bytes.Out, "Searched:")}");

            // 2. Made when asked
            var built = await Call(Cmd("indexes", "build", "world"));
            var files0 = Files(world, "*.wlinks");
            Check("worldindex: indexes build world makes one world link file and keeps the choice in wanted.txt",
                built.Code == 0 && built.Out.Contains("World link index: 1 plugin(s) made") && files0.Count == 1 && File.Exists(wanted) && File.ReadAllLines(wanted).Contains("*"),
                $"{Line(built.Out, "World link index:")}; {files0.Count} file(s); wanted.txt {(File.Exists(wanted) ? string.Join(" ", File.ReadAllLines(wanted).Where(l => !l.StartsWith('#'))) : "missing")}");
            show = await Call(Cmd("indexes"));
            Check("worldindex: indexes shows it: 1 of 1 plugins with world data, asked for",
                show.Code == 0 && Regex.IsMatch(show.Out, @"\| world \| [\d.,]+ (bytes|KB|MB) \| 1 of 1 plugins with world data \(asked for: all\)"), Line(show.Out, "| world |"));

            // 3. refs and query2 answer from it
            var fromIndex = await Call(Cmd("refs2", key, "--limit", "100"));
            var indexRows = Rows(fromIndex.Out);
            Check("worldindex: refs answers from it by default, with the same rows as the byte search",
                fromIndex.Code == 0 && fromIndex.Out.Contains("30 records point at j32_stat") && fromIndex.Out.Contains("from the world link index of 1 plugin") && !fromIndex.Out.Contains("by its bytes") && indexRows.SequenceEqual(bytesRows),
                $"{Line(fromIndex.Out, "point at")}; {Line(fromIndex.Out, "Searched:")}; rows the same: {indexRows.SequenceEqual(bytesRows)}");
            var q = await Call(Cmd("query2", $"SELECT FromKey, FromType, Field FROM Links WHERE ToKey = '{key}';", "--rows", "100"));
            Check("worldindex: query2's Links WHERE ToKey gets the world rows from it",
                q.Code == 0 && Regex.Matches(q.Out, @"\| PlacedObject \| Base \|").Count == 30 && q.Out.Contains("came from the world link index of 1 plugin"),
                $"{Regex.Matches(q.Out, @"\| PlacedObject \| Base \|").Count} PlacedObject rows; {Line(q.Out, "world link index")}");

            // 4. Kept across a restart
            await RunWith(env, "Service", "stop");
            (ok, _, log) = await StartAndSettle(env);
            var files1 = Files(world, "*.wlinks");
            var again = await Call(Cmd("refs2", key, "--limit", "100"));
            Check("worldindex: kept across a restart: the same file, and refs answers from it",
                ok && Same(files0, files1) && again.Out.Contains("from the world link index of 1 plugin") && Rows(again.Out).SequenceEqual(bytesRows),
                $"the same file: {Same(files0, files1)}; {Line(again.Out, "Searched:")}");

            // 5. A changed plugin gets a new file
            await RunWith(env, "Service", "stop");
            WriteWorld(data, stat, placed: 31);
            (ok, _, log) = await StartAndSettle(env);
            var files2 = Files(world, "*.wlinks");
            var changed = await Call(Cmd("refs2", key, "--limit", "100"));
            Check("worldindex: a changed plugin gets a new world link file at the next start, and the old one is deleted",
                ok && files2.Count == 1 && !files2.Keys.Intersect(files0.Keys, StringComparer.OrdinalIgnoreCase).Any() && Regex.IsMatch(log, @"world link index: 1 of 1 plugins[^\n]*This build: 1 plugin\(s\)") && log.Contains("1 world link file(s)"),
                $"{files2.Count} file(s), new name: {!files2.Keys.Intersect(files0.Keys, StringComparer.OrdinalIgnoreCase).Any()}; {Grab(log, @"world link index: [^\n]*")}; {Grab(log, @"Removed as no plugin uses them[^.]*")}");
            Check("worldindex: refs sees the change from the new file (31 placed objects)",
                changed.Code == 0 && changed.Out.Contains("31 records point at j32_stat") && changed.Out.Contains("from the world link index of 1 plugin"), Line(changed.Out, "point at"));

            // 6. Dropped and forgotten
            var drop = await Call(Cmd("indexes", "drop", "world"));
            var afterDrop = await Call(Cmd("refs2", key, "--world", "--limit", "100"));
            Check("worldindex: indexes drop world deletes it and forgets the choice; refs reads the bytes again",
                drop.Code == 0 && drop.Out.Contains("world link index") && Files(world, "*.wlinks").Count == 0 && !File.Exists(wanted) && afterDrop.Out.Contains("31 records point at j32_stat") && afterDrop.Out.Contains("by its bytes"),
                $"{Line(drop.Out, "Deleted")}; {Files(world, "*.wlinks").Count} file(s); wanted.txt {(File.Exists(wanted) ? "still there" : "gone")}; {Line(afterDrop.Out, "Searched:")}");
            await RunWith(env, "Service", "stop");
            (ok, _, log) = await StartAndSettle(env);
            Check("worldindex: a restart after the drop doesn't make it again", ok && Files(world, "*.wlinks").Count == 0 && !log.Contains("world link index:"), $"{Files(world, "*.wlinks").Count} file(s); {Grab(log, @"world link index: [^\n]*")}");
            var status = await Call(Cmd("status"));
            Check("worldindex: sfs_status's indexes line says the world link index is made only when asked", status.Out.Contains("world none (made only when asked: indexes build world)"), Line(status.Out, "indexes:"));
            await RunWith(env, "Service", "stop");
        }
        finally
        {
            await RunWith(env, "Service", "stop");
            Home = outer;
        }
    }

    /// <summary>A master with one static: the record the placed objects use.</summary>
    static FormKey WriteWorldMaster(string data)
    {
        var m = new StarfieldMod(ModKey.FromFileName(WMaster), StarfieldRelease.Starfield);
        var stat = m.Statics.AddNew("j32_stat");
        WriteWorldMod(m, Path.Combine(data, WMaster), withMaster: false);
        return stat.FormKey;
    }

    /// <summary>A plugin with one interior cell holding <paramref name="placed"/> placed objects of the master's static.</summary>
    static void WriteWorld(string data, FormKey stat, int placed)
    {
        var m = new StarfieldMod(ModKey.FromFileName(WWorld), StarfieldRelease.Starfield);
        var cell = new Cell(m) { EditorID = "j32_cell", Flags = Cell.Flag.IsInteriorCell };
        for (int i = 0; i < placed; i++)
        {
            var o = new PlacedObject(m);
            o.Base.SetTo(stat);
            cell.Temporary.Add(o);
        }
        m.Cells.Add(new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock, SubBlocks = { new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = { cell } } } });
        WriteWorldMod(m, Path.Combine(data, WWorld), withMaster: true);
    }

    static void WriteWorldMod(StarfieldMod mod, string path, bool withMaster)
    {
        if (Path.GetFullPath(path).Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("never into the game's Data folder");
        var order = new List<KeyedMasterStyle>();
        if (withMaster) order.Add(new KeyedMasterStyle(ModKey.FromFileName(WMaster), MutagenStyle.Full));
        order.Add(new KeyedMasterStyle(mod.ModKey, MutagenStyle.Full));
        var lookup = new LoadOrder<IModMasterStyledGetter>(order);
        mod.WriteToBinary(path, new BinaryWriteParameters
        {
            MasterFlagsLookup = lookup, ModKey = ModKeyOption.CorrectToPath, MastersListOrdering = new MastersListOrderingByLoadOrder(lookup),
            LowerRangeDisallowedHandler = new NoCheckIfLowerRangeDisallowed(), FormIDCompaction = FormIDCompactionOption.NoCheck, HeaderSizeFlags = HeaderSizeFlagsOption.NoCheck,
        });
    }
}
