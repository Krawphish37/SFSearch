// A list view's named columns read from a bulky subrecord's bytes by Mutagen's own layout of it, compiled (LayoutPlan,
// J125, GAPS S153): a navmesh's NVNM geometry lists (vertices, triangles, edge links, door triangles, cover, cover
// triangle maps, waypoints, grid arrays), a surface tree's FNAM pattern grids, the navmesh info map's NVMI map infos and
// their lists. The layout (the package's Layouts/HandReadLayouts.xml: AnyRecord NVNM and Record SFTR from fixes.24, C84;
// Record NAVI from fixes.18, C47) says where each entry and value sits; the plan finds the lists' starts and counts in
// each record and reads each entry's values at their places, with no Mutagen object made. Each value is read by
// RawColumn's readers, as a fixed-layout list's (RawLists.cs). A value the layout gives no bytes in this record (below a
// Versioning's form version: a vertex's Unknown before 493) is Mutagen's default, read as zero bytes. A record whose
// bytes don't follow the layout (a count past the end, bytes left over, the subrecord more often than the layout takes
// it) goes to Mutagen. The tests world-reads and list-reads hold these against the records' JSON and, with --heavy,
// against Mutagen's object record by record.
using System.Buffers.Binary;
using SFSearch.Records;
using SFSearch.SchemaGen;

namespace SFSearch.Query;

sealed partial class RawList
{
    /// <summary>Planned: the layout's Subs at the record's level, the one whose content holds the list, its plan.</summary>
    IReadOnlyList<(System.Xml.Linq.XElement Sub, string Sig, string? Field, bool Repeats, bool Optional)>? _pSubs;
    int _pSub;
    LayoutPlan? _plan;
    /// <summary>The list in the plan (-1: the entries are the Sub's subrecords, a SubRepeat's); the list inside its entries
    /// (-1: none); whether each subrecord is an entry of the outer list (a SubRepeat).</summary>
    int _pList = -1, _pInner = -1;
    bool _pPerSub;
    /// <summary>Per column: the plan's slots that may hold it (one per Case that names it; the one placed is read).</summary>
    int[][] _pSlots = Array.Empty<int[]>();
    /// <summary>Per column: the zero bytes read for a value the layout gives no bytes in this record (Mutagen's default).</summary>
    byte[][] _pZero = Array.Empty<byte[]>();
    uint[] _pSigs = Array.Empty<uint>();
    [ThreadStatic] static Dictionary<LayoutPlan, LayoutPlan.Placed>? t_placed;

    /// <summary>A list view whose list Mutagen's layout of a bulky subrecord lays out, with every named column one of its
    /// entry's values of a kind RawColumn reads; null otherwise.</summary>
    static RawList? MakePlanned(ViewSpec v, IReadOnlyList<string> columns, RecordSchema schema)
    {
        if (v.Lists.Count is not (1 or 2) || v.Lists.Any(l => l.Kind is not ("List" or "Array"))) return null;
        string type = v.Types[0];
        var m = schema.Model;
        string? recordSig = schema.SignatureOf(type);
        string l1 = v.Lists[0].Path;
        string? l2 = v.Lists.Count == 2 ? v.Lists[1].Path : null;
        foreach (var h in m.HandLayouts.Where(x => x.Kind == "Record" && x.Sig == recordSig || x.Kind == "AnyRecord"))
        {
            if (LayoutPlan.SubsOf(h) is not { } subs) continue;
            for (int si = 0; si < subs.Count; si++)
            {
                var (sub, _, field, repeats, _) = subs[si];
                if (LayoutPlan.Of(m, sub, out _) is not { Partial: false } plan) continue;
                int list = -1, inner = -1;
                string entry;
                if (repeats)
                {
                    if (field != l1) continue;
                    if (l2 == null) entry = "";
                    else
                    {
                        list = Find(plan, l2, -1);
                        if (list < 0) continue;
                        entry = l2 + "[*]";
                    }
                }
                else
                {
                    string? rel = field == null ? l1 : l1.StartsWith(field + ".", StringComparison.Ordinal) ? l1[(field.Length + 1)..] : null;
                    if (rel == null || (list = Find(plan, rel, -1)) < 0) continue;
                    if (l2 == null) entry = rel + "[*]";
                    else
                    {
                        inner = Find(plan, rel + "[*]." + l2, list);
                        if (inner < 0) continue;
                        entry = rel + "[*]." + l2 + "[*]";
                    }
                }
                var made = Columns(v, columns, schema, type, plan, entry, repeats && l2 == null ? -1 : inner >= 0 ? inner : list);
                if (made == null) return null;
                var (slots, zero, readers) = made.Value;
                return new RawList(Shape.Planned)
                {
                    _pSubs = subs, _pSub = si, _plan = plan, _pList = list, _pInner = inner, _pPerSub = repeats,
                    _pSlots = slots, _pZero = zero, _cols = readers,
                    _pSigs = subs.Select(x => Sig(x.Sig)).Distinct().ToArray(),
                };
            }
        }
        return null;
    }

