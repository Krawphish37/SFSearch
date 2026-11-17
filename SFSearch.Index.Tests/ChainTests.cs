using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SFSearch.Index;

namespace SFSearch.Index.Tests;

/// <summary>
/// GAPS S115: a plugin built on a plugin whose master is missing can't be loaded either. Mutagen's load order drops
/// both (fixes.17, DropPlugin: "a loaded plugin whose master was dropped is dropped too") and xEdit loads neither; the
/// header index leaves the chain out of the winners, as it does a plugin built on one Mutagen refuses.
/// </summary>
static class ChainTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);

    public static string MissingMasterChain(Ctx ctx)
    {
        var data = ctx.DataIndex;
        var a = data.Plugin("atom74.esm") ?? throw new TestFailed("atom74.esm isn't in Data");
        Check(a.Header!.Masters.SequenceEqual(new[] { "Starfield.esm" }, StringComparer.OrdinalIgnoreCase), $"atom74.esm's masters are {string.Join(", ", a.Header.Masters)}, not Starfield.esm alone");
        string folder = ctx.Folder("chain");
        string copy = Path.Combine(folder, a.Name);
        File.Copy(a.Path, copy, overwrite: true);
        // B masters atom74.esm and holds one keyword of its own; C masters B
        string b = Write(folder, "sfs_chain_b.esm", (a.Name, Mutagen.Bethesda.Plugins.MasterStyle.Small), "sfs_chain_B");
        string c = Write(folder, "sfs_chain_c.esm", ("sfs_chain_b.esm", Mutagen.Bethesda.Plugins.MasterStyle.Full), "sfs_chain_C");
        string txt = Path.Combine(folder, "plugins.txt");
        File.WriteAllLines(txt, new[] { "*atom74.esm", "*sfs_chain_b.esm", "*sfs_chain_c.esm" });

        string said;
        using (var index = new HeaderIndex(PluginSource.FromFiles(new[] { copy, b, c }), ctx.Options(pluginsTxt: txt)))
        {
            index.Refresh();
            var pa = index.Plugin("atom74.esm")!; var pb = index.Plugin("sfs_chain_b.esm")!; var pc = index.Plugin("sfs_chain_c.esm")!;
            Check(pa.Order.Enabled && pb.Order.Enabled && pc.Order.Enabled, "the three plugins aren't enabled by the test's plugins.txt");
            Check(pa.Problems.MissingMasters.SequenceEqual(new[] { "Starfield.esm" }) && pa.CantLoad && pa.LeftOut == null, $"atom74.esm without Starfield.esm: missing {string.Join(", ", pa.Problems.MissingMasters)}, can't load {pa.CantLoad}, left out \"{pa.LeftOut}\"");
            Check(pb.Problems.MissingMasters.Count == 0 && pb.Problems.MasterUnreadable == "atom74.esm" && pb.CantLoad && pb.LeftOut != null && pb.LeftOutShort == "a master can't be loaded",
                $"B, built on atom74.esm: master unreadable \"{pb.Problems.MasterUnreadable}\", can't load {pb.CantLoad}, left out \"{pb.LeftOut}\" ({pb.LeftOutShort})");
            Check(pc.Problems.MasterUnreadable == "sfs_chain_b.esm" && pc.CantLoad && pc.LeftOut != null, $"C, built on B: master unreadable \"{pc.Problems.MasterUnreadable}\", left out \"{pc.LeftOut}\"");
            Check(pb.Problems.MasterRefused == null && pc.Problems.MasterRefused == null, "the chain of a missing master is called a refusal by Mutagen");
            Check(pb.Problems.Lines().Any(l => l.Contains("can't be loaded (a master of it isn't in Data)")), $"B's problems don't say why: {string.Join("; ", pb.Problems.Lines())}");
            foreach (var p in new[] { pb, pc })
            {
                var own = Own(index, p);
                Check(own != null && own.Winner < 0, $"{p.Name}'s own keyword has a winner: it should be left out of the winners");
            }
            said = $"atom74.esm without Starfield.esm: missing master, can't be loaded; B (masters atom74.esm) and C (masters B): can't be loaded, left out (\"{pb.LeftOut}\"; \"{pc.LeftOut}\"), their own records win nowhere";
        }
        // With the master there, the chain loads: nothing is left out, B's and C's keywords win
        using (var whole = new HeaderIndex(PluginSource.FromFiles(new[] { data.Plugin("Starfield.esm")!.Path, copy, b, c }), ctx.Options(pluginsTxt: txt)))
        {
            whole.Refresh();
            foreach (var name in new[] { "atom74.esm", "sfs_chain_b.esm", "sfs_chain_c.esm" })
            {
                var p = whole.Plugin(name)!;
                Check(!p.CantLoad && p.LeftOut == null && p.Problems.MasterUnreadable == null, $"{name} with Starfield.esm there: can't load {p.CantLoad}, left out \"{p.LeftOut}\"");
            }
            foreach (var name in new[] { "sfs_chain_b.esm", "sfs_chain_c.esm" })
                Check(Own(whole, whole.Plugin(name)!) is { Winner: >= 0 }, $"{name}'s own keyword has no winner with every master there");
        }
        return said + "; with Starfield.esm there, all three load and B's and C's keywords win";
    }

    static RecordVersions? Own(HeaderIndex index, IndexedPlugin p)
    {
        var seg = p.Segment!;
        return seg.FirstUnresolved > seg.FirstOwn ? index.Versions(p.Name, seg[seg.FirstOwn].LocalId) : null;
    }

    /// <summary>A plugin with one master and one keyword of its own, written by Mutagen.</summary>
    static string Write(string folder, string name, (string Name, Mutagen.Bethesda.Plugins.MasterStyle Style) master, string keyword)
    {
        var mod = new StarfieldMod(ModKey.FromFileName(name), StarfieldRelease.Starfield);
        mod.ModHeader.MasterReferences.Add(new MasterReference { Master = ModKey.FromFileName(master.Name) });
        mod.Keywords.AddNew(keyword);
        var order = new LoadOrder<IModMasterStyledGetter>(new IModMasterStyledGetter[]
        {
            new KeyedMasterStyle(ModKey.FromFileName(master.Name), master.Style), new KeyedMasterStyle(mod.ModKey, Mutagen.Bethesda.Plugins.MasterStyle.Full),
        });
        string path = Path.Combine(folder, name);
        mod.WriteToBinary(path, new BinaryWriteParameters { MasterFlagsLookup = order, ModKey = ModKeyOption.CorrectToPath, MastersListContent = MastersListContentOption.NoCheck });
        return path;
    }
}
