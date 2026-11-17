// Plugin sessions: plugins opened as Mutagen binary overlays (each at its first use) and kept open.
//  - Master sizes come from the header index, never from a guess and never from Mutagen's own look at the Data folder.
//  - Before each use the plugin's quick stamp (and each master's) is checked against the header index. A changed
//    plugin is reopened and the caller is told.
//  - A plugin whose master is missing is an error.
//  - A localized plugin's string files are part of what is checked: when one changed (a loose file, or the archive it
//    is read from), the plugin is reopened so that its texts are read from the files as they are now (GAPS S15).
//  - Files are opened for reading with every kind of sharing, so a held plugin never stands in the way of the game,
//    xEdit or a mod manager: Mutagen opens them with the sharing SFSearch asks for (BinaryReadParameters.FileShare,
//    C175). For the same reason a plugin is never kept memory-mapped: Windows refuses to overwrite a file that has a
//    mapped view.
//  - One record is read on its own (no group walked) by Mutagen's MajorRecordReader (C176), from bytes the session
//    reads itself.
//  - A record's own bytes are read through PluginSession.ReadAt, which several threads can call at once. Each
//    reading thread has a window of its own on the file, filled through a file handle no other thread is using.
//    One shared handle doesn't do: Windows lets one thread at a time read through a handle opened for synchronous
//    reads (design\agent-first\notes\perf-reads.md).
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Exceptions;
using Mutagen.Bethesda.Plugins.Meta;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Utility;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Strings;
using Noggog;
using SFSearch.Index;
using IndexStyle = SFSearch.Index.MasterStyle;
using MutagenStyle = Mutagen.Bethesda.Plugins.MasterStyle;

namespace SFSearch.Records;

/// <summary>What a use of the sessions found: which plugins had changed on disk and were opened again.</summary>
public sealed class SessionUse
{
    /// <summary>Lines the caller must pass on (the answer's notes).</summary>
    public List<string> Notes { get; } = new();
    /// <summary>Plugins that were open, had changed on disk, and were closed so the next read opens the new file.</summary>
    public List<string> Reopened { get; } = new();
    /// <summary>The header index's own report, when a changed stamp made it look at the files again.</summary>
    public RefreshReport? Refresh { get; internal set; }
    internal List<(string Path, QuickStamp Stamp, string Name)> Stamps { get; } = new();
    /// <summary>The string files of the localized plugins used, as they were at the start.</summary>
    internal List<(IndexedPlugin Plugin, StringsStamp Strings)> StringStamps { get; } = new();
}

/// <summary>
/// Whether Mutagen refuses a plugin (fixes.17, C204): a plugin in which two stored FormIDs read as one FormKey is
/// refused when it is opened, with a <see cref="DuplicateFormKeyException"/> that names each pair. The header index asks
/// this for a plugin whose headers repeat a FormKey; the answer is Mutagen's own, from an open with its default.
/// </summary>
public sealed class MutagenRefusals : RefusalSource
{
    public override string? Refuses(IndexedPlugin plugin)
    {
        if (plugin.Header == null) return null;
        LoadOrder<IModMasterStyledGetter> lookup;
        try { lookup = PluginSession.MasterLookup(plugin, out _); }
        catch (RecordException) { return null; } // a missing master: the header index says that itself
        var read = new BinaryReadParameters
        {
            MasterFlagsLookup = lookup, ThrowOnUnknownSubrecord = false, Parallel = false, FileShare = PluginSession.Sharing,
            DuplicateFormKeys = DuplicateFormKeyOption.Refuse,
        };
        try
        {
            using var mod = StarfieldMod.CreateFromBinaryOverlay(new ModPath(ModKey.FromFileName(plugin.Name), plugin.Path), StarfieldRelease.Starfield, read);
            return null;
        }
        catch (DuplicateFormKeyException e) { return $"{nameof(DuplicateFormKeyException)}: {e.Message}"; }
    }
}