    /// <summary>The plan's list at <paramref name="path"/> inside list <paramref name="parent"/> (-1: none), or -1.</summary>
    static int Find(LayoutPlan plan, string path, int parent)
    {
        for (int i = 0; i < plan.Lists.Count; i++) if (plan.Lists[i].Path == path && plan.Lists[i].Parent == parent) return i;
        return -1;
    }

    /// <summary>Each named column's slots in the plan (an entry's values at <paramref name="entry"/>, of list
    /// <paramref name="list"/>, -1 for a subrecord's own values) and its reader; null when one isn't laid out so.</summary>
    static (int[][], byte[][], RawColumn[])? Columns(ViewSpec v, IReadOnlyList<string> columns, RecordSchema schema, string type, LayoutPlan plan, string entry, int list)
    {
        var slots = new int[columns.Count][];
        var zero = new byte[columns.Count][];
        var readers = new RawColumn[columns.Count];
        var levels = v.Lists.SelectMany(l => l.Path.Split('.')).ToList();
        for (int i = 0; i < columns.Count; i++)
        {
            if (!v.ByName.TryGetValue(columns[i], out var c) || c.RelPath is not { } rel || c.Kind is "ObjectType" or "Array2DRow" or "Json" or "String" or "AssetLink" || c.ShapeVaries) return null;
            if (c.VariantGuards.Count > 0 || c.NullGuard != null || !c.FromBase) return null;
            string path = rel.Length == 0 ? entry : entry.Length == 0 ? rel : entry + "." + rel;
            var found = Enumerable.Range(0, plan.Slots.Count).Where(k => plan.Slots[k].Path == path && plan.Slots[k].List == list && plan.Slots[k].Kind is not ("Object" or "Kind" or "Null" or "Const")).ToArray();
            if (found.Length == 0) return null;
            var first = plan.Slots[found[0]];
            if (found.Any(k => plan.Slots[k].Kind != first.Kind || plan.Slots[k].Size != first.Size) || first.Encoding != null || first.Size <= 0) return null;
            if ((c.Row ?? c.FirstRow)?.Def is not { } def) return null;
            Type? et = FlagsRaw.IsEnumKind(c.Kind) ? ClrTypeAt(type, levels.Concat(rel.Length == 0 ? Array.Empty<string>() : rel.Split('.'))) : null;
            RawColumn? rc = first.Kind == "FormID" ? (c.Kind == "FormLink" ? RawColumn.Entry(c, def, 0, 4, null) : null)
                : first.Kind == "Bytes" ? (c.Kind == "ByteArray" ? RawColumn.Entry(c, def, 0, first.Size, null) : null)
                : KindFits(first.Kind, c.Kind) ? RawColumn.Entry(c, def, 0, first.Size, et) : null;
            if (rc == null) return null;
            slots[i] = found; zero[i] = new byte[first.Size]; readers[i] = rc;
        }
        return (slots, zero, readers);
    }

