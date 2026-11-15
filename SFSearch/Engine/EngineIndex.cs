using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using SFSearch.Index;
using SFSearch.Service;

namespace SFSearch.Engine
{
    /// <summary>What the check before an answer found: how long it took and what had changed on disk.</summary>
    public sealed class Validity
    {
        /// <summary>This program had no index yet: segments were mapped (or made) for every plugin.</summary>
        public bool Cold;
        public double Ms;
        public RefreshReport Report = new();
        /// <summary>What changed since the last answer, in words, for the answer's notes.</summary>
        public List<string> Notes = new();
        /// <summary>What had to be read, for the cost line. Empty when every stamp matched.</summary>
        public List<string> Reads = new();
    }

    /// <summary>
    /// The engine's header index inside SFSearch (design\agent-first, milestones M2 and M5): one HeaderIndex per program,
    /// over the Data folder, with its segments in the scratch folder (scratch\index under the home). In the service every
    /// worker maps the same segment files; the indexer lane makes them when the service starts. Run with no service, the
    /// first use makes them and later runs map them again while each plugin's stamps still match.
    /// <see cref="Check"/> is called before every answer: it compares every plugin's quick stamp (size, date, a hash of
    /// its first and last 64 KB) by equality, never by "newer than", and reads again whatever changed.
    /// </summary>
    public sealed class EngineIndex : IDisposable
    {
        static EngineIndex? _current;
        static readonly object _gate = new();

        readonly string _key;
        bool _warm;
        readonly Dictionary<string, (string Key, HeaderIndex Ix)> _side = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<IndexedPlugin, IndexedPlugin?[]> _owners = new();

        public HeaderIndex Index { get; }
        public string DataFolder { get; }
        public string ScratchFolder { get; }
        public string? PluginsTxt { get; }
        public string VersionText { get; }
        /// <summary>The last check's result.</summary>
        public Validity Last { get; private set; } = new();

        EngineIndex(string key, string data, string scratch, string? pluginsTxt)
        {
            _key = key; DataFolder = data; ScratchFolder = scratch; PluginsTxt = pluginsTxt;
            VersionText = "sfsearch " + Extensions.DisplayVersion;
            Index = new HeaderIndex(PluginSource.Folder(data), new IndexOptions { ScratchFolder = scratch, VersionText = VersionText, PluginsTxt = pluginsTxt, StringFiles = new SFSearch.Records.StringFiles(data, scratch), Refusals = new SFSearch.Records.MutagenRefusals() });
        }

        /// <summary>The index of this program for the Data folder and home in use now. It is made on first use; call <see cref="Check"/> before reading it.</summary>
        public static EngineIndex Get()
        {
            string data = Path.GetFullPath(Home.DataFolder());
            string scratch = IndexFolder();
            string? txt = ServiceConfig.Load(Home.Dir).PluginsTxt is { Length: > 0 } custom ? custom : null;
            string key = data + "|" + scratch + "|" + txt;
            lock (_gate)
            {
                if (_current == null || !_current._key.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    _current?.Dispose();
                    _current = new EngineIndex(key, data, scratch, txt);
                }
                return _current;
            }
        }

        /// <summary>
        /// scratch\index under the home. In a service worker the coordinator made the scratch folder and emptied it at
        /// start. A plain run makes it itself, with the marker that tells the service the folder is SFSearch's own.
        /// </summary>
        public static string IndexFolder() => Path.Combine(Home.Scratch, "index");

        public void Dispose()
        {
            foreach (var s in _side.Values) s.Ix.Dispose();
            _side.Clear();
            Index.Dispose();
        }

