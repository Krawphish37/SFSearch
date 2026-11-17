using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SFSearch.Index;
using SFSearch.Service;

namespace SFSearch.Engine
{
    /// <summary>
    /// The kept index parts no plugin in Data uses now (J43, from J42/S71, 2026-11-15): segments and link files of a plugin
    /// taken out of Data, or of the copy a changed plugin replaced. With keepRemovedPluginIndexMB at 0 (the default) they are
    /// deleted as before (EngineIndex.RemoveUnusedSegments, EngineLinks.RemoveOldFiles). With a number they are kept up to
    /// that many MB, so a plugin put back (switching between mod lists) reuses its parts: its segment is found by its
    /// reading key, its link file by the same key, and neither is made again. <see cref="Trim"/> runs in the start's full
    /// check and the hourly one, in place of those two.
    /// Over the size, a plugin's parts go together, the plugin out of Data longest first (J60, GAPS S86): the full check
    /// writes, for every plugin it sees in Data, its segment's, link file's and world link file's names and the time
    /// (<see cref="ListFile"/>, indexes\header\parts.tsv), so a kept part's plugin was last in Data at its line's time. A
    /// link file doesn't name its plugin; that list is what pairs it with its segment.
    /// </summary>
    public static class KeptParts
    {
        /// <summary>
        /// One file no plugin in Data uses now. Group pairs a plugin's segment and link file (its segment's file name);
        /// LastInDataUtc is when the full check last saw that plugin in Data, or, for a file the list doesn't name (Listed
        /// false: made before the list, or after `indexes drop header`), when the file was written.
        /// </summary>
        public sealed record Unused(string Path, string Kind, long Bytes, DateTime WrittenUtc, string? Plugin, bool Readable, string Group, DateTime LastInDataUtc, bool Listed);

        /// <summary>One line of <see cref="ListFile"/>: a file of a plugin the full check saw in Data, the plugin, its segment's name, and when.</summary>
        sealed record Seen(string File, string Plugin, string Segment, DateTime LastInDataUtc);

        /// <summary>indexes\header\parts.tsv: the plugins the full check saw in Data, their parts' file names and when (J60).</summary>
        public static string ListFile(EngineIndex eng) => System.IO.Path.Combine(eng.Folder, "parts.tsv");

