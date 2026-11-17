// sfsrefs-check: a dev tool behind J29 (refs' confirmation of byte hits on all cores). See the .csproj for its verbs.
using System.Collections.Concurrent;
using System.Diagnostics;
using Mutagen.Bethesda.Plugins;
using SFSearch.Engine;
using SFSearch.Index;
using SFSearch.Records;

var rest = args.ToList();
string? Opt(string name) { int i = rest.IndexOf(name); if (i < 0 || i + 1 >= rest.Count) return null; string v = rest[i + 1]; rest.RemoveRange(i, 2); return v; }
string data = Opt("--data") ?? Environment.GetEnvironmentVariable("SFSEARCH_DATA") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data";
string scratch = Opt("--scratch") ?? Path.Combine(Path.GetTempPath(), "sfsrefs-check");
int threads = int.Parse(Opt("--threads") ?? PipelineOptions.DefaultThreads.ToString());
// --batch: the garbage collector's batch mode (no background collections) for the whole run
if (args.Contains("--batch")) { rest.Remove("--batch"); System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Batch; }
if (rest.Count == 0) { Console.WriteLine("usage: sfsrefs-check subrecords <SIG...> | walk <plugin> [SIG,...] | drop <localIdHex> <owner> | cells <plugin>"); return 2; }

var total = Stopwatch.StartNew();
var schema = new RecordSchema(Path.Combine(AppContext.BaseDirectory, "schema.db"));
if (rest[0] == "subrecords")
{
    foreach (var sig in rest.Skip(1))
    {
        var (outer, inComponent) = LinkSubrecords.Of(schema, sig);
        foreach (var (name, c) in new[] { ("its own", outer), ("in components", inComponent) })
        {
            Console.WriteLine($"{sig} {name}: no link field in {string.Join(" ", c.NoLink)}\n      may hold a link: {string.Join(" ", c.MayLink)}");
        }
    }
    return 0;
}

using var index = new HeaderIndex(PluginSource.Folder(data), new IndexOptions { ScratchFolder = Path.Combine(scratch, "index"), VersionText = $"sfsrefs-check / mutagen {schema.MutagenVersion} / schema {schema.Version}", Refusals = new MutagenRefusals() });
index.Refresh();
MutagenStart.Run();
using var engine = new RecordEngine(index, schema, Path.Combine(scratch, "verify"), data) { AllowHeavy = true };
Console.WriteLine($"header index and Mutagen ready in {total.ElapsedMilliseconds:N0} ms; {index.Plugins.Count} plugins; {threads} threads");
int bad = rest[0] switch
{
    "walk" => Walk(rest[1], rest.Count > 2 ? rest[2].Split(',').ToHashSet(StringComparer.Ordinal) : null),
    "drop" => Drop(uint.Parse(rest[1], System.Globalization.NumberStyles.HexNumber), rest[2]),
    "cells" => Cells(rest[1]),
    "parts" => Parts(uint.Parse(rest[1], System.Globalization.NumberStyles.HexNumber), rest[2]),
    "confirm" => Confirm(uint.Parse(rest[1], System.Globalization.NumberStyles.HexNumber), rest[2]),
    "fields" => Fields(rest[1], rest[2], int.Parse(rest.Count > 3 ? rest[3] : "200")),
    "world" => World(rest[1], rest.Count > 2 ? rest[2].Split(',').ToHashSet(StringComparer.Ordinal) : null),
    _ => throw new ArgumentException($"no verb {rest[0]}"),
};
using (var me = Process.GetCurrentProcess())
    Console.WriteLine($"{(bad == 0 ? "PASS" : $"FAIL ({bad})")}: {total.Elapsed.TotalSeconds:F1} s, peak working set {me.PeakWorkingSet64 >> 20:N0} MB");
return bad == 0 ? 0 : 1;

