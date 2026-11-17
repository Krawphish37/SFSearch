using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("sfsindex-tests")]

namespace SFSearch.Index;

public sealed class IndexOptions
{
    /// <summary>Where segments are written. Never a folder that holds plugins, and never inside one.</summary>
    public required string ScratchFolder { get; init; }
    /// <summary>SFSearch's, Mutagen's and the schema's versions as one text. Part of every reading key.</summary>
    public string VersionText { get; init; } = "";
    public int Threads { get; init; } = Math.Min(Environment.ProcessorCount, 8);
    /// <summary>plugins.txt. Null means Starfield's own; an empty text means none.</summary>
    public string? PluginsTxt { get; init; }
    /// <summary>Starfield.ccc. Null means the one beside the Data folder; an empty text means none.</summary>
    public string? Ccc { get; init; }
    /// <summary>Where localized plugins' string files are found. Null: they aren't looked for, and a changed string
    /// file isn't noticed (<see cref="IndexedPlugin.Strings"/> stays <see cref="StringsStamp.NotStamped"/>).</summary>
    public StringFilesSource? StringFiles { get; init; }
    /// <summary>Who decides that a plugin can't be read (the record layer asks Mutagen, which refuses a plugin that holds
    /// one FormKey twice: C204, fixes.17). Asked only for a plugin whose headers repeat a FormKey. Null: nothing is
    /// refused.</summary>
    public RefusalSource? Refusals { get; init; }
    /// <summary>Asked when a refresh lets go of segments no plugin uses any more (a plugin taken out of Data, or the copy a
    /// changed plugin replaced): true keeps their files, so the plugin put back finds its segment (J43, the setting
    /// keepRemovedPluginIndexMB; the program trims them to a size). Null or false: they are deleted at once.</summary>
    public Func<bool>? KeepUnusedSegments { get; init; }
}

/// <summary>Says whether a plugin is refused by its reader, and why (Mutagen's error). The index itself never opens a
/// plugin with Mutagen: the record layer gives this.</summary>
public abstract class RefusalSource
{
    /// <summary>The reader's error when it refuses the plugin, or null when it reads it.</summary>
    public abstract string? Refuses(IndexedPlugin plugin);
}

/// <summary>Where the plugins are: a Data folder, or a list of files (tests mix copies in a temp folder with real masters).</summary>
public sealed class PluginSource
{
    readonly string? _folder;
    readonly List<string>? _files;
    PluginSource(string? folder, List<string>? files) { _folder = folder; _files = files; }

    public static PluginSource Folder(string dataFolder) => new(Path.GetFullPath(dataFolder), null);
    public static PluginSource FromFiles(IEnumerable<string> paths) => new(null, paths.Select(Path.GetFullPath).ToList());
    public string? DataFolder => _folder;

    /// <summary>File name to path, for every plugin that is there now.</summary>
    internal Dictionary<string, string> List()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var paths = _folder != null ? Directory.EnumerateFiles(_folder) : _files!.Where(File.Exists);
        foreach (var f in paths)
        {
            string name = Path.GetFileName(f);
            if (Files.IsPluginName(name)) d.TryAdd(name, f);
        }
        return d;
    }
}

/// <summary>What is wrong with a plugin, as far as record headers and the load order show.</summary>
public sealed class PluginProblems
{
    /// <summary>Masters whose file isn't there. FormIDs that could go through one stay unresolved.</summary>
    public IReadOnlyList<string> MissingMasters { get; internal set; } = Array.Empty<string>();
    /// <summary>Masters that are there but not enabled, while this plugin is.</summary>
    public IReadOnlyList<string> MastersNotLoaded { get; internal set; } = Array.Empty<string>();
    /// <summary>Masters that load after this plugin.</summary>
    public IReadOnlyList<string> MastersLoadedAfter { get; internal set; } = Array.Empty<string>();
    /// <summary>Entries of this plugin that override a record of another type: the sign of a patch built for an older master.</summary>
    public IReadOnlyList<int> TypeMismatches { get; internal set; } = Array.Empty<int>();
    /// <summary>Entries of this plugin whose FormKey belongs to a master that has no such record.</summary>
    public IReadOnlyList<int> OverridesOfNothing { get; internal set; } = Array.Empty<int>();
    public int Unresolved { get; internal set; }
    public int Duplicates { get; internal set; }
    public bool BothSizeFlags { get; internal set; }
    /// <summary>Why the plugin has no segment (not a plugin, can't be walked, changed while it was read).</summary>
    public string? ReadError { get; internal set; }
    /// <summary>Mutagen refuses the plugin: its error (C204: one FormKey held twice).</summary>
    public string? Refused { get; internal set; }
    /// <summary>A master of the plugin is refused (or itself left out), so the plugin is left out too.</summary>
    public string? MasterRefused { get; internal set; }
    /// <summary>A master of the plugin can't be read (a master of its own isn't in Data, or one of its masters can't be
    /// read in turn), so the plugin is left out too, as Mutagen's load order drops it (fixes.17's DropPlugin: "a loaded
    /// plugin whose master was dropped is dropped too").</summary>
    public string? MasterUnreadable { get; internal set; }

    public bool Any => MissingMasters.Count + MastersNotLoaded.Count + MastersLoadedAfter.Count + TypeMismatches.Count + OverridesOfNothing.Count + Unresolved + Duplicates > 0 || BothSizeFlags || ReadError != null || Refused != null || MasterRefused != null || MasterUnreadable != null;

