// A hand-read layout's subrecord content (Mutagen's Layouts/HandReadLayouts.xml), compiled once into a fixed-step reader
// (J125, GAPS S122, S153): where each value and each list of it sits in one subrecord, worked out from the bytes with no
// string, path or object made per value. The layout walk (HandLayoutWalk) follows any layout and names every value; this
// reads the bulky ones that hold millions of values (a navmesh's NVNM geometry, a surface tree's FNAM grids, the navmesh
// info map's NVMI) fast enough for the link census, the world link index and the list and typed views.
//
// What it takes (every element is Mutagen's file form, read as the file's header documents it; a layout with anything else
// isn't compiled, and its reader reads through Mutagen or the layout walk):
//   - fixed-size values: U8 I8 U16 I16 U32 I32 U64 F32 F16 FormID Color P3F32 P3I16 Guid; a name= keeps a number (a
//     count, a Switch's selector);
//   - Bytes with a fixed length, or to the end of the subrecord;
//   - Object field= (a path step), Null and Const (no bytes), Peek (a value further on, read without moving);
//   - Switch on a kept value (mask=, Case value= a list, Default), its Case's type= naming the object's kind;
//   - Versioning formVersion="N" (C84: the children only from the record's form version N on);
//   - Repeat count="N|$var|end" whose item is of a fixed size in the record (fixed-size values, Bytes of a fixed length,
//     Versioning, no Switch), or count="end" whose item is a Repeat count="end" to the subrecord's end (at most one item:
//     a navmesh's grid arrays).
// Every byte of the subrecord must be described, as the layout walk requires, or the run fails.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using SFSearch.SchemaGen;

namespace SFSearch.Records;

/// <summary>A hand-read layout's subrecord content compiled into a fixed-step reader (see the file's head).</summary>
public sealed class LayoutPlan
{
    /// <summary>
    /// One value the layout names, by its path from the subrecord's object (list entries as <c>[*]</c>, a Case's kind in
    /// braces: <c>Parent{WorldspaceNavmeshParent}.Parent</c>, <c>Vertices[*].Point</c>, <c>[*]</c> for a list of plain
    /// values' entry). <c>Kind</c> is the layout's element (<c>FormID</c>, <c>U16</c>, <c>Bytes</c>), or <c>Object</c> for
    /// an object's start, <c>Null</c> for a field Mutagen holds null there, <c>Const</c> for a value with no bytes
    /// (<c>Value</c>), <c>Kind</c> for the kind a Case picked at an object (<c>Value</c> its class). <c>List</c>: the list
    /// it is an entry's value of (an index into <see cref="Lists"/>), -1 for none. <c>Size</c>: its bytes, -1 for Bytes to
    /// the end.
    /// </summary>
    public sealed record Slot(string Path, string Kind, int Size, string? Encoding, int List, string? Value);

    /// <summary>A list the layout names: its path (<c>Vertices</c>), the list it sits in (-1: none), and the slots of its
    /// entry.</summary>
    public sealed record ListSlot(string Path, int Parent);

    public IReadOnlyList<Slot> Slots => _slots;
    public IReadOnlyList<ListSlot> Lists => _lists;

    readonly List<Slot> _slots = new();
    readonly List<ListSlot> _lists = new();
    readonly Node[] _root;
    int _vars;
    readonly Dictionary<string, int> _varIndex = new(StringComparer.Ordinal);

    /// <summary>Where one run of the plan put each slot and list (<see cref="Run"/>); reused from run to run.</summary>
    public sealed class Placed
    {
        /// <summary>Per slot: its offset in the subrecord (in its list's first entry for a list's), or -1 when the bytes
        /// don't hold it (a Case not taken, a Versioning below its form version, a Null's field: a Null is placed, with no
        /// bytes).</summary>
        public int[] At = Array.Empty<int>();
        /// <summary>Per list: where its first entry starts, how many entries, how many bytes each.</summary>
        public int[] Start = Array.Empty<int>(), Count = Array.Empty<int>(), Stride = Array.Empty<int>();
        internal long[] Vars = Array.Empty<long>();

