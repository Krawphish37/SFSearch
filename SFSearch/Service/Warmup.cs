using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SFSearch.Service
{
    /// <summary>
    /// Warm-up (2026-11-07): when no agent has asked anything for a while, the coordinator has a worker read plugins ahead,
    /// so the first question about them is answered at once. What to read is in sfsearch.json (`warmup.plugins`, `types`):
    /// plugin names, patterns, and @base (Starfield.esm, ShatteredSpace.esm), @official (every loaded Bethesda file), @recent
    /// (the plugins agents asked about most recently; the coordinator passes them, newest first) and @loaded (every loaded
    /// plugin). The default, @base and @recent, suits anyone's load order.
    /// `__warmup plan [recent...]` brings the load-order index up to date and lists the plugins; `__warmup <plugin>` reads one.
    /// </summary>
    public static class Warmup
    {
        static readonly string[] BaseFiles = { "Starfield.esm", "ShatteredSpace.esm" };

        public static void Run(List<string> args)
        {
            if (args.Count == 0) return;
            var cfg = ServiceConfig.Load(Sfs.HomeDir).Warmup;
            if (args[0].Equals("plan", StringComparison.OrdinalIgnoreCase))
            {
                LoadOrderIndex.Ensure(); // labels and load-order questions need it; built now rather than while an agent waits
                var listing = LoadOrderIndex.Listing().Where(p => p.Stamp != "").ToList();
                var inData = listing.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
                var recent = args.Skip(1).ToList();
                var targets = new List<string>();
                foreach (var entry in cfg.Plugins)
                {
                    string e = entry.Trim();
                    IEnumerable<string> names = e.ToLowerInvariant() switch
                    {
                        "@base" => BaseFiles,
                        "@official" => listing.Where(p => p.Enabled && LoadOrderIndex.IsOfficial(p.Name)).Select(p => p.Name),
                        "@recent" => recent,
                        "@loaded" => listing.Where(p => p.Enabled).Select(p => p.Name),
                        _ when e.Contains('*') || e.Contains('?') =>
                            listing.Select(p => p.Name).Where(n => Regex.IsMatch(n, "^" + Regex.Escape(e).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase)),
                        _ => new[] { e },
                    };
                    foreach (var n in names)
                        if (inData.TryGetValue(n, out var p) && !targets.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) targets.Add(p.Name);
                }
                foreach (var t in targets) Console.WriteLine("warm: " + t);
                return;
            }
            string plugin = args[0];
            var types = new HashSet<string>(cfg.Types.Where(t => Program.AllStoredTypes.Contains(t)), StringComparer.OrdinalIgnoreCase);
            if (types.Count == 0 && !cfg.AllRecordsList) return;
            LiveDb.Forced = new LiveDb.Needs { Records = types.Count > 0, Forms = cfg.AllRecordsList, Types = types };
            try { Program.EnsureCacheUpToDatePublic(plugin); }
            finally { LiveDb.Forced = null; }
            Console.WriteLine("warmed " + plugin);
        }
    }
}
