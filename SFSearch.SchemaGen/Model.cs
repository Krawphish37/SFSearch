// The schema in memory: every object Mutagen's XML defines, each with its own fields stored once, and the two things
// worked out on request: a record type's fields spelled out as paths (Expand), and one path looked up (Resolve).
// Nothing in this file needs Mutagen: it is filled either from the XML (LoquiXml) or from a finished schema.db (SchemaDb).
namespace SFSearch.SchemaGen;

public sealed class ObjDef
{
    public string Name = "";
    public string? Sig, Base, File;
    public string ObjType = "Subrecord";
    public bool Abstract;
    /// <summary>True when no field of the object (or of its bases) starts a subrecord of its own: the object is one
    /// run of bytes inside whatever subrecord its user puts it in.</summary>
    public bool Struct;
    public int? FixedSize;
    /// <summary>The XML's <c>markerType</c>: an empty subrecord written before the object's own (HNAM before head tracking).</summary>
    public string? Marker;
    /// <summary>The XML's <c>endMarkerType</c>: an empty subrecord that ends the object (BFCE ends a component).</summary>
    public string? EndMarker;
    /// <summary>The XML's <c>AdditionalContainedRecordType</c>: other subrecords the object holds, read by hand-written
    /// code (a condition's CITC, CIS1, CIS2). The XML doesn't say where they stand.</summary>
    public List<string> Contains = new();
    /// <summary>Null when Mutagen's generated reader lays the whole object out. Otherwise the hand-written Mutagen code
    /// that reads it, in part or whole, as compiled methods (<c>ConditionBinaryCreateTranslation.FillBinaryFlagsCustom</c>),
    /// with the field through which another object's code reads it (<c>via PerkRank.Effects</c>), joined by "; ". The
    /// schema gives no offset for a field that code places (GAPS S36).</summary>
    public string? HandRead;
    /// <summary>A record type's name in xEdit ("Weapon", TES4 "Main File Header"): <c>inputs/xedit-record-names-*.tsv</c>
    /// (GAPS S98). Null for other objects and for a record type xEdit doesn't define.</summary>
    public string? XEditName;
    public List<string> LinkInterfaces = new();
    public List<FieldDef> Fields = new();
    /// <summary>The XML's layout attributes on an object (C186): <c>SelectedBy</c> (what picks among an abstract
    /// object's kinds: "field:Flags:bit=2", "subrecord:PRKE:UInt8"), <c>SelectorValue</c> (the value that picks this
    /// kind: "1", "Quest", "*"), <c>ConditionFunction</c> (a condition data object: the CTDA function index that
    /// picks it). Null when the XML gives none.</summary>
    public string? SelectedBy, SelectorValue, ConditionFunction;
    /// <summary>The XML's <c>selectorEnum</c> (C57 W3/W4): the enum whose member names the <see cref="SelectorValue"/>s of the
    /// object's kinds are (<c>AComponent.ComponentType</c>: a component's BFCB text). Null when the values are numbers.</summary>
    public string? SelectorEnum;
    /// <summary>The XML's <c>engineClass</c> (C57 W15): the engine's reflected class the object is laid out as
    /// (<c>BGSAtmosphere</c>). Null for most objects.</summary>
    public string? EngineClass;
    /// <summary>The XML's <c>shortCircuitToLastRequired</c> ("ToLastRequired"): in a list, an item reads its fields in any
    /// order until its last field that is never null has been read; any field after that starts the next item (a race's
    /// subgraphs). Null for the usual rule, where an item ends at a field it already had or one before it.</summary>
    public string? ShortCircuit;
    /// <summary>The engine's reflected class the object holds (its REFL is a value of it): <c>BGSAtmosphere</c>. Null for
    /// most objects. The XML's <see cref="EngineClass"/> found in the CK's names (to-do F30, C57 W15).</summary>
    public string? CkClass;
    /// <summary>The Creation Kit's display name for it: Mutagen's XML (ckName, C220, fixes.17 on), else the CK's names
    /// for <see cref="CkClass"/>. Null without either.</summary>
    public string? CkName;