// ---------------------------------------------------------------- walk
// For every world record of a plugin: each link the walk finds is in Mutagen's EnumerateFormLinks (so a record whose list
// lacks the target can't link to it by the walk), and a record that holds others gives the same links read alone.
int Walk(string pluginName, HashSet<string>? only)
{
    var p = index.Plugin(pluginName) ?? throw new ArgumentException($"no plugin {pluginName}");
    var seg = p.Segment!;
    var world = WorldTypes.Of(seg);
    var mask = seg.Signatures.Select((s, i) => world[i] && (only == null || only.Contains(s.Signature))).ToArray();
    var use = engine.Sessions.Use(p.Name);
    var set = new ReadSet { Plugin = p, Signatures = mask };
    engine.OpenForThreads(Enumerable.Range(0, seg.Count).Where(i => mask[seg[i].Signature]).Select(i => new RecordRef(p.Id, i)));
    var sinks = new ThreadLocal<Mutagen.Bethesda.Serialization.FormLinkPlaceCollector>(() => RecordLinks.Collector());
    var bySig = new ConcurrentDictionary<string, long[]>(); // records, walk links, enumerate links, records with a walk link not listed, holders differing, failed
    var examples = new ConcurrentQueue<string>();
    var sw = Stopwatch.StartNew();
    foreach (var _ in RecordPipeline.Read<int>(new[] { set }, (in PipelineRecord r, List<int> output) =>
    {
        string sig = seg.SignatureOf(r.Header);
        var c = bySig.GetOrAdd(sig, _ => new long[6]);
        Interlocked.Increment(ref c[0]);
        try
        {
            var rec = engine.LoadAlone(r.Ref);
            var listed = new HashSet<FormKey>();
            foreach (var l in rec.Record.EnumerateFormLinks()) if (!l.FormKey.IsNull) listed.Add(l.FormKey);
            var sink = sinks.Value!;
            RecordLinks.Walk(rec.Record, sink);
            var walked = sink.Links.Select(l => (sink.Paths[l.Path], Target: l.FormKey)).ToList();
            Interlocked.Add(ref c[1], walked.Count); Interlocked.Add(ref c[2], listed.Count);
            var missing = walked.Where(w => !listed.Contains(w.Target)).ToList();
            if (missing.Count > 0) { Interlocked.Increment(ref c[3]); if (examples.Count < 20) examples.Enqueue($"{sig} {r.Ref}: walk link {missing[0].Item1} -> {missing[0].Target} isn't in EnumerateFormLinks"); }
            if (schema.HoldsRecords(rec.Type))
            {
                var full = engine.Load(r.Ref);
                RecordLinks.Walk(full.Record, sink);
                var withStubs = sink.Links.Select(l => (sink.Paths[l.Path], Target: l.FormKey)).OrderBy(x => x.Item1).ThenBy(x => x.Target.ToString()).ToList();
                if (!withStubs.SequenceEqual(walked.OrderBy(x => x.Item1).ThenBy(x => x.Target.ToString())))
                { Interlocked.Increment(ref c[4]); if (examples.Count < 20) examples.Enqueue($"{sig} {r.Ref}: {walked.Count} links read alone, {withStubs.Count} with its children's stubs"); }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { Interlocked.Increment(ref c[5]); if (examples.Count < 20) examples.Enqueue($"{sig} {r.Ref}: {e.GetType().Name}: {e.Message.Split('\n')[0]}"); }
    }, new PipelineOptions { Threads = threads, Data = PipelineData.None, CostPerByte = PipelineOptions.MutagenCostPerByte, MemoryBytes = 8L << 30 })) { }
    engine.Sessions.Confirm(use);
    Console.WriteLine($"{p.Name}: world records walked in {sw.Elapsed.TotalSeconds:F1} s");
    Console.WriteLine("type\trecords\twalk links\tlisted links\twalk link not listed\tholder alone differs\tfailed");
    foreach (var (sig, c) in bySig.OrderBy(k => k.Key)) Console.WriteLine($"{sig}\t{c[0]:N0}\t{c[1]:N0}\t{c[2]:N0}\t{c[3]:N0}\t{c[4]:N0}\t{c[5]:N0}");
    foreach (var e in examples) Console.WriteLine("  " + e);
    return (int)bySig.Values.Sum(c => c[3] + c[4]);
}

// ---------------------------------------------------------------- drop
// The byte search of every plugin that can point at the target, world data only, with the schema's subrecords: every hit
// it rules out is read and walked, and none may link to the target.
int Drop(uint localId, string owner)
{
    var noLink = LinkSubrecords.For(schema);
    var places = index.Plugins.Where(p => p.Segment != null && p.LeftOut == null && p.Problems.MissingMasters.Count == 0
        && (p.Name.Equals(owner, StringComparison.OrdinalIgnoreCase) || (p.Header?.Masters.Any(m => m.Equals(owner, StringComparison.OrdinalIgnoreCase)) ?? false))).ToList();
    var jobs = new List<ByteSearch.Job>();
    foreach (var p in places)
    {
        var seg = p.Segment!; int o = seg.OwnerOf(owner);
        if (o < 0 || FormIdPattern.For(p, o, localId) is not { } pat) continue;
        var world = WorldTypes.Of(seg);
        jobs.Add(new ByteSearch.Job(p, pat, world, NoLink: noLink.For(seg, world)));
    }
    var results = ByteSearch.SearchMany(jobs, threads, out double ms);
    var bySig = new Dictionary<string, (int Hits, int Ruled)>();
    var sets = new List<ReadSet>();
    for (int k = 0; k < jobs.Count; k++)
    {
        var seg = jobs[k].Plugin.Segment!;
        foreach (int e in results[k].Hits) { var s = seg.SignatureOf(seg[e]); var v = bySig.GetValueOrDefault(s); bySig[s] = (v.Hits + 1, v.Ruled); }
        foreach (int e in results[k].NoLinkField) { var s = seg.SignatureOf(seg[e]); var v = bySig.GetValueOrDefault(s); bySig[s] = (v.Hits, v.Ruled + 1); }
        if (results[k].NoLinkField.Count > 0) sets.Add(new ReadSet { Plugin = jobs[k].Plugin, Entries = results[k].NoLinkField });
    }
    Console.WriteLine($"byte search of {jobs.Count} plugins: {ms:N0} ms; by type, hits left for Mutagen / ruled out by the schema: {string.Join(", ", bySig.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value.Hits:N0}/{k.Value.Ruled:N0}"))}");
    var use = engine.Sessions.Use(sets.Select(s => s.Plugin.Name).Distinct().ToArray());
    foreach (var s in sets) engine.OpenForThreads(s.Entries!.Select(e => new RecordRef(s.Plugin.Id, e)));
    var sinks = new ThreadLocal<Mutagen.Bethesda.Serialization.FormLinkPlaceCollector>(() => RecordLinks.Collector());
    var wrong = RecordPipeline.ReadAll<string>(sets, (in PipelineRecord r, List<string> output) =>
    {
        var rec = engine.LoadAlone(r.Ref);
        var sink = sinks.Value!;
        RecordLinks.Walk(rec.Record, sink);
        foreach (var (field, target) in sink.Links)
            if (target.ID == localId && target.ModKey.FileName.String.Equals(owner, StringComparison.OrdinalIgnoreCase)) { output.Add($"{r.Plugin.Name} {r.Ref} {sink.Paths[field]}"); break; }
    }, new PipelineOptions { Threads = threads, Data = PipelineData.None, CostPerByte = PipelineOptions.MutagenCostPerByte, MemoryBytes = 8L << 30 });
    engine.Sessions.Confirm(use);
    Console.WriteLine($"{sets.Sum(s => s.Entries!.Count):N0} ruled-out hits walked: {wrong.Count} link to the target{(wrong.Count > 0 ? ": " + string.Join("; ", wrong.Take(10)) : "")}");
    return wrong.Count;
}

