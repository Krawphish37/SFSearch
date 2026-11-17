using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mutagen.Bethesda;

namespace SFSearch
{
    /// <summary>
    /// Where SFSearch's files are: its home folder (or SFSEARCH_HOME), the caller's working folder, the game's Data folder,
    /// the scratch folder, the kept folder and the small state files. Every tool, the service and the add-on API take these
    /// from here (moved out of the older commands' code when those were removed, design\agent-first\notes\remove-old-commands.md).
    /// </summary>
    public static class Home
    {
        // ---------------------------------------------------------------- The home and the caller's folder
        /// <summary>The caller's working folder, taken before the program's own is set to the home.</summary>
        public static string CallerCwd { get; private set; } = Directory.GetCurrentDirectory();

        /// <summary>SFSearch's home: SFSEARCH_HOME, else the folder with SFSearch.csproj above the program.</summary>
        public static string Dir { get; private set; } = Directory.GetCurrentDirectory();

        /// <summary>Finds the home and makes it the program's working folder, so relative files of SFSearch's own resolve the same from anywhere.</summary>
        public static void Pin()
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
            Dir = home ?? CallerCwd;
            Directory.SetCurrentDirectory(Dir);
        }

        /// <summary>A path the user typed, relative to the folder they ran SFSearch from.</summary>
        public static string UserPath(string path) =>
            Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(CallerCwd, path));

        // ---------------------------------------------------------------- The Data folder
        static string? _data, _dataHome;

        /// <summary>
        /// Where Starfield's Data folder is. In order: the SFSEARCH_DATA environment variable, the path remembered by SetData or
        /// an earlier run (state\DataPath.txt; Cache\DataPath.txt of an earlier version is read when state\ has none), then
        /// Mutagen's search for the game (registry, Steam, Xbox).
        /// </summary>
        public static string DataFolder()
        {
            if (_data != null && _dataHome == Dir) return _data;
            string? remembered = ReadState("DataPath.txt")?.Trim();
            string? fromEnv = Environment.GetEnvironmentVariable("SFSEARCH_DATA");
            string found;
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                if (!Directory.Exists(fromEnv.Trim())) throw new FileNotFoundException($"SFSEARCH_DATA points at a folder that doesn't exist: {fromEnv}");
                found = fromEnv.Trim();
            }
            else if (!string.IsNullOrEmpty(remembered) && Directory.Exists(remembered)) found = remembered;
            else
            {
                if (!string.IsNullOrEmpty(remembered)) Console.Error.WriteLine($"The remembered Data folder is gone ({remembered}). Looking for Starfield again...");
                if (!Mutagen.Bethesda.Installs.GameLocations.TryGetDataFolder(GameRelease.Starfield, out var located) || !Directory.Exists(located.Path))
                    throw new FileNotFoundException("Couldn't find Starfield's Data folder. Tell SFSearch where it is, once:\n" +
                        "  sfs.cmd SetData \"C:\\Path\\To\\Starfield\\Data\"\n(or set the SFSEARCH_DATA environment variable to that folder).");
                found = located.Path;
                WriteState("DataPath.txt", found);
            }
            _dataHome = Dir;
            return _data = found;
        }

        /// <summary>
        /// For `Service config` (J43): the settings that live outside sfsearch.json, each with its value now and where it came
        /// from: the home (SFSEARCH_HOME), the Data folder (SFSEARCH_DATA, SetData, or found by itself) and the name this
        /// command line's commands carry in the service's status (SFSEARCH_AGENT). Nothing is looked for here: a Data folder
        /// not found yet says so.
        /// </summary>
        public static List<(string Setting, string InEffect, string From)> SettingsOutsideFile()
        {
            var rows = new List<(string, string, string)>();
            string? homeEnv = Environment.GetEnvironmentVariable("SFSEARCH_HOME");
            rows.Add(("SFSEARCH_HOME (the home)", Dir, !string.IsNullOrWhiteSpace(homeEnv) && Directory.Exists(homeEnv) ? "set by SFSEARCH_HOME" : "the default: the SFSearch folder the program is built in"));
            string? dataEnv = Environment.GetEnvironmentVariable("SFSEARCH_DATA");
            string? remembered = ReadState("DataPath.txt")?.Trim();
            rows.Add(("SFSEARCH_DATA (the Data folder)",
                !string.IsNullOrWhiteSpace(dataEnv) ? dataEnv.Trim() : !string.IsNullOrEmpty(remembered) ? remembered : "(not looked for yet)",
                !string.IsNullOrWhiteSpace(dataEnv) ? "set by SFSEARCH_DATA"
                : !string.IsNullOrEmpty(remembered) ? "the default: remembered in state/DataPath.txt (by SetData, or found by itself the first time)"
                : "the default: found by itself (Steam, Xbox, the registry) by the first command that needs it"));
            string? agent = Environment.GetEnvironmentVariable("SFSEARCH_AGENT");
            rows.Add(("SFSEARCH_AGENT", string.IsNullOrEmpty(agent) ? "cli" : agent, string.IsNullOrEmpty(agent) ? "the default: cli" : "set by SFSEARCH_AGENT"));
            return rows;
        }

        /// <summary>The SetData command: remember the Data folder in state\DataPath.txt.</summary>
        public static void SetDataFolder(string path)
        {
            string full = UserPath(path.Trim().Trim('"'));
            if (!Directory.Exists(full)) { Console.Error.WriteLine($"That folder doesn't exist: {full}"); Environment.ExitCode = 2; return; }
            if (!File.Exists(Path.Combine(full, "Starfield.esm")))
                Console.Error.WriteLine($"Warning: no Starfield.esm in {full}. Saved anyway; check this is the game's Data folder.");
            WriteState("DataPath.txt", full);
            _data = full; _dataHome = Dir;
            Console.WriteLine($"Data folder set to: {full}. If the SFSearch service is running, restart it: sfs.cmd Service restart");
        }

        /// <summary>
        /// Splits "a.esm,b.esm" into plugins, keeping a comma that is part of a file's name or folder: pieces that join up into
        /// an existing file (a path, or a name in Data) stay one entry, longest match first.
        /// </summary>
        public static List<string> SplitPluginList(string input)
        {
            var pieces = input.Split(',');
            var result = new List<string>();
            string dataPath = DataFolder();
            for (int i = 0; i < pieces.Length; i++)
            {
                int end = i;
                for (int j = pieces.Length - 1; j > i; j--)
                {
                    string joined = string.Join(",", pieces[i..(j + 1)]).Trim();
                    if (File.Exists(UserPath(joined)) || File.Exists(Path.Combine(dataPath, joined))) { end = j; break; }
                }
                string entry = string.Join(",", pieces[i..(end + 1)]).Trim();
                if (entry.Length > 0) result.Add(entry);
                i = end;
            }
            return result;
        }

        // ---------------------------------------------------------------- Scratch and kept folders
        static string? _marked;

        /// <summary>
        /// The home's scratch folder: working files that are only good until the service starts again (SQL tables,
        /// round-trip copies, verify files). The kept indexes are in <see cref="Indexes"/>. In a service worker the coordinator made it and emptied it at start. A
        /// plain run makes it itself, with the marker that tells the service the folder is SFSearch's own.
        /// </summary>
        public static string Scratch
        {
            get
            {
                string? root = Service.JobContext.ScratchDir;
                if (root != null) return root;
                root = Service.ServiceWorker.ScratchFolder(Dir);
                if (_marked == root && Directory.Exists(root)) return root;
                try
                {
                    Directory.CreateDirectory(root);
                    string marker = Path.Combine(root, Service.ServiceHost.ScratchMarker);
                    if (!File.Exists(marker) && !Directory.EnumerateFileSystemEntries(root).Any())
                        File.WriteAllText(marker, "SFSearch's scratch folder. The service empties it every time it starts: don't keep anything here.\n");
                    _marked = root;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return root;
            }
        }

        /// <summary>kept\ under the home: what outlives a service start (the reports' history and decompiled scripts).</summary>
        public static string Kept => Path.Combine(Dir, "kept");

        /// <summary>The marker file that says the indexes folder is SFSearch's own: only then does anything delete in it.</summary>
        public const string IndexesMarker = ".sfsearch-indexes";
        public const string IndexesMarkerText = "SFSearch's kept indexes (header\\, links\\): made from the plugin files and kept between service starts. 'indexes' shows them; 'indexes drop all' deletes them. Don't keep anything else here.\n";
        static string? _indexesMarked;

        /// <summary>
        /// indexes\ under the home (J31, 2026-11-15): the indexes SFSearch builds from the plugin files, kept between
        /// service starts and brought up to date plugin by plugin: header\ (the header index's segments) and links\ (the
        /// link index). Each part is named by its plugin's reading key, so a part of a changed plugin is never read. Only
        /// indexes are kept here, never record data. With keepStartupIndexes false in sfsearch.json the service empties
        /// header\ and links\ at every start. Made here with its marker, like the scratch folder.
        /// </summary>
        public static string Indexes
        {
            get
            {
                string root = Path.Combine(Dir, "indexes");
                if (_indexesMarked == root && Directory.Exists(root)) return root;
                try
                {
                    Directory.CreateDirectory(root);
                    string marker = Path.Combine(root, IndexesMarker);
                    if (!File.Exists(marker) && !Directory.EnumerateFileSystemEntries(root).Any()) File.WriteAllText(marker, IndexesMarkerText);
                    _indexesMarked = root;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return root;
            }
        }

        // ---------------------------------------------------------------- Small state files
        /// <summary>A small state file: state\name under the home.</summary>
        public static string StatePath(string name) => Path.Combine(Dir, "state", name);

        /// <summary>A state file's text: from state\, else from Cache\ where earlier versions kept it (read only, so the user's settings carry over). Null when neither is there.</summary>
        public static string? ReadState(string name)
        {
            foreach (var path in new[] { StatePath(name), Path.Combine(OldCacheFolder(Dir), name) })
            {
                try { if (File.Exists(path)) return File.ReadAllText(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }

        public static void WriteState(string name, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath(name))!);
            File.WriteAllText(StatePath(name), text);
        }

        // ---------------------------------------------------------------- What earlier versions left behind
        /// <summary>The two folders 1.x kept databases in. Never read, written or deleted.</summary>
        public static string OldCacheFolder(string home) => Path.Combine(home, "Cache");
        public static string OldServiceData(string home) => Path.Combine(home, "Service", "data");

        /// <summary>How much the two old database folders hold: bytes and files. Sizes only: no file is opened.</summary>
        public static (long Bytes, int Files) OldData(string home)
        {
            long bytes = 0; int files = 0;
            foreach (var (dir, pattern) in new[] { (OldCacheFolder(home), "*.db*"), (OldServiceData(home), "*") })
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in new DirectoryInfo(dir).EnumerateFiles(pattern)) { bytes += f.Length; files++; }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return (bytes, files);
        }

        /// <summary>One line about the old databases, or null when there are none.</summary>
        public static string? OldDataLine(string home)
        {
            var (bytes, files) = OldData(home);
            if (files == 0) return null;
            string size = bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : $"{Math.Max(1, bytes >> 20)} MB";
            return $"Old databases: {OldCacheFolder(home)} and {OldServiceData(home)} hold {size} in {files:N0} file(s) from earlier versions. SFSearch no longer reads or writes them, and never deletes them. What it still needs from Cache (the Data folder's path) is copied out by the first command, so both folders can be deleted when you like.";
        }

        static readonly object _notesGate = new();
        static HashSet<string>? _said;
        static string? _saidHome;

        /// <summary>True while a one-time note hasn't been said on this home. state\notes.txt remembers the ones that were;
        /// it is read again while a note is still open, since another worker may have said it meanwhile.</summary>
        static bool NotSaid(string note)
        {
            if (_said == null || _saidHome != Dir) { _said = new HashSet<string>(StringComparer.Ordinal); _saidHome = Dir; }
            if (_said.Contains(note)) return false;
            try
            {
                string file = StatePath("notes.txt");
                if (File.Exists(file)) foreach (var l in File.ReadAllLines(file)) _said.Add(l.Split('|')[0]);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return !_said.Contains(note);
        }

        static void Said(string note)
        {
            _said!.Add(note);
            try
            {
                string file = StatePath("notes.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.AppendAllText(file, $"{note}|{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>
        /// Once per home, with the first command's answer after this version is put in: the Data folder's path is copied out
        /// of Cache\ (when state\ has none), and one line says how much the old database folders hold and that they are no
        /// longer used. Nothing is said, and nothing remembered, while neither folder is there.
        /// </summary>
        public static void OldDataNote()
        {
            lock (_notesGate)
            {
                if (_said != null && _saidHome == Dir && _said.Contains("old-databases") && _said.Contains("carried")) return;
                if (!Directory.Exists(OldCacheFolder(Dir)) && !Directory.Exists(OldServiceData(Dir))) return;
                if (NotSaid("carried"))
                {
                    try
                    {
                        string old = Path.Combine(OldCacheFolder(Dir), "DataPath.txt");
                        if (!File.Exists(StatePath("DataPath.txt")) && File.Exists(old)) WriteState("DataPath.txt", File.ReadAllText(old));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    Said("carried");
                }
                if (!NotSaid("old-databases") || OldDataLine(Dir) is not { } line) return;
                Said("old-databases");
                Console.Error.WriteLine("[sfs] " + line + " (Said once; 'Service status' shows it too.)");
            }
        }
    }
}