        // ---------------------------------------------------------------- Validity
        /// <summary>
        /// Brings the index in line with the files and says what had changed. About 30 ms when nothing changed (every
        /// plugin's quick stamp is taken again). A plugin whose stamp differs is hashed and read again, a plugin whose
        /// master changed size is read again, and a changed plugins.txt reworks the winners.
        /// </summary>
        /// <param name="verifyContent">Hash every file again whatever its stamp says: the idle sweep.</param>
        public Validity Check(bool verifyContent = false)
        {
            var clock = Stopwatch.StartNew();
            var v = new Validity { Cold = !_warm };
            _owners.Clear();
            Dictionary<string, string>? before = v.Cold ? ManifestKeys() : null;
            if (v.Cold)
            {
                // One program at a time makes the segments: the others wait, then map what it made
                using var one = new Mutex(false, @"Local\sfsearch_index_" + Wire.Hash(ScratchFolder));
                bool held = false;
                try { held = one.WaitOne(TimeSpan.FromMinutes(3)); } catch (AbandonedMutexException) { held = true; }
                try { v.Report = Index.Refresh(verifyContent); }
                finally { if (held) one.ReleaseMutex(); }
            }
            else v.Report = Index.Refresh(verifyContent);
            _warm = true;
            var r = v.Report;

            if (v.Cold)
            {
                // What changed since the index was last brought up to date by anyone (the manifest in the scratch folder remembers)
                foreach (var name in r.Hashed)
                    if (Index.Plugin(name) is { } p && before!.TryGetValue(p.Path, out var old) && old != p.ContentKey.ToString())
                        v.Notes.Add($"{name} changed on disk since the header index was last used: it was read again.");
            }
            else
            {
                foreach (var name in r.ContentChanged) v.Notes.Add($"{name} changed on disk: it was read again.");
                foreach (var name in r.Added) v.Notes.Add($"{name} is new in Data: it was read.");
                foreach (var name in r.Removed) v.Notes.Add($"{name} is gone from Data.");
                foreach (var name in r.Rekeyed) v.Notes.Add($"{name} reads differently now: one of its masters changed size, came or went. Its FormIDs were worked out again.");
                foreach (var name in r.StringsChanged) v.Notes.Add($"{name}'s string files changed on disk ({r.StringsChanges.GetValueOrDefault(name, "")}): its texts are read from them as they are now.");
                if (r.LoadOrderChanged) v.Notes.Add("The load order changed (plugins.txt): the winners were worked out again.");
            }
            if (r.PassesRun.Count > 0)
            {
                v.Reads.Add(r.PassesRun.Count <= 3 ? "record headers of " + string.Join(", ", r.PassesRun) : $"record headers of {r.PassesRun.Count} plugins");
                // A pass over everything holds about 2 GB for a moment: give it back now, not at some later collection.
                // Twice, with the finalizers run between: one collection alone left 170 to 670 MB, from run to run.
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                GC.WaitForPendingFinalizers();
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            }
            v.Ms = clock.Elapsed.TotalMilliseconds;
            Last = v;
            return v;
        }

