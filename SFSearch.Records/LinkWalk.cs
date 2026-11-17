// The link fields of a record read from its bytes alone (GAPS S92): every FormID the schema calls a link, at the place
// Mutagen's reader takes it from, with no record read through Mutagen. profile's link census (profile --subrecords "*",
// and profile <Type.Path> of a link field) counts them.
//
// Three readers, each the one that knows the bytes:
//  - the four bulky subrecords that hold few links in a lot of data (a cell's and a placed object's traversals XTV2, a
//    navmesh's NVNM, a surface tree's FNAM grids, the navmesh info map's NVMI): WorldBytes.cs, the world link index's
//    readers (J32);
//  - what Mutagen reads with hand-written code and a hand-read layout describes (VMAD's scripts and properties, a
//    patrol's topics, a package's data inputs, a component's pattern swaps, an object template's includes …): the
//    layouts walked over the bytes (HandLayoutWalk.RunBytes), as evidence follows them;
//  - the rest as Mutagen's generated reader reads it: the subrecords handed to the record's fields in file order (the
//    evidence walk's Claim, EvidenceItems.cs), a list of items several subrecords each cut into items, a component's kind
//    picked by its BFCB text, and inside a subrecord the fields read one after another: counted lists of entries of
//    varying length (a placed object's snap links XSL1, a world's large references, the preferred pathing NVPP), texts
//    with their lengths, objects inside objects; a condition by the XML's layout of a CTDA; a reflection's FormLinks
//    where Mutagen's ReflectionData finds them.
// Every record type is walked (GAPS S105): the runs a type's own hand-read layout cuts out (a quest's aliases, a perk's
// ranks and effects, a scene's actions, a package's inputs) are read as the object Mutagen's generated reader makes of
// each, the record's own fields never seeing their subrecords; an object whose kind Mutagen's hand-written code picks is
// read by the schema's selector (a book's Teaches, a magic effect's archetype, a location target, a terminal menu item's
// target, a container entry's owner by the record type its FormID names); a field the record XML's layout attributes
// place is read there (a package's template, a traversal's flags, FormID and halves); gendered halves after MNAM/FNAM
// are each read as their object. Held field by field against Mutagen by sfsreport-tests link-census.
// A FormID found twice at one place (a layout and the generated walk both reach it) counts once. A link field none of
// the three reaches is named by LinkFields.Left, with why; a record whose bytes don't follow the layout (a count past the
// end of its subrecord, a kind no selector picks) is said in the walk's Problems, and its links found so far stay.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using SFSearch.SchemaGen;

namespace SFSearch.Records;

/// <summary>
/// What the byte walk gives when it is asked for every value, not only links (<see cref="LinkByteWalker.WalkValues"/>,
/// GAPS S153): each place as a path with list indexes as numbers (<c>Components[3].Items[0].Name</c>), each object with
/// the kind the walk picked for it.
/// </summary>
public interface IRecordValueSink
{
    /// <summary>An object the walk read (a list entry, an object field, the record itself at ""), of the kind named.</summary>
    void Object(string path, string kind);
    /// <summary>A value the walk read: its field, the subrecord it sits in, and its bytes [at, end) of that subrecord.</summary>
    void Value(string path, FieldDef field, int subrecord, int at, int end);
    /// <summary>A value one of Mutagen's hand-read layouts lays out (HandReadLayouts.xml, GAPS S164): its bytes [at, end)
    /// of the subrecord, stored as the layout's kind says (<c>U8</c>, <c>I16</c>, <c>FormID</c>, <c>ZString</c>,
    /// <c>WString</c> …), which may be narrower or wider than the field's own (an object mod property's FunctionType is
    /// one byte, its Property four).</summary>
    void LaidOut(string path, FieldDef field, int subrecord, int at, int end, string layoutKind) => Value(path, field, subrecord, at, end);
    /// <summary>A part whose values the walk doesn't give (a hand-read layout's, Mutagen's hand-written code's, a layout the
    /// bytes don't follow): nothing at or under the path is known from the walk.</summary>
    void Unread(string path, string why);
}

/// <summary>The link fields of one record type: every schema path that is a link (records nested in it are their own
/// type), which of them the bytes give (<see cref="LinkByteWalker"/>), and why the others aren't.</summary>
public sealed class LinkFields
{
    public required string Type { get; init; }
    /// <summary>Every link field, as the schema writes its path (<c>Components[*]{KeywordFormComponent}.Keywords[*]</c>).</summary>
    public required IReadOnlyList<string> Paths { get; init; }
    /// <summary>Each field's subrecord (null when the schema gives none).</summary>
    public required IReadOnlyList<string?> Subrecords { get; init; }
    /// <summary>For each field: null when the byte walk reads it; else why not.</summary>
    public required IReadOnlyList<string?> Left { get; init; }
    /// <summary>For each field: which reader gives it ("the generated layout", a hand-read layout's name, "NVNM's reader").</summary>
    public required IReadOnlyList<string?> By { get; init; }
    /// <summary>A record that holds none of these subrecords has no link the walk reads beyond those at fixed places.</summary>
    public required IReadOnlySet<string> Triggers { get; init; }
    /// <summary>The bulky subrecord whose byte reader (WorldBytes) gives some of the fields: XTV2, NVNM, FNAM or NVMI; null when none.</summary>
    public string? Bulky { get; init; }
    /// <summary>The generated walk reads only the type's components: its own fields are all kept on Mutagen's count (HeldBack).</summary>
    public bool ComponentsOnly { get; init; }
    /// <summary>For each field: the subrecords a record must hold to hold it (its reader's).</summary>
    public required IReadOnlyList<IReadOnlySet<string>> TriggersOf { get; init; }

    readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _unmarked = new(StringComparer.Ordinal);

    internal void MakeIndex()
    {
        for (int i = 0; i < Paths.Count; i++)
        {
            _index[Paths[i]] = i;
            string plain = LinkByteWalker.Unmarked(Paths[i]);
            _unmarked[plain] = _unmarked.ContainsKey(plain) ? -1 : i;
        }
    }

    readonly ConcurrentDictionary<string, bool> _under = new(StringComparer.Ordinal);

    /// <summary>Whether a link field is at or below a path (its kinds of object left out).</summary>
    public bool HasLinksUnder(string path) => _under.GetOrAdd(LinkByteWalker.Unmarked(path), p =>
        Paths.Any(x => LinkByteWalker.Unmarked(x) is var u && (u == p || u.StartsWith(p + ".", StringComparison.Ordinal) || u.StartsWith(p + "[", StringComparison.Ordinal))));

    /// <summary>The field a path names: exactly, or (a path without its kinds of object) the one field it can be.</summary>
    public int IndexOf(string path)
    {
        if (_index.TryGetValue(path, out int i)) return i;
        return _unmarked.TryGetValue(LinkByteWalker.Unmarked(path), out int j) ? j : -1;
    }
}

/// <summary>One link found: its field (an index into <see cref="LinkFields.Paths"/>), the FormID as stored (0: a null
/// link), and where it sits (the subrecord's index in the record, the offset in it).</summary>
public readonly record struct FoundLink(int Field, uint Raw, int Subrecord, int Offset);

/// <summary>
/// Reads a record's links from its bytes (see the file's head). One per thread: it keeps what the schema says of each
/// object from record to record.
/// </summary>
public sealed class LinkByteWalker
{
    readonly RecordSchema _schema;
    readonly ItemLocator.Tables _tables = new();

    public LinkByteWalker(RecordSchema schema) { _schema = schema; }

    public RecordSchema Schema => _schema;

    /// <summary>What went wrong in the last record read (a layout its bytes don't follow); empty when nothing did.</summary>
    public List<string> Problems { get; } = new();
    /// <summary>Where in the last record the walk couldn't follow the bytes: the paths (kinds of object left out) below
    /// which its links may be missing.</summary>
    public List<string> ProblemPaths { get; } = new();

    static readonly ConcurrentDictionary<(RecordSchema, string), LinkFields> Fields = new();

    /// <summary>A path with its kinds of object left out: <c>Properties[*].Object</c>.</summary>
    public static string Unmarked(string path) => path.Contains('{') ? Regex.Replace(path, @"\{[^}]*\}", "") : path;

    static bool IsLink(FieldDef d) => d.Kind is "FormLink" or "FormLinkOrIndex" || d.LinkTo != null && d.Kind is not ("List" or "Object");

    /// <summary>The link fields of a record type (its Mutagen name), and which the walk reads.</summary>
    public static LinkFields FieldsOf(RecordSchema schema, string type) => Fields.GetOrAdd((schema, type), k => MakeFields(k.Item1, k.Item2));

