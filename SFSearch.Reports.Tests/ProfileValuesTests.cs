// profile --subrecords X --values (GAPS S118): the distinct raw values of a subrecord, by where it stands. Held against a
// plugin written here byte by byte (texts and the forms C63 tested: a leading space, lower case, no closing zero; a
// subrecord in a component and the same at the record's level; long values told apart by their last byte; a value sized
// by XXXX; a compressed record), against profile --subrecords' holder census (the same subrecords counted), and against
// C63's census of the component kinds (every BFCB text is exactly a member of Mutagen's ComponentType; over all 370
// plugins 64 of the 66 occur, never BGSOrbitalDataComponent_Component or LightAnimFormComponent).
using System.Text;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class ProfileValuesTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The two component kinds C63 found in no plugin of Data.</summary>
    static readonly string[] NeverInData = { "BGSOrbitalDataComponent_Component", "LightAnimFormComponent" };

    public static string Values(Ctx ctx)
    {
        var parts = new List<string> { Made(ctx), Official(ctx) };
        try { new SubrecordValuesReport(ctx.Session).Values(new SubrecordsOptions { Signatures = "*", In = "sfta01.esm" }); throw new TestFailed("--values took *"); }
        catch (ReportException e) { Check(e.Message.Contains("--values counts the values of the subrecords named", StringComparison.Ordinal), e.Line); }
        parts.Add("* refused");
        if (ctx.CanRunToday)
        {
            string text = ctx.Today("profile__values", "profile", "--subrecords", "BFCB", "--values", "--type", "STAT", "--in", "sfta01.esm")!;
            Check(text.Contains("| BFCB | the record | \"", StringComparison.Ordinal), "the verb's text: " + text[..Math.Min(1500, text.Length)]);
            string alone = ctx.Today("profile__values__alone", "profile", "--values", "--type", "KYWD")!;
            Check(alone.Contains("no --subrecords was given", StringComparison.Ordinal), "--values alone: " + alone);
            parts.Add("the verb's text; --values alone refused");
        }
        return string.Join("; ", parts);
    }

    static (string, byte[]) Z(string sig, string text) => (sig, Encoding.ASCII.GetBytes(text + "\0"));
    static (string, byte[]) B(string sig, params byte[] bytes) => (sig, bytes);

    /// <summary>
    /// One plugin: WEAP 000800 a keyword component (KWDA 01080000 02080000) and BGSScannable; ARMO 000801 (compressed) the
    /// same keyword component, " BGSScannable" and a KWDA of its own (03080000); MISC 000802 "bgsscannable", BGSScannable
    /// without its closing zero, BGSScannable twice, two 100-byte DATA that differ in their last byte and a 70,000-byte DATA
    /// (sized by XXXX).
    /// </summary>
    static string Made(Ctx ctx)
    {
        string data = ctx.Folder("values-data");
        const string plugin = "j106_values.esm";
        var kwda = B("KWDA", 1, 8, 0, 0, 2, 8, 0, 0);
        (string, byte[]) Bfce = ("BFCE", Array.Empty<byte>());
        var d1 = Enumerable.Repeat((byte)1, 100).ToArray(); var d2 = (byte[])d1.Clone(); d2[99] = 2;
        var big = new byte[70000]; big[0] = 9;
        var weap = ProfileOrderTests.Record("WEAP", 0x800, false, new[] { Z("EDID", "j106W"), Z("BFCB", "BGSKeywordForm_Component"), kwda, Bfce, Z("BFCB", "BGSScannable"), Bfce });
        var armo = ProfileOrderTests.Record("ARMO", 0x801, true, new[] { Z("EDID", "j106A"), Z("BFCB", "BGSKeywordForm_Component"), kwda, Bfce, Z("BFCB", " BGSScannable"), Bfce, B("KWDA", 3, 8, 0, 0) });
        var misc = ProfileOrderTests.Record("MISC", 0x802, false, new[] { Z("EDID", "j106M"), Z("BFCB", "bgsscannable"), Bfce, ("BFCB", Encoding.ASCII.GetBytes("BGSScannable")), Bfce,
            Z("BFCB", "BGSScannable"), Bfce, Z("BFCB", "BGSScannable"), Bfce, ("DATA", d1), ("DATA", d2), ("DATA", big) });
        ProfileFormIdsTests.WritePlugin(ctx, data, plugin, ProfileFormIdsTests.Tes4(0x1),
            ProfileOrderTests.Group("WEAP", new() { weap }), ProfileOrderTests.Group("ARMO", new() { armo }), ProfileOrderTests.Group("MISC", new() { misc }));
        using var session = new ReportSession(data, Path.Combine(ctx.Work, "scratch-values"), ctx.Schema, "sfsreport-tests");
        var a = new SubrecordValuesReport(session).Values(new SubrecordsOptions { Signatures = "bfcb,KWDA,DATA,XNAM", In = plugin });
        ctx.WriteReport("profile-values-made.txt", a.ToText(new ReportTextOptions { Detail = true }));
        string Group(ValueGroup g) => $"{g.Subrecord} [{g.Run}] {g.Uses}/{g.Records}: " + string.Join(" | ", g.Sorted.Select(v => $"{v.Shown} {v.Uses}/{v.Records} {string.Join(",", v.RecordTypes.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}{x.Value}"))} {v.Sample}"));
        var got = a.Groups.Select(Group).ToList();
        var want = new[]
        {
            $"BFCB [] 8/3: \"BGSScannable\" 3/2 MISC1,WEAP1 j106W (000800:{plugin}) | \"BGSKeywordForm_Component\" 2/2 ARMO1,WEAP1 j106W (000800:{plugin}) | \" BGSScannable\" 1/1 ARMO1 j106A (000801:{plugin}) | \"bgsscannable\" 1/1 MISC1 j106M (000802:{plugin}) | 42 47 53 53 63 61 6E 6E 61 62 6C 65 1/1 MISC1 j106M (000802:{plugin})",
            $"KWDA [] 1/1: 03 08 00 00 1/1 ARMO1 j106A (000801:{plugin})",
            $"KWDA [\"BGSKeywordForm_Component\"] 2/2: 01 08 00 00 02 08 00 00 2/2 ARMO1,WEAP1 j106W (000800:{plugin})",
            $"DATA [] 3/1: 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 … (100 bytes) 1/1 MISC1 j106M (000802:{plugin}) | 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 01 … (100 bytes) 1/1 MISC1 j106M (000802:{plugin}) | 09 {string.Join(" ", Enumerable.Repeat("00", 47))} … (70000 bytes) 1/1 MISC1 j106M (000802:{plugin})",
        };
        Check(got.SequenceEqual(want), $"made:\n{string.Join("\n", got)}\nwritten:\n{string.Join("\n", want)}");
        Check(a.Records == 3 && a.Inflated == 1 && a.ToText().Contains("XNAM: no record in", StringComparison.Ordinal), $"made: {a.Records} records, {a.Inflated} inflated; XNAM said");
        return "made plugin: BFCB's 5 texts and forms (a leading space, lower case, no closing zero), KWDA in a component and in the record, 3 long DATA told apart, a compressed record";
    }

    /// <summary>BFCB over the official plugins: every text exactly a ComponentType member (C63), the kinds a subset of
    /// C63's 64, and per record type as many BFCB as profile --subrecords BFCB counts.</summary>
    static string Official(Ctx ctx)
    {
        var (kinds, uses, records) = Census(ctx, "@official");
        return $"@official: {N(uses)} BFCB in {N(records)} records, {kinds} component kinds, each a ComponentType member, none of C63's two never seen";
    }

    static (int Kinds, long Uses, long Records) Census(Ctx ctx, string set)
    {
        var a = new SubrecordValuesReport(ctx.Session).Values(new SubrecordsOptions { Signatures = "BFCB", In = set });
        ctx.WriteReport($"profile-values-bfcb-{set.TrimStart('@')}.txt", a.ToText(new ReportTextOptions { Detail = true }));
        var members = ctx.Schema.Model.Enums[ctx.Schema.Model.Objects[SFSearch.SchemaGen.Selectors.Component].SelectorEnum!].Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        Check(members.Count == 66 && NeverInData.All(members.Contains), $"the schema's ComponentType has {members.Count} members (C63: 66)");
        var top = a.Groups.Where(g => g.Run.Length == 0).ToList();
        Check(top.Count == 1 && a.Groups.Count == 1, $"{set}: BFCB stands in {string.Join(", ", a.Groups.Select(g => g.RunText))} (only in the record expected: components aren't nested)");
        var g = top[0];
        var names = g.Sorted.Select(v => v.Shown).ToList();
        Check(g.Sorted.All(v => v.IsText && v.Shown.Length > 2 && members.Contains(v.Shown[1..^1])), $"{set}: a BFCB that isn't exactly a member: {string.Join(", ", g.Sorted.Where(v => !v.IsText || !members.Contains(v.Shown.Trim('"'))).Select(v => v.Shown))}");
        Check(!names.Any(n => NeverInData.Contains(n.Trim('"'))), $"{set}: one of C63's never-seen kinds is here");
        // The same subrecords as the holder census counts
        var holders = new SubrecordsReport(ctx.Session).Census(new SubrecordsOptions { Signatures = "BFCB", In = set });
        foreach (var h in holders.Holders)
            Check(g.RecordTypes.GetValueOrDefault(h.RecordSig) == h.Records, $"{set} {h.RecordSig}: --values has {g.RecordTypes.GetValueOrDefault(h.RecordSig)} records with a BFCB, the holder census {h.Records}");
        Check(holders.Holders.Sum(h => h.Times) == g.Uses && holders.Holders.Sum(h => h.Records) == g.Records && holders.Holders.Count == g.RecordTypes.Count, $"{set}: --values {g.Uses} BFCB in {g.Records} records, the holder census {holders.Holders.Sum(h => h.Times)} in {holders.Holders.Sum(h => h.Records)}");
        return (g.Values.Count, g.Uses, g.Records);
    }

    /// <summary>Over Data: C63's census of all 370 plugins: 64 of the 66 kinds, each exactly its name.</summary>
    public static string Data(Ctx ctx)
    {
        var (kinds, uses, records) = Census(ctx, "@all");
        Check(kinds == 64, $"@all: {kinds} component kinds (C63: 64 of 66)");
        return $"@all: {N(uses)} BFCB in {N(records)} records (C63: 549,798), 64 kinds, each exactly a ComponentType member; never {string.Join(" or ", NeverInData)}";
    }
}
