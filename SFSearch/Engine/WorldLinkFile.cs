using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using SFSearch.Index;

namespace SFSearch.Engine
{
    /// <summary>One world link while a world link file is made: the target, the record that holds it, the field, and how
    /// many times that record holds it in that field. 24 bytes.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8, Size = 24)]
    public readonly struct WorldRow
    {
        /// <summary>owner &lt;&lt; 24 | local ID, the owner a place in the plugin's own name table (its masters, then itself), as <see cref="LinkRow.Target"/>.</summary>
        public readonly ulong Target;
        public readonly uint From;
        public readonly uint Count;
        public readonly int Field;
        public WorldRow(ulong target, uint from, int field, uint count) { Target = target; From = from; Field = field; Count = count; }
    }

    /// <summary>One posting of a world link file: the record (its entry in the plugin's segment), the field, and the
    /// count (<see cref="WorldLinkFile.Overflow"/>: the count is in the overflow list). 8 bytes.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 2, Size = 8)]
    public readonly struct WorldPosting
    {
        public readonly uint From;
        public readonly ushort Field;
        public readonly ushort Count;
        public WorldPosting(uint from, ushort field, ushort count) { From = from; Field = field; Count = count; }
    }

    /// <summary>One target of a world link file and where its postings are. 16 bytes.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
    public readonly struct WorldTarget
    {
        public readonly ulong Target;
        public readonly uint First;
        public readonly uint Count;
        public WorldTarget(ulong target, uint first, uint count) { Target = target; First = first; Count = count; }
    }

    /// <summary>What a build found in one plugin's world data, ready to be written.</summary>
    public sealed class WorldLinkContent
    {
        public WorldTarget[] Targets = Array.Empty<WorldTarget>();
        public WorldPosting[] Postings = Array.Empty<WorldPosting>();
        /// <summary>Posting index and its count, sorted by posting, for every count of 65,535 or more.</summary>
        public (uint Posting, uint Count)[] Overflow = Array.Empty<(uint, uint)>();
        public List<string> Fields = new();
        /// <summary>World records neither a byte reader nor Mutagen could read (sorted): refs searches their bytes.</summary>
        public int[] Unread = Array.Empty<int>();
        public string UnreadWhy = "";
        public int Records;
        public long Bytes;
        /// <summary>Records whose bulky subrecords the byte readers read (the rest through Mutagen).</summary>
        public int ByBytes;
        public long Links;
        public double Seconds;
    }

    /// <summary>
    /// A plugin's world link index (J32, the J1 plan with STEP1 4.6): every link of every world record (cells, placed
    /// objects and NPCs, navmeshes, the navmesh info map, surface trees, worldspaces and the other records under cells
    /// and worldspaces), stored once per target, record and field with a count, as a read-only memory-mapped file in
    /// indexes\world. Built only when the user asks (indexes build world), kept between service starts, named by the
    /// plugin's reading key and the build that made it, so a changed plugin is never answered from an old file.
    /// It holds where links are, never record data.
    /// Layout: a 192-byte header; the field paths (u16 length + UTF-8); the unread entries' text; the targets (sorted,
    /// 16 bytes each); the postings (by target, then record, 8 bytes each); the overflow counts; the unread entries.
    /// </summary>
    public sealed unsafe class WorldLinkFile : IDisposable
    {
        public const int FormatVersion = 1;
        const ulong Magic = 0x31304B4C57534653; // "SFSWLK01"
        const int HeaderSize = 192;
        /// <summary>A posting's count field holds this when the count is in the overflow list.</summary>
        public const ushort Overflow = 0xFFFF;

        readonly FileStream _file;
        readonly MemoryMappedFile? _map;
        readonly MemoryMappedViewAccessor? _view;
        byte* _p;
        readonly WorldTarget* _targets;
        readonly WorldPosting* _postings;
        readonly ulong* _overflow; // posting << 32 | count, sorted
        readonly int* _unread;

        public string Path { get; }
        public Key128 ReadingKey { get; }
        public int TargetCount { get; }
        public int PostingCount { get; }
        public int OverflowCount { get; }
        public IReadOnlyList<string> Fields { get; }
        public int UnreadCount { get; }
        public string UnreadWhy { get; } = "";
        public int Records { get; }
        public long Bytes { get; }
        public int ByBytes { get; }
        public long Links { get; }
        public double BuildSeconds { get; }
        public long FileBytes { get; }

        public static string PathFor(string folder, Key128 readingKey, string builderKey) => System.IO.Path.Combine(folder, $"{readingKey}-{builderKey}.wlinks");

