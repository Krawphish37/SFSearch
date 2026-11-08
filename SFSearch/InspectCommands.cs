using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace SFSearch
{
    public static class InspectCommands
    {
        static readonly HashSet<string> InternalMetaKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "Signature", "Record Header", "EDID - Editor ID",
            "FormVersion", "Version2", "VersionControl"
        };

        /// <summary>A cache built from a plugin with records Mutagen couldn't read says so, every time someone asks how complete it is.</summary>
        static void WarnIfIncomplete(SqliteConnection conn, string cacheKey)
        {
            string? n = Sfs.Meta(conn, "ReadProblems");
            if (n != null) Sfs.Say($"**WARNING: {n} problem(s) reading {cacheKey}, so this cache is missing what couldn't be read. Use `Live` for those records. First: {Sfs.Meta(conn, "ReadProblemSample")}**\n");
        }

        // ---------------------------------------------------------------- Header
        public static void Header(List<string> args)
        {
            if (args.Count < 1)
            {
                Console.WriteLine("Usage: SFSearch Header <plugin|path>");
                return;
            }

            string cacheKey = Program.EnsureCacheForFile(args[0]);
            using var conn = Sfs.OpenCache(cacheKey, ensureUpToDate: false);

            string style = Sfs.Meta(conn, "MasterStyle") ?? "Unknown";
            string flags = Sfs.Meta(conn, "HeaderFlags") ?? "";
            string totalRecs = Sfs.Meta(conn, "RecordCount") ?? "0";
            string cachedRecs = Sfs.Meta(conn, "CachedRecordCount") ?? "0";
            string overrides = Sfs.Meta(conn, "OverrideCount") ?? "0";
            string maxHex = Sfs.Meta(conn, "MaxLocalFormID") ?? "0x000000";
            string author = Sfs.Meta(conn, "Author") ?? "";
            string desc = Sfs.Meta(conn, "Description") ?? "";

            uint maxId = 0;
            if (maxHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                uint.TryParse(maxHex.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out maxId);

            string capacityNote = maxId <= 0xFFF
                ? $"Fits Light (<= 0xFFF, {maxId}/4096 used)"
                : maxId <= 0xFFFF
                    ? $"Fits Medium (<= 0xFFFF, exceeds Light 0xFFF!)"
                    : "Requires Full Master (> 0xFFFF)";

            WarnIfIncomplete(conn, cacheKey);
            Sfs.Say($"### Header: {cacheKey}");
            var metaTable = new Sfs.Table("Property", "Value");
            metaTable.Add("Plugin", cacheKey);
            metaTable.Add("MasterStyle", style);
            metaTable.Add("HeaderFlags", flags);
            metaTable.Add("TotalRecords", totalRecs);
            metaTable.Add("CachedRecords", cachedRecs);
            metaTable.Add("OverrideRecords", overrides);
            metaTable.Add("MaxLocalFormID", $"{maxHex} ({capacityNote})");
            if (!string.IsNullOrWhiteSpace(author)) metaTable.Add("Author", author);
            if (!string.IsNullOrWhiteSpace(desc)) metaTable.Add("Description", desc);
            Sfs.Print(metaTable, annotate: false);

            var masters = Sfs.Masters(conn);
            Sfs.Say($"\n#### Masters ({masters.Count})");
            var masterTable = new Sfs.Table("Index", "MasterName");
            for (int i = 0; i < masters.Count; i++) masterTable.Add(i, masters[i]);
            Sfs.Print(masterTable, annotate: false);
        }

        // ---------------------------------------------------------------- Stats
        public static void Stats(List<string> args)
        {
            if (args.Count < 1)
            {
                Console.WriteLine("Usage: SFSearch Stats <plugin|path>");
                return;
            }

            string cacheKey = Program.EnsureCacheForFile(args[0]);
            using var conn = Sfs.OpenCache(cacheKey, ensureUpToDate: false);

            WarnIfIncomplete(conn, cacheKey);
            Sfs.Say($"### Record Statistics: {cacheKey}");
            var t = new Sfs.Table("Signature", "Total", "New", "Overrides", "CachedInDB");

            if (Sfs.TableExists(conn, "SignatureCounts"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Signature, TotalCount, OverrideCount, Cached FROM SignatureCounts ORDER BY TotalCount DESC, Signature ASC";
                using var r = cmd.ExecuteReader();
                int sumTotal = 0, sumNew = 0, sumOvr = 0;
                while (r.Read())
                {
                    string sig = r.GetString(0);
                    int total = r.GetInt32(1);
                    int ovr = r.GetInt32(2);
                    int nw = total - ovr;
                    int cached = r.GetInt32(3);
                    sumTotal += total; sumNew += nw; sumOvr += ovr;
                    t.Add(sig, total, nw, ovr, cached == 0 ? "No" : cached == 2 ? "Partly" : "Yes");
                }
                t.Add("**TOTAL**", sumTotal, sumNew, sumOvr, "");
            }
            else
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Signature, COUNT(1) AS Cnt FROM Records GROUP BY Signature ORDER BY Cnt DESC";
                using var r = cmd.ExecuteReader();
                while (r.Read()) t.Add(r.GetString(0), r.GetInt32(1), "-", "-", "Yes");
            }

            Sfs.Print(t, annotate: false);
        }

        // ---------------------------------------------------------------- Names
        /// <summary>
        /// Batch FormID → "EditorID (FormID:plugin)" lookup for writing replies and notes. A bare hex ID is tried as a new record
        /// of each --in plugin (default darkstar.esm, Starfield.esm, DarkStar_Manufacturing.esm); every plugin that has it is listed,
        /// since the same local ID means different records in different plugins.
        /// </summary>
        public static void Names(List<string> args)
        {
            string? inOpt = Sfs.TakeOption(args, "--in");
            // One quoted list is easiest from PowerShell, which reads an unquoted 0003E6 as the number 3000000.
            var ids = args.SelectMany(a => a.Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).ToList();
            if (ids.Count < 1)
            {
                Console.WriteLine("Usage: SFSearch Names <FormID|FormKey> [<FormID|FormKey>...] [--in <plugin,...>]");
                return;
            }

            var plugins = string.IsNullOrWhiteSpace(inOpt)
                ? new List<string> { "darkstar.esm", "Starfield.esm", "DarkStar_Manufacturing.esm" }
                : Program.TargetResolver(inOpt, false);
            var conns = new Dictionary<string, SqliteConnection?>(StringComparer.OrdinalIgnoreCase);
            SqliteConnection? ConnOf(string plugin)
            {
                if (!conns.TryGetValue(plugin, out var c))
                {
                    if (!Sfs.HasCache(plugin)) Program.EnsureCacheUpToDatePublic(plugin);
                    c = Sfs.HasCache(plugin) ? Sfs.OpenCache(plugin, ensureUpToDate: false) : null;
                    conns[plugin] = c;
                }
                return c;
            }

            var t = new Sfs.Table("Input", "Written as", "Signature", "Name");
            try
            {
                foreach (var id in ids)
                {
                    List<string> keys;
                    if (Sfs.TryParseKey(id, null, out string fk)) keys = new List<string> { fk };
                    else if (Sfs.TryParseKey(id, "x.esm", out _)) keys = plugins.Select(p => Sfs.NormalizeFormKey(id + ":" + p)).ToList();
                    else { t.Add(id, "not a FormID", "", ""); continue; }

                    int hits = 0;
                    foreach (var key in keys)
                    {
                        var conn = ConnOf(Sfs.PluginOf(key));
                        if (conn == null) continue;
                        var row = LookupName(conn, key);
                        if (row == null) continue;
                        hits++;
                        string edid = string.IsNullOrEmpty(row.Value.EditorID) || row.Value.EditorID.Equals(key, StringComparison.OrdinalIgnoreCase)
                            ? "[no EditorID]" : row.Value.EditorID;
                        t.Add(id, $"{edid} ({key})", row.Value.Signature, row.Value.Name);
                    }
                    if (hits == 0) t.Add(id, "not found in " + string.Join(", ", keys.Select(Sfs.PluginOf)), "", "");
                }
            }
            finally
            {
                foreach (var c in conns.Values) c?.Dispose();
            }
            Sfs.Print(t, annotate: false);
        }

        /// <summary>EditorID, signature and name of a record by FormKey: Records first, then Forms (types that aren't cached).</summary>
        static (string EditorID, string Signature, string Name)? LookupName(SqliteConnection conn, string formKey)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT EditorID, Signature, Name FROM Records WHERE {Sfs.FormKeyMatch("FormKey")} LIMIT 1";
                Sfs.AddFormKeyParams(cmd, formKey);
                using var r = cmd.ExecuteReader();
                if (r.Read()) return (r.IsDBNull(0) ? "" : r.GetString(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2));
            }
            if (!Sfs.TableExists(conn, "Forms")) return null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT EditorID, Signature FROM Forms WHERE {Sfs.FormKeyMatch("FormKey")} LIMIT 1";
                Sfs.AddFormKeyParams(cmd, formKey);
                using var r = cmd.ExecuteReader();
                if (r.Read()) return (r.IsDBNull(0) ? "" : r.GetString(0), r.GetString(1), "");
            }
            return null;
        }

        // ---------------------------------------------------------------- Refs
        public static void Refs(List<string> args)
        {
            string? inOpt = Sfs.TakeOption(args, "--in");
            if (args.Count < 1)
            {
                Console.WriteLine("Usage: SFSearch Refs <FormKey|EditorID> [--in <target>]");
                return;
            }

            var searchPlugins = string.IsNullOrWhiteSpace(inOpt)
                ? Sfs.CachedPlugins()
                : Program.TargetResolver(inOpt, false);

            string? targetFk = Sfs.ResolveUserKey(args[0], searchPlugins);
            if (string.IsNullOrEmpty(targetFk))
            {
                Console.WriteLine($"Could not resolve '{args[0]}' to a FormKey in any database or plugin in Data.");
                return;
            }

            targetFk = Sfs.NormalizeFormKey(targetFk);
            LiveDb.FocusOn("refs", targetFk);
            if (string.IsNullOrWhiteSpace(inOpt))
            {
                // Every plugin in Data that can point at it, not only those that happen to have a database. One without a
                // database gets one only when its file holds the record's bytes.
                var canHold = Sfs.PluginsThatCanHold(Sfs.PluginOf(targetFk));
                var key = Mutagen.Bethesda.Plugins.FormKey.Factory(targetFk);
                var mentions = new System.Collections.Concurrent.ConcurrentBag<string>();
                System.Threading.Tasks.Parallel.ForEach(canHold.Where(p => !Sfs.HasCache(p)), new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 8 }, p =>
                {
                    try
                    {
                        // Each plugin's scan is remembered by its size and date: a repeat reads the answer instead of the file
                        bool hit;
                        if (RefScanCache.Get(p, targetFk) is { } known) hit = known.Mentions;
                        else { hit = LiveCommand.FileMentions(p, key); RefScanCache.SetMentions(p, targetFk, hit); }
                        if (hit) mentions.Add(p);
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"[sfs] {p}: {ex.Message}"); }
                });
                if (!mentions.IsEmpty) Console.Error.WriteLine($"[sfs] Building databases for {mentions.Count} plugin(s) whose file mentions {targetFk}: {string.Join(", ", mentions.OrderBy(x => x))}");
                searchPlugins = canHold.Where(p => Sfs.HasCache(p) || mentions.Contains(p)).ToList();
                searchPlugins.AddRange(Sfs.CachedPlugins().Where(p => !System.IO.File.Exists(System.IO.Path.Combine(Program.GetDataFolderPathPublic(), p)) && !searchPlugins.Contains(p, StringComparer.OrdinalIgnoreCase)));
                if (canHold.Count == 0) Console.WriteLine($"{Sfs.PluginOf(targetFk)} isn't in the Data folder.");
            }
            string? targetEdid = Sfs.EditorIdOf(targetFk);
            Sfs.Say($"### Inbound References to `{targetFk}`{(string.IsNullOrEmpty(targetEdid) ? "" : $" (`{targetEdid}`)")}");

            var t = new Sfs.Table("Plugin", "FromFormKey", "Signature", "EditorID", "Path");
            int searched = 0;
            foreach (var p in searchPlugins)
            {
                try { Program.EnsureCacheUpToDatePublic(p); } catch (Exception ex) { Console.Error.WriteLine($"[sfs] {p}: {ex.Message}"); continue; }
                if (!Sfs.HasCache(p)) continue;
                searched++;
                using var conn = Sfs.OpenCache(p, ensureUpToDate: false);
                if (!Sfs.TableExists(conn, "Refs")) continue;

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT FromFormKey, FromSignature, FromEditorID, Path FROM Refs WHERE {Sfs.FormKeyMatch("ToFormKey", "@fk")} ORDER BY FromSignature, FromEditorID, Path";
                Sfs.AddFormKeyParams(cmd, targetFk, "@fk");
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    t.Add(p, r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3));
                }
            }

            Sfs.Print(t, annotate: false);
            int canHoldCount = string.IsNullOrWhiteSpace(inOpt) ? Sfs.PluginsThatCanHold(Sfs.PluginOf(targetFk)).Count : searchPlugins.Count;
            Sfs.Say($"\nFound {t.Rows.Count} reference(s) across {(string.IsNullOrWhiteSpace(inOpt) ? $"the {canHoldCount} plugin(s) in Data that can point at it" : $"{searched} plugin(s)")}.");
        }

        // ---------------------------------------------------------------- Conflicts
        public static void Conflicts(List<string> args)
        {
            string? inOpt = Sfs.TakeOption(args, "--in");
            if (args.Count < 1)
            {
                Console.WriteLine("Usage: SFSearch Conflicts <FormKey|EditorID> [--in <target>]");
                return;
            }

            var candidatePlugins = string.IsNullOrWhiteSpace(inOpt)
                ? Sfs.CachedPlugins()
                : Program.TargetResolver(inOpt, false);

            string? fk = Sfs.ResolveUserKey(args[0], candidatePlugins);
            if (string.IsNullOrEmpty(fk))
            {
                Console.WriteLine($"Could not resolve '{args[0]}' to a FormKey in any database or plugin in Data.");
                return;
            }
            fk = Sfs.NormalizeFormKey(fk);
            LiveDb.FocusOn("conflicts", fk);
            string originPlugin = Sfs.PluginOf(fk);
            if (string.IsNullOrWhiteSpace(inOpt))
            {
                // Every plugin in Data that really has the record: from the load-order index when it's up to date, otherwise
                // from the record headers of every plugin that can hold it. Plus any database that has it.
                var indexed = LoadOrderIndex.HoldersIfFresh(fk);
                if (indexed != null) candidatePlugins = indexed;
                else
                {
                    var key = Mutagen.Bethesda.Plugins.FormKey.Factory(fk);
                    var holders = new System.Collections.Concurrent.ConcurrentBag<string>();
                    System.Threading.Tasks.Parallel.ForEach(Sfs.PluginsThatCanHold(originPlugin), new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 }, p =>
                    {
                        try { if (LiveCommand.FileHasRecord(p, key)) holders.Add(p); } catch { }
                    });
                    candidatePlugins = holders.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                }
                foreach (var c in Sfs.CachedPlugins()) if (!System.IO.File.Exists(System.IO.Path.Combine(Program.GetDataFolderPathPublic(), c)) && !candidatePlugins.Contains(c, StringComparer.OrdinalIgnoreCase)) candidatePlugins.Add(c);
            }

            if (!candidatePlugins.Contains(originPlugin, StringComparer.OrdinalIgnoreCase) && Sfs.HasCache(originPlugin))
                candidatePlugins.Insert(0, originPlugin);

            // Order plugins so origin is first and plugins with more masters / dependent on earlier plugins come later
            var hits = new List<(string Plugin, Sfs.RecordRow Row, HashSet<string> Masters)>();
            foreach (var p in candidatePlugins)
            {
                try { Program.EnsureCacheUpToDatePublic(p); } catch (Exception ex) { Console.Error.WriteLine($"[sfs] {p}: {ex.Message}"); continue; }
                if (!Sfs.HasCache(p)) continue;
                using var conn = Sfs.OpenCache(p, ensureUpToDate: false);
                var row = Sfs.FindRecord(conn, p, fk, "*");
                if (row != null)
                {
                    var masters = new HashSet<string>(Sfs.Masters(conn), StringComparer.OrdinalIgnoreCase);
                    hits.Add((p, row, masters));
                }
            }

            if (hits.Count == 0)
            {
                Console.WriteLine($"No cached plugins contain `{fk}`.");
                return;
            }

            // By master references when the load order can't be read (origin first, then a plugin before any plugin that masters it)
            int MasterOrder((string Plugin, Sfs.RecordRow Row, HashSet<string> Masters) a, (string Plugin, Sfs.RecordRow Row, HashSet<string> Masters) b)
            {
                bool aOrigin = a.Plugin.Equals(originPlugin, StringComparison.OrdinalIgnoreCase);
                bool bOrigin = b.Plugin.Equals(originPlugin, StringComparison.OrdinalIgnoreCase);
                if (aOrigin != bOrigin) return aOrigin ? -1 : 1;
                if (b.Masters.Contains(a.Plugin)) return -1;
                if (a.Masters.Contains(b.Plugin)) return 1;
                int mc = a.Masters.Count.CompareTo(b.Masters.Count);
                return mc != 0 ? mc : string.Compare(a.Plugin, b.Plugin, StringComparison.OrdinalIgnoreCase);
            }
            // The real load order (plugins.txt): the last loaded version wins. Plugins in Data that aren't loaded are listed apart.
            Dictionary<string, (int Pos, bool Enabled)>? positions = null;
            try { positions = LoadOrderIndex.Positions(); } catch (Exception ex) { Console.Error.WriteLine($"[sfs] couldn't read the load order ({ex.Message}); ordering by masters instead."); }
            var loaded = new List<(string Plugin, Sfs.RecordRow Row, HashSet<string> Masters)>();
            var notLoaded = new List<(string Plugin, Sfs.RecordRow Row, HashSet<string> Masters)>();
            foreach (var h in hits)
                if (positions == null || (positions.TryGetValue(h.Plugin, out var ps) && ps.Enabled)) loaded.Add(h); else notLoaded.Add(h);
            if (positions != null) loaded.Sort((a, b) => positions[a.Plugin].Pos.CompareTo(positions[b.Plugin].Pos));
            else loaded.Sort(MasterOrder);
            notLoaded.Sort(MasterOrder);

            var all = loaded.Concat(notLoaded).ToList();
            var originHit = all.FirstOrDefault(h => h.Plugin.Equals(originPlugin, StringComparison.OrdinalIgnoreCase));
            var baseline = (originHit.Plugin != null ? originHit : all[0]).Row;
            Sfs.Say($"### Conflict / Override Chain for `{fk}` (`{baseline.EditorID}`, `{baseline.Signature}`){(positions != null ? ", in load order" : ", by masters (load order unreadable)")}");

            var t = new Sfs.Table("Order", "Plugin", "Role", "ChangedVsPrevious", "ChangedVsOrigin", "Summary");
            Sfs.RecordRow? previous = null;
            for (int i = 0; i < all.Count; i++)
            {
                var h = all[i];
                bool isLoaded = i < loaded.Count;
                bool isOrigin = h.Plugin.Equals(originPlugin, StringComparison.OrdinalIgnoreCase);
                bool isWinner = isLoaded && i == loaded.Count - 1;
                string role = !isLoaded ? (isOrigin ? "ORIGIN (NOT LOADED)" : "NOT LOADED") : isOrigin && isWinner ? "ORIGIN (WINNER)" : isOrigin ? "ORIGIN" : isWinner ? "OVERRIDE (WINNER)" : "OVERRIDE";
                string order = isLoaded ? (positions != null ? positions[h.Plugin].Pos.ToString() : i.ToString()) : "-";
                if (isOrigin)
                    t.Add(order, h.Plugin, role, previous == null ? "(first)" : Fields(GetChangedTopFields(previous.Data, h.Row.Data)), "(baseline)", $"Defines {h.Row.EditorID}");
                else
                {
                    var changed = GetChangedTopFields(baseline.Data, h.Row.Data);
                    string vsPrev = previous == null || !isLoaded ? "-" : Fields(GetChangedTopFields(previous.Data, h.Row.Data));
                    t.Add(order, h.Plugin, role, vsPrev, Fields(changed), ClassifyChanges(changed));
                }
                if (isLoaded) previous = h.Row;
            }
            Sfs.Print(t, annotate: false);
            if (notLoaded.Count > 0) Sfs.Say($"\n{notLoaded.Count} plugin(s) in Data have this record but aren't loaded: they don't take part in who wins.");
        }

        static string Fields(List<string> changed) => changed.Count == 0 ? "NONE (ITM)" : string.Join(", ", changed);

        // ---------------------------------------------------------------- Delta
        public static void Delta(List<string> args)
        {
            bool detail = Sfs.TakeFlag(args, "--detail");
            string? outFile = Sfs.TakeOption(args, "--out");
            if (args.Count < 1)
            {
                Console.WriteLine("Usage: SFSearch Delta <plugin> [FormKey|EditorID] [--detail] [--out report.md]");
                return;
            }
            // An unknown option would otherwise become the EditorID filter and silently report 0 overrides.
            var unknown = args.Skip(1).FirstOrDefault(a => a.StartsWith("--"));
            if (unknown != null)
            {
                Console.WriteLine($"Delta: unknown option '{unknown}'. Usage: SFSearch Delta <plugin> [FormKey|EditorID] [--detail] [--out report.md]");
                return;
            }

            string plugin = Program.EnsureCacheForFile(args[0]);
            string? singleKey = args.Count > 1 ? args[1] : null;

            using var conn = Sfs.OpenCache(plugin, ensureUpToDate: false);
            var masters = Sfs.Masters(conn);

            var overrides = new List<Sfs.RecordRow>();
            using (var cmd = conn.CreateCommand())
            {
                // IsOverride column, not "FormKey plugin != cache name" (a copy read by path has a "_file1_<name>.esm" cache).
                cmd.CommandText = "SELECT FormKey, Signature, EditorID, Name, Data FROM Records WHERE IsOverride = 1";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string fk = r.GetString(0);
                    var row = new Sfs.RecordRow
                    {
                        Plugin = plugin,
                        FormKey = fk,
                        Signature = r.GetString(1),
                        EditorID = r.IsDBNull(2) ? "" : r.GetString(2),
                        Name = r.IsDBNull(3) ? "" : r.GetString(3),
                        Data = (JsonNode.Parse(r.GetString(4)) as JsonObject) ?? new JsonObject()
                    };
                    if (singleKey != null)
                    {
                        bool match = row.EditorID.Equals(singleKey, StringComparison.OrdinalIgnoreCase)
                                  || row.FormKey.Equals(Sfs.NormalizeFormKey(singleKey), StringComparison.OrdinalIgnoreCase)
                                  || row.EditorID.Contains(singleKey, StringComparison.OrdinalIgnoreCase);
                        if (!match) continue;
                    }
                    overrides.Add(row);
                }
            }

            using var report = Sfs.ReportOut.Begin(outFile);
            Sfs.Summary($"### Override Delta Report: {plugin} ({overrides.Count} override record(s))");
            var t = new Sfs.Table("FormKey", "Signature", "EditorID", "BaselinePlugin", "Category", "ChangedFields");
            int itm = 0, noBase = 0;

            var detailPairs = new List<(Sfs.RecordRow Ovr, Sfs.RecordRow Base, List<string> Changed)>();
            foreach (var ovr in overrides)
            {
                var baseRow = FindPrecedingRecord(ovr.FormKey, masters);
                if (baseRow == null)
                {
                    t.Add(ovr.FormKey, ovr.Signature, ovr.EditorID, "(unmached master)", "Unknown", "Master not cached/found");
                    noBase++;
                    continue;
                }
                var changed = GetChangedTopFields(baseRow.Data, ovr.Data);
                string cat = ClassifyChanges(changed);
                if (changed.Count == 0) itm++;
                t.Add(ovr.FormKey, ovr.Signature, ovr.EditorID, Sfs.CanonicalPluginName(baseRow.Plugin), cat, changed.Count == 0 ? "NONE (ITM)" : string.Join(", ", changed));
                if (detail || singleKey != null) detailPairs.Add((ovr, baseRow, changed));
            }

            Sfs.Print(t, annotate: false);
            Sfs.ActiveSummary($"{itm} ITM, {noBase} with no master record found, {detailPairs.Count} detail section(s)");

            foreach (var (ovr, baseRow, changed) in detailPairs)
            {
                Sfs.Say($"\n#### Detail: `{ovr.EditorID}` (`{ovr.FormKey}`) — `{baseRow.Plugin}` -> `{plugin}`");
                var dt = new Sfs.Table("Field", $"Baseline ({baseRow.Plugin})", $"Override ({plugin})");
                foreach (var f in changed)
                {
                    dt.Add(f, Sfs.Compact(baseRow.Data[f]), Sfs.Compact(ovr.Data[f]));
                }
                Sfs.Print(dt);
            }
        }

        public static Sfs.RecordRow? FindPrecedingRecord(string formKey, List<string> mastersInOrder)
        {
            string origin = Sfs.PluginOf(formKey);
            // Walk masters from last (highest priority before our plugin) to first
            for (int i = mastersInOrder.Count - 1; i >= 0; i--)
            {
                string m = mastersInOrder[i];
                if (Sfs.HasCache(m))
                {
                    var found = Sfs.FindRecord(m, formKey, "*", ensureUpToDate: false);
                    if (found != null) return found;
                }
            }
            // Fall back to ensuring origin cache is built if it exists in Data
            if (Sfs.HasCache(origin))
                return Sfs.FindRecord(origin, formKey, "*", ensureUpToDate: false);
            try
            {
                Program.EnsureCacheUpToDatePublic(origin);
                if (Sfs.HasCache(origin))
                    return Sfs.FindRecord(origin, formKey, "*", ensureUpToDate: false);
            }
            catch { }
            return null;
        }

        public static List<string> GetChangedTopFields(JsonObject a, JsonObject b, HashSet<string>? ignore = null)
        {
            var keys = a.Select(x => x.Key).Union(b.Select(x => x.Key)).Distinct().ToList();
            var changed = new List<string>();
            foreach (var k in keys)
            {
                if (InternalMetaKeys.Contains(k)) continue;
                if (ignore != null && ignore.Contains(k)) continue;
                string s1 = a[k]?.ToJsonString() ?? "";
                string s2 = b[k]?.ToJsonString() ?? "";
                if (s1 != s2) changed.Add(k);
            }
            return changed;
        }

        public static string ClassifyChanges(List<string> changedFields)
        {
            if (changedFields.Count == 0) return "ITM";
            var cats = new List<string>();
            if (changedFields.Any(f => f is "Items" or "Entries")) cats.Add("List");
            if (changedFields.Any(f => f is "Armatures" or "WorldModel" or "FirstPersonModel" or "Model" or "FirstPersonFlags" or "Skin" or "ObjectBounds")) cats.Add("Mesh/Visual");
            if (changedFields.Any(f => f is "ArmorRating" or "Resistances" or "AttackDamage" or "DamageTypes" or "BaseWeight" or "BaseValue" or "Weight" or "Value" or "AmmoCapacity" or "ShotsPerSecond" or "ReloadSpeed")) cats.Add("Stats");
            if (changedFields.Any(f => f is "Keywords" or "AttachParentSlots")) cats.Add("Keywords/APPR");
            if (changedFields.Any(f => f is "ObjectTemplates" or "Components" or "InstanceNaming")) cats.Add("Templates/Quality");
            if (changedFields.Any(f => f is "VirtualMachineAdapter")) cats.Add("Scripts");
            if (cats.Count == 0) cats.Add("Other");
            return string.Join(" + ", cats);
        }
    }
}
