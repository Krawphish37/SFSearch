// assets reads a record's file paths and armor addon links from its bytes where the schema places every subrecord
// (AssetFields, GAPS S30). Held here against what PathWalker and LinkWalker find in Mutagen's JSON of the same record,
// for every record of a set of plugins: the same paths in the same order with the same values and kinds, and the same
// armor addon links. A record the byte reader leaves to Mutagen is counted, not compared.
using System.Globalization;
using SFSearch.Compare;
using SFSearch.Index;

namespace SFSearch.Reports.Tests;

static class AssetFieldsTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The report's three test plugins, a master whose armor addons a patch shows, and plugins with many kinds
    /// of records: weapons with components, armors with armatures, furniture, books, misc items, ammunition, projectiles.</summary>
    public static readonly string[] Plugins =
    {
        "atom74.esm", "darkstarpatch_nomadspacesuit.esm", "Nomad_Spacesuit_Z.esm", "darkstarpatch_bountyhunteroutfitz.esm", "BountyHunter_Outfit_Z.esm",
        "aa50_longwatch.esm", "apc_galaxysmartcontainers.esm", "kgcdoom.esm", "SFBGS003.esm", "SFBGS007.esm", "SFBGS008.esm", "darkstar.esm",
    };

    public static string Parity(Ctx ctx)
    {
        var s = ctx.Session;
        var report = new AssetsReport(s);
        var walker = report.Walker;
        var linkWalker = new LinkWalker(s.Schema);
        long records = 0, fromBytes = 0, paths = 0, links = 0, plugins = 0;
        var mutagenBySig = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var plugin in Plugins)
        {
            var p = s.Index.Plugin(plugin);
            if (p?.Segment == null) continue;
            plugins++;
            var seg = p.Segment;
            s.Engine.Sessions.Use(p.Name);
            for (int i = 0; i < seg.FirstUnresolved; i++)
            {
                var e = seg[i];
                string sig = seg.SignatureOf(e);
                if (ReportSession.IsWorld(seg, e) || sig == "NAVI") continue;
                var hit = new Hit(s.Engine, new RecordRef(p.Id, i));
                records++;
                var raw = report.FromBytes(hit);
                if (raw == null) { mutagenBySig[sig] = mutagenBySig.GetValueOrDefault(sig) + 1; continue; }
                fromBytes++;
                RecordView view;
                try { view = s.Compare.View(s.Engine, hit.Ref); }
                catch (CompareException ex) { Check(false, $"{plugin} {hit.Info.Label}: read from its bytes, but Mutagen can't read it: {ex.Message}"); continue; }
                var want = new List<PathFound>();
                walker.Walk(view.Type, view.Json, want.Add);
                var wantLinks = new List<AddonLink>();
                linkWalker.Walk(view.Type, view.Json, l => { if (l.LinkTo == "ArmorAddon") wantLinks.Add(new AddonLink(l.Path, ReportSession.Canon(l.FormKey))); });
                var gotLinks = raw.AddonLinks.Select(l => l with { FormKey = ReportSession.Canon(l.FormKey) }).ToList();
                Check(want.SequenceEqual(raw.Paths), $"{plugin} {hit.Info.Label} {sig}: from its bytes [{string.Join("; ", raw.Paths)}], from Mutagen's JSON [{string.Join("; ", want)}]");
                Check(wantLinks.Count == gotLinks.Count && wantLinks.Zip(gotLinks).All(x => x.First.Path == x.Second.Path && Ctx.Key(x.First.FormKey) == Ctx.Key(x.Second.FormKey)),
                    $"{plugin} {hit.Info.Label} {sig}: armor addon links from its bytes [{string.Join("; ", gotLinks)}], from Mutagen's JSON [{string.Join("; ", wantLinks)}]");
                paths += want.Count; links += wantLinks.Count;
            }
        }
        Check(plugins >= 10, $"only {plugins} of the test plugins are in Data");
        string left = string.Join(", ", mutagenBySig.OrderByDescending(k => k.Value).Take(40).Select(k => $"{k.Key} {N(k.Value)}"));
        // The three plugins of the speed check are read wholly from their bytes
        foreach (var plugin in new[] { "atom74.esm", "darkstarpatch_nomadspacesuit.esm" })
        {
            var a = report.Assets(plugin);
            Check(a.Read.Any(r => r.Contains("(all from their path fields' bytes)", StringComparison.Ordinal)), $"{plugin}: not every record was read from its bytes: {string.Join("; ", a.Read)}. Left to Mutagen: {left}");
        }
        string kinds = ComponentKindsAsMutagen(s.Schema.Model);
        return $"{N(records)} records of {plugins} plugins: {N(fromBytes)} read from their bytes, each with the same {N(paths)} paths and {N(links)} armor addon links as Mutagen's JSON gives; {N(records - fromBytes)} left to Mutagen ({left}); {kinds}";
    }

    /// <summary>
    /// GAPS S64: the component a BFCB text picks, as the schema says it (the XML's selectorValue through
    /// Selectors.ComponentKind, no question to Mutagen), is the one Mutagen's own reader makes, for every member of
    /// AComponent.ComponentType and for texts that are no exact name (white space, numbers, names joined by commas,
    /// letter case, text after the zero byte, no zero byte, bytes over 0x7F, nothing), which Mutagen reads as
    /// UnknownComponent since fixes.20 (C63).
    /// </summary>
    static string ComponentKindsAsMutagen(SFSearch.SchemaGen.SchemaModel model)
    {
        var key = Mutagen.Bethesda.Plugins.ModKey.FromFileName("ReportTestsComponents.esm");
        var meta = new Mutagen.Bethesda.Plugins.Binary.Streams.ParsingMeta(Mutagen.Bethesda.Plugins.Meta.GameConstants.Starfield, key,
            Mutagen.Bethesda.Plugins.Masters.SeparatedMasterPackage.Factory(Mutagen.Bethesda.GameRelease.Starfield, key, Mutagen.Bethesda.Plugins.MasterStyle.Full,
                new Mutagen.Bethesda.Plugins.Masters.MasterReferenceCollection(key), null));
        string? Mutagens(byte[] content)
        {
            var bytes = new List<byte>();
            bytes.AddRange("BFCB"u8.ToArray()); bytes.AddRange(BitConverter.GetBytes((ushort)content.Length)); bytes.AddRange(content);
            bytes.AddRange("BFCE"u8.ToArray()); bytes.Add(0); bytes.Add(0);
            try
            {
                var frame = new Mutagen.Bethesda.Plugins.Binary.Streams.MutagenFrame(new Mutagen.Bethesda.Plugins.Binary.Streams.MutagenMemoryReadStream(bytes.ToArray(), meta));
                return Mutagen.Bethesda.Starfield.AComponent.CreateFromBinary(frame, default).GetType().Name;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException or NotImplementedException or OverflowException)
            {
                return null;
            }
        }
        static byte[] Z(string text) => System.Text.Encoding.Latin1.GetBytes(text + "\0");
        var members = Enum.GetNames<Mutagen.Bethesda.Starfield.AComponent.ComponentType>();
        var texts = members.Select(Z).ToList();
        foreach (var odd in new[] { " BGSScannable", "BGSScannable ", "\tBGSScannable", "BGSScannable\u00A0", "5", "+5", " 5 ", "-1", "65", "66", "007", "99999999999", "5x",
                     "BGSScannable,BGSActivityTracker", "BGSScannable, BGSActivityTracker", "BGSScannable,", ",", "bgsscannable", "BGSSCANNABLE", "", " ", "Nothing_Component" })
            texts.Add(Z(odd));
        texts.Add("BGSScannable\0junk\0"u8.ToArray());
        texts.Add("BGSScannable"u8.ToArray());
        texts.Add(new byte[] { (byte)'B', (byte)'G', (byte)'S', 0xE9, 0 });
        int named = 0;
        foreach (var t in texts)
        {
            string? want = Mutagens(t), got = SFSearch.SchemaGen.Selectors.ComponentKind(model, t);
            Check(want == got, $"BFCB \"{System.Text.Encoding.Latin1.GetString(t).Replace("\0", "\\0")}\": the schema picks {got ?? "nothing"}, Mutagen {want ?? "nothing"}");
            if (want != null) named++;
        }
        return $"the component a BFCB text picks is Mutagen's for all {members.Length} kinds and {texts.Count - members.Length} odd texts ({named} of {texts.Count} texts pick one)";
    }
}
