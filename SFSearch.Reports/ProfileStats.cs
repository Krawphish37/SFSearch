// profile --subrecords "*" (GAPS S52): what a record type holds, how big, how linked, across plugins, read from the
// files' bytes on all cores. Planning the world link index (J1) needed it for Starfield.esm's world types and used a
// Python scan; a user asks it of any type ("what does a cell hold, how big are they, what do placed objects point at").
// Per record type: its records, their bytes in the file (with the 24-byte headers) and as read (inflated); every
// subrecord its records hold (records that hold it, times, bytes, the largest); and every link field the schema places
// exactly in the bytes, its links that aren't null, the distinct (record, target) pairs, the distinct targets and the
// records that have one. A link field whose place depends on data of varying length or on Mutagen's own code (a
// navmesh's edges, a cell's traversals) isn't guessed at: the answer names it, with the profile call that counts it
// through Mutagen.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using SFSearch.Index;
using SFSearch.Records;
using SFSearch.SchemaGen;

namespace SFSearch.Reports;

/// <summary>A link field the bytes place exactly: in which subrecord, at which offset; for a list of entries packed in
/// one subrecord, where the entries start (after their count) and how long each is. Or (<see cref="By"/> set) a field
/// the byte walk reads (LinkWalk.cs: after data of varying length, a hand-read layout, a bulky subrecord's reader).</summary>
public sealed record LinkPlace(string Path, string Subrecord, int Offset, int Start = 0, int Stride = 0, string? By = null)
{
    /// <summary>The subrecord, as its four bytes read little-endian (0 when the field has none of its own).</summary>
    public uint Sig { get; } = Subrecord.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(Subrecord)) : 0;
    /// <summary>Entries packed in one subrecord, each <see cref="Stride"/> bytes.</summary>
    public bool Packed => Stride > 0;
    /// <summary>Read by the byte walk, not at a fixed place.</summary>
    public bool Walked => By != null;

    public string Where => Walked ? $"{(Subrecord.Length > 0 ? Subrecord + ", " : "")}walked: {By}" : Packed ? $"{Subrecord} ({Stride} bytes an entry{(Offset > 0 ? $", @{Offset} in it" : "")})" : Offset > 0 ? $"{Subrecord} @{Offset}" : Subrecord;
}

/// <summary>A record type's link fields as profile --subrecords "*" counts them: those at fixed places, read by the
/// stepper; those the byte walk reads (each with its LinkFields index), read when a record holds one of the walk's
/// subrecords; and those neither reads, with why.</summary>
public sealed class LinkPlan
{
    /// <summary>The fixed places first (<see cref="Exact"/> of them), then the walked fields: the index is the field's.</summary>
    public required LinkPlace[] Places { get; init; }
    public required int Exact { get; init; }
    public LinkFields? Fields { get; init; }
    /// <summary>A LinkFields index to a <see cref="Places"/> index, or -1 (a fixed place's, or not counted).</summary>
    public int[] FromWalk { get; init; } = Array.Empty<int>();
    /// <summary>The walk's subrecords, as their four bytes read little-endian.</summary>
    public HashSet<uint> Triggers { get; init; } = new();
    public List<(string Path, string? Subrecord, string Why)> Left { get; } = new();
    public bool Walks => Places.Length > Exact;

    static readonly ConcurrentDictionary<(RecordSchema, string), LinkPlan> Cache = new();

