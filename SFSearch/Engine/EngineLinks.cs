using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Strings;
using SFSearch.Index;
using SFSearch.Records;
using SFSearch.Service;

namespace SFSearch.Engine
{
    /// <summary>
    /// The link index of the engine (design\agent-first\ARCHITECTURE.md section 5; milestone M5, second half): one
    /// <see cref="LinkFile"/> per plugin in indexes\links under the home (J31: kept between service starts), named by the
    /// plugin's reading key, made by the indexer lane while no agent waits. A plugin whose file isn't there yet is answered by a search of its bytes, so the index is
    /// never needed for a right answer: it only makes "what points at this" instant.
    /// </summary>
    public static class EngineLinks
    {
        static readonly object _gate = new();
        static readonly Dictionary<Key128, LinkFile> _open = new();
        static string? _builderKey;

        /// <summary>indexes\links under the home: kept between service starts unless keepStartupIndexes is false.</summary>
        public static string Folder => Path.Combine(Home.Indexes, "links");

        /// <summary>
        /// Eight digits that change with anything that changes what a walk finds: this build of SFSearch, Mutagen, the
        /// schema, the file format. A link file made by another build is never read: its name differs.
        /// </summary>
        public static string BuilderKey => _builderKey ??= Wire.Hash(string.Join("|",
            typeof(EngineLinks).Module.ModuleVersionId, typeof(Mutagen.Bethesda.Starfield.IWeaponGetter).Assembly.ManifestModule.ModuleVersionId,
            typeof(RecordEngine).Module.ModuleVersionId, SchemaTypes.Default.Version, LinkFile.FormatVersion))[..8].ToLowerInvariant();

        /// <summary>
        /// What names a plugin's link file: its reading key, and for a localized plugin the stamp of its string files
        /// too, since the file keeps the records' names and those are read from the string files (GAPS S15). A changed
        /// string file so gives the plugin a new link file; the old one is let go like a changed plugin's.
        /// </summary>
        public static Key128 KeyOf(IndexedPlugin p)
        {
            if (!p.Strings.Stamped || p.ReadingKey.IsZero) return p.ReadingKey;
            var both = System.IO.Hashing.XxHash128.HashToUInt128(System.Text.Encoding.UTF8.GetBytes(p.ReadingKey + "/strings/" + p.Strings.Key));
            return new Key128((ulong)(both >> 64), (ulong)both);
        }

        public static string PathOf(IndexedPlugin p) => LinkFile.PathFor(Folder, KeyOf(p), BuilderKey);

        /// <summary>The plugin's link file, mapped, or null when it hasn't been made yet.</summary>
        public static LinkFile? For(IndexedPlugin p)
        {
            if (p.Segment == null || p.ReadingKey.IsZero) return null;
            lock (_gate)
            {
                var key = KeyOf(p);
                if (_open.TryGetValue(key, out var f)) return f;
                f = LinkFile.TryOpen(PathOf(p), key);
                if (f != null) _open[key] = f;
                return f;
            }
        }

        /// <summary>Lets go of the link files of plugins the index no longer has (changed or removed).</summary>
        public static void Trim(HeaderIndex ix)
        {
            lock (_gate)
            {
                if (_open.Count == 0) return;
                var now = new HashSet<Key128>(ix.Plugins.Select(KeyOf));
                var letGo = _open.Keys.Where(k => !now.Contains(k)).ToList();
                bool keep = letGo.Count > 0 && EngineIndex.KeepsRemovedParts(); // keepRemovedPluginIndexMB (J43): KeptParts trims them
                foreach (var key in letGo)
                {
                    string path = _open[key].Path;
                    _open[key].Dispose();
                    _open.Remove(key);
                    if (keep) continue;
                    try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } // another worker may still map it
                }
            }
        }

