// One prepared INSERT run many times through SQLite's own calls. Microsoft.Data.Sqlite's command object costs a few
// microseconds a row on top of SQLite; for millions of rows (the header index as the Forms table, links, the named
// columns of placed objects) that is most of the time.
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace SFSearch.Query;

sealed class BulkInsert : IDisposable
{
    readonly sqlite3 _db;
    readonly sqlite3_stmt _stmt;

    public BulkInsert(SqliteConnection connection, string sql)
    {
        _db = connection.Handle ?? throw new InvalidOperationException("the connection isn't open");
        int rc = raw.sqlite3_prepare_v2(_db, sql, out _stmt);
        if (rc != raw.SQLITE_OK) throw new SqliteException($"{raw.sqlite3_errmsg(_db).utf8_to_string()} (in: {sql})", rc);
    }

    public void Text(int i, string? s) { if (s == null) raw.sqlite3_bind_null(_stmt, i); else raw.sqlite3_bind_text(_stmt, i, s); }
    public void Int(int i, long v) => raw.sqlite3_bind_int64(_stmt, i, v);
    public void Int(int i, long? v) { if (v == null) raw.sqlite3_bind_null(_stmt, i); else raw.sqlite3_bind_int64(_stmt, i, v.Value); }
    public void Blob(int i, byte[]? b) { if (b == null) raw.sqlite3_bind_null(_stmt, i); else raw.sqlite3_bind_blob(_stmt, i, b); }
    public void Null(int i) => raw.sqlite3_bind_null(_stmt, i);

    /// <summary>A value as a SQLite reader gave it: integer, real, text, bytes or nothing.</summary>
    public void Value(int i, object? v)
    {
        switch (v)
        {
            case null or DBNull: raw.sqlite3_bind_null(_stmt, i); break;
            case long l: raw.sqlite3_bind_int64(_stmt, i, l); break;
            case int n: raw.sqlite3_bind_int64(_stmt, i, n); break;
            case bool b: raw.sqlite3_bind_int64(_stmt, i, b ? 1 : 0); break;
            case double d: raw.sqlite3_bind_double(_stmt, i, d); break;
            case byte[] bytes: raw.sqlite3_bind_blob(_stmt, i, bytes); break;
            default: raw.sqlite3_bind_text(_stmt, i, Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? ""); break;
        }
    }

    public void Step()
    {
        int rc = raw.sqlite3_step(_stmt);
        if (rc != raw.SQLITE_DONE)
        {
            string message = raw.sqlite3_errmsg(_db).utf8_to_string();
            raw.sqlite3_reset(_stmt);
            throw new SqliteException(message, rc);
        }
        raw.sqlite3_reset(_stmt);
    }

    public void Dispose() => _stmt.Dispose();
}
