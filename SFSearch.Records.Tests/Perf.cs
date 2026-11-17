// sfsrecord-tests --perf: how records read straight from their own bytes scale with threads, and where the time goes.
// The numbers of design\agent-first\notes\perf-reads.md are made here.
//
//   sfsrecord-tests --perf <Data folder> [--plugin Starfield.esm] [--sig REFR,INFO] [--threads 1,2,4,8]
//                   [--stages describe,handle,bytes,load,json] [--limit <records>] [--work <folder>] [--runs 1]
//
// Each stage does everything the stage before it does, and one thing more, over every record of the signature in
// file order, in batches of 512 like the SQL layer's reading threads:
//   describe  the header index's line for the record (FormKey, EditorID, position, size)
//   handle    + the record's bytes the way the code read them before this work: two reads through the session's
//             one shared file handle (and a third for a compressed record)
//   bytes     + the record's bytes the way a direct read gets them now
//   load      RecordEngine.Load: describe, bytes, and Mutagen's CreateFromBinary
//   json      + the record's JSON as the SQL layer makes it
// A stage's own cost is its time less the time of the stage before. "Summed" is the time inside the stage added up
// over the threads: with perfect scaling it stays what one thread takes.
// The Data folder is only read. Run it through heavy.sh with HEAVY_FULL=1 when the signature is a world type.
using System.Diagnostics;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Records.Tests;

static class Perf
{
    public static int Run(string[] args)
    {
        var rest = args.Skip(2).ToList();
        string? Opt(string name) { int i = rest.IndexOf(name); if (i < 0 || i + 1 >= rest.Count) return null; string v = rest[i + 1]; rest.RemoveRange(i, 2); return v; }
        string data = Path.GetFullPath(args[1]);
        string plugin = Opt("--plugin") ?? "Starfield.esm";
        string[] sigs = (Opt("--sig") ?? "REFR,INFO").Split(',');
        int[] threads = (Opt("--threads") ?? "1,2,4,8").Split(',').Select(int.Parse).ToArray();
        string[] stages = (Opt("--stages") ?? "describe,handle,bytes,load,json").Split(',');
        int limit = int.Parse(Opt("--limit") ?? "0"), runs = int.Parse(Opt("--runs") ?? "1");
        string work = Path.GetFullPath(Opt("--work") ?? Path.Combine(Path.GetTempPath(), "sfsrecord-perf"));
        Directory.CreateDirectory(work);

        var schema = new RecordSchema(Path.Combine(AppContext.BaseDirectory, "schema.db"));
        string path = Path.Combine(data, plugin);
        if (!File.Exists(path)) { Console.Error.WriteLine($"Error: {path} isn't there."); return 1; }
        // The one plugin and its masters: the header index of all of Data isn't needed to read one plugin's records
        var files = new List<string> { path };
        var header = PluginHeader.Read(path) ?? throw new InvalidDataException($"{path} has no plugin header");
        files.AddRange(header.Masters.Select(m => Path.Combine(data, m)).Where(File.Exists));
        using var index = new HeaderIndex(PluginSource.FromFiles(files), new IndexOptions { ScratchFolder = Path.Combine(work, "index"), VersionText = $"sfsrecord-perf / mutagen {schema.MutagenVersion} / schema {schema.Version}" });
        index.Refresh();
        using var e = new RecordEngine(index, schema, Path.Combine(work, "verify"), data) { AllowHeavy = false };
        var p = index.Plugin(plugin)!;
        var seg = p.Segment!;
        e.Sessions.Use(plugin);
        var session = e.Sessions.Get(plugin);
        Console.WriteLine($"{plugin}: {seg.Count:N0} records; {Environment.ProcessorCount} logical processors; server GC {System.Runtime.GCSettings.IsServerGC}, concurrent GC {System.Runtime.GCSettings.LatencyMode}");

        foreach (string sig in sigs)
        {
            int[] entries = seg.EntriesOf(sig).ToArray();
            if (entries.Length == 0) { Console.WriteLine($"{sig}: none in {plugin}"); continue; }
            var offsets = new long[entries.Length];
            long bytes = 0;
            for (int i = 0; i < entries.Length; i++) { offsets[i] = seg[entries[i]].Offset; bytes += seg[entries[i]].Size; }
            Array.Sort(offsets, entries);
            if (limit > 0 && entries.Length > limit)
            {
                // Whole batches spread over the file, so the sample has the file's own mix and stays in file order
                const int batch = 512;
                int batches = entries.Length / batch, want = Math.Max(1, limit / batch), step = Math.Max(1, batches / want);
                var pick = new List<int>(limit + batch);
                for (int b = 0; b < batches; b += step) pick.AddRange(entries.AsSpan(b * batch, batch).ToArray());
                entries = pick.ToArray();
            }
            string type = e.TypeFor(sig);
            bool holder = schema.HoldsRecords(type);
            Console.WriteLine();
            Console.WriteLine($"{sig} ({type}): {entries.Length:N0} records{(limit > 0 ? " (a sample)" : "")}, {bytes / 1048576.0:F1} MB in the file in all, {seg.Signatures.First(s => s.Signature == sig).Compressed:N0} compressed");
            Console.WriteLine("Stage\tThreads\tWallSeconds\tSummedSeconds\tMicrosecondsPerRecord\tGcPauseSeconds\tGen0\tGen1\tGen2\tAllocatedMB\tCheck");
            // The code's one-time start-up isn't measured
            Stage("json", e, session, p, entries.AsSpan(0, Math.Min(entries.Length, 2048)).ToArray(), 1, holder, out _);
            foreach (string stage in stages)
                foreach (int t in threads)
                    for (int run = 0; run < runs; run++)
                    {
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        long alloc0 = GC.GetTotalAllocatedBytes(true); var pause0 = GC.GetTotalPauseDuration();
                        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                        var wall = Stopwatch.StartNew();
                        double summed = Stage(stage, e, session, p, entries, t, holder, out long check);
                        double w = wall.Elapsed.TotalSeconds;
                        Console.WriteLine($"{stage}\t{t}\t{w:F2}\t{summed:F2}\t{w * 1e6 / entries.Length:F2}\t{(GC.GetTotalPauseDuration() - pause0).TotalSeconds:F2}\t{GC.CollectionCount(0) - g0}\t{GC.CollectionCount(1) - g1}\t{GC.CollectionCount(2) - g2}\t{(GC.GetTotalAllocatedBytes(true) - alloc0) / 1048576.0:F0}\t{check}");
                        Console.Out.Flush();
                    }
        }
        using var proc = Process.GetCurrentProcess();
        Console.WriteLine($"peak working set {proc.PeakWorkingSet64 >> 20:N0} MB");
        return 0;
    }

