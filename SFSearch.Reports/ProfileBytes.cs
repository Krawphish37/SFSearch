// profile <Type.Path> of link fields from the files' bytes (GAPS S92): when every field asked is a link field whose
// place the bytes give (a fixed place, or the byte walk's, LinkWalk.cs), the records are stepped through on all cores
// with no Mutagen read, and the answer is the one Mutagen's walk gives: each plugin's records and values, the null ones,
// the distinct values, the census of what the field points at. Only the samples, a dozen records a field, are read
// through Mutagen, for the values beside the field (the same records Mutagen's walk would pick: their marks are worked
// out from the bytes the same way).
//
// A value is what Mutagen's object has: a list's items as the file holds them; a field outside any list once a record
// (null when its subrecord isn't there) or, inside an object that may be absent, once a record that has the object's
// subrecord. A field whose object's presence the bytes don't give that simply is left to Mutagen's walk (the whole
// question then is).
using System.Buffers.Binary;
using Mutagen.Bethesda.Plugins;
using SFSearch.Index;
using SFSearch.Records;
using SFSearch.SchemaGen;

namespace SFSearch.Reports;

public sealed partial class ProfileReport
{
    /// <summary>How one field asked is read from the bytes.</summary>
    internal sealed class ByteField
    {
        public required ProfileTarget Target;
        /// <summary>A fixed place (null: the byte walk's field <see cref="Walked"/>).</summary>
        public LinkPlace? Exact;
        public int Walked = -1;
        /// <summary>The field is a list's item: a value for each item the file holds.</summary>
        public bool List;
        /// <summary>Outside any list and any object that may be absent: a value for every record.</summary>
        public bool EveryRecord;
    }

    /// <summary>The fields asked, each read from the bytes; null when one of them isn't a link field the bytes give
    /// whole (then Mutagen's walk reads them all).</summary>
    internal List<ByteField>? ByteRoute(IReadOnlyList<ProfileTarget> targets)
    {
        if (targets.Count == 0) return null;
        var model = session.Schema.Model;
        var route = new List<ByteField>();
        foreach (var t in targets)
        {
            var def = t.Row.Def;
            if (t.Census || t.Part != null || !(def.Kind is "FormLink" or "FormLinkOrIndex" || def.LinkTo != null && def.Kind is not ("List" or "Object"))) return null;
            var plan = LinkPlan.Of(session.Schema, t.Type);
            var place = plan.Places.FirstOrDefault(p => p.Path == t.Path);
            if (place == null) return null;
            bool list = t.Path.Contains("[*]", StringComparison.Ordinal);
            // The objects on the way: may one of them be absent?
            bool absentable = false;
            for (var up = t.Row.Parent; up != null; up = up.Parent) if (up.Def.Nullable) absentable = true;
            var bf = new ByteField { Target = t, List = list, EveryRecord = !list && !absentable };
            if (!place.Walked)
            {
                // A fixed place: an object that may be absent is there when its subrecord is (an object in the field's own subrecord)
                if (!list && absentable)
                    for (var up = t.Row.Parent; up != null; up = up.Parent)
                        if (up.Def.Nullable && up.Subrecord != place.Subrecord) return null;
                bf.Exact = place;
            }
            else
            {
                // The walk gives what the file holds: a list's items, or a field Mutagen always writes where its object is
                if (!list && def.Nullable) return null;
                bf.Walked = plan.Fields!.IndexOf(t.Path);
                if (bf.Walked < 0) return null;
            }
            route.Add(bf);
        }
        return route;
    }

    /// <summary>One record's values of the fields asked, in their order (raw FormIDs; 0 a null link).</summary>
    /// <summary>Each field's smallest sample marks of the record (its values that aren't null, and its null ones; 0: none),
    /// worked out on the reading threads.</summary>
    readonly record struct RecordValues(int Entry, List<uint>?[] Values, ulong[] MarkNonZero, ulong[] MarkZero);

    /// <summary>One plugin's counts of one field, as Mutagen's walk adds them up; values counted by the FormID as stored
    /// (in the order first met) and made FormKeys once at the end.</summary>
    sealed class ByteAcc
    {
        public long Records, RecordsNonZero, Values, NonZero, NullLinks;
        public readonly Dictionary<uint, long> Raw = new();
    }