    /// <summary>Only used while the XML is read: the XML's <c>customBinaryEnd</c> (hand-written code reads what
    /// follows the generated fields).</summary>
    internal string? CustomEnd;
    /// <summary>Only used while the XML is read: the XML's <c>customBinaryEndFills</c> (C60 W5), the fields that code fills.</summary>
    internal string? CustomEndFills;
    /// <summary>Only used while the schema is built: the XML's <c>handRead</c> and <c>handReadFills</c> (C73, fixes.22 on):
    /// the static method of the object's class that reads the whole object by hand (UnknownComponent's <c>ReadRaw</c>), and
    /// the fields it fills.</summary>
    internal string? XmlHandRead, XmlHandReadFills;
    /// <summary>Only used while the schema is built: the hand-read fields of other objects this object is entered
    /// through (<c>PerkRank.Effects</c>), when no generated field enters it.</summary>
    internal List<string> EnteredVia = new();
}

public sealed class FieldDef
{
    public string Object = "";
    public int Ord;
    /// <summary>Path inside the object: the property name, or a part of it (<c>Keywords[*]</c>, <c>WorldModel.Male</c>).</summary>
    public string Path = "";
    /// <summary>The Mutagen property name.</summary>
    public string Name = "";
    public string Kind = "";
    /// <summary>The field's own subrecord (its own signature, or its data block's). Null: it sits in its user's subrecord.</summary>
    public string? Subrecord;
    public int? Offset, Size;
    /// <summary>True: Offset counts from the start of the object. False: from the start of the subrecord.</summary>
    public bool OffsetRel;
    /// <summary>For object fields: the object's bytes begin a subrecord (or a list item), so its fields count from 0.</summary>
    public bool StartsSubrecord;
    public bool Nullable, InList, Translated, Unknown, Custom, Property;
    public string? RefObject, LinkTo, Enum, Default, SigMap, GenericArg, ClrType, Readable, XEdit, Unit, Note;
    /// <summary>The XML's <c>markerType</c> and <c>endMarkerType</c> on a field: empty subrecords written before and
    /// after the field's own (POBA before a package's OnBegin, ECHD … ECHE around an echo marker).</summary>
    public string? Marker, EndMarker;
    /// <summary>A translated text: the strings table Mutagen reads it from (the XML's <c>translated</c>: <c>Normal</c>
    /// = .strings, <c>DL</c> = .dlstrings, <c>IL</c> = .ilstrings). Null for any other field (GAPS S35).</summary>
    public string? StringsTable;
    /// <summary>A list: how many bytes its count takes (the XML's <c>counterLength</c>; 4 for a list counted by a counter
    /// subrecord that names no length, as Mutagen's generator sets it). The count is written just before the entries,
    /// or, for a list counted by a subrecord (<c>counted by</c> in the note), is that subrecord. Null: no count is
    /// stored, the entries run to the end of their subrecord or each is a subrecord of its own (GAPS S37).</summary>
    public int? CounterLength;
    /// <summary>A text (String or AssetLink): how Mutagen stores it, the XML's <c>binaryType</c> or Mutagen's default
    /// <c>NullTerminate</c>: <c>NullTerminate</c> (the text and a zero byte; in a subrecord of its own, the whole
    /// subrecord), <c>Plain</c> (no end mark), <c>PrependLength</c> (a 4-byte length before it),
    /// <c>PrependLengthWithNullIfContent</c> (a 4-byte length, the length counting a zero byte at the end when there is
    /// text), <c>PrependLengthUShort</c> (2-byte length), <c>PrependLengthUInt8</c> (1-byte length),
    /// <c>NullPaddedFixedLength</c> (a slot of Size bytes padded with zeros). A translated text in a localized plugin
    /// is a 4-byte string ID instead (GAPS S37).</summary>
    public string? TextForm;
    public int XEditPairs;
    /// <summary>Null for a field Mutagen's XML declares. Otherwise what makes the property, for one Mutagen adds beyond
    /// its XML (Code-Generation.md, GAPS Part 2 row 8): a generator rule applied to the XML (<c>&lt;Break /&gt;</c> gives
    /// <c>Versioning</c>, a data block with a break or optional="true" gives <c>&lt;Sig&gt;DataTypeState</c>,
    /// majorFlag="true" gives <c>MajorFlags</c>) or hand-written code (<c>IsCompressed</c>, <c>IsDeleted</c>,
    /// <c>RegionData.DataType</c>). Such a field holds no bytes of its own except RegionData.DataType.</summary>
    public string? Source;
    /// <summary>True for a property Mutagen's record class has no setter for (RegionData.DataType: each kind returns its
    /// own value), which the serializer therefore never writes: the record's JSON never holds it, so it must be read
    /// from the record object.</summary>
    public bool ReadOnly;
    /// <summary>The Creation Kit's display name for the field: Mutagen's XML (ckName, C220, fixes.17 on), else the CK's
    /// dump for <see cref="CkField"/>. Null without either (to-do F30).</summary>
    public string? CkName;
    /// <summary>The reflected class's field this Mutagen field is (<c>ExtraBendableSplineParams::ParamData.fThickness</c>),
    /// from the XML's engineField on a field of an object with an engineClass (C57 W15). Null for most fields.</summary>
    public string? CkField;
    /// <summary>The XML's <c>engineField</c> (the field's name in its object's engine class) and <c>engineEnum</c> (on an
    /// enum field: the engine's reflected enum whose members are its members by value), C57 W15. Null for most fields.</summary>
    public string? EngineField, EngineEnum;
    /// <summary>The XML's <c>selectorEnum</c> on a field (C57 W3): the enum whose member names its kinds' selector values
    /// are. Null for most fields.</summary>
    public string? SelectorEnum;
    /// <summary>
    /// The subrecords Mutagen's hand-written code reads (GAPS S38), comma-joined in the XML's order. On a CustomLogic
    /// step: the XML's <c>recordType</c> and its <c>&lt;RecordType&gt;</c> items (a step inside a subrecord: that
    /// subrecord). On a field that a step fills (<see cref="FilledBy"/>): the subrecords of those steps, so a field such
    /// as a PERS's <c>Items</c> (from DAT2, with XXXX long forms) has its signatures in the schema. Null otherwise.
    /// </summary>
    public string? CustomSubrecords;
    /// <summary>On a CustomLogic step: the object's fields (and its kinds' fields) its hand-written reader fills,
    /// comma-joined, as the XML's <c>fills=</c> names them (fixes.18 on, C36/C44; before, read from the compiled IL).
    /// Null when the code fills none of the object's fields.</summary>
    public string? CustomFills;
    /// <summary>On a CustomLogic step: the XML's <c>fills=</c> as given (dotted paths such as "Clouds.XSpeed"; "" when the
    /// step fills nothing) and <c>writtenFrom=</c> (what the writer makes again what such a step reads from: count:List,
    /// marker, class, field:Field). Not stored.</summary>
    public string? XmlFills, WrittenFrom;
    /// <summary>A gendered value's rule, from the XML (C57 W3; Mutagen's Code-Generation.md, "Gendered values in the binary"):
    /// <c>GenderRecord</c>, the subrecord that says the gender before each half (genderEnumRecord: up to two, in any order,
    /// each followed by its half); <c>MaleMarker</c>/<c>FemaleMarker</c>, the empty subrecords before each half.</summary>
    public string? GenderRecord, MaleMarker, FemaleMarker;
    /// <summary>On a field: the CustomLogic steps whose compiled reader fills it, comma-joined. Null otherwise.</summary>
    public string? FilledBy;
    /// <summary>The signatures of <see cref="CustomSubrecords"/>, split.</summary>
    public IReadOnlyList<string> CustomSubrecordList => CustomSubrecords is { Length: > 0 } s ? s.Split(',') : Array.Empty<string>();
    /// <summary>
    /// Where Mutagen's hand-written code puts a field it reads itself: the XML's layout attributes (C186, fixes.14 on),
    /// which Mutagen's generator ignores. <c>LayoutOffset</c>: the byte offset in the subrecord's content ("0", "28", or
    /// "after:Traversals"); <c>LayoutBits</c>: the bits of that byte ("5-7", "1"); <c>LayoutSubrecord</c>: the
    /// subrecord, when not the field's own; <c>LayoutEncoding</c>: how the stored value maps to the field
    /// ("uint16/1000", "float/100", "uint8*10min"); <c>LayoutDroppedBits</c>: bits of the stored value Mutagen keeps
    /// elsewhere; <c>LayoutPresentWhen</c>: when the bytes are there ("field:Flags:bit=2:clear"); <c>LayoutGroup</c>:
    /// the child group the field comes from ("CellChildren"); <c>SelectedBy</c>: what picks the kind of object the field
    /// holds ("field:Flags:bit=2", "subrecord:PRKE:UInt8", "here:Int32"); <c>LayoutMap</c> (C196, fixes.15 on): a field
    /// not stored as such, which Mutagen derives from the stored number at its place ("1:Set,2:Add,3:Multiply": a perk
    /// effect's Modification from the entry point function in DATA). Null when the XML gives none.
    /// </summary>
    public string? LayoutOffset, LayoutBits, LayoutSubrecord, LayoutEncoding, LayoutDroppedBits, LayoutPresentWhen, LayoutGroup, SelectedBy, LayoutMap;
    /// <summary>On a link: the XML's <c>maxIsNone="true"</c>. A stored FormID of FFFFFFFF is no link there, which Mutagen
    /// holds as FormKey.None and its serializer writes as "None" (a dialogue response's Emotion, an equip type's
    /// ConditionActorValue); in any other link field the FormID is read as any other.</summary>
    public bool MaxIsNone;
    /// <summary>On a float (or a percent): the XML's <c>integerType</c>, <c>multiplier</c> and <c>divisor</c>, as written.
    /// The field is stored as a whole number of that type (UInt, UShort, Byte, ByteHundred), which Mutagen's
    /// FloatBinaryTranslation turns into the value (stored × multiplier ÷ divisor: a dialogue response's ResetHours, a
    /// uint16 × 24 ÷ 65535).</summary>
    public string? IntegerType, Multiplier, Divisor;
    /// <summary>On a list: the XML's <c>itemMarkerType</c> and <c>itemEndMarkerType</c>, the empty subrecords each item
    /// sits between (a sound echo marker's Markers: ECHO, the item's subrecords, ECHE; a perk's Ranks: PRRK to PRRF).
    /// Null for a list whose items have none.</summary>
    public string? ItemMarker, ItemEndMarker;

