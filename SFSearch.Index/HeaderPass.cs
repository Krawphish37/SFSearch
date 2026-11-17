using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Exceptions;
using Mutagen.Bethesda.Plugins.Masters;
using Mutagen.Bethesda.Plugins.Records;
using MFormKey = Mutagen.Bethesda.Plugins.FormKey;
using MStyle = Mutagen.Bethesda.Plugins.MasterStyle;

namespace SFSearch.Index;

/// <summary>What one pass over a plugin's record headers found, ready to be written as a segment.</summary>
internal sealed class PassResult
{
    public string[] Names = Array.Empty<string>();
    public MasterStyle[] MasterStyles = Array.Empty<MasterStyle>();
    public uint[] Sigs = Array.Empty<uint>();
    public int[] SigCounts = Array.Empty<int>();
    public long[] SigBytes = Array.Empty<long>();
    public int[] SigCompressed = Array.Empty<int>();
    public Entry[] Entries = Array.Empty<Entry>();
    public ulong[] Parents = Array.Empty<ulong>();
    public ulong[] EdidByEntry = Array.Empty<ulong>();
    public ulong[] EdidByHash = Array.Empty<ulong>();
    public List<ReadOnlyMemory<byte>> Pool = new();
    public long PluginLength;
    public uint PluginFlags;
    public int Groups, Unresolved, Duplicates, FirstOwn, Ranges;
    /// <summary>Seconds: cutting into pieces and reading the headers, sorting and merging, hashing the content.</summary>
    public double ScanSeconds, MergeSeconds, HashSeconds;
}

/// <summary>
/// Turns a FormID as a plugin's file writes it into its owner and local ID, and back, with Starfield's separated
/// master numbering. The rule is Mutagen's (C58 W8, fixes.20): a <see cref="SeparatedMasterPackage"/> made from the
/// plugin's masters and their sizes (<c>FromMasterStyles</c>, no load order and no file read), and Mutagen's
/// <see cref="FormID"/> for where a numbering's index and ID sit. This class only maps Mutagen's masters to their places
/// in the plugin's header (the owners), as arrays, so the header pass resolves every record without allocating; and it
/// keeps SFSearch's one addition: a master whose file isn't there (Mutagen refuses such a plugin, C180). From the first
/// missing master on no place in any numbering is certain, so the package holds the masters before it and FormIDs past
/// them stay unresolved.
/// </summary>
public sealed class FormIdResolver
{
    readonly int[] _full, _medium, _small;
    readonly int _self;
    readonly bool _missing;
    readonly IReadOnlyList<string>? _names;
    readonly ModKey[] _keys;

    /// <summary>Mutagen's package of the masters this resolver knows (all of them unless one is missing).</summary>
    public SeparatedMasterPackage Package { get; }

    /// <param name="styles">Each master's size, in header order. A missing master ends what can be resolved:
    /// from the first missing master on, no place in any of the three numberings is certain, so FormIDs there stay unresolved.</param>
    public FormIdResolver(IReadOnlyList<MasterStyle> styles) : this(styles, MasterStyle.Full, null) { }