    public IEnumerable<string> Lines()
    {
        if (ReadError != null) yield return "couldn't be read: " + ReadError;
        if (Refused != null) yield return "refused by Mutagen, left out of queries and of the winners: " + Refused;
        if (MasterRefused != null) yield return $"left out of queries and of the winners: its master {MasterRefused} is refused by Mutagen";
        if (MasterUnreadable != null) yield return $"left out of queries and of the winners: its master {MasterUnreadable} can't be loaded (a master of it isn't in Data)";
        if (MissingMasters.Count > 0) yield return $"missing master(s): {string.Join(", ", MissingMasters)}";
        if (Unresolved > 0) yield return $"{Unresolved:N0} record(s) have a FormID that can't be resolved without the missing master(s)";
        if (MastersNotLoaded.Count > 0) yield return $"master(s) not loaded: {string.Join(", ", MastersNotLoaded)}";
        if (MastersLoadedAfter.Count > 0) yield return $"master(s) load after it: {string.Join(", ", MastersLoadedAfter)}";
        if (TypeMismatches.Count > 0) yield return $"{TypeMismatches.Count:N0} override(s) of a different type than their base: built for an older version of a master?";
        if (OverridesOfNothing.Count > 0) yield return $"{OverridesOfNothing.Count:N0} override(s) of a record their master doesn't have";
        if (Duplicates > 0) yield return $"{Duplicates:N0} record(s) repeat a FormKey the plugin already holds";
        if (BothSizeFlags) yield return "the header has both the Light (Small) and the Medium flag; read as Light";
    }
}

/// <summary>One plugin the index knows: its stamps and keys, its segment, its place in the load order, its problems.</summary>
public sealed class IndexedPlugin
{
    public int Id { get; internal set; }
    public string Name { get; internal init; } = "";
    public string Path { get; internal set; } = "";
    public QuickStamp Stamp { get; internal set; }
    public Key128 ContentKey { get; internal set; }
    public Key128 ReadingKey { get; internal set; }
    /// <summary>The string files a localized plugin's texts are read from, with their sizes and dates. Not part of the
    /// content or reading key: the record headers don't change with them, so the segment stays.</summary>
    public StringsStamp Strings { get; internal set; } = StringsStamp.NotStamped;
    public PluginHeader? Header { get; internal set; }
    public IReadOnlyList<MasterStyle> MasterStyles { get; internal set; } = Array.Empty<MasterStyle>();
    public Segment? Segment { get; internal set; }
    public LoadOrderEntry Order { get; internal set; } = new();
    public PluginProblems Problems { get; internal set; } = new();
    /// <summary>Why the plugin is left out of queries and of the winners, as Mutagen's load order drops it (C204):
    /// Mutagen refuses it (<see cref="PluginProblems.Refused"/>) or one of its masters is left out. Null: it is read.</summary>
    public string? LeftOut => Problems.Refused != null ? "Mutagen refuses it: " + Problems.Refused : Problems.MasterRefused != null ? $"its master {Problems.MasterRefused} is refused by Mutagen"
        : Problems.MasterUnreadable != null ? $"its master {Problems.MasterUnreadable} can't be loaded (a master of it isn't in Data)" : null;
    /// <summary>Why the plugin is left out, in a few words for a list: "refused by Mutagen" or "a master can't be loaded". Null: it is read.</summary>
    public string? LeftOutShort => Problems.Refused != null || Problems.MasterRefused != null ? "refused by Mutagen" : Problems.MasterUnreadable != null ? "a master can't be loaded" : null;
    /// <summary>The game can't load the plugin as it is: a master of it isn't in Data, or one of its masters can't be
    /// loaded in turn. Mutagen's load order drops it, and xEdit doesn't load it.</summary>
    public bool CantLoad => Problems.MissingMasters.Count > 0 || Problems.MasterUnreadable != null;
    /// <summary>The index's plugin Id for each place in the segment's name table.</summary>
    internal int[] OwnerToPlugin = Array.Empty<int>();
    /// <summary>For sorting holders: enabled plugins in load order, then the rest.</summary>
    internal long Rank;
    /// <summary>What the last read of this plugin took, when this refresh read it.</summary>
    public PassTimings? Timings { get; internal set; }
    FormIdResolver? _formIds;
    /// <summary>Reads a FormID as this plugin's file writes it, and writes one: the index's own resolver, with this
    /// plugin's masters and their sizes.</summary>
    public FormIdResolver FormIds => _formIds ??= FormIdResolver.For(this);
    public override string ToString() => Name;
}

/// <summary>One record in one plugin.</summary>
public readonly record struct RecordRef(int Plugin, int Entry);

/// <summary>Every plugin that holds a FormKey, lowest in the load order first (plugins that aren't loaded come last), and which one wins.</summary>
public sealed class RecordVersions
{
    public IReadOnlyList<RecordRef> Holders { get; init; } = Array.Empty<RecordRef>();
    /// <summary>The place in <see cref="Holders"/> of the last enabled plugin, or -1 when none is enabled.</summary>
    public int Winner { get; init; } = -1;
}

/// <summary>A record as text, for printing.</summary>
public sealed record RecordInfo(string Plugin, string FormKey, string Signature, string? EditorId, long Offset, uint Size, uint Flags, string? Parent, bool IsOverride, bool Unresolved)
{
    /// <summary>EditorID (FormID:plugin), the way records are named to people.</summary>
    public string Label => $"{EditorId ?? "(no EditorID)"} ({FormKey})";
}

