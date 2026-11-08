using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace SFSearch
{
    /// <summary>
    /// Remembers raw-byte scans for `Refs` (2026-11-07): whether a plugin's file mentions a record at all, and which record
    /// types hold the mentions. Kept in Cache\_refscan.db by the plugin's size and date, so a repeat of `Refs` across all of
    /// Data reads a list instead of scanning hundreds of files again. Shared by every process (WAL, retries on busy).
    /// </summary>
    public static class RefScanCache
    {
        static string DbPath => Path.Combine(Sfs.HomeDir, "Cache", "_refscan.db");
        static readonly object Gate = new();
        static bool _ready;

        static string? StampOf(string plugin)
        {
            var fi = new FileInfo(Path.Combine(Program.GetDataFolderPathPublic(), plugin));
            return fi.Exists ? $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}" : null;
        }

        static SqliteConnection Open()
        {
            var c = new SqliteConnection($"Data Source={DbPath};Pooling=False;Default Timeout=60");
            c.Open();
            using var x = c.CreateCommand();
            x.CommandText = "PRAGMA busy_timeout = 60000;";
            x.ExecuteNonQuery();
            lock (Gate)
            {
                if (!_ready)
                {
                    x.CommandText = @"PRAGMA journal_mode = WAL;
                        CREATE TABLE IF NOT EXISTS Scans (Plugin TEXT, Target TEXT, Stamp TEXT, Mentions INTEGER, Types TEXT, PRIMARY KEY (Plugin, Target)) WITHOUT ROWID;";
                    x.ExecuteNonQuery();
                    _ready = true;
                }
            }
            return c;
        }

        /// <summary>The remembered answer for this copy of the plugin, or null.</summary>
        public static (bool Mentions, string? Types)? Get(string plugin, string target)
        {
            string? stamp = StampOf(plugin);
            if (stamp == null || !File.Exists(DbPath)) return null;
            try
            {
                using var c = Open();
                using var x = c.CreateCommand();
                x.CommandText = "SELECT Mentions, Types FROM Scans WHERE Plugin = @p AND Target = @t AND Stamp = @s";
                x.Parameters.AddWithValue("@p", plugin.ToLowerInvariant());
                x.Parameters.AddWithValue("@t", target.ToLowerInvariant());
                x.Parameters.AddWithValue("@s", stamp);
                using var r = x.ExecuteReader();
                if (!r.Read()) return null;
                return (r.GetInt32(0) == 1, r.IsDBNull(1) ? null : r.GetString(1));
            }
            catch (SqliteException) { return null; }
        }

        public static void SetMentions(string plugin, string target, bool mentions) => Set(plugin, target, mentions, null, typesOnly: false);

        public static void SetTypes(string plugin, string target, string types) => Set(plugin, target, true, types, typesOnly: true);

        static void Set(string plugin, string target, bool mentions, string? types, bool typesOnly)
        {
            string? stamp = StampOf(plugin);
            if (stamp == null) return;
            try
            {
                using var c = Open();
                using var x = c.CreateCommand();
                x.CommandText = typesOnly
                    ? "INSERT INTO Scans VALUES (@p, @t, @s, 1, @y) ON CONFLICT(Plugin, Target) DO UPDATE SET Stamp = @s, Mentions = 1, Types = @y"
                    : "INSERT INTO Scans VALUES (@p, @t, @s, @m, NULL) ON CONFLICT(Plugin, Target) DO UPDATE SET Mentions = @m, Types = CASE WHEN Stamp = @s THEN Types END, Stamp = @s";
                x.Parameters.AddWithValue("@p", plugin.ToLowerInvariant());
                x.Parameters.AddWithValue("@t", target.ToLowerInvariant());
                x.Parameters.AddWithValue("@s", stamp);
                x.Parameters.AddWithValue("@m", mentions ? 1 : 0);
                x.Parameters.AddWithValue("@y", (object?)types ?? DBNull.Value);
                x.ExecuteNonQuery();
            }
            catch (SqliteException) { } // a cache: never worth failing a command over
        }
    }
}
