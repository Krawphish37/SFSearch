// What the file reports share: the BA2 archives of the Data folder read through Mutagen's archive reader, the loose
// files in Data, and where a Data-relative path is found (a loose file first, as the game does, then an archive).
// Nothing is stored. In memory, while an archive keeps its size and date:
//  - its file list, for archives up to a total of 250,000 files (a plugin's own archives stay ready; a walk over
//    all 457 archives of a Data folder reads each list, uses it and lets it go);
//  - the hashes of its paths (8 bytes a file), which is all "is this path in this archive" needs. They are read
//    from the archive's name table alone (Mutagen's EnumerateFilePaths: the header and the names at the file's end),
//    once per process (shared by every report session) for the reports that need only names: assets, materials,
//    archive's summary;
//  - the material list of a compiled material database it holds (materialsbeta.cdb), once asked for: 8 bytes a material;
//    and the database's contents (its objects and their textures), once a report needs a compiled material's textures.
//    Both once per process, like the path hashes.
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace SFSearch.Reports;

/// <summary>One file inside an archive.</summary>
public sealed class ArchivedFile
{
    /// <summary>The path as archives and the Data folder name it: backslashes, no leading one ("meshes\x\y.nif").</summary>
    public required string Path { get; init; }
    public required ArchiveInfo Archive { get; init; }
    public required IArchiveFile File { get; init; }
    public bool IsTexture => Path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
    /// <summary>The file's size in bytes as the archive's table gives it. Null for a texture: the reader's number
    /// for a texture entry isn't the file's size.</summary>
    public long? Size => IsTexture ? null : File.Size;
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
    public byte[] Bytes() => File.GetBytes();
}

/// <summary>Which copy of an archive: its file, size and date.</summary>
public class ArchiveStamp
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public long Length { get; init; }
    public DateTime WriteUtc { get; init; }
    /// <summary>The line of an answer's source: which copy of the archive was read.</summary>
    public string SourceLine => $"{Name} {ArchiveStore.SizeText(Length)}, written {WriteUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
    public bool SameCopy(FileInfo fi) => fi.Exists && fi.Length == Length && fi.LastWriteTimeUtc == WriteUtc;
}

/// <summary>One archive with the files inside it.</summary>
public sealed class ArchiveInfo : ArchiveStamp
{
    public IReadOnlyList<ArchivedFile> Files { get; internal set; } = Array.Empty<ArchivedFile>();
    /// <summary>Why the archive couldn't be read. Its file list is then empty.</summary>
    public string? Error { get; internal set; }
    Dictionary<string, ArchivedFile>? _byPath;

    public ArchivedFile? Find(string path)
    {
        if (_byPath == null)
        {
            var d = new Dictionary<string, ArchivedFile>(Files.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var f in Files) d.TryAdd(f.Path, f);
            _byPath = d;
        }
        return _byPath.GetValueOrDefault(path);
    }
}

/// <summary>Which paths an archive holds, as hashes: enough to say whether a path is in it.</summary>
public sealed class ArchiveNames : ArchiveStamp
{
    internal ulong[] Hashes = Array.Empty<ulong>();
    public int Count => Hashes.Length;
    public string? Error { get; internal set; }
    public bool Has(string path) => Array.BinarySearch(Hashes, ArchiveStore.Hash(path)) >= 0;
    /// <summary>The compiled material databases (.cdb) the archive holds, by path.</summary>
    public IReadOnlyList<string> Databases { get; internal set; } = Array.Empty<string>();
    /// <summary>How many compiled scripts (.pex) the archive holds: one with none needn't be opened for scripts.</summary>
    public int Scripts { get; internal set; }
}

/// <summary>The paths an archive holds, in its own order, without the entries' sizes or data (<see cref="ArchiveStore.PathsOf"/>).</summary>
public sealed class ArchivePaths : ArchiveStamp
{
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();
    /// <summary>Why the archive couldn't be read. Its paths are then empty.</summary>
    public string? Error { get; init; }
}

public sealed class ArchiveStore
{
    /// <summary>How many files' worth of file lists stay in memory between uses.</summary>
    public const int KeptFiles = 250_000;