    /// <summary>For each condition place among a type's link rows (a Data.Reference under ConditionData): its parameters'
    /// link field paths (<see cref="ConditionParameters"/>), and the index after its Data.Reference.</summary>
    public static List<(List<string> Paths, int At)> ConditionParameterPaths(SchemaModel m, IReadOnlyList<FieldRow> links)
    {
        var found = new List<(List<string>, int)>();
        var have = links.Select(r => r.Path).ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < links.Count; i++)
        {
            var r = links[i];
            if (!r.Path.EndsWith(".Data.Reference", StringComparison.Ordinal) || r.Def.Object != "ConditionData") continue;
            string head = r.Path[..^"Reference".Length];
            var add = ConditionParameters.LinkNames(m).Select(n => head + n).Where(p => !have.Contains(p)).ToList();
            if (add.Count > 0) found.Add((add, i + 1));
        }
        return found;
    }

    static LinkFields MakeFields(RecordSchema schema, string type)
    {
        var m = schema.Model;
        string sig = m.Objects.TryGetValue(type, out var od) ? od.Sig ?? "" : "";
        var rows = m.Expand(type).ToList();
        string[] Stack(FieldRow r) => r.Stack.Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool Nested(FieldRow r) => Stack(r).Skip(1).Any(o => m.Objects.TryGetValue(o, out var x) && x.ObjType == "Record");
        var links = rows.Where(r => !Nested(r) && IsLink(r.Def)).GroupBy(r => r.Path, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var paths = links.Select(r => r.Path).ToList();
        var subs = links.Select(r => r.Subrecord).ToList();
        // A condition's function parameters (GAPS S156): the schema spells out no ConditionData kind, so each condition
        // place gets its parameters' link fields after its Data.Reference
        foreach (var (path, at) in Enumerable.Reverse(ConditionParameterPaths(m, links)))
        {
            paths.InsertRange(at, path);
            subs.InsertRange(at, path.Select(_ => (string?)"CTDA"));
        }
        var by = new string?[paths.Count];
        var left = new string?[paths.Count];
        string? bulkySig = Enumerable.Range(0, WorldBytes.Paths.Length).Where(i => paths.Contains(WorldBytes.Paths[i])).Select(i => BulkyOf(sig, i)).FirstOrDefault(x => x != null);
        var f = new LinkFields { Bulky = bulkySig, ComponentsOnly = HeldBack.TryGetValue(sig, out var whole) && whole.Paths.Length == 0, Type = type, Paths = paths, Subrecords = subs, Left = left, By = by, Triggers = new HashSet<string>(StringComparer.Ordinal), TriggersOf = paths.Select(_ => (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal)).ToList() };
        f.MakeIndex();
        var triggers = (HashSet<string>)f.Triggers;
        // The bulky subrecords' readers
        foreach (var (field, path) in WorldBytes.Paths.Select((p, i) => (i, p)))
        {
            if (BulkyOf(sig, field) is not { } bulky) continue;
            int i = f.IndexOf(path);
            if (i >= 0) { by[i] = $"{bulky}'s byte reader (WorldBytes)"; triggers.Add(bulky); ((HashSet<string>)f.TriggersOf[i]).Add(bulky); }
        }
        // The hand-read layouts that apply to the type
        foreach (var h in m.HandLayouts.Where(h => h.Kind == "Record" && h.Sig == sig || h.Kind == "AnyRecord"))
        {
            if (h.Kind == "Record" && SkipLayout(sig)) continue;
            foreach (var (path, subsOf) in LayoutLinks(m, h))
            {
                int i = f.IndexOf(path);
                if (i < 0 || by[i] != null) continue;
                by[i] = HandLayoutWalk.Said(HandLayoutWalk.NameOf(h));
                triggers.UnionWith(subsOf); ((HashSet<string>)f.TriggersOf[i]).UnionWith(subsOf);
            }
        }
        // The generated layout: the record's own fields, and inside each run of subrecords the type's own hand-read layout
        // cuts out (a quest's aliases, a perk's ranks and effects, a scene's actions, a package's data inputs, an
        // instance naming rule's rule sets) the fields of the object Mutagen's generated reader reads from that run
        var reach = new Dictionary<string, string?>(StringComparer.Ordinal);
        var top = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        ItemLocator.StaticLinks(schema, type, reach, top);
        var runSubs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in m.HandLayouts.Where(h => h.Kind == "Record" && h.Sig == sig)) runSubs.UnionWith(LayoutSubs(m, h));
        var cuts = CutPaths(m, sig);
        foreach (var cut in cuts) ItemLocator.StaticRun(schema, type, cut, reach);
        bool InRun(string path) => Unmarked(path) is var u && cuts.Any(c => u == c || u.StartsWith(c + ".", StringComparison.Ordinal) || u.StartsWith(c + "[", StringComparison.Ordinal));
        HeldBack.TryGetValue(sig, out var heldBack);
        for (int i = 0; i < paths.Count; i++)
        {
            if (by[i] != null) continue;
            if (heldBack != null && !paths[i].StartsWith("Components[*]", StringComparison.Ordinal) && heldBack.Applies(paths[i]))
            {
                left[i] = heldBack.Why;
                continue;
            }
            if (reach.TryGetValue(paths[i], out var why) && why == null)
            {
                by[i] = "the generated layout";
                var mine = (HashSet<string>)f.TriggersOf[i];
                if (InRun(paths[i])) { triggers.UnionWith(runSubs); mine.UnionWith(runSubs); }
                else
                {
                    string head = paths[i].Split('.', '[', '{')[0];
                    if (top.TryGetValue(head, out var t)) { triggers.UnionWith(t); mine.UnionWith(t); }
                }
            }
            else left[i] = why ?? "not reached by the byte walk";
        }
        // A kind of object a hand-read layout's reader never makes where the layout says which one it makes (an Object's
        // class=, C57 W3: DialogResponsesAdapter's reader makes a plain ScriptFragments, never a SceneScriptFragments): a
        // field under another kind there is never there, so never has a link
        foreach (var (within, field, cls, layout) in OneClassPlaces(m))
        {
            string mark = "." + field + "{";
            for (int i = 0; i < paths.Count; i++)
            {
                if (by[i] != null) continue;
                int at = paths[i].IndexOf(mark, StringComparison.Ordinal);
                if (at < 0) continue;
                // The object the field is in must be the layout's case (its kind marked in the path, or the field's one type)
                string owner = paths[i][..at];
                string? ownerClass = owner.EndsWith('}') ? owner[(owner.LastIndexOf('{') + 1)..^1]
                    : rows.FirstOrDefault(r => r.Path == owner)?.Def.RefObject;
                if (within != null && ownerClass != within) continue;
                int end = paths[i].IndexOf('}', at + mark.Length);
                string kind = end < 0 ? "" : paths[i][(at + mark.Length)..end];
                if (kind.Length == 0 || kind == cls) continue;
                by[i] = $"{HandLayoutWalk.Said(layout)}: its reader makes a {cls} there, never a {kind}";
                left[i] = null;
            }
        }
        return f;
    }

    /// <summary>The places a hand-read layout names the one class Mutagen's reader makes (an Object's class=, C57 W3): the
    /// kind of the TypeSwitch case it is in (null: none), the field, the class, and the layout's name.</summary>
    static List<(string? Within, string Field, string Class, string Layout)> OneClassPlaces(SchemaModel m) => ClassPlaces.GetValue(m, x =>
    {
        var list = new List<(string?, string, string, string)>();
        foreach (var h in x.HandLayouts)
            foreach (var e in h.Element.Descendants().Where(d => d.Name.LocalName == "Object" && d.Attribute("class") != null && d.Attribute("field") != null))
            {
                string? within = e.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Case" && a.Attribute("type") != null)?.Attribute("type")!.Value;
                list.Add((within == null ? null : HandLayouts.ClassName(within.Split(',')[0]), e.Attribute("field")!.Value, HandLayouts.ClassName(e.Attribute("class")!.Value), HandLayoutWalk.NameOf(h)));
            }
        return list;
    });

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SchemaModel, List<(string?, string, string, string)>> ClassPlaces = new();

    /// <summary>The lists and objects a record type's own hand-read layouts cut out of its subrecords for Mutagen's
    /// generated reader: each Segments with a field (its items, <c>Aliases[*]</c>, <c>Ranks[*].Effects[*]</c>) and each
    /// Object read by the generated reader (<c>Rules</c>), as schema paths without kinds.</summary>
    internal static List<string> CutPaths(SchemaModel m, string sig) => Cuts.GetValue(m, _ => new ConcurrentDictionary<string, List<string>>(StringComparer.Ordinal)).GetOrAdd(sig, s => MakeCutPaths(m, s));

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SchemaModel, ConcurrentDictionary<string, List<string>>> Cuts = new();

    /// <summary>
    /// A list that Mutagen's generated reader of the record reads and the schema delimits: no hand-written step, a counter
    /// subrecord (the record XML's counterRecType) and an item object (a weapon's ObjectTemplates, OBTE then each
    /// template's OBTF, FULL and OBTS). A layout's Segments over it only describes what hand-written code reads inside each
    /// item (the OBTS), and cuts no run: the generated walk reads the list, its items as Mutagen's generated reader ends
    /// them (GAPS S164, S165). A list whose items the schema can't end (a perk's ranks: the XML's itemMarkerType, which
    /// the schema doesn't keep) stays the layout's cut.
    /// </summary>
    static bool GeneratedList(SchemaModel m, string? type, string list)
    {
        if (type == null || !m.Objects.ContainsKey(type) || !list.EndsWith("[*]", StringComparison.Ordinal)) return false;
        string plain = list[..^3];
        var row = m.Expand(type).FirstOrDefault(r => r.PlainPath == plain);
        return row != null && row.Def.Kind is "List" or "Array" && !row.Def.Custom && row.Def.Note is { } note && note.Contains("counted by ", StringComparison.Ordinal);
    }

    static List<string> MakeCutPaths(SchemaModel m, string sig)
    {
        string? type = null;
        var defines = m.HandLayouts.Where(x => x.Kind == "Define" && x.Name != null).GroupBy(x => x.Name!).ToDictionary(g => g.Key, g => g.Last().Element, StringComparer.Ordinal);
        var found = new List<string>();
        void Walk(System.Xml.Linq.XElement e, string path, int depth)
        {
            if (depth > 30) return;
            foreach (var c in e.Elements())
            {
                string? field = c.Attribute("field")?.Value;
                string Join(string p) => field == null || field == "." ? p : p.Length == 0 ? field : p + "." + field;
                switch (c.Name.LocalName)
                {
                    case "Use": if (defines.TryGetValue(c.Attribute("name")?.Value ?? "", out var d)) Walk(d, path, depth + 1); break;
                    case "Segments":
                    {
                        string p = field == null ? path : Join(path) + "[*]";
                        if (field != null && !found.Contains(p) && !GeneratedList(m, type, p)) found.Add(p);
                        Walk(c, p, depth + 1);
                        break;
                    }
                    case "Object":
                    {
                        string p = Join(path);
                        if (c.Attribute("reader")?.Value == "generated" && !found.Contains(p)) found.Add(p);
                        Walk(c, p, depth + 1);
                        break;
                    }
                    case "Switch": case "TypeSwitch": case "Case": case "Default": case "AnyOrder": Walk(c, path, depth + 1); break;
                    // Sub, SubRepeat, Generated: the subrecord level ends there (a run is never inside one subrecord)
                }
            }
        }
        foreach (var h in m.HandLayouts.Where(h => h.Kind == "Record" && h.Sig == sig)) { type = h.Element.Attribute("object")?.Value; Walk(h.Element, "", 0); }
        return found;
    }

    /// <summary>Link fields kept on Mutagen's count (profile reads them through Mutagen's object) because Mutagen reads
    /// their bytes wrong, with the GAPS row of the Mutagen finding. No paths: the type's own fields, all of them.</summary>
    sealed record HeldField(string[] Paths, string Why)
    {
        public bool Applies(string path) => Paths.Length == 0 || Paths.Any(p => path == p || path.StartsWith(p + ".", StringComparison.Ordinal) || path.StartsWith(p + "[", StringComparison.Ordinal) || path.StartsWith(p + "{", StringComparison.Ordinal));
    }

    /// <summary>The bulky subrecord whose byte reader gives a WorldBytes field in a record of this signature (when the type
    /// has the field).</summary>
    static string? BulkyOf(string sig, int field) => (sig, field) switch
    {
        ("CELL" or "REFR", WorldBytes.Traversal) => "XTV2",
        (_, >= WorldBytes.NavWorldParent and <= WorldBytes.NavDoor) => "NVNM", // a navmesh, and a generic base form that holds one
        ("SFTR", WorldBytes.Patterns or WorldBytes.Patterns2) => "FNAM",
        ("NAVI", >= WorldBytes.MiMesh and <= WorldBytes.MiCell) => "NVMI",
        _ => null,
    };

    /// <summary>The link fields kept on Mutagen's count, by record type: none yet. Every other field is walked, held
    /// field by field against Mutagen's object (sfsreport-tests link-census on Starfield.esm, the official plugins and
    /// two mods).</summary>
    static readonly Dictionary<string, HeldField> HeldBack = new(StringComparer.Ordinal)
    {
    };

    /// <summary>A record type whose own hand-read layout lays out only what a bulky reader reads (CELL's traversals,
    /// NAVI's map infos): its layout isn't walked.</summary>
    static bool SkipLayout(string sig) => sig is "CELL" or "NAVI";

    /// <summary>Every FormID a hand-read layout names, as a schema path with the kinds its Cases pick, with the
    /// subrecords the layout reads.</summary>
    static List<(string Path, HashSet<string> Subs)> LayoutLinks(SchemaModel m, HandLayout h)
    {
        var defines = m.HandLayouts.Where(x => x.Kind == "Define" && x.Name != null).ToDictionary(x => x.Name!, x => x.Element, StringComparer.Ordinal);
        var found = new List<(string, HashSet<string>)>();
        var subs = new HashSet<string>(StringComparer.Ordinal);
        if (h.Kind == "AnyRecord") subs.UnionWith(HandLayoutWalk.Sigs(h));
        var using_ = new Dictionary<string, int>(StringComparer.Ordinal);
        // The kinds a Case picked for the object at a path: the values after the Switch are that object's too (a package
        // input's target after the ANAM that picked PackageDataTarget)
        var kinds = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Walk(System.Xml.Linq.XElement e, string path, int depth)
        {
            if (depth > 60) return;
            foreach (var c in e.Elements())
            {
                string n = c.Name.LocalName;
                string? field = c.Attribute("field")?.Value;
                string Join(string p, string? fld) => fld == null || fld == "." ? p : p.Length == 0 ? fld : p + "." + fld;
                switch (n)
                {
                    case "Use":
                    {
                        // A Define used inside itself (a script struct's members are script properties again) is followed
                        // twice, as deep as the schema spells such paths out
                        string name = c.Attribute("name")?.Value ?? "";
                        if (!defines.TryGetValue(name, out var d) || using_.GetValueOrDefault(name) >= 2) break;
                        using_[name] = using_.GetValueOrDefault(name) + 1;
                        Walk(d, path, depth + 1);
                        using_[name]--;
                        break;
                    }
                    case "Sub": case "SubRepeat": case "Segments":
                        foreach (var s in (c.Attribute("sig")?.Value ?? c.Attribute("start")?.Value ?? "").Split(',')) if (s.Length == 4) subs.Add(s);
                        Walk(c, n == "Sub" ? Join(path, field) : field == null ? path : Join(path, field) + "[*]", depth + 1);
                        break;
                    case "Repeat": Walk(c, field == null ? path : Join(path, field) + "[*]", depth + 1); break;
                    case "Object": Walk(c, Join(path, field), depth + 1); break;
                    case "Case": case "Default":
                    {
                        string? type = c.Attribute("type")?.Value;
                        if (type != null && path.Length > 0) (kinds.TryGetValue(path, out var ks) ? ks : kinds[path] = new HashSet<string>(StringComparer.Ordinal)).Add(HandLayouts.ClassName(type.Split(',')[0]));
                        Walk(c, type == null ? path : path + "{" + HandLayouts.ClassName(type.Split(',')[0]) + "}", depth + 1);
                        break;
                    }
                    case "FormID": if (field != null) found.Add((Join(path, field).EndsWith(".FormKey", StringComparison.Ordinal) ? Join(path, field)[..^".FormKey".Length] : Join(path, field), subs)); break;
                    default: Walk(c, path, depth + 1); break;
                }
            }
        }
        Walk(h.Element, "", 0);
        IEnumerable<string> Kinds(string p)
        {
            yield return p;
            foreach (var (at, ks) in kinds)
                if (p.StartsWith(at + ".", StringComparison.Ordinal))
                    foreach (var k in ks) yield return at + "{" + k + "}" + p[at.Length..];
        }
        // A Case's kind may stand where the schema writes none (a VMAD's adapter): the path is matched with fewer kinds too
        return found.SelectMany(x => Kinds(x.Item1).SelectMany(Variants).Select(p => (p, x.Item2))).ToList();
    }

    /// <summary>A path, then the same with its kinds of object dropped one by one from the outside in.</summary>
    internal static IEnumerable<string> Variants(string path)
    {
        yield return path;
        var marks = Regex.Matches(path, @"\{[^}]*\}").Cast<Match>().ToList();
        for (int k = 1; k <= marks.Count; k++)
        {
            string p = path;
            foreach (var mk in marks.Take(k).Reverse()) p = p.Remove(mk.Index, mk.Length);
            yield return p;
        }
    }

    /// <summary>
    /// Every link of the record's link fields, from its bytes. <paramref name="head"/> is the record's 24-byte header and
    /// <paramref name="data"/> its subrecords, inflated. <paramref name="localized"/>: the plugin keeps its texts in
    /// strings files (a translated text is a 4-byte ID).
    /// </summary>
    /// <param name="sigOf">The record type of the record a stored FormID names in the plugin read (null: none, or not
    /// known): an owner in a container entry's COED is a faction's, an NPC's or neither by it, as Mutagen's reader asks
    /// its record cache.</param>
    public List<FoundLink> Walk(LinkFields fields, ReadOnlySpan<byte> head, ReadOnlySpan<byte> data, bool localized, Func<uint, string?>? sigOf = null)
        => Walk(fields, RawRecord.FromInflated(head, data), localized, sigOf);

    public List<FoundLink> Walk(LinkFields fields, RawRecord raw, bool localized, Func<uint, string?>? sigOf = null) => Walk(fields, raw, localized, sigOf, null, null);

    /// <summary>
    /// Every value of the record's fields the generated layout reads, from its bytes (GAPS S153): the same walk as
    /// <see cref="Walk(LinkFields, RawRecord, bool, Func{uint, string?}?)"/>, each value, object and part it doesn't read
    /// given to <paramref name="sink"/>. The bulky subrecords' readers and the hand-read layouts give no values here:
    /// what they read is said unread.
    /// </summary>
    /// <param name="want">With a value walk: whether the part at a path (kinds left out, list indexes as <c>[*]</c>) is
    /// wanted; the walk passes over the others (a list view needs only its list and what holds it).</param>
    /// <param name="component">Mutagen's own object of the record's component k, read alone (its BFCB … BFCE): for a
    /// layout's TypeSwitch that the bytes don't decide, which chooses by the class Mutagen made (a volumes item's ender of
    /// type 11: VolumesUnknownEnderEmpty or …EmptyPadded, which Mutagen tells by what still fits in the VLMS). Null: such
    /// a place is said unread.</param>
    public void WalkValues(LinkFields fields, RawRecord raw, bool localized, IRecordValueSink sink, Func<uint, string?>? sigOf = null, Func<string, bool>? want = null, Func<int, object?>? component = null)
        => Walk(fields, raw, localized, sigOf, sink, want, component);

    List<FoundLink> Walk(LinkFields fields, RawRecord raw, bool localized, Func<uint, string?>? sigOf, IRecordValueSink? values, Func<string, bool>? want, Func<int, object?>? component = null)
    {
        Problems.Clear(); ProblemPaths.Clear();
        var found = new List<FoundLink>();
        var seen = new HashSet<(int, int)>();
        string sig = raw.Signature;
        void Add(string path, uint value, int sub, int offset)
        {
            int f = fields.IndexOf(path);
            if (f < 0) return;
            if (sub >= 0 && !seen.Add((sub, offset))) return;
            found.Add(new FoundLink(f, value, sub, offset));
        }
        // The bulky subrecords, outside components
        var bulkyAt = new HashSet<int>();
        string? bulky = fields.Bulky;
        if (bulky != null)
        {
            int depth = 0, ordinal = 0;
            var sink = new Sink();
            for (int i = 0; i < raw.Subrecords.Count; i++)
            {
                var s = raw.Subrecords[i];
                if (s.Signature == "BFCB") depth++;
                else if (s.Signature == "BFCE" && depth > 0) depth--;
                else if (depth == 0 && s.Signature == bulky && values != null) bulkyAt.Add(i);
                else if (depth == 0 && s.Signature == bulky)
                {
                    sink.Links.Clear();
                    var c = raw.Content(s);
                    bool ok = bulky switch
                    {
                        "XTV2" => WorldBytes.Traversals(_schema.Model, c, sink),
                        "NVNM" => WorldBytes.Geometry(_schema.Model, c, raw.FormVersion, sink),
                        "FNAM" => ordinal < 2 && WorldBytes.PatternGrid(_schema.Model, c, ordinal == 0 ? WorldBytes.Patterns : WorldBytes.Patterns2, sink),
                        "NVMI" => WorldBytes.MapInfo(_schema.Model, c, sink),
                        _ => false,
                    };
                    ordinal++;
                    if (!ok)
                    {
                        Problems.Add($"{s.Signature} #{ordinal}: its bytes don't end where Mutagen's layout of it ends");
                        foreach (var wp in WorldBytes.Paths) if (fields.IndexOf(wp) >= 0) ProblemPaths.Add(Unmarked(wp));
                        continue;
                    }
                    bulkyAt.Add(i);
                    foreach (var (field, value, count) in sink.Links)
                    {
                        int fi = fields.IndexOf(WorldBytes.Paths[field]);
                        if (fi < 0) continue;
                        for (uint n = 0; n < count; n++) found.Add(new FoundLink(fi, value, i, -1));
                    }
                }
            }
        }
        // The hand-read layouts (each value's place is its own, so one reached by the generated walk too counts once)
        var model = _schema.Model;
        List<(int S, int E, string Path)>? runs = null;
        Dictionary<string, string>? marks = null;
        // A value walk leaves out a layout that cuts no run and lays out nothing the walk is asked for (NeededForValues)
        bool Use(HandLayout h) => Applies(h, raw) && (values == null || NeededForValues(h, sig, want));
        HandLayoutWalk? walked = null;
        var cutSet = CutSet(model, sig);
        if (model.HandLayouts.Any(Use))
        {
            var components = new ComponentKinds(model, raw);
            var hand = walked = HandLayoutWalk.RunBytes(model, raw, localized, p => ClassOf(fields.Type, raw, p, components, component), Use, names: values != null);
            // The runs of subrecords the record type's own layout cut out for Mutagen's generated reader (a quest's
            // aliases, a perk's ranks and effects …), each read below as the object Mutagen makes of it; not a list the
            // record's generated reader reads itself (GeneratedList: a weapon's object templates)
            foreach (var (path, run) in hand.Segments.Concat(hand.Runs))
                if (hand.CutBy.TryGetValue(path, out var cutBy) && cutBy.StartsWith("Record ", StringComparison.Ordinal) && run.E > run.S && cutSet.Contains(Indexless(path)))
                    (runs ??= new()).Add((run.S, run.E, path));
            marks = hand.Marks;
            foreach (var v in values != null ? Enumerable.Empty<HandLayoutWalk.Value>() : (IEnumerable<HandLayoutWalk.Value>)hand.Values.Values)
            {
                if (v.Kind != "FormID" || bulkyAt.Contains(v.Sub)) continue;
                var c = raw.Content(raw.Subrecords[v.Sub]);
                if (v.Offset + 4 > c.Length) continue;
                string path = Marked(v.Path, hand.Marks);
                foreach (var p in Variants(path))
                    if (fields.IndexOf(p) >= 0) { Add(p, BinaryPrimitives.ReadUInt32LittleEndian(c[v.Offset..]), v.Sub, v.Offset); break; }
            }
            foreach (var p in hand.Problems) Problems.Add(p);
            // What a layout read is the layout's: the generated walk doesn't read it again
            bulkyAt.UnionWith(hand.Read);
        }
        // The generated layout
        if (values != null) { ItemLocator.Values(_schema, raw, _tables, fields.Type, localized, bulkyAt, sigOf, values, Problems, runs, want, walked, cutSet); return found; }
        ItemLocator.Links(_schema, raw, _tables, fields.Type, localized, bulkyAt, fields.HasLinksUnder, sigOf, Add, Problems, ProblemPaths, fields.ComponentsOnly, runs, marks);
        found.Sort((a, b) => a.Subrecord != b.Subrecord ? a.Subrecord.CompareTo(b.Subrecord) : a.Offset.CompareTo(b.Offset));
        return found;
    }

    static readonly ConcurrentDictionary<(RecordSchema, string), IReadOnlyDictionary<string, string?>> Reaches = new();

    /// <summary>
    /// What a value walk of a record type gives (<see cref="WalkValues"/>, GAPS S153): each value and list's entries it
    /// reaches (null) or why not, by schema path with the kinds of object (<c>Components[*]{ItemsComponent}.Items[*].Name</c>).
    /// A path that isn't there isn't reached either. What the type's own hand-read layout cuts out is a hand-read
    /// layout's run, unread.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> ValueReach(RecordSchema schema, string type) => Reaches.GetOrAdd((schema, type), k =>
    {
        var reach = ItemLocator.StaticValues(k.Item1, k.Item2);
        var m = k.Item1.Model;
        string sig = m.Objects.TryGetValue(k.Item2, out var od) ? od.Sig ?? "" : "";
        // What a bulky subrecord holds (NVNM's geometry, a surface tree's FNAM grids, the map's NVMI, XTV2) is its byte
        // reader's (WorldBytes), never the value walk's
        var held = new List<string>();
        if (FieldsOf(k.Item1, k.Item2).Bulky is { } bulky)
        {
            held = m.Expand(k.Item2).Where(r => r.Subrecord == bulky).Select(r => Unmarked(r.Path)).Distinct().ToList();
            foreach (var key in reach.Keys.ToList())
                if (Unmarked(key) is var u && held.Any(h => u == h || u.StartsWith(h + ".", StringComparison.Ordinal) || u.StartsWith(h + "[", StringComparison.Ordinal))) reach[key] = $"{bulky}'s byte reader's";
        }
        if (!SkipLayout(sig))
        {
            // The runs the type's own layout cuts (a quest's aliases, a perk's ranks and effects …): each walked as the
            // object Mutagen's generated reader makes of it
            foreach (var cut in CutPaths(m, sig))
            {
                foreach (var key in reach.Keys.ToList())
                    if (Unmarked(key) is var u && (u == cut || u.StartsWith(cut + ".", StringComparison.Ordinal) || u.StartsWith(cut + "[", StringComparison.Ordinal))) reach.Remove(key);
                ItemLocator.StaticRunValues(k.Item1, k.Item2, cut, reach);
                if (cut.EndsWith("[*]", StringComparison.Ordinal)) reach[cut] = null;
            }
        }
        // What the hand-read layouts that apply to the type lay out (the record type's, and each AnyRecord's: VMAD, the
        // components' BFCB): the value walk gives it from the layout where the generated walk comes to the field (GAPS
        // S164); where the walk can't come to it, it says the part above unread. Not what a bulky subrecord's byte
        // reader holds.
        foreach (var h in m.HandLayouts.Where(h => h.Kind == "Record" && h.Sig == sig && !SkipLayout(sig) || h.Kind == "AnyRecord"))
            foreach (var p in HandLaidOut.Paths(m, h))
                if (!held.Any(x => p == x || p.StartsWith(x + ".", StringComparison.Ordinal) || p.StartsWith(x + "[", StringComparison.Ordinal))) reach[p] = null;
        // A list whose file holds only its first entries (a package's value blocks, which its template's input names
        // extend) isn't known from the bytes
        if (!SkipLayout(sig))
            foreach (var list in PrefixLists(m, sig))
                foreach (var key in reach.Keys.ToList())
                    if (Unmarked(key) is var u && (u == list + "[*]" || u.StartsWith(list + "[", StringComparison.Ordinal))) reach[key] = "the file holds only its first entries (a hand-read layout's prefix list)";
        return reach;
    });

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SchemaModel, ConcurrentDictionary<string, HashSet<string>>> CutSets = new();

    /// <summary>The type's cut paths (<see cref="CutPaths"/>) as a set.</summary>
    static HashSet<string> CutSet(SchemaModel m, string sig) => CutSets.GetValue(m, _ => new ConcurrentDictionary<string, HashSet<string>>(StringComparer.Ordinal))
        .GetOrAdd(sig, s => CutPaths(m, s).ToHashSet(StringComparer.Ordinal));

    /// <summary>The lists a record type's own layout holds only the first entries of (Segments prefix="true": a package's
    /// value blocks, which its template's input names extend), as paths without kinds.</summary>
    static List<string> PrefixLists(SchemaModel m, string sig)
    {
        var found = new List<string>();
        foreach (var h in m.HandLayouts.Where(h => h.Kind == "Record" && h.Sig == sig))
            foreach (var e in h.Element.Descendants().Where(d => d.Name.LocalName == "Segments" && d.Attribute("prefix")?.Value == "true" && d.Attribute("field") != null && d.Attribute("key") == null))
                found.Add(string.Join(".", e.AncestorsAndSelf().Reverse().Where(a => a.Attribute("field") != null && a.Name.LocalName != "Record")
                    .Select(a => a.Attribute("field")!.Value + (a != e && a.Name.LocalName is "Segments" or "SubRepeat" or "Repeat" ? "[*]" : ""))));
        return found;
    }

    /// <summary>A walk's path with its list indexes as [*]: <c>Ranks[0].Effects[2]</c> → <c>Ranks[*].Effects[*]</c>.</summary>
    internal static string Indexless(string path)
    {
        if (path.IndexOf('[') < 0) return path;
        var sb = new System.Text.StringBuilder(path.Length);
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '[') { int e = path.IndexOf(']', i); sb.Append("[*]"); i = e < 0 ? path.Length : e; continue; }
            sb.Append(path[i]);
        }
        return sb.ToString();
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Func<string, bool>, ConcurrentDictionary<HandLayout, bool>> Needed = new();

    /// <summary>
    /// Whether a value walk needs a layout: always when the walk is asked for everything, or when it is the record
    /// type's own layout and cuts runs for its generated reader (the record's own fields must not see them); else when
    /// it lays out a part the walk is asked for (<see cref="HandLaidOut.Paths"/>). A layout left out costs nothing: a
    /// list's walk doesn't run the components' BFCB layout unless the list is one it lays out.
    /// </summary>
    bool NeededForValues(HandLayout h, string sig, Func<string, bool>? want)
    {
        if (want == null) return true;
        if (h.Kind == "Record" && CutPaths(_schema.Model, sig).Count > 0) return true;
        return Needed.GetValue(want, _ => new ConcurrentDictionary<HandLayout, bool>(ReferenceEqualityComparer.Instance))
            .GetOrAdd(h, x => HandLaidOut.Paths(_schema.Model, x).Any(want));
    }

    /// <summary>A layout applies to a record that holds one of the subrecords it reads (a record type's own layout) or its
    /// subrecord (an AnyRecord's).</summary>
    bool Applies(HandLayout h, RawRecord raw)
    {
        if (h.Kind == "Record" && h.Sig == raw.Signature && !SkipLayout(raw.Signature))
        {
            var subs = LayoutSubs(_schema.Model, h);
            foreach (var s in raw.Subrecords) if (subs.Contains(s.Signature)) return true;
            return false;
        }
        return h.Kind == "AnyRecord" && HandLayoutWalk.Holds(h, raw);
    }

    // By the layout object itself: a HandLayout record's own hash reads its whole XML text
    static readonly ConcurrentDictionary<HandLayout, HashSet<string>> SubsOfLayout = new(ReferenceEqualityComparer.Instance);

    /// <summary>Every subrecord a layout reads or passes over (its Subs, SubRepeats, Segments' starts and Generated
    /// ones, through its Uses).</summary>
    static HashSet<string> LayoutSubs(SchemaModel m, HandLayout h) => SubsOfLayout.GetOrAdd(h, x =>
    {
        var defines = m.HandLayouts.Where(d => d.Kind == "Define" && d.Name != null).GroupBy(d => d.Name!).ToDictionary(g => g.Key, g => g.Last().Element, StringComparer.Ordinal);
        var set = new HashSet<string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        void Walk(System.Xml.Linq.XElement e)
        {
            foreach (var c in e.Elements())
            {
                foreach (var a in new[] { "sig", "start", "sigs" })
                    if (c.Attribute(a)?.Value is { } v) foreach (var sg in v.Split(',')) if (sg.Length == 4) set.Add(sg);
                if (c.Name.LocalName == "Use" && c.Attribute("name")?.Value is { } n && used.Add(n) && defines.TryGetValue(n, out var d)) Walk(d);
                Walk(c);
            }
        }
        Walk(x.Element);
        return set;
    });

    /// <summary>The class Mutagen makes at a path, for a hand-read layout's TypeSwitch: a component's by its BFCB text,
    /// else the schema's when it has one kind there.</summary>
    string? ClassOf(string type, RawRecord raw, string path, ComponentKinds components, Func<int, object?>? component = null)
    {
        var m = _schema.Model;
        if (path.StartsWith("Components[", StringComparison.Ordinal) && path.EndsWith(']') && path.IndexOf('.') < 0
            && int.TryParse(path.AsSpan(11, path.Length - 12), NumberStyles.None, CultureInfo.InvariantCulture, out int k))
            return components.Of(k);
        string plain = Regex.Replace(path, @"\[\d+\]", "");
        var rows = m.Resolve(type, plain).Found.Select(f => f.Row).ToList();
        var def = rows.Count == 0 ? null : rows[0].Def;
        string? obj = def == null ? null : def.Kind == "List" ? m.AllFields(def.Object).Select(x => x.F).FirstOrDefault(x => x.Path == def.Path + "[*]")?.RefObject : def.RefObject;
        if (obj != null && m.Variants(obj).Count == 0) return obj;
        // Inside a component, where neither the schema nor the bytes decide: the class Mutagen's reader made, from its own
        // read of that component alone
        return component != null && path.StartsWith("Components[", StringComparison.Ordinal) ? ComponentKinds.MadeBy(m, component, path) : null;
    }

    /// <summary>A hand-read layout's value path with the kinds its Cases picked, list indexes as [*].</summary>
    static string Marked(string path, Dictionary<string, string> marks)
    {
        if (marks.Count == 0) return Regex.Replace(path, @"\[\d+\]", "[*]");
        var sb = new System.Text.StringBuilder();
        int start = 0;
        for (int i = 0; i <= path.Length; i++)
        {
            if (i < path.Length && path[i] != '.') continue;
            string prefix = path[..i];
            sb.Append(path, start, i - start);
            if (marks.TryGetValue(prefix, out var cls)) sb.Append('{').Append(cls).Append('}');
            if (i < path.Length) sb.Append('.');
            start = i + 1;
        }
        return Regex.Replace(sb.ToString(), @"\[\d+\]", "[*]");
    }

    sealed class Sink : IWorldLinkSink
    {
        public readonly List<(int Field, uint Raw, uint Count)> Links = new();
        public void Link(int field, uint raw, uint count) => Links.Add((field, raw, count));
    }
}

