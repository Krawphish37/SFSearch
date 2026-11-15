// Walks Mutagen's hand-read layouts (the package's Layouts\HandReadLayouts.xml, C186, kept in schema.db's HandLayouts)
// over one record's bytes, and says where each value they name sits: the subrecord, the offset, the size, and how the
// stored bytes map to Mutagen's value. Evidence (EvidenceItems.cs) gives a field's slice from it and checks the slice
// against Mutagen's own value, as for every other slice. Nothing here knows a record type: the layout file says it all
// (a perk's effects, an object template's OBTS, an object mod's DATA with its 24-byte properties, VMAD, weather clouds,
// a race's biped objects, a package's idles, a footstep set, a region's RDAT, a placed trap's NAME).
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
    public List<string> Problems { get; } = new();
    /// <summary>The layouts that applied: "Record PERK", "AnyRecord VMAD".</summary>
    public List<string> Used { get; } = new();

    readonly SchemaModel _m;
    readonly RawRecord _raw;
    readonly bool _localized;
    readonly Dictionary<string, CardField> _card;
    readonly Dictionary<string, XElement> _defines = new(StringComparer.Ordinal);
    string _layout = "";

    HandLayoutWalk(SchemaModel m, RawRecord raw, Card card, bool localized)
    {
        _m = m; _raw = raw; _localized = localized;
        _card = new Dictionary<string, CardField>(StringComparer.Ordinal);
        void Add(IEnumerable<CardField> fields) { foreach (var f in fields) { _card.TryAdd(f.Path, f); if (f.Children != null) Add(f.Children); } }
        Add(card.Fields);
        foreach (var d in m.HandLayouts.Where(h => h.Kind == "Define" && h.Name != null)) _defines[d.Name!] = d.Element;
    }

    sealed class Stop(string why) : Exception(why);

    /// <summary>Walks every layout that applies to the record: its type's Record, and each AnyRecord whose subrecord it has.</summary>
    public static HandLayoutWalk Run(SchemaModel m, RawRecord raw, Card card, bool localized)
    {
        var w = new HandLayoutWalk(m, raw, card, localized);
        foreach (var h in m.HandLayouts)
        {
            if (h.Kind == "Record" && h.Sig == card.Signature || h.Kind == "AnyRecord" && raw.Subrecords.Any(s => s.Signature == h.Sig))
            {
                w._layout = $"{h.Kind} {h.Sig}";
                w.Used.Add(w._layout);
                int cursor = 0;
                try { w.Subs(h.Element.Elements(), ref cursor, 0, raw.Subrecords.Count, "", new Dictionary<string, Var>(StringComparer.Ordinal)); }
                catch (Stop s) { w.Problems.Add($"{w._layout}: {s.Message}"); }
            }
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
        _m = m; _raw = null!; _card = new(StringComparer.Ordinal);
        foreach (var d in m.HandLayouts.Where(h => h.Kind == "Define" && h.Name != null)) _defines[d.Name!] = d.Element;
    }

    /// <summary>Whether the schema has a hand-read layout for this record at all (its type's, or an AnyRecord's).</summary>
    public static bool Applies(SchemaModel m, RawRecord raw, string sig)
        => m.HandLayouts.Any(h => h.Kind == "Record" && h.Sig == sig || h.Kind == "AnyRecord" && raw.Subrecords.Any(s => s.Signature == h.Sig));

    string Sig(int i) => _raw.Subrecords[i].Signature;
    static string? A(XElement e, string name) => e.Attribute(name)?.Value;

    static string Join(string path, string? field)
    {
        if (field == null || field == ".") return path;
        string p = path.Length == 0 ? field : path + "." + field;
        return p;
    }

    /// <summary>A link list's element is named by its FormKey in the layout (<c>Keywords[3].FormKey</c>); the card names
    /// the element itself (<c>Keywords[3]</c>).</summary>
    static string Leaf(string path) => path.EndsWith("].FormKey", StringComparison.Ordinal) ? path[..^".FormKey".Length] : path;

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
            switch (e.Name.LocalName)
            {
                case "Note": case "Generated": break;
                case "Use": Subs(Define(e).Elements(), ref cursor, from, to, path, vars); break;
                case "Sub":
                {
                    string sig = A(e, "sig")!;
                    int j = Next(sig, cursor, to);
                    if (j < 0) { if (A(e, "optional") == "true") break; throw new Stop($"no {sig} subrecord where {path} has one"); }
                    Content(e.Elements(), j, Join(path, A(e, "field")), vars);
                    cursor = j + 1;
                    break;
                }
                case "SubRepeat":
                {
                    string sig = A(e, "sig")!;
                    int j = Next(sig, cursor, to);
                    if (j < 0) break;
                    long n = A(e, "count") != null ? Count(A(e, "count"), vars, sig) : long.MaxValue;
                    string list = Join(path, A(e, "field"));
                    for (int k = 0; k < n && j < to && Sig(j) == sig; k++, j++)
                    {
                        var v = new Dictionary<string, Var>(vars, StringComparer.Ordinal);
                        if (A(e, "index") is { } ix) v[ix] = new Var(-1, 0, 0, k, null);
                        Content(e.Elements(), j, $"{list}[{k}]", v);
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
                        if (sub != null) Content(sub.Elements(), j, Join(path, A(sub, "field")), vars);
                        else if (!generated.Contains(Sig(j))) break;
                    }
                    cursor = j;
                    break;
                }
                case "Segments":
                {
                    string start = A(e, "start")!; string? end = A(e, "end"), stopAt = A(e, "stopAt");
                    string list = Join(path, A(e, "field"));
                    SegmentSigs[Unindexed(list)] = SigsOf(e);
                    int k = 0, j = cursor;
                    while (j < to && Sig(j) != start && Sig(j) != stopAt) j++;
                    while (j < to && Sig(j) == start)
                    {
                        int s = j, x = j + 1;
                        while (x < to && Sig(x) != start && (end == null || Sig(x - 1) != end) && Sig(x) != stopAt) x++;
                        // Without a field the runs aren't list items: their values belong to the object itself (REGN's RDAT blocks)
                        string item = A(e, "field") == null ? path : $"{list}[{k}]";
                        if (A(e, "field") != null) Segments[item] = (s, x);
                        var v = new Dictionary<string, Var>(vars, StringComparer.Ordinal);
                        if (A(e, "index") is { } ix) v[ix] = new Var(-1, 0, 0, k, null);
                        int c = A(e, "skipStart") == "true" ? s + 1 : s;
                        try { Subs(e.Elements(), ref c, s, x, item, v); }
                        catch (Stop st) { Problems.Add($"{_layout}: {item}: {st.Message}"); }
                        if (A(e, "field") != null) SegmentVars[item] = v;
                        k++; j = x;
                        // Between segments: what isn't a start (a stopAt, or generated subrecords) ends the list
                        while (j < to && Sig(j) != start && Sig(j) != stopAt && end != null) j++;
                    }
                    cursor = j;
                    break;
                }
                case "Object":
                {
                    string obj = Join(path, A(e, "field"));
                    if (A(e, "optional") == "true" && !_card.ContainsKey(obj))
                    {
                        // Mutagen has none: none of its subrecords may follow (checked by the first one's absence)
                        break;
                    }
                    Subs(e.Elements(), ref cursor, from, to, obj, vars);
                    break;
                }
                case "Switch": case "TypeSwitch":
                {
                    var chosen = Choose(e, path, vars);
                    if (chosen != null) Subs(chosen.Elements(), ref cursor, from, to, path, vars);
                    break;
                }
                default:
                    throw new Stop($"<{e.Name.LocalName}> at the subrecord level");
            }
        }
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
        var set = new HashSet<string>(StringComparer.Ordinal) { A(seg, "start")! };
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

    static string Unindexed(string path) => System.Text.RegularExpressions.Regex.Replace(path, @"\[\d+\]", "[*]");

    /// <summary>The Case a Switch (by a value read earlier) or a TypeSwitch (by the class Mutagen made) takes, its
    /// type checked against the class Mutagen made for the object at <paramref name="path"/>.</summary>
    XElement? Choose(XElement sw, string path, Dictionary<string, Var> vars)
    {
        var cases = sw.Elements().Where(c => c.Name.LocalName == "Case").ToList();
        var def = sw.Elements().FirstOrDefault(c => c.Name.LocalName == "Default");
        string? made = _card.TryGetValue(path, out var cf) ? cf.Object : null;
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
            pick = cases.FirstOrDefault(c => (A(c, "value") ?? "").Split(',').Contains(val, StringComparer.Ordinal)) ?? def;
            said = $"{on} = {(v.Number != null ? val : "\"" + val + "\"")}";
        }
        if (pick == null) throw new Stop($"no Case takes {said}");
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

    void Content(IEnumerable<XElement> children, int sub, string path, Dictionary<string, Var> vars)
    {
        var s = _raw.Subrecords[sub];
        var c = new Cursor(sub, s.Start, s.Length);
        Content(children, c, path, vars, null);
        if (c.P != c.Length) Problems.Add($"{_layout}: {Sig(sub)} at {path}: the layout describes {c.P} of its {c.Length} bytes");
    }

    ReadOnlySpan<byte> Bytes(Cursor c, int at, int size) => _raw.Data.AsSpan(c.Start + at, size);

    void Need(Cursor c, int size, string what)
    {
        if (size < 0 || c.P + size > c.Length) throw new Stop($"{what}: {size} bytes at offset {c.P} of {Sig(c.Sub)}, which is {c.Length} bytes");
    }

    /// <param name="selector">The variable the nearest Switch chose by: a Const's value is derived from it.</param>
    void Content(IEnumerable<XElement> children, Cursor c, string path, Dictionary<string, Var> vars, Var? selector)
    {
        foreach (var e in children)
        {
            string n = e.Name.LocalName;
            string? field = A(e, "field"), name = A(e, "name");
            switch (n)
            {
                case "Note": break;
                case "Use": Content(Define(e).Elements(), c, path, vars, selector); break;
                case "U8" or "I8" or "U16" or "I16" or "U32" or "I32" or "F32" or "FormID" or "Color":
                {
                    int size = n switch { "U8" or "I8" => 1, "U16" or "I16" => 2, _ => 4 };
                    Need(c, size, Join(path, field ?? name ?? n));
                    var b = Bytes(c, c.P, size);
                    if (A(e, "expect") is { } ex && !b.SequenceEqual(Convert.FromHexString(ex))) throw new Stop($"{Join(path, field ?? n)}: offset {c.P} of {Sig(c.Sub)} holds {Convert.ToHexString(b)}, the layout expects {ex}");
                    long? num = n switch
                    {
                        "U8" => b[0], "I8" => (sbyte)b[0], "U16" => BinaryPrimitives.ReadUInt16LittleEndian(b), "I16" => BinaryPrimitives.ReadInt16LittleEndian(b),
                        "U32" or "FormID" or "Color" => BinaryPrimitives.ReadUInt32LittleEndian(b), "I32" => BinaryPrimitives.ReadInt32LittleEndian(b), _ => null,
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
                    if (field != null) Add(Join(path, field), c.Sub, c.P, size, kind, null);
                    c.P += size;
                    break;
                }
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
                    if (field != null) Add(Join(path, field), c.Sub, c.P, size, "Bytes", null);
                    c.P += size;
                    break;
                }
                case "Const":
                {
                    string value = A(e, "value") ?? "";
                    if (value.StartsWith('$')) value = vars.TryGetValue(value[1..], out var v) ? v.Text ?? v.Number?.ToString(CultureInfo.InvariantCulture) ?? "" : throw new Stop($"Const {value}, which wasn't read");
                    if (selector == null) throw new Stop($"{Join(path, field)}: a Const outside a Switch");
                    Values.TryAdd(Join(path, field), new Value(Join(path, field), selector.Sub, selector.Offset, selector.Size, "Const", null, value, 0, 0, false, _layout));
                    break;
                }
                case "Repeat":
                {
                    string list = Join(path, field);
                    string? countText = A(e, "count");
                    long count = countText == "end" ? long.MaxValue : Count(countText, vars, list);
                    for (long k = 0; k < count && !(countText == "end" && c.Left == 0); k++)
                    {
                        if (A(e, "index") is { } ix) vars[ix] = new Var(-1, 0, 0, k, null);
                        Content(e.Elements(), c, $"{list}[{k}]", vars, selector);
                    }
                    break;
                }
                case "Object":
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
                case "IfMore":
                    if (c.Left > 0) Content(e.Elements(), c, path, vars, selector);
                    break;
                case "Peek":
                {
                    int off = int.Parse(A(e, "offset")!, CultureInfo.InvariantCulture);
                    int size = A(e, "type") switch { "U8" => 1, "U16" => 2, _ => 4 };
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

    void Add(string path, int sub, int offset, int size, string kind, string? encoding)
    {
        path = Leaf(path);
        Values.TryAdd(path, new Value(path, sub, offset, size, kind, encoding, null, 0, 0, false, _layout));
    }
}
