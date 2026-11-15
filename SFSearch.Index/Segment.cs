using System.IO.Hashing;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SFSearch.Index;

/// <summary>
/// One record of a plugin, 24 bytes. Entries are sorted by FormKey (owner, then local ID).
/// <c>Bits</c>: 63..50 owner (a place in the segment's name table, or 0x3F00 + the raw top byte when the FormID
/// couldn't be resolved), 49..26 local ID, 25..16 signature (a place in the signature table), 15..8 file position
/// bits 39..32, bit 0 = has an EditorID.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 24)]
public readonly struct Entry
{
    public readonly ulong Bits;
    /// <summary>The record header's flags (0x00040000 = compressed, 0x20 = deleted, …).</summary>
    public readonly uint Flags;
    /// <summary>The size of the record's data, without its 24-byte header.</summary>
    public readonly uint Size;
    readonly uint _offsetLo;
    /// <summary>A place in the segment's parent table, or <see cref="NoParent"/>.</summary>
    public readonly uint Parent;

    public const uint NoParent = 0xFFFFFFFF;
    public const int UnresolvedOwner = 0x3F00;
    internal const int KeyShift = 26;

    internal Entry(ulong key38, int sig, long offset, bool hasEdid, uint flags, uint size, uint parent)
    {
        Bits = (key38 << KeyShift) | ((ulong)sig << 16) | ((ulong)(offset >> 32) << 8) | (hasEdid ? 1UL : 0);
        Flags = flags; Size = size; _offsetLo = (uint)offset; Parent = parent;
    }

    /// <summary>Owner and local ID in one number: owner &lt;&lt; 24 | local ID. The order of the segment.</summary>
    public ulong Key => Bits >> KeyShift;
    public int Owner => (int)(Bits >> 50);
    public uint LocalId => (uint)(Bits >> KeyShift) & 0xFFFFFF;
    public int Signature => (int)(Bits >> 16) & 0x3FF;
    /// <summary>Where the record's header starts in the plugin file.</summary>
    public long Offset => (long)((Bits >> 8) & 0xFF) << 32 | _offsetLo;
    public bool HasEditorId => (Bits & 1) != 0;
    public bool IsCompressed => (Flags & 0x00040000) != 0;
    /// <summary>The FormID went through a master that isn't there. <see cref="RawFormId"/> is what the file says.</summary>
    public bool IsUnresolved => Owner >= UnresolvedOwner;
    public uint RawFormId => IsUnresolved ? (uint)(Owner & 0xFF) << 24 | LocalId : 0;
}

/// <summary>One record type in a plugin: how many records and how many bytes (headers included). Cost estimates come from here.</summary>
public readonly record struct SignatureStats(string Signature, int Count, long Bytes, int Compressed);

/// <summary>
/// A plugin's header index: a read-only memory-mapped file in the scratch folder, named by the plugin's reading key.
/// Any number of processes can map the same segment; Windows shares the pages.
/// </summary>
public sealed unsafe class Segment : IDisposable
{
    public const int FormatVersion = 1;
    const ulong Magic = 0x3130584449534653; // "SFSIDX01"
    const int HeaderSize = 256;

    readonly FileStream _file;
    readonly MemoryMappedFile _map;
    readonly MemoryMappedViewAccessor _view;
    byte* _p;
    readonly Entry* _entries;
    readonly ulong* _parents;
    readonly ulong* _edidByEntry;   // entry index << 32 | pool offset, sorted
    readonly ulong* _edidByHash;    // hash << 32 | entry index, sorted
    readonly byte* _pool;
    readonly long _poolBytes;

    public string Path { get; }
    public Key128 ReadingKey { get; }
    public Key128 ContentKey { get; }
    public long PluginLength { get; }
    public uint PluginFlags { get; }
    /// <summary>The plugin's own file name, as it was when the segment was made.</summary>
    public string Plugin { get; }
    /// <summary>The masters in header order, then the plugin itself. An entry's owner is a place in this list.</summary>
    public IReadOnlyList<string> Names { get; }
    /// <summary>The size each master had when the segment was made (Missing when its file wasn't there).</summary>
    public IReadOnlyList<MasterStyle> MasterStyles { get; }
    public int SelfOwner => Names.Count - 1;
    public IReadOnlyList<SignatureStats> Signatures { get; }
    public int Count { get; }
    public int ParentCount { get; }
    public int EditorIdCount { get; }
    public int Groups { get; }
    /// <summary>Records whose FormID couldn't be resolved because a master is missing.</summary>
    public int Unresolved { get; }
    /// <summary>Records whose FormKey another record of the same plugin already has.</summary>
    public int Duplicates { get; }
    /// <summary>Entries [0, FirstOwn) are overrides of masters' records; [FirstOwn, FirstUnresolved) are the plugin's own.</summary>
    public int FirstOwn { get; }
    public int FirstUnresolved => Count - Unresolved;
    public long FileBytes { get; }

