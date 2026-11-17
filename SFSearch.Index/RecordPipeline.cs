using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;

// The shared "read these records" pipeline (to-do J35, 2026-11-15). It lives here, in the header index's library, because
// everything that reads records stands on this library: the byte search below it, the records layer (Mutagen) and the SQL
// layer above it. The pipeline itself never calls Mutagen; a caller that reads through Mutagen does so in its work
// callback, with the record's place (SFSearch.Records\RecordEngine.cs, Load).
namespace SFSearch.Index
{
    /// <summary>The records of one plugin to read: every record of the chosen types, or the entries named.</summary>
    public sealed class ReadSet
    {
        public required IndexedPlugin Plugin { get; init; }
        /// <summary>Which record types, by their place in the segment's signature table. Null: all.</summary>
        public bool[]? Signatures { get; init; }
        /// <summary>Only these entries of the segment, in the order given (the results keep it). Null: every entry of the
        /// chosen types, in the segment's order (FormKey order).</summary>
        public IReadOnlyList<int>? Entries { get; init; }
    }

    /// <summary>What the work callback is given for each record.</summary>
    public enum PipelineData
    {
        /// <summary>Only the record's place and header-index line: the work reads the record itself (through Mutagen).</summary>
        None,
        /// <summary>The record's data (its subrecords, after the 24-byte header), inflated when it is compressed.</summary>
        Plain,
        /// <summary>The record's data as the file stores it, not inflated (a compressed record's first four bytes are its
        /// inflated size): for a pass that needs only the stored bytes (the Forms table's sizes, J30).</summary>
        Stored,
    }

    /// <summary>Why a record's data couldn't be given.</summary>
    public enum PipelineProblem { None, NotInflated, OutsideFile }

    /// <summary>One record as the work callback sees it. Its data is only good inside the callback.</summary>
    public readonly ref struct PipelineRecord
    {
        public PipelineRecord(int set, IndexedPlugin plugin, int entry, Entry header, ReadOnlySpan<byte> data, PipelineProblem problem, ReadOnlySpan<byte> head = default)
        { Set = set; Plugin = plugin; EntryIndex = entry; Header = header; Data = data; Problem = problem; Head = head; }
        /// <summary>The place of its <see cref="ReadSet"/> in the list given.</summary>
        public int Set { get; }
        public IndexedPlugin Plugin { get; }
        /// <summary>Its entry in the plugin's segment.</summary>
        public int EntryIndex { get; }
        public Entry Header { get; }
        /// <summary>The record's data, inflated (with <see cref="PipelineData.Plain"/>); empty with <see cref="PipelineData.None"/>
        /// or when <see cref="Problem"/> says why.</summary>
        public ReadOnlySpan<byte> Data { get; }
        public PipelineProblem Problem { get; }
        /// <summary>The record's 24-byte header as the file stores it (signature, data size, flags, FormID, version
        /// control, form version), with <see cref="PipelineData.Plain"/> or <see cref="PipelineData.Stored"/>; empty
        /// otherwise or when <see cref="Problem"/> is <see cref="PipelineProblem.OutsideFile"/>. The world link index
        /// (J32) reads the form version from it and gives Mutagen the record with some subrecords left out.</summary>
        public ReadOnlySpan<byte> Head { get; }
        public RecordRef Ref => new(Plugin.Id, EntryIndex);
    }

    /// <summary>The work done on one record: put zero or more results into <paramref name="output"/>. Called on many
    /// threads at once; the results come out in the records' order whatever thread made them.</summary>
    public delegate void PipelineWork<T>(in PipelineRecord record, List<T> output);

    public sealed class PipelineOptions
    {
        /// <summary>The threads SFSearch reads with everywhere: the cores, at most 8.</summary>
        public static int DefaultThreads => Math.Min(Environment.ProcessorCount, 8);

