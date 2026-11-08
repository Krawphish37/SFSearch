using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace SFSearch
{
    /// <summary>
    /// Archive: what's inside BA2 archives. A plugin's own archives (&lt;name&gt; - *.ba2), one .ba2, or every archive in Data
    /// (all). Lists, finds and shows files, extracts them outside the Data folder, and notes loose copies in Data that the
    /// game uses instead of the archived file. Never writes to the Data folder.
    /// </summary>
    public static class ArchiveCommand
    {
        const string Usage = "Usage: SFSearch Archive <plugin | file.ba2 | all> [--list [glob]] [--find text|glob] [--show <path>] [--extract <path|glob> --to <folder>] [--all]";
        static readonly HashSet<string> TextKinds = new(StringComparer.OrdinalIgnoreCase)
            { ".mat", ".json", ".txt", ".psc", ".xml", ".ini", ".csv", ".cfg", ".lst", ".html", ".css", ".js", ".bat", ".log", ".md" };

        public static void Run(List<string> args)
        {
            bool all = Sfs.TakeFlag(args, "--all");
            string? find = Sfs.TakeOption(args, "--find");
            string? show = Sfs.TakeOption(args, "--show");
            string? extract = Sfs.TakeOption(args, "--extract");
            string? to = Sfs.TakeOption(args, "--to");
            bool list = Sfs.TakeFlag(args, "--list");
            if (args.Count < 1) { Console.WriteLine(Usage); return; }
            string? listGlob = list && args.Count > 1 ? args[1] : null;

            string dataDir = Program.GetDataFolderPathPublic();
            var archives = Resolve(args[0], dataDir);
            if (archives.Count == 0) { Console.WriteLine($"Error: no archives for '{args[0]}' (a plugin's archives are named \"<plugin name> - <part>.ba2\" in {dataDir})."); return; }

            if (show != null) { Show(archives, show, dataDir); return; }
            if (extract != null) { Extract(archives, extract, to, dataDir); return; }
            if (find != null || list) { ListFiles(archives, find ?? listGlob, dataDir, all); return; }
            Summary(archives, dataDir);
        }

        internal static List<string> Resolve(string target, string dataDir)
        {
            if (target.Equals("all", StringComparison.OrdinalIgnoreCase) || target == "*")
                return Directory.GetFiles(dataDir, "*.ba2").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (target.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase))
            {
                string p = File.Exists(Sfs.ResolveUserPath(target)) ? Sfs.ResolveUserPath(target) : Path.Combine(dataDir, target);
                return File.Exists(p) ? new List<string> { p } : new List<string>();
            }
            string stem = Path.GetFileNameWithoutExtension(target);
            return Directory.GetFiles(dataDir, stem + " - *.ba2").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static IEnumerable<(string Archive, IArchiveFile File, string Path)> Files(List<string> archives)
        {
            foreach (var a in archives)
            {
                IArchiveReader reader;
                try { reader = Archive.CreateReader(GameRelease.Starfield, a); }
                catch (Exception e) { Console.Error.WriteLine($"[sfs] Couldn't read {Path.GetFileName(a)}: {e.Message}"); continue; }
                foreach (var f in reader.Files)
                    yield return (Path.GetFileName(a), f, f.Path.ToString().Trim().Replace('/', '\\').TrimStart('\\'));
            }
        }

        static Regex Matcher(string? pattern) =>
            pattern == null ? new Regex(".") :
            pattern.Contains('*') || pattern.Contains('?') ? Sfs.GlobRegex(pattern.Replace('/', '\\')) :
            new Regex(Regex.Escape(pattern.Replace('/', '\\')), RegexOptions.IgnoreCase);

        static void Summary(List<string> archives, string dataDir)
        {
            Sfs.Say($"### Archives ({archives.Count})");
            var t = new Sfs.Table("Archive", "Files", "Size (MB)", "Kinds");
            foreach (var a in archives)
            {
                var files = Files(new List<string> { a }).ToList();
                var kinds = files.GroupBy(f => Path.GetExtension(f.Path).ToLowerInvariant()).OrderByDescending(g => g.Count()).Take(6)
                    .Select(g => $"{(g.Key.Length == 0 ? "(none)" : g.Key)} {g.Count()}");
                t.Add(Path.GetFileName(a), files.Count, (new FileInfo(a).Length / 1048576.0).ToString("0.0"), string.Join(", ", kinds));
            }
            Sfs.Print(t, annotate: false);
            Sfs.Say("\n`--list [glob]` lists files, `--find text` finds them, `--show <path>` prints a text file, `--extract <path|glob> --to <folder>` copies files out.");
        }

        static void ListFiles(List<string> archives, string? pattern, string dataDir, bool all)
        {
            var rx = Matcher(pattern);
            var t = new Sfs.Table("Archive", "File", "Size (KB)", "Loose copy in Data");
            int shown = 0, total = 0;
            foreach (var (arch, f, p) in Files(archives))
            {
                if (!rx.IsMatch(p)) continue;
                total++;
                if (!all && shown >= 500) continue;
                shown++;
                t.Add(arch, p, p.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? "" : (f.Size / 1024.0).ToString("0.#"),  // texture entries report a wrong size
                      File.Exists(Path.Combine(dataDir, p)) ? "YES (the game uses the loose file)" : "");
            }
            Sfs.Say($"### {(pattern == null ? "Files" : $"Files matching '{pattern}'")} in {archives.Count} archive(s): {total}");
            if (total > 0) Sfs.Print(t, annotate: false);
            if (shown < total) Sfs.Say($"_… {total - shown} more; add --all, or narrow the pattern._");
        }

        static (string Archive, IArchiveFile File, string Path)? FindOne(List<string> archives, string path)
        {
            string want = path.Replace('/', '\\').TrimStart('\\');
            foreach (var x in Files(archives))
                if (x.Path.Equals(want, StringComparison.OrdinalIgnoreCase)) return x;
            return null;
        }

        static void Show(List<string> archives, string path, string dataDir)
        {
            var hit = FindOne(archives, path);
            if (hit == null) { Console.WriteLine($"Error: '{path}' isn't in {string.Join(", ", archives.Select(Path.GetFileName))}. Use --find to look for it."); return; }
            var (arch, f, p) = hit.Value;
            byte[] bytes = f.GetBytes();
            Sfs.Say($"### {p} ({arch}, {bytes.Length:N0} bytes){(File.Exists(Path.Combine(dataDir, p)) ? " — a loose copy in Data is used instead" : "")}");
            bool text = TextKinds.Contains(Path.GetExtension(p)) || LooksLikeText(bytes);
            if (!text) { Sfs.Say("It's a binary file. Copy it out with `--extract` to open it in another tool."); return; }
            const int Limit = 200_000;
            string s = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, Limit));
            Console.WriteLine("```");
            Console.WriteLine(s.TrimEnd());
            Console.WriteLine("```");
            if (bytes.Length > Limit) Sfs.Say($"_… cut at {Limit:N0} bytes; extract it for the rest._");
        }

        static bool LooksLikeText(byte[] b)
        {
            int n = Math.Min(b.Length, 4096), bad = 0;
            for (int i = 0; i < n; i++) if (b[i] == 0 || (b[i] < 9)) bad++;
            return n > 0 && bad == 0;
        }

        static void Extract(List<string> archives, string pattern, string? to, string dataDir)
        {
            if (string.IsNullOrWhiteSpace(to)) { Console.WriteLine("Error: say where to put the files: --extract <path|glob> --to <folder>."); return; }
            string dest = Path.GetFullPath(Sfs.ResolveUserPath(to));
            string data = Path.GetFullPath(dataDir).TrimEnd('\\') + "\\";
            if ((dest.TrimEnd('\\') + "\\").StartsWith(data, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Error: SFSearch never writes into the Data folder. Pick a folder outside {dataDir}.");
                return;
            }
            var rx = pattern.Contains('*') || pattern.Contains('?') ? Matcher(pattern) : new Regex("^" + Regex.Escape(pattern.Replace('/', '\\').TrimStart('\\')) + "$", RegexOptions.IgnoreCase);
            int n = 0; long bytes = 0;
            foreach (var (arch, f, p) in Files(archives))
            {
                if (!rx.IsMatch(p)) continue;
                string outPath = Path.Combine(dest, p);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                var b = f.GetBytes();
                File.WriteAllBytes(outPath, b);
                n++; bytes += b.Length;
            }
            Sfs.Say(n == 0 ? $"No file matches '{pattern}'." : $"Extracted {n:N0} file(s), {bytes / 1048576.0:0.0} MB, to {dest} (their archive folders kept).");
        }
    }
}