    public ReadOnlySpan<Entry> Entries => new(_entries, Count);

    Segment(string path)
    {
        Path = path;
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            FileBytes = _file.Length;
            if (FileBytes < HeaderSize) throw new InvalidDataException("segment too short");
            _map = MemoryMappedFile.CreateFromFile(_file, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
            _view = _map.CreateViewAccessor(0, FileBytes, MemoryMappedFileAccess.Read);
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _p);
            _p += _view.PointerOffset;
            var h = new ReadOnlySpan<byte>(_p, HeaderSize);
            if (Rd<ulong>(h, 0) != Magic || Rd<int>(h, 8) != FormatVersion || Rd<long>(h, 16) != FileBytes) throw new InvalidDataException("not a segment of this version, or cut short");
            ReadingKey = new Key128(Rd<ulong>(h, 24), Rd<ulong>(h, 32));
            ContentKey = new Key128(Rd<ulong>(h, 40), Rd<ulong>(h, 48));
            PluginLength = Rd<long>(h, 56);
            PluginFlags = Rd<uint>(h, 64);
            int nameCount = Rd<int>(h, 68), sigCount = Rd<int>(h, 72);
            Count = Rd<int>(h, 76); ParentCount = Rd<int>(h, 80); EditorIdCount = Rd<int>(h, 84);
            Groups = Rd<int>(h, 88); Unresolved = Rd<int>(h, 92); Duplicates = Rd<int>(h, 96); FirstOwn = Rd<int>(h, 100);
            long oNames = Rd<long>(h, 104), oSigs = Rd<long>(h, 112), oEntries = Rd<long>(h, 120), oParents = Rd<long>(h, 128),
                 oByEntry = Rd<long>(h, 136), oByHash = Rd<long>(h, 144), oPool = Rd<long>(h, 152);
            _poolBytes = Rd<long>(h, 160);
            if (oPool + _poolBytes > FileBytes || oEntries + (long)Count * sizeof(Entry) > FileBytes) throw new InvalidDataException("segment sections run past its end");

            var names = new string[nameCount]; var styles = new MasterStyle[nameCount - 1];
            byte* q = _p + oNames;
            for (int i = 0; i < nameCount; i++)
            {
                int len = *(ushort*)q;
                names[i] = Encoding.UTF8.GetString(q + 2, len);
                if (i < nameCount - 1) styles[i] = (MasterStyle)q[2 + len];
                q += 3 + len;
            }
            Names = names; MasterStyles = styles; Plugin = names[^1];
            var sigs = new SignatureStats[sigCount];
            q = _p + oSigs;
            for (int i = 0; i < sigCount; i++, q += 20)
                sigs[i] = new SignatureStats(SigText(*(uint*)q), *(int*)(q + 4), *(long*)(q + 8), *(int*)(q + 16));
            Signatures = sigs;
            _entries = (Entry*)(_p + oEntries);
            _parents = (ulong*)(_p + oParents);
            _edidByEntry = (ulong*)(_p + oByEntry);
            _edidByHash = (ulong*)(_p + oByHash);
            _pool = _p + oPool;
        }
        catch { Dispose(); throw; }
    }

    static T Rd<T>(ReadOnlySpan<byte> h, int at) where T : unmanaged => MemoryMarshal.Read<T>(h[at..]);

    /// <summary>Maps a segment read only. Null when the file isn't there or isn't a whole segment of this version.</summary>
    public static Segment? TryOpen(string path)
    {
        if (!File.Exists(path)) return null;
        try { return new Segment(path); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        if (_p != null) { _view.SafeMemoryMappedViewHandle.ReleasePointer(); _p = null; }
        _view?.Dispose(); _map?.Dispose(); _file?.Dispose();
    }

    // ---------------------------------------------------------------- Lookups
    /// <summary>The first entry at or after this owner and local ID.</summary>
    public int LowerBound(int owner, uint localId)
    {
        ulong key = (ulong)owner << 24 | localId;
        int lo = 0, hi = Count;
        while (lo < hi) { int mid = (int)(((uint)lo + (uint)hi) >> 1); if (_entries[mid].Key < key) lo = mid + 1; else hi = mid; }
        return lo;
    }

    /// <summary>The entry with this owner and local ID, or -1. When the plugin holds the FormKey twice, the first in the file.</summary>
    public int Find(int owner, uint localId)
    {
        int i = LowerBound(owner, localId);
        return i < Count && _entries[i].Key == ((ulong)owner << 24 | localId) ? i : -1;
    }

    /// <summary>The place of a plugin name in <see cref="Names"/>, or -1.</summary>
    public int OwnerOf(string plugin)
    {
        for (int i = Names.Count - 1; i >= 0; i--) if (Names[i].Equals(plugin, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public string SignatureOf(in Entry e) => Signatures[e.Signature].Signature;

    /// <summary>A record's parent (a placed reference's cell, a cell's worldspace, a response's topic, a topic's quest)
    /// as owner &lt;&lt; 24 | local ID in this segment's name table. False when it has none.</summary>
    public bool TryGetParent(in Entry e, out int owner, out uint localId)
    {
        owner = -1; localId = 0;
        if (e.Parent == Entry.NoParent) return false;
        ulong k = _parents[e.Parent];
        owner = (int)(k >> 24); localId = (uint)k & 0xFFFFFF;
        return true;
    }

    /// <summary>The record's EditorID, or null.</summary>
    public string? EditorId(int entry)
    {
        if (!_entries[entry].HasEditorId) return null;
        int lo = 0, hi = EditorIdCount;
        while (lo < hi) { int mid = (lo + hi) >>> 1; if ((uint)(_edidByEntry[mid] >> 32) < (uint)entry) lo = mid + 1; else hi = mid; }
        if (lo >= EditorIdCount || (uint)(_edidByEntry[lo] >> 32) != (uint)entry) return null;
        byte* s = _pool + (uint)_edidByEntry[lo];
        return Text.Decode(new ReadOnlySpan<byte>(s + 2, *(ushort*)s));
    }

    /// <summary>Every entry whose EditorID is this text, letter case aside.</summary>
    public void FindEditorId(string editorId, List<int> into)
    {
        if (EditorIdCount == 0) return;
        Span<byte> buf = editorId.Length <= 256 ? stackalloc byte[editorId.Length] : new byte[editorId.Length];
        if (!Text.TryEncodeLower(editorId, buf)) return;
        uint hash = Text.Hash(buf);
        // The hashes are spread evenly, so start close to the right place
        int n = EditorIdCount;
        int i = (int)((ulong)hash * (ulong)n >> 32);
        while (i > 0 && (uint)(_edidByHash[i - 1] >> 32) >= hash) i--;
        while (i < n && (uint)(_edidByHash[i] >> 32) < hash) i++;
        for (; i < n && (uint)(_edidByHash[i] >> 32) == hash; i++)
        {
            int entry = (int)(uint)_edidByHash[i];
            if (string.Equals(EditorId(entry), editorId, StringComparison.OrdinalIgnoreCase)) into.Add(entry);
        }
    }

    /// <summary>Every entry of one record type, in FormKey order. A scan of the entries: about 4 ms per million.</summary>
    public IEnumerable<int> EntriesOf(string signature)
    {
        int sig = -1;
        for (int i = 0; i < Signatures.Count; i++) if (Signatures[i].Signature.Equals(signature, StringComparison.OrdinalIgnoreCase)) { sig = i; break; }
        if (sig < 0) yield break;
        int left = Signatures[sig].Count;
        for (int i = 0; i < Count && left > 0; i++)
            if (SigAt(i) == sig) { left--; yield return i; }
    }

    int SigAt(int i) => _entries[i].Signature;

    public Entry this[int i] => (uint)i < (uint)Count ? _entries[i] : throw new ArgumentOutOfRangeException(nameof(i));

    /// <summary>A cheap check that two mappings show the same bytes.</summary>
    public ulong Checksum() => XxHash3.HashToUInt64(new ReadOnlySpan<byte>(_p, (int)Math.Min(FileBytes, int.MaxValue)));

    internal static string SigText(uint sig)
    {
        Span<char> c = stackalloc char[4];
        for (int i = 0; i < 4; i++) c[i] = (char)(byte)(sig >> (8 * i));
        return new string(c);
    }

    // ---------------------------------------------------------------- Writing
    /// <summary>Writes a pass's result as a segment file. It is written under a temporary name and moved into place whole.</summary>
    internal static void Write(string path, PassResult r, Key128 readingKey, Key128 contentKey)
    {
        string tmp = path + $".{Environment.ProcessId}-{Environment.CurrentManagedThreadId}.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var head = new byte[HeaderSize];
                fs.Write(head);
                long oNames = fs.Position;
                var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);
                for (int i = 0; i < r.Names.Length; i++)
                {
                    var b = Encoding.UTF8.GetBytes(r.Names[i]);
                    bw.Write((ushort)b.Length); bw.Write(b);
                    bw.Write((byte)(i < r.MasterStyles.Length ? r.MasterStyles[i] : MasterStyle.Full));
                }
                bw.Flush(); Pad(fs);
                long oSigs = fs.Position;
                for (int i = 0; i < r.Sigs.Length; i++) { bw.Write(r.Sigs[i]); bw.Write(r.SigCounts[i]); bw.Write(r.SigBytes[i]); bw.Write(r.SigCompressed[i]); }
                bw.Flush(); Pad(fs);
                long oEntries = fs.Position;
                fs.Write(MemoryMarshal.AsBytes(r.Entries.AsSpan()));
                Pad(fs);
                long oParents = fs.Position;
                fs.Write(MemoryMarshal.AsBytes(r.Parents.AsSpan()));
                long oByEntry = fs.Position;
                fs.Write(MemoryMarshal.AsBytes(r.EdidByEntry.AsSpan()));
                long oByHash = fs.Position;
                fs.Write(MemoryMarshal.AsBytes(r.EdidByHash.AsSpan()));
                long oPool = fs.Position; long poolBytes = 0;
                foreach (var seg in r.Pool) { fs.Write(seg.Span); poolBytes += seg.Length; }
                long total = fs.Position;

                var h = head.AsSpan();
                Wr(h, 0, Magic); Wr(h, 8, FormatVersion); Wr(h, 16, total);
                Wr(h, 24, readingKey.Hi); Wr(h, 32, readingKey.Lo); Wr(h, 40, contentKey.Hi); Wr(h, 48, contentKey.Lo);
                Wr(h, 56, r.PluginLength); Wr(h, 64, r.PluginFlags);
                Wr(h, 68, r.Names.Length); Wr(h, 72, r.Sigs.Length); Wr(h, 76, r.Entries.Length); Wr(h, 80, r.Parents.Length); Wr(h, 84, r.EdidByEntry.Length);
                Wr(h, 88, r.Groups); Wr(h, 92, r.Unresolved); Wr(h, 96, r.Duplicates); Wr(h, 100, r.FirstOwn);
                Wr(h, 104, oNames); Wr(h, 112, oSigs); Wr(h, 120, oEntries); Wr(h, 128, oParents); Wr(h, 136, oByEntry); Wr(h, 144, oByHash); Wr(h, 152, oPool);
                Wr(h, 160, poolBytes);
                fs.Position = 0; fs.Write(head);
            }
            try { File.Move(tmp, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { /* another process wrote the same segment first: it holds the same bytes */ }
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    static void Pad(FileStream fs) { while ((fs.Position & 7) != 0) fs.WriteByte(0); }
    static void Wr<T>(Span<byte> h, int at, T v) where T : unmanaged => MemoryMarshal.Write(h[at..], in v);
}

/// <summary>EditorID bytes: Windows-1252 as Mutagen reads them, compared with letter case aside.</summary>
internal static class Text
{
    static readonly char[] High =
    {
        '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
        '\u0090', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ',
    };

    public static string Decode(ReadOnlySpan<byte> b)
    {
        return string.Create(b.Length, b, static (chars, bytes) =>
        {
            for (int i = 0; i < bytes.Length; i++) { byte c = bytes[i]; chars[i] = c is >= 0x80 and < 0xA0 ? High[c - 0x80] : (char)c; }
        });
    }

    /// <summary>The text as Windows-1252 bytes with A to Z in lower case. False when a character has no such byte (then no EditorID can match it).</summary>
    public static bool TryEncodeLower(string s, Span<byte> into)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is >= 'A' and <= 'Z') into[i] = (byte)(c + 32);
            else if (c < 0x80 || (c >= 0xA0 && c <= 0xFF)) into[i] = (byte)c;
            else
            {
                int at = Array.IndexOf(High, c);
                if (at < 0) return false;
                into[i] = (byte)(0x80 + at);
            }
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Hash(ReadOnlySpan<byte> lower) => (uint)XxHash3.HashToUInt64(lower);

    /// <summary>The hash of raw EditorID bytes, with A to Z lowered first.</summary>
    public static unsafe uint HashRaw(byte* s, int len)
    {
        Span<byte> buf = len <= 512 ? stackalloc byte[len] : new byte[len];
        for (int i = 0; i < len; i++) { byte c = s[i]; buf[i] = c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c; }
        return Hash(buf);
    }
}