/// <summary>What a refresh did.</summary>
public sealed class RefreshReport
{
    public List<string> Added { get; } = new();
    /// <summary>The file's content changed: its segment was replaced.</summary>
    public List<string> ContentChanged { get; } = new();
    /// <summary>Size, date or the hashed ends changed but the whole content didn't (a date set backwards, for one).</summary>
    public List<string> StampChangedOnly { get; } = new();
    /// <summary>A master changed size or came or went, so the plugin's FormIDs read differently: it got a new reading key.</summary>
    public List<string> Rekeyed { get; } = new();
    /// <summary>A localized plugin's string files changed (a loose file, or the archive one is read from): its texts
    /// read differently. The plugin itself may be the same.</summary>
    public List<string> StringsChanged { get; } = new();
    /// <summary>For each plugin in <see cref="StringsChanged"/>: which files, in words.</summary>
    public Dictionary<string, string> StringsChanges { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Removed { get; } = new();
    /// <summary>Plugins whose record headers were read in this refresh.</summary>
    public List<string> PassesRun { get; } = new();
    /// <summary>Plugins whose segment was already in the scratch folder.</summary>
    public List<string> SegmentsReused { get; } = new();
    /// <summary>Plugins whose whole content was hashed in this refresh.</summary>
    public List<string> Hashed { get; } = new();
    public bool LoadOrderChanged { get; internal set; }
    public bool WinnersReworked { get; internal set; }
    public double Seconds { get; internal set; }
    public double MapSeconds { get; internal set; }
    public bool AnythingChanged => Added.Count + ContentChanged.Count + StampChangedOnly.Count + Rekeyed.Count + StringsChanged.Count + Removed.Count > 0 || LoadOrderChanged;
}

/// <summary>
/// The header index of a set of plugins: a segment per plugin, the load order, who holds each record and who wins.
/// Everything is worked out from the files and checked by equality of stamps on every <see cref="Refresh"/>.
/// One caller at a time: a worker runs one command at a time.
/// </summary>
public sealed class HeaderIndex : IDisposable
{
    readonly PluginSource _source;
    readonly IndexOptions _options;
    readonly Dictionary<string, IndexedPlugin> _byName = new(StringComparer.OrdinalIgnoreCase);
    List<IndexedPlugin> _plugins = new();
    List<LoadOrderEntry> _order = new();
    ulong _orderStamp;
    // The override map: only FormKeys that a plugin other than their owner holds
    ulong[] _ovKeys = Array.Empty<ulong>();
    int[] _ovStart = new int[1];
    RecordRef[] _ovHolders = Array.Empty<RecordRef>();

    public HeaderIndex(PluginSource source, IndexOptions options)
    {
        _source = source; _options = options;
    }

    public string ScratchFolder => _options.ScratchFolder;
    /// <summary>Every plugin that is there, by Id.</summary>
    public IReadOnlyList<IndexedPlugin> Plugins => _plugins;
    /// <summary>The load order: listed plugins first, then the plugins no list names.</summary>
    public IReadOnlyList<LoadOrderEntry> LoadOrderEntries => _order;
    /// <summary>Lines of plugins.txt that name no plugin.</summary>
    public IReadOnlyList<string> BadLoadOrderLines { get; private set; } = Array.Empty<string>();
    public long TotalRecords => _plugins.Sum(p => (long)(p.Segment?.Count ?? 0));
    public long TotalSegmentBytes => _plugins.Sum(p => p.Segment?.FileBytes ?? 0);
    /// <summary>FormKeys held by more than their owner.</summary>
    public int OverriddenRecords => _ovKeys.Length;
    public int OverrideEntries => _ovHolders.Length;

    public IndexedPlugin? Plugin(string name) => _byName.GetValueOrDefault(name);

    /// <summary>True when this index looks for localized plugins' string files.</summary>
    public bool StampsStrings => _options.StringFiles != null;

    /// <summary>
    /// The string files of these plugins as they are now, from one look at the folders (a listing of the archives and
    /// of the loose string files: about a millisecond), to hold against <see cref="IndexedPlugin.Strings"/>. A plugin
    /// that isn't localized, or an index that doesn't look for string files, gives <see cref="StringsStamp.NotStamped"/>.
    /// </summary>
    public StringsStamp[] StringsNow(IReadOnlyList<IndexedPlugin> plugins)
    {
        var result = new StringsStamp[plugins.Count];
        Array.Fill(result, StringsStamp.NotStamped);
        if (_options.StringFiles == null || !plugins.Any(p => p.Header is { Localized: true })) return result;
        var localized = Enumerable.Range(0, plugins.Count).Where(i => plugins[i].Header is { Localized: true }).ToList();
        var stamps = _options.StringFiles.Stamps(localized.Select(i => (plugins[i].Path, plugins[i].Name)).ToList());
        for (int j = 0; j < localized.Count; j++) result[localized[j]] = stamps[j];
        return result;
    }

    public void Dispose()
    {
        foreach (var p in _plugins) p.Segment?.Dispose();
        _plugins = new(); _byName.Clear();
    }

    string SegmentPath(Key128 readingKey) => Path.Combine(_options.ScratchFolder, readingKey + ".seg");
    string ManifestPath => Path.Combine(_options.ScratchFolder, "stamps.tsv");

    // ---------------------------------------------------------------- Refresh
    sealed class Work
    {
        public required string Name, Path;
        public IndexedPlugin? Old;
        public QuickStamp Stamp; public PluginHeader? Header; public bool StampSame;
        public StringsStamp Strings = StringsStamp.NotStamped;
        public Key128 Content; public bool ContentKnown;
        public MasterStyle[] Styles = Array.Empty<MasterStyle>();
        public IndexedPlugin? Result;
        /// <summary>The file was gone when it was looked at again: it isn't part of this refresh (S43).</summary>
        public bool Gone;
    }

    /// <summary>Each master's size, from this refresh's one look at the plugins (Missing for a master that isn't there).</summary>
    static MasterStyle[] StylesOf(PluginHeader? header, Dictionary<string, MasterStyle> styleOf) =>
        header == null ? Array.Empty<MasterStyle>() : header.Masters.Select(m => styleOf.TryGetValue(m, out var s) ? s : MasterStyle.Missing).ToArray();

    /// <summary>True when the file isn't there any more. A delete still pending counts as gone: Windows won't open it.</summary>
    static bool IsGone(string path) => !File.Exists(path);

