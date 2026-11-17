// The byte readers of four bulky subrecords that hold few links in a lot of data: a cell's or placed object's traversals
// (XTV2), a navmesh's geometry (NVNM), a surface tree's pattern grids (FNAM) and the navmesh info map's map infos (NVMI).
// Each reads its links where Mutagen's own layout of the subrecord (the package's Layouts/HandReadLayouts.xml) puts them,
// compiled from the layout (the traversals' Define, C47; since fixes.24, J125: AnyRecord NVNM, Record SFTR's FNAMs and
// Record NAVI's NVMI through LayoutPlan, C84), so no copy of Mutagen's hand-written readers is kept here. Each link (null
// ones too, as 0) goes to a sink with the field it is. The world link index (J32, SFSearch/Engine/WorldReader.cs) and the
// link census of profile (J68, LinkWalk.cs) read them. A reader that doesn't reach its subrecord's end as Mutagen lays it
// out says so (false), and its caller reads the record another way.
using System.Buffers.Binary;
using System.Globalization;
using System.Xml.Linq;
using SFSearch.SchemaGen;

namespace SFSearch.Records;

/// <summary>Where <see cref="WorldBytes"/>' readers put each link they find.</summary>
public interface IWorldLinkSink
{
    /// <param name="field">One of <see cref="WorldBytes"/>' field numbers.</param>
    /// <param name="raw">The FormID as stored (0: a null link).</param>
    /// <param name="count">How many times (a surface tree's grid gives each pattern once with its count).</param>
    void Link(int field, uint raw, uint count);
}

public static class WorldBytes
{
    /// <summary>The fields the readers give, as the schema names them (<see cref="Paths"/>).</summary>
    public const int Traversal = 0, NavWorldParent = 1, NavCellParent = 2, NavUnusedWorldspaceParent = 3, NavEdge = 4, NavDoor = 5,
        Patterns = 6, Patterns2 = 7, MiMesh = 8, MiMerged = 9, MiPreferred = 10, MiDoor = 11, MiTravCell = 12, MiTravTrav = 13,
        MiTravFrom = 14, MiTravTo = 15, MiWorld = 16, MiCell = 17;

    /// <summary>Each field's schema path (with its kind of object where the schema writes one).</summary>
    public static readonly string[] Paths =
    {
        "Traversals[*].Traversal",
        "NavmeshGeometry.Parent{WorldspaceNavmeshParent}.Parent", "NavmeshGeometry.Parent{CellNavmeshParent}.Parent",
        "NavmeshGeometry.Parent{CellNavmeshParent}.UnusedWorldspaceParent", "NavmeshGeometry.EdgeLinks[*].Mesh", "NavmeshGeometry.DoorTriangles[*].Door",
        "SurfacePatterns[*]", "SurfacePatterns2[*]",
        "MapInfos[*].NavigationMesh", "MapInfos[*].MergedTo[*]", "MapInfos[*].PreferredMerges[*]", "MapInfos[*].LinkedDoors[*].Door",
        "MapInfos[*].Traversals[*].CellOrObject", "MapInfos[*].Traversals[*].Traversal", "MapInfos[*].Traversals[*].FromNavmesh", "MapInfos[*].Traversals[*].ToNavmesh",
        "MapInfos[*].PathingCell.ParentWorld", "MapInfos[*].PathingCell.ParentCell",
    };

    public const uint Xtv2 = 0x32565458, Nvnm = 0x4D4E564E, Fnam = 0x4D414E46, Nvmi = 0x494D564E;
    public const uint SigCell = 0x4C4C4543, SigRefr = 0x52464552, SigNavm = 0x4D56414E, SigSftr = 0x52544653, SigNavi = 0x4956414E;

    /// <summary>
    /// XTV2, a cell's or a placed object's traversals, read as Mutagen's own layout lays them out (the package's
    /// HandReadLayouts.xml, Define "Traversals", C47; <see cref="TraversalLayout"/>): items while a whole one fits, none
    /// that starts with the layout's zero bytes (Mutagen's "fluff" after the list), each item's FormID there only when its
    /// flags say so. An item without a FormID gives nothing (Mutagen's Traversal is null there). False when the schema's
    /// layout isn't one this reads (the caller then reads the record through Mutagen).
    /// </summary>
    public static bool Traversals(SchemaModel m, ReadOnlySpan<byte> c, IWorldLinkSink sink)
    {
        if (TraversalLayout.Of(m) is not { } l) return false;
        int pos = 0;
        while (true)
        {
            int rest = c.Length - pos;
            if (rest < l.Fixed) break;
            uint flags = l.FlagsSize switch { 1 => c[pos + l.FlagsAt], 2 => BinaryPrimitives.ReadUInt16LittleEndian(c[(pos + l.FlagsAt)..]), _ => BinaryPrimitives.ReadUInt32LittleEndian(c[(pos + l.FlagsAt)..]) };
            bool hasTraversal = (flags & l.Mask) == l.FormIdWhen;
            int size = l.Fixed + (hasTraversal ? 4 : 0);
            if (rest < size || c.Slice(pos, l.ZeroBytes).IndexOfAnyExcept((byte)0) < 0) break;
            if (hasTraversal) sink.Link(Traversal, BinaryPrimitives.ReadUInt32LittleEndian(c[(pos + l.FormIdAt)..]), 1);
            pos += size;
        }
        return true;
    }