/// <summary>The generated layout's part of the link walk (LinkWalk.cs): the evidence walk's subrecord claims, read with
/// no Mutagen object.</summary>
internal sealed partial class ItemLocator
{
    Action<string, uint, int, int>? _link;
    List<string>? _linkProblems;
    bool _linkLocalized;
    HashSet<int>? _linkSkip;
    Func<string, bool>? _linksUnder;
    Func<uint, string?>? _sigOf;
    List<string>? _problemPaths;
    bool _componentsOnly;
    HashSet<int>? _hidden;
    /// <summary>Walking a run a hand-read layout cut out (its object's own hand-written lists may call generated readers).</summary>
    bool _inRun;
    /// <summary>The value walk's receiver (<see cref="LinkByteWalker.WalkValues"/>, GAPS S153); null in a link walk.</summary>
    IRecordValueSink? _values;
    /// <summary>A value walk's choice of the parts it reads, by path with kinds left out and indexes as [*]; null: all.</summary>
    Func<string, bool>? _want;

    static readonly ConcurrentDictionary<(Func<string, bool>, string, string?, string), bool[]> WantedCache = new();

    /// <summary>For each group of an object at a place (kinds left out, indexes as [*]): whether a value walk reads it. A
    /// data block is read whole when one of its fields is wanted: its first field reads the others.</summary>
    bool[] Wanted(string obj, string? map, string place, List<Group> groups) => WantedCache.GetOrAdd((_want!, obj, map, place), _ =>
    {
        var w = groups.Select(g => _want!(place.Length == 0 ? g.Name : place + "." + g.Name)).ToArray();
        var keep = (bool[])w.Clone();
        for (int i = 0; i < groups.Count; i++)
            if (!w[i] && groups[i].Own != null && groups[i].Kind is GroupKind.Single or GroupKind.StructObj or GroupKind.GenderedOne or GroupKind.ListArray)
                for (int j = 0; j < groups.Count; j++)
                    if (j != i && w[j] && groups[j].Own == groups[i].Own) { keep[i] = true; break; }
        return keep;
    });

    /// <summary>A walk's path with its kinds of object left out and its list indexes as [*].</summary>
    static string General(string path)
    {
        if (path.IndexOfAny(GeneralChars) < 0) return path;
        var sb = new System.Text.StringBuilder(path.Length);
        for (int i = 0; i < path.Length; i++)
        {
            char ch = path[i];
            if (ch == '{') { int e = path.IndexOf('}', i); i = e < 0 ? path.Length : e; continue; }
            if (ch == '[') { int e = path.IndexOf(']', i); sb.Append("[*]"); i = e < 0 ? path.Length : e; continue; }
            sb.Append(ch);
        }
        return sb.ToString();
    }
    static readonly char[] GeneralChars = { '{', '[' };

    /// <summary>A list's entry: <c>[*]</c> in a link walk (the link fields' paths), its index in a value walk.</summary>
    string Each(string path, long k) => _values == null ? path + "[*]" : path + "[" + k.ToString(CultureInfo.InvariantCulture) + "]";

    /// <summary>In a value walk, a part whose values this walk doesn't give.</summary>
    void Unread(string path, string why) => _values?.Unread(path, why);

