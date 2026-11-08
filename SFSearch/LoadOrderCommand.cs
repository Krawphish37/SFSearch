using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;

namespace SFSearch
{
    /// <summary>
    /// The load order and the load-order index (2026-11-07).
    /// The order comes from plugins.txt, Starfield.ccc and the game's implicit plugins (Mutagen's listing reader; no plugin is
    /// opened). The index (`_loadorder.db` beside the databases) has one row per record per plugin in Data, read from the
    /// record headers only, and a Winners table: the last enabled version of every record. It updates plugin by plugin when a
    /// file changes, and re-reads a plugin whose master changed size, because its FormIDs then point somewhere else.
    /// </summary>
    public static class LoadOrderIndex
    {
        public sealed class PluginInfo
        {
            public string Name = "";
            public int Pos = -1;              // place in the load order; -1 when not listed
            public bool Enabled;
            public string Size = "Full";      // Full | Medium | Light
            public string Slot = "";          // runtime prefix: 0A, FD:03, FE:09A
            public long Length;
            public DateTime Mtime;
            public string Stamp = "";
            public List<string> Masters = new();
            public bool IsEsm;
        }

        /// <summary>Kept in Cache in every mode (stored databases, --live, the service), like the history and the scripts: it holds no plugin data, only names and positions.</summary>
        public static string DbPath => Path.Combine(Sfs.HomeDir, "Cache", "_loadorder.db");