// ---------------------------------------------------------------- cells
// Every cell of a plugin read through Mutagen (no JSON) on the pipeline: one thread, then all; the same links in the same order.
int Cells(string pluginName)
{
    var p = index.Plugin(pluginName) ?? throw new ArgumentException($"no plugin {pluginName}");
    var seg = p.Segment!;
    var set = new ReadSet { Plugin = p, Signatures = seg.Signatures.Select(s => s.Signature == "CELL").ToArray() };
    var use = engine.Sessions.Use(p.Name);
    PipelineWork<int> load = (in PipelineRecord r, List<int> output) => output.Add(engine.Load(r.Ref).Record.EnumerateFormLinks().Count());
    var results = new List<List<int>>();
    foreach (int n in new[] { 1, threads })
    {
        var stats = new PipelineStats();
        var sw = Stopwatch.StartNew();
        long alloc0 = GC.GetTotalAllocatedBytes();
        results.Add(RecordPipeline.ReadAll(new[] { set }, load, new PipelineOptions { Threads = n, Data = PipelineData.None, CostPerByte = PipelineOptions.MutagenCostPerByte, MemoryBytes = 2L << 30, Stats = stats }));
        Console.WriteLine($"{stats.Records[0]:N0} cells of {p.Name} through Mutagen (Load and EnumerateFormLinks, no JSON), {n} thread(s): {sw.Elapsed.TotalSeconds:F2} s, {(GC.GetTotalAllocatedBytes() - alloc0) >> 20:N0} MB allocated");
    }
    engine.Sessions.Confirm(use);
    bool same = results[0].SequenceEqual(results[1]);
    Console.WriteLine(same ? "the same links per cell, in the same order" : "the threads gave other counts or order");
    return same ? 0 : 1;
}