    /// <summary>
    /// Brings the index in line with the files. Each plugin's quick stamp is compared with the one remembered; only a plugin
    /// whose stamp differs is hashed again, only a plugin whose reading key is new gets a new pass, and a change of
    /// plugins.txt alone reworks the winners with no plugin file opened.
    /// </summary>
    /// <param name="verifyContent">Hash every file again whatever its quick stamp says (the idle sweep). This is what
    /// notices a change in the middle of a file that kept its size, its date and its first and last 64 KB.</param>
    public RefreshReport Refresh(bool verifyContent = false)
    {
        var clock = Stopwatch.StartNew();
        var report = new RefreshReport();
        var files = _source.List();
        GuardScratch(files.Values);
        Directory.CreateDirectory(_options.ScratchFolder);
        var manifest = ReadManifest();
        // Where localized plugins' string files are found: asked once for every plugin of this refresh, after step 1
        var strings = _options.StringFiles;

        // 1. Quick stamps and headers (and the string files of a localized plugin)
        var work = new ConcurrentBag<Work>();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = _options.Threads }, f =>
        {
            var w = new Work { Name = f.Key, Path = f.Value, Old = _byName.GetValueOrDefault(f.Key) };
            try
            {
                w.Stamp = QuickStamp.Read(f.Value, out var start);
                w.StampSame = w.Old != null && w.Old.Path == f.Value && w.Old.Stamp == w.Stamp;
                if (w.StampSame) w.Header = w.Old!.Header;
                else
                {
                    long need = PluginHeader.LengthNeeded(start);
                    w.Header = need > start.Length ? PluginHeader.Read(f.Value) : PluginHeader.Parse(start);
                }
                if (w.StampSame && !verifyContent)
                {
                    w.Content = w.Old!.ContentKey; w.ContentKnown = !w.Content.IsZero;
                    // Another program on this index folder hashed the file whole since (the service's full check at its
                    // start, or the hourly one) and found other content under the same quick stamp: a change in the middle
                    // of the file. Its key is the truth now (J31: kept indexes are checked by a full hash at start).
                    if (manifest.TryGetValue(f.Value, out var seen) && seen.Stamp == w.Stamp && !seen.Content.IsZero && seen.Content != w.Content) w.Content = seen.Content;
                }
                else if (!verifyContent && manifest.TryGetValue(f.Value, out var m) && m.Stamp == w.Stamp) { w.Content = m.Content; w.ContentKnown = true; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { w.Header = null; w.StampSame = false; w.Stamp = default; w.Gone = IsGone(f.Value); }
            work.Add(w);
        });
        // The string files of the localized plugins, all in one look (Mutagen resolves the load order in one pass, C74)
        if (strings != null)
        {
            var localized = work.Where(w => !w.Gone && w.Header is { Localized: true }).ToList();
            var stamps = strings.Stamps(localized.Select(w => (w.Path, w.Name)).ToList());
            for (int i = 0; i < localized.Count; i++) localized[i].Strings = stamps[i];
        }
        // The plugins of this refresh: the folder as it was listed, less the files already gone when they were stamped
        // (S43). This one list, and the masters' sizes taken from it, serve the whole refresh.
        var all = work.Where(w => !w.Gone).OrderByDescending(w => w.Stamp.Length).ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var styleOf = all.Where(w => w.Header != null).ToDictionary(w => w.Name, w => w.Header!.Style, StringComparer.OrdinalIgnoreCase);
        foreach (var w in all) w.Styles = StylesOf(w.Header, styleOf);

        // 2. Content keys, reading keys, segments. Biggest first; each pass is itself spread over the threads.
        var notes = new ConcurrentQueue<(string List, string Name)>();
        Parallel.ForEach(all, new ParallelOptions { MaxDegreeOfParallelism = _options.Threads }, w =>
        {
            for (int attempt = 0; ; attempt++)
            {
                var localNotes = new List<(string, string)>();
                // A file being written can hold anything for a moment: whatever reading it throws becomes the plugin's
                // read error, never the refresh's (S43). The stamp check below then reads it again if it changed.
                try { w.Result = Bring(w, localNotes, verifyContent); }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    w.Result = new IndexedPlugin { Name = w.Name, Path = w.Path, Stamp = w.Stamp, Header = w.Header, MasterStyles = w.Styles, Strings = w.Strings };
                    w.Result.Problems.ReadError = $"it couldn't be read ({e.GetType().Name}: {e.Message.TrimEnd('.')})";
                    if (w.Old == null && !localNotes.Contains(("added", w.Name))) localNotes.Add(("added", w.Name));
                }
                // Rule 5: a plugin that changed while it was read isn't trusted. Read it once more, then give up on it.
                QuickStamp after;
                try { after = w.Header == null && w.Stamp == default ? default : QuickStamp.Read(w.Path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { after = default; w.Gone = IsGone(w.Path); }
                if (after == w.Stamp && !w.Gone) { foreach (var n in localNotes) notes.Enqueue(n); break; }
                if (!ReferenceEquals(w.Result.Segment, w.Old?.Segment)) w.Result.Segment?.Dispose();
                // Deleted while it was read: it isn't part of this refresh (one seen before is listed as removed)
                if (w.Gone) { w.Result.Segment = null; break; }
                if (attempt == 1)
                {
                    w.Result.Segment = null;
                    w.Result.ContentKey = default; // hashed from a file that kept changing: not a key to remember
                    w.Result.Problems = new PluginProblems { ReadError = "it changed while it was being read: run the command again" };
                    if (w.Old == null) notes.Enqueue(("added", w.Name));
                    break;
                }
                // Read it again: its stamp, its header, and its masters' sizes for that header. A header that changed can
                // name other masters, or there was none yet (a file still being written): sizes kept from the first look
                // made the reading key read past their end, the crash of S43.
                try
                {
                    w.Stamp = QuickStamp.Read(w.Path, out var start);
                    w.Header = PluginHeader.Parse(start) ?? PluginHeader.Read(w.Path);
                    w.Strings = strings != null && w.Header is { Localized: true } ? strings.Stamps(new[] { (w.Path, w.Name) })[0] : StringsStamp.NotStamped;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { w.Stamp = default; w.Header = null; w.Strings = StringsStamp.NotStamped; }
                w.ContentKnown = false; w.StampSame = false;
                w.Styles = StylesOf(w.Header, styleOf);
            }
        });
        // Removed: seen before and not part of this refresh (not listed, or gone before it was read)
        all.RemoveAll(w => w.Gone);
        var present = all.Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _byName.Keys.Where(n => !present.Contains(n)).ToList()) report.Removed.Add(gone);
        foreach (var (list, name) in notes.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
            (list switch { "added" => report.Added, "content" => report.ContentChanged, "stamp" => report.StampChangedOnly, "rekeyed" => report.Rekeyed, "strings" => report.StringsChanged, "pass" => report.PassesRun, "reused" => report.SegmentsReused, _ => report.Hashed }).Add(name);
        foreach (var name in report.StringsChanged)
            if (_byName.GetValueOrDefault(name) is { } was && all.FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Result is { } now)
                report.StringsChanges[name] = StringsStamp.Changes(was.Strings, now.Strings);

        // 3. Swap in the new state; let go of segments that were replaced
        var oldSegments = _plugins.Where(p => p.Segment != null).Select(p => p.Segment!).ToList();
        var plugins = all.Select(w => w.Result!).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var kept = new HashSet<Segment>(plugins.Where(p => p.Segment != null).Select(p => p.Segment!));
        var letGo = oldSegments.Where(s => !kept.Contains(s)).ToList();
        bool keepFiles = letGo.Count > 0 && _options.KeepUnusedSegments?.Invoke() == true;
        foreach (var s in letGo)
        {
            s.Dispose();
            if (keepFiles) continue;
            try { File.Delete(s.Path); } catch (IOException) { } catch (UnauthorizedAccessException) { } // another worker may still map it
        }
        bool segmentsChanged = report.Removed.Count > 0 || plugins.Count != _plugins.Count || plugins.Any(p => !_byName.TryGetValue(p.Name, out var o) || !ReferenceEquals(o.Segment, p.Segment));
        _plugins = plugins; _byName.Clear();
        for (int i = 0; i < plugins.Count; i++) { plugins[i].Id = i; _byName[plugins[i].Name] = plugins[i]; }

        // 4. The load order, then who holds what and who wins
        var mapClock = Stopwatch.StartNew();
        AskRefusals();
        report.LoadOrderChanged = ReadLoadOrder();
        BuildOverrideMap(); // a few milliseconds, and plugin Ids may have moved
        report.WinnersReworked = segmentsChanged || report.LoadOrderChanged;
        report.MapSeconds = mapClock.Elapsed.TotalSeconds;
        if (report.Added.Count + report.ContentChanged.Count + report.StampChangedOnly.Count + report.Hashed.Count > 0) WriteManifest(report.Hashed);
        report.Seconds = clock.Elapsed.TotalSeconds;
        return report;
    }

    /// <summary>One plugin: reuse what is still true, hash and read what isn't.</summary>
    IndexedPlugin Bring(Work w, List<(string, string)> notes, bool verifyContent)
    {
        var old = w.Old;
        var p = new IndexedPlugin { Name = w.Name, Path = w.Path, Stamp = w.Stamp, Header = w.Header, MasterStyles = w.Styles, Strings = w.Strings };
        // A localized plugin whose string files changed: its texts read differently, whatever happened to the file
        if (old != null && old.Strings.Stamped && w.Strings.Stamped && !old.Strings.SameAs(w.Strings)) notes.Add(("strings", w.Name));
        if (w.Header == null)
        {
            p.Problems.ReadError = "it doesn't start with a plugin header (TES4), or it can't be opened";
            if (old == null) notes.Add(("added", w.Name));
            return p;
        }
        bool isNew = old == null;
        if (isNew) notes.Add(("added", w.Name));
        try
        {
            PassResult? pass = null;
            if (!w.ContentKnown)
            {
                if (old?.Segment != null)
                {
                    // A plugin seen before whose stamp changed (or a full check): hash first, the segment may still be right
                    var hashClock = Stopwatch.StartNew();
                    w.Content = Keys.Content(w.Path);
                    p.Timings = new PassTimings(0, 0, hashClock.Elapsed.TotalSeconds, 0, 0);
                }
                else
                {
                    // First sight: the pass and the hash run side by side
                    pass = HeaderPass.Run(w.Path, w.Name, w.Header, w.Styles, _options.Threads, out w.Content, hash: true);
                    notes.Add(("pass", w.Name));
                }
                w.ContentKnown = true;
                notes.Add(("hashed", w.Name));
            }
            p.ContentKey = w.Content;
            if (!isNew && !w.StampSame) notes.Add((old!.ContentKey == w.Content ? "stamp" : "content", w.Name));
            else if (!isNew && old!.ContentKey != w.Content) notes.Add(("content", w.Name)); // the full check, here or in another program
            p.ReadingKey = Keys.Reading(w.Content, w.Name, w.Header.Masters, w.Styles, _options.VersionText);
            if (!isNew && old!.ContentKey == w.Content && old.ReadingKey != p.ReadingKey && !old.ReadingKey.IsZero) notes.Add(("rekeyed", w.Name));

            if (old?.Segment != null && old.ReadingKey == p.ReadingKey) p.Segment = old.Segment;
            else
            {
                string path = SegmentPath(p.ReadingKey);
                var seg = Segment.TryOpen(path);
                if (seg != null && seg.ReadingKey != p.ReadingKey) { seg.Dispose(); seg = null; }
                if (seg != null) { if (pass == null) notes.Add(("reused", w.Name)); }
                else
                {
                    if (pass == null) { pass = HeaderPass.Run(w.Path, w.Name, w.Header, w.Styles, _options.Threads, out _, hash: false); notes.Add(("pass", w.Name)); }
                    var writeClock = Stopwatch.StartNew();
                    Segment.Write(path, pass, p.ReadingKey, p.ContentKey);
                    p.Timings = new PassTimings(pass.ScanSeconds, pass.MergeSeconds, pass.HashSeconds, writeClock.Elapsed.TotalSeconds, pass.Ranges);
                    seg = Segment.TryOpen(path) ?? throw new IOException($"the segment {path} couldn't be opened after it was written");
                }
                p.Segment = seg;
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            p.Segment = null;
            p.Problems.ReadError = e.Message;
        }
        return p;
    }

    void GuardScratch(IEnumerable<string> pluginPaths)
    {
        string scratch = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_options.ScratchFolder));
        var dirs = pluginPaths.Select(p => Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(p)!));
        if (_source.DataFolder != null) dirs = dirs.Append(Path.TrimEndingDirectorySeparator(_source.DataFolder));
        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
            if (scratch.Equals(dir, StringComparison.OrdinalIgnoreCase) || scratch.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The scratch folder {scratch} is inside a folder that holds plugins ({dir}). Nothing is ever written there: choose another scratch folder.");
    }

    // The manifest only saves hashing: path, quick stamp and content key of every plugin an index on this scratch
    // folder has hashed. A line is used only when its stamp equals the file's stamp now, so an old line can't mislead.
    Dictionary<string, (QuickStamp Stamp, Key128 Content)> ReadManifest()
    {
        var d = new Dictionary<string, (QuickStamp, Key128)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(ManifestPath)) return d;
            using var fs = Files.OpenShared(ManifestPath);
            using var r = new StreamReader(fs);
            while (r.ReadLine() is string line)
            {
                var c = line.Split('\t');
                if (c.Length != 5 || c[4].Length != 32) continue;
                d[c[0]] = (new QuickStamp(long.Parse(c[1], CultureInfo.InvariantCulture), long.Parse(c[2], CultureInfo.InvariantCulture), Convert.ToUInt64(c[3], 16)), Key128.Parse(c[4]));
            }
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException) { }
        return d;
    }

