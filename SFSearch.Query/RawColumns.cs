// The named columns of a world type read from the record's own bytes, at the schema's offsets (J30, GAPS S5). A
// column read straight from Mutagen's object (DirectColumns.cs) still costs Mutagen's read of the whole record: for a
// cell that is all of its traversals (14.1 million in Starfield.esm, 99% of its bytes), to give one link that sits
// alone in an XLCN subrecord. Reading the cell's 30,717 records inflated and stepping through their subrecords takes
// 0.27 s on 8 threads, against 6.6 s through Mutagen.
//
// A column is read this way only when its bytes say exactly what Mutagen would give, by the schema:
//   - DirectColumn.For takes it (a plain field of the record or of an object in it, of a plain kind, no "kind of
//     object" guard), and the view is of one record type;
//   - the field has a subrecord and a fixed offset in it, and a size that fits its kind (a link 4 bytes, an Int16 2,
//     a float 4 with no divisor or integer form, a point 8 or 12, an enum its own width);
//   - no other field of the type uses that subrecord in another place (a list, a kind of object, a component, a
//     nested record, a marker before it), and no step of Mutagen's own code reads it. Where the other place is a list
//     the record's value walk reads (each object template's FULL beside a weapon's own Name: GAPS S165), the record's own
//     subrecord is the one the walk gives the field, as Mutagen's generated reader takes it (Walked).
// Each record is then read alone: its subrecords stepped through (XXXX long forms, components skipped), the
// subrecord found once at the record's own level, the value taken at the offset. A record the bytes can't answer for
// sure goes through Mutagen as before: the subrecord twice, too short for the field, absent where Mutagen would give
// a default, a link through a missing master. The test direct-columns holds the bytes against Mutagen row by row on
// Starfield.esm's cells and placed objects.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Noggog;
using Mutagen.Bethesda.Binary;
using Mutagen.Bethesda.Plugins.Binary.Translations;
using SFSearch.Index;
using SFSearch.Records;
using SFSearch.SchemaGen;

namespace SFSearch.Query;

/// <summary>One column of a typed view read from the record's own bytes.</summary>
sealed partial class RawColumn
{
    enum Form { Constant, Link, Signed, Unsigned, Bool, Float, Double, Half, Enum, P2Float, P3Float, P2Int16, P3Int16, P2Int32, Bytes, Color, Guid, Text, Translated, HeaderFlag, Present }

    /// <summary>The subrecord, as the four bytes read little-endian.</summary>
    public readonly uint Subrecord;
    public readonly string SubrecordText;
    readonly int _offset, _size;
    readonly Form _form;
    /// <summary>An absent subrecord means no value (NULL): the field, or the one object on its way that may be absent,
    /// is that subrecord alone. Otherwise an absent subrecord is read through Mutagen.</summary>
    readonly bool _absentIsNull;
    readonly Type? _enum;
    /// <summary>The field is the property's own value type (not a nullable one): the serializer leaves it out when it
    /// is its type's default, all its bytes zero, and the view then gives <see cref="_viewDefault"/> (its coalesce, or
    /// NULL where it has none).</summary>
    readonly bool _zeroIsDefault;
    readonly object? _viewDefault;
    /// <summary>A field of the record's 24-byte header (its flags, version control, form version), not of a subrecord.</summary>
    public readonly bool InHeader;
    /// <summary>For <see cref="Form.HeaderFlag"/>: the flag's bit.</summary>
    readonly uint _bit;
    /// <summary>The field is its subrecord, whole: an enum is read at the subrecord's own length (Mutagen's
    /// EnumBinaryTranslation reads what remains), bytes are all of it.</summary>
    readonly bool _whole;
    /// <summary>When the field's subrecord is absent: the subrecords of the nullable object it sits in (the object is
    /// there when one of them is). Absent with the object there, the field is the view's default; with no object, NULL.
    /// Empty when the absent subrecord is decided otherwise.</summary>
    public readonly uint[] ObjectSubrecords = Array.Empty<uint>();
    /// <summary>A float Mutagen scales (<see cref="Scaled"/>): times it, or divided by its negative; 0 for none.</summary>
    float _scale;
    /// <summary>A field of an object that starts at an empty marker subrecord (a placed object's map marker after XMRK):
    /// the marker, and the object's own subrecords in its fields' order. Mutagen's generated reader gives the object
    /// the subrecords after the marker while each is one of its fields' and comes after the last one it took (an item
    /// ends at a field it already had or one before it); 0 and empty for any other field.</summary>
    public uint Marker { get; private init; }
    public uint[] RunSubrecords { get; private init; } = Array.Empty<uint>();
    /// <summary>The marker that ends the object's run (0: none; the run then ends at a subrecord out of its order).</summary>
    public uint EndMarker { get; private init; }
    /// <summary>The field's subrecord absent from its object: read through Mutagen.</summary>
    public bool RunAbsentToMutagen { get; private init; }
    /// <summary>For each of <see cref="RunSubrecords"/>: it may come many times in a row (a list's entries).</summary>
    public bool[] RunRepeats { get; private init; } = Array.Empty<bool>();
    /// <summary>For a marker object's field: the object is there and the field's subrecord isn't, so Mutagen holds its
    /// default (left out, the view's default) or, for a field that may be absent, nothing.</summary>
    object? _runAbsent;
    /// <summary>For an enum a layout stores with bits Mutagen keeps elsewhere (encoding "drop:2"): those bits, cleared
    /// before the value is read.</summary>
    readonly uint _drop;
    /// <summary>For <see cref="Form.Translated"/>: the strings table the text's ID is in (the field's StringsSource).</summary>
    Mutagen.Bethesda.Strings.StringsSource _table;
    /// <summary>A point the file stores Y first (the XML's swapCoords: a world's large references): read the other way round.</summary>
    bool _swap;
    /// <summary>A link whose stored FFFFFFFF is no link (FieldDef.MaxIsNone).</summary>
    bool _maxIsNone;
    /// <summary>The places of the Breaks before the field in its data block, among the block's own fields (not in an
    /// object laid inside it): a subrecord that ends at one of them holds no bytes of the field.</summary>
    int[] _breaks = Array.Empty<int>();
    /// <summary>A flags field's number column (GAPS S168): the enum's value as its number, not its names.</summary>
    bool _number;
    /// <summary>The field's subrecord also stands inside a list the record's value walk reads (a weapon's object
    /// templates each have a FULL, GAPS S165): the record's own is the one the walk gives at this path (the field's path in
    /// the record, <c>Name</c>), with the type it walks; null for any other column.</summary>
    public (RecordSchema Schema, string Type, string Path)? Walked { get; private init; }
    public string Name { get; }

    RawColumn(string name, string sub, int offset, int size, Form form, bool absentIsNull, Type? enumType, bool zeroIsDefault, object? viewDefault, bool inHeader = false, uint bit = 0, bool whole = false, uint[]? objectSubrecords = null, uint drop = 0)
    {
        _whole = whole; ObjectSubrecords = objectSubrecords ?? Array.Empty<uint>(); _drop = drop;
        Name = name; SubrecordText = sub; Subrecord = inHeader || sub.Length != 4 ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(System.Text.Encoding.ASCII.GetBytes(sub));
        _offset = offset; _size = size; _form = form; _absentIsNull = absentIsNull; _enum = enumType;
        _zeroIsDefault = zeroIsDefault; _viewDefault = viewDefault; InHeader = inHeader; _bit = bit;
    }

    static readonly ConcurrentDictionary<(RecordSchema, string, string), RawColumn?> Cache = new();

    /// <summary>The raw reader of a typed view's column, or null when the column is read through Mutagen.</summary>
    public static RawColumn? For(ViewSpec view, ColumnSpec c, RecordSchema schema)
    {
        if (view.IsList || view.IsUnion || view.Types.Count != 1) return null;
        // A type that shares its signature with others (GMST's five game settings, OMOD's eight modifications): which of
        // them a record is, only Mutagen's read says, so its bytes aren't read as this type's
        if (schema.SignatureOf(view.Types[0]) is not { } sig || schema.TypesOf(sig).Count != 1) return null;
        // Where no generated place gives it: by Mutagen's own layout of its subrecord (RawPlannedColumns.cs)
        return Cache.GetOrAdd((schema, view.Types[0], c.Name), _ => Make(view.Types[0], c, schema) ?? Numbered(Planned(view.Types[0], c, schema), c));
    }

    /// <summary>A flags field's number column is read as its flags column is, and gives the number (FlagsRaw.cs).</summary>
    static RawColumn? Numbered(RawColumn? r, ColumnSpec c)
    {
        if (r != null && c.Kind == FlagsRaw.Kind) r._number = true;
        return r;
    }