    readonly Dictionary<string, ArchiveInfo> _lists = new(StringComparer.OrdinalIgnoreCase);
    readonly LinkedList<string> _recent = new();
    /// <summary>The path hashes of every archive read, for the whole process: every report session (the service makes
    /// one per call) finds them here while the archive keeps its size and date. About 15 MB for the 457 archives of
    /// a Data folder.</summary>
    static readonly ConcurrentDictionary<string, ArchiveNames> _names = new(StringComparer.OrdinalIgnoreCase);
    HashSet<string>? _loose;
    long _keptFiles;

    public string DataFolder { get; }
    readonly HashSet<string> _readNow = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>How many archives had their file list read (not taken from memory) since the last <see cref="Begin"/>.</summary>
    public int ListsRead => _readNow.Count;
    /// <summary>How many archives' names a report looked in since the last <see cref="Begin"/> (<see cref="Names"/>),
    /// read now or taken from memory.</summary>
    public int NamesUsed => _checkedNow.Count;
    /// <summary>How many of those were read now, not taken from memory.</summary>
    public int NamesUsedReadNow => _checkedNow.Values.Count(n => _readNow.Contains(n.Path));
    /// <summary>How many archives <see cref="ReadAhead"/> read in this report that it then didn't look in.</summary>
    public int ReadAheadUnused => _aheadNow.Count;
    /// <summary>Archives this store's <see cref="ReadAhead"/> read since the last <see cref="Begin"/>, until they are looked in.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _aheadNow = new(StringComparer.OrdinalIgnoreCase);

    public ArchiveStore(string dataFolder) { DataFolder = System.IO.Path.GetFullPath(dataFolder); }

    /// <summary>At the start of a report: the loose files are looked at again, and the count of lists read starts over.</summary>
    public void Begin() { _loose = null; _aheadNow.Clear(); _readNow.Clear(); _checkedNow.Clear(); _databasesReadNow.Clear(); _contentsReadNow.Clear(); }

    readonly Dictionary<string, ArchiveNames> _checkedNow = new(StringComparer.OrdinalIgnoreCase);

    public static ArchiveStamp Stamp(string path)
    {
        var fi = new FileInfo(path);
        return new ArchiveStamp { Path = fi.FullName, Length = fi.Exists ? fi.Length : 0, WriteUtc = fi.Exists ? fi.LastWriteTimeUtc : default };
    }

    /// <summary>An archive's file list, read through Mutagen's archive reader. Taken from memory when the file has
    /// the same size and date as when it was read; read again otherwise.</summary>
    public ArchiveInfo Open(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var fi = new FileInfo(path);
        if (_lists.TryGetValue(path, out var have))
        {
            if (have.SameCopy(fi)) { _recent.Remove(path); _recent.AddLast(path); return have; }
            Forget(path);
        }
        var info = new ArchiveInfo { Path = path, Length = fi.Exists ? fi.Length : 0, WriteUtc = fi.Exists ? fi.LastWriteTimeUtc : default };
        _readNow.Add(path);
        try
        {
            var reader = Archive.CreateReader(GameRelease.Starfield, path);
            var files = new List<ArchivedFile>();
            foreach (var f in reader.Files)
                files.Add(new ArchivedFile { Path = Normalize(f.Path.ToString()), Archive = info, File = f });
            info.Files = files;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotImplementedException or ArgumentException or OverflowException)
        {
            info.Error = e.Message;
        }
        // The path hashes come for free
        var hashes = new ulong[info.Files.Count];
        for (int i = 0; i < hashes.Length; i++) hashes[i] = Hash(info.Files[i].Path);
        Array.Sort(hashes);
        _names[path] = new ArchiveNames
        {
            Path = path, Length = info.Length, WriteUtc = info.WriteUtc, Hashes = hashes, Error = info.Error,
            Databases = info.Files.Where(f => f.Path.EndsWith(".cdb", StringComparison.OrdinalIgnoreCase)).Select(f => f.Path).ToArray(),
            Scripts = info.Files.Count(f => f.Path.EndsWith(".pex", StringComparison.OrdinalIgnoreCase)),
        };

        _lists[path] = info; _recent.AddLast(path); _keptFiles += info.Files.Count;
        while (_keptFiles > KeptFiles && _recent.Count > 1) Forget(_recent.First!.Value);
        return info;
    }

