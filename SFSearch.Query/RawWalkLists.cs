// A list view's named columns read from the record's bytes where the entries have no fixed size (GAPS S153, J114):
// entries that hold a text or a list, items of several subrecords, a component's fields after a text. RawLists.cs reads
// the lists whose every entry and value sits at a place the schema fixes; these are found by walking the record the way
// Mutagen's generated reader reads it, the walk the link census holds against Mutagen (LinkByteWalker, J68/J83), asked
// for every value (LinkByteWalker.WalkValues): each value with its place, each object it read, each part it passes over.
//
// A list is read this way when the walk reaches its entries (LinkByteWalker.ValueReach), and a column when it reaches
// the column's field in some kind of entry. Per record:
//   - the walk; a part it didn't read (a hand-read layout's run or subrecord, Mutagen's hand-written code, a layout the
//     bytes don't follow) at or above the list, or at or above a column's field, sends the record to Mutagen;
//   - the entries, in their order: every object or value the walk read at the list's place, by its indexes;
//   - each column of each entry, as DirectColumn gives it from Mutagen's object: the kind guards the view keeps (NULL for
//     an entry, or an object on the way, of a kind it doesn't take); the value as RawColumn reads that kind (a text by
//     its form, a translated text in the query's language, RawPlugin.TryText); a field the bytes don't hold as Mutagen
//     holds it (a nullable field or object NULL, a value the view's default, a required text ""), a property the entry's
//     class doesn't have as DirectColumn's walk of the class gives it. A field Mutagen holds otherwise (bytes, an asset)
//     when absent sends the record to Mutagen.
// What Mutagen reads with hand-written code and its package's HandReadLayouts.xml lays out (an object template's OBTS, a
// volumes component's VLMS, a pattern swap's DAT2, a patrol's topics, VMAD) the walk gives from the layout, each value as
// the layout stores it (a one-byte enum, a four-byte text, a WString), and the runs a type's layout cuts (a quest's
// aliases, a perk's ranks and effects) are walked as the objects Mutagen's generated reader makes of them (GAPS S164).
// A condition (every …_Conditions view) the walk gives by the record XML's layout of a CTDA (GAPS S166): a field of bits
// (CompareOperator, Flags, UseAliases) by its layoutBits and layoutDroppedBits, its data's fields, and its function's
// parameters and texts at their places.
// NVNM's geometry, NVMI and a surface tree's FNAM grids are read by Mutagen's hand-written code that no layout in the
// package describes: they stay on Mutagen (GAPS S153).
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins.Binary.Translations;
using SFSearch.Records;
using SFSearch.SchemaGen;

namespace SFSearch.Query;

