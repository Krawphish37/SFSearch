using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;

namespace SFSearch
{
    /// <summary>
    /// Adds record types to a plugin's database, unfiltered, with no rebuild. Each record gets its own row: a cell's references,
    /// a worldspace's cells and a topic's responses are never stored inside their parent. Writes in batches, so an interrupted
    /// run continues where it stopped. The database remembers what was added, and a rebuild adds it again.
    /// </summary>
    public static class AddCommand
    {
        const string Usage = @"Usage: SFSearch Add <plugin|path> <Type> [<Type> ...]   Store these record types in full
       SFSearch Add <plugin|path> --list               Types in the plugin, and how many rows of each are stored
       SFSearch Add <plugin|path> --all                Every type, unfiltered. Starfield.esm takes about 20 minutes and 50 GB
  Types are names or signatures: Static, STAT, Cell, CELL.
  Run it again after an interruption and it continues where it stopped. A rebuild adds the same types back.";

        const string AddedTypesKey = "AddedTypes";
        const int BatchRecords = 5000;
        const long BatchChars = 64 * 1024 * 1024;

        public static void Run(List<string> args)
        {
            bool all = Sfs.TakeFlag(args, "--all");
            bool list = Sfs.TakeFlag(args, "--list");
            if (args.Count == 0 || (!all && !list && args.Count < 2))
            {
                Console.WriteLine(Usage);
                return;
            }
            string target = args[0];
            string cacheKey = Program.EnsureCacheForFile(target);
            string dbPath = Program.GetCachePath(cacheKey);
            Sfs.WarnMissingMasters(cacheKey, Program.ReadCacheMetaInternal(dbPath, "MissingMasters"));

            if (list)
            {
                ListTypes(dbPath);
                return;
            }

            string? sourcePath = Program.CacheSourcePath(cacheKey, dbPath);
            if (sourcePath == null)
            {
                Console.WriteLine($"Can't find the plugin behind {cacheKey}.");
                return;
            }

            var known = FormSignatures(dbPath);
            List<string> types;
            if (all)
            {
                types = known.Keys.ToList();
                long rows = known.Values.Sum();
                Console.WriteLine($"Adding every type in {Path.GetFileName(sourcePath)}: {rows} rows. Starfield.esm, with 3.8 million, takes about 20 minutes and 50 GB.");
            }
            else
            {
                types = new List<string>();
                foreach (var raw in args.Skip(1))
                {
                    string name = Program.NormalizeRecordTypePublic(raw);
                    var match = known.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (match == null)
                    {
                        Console.WriteLine($"{Path.GetFileName(sourcePath)} has no {raw} records. `Add {target} --list` shows what it has.");
                        return;
                    }
                    types.Add(match);
                }
            }

            string modName = Path.GetFileName(sourcePath);
            var read = new BinaryReadParameters { MasterFlagsLookup = new LossCheckCommand.HeaderFlagCache(Program.GetDataFolderPathPublic()) };
            using var mod = StarfieldMod.CreateFromBinaryOverlay(new ModPath(ModKey.FromFileName(modName), sourcePath), StarfieldRelease.Starfield, read);
            AddTypes(dbPath, mod, mod.ToUntypedImmutableLinkCache(), types);
        }

