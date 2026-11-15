using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// Moved here from SFSearch\Engine (fix g-sql, 2026-11-13) so the SQL layer can search bytes too: a query over Links
// WHERE ToKey = '…' for plugins that have no link file yet (GAPS S3). The code is unchanged.
namespace SFSearch.Index
{
    /// <summary>
    /// The byte search (design\agent-first\ARCHITECTURE.md section 5, "Links", way 2): which records of a plugin hold
    /// the four bytes of a FormID as that plugin's file writes it. Compressed records are inflated first. It reads the
    /// file through the header index's entries, so no record header is parsed again. A hit only says "the bytes are
    /// there": the caller confirms each one by reading that record through Mutagen.
    /// </summary>
    /// <summary>
    /// The bytes a plugin's file can write for one record's FormID. A master's record has one form: its place in the
    /// numbering of the master's size (Full by the top byte, Medium FD xx, Small FE xxx). The plugin's own record has
    /// many: any index at or past the masters of a numbering reads as the plugin's own, in each of the three
    /// numberings its local ID fits. atom74.esm writes its own records as FE00083A, a plugin made as Full and marked
    /// Small later as 0100083A; both are the same record, and the header index and Mutagen read both. So does this.
    /// </summary>
    public sealed class FormIdPattern
    {
        readonly uint? _exact;
        readonly uint _local;
        readonly int _full, _medium, _small;

        FormIdPattern(uint? exact, uint local, int full, int medium, int small) { _exact = exact; _local = local; _full = full; _medium = medium; _small = small; }

        /// <summary>Null when the plugin can't write that FormID at all (the owner isn't one of its masters, or the local ID doesn't fit the owner's size).</summary>
        public static FormIdPattern? For(IndexedPlugin plugin, int owner, uint localId)
        {
            var ids = plugin.FormIds;
            if (owner == ids.SelfOwner) return new FormIdPattern(null, localId, ids.FullMasters, ids.MediumMasters, ids.SmallMasters);
            return ids.Raw(owner, localId) is { } raw ? new FormIdPattern(raw, localId, 0, 0, 0) : null;
        }

        public bool IsOwn => _exact == null;