        /// <summary>Threads that work at once. 1: the records are read on the calling thread, one after another.</summary>
        public int Threads { get; init; } = DefaultThreads;
        public PipelineData Data { get; init; } = PipelineData.Plain;
        /// <summary>
        /// The memory the records being worked on may take together, as estimated by <see cref="CostPerByte"/>. A thread
        /// waits before a record that doesn't fit beside the ones in work; a record larger than the whole limit waits until
        /// it is alone and then runs alone. The caller gives what its job may use (SFSearch: JobContext.MemoryBudgetMB).
        /// </summary>
        public long MemoryBytes { get; init; } = 2L << 30;
        /// <summary>Memory a record takes while it is worked on, per byte of its plain data: 1 for its bytes alone (the
        /// inflate buffer), about 20 when the work reads it through Mutagen (<see cref="MutagenCostPerByte"/>).</summary>
        public double CostPerByte { get; init; } = 1;
        /// <summary>Measured on Starfield.esm's world records (2026-11-15): Mutagen's objects of a record and a walk of
        /// them take 10 to 30 times its plain size (the 51 MB navigation mesh info map: about 1.5 GB at its peak).</summary>
        public const double MutagenCostPerByte = 30;
        public CancellationToken Cancel { get; init; }
        /// <summary>A piece (the unit a thread takes) ends after this many records or this many stored bytes.</summary>
        public int PieceRecords { get; init; } = 4096;
        public long PieceBytes { get; init; } = 2L << 20;
        /// <summary>How many pieces may be finished or in work ahead of the one the caller waits for. Results come out in
        /// order, so a slow piece holds back the ones after it: with too few ahead, the threads stand idle behind it.
        /// Each finished piece holds its results until the caller takes them. 0: eight per thread.</summary>
        public int Ahead { get; init; }
        /// <summary>Filled while the pipeline runs: counts per set. Optional.</summary>
        public PipelineStats? Stats { get; init; }
    }

    /// <summary>What the pipeline read, per set: records, their stored bytes, how many were compressed.</summary>
    public sealed class PipelineStats
    {
        public long[] Records = Array.Empty<long>(), StoredBytes = Array.Empty<long>(), Compressed = Array.Empty<long>();
    }

