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
    /// (DirectColumns.cs, GAPS S5). False: every column through the record's JSON, as before; the tests compare the two.</summary>
    public bool DirectColumns { get; init; } = true;
    /// <summary>plugins.txt, for the date in the source line. Null: Starfield's own.</summary>
    public string? PluginsTxt { get; init; }
}

/// <summary>What a link index says of one plugin and one target: the signatures of the records that link to it, and
/// the signatures it doesn't cover (world data, records the walk couldn't read), which may link to it too.</summary>
public sealed record LinkHolders(IReadOnlyCollection<string> Signatures, IReadOnlyCollection<string> NotIndexed);

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

    /// <summary>One plugin's scratch file, for callers that want to look at it (tests).</summary>
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

    /// <summary>The three tables that aren't made from the schema.</summary>
    static class Special
    {
        public static readonly string[] Names = { "Records", "Forms", "Links" };

        public static List<string>? Columns(string name, bool versions)
        {
            if (name.Equals("Records", StringComparison.OrdinalIgnoreCase))
                return versions ? new() { "FormKey", "Type", "Sig", "EditorID", "Name", "Plugin", "IsOverride", "LoadOrder", "IsWinner", "Json" }
                    : new() { "FormKey", "Type", "Sig", "EditorID", "Name", "Plugin", "IsOverride", "Json" };
            if (name.Equals("Forms", StringComparison.OrdinalIgnoreCase))
                return new() { "FormKey", "Sig", "Type", "EditorID", "Plugin", "Parent", "IsWinner", "IsOverride", "Pos", "LoadOrder" };
            if (name.Equals("Links", StringComparison.OrdinalIgnoreCase))
                return new() { "FromKey", "FromType", "Field", "Idx", "ToKey", "Path", "Plugin" };
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
}
