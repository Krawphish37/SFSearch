using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;

namespace SFSearch
{
    /// <summary>
    /// Mod update history (2026-11-07). `Cache\History.db` keeps a snapshot of a plugin each time the load-order index finds
    /// it new or changed: size, date, flags, masters with their sizes at that moment, records per type, the override checks,
    /// and for mods (not the game's own files) the list of records. It's kept in every mode, so a mod update is never
    /// forgotten. `Changes` compares, and flags patches whose master changed size since they were built (trap 15).
    /// </summary>
    public sealed class History
    {
        public static string DbPath => Path.Combine(Sfs.HomeDir, "Cache", "History.db");

        sealed class Pending
        {
            public LoadOrderIndex.PluginInfo? P;
            public string Plugin = "";
            public bool Removed;
            public int Records, New, Overrides;
            public string Counts = "";
            public byte[]? List;
            public string? Problem;
            public long? Mismatch, NoBase;
        }

        readonly bool _firstIndex;
        readonly Dictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, (long M, long N)> _checks = new(StringComparer.OrdinalIgnoreCase);

        public History(bool firstIndex) { _firstIndex = firstIndex; }

        public void Gone(string plugin) => _pending[plugin] = new Pending { Plugin = plugin, Removed = true };

        public void Read(LoadOrderIndex.PluginInfo p, List<(string Fk, string Sig4, string? Edid, bool Ovr)>? rows, string? problem)
        {
            var e = new Pending { P = p, Plugin = p.Name, Problem = problem };
            if (rows != null)
            {
                e.Records = rows.Count;
                e.Overrides = rows.Count(r => r.Ovr);
                e.New = e.Records - e.Overrides;
                e.Counts = string.Join(", ", rows.GroupBy(r => r.Sig4).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"));
                if (!LoadOrderIndex.IsOfficial(p.Name))
                {
                    var sb = new StringBuilder();
                    foreach (var r in rows) sb.Append(r.Fk).Append('|').Append(r.Sig4).Append('|').Append(r.Edid).Append('|').Append(r.Ovr ? '1' : '0').Append('\n');
                    using var ms = new MemoryStream();
                    using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(Encoding.UTF8.GetBytes(sb.ToString()));
                    e.List = ms.ToArray();
                }
            }
            _pending[p.Name] = e;
        }

        public void Checked(string plugin, string stamp, long mismatch, long noBase)
        {
            _checks[plugin] = (mismatch, noBase);
            if (_pending.TryGetValue(plugin, out var e)) { e.Mismatch = mismatch; e.NoBase = noBase; }
        }

        static SqliteConnection OpenRw()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
            var c = new SqliteConnection($"Data Source={DbPath};Pooling=False");
            c.Open();
            using var x = c.CreateCommand();
            x.CommandText = @"PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 60000;
                CREATE TABLE IF NOT EXISTS Snapshots (Id INTEGER PRIMARY KEY, Plugin TEXT, Seen TEXT, Stamp TEXT, Length INTEGER, Modified TEXT, Size TEXT,
                    Masters TEXT, MasterSizes TEXT, Records INTEGER, NewRecords INTEGER, Overrides INTEGER, Counts TEXT, TypeMismatches INTEGER, NoBase INTEGER,
                    List BLOB, Problem TEXT, Initial INTEGER, Removed INTEGER);
                CREATE INDEX IF NOT EXISTS idx_snap_plugin ON Snapshots(Plugin, Id);";
            x.ExecuteNonQuery();
            return c;
        }