    /// <summary>The XML's layoutMap as stored number to Mutagen's value ("1:Set" gives 1 → "Set"). Empty without one.</summary>
    public IReadOnlyDictionary<long, string> LayoutMapEntries => LayoutMap is not { Length: > 0 } lm ? new Dictionary<long, string>()
        : lm.Split(',').Select(p => p.Split(':', 2)).ToDictionary(p => long.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), p => p[1]);

    /// <summary>The layout offset as a number, when it is one ("after:…" is not).</summary>
    public int? LayoutOffsetNumber => int.TryParse(LayoutOffset, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int o) ? o : null;

    /// <summary>The bits of the byte at <see cref="LayoutOffset"/> the field takes, as a mask and the lowest bit
    /// ("5-7": 0xE0 and 5; "1": 0x02 and 1). Null without <see cref="LayoutBits"/>.</summary>
    public (byte Mask, int Shift)? LayoutMask
    {
        get
        {
            if (LayoutBits is not { Length: > 0 } b) return null;
            var parts = b.Split('-');
            if (!int.TryParse(parts[0], out int lo) || !int.TryParse(parts[^1], out int hi) || lo < 0 || hi > 7 || hi < lo) return null;
            byte mask = 0;
            for (int i = lo; i <= hi; i++) mask |= (byte)(1 << i);
            return (mask, lo);
        }
    }

    /// <summary>The bits <see cref="LayoutDroppedBits"/> names, as a mask of the stored value ("1-3": 0x0E; "7": 0x80;
    /// "1,3": 0x0A). 0 without one, or when it isn't bit numbers.</summary>
    public uint LayoutDroppedMask
    {
        get
        {
            if (LayoutDroppedBits is not { Length: > 0 } d) return 0;
            uint mask = 0;
            foreach (string part in d.Split(','))
            {
                var ends = part.Split('-');
                if (!int.TryParse(ends[0], out int lo) || !int.TryParse(ends[^1], out int hi) || lo < 0 || hi > 31 || hi < lo) return 0;
                for (int i = lo; i <= hi; i++) mask |= 1u << i;
            }
            return mask;
        }
    }

    // Only used while the XML is read
    internal int Block;
    internal bool IsSub, HasOwnSig;
    /// <summary>The XML's <c>binary</c> (Normal when absent): NoGeneration fields take no bytes where the generated reader
    /// reads; Custom ones take their byteLength.</summary>
    internal string Binary = "Normal";
    internal int? ByteLength;
}

