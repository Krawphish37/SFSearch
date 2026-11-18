// A list view's named columns read from the record's own bytes (GAPS S123, J103), as RawColumns.cs reads a typed view's.
// A narrowed list read (ListReads.cs) used to read each record that holds the list through Mutagen and walk its object
// (DirectList): for Starfield.esm's Cell_Traversals that is the 1,813 cells that hold XTV2, 14.1 million traversals,
// every one made into a Mutagen object to give a few numbers.
//
// A list is read this way only where the schema and Mutagen's layouts say exactly where each entry and each value sits:
//   - a record's flags as a list (Cell_Flags, …_MajorFlags): the typed column's own reader from the bytes (RawColumn),
//     each flag's name as the flags' JSON list holds it;
//   - a list of the record's own (not in a list, a component or a kind of object; through plain objects only), whose
//     entries are of a fixed size and whose named fields sit at fixed places in the entry, stored
//       * one entry to a subrecord (LinkedReferences: XLKR after XLKR), the subrecords one run at the record's level;
//       * packed in one subrecord: filling it (LocationRefTypes in XLRT), after a count in front of them (the schema's
//         CounterLength), or counted by a subrecord of their own (KSIZ before KWDA; WaterCurrentVelocities, XWCN);
//       * as a hand-read layout of Mutagen's lays them out (the package's HandReadLayouts.xml): a Repeat of fixed-size
//         values with one Switch that puts a FormID in or not (the traversals' Define, XTV2, C47);
//   - a grid (Array2d) of fixed-size cells at a fixed place in its subrecord, each row as the JSON list of its cells;
//   - such a list inside each component (Cell_Components_Keywords): the components from BFCB to BFCE, each one's kind
//     by its BFCB text (ComponentKinds), the kind's list read from the component's own subrecords.
// Each value is read by RawColumn's readers (the same kinds, widths and "left out" rule as a typed view's column). A
// record whose bytes don't follow the layout exactly (an entry of another size, a count that doesn't match, the
// subrecord twice, a link through a missing master) is read through Mutagen as before (DirectList), so every answer
// is Mutagen's. The tests world-reads and list-reads hold the bytes against the records' JSON and, with --heavy,
// against Mutagen's object on Starfield.esm, record by record.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SFSearch.Records;
using SFSearch.SchemaGen;

namespace SFSearch.Query;

/// <summary>A list view's named columns of one record read from its own bytes; null from <see cref="For"/> when the
/// list or one of the columns isn't laid out exactly.</summary>
sealed partial class RawList
{
    enum Shape { Flags, PerSubrecord, Packed, Repeat, Grid, Components, ComponentFields, Walk, Planned }
    const uint XXXX = 0x58585858, BFCB = 0x42434642, BFCE = 0x45434642;

    readonly Shape _shape;
    /// <summary>The list's subrecord, and the subrecord that counts it (0: none).</summary>
    uint _sub, _counter;
    /// <summary>Where the list starts in its subrecord; how many bytes its count takes (in front of the entries, or the
    /// counting subrecord's); the size of an entry; a count every list of this kind has (-1: any).</summary>
    int _start, _countBytes, _entrySize, _fixedCount = -1;
    /// <summary>Each named column's reader from an entry's bytes.</summary>
    RawColumn[] _cols = Array.Empty<RawColumn>();
    /// <summary>Flags: the typed column's reader.</summary>
    RawRecordColumns? _flags;
    /// <summary>Repeat: the layout, and each column's value in it.</summary>
    RepeatLayout? _repeat;
    int[] _repeatValue = Array.Empty<int>();
    /// <summary>Grid: its width and height, and how a cell is written.</summary>
    int _width, _height;
    string _cellKind = "";
    /// <summary>Grid of objects (_cellKind "Object"): the cell's class, and its plain fields with their place in a cell.</summary>
    Type? _cellClass;
    (System.Reflection.PropertyInfo Property, string Kind, int At)[]? _cellFields;
    /// <summary>Flags and Grid: the one column (Value) is named; else a row is its place alone.</summary>
    bool _value;
    /// <summary>Components: each kind's reader of its list (null: not laid out exactly; a kind not here has no such list).</summary>
    Dictionary<string, RawList?>? _kinds;
    SchemaModel? _model;
    /// <summary>ComponentFields: for each kind of component, each column's reader (null: the column's value is the kind's
    /// constant), the readers together, and each column's constant.</summary>
    Dictionary<string, KindColumns>? _kindColumns;

    sealed class KindColumns
    {
        public required RawRecordColumns? Reader;
        /// <summary>For each column, its place among the reader's columns, or -1 for a constant.</summary>
        public required int[] At;
        public required object?[] Constant;
    }

    RawList(Shape shape) { _shape = shape; }

    static readonly ConcurrentDictionary<(RecordSchema, string, string), RawList?> Cache = new();

    /// <summary>The byte reader of a list view's named columns (in the order given), or null when one of them, or the
    /// list, is read through Mutagen.</summary>
    public static RawList? For(ViewSpec v, IReadOnlyList<string> columns, RecordSchema schema, ViewSpec typed)
    {
        if (!v.IsList || v.IsUnion || v.Types.Count != 1) return null;
        // (a type that shares its signature with others is read from the records that are its own: ReadPlan.OnlyType)
        if (schema.SignatureOf(v.Types[0]) == null) return null;
        // A column whose shape differs by kind (ColumnSpec.ShapeVaries) is read as the JSON gives it, but one that is a
        // plain value or a list of plain values in every kind (a script property's Data), which the value walk reads
        bool varying = false;
        foreach (string n in columns)
            if (v.ByName.TryGetValue(n, out var sc) && sc.ShapeVaries) { if (!VaryingColumn.Supported(sc)) return null; varying = true; }
        return Cache.GetOrAdd((schema, v.Name, string.Join(",", columns)), _ =>
        {
            // The lists the schema lays out exactly, else those the record's value walk reaches (RawWalkLists.cs), a list
            // in a list in a list too
            // A bulky subrecord's lists by Mutagen's layout compiled (RawPlannedLists.cs) come first: their entries are many
            try
            {
                return varying ? MakeWalk(v, columns, schema) : MakePlanned(v, columns, schema) ?? (v.Lists.Count is 1 or 2 ? Make(v, columns, schema, typed) : null) ?? MakeWalk(v, columns, schema)
                    // Last, a list inside a component that none of these reads (a block height adjustment's grid of
                    // surface blocks): a record with no component of a kind that has the list has no entry, which its
                    // BFCB texts tell; one with such a component goes to Mutagen
                    ?? (v.Lists.Count == 2 ? MakeComponents(v, columns, schema, v.Types[0], unreadKinds: true) : null);
            }
            catch (Exception e) when (e is FormatException or OverflowException or InvalidOperationException) { return null; }
        });
    }

    static readonly Regex AlwaysItems = new(@"always (\d+) items", RegexOptions.Compiled);
    static readonly Regex AlwaysGrid = new(@"always (\d+) x (\d+) items", RegexOptions.Compiled);
    static readonly Regex CountedBy = new(@"counted by ([A-Z0-9_]{4})", RegexOptions.Compiled);

    static uint Sig(string s) => BinaryPrimitives.ReadUInt32LittleEndian(System.Text.Encoding.ASCII.GetBytes(s));