        /// <summary>
        /// Deletes link files no plugin of this index can use: those another build made (their name ends with another
        /// builder key), at once, and those of plugins that changed, once they are over an hour old (another worker may
        /// still map one), or at once when they were written before the service started (<paramref name="unusedBefore"/>,
        /// the start's full check: J31). With keepRemovedPluginIndexMB (J43) this build's unused link files stay: the
        /// start's and the hourly check trim them to that size (<see cref="KeptParts"/>).
        /// </summary>
        public static (int Files, long Bytes) RemoveOldFiles(HeaderIndex ix, DateTime? unusedBefore = null)
        {
            bool keep = EngineIndex.KeepsRemovedParts();
            int removed = 0; long bytes = 0;
            try
            {
                var used = new HashSet<string>(ix.Plugins.Where(p => !p.ReadingKey.IsZero).Select(p => Path.GetFileName(PathOf(p))), StringComparer.OrdinalIgnoreCase);
                string mine = $"-{BuilderKey}.links";
                if (!Directory.Exists(Folder)) return (0, 0);
                // Link files, and what a walk that was stopped while it wrote one left behind
                foreach (var f in Directory.EnumerateFiles(Folder, "*.links").Concat(Directory.EnumerateFiles(Folder, "*.tmp")))
                {
                    string name = Path.GetFileName(f);
                    if (used.Contains(name)) continue;
                    var written = File.GetLastWriteTimeUtc(f);
                    bool ours = name.EndsWith(mine, StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
                    if (keep && name.EndsWith(mine, StringComparison.OrdinalIgnoreCase)) continue;
                    if (ours && !(unusedBefore is DateTime start && written < start) && DateTime.UtcNow - written < TimeSpan.FromHours(1)) continue;
                    try { long size = new FileInfo(f).Length; File.Delete(f); removed++; bytes += size; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return (removed, bytes);
        }

        /// <summary>Lets go of every link file this program has open (after `indexes drop` deleted them).</summary>
        public static void CloseAll()
        {
            lock (_gate)
            {
                foreach (var f in _open.Values) f.Dispose();
                _open.Clear();
            }
        }

        /// <summary>May this plugin's records be walked at all? Not when its FormIDs can't be read.</summary>
        public static bool Walkable(IndexedPlugin p) => p.Segment != null && p.Header != null && ProblemNotes.Blocking(p) == null;

        // ---------------------------------------------------------------- The build
        public sealed class BuildReport
        {
            public int Built, AlreadyThere, NotWalkable, Left, Changed;
            public long Records, Links, Unread, Names, Foreign, Bytes;
            public double Seconds;
            public long PeakMB;
            public bool Yielded;
            public List<string> Problems = new();
        }

        sealed class PluginJob
        {
            public required IndexedPlugin Plugin;
            public int UnitsLeft, Units;
            public bool Incomplete;
            public readonly List<UnitResult> Results = new();
            public int WorldRecords; public long WorldBytes; public int Walkable;
            public Stopwatch Clock = new();
        }

        sealed class UnitResult
        {
            public List<LinkRow> Rows = new();
            public List<string> Fields = new();
            public List<int> Unread = new();
            public List<(int Entry, string Name)> Names = new();
            public string? Why;
            public long Foreign;
        }

        sealed record Unit(PluginJob Job, int[] Entries);

        /// <summary>
        /// From how many records a walk runs on the workstation garbage collector (J58, GAPS S85): about 4 s of walking,
        /// where the server collector's cost (about a fifth of the walk) is more than starting a worker for it.
        /// </summary>
        public const long WorkstationWalkRecords = 1_000_000;

        /// <summary>The records a build would walk now: those outside world data of the plugins without a link file (or of the plugins named).</summary>
        public static long RecordsToWalk(HeaderIndex ix, Func<IndexedPlugin, bool>? only)
        {
            long n = 0;
            foreach (var p in ix.Plugins)
            {
                if (only != null && !only(p)) continue;
                if (!Walkable(p) || File.Exists(PathOf(p))) continue;
                n += Math.Max(0, p.Segment!.FirstUnresolved - WorldTypes.Totals(p.Segment).Records);
            }
            return n;
        }

        /// <summary>Records a unit of work holds at most: a big plugin is cut into units, each read through an overlay of its own.</summary>
        const int UnitRecords = 40_000;
        /// <summary>The walk collects its garbage when .NET's heap is past this.</summary>
        const long HeapLimitBytes = 900L << 20;

        /// <summary>
        /// Makes the link file of every plugin that has none (or of the plugins named). Plugins are walked side by side,
        /// a big one in units of 40,000 records, each unit by one thread through a session of its own. Mutagen allows one
        /// overlay to be read from several threads (C179, fixes.14), but one session shared by the threads of a plugin was
        /// slower: Starfield.esm's 434,970 records took 11.8 s against 7.9 s with a session per unit, at the same peak
        /// memory (2.2 GB) and to the same link file (fix-c-sfsearch-side, 2026-11-15). A plugin's file is written when
        /// all of it is walked and the plugin's stamp still matches. When <paramref name="stop"/> turns true (an agent is
        /// waiting) the walk ends within a moment: the files written stay, and the plugins that weren't finished are walked
        /// from their start the next time.
        /// </summary>
        public static BuildReport Build(EngineIndex eng, Func<bool> stop, int threads = 0, Func<IndexedPlugin, bool>? only = null, Language language = Language.English)
        {
            var clock = Stopwatch.StartNew();
            var report = new BuildReport();
            var ix = eng.Index;
            var schema = EngineRecords.Schema;
            Directory.CreateDirectory(Folder);
            // One program at a time walks: a second one (an agent's own "__links build" beside the indexer lane's) waits,
            // then finds the files made. It still gives way while it waits.
            using var one = new Mutex(false, @"Local\sfsearch_links_" + Wire.Hash(Folder));
            bool held = false;
            try
            {
                while (!held)
                {
                    try { held = one.WaitOne(200); } catch (AbandonedMutexException) { held = true; }
                    if (!held && stop())
                    {
                        report.Yielded = true;
                        report.Left = ix.Plugins.Count(p => Walkable(p) && (only == null || only(p)) && !File.Exists(PathOf(p)));
                        return report;
                    }
                }
                return BuildLocked(eng, stop, threads, only, language, report, clock);
            }
            finally { if (held) one.ReleaseMutex(); }
        }

        static BuildReport BuildLocked(EngineIndex eng, Func<bool> stop, int threads, Func<IndexedPlugin, bool>? only, Language language, BuildReport report, Stopwatch clock)
        {
            var ix = eng.Index;
            var schema = EngineRecords.Schema;
            RemoveOldFiles(ix);
            // The cores less 2, 2 to 8 (J37: the developer switch SFSEARCH_LINK_THREADS that set it went, by the user's decision)
            if (threads <= 0) threads = Math.Clamp(Environment.ProcessorCount - 2, 2, 8);

            var units = new List<Unit>();
            // Small plugins first: they are done within the first seconds, and a walk that then has to give way keeps
            // them. The few big ones come last, their units side by side.
            foreach (var p in ix.Plugins.OrderBy(p => p.Stamp.Length))
            {
                if (only != null && !only(p)) continue;
                if (!Walkable(p)) { report.NotWalkable++; continue; }
                if (File.Exists(PathOf(p))) { report.AlreadyThere++; continue; }
                var seg = p.Segment!;
                var world = WorldTypes.Of(seg);
                var job = new PluginJob { Plugin = p };
                (job.WorldRecords, job.WorldBytes) = WorldTypes.Totals(seg);
                // Its records outside the world groups, by type and then FormKey, so that a unit reads few groups
                var entries = new List<int>(Math.Max(0, seg.Count - job.WorldRecords));
                for (int i = 0; i < seg.FirstUnresolved; i++) if (!world[seg[i].Signature]) entries.Add(i);
                entries.Sort((a, b) => { int c = seg[a].Signature.CompareTo(seg[b].Signature); return c != 0 ? c : a.CompareTo(b); });
                job.Walkable = entries.Count;
                int n = Math.Max(1, (entries.Count + UnitRecords - 1) / UnitRecords);
                job.Units = job.UnitsLeft = n;
                for (int u = 0; u < n; u++)
                {
                    int from = (int)((long)entries.Count * u / n), to = (int)((long)entries.Count * (u + 1) / n);
                    units.Add(new Unit(job, entries.GetRange(from, to - from).ToArray()));
                }
            }
            var jobs = units.Select(u => u.Job).Distinct().ToList();

            object collect = new();
            var queue = new ConcurrentQueue<Unit>(units);
            var done = new ConcurrentQueue<PluginJob>();
            string verify = Path.Combine(EngineRecords.ScratchRoot, "verify");
            void Worker()
            {
                while (!stop() && queue.TryDequeue(out var unit))
                {
                    var job = unit.Job;
                    lock (job) { if (!job.Clock.IsRunning) job.Clock.Start(); }
                    var result = new UnitResult();
                    result.Rows.Capacity = unit.Entries.Length * 5;
                    bool whole = WalkUnit(ix, schema, verify, eng.DataFolder, language, unit, result, stop);
                    lock (job)
                    {
                        if (whole) job.Results.Add(result); else job.Incomplete = true;
                        if (--job.UnitsLeft == 0) done.Enqueue(job);
                    }
                    // What a unit read is garbage now. .NET would let it pile up for a while: collect when the heap has
                    // grown past the limit, so the walk stays far under what a worker may hold.
                    if (GC.GetTotalMemory(false) > HeapLimitBytes && Monitor.TryEnter(collect))
                    {
                        try { GC.Collect(2, GCCollectionMode.Forced, true, false); }
                        finally { Monitor.Exit(collect); }
                    }
                }
            }
            var pool = new List<Thread>();
            for (int i = 0; i < Math.Min(threads, Math.Max(1, units.Count)); i++)
            {
                var th = new Thread(Worker, 16 << 20) { IsBackground = true, Name = "link walk " + i, Priority = ThreadPriority.BelowNormal };
                pool.Add(th); th.Start();
            }
            // Finished plugins are written as they come, so a walk that is stopped keeps what it finished
            void Drain()
            {
                while (done.TryDequeue(out var job))
                {
                    if (job.Incomplete) continue;
                    try { WriteJob(job, report, language); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { report.Problems.Add($"{job.Plugin.Name}: its link file couldn't be written ({e.Message.TrimEnd('.')})"); }
                }
            }
            while (pool.Any(th => th.IsAlive)) { Drain(); Thread.Sleep(20); }
            Drain();

            report.Yielded = stop();
            report.Left = jobs.Count(j => !File.Exists(PathOf(j.Plugin)));
            report.Seconds = clock.Elapsed.TotalSeconds;
            if (report.Built > 0)
            {
                // The walk holds a lot for a moment: give it back now (twice, as the header index's build does)
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                GC.WaitForPendingFinalizers();
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            }
            using (var me = Process.GetCurrentProcess()) report.PeakMB = me.PeakWorkingSet64 / 1048576;
            return report;
        }

        /// <summary>Walks one unit's records. False when it was stopped before the end.</summary>
        static bool WalkUnit(HeaderIndex ix, RecordSchema schema, string verify, string data, Language language, Unit unit, UnitResult result, Func<bool> stop)
        {
            var p = unit.Job.Plugin;
            var seg = p.Segment!;
            // Each record is read from its own bytes where its type allows (all but OMOD and GMST, whose signature has
            // several Mutagen types): an overlay keeps every group it has touched, and eight overlays of Starfield.esm
            // side by side held 5 GB. A record of any size is walked: the walk makes no JSON, so it needs no extra memory.
            using var engine = new RecordEngine(ix, schema, verify, data, language) { PreferDirect = true, AllowHeavy = true }; // (J37: the developer switch SFSEARCH_LINK_DIRECT=0 that turned the direct read off went)
            var sink = RecordLinks.Collector();
            var owners = new Dictionary<ModKey, int>();
            PluginSession session;
            try { session = engine.Sessions.Get(p.Name); }
            catch (RecordException e)
            {
                // The plugin can't be opened at all: every record of the unit goes to the byte search
                result.Unread.AddRange(unit.Entries);
                result.Why = e.Message;
                return true;
            }
            int n = 0;
            foreach (int entry in unit.Entries)
            {
                if ((n++ & 63) == 0 && stop()) return false;
                try
                {
                    var (record, type) = Read(engine, session, schema, p, entry);
                    RecordLinks.Walk(record, sink);
                    foreach (var (field, target) in sink.Links)
                    {
                        if (!owners.TryGetValue(target.ModKey, out int owner)) owners[target.ModKey] = owner = seg.OwnerOf(target.ModKey.FileName.String);
                        if (owner < 0) { result.Foreign++; continue; }
                        result.Rows.Add(new LinkRow((ulong)owner << 24 | target.ID, (uint)entry, (uint)field));
                    }
                    if (RecordLinks.NameOf(record) is { } name) result.Names.Add((entry, name));
                }
                catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
                {
                    result.Unread.Add(entry);
                    result.Why ??= $"{ix.Describe(new RecordRef(p.Id, entry)).Label}: {e.Message.Split('\n')[0]}";
                }
            }
            result.Fields = sink.Paths.ToList();
            return true;
        }

        /// <summary>False once Mutagen has shown that a record whose signature has several types can't be read from its own bytes.</summary>
        static volatile bool _directMany = true;

        /// <summary>
        /// One record for the walk. A signature with several Mutagen types (OMOD has 8, GMST has 5) is read from its own
        /// bytes too, through the abstract class they share: Mutagen's own reader of that class picks the type. The
        /// record layer reads those through their group, which an overlay then keeps (180 MB for one mod's OMODs, once
        /// for every thread that meets one).
        /// </summary>
        static (object Record, string Type) Read(RecordEngine engine, PluginSession session, RecordSchema schema, IndexedPlugin p, int entry)
        {
            var seg = p.Segment!;
            var e = seg[entry];
            var types = schema.TypesOf(seg.SignatureOf(e));
            if (types.Count > 1 && _directMany && engine.PreferDirect && schema.Model.Objects.TryGetValue(types[0], out var def) && def.Base is { Length: > 0 } shared)
            {
                try
                {
                    object obj = session.ReadDirect(shared, e.Offset, false, out _);
                    return (obj, RecordSerializer.TypeName(obj));
                }
                catch (RecordException ex) when (ex.Message.Contains("can't be read on its own", StringComparison.Ordinal) || ex.Message.Contains("has no type named", StringComparison.Ordinal)) { _directMany = false; }
            }
            var rec = engine.Load(new RecordRef(p.Id, entry));
            return (rec.Record, rec.Type);
        }

        static void WriteJob(PluginJob job, BuildReport report, Language language)
        {
            var p = job.Plugin;
            // Rule 5 of the design's section 6: a plugin that changed while it was walked isn't trusted
            QuickStamp now;
            try { now = QuickStamp.Read(p.Path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { now = default; }
            if (now != p.Stamp) { report.Changed++; report.Problems.Add($"{p.Name} changed while it was walked: it is walked again the next time"); return; }

            var fields = new List<string>();
            var fieldIds = new Dictionary<string, int>(StringComparer.Ordinal);
            long total = job.Results.Sum(r => (long)r.Rows.Count);
            var rows = new LinkRow[total];
            int at = 0;
            var content = new LinkFileContent { Fields = fields, Walked = job.Walkable, WorldRecords = job.WorldRecords, WorldBytes = job.WorldBytes, Language = language.ToString() };
            var unread = new List<int>();
            foreach (var r in job.Results)
            {
                var map = new int[r.Fields.Count];
                for (int i = 0; i < map.Length; i++)
                {
                    if (!fieldIds.TryGetValue(r.Fields[i], out int id)) { id = fields.Count; fields.Add(r.Fields[i]); fieldIds[r.Fields[i]] = id; }
                    map[i] = id;
                }
                foreach (var row in r.Rows) rows[at++] = new LinkRow(row.Target, row.From, (uint)map[row.Field]);
                unread.AddRange(r.Unread);
                content.Names.AddRange(r.Names);
                if (r.Why != null && content.UnreadWhy.Length == 0) content.UnreadWhy = r.Why;
                report.Foreign += r.Foreign;
            }
            Array.Sort(rows, (a, b) => a.Target != b.Target ? a.Target.CompareTo(b.Target) : a.From != b.From ? a.From.CompareTo(b.From) : a.Field.CompareTo(b.Field));
            unread.Sort();
            content.Names.Sort((a, b) => a.Entry.CompareTo(b.Entry));
            content.Links = rows; content.Unread = unread.ToArray();
            content.Seconds = job.Clock.Elapsed.TotalSeconds;
            string path = PathOf(p);
            LinkFile.Write(path, KeyOf(p), content);
            report.Built++;
            report.Records += job.Walkable - unread.Count; report.Links += rows.Length; report.Unread += unread.Count; report.Names += content.Names.Count;
            try { report.Bytes += new FileInfo(path).Length; } catch (IOException) { }
            if (unread.Count > 0) report.Problems.Add($"{p.Name}: {unread.Count:N0} record(s) couldn't be read through Mutagen (a link search reads their bytes). The first: {content.UnreadWhy}");
            job.Results.Clear();
        }

        /// <summary>How many plugins have a link file now, and how much there is.</summary>
        public static (int Have, int Could, long Links, long Bytes, long Names) Totals(HeaderIndex ix)
        {
            int have = 0, could = 0; long links = 0, bytes = 0, names = 0;
            foreach (var p in ix.Plugins)
            {
                if (!Walkable(p)) continue;
                could++;
                if (For(p) is not { } f) continue;
                have++; links += f.Count; bytes += f.FileBytes; names += f.NameCount;
            }
            return (have, could, links, bytes, names);
        }
    }
}
