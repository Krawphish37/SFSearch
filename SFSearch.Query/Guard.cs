// What the user's SQL may do, enforced by SQLite's own authorizer while each statement is compiled:
// read anything; make, fill and drop tables, indexes and views of its own (in the query's private memory);
// nothing else. The scratch files are attached read only as well, so a write to one fails twice over.
// The same callback notes which tables and columns a statement reads: that is how the engine learns which record
// types to read, and which columns of a heavy type a query names.
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace SFSearch.Query;

sealed class Guard
{
    public enum Mode { Off, User }

    const int CreateIndex = 1, CreateTable = 2, CreateTempIndex = 3, CreateTempTable = 4, CreateTempView = 6, CreateView = 8, Delete = 9,
        DropIndex = 10, DropTable = 11, DropTempIndex = 12, DropTempTable = 13, DropTempView = 15, DropView = 17, Insert = 18, Pragma = 19,
        Read = 20, Select = 21, Update = 23, Attach = 24, Detach = 25, AlterTable = 26, Reindex = 27, Function = 31, Recursive = 33;
    const int Ok = 0, Deny = 1;

    static readonly HashSet<string> ReadPragmas = new(StringComparer.OrdinalIgnoreCase)
        { "table_info", "table_xinfo", "table_list", "index_list", "index_info", "index_xinfo" };

    readonly delegate_authorizer _callback;   // kept alive for as long as the connection uses it
    public Mode Current = Mode.Off;
    /// <summary>What was refused, in words, for the error line.</summary>
    public string? Denied;
    /// <summary>Tables and columns the statement being compiled reads: table name to column names.</summary>
    public Dictionary<string, HashSet<string>>? Reads;
    /// <summary>
    /// The names that stand for the plugins' records while a statement is compiled: the record types' views, their
    /// list views, Records, Forms and Links. A statement may read them. One that would change one (DELETE, UPDATE,
    /// INSERT, DROP, ALTER, an index on it) is refused, and <see cref="DeniedChange"/> says what it tried.
    /// </summary>
    public HashSet<string>? Protected;
    /// <summary>The change that was refused, as "DELETE on Weapon".</summary>
    public string? DeniedChange;

    public Guard(SqliteConnection connection)
    {
        _callback = Authorize;
        int rc = raw.sqlite3_set_authorizer(connection.Handle!, _callback, null!);
        if (rc != raw.SQLITE_OK) throw new InvalidOperationException("SQLite refused the authorizer: the user's SQL can't be guarded");
    }

    static bool Private(string? db) => db is "temp" or "main";

    int Authorize(object userData, int action, utf8z p0, utf8z p1, utf8z dbName, utf8z inner)
    {
        if (Current == Mode.Off) return Ok;
        switch (action)
        {
            case Read:
                if (Reads != null)
                {
                    string table = p0.utf8_to_string() ?? "";
                    if (!Reads.TryGetValue(table, out var cols)) Reads[table] = cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    cols.Add(p1.utf8_to_string() ?? "");
                }
                return Ok;
            case Select or Recursive:
                return Ok;
            case Function:
                return string.Equals(p1.utf8_to_string(), "load_extension", StringComparison.OrdinalIgnoreCase) ? Refuse("loading an extension") : Ok;
            case CreateTempIndex:
                // p0 is the index, p1 the table it is on
                return IsProtected(p1.utf8_to_string()) ? RefuseChange("CREATE INDEX", p1.utf8_to_string()!) : Ok;
            case DropTempTable or DropTempView:
                return IsProtected(p0.utf8_to_string()) ? RefuseChange("DROP", p0.utf8_to_string()!) : Ok;
            case CreateTempTable or CreateTempView or DropTempIndex:
                return Ok;
            case Insert or Update or Delete:
            {
                string? db = dbName.utf8_to_string();
                string? table = p0.utf8_to_string();
                if (Private(db) && IsProtected(table)) return RefuseChange(action == Insert ? "INSERT" : action == Update ? "UPDATE" : "DELETE", table!);
                return Private(db) ? Ok : Refuse($"a write to {db}.{table}: the scratch tables and the schema are read only");
            }
            case CreateTable or CreateIndex or CreateView or DropTable or DropIndex or DropView:
            {
                string? db = dbName.utf8_to_string();
                return Private(db) ? Ok : Refuse($"a change to the tables of {db}: the scratch tables and the schema are read only");
            }
            case AlterTable:
                // p0 is the database, p1 the table
                if (Private(p0.utf8_to_string()) && IsProtected(p1.utf8_to_string())) return RefuseChange("ALTER", p1.utf8_to_string()!);
                return Private(p0.utf8_to_string()) ? Ok : Refuse($"a change to a table of {p0.utf8_to_string()}");
            case Pragma:
                return ReadPragmas.Contains(p0.utf8_to_string() ?? "") ? Ok : Refuse($"PRAGMA {p0.utf8_to_string()}");
            // Making an index runs a reindex of it
            case Reindex: return Private(dbName.utf8_to_string()) ? Ok : Refuse("a reindex of a scratch table");
            case Attach: return Refuse("attaching a file");
            case Detach: return Refuse("detaching a file");
            default: return Refuse($"this kind of statement (SQLite action {action})");
        }
    }