    public static LinkPlan Of(RecordSchema schema, string typeName) => Cache.GetOrAdd((schema, typeName), k =>
    {
        var lp = LinkPlaces.Of(k.Item1, k.Item2);
        var fields = LinkByteWalker.FieldsOf(k.Item1, k.Item2);
        var places = new List<LinkPlace>(lp.Placed);
        var fromWalk = Enumerable.Repeat(-1, fields.Paths.Count).ToArray();
        var left = new List<(string, string?, string)>();
        foreach (var (path, sub, why) in lp.Left)
        {
            int i = fields.IndexOf(path);
            if (i >= 0 && fields.Left[i] == null) { fromWalk[i] = places.Count; places.Add(new LinkPlace(path, sub ?? "", 0, By: fields.By[i])); }
            else left.Add((path, sub, i >= 0 && fields.Left[i] is { } w ? w : why));
        }
        var plan = new LinkPlan
        {
            Places = places.ToArray(), Exact = lp.Placed.Count, Fields = fields, FromWalk = fromWalk,
            Triggers = Enumerable.Range(0, fields.Paths.Count).Where(i => fromWalk[i] >= 0).SelectMany(i => fields.TriggersOf[i]).Distinct().Where(t => t.Length == 4).Select(t => BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(t))).ToHashSet(),
        };
        plan.Left.AddRange(left);
        return plan;
    });

    /// <summary>One link found in a record's bytes: its field (an index into <see cref="Places"/>), the FormID as stored
    /// (0: a null link), its subrecord's signature, and the offset in it (-1: a bulky subrecord's reader gives none).</summary>
    public delegate void Found(int place, uint raw, string subrecord, int offset);

    /// <summary>
    /// Every link of a record of this type, from its bytes: the fixed places as its subrecords are stepped through
    /// (outside components; an XXXX sizes the next subrecord), then the fields the byte walk reads (GAPS S92), when the
    /// record holds one of the walk's subrecords. profile --subrecords "*" counts them, profile --formids classifies how
    /// each is stored. Gives the walk's first problem and the paths it lost in this record, when any.
    /// </summary>
    public (string? Problem, string[]? Lost) Links(ReadOnlySpan<byte> head, ReadOnlySpan<byte> body, bool localized, Func<uint, string?> sigOf, LinkByteWalker walker, Found found)
    {
        bool walk = false;
        int p = 0, component = 0; uint? big = null;
        while (p + 6 <= body.Length)
        {
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(body[p..]);
            int n = BinaryPrimitives.ReadUInt16LittleEndian(body[(p + 4)..]);
            p += 6;
            if (sig == 0x58585858 /* XXXX */ && n == 4 && p + 4 <= body.Length) { big = BinaryPrimitives.ReadUInt32LittleEndian(body[p..]); p += 4; continue; }
            long len = big ?? (uint)n; big = null;
            var data = body.Slice(p, (int)Math.Min(len, body.Length - p));
            p = (int)Math.Min(body.Length, p + len);
            if (Walks && Triggers.Contains(sig)) walk = true;
            if (sig == 0x42434642 /* BFCB */) { component++; continue; }
            if (sig == 0x45434642 /* BFCE */) { if (component > 0) component--; continue; }
            if (component > 0) continue;
            for (int f = 0; f < Exact; f++)
            {
                var pl = Places[f];
                if (pl.Sig != sig) continue;
                if (pl.Packed)
                {
                    for (int q = pl.Start + pl.Offset; q + 4 <= data.Length; q += pl.Stride) found(f, BinaryPrimitives.ReadUInt32LittleEndian(data[q..]), pl.Subrecord, q);
                }
                else if (pl.Offset + 4 <= data.Length) found(f, BinaryPrimitives.ReadUInt32LittleEndian(data[pl.Offset..]), pl.Subrecord, pl.Offset);
            }
        }
        if (!walk) return (null, null);
        var raw = RawRecord.FromInflated(head, body);
        foreach (var l in walker.Walk(Fields!, raw, localized, sigOf))
            if (FromWalk[l.Field] is >= 0 and var f) found(f, l.Raw, l.Subrecord >= 0 ? raw.Subrecords[l.Subrecord].Signature : Places[f].Subrecord, l.Offset);
        return (walker.Problems.Count > 0 ? walker.Problems[0] : null, walker.ProblemPaths.Count > 0 ? walker.ProblemPaths.Distinct().ToArray() : null);
    }
}

/// <summary>The link fields of a record type: those read from the bytes, and those left to Mutagen (with why).</summary>
public sealed class LinkPlaces
{
    public List<LinkPlace> Placed { get; } = new();
    /// <summary>Path, subrecord, why it isn't read from the bytes.</summary>
    public List<(string Path, string? Subrecord, string Why)> Left { get; } = new();

    static readonly ConcurrentDictionary<(RecordSchema, string), LinkPlaces> Cache = new();

    public const string InComponentWhy = "in a component";

    /// <summary>
    /// The schema's link fields of a record type (records nested in it are their own type), each placed when its bytes
    /// are certain, with no hand-written read or marker on its way: (1) a field at a fixed offset of a subrecord no other
    /// field of the type uses in another place; (2) a list whose entries are packed in its own subrecord (after their
    /// count, if any), every field of an entry at a fixed offset; (3) a field at a fixed offset of a list entry's own
    /// subrecord (each entry a subrecord, as a placed object's linked references, XLKR). A component's fields (BFCB …
    /// BFCE) are the component's: they aren't read here, and their subrecords are no conflict.
    /// </summary>
    public static LinkPlaces Of(RecordSchema schema, string typeName) => Cache.GetOrAdd((schema, typeName), k => Make(k.Item1.Model, k.Item2));

