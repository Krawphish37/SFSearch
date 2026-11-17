using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

// Moved here from SFSearch\Engine (fix g-sql, 2026-11-13) so the SQL layer can search bytes too: a query over Links
// WHERE ToKey = '…' for plugins that have no link file yet (GAPS S3). Since J29 (2026-11-15) it reads through the shared
// record pipeline (RecordPipeline.cs), many plugins at once, and can rule out hits by the schema's subrecords.
namespace SFSearch.Index
{
    /// <summary>
    /// The byte search (design\agent-first\ARCHITECTURE.md section 5, "Links", way 2): which records of a plugin hold
    /// the four bytes of a FormID as that plugin's file writes it. Compressed records are inflated first. It reads the
    /// file through the header index's entries, so no record header is parsed again. A hit only says "the bytes are
    /// there": the caller confirms each one by reading that record through Mutagen.
    /// </summary>
    /// <summary>
    /// The bytes a plugin's file can write for one record's FormID: Mutagen's stored forms of it
    /// (<c>SeparatedMasterPackage.EnumerateStoredForms</c> with the indexes past the list, C58 W8, through
    /// <see cref="FormIdResolver.StoredForms"/>). A master's record has one form. The plugin's own record has many: any
    /// index at or past the masters of a numbering reads as the plugin's own, in each of the three numberings its local ID
    /// fits. atom74.esm writes its own records as FE00083A, a plugin made as Full and marked Small later as 0100083A; both
    /// are the same record. The matcher below is the search's speed code: it finds those forms as ranges (an index at or
    /// past Mutagen's own index of each numbering) instead of looking up each of them; sfsindex-tests formid-forms holds
    /// it equal to Mutagen's list.
    /// </summary>
    public sealed class FormIdPattern
    {
        readonly uint? _exact;
        readonly uint _local;
        readonly int _full, _medium, _small;

        FormIdPattern(uint? exact, uint local, int full, int medium, int small) { _exact = exact; _local = local; _full = full; _medium = medium; _small = small; }

        /// <summary>Null when the plugin can't write that FormID at all (the owner isn't one of its masters, or the local ID doesn't fit the owner's size).</summary>
        public static FormIdPattern? For(IndexedPlugin plugin, int owner, uint localId) => For(plugin.FormIds, owner, localId);

        /// <summary>The same for a plugin's resolver.</summary>
        public static FormIdPattern? For(FormIdResolver ids, int owner, uint localId)
        {
            if (owner == ids.SelfOwner) return new FormIdPattern(null, localId, ids.FullMasters, ids.MediumMasters, ids.SmallMasters);
            return ids.StoredForms(owner, localId, false).FirstOrDefault() is var raw && raw != 0 ? new FormIdPattern(raw, localId, 0, 0, 0) : null;
        }

        public bool IsOwn => _exact == null;

        public bool Matches(ReadOnlySpan<byte> data) => Next(data, 0) >= 0;