        /// <summary>The offset of entry <paramref name="k"/> of a list slot's value (<paramref name="slot"/> in list
        /// <paramref name="list"/>), or -1.</summary>
        public int AtEntry(int slot, int list, int k) => At[slot] < 0 ? -1 : At[slot] + k * Stride[list];
    }

    public Placed NewPlaced() => new()
    {
        At = new int[_slots.Count], Start = new int[_lists.Count], Count = new int[_lists.Count], Stride = new int[_lists.Count], Vars = new long[_vars],
    };

    /// <summary>
    /// Places every value of one subrecord's content: false when the bytes don't follow the layout (a count past the end,
    /// bytes the layout doesn't describe, a value no Case takes). <paramref name="formVersion"/>: the record's (its
    /// header's uint16 at 20).
    /// </summary>
    public bool Run(ReadOnlySpan<byte> c, int formVersion, Placed r)
    {
        Array.Fill(r.At, -1);
        Array.Clear(r.Count);
        Array.Clear(r.Start);
        Array.Clear(r.Stride);
        int p = 0;
        return Exec(_root, c, ref p, formVersion, r) && (Partial || p == c.Length);
    }

    // ------------------------------------------------------------------ compiled form

    abstract class Node;
    sealed class Val(int size, int slot, int var, bool signed) : Node { public readonly int Size = size, Slot = slot, Var = var; public readonly bool Signed = signed; }
    sealed class Rest(int slot) : Node { public readonly int Slot = slot; }
    sealed class Mark(int slot) : Node { public readonly int Slot = slot; }
    sealed class Peek(int offset, int size, int var) : Node { public readonly int Offset = offset, Size = size, Var = var; }
    sealed class Switch(int var, long mask, List<(long[] Values, Node[] Body)> cases, Node[]? other) : Node
    {
        public readonly int Var = var; public readonly long Mask = mask;
        public readonly List<(long[] Values, Node[] Body)> Cases = cases; public readonly Node[]? Other = other;
    }
    sealed class Versioning(int from, Node[] body) : Node { public readonly int From = from; public readonly Node[] Body = body; }
    sealed class Repeat(int list, long count, int var, bool end, Node[] item, bool itemToEnd) : Node
    {
        public readonly int List = list, Var = var; public readonly long Count = count; public readonly bool End = end, ItemToEnd = itemToEnd;
        public readonly Node[] Item = item;
    }

    sealed class NotPlanned(string why) : Exception(why);

    /// <summary>The plan places the values of the subrecord's start only: its layout goes on with an element this doesn't
    /// read (<see cref="Stop"/> says which), so a run doesn't reach the subrecord's end and nothing after that is placed.
    /// Its values are where the layout puts them all the same (a VMAD's Version and ObjectFormat before its scripts).</summary>
    public bool Partial { get; }
    public string? Stop { get; }

    LayoutPlan(XElement content, IReadOnlyDictionary<string, XElement> defines)
    {
        // The subrecord's elements one by one (a Define used at its top level read as its elements): the first one this
        // can't read ends the plan there, with what it compiled of it taken back
        var top = new List<XElement>();
        void Flatten(IEnumerable<XElement> es, int depth)
        {
            foreach (var e in es)
                if (e.Name.LocalName == "Use" && depth < 8 && defines.TryGetValue(e.Attribute("name")?.Value ?? "", out var d)) Flatten(d.Elements(), depth + 1);
                else top.Add(e);
        }
        Flatten(content.Elements(), 0);
        var nodes = new List<Node>();
        foreach (var e in top)
        {
            int slots = _slots.Count, lists = _lists.Count;
            try { nodes.AddRange(Compile(new[] { e }, "", -1, defines, inItem: false)); }
            catch (NotPlanned x)
            {
                _slots.RemoveRange(slots, _slots.Count - slots);
                _lists.RemoveRange(lists, _lists.Count - lists);
                Partial = true; Stop = x.Message;
                break;
            }
        }
        _root = nodes.ToArray();
    }