// ---------------------------------------------------------------- confirm
// refs' confirmation of a target's world hits as RefsTool does it (LoadAlone, EnumerateFormLinks, the walk when listed),
// timed by type, with the pipeline's memory limit and cost per byte given: --mem MB, --cost N, --threads N, --stubs.
int Confirm(uint localId, string owner)
{
    long memMB = long.Parse(Opt("--mem") ?? "2048"); double cost = double.Parse(Opt("--cost") ?? PipelineOptions.MutagenCostPerByte.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
    bool stubs = rest.Remove("--stubs");
    var noLink = LinkSubrecords.For(schema);
    var places = index.Plugins.Where(p => p.Segment != null && p.LeftOut == null && p.Problems.MissingMasters.Count == 0
        && (p.Name.Equals(owner, StringComparison.OrdinalIgnoreCase) || (p.Header?.Masters.Any(m => m.Equals(owner, StringComparison.OrdinalIgnoreCase)) ?? false))).ToList();
    var jobs = new List<ByteSearch.Job>();
    foreach (var p in places)
    {
        var seg = p.Segment!; int o = seg.OwnerOf(owner);
        if (o < 0 || FormIdPattern.For(p, o, localId) is not { } pat) continue;
        var world = WorldTypes.Of(seg);
        jobs.Add(new ByteSearch.Job(p, pat, world, NoLink: noLink.For(seg, world)));
    }
    for (int pass = 0; pass < 2; pass++)
    {
        var results = ByteSearch.SearchMany(jobs, threads, out double ms);
        Console.WriteLine($"byte search of {jobs.Count} plugins, pass {pass + 1}: {ms:N0} ms, {results.Sum(r => r.Hits.Count):N0} hits, {results.Sum(r => r.NoLinkField.Count):N0} ruled out");
    }
    var res = ByteSearch.SearchMany(jobs, threads, out _);
    var sets = new List<ReadSet>();
    for (int k = 0; k < jobs.Count; k++) if (res[k].Hits.Count > 0) sets.Add(new ReadSet { Plugin = jobs[k].Plugin, Entries = res[k].Hits });
    var use = engine.Sessions.Use(sets.Select(s => s.Plugin.Name).Distinct().ToArray());
    foreach (var s in sets) engine.OpenForThreads(s.Entries!.Select(e => new RecordRef(s.Plugin.Id, e)));
    var target = new FormKey(ModKey.FromFileName(owner), localId);
    var sinks = new ThreadLocal<Mutagen.Bethesda.Serialization.FormLinkPlaceCollector>(() => RecordLinks.Collector());
    var bySig = new ConcurrentDictionary<string, long[]>(); // records, load ticks, list ticks, walk ticks, links
    var stats = new PipelineStats();
    var sw = Stopwatch.StartNew();
    long alloc0 = GC.GetTotalAllocatedBytes();
    RecordPipeline.ReadAll<int>(sets, (in PipelineRecord r, List<int> output) =>
    {
        var c = bySig.GetOrAdd(r.Plugin.Segment!.SignatureOf(r.Header), _ => new long[5]);
        long a = Stopwatch.GetTimestamp();
        var rec = stubs ? engine.Load(r.Ref) : engine.LoadAlone(r.Ref);
        long b = Stopwatch.GetTimestamp();
        bool listed = rec.Record.EnumerateFormLinks().Any(l => l.FormKey == target);
        long d = Stopwatch.GetTimestamp();
        if (listed) { var sink = sinks.Value!; RecordLinks.Walk(rec.Record, sink); Interlocked.Increment(ref c[4]); }
        long e = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref c[0]); Interlocked.Add(ref c[1], b - a); Interlocked.Add(ref c[2], d - b); Interlocked.Add(ref c[3], e - d);
    }, new PipelineOptions { Threads = threads, Data = PipelineData.None, CostPerByte = cost, MemoryBytes = memMB << 20, Stats = stats, PieceRecords = 256, PieceBytes = 1L << 20 });
    engine.Sessions.Confirm(use);
    double f = Stopwatch.Frequency / 1000.0;
    Console.WriteLine($"confirm of {sets.Sum(s => s.Entries!.Count):N0} hits, {threads} threads, memory {memMB:N0} MB at {cost} a byte{(stubs ? ", with stubs" : "")}: {sw.Elapsed.TotalSeconds:F2} s, {(GC.GetTotalAllocatedBytes() - alloc0) >> 20:N0} MB allocated, GC pauses {GC.GetTotalPauseDuration().TotalSeconds:F1} s so far");
    foreach (var (sig, c) in bySig.OrderByDescending(k => k.Value[1] + k.Value[2] + k.Value[3]))
        Console.WriteLine($"   {sig}	{c[0]:N0} records	load {c[1] / f:N0} ms	links list {c[2] / f:N0} ms	walk {c[3] / f:N0} ms	{c[4]:N0} linked");
    return 0;
}