/// <summary>One field at its path in one record type (or in one object described by itself).</summary>
public sealed class FieldRow
{
    public string Sig = "", Type = "", Path = "", PlainPath = "", Sort = "";
    public FieldDef Def = null!;
    public string? Subrecord, Enum, Map, ChildGen;
    public int? Offset, ChildBase;
    public bool InList;
    public int Depth;
    public string Stack = "";
    /// <summary>The object field this row sits under (null at the top of the record).</summary>
    public FieldRow? Parent;
}

public sealed record EnumMember(string Name, long Value, bool IsFlags)
{
    public string? XEditName { get; set; }
    /// <summary>The Creation Kit's display name for the member, and the reflected enum's member it is
    /// (<c>BGSBodyPartDefs::LIMB_ENUM.LIMB_FLY_GRAB</c>), where a field of the enum has the XML's engineEnum (C57 W15).</summary>
    public string? CkName { get; set; }
    public string? CkMember { get; set; }
}

public sealed record Target(string Name, string Marker, int Idx);

public sealed class SchemaModel
{
    public const int BigVariantLimit = 100;
    public const int MaxDepth = 10;

    public readonly Dictionary<string, ObjDef> Objects = new(StringComparer.Ordinal);
    public readonly List<ObjDef> RecordTypes = new();
    public readonly Dictionary<string, List<EnumMember>> Enums = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> Meta = new(StringComparer.Ordinal);
    /// <summary>The layouts Mutagen reads by hand and the record XML can't describe (the package's
    /// <c>Layouts\HandReadLayouts.xml</c>, C186): one entry per Record, AnyRecord and Define, in the file's order.</summary>
    public readonly List<HandLayout> HandLayouts = new();
    /// <summary>The Creation Kit's names for the engine's reflected classes, fields and enum members (to-do F30), from
    /// inputs\ck-names-&lt;CK version&gt;.tsv. Empty only in a schema built with --ck-names none.</summary>
    public CkData Ck = new();