    /// <summary>
    /// The traversal list's layout, taken from the schema's copy of Mutagen's Define "Traversals" (C47): a Sub XTV2 holding a
    /// Repeat count="fit" endsAtZeros="N" of fixed-size values and one Switch on a flags value whose Case holds the FormID
    /// and whose Default holds nothing (Null). Null when the Define is missing or of another shape.
    /// </summary>
    public sealed class TraversalLayout
    {
        /// <summary>An item's bytes without its FormID; where the flags sit and their width; the FormID is there when
        /// (flags &amp; Mask) == FormIdWhen, at FormIdAt; an item starting with ZeroBytes zero bytes isn't one.</summary>
        public int Fixed { get; private init; }
        public int FlagsAt { get; private init; }
        public int FlagsSize { get; private init; }
        public uint Mask { get; private init; }
        public uint FormIdWhen { get; private init; }
        public int FormIdAt { get; private init; }
        public int ZeroBytes { get; private init; }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SchemaModel, Holder> Cache = new();
        sealed class Holder(TraversalLayout? l) { public readonly TraversalLayout? Layout = l; }

        public static TraversalLayout? Of(SchemaModel m) => Cache.GetValue(m, x => new Holder(Make(x))).Layout;

        static int SizeOf(string kind) => kind switch
        {
            "I8" or "U8" => 1, "I16" or "U16" or "F16" => 2, "I32" or "U32" or "F32" or "FormID" => 4, "I64" or "U64" or "F64" => 8,
            "P3F32" => 12, "P3I16" => 6, "P2F32" => 8, _ => -1,
        };

        static uint Number(string? text) => text == null ? 0 : text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : uint.Parse(text, CultureInfo.InvariantCulture);

        static TraversalLayout? Make(SchemaModel m)
        {
            var define = m.HandLayouts.LastOrDefault(h => h.Kind == "Define" && h.Name == "Traversals")?.Element;
            var sub = define?.Elements().SingleOrDefault();
            if (sub is not { Name.LocalName: "Sub" } || (string?)sub.Attribute("sig") != "XTV2") return null;
            var repeat = sub.Elements().FirstOrDefault();
            if (repeat is not { Name.LocalName: "Repeat" } || (string?)repeat.Attribute("count") != "fit" || !int.TryParse((string?)repeat.Attribute("endsAtZeros"), out int zeros)) return null;
            int at = 0, flagsAt = -1, flagsSize = 0, formIdAt = -1; uint mask = 0, when = 0;
            var named = new Dictionary<string, (int At, int Size)>(StringComparer.Ordinal);
            foreach (var e in repeat.Elements())
            {
                string kind = e.Name.LocalName;
                if (kind == "Switch")
                {
                    if (formIdAt >= 0 || !named.TryGetValue((string?)e.Attribute("on") ?? "", out var on)) return null;
                    var cases = e.Elements().ToList();
                    if (cases.Count != 2 || cases[0] is not { Name.LocalName: "Case" } c0 || cases[1] is not { Name.LocalName: "Default" } d0) return null;
                    if (c0.Elements().SingleOrDefault() is not { Name.LocalName: "FormID" } || d0.Elements().SingleOrDefault() is not { Name.LocalName: "Null" }) return null;
                    (flagsAt, flagsSize) = on; mask = Number((string?)e.Attribute("mask")); when = Number((string?)c0.Attribute("value"));
                    formIdAt = at;
                    continue;
                }
                int size = SizeOf(kind);
                if (size < 0) return null;
                if ((string?)e.Attribute("name") is { } name) named[name] = (at, size);
                at += size;
            }
            if (formIdAt < 0 || flagsSize is not (1 or 2 or 4) || zeros > at || mask == 0) return null;
            return new TraversalLayout { Fixed = at, FlagsAt = flagsAt, FlagsSize = flagsSize, Mask = mask, FormIdWhen = when, FormIdAt = formIdAt, ZeroBytes = zeros };
        }
    }

    /// <summary>
    /// NVNM, a navigation mesh's geometry (or a generic base form's, an activator's ...), by Mutagen's layout of it (C84,
    /// AnyRecord NVNM): its parent, the edge links' meshes and the door triangles' doors. <paramref name="formVersion"/>: the
    /// record's (a vertex holds 4 more bytes from 493 on, the layout's Versioning).
    /// </summary>
    public static bool Geometry(SchemaModel m, ReadOnlySpan<byte> c, int formVersion, IWorldLinkSink sink)
        => Bulky.Of(m, "NVNM", 0) is { } b && b.Read(c, formVersion, sink);

