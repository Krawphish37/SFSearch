// The test of GAPS S162: a flags enum's flags are listed by SFSearch's own bounded bit loop (SFSearch.Records.FlagBits),
// never by Noggog's EnumerateContainedFlags, which gives a spurious 0 (and the bit's number even when undefined bits
// aren't asked for) for an int enum's bit 31, and never ends when the enum names bit 31 (Mutagen fixes.20's
// NavigationMesh and BiomeMarker MajorFlag). Synthetic enums for bit 31 and bit 30, then the real records of
// Starfield.esm and the official plugins whose header sets bit 31, through the typed views' MajorFlags column.
using SFSearch.Records;
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class FlagBitsTests
{
    [Flags] enum NoTopBit : int { A = 1, B = 2, C = 0x4000_0000 }
    [Flags] enum TopBit : int { A = 1, Top = unchecked((int)0x8000_0000) }
    [Flags] enum Wide : ulong { A = 1, Top = 0x8000_0000_0000_0000 }
    [Flags] enum Small : byte { A = 1, Top = 0x80 }
    [Flags] enum UNoTopBit : uint { A = 1, B = 2, C = 0x4000_0000 }
    [Flags] enum UTopBit : uint { A = 1, Top = 0x8000_0000 }

    static string L(IEnumerable<string> names) => string.Join(",", names);

    public static string Run(Ctx c)
    {
        // Bit 31 on an int enum that doesn't name it (fixes.19's NavigationMesh.MajorFlag): no 0, the bit only when asked for
        var v = (NoTopBit)unchecked((int)0x8000_0003);
        Check(L(FlagBits.Names(v, includeUndefined: false)) == "A,B", $"bit 31, undefined bits left out: {L(FlagBits.Names(v, false))}");
        Check(L(FlagBits.Names(v, includeUndefined: true)) == "A,B,0x80000000", $"bit 31, undefined bits listed: {L(FlagBits.Names(v, true))}");
        Check((string)DirectColumn.EnumText(v) == "[\"A\",\"B\",\"0x80000000\"]", $"bit 31 in the JSON form: {DirectColumn.EnumText(v)}");
        Check(L(DirectColumn.FlagNames(v)) == "A,B,0x80000000", $"bit 31 as a flags list: {L(DirectColumn.FlagNames(v))}");
        // Bit 30, which the enum names
        var w = (NoTopBit)0x4000_0003;
        Check(L(FlagBits.Names(w, false)) == "A,B,C" && (string)DirectColumn.EnumText(w) == "[\"A\",\"B\",\"C\"]", $"bit 30: {DirectColumn.EnumText(w)}");
        // An enum that names its top bit (fixes.20's NavmeshGenCell, MustBeUnique), and other widths: each ends, each bit once
        Check(L(FlagBits.Names(TopBit.A | TopBit.Top, false)) == "A,Top", $"a named bit 31: {L(FlagBits.Names(TopBit.A | TopBit.Top, false))}");
        Check(L(FlagBits.Names(Wide.A | Wide.Top, false)) == "A,Top" && L(FlagBits.Names(Small.A | Small.Top, false)) == "A,Top", "a ulong's bit 63 or a byte's bit 7");
        Check(!FlagBits.Names((NoTopBit)0, true).Any(), "no flags give no names");
        // The same on uint-based enums (fixes.21's NavigationMesh and BiomeMarker MajorFlag): 0x80000003 named and unnamed
        var u = (UNoTopBit)0x8000_0003u;
        Check(L(FlagBits.Names(u, false)) == "A,B" && L(FlagBits.Names(u, true)) == "A,B,0x80000000" && (string)DirectColumn.EnumText(u) == "[\"A\",\"B\",\"0x80000000\"]",
            $"uint bit 31 unnamed: {DirectColumn.EnumText(u)}");
        Check(L(FlagBits.Names((UTopBit)0x8000_0001u, false)) == "A,Top" && FlagBits.Raw((UTopBit)0x8000_0001u) == 0x8000_0001u && FlagBits.Raw((NoTopBit)unchecked((int)0x8000_0001)) == 0x8000_0001u,
            "uint bit 31 named, and Raw of an int and a uint");

        // The real records whose header sets bit 31, in the official plugins: Mutagen's object of each, its MajorFlags as
        // SFSearch's direct columns give it (DirectColumn.EnumText): no 0, the bit by its name or, where Mutagen names none,
        // in hex as the JSON writes an undefined flag; and the serializer's own JSON of each is the same list (1.38.6-fixes.6,
        // C72: no spurious "0x0" any more, GAPS S162's serializer half).
        var engine = c.NewEngine("flag-bits");
        var official = c.Index.Plugins.Select(p => p.Name).Where(n => n.Equals("Starfield.esm", StringComparison.OrdinalIgnoreCase) || n.StartsWith("SFBGS", StringComparison.OrdinalIgnoreCase) || n.StartsWith("sfta", StringComparison.OrdinalIgnoreCase)).ToArray();
        var top = Must(engine.Run(new QueryRequest { Sql = "SELECT Sig, FormKey, Plugin FROM Forms WHERE Flags & 0x80000000 ORDER BY Sig, Plugin, FormKey", Plugins = official, PageRows = 10_000, AllowHeavy = true }), "bit 31 in Forms");
        var bySig = top.Rows.GroupBy(r => (string)r[0]!).ToDictionary(g => g.Key, g => g.Count());
        Check(top.Rows.Count(r => (string)r[0]! == "NAVM" && ((string)r[2]!).Equals("Starfield.esm", StringComparison.OrdinalIgnoreCase)) == 13, $"Starfield.esm's navmeshes with bit 31: {bySig.GetValueOrDefault("NAVM")}; 13 were expected");
        int read = 0, jsonZero = 0;
        string? example = null;
        foreach (var r in top.Rows)
        {
            string plugin = (string)r[2]!;
            c.Records.Sessions.Use(plugin);
            var rec = c.Records.Load(c.Records.Find(plugin, (string)r[1]!));
            // Each view of the header flags the record has: its type's MajorFlags (NAVM) and the shared StarfieldMajorRecordFlags (both)
            foreach (string view in new[] { "MajorFlags", "StarfieldMajorRecordFlags" })
            {
                var prop = rec.Record.GetType().GetProperty(view);
                if (prop?.GetValue(rec.Record) is not Enum flags) continue;
                string direct = (string)DirectColumn.EnumText(flags);
                Check(!direct.Contains("\"0x0\"", StringComparison.Ordinal) && !direct.Contains("\"0\"", StringComparison.Ordinal) && (direct.Contains("0x80000000", StringComparison.Ordinal) || direct.Contains("NavmeshGenCell", StringComparison.Ordinal) || direct.Contains("MustBeUnique", StringComparison.Ordinal)),
                    $"{r[0]} {r[1]} of {plugin}: {view} {direct}");
                string? j = c.Records.Json(rec)[view]?.ToJsonString();
                Check(j == direct, $"{r[0]} {r[1]} of {plugin}: {view} in the JSON {j}, the direct columns {direct}");
                if (j != null && j.Contains("\"0x0\"", StringComparison.Ordinal)) jsonZero++;
                if (r[0] as string == "REFR" || example == null) example = $"{rec.Label} {view}: {direct}, the serializer's JSON {c.Records.Json(rec)[view]?.ToJsonString()}";
                read++;
            }
        }
        return $"bit 31 on an int enum that doesn't name it: [A,B], and with the undefined bits [A,B,0x80000000], no 0; bit 30 [A,B,C]; a named top bit of an int, a ulong and a byte each once. Records with header bit 31 in the official plugins: {string.Join(", ", bySig.Select(x => $"{x.Key} {x.Value}"))}; {read} MajorFlags and StarfieldMajorRecordFlags values as the direct columns give them, none with a 0 ({example}); the serializer's JSON gives the same lists ({jsonZero} with a 0)";
    }
}