    /// <summary>
    /// Adds this index's plugins to what the manifest already holds (other indexes and programs share the folder). A line
    /// for the same quick stamp is replaced only by a plugin this refresh hashed: another program may have hashed the
    /// file since this one did, and found a change in its middle.
    /// </summary>
    void WriteManifest(IReadOnlyCollection<string> hashed)
    {
        string tmp = ManifestPath + $".{Environment.ProcessId}.tmp";
        var hashedNow = new HashSet<string>(hashed, StringComparer.OrdinalIgnoreCase);
        try
        {
            var all = ReadManifest();
            foreach (var p in _plugins.Where(p => !p.ContentKey.IsZero))
                if (hashedNow.Contains(p.Name) || !all.TryGetValue(p.Path, out var had) || had.Stamp != p.Stamp) all[p.Path] = (p.Stamp, p.ContentKey);
            File.WriteAllLines(tmp, all.Select(m =>
                string.Join('\t', m.Key, m.Value.Stamp.Length.ToString(CultureInfo.InvariantCulture), m.Value.Stamp.WriteTicksUtc.ToString(CultureInfo.InvariantCulture), m.Value.Stamp.EndsHash.ToString("x16"), m.Value.Content.ToString())));
            File.Move(tmp, ManifestPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { try { File.Delete(tmp); } catch (IOException) { } }
    }

    // ---------------------------------------------------------------- Load order
    /// <summary>Reads the lists again and rebuilds the order. True when the order, an enabled mark or a slot changed.</summary>
    bool ReadLoadOrder()
    {
        string? txt = _options.PluginsTxt ?? LoadOrder.DefaultPluginsTxt;
        string? ccc = _options.Ccc ?? (_source.DataFolder != null ? LoadOrder.DefaultCcc(_source.DataFolder) : null);
        var txtLines = LoadOrder.ReadLines(txt, out ulong s1);
        var cccLines = LoadOrder.ReadLines(ccc, out ulong s2);
        _orderStamp = s1 ^ (s2 * 31);
        // For slots a plugin named .esl counts as Small, as today's LoadOrder command has it
        var inData = _plugins.Where(p => p.Header != null).ToDictionary(p => p.Name,
            p => (p.Name.EndsWith(".esl", StringComparison.OrdinalIgnoreCase) ? MasterStyle.Small : p.Header!.Style, p.Stamp.Length), StringComparer.OrdinalIgnoreCase);
        var bad = new List<string>();
        var order = LoadOrder.Build(inData, txtLines, cccLines, bad);
        BadLoadOrderLines = bad;
        bool changed = order.Count != _order.Count;
        for (int i = 0; !changed && i < order.Count; i++)
            changed = !order[i].Name.Equals(_order[i].Name, StringComparison.OrdinalIgnoreCase) || order[i].Enabled != _order[i].Enabled || order[i].Slot != _order[i].Slot || order[i].Position != _order[i].Position;
        _order = order;
        var byName = order.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var p in _plugins)
        {
            p.Order = byName.GetValueOrDefault(p.Name) ?? new LoadOrderEntry { Name = p.Name, InData = true };
            p.Rank = p.Order.Enabled ? p.Order.Position : (1L << 40) + (p.Order.Position >= 0 ? p.Order.Position : (1L << 30) + p.Id);
        }
        return changed;
    }

    // ---------------------------------------------------------------- Plugins the reader refuses (C204)
    readonly Dictionary<string, (Key128 Content, string? Error)> _refusals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Asks the reader (<see cref="IndexOptions.Refusals"/>) about each plugin whose headers repeat a FormKey,
    /// once for each content: Mutagen refuses such a plugin by default (fixes.17, C204).</summary>
    void AskRefusals()
    {
        foreach (var p in _plugins)
        {
            if (_options.Refusals == null || p.Segment is not { Duplicates: > 0 }) { _refusals.Remove(p.Name); continue; }
            if (_refusals.TryGetValue(p.Name, out var known) && known.Content == p.ContentKey) continue;
            string? error;
            try { error = _options.Refusals.Refuses(p); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // The reader couldn't open it (gone, or written since the header pass: S43): not known, asked again next time
                _refusals.Remove(p.Name);
                continue;
            }
            _refusals[p.Name] = (p.ContentKey, error);
        }
    }

    /// <summary>Checks plugins.txt and Starfield.ccc alone. When they changed, the winners are worked out again from the
    /// segments that are already mapped: no plugin file is opened.</summary>
    public bool RefreshLoadOrder()
    {
        ulong before = _orderStamp;
        bool changed = ReadLoadOrder();
        if (changed || before != _orderStamp) BuildOverrideMap();
        return changed;
    }

    // ---------------------------------------------------------------- Who holds what
    void BuildOverrideMap()
    {
        foreach (var p in _plugins)
        {
            p.OwnerToPlugin = p.Segment == null ? Array.Empty<int>() : p.Segment.Names.Select(n => _byName.TryGetValue(n, out var m) ? m.Id : -1).ToArray();
            if (p.Segment != null) p.OwnerToPlugin[^1] = p.Id;
        }
        var list = new List<(ulong Key, long Rank, RecordRef Ref)>();
        foreach (var p in _plugins)
        {
            var seg = p.Segment;
            if (seg == null) continue;
            var entries = seg.Entries;
            for (int i = 0; i < seg.FirstOwn; i++)
            {
                int owner = p.OwnerToPlugin[entries[i].Owner];
                if (owner < 0 || owner == p.Id) continue;
                list.Add(((ulong)owner << 24 | entries[i].LocalId, p.Rank, new RecordRef(p.Id, i)));
            }
        }
        list.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Ref.Entry.CompareTo(b.Ref.Entry));
        var keys = new List<ulong>(); var start = new List<int>(); var holders = new RecordRef[list.Count];
        var mismatch = _plugins.Select(_ => new List<int>()).ToArray(); var noBase = _plugins.Select(_ => new List<int>()).ToArray();
        for (int i = 0; i < list.Count; i++)
        {
            if (i == 0 || list[i].Key != list[i - 1].Key) { keys.Add(list[i].Key); start.Add(i); }
            holders[i] = list[i].Ref;
        }
        start.Add(list.Count);
        _ovKeys = keys.ToArray(); _ovStart = start.ToArray(); _ovHolders = holders;

        // Overrides of a record of another type, and overrides of nothing
        for (int g = 0; g < _ovKeys.Length; g++)
        {
            var origin = _plugins[(int)(_ovKeys[g] >> 24)];
            if (origin.Segment == null) continue;
            int at = origin.Segment.Find(origin.Segment.SelfOwner, (uint)_ovKeys[g] & 0xFFFFFF);
            string? baseSig = at < 0 ? null : origin.Segment.SignatureOf(origin.Segment[at]);
            for (int h = _ovStart[g]; h < _ovStart[g + 1]; h++)
            {
                var r = _ovHolders[h];
                if (baseSig == null) noBase[r.Plugin].Add(r.Entry);
                else if (_plugins[r.Plugin].Segment!.SignatureOf(_plugins[r.Plugin].Segment![r.Entry]) != baseSig) mismatch[r.Plugin].Add(r.Entry);
            }
        }
        foreach (var p in _plugins)
        {
            var masters = p.Header?.Masters ?? Array.Empty<string>();
            var pr = new PluginProblems
            {
                ReadError = p.Problems.ReadError,
                BothSizeFlags = p.Header?.BothSizeFlags ?? false,
                MissingMasters = masters.Where(m => !_byName.TryGetValue(m, out var mp) || mp.Header == null).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                MastersNotLoaded = !p.Order.Enabled ? Array.Empty<string>() : masters.Where(m => _byName.TryGetValue(m, out var mp) && mp.Header != null && !mp.Order.Enabled).ToArray(),
                MastersLoadedAfter = !p.Order.Enabled ? Array.Empty<string>() : masters.Where(m => _byName.TryGetValue(m, out var mp) && mp.Order.Enabled && mp.Order.Position > p.Order.Position).ToArray(),
                TypeMismatches = mismatch[p.Id], OverridesOfNothing = noBase[p.Id],
                Unresolved = p.Segment?.Unresolved ?? 0, Duplicates = p.Segment?.Duplicates ?? 0,
            };
            if (_refusals.TryGetValue(p.Name, out var refused) && refused.Content == p.ContentKey) pr.Refused = refused.Error;
            p.Problems = pr;
        }
        // A plugin whose master is left out is left out too, as Mutagen's load order drops it ("master X was refused"); so
        // is one whose master can't be read because a master of that one isn't in Data (Mutagen drops both: DropPlugin)
        for (bool more = true; more;)
        {
            more = false;
            foreach (var p in _plugins)
            {
                if (p.LeftOut != null || p.Header == null) continue;
                var refused = p.Header.Masters.FirstOrDefault(m => _byName.TryGetValue(m, out var mp) && (mp.Problems.Refused != null || mp.Problems.MasterRefused != null));
                if (refused != null) { p.Problems.MasterRefused = refused; more = true; continue; }
                var unreadable = p.Header.Masters.FirstOrDefault(m => _byName.TryGetValue(m, out var mp) && mp.Header != null && (mp.Problems.MissingMasters.Count > 0 || mp.Problems.MasterUnreadable != null));
                if (unreadable != null) { p.Problems.MasterUnreadable = unreadable; more = true; }
            }
        }
    }

