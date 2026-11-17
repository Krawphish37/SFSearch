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
    /// __links build: the link index's own job, what the service queues in its indexer lane after the header index is
    /// up to date: it makes the link file of every plugin that has none, and gives way when an agent is waiting. How far
    /// the index is: the indexes verb. (The check of the link walk against the record's JSON and Mutagen's own list of
    /// links, `__links check` until J37, is a command of the test add-on now: tests\SFSearch.TestHooks.)
    /// Then (J32) the world link files of the plugins the user asked to keep one for (indexes build world; WorldLinks.Wanted)
    /// that have none: a new or changed plugin among them. `__links world [plugins]` makes only those: the call for a
    /// build in the indexer lane after an agent's answer (J33: WorldLinks.Want, then this job).
    /// </summary>
    public static class LinksJob
    {
        public static void Run(List<string> args)
        {
            string sub = args.Count > 0 ? args[0].ToLowerInvariant() : "build";
            if (sub is not ("build" or "world"))
            {
                Console.WriteLine("Usage: SFSearch __links build [plugin,...] | world [plugin,...]   (the link index's jobs; the service runs them by itself. What is kept and how far it is: indexes)");
                return;
            }
            try
            {
                var eng = EngineIndex.Get();
                var v = eng.Check();
                if (v.Cold || v.Report.AnythingChanged) { EngineLinks.Trim(eng.Index); WorldLinks.Trim(eng.Index); }
                var ix = eng.Index;
                Func<IndexedPlugin, bool>? only = null;
                if (args.Count > 1)
                {
                    var globs = Home.SplitPluginList(args[1]).Select(n => new Glob(n.Trim())).ToList();
                    only = p => globs.Any(g => g.IsMatch(p.Name));
                }
                if (sub == "world")
                {
                    World(eng, only);
                    return;
                }
                // A big walk is faster on .NET's workstation garbage collector (J58, GAPS S85): on the server one each of
                // its 300 to 500 short collections stops the walk's threads two to three times as long. A worker on server
                // hands it to one on workstation, which the coordinator starts for it; a small walk stays here.
                if (JobContext.OnServerWorker && EngineLinks.RecordsToWalk(ix, only) is var toWalk && toWalk >= EngineLinks.WorkstationWalkRecords)
                {
                    JobContext.RunOnWorkstation = true;
                    Console.WriteLine($"link index: {toWalk:N0} records to walk: handed to a worker on .NET's workstation garbage collector.");
                    return;
                }
                var r = EngineLinks.Build(eng, () => JobContext.YieldRequested, only: only);
                // Stopped for an agent with work left: the coordinator queues the job again
                if (r.Yielded && r.Left > 0) JobContext.Yielded = true;
                string did = $" This build: {r.Built:N0} plugin(s) walked in {r.Seconds:0.0} s ({r.Records:N0} records, {r.Links:N0} links, {r.Names:N0} names, {EngineIndex.Mb(r.Bytes)} written"
                    + $"{(r.Unread > 0 ? $", {r.Unread:N0} records Mutagen couldn't read" : "")}{(r.Foreign > 0 ? $", {r.Foreign:N0} links to a plugin that isn't a master" : "")}), {r.AlreadyThere:N0} already there, "
                    + $"{r.NotWalkable:N0} can't be read{(r.Changed > 0 ? $", {r.Changed} changed while walked" : "")}; {r.Left:N0} left{(r.Yielded && r.Left > 0 ? " (it gave way to an agent: it goes on later)" : "")}. Peak memory {r.PeakMB:N0} MB.";
                Print(ix, did);
                foreach (var p in r.Problems.Take(12)) Console.WriteLine("  " + p);
                if (r.Problems.Count > 12) Console.WriteLine($"  … {r.Problems.Count - 12} more");
                // The world link files the user asked to keep, once the link index has nothing left (J32)
                if (!(r.Yielded && r.Left > 0) && WorldLinks.Missing(ix) is { Count: > 0 } missing) World(eng, missing.ToHashSet().Contains);
            }
            catch (RecordException e) { Console.WriteLine($"Error: {e.Message.TrimEnd('.')}. Fix: build SFSearch again, then run it again."); Environment.ExitCode = 1; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                Console.WriteLine($"Error: the link index couldn't be brought up to date ({e.Message.TrimEnd('.')}). Fix: check the Data folder and the indexes folder, then run it again.");
                Environment.ExitCode = 1;
            }
        }

        /// <summary>Makes the world link files of the plugins picked (null: those the user asked to keep that have none).</summary>
        static void World(EngineIndex eng, Func<IndexedPlugin, bool>? only)
        {
            var ix = eng.Index;
            if (only == null)
            {
                var missing = WorldLinks.Missing(ix).ToHashSet();
                only = missing.Contains;
            }
            var r = WorldLinks.Build(eng, () => JobContext.YieldRequested, only);
            if (r.Yielded && r.Left > 0) JobContext.Yielded = true;
            var (have, could, links, bytes, records) = WorldLinks.Totals(ix);
            Console.WriteLine($"world link index: {have:N0} of {could:N0} plugins with world data have a world link file: {records:N0} records, {links:N0} links, {EngineIndex.Mb(bytes)} in {WorldLinks.Folder}. "
                + $"This build: {r.Built:N0} plugin(s) in {r.Seconds:0.0} s ({r.Records:N0} records, {r.ByBytes:N0} of them partly by their bytes, {r.Links:N0} links in {r.Postings:N0} rows, {EngineIndex.Mb(r.Bytes)} written"
                + $"{(r.Unread > 0 ? $", {r.Unread:N0} records couldn't be read" : "")}{(r.UnitsFromParts > 0 ? $", {r.UnitsFromParts:N0} part(s) kept from an earlier build" : "")}), {r.AlreadyThere:N0} already there"
                + $"{(r.Changed > 0 ? $", {r.Changed} changed while read" : "")}; {r.Left:N0} left{(r.Yielded && r.Left > 0 ? " (it gave way to an agent: it goes on later)" : "")}. Peak memory {r.PeakMB:N0} MB.");
            foreach (var p in r.Problems.Take(12)) Console.WriteLine("  " + p);
        }

        /// <summary>
        /// A plain command line (no service) whose caller answered "save_index" (J33): the world link files the user asked
        /// for that are missing are made right after the answer was printed, and one line says so (none with --json, whose
        /// answer is one JSON object).
        /// </summary>
        public static void WorldAfterAnswer()
        {
            try
            {
                var eng = EngineIndex.Get();
                var missing = WorldLinks.Missing(eng.Index);
                if (missing.Count == 0) return;
                SFSearch.Records.MutagenStart.Run();
                var r = WorldLinks.Build(eng, () => false, missing.ToHashSet().Contains);
                if (CommandLine.Format != OutputFormat.Json)
                    Console.WriteLine($"Saved as asked (save_index): the world link index of {r.Built:N0} plugin(s), {EngineIndex.Mb(r.Bytes)} in {r.Seconds:0.0} s. indexes shows it; indexes drop world deletes it.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                Console.WriteLine($"Error: the world link index couldn't be saved ({e.Message.TrimEnd('.')}). Fix: run indexes build world.");
            }
        }

        static void Print(HeaderIndex ix, string did)
        {
            var (have, could, links, bytes, names) = EngineLinks.Totals(ix);
            long mem;
            using (var me = Process.GetCurrentProcess()) mem = me.PrivateMemorySize64 / 1048576;
            Console.WriteLine($"link index: {have:N0} of {could:N0} plugins have a link file: {links:N0} links, {names:N0} names, {EngineIndex.Mb(bytes)} in {EngineLinks.Folder}.{did} This program holds {mem:N0} MB.");
        }
    }
}
