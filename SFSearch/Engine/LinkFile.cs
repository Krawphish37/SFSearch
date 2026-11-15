using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using SFSearch.Index;

namespace SFSearch.Engine
{
    /// <summary>One link of a plugin: the record it points at, the record that holds it, and the field. 16 bytes.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
    public readonly struct LinkRow
    {
        /// <summary>The target as the plugin's header index numbers it: owner &lt;&lt; 24 | local ID, the owner a place in
        /// the plugin's own name table (its masters, then itself).</summary>
        public readonly ulong Target;
        /// <summary>The record that holds the link: its entry in the plugin's segment.</summary>
        public readonly uint From;
        /// <summary>The field: a place in the file's list of paths (<c>ObjectTemplates[*].Includes[*].Mod</c>).</summary>
        public readonly uint Field;
        public LinkRow(ulong target, uint from, uint field) { Target = target; From = from; Field = field; }
    }

    /// <summary>What one walk over a plugin's records found, ready to be written as its link file.</summary>
    public sealed class LinkFileContent
    {
        public LinkRow[] Links = Array.Empty<LinkRow>();
        public List<string> Fields = new();
        /// <summary>Entries the walk couldn't read through Mutagen (sorted). A search for links reads their bytes.</summary>
        public int[] Unread = Array.Empty<int>();
        public string UnreadWhy = "";
        /// <summary>In-game names: entry and name, sorted by entry.</summary>
        public List<(int Entry, string Name)> Names = new();
        public int Walked, WorldRecords;
        public long WorldBytes;
        public double Seconds;
        public string Language = "English";
    }

    /// <summary>
    /// A plugin's link index (design\agent-first\ARCHITECTURE.md section 5): every link of every record outside the
    /// world groups, sorted by target, as a read-only memory-mapped file in scratch\links. It is named by the plugin's
    /// reading key and the build that made it, so a changed plugin, a master that changed size, or another SFSearch,
    /// Mutagen or schema never reads an old file. Any number of workers map the same file.
    /// </summary>
    public sealed unsafe class LinkFile : IDisposable
    {
        public const int FormatVersion = 1;
        const ulong Magic = 0x31304B4E4C534653; // "SFSLNK01"
        const int HeaderSize = 192;

        readonly FileStream _file;
        readonly MemoryMappedFile? _map;
        readonly MemoryMappedViewAccessor? _view;
        byte* _p;
        readonly LinkRow* _links;
        readonly int* _unread;
        readonly ulong* _names;     // entry << 32 | pool offset, sorted by entry
        readonly byte* _namePool;

        public string Path { get; }
        public Key128 ReadingKey { get; }
        public int Count { get; }
        public IReadOnlyList<string> Fields { get; }
        public int UnreadCount { get; }
        public string UnreadWhy { get; } = "";
        public int NameCount { get; }
        public int Walked { get; }
        public int WorldRecords { get; }
        public long WorldBytes { get; }
        public double BuildSeconds { get; }
        public string Language { get; } = "English";
        public long FileBytes { get; }

        public static string PathFor(string folder, Key128 readingKey, string builderKey) => System.IO.Path.Combine(folder, $"{readingKey}-{builderKey}.links");

        LinkFile(string path)
        {
            Path = path;
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            try
            {
                FileBytes = _file.Length;
                if (FileBytes < HeaderSize) throw new InvalidDataException("link file too short");
                _map = MemoryMappedFile.CreateFromFile(_file, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
                _view = _map.CreateViewAccessor(0, FileBytes, MemoryMappedFileAccess.Read);
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _p);
                _p += _view.PointerOffset;
                var h = new ReadOnlySpan<byte>(_p, HeaderSize);
                if (Rd<ulong>(h, 0) != Magic || Rd<int>(h, 8) != FormatVersion || Rd<long>(h, 16) != FileBytes) throw new InvalidDataException("not a link file of this version, or cut short");
                ReadingKey = new Key128(Rd<ulong>(h, 24), Rd<ulong>(h, 32));
                Count = Rd<int>(h, 48); int fieldCount = Rd<int>(h, 52); UnreadCount = Rd<int>(h, 56); NameCount = Rd<int>(h, 60);
                Walked = Rd<int>(h, 64); WorldRecords = Rd<int>(h, 68); WorldBytes = Rd<long>(h, 72);
                long oFields = Rd<long>(h, 80), oLinks = Rd<long>(h, 88), oUnread = Rd<long>(h, 96), oNames = Rd<long>(h, 104), oPool = Rd<long>(h, 112), poolBytes = Rd<long>(h, 120);
                BuildSeconds = Rd<double>(h, 128);
                long oText = Rd<long>(h, 136);
                if (oLinks + (long)Count * sizeof(LinkRow) > FileBytes || oPool + poolBytes > FileBytes || oNames + (long)NameCount * 8 > FileBytes) throw new InvalidDataException("link file sections run past its end");
                var fields = new string[fieldCount];
                byte* q = _p + oFields;
                for (int i = 0; i < fieldCount; i++) { int len = *(ushort*)q; fields[i] = Encoding.UTF8.GetString(q + 2, len); q += 2 + len; }
                Fields = fields;
                q = _p + oText;
                int n = *(ushort*)q; UnreadWhy = Encoding.UTF8.GetString(q + 2, n); q += 2 + n;
                n = *(ushort*)q; Language = Encoding.UTF8.GetString(q + 2, n);
                _links = (LinkRow*)(_p + oLinks);
                _unread = (int*)(_p + oUnread);
                _names = (ulong*)(_p + oNames);
                _namePool = _p + oPool;
            }
            catch { Dispose(); throw; }
            Fields ??= Array.Empty<string>();
        }