    // Every object's fields (bases first), kinds and targets, made by Link and only read after it: the model is safe to
    // walk from many threads at once with no lock (GAPS S65). Filling them all takes about 1 ms.
    readonly Dictionary<string, List<string>> _children = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<(FieldDef F, int Level)>> _allFields = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<Target>> _targets = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<string>> _variants = new(StringComparer.Ordinal);

    /// <summary>Call once every object is in <see cref="Objects"/> with all its fields, before the model is used: it makes
    /// every object's tables. Nothing changes them after it, so many threads may use the model at once.</summary>
    public void Link()
    {
        _children.Clear(); _allFields.Clear(); _targets.Clear(); _variants.Clear(); RecordTypes.Clear();
        foreach (var o in Objects.Values.OrderBy(o => o.Name, StringComparer.Ordinal))
            if (o.Base != null && Objects.ContainsKey(o.Base))
                (_children.TryGetValue(o.Base, out var l) ? l : _children[o.Base] = new()).Add(o.Name);
        RecordTypes.AddRange(Objects.Values.Where(o => o.ObjType == "Record" && !o.Abstract && o.Sig != null)
            .OrderBy(o => o.Sig, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal));
        foreach (var name in Objects.Keys)
        {
            _allFields[name] = MakeAllFields(name);
            _variants[name] = MakeVariants(name);
        }
        foreach (var name in Objects.Keys) _targets[name] = MakeTargets(name);
    }

    /// <summary>The end marker of an object: its own, else its nearest base's (AComponent's BFCE ends every component).</summary>
    public string? EndMarkerOf(string obj) => Ancestry(obj).Select(o => o.EndMarker).FirstOrDefault(x => x != null);

    /// <summary>The start marker of an object: its own, else its nearest base's.</summary>
    public string? MarkerOf(string obj) => Ancestry(obj).Select(o => o.Marker).FirstOrDefault(x => x != null);

    /// <summary>The other subrecords an object holds (its own and its bases'), in the XML's order.</summary>
    public IEnumerable<string> ContainsOf(string obj) => Ancestry(obj).SelectMany(o => o.Contains).Distinct();

