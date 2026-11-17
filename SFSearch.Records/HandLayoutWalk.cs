// Walks Mutagen's hand-read layouts (the package's Layouts\HandReadLayouts.xml, C186, kept in schema.db's HandLayouts)
// over one record's bytes, and says where each value they name sits: the subrecord, the offset, the size, and how the
// stored bytes map to Mutagen's value. The byte link and value walks (LinkWalk.cs) read the parts Mutagen reads by hand
// from it, with no record read through Mutagen (SFSearch's speed design); sfsreport-tests link-positions holds what it
// places equal to Mutagen's own value positions (C60). Evidence takes Mutagen's value positions instead, except for the
// plugin header (TES4), which they don't cover (GAPS S172). Nothing here knows a record type: the layout file says it all
// (a perk's effects, an object template's OBTS, an object mod's DATA with its 24-byte properties, VMAD, weather clouds,
// a race's biped objects, a package's idles, a footstep set, a region's RDAT, a placed trap's NAME).
//
// The layouts come from Mutagen's package alone (Layouts/HandReadLayouts.xml): since fixes.20 (C57 W2) it lays out the
// plugin header's master list too, and SFSearch's own file is gone (GAPS S67, S88). This walk follows the file form the
// package's header documents (C47, GAPS Part 2 row 38; C57 added U64, Guid, View, a record Header and Object class):
//  - Segments: several start signatures; reader="generated" (an item runs from its start to its end marker, a start
//    inside it being its own subrecord, or without an end marker to the first subrecord it can't hold; the list ends at
//    the first subrecord after an item that starts none); at="cursor"; key="I8" (a dictionary's entries by key, the key
//    placed at offset 0 of the start subrecord); startName (the start signature kept as text for a Switch); prefix (the
//    file holds the first items of a longer list: a package's value blocks);
//  - SubRepeat at="cursor"; Generated at the subrecord level passes over the subrecords it names where the walk stands;
//  - an Object with reader="generated" is read by Mutagen's generated reader from the subrecords its children pass over,
//    kept as its run (Runs); a TypeSwitch at the subrecord level;
//  - P3F32, P3I16, F16, Null (Mutagen's field is null here), NotLast (on the last item its fields are null);
//  - Repeat count="fit" (while a whole item fits) and endsAtZeros="N" (an item starting with N zero bytes isn't one);
//  - encodings drop:N and length (Bytes: the field is the number of bytes); name on a ZString keeps its text;
//  - a Const of a list's Count is held against Mutagen's count;
//  - a dictionary's elements are its values: a path under an entry is the value's (Flags, not Value.Flags);
//  - U64 (8 bytes), Guid (16 bytes, a .NET Guid as Mutagen reads one), View (no bytes: a field that is a value read
//    earlier as a whole, seen as its own type); Object class= (the one class the reader makes there). A Header entry
//    (the 24-byte record header) isn't walked here: FieldLookup places the header's fields from it (HandLayouts.HeaderPlace);
//  - fixes.24 (C84, C86): an AnyRecord with several signatures (applies to a record holding any of them: Sigs); Sub
//    at="cursor" (only the subrecord where the walk stands); Peek type="FormID" (kept as a FormID name keeps one);
//    Versioning formVersion="N" (its children only from that form version of the record on; below it their fields keep
//    their defaults); an optional Sub without a field that isn't there names the lists its children name, empty;
//    ReflectionFormLinks (no bytes: the FormIDs Mutagen's ReflectionData.FindFormIds finds in the bytes the Bytes
//    before it read, each a FormID value at its place).
//
// The file's own comment describes each element; this follows it. A place the walk can't follow (a subrecord that
// isn't there, a value no Case takes, a class other than the one Mutagen made) stops that branch and is said in
// Problems; the values found before it stay, each still checked against Mutagen when asked for.
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using SFSearch.SchemaGen;

namespace SFSearch.Records;

internal sealed class HandLayoutWalk
{
    /// <summary>One value the layout names. <c>Path</c> is the card's path (<c>Ranks[0].Effects[5].Priority</c>).
    /// <c>Const</c>: no bytes of its own, Mutagen derives it from the value at Sub/Offset (a Switch's selector).
    /// <c>Mask</c>: one bit of the byte at Offset (<c>Invert</c>: Mutagen's value is the bit cleared).</summary>
    public sealed record Value(string Path, int Sub, int Offset, int Size, string Kind, string? Encoding, string? Const, byte Mask, int Shift, bool Invert, string Layout);

    /// <summary>A value kept by name for a count, a Switch or a Bit: where it is and what it reads.</summary>
    public sealed record Var(int Sub, int Offset, int Size, long? Number, string? Text);