    static RawList? Make(ViewSpec v, IReadOnlyList<string> columns, RecordSchema schema, ViewSpec typed)
    {
        string type = v.Types[0];
        var level = v.Lists[0];
        var model = schema.Model;
        // A record's flags as a list: the typed column's reader, each flag's name (a field whose XML default isn't zero
        // is left out by the serializer at that default, which a list of names can't give back)
        if (level.Kind == "Flags")
        {
            var col = typed.Columns.FirstOrDefault(c => c.Kind == "Flags" && c.RelPath == level.Path);
            if (col == null || col.Row is not { } fr || fr.Def.Default != null || columns.Any(c => !c.Equals("Value", StringComparison.OrdinalIgnoreCase))) return null;
            if (RawColumn.For(typed, col, schema) is not { } rc) return null;
            return new RawList(Shape.Flags) { _flags = new RawRecordColumns(new[] { rc }), _value = columns.Count == 1 };
        }
        if (v.Lists.Count == 2) return MakeComponents(v, columns, schema, type);
        if (level.Path == "Components" && level.Kind == "List") return MakeComponentFields(v, columns, schema, type);
        if (level.Kind is not ("List" or "Array" or "Array2D") || level.Path.Contains('[') || level.Path.Contains('{')) return null;
        var rows = RowsOf(schema, type);
        var list = rows.Where(r => r.PlainPath == level.Path && r.Def.Kind == level.Kind).ToList();
        if (list.Count != 1) return null;
        var l = list[0];
        var ld = l.Def;
        if (l.InList || ld.Marker != null || ld.EndMarker != null || ld.Source != null || ld.ReadOnly) return null;
        // The objects on the list's way: none read by Mutagen's own code, none with a marker
        for (var up = l.Parent; up != null; up = up.Parent)
        {
            if (up.Def.Marker != null || up.Def.EndMarker != null || up.Def.Custom || up.InList || up.Def.Kind != "Object") return null;
            if (up.Def.RefObject is { } ro && (model.HandReadOf(ro) != null || schema.IsPolymorphic(ro))) return null;
        }
        var entries = rows.Where(r => r.PlainPath == l.PlainPath + "[*]").ToList();
        if (entries.Count != 1) return null;
        var entry = entries[0];
        string? sub = l.Subrecord ?? entry.Subrecord;
        if (sub is not { Length: 4 }) return null;

        RawList made;
        if (level.Kind == "Array2D")
        {
            // A grid: its cells of one plain kind, row by row, at a fixed place in its subrecord, nothing after it
            if (l.Subrecord != sub || ld.Size is not int total || ld.Note == null || AlwaysGrid.Match(ld.Note) is not { Success: true } g) return null;
            int w = int.Parse(g.Groups[1].Value, CultureInfo.InvariantCulture), h = int.Parse(g.Groups[2].Value, CultureInfo.InvariantCulture);
            if (entry.Def.Size is not int cell || cell <= 0 || w * h * cell != total || entry.Def.RefObject != null || entry.Def.Kind is not ("UInt8" or "Int8" or "UInt16" or "Int16" or "UInt32" or "Int32" or "FormLink")) return null;
            if (entry.Def.Kind == "FormLink" && cell != 4) return null;
            int start = l.Offset ?? (ld.Subrecord == sub ? 0 : -1);
            if (start < 0 || !Owns(schema, rows, l, sub, start, allowBefore: true, allowCustom: false)) return null;
            if (columns.Count > 1 || columns.Count == 1 && !(v.ByName.TryGetValue(columns[0], out var vc) && vc.Kind == "Array2DRow")) return null;
            made = new RawList(Shape.Grid) { _sub = Sig(sub), _start = start, _width = w, _height = h, _entrySize = cell, _cellKind = entry.Def.Kind, _value = columns.Count == 1 };
            return made;
        }
        if (ld.Custom)
        {
            // Read by Mutagen's hand-written code: only as a hand-read layout of Mutagen's lays it out
            if (RepeatLayout.Of(model, schema.SignatureOf(type)!, sub, ld.Name) is not { } layout) return null;
            if (!Owns(schema, rows, l, sub, 0, allowBefore: false, allowCustom: true)) return null;
            var at = new int[columns.Count];
            var readers = new RawColumn[columns.Count];
            for (int i = 0; i < columns.Count; i++)
            {
                if (!v.ByName.TryGetValue(columns[i], out var c) || !Plain(c) || c.Row is not { } cr || cr.PlainPath != l.PlainPath + "[*]." + c.RelPath) return null;
                int k = layout.Values.FindIndex(x => x.Field == c.RelPath);
                if (k < 0) return null;
                var val = layout.Values[k];
                Type? et = FlagsRaw.IsEnumKind(c.Kind) ? ClrTypeAt(type, level.Path.Split('.').Concat(c.RelPath!.Split('.'))) : null;
                if (val.Kind == "FormID")
                {
                    if (c.Kind != "FormLink") return null;
                    readers[i] = RawColumn.Entry(c, cr.Def, 0, 4, null)!;
                }
                else if (RawColumn.Entry(c, cr.Def, 0, val.Size, et, val.Drop) is { } rc && KindFits(val.Kind, c.Kind)) readers[i] = rc;
                else return null;
                if (readers[i] == null) return null;
                at[i] = k;
            }
            return new RawList(Shape.Repeat) { _sub = Sig(sub), _repeat = layout, _repeatValue = at, _cols = readers };
        }
        if (ld.Kind is not ("List" or "Array")) return null;
        var getter = typeof(Mutagen.Bethesda.Starfield.StarfieldMod).Assembly.GetType($"Mutagen.Bethesda.Starfield.I{type}Getter");
        return MakeEntries(schema, rows, l, entry, sub, v, columns, path => ClrTypeAt(type, level.Path.Split('.').Concat(path)), components: false, getter == null ? null : ElementType(getter, level.Path));
    }