    /// <summary>The raw reader of a field of an object read like a record from its own run of subrecords (a component of
    /// one kind, between its BFCB and BFCE: RawLists.cs), by the same rules as a record's column. <paramref name="c"/>
    /// names the field by its path in the object, with its schema row of that object.</summary>
    internal static RawColumn? ForObject(string objectType, ColumnSpec c, RecordSchema schema) => Make(objectType, c, schema);

    /// <summary>
    /// The reader of one field of a list entry at a fixed place in the entry's bytes (RawLists.cs, GAPS S123): the same
    /// kinds, widths and "left out" rule as a typed view's column, read from the entry's own slice. <paramref name="drop"/>:
    /// bits of a stored enum Mutagen keeps elsewhere. Null when the kind or size isn't one read this way.
    /// </summary>
    /// <param name="kind">The kind the value is read as, when not the column's (a column of several kinds of entry, read
    /// as DirectColumn gives each: the entry's own value by its own kind, an enum in a number column as its number).</param>
    internal static RawColumn? Entry(ColumnSpec c, FieldDef def, int offset, int size, Type? enumType, uint drop = 0, string? kind = null)
    {
        // The entry's own field says what the serializer leaves out (DirectColumn.OmitDefault): a property that isn't
        // nullable at its type's default, which the column gives as its default (NULL in a column that may be NULL: a
        // script property's Data, of several kinds). A list's entry is never left out (a list of floats keeps its 0s).
        bool mixed = kind != null && kind != c.Kind;
        if (def.Default != null && (mixed || c.DefaultSql == null && !c.Nullable)) return null;
        bool zeroIsDefault = !def.Nullable && def.Default == null && !def.Path.EndsWith("[*]", StringComparison.Ordinal);
        object? viewDefault = c.DefaultSql != null && !c.Nullable && DirectColumn.TryLiteral(c.DefaultSql, out var dv) ? dv : null;
        kind ??= c.Kind;
        if (FormOf(kind, size, enumType) is not { } form || form is Form.Text or Form.Present) return null;
        if (drop != 0 && form != Form.Enum) return null;
        return new RawColumn(c.Name, "", offset, size, form, false, enumType, zeroIsDefault && form is not (Form.Link or Form.Bytes or Form.Color), viewDefault, drop: drop) { _swap = Swapped(def), _maxIsNone = def.MaxIsNone, _number = kind == FlagsRaw.Kind };
    }

    /// <summary>A column whose value is the same for every entry of a kind: what the view gives an entry whose class
    /// has no such property (RawLists.cs).</summary>
    internal static RawColumn Constant(string name, object? value) => new(name, "", 0, 0, Form.Constant, false, null, false, value);

    /// <summary>The form a value of this kind and size is read in from its bytes; null when it isn't read so.</summary>
    static Form? FormOf(string kind, int size, Type? enumType) => kind switch
    {
        "FormLink" when size == 4 => Form.Link,
        "Int8" when size == 1 => Form.Signed, "Int16" when size == 2 => Form.Signed, "Int32" when size == 4 => Form.Signed, "Int64" when size == 8 => Form.Signed,
        "UInt8" when size == 1 => Form.Unsigned, "UInt16" when size == 2 => Form.Unsigned, "UInt32" when size == 4 => Form.Unsigned, "UInt64" when size == 8 => Form.Unsigned,
        "Bool" when size is 1 or 2 or 4 => Form.Bool,
        "Float" when size == 4 => Form.Float,
        "Double" when size == 8 => Form.Double,
        "Half" when size == 2 => Form.Half,
        "P2Float" when size == 8 => Form.P2Float,
        "P3Float" when size == 12 => Form.P3Float,
        "P2Int16" when size == 4 => Form.P2Int16,
        "P3Int16" when size == 6 => Form.P3Int16,
        "P2Int32" when size == 8 => Form.P2Int32,
        "ByteArray" when size > 0 => Form.Bytes,
        "Color" when size is 3 or 4 => Form.Color,
        "Guid" when size == 16 => Form.Guid,
        _ when FlagsRaw.IsEnumKind(kind) && enumType != null && size is 1 or 2 or 4 or 8 => Form.Enum,
        _ => null,
    };

    static RawColumn? Make(string type, ColumnSpec c, RecordSchema schema) => Numbered(MakeColumn(type, c, schema), c);