    /// <summary>
    /// The values of the record's fields as Mutagen's generated reader lays them out, given to <paramref name="sink"/>
    /// (GAPS S153), by the walk that gives the links: each value with its place in its subrecord, each object read, and
    /// each part the walk passes over (a hand-read layout's run or subrecord, Mutagen's hand-written code, a layout the
    /// bytes don't follow) said unread; a condition's values by the record XML's layout of a CTDA (ConditionValues). <paramref name="skip"/>: subrecords another reader takes.
    /// </summary>
    /// <param name="hand">The hand-read layouts walked over the record (with the names each object's layout gives):
    /// where the generated walk meets a field one of them names, its values are the layout's (GAPS S164).</param>
    /// <param name="cuts">The lists and objects the record type's own layout cuts into runs (LinkByteWalker.CutPaths):
    /// the runs give them, each walked as the object Mutagen's generated reader makes of it.</param>
    internal static void Values(RecordSchema schema, RawRecord raw, Tables tables, string type, bool localized, HashSet<int> skip, Func<uint, string?>? sigOf, IRecordValueSink sink, List<string> problems, List<(int S, int E, string Path)>? runs, Func<string, bool>? want = null,
        HandLayoutWalk? hand = null, HashSet<string>? cuts = null)
    {
        var l = new ItemLocator(schema, raw, null, _ => "", null, tables) { _link = (_, _, _, _) => { }, _linkProblems = problems, _linkLocalized = localized, _linkSkip = skip, _linksUnder = _ => true, _sigOf = sigOf, _problemPaths = new List<string>(), _values = sink, _want = want, _hand = hand, _cuts = cuts };
        if (hand != null)
        {
            // Where a layout didn't follow the bytes, what it names there isn't known; a list whose file holds only its
            // first entries (a package's value blocks) isn't either
            foreach (var p in hand.StoppedAt) sink.Unread(p, "Mutagen's hand-read layout didn't follow the bytes here");
            foreach (var p in hand.Prefixes) sink.Unread(p, "the file holds only the first entries of the list (a hand-read layout's prefix list)");
        }
        if (runs != null)
        {
            l._hidden = new HashSet<int>();
            foreach (var r in runs) for (int x = r.S; x < r.E; x++) l._hidden.Add(x);
        }
        try { l.LinkObject(type, null, null, 0, raw.Subrecords.Count, ""); }
        catch (NotFound e) { problems.Add(e.Message); sink.Unread("", e.Message); }
        catch (NotFollowed e) { problems.Add(e.Message); sink.Unread("", e.Message); }
        if (runs == null) return;
        // The runs, each read as Mutagen's generated reader reads the object the layout cut it for (a quest's alias, a
        // perk's rank and its effects): the runs inside it are theirs
        foreach (var r in runs)
        {
            if (want != null && !want(General(r.Path))) continue;
            l._hidden = new HashSet<int>();
            foreach (var o in runs) if (o != r && o.S >= r.S && o.E <= r.E) for (int x = o.S; x < o.E; x++) l._hidden.Add(x);
            l._inRun = true;
            try { l.LinkRun(type, r.Path, hand?.Marks ?? new Dictionary<string, string>(), r.S, r.E); }
            catch (NotFollowed e) { problems.Add($"{r.Path}: {e.Message}"); sink.Unread(r.Path, e.Message); }
            catch (NotFound e) { problems.Add(e.Message); sink.Unread(r.Path, e.Message); }
        }
        l._hidden = null;
        l._inRun = false;
    }

    sealed class NotFollowed(string why) : Exception(why);

    /// <summary>The links of the record's fields as Mutagen's generated reader lays them out, given to
    /// <paramref name="link"/> (path, FormID as stored, subrecord, offset). Subrecords in <paramref name="skip"/> are
    /// another reader's.</summary>
    /// <param name="linksUnder">Whether the type has a link field at or below a path: a part the walk can't follow is a
    /// problem only then.</param>
    /// <param name="runs">The runs of subrecords the record type's own hand-read layout cut out (HandLayoutWalk's Segments
    /// and Runs, by the item's path, <c>Ranks[0].Effects[1]</c>): Mutagen's hand-written code takes them from the record's
    /// subrecords and its generated reader of the item's class reads each. <paramref name="marks"/>: the class the
    /// layout picked at a path.</param>
    internal static void Links(RecordSchema schema, RawRecord raw, Tables tables, string type, bool localized, HashSet<int> skip, Func<string, bool> linksUnder, Func<uint, string?>? sigOf, Action<string, uint, int, int> link, List<string> problems, List<string> problemPaths, bool componentsOnly = false,
        List<(int S, int E, string Path)>? runs = null, IReadOnlyDictionary<string, string>? marks = null)
    {
        var l = new ItemLocator(schema, raw, null, _ => "", null, tables) { _link = link, _linkProblems = problems, _linkLocalized = localized, _linkSkip = skip, _linksUnder = linksUnder, _sigOf = sigOf, _problemPaths = problemPaths, _componentsOnly = componentsOnly };
        if (runs != null)
        {
            // The record's own fields never see a run's subrecords: Mutagen's hand-written code took them
            l._hidden = new HashSet<int>();
            foreach (var r in runs) for (int x = r.S; x < r.E; x++) l._hidden.Add(x);
        }
        try { l.LinkObject(type, null, null, 0, raw.Subrecords.Count, ""); }
        catch (NotFound e) { problems.Add(e.Message); }
        if (runs == null || componentsOnly) return;
        foreach (var r in runs)
        {
            // Inside a run, the runs inside it (a perk rank's effects) are theirs
            l._hidden = new HashSet<int>();
            foreach (var o in runs) if (o != r && o.S >= r.S && o.E <= r.E) for (int x = o.S; x < o.E; x++) l._hidden.Add(x);
            l._inRun = true;
            try { l.LinkRun(type, r.Path, marks ?? new Dictionary<string, string>(), r.S, r.E); }
            catch (NotFollowed e)
            {
                string plain = Regex.Replace(r.Path, @"\[\d+\]", "[*]");
                if (linksUnder(plain)) { problems.Add($"{r.Path}: {e.Message}"); problemPaths.Add(plain); }
            }
            catch (NotFound e) { problems.Add(e.Message); }
        }
        l._hidden = null;
    }

    /// <summary>A run of subrecords cut out by a hand-read layout, read as Mutagen's generated reader reads the object at
    /// <paramref name="path"/> (list indexes as numbers): its class is the one the layout picked, or the one the schema
    /// has there; a condition's run is its CTDA.</summary>
    void LinkRun(string type, string path, IReadOnlyDictionary<string, string> marks, int s, int e)
    {
        string obj = type, prefix = "", sofar = "";
        string? map = null, gen = null;
        var segs = path.Split('.');
        for (int i = 0; i < segs.Length; i++)
        {
            var mm = Regex.Match(segs[i], @"^([^\[]+)(\[\d+\])?$");
            if (!mm.Success) throw new NotFollowed($"{path}: no field path");
            string name = mm.Groups[1].Value;
            bool indexed = mm.Groups[2].Success;
            sofar = sofar.Length == 0 ? segs[i] : sofar + "." + segs[i];
            var g = Groups(obj, map).FirstOrDefault(x => x.Name == name) ?? throw new NotFollowed($"{obj} has no field {name}");
            var def = indexed && g.Item != null ? g.Item : g.Main;
            string ro = def.RefObject ?? throw new NotFollowed($"{sofar}: the schema doesn't say what object it holds");
            // A value walk keeps the item's index (Aliases[3]); a link walk's paths name the list's items as [*]
            string at = PathOf(prefix, name) + (indexed ? _values != null ? mm.Groups[2].Value : "[*]" : "");
            if (i == segs.Length - 1 && IsCondition(ro))
            {
                if (Sig(s) != "CTDA") throw new NotFollowed($"{sofar}: a condition whose run doesn't start with CTDA");
                ConditionLinks(s, ro, at);
                if (_values != null) ConditionValues(s, at);
                return;
            }
            var targets = TargetsOf(ro);
            string cls = marks.TryGetValue(sofar, out var mk) && targets.Any(t => t.Name == mk) ? mk
                : targets.Count == 1 ? targets[0].Name
                : throw new NotFollowed($"{sofar}: {ro} has {targets.Count} kinds, and the layout didn't pick one");
            prefix = at + MarkOf(ro, cls);
            map = def.SigMap ?? g.Map ?? map;
            gen = def.GenericArg == "T" ? gen : def.GenericArg ?? gen;
            obj = cls;
        }
        LinkObject(obj, map, gen, s, e, prefix);
    }

    /// <summary>The subrecords a link walk can't see where it stands (another reader's runs): their signature reads as this.</summary>
    const string HiddenSig = "\0\0\0\0";

    static string PathOf(string prefix, string name) => prefix.Length == 0 ? name : prefix + "." + name;

    /// <summary>The kinds of object a field can hold, each with the mark the schema writes after the field's path.</summary>
    IReadOnlyList<Target> TargetsOf(string refObj) => _m.Targets(refObj);

    /// <summary>The mark the schema writes for <paramref name="kind"/> made where the field holds <paramref name="refObj"/>.
    /// In a value walk none: its paths name no kinds (the kind of each object comes with it, IRecordValueSink.Object).</summary>
    string MarkOf(string refObj, string kind) => _values != null ? "" : TargetsOf(refObj).FirstOrDefault(t => t.Name == kind)?.Marker ?? "";

    void LinkObject(string obj, string? map, string? gen, int from, int to, string prefix)
    {
        _values?.Object(prefix, KindName(obj, gen));
        _items = new(); _halves = new();
        var claims = Claim(obj, map, from, to);
        var groups = _lastGroups!;
        var items = _items; var halves = _halves;
        var claimedBy = new Dictionary<int, int>();
        for (int x = from; x < to; x++) if (claims[x - from] >= 0) claimedBy[claims[x - from]] = claimedBy.GetValueOrDefault(claims[x - from]) + 1;
        var wanted = _want == null ? null : Wanted(obj, map, General(prefix), groups);
        for (int gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            // At the record's own level of a type whose own fields are kept on Mutagen's count: only its components
            if (_componentsOnly && prefix.Length == 0 && g.Name != "Components") continue;
            // A value walk passes over what it isn't asked for
            if (wanted != null && !wanted[gi]) continue;
            // A field one of Mutagen's hand-read layouts names here: its values are the layout's (GAPS S164)
            if (_values != null && HandNames(prefix) is { } named && named.Contains(g.Name)) { HandGroup(prefix, obj, gen, g); continue; }
            try
            {
                if (g.Halves is { } hg)
                {
                    // Each half after its marker, read by its object's generated reader
                    if (halves.TryGetValue(gi, out var hv))
                    {
                        foreach (var (s, e) in hv.Male) LinkObject(hg.Male!.RefObject!, hg.Male.SigMap ?? hg.Map, gen, s, e, PathOf(prefix, hg.Name) + ".Male");
                        foreach (var (s, e) in hv.Female) LinkObject(hg.Female!.RefObject!, hg.Female.SigMap ?? hg.Map, gen, s, e, PathOf(prefix, hg.Name) + ".Female");
                    }
                    continue;
                }
                switch (g.Kind)
                {
                    case GroupKind.Single or GroupKind.StructObj or GroupKind.GenderedOne or GroupKind.ListArray:
                        for (int x = from; x < to; x++)
                        {
                            if (claims[x - from] != gi || Sig(x) != g.Own) continue;
                            // A subrecord a hand-read layout read is the layout's, but for a link it leaves to the record
                            // XML's layout attributes (a package's template in PKCU)
                            if (_linkSkip!.Contains(x))
                            {
                                LaidOutLinks(x, groups.Where(b => b.Own == g.Own), prefix);
                                foreach (var b in groups.Where(b => b.Own == g.Own)) Unread(PathOf(prefix, b.Name), $"{Sig(x)} is a hand-read layout's");
                                continue;
                            }
                            // The subrecord's data block: this field, and the fields after it that share its subrecord
                            // and took none of their own (a counted list after other values, a second list in STRD)
                            var block = new List<Group> { g };
                            for (int gj = 0; gj < groups.Count; gj++)
                                if (gj != gi && groups[gj].Own == g.Own && groups[gj].Kind is GroupKind.Single or GroupKind.StructObj or GroupKind.GenderedOne or GroupKind.ListArray
                                    && !claimedBy.ContainsKey(gj)) block.Add(groups[gj]);
                            block.Sort((a, b) => groups.IndexOf(a).CompareTo(groups.IndexOf(b)));
                            Block(x, block, map, gen, prefix);
                        }
                        break;
                    case GroupKind.ListSub:
                    {
                        if (!items.TryGetValue(gi, out var list)) break;
                        var item = g.Item!;
                        long k = 0;
                        foreach (var (s, _) in list)
                        {
                            string path = Each(PathOf(prefix, g.Name), k++);
                            if (_linkSkip!.Contains(s)) { Unread(path, $"{Sig(s)} is a hand-read layout's"); continue; }
                            var content = _raw.Content(_raw.Subrecords[s]);
                            if (item.RefObject is { } ro)
                            {
                                if (IsCondition(ro)) { ConditionLinks(s, ro, path); if (_values != null) ConditionValues(s, path); continue; }
                                string kind = Pick(ro, s, s + 1);
                                Decode(s, content, 0, kind, item.SigMap ?? g.Map, item.GenericArg == "T" ? gen : item.GenericArg ?? gen, path + MarkOf(ro, kind));
                            }
                            else Value(s, content, 0, item, path);
                        }
                        break;
                    }
                    case GroupKind.ListMulti:
                    {
                        if (!items.TryGetValue(gi, out var list)) break;
                        var item = g.Item!;
                        string? itemMap = item.SigMap ?? g.Map;
                        // Items that sit between markers of their own (the XML's itemMarkerType: a sound echo marker's Markers,
                        // ECHO to ECHE), which this walk doesn't read: where one entry ends isn't sure, so a value walk gives
                        // none of the list (its links are found either way). A hand-read layout's cut (a perk's ranks) is the
                        // layout's, not read here
                        if (_values != null && g.Main.ItemMarker != null) Unread(PathOf(prefix, g.Name), $"its items sit between {g.Main.ItemMarker} and {g.Main.ItemEndMarker ?? "the next"}, which this walk doesn't read");
                        long k = 0;
                        foreach (var (s, e) in list)
                        {
                            string path = Each(PathOf(prefix, g.Name), k++);
                            // An item a hand-read layout read (a scene's actions, cut at each ANAM): the layout's; not one whose
                            // first subrecord a layout describes inside the item the generated reader reads (an object
                            // template that starts at its OBTS: the value walk takes the layout's values there)
                            if (_linkSkip!.Contains(s) && !(_hand != null && _hand.SubAt.TryGetValue(s, out var described) && described.Path == path)) { Unread(path, $"{Sig(s)} is a hand-read layout's"); continue; }
                            string kind = Pick(item.RefObject!, s, e);
                            LinkObject(kind, itemMap, item.GenericArg == "T" ? gen : item.GenericArg ?? gen, s, e, path + MarkOf(item.RefObject!, kind));
                        }
                        break;
                    }
                    case GroupKind.MultiObj:
                    {
                        // Each run of subrecords the object took
                        for (int x = from; x < to; x++)
                        {
                            if (claims[x - from] != gi) continue;
                            int e = x;
                            while (e < to && claims[e - from] == gi) e++;
                            string kind = Selectable(g.Main) ? Selected(g.Main.RefObject!, g.Main, obj, -1, default, 0, from, to) : Pick(g.Main.RefObject!, x, e);
                            LinkObject(kind, g.Main.SigMap ?? g.Map, g.Main.GenericArg == "T" ? gen : g.Main.GenericArg ?? gen, x, e, PathOf(prefix, g.Name) + MarkOf(g.Main.RefObject!, kind));
                            x = e - 1;
                        }
                        break;
                    }
                    case GroupKind.GenderedSplit:
                    {
                        if (!halves.TryGetValue(gi, out var h)) break;
                        foreach (var (s, e) in h.Male) LinkObject(g.Male!.RefObject!, g.Male.SigMap, gen, s, e, PathOf(prefix, g.Name) + ".Male");
                        foreach (var (s, e) in h.Female) LinkObject(g.Female!.RefObject!, g.Female.SigMap, gen, s, e, PathOf(prefix, g.Name) + ".Female");
                        break;
                    }
                    case GroupKind.GenderedMarkers:
                        for (int x = from; x < to; x++)
                        {
                            if (claims[x - from] != gi) continue;
                            bool male = Sig(x) == g.MaleMarker;
                            var part = male ? g.Male! : g.Female!;
                            Value(x, _raw.Content(_raw.Subrecords[x]), 0, part, PathOf(prefix, g.Name) + (male ? ".Male" : ".Female"));
                        }
                        break;
                    case GroupKind.Other when GenderBy(g) is { } marker:
                        GenderLists(g, marker, from, to, prefix);
                        break;
                    case GroupKind.InCustom when Selectable(g.Main) && g.Main.Kind == "Object":
                    {
                        string ro = g.Main.RefObject!;
                        string path = PathOf(prefix, g.Name);
                        if (g.Main.Offset is int at && g.Main.Subrecord is { } sub)
                        {
                            // Inside a subrecord another field shares (a magic effect's archetype at 80 of DATA)
                            for (int x = from; x < to; x++)
                                if (Sig(x) == sub)
                            {
                                if (_linkSkip!.Contains(x)) { Unread(path, $"{sub} is a hand-read layout's"); break; }
                                SelectedObject(x, _raw.Content(_raw.Subrecords[x]), at, g.Main, map, gen, path);
                                break;
                            }
                        }
                        else if (g.Main.Offset == null && g.Main.Subrecord == null)
                        {
                            // Its own subrecords, which hand-written code hands to the kind's generated reader (a terminal
                            // menu item's target: TNAM or BNAM, the kind by ISET)
                            string kind = Selected(ro, g.Main, obj, -1, default, 0, from, to);
                            var sigs = ObjSigs(kind, g.Main.SigMap ?? g.Map);
                            bool read = false;
                            for (int x = from; x < to; x++)
                            {
                                if (!sigs.Contains(Sig(x)) || _linkSkip!.Contains(x)) continue;
                                int e = x;
                                while (e < to && sigs.Contains(Sig(e)) && !_linkSkip.Contains(e)) e++;
                                LinkObject(kind, g.Main.SigMap ?? g.Map, gen, x, e, path + MarkOf(ro, kind));
                                x = e - 1; read = true;
                            }
                            // None of its subrecords there (a kind that has none: a menu item that returns to the previous
                            // menu): Mutagen holds the object all the same when the field can't be absent, of the kind picked
                            if (!read && !g.Main.Nullable) _values?.Object(path + MarkOf(ro, kind), KindName(kind, gen));
                        }
                        break;
                    }
                    case GroupKind.InCustom when _inRun && GeneratedItems(g) is { } ro:
                    {
                        // Inside a run a hand-read layout cut out, a list Mutagen's hand-written code reads by calling the
                        // generated reader of its item (a perk effect's conditions: PerkCondition, PRKC and its CTDA): each
                        // item from a subrecord no other field took, as far as the item's generated reader goes
                        string? itemMap = g.Item!.SigMap ?? g.Map;
                        var sigs = ObjSigs(ro, itemMap);
                        long k = 0;
                        for (int x = from; x < to;)
                        {
                            if (claims[x - from] >= 0 || _linkSkip!.Contains(x) || !sigs.Contains(Sig(x))) { x++; continue; }
                            int e = Math.Max(ParseItem(ro, itemMap, x, to), x + 1);
                            string path = Each(PathOf(prefix, g.Name), k++) + MarkOf(ro, ro);
                            LinkObject(ro, itemMap, g.Item.GenericArg == "T" ? gen : g.Item.GenericArg ?? gen, x, e, path);
                            x = e;
                        }
                        break;
                    }
                    // PerkEffects, InCustom, Other: Mutagen's hand-written code reads them; the hand-read layouts give theirs,
                    // and a list or object the type's layout cuts into runs is given by its runs
                    default:
                        if (_values != null && _cuts != null && (_cuts.Contains(General(PathOf(prefix, g.Name)) + "[*]") || _cuts.Contains(General(PathOf(prefix, g.Name))))) break;
                        Unread(PathOf(prefix, g.Name), "read by Mutagen's hand-written code");
                        break;
                }
            }
            catch (NotFollowed e)
            {
                // Only when a link field could be in what wasn't followed (the field, or another of its subrecord's)
                // The field, and the others of its subrecord after it, may have links the walk missed
                foreach (var x in groups.Where(x => x == g || x.Own != null && x.Own == g.Own && groups.IndexOf(x) > gi)) Unread(PathOf(prefix, x.Name), e.Message);
                var lost = groups.Where(x => x == g || x.Own != null && x.Own == g.Own && groups.IndexOf(x) > gi).Select(x => PathOf(prefix, x.Name)).Where(_linksUnder!).ToList();
                if (lost.Count > 0)
                {
                    _linkProblems!.Add($"{PathOf(prefix, g.Name)}: {e.Message}");
                    foreach (var l in lost) _problemPaths!.Add(LinkByteWalker.Unmarked(l));
                }
            }
        }
    }