    /// <summary>The hand-written Mutagen code that reads an object, its own and its bases' (<see cref="ObjDef.HandRead"/>),
    /// in words: "ConditionBinaryCreateTranslation.FillBinaryFlagsCustom; entered through PerkRank.Effects (…)". Null
    /// when Mutagen's generated reader reads it all (GAPS S36).</summary>
    public string? HandReadOf(string obj)
    {
        var parts = Ancestry(obj).Where(a => a.HandRead != null).SelectMany(a => a.HandRead!.Split("; ")).Distinct()
            .Select(p => p.StartsWith("via ", StringComparison.Ordinal) ? "entered through " + p[4..] : p).ToList();
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>The object and its bases, the object first.</summary>
    public IEnumerable<ObjDef> Ancestry(string obj)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (string? n = obj; n != null && Objects.TryGetValue(n, out var o) && seen.Add(n); n = o.Base) yield return o;
    }

    /// <summary>Every field of an object: its bases' fields first (the outermost base first), then its own.</summary>
    public IReadOnlyList<(FieldDef F, int Level)> AllFields(string obj) => _allFields.TryGetValue(obj, out var l) ? l : NoFields;

    static readonly List<(FieldDef F, int Level)> NoFields = new();

    List<(FieldDef F, int Level)> MakeAllFields(string obj)
    {
        var l = new List<(FieldDef F, int Level)>();
        var chain = Ancestry(obj).ToList();
        for (int level = chain.Count - 1; level >= 0; level--)
            foreach (var f in chain[level].Fields) l.Add((f, level));
        return l;
    }

    /// <summary>Every concrete object that derives from this one, by name.</summary>
    public IReadOnlyList<string> Variants(string obj) => _variants.TryGetValue(obj, out var l) ? l : Array.Empty<string>();

    List<string> MakeVariants(string obj)
    {
        var l = new List<string>();
        void Add(string n, int guard)
        {
            if (guard > 50 || !_children.TryGetValue(n, out var kids)) return;
            foreach (var k in kids) { if (!Objects[k].Abstract) l.Add(k); Add(k, guard + 1); }
        }
        Add(obj, 0);
        l.Sort(StringComparer.Ordinal);
        return l;
    }

    /// <summary>
    /// What a reference to an object can hold. A plain object: itself. A base with variants: each variant under a
    /// <c>{Variant}</c> marker (and itself too when it isn't abstract). A base with over 100 variants (the 612 condition
    /// functions): only the base's own fields, spelled out once; the variants are listed by <c>describe</c> on the base.
    /// </summary>
    public IReadOnlyList<Target> Targets(string refObj) => _targets.TryGetValue(refObj, out var l) ? l : Array.Empty<Target>();

    List<Target> MakeTargets(string refObj)
    {
        var l = new List<Target>();
        var o = Objects[refObj];
        var v = Variants(refObj);
        if (!o.Abstract || v.Count == 0 || v.Count > BigVariantLimit) l.Add(new(refObj, "", 0));
        if (v.Count is > 0 and <= BigVariantLimit) foreach (var s in v) l.Add(new(s, "{" + s + "}", l.Count));
        return l;
    }

    public static string? MapSig(string? map, string? sig)
    {
        if (map == null || sig == null) return sig;
        int i = map.IndexOf(sig + "=", StringComparison.Ordinal);
        return i < 0 || i + 9 > map.Length ? sig : map.Substring(i + 5, 4);
    }

    sealed record Node(string Sig, string Type, string Object, string Prefix, string Plain, string? Ctx, string? Map, string? Gen,
        int? Base, int Depth, string Stack, string Sort, bool InList, FieldRow? Via = null);

    IEnumerable<FieldRow> RowsOf(Node n)
    {
        foreach (var (f, level) in AllFields(n.Object))
        {
            var owner = Objects[f.Object];
            string? map = f.SigMap ?? n.Map;
            string? raw = f.Subrecord ?? (f.RefObject != null && Objects.TryGetValue(f.RefObject, out var ro) ? ro.Sig : null) ?? (owner.Struct ? n.Ctx : null);
            int? abs = f.Offset == null ? null : f.OffsetRel ? n.Base + f.Offset : f.Offset;
            yield return new FieldRow
            {
                Sig = n.Sig, Type = n.Type, Def = f, Depth = n.Depth, Stack = n.Stack, Map = map, Parent = n.Via,
                Path = n.Prefix.Length == 0 ? f.Path : n.Prefix + "." + f.Path,
                PlainPath = n.Plain.Length == 0 ? f.Path : n.Plain + "." + f.Path,
                Subrecord = MapSig(map, raw),
                Offset = abs,
                ChildBase = f.StartsSubrecord ? 0 : abs,
                Enum = f.Enum == "T" ? n.Gen : f.Enum,
                ChildGen = f.GenericArg == "T" ? n.Gen : f.GenericArg,
                InList = n.InList || f.InList,
                Sort = n.Sort + (9 - level).ToString() + f.Ord.ToString("D4") + ".",
            };
        }
    }