        /// <summary>
        /// Deletes segment files in the index folder that no plugin of this index uses and that are over an hour old
        /// (a changed plugin leaves its old segment behind while another program still maps it). A file still mapped
        /// somewhere can't be deleted and stays for the next time.
        /// </summary>
        public int RemoveUnusedSegments()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Index.Plugins) if (p.Segment != null) used.Add(Path.GetFullPath(p.Segment.Path));
            foreach (var s in _side.Values) foreach (var p in s.Ix.Plugins) if (p.Segment != null) used.Add(Path.GetFullPath(p.Segment.Path));
            int removed = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(ScratchFolder, "*.seg"))
                {
                    if (used.Contains(Path.GetFullPath(f)) || DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < TimeSpan.FromHours(1)) continue;
                    try { File.Delete(f); removed++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return removed;
        }

        /// <summary>Path to content key, as the scratch folder's stamps.tsv has them now.</summary>
        Dictionary<string, string> ManifestKeys()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(ScratchFolder, "stamps.tsv");
            try
            {
                if (!File.Exists(path)) return d;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                while (reader.ReadLine() is string line)
                {
                    var c = line.Split('\t');
                    if (c.Length == 5) d[c[0]] = c[4];
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return d;
        }

        // ---------------------------------------------------------------- Helpers the tools share
        /// <summary>The plugin an entry's owner number stands for, or null (the owner isn't in Data, or the FormID couldn't be resolved).</summary>
        public IndexedPlugin? Owner(HeaderIndex ix, IndexedPlugin p, int owner)
        {
            if (p.Segment == null || owner >= p.Segment.Names.Count) return null;
            if (!_owners.TryGetValue(p, out var map))
                _owners[p] = map = p.Segment.Names.Select(n => ix.Plugin(n)).ToArray();
            return owner == p.Segment.SelfOwner ? p : map[owner];
        }

        /// <summary>The order holders are listed in: loaded plugins by position, then the rest.</summary>
        public static long Rank(IndexedPlugin p) =>
            p.Order.Enabled ? p.Order.Position : (1L << 40) + (p.Order.Position >= 0 ? p.Order.Position : (1L << 30) + p.Id);

        public static string SizeName(MasterStyle s) => s switch { MasterStyle.Full => "Full", MasterStyle.Medium => "Medium", MasterStyle.Small => "Small", _ => "missing" };

        /// <summary>The highest local ID a plugin of this size can give its own records.</summary>
        public static uint MaxLocalId(MasterStyle s) => s switch { MasterStyle.Small => 0xFFF, MasterStyle.Medium => 0xFFFF, _ => 0xFFFFFF };

        /// <summary>The plugins in Data whose names are nearest to a name that isn't there, chosen as every wrong name's
        /// nearest ones are (<see cref="SFSearch.SchemaGen.NearNames"/>). Empty when none is near.</summary>
        public static List<string> NearPlugins(HeaderIndex ix, string name)
        {
            return SFSearch.SchemaGen.NearNames.Plugins(System.IO.Path.GetFileName(name.Trim()), ix.Plugins.Select(p => p.Name));
        }

        /// <summary>A size as every new tool writes it: the one function, <see cref="SFSearch.Records.Sizes.Text"/>.</summary>
        public static string Mb(long bytes) => SFSearch.Records.Sizes.Text(bytes);

        /// <summary>One plugin copy as a source: its size, date and the first 8 digits of its content key.</summary>
        public static SourceInfo Source(IndexedPlugin p, string? shownAs = null) => new()
        {
            Plugin = shownAs ?? p.Name, Bytes = p.Stamp.Length,
            Modified = p.Stamp.WriteTicksUtc > 0 ? new DateTime(p.Stamp.WriteTicksUtc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ") : "",
            Key = p.ContentKey.IsZero ? "" : p.ContentKey.Short,
            Strings = p.Strings.Stamped ? p.Strings.Short : null,
        };

        /// <summary>Every plugin in Data as one source: their number, their size together, and one key over all their content keys.</summary>
        public SourceInfo AllSource()
        {
            // A localized plugin's string files count too: they are where its texts are read from
            var keys = string.Join("", Index.Plugins.Select(p => p.ContentKey.ToString() + (p.Strings.Stamped ? "/" + p.Strings.Key : "")));
            return new SourceInfo { Plugin = $"all {Index.Plugins.Count} plugins in Data", Bytes = Index.Plugins.Sum(p => p.Stamp.Length), Key = Wire.Hash(keys)[..8].ToLowerInvariant() };
        }

        /// <summary>The load order as a source: plugins.txt's size and date, and a key over the loaded plugins and their slots.</summary>
        public SourceInfo LoadOrderSource()
        {
            string path = PluginsTxt ?? LoadOrder.DefaultPluginsTxt;
            var loaded = Index.LoadOrderEntries.Where(e => e.Enabled).ToList();
            var s = new SourceInfo { Plugin = $"load order ({loaded.Count} loaded, plugins.txt)", Key = Wire.Hash(string.Join("|", loaded.Select(e => e.Slot + "=" + e.Name)))[..8].ToLowerInvariant() };
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists) { s.Bytes = fi.Length; s.Modified = fi.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"); }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return s;
        }

        // ---------------------------------------------------------------- A plugin file outside Data
        /// <summary>
        /// A plugin given by path, outside Data: an index of its own over that file and the masters it lists (taken from
        /// Data, with their masters). It shares the scratch folder, so the masters' segments are the ones already made.
        /// The file isn't part of the load order. Null when the file isn't a plugin.
        /// </summary>
        public (HeaderIndex Ix, IndexedPlugin Plugin)? ByPath(string path)
        {
            path = Path.GetFullPath(path);
            string name = Path.GetFileName(path);
            var header = PluginHeader.Read(path);
            if (header == null) return null;
            var files = new List<string> { path };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
            var queue = new Queue<string>(header.Masters);
            while (queue.Count > 0)
            {
                string m = queue.Dequeue();
                if (!seen.Add(m)) continue;
                var mp = Index.Plugin(m);
                if (mp?.Header == null) continue;
                files.Add(mp.Path);
                foreach (var mm in mp.Header.Masters) queue.Enqueue(mm);
            }
            string key = string.Join("|", files);
            if (!_side.TryGetValue(path, out var side) || side.Key != key)
            {
                if (side.Ix != null) side.Ix.Dispose();
                side = (key, new HeaderIndex(PluginSource.FromFiles(files), new IndexOptions { ScratchFolder = ScratchFolder, VersionText = VersionText, PluginsTxt = "", Ccc = "", StringFiles = new SFSearch.Records.StringFiles(DataFolder, ScratchFolder), Refusals = new SFSearch.Records.MutagenRefusals() }));
                _side[path] = side;
            }
            side.Ix.Refresh();
            return side.Ix.Plugin(name) is { } p ? (side.Ix, p) : null;
        }
    }
}