    /// <summary>One subrecord's data block: its fields one after another (at the offset the schema gives, when it does).</summary>
    void Block(int x, List<Group> block, string? map, string? gen, string prefix)
    {
        var content = _raw.Content(_raw.Subrecords[x]);
        int p = 0;
        for (int i = 0; i < block.Count; i++)
        {
            var f = block[i].Main;
            // A link Mutagen's hand-written code reads at the place the record XML's layout attributes give (a package's
            // template in PKCU at 4)
            if (LaidOutLink(f) is int lo)
            {
                if (lo + 4 <= content.Length) _link!(PathOf(prefix, f.Name), BinaryPrimitives.ReadUInt32LittleEndian(content[lo..]), x, lo);
                Unread(PathOf(prefix, f.Name), "read by Mutagen's hand-written code");
                p = lo + 4;
                continue;
            }
            if (f.Offset is int o && !f.OffsetRel) p = o;
            // (a yes/no that is its subrecord's presence, Mutagen's boolAsMarker, is there with no bytes: a template's OBTF)
            if (p >= content.Length && !(f.Kind == "Bool" && f.Size == 0 && content.Length == 0)) break;
            // A step Mutagen's hand-written code reads (a component's BFCB text): nothing after it in the subrecord to find
            if ((f.Kind == "CustomLogic" || f.Custom) && !Selectable(f) && !(_inRun && f.Custom && RunGenerated(f)) && i == block.Count - 1) { Unread(PathOf(prefix, f.Name), "read by Mutagen's hand-written code"); break; }
            // A value Mutagen reads by hand, when the schema places the next field: the walk goes on there
            if ((f.Kind == "CustomLogic" || f.Custom) && !Selectable(f) && !(_inRun && f.Custom && RunGenerated(f)) && block[i + 1].Main.Offset is int && !block[i + 1].Main.OffsetRel) { Unread(PathOf(prefix, f.Name), "read by Mutagen's hand-written code"); continue; }
            p = Field(x, content, p, block[i], f, map, gen, prefix);
        }
    }

    /// <summary>A property Mutagen adds beyond its XML (Versioning, a data block's DataTypeState …): no bytes of its own,
    /// except a region data's DataType.</summary>
    static bool NoBytes(FieldDef f) => f.Source != null && !(f.Object == "RegionData" && f.Name == "DataType") || f.Kind is "Break" or "Marker";

    /// <summary>Reads one field of an object from <paramref name="p"/> in a subrecord's content and gives where it ends.</summary>
    int Field(int x, ReadOnlySpan<byte> content, int p, Group g, FieldDef f, string? map, string? gen, string prefix)
    {
        if (NoBytes(f)) return p;
        string path = PathOf(prefix, f.Name);
        if (f.Custom && Selectable(f)) return SelectedObject(x, content, p, f, map, gen, path);
        // Inside a run, hand-written code may hand an object's subrecord to the object's generated reader (RunGenerated)
        if ((f.Kind == "CustomLogic" || f.Custom) && !(f.Custom && _inRun && RunGenerated(f))) throw new NotFollowed($"{f.Name} is read by hand-written Mutagen code, so where it and the fields after it stand isn't known");
        switch (f.Kind)
        {
            case "List" or "Array":
            {
                var item = g.Item ?? throw new NotFollowed($"{f.Name}: the schema has no row for its entries");
                long n = -1;
                if (f.CounterLength is int cl && !CountedBySubrecord(f))
                {
                    if (p + cl > content.Length) throw new NotFollowed($"{f.Name}: its count runs past the end of {Sig(x)}");
                    n = cl switch { 1 => content[p], 2 => BinaryPrimitives.ReadUInt16LittleEndian(content[p..]), 4 => BinaryPrimitives.ReadUInt32LittleEndian(content[p..]), 8 => checked((long)BinaryPrimitives.ReadUInt64LittleEndian(content[p..])), _ => throw new NotFollowed($"{f.Name}: a count of {cl} bytes") };
                    p += cl;
                    // The length of one entry after the count (expectedLengthLength: a planet biome's flora)
                    p += EntryLength(f) ?? 0;
                }
                else if (f.Note != null && Regex.Match(f.Note, @"always (\d+) items") is { Success: true } fixedCount) n = int.Parse(fixedCount.Groups[1].Value, CultureInfo.InvariantCulture);
                else if (f.Size is int whole && item.Size is int each && each > 0) n = whole / each;
                for (long k = 0; n < 0 ? p < content.Length : k < n; k++)
                {
                    if (p > content.Length) throw new NotFollowed($"{f.Name}: its entries run past the end of {Sig(x)}");
                    p = Item(x, content, p, item, g.Map ?? map, gen, Each(path, k));
                }
                return p;
            }
            case "Object":
            {
                string ro = f.RefObject ?? throw new NotFollowed($"{f.Name}: the schema doesn't say what object it is");
                string kind = PickHere(ro, f, content, p);
                return Decode(x, content, p, kind, f.SigMap ?? map, f.GenericArg == "T" ? gen : f.GenericArg ?? gen, path + MarkOf(ro, kind));
            }
            case "Gendered":
            {
                var male = g.Male ?? throw new NotFollowed($"{f.Name}: no male row");
                var female = g.Female ?? throw new NotFollowed($"{f.Name}: no female row");
                p = Item(x, content, p, male, map, gen, path + ".Male");
                return Item(x, content, p, female, map, gen, path + ".Female");
            }
            default:
                return Value(x, content, p, f, path);
        }
    }

    /// <summary>One list entry (or half of a gendered value): a value, a text or an object.</summary>
    int Item(int x, ReadOnlySpan<byte> content, int p, FieldDef item, string? map, string? gen, string path)
    {
        if (item.RefObject is { } ro)
        {
            if (IsCondition(ro)) throw new NotFollowed($"{path}: a condition inside a subrecord");
            string kind = PickHere(ro, item, content, p);
            return Decode(x, content, p, kind, item.SigMap ?? map, item.GenericArg == "T" ? gen : item.GenericArg ?? gen, path + MarkOf(ro, kind));
        }
        return Value(x, content, p, item, path);
    }

    /// <summary>The links of a data block that the record XML's layout attributes place (<see cref="LaidOutLink"/>), read
    /// alone: the rest of the subrecord is a hand-read layout's.</summary>
    void LaidOutLinks(int x, IEnumerable<Group> block, string prefix)
    {
        var content = _raw.Content(_raw.Subrecords[x]);
        foreach (var b in block)
            if (LaidOutLink(b.Main) is int lo && lo + 4 <= content.Length) _link!(PathOf(prefix, b.Main.Name), BinaryPrimitives.ReadUInt32LittleEndian(content[lo..]), x, lo);
    }

    /// <summary>A link Mutagen's hand-written code reads at a place the record XML's layout attributes give in the field's
    /// own subrecord (C186): its offset there, else null.</summary>
    static int? LaidOutLink(FieldDef f) => f.Custom && f.Kind is "FormLink" or "FormLinkOrIndex" && f.LayoutSubrecord == null && f.LayoutOffsetNumber is int lo ? lo : null;

    /// <summary>An object with several kinds whose kind the schema's selector picks from the bytes: the Int32 where it
    /// stands (here:Int32: a magic effect's archetype, a location target), the bits of another field (field:Flags:bit=0,2,4:
    /// what a book teaches), another field's value (field:TypeParse:UInt16: a terminal menu item's target), or the record
    /// type of the record the FormID where it stands names (linkTarget: a container entry's owner).</summary>
    bool Selectable(FieldDef f) => f.RefObject is { } ro && TargetsOf(ro).Count > 1 && f.SelectedBy is { } s && (s is "here:Int32" or "linkTarget" || s.StartsWith("field:", StringComparison.Ordinal));

    /// <summary>
    /// The kind a selector picks (<see cref="Selectable"/>), as Mutagen's hand-written reader picks it: a kind's
    /// SelectorValue is the number, the bit, or a member of the enum the selectors name (the schema's enums, else the one
    /// in Mutagen's assembly that has every name), or with linkTarget the record type of the record the FormID names, as
    /// Mutagen's record cache tells it (ExtraDataBinaryCreateTranslation.GetBinaryOwner); "*" is the kind for any other
    /// value (a null FormID, one no loaded record has), "none" for no bit set.
    /// <paramref name="x"/>: the subrecord the field is in (-1: the object is a run of subrecords, [from, to)).
    /// </summary>
    string Selected(string ro, FieldDef f, string obj, int x, ReadOnlySpan<byte> content, int p, int from, int to)
    {
        var targets = TargetsOf(ro);
        string sel = f.SelectedBy!;
        long v;
        if (sel == "linkTarget")
        {
            if (x < 0 || p + 4 > content.Length) throw new NotFollowed($"{f.Name}: the FormID that picks its kind isn't in {(x < 0 ? "a subrecord" : Sig(x))}");
            if (_sigOf == null) throw new NotFollowed($"{f.Name}: its kind is the record type of the record its FormID names, which this read can't look up");
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(content[p..]);
            string? sig = id == 0 ? null : _sigOf(id);
            return targets.FirstOrDefault(t => sig != null && _m.Objects[t.Name].SelectorValue == sig)?.Name
                ?? targets.FirstOrDefault(t => _m.Objects[t.Name].SelectorValue == "*")?.Name
                ?? throw new NotFollowed($"{f.Name}: no kind of {ro} for a link to {sig ?? "nothing"}");
        }
        if (sel == "here:Int32")
        {
            if (x < 0 || p + 4 > content.Length) throw new NotFollowed($"{f.Name}: its kind's Int32 isn't in {(x < 0 ? "a subrecord" : Sig(x))}");
            v = BinaryPrimitives.ReadInt32LittleEndian(content[p..]);
        }
        else
        {
            var parts = sel.Split(':');
            var src = _m.Ancestry(obj).SelectMany(o => o.Fields).FirstOrDefault(d => d.Name == parts[1] && d.Path == d.Name)
                ?? throw new NotFollowed($"{f.Name}: {obj} has no field {parts[1]} to pick its kind");
            ReadOnlySpan<byte> c = default;
            bool found = false;
            if (x >= 0 && (src.Subrecord == null || src.Subrecord == Sig(x))) { c = content; found = true; }
            else
                for (int y = from; y < to; y++)
                    if (Sig(y) == src.Subrecord) { c = _raw.Content(_raw.Subrecords[y]); found = true; break; }
            if (!found) throw new NotFollowed($"{f.Name}: no {src.Subrecord} holds {parts[1]}, which picks its kind");
            int off = src.Offset ?? 0;
            int size = parts[2] switch { "UInt8" or "Int8" => 1, "UInt16" or "Int16" => 2, "UInt32" or "Int32" => 4, _ => src.Size ?? 1 };
            if (off + size > c.Length) throw new NotFollowed($"{f.Name}: {parts[1]} runs past the end of {src.Subrecord}");
            v = size switch { 1 => c[off], 2 => BinaryPrimitives.ReadUInt16LittleEndian(c[off..]), _ => BinaryPrimitives.ReadUInt32LittleEndian(c[off..]) };
            if (parts[2].StartsWith("bit=", StringComparison.Ordinal))
            {
                foreach (var b in parts[2][4..].Split(','))
                    if ((v >> int.Parse(b, CultureInfo.InvariantCulture) & 1) != 0)
                        return targets.FirstOrDefault(t => _m.Objects[t.Name].SelectorValue == b)?.Name ?? throw new NotFollowed($"{f.Name}: no kind of {ro} for bit {b}");
                return targets.FirstOrDefault(t => _m.Objects[t.Name].SelectorValue == "none")?.Name ?? throw new NotFollowed($"{f.Name}: no kind of {ro} for no bit set");
            }
        }
        foreach (var t in targets)
            if (long.TryParse(_m.Objects[t.Name].SelectorValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) && n == v) return t.Name;
        // Selector values that are member names of the enum the record XML names (selectorEnum, C57 W3: a terminal menu
        // item's TypeOption, nested in its reader), each worth that member's value
        if ((f.SelectorEnum ?? _m.Objects.GetValueOrDefault(ro)?.SelectorEnum) is { } se && _m.Enums.TryGetValue(se, out var members))
            foreach (var t in targets)
                if (_m.Objects[t.Name].SelectorValue is { } sv && members.FirstOrDefault(x => x.Name == sv) is { } member && member.Value == v) return t.Name;
        return targets.FirstOrDefault(t => _m.Objects[t.Name].SelectorValue == "*")?.Name
            ?? throw new NotFollowed($"{f.Name}: no kind of {ro} has the selector value {v}");
    }

