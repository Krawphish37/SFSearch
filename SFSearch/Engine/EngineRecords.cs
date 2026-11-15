using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Mutagen.Bethesda.Strings;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Engine
{
    /// <summary>
    /// The record layer inside SFSearch (design\agent-first, milestones M3 and M5): one <see cref="RecordEngine"/> per
    /// header index and language, kept for the life of the program, so the plugins a worker has read stay open as
    /// Mutagen overlays. Nothing about a record is stored. The engine's own header index is the main one; a plugin
    /// file outside Data has an index of its own (<see cref="EngineIndex.ByPath"/>) and so an engine of its own.
    /// </summary>
    public static class EngineRecords
    {
        static readonly object _gate = new();
        static RecordSchema? _schema;
        static readonly Dictionary<(HeaderIndex Ix, Language Language), RecordEngine> _engines = new();
        static EngineIndex? _for;

        /// <summary>A worker gives its open plugins back above this much memory of its own, after the command.</summary>
        const long TrimAboveMB = 1500;

        /// <summary>schema.db beside the program, as the record layer reads it. It is needed: cards, defaults and evidence come from it.</summary>
        public static RecordSchema Schema
        {
            get
            {
                lock (_gate)
                {
                    if (_schema != null) return _schema;
                    string path = SchemaTypes.DefaultPath;
                    if (!File.Exists(path)) throw new RecordException($"schema.db isn't beside SFSearch.exe ({path}). The record tools need it: build SFSearch again (the build makes it).");
                    return _schema = new RecordSchema(path);
                }
            }
        }

        public static string ScratchRoot => Path.GetDirectoryName(EngineIndex.IndexFolder())!;

        /// <summary>The engine for a header index: the main one, or a side index of a plugin outside Data.</summary>
        public static RecordEngine For(EngineIndex eng, HeaderIndex ix, Language language = Language.English)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_for, eng)) { CloseAllLocked(); _for = eng; }
                if (_engines.TryGetValue((ix, language), out var e)) return e;
                e = new RecordEngine(ix, Schema, Path.Combine(ScratchRoot, "verify"), eng.DataFolder, language);
                return _engines[(ix, language)] = e;
            }
        }

        static void CloseAllLocked()
        {
            foreach (var e in _engines.Values) e.Dispose();
            _engines.Clear();
        }

        /// <summary>How many plugins this program holds open as overlays: each plugin once, however many of its engines (one per language, one per side index) hold it. It is the count of <see cref="OpenNames"/>, so status shows one number.</summary>
        public static int OpenPlugins => OpenNames().Count;

        /// <summary>The names of the plugins this program holds open as overlays.</summary>
        public static List<string> OpenNames() { lock (_gate) return _engines.Values.SelectMany(e => e.Sessions.Open.Select(s => s.Name)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(); }

        /// <summary>
        /// After a command: a program that holds more than 1.5 GB closes the plugins it has open and gives the memory
        /// back. The next command opens what it needs again (0.05 to 0.5 s a plugin). This keeps a worker under the
        /// service's recycle limit (3 GB) however many plugins a search has read.
        /// </summary>
        public static string? TrimIfLarge()
        {
            long mb;
            using (var me = Process.GetCurrentProcess()) mb = me.PrivateMemorySize64 / 1048576;
            if (mb < TrimAboveMB) return null;
            int closed;
            lock (_gate)
            {
                closed = _engines.Values.Sum(e => e.Sessions.Open.Count);
                foreach (var e in _engines.Values) e.Sessions.Dispose();
            }
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            long after;
            using (var me = Process.GetCurrentProcess()) after = me.PrivateMemorySize64 / 1048576;
            return $"afterwards this program held {mb:N0} MB, so it closed its {closed:N0} open plugin{(closed == 1 ? "" : "s")} and now holds {after:N0} MB";
        }

        public static bool TryLanguage(string? text, out Language language)
        {
            language = Language.English;
            if (string.IsNullOrWhiteSpace(text)) return true;
            // The same names and codes as SQL's text(): Mutagen's names (Japanese) and the strings files' codes (ja);
            // never a bare number, which Enum.TryParse would take as a language
            return SFSearch.Query.TextLanguages.TryName(text, out var name) && Enum.TryParse(name, true, out language);
        }
    }

    /// <summary>
    /// Which record types are world data: cells and what they hold (placed references, navmeshes, terrain), worldspaces,
    /// surface trees, blocks and patterns, and the navigation mesh info map. The link index leaves them out (about
    /// 97% of a complete link table would be theirs, design section 2), and a link search reads their bytes only when
    /// asked. A type is world data when it is one of the six top-level world groups' types, or its records sit inside
    /// a cell or a worldspace: told from the plugin's own header index, not from a list of names.
    /// </summary>
    public static class WorldTypes
    {
        static readonly HashSet<string> Top = new(StringComparer.Ordinal) { "CELL", "WRLD", "SFTR", "SFBK", "SFPT", "NAVI" };
        /// <summary>Types that sit inside another record and aren't world data (dialogue and scenes inside quests).</summary>
        static readonly HashSet<string> NestedElsewhere = new(StringComparer.Ordinal) { "DIAL", "INFO", "SCEN" };
        static readonly ConditionalWeakTable<Segment, bool[]> _bySegment = new();

        /// <summary>For each signature of the segment (by its place in <see cref="Segment.Signatures"/>): true when it is world data.</summary>
        public static bool[] Of(Segment seg) => _bySegment.GetValue(seg, Classify);

        static bool[] Classify(Segment seg)
        {
            int sigs = seg.Signatures.Count;
            var world = new bool[sigs];
            var seen = new bool[sigs];
            int left = sigs;
            for (int i = 0; i < seg.Count && left > 0; i++)
            {
                var e = seg[i];
                if (seen[e.Signature]) continue;
                seen[e.Signature] = true; left--;
                string sig = seg.Signatures[e.Signature].Signature;
                if (Top.Contains(sig)) { world[e.Signature] = true; continue; }
                if (!seg.TryGetParent(e, out int owner, out uint local)) continue;
                int at = owner < Entry.UnresolvedOwner ? seg.Find(owner, local) : -1;
                string? parent = at < 0 ? null : seg.SignatureOf(seg[at]);
                world[e.Signature] = parent != null ? parent is "CELL" or "WRLD" : !NestedElsewhere.Contains(sig);
            }
            return world;
        }

        /// <summary>Records and bytes (headers included) of the plugin's world data.</summary>
        public static (int Records, long Bytes) Totals(Segment seg)
        {
            var world = Of(seg);
            int records = 0; long bytes = 0;
            for (int i = 0; i < world.Length; i++)
                if (world[i]) { records += seg.Signatures[i].Count; bytes += seg.Signatures[i].Bytes; }
            return (records, bytes);
        }

        public static IEnumerable<string> Names(Segment seg)
        {
            var world = Of(seg);
            for (int i = 0; i < world.Length; i++) if (world[i]) yield return seg.Signatures[i].Signature;
        }
    }
}