    static RawColumn? MakeColumn(string type, ColumnSpec c, RecordSchema schema)
    {
        // (any language: it decides only how a translated text is read, not whether)
        if (DirectColumn.For(c, "English") == null || c.Row is not { } row) return null;
        var def = row.Def;
        // A default the XML gives (default="uint.MaxValue"): the serializer leaves that value out, not zero, and the view
        // gives it back through its coalesce; with no coalesce to give it back, the column is read through Mutagen
        if (def.Default != null && c.DefaultSql == null && !c.Nullable) return null;
        bool zeroIsDefault = !c.Nullable && def.Default == null;
        object? viewDefault = c.DefaultSql != null && !c.Nullable && DirectColumn.TryLiteral(c.DefaultSql, out var dv) ? dv : null;
        // The record's header: its fields where Mutagen's layout of the record header places them (HandReadLayouts.xml's
        // Header, C57 W2): the flags at 8, version control at 16, form version at 20, the second version at 22;
        // IsCompressed and IsDeleted flag bits; MajorFlags and StarfieldMajorRecordFlags the flags as the type's enums
        if (c.RelPath is { } hp && def.Subrecord == null && HandLayouts.HeaderPlace(schema.Model, def) is { } h
            && schema.Model.Objects.TryGetValue(def.Object, out var owner) && owner.ObjType == "Record")
        {
            if (h.Bit != 0) return c.Kind == "Bool" ? new RawColumn(c.Name, "", h.Offset, h.Size, Form.HeaderFlag, false, null, zeroIsDefault, viewDefault, inHeader: true, bit: h.Bit) : null;
            Form hf; Type? he = null;
            switch (c.Kind)
            {
                case "Int32" or "Int16": hf = Form.Signed; break;
                case "UInt32" or "UInt16": hf = Form.Unsigned; break;
                case "Flags" or "Enum" or FlagsRaw.Kind:
                    he = EnumType(type, hp);
                    if (he == null) return null;
                    hf = Form.Enum; break;
                default: return null;
            }
            return new RawColumn(c.Name, "", h.Offset, h.Size, hf, false, he, zeroIsDefault, viewDefault, inHeader: true);
        }
        if (row.Subrecord is not { Length: 4 } sub) return null;
        // A text that is its subrecord, whole: Mutagen reads it null-terminated in the game's encoding for texts that
        // aren't translated (GameConstants.Starfield.Encodings.NonTranslated)
        int? textSize = c.Kind == "String" && def.Subrecord == sub && (def.TextForm ?? "NullTerminate") == "NullTerminate" && !def.Translated ? -1 : null;
        // An asset's path that is its subrecord, whole (a model's File in MODL): Mutagen's AssetLink keeps the text as
        // read (GivenPath), which DirectColumn gives
        if (c.Kind == "AssetLink" && def.Kind == "AssetLink" && def.Subrecord == sub && (def.TextForm ?? "NullTerminate") == "NullTerminate" && row.Offset is null or 0) textSize = -1;
        // A translated text that is its subrecord, whole (GAPS S152): a localized plugin's 4-byte string ID, looked up in
        // the field's strings table as Mutagen's TranslatedString does; another plugin's stored text (RawPlugin.TryText)
        if (c.Kind == "String" && def.Translated && def.Subrecord == sub && (def.TextForm ?? "NullTerminate") == "NullTerminate" && row.Offset is null or 0
            && Enum.TryParse<Mutagen.Bethesda.Strings.StringsSource>(def.StringsTable, out _)) textSize = -3;
        // Bytes that are their subrecord, whole, of any length (an unknown subrecord's content)
        if (c.Kind == "ByteArray" && def.Size == null && def.Subrecord == sub && row.Offset is null or 0) textSize = -2;
        if ((textSize == -3 ? textSize : def.Size ?? textSize) is not int size) return null;
        // A field that Mutagen's hand-written code fills (a placed object's Position and Rotation, by PlacementParse from
        // DATA) is read the same way when the schema lays it out plainly: its subrecord and offset, no encoding, no bits
        bool handPlain = def.Custom && def.FilledBy != null && def.LayoutSubrecord == sub && int.TryParse(def.LayoutOffset, out _)
            && def.LayoutEncoding == null && def.LayoutPresentWhen == null && def.LayoutBits == null && def.LayoutDroppedBits == null;
        // A field that is its own subrecord starts it; any other has its offset in the schema
        int offset = handPlain ? int.Parse(def.LayoutOffset!) : row.Offset ?? (def.Subrecord == sub ? 0 : -1);
        // A float or float point Mutagen's hand-written code reads at its place and scales (the XML's layoutEncoding
        // "float*2", "float/100": a placed primitive's Bounds, a worldspace's object bounds): the same bytes, scaled
        float scale = Scaled(def);
        bool encoded = scale != 0;
        if (offset < 0) return null;
        if (row.InList || row.Path.Contains('[') || row.Path.Contains('{') || def.Custom && !handPlain && !encoded || def.Source != null || def.ReadOnly) return null;
        if (def.Marker != null || def.EndMarker != null || def.LayoutOffset != null && !handPlain || def.LayoutEncoding != null && !encoded || def.LayoutPresentWhen != null) return null;
        if (encoded && c.Kind is not ("Float" or "P2Float" or "P3Float")) return null;
        // A float kept as an integer, or scaled, isn't its four bytes as they are
        if (def.Note is { } note && (note.Contains("stored value", StringComparison.Ordinal) || note.Contains("stored as", StringComparison.Ordinal))) return null;
        var model = schema.Model;
        // A field of an object of the record's own that starts at a marker (MapMarker after XMRK): its subrecords are the
        // run after the marker, whatever else of the record uses the same signatures
        uint marker = 0, endMarker = 0; uint[] runSubs = Array.Empty<uint>();
        if (row.Parent is { Parent: null } mo && mo.Def.Kind == "Object" && mo.Def.Nullable && mo.Def.RefObject is { } mro && (mo.Def.Marker ?? model.MarkerOf(mro)) is { Length: 4 } mk
            && !mo.Def.Custom && !mo.InList && model.HandReadOf(mro) == null && !schema.IsPolymorphic(mro) && def.Subrecord == sub && offset == 0)
        {
            var fields = Rows(schema, type).Where(r => r.PlainPath.StartsWith(mo.PlainPath + ".", StringComparison.Ordinal) && !r.PlainPath[(mo.PlainPath.Length + 1)..].Contains('.') && r.Def.Kind is not ("Break" or "Marker")).ToList();
            // Each field its own subrecord, read by the generated reader, in its order
            if (fields.Count == 0 || fields.Any(f => f.Subrecord is not { Length: 4 } || f.Def.Subrecord != f.Subrecord || f.Def.Custom || f.Def.Kind is "CustomLogic" or "Object" or "List" or "Array" or "Gendered" || f.Def.Marker != null)
                || fields.Select(f => f.Subrecord).Distinct().Count() != fields.Count) return null;
            marker = BinaryPrimitives.ReadUInt32LittleEndian(System.Text.Encoding.ASCII.GetBytes(mk));
            if ((mo.Def.EndMarker ?? model.EndMarkerOf(mro)) is { } em) endMarker = em.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(System.Text.Encoding.ASCII.GetBytes(em)) : uint.MaxValue;
            if (endMarker == uint.MaxValue) return null;
            runSubs = fields.Select(f => BinaryPrimitives.ReadUInt32LittleEndian(System.Text.Encoding.ASCII.GetBytes(f.Subrecord!))).ToArray();
        }
        // A field of an object of the record's own that Mutagen's hand-written code reads as the record type's hand-read
        // layout lays it out (a placed object's patrol: XPRD, XPPA, INAM, then its topics): the object's run starts at its
        // first subrecord and takes the layout's subrecords in order
        bool[] runRepeat = Array.Empty<bool>();
        if (marker == 0 && row.Parent is { Parent: null } ho && ho.Def.Kind == "Object" && ho.Def.Nullable && ho.Def.RefObject is { } hro && model.HandReadOf(hro) != null
            && !schema.IsPolymorphic(hro) && def.Subrecord == sub && offset == 0 && HandRun(model, schema.SignatureOf(type)!, ho.Def.Name, def.Name, sub, size) is { } run)
        {
            marker = run.Sigs[0]; runSubs = run.Sigs; runRepeat = run.Repeat;
        }
        // The objects on the field's way: none read by Mutagen's own code, none with a marker
        for (var up = row.Parent; up != null && marker == 0; up = up.Parent)
        {
            if (up.Def.Marker != null || up.Def.EndMarker != null || up.Def.Custom || up.InList) return null;
            // Read by Mutagen's own code: only where that code is a scaled float laid out plainly (a primitive's Bounds)
            if (up.Def.RefObject is { } ro && model.HandReadOf(ro) != null && !OnlyScaled(Rows(schema, type), up)) return null;
            // An object that starts or ends at a marker of its own (a grouped pack-in between XWPK and XWPK): its
            // subrecords are the run between them, never the record's own (GAPS S125)
            if (up.Def.RefObject is { } mr && (model.MarkerOf(mr) != null || model.EndMarkerOf(mr) != null)) return null;
        }
        Form form; Type? enumType = null;
        switch (c.Kind)
        {
            case "FormLink" when size == 4: form = Form.Link; break;
            case "Int8" when size == 1: case "Int16" when size == 2: case "Int32" when size == 4: case "Int64" when size == 8: form = Form.Signed; break;
            case "UInt8" when size == 1: case "UInt16" when size == 2: case "UInt32" when size == 4: form = Form.Unsigned; break;
            case "Bool" when size is 1 or 2 or 4: form = Form.Bool; break;
            // A yes/no that is its subrecord's presence (Mutagen's boolAsMarker)
            // (not in an object that may be absent: then the absent subrecord can't tell false from no object)
            case "Bool" when size == 0 && def.Subrecord == sub && (c.NullGuard == null || marker != 0): form = Form.Present; break;
            case "Float" when size == 4: form = Form.Float; break;
            case "Double" when size == 8: form = Form.Double; break;
            case "Half" when size == 2: form = Form.Half; break;
            case "P2Float" when size == 8: form = Form.P2Float; break;
            case "P3Float" when size == 12: form = Form.P3Float; break;
            case "P2Int16" when size == 4: form = Form.P2Int16; break;
            case "P3Int16" when size == 6: form = Form.P3Int16; break;
            case "P2Int32" when size == 8: form = Form.P2Int32; break;
            case "ByteArray" when size > 0 || size == -2: form = Form.Bytes; break;
            // Mutagen's ReadColor: Alpha four bytes (R, G, B, A), NoAlpha three
            case "Color" when size is 3 or 4: form = Form.Color; break;
            // Mutagen's GuidBinaryTranslation: the 16 bytes as .NET's Guid reads them
            case "Guid" when size == 16: form = Form.Guid; break;
            case "String" when size == -1: form = Form.Text; break;
            case "AssetLink" when size == -1: form = Form.Text; break;
            case "String" when size == -3: form = Form.Translated; break;
            case "Enum" or "Flags" or FlagsRaw.Kind:
                enumType = EnumType(type, c.RelPath!);
                // Mutagen reads an enum at the field's own width whatever the enum's CLR width (EnumBinaryTranslation:
                // 1 byte unsigned, 2 unsigned, 4 signed, 8 signed), and a whole subrecord at the length it has
                if (enumType == null || size is not (1 or 2 or 4 or 8)) return null;
                form = Form.Enum; break;
            default: return null;
        }
        // The subrecord in one place of the record: every field of the type in it sits under the same object, at the
        // record's own level, read by Mutagen's generated reader
        string parent = ParentPath(row.PlainPath);
        var rows = Rows(schema, type);
        bool walked = false, beside = false;
        foreach (var r in marker != 0 ? Enumerable.Empty<FieldRow>() : rows)
        {
            bool uses = r.Subrecord == sub || r.Def.CustomSubrecordList.Contains(sub);
            if (!uses) continue;
            var stack = r.Stack.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
            // A record nested in this one (a cell's placed objects) is read from its own bytes, not from this record's
            if (stack.Any(o => model.Objects.TryGetValue(o, out var od) && od.ObjType == "Record")) continue;
            // A component's field: its subrecords sit between BFCB and BFCE, which the reading steps over (J68: Mutagen's
            // AComponent reader takes everything between them), so the same four letters there are never the record's
            if (stack.Any(o => model.EndMarkerOf(o) == "BFCE")) continue;
            // The hand-written step that fills this field, and the fields it fills beside it from the same layout
            if (handPlain && (r.Def.Kind == "CustomLogic" && r.Def.Path == def.FilledBy || r.Def.Custom && r.Def.FilledBy == def.FilledBy)) continue;
            // A scaled float beside it is laid out plainly too (a primitive's Bounds before its Color)
            if (r.Def.Kind == "CustomLogic" || r.Def.Custom && Scaled(r.Def) == 0) return null;
            // A list in the same subrecord that starts after the field doesn't move it
            if (ListAfter(r, rows, offset + Math.Max(size, 0))) continue;
            // In a list the record's value walk reads (its entries as Mutagen's generated reader ends them, or the runs the
            // type's hand-read layout cuts): the walk tells the record's own subrecord from the list's (GAPS S165)
            if ((r.InList || r.Path.Contains('[') || r.Path.Contains('{')) && r.Def.Marker == null && WalkTells(schema, type, row, r)) { walked = true; continue; }
            if (r.InList || r.Path.Contains('[') || r.Path.Contains('{') || r.Def.Marker != null) return null;
            // An object field whose bytes are this subrecord (the data block itself), or a field in it; or an object laid
            // beside the field's in the same subrecord (a weather's colours in NAM0, a weapon's sounds in WAUD), each at
            // the place the schema gives it there
            // (a property Mutagen derives, an object's Versioning, takes no bytes)
            if (r.Def.Source != null && r.Offset == null && ParentPath(r.PlainPath) != parent) continue;
            if (r.Def.Kind is "Object" or "Gendered")
            {
                if (row.PlainPath.StartsWith(r.PlainPath + ".", StringComparison.Ordinal)) continue;
                if (!SameBlock(rows, row, r, sub)) return null;
                beside = true; continue;
            }
            if (ParentPath(r.PlainPath) != parent)
            {
                if (!SameBlock(rows, row, r, sub)) return null;
                beside = true; continue;
            }
            // Another field that takes a whole subrecord of the same signature (a scene's PlayerDialogueBehavior and
            // ReactionRadiusBehavior, both VNAM): which one a VNAM is, Mutagen's generated reader decides by their order
            // and by how many came; the value walk claims them so (GAPS S165's route)
            if (r.PlainPath != row.PlainPath && WholeSubrecord(r, sub) && WholeSubrecord(row, sub))
            {
                if (!WalkTells(schema, type, row, null)) return null;
                walked = true;
            }
        }
        // An absent subrecord: NULL when the field itself is that subrecord and may be absent, or the nullable object
        // it sits in is that subrecord alone; otherwise Mutagen says what it gives (a default)
        bool absentIsNull = false;
        uint[]? objectSubs = null;
        if (def.Nullable && def.Subrecord == sub) absentIsNull = true;
        // A link, or bytes with no default, that are their subrecord: absent, Mutagen holds a null link or no bytes, which
        // the serializer leaves out, and the view gives NULL, whether the object it sits in is there or not
        else if (def.Subrecord == sub && (form == Form.Link || form == Form.Bytes && c.DefaultSql == null)) absentIsNull = true;
        else if (c.NullGuard != null)
        {
            var guard = row.Parent;
            while (guard != null && guard.PlainPath != c.NullGuard && !guard.PlainPath.EndsWith("." + c.NullGuard, StringComparison.Ordinal)) guard = guard.Parent;
            // The object is there exactly when the subrecord is: every field of it sits in that subrecord
            var inside = guard == null ? null : rows.Where(r => r.PlainPath.StartsWith(guard.PlainPath + ".", StringComparison.Ordinal) && r.Def.Kind is not ("Break" or "Marker")).ToList();
            if (guard != null && guard.Def.Nullable && (guard.Subrecord == null || guard.Subrecord == sub) && inside!.Count > 0 && inside.All(r => r.Subrecord == sub))
                absentIsNull = true;
            // An object of several subrecords, each read by Mutagen's generated reader (no list, kind, marker or step of
            // its own code in it): it is there when one of its subrecords is (fix-s4s5-world-reads)
            else if (guard != null && guard.Def.Nullable && guard.Subrecord == null && inside!.Count > 0
                && inside.All(r => r.Subrecord is { Length: 4 } && !r.InList && !r.Path.Contains('[') && !r.Path.Contains('{') && !r.Def.Custom && r.Def.Kind != "CustomLogic"
                    && r.Def.Marker == null && r.Def.EndMarker == null && (r.Def.RefObject == null || model.HandReadOf(r.Def.RefObject) == null)))
                objectSubs = inside.Select(r => BinaryPrimitives.ReadUInt32LittleEndian(System.Text.Encoding.ASCII.GetBytes(r.Subrecord!))).Distinct().ToArray();
        }
        // The field is its subrecord, whole, only when no other field of its object shares the subrecord: the first field
        // of a data block (a weapon's FiringType at 0 of WFIR) is read at its own width (J114)
        bool whole = def.Subrecord == sub && offset == 0 && form is Form.Enum or Form.Bytes && !beside
            && !rows.Any(r => r.Path != row.Path && r.Subrecord == sub && ParentPath(r.PlainPath) == parent && r.Def.Kind is not ("Break" or "Marker") && r.Def.Source == null && !r.InList && !r.Path.Contains('['));
        if (marker != 0)
        {
            if (form is Form.Text || def.Default != null) return null;
            // Absent in an object that is there: Mutagen's default (left out: the view's default), a presence yes/no
            // false, a link or a field that may be absent nothing; bytes Mutagen holds as it holds them (read through it)
            return new RawColumn(c.Name, sub, offset, size, form, false, enumType, zeroIsDefault && form is not (Form.Link or Form.Bytes or Form.Color or Form.Translated), viewDefault, whole: def.Subrecord == sub && form is Form.Enum or Form.Bytes)
            {
                _table = form == Form.Translated ? Enum.Parse<Mutagen.Bethesda.Strings.StringsSource>(def.StringsTable!) : default,
                Marker = marker, EndMarker = endMarker, RunSubrecords = runSubs, RunRepeats = runRepeat,
                _swap = Swapped(def), _maxIsNone = def.MaxIsNone,
                // A translated text absent from the object: none when it may be absent, else Mutagen's default, an empty
                // text (a map marker's Name, `= string.Empty`), which DirectColumn gives as ""
                _runAbsent = form == Form.Present ? (zeroIsDefault ? viewDefault : 0L) : form == Form.Translated ? (def.Nullable ? null : "") : def.Nullable || form == Form.Link ? null : viewDefault,
                RunAbsentToMutagen = form == Form.Bytes,
            };
        }
        // Told from the list's by the walk: the field alone at its place, its object's other subrecords not looked at
        if (walked && (objectSubs != null || row.Parent != null)) return null;
        // A scaled value is left out by the serializer when Mutagen's value is 0, which -0 bytes scale to as well: read
        // it, never take the bytes' zeros for the default
        // A walked field is read from the place the walk gives it (its value's bytes, offset 0 of them)
        return new RawColumn(c.Name, sub, walked ? 0 : offset, size, form, absentIsNull, enumType, zeroIsDefault && form is not (Form.Link or Form.Bytes or Form.Color or Form.Text or Form.Translated), viewDefault, whole: whole && !walked, objectSubrecords: objectSubs)
        {
            Walked = walked ? (schema, type, row.Path) : null,
            _breaks = walked || marker != 0 ? Array.Empty<int>()
                : rows.Where(r => r.Def.Kind == "Break" && r.Subrecord == sub && ParentPath(r.PlainPath) == parent && r.Offset is int at && at > 0 && at <= offset && r.Stack == row.Stack).Select(r => r.Offset!.Value).Distinct().ToArray(),
            _scale = scale,
            _swap = Swapped(def), _maxIsNone = def.MaxIsNone,
            _table = form == Form.Translated ? Enum.Parse<Mutagen.Bethesda.Strings.StringsSource>(def.StringsTable!) : default,
        };
    }