    /// <summary>A hand-read object whose kind a selector picks (<see cref="Selected"/>), read as Mutagen's code reads it:
    /// a kind whose fields the record XML's layout attributes place (a magic effect's archetype: the association at 0 of
    /// DATA) at those places; with here:Int32, a kind without a field of its own for that Int32 after it (a location
    /// target: the Int32, then the link); else the kind's fields from where the field stands (a book's DNAM at 1).</summary>
    int SelectedObject(int x, ReadOnlySpan<byte> content, int p, FieldDef f, string? map, string? gen, string path)
    {
        string ro = f.RefObject!;
        string kind = Selected(ro, f, f.Object, x, content, p, 0, 0);
        string at = path + MarkOf(ro, kind);
        var own = _m.Ancestry(kind).SelectMany(o => o.Fields).Where(d => d.Path == d.Name && !NoBytes(d)).ToList();
        if (own.Count > 0 && own.All(d => d.LayoutOffsetNumber != null))
        {
            Unread(at, "a kind whose fields Mutagen's code reads where the layout places them");
            foreach (var d in own.Where(IsLinkDef))
            {
                int lo = d.LayoutOffsetNumber!.Value;
                ReadOnlySpan<byte> c = content;
                int y = x;
                if (d.LayoutSubrecord is { } ls && ls != Sig(x))
                {
                    y = -1;
                    for (int k = 0; k < _raw.Subrecords.Count; k++) if (Sig(k) == ls) { y = k; break; }
                    if (y < 0) continue;
                    c = _raw.Content(_raw.Subrecords[y]);
                }
                if (lo + 4 <= c.Length) _link!(at + "." + d.Name, BinaryPrimitives.ReadUInt32LittleEndian(c[lo..]), y, lo);
            }
            return p + (f.Size ?? 4);
        }
        int start = f.SelectedBy == "here:Int32" && own.FirstOrDefault()?.Kind != "Enum" ? p + 4 : p;
        return Decode(x, content, start, kind, f.SigMap ?? map, f.GenericArg == "T" ? gen : f.GenericArg ?? gen, at);
    }

    /// <summary>Inside a run a hand-read layout cut out, an object of one subrecord that hand-written code hands to the
    /// object's generated reader (a package input's location PLDT, which the layout lists as generated): one kind,
    /// read like any object in its subrecord.</summary>
    bool RunGenerated(FieldDef f) => f.Kind == "Object" && f.Subrecord != null && f.RefObject is { } ro && !IsCondition(ro) && TargetsOf(ro).Count == 1;

    /// <summary>A list Mutagen's hand-written code reads by calling its item's generated reader, with one kind of item (a
    /// perk effect's conditions): the item's object, else null.</summary>
    string? GeneratedItems(Group g) => g.Main.Kind == "List" && g.Item?.RefObject is { } ro && !IsCondition(ro) && TargetsOf(ro).Count == 1 && _m.Objects.TryGetValue(ro, out var od) && !od.Struct ? ro : null;

    /// <summary>A field Mutagen's hand-written code reads at the place the record XML's layout attributes give in the
    /// object's own bytes (C186: layoutOffset a number or after:&lt;field&gt;, with layoutPresentWhen when the bytes may be
    /// missing): placed by them, not by the fields before it (a traversal's flags at 40, its FormID at 44 when bit 2 of the
    /// flags is clear, then four halves, each after the one before: TraversalReference.xml).</summary>
    static bool Placed(FieldDef f) => (f.Custom || f.Kind == "CustomLogic") && f.LayoutOffset != null && f.LayoutSubrecord == null && f.LayoutGroup == null;

    /// <summary>Where a field of an object that starts at <paramref name="start"/> stands, by the layout attributes (or,
    /// for a field Mutagen's generated reader reads, the schema's offset): its first byte and its end; a field that
    /// isn't there (layoutPresentWhen) ends where it starts.</summary>
    (int At, int End) PlaceOf(List<Group> groups, FieldDef f, ReadOnlySpan<byte> content, int start, string path, int depth = 0)
    {
        if (depth > 32) throw new NotFollowed($"{PathOf(path, f.Name)}: its place is laid out after itself");
        int at;
        if (Placed(f) && f.LayoutOffsetNumber is int n) at = start + n;
        else if (Placed(f) && f.LayoutOffset!.StartsWith("after:", StringComparison.Ordinal)) at = PlaceOf(groups, LaidOutField(groups, f.LayoutOffset[6..], path), content, start, path, depth + 1).End;
        else if (!Placed(f) && f.Offset is int o && f.OffsetRel) at = start + o;
        else throw new NotFollowed($"{PathOf(path, f.Name)}: its place ({f.LayoutOffset ?? "after the fields before it"}) isn't laid out");
        if (!Present(groups, f, content, start, path, depth)) return (at, at);
        return (at, at + StoredSize(f, path));
    }

    static FieldDef LaidOutField(List<Group> groups, string name, string path)
        => groups.FirstOrDefault(g => g.Name == name)?.Main ?? throw new NotFollowed($"{path}: the layout names a field {name} the object doesn't have");

    /// <summary>Whether a field's bytes are there, by the XML's layoutPresentWhen: field:F:bit=N:clear (or set) on the
    /// stored value of F, field:F:null (or notnull) on a FormID F; true without one.</summary>
    bool Present(List<Group> groups, FieldDef f, ReadOnlySpan<byte> content, int start, string path, int depth)
    {
        if (f.LayoutPresentWhen is not { } when) return true;
        var parts = when.Split(':');
        if (parts.Length >= 3 && parts[0] == "field")
        {
            var (at, end) = PlaceOf(groups, LaidOutField(groups, parts[1], path), content, start, path, depth + 1);
            if (end > content.Length) throw new NotFollowed($"{PathOf(path, parts[1])}: runs past the end of the subrecord");
            ulong v = (end - at) switch
            {
                1 => content[at],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(content[at..]),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(content[at..]),
                _ => throw new NotFollowed($"{PathOf(path, f.Name)}: there when {when}, and {parts[1]} has {end - at} bytes"),
            };
            if (parts.Length == 3 && parts[2] is "null" or "notnull") return (v == 0) == (parts[2] == "null");
            if (parts.Length == 4 && parts[2].StartsWith("bit=", StringComparison.Ordinal) && parts[3] is "set" or "clear"
                && int.TryParse(parts[2][4..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int bit))
                return ((v >> bit & 1) != 0) == (parts[3] == "set");
        }
        throw new NotFollowed($"{PathOf(path, f.Name)}: there when {when}, which the walk doesn't read");
    }

    /// <summary>A laid-out field's bytes: the stored number the XML's layoutEncoding names (uint16, uint8*10min,
    /// float/100), one byte for bits of a byte, else the field's size.</summary>
    static int StoredSize(FieldDef f, string path)
    {
        if (f.LayoutBits != null) return 1;
        if (f.LayoutEncoding is { } enc && StoredNumber().Match(enc) is { Success: true } m)
            return m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / 8 : m.Value == "double" ? 8 : 4;
        return f.Size ?? (f.Kind is "FormLink" or "FormLinkOrIndex" ? 4 : throw new NotFollowed($"{PathOf(path, f.Name)}: the schema gives no size for a laid-out {f.Kind}"));
    }

    [GeneratedRegex(@"^(u?int(8|16|32|64)|float|double)(?=$|[/*!<>=])")]
    private static partial Regex StoredNumber();

    /// <summary>A gendered value whose halves follow a subrecord that says the gender (the XML's genderEnumRecord, C57 W3: an
    /// armor addon's BoneDataModifiers after BSMP): that subrecord's signature, or null.</summary>
    static string? GenderBy(Group g) => g.Main.Kind == "Gendered" ? g.Main.GenderRecord : null;

    /// <summary>
    /// A gendered value with a gender subrecord, by the rule Mutagen documents for genderEnumRecord (Code-Generation.md,
    /// "Gendered values in the binary", C57 W3): up to two of it, in any order, each an int32 (0 male, 1 female) followed by
    /// the half's list, its items the subrecords of the item's signature that follow in a row.
    /// </summary>
    void GenderLists(Group g, string marker, int from, int to, string prefix)
    {
        int halves = 0;
        for (int x = from; x < to && halves < 2; x++)
        {
            if (Sig(x) != marker || _linkSkip!.Contains(x)) continue;
            halves++;
            var c = _raw.Content(_raw.Subrecords[x]);
            if (c.Length < 4) throw new NotFollowed($"{g.Name}: {marker} holds {c.Length} bytes, not a gender");
            int gender = BinaryPrimitives.ReadInt32LittleEndian(c);
            var half = gender == 0 ? g.Male : gender == 1 ? g.Female : null;
            if (half == null) continue;
            var item = _m.AllFields(half.Object).Select(x => x.F).FirstOrDefault(f => f.Path == half.Path + "[*]") ?? throw new NotFollowed($"{g.Name}: no row for its items");
            string sig = item.Subrecord ?? throw new NotFollowed($"{g.Name}: its items have no subrecord in the schema");
            long k = 0;
            for (int y = x + 1; y < to && Sig(y) == sig; y++)
                Value(y, _raw.Content(_raw.Subrecords[y]), 0, item, Each(PathOf(prefix, g.Name) + (gender == 0 ? ".Male" : ".Female"), k++));
        }
    }

    /// <summary>An object read inside a subrecord from <paramref name="p"/>: its fields one after another (a field
    /// Mutagen reads with hand-written code stops the walk there, unless it is one of those read above).</summary>
    int Decode(int x, ReadOnlySpan<byte> content, int p, string obj, string? map, string? gen, string path)
    {
        if (obj == "NpcRaceOverride") Unread(path, "read by Mutagen's hand-written code");
        if (obj == "Reflection") return Reflection(x, content, p, path);
        // An NPC's race override (ONA2) is Mutagen's NPC_ hand-read layout's (C57 W3), read before this walk; here only
        // when that layout didn't follow the bytes
        if (obj == "NpcRaceOverride") throw new NotFollowed($"{path}: an NPC's race override, which Mutagen's NPC_ layout (HandReadLayouts.xml) reads, and its walk didn't read this one");
        _values?.Object(path, KindName(obj, gen));
        int start = p;
        var groups = Groups(obj, map);
        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            var f = g.Main;
            if (f.Offset is int o && f.OffsetRel) p = start + o;
            if (p >= content.Length) break; // the subrecord ends before it (a version break): Mutagen leaves it out
            // A value Mutagen reads by hand where the layout attributes place it in the object's bytes
            if (Placed(f) && !Selectable(f))
            {
                var (at, end) = PlaceOf(groups, f, content, start, path);
                if (end > content.Length) throw new NotFollowed($"{PathOf(path, f.Name)}: {end - at} bytes at offset {at} run past the end of {Sig(x)} ({content.Length} bytes)");
                if (end - at == 4 && IsLinkDef(f)) _link!(PathOf(path, f.Name), BinaryPrimitives.ReadUInt32LittleEndian(content[at..]), x, at);
                // A value Mutagen's code reads where the layout places it: its bytes there, when they are stored as they are
                // (a value with bits Mutagen keeps elsewhere, the XML's layoutDroppedBits alone, is given: the field says
                // which bits, and whoever reads the value clears them: a traversal's Flags in a component)
                if (f.LayoutEncoding != null || f.LayoutBits != null) Unread(PathOf(path, f.Name), "stored in a form Mutagen's code turns into its value");
                else if (end > at) _values?.Value(PathOf(path, f.Name), f, x, at, end);
                p = Math.Max(p, end);
                continue;
            }
            // A value Mutagen reads by hand, when the schema places the next field: the walk goes on there
            if ((f.Kind == "CustomLogic" || f.Custom) && !Selectable(f) && i + 1 < groups.Count && groups[i + 1].Main.Offset is int && groups[i + 1].Main.OffsetRel) { Unread(PathOf(path, f.Name), "read by Mutagen's hand-written code"); continue; }
            p = Field(x, content, p, g, f, map, gen, path);
        }
        return p;
    }

    /// <summary>A reflection (REFL and its kin): the whole subrecord is the blob, and its FormLinks are the FormIDs
    /// Mutagen's ReflectionData.FindFormIds finds in it, in order (ReflectionBinaryCreateTranslation.CustomBinaryEndImport).
    /// A value walk gets both: Data, the subrecord whole, and each of FormLinks at its place in it.</summary>
    int Reflection(int x, ReadOnlySpan<byte> content, int p, string path)
    {
        if (p != 0) throw new NotFollowed($"{path}: a reflection that doesn't start its subrecord");
        var blob = content.ToArray();
        // For a value walk: the schema's two fields of a reflection (its bytes, and each FormID found in them)
        FieldDef? dataField = null, linkField = null;
        if (_values != null)
        {
            var fields = _m.Objects["Reflection"].Fields;
            dataField = fields.FirstOrDefault(f => f.Path == "Data" && f.Kind == "ByteArray");
            linkField = fields.FirstOrDefault(f => f.Path == "FormLinks[*]");
            if (dataField == null || linkField == null) { Unread(path, "a reflection, whose fields the schema names otherwise"); dataField = linkField = null; }
            else { _values.Object(path, KindName("Reflection", null)); _values.Value(PathOf(path, "Data"), dataField, x, 0, content.Length); }
        }
        long k = 0;
        if (Mutagen.Bethesda.Starfield.ReflectionData.IsReflection(blob) && Mutagen.Bethesda.Starfield.ReflectionData.TryParse(blob) is { } data)
            foreach (var id in data.FormIds)
            {
                if (id.Position + 4 > content.Length) { if (linkField != null) Unread(PathOf(path, "FormLinks"), "a FormID Mutagen finds past the reflection's end"); continue; }
                _link!(path + ".FormLinks[*]", BinaryPrimitives.ReadUInt32LittleEndian(content[id.Position..]), x, id.Position);
                // (as a value laid out: the field is one Mutagen's own code fills, and here its place is that code's)
                if (linkField != null) _values!.LaidOut(Each(PathOf(path, "FormLinks"), k++), linkField, x, id.Position, id.Position + 4, "FormID");
            }
        return content.Length;
    }

    /// <summary>A plain value: a link is given; any other value is passed over by its size (a text by its length).</summary>
    int Value(int x, ReadOnlySpan<byte> content, int p, FieldDef f, string path)
    {
        if (f.Kind is "String" or "AssetLink")
        {
            int end = TextEnd(x, content, p, f, path);
            _values?.Value(path, f, x, p, end);
            return end;
        }
        if (f.Kind is "Dictionary" or "Array2D" && f.Size == null) throw new NotFollowed($"{path}: a {f.Kind} inside a subrecord");
        // A color with no size in the schema is Mutagen's default binary color: R G B A
        int size = f.Size ?? (f.Kind == "ByteArray" ? content.Length - p : f.Kind == "Color" ? 4 : throw new NotFollowed($"{path}: the schema gives no size for a {f.Kind}"));
        if (p + size > content.Length) throw new NotFollowed($"{path}: {size} bytes at offset {p} run past the end of {Sig(x)} ({content.Length} bytes)");
        if (f.Kind is "FormLink" or "FormLinkOrIndex" || f.LinkTo != null && size == 4) _link!(path, BinaryPrimitives.ReadUInt32LittleEndian(content[p..]), x, p);
        _values?.Value(path, f, x, p, p + size);
        return p + size;
    }

    /// <summary>Where a text inside a subrecord ends, by the schema's TextForm (the XML's binaryType); a translated text
    /// of a localized plugin is its 4-byte string ID.</summary>
    int TextEnd(int x, ReadOnlySpan<byte> content, int p, FieldDef f, string path)
    {
        if (f.Translated && _linkLocalized) return p + 4;
        string form = f.TextForm ?? "NullTerminate";
        int prefix = form switch { "PrependLength" or "PrependLengthWithNullIfContent" => 4, "PrependLengthUShort" => 2, "PrependLengthUInt8" => 1, _ => 0 };
        if (prefix > 0)
        {
            if (p + prefix > content.Length) throw new NotFollowed($"{path}: its length runs past the end of {Sig(x)}");
            long len = prefix switch { 1 => content[p], 2 => BinaryPrimitives.ReadUInt16LittleEndian(content[p..]), _ => BinaryPrimitives.ReadUInt32LittleEndian(content[p..]) };
            return checked((int)(p + prefix + len));
        }
        if (form == "NullPaddedFixedLength") return p + (f.Size ?? throw new NotFollowed($"{path}: a text in a slot of a size the schema doesn't give"));
        int z = content[p..].IndexOf((byte)0);
        return z < 0 ? content.Length : p + z + 1;
    }