    /// <summary>SFTR FNAM, a surface tree's grid of 65,536 surface patterns, by Mutagen's layout of it (C84, Record SFTR):
    /// the first FNAM is SurfacePatterns, the next SurfacePatterns2 (<paramref name="field"/>: <see cref="Patterns"/> or
    /// <see cref="Patterns2"/>). Each pattern is given once with its count.</summary>
    public static bool PatternGrid(SchemaModel m, ReadOnlySpan<byte> c, int field, IWorldLinkSink sink)
        => Bulky.Of(m, "FNAM", field == Patterns ? 0 : 1) is { } b && b.Fields.Contains(field) && b.Read(c, 0, sink);

    /// <summary>NAVI NVMI, one map info, by Mutagen's layout of it (C47, Record NAVI): its navigation mesh, merged and
    /// preferred navmeshes, linked doors, traversals and pathing cell.</summary>
    public static bool MapInfo(SchemaModel m, ReadOnlySpan<byte> c, IWorldLinkSink sink)
        => Bulky.Of(m, "NVMI", 0) is { } b && b.Read(c, 0, sink);

    /// <summary>
    /// One bulky subrecord's compiled layout (LayoutPlan) and the link fields of its FormIDs: the layout that reads the
    /// signature (an AnyRecord of it, or a record type's layout of Subs alone), its <c>ordinal</c>-th Sub of that signature.
    /// Every FormID the layout names must be one of <see cref="Paths"/>, or the reader isn't made (a link the world index
    /// and the census wouldn't count).
    /// </summary>
    sealed class Bulky
    {
        readonly LayoutPlan _plan;
        readonly (int Slot, int List, int Field)[] _links;
        public readonly HashSet<int> Fields;
        [ThreadStatic] static Dictionary<LayoutPlan, LayoutPlan.Placed>? t_placed;
        [ThreadStatic] static uint[]? t_scratch;

        Bulky(LayoutPlan plan, (int, int, int)[] links) { _plan = plan; _links = links; Fields = links.Select(x => x.Item3).ToHashSet(); }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SchemaModel, System.Collections.Concurrent.ConcurrentDictionary<(string, int), Bulky?>> Made = new();

        public static Bulky? Of(SchemaModel m, string sig, int ordinal) => Made.GetValue(m, _ => new()).GetOrAdd((sig, ordinal), k => Make(m, k.Item1, k.Item2));

        static Bulky? Make(SchemaModel m, string sig, int ordinal)
        {
            foreach (var h in m.HandLayouts.Where(x => x.Kind is "Record" or "AnyRecord"))
            {
                if (h.Kind == "AnyRecord" && Array.IndexOf(HandLayoutWalk.Sigs(h), sig) < 0) continue;
                if (LayoutPlan.SubsOf(h) is not { } subs) continue;
                var mine = subs.Where(x => x.Sig == sig).ToList();
                if (mine.Count <= ordinal) continue;
                var (sub, _, field, repeats, _) = mine[ordinal];
                if (LayoutPlan.Of(m, sub, out _) is not { Partial: false } plan) return null;
                string prefix = field == null ? "" : field + (repeats ? "[*]" : "");
                var links = new List<(int, int, int)>();
                for (int i = 0; i < plan.Slots.Count; i++)
                {
                    var sl = plan.Slots[i];
                    if (sl.Kind != "FormID") continue;
                    string full = prefix.Length == 0 ? sl.Path : sl.Path.StartsWith('[') ? prefix + sl.Path : prefix + "." + sl.Path;
                    int f = Array.IndexOf(Paths, full);
                    if (f < 0) return null;
                    links.Add((i, sl.List, f));
                }
                return new Bulky(plan, links.ToArray());
            }
            return null;
        }

        public bool Read(ReadOnlySpan<byte> c, int formVersion, IWorldLinkSink sink)
        {
            var placedOf = t_placed ??= new Dictionary<LayoutPlan, LayoutPlan.Placed>(ReferenceEqualityComparer.Instance);
            if (!placedOf.TryGetValue(_plan, out var r)) placedOf[_plan] = r = _plan.NewPlaced();
            if (!_plan.Run(c, formVersion, r)) return false;
            foreach (var (slot, list, field) in _links)
            {
                if (list < 0)
                {
                    if (r.At[slot] >= 0) sink.Link(field, BinaryPrimitives.ReadUInt32LittleEndian(c[r.At[slot]..]), 1);
                    continue;
                }
                int n = r.Count[list];
                if (n == 0 || r.At[slot] < 0) continue;
                if (n < 64)
                {
                    for (int k = 0; k < n; k++) sink.Link(field, BinaryPrimitives.ReadUInt32LittleEndian(c[r.AtEntry(slot, list, k)..]), 1);
                    continue;
                }
                // A long list (a surface tree's 65,536 patterns): each FormID once, with how many times
                var grid = t_scratch is { } g && g.Length >= n ? g : t_scratch = new uint[Math.Max(n, 65536)];
                for (int k = 0; k < n; k++) grid[k] = BinaryPrimitives.ReadUInt32LittleEndian(c[r.AtEntry(slot, list, k)..]);
                Array.Sort(grid, 0, n);
                for (int i = 0; i < n;)
                {
                    int j = i + 1;
                    while (j < n && grid[j] == grid[i]) j++;
                    sink.Link(field, grid[i], (uint)(j - i));
                    i = j;
                }
            }
            return true;
        }
    }
}