    /// <summary>Whether the record's value walk tells the field's own subrecord from that of <paramref name="other"/>, a
    /// field in a list: the walk reaches the field at the record's own level and the list (its entries), so Mutagen's
    /// generated reader's claim of each subrecord is the walk's (LinkByteWalker.ValueReach).</summary>
    /// <summary>
    /// The object whose subrecord a field's bytes are in: up from the field through the objects laid inline in that
    /// subrecord (each at a place of its own there: not a list's entry, not one that may be absent, read by Mutagen's
    /// generated reader), to the first that isn't (the object that is the subrecord, or one of several subrecords), or
    /// the record (null).
    /// </summary>
    static FieldRow? BlockOwner(FieldRow r, string sub)
    {
        var up = r.Parent;
        while (up != null && up.Subrecord == sub && up.Offset != null && up.Def.Kind == "Object" && !up.InList && !up.Def.Nullable
            && !up.Def.Custom && up.Def.Marker == null && up.Def.EndMarker == null) up = up.Parent;
        return up;
    }

    /// <summary>
    /// Two fields that name the same subrecord sit in one and the same subrecord: both at a place the schema gives in it,
    /// under the same owner (<see cref="BlockOwner"/>), and no two values of that owner's in the subrecord at the same
    /// place (two subrecords of one signature, or two kinds of one object, would each start at 0). So a field beside an
    /// object in a data block (an explosion's Light before its Sound1, a weather's nine colours) is read at its place.
    /// </summary>
    static bool SameBlock(List<FieldRow> rows, FieldRow row, FieldRow other, string sub)
    {
        if (row.Offset == null || other.Offset == null) return false;
        // (by path: a view's row and the type's rows are two readings of the same schema)
        string? owner = BlockOwner(row, sub)?.Path;
        if (BlockOwner(other, sub)?.Path != owner) return false;
        var places = new HashSet<int>();
        foreach (var r in rows)
        {
            if (r.Subrecord != sub || r.Def.Kind is "Object" or "Gendered" or "Break" or "Marker" or "CustomLogic" || r.Def.Source != null) continue;
            if (BlockOwner(r, sub)?.Path != owner) continue;
            // (a flags field's number column is the same row; two rows at one place are two things)
            if (r.Offset is not int at || !places.Add(at)) return false;
        }
        return true;
    }

