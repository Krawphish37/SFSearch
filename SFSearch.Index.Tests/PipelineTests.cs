// The pipeline test (to-do J35): the shared record-reading pipeline gives the same records, in the same order, on many
// threads as on one; it stops when cancelled; and the records in work never hold more memory than it is given.
using System.Diagnostics;
using SFSearch.Index;

namespace SFSearch.Index.Tests;

static class PipelineTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);

    /// <summary>What a read gives for one record: where it is and a hash of its plain bytes.</summary>
    readonly record struct Seen(int Set, int Entry, int Length, ulong Hash, PipelineProblem Problem);

    static ulong Fnv(ReadOnlySpan<byte> data)
    {
        ulong h = 14695981039346656037;
        foreach (byte b in data) { h ^= b; h *= 1099511628211; }
        return h;
    }

    public static string Run(Ctx ctx)
    {
        var index = ctx.DataIndex;
        var sf = index.Plugin("Starfield.esm") ?? throw new TestFailed("Starfield.esm isn't in Data");
        var seg = sf.Segment!;
        bool[] cells = seg.Signatures.Select(s => s.Signature == "CELL").ToArray();
        // Every cell of Starfield.esm; every record of three mods; and some entries of one of them given out of order
        var sets = new List<ReadSet> { new() { Plugin = sf, Signatures = cells } };
        foreach (var name in new[] { "kgcdoom.esm", "darkstar.esm", "atom74.esm" })
            if (index.Plugin(name) is { Segment: not null } p) sets.Add(new ReadSet { Plugin = p });
        var last = sets[^1].Plugin;
        var rnd = new Random(29);
        var shuffled = Enumerable.Range(0, last.Segment!.Count).Where(i => i % 7 == 0).OrderBy(_ => rnd.Next()).ToList();
        sets.Add(new ReadSet { Plugin = last, Entries = shuffled });

        PipelineWork<Seen> work = (in PipelineRecord r, List<Seen> output) => output.Add(new Seen(r.Set, r.EntryIndex, r.Data.Length, Fnv(r.Data), r.Problem));

        // One thread: the reference, with its time
        var one = new PipelineStats();
        var sw = Stopwatch.StartNew();
        var a = RecordPipeline.ReadAll(sets, work, new PipelineOptions { Threads = 1, Stats = one });
        double oneMs = sw.Elapsed.TotalMilliseconds;
        // Every record of every set, in the order of the sets and of their entries
        long expected = 0;
        for (int s = 0; s < sets.Count; s++)
        {
            var st = sets[s]; var sg = st.Plugin.Segment!;
            var order = st.Entries ?? Enumerable.Range(0, sg.Count).ToList();
            var want = order.Where(i => st.Signatures == null || st.Signatures[sg[i].Signature]).ToList();
            var got = a.Where(x => x.Set == s).Select(x => x.Entry).ToList();
            Check(got.SequenceEqual(want), $"set {s} ({st.Plugin.Name}): the one-thread read gave {got.Count:N0} records, not its {want.Count:N0} in their order");
            expected += want.Count;
        }
        Check(a.Count == expected, $"the one-thread read gave {a.Count:N0} records, not {expected:N0}");
        Check(a.Select(x => x.Set).SequenceEqual(a.Select(x => x.Set).OrderBy(x => x)), "the sets came out of order");
        Check(a.All(x => x.Problem == PipelineProblem.None), $"{a.Count(x => x.Problem != PipelineProblem.None)} records couldn't be given");

        // All threads: the same, record for record
        var many = new PipelineStats();
        sw.Restart();
        var b = RecordPipeline.ReadAll(sets, work, new PipelineOptions { Stats = many });
        double manyMs = sw.Elapsed.TotalMilliseconds;
        Check(a.SequenceEqual(b), $"{PipelineOptions.DefaultThreads} threads gave other records or another order than one thread");

        // A memory limit of half the largest record: never more in work at once (counted inside the work too), the
        // records over it alone, the same records
        long cap = Math.Max(1L << 20, (a.Max(x => (long)x.Length) + 24 + 4096) / 2);
        long inWork = 0, peak = 0;
        var capped = new PipelineStats();
        var c = RecordPipeline.ReadAll(sets, (in PipelineRecord r, List<Seen> output) =>
        {
            long est = Math.Min(cap, r.Data.Length + 24 + 4096);
            long now = Interlocked.Add(ref inWork, est);
            long p; while (now > (p = Interlocked.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, p) != p) { }
            output.Add(new Seen(r.Set, r.EntryIndex, r.Data.Length, Fnv(r.Data), r.Problem));
            Interlocked.Add(ref inWork, -est);
        }, new PipelineOptions { MemoryBytes = cap, Stats = capped });
        Check(a.SequenceEqual(c), "with a memory limit the records or their order changed");
        // A record charged the whole limit was alone in work whenever the count above held
        long alone = c.Count(x => x.Length + 24 + 4096 >= cap);
        Check(peak <= cap, $"the records in work held {peak:N0} bytes at once; the limit is {cap:N0}");
        Check(alone > 0, $"no record was over the {cap:N0}-byte limit: the test needs one that runs alone");

        // Cancelled after 1,000 records: it stops, says so, and gives no more than were read
        using var cts = new CancellationTokenSource();
        int read = 0, given = 0;
        bool stopped = false;
        sw.Restart();
        try
        {
            foreach (var _ in RecordPipeline.Read(sets, (in PipelineRecord r, List<int> output) => { if (Interlocked.Increment(ref read) == 1000) cts.Cancel(); output.Add(r.EntryIndex); }, new PipelineOptions { Cancel = cts.Token }))
                given++;
        }
        catch (OperationCanceledException) { stopped = true; }
        double cancelMs = sw.Elapsed.TotalMilliseconds;
        Check(stopped, "a cancelled read didn't throw OperationCanceledException");
        Check(read < expected / 2, $"after the cancel {read:N0} of {expected:N0} records were still read");
        Check(given <= read, "more records came out than were read");

        // Cells alone, raw: one thread against all
        var cellSet = new[] { sets[0] };
        PipelineWork<long> count = (in PipelineRecord r, List<long> output) => { if (r.Data.Length > 0 && r.Data[0] == 0xFF) output.Add(r.EntryIndex); };
        var c1 = new PipelineStats(); sw.Restart(); RecordPipeline.ReadAll(cellSet, count, new PipelineOptions { Threads = 1, Stats = c1 }); double cell1 = sw.Elapsed.TotalMilliseconds;
        var cN = new PipelineStats(); sw.Restart(); RecordPipeline.ReadAll(cellSet, count, new PipelineOptions { Stats = cN }); double cellN = sw.Elapsed.TotalMilliseconds;

        return $"{expected:N0} records of {sets.Count} sets 1 thread {oneMs:N0} ms, {PipelineOptions.DefaultThreads} threads {manyMs:N0} ms, the same records in the same order; "
            + $"{cap / 1048576.0:N1} MB limit: at most {peak / 1048576.0:N1} MB in work, {alone:N0} records alone, the same records; cancelled after 1,000: {read:N0} read, stopped in {cancelMs:N0} ms. "
            + $"Starfield.esm's {c1.Records[0]:N0} cells inflated: 1 thread {cell1:N0} ms, {PipelineOptions.DefaultThreads} threads {cellN:N0} ms";
    }
}
