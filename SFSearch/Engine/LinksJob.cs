using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins.Records;
using SFSearch.Index;
using SFSearch.Records;
using SFSearch.Service;

namespace SFSearch.Engine
{
    /// <summary>
    /// __links build | status | check: the link index's own job. "build" is what the service queues in its indexer lane
    /// after the header index is up to date: it makes the link file of every plugin that has none, and gives way when
    /// an agent is waiting. "status" says how far it is. "check" holds the link walk against two other readings of
    /// the same records: the links in the record's JSON, and Mutagen's own list of a record's links.
    /// </summary>
    public static class LinksJob
    {
        public static void Run(List<string> args)
        {
            string sub = args.Count > 0 ? args[0].ToLowerInvariant() : "build";
            if (sub is not ("build" or "status" or "check"))
            {
                Console.WriteLine("Usage: SFSearch __links build [plugin,...] | status | check <plugin> [--sample N] [--world]   (the link index of the refs tool; the service runs build by itself)");
                return;
            }
            try
            {
                var eng = EngineIndex.Get();
                var v = eng.Check();
                if (v.Cold || v.Report.AnythingChanged) EngineLinks.Trim(eng.Index);
                if (sub == "check") { Check(eng, args.Skip(1).ToList()); return; }
                var ix = eng.Index;
                string did = "";
                if (sub == "build")
                {
                    Func<IndexedPlugin, bool>? only = null;
                    if (args.Count > 1)
                    {
                        var globs = Home.SplitPluginList(args[1]).Select(n => new Glob(n.Trim())).ToList();
                        only = p => globs.Any(g => g.IsMatch(p.Name));
                    }
                    var r = EngineLinks.Build(eng, () => JobContext.YieldRequested, only: only);
                    // Stopped for an agent with work left: the coordinator queues the job again
                    if (r.Yielded && r.Left > 0) JobContext.Yielded = true;
                    did = $" This build: {r.Built:N0} plugin(s) walked in {r.Seconds:0.0} s ({r.Records:N0} records, {r.Links:N0} links, {r.Names:N0} names, {EngineIndex.Mb(r.Bytes)} written"
                        + $"{(r.Unread > 0 ? $", {r.Unread:N0} records Mutagen couldn't read" : "")}{(r.Foreign > 0 ? $", {r.Foreign:N0} links to a plugin that isn't a master" : "")}), {r.AlreadyThere:N0} already there, "
                        + $"{r.NotWalkable:N0} can't be read{(r.Changed > 0 ? $", {r.Changed} changed while walked" : "")}; {r.Left:N0} left{(r.Yielded && r.Left > 0 ? " (it gave way to an agent: it goes on later)" : "")}. Peak memory {r.PeakMB:N0} MB.";
                    Print(ix, did);
                    foreach (var p in r.Problems.Take(12)) Console.WriteLine("  " + p);
                    if (r.Problems.Count > 12) Console.WriteLine($"  … {r.Problems.Count - 12} more");
                    return;
                }
                Print(ix, did);
                // status slow: the plugins whose walk took longest, with what was found in them
                if (args.Count > 1 && args[1].Equals("slow", StringComparison.OrdinalIgnoreCase))
                    foreach (var (p, f) in ix.Plugins.Select(p => (p, EngineLinks.For(p))).Where(x => x.Item2 != null).OrderByDescending(x => x.Item2!.BuildSeconds).Take(12))
                        Console.WriteLine($"  {p.Name}: {f!.BuildSeconds:0.00} s, {f.Walked:N0} records walked, {f.Count:N0} links, {f.Fields.Count:N0} fields, {f.NameCount:N0} names, {f.UnreadCount} unread, {EngineIndex.Mb(f.FileBytes)}; world data left out: {f.WorldRecords:N0} records, {f.WorldBytes / 1048576.0:N0} MB");
            }
            catch (RecordException e) { Console.WriteLine($"Error: {e.Message.TrimEnd('.')}. Fix: build SFSearch again, then run it again."); Environment.ExitCode = 1; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                Console.WriteLine($"Error: the link index couldn't be brought up to date ({e.Message.TrimEnd('.')}). Fix: check the Data folder and the scratch folder, then run it again.");
                Environment.ExitCode = 1;
            }
        }

        static void Print(HeaderIndex ix, string did)
        {
            var (have, could, links, bytes, names) = EngineLinks.Totals(ix);
            long mem;
            using (var me = Process.GetCurrentProcess()) mem = me.PrivateMemorySize64 / 1048576;
            Console.WriteLine($"link index: {have:N0} of {could:N0} plugins have a link file: {links:N0} links, {names:N0} names, {EngineIndex.Mb(bytes)} in {EngineLinks.Folder}.{did} This program holds {mem:N0} MB.");
        }