    static bool WalkTells(RecordSchema schema, string type, FieldRow row, FieldRow? other)
    {
        // A record's own field (not an object's read like one: a component's, RawLists.cs)
        if (row.Parent != null || row.Path.Contains('[') || row.Path.Contains('{') || !schema.Model.Objects.TryGetValue(type, out var od) || od.ObjType != "Record") return false;
        var reach = LinkByteWalker.ValueReach(schema, type);
        if (!reach.TryGetValue(row.Path, out var own) || own != null) return false;
        if (other == null) return true;
        int at = other.Path.IndexOf('[');
        if (at <= 0) return false;
        string list = other.Path[..at] + "[*]";
        return reach.Any(kv => kv.Value == null && LinkByteWalker.Unmarked(kv.Key) == list);
    }

    /// <summary>A field that is a whole subrecord of its own (the subrecord's start, no other field of a data block after
    /// it in the subrecord).</summary>
    static bool WholeSubrecord(FieldRow r, string sub) => r.Def.Subrecord == sub && r.Offset is null or 0 && r.Def.Kind is not ("Object" or "Gendered" or "List" or "Array");

    /// <summary>Every field of the object Mutagen's own code reads is a scaled float laid out plainly, and nothing else of
    /// it is hand-read (no step, no field a step fills).</summary>
    static bool OnlyScaled(List<FieldRow> rows, FieldRow obj)
    {
        var inside = rows.Where(r => r.PlainPath.StartsWith(obj.PlainPath + ".", StringComparison.Ordinal)).ToList();
        return inside.Any(r => r.Def.Custom) && inside.All(r => r.Def.Kind != "CustomLogic" && (!r.Def.Custom || Scaled(r.Def) != 0) && r.Def.FilledBy == null);
    }

    /// <summary>The scale of a float Mutagen's hand-written code reads at a fixed place and scales (layoutEncoding
    /// "float*N" or "float/N" with a whole N, nothing else about it hand-laid): N, or 1/N as a divisor marked by a
    /// negative number; 0 when the field isn't one.</summary>
    static float Scaled(FieldDef d)
    {
        if (!d.Custom || d.LayoutEncoding is not { } e || d.Offset == null || d.Size == null || d.FilledBy != null || d.LayoutPresentWhen != null || d.LayoutBits != null || d.LayoutDroppedBits != null || d.LayoutOffset != null || d.LayoutSubrecord != null) return 0;
        var m = System.Text.RegularExpressions.Regex.Match(e, @"^float([*/])(\d+)$");
        if (!m.Success || !int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n <= 0) return 0;
        return m.Groups[1].Value == "*" ? n : -n;
    }

    /// <summary>True when the row is a list (or a field of its entries) whose list starts at a fixed offset at or after
    /// <paramref name="end"/> in the subrecord: the field before it sits where the schema says whatever the list holds.</summary>
    static bool ListAfter(FieldRow r, List<FieldRow> rows, int end)
    {
        string path = r.PlainPath;
        int bracket = path.IndexOf('[');
        string list = bracket < 0 ? path : path[..bracket];
        var top = bracket < 0 ? r : rows.FirstOrDefault(x => x.PlainPath == list);
        return top != null && top.Def.Kind is "List" or "Array" or "Array2D" && top.Offset is int at && at >= end && top.Subrecord == r.Subrecord;
    }

    static readonly ConcurrentDictionary<(RecordSchema, string), List<FieldRow>> RowCache = new();

    /// <summary>Every schema row of a record type, once (a cell's are many: its nested records' too).</summary>
    static List<FieldRow> Rows(RecordSchema schema, string type) => RowCache.GetOrAdd((schema, type), k => k.Item1.Model.Expand(k.Item2).ToList());

    static string ParentPath(string path) { int dot = path.LastIndexOf('.'); return dot < 0 ? "" : path[..dot]; }