    /// <summary>
    /// A list of fixed-size entries, one to a subrecord or packed in one, in an object whose fields are
    /// <paramref name="rows"/> (a record type's, or a component kind's: <paramref name="components"/>). Each column is the
    /// field at its RelPath in an entry, looked up in those rows. <paramref name="clr"/>: the CLR enum at a path in an entry.
    /// </summary>
    static RawList? MakeEntries(RecordSchema schema, List<FieldRow> rows, FieldRow l, FieldRow entry, string sub, ViewSpec v, IReadOnlyList<string> columns, Func<IEnumerable<string>, Type?> clr, bool components, Type? entryType)
    {
        var model = schema.Model;
        var ld = l.Def;
        // The entry: a plain value, or an object of one kind whose named fields sit at fixed places in it
        int size;
        bool scalar = entry.Def.RefObject == null;
        if (scalar)
        {
            if (entry.Def.Size is not int es || es <= 0 || entry.Def.Custom || entry.Def.Kind is "List" or "Array" or "Array2D" or "Object" or "String") return null;
            size = es;
        }
        else
        {
            string ro = entry.Def.RefObject!;
            if (entry.Def.Kind != "Object" || schema.IsPolymorphic(ro) || model.HandReadOf(ro) != null || entry.Def.Marker != null || entry.Def.EndMarker != null || model.MarkerOf(ro) != null || model.EndMarkerOf(ro) != null) return null;
            if (EntrySize(rows, entry, model) is not int es) return null;
            size = es;
            if (entry.ChildBase is not (null or 0)) return null;
        }
        var cols = new RawColumn[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            if (!v.ByName.TryGetValue(columns[i], out var c) || !Plain(c, components)) return null;
            string at = c.RelPath!.Length == 0 ? l.PlainPath + "[*]" : l.PlainPath + "[*]." + c.RelPath;
            var found = rows.Where(r => r.PlainPath == at).ToList();
            // An entry whose class has no such property (another kind's list of the same name): DirectColumn's value for
            // a field that isn't there, the view's default where it guards one, else NULL
            if (found.Count == 0 && entryType != null && c.RelPath.Length > 0 && !c.RelPath.Contains('.') && !HasProperty(entryType, c.RelPath))
            {
                bool guarded = !c.Nullable && c.DefaultSql != null;
                object? absent = null;
                if (guarded && c.Kind != "FormLink" && !DirectColumn.TryLiteral(c.DefaultSql!, out absent)) return null;
                cols[i] = RawColumn.Constant(c.Name, absent);
                continue;
            }
            if (found.Count != 1) return null;
            var cr = found[0];
            var def = cr.Def;
            // The column's kind is the field's here (a flags enum's column is Flags)
            if (!(def.Kind == c.Kind || def.Kind == "Enum" && c.Kind is "Flags" or FlagsRaw.Kind)) return null;
            int offset, fsize;
            if (c.RelPath.Length == 0)
            {
                if (!scalar || cr != entry) return null;
                offset = 0; fsize = size;
            }
            else
            {
                if (scalar || cr.Offset is not int o || def.Size is not int fs) return null;
                if (cr.Subrecord != entry.Subrecord || def.Custom || def.Source != null || def.ReadOnly || def.Marker != null || def.EndMarker != null || def.LayoutEncoding != null) return null;
                // Every object between the entry and the field: plain, of one kind, always there
                for (var up = cr.Parent; up != null && up != entry; up = up.Parent)
                    if (up.Def.Nullable || up.Def.Custom || up.Def.RefObject is { } uro && (schema.IsPolymorphic(uro) || model.HandReadOf(uro) != null)) return null;
                offset = o; fsize = fs;
            }
            if (offset < 0 || offset + fsize > size) return null;
            Type? et = FlagsRaw.IsEnumKind(c.Kind) ? clr(c.RelPath.Length == 0 ? Array.Empty<string>() : c.RelPath.Split('.')) : null;
            if (RawColumn.Entry(c, def, offset, fsize, et) is not { } rc) return null;
            cols[i] = rc;
        }
        if (l.Subrecord == null)
        {
            // One entry to a subrecord of its own: an object whose fields are that subrecord alone, or a plain value
            if (entry.Subrecord != sub) return null;
            if (!scalar && rows.Any(r => r.PlainPath.StartsWith(l.PlainPath + "[*].", StringComparison.Ordinal) && r.Def.Kind is not ("Break" or "Marker") && r.Subrecord != sub)) return null;
            if (!Owns(schema, rows, l, sub, 0, allowBefore: false, allowCustom: false)) return null;
            return new RawList(Shape.PerSubrecord) { _sub = Sig(sub), _entrySize = size, _cols = cols };
        }
        // Packed in one subrecord, after anything of the object it sits in, nothing after it
        if (l.Subrecord != sub || entry.Subrecord != sub) return null;
        // An entry with a break (a field Mutagen reads only when the bytes go on) can't be packed by a fixed size
        if (!scalar && rows.Any(r => r.PlainPath.StartsWith(l.PlainPath + "[*].", StringComparison.Ordinal) && r.Def.Kind == "Break")) return null;
        int listStart = l.Offset ?? (ld.Subrecord == sub ? 0 : -1);
        if (listStart < 0) return null;
        var packed = new RawList(Shape.Packed) { _sub = Sig(sub), _start = listStart, _entrySize = size, _cols = cols };
        if (ld.Note != null && CountedBy.Match(ld.Note) is { Success: true } cb)
        {
            packed._counter = Sig(cb.Groups[1].Value);
            packed._countBytes = ld.CounterLength ?? 4;
            // Nothing else of the record in the counting subrecord
            if (rows.Any(r => AtRecordLevel(r, model) && (r.Subrecord == cb.Groups[1].Value || r.Def.CustomSubrecordList.Contains(cb.Groups[1].Value)))) return null;
        }
        else if (ld.CounterLength is int cl) packed._countBytes = cl;
        if (ld.Note != null && AlwaysItems.Match(ld.Note) is { Success: true } fixedCount) packed._fixedCount = int.Parse(fixedCount.Groups[1].Value, CultureInfo.InvariantCulture);
        if (packed._countBytes is not (0 or 1 or 2 or 4)) return null;
        if (!Owns(schema, rows, l, sub, listStart, allowBefore: true, allowCustom: false)) return null;
        return packed;
    }

    /// <summary>
    /// A grid whose cells are objects of plain fields, alone in its subrecord (a block height adjustment's surface
    /// blocks: 16 x 16 cells of two floats in DAT2). Each row is written as the JSON has it: the serializer writes a cell
    /// through the cell class's own generated writer, so a cell is made of its class from the bytes and written by that
    /// writer, as DirectList writes Mutagen's (GridRows). Null when the cell isn't laid out so.
    /// </summary>
    static RawList? MakeObjectGrid(RecordSchema schema, List<FieldRow> krows, FieldRow l, FieldRow entry, System.Reflection.Assembly asm, ViewSpec v, IReadOnlyList<string> columns)
    {
        var ld = l.Def;
        if (ld.Custom || ld.Marker != null || ld.EndMarker != null || ld.Source != null || l.Subrecord is not { Length: 4 } sub || (l.Offset ?? 0) != 0) return null;
        if (ld.Size is not int total || ld.Note == null || AlwaysGrid.Match(ld.Note) is not { Success: true } g) return null;
        int w = int.Parse(g.Groups[1].Value, CultureInfo.InvariantCulture), h = int.Parse(g.Groups[2].Value, CultureInfo.InvariantCulture);
        if (w <= 0 || h <= 0 || total % (w * h) != 0) return null;
        int cell = total / (w * h);
        // Nothing else of the kind in the grid's subrecord
        if (krows.Any(r => r.Subrecord == sub && r != l && r.PlainPath != l.PlainPath && !r.PlainPath.StartsWith(l.PlainPath + "[", StringComparison.Ordinal))) return null;
        if (entry.Def.Kind != "Object" || entry.Def.RefObject is not { } ro || schema.IsPolymorphic(ro) || schema.Model.HandReadOf(ro) != null) return null;
        var cls = asm.GetType("Mutagen.Bethesda.Starfield." + ro);
        if (cls == null || cls.GetConstructor(Type.EmptyTypes) == null || !typeof(Loqui.ILoquiObject).IsAssignableFrom(cls)) return null;
        var fields = new List<(System.Reflection.PropertyInfo, string, int)>();
        int at = 0;
        foreach (var (f, _) in schema.Model.AllFields(ro))
        {
            if (f.Path != f.Name) return null; // a field inside a field: not a cell of plain fields
            if (f.Source != null || f.Kind is "Break" or "Marker") continue;
            var want = f.Kind switch { "Float" => typeof(float), "Int32" => typeof(int), "UInt32" => typeof(uint), "Int16" => typeof(short), "UInt16" => typeof(ushort), "Int8" => typeof(sbyte), "UInt8" => typeof(byte), _ => null };
            if (want == null || f.Custom || f.Subrecord != null || f.Size is not int size || size != System.Runtime.InteropServices.Marshal.SizeOf(want) || (f.Offset ?? at) != at) return null;
            if (f.IntegerType != null || f.Multiplier != null || f.Divisor != null) return null;
            var p = cls.GetProperty(f.Name);
            if (p == null || p.PropertyType != want || !p.CanWrite) return null;
            fields.Add((p, f.Kind, at));
            at += size;
        }
        if (fields.Count == 0 || at != cell) return null;
        if (columns.Count > 1 || columns.Count == 1 && !(v.ByName.TryGetValue(columns[0], out var vc) && vc.Kind == "Array2DRow")) return null;
        return new RawList(Shape.Grid) { _sub = Sig(sub), _start = 0, _width = w, _height = h, _entrySize = cell, _cellKind = "Object", _cellClass = cls, _cellFields = fields.ToArray(), _value = columns.Count == 1 };
    }

