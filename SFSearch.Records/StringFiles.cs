// The string files of localized plugins: where they are, and their stamps (GAPS S15).
//  - A localized plugin keeps its texts in .strings, .dlstrings and .ilstrings files, one of each per language. They
//    are read the way Mutagen reads them (Strings\StringsFolderLookupFactory): the loose files of the Strings folder
//    first, then, for a kind and language no loose file has, the first archive that holds it among the archives
//    Mutagen applies to the plugin (those named after it, and those the ini lists), in Mutagen's order.
//  - The stamp of a loose file is its quick stamp (size, date, a hash of the first and last 64 KB, as for a plugin);
//    the stamp of a file in an archive is the archive's quick stamp. No file is hashed whole. Compared by equality.
//  - What an archive holds in its Strings folder is read from its name table once per copy of the archive (path, size,
//    date) and kept in the scratch folder (strings-archives.tsv), so that later programs on the same scratch folder
//    don't read the name tables again. The first look over all of Data reads about 100 MB of name tables.
//  - A plugin outside Data (a copy in a work folder) reads loose string files from the Strings folder beside it when
//    that folder holds string files of the plugin; otherwise from Data's Strings folder. Archives always come from Data.
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Meta;
using Mutagen.Bethesda.Strings;
using SFSearch.Index;

namespace SFSearch.Records;

/// <summary>Where localized plugins' string files are found, and their stamps, for the header index.</summary>
public sealed class StringFiles : StringFilesSource
{
    readonly string? _data;
    readonly string? _cacheFile;

    /// <param name="dataFolder">The Data folder: its Strings folder and its archives. Null: each plugin's own folder.</param>
    /// <param name="scratchFolder">Where the list of what each archive holds is kept between programs. Null: not kept.</param>
    public StringFiles(string? dataFolder, string? scratchFolder)
    {
        _data = dataFolder == null ? null : Path.GetFullPath(dataFolder);
        _cacheFile = scratchFolder == null ? null : Path.Combine(scratchFolder, "strings-archives.tsv");
    }

    public override IStringFilesLook Look() => new TheLook(this);

    // ------------------------------------------------------------------ The rules both the stamp and the reading use
    static readonly StringsLanguageFormat Format = GameConstants.Get(GameRelease.Starfield).StringsLanguageFormat!.Value;

    /// <summary>True when the file name is one of the plugin's string files; the kind and language are given back.</summary>
    static bool IsStringFileOf(string mod, string fileName, out string kindAndLanguage)
    {
        kindAndLanguage = "";
        if (!StringsUtility.TryRetrieveInfoFromString(Format, fileName, out var source, out var language, out var modName)) return false;
        if (!mod.AsSpan().Equals(modName, StringComparison.OrdinalIgnoreCase)) return false;
        kindAndLanguage = source + "|" + language;
        return true;
    }

    static bool SamePath(string a, string b) =>
        Path.TrimEndingDirectorySeparator(a).Equals(Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The folder a plugin's loose string files are read from: Data's Strings folder; for a plugin outside Data, the
    /// Strings folder beside it when that folder holds string files of the plugin.
    /// </summary>
    public static string LooseFolder(string pluginPath, string pluginName, string? dataFolder)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(pluginPath))!;
        string data = dataFolder == null ? dir : Path.GetFullPath(dataFolder);
        string inData = Path.Combine(data, "Strings");
        if (SamePath(dir, data)) return inData;
        string beside = Path.Combine(dir, "Strings");
        string mod = Path.GetFileNameWithoutExtension(pluginName);
        try
        {
            if (Directory.Exists(beside) && Directory.EnumerateFiles(beside, mod + "*").Any(f => IsStringFileOf(mod, Path.GetFileName(f), out _))) return beside;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return inData;
    }