/// <summary>One plugin held open: its overlay, read handles for raw slices, and what a direct read needs.</summary>
public sealed class PluginSession : IDisposable
{
    public string Name { get; }
    public string Path { get; }
    public ModKey ModKey { get; }
    /// <summary>The plugin as Mutagen's binary overlay, opened at its first use (J29): a read of one record from its own
    /// bytes doesn't need it, and opening it walks the plugin's groups (refs opened 68 plugins' overlays in 4.1 s to read
    /// a few records of each). A plugin Mutagen can't open fails here, with the same words as before.</summary>
    public IStarfieldModDisposableGetter Mod => _mod.Value;
    readonly Lazy<IStarfieldModDisposableGetter> _mod;
    public QuickStamp Stamp { get; }
    public Key128 ReadingKey { get; }
    public Key128 ContentKey { get; }
    /// <summary>The string files its texts are read from, as they were when it was opened.</summary>
    public StringsStamp Strings { get; }
    /// <summary>The session's first handle on the file. Windows lets one thread at a time read through it: a caller
    /// that reads from several threads uses <see cref="ReadAt"/>.</summary>
    public SafeFileHandle File { get; }
    public long FileLength { get; }
    /// <summary>The header index's own FormID resolver for this plugin (its masters and their sizes).</summary>
    public FormIdResolver FormIds { get; }
    public IReadOnlyList<KeyedMasterStyle> MasterStyles { get; }
    /// <summary>The same sizes as Mutagen's master flags lookup, the one the session reads with: for Mutagen calls that
    /// need it with a record read from its own bytes (GetStoredFormIds).</summary>
    public IReadOnlyCache<IModMasterStyledGetter, ModKey> MasterFlags { get; }
    /// <summary>How long opening the session took (the overlay itself opens at its first use).</summary>
    public double OpenSeconds { get; }
    public bool Localized { get; }
    /// <summary>The language Mutagen reads the plugin's translated texts in (StringsReadParameters.TargetLanguage): what
    /// its TranslatedString gives as <c>String</c>.</summary>
    public Language TargetLanguage { get; }
    /// <summary>The strings lookup every read of this plugin uses (the overlay's, the record reader's, <see cref="StringsFor"/>):
    /// for a translated text read from the record's bytes, the same tables and texts as Mutagen's (RawColumns.cs, GAPS
    /// S152). Null when the plugin isn't localized.</summary>
    public IStringsFolderLookup? StringsLookup => _strings;

    /// <summary>Mutagen's reader of one record at a time (C176): the same masters, strings and encodings as the
    /// overlay. Made at the first direct read; it holds a handle on the file, shared as the overlay's is.</summary>
    readonly Lazy<MajorRecordReader> _records;
    /// <summary>The strings lookup <see cref="StringsFor"/> asks, made as every reading makes it. Null when the plugin
    /// isn't localized.</summary>
    readonly IStringsFolderLookup? _strings;
    readonly Dictionary<string, IGroupGetter?> _groups = new(StringComparer.Ordinal);
    // More handles on the same file, one for each thread that reads at the same moment: see ReadAt
    readonly ConcurrentBag<SafeFileHandle> _idleHandles = new();
    readonly List<SafeFileHandle> _moreHandles = new();
    /// <summary>What a thread's window names this session by. Not the session itself: a window outlives it.</summary>
    readonly object _windowKey = new();
    bool _disposed;