// ---------------------------------------------------------------- world
// J32: for every world record of a plugin (or of the types named), the world link index's reading (WorldReader: the
// byte readers for CELL XTV2, NAVM NVNM, SFTR FNAM and NAVI NVMI, and Mutagen's walk of the rest of the record) against
// the walk of the whole record read by Mutagen: the same (field, target, count) rows, record by record. Also against
// Mutagen's own list of links (EnumerateFormLinks): the same targets and counts (a difference there is reported, not
// failed: the walk is the definition, J29).
int World(string pluginName, HashSet<string>? only)
{
    var p = index.Plugin(pluginName) ?? throw new ArgumentException($"no plugin {pluginName}");
    var seg = p.Segment!;
    var world = WorldTypes.Of(seg);
    var mask = seg.Signatures.Select((s, i) => world[i] && (only == null || only.Contains(s.Signature))).ToArray();
    var use = engine.Sessions.Use(p.Name);
    var plugin = new WorldReader.Plugin { P = p, Session = engine.Sessions.Get(p.Name) };
    var fields = new List<string>();
    var ids = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
    int FieldId(string path) => ids.GetOrAdd(path, x => { lock (fields) { fields.Add(x); return fields.Count - 1; } });
    var readers = new ThreadLocal<WorldReader>(() => new WorldReader(schema, FieldId));
    var sinks = new ThreadLocal<Mutagen.Bethesda.Serialization.FormLinkPlaceCollector>(() => RecordLinks.Collector());
    var owners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < seg.Names.Count; i++) owners[seg.Names[i]] = i;
    // records, read by bytes, whole by Mutagen, unread, rows, links, records differing from the walk, records differing from EnumerateFormLinks, ours ticks, walk ticks
    var bySig = new ConcurrentDictionary<string, long[]>();
    var examples = new ConcurrentQueue<string>();
    var enumExamples = new ConcurrentQueue<string>();
    var sw = Stopwatch.StartNew();
    RecordPipeline.ReadAll<int>(new[] { new ReadSet { Plugin = p, Signatures = mask } }, (in PipelineRecord r, List<int> output) =>
    {
        string sig = seg.SignatureOf(r.Header);
        var c = bySig.GetOrAdd(sig, _ => new long[10]);
        Interlocked.Increment(ref c[0]);
        string label = $"{sig} {index.Describe(r.Ref).Label}";
        try
        {
            long t0 = Stopwatch.GetTimestamp();
            var rows = new List<WorldRow>();
            var how = readers.Value!.Read(plugin, r.EntryIndex, r.Head, r.Data, rows, out string? why, out _);
            long t1 = Stopwatch.GetTimestamp();
            Interlocked.Add(ref c[8], t1 - t0);
            if (how == WorldReader.How.BytesAndMutagen) Interlocked.Increment(ref c[1]);
            if (how == WorldReader.How.MutagenWhole) Interlocked.Increment(ref c[2]);
            if (how == WorldReader.How.Unread) { Interlocked.Increment(ref c[3]); if (examples.Count < 30) examples.Enqueue($"{label}: unread: {why}"); return; }
            var ours = new Dictionary<(string, ulong), long>();
            foreach (var row in rows) { string f; lock (fields) f = fields[row.Field]; ours[(f, row.Target)] = ours.GetValueOrDefault((f, row.Target)) + row.Count; }
            Interlocked.Add(ref c[4], rows.Count); Interlocked.Add(ref c[5], rows.Sum(x => (long)x.Count));
            // The whole record, as the file holds it, through Mutagen and the walk
            var bytes = new byte[24 + r.Data.Length];
            r.Head.CopyTo(bytes);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)r.Data.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(r.Head[8..]) & ~RawRecord.CompressedFlag);
            r.Data.CopyTo(bytes.AsSpan(24));
            var rec = (Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter)plugin.Session.ReadBytes(schema.TypesOf(sig)[0], bytes);
            var sink = sinks.Value!;
            RecordLinks.Walk(rec, sink);
            Interlocked.Add(ref c[9], Stopwatch.GetTimestamp() - t1);
            var walk = new Dictionary<(string, ulong), long>();
            foreach (var (field, target) in sink.Links)
            {
                if (!owners.TryGetValue(target.ModKey.FileName.String, out int o)) continue;
                var k = (sink.Paths[field], (ulong)o << 24 | target.ID);
                walk[k] = walk.GetValueOrDefault(k) + 1;
            }
            if (walk.Count != ours.Count || walk.Any(kv => ours.GetValueOrDefault(kv.Key) != kv.Value))
            {
                Interlocked.Increment(ref c[6]);
                if (examples.Count < 30)
                {
                    var only1 = walk.Where(kv => ours.GetValueOrDefault(kv.Key) != kv.Value).Take(3).Select(kv => $"walk {kv.Key.Item1} {kv.Key.Item2:X} x{kv.Value} (ours x{ours.GetValueOrDefault(kv.Key)})");
                    var only2 = ours.Where(kv => !walk.ContainsKey(kv.Key)).Take(3).Select(kv => $"ours only {kv.Key.Item1} {kv.Key.Item2:X} x{kv.Value}");
                    examples.Enqueue($"{label} ({how}): {string.Join("; ", only1.Concat(only2))}");
                }
            }
            // Mutagen's own list: targets and counts
            var listed = new Dictionary<ulong, long>();
            foreach (var l in rec.EnumerateFormLinks())
            {
                if (l.FormKey.IsNull || !owners.TryGetValue(l.FormKey.ModKey.FileName.String, out int o)) continue;
                ulong k = (ulong)o << 24 | l.FormKey.ID;
                listed[k] = listed.GetValueOrDefault(k) + 1;
            }
            var mine = new Dictionary<ulong, long>();
            foreach (var (k, n) in ours) mine[k.Item2] = mine.GetValueOrDefault(k.Item2) + n;
            if (listed.Count != mine.Count || listed.Any(kv => mine.GetValueOrDefault(kv.Key) != kv.Value))
            {
                Interlocked.Increment(ref c[7]);
                if (enumExamples.Count < 10)
                {
                    var d = listed.Where(kv => mine.GetValueOrDefault(kv.Key) != kv.Value).Take(2).Select(kv => $"{kv.Key:X} listed x{kv.Value}, ours x{mine.GetValueOrDefault(kv.Key)}")
                        .Concat(mine.Where(kv => !listed.ContainsKey(kv.Key)).Take(2).Select(kv => $"{kv.Key:X} ours only x{kv.Value}"));
                    enumExamples.Enqueue($"{label}: {string.Join("; ", d)}");
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { Interlocked.Increment(ref c[6]); if (examples.Count < 30) examples.Enqueue($"{label}: {e.GetType().Name}: {e.Message.Split('\n')[0]}"); }
    }, new PipelineOptions { Threads = threads, Data = PipelineData.Plain, CostPerByte = PipelineOptions.MutagenCostPerByte, MemoryBytes = 12L << 30 });
    engine.Sessions.Confirm(use);
    double f = Stopwatch.Frequency;
    Console.WriteLine($"{p.Name}: world records read both ways in {sw.Elapsed.TotalSeconds:F1} s ({threads} threads)");
    Console.WriteLine("type\trecords\tbulky by bytes\twhole by Mutagen\tunread\trows\tlinks\tdiffer from walk\tdiffer from EnumerateFormLinks\tindex reading s (summed)\twhole walk s (summed)");
    foreach (var (sig, c) in bySig.OrderBy(k => k.Key)) Console.WriteLine($"{sig}\t{c[0]:N0}\t{c[1]:N0}\t{c[2]:N0}\t{c[3]:N0}\t{c[4]:N0}\t{c[5]:N0}\t{c[6]:N0}\t{c[7]:N0}\t{c[8] / f:F1}\t{c[9] / f:F1}");
    foreach (var e in examples) Console.WriteLine("  " + e);
    foreach (var e in enumExamples) Console.WriteLine("  EnumerateFormLinks: " + e);
    return (int)bySig.Values.Sum(c => c[6] + c[3]);
}

