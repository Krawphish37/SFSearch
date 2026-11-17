// The SQL layer in one place (milestone M6, ARCHITECTURE.md section 8). A library: nothing here is a command.
//
//   var engine = new QueryEngine(recordEngine, new QueryOptions { ScratchFolder = … });
//   QueryAnswer a = engine.Run(new QueryRequest { Sql = "SELECT …", Plugins = new[] { "Starfield.esm" } });
//   Console.Write(a.ToText());
//
// One caller at a time, like the header index and the records layer it stands on. Several processes share the
// scratch folder.
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Query;

public sealed class QueryOptions
{
    /// <summary>Where the scratch tables go. Never the Data folder; safe to delete at any time.</summary>
    public required string ScratchFolder { get; init; }
    /// <summary>schema.db, attached to every query as <c>schema</c>. Null: the one beside the program.</summary>
    public string? SchemaDbPath { get; init; }
    /// <summary>The language of text columns, by Mutagen's name or a short code.</summary>
    public string Language { get; init; } = "English";
    public int Threads { get; init; } = Math.Min(Environment.ProcessorCount, 8);
    /// <summary>A limit on how many of the reading threads read records straight from their own bytes at once
    /// (records nested in others: placed objects, dialogue). 0, the default: no limit of its own, all of
    /// <see cref="Threads"/> do. It was 2 while those reads shared one file handle: see RecordReader.</summary>
    public int DirectReaders { get; init; }
    /// <summary>Over either limit a query answers with its estimate and runs only when the request allows heavy work.</summary>
    public double HeavySeconds { get; init; } = 10;
    public long HeavyMemoryMB { get; init; } = 2048;
    /// <summary>A read that opens more records than this is announced like a heavy one, whatever its estimate: the
    /// design's "big world types (millions of rows) are read on request, with the cost said first". Reading one
    /// column of Starfield.esm's 3.29 million placed objects is now estimated at about 9 s, under
    /// <see cref="HeavySeconds"/>; it is still said first. Rows that come from the header index alone don't count.</summary>
    public long HeavyRecords { get; init; } = 1_000_000;
    /// <summary>A heavy read starts only when Windows has its estimate plus this much available.</summary>
    public long HeavySpareMB { get; init; } = 4096;
    /// <summary>A read with more than 1 GB of scratch files starts only when the scratch folder's drive has its
    /// estimate plus this much free (fix C14: SurfaceTree of Starfield.esm needs 24 GB while it reads).</summary>
    public long ScratchSpareMB { get; init; } = 10240;
    /// <summary>Plugins of a "plugins" scope that are attached each from its own scratch file. More than this go
    /// into one file for the set. SQLite itself allows 10 attached files; one is the schema.</summary>
    public int MaxAttached { get; init; } = 8;
    /// <summary>The service's own admission check for a heavy read (JobContext.RequireHeavy): given the estimate in
    /// MB and what it is for, null lets it start, a text refuses it. Null: the engine asks Windows itself.</summary>
    public Func<long, string, string?>? HeavyGate { get; init; }
    /// <summary>A type too big to read whole is read for the columns a query names. True (the default): the plain
    /// columns (links, numbers, yes/no, enum names, plain text, points) straight from Mutagen's object, with no JSON
    /// (DirectColumns.cs, GAPS S5). False: every column through the record's JSON, as before. Internal (J81): only the
    /// tests (sfsquery-tests, through InternalsVisibleTo) set it false, to hold the two ways against each other.</summary>
    internal bool DirectColumns { get; init; } = true;
    /// <summary>
    /// The memory one command may hold (MB, the whole process's private bytes), asked when a statement starts: the
    /// service's allowance for the command (workerMaxMB, or a heavy command's own). A statement that takes the process
    /// past it is stopped with a clear error instead of growing until the PC runs out (GAPS S104). Null, or 0 or less:
    /// no limit. It also sizes how much of a statement's temporary tables SQLite keeps in memory before it writes the
    /// rest to temporary files (<see cref="SqliteTuning.TempFolder"/>).
    /// </summary>
    public Func<long>? MemoryLimitMB { get; init; }
    /// <summary>What one statement's temporary files may take (MB), asked when it starts. Internal (J81's rule): only the tests set it. Null, or 0 or less: 4 times
    /// <see cref="MemoryLimitMB"/>, 4 to 32 GB (16 GB with no memory limit; GAPS S110). Past it the statement stops with
    /// a clear error.</summary>
    internal Func<long>? TempFilesLimitMB { get; init; }
    /// <summary>The setting that raises <see cref="MemoryLimitMB"/>, for the error's fix ("workerMaxMB in sfsearch.json").
    /// Null: the fix only says to narrow the query.</summary>
    public string? MemoryLimitSetting { get; init; }
    /// <summary>plugins.txt, for the date in the source line. Null: Starfield's own.</summary>
    public string? PluginsTxt { get; init; }
    /// <summary>The host's world link index, as an offer (J33): for the plugins whose world records a Links WHERE ToKey = '…'
    /// statement would have to read whole (they have no world link file), what making their world link index costs and
    /// what it saves; null when none would be made. A "Not run" answer and the note of types left out offer it as the
    /// third answer (allow_heavy="save_index").</summary>
    public Func<IReadOnlyCollection<IndexedPlugin>, IndexOffer?>? WorldIndexOffer { get; init; }
}