    /// <summary>
    /// A list inside each component (Cell_Components_Keywords): the record's components one after another (each from its
    /// BFCB to its BFCE, as Mutagen's AComponent reader takes them), each one's kind by its BFCB text (ComponentKinds),
    /// and the kind's own list read from the component's subrecords as a record's list is read from the record's. A kind
    /// without the list gives no row (as json_each gives none); a kind whose list isn't laid out exactly, or a component
    /// Mutagen reads no kind of, sends the record to Mutagen.
    /// </summary>
    /// <param name="unreadKinds">Made also when no kind's list is laid out (and for a grid): then every component of a
    /// kind that has the list sends its record to Mutagen, and a record without one has no entry.</param>
    static RawList? MakeComponents(ViewSpec v, IReadOnlyList<string> columns, RecordSchema schema, string type, bool unreadKinds = false)
    {
        var model = schema.Model;
        var outer = v.Lists[0];
        var inner = v.Lists[1];
        if (outer.Path != "Components" || outer.Kind != "List" || inner.Kind is not ("List" or "Array" or "Array2D") || inner.Path.Contains('[') || inner.Path.Contains('{')) return null;
        if (inner.Kind == "Array2D" && !unreadKinds) return null;
        var rows = RowsOf(schema, type);
        var comps = rows.Where(r => r.PlainPath == "Components" && r.Def.Kind == "List").ToList();
        if (comps.Count != 1 || comps[0].Parent != null || comps[0].Def.Subrecord != null) return null;
        if (model.EndMarkerOf(Selectors.Component) != "BFCE") return null;
        // (a list of that name whose entries aren't components, a generic base form template's Components of STRV names,
        // isn't read as the blocks between BFCB and BFCE)
        if (!rows.Any(r => r.PlainPath == "Components[*]" && r.Def.Kind == "Object" && r.Def.RefObject == Selectors.Component)) return null;
        var kinds = new Dictionary<string, RawList?>(StringComparer.Ordinal);
        var asm = typeof(Mutagen.Bethesda.Starfield.StarfieldMod).Assembly;
        foreach (string kind in model.Variants(Selectors.Component))
        {
            if (model.Objects[kind].Abstract) continue;
            var krows = RowsOf(schema, kind);
            var list = krows.Where(r => r.PlainPath == inner.Path).ToList();
            // A kind without the list: no rows, as Mutagen's object of it has no such property
            if (list.Count == 0)
            {
                if (KindHas(asm, kind, inner.Path)) kinds[kind] = null;
                continue;
            }
            var l = list[0];
            var entries = krows.Where(r => r.PlainPath == l.PlainPath + "[*]").ToList();
            RawList? made = null;
            if (inner.Kind == "Array2D")
            {
                // A grid in a component (a block height adjustment's 16 x 16 surface blocks): of objects of plain fields
                if (list.Count == 1 && l.Def.Kind == "Array2D" && entries.Count == 1 && !l.InList && ParentChainPlain(schema, l))
                    made = MakeObjectGrid(schema, krows, l, entries[0], asm, v, columns);
            }
            else if (list.Count == 1 && l.Def.Kind == inner.Kind && entries.Count == 1 && !l.InList && l.Def.Marker == null && l.Def.EndMarker == null && !l.Def.Custom && l.Def.Source == null
                && ParentChainPlain(schema, l) && (l.Subrecord ?? entries[0].Subrecord) is { Length: 4 } sub)
            {
                var cls = asm.GetType("Mutagen.Bethesda.Starfield." + kind);
                made = MakeEntries(schema, krows, l, entries[0], sub, v, columns, path => cls == null ? null : ClrTypeFrom(cls, inner.Path.Split('.').Concat(path)), components: true, cls == null ? null : ElementType(cls, inner.Path));
            }
            kinds[kind] = made;
        }
        if (kinds.Count == 0 || !unreadKinds && kinds.Values.All(x => x == null)) return null;
        return new RawList(Shape.Components) { _kinds = kinds, _model = model };
    }

    /// <summary>
    /// The components' own fields (Cell_Components: one row per component, each column a field of the component, most
    /// of one kind): each component from its BFCB to its BFCE, its kind by its BFCB text, and each column read from the
    /// component's subrecords as a record's own column is read from the record's (RawColumn, the kind's schema rows), or
    /// what the view gives for that kind without reading: NULL where the column's kind guard doesn't take it, the
    /// serializer's absent value where the kind has no such property, the kind's MutagenObjectType for ObjectType. Null
    /// when a kind that has the field doesn't have it laid out exactly.
    /// </summary>
    static RawList? MakeComponentFields(ViewSpec v, IReadOnlyList<string> columns, RecordSchema schema, string type)
    {
        var model = schema.Model;
        var rows = RowsOf(schema, type);
        var comps = rows.Where(r => r.PlainPath == "Components" && r.Def.Kind == "List").ToList();
        if (comps.Count != 1 || comps[0].Parent != null || comps[0].Def.Subrecord != null) return null;
        if (model.EndMarkerOf(Selectors.Component) != "BFCE") return null;
        // (a list of that name whose entries aren't components, a generic base form template's Components of STRV names,
        // isn't read as the blocks between BFCB and BFCE)
        if (!rows.Any(r => r.PlainPath == "Components[*]" && r.Def.Kind == "Object" && r.Def.RefObject == Selectors.Component)) return null;
        var asm = typeof(Mutagen.Bethesda.Starfield.StarfieldMod).Assembly;
        var specs = new List<ColumnSpec>();
        foreach (string name in columns)
        {
            if (!v.ByName.TryGetValue(name, out var c) || c.RelPath == null && c.Kind != "ObjectType") return null;
            if (c.Kind == "ObjectType" ? c.ObjectPath != "" : c.VariantGuards.Any(g => g.ObjPath != "")) return null;
            specs.Add(c);
        }
        var plans = new Dictionary<string, KindColumns>(StringComparer.Ordinal);
        foreach (string kind in model.Variants(Selectors.Component))
        {
            if (model.Objects[kind].Abstract) continue;
            // The kind a BFCB text that names none the schema has picks (UnknownComponent, its selector "*"): Mutagen's
            // hand-written code reads it whole (its TypeName, its Data). A column this can't give for it leaves the kind
            // without a plan, so a record that holds such a component goes to Mutagen; the other kinds are read
            bool other = model.Objects[kind].SelectorValue == "*", skip = false;
            // A kind with no class of that name: its components go to Mutagen
            var cls = asm.GetType("Mutagen.Bethesda.Starfield." + kind);
            if (cls == null) continue;
            var krows = RowsOf(schema, kind);
            var at = new int[specs.Count];
            var constant = new object?[specs.Count];
            var readers = new List<RawColumn>();
            for (int i = 0; i < specs.Count; i++)
            {
                var c = specs[i];
                at[i] = -1;
                if (c.Kind == "ObjectType") { constant[i] = DirectColumn.ObjectTypeName(cls); continue; }
                // DirectColumn's and the view's rules: a guard applies to a field with a default (the view's CASE); a
                // kind without the property gives the serializer's absent value (the view's coalesce, or NULL)
                bool guarded = !c.Nullable && c.DefaultSql != null;
                if (guarded && !c.FromBase && !c.VariantGuards.Any(g => g.Variants.Contains(kind))) continue;
                object? absent = guarded && c.Kind != "FormLink" && DirectColumn.TryLiteral(c.DefaultSql!, out var lit) ? lit : null;
                if (guarded && c.Kind != "FormLink" && absent == null) { if (other) { skip = true; break; } return null; }
                if (!KindHas(asm, kind, c.RelPath!))
                {
                    // A field below an object the kind has: not a constant this can give
                    if (c.RelPath!.Contains('.') && KindHas(asm, kind, c.RelPath[..c.RelPath.IndexOf('.')])) { if (other) { skip = true; break; } return null; }
                    constant[i] = absent;
                    continue;
                }
                var found = krows.Where(r => r.PlainPath == c.RelPath).ToList();
                if (found.Count != 1) { if (other) { skip = true; break; } return null; }
                var one = new ColumnSpec
                {
                    Name = c.Name, Expr = c.Expr, Kind = c.Kind, SchemaPath = c.RelPath!, IsLink = c.IsLink,
                    DefaultSql = c.DefaultSql, RelPath = c.RelPath, Nullable = c.Nullable, NullGuard = c.NullGuard, FromBase = true, Row = found[0],
                };
                if (RawColumn.ForObject(kind, one, schema) is not { } rc) { if (other) { skip = true; break; } return null; }
                at[i] = readers.Count;
                readers.Add(rc);
            }
            if (skip) continue;
            plans[kind] = new KindColumns { Reader = readers.Count == 0 ? null : new RawRecordColumns(readers.ToArray()), At = at, Constant = constant };
        }
        return new RawList(Shape.ComponentFields) { _kindColumns = plans, _model = model };
    }