        public void Save(Dictionary<string, LoadOrderIndex.PluginInfo> byName)
        {
            if (_pending.Count == 0 && _checks.Count == 0) return;
            using var lk = new Service.ServiceDb.BuildLock(DbPath);
            using var c = OpenRw();
            using var tx = c.BeginTransaction();
            foreach (var e in _pending.Values)
            {
                // The last snapshot of this plugin: skip a repeat of the same copy (the index was rebuilt, nothing changed)
                string? lastStamp = null, lastSizes = null; bool any = false; bool lastRemoved = false;
                using (var q = c.CreateCommand())
                {
                    q.Transaction = tx;
                    q.CommandText = "SELECT Stamp, MasterSizes, Removed FROM Snapshots WHERE Plugin = @p ORDER BY Id DESC LIMIT 1";
                    q.Parameters.AddWithValue("@p", e.Plugin);
                    using var r = q.ExecuteReader();
                    if (r.Read()) { any = true; lastStamp = r.IsDBNull(0) ? null : r.GetString(0); lastSizes = r.IsDBNull(1) ? null : r.GetString(1); lastRemoved = !r.IsDBNull(2) && r.GetInt32(2) == 1; }
                }
                if (e.Removed)
                {
                    if (!any || lastRemoved) continue;
                    Insert(c, tx, e.Plugin, ("@removed", 1), ("@initial", 0));
                    continue;
                }
                var p = e.P!;
                string sizes = string.Join(",", p.Masters.Select(m => $"{m}={(byName.TryGetValue(m, out var mi) ? mi.Size : "missing")}"));
                if (any && !lastRemoved && lastStamp == p.Stamp && lastSizes == sizes)
                {
                    if (e.Mismatch != null) UpdateChecks(c, tx, e.Plugin, e.Mismatch.Value, e.NoBase ?? 0);
                    continue;
                }
                Insert(c, tx, e.Plugin,
                    ("@stamp", p.Stamp), ("@len", p.Length), ("@mod", p.Mtime.ToLocalTime().ToString("yyyy-MM-dd HH:mm")), ("@size", p.Size),
                    ("@masters", string.Join(", ", p.Masters)), ("@sizes", sizes), ("@rec", e.Records), ("@new", e.New), ("@ovr", e.Overrides), ("@counts", e.Counts),
                    ("@mis", (object?)e.Mismatch ?? DBNull.Value), ("@nobase", (object?)e.NoBase ?? DBNull.Value), ("@list", (object?)e.List ?? DBNull.Value),
                    ("@problem", (object?)e.Problem ?? DBNull.Value), ("@initial", _firstIndex && !any ? 1 : 0), ("@removed", 0));
            }
            foreach (var (plugin, (m, n)) in _checks) if (!_pending.ContainsKey(plugin)) UpdateChecks(c, tx, plugin, m, n);
            tx.Commit();
            lock (_warned) _risk.Clear();
        }