    /// <param name="everyVariant">For a path lookup: the variants of a big base are tried too.</param>
    /// <param name="intoCycles">For a path lookup: an object already on the way down is entered again (a script struct's
    /// members are script entries again: <c>Members[*].Properties[*].Name</c>). The asked path bounds the depth;
    /// Expand never sets it, so the spelled-out fields stop at the first repeat.</param>
    IEnumerable<Node> ChildNodes(FieldRow r, bool everyVariant, bool intoCycles = false)
    {
        if (r.Def.RefObject == null || r.Depth >= MaxDepth) yield break;
        IEnumerable<Target> targets = Targets(r.Def.RefObject);
        // For a path lookup the variants of a big base are tried too (they aren't spelled out in Expand)
        if (everyVariant && Variants(r.Def.RefObject).Count > BigVariantLimit)
            targets = targets.Concat(Variants(r.Def.RefObject).Select((v, i) => new Target(v, "{" + v + "}", i + 1)));
        foreach (var t in targets)
        {
            if (!intoCycles && r.Stack.Contains("/" + t.Name + "/", StringComparison.Ordinal)) continue;
            yield return new Node(r.Sig, r.Type, t.Name, r.Path + t.Marker, r.PlainPath, r.Subrecord, r.Map, r.ChildGen, r.ChildBase,
                r.Depth + 1, r.Stack + t.Name + "/", r.Sort + t.Idx.ToString("D3") + ".", r.InList, r);
        }
    }

    Node Root(string obj)
    {
        var o = Objects[obj];
        bool record = o.ObjType == "Record";
        // An object described by itself starts in its own subrecord, when it names one
        return new Node(record ? o.Sig ?? "" : "", obj, obj, "", "", record ? null : o.Sig, null, null, 0, 0, "/" + obj + "/", "", false);
    }

    /// <summary>Every field of a record type (or of any object), each at its path, in file order.</summary>
    public IEnumerable<FieldRow> Expand(string obj)
    {
        return Objects.ContainsKey(obj) ? Walk(Root(obj)) : Enumerable.Empty<FieldRow>();

        IEnumerable<FieldRow> Walk(Node n)
        {
            foreach (var r in RowsOf(n))
            {
                yield return r;
                foreach (var c in ChildNodes(r, false))
                    foreach (var x in Walk(c)) yield return x;
            }
        }
    }

    /// <summary>The fields of one object only (bases included), not followed into the objects they refer to.</summary>
    public IEnumerable<FieldRow> Shallow(string obj) => Objects.ContainsKey(obj) ? RowsOf(Root(obj)) : Enumerable.Empty<FieldRow>();

    /// <summary>The fields one level below an object field.</summary>
    public IEnumerable<FieldRow> Below(FieldRow r, bool everyVariant = false) => ChildNodes(r, everyVariant).SelectMany(RowsOf);

    public sealed record Found(FieldRow Row, string Match, string? Part);

    /// <summary>What a lookup found. When it found nothing: where the path stopped (the deepest point reached).</summary>
    public sealed class Lookup
    {
        public List<Found> Found = new();
        /// <summary>The objects that were tried at the point where the path stopped (several when variants were tried).</summary>
        public List<string> StoppedAtObjects = new();
        public string StoppedAtSegment = "", PathSoFar = "";
        /// <summary>Set when the path went on past a plain value: that value.</summary>
        public FieldRow? StoppedAtLeaf;
        internal int Depth = -1;
    }