    /// <summary>The CLR type of a list's entries at a path from a type (its IEnumerable's element type).</summary>
    static Type? ElementType(Type start, string path)
    {
        Type? t = start;
        foreach (string name in path.Split('.'))
        {
            if (t == null) return null;
            t = FindProperty(t, name)?.PropertyType;
        }
        return t != null && t != typeof(string) && t.GetInterfaces().Prepend(t).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)) is { } en
            ? en.GetGenericArguments()[0] : null;
    }

    static bool HasProperty(Type t, string name) => FindProperty(t, name) != null;

    static PropertyInfo? FindProperty(Type t, string name)
    {
        foreach (var i in t.GetInterfaces().Prepend(t))
        {
            PropertyInfo? p;
            try { p = i.GetProperty(name, BindingFlags.Public | BindingFlags.Instance); }
            catch (AmbiguousMatchException) { p = i.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == name); }
            if (p != null) return p;
        }
        return null;
    }

    /// <summary>The objects on a list's way in its object: none read by Mutagen's own code, none with a marker, one kind each.</summary>
    static bool ParentChainPlain(RecordSchema schema, FieldRow l)
    {
        for (var up = l.Parent; up != null; up = up.Parent)
        {
            if (up.Def.Marker != null || up.Def.EndMarker != null || up.Def.Custom || up.InList || up.Def.Kind != "Object") return false;
            if (up.Def.RefObject is { } ro && (schema.Model.HandReadOf(ro) != null || schema.IsPolymorphic(ro))) return false;
        }
        return true;
    }

    /// <summary>Mutagen's class of a component kind has a property at the path (one the schema has no row for would
    /// give rows the bytes can't).</summary>
    static bool KindHas(Assembly asm, string kind, string path)
    {
        Type? t = asm.GetType("Mutagen.Bethesda.Starfield." + kind);
        foreach (string name in path.Split('.'))
        {
            if (t == null) return false;
            t = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.PropertyType;
        }
        return t != null;
    }

    /// <summary>A column read the plain way: one field of one kind of object, no kind guard, not translated. Inside a
    /// component's list (<paramref name="components"/>) the component's kind sits before the list, never after it.</summary>
    static bool Plain(ColumnSpec c, bool components = false) => c.RelPath != null && c.FromBase && c.VariantGuards.Count == 0 && c.NullGuard == null
        && c.Kind is not ("ObjectType" or "Array2DRow" or "Json" or "String" or "AssetLink")
        && (components ? c.SchemaPath.LastIndexOf('{') < c.SchemaPath.LastIndexOf('[') : !c.SchemaPath.Contains('{'));

    /// <summary>A hand-read layout's value kind fits the column's kind.</summary>
    static bool KindFits(string layout, string column) => (layout, column) switch
    {
        ("I32", "Int32") or ("U32", "UInt32") or ("I16", "Int16") or ("U16", "UInt16") or ("I8", "Int8") or ("U8", "UInt8") => true,
        ("F32", "Float") or ("F16", "Half") or ("P3F32", "P3Float") or ("P3I16", "P3Int16") or ("U64", "UInt64") => true,
        ("U8" or "U16" or "U32" or "I32", "Enum" or "Flags" or FlagsRaw.Kind) => true,
        _ => false,
    };

    /// <summary>The fixed size of an entry: its object's own, else its fields laid end to end from 0 with nothing
    /// missing. Null when it can't be told.</summary>
    static int? EntrySize(List<FieldRow> rows, FieldRow entry, SchemaModel model)
    {
        if (entry.Def.RefObject is { } ro && model.Objects.TryGetValue(ro, out var od) && od.FixedSize is int fs && fs > 0) return fs;
        var fields = rows.Where(r => r.Parent == entry && r.Def.Kind is not ("Break" or "Marker") && r.Def.Source == null).ToList();
        if (fields.Count == 0 || fields.Any(f => f.Offset == null || f.Def.Size == null || f.Def.Kind is "List" or "Array" or "Array2D" or "Object" or "Gendered")) return null;
        int end = 0;
        foreach (var f in fields.OrderBy(f => f.Offset))
        {
            if (f.Offset != end) return null;
            end += f.Def.Size!.Value;
        }
        return end;
    }

    /// <summary>
    /// The list's subrecord is the list's alone at the record's level: every other field of the type that uses it is the
    /// list's own (its entries), an object the list sits in, a field of that object before the list
    /// (<paramref name="allowBefore"/>: ending at or before <paramref name="start"/>), or, for a list a hand-read layout
    /// lays out, a field the same hand-written code fills (<paramref name="allowCustom"/>: the fluff after the traversals).
    /// A record nested in this one, and a component (between BFCB and BFCE, which the reading steps over), don't count.
    /// </summary>
    static bool Owns(RecordSchema schema, List<FieldRow> rows, FieldRow l, string sub, int start, bool allowBefore, bool allowCustom)
    {
        var model = schema.Model;
        foreach (var r in rows)
        {
            bool uses = r.Subrecord == sub || r.Def.CustomSubrecordList.Contains(sub) || r.Def.LayoutSubrecord == sub;
            if (!uses) continue;
            if (!AtRecordLevel(r, model)) continue;
            if (r == l || r.PlainPath.StartsWith(l.PlainPath + "[", StringComparison.Ordinal)) continue;
            if (r.Def.Kind is "Object" && l.PlainPath.StartsWith(r.PlainPath + ".", StringComparison.Ordinal)) continue;
            if (allowCustom && (r.Def.Kind == "CustomLogic" || r.Def.Custom) && !r.InList && !r.Path.Contains('[')) continue;
            if (allowBefore && !r.InList && !r.Path.Contains('[') && !r.Path.Contains('{') && r.Offset is int o && r.Def.Size is int s && o + s <= start
                && !r.Def.Custom && r.Def.Kind != "CustomLogic" && r.Def.Marker == null && ParentOf(r.PlainPath) == ParentOf(l.PlainPath)) continue;
            if (r.Def.Kind is "Break" or "Marker" && ParentOf(r.PlainPath) == ParentOf(l.PlainPath) && r.Offset is int bo && bo <= start) continue;
            return false;
        }
        return true;
    }

    /// <summary>The field is the record's own, at its level: not in a record nested in it, not in a component (whose
    /// subrecords sit between BFCB and BFCE, which the reading steps over).</summary>
    static bool AtRecordLevel(FieldRow r, SchemaModel model)
    {
        var stack = r.Stack.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1);
        return !stack.Any(o => model.Objects.TryGetValue(o, out var od) && od.ObjType == "Record" || model.EndMarkerOf(o) == "BFCE");
    }

    static string ParentOf(string path) { int dot = path.LastIndexOf('.'); return dot < 0 ? "" : path[..dot]; }

    static readonly ConcurrentDictionary<(RecordSchema, string), List<FieldRow>> RowCache = new();

    static List<FieldRow> RowsOf(RecordSchema schema, string type) => RowCache.GetOrAdd((schema, type), k => k.Item1.Model.Expand(k.Item2).ToList());

    /// <summary>The CLR type at a path of a record type's getter interface, a list's entries by their element type.</summary>
    static Type? ClrTypeAt(string recordType, IEnumerable<string> path) =>
        typeof(Mutagen.Bethesda.Starfield.StarfieldMod).Assembly.GetType($"Mutagen.Bethesda.Starfield.I{recordType}Getter") is { } t ? ClrTypeFrom(t, path) : null;

    /// <summary>The CLR enum at a path from a type, a list's entries by their element type.</summary>
    static Type? ClrTypeFrom(Type start, IEnumerable<string> path)
    {
        Type? t = start;
        foreach (string name in path)
        {
            if (t == null) return null;
            if (t != typeof(string) && t.IsGenericType && t.GetInterfaces().Prepend(t).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)) is { } en)
                t = en.GetGenericArguments()[0];
            PropertyInfo? p = null;
            foreach (var i in t.GetInterfaces().Prepend(t))
            {
                try { p = i.GetProperty(name, BindingFlags.Public | BindingFlags.Instance); }
                catch (AmbiguousMatchException) { p = i.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == name); }
                if (p != null) break;
            }
            t = p?.PropertyType;
            if (t != null && Nullable.GetUnderlyingType(t) is { } u) t = u;
        }
        if (t != null && t != typeof(string) && t.IsGenericType && t.GetInterfaces().Prepend(t).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)) is { } last)
            t = last.GetGenericArguments()[0];
        if (t != null && Nullable.GetUnderlyingType(t) is { } u2) t = u2;
        return t is { IsEnum: true } ? t : null;
    }

    // ---------------------------------------------------------------------------------------------- Reading
    /// <summary>The record's rows (the list place, then the columns) from its header and plain data; false when the
    /// bytes don't follow the layout exactly (the record is then read through Mutagen).</summary>
    public bool Read(ReadOnlySpan<byte> head, ReadOnlySpan<byte> data, RawPlugin plugin, List<object?[]> rows) => Read(head, data, plugin, rows, out _);

    /// <summary><see cref="Read(ReadOnlySpan{byte}, ReadOnlySpan{byte}, RawPlugin, List{object?[]})"/>, saying why when it is
    /// the value walk that leaves the record to Mutagen.</summary>
    internal bool Read(ReadOnlySpan<byte> head, ReadOnlySpan<byte> data, RawPlugin plugin, List<object?[]> rows, out WalkLeft left)
    {
        left = default;
        if (_shape is Shape.Components or Shape.ComponentFields) return ReadComponents(data, plugin, rows);
        if (_shape == Shape.Walk) return ReadWalk(head, data, plugin, rows, out left);
        if (_shape == Shape.Planned) return ReadPlanned(head, data, plugin, rows);
        if (_shape == Shape.Flags)
        {
            var one = new object?[1];
            if (!_flags!.Read(head, data, plugin, one)) return false;
            if (one[0] is not string text) return one[0] == null;
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            long i = 0;
            foreach (var name in doc.RootElement.EnumerateArray()) rows.Add(_value ? new object?[] { i++, name.GetString() } : new object?[] { i++ });
            return true;
        }
        // The list's subrecords (and its counter) at the record's own level: where, how long, their place in the record
        int first = -1, firstLen = 0, count = 0, lastSeq = -1, counterAt = -1, counterLen = 0, counters = 0;
        bool run = true;
        List<(int Start, int Length)>? each = _shape == Shape.PerSubrecord ? new() : null;
        int p = 0, depth = 0, seq = 0; uint big = 0; bool hasBig = false;
        while (p + 6 <= data.Length)
        {
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(data[p..]);
            int n = BinaryPrimitives.ReadUInt16LittleEndian(data[(p + 4)..]);
            p += 6;
            if (sig == XXXX && n == 4 && p + 4 <= data.Length) { big = BinaryPrimitives.ReadUInt32LittleEndian(data[p..]); hasBig = true; p += 4; continue; }
            long len = hasBig ? big : n; hasBig = false;
            if (p + len > data.Length) return false;
            if (sig == BFCB) depth++;
            else if (sig == BFCE) { if (depth > 0) depth--; }
            else if (depth == 0)
            {
                if (sig == _sub)
                {
                    if (count == 0) { first = p; firstLen = (int)len; }
                    else if (lastSeq != seq - 1) run = false;
                    lastSeq = seq;
                    count++;
                    each?.Add((p, (int)len));
                }
                else if (sig == _counter && _counter != 0) { counterAt = p; counterLen = (int)len; counters++; }
            }
            seq++;
            p += (int)len;
        }
        if (p != data.Length) return false;
        switch (_shape)
        {
            case Shape.PerSubrecord:
            {
                // One run: Mutagen's reader takes the list's subrecords as they come, one after another
                if (!run) return false;
                long idx = 0;
                foreach (var (start, length) in each!)
                {
                    if (length != _entrySize) return false;
                    if (!Row(data.Slice(start, length), idx++, plugin, rows)) return false;
                }
                return true;
            }
            case Shape.Packed:
            {
                if (count > 1 || counters > 1) return false;
                if (count == 0) return counters == 0 || CounterIsZero(data, counterAt, counterLen);
                var c = data.Slice(first, firstLen);
                int at = _start;
                long n;
                if (_counter != 0)
                {
                    if (counters != 1 || counterLen != _countBytes) return false;
                    n = ReadCount(data.Slice(counterAt, counterLen), _countBytes);
                }
                else if (_countBytes > 0)
                {
                    if (at + _countBytes > c.Length) return false;
                    n = ReadCount(c[at..], _countBytes);
                    at += _countBytes;
                }
                else
                {
                    if ((c.Length - at) % _entrySize != 0 || c.Length < at) return false;
                    n = (c.Length - at) / _entrySize;
                }
                if (n < 0 || at + n * _entrySize != c.Length || _fixedCount >= 0 && n != _fixedCount) return false;
                for (long i = 0; i < n; i++)
                    if (!Row(c.Slice(at + (int)(i * _entrySize), _entrySize), i, plugin, rows)) return false;
                return true;
            }
            case Shape.Repeat:
                if (count > 1) return false;
                return count == 0 || _repeat!.Read(data.Slice(first, firstLen), this, plugin, rows);
            case Shape.Grid:
            {
                if (count > 1) return false;
                if (count == 0) return true;
                var c = data.Slice(first, firstLen);
                if (c.Length != _start + _width * _height * _entrySize) return false;
                for (int y = 0; y < _height; y++)
                {
                    if (!_value) { rows.Add(new object?[] { (long)y }); continue; }
                    using var sw = new StringWriter(CultureInfo.InvariantCulture);
                    using (var jw = new Newtonsoft.Json.JsonTextWriter(sw) { Formatting = Newtonsoft.Json.Formatting.None })
                    {
                        jw.WriteStartArray();
                        for (int x = 0; x < _width; x++)
                        {
                            var b = c.Slice(_start + (y * _width + x) * _entrySize, _entrySize);
                            switch (_cellKind)
                            {
                                case "Object":
                                {
                                    // The cell made of its class from its bytes, written by the class's own writer
                                    object made = Activator.CreateInstance(_cellClass!)!;
                                    foreach (var (property, kind, at) in _cellFields!)
                                        property.SetValue(made, kind switch
                                        {
                                            "Float" => BinaryPrimitives.ReadSingleLittleEndian(b[at..]),
                                            "Int32" => BinaryPrimitives.ReadInt32LittleEndian(b[at..]),
                                            "UInt32" => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]),
                                            "Int16" => BinaryPrimitives.ReadInt16LittleEndian(b[at..]),
                                            "UInt16" => BinaryPrimitives.ReadUInt16LittleEndian(b[at..]),
                                            "Int8" => (object)(sbyte)b[at],
                                            _ => b[at],
                                        });
                                    jw.WriteRawValue(DirectList.Minified(SFSearch.Records.RecordSerializer.ToUtf8((Loqui.ILoquiObject)made)));
                                    break;
                                }
                                case "UInt8": jw.WriteValue(b[0]); break;
                                case "Int8": jw.WriteValue((sbyte)b[0]); break;
                                case "UInt16": jw.WriteValue(BinaryPrimitives.ReadUInt16LittleEndian(b)); break;
                                case "Int16": jw.WriteValue(BinaryPrimitives.ReadInt16LittleEndian(b)); break;
                                case "UInt32": jw.WriteValue(BinaryPrimitives.ReadUInt32LittleEndian(b)); break;
                                case "Int32": jw.WriteValue(BinaryPrimitives.ReadInt32LittleEndian(b)); break;
                                default:
                                {
                                    uint id = BinaryPrimitives.ReadUInt32LittleEndian(b);
                                    // A null link is Mutagen's FormKey.Null, which writes itself "Null"
                                    if (id == 0) { jw.WriteValue("Null"); break; }
                                    if (plugin.Key(id) is not { } key) return false;
                                    jw.WriteValue(key);
                                    break;
                                }
                            }
                        }
                        jw.WriteEndArray();
                    }
                    rows.Add(new object?[] { (long)y, sw.ToString() });
                }
                return true;
            }
        }
        return false;
    }

    /// <summary>Each component from its BFCB to its BFCE at the record's level, its kind's list read from its subrecords,
    /// each row the component's place, then the entry's.</summary>
    bool ReadComponents(ReadOnlySpan<byte> data, RawPlugin plugin, List<object?[]> rows)
    {
        int p = 0; uint big = 0; bool hasBig = false;
        int bodyStart = -1, bfcbAt = -1, bfcbLen = 0;
        long component = 0;
        // The components stand in one run. Mutagen's reader of the record reads a run where it meets a BFCB and sets the
        // list to it, so a second run after other subrecords would take the first one's place: not a record this reads
        bool runOver = false;
        var inner = new List<object?[]>();
        while (p + 6 <= data.Length)
        {
            int at = p;
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(data[p..]);
            int n = BinaryPrimitives.ReadUInt16LittleEndian(data[(p + 4)..]);
            p += 6;
            if (sig == XXXX && n == 4 && p + 4 <= data.Length) { big = BinaryPrimitives.ReadUInt32LittleEndian(data[p..]); hasBig = true; p += 4; continue; }
            long len = hasBig ? big : n; hasBig = false;
            if (p + len > data.Length) return false;
            if (sig == BFCB)
            {
                // A component inside a component: not a layout this reads
                if (bodyStart >= 0 || runOver) return false;
                bfcbAt = p; bfcbLen = (int)len; bodyStart = p + (int)len;
            }
            else if (bodyStart < 0 && component > 0 && sig != BFCE) runOver = true;
            else if (sig == BFCE)
            {
                if (bodyStart < 0) return false;
                string? kind = Selectors.ComponentKind(_model!, data.Slice(bfcbAt, bfcbLen));
                if (kind == null) return false;
                if (_kindColumns != null)
                {
                    // The component's own fields: one row
                    if (!_kindColumns.TryGetValue(kind, out var plan)) return false;
                    var row = new object?[1 + plan.At.Length];
                    row[0] = component++;
                    object?[]? values = null;
                    if (plan.Reader != null)
                    {
                        values = new object?[plan.Reader.Count];
                        if (!plan.Reader.Read(default, data[bodyStart..at], plugin, values)) return false;
                    }
                    for (int i = 0; i < plan.At.Length; i++) row[1 + i] = plan.At[i] < 0 ? plan.Constant[i] : values![plan.At[i]];
                    rows.Add(row);
                    bodyStart = -1;
                    p += (int)len;
                    continue;
                }
                if (!_kinds!.TryGetValue(kind, out var reader) && !KnownKind(kind)) return false;
                if (_kinds!.TryGetValue(kind, out reader))
                {
                    if (reader == null) return false;
                    inner.Clear();
                    if (!reader.Read(default, data[bodyStart..at], plugin, inner)) return false;
                    foreach (var r in inner)
                    {
                        var row = new object?[r.Length + 1];
                        row[0] = component;
                        Array.Copy(r, 0, row, 1, r.Length);
                        rows.Add(row);
                    }
                }
                component++;
                bodyStart = -1;
            }
            p += (int)len;
        }
        return p == data.Length && bodyStart < 0;
    }

    /// <summary>A component kind Mutagen reads (one of AComponent's), whatever its fields.</summary>
    bool KnownKind(string kind) => _model!.Objects.ContainsKey(kind);

    /// <summary>A list counted by a subrecord of its own whose subrecord is absent: exact only when the count is 0.</summary>
    bool CounterIsZero(ReadOnlySpan<byte> data, int at, int len) => len == _countBytes && ReadCount(data.Slice(at, len), _countBytes) == 0;

    static long ReadCount(ReadOnlySpan<byte> b, int bytes) => bytes switch
    {
        1 => b[0],
        2 => BinaryPrimitives.ReadUInt16LittleEndian(b),
        _ => BinaryPrimitives.ReadUInt32LittleEndian(b),
    };

    /// <summary>One entry's row: its place, then each column read from the entry's bytes.</summary>
    bool Row(ReadOnlySpan<byte> entry, long idx, RawPlugin plugin, List<object?[]> rows)
    {
        var row = new object?[1 + _cols.Length];
        row[0] = idx;
        for (int i = 0; i < _cols.Length; i++)
            if (_cols[i].Read(entry, true, plugin, out row[1 + i]) != RawColumn.Outcome.Value) return false;
        rows.Add(row);
        return true;
    }

    /// <summary>
    /// A list a hand-read layout of Mutagen's lays out (the package's HandReadLayouts.xml): a Sub holding one Repeat of
    /// fixed-size values (count="fit": entries while a whole one fits; endsAtZeros: none that starts with that many zero
    /// bytes, the rest Mutagen's fluff) with at most one Switch on a value before it, whose Case holds a FormID and whose
    /// Default holds nothing (the entry is 4 bytes shorter there, and the field null). A value's encoding "drop:N": bit N
    /// is Mutagen's elsewhere, cleared. Anything else in the Define: not read this way.
    /// </summary>
    sealed class RepeatLayout
    {
        public sealed record Value(string Field, string Kind, int Size, int At, bool AfterSwitch, uint Drop);
        public readonly List<Value> Values = new();
        int _fixed, _zeros = -1, _switchOn = -1, _switchSize, _formIdAt = -1;
        uint _mask, _when;

        static readonly ConcurrentDictionary<(SchemaModel, string, string, string), RepeatLayout?> Cache = new();

        public static RepeatLayout? Of(SchemaModel m, string recordSig, string sub, string field) =>
            Cache.GetOrAdd((m, recordSig, sub, field), k => Make(k.Item1, k.Item2, k.Item3, k.Item4));

        static int SizeOf(string kind) => kind switch
        {
            "I8" or "U8" => 1, "I16" or "U16" or "F16" => 2, "I32" or "U32" or "F32" or "FormID" => 4, "P3F32" => 12, "P3I16" => 6, _ => -1,
        };

        static uint Number(string? text) => text == null ? 0 : text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : uint.Parse(text, CultureInfo.InvariantCulture);

        static RepeatLayout? Make(SchemaModel m, string recordSig, string sub, string field)
        {
            // The record type's layout uses a Define whose one Sub is the list's subrecord and repeats the list
            var record = m.HandLayouts.LastOrDefault(h => h.Kind == "Record" && h.Sig == recordSig)?.Element;
            if (record == null) return null;
            var defines = m.HandLayouts.Where(h => h.Kind == "Define" && h.Name != null).GroupBy(h => h.Name!).ToDictionary(g => g.Key, g => g.Last().Element, StringComparer.Ordinal);
            XElement? sublayout = null;
            foreach (var use in record.Elements().Where(e => e.Name.LocalName == "Use"))
                if (defines.TryGetValue((string?)use.Attribute("name") ?? "", out var d) && d.Elements().ToList() is [{ Name.LocalName: "Sub" } s] && (string?)s.Attribute("sig") == sub)
                    sublayout = s;
            sublayout ??= record.Elements().FirstOrDefault(e => e.Name.LocalName == "Sub" && (string?)e.Attribute("sig") == sub);
            if (sublayout == null) return null;
            var parts = sublayout.Elements().ToList();
            if (parts.Count is < 1 or > 2 || parts[0] is not { Name.LocalName: "Repeat" } repeat || (string?)repeat.Attribute("field") != field || (string?)repeat.Attribute("count") != "fit") return null;
            // After the entries only Mutagen's count of the fluff bytes
            if (parts.Count == 2 && !(parts[1].Name.LocalName == "Bytes" && (string?)parts[1].Attribute("encoding") == "length")) return null;
            var l = new RepeatLayout();
            if (repeat.Attribute("endsAtZeros") is { } z) l._zeros = int.Parse(z.Value, CultureInfo.InvariantCulture);
            int at = 0;
            bool after = false;
            var named = new Dictionary<string, (int At, int Size)>(StringComparer.Ordinal);
            foreach (var e in repeat.Elements())
            {
                string kind = e.Name.LocalName;
                if (kind == "Switch")
                {
                    if (after || !named.TryGetValue((string?)e.Attribute("on") ?? "", out var on)) return null;
                    var cases = e.Elements().ToList();
                    if (cases.Count != 2 || cases[0] is not { Name.LocalName: "Case" } c0 || cases[1] is not { Name.LocalName: "Default" } d0) return null;
                    if (c0.Elements().SingleOrDefault() is not { Name.LocalName: "FormID" } fid || d0.Elements().SingleOrDefault() is not { Name.LocalName: "Null" } nul) return null;
                    string? f = (string?)fid.Attribute("field");
                    if (f == null || (string?)nul.Attribute("field") != f) return null;
                    (l._switchOn, l._switchSize) = on; l._mask = Number((string?)e.Attribute("mask")); l._when = Number((string?)c0.Attribute("value"));
                    if (l._mask == 0 || l._switchSize is not (1 or 2 or 4)) return null;
                    l._formIdAt = at;
                    l.Values.Add(new Value(f, "FormID", 4, at, false, 0));
                    after = true;
                    continue;
                }
                int size = SizeOf(kind);
                if (size < 0 || kind == "FormID") return null;
                uint drop = 0;
                if ((string?)e.Attribute("encoding") is { } enc)
                {
                    var dm = Regex.Match(enc, @"^drop:(\d+)$");
                    if (!dm.Success) return null;
                    drop = 1u << int.Parse(dm.Groups[1].Value, CultureInfo.InvariantCulture);
                }
                if ((string?)e.Attribute("name") is { } nm) named[nm] = (at, size);
                if ((string?)e.Attribute("field") is { } fld) l.Values.Add(new Value(fld, kind, size, at, after, drop));
                at += size;
            }
            l._fixed = at;
            if (l._zeros > at) return null;
            return l;
        }

        /// <summary>The entries of the list's subrecord, as Mutagen's hand-written reader takes them.</summary>
        public bool Read(ReadOnlySpan<byte> c, RawList list, RawPlugin plugin, List<object?[]> rows)
        {
            int pos = 0;
            long idx = 0;
            var cols = list._cols;
            while (true)
            {
                int rest = c.Length - pos;
                if (rest < _fixed) break;
                bool hasId = false;
                if (_formIdAt >= 0)
                {
                    uint flags = _switchSize switch { 1 => c[pos + _switchOn], 2 => BinaryPrimitives.ReadUInt16LittleEndian(c[(pos + _switchOn)..]), _ => BinaryPrimitives.ReadUInt32LittleEndian(c[(pos + _switchOn)..]) };
                    hasId = (flags & _mask) == _when;
                }
                int size = _fixed + (hasId ? 4 : 0);
                if (rest < size || _zeros > 0 && c.Slice(pos, _zeros).IndexOfAnyExcept((byte)0) < 0) break;
                var entry = c.Slice(pos, size);
                var row = new object?[1 + cols.Length];
                row[0] = idx++;
                for (int i = 0; i < cols.Length; i++)
                {
                    var v = Values[list._repeatValue[i]];
                    if (v.Kind == "FormID" && !hasId) { row[1 + i] = null; continue; }
                    int at = v.AfterSwitch && hasId ? v.At + 4 : v.At;
                    if (cols[i].Read(entry.Slice(at, v.Size), true, plugin, out row[1 + i]) != RawColumn.Outcome.Value) return false;
                }
                rows.Add(row);
                pos += size;
            }
            return true;
        }
    }
}