    static bool IsLink(FieldDef d) => d.Kind is "FormLink" or "FormLinkOrIndex" || d.LinkTo != null && d.Kind is not ("List" or "Object");

    static LinkPlaces Make(SchemaModel model, string typeName)
    {
        var lp = new LinkPlaces();
        var rows = model.Expand(typeName).ToList();
        string[] Stack(FieldRow r) => r.Stack.Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool Nested(FieldRow r) => Stack(r).Skip(1).Any(o => model.Objects.TryGetValue(o, out var od) && od.ObjType == "Record");
        bool InComponent(FieldRow r) => r.Path.StartsWith("Components[*]", StringComparison.Ordinal) || Stack(r).Any(o => model.EndMarkerOf(o) == "BFCE");
        bool HandOrMarked(FieldDef d) => d.Custom || d.Source != null || d.Marker != null || d.EndMarker != null || d.LayoutOffset != null || d.LayoutEncoding != null || d.LayoutPresentWhen != null || d.Kind == "CustomLogic";
        bool NoBytes(FieldRow r) => r.Def.Kind is "Break" or "Marker" || r.Path.EndsWith(".Versioning", StringComparison.Ordinal) || r.Path.EndsWith(".#break", StringComparison.Ordinal);
        // A version break and the generator's Versioning hold no bytes of their own
        bool Uses(FieldRow r, string sub) => (r.Subrecord == sub || r.Def.CustomSubrecordList.Contains(sub)) && !NoBytes(r);
        // The type's own fields outside components: those a subrecord could be shared with
        var own = rows.Where(r => !Nested(r) && !InComponent(r)).ToList();
        var byPath = own.GroupBy(r => r.Path).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var parameters = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => !Nested(r)))
        {
            var def = row.Def;
            if (!IsLink(def)) continue;
            string? sub = row.Subrecord;
            string? why = null;
            LinkPlace? place = null;
            if (InComponent(row)) why = InComponentWhy;
            else if (sub is not { Length: 4 } || def.Size != 4) why = "no fixed place in a subrecord";
            else if (HandOrMarked(def)) why = "read by Mutagen's own code";
            else
            {
                for (var up = row.Parent; up != null && why == null; up = up.Parent)
                    if (HandOrMarked(up.Def) || up.Def.RefObject is { } ro && model.HandReadOf(ro) != null) why = "read by Mutagen's own code";
                int lists = row.Path.Split('[').Length - 1;
                string Shared(FieldRow r) => $"its subrecord {sub} is used in another place too ({r.Path})";
                if (why != null) { }
                else if (lists == 0)
                {
                    int offset = row.Offset ?? (def.Subrecord == sub ? 0 : -1);
                    // A gendered value's halves in one subrecord (a race's VTCK): the male value, then the female one
                    if (row.Offset == null && def.Path.EndsWith(".Female", StringComparison.Ordinal))
                        offset = model.AllFields(def.Object).Select(x => x.F).FirstOrDefault(x => x.Path == def.Path[..^".Female".Length] + ".Male") is { Size: int ms } male && male.Subrecord == def.Subrecord ? ms : -1;
                    string parent = Parent(row.PlainPath);
                    foreach (var r in own)
                    {
                        if (r == row || !Uses(r, sub)) continue;
                        if (r.Path.Contains('[') || HandOrMarked(r.Def)) { why = Shared(r); break; }
                        if (r.Def.Kind is "Object" or "Gendered") { if (!row.PlainPath.StartsWith(r.PlainPath + ".", StringComparison.Ordinal)) { why = Shared(r); break; } continue; }
                        if (Parent(r.PlainPath) != parent) { why = Shared(r); break; }
                    }
                    // No offset in the schema and other fields in the same subrecord: the fields before it take sizes of
                    // their own (a text, an optical sight marker's attach node in ANAM), so its place isn't fixed; the walk
                    // reads it (J83: Starfield.esm's AOPS read 0 for three links)
                    if (why == null && row.Offset == null && !def.Path.EndsWith(".Female", StringComparison.Ordinal) && own.Any(r => r != row && Uses(r, sub)))
                        why = "no fixed place in its subrecord: fields before it take sizes of their own";
                    if (why == null && offset >= 0) place = new LinkPlace(row.Path, sub, offset);
                }
                else if (lists == 1)
                {
                    string listPath = row.Path[..row.Path.IndexOf("[*]", StringComparison.Ordinal)];
                    string item = listPath + "[*]";
                    var list = byPath.GetValueOrDefault(listPath);
                    // Every other field in that subrecord sits in the same list
                    foreach (var r in own)
                        if (r != row && Uses(r, sub) && r.Path != listPath && r.Path != item && !r.Path.StartsWith(item + ".", StringComparison.Ordinal)) { why = Shared(r); break; }
                    if (why != null) { }
                    else if (list == null || HandOrMarked(list.Def)) why = "in a list Mutagen reads by hand";
                    else if (list.Subrecord == sub)
                    {
                        // Entries packed in the list's own subrecord, after their count (a list counted by a subrecord of its
                        // own, KSIZ before KWDA, has none in it)
                        int start = list.Def.Note?.Contains("counted by ", StringComparison.Ordinal) == true ? 0 : list.Def.CounterLength ?? 0;
                        if (row.Path == item) { if (row.Offset == null) place = new LinkPlace(row.Path, sub, 0, start, 4); }
                        else
                        {
                            var fields = own.Where(r => r.Path.StartsWith(item + ".", StringComparison.Ordinal) && !NoBytes(r)).ToList();
                            if (row.Offset is int o && fields.All(r => r.Offset != null && r.Def.Size != null && !r.Path[item.Length..].Contains('[') && !HandOrMarked(r.Def)))
                                place = new LinkPlace(row.Path, sub, o, start, fields.Max(r => r.Offset!.Value + r.Def.Size!.Value));
                            else why = "in a list of entries of varying length in one subrecord";
                        }
                    }
                    else
                    {
                        // Each entry has the subrecord of its own (XLKR), or the field is a subrecord of each entry (XCZR)
                        int offset = row.Offset ?? (def.Subrecord == sub ? 0 : -1);
                        if (offset >= 0) place = new LinkPlace(row.Path, sub, offset);
                    }
                }
                else why = "in a list inside a list";
                if (why == null && place == null) why = "no fixed place in a subrecord";
            }
            if (place != null) lp.Placed.Add(place);
            else lp.Left.Add((row.Path, sub, why!));
            // A condition's function parameters (GAPS S156) after its Data.Reference: the schema spells out no
            // ConditionData kind, and Mutagen's condition reader picks it by the function (the byte walk reads them)
            if (row.Path.EndsWith(".Data.Reference", StringComparison.Ordinal) && def.Object == "ConditionData" && parameters.Add(row.Path))
                foreach (var name in SFSearch.Records.ConditionParameters.LinkNames(model))
                    lp.Left.Add((row.Path[..^"Reference".Length] + name, "CTDA", "read by Mutagen's own code"));
        }
        return lp;
    }

    static string Parent(string path) { int dot = path.LastIndexOf('.'); return dot < 0 ? "" : path[..dot]; }
}

