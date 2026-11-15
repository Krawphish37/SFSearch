using System;
using System.Collections.Generic;
using System.Linq;
using SFSearch.Index;

namespace SFSearch.Engine
{
    /// <summary>
    /// Problems lead the notes, always (design\agent-first\ARCHITECTURE.md section 11, traps 14 and 15).
    /// A tool hands over the plugins its answer was read from. Each one's problems, as the header index found them,
    /// go to the top of the answer's notes: a patch built for an older master (overrides whose type differs from their
    /// base), overrides of nothing, a repeated FormKey, a master that isn't loaded or loads late.
    /// A plugin whose FormIDs can't be read at all (a missing master, a file that can't be walked) is an error for an
    /// answer that needs it: nothing is mapped by guess.
    /// The index finds all of this in its own pass, before the first answer, so no other command has to run first.
    /// </summary>
    public static class ProblemNotes
    {
        /// <summary>Why a plugin's records can't be read, with what to do about it. Null when they can.</summary>
        public static (string What, string Fix)? Blocking(IndexedPlugin p, string? shownAs = null)
        {
            string name = shownAs ?? p.Name;
            var pr = p.Problems;
            if (pr.ReadError != null)
                return ($"{name} couldn't be read: {pr.ReadError.TrimEnd('.')}", "check that the file is a whole Starfield plugin, then ask again");
            if (pr.MissingMasters.Count > 0)
            {
                string masters = string.Join(", ", pr.MissingMasters);
                int total = p.Segment?.Count ?? 0;
                return ($"{name} lists {(pr.MissingMasters.Count == 1 ? "a master that isn't" : "masters that aren't")} in Data: {masters}. "
                        + $"{pr.Unresolved:N0} of its {total:N0} records have FormIDs that can't be read without {(pr.MissingMasters.Count == 1 ? "it" : "them")}, and nothing is mapped by guess",
                    $"put {masters} in the Data folder (or name the right master in the plugin's header), then ask again");
            }
            return null;
        }

        /// <summary>The older-master problem in a table cell, in the words of the note: "105 overrides of a different type than their base".</summary>
        public static string TypeMismatchCell(int count) => $"{count:N0} override{(count == 1 ? "" : "s")} of a different type than {(count == 1 ? "its" : "their")} base";

        /// <summary>One note for a plugin that has problems but can be read, or null when it has none.</summary>
        /// <param name="ix">The index that holds the plugin. Null: the note has no example record.</param>
        public static string? Line(HeaderIndex? ix, IndexedPlugin p, string? shownAs = null)
        {
            var pr = p.Problems;
            var parts = new List<string>();
            if (pr.TypeMismatches.Count > 0)
            {
                string example = "";
                if (ix != null) try
                {
                    var r = ix.Describe(new RecordRef(p.Id, pr.TypeMismatches[0]));
                    var v = ix.Versions(r.FormKey);
                    if (v != null)
                    {
                        var b = ix.Describe(v.Holders.First(h => ix.Plugins[h.Plugin].Name.Equals(PluginOf(r.FormKey), StringComparison.OrdinalIgnoreCase)));
                        example = $" Example: its {r.Signature} {Label(r)} overrides the {b.Signature} {Label(b)}.";
                    }
                }
                catch (InvalidOperationException) { }
                parts.Add($"{pr.TypeMismatches.Count:N0} override(s) have a different type than their base: it was built for an older version of a master, so its overrides may sit on the wrong records.{example}");
            }
            if (pr.OverridesOfNothing.Count > 0) parts.Add($"{pr.OverridesOfNothing.Count:N0} override(s) of a record their master doesn't have.");
            // Mutagen refuses a plugin that holds a FormKey twice (C204, fixes.17): its error names each pair
            if (pr.Refused != null) parts.Add($"Mutagen refuses it ({pr.Refused.TrimEnd('.')}). It is left out of queries and of the winners, as Mutagen's load order leaves it out: the version before each of its overrides wins. record (every view) and check2 still read it, with Mutagen's read for diagnosis (DuplicateFormKeys = KeepLater: of each pair, the later record).");
            else if (pr.Duplicates > 0) parts.Add($"{pr.Duplicates:N0} record(s) repeat a FormKey the plugin already holds (of two in one group, Mutagen reads the later one only).");
            if (pr.MasterRefused != null) parts.Add($"its master {pr.MasterRefused} is refused by Mutagen, so it is left out of queries and of the winners too, as Mutagen's load order leaves it out.");
            if (pr.MastersNotLoaded.Count > 0) parts.Add($"master(s) not loaded: {string.Join(", ", pr.MastersNotLoaded)}.");
            if (pr.MastersLoadedAfter.Count > 0) parts.Add($"master(s) load after it: {string.Join(", ", pr.MastersLoadedAfter)}.");
            if (pr.BothSizeFlags) parts.Add("its header has both the Light (Small) and the Medium flag; it is read as Light.");
            return parts.Count == 0 ? null : $"{shownAs ?? p.Name}: {string.Join(" ", parts)}";
        }