        WorldLinkFile(string path)
        {
            Path = path;
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            try
            {
                FileBytes = _file.Length;
                if (FileBytes < HeaderSize) throw new InvalidDataException("world link file too short");
                _map = MemoryMappedFile.CreateFromFile(_file, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
                _view = _map.CreateViewAccessor(0, FileBytes, MemoryMappedFileAccess.Read);
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _p);
                _p += _view.PointerOffset;
                var h = new ReadOnlySpan<byte>(_p, HeaderSize);
                if (Rd<ulong>(h, 0) != Magic || Rd<int>(h, 8) != FormatVersion || Rd<long>(h, 16) != FileBytes) throw new InvalidDataException("not a world link file of this version, or cut short");
                ReadingKey = new Key128(Rd<ulong>(h, 24), Rd<ulong>(h, 32));
                TargetCount = Rd<int>(h, 40); PostingCount = Rd<int>(h, 44); OverflowCount = Rd<int>(h, 48);
                int fieldCount = Rd<int>(h, 52); UnreadCount = Rd<int>(h, 56);
                Records = Rd<int>(h, 60); Bytes = Rd<long>(h, 64); ByBytes = Rd<int>(h, 72); Links = Rd<long>(h, 80);
                BuildSeconds = Rd<double>(h, 88);
                long oFields = Rd<long>(h, 96), oText = Rd<long>(h, 104), oTargets = Rd<long>(h, 112), oPostings = Rd<long>(h, 120), oOverflow = Rd<long>(h, 128), oUnread = Rd<long>(h, 136);
                if (oTargets + (long)TargetCount * sizeof(WorldTarget) > FileBytes || oPostings + (long)PostingCount * sizeof(WorldPosting) > FileBytes
                    || oOverflow + (long)OverflowCount * 8 > FileBytes || oUnread + (long)UnreadCount * 4 > FileBytes)
                    throw new InvalidDataException("world link file sections run past its end");
                var fields = new string[fieldCount];
                byte* q = _p + oFields;
                for (int i = 0; i < fieldCount; i++) { int len = *(ushort*)q; fields[i] = Encoding.UTF8.GetString(q + 2, len); q += 2 + len; }
                Fields = fields;
                q = _p + oText;
                int n = *(ushort*)q; UnreadWhy = Encoding.UTF8.GetString(q + 2, n);
                _targets = (WorldTarget*)(_p + oTargets);
                _postings = (WorldPosting*)(_p + oPostings);
                _overflow = (ulong*)(_p + oOverflow);
                _unread = (int*)(_p + oUnread);
            }
            catch { Dispose(); throw; }
            Fields ??= Array.Empty<string>();
        }

        static T Rd<T>(ReadOnlySpan<byte> h, int at) where T : unmanaged => MemoryMarshal.Read<T>(h[at..]);

        /// <summary>Maps a world link file read only. Null when it isn't there, isn't whole, or belongs to another reading key.</summary>
        public static WorldLinkFile? TryOpen(string path, Key128 readingKey)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var f = new WorldLinkFile(path);
                if (f.ReadingKey == readingKey) return f;
                f.Dispose();
                return null;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
        }

        public void Dispose()
        {
            if (_p != null) { _view!.SafeMemoryMappedViewHandle.ReleasePointer(); _p = null; }
            _view?.Dispose(); _map?.Dispose(); _file?.Dispose();
        }

        public ReadOnlySpan<WorldTarget> Targets => new(_targets, TargetCount);
        public ReadOnlySpan<WorldPosting> Postings => new(_postings, PostingCount);
        public ReadOnlySpan<int> Unread => new(_unread, UnreadCount);

        /// <summary>The postings of one target: a range of <see cref="Postings"/> (empty when nothing in world data points at it).</summary>
        public (int From, int To) Range(ulong target)
        {
            int lo = 0, hi = TargetCount;
            while (lo < hi) { int mid = (int)(((uint)lo + (uint)hi) >> 1); if (_targets[mid].Target < target) lo = mid + 1; else hi = mid; }
            if (lo >= TargetCount || _targets[lo].Target != target) return (0, 0);
            return ((int)_targets[lo].First, (int)(_targets[lo].First + _targets[lo].Count));
        }

        /// <summary>How many times the posting's record holds the target in its field.</summary>
        public long CountAt(int posting)
        {
            ushort c = _postings[posting].Count;
            if (c != Overflow) return c;
            int lo = 0, hi = OverflowCount;
            while (lo < hi) { int mid = (lo + hi) >>> 1; if ((uint)(_overflow[mid] >> 32) < (uint)posting) lo = mid + 1; else hi = mid; }
            return lo < OverflowCount && (uint)(_overflow[lo] >> 32) == (uint)posting ? (uint)_overflow[lo] : c;
        }