    /// <summary>Reads the fields of <paramref name="route"/> from the bytes of every plugin of the plan into the fields'
    /// stats, then the samples' records through Mutagen.</summary>
    void RunBytes(ProfilePlan plan, ProfileAnswer answer, List<FieldStats> fields, List<ByteField> route, Walker walker, ReadScope scope)
    {
        var index = session.Index;
        var engine = session.Engine;
        var sigs = plan.Targets.Select(t => t.Signature).ToHashSet(StringComparer.Ordinal);
        using var walkers = new ThreadLocal<LinkByteWalker>(() => new LinkByteWalker(session.Schema));
        var seeds = plan.Targets.Select(t => Walker.Seed(t.Name)).ToArray();
        // Each field's sample candidates: (mark, plugin, entry), the smallest marks, one a record
        var lostIn = new long[route.Count];
        var picks = fields.Select(_ => (NonZero: new List<(ulong Mark, int Plugin, int Entry)>(), Zero: new List<(ulong Mark, int Plugin, int Entry)>())).ToArray();
        foreach (var p in plan.Set.Plugins)
        {
            if (p.Segment is not { } seg)
            {
                answer.Notes.Add($"{p.Name} can't be read ({p.Problems.ReadError ?? "it has no header index"}): left out.");
                continue;
            }
            ReportSession.AddSource(answer, p);
            ReportSession.AddProblemNotes(answer, p);
            answer.PluginsRead++;
            var entries = new List<int>();
            foreach (var s in sigs) entries.AddRange(seg.EntriesOf(s));
            if (plan.Set.WinnersOnly) entries.RemoveAll(e => !index.IsWinner(new RecordRef(p.Id, e)));
            if (entries.Count == 0) continue;
            scope.TouchBytes(p);
            // In the file's order, as Mutagen's walk reads them
            var arr = entries.ToArray();
            Array.Sort(arr.Select(e => seg[e].Offset).ToArray(), arr);
            bool localized = p.Header is { } h && (h.Flags & 0x80) != 0;
            var sigOf = TypeStatsReport.SigOf(index, p);
            var types = route.Select(r => r.Target.Signature).ToArray();
            var accs = route.Select(_ => new ByteAcc()).ToArray();
            var keys = new Dictionary<uint, (FormKey Key, string Text)?>();
            (FormKey Key, string Text)? KeyOf(uint raw)
            {
                lock (keys)
                {
                    if (keys.TryGetValue(raw, out var k)) return k;
                    k = p.FormIds.TryResolve(raw, out string owner, out uint local) ? (new FormKey(ModKey.FromFileName(owner), local), "") : null;
                    if (k is { } kk) k = (kk.Key, kk.Key.ToString());
                    return keys[raw] = k;
                }
            }
            // A FormID that names no record in the plugin's numbering is a null link (as one of 0)
            var nulls = new System.Collections.Concurrent.ConcurrentDictionary<uint, bool>();
            bool IsNull(uint raw) => raw is 0 or uint.MaxValue || nulls.GetOrAdd(raw, r => KeyOf(r) == null);
            foreach (var rv in RecordPipeline.Read<RecordValues>(new[] { new ReadSet { Plugin = p, Entries = arr } }, (in PipelineRecord rec, List<RecordValues> output) =>
            {
                if (rec.Problem != PipelineProblem.None) return;
                string sig = rec.Plugin.Segment!.SignatureOf(rec.Header);
                var values = ValuesOf(route, sig, rec.Head, rec.Data, localized, sigOf, walkers, lostIn);
                var mnz = new ulong[route.Count]; var mz = new ulong[route.Count];
                string? formKey = null;
                for (int i = 0; i < route.Count; i++)
                {
                    if (values[i] is not { Count: > 0 } list) continue;
                    formKey ??= index.FormKeyOf(new RecordRef(rec.Plugin.Id, rec.EntryIndex));
                    ulong a = ulong.MaxValue, b = ulong.MaxValue;
                    for (int instance = 0; instance < list.Count; instance++)
                    {
                        ulong mark = Walker.Mark(rec.Plugin.Name, formKey, seeds[i], instance);
                        if (IsNull(list[instance])) { if (mark < b) b = mark; } else if (mark < a) a = mark;
                    }
                    mnz[i] = a; mz[i] = b;
                }
                output.Add(new RecordValues(rec.EntryIndex, values, mnz, mz));
            }))
            {
                answer.RecordsRead++;
                for (int i = 0; i < route.Count; i++)
                {
                    var values = rv.Values[i];
                    if (values == null || values.Count == 0) continue;
                    var acc = accs[i];
                    acc.Records++;
                    bool anyNonZero = false;
                    foreach (uint raw in values)
                    {
                        acc.Values++;
                        if (IsNull(raw)) acc.NullLinks++;
                        else { acc.NonZero++; anyNonZero = true; }
                        acc.Raw[raw] = acc.Raw.GetValueOrDefault(raw) + 1;
                    }
                    if (anyNonZero) acc.RecordsNonZero++;
                    // The route keeps the plan's order of fields: field i is target i
                    if (rv.MarkNonZero[i] != ulong.MaxValue) Pick(picks[i].NonZero, KeptSamples, rv.MarkNonZero[i], p.Id, rv.Entry);
                    if (rv.MarkZero[i] != ulong.MaxValue) Pick(picks[i].Zero, KeptSamples / 3, rv.MarkZero[i], p.Id, rv.Entry);
                }
            }
            // Added up as Mutagen's walk adds a plugin's batches
            for (int i = 0; i < route.Count; i++)
            {
                var acc = accs[i];
                var fs = fields[i];
                if (acc.Values == 0) continue;
                var ps = fs.Plugin(p.Name);
                ps.Records += acc.Records; ps.RecordsNonZero += acc.RecordsNonZero; ps.Values += acc.Values; ps.NonZero += acc.NonZero;
                fs.Values += acc.Values; fs.NonZero += acc.NonZero;
                // The values as Mutagen writes them, each first met where its first FormID was
                var counts = new Dictionary<string, long>(StringComparer.Ordinal);
                var targets = new Dictionary<FormKey, long>();
                foreach (var (raw, n) in acc.Raw)
                {
                    var key = IsNull(raw) ? null : KeyOf(raw);
                    string repr = key == null ? "null" : key.Value.Text;
                    counts[repr] = counts.GetValueOrDefault(repr) + n;
                    if (key != null) targets[key.Value.Key] = targets.GetValueOrDefault(key.Value.Key) + n;
                }
                foreach (var (repr, c) in counts)
                {
                    if (fs.Counts.TryGetValue(repr, out var have)) fs.Counts[repr] = have + c;
                    else if (fs.Counts.Count < MaxDistinct) fs.Counts[repr] = c;
                    else fs.OtherValues += c;
                }
                fs.NullLinks += acc.NullLinks;
                fs.Targets ??= new();
                foreach (var (k, n) in targets) fs.Targets[k] = fs.Targets.GetValueOrDefault(k) + n;
            }
        }
        // The samples' records, read through Mutagen for the values beside the field
        var sampled = picks.SelectMany(x => x.NonZero.Concat(x.Zero)).Select(x => (x.Plugin, x.Entry)).Distinct().GroupBy(x => x.Plugin);
        int read = 0;
        foreach (var g in sampled)
        {
            var p = index.Plugins[g.Key];
            scope.Touch(engine, p.Name);
            var arr = g.Select(x => x.Entry).ToArray();
            Array.Sort(arr.Select(e => p.Segment![e].Offset).ToArray(), arr);
            var res = walker.Batch(engine, index, new PluginCtx(p, plan.Set.WinnersOnly), arr, 0, arr.Length);
            read += arr.Length;
            foreach (var (i, acc) in res.Fields)
            {
                // Only the records the bytes picked for this field
                var mine = picks[i].NonZero.Concat(picks[i].Zero).Where(x => x.Plugin == g.Key).Select(x => index.FormKeyOf(new RecordRef(x.Plugin, x.Entry))).ToHashSet(StringComparer.Ordinal);
                fields[i].Offer(acc.Samples.Where(s => mine.Contains(s.FormKey)).ToList());
                fields[i].Offer(acc.SamplesZero.Where(s => mine.Contains(s.FormKey)).ToList());
            }
            foreach (var (why, n) in res.Skipped) answer.Skipped[why] = answer.Skipped.GetValueOrDefault(why) + n;
        }
        for (int i = 0; i < route.Count; i++)
            if (lostIn[i] > 0) answer.Notes.Add($"{route[i].Target.Name}: in {ReportAnswer.Plural(lostIn[i], "record")} the byte walk couldn't follow the bytes to this field, so its values there aren't counted.");
        answer.ReadFromBytes = $"from the files' bytes on {PipelineOptions.DefaultThreads} threads (the link fields' places, GAPS S92); {ReportAnswer.Plural(read, "sample record")} read through Mutagen for the values beside the field";
    }