/// <summary>One subrecord of a record type: records that hold it, times, bytes, the largest.</summary>
public sealed class SubrecordStat
{
    public required string Signature { get; init; }
    public long Records, Times, Bytes, Largest;
}

/// <summary>One link field of a record type read from the bytes.</summary>
public sealed class LinkFieldStat
{
    public required LinkPlace Place { get; init; }
    /// <summary>Links that aren't null; distinct (record, target) pairs; records with one.</summary>
    public long Links, Rows, Records;
    internal readonly HashSet<ulong> Targets = new();
    public long DistinctTargets => Targets.Count;
}

/// <summary>What one record type holds in the plugins read.</summary>
public sealed class TypeStats
{
    public required string Sig { get; init; }
    public required string Name { get; init; }
    public long Records, FileBytes, ReadBytes, Compressed, Unreadable;
    public Dictionary<uint, SubrecordStat> Subrecords { get; } = new();
    public List<LinkFieldStat> Links { get; } = new();
    public List<(string Path, string? Subrecord, string Why)> LinksLeft { get; } = new();
    /// <summary>Records in which the byte walk couldn't follow every link field, and a few of them with why.</summary>
    public long WalkProblems;
    /// <summary>The paths below which the walk lost its way, with the records it did in.</summary>
    public Dictionary<string, long> WalkLost { get; } = new(StringComparer.Ordinal);
    public List<(int Plugin, int Entry, string Why)> WalkProblemSamples { get; } = new();
}

public sealed class TypeStatsReport(ReportSession session)
{
    /// <summary>What stepping through the records costs: 0.003 s a MB as stored and 1 µs a record (their links resolved
    /// and counted), on 8 threads. Measured on a quiet PC: Starfield.esm's cells 1.3 s, placed objects 4.1-4.5 s,
    /// navmeshes 1.1 s; the 33 official plugins, every type, 20 s while other programs held the cores.</summary>
    public (double Seconds, long MemoryMB, string What) Estimate(SubrecordsOptions o)
    {
        var (set, bytes, records) = SubrecordsReport.Size(session, o);
        return (0.5 + bytes / 1048576.0 * 0.003 + records * 1e-6, 1500, $"What every subrecord holds steps through {ReportAnswer.N(records)} records ({Sizes.Text(bytes)}) of {set.Shown}");
    }

