using System.Reflection;
using System.Runtime.Loader;
using System.Text;

namespace SFSearch
{
    /// <summary>
    /// A set of extra commands kept outside SFSearch: lookups and checks for one mod, or research you run often. Build a
    /// class library that references SFSearch.csproj, implement this, and put the .dll in SFSearch's <c>extensions</c>
    /// folder: its commands then run like SFSearch's own, through <c>sfs.cmd</c>, the service and the MCP tool
    /// <c>sfs_run</c>. Name the file <c>SFSearch.&lt;ModFamily&gt;.dll</c>; its commands can then also be called as
    /// <c>&lt;ModFamily&gt;.&lt;Command&gt;</c>. Start from <c>extensions\SFSearch.Example</c>.
    /// </summary>
    public interface ISfsExtension
    {
        /// <summary>Each command's name (matched ignoring case) and its line for <c>--help</c>.</summary>
        IReadOnlyList<(string Name, string Help)> Commands { get; }

        /// <summary>Runs one of the commands. <paramref name="args"/> holds what follows the command name, global options removed.</summary>
        void Run(string command, List<string> args);
    }

    /// <summary>Finds and runs the add-ons in <c>&lt;home&gt;\extensions\*.dll</c>.</summary>
    public static class Extensions
    {
        public static string Folder => Path.Combine(Sfs.HomeDir, "extensions");

        /// <summary>The add-on API version: SFSearch's own version. An add-on built against a different first number isn't loaded.</summary>
        public static Version ApiVersion => typeof(Extensions).Assembly.GetName().Version ?? new Version(1, 0);

        /// <summary>One add-on file: its name (the file name without "SFSearch."), what it was built against, and its command sets
        /// (empty when it couldn't be loaded; <see cref="Problem"/> says why).</summary>
        public sealed record AddOn(string File, string Name, DateTime Stamp, long Size, Version? BuiltFor, List<ISfsExtension> Sets, string? Problem)
        {
            public IEnumerable<(string Name, string Help)> Commands => Sets.SelectMany(s => s.Commands);
        }

        static readonly Dictionary<string, AddOn> _cache = new(StringComparer.OrdinalIgnoreCase);
        static readonly object _gate = new();

        static string NameOf(string dll)
        {
            string n = Path.GetFileNameWithoutExtension(dll);
            return n.StartsWith("SFSearch.", StringComparison.OrdinalIgnoreCase) ? n["SFSearch.".Length..] : n;
        }

        /// <summary>
        /// Every add-on in the folder. Each .dll is read from memory (so it can be rebuilt while the service runs) into a
        /// context of its own, and read again when the file changes. SFSearch, Mutagen and the rest resolve to the copies
        /// already loaded.
        /// </summary>
        public static List<AddOn> All()
        {
            var result = new List<AddOn>();
            if (!Directory.Exists(Folder)) return result;
            lock (_gate)
            {
                foreach (var dll in Directory.GetFiles(Folder, "*.dll").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var fi = new FileInfo(dll);
                    if (!_cache.TryGetValue(dll, out var hit) || hit.Stamp != fi.LastWriteTimeUtc || hit.Size != fi.Length)
                        _cache[dll] = hit = Load(fi);
                    result.Add(hit);
                }
            }
            return result;
        }

        static AddOn Load(FileInfo fi)
        {
            var sets = new List<ISfsExtension>();
            Version? builtFor = null;
            string? problem = null;
            try
            {
                var ctx = new AssemblyLoadContext($"sfs-ext:{fi.Name}:{fi.LastWriteTimeUtc.Ticks}", isCollectible: true);
                using var ms = new MemoryStream(File.ReadAllBytes(fi.FullName));
                var asm = ctx.LoadFromStream(ms);
                builtFor = asm.GetReferencedAssemblies().FirstOrDefault(a => a.Name == "SFSearch")?.Version;
                if (builtFor != null && builtFor.Major != ApiVersion.Major)
                    problem = $"built for SFSearch {builtFor.ToString(3)}, and this is SFSearch {ApiVersion.ToString(3)}: the add-on API changed in between. Rebuild it against this SFSearch.";
                else
                    foreach (var t in asm.GetTypes().Where(t => typeof(ISfsExtension).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }))
                        sets.Add((ISfsExtension)Activator.CreateInstance(t)!);
                if (problem == null && sets.Count == 0) problem = "it has no ISfsExtension class.";
            }
            catch (Exception e) when (e is ReflectionTypeLoadException or BadImageFormatException or FileLoadException or TypeLoadException or MissingMethodException or IOException)
            {
                problem = $"it couldn't be loaded ({e.GetType().Name}: {e.Message}). Rebuild it against this SFSearch.";
            }
            if (problem != null) Console.Error.WriteLine($"[sfs] WARNING: the add-on {fi.Name} isn't used: {problem}");
            return new AddOn(fi.FullName, NameOf(fi.FullName), fi.LastWriteTimeUtc, fi.Length, builtFor, problem == null ? sets : new(), problem);
        }