    /// <summary>The CLR enum of a field, from Mutagen's getter interface of the record type, along the path.</summary>
    static Type? EnumType(string recordType, string relPath)
    {
        var asm = typeof(Mutagen.Bethesda.Starfield.StarfieldMod).Assembly;
        Type? t = asm.GetType($"Mutagen.Bethesda.Starfield.I{recordType}Getter");
        foreach (string name in relPath.Split('.'))
        {
            if (t == null) return null;
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
        return t is { IsEnum: true } ? t : null;
    }

    /// <summary>What a read from the bytes gave.</summary>
    public enum Outcome { Value, Mutagen }

    /// <summary>
    /// The run of subrecords the record type's hand-read layout gives an object (an Object element of its Record): a Sub
    /// that starts it, then Subs (optional or not), Generated subrecords and Uses of a Define that is one SubRepeat, in
    /// order. Null when the layout has another element, or when <paramref name="field"/> isn't a value at the start of
    /// one of its Subs (as the layout's value of that size) or a Generated subrecord's field.
    /// </summary>
    static (uint[] Sigs, bool[] Repeat)? HandRun(SchemaModel m, string recordSig, string objectField, string field, string fieldSub, int size)
    {
        var record = m.HandLayouts.LastOrDefault(h => h.Kind == "Record" && h.Sig == recordSig)?.Element;
        var obj = record?.Elements().FirstOrDefault(e => e.Name.LocalName == "Object" && (string?)e.Attribute("field") == objectField);
        if (obj == null) return null;
        var defines = m.HandLayouts.Where(h => h.Kind == "Define" && h.Name != null).GroupBy(h => h.Name!).ToDictionary(g => g.Key, g => g.Last().Element, StringComparer.Ordinal);
        var sigs = new List<string>(); var repeat = new List<bool>();
        bool found = false;
        foreach (var e in obj.Elements())
        {
            switch (e.Name.LocalName)
            {
                case "Sub":
                {
                    string? sig = (string?)e.Attribute("sig");
                    if (sig is not { Length: 4 } || sigs.Count == 0 && (string?)e.Attribute("optional") == "true") return null;
                    var first = e.Elements().FirstOrDefault();
                    if (first != null && (string?)first.Attribute("field") == field)
                        found = first.Name.LocalName switch { "F32" or "I32" or "U32" or "FormID" => size == 4, "I16" or "U16" or "F16" => size == 2, "I8" or "U8" => size == 1, _ => false };
                    sigs.Add(sig); repeat.Add(false);
                    break;
                }
                case "Generated":
                    foreach (string sig in ((string?)e.Attribute("sigs") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (sig.Length != 4 || sigs.Count == 0) return null;
                        found |= sig == fieldSub;
                        sigs.Add(sig); repeat.Add(false);
                    }
                    break;
                case "Use":
                {
                    if (!defines.TryGetValue((string?)e.Attribute("name") ?? "", out var d) || d.Elements().ToList() is not [{ Name.LocalName: "SubRepeat" } sr] || (string?)sr.Attribute("sig") is not { Length: 4 } rs) return null;
                    sigs.Add(rs); repeat.Add(true);
                    break;
                }
                default: return null;
            }
        }
        if (sigs.Count == 0 || sigs.Distinct().Count() != sigs.Count) return null;
        return found ? (sigs.Select(x => BinaryPrimitives.ReadUInt32LittleEndian(System.Text.Encoding.ASCII.GetBytes(x))).ToArray(), repeat.ToArray()) : null;
    }

    /// <summary>A marker object's field whose object is there and whose subrecord isn't (<see cref="Marker"/>).</summary>
    public object? RunAbsent => _runAbsent;

    /// <summary>The field's subrecord is absent and the object it sits in is there (<paramref name="objectThere"/>) or not.
    /// Bytes that are a whole subrecord of their own length are none (null) when it is absent, the object there or not.</summary>
    public object? Absent(bool objectThere) => objectThere && !(_whole && _form == Form.Bytes) ? _viewDefault : null;

    /// <summary>The column's value from its subrecord's content (empty and <paramref name="present"/> false when the
    /// record hasn't it): a long, a double, a text or null, as DirectColumn gives it. <see cref="Outcome.Mutagen"/> when
    /// the bytes can't say it for sure.</summary>
    public Outcome Read(ReadOnlySpan<byte> content, bool present, RawPlugin plugin, out object? value)
    {
        value = null;
        if (_form == Form.Constant) { value = _viewDefault; return Outcome.Value; }
        // A presence yes/no: there, true; absent, false, which the serializer leaves out (the view's default)
        if (_form == Form.Present) { value = present ? 1L : _zeroIsDefault ? _viewDefault : 0L; return Outcome.Value; }
        if (!present) return _absentIsNull ? Outcome.Value : Outcome.Mutagen;
        if (_form == Form.Text)
        {
            value = BinaryStringUtility.ProcessWholeToZString(content[_offset..], Mutagen.Bethesda.Plugins.Meta.GameConstants.Starfield.Encodings.NonTranslated);
            return Outcome.Value;
        }
        if (_form == Form.Translated)
        {
            // As DirectColumn gives it: the text in the query's language, the CK's tag taken off, the view's default for none
            if (!plugin.TryText(content, _table, out var text)) return Outcome.Mutagen;
            value = Functions.Display(text) ?? _viewDefault;
            return Outcome.Value;
        }
        int size = _whole ? content.Length : _size;
        // Bytes that are an empty subrecord: Mutagen tells them from an absent one (an empty array, C65): read through it
        if (_whole && _form == Form.Bytes && size == 0) return Outcome.Mutagen;
        if (_whole && _form == Form.Enum && size is not (1 or 2 or 4 or 8)) return Outcome.Mutagen;
        if (content.Length < _offset + size || size < 0)
        {
            // The subrecord ends at a Break before the field (a record written before the field was added: a static's DNAM
            // without its leaf settings): Mutagen's generated reader stops there and the field keeps its type's default,
            // which the serializer leaves out
            if (size > 0 && _zeroIsDefault && _scale == 0 && Array.IndexOf(_breaks, content.Length) >= 0) { value = _viewDefault; return Outcome.Value; }
            return Outcome.Mutagen;
        }
        var b = content.Slice(_offset, size);
        // Left out by the serializer as its type's default: the view's default
        if (_zeroIsDefault && _scale == 0 && _form != Form.HeaderFlag && b.IndexOfAnyExcept((byte)0) < 0) { value = _viewDefault; return Outcome.Value; }
        switch (_form)
        {
            case Form.HeaderFlag:
                bool set = (BinaryPrimitives.ReadUInt32LittleEndian(b) & _bit) != 0;
                value = !set && _zeroIsDefault ? _viewDefault : set ? 1L : 0L;
                return Outcome.Value;
            case Form.P2Int32: value = DirectColumn.Point(new P2Int(BinaryPrimitives.ReadInt32LittleEndian(b), BinaryPrimitives.ReadInt32LittleEndian(b[4..]))); return Outcome.Value;
            case Form.Bytes: value = "0x" + Convert.ToHexString(b); return Outcome.Value;
            case Form.Guid: value = new Guid(b).ToString(); return Outcome.Value;
            case Form.Color: value = DirectColumn.Plain(b.ReadColor(_size == 4 ? ColorBinaryType.Alpha : ColorBinaryType.NoAlpha)); return Outcome.Value;
            case Form.Link:
            {
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(b);
                if (id == 0) return Outcome.Value;
                // FFFFFFFF where the XML says maxIsNone: Mutagen's FormKey.None, which its serializer writes as "None"
                // (DirectColumn gives the same)
                if (id == uint.MaxValue && _maxIsNone) { value = Mutagen.Bethesda.Plugins.FormKey.NoneStr; return Outcome.Value; }
                if (plugin.Key(id) is not { } key) return Outcome.Mutagen;
                value = key;
                return Outcome.Value;
            }
            case Form.Signed: value = _size switch { 1 => (long)(sbyte)b[0], 2 => BinaryPrimitives.ReadInt16LittleEndian(b), 4 => BinaryPrimitives.ReadInt32LittleEndian(b), _ => BinaryPrimitives.ReadInt64LittleEndian(b) }; return Outcome.Value;
            // A uint64 as DirectColumn gives it: a long, or a double past long's range
            case Form.Unsigned when _size == 8: { ulong u = BinaryPrimitives.ReadUInt64LittleEndian(b); value = u <= long.MaxValue ? (object)(long)u : (double)u; return Outcome.Value; }
            case Form.Unsigned: value = _size switch { 1 => (long)b[0], 2 => BinaryPrimitives.ReadUInt16LittleEndian(b), _ => (long)BinaryPrimitives.ReadUInt32LittleEndian(b) }; return Outcome.Value;
            case Form.Bool: { bool any = false; foreach (byte x in b) any |= x != 0; value = any ? 1L : 0L; return Outcome.Value; }
            case Form.Float:
            {
                float f = Scale(BinaryPrimitives.ReadSingleLittleEndian(b));
                value = _scale != 0 && _zeroIsDefault && BitConverter.SingleToInt32Bits(f) == 0 ? _viewDefault : DirectColumn.Real(f);
                return Outcome.Value;
            }
            case Form.Double: value = DirectColumn.Real(BinaryPrimitives.ReadDoubleLittleEndian(b)); return Outcome.Value;
            case Form.Half: value = DirectColumn.Real(BinaryPrimitives.ReadHalfLittleEndian(b)); return Outcome.Value;
            case Form.P2Float: return Scaled(new P2Float(Scale(F(b, 0)), Scale(F(b, 4))), out value);
            case Form.P3Float: return Scaled(new P3Float(Scale(F(b, 0)), Scale(F(b, 4)), Scale(F(b, 8))), out value);
            case Form.P2Int16: value = DirectColumn.Point(_swap ? new P2Int16(S(b, 2), S(b, 0)) : new P2Int16(S(b, 0), S(b, 2))); return Outcome.Value;
            case Form.P3Int16: value = DirectColumn.Point(new P3Int16(S(b, 0), S(b, 2), S(b, 4))); return Outcome.Value;
            case Form.Enum:
            {
                // The stored number as the enum's own type reads it: a uint-based enum's bit 31 is no negative number
                bool unsigned = Type.GetTypeCode(Enum.GetUnderlyingType(_enum!)) is TypeCode.Byte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64;
                long n = size switch { 1 => b[0], 2 => BinaryPrimitives.ReadUInt16LittleEndian(b), 4 => unsigned ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadInt32LittleEndian(b), _ => BinaryPrimitives.ReadInt64LittleEndian(b) };
                if (_drop != 0)
                {
                    n &= ~(long)_drop;
                    // Left out by the serializer as its type's default once the dropped bits are gone
                    if (n == 0 && _zeroIsDefault) { value = _viewDefault; return Outcome.Value; }
                }
                var e = (Enum)Enum.ToObject(_enum!, n);
                value = _number ? FlagsRaw.Number(e) : DirectColumn.EnumText(e);
                return Outcome.Value;
            }
        }
        return Outcome.Mutagen;
    }

    static bool Swapped(FieldDef d) => d.Note != null && d.Note.Contains(LoquiXml.SwapCoordsNote, StringComparison.Ordinal);

    static float F(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadSingleLittleEndian(b[at..]);

    /// <summary>The stored float as Mutagen's value: scaled as its layout says (float arithmetic, as Mutagen's code).</summary>
    float Scale(float f) => _scale == 0 ? f : _scale > 0 ? f * _scale : f / -_scale;

    /// <summary>A scaled point: the serializer leaves it out when it equals its default (all zeros, by bits), which the
    /// view gives as its default.</summary>
    Outcome Scaled(object point, out object? value)
    {
        bool zero = point switch
        {
            P2Float p => BitConverter.SingleToInt32Bits(p.X) == 0 && BitConverter.SingleToInt32Bits(p.Y) == 0,
            P3Float p => BitConverter.SingleToInt32Bits(p.X) == 0 && BitConverter.SingleToInt32Bits(p.Y) == 0 && BitConverter.SingleToInt32Bits(p.Z) == 0,
            _ => false,
        };
        value = zero && _zeroIsDefault ? _viewDefault : DirectColumn.Point(point);
        return Outcome.Value;
    }
    static short S(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadInt16LittleEndian(b[at..]);
}

/// <summary>A plugin as the raw columns read it: its FormIDs resolved through its own masters, to FormKeys written as
/// Mutagen writes them. One per plugin and read; used by many threads.</summary>
sealed class RawPlugin
{
    readonly FormIdResolver _resolver;
    readonly string[] _owners;
    /// <summary>For translated texts (GAPS S152): the plugin's session (its strings lookup and the language Mutagen
    /// reads it in) and the query's language. Null: a translated text is read through Mutagen.</summary>
    readonly PluginSession? _session;
    readonly string? _language;
    readonly Mutagen.Bethesda.Strings.Language? _asked;

    /// <summary>The plugin keeps its translated texts in strings files: such a text in a record is a 4-byte ID.</summary>
    public bool Localized { get; }

    /// <summary>A text inside a subrecord as Mutagen's StringBinaryTranslation reads one of this form (the XML's binaryType)
    /// in an encoding: up to its first zero, or the length in front of it.</summary>
    internal static string Decode(ReadOnlySpan<byte> b, string form, Mutagen.Bethesda.Strings.IMutagenEncoding encoding) => form switch
    {
        "PrependLength" => BinaryStringUtility.ToZString(b[4..], encoding),
        "PrependLengthWithNullIfContent" => BinaryStringUtility.ProcessWholeToZString(b[4..], encoding),
        "PrependLengthUShort" => BinaryStringUtility.ToZString(b[2..], encoding),
        "PrependLengthUInt8" => BinaryStringUtility.ToZString(b[1..], encoding),
        _ => BinaryStringUtility.ProcessWholeToZString(b, encoding),
    };

    /// <summary>A translated text inside a subrecord (a list entry's, GAPS S153): a localized plugin's 4-byte ID, as
    /// <see cref="TryText"/> reads it; another plugin's text by its form in Mutagen's NonLocalized encoding.</summary>
    internal bool TryTextAt(ReadOnlySpan<byte> b, string form, Mutagen.Bethesda.Strings.StringsSource table, out string? text)
    {
        text = null;
        if (_session == null) return false;
        if (_session.Localized) return TryText(b, table, out text);
        text = Decode(b, form, Mutagen.Bethesda.Plugins.Meta.GameConstants.Starfield.Encodings.NonLocalized);
        return true;
    }

    /// <summary>
    /// Mutagen's own object of a record's component (its BFCB … BFCE at the record's own level, the <paramref name="k"/>-th),
    /// read alone through the plugin's record reader (PluginSession.ReadBytes: a record of the type holding only that
    /// component): for a hand-read layout's TypeSwitch the bytes don't decide (LinkByteWalker.WalkValues). Null without a
    /// session, or when the record has no such component or Mutagen can't read it.
    /// </summary>
    internal object? Component(RawRecord raw, int k, string typeName)
    {
        if (_session == null) return null;
        int depth = 0, n = -1, begin = -1, end = -1;
        foreach (var s in raw.Subrecords)
        {
            if (s.Signature == "BFCB") { if (depth == 0 && ++n == k) begin = s.LongForm ? s.HeaderStart - 10 : s.HeaderStart; depth++; }
            else if (s.Signature == "BFCE" && depth > 0) { depth--; if (depth == 0 && n == k && begin >= 0) { end = s.Start + s.Length; break; } }
        }
        if (begin < 0 || end < begin) return null;
        var bytes = new byte[RawRecord.HeaderLength + end - begin];
        raw.Header.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(end - begin));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), raw.Flags & ~RawRecord.CompressedFlag);
        raw.Data.AsSpan(begin, end - begin).CopyTo(bytes.AsSpan(RawRecord.HeaderLength));
        try
        {
            var record = _session.ReadBytes(typeName, bytes);
            var components = record.GetType().GetProperty("Components")?.GetValue(record) as System.Collections.IEnumerable;
            return components?.Cast<object?>().FirstOrDefault();
        }
        catch (RecordException) { return null; }
    }

    /// <param name="session">The plugin's session, whose strings lookup Mutagen's reads use; with
    /// <paramref name="language"/>, the query's language, translated texts are read from the bytes too.</param>
    public RawPlugin(IndexedPlugin plugin, PluginSession? session = null, string? language = null)
    {
        Localized = plugin.Header is { Localized: true };
        _resolver = FormIdResolver.For(plugin);
        _owners = (plugin.Header?.Masters ?? Array.Empty<string>()).Append(plugin.Name).ToArray();
        if (session != null && language != null)
        {
            _session = session; _language = language;
            if (Enum.TryParse<Mutagen.Bethesda.Strings.Language>(language, true, out var l) && l.ToString().Equals(language, StringComparison.OrdinalIgnoreCase)) _asked = l;
        }
    }

    /// <summary>
    /// A translated text as DirectColumn gives it from Mutagen's TranslatedString of this plugin, in the query's language
    /// (DirectColumn.Text, then the CK tag taken off), from the subrecord's own bytes. False when only Mutagen can say
    /// (no session or language; a localized text that isn't a 4-byte ID, which Mutagen refuses).
    /// <list type="bullet">
    /// <item>A plugin that isn't localized: the stored text, read as Mutagen reads it (whole, its NonLocalized encoding),
    /// whatever the language: Mutagen's TranslatedString holds one language (NumLanguages 1), and DirectColumn gives it.</item>
    /// <item>A localized one: the 4-byte string ID; 0 is Mutagen's TranslatedString with no text (null). Else Mutagen's
    /// TranslatedString resolves every language its lookup has a table of (AvailableLanguages of the field's table) and
    /// keeps those whose table holds the ID: NumLanguages is how many. With one or none DirectColumn gives String, the
    /// text in Mutagen's own language (the session's TargetLanguage; null when that table hasn't the ID); with two or
    /// more it gives the asked language's text, null when its table hasn't the ID. When the asked language is the
    /// session's, both are that table's text: one lookup.</item>
    /// </list>
    /// </summary>
    public bool TryText(ReadOnlySpan<byte> content, Mutagen.Bethesda.Strings.StringsSource table, out string? text)
    {
        text = null;
        if (_session == null) return false;
        if (!_session.Localized)
        {
            text = BinaryStringUtility.ProcessWholeToZString(content, Mutagen.Bethesda.Plugins.Meta.GameConstants.Starfield.Encodings.NonLocalized);
            return true;
        }
        if (content.Length != 4 || _session.StringsLookup is not { } lookup) return false;
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(content);
        text = id == 0 ? null : LookupText(lookup, table, _session.TargetLanguage, _asked, id);
        return true;
    }

    /// <summary>
    /// The text DirectColumn gives for a localized string ID (not 0) from Mutagen's TranslatedString of it (TargetLanguage
    /// <paramref name="target"/>, the strings lookup <paramref name="lookup"/>) in the asked language (null: a name that
    /// isn't one of Mutagen's languages): see <see cref="TryText"/>. Each language's table is asked only as far as the
    /// answer needs: with the asked language Mutagen's own, that table alone.
    /// </summary>
    internal static string? LookupText(Mutagen.Bethesda.Strings.IStringsFolderLookup lookup, Mutagen.Bethesda.Strings.StringsSource table, Mutagen.Bethesda.Strings.Language target, Mutagen.Bethesda.Strings.Language? asked, uint id)
    {
        bool inTarget = lookup.TryLookup(table, target, id, out var targetText);
        if (asked == target) return inTarget ? targetText : null;
        string? askedText = null;
        bool inAsked = asked is { } a && lookup.TryLookup(table, a, id, out askedText);
        // How many languages hold the ID: only whether it is more than one matters
        int count = (inTarget ? 1 : 0) + (inAsked ? 1 : 0);
        if (count == 1)
            foreach (var l in lookup.AvailableLanguages(table))
            {
                if (l == target || l == asked) continue;
                if (lookup.TryLookup(table, l, id, out _)) { count = 2; break; }
            }
        return count <= 1 ? (inTarget ? targetText : null) : inAsked ? askedText : null;
    }

    /// <summary>The FormKey a FormID of this plugin stands for, as text; null when a missing master leaves it open.</summary>
    public string? Key(uint formId)
    {
        if (!_resolver.TryResolve(formId, out int owner, out uint local) || owner < 0 || owner >= _owners.Length) return null;
        return local.ToString("X6", CultureInfo.InvariantCulture) + ":" + _owners[owner];
    }
}

/// <summary>Several raw columns of one record, read in one pass over its subrecords.</summary>
sealed class RawRecordColumns
{
    readonly RawColumn[] _columns;
    readonly uint[] _subs;
    const uint XXXX = 0x58585858, BFCB = 0x42434642, BFCE = 0x45434642;