    /// <summary>What Mutagen is given to read a plugin's texts: the language, Data's archives, and the loose folder of
    /// <see cref="LooseFolder"/>. The same rules as the stamp, so the stamp names the files the texts come from.</summary>
    public static StringsReadParameters ReadParameters(string pluginPath, string pluginName, string? dataFolder, Language language) => new()
    {
        TargetLanguage = language,
        BsaFolderOverride = dataFolder,
        StringsFolderOverride = LooseFolder(pluginPath, pluginName, dataFolder),
    };

    // ------------------------------------------------------------------ What each archive holds
    /// <summary>The names (lower case, without the folder) of the files in an archive's Strings folder, per copy of the archive.</summary>
    static readonly ConcurrentDictionary<(string Path, long Length, long Ticks), Lazy<string[]>> Held = new();
    static readonly ConcurrentDictionary<string, bool> CacheFilesRead = new(StringComparer.OrdinalIgnoreCase);
    static int _archivesRead;
    static long _readTicks;

    /// <summary>How many archives' name tables this program has read (not taken from the scratch folder's list). For tests.</summary>
    public static int ArchivesRead => _archivesRead;
    /// <summary>The time this program spent reading archives' name tables, on all threads together. For tests.</summary>
    public static double ArchiveSeconds => TimeSpan.FromTicks(Interlocked.Read(ref _readTicks)).TotalSeconds;

