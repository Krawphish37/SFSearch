// The scratch tables SQL reads. Working files only: one SQLite file per plugin (named by the plugin's reading key),
// and one per set of plugins (the load order's every version, or a long list of plugins). They live in the scratch
// folder, which is wiped at every service start, and any of them can be deleted at any time.
//
// Shared by several processes the way today's service databases are (ServiceDb, LiveDb):
//  - WAL mode, so readers never wait for the writer;
//  - one writer per file, through a named mutex that Windows releases if its holder dies;
//  - a record type goes in inside one transaction, and the Done table says which types are complete;
//  - a new file is made under a temporary name and renamed when it is whole.
using System.IO.Hashing;
using System.Text;
using Microsoft.Data.Sqlite;
using SFSearch.Index;

namespace SFSearch.Query;

/// <summary>One plugin whose records a scratch file holds.</summary>
public sealed record ScratchMember(string Plugin, string Path, long Size, Key128 ReadingKey, Key128 ContentKey, int? LoadOrder);

/// <summary>What the Done table says about one signature of a scratch file.</summary>
public sealed record DoneRow(string Sig, long Rows, long JsonBytes, double Seconds, int Skipped, bool LinksDone);

public sealed class ScratchFile
{
    /// <summary>Raised when the layout of the tables changes: a file of another format is never opened.
    /// The tables behind Links and Forms are LinkRows and FormRows: a table named like a view would be found
    /// by the bare name before the view is made, and a query would read the wrong thing.</summary>
    public const int Format = 1;

    public required string Path { get; init; }
    /// <summary>True for a file that holds several plugins (its rows carry Plugin, LoadOrder and IsWinner).</summary>
    public required bool IsSet { get; init; }
    public required IReadOnlyList<ScratchMember> Members { get; init; }
    /// <summary>The name the file is attached under in a query (p0, p1, …).</summary>
    public string Alias { get; set; } = "";

    static string? _buildKey;

    /// <summary>
    /// Eight hex digits that change with the build that writes the tables' JSON: Mutagen (its Starfield assembly), the
    /// record layer (the serializers it generates) and this library. A file made by another build is never read: its
    /// name differs. The service empties the scratch folder at every start, but the command line doesn't, so before
    /// this a query run on the command line after moving to Mutagen 0.55.0-fixes.14 read the tables fixes.13 had
    /// written (Spell's Type still as a list of flags: notes\fixes14.md).
    /// </summary>
    public static string BuildKey => _buildKey ??= Convert.ToHexString(XxHash128.Hash(Encoding.UTF8.GetBytes(string.Join("|",
        typeof(Mutagen.Bethesda.Starfield.IWeaponGetter).Assembly.ManifestModule.ModuleVersionId,
        typeof(SFSearch.Records.ItemPlace).Module.ModuleVersionId, typeof(ScratchFile).Module.ModuleVersionId))))[..8].ToLowerInvariant();

    /// <summary>A plugin's file: named by its reading key and the language, and for a localized plugin with string files
    /// by their stamp too (GAPS S31): a table read before a string file changed is never used after it. The build key
    /// comes first: a table another build wrote is never used either.</summary>
    public static string PluginFileName(Key128 readingKey, string language, StringsStamp? strings = null) =>
        $"q{Format}-{BuildKey}-{language.ToLowerInvariant()}-{readingKey}{(strings is { Stamped: true, Key.IsZero: false } ? "-s" + strings.Key : "")}.db";

    public static string SetFileName(string kind, string language, IEnumerable<string> keyParts)
    {
        var h = new XxHash128();
        foreach (var p in keyParts) { h.Append(Encoding.UTF8.GetBytes(p)); h.Append(new byte[] { 0 }); }
        return $"{kind}{Format}-{BuildKey}-{language.ToLowerInvariant()}-{Convert.ToHexString(h.GetCurrentHash()).ToLowerInvariant()}.db";
    }

