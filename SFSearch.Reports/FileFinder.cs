// Where a plugin's files are: a loose file in Data first (the game uses that one), then the plugin's own archives,
// its masters' archives, the archives of the other loaded plugins, and last the archives the game doesn't load (their
// plugin isn't in the load order, or isn't in Data at all). An archive's file list is read only when the search gets
// to it: most paths are found in the plugin's own archives or an early archive of a master.
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using SFSearch.Compare;
using SFSearch.Index;

namespace SFSearch.Reports;

public enum FileOrigin
{
    /// <summary>Nowhere: no loose file and no archive in Data holds it.</summary>
    None,
    Loose,
    /// <summary>One of the plugin's own archives.</summary>
    Own,
    /// <summary>An archive of one of its masters (the game's own archives are Starfield.esm's).</summary>
    Master,
    /// <summary>An archive of a loaded plugin that isn't one of its masters.</summary>
    OtherLoaded,
    /// <summary>Only an archive the game doesn't load.</summary>
    NotLoaded,
}

/// <summary>Where a file was found.</summary>
/// <param name="Where">"loose", or the archive's name. Null when it is nowhere.</param>
/// <param name="FoundAs">The Data-relative path it was found under.</param>
public readonly record struct FileHit(FileOrigin Origin, string? Where, string? FoundAs, ArchiveNames? Archive)
{
    public bool Found => Origin is not (FileOrigin.None or FileOrigin.NotLoaded);
}

public sealed class FileFinder
{
    readonly ArchiveStore _store;
    readonly List<(string Path, FileOrigin Origin)> _order = new();
    readonly Dictionary<string, FileHit> _found = new(StringComparer.OrdinalIgnoreCase);

    public int OwnCount { get; }
    public int MasterCount { get; }
    public int OtherCount { get; }
    public int NotLoadedCount { get; }
    public int Archives => _order.Count;
    /// <summary>The archives in the order files are looked for in them, each with what it is to the plugin.</summary>
    public IReadOnlyList<(string Path, FileOrigin Origin)> Order => _order;

    /// <param name="masters">The plugin's masters, as file names.</param>
    public FileFinder(ReportSession s, IReadOnlyList<string> masters, ArchiveTarget own)
    {
        _store = s.Archives;
        var ownPaths = new HashSet<string>(own.Archives.Select(a => a.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var a in own.Archives) _order.Add((a.Path, FileOrigin.Own));
        var masterStems = new HashSet<string>(masters.Select(m => Path.GetFileNameWithoutExtension(m)), StringComparer.OrdinalIgnoreCase);
        var master = new List<string>(); var other = new List<string>(); var notLoaded = new List<string>();
        foreach (var path in _store.AllInData())
        {
            if (ownPaths.Contains(path)) continue;
            string name = Path.GetFileName(path);
            int at = name.IndexOf(" - ", StringComparison.Ordinal);
            string stem = at > 0 ? name[..at] : "";
            if (masterStems.Contains(stem)) master.Add(path);
            else if (OwnerOf(s.Index, name) is { Order.Enabled: true }) other.Add(path);
            else notLoaded.Add(path);
        }
        foreach (var m in master) _order.Add((m, FileOrigin.Master));
        foreach (var m in other) _order.Add((m, FileOrigin.OtherLoaded));
        foreach (var m in notLoaded) _order.Add((m, FileOrigin.NotLoaded));
        OwnCount = own.Archives.Count; MasterCount = master.Count; OtherCount = other.Count; NotLoadedCount = notLoaded.Count;
    }

    /// <summary>The plugin of Data an archive belongs to by its name ("&lt;plugin name&gt; - &lt;part&gt;.ba2"), or null.</summary>
    public static IndexedPlugin? OwnerOf(HeaderIndex index, string archiveName)
    {
        int at = archiveName.IndexOf(" - ", StringComparison.Ordinal);
        if (at <= 0) return null;
        string stem = archiveName[..at];
        foreach (var ext in new[] { ".esm", ".esp", ".esl" })
            if (index.Plugin(stem + ext) is { } p) return p;
        return null;
    }

    /// <summary>Finds a file by the spellings it may have, each tried in turn at each place.</summary>
    public FileHit Find(IReadOnlyList<string> candidates)
    {
        if (candidates.Count == 0) return default;
        if (_found.TryGetValue(candidates[0], out var have)) return have;
        FileHit hit = default;
        string? loose = candidates.FirstOrDefault(_store.IsLoose);
        if (loose != null) hit = new FileHit(FileOrigin.Loose, "loose", loose, null);
        else
            foreach (var (path, origin) in _order)
            {
                var names = _store.Names(path);
                string? at = candidates.FirstOrDefault(names.Has);
                if (at == null) continue;
                hit = new FileHit(origin, names.Name, at, names);
                break;
            }
        return _found[candidates[0]] = hit;
    }

    public FileHit Find(string dataRelativePath) => Find(new[] { dataRelativePath });

    /// <summary>Finds a file as <see cref="Find"/> does, the archives' names read a few at a time side by side
    /// (<see cref="FindAll"/>).</summary>
    public FileHit FindAhead(IReadOnlyList<string> candidates)
    {
        FindAll(new[] { candidates });
        return Find(candidates);
    }

    /// <summary>
    /// Finds many files at once, each as <see cref="Find"/> would (later calls of Find answer from what this found).
    /// The archives are taken in the same order, a few at a time: the plugin's own first, then as many as the
    /// computer has cores, their names read side by side (<see cref="ArchiveStore.ReadAhead"/>) and looked in one
    /// after another in that order. Once every file is found the reads still under way are stopped.
    /// </summary>
    public void FindAll(IEnumerable<IReadOnlyList<string>> candidateLists)
    {
        var pending = new List<IReadOnlyList<string>>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidateLists)
        {
            if (c.Count == 0 || _found.ContainsKey(c[0]) || !keys.Add(c[0])) continue;
            string? loose = c.FirstOrDefault(_store.IsLoose);
            if (loose != null) { _found[c[0]] = new FileHit(FileOrigin.Loose, "loose", loose, null); continue; }
            pending.Add(c);
        }
        var stop = new CancellationTokenSource();
        try
        {
            int at = 0;
            while (pending.Count > 0 && at < _order.Count)
            {
                int width = at == 0 && OwnCount > 0 ? OwnCount : Math.Max(2, Environment.ProcessorCount);
                var window = _order.Skip(at).Take(width).ToList();
                var reads = _store.ReadAhead(window.Select(w => w.Path).ToList(), stop.Token);
                for (int k = 0; k < window.Count && pending.Count > 0; k++)
                {
                    var (path, origin) = window[k];
                    if (reads[k].Result) _store.NoteRead(path);
                    var names = _store.Names(path);
                    pending.RemoveAll(c =>
                    {
                        string? hit = c.FirstOrDefault(names.Has);
                        if (hit == null) return false;
                        _found[c[0]] = new FileHit(origin, names.Name, hit, names);
                        return true;
                    });
                }
                at += width;
            }
        }
        finally { stop.Cancel(); }
        foreach (var c in pending) _found[c[0]] = default;
    }

