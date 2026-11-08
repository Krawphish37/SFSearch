using System.Reflection;
using System.Runtime.Loader;

namespace SFSearch
{
    /// <summary>
    /// A set of extra commands kept outside SFSearch (checks for one mod, one-off research). Build a class library that
    /// references SFSearch.csproj, implement this, and put the .dll in SFSearch's <c>extensions</c> folder: its commands
    /// then run like SFSearch's own, through <c>sfs.cmd</c>, the service and the MCP tool <c>sfs_run</c>.
    /// </summary>
    public interface ISfsExtension
    {
        /// <summary>Each command's name (matched ignoring case) and its line for <c>--help</c>.</summary>
        IReadOnlyList<(string Name, string Help)> Commands { get; }

        /// <summary>Runs one of the commands. <paramref name="args"/> holds what follows the command name, global options removed.</summary>
        void Run(string command, List<string> args);
    }

    /// <summary>Finds and runs the extensions in <c>&lt;home&gt;\extensions\*.dll</c>.</summary>
    public static class Extensions
    {
        public static string Folder => Path.Combine(Sfs.HomeDir, "extensions");

        sealed record Loaded(string File, DateTime Stamp, long Size, List<ISfsExtension> Sets);
        static readonly Dictionary<string, Loaded> _cache = new(StringComparer.OrdinalIgnoreCase);
        static readonly object _gate = new();

        /// <summary>
        /// Each .dll is read from memory (so it can be rebuilt while the service runs) into a context of its own, and read
        /// again when the file changes. SFSearch, Mutagen and the rest resolve to the copies already loaded.
        /// </summary>
        static List<(ISfsExtension Ext, string File)> All()
        {
            var result = new List<(ISfsExtension, string)>();
            if (!Directory.Exists(Folder)) return result;
            lock (_gate)
            {
                foreach (var dll in Directory.GetFiles(Folder, "*.dll").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var fi = new FileInfo(dll);
                    if (!_cache.TryGetValue(dll, out var hit) || hit.Stamp != fi.LastWriteTimeUtc || hit.Size != fi.Length)
                    {
                        var sets = new List<ISfsExtension>();
                        try
                        {
                            var ctx = new AssemblyLoadContext($"sfs-ext:{fi.Name}:{fi.LastWriteTimeUtc.Ticks}", isCollectible: true);
                            using var ms = new MemoryStream(File.ReadAllBytes(dll));
                            var asm = ctx.LoadFromStream(ms);
                            foreach (var t in asm.GetTypes().Where(t => typeof(ISfsExtension).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }))
                                sets.Add((ISfsExtension)Activator.CreateInstance(t)!);
                        }
                        catch (Exception e) when (e is ReflectionTypeLoadException or BadImageFormatException or FileLoadException or TypeLoadException or MissingMethodException or IOException)
                        {
                            Console.Error.WriteLine($"[sfs] WARNING: the extension {fi.Name} couldn't be loaded ({e.GetType().Name}: {e.Message}). Rebuild it against this SFSearch, or take it out of {Folder}.");
                        }
                        _cache[dll] = hit = new Loaded(dll, fi.LastWriteTimeUtc, fi.Length, sets);
                    }
                    foreach (var s in hit.Sets) result.Add((s, dll));
                }
            }
            return result;
        }

        /// <summary>Runs <paramref name="command"/> if an extension has it. False when none does.</summary>
        public static bool TryRun(string command, List<string> args)
        {
            foreach (var (ext, file) in All())
            {
                if (!ext.Commands.Any(c => c.Name.Equals(command, StringComparison.OrdinalIgnoreCase))) continue;
                try { ext.Run(command, args); }
                catch (Exception e) when (e is MissingMethodException or MissingFieldException or TypeLoadException)
                {
                    Console.WriteLine($"Error: the extension {Path.GetFileName(file)} was built against a different SFSearch ({e.Message}). Rebuild it.");
                }
                return true;
            }
            return false;
        }

        /// <summary>The extensions' lines for <c>--help</c>, under a heading of their own.</summary>
        public static IEnumerable<string> HelpLines()
        {
            var all = All();
            if (all.Count == 0) yield break;
            yield return "";
            yield return "Extension Commands (from the extensions folder):";
            foreach (var (ext, _) in all)
                foreach (var c in ext.Commands) yield return c.Help;
        }
    }
}