    void Forget(string path)
    {
        if (_lists.Remove(path, out var gone)) _keptFiles -= gone.Files.Count;
        _recent.Remove(path);
    }

    /// <summary>Which paths an archive holds. Taken from memory when the file has the same size and date; read again
    /// otherwise, from the archive's name table alone (<see cref="PathsOf"/>).</summary>
    public ArchiveNames Names(string path)
    {
        // The file's size and date are looked at once per report, not for every path asked about
        if (_checkedNow.TryGetValue(path, out var known)) return known;
        string full = System.IO.Path.GetFullPath(path);
        if (!(_names.TryGetValue(full, out var have) && have.SameCopy(new FileInfo(full)))) { PathsOf(full); have = _names[full]; }
        else if (_aheadNow.TryRemove(full, out _) | _readBefore.TryRemove(full, out _)) _readNow.Add(full);
        return _checkedNow[path] = have;
    }

    /// <summary>
    /// Starts reading the names of several archives, each on a thread of its own, from its name table as
    /// <see cref="Names"/> would, and returns at once: one task per archive, true when it read the names now (false
    /// when they were in memory with the same size and date, when Mutagen can't read the table, which
    /// <see cref="Names"/> then reads whole for its error, or when <paramref name="stop"/> came first). Only the path
    /// hashes are made, no path's text. The assets report reads a few archives ahead of where its search stands and
    /// stops the rest once every file is found (GAPS S30). Call <see cref="NoteRead"/> for each one it uses.
    /// </summary>
    /// <param name="before">True when no report runs yet (<see cref="AssetsReport.Prepare"/>): the first report that
    /// uses such an archive counts it as read now.</param>
    public IReadOnlyList<Task<bool>> ReadAhead(IReadOnlyList<string> paths, CancellationToken stop, bool before = false)
    {
        var tasks = new List<Task<bool>>();
        foreach (var p in paths)
        {
            string full = System.IO.Path.GetFullPath(p);
            var fi = new FileInfo(full);
            if (_names.TryGetValue(full, out var have) && have.SameCopy(fi) || _lists.ContainsKey(full)) { tasks.Add(Task.FromResult(false)); continue; }
            // A read already under way for the archive is waited for, not started again
            Lazy<Task<bool>>? mine = null;
            mine = new Lazy<Task<bool>>(() => Task.Run(() =>
            {
                try
                {
                    bool read = ReadHashes(full, fi, stop, before);
                    if (read && !before) _aheadNow[full] = 0;
                    return read;
                }
                finally { _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<bool>>>(full, mine!)); }
            }));
            tasks.Add(_inFlight.GetOrAdd(full, mine).Value);
        }
        return tasks;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<bool>>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Archives whose names were read before any report ran (<see cref="ReadAhead"/>'s before).</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _readBefore = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Counts an archive <see cref="ReadAhead"/> read as read in this report.</summary>
    public void NoteRead(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        _readBefore.TryRemove(full, out _);
        _aheadNow.TryRemove(full, out _);
        _readNow.Add(full);
    }

    static bool ReadHashes(string path, FileInfo fi, CancellationToken stop, bool before)
    {
        try
        {
            var hashes = new List<ulong>();
            var databases = new List<string>();
            int n = 0;
            foreach (var p in Archive.CreateReader(GameRelease.Starfield, path).EnumerateFilePaths())
            {
                if ((++n & 4095) == 0 && stop.IsCancellationRequested) return false;
                hashes.Add(HashOfStored(p));
                if (p.AsSpan().Trim().EndsWith(".cdb", StringComparison.OrdinalIgnoreCase)) databases.Add(Normalize(p));
            }
            var sorted = hashes.ToArray();
            Array.Sort(sorted);
            // Marked before the names are kept, so a report that finds them in memory counts them as read now
            if (before) _readBefore[path] = 0;
            _names[path] = new ArchiveNames
            {
                Path = path, Length = fi.Exists ? fi.Length : 0, WriteUtc = fi.Exists ? fi.LastWriteTimeUtc : default, Hashes = sorted, Databases = databases.ToArray(),
            };
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotImplementedException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    /// <summary><see cref="Hash"/> of <see cref="Normalize"/> of a path as an archive stores it, without making the
    /// normalized text: trimmed, slashes as backslashes, no leading one, lower case.</summary>
    public static ulong HashOfStored(string stored)
    {
        var t = stored.AsSpan().Trim();
        int start = 0;
        while (start < t.Length && t[start] is '\\' or '/') start++;
        t = t[start..];
        Span<char> lower = t.Length <= 256 ? stackalloc char[t.Length] : new char[t.Length];
        int len = t.ToLowerInvariant(lower);
        for (int i = 0; i < len; i++) if (lower[i] == '/') lower[i] = '\\';
        return XxHash3.HashToUInt64(MemoryMarshal.AsBytes(lower[..len]));
    }

    /// <summary>
    /// The paths an archive holds, in its own order, as <see cref="Open"/> would list them, but read from the archive's
    /// name table alone (<see cref="ReadNameTable"/>: Mutagen reads the header and the names at its end, not the file
    /// records and never a file's data). A file list already in memory is used as it is. An archive Mutagen can't list
    /// that way (damaged, not an archive) is read whole through Mutagen's reader, which says what is wrong with it.
    /// The path hashes are kept, for <see cref="Names"/>. Any report that needs only the names reads them here:
    /// assets and materials through <see cref="FileFinder"/>, and archive's summary.
    /// </summary>
    public ArchivePaths PathsOf(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var fi = new FileInfo(path);
        long length = fi.Exists ? fi.Length : 0;
        DateTime written = fi.Exists ? fi.LastWriteTimeUtc : default;
        IReadOnlyList<string>? paths;
        string? error = null;
        if (_lists.TryGetValue(path, out var have) && have.SameCopy(fi)) { paths = have.Files.Select(f => f.Path).ToList(); error = have.Error; }
        else
        {
            _readNow.Add(path);
            paths = ReadNameTable(path);
            if (paths == null) { var whole = Open(path); paths = whole.Files.Select(f => f.Path).ToList(); error = whole.Error; }
        }
        Keep(path, length, written, paths, error);
        return new ArchivePaths { Path = path, Length = length, WriteUtc = written, Paths = paths, Error = error };
    }

    /// <summary>Keeps an archive's path hashes for the whole process (<see cref="Names"/>).</summary>
    static void Keep(string path, long length, DateTime written, IReadOnlyList<string> paths, string? error)
    {
        var hashes = new ulong[paths.Count];
        for (int i = 0; i < hashes.Length; i++) hashes[i] = Hash(paths[i]);
        Array.Sort(hashes);
        _names[path] = new ArchiveNames
        {
            Path = path, Length = length, WriteUtc = written, Hashes = hashes, Error = error,
            Databases = paths.Where(f => f.EndsWith(".cdb", StringComparison.OrdinalIgnoreCase)).ToArray(),
            Scripts = paths.Count(f => f.EndsWith(".pex", StringComparison.OrdinalIgnoreCase)),
        };
    }

    /// <summary>
    /// The paths in a BA2 archive's name table, normalized as <see cref="Open"/> gives them, or null when Mutagen can't
    /// read the archive: the caller then reads it whole, for the error. Mutagen's
    /// <c>IArchiveReader.EnumerateFilePaths</c> (fixes.16, C201) reads the header and the name table alone (no file
    /// record, never a file's data) and gives the paths in the order and spelling of its whole reader's Files; a table
    /// that doesn't add up is left to the whole reader. SFSearch's own reader of the table is gone (GAPS Part 2 row 32):
    /// the same 1,894,720 paths in all 457 archives, in about the same time.
    /// </summary>
    public static List<string>? ReadNameTable(string path)
    {
        try
        {
            var paths = new List<string>();
            foreach (var p in Archive.CreateReader(GameRelease.Starfield, path).EnumerateFilePaths()) paths.Add(Normalize(p));
            return paths;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotImplementedException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>The compiled material databases read, for the whole process like the path hashes: every report session
    /// finds them here while the archive (or the loose file) keeps its size and date. The game's material list is
    /// 390 KB; its contents, once a report needed a texture of a material compiled into it, about 40 MB.</summary>
    static readonly ConcurrentDictionary<string, (ArchiveStamp Stamp, MaterialDatabase Db)> _databases = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _databasesReadNow = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _contentsReadNow = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The compiled material databases read (not taken from memory) since the last <see cref="Begin"/>.</summary>
    public IReadOnlyCollection<string> DatabasesRead => _databasesReadNow;
    /// <summary>The compiled material databases whose contents were read (not taken from memory) since the last <see cref="Begin"/>.</summary>
    public IReadOnlyCollection<string> ContentsRead => _contentsReadNow;

    /// <summary>The whole of a compiled material database (<see cref="MaterialDatabase.Contents"/>): read once per
    /// process, when first asked for. The database's own error, if it has one, is in <see cref="MaterialDatabase.ContentsError"/>.</summary>
    public MaterialContents? Contents(MaterialDatabase db)
    {
        if (db.Error != null) return null;
        bool now = db.LoadContents(() =>
        {
            if (db.Where == "loose")
            {
                var fs = new FileStream(db.Container, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                return (fs, fs.Length);
            }
            var info = Open(db.Container);
            var file = info.Find(db.Path) ?? throw new IOException($"{db.Path} is no longer in {info.Name}");
            // The reader asks for a few bytes at a time; the archive's stream inflates as it goes, so a buffer in between
            return (new BufferedStream(file.File.AsStream(), 1 << 20), file.File.Size);
        });
        if (now) _contentsReadNow.Add(db.Container + "|" + db.Path);
        db.Touch();
        if (now && _idleTimer == null)
        {
            var t = new Timer(_ => ReleaseIfIdle(IdleRelease));
            if (Interlocked.CompareExchange(ref _idleTimer, t, null) == null) t.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            else t.Dispose();
        }
        return db.Contents;
    }

    /// <summary>
    /// How long the databases read whole stay in memory after the last view used one (S33e). Measured in a test
    /// service (notes\fix-s33efg.md): a worker that answered five ordinary calls held 200 to 315 MB; one
    /// materials2 --texture and one --material took it to 836 to 848 MB: the 21 databases' contents are 125 MB of it,
    /// about 25 MB other material lists, the rest the garbage collector's free space (a --texture allocates about
    /// 2.5 GB). Seven idle minutes later it held 198 MB. The next call that needs them reads them again (about 1 to
    /// 2 s for all 21).
    /// </summary>
    internal static TimeSpan IdleRelease = TimeSpan.FromMinutes(5);
    static Timer? _idleTimer;
    static readonly object _releaseGate = new();

    /// <summary>When no view has used any database's contents for <paramref name="idle"/>, forgets them all and
    /// compacts the memory, so the process gives it back. How many were forgotten and the bytes they kept; (0, 0) when
    /// one was used more recently, or none is held.</summary>
    internal static (int Databases, long Kept) ReleaseIfIdle(TimeSpan idle)
    {
        lock (_releaseGate)
        {
            DateTime newest = default;
            foreach (var (_, (_, db)) in _databases) if (db.Contents != null && db.LastUsedUtc > newest) newest = db.LastUsedUtc;
            if (newest == default || DateTime.UtcNow - newest < idle) return (0, 0);
            var r = ReleaseContents();
            if (r.Databases > 0) Compact();
            return r;
        }
    }

    /// <summary>
    /// At the end of a call that read a database whole (S33e, choice b, 2026-11-14): the read allocates about 2.5 GB
    /// and leaves the process holding it as free space until the idle release; compacting at once gives it back
    /// (960 to 587 MB in the test, about 0.1 s). A line for the answer's cost, or null when nothing was read whole.
    /// </summary>
    internal static string? CompactAfterWholeRead(int readNow)
    {
        if (readNow == 0) return null;
        var t = System.Diagnostics.Stopwatch.StartNew();
        long before = Environment.WorkingSet;
        Compact();
        return $"memory given back after the whole read: {SizeText(Math.Max(0, before - Environment.WorkingSet))} in {t.Elapsed.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} s";
    }

    /// <summary>A full, compacting garbage collection: the free space a whole read left is given back to Windows.</summary>
    internal static void Compact()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
    }

    /// <summary>The compiled material databases whose contents are held now, and the bytes they keep (S33e).</summary>
    public static (int Databases, long Kept) ContentsHeld()
    {
        int n = 0; long kept = 0;
        foreach (var (_, (_, db)) in _databases) if (db.Contents is { } c) { n++; kept += c.Kept; }
        return (n, kept);
    }

    /// <summary>Forgets the contents of the databases read whole (S33e) that no view asked for in
    /// <paramref name="unusedFor"/> (all of them when null), or only those of <paramref name="only"/>: they are read
    /// again when next needed. Their material lists stay. How many were forgotten, and the bytes they kept.</summary>
    public static (int Databases, long Kept) ReleaseContents(TimeSpan? unusedFor = null, IEnumerable<MaterialDatabase>? only = null)
    {
        var cut = DateTime.UtcNow - (unusedFor ?? TimeSpan.Zero);
        var set = only?.ToHashSet();
        int n = 0; long kept = 0;
        foreach (var (_, (_, db)) in _databases)
        {
            if (set != null && !set.Contains(db)) continue;
            if (unusedFor != null && db.LastUsedUtc > cut) continue;
            long k = db.UnloadContents();
            if (k > 0) { n++; kept += k; }
        }
        return (n, kept);
    }

    /// <summary>The material list of a compiled material database in an archive, or loose in Data (<paramref name="archive"/>
    /// null). Read once per process, then taken from memory while the archive or the loose file keeps its size and date.</summary>
    public MaterialDatabase Database(string? archive, string path)
    {
        string full = archive == null ? System.IO.Path.Combine(DataFolder, path) : System.IO.Path.GetFullPath(archive);
        var stamp = Stamp(full);
        string key = full + "|" + path;
        if (_databases.TryGetValue(key, out var have) && have.Stamp.SameCopy(new FileInfo(full))) return have.Db;
        _databasesReadNow.Add(key);
        MaterialDatabase db;
        if (archive == null)
        {
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            db = MaterialDatabase.Read(fs, fs.Length, path, "loose", full);
        }
        else
        {
            var info = Open(full);
            var file = info.Find(path) ?? throw new IOException($"{path} is no longer in {info.Name}");
            using var s = file.File.AsStream();
            db = MaterialDatabase.Read(s, file.File.Size, path, info.Name, full);
        }
        _databases[key] = (stamp, db);
        return db;
    }

    public static ulong Hash(string path) => XxHash3.HashToUInt64(MemoryMarshal.AsBytes(path.ToLowerInvariant().AsSpan()));

    /// <summary>"Data/Meshes/x.nif", "\meshes\x.nif" → "Meshes\x.nif": backslashes, no leading one. Letter case is kept.</summary>
    public static string Normalize(string path) => path.Trim().Replace('/', '\\').TrimStart('\\');

    /// <summary>The same, and a leading "Data\" taken off: how a material names its textures.</summary>
    public static string DataRelative(string path)
    {
        string p = Normalize(path);
        return p.StartsWith("data\\", StringComparison.OrdinalIgnoreCase) ? p[5..] : p;
    }

    /// <summary>Every archive in the Data folder, by name.</summary>
    public List<string> AllInData() =>
        Directory.Exists(DataFolder) ? Directory.GetFiles(DataFolder, "*.ba2").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();

    /// <summary>A plugin's own archives in a folder: "&lt;plugin name&gt; - &lt;part&gt;.ba2", the names the game loads with the plugin.</summary>
    public static List<string> OfPlugin(string folder, string pluginFileName)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(pluginFileName);
        if (!Directory.Exists(folder)) return new List<string>();
        return Directory.GetFiles(folder, "*.ba2")
            .Where(f => System.IO.Path.GetFileName(f).StartsWith(stem + " - ", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The loose files under the Data folder's own folders, as Data-relative paths. Read once per report.</summary>
    public HashSet<string> Loose
    {
        get
        {
            if (_loose != null) return _loose;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(DataFolder))
                foreach (var dir in Directory.EnumerateDirectories(DataFolder))
                {
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
                            set.Add(System.IO.Path.GetRelativePath(DataFolder, f));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            return _loose = set;
        }
    }

    /// <summary>True when Data holds the path as a loose file (in one of its folders).</summary>
    public bool IsLoose(string dataRelativePath) => dataRelativePath.Contains('\\') && Loose.Contains(dataRelativePath);

    /// <summary>A size as every new tool writes it (<see cref="SFSearch.Records.Sizes"/>).</summary>
    public static string SizeText(long bytes) => SFSearch.Records.Sizes.Text(bytes);
}