    /// <summary>
    /// Reads a list of records on several threads and gives the work's results in the records' order: the same results,
    /// in the same order, as a read on one thread. Each plugin file is mapped for the run only (never kept mapped: Windows
    /// refuses to overwrite a mapped file). Records are taken in pieces; at most twice as many pieces as threads wait for
    /// the caller, and the records in work never hold more than <see cref="PipelineOptions.MemoryBytes"/> together.
    /// Cancelling the token stops the reading threads at the next record; the enumeration then throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public static unsafe class RecordPipeline
    {
        readonly record struct Piece(int Set, int From, int To);

        public static IEnumerable<T> Read<T>(IReadOnlyList<ReadSet> sets, PipelineWork<T> work, PipelineOptions? options = null)
        {
            var o = options ?? new PipelineOptions();
            var stats = o.Stats ?? new PipelineStats();
            stats.Records = new long[sets.Count]; stats.StoredBytes = new long[sets.Count]; stats.Compressed = new long[sets.Count];
            var files = new Mapped?[sets.Count];
            var byPlugin = new Dictionary<int, Mapped>();
            var budget = new Budget(Math.Max(1, o.MemoryBytes));
            try
            {
                for (int s = 0; s < sets.Count; s++)
                {
                    var p = sets[s].Plugin;
                    if (p.Segment == null) throw new InvalidOperationException($"{p.Name} has no header index.");
                    if (!byPlugin.TryGetValue(p.Id, out var m)) byPlugin[p.Id] = m = new Mapped(p.Path);
                    files[s] = m;
                }
                var pieces = Pieces(sets, o);
                // A file is unmapped as soon as its last piece is done, so the files of a search over many plugins
                // aren't all mapped at the end
                foreach (var piece in pieces) files[piece.Set]!.Pieces++;
                foreach (var m in byPlugin.Values) if (m.Pieces == 0) m.Dispose();
                IEnumerable<List<T>> done = o.Threads <= 1
                    ? Sequential(pieces, p => Work(sets, files, p, work, o, budget, stats))
                    : Ordered(pieces, p => Work(sets, files, p, work, o, budget, stats), o.Threads, o.Ahead > 0 ? o.Ahead : 8 * o.Threads, o.Cancel);
                foreach (var list in done)
                    foreach (var item in list) yield return item;
                o.Cancel.ThrowIfCancellationRequested();
            }
            finally
            {
                foreach (var m in byPlugin.Values) m.Dispose();
            }
        }

        /// <summary>The same, with every result in one list.</summary>
        public static List<T> ReadAll<T>(IReadOnlyList<ReadSet> sets, PipelineWork<T> work, PipelineOptions? options = null) => new(Read(sets, work, options));

        static IEnumerable<List<T>> Sequential<T>(List<Piece> pieces, Func<Piece, List<T>> run)
        {
            foreach (var p in pieces) yield return run(p);
        }

        /// <summary>The pieces in order: each ends after <see cref="PipelineOptions.PieceRecords"/> records or
        /// <see cref="PipelineOptions.PieceBytes"/> stored bytes of the chosen types, and never spans two sets.</summary>
        static List<Piece> Pieces(IReadOnlyList<ReadSet> sets, PipelineOptions o)
        {
            var pieces = new List<Piece>();
            for (int s = 0; s < sets.Count; s++)
            {
                var set = sets[s]; var seg = set.Plugin.Segment!;
                int count = set.Entries?.Count ?? seg.Count;
                int from = 0, records = 0; long bytes = 0;
                for (int k = 0; k < count; k++)
                {
                    var e = seg[set.Entries != null ? set.Entries[k] : k];
                    if (set.Signatures != null && !set.Signatures[e.Signature]) continue;
                    records++; bytes += e.Size;
                    if (records >= o.PieceRecords || bytes >= o.PieceBytes)
                    {
                        pieces.Add(new Piece(s, from, k + 1));
                        from = k + 1; records = 0; bytes = 0;
                    }
                }
                if (records > 0) pieces.Add(new Piece(s, from, count));
            }
            return pieces;
        }

        static List<T> Work<T>(IReadOnlyList<ReadSet> sets, Mapped?[] files, Piece piece, PipelineWork<T> work, PipelineOptions o, Budget budget, PipelineStats stats)
        {
            var output = new List<T>();
            var set = sets[piece.Set]; var seg = set.Plugin.Segment!; var file = files[piece.Set]!;
            byte[]? plain = null;
            long recs = 0, size = 0, zipped = 0;
            // The memory this thread holds for its records: kept from record to record while they are small, so most
            // records don't wait on the shared count, and given back after a big one
            long lease = 0;
            try
            {
                for (int k = piece.From; k < piece.To; k++)
                {
                    o.Cancel.ThrowIfCancellationRequested();
                    int i = set.Entries != null ? set.Entries[k] : k;
                    var e = seg[i];
                    if (set.Signatures != null && !set.Signatures[e.Signature]) continue;
                    recs++; size += e.Size;
                    bool inside = e.Offset + 24 + e.Size <= file.Length;
                    long plainSize = e.Size;
                    if (e.IsCompressed)
                    {
                        zipped++;
                        if (inside && e.Size >= 4) plainSize = *(uint*)(file.Pointer + e.Offset + 24);
                    }
                    long cost = (long)((plainSize + 24) * o.CostPerByte) + 4096;
                    if (cost > lease) { budget.Give(lease); lease = 0; lease = budget.Take(Math.Max(cost, Math.Min(budget.Keep, cost * 4)), o.Cancel); }
                    try
                    {
                        if (!inside || e.Size < 4 && e.IsCompressed)
                        {
                            work(new PipelineRecord(piece.Set, set.Plugin, i, e, default, PipelineProblem.OutsideFile), output);
                            continue;
                        }
                        if (o.Data == PipelineData.None) { work(new PipelineRecord(piece.Set, set.Plugin, i, e, default, PipelineProblem.None), output); continue; }
                        byte* data = file.Pointer + e.Offset + 24;
                        var head = new ReadOnlySpan<byte>(data - 24, 24);
                        if (!e.IsCompressed || o.Data == PipelineData.Stored) { work(new PipelineRecord(piece.Set, set.Plugin, i, e, new ReadOnlySpan<byte>(data, (int)e.Size), PipelineProblem.None, head), output); continue; }
                        if (plainSize > int.MaxValue - 64) { work(new PipelineRecord(piece.Set, set.Plugin, i, e, default, PipelineProblem.NotInflated, head), output); continue; }
                        if (plain == null || plain.Length < plainSize)
                        {
                            if (plain != null) ArrayPool<byte>.Shared.Return(plain);
                            plain = ArrayPool<byte>.Shared.Rent((int)Math.Max(plainSize, 1 << 16));
                        }
                        bool ok;
                        try
                        {
                            using var packed = new UnmanagedMemoryStream(data + 4, e.Size - 4);
                            using var zip = new ZLibStream(packed, CompressionMode.Decompress);
                            zip.ReadExactly(plain, 0, (int)plainSize);
                            ok = true;
                        }
                        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException) { ok = false; }
                        work(ok ? new PipelineRecord(piece.Set, set.Plugin, i, e, plain.AsSpan(0, (int)plainSize), PipelineProblem.None, head)
                                : new PipelineRecord(piece.Set, set.Plugin, i, e, default, PipelineProblem.NotInflated, head), output);
                    }
                    finally { if (lease > budget.Keep) { budget.Give(lease); lease = 0; } }
                }
            }
            finally
            {
                budget.Give(lease);
                file.Release();
                if (plain != null) ArrayPool<byte>.Shared.Return(plain);
                Interlocked.Add(ref stats.Records[piece.Set], recs); Interlocked.Add(ref stats.StoredBytes[piece.Set], size); Interlocked.Add(ref stats.Compressed[piece.Set], zipped);
            }
            return output;
        }