    int Refuse(string what)
    {
        Denied ??= what;
        return Deny;
    }

    bool IsProtected(string? table) => table != null && Protected != null && Protected.Contains(table);

    int RefuseChange(string verb, string table)
    {
        DeniedChange ??= $"{verb} on {table}";
        return Deny;
    }

    /// <summary>
    /// The error for a statement that would change one of the plugins' tables. SQL here only reads; the one thing a
    /// query may change is a temporary table of its own. Worded as the refusal of ATTACH is.
    /// </summary>
    public static QueryException ChangeRefused(string change, string holds = "the plugins' records as they are read from the files") =>
        new($"a query may read, and may make temporary tables and indexes of its own; it can't run {change}, which holds {holds}",
            "write a SELECT, or CREATE TEMP TABLE … AS SELECT … followed by a SELECT. A temporary table of your own can be changed");

    static readonly HashSet<string> FirstWords = new(StringComparer.OrdinalIgnoreCase)
        { "SELECT", "WITH", "VALUES", "EXPLAIN", "CREATE", "INSERT", "REPLACE", "UPDATE", "DELETE", "DROP", "ALTER", "PRAGMA" };
    /// <summary>Statements SQLite has that a query here may not run. Any other first word is no SQL statement at all.</summary>
    static readonly HashSet<string> OtherStatements = new(StringComparer.OrdinalIgnoreCase)
        { "ATTACH", "DETACH", "VACUUM", "ANALYZE", "REINDEX", "BEGIN", "COMMIT", "END", "ROLLBACK", "SAVEPOINT", "RELEASE" };

    /// <summary>Statements that never reach SQLite: anything that isn't a read or a change to the query's own tables.</summary>
    public static void CheckFirstWord(List<Tok> toks)
    {
        if (toks.Count == 0) return;
        string first = toks[0].Text;
        // A first word that is no SQL statement (a typing mistake, or text that isn't SQL): say that, not "it can't run SELEC"
        if (toks[0].Kind != TokKind.Word || !FirstWords.Contains(first) && !OtherStatements.Contains(first))
            throw new QueryException($"the SQL starts with \"{(first.Length > 40 ? first[..40] + "…" : first)}\", which is no SQL statement",
                "start with SELECT (or WITH … SELECT); a query may also make temporary tables of its own first (CREATE TEMP TABLE … AS SELECT …)");
        if (!FirstWords.Contains(first))
            throw new QueryException($"a query may read, and may make temporary tables and indexes of its own; it can't run {first.ToUpperInvariant()}",
                "write a SELECT, or CREATE TEMP TABLE … AS SELECT … followed by a SELECT");
        if (first.Equals("CREATE", StringComparison.OrdinalIgnoreCase) && toks.Any(t => t.Is("TRIGGER") || t.Is("VIRTUAL")))
            throw new QueryException("a query can't make triggers or virtual tables", "make a temporary table or view instead");
    }
}