    int FindGroup(ulong key)
    {
        int i = Array.BinarySearch(_ovKeys, key);
        return i;
    }

    // ---------------------------------------------------------------- Lookups
    /// <summary>Splits "0476C4:Starfield.esm" (or "Starfield.esm:0476C4") into its plugin and local ID.</summary>
    public static bool TryParseFormKey(string text, out string plugin, out uint localId)
    {
        plugin = ""; localId = 0;
        int colon = text.IndexOf(':');
        if (colon <= 0 || colon == text.Length - 1) return false;
        string a = text[..colon].Trim(), b = text[(colon + 1)..].Trim();
        if (Files.IsPluginName(a)) (a, b) = (b, a);
        if (a.Length is 0 or > 6 || !uint.TryParse(a, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out localId)) return false;
        plugin = b;
        return true;
    }

    /// <summary>Every plugin that holds this record, in load order, and the winner. Null when no plugin holds it
    /// (or the owner isn't there).</summary>
    public RecordVersions? Versions(string plugin, uint localId)
    {
        if (!_byName.TryGetValue(plugin, out var origin)) return null;
        var holders = new List<RecordRef>();
        int own = origin.Segment?.Find(origin.Segment.SelfOwner, localId) ?? -1;
        if (own >= 0) holders.Add(new RecordRef(origin.Id, own));
        int g = FindGroup((ulong)origin.Id << 24 | localId);
        if (g >= 0)
        {
            for (int h = _ovStart[g]; h < _ovStart[g + 1]; h++) holders.Add(_ovHolders[h]);
            if (own >= 0) holders.Sort((a, b) => _plugins[a.Plugin].Rank.CompareTo(_plugins[b.Plugin].Rank));
        }
        if (holders.Count == 0) return null;
        // A plugin Mutagen refuses (or one built on it) is no winner: the version before it wins (C204)
        int winner = -1;
        for (int i = 0; i < holders.Count; i++) if (_plugins[holders[i].Plugin] is { Order.Enabled: true, LeftOut: null }) winner = i;
        return new RecordVersions { Holders = holders, Winner = winner };
    }