        /// <summary>Where the first match at or after <paramref name="from"/> starts: the first of its four bytes. -1: none.</summary>
        public int Next(ReadOnlySpan<byte> data, int from)
        {
            if (from < 0) from = 0;
            Span<byte> n = stackalloc byte[4];
            if (_exact is { } exact)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(n, exact);
                if (from > data.Length - 4) return -1;
                int i = data[from..].IndexOf(n);
                return i < 0 ? -1 : from + i;
            }
            int best = -1;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(n, _local);
            // By the top byte: the local ID's three bytes, then an index at or past the Full masters (FD and FE are the other two numberings)
            var three = n[..3];
            for (int f = from; f + 4 <= data.Length;)
            {
                int i = data[f..].IndexOf(three);
                if (i < 0) break;
                int at = f + i;
                if (at + 3 < data.Length && data[at + 3] >= _full && data[at + 3] < 0xFD) { best = at; break; }
                f = at + 1;
            }
            // FD xx yyyy: the low two bytes, an index at or past the Medium masters, then FD
            if (_local <= 0xFFFF)
            {
                var two = n[..2];
                for (int f = from; f + 4 <= data.Length && (best < 0 || f < best);)
                {
                    int i = data[f..].IndexOf(two);
                    if (i < 0) break;
                    int at = f + i;
                    if (best >= 0 && at >= best) break;
                    if (at + 3 < data.Length && data[at + 3] == 0xFD && data[at + 2] >= _medium) { best = at; break; }
                    f = at + 1;
                }
            }
            // FE xxx yyy: twelve bits of local ID, twelve of index at or past the Small masters, then FE
            if (_local <= 0xFFF)
            {
                byte low = (byte)_local, high = (byte)(_local >> 8);
                for (int f = Math.Max(3, from + 3); f < data.Length;)
                {
                    int i = data[f..].IndexOf((byte)0xFE);
                    if (i < 0) break;
                    int at = f + i;
                    if (best >= 0 && at - 3 >= best) break;
                    if (data[at - 3] == low && (data[at - 2] & 0x0F) == high && ((data[at - 2] >> 4) | (data[at - 1] << 4)) >= _small) { best = at - 3; break; }
                    f = at + 1;
                }
            }
            return best;
        }
    }

    /// <summary>
    /// Which subrecords of each record type can't hold a link, by the schema (SFSearch.Records\LinkSubrecords.cs): a
    /// byte hit that sits only in such subrecords is not a link, and needs no read through Mutagen to say so. Two lists
    /// per type: the record's own subrecords, and those inside a component (between BFCB and BFCE), where the same
    /// signature is another field (a placed object's DATA is its position; a component's DATA can hold a worldspace).
    /// </summary>
    public sealed class NoLinkSubrecords
    {
        /// <summary>One record type's lists, each the subrecords' four bytes read little-endian. Null: none is ruled out there.</summary>
        public sealed record Sets(uint[]? Outer, uint[]? InComponent);

        readonly Func<string, (uint[]? Outer, uint[]? InComponent)> _of;
        readonly System.Collections.Concurrent.ConcurrentDictionary<string, Sets?> _bySignature = new(StringComparer.Ordinal);

        /// <param name="of">A signature's two lists (null: none ruled out there); asked once per signature.</param>
        public NoLinkSubrecords(Func<string, (uint[]? Outer, uint[]? InComponent)> of) { _of = of; }

        /// <summary>For each signature of the segment (by its place) that <paramref name="only"/> marks (null: all): its
        /// lists, or null when every subrecord must be read.</summary>
        public Sets?[] For(Segment seg, bool[]? only = null)
        {
            var r = new Sets?[seg.Signatures.Count];
            for (int i = 0; i < r.Length; i++)
                if (only == null || only[i])
                    r[i] = _bySignature.GetOrAdd(seg.Signatures[i].Signature, sig => _of(sig) is var (o, c) && (o != null || c != null) ? new Sets(o, c) : null);
            return r;
        }

        public static uint Sig(string s) => (uint)(s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24);
        const uint Xxxx = 0x58585858, Bfcb = 0x42434642, Bfce = 0x45434642;

        /// <summary>
        /// True when a match of <paramref name="pattern"/> in a record's data can be a link: it lies inside a subrecord that
        /// isn't ruled out in its context. A match across a subrecord's header lies in no field. When the subrecords can't
        /// be walked to the end (a cut record), the answer is true: Mutagen decides.
        /// </summary>
        public static bool AnyMayLink(ReadOnlySpan<byte> data, FormIdPattern pattern, Sets sets)
        {
            int at = pattern.Next(data, 0);
            if (at < 0) return false;
            int pos = 0; long longForm = -1; bool inComponent = false;
            while (at >= 0)
            {
                // The subrecord the match starts in, walking on from the last one
                while (true)
                {
                    if (pos + 6 > data.Length) return true;
                    uint sig = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
                    long len = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 4)..]);
                    if (longForm >= 0) { len = longForm; longForm = -1; }
                    else if (sig == Xxxx && len == 4 && pos + 10 <= data.Length) { longForm = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 6)..]); pos += 10; continue; }
                    long start = pos + 6, end = start + len;
                    if (end > data.Length) return true;
                    if (sig == Bfcb) inComponent = true;
                    if (at < end)
                    {
                        var noLink = inComponent ? sets.InComponent : sets.Outer;
                        if (at >= start && at + 4 <= end && (noLink == null || Array.IndexOf(noLink, sig) < 0)) return true;
                        break;
                    }
                    if (sig == Bfce) inComponent = false;
                    pos = (int)end;
                }
                at = pattern.Next(data, at + 1);
            }
            return false;
        }
    }

    public static class ByteSearch
    {
        public sealed class Result
        {
            /// <summary>Entries of the plugin's segment whose data holds the bytes, in entry order.</summary>
            public List<int> Hits = new();
            /// <summary>Entries that are compressed and couldn't be inflated: they weren't searched.</summary>
            public List<int> NotInflated = new();
            /// <summary>Entries whose data holds the bytes only in subrecords that hold no link field (by the schema's
            /// <see cref="NoLinkSubrecords"/>): not links, told without Mutagen. Empty when no such list was given.</summary>
            public List<int> NoLinkField = new();
            public long Records, Bytes, Compressed;
            public double Ms;
        }

        /// <summary>One plugin's part of a search of many.</summary>
        /// <param name="Pattern">The FormID as the plugin's file can write it (its own master numbering).</param>
        /// <param name="Signatures">Which record types to search, by their place in the segment's signature table. Null: all.</param>
        /// <param name="Only">Search these entries only (sorted). Null: every entry of the chosen types.</param>
        /// <param name="NoLink">For each signature of the segment: the subrecords that hold no link field (<see cref="NoLinkSubrecords.For"/>). Null: none is ruled out.</param>
        public sealed record Job(IndexedPlugin Plugin, FormIdPattern Pattern, bool[]? Signatures = null, IReadOnlyList<int>? Only = null, NoLinkSubrecords.Sets?[]? NoLink = null);

        /// <param name="pattern">The FormID as the plugin's file can write it (its own master numbering).</param>
        /// <param name="signatures">Which record types to search, by their place in the segment's signature table. Null: all.</param>
        /// <param name="only">Search these entries only (sorted). Null: every entry of the chosen types.</param>
        public static Result Search(IndexedPlugin plugin, FormIdPattern pattern, bool[]? signatures = null, IReadOnlyList<int>? only = null, int threads = 0)
        {
            if (plugin.Segment == null) throw new InvalidOperationException($"{plugin.Name} has no header index.");
            return SearchMany(new[] { new Job(plugin, pattern, signatures, only) }, threads, out _)[0];
        }

        enum Found : byte { Hit, NotInflated, NoLinkField }

        /// <summary>
        /// Many plugins searched at once: their records go through one <see cref="RecordPipeline"/> on all its threads, so
        /// a small plugin doesn't wait for a big one and a big one is split in pieces. Each job's result is the one
        /// <see cref="Search"/> gives for it alone. <paramref name="ms"/>: the time of the whole search.
        /// </summary>
        public static Result[] SearchMany(IReadOnlyList<Job> jobs, int threads, out double ms, CancellationToken cancel = default)
        {
            var clock = Stopwatch.StartNew();
            var results = new Result[jobs.Count];
            for (int j = 0; j < jobs.Count; j++) results[j] = new Result();
            var sets = new List<ReadSet>(jobs.Count);
            var jobOf = new List<int>(jobs.Count);
            for (int j = 0; j < jobs.Count; j++)
            {
                var p = jobs[j].Plugin;
                if (p.Segment == null) throw new InvalidOperationException($"{p.Name} has no header index.");
                if (p.Segment.Count == 0 || p.Stamp.Length == 0) continue;
                sets.Add(new ReadSet { Plugin = p, Signatures = jobs[j].Signatures, Entries = jobs[j].Only });
                jobOf.Add(j);
            }
            var stats = new PipelineStats();
            var options = new PipelineOptions { Threads = threads <= 0 ? PipelineOptions.DefaultThreads : threads, Data = PipelineData.Plain, Cancel = cancel, Stats = stats };
            foreach (var (set, entry, found) in RecordPipeline.Read<(int Set, int Entry, Found Found)>(sets, (in PipelineRecord r, List<(int, int, Found)> output) =>
            {
                if (r.Problem == PipelineProblem.NotInflated) { output.Add((r.Set, r.EntryIndex, Found.NotInflated)); return; }
                if (r.Problem != PipelineProblem.None || r.Data.Length < 4) return;
                var job = jobs[jobOf[r.Set]];
                if (!job.Pattern.Matches(r.Data)) return;
                var noLink = job.NoLink?[r.Header.Signature];
                output.Add((r.Set, r.EntryIndex, noLink == null || NoLinkSubrecords.AnyMayLink(r.Data, job.Pattern, noLink) ? Found.Hit : Found.NoLinkField));
            }, options))
            {
                var res = results[jobOf[set]];
                (found == Found.Hit ? res.Hits : found == Found.NotInflated ? res.NotInflated : res.NoLinkField).Add(entry);
            }
            ms = clock.Elapsed.TotalMilliseconds;
            for (int s = 0; s < sets.Count; s++)
            {
                var res = results[jobOf[s]];
                res.Hits.Sort(); res.NotInflated.Sort(); res.NoLinkField.Sort();
                res.Records = stats.Records[s]; res.Bytes = stats.StoredBytes[s]; res.Compressed = stats.Compressed[s];
                res.Ms = ms;
            }
            return results;
        }
    }
}