        static readonly string[] OfficialPrefixes = { "starfield.esm", "constellation.esm", "oldmars.esm", "blueprintships-starfield.esm", "shatteredspace.esm", "sfbgs" };
        public static bool IsOfficial(string plugin) => OfficialPrefixes.Any(p => plugin.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        // ---------------------------------------------------------------- The listing (cheap: plugins.txt and plugin headers)
        static readonly Dictionary<string, (string Stamp, string Size, bool Esm, List<string> Masters)> _headers = new(StringComparer.OrdinalIgnoreCase);

        static (string Size, bool Esm, List<string> Masters)? ReadHeader(string path, string stamp)
        {
            lock (_headers) if (_headers.TryGetValue(path, out var h) && h.Stamp == stamp) return (h.Size, h.Esm, h.Masters);
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var head = new byte[24];
                if (fs.Read(head, 0, 24) < 24 || Encoding.ASCII.GetString(head, 0, 4) != "TES4") return null;
                uint flags = BitConverter.ToUInt32(head, 8);
                var data = new byte[BitConverter.ToInt32(head, 4)];
                fs.ReadExactly(data);
                var masters = new List<string>();
                for (int pos = 0; pos + 6 <= data.Length;)
                {
                    string sig = Encoding.ASCII.GetString(data, pos, 4);
                    int len = BitConverter.ToUInt16(data, pos + 4);
                    if (sig == "MAST" && len > 1) masters.Add(Encoding.UTF8.GetString(data, pos + 6, len - 1));
                    pos += 6 + len;
                }
                bool light = (flags & 0x100) != 0 || path.EndsWith(".esl", StringComparison.OrdinalIgnoreCase);
                string size = light ? "Light" : (flags & 0x400) != 0 ? "Medium" : "Full";
                bool esm = (flags & 0x1) != 0 || !path.EndsWith(".esp", StringComparison.OrdinalIgnoreCase);
                lock (_headers) _headers[path] = (stamp, size, esm, masters);
                return (size, esm, masters);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>Every plugin in Data, in load order first (enabled or not), then the ones not listed.</summary>
        public static List<PluginInfo> Listing()
        {
            string data = Program.GetDataFolderPathPublic();
            var files = Directory.EnumerateFiles(data)
                .Where(f => System.Text.RegularExpressions.Regex.IsMatch(f, @"\.es[mpl]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .ToDictionary(f => Path.GetFileName(f), f => f, StringComparer.OrdinalIgnoreCase);
            var list = new List<PluginInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<ILoadOrderListingGetter> listed;
            try { listed = LoadOrder.GetLoadOrderListings(GameRelease.Starfield, data, throwOnMissingMods: false).ToList(); }
            catch (Exception e) { Console.Error.WriteLine($"[loadorder] couldn't read the load order ({e.Message}); showing Data in name order."); listed = new(); }
            int pos = 0;
            foreach (var l in listed)
            {
                string name = l.FileName.ToString();
                if (!seen.Add(name)) continue;
                var p = new PluginInfo { Name = files.TryGetValue(name, out var f) ? Path.GetFileName(f) : name, Pos = pos++, Enabled = l.Enabled };
                list.Add(p);
            }
            foreach (var name in files.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                if (seen.Add(name)) list.Add(new PluginInfo { Name = name, Pos = -1, Enabled = false });
            int full = 0, medium = 0, small = 0;
            foreach (var p in list)
            {
                if (!files.TryGetValue(p.Name, out var path)) { p.Enabled = false; p.Stamp = ""; continue; } // listed but not in Data
                var fi = new FileInfo(path);
                p.Length = fi.Length; p.Mtime = fi.LastWriteTimeUtc;
                p.Stamp = $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
                var h = ReadHeader(path, p.Stamp);
                if (h != null) { p.Size = h.Value.Size; p.IsEsm = h.Value.Esm; p.Masters = h.Value.Masters; }
                if (!p.Enabled) continue;
                p.Slot = p.Size == "Light" ? $"FE:{small++:X3}" : p.Size == "Medium" ? $"FD:{medium++:X2}" : $"{full++:X2}";
            }
            return list;
        }

        // ---------------------------------------------------------------- The index
        // Layout (kept small: names are stored once): Plugins (Id, Plugin, position, slot, size, counts, checks), Types (Sig4 →
        // type name), VersionRows (FormKey, PluginId, Sig4, EditorID, IsOverride) and WinnerRows (FormKey, PluginId, Sig4,
        // Versions). The views Versions and Winners give them with names. Navmeshes and landscape, and the game's own placed
        // references, are counted in Plugins but not listed: they're most of the big plugins' records. Mods' placed references
        // are listed, so Conflicts finds who edits a placed object without scanning every file.
        const string IndexSchema = "4";

        static void Schema(SqliteConnection c)
        {
            string? have = null;
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT Value FROM Meta WHERE Key = 'Schema'";
                try { have = q.ExecuteScalar() as string; } catch (SqliteException) { }
            }
            using var x = c.CreateCommand();
            if (have != IndexSchema)
                x.CommandText = @"DROP VIEW IF EXISTS Versions; DROP VIEW IF EXISTS Winners; DROP TABLE IF EXISTS Versions; DROP TABLE IF EXISTS Winners;
                    DROP TABLE IF EXISTS VersionRows; DROP TABLE IF EXISTS WinnerRows; DROP TABLE IF EXISTS Plugins; DROP TABLE IF EXISTS Types; DROP TABLE IF EXISTS Meta;";
            else x.CommandText = "SELECT 1";
            x.ExecuteNonQuery();
            x.CommandText = @"PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS Plugins (Id INTEGER PRIMARY KEY, Plugin TEXT UNIQUE COLLATE NOCASE, Pos INTEGER, Enabled INTEGER, Slot TEXT, Size TEXT, Stamp TEXT, Length INTEGER, Modified TEXT,
                    Records INTEGER, NewRecords INTEGER, Overrides INTEGER, Listed INTEGER, Masters TEXT, MasterSizes TEXT, MissingMasters TEXT, LateMasters TEXT, TypeMismatches INTEGER, NoBase INTEGER, Problem TEXT);
                CREATE TABLE IF NOT EXISTS Types (Sig4 TEXT PRIMARY KEY, Signature TEXT);
                CREATE INDEX IF NOT EXISTS idx_types_name ON Types(Signature COLLATE NOCASE);
                CREATE TABLE IF NOT EXISTS VersionRows (FormKey TEXT, PluginId INTEGER, Sig4 TEXT, EditorID TEXT, IsOverride INTEGER, PRIMARY KEY (FormKey, PluginId)) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS idx_versions_plugin ON VersionRows(PluginId);
                CREATE TABLE IF NOT EXISTS WinnerRows (FormKey TEXT PRIMARY KEY, PluginId INTEGER, Sig4 TEXT, Versions INTEGER) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS idx_winners_sig ON WinnerRows(Sig4);
                CREATE INDEX IF NOT EXISTS idx_winners_plugin ON WinnerRows(PluginId);
                CREATE VIEW IF NOT EXISTS Versions AS
                    SELECT v.FormKey, p.Plugin, t.Signature, v.Sig4, v.EditorID, v.IsOverride, p.Pos, p.Enabled
                    FROM VersionRows v JOIN Plugins p ON p.Id = v.PluginId LEFT JOIN Types t ON t.Sig4 = v.Sig4;
                CREATE VIEW IF NOT EXISTS Winners AS
                    SELECT w.FormKey, p.Plugin, t.Signature, w.Sig4,
                        coalesce((SELECT v.EditorID FROM VersionRows v WHERE v.FormKey = w.FormKey AND v.PluginId = w.PluginId), (SELECT v.EditorID FROM VersionRows v WHERE v.FormKey = w.FormKey AND v.IsOverride = 0)) AS EditorID,
                        w.Versions, p.Pos
                    FROM WinnerRows w JOIN Plugins p ON p.Id = w.PluginId LEFT JOIN Types t ON t.Sig4 = w.Sig4;
                CREATE TABLE IF NOT EXISTS Meta (Key TEXT PRIMARY KEY, Value TEXT);
                INSERT OR REPLACE INTO Meta VALUES ('Schema', '" + IndexSchema + "');";
            x.ExecuteNonQuery();
        }

        /// <summary>Navmeshes and landscape are never listed; placed references only from mods, not from the game's own files
        /// (millions of them, defined there and overridden by mods, so a mod's copy is enough to find who edits one).</summary>
        static bool Indexed(string sig4, bool placed, bool official) => sig4 is not ("NAVM" or "LAND") && (!placed || !official);

        static Dictionary<string, (string Stamp, int Pos, bool Enabled, string Size, string MasterSizes)> Stored(SqliteConnection c)
        {
            var d = new Dictionary<string, (string, int, bool, string, string)>(StringComparer.OrdinalIgnoreCase);
            using var x = c.CreateCommand();
            x.CommandText = "SELECT Plugin, Stamp, Pos, Enabled, Size, coalesce(MasterSizes,'') FROM Plugins";
            using var r = x.ExecuteReader();
            while (r.Read()) d[r.GetString(0)] = (r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? -1 : r.GetInt32(2), !r.IsDBNull(3) && r.GetInt32(3) == 1, r.IsDBNull(4) ? "" : r.GetString(4), r.GetString(5));
            return d;
        }

        static string MasterSizes(PluginInfo p, Dictionary<string, PluginInfo> byName) =>
            string.Join(",", p.Masters.Select(m => $"{m}={(byName.TryGetValue(m, out var mi) && mi.Stamp != "" ? mi.Size : "missing")}"));

        static long PluginId(SqliteConnection c, SqliteTransaction tx, string plugin)
        {
            Exec(c, tx, "INSERT OR IGNORE INTO Plugins (Plugin) VALUES (@p)", ("@p", plugin));
            return Scalar(c, tx, "SELECT Id FROM Plugins WHERE Plugin = @p", ("@p", plugin));
        }

        /// <summary>Brings the index up to date and returns its path. Cheap when nothing changed (plugins.txt and file dates only).</summary>
        public static string Ensure()
        {
            var cur = Listing();
            var byName = cur.Where(p => p.Stamp != "").ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            string path = DbPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (UpToDate(path, cur, byName)) return path;
            using var buildLock = new Service.ServiceDb.BuildLock(path);
            using var c = new SqliteConnection($"Data Source={path};Pooling=False");
            c.Open();
            using (var x = c.CreateCommand()) { x.CommandText = "PRAGMA busy_timeout = 60000; PRAGMA synchronous = NORMAL;"; x.ExecuteNonQuery(); }
            Schema(c);
            var stored = Stored(c);
            // A plugin needs reading when its file changed, or when one of its masters changed size: its FormIDs then land elsewhere.
            var stale = byName.Values.Where(p => !stored.TryGetValue(p.Name, out var s) || s.Stamp != p.Stamp || s.MasterSizes != MasterSizes(p, byName)).ToList();
            var gone = stored.Keys.Where(n => !byName.ContainsKey(n)).ToList();
            bool orderChanged = cur.Any(p => p.Stamp != "" && (!stored.TryGetValue(p.Name, out var s) || s.Pos != p.Pos || s.Enabled != p.Enabled)) || gone.Count > 0;
            if (stale.Count == 0 && !orderChanged) return path;
            var clock = Stopwatch.StartNew();
            if (stale.Count > 3) Console.Error.WriteLine($"[loadorder] indexing {stale.Count} plugin(s) from their record headers{(stored.Count == 0 ? " (first time: a few minutes for a full Data folder)" : "")}...");
            var history = new History(stored.Count == 0);

            var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool fullWinners = orderChanged || stored.Count == 0 || stale.Any(p => p.Name.Equals("Starfield.esm", StringComparison.OrdinalIgnoreCase));
            foreach (var g in gone)
            {
                using var tx = c.BeginTransaction();
                long id = PluginId(c, tx, g);
                Exec(c, tx, "DELETE FROM VersionRows WHERE PluginId = @i", ("@i", id));
                Exec(c, tx, "DELETE FROM Plugins WHERE Id = @i", ("@i", id));
                tx.Commit();
                history.Gone(g);
            }
            var types = new HashSet<string>(StringComparer.Ordinal);
            using (var x = c.CreateCommand()) { x.CommandText = "SELECT Sig4 FROM Types"; using var r = x.ExecuteReader(); while (r.Read()) types.Add(r.GetString(0)); }
            // Read in parallel, write one plugin at a time (the biggest first)
            string data = Program.GetDataFolderPathPublic();
            var queue = stale.OrderByDescending(p => p.Length).ToList();
            var results = new System.Collections.Concurrent.BlockingCollection<(PluginInfo P, List<(string Fk, string Sig4, string? Edid, bool Ovr, bool Placed)>? Rows, string? Problem)>(boundedCapacity: 4);
            var reader = Task.Run(() =>
            {
                try
                {
                    Parallel.ForEach(queue, new ParallelOptions { MaxDegreeOfParallelism = 3 }, p =>
                    {
                        try { results.Add((p, ReadRows(Path.Combine(data, p.Name), p.Name), null)); }
                        catch (Exception e) { results.Add((p, null, e.Message)); }
                    });
                }
                finally { results.CompleteAdding(); }
            });
            foreach (var (p, rows, problem) in results.GetConsumingEnumerable())
            {
                using var tx = c.BeginTransaction();
                long id = PluginId(c, tx, p.Name);
                if (!fullWinners) using (var x = c.CreateCommand()) { x.Transaction = tx; x.CommandText = "SELECT FormKey FROM VersionRows WHERE PluginId = @i"; x.Parameters.AddWithValue("@i", id); using var r = x.ExecuteReader(); while (r.Read()) affected.Add(r.GetString(0)); }
                Exec(c, tx, "DELETE FROM VersionRows WHERE PluginId = @i", ("@i", id));
                int total = 0, ovr = 0, listed = 0;
                bool official = IsOfficial(p.Name);
                if (rows != null)
                {
                    using var ins = c.CreateCommand(); ins.Transaction = tx;
                    ins.CommandText = "INSERT OR IGNORE INTO VersionRows VALUES (@a,@b,@c,@d,@e)";
                    var ps = new[] { "@a", "@b", "@c", "@d", "@e" }.Select(n => ins.Parameters.Add(n, SqliteType.Text)).ToArray();
                    ps[1].Value = id;
                    foreach (var (fk, sig4, edid, isOvr, placed) in rows)
                    {
                        total++; if (isOvr) ovr++;
                        if (!Indexed(sig4, placed, official)) continue;
                        if (types.Add(sig4)) Exec(c, tx, "INSERT OR IGNORE INTO Types VALUES (@s, @n)", ("@s", sig4), ("@n", Program.TypeNameOf(sig4)));
                        ps[0].Value = fk; ps[2].Value = sig4; ps[3].Value = (object?)edid ?? DBNull.Value; ps[4].Value = isOvr ? 1 : 0;
                        ins.ExecuteNonQuery();
                        listed++;
                        if (!fullWinners) affected.Add(fk);
                    }
                }
                Exec(c, tx, @"UPDATE Plugins SET Stamp = @s, Records = @t, NewRecords = @n, Overrides = @o, Listed = @l, MasterSizes = @m, Problem = @x WHERE Id = @i",
                    ("@i", id), ("@s", p.Stamp), ("@t", total), ("@n", total - ovr), ("@o", ovr), ("@l", listed), ("@m", MasterSizes(p, byName)), ("@x", (object?)problem ?? DBNull.Value));
                tx.Commit();
                history.Read(p, rows?.Select(r => (r.Fk, r.Sig4, r.Edid, r.Ovr)).ToList(), problem);
            }
            reader.Wait();

            // Positions, slots and master notes for everything listed
            using (var tx = c.BeginTransaction())
            {
                foreach (var p in cur.Where(p => p.Stamp != ""))
                {
                    var missing = p.Masters.Where(m => !byName.ContainsKey(m)).ToList();
                    var late = p.Masters.Where(m => byName.TryGetValue(m, out var mi) && p.Enabled && (!mi.Enabled || mi.Pos > p.Pos)).ToList();
                    Exec(c, tx, @"UPDATE Plugins SET Pos = @pos, Enabled = @en, Slot = @slot, Size = @size, Length = @len, Modified = @mod, Masters = @m, MissingMasters = @miss, LateMasters = @late WHERE Plugin = @p",
                        ("@p", p.Name), ("@pos", p.Pos < 0 ? DBNull.Value : p.Pos), ("@en", p.Enabled ? 1 : 0), ("@slot", p.Slot), ("@size", p.Size), ("@len", p.Length),
                        ("@mod", p.Mtime.ToString("yyyy-MM-dd HH:mm")), ("@m", string.Join(", ", p.Masters)), ("@miss", missing.Count > 0 ? string.Join(", ", missing) : DBNull.Value), ("@late", late.Count > 0 ? string.Join(", ", late) : DBNull.Value));
                }
                tx.Commit();
            }

            // Winners: the last enabled version of each record (SQLite takes the other columns from the row with the max)
            using (var tx = c.BeginTransaction())
            {
                const string pick = "SELECT v.FormKey, v.PluginId, v.Sig4, count(*) AS n, max(p.Pos) FROM VersionRows v JOIN Plugins p ON p.Id = v.PluginId WHERE p.Enabled = 1";
                if (fullWinners || affected.Count > 500000)
                {
                    Exec(c, tx, "DELETE FROM WinnerRows");
                    Exec(c, tx, $"INSERT INTO WinnerRows SELECT FormKey, PluginId, Sig4, n FROM ({pick} GROUP BY v.FormKey)");
                }
                else if (affected.Count > 0)
                {
                    Exec(c, tx, "CREATE TEMP TABLE IF NOT EXISTS aff (FormKey TEXT PRIMARY KEY)");
                    Exec(c, tx, "DELETE FROM temp.aff");
                    using (var ins = c.CreateCommand())
                    {
                        ins.Transaction = tx; ins.CommandText = "INSERT OR IGNORE INTO temp.aff VALUES (@k)";
                        var k = ins.Parameters.Add("@k", SqliteType.Text);
                        foreach (var fk in affected) { k.Value = fk; ins.ExecuteNonQuery(); }
                    }
                    Exec(c, tx, "DELETE FROM WinnerRows WHERE FormKey IN (SELECT FormKey FROM temp.aff)");
                    Exec(c, tx, $"INSERT INTO WinnerRows SELECT FormKey, PluginId, Sig4, n FROM ({pick} AND v.FormKey IN (SELECT FormKey FROM temp.aff) GROUP BY v.FormKey)");
                }
                tx.Commit();
            }

            // Overrides whose type differs from the record they override, or that override nothing: the signs of a patch built
            // before its master changed size (trap 15). Checked for every plugin read now and every plugin whose master was.
            var recheck = new HashSet<string>(stale.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var p in byName.Values) if (p.Masters.Any(recheck.Contains)) recheck.Add(p.Name);
            using (var tx = c.BeginTransaction())
            {
                foreach (var name in recheck)
                {
                    long id = PluginId(c, tx, name);
                    long mismatch = Scalar(c, tx, @"SELECT count(*) FROM VersionRows o JOIN VersionRows b ON b.FormKey = o.FormKey AND b.IsOverride = 0 AND b.PluginId <> o.PluginId
                                                   WHERE o.PluginId = @i AND o.IsOverride = 1 AND b.Sig4 <> o.Sig4", ("@i", id));
                    long noBase = Scalar(c, tx, @"SELECT count(*) FROM VersionRows o WHERE o.PluginId = @i AND o.IsOverride = 1
                                                   AND NOT EXISTS (SELECT 1 FROM VersionRows b WHERE b.FormKey = o.FormKey AND b.IsOverride = 0)", ("@i", id));
                    Exec(c, tx, "UPDATE Plugins SET TypeMismatches = @m, NoBase = @n WHERE Id = @i", ("@i", id), ("@m", mismatch), ("@n", noBase));
                    history.Checked(name, byName.TryGetValue(name, out var pi) ? pi.Stamp : "", mismatch, noBase);
                }
                Exec(c, tx, "INSERT OR REPLACE INTO Meta VALUES ('Updated', @t)", ("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
                tx.Commit();
            }
            history.Save(byName);
            if (stale.Count > 3) Console.Error.WriteLine($"[loadorder] index ready in {clock.Elapsed.TotalSeconds:0} s.");
            return path;
        }

        static bool UpToDate(string path, List<PluginInfo> cur, Dictionary<string, PluginInfo> byName)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
                c.Open();
                using (var q = c.CreateCommand()) { q.CommandText = "SELECT Value FROM Meta WHERE Key = 'Schema'"; if (q.ExecuteScalar() as string != IndexSchema) return false; }
                var stored = Stored(c);
                if (stored.Count != byName.Count) return false;
                foreach (var p in byName.Values)
                    if (!stored.TryGetValue(p.Name, out var s) || s.Stamp != p.Stamp || s.Pos != p.Pos || s.Enabled != p.Enabled || s.MasterSizes != MasterSizes(p, byName)) return false;
                return true;
            }
            catch (SqliteException) { return false; }
        }

        /// <summary>FormKey, type, EditorID, new-or-override and placed-or-not for every record in a plugin, from its record headers.</summary>
        static List<(string Fk, string Sig4, string? Edid, bool Ovr, bool Placed)> ReadRows(string path, string name)
        {
            var key = ModKey.FromFileName(name);
            var masters = HeaderScanner.MasterTable(key, path);
            var raw = LiveDb.RawScanOnce(path);
            var rows = new List<(string, string, string?, bool, bool)>(raw.Count);
            foreach (var r in raw)
            {
                var fk = masters.GetFormKey(FormID.Factory(r.Raw), reference: false);
                rows.Add((Sfs.NormalizeFormKey(fk.ToString()), r.Sig4, r.EditorId, fk.ModKey != key, r.Placed));
            }
            return rows;
        }

        static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] ps)
        {
            using var x = c.CreateCommand(); x.Transaction = tx; x.CommandText = sql; x.CommandTimeout = 0;
            foreach (var (n, v) in ps) x.Parameters.AddWithValue(n, v ?? DBNull.Value);
            x.ExecuteNonQuery();
        }

        static long Scalar(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] ps)
        {
            using var x = c.CreateCommand(); x.Transaction = tx; x.CommandText = sql; x.CommandTimeout = 0;
            foreach (var (n, v) in ps) x.Parameters.AddWithValue(n, v ?? DBNull.Value);
            return Convert.ToInt64(x.ExecuteScalar() ?? 0);
        }

        /// <summary>Opens the index read-only, building or updating it first.</summary>
        public static SqliteConnection Open()
        {
            string path = Ensure();
            var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            c.Open();
            return c;
        }

        // ---------------------------------------------------------------- SQL: Query --loadorder
        /// <summary>WinningRecords is a temporary table (the index is attached read-only), so `lo.WinningRecords` is read as it.</summary>
        public static string Rewrite(string sql) =>
            System.Text.RegularExpressions.Regex.Replace(sql, @"\blo\s*\.\s*WinningRecords\b", "temp.WinningRecords", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>Attaches the index to a query connection as `lo`, and fills temp.WinningRecords (lo.WinningRecords) with the
        /// winning records' data for the types the SQL names.</summary>
        public static void Attach(SqliteConnection conn, string sql)
        {
            string path = Ensure();
            using (var x = conn.CreateCommand()) { x.CommandText = "ATTACH DATABASE @p AS lo"; x.Parameters.AddWithValue("@p", path); x.ExecuteNonQuery(); }
            conn.CreateFunction<string?, string?>("winner", fk => fk == null ? null : WinnerOf(path, Sfs.NormalizeFormKey(fk)));
            conn.CreateFunction<string?, string?>("versions", fk => fk == null ? null : VersionsOf(path, Sfs.NormalizeFormKey(fk)));
            if (!System.Text.RegularExpressions.Regex.IsMatch(sql, @"\bWinningRecords\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return;
            var types = LiveDb.TypesNamedIn(sql);
            if (types == null || types.Count == 0)
            {
                Console.Error.WriteLine("[loadorder] WinningRecords holds the full data of the winning records of the types the query names: add e.g. WHERE Signature = 'Weapon' (or IN (...)) to say which.");
                types = new HashSet<string>();
            }
            BuildWinningRecords(conn, types);
        }

        static void BuildWinningRecords(SqliteConnection conn, HashSet<string> types)
        {
            using (var x = conn.CreateCommand())
            {
                x.CommandText = "CREATE TEMP TABLE WinningRecords (FormKey TEXT PRIMARY KEY, Plugin TEXT, Signature TEXT, EditorID TEXT, Name TEXT, Origin TEXT, IsOverride INTEGER, Data TEXT)";
                x.ExecuteNonQuery();
            }
            if (types.Count == 0) return;
            // Winners of those types, by the plugin that wins them. OMODs are one type in the index (OMOD) and several in the databases.
            bool omod = types.Any(t => t.EndsWith("Modification", StringComparison.OrdinalIgnoreCase) || t.Equals("ObjectMod", StringComparison.OrdinalIgnoreCase));
            var byPlugin = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            using (var x = conn.CreateCommand())
            {
                var names = types.Select((t, i) => $"@t{i}").ToList();
                x.CommandText = $"SELECT w.FormKey, p.Plugin FROM lo.WinnerRows w JOIN lo.Plugins p ON p.Id = w.PluginId WHERE w.Sig4 IN (SELECT Sig4 FROM lo.Types WHERE Signature IN ({string.Join(",", names)}) COLLATE NOCASE){(omod ? " OR w.Sig4 = 'OMOD'" : "")}";
                int i = 0; foreach (var t in types) x.Parameters.AddWithValue($"@t{i++}", t);
                using var r = x.ExecuteReader();
                while (r.Read())
                {
                    if (!byPlugin.TryGetValue(r.GetString(1), out var l)) byPlugin[r.GetString(1)] = l = new List<string>();
                    l.Add(r.GetString(0));
                }
            }
            var clock = Stopwatch.StartNew();
            int rows = 0;
            using var tx = conn.BeginTransaction();
            using var ins = conn.CreateCommand(); ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO temp.WinningRecords VALUES (@a,@b,@c,@d,@e,@f,@g,@h)";
            var ps = new[] { "@a", "@b", "@c", "@d", "@e", "@f", "@g", "@h" }.Select(n => ins.Parameters.Add(n, SqliteType.Text)).ToArray();
            foreach (var (plugin, keys) in byPlugin)
            {
                // The winning plugin's database must hold these types (the service and --live read only what's asked)
                LiveDb.Forced = new LiveDb.Needs { Records = true, Types = new HashSet<string>(types, StringComparer.OrdinalIgnoreCase), FormKeys = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase) };
                try { Program.EnsureCacheUpToDatePublic(plugin); }
                catch (Exception e) { Console.Error.WriteLine($"[loadorder] {plugin}: {e.Message}"); continue; }
                finally { LiveDb.Forced = null; }
                if (!Sfs.HasCache(plugin)) continue;
                var want = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
                using var db = new SqliteConnection($"Data Source={Sfs.CachePath(plugin)};Mode=ReadOnly;Pooling=False");
                db.Open();
                using var q = db.CreateCommand();
                var names = types.Select((t, i) => $"@t{i}").ToList();
                q.CommandText = $"SELECT FormKey, Signature, EditorID, Name, Origin, IsOverride, Data FROM Records WHERE Signature IN ({string.Join(",", names)})";
                int k = 0; foreach (var t in types) q.Parameters.AddWithValue($"@t{k++}", t);
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    string fk = r.GetString(0);
                    if (!want.Contains(fk)) continue;
                    ps[0].Value = fk; ps[1].Value = plugin;
                    for (int c = 1; c < 7; c++) ps[c + 1].Value = r.IsDBNull(c) ? DBNull.Value : r.GetValue(c);
                    ins.ExecuteNonQuery();
                    rows++;
                }
            }
            tx.Commit();
            Console.Error.WriteLine($"[loadorder] WinningRecords: {rows} winning record(s) of {string.Join(", ", types)} from {byPlugin.Count} plugin(s) in {clock.Elapsed.TotalSeconds:0.0} s.");
        }

        static string? WinnerOf(string path, string fk)
        {
            using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"); c.Open();
            using var x = c.CreateCommand(); x.CommandText = "SELECT p.Plugin FROM WinnerRows w JOIN Plugins p ON p.Id = w.PluginId WHERE w.FormKey = @k"; x.Parameters.AddWithValue("@k", fk);
            return x.ExecuteScalar() as string;
        }

        static string? VersionsOf(string path, string fk)
        {
            using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"); c.Open();
            using var x = c.CreateCommand();
            x.CommandText = "SELECT p.Plugin, p.Enabled FROM VersionRows v JOIN Plugins p ON p.Id = v.PluginId WHERE v.FormKey = @k ORDER BY p.Enabled DESC, coalesce(p.Pos, 1000000), p.Plugin";
            x.Parameters.AddWithValue("@k", fk);
            using var r = x.ExecuteReader();
            var on = new List<string>(); var off = new List<string>();
            while (r.Read()) (r.GetInt32(1) == 1 ? on : off).Add(r.GetString(0));
            return on.Count + off.Count == 0 ? null : string.Join(" > ", on) + (off.Count > 0 ? $" (not loaded: {string.Join(", ", off)})" : "");
        }

        // ---------------------------------------------------------------- EditorIDs for labels (shared by every worker, on disk)
        static SqliteConnection? _labels;
        static readonly Dictionary<string, bool> _freshPlugin = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Forget the per-command state (called before every command).</summary>
        public static void ResetForCommand()
        {
            lock (_freshPlugin) { _freshPlugin.Clear(); _labels?.Dispose(); _labels = null; }
        }

        /// <summary>A plugin's masters, from its header (remembered by the file's size and date).</summary>
        public static HashSet<string> MastersOf(string path)
        {
            var fi = new FileInfo(path);
            var h = fi.Exists ? ReadHeader(path, $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}") : null;
            return new HashSet<string>(h?.Masters ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The 4-letter types of these records in this plugin, from the index; null when the index doesn't hold this
        /// copy of the plugin or doesn't list one of them (then the file is scanned).</summary>
        public static HashSet<string>? Sig4sIfFresh(string plugin, List<string> formKeys)
        {
            try
            {
                lock (_freshPlugin)
                {
                    if (!PluginFresh(plugin)) return null;
                    var set = new HashSet<string>(StringComparer.Ordinal);
                    using var x = _labels!.CreateCommand();
                    x.CommandText = "SELECT v.Sig4 FROM VersionRows v JOIN Plugins p ON p.Id = v.PluginId WHERE v.FormKey = @k AND p.Plugin = @p";
                    var k = x.Parameters.Add("@k", SqliteType.Text);
                    x.Parameters.AddWithValue("@p", plugin);
                    foreach (var fk in formKeys)
                    {
                        k.Value = Sfs.NormalizeFormKey(fk);
                        if (x.ExecuteScalar() is not string s4) return null;
                        set.Add(s4);
                    }
                    return set;
                }
            }
            catch (SqliteException) { return null; }
        }

        /// <summary>True when the index holds this plugin's current copy (checked once per command). Call with _freshPlugin locked.</summary>
        static bool PluginFresh(string plugin)
        {
            if (_labels == null)
            {
                if (!File.Exists(DbPath)) return false;
                _labels = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Pooling=False");
                _labels.Open();
            }
            if (!_freshPlugin.TryGetValue(plugin, out bool fresh))
            {
                var fi = new FileInfo(Path.Combine(Program.GetDataFolderPathPublic(), plugin));
                using var q = _labels.CreateCommand();
                q.CommandText = "SELECT Stamp FROM Plugins WHERE Plugin = @p";
                q.Parameters.AddWithValue("@p", plugin);
                fresh = fi.Exists && q.ExecuteScalar() as string == $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
                _freshPlugin[plugin] = fresh;
            }
            return fresh;
        }

        /// <summary>A record's EditorID from the index, when the index holds that plugin's current copy. Found = the index
        /// lists the record (its EditorID may still be null). Not found = ask the plugin file.</summary>
        public static (bool Found, string? EditorId) EditorIdFromIndex(string fk)
        {
            try
            {
                lock (_freshPlugin)
                {
                    if (_labels == null)
                    {
                        if (!File.Exists(DbPath)) return (false, null);
                        _labels = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Pooling=False");
                        _labels.Open();
                    }
                    string plugin = Sfs.PluginOf(fk);
                    if (!_freshPlugin.TryGetValue(plugin, out bool fresh))
                    {
                        var fi = new FileInfo(Path.Combine(Program.GetDataFolderPathPublic(), plugin));
                        using var q = _labels.CreateCommand();
                        q.CommandText = "SELECT Stamp FROM Plugins WHERE Plugin = @p";
                        q.Parameters.AddWithValue("@p", plugin);
                        fresh = fi.Exists && q.ExecuteScalar() as string == $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
                        _freshPlugin[plugin] = fresh;
                    }
                    if (!fresh) return (false, null);
                    using var x = _labels.CreateCommand();
                    x.CommandText = "SELECT v.EditorID FROM VersionRows v JOIN Plugins p ON p.Id = v.PluginId WHERE v.FormKey = @k AND p.Plugin = @p";
                    x.Parameters.AddWithValue("@k", fk);
                    x.Parameters.AddWithValue("@p", plugin);
                    using var r = x.ExecuteReader();
                    if (!r.Read()) return (false, null);
                    return (true, r.IsDBNull(0) ? null : r.GetString(0));
                }
            }
            catch (SqliteException) { return (false, null); }
        }

        /// <summary>The plugins in Data that have this record, from the index when it's already up to date (null otherwise, or
        /// when the index doesn't list the record: placed references aren't in it). Never builds the index.</summary>
        public static List<string>? HoldersIfFresh(string fk)
        {
            string path = DbPath;
            if (!File.Exists(path)) return null;
            var cur = Listing();
            var byName = cur.Where(p => p.Stamp != "").ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            if (!UpToDate(path, cur, byName)) return null;
            using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            c.Open();
            using var x = c.CreateCommand();
            x.CommandText = "SELECT p.Plugin FROM VersionRows v JOIN Plugins p ON p.Id = v.PluginId WHERE v.FormKey = @k ORDER BY p.Plugin";
            x.Parameters.AddWithValue("@k", fk);
            var list = new List<string>();
            using (var r = x.ExecuteReader()) while (r.Read()) list.Add(r.GetString(0));
            // The game's own files list no placed references: the origin always holds its own record
            string origin = Sfs.PluginOf(fk);
            if (IsOfficial(origin) && byName.ContainsKey(origin) && !list.Contains(origin, StringComparer.OrdinalIgnoreCase)) list.Insert(0, byName[origin].Name);
            return list.Count == 0 ? null : list;
        }

        /// <summary>Load-order position of each plugin in Data (-1 when not loaded): for Conflicts.</summary>
        public static Dictionary<string, (int Pos, bool Enabled)> Positions() =>
            Listing().Where(p => p.Stamp != "").ToDictionary(p => p.Name, p => (p.Pos, p.Enabled), StringComparer.OrdinalIgnoreCase);
    }

    public static class LoadOrderCommand
    {
        public static void Run(List<string> args)
        {
            bool all = Sfs.TakeFlag(args, "--all");
            bool quick = Sfs.TakeFlag(args, "--quick");
            string? one = args.Count > 0 ? args[0] : null;
            var listing = LoadOrderIndex.Listing();
            var counts = new Dictionary<string, (long Rec, long New, long Ovr, long Mis, long NoBase)>(StringComparer.OrdinalIgnoreCase);
            if (!quick)
            {
                using var c = LoadOrderIndex.Open();
                using var x = c.CreateCommand();
                x.CommandText = "SELECT Plugin, coalesce(Records,0), coalesce(NewRecords,0), coalesce(Overrides,0), coalesce(TypeMismatches,0), coalesce(NoBase,0) FROM Plugins";
                using var r = x.ExecuteReader();
                while (r.Read()) counts[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5));
            }
            var byName = listing.Where(p => p.Stamp != "").ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            var t = quick ? new Sfs.Table("Pos", "Slot", "Plugin", "Size", "Loaded", "Notes") : new Sfs.Table("Pos", "Slot", "Plugin", "Size", "Loaded", "Records", "New", "Overrides", "Notes");
            int loaded = 0, shown = 0;
            foreach (var p in listing)
            {
                if (p.Enabled) loaded++;
                if (one != null && !p.Name.Equals(one, StringComparison.OrdinalIgnoreCase)) continue;
                if (one == null && !all && !p.Enabled) continue;
                var notes = new List<string>();
                if (p.Stamp == "") notes.Add("listed in plugins.txt but not in Data");
                var missing = p.Masters.Where(m => !byName.ContainsKey(m)).ToList();
                if (missing.Count > 0) notes.Add("missing master(s): " + string.Join(", ", missing));
                var off = p.Masters.Where(m => byName.TryGetValue(m, out var mi) && !mi.Enabled).ToList();
                if (p.Enabled && off.Count > 0) notes.Add("master(s) not loaded: " + string.Join(", ", off));
                var late = p.Masters.Where(m => byName.TryGetValue(m, out var mi) && mi.Enabled && mi.Pos > p.Pos).ToList();
                if (p.Enabled && late.Count > 0) notes.Add("master(s) load after it: " + string.Join(", ", late));
                if (counts.TryGetValue(p.Name, out var ct) && ct.Mis > 0) notes.Add($"{ct.Mis} override(s) of a different type than their base: built for an older version of a master? (trap 15)");
                string pos = p.Pos < 0 ? "-" : p.Pos.ToString();
                string en = p.Enabled ? "yes" : "no";
                if (quick) t.Add(pos, p.Slot, p.Name, p.Size, en, string.Join("; ", notes));
                else
                {
                    counts.TryGetValue(p.Name, out var cc);
                    t.Add(pos, p.Slot, p.Name, p.Size, en, cc.Rec, cc.New, cc.Ovr, string.Join("; ", notes));
                }
                shown++;
            }
            Sfs.Print(t, annotate: false);
            Sfs.Say($"\n{loaded} plugin(s) loaded, {listing.Count(p => p.Stamp != "")} in Data.{(all || one != null ? "" : " Add --all for the ones not loaded.")} Slot is the runtime prefix the game gives the plugin's own records (FE:09A → FormIDs FE09Axxx in the console).");
        }
    }
}
