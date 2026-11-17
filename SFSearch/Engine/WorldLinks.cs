using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Mutagen.Bethesda.Strings;
using SFSearch.Index;
using SFSearch.Records;
using SFSearch.Service;

namespace SFSearch.Engine
{
    /// <summary>
    /// The world link index (J32; the J1 plan as an opt-in kept index, STEP1 4.6): what points at a record from world data
    /// (cells, placed objects and NPCs, navmeshes, the navmesh info map, surface trees, worldspaces), one
    /// <see cref="WorldLinkFile"/> per plugin in indexes\world. It is never built by itself: only for the plugins the
    /// user asked for (indexes build world [--in plugins]; the list is kept in indexes\world\wanted.txt). It is kept
    /// between service starts, and a wanted plugin that changed gets a new file at the next link job of the indexer lane
    /// (its old file is never read: the name is the plugin's reading key). refs uses it when it is there and searches the
    /// world data's bytes when it isn't; nothing else depends on it. It holds where links are, never record data.
    /// </summary>
    public static class WorldLinks
    {
        static readonly object _gate = new();
        static readonly Dictionary<Key128, WorldLinkFile> _open = new();
        static string? _builderKey;

        /// <summary>indexes\world under the home.</summary>
        public static string Folder => Path.Combine(Home.Indexes, "world");

        /// <summary>Eight digits that change with anything that changes what a build finds: the link index's builder key
        /// (this SFSearch, Mutagen, the schema), this file format and the byte readers.</summary>
        public static string BuilderKey => _builderKey ??= Wire.Hash($"{EngineLinks.BuilderKey}|world {WorldLinkFile.FormatVersion}|readers {WorldReader.Version}")[..8].ToLowerInvariant();

        public static string PathOf(IndexedPlugin p) => WorldLinkFile.PathFor(Folder, p.ReadingKey, BuilderKey);

        /// <summary>True when the plugin holds world records (only those plugins get a world link file).</summary>
        public static bool HasWorld(IndexedPlugin p) => p.Segment != null && WorldTypes.Totals(p.Segment).Records > 0;

        /// <summary>The plugin's world link file, mapped, or null when it hasn't been made (it is made only when asked).</summary>
        public static WorldLinkFile? For(IndexedPlugin p)
        {
            if (p.Segment == null || p.ReadingKey.IsZero) return null;
            lock (_gate)
            {
                if (_open.TryGetValue(p.ReadingKey, out var f)) return f;
                f = WorldLinkFile.TryOpen(PathOf(p), p.ReadingKey);
                if (f != null) _open[p.ReadingKey] = f;
                return f;
            }
        }

        /// <summary>Lets go of the world link files of plugins the index no longer has (changed or removed).</summary>
        public static void Trim(HeaderIndex ix)
        {
            lock (_gate)
            {
                if (_open.Count == 0) return;
                var now = new HashSet<Key128>(ix.Plugins.Select(p => p.ReadingKey));
                foreach (var key in _open.Keys.Where(k => !now.Contains(k)).ToList()) { _open[key].Dispose(); _open.Remove(key); }
            }
        }

        /// <summary>Lets go of every world link file this program has open (after `indexes drop` deleted them).</summary>
        public static void CloseAll()
        {
            lock (_gate)
            {
                foreach (var f in _open.Values) f.Dispose();
                _open.Clear();
            }
        }

