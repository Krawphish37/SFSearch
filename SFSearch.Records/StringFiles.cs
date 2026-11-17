// The string files of localized plugins: where they are, and their stamps (GAPS S15).
//  - A localized plugin keeps its texts in .strings, .dlstrings and .ilstrings files, one of each per language. Where
//    each is read from is Mutagen's rule (StringsFolderLookupOverlay.ResolveSources): the loose files of the Strings
//    folder first, then, for a kind and language no loose file has, the first archive that holds it among the archives
//    Mutagen applies to the plugin (those named after it, and those the ini lists), in Mutagen's order. Mutagen resolves
//    a whole load order in one pass since fixes.22 (C74, GAPS S167): the folders are listed once and each archive's
//    strings listing is read once for all the plugins (Data's 41 localized plugins in about 0.3-0.8 s). SFSearch's own
//    copy of the rule (the archives by plugin name, the ini's archives, Mutagen's archive order, the archives' strings
//    listings kept in the scratch folder between programs) is gone.
//  - The stamp of a loose file is its quick stamp (size, date, a hash of the first and last 64 KB, as for a plugin);
//    the stamp of a file in an archive is the archive's quick stamp. No file is hashed whole. Compared by equality.
//  - One Mutagen StringsSourcesResolver is kept for each Data folder and loose folder (fixes.23, C77, GAPS S185). It
//    keeps each archive's strings listing, read again only when that archive's size or date changes, and on every call
//    looks again at the Strings folder's files and the Data folder's archives: the quick check on every use
//    (StringsNow) takes about 1.6 ms for one plugin where a new resolver reads the ini's archives' name tables (about
//    0.1 s). It reads the game's ini and the load order (plugins.txt, the .ccc file) once, so a new one is made when one
//    of those files' size or date changes. Nothing is kept between programs.
//  - A plugin outside Data (a copy in a work folder) reads loose string files from the Strings folder beside it when
//    that folder holds string files of the plugin; otherwise from Data's Strings folder. Archives always come from Data.
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Inis;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Meta;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Strings;
using SFSearch.Index;

namespace SFSearch.Records;

/// <summary>Where localized plugins' string files are found, and their stamps, for the header index.</summary>
public sealed class StringFiles : StringFilesSource
{
    readonly string? _data;

    /// <param name="dataFolder">The Data folder: its Strings folder and its archives. Null: each plugin's own folder.</param>
    public StringFiles(string? dataFolder)
    {
        _data = dataFolder == null ? null : Path.GetFullPath(dataFolder);
    }

    // ------------------------------------------------------------------ The rules both the stamp and the reading use
    static readonly StringsLanguageFormat Format = GameConstants.Get(GameRelease.Starfield).StringsLanguageFormat!.Value;

