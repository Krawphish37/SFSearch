// The test of GAPS S168 (J120): each flags column of a typed or list view has its number column right after it
// (<Field>Raw), and on Starfield.esm each number column is held against the flags' names beside it and against
// Mutagen's enum value (cast to the enum's underlying type), and a bit test by number counts what the test by name counts.
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using SFSearch.Records;
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class FlagRawTests
{
    const string SF = "Starfield.esm";

    [Flags] enum IntFlags : int { A = 1, B = 2 }
    [Flags] enum WideFlags : ulong { A = 1, Top = 0x8000_0000_0000_0000 }

    /// <summary>The value as an unsigned number of its enum's underlying width, by the cast (not SFSearch's FlagBits).</summary>
    static long Cast(Enum e)
    {
        object u = Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType()), CultureInfo.InvariantCulture);
        return u switch
        {
            sbyte x => unchecked((byte)x), byte x => x, short x => unchecked((ushort)x), ushort x => x,
            int x => unchecked((uint)x), uint x => x, long x => x, ulong x => unchecked((long)x),
            _ => throw new TestFailed($"{e.GetType().Name} has a {u.GetType().Name} underneath"),
        };
    }

    /// <summary>The number of a flags column's JSON list of names, by the enum's own names (Enum.Parse) and an unnamed
    /// bit's hex text.</summary>
    static long FromNames(string json, Type t)
    {
        long n = 0;
        foreach (var item in JsonDocument.Parse(json).RootElement.EnumerateArray())
        {
            string name = item.GetString()!;
            n |= name.StartsWith("0x", StringComparison.Ordinal) ? unchecked((long)ulong.Parse(name[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)) : Cast((Enum)Enum.Parse(t, name));
        }
        return n;
    }

    /// <summary>A bit's name in a flags list: the member's name, or its hex text when no member has it.</summary>
    static string BitName(Type t, long bit)
    {
        var e = (Enum)Enum.ToObject(t, bit);
        return Enum.IsDefined(t, e) ? e.ToString() : "0x" + unchecked((ulong)bit).ToString("X", CultureInfo.InvariantCulture);
    }

    sealed record Target(ViewSpec View, ColumnSpec Flags, ColumnSpec Raw, Type Enum);

    public static string Run(Ctx c)
    {
        // The number of a list of names, and of an int enum's bit 31 and a ulong's bit 63
        Check(FlagsRaw.FromNames("[\"Left\",\"0x80000000\"]", "Furniture.EntryParameterType") is { } named && named == (Cast(Enum.Parse<Mutagen.Bethesda.Starfield.Furniture.EntryParameterType>("Left")) | 0x8000_0000L),
            $"sfs_flagbits of a member and an unnamed bit 31: {FlagsRaw.FromNames("[\"Left\",\"0x80000000\"]", "Furniture.EntryParameterType")}");
        Check(FlagsRaw.FromNames(null, "Furniture.EntryParameterType") == null && FlagsRaw.FromNames("[]", "Furniture.EntryParameterType") == 0, "sfs_flagbits of NULL and of no flags");
        Check(FlagsRaw.Number((IntFlags)unchecked((int)0x8000_0001)) == 0x8000_0001L, "an int enum's bit 31 is 2147483648, not a negative number");
        Check(FlagsRaw.Number(WideFlags.A | WideFlags.Top) == unchecked((long)0x8000_0000_0000_0001UL), "a ulong's bit 63 is SQLite's negative number of the same bits");

        // 1. Every flags column of every view has its number column right after it, but the record header's flags
        // (MajorFlags, StarfieldMajorRecordFlags), which the view has as MajorRecordFlagsRaw already
        var targets = new List<Target>();
        int flagsColumns = 0, headerFlags = 0, views = 0;
        var missing = new List<string>();
        var byType = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (string root in c.Engine.Views.RootNames)
        {
            var cat = c.Engine.Views.Catalog(root);
            foreach (var v in cat.AllViews)
            {
                views++;
                Check(v.Columns.Count(x => x.Kind == FlagsRaw.Kind) == v.Columns.Count(x => x.Kind == FlagsRaw.Kind && v.Columns.IndexOf(x) > 0 && v.Columns[v.Columns.IndexOf(x) - 1].Kind == "Flags"),
                    $"{v.Name}: a flags number column that doesn't follow its flags column");
                for (int i = 0; i < v.Columns.Count; i++)
                {
                    var col = v.Columns[i];
                    if (col.Kind != "Flags") continue;
                    flagsColumns++;
                    if (i + 1 < v.Columns.Count && v.Columns[i + 1] is { Kind: FlagsRaw.Kind } raw)
                    {
                        Check(raw.Name == col.Name + "Raw", $"{v.Name}: {col.Name}'s number column is {raw.Name}");
                        var t = FlagsRaw.EnumOf(col.Enum) ?? throw new TestFailed($"{v.Name}.{col.Name}: no enum {col.Enum}");
                        targets.Add(new Target(v, col, raw, t));
                        byType[root] = byType.GetValueOrDefault(root) + 1;
                    }
                    else if (col.SchemaPath is "MajorFlags" or "StarfieldMajorRecordFlags" && v.ByName.ContainsKey("MajorRecordFlagsRaw")) headerFlags++;
                    else missing.Add($"{v.Name}.{col.Name} ({col.Enum ?? "several enums"})");
                }
            }
        }
        Check(missing.Count == 0, $"{missing.Count} flags columns have no number column: {Cut(string.Join(", ", missing), 1500)}");
        Check(targets.Any(t => t.View.Name == "Furniture_MarkerParameters" && t.Flags.Name == "EntryTypes") && targets.Any(t => t.View.Name == "Race" && t.Flags.Name == "EquipmentFlags"),
            "Furniture_MarkerParameters.EntryTypesRaw and Race.EquipmentFlagsRaw are there");

        // 2. On Starfield.esm: each view's distinct (names, number) pairs, with how many rows and one row's place. A view
        // too big to read from Starfield.esm without being asked (the world types: cells, placed objects) is read there
        // with --heavy, else on a smaller plugin that has the type, as views-all does
        var engine = c.NewEngine("sql-flagraw");
        var fallbacks = new[] { "kgcdoom.esm", "darkstar.esm", "ShatteredSpace.esm", "sfbgs003.esm", "SFBGS00D.esm" }.Where(n => c.Index.Plugin(n)?.Segment != null).ToArray();
        bool Has(string pluginName, ViewSpec v) => c.Index.Plugin(pluginName)!.Segment!.Signatures.Any(s => s.Count > 0 && v.Types.Any(t => c.Schema.SignatureOf(t) == s.Signature));
        var elsewhere = new SortedDictionary<string, string>(StringComparer.Ordinal);
        int heavySkipped = 0, pairs = 0, rowsChecked = 0, mutagenChecked = 0, mutagenAbsent = 0, bitTests = 0, columnsWithRows = 0;
        long values = 0;
        var lines = new List<string>();
        var bit31 = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var group in targets.GroupBy(t => t.View))
        {
            var v = group.Key;
            if (!Has(SF, v)) continue;
            string plugin = SF;
            bool heavy = false;
            var places = new List<string> { "FormKey" };
            if (v.IsList) { places.Add("Idx"); for (int i = 2; i <= v.Lists.Count; i++) places.Add("Idx" + i); }
            foreach (var t in group)
            {
                string f = SqlText.Name(t.Flags.Name), r = SqlText.Name(t.Raw.Name);
                string where = v.IsList ? "" : $" WHERE {f} IS NOT NULL OR {r} IS NOT NULL";
                string sql = $"SELECT {f}, {r}, count(*), min({string.Join(" || '|' || ", places.Select(p => SqlText.Name(p)))}) FROM {SqlText.Name(v.Name)}{where} GROUP BY 1, 2";
                var a = engine.Run(new QueryRequest { Sql = sql, Plugins = new[] { plugin }, PageRows = 1_000_000, AllowHeavy = heavy });
                if (a.NotRun != null && plugin == SF && !heavy)
                {
                    if (c.Heavy) heavy = true;
                    else plugin = fallbacks.FirstOrDefault(fb => Has(fb, v)) ?? SF;
                    if (plugin == SF && !heavy) { elsewhere[v.Name] = "not run: heavy in Starfield.esm, in no smaller plugin tried"; break; }
                    if (plugin != SF) elsewhere[v.Name] = plugin;
                    a = engine.Run(new QueryRequest { Sql = sql, Plugins = new[] { plugin }, PageRows = 1_000_000, AllowHeavy = heavy });
                }
                Must(a, sql);
                if (a.Rows.Count == 0) continue;
                columnsWithRows++;
                long seen = 0;
                var counts = new Dictionary<long, long>();
                foreach (var row in a.Rows)
                {
                    pairs++;
                    long n = (long)row[2]!;
                    seen += n;
                    // The number and the names say the same flags; NULL together
                    Check(row[0] == null == (row[1] == null), $"{v.Name}.{t.Raw.Name}: {Cell(row[0])} beside {Cell(row[1])}");
                    if (row[1] is not long raw) continue;
                    Check(FromNames((string)row[0]!, t.Enum) == raw, $"{v.Name}.{t.Raw.Name} is {raw} (0x{raw:X}) beside {row[0]}");
                    rowsChecked++;
                    for (int b = 0; b < 64; b++) if (((raw >> b) & 1) != 0) counts[1L << b] = counts.GetValueOrDefault(1L << b) + n;
                    if (((raw >> 31) & 1) != 0) bit31[$"{v.Name}.{t.Raw.Name}"] = bit31.GetValueOrDefault($"{v.Name}.{t.Raw.Name}") + n;
                    // Mutagen's value at the row's place, cast to the enum's underlying type
                    string[] place = ((string)row[3]!).Split('|');
                    switch (MutagenValue(c, plugin, v, t.Flags, place))
                    {
                        case Enum e:
                            Check(e.GetType() == t.Enum, $"{v.Name}.{t.Raw.Name}: Mutagen's value is a {e.GetType().FullName}, the column's enum {t.Enum.FullName}");
                            Check(Cast(e) == raw, $"{v.Name}.{t.Raw.Name} at {row[3]}: {raw} (0x{raw:X}), Mutagen's value {e} = {Cast(e)} (0x{Cast(e):X})");
                            mutagenChecked++;
                            break;
                        case null: mutagenAbsent++; break;
                        case Skip s when ReferenceEquals(s, Skip.Heavy): heavySkipped++; break;
                        case Skip: break;
                        case var other: throw new TestFailed($"{v.Name}.{t.Flags.Name} at {row[3]}: Mutagen gives a {other.GetType().Name}");
                    }
                }
                values += seen;
                // 3. A bit by number counts what the bit by name counts: the lowest and highest bit seen, every bit of
                // RACE EquipmentFlags and of FURN's marker types
                bool every = v.Name is "Race" or "Furniture_MarkerParameters";
                var bits = counts.Keys.OrderBy(x => (ulong)x).ToList();
                foreach (long bit in every ? bits : bits.Take(1).Concat(bits.Skip(1).TakeLast(1)))
                {
                    string byNumber = $"SELECT count(*) FROM {SqlText.Name(v.Name)} WHERE ({r} & {bit}) <> 0";
                    string byName = $"SELECT count(*) FROM {SqlText.Name(v.Name)} sfs_v WHERE EXISTS (SELECT 1 FROM json_each(sfs_v.{f}) sfs_j WHERE sfs_j.value = {SqlText.Literal(BitName(t.Enum, bit))})";
                    long x = (long)Must(engine.Run(new QueryRequest { Sql = byNumber, Plugins = new[] { plugin }, AllowHeavy = heavy }), byNumber).Rows[0][0]!;
                    long y = (long)Must(engine.Run(new QueryRequest { Sql = byName, Plugins = new[] { plugin }, AllowHeavy = heavy }), byName).Rows[0][0]!;
                    Check(x == y && x == counts[bit], $"{v.Name}.{t.Raw.Name} bit 0x{bit:X}: {x} by number, {y} by name ({BitName(t.Enum, bit)}), {counts[bit]} in the values");
                    bitTests++;
                }
                lines.Add($"{v.Name}\t{plugin}\t{t.Raw.Name}\t{t.Enum.Name}\t{a.Rows.Count} values\t{seen} rows\t{string.Join(",", bits.Select(b => "0x" + unchecked((ulong)b).ToString("X", CultureInfo.InvariantCulture)))}");
            }
        }
        c.Save("flag-raw.tsv", lines);
        string furn = "Furniture_MarkerParameters.EntryTypesRaw";
        Check(bit31.GetValueOrDefault(furn) > 0 && bit31.GetValueOrDefault("Furniture_MarkerParameters.ExitTypesRaw") > 0, "FURN's marker parameters with bit 31 in Starfield.esm");
        return $"{flagsColumns:N0} flags columns in {views:N0} views; {targets.Count:N0} with a number column ({byType.Count} typed views and their lists: {string.Join(", ", byType.Select(x => $"{x.Key} {x.Value}"))}); {headerFlags} header flags left to MajorRecordFlagsRaw. "
            + $"On Starfield.esm (and smaller plugins for the world types): {columnsWithRows} columns with values, {values:N0} values in {pairs:N0} distinct (names, number) pairs, each number equal to its names ({rowsChecked:N0}); "
            + $"{mutagenChecked:N0} held against Mutagen's enum cast to its underlying type ({mutagenAbsent} absent in Mutagen, the column NULL or its default; {heavySkipped} in records too big to read whole held against their names only); {bitTests} bit tests by number equal to the test by name. "
            + $"Bit 31: {string.Join(", ", bit31.Select(x => $"{x.Key} {x.Value:N0}"))}. "
            + (elsewhere.Count == 0 ? "Every view read from Starfield.esm" : $"Read elsewhere (too big in Starfield.esm without --heavy): {string.Join(", ", elsewhere.Select(x => $"{x.Key} {x.Value}"))}");
    }

    sealed class Skip { public static readonly Skip It = new(), Heavy = new(); }

    /// <summary>Mutagen's value of a column at a row's place (FormKey, then each list's index): the record loaded through
    /// the records layer, each list walked to its entry, then the field's path. Null where an object on the way is
    /// absent or of a kind without the field; <see cref="Skip"/> for a dictionary or a grid.</summary>
    static object? MutagenValue(Ctx c, string plugin, ViewSpec v, ColumnSpec col, string[] place)
    {
        if (v.Lists.Any(l => l.Kind is "Dictionary" or "Array2D" or "Flags")) return Skip.It;
        c.Records.Sessions.Use(plugin);
        var r = c.Records.Find(plugin, place[0]);
        // A record too big to read whole without asking (Starfield.esm's navmesh info map, 51 MB): its numbers are held
        // against their names only
        if (c.Records.HeavyNote(r) != null) return Skip.Heavy;
        var rec = c.Records.Load(r);
        object? o = rec.Record;
        for (int i = 0; i < v.Lists.Count; i++)
        {
            o = Walk(o, v.Lists[i].Path);
            if (o is not IEnumerable items) return null;
            o = items.Cast<object?>().ElementAt(int.Parse(place[1 + i], CultureInfo.InvariantCulture));
        }
        return col.RelPath is { Length: > 0 } rel ? Walk(o, rel) : o;
    }

    static object? Walk(object? o, string path)
    {
        foreach (string name in path.Split('.'))
        {
            if (o == null) return null;
            var t = o.GetType();
            PropertyInfo? p = null;
            foreach (var i in t.GetInterfaces().Prepend(t))
            {
                try { p = i.GetProperty(name, BindingFlags.Public | BindingFlags.Instance); }
                catch (AmbiguousMatchException) { p = i.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == name); }
                if (p != null) break;
            }
            if (p == null) return null;
            o = p.GetValue(o);
        }
        return o;
    }
}