        /// <summary>Runs <paramref name="run"/> on up to <paramref name="threads"/> threads and gives the results in the
        /// order of the pieces. At most <paramref name="ahead"/> pieces are finished or in work ahead of the consumer.</summary>
        static IEnumerable<TOut> Ordered<TOut>(List<Piece> items, Func<Piece, TOut> run, int threads, int ahead, CancellationToken cancel)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            using var queue = new BlockingCollection<Task<TOut>>(Math.Max(ahead, threads));
            using var slots = new SemaphoreSlim(threads);
            var producer = Task.Run(() =>
            {
                try
                {
                    foreach (var item in items)
                    {
                        slots.Wait(stop.Token);
                        var it = item;
                        var task = Task.Run(() => { try { return run(it); } finally { try { slots.Release(); } catch (ObjectDisposedException) { } } });
                        try { queue.Add(task, stop.Token); }
                        catch (OperationCanceledException) { try { task.Wait(); } catch (AggregateException) { } throw; }
                    }
                }
                catch (OperationCanceledException) { }
                finally { queue.CompleteAdding(); }
            });
            try
            {
                foreach (var task in queue.GetConsumingEnumerable())
                {
                    cancel.ThrowIfCancellationRequested();
                    yield return task.GetAwaiter().GetResult();
                }
            }
            finally
            {
                stop.Cancel();
                try { producer.Wait(); } catch (AggregateException) { }
                // The tasks still running finish before the files they read are unmapped
                while (queue.TryTake(out var left)) { try { left.Wait(); } catch (AggregateException) { } }
            }
        }

        /// <summary>The memory the records in work hold, by their estimates.</summary>
        sealed class Budget
        {
            readonly long _cap;
            long _used;
            /// <summary>What a thread may keep between records: a sixteenth of the limit, at most 64 MB.</summary>
            public readonly long Keep;
            public Budget(long cap) { _cap = cap; Keep = Math.Min(cap / 16, 64L << 20); }

            long _nextTicket, _serving;

            /// <summary>Waits until <paramref name="want"/> fits beside what is in work (a record over the whole limit
            /// waits until nothing else is in work). Takers are served in turn, so a big record isn't starved by small
            /// ones. Returns what it took, to give back.</summary>
            public long Take(long want, CancellationToken cancel)
            {
                long take = Math.Min(want, _cap);
                lock (this)
                {
                    long ticket = _nextTicket++;
                    try
                    {
                        while (ticket != _serving || _used > 0 && _used + take > _cap)
                        {
                            cancel.ThrowIfCancellationRequested();
                            Monitor.Wait(this, 100);
                        }
                    }
                    finally
                    {
                        // Served, or given up (cancelled): the next in turn may go
                        if (ticket == _serving) { _serving++; Monitor.PulseAll(this); }
                        else _skipped.Add(ticket);
                        while (_skipped.Remove(_serving)) _serving++;
                    }
                    _used += take;
                }
                return take;
            }

            readonly HashSet<long> _skipped = new();

            public void Give(long held)
            {
                if (held <= 0) return;
                lock (this) { _used -= held; Monitor.PulseAll(this); }
            }
        }

        /// <summary>A plugin's file mapped for one run.</summary>
        sealed class Mapped : IDisposable
        {
            readonly FileStream? _fs;
            readonly MemoryMappedFile? _mmf;
            readonly MemoryMappedViewAccessor? _view;
            public readonly byte* Pointer;
            public readonly long Length;
            /// <summary>Pieces still to read from it; the last one unmaps it.</summary>
            public int Pieces;
            int _disposed;

            public void Release() { if (Interlocked.Decrement(ref Pieces) == 0) Dispose(); }

            public Mapped(string path)
            {
                _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Length = _fs.Length;
                if (Length == 0) return;
                _mmf = MemoryMappedFile.CreateFromFile(_fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
                _view = _mmf.CreateViewAccessor(0, Length, MemoryMappedFileAccess.Read);
                byte* p = null;
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                Pointer = p + _view.PointerOffset;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                if (_view != null) { _view.SafeMemoryMappedViewHandle.ReleasePointer(); _view.Dispose(); }
                _mmf?.Dispose();
                _fs?.Dispose();
            }
        }
    }
}