        /// <summary>
        /// Runs <paramref name="command"/> if an add-on has it: either its full name (<c>DarkStar.Lint</c>) or a short name only
        /// one add-on has. False when none has it. A short name two add-ons share is refused with their full names.
        /// </summary>
        public static bool TryRun(string command, List<string> args)
        {
            var all = All();
            var matches = new List<(AddOn AddOn, ISfsExtension Set, string Name)>();
            int dot = command.IndexOf('.');
            foreach (var a in all)
                foreach (var s in a.Sets)
                    foreach (var c in s.Commands)
                    {
                        bool full = dot > 0 && command[..dot].Equals(a.Name, StringComparison.OrdinalIgnoreCase) && command[(dot + 1)..].Equals(c.Name, StringComparison.OrdinalIgnoreCase);
                        if (full || c.Name.Equals(command, StringComparison.OrdinalIgnoreCase)) matches.Add((a, s, c.Name));
                    }
            if (matches.Count == 0) return false;
            if (matches.Select(m => m.AddOn.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            {
                Console.WriteLine($"Error: more than one add-on has a '{command}' command: {string.Join(", ", matches.Select(m => $"{m.AddOn.Name}.{m.Name}"))}. Use the full name.");
                return true;
            }
            var (addOn, set, name) = matches[0];
            try { set.Run(name, args); }
            catch (Exception e) when (e is MissingMethodException or MissingFieldException or TypeLoadException)
            {
                Console.WriteLine($"Error: the add-on {Path.GetFileName(addOn.File)} was built against a different SFSearch ({e.Message}). Rebuild it.");
            }
            return true;
        }

        /// <summary>The add-ons' lines for <c>--help</c>, one heading per add-on.</summary>
        public static IEnumerable<string> HelpLines()
        {
            foreach (var a in All().Where(a => a.Sets.Count > 0))
            {
                yield return "";
                yield return $"Add-on {a.Name} ({Path.GetFileName(a.File)}; full names {a.Name}.<Command>):";
                foreach (var c in a.Commands) yield return c.Help;
            }
        }

        /// <summary>The Extensions command: every add-on, what it was built for, its commands.</summary>
        public static string Describe()
        {
            var all = All();
            var sb = new StringBuilder();
            sb.AppendLine($"SFSearch {ApiVersion.ToString(3)}. Add-ons in {Folder}: {all.Count}.");
            if (all.Count == 0)
            {
                sb.AppendLine("No add-ons. To make one, start from extensions\\SFSearch.Example (README: \"Your own commands\").");
                return sb.ToString();
            }
            foreach (var a in all)
            {
                sb.AppendLine();
                sb.AppendLine($"## {a.Name} ({Path.GetFileName(a.File)}, built for SFSearch {a.BuiltFor?.ToString(3) ?? "?"})");
                if (a.Problem != null) { sb.AppendLine($"Not used: {a.Problem}"); continue; }
                foreach (var c in a.Commands) sb.AppendLine(c.Help);
            }
            sb.AppendLine();
            sb.AppendLine("Run a command by its short name, or by its full name (<AddOn>.<Command>) when two add-ons share a name.");
            return sb.ToString();
        }

        /// <summary>One line for Service status, or null when there are no add-ons.</summary>
        public static string? Summary()
        {
            var all = All();
            if (all.Count == 0) return null;
            return "Add-ons: " + string.Join("; ", all.Select(a => a.Problem != null
                ? $"{a.Name} (not used: rebuild it)"
                : $"{a.Name} ({string.Join(", ", a.Commands.Select(c => c.Name))})")) + ". 'Extensions' lists them.";
        }
    }
}