        public bool Matches(ReadOnlySpan<byte> data)
        {
            Span<byte> n = stackalloc byte[4];
            if (_exact is { } exact)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(n, exact);
                return data.IndexOf(n) >= 0;
            }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(n, _local);
            // By the top byte: the local ID's three bytes, then an index at or past the Full masters (FD and FE are the other two numberings)
            var three = n[..3];
            for (int from = 0; from + 4 <= data.Length;)
            {
                int i = data[from..].IndexOf(three);
                if (i < 0) break;
                int at = from + i;
                if (at + 3 < data.Length && data[at + 3] >= _full && data[at + 3] < 0xFD) return true;
                from = at + 1;
            }
            // FD xx yyyy: the low two bytes, an index at or past the Medium masters, then FD
            if (_local <= 0xFFFF)
            {
                var two = n[..2];
                for (int from = 0; from + 4 <= data.Length;)
                {
                    int i = data[from..].IndexOf(two);
                    if (i < 0) break;
                    int at = from + i;
                    if (at + 3 < data.Length && data[at + 3] == 0xFD && data[at + 2] >= _medium) return true;
                    from = at + 1;
                }
            }
            // FE xxx yyy: twelve bits of local ID, twelve of index at or past the Small masters, then FE
            if (_local <= 0xFFF)
            {
                byte low = (byte)_local, high = (byte)(_local >> 8);
                for (int from = 3; from < data.Length;)
                {
                    int i = data[from..].IndexOf((byte)0xFE);
                    if (i < 0) break;
                    int at = from + i;
                    if (data[at - 3] == low && (data[at - 2] & 0x0F) == high && ((data[at - 2] >> 4) | (data[at - 1] << 4)) >= _small) return true;
                    from = at + 1;
                }
            }
            return false;
        }
    }

    public static unsafe class ByteSearch
    {
        public sealed class Result
        {
            /// <summary>Entries of the plugin's segment whose data holds the bytes, in entry order.</summary>
            public List<int> Hits = new();
            /// <summary>Entries that are compressed and couldn't be inflated: they weren't searched.</summary>
            public List<int> NotInflated = new();
            public long Records, Bytes, Compressed;
            public double Ms;
        }

        /// <param name="pattern">The FormID as the plugin's file can write it (its own master numbering).</param>
        /// <param name="signatures">Which record types to search, by their place in the segment's signature table. Null: all.</param>
        /// <param name="only">Search these entries only (sorted). Null: every entry of the chosen types.</param>
        public static Result Search(IndexedPlugin plugin, FormIdPattern pattern, bool[]? signatures = null, IReadOnlyList<int>? only = null, int threads = 0)
        {
            var clock = Stopwatch.StartNew();
            var seg = plugin.Segment ?? throw new InvalidOperationException($"{plugin.Name} has no header index.");
            var result = new Result();
            if (threads <= 0) threads = Math.Min(Environment.ProcessorCount, 8);
            using var fs = new FileStream(plugin.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = fs.Length;
            if (length == 0 || seg.Count == 0) return result;
            using var mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
            using var view = mmf.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            try
            {
                p += view.PointerOffset;
                int count = only?.Count ?? seg.Count;
                const int Piece = 1 << 14;
                int pieces = (count + Piece - 1) / Piece;
                var hits = new List<int>[pieces]; var bad = new List<int>[pieces];
                long records = 0, bytes = 0, compressed = 0;
                byte* file = p;
                void Work(int n)
                {
                    byte[] plain = ArrayPool<byte>.Shared.Rent(1 << 16);
                    List<int>? found = null, failed = null;
                    long recs = 0, size = 0, zipped = 0;
                    try
                    {
                        int to = Math.Min(count, (n + 1) * Piece);
                        for (int k = n * Piece; k < to; k++)
                        {
                            int i = only != null ? only[k] : k;
                            var e = seg[i];
                            if (signatures != null && !signatures[e.Signature]) continue;
                            recs++; size += e.Size;
                            if (e.Size < 4 || e.Offset + 24 + e.Size > length) continue;
                            byte* data = file + e.Offset + 24;
                            ReadOnlySpan<byte> span;
                            if (e.IsCompressed)
                            {
                                zipped++;
                                uint plainSize = *(uint*)data;
                                if (plainSize > int.MaxValue - 64) { (failed ??= new()).Add(i); continue; }
                                if (plain.Length < plainSize) { ArrayPool<byte>.Shared.Return(plain); plain = ArrayPool<byte>.Shared.Rent((int)plainSize); }
                                try
                                {
                                    using var packed = new UnmanagedMemoryStream(data + 4, e.Size - 4);
                                    using var zip = new ZLibStream(packed, CompressionMode.Decompress);
                                    zip.ReadExactly(plain, 0, (int)plainSize);
                                }
                                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException) { (failed ??= new()).Add(i); continue; }
                                span = plain.AsSpan(0, (int)plainSize);
                            }
                            else span = new ReadOnlySpan<byte>(data, (int)e.Size);
                            if (pattern.Matches(span)) (found ??= new()).Add(i);
                        }
                    }
                    finally { ArrayPool<byte>.Shared.Return(plain); }
                    hits[n] = found ?? new(); bad[n] = failed ?? new();
                    Interlocked.Add(ref records, recs); Interlocked.Add(ref bytes, size); Interlocked.Add(ref compressed, zipped);
                }
                if (pieces <= 1 || threads == 1) for (int n = 0; n < pieces; n++) Work(n);
                else Parallel.For(0, pieces, new ParallelOptions { MaxDegreeOfParallelism = threads }, Work);
                foreach (var h in hits) result.Hits.AddRange(h);
                foreach (var b in bad) result.NotInflated.AddRange(b);
                result.Hits.Sort(); result.NotInflated.Sort();
                result.Records = records; result.Bytes = bytes; result.Compressed = compressed;
            }
            finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
            result.Ms = clock.Elapsed.TotalMilliseconds;
            return result;
        }
    }
}