    /// <summary>The kind of an object that is a run of subrecords: a component's by its BFCB text, a condition's by its
    /// CTDA flags, else the one kind the schema has.</summary>
    string Pick(string refObj, int s, int e)
    {
        var targets = TargetsOf(refObj);
        if (targets.Count == 1) return targets[0].Name;
        var od = _m.Objects[refObj];
        if (od.SelectedBy == "subrecord:BFCB:string" && s < e && Sig(s) == "BFCB")
            return Selectors.ComponentKind(_m, _raw.Content(_raw.Subrecords[s])) ?? throw new NotFollowed($"BFCB \"{System.Text.Encoding.Latin1.GetString(_raw.Content(_raw.Subrecords[s])).TrimEnd('\0')}\" names no component kind the schema has");
        if (IsCondition(refObj) && s < e && Sig(s) == "CTDA") return ConditionKind(s);
        throw new NotFollowed($"{refObj} has {targets.Count} kinds, and the bytes don't say which (its selector: {od.SelectedBy ?? "none in the schema"})");
    }

    /// <summary>The kind of an object inside a subrecord: the one the schema has, or the one an Int32 here picks.</summary>
    string PickHere(string refObj, FieldDef f, ReadOnlySpan<byte> content, int p)
    {
        var targets = TargetsOf(refObj);
        if (targets.Count == 1) return targets[0].Name;
        if (f.SelectedBy == "here:Int32" && p + 4 <= content.Length)
        {
            string value = BinaryPrimitives.ReadInt32LittleEndian(content[p..]).ToString(CultureInfo.InvariantCulture);
            foreach (var t in targets) if (_m.Objects[t.Name].SelectorValue == value) return t.Name;
        }
        throw new NotFollowed($"{f.Name} holds one of {targets.Count} kinds of {refObj}, and the bytes don't say which (its selector: {f.SelectedBy ?? _m.Objects[refObj].SelectedBy ?? "none in the schema"})");
    }

    /// <summary>A condition's kind by its CTDA: bit 2 of the flags byte picks ConditionGlobal (the schema's selector
    /// field:Flags:bit=2, each kind's SelectorValue).</summary>
    string ConditionKind(int s)
    {
        var content = _raw.Content(_raw.Subrecords[s]);
        if (content.Length == 0) throw new NotFollowed("an empty CTDA");
        string bit = (content[0] & 4) != 0 ? "1" : "0";
        foreach (var v in _m.Variants("Condition")) if (_m.Objects[v].SelectorValue == bit) return v;
        throw new NotFollowed($"no kind of condition has the selector value {bit}");
    }