    /// <summary>True when the file name is one of the plugin's string files.</summary>
    static bool IsStringFileOf(string mod, string fileName)
    {
        if (!StringsUtility.TryRetrieveInfoFromString(Format, fileName, out _, out _, out var modName)) return false;
        return mod.AsSpan().Equals(modName, StringComparison.OrdinalIgnoreCase);
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
            if (Directory.Exists(beside) && Directory.EnumerateFiles(beside, mod + "*").Any(f => IsStringFileOf(mod, Path.GetFileName(f)))) return beside;
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

    // ------------------------------------------------------------------ Mutagen's resolvers, one for each Data folder and loose folder
    /// <summary>A resolver and the key of the ini and load-order files it read when it was made.</summary>
    sealed record Kept(ulong Inputs, StringsSourcesResolver Resolver);

    readonly Dictionary<(string Data, string Loose), Kept> _resolvers = new();

    static void AddFile(StringBuilder sb, string? path)
    {
        if (path == null) { sb.Append("-\n"); return; }
        try
        {
            var fi = new FileInfo(path);
            sb.Append(path.ToLowerInvariant()).Append('|');
            if (fi.Exists) sb.Append(fi.Length.ToString(CultureInfo.InvariantCulture)).Append('|').Append(fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            sb.Append('\n');
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { sb.Append("?\n"); }
    }

    /// <summary>
    /// The key of what a resolver reads once (StringsSourcesResolver: "make a new resolver when they change"): the game's
    /// ini (the archives it lists), plugins.txt and the .ccc file (the archives' order), by size and date, at the paths
    /// Mutagen's own Ini, PluginListings and CreationClubListings give.
    /// </summary>
    static ulong ReadOnceInputs(string data)
    {
        var sb = new StringBuilder();
        string? ini = null, pluginsTxt = null, ccc = null;
        try { ini = Ini.TryGetTypicalPath(GameRelease.Starfield)?.Path; } catch (Exception e) when (e is not OutOfMemoryException) { }
        try { pluginsTxt = PluginListings.GetListingsPath(GameRelease.Starfield); } catch (Exception e) when (e is not OutOfMemoryException) { }
        try { ccc = CreationClubListings.GetListingsPath(GameCategory.Starfield, data)?.Path; } catch (Exception e) when (e is not OutOfMemoryException) { }
        AddFile(sb, ini); AddFile(sb, pluginsTxt); AddFile(sb, ccc);
        return XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    StringsSourcesResolver ResolverFor(string data, string loose)
    {
        ulong inputs = ReadOnceInputs(data);
        lock (_resolvers)
        {
            if (!_resolvers.TryGetValue((data, loose), out var kept) || kept.Inputs != inputs)
                _resolvers[(data, loose)] = kept = new Kept(inputs, new StringsSourcesResolver(GameRelease.Starfield, data,
                    new StringsReadParameters { StringsFolderOverride = loose, BsaFolderOverride = data }, fileSystem: null));
            return kept.Resolver;
        }
    }

    /// <summary>Mutagen's sources for these plugins (all in one Data folder and loose folder), in one pass. A pass Mutagen
    /// can't finish (an archive it can't read) is done again plugin by plugin, so that only the plugins that archive
    /// applies to have no sources.</summary>
    IReadOnlyList<StringsTableSource>?[] SourcesOf(string data, string loose, IReadOnlyList<ModKey> mods)
    {
        var resolver = ResolverFor(data, loose);
        try
        {
            var all = resolver.ResolveSources(mods);
            return mods.Select(m => all.GetValueOrDefault(m)).ToArray();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return mods.Select(m =>
            {
                try { return resolver.ResolveSources(m); }
                catch (Exception e2) when (e2 is not OutOfMemoryException) { return null; }
            }).ToArray();
        }
    }

    public override StringsStamp[] Stamps(IReadOnlyList<(string PluginPath, string PluginName)> plugins)
    {
        var result = new StringsStamp[plugins.Count];
        Array.Fill(result, StringsStamp.NotStamped);
        // The plugins by the folders their string files are read from: one pass of Mutagen's for each (Data's plugins, all
        // in one)
        var groups = new Dictionary<(string Data, string Loose), List<(int Index, ModKey Mod)>>();
        for (int i = 0; i < plugins.Count; i++)
        {
            var (path, name) = plugins[i];
            if (!ModKey.TryFromNameAndExtension(name, out var mod)) continue;
            string full = Path.GetFullPath(path);
            string data = _data ?? Path.GetDirectoryName(full)!;
            var key = (data, LooseFolder(full, name, _data));
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
            list.Add((i, mod));
        }
        // An archive holds the files of several plugins: its quick stamp is taken once
        var quick = new Dictionary<string, QuickStamp>(StringComparer.OrdinalIgnoreCase);
        QuickStamp QuickOf(string p)
        {
            if (quick.TryGetValue(p, out var q)) return q;
            try { q = QuickStamp.Read(p); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { q = default; }
            return quick[p] = q;
        }
        foreach (var ((data, loose), list) in groups)
        {
            var sources = SourcesOf(data, loose, list.Select(x => x.Mod).ToList());
            for (int j = 0; j < list.Count; j++)
            {
                if (sources[j] is not { } found) continue;
                var files = new List<StringFile>(found.Count);
                foreach (var s in found)
                {
                    string from = Path.GetFullPath(s.ArchivePath ?? s.SourcePath);
                    string name = Path.GetFileName((s.ArchiveEntryPath ?? s.SourcePath).Replace('/', '\\')).ToLowerInvariant();
                    files.Add(new StringFile(name, from, s.FromArchive, QuickOf(from)));
                }
                result[list[j].Index] = StringsStamp.Of(files);
            }
        }
        return result;
    }
}