    /// <param name="styles">Each master's size, in header order.</param>
    /// <param name="selfStyle">The plugin's own size: how it numbers its own records.</param>
    /// <param name="names">The masters in header order, then the plugin itself. An owner is a place in this list.</param>
    public FormIdResolver(IReadOnlyList<MasterStyle> styles, MasterStyle selfStyle, IReadOnlyList<string>? names)
    {
        _self = styles.Count; _names = names;
        // The masters as Mutagen keys them: their file names (or a stand-in name per place when none are given). A name
        // that isn't a plugin's, a master listed twice or the plugin itself are no masters Mutagen takes: like a missing
        // one, they end what is certain.
        var self = names != null && names.Count > _self && ModKey.TryFromFileName(names[_self], out var own) ? own : ModKey.FromName($"SFSearchPlugin{_self}", ModType.Plugin);
        var keyed = new List<KeyedMasterStyle>();
        var seen = new HashSet<ModKey> { self };
        for (int i = 0; i < styles.Count; i++)
        {
            var key = names != null && i < names.Count && ModKey.TryFromFileName(names[i], out var k) ? k : names == null ? ModKey.FromName($"SFSearchMaster{i}", ModType.Master) : ModKey.Null;
            if (styles[i] == MasterStyle.Missing || key.IsNull || !seen.Add(key)) { _missing = true; break; }
            keyed.Add(new KeyedMasterStyle(key, ToMutagen(styles[i])));
        }
        SeparatedMasterPackage package;
        while (true)
        {
            try { package = SeparatedMasterPackage.FromMasterStyles(self, ToMutagen(selfStyle), keyed); break; }
            catch (TooManyMastersException)
            {
                // More masters of a size than a FormID can number: the ones that fit stay certain
                keyed.RemoveAt(keyed.Count - 1);
                _missing = true;
            }
        }
        Package = package;
        _keys = keyed.Select(x => x.ModKey).Append(self).ToArray();
        var owners = new Dictionary<ModKey, int>();
        for (int i = 0; i < keyed.Count; i++) owners[keyed[i].ModKey] = i;
        int[] Owners(IReadOnlyList<ModKey> list, MStyle style)
            => Enumerable.Range(0, (int)package.OwnIndex(style)).Select(i => owners[list[i]]).ToArray();
        _full = Owners(package.Full, MStyle.Full);
        _medium = Owners(package.Medium, MStyle.Medium);
        _small = Owners(package.Small, MStyle.Small);
    }

    static MStyle ToMutagen(MasterStyle s) => s switch
    {
        MasterStyle.Small => MStyle.Small,
        MasterStyle.Medium => MStyle.Medium,
        _ => MStyle.Full,
    };

    /// <summary>The resolver of a plugin the header index knows: its masters and their sizes as the index has them.</summary>
    public static FormIdResolver For(IndexedPlugin plugin)
    {
        var masters = plugin.Header?.Masters ?? Array.Empty<string>();
        var styles = new MasterStyle[masters.Count];
        for (int i = 0; i < styles.Length; i++) styles[i] = i < plugin.MasterStyles.Count ? plugin.MasterStyles[i] : MasterStyle.Missing;
        var names = new List<string>(masters) { plugin.Name };
        return new FormIdResolver(styles, plugin.Header?.Style ?? MasterStyle.Full, names);
    }

    /// <summary>The place of the plugin itself among the owners: one past its last master.</summary>
    public int SelfOwner => _self;
    /// <summary>How many masters each of the three numberings has (Mutagen's <c>OwnIndex</c>). A FormID whose index is at
    /// or past that count is the plugin's own record.</summary>
    public int FullMasters => _full.Length;
    public int MediumMasters => _medium.Length;
    public int SmallMasters => _small.Length;

    /// <summary>Owner &lt;&lt; 24 | local ID. A FormID past the plugin's masters is the plugin's own, as in Mutagen
    /// (decision 6.1); past a missing master it is unresolved. The numbering, index and ID are Mutagen's FormID's.</summary>
    public ulong Key(uint formId)
    {
        var f = new FormID(formId);
        uint index, local; int[] list;
        switch (f.FullMasterIndex)
        {
            case FormID.SmallMasterMarker: index = f.LightMasterIndex; local = f.LightId; list = _small; break;
            case FormID.MediumMasterMarker: index = f.MediumMasterIndex; local = f.MediumId; list = _medium; break;
            default: index = f.FullMasterIndex; local = f.FullId; list = _full; break;
        }
        if (index < (uint)list.Length) return (ulong)list[index] << 24 | local;
        if (!_missing) return (ulong)_self << 24 | local;
        return (ulong)(Entry.UnresolvedOwner | (int)f.FullMasterIndex) << 24 | (formId & 0xFFFFFF);
    }

    /// <summary>The owner (a place among the masters, or <see cref="SelfOwner"/>) and the local ID of a FormID.
    /// False when a missing master makes it uncertain: the local ID is then the FormID's low 24 bits.</summary>
    public bool TryResolve(uint formId, out int owner, out uint localId)
    {
        ulong key = Key(formId);
        owner = (int)(key >> 24); localId = (uint)key & 0xFFFFFF;
        if (owner < Entry.UnresolvedOwner) return true;
        owner = _self;
        return false;
    }

    /// <summary>The same with the owner's file name. Needs the names (<see cref="For"/>).</summary>
    public bool TryResolve(uint formId, out string plugin, out uint localId)
    {
        bool ok = TryResolve(formId, out int owner, out localId);
        plugin = Names[owner];
        return ok;
    }