    static string Join(string path, string? field) => field == null || field == "." ? path : path.Length == 0 ? field : path + "." + field;

    int Var(string name) => _varIndex.TryGetValue(name, out int i) ? i : _varIndex[name] = _vars++;

    int Add(string path, string kind, int size, string? encoding, int list, string? value)
    {
        _slots.Add(new Slot(path, kind, size, encoding, list, value));
        return _slots.Count - 1;
    }

    static int SizeOf(string n) => n switch
    {
        "U8" or "I8" => 1, "U16" or "I16" or "F16" => 2, "U32" or "I32" or "F32" or "FormID" or "Color" => 4,
        "U64" => 8, "P3F32" => 12, "P3I16" => 6, "Guid" => 16, _ => -1,
    };

    Node[] Compile(IEnumerable<XElement> children, string path, int list, IReadOnlyDictionary<string, XElement> defines, bool inItem)
    {
        var nodes = new List<Node>();
        foreach (var e in children)
        {
            string n = e.Name.LocalName;
            string? field = e.Attribute("field")?.Value, name = e.Attribute("name")?.Value;
            switch (n)
            {
                case "Note": break;
                case "Use":
                    if (!defines.TryGetValue(e.Attribute("name")?.Value ?? "", out var d)) throw new NotPlanned("a Use of no Define");
                    nodes.AddRange(Compile(d.Elements(), path, list, defines, inItem));
                    break;
                case "Object":
                    nodes.Add(new Mark(Add(Join(path, field), "Object", 0, null, list, null)));
                    nodes.AddRange(Compile(e.Elements(), Join(path, field), list, defines, inItem));
                    break;
                case "Null":
                    if (field != null) nodes.Add(new Mark(Add(Join(path, field), "Null", 0, null, list, null)));
                    break;
                case "Const":
                    if (field != null && e.Attribute("value")?.Value is { } cv && !cv.StartsWith('$')) nodes.Add(new Mark(Add(Join(path, field), "Const", 0, null, list, cv)));
                    else throw new NotPlanned("a Const of a value read earlier");
                    break;
                case "Peek":
                {
                    if (inItem || name == null) throw new NotPlanned("a Peek in a list's entry");
                    int size = e.Attribute("type")?.Value switch { "U8" => 1, "U16" => 2, "U32" or "FormID" => 4, _ => throw new NotPlanned("a Peek of another type") };
                    nodes.Add(new Peek(int.Parse(e.Attribute("offset")!.Value, CultureInfo.InvariantCulture), size, Var(name)));
                    break;
                }
                case "Bytes":
                {
                    string? length = e.Attribute("length")?.Value;
                    if (e.Attribute("expect") != null) throw new NotPlanned("Bytes with an expected value");
                    int slot = field == null ? -1 : Add(Join(path, field), "Bytes", length == null ? -1 : 0, e.Attribute("encoding")?.Value, list, null);
                    if (length == null) { if (inItem) throw new NotPlanned("Bytes to the end in a list's entry"); nodes.Add(new Rest(slot)); break; }
                    if (!int.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out int len)) throw new NotPlanned("Bytes of a length read earlier");
                    if (slot >= 0) _slots[slot] = _slots[slot] with { Size = len };
                    nodes.Add(new Val(len, slot, -1, false));
                    break;
                }
                case "Switch":
                {
                    if (inItem) throw new NotPlanned("a Switch in a list's entry");
                    string on = e.Attribute("on")?.Value ?? throw new NotPlanned("a Switch on nothing");
                    if (!_varIndex.TryGetValue(on, out int v)) throw new NotPlanned($"a Switch on {on}, which isn't kept");
                    long mask = e.Attribute("mask")?.Value is { } mk ? Convert.ToInt64(mk, 16) : -1;
                    var cases = new List<(long[], Node[])>();
                    Node[]? other = null;
                    foreach (var c in e.Elements())
                    {
                        string? type = c.Attribute("type")?.Value is { } t ? HandLayouts.ClassName(t.Split(',')[0]) : null;
                        // The kind the Case picks is the object's at the Switch's path: its fields' paths carry it
                        string at = type == null ? path : path + "{" + type + "}";
                        var body = new List<Node>();
                        if (type != null) body.Add(new Mark(Add(path, "Kind", 0, null, list, type)));
                        body.AddRange(Compile(c.Elements(), at, list, defines, inItem));
                        if (c.Name.LocalName == "Default") { other = body.ToArray(); continue; }
                        if (c.Name.LocalName != "Case") throw new NotPlanned($"a <{c.Name.LocalName}> in a Switch");
                        var values = (c.Attribute("value")?.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(x => x.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? long.Parse(x[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : long.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                        cases.Add((values, body.ToArray()));
                    }
                    nodes.Add(new Switch(v, mask, cases, other));
                    break;
                }
                case "Versioning":
                    nodes.Add(new Versioning(int.Parse(e.Attribute("formVersion")?.Value ?? throw new NotPlanned("a Versioning without its form version"), CultureInfo.InvariantCulture),
                        Compile(e.Elements(), path, list, defines, inItem)));
                    break;
                case "Repeat":
                {
                    string at = Join(path, field) + "[*]";
                    string count = e.Attribute("count")?.Value ?? throw new NotPlanned("a Repeat with no count");
                    if (e.Attribute("endsAtZeros") != null || e.Attribute("prefix") != null || e.Attribute("index") != null) throw new NotPlanned("a Repeat of another form");
                    if (inItem && count != "end") throw new NotPlanned("a counted list in a list's entry");
                    _lists.Add(new ListSlot(Join(path, field), list));
                    int li = _lists.Count - 1;
                    var item = Compile(e.Elements(), at, li, defines, inItem: true);
                    bool toEnd = item.Length == 1 && item[0] is Repeat { End: true };
                    if (!toEnd && !Fixed(item)) throw new NotPlanned($"{at}: an entry of no fixed size");
                    long count0 = 0; int var = -1; bool end = false;
                    if (count == "end") end = true;
                    else if (count.StartsWith('$')) { if (!_varIndex.TryGetValue(count[1..], out var)) throw new NotPlanned($"a count {count} not kept"); }
                    else if (!long.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out count0)) throw new NotPlanned($"a count {count}");
                    if (inItem && !end) throw new NotPlanned("a counted list in a list's entry");
                    nodes.Add(new Repeat(li, count0, var, end, item, toEnd));
                    break;
                }
                default:
                {
                    int size = SizeOf(n);
                    if (size < 0) throw new NotPlanned($"<{n}>");
                    if (e.Attribute("expect") != null) throw new NotPlanned("a value with an expected value");
                    // A list's element named by its FormKey (Keywords[3].FormKey) is the element itself
                    string? f = field == "FormKey" && path.EndsWith("[*]", StringComparison.Ordinal) ? "." : field;
                    int slot = f == null ? -1 : Add(Join(path, f), n, size, e.Attribute("encoding")?.Value, list, null);
                    int var = name == null ? -1 : inItem ? throw new NotPlanned("a kept value in a list's entry") : Var(name);
                    nodes.Add(new Val(size, slot, var, n is "I8" or "I16" or "I32"));
                    break;
                }
            }
        }
        return nodes.ToArray();
    }

    /// <summary>An entry of a fixed size within one record: values, fixed Bytes, marks, Versioning of such.</summary>
    static bool Fixed(Node[] item) => item.All(x => x switch
    {
        Val or Mark => true,
        Versioning v => Fixed(v.Body),
        _ => false,
    });

    // ------------------------------------------------------------------ running

    static bool Exec(Node[] nodes, ReadOnlySpan<byte> c, ref int p, int formVersion, Placed r)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case Val v:
                    if (p + v.Size > c.Length) return false;
                    if (v.Slot >= 0) r.At[v.Slot] = p;
                    if (v.Var >= 0) r.Vars[v.Var] = Number(c.Slice(p, v.Size), v.Signed);
                    p += v.Size;
                    break;
                case Rest rest:
                    if (rest.Slot >= 0) r.At[rest.Slot] = p;
                    p = c.Length;
                    break;
                case Mark m:
                    r.At[m.Slot] = p;
                    break;
                case Peek pk:
                    if (p + pk.Offset + pk.Size > c.Length) return false;
                    r.Vars[pk.Var] = Number(c.Slice(p + pk.Offset, pk.Size), false);
                    break;
                case Switch sw:
                {
                    long value = r.Vars[sw.Var] & sw.Mask;
                    Node[]? body = sw.Other;
                    foreach (var (values, b) in sw.Cases) if (Array.IndexOf(values, value) >= 0) { body = b; break; }
                    if (body == null || !Exec(body, c, ref p, formVersion, r)) return false;
                    break;
                }
                case Versioning ver:
                    if (formVersion >= ver.From && !Exec(ver.Body, c, ref p, formVersion, r)) return false;
                    break;
                case Repeat rp:
                {
                    int rel = 0;
                    if (rp.ItemToEnd)
                    {
                        // One entry that reads to the end of the subrecord, when bytes are left (a navmesh's grid arrays)
                        r.Start[rp.List] = p;
                        r.Count[rp.List] = p < c.Length ? 1 : 0;
                        if (p < c.Length && !Exec(rp.Item, c, ref p, formVersion, r)) return false;
                        r.Stride[rp.List] = c.Length - r.Start[rp.List];
                        break;
                    }
                    // The entry's size and its values' places in it, the same for every entry of this record
                    if (!Measure(rp.Item, ref rel, formVersion, r)) return false;
                    long n = rp.End ? (rel == 0 ? 0 : (c.Length - p) / rel) : rp.Var >= 0 ? r.Vars[rp.Var] : rp.Count;
                    if (rp.End && rel > 0 && (c.Length - p) % rel != 0) return false;
                    if (n < 0 || n > int.MaxValue || p + n * rel > c.Length) return false;
                    r.Start[rp.List] = p; r.Count[rp.List] = (int)n; r.Stride[rp.List] = rel;
                    // The entry's values: their places in the first entry
                    Shift(rp.Item, p, formVersion, r);
                    p += (int)(n * rel);
                    break;
                }
                default: return false;
            }
        }
        return true;
    }

    /// <summary>An entry's size, its values placed from 0.</summary>
    static bool Measure(Node[] item, ref int rel, int formVersion, Placed r)
    {
        foreach (var node in item)
        {
            switch (node)
            {
                case Val v: if (v.Slot >= 0) r.At[v.Slot] = rel; rel += v.Size; break;
                case Mark m: r.At[m.Slot] = rel; break;
                case Versioning ver: if (formVersion >= ver.From && !Measure(ver.Body, ref rel, formVersion, r)) return false; break;
                default: return false;
            }
        }
        return true;
    }

    /// <summary>Moves an entry's measured places to the list's first entry.</summary>
    static void Shift(Node[] item, int by, int formVersion, Placed r)
    {
        foreach (var node in item)
        {
            switch (node)
            {
                case Val { Slot: >= 0 } v: r.At[v.Slot] += by; break;
                case Mark m: r.At[m.Slot] += by; break;
                case Versioning ver: if (formVersion >= ver.From) Shift(ver.Body, by, formVersion, r); break;
            }
        }
    }

    static long Number(ReadOnlySpan<byte> b, bool signed) => b.Length switch
    {
        1 => signed ? (sbyte)b[0] : b[0],
        2 => signed ? BinaryPrimitives.ReadInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b),
        4 => signed ? BinaryPrimitives.ReadInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b),
        8 => unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(b)),
        _ => 0,
    };

    // ------------------------------------------------------------------ the plans of a schema's layouts

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SchemaModel, ConcurrentDictionary<XElement, PlanHolder>> Plans = new();
    sealed record PlanHolder(LayoutPlan? Plan, string? Why);

    /// <summary>
    /// The plan of one Sub or SubRepeat element of a layout (its content); null when its content isn't of the form this
    /// reads (<paramref name="why"/> says what). Compiled once per schema.
    /// </summary>
    public static LayoutPlan? Of(SchemaModel m, XElement sub, out string? why)
    {
        var h = Plans.GetValue(m, _ => new ConcurrentDictionary<XElement, PlanHolder>(ReferenceEqualityComparer.Instance)).GetOrAdd(sub, s =>
        {
            var defines = m.HandLayouts.Where(x => x.Kind == "Define" && x.Name != null).GroupBy(x => x.Name!).ToDictionary(g => g.Key, g => g.Last().Element, StringComparer.Ordinal);
            try { return new PlanHolder(new LayoutPlan(s, defines), null); }
            catch (NotPlanned e) { return new PlanHolder(null, e.Message); }
        });
        why = h.Why;
        return h.Plan;
    }

    /// <summary>
    /// The Sub (or SubRepeat) elements a layout reads at the record's level, in order, each with the field its content is
    /// (null: the record itself) and whether it repeats: a layout of Subs alone (optional or not), as SFTR's FNAM, GNAM,
    /// FNAM, GNAM, an AnyRecord's one Sub (NVNM), NAVI's SubRepeat NVMI. Null for a layout with any other element at that
    /// level (Segments, Objects, Generated subrecords), whose subrecords only the layout walk finds.
    /// </summary>
    public static IReadOnlyList<(XElement Sub, string Sig, string? Field, bool Repeats, bool Optional)>? SubsOf(HandLayout h)
    {
        var list = new List<(XElement, string, string?, bool, bool)>();
        foreach (var e in h.Element.Elements())
        {
            if (e.Name.LocalName == "Note") continue;
            if (e.Name.LocalName is not ("Sub" or "SubRepeat") || e.Attribute("at") != null || e.Attribute("count") != null || e.Attribute("index") != null) return null;
            list.Add((e, e.Attribute("sig")!.Value, e.Attribute("field")?.Value, e.Name.LocalName == "SubRepeat", e.Attribute("optional")?.Value == "true"));
        }
        return list.Count == 0 ? null : list;
    }

    /// <summary>
    /// Finds the subrecords a layout of Subs alone (<see cref="SubsOf"/>) reads, in a record's subrecords at its own level
    /// (components between BFCB and BFCE left out), as the layout walk does: each Sub the next subrecord of its signature
    /// from where the walk stands, a SubRepeat the run of them there. <paramref name="at"/> gets, for each Sub, the
    /// subrecord indexes it reads (empty: none). False when a Sub that isn't optional isn't there.
    /// </summary>
    public static bool Find(IReadOnlyList<(XElement Sub, string Sig, string? Field, bool Repeats, bool Optional)> subs, IReadOnlyList<string> sigs, List<int>[] at)
    {
        int cursor = 0;
        for (int i = 0; i < subs.Count; i++)
        {
            at[i].Clear();
            int j = cursor;
            while (j < sigs.Count && sigs[j] != subs[i].Sig) j++;
            if (j >= sigs.Count) { if (subs[i].Optional || subs[i].Repeats) continue; return false; }
            if (!subs[i].Repeats) { at[i].Add(j); cursor = j + 1; continue; }
            for (; j < sigs.Count && sigs[j] == subs[i].Sig; j++) at[i].Add(j);
            cursor = j;
        }
        return true;
    }
}
