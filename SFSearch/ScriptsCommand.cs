using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SFSearch
{
    /// <summary>
    /// A plugin's Papyrus scripts as source (2026-11-07). `Scripts <plugin>` takes its .pex files from its own BA2s and loose
    /// Data\Scripts, decompiles them once with Champollion, and keeps the source in Cache\Scripts.db (kept in every mode),
    /// keyed by the archives' size and date so an updated mod is decompiled again. `--find` searches every line, `--show`
    /// prints one script, and `Query --scripts` attaches the store as `scr` (scr.Scripts, scr.Lines) to join records'
    /// scripts (Q vmad) to their source.
    /// </summary>
    public static class ScriptsCommand
    {
        public static string DbPath => Path.Combine(Sfs.HomeDir, "Cache", "Scripts.db");

        static string ChampollionPath()
        {
            string? env = Environment.GetEnvironmentVariable("SFSEARCH_CHAMPOLLION");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
            string? fromSettings = Service.ServiceConfig.Load(Sfs.HomeDir).Champollion;
            if (!string.IsNullOrWhiteSpace(fromSettings) && File.Exists(fromSettings)) return fromSettings;
            string inHome = Path.Combine(Sfs.HomeDir, "tools", "champollion", "Champollion.exe");
            return inHome; // when it's missing, the caller says where to put it
        }

        /// <summary>The plugin's compiled scripts: from its own archives (`<name> - *.ba2`, not textures or voices) and loose files
        /// under Data\Scripts whose path mentions the plugin's name. Shared with Sources --scripts.</summary>
        public static (List<(string Name, byte[] Bytes)> Pex, string[] Archives, int Loose, string Note) CollectPex(string pluginFile)
        {
            string dataDir = Program.GetDataFolderPathPublic();
            string stem = Path.GetFileNameWithoutExtension(pluginFile);
            var archives = Directory.GetFiles(dataDir, stem + " - *.ba2")
                .Where(a => !Regex.IsMatch(Path.GetFileName(a), @" - (textures|voices)", RegexOptions.IgnoreCase)).ToArray();
            var pex = new List<(string Name, byte[] Bytes)>();
            string note = "";
            foreach (var arch in archives)
            {
                try
                {
                    var reader = Mutagen.Bethesda.Archives.Archive.CreateReader(Mutagen.Bethesda.GameRelease.Starfield, arch);
                    foreach (var f in reader.Files)
                    {
                        string name = f.Path.ToString().Replace('/', '\\').TrimStart('\\');
                        if (name.EndsWith(".pex", StringComparison.OrdinalIgnoreCase)) pex.Add((name, f.GetBytes()));
                    }
                }
                catch (Exception ex) { note = $"Couldn't read {Path.GetFileName(arch)}: {ex.Message}"; }
            }
            int loose = 0;
            string looseDir = Path.Combine(dataDir, "Scripts");
            if (Directory.Exists(looseDir))
                foreach (var f in Directory.GetFiles(looseDir, "*.pex", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(dataDir, f);
                    if (rel.Contains(stem, StringComparison.OrdinalIgnoreCase)) { pex.Add((rel, File.ReadAllBytes(f))); loose++; }
                }
            return (pex, archives, loose, note);
        }

        /// <summary>Size and date of every source of the plugin's scripts: when it changes, they're decompiled again.</summary>
        static string StampOf(string pluginFile)
        {
            string dataDir = Program.GetDataFolderPathPublic();
            string stem = Path.GetFileNameWithoutExtension(pluginFile);
            var sb = new StringBuilder();
            foreach (var a in Directory.GetFiles(dataDir, stem + " - *.ba2").Where(a => !Regex.IsMatch(Path.GetFileName(a), @" - (textures|voices)", RegexOptions.IgnoreCase)).OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
            {
                var fi = new FileInfo(a);
                sb.Append(fi.Name).Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
            }
            string looseDir = Path.Combine(dataDir, "Scripts");
            if (Directory.Exists(looseDir))
                foreach (var f in Directory.GetFiles(looseDir, "*.pex", SearchOption.AllDirectories).Where(f => Path.GetRelativePath(dataDir, f).Contains(stem, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var fi = new FileInfo(f);
                    sb.Append(f).Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
                }
            return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
        }

        static SqliteConnection OpenRw()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
            var c = new SqliteConnection($"Data Source={DbPath};Pooling=False");
            c.Open();
            using var x = c.CreateCommand();
            x.CommandText = @"PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 60000;
                CREATE TABLE IF NOT EXISTS Sources (Plugin TEXT PRIMARY KEY, Stamp TEXT, Scripts INTEGER, Decompiled TEXT, Problem TEXT);
                CREATE TABLE IF NOT EXISTS Scripts (Plugin TEXT, Script TEXT, File TEXT, Lines INTEGER, Source TEXT, PRIMARY KEY (Plugin, Script));
                CREATE TABLE IF NOT EXISTS Lines (Plugin TEXT, Script TEXT, Line INTEGER, Text TEXT);
                CREATE INDEX IF NOT EXISTS idx_lines_script ON Lines(Plugin, Script);";
            x.ExecuteNonQuery();
            return c;
        }

        /// <summary>Makes sure the plugin's scripts are decompiled and stored. Returns how many it has.</summary>
        public static int Ensure(string plugin)
        {
            string stamp = StampOf(plugin);
            if (File.Exists(DbPath))
            {
                using var r = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Pooling=False");
                r.Open();
                using var q = r.CreateCommand();
                q.CommandText = "SELECT Scripts FROM Sources WHERE Plugin = @p AND Stamp = @s";
                q.Parameters.AddWithValue("@p", plugin); q.Parameters.AddWithValue("@s", stamp);
                try { if (q.ExecuteScalar() is long n) return (int)n; } catch (SqliteException) { }
            }
            using var lk = new Service.ServiceDb.BuildLock(DbPath + "." + plugin.ToLowerInvariant());
            var clock = Stopwatch.StartNew();
            var (pex, archives, loose, note) = CollectPex(plugin);
            var sources = new List<(string Script, string File, string Text)>();
            string? problem = note.Length > 0 ? note : null;
            if (pex.Count > 0)
            {
                string champ = ChampollionPath();
                if (!File.Exists(champ)) throw new FileNotFoundException($"Champollion (the Papyrus decompiler) wasn't found. Put Champollion.exe in {Path.Combine(Sfs.HomeDir, "tools", "champollion")}, or set its path in sfsearch.json (\"champollion\"). Download: https://github.com/Orvid/Champollion/releases");
                if (pex.Count > 200) Console.Error.WriteLine($"[scripts] decompiling {pex.Count} scripts of {plugin} (once; this can take a few minutes)...");
                string work = Path.Combine(Path.GetTempPath(), "sfsearch_scripts", $"{Environment.ProcessId}_{DateTime.Now.Ticks}");
                string input = Path.Combine(work, "pex"), output = Path.Combine(work, "psc");
                try
                {
                    foreach (var (name, bytes) in pex)
                    {
                        string rel = Regex.Replace(name, @"^(data\\)?scripts\\", "", RegexOptions.IgnoreCase);
                        string dest = Path.Combine(input, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.WriteAllBytes(dest, bytes);
                    }
                    Directory.CreateDirectory(output);
                    var psi = new ProcessStartInfo(champ) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
                    foreach (var a in new[] { input, "-r", "-s", "--no-debug-line", "-t", "-p", output }) psi.ArgumentList.Add(a);
                    using (var p = Process.Start(psi)!)
                    {
                        p.StandardInput.Close();
                        var so = p.StandardOutput.ReadToEndAsync();
                        var se = p.StandardError.ReadToEndAsync();
                        if (!p.WaitForExit(15 * 60 * 1000)) { try { p.Kill(true); } catch (Exception) { } throw new TimeoutException("Champollion took longer than 15 minutes."); }
                        so.Wait(); se.Wait();
                    }
                    foreach (var f in Directory.GetFiles(output, "*.psc", SearchOption.AllDirectories))
                    {
                        string text = File.ReadAllText(f);
                        var m = Regex.Match(text, @"^\s*ScriptName\s+([\w:]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                        string script = m.Success ? m.Groups[1].Value : Path.GetFileNameWithoutExtension(f);
                        sources.Add((script, Path.GetRelativePath(output, f), text));
                    }
                    if (sources.Count < pex.Count) problem = (problem == null ? "" : problem + "; ") + $"Champollion decompiled {sources.Count} of {pex.Count} scripts";
                }
                finally { try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
            using var c = OpenRw();
            using var tx = c.BeginTransaction();
            void Exec(string sql, params (string, object?)[] ps) { using var x = c.CreateCommand(); x.Transaction = tx; x.CommandText = sql; foreach (var (n, v) in ps) x.Parameters.AddWithValue(n, v ?? DBNull.Value); x.ExecuteNonQuery(); }
            Exec("DELETE FROM Lines WHERE Plugin = @p", ("@p", plugin));
            Exec("DELETE FROM Scripts WHERE Plugin = @p", ("@p", plugin));
            using (var ins = c.CreateCommand())
            using (var line = c.CreateCommand())
            {
                ins.Transaction = tx; line.Transaction = tx;
                ins.CommandText = "INSERT OR REPLACE INTO Scripts VALUES (@p, @s, @f, @n, @t)";
                line.CommandText = "INSERT INTO Lines VALUES (@p, @s, @n, @t)";
                var ip = new[] { "@p", "@s", "@f", "@n", "@t" }.Select(n => ins.Parameters.Add(n, SqliteType.Text)).ToArray();
                var lp = new[] { "@p", "@s", "@n", "@t" }.Select(n => line.Parameters.Add(n, SqliteType.Text)).ToArray();
                foreach (var (script, file, text) in sources)
                {
                    var lines = text.Replace("\r\n", "\n").Split('\n');
                    ip[0].Value = plugin; ip[1].Value = script; ip[2].Value = file; ip[3].Value = lines.Length; ip[4].Value = text;
                    ins.ExecuteNonQuery();
                    lp[0].Value = plugin; lp[1].Value = script;
                    for (int i = 0; i < lines.Length; i++) { lp[2].Value = i + 1; lp[3].Value = lines[i]; line.ExecuteNonQuery(); }
                }
            }
            Exec("INSERT OR REPLACE INTO Sources VALUES (@p, @s, @n, @d, @x)", ("@p", plugin), ("@s", stamp), ("@n", sources.Count), ("@d", DateTime.Now.ToString("yyyy-MM-dd HH:mm")), ("@x", problem));
            tx.Commit();
            if (pex.Count > 0) Console.Error.WriteLine($"[scripts] {plugin}: {sources.Count} script(s) decompiled from {(archives.Length > 0 ? string.Join(", ", archives.Select(Path.GetFileName)) : "")}{(loose > 0 ? $"{(archives.Length > 0 ? " and " : "")}{loose} loose file(s)" : "")} in {clock.Elapsed.TotalSeconds:0.0} s{(problem != null ? $" ({problem})" : "")}.");
            return sources.Count;
        }

        public static void Run(List<string> args)
        {
            string? find = Sfs.TakeOption(args, "--find");
            string? show = Sfs.TakeOption(args, "--show");
            if (args.Count < 1)
            {
                Console.WriteLine("Usage: SFSearch Scripts <plugin[,plugin2]> [--find <text>] [--show <ScriptName>]");
                return;
            }
            var plugins = args[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            foreach (var p in plugins)
            {
                if (!File.Exists(Path.Combine(Program.GetDataFolderPathPublic(), p))) { Console.WriteLine($"Error: {p} isn't in the Data folder."); Environment.ExitCode = 1; return; }
                Ensure(p);
            }
            using var c = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Pooling=False");
            c.Open();
            string inList = string.Join(",", plugins.Select((_, i) => $"@p{i}"));
            void AddPlugins(SqliteCommand x) { for (int i = 0; i < plugins.Count; i++) x.Parameters.AddWithValue($"@p{i}", plugins[i]); }
            if (show != null)
            {
                using var x = c.CreateCommand();
                x.CommandText = $"SELECT Plugin, Script, Source FROM Scripts WHERE Plugin IN ({inList}) AND (Script = @s COLLATE NOCASE OR Script LIKE '%:' || @s COLLATE NOCASE) LIMIT 5";
                AddPlugins(x); x.Parameters.AddWithValue("@s", show);
                using var r = x.ExecuteReader();
                bool any = false;
                while (r.Read()) { any = true; Sfs.Say($"### {r.GetString(1)} ({r.GetString(0)})\n```papyrus\n{r.GetString(2).TrimEnd()}\n```\n"); }
                if (!any) Console.WriteLine($"No script named {show} in {string.Join(", ", plugins)}.");
                return;
            }
            if (find != null)
            {
                using var x = c.CreateCommand();
                x.CommandText = $"SELECT Plugin, Script, Line, Text FROM Lines WHERE Plugin IN ({inList}) AND Text LIKE '%' || @f || '%' ORDER BY Plugin, Script, Line LIMIT 1001";
                AddPlugins(x); x.Parameters.AddWithValue("@f", find);
                var t = new Sfs.Table("Plugin", "Script", "Line", "Text");
                using var r = x.ExecuteReader();
                while (r.Read()) t.Add(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3).Trim());
                if (t.Rows.Count == 0) { Sfs.Say($"No line of {string.Join(", ", plugins)}'s scripts contains \"{find}\"."); return; }
                bool more = t.Rows.Count > 1000;
                if (more) t.Rows.RemoveAt(t.Rows.Count - 1);
                Sfs.Print(t, annotate: false);
                Sfs.Say($"\n{t.Rows.Count}{(more ? "+ (first 1000 shown)" : "")} line(s) contain \"{find}\".");
                return;
            }
            {
                using var x = c.CreateCommand();
                x.CommandText = $"SELECT s.Plugin, s.Script, s.Lines, s.File FROM Scripts s WHERE s.Plugin IN ({inList}) ORDER BY s.Plugin, s.Script";
                AddPlugins(x);
                var t = new Sfs.Table("Plugin", "Script", "Lines", "File");
                using var r = x.ExecuteReader();
                while (r.Read()) t.Add(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3));
                if (t.Rows.Count == 0) { Sfs.Say($"{string.Join(", ", plugins)}: no scripts found in its archives or loose Data\\Scripts."); return; }
                Sfs.Print(t, annotate: false);
                Sfs.Say($"\n{t.Rows.Count} script(s). --find <text> searches every line; --show <ScriptName> prints one; Query --scripts joins them in SQL (scr.Scripts, scr.Lines).");
            }
        }

        /// <summary>Query --scripts: decompile the query's plugins' scripts if needed and attach the store as `scr`.</summary>
        public static void Attach(SqliteConnection conn, IEnumerable<string> plugins)
        {
            foreach (var p in plugins)
                if (File.Exists(Path.Combine(Program.GetDataFolderPathPublic(), p)))
                    try { Ensure(p); } catch (Exception e) { Console.Error.WriteLine($"[scripts] {p}: {e.Message}"); }
            if (!File.Exists(DbPath)) using (OpenRw()) { }
            using var x = conn.CreateCommand();
            x.CommandText = "ATTACH DATABASE @p AS scr";
            x.Parameters.AddWithValue("@p", DbPath);
            x.ExecuteNonQuery();
        }
    }
}