/// <summary>What a link index says of one plugin and one target: the signatures of the records that link to it, and
/// the signatures it doesn't cover (world data, records the walk couldn't read), which may link to it too. With a world
/// link index (J32): <see cref="WorldEntries"/>, the world records of the plugin that link to it (entries of its
/// segment), read alone; its world types are then covered, but for the records it couldn't read.
/// <see cref="WorldTypes"/> (J33): the plugin's world types. Without a world link file (WorldEntries null) they are
/// what the world link index would cover (and are among <see cref="NotIndexed"/>); with one, those not among NotIndexed
/// are covered by it, so a statement that names one of them with a ToKey needn't read it whole.</summary>
public sealed record LinkHolders(IReadOnlyCollection<string> Signatures, IReadOnlyCollection<string> NotIndexed, IReadOnlyList<int>? WorldEntries = null, IReadOnlyCollection<string>? WorldTypes = null);

public sealed partial class QueryEngine : IDisposable
{
    readonly RecordEngine _records;
    readonly HeaderIndex _index;
    readonly RecordSchema _schema;
    readonly QueryOptions _options;
    readonly RecordReader _reader;
    readonly string _schemaDb;
    readonly string _recordLanguage;

    public ViewBuilder Views { get; }
    public QueryOptions Options => _options;
    public RecordEngine Records => _records;
    /// <summary>The signature label used in cost lines: the record type, or the union view of a signature with several.</summary>
    public string LabelOf(string signature)
    {
        var types = _schema.TypesOf(signature);
        if (types.Count == 1) return types[0];
        return Views.UnionNames.FirstOrDefault(u => u.Value == signature).Key ?? signature;
    }

    public QueryEngine(RecordEngine records, QueryOptions options)
    {
        _records = records; _index = records.Index; _schema = records.Schema; _options = options;
        _schemaDb = Path.GetFullPath(options.SchemaDbPath ?? Path.Combine(AppContext.BaseDirectory, "schema.db"));
        if (!File.Exists(_schemaDb)) throw new QueryException($"there is no schema at {_schemaDb}", "build SFSearch.SchemaGen first");
        _recordLanguage = records.Sessions.Language.ToString();
        Views = new ViewBuilder(_schema);
        _reader = new RecordReader(records, options.Threads, options.DirectReaders, c => Functions.RegisterPure(c, Languages.Name(options.Language)));
        GuardScratch();
        SqliteTuning.TempFolder(Path.Combine(options.ScratchFolder, "temp"));
    }

    public void Dispose() => _reader.Dispose();

    /// <summary>
    /// The host's link index, if it has one: for a plugin and a FormKey, which record types of that plugin hold a link
    /// to it. A query over Links that asks for the links into one record (<c>WHERE ToKey = '…'</c>) reads those types
    /// first. Null for the plugin (no index made for it): the schema's types whose link fields could point at the
    /// record are read instead, which can be many more.
    /// </summary>
    public Func<IndexedPlugin, string, LinkHolders?>? LinkIndex { get; set; }

