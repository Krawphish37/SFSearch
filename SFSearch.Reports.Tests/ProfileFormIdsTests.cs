// profile --formids (GAPS S119): how the records' FormIDs are stored, by plugin and record type. Held against plugins
// written here byte by byte (every stored form known: a master's record in each numbering, the own record in the full
// index and the Small form, past the master list, the Medium form in a Small plugin, null and all ones), against
// check2's own-FormID counts on a Small and a Medium official-size plugin (darkstarpatch_bublumi.esm, sfta04.esm), against
// profile --subrecords "*"'s link census (the same links), and over Data against C64's count of the FormIDs stored past
// the master list (4,112 in 14 plugins; the Mutagen session's scratch tool, GetStoredFormIds).
using System.Buffers.Binary;
using System.Text;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class ProfileFormIdsTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    public static string FormIds(Ctx ctx)
    {
        var parts = new List<string> { Made(ctx), Real(ctx), SameLinks(ctx) };
        if (ctx.CanRunToday)
        {
            string text = ctx.Today("profile__formids", "profile", "--formids", "--in", "darkstarpatch_bublumi.esm")!;
            Check(text.Contains("116 03xxxxxx, 102 FE001xxx", StringComparison.Ordinal), "the verb's text: " + text[..Math.Min(1500, text.Length)]);
            string both = ctx.Today("profile__formids__fields", "profile", "--formids", "Weapon.AttackDamage")!;
            Check(both.Contains("--formids was given with fields", StringComparison.Ordinal), "--formids with a field: " + both);
            parts.Add("the verb's text; --formids with a field refused");
        }
        return string.Join("; ", parts);
    }

    /// <summary>A plugin header (TES4) written by hand: its flags and its masters.</summary>
    internal static byte[] Tes4(uint flags, params string[] masters)
    {
        var subs = new List<(string, byte[])> { ("HEDR", new byte[12]) };
        BinaryPrimitives.WriteSingleLittleEndian(subs[0].Item2, 0.96f);
        BinaryPrimitives.WriteUInt32LittleEndian(subs[0].Item2.AsSpan(8), 0x900);
        foreach (var m in masters) { subs.Add(("MAST", Encoding.ASCII.GetBytes(m + "\0"))); subs.Add(("DATA", new byte[8])); }
        var r = ProfileOrderTests.Record("TES4", 0, false, subs);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(8), flags);
        return r;
    }

    /// <summary>Writes a plugin of hand-made bytes into a Data folder of the test's own.</summary>
    internal static void WritePlugin(Ctx ctx, string data, string name, byte[] header, params byte[][] groups)
    {
        string path = Path.Combine(data, name);
        ctx.MustBeInWork(path);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(header);
        foreach (var g in groups) fs.Write(g);
    }

    static (string, byte[]) Edid(string name) => ("EDID", Encoding.ASCII.GetBytes(name + "\0"));
    static (string, byte[]) U32(string sig, uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return (sig, b); }
    static byte[] Kywd(uint id, string edid) => ProfileOrderTests.Record("KYWD", id, false, new[] { Edid(edid) });

    const string Test = "j107_test.esm";

    /// <summary>
    /// Three masters (Full, Medium, Small; each with a keyword 0x800) and a Small plugin with all three as masters, so
    /// each numbering has one master: the plugin's own records are 01xxxxxx, FD01xxxx and FE001xxx. Its keywords' headers:
    /// 00000800 (an override of the Full master's), FE001801 (its own, the Small form), 01000802 (its own, the full index),
    /// 02000803 (past the master list), FE002804 (past the Small masters), FD010805 (its own in the Medium form: not a
    /// Small plugin's). Its form list FE001806 (compressed) links in LNAM to each of the nine forms: 00000800, FD000800,
    /// FE000800, FE001801, 01000802, 02000803, FD020001 (past the Medium masters, in a Small plugin), 0 and FFFFFFFF.
    /// </summary>
    static string Made(Ctx ctx)
    {
        string data = ctx.Folder("formids-data");
        WritePlugin(ctx, data, "j107_full.esm", Tes4(0x1), ProfileOrderTests.Group("KYWD", new() { Kywd(0x00000800, "j107FullKw") }));
        WritePlugin(ctx, data, "j107_medium.esm", Tes4(0x401), ProfileOrderTests.Group("KYWD", new() { Kywd(0xFD000800, "j107MediumKw") }));
        WritePlugin(ctx, data, "j107_small.esm", Tes4(0x101), ProfileOrderTests.Group("KYWD", new() { Kywd(0xFE000800, "j107SmallKw") }));
        var kywd = new List<byte[]>
        {
            Kywd(0x00000800, "j107Override"), Kywd(0xFE001801, "j107OwnSmall"), Kywd(0x01000802, "j107OwnFull"),
            Kywd(0x02000803, "j107PastFull"), Kywd(0xFE002804, "j107PastSmall"), Kywd(0xFD010805, "j107OwnMedium"),
        };
        uint[] links = { 0x00000800, 0xFD000800, 0xFE000800, 0xFE001801, 0x01000802, 0x02000803, 0xFD020001, 0, 0xFFFFFFFF };
        var flst = ProfileOrderTests.Record("FLST", 0xFE001806, true, new[] { Edid("j107List") }.Concat(links.Select(l => U32("LNAM", l))));
        WritePlugin(ctx, data, Test, Tes4(0x101, "j107_full.esm", "j107_medium.esm", "j107_small.esm"), ProfileOrderTests.Group("KYWD", kywd), ProfileOrderTests.Group("FLST", new() { flst }));

        using var session = new ReportSession(data, Path.Combine(ctx.Work, "scratch-formids"), ctx.Schema, "sfsreport-tests");
        var report = new FormIdsReport(session);
        var a = report.Census(new SubrecordsOptions { Signatures = "", In = Test });
        ctx.WriteReport("profile-formids-made.txt", a.ToText(new ReportTextOptions { Detail = true }));
        string Kinds(long[] c) => string.Join(", ", Enumerable.Range(0, StoredKinds.Count).Where(k => c[k] > 0).Select(k => $"{(StoredKind)k} {c[k]}"));
        var p = a.Plugins.Single();
        Check(p.Full == 1 && p.Medium == 1 && p.Small == 1 && p.Size == SFSearch.Index.MasterStyle.Small && a.Totals.Records == 7 && a.Unreadable == 0, $"made: {p.Full}/{p.Medium}/{p.Small} masters, {p.Size}, {a.Totals.Records} records, {a.Unreadable} unreadable");
        // FD010805 in a Small plugin with one Medium master: Mutagen classifies the plugin's own number in a numbering it has no
        // place in as past the list (Classify, C203; SFSearch's own rule called it its own number until fixes.20, C58 W8)
        Check(Kinds(a.Totals.Headers) == "MasterFull 1, OwnFull 1, OwnSmall 2, PastFull 1, PastSmall 1, PastMediumOtherSize 1", "made, headers: " + Kinds(a.Totals.Headers));
        Check(Kinds(a.Totals.Links) == "Null 1, AllOnes 1, MasterFull 1, MasterMedium 1, MasterSmall 1, OwnFull 1, OwnSmall 1, PastFull 1, PastMediumOtherSize 1", "made, links: " + Kinds(a.Totals.Links));
        Check(Kinds(a.ByType["FLST"].Links) == Kinds(a.Totals.Links) && a.ByType["KYWD"].Links.Sum() == 0, "made, by type");
        string Place(UnusualPlace u) => $"{u.RecordSig} {u.Subrecord} {u.Kind} {u.FormIds}/{u.Records} {string.Join("/", u.Prefixes.Select(StoredKinds.Pattern))} @{u.MinOffset} {string.Join("; ", u.SampleText)}";
        var places = a.Unusual.Select(Place).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var want = new[]
        {
            $"FLST LNAM PastFull 1/1 02xxxxxx @0 j107List (000806:{Test}) 02000803",
            $"FLST LNAM PastMediumOtherSize 1/1 FD02xxxx @0 j107List (000806:{Test}) FD020001",
            $"KYWD header PastFull 1/1 02xxxxxx @-1 j107PastFull (000803:{Test}) 02000803",
            $"KYWD header PastMediumOtherSize 1/1 FD01xxxx @-1 j107OwnMedium (000805:{Test}) FD010805",
            $"KYWD header PastSmall 1/1 FE002xxx @-1 j107PastSmall (000804:{Test}) FE002804",
        };
        Check(places.SequenceEqual(want), $"made, the unusual places:\n{string.Join("\n", places)}\nwritten:\n{string.Join("\n", want)}");
        string text = a.ToText();
        Check(text.Contains($"| {Test} | Small, 3 masters (1 Full, 1 Medium, 1 Small) | 7 | 1 01xxxxxx, 2 FE001xxx |", StringComparison.Ordinal), "made, the plugin's row:\n" + text);
        // --type: only the form list
        var b = report.Census(new SubrecordsOptions { Signatures = "", In = Test, Type = "FormList" });
        Check(b.Totals.Records == 1 && b.Unusual.Count == 2 && b.ByType.Keys.Single() == "FLST", $"--type FormList: {b.Totals.Records} records, {b.Unusual.Count} places");
        return "made plugin (3 masters, one per numbering): 7 headers and 9 links in their 15 forms, the 5 unusual places with their records; --type";
    }

    /// <summary>A Small and a Medium plugin: their own forms as check2 counts them (its links from Mutagen's
    /// GetStoredFormIds, its headers from the file's bytes).</summary>
    /// <remarks>check2 counts a navmesh info map's links apart (NAVI is left out of its own links). Its links come
    /// through Mutagen, condition parameters included: sfta04.esm stores 125 own links in condition parameters (INFO 61,
    /// SCEN 28, QUST 9, PACK 9, LVLI 7, PERK 4, COBJ 3, CPTH 2, PCBN 2), which the census reads from the bytes since GAPS
    /// S156 (fix-j111-condition-links).</remarks>
    static string Real(Ctx ctx)
    {
        var report = new FormIdsReport(ctx.Session);
        var check = new CheckReport(ctx.Session);
        var parts = new List<string>();
        foreach (var (plugin, own) in new[] { ("darkstarpatch_bublumi.esm", StoredKind.OwnSmall), ("sfta04.esm", StoredKind.OwnMedium) })
        {
            var a = report.Census(new SubrecordsOptions { Signatures = "", In = plugin });
            ctx.WriteReport($"profile-formids-{plugin}.txt", a.ToText(new ReportTextOptions { Detail = true }));
            var f = check.Check(plugin, new CheckOptions { World = true }).OwnForms!;
            var t = a.Totals;
            long hFull = t.Headers[(int)StoredKind.OwnFull] + t.Headers[(int)StoredKind.PastFull], lFull = t.Links[(int)StoredKind.OwnFull] + t.Links[(int)StoredKind.PastFull];
            int form = own == StoredKind.OwnSmall ? 1 : 2;
            long navi = a.ByType.GetValueOrDefault("NAVI")?.Links[(int)own] ?? 0;
            long links = t.Links[(int)own] - navi;
            Check(hFull == f.Headers[0] && t.Headers[(int)own] == f.Headers[form] && lFull == f.Links[0] && links == f.Links[form] && a.Totals.Unusual == 0,
                $"{plugin}: census headers {hFull} full / {t.Headers[(int)own]} {own}, links {lFull} / {t.Links[(int)own]} ({navi} NAVI), {a.Totals.Unusual} unusual; check2 headers {string.Join("/", f.Headers)}, links {string.Join("/", f.Links)}");
            parts.Add($"{plugin}: own headers {N(hFull)} full index + {N(t.Headers[(int)own])} {(form == 1 ? "FE" : "FD")}, own links {N(lFull)} + {N(t.Links[(int)own])} ({N(navi)} in NAVI, which check2 counts apart), as check2 counts them; none unusual");
        }
        return string.Join("; ", parts);
    }

    /// <summary>The links classified are profile --subrecords "*"'s, field by field held against Mutagen by link-census:
    /// per record type, the census' links that aren't null or all ones equal the link fields' counts.</summary>
    static string SameLinks(Ctx ctx)
    {
        const string plugin = "sfta01.esm";
        var a = new FormIdsReport(ctx.Session).Census(new SubrecordsOptions { Signatures = "", In = plugin });
        var s = new TypeStatsReport(ctx.Session).Stats(new SubrecordsOptions { Signatures = "*", In = plugin });
        long all = 0;
        foreach (var t in s.Types)
        {
            long stats = t.Links.Sum(l => l.Links);
            var c = a.ByType.GetValueOrDefault(t.Sig);
            long census = c == null ? 0 : c.LinksNotNull - c.Links[(int)StoredKind.AllOnes];
            Check(stats == census && (c?.Records ?? 0) == t.Records, $"{plugin} {t.Sig}: --subrecords * counts {stats} links in {t.Records} records, --formids {census} in {c?.Records}");
            all += census;
        }
        return $"{plugin}: {N(all)} links in {s.Types.Count} record types, the same as profile --subrecords *";
    }

    /// <summary>
    /// Over Data: the FormIDs stored past the master list, plugin by plugin as C64 counted them with Mutagen's
    /// GetStoredFormIds (all 370 plugins; its own count of the FD01xxxx values in a Small plugin's reflection data, which
    /// this census names as another size's form, included), MissionsGalore.esm's 116 in its quests' condition parameters
    /// too (GAPS S156, fix-j111-condition-links). One differs: CrewSkillOverhaul.esm stores one more, its perk
    /// Crew_WeightLifting (21B8D9:Starfield.esm)'s fourth ability 02000800, which GetStoredFormIds gave as 01000800 until
    /// fixes.20 (GAPS S157); C69 fixed the overlay, and its rerun census counts 4,113, this census's number. Mutagen's
    /// GetStoredFormIds of that perk, read as SFSearch reads it, now gives 02000800 as stored. C64's text says 14 plugins;
    /// its table names 13.
    /// </summary>
    public static string Data(Ctx ctx)
    {
        var a = new FormIdsReport(ctx.Session).Census(new SubrecordsOptions { Signatures = "", In = "@all" });
        ctx.WriteReport("profile-formids-all.txt", a.ToText(new ReportTextOptions { Detail = true }));
        static bool Past(StoredKind k) => StoredKinds.IsUnusual(k);
        var byPlugin = a.Plugins.Select(p => (p.Plugin.Name, Past: Enumerable.Range(0, StoredKinds.Count).Where(k => Past((StoredKind)k)).Sum(k => p.Counts.Headers[k] + p.Counts.Links[k])))
            .Where(x => x.Past > 0).ToDictionary(x => x.Name, x => x.Past, StringComparer.OrdinalIgnoreCase);
        // C64's table (C:/mtg/cov/agent-reports/c64.md)
        var c64 = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["FalloutObjectModFeatureRestored.esm"] = 2747, ["MissionsGalore.esm"] = 1281, ["ShatteredSpace.esm"] = 41, ["useful mess halls - expanded.esm"] = 24,
            ["SFBGS050.esm"] = 4, ["SFBGS00D.esm"] = 4, ["kinggathcreations_spaceship.esm"] = 2, ["dstgo_sfta01.esm"] = 2, ["dstgo_sfta04.esm"] = 2, ["dstgo_sfta06.esm"] = 2,
            ["IncreasedSpaceLootDistance.esm"] = 1, ["Inquisitor_Distance_Tweaks5000.esm"] = 1, ["CrewSkillOverhaul.esm"] = 1 + 1,
        };
        string Diff() => string.Join("; ", byPlugin.Keys.Union(c64.Keys, StringComparer.OrdinalIgnoreCase).Where(k => byPlugin.GetValueOrDefault(k) != c64.GetValueOrDefault(k)).Select(k => $"{k}: {byPlugin.GetValueOrDefault(k)} (C64 {c64.GetValueOrDefault(k)})"));
        long total = byPlugin.Values.Sum();
        Check(Diff().Length == 0 && total == 4112 + 1 && byPlugin.Count == 13, $"@all: {total} FormIDs past the master list in {byPlugin.Count} plugins (C64: 4,112, plus 1 perk ability, in 13); differences: {Diff()}");
        // S157 (C69): Mutagen's own report of the perk's stored FormIDs agrees with the bytes
        var engine = ctx.Session.Engine;
        engine.Sessions.Use("CrewSkillOverhaul.esm");
        var perk = engine.Load(engine.Find("CrewSkillOverhaul.esm", "21B8D9:Starfield.esm"));
        var places = SFSearch.Records.FormIdForms.OwnLinks(perk, engine.Raw(perk.Ref), out int notFound);
        Check(notFound == 0 && places.Any(x => x.Raw == 0x0200_0800 && x.Subrecord == "DATA"), $"CrewSkillOverhaul.esm's perk 21B8D9: Mutagen's GetStoredFormIds gives {string.Join(", ", places.Select(x => $"{x.Subrecord} {x.Raw:X8}"))} ({notFound} not found); 02000800 in a DATA was expected");
        return $"@all: {N(a.Totals.Records)} records of {a.Plugins.Count} plugins; {N(total)} FormIDs past the master list in {byPlugin.Count} plugins, C64's 4,112 plugin by plugin (MissionsGalore.esm's 116 condition parameters included) plus one perk ability (S157), which Mutagen's GetStoredFormIds now gives as stored, 02000800 ({string.Join(", ", byPlugin.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {N(x.Value)}"))})";
    }
}