    static void Pick(List<(ulong Mark, int Plugin, int Entry)> kept, int cap, ulong mark, int plugin, int entry)
    {
        if (kept.Count >= cap)
        {
            int worst = 0;
            for (int i = 1; i < kept.Count; i++) if (kept[i].Mark > kept[worst].Mark) worst = i;
            if (kept[worst].Mark <= mark) return;
            kept.RemoveAt(worst);
        }
        kept.Add((mark, plugin, entry));
    }

    /// <summary>One record's values of each field (null: none in this record), from its bytes.</summary>
    static List<uint>?[] ValuesOf(List<ByteField> route, string sig, ReadOnlySpan<byte> head, ReadOnlySpan<byte> body, bool localized, Func<uint, string?> sigOf, ThreadLocal<LinkByteWalker> walkers, long[] lostIn)
    {
        var values = new List<uint>?[route.Count];
        // Fixed places: the record's subrecords outside components
        var last = new uint?[route.Count];
        bool anyExact = false, anyWalk = false;
        for (int f = 0; f < route.Count; f++) { if (route[f].Target.Signature != sig) continue; if (route[f].Exact != null) anyExact = true; else anyWalk = true; }
        if (anyExact)
        {
            int p = 0, component = 0; uint? big = null;
            while (p + 6 <= body.Length)
            {
                uint s = BinaryPrimitives.ReadUInt32LittleEndian(body[p..]);
                int n = BinaryPrimitives.ReadUInt16LittleEndian(body[(p + 4)..]);
                p += 6;
                if (s == 0x58585858 /* XXXX */ && n == 4 && p + 4 <= body.Length) { big = BinaryPrimitives.ReadUInt32LittleEndian(body[p..]); p += 4; continue; }
                long len = big ?? (uint)n; big = null;
                var data = body.Slice(p, (int)Math.Min(len, body.Length - p));
                p = (int)Math.Min(body.Length, p + len);
                if (s == 0x42434642 /* BFCB */) { component++; continue; }
                if (s == 0x45434642 /* BFCE */) { if (component > 0) component--; continue; }
                if (component > 0) continue;
                for (int f = 0; f < route.Count; f++)
                {
                    var pl = route[f].Exact;
                    if (pl == null || pl.Sig != s || route[f].Target.Signature != sig) continue;
                    if (pl.Packed)
                    {
                        var list = values[f] ??= new List<uint>();
                        for (int q = pl.Start + pl.Offset; q + 4 <= data.Length && q - pl.Offset + pl.Stride <= data.Length; q += pl.Stride) list.Add(BinaryPrimitives.ReadUInt32LittleEndian(data[q..]));
                    }
                    else
                    {
                        // Past the end of a short subrecord Mutagen keeps the field's default: a null link
                        uint v = pl.Offset + 4 <= data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(data[pl.Offset..]) : 0;
                        if (route[f].List) (values[f] ??= new List<uint>()).Add(v);
                        else last[f] = v; // a field Mutagen reads again takes the last one
                    }
                }
            }
            for (int f = 0; f < route.Count; f++)
            {
                if (route[f].Exact == null || route[f].List || route[f].Target.Signature != sig) continue;
                if (last[f] is uint v) values[f] = new List<uint> { v };
                else if (route[f].EveryRecord) values[f] = new List<uint> { 0 };
            }
        }
        if (anyWalk)
        {
            var plan = LinkPlan.Of(walkers.Value!.Schema, route.First(r => r.Target.Signature == sig && r.Exact == null).Target.Type);
            // Only records that hold one of the walk's subrecords hold a walked field
            bool walk = false;
            for (int p = 0; p + 6 <= body.Length && !walk;)
            {
                uint s = BinaryPrimitives.ReadUInt32LittleEndian(body[p..]);
                long n = BinaryPrimitives.ReadUInt16LittleEndian(body[(p + 4)..]);
                if (s == 0x58585858 && n == 4 && p + 10 <= body.Length) { n = BinaryPrimitives.ReadUInt32LittleEndian(body[(p + 6)..]); p += 10; s = p + 4 <= body.Length ? BinaryPrimitives.ReadUInt32LittleEndian(body[p..]) : 0; }
                walk = plan.Triggers.Contains(s);
                p = (int)Math.Min(int.MaxValue, p + 6 + n);
            }
            if (walk)
            {
                var walker = walkers.Value!;
                foreach (var l in walker.Walk(plan.Fields!, RawRecord.FromInflated(head, body), localized, sigOf))
                    for (int f = 0; f < route.Count; f++)
                        if (route[f].Walked == l.Field && route[f].Target.Signature == sig) (values[f] ??= new List<uint>()).Add(l.Raw);
                // A part of the record the walk couldn't follow: the field's values there are missing
                foreach (var lost in walker.ProblemPaths)
                    for (int f = 0; f < route.Count; f++)
                        if (route[f].Exact == null && LinkByteWalker.Unmarked(route[f].Target.Path) is var u && (u == lost || u.StartsWith(lost + ".", StringComparison.Ordinal) || u.StartsWith(lost + "[", StringComparison.Ordinal)))
                            Interlocked.Increment(ref lostIn[f]);
            }
            for (int f = 0; f < route.Count; f++)
                if (route[f].Exact == null && !route[f].List && values[f] == null && route[f].EveryRecord && route[f].Target.Signature == sig) values[f] = new List<uint> { 0 };
        }
        return values;
    }
}