    static readonly Dictionary<string, string[]> Components = new(StringComparer.Ordinal)
    {
        ["P2Float"] = new[] { "X", "Y" }, ["P2Int16"] = new[] { "X", "Y" }, ["P2Int32"] = new[] { "X", "Y" },
        ["P3Float"] = new[] { "X", "Y", "Z" }, ["P3Int16"] = new[] { "X", "Y", "Z" }, ["P3Int32"] = new[] { "X", "Y", "Z" },
        ["Color"] = new[] { "R", "G", "B", "A" },
        ["AssetLink"] = new[] { "GivenPath", "DataRelativePath" },
        ["FormLinkOrIndex"] = new[] { "Link", "Index" },
    };

    /// <summary>
    /// Looks one path up in a record type or object. A path may end in a part of a field: a member of its enum
    /// (<c>Flags.RunOnce</c>), a component (<c>.X</c>), a byte slice (<c>@04:u8</c>) or a list's count (<c>#count</c>).
    /// Variant markers (<c>{Sub}</c>) may be left out. When nothing is found, the result says where the path stopped.
    /// </summary>
    public Lookup Resolve(string obj, string path, bool ignoreCase = false)
    {
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var look = new Lookup { StoppedAtSegment = path };
        if (!Objects.ContainsKey(obj)) { look.StoppedAtObjects.Add(obj); return look; }

        string? part = null; string match = "exact";
        int at = path.IndexOf('@');
        if (at > 0) { part = path[at..]; path = path[..at]; match = "bytes"; }
        else if (path.EndsWith("#count", StringComparison.Ordinal)) { part = "#count"; path = path[..^6]; match = "count"; }
        // Variant markers in the asked path are dropped: every variant is tried anyway
        path = System.Text.RegularExpressions.Regex.Replace(path, @"\{[^}]*\}", "");
        var segs = path.Split('.');

        void Stop(string o, string seg, string soFar, int depth, FieldRow? leaf)
        {
            if (depth < look.Depth) return;
            if (depth > look.Depth) { look.Depth = depth; look.StoppedAtObjects.Clear(); look.StoppedAtSegment = seg; look.PathSoFar = soFar; look.StoppedAtLeaf = leaf; }
            if (!look.StoppedAtObjects.Contains(o)) look.StoppedAtObjects.Add(o);
        }

        void Search(Node n, int from)
        {
            var rows = RowsOf(n).ToList();
            bool any = false;
            for (int k = segs.Length - from; k >= 1; k--)
            {
                string want = string.Join('.', segs, from, k);
                foreach (var r in rows)
                {
                    if (!r.Def.Path.Equals(want, cmp)) continue;
                    any = true;
                    int next = from + k;
                    // The item of a grid or dictionary may be written with ".Value" (it is a key and a value)
                    if (next < segs.Length && segs[next].Equals("Value", cmp) && r.Def.Path.EndsWith("[*]", StringComparison.Ordinal)
                        && rows.Any(x => x.Def.Kind is "Array2D" or "Dictionary" && x.Def.Path == r.Def.Path[..^3])) next++;
                    if (next == segs.Length)
                    {
                        if (match == "count" && r.Def.Kind is not ("List" or "Array" or "Array2D" or "Dictionary")) Stop(n.Object, "#count", r.PlainPath, next, r);
                        else look.Found.Add(new Found(r, match, part));
                        continue;
                    }
                    if (r.Def.RefObject != null)
                    {
                        foreach (var c in ChildNodes(r, true, intoCycles: true)) Search(c, next);
                        continue;
                    }
                    // A part of a leaf field
                    string rest = string.Join('.', segs, next, segs.Length - next);
                    if (match == "exact" && r.Enum != null && Enums.TryGetValue(r.Enum, out var members) && members.FirstOrDefault(m => m.Name.Equals(rest, cmp)) is { } mem)
                        look.Found.Add(new Found(r, "flag", mem.Name));
                    else if (match == "exact" && Components.TryGetValue(r.Def.Kind, out var comps) && comps.FirstOrDefault(c => c.Equals(rest, cmp)) is { } comp)
                        look.Found.Add(new Found(r, "component", comp));
                    else Stop(n.Object, rest, r.PlainPath, next, r);
                }
            }
            if (!any) Stop(n.Object, segs[from], n.Plain, from, null);
        }

        Search(Root(obj), 0);
        // The same field reached through several variants counts once per path
        look.Found = look.Found.GroupBy(f => (f.Row.Path, f.Match, f.Part)).Select(g => g.First()).ToList();
        return look;
    }
}