    public RecordVersions? Versions(string formKey) => TryParseFormKey(formKey, out var plugin, out var id) ? Versions(plugin, id) : null;

    /// <summary>The record the game uses for this FormKey: the last enabled plugin that holds it. Null when none does.</summary>
    public RecordRef? Winner(string plugin, uint localId)
    {
        var v = Versions(plugin, localId);
        return v == null || v.Winner < 0 ? null : v.Holders[v.Winner];
    }

    /// <summary>True when this entry is the version the game uses.</summary>
    public bool IsWinner(RecordRef r)
    {
        var p = _plugins[r.Plugin]; var e = p.Segment![r.Entry];
        if (!p.Order.Enabled || p.LeftOut != null || e.IsUnresolved) return false;
        int owner = p.OwnerToPlugin[e.Owner];
        if (owner < 0) return false;
        // Nobody else holds it: it wins (the first copy, should the plugin hold the FormKey twice)
        if (owner == p.Id && FindGroup((ulong)owner << 24 | e.LocalId) < 0) return p.Segment.Find(e.Owner, e.LocalId) == r.Entry;
        return Winner(_plugins[owner].Name, e.LocalId) == r;
    }

    /// <summary>Every record with this EditorID, letter case aside, in every plugin.</summary>
    public List<RecordRef> FindEditorId(string editorId)
    {
        var found = new List<RecordRef>(); var hits = new List<int>();
        foreach (var p in _plugins)
        {
            if (p.Segment == null) continue;
            hits.Clear();
            p.Segment.FindEditorId(editorId, hits);
            foreach (int h in hits) found.Add(new RecordRef(p.Id, h));
        }
        return found;
    }