// ---------------------------------------------------------------- fields
// The walk's field paths of the first N records of one type of a plugin, with how many links each.
int Fields(string pluginName, string sig, int most)
{
    var p = index.Plugin(pluginName) ?? throw new ArgumentException($"no plugin {pluginName}");
    var seg = p.Segment!;
    var use = engine.Sessions.Use(p.Name);
    var sink = RecordLinks.Collector();
    var count = new SortedDictionary<string, long>(StringComparer.Ordinal);
    int n = 0;
    for (int i = 0; i < seg.Count && n < most; i++)
    {
        if (seg.SignatureOf(seg[i]) != sig) continue;
        n++;
        var rec = engine.LoadAlone(new RecordRef(p.Id, i));
        RecordLinks.Walk(rec.Record, sink);
        foreach (var (f, _) in sink.Links) count[sink.Paths[f]] = count.GetValueOrDefault(sink.Paths[f]) + 1;
    }
    engine.Sessions.Confirm(use);
    Console.WriteLine($"{n} {sig} of {p.Name}:");
    foreach (var (f, c) in count) Console.WriteLine($"  {f}\t{c:N0}");
    return 0;
}

// ---------------------------------------------------------------- parts
// The fixed parts of refs' world search, each timed once, cold: the schema's lists, the first byte search, opening the
// plugin with hits, readying its records for threads.
int Parts(uint localId, string owner)
{
    var sw = Stopwatch.StartNew();
    var noLink = LinkSubrecords.For(schema);
    var places = index.Plugins.Where(p => p.Segment != null && p.LeftOut == null && p.Problems.MissingMasters.Count == 0
        && (p.Name.Equals(owner, StringComparison.OrdinalIgnoreCase) || (p.Header?.Masters.Any(m => m.Equals(owner, StringComparison.OrdinalIgnoreCase)) ?? false))).ToList();
    var jobs = new List<ByteSearch.Job>();
    foreach (var p in places)
    {
        var seg = p.Segment!; int o = seg.OwnerOf(owner);
        if (o < 0 || FormIdPattern.For(p, o, localId) is not { } pat) continue;
        var world = WorldTypes.Of(seg);
        if (!world.Any(x => x)) continue;
        jobs.Add(new ByteSearch.Job(p, pat, world, NoLink: noLink.For(seg, world)));
    }
    Console.WriteLine($"the schema's lists for {jobs.Count} plugins' world types: {sw.ElapsedMilliseconds:N0} ms");
    sw.Restart(); var res = ByteSearch.SearchMany(jobs, threads, out _); Console.WriteLine($"byte search, first: {sw.ElapsedMilliseconds:N0} ms");
    sw.Restart(); ByteSearch.SearchMany(jobs, threads, out _); Console.WriteLine($"byte search, second: {sw.ElapsedMilliseconds:N0} ms");
    var names = jobs.Select((j, k) => (j, k)).Where(x => res[x.k].Hits.Count > 0).Select(x => x.j.Plugin.Name).ToArray();
    sw.Restart(); var use = engine.Sessions.Use(names); Console.WriteLine($"opening {names.Length} plugin(s) with hits: {sw.ElapsedMilliseconds:N0} ms");
    sw.Restart();
    for (int k = 0; k < jobs.Count; k++) if (res[k].Hits.Count > 0) engine.OpenForThreads(res[k].Hits.Select(e => new RecordRef(jobs[k].Plugin.Id, e)), alone: true);
    Console.WriteLine($"readying their records for threads: {sw.ElapsedMilliseconds:N0} ms");
    engine.Sessions.Confirm(use);
    return 0;
}