    public TypeStatsAnswer Stats(SubrecordsOptions o) => session.Run(scope =>
    {
        session.Index.Refresh();
        var set = PluginSet.Parse(session.Index, o.In, "@official");
        string? onlyType = SubrecordsReport.TypeSig(session, o.Type);
        var model = session.Schema.Model;
        var names = model.RecordTypes.GroupBy(r => r.Sig!).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        var answer = new TypeStatsAnswer { Set = set, Type = onlyType };
        var index = session.Index;
        // A target's owner, the same number in every plugin read
        var owners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sets = new List<ReadSet>();
        var resolvers = new List<(FormIdResolver R, int[] Owner, bool Localized, Func<uint, string?> SigOf)>();
        foreach (var p in set.Plugins)
        {
            if (p.Segment is not { } seg) { answer.Notes.Add($"{p.Name} can't be read ({p.Problems.ReadError ?? "it has no header index"}): left out."); continue; }
            ReportSession.AddSource(answer, p);
            answer.PluginsRead++;
            var types = seg.Signatures.Select(s => onlyType == null || s.Signature == onlyType).ToArray();
            if (!types.Any(x => x)) continue;
            List<int>? entries = null;
            if (set.WinnersOnly)
            {
                entries = new List<int>();
                for (int e = 0; e < seg.Count; e++)
                    if (types[seg[e].Signature] && index.IsWinner(new RecordRef(p.Id, e))) entries.Add(e);
            }
            sets.Add(new ReadSet { Plugin = p, Signatures = types, Entries = entries });
            var masters = (p.Header?.Masters ?? Array.Empty<string>()).Append(p.Name).ToList();
            resolvers.Add((FormIdResolver.For(p), masters.Select(m => owners.TryGetValue(m, out int id) ? id : owners[m] = owners.Count).ToArray(), p.Header is { } h && (h.Flags & 0x80) != 0, SigOf(index, p)));
        }
        // Each type's link fields: at fixed places, and those the byte walk reads (GAPS S92), by its signature
        var plans = new ConcurrentDictionary<string, LinkPlan?>(StringComparer.Ordinal);
        LinkPlan? PlanOf(string sig) => plans.GetOrAdd(sig, s => names.TryGetValue(s, out var n) ? LinkPlan.Of(session.Schema, n) : null);
        LinkPlace[] PlacesOf(string sig) => PlanOf(sig)?.Places ?? Array.Empty<LinkPlace>();
        // One byte walker for each reading thread: it keeps what the schema says of each object from record to record
        using var walkers = new ThreadLocal<LinkByteWalker>(() => new LinkByteWalker(session.Schema));
        var byType = new Dictionary<string, TypeStats>(StringComparer.Ordinal);
        foreach (var r in RecordPipeline.Read<Stepped>(sets, (in PipelineRecord rec, List<Stepped> output) =>
        {
            string rsig = rec.Plugin.Segment!.SignatureOf(rec.Header);
            if (rec.Problem != PipelineProblem.None) { output.Add(new Stepped(rec.Set, rsig, rec.Header.Size, -1, rec.Header.IsCompressed, null, null, null)); return; }
            var (r0, owner, localized, sigOf) = resolvers[rec.Set];
            output.Add(Step(rec.Set, rsig, rec.Header.Size, rec.Header.IsCompressed, rec.Head, rec.Data, PlanOf(rsig), r0, owner, localized, sigOf, walkers) with { Plugin = rec.Plugin.Id, Entry = rec.EntryIndex });
        }))
        {
            if (!byType.TryGetValue(r.RecordSig, out var t))
                byType[r.RecordSig] = t = new TypeStats { Sig = r.RecordSig, Name = names.GetValueOrDefault(r.RecordSig) ?? r.RecordSig };
            t.Records++;
            t.FileBytes += r.Stored + 24;
            if (r.Subs == null) { t.Unreadable++; answer.Unreadable++; continue; }
            if (r.Problem != null)
            {
                t.WalkProblems++;
                if (t.WalkProblemSamples.Count < 3 && !t.WalkProblemSamples.Any(x => x.Why == r.Problem)) t.WalkProblemSamples.Add((r.Plugin, r.Entry, r.Problem));
                foreach (var lost in r.Lost ?? Array.Empty<string>()) t.WalkLost[lost] = t.WalkLost.GetValueOrDefault(lost) + 1;
            }
            t.ReadBytes += r.Plain;
            if (r.Compressed) t.Compressed++;
            foreach (var (sig, times, bytes, largest) in r.Subs)
            {
                if (!t.Subrecords.TryGetValue(sig, out var s)) t.Subrecords[sig] = s = new SubrecordStat { Signature = Sig(sig) };
                s.Records++; s.Times += times; s.Bytes += bytes; s.Largest = Math.Max(s.Largest, largest);
            }
            if (r.Links is { Count: > 0 } links)
            {
                if (t.Links.Count == 0) t.Links.AddRange(PlacesOf(r.RecordSig).Select(p => new LinkFieldStat { Place = p }));
                int last = -1;
                var seen = new HashSet<ulong>();
                foreach (var (field, key) in links)
                {
                    var f = t.Links[field];
                    if (field != last) { f.Records++; seen.Clear(); last = field; }
                    f.Links++;
                    if (seen.Add(key)) f.Rows++;
                    f.Targets.Add(key);
                }
            }
        }
        foreach (var t in byType.Values)
        {
            if (t.Links.Count == 0) t.Links.AddRange(PlacesOf(t.Sig).Select(p => new LinkFieldStat { Place = p }));
            if (PlanOf(t.Sig) is { } plan) t.LinksLeft.AddRange(plan.Left);
            // A walked field below a part the walk couldn't follow in some record isn't counted whole: it is named instead
            foreach (var (lost, n) in t.WalkLost)
                foreach (var l in t.Links.Where(l => l.Place.Walked && LinkByteWalker.Unmarked(l.Place.Path) is var u && (u == lost || u.StartsWith(lost + ".", StringComparison.Ordinal) || u.StartsWith(lost + "[", StringComparison.Ordinal))).ToList())
                {
                    t.Links.Remove(l);
                    t.LinksLeft.Add((l.Place.Path, l.Place.Subrecord.Length == 4 ? l.Place.Subrecord : null, $"the byte walk couldn't follow {lost} in {ReportAnswer.Plural(n, "record")}"));
                }
            answer.Records += t.Records;
            answer.Types.Add(t);
        }
        answer.Types.Sort((a, b) => b.FileBytes.CompareTo(a.FileBytes) is var c && c != 0 ? c : string.CompareOrdinal(a.Sig, b.Sig));
        answer.Read.Add($"the subrecords of {ReportAnswer.N(answer.Records)} records of {ReportAnswer.Plural(answer.PluginsRead, "plugin")}, from the files' bytes on {PipelineOptions.DefaultThreads} threads (no record read through Mutagen)");
        foreach (var t in answer.Types.Where(t => t.WalkProblems > 0))
            answer.Notes.Add($"{t.Sig}: in {ReportAnswer.Plural(t.WalkProblems, "record")} the byte walk couldn't follow every link field (its links found before that are counted): {string.Join("; ", t.WalkProblemSamples.Select(x => $"{x.Why} ({index.Describe(new RecordRef(x.Plugin, x.Entry)).Label})"))}.");
        if (answer.Unreadable > 0) answer.Notes.Add($"{ReportAnswer.Plural(answer.Unreadable, "record")} couldn't be read from the file (cut short, or compressed data that doesn't inflate): counted in the records and their size in the file, not in what they hold.");
        if (set.WinnersOnly) answer.Notes.Add("@winners: each record counts once, in the version the game uses.");
        return answer;
    });