        // ---------------------------------------------------------------- Writing
        /// <summary>Writes a world link file under a temporary name and moves it into place whole.</summary>
        public static void Write(string path, Key128 readingKey, WorldLinkContent c)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            string tmp = path + $".{Environment.ProcessId}-{Environment.CurrentManagedThreadId}.tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var head = new byte[HeaderSize];
                    fs.Write(head);
                    var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);
                    long oFields = fs.Position;
                    foreach (var f in c.Fields) Str(bw, f);
                    bw.Flush(); Pad(fs);
                    long oText = fs.Position;
                    Str(bw, c.UnreadWhy);
                    bw.Flush(); Pad(fs);
                    long oTargets = fs.Position;
                    fs.Write(MemoryMarshal.AsBytes(c.Targets.AsSpan()));
                    long oPostings = fs.Position;
                    fs.Write(MemoryMarshal.AsBytes(c.Postings.AsSpan()));
                    Pad(fs);
                    long oOverflow = fs.Position;
                    var over = new ulong[c.Overflow.Length];
                    for (int i = 0; i < over.Length; i++) over[i] = (ulong)c.Overflow[i].Posting << 32 | c.Overflow[i].Count;
                    fs.Write(MemoryMarshal.AsBytes(over.AsSpan()));
                    long oUnread = fs.Position;
                    fs.Write(MemoryMarshal.AsBytes(c.Unread.AsSpan()));
                    Pad(fs);
                    long total = fs.Position;

                    var h = head.AsSpan();
                    Wr(h, 0, Magic); Wr(h, 8, FormatVersion); Wr(h, 16, total);
                    Wr(h, 24, readingKey.Hi); Wr(h, 32, readingKey.Lo);
                    Wr(h, 40, c.Targets.Length); Wr(h, 44, c.Postings.Length); Wr(h, 48, c.Overflow.Length); Wr(h, 52, c.Fields.Count); Wr(h, 56, c.Unread.Length);
                    Wr(h, 60, c.Records); Wr(h, 64, c.Bytes); Wr(h, 72, c.ByBytes); Wr(h, 80, c.Links); Wr(h, 88, c.Seconds);
                    Wr(h, 96, oFields); Wr(h, 104, oText); Wr(h, 112, oTargets); Wr(h, 120, oPostings); Wr(h, 128, oOverflow); Wr(h, 136, oUnread);
                    fs.Position = 0; fs.Write(head);
                }
                try { File.Move(tmp, path, overwrite: false); }
                catch (IOException) when (File.Exists(path)) { /* another program wrote the same file first: it holds the same links */ }
            }
            finally { try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

        /// <summary>Sorts a plugin's rows (by target, record, field) and lays them out as targets, postings and overflow.
        /// Rows of the same target, record and field are added together.</summary>
        public static void Fill(WorldLinkContent c, WorldRow[] rows, int count)
        {
            var span = rows.AsSpan(0, count);
            span.Sort(static (a, b) => a.Target != b.Target ? a.Target.CompareTo(b.Target) : a.From != b.From ? a.From.CompareTo(b.From) : a.Field.CompareTo(b.Field));
            var targets = new List<WorldTarget>();
            var postings = new List<WorldPosting>(count);
            var over = new List<(uint, uint)>();
            long links = 0;
            for (int i = 0; i < count;)
            {
                int first = postings.Count;
                ulong target = span[i].Target;
                while (i < count && span[i].Target == target)
                {
                    uint from = span[i].From; int field = span[i].Field;
                    long n = 0;
                    while (i < count && span[i].Target == target && span[i].From == from && span[i].Field == field) { n += span[i].Count; i++; }
                    uint total = (uint)Math.Min(n, uint.MaxValue);
                    links += total;
                    if (total >= Overflow) { over.Add(((uint)postings.Count, total)); postings.Add(new WorldPosting(from, checked((ushort)field), Overflow)); }
                    else postings.Add(new WorldPosting(from, checked((ushort)field), (ushort)total));
                }
                targets.Add(new WorldTarget(target, (uint)first, (uint)(postings.Count - first)));
            }
            c.Targets = targets.ToArray(); c.Postings = postings.ToArray(); c.Overflow = over.ToArray(); c.Links = links;
        }

        static void Str(BinaryWriter bw, string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            if (b.Length > ushort.MaxValue) b = b.AsSpan(0, ushort.MaxValue).ToArray();
            bw.Write((ushort)b.Length); bw.Write(b);
        }

        static void Pad(FileStream fs) { while ((fs.Position & 7) != 0) fs.WriteByte(0); }
        static void Wr<T>(Span<byte> h, int at, T v) where T : unmanaged => MemoryMarshal.Write(h[at..], in v);
    }
}