    /// <summary>The plugins whose own records include this local ID (a bare "000CF2" with no plugin named).</summary>
    public List<RecordRef> FindLocalId(uint localId)
    {
        var found = new List<RecordRef>();
        foreach (var p in _plugins)
        {
            if (p.Segment == null) continue;
            int at = p.Segment.Find(p.Segment.SelfOwner, localId);
            if (at >= 0) found.Add(new RecordRef(p.Id, at));
        }
        return found;
    }

    /// <summary>A plugin's records of one type, as entry numbers in FormKey order.</summary>
    public IEnumerable<RecordRef> RecordsOfType(string plugin, string signature)
    {
        if (!_byName.TryGetValue(plugin, out var p) || p.Segment == null) yield break;
        foreach (int i in p.Segment.EntriesOf(signature)) yield return new RecordRef(p.Id, i);
    }

    /// <summary>Records and bytes of each type in a plugin: what a read of that type will cost.</summary>
    public IReadOnlyList<SignatureStats> Stats(string plugin) =>
        _byName.TryGetValue(plugin, out var p) && p.Segment != null ? p.Segment.Signatures : Array.Empty<SignatureStats>();

    string FormKeyText(IndexedPlugin p, int owner, uint localId) =>
        owner >= Entry.UnresolvedOwner ? $"{(uint)(owner & 0xFF) << 24 | localId:X8}:(unresolved in {p.Name})"
        : $"{localId:X6}:{(p.OwnerToPlugin[owner] >= 0 ? _plugins[p.OwnerToPlugin[owner]].Name : p.Segment!.Names[owner])}";

    /// <summary>A record's FormKey as Describe writes it, alone: for a caller that reads millions of records and keeps
    /// only that (the SQL layer's columns read from the records' bytes, J30).</summary>
    public string FormKeyOf(RecordRef r)
    {
        var p = _plugins[r.Plugin]; var e = p.Segment![r.Entry];
        return FormKeyText(p, e.Owner, e.LocalId);
    }

    public RecordInfo Describe(RecordRef r)
    {
        var p = _plugins[r.Plugin]; var seg = p.Segment!; var e = seg[r.Entry];
        string? parent = seg.TryGetParent(e, out int po, out uint pl) ? FormKeyText(p, po, pl) : null;
        return new RecordInfo(p.Name, FormKeyText(p, e.Owner, e.LocalId), seg.SignatureOf(e), seg.EditorId(r.Entry), e.Offset, e.Size, e.Flags, parent, e.Owner < seg.SelfOwner, e.IsUnresolved);
    }
}

/// <summary>Seconds spent on one plugin: cutting it into pieces and reading the headers, sorting and merging, hashing the content, writing the segment.</summary>
public sealed record PassTimings(double Scan, double Merge, double Hash, double Write, int Pieces)
{
    public double Pass => Scan + Merge;
}