    /// <summary>The record type of the record a stored FormID of the plugin names (its own masters' numbering), as the
    /// header index has it; null when no plugin in Data holds it. Each FormID looked up once.</summary>
    internal static Func<uint, string?> SigOf(HeaderIndex index, IndexedPlugin p)
    {
        var seen = new ConcurrentDictionary<uint, string?>();
        var ids = p.FormIds;
        return raw => seen.GetOrAdd(raw, w =>
        {
            if (!ids.TryResolve(w, out string owner, out uint local)) return null;
            if (index.Plugin(owner)?.Segment is { } os && os.Find(os.SelfOwner, local) is >= 0 and var at) return os.SignatureOf(os[at]);
            if (p.Segment is { } ps && ps.OwnerOf(owner) is >= 0 and var o && ps.Find(o, local) is >= 0 and var mine) return ps.SignatureOf(ps[mine]);
            return null;
        });
    }

    static string Sig(uint sig)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, sig);
        return Encoding.Latin1.GetString(b);
    }

    /// <summary>One record: its subrecords (signature, times, bytes, largest) and its links (field, target), the
    /// links in field order.</summary>
    readonly record struct Stepped(int Set, string RecordSig, long Stored, long Plain, bool Compressed, List<(uint Sig, int Times, long Bytes, long Largest)>? Subs, List<(int Field, ulong Target)>? Links, string? Problem, int Plugin = 0, int Entry = 0, string[]? Lost = null);

    static Stepped Step(int set, string rsig, long stored, bool compressed, ReadOnlySpan<byte> head, ReadOnlySpan<byte> body, LinkPlan? plan, FormIdResolver resolver, int[] owner, bool localized, Func<uint, string?> sigOf, ThreadLocal<LinkByteWalker> walkers)
    {
        var subs = new List<(uint Sig, int Times, long Bytes, long Largest)>();
        int p = 0; uint? big = null;
        while (p + 6 <= body.Length)
        {
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(body[p..]);
            int n = BinaryPrimitives.ReadUInt16LittleEndian(body[(p + 4)..]);
            p += 6;
            if (sig == 0x58585858 /* XXXX */ && n == 4 && p + 4 <= body.Length) { big = BinaryPrimitives.ReadUInt32LittleEndian(body[p..]); p += 4; continue; }
            long len = big ?? (uint)n; big = null;
            int size = (int)Math.Min(len, body.Length - p);
            p = (int)Math.Min(body.Length, p + len);
            int k = subs.FindIndex(s => s.Sig == sig);
            if (k < 0) subs.Add((sig, 1, size, size));
            else { var s = subs[k]; subs[k] = (sig, s.Times + 1, s.Bytes + size, Math.Max(s.Largest, size)); }
        }
        // The links: at the fixed places, and those the byte walk reads (GAPS S92)
        List<(int, ulong)>? links = null;
        var (problem, lost) = plan == null ? (null, null) : plan.Links(head, body, localized, sigOf, walkers.Value!, (field, formId, _, _) =>
        {
            if (formId is 0 or uint.MaxValue) return; // 0, and all ones (Mutagen reads it as FormKey.None), are no link
            ulong key = resolver.Key(formId);
            int o = (int)(key >> 24);
            ulong global = o < owner.Length ? (ulong)owner[o] << 24 | (key & 0xFFFFFF) : 1UL << 63 | key;
            (links ??= new()).Add((field, global));
        });
        links?.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return new Stepped(set, rsig, stored, body.Length, compressed, subs, links, problem, Lost: lost);
    }
}