        /// <summary>The same problems in a few words, for a table cell.</summary>
        public static string Short(IndexedPlugin p)
        {
            var pr = p.Problems;
            var parts = new List<string>();
            if (pr.ReadError != null) parts.Add("can't be read");
            if (pr.MissingMasters.Count > 0) parts.Add("missing master: " + string.Join(", ", pr.MissingMasters));
            if (pr.TypeMismatches.Count > 0) parts.Add(TypeMismatchCell(pr.TypeMismatches.Count));
            if (pr.OverridesOfNothing.Count > 0) parts.Add($"{pr.OverridesOfNothing.Count:N0} overrides of nothing");
            if (pr.Refused != null) parts.Add("refused by Mutagen");
            if (pr.Duplicates > 0) parts.Add($"{pr.Duplicates:N0} repeated FormKeys");
            if (pr.MasterRefused != null) parts.Add($"master refused by Mutagen: {pr.MasterRefused}");
            if (pr.MastersNotLoaded.Count > 0) parts.Add("master not loaded: " + string.Join(", ", pr.MastersNotLoaded));
            if (pr.MastersLoadedAfter.Count > 0) parts.Add("master loads later: " + string.Join(", ", pr.MastersLoadedAfter));
            if (pr.BothSizeFlags) parts.Add("both size flags");
            return string.Join("; ", parts);
        }

        /// <summary>
        /// Adds the problems of the plugins an answer was read from to the top of its notes. A plugin the answer needs
        /// (<paramref name="required"/>) whose FormIDs can't be read fails the answer: false is returned and the answer
        /// holds the error. Such a plugin among the others is left out, and a note says so (<paramref name="leftOut"/>
        /// false: the note only states the problem, for an answer that describes the plugin itself).
        /// </summary>
        public static bool Add(EngineAnswer answer, IEnumerable<(HeaderIndex Ix, IndexedPlugin Plugin, string? ShownAs)> readFrom, bool required, bool leftOut = true, int most = 10)
        {
            var seen = new HashSet<IndexedPlugin>();
            int more = 0, lines = 0;
            foreach (var (ix, p, shownAs) in readFrom)
            {
                if (!seen.Add(p)) continue;
                if (Blocking(p, shownAs) is { } block)
                {
                    if (required) { answer.Fail(block.What, block.Fix); return false; }
                    answer.Problem(!leftOut ? $"{block.What}." : p.Segment == null ? $"{block.What}. It was left out of this answer." : $"{block.What}. Those records were left out of this answer.");
                    continue;
                }
                if (Line(ix, p, shownAs) is not { } line) continue;
                if (lines++ < most) answer.Problem(line); else more++;
            }
            if (more > 0) answer.Problem($"{more} more plugin(s) of this answer have problems: see loadorder, view \"problems\".");
            return true;
        }

        static string PluginOf(string formKey) => formKey[(formKey.IndexOf(':') + 1)..];

        // ---------------------------------------------------------------- For the libraries
        static readonly HashSet<string> _said = new(StringComparer.Ordinal);

        /// <summary>
        /// The note for a plugin that a library read (the comparisons, the reports): the same one line every tool of the
        /// engine gives, so one problem has one wording. A plugin whose FormIDs can't be read gets the line that says
        /// why. Null when the plugin has no problem. (CompareSession.ProblemNote is set to this.)
        /// </summary>
        public static string? ForLibrary(HeaderIndex? ix, IndexedPlugin p, string shownAs)
        {
            // A library that doesn't pass the index: the plugin is the main index's when that holds this very object
            if (ix == null)
            {
                try { var main = EngineIndex.Get().Index; if (ReferenceEquals(main.Plugin(p.Name), p)) ix = main; }
                catch (Exception e) when (e is System.IO.IOException or InvalidOperationException or UnauthorizedAccessException) { }
            }
            string? note = Blocking(p, shownAs) is { } block ? block.What.TrimEnd('.') + "." : Line(ix, p, shownAs);
            if (note != null) lock (_said) _said.Add(note);
            return note;
        }

        /// <summary>True for a note this class worded for a library: it leads the answer's notes.</summary>
        public static bool IsOurs(string note) { lock (_said) return _said.Contains(note); }

        /// <summary>EditorID (FormID:plugin), the way records are named to people.</summary>
        public static string Label(RecordInfo r) => $"{r.EditorId ?? "[no EditorID]"} ({r.FormKey})";
    }
}
