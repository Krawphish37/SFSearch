// value-positions: SFSearch's byte value walk (LinkByteWalker.WalkValues, read by the list views of SFSearch.Query's
// RawWalkLists.cs) held against Mutagen's value positions (C60), record by record (GAPS S164, S165).
//
// The value walk reads a record as Mutagen's generated reader does, and what Mutagen reads with hand-written code from
// the layouts of its package's HandReadLayouts.xml (an object template's OBTS, a volumes component's VLMS, a pattern
// swap's DAT2, a patrol's topics, VMAD) and the runs a type's layout cuts (a quest's aliases, a perk's ranks and effects,
// a scene's actions). Every value it places must be one Mutagen's own reader noted at the same path (list entries by
// their indexes), in the same subrecord, at the same offset and of the same length (a value Mutagen converts, at the same
// offset); and every value Mutagen noted must be one the walk placed, or under a part the walk says it didn't read
// (Mutagen's hand-written code no layout describes, a condition, NVNM …). Records of the types whose values a hand-read
// layout lays out or whose layout cuts runs, from Starfield.esm, kgcdoom.esm, sfta01.esm and darkstar.esm.
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins.Records;
using SFSearch.Index;
using SFSearch.Records;
using SFSearch.SchemaGen;

namespace SFSearch.Records.Tests;

static partial class ValuePositions
{
    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex Index();

    sealed class Sink : IRecordValueSink
    {
        public readonly Dictionary<string, (int Sub, int At, int End, bool Laid)> Values = new(StringComparer.Ordinal);
        public readonly List<string> Unread = new();
        public void Object(string path, string kind) { }
        public void Value(string path, FieldDef field, int subrecord, int at, int end) => Values[path] = (subrecord, at, end, false);
        public void LaidOut(string path, FieldDef field, int subrecord, int at, int end, string layoutKind) => Values[path] = (subrecord, at, end, true);
        void IRecordValueSink.Unread(string path, string why) => Unread.Add(path);
    }

    static bool Under(string path, string over) => over.Length == 0 || path == over || path.StartsWith(over + ".", StringComparison.Ordinal) || path.StartsWith(over + "[", StringComparison.Ordinal);

