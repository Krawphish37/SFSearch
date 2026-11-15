// "What points at this record?" for the reports, behind a small interface, so that the link index (ARCHITECTURE.md
// section 5) can be put behind it later without changing a report.
// Until the link index is in this worktree, ScanBackLinks answers by walking records: the first question reads
// every record of the plugins searched (as JSON, walked beside the schema) and remembers, for each record named by
// a link, which records name it. The places inside those records are worked out when a target is asked for.
using SFSearch.Compare;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Reports;

/// <summary>One link into a record: the record that holds the link, and where in it the link sits.</summary>
public sealed class BackLink
{
    /// <summary>The record that holds the link.</summary>
    public required Hit From { get; init; }
    public required string FromKey { get; init; }
    public required string Signature { get; init; }
    /// <summary>The Mutagen type of the record that holds the link.</summary>
    public required string Type { get; init; }
    public string? EditorId { get; init; }
    public required string Plugin { get; init; }
    /// <summary>Where the link sits: Entries[3].Reference.</summary>
    public required string Path { get; init; }
    /// <summary>The record the holder is nested in (a placed reference's cell, a response's topic), as a FormKey.</summary>
    public string? Parent { get; init; }
}

public interface IBackLinks
{
    /// <summary>The plugins whose records are searched.</summary>
    IReadOnlyList<string> Plugins { get; }
    /// <summary>What was searched and what wasn't, for the answer's notes.</summary>
    IReadOnlyList<string> Notes { get; }
    /// <summary>What answering cost so far, for the answer's cost line.</summary>
    string Cost { get; }
    /// <summary>Every link in the searched plugins that names this record.</summary>
    IReadOnlyList<BackLink> Into(string formKey);
}

public sealed class ScanBackLinks : IBackLinks
{
    readonly ReportSession _s;
    readonly ReadScope _scope;
    readonly List<(RecordEngine Engine, IndexedPlugin Plugin)> _plugins;
    readonly LinkWalker _walker;
    readonly List<string> _notes = new();
    // target FormKey → the records that name it, each as (place in _plugins) << 32 | entry
    Dictionary<string, List<long>>? _byTarget;
    readonly Dictionary<string, IReadOnlyList<BackLink>> _answers = new(StringComparer.OrdinalIgnoreCase);
    int _read, _unread, _worldLeftOut; long _links; double _seconds;

    /// <summary>A plugin with more placed references than this isn't searched for placed copies (the note says so).</summary>
    public int PlacedLimit { get; init; } = 300_000;

    public ScanBackLinks(ReportSession session, ReadScope scope, IEnumerable<(RecordEngine Engine, IndexedPlugin Plugin)> plugins)
    {
        _s = session; _scope = scope; _plugins = plugins.ToList(); _walker = new LinkWalker(session.Schema);
    }

    public IReadOnlyList<string> Plugins => _plugins.Select(p => p.Plugin.Name).ToList();
    public IReadOnlyList<string> Notes { get { Build(); return _notes; } }
    public string Cost => $"{_read:N0} records of {_plugins.Count:N0} plugin{(_plugins.Count == 1 ? "" : "s")} for their links ({_links:N0} links, {_seconds:F1} s" +
        (_worldLeftOut > 0 ? $"; {_worldLeftOut:N0} world records that aren't placed references were left out)" : ")");

    static readonly HashSet<string> Placed = new(StringComparer.Ordinal) { "REFR", "ACHR" };

    void Build()
    {
        if (_byTarget != null) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _byTarget = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        for (int pi = 0; pi < _plugins.Count; pi++)
        {
            var (engine, p) = _plugins[pi];
            var seg = p.Segment;
            if (seg == null || p.Problems.MissingMasters.Count > 0) { _notes.Add($"{p.Name} couldn't be searched: {p.Problems.ReadError ?? "a master is missing"}."); continue; }
            _scope.Touch(engine, p.Name);
            int placed = seg.Signatures.Where(x => Placed.Contains(x.Signature)).Sum(x => x.Count);
            bool withPlaced = placed <= PlacedLimit;
            int world = 0;
            for (int i = 0; i < seg.FirstUnresolved; i++)
            {
                var e = seg[i];
                if (ReportSession.IsWorld(seg, e) && !(withPlaced && Placed.Contains(seg.SignatureOf(e)))) { world++; continue; }
                RecordView view;
                try { view = _s.Compare.View(engine, new RecordRef(p.Id, i)); }
                catch (CompareException) { _unread++; continue; }
                _read++;
                long from = (long)pi << 32 | (uint)i;
                _walker.Walk(view.Type, view.Json, link =>
                {
                    _links++;
                    string key = ReportSession.Canon(link.FormKey);
                    if (!_byTarget.TryGetValue(key, out var list)) _byTarget[key] = list = new List<long>(1);
                    if (list.Count == 0 || list[^1] != from) list.Add(from);
                });
            }
            if (!withPlaced) _notes.Add($"{p.Name} has {placed:N0} placed references: they weren't searched for placed copies.");
            _worldLeftOut += world;
        }
        if (_unread > 0) _notes.Add($"{_unread:N0} record(s) couldn't be read and weren't searched.");
        _seconds += clock.Elapsed.TotalSeconds;
    }

    public IReadOnlyList<BackLink> Into(string formKey)
    {
        string key = ReportSession.Canon(formKey);
        if (_answers.TryGetValue(key, out var have)) return have;
        Build();
        var result = new List<BackLink>();
        if (_byTarget!.TryGetValue(key, out var holders))
            foreach (long h in holders)
            {
                var (engine, p) = _plugins[(int)(h >> 32)];
                var hit = new Hit(engine, new RecordRef(p.Id, (int)(uint)h));
                var view = _s.Compare.View(engine, hit.Ref);
                var info = view.Info;
                _walker.Walk(view.Type, view.Json, link =>
                {
                    if (!ReportSession.Canon(link.FormKey).Equals(key, StringComparison.OrdinalIgnoreCase)) return;
                    result.Add(new BackLink
                    {
                        From = hit, FromKey = ReportSession.Canon(info.FormKey), Signature = info.Signature, Type = view.Type, EditorId = info.EditorId,
                        Plugin = p.Name, Path = link.Path, Parent = info.Parent == null ? null : ReportSession.Canon(info.Parent),
                    });
                });
            }
        return _answers[key] = result;
    }
}