        internal static List<string> ReadAddedTypes(string dbPath)
        {
            string? value = Program.ReadCacheMetaInternal(dbPath, AddedTypesKey);
            return string.IsNullOrEmpty(value) ? new List<string>() : value.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        /// <summary>Stores every record of these types in full, replacing a filtered row where one exists.</summary>
        internal static void AddTypes(string dbPath, IModGetter mod, ILinkCache linkCache, List<string> types)
        {
            string modFileName = mod.ModKey.FileName.String;
            using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            conn.Open();
            void Exec(string sql) { using var c = conn.CreateCommand(); c.CommandText = sql; c.CommandTimeout = 0; c.ExecuteNonQuery(); }
            Exec("PRAGMA journal_mode = WAL;");
            Exec("PRAGMA synchronous = NORMAL;");
            Exec("CREATE TABLE IF NOT EXISTS AddDone (FormKey TEXT PRIMARY KEY) WITHOUT ROWID;");

            // Noted first, so a rebuild after an interruption still brings these types back.
            var added = new HashSet<string>(ReadAddedTypes(dbPath), StringComparer.OrdinalIgnoreCase);
            added.UnionWith(types);
            SetMeta(conn, null, AddedTypesKey, string.Join(",", added.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));

            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT FormKey FROM AddDone";
                using var r = c.ExecuteReader();
                while (r.Read()) done.Add(r.GetString(0));
            }
            if (done.Count > 0) Console.WriteLine($"Continuing an earlier run: {done.Count} records already added.");

            long totalAdded = 0;
            var refBuffer = new List<(string Path, string ToFormKey)>();
            var options = new JsonSerializerOptions { WriteIndented = false };
            Program.TooDeepValues = 0;

            foreach (var type in types)
            {
                long typeAdded = 0, pendingChars = 0, pendingRecords = 0;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var tx = conn.BeginTransaction();
                var commands = new Commands(conn, tx);
                var problems = new List<string>();
                long unreadFields = 0;
                string? unreadExample = null;
                foreach (var rec in RecordsOfType(mod, type, problems))
                {
                    string fk = Sfs.NormalizeFormKey(rec.FormKey.ToString());
                    if (done.Contains(fk)) continue;

                    var obj = Program.RecordToJson(rec, linkCache, out string name, full: true, skipChildren: true);
                    string data = obj.ToJsonString(options);
                    // The JSON writer stores < as \u003C
                    if (data.Contains("\\u003Cerror:", StringComparison.Ordinal))
                    {
                        unreadFields++;
                        unreadExample ??= fk;
                    }
                    string origin = Sfs.CanonicalPluginName(rec.FormKey.ModKey.FileName.String);
                    bool isOverride = !origin.Equals(modFileName, StringComparison.OrdinalIgnoreCase);
                    string edid = !string.IsNullOrEmpty(rec.EditorID) ? rec.EditorID : fk;

                    commands.DeleteRefs.Parameters[0].Value = fk;
                    commands.DeleteRefs.ExecuteNonQuery();
                    var p = commands.Insert.Parameters;
                    p["@fk"].Value = fk;
                    p["@sig"].Value = type;
                    p["@edid"].Value = edid;
                    p["@name"].Value = Program.StripStringIdTagInternal(name);
                    p["@origin"].Value = origin;
                    p["@ovr"].Value = isOverride ? 1 : 0;
                    p["@data"].Value = data;
                    commands.Insert.ExecuteNonQuery();

                    refBuffer.Clear();
                    foreach (var kvp in obj)
                    {
                        if (kvp.Key == "Record Header" || kvp.Key == "EDID - Editor ID" || kvp.Key == "Signature") continue;
                        Program.CollectRefsFromJsonInternal(kvp.Value, kvp.Key, refBuffer);
                    }
                    if (refBuffer.Count > 0)
                    {
                        var rp = commands.InsertRef.Parameters;
                        rp["@from"].Value = fk;
                        rp["@sig"].Value = type;
                        rp["@edid"].Value = edid;
                        foreach (var (path, to) in refBuffer)
                        {
                            rp["@path"].Value = path;
                            rp["@to"].Value = to;
                            commands.InsertRef.ExecuteNonQuery();
                        }
                    }

                    commands.Done.Parameters[0].Value = fk;
                    commands.Done.ExecuteNonQuery();
                    typeAdded++;
                    totalAdded++;
                    pendingRecords++;
                    pendingChars += data.Length;
                    if (pendingRecords >= BatchRecords || pendingChars >= BatchChars)
                    {
                        tx.Commit();
                        tx.Dispose();
                        commands.Dispose();
                        tx = conn.BeginTransaction();
                        commands = new Commands(conn, tx);
                        pendingRecords = 0;
                        pendingChars = 0;
                        Console.Error.WriteLine($"  {type}: {typeAdded} added...");
                    }
                }

                // This type is finished: Stats shows it as cached.
                using (var c = conn.CreateCommand())
                {
                    c.Transaction = tx;
                    c.CommandText = "UPDATE SignatureCounts SET Cached = 1 WHERE Signature = @s";
                    c.Parameters.AddWithValue("@s", type);
                    c.ExecuteNonQuery();
                }
                tx.Commit();
                tx.Dispose();
                commands.Dispose();
                foreach (var problem in problems.Take(3)) Console.Error.WriteLine($"[sfs] WARNING: {problem}");
                if (unreadFields > 0)
                    Console.WriteLine($"WARNING: Mutagen couldn't read a field in {unreadFields} {type} record(s), e.g. {unreadExample}. They are stored with <error: ...> in that field, and that field's links are missing.");
                Console.WriteLine($"{type}: {typeAdded} records added ({clock.Elapsed.TotalSeconds:F1} s).");
            }

            Exec("DROP TABLE IF EXISTS AddDone;");
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT count(*) FROM Records";
                SetMeta(conn, null, "CachedRecordCount", Convert.ToString(c.ExecuteScalar()) ?? "0");
            }
            if (Program.TooDeepValues > 0) Console.Error.WriteLine($"[sfs] WARNING: {Program.TooDeepValues} value(s) were nested too deep and were stored as plain text.");
            Exec("PRAGMA journal_mode = DELETE;");
            Console.WriteLine($"Done: {totalAdded} records added to {Path.GetFileName(dbPath)}. Now {new FileInfo(dbPath).Length / (1024 * 1024)} MB.");
        }