    /// <summary>The rows of one record by the plan; false when the record goes to Mutagen.</summary>
    bool ReadPlanned(ReadOnlySpan<byte> head, ReadOnlySpan<byte> data, RawPlugin plugin, List<object?[]> rows)
    {
        // The record's subrecords at its own level (components left out), as the layout walk finds the layout's Subs
        var sigs = new List<string>();
        var where = new List<(int Start, int Length)>();
        int p = 0, depth = 0; uint big = 0; bool hasBig = false, any = false;
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
                bool mine = Array.IndexOf(_pSigs, sig) >= 0;
                any |= mine;
                sigs.Add(mine ? SigText(sig) : "");
                where.Add((p, (int)len));
            }
            p += (int)len;
        }
        if (p != data.Length) return false;
        if (!any) return true;
        var at = new List<int>[_pSubs!.Count];
        for (int i = 0; i < at.Length; i++) at[i] = new List<int>();
        if (!LayoutPlan.Find(_pSubs, sigs, at)) return false;
        // A subrecord of the layout's signatures that the layout doesn't take: Mutagen decides what it is
        int taken = at.Sum(x => x.Count), held = sigs.Count(x => x.Length > 0);
        if (taken != held) return false;
        int formVersion = BinaryPrimitives.ReadUInt16LittleEndian(head[20..]);
        var placedOf = t_placed ??= new Dictionary<LayoutPlan, LayoutPlan.Placed>(ReferenceEqualityComparer.Instance);
        if (!placedOf.TryGetValue(_plan!, out var r)) placedOf[_plan!] = r = _plan!.NewPlaced();
        long outer = 0;
        foreach (int s in at[_pSub])
        {
            var c = data.Slice(where[s].Start, where[s].Length);
            if (!_plan!.Run(c, formVersion, r)) return false;
            if (_pPerSub && _pList < 0)
            {
                // Each subrecord is an entry: its values at their places
                if (!Row(c, r, -1, 0, new[] { outer }, plugin, rows)) return false;
            }
            else if (_pInner >= 0)
            {
                // A list in the entries of a list that has at most one entry (a navmesh's grid arrays)
                for (int k = 0; k < r.Count[_pList]; k++)
                    for (int j = 0; j < r.Count[_pInner]; j++)
                        if (!Row(c, r, _pInner, j, new[] { (long)k, j }, plugin, rows)) return false;
            }
            else
            {
                int list = _pList;
                for (int k = 0; k < r.Count[list]; k++)
                    if (!Row(c, r, list, k, _pPerSub ? new[] { outer, k } : new long[] { k }, plugin, rows)) return false;
            }
            outer++;
        }
        return true;
    }

    static string SigText(uint sig) => System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(sig));

    /// <summary>One entry's row: its place, then each column read at its slot's place in entry <paramref name="k"/> of
    /// list <paramref name="list"/> (-1: the subrecord's own values).</summary>
    bool Row(ReadOnlySpan<byte> c, LayoutPlan.Placed r, int list, int k, long[] place, RawPlugin plugin, List<object?[]> rows)
    {
        var row = new object?[place.Length + _cols.Length];
        for (int i = 0; i < place.Length; i++) row[i] = place[i];
        for (int i = 0; i < _cols.Length; i++)
        {
            int at = -1, size = _pZero[i].Length;
            foreach (int s in _pSlots[i])
            {
                at = list < 0 ? r.At[s] : r.AtEntry(s, list, k);
                if (at >= 0) break;
            }
            // No bytes in this record (a Versioning below its form version): Mutagen's default, its zero bytes
            var b = at < 0 ? _pZero[i] : c.Slice(at, size);
            if (at >= 0 && at + size > c.Length) return false;
            if (_cols[i].Read(b, true, plugin, out row[place.Length + i]) != RawColumn.Outcome.Value) return false;
        }
        rows.Add(row);
        return true;
    }
}