        static void Insert(SqliteConnection c, SqliteTransaction tx, string plugin, params (string, object?)[] values)
        {
            var cols = new Dictionary<string, string> { ["@stamp"] = "Stamp", ["@len"] = "Length", ["@mod"] = "Modified", ["@size"] = "Size", ["@masters"] = "Masters", ["@sizes"] = "MasterSizes",
                ["@rec"] = "Records", ["@new"] = "NewRecords", ["@ovr"] = "Overrides", ["@counts"] = "Counts", ["@mis"] = "TypeMismatches", ["@nobase"] = "NoBase", ["@list"] = "List",
                ["@problem"] = "Problem", ["@initial"] = "Initial", ["@removed"] = "Removed" };
            using var x = c.CreateCommand(); x.Transaction = tx;
            x.CommandText = $"INSERT INTO Snapshots (Plugin, Seen{string.Concat(values.Select(v => ", " + cols[v.Item1]))}) VALUES (@plugin, @seen{string.Concat(values.Select(v => ", " + v.Item1))})";
            x.Parameters.AddWithValue("@plugin", plugin);
            x.Parameters.AddWithValue("@seen", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            foreach (var (n, v) in values) x.Parameters.AddWithValue(n, v ?? DBNull.Value);
            x.ExecuteNonQuery();
        }

        static void UpdateChecks(SqliteConnection c, SqliteTransaction tx, string plugin, long mismatch, long noBase)
        {
            using var x = c.CreateCommand(); x.Transaction = tx;
            x.CommandText = "UPDATE Snapshots SET TypeMismatches = @m, NoBase = @n WHERE Id = (SELECT max(Id) FROM Snapshots WHERE Plugin = @p)";
            x.Parameters.AddWithValue("@p", plugin); x.Parameters.AddWithValue("@m", mismatch); x.Parameters.AddWithValue("@n", noBase);
            x.ExecuteNonQuery();
        }

        // ---------------------------------------------------------------- The warning on every read
        static readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, (string Stamp, long Mismatch)> _risk = new(StringComparer.OrdinalIgnoreCase);

        public static void ResetForCommand() { lock (_warned) { _warned.Clear(); _risk.Clear(); } }

        /// <summary>Says once per command when a plugin's overrides don't match the types of the records they override (from the
        /// last check of this exact copy; nothing is said before the load-order index has checked it).</summary>
        public static void Warn(string plugin)
        {
            try
            {
                string path = Path.Combine(Program.GetDataFolderPathPublic(), plugin);
                if (!File.Exists(path) || !File.Exists(DbPath)) return;
                var fi = new FileInfo(path);
                string stamp = $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
                long mismatch;
                lock (_warned)
                {
                    if (_warned.Contains(plugin)) return;
                    if (!_risk.TryGetValue(plugin, out var r) || r.Stamp != stamp)
                    {
                        using var c = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Pooling=False");
                        c.Open();
                        using var x = c.CreateCommand();
                        x.CommandText = "SELECT coalesce(TypeMismatches, 0) FROM Snapshots WHERE Plugin = @p AND Stamp = @s ORDER BY Id DESC LIMIT 1";
                        x.Parameters.AddWithValue("@p", plugin); x.Parameters.AddWithValue("@s", stamp);
                        r = (stamp, Convert.ToInt64(x.ExecuteScalar() ?? 0L));
                        _risk[plugin] = r;
                    }
                    mismatch = r.Mismatch;
                    if (mismatch <= 0) return;
                    _warned.Add(plugin);
                }
                Console.Error.WriteLine($"[sfs] WARNING: {plugin} has {mismatch} override(s) whose type differs from the record they override. It was probably built for an older version of a master that has since changed size (Full ↔ Light), so its FormIDs now point at the wrong records (trap 15). 'Changes {plugin}' shows what changed.");
            }
            catch (SqliteException) { }
            catch (IOException) { }
        }

        // ---------------------------------------------------------------- Changes
        public static void Command(List<string> args)
        {
            string? since = Sfs.TakeOption(args, "--since");
            bool all = Sfs.TakeFlag(args, "--all");
            string? plugin = args.Count > 0 ? args[0] : null;
            using (var lo = LoadOrderIndex.Open()) { } // brings the index and the history up to date
            if (!File.Exists(DbPath)) { Console.WriteLine("No history yet."); return; }
            using var c = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Pooling=False");
            c.Open();
            if (plugin != null) { One(c, plugin); return; }

            var rows = new List<Snap>();
            using (var x = c.CreateCommand())
            {
                x.CommandText = "SELECT * FROM Snapshots ORDER BY Plugin, Id";
                using var r = x.ExecuteReader();
                while (r.Read()) rows.Add(Snap.From(r));
            }
            var byPlugin = rows.GroupBy(s => s.Plugin, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            DateTime from = since != null && DateTime.TryParse(since, out var d) ? d : all ? DateTime.MinValue : DateTime.Now.AddDays(-30);
            var first = rows.Count == 0 ? DateTime.Now : rows.Min(s => s.Seen);
            Sfs.Say($"### Plugin changes since {(from == DateTime.MinValue ? "history began" : from.ToString("yyyy-MM-dd"))} (history began {first:yyyy-MM-dd HH:mm})\n");
            var t = new Sfs.Table("When", "Plugin", "What changed");
            foreach (var (name, snaps) in byPlugin.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                for (int i = 0; i < snaps.Count; i++)
                {
                    var s = snaps[i];
                    if (s.Initial || s.Seen < from) continue;
                    var prev = i > 0 ? snaps[i - 1] : null;
                    t.Add(s.Seen.ToString("yyyy-MM-dd HH:mm"), name, Describe(prev, s));
                }
            if (t.Rows.Count == 0) Sfs.Say("No plugin was added, removed or changed in that time.");
            else Sfs.Print(t, annotate: false);

            // Patches at risk: overrides that don't match their base's type now, and plugins whose master changed size while they didn't
            var risk = new Sfs.Table("Plugin", "Why");
            foreach (var (name, snaps) in byPlugin)
            {
                var last = snaps[^1];
                if (last.Removed) continue;
                var why = new List<string>();
                if (last.Mismatch > 0) why.Add($"{last.Mismatch} override(s) of a different type than the record they override");
                var prevSame = snaps.Take(snaps.Count - 1).LastOrDefault(s => !s.Removed && s.Stamp == last.Stamp);
                if (prevSame != null && prevSame.MasterSizes != last.MasterSizes)
                    why.Add($"master size changed since it was built: {SizeChange(prevSame.MasterSizes, last.MasterSizes)}");
                if (why.Count > 0) risk.Add(name, string.Join("; ", why));
            }
            Sfs.Say("\n### Patches at risk (trap 15)\n");
            if (risk.Rows.Count == 0) Sfs.Say("None: every plugin's overrides match the types of the records they override.");
            else
            {
                Sfs.Print(risk, annotate: false);
                Sfs.Say("\nA plugin's FormIDs count its masters by size, so a master that turned Light (or back) after the plugin was built moves its records onto the wrong plugin. Don't trust or copy these until rebuilt.");
            }
        }

        static void One(SqliteConnection c, string plugin)
        {
            var snaps = new List<Snap>();
            using (var x = c.CreateCommand())
            {
                x.CommandText = "SELECT * FROM Snapshots WHERE Plugin = @p COLLATE NOCASE ORDER BY Id";
                x.Parameters.AddWithValue("@p", plugin);
                using var r = x.ExecuteReader();
                while (r.Read()) snaps.Add(Snap.From(r));
            }
            if (snaps.Count == 0) { Console.WriteLine($"No history for {plugin}."); return; }
            Sfs.Say($"### History of {snaps[0].Plugin}\n");
            var t = new Sfs.Table("Seen", "File date", "Size", "Records", "New", "Overrides", "Type mismatches", "What changed");
            for (int i = 0; i < snaps.Count; i++)
            {
                var s = snaps[i];
                t.Add(s.Seen.ToString("yyyy-MM-dd HH:mm"), s.Modified, s.Removed ? "removed" : s.Size, s.Records, s.New, s.Overrides, s.Mismatch, s.Initial ? "(first seen)" : Describe(i > 0 ? snaps[i - 1] : null, s));
            }
            Sfs.Print(t, annotate: false);
            var lastTwo = snaps.Where(s => s.List != null).TakeLast(2).ToList();
            if (lastTwo.Count == 2)
            {
                var a = ReadList(lastTwo[0].List!); var b = ReadList(lastTwo[1].List!);
                var added = b.Keys.Except(a.Keys).ToList();
                var removed = a.Keys.Except(b.Keys).ToList();
                var retyped = a.Keys.Intersect(b.Keys).Where(k => a[k].Sig != b[k].Sig).ToList();
                Sfs.Say($"\n### Records: {lastTwo[0].Seen:yyyy-MM-dd} → {lastTwo[1].Seen:yyyy-MM-dd}: {added.Count} added, {removed.Count} removed, {retyped.Count} changed type\n");
                var d = new Sfs.Table("Change", "Record", "Type");
                foreach (var k in removed.Take(200)) d.Add("removed", Label(k, a[k].Edid), a[k].Sig);
                foreach (var k in added.Take(200)) d.Add("added", Label(k, b[k].Edid), b[k].Sig);
                foreach (var k in retyped.Take(200)) d.Add("type", Label(k, b[k].Edid), $"{a[k].Sig} → {b[k].Sig}");
                if (d.Rows.Count > 0) Sfs.Print(d, annotate: false);
                if (added.Count + removed.Count + retyped.Count > d.Rows.Count) Sfs.Say($"(first 200 of each shown)");
            }
        }

        static string Label(string fk, string? edid) => string.IsNullOrEmpty(edid) ? $"[no EditorID] ({fk})" : $"{edid} ({fk})";

        static Dictionary<string, (string Sig, string? Edid)> ReadList(byte[] blob)
        {
            using var gz = new GZipStream(new MemoryStream(blob), CompressionMode.Decompress);
            using var sr = new StreamReader(gz, Encoding.UTF8);
            var d = new Dictionary<string, (string, string?)>(StringComparer.OrdinalIgnoreCase);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                var p = line.Split('|');
                if (p.Length >= 3) d[p[0]] = (p[1], p[2].Length == 0 ? null : p[2]);
            }
            return d;
        }

        static string SizeChange(string? before, string? after)
        {
            var a = (before ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=')).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase);
            var b = (after ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=')).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase);
            return string.Join(", ", b.Where(kv => a.TryGetValue(kv.Key, out var old) && old != kv.Value).Select(kv => $"{kv.Key} {a[kv.Key]} → {kv.Value}")) is { Length: > 0 } s ? s : "masters changed";
        }

        static string Describe(Snap? prev, Snap s)
        {
            if (s.Removed) return "removed from Data";
            if (prev == null || prev.Removed) return $"{(prev == null ? "new" : "back")} in Data: {s.Size}, {s.Records} records ({s.New} new, {s.Overrides} overrides)";
            var parts = new List<string>();
            if (prev.Stamp != s.Stamp) parts.Add($"file updated ({prev.Modified} → {s.Modified})");
            if (prev.Size != s.Size) parts.Add($"size {prev.Size} → {s.Size}");
            if (prev.Records != s.Records || prev.New != s.New || prev.Overrides != s.Overrides)
                parts.Add($"records {prev.Records} → {s.Records} (new {prev.New} → {s.New}, overrides {prev.Overrides} → {s.Overrides})");
            if (prev.Masters != s.Masters) parts.Add($"masters now: {s.Masters}");
            else if (prev.MasterSizes != s.MasterSizes) parts.Add($"a master changed size: {SizeChange(prev.MasterSizes, s.MasterSizes)}");
            if (s.Mismatch > 0 && prev.Mismatch == 0) parts.Add($"now {s.Mismatch} override(s) of a different type than their base");
            return parts.Count == 0 ? "re-read (no visible change)" : string.Join("; ", parts);
        }

        sealed class Snap
        {
            public string Plugin = "", Stamp = "", Size = "", Masters = "", MasterSizes = "", Modified = "";
            public DateTime Seen;
            public long Records, New, Overrides, Mismatch;
            public bool Initial, Removed;
            public byte[]? List;

            public static Snap From(SqliteDataReader r)
            {
                string S(string col) { int i = r.GetOrdinal(col); return r.IsDBNull(i) ? "" : r.GetValue(i).ToString() ?? ""; }
                long L(string col) { int i = r.GetOrdinal(col); return r.IsDBNull(i) ? 0 : r.GetInt64(i); }
                int li = r.GetOrdinal("List");
                return new Snap
                {
                    Plugin = S("Plugin"), Stamp = S("Stamp"), Size = S("Size"), Masters = S("Masters"), MasterSizes = S("MasterSizes"), Modified = S("Modified"),
                    Seen = DateTime.TryParse(S("Seen"), out var d) ? d : DateTime.MinValue,
                    Records = L("Records"), New = L("NewRecords"), Overrides = L("Overrides"), Mismatch = L("TypeMismatches"),
                    Initial = L("Initial") == 1, Removed = L("Removed") == 1,
                    List = r.IsDBNull(li) ? null : (byte[])r.GetValue(li),
                };
            }
        }
    }
}
