// The five kinds of comparison (ARCHITECTURE.md section 9), each on the one diff engine:
//   Versions            one record across the load order
//   Records             two records
//   PluginVsMasters     what a plugin changes in its masters, and which overrides change nothing
//   Plugins             two versions of one mod, or a patch against the plugin it masters
//   PatchVsReference    two patches for the same masters
// Each gives an answer object (structured, and text under the output contract). This file holds what they share.
using System.Diagnostics;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Compare;

/// <summary>How the records of two files are paired.</summary>
public enum MatchBy
{
    /// <summary>By FormKey: the default, and always for overrides.</summary>
    FormKey,
    /// <summary>A plugin's own records by record type and EditorID. Only when asked. EditorIDs a file holds more
    /// than once are listed and counted, and those records are paired by FormKey.</summary>
    EditorId,
}

public sealed partial class Comparer
{
    public CompareSession Session { get; }
    HeaderIndex Index => Session.Index;

    /// <summary>For two versions of one plugin with the same masters: a pair of records with equal bytes in the two
    /// files is equal, and isn't read. Off: every pair is read through Mutagen and compared (tests, and doubt).</summary>
    public bool TrustEqualBytes { get; set; } = true;

    public Comparer(CompareSession session) { Session = session; }

    DiffOptions Options(Func<string, string> label, Func<string, string>? canonA = null, Func<string, string>? canonB = null) =>
        new() { Label = label, CanonA = canonA, CanonB = canonB, Language = Session.Language.ToString() };

    RecordDiff Diff(RecordView a, RecordView b, DiffOptions options) => Session.Differ.Diff(a.Type, a.Json, b.Type, b.Json, options);

    /// <summary>
    /// The version of a record before a plugin's own: the last of the plugin's masters, in the order its header
    /// lists them, that holds the record. Null when none does (an override of nothing).
    /// </summary>
    (RecordEngine Engine, RecordRef Ref, string Plugin)? Before(PluginSide side, string owner, uint localId)
    {
        var masters = side.Masters;
        for (int i = masters.Count - 1; i >= 0; i--)
        {
            var engine = Session.EngineFor(side, masters[i]);
            if (CompareSession.Find(engine.Index, masters[i], owner, localId) is { } r) return (engine, r, engine.Index.Plugins[r.Plugin].Name);
        }
        return null;
    }

    /// <summary>The usual frame of a comparison: check the stamps, run, check them again, add source and cost.</summary>
    T Run<T>(PluginSide[] sides, Func<T> work) where T : CompareAnswer
    {
        var clock = Stopwatch.StartNew();
        var uses = Session.Use(sides);
        T answer;
        try { answer = work(); }
        catch (RecordException e) { throw new CompareException(e.Message); }
        Session.Confirm(uses, answer);
        int at = 0; // the files compared come first in the source line, the masters read for them after
        foreach (var s in sides)
        {
            string line = CompareSession.SourceLine(s);
            if (!answer.Sources.Contains(line)) answer.Sources.Insert(Math.Min(at++, answer.Sources.Count), line);
            if (!s.InData && s.Plugin.Header is { } h && (h.Flags & 0x80) != 0)
                answer.Notes.Add($"{s.Shown} is localized: its texts come from string files named after the plugin, looked for beside the file and in Data's archives. Texts that aren't found read as empty.");
            foreach (string note in CompareSession.ProblemNotes(s.Index, s.Plugin, s.InData ? s.Name : s.Shown))
                if (!answer.Notes.Contains(note)) answer.Notes.Add(note);
        }
        answer.Seconds = clock.Elapsed.TotalSeconds;
        return answer;
    }

    /// <summary>The sources of a plugin's masters, added once each.</summary>
    void AddMasterSources(CompareAnswer answer, PluginSide side, IEnumerable<string> used)
    {
        foreach (var m in used.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var p = Session.EngineFor(side, m).Index.Plugin(m);
            if (p == null) continue;
            string line = CompareSession.SourceLine(p);
            if (!answer.Sources.Contains(line)) answer.Sources.Add(line);
        }
    }

    static bool Matches(RecordInfo info, string filter) =>
        (info.EditorId != null && info.EditorId.Contains(filter, StringComparison.OrdinalIgnoreCase))
        || info.FormKey.Equals(filter, StringComparison.OrdinalIgnoreCase)
        || (HeaderIndex.TryParseFormKey(filter, out var p, out var id) && info.FormKey.Equals($"{id:X6}:{p}", StringComparison.OrdinalIgnoreCase))
        || info.Signature.Equals(filter, StringComparison.OrdinalIgnoreCase);
}