    public static string Run(Ctx ctx)
    {
        var e = ctx.Engine;
        var schema = e.Schema;
        var m = schema.Model;
        var problems = new ConcurrentBag<string>();
        long records = 0, values = 0, laid = 0, mutagen = 0, unreadByWalk = 0, unnoted = 0;
        var perType = new ConcurrentDictionary<string, (long Values, long Laid)>(StringComparer.Ordinal);
        // The types a hand-read layout of their own lays out or cuts, and those with a component or VMAD layout's values
        var sigs = m.HandLayouts.Where(h => h.Kind == "Record" && h.Sig is not ("TES4" or "CELL" or "NAVI")).Select(h => h.Sig!).ToHashSet(StringComparer.Ordinal);
        sigs.UnionWith(new[] { "REFR", "SFTR", "STAT", "ACTI", "CONT", "FURN", "MISC", "INFO", "DOOR" });
        foreach (var plugin in new[] { "Starfield.esm", "kgcdoom.esm", "sfta01.esm", "darkstar.esm" })
        {
            e.Sessions.Use(plugin);
            var p = ctx.DataIndex.Plugin(plugin);
            if (p?.Segment is not { } seg) { problems.Add($"{plugin}: not in the Data folder's index"); continue; }
            var session = e.Sessions.Get(plugin);
            foreach (var t in m.RecordTypes.Where(t => t.Sig != null && sigs.Contains(t.Sig)))
            {
                var fields = LinkByteWalker.FieldsOf(schema, t.Name);
                // The records that hold what the layouts lay out first (an OBTS, a VLMS, a DAT2, a PDTO, a VMAD), then others
                var entries = seg.EntriesOf(t.Sig!).Take(4000).ToList();
                Parallel.ForEach(entries.Take(t.Sig is "REFR" or "SFTR" ? 4000 : 400), new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount / 2 }, () => new LinkByteWalker(schema), (entry, _, walker) =>
                {
                    var raw = e.Raw(new RecordRef(p.Id, entry));
                    if (raw.Data.Length > 4_000_000) return walker;
                    // A placed object's or tree's own fields hold nothing laid out unless it has one of these
                    if (t.Sig is "REFR" or "SFTR" or "STAT" or "ACTI" or "CONT" or "FURN" or "MISC" or "INFO" or "DOOR" && !raw.Subrecords.Any(s => s.Signature is "VLMS" or "DAT2" or "PDTO" or "VMAD" or "XPRD")) return walker;
                    var sink = new Sink();
                    walker.WalkValues(fields, raw, session.Localized, sink, id => null);
                    RecordValuePositions pos;
                    try { pos = session.Positions(raw); }
                    catch (RecordException x) { problems.Add($"{plugin} {t.Sig} {raw.FormId:X8}: {x.Message}"); return walker; }
                    var byPath = new Dictionary<string, List<ValuePosition>>(StringComparer.Ordinal);
                    foreach (var v in pos.Positions)
                        if (v.Kind == ValuePositionKind.Value && v.SubrecordIndex >= 0)
                            (byPath.TryGetValue(v.Path, out var l) ? l : byPath[v.Path] = new()).Add(v);
                    string Where(int sub, int at, int end) => $"#{sub + 1} {raw.Subrecords[sub].Signature} {at}-{end}";
                    long n = 0, nl = 0;
                    // Every value the walk placed is where Mutagen read it
                    foreach (var (path, w) in sink.Values)
                    {
                        if (w.End == w.At) continue; // a yes/no that is its subrecord's presence: no bytes
                        // Under a part the walk says it didn't read: no list view takes it (RawWalkLists sends the record to Mutagen)
                        if (sink.Unread.Any(x => Under(path, x))) continue;
                        n++; if (w.Laid) nl++;
                        // An entry of a list whose entries Mutagen's positions don't note (a weather's NAM4, an array of 32 floats
                        // noted as one List: GAPS S186): inside the list's bytes, and equal to Mutagen's entry on its object
                        if (!byPath.ContainsKey(path) && path.EndsWith(']') && path.LastIndexOf('[') is int lb and > 0
                            && pos.Positions.Any(v => v.Path == path[..lb] && v.SubrecordIndex == w.Sub && v.Offset <= w.At && w.End <= v.Offset + v.Length))
                        {
                            Interlocked.Increment(ref unnoted);
                            if (!SameAsObject(pos.Record, path, raw.Content(raw.Subrecords[w.Sub])[w.At..w.End]))
                                problems.Add($"{plugin} {t.Sig} {raw.FormId:X8}: the walk's {path} at {Where(w.Sub, w.At, w.End)} isn't Mutagen's entry there");
                            continue;
                        }
                        if (!byPath.TryGetValue(path, out var at))
                        {
                            var there = pos.Positions.Where(v => v.SubrecordIndex == w.Sub).Select(v => $"{v.Path} {v.Kind} {v.Offset}+{v.Length}").Take(4).ToList();
                            problems.Add($"{plugin} {t.Sig} {raw.FormId:X8}: the walk's {path} at {Where(w.Sub, w.At, w.End)}; Mutagen noted no value there{(there.Count > 0 ? " (in that subrecord: " + string.Join(", ", there) + ")" : "")}");
                            continue;
                        }
                        if (!at.Any(v => v.SubrecordIndex == w.Sub && v.Offset == w.At && (v.Converted || v.Length == w.End - w.At)))
                            problems.Add($"{plugin} {t.Sig} {raw.FormId:X8}: the walk's {path} at {Where(w.Sub, w.At, w.End)}; Mutagen read it at {string.Join(", ", at.Select(v => Where(v.SubrecordIndex, v.Offset, v.Offset + v.Length) + (v.Converted ? " (converted)" : "")))}");
                    }
                    // Every value Mutagen read is one the walk placed, or under a part it says it didn't read
                    long u = 0;
                    foreach (var (path, at) in byPath)
                    {
                        if (sink.Values.ContainsKey(path)) continue;
                        if (sink.Unread.Any(x => Under(path, x))) { u++; continue; }
                        // A value at a place the walk doesn't reach for this type (the list views read those through Mutagen)
                        if (!Reached(schema, t.Name, path)) { u++; continue; }
                        problems.Add($"{plugin} {t.Sig} {raw.FormId:X8}: Mutagen read {path} at {string.Join(", ", at.Select(v => Where(v.SubrecordIndex, v.Offset, v.Offset + v.Length)))}; the walk gave nothing there");
                    }
                    Interlocked.Add(ref values, n); Interlocked.Add(ref laid, nl); Interlocked.Add(ref mutagen, byPath.Count); Interlocked.Add(ref unreadByWalk, u);
                    Interlocked.Increment(ref records);
                    perType.AddOrUpdate(t.Sig!, (n, nl), (_, o) => (o.Values + n, o.Laid + nl));
                    return walker;
                }, _ => { });
            }
        }
        var list = problems.OrderBy(x => x, StringComparer.Ordinal).ToList();
        ctx.WriteReport("value-positions.txt", list.Prepend($"{list.Count} differences").Concat(perType.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}: {k.Value.Values} values, {k.Value.Laid} from hand-read layouts")));
        Ctx.Check(list.Count == 0, $"{list.Count} differences between the byte value walk and Mutagen's value positions: {string.Join(" | ", list.Take(20))}");
        return $"{records:N0} records of {perType.Count} types (Starfield.esm, kgcdoom.esm, sfta01.esm, darkstar.esm): {values:N0} values the walk placed ({laid:N0} from Mutagen's hand-read layouts), each where Mutagen's reader read it; of Mutagen's {mutagen:N0} values, every one the walk places was placed, {unreadByWalk:N0} under parts it says it doesn't read; {unnoted:N0} entries of lists whose entries Mutagen's positions don't note (S186) equal to Mutagen's object";
    }