        static Dictionary<string, Seen> ReadList(EngineIndex eng)
        {
            var d = new Dictionary<string, Seen>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = ListFile(eng);
                if (!File.Exists(path)) return d;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                while (reader.ReadLine() is string line)
                {
                    if (line.StartsWith('#')) continue;
                    var c = line.Split('\t');
                    if (c.Length < 4 || !DateTime.TryParse(c[3], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)) continue;
                    d[c[0]] = new Seen(c[0], c[1], c[2], when);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return d;
        }

        /// <summary>
        /// The full check, when keepRemovedPluginIndexMB is set: every plugin in Data gets the time now on its segment's,
        /// link file's and world link file's (J32) lines; the lines of parts no plugin uses keep the time their plugin was last seen, until the
        /// file goes.
        /// </summary>
        public static void Record(EngineIndex eng, DateTime nowUtc)
        {
            var list = ReadList(eng);
            foreach (var p in eng.Index.Plugins)
            {
                if (p.Segment == null || p.ReadingKey.IsZero) continue;
                string seg = System.IO.Path.GetFileName(p.Segment.Path), links = System.IO.Path.GetFileName(EngineLinks.PathOf(p));
                list[seg] = new Seen(seg, p.Name, seg, nowUtc);
                list[links] = new Seen(links, p.Name, seg, nowUtc);
                string world = System.IO.Path.GetFileName(WorldLinks.PathOf(p));
                list[world] = new Seen(world, p.Name, seg, nowUtc);
            }
            // A file that is gone loses its line (a link file not made yet keeps it while its plugin is in Data)
            var sb = new System.Text.StringBuilder("# SFSearch: the index parts of the plugins the full check saw in Data, and when it last saw each (keepRemovedPluginIndexMB). File, plugin, its segment, last in Data (UTC).\n");
            foreach (var s in list.Values.OrderBy(s => s.Segment, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.File, StringComparer.OrdinalIgnoreCase))
            {
                string dir = s.File.EndsWith(".seg", StringComparison.OrdinalIgnoreCase) ? eng.Folder : s.File.EndsWith(".wlinks", StringComparison.OrdinalIgnoreCase) ? WorldLinks.Folder : EngineLinks.Folder;
                if (s.LastInDataUtc != nowUtc && !File.Exists(System.IO.Path.Combine(dir, s.File))) continue;
                sb.Append(s.File).Append('\t').Append(s.Plugin).Append('\t').Append(s.Segment).Append('\t').Append(s.LastInDataUtc.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
            }
            string path = ListFile(eng), tmp = path + ".tmp";
            try
            {
                Directory.CreateDirectory(eng.Folder);
                File.WriteAllText(tmp, sb.ToString());
                File.Move(tmp, path, overwrite: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>With keepRemovedPluginIndexMB 0 nothing is kept, so the list has nothing to order: it goes.</summary>
        public static void Forget(EngineIndex eng)
        {
            try { File.Delete(ListFile(eng)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>
        /// Every segment and link file no plugin of this index (or of a side index, a plugin outside Data) uses. A segment
        /// says which plugin it is of, and whether this SFSearch can read it (its reading key matches this version); a link
        /// file of another build is never readable. Files left by a stopped write (.tmp) aren't listed.
        /// </summary>
        public static List<Unused> List(EngineIndex eng)
        {
            var list = new List<Unused>();
            var seen = ReadList(eng);
            var usedSegs = eng.UsedSegmentPaths();
            try
            {
                if (Directory.Exists(eng.Folder))
                    foreach (var f in new DirectoryInfo(eng.Folder).EnumerateFiles("*.seg"))
                    {
                        if (usedSegs.Contains(f.FullName)) continue;
                        string? plugin = null; bool readable = false;
                        using (var s = Segment.TryOpen(f.FullName))
                            if (s != null) { plugin = s.Plugin; readable = s.ReadableBy(eng.VersionText); }
                        bool listed = seen.TryGetValue(f.Name, out var row);
                        list.Add(new Unused(f.FullName, "header", f.Length, f.LastWriteTimeUtc, plugin ?? row?.Plugin, readable, f.Name, listed ? row!.LastInDataUtc : f.LastWriteTimeUtc, listed));
                    }
                var usedLinks = new HashSet<string>(eng.Index.Plugins.Where(p => !p.ReadingKey.IsZero).Select(p => Path.GetFileName(EngineLinks.PathOf(p))), StringComparer.OrdinalIgnoreCase);
                string mine = $"-{EngineLinks.BuilderKey}.links";
                if (Directory.Exists(EngineLinks.Folder))
                    foreach (var f in new DirectoryInfo(EngineLinks.Folder).EnumerateFiles("*.links"))
                    {
                        if (usedLinks.Contains(f.Name)) continue;
                        // Paired with its segment by the list; one the list doesn't name pairs with the segment of the same
                        // reading key (a plugin without stamped string files: its link file is named by that key)
                        bool listed = seen.TryGetValue(f.Name, out var row);
                        int dash = f.Name.IndexOf('-');
                        string group = listed ? row!.Segment : dash > 0 ? f.Name[..dash] + ".seg" : f.Name;
                        list.Add(new Unused(f.FullName, "links", f.Length, f.LastWriteTimeUtc, row?.Plugin, f.Name.EndsWith(mine, StringComparison.OrdinalIgnoreCase), group, listed ? row!.LastInDataUtc : f.LastWriteTimeUtc, listed));
                    }
                // World link files (J32): named by the reading key alone
                var usedWorld = new HashSet<string>(eng.Index.Plugins.Where(p => !p.ReadingKey.IsZero).Select(p => Path.GetFileName(WorldLinks.PathOf(p))), StringComparer.OrdinalIgnoreCase);
                string mineWorld = $"-{WorldLinks.BuilderKey}.wlinks";
                if (Directory.Exists(WorldLinks.Folder))
                    foreach (var f in new DirectoryInfo(WorldLinks.Folder).EnumerateFiles("*.wlinks"))
                    {
                        if (usedWorld.Contains(f.Name)) continue;
                        // Paired with its segment by the list, or by its name: a world link file is named by the reading key alone
                        bool listed = seen.TryGetValue(f.Name, out var row);
                        int dash = f.Name.IndexOf('-');
                        string group = listed ? row!.Segment : dash > 0 ? f.Name[..dash] + ".seg" : f.Name;
                        list.Add(new Unused(f.FullName, "world", f.Length, f.LastWriteTimeUtc, row?.Plugin, f.Name.EndsWith(mineWorld, StringComparison.OrdinalIgnoreCase), group, listed ? row!.LastInDataUtc : f.LastWriteTimeUtc, listed));
                    }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return list;
        }

        /// <summary>
        /// The start's and the hourly check, when keepRemovedPluginIndexMB is set: deletes what no SFSearch of this build
        /// can read again (segments of another version, link files of another build, files a stopped write left), then
        /// the parts of the plugins out of Data longest, each plugin's segment, link file and world link file together, until they hold no
        /// more than the setting's MB (J60, GAPS S86). A plugin with a file written since the service started and less
        /// than an hour ago is left alone, as before (another worker may have just made it). Returns one line for the
        /// log (the sweep's answer).
        /// </summary>
        public static string Trim(EngineIndex eng, DateTime? unusedBefore, int capMB)
        {
            bool MayGo(DateTime written) => (unusedBefore is DateTime start && written < start) || DateTime.UtcNow - written >= TimeSpan.FromHours(1);
            int gone = 0; long goneBytes = 0;
            void Delete(string path, long bytes)
            {
                try { File.Delete(path); gone++; goneBytes += bytes; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            // What a stopped write left
            foreach (var dir in new[] { eng.Folder, EngineLinks.Folder, WorldLinks.Folder })
                try
                {
                    if (Directory.Exists(dir))
                        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.tmp"))
                            if (MayGo(f.LastWriteTimeUtc)) Delete(f.FullName, f.Length);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            var unused = List(eng);
            int dead = 0; long deadBytes = 0;
            foreach (var u in unused.Where(u => !u.Readable && (u.Kind is "links" or "world" || MayGo(u.WrittenUtc))))
            {
                int before = gone; Delete(u.Path, u.Bytes);
                if (gone > before) { dead++; deadBytes += u.Bytes; }
            }
            // Each plugin's parts together, the plugin out of Data longest first
            var kept = unused.Where(u => u.Readable && File.Exists(u.Path)).ToList();
            var plugins = kept.GroupBy(u => u.Group, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Files: g.ToList(), LastInData: g.Max(u => u.LastInDataUtc)))
                .OrderBy(g => g.LastInData).ToList();
            long cap = (long)capMB << 20, total = kept.Sum(u => u.Bytes);
            int dropped = 0; long droppedBytes = 0;
            var droppedNames = new List<string>();
            foreach (var (files, _) in plugins)
            {
                if (total <= cap) break;
                if (files.Any(u => !MayGo(u.WrittenUtc))) continue;
                int before = dropped;
                foreach (var u in files)
                {
                    int was = gone; Delete(u.Path, u.Bytes);
                    if (gone > was) { dropped++; droppedBytes += u.Bytes; total -= u.Bytes; }
                }
                if (dropped > before) droppedNames.Add(files.Select(u => u.Plugin).FirstOrDefault(n => n != null) ?? "a plugin SFSearch can't name");
            }
            int left = kept.Count - dropped;
            var line = $" Kept for plugins not in Data now (keepRemovedPluginIndexMB {capMB:N0}): {left:N0} file(s), {EngineIndex.Mb(total)}.";
            if (dropped > 0) line += $" Removed the parts of the plugins out of Data longest to stay under {capMB:N0} MB: {dropped:N0} file(s) of {droppedNames.Count:N0} plugin(s) ({Names(droppedNames)}), {EngineIndex.Mb(droppedBytes)}.";
            if (dead > 0) line += $" Removed as no SFSearch of this build can read them: {dead:N0} file(s), {EngineIndex.Mb(deadBytes)}.";
            return line;
        }

        static string Names(List<string> n) => string.Join(", ", n.Take(12)) + (n.Count > 12 ? $" and {n.Count - 12:N0} more" : "");

        /// <summary>For `indexes`: what is kept for plugins not in Data now, and the setting; null when nothing is.</summary>
        public static string? Describe(EngineIndex eng, ServiceConfig cfg)
        {
            var unused = List(eng);
            if (unused.Count == 0) return null;
            long bytes = unused.Sum(u => u.Bytes);
            if (!cfg.KeepsRemovedParts)
                return $"No plugin in Data uses {unused.Count:N0} of these files now ({EngineIndex.Mb(bytes)}): parts of plugins that were taken out or changed. They are deleted at the next service start (keepRemovedPluginIndexMB is 0 in sfsearch.json; a number of MB keeps them up to that size, so a plugin put back isn't indexed again).";
            var inData = eng.Index.Plugins.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var segs = unused.Where(u => u.Kind == "header" && u.Readable && u.Plugin != null).ToList();
            // Each plugin taken out, with when it was last in Data, the one out longest first (the order they go in)
            var removed = segs.Where(u => !inData.Contains(u.Plugin!)).GroupBy(u => u.Plugin!, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key, Last: g.Max(u => u.LastInDataUtc), Listed: g.Any(u => u.Listed))).OrderBy(g => g.Last)
                .Select(g => $"{g.Name} ({(g.Listed ? "last in Data" : "made")} {g.Last.ToLocalTime():yyyy-MM-dd HH:mm})").ToList();
            var older = segs.Where(u => inData.Contains(u.Plugin!)).Select(u => u.Plugin!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            int links = unused.Count(u => u.Kind == "links" && u.Readable);
            int worlds = unused.Count(u => u.Kind == "world" && u.Readable);
            int dead = unused.Count(u => !u.Readable);
            var parts = new List<string>();
            if (removed.Count > 0) parts.Add($"{removed.Count:N0} plugin(s) not in Data: {Names(removed)}");
            if (older.Count > 0) parts.Add($"older copies of {older.Count:N0} plugin(s) in Data: {Names(older)}");
            string what = parts.Count > 0 ? string.Join("; ", parts) : "no segment";
            return $"Kept for plugins not in Data now: {EngineIndex.Mb(unused.Where(u => u.Readable).Sum(u => u.Bytes))} of up to {cfg.KeepRemovedPluginIndexMB:N0} MB (keepRemovedPluginIndexMB in sfsearch.json): {segs.Count:N0} segment(s), of {what}, and {links:N0} link file(s){(worlds > 0 ? $" and {worlds:N0} world link file(s)" : "")}. A plugin put back in Data reuses its parts instead of being indexed again; when they pass the size, the parts of the plugin out of Data longest go first, its segment, link file and world link file together, at the service's start or its hourly check."
                + (dead > 0 ? $" {dead:N0} file(s) of another SFSearch build go at the next start." : "");
        }
    }
}