    readonly Dictionary<(string, string), bool> _mayHold = new();

    /// <summary>
    /// False when an archive holds no file with this extension, from its paths as Mutagen's
    /// <c>IArchiveReader.EnumerateFilePaths</c> (fixes.16, C201) gives them: the header and the name table alone, read
    /// until the first such path. True when Mutagen can't list the archive (the name lookup then decides). SFSearch's own
    /// check of the entry table's extensions is gone (GAPS Part 2 row 32): the same answer for all 457 archives.
    /// </summary>
    bool MayHold(string archive, string extension)
    {
        if (_mayHold.TryGetValue((archive, extension), out bool may)) return may;
        may = true;
        try { may = Archive.CreateReader(GameRelease.Starfield, archive).EnumerateFilePaths().Any(p => p.EndsWith(extension, StringComparison.OrdinalIgnoreCase)); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotImplementedException or ArgumentException or OverflowException) { }
        return _mayHold[(archive, extension)] = may;
    }

    /// <summary>
    /// Finds a file that isn't a texture (a compiled script): an archive that holds no file of its extension is passed
    /// over without reading its names. With <paramref name="loose"/> false only archives are looked in; with
    /// <paramref name="only"/>, only that kind of archive.
    /// </summary>
    public FileHit FindNonTexture(string dataRelativePath, bool loose = true, FileOrigin? only = null)
    {
        if (loose && _store.IsLoose(dataRelativePath)) return new FileHit(FileOrigin.Loose, "loose", dataRelativePath, null);
        string extension = Path.GetExtension(dataRelativePath).ToLowerInvariant();
        foreach (var (path, origin) in _order)
        {
            if ((only != null && origin != only) || !MayHold(path, extension)) continue;
            var names = _store.Names(path);
            if (names.Has(dataRelativePath)) return new FileHit(origin, names.Name, dataRelativePath, names);
        }
        return default;
    }

    /// <summary>The bytes of a file that was found.</summary>
    public byte[] Read(FileHit hit) =>
        hit.Origin == FileOrigin.Loose ? File.ReadAllBytes(Path.Combine(_store.DataFolder, hit.FoundAs!))
        : _store.Open(hit.Archive!.Path).Find(hit.FoundAs!)?.Bytes() ?? throw new IOException($"{hit.FoundAs} is no longer in {hit.Where}");

    /// <summary>The line of an answer's source that says where files were looked for.</summary>
    public string SourceLine() =>
        $"files looked for in {ReportAnswer.N(_store.Loose.Count)} loose files and {ReportAnswer.N(Archives)} archives in Data ({ReportAnswer.N(OwnCount)} its own, {ReportAnswer.N(MasterCount)} of its masters, {ReportAnswer.N(OtherCount)} of other loaded plugins, {ReportAnswer.N(NotLoadedCount)} the game doesn't load)";
}