    /// <summary>Whether Mutagen's object holds, at a path of list entries, the number the bytes store (a float by its bits).</summary>
    static bool SameAsObject(object root, string path, ReadOnlySpan<byte> bytes)
    {
        object? o = root;
        foreach (Match m in Regex.Matches(path, @"([^.\[\]]+)|\[([^\]]*)\]"))
        {
            if (o == null) return false;
            if (m.Groups[1].Success) o = o.GetType().GetProperty(m.Groups[1].Value)?.GetValue(o);
            else if (o is System.Collections.IEnumerable list && int.TryParse(m.Groups[2].Value, out int k)) o = list.Cast<object?>().ElementAtOrDefault(k);
            else return false;
        }
        return o switch
        {
            float f => bytes.Length == 4 && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes) == BitConverter.SingleToInt32Bits(f),
            int i => bytes.Length == 4 && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes) == i,
            uint u => bytes.Length == 4 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes) == u,
            short sh => bytes.Length == 2 && System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes) == sh,
            ushort us => bytes.Length == 2 && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes) == us,
            byte b => bytes.Length == 1 && bytes[0] == b,
            _ => false,
        };
    }

    static readonly ConcurrentDictionary<(RecordSchema, string), HashSet<string>> ReachedPaths = new();

    /// <summary>Whether a value walk of the type reaches a path of Mutagen's (indexes as [*], kinds left out).</summary>
    static bool Reached(RecordSchema schema, string type, string path)
    {
        var set = ReachedPaths.GetOrAdd((schema, type), k => LinkByteWalker.ValueReach(k.Item1, k.Item2).Where(x => x.Value == null).Select(x => LinkByteWalker.Unmarked(x.Key)).ToHashSet(StringComparer.Ordinal));
        string plain = Index().Replace(path, "[*]");
        return set.Contains(plain);
    }
}
