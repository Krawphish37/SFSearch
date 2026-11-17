// profile --subrecords X|* --lengths (GAPS S128): subrecord lengths by record type and signature, and the empty ones.
// Held against a plugin written here byte by byte (an end marker in a component, a data subrecord sometimes empty, one
// always empty, one of fixed length, one varying, a compressed record), and against C65's census of zero-length
// subrecords (the Mutagen session, 2026-11-15, Python; data/c65-empty.tsv): over Starfield.esm, the 32 official plugins
// and kgcdoom.esm, 198 kinds with an empty subrecord, 5,085,271 in all, each kind's records with an empty one, empty
// subrecords, records that hold the subrecord and records with an empty one per plugin the same.
using System.Text;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class ProfileLengthsTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    public static string Lengths(Ctx ctx)
    {
        var parts = new List<string> { Made(ctx), C65(ctx) };
        if (ctx.CanRunToday)
        {
            string text = ctx.Today("profile__lengths", "profile", "--subrecords", "*", "--lengths", "--type", "LCTN", "--in", "Starfield.esm")!;
            Check(text.Contains("| LCTN | LCUN | empty, or ", StringComparison.Ordinal), "the verb's text: " + text[..Math.Min(2000, text.Length)]);
            string both = ctx.Today("profile__lengths__values", "profile", "--subrecords", "FNAM", "--lengths", "--values")!;
            Check(both.Contains("--lengths and --values were both given", StringComparison.Ordinal), "--lengths with --values: " + both);
            parts.Add("the verb's text; --lengths with --values refused");
        }
        return string.Join("; ", parts);
    }

    static (string, byte[]) Z(string sig, string text) => (sig, Encoding.ASCII.GetBytes(text + "\0"));
    static (string, byte[]) E(string sig) => (sig, Array.Empty<byte>());

    /// <summary>
    /// WEAP 000800: EDID "j110W", a component (BFCB "BGSKeywordForm_Component", its BFCE empty), FNAM empty, DATA 4 bytes.
    /// MISC 000801 (compressed): EDID "j110Misc", FNAM "abc", FNAM empty twice, DATA 4 bytes, STOP empty. MISC 000802:
    /// EDID "j110M2", DATA 4 bytes.
    /// </summary>
    static string Made(Ctx ctx)
    {
        string data = ctx.Folder("lengths-data");
        const string plugin = "j110_lengths.esm";
        var four = ("DATA", new byte[] { 1, 2, 3, 4 });
        var weap = ProfileOrderTests.Record("WEAP", 0x800, false, new[] { Z("EDID", "j110W"), Z("BFCB", "BGSKeywordForm_Component"), E("BFCE"), E("FNAM"), four });
        var misc = ProfileOrderTests.Record("MISC", 0x801, true, new[] { Z("EDID", "j110Misc"), Z("FNAM", "abc"), E("FNAM"), E("FNAM"), four, E("STOP") });
        var misc2 = ProfileOrderTests.Record("MISC", 0x802, false, new[] { Z("EDID", "j110M2"), four });
        ProfileFormIdsTests.WritePlugin(ctx, data, plugin, ProfileFormIdsTests.Tes4(0x1), ProfileOrderTests.Group("WEAP", new() { weap }), ProfileOrderTests.Group("MISC", new() { misc, misc2 }));
        using var session = new ReportSession(data, Path.Combine(ctx.Work, "scratch-lengths"), ctx.Schema, "sfsreport-tests");
        var report = new SubrecordLengthsReport(session);
        var a = report.Lengths(new SubrecordsOptions { Signatures = "*", In = plugin });
        ctx.WriteReport("profile-lengths-made.txt", a.ToText(new ReportTextOptions { Detail = true }));
        string Kind(SubrecordLengths k) => $"{k.RecordSig} {k.Subrecord} {k.Class} {k.Min}-{k.Max} {k.Uses}/{k.Records} empty {k.Empty}/{k.RecordsEmpty} [{string.Join(",", k.EmptyIn.Select(x => $"{x.Key}={x.Value}"))}] {string.Join("; ", k.EmptySamples)} | {string.Join("; ", k.DataSamples)}";
        var got = a.Kinds.Select(Kind).ToList();
        var want = new[]
        {
            $"MISC DATA Fixed 4-4 2/2 empty 0/0 []  | ",
            $"MISC EDID Varying 7-9 2/2 empty 0/0 []  | ",
            $"MISC FNAM SometimesEmpty 0-4 3/1 empty 2/1 [=2] j110Misc (000801:{plugin}) | j110Misc (000801:{plugin})",
            $"MISC STOP AlwaysEmpty 0-0 1/1 empty 1/1 [=1] j110Misc (000801:{plugin}) | ",
            $"WEAP BFCB Fixed 25-25 1/1 empty 0/0 []  | ",
            $"WEAP BFCE AlwaysEmpty 0-0 1/1 empty 1/1 [\"BGSKeywordForm_Component\"=1] j110W (000800:{plugin}) | ",
            $"WEAP DATA Fixed 4-4 1/1 empty 0/0 []  | ",
            $"WEAP EDID Fixed 6-6 1/1 empty 0/0 []  | ",
            $"WEAP FNAM AlwaysEmpty 0-0 1/1 empty 1/1 [=1] j110W (000800:{plugin}) | ",
        };
        Check(got.SequenceEqual(want), $"made:\n{string.Join("\n", got)}\nwritten:\n{string.Join("\n", want)}");
        Check(a.Inflated == 1 && a.Records == 3, $"made: {a.Records} records, {a.Inflated} inflated");
        var b = report.Lengths(new SubrecordsOptions { Signatures = "FNAM,XNAM", In = plugin, Type = "MISC" });
        Check(b.Kinds.Count == 1 && b.Kinds[0].Class == LengthClass.SometimesEmpty && b.Missing.SequenceEqual(new[] { "XNAM" }) && b.ToText().Contains("XNAM: no record in", StringComparison.Ordinal), "FNAM,XNAM --type MISC");
        return "made plugin: always empty (an end marker in a component, a marker), sometimes empty, fixed, varying, a compressed record; named signatures with --type";
    }

    /// <summary>C65's census: every kind with an empty subrecord over its 34 plugins, each number and plugin the same.</summary>
    static string C65(Ctx ctx)
    {
        var c65 = new Dictionary<(string, string), (long RecEmpty, long Empty, long Records, Dictionary<string, long> Plugins)>();
        using (var st = typeof(ProfileLengthsTests).Assembly.GetManifestResourceStream("c65-empty.tsv")!)
        using (var rd = new StreamReader(st))
        {
            rd.ReadLine();
            for (string? line; (line = rd.ReadLine()) != null;)
            {
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var c = line.Split('\t');
                var plugins = c[5].Split(", ").Select(x => { int sp = x.LastIndexOf(' '); return (Name: x[..sp], N: long.Parse(x[(sp + 1)..], System.Globalization.CultureInfo.InvariantCulture)); })
                    .ToDictionary(x => x.Name, x => x.N, StringComparer.OrdinalIgnoreCase);
                c65[(c[0], c[1])] = (long.Parse(c[2], System.Globalization.CultureInfo.InvariantCulture), long.Parse(c[3], System.Globalization.CultureInfo.InvariantCulture), long.Parse(c[4], System.Globalization.CultureInfo.InvariantCulture), plugins);
            }
        }
        Check(c65.Count == 198 && c65.Values.Sum(v => v.Empty) == 5085271, $"C65's file: {c65.Count} kinds, {c65.Values.Sum(v => v.Empty)} empty");
        var a = new SubrecordLengthsReport(ctx.Session).Lengths(new SubrecordsOptions { Signatures = "*", In = "@official,kgcdoom.esm" });
        ctx.WriteReport("profile-lengths-official.txt", a.ToText(new ReportTextOptions { Detail = true }));
        Check(a.PluginsRead == 34 && a.Unreadable == 0, $"{a.PluginsRead} plugins read (C65: 34), {a.Unreadable} unreadable");
        var empty = a.Kinds.Where(k => k.Empty > 0).ToDictionary(k => (k.RecordSig, k.Subrecord));
        var diff = new List<string>();
        foreach (var key in empty.Keys.Union(c65.Keys))
        {
            if (!empty.TryGetValue(key, out var k)) { diff.Add($"{key}: only C65"); continue; }
            if (!c65.TryGetValue(key, out var c)) { diff.Add($"{key}: only --lengths ({k.Empty})"); continue; }
            bool plugins = c.Plugins.Count == k.PluginsEmpty.Count && c.Plugins.All(x => k.PluginsEmpty.GetValueOrDefault(x.Key) == x.Value);
            if (k.RecordsEmpty != c.RecEmpty || k.Empty != c.Empty || k.Records != c.Records || !plugins)
                diff.Add($"{key}: --lengths {k.RecordsEmpty}/{k.Empty}/{k.Records}, C65 {c.RecEmpty}/{c.Empty}/{c.Records}{(plugins ? "" : ", plugins differ")}");
        }
        Check(diff.Count == 0, "--lengths against C65:\n" + string.Join("\n", diff.Take(40)));
        // C65's 16 data kinds that are sometimes empty, and the camera shot's XNAM (an end marker in a shot, data elsewhere)
        var c65Some = new[] { "DIAL TIFL", "GBFM PRPS", "LCTN LCUN", "LCTN LCUR", "OTFT INAM", "PNDT CNAM", "PNDT EOVR", "SCEN BTXT", "SCEN DNAM", "SCEN FNAM", "SCEN HTID", "SCEN PNAM", "SCEN VNAM", "WEAP BNAM", "WRLD WHGT", "WRLD XCLW", "SCEN XNAM" };
        // and three C65 classed as markers: a quest stage's QSRD, a scene action's ANAM and HNAM are empty where they end a block and hold data elsewhere
        var markersToo = new[] { "QUST QSRD", "SCEN ANAM", "SCEN HNAM" };
        var some = empty.Values.Where(k => k.Class == LengthClass.SometimesEmpty).Select(k => $"{k.RecordSig} {k.Subrecord}").OrderBy(x => x, StringComparer.Ordinal).ToList();
        Check(some.SequenceEqual(c65Some.Concat(markersToo).OrderBy(x => x, StringComparer.Ordinal)), $"sometimes empty: {string.Join(", ", some)}; C65's: {string.Join(", ", c65Some)}");
        Check(empty.Values.Where(k => k.Class == LengthClass.SometimesEmpty).All(k => k.EmptySamples.Count > 0 && k.DataSamples.Count > 0), "a sometimes-empty kind without samples");
        string extra = string.Join(", ", some.Except(c65Some));
        return $"@official,kgcdoom.esm: C65's 198 kinds with an empty subrecord ({N(empty.Values.Sum(k => k.Empty))} empty) number by number and plugin by plugin; {some.Count} sometimes empty, C65's 16 data kinds and SCEN XNAM{(extra.Length > 0 ? " and " + extra : "")}, each with records with an empty one and with data";
    }
}
