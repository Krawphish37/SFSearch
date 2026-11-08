using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SFSearch
{
    public enum OutputFormat { Markdown, Json, Csv }

    /// <summary>
    /// Shared infrastructure: home directory pinning, global CLI options, FormKey helpers,
    /// cache database access, EditorID resolution and table rendering.
    /// </summary>
    public static class Sfs
    {
        // ---------------------------------------------------------------- Paths
        /// <summary>The caller's working directory (captured before we pin CWD to the SFSearch home).</summary>
        public static string CallerCwd { get; private set; } = Directory.GetCurrentDirectory();

        /// <summary>Directory containing SFSearch.csproj (and the Cache folder).</summary>
        public static string HomeDir { get; private set; } = Directory.GetCurrentDirectory();

        /// <summary>
        /// Pins the process working directory to the SFSearch project folder so the relative "Cache" folder
        /// resolves no matter where SFSearch is invoked from (bin\Release, repo root, patcher folder, ...).
        /// </summary>
        public static void PinHomeDirectory()
        {
            CallerCwd = Directory.GetCurrentDirectory();
            string? home = Environment.GetEnvironmentVariable("SFSEARCH_HOME");
            if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home))
            {
                home = null;
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "SFSearch.csproj"))) { home = dir.FullName; break; }
                    dir = dir.Parent;
                }
            }
            HomeDir = home ?? CallerCwd;
            Directory.SetCurrentDirectory(HomeDir);
        }

        /// <summary>Resolves a user-supplied (possibly relative) file path against the caller's original CWD.</summary>
        public static string ResolveUserPath(string path) =>
            Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(CallerCwd, path));

        // ---------------------------------------------------------------- Global options
        public static OutputFormat Format { get; set; } = OutputFormat.Markdown;
        public static int MaxWidth { get; set; } = 0;          // 0 = unlimited
        public static bool ResolveNames { get; set; } = true;  // annotate FormKeys with EditorIDs (markdown only)

        /// <summary>Removes global options from the arg list and applies them. Returns remaining args.</summary>
        public static List<string> ParseGlobalOptions(List<string> args)
        {
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--format" && i + 1 < args.Count)
                {
                    Format = args[i + 1].ToLowerInvariant() switch { "json" => OutputFormat.Json, "csv" => OutputFormat.Csv, _ => OutputFormat.Markdown };
                    args.RemoveRange(i, 2); i--;
                }
                else if (a == "--json") { Format = OutputFormat.Json; args.RemoveAt(i); i--; }
                else if (a == "--csv") { Format = OutputFormat.Csv; args.RemoveAt(i); i--; }
                else if (a == "--maxwidth" && i + 1 < args.Count && int.TryParse(args[i + 1], out int w))
                {
                    MaxWidth = Math.Max(0, w); args.RemoveRange(i, 2); i--;
                }
                else if (a == "--raw") { ResolveNames = false; args.RemoveAt(i); i--; }
            }
            return args;
        }

        /// <summary>Extracts "--name value" from args (removing both). Returns null if absent.</summary>
        public static string? TakeOption(List<string> args, string name)
        {
            int i = args.FindIndex(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= args.Count) return null;
            string v = args[i + 1];
            args.RemoveRange(i, 2);
            return v;
        }

        /// <summary>Extracts a boolean "--flag" from args (removing it).</summary>
        public static bool TakeFlag(List<string> args, string name) =>
            args.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;

        // ---------------------------------------------------------------- FormKeys
        static readonly Regex ExactFormKey = new(@"^[0-9A-Fa-f]{6}:.+\.(esm|esp|esl)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        /// <summary>FormKey occurrences inside arbitrary text (plugin names may contain spaces).</summary>
        public static readonly Regex EmbeddedFormKey = new(@"(?<![0-9A-Fa-f])[0-9A-Fa-f]{6}:[^""<>|,\[\]{}\r\n]+?\.(?:esm|esp|esl)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool IsFormKey(string s) => ExactFormKey.IsMatch(s.Trim());

        /// <summary>Normalizes "0x000CF2:DarkStar.esm" / "CF2:darkstar.esm" to "000CF2:DarkStar.esm".</summary>
        public static string NormalizeFormKey(string s)
        {
            s = s.Trim();
            int colon = s.IndexOf(':');
            if (colon <= 0) return s;
            string id = s.Substring(0, colon).Replace("0x", "", StringComparison.OrdinalIgnoreCase);
            if (id.Length < 6) id = id.PadLeft(6, '0');
            return id.ToUpperInvariant() + ":" + CanonicalPluginName(s.Substring(colon + 1));
        }

        static Dictionary<string, string>? _dataPluginNames;

        /// <summary>
        /// A plugin name spelled as its file in the Data folder ("starfield.esm" → "Starfield.esm"); a name not in Data is returned as is.
        /// Mutagen keeps the master names as a plugin's header spells them, and SFBGS050.esm's header has "starfield.esm" / "sfbgs00d.esm",
        /// so without this its cache stored other FormKeys than every other cache (2026-10-27).
        /// </summary>
        public static string CanonicalPluginName(string plugin)
        {
            if (_dataPluginNames == null)
            {
                var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var f in Directory.EnumerateFiles(Program.GetDataFolderPathPublic()))
                    {
                        string n = Path.GetFileName(f);
                        string ext = Path.GetExtension(n).ToLowerInvariant();
                        if (ext is ".esm" or ".esp" or ".esl") names[n] = n;
                    }
                }
                catch { }
                _dataPluginNames = names;
            }
            return _dataPluginNames.TryGetValue(plugin, out var canonical) ? canonical : plugin;
        }

        /// <summary>Accepts "000CF2:darkstar.esm" or bare hex "CF2" (interpreted in defaultPlugin).</summary>
        public static bool TryParseKey(string input, string? defaultPlugin, out string formKey)
        {
            formKey = "";
            if (IsFormKey(input) || (input.Contains(':') && input.Split(':')[1].Contains('.'))) { formKey = NormalizeFormKey(input); return true; }
            if (defaultPlugin != null && Regex.IsMatch(input, @"^(0x)?[0-9A-Fa-f]{1,6}$"))
            {
                formKey = NormalizeFormKey(input + ":" + defaultPlugin);
                return true;
            }
            return false;
        }

        public static string PluginOf(string formKey)
        {
            int c = formKey.IndexOf(':');
            return c < 0 ? "" : formKey.Substring(c + 1);
        }

        public static uint LocalIdOf(string formKey)
        {
            int c = formKey.IndexOf(':');
            return c < 0 ? 0 : Convert.ToUInt32(formKey.Substring(0, c), 16);
        }

        /// <summary>
        /// Case-insensitive FormKey match that still uses the column's index. A plain "col = @k COLLATE NOCASE" can't use the
        /// (case-sensitive) primary-key index and scans the whole table: about a second per lookup on the 1.2 GB Starfield.esm cache.
        /// Cached FormKeys always store the 6-digit hex ID in upper case, so every case-insensitive match starts with
        /// "UPPERHEX:" and lies in the index range ["UPPERHEX:", "UPPERHEX;"). Same rows as the old NOCASE match, found in a few
        /// index steps (hits and misses alike). Bind the parameters with <see cref="AddFormKeyParams"/>.
        /// </summary>
        public static string FormKeyMatch(string column, string param = "@k") =>
            $"{column} >= {param}lo AND {column} < {param}hi AND {column} = {param} COLLATE NOCASE";

        public static void AddFormKeyParams(SqliteCommand cmd, string formKey, string param = "@k")
        {
            int c = formKey.IndexOf(':');
            string id = (c < 0 ? formKey : formKey.Substring(0, c)).ToUpperInvariant();
            cmd.Parameters.AddWithValue(param, formKey);
            cmd.Parameters.AddWithValue(param + "lo", id + ":");
            cmd.Parameters.AddWithValue(param + "hi", id + ";");
        }

        // ---------------------------------------------------------------- Cache DB access
        /// <summary>Where databases live: Cache normally; a folder of their own for --live and the agent session.</summary>
        public static string DbFolder { get; set; } = "Cache";

        public static string CachePath(string plugin) => Path.Combine(HomeDir, DbFolder, DbFileName(plugin));

        /// <summary>A database's file name: `plugin.db`, or in the service `plugin@STAMP.db`, named after the exact copy of the plugin it was read from.</summary>
        public static string DbFileName(string plugin) => SessionServer.Active ? Service.ServiceDb.FileName(plugin) : plugin + ".db";

        public static bool HasCache(string plugin) => File.Exists(CachePath(plugin));

        /// <summary>True when the plugin has a database, building it first when the plugin is in Data. For lookups into another
        /// plugin (a FormList's items, an armor's addons): without it the answer depended on which databases happened to exist.</summary>
        public static bool HasCacheOrBuild(string plugin)
        {
            if (HasCache(plugin)) return true;
            if (string.IsNullOrEmpty(plugin) || !File.Exists(Path.Combine(Program.GetDataFolderPathPublic(), plugin))) return false;
            Program.EnsureCacheUpToDatePublic(plugin);
            return HasCache(plugin);
        }

        /// <summary>Forgets what this process has looked up (the session does this when a plugin changes on disk).</summary>
        public static void ResetCaches()
        {
            lock (_edidCache) _edidCache.Clear();
            _hasForms.Clear();
            lock (_fileEditorIds) if (_fileEditorIds.Count > 40) _fileEditorIds.Clear(); // keyed by the file's size and date, so kept between commands
            _mastering = null;
            lock (_warnedMissingMasters) _warnedMissingMasters.Clear();
            lock (_nameCache) _nameCache.Clear();
            History.ResetForCommand();
            LoadOrderIndex.ResetForCommand();
            CloseResolver();
        }

        static Dictionary<string, List<string>>? _mastering;

        /// <summary>Every plugin in Data that lists this plugin as a master, plus the plugin itself: the plugins that can point at
        /// or override its records. Read from the plugin headers only, once per run.</summary>
        public static List<string> PluginsThatCanHold(string plugin)
        {
            if (_mastering == null)
            {
                _mastering = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.EnumerateFiles(Program.GetDataFolderPathPublic()))
                {
                    string name = Path.GetFileName(f);
                    if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"\.es[mpl]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                    try
                    {
                        using var fs = File.OpenRead(f);
                        var head = new byte[24];
                        if (fs.Read(head, 0, 24) < 24 || System.Text.Encoding.ASCII.GetString(head, 0, 4) != "TES4") continue;
                        var data = new byte[BitConverter.ToInt32(head, 4)];
                        fs.ReadExactly(data);
                        for (int pos = 0; pos + 6 <= data.Length;)
                        {
                            string sig = System.Text.Encoding.ASCII.GetString(data, pos, 4);
                            int len = BitConverter.ToUInt16(data, pos + 4);
                            if (sig == "MAST" && len > 1)
                            {
                                string master = System.Text.Encoding.UTF8.GetString(data, pos + 6, len - 1);
                                if (!_mastering.TryGetValue(master, out var list)) _mastering[master] = list = new List<string>();
                                list.Add(name);
                            }
                            pos += 6 + len;
                        }
                    }
                    catch (IOException) { }
                }
            }
            var result = new List<string>();
            if (File.Exists(Path.Combine(Program.GetDataFolderPathPublic(), plugin))) result.Add(CanonicalPluginName(plugin));
            if (_mastering.TryGetValue(plugin, out var users)) result.AddRange(users.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            return result;
        }

        static readonly Dictionary<string, Dictionary<string, string>?> _fileEditorIds = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>EditorIDs of a plugin's own records straight from its file (one pass, kept for the run): labels and
        /// EditorID lookups for a plugin that has no database.</summary>
        static Dictionary<string, string>? FileEditorIds(string plugin)
        {
            lock (_fileEditorIds)
            {
                string path = Path.Combine(Program.GetDataFolderPathPublic(), plugin);
                var info = new FileInfo(path);
                string slot = info.Exists ? $"{plugin}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : plugin;
                if (_fileEditorIds.TryGetValue(slot, out var map)) return map;
                if (!info.Exists) return _fileEditorIds[slot] = null;
                var key = Mutagen.Bethesda.Plugins.ModKey.FromFileName(Path.GetFileName(path));
                var masters = HeaderScanner.MasterTable(key, path);
                map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                List<(uint Raw, string EditorId)> found;
                try { found = HeaderScanner.EditorIds(path); }
                catch (Exception e) when (e is IOException or InvalidDataException) { return _fileEditorIds[slot] = null; }
                foreach (var (raw, edid) in found)
                {
                    var fk = masters.GetFormKey(Mutagen.Bethesda.Plugins.FormID.Factory(raw), reference: false);
                    if (fk.ModKey == key) map[NormalizeFormKey(fk.ToString())] = edid;
                }
                return _fileEditorIds[slot] = map;
            }
        }

        /// <summary>Opens a cache read-only (no pooling so files can be rebuilt/deleted afterwards).</summary>
        public static SqliteConnection OpenCache(string plugin, bool ensureUpToDate = true)
        {
            if (SessionServer.Active && !ensureUpToDate) Program.EnsureCacheUpToDatePublic(plugin);
            if (ensureUpToDate || Program.IsCacheStale(plugin)) Program.EnsureCacheUpToDatePublic(plugin);
            if (!File.Exists(CachePath(plugin)))
                throw new FileNotFoundException($"{plugin} isn't in the Data folder or at that path, and has no database.");
            var conn = new SqliteConnection($"Data Source={CachePath(plugin)};Mode=ReadOnly;Pooling=False");
            conn.Open();
            WarnMissingMasters(plugin, Meta(conn, "MissingMasters"));
            History.Warn(plugin);
            return conn;
        }

        static readonly HashSet<string> _warnedMissingMasters = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Mutagen numbers FormIDs by each master's size, and a missing file has none. Says so once per plugin, on every command that opens its database.</summary>
        public static void WarnMissingMasters(string plugin, string? missing)
        {
            if (missing == null || !_warnedMissingMasters.Add(plugin)) return;
            Console.Error.WriteLine($"[sfs] WARNING: {plugin} needs master(s) that aren't in Data: {missing}. SFSearch can't tell their size, and a missing Small or Medium master can put this plugin's FormIDs and links on the wrong plugin. Put the master in Data before trusting these results.");
        }

        /// <summary>All plugins that currently have a cache database.</summary>
        public static List<string> CachedPlugins()
        {
            string dir = Path.Combine(HomeDir, DbFolder);
            if (!Directory.Exists(dir)) return new List<string>();
            if (SessionServer.Active) // only databases of the plugin copies in Data now
                return Directory.GetFiles(dir, "*.db").Where(f => Path.GetFileName(f).Equals(DbFileName(Service.ServiceDb.PluginOf(f)), StringComparison.OrdinalIgnoreCase))
                    .Select(Service.ServiceDb.PluginOf).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            return Directory.GetFiles(dir, "*.db")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Where(n => n.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static bool TableExists(SqliteConnection conn, string table)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name=@t";
            cmd.Parameters.AddWithValue("@t", table);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }

        public static string? Meta(SqliteConnection conn, string key)
        {
            if (!TableExists(conn, "Meta")) return null;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Meta WHERE Key=@k";
            cmd.Parameters.AddWithValue("@k", key);
            return cmd.ExecuteScalar()?.ToString();
        }

        /// <summary>Ordered master list from a cache.</summary>
        public static List<string> Masters(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            bool hasIdx = false;
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(Masters)";
                using var r = pragma.ExecuteReader();
                while (r.Read()) if (r.GetString(1) == "Idx") hasIdx = true;
            }
            cmd.CommandText = hasIdx ? "SELECT MasterName FROM Masters ORDER BY Idx" : "SELECT MasterName FROM Masters ORDER BY rowid";
            var list = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(reader.GetString(0));
            return list;
        }

        public class RecordRow
        {
            public string Plugin = "";
            public string FormKey = "";
            public string Signature = "";
            public string EditorID = "";
            public string Name = "";
            public JsonObject Data = new();
            /// <summary>The cache's IsOverride column; only filled by readers that select it (Lint, Profile, saved queries).</summary>
            public bool IsOverride;
        }

        static RecordRow ReadRow(string plugin, SqliteDataReader r) => new RecordRow
        {
            Plugin = plugin,
            FormKey = r.GetString(0),
            Signature = r.GetString(1),
            EditorID = r.IsDBNull(2) ? "" : r.GetString(2),
            Name = r.IsDBNull(3) ? "" : r.GetString(3),
            Data = (JsonNode.Parse(r.GetString(4)) as JsonObject) ?? new JsonObject()
        };

        /// <summary>Finds a record in one plugin's cache by FormKey or EditorID (optionally filtered by signature, "*" = any).</summary>
        public static RecordRow? FindRecord(string plugin, string key, string signature = "*", bool ensureUpToDate = true)
        {
            if (!HasCache(plugin) && !ensureUpToDate) return null;
            if (ensureUpToDate && _ensuredPlugins.Add(plugin)) Program.EnsureCacheUpToDatePublic(plugin);
            if (!HasCache(plugin))
            {
                using var fresh = OpenCache(plugin, false); // no cache even after the check: fail the same way as before
                return FindRecord(fresh, plugin, key, signature);
            }
            var conn = ResolverConn(plugin);
            return conn == null ? null : FindRecord(conn, plugin, key, signature);
        }

        public static RecordRow? FindRecord(SqliteConnection conn, string plugin, string key, string signature = "*")
        {
            using var cmd = conn.CreateCommand();
            string sigClause = signature == "*" ? "" : " AND Signature = @sig";
            if (TryParseKey(key, null, out string fk))
            {
                cmd.CommandText = $"SELECT FormKey, Signature, EditorID, Name, Data FROM Records WHERE {FormKeyMatch("FormKey")}{sigClause} LIMIT 1";
                AddFormKeyParams(cmd, fk);
            }
            else
            {
                cmd.CommandText = $"SELECT FormKey, Signature, EditorID, Name, Data FROM Records WHERE EditorID = @k{sigClause} LIMIT 1";
                cmd.Parameters.AddWithValue("@k", key);
            }
            if (signature != "*") cmd.Parameters.AddWithValue("@sig", signature);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadRow(plugin, r) : null;
        }

        static readonly List<string> _unreadableMasters = new();

        /// <summary>
        /// Builds or refreshes a master's cache (once per run) before records are looked up in it. False when the plugin has no
        /// cache and isn't in Data; such plugins are listed in <see cref="UnreadableMasters"/> so a report can say so.
        /// </summary>
        public static bool EnsureMasterCache(string plugin)
        {
            if (plugin.Length == 0) return false;
            if (_ensuredPlugins.Add(plugin))
            {
                Program.EnsureCacheUpToDatePublic(plugin);
                if (!HasCache(plugin)) _unreadableMasters.Add(plugin);
            }
            return HasCache(plugin);
        }

        /// <summary>Plugins <see cref="EnsureMasterCache"/> couldn't read this run.</summary>
        public static IReadOnlyList<string> UnreadableMasters => _unreadableMasters;

        /// <summary>Resolves an EditorID to FormKeys by searching the given plugins' caches (no rebuilds).</summary>
        public static List<(string Plugin, string FormKey)> LookupEditorId(string editorId, IEnumerable<string> plugins)
        {
            var hits = new List<(string, string)>();
            foreach (var p in plugins)
            {
                if (!HasCache(p)) continue;
                try
                {
                    // Opened as-is: this may scan every cache, and a freshness check here could rebuild them all in one go.
                    using var conn = new SqliteConnection($"Data Source={CachePath(p)};Mode=ReadOnly;Pooling=False");
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT FormKey FROM Records WHERE EditorID = @e";
                    cmd.Parameters.AddWithValue("@e", editorId);
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) hits.Add((p, r.GetString(0)));
                }
                catch { }
            }
            return hits;
        }

        /// <summary>
        /// Turns a user key (FormKey or EditorID) into a FormKey. EditorIDs are searched in the preferred plugins first,
        /// then in all caches. Prefers the defining (origin) record over overrides.
        /// </summary>
        public static string? ResolveUserKey(string key, IEnumerable<string>? preferredPlugins = null)
        {
            if (TryParseKey(key, null, out string fk)) return fk;
            var pref = preferredPlugins?.ToList() ?? new List<string>();
            var hits = LookupEditorId(key, pref);
            if (hits.Count == 0) hits = LookupEditorId(key, CachedPlugins().Except(pref, StringComparer.OrdinalIgnoreCase));
            if (hits.Count == 0)
            {
                // Plugins with no database: their files, a few at a time
                var files = Directory.EnumerateFiles(Program.GetDataFolderPathPublic()).Select(Path.GetFileName)
                    .Where(n => n != null && System.Text.RegularExpressions.Regex.IsMatch(n, @"\.es[mpl]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && (!HasCache(n) || LiveDb.Enabled)).ToList(); // a --live or session database may hold only some types
                var found = new System.Collections.Concurrent.ConcurrentBag<(string Plugin, string FormKey)>();
                System.Threading.Tasks.Parallel.ForEach(files, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 }, n =>
                {
                    var map = FileEditorIds(n!);
                    if (map == null) return;
                    foreach (var kv in map) if (kv.Value.Equals(key, StringComparison.OrdinalIgnoreCase)) found.Add((n!, kv.Key));
                });
                hits = found.ToList();
            }
            if (hits.Count == 0) return null;
            var origin = hits.FirstOrDefault(h => PluginOf(h.FormKey).Equals(h.Plugin, StringComparison.OrdinalIgnoreCase));
            return origin.FormKey ?? hits[0].FormKey;
        }

        // ---------------------------------------------------------------- EditorID resolver (display)
        static readonly Dictionary<string, string?> _edidCache = new(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, SqliteConnection?> _resolverConns = new(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> _ensuredPlugins = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// One kept-open read-only connection per cache for the whole run (null when the plugin has no cache). Opening a cache
        /// costs far more than one indexed lookup, so per-record lookups must not open a connection each. Cache rebuilds close
        /// these first (<see cref="CloseResolver"/>).
        /// </summary>
        static SqliteConnection? ResolverConn(string plugin)
        {
            if (!_resolverConns.TryGetValue(plugin, out var conn))
            {
                conn = HasCache(plugin) ? OpenCache(plugin, false) : null;
                _resolverConns[plugin] = conn;
            }
            return conn;
        }

        /// <summary>Returns the EditorID of a FormKey by looking in its origin plugin's cache (never rebuilds caches).</summary>
        public static string? EditorIdOf(string formKey)
        {
            if (_edidCache.TryGetValue(formKey, out var cached)) return cached;
            string? edid = null;
            string plugin = PluginOf(formKey);
            try
            {
                var conn = ResolverConn(plugin);
                if (conn != null)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT EditorID FROM Records WHERE {FormKeyMatch("FormKey")} LIMIT 1";
                    AddFormKeyParams(cmd, formKey);
                    edid = cmd.ExecuteScalar()?.ToString();
                    if (edid == formKey) edid = null; // records without EDID store their FormKey
                    // Types that aren't cached in full (Cell, Static, PackIn, ...) still have a Forms row with their EditorID.
                    if (edid == null && HasFormsTable(plugin, conn))
                    {
                        using var fcmd = conn.CreateCommand();
                        fcmd.CommandText = $"SELECT EditorID FROM Forms WHERE {FormKeyMatch("FormKey")} LIMIT 1";
                        AddFormKeyParams(fcmd, formKey);
                        edid = fcmd.ExecuteScalar() as string;
                    }
                }
            }
            catch { }
            // The load-order index has every plugin's EditorIDs on disk, shared by every process: ask it before reading the file
            bool listed = false;
            if (edid == null) { var (found, fromIndex) = LoadOrderIndex.EditorIdFromIndex(NormalizeFormKey(formKey)); if (found) { listed = true; edid = fromIndex; } }
            if (edid == null && !listed && !HasCache(plugin)) FileEditorIds(plugin)?.TryGetValue(formKey, out edid);
            // A database built for --live or the session may hold only some types
            if (edid == null && !listed && LiveDb.Enabled) edid = LiveDb.EditorIdOf(formKey);
            _edidCache[formKey] = edid;
            return edid;
        }

        static readonly Dictionary<string, bool> _hasForms = new(StringComparer.OrdinalIgnoreCase);
        static bool HasFormsTable(string plugin, SqliteConnection conn)
        {
            if (!_hasForms.TryGetValue(plugin, out bool has)) _hasForms[plugin] = has = TableExists(conn, "Forms");
            return has;
        }

        /// <summary>Annotates every FormKey in a string with its EditorID: "000CF2:darkstar.esm [DarkStar_Mk2_Helmet_Ecliptic]".</summary>
        public static string AnnotateFormKeys(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf(':') < 0) return text;
            return EmbeddedFormKey.Replace(text, m =>
            {
                string edid = EditorIdOf(m.Value) ?? "";
                return edid.Length > 0 ? $"{m.Value} [{edid}]" : m.Value;
            });
        }

        public static void CloseResolver()
        {
            foreach (var c in _resolverConns.Values) c?.Dispose();
            _resolverConns.Clear();
            _hasForms.Clear();
        }

        // ---------------------------------------------------------------- Table rendering
        public class Table
        {
            public List<string> Columns { get; } = new();
            public List<List<string>> Rows { get; } = new();
            public Table(params string[] cols) { Columns.AddRange(cols); }
            public void Add(params object?[] vals) => Rows.Add(vals.Select(v => v?.ToString() ?? "").ToList());
        }

        /// <summary>Renders a table in the selected format. Markdown output annotates FormKeys and honours --maxwidth.</summary>
        public static void Print(Table t, bool annotate = true)
        {
            switch (Format)
            {
                case OutputFormat.Json:
                    var arr = new JsonArray();
                    foreach (var row in t.Rows)
                    {
                        var o = new JsonObject();
                        for (int i = 0; i < t.Columns.Count; i++)
                        {
                            string v = i < row.Count ? row[i] : "";
                            JsonNode? parsed = null;
                            if (v.Length > 1 && (v[0] == '{' || v[0] == '[')) { try { parsed = JsonNode.Parse(v); } catch { } }
                            o[t.Columns[i]] = parsed ?? JsonValue.Create(v);
                        }
                        arr.Add(o);
                    }
                    Console.WriteLine(arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    break;
                case OutputFormat.Csv:
                    Console.WriteLine(string.Join(",", t.Columns.Select(Csv)));
                    foreach (var row in t.Rows) Console.WriteLine(string.Join(",", row.Select(Csv)));
                    break;
                default:
                    Console.WriteLine("| " + string.Join(" | ", t.Columns) + " |");
                    Console.WriteLine("| " + string.Join(" | ", t.Columns.Select(_ => ":---")) + " |");
                    foreach (var row in t.Rows)
                    {
                        var cells = row.Select(v =>
                        {
                            string s = v.Replace("\r", "").Replace("\n", " ");
                            if (annotate && ResolveNames) s = AnnotateFormKeys(s);
                            if (MaxWidth > 0 && s.Length > MaxWidth) s = s.Substring(0, MaxWidth) + $"…(+{s.Length - MaxWidth})";
                            return s.Replace("|", "\\|");
                        });
                        Console.WriteLine("| " + string.Join(" | ", cells) + " |");
                    }
                    break;
            }
        }

        static string Csv(string v) => v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

        /// <summary>Writes a heading/line only in markdown mode (keeps json/csv output machine-parseable).</summary>
        public static void Say(string line = "")
        {
            if (Format == OutputFormat.Markdown) Console.WriteLine(line);
        }

        /// <summary>Compact one-line rendering of a JSON node for tables.</summary>
        public static string Compact(JsonNode? n)
        {
            if (n == null) return "";
            if (n is JsonValue v) return v.ToString();
            return n.ToJsonString();
        }

        // ---------------------------------------------------------------- Report files (--out)
        /// <summary>
        /// Sends everything a command prints to a markdown file. Lines passed to <see cref="Summary"/> go to the file
        /// and are also printed on the console when the report closes, followed by the file path.
        /// Without a file path it does nothing (output stays on the console).
        /// </summary>
        public sealed class ReportOut : IDisposable
        {
            readonly TextWriter _orig;
            readonly StreamWriter? _file;
            readonly string? _path;
            internal readonly List<string> SummaryLines = new();

            ReportOut(string? outFile)
            {
                _orig = Console.Out;
                if (string.IsNullOrWhiteSpace(outFile)) return;
                _path = ResolveUserPath(outFile);
                string? dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _file = new StreamWriter(_path, append: false, new UTF8Encoding(false));
                Console.SetOut(_file);
            }

            public static ReportOut Begin(string? outFile)
            {
                var r = new ReportOut(outFile);
                if (r._file != null) _activeReport = r;
                return r;
            }

            public void Dispose()
            {
                if (_file == null) return;
                _file.Flush();
                _file.Dispose();
                Console.SetOut(_orig);
                _activeReport = null;
                foreach (var l in SummaryLines) Console.WriteLine(l);
                int lines = File.ReadLines(_path!).Count();
                Console.WriteLine($"Full report ({lines} lines): {_path}");
            }
        }

        static ReportOut? _activeReport;

        /// <summary>Writes a line like <see cref="Say"/>; when --out is active the line is also echoed to the console at the end.</summary>
        public static void Summary(string line)
        {
            Say(line);
            _activeReport?.SummaryLines.Add(line);
        }

        /// <summary>Records a line for the console summary only (when --out is active); nothing is written to the report.</summary>
        public static void ActiveSummary(string line) => _activeReport?.SummaryLines.Add(line);

        // ---------------------------------------------------------------- Localized strings
        static readonly Regex LocalizedEntry = new(@"^\[([A-Za-z_]+), (.*)\]$", RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>
        /// Localized strings are cached as an array of "[Language, text]" entries (vanilla: German first; mods: English only).
        /// Returns the English text (or the first entry when there is no English one). Returns null if the node isn't such an array.
        /// </summary>
        public static string? English(JsonNode? n)
        {
            if (n is not JsonArray arr || arr.Count == 0) return null;
            string? first = null;
            foreach (var item in arr)
            {
                if (item is not JsonValue v) return null;
                var m = LocalizedEntry.Match(v.ToString());
                if (!m.Success) return null;
                first ??= m.Groups[2].Value;
                if (m.Groups[1].Value.Equals("English", StringComparison.OrdinalIgnoreCase)) return m.Groups[2].Value;
            }
            return first;
        }

        /// <summary>Same as <see cref="English(JsonNode?)"/> for raw JSON text (used by the en() SQL function). Non-localized text is returned unchanged.</summary>
        public static string? EnglishText(string? json)
        {
            if (string.IsNullOrEmpty(json) || json[0] != '[') return json;
            try { return English(JsonNode.Parse(json)) ?? json; } catch { return json; }
        }

        /// <summary>Copy of a JSON tree in which every localized array is replaced by its English text.</summary>
        public static JsonNode? NormalizeLocalized(JsonNode? n)
        {
            if (n == null) return null;
            string? en = English(n);
            if (en != null) return JsonValue.Create(en);
            if (n is JsonArray a)
            {
                var copy = new JsonArray();
                foreach (var x in a) copy.Add(NormalizeLocalized(x));
                return copy;
            }
            if (n is JsonObject o)
            {
                var copy = new JsonObject();
                foreach (var kv in o) copy[kv.Key] = NormalizeLocalized(kv.Value);
                return copy;
            }
            return n.DeepClone();
        }

        // ---------------------------------------------------------------- Globs
        /// <summary>Case-insensitive regex for a * / ? glob.</summary>
        public static Regex GlobRegex(string glob) =>
            new("^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ---------------------------------------------------------------- Tiers
        static readonly Regex QualityTier = new(@"_?Quality_(\d+|TiersAny)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>"mod_X_Quality_3" → "Q3", "mod_X_Quality_TiersAny" → "TiersAny", anything else → null.</summary>
        public static string? TierOf(string? editorId)
        {
            if (string.IsNullOrEmpty(editorId)) return null;
            var m = QualityTier.Match(editorId);
            if (!m.Success) return null;
            return char.IsDigit(m.Groups[1].Value[0]) ? "Q" + int.Parse(m.Groups[1].Value) : "TiersAny"; // "_Quality_08" → Q8
        }

        /// <summary>EditorID without its "_Quality_N" / "_Quality_TiersAny" suffix (the set name).</summary>
        public static string SetStemOf(string editorId) => QualityTier.Replace(editorId, "");

        // ---------------------------------------------------------------- SQL helpers
        /// <summary>
        /// Adds SFSearch's SQL functions to a connection:
        /// edid(fk) = EditorID of a FormKey (looked up in its own plugin's cache), fkname(fk) = its English name,
        /// en(json) = English text of a localized JSON array, tier(edid) = 1..8 for "_Quality_N" EditorIDs (0 for TiersAny).
        /// </summary>
        public static void RegisterSqlFunctions(SqliteConnection conn)
        {
            conn.CreateFunction<string?, string?>("edid", fk => string.IsNullOrEmpty(fk) ? null : EditorIdOf(NormalizeFormKey(fk)), isDeterministic: true);
            conn.CreateFunction<string?, string?>("fkname", fk => string.IsNullOrEmpty(fk) ? null : NameOf(NormalizeFormKey(fk)), isDeterministic: true);
            conn.CreateFunction<string?, string?>("en", EnglishText, isDeterministic: true);
            conn.CreateFunction<string?, int?>("tier", e =>
            {
                string? t = TierOf(e);
                return t == null ? null : t == "TiersAny" ? 0 : int.Parse(t.Substring(1));
            }, isDeterministic: true);
        }

        static readonly Dictionary<string, string?> _nameCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>English display name of a FormKey from its origin plugin's cache (never rebuilds caches).</summary>
        public static string? NameOf(string formKey)
        {
            if (_nameCache.TryGetValue(formKey, out var cached)) return cached;
            string? name = null;
            string plugin = PluginOf(formKey);
            try
            {
                var conn = ResolverConn(plugin);
                if (conn != null)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT Name FROM Records WHERE {FormKeyMatch("FormKey")} LIMIT 1";
                    AddFormKeyParams(cmd, formKey);
                    name = cmd.ExecuteScalar()?.ToString();
                }
            }
            catch { }
            _nameCache[formKey] = name;
            return name;
        }

        /// <summary>SQL alias used when a plugin's cache is attached: Starfield.esm → sf, "SFBGS00D.esm" → sfbgs00d, "DarkStar_Manufacturing.esm" → darkstar_manufacturing.</summary>
        public static string AliasFor(string plugin)
        {
            string stem = Path.GetFileNameWithoutExtension(plugin).ToLowerInvariant();
            if (stem == "starfield") return "sf";
            var sb = new StringBuilder();
            foreach (char c in stem) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            string a = sb.ToString();
            if (a.Length == 0 || char.IsDigit(a[0])) a = "p_" + a;
            if (a is "main" or "temp") a += "_esm";
            return a;
        }

        /// <summary>
        /// Attaches other plugins' caches to an open cache connection (read-only) and builds the temp table
        /// AllRecords(FormKey, Signature, EditorID, Name, Origin, IsOverride, Plugin) over main + attached caches.
        /// When a FormKey is in several caches the row of the highest-priority plugin wins: the main plugin, then its masters
        /// (later masters first), then the extra plugins. Returns the (alias, plugin) list actually attached.
        /// </summary>
        public static List<(string Alias, string Plugin)> AttachCaches(SqliteConnection conn, string mainPlugin, IEnumerable<string> plugins)
        {
            const int MaxAttached = 10; // SQLite's default SQLITE_MAX_ATTACHED
            var attached = new List<(string Alias, string Plugin)>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "main", "temp" };
            foreach (var p in plugins.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (p.Equals(mainPlugin, StringComparison.OrdinalIgnoreCase)) continue;
                if (attached.Count >= MaxAttached)
                {
                    Console.Error.WriteLine($"[sfs] Not attached: {p} (SQLite allows {MaxAttached} attached caches).");
                    continue;
                }
                Program.EnsureCacheUpToDatePublic(p);
                if (!HasCache(p)) { Console.Error.WriteLine($"[sfs] Not attached: {p} (no cache, plugin not in Data)."); continue; }
                string alias = AliasFor(p);
                for (int n = 2; used.Contains(alias); n++) alias = AliasFor(p) + n;
                used.Add(alias);
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"ATTACH DATABASE @path AS \"{alias}\"";
                    cmd.Parameters.AddWithValue("@path", CachePath(p));
                    cmd.ExecuteNonQuery();
                }
                attached.Add((alias, p));
            }

            using (var cmd = conn.CreateCommand())
            {
                var sb = new StringBuilder("DROP TABLE IF EXISTS temp.AllRecords;\n" +
                    "CREATE TEMP TABLE AllRecords (FormKey TEXT PRIMARY KEY COLLATE NOCASE, Signature TEXT, EditorID TEXT, Name TEXT, Origin TEXT, IsOverride INTEGER, Plugin TEXT);\n");
                // Lowest priority first; INSERT OR REPLACE lets later (higher-priority) caches win.
                foreach (var (alias, p) in Enumerable.Reverse(attached))
                    sb.Append($"INSERT OR REPLACE INTO temp.AllRecords SELECT FormKey, Signature, EditorID, Name, Origin, IsOverride, '{p.Replace("'", "''")}' FROM \"{alias}\".Records;\n");
                sb.Append($"INSERT OR REPLACE INTO temp.AllRecords SELECT FormKey, Signature, EditorID, Name, Origin, IsOverride, '{mainPlugin.Replace("'", "''")}' FROM main.Records;\n");
                sb.Append("CREATE INDEX temp.idx_allrecords_edid ON AllRecords(EditorID);");
                cmd.CommandText = sb.ToString();
                cmd.ExecuteNonQuery();
            }
            return attached;
        }

        /// <summary>Faster settings for big read-only analysis queries (temp tables in RAM, bigger page cache, memory-mapped reads).</summary>
        public static void TuneForAnalysis(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA temp_store = MEMORY; PRAGMA cache_size = -262144; PRAGMA mmap_size = 2147483648;";
            cmd.ExecuteNonQuery();
        }
    }
}
