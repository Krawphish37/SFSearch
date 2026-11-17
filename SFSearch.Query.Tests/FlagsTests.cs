// The test of fix J86 (GAPS S108): the Forms view's Flags and FlagNames. Flags is each record header's flags as the
// file stores them (bytes 8-11), from the header index; FlagNames names them by Mutagen's enum for the record's type.
// Both are held against a walk of the plugin files' bytes done here, not against SFSearch's header index.
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Starfield;
using SFSearch.Index;
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class FlagsTests
{
    /// <summary>The game's own plugins and Bethesda's Creations: the set @official names (SFSearch.Reports.PluginSets).</summary>
    static readonly Regex Official = new(@"^(sfbgs[^\\/]*|sfta[^\\/]*|blueprintships-[^\\/]*)\.esm$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Every record of Starfield.esm and the official plugins: the count of each (signature, flags) in Forms equals the
    /// count a walk of the file's record headers finds, plugin by plugin; so does each (signature, bit) census. The C54
    /// examples on Starfield.esm by SQL: WEAP 0x40000000 139, KYWD 0x8000 192, OMOD 0x80 182 and 0x10 78, TRNS 0x10000 51.
    /// FlagNames, for every (signature, flags) found, equals the names this test takes from Mutagen's MajorFlag enum of
    /// each of the signature's types (Compressed and Deleted for the two bits Mutagen reads on every record), and a bit
    /// with no name is its number; the four C54 types are checked by their own enum types too.
    /// </summary>
    public static string Run(Ctx c)
    {
        var plugins = c.Index.Plugins.Select(p => p.Name).Where(n => LoadOrder.Implicit.Contains(n, StringComparer.OrdinalIgnoreCase) || Official.IsMatch(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        Check(plugins.Contains("Starfield.esm", StringComparer.OrdinalIgnoreCase) && plugins.Length > 20, $"the official plugins in Data: {plugins.Length}");

        // The walk of the bytes: (plugin, signature, flags) -> records
        var walk = new Dictionary<(string, string, uint), long>();
        long walked = 0;
        foreach (string name in plugins) walked += Walk(c.Index.Plugin(name)!.Path, name, walk);

        // Forms: the same count by SQL, all plugins in one statement
        var engine = c.NewEngine("sql-flags");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var a = Must(engine.Run(new QueryRequest { Sql = "SELECT Plugin, Sig, Flags, count(*) FROM Forms GROUP BY 1, 2, 3", Plugins = plugins, PageRows = 10_000_000, AllowHeavy = true }), "Forms flags");
        double seconds = clock.Elapsed.TotalSeconds;
        var forms = a.Rows.ToDictionary(r => (((string)r[0]!).ToLowerInvariant(), (string)r[1]!, (uint)(long)r[2]!), r => (long)r[3]!);
        var bad = new List<string>();
        foreach (var k in walk.Keys.Union(forms.Keys).Distinct())
        {
            long w = walk.GetValueOrDefault(k), f = forms.GetValueOrDefault(k);
            if (w != f && bad.Count < 10) bad.Add($"{k.Item1} {k.Item2} 0x{k.Item3:X8}: Forms {f}, the bytes {w}");
        }
        Check(bad.Count == 0, $"Forms' (plugin, signature, flags) counts differ from the bytes': {string.Join("; ", bad)}");
        Check(forms.Values.Sum() == walked, $"Forms has {forms.Values.Sum():N0} records, the bytes {walked:N0}");

        // The census by bit, over all the plugins: (signature, bit) -> records
        var census = new Dictionary<(string, int), long>();
        foreach (var ((_, sig, flags), n) in walk)
            for (int b = 0; b < 32; b++) if ((flags & (1u << b)) != 0) census[(sig, b)] = census.GetValueOrDefault((sig, b)) + n;
        var byBit = new Dictionary<(string, int), long>();
        foreach (var ((_, sig, flags), n) in forms)
            for (int b = 0; b < 32; b++) if ((flags & (1u << b)) != 0) byBit[(sig, b)] = byBit.GetValueOrDefault((sig, b)) + n;
        Check(census.Count == byBit.Count && census.All(x => byBit.GetValueOrDefault(x.Key) == x.Value), "the census by bit differs");
        c.Save("forms-flags-census.tsv", census.OrderBy(x => x.Key.Item1, StringComparer.Ordinal).ThenBy(x => x.Key.Item2).Select(x => $"{x.Key.Item1}\t0x{1u << x.Key.Item2:X8}\t{x.Value}"));

        // The C54 examples, counted by SQL as a user would
        var sf = Must(engine.Run(new QueryRequest { Sql = "SELECT Sig, count(*) FROM Forms WHERE (Sig = 'WEAP' AND Flags & 0x40000000) OR (Sig = 'KYWD' AND Flags & 0x8000) OR (Sig = 'OMOD' AND Flags & 0x80) OR (Sig = 'TRNS' AND Flags & 0x10000) GROUP BY Sig ORDER BY Sig", Plugins = new[] { "Starfield.esm" }, PageRows = 100 }), "C54's counts");
        Check(string.Join(", ", sf.Rows.Select(r => $"{r[0]} {r[1]}")) == "KYWD 192, OMOD 182, TRNS 51, WEAP 139", $"C54's counts on Starfield.esm: {string.Join(", ", sf.Rows.Select(r => $"{r[0]} {r[1]}"))}");
        var legendary = Must(engine.Run(new QueryRequest { Sql = "SELECT count(*) FROM Forms WHERE Sig = 'OMOD' AND Flags & 0x10", Plugins = new[] { "Starfield.esm" } }), "OMOD 0x10");
        Check((long)legendary.Rows[0][0]! == 78, $"OMOD 0x10 on Starfield.esm: {legendary.Rows[0][0]}");

        // FlagNames for every (signature, flags) of the official plugins, against the names taken from Mutagen here
        var names = Must(engine.Run(new QueryRequest { Sql = "SELECT DISTINCT Sig, Flags, FlagNames FROM Forms", Plugins = plugins, PageRows = 1_000_000, AllowHeavy = true }), "FlagNames");
        int namedChecked = 0;
        foreach (var r in names.Rows)
        {
            string sig = (string)r[0]!; uint flags = (uint)(long)r[1]!; string got = (string)r[2]!;
            string want = Expected(c, sig, flags);
            if (got != want && bad.Count < 10) bad.Add($"{sig} 0x{flags:X8}: FlagNames \"{got}\", Mutagen's enum \"{want}\"");
            namedChecked++;
        }
        Check(bad.Count == 0, $"FlagNames differ from Mutagen's names: {string.Join("; ", bad)}");

        // The four types C54 fixed, each by its own enum type: the name when Mutagen has the bit, else the number
        string On(Type e, uint bit) => Enum.IsDefined(e, Enum.ToObject(e, (int)bit)) ? Enum.GetName(e, Enum.ToObject(e, (int)bit))! : $"0x{bit:X}";
        var examples = new (string Key, string Want)[]
        {
            ("3FA258:Starfield.esm", $"{Weapon.MajorFlag.NonPlayable}, {On(typeof(Weapon.MajorFlag), 0x4000_0000)}"),   // CreatureRangedAttack_Mouth1_Oxygen, 0x40000004
            ("0019F4:Starfield.esm", On(typeof(Keyword.MajorFlag), 0x8000)),                                          // Weapon_Reward_MQ105_Equalizer_Restricted
            ("06288A:Starfield.esm", On(typeof(AObjectModification.MajorFlag), 0x80)),                                  // mod_Grendel_Quality_TiersAny
            ("002983:Starfield.esm", On(typeof(AObjectModification.MajorFlag), 0x10)),                                  // mod_Legendary_Armor_03_Incendiary
            ("00CF83:Starfield.esm", On(typeof(Transform.MajorFlag), 0x1_0000)),                                        // ...BootlegMoonshineThermometer
        };
        var said = new List<string>();
        foreach (var (key, want) in examples)
        {
            var e = Must(engine.Run(new QueryRequest { Sql = $"SELECT EditorID, Flags, FlagNames FROM Forms WHERE FormKey = '{key}'", Plugins = new[] { "Starfield.esm" } }), key);
            Check(e.Rows.Count == 1 && (string)e.Rows[0][2]! == want, $"{key}: FlagNames \"{(e.Rows.Count == 1 ? e.Rows[0][2] : "no row")}\", want \"{want}\"");
            said.Add($"{e.Rows[0][0]} ({key}) 0x{(long)e.Rows[0][1]!:X}: {e.Rows[0][2]}");
        }
        // J90 (GAPS S112): the containers of Starfield.esm with header bit 28 or 29 set. Until fixes.20 Mutagen named 0x10000000
        // twice (NavMeshOnlyCut and NavMeshIgnoreErosion); C56 moved NavMeshIgnoreErosion to 0x20000000 (xEdit's bit 29)
        var cont = Must(engine.Run(new QueryRequest { Sql = "SELECT EditorID, FormKey, Flags, FlagNames FROM Forms WHERE Sig = 'CONT' AND Flags & 0x30000000 ORDER BY FormKey", Plugins = new[] { "Starfield.esm" }, PageRows = 100 }), "CONT bits 28 and 29");
        foreach (var r in cont.Rows)
        {
            uint f = (uint)(long)r[2]!; string n = (string)r[3]!;
            Check(!n.Contains('/') && ((f & 0x1000_0000) == 0 || n.Contains("NavMeshOnlyCut")) && ((f & 0x2000_0000) == 0 || n.Contains("NavMeshIgnoreErosion"))
                && ((f & 0x2000_0000) != 0 || !n.Contains("NavMeshIgnoreErosion")), $"CONT {r[1]} 0x{f:X8}: FlagNames \"{n}\"");
        }
        Check(cont.Rows.Count == 2, $"Starfield.esm has {cont.Rows.Count} containers with bit 28 or 29 set; J90 found 2");
        said.Add("CONT bits 28/29 (J90): " + string.Join("; ", cont.Rows.Select(r => $"{r[0]} ({r[1]}) 0x{(long)r[2]!:X}: {r[3]}")));

        // describe Forms names both columns
        var doc = ViewDocs.Find(engine.Views, "Forms")!;
        Check(doc.Columns.Select(x => x.Name).SequenceEqual(engine.ColumnsOf("Forms", ScopeKind.Plugins)!), $"describe Forms gives {string.Join(", ", doc.Columns.Select(x => x.Name))}");

        int sigs = census.Keys.Select(k => k.Item1).Distinct().Count();
        return $"{plugins.Length} plugins, {walked:N0} records: Forms' {forms.Count:N0} (plugin, signature, flags) counts equal the bytes', and the census of {census.Count:N0} (signature, bit) pairs over {sigs} signatures; "
            + $"Forms by SQL in {seconds:F1} s; C54's counts KYWD 192, OMOD 182 (0x10: 78), TRNS 51, WEAP 139; FlagNames of {namedChecked:N0} (signature, flags) equal Mutagen's enums; {string.Join("; ", said)}";
    }

    /// <summary>The names the test takes from Mutagen: the signature's header-flag enum as Mutagen's
    /// StarfieldMajorFlagRegistry lists it by record type (C61), then Deleted 0x20 and Compressed 0x40000; a bit with none
    /// as its number; named bits first. No enum gives a bit two names since fixes.20 (C56); were one to, both are said and
    /// the test fails.</summary>
    static string Expected(Ctx c, string sig, uint flags)
    {
        var byBit = new Dictionary<uint, List<string>>();
        if (StarfieldMajorFlagRegistry.EnumTypesByRecordType.TryGetValue(new Mutagen.Bethesda.Plugins.RecordType(sig), out var e))
            foreach (string n in Enum.GetNames(e))
            {
                uint bit = unchecked((uint)Convert.ToInt64(Enum.Parse(e, n)));
                if (bit == 0 || (bit & (bit - 1)) != 0) continue;
                var l = byBit.TryGetValue(bit, out var x) ? x : byBit[bit] = new();
                if (!l.Contains(n)) l.Add(n);
            }
        byBit.TryAdd(0x20, new() { "Deleted" });
        byBit.TryAdd(0x40000, new() { "Compressed" });
        var named = new List<string>(); var numbers = new List<string>();
        for (int b = 0; b < 32; b++)
        {
            uint bit = 1u << b;
            if ((flags & bit) == 0) continue;
            if (byBit.TryGetValue(bit, out var l)) named.Add(string.Join("/", l)); else numbers.Add($"0x{bit:X}");
        }
        return string.Join(", ", named.Concat(numbers));
    }

    /// <summary>Every record header of a plugin, walked from its bytes: the TES4 header skipped, a group's 24-byte header
    /// stepped into, a record's header read and its data stepped over. Adds (plugin, signature, flags) counts.</summary>
    static long Walk(string path, string plugin, Dictionary<(string, string, uint), long> into)
    {
        using var map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var v = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        long length = new FileInfo(path).Length, pos = 24 + v.ReadUInt32(4), records = 0;
        var sigs = new Dictionary<uint, string>();
        while (pos + 24 <= length)
        {
            uint s = v.ReadUInt32(pos);
            if (s == 0x50555247) { pos += 24; continue; }   // GRUP
            uint size = v.ReadUInt32(pos + 4), flags = v.ReadUInt32(pos + 8);
            if (!sigs.TryGetValue(s, out var sig)) sigs[s] = sig = Encoding.Latin1.GetString(BitConverter.GetBytes(s));
            var k = (plugin.ToLowerInvariant(), sig, flags);
            into[k] = into.GetValueOrDefault(k) + 1;
            records++;
            pos += 24 + size;
        }
        Check(pos == length, $"{plugin}: the walk ended at {pos}, the file is {length} bytes");
        return records;
    }
}