    public Dictionary<string, Value> Values { get; } = new(StringComparer.Ordinal);
    /// <summary>The runs of subrecords a Segments element cut, by the list item's path (<c>Ranks[0].Effects[1]</c>).</summary>
    public Dictionary<string, (int S, int E)> Segments { get; } = new(StringComparer.Ordinal);
    /// <summary>The variables each Segments item had when it ended (a perk effect's <c>function</c>), by its path.</summary>
    public Dictionary<string, Dictionary<string, Var>> SegmentVars { get; } = new(StringComparer.Ordinal);
    /// <summary>The Segments definitions the walk used, by the list's path with no index (<c>Ranks[*].Effects</c>):
    /// every signature a segment may hold (described, generated, its start and end).</summary>
    public Dictionary<string, HashSet<string>> SegmentSigs { get; } = new(StringComparer.Ordinal);
    /// <summary>The runs of subrecords an Object read by Mutagen's generated reader took, by the object's path
    /// (<c>Rules</c>: an instance naming rule's rule sets, the subrecords after UNAM).</summary>
    public Dictionary<string, (int S, int E)> Runs { get; } = new(StringComparer.Ordinal);
    /// <summary>The items each Segments list was cut into, by the list's path (<c>Aliases</c>), to check against
    /// Mutagen's count.</summary>
    public Dictionary<string, int> SegmentCounts { get; } = new(StringComparer.Ordinal);
    /// <summary>The layout that cut each list item, object run or list (<see cref="Segments"/>, <see cref="Runs"/>,
    /// <see cref="SegmentCounts"/>), by its path.</summary>
    public Dictionary<string, string> CutBy { get; } = new(StringComparer.Ordinal);
    /// <summary>The lists whose layout holds only their first items (prefix="true": a package's value blocks are its
    /// Data's first entries; a template's input names add more later), by the list's path.</summary>
    public HashSet<string> Prefixes { get; } = new(StringComparer.Ordinal);
    public List<string> Problems { get; } = new();
    /// <summary>The layouts that applied: "Record PERK", "AnyRecord VMAD".</summary>
    public List<string> Used { get; } = new();
    /// <summary>Walked from the bytes alone (<see cref="RunBytes"/>): the class a Case picked for an object, by its path
    /// (<c>VirtualMachineAdapter.Scripts[0].Properties[2]</c> → ScriptObjectProperty), so a value's path can carry the
    /// schema's kind of object.</summary>
    public Dictionary<string, string> Marks { get; } = new(StringComparer.Ordinal);
    /// <summary>The subrecords whose content a layout described (their indexes in the record).</summary>
    public HashSet<int> Read { get; } = new();
    /// <summary>Each subrecord a layout described: the path its content was described at (<c>ObjectTemplates[2]</c> for an
    /// OBTS, <c>Components[3].Data</c> for a pattern swap's DAT2) and the Sub (or SubRepeat) element that describes it.
    /// The value walk (LinkWalk.cs) gives the values a layout names where Mutagen's generated reader meets the subrecord.</summary>
    public Dictionary<int, (string Path, XElement Element)> SubAt { get; } = new();
    /// <summary>The paths below which a branch of a layout stopped (a value no Case takes, bytes the layout doesn't
    /// describe whole): "" when a whole layout stopped. What the layout named there may be missing or wrong.</summary>
    public List<string> StoppedAt { get; } = new();
    /// <summary>Kept only for a value walk (<see cref="RunBytes"/>'s names): each object the layouts described, by its
    /// path (an OBTS's template <c>ObjectTemplates[2]</c>, a property <c>ObjectTemplates[2].Properties[0]</c>), with the
    /// fields they name in it, in the Case the bytes picked (a field a Null names is named: Mutagen's is null there; a
    /// NotLast's fields are named on the last item too, where they are null). A field of the object no layout names
    /// isn't known from the bytes.</summary>
    public Dictionary<string, HashSet<string>>? Named { get; private set; }

    readonly SchemaModel _m;
    readonly RawRecord _raw;
    readonly bool _localized;
    readonly IReadOnlyDictionary<string, XElement> _defines;
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SchemaModel, Dictionary<string, XElement>> Defines = new();

    /// <summary>The layouts' Defines by name, made once per schema (the elements are parsed once and only read).</summary>
    static IReadOnlyDictionary<string, XElement> DefinesOf(SchemaModel m) => Defines.GetValue(m, x =>
        x.HandLayouts.Where(h => h.Kind == "Define" && h.Name != null).GroupBy(h => h.Name!).ToDictionary(g => g.Key, g => g.Last().Element, StringComparer.Ordinal));
    /// <summary>The class Mutagen makes for the object at a path, from the schema (null when the schema has several kinds
    /// there).</summary>
    readonly Func<string, string?>? _classOf;
    string _layout = "";

    /// <summary>A layout's name in <see cref="Used"/> and <see cref="Value.Layout"/>: "Record QUST".</summary>
    internal static string NameOf(HandLayout h) => $"{h.Kind} {h.Sig}";

    /// <summary>Where a layout the walk used comes from, in words: "Mutagen's hand-read layout (Record QUST in HandReadLayouts.xml)".</summary>
    public static string Said(string layout) => HandLayouts.Said(layout);

    sealed class Stop(string why) : Exception(why);

    HandLayoutWalk(SchemaModel m, RawRecord raw, bool localized, Func<string, string?> classOf)
    {
        _m = m; _raw = raw; _localized = localized; _classOf = classOf;
        _defines = DefinesOf(m);
    }