    /// <summary>The one marker object the columns read (0: none), and its subrecords in its fields' order.</summary>
    readonly uint _marker;
    readonly uint[] _run = Array.Empty<uint>();
    readonly bool[] _runRepeat = Array.Empty<bool>();
    readonly uint _end;
    readonly bool _twoMarkers;

    /// <summary>The columns the record's value walk places (RawColumn.Walked), their paths, and what the walk is asked for.</summary>
    readonly int[] _walked;
    readonly LinkFields? _walkFields;
    readonly RecordSchema? _walkSchema;
    readonly Func<string, bool>? _walkWant;
    [ThreadStatic] static LinkByteWalker? t_walker;

    public RawRecordColumns(RawColumn[] columns)
    {
        _columns = columns;
        _walked = Enumerable.Range(0, columns.Length).Where(i => columns[i].Walked != null).ToArray();
        if (_walked.Length > 0)
        {
            var (schema, type, _) = columns[_walked[0]].Walked!.Value;
            _walkSchema = schema;
            _walkFields = LinkByteWalker.FieldsOf(schema, type);
            // Each field's path and the objects on its way (a gendered pair's Voices for Voices.Male)
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (int i in _walked)
            {
                string path = columns[i].Walked!.Value.Path;
                paths.Add(path);
                for (int dot = path.IndexOf('.'); dot > 0; dot = path.IndexOf('.', dot + 1)) paths.Add(path[..dot]);
            }
            _walkWant = paths.Contains;
        }
        _subs = columns.Where(c => !c.InHeader && c.Marker == 0 && c.Walked == null).SelectMany(c => c.ObjectSubrecords.Prepend(c.Subrecord)).Distinct().ToArray();
        var markers = columns.Where(c => c.Marker != 0).Select(c => c.Marker).Distinct().ToList();
        // Columns of two marker objects: not read from the bytes together (every record goes to Mutagen)
        _twoMarkers = markers.Count > 1;
        if (markers.Count == 1)
        {
            var first = columns.First(c => c.Marker != 0);
            _marker = markers[0]; _run = first.RunSubrecords; _end = first.EndMarker;
            _runRepeat = first.RunRepeats.Length == _run.Length ? first.RunRepeats : new bool[_run.Length];
        }
    }