    IReadOnlyList<string> Names => _names ?? throw new InvalidOperationException("This resolver was made without plugin names.");

    /// <summary>The FormID this plugin's file writes for a record of an owner: the inverse of <see cref="TryResolve(uint, out int, out uint)"/>,
    /// the first of Mutagen's stored forms (<c>EnumerateStoredForms</c>: the one it writes). Null when the owner sits at
    /// or after a missing master, or Mutagen has no form for it (a master's local ID that doesn't fit its size).</summary>
    public uint? Raw(int owner, uint localId)
    {
        if (owner < 0 || owner > _self || owner == _self && _missing || owner < _self && owner >= _keys.Length - 1) return null;
        var key = new MFormKey(owner == _self ? _keys[^1] : _keys[owner], localId);
        foreach (var form in Package.EnumerateStoredForms(key)) return form.Raw;
        return null;
    }

    /// <summary>The same by the owner's file name: a master's, or the plugin's own. Null when it is neither.</summary>
    public uint? Raw(string plugin, uint localId)
    {
        var names = Names;
        for (int i = 0; i < names.Count - 1; i++)
            if (names[i].Equals(plugin, StringComparison.OrdinalIgnoreCase)) return Raw(i, localId);
        return names[^1].Equals(plugin, StringComparison.OrdinalIgnoreCase) ? Raw(_self, localId) : null;
    }

    /// <summary>Every FormID this plugin's file can store that reads as a record of an owner, the one it writes first:
    /// Mutagen's <c>EnumerateStoredForms</c> (a master's record: its one form; the plugin's own: its full index, its FE or
    /// FD form, and with <paramref name="pastList"/> every index past the list in each numbering its ID fits). Empty when
    /// the owner sits at or after a missing master.</summary>
    public IEnumerable<uint> StoredForms(int owner, uint localId, bool pastList)
    {
        if (owner < 0 || owner > _self || owner == _self && _missing || owner < _self && owner >= _keys.Length - 1) return Array.Empty<uint>();
        return Package.EnumerateStoredForms(new MFormKey(owner == _self ? _keys[^1] : _keys[owner], localId), pastList).Select(f => f.Raw);
    }

    /// <summary>"0003C2:darkstar.esm", "Null" for 0, or the raw FormID with a note when a master is missing.</summary>
    public string FormKey(uint formId) =>
        formId == 0 ? "Null" : TryResolve(formId, out string p, out uint id) ? $"{id:X6}:{p}" : $"{formId:X8}:(unresolved in {Names[^1]})";
}

internal static unsafe class HeaderPass
{
    const uint Grup = 0x50555247, Edid = 0x44494445;
    const uint CompressedFlag = 0x00040000;
    const int Head = PluginHeader.RecordHeaderLength;
    /// <summary>A group bigger than this is opened up and its content handed out in pieces.</summary>
    const long BigGroup = 6 << 20;
    /// <summary>The size of one piece of work.</summary>
    const long Chunk = 3 << 20;

    struct Raw
    {
        public ulong Key; public long Offset; public uint Sig, Flags, Size, ParentRaw, EdidHash; public int EdidAt;
    }


    sealed class RangeResult
    {
        public Raw[] Items = new Raw[1024]; public int Count;
        public byte[] Edids = new byte[4096]; public int EdidBytes;
        public int Groups;
        public byte[] Inflate = new byte[512];

        public ref Raw Add()
        {
            if (Count == Items.Length) Array.Resize(ref Items, Count * 2);
            return ref Items[Count++];
        }

        public int AddEdid(byte* s, int len)
        {
            if (EdidBytes + len + 2 > Edids.Length) Array.Resize(ref Edids, Math.Max(Edids.Length * 2, EdidBytes + len + 2));
            int at = EdidBytes;
            Edids[at] = (byte)len; Edids[at + 1] = (byte)(len >> 8);
            new ReadOnlySpan<byte>(s, len).CopyTo(Edids.AsSpan(at + 2));
            EdidBytes += len + 2;
            return at;
        }
    }

    /// <summary>A group's label names its records' parent: world children (1), cell children (6, 8, 9), topic children (7), quest children (10).</summary>
    static bool NamesParent(int groupType) => groupType is 1 or 6 or 7 or 8 or 9 or 10;