    const string Ddl = @"
CREATE TABLE Meta(Key TEXT PRIMARY KEY, Value TEXT) WITHOUT ROWID;
CREATE TABLE Members(Ord INTEGER PRIMARY KEY, Plugin TEXT, LoadOrder INT, ReadingKey TEXT, ContentKey TEXT, Size INT);
CREATE TABLE Rec(Seq INTEGER PRIMARY KEY, Ord INT NOT NULL, Type TEXT NOT NULL, Sig TEXT NOT NULL, FormKey TEXT NOT NULL COLLATE NOCASE, EditorID TEXT COLLATE NOCASE,
    IsOverride INT NOT NULL, Pos INT NOT NULL, Plugin TEXT COLLATE NOCASE, LoadOrder INT, IsWinner INT, Json BLOB NOT NULL, Lnk BLOB);
CREATE INDEX Rec_Type ON Rec(Type, Ord, EditorID, FormKey, IsOverride, Plugin, LoadOrder, IsWinner);
CREATE INDEX Rec_FormKey ON Rec(FormKey);
CREATE TABLE Done(Sig TEXT PRIMARY KEY, Rows INT, JsonBytes INT, Seconds REAL, Skipped INT, LinksDone INT NOT NULL DEFAULT 0) WITHOUT ROWID;
CREATE TABLE Skipped(Sig TEXT, Plugin TEXT, FormKey TEXT, Reason TEXT);
CREATE TABLE LinkRows(FromKey TEXT COLLATE NOCASE, FromType TEXT, Field TEXT, Idx INT, ToKey TEXT COLLATE NOCASE, Path TEXT, Plugin TEXT COLLATE NOCASE, IsWinner INT);
CREATE INDEX Links_To ON LinkRows(ToKey);
CREATE INDEX Links_From ON LinkRows(FromKey);
CREATE TABLE FormRows(FormKey TEXT COLLATE NOCASE, Sig TEXT, EditorID TEXT COLLATE NOCASE, Plugin TEXT COLLATE NOCASE, Parent TEXT COLLATE NOCASE, IsOverride INT, Pos INT, LoadOrder INT, IsWinner INT);
CREATE TABLE Proj(Type TEXT, TableName TEXT PRIMARY KEY, Cols TEXT, Rows INT) WITHOUT ROWID;
";