        static IEnumerable<IMajorRecordGetter> RecordsOfType(IModGetter mod, string type, List<string> problems)
        {
            var getter = typeof(IWeaponGetter).Assembly.GetType($"Mutagen.Bethesda.Starfield.I{type}Getter", false, true);
            if (getter != null)
            {
                var found = new HashSet<FormKey>();
                bool failed = false;
                using var e = mod.EnumerateMajorRecords(getter).GetEnumerator();
                while (true)
                {
                    IMajorRecordGetter? rec = null;
                    try
                    {
                        if (!e.MoveNext()) break;
                        rec = e.Current;
                    }
                    catch (Exception ex)
                    {
                        problems.Add($"{type}: {(ex.InnerException ?? ex).Message}");
                        failed = true;
                        break;
                    }
                    if (rec != null && rec.Registration.Name.Equals(type, StringComparison.OrdinalIgnoreCase) && found.Add(rec.FormKey)) yield return rec;
                }
                if (!failed) yield break;
            }
            foreach (var rec in Program.WalkRecordsInternal(mod, problems))
                if (rec.Registration.Name.Equals(type, StringComparison.OrdinalIgnoreCase)) yield return rec;
        }

        static Dictionary<string, long> FormSignatures(string dbPath)
        {
            var ret = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var c = conn.CreateCommand();
            c.CommandText = "SELECT Signature, count(*) FROM Forms GROUP BY Signature";
            c.CommandTimeout = 0;
            using var r = c.ExecuteReader();
            while (r.Read()) ret[r.GetString(0)] = r.GetInt64(1);
            return ret;
        }

        static void ListTypes(string dbPath)
        {
            var added = new HashSet<string>(ReadAddedTypes(dbPath), StringComparer.OrdinalIgnoreCase);
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            conn.Open();
            var stored = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT Signature, count(*) FROM Records GROUP BY Signature";
                c.CommandTimeout = 0;
                using var r = c.ExecuteReader();
                while (r.Read()) stored[r.GetString(0)] = r.GetInt64(1);
            }
            var t = new Sfs.Table("Type", "Rows", "Stored in full", "Added");
            foreach (var kv in FormSignatures(dbPath).OrderByDescending(k => k.Value))
                t.Add(kv.Key, kv.Value.ToString(), stored.GetValueOrDefault(kv.Key).ToString(), added.Contains(kv.Key) ? "yes" : "");
            Sfs.Print(t, annotate: false);
            Console.WriteLine($"Database: {new FileInfo(dbPath).Length / (1024 * 1024)} MB.");
            Console.WriteLine("Biggest types in Starfield.esm: SurfaceTree by far (4.7 GB of text and 197 million of the 209 million link rows), then NavigationMesh 3.0 GB, Cell 2.6 GB and PlacedObject 2.1 GB.");
        }

        static void SetMeta(SqliteConnection conn, SqliteTransaction? tx, string key, string value)
        {
            using var c = conn.CreateCommand();
            c.Transaction = tx;
            c.CommandText = "INSERT OR REPLACE INTO Meta (Key, Value) VALUES (@k, @v)";
            c.Parameters.AddWithValue("@k", key);
            c.Parameters.AddWithValue("@v", value);
            c.ExecuteNonQuery();
        }

        sealed class Commands : IDisposable
        {
            public readonly SqliteCommand DeleteRefs, Insert, InsertRef, Done;

            public Commands(SqliteConnection conn, SqliteTransaction tx)
            {
                DeleteRefs = conn.CreateCommand();
                DeleteRefs.Transaction = tx;
                DeleteRefs.CommandText = "DELETE FROM Refs WHERE FromFormKey = @fk";
                DeleteRefs.Parameters.Add("@fk", SqliteType.Text);

                Insert = conn.CreateCommand();
                Insert.Transaction = tx;
                Insert.CommandText = "INSERT OR REPLACE INTO Records (FormKey, Signature, EditorID, Name, Origin, IsOverride, Data) VALUES (@fk, @sig, @edid, @name, @origin, @ovr, @data)";
                foreach (var name in new[] { "@fk", "@sig", "@edid", "@name", "@origin", "@data" }) Insert.Parameters.Add(name, SqliteType.Text);
                Insert.Parameters.Add("@ovr", SqliteType.Integer);

                InsertRef = conn.CreateCommand();
                InsertRef.Transaction = tx;
                InsertRef.CommandText = "INSERT INTO Refs (FromFormKey, FromSignature, FromEditorID, Path, ToFormKey) VALUES (@from, @sig, @edid, @path, @to)";
                foreach (var name in new[] { "@from", "@sig", "@edid", "@path", "@to" }) InsertRef.Parameters.Add(name, SqliteType.Text);

                Done = conn.CreateCommand();
                Done.Transaction = tx;
                Done.CommandText = "INSERT OR IGNORE INTO AddDone (FormKey) VALUES (@fk)";
                Done.Parameters.Add("@fk", SqliteType.Text);
            }

            public void Dispose()
            {
                DeleteRefs.Dispose();
                Insert.Dispose();
                InsertRef.Dispose();
                Done.Dispose();
            }
        }
    }
}