    /// <summary>A condition's links, placed by the XML's layout of a CTDA: every link field of its kind and of
    /// ConditionData (the reference, the linked reference's keyword) at its layoutOffset.</summary>
    void ConditionLinks(int s, string refObj, string path)
    {
        var content = _raw.Content(_raw.Subrecords[s]);
        string kind = ConditionKind(s);
        string at = path + MarkOf(refObj, kind);
        foreach (var o in _m.Ancestry(kind))
            foreach (var f in o.Fields)
                if (f.LayoutOffsetNumber is int lo && f.Path == f.Name && (f.Kind is "FormLink" || f.LinkTo != null) && lo + 4 <= content.Length)
                    _link!(at + "." + f.Name, BinaryPrimitives.ReadUInt32LittleEndian(content[lo..]), s, lo);
        // A field the XML says is there only under a condition (layoutPresentWhen, C57 W3: the linked reference's keyword
        // at 28 only when the condition runs on a linked reference and the value isn't 0 or -1); else Mutagen's is null
        var data = _m.Ancestry("ConditionData").SelectMany(o => o.Fields).Where(f => f.Path == f.Name).ToList();
        var ctda = content.ToArray();
        long? Sibling(string name) => data.FirstOrDefault(f => f.Name == name) is { LayoutOffsetNumber: int so } sf && so + 4 <= ctda.Length
            ? sf.Size == 4 ? BinaryPrimitives.ReadInt32LittleEndian(ctda.AsSpan(so)) : ctda[so] : null;
        foreach (var f in data)
            if (f.LayoutOffsetNumber is int lo && (f.Kind is "FormLink" || f.LinkTo != null) && lo + 4 <= content.Length)
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(content[lo..]);
                if (f.LayoutPresentWhen is { } when && !PresentWhen(_m, data, when, Sibling, unchecked((int)v))) v = 0;
                _link!(at + ".Data." + f.Name, v, s, lo);
            }
        // The function's parameters (GAPS S156): the kind its index picks, its link fields from byte 12
        ConditionParameters.Links(_m, content, (name, v, lo) => _link!(at + ".Data." + name, v, s, lo));
    }

    /// <summary>A field of a condition or of its data that the record XML places in the CTDA: at its layoutOffset (a
    /// field of bits: the one byte that holds them), or where the generated reader reads it (Unknown1).</summary>
    static (int At, int Size)? ConditionPlace(FieldDef f)
    {
        if (f.Kind is "CustomLogic" or "Object" || f.Path != f.Name) return null;
        if ((f.LayoutOffsetNumber ?? f.Offset) is not int at) return null;
        int size = f.LayoutBits != null ? 1 : f.Size ?? 0;
        return size > 0 ? (at, size) : null;
    }

    /// <summary>
    /// A condition's values for a value walk (GAPS S166), placed as its links are, by the record XML's layout of a CTDA:
    /// each field of its kind (CompareOperator, Flags, Unknown1, ComparisonValue, Unknown2) and of ConditionData (RunOnType,
    /// Reference, ParameterThree, UseAliases, UsePackageData) at its place (<see cref="ConditionPlace"/>); a field of bits
    /// is given as its byte, the field's LayoutMask and LayoutDroppedMask saying which bits are its value. A field the XML
    /// says is there only under a condition and isn't (layoutPresentWhen: the linked reference's keyword) gets no value:
    /// Mutagen's is null. The function's own parameters, of the kind of ConditionData its index picks: the fields its
    /// generated reader reads one after another from the data's place (ConditionParameters: byte 12), a field the XML
    /// places (the catch-all kind's Function), and its texts from the CIS1 and CIS2 right after the CTDA; a field of
    /// another shape leaves the data unread.
    /// </summary>
    void ConditionValues(int s, string path)
    {
        var content = _raw.Content(_raw.Subrecords[s]);
        string kind = ConditionKind(s);
        _values!.Object(path, KindName(kind, null));
        var data = _m.Ancestry("ConditionData").SelectMany(o => o.Fields).Where(f => f.Path == f.Name).ToList();
        var ctda = content.ToArray();
        long? Sibling(string name) => data.FirstOrDefault(f => f.Name == name) is { LayoutOffsetNumber: int so } sf && so + 4 <= ctda.Length
            ? sf.Size == 4 ? BinaryPrimitives.ReadInt32LittleEndian(ctda.AsSpan(so)) : ctda[so] : null;
        void Give(string at, FieldDef f)
        {
            if (f.Kind is "CustomLogic" or "Object") return;
            string fp = PathOf(at, f.Name);
            if (ConditionPlace(f) is not var (lo, size)) { Unread(fp, "the record XML doesn't place it in the CTDA"); return; }
            if (lo + size > ctda.Length) { Unread(fp, "a CTDA shorter than its layout"); return; }
            if (f.LayoutPresentWhen is { } when)
            {
                if (size != 4) { Unread(fp, "a field there under a condition, not four bytes"); return; }
                if (!PresentWhen(_m, data, when, Sibling, BinaryPrimitives.ReadInt32LittleEndian(ctda.AsSpan(lo)))) return;
            }
            _values!.Value(fp, f, s, lo, lo + size);
        }
        foreach (var o in _m.Ancestry(kind)) foreach (var f in o.Fields) Give(path, f);
        // The data: the kind the function index picks (a uint16 at the function step's place)
        string dataPath = path + ".Data";
        int fnAt = _m.Objects["Condition"].Fields.FirstOrDefault(f => f.Kind == "CustomLogic" && f.LayoutOffsetNumber != null)?.LayoutOffsetNumber ?? 8;
        if (fnAt + 2 > ctda.Length || ConditionParameters.KindOf(_m, BinaryPrimitives.ReadUInt16LittleEndian(ctda.AsSpan(fnAt))) is not { } dataKind)
        {
            Unread(dataPath, "a condition whose function no kind of ConditionData takes");
            return;
        }
        int at = _m.Objects["Condition"].Fields.FirstOrDefault(f => f.Name == "Data")?.LayoutOffsetNumber ?? 12;
        var own = new List<(FieldDef Field, int Sub, int At, int End)>();
        foreach (var f in _m.Objects[dataKind].Fields.OrderBy(f => f.Ord))
        {
            if (f.Path != f.Name || f.Kind == "CustomLogic") continue;
            if (f.Kind == "String" && f.Subrecord is "CIS1" or "CIS2")
            {
                // The text right after the CTDA, when it is there (Mutagen's customBinaryEnd reads CIS1, then CIS2)
                for (int x = s + 1; x < _raw.Subrecords.Count && x <= s + 2 && Sig(x) is "CIS1" or "CIS2"; x++)
                    if (Sig(x) == f.Subrecord) own.Add((f, x, 0, _raw.Content(_raw.Subrecords[x]).Length));
                continue;
            }
            if (f.LayoutOffsetNumber is int placed)
            {
                int width = f.LayoutEncoding == "uint16" ? 2 : f.LayoutEncoding == null ? f.Size ?? 0 : 0;
                if (width <= 0 || placed + width > ctda.Length) { Unread(dataPath, $"{f.Name}: the record XML places it in a way this reader doesn't know"); return; }
                own.Add((f, s, placed, placed + width));
                continue;
            }
            int? size = f.Kind switch
            {
                "FormLink" or "FormLinkOrIndex" or "Int32" or "UInt32" or "Float" => 4,
                "Int16" or "UInt16" => 2,
                "Int8" or "UInt8" => 1,
                "Enum" => f.Size ?? 4,
                _ => null,
            };
            if (size is not int sz || f.Default != null || at + sz > ctda.Length) { Unread(dataPath, $"{f.Name}: a condition function's parameter this reader doesn't place"); return; }
            own.Add((f, s, at, at + sz));
            at += sz;
        }
        _values.Object(dataPath, KindName(dataKind, null));
        foreach (var f in data) Give(dataPath, f);
        foreach (var (f, sub, lo, hi) in own) _values.Value(PathOf(dataPath, f.Name), f, sub, lo, hi);
    }

    /// <summary>The schema's side of <see cref="ConditionValues"/>: a condition at <paramref name="at"/> reached, and each
    /// field the record XML places, under each kind of condition.</summary>
    void StaticConditionValues(Dictionary<string, string?> reach, string at, string refObj)
    {
        Mark(reach, at, null);
        var data = _m.Ancestry("ConditionData").SelectMany(o => o.Fields).Where(f => f.Path == f.Name).ToList();
        foreach (var v in _m.Variants("Condition"))
        {
            string c = at + MarkOf(refObj, v);
            foreach (var o in _m.Ancestry(v)) foreach (var f in o.Fields) if (ConditionPlace(f) != null) Mark(reach, c + "." + f.Name, null);
            foreach (var f in data) if (ConditionPlace(f) != null) Mark(reach, c + ".Data." + f.Name, null);
        }
    }

    /// <summary>
    /// Whether a field the record XML places under a condition is there (layoutPresentWhen, C57 W3), in the attribute's
    /// forms: <c>field:F:is=Member</c> (the sibling F holds that member of its enum), <c>value:not=a,b</c> (the field's
    /// own value is none of these), <c>field:F:null</c> / <c>field:F:notnull</c>; parts joined by ';' must all hold.
    /// </summary>
    internal static bool PresentWhen(SchemaModel m, IReadOnlyList<FieldDef> siblings, string when, Func<string, long?> sibling, long value)
    {
        foreach (var part in when.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split(':');
            if (bits[0] == "value" && bits.Length == 2 && bits[1].StartsWith("not=", StringComparison.Ordinal))
            {
                if (bits[1][4..].Split(',').Any(x => long.Parse(x, CultureInfo.InvariantCulture) == value)) return false;
            }
            else if (bits[0] == "field" && bits.Length == 3)
            {
                long? have = sibling(bits[1]);
                if (bits[2] == "null" ? have is not (null or 0) : bits[2] == "notnull" ? have is null or 0
                    : bits[2].StartsWith("is=", StringComparison.Ordinal)
                        && !(siblings.FirstOrDefault(f => f.Name == bits[1])?.Enum is { } en && m.Enums.TryGetValue(en, out var members)
                            && members.FirstOrDefault(x => x.Name == bits[2][3..]) is { } member && member.Value == have)) return false;
            }
            else throw new NotFollowed($"the record XML's layoutPresentWhen \"{when}\" has a part this reader doesn't know: {part}");
        }
        return true;
    }

    // ------------------------------------------------------------------ Which link fields the generated walk reaches
    /// <summary>
    /// The schema's side of the walk above, with no record: each link field the generated layout reaches (null) or why
    /// not, by path; and for each top-level field the subrecords it can take (a record holding none of them has no link
    /// there). Follows the same rules: hand-written code stops a subrecord's walk; an object with several kinds is
    /// followed when a BFCB text, a CTDA's flags or an Int32 before it picks the kind.
    /// </summary>
    internal static void StaticLinks(RecordSchema schema, string type, Dictionary<string, string?> reach, Dictionary<string, HashSet<string>> top)
    {
        var l = new ItemLocator(schema, new RawRecord { Signature = "", Header = new byte[24], Data = Array.Empty<byte>(), Subrecords = Array.Empty<RawSubrecord>() }, null, _ => "", null);
        foreach (var g in l.Groups(type, null))
        {
            // The subrecords a run of the field can start with: its own, its items', its counter, its marker; an object
            // of several subrecords (a component: BFCB) its start
            var sigs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in new[] { g.Own, g.ItemSig, g.Counter, g.Marker }) if (s != null) sigs.Add(s);
            var ro = g.Kind == GroupKind.ListMulti ? g.Item?.RefObject : g.Kind is GroupKind.MultiObj or GroupKind.GenderedSplit ? g.Main.RefObject ?? g.Male?.RefObject : null;
            if (ro != null) sigs.UnionWith(l.Triggers(ro, g.Item?.SigMap ?? g.Map));
            if (g.Kind == GroupKind.GenderedSplit && g.Female?.RefObject is { } fro) sigs.UnionWith(l.Triggers(fro, g.Female.SigMap));
            if (g.Kind is GroupKind.GenderedMarkers) { if (g.MaleMarker != null) sigs.Add(g.MaleMarker); if (g.FemaleMarker != null) sigs.Add(g.FemaleMarker); }
            if (sigs.Count == 0) sigs.UnionWith(g.Sigs);
            top[g.Name] = sigs;
        }
        l.StaticObject(type, null, "", reach, 0, new HashSet<string>(StringComparer.Ordinal));
    }

    void Mark(Dictionary<string, string?> reach, string path, string? why) { if (!reach.TryGetValue(path, out var had) || had != null) reach[path] = why; }

    /// <summary>The schema's side of <see cref="LinkRun"/>: the link fields under a list or object a hand-read layout cuts
    /// out (<paramref name="cut"/>, <c>Ranks[*].Effects[*]</c>), every kind of it, reached as its generated reader reads them.</summary>
    internal static void StaticRun(RecordSchema schema, string type, string cut, Dictionary<string, string?> reach)
    {
        var l = new ItemLocator(schema, new RawRecord { Signature = "", Header = new byte[24], Data = Array.Empty<byte>(), Subrecords = Array.Empty<RawSubrecord>() }, null, _ => "", null);
        l._staticInRun = true;
        l.StaticRunAt(type, null, cut.Split('.'), 0, "", reach);
    }

    bool _staticInRun;

    /// <summary>The value walk's side of a run (<see cref="LinkRun"/> in a value walk): every value of the object Mutagen's
    /// generated reader reads from the run, by path with kinds, added to a value walk's reach (GAPS S164).</summary>
    internal static void StaticRunValues(RecordSchema schema, string type, string cut, Dictionary<string, string?> reach)
    {
        var l = new ItemLocator(schema, new RawRecord { Signature = "", Header = new byte[24], Data = Array.Empty<byte>(), Subrecords = Array.Empty<RawSubrecord>() }, null, _ => "", null) { _staticAll = true };
        l._staticInRun = true;
        l.StaticRunAt(type, null, cut.Split('.'), 0, "", reach);
    }

    void StaticRunAt(string obj, string? map, string[] segs, int i, string prefix, Dictionary<string, string?> reach)
    {
        bool list = segs[i].EndsWith("[*]", StringComparison.Ordinal);
        string name = list ? segs[i][..^3] : segs[i];
        var g = Groups(obj, map).FirstOrDefault(x => x.Name == name);
        if (g == null) return;
        var def = list && g.Item != null ? g.Item : g.Main;
        if (def.RefObject is not { } ro) return;
        string at = PathOf(prefix, name) + (list ? "[*]" : "");
        string? inner = def.SigMap ?? g.Map ?? map;
        if (i == segs.Length - 1 && IsCondition(ro) && _staticAll) { StaticConditionValues(reach, at, ro); return; }
        if (i == segs.Length - 1 && IsCondition(ro))
        {
            foreach (var v in _m.Variants("Condition"))
            {
                string c = at + MarkOf(ro, v);
                foreach (var o in _m.Ancestry(v)) foreach (var f in o.Fields) if (f.LayoutOffsetNumber != null && f.Path == f.Name && (f.Kind is "FormLink" || f.LinkTo != null)) Mark(reach, c + "." + f.Name, null);
                foreach (var o in _m.Ancestry("ConditionData")) foreach (var f in o.Fields) if (f.LayoutOffsetNumber != null && f.Path == f.Name && (f.Kind is "FormLink" || f.LinkTo != null)) Mark(reach, c + ".Data." + f.Name, null);
                foreach (var pn in ConditionParameters.LinkNames(_m)) Mark(reach, c + ".Data." + pn, null);
            }
            return;
        }
        foreach (var t in TargetsOf(ro))
        {
            if (i < segs.Length - 1) StaticRunAt(t.Name, inner, segs, i + 1, at + t.Marker, reach);
            else StaticObject(t.Name, inner, at + t.Marker, reach, 0, new HashSet<string>(StringComparer.Ordinal));
        }
    }

    void StaticObject(string obj, string? map, string prefix, Dictionary<string, string?> reach, int depth, HashSet<string> stack)
    {
        if (depth > SchemaModel.MaxDepth || !stack.Add(obj)) return;
        try
        {
            foreach (var g in Groups(obj, map))
            {
                string path = PathOf(prefix, g.Name);
                if (g.Halves is { } hg)
                {
                    StaticObject(hg.Male!.RefObject!, hg.Male.SigMap ?? hg.Map, PathOf(prefix, hg.Name) + ".Male", reach, depth + 1, stack);
                    StaticObject(hg.Female!.RefObject!, hg.Female.SigMap ?? hg.Map, PathOf(prefix, hg.Name) + ".Female", reach, depth + 1, stack);
                    continue;
                }
                switch (g.Kind)
                {
                    case GroupKind.Single or GroupKind.StructObj or GroupKind.GenderedOne or GroupKind.ListArray:
                        // The block: the fields before this one in the same subrecord must be followed
                        string? stop = null;
                        foreach (var b in Groups(obj, map).Where(x => x.Own == g.Own && x.Kind is GroupKind.Single or GroupKind.StructObj or GroupKind.GenderedOne or GroupKind.ListArray))
                        {
                            if (b == g) break;
                            if (b.Main.Offset == null && Unfollowed(b.Main) is { } why) stop = $"after {b.Name}, which {why}";
                        }
                        StaticField(g, g.Main, map, prefix, reach, stop, depth, stack);
                        break;
                    case GroupKind.ListSub:
                    {
                        var item = g.Item!;
                        // A value walk gives the list's entries, a condition's by the record XML's layout of its CTDA
                        if (_staticAll && !(item.RefObject is { } cro && IsCondition(cro))) Mark(reach, path + "[*]", null);
                        if (item.RefObject is { } ro)
                        {
                            if (IsCondition(ro) && _staticAll) { StaticConditionValues(reach, path + "[*]", ro); break; }
                            if (IsCondition(ro))
                            {
                                foreach (var v in _m.Variants("Condition"))
                                {
                                    string at = path + "[*]" + MarkOf(ro, v);
                                    foreach (var o in _m.Ancestry(v)) foreach (var f in o.Fields) if (f.LayoutOffsetNumber != null && f.Path == f.Name && (f.Kind is "FormLink" || f.LinkTo != null)) Mark(reach, at + "." + f.Name, null);
                                    foreach (var o in _m.Ancestry("ConditionData")) foreach (var f in o.Fields) if (f.LayoutOffsetNumber != null && f.Path == f.Name && (f.Kind is "FormLink" || f.LinkTo != null)) Mark(reach, at + ".Data." + f.Name, null);
                                    foreach (var pn in ConditionParameters.LinkNames(_m)) Mark(reach, at + ".Data." + pn, null);
                                }
                                break;
                            }
                            foreach (var t in TargetsOf(ro)) StaticStruct(t.Name, item.SigMap ?? g.Map, path + "[*]" + t.Marker, reach, TargetsOf(ro).Count > 1 ? "its kind isn't told by the bytes" : null, depth, stack);
                        }
                        else if (Want(item)) Mark(reach, path + "[*]", null);
                        break;
                    }
                    case GroupKind.ListMulti or GroupKind.MultiObj:
                    {
                        var def = g.Kind == GroupKind.ListMulti ? g.Item! : g.Main;
                        string at = g.Kind == GroupKind.ListMulti ? path + "[*]" : path;
                        if (def.RefObject is not { } ro) break;
                        var targets = TargetsOf(ro);
                        bool picked = targets.Count == 1 || _m.Objects[ro].SelectedBy == "subrecord:BFCB:string" || g.Kind == GroupKind.MultiObj && Selectable(def);
                        if (_staticAll && g.Kind == GroupKind.ListMulti)
                            Mark(reach, at, g.Main.ItemMarker != null ? $"its items sit between {g.Main.ItemMarker} and {g.Main.ItemEndMarker ?? "the next"}, which this walk doesn't read"
                                : picked ? null : $"{ro} has {targets.Count} kinds, and the bytes don't say which");
                        foreach (var t in targets)
                        {
                            if (picked) StaticObject(t.Name, def.SigMap ?? g.Map, at + t.Marker, reach, depth + 1, stack);
                            else MarkAll(t.Name, at + t.Marker, reach, $"{ro} has {targets.Count} kinds, and the bytes don't say which");
                        }
                        break;
                    }
                    case GroupKind.GenderedSplit:
                        if (g.Male?.RefObject is { } mro) StaticObject(mro, g.Male.SigMap, path + ".Male", reach, depth + 1, stack);
                        if (g.Female?.RefObject is { } fro) StaticObject(fro, g.Female.SigMap, path + ".Female", reach, depth + 1, stack);
                        break;
                    case GroupKind.GenderedMarkers:
                        if (g.Male != null && Want(g.Male)) Mark(reach, path + ".Male", null);
                        if (g.Female != null && Want(g.Female)) Mark(reach, path + ".Female", null);
                        break;
                    case GroupKind.Other when GenderBy(g) is not null:
                        foreach (var half in new[] { (g.Male, ".Male"), (g.Female, ".Female") })
                            if (half.Item1 != null && _m.AllFields(half.Item1.Object).Select(x => x.F).FirstOrDefault(x => x.Path == half.Item1.Path + "[*]") is { } it && Want(it))
                                Mark(reach, path + half.Item2 + "[*]", null);
                        break;
                    default:
                        if (g.Kind == GroupKind.InCustom && Selectable(g.Main) && g.Main.Kind == "Object" && (g.Main.Offset != null && g.Main.Subrecord != null || g.Main.Offset == null && g.Main.Subrecord == null))
                        {
                            if (g.Main.Offset != null) StaticField(g, g.Main, map, prefix, reach, null, depth, stack);
                            else foreach (var t in TargetsOf(g.Main.RefObject!)) StaticObject(t.Name, g.Main.SigMap ?? g.Map, path + t.Marker, reach, depth + 1, stack);
                        }
                        else if (_staticInRun && g.Kind == GroupKind.InCustom && GeneratedItems(g) is { } gro)
                            StaticObject(gro, g.Item!.SigMap ?? g.Map, path + "[*]" + MarkOf(gro, gro), reach, depth + 1, stack);
                        else MarkAll(g, path, reach, "read by Mutagen's hand-written code");
                        break;
                }
            }
        }
        finally { stack.Remove(obj); }
    }

    static bool IsLinkDef(FieldDef d) => d.Kind is "FormLink" or "FormLinkOrIndex" || d.LinkTo != null && d.Kind is not ("List" or "Object");

    /// <summary>The static walk marks every value (a value walk's reach, <see cref="StaticValues"/>), not only links.</summary>
    bool _staticAll;

    /// <summary>A field the static walk marks: a link, or in a value walk's reach any value with bytes of its own.</summary>
    bool Want(FieldDef d) => _staticAll ? !NoBytes(d) && d.Kind is not ("List" or "Array" or "Object" or "Gendered" or "Dictionary" or "Array2D" or "CustomLogic") : IsLinkDef(d);

    /// <summary>
    /// The schema's side of a value walk (<see cref="Values"/>, GAPS S153): each value and list of the record type the
    /// generated layout reaches (null) or why not, by path with the kinds of object (<c>Components[*]{ItemsComponent}.Items[*]</c>
    /// for a list's entries). Only the reached ones matter: a path not in it isn't reached. The runs a hand-read layout of
    /// the type cuts out are a value walk's unread parts, as are conditions and hand-written readers.
    /// </summary>
    internal static Dictionary<string, string?> StaticValues(RecordSchema schema, string type)
    {
        var l = new ItemLocator(schema, new RawRecord { Signature = "", Header = new byte[24], Data = Array.Empty<byte>(), Subrecords = Array.Empty<RawSubrecord>() }, null, _ => "", null) { _staticAll = true };
        var reach = new Dictionary<string, string?>(StringComparer.Ordinal);
        l.StaticObject(type, null, "", reach, 0, new HashSet<string>(StringComparer.Ordinal));
        return reach;
    }

    /// <summary>Why the walk can't step over a field to the ones after it in its subrecord; null when it can.</summary>
    string? Unfollowed(FieldDef f)
    {
        if (LaidOutLink(f) != null || Placed(f) || f.Custom && Selectable(f)) return null;
        if (f.Kind == "CustomLogic" || f.Custom) return "is read by hand-written code";
        if (f.Kind is "Dictionary" or "Array2D" && f.Size == null) return $"is a {f.Kind}";
        if (f.Kind == "Object" && f.RefObject is { } ro && TargetsOf(ro).Count > 1 && f.SelectedBy != "here:Int32") return "has a kind the bytes don't tell";
        return null;
    }

    void StaticField(Group g, FieldDef f, string? map, string prefix, Dictionary<string, string?> reach, string? stop, int depth, HashSet<string> stack)
    {
        string path = PathOf(prefix, f.Name);
        string? why = stop ?? ((f.Kind == "CustomLogic" || f.Custom) && LaidOutLink(f) == null && !Placed(f) && !Selectable(f) && !(_staticInRun && f.Custom && RunGenerated(f)) ? "read by Mutagen's hand-written code" : null);
        if (f.Custom && Selectable(f) && f.RefObject is { } sro)
        {
            // The kinds a selector picks: placed by the layout attributes, or read from where the field stands
            foreach (var t in TargetsOf(sro))
            {
                var own = _m.Ancestry(t.Name).SelectMany(o => o.Fields).Where(d => d.Path == d.Name && !NoBytes(d)).ToList();
                if (own.Count > 0 && own.All(d => d.LayoutOffsetNumber != null)) { if (!_staticAll) foreach (var d in own.Where(IsLinkDef)) Mark(reach, path + t.Marker + "." + d.Name, why); }
                else StaticStruct(t.Name, f.SigMap ?? map, path + t.Marker, reach, why, depth, stack);
            }
            return;
        }
        switch (f.Kind)
        {
            case "List" or "Array":
                if (g.Item is not { } item) return;
                if (_staticAll) Mark(reach, path + "[*]", why ?? (item.RefObject is { } lro && TargetsOf(lro).Count > 1 && item.SelectedBy != "here:Int32" ? "its kind isn't told by the bytes" : null));
                if (item.RefObject is { } ro)
                    foreach (var t in TargetsOf(ro)) StaticStruct(t.Name, item.SigMap ?? map, path + "[*]" + t.Marker, reach, why ?? (TargetsOf(ro).Count > 1 && item.SelectedBy != "here:Int32" ? "its kind isn't told by the bytes" : null), depth, stack);
                else if (Want(item)) Mark(reach, path + "[*]", why);
                return;
            case "Object":
                if (f.RefObject is { } ro2)
                    foreach (var t in TargetsOf(ro2)) StaticStruct(t.Name, f.SigMap ?? map, path + t.Marker, reach, why ?? (TargetsOf(ro2).Count > 1 && f.SelectedBy != "here:Int32" && !Selectable(f) ? "its kind isn't told by the bytes" : null), depth, stack);
                return;
            case "Gendered":
                if (g.Male != null && Want(g.Male)) Mark(reach, path + ".Male", why);
                if (g.Female != null && Want(g.Female)) Mark(reach, path + ".Female", why);
                if (g.Male?.RefObject is { } m1) StaticStruct(m1, map, path + ".Male", reach, why, depth, stack);
                if (g.Female?.RefObject is { } f1) StaticStruct(f1, map, path + ".Female", reach, why, depth, stack);
                return;
            default:
                // A value walk gives a laid-out link and a value stored in another form as unread
                if (_staticAll && why == null && (LaidOutLink(f) != null || Placed(f) && (f.LayoutEncoding != null || f.LayoutBits != null || f.LayoutDroppedBits != null))) why = "read by Mutagen's hand-written code";
                if (Want(f)) Mark(reach, path, why);
                return;
        }
    }

    /// <summary>An object read inside a subrecord: its fields one after another, each reached while those before it can
    /// be stepped over.</summary>
    void StaticStruct(string obj, string? map, string path, Dictionary<string, string?> reach, string? why, int depth, HashSet<string> stack)
    {
        if (depth > SchemaModel.MaxDepth || !stack.Add(obj)) return;
        try
        {
            // (a value walk gives a reflection's Data and its FormLinks, each at its place: Reflection above)
            if (obj == "Reflection") { if (_staticAll) Mark(reach, path + ".Data", why); Mark(reach, path + ".FormLinks[*]", why); return; }
            if (obj == "NpcRaceOverride") return; // Mutagen's NPC_ hand-read layout reads it (C57 W3), not the generated walk
            string? stop = why;
            if (_staticAll)
            {
                // As Decode reads it: a value Mutagen reads by hand is passed over when the schema places the field after
                // it (its offset in the object), and the walk goes on there; any other field it can't step over ends it
                var gs = Groups(obj, map);
                for (int i = 0; i < gs.Count; i++)
                {
                    var f = gs[i].Main;
                    bool passed = (f.Kind == "CustomLogic" || f.Custom) && !Selectable(f) && !Placed(f) && i + 1 < gs.Count && gs[i + 1].Main.Offset is int && gs[i + 1].Main.OffsetRel;
                    StaticField(gs[i], f, map, path, reach, passed ? stop ?? "read by Mutagen's hand-written code" : stop, depth + 1, stack);
                    if (!passed && stop == null && Unfollowed(f) is { } u) stop = $"after {gs[i].Name}, which {u}";
                }
                return;
            }
            foreach (var g in Groups(obj, map))
            {
                StaticField(g, g.Main, map, path, reach, stop, depth + 1, stack);
                if (stop == null && g.Main.Offset == null && Unfollowed(g.Main) is { } u) stop = $"after {g.Name}, which {u}";
                else if (stop == null && Unfollowed(g.Main) is { } u2) stop = $"after {g.Name}, which {u2}";
            }
        }
        finally { stack.Remove(obj); }
    }

    /// <summary>Every link field below a group or an object, marked as not reached, with why.</summary>
    void MarkAll(Group g, string path, Dictionary<string, string?> reach, string why)
    {
        if (IsLinkDef(g.Main)) Mark(reach, path, why);
        if (g.Item is { } item && IsLinkDef(item)) Mark(reach, path + "[*]", why);
        var ro = g.Item?.RefObject ?? g.Main.RefObject;
        if (ro != null) foreach (var t in TargetsOf(ro)) MarkAll(t.Name, (g.Item?.RefObject != null ? path + "[*]" : path) + t.Marker, reach, why);
    }

    void MarkAll(string obj, string path, Dictionary<string, string?> reach, string why)
    {
        foreach (var r in _m.Expand(obj).Take(4000))
            if (IsLinkDef(r.Def)) { string p = PathOf(path, r.Path); if (!reach.ContainsKey(p)) reach[p] = why; }
    }
}