    internal PluginSession(IndexedPlugin plugin, string? dataFolder, Language language)
    {
        var clock = Stopwatch.StartNew();
        Name = plugin.Name; Path = plugin.Path; Stamp = plugin.Stamp; ReadingKey = plugin.ReadingKey; ContentKey = plugin.ContentKey; Strings = plugin.Strings;
        ModKey = ModKey.FromFileName(plugin.Name);
        FormIds = plugin.FormIds;
        var header = plugin.Header!;
        Localized = (header.Flags & 0x80) != 0;
        TargetLanguage = language;

        var lookup = MasterLookup(plugin, out var styles);
        MasterStyles = styles;
        MasterFlags = lookup;
        Refused = plugin.Problems.Refused;

        var read = new BinaryReadParameters
        {
            MasterFlagsLookup = lookup,
            ThrowOnUnknownSubrecord = false,
            Parallel = false,
            FileShare = Sharing,
            StringsParam = StringFiles.ReadParameters(Path, Name, dataFolder, language),
            // A plugin Mutagen refuses (C204, one FormKey held twice) is opened all the same, with Mutagen's read for
            // diagnostic tools: of each pair, the later record is in the groups. The tools that answer for the load
            // order leave such a plugin out (IndexedPlugin.LeftOut); record and check2 read it, and say so.
            DuplicateFormKeys = Refused != null ? DuplicateFormKeyOption.KeepLater : DuplicateFormKeyOption.Refuse,
        };
        var modPath = new ModPath(ModKey, Path);
        _mod = new Lazy<IStarfieldModDisposableGetter>(() =>
        {
            try { return StarfieldMod.CreateFromBinaryOverlay(modPath, StarfieldRelease.Starfield, read); }
            catch (Exception e) when (e is not RecordException) { throw new RecordException($"Mutagen couldn't open {Name}: {e.GetType().Name}: {e.Message}"); }
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        File = System.IO.File.OpenHandle(Path, FileMode.Open, FileAccess.Read, Sharing);
        FileLength = RandomAccess.GetLength(File);

        // For reading one record straight from its own bytes: the same masters, strings and encodings as the overlay.
        // A localized plugin's texts come from the session's own strings lookup (C197), the one StringsFor asks: one
        // lookup of the strings files per session, not a second one made by the reader
        _records = new Lazy<MajorRecordReader>(() => MajorRecordReader.FromPath(modPath, GameRelease.Starfield, read with { KeepCompressedRecords = true }, _strings),
            LazyThreadSafetyMode.ExecutionAndPublication);
        if (Localized)
            _strings = StringsFolderLookupOverlay.TypicalFactory(GameRelease.Starfield, ModKey, System.IO.Path.GetDirectoryName(Path)!, read.StringsParam);
        OpenSeconds = clock.Elapsed.TotalSeconds;
    }

    /// <summary>Mutagen's error when it refuses the plugin by default (C204): the session reads it with
    /// <c>DuplicateFormKeys = KeepLater</c>. Null for a plugin Mutagen reads.</summary>
    public string? Refused { get; }

    /// <summary>Each master's size as the header index has it (read from that master's own header), and the plugin's
    /// own: the lookup Mutagen reads the plugin's FormIDs with.</summary>
    internal static LoadOrder<IModMasterStyledGetter> MasterLookup(IndexedPlugin plugin, out List<KeyedMasterStyle> styles)
    {
        var header = plugin.Header!;
        var self = ModKey.FromFileName(plugin.Name);
        styles = new List<KeyedMasterStyle>();
        var seen = new HashSet<ModKey>();
        for (int i = 0; i < header.Masters.Count; i++)
        {
            var key = ModKey.FromFileName(header.Masters[i]);
            if (seen.Add(key)) styles.Add(new KeyedMasterStyle(key, Convert(plugin.MasterStyles[i])));
        }
        if (seen.Add(self)) styles.Add(new KeyedMasterStyle(self, Convert(header.Style)));
        return new LoadOrder<IModMasterStyledGetter>(styles);
    }

    /// <summary>How SFSearch opens a plugin, and asks Mutagen to: others may read it, write it, and delete, rename or
    /// replace it while it is held. A change is caught by the stamps before each use (the plugin is opened again).</summary>
    internal const FileShare Sharing = FileShare.ReadWrite | FileShare.Delete;

    internal static MutagenStyle Convert(IndexStyle s) => s switch
    {
        IndexStyle.Small => MutagenStyle.Small,
        IndexStyle.Medium => MutagenStyle.Medium,
        IndexStyle.Full => MutagenStyle.Full,
        _ => throw new RecordException("A master's size isn't known: its file is missing."),
    };

    public void Dispose()
    {
        if (_mod.IsValueCreated) try { _mod.Value.Dispose(); } catch (IOException) { }
        if (_records.IsValueCreated) _records.Value.Dispose();
        File.Dispose();
        lock (_moreHandles)
        {
            _disposed = true;
            foreach (var h in _moreHandles) h.Dispose();
            _moreHandles.Clear();
        }
    }

    // ------------------------------------------------------------------ The file's bytes, from several threads
    /// <summary>How many file handles the session has opened beyond <see cref="File"/>: at most one for each thread
    /// that was reading the file at the same moment.</summary>
    public int ExtraHandles { get { lock (_moreHandles) return _moreHandles.Count; } }

    /// <summary>
    /// Copies the file's bytes at a position. Several threads can call it at once, and none waits for another.
    /// Each thread reads through a window of its own (4 to 64 KB, growing while its reads go forward through the
    /// file), so records that follow each other cost one read of the file for hundreds of them. Anything as large
    /// as a quarter of the window is read straight into the caller's memory.
    /// </summary>
    public void ReadAt(long offset, Span<byte> into)
    {
        if (into.Length == 0) return;
        // Past the end as the session knows it: the read itself says so, as it always did
        if (into.Length >= ReadWindow.MaxBytes / 4 || offset < 0 || offset + into.Length > FileLength) { ReadThrough(offset, into); return; }
        var w = ReadWindow.OfThisThread;
        if (!w.Holds(_windowKey, offset, into.Length))
        {
            int length = (int)Math.Min(Math.Max(w.NextLength(_windowKey, offset), into.Length), FileLength - offset);
            ReadThrough(offset, w.Empty(length));
            w.Filled(_windowKey, offset, length);
        }
        w.CopyTo(offset, into);
    }

    /// <summary>One read of the file through a handle that no other thread is using.</summary>
    void ReadThrough(long offset, Span<byte> into)
    {
        var handle = RentHandle();
        try { RawRecord.ReadExactly(handle, into, offset); }
        finally { if (!ReferenceEquals(handle, File)) _idleHandles.Add(handle); }
    }

    SafeFileHandle RentHandle()
    {
        if (_idleHandles.TryTake(out var idle)) return idle;
        lock (_moreHandles)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // The very file the session's first handle is on, whatever the path names by now. Where Windows won't
            // open it again (the file was deleted while it was held open), the first handle itself is read
            // through: slower from several threads, and the same bytes.
            var handle = SameFile.Reopen(File);
            if (handle == null) return File;
            _moreHandles.Add(handle);
            return handle;
        }
    }

    /// <summary>
    /// The texts a string ID names in this plugin's strings files of one language, one per file that holds the ID
    /// (.strings, .dlstrings, .ilstrings), looked up through the very lookup Mutagen's reads of the plugin use. The
    /// file is named as the stamp has it ("starfield_en.strings in Starfield - Localization.ba2"). Empty for a plugin
    /// that isn't localized. For evidence of a translated text (fix F6r). With a table (the schema's strings table of
    /// the field, GAPS S35), only that file is asked.
    /// </summary>
    public IReadOnlyList<(string File, string Text)> StringsFor(uint id, Language language, StringsSource? table = null)
    {
        var found = new List<(string, string)>();
        if (!Localized || _strings is not { } lookup) return found;
        var format = GameConstants.Get(GameRelease.Starfield).StringsLanguageFormat ?? StringsLanguageFormat.FullName;
        foreach (var source in table is { } one ? new[] { one } : new[] { StringsSource.Normal, StringsSource.DL, StringsSource.IL })
        {
            if (!lookup.TryLookup(source, language, id, out var text)) continue;
            string name = StringsUtility.GetFileName(format, ModKey, language, source).ToLowerInvariant();
            var file = Strings.Files.FirstOrDefault(f => f.Name == name);
            found.Add((file?.Where ?? name, text));
        }
        return found;
    }

    /// <summary>The record's own top-level group in the overlay, by the Mutagen type of its records. Null when the
    /// type has none (cells, and records nested in others).</summary>
    public IGroupGetter? TopLevelGroup(string typeName)
    {
        lock (_groups)
        {
            if (_groups.TryGetValue(typeName, out var g)) return g;
            var getter = MutagenTypes.Getter(typeName);
            try { g = getter == null ? null : Mod.TryGetTopLevelGroup(getter); }
            catch (ArgumentException) { g = null; }
            return _groups[typeName] = g;
        }
    }

    /// <summary>
    /// Reads one record straight from its own bytes with Mutagen's binary reader: no group is walked and nothing else
    /// of the plugin is read. For a record that holds others (a cell, a worldspace, a quest, a topic), Mutagen lays its
    /// children group out with each child read from its record header alone (<c>ChildRecordRead.Headers</c>, C58 W9):
    /// which list, which order, the group's own values, without reading the children. <paramref name="stubs"/>: how
    /// many children it laid out so.
    /// </summary>
    public object ReadDirect(string typeName, long offset, bool withChildStubs, out int stubs)
    {
        Span<byte> head = stackalloc byte[4];
        ReadAt(offset, head);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) == Grup)
            throw new RecordException($"The file has a group, not a record, at 0x{offset:X} of {Name}. The plugin may have changed: run the command again.");
        string what = $"the {typeName} at 0x{offset:X} of {Name}";
        var record = Read(typeName, new RecordType(BinaryPrimitives.ReadInt32LittleEndian(head)), what,
            () => _records.Value.ReadAt(offset, withChildStubs ? ChildRecordRead.Headers : ChildRecordRead.None));
        stubs = withChildStubs && record is IMajorRecordGetterEnumerable holder ? holder.EnumerateMajorRecords().Count() : 0;
        return record;
    }

    /// <summary>
    /// Reads a record from bytes given here (its 24-byte header and its subrecords, not compressed) with Mutagen's
    /// binary reader, as if the plugin held them: the plugin's masters, strings and encodings apply. The part read of
    /// a heavy record (<see cref="RecordEngine.ReadPart"/>) gives it the record with only some of its subrecords.
    /// </summary>
    public object ReadBytes(string typeName, byte[] record)
        => Read(typeName, new RecordType(BinaryPrimitives.ReadInt32LittleEndian(record)), $"the part of the {typeName} given to it", () => _records.Value.Read(record, 0));

    /// <summary>
    /// Where each value of a record sits in its bytes, as Mutagen's reader reads it (C60, value positions): the record's
    /// header and subrecords as <see cref="RawRecord"/> holds them (a compressed record inflated), read by this plugin's
    /// record reader (its masters, strings and encodings) noting each value's place. The offsets are those of
    /// <see cref="RawRecord.Data"/>'s subrecords. Only the record's own bytes: not its child group.
    /// </summary>
    public RecordValuePositions Positions(RawRecord raw)
    {
        var bytes = new byte[RawRecord.HeaderLength + raw.Data.Length];
        raw.Header.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)raw.Data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), raw.Flags & ~RawRecord.CompressedFlag);
        raw.Data.CopyTo(bytes, RawRecord.HeaderLength);
        try { return _records.Value.ReadValuePositions(bytes, raw.Offset); }
        catch (Exception e) when (e is not RecordException) { throw new RecordException($"Mutagen couldn't read the {raw.Signature} at 0x{raw.Offset:X} of {Name} noting its value positions: {e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>
    /// Where each value of the plugin's header (TES4) sits in its bytes, as Mutagen's header reader reads it (C82,
    /// <c>ReadModHeaderValuePositions</c>, GAPS S172): read from the file by this plugin's record reader. The offsets are
    /// those of the header's subrecords as <see cref="RawRecord"/> holds them (a header is never stored compressed).
    /// </summary>
    public ModHeaderValuePositions HeaderPositions()
    {
        try { return _records.Value.ReadModHeaderValuePositions(); }
        catch (Exception e) when (e is not RecordException) { throw new RecordException($"Mutagen couldn't read the plugin header of {Name} noting its value positions: {e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>A record read by Mutagen's <see cref="MajorRecordReader"/>, which picks the class from the record's
    /// signature as the full import does (an OMOD's or a GMST's from its content). The record read must be a
    /// <paramref name="typeName"/>.</summary>
    object Read(string typeName, RecordType signature, string what, Func<IMajorRecord> read)
    {
        var type = MutagenTypes.Class(typeName) ?? throw new RecordException($"Mutagen has no type named {typeName}.");
        if (MajorRecordReader.GetRecordClass(GameCategory.Starfield, signature) == null)
            throw new RecordException($"Mutagen reads no {signature} record on its own: {what} can't be read on its own.");
        IMajorRecord record;
        try { record = read(); }
        catch (Exception e) when (e is not RecordException)
        {
            throw new RecordException($"Mutagen couldn't read {what}: {e.GetType().Name}: {e.Message}");
        }
        if (!type.IsInstanceOfType(record))
            throw new RecordException($"Mutagen read {what} as a {record.GetType().Name}, not a {typeName}.");
        return record;
    }

    const uint Grup = 0x50555247;

    /// <summary>A header inside a group, through the thread's window: a walk over a group's record headers isn't
    /// one read of the file each.</summary>
    void GroupHeader(long pos, Span<byte> into)
    {
        if (pos + into.Length > FileLength) throw new RecordException($"The file ends at 0x{FileLength:X}, inside a group. The plugin may have changed: run the command again.");
        ReadAt(pos, into);
    }
}

/// <summary>
/// A reading thread's window on a plugin's file: the bytes of one stretch of it. Each thread has one, for whichever
/// session it read last, so the memory is 64 KB a reading thread however many plugins are open. A window starts at
/// 4 KB and doubles while the reads go forward through the file (records read in file order). It starts again at
/// 4 KB when a read lands elsewhere (one record by FormKey).
/// </summary>
sealed class ReadWindow
{
    public const int MaxBytes = 1 << 16, MinBytes = 1 << 12;
    [ThreadStatic] static ReadWindow? _mine;
    public static ReadWindow OfThisThread => _mine ??= new ReadWindow();

    readonly byte[] _buffer = new byte[MaxBytes];
    object? _owner;
    long _at; int _length, _next = MinBytes;

    public bool Holds(object owner, long offset, int length) =>
        ReferenceEquals(owner, _owner) && offset >= _at && offset + length <= _at + _length;

    /// <summary>How much to read for a miss at this position.</summary>
    public int NextLength(object owner, long offset)
    {
        bool forward = ReferenceEquals(owner, _owner) && offset >= _at && offset <= _at + _length + MaxBytes;
        return _next = forward ? Math.Min(MaxBytes, _next * 2) : MinBytes;
    }

    /// <summary>The memory to read the file into. The window holds nothing until <see cref="Filled"/>: a read that
    /// fails leaves no half-filled window behind.</summary>
    public Span<byte> Empty(int length)
    {
        _owner = null;
        return new Span<byte>(_buffer, 0, length);
    }

    public void Filled(object owner, long offset, int length) { _owner = owner; _at = offset; _length = length; }

    public void CopyTo(long offset, Span<byte> into) => _buffer.AsSpan((int)(offset - _at), into.Length).CopyTo(into);
}

/// <summary>Another handle on the file an open handle is on.</summary>
static class SameFile
{
    const uint GenericRead = 0x80000000, ShareAll = 0x7;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeFileHandle ReOpenFile(SafeFileHandle original, uint access, uint share, uint flags);

    /// <summary>
    /// A new handle for reading, with every kind of sharing, on the file <paramref name="open"/> is on. Windows
    /// opens that very file again, so the new handle reads the same bytes even when the path has been given to
    /// another file since. Null where that can't be done: the file was deleted, or this isn't Windows (where one
    /// handle doesn't make reading threads wait for each other in the first place).
    /// </summary>
    public static SafeFileHandle? Reopen(SafeFileHandle open)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var again = ReOpenFile(open, GenericRead, ShareAll, 0);
        if (!again.IsInvalid) return again;
        again.Dispose();
        return null;
    }
}

/// <summary>Mutagen's Starfield types by name: the getter interface of a record type, and its class. A class the
/// serializers' registry has (C59 W11) is looked up there; an abstract one (a type several record classes share, which
/// has no serializer of its own) by its Loqui registration.</summary>
static class MutagenTypes
{
    static readonly ConcurrentDictionary<string, Loqui.ILoquiRegistration?> Registrations = new(StringComparer.Ordinal);

    static Loqui.ILoquiRegistration? Registration(string typeName) => Registrations.GetOrAdd(typeName, n =>
        RecordSerializer.Registry.TryGetClassType(n, out var t) && Loqui.LoquiRegistration.TryGetRegister(t, out var r) ? r
        : Loqui.LoquiRegistration.TryGetRegisterByFullName($"Mutagen.Bethesda.Starfield.{n}", out var a) ? a : null);

    public static Type? Getter(string typeName) => Registration(typeName)?.GetterType;
    public static Type? Class(string typeName) => Registration(typeName)?.ClassType;
}

/// <summary>The plugins held open. One caller at a time, like the header index it stands on.</summary>
public sealed class PluginSessions : IDisposable
{
    readonly HeaderIndex _index;
    readonly string? _dataFolder;
    readonly Dictionary<string, PluginSession> _open = new(StringComparer.OrdinalIgnoreCase);

    public Language Language { get; }


    public HeaderIndex Index => _index;
    public IReadOnlyCollection<PluginSession> Open => _open.Values;
    /// <summary>How many times a plugin was opened, reopens included.</summary>
    public int Opens { get; private set; }

    /// <param name="dataFolder">The Data folder, for the translated text of localized plugins that sit elsewhere (test copies).</param>
    public PluginSessions(HeaderIndex index, string? dataFolder = null, Language language = Language.English)
    {
        _index = index; _dataFolder = dataFolder; Language = language;
    }

    public void Dispose()
    {
        foreach (var s in _open.Values) s.Dispose();
        _open.Clear();
    }

    /// <summary>
    /// Call before each use (each command). The quick stamp of every plugin named, and of each of its masters, is
    /// taken again and compared with the header index's. When one differs the index looks at the files again, and
    /// every open plugin whose file or whose masters' sizes changed is closed, so that the next read opens the file
    /// as it is now. The result says which.
    /// </summary>
    public SessionUse Use(params string[] plugins)
    {
        var use = new SessionUse();
        bool refresh = false;
        var check = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in plugins)
        {
            var p = _index.Plugin(name);
            if (p == null) { refresh = true; continue; }
            check.Add(p.Name);
            foreach (var m in p.Header?.Masters ?? Array.Empty<string>()) check.Add(m);
        }
        foreach (var name in check)
        {
            var p = _index.Plugin(name);
            if (p == null) { refresh = true; continue; }
            QuickStamp now;
            try { now = QuickStamp.Read(p.Path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { refresh = true; continue; }
            if (now != p.Stamp) refresh = true;
            else use.Stamps.Add((p.Path, now, p.Name));
        }
        // The string files of the localized ones: one look at the folders for all of them
        var localized = check.Select(n => _index.Plugin(n)).Where(p => p?.Header is { Localized: true }).Select(p => p!).ToList();
        if (!refresh && localized.Count > 0 && _index.StampsStrings)
        {
            var now = _index.StringsNow(localized);
            for (int i = 0; i < localized.Count; i++)
                if (!now[i].SameAs(localized[i].Strings)) refresh = true;
                else use.StringStamps.Add((localized[i], now[i]));
        }
        if (refresh)
        {
            use.Refresh = _index.Refresh();
            use.Stamps.Clear(); use.StringStamps.Clear();
            foreach (var name in check)
                if (_index.Plugin(name) is { } p)
                {
                    use.Stamps.Add((p.Path, p.Stamp, p.Name));
                    if (p.Strings.Stamped) use.StringStamps.Add((p, p.Strings));
                }
        }
        // Whatever made the index look again, an open plugin is only as good as the files it was opened from
        foreach (var s in _open.Values.ToList())
        {
            var p = _index.Plugin(s.Name);
            if (p != null && p.Stamp == s.Stamp && p.ReadingKey == s.ReadingKey && p.Path == s.Path && p.Strings.SameAs(s.Strings)) continue;
            string why = p == null ? "is no longer there"
                : p.ContentKey != s.ContentKey ? "changed on disk since it was opened: it is read again from the file as it is now"
                : p.ReadingKey != s.ReadingKey ? "has a master that changed size, so its FormIDs read differently: it is read again"
                : !p.Strings.SameAs(s.Strings) ? $"has string files that changed on disk since it was opened ({StringsStamp.Changes(s.Strings, p.Strings)}): its texts are read again from the files as they are now"
                : "has a new date or size on disk but the same content: it was opened again";
            use.Notes.Add($"{s.Name} {why}.");
            use.Reopened.Add(s.Name);
            s.Dispose();
            _open.Remove(s.Name);
        }
        foreach (var name in plugins) Get(name);
        return use;
    }

    /// <summary>Call at the end of a command: a plugin that changed while the command read it makes the answer
    /// untrustworthy, so the command fails.</summary>
    public void Confirm(SessionUse use)
    {
        foreach (var (path, stamp, name) in use.Stamps)
        {
            QuickStamp now;
            try { now = QuickStamp.Read(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { now = default; }
            if (now != stamp) throw new RecordException($"{name} changed during the read: run the command again.");
        }
        if (use.StringStamps.Count == 0) return;
        var strings = _index.StringsNow(use.StringStamps.Select(s => s.Plugin).ToList());
        for (int i = 0; i < strings.Length; i++)
            if (!strings[i].SameAs(use.StringStamps[i].Strings))
                throw new RecordException($"The string files of {use.StringStamps[i].Plugin.Name} changed during the read ({StringsStamp.Changes(use.StringStamps[i].Strings, strings[i])}): run the command again.");
    }

    /// <summary>The open session of a plugin, opened now when it isn't yet. A plugin whose master is missing is refused.</summary>
    public PluginSession Get(string plugin)
    {
        if (_open.TryGetValue(plugin, out var s)) return s;
        var p = _index.Plugin(plugin) ?? throw new RecordException($"There is no plugin named {plugin} among the indexed plugins.");
        if (p.Header == null || p.Segment == null)
            throw new RecordException($"{p.Name} can't be read: {p.Problems.ReadError ?? "it has no header index"}.");
        if (p.Problems.MissingMasters.Count > 0)
            throw new RecordException($"{p.Name} can't be read: its master {string.Join(", ", p.Problems.MissingMasters)} isn't there. Without it the FormIDs in {p.Name} can't be told apart, and nothing is guessed. Put the master back, then run the command again.");
        try { s = new PluginSession(p, _dataFolder, Language); }
        catch (Exception e) when (e is not RecordException)
        {
            throw new RecordException($"Mutagen couldn't open {p.Name}: {e.GetType().Name}: {e.Message}");
        }
        Opens++;
        return _open[p.Name] = s;
    }
}