        static T Rd<T>(ReadOnlySpan<byte> h, int at) where T : unmanaged => MemoryMarshal.Read<T>(h[at..]);

        /// <summary>Maps a link file read only. Null when it isn't there, isn't whole, or belongs to another reading key.</summary>
        public static LinkFile? TryOpen(string path, Key128 readingKey)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var f = new LinkFile(path);
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

        public ReadOnlySpan<LinkRow> Links => new(_links, Count);
        public ReadOnlySpan<int> Unread => new(_unread, UnreadCount);

        /// <summary>The links that point at a target: a range of <see cref="Links"/>.</summary>
        public (int From, int To) Range(ulong target)
        {
            int lo = 0, hi = Count;
            while (lo < hi) { int mid = (int)(((uint)lo + (uint)hi) >> 1); if (_links[mid].Target < target) lo = mid + 1; else hi = mid; }
            int end = lo;
            while (end < Count && _links[end].Target == target) end++;
            return (lo, end);
        }

        /// <summary>The in-game name the walk noted for an entry, or null.</summary>
        public string? NameOf(int entry)
        {
            int lo = 0, hi = NameCount;
            while (lo < hi) { int mid = (lo + hi) >>> 1; if ((uint)(_names[mid] >> 32) < (uint)entry) lo = mid + 1; else hi = mid; }
            return lo < NameCount && (uint)(_names[lo] >> 32) == (uint)entry ? NameAt(lo) : null;
        }

        public int NameEntry(int i) => (int)(_names[i] >> 32);

        public string NameAt(int i)
        {
            byte* s = _namePool + (uint)_names[i];
            return Encoding.UTF8.GetString(s + 2, *(ushort*)s);
        }

        /// <summary>The name's bytes (UTF-8), for a search that needn't make a string of every name.</summary>
        public ReadOnlySpan<byte> NameBytes(int i)
        {
            byte* s = _namePool + (uint)_names[i];
            return new ReadOnlySpan<byte>(s + 2, *(ushort*)s);
        }

        // ---------------------------------------------------------------- Writing
        /// <summary>Writes a link file under a temporary name and moves it into place whole.</summary>
        public static void Write(string path, Key128 readingKey, LinkFileContent c)
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
                    Str(bw, c.UnreadWhy); Str(bw, c.Language);
                    bw.Flush(); Pad(fs);
                    long oLinks = fs.Position;
                    fs.Write(MemoryMarshal.AsBytes(c.Links.AsSpan()));
                    long oUnread = fs.Position;
                    fs.Write(MemoryMarshal.AsBytes(c.Unread.AsSpan()));
                    Pad(fs);
                    // Names: the pool first in memory, so each name's place is known
                    var pool = new MemoryStream();
                    var index = new ulong[c.Names.Count];
                    for (int i = 0; i < c.Names.Count; i++)
                    {
                        var b = Encoding.UTF8.GetBytes(c.Names[i].Name);
                        if (b.Length > ushort.MaxValue) b = b.AsSpan(0, ushort.MaxValue).ToArray();
                        index[i] = (ulong)(uint)c.Names[i].Entry << 32 | (uint)pool.Position;
                        pool.Write(BitConverter.GetBytes((ushort)b.Length)); pool.Write(b);
                    }
                    long oNames = fs.Position;
                    fs.Write(MemoryMarshal.AsBytes(index.AsSpan()));
                    long oPool = fs.Position;
                    pool.Position = 0; pool.CopyTo(fs);
                    long total = fs.Position;

                    var h = head.AsSpan();
                    Wr(h, 0, Magic); Wr(h, 8, FormatVersion); Wr(h, 16, total);
                    Wr(h, 24, readingKey.Hi); Wr(h, 32, readingKey.Lo);
                    Wr(h, 48, c.Links.Length); Wr(h, 52, c.Fields.Count); Wr(h, 56, c.Unread.Length); Wr(h, 60, c.Names.Count);
                    Wr(h, 64, c.Walked); Wr(h, 68, c.WorldRecords); Wr(h, 72, c.WorldBytes);
                    Wr(h, 80, oFields); Wr(h, 88, oLinks); Wr(h, 96, oUnread); Wr(h, 104, oNames); Wr(h, 112, oPool); Wr(h, 120, pool.Length);
                    Wr(h, 128, c.Seconds); Wr(h, 136, oText);
                    fs.Position = 0; fs.Write(head);
                }
                try { File.Move(tmp, path, overwrite: false); }
                catch (IOException) when (File.Exists(path)) { /* another program wrote the same file first: it holds the same links */ }
            }
            finally { try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
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