public sealed class TypeStatsAnswer : ReportAnswer
{
    public override string Kind => "profile";
    public required PluginSet Set { get; init; }
    public string? Type { get; init; }
    public long Records, Unreadable;
    public int PluginsRead;
    /// <summary>Per record type, most bytes first.</summary>
    public List<TypeStats> Types { get; } = new();

    /// <summary>Subrecord rows shown per type without --detail.</summary>
    public const int Shown = 25;

    static string MB(long b) => Sizes.Text(b);
    static bool InComponent(string path) => path.StartsWith("Components[*]", StringComparison.Ordinal);

    protected override void Render(ReportBody body, ReportTextOptions options)
    {
        body.Head.Add($"What {(Type != null ? $"the {Type} records" : "the records")} of {Set.Shown} hold, from the files' bytes: {N(Records)} records of {Plural(Types.Count, "record type")} in {Plural(PluginsRead, "plugin")}.");
        body.RowNoun = "rows";
        foreach (var t in Types)
        {
            body.Rows.Add("");
            body.Rows.Add($"{t.Sig} ({t.Name}): {Plural(t.Records, "record")}, {MB(t.FileBytes)} in the file (with the 24-byte headers), {MB(t.ReadBytes)} as read ({N(t.Compressed)} compressed, inflated){(t.Unreadable > 0 ? $"; {N(t.Unreadable)} couldn't be read" : "")}.");
            body.Rows.Add("| Subrecord | Records that hold it | Times | Bytes as read | Largest |");
            body.Rows.Add("|---|---|---|---|---|");
            var subs = t.Subrecords.Values.OrderByDescending(s => s.Records).ThenByDescending(s => s.Bytes).ThenBy(s => s.Signature, StringComparer.Ordinal).ToList();
            foreach (var s in subs.Take(options.Detail ? int.MaxValue : Shown))
                body.Rows.Add($"| {Cell(s.Signature)} | {N(s.Records)} | {N(s.Times)} | {N(s.Bytes)} | {N(s.Largest)} |");
            if (!options.Detail && subs.Count > Shown) body.Rows.Add($"{t.Sig}: {Plural(subs.Count - Shown, "more subrecord")} (--detail shows them).");
            var links = t.Links.Where(l => l.Links > 0).OrderByDescending(l => l.Links).ThenBy(l => l.Place.Path, StringComparer.Ordinal).ToList();
            if (links.Count > 0)
            {
                body.Rows.Add($"| {t.Sig} link field | Subrecord | Links (not null) | Distinct per record | Distinct targets | Records with one |");
                body.Rows.Add("|---|---|---|---|---|---|");
                foreach (var l in links)
                    body.Rows.Add($"| {l.Place.Path} | {l.Place.Where} | {N(l.Links)} | {N(l.Rows)} | {N(l.DistinctTargets)} | {N(l.Records)} |");
            }
            long none = t.Links.Count(l => l.Links == 0);
            body.Rows.Add($"{t.Sig}: {Plural(t.Links.Count, "link field")} read from the bytes{(links.Count > 0 && none > 0 ? $", {N(none)} of them with no link here" : links.Count == 0 && t.Links.Count > 0 ? ", none with a link here" : "")}.");
            if (t.LinksLeft.Count > 0)
            {
                var left = t.LinksLeft.Where(l => !InComponent(l.Path)).ToList();
                int inComponents = t.LinksLeft.Count - left.Count;
                int show = options.Detail ? left.Count : 8;
                var named = left.Take(show).Select(l => $"{l.Path}{(l.Subrecord != null ? $" ({l.Subrecord})" : "")}").ToList();
                string first = left.Select(l => l.Path).FirstOrDefault(x => !x.StartsWith("VirtualMachineAdapter", StringComparison.Ordinal)) ?? left.Select(l => l.Path).FirstOrDefault() ?? t.LinksLeft[0].Path;
                body.Rows.Add($"{t.Sig}: {Plural(t.LinksLeft.Count, "link field")} not counted from the bytes"
                    + (left.Count > 0 ? $": {string.Join(", ", named)}{(left.Count > show ? $", … {N(left.Count - show)} more (--detail)" : "")} (read by Mutagen's own code that no layout describes, or of a kind the bytes don't tell; the JSON says why for each)" : "")
                    + (inComponents > 0 ? $"{(left.Count > 0 ? "; and" : ":")} {N(inComponents)} in components" : "")
                    + $". profile {t.Name}.{first} --in {Set.Shown.Split(' ')[0]} counts one through Mutagen.");
            }
        }
        if (Types.Count == 0) body.Rows.Add($"No record{(Type != null ? $" of type {Type}" : "")} in {Set.Shown}.");
    }

    protected override void Fill(JsonObject json)
    {
        json["plugins"] = Set.Shown;
        json["type"] = Type;
        json["records"] = Records;
        json["pluginsRead"] = PluginsRead;
        json["unreadable"] = Unreadable;
        json["types"] = new JsonArray(Types.Select(t => (JsonNode)new JsonObject
        {
            ["recordType"] = t.Sig, ["name"] = t.Name, ["records"] = t.Records, ["fileBytes"] = t.FileBytes, ["readBytes"] = t.ReadBytes,
            ["compressed"] = t.Compressed, ["unreadable"] = t.Unreadable,
            ["subrecords"] = new JsonArray(t.Subrecords.Values.OrderByDescending(s => s.Records).ThenBy(s => s.Signature, StringComparer.Ordinal).Select(s => (JsonNode)new JsonObject
            {
                ["subrecord"] = s.Signature, ["records"] = s.Records, ["times"] = s.Times, ["bytes"] = s.Bytes, ["largest"] = s.Largest,
            }).ToArray()),
            ["links"] = new JsonArray(t.Links.OrderByDescending(l => l.Links).ThenBy(l => l.Place.Path, StringComparer.Ordinal).Select(l => (JsonNode)new JsonObject
            {
                ["field"] = l.Place.Path, ["subrecord"] = l.Place.Subrecord, ["offset"] = l.Place.Offset, ["entryBytes"] = l.Place.Stride, ["entriesStart"] = l.Place.Start,
                ["links"] = l.Links, ["distinctPerRecord"] = l.Rows, ["distinctTargets"] = l.DistinctTargets, ["records"] = l.Records,
            }).ToArray()),
            ["linksNotFromBytes"] = new JsonArray(t.LinksLeft.Select(l => (JsonNode)new JsonObject { ["field"] = l.Path, ["subrecord"] = l.Subrecord, ["why"] = l.Why }).ToArray()),
        }).ToArray());
    }
}