    /// <summary>The scratch folder is never a folder that holds plugins, or inside one.</summary>
    void GuardScratch()
    {
        string scratch = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_options.ScratchFolder));
        foreach (var dir in _index.Plugins.Select(p => Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(p.Path)!)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (scratch.Equals(dir, StringComparison.OrdinalIgnoreCase) || scratch.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new QueryException($"the scratch folder {scratch} is inside a folder that holds plugins ({dir})", "choose a scratch folder outside the game's folders");
    }

    /// <summary>Runs one query. Never throws for a wrong query or a refused read: the answer says what is wrong.</summary>
    public QueryAnswer Run(QueryRequest request)
    {
        var session = new Session(this, request);
        return session.Run();
    }

    /// <summary>One plugin's scratch file: the file a session attaches for it (Session.cs), and the tests look into.</summary>
    public ScratchFile PluginFile(IndexedPlugin p) => new()
    {
        Path = Path.Combine(_options.ScratchFolder, ScratchFile.PluginFileName(p.ReadingKey, _recordLanguage, p.Strings)), IsSet = false,
        Members = new[] { new ScratchMember(p.Name, p.Path, p.Stamp.Length, p.ReadingKey, p.ContentKey, p.Order.Enabled ? p.Order.Position : null) },
    };

    /// <summary>What a view's columns are, as a query sees them in a scope.</summary>
    public List<string>? ColumnsOf(string view, ScopeKind scope)
    {
        if (Special.Columns(view, scope == ScopeKind.Versions) is { } special) return special;
        return Views.Find(view) is { } v ? ViewBuilder.ColumnNames(v, scope == ScopeKind.Versions) : null;
    }

    /// <summary>The five tables that aren't made from the schema.</summary>
    static class Special
    {
        public static readonly string[] Names = { "Records", "Forms", "Links", "Conditions", "Plugins" };

        public static List<string>? Columns(string name, bool versions)
        {
            if (name.Equals("Records", StringComparison.OrdinalIgnoreCase))
                return versions ? new() { "FormKey", "Type", "Sig", "EditorID", "Name", "Plugin", "IsOverride", "LoadOrder", "IsWinner", "Json" }
                    : new() { "FormKey", "Type", "Sig", "EditorID", "Name", "Plugin", "IsOverride", "Json" };
            if (name.Equals("Forms", StringComparison.OrdinalIgnoreCase))
                return new() { "FormKey", "Sig", "Type", "EditorID", "Plugin", "Parent", "IsWinner", "IsOverride", "Pos", "LoadOrder", "Size", "DataSize", "Compressed", "Flags", "FlagNames" };
            if (name.Equals("Links", StringComparison.OrdinalIgnoreCase))
                return new() { "FromKey", "FromType", "Field", "Idx", "ToKey", "Path", "Plugin" };
            if (name.Equals("Conditions", StringComparison.OrdinalIgnoreCase))
                return versions ? ConditionRows.Columns.Concat(new[] { "LoadOrder", "IsWinner" }).ToList() : ConditionRows.Columns.ToList();
            if (name.Equals("Plugins", StringComparison.OrdinalIgnoreCase))
                return PluginColumns.ToList();
            return null;
        }

        public static string Canonical(string name) => Names.First(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    const string FormsDone = "*Forms";
}

/// <summary>Process-wide settings a host can make once, before it opens any SQLite connection.</summary>
public static class SqliteTuning
{
    /// <summary>
    /// Switches off SQLite's memory statistics. With them on, every allocation in every connection takes one
    /// process-wide lock, which the reading threads (each with a connection of its own) then queue for. SQLite takes
    /// this setting only before its first use in the process: false means it was too late, and nothing changed.
    /// </summary>
    public static bool NoMemoryStatistics()
    {
        SQLitePCL.Batteries_V2.Init();
        return SQLitePCL.raw.sqlite3_config(SQLitePCL.raw.SQLITE_CONFIG_MEMSTATUS, 0) == SQLitePCL.raw.SQLITE_OK;
    }

    static string? _tempFolder;

    /// <summary>
    /// Where SQLite writes the temporary files of a sort, a GROUP BY or a temporary table too big for its share of
    /// memory (GAPS S104): a folder of its own under the query engine's scratch folder, on the drive whose free space
    /// the engine already checks before a big read. The files are made "delete on close" and "temporary": Windows keeps
    /// them in its file cache while it has memory to spare, and deletes them when the query ends or the program is
    /// stopped, so nothing is left behind. Process-wide (SQLite's own setting); the query engine sets it when made and
    /// at each query's start, so the folder of the engine that runs is the one in use, and is there.
    /// </summary>
    public static void TempFolder(string folder)
    {
        if (!OperatingSystem.IsWindows()) return;
        folder = Path.GetFullPath(folder);
        // Made again when it is gone (the scratch folder emptied, or a host that deletes its scratch folder): SQLite
        // can't open a temporary file in a folder that isn't there ("unable to open database file")
        try { Directory.CreateDirectory(folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Can't be made: SQLite's own choice (the user's TEMP folder) rather than a folder that isn't there
            if (_tempFolder != null && SqliteWin32.SetTempDirectory(null) == SQLitePCL.raw.SQLITE_OK) _tempFolder = null;
            return;
        }
        if (string.Equals(_tempFolder, folder, StringComparison.OrdinalIgnoreCase)) return;
        SQLitePCL.Batteries_V2.Init();
        if (SqliteWin32.SetTempDirectory(folder) == SQLitePCL.raw.SQLITE_OK) _tempFolder = folder;
    }

    /// <summary>The folder <see cref="TempFolder"/> set, or null (SQLite's own choice: the user's TEMP folder).</summary>
    public static string? TempFolderInUse => _tempFolder;
}

/// <summary>
/// SQLite's own Windows call for its temporary-files folder, made straight to e_sqlite3 (GAPS S109, J87).
/// SQLitePCLRaw has <c>raw.sqlite3_win32_set_directory</c>, but its provider implements it only in the build for a
/// Windows target framework (net6.0-windows7.0, net8.0-windows7.0 in 3.x), where it calls
/// <c>sqlite3_win32_set_directory8</c>. SFSearch targets plain net10.0, so NuGet gives it the provider's cross-platform
/// build, where the method is compiled as <c>return 1;</c> (SQLITE_ERROR) and sets nothing: SQLitePCLRaw 2.1.12, 2.1.14
/// and 3.0.5 alike (read in each package's provider; the newest Microsoft.Data.Sqlite changes nothing). The other routes
/// are worse: PRAGMA temp_store_directory is deprecated and, by SQLite's own words, not threadsafe (the reading threads
/// and the indexer have connections open while a query starts), and the process's TMP variable would move .NET's and
/// every child program's temporary files too; a Windows target framework for every program would move their output
/// folders. So the call SQLitePCLRaw's Windows build makes is made here, the same way: a documented SQLite function
/// (https://sqlite.org/c3ref/win32_set_directory.html), imported from the e_sqlite3 the provider loaded, under SQLite's own
/// lock (SQLITE_MUTEX_STATIC_TEMPDIR). Take it out for <c>raw.sqlite3_win32_set_directory</c> if SFSearch is ever built
/// for a Windows target framework.
/// </summary>
static class SqliteWin32
{
    /// <summary>SQLITE_WIN32_TEMP_DIRECTORY_TYPE.</summary>
    const uint TempDirectoryType = 2;

    /// <summary>Sets SQLite's folder for temporary files, process-wide (null: back to SQLite's own choice, TEMP).
    /// SQLITE_OK (0) when set.</summary>
    public static int SetTempDirectory(string? folder)
    {
        if (folder == null) return SetDirectory8(TempDirectoryType, null);
        // UTF-8 with its terminating zero, as SQLitePCLRaw's utf8z gives it; a byte array is passed pinned, as a pointer
        byte[] utf8 = new byte[System.Text.Encoding.UTF8.GetByteCount(folder) + 1];
        System.Text.Encoding.UTF8.GetBytes(folder, utf8);
        return SetDirectory8(TempDirectoryType, utf8);
    }

    [System.Runtime.InteropServices.DllImport("e_sqlite3", EntryPoint = "sqlite3_win32_set_directory8", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, ExactSpelling = true)]
    static extern int SetDirectory8(uint type, byte[]? path);
}