    /// <summary>
    /// The files of an archive's Strings folder, from its name table. Mutagen takes a file to be in that folder when
    /// its path starts with "strings"; the same is done here. The paths come from Mutagen's
    /// <c>IArchiveReader.EnumerateFilePaths</c> (fixes.16, C201), which reads the header and the name table alone; SFSearch's
    /// own reader of the table is gone (GAPS Part 2 row 32): the same 1,161 strings files in all 457 archives. An archive
    /// Mutagen can't read holds none.
    /// </summary>
    internal static string[] ReadHeld(string path)
    {
        Interlocked.Increment(ref _archivesRead);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var found = new List<string>();
            foreach (var text in Archive.CreateReader(GameRelease.Starfield, path).EnumerateFilePaths())
            {
                if (!text.StartsWith("strings", StringComparison.OrdinalIgnoreCase)) continue;
                string file = Path.GetFileName(text.Replace('/', '\\')).ToLowerInvariant();
                if (file.EndsWith("strings", StringComparison.Ordinal)) found.Add(file);
            }
            return found.Distinct(StringComparer.Ordinal).ToArray();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotImplementedException or ArgumentException or OverflowException)
        {
            return Array.Empty<string>();
        }
        finally { Interlocked.Add(ref _readTicks, clock.Elapsed.Ticks); }
    }

    void ReadCacheFile()
    {
        if (_cacheFile == null || !CacheFilesRead.TryAdd(_cacheFile, true)) return;
        try
        {
            if (!File.Exists(_cacheFile)) return;
            using var fs = new FileStream(_cacheFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            while (r.ReadLine() is string line)
            {
                var c = line.Split('\t');
                if (c.Length != 4 || !long.TryParse(c[1], NumberStyles.None, CultureInfo.InvariantCulture, out long length) || !long.TryParse(c[2], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)) continue;
                var files = c[3].Length == 0 ? Array.Empty<string>() : c[3].Split('|');
                Held.TryAdd((c[0].ToLowerInvariant(), length, ticks), new Lazy<string[]>(files));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Adds what this program learned to the scratch folder's list, keeping the lines other programs wrote.</summary>
    void WriteCacheFile()
    {
        if (_cacheFile == null) return;
        string tmp = _cacheFile + $".{Environment.ProcessId}.tmp";
        try
        {
            var lines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(_cacheFile))
            {
                using var fs = new FileStream(_cacheFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(fs);
                while (r.ReadLine() is string line) { int tab = line.IndexOf('\t'); if (tab > 0) lines[line[..tab]] = line; }
            }
            foreach (var (key, held) in Held)
                if (held.IsValueCreated)
                    lines[key.Path] = string.Join('\t', key.Path, key.Length.ToString(CultureInfo.InvariantCulture), key.Ticks.ToString(CultureInfo.InvariantCulture), string.Join('|', held.Value));
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            File.WriteAllLines(tmp, lines.Values.OrderBy(l => l, StringComparer.OrdinalIgnoreCase));
            File.Move(tmp, _cacheFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { try { File.Delete(tmp); } catch (IOException) { } }
    }

    // ------------------------------------------------------------------ Mutagen's archive order, when it matters
    /// <summary>Mutagen's order of the archives that apply to a plugin, per folder, plugin and set of archives.</summary>
    static readonly ConcurrentDictionary<(string Data, string Mod, ulong Archives, long Ini), Dictionary<string, int>> Orders = new();

    static string IniPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Starfield", "Starfield.ini");

    // ------------------------------------------------------------------ One look
    sealed record Archive2(string Name, string Path, long Length, long Ticks);

    sealed class Folder
    {
        public required List<Archive2> Archives;
        public required ulong ArchivesKey;
        /// <summary>The archives by the plugin name they may apply to: "X - Main.ba2" and "X.ba2" under "x".</summary>
        public required Dictionary<string, List<Archive2>> ByMod;
        public required Dictionary<string, StringFile> DataLoose;
    }

    sealed class TheLook : IStringFilesLook
    {
        readonly StringFiles _owner;
        readonly ConcurrentDictionary<string, Folder> _folders = new(StringComparer.OrdinalIgnoreCase);
        readonly Lazy<(HashSet<string> Names, long Stamp)> _ini;
        int _learned;

        public TheLook(StringFiles owner)
        {
            _owner = owner;
            owner.ReadCacheFile();
            _ini = new Lazy<(HashSet<string>, long)>(IniArchives);
        }

        /// <summary>The archives the ini lists (Mutagen applies them to every plugin), and the ini's stamp.</summary>
        static (HashSet<string>, long) IniArchives()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = IniPath();
                var fi = new FileInfo(path);
                if (!fi.Exists) return (names, 0);
                foreach (var n in Mutagen.Bethesda.Archives.Archive.GetIniListings(GameRelease.Starfield, path)) names.Add(n.String);
                return (names, fi.Length * 31 + fi.LastWriteTimeUtc.Ticks);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return (names, -1); }
        }

        Folder FolderOf(string data) => _folders.GetOrAdd(data, d =>
        {
            var archives = new List<Archive2>();
            var loose = new Dictionary<string, StringFile>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var fi in new DirectoryInfo(d).EnumerateFiles("*.ba2"))
                    if (fi.Name.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase)) archives.Add(new Archive2(fi.Name, fi.FullName, fi.Length, fi.LastWriteTimeUtc.Ticks));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            foreach (var f in LooseFiles(Path.Combine(d, "Strings"), null)) loose[f.Name] = f;
            archives.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            ulong key = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(string.Join("|", archives.Select(a => a.Name.ToLowerInvariant()))));
            var byMod = new Dictionary<string, List<Archive2>>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in archives)
            {
                string bare = Path.GetFileNameWithoutExtension(a.Name);
                int dash = bare.LastIndexOf(" - ", StringComparison.Ordinal);
                foreach (var k in dash < 0 ? new[] { bare } : new[] { bare, bare[..dash] })
                {
                    if (!byMod.TryGetValue(k, out var list)) byMod[k] = list = new();
                    if (!list.Contains(a)) list.Add(a);
                }
            }
            return new Folder { Archives = archives, ArchivesKey = key, DataLoose = loose, ByMod = byMod };
        });

        /// <summary>The string files in a folder (only those whose name starts with the plugin's, when one is given).</summary>
        static List<StringFile> LooseFiles(string folder, string? mod)
        {
            var list = new List<StringFile>();
            try
            {
                if (!Directory.Exists(folder)) return list;
                foreach (var fi in new DirectoryInfo(folder).EnumerateFiles(mod == null ? "*strings" : mod + "*strings"))
                    if (fi.Name.EndsWith("strings", StringComparison.OrdinalIgnoreCase))
                        list.Add(new StringFile(fi.Name.ToLowerInvariant(), fi.FullName, false, new QuickStamp(fi.Length, fi.LastWriteTimeUtc.Ticks, 0)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return list;
        }

        // The quick stamps taken in this look: an archive that holds files of several plugins is read once
        readonly ConcurrentDictionary<string, QuickStamp> _quick = new(StringComparer.OrdinalIgnoreCase);

        QuickStamp QuickOf(string path) => _quick.GetOrAdd(path, p =>
        {
            try { return QuickStamp.Read(p); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return default; }
        });

        string[] HeldBy(Archive2 a)
        {
            var lazy = Held.GetOrAdd((a.Path.ToLowerInvariant(), a.Length, a.Ticks), k => new Lazy<string[]>(() => { Interlocked.Exchange(ref _learned, 1); return ReadHeld(a.Path); }));
            return lazy.Value;
        }

        public StringsStamp Stamp(string pluginPath, string pluginName)
        {
            string full = Path.GetFullPath(pluginPath);
            string data = _owner._data ?? Path.GetDirectoryName(full)!;
            string mod = Path.GetFileNameWithoutExtension(pluginName);
            var folder = FolderOf(data);
            // kind|language → the file it is read from. Loose files first, as Mutagen reads them.
            var chosen = new Dictionary<string, StringFile>(StringComparer.Ordinal);
            string loose = LooseFolder(full, pluginName, _owner._data);
            var looseFiles = SamePath(loose, Path.Combine(data, "Strings")) ? folder.DataLoose.Values.ToList() : LooseFiles(loose, mod);
            foreach (var f in looseFiles.OrderBy(f => f.Name, StringComparer.Ordinal))
                if (IsStringFileOf(mod, f.Name, out var kl)) chosen[kl] = f with { Stamp = QuickOf(f.From) };

            // The archives that apply to the plugin and hold a string file of it
            var ini = _ini.Value.Names;
            var modKey = ModKey.FromFileName(pluginName);
            var holders = new List<(Archive2 Archive, List<(string Name, string KindLanguage)> Files)>();
            var candidates = folder.ByMod.TryGetValue(mod, out var named) ? named : new List<Archive2>();
            if (ini.Count > 0) candidates = candidates.Concat(folder.Archives.Where(a => ini.Contains(a.Name))).Distinct().ToList();
            foreach (var a in candidates)
            {
                if (!ini.Contains(a.Name) && !Mutagen.Bethesda.Archives.Archive.IsApplicable(GameRelease.Starfield, modKey, a.Name)) continue;
                List<(string, string)>? mine = null;
                foreach (var name in HeldBy(a))
                    if (IsStringFileOf(mod, name, out var kl)) (mine ??= new()).Add((name, kl));
                if (mine != null) holders.Add((a, mine));
            }
            if (holders.Count > 1)
            {
                // Two archives hold string files of the plugin: the first in Mutagen's own order wins
                var order = Orders.GetOrAdd((data.ToLowerInvariant(), mod.ToLowerInvariant(), folder.ArchivesKey, _ini.Value.Stamp), k =>
                {
                    var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        foreach (var p in Mutagen.Bethesda.Archives.Archive.GetApplicableArchivePaths(GameRelease.Starfield, data, modKey))
                            d.TryAdd(Path.GetFileName(p.Path), d.Count);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException) { }
                    return d;
                });
                holders = holders.OrderBy(h => order.TryGetValue(h.Archive.Name, out int i) ? i : int.MaxValue).ThenBy(h => h.Archive.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            foreach (var (a, files) in holders)
                foreach (var (name, kl) in files)
                    chosen.TryAdd(kl, new StringFile(name, a.Path, true, QuickOf(a.Path)));
            return StringsStamp.Of(chosen.Values.ToList());
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _learned, 0) == 1) _owner.WriteCacheFile();
        }
    }
}
