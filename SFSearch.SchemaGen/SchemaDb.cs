// Reads a finished schema.db back into the model. Needs no Mutagen and no XML: this is what the service will use.
using Microsoft.Data.Sqlite;

namespace SFSearch.SchemaGen;

public sealed class SchemaDb
{
    public SchemaModel Model { get; } = new();
    /// <summary>xEdit's path for a field of one record type, where a checked pair names exactly that path.</summary>
    public Dictionary<(string Sig, string Path), (string XEditPath, string Readable)> PairNames { get; } = new();
    public Dictionary<(string Sig, string Object, string Field), string> Evidence { get; } = new();
    public Dictionary<(string Object, int Ord), (int Checked, int Agree, string Method)> PairChecks { get; } = new();

    public static SchemaDb Load(string file)
    {
        if (!File.Exists(file)) throw new SchemaException($"There is no schema at {file}. Build SFSearch.SchemaGen first.");
        var db = new SchemaDb();
        var m = db.Model;
        using var con = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
        con.Open();

        foreach (var r in Query(con, "SELECT Key, Value FROM Meta")) m.Meta[r.S(0)!] = r.S(1) ?? "";
        foreach (var r in Query(con, "SELECT Name, Base, Abstract, Sig, ObjType, Struct, FixedSize, File, Marker, EndMarker, Contains, HandRead, SelectedBy, SelectorValue, ConditionFunction, CkClass, CkName, ShortCircuit, SelectorEnum FROM Objects"))
        {
            var o = new ObjDef { Name = r.S(0)!, Base = r.S(1), Abstract = r.B(2), Sig = r.S(3), ObjType = r.S(4) ?? "Subrecord", Struct = r.B(5), FixedSize = r.I(6), File = r.S(7),
                Marker = r.S(8), EndMarker = r.S(9), HandRead = r.S(11), SelectedBy = r.S(12), SelectorValue = r.S(13), ConditionFunction = r.S(14), CkClass = r.S(15), CkName = r.S(16), ShortCircuit = r.S(17), SelectorEnum = r.S(18) };
            if (r.S(10) is { Length: > 0 } contains) o.Contains.AddRange(contains.Split(','));
            m.Objects[o.Name] = o;
        }
        // xEdit's name of each record type (GAPS S98)
        foreach (var r in Query(con, "SELECT Type, XEdit FROM RecordTypes"))
            if (m.Objects.TryGetValue(r.S(0)!, out var rt)) rt.XEditName = r.S(1);
        foreach (var r in Query(con, "SELECT Interface, Type FROM LinkInterfaces ORDER BY Type, Interface"))
            if (m.Objects.TryGetValue(r.S(1)!, out var o)) o.LinkInterfaces.Add(r.S(0)!);
        foreach (var r in Query(con, @"SELECT Object, Ord, Path, Name, Kind, Subrecord, Offset, OffsetRel, Size, StartsSubrecord, Nullable, InList, RefObject, LinkTo, Enum, DefaultValue,
                Translated, Unknown, Custom, Property, SigMap, GenericArg, ClrType, Readable, XEdit, XEditPairs, Unit, Note, Marker, EndMarker,
                StringsTable, CounterLength, TextForm, LayoutOffset, LayoutBits, LayoutSubrecord, LayoutEncoding, LayoutDroppedBits, LayoutPresentWhen, LayoutGroup, SelectedBy, LayoutMap, Source, ReadOnly, CkName, CkField,
                CustomSubrecords, CustomFills, FilledBy, SelectorEnum, GenderRecord, MaleMarker, FemaleMarker, MaxIsNone, ItemMarker, ItemEndMarker,
                IntegerType, Multiplier, Divisor
                FROM ObjectFields ORDER BY Object, Ord"))
            m.Objects[r.S(0)!].Fields.Add(new FieldDef
            {
                Object = r.S(0)!, Ord = r.I(1) ?? 0, Path = r.S(2)!, Name = r.S(3)!, Kind = r.S(4)!, Subrecord = r.S(5), Offset = r.I(6), OffsetRel = r.B(7), Size = r.I(8),
                StartsSubrecord = r.B(9), Nullable = r.B(10), InList = r.B(11), RefObject = r.S(12), LinkTo = r.S(13), Enum = r.S(14), Default = r.S(15),
                Translated = r.B(16), Unknown = r.B(17), Custom = r.B(18), Property = r.B(19), SigMap = r.S(20), GenericArg = r.S(21), ClrType = r.S(22),
                Readable = r.S(23), XEdit = r.S(24), XEditPairs = r.I(25) ?? 0, Unit = r.S(26), Note = r.S(27), Marker = r.S(28), EndMarker = r.S(29),
                StringsTable = r.S(30), CounterLength = r.I(31), TextForm = r.S(32),
                LayoutOffset = r.S(33), LayoutBits = r.S(34), LayoutSubrecord = r.S(35), LayoutEncoding = r.S(36), LayoutDroppedBits = r.S(37),
                LayoutPresentWhen = r.S(38), LayoutGroup = r.S(39), SelectedBy = r.S(40), LayoutMap = r.S(41), Source = r.S(42), ReadOnly = r.B(43), CkName = r.S(44), CkField = r.S(45),
                CustomSubrecords = r.S(46), CustomFills = r.S(47), FilledBy = r.S(48), SelectorEnum = r.S(49),
                GenderRecord = r.S(50), MaleMarker = r.S(51), FemaleMarker = r.S(52), MaxIsNone = r.B(53), ItemMarker = r.S(54), ItemEndMarker = r.S(55),
                IntegerType = r.S(56), Multiplier = r.S(57), Divisor = r.S(58),
            });
        foreach (var r in Query(con, "SELECT Kind, Sig, Object, Name, Xml FROM HandLayouts ORDER BY Ord"))
            m.HandLayouts.Add(new HandLayout(r.S(0)!, r.S(1), r.S(2), r.S(3), r.S(4)!));
        foreach (var r in Query(con, "SELECT Enum, Member, Value, IsFlags, XEditName, CkName, CkMember FROM Enums ORDER BY Enum, Value, Member"))
            (m.Enums.TryGetValue(r.S(0)!, out var l) ? l : m.Enums[r.S(0)!] = new()).Add(new EnumMember(r.S(1)!, r.L(2), r.B(3)) { XEditName = r.S(4), CkName = r.S(5), CkMember = r.S(6) });
        // The Creation Kit's names (to-do F30): empty tables only when the schema was built with --ck-names none
        m.Ck.Source = m.Meta.GetValueOrDefault("CkReflection") is { Length: > 0 } src ? src : null;
        foreach (var r in Query(con, "SELECT Type, Kind, Base, DisplayName, ToolTip, Hidden, Deprecated FROM CkTypes"))
            m.Ck.Types[r.S(0)!] = new CkType { Name = r.S(0)!, Kind = r.S(1)!, Base = r.S(2), DisplayName = r.S(3), ToolTip = r.S(4), Hidden = r.B(5), Deprecated = r.B(6) };
        foreach (var r in Query(con, "SELECT Type, Ord, Field, FieldType, DisplayName, ToolTip, Hidden, Deprecated, RangeMin, RangeMax, RangeStep FROM CkFields ORDER BY Type, Ord"))
            if (m.Ck.Types.TryGetValue(r.S(0)!, out var ct))
                ct.Fields.Add(new CkField { Type = ct.Name, Ord = r.I(1) ?? 0, Name = r.S(2)!, FieldType = r.S(3), DisplayName = r.S(4), ToolTip = r.S(5), Hidden = r.B(6), Deprecated = r.B(7), RangeMin = r.D(8), RangeMax = r.D(9), RangeStep = r.D(10) });
        foreach (var r in Query(con, "SELECT Type, Ord, Member, Value, DisplayName, ToolTip, Hidden, Deprecated FROM CkMembers ORDER BY Type, Ord"))
            if (m.Ck.Types.TryGetValue(r.S(0)!, out var ct))
                ct.Members.Add(new CkMember { Type = ct.Name, Ord = r.I(1) ?? 0, Name = r.S(2)!, Value = r.L(3), DisplayName = r.S(4), ToolTip = r.S(5), Hidden = r.B(6), Deprecated = r.B(7) });
        foreach (var r in Query(con, "SELECT Sig, Path, XEditPath, Readable FROM PairNames")) db.PairNames[(r.S(0)!, r.S(1)!)] = (r.S(2) ?? "", r.S(3) ?? "");
        foreach (var r in Query(con, "SELECT Sig, Object, Field, Evidence, TopValues, RecordsWithField, RecordsNonDefault, Note FROM UnknownEvidence"))
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(r.S(3))) parts.Add(r.S(3)!);
            parts.Add(r.I(5) is int with && with > 0 ? $"in {with} records of Starfield.esm, not the default in {r.I(6) ?? 0}" : "in no record of Starfield.esm");
            if (!string.IsNullOrEmpty(r.S(4))) parts.Add("most common: " + r.S(4));
            if (!string.IsNullOrEmpty(r.S(7))) parts.Add(r.S(7)!);
            db.Evidence[(r.S(0)!, r.S(1)!, r.S(2)!)] = string.Join("; ", parts);
        }
        foreach (var r in Query(con, "SELECT Object, Ord, sum(Checked), sum(Agree), min(Method) FROM Pairs WHERE Match IN ('exact', 'whole value') GROUP BY Object, Ord"))
            db.PairChecks[(r.S(0)!, r.I(1) ?? 0)] = (r.I(2) ?? 0, r.I(3) ?? 0, r.S(4) ?? "");
        m.Link();
        return db;
    }

    public sealed class Row(SqliteDataReader r)
    {
        public string? S(int i) => r.IsDBNull(i) ? null : r.GetValue(i).ToString();
        public int? I(int i) => r.IsDBNull(i) ? null : (int)r.GetInt64(i);
        public long L(int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i);
        public bool B(int i) => !r.IsDBNull(i) && r.GetInt64(i) != 0;
        public double? D(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    }

    public static IEnumerable<Row> Query(SqliteConnection con, string sql)
    {
        using var c = con.CreateCommand(); c.CommandText = sql;
        using var r = c.ExecuteReader();
        var row = new Row(r);
        while (r.Read()) yield return row;
    }
}