    /// <summary>
    /// The layouts that apply to a record, walked over its bytes alone, with no Mutagen object to check against (the link
    /// census of profile, LinkWalk.cs): a TypeSwitch takes the class <paramref name="classOf"/> gives for the path, a
    /// keyed entry is named by its key, an optional object that isn't there is passed over, and nothing is held against
    /// Mutagen's values. <paramref name="use"/> picks the layouts to walk (the record's type's and each AnyRecord whose
    /// subrecord it has).
    /// </summary>
    public static HandLayoutWalk RunBytes(SchemaModel m, RawRecord raw, bool localized, Func<string, string?> classOf, Func<HandLayout, bool> use, bool names = false)
    {
        var w = new HandLayoutWalk(m, raw, localized, classOf);
        if (names) w.Named = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var h in m.HandLayouts)
        {
            if (h.Kind is not ("Record" or "AnyRecord") || !use(h)) continue;
            w._layout = NameOf(h);
            w.Used.Add(w._layout);
            int cursor = 0;
            try { w.Subs(h.Element.Elements(), ref cursor, 0, raw.Subrecords.Count, "", new Dictionary<string, Var>(StringComparer.Ordinal)); }
            catch (Stop s) { w.Problems.Add($"{w._layout}: {s.Message}"); w.StoppedAt.Add(""); }
        }
        return w;
    }

    /// <summary>Every signature the items of a record type's Segments element starting at <paramref name="start"/> may
    /// hold (a perk effect's PRKE … PRKF), from the layout alone. Null when the record type's layout has none.</summary>
    public static HashSet<string>? SegmentSigsOf(SchemaModel m, string recordSig, string start)
    {
        var rec = m.HandLayouts.FirstOrDefault(h => h.Kind == "Record" && h.Sig == recordSig);
        var seg = rec?.Element.Descendants("Segments").FirstOrDefault(x => A(x, "start") == start);
        if (seg == null) return null;
        var w = new HandLayoutWalk(m);
        return w.SigsOf(seg);
    }

    HandLayoutWalk(SchemaModel m)
    {
        _m = m; _raw = null!;
        _defines = DefinesOf(m);
    }

    /// <summary>Whether the schema has a hand-read layout for this record at all (its type's, or an AnyRecord's).</summary>
    public static bool Applies(SchemaModel m, RawRecord raw, string sig)
        => m.HandLayouts.Any(h => h.Kind == "Record" && h.Sig == sig || h.Kind == "AnyRecord" && Holds(h, raw));

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HandLayout, string[]> SigLists = new();

    /// <summary>An AnyRecord's signatures (C84: <c>sig="XCZR,XCZC,XCZA"</c>, checked on records that have any of them).</summary>
    public static string[] Sigs(HandLayout h) => SigLists.GetValue(h, x => (x.Sig ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Whether a record holds one of an AnyRecord's subrecords, so the layout applies to it.</summary>
    public static bool Holds(HandLayout h, RawRecord raw)
    {
        var sigs = Sigs(h);
        foreach (var s in raw.Subrecords) if (Array.IndexOf(sigs, s.Signature) >= 0) return true;
        return false;
    }

    string Sig(int i) => _raw.Subrecords[i].Signature;
    static string? A(XElement e, string name) => e.Attribute(name)?.Value;

    static string Join(string path, string? field)
    {
        if (field == null || field == ".") return path;
        string p = path.Length == 0 ? field : path + "." + field;
        return p;
    }

    /// <summary>A link list's element is named by its FormKey in the layout (<c>Keywords[3].FormKey</c>); the card names
    /// the element itself (<c>Keywords[3]</c>). A RecordType is named by its number (<c>Subtype.TypeInt</c>, a topic's
    /// subtype); the card names the field.</summary>
    static string Leaf(string path) => path.EndsWith("].FormKey", StringComparison.Ordinal) ? path[..^".FormKey".Length]
        : path.EndsWith(".TypeInt", StringComparison.Ordinal) ? path[..^".TypeInt".Length] : path;

    long Count(string? count, Dictionary<string, Var> vars, string what)
    {
        if (count == null) throw new Stop($"{what} has no count");
        if (count.StartsWith('$'))
            return vars.TryGetValue(count[1..], out var v) && v.Number is long n ? n : throw new Stop($"{what}: the count {count} wasn't read");
        return long.Parse(count, CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ the subrecord level

    void Subs(IEnumerable<XElement> children, ref int cursor, int from, int to, string path, Dictionary<string, Var> vars)
    {
        foreach (var e in children)
        {
            // A field a subrecord (or a run of one subrecord each) holds whole, named in the object it stands in (a pattern
            // swap's Data, a patrol's Topics). Not a Segments' list or an Object at this level: Mutagen's generated reader
            // reads those (a weapon's object templates, a patrol), and the layout names what hand-written code reads in them
            if (Named != null && e.Name.LocalName is "Sub" or "SubRepeat" && A(e, "field") is { } own) Name(path, own);
            switch (e.Name.LocalName)
            {
                case "Note": break;
                case "Generated":
                {
                    // Subrecords the generated code reads, passed over where the walk stands (an object's run, a patrol's INAM)
                    var sigs = (A(e, "sigs") ?? "").Split(',').ToHashSet(StringComparer.Ordinal);
                    while (cursor < to && sigs.Contains(Sig(cursor))) cursor++;
                    break;
                }
                case "Null": break;
                case "Use": Subs(Define(e).Elements(), ref cursor, from, to, path, vars); break;
                case "Sub":
                {
                    string sig = A(e, "sig")!;
                    // at="cursor" (C84): only the subrecord where the walk stands (a patrol's TNAM right after its topics)
                    int j = A(e, "at") == "cursor" ? (cursor < to && Sig(cursor) == sig ? cursor : -1) : Next(sig, cursor, to);
                    if (j < 0)
                    {
                        // Optional and not there: the field it holds is null (a quest's NextAliasID without ANAM); without a
                        // field, the lists its children name are empty (C84)
                        if (A(e, "optional") == "true") { if (Named != null && A(e, "field") == null) NameEmptyLists(e, path); break; }
                        throw new Stop($"no {sig} subrecord where {(path.Length == 0 ? "the record" : path)} has one");
                    }
                    Content(e, j, Join(path, A(e, "field")), vars);
                    cursor = j + 1;
                    break;
                }
                case "SubRepeat":
                {
                    string sig = A(e, "sig")!;
                    // at="cursor": the list is where the walk stands, or it has none (an event with no topics doesn't take the next one's)
                    int j = A(e, "at") == "cursor" ? (cursor < to && Sig(cursor) == sig ? cursor : -1) : Next(sig, cursor, to);
                    if (j < 0) break;
                    long n = A(e, "count") != null ? Count(A(e, "count"), vars, sig) : long.MaxValue;
                    string list = Join(path, A(e, "field"));
                    for (int k = 0; k < n && j < to && Sig(j) == sig; k++, j++)
                    {
                        var v = new Dictionary<string, Var>(vars, StringComparer.Ordinal);
                        if (A(e, "index") is { } ix) v[ix] = new Var(-1, 0, 0, k, null);
                        Content(e, j, $"{list}[{k}]", v);
                    }
                    cursor = j;
                    break;
                }
                case "AnyOrder":
                {
                    // The child subrecords in any order, as the file has them; generated ones passed over
                    var subs = e.Elements().Where(x => x.Name.LocalName == "Sub").ToList();
                    var generated = e.Elements().Where(x => x.Name.LocalName == "Generated").SelectMany(x => (A(x, "sigs") ?? "").Split(',')).ToHashSet(StringComparer.Ordinal);
                    int j = cursor;
                    for (; j < to; j++)
                    {
                        var sub = subs.FirstOrDefault(s => A(s, "sig") == Sig(j));
                        if (sub != null) Content(sub, j, Join(path, A(sub, "field")), vars);
                        else if (!generated.Contains(Sig(j))) break;
                    }
                    cursor = j;
                    break;
                }
                case "Segments":
                {
                    var starts = A(e, "start")!.Split(',').ToHashSet(StringComparer.Ordinal); string? end = A(e, "end"), stopAt = A(e, "stopAt");
                    bool generated = A(e, "reader") == "generated";
                    string list = Join(path, A(e, "field"));
                    var holds = SigsOf(e);
                    SegmentSigs[Unindexed(list)] = holds;
                    int k = 0, j = cursor;
                    // at="cursor": the list is where the walk stands, or it has no items
                    if (A(e, "at") != "cursor") while (j < to && !starts.Contains(Sig(j)) && Sig(j) != stopAt) j++;
                    // key="I8" (or U8, I16, U16, I32, U32): a dictionary's entries found by key, each the entry whose Key is
                    // the number at offset 0 of its start subrecord; these give values only, the runs stay the ones cut by place
                    string? key = A(e, "key");
                    if (A(e, "prefix") == "true" && A(e, "field") != null && key == null) Prefixes.Add(list);
                    while (j < to && starts.Contains(Sig(j)))
                    {
                        int s = j, x = j + 1;
                        if (generated && end != null)
                        {
                            // To its end marker, which is the item's; a start before it is a subrecord of the item
                            while (x < to && Sig(x) != end) x++;
                            if (x < to) x++;
                        }
                        else if (generated)
                            while (x < to && !starts.Contains(Sig(x)) && holds.Contains(Sig(x))) x++;
                        else
                            while (x < to && !starts.Contains(Sig(x)) && (end == null || Sig(x - 1) != end) && Sig(x) != stopAt) x++;
                        // Without a field the runs aren't list items: their values belong to the object itself (REGN's RDAT blocks)
                        string item;
                        try { item = A(e, "field") == null ? path : key != null ? $"{list}[{Keyed(list, key, s)}]" : $"{list}[{k}]"; }
                        catch (Stop st) { Problems.Add($"{_layout}: {list}: {st.Message}"); StoppedAt.Add(list); k++; j = x; continue; }
                        if (A(e, "field") != null && key == null) { Segments[item] = (s, x); CutBy[item] = _layout; }
                        var v = new Dictionary<string, Var>(vars, StringComparer.Ordinal);
                        if (A(e, "index") is { } ix) v[ix] = new Var(-1, 0, 0, k, null);
                        // startName: the item's start signature as text, for a Switch (a quest alias's class)
                        if (A(e, "startName") is { } sn) v[sn] = new Var(s, 0, 0, null, Sig(s));
                        // key=: the entry's Key is the number at offset 0 of its start subrecord
                        if (key != null) Values.TryAdd($"{item}.Key", new Value($"{item}.Key", s, 0, KeySize(key), key, null, null, 0, 0, false, _layout));
                        int c = A(e, "skipStart") == "true" ? s + 1 : s;
                        // A later value of a keyed entry replaces an earlier one, as Mutagen's reader sets it again
                        bool was = _replace; _replace = key != null;
                        try { Subs(e.Elements(), ref c, s, x, item, v); }
                        catch (Stop st) { Problems.Add($"{_layout}: {item}: {st.Message}"); StoppedAt.Add(item); }
                        finally { _replace = was; }
                        if (A(e, "field") != null) SegmentVars[item] = v;
                        k++; j = x;
                        // Between segments: what isn't a start (a stopAt, or generated subrecords) ends the list
                        if (!generated) while (j < to && !starts.Contains(Sig(j)) && Sig(j) != stopAt && end != null) j++;
                    }
                    if (A(e, "field") != null && key == null) { SegmentCounts[list] = k; CutBy[list] = _layout; }
                    // A list with no item takes no subrecord: what follows in the layout is looked for from where the walk
                    // stood (an NPC with no object templates: its ONA2 after them)
                    if (k > 0) cursor = j;
                    break;
                }
                case "Object":
                {
                    string obj = Join(path, A(e, "field"));
                    OneClass(e, obj);
                    if (A(e, "optional") == "true")
                    {
                        // From the bytes alone: the object is there when its subrecords are (a patrol's XPRD)
                        int at = cursor;
                        try { Subs(e.Elements(), ref cursor, from, to, obj, vars); }
                        catch (Stop) { cursor = at; }
                        break;
                    }
                    int first = cursor;
                    Subs(e.Elements(), ref cursor, from, to, obj, vars);
                    // Read by Mutagen's generated reader from the subrecords it passed over: its run
                    if (A(e, "reader") == "generated" && cursor > first) { Runs[obj] = (first, cursor); CutBy[obj] = _layout; }
                    break;
                }
                case "Switch": case "TypeSwitch":
                {
                    var chosen = Choose(e, path, vars);
                    if (chosen != null) Subs(chosen.Elements(), ref cursor, from, to, path, vars);
                    break;
                }
                case "Versioning":
                    if (FromVersion(e)) Subs(e.Elements(), ref cursor, from, to, path, vars);
                    break;
                default:
                    throw new Stop($"<{e.Name.LocalName}> at the subrecord level");
            }
        }
    }

    /// <summary>An Object's class= (C57): the one class Mutagen's reader makes there though the field's type has kinds (a
    /// dialogue response's script fragments are a plain ScriptFragments). From the bytes alone it names the object's kind;
    /// with Mutagen's card, another class there is a problem.</summary>
    void OneClass(XElement e, string obj)
    {
        if (A(e, "class") is not { } cls) return;
        cls = HandLayouts.ClassName(cls);
        Marks[obj] = cls;
    }

    int Next(string sig, int from, int to)
    {
        for (int j = from; j < to; j++) if (Sig(j) == sig) return j;
        return -1;
    }

    XElement Define(XElement use) => _defines.TryGetValue(A(use, "name") ?? "", out var d) ? d : throw new Stop($"<Use name=\"{A(use, "name")}\"> names no Define");

    /// <summary>Every signature a Segments element's items may hold: its start and end, the subrecords it describes and
    /// the generated ones it names.</summary>
    HashSet<string> SigsOf(XElement seg)
    {
        var set = new HashSet<string>(A(seg, "start")!.Split(','), StringComparer.Ordinal);
        if (A(seg, "end") is { } end) set.Add(end);
        void Walk(XElement x)
        {
            foreach (var c in x.Elements())
            {
                switch (c.Name.LocalName)
                {
                    case "Segments": continue; // its own items
                    case "Sub": case "SubRepeat": set.Add(A(c, "sig")!); continue;
                    case "Generated": foreach (var s in (A(c, "sigs") ?? "").Split(',')) if (s.Length == 4) set.Add(s); continue;
                    case "Use": Walk(_defines.TryGetValue(A(c, "name") ?? "", out var d) ? d : c); continue;
                    default: Walk(c); continue;
                }
            }
        }
        Walk(seg);
        return set;
    }

    bool _replace;

    /// <summary>The variable a Repeat keeps for NotLast: 1 on its last item.</summary>
    const string LastVar = " last";
    /// <summary>The variable a Bytes element keeps for a ReflectionFormLinks after it: the bytes it read.</summary>
    const string BytesVar = " bytes";

    /// <summary>A Versioning element's condition (C84): the record's form version (the uint16 at offset 20 of its header)
    /// is at least formVersion.</summary>
    bool FromVersion(XElement e)
    {
        if (!int.TryParse(A(e, "formVersion"), NumberStyles.None, CultureInfo.InvariantCulture, out int n)) throw new Stop("a Versioning without its formVersion");
        return _raw.FormVersion >= n;
    }

    /// <summary>An optional Sub without a field that isn't there (C84): the lists its direct children name are empty, so
    /// a value walk knows them (no entries) rather than leaving them unread.</summary>
    void NameEmptyLists(XElement sub, string path)
    {
        foreach (var x in sub.Elements())
            if (x.Name.LocalName is "Repeat" or "SubRepeat" or "Segments" && A(x, "field") is { } f && f != ".") Name(path, f);
    }

    /// <summary>The index on the card of the dictionary entry whose Key is the number at offset 0 of subrecord
    /// <paramref name="sub"/>, read as <paramref name="type"/>.</summary>
    static int KeySize(string type) => type switch { "I8" or "U8" => 1, "I16" or "U16" => 2, _ => 4 };

    string Keyed(string list, string type, int sub)
    {
        // The entry is named by its key, as Mutagen's value positions name a dictionary entry
        var s = _raw.Subrecords[sub];
        int size = KeySize(type);
        if (s.Length < size) throw new Stop($"{Sig(sub)}: {s.Length} bytes, too few for a {type} key");
        var kb = _raw.Data.AsSpan(s.Start, size);
        long key = type switch { "I8" => (sbyte)kb[0], "U8" => kb[0], "I16" => BinaryPrimitives.ReadInt16LittleEndian(kb), "U16" => BinaryPrimitives.ReadUInt16LittleEndian(kb), "I32" => BinaryPrimitives.ReadInt32LittleEndian(kb), _ => BinaryPrimitives.ReadUInt32LittleEndian(kb) };
        return key.ToString(CultureInfo.InvariantCulture);
    }

    static string Unindexed(string path) => System.Text.RegularExpressions.Regex.Replace(path, @"\[\d+\]", "[*]");

    /// <summary>The Case a Switch (by a value read earlier) or a TypeSwitch (by the class Mutagen made) takes, its
    /// type checked against the class Mutagen made for the object at <paramref name="path"/>.</summary>
    XElement? Choose(XElement sw, string path, Dictionary<string, Var> vars)
    {
        var cases = sw.Elements().Where(c => c.Name.LocalName == "Case").ToList();
        var def = sw.Elements().FirstOrDefault(c => c.Name.LocalName == "Default");
        string? made = _classOf?.Invoke(path);
        XElement? pick;
        string said;
        if (sw.Name.LocalName == "TypeSwitch")
        {
            if (made == null) throw new Stop($"{(path.Length == 0 ? "the record" : path)}: the class Mutagen made isn't known");
            pick = cases.FirstOrDefault(c => (A(c, "type") ?? "").Split(',').Any(t => HandLayouts.ClassName(t) == made)) ?? def;
            said = made;
        }
        else
        {
            string on = A(sw, "on")!;
            if (!vars.TryGetValue(on, out var v)) throw new Stop($"Switch on {on}, which wasn't read");
            long mask = A(sw, "mask") is { } mk ? Convert.ToInt64(mk, 16) : -1;
            string val = v.Number is long n ? (n & mask).ToString(CultureInfo.InvariantCulture) : v.Text ?? "";
            // A number is compared as a number: a Case may give it in hex (value="0x10", NPC_'s race override sections, C57)
            static long? Num(string s) => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long h) ? h
                : long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long d) ? d : null;
            pick = cases.FirstOrDefault(c => (A(c, "value") ?? "").Split(',').Any(x => v.Number is long n2 ? Num(x.Trim()) == (n2 & mask) : string.Equals(x, val, StringComparison.Ordinal))) ?? def;
            said = $"{on} = {(v.Number != null ? val : "\"" + val + "\"")}";
        }
        if (pick == null) throw new Stop($"no Case takes {said}");
        if ((A(pick, "type") is { } picked ? HandLayouts.ClassName(picked.Split(',')[0]) : sw.Name.LocalName == "TypeSwitch" ? made : null) is { } cls) Marks[path] = cls;
        if (A(pick, "type") is { } type && made != null && !type.Split(',').Any(t => HandLayouts.ClassName(t) == made))
            throw new Stop($"{path}: the layout gives {type} for {said}, and Mutagen made {made}");
        return pick;
    }

    // ------------------------------------------------------------------ inside one subrecord

    sealed class Cursor(int sub, int start, int length)
    {
        public readonly int Sub = sub, Start = start, Length = length;
        public int P;
        public int Left => Length - P;
    }

    void Content(XElement element, int sub, string path, Dictionary<string, Var> vars)
    {
        var s = _raw.Subrecords[sub];
        Read.Add(sub);
        SubAt[sub] = (path, element);
        var c = new Cursor(sub, s.Start, s.Length);
        try { Content(element.Elements(), c, path, vars, null); }
        catch (Stop) { StoppedAt.Add(path); throw; }
        if (c.P != c.Length) { Problems.Add($"{_layout}: {Sig(sub)} at {path}: the layout describes {c.P} of its {c.Length} bytes"); StoppedAt.Add(path); }
    }

    ReadOnlySpan<byte> Bytes(Cursor c, int at, int size) => _raw.Data.AsSpan(c.Start + at, size);

    void Need(Cursor c, int size, string what)
    {
        if (size < 0 || c.P + size > c.Length) throw new Stop($"{what}: {size} bytes at offset {c.P} of {Sig(c.Sub)}, which is {c.Length} bytes");
    }

    /// <param name="selector">The variable the nearest Switch chose by: a Const's value is derived from it.</param>
    void Content(IEnumerable<XElement> children, Cursor c, string path, Dictionary<string, Var> vars, Var? selector)
    {
        if (Named != null && !Named.ContainsKey(path)) Named[path] = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in children)
        {
            string n = e.Name.LocalName;
            string? field = A(e, "field"), name = A(e, "name");
            if (Named != null && field != null) Name(path, field);
            // On the last item a NotLast's fields are null: named all the same
            if (Named != null && n == "NotLast") foreach (var x in e.Descendants()) if (A(x, "field") is { } nf && !x.Ancestors().TakeWhile(a => a != e).Any(a => a.Attribute("field") != null)) Name(path, nf);
            switch (n)
            {
                case "Note": break;
                case "Use": Content(Define(e).Elements(), c, path, vars, selector); break;
                case "U8" or "I8" or "U16" or "I16" or "U32" or "I32" or "F32" or "FormID" or "Color" or "F16" or "P3F32" or "P3I16" or "U64" or "Guid":
                {
                    int size = n switch { "U8" or "I8" or "U16" or "I16" or "F16" => n is "U8" or "I8" ? 1 : 2, "P3F32" => 12, "P3I16" => 6, "U64" => 8, "Guid" => 16, _ => 4 };
                    Need(c, size, Join(path, field ?? name ?? n));
                    var b = Bytes(c, c.P, size);
                    if (A(e, "expect") is { } ex && !b.SequenceEqual(Convert.FromHexString(ex))) throw new Stop($"{Join(path, field ?? n)}: offset {c.P} of {Sig(c.Sub)} holds {Convert.ToHexString(b)}, the layout expects {ex}");
                    long? num = n switch
                    {
                        "U8" => b[0], "I8" => (sbyte)b[0], "U16" => BinaryPrimitives.ReadUInt16LittleEndian(b), "I16" => BinaryPrimitives.ReadInt16LittleEndian(b),
                        "U32" or "FormID" or "Color" => BinaryPrimitives.ReadUInt32LittleEndian(b), "I32" => BinaryPrimitives.ReadInt32LittleEndian(b),
                        "U64" => unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(b)), _ => null,
                    };
                    if (name != null) vars[name] = new Var(c.Sub, c.P, size, num, null);
                    if (field != null) Add(Join(path, field), c.Sub, c.P, size, n, A(e, "encoding"));
                    c.P += size;
                    break;
                }
                case "I32String":
                {
                    Need(c, 4, Join(path, name ?? "text"));
                    int len = BinaryPrimitives.ReadInt32LittleEndian(Bytes(c, c.P, 4));
                    Need(c, 4 + len, Join(path, name ?? "text"));
                    string text = Encoding.Latin1.GetString(Bytes(c, c.P + 4, len)).TrimEnd('\0');
                    if (name != null) vars[name] = new Var(c.Sub, c.P, 4 + len, null, text);
                    if (field != null) Add(Join(path, field), c.Sub, c.P + 4, len, "ZString", null);
                    c.P += 4 + len;
                    break;
                }
                case "ZString" or "LString":
                {
                    int size;
                    string kind = n;
                    if (n == "LString" && _localized) { size = 4; kind = "LString"; }
                    else if (A(e, "length") is { } fixedLength) { size = int.Parse(fixedLength, CultureInfo.InvariantCulture); kind = "ZString"; }
                    else
                    {
                        int z = _raw.Data.AsSpan(c.Start + c.P, c.Left).IndexOf((byte)0);
                        size = z < 0 ? c.Left : z + 1;
                        kind = "ZString";
                    }
                    Need(c, size, Join(path, field ?? n));
                    // name: the text, for a Switch (a package input's kind) or a Const
                    if (name != null) vars[name] = new Var(c.Sub, c.P, size, null, kind == "ZString" ? Encoding.Latin1.GetString(Bytes(c, c.P, size)).TrimEnd('\0') : null);
                    if (field != null) Add(Join(path, field), c.Sub, c.P, size, kind, null);
                    c.P += size;
                    break;
                }
                case "Null": break;
                case "WString":
                {
                    Need(c, 2, Join(path, field ?? "text"));
                    int len = BinaryPrimitives.ReadUInt16LittleEndian(Bytes(c, c.P, 2));
                    Need(c, 2 + len, Join(path, field ?? "text"));
                    if (A(e, "lengthName") is { } ln) vars[ln] = new Var(c.Sub, c.P, 2, len, null);
                    if (field != null) Add(Join(path, field), c.Sub, c.P + 2, len, "WString", null);
                    c.P += 2 + len;
                    break;
                }
                case "Bytes":
                {
                    int size = A(e, "length") is { } l ? (int)Count(l, vars, Join(path, "bytes")) : c.Left;
                    Need(c, size, Join(path, field ?? "bytes"));
                    if (A(e, "expect") is { } ex && !Bytes(c, c.P, size).SequenceEqual(Convert.FromHexString(ex)))
                        throw new Stop($"{path}: offset {c.P} of {Sig(c.Sub)} holds {Convert.ToHexString(Bytes(c, c.P, size))}, the layout expects {ex}");
                    // encoding="length": the field is the number of bytes (a cell's NumTraversalFluffBytes)
                    if (field != null) Add(Join(path, field), c.Sub, c.P, size, "Bytes", A(e, "encoding"));
                    // For a ReflectionFormLinks after it: the bytes this read
                    vars[BytesVar] = new Var(c.Sub, c.P, size, null, null);
                    c.P += size;
                    break;
                }
                case "Const":
                {
                    string value = A(e, "value") ?? "";
                    // No bytes of its own: derived from the value the nearest Switch chose by, or from the value it names
                    var from = selector;
                    if (value.StartsWith('$'))
                    {
                        if (!vars.TryGetValue(value[1..], out var v)) throw new Stop($"Const {value}, which wasn't read");
                        value = v.Text ?? v.Number?.ToString(CultureInfo.InvariantCulture) ?? "";
                        if (from == null) from = v;
                    }
                    string at = Join(path, field);
                    if (from == null)
                    {
                        // A value the layout states outright for a field no bytes are read into (a navmesh geometry's
                        // Unknown3 stays 0 and its Versioning Break0: the reader stops at the break, fixes.24's NVNM
                        // layout): placed where the read stands, with no bytes of its own
                        if (!at.EndsWith(".Count", StringComparison.Ordinal))
                            Values.TryAdd(at, new Value(at, c.Sub, c.P, 0, "Const", null, value, 0, 0, false, _layout));
                        break;
                    }
                    if (at.EndsWith(".Count", StringComparison.Ordinal))
                    {
                        break;
                    }
                    Values.TryAdd(at, new Value(at, from.Sub, from.Offset, from.Size, "Const", null, value, 0, 0, false, _layout));
                    break;
                }
                case "Repeat":
                {
                    string list = Join(path, field);
                    string? countText = A(e, "count");
                    bool toEnd = countText is "end" or "fit";
                    long count = toEnd ? long.MaxValue : Count(countText, vars, list);
                    // endsAtZeros="N": an item that starts with N zero bytes isn't one; it and what follows are left
                    int zeros = A(e, "endsAtZeros") is { } z ? int.Parse(z, CultureInfo.InvariantCulture) : 0;
                    for (long k = 0; k < count && !(toEnd && c.Left == 0); k++)
                    {
                        if (zeros > 0 && c.Left >= zeros && !Bytes(c, c.P, zeros).ContainsAnyExcept((byte)0)) break;
                        if (A(e, "index") is { } ix) vars[ix] = new Var(-1, 0, 0, k, null);
                        // For NotLast: whether this is the last item of a counted Repeat
                        vars[LastVar] = new Var(-1, 0, 0, !toEnd && k == count - 1 ? 1 : 0, null);
                        string item = $"{list}[{k}]";
                        if (countText != "fit") { Content(e.Elements(), c, item, vars, selector); continue; }
                        // count="fit": an item is read only while a whole one fits in what is left (a cell's traversals:
                        // 52 or 56 bytes by their flags); a part that doesn't fit is left for what follows the Repeat
                        int p0 = c.P;
                        try { Content(e.Elements(), c, item, vars, selector); }
                        catch (Stop)
                        {
                            c.P = p0;
                            foreach (var key in Values.Keys.Where(x => x == item || x.StartsWith(item + ".", StringComparison.Ordinal)).ToList()) Values.Remove(key);
                            break;
                        }
                    }
                    break;
                }
                case "Object":
                    OneClass(e, Join(path, field));
                    Content(e.Elements(), c, Join(path, field), vars, selector);
                    break;
                case "Switch": case "TypeSwitch":
                {
                    var chosen = Choose(e, path, vars);
                    var sel = e.Name.LocalName == "Switch" && vars.TryGetValue(A(e, "on")!, out var sv) ? sv : selector;
                    if (chosen != null) Content(chosen.Elements(), c, path, vars, sel);
                    break;
                }
                case "Bit":
                {
                    string of = A(e, "of")!;
                    if (!vars.TryGetValue(of, out var v) || v.Number == null) throw new Stop($"Bit of {of}, which wasn't read");
                    int index = (int)Count(A(e, "index"), vars, $"Bit of {of}");
                    if (index < 0 || index >= v.Size * 8) throw new Stop($"bit {index} of {of}, a {v.Size}-byte value");
                    if (field != null)
                        Values.TryAdd(Join(path, field), new Value(Join(path, field), v.Sub, v.Offset + index / 8, 1, "Bit", null, null, (byte)(1 << (index % 8)), index % 8, A(e, "invert") == "true", _layout));
                    break;
                }
                case "View":
                {
                    // No bytes of its own: the field is a value read earlier as a whole, seen as its own type (an enum view of
                    // a flags value); optional: only some classes have the property
                    string of = A(e, "of")!;
                    if (!vars.TryGetValue(of, out var v)) throw new Stop($"View of {of}, which wasn't read");
                    if (field != null)
                        Values.TryAdd(Join(path, field), new Value(Join(path, field), v.Sub, v.Offset, v.Size, "View", null, null, 0, 0, false, _layout));
                    break;
                }
                case "IfMore":
                    if (c.Left > 0) Content(e.Elements(), c, path, vars, selector);
                    break;
                case "NotLast":
                    if (!vars.TryGetValue(LastVar, out var lastVar)) throw new Stop("NotLast outside a counted Repeat");
                    // On the last item its fields are null (a pattern swap list's Unknown2): no bytes
                    if (lastVar.Number == 0) Content(e.Elements(), c, path, vars, selector);
                    break;
                case "Versioning":
                    // C84: the children only from the record's form version N on; below it they have no bytes and their
                    // fields keep their defaults
                    if (FromVersion(e)) Content(e.Elements(), c, path, vars, selector);
                    // Below it the fields are named all the same: Mutagen's are at their defaults
                    else if (Named != null) foreach (var x in e.Elements()) if (A(x, "field") is { } vf) Name(path, vf);
                    break;
                case "ReflectionFormLinks":
                {
                    // C86: no bytes of its own; its list is the FormIDs the reflection format stores in the bytes the Bytes
                    // before it read, as Mutagen's ReflectionData.FindFormIds finds them, each 4 bytes at its place
                    if (!vars.TryGetValue(BytesVar, out var blob)) throw new Stop($"{Join(path, field)}: a ReflectionFormLinks with no Bytes before it");
                    if (field == null) break;
                    var ids = Mutagen.Bethesda.Starfield.ReflectionData.FindFormIds(_raw.Data.AsSpan(c.Start + blob.Offset, blob.Size));
                    string list = Join(path, field);
                    int k = 0;
                    foreach (var id in ids)
                    {
                        if (id.Position < 0 || id.Position + 4 > blob.Size) throw new Stop($"{list}: a FormID at {id.Position}, past the {blob.Size} bytes read");
                        Add($"{list}[{k++}]", c.Sub, blob.Offset + id.Position, 4, "FormID", null);
                    }
                    break;
                }
                case "Peek":
                {
                    int off = int.Parse(A(e, "offset")!, CultureInfo.InvariantCulture);
                    // FormID (C84): kept as a FormID name keeps one, the stored number (0 when null)
                    int size = A(e, "type") switch { "U8" => 1, "U16" => 2, "U32" or "FormID" => 4, var t => throw new Stop($"Peek of type {t}") };
                    if (c.P + off + size > c.Length) throw new Stop($"Peek at offset {c.P + off} of {Sig(c.Sub)}, which is {c.Length} bytes");
                    var b = Bytes(c, c.P + off, size);
                    long num = size switch { 1 => b[0], 2 => BinaryPrimitives.ReadUInt16LittleEndian(b), _ => BinaryPrimitives.ReadUInt32LittleEndian(b) };
                    if (name != null) vars[name] = new Var(c.Sub, c.P + off, size, num, null);
                    break;
                }
                default:
                    throw new Stop($"<{n}> inside a subrecord");
            }
        }
    }

    /// <summary>Notes that the layout names <paramref name="field"/> (its first step: <c>Subtype</c> of <c>Subtype.TypeInt</c>;
    /// not ".", the list's element itself) in the object at <paramref name="path"/>.</summary>
    void Name(string path, string field)
    {
        if (field == ".") return;
        int dot = field.IndexOf('.');
        string first = dot < 0 ? field : field[..dot];
        if (!Named!.TryGetValue(path, out var set)) Named[path] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(first);
    }

    void Add(string path, int sub, int offset, int size, string kind, string? encoding)
    {
        path = Leaf(path);
        var value = new Value(path, sub, offset, size, kind, encoding, null, 0, 0, false, _layout);
        if (_replace) Values[path] = value; else Values.TryAdd(path, value);
    }
}