        /// <summary>
        /// Deletes world link files and parts no plugin of this index can use: another build's at once; a changed
        /// plugin's once over an hour old, or at once when written before the service started (<paramref name="unusedBefore"/>,
        /// the start's full check). With keepRemovedPluginIndexMB (J43) this build's unused files stay (KeptParts trims them).
        /// </summary>
        public static (int Files, long Bytes) RemoveOldFiles(HeaderIndex ix, DateTime? unusedBefore = null)
        {
            bool keep = EngineIndex.KeepsRemovedParts();
            int removed = 0; long bytes = 0;
            try
            {
                if (!Directory.Exists(Folder)) return (0, 0);
                var used = new HashSet<string>(ix.Plugins.Where(p => !p.ReadingKey.IsZero).Select(p => $"{p.ReadingKey}-{BuilderKey}"), StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.EnumerateFiles(Folder))
                {
                    string name = Path.GetFileName(f);
                    bool world = name.EndsWith(".wlinks", StringComparison.OrdinalIgnoreCase), part = name.Contains(".wpart", StringComparison.OrdinalIgnoreCase), tmp = name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
                    if (!world && !part && !tmp) continue;
                    int dot = name.IndexOf('.');
                    string stem = dot < 0 ? name : name[..dot];
                    if (!tmp && used.Contains(stem)) continue;
                    bool ours = stem.EndsWith("-" + BuilderKey, StringComparison.OrdinalIgnoreCase) || tmp;
                    if (keep && world && ours) continue;
                    var written = File.GetLastWriteTimeUtc(f);
                    if (ours && !(unusedBefore is DateTime start && written < start) && DateTime.UtcNow - written < TimeSpan.FromHours(1)) continue;
                    try { long size = new FileInfo(f).Length; File.Delete(f); removed++; bytes += size; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return (removed, bytes);
        }

        /// <summary>How many plugins have a world link file now, of how many hold world data; links and bytes.</summary>
        public static (int Have, int Could, long Links, long Bytes, long Records) Totals(HeaderIndex ix)
        {
            int have = 0, could = 0; long links = 0, bytes = 0, records = 0;
            foreach (var p in ix.Plugins)
            {
                if (!EngineLinks.Walkable(p) || !HasWorld(p)) continue;
                could++;
                if (For(p) is not { } f) continue;
                have++; links += f.Links; bytes += f.FileBytes; records += f.Records;
            }
            return (have, could, links, bytes, records);
        }

        // ---------------------------------------------------------------- What the user asked for
        /// <summary>The plugins whose world link index the user asked to keep: one pattern a line (* is every plugin), a
        /// line starting with ! leaves those out. Written by indexes build world and indexes drop world.</summary>
        public static string WantedPath => Path.Combine(Folder, "wanted.txt");

        const string WantedHeader = "# The plugins whose world link index you asked SFSearch to keep (indexes build world). One name or pattern a line, * for every plugin; ! leaves one out. indexes drop world forgets them.";

        public static List<string> Wanted()
        {
            try
            {
                if (!File.Exists(WantedPath)) return new List<string>();
                return File.ReadAllLines(WantedPath).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
            }
            catch (IOException) { return new List<string>(); }
            catch (UnauthorizedAccessException) { return new List<string>(); }
        }

        /// <summary>Is the plugin among those asked for? The last line that matches it decides.</summary>
        public static bool IsWanted(IndexedPlugin p, IReadOnlyList<string> wanted)
        {
            bool yes = false;
            foreach (var w in wanted)
            {
                bool not = w.StartsWith('!');
                if (new Glob(not ? w[1..] : w).IsMatch(p.Name)) yes = !not;
            }
            return yes;
        }

        /// <summary>Adds plugins (names or patterns; * for all) to what the user asked for.</summary>
        public static void Want(IEnumerable<string> patterns)
        {
            var list = Wanted();
            foreach (var w in patterns)
            {
                list.RemoveAll(x => x.Equals("!" + w, StringComparison.OrdinalIgnoreCase) || x.Equals(w, StringComparison.OrdinalIgnoreCase) || w == "*");
                list.Add(w);
            }
            WriteWanted(list);
        }

        /// <summary>Takes plugins out of what the user asked for. Null: all of them (the file goes).</summary>
        public static void Unwant(IEnumerable<string>? patterns)
        {
            if (patterns == null) { try { File.Delete(WantedPath); } catch (IOException) { } catch (UnauthorizedAccessException) { } return; }
            var list = Wanted();
            foreach (var w in patterns)
            {
                if (list.RemoveAll(x => x.Equals(w, StringComparison.OrdinalIgnoreCase)) > 0 && !list.Any(x => !x.StartsWith('!'))) continue;
                list.RemoveAll(x => x.Equals("!" + w, StringComparison.OrdinalIgnoreCase));
                if (list.Any(x => !x.StartsWith('!'))) list.Add("!" + w);
            }
            if (!list.Any(x => !x.StartsWith('!'))) list.Clear();
            WriteWanted(list);
        }

        static void WriteWanted(List<string> list)
        {
            if (list.Count == 0) { try { File.Delete(WantedPath); } catch (IOException) { } catch (UnauthorizedAccessException) { } return; }
            Directory.CreateDirectory(Folder);
            File.WriteAllLines(WantedPath, new[] { WantedHeader }.Concat(list));
        }

        /// <summary>The plugins the user asked for that have no world link file yet (a new or changed plugin among them).</summary>
        public static List<IndexedPlugin> Missing(HeaderIndex ix)
        {
            var wanted = Wanted();
            if (wanted.Count == 0) return new List<IndexedPlugin>();
            return ix.Plugins.Where(p => EngineLinks.Walkable(p) && HasWorld(p) && IsWanted(p, wanted) && !File.Exists(PathOf(p))).ToList();
        }

        // ---------------------------------------------------------------- Estimates
        /// <summary>What making the world link files of these plugins costs, measured (J32 note): about 1.6 µs a world
        /// record on 8 threads plus 0.6 s a GB of world data (every plugin with world data: 14 s; Starfield.esm: 7 s). The
        /// memory is the rows of the biggest plugin while they are sorted, with Mutagen's reading beside them (the
        /// process's peak working set, 2.3 GB, also counts the plugin files' pages it maps).</summary>
        public static (long Records, long Bytes, double Seconds, long MB) Cost(IEnumerable<IndexedPlugin> plugins)
        {
            long records = 0, bytes = 0; long most = 0;
            foreach (var p in plugins)
            {
                if (p.Segment == null) continue;
                var (r, b) = WorldTypes.Totals(p.Segment);
                records += r; bytes += b; most = Math.Max(most, r);
            }
            int threads = Math.Clamp(Environment.ProcessorCount - 2, 2, 8);
            double seconds = (records * 1.6e-6 + bytes / 1073741824.0 * 0.6) * 8.0 / threads;
            long mb = Math.Min(4000, 400 + most * 24 * 3 / 1048576);
            return (records, bytes, seconds, mb);
        }

        // ---------------------------------------------------------------- The build
        public sealed class BuildReport
        {
            public int Built, AlreadyThere, NotWalkable, Left, Changed, Units, UnitsFromParts;
            public long Records, ByBytes, Links, Postings, Unread, Foreign, Bytes;
            public double Seconds;
            public long PeakMB;
            public bool Yielded;
            public List<string> Problems = new();
        }

        /// <summary>World records a unit of work holds at most: a build that gives way keeps the units it finished (as
        /// parts in indexes\world), so a busy service still finishes a big plugin.</summary>
        const int UnitRecords = 200_000;

        sealed class Job
        {
            public required IndexedPlugin P;
            public required List<int> Entries;
            public int Units;
            public Unit?[] Done = Array.Empty<Unit?>();
            public string? SessionWhy;
            public WorldReader.Plugin? Reading;
            public Stopwatch Clock = new();
        }

        /// <summary>What one unit found: rows with field numbers of this build's table, unread entries, counts.</summary>
        sealed class Unit
        {
            public List<WorldRow> Rows = new();
            public List<int> Unread = new();
            public string? Why;
            public int Records, ByBytes;
            public long Bytes, Foreign;
        }

        readonly record struct Out(int Set, WorldRow Row);

        /// <summary>
        /// Makes the world link file of each plugin <paramref name="only"/> picks that has world data and none yet. When
        /// <paramref name="stop"/> turns true (an agent is waiting) it ends within a moment: finished plugins' files stay,
        /// and the finished units of a plugin left half done are kept as parts, which the next build takes up.
        /// </summary>
        public static BuildReport Build(EngineIndex eng, Func<bool> stop, Func<IndexedPlugin, bool> only, int threads = 0)
        {
            var clock = Stopwatch.StartNew();
            var report = new BuildReport();
            Directory.CreateDirectory(Folder);
            using var one = new Mutex(false, @"Local\sfsearch_wlinks_" + Wire.Hash(Folder));
            bool held = false;
            try
            {
                while (!held)
                {
                    try { held = one.WaitOne(200); } catch (AbandonedMutexException) { held = true; }
                    if (!held && stop())
                    {
                        report.Yielded = true;
                        report.Left = eng.Index.Plugins.Count(p => EngineLinks.Walkable(p) && HasWorld(p) && only(p) && !File.Exists(PathOf(p)));
                        return report;
                    }
                }
                return BuildLocked(eng, stop, only, threads, report, clock);
            }
            finally { if (held) one.ReleaseMutex(); }
        }

        static string PartPath(IndexedPlugin p, int unit, int units) => Path.Combine(Folder, $"{p.ReadingKey}-{BuilderKey}.wpart{unit}of{units}");

        static BuildReport BuildLocked(EngineIndex eng, Func<bool> stop, Func<IndexedPlugin, bool> only, int threads, BuildReport report, Stopwatch clock)
        {
            var ix = eng.Index;
            var schema = EngineRecords.Schema;
            // Files of another build go at once, a changed plugin's old one after an hour (as the link index's build does)
            RemoveOldFiles(ix);
            if (threads <= 0) threads = Math.Clamp(Environment.ProcessorCount - 2, 2, 8);

            // The field table of this build: every thread numbers a path the same way
            var fields = new List<string>();
            var fieldIds = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            int FieldId(string path) => fieldIds.GetOrAdd(path, x => { lock (fields) { fields.Add(x); return fields.Count - 1; } });

            var jobs = new List<Job>();
            var sets = new List<ReadSet>();
            var setOf = new List<(Job Job, int Unit)>();
            foreach (var p in ix.Plugins.OrderBy(p => p.Stamp.Length))
            {
                if (!only(p) || !HasWorld(p)) continue;
                if (!EngineLinks.Walkable(p)) { report.NotWalkable++; continue; }
                if (File.Exists(PathOf(p))) { report.AlreadyThere++; continue; }
                var seg = p.Segment!;
                var world = WorldTypes.Of(seg);
                var entries = new List<int>();
                for (int i = 0; i < seg.FirstUnresolved; i++) if (world[seg[i].Signature]) entries.Add(i);
                var job = new Job { P = p, Entries = entries, Units = Math.Max(1, (entries.Count + UnitRecords - 1) / UnitRecords) };
                job.Done = new Unit?[job.Units];
                for (int u = 0; u < job.Units; u++)
                {
                    if (job.Units > 1 && ReadPart(PartPath(p, u, job.Units), FieldId) is { } part) { job.Done[u] = part; report.UnitsFromParts++; continue; }
                    int from = (int)((long)entries.Count * u / job.Units), to = (int)((long)entries.Count * (u + 1) / job.Units);
                    sets.Add(new ReadSet { Plugin = p, Entries = entries.GetRange(from, to - from) });
                    setOf.Add((job, u));
                }
                jobs.Add(job);
            }
            report.Units = setOf.Count;

            using var engine = new RecordEngine(ix, schema, Path.Combine(EngineRecords.ScratchRoot, "verify"), eng.DataFolder, Language.English) { PreferDirect = true, AllowHeavy = true };
            foreach (var job in jobs.Where(j => j.Done.Any(d => d == null)))
            {
                try { job.Reading = new WorldReader.Plugin { P = job.P, Session = engine.Sessions.Get(job.P.Name) }; }
                catch (RecordException e) { job.SessionWhy = e.Message; }
            }
            if (setOf.Count > 0) MutagenStart.Run();

            using var cancel = new CancellationTokenSource();
            int asked = 0;
            var readers = new ThreadLocal<WorldReader>(() => new WorldReader(schema, FieldId));
            var rowsOf = new ThreadLocal<List<WorldRow>>(() => new List<WorldRow>());
            var whys = new string?[sets.Count];
            var counts = new long[sets.Count, 4]; // records, by bytes, bytes, foreign
            PipelineWork<Out> work = (in PipelineRecord r, List<Out> output) =>
            {
                if ((Interlocked.Increment(ref asked) & 255) == 0 && stop()) cancel.Cancel();
                var (job, _) = setOf[r.Set];
                Interlocked.Increment(ref counts[r.Set, 0]);
                Interlocked.Add(ref counts[r.Set, 2], r.Data.Length);
                string? why;
                if (job.Reading == null) why = job.SessionWhy ?? "the plugin couldn't be opened";
                else if (r.Problem != PipelineProblem.None) why = r.Problem == PipelineProblem.NotInflated ? "its compressed data couldn't be inflated" : "it runs past the end of the file";
                else
                {
                    var rows = rowsOf.Value!; rows.Clear();
                    var how = readers.Value!.Read(job.Reading, r.EntryIndex, r.Head, r.Data, rows, out why, out long foreign);
                    if (foreign > 0) Interlocked.Add(ref counts[r.Set, 3], foreign);
                    if (how != WorldReader.How.Unread)
                    {
                        if (how == WorldReader.How.BytesAndMutagen) Interlocked.Increment(ref counts[r.Set, 1]);
                        foreach (var row in rows) output.Add(new Out(r.Set, row));
                        return;
                    }
                }
                Interlocked.CompareExchange(ref whys[r.Set], $"{ix.Describe(r.Ref).Label}: {why}", null);
                output.Add(new Out(r.Set, new WorldRow(0, (uint)r.EntryIndex, -1, 0)));
            };

            // Each unit is finished when the results of a later one come (they come in order), or at the end
            int current = 0;
            var open = sets.Count > 0 ? new Unit() : null;
            void Finish(int set)
            {
                var (job, u) = setOf[set];
                var unit = open!;
                unit.Records = (int)counts[set, 0]; unit.ByBytes = (int)counts[set, 1]; unit.Bytes = counts[set, 2]; unit.Foreign = counts[set, 3]; unit.Why = whys[set];
                job.Done[u] = unit;
                open = new Unit();
                if (job.Done.All(d => d != null)) WriteJob(job, fields, report);
            }
            bool stopped = false;
            if (sets.Count > 0)
            {
                foreach (var job in jobs) job.Clock.Start();
                try
                {
                    foreach (var o in RecordPipeline.Read(sets, work, new PipelineOptions
                    {
                        Threads = threads, Data = PipelineData.Plain, CostPerByte = 8, MemoryBytes = JobContext.MemoryBudgetMB << 20, Cancel = cancel.Token,
                    }))
                    {
                        while (o.Set > current) { Finish(current); current++; }
                        if (o.Row.Field < 0) open!.Unread.Add((int)o.Row.From);
                        else open!.Rows.Add(o.Row);
                    }
                    while (current < sets.Count) { Finish(current); current++; }
                }
                catch (OperationCanceledException) { stopped = true; }
            }
            // Jobs that had every unit from parts
            foreach (var job in jobs.Where(j => !File.Exists(PathOf(j.P)) && j.Done.All(d => d != null))) WriteJob(job, fields, report);
            if (stopped)
            {
                // Keep the finished units of the plugins left half done, for the next build
                foreach (var job in jobs.Where(j => j.Units > 1 && !File.Exists(PathOf(j.P))))
                    for (int u = 0; u < job.Units; u++)
                        if (job.Done[u] is { } unit && !File.Exists(PartPath(job.P, u, job.Units)))
                            try { WritePart(PartPath(job.P, u, job.Units), unit, fields); }
                            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { report.Problems.Add($"{job.P.Name}: a finished part couldn't be kept ({e.Message.TrimEnd('.')})"); }
            }
            report.Yielded = stopped;
            report.Left = jobs.Count(j => !File.Exists(PathOf(j.P)));
            report.Seconds = clock.Elapsed.TotalSeconds;
            if (report.Built > 0)
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            }
            using (var me = Process.GetCurrentProcess()) report.PeakMB = me.PeakWorkingSet64 / 1048576;
            return report;
        }

        static void WriteJob(Job job, List<string> fields, BuildReport report)
        {
            var p = job.P;
            QuickStamp now;
            try { now = QuickStamp.Read(p.Path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { now = default; }
            if (now != p.Stamp) { report.Changed++; report.Problems.Add($"{p.Name} changed while it was read: its world link index is made again the next time"); return; }
            // Dropped while it was being made (indexes drop world during a build after a save_index answer, J33): the user
            // no longer asks for it, so it isn't written (refs would read it again though nobody asked for it)
            if (!IsWanted(p, Wanted()))
            {
                report.Problems.Add($"{p.Name}: its world link index was dropped (indexes drop world) while it was being made, so it isn't written");
                for (int u = 0; u < job.Units; u++) try { File.Delete(PartPath(p, u, job.Units)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return;
            }
            var units = job.Done.Select(d => d!).ToList();
            int count = units.Sum(u => u.Rows.Count);
            var rows = new WorldRow[count];
            int at = 0;
            foreach (var u in units) { CollectionsMarshal.AsSpan(u.Rows).CopyTo(rows.AsSpan(at)); at += u.Rows.Count; u.Rows = new List<WorldRow>(); }
            // The file's own field table: only the fields its rows use, numbered from 0
            var used = new SortedSet<int>(rows.Select(r => r.Field));
            var map = new Dictionary<int, int>();
            var own = new List<string>();
            lock (fields) foreach (int f in used) { map[f] = own.Count; own.Add(fields[f]); }
            for (int i = 0; i < count; i++) rows[i] = new WorldRow(rows[i].Target, rows[i].From, map[rows[i].Field], rows[i].Count);
            var content = new WorldLinkContent
            {
                Fields = own,
                Unread = units.SelectMany(u => u.Unread).OrderBy(e => e).ToArray(),
                UnreadWhy = units.Select(u => u.Why).FirstOrDefault(w => w != null) ?? "",
                Records = units.Sum(u => u.Records), Bytes = units.Sum(u => u.Bytes), ByBytes = units.Sum(u => u.ByBytes),
                Seconds = job.Clock.Elapsed.TotalSeconds,
            };
            WorldLinkFile.Fill(content, rows, count);
            string path = PathOf(p);
            try { WorldLinkFile.Write(path, p.ReadingKey, content); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { report.Problems.Add($"{p.Name}: its world link file couldn't be written ({e.Message.TrimEnd('.')})"); return; }
            for (int u = 0; u < job.Units; u++) try { File.Delete(PartPath(p, u, job.Units)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            report.Built++;
            report.Records += content.Records; report.ByBytes += content.ByBytes; report.Links += content.Links; report.Postings += content.Postings.Length; report.Unread += content.Unread.Length;
            report.Foreign += units.Sum(u => u.Foreign);
            try { report.Bytes += new FileInfo(path).Length; } catch (IOException) { }
            if (content.Unread.Length > 0) report.Problems.Add($"{p.Name}: {content.Unread.Length:N0} world record(s) couldn't be read (refs searches their bytes). The first: {content.UnreadWhy}");
        }

        // ---------------------------------------------------------------- Parts of a plugin left half done
        const ulong PartMagic = 0x3130545057534653; // "SFSWPT01"

        static void WritePart(string path, Unit u, List<string> fields)
        {
            string tmp = path + $".{Environment.ProcessId}.tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var w = new BinaryWriter(fs, Encoding.UTF8))
            {
                w.Write(PartMagic);
                string[] names; lock (fields) names = fields.ToArray();
                w.Write(names.Length); foreach (var n in names) w.Write(n);
                w.Write(u.Records); w.Write(u.ByBytes); w.Write(u.Bytes); w.Write(u.Foreign); w.Write(u.Why ?? "");
                w.Write(u.Unread.Count); foreach (int e in u.Unread) w.Write(e);
                w.Write(u.Rows.Count); w.Flush();
                fs.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(u.Rows)));
                w.Write(PartMagic);
            }
            File.Move(tmp, path, overwrite: true);
        }

        static Unit? ReadPart(string path, Func<string, int> fieldId)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var r = new BinaryReader(fs, Encoding.UTF8);
                if (r.ReadUInt64() != PartMagic) return null;
                var map = new int[r.ReadInt32()];
                for (int i = 0; i < map.Length; i++) map[i] = fieldId(r.ReadString());
                var u = new Unit { Records = r.ReadInt32(), ByBytes = r.ReadInt32(), Bytes = r.ReadInt64(), Foreign = r.ReadInt64() };
                string why = r.ReadString(); u.Why = why.Length > 0 ? why : null;
                int n = r.ReadInt32(); for (int i = 0; i < n; i++) u.Unread.Add(r.ReadInt32());
                n = r.ReadInt32();
                var rows = new WorldRow[n];
                fs.ReadExactly(MemoryMarshal.AsBytes(rows.AsSpan()));
                if (r.ReadUInt64() != PartMagic) return null;
                for (int i = 0; i < n; i++) rows[i] = new WorldRow(rows[i].Target, rows[i].From, map[rows[i].Field], rows[i].Count);
                u.Rows = new List<WorldRow>(rows);
                return u;
            }
            catch (Exception e) when (e is IOException or EndOfStreamException or UnauthorizedAccessException or IndexOutOfRangeException) { return null; }
        }
    }
}