        // ---------------------------------------------------------------- check
        /// <summary>
        /// Holds the link walk against the record's JSON and against Mutagen's EnumerateFormLinks, record by record.
        /// The walk and the JSON must give the same links at the same paths. Mutagen's list has no paths: the targets
        /// are compared.
        /// </summary>
        static void Check(EngineIndex eng, List<string> args)
        {
            int sample = int.TryParse(CommandLine.TakeOption(args, "--sample"), out int s) ? s : 0;
            bool withWorld = CommandLine.TakeFlag(args, "--world");
            if (args.Count == 0) { Console.WriteLine("Usage: SFSearch __links check <plugin> [--sample N] [--world]"); return; }
            var ix = eng.Index;
            var p = ix.Plugin(args[0]) ?? ix.Plugin(args[0] + ".esm");
            if (p?.Segment == null) { Console.WriteLine($"Error: {args[0]} isn't a plugin in Data that can be read. Fix: check the name."); Environment.ExitCode = 1; return; }
            var seg = p.Segment;
            var world = WorldTypes.Of(seg);
            var entries = new List<int>();
            for (int i = 0; i < seg.FirstUnresolved; i++) if (withWorld || !world[seg[i].Signature]) entries.Add(i);
            int all = entries.Count;
            if (sample > 0 && entries.Count > sample)
            {
                // Evenly spread, and the same records on every run: so many of each type
                var byType = entries.GroupBy(e => seg[e].Signature).ToList();
                int each = Math.Max(1, sample / byType.Count);
                entries = byType.SelectMany(g => { var l = g.ToList(); return l.Count <= each ? l : Enumerable.Range(0, each).Select(k => l[(int)((long)k * l.Count / each)]); }).OrderBy(e => e).ToList();
            }
            var engine = EngineRecords.For(eng, ix);
            var use = engine.Sessions.Use(p.Name);
            var sink = new LinkSink(indexed: true);
            long records = 0, links = 0, mutagenLinks = 0, unread = 0, jsonDiffer = 0, mutagenDiffer = 0, onlyWalk = 0, onlyMutagen = 0;
            var examples = new List<string>();
            var clock = Stopwatch.StartNew();
            foreach (int entry in entries)
            {
                var r = new RecordRef(p.Id, entry);
                LoadedRecord rec;
                try { rec = engine.Load(r); }
                catch (RecordException e) { unread++; if (examples.Count < 12) examples.Add($"not read: {e.Message}"); continue; }
                records++;
                RecordLinks.Walk(rec.Record, rec.Type, sink);
                var walk = sink.Links.Select(l => (Path: sink.Fields[l.Field], Target: $"{l.Target.ID:X6}:{l.Target.ModKey.FileName.String}")).ToList();
                links += walk.Count;

                // The same record as JSON: every text that is a FormKey, but the record's own and the markers of records inside it
                var json = new List<(string Path, string Target)>();
                JsonLinks(engine.Json(rec), "", json, top: true);
                var a = walk.Select(x => x.Path + " = " + x.Target.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal).ToList();
                var b = json.Select(x => x.Path + " = " + x.Target.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (!a.SequenceEqual(b))
                {
                    jsonDiffer++;
                    if (examples.Count < 12) examples.Add($"{rec.Info.Signature} {ProblemNotes.Label(rec.Info)}: the walk and the JSON differ. Only the walk: {string.Join("; ", a.Except(b).Take(3))}. Only the JSON: {string.Join("; ", b.Except(a).Take(3))}");
                }

                // Mutagen's own list of the record's links (no paths; a record that holds others lists theirs too)
                var mine = walk.Select(x => x.Target.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal).ToList();
                var theirs = rec.Record.EnumerateFormLinks().Where(l => !l.IsNull).Select(l => $"{l.FormKey.ID:X6}:{l.FormKey.ModKey.FileName.String}".ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal).ToList();
                mutagenLinks += theirs.Count;
                if (!mine.SequenceEqual(theirs))
                {
                    mutagenDiffer++;
                    var mineOnly = Minus(mine, theirs); var theirsOnly = Minus(theirs, mine);
                    onlyWalk += mineOnly.Count; onlyMutagen += theirsOnly.Count;
                    if (examples.Count < 12) examples.Add($"{rec.Info.Signature} {ProblemNotes.Label(rec.Info)}: the walk has {mine.Count} links, EnumerateFormLinks {theirs.Count}. Only the walk: {string.Join("; ", mineOnly.Take(3).Select(t => t + " at " + string.Join(", ", walk.Where(w => w.Target.Equals(t, StringComparison.OrdinalIgnoreCase)).Select(w => w.Path).Distinct().Take(2))))}. Only Mutagen: {string.Join("; ", theirsOnly.Take(3))}");
                }
            }
            engine.Sessions.Confirm(use);
            Console.WriteLine($"{p.Name}: {records:N0} of {all:N0} records{(withWorld ? "" : " outside the world groups")} walked in {clock.Elapsed.TotalSeconds:0.0} s ({unread:N0} couldn't be read): {links:N0} links. "
                + $"Against the record's JSON: {jsonDiffer:N0} record(s) differ. Against Mutagen's EnumerateFormLinks ({mutagenLinks:N0} links): {mutagenDiffer:N0} record(s) differ ({onlyWalk:N0} links only the walk has, {onlyMutagen:N0} only Mutagen has).");
            foreach (var e in examples) Console.WriteLine("  " + e);
            if (jsonDiffer > 0) Environment.ExitCode = 1;
        }

        static List<string> Minus(List<string> a, List<string> b)
        {
            var left = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var x in b) left[x] = left.GetValueOrDefault(x) + 1;
            var only = new List<string>();
            foreach (var x in a) { if (left.TryGetValue(x, out int n) && n > 0) left[x] = n - 1; else only.Add(x); }
            return only;
        }

        static void JsonLinks(JsonNode? node, string path, List<(string, string)> into, bool top = false)
        {
            switch (node)
            {
                case JsonObject o:
                    if (RecordSplit.ChildOf(o) != null) return;
                    foreach (var (k, v) in o)
                    {
                        if (top && k == "FormKey") continue;
                        JsonLinks(v, path.Length == 0 ? k : path + "." + k, into);
                    }
                    break;
                case JsonArray a:
                    for (int i = 0; i < a.Count; i++) JsonLinks(a[i], $"{path}[{i}]", into);
                    break;
                case JsonValue v when v.TryGetValue<string>(out var s) && RecordSplit.IsFormKey(s):
                    into.Add((path, s));
                    break;
            }
        }
    }
}