    /// <summary>Runs one stage over the records on a number of threads of its own. Gives the seconds inside the
    /// stage summed over the threads, and a number that must be the same for every thread count of a stage.</summary>
    static double Stage(string stage, RecordEngine e, PluginSession session, IndexedPlugin p, int[] entries, int threads, bool holder, out long check)
    {
        const int batch = 512;
        int next = 0; long ticks = 0, sum = 0;
        Exception? failure = null;
        var index = e.Index;
        // The "bytes" stage: Mutagen's reader of one record, as the session makes it (the same masters' sizes)
        using var reader = Mutagen.Bethesda.Plugins.Binary.MajorRecordReader.FromPath(new Mutagen.Bethesda.Plugins.ModPath(session.ModKey, session.Path), Mutagen.Bethesda.GameRelease.Starfield,
            new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryReadParameters { MasterFlagsLookup = session.MasterFlags });
        void Work()
        {
            long mine = 0, t = 0;
            var head = new byte[24]; var first = new byte[28];
            try
            {
                while (true)
                {
                    int start = Interlocked.Add(ref next, batch) - batch;
                    if (start >= entries.Length) break;
                    int end = Math.Min(entries.Length, start + batch);
                    long t0 = Stopwatch.GetTimestamp();
                    for (int i = start; i < end; i++)
                    {
                        var r = new RecordRef(p.Id, entries[i]);
                        switch (stage)
                        {
                            case "describe":
                                mine += index.Describe(r).FormKey.Length;
                                break;
                            case "handle":
                            {
                                var info = index.Describe(r);
                                // As RecordEngine.Load and PluginSession.DirectBuffer read before this work
                                if ((info.Flags & RawRecord.CompressedFlag) != 0 && info.Size >= 4) RawRecord.ReadExactly(session.File, head.AsSpan(0, 4), info.Offset + RawRecord.HeaderLength);
                                RawRecord.ReadExactly(session.File, head, info.Offset);
                                var record = new byte[24 + BitConverter.ToUInt32(head, 4)];
                                RawRecord.ReadExactly(session.File, record, info.Offset);
                                if (holder && info.Offset + record.Length + 24 <= session.FileLength) RawRecord.ReadExactly(session.File, head, info.Offset + record.Length);
                                mine += record.Length;
                                break;
                            }
                            case "bytes":
                            {
                                var info = index.Describe(r);
                                // As RecordEngine.Load reads now: Mutagen's MajorRecordReader, a holder's children as headers (C58 W9)
                                mine += reader.ReadBytesAt(info.Offset, holder ? Mutagen.Bethesda.Plugins.Binary.ChildRecordRead.Headers : Mutagen.Bethesda.Plugins.Binary.ChildRecordRead.None).Length;
                                break;
                            }
                            case "load":
                                mine += e.Load(r).Type.Length;
                                break;
                            case "json":
                            {
                                var rec = e.Load(r);
                                string json = e.Schema.HoldsRecords(rec.Type) ? e.Json(rec).ToJsonString() : RecordSerializer.ToJson(rec.Record);
                                mine += json.Length;
                                break;
                            }
                            default: throw new ArgumentException($"no stage named {stage}");
                        }
                    }
                    t += Stopwatch.GetTimestamp() - t0;
                }
            }
            catch (Exception x) { Interlocked.CompareExchange(ref failure, x, null); }
            Interlocked.Add(ref ticks, t); Interlocked.Add(ref sum, mine);
        }
        var pool = Enumerable.Range(0, threads).Select(_ => new Thread(Work) { IsBackground = true }).ToList();
        foreach (var th in pool) th.Start();
        foreach (var th in pool) th.Join();
        if (failure != null) throw new InvalidOperationException($"the stage {stage} failed on {threads} threads: {failure.GetType().Name}: {failure.Message}", failure);
        check = sum;
        return ticks / (double)Stopwatch.Frequency;
    }
}