    /// <summary>The pass over a mapped plugin. Throws InvalidDataException when the file can't be walked.</summary>
    public static PassResult Run(byte* p, long length, string pluginName, PluginHeader header, IReadOnlyList<MasterStyle> masterStyles, int threads)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var resolver = new FormIdResolver(masterStyles);
        var job = new Job(p, resolver, pluginName);
        var root = new Piece(header.RecordsStart, length, 0, Cut: true);
        if (threads <= 1 || length - header.RecordsStart <= BigGroup) job.RunInline(root);
        else job.RunOnThreads(root, threads);
        if (job.Error != null) throw job.Error;
        // The pieces come back in any order: put them in file order
        var results = job.Results.OrderBy(r => r.Start).Select(r => r.Result).ToArray();
        double scan = clock.Elapsed.TotalSeconds;
        var res = Merge(results, resolver, pluginName, header, masterStyles, length, job.OpenedGroups);
        res.ScanSeconds = scan; res.MergeSeconds = clock.Elapsed.TotalSeconds - scan;
        return res;
    }

    public static PassResult Run(string path, string pluginName, PluginHeader header, IReadOnlyList<MasterStyle> masterStyles, int threads, out Key128 contentKey, bool hash)
    {
        using var fs = Files.OpenShared(path);
        long length = fs.Length;
        using var mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
        using var view = mmf.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
        byte* p = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        try
        {
            p += view.PointerOffset;
            // The content key is one stream over the whole file, so it runs beside the pass, not before it
            double hashSeconds = 0;
            var handle = fs.SafeFileHandle;
            Task<Key128>? hashing = hash ? Task.Run(() => { var t = System.Diagnostics.Stopwatch.StartNew(); var k = Keys.Content(handle, length); hashSeconds = t.Elapsed.TotalSeconds; return k; }) : null;
            PassResult r;
            try { r = Run(p, length, pluginName, header, masterStyles, threads); }
            catch { try { hashing?.Wait(); } catch (AggregateException) { } throw; }
            try { contentKey = hashing?.Result ?? default; }
            catch (AggregateException e) when (e.InnerException is IOException io) { throw io; }
            r.HashSeconds = hashSeconds;
            return r;
        }
        finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
    }

    static InvalidDataException Bad(string plugin, long pos, string what) => new($"{plugin}: {what} at 0x{pos:X}");

    /// <summary>A stretch of the file that starts and ends at record or group borders. Cut = still to be cut into pieces; otherwise read as it is.</summary>
    readonly record struct Piece(long Start, long End, uint ParentRaw, bool Cut);

    /// <summary>One plugin's pass: pieces are cut and read by whichever thread is free, so a big plugin is spread over all of them.</summary>
    sealed class Job(byte* p, FormIdResolver resolver, string plugin)
    {
        readonly byte* _p = p;
        public readonly System.Collections.Concurrent.ConcurrentBag<(long Start, RangeResult Result)> Results = new();
        public int OpenedGroups;
        public InvalidDataException? Error;
        System.Collections.Concurrent.BlockingCollection<Piece>? _queue;
        int _pending;
        Stack<Piece>? _inline;

        void Add(Piece piece)
        {
            if (_queue == null) { _inline!.Push(piece); return; }
            Interlocked.Increment(ref _pending);
            _queue.Add(piece);
        }

        void Do(Piece piece)
        {
            if (piece.Cut) Cut(piece);
            else Results.Add((piece.Start, Scan(_p, piece, resolver, plugin)));
        }

        public void RunInline(Piece root)
        {
            _inline = new Stack<Piece>();
            _inline.Push(root);
            try { while (_inline.TryPop(out var piece)) Do(piece); }
            catch (InvalidDataException e) { Error = e; }
        }

        public void RunOnThreads(Piece root, int threads)
        {
            _queue = new System.Collections.Concurrent.BlockingCollection<Piece>();
            _pending = 1;
            _queue.Add(root);
            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, _ =>
            {
                foreach (var piece in _queue.GetConsumingEnumerable())
                {
                    try { if (Error == null) Do(piece); }
                    catch (InvalidDataException e) { Error ??= e; }
                    finally { if (Interlocked.Decrement(ref _pending) == 0) _queue.CompleteAdding(); }
                }
            });
        }

        /// <summary>Cuts a stretch into pieces of about <see cref="Chunk"/> at record and group borders. A big group is
        /// handed on to be cut by itself, so its content is cut on another thread.</summary>
        void Cut(Piece piece)
        {
            byte* p = _p;
            long pos = piece.Start, runStart = pos, end = piece.End;
            while (pos + Head <= end)
            {
                uint sig = *(uint*)(p + pos), size = *(uint*)(p + pos + 4);
                long next;
                if (sig == Grup)
                {
                    if (size < Head || pos + size > end) throw Bad(plugin, pos, $"a group of size {size} doesn't fit its place");
                    next = pos + size;
                    if (size > BigGroup)
                    {
                        if (pos > runStart) Add(new Piece(runStart, pos, piece.ParentRaw, false));
                        Interlocked.Increment(ref OpenedGroups);
                        Add(new Piece(pos + Head, next, NamesParent(*(int*)(p + pos + 12)) ? *(uint*)(p + pos + 8) : piece.ParentRaw, true));
                        runStart = pos = next;
                        continue;
                    }
                }
                else
                {
                    next = pos + Head + size;
                    if (next > end) throw Bad(plugin, pos, $"a record of size {size} runs past its group");
                }
                pos = next;
                if (pos - runStart >= Chunk) { Add(new Piece(runStart, pos, piece.ParentRaw, false)); runStart = pos; }
            }
            if (pos != end) throw Bad(plugin, pos, "bytes left over that are too few for a record");
            if (pos > runStart) Add(new Piece(runStart, pos, piece.ParentRaw, false));
        }
    }

    static RangeResult Scan(byte* p, Piece range, FormIdResolver resolver, string plugin)
    {
        var r = new RangeResult();
        const int MaxDepth = 32;
        long* ends = stackalloc long[MaxDepth]; uint* parents = stackalloc uint[MaxDepth];
        int depth = 0;
        long pos = range.Start, end = range.End;
        while (pos + Head <= end)
        {
            while (depth > 0 && pos >= ends[depth - 1]) depth--;
            long limit = depth > 0 ? ends[depth - 1] : end;
            uint parent = depth > 0 ? parents[depth - 1] : range.ParentRaw;
            uint sig = *(uint*)(p + pos), size = *(uint*)(p + pos + 4);
            if (sig == Grup)
            {
                if (size < Head || pos + size > limit) throw Bad(plugin, pos, $"a group of size {size} doesn't fit its place");
                if (depth == MaxDepth) throw Bad(plugin, pos, "groups nested too deep");
                ends[depth] = pos + size;
                parents[depth] = NamesParent(*(int*)(p + pos + 12)) ? *(uint*)(p + pos + 8) : parent;
                depth++; r.Groups++;
                pos += Head;
                continue;
            }
            if (pos + Head + size > limit) throw Bad(plugin, pos, $"a record of size {size} runs past its group");
            uint flags = *(uint*)(p + pos + 8);
            ref Raw e = ref r.Add();
            e.Key = resolver.Key(*(uint*)(p + pos + 12));
            e.Offset = pos; e.Sig = sig; e.Flags = flags; e.Size = size; e.ParentRaw = parent; e.EdidAt = -1; e.EdidHash = 0;
            byte* d = p + pos + Head;
            if ((flags & CompressedFlag) != 0) ReadCompressedEdid(d, size, r, ref e);
            else
            {
                // The EditorID is the first subrecord in every file the game's tools write; the walk covers the rest
                uint at = 0, longForm = 0;
                while (at + 6 <= size)
                {
                    uint ssig = *(uint*)(d + at); uint slen = *(ushort*)(d + at + 4);
                    if (ssig == PluginHeader.Xxxx && slen == 4 && at + 10 <= size) { longForm = *(uint*)(d + at + 6); at += 10; continue; }
                    if (longForm != 0) { slen = longForm; longForm = 0; }
                    if ((ulong)at + 6 + slen > size) break;
                    if (ssig == Edid)
                    {
                        SetEdid(d + at + 6, (int)slen, r, ref e);
                        break;
                    }
                    at += 6 + slen;
                }
            }
            pos += Head + size;
        }
        if (pos != end) throw Bad(plugin, pos, "bytes left over that are too few for a record");
        return r;
    }

    static void SetEdid(byte* s, int len, RangeResult r, ref Raw e)
    {
        // Up to the first zero byte, as Mutagen reads it
        int n = new ReadOnlySpan<byte>(s, len).IndexOf((byte)0);
        if (n < 0) n = len;
        if (n > ushort.MaxValue) n = ushort.MaxValue;
        e.EdidAt = r.AddEdid(s, n);
        e.EdidHash = Text.HashRaw(s, n);
    }

    /// <summary>A compressed record: only its start is inflated, enough for an EditorID that is the first subrecord.</summary>
    static void ReadCompressedEdid(byte* d, uint size, RangeResult r, ref Raw e)
    {
        if (size < 10) return;
        try
        {
            using var ums = new UnmanagedMemoryStream(d + 4, size - 4);
            using var z = new ZLibStream(ums, CompressionMode.Decompress);
            byte[] buf = r.Inflate;
            int got = z.ReadAtLeast(buf.AsSpan(0, 6), 6, false);
            if (got < 6 || buf[0] != 'E' || buf[1] != 'D' || buf[2] != 'I' || buf[3] != 'D') return;
            int len = buf[4] | buf[5] << 8;
            if (len + 6 > buf.Length) { Array.Resize(ref buf, len + 6); r.Inflate = buf; }
            got = 6 + z.ReadAtLeast(buf.AsSpan(6, len), len, false);
            if (got < len + 6) return;
            fixed (byte* s = &buf[6]) SetEdid(s, len, r, ref e);
        }
        catch (InvalidDataException) { }
    }

    static PassResult Merge(RangeResult[] parts, FormIdResolver resolver, string pluginName, PluginHeader header, IReadOnlyList<MasterStyle> masterStyles, long length, int splitGroups)
    {
        long total = 0; foreach (var part in parts) total += part.Count;
        if (total >= 1 << Entry.KeyShift) throw new InvalidDataException($"{pluginName}: {total:N0} records is more than the index holds for one plugin ({1 << Entry.KeyShift:N0})");
        int n = (int)total;
        var res = new PassResult
        {
            Names = header.Masters.Append(pluginName).ToArray(), MasterStyles = masterStyles.ToArray(),
            PluginLength = length, PluginFlags = header.Flags, Groups = splitGroups, Ranges = parts.Length,
        };
        if (res.Names.Length >= Entry.UnresolvedOwner) throw new InvalidDataException($"{pluginName}: too many masters ({header.Masters.Count})");

        // Signatures, in the order of their four bytes read as text
        var sigSet = new HashSet<uint>();
        foreach (var part in parts)
        {
            uint last = 0;
            var items = part.Items;
            for (int i = 0; i < part.Count; i++) if (items[i].Sig != last) { last = items[i].Sig; sigSet.Add(last); }
            res.Groups += part.Groups;
        }
        var sigs = sigSet.OrderBy(Segment.SigText, StringComparer.Ordinal).ToArray();
        if (sigs.Length > 1024) throw new InvalidDataException($"{pluginName}: {sigs.Length} record types is more than the index holds for one plugin (1,024)");
        var sigIndex = new Dictionary<uint, int>();
        for (int i = 0; i < sigs.Length; i++) sigIndex[sigs[i]] = i;
        res.Sigs = sigs; res.SigCounts = new int[sigs.Length]; res.SigBytes = new long[sigs.Length]; res.SigCompressed = new int[sigs.Length];

        // The order: by owner and local ID, and by place in the file where a plugin holds a FormKey twice
        var order = new ulong[n]; var tmp = new ulong[n];
        var starts = new int[parts.Length];
        {
            int g = 0;
            for (int pi = 0; pi < parts.Length; pi++)
            {
                starts[pi] = g;
                var items = parts[pi].Items;
                for (int i = 0; i < parts[pi].Count; i++, g++) order[g] = items[i].Key << Entry.KeyShift | (uint)g;
            }
        }
        Sorting.Radix(ref order, ref tmp, Entry.KeyShift);

        var entries = new Entry[n];
        var parentIndex = new Dictionary<uint, uint>(); var parentKeys = new List<ulong>();
        int edidCount = 0; foreach (var part in parts) for (int i = 0; i < part.Count; i++) if (part.Items[i].EdidAt >= 0) edidCount++;
        var byEntry = new ulong[edidCount]; var byHash = new ulong[edidCount];
        var poolBase = new long[parts.Length];
        { long b = 0; for (int pi = 0; pi < parts.Length; pi++) { poolBase[pi] = b; b += parts[pi].EdidBytes; res.Pool.Add(parts[pi].Edids.AsMemory(0, parts[pi].EdidBytes)); } if (b > uint.MaxValue) throw new InvalidDataException($"{pluginName}: its EditorIDs are more than 4 GB"); }

        int ed = 0, lastSig = -1; uint lastSigRaw = 0, lastParentRaw = 0, lastParent = Entry.NoParent;
        ulong prevKey = ulong.MaxValue; int selfOwner = res.Names.Length - 1;
        res.FirstOwn = n;
        for (int k = 0; k < n; k++)
        {
            int g = (int)(order[k] & ((1UL << Entry.KeyShift) - 1));
            int pi = Array.BinarySearch(starts, g); if (pi < 0) pi = ~pi - 1;
            // Empty ranges share a start: take the last one that starts here
            while (pi + 1 < parts.Length && starts[pi + 1] == g) pi++;
            ref Raw e = ref parts[pi].Items[g - starts[pi]];
            if (e.Sig != lastSigRaw || lastSig < 0) { lastSigRaw = e.Sig; lastSig = sigIndex[e.Sig]; }
            res.SigCounts[lastSig]++; res.SigBytes[lastSig] += Head + (long)e.Size; if ((e.Flags & CompressedFlag) != 0) res.SigCompressed[lastSig]++;
            uint parent = Entry.NoParent;
            if (e.ParentRaw != 0)
            {
                if (e.ParentRaw == lastParentRaw) parent = lastParent;
                else
                {
                    if (!parentIndex.TryGetValue(e.ParentRaw, out parent)) { parent = (uint)parentKeys.Count; parentKeys.Add(resolver.Key(e.ParentRaw)); parentIndex[e.ParentRaw] = parent; }
                    lastParentRaw = e.ParentRaw; lastParent = parent;
                }
            }
            entries[k] = new Entry(e.Key, lastSig, e.Offset, e.EdidAt >= 0, e.Flags, e.Size, parent);
            if (e.EdidAt >= 0)
            {
                byEntry[ed] = (ulong)(uint)k << 32 | (uint)(poolBase[pi] + e.EdidAt);
                byHash[ed] = (ulong)e.EdidHash << 32 | (uint)k;
                ed++;
            }
            int owner = (int)(e.Key >> 24);
            if (owner >= Entry.UnresolvedOwner) res.Unresolved++;
            else if (owner >= selfOwner && res.FirstOwn == n) res.FirstOwn = k;
            if (e.Key == prevKey) res.Duplicates++;
            prevKey = e.Key;
        }
        if (res.FirstOwn == n) res.FirstOwn = n - res.Unresolved;
        var tmp2 = new ulong[edidCount];
        Sorting.Radix(ref byHash, ref tmp2, 0);
        res.Entries = entries; res.Parents = parentKeys.ToArray(); res.EdidByEntry = byEntry; res.EdidByHash = byHash;
        return res;
    }
}

internal static class Sorting
{
    /// <summary>Sorts by the bits from <paramref name="lowBit"/> up, keeping the order of equal ones. The two arrays may come back swapped.</summary>
    public static void Radix(ref ulong[] a, ref ulong[] tmp, int lowBit)
    {
        int n = a.Length;
        if (n < 2) return;
        if (n < 64) { Array.Sort(a, 0, n); return; } // whole-value order is the same: the low bits are the original place
        Span<int> count = stackalloc int[256];
        for (int shift = lowBit; shift < 64; shift += 8)
        {
            int bits = Math.Min(8, 64 - shift); int mask = (1 << bits) - 1;
            count.Clear();
            var src = a;
            for (int i = 0; i < n; i++) count[(int)(src[i] >> shift) & mask]++;
            if (count[(int)(src[0] >> shift) & mask] == n) continue; // every value has the same digit here
            int sum = 0;
            for (int i = 0; i <= mask; i++) { int c = count[i]; count[i] = sum; sum += c; }
            var dst = tmp;
            for (int i = 0; i < n; i++) { ulong v = src[i]; dst[count[(int)(v >> shift) & mask]++] = v; }
            (a, tmp) = (tmp, a);
        }
    }
}