    public int Count => _columns.Length;

    /// <summary>The values of every column for one record's plain data; false when one of them must come from Mutagen.</summary>
    public bool Read(ReadOnlySpan<byte> head, ReadOnlySpan<byte> data, RawPlugin plugin, object?[] values)
    {
        Span<int> start = stackalloc int[_subs.Length], length = stackalloc int[_subs.Length];
        start.Fill(-1);
        if (_twoMarkers) return false;
        Span<int> runStart = stackalloc int[_run.Length], runLength = stackalloc int[_run.Length];
        runStart.Fill(-1);
        bool markerSeen = false, inRun = false; int runLast = -1;
        int p = 0, depth = 0; uint big = 0; bool hasBig = false;
        while (p + 6 <= data.Length)
        {
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(data[p..]);
            int n = BinaryPrimitives.ReadUInt16LittleEndian(data[(p + 4)..]);
            p += 6;
            if (sig == XXXX && n == 4 && p + 4 <= data.Length) { big = BinaryPrimitives.ReadUInt32LittleEndian(data[p..]); hasBig = true; p += 4; continue; }
            long len = hasBig ? big : n; hasBig = false;
            if (p + len > data.Length) return false;
            if (sig == BFCB) { depth++; inRun = false; }
            else if (sig == BFCE) { if (depth > 0) depth--; }
            else if (depth == 0 && inRun && _end != 0 && sig == _end) inRun = false;
            else if (depth == 0 && inRun && _end != 0)
            {
                // Between its markers every subrecord is the object's, each once
                int ei = Array.IndexOf(_run, sig);
                if (ei < 0 || runStart[ei] >= 0) return false;
                runStart[ei] = p; runLength[ei] = (int)len;
            }
            else if (depth == 0 && _marker != 0 && sig == _marker)
            {
                // The object twice: Mutagen decides which it keeps
                if (markerSeen) return false;
                markerSeen = inRun = true; runLast = -1;
                // An object whose first field is its start (a patrol's XPRD), not an empty marker
                if (_run.Length > 0 && _run[0] == sig) { runStart[0] = p; runLength[0] = (int)len; runLast = 0; }
            }
            else if (depth == 0 && inRun && Array.IndexOf(_run, sig) is int ri && (ri > runLast || ri == runLast && _runRepeat[ri]))
            {
                if (!_runRepeat[ri]) { runStart[ri] = p; runLength[ri] = (int)len; }
                runLast = ri;
            }
            else if (depth == 0)
            {
                inRun = false;
                int k = Array.IndexOf(_subs, sig);
                if (k >= 0)
                {
                    // Twice at the record's own level: Mutagen decides which it keeps
                    if (start[k] >= 0) return false;
                    start[k] = p; length[k] = (int)len;
                }
            }
            p += (int)len;
        }
        if (p != data.Length) return false;
        // An object whose end marker never came
        if (inRun && _end != 0) return false;
        // The columns the value walk places: the subrecord it gives each field (GAPS S165)
        if (_walked.Length > 0 && !ReadWalked(head, data, plugin, values)) return false;
        for (int i = 0; i < _columns.Length; i++)
        {
            if (_columns[i].Walked != null) continue;
            // A field of the record's header: its 24 bytes as the file stores them
            if (_columns[i].InHeader)
            {
                if (head.Length < 24 || _columns[i].Read(head, true, plugin, out values[i]) != RawColumn.Outcome.Value) return false;
                continue;
            }
            if (_columns[i].Marker != 0)
            {
                int ri = Array.IndexOf(_run, _columns[i].Subrecord);
                if (!markerSeen) { values[i] = null; continue; }
                if (runStart[ri] < 0)
                {
                    if (_columns[i].RunAbsentToMutagen) return false;
                    values[i] = _columns[i].RunAbsent;
                    continue;
                }
                if (_columns[i].Read(data.Slice(runStart[ri], runLength[ri]), true, plugin, out values[i]) != RawColumn.Outcome.Value) return false;
                continue;
            }
            int k = Array.IndexOf(_subs, _columns[i].Subrecord);
            if (start[k] < 0 && _columns[i].ObjectSubrecords.Length > 0)
            {
                bool there = false;
                foreach (uint os in _columns[i].ObjectSubrecords) there |= start[Array.IndexOf(_subs, os)] >= 0;
                values[i] = _columns[i].Absent(there);
                continue;
            }
            var content = start[k] >= 0 ? data.Slice(start[k], length[k]) : ReadOnlySpan<byte>.Empty;
            if (_columns[i].IsPlanned)
            {
                // Placed by a layout's plan, which reads by the record's form version (its header's bytes 20 and 21)
                if (head.Length < 24 || _columns[i].ReadPlanned(content, start[k] >= 0, BinaryPrimitives.ReadUInt16LittleEndian(head[20..]), plugin, out values[i]) != RawColumn.Outcome.Value) return false;
                continue;
            }
            if (_columns[i].Read(content, start[k] >= 0, plugin, out values[i]) != RawColumn.Outcome.Value) return false;
        }
        return true;
    }

    /// <summary>The walked columns of one record: the record's value walk, asked only for their fields, gives the
    /// subrecord of each (none: absent); a part it didn't read at or above one sends the record to Mutagen.</summary>
    bool ReadWalked(ReadOnlySpan<byte> head, ReadOnlySpan<byte> data, RawPlugin plugin, object?[] values)
    {
        var raw = RawRecord.FromInflated(head, data);
        var walker = t_walker;
        if (walker == null || walker.Schema != _walkSchema) t_walker = walker = new LinkByteWalker(_walkSchema!);
        var found = new WalkedFields();
        walker.WalkValues(_walkFields!, raw, plugin.Localized, found, want: _walkWant);
        foreach (int i in _walked)
        {
            string path = _columns[i].Walked!.Value.Path;
            if (found.Unread.Any(u => u.Length == 0 || u == path)) return false;
            if (!found.Values.TryGetValue(path, out var v)) { if (_columns[i].Read(ReadOnlySpan<byte>.Empty, false, plugin, out values[i]) != RawColumn.Outcome.Value) return false; continue; }
            var content = raw.Content(raw.Subrecords[v.Sub]);
            if (v.End > content.Length || v.At > v.End) return false;
            if (_columns[i].Read(content[v.At..v.End], true, plugin, out values[i]) != RawColumn.Outcome.Value) return false;
        }
        return true;
    }

    /// <summary>What a value walk asked for a record's own fields gave: each field's subrecord, the parts unread.</summary>
    sealed class WalkedFields : IRecordValueSink
    {
        public readonly Dictionary<string, (int Sub, int At, int End)> Values = new(StringComparer.Ordinal);
        public readonly List<string> Unread = new();
        public void Object(string path, string kind) { }
        public void Value(string path, FieldDef field, int subrecord, int at, int end) => Values[path] = (subrecord, at, end);
        void IRecordValueSink.Unread(string path, string why) => Unread.Add(path);
    }
}