    /// <summary>Makes the file when it isn't there: under a temporary name, renamed when it is whole. Another
    /// process that got there first wins, and its file is used.</summary>
    public void EnsureCreated()
    {
        if (File.Exists(Path)) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string tmp = $"{Path}.tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
        try
        {
            using (var c = new SqliteConnection($"Data Source={tmp};Pooling=False"))
            {
                c.Open();
                // 8 KB pages, not SQLite's 4 KB: a record's JSON is over 1 KB on average and many are far larger, so
                // with 4 KB pages most rows spill into overflow pages and the one inserting thread is what a read
                // waits for. Measured on Starfield.esm: the first read of Planet 1.9 s with 4 KB, 1.3 s with 8 KB;
                // GenericBaseForm 2.0 and 1.5 s. 16 KB inserts faster still, and reads whole rows back about half
                // as fast again (design\agent-first\notes\perf-reads.md).
                Exec(c, "PRAGMA page_size = 8192; PRAGMA journal_mode = WAL; PRAGMA synchronous = OFF;");
                using var tx = c.BeginTransaction();
                Exec(c, Ddl);
                Exec(c, $"INSERT INTO Meta VALUES ('Format', '{Format}'), ('Kind', '{(IsSet ? "set" : "plugin")}'), ('Made', '{DateTime.UtcNow:O}')");
                using var ins = c.CreateCommand();
                ins.CommandText = "INSERT INTO Members VALUES ($o, $p, $lo, $rk, $ck, $s)";
                var po = ins.Parameters.Add("$o", SqliteType.Integer); var pp = ins.Parameters.Add("$p", SqliteType.Text); var plo = ins.Parameters.Add("$lo", SqliteType.Integer);
                var prk = ins.Parameters.Add("$rk", SqliteType.Text); var pck = ins.Parameters.Add("$ck", SqliteType.Text); var ps = ins.Parameters.Add("$s", SqliteType.Integer);
                for (int i = 0; i < Members.Count; i++)
                {
                    var m = Members[i];
                    po.Value = i; pp.Value = m.Plugin; plo.Value = (object?)m.LoadOrder ?? DBNull.Value; prk.Value = m.ReadingKey.ToString(); pck.Value = m.ContentKey.ToString(); ps.Value = m.Size;
                    ins.ExecuteNonQuery();
                }
                tx.Commit();
            }
            // Closing the last connection folds the WAL back into the file, so the file can be renamed on its own
            foreach (string side in new[] { tmp + "-wal", tmp + "-shm" }) if (File.Exists(side)) File.Delete(side);
            try { File.Move(tmp, Path, overwrite: false); }
            catch (IOException) when (File.Exists(Path)) { }
        }
        finally
        {
            foreach (string f in new[] { tmp, tmp + "-wal", tmp + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public SqliteConnection OpenWriter()
    {
        var c = new SqliteConnection($"Data Source={Path};Pooling=False");
        c.Open();
        Exec(c, "PRAGMA synchronous = OFF; PRAGMA temp_store = MEMORY; PRAGMA busy_timeout = 60000; PRAGMA cache_size = -65536;");
        return c;
    }

    /// <summary>
    /// Folds the WAL back into the file and empties it. Without this the WAL only grows: the query connections are
    /// read only, so the last one to close can't clear it, and every later open would read all of it again (measured:
    /// 42 ms to open a file with a 53 MB WAL, 2 ms with none). When another process is in the middle of a read the
    /// WAL can't be emptied now; the next writer does it.
    /// </summary>
    public void FoldWal(SqliteConnection writer)
    {
        try { Exec(writer, "PRAGMA busy_timeout = 250; PRAGMA wal_checkpoint(TRUNCATE); PRAGMA busy_timeout = 60000;"); }
        catch (SqliteException) { }
    }

    /// <summary>The file as a URI SQLite opens read only.</summary>
    public string ReadOnlyUri => "file:///" + Path.Replace('\\', '/').Replace("%", "%25").Replace("?", "%3f").Replace("#", "%23") + "?mode=ro";

    public static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static Dictionary<string, DoneRow> ReadDone(SqliteConnection c, string schema)
    {
        var d = new Dictionary<string, DoneRow>(StringComparer.Ordinal);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT Sig, Rows, JsonBytes, Seconds, Skipped, LinksDone FROM {schema}.Done";
        using var r = cmd.ExecuteReader();
        while (r.Read()) d[r.GetString(0)] = new DoneRow(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetDouble(3), r.GetInt32(4), r.GetInt64(5) != 0);
        return d;
    }

    /// <summary>
    /// The right to write one scratch file. One writer at a time across every process: a named mutex, which Windows
    /// hands on if its holder dies (the dead writer's transaction never committed, so the file is as it was).
    /// </summary>
    public sealed class WriterLock : IDisposable
    {
        readonly Mutex _m;
        bool _held;
        /// <summary>How long this writer waited for another one.</summary>
        public double WaitedSeconds { get; }

        public WriterLock(string path, CancellationToken cancel = default)
        {
            string key = Convert.ToHexString(XxHash128.Hash(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path).ToLowerInvariant())));
            _m = new Mutex(false, @"Local\sfsquery_" + key);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                while (!_m.WaitOne(200)) cancel.ThrowIfCancellationRequested();
            }
            catch (AbandonedMutexException) { }
            _held = true;
            WaitedSeconds = clock.Elapsed.TotalSeconds;
        }

        public void Dispose()
        {
            if (_held) { try { _m.ReleaseMutex(); } catch (ApplicationException) { } _held = false; }
            _m.Dispose();
        }
    }
}