sealed partial class RawList
{
    /// <summary>Walk: the record type's link fields (the walk's tables), the list's places as the walk's paths.</summary>
    LinkFields? _fields;
    string _listPlain = "";
    string _first = "";
    string[] _levelPaths = Array.Empty<string>();
    int _levels;
    ColumnSpec[] _specs = Array.Empty<ColumnSpec>();
    /// <summary>Each column's plain path from the record (the list's place, then its RelPath): what an unread part blocks.</summary>
    string[] _colPlain = Array.Empty<string>();
    /// <summary>The fields of the columns whose value may be a list (VaryingColumn.cs): a part the walk didn't read at
    /// or under one of them (an item of the list) leaves the record to Mutagen too.</summary>
    string[] _colVarying = Array.Empty<string>();
    RecordSchema? _schema;
    readonly ConcurrentDictionary<(int Column, string Kind), WalkColumn?> _plans = new();
    /// <summary>The subrecords one of which a record must hold to hold an entry (null: not known, every record is walked).</summary>
    uint[]? _entrySigs;
    /// <summary>The list is read by the value walk.</summary>
    public bool Walks => _shape == Shape.Walk;
    /// <summary>For a walked list: the subrecords one of which a record holds when it holds an entry (the list's, its
    /// entries', their fields', counters and markers); null when not known.</summary>
    public IReadOnlyCollection<string>? EntrySubrecords => _entrySigs?.Select(x => System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(x))).ToArray();

    /// <summary>The value paths' ends the columns read (see Found.Keep).</summary>
    string[]? _keep;
    /// <summary>The parts of the record the walk reads: those that hold the list, and the list's own.</summary>
    Func<string, bool>? _want;
    /// <summary>The objects (paths with [*]) the view's guard needs that the record always has (ColumnSpec.GuardOmittable):
    /// whether the serializer writes each is worked out from its values (Found.Guard).</summary>
    HashSet<string>? _guardObjects;

    static readonly Assembly Starfield = typeof(Mutagen.Bethesda.Starfield.StarfieldMod).Assembly;

    /// <summary>A list view whose entries the record's value walk reaches, with each named column's field reached in
    /// some kind of entry; null otherwise.</summary>
    static RawList? MakeWalk(ViewSpec v, IReadOnlyList<string> columns, RecordSchema schema)
    {
        if (!v.IsList || v.IsUnion || v.Types.Count != 1 || v.Lists.Any(l => l.Kind is not ("List" or "Array"))) return null;
        string type = v.Types[0];
        var reach = LinkByteWalker.ValueReach(schema, type);
        string listPlain = string.Join(".", v.Lists.Select(l => l.Path + "[*]"));
        var reached = reach.Where(kv => kv.Value == null).Select(kv => LinkByteWalker.Unmarked(kv.Key)).ToHashSet(StringComparer.Ordinal);
        if (!reached.Contains(listPlain)) return null;
        // A column's field reached in one kind of entry and not in another: an entry of the other kind is a part the walk
        // says it didn't read (a hand-read list, a field after hand-written code), and its record goes to Mutagen
        var known = reach.Keys.Select(LinkByteWalker.Unmarked).ToHashSet(StringComparer.Ordinal);
        var specs = new ColumnSpec[columns.Count];
        var plain = new string[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            if (!v.ByName.TryGetValue(columns[i], out var c)) return null;
            specs[i] = c;
            if (c.Kind == "ObjectType")
            {
                // The entry's own kind, or an object's in it that the walk reaches
                if (c.ObjectPath is not { } op || op.Length > 0 && !known.Any(r => r.StartsWith(listPlain + "." + op + ".", StringComparison.Ordinal))) return null;
                plain[i] = op.Length == 0 ? listPlain : listPlain + "." + op;
                continue;
            }
            if (c.RelPath is not { } rel || !WalkKinds.Contains(c.Kind)) return null;
            plain[i] = rel.Length == 0 ? listPlain : listPlain + "." + rel;
            if (!known.Contains(plain[i]))
            {
                // A field only the kind has that a BFCB text naming no component picks (UnknownComponent's TypeName, its
                // selector "*"): Mutagen's hand-written code reads that kind whole, and the walk says its fields unread
                // where it meets one, so its record goes to Mutagen; in a record the walk reads, the field isn't there
                if (!(c.Row?.Def is { } only && schema.Model.Objects.TryGetValue(only.Object, out var owner) && owner.SelectorValue == "*"
                    && schema.Model.Ancestry(only.Object).Any(a => a.Name == SFSearch.SchemaGen.Selectors.Component))) return null;
            }
        }
        // The subrecords an entry of the list is read from (its list's, its own, its fields', their counters): a record
        // that holds none of them has no entry, and isn't walked
        string listField = listPlain[..^3];
        var sigs = new HashSet<uint>();
        foreach (var r in schema.Model.Expand(type))
        {
            if (!(r.PlainPath == listField || Under(r.PlainPath, listField))) continue;
            if (r.Subrecord is { Length: 4 } sub) sigs.Add(BinaryPrimitives.ReadUInt32LittleEndian(System.Text.Encoding.ASCII.GetBytes(sub)));
            foreach (var cs in r.Def.CustomSubrecordList) if (cs.Length == 4) sigs.Add(BinaryPrimitives.ReadUInt32LittleEndian(System.Text.Encoding.ASCII.GetBytes(cs)));
            if (r.Def.Note != null && CountedBy.Match(r.Def.Note) is { Success: true } cb) sigs.Add(Sig(cb.Groups[1].Value));
            if (r.Def.Marker is { Length: 4 } mk) sigs.Add(Sig(mk));
            if (r.Def.EndMarker is { Length: 4 } emk) sigs.Add(Sig(emk));
        }
        return new RawList(Shape.Walk)
        {
            _fields = LinkByteWalker.FieldsOf(schema, type), _listPlain = listPlain, _first = v.Lists[0].Path + "[",
            _levelPaths = v.Lists.Select(l => l.Path).ToArray(), _levels = v.Lists.Count,
            _specs = specs, _colPlain = plain, _colVarying = plain.Where((_, i) => specs[i].ShapeVaries).ToArray(), _schema = schema, _entrySigs = sigs.Count == 0 ? null : sigs.ToArray(),
            _keep = specs.Where(c => c.Kind != "ObjectType" && c.RelPath is { Length: > 0 }).Select(c => "]." + c.RelPath).Distinct().ToArray(),
            _guardObjects = specs.Any(c => c.GuardOmittable && c.NullGuard != null)
                ? specs.Where(c => c.GuardOmittable && c.NullGuard != null).Select(c => listPlain + "." + c.NullGuard).ToHashSet(StringComparer.Ordinal) : null,
            _want = p => Under(listPlain, p) || Under(p, listPlain),
        };
    }

    /// <summary>The kinds of value a walked column reads: those RawColumn reads at a place, and texts.</summary>
    static readonly HashSet<string> WalkKinds = new(StringComparer.Ordinal)
    {
        "FormLink", "Int8", "Int16", "Int32", "Int64", "UInt8", "UInt16", "UInt32", "Bool", "Float", "Double", "Half",
        "P2Float", "P3Float", "P2Int16", "P3Int16", "P2Int32", "ByteArray", "Color", "Guid", "Enum", "Flags", FlagsRaw.Kind, "String", "AssetLink",
        "RecordType", "Percent", "UInt64", "TimeOnly",
    };

    [ThreadStatic] static LinkByteWalker? t_walker;

    /// <summary>The value walk's findings for one record.</summary>
    internal sealed class Found : IRecordValueSink
    {
        public readonly Dictionary<string, string> Objects = new(StringComparer.Ordinal);
        /// <summary>Each value kept: its field, its place, and the kind a hand-read layout stores it as (null: as the
        /// field's own kind, the generated reader's).</summary>
        public readonly Dictionary<string, (FieldDef Field, int Sub, int At, int End, string? Laid)> Values = new(StringComparer.Ordinal);
        public readonly List<string> Unread = new();
        public void Object(string path, string kind)
        {
            path = Strip(path);
            Objects[path] = kind;
            if (Guards != null) Track(path, null, 0, 0, 0, inside: true);
        }
        /// <summary>The guard objects the serializer may leave out (RawList._guardObjects), and the record's bytes.</summary>
        public HashSet<string>? Guards;
        public RawRecord? Raw;
        /// <summary>For each guard object met: 1 when something in it is written (a value not at its default, a list's
        /// entry, a yes/no that is there), 2 when the bytes can't say (a text, a value with a default of its own, an object
        /// in it); absent when nothing in it is written, so the serializer leaves it out.</summary>
        public readonly Dictionary<string, int> Guard = new(StringComparer.Ordinal);
        /// <summary>Notes what a value (or, <paramref name="inside"/>, an object) under a guard object says about it.</summary>
        void Track(string path, FieldDef? field, int sub, int at, int end, bool inside = false)
        {
            for (int i = 1; i < path.Length; i++)
            {
                if (path[i] != '.' && path[i] != '[') continue;
                string obj = path[..i];
                if (!Guards!.Contains(Indexes.Replace(obj, "[*]"))) continue;
                int state;
                if (inside || field == null) state = 2;
                else if (path.IndexOf('[', i) >= 0) state = 1;                                   // a list's entry: the list isn't empty
                else if (field.Kind == "Bool" && field.Size == 0) state = 1;                       // a yes/no that is its subrecord's presence
                else if (field.Kind is "String" or "AssetLink") state = field.Nullable ? 1 : 2;     // a text that may be absent is there
                // A field of bits of one byte (the record XML's layoutBits): written when one of its own bits is set
                else if (field.LayoutMask is var (mask, _)) state = end - at == 1 && (Raw!.Content(Raw.Subrecords[sub])[at] & mask & ~(int)field.LayoutDroppedMask) == 0 ? 0 : 1;
                else if (field.Default != null) state = 2;
                else state = Raw!.Content(Raw.Subrecords[sub])[at..end].IndexOfAnyExcept((byte)0) >= 0 ? 1 : 0;
                if (state > Guard.GetValueOrDefault(obj)) Guard[obj] = state;
            }
        }
        /// <summary>The ends of the paths whose values are kept ("." and a column's RelPath; null: every value): an entry's
        /// other fields are only stepped over.</summary>
        public string[]? Keep;
        public void Value(string path, FieldDef field, int subrecord, int at, int end)
        {
            if (Guards != null) Track(Strip(path), field, subrecord, at, end);
            if (Keep != null && !Kept(path)) return;
            Values[Strip(path)] = (field, subrecord, at, end, null);
        }
        /// <summary>The values a layout states with no bytes (IRecordValueSink.Stated), by path.</summary>
        public readonly Dictionary<string, string> States = new(StringComparer.Ordinal);
        public void Stated(string path, FieldDef field, string value)
        {
            // (what a stated value says of a guard object the serializer may leave out, the bytes can't say)
            if (Guards != null) Track(Strip(path), null, 0, 0, 0);
            if (Keep != null && !Kept(path)) return;
            Values[Strip(path)] = (field, -1, 0, 0, "Const");
            States[Strip(path)] = value;
        }
        public void LaidOut(string path, FieldDef field, int subrecord, int at, int end, string layoutKind)
        {
            if (Guards != null) Track(Strip(path), field, subrecord, at, end);
            if (Keep != null && !Kept(path)) return;
            Values[Strip(path)] = (field, subrecord, at, end, layoutKind);
        }
        /// <summary>A value stored with an encoding this reader knows (LaidReader): kept with the layout's kind and the
        /// encoding ("U8:speed"); any other is unread.</summary>
        public void Encoded(string path, FieldDef field, int subrecord, int at, int end, string layoutKind, string encoding)
        {
            if (encoding is not ("speed" or "10min" or "/255" or "/1000" or "length")) { ((IRecordValueSink)this).Unread(path, "a value Mutagen's hand-written code works out from the bytes"); return; }
            LaidOut(path, field, subrecord, at, end, layoutKind + ":" + encoding);
        }
        bool Kept(string path)
        {
            // A plain entry's own value (a list of links) ends in its index: always kept, it is how its entry is found
            if (path.EndsWith(']')) return true;
            foreach (var k in Keep!) if (path.EndsWith(k, StringComparison.Ordinal)) return true;
            return false;
        }
        /// <summary>Why each of <see cref="Unread"/> wasn't read, in step with it.</summary>
        public readonly List<string> UnreadWhy = new();
        void IRecordValueSink.Unread(string path, string why) { Unread.Add(Strip(path)); UnreadWhy.Add(why); }
    }

    /// <summary>Why the value walk left a record to Mutagen: the part it didn't read (its path, and the walk's reason),
    /// or the column it had no reader or no value for (and the kind of the entry). Empty when the walk didn't leave it.</summary>
    internal readonly record struct WalkLeft(string? Unread, string? Why, string? Column, string? Kind, bool NoReader);

    /// <summary>A walk's path with its kinds of object left out.</summary>
    static string Strip(string path)
    {
        int b = path.IndexOf('{');
        if (b < 0) return path;
        var sb = new System.Text.StringBuilder(path.Length);
        sb.Append(path, 0, b);
        for (int i = b; i < path.Length; i++)
        {
            if (path[i] == '{') { int e = path.IndexOf('}', i); i = e < 0 ? path.Length : e; continue; }
            sb.Append(path[i]);
        }
        return sb.ToString();
    }
    static readonly Regex Indexes = new(@"\[\d+\]", RegexOptions.Compiled);

    static bool Under(string path, string over) => over.Length == 0 || path == over || path.StartsWith(over + ".", StringComparison.Ordinal) || path.StartsWith(over + "[", StringComparison.Ordinal);

    /// <summary>The rows of one record by the value walk; false when the record goes to Mutagen.</summary>
    bool ReadWalk(ReadOnlySpan<byte> head, ReadOnlySpan<byte> data, RawPlugin plugin, List<object?[]> rows, out WalkLeft left)
    {
        left = default;
        // A record with none of the subrecords an entry is read from has no entry
        if (_entrySigs != null && !HoldsAny(data, _entrySigs)) return true;
        var raw = RawRecord.FromInflated(head, data);
        var walker = t_walker;
        if (walker == null || walker.Schema != _schema) t_walker = walker = new LinkByteWalker(_schema!);
        var found = new Found { Keep = _keep, Guards = _guardObjects, Raw = raw };
        walker.WalkValues(_fields!, raw, plugin.Localized, found, want: _want, component: k => plugin.Component(raw, k, _fields!.Type));
        // A part the walk didn't read, at or above the list or a column's field
        for (int ui = 0; ui < found.Unread.Count; ui++)
        {
            string g = Indexes.Replace(found.Unread[ui], "[*]");
            bool blocks = Under(_listPlain, g);
            foreach (string cp in _colPlain) if (Under(cp, g)) blocks = true;
            foreach (string cp in _colVarying) if (Under(g, cp)) blocks = true;
            if (!blocks) continue;
            left = new WalkLeft(g, found.UnreadWhy[ui], null, null, false);
            return false;
        }
        // The entries, by their indexes
        var entries = new List<(long[] Idx, string Path)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Consider(string key)
        {
            if (!key.StartsWith(_first, StringComparison.Ordinal) || EntryIndexes(key) is not { } idx || !seen.Add(key)) return;
            entries.Add((idx, key));
        }
        foreach (var k in found.Objects.Keys) Consider(k);
        foreach (var k in found.Values.Keys) Consider(k);
        entries.Sort((a, b) =>
        {
            for (int i = 0; i < a.Idx.Length; i++) { int c = a.Idx[i].CompareTo(b.Idx[i]); if (c != 0) return c; }
            return 0;
        });
        foreach (var (idx, path) in entries)
        {
            var row = new object?[_levels + _specs.Length];
            for (int i = 0; i < _levels; i++) row[i] = idx[i];
            string kind = found.Objects.TryGetValue(path, out var k) ? k : "";
            for (int i = 0; i < _specs.Length; i++)
            {
                var plan = _plans.GetOrAdd((i, kind), key => WalkColumn.Make(_specs[key.Column], key.Kind, _schema!));
                if (plan == null || !plan.Read(path, found, raw, plugin, out row[_levels + i]))
                {
                    left = new WalkLeft(null, null, _specs[i].Name, kind, plan == null);
                    return false;
                }
            }
            rows.Add(row);
        }
        return true;
    }

    /// <summary>The indexes of a path that is an entry of the list (<c>Components[3].Items[0]</c>), else null.</summary>
    long[]? EntryIndexes(string key)
    {
        var idx = new long[_levels];
        int at = 0;
        for (int i = 0; i < _levels; i++)
        {
            string level = _levelPaths[i];
            if (i > 0) { if (at >= key.Length || key[at] != '.') return null; at++; }
            if (string.CompareOrdinal(key, at, level, 0, level.Length) != 0) return null;
            at += level.Length;
            if (at >= key.Length || key[at] != '[') return null;
            long n = 0; int digits = 0;
            for (at++; at < key.Length && key[at] is >= '0' and <= '9'; at++, digits++) n = n * 10 + (key[at] - '0');
            if (digits == 0 || at >= key.Length || key[at] != ']') return null;
            at++;
            idx[i] = n;
        }
        return at == key.Length ? idx : null;
    }

    /// <summary>Whether the record's subrecords, at any depth, hold one of these signatures.</summary>
    static bool HoldsAny(ReadOnlySpan<byte> data, uint[] sigs)
    {
        int p = 0; uint big = 0; bool hasBig = false;
        while (p + 6 <= data.Length)
        {
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(data[p..]);
            int n = BinaryPrimitives.ReadUInt16LittleEndian(data[(p + 4)..]);
            p += 6;
            if (sig == XXXX && n == 4 && p + 4 <= data.Length) { big = BinaryPrimitives.ReadUInt32LittleEndian(data[p..]); hasBig = true; p += 4; continue; }
            long len = hasBig ? big : n; hasBig = false;
            if (Array.IndexOf(sigs, sig) >= 0) return true;
            if (p + len > data.Length) return true;
            p += (int)len;
        }
        // Bytes past the last whole subrecord: let the walk say what it makes of them
        return p != data.Length;
    }

    /// <summary>
    /// One column of a walked list for one kind of entry, as DirectColumn reads it from Mutagen's object of that kind:
    /// the guards, the walk through the entry's class, the value read where the walk found it or Mutagen's value for a
    /// field the bytes don't hold.
    /// </summary>
    internal sealed class WalkColumn
    {
        ColumnSpec _c = null!;
        string[] _segs = Array.Empty<string>();
        /// <summary>The entry's class (null: a plain value).</summary>

        /// <summary>The view's default for a value left out (its coalesce), when the column has one; NULL otherwise.</summary>
        object? _default;
        bool _absentIsNull;
        (int Depth, HashSet<string> Kinds)[] _guards = Array.Empty<(int, HashSet<string>)>();
        /// <summary>For each step of the path: the property is on the class there (false past one that isn't), and the
        /// schema's row of the object or field (null when the schema has none there).</summary>
        bool[] _has = Array.Empty<bool>();
        FieldDef?[] _defs = Array.Empty<FieldDef?>();
        readonly ConcurrentDictionary<(FieldDef, string?), Func<ReadOnlySpan<byte>, RawPlugin, (bool, object?)>?> _readers = new();
        string? _objectType;
        /// <summary>The CLR type of the field's property on the entry's class (an enum's own type, Weapon.Property for an
        /// object template's property of a weapon); null when the path leaves the class.</summary>
        Type? _leafType;
        /// <summary>The steps to the object the view's guard needs, which the record always has (ColumnSpec.GuardOmittable);
        /// -1 for none.</summary>
        int _omittableAt = -1;
        /// <summary>In this kind of entry the column's field is a list of plain values (VaryingColumn.cs: a script
        /// property's Data on ScriptIntListProperty): the schema's row of its items; null for a plain value.</summary>
        FieldDef? _arrayItem;

        public static WalkColumn? Make(ColumnSpec c, string kind, RecordSchema schema)
        {
            var w = new WalkColumn { _c = c, _omittableAt = c.GuardOmittable && c.NullGuard is { } ng ? ng.Split('.').Length : -1 };
            Type? cls = kind.Length == 0 ? null : ClassOf(kind);
            if (kind.Length > 0 && cls == null) return null;
            kind = KindOf(kind);

            bool guarded = !c.Nullable && c.DefaultSql != null;
            if (guarded && !DirectColumn.TryLiteral(c.DefaultSql!, out w._default)) return null;
            w._absentIsNull = !guarded || c.NullGuard != null;
            if (guarded && !c.FromBase) w._guards = c.VariantGuards.Select(g => (g.ObjPath.Length == 0 ? 0 : g.ObjPath.Split('.').Length, g.Variants)).ToArray();
            if (c.Kind == "ObjectType")
            {
                w._segs = c.ObjectPath!.Length == 0 ? Array.Empty<string>() : c.ObjectPath.Split('.');
                if (w._segs.Length == 0) { if (cls == null) return null; w._objectType = DirectColumn.ObjectTypeName(cls); }
                return w;
            }
            w._segs = c.RelPath!.Length == 0 ? Array.Empty<string>() : c.RelPath.Split('.');
            // The class's properties and the schema's rows along the path
            w._has = new bool[w._segs.Length];
            w._defs = new FieldDef?[w._segs.Length];
            var rows = kind.Length == 0 ? new List<FieldRow>() : schema.Model.Expand(kind).ToList();
            Type? t = cls;
            for (int i = 0; i < w._segs.Length; i++)
            {
                var p = t == null ? null : FindProperty(t, w._segs[i]);
                w._has[i] = p != null;
                if (p == null) break;
                t = p.PropertyType;
                if (System.Nullable.GetUnderlyingType(t) is { } u) t = u;
                string at = string.Join(".", w._segs.Take(i + 1));
                var r = rows.Where(x => x.PlainPath == at).Select(x => x.Def).Distinct().ToList();
                w._defs[i] = r.Count == 1 ? r[0] : null;
                if (i == w._segs.Length - 1) w._leafType = t;
                // A list in this kind where another kind holds a plain value: read as its JSON text, item by item
                if (i == w._segs.Length - 1 && c.ShapeVaries && w._defs[i]?.Kind is "List" or "Array")
                {
                    w._arrayItem = rows.Where(x => x.PlainPath == at + "[*]").Select(x => x.Def).Distinct().SingleOrDefault();
                    if (w._arrayItem == null) return null;
                }
            }
            return w;
        }

        /// <summary>A walk's kind of object without its generic argument (<c>ObjectTemplate</c> of
        /// <c>ObjectTemplate&lt;Weapon.Property&gt;</c>): the kind the view's guards compare.</summary>
        internal static string KindOf(string kind) => kind.IndexOf('<') is int lt and >= 0 ? kind[..lt] : kind;

        static readonly ConcurrentDictionary<string, Type?> Classes = new(StringComparer.Ordinal);

        /// <summary>Mutagen's class of a walk's kind of object: <c>ObjectModFloatProperty&lt;Weapon.Property&gt;</c> is
        /// ObjectModFloatProperty`1 closed over Weapon.Property.</summary>
        internal static Type? ClassOf(string kind) => Classes.GetOrAdd(kind, k =>
        {
            int lt = k.IndexOf('<');
            if (lt < 0) return Starfield.GetType("Mutagen.Bethesda.Starfield." + k);
            var open = Starfield.GetType("Mutagen.Bethesda.Starfield." + k[..lt] + "`1");
            var arg = Starfield.GetType("Mutagen.Bethesda.Starfield." + k[(lt + 1)..^1].Replace('.', '+'));
            return open == null || arg == null ? null : open.MakeGenericType(arg);
        });

        /// <summary>A path under an entry; the record itself is the entry "" (a typed view's column, RawWalkTyped.cs).</summary>
        static string At(string entry, string rest) => entry.Length == 0 ? rest : entry + "." + rest;

        /// <summary>The column's value for the entry at <paramref name="entry"/>; false when only Mutagen can say it.</summary>
        public bool Read(string entry, Found found, RawRecord raw, RawPlugin plugin, out object? value)
        {
            value = null;
            // The guards: the kind of the entry, or of an object in it, that the view takes
            foreach (var (depth, kinds) in _guards)
            {
                string at = depth == 0 ? entry : At(entry, string.Join(".", _segs.Take(depth)));
                if (!found.Objects.TryGetValue(at, out var k)) return true;
                if (!kinds.Contains(KindOf(k))) return true;
            }
            if (_c.Kind == "ObjectType")
            {
                if (_objectType != null) { value = _objectType; return true; }
                string at = At(entry, string.Join(".", _segs));
                if (!found.Objects.TryGetValue(at, out var k)) return true;
                if (ClassOf(k) is not { } kc) return false;
                value = DirectColumn.ObjectTypeName(kc);
                return true;
            }
            // The object the view's guard needs, which the record always has: the serializer leaves it out when it has
            // nothing in it to write (a dialogue action's Headtracking read from an HNAM HNAM pair), and the view gives NULL
            if (_omittableAt > 0)
            {
                string guard = At(entry, string.Join(".", _segs.Take(_omittableAt)));
                if (!found.Objects.ContainsKey(guard) || !found.Guard.TryGetValue(guard, out int state) || state == 0) { value = Missing(_omittableAt); return true; }
                if (state == 2) return false;
            }
            string path = _segs.Length == 0 ? entry : At(entry, _c.RelPath!);
            // A list of plain values: each item the walk placed, in order, as the list's JSON text (none: left out)
            if (_arrayItem != null)
            {
                var items = new List<object>();
                for (int k = 0; found.Values.TryGetValue(path + "[" + k.ToString(CultureInfo.InvariantCulture) + "]", out var iv); k++)
                {
                    if (iv.Sub < 0) return false;
                    var itemContent = raw.Content(raw.Subrecords[iv.Sub]);
                    if (iv.End > itemContent.Length || iv.At > iv.End || !VaryingColumn.TryItem(_arrayItem, iv.Laid, itemContent[iv.At..iv.End], out object item)) return false;
                    items.Add(item);
                }
                value = VaryingColumn.ArrayText(items);
                return true;
            }
            if (found.Values.TryGetValue(path, out var v))
            {
                if (v.Laid == "Const") return StatedValue(v.Field, found.States[path], plugin, out value);
                var read = _readers.GetOrAdd((v.Field, v.Laid), x => x.Item2 == null ? Reader(x.Item1) : LaidReader(x.Item1, x.Item2));
                if (read == null) return false;
                var content = raw.Content(raw.Subrecords[v.Sub]);
                if (v.End > content.Length || v.At > v.End) return false;
                bool ok;
                (ok, value) = read(content[v.At..v.End], plugin);
                return ok;
            }
            // Not in the bytes: what Mutagen's object holds, walked as DirectColumn walks it
            for (int i = 0; i < _segs.Length; i++)
            {
                bool last = i == _segs.Length - 1;
                // A class without the property: the field a null value, an object on the way an absent one
                if (!_has[i]) { value = last ? Null() : Missing(i + 1); return true; }
                if (_defs[i] is not { } d) return false;
                if (last)
                {
                    if (d.Nullable || d.Kind is not ("String" or "ByteArray" or "AssetLink")) { value = Null(); return true; }
                    // A required text Mutagen holds as an empty one; bytes and assets as Mutagen makes them
                    if (d.Kind == "String") { value = ""; return true; }
                    // Required bytes the record's data ends before (a surface block's UnknownEnding after a version break):
                    // Mutagen's empty ones, or its zeros when the field has a size
                    if (d.Kind == "ByteArray" && _c.Kind == "ByteArray") { value = "0x" + new string('0', 2 * (d.Size ?? 0)); return true; }
                    return false;
                }
                // An object on the way the bytes don't hold: absent when it may be (NULL past it), or when the view's guard
                // needs it and the record always has it (the serializer leaves such an object out when it has nothing in
                // it to write, as an object with no bytes: a dialogue action's Headtracking); else Mutagen's own with its
                // fields' defaults
                if (!found.Objects.ContainsKey(At(entry, string.Join(".", _segs.Take(i + 1)))) && (d.Nullable || i + 1 == _omittableAt)) { value = Missing(i + 1); return true; }
            }
            return false;
        }

        /// <summary>
        /// A value a layout states with no bytes (a Const), as the column gives Mutagen's value: an enum by its member's
        /// name, a yes/no, a whole number, each as the bytes of that value would read (so a value at its default is the
        /// view's default, as the serializer leaves it out); a text as it stands. False when the column's kind isn't one of
        /// these, or the text isn't a value of the field: Mutagen then says it.
        /// </summary>
        bool StatedValue(FieldDef f, string text, RawPlugin plugin, out object? value)
        {
            value = null;
            if (_c.Kind == "String")
            {
                if (f.Kind != "String" || f.Translated) return false;
                value = text;
                return true;
            }
            if (ReadKind(f) is not { } kind) return false;
            long n; int size; Type? et = null;
            if (FlagsRaw.IsEnumKind(kind))
            {
                if ((et = EnumOf(f)) == null || !Enum.TryParse(et, text, out object? member) || member == null) return false;
                n = Convert.ToInt64(member, CultureInfo.InvariantCulture);
                size = System.Runtime.InteropServices.Marshal.SizeOf(Enum.GetUnderlyingType(et));
            }
            else if (kind == "Bool")
            {
                if (!bool.TryParse(text, out bool yes)) return false;
                n = yes ? 1 : 0; size = 1;
            }
            else if (kind is "Int8" or "Int16" or "Int32" or "Int64" or "UInt8" or "UInt16" or "UInt32")
            {
                if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) return false;
                size = kind switch { "Int8" or "UInt8" => 1, "Int16" or "UInt16" => 2, "Int64" => 8, _ => 4 };
            }
            else return false;
            var read = _readers.GetOrAdd((f, "Const:" + size.ToString(CultureInfo.InvariantCulture)), _ =>
            {
                var rc = RawColumn.Entry(_c, f, 0, size, et, kind: kind);
                return rc == null ? null : (b, p) => rc.Read(b, true, p, out var x) == RawColumn.Outcome.Value ? (true, x) : (false, null);
            });
            if (read == null) return false;
            Span<byte> bytes = stackalloc byte[size];
            for (int i = 0; i < size; i++) bytes[i] = (byte)(n >> (8 * i));
            bool ok;
            (ok, value) = read(bytes, plugin);
            return ok;
        }

        /// <summary>DirectColumn.Convert(null): a link NULL, else the view's default.</summary>
        object? Null() => _c.Kind == "FormLink" ? null : _default;

        /// <summary>DirectColumn.Missing: the object that many steps in is absent.</summary>
        object? Missing(int depth)
        {
            if (depth == 0 || _absentIsNull) return null;
            foreach (var (d, _) in _guards) if (d >= depth) return null;
            return _default;
        }

        /// <summary>
        /// How a value a hand-read layout lays out is read from its bytes (GAPS S164): as the layout stores it, its width
        /// the layout's (a property's FunctionType one byte), as DirectColumn gives Mutagen's value. A column of several
        /// kinds of entry (an object template property's Value: a number, a float, a yes/no or a text) gives each entry's
        /// value by its own kind, as DirectColumn's Plain does; an enum in a number column its number. Texts: a ZString up
        /// to its zero (Mutagen's ProcessWholeToZString), a WString or I32String's text as Mutagen's length-prefixed reads
        /// take it, an LString of a localized plugin its strings ID. A Mutagen RecordType from its four bytes.
        /// </summary>
        Func<ReadOnlySpan<byte>, RawPlugin, (bool, object?)>? LaidReader(FieldDef f, string laid)
        {
            var c = _c;
            var nonTranslated = Mutagen.Bethesda.Plugins.Meta.GameConstants.Starfield.Encodings.NonTranslated;
            if (laid.Contains(':'))
            {
                // A cloud layer's X or Y speed (the layout's encoding="speed"): one byte, read as Mutagen's
                // WeatherBinaryCreateTranslation.ConvertToSpeed reads it, in its own arithmetic
                // A time of day (a climate's sunrise and sunset, the record XML's uint8*10min): one byte of tens of
                // minutes, as Mutagen's ClimateBinaryCreateTranslation.GetTime reads it; past 144 (a day) its reader
                // leaves the field as it was, which the bytes don't say
                if (laid == "U8:10min")
                    return f.Kind != "TimeOnly" || c.Kind != "TimeOnly" ? null
                        : (b, _) => b.Length != 1 || b[0] > 144 ? (false, null) : (true, DirectColumn.Plain(new TimeOnly().AddMinutes(b[0] * 10)));
                // The number of the bytes itself (the layout's encoding="length": a cell's and a placed object's
                // NumTraversalFluffBytes, what is left of XTV2 after its last entry, as Mutagen's Parse counts it)
                if (laid == "Bytes:length")
                {
                    if (f.Kind != "UInt32" || c.Kind != "UInt32" || RawColumn.Entry(c, f, 0, 4, null, kind: "UInt32") is not { } count) return null;
                    return (b, p) =>
                    {
                        Span<byte> four = stackalloc byte[4];
                        BinaryPrimitives.WriteUInt32LittleEndian(four, (uint)b.Length);
                        return count.Read(four, true, p, out var n) == RawColumn.Outcome.Value ? (true, n) : (false, null);
                    };
                }
                if (f.Kind != "Float" || c.Kind != "Float") return null;
                // A leveled list's chance of none before form version 510: a byte of 255 (Mutagen's GetChance: chance / 255f)
                if (laid == "U8:/255") return (b, _) => b.Length != 1 ? (false, null) : (true, DirectColumn.Real(b[0] / 255f));
                // A PC level multiplier: a uint16 of thousandths (Mutagen's FillBinaryLevelCustom: the number as a float, over 1000)
                if (laid == "U16:/1000")
                    return (b, _) =>
                    {
                        if (b.Length != 2) return (false, null);
                        float v = BinaryPrimitives.ReadUInt16LittleEndian(b);
                        v /= 1000;
                        return (true, DirectColumn.Real(v));
                    };
                if (laid != "U8:speed") return null;
                return (b, _) => b.Length != 1 ? (false, null) : (true, DirectColumn.Real((b[0] - 127) / 127f / 10f));
            }
            if (laid is "ZString" or "LString" or "WString" or "I32String")
            {
                if (f.Kind != "String" || c.Kind is not ("String" or "Bool" or "Int8" or "Int16" or "Int32" or "UInt8" or "UInt16" or "UInt32" or "Float" or "Enum")) return null;
                if (c.Kind != "String" && f.Translated) return null;
                if (f.Translated)
                {
                    if (!Enum.TryParse<Mutagen.Bethesda.Strings.StringsSource>(f.StringsTable, out var table)) return null;
                    var dflt = _default;
                    // A localized plugin's LString is its 4-byte ID; else the stored text, up to its zero
                    if (laid is not ("ZString" or "LString")) return null;
                    return (b, p) => p.TryTextAt(b, "NullTerminate", table, out var t) ? (true, Functions.Display(t) ?? dflt) : (false, null);
                }
                if (laid == "LString") return null;
                // A ZString as Mutagen's ProcessWholeToZString (a fixed four-byte text too); a length-prefixed text (its
                // place holds the length first) as Mutagen's PrependLength readers take it, parsed whole
                int length = laid switch { "WString" => 2, "I32String" => 4, _ => 0 };
                return (b, _) => b.Length < length ? (false, null) : (true, BinaryStringUtility.ProcessWholeToZString(b[length..], nonTranslated));
            }
            if (c.Kind == "RecordType") return laid is "I32" or "U32" ? RecordTypeReader(f) : null;
            if (laid is "Bytes")
            {
                if (f.Kind != "ByteArray" || c.Kind != "ByteArray") return null;
                // (no bytes left for required ones, a pattern swap's Unknown after its last item: Mutagen's empty ones)
                bool emptyIs = !f.Nullable;
                return (b, _) => b.Length == 0 ? (emptyIs ? (true, "0x") : (false, null)) : (true, "0x" + Convert.ToHexString(b));
            }
            // A number, a link, a point, a colour or a guid, at the layout's width
            if (ReadKind(f) is not { } kind) return null;
            Type? et = null;
            if (FlagsRaw.IsEnumKind(kind) && (et = EnumOf(f)) == null) return null;
            var cache = new ConcurrentDictionary<int, RawColumn?>();
            // A layout's signed number into an enum (an NPC race override's Size: I8, -1 is Unknown): at the enum's own
            // width, its sign kept, as Mutagen's hand-written reader casts it
            int wide = et != null && laid is "I8" or "I16" ? System.Runtime.InteropServices.Marshal.SizeOf(Enum.GetUnderlyingType(et!)) : 0;
            // A number stored wider than its field (an alias's AliasID to find a ref near: an int32 Mutagen keeps as a
            // short, checked): its low bytes when the rest only repeat its sign (zeros, unsigned); else Mutagen says it
            int narrow = kind switch { "Int8" or "UInt8" => 1, "Int16" or "UInt16" => 2, "Int32" or "UInt32" => 4, _ => 0 };
            return (b, p) =>
            {
                if (b.Length == 0) return (false, null);
                if (narrow > 0 && b.Length > narrow)
                {
                    byte rest = kind[0] == 'I' && (b[narrow - 1] & 0x80) != 0 ? (byte)0xFF : (byte)0;
                    if (b[narrow..].IndexOfAnyExcept(rest) >= 0) return (false, null);
                    b = b[..narrow];
                }
                if (wide > b.Length && wide <= 8)
                {
                    Span<byte> full = stackalloc byte[wide];
                    b.CopyTo(full);
                    full[b.Length..].Fill((b[^1] & 0x80) != 0 ? (byte)0xFF : (byte)0);
                    var wrc = cache.GetOrAdd(wide, n => RawColumn.Entry(c, f, 0, n, et, kind: kind));
                    return wrc != null && wrc.Read(full, true, p, out var wx) == RawColumn.Outcome.Value ? (true, wx) : (false, null);
                }
                var rc = cache.GetOrAdd(b.Length, n => RawColumn.Entry(c, f, 0, n, et, kind: kind));
                if (rc == null) return (false, null);
                return rc.Read(b, true, p, out var x) == RawColumn.Outcome.Value ? (true, x) : (false, null);
            };
        }

        /// <summary>How a value of this field is read from its bytes as the column's kind; null when it isn't.</summary>
        Func<ReadOnlySpan<byte>, RawPlugin, (bool, object?)>? Reader(FieldDef f)
        {
            var c = _c;
            if (c.Kind == "String")
            {
                if (f.Kind != "String") return null;
                string form = f.TextForm ?? "NullTerminate";
                // (Plain: the subrecord's bytes are the text, with no zero after it: an activity's Configuration, a race's
                // movement type names)
                if (form is not ("NullTerminate" or "PrependLength" or "PrependLengthUShort" or "PrependLengthUInt8" or "PrependLengthWithNullIfContent" or "Plain")) return null;
                if (form == "Plain" && f.Translated) return null;
                if (f.Translated)
                {
                    if (!Enum.TryParse<Mutagen.Bethesda.Strings.StringsSource>(f.StringsTable, out var table)) return null;
                    var dflt = _default;
                    return (b, p) => p.TryTextAt(b, form, table, out var t) ? (true, Functions.Display(t) ?? dflt) : (false, null);
                }
                return (b, _) => (true, RawPlugin.Decode(b, form, Mutagen.Bethesda.Plugins.Meta.GameConstants.Starfield.Encodings.NonTranslated));
            }
            if (c.Kind == "AssetLink")
            {
                // Mutagen's AssetLink keeps the text as read (GivenPath), which DirectColumn gives
                if (f.Kind != "AssetLink" || (f.TextForm ?? "NullTerminate") != "NullTerminate") return null;
                return (b, _) => (true, RawPlugin.Decode(b, "NullTerminate", Mutagen.Bethesda.Plugins.Meta.GameConstants.Starfield.Encodings.NonTranslated));
            }
            if (c.Kind == "RecordType") return f.Size == 4 ? RecordTypeReader(f) : null;
            // A yes/no that is its subrecord's presence (Mutagen's boolAsMarker): there, true
            if (f.Kind == "Bool" && f.Size == 0) return c.Kind == "Bool" ? (b, _) => b.Length == 0 ? (true, 1L) : (false, null) : null;
            if (ReadKind(f) is not { } kind) return null;
            // (a value Mutagen's hand-written code reads isn't read here, but one the record XML places, or an enum it
            // keeps without some bits, which the walk gives whole: an NPC's Flags, a colour record's)
            bool dropped = f.Kind == "Enum" && f.LayoutDroppedBits != null && f.LayoutEncoding == null && f.LayoutBits == null && f.LayoutPresentWhen == null;
            if (f.Custom && f.LayoutOffset == null && !dropped || f.Source != null) return null;
            if (f.LayoutMask is var (mask, shift))
            {
                // A field of bits of one byte (the record XML's layoutBits: a condition's CompareOperator, Flags, UseAliases,
                // GAPS S166): those bits as a number from the lowest of them, without the ones Mutagen keeps elsewhere
                // (layoutDroppedBits), read as a one-byte value of the column's kind
                if (kind is not ("Bool" or "UInt8") && !FlagsRaw.IsEnumKind(kind) || f.LayoutEncoding != null) return null;
                Type? bt = null;
                if (FlagsRaw.IsEnumKind(kind) && (bt = EnumOf(f)) == null) return null;
                int keep = mask & ~(int)f.LayoutDroppedMask;
                if (RawColumn.Entry(c, f, 0, 1, bt, kind: kind) is not { } bits) return null;
                return (b, p) =>
                {
                    if (b.Length != 1) return (false, null);
                    Span<byte> one = stackalloc byte[1];
                    one[0] = (byte)((b[0] & keep) >> shift);
                    return bits.Read(one, true, p, out var x) == RawColumn.Outcome.Value ? (true, x) : (false, null);
                };
            }
            // A float Mutagen keeps as a whole number scaled (the XML's integerType, multiplier and divisor: a dialogue
            // response's ResetHours, a uint16 times 24 over 65535; a music type's DuckingDecibel): Mutagen's own conversion
            // of the stored number, then read as that float's bytes are
            // (a percent stored so is read by the column itself: RawColumn's Form.Percent)
            if (f.IntegerType != null && kind != "Percent")
            {
                if (kind != "Float" || f.LayoutEncoding != null || f.LayoutBits != null || f.LayoutDroppedBits != null) return null;
                int width = f.IntegerType switch { "UInt" => 4, "UShort" => 2, "Byte" => 1, _ => 0 };
                if (!Enum.TryParse<Mutagen.Bethesda.Translations.Binary.FloatIntegerType>(f.IntegerType, out var stored)) return null;
                if (width == 0 || f.Size != width) return null;
                float? mul = null, div = null;
                if (f.Multiplier != null) { if (!float.TryParse(f.Multiplier, NumberStyles.Float, CultureInfo.InvariantCulture, out float m)) return null; mul = m; }
                if (f.Divisor != null) { if (!float.TryParse(f.Divisor, NumberStyles.Float, CultureInfo.InvariantCulture, out float d)) return null; div = d; }
                if (RawColumn.Entry(c, f, 0, 4, null, kind: kind) is not { } asFloat) return null;
                return (b, p) =>
                {
                    if (b.Length != width) return (false, null);
                    float v = Mutagen.Bethesda.Translations.Binary.FloatBinaryTranslation<Mutagen.Bethesda.Plugins.Binary.Streams.MutagenFrame, Mutagen.Bethesda.Plugins.Binary.Streams.MutagenWriter>.Instance.GetFloat(b, stored, multiplier: mul, divisor: div);
                    Span<byte> four = stackalloc byte[4];
                    BinaryPrimitives.WriteSingleLittleEndian(four, v);
                    return asFloat.Read(four, true, p, out var x) == RawColumn.Outcome.Value ? (true, x) : (false, null);
                };
            }
            int size = f.Size ?? (kind == "ByteArray" ? -1 : kind == "Color" ? 4 : kind == "Guid" ? 16 : 0);
            Type? et = null;
            if (FlagsRaw.IsEnumKind(kind) && (et = EnumOf(f)) == null) return null;
            // Bits of the stored value Mutagen keeps elsewhere (the XML's layoutDroppedBits: a traversal's Flags without
            // its bit 2): cleared before the value is read, an enum's only
            uint drop = f.LayoutDroppedBits != null ? f.LayoutDroppedMask : 0;
            if (f.LayoutDroppedBits != null && (drop == 0 || !FlagsRaw.IsEnumKind(kind) || f.LayoutEncoding != null)) return null;
            var cache = new ConcurrentDictionary<int, RawColumn?>();
            return (b, p) =>
            {
                int n = size < 0 ? b.Length : size;
                // (required bytes to the end of their data with none left: Mutagen's empty ones)
                if (b.Length == 0 && size < 0 && kind == "ByteArray" && c.Kind == "ByteArray" && !f.Nullable) return (true, "0x");
                if (n != b.Length || n <= 0) return (false, null);
                var rc = cache.GetOrAdd(n, k => RawColumn.Entry(c, f, 0, k, et, drop, kind: kind));
                if (rc == null) return (false, null);
                return rc.Read(b, true, p, out var x) == RawColumn.Outcome.Value ? (true, x) : (false, null);
            };
        }

        /// <summary>
        /// The kind a value of this field is read as for the column, as DirectColumn.Convert gives Mutagen's value: the
        /// column's own (an enum as the flags column's); in a column of several kinds of entry (an object template
        /// property's Value: a number, a float, a yes/no), the field's own, as DirectColumn.Plain gives it, an enum in a
        /// number column as its number. Null where DirectColumn gives something else (a link column holds only links).
        /// </summary>
        string? ReadKind(FieldDef f)
        {
            var c = _c;
            string? kind = f.Kind == c.Kind || f.Kind == "Enum" && c.Kind is "Flags" or FlagsRaw.Kind ? c.Kind
                : c.Kind is "FormLink" or FlagsRaw.Kind or "String" or "AssetLink" or "RecordType" ? null
                : f.Kind == "Enum" && c.Kind is "Int8" or "Int16" or "Int32" or "Int64" or "UInt8" or "UInt16" or "UInt32" ? FlagsRaw.Kind
                : f.Kind;
            return kind != null && WalkKinds.Contains(kind) && kind is not ("String" or "AssetLink" or "RecordType") ? kind : null;
        }

        /// <summary>The CLR enum of the field: on the class that declares it, or for a generic class's (an object mod
        /// property's Property: Weapon.Property) on the entry's class.</summary>
        Type? EnumOf(FieldDef f)
        {
            var owner = Starfield.GetType("Mutagen.Bethesda.Starfield." + f.Object);
            var pt = owner == null ? null : FindProperty(owner, f.Name)?.PropertyType;
            if (pt != null && System.Nullable.GetUnderlyingType(pt) is { } u) pt = u;
            // A list's entry (an armor addon's ExtraLightLayers, one FLLD each): the list's item type
            if (pt is { IsEnum: false, IsGenericType: true } && f.Path.EndsWith("[*]", StringComparison.Ordinal) && pt.GetGenericArguments() is [var item])
                pt = System.Nullable.GetUnderlyingType(item) ?? item;
            if (pt is not { IsEnum: true }) pt = _leafType;
            return pt is { IsEnum: true } ? pt : null;
        }

        /// <summary>A Mutagen RecordType from its four bytes, as DirectColumn gives it ("null" for none).</summary>
        static Func<ReadOnlySpan<byte>, RawPlugin, (bool, object?)>? RecordTypeReader(FieldDef f) => f.Kind != "RecordType" ? null
            : (b, _) => b.Length == 4 ? (true, DirectColumn.Plain(new Mutagen.Bethesda.Plugins.RecordType(BinaryPrimitives.ReadInt32LittleEndian(b)))) : (false, null);
    }
}
