// "What points at this record?" for the reports, behind a small interface, so that the link index (ARCHITECTURE.md
// section 5) can be put behind it later without changing a report.
// ScanBackLinks answers by walking records: the first question reads every record of the plugins searched (as JSON,
// walked beside the schema) and remembers, for each record named by a link, which records name it. The places inside
// those records are worked out when a target is asked for. ByteBackLinks gives the same links from the bytes: for
// each record asked about, only the records whose bytes hold its FormID are read and walked (GAPS S70).
using System.Buffers;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
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
    /// <summary>Says which records will be asked about next, so an answer that searches the files can search for them
    /// together. It changes no answer; one that has them at hand does nothing.</summary>
    void Prefetch(IEnumerable<string> formKeys) { }
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

    /// <summary>A plugin with more placed references than this isn't searched for placed copies (the note says so): the
    /// scan reads every record through Mutagen, where Starfield.esm's 3.29 million placed references would take minutes.</summary>
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

/// <summary>
/// "What points at this record", from the plugins' bytes (GAPS S70). For each record asked about, the records of the
/// searched plugins whose bytes hold its FormID, as each plugin writes it, are found by a byte search (<see
/// cref="ByteHolders"/>: all plugins at once, all cores, many records asked about in one pass when the caller says
/// which come next); only those are read through Mutagen and walked for their links, as the scan walks every record.
/// It searches each plugin's records that aren't world data, and its placed references: all of them by default (GAPS
/// S100: a search of their bytes takes seconds even for Starfield.esm's 3.29 million). With <see cref="PlacedLimit"/>
/// set to the scan's limit, it searches exactly the records the scan reads, so the links are the scan's, in the same order.
/// </summary>
public sealed class ByteBackLinks : IBackLinks
{
    readonly ReadScope _scope;
    readonly List<(RecordEngine Engine, IndexedPlugin Plugin)> _plugins;
    readonly List<string> _notes = new();
    readonly Dictionary<string, IReadOnlyList<BackLink>> _answers = new(StringComparer.OrdinalIgnoreCase);
    ByteHolders? _holders;
    readonly HolderLinks _links;
    long _hits; int _lookups, _worldLeftOut; double _seconds;

    /// <summary>A plugin with more placed references than this isn't searched for placed copies (the note says so). None
    /// by default; the scan's limit (<see cref="ScanBackLinks.PlacedLimit"/>) to search what the scan reads.</summary>
    public int PlacedLimit { get; init; } = int.MaxValue;

    public ByteBackLinks(ReportSession session, ReadScope scope, IEnumerable<(RecordEngine Engine, IndexedPlugin Plugin)> plugins)
    {
        _scope = scope; _plugins = plugins.ToList(); _links = new HolderLinks(session);
    }

    public IReadOnlyList<string> Plugins => _plugins.Select(p => p.Plugin.Name).ToList();
    public IReadOnlyList<string> Notes
    {
        get
        {
            var holders = Prepare();
            int unread = _links.Unread;
            var notes = new List<string>(_notes);
            if (unread > 0) notes.Add($"{unread:N0} record(s) hold the FormID of a record asked about but couldn't be read: their links weren't taken.");
            if (holders.NotInflated > 0) notes.Add($"{holders.NotInflated:N0} compressed record(s) couldn't be inflated and weren't searched.");
            return notes;
        }
    }
    public string Cost => $"the bytes of {_holders?.Records ?? 0:N0} records of {_plugins.Count:N0} plugin{(_plugins.Count == 1 ? "" : "s")} for the FormIDs of {_lookups:N0} record{(_lookups == 1 ? "" : "s")} ({_holders?.Passes ?? 0:N0} pass{(_holders?.Passes == 1 ? "" : "es")}, {_hits:N0} hits), {_links.Read:N0} of them read for their links ({_seconds + (_holders?.Seconds ?? 0):F1} s" +
        (_worldLeftOut > 0 ? $"; {_worldLeftOut:N0} world records that aren't placed references were left out)" : ")");

    static readonly HashSet<string> Placed = new(StringComparer.Ordinal) { "REFR", "ACHR" };

    /// <summary>The entries of each plugin that are searched (the ones the scan reads), from the header index only.</summary>
    ByteHolders Prepare()
    {
        if (_holders != null) return _holders;
        var entries = new int[]?[_plugins.Count];
        for (int pi = 0; pi < _plugins.Count; pi++)
        {
            var (_, p) = _plugins[pi];
            var seg = p.Segment;
            if (seg == null || p.Problems.MissingMasters.Count > 0) { _notes.Add($"{p.Name} couldn't be searched: {p.Problems.ReadError ?? "a master is missing"}."); continue; }
            var placedSig = seg.Signatures.Select(x => Placed.Contains(x.Signature)).ToArray();
            int placed = seg.Signatures.Where(x => Placed.Contains(x.Signature)).Sum(x => x.Count);
            bool withPlaced = placed <= PlacedLimit;
            var list = new List<int>(seg.FirstUnresolved);
            for (int i = 0; i < seg.FirstUnresolved; i++)
            {
                var e = seg[i];
                if (ReportSession.IsWorld(seg, e) && !(withPlaced && placedSig[e.Signature])) { _worldLeftOut++; continue; }
                list.Add(i);
            }
            entries[pi] = list.ToArray();
            if (!withPlaced) _notes.Add($"{p.Name} has {placed:N0} placed references: they weren't searched for placed copies.");
        }
        return _holders = new ByteHolders(_plugins.Select(x => x.Plugin).ToList(), entries, _scope);
    }

    /// <summary>The records asked about next are searched for together, in one pass over each plugin.</summary>
    public void Prefetch(IEnumerable<string> formKeys) => Prepare().Prefetch(formKeys.Where(k => !_answers.ContainsKey(ReportSession.Canon(k))));

    public IReadOnlyList<BackLink> Into(string formKey)
    {
        string key = ReportSession.Canon(formKey);
        if (_answers.TryGetValue(key, out var have)) return have;
        var hits = Prepare().Of(key);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = new List<BackLink>();
        _lookups++;
        for (int pi = 0; pi < _plugins.Count; pi++)
        {
            if (hits[pi] is not { Count: > 0 } entries) continue;
            var (engine, p) = _plugins[pi];
            _hits += entries.Count;
            _scope.Touch(engine, p.Name);
            _links.ReadAll(engine, p, entries);
            foreach (int entry in entries) _links.Into(engine, p, entry, key, result);
        }
        _seconds += clock.Elapsed.TotalSeconds;
        return _answers[key] = result;
    }
}

/// <summary>
/// The links of the records read to answer "what points at this", each record read through Mutagen and walked once per
/// command, however many of the records asked about it names (a quest that holds a dozen of the walk's lists). The
/// records one question needs are read on all cores (<see cref="ReadAll"/>): a record engine reads open plugins on many
/// threads, as SFSearch.Query's reader does. (The shared record-reading pipeline of J29/J35 can take this loop over.)
/// </summary>
public sealed class HolderLinks
{
    readonly ReportSession _s;
    readonly LinkWalker _walker;
    readonly Dictionary<(RecordEngine Engine, int Plugin, int Entry), Walked?> _done = new();

    sealed record Walked(BackLink Holder, List<(string Target, string Path)> Links);

    /// <summary>Records read through Mutagen, and records that couldn't be read.</summary>
    public int Read { get; private set; }
    public int Unread { get; private set; }

    public HolderLinks(ReportSession session) { _s = session; _walker = new LinkWalker(session.Schema); }

    /// <summary>
    /// Reads and walks the records of one plugin not read yet, on all cores. The plugin must be open (the command's
    /// <see cref="ReadScope.Touch"/> first, on the calling thread). <see cref="Into"/> then finds them read.
    /// </summary>
    public void ReadAll(RecordEngine engine, IndexedPlugin plugin, IReadOnlyList<int> entries)
    {
        var todo = entries.Where(e => !_done.ContainsKey((engine, plugin.Id, e))).Distinct().ToList();
        if (todo.Count < 2) return;
        var walked = new Walked?[todo.Count];
        try
        {
            Parallel.For(0, todo.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) }, i =>
            {
                try { walked[i] = Walk(engine, plugin, todo[i]); }
                catch (CompareException) { walked[i] = null; }
            });
        }
        catch (AggregateException e) when (e.InnerExceptions.Count > 0)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
        }
        for (int i = 0; i < todo.Count; i++)
        {
            _done[(engine, plugin.Id, todo[i])] = walked[i];
            if (walked[i] == null) Unread++; else Read++;
        }
    }

    Walked Walk(RecordEngine engine, IndexedPlugin plugin, int entry)
    {
        var hit = new Hit(engine, new RecordRef(plugin.Id, entry));
        var view = _s.Compare.View(engine, hit.Ref);
        var info = view.Info;
        var links = new List<(string, string)>();
        _walker.Walk(view.Type, view.Json, link => links.Add((ReportSession.Canon(link.FormKey), link.Path)));
        return new Walked(new BackLink
        {
            From = hit, FromKey = ReportSession.Canon(info.FormKey), Signature = info.Signature, Type = view.Type, EditorId = info.EditorId,
            Plugin = plugin.Name, Path = "", Parent = info.Parent == null ? null : ReportSession.Canon(info.Parent),
        }, links);
    }

    /// <summary>
    /// The links of this record that name <paramref name="key"/> (a canonical FormKey), in the record's order. False
    /// when the record can't be read through Mutagen.
    /// </summary>
    public bool Into(RecordEngine engine, IndexedPlugin plugin, int entry, string key, List<BackLink> into)
    {
        if (!_done.TryGetValue((engine, plugin.Id, entry), out var walked))
        {
            try { walked = Walk(engine, plugin, entry); Read++; }
            catch (CompareException) { walked = null; Unread++; }
            _done[(engine, plugin.Id, entry)] = walked;
        }
        if (walked == null) return false;
        var h = walked.Holder;
        foreach (var (target, path) in walked.Links)
            if (target.Equals(key, StringComparison.OrdinalIgnoreCase))
                into.Add(new BackLink { From = h.From, FromKey = h.FromKey, Signature = h.Signature, Type = h.Type, EditorId = h.EditorId, Plugin = h.Plugin, Path = path, Parent = h.Parent });
        return true;
    }
}

/// <summary>
/// Which records of some plugins hold a record's FormID in their bytes, as each plugin writes it (<see
/// cref="FormIdPattern"/>): only the given entries of each plugin are searched, compressed ones inflated. Many records
/// asked about are searched for in one pass over each plugin (<see cref="Prefetch"/>), the plugins at once, each on all
/// cores (a plain parallel loop: the shared record-reading pipeline of J29/J35 can take this loop over when it lands).
/// A hit only says the bytes are there: the caller reads each one to see whether a link names the record. Nothing is
/// kept past the command.
/// </summary>
public sealed unsafe class ByteHolders
{
    readonly IReadOnlyList<IndexedPlugin> _plugins;
    readonly IReadOnlyList<int[]?> _entries;
    readonly ReadScope _scope;
    readonly Dictionary<string, List<int>?[]> _found = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The records that can be searched (each plugin's entries given), records searched (summed over the
    /// passes), passes made, compressed records that couldn't be inflated.</summary>
    public long Records { get; }
    public long Searched { get; private set; }
    public int Passes { get; private set; }
    public int NotInflated { get; private set; }
    public double Seconds { get; private set; }

    /// <param name="entries">For each plugin, the entries to search (sorted); null: none.</param>
    public ByteHolders(IReadOnlyList<IndexedPlugin> plugins, IReadOnlyList<int[]?> entries, ReadScope scope)
    {
        _plugins = plugins; _entries = entries; _scope = scope;
        Records = entries.Sum(e => (long)(e?.Length ?? 0));
    }

    /// <summary>For each plugin, its entries that hold the record's FormID, in entry order (null: none). Searched now, unless <see cref="Prefetch"/> did.</summary>
    public List<int>?[] Of(string formKey)
    {
        string key = ReportSession.Canon(formKey);
        if (!_found.ContainsKey(key)) Search(new[] { key });
        return _found[key];
    }

    /// <summary>Searches for these records together, in one pass over each plugin.</summary>
    public void Prefetch(IEnumerable<string> formKeys)
    {
        var keys = formKeys.Select(ReportSession.Canon).Where(k => !_found.ContainsKey(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (keys.Count > 0) Search(keys);
    }

    void Search(IReadOnlyList<string> keys)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // For each plugin, each record as its file can name it: (its owner among the plugin's masters, its local ID)
        var targets = new (int Owner, uint Local)?[_plugins.Count][];
        bool anyPlugin = false;
        for (int pi = 0; pi < _plugins.Count; pi++)
        {
            var p = _plugins[pi];
            var row = new (int, uint)?[keys.Count];
            bool any = false;
            if (_entries[pi] is { Length: > 0 } && p.Segment is { } seg)
                for (int k = 0; k < keys.Count; k++)
                {
                    if (!HeaderIndex.TryParseFormKey(keys[k], out var owner, out var localId)) continue;
                    int o = seg.OwnerOf(owner);
                    // The plugin doesn't know the record's plugin, or can't write that FormID: it can't point at it
                    if (o < 0 || FormIdPattern.For(p, o, localId) == null) continue;
                    row[k] = (o, localId);
                    any = true;
                }
            if (any) { targets[pi] = row; _scope.TouchBytes(p); anyPlugin = true; }
        }
        var found = new List<int>[]?[_plugins.Count];
        long searched = 0; int bad = 0;
        if (anyPlugin)
            Parallel.For(0, _plugins.Count, pi =>
            {
                if (targets[pi] is not { } row) return;
                found[pi] = SearchMany(pi, Matcher.For(_plugins[pi], row), keys.Count, ref searched, ref bad);
            });
        for (int k = 0; k < keys.Count; k++)
            _found[keys[k]] = found.Select(f => f?[k] is { Count: > 0 } l ? l : null).ToArray();
        Searched += searched; NotInflated += bad;
        if (anyPlugin) Passes++;
        Seconds += clock.Elapsed.TotalSeconds;
    }

    /// <summary>Tells which of the records asked about a record's data names by its bytes.</summary>
    abstract class Matcher
    {
        /// <summary>Adds to <paramref name="into"/> the place of each record asked about whose FormID the data holds, each once.</summary>
        public abstract void Match(ReadOnlySpan<byte> data, List<int> into);

        /// <summary>A few records: each FormID's own search (vectorized). More: one look-up for each four bytes of the data.</summary>
        public static Matcher For(IndexedPlugin plugin, (int Owner, uint Local)?[] targets) =>
            targets.Count(t => t != null) <= 8 ? new EachPattern(plugin, targets) : new Hashed(plugin, targets);
    }

    sealed class EachPattern : Matcher
    {
        readonly FormIdPattern?[] _patterns;
        public EachPattern(IndexedPlugin plugin, (int Owner, uint Local)?[] targets) =>
            _patterns = targets.Select(t => t is { } x ? FormIdPattern.For(plugin, x.Owner, x.Local) : null).ToArray();
        public override void Match(ReadOnlySpan<byte> data, List<int> into)
        {
            for (int k = 0; k < _patterns.Length; k++) if (_patterns[k] is { } pattern && pattern.Matches(data)) into.Add(k);
        }
    }

    /// <summary>
    /// The rule of <see cref="FormIdPattern"/> for many records at once: each four bytes of the data are looked up. A
    /// master's record is one exact FormID. The plugin's own record is its local ID with any index at or past the
    /// plugin's masters in one of the three numberings: by the top byte (local ID in the low three bytes, top byte at or
    /// past the Full masters and below FD), FD (low two bytes, then an index at or past the Medium masters), FE (twelve
    /// bits of local ID, twelve of index at or past the Small masters).
    /// </summary>
    sealed class Hashed : Matcher
    {
        readonly Dictionary<uint, List<int>> _exact = new(), _own = new();
        readonly bool[] _low = new bool[4096];
        readonly int _full, _medium, _small;

        public Hashed(IndexedPlugin plugin, (int Owner, uint Local)?[] targets)
        {
            var ids = plugin.FormIds;
            _full = ids.FullMasters; _medium = ids.MediumMasters; _small = ids.SmallMasters;
            for (int k = 0; k < targets.Length; k++)
            {
                if (targets[k] is not { } t) continue;
                uint value;
                Dictionary<uint, List<int>> into;
                if (t.Owner == ids.SelfOwner) { value = t.Local; into = _own; }
                else if (ids.Raw(t.Owner, t.Local) is uint raw) { value = raw; into = _exact; }
                else continue;
                if (!into.TryGetValue(value, out var list)) into[value] = list = new List<int>(1);
                list.Add(k);
                _low[value & 0xFFF] = true;
            }
        }

        public override void Match(ReadOnlySpan<byte> data, List<int> into)
        {
            int first = into.Count;
            for (int at = 0; at + 4 <= data.Length; at++)
            {
                uint v = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
                if (!_low[v & 0xFFF]) continue;
                if (_exact.Count > 0 && _exact.TryGetValue(v, out var exact)) into.AddRange(exact);
                if (_own.Count == 0) continue;
                uint top = v >> 24;
                List<int>? own = null;
                if (top < 0xFD) { if (top >= _full) _own.TryGetValue(v & 0xFFFFFF, out own); }
                else if (top == 0xFD) { if (((v >> 16) & 0xFF) >= _medium) _own.TryGetValue(v & 0xFFFF, out own); }
                else if (top == 0xFE) { if (((v >> 12) & 0xFFF) >= _small) _own.TryGetValue(v & 0xFFF, out own); }
                if (own != null) into.AddRange(own);
            }
            // Each record asked about once
            if (into.Count - first > 1)
            {
                var distinct = into.Skip(first).Distinct().ToList();
                into.RemoveRange(first, into.Count - first);
                into.AddRange(distinct);
            }
        }
    }

    // Compressed records inflated by the first pass, kept for the next ones (up to PlainLimit bytes; null: couldn't be
    // inflated), by (place of the plugin) << 32 | entry. Thrown away with the command.
    readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte[]?> _plain = new();
    long _plainBytes;
    const long PlainLimit = 256L << 20;

    /// <summary>One pass over a plugin's entries: for each record asked about, the entries whose data names it (sorted).</summary>
    List<int>[] SearchMany(int pi, Matcher matcher, int count, ref long searched, ref int notInflated)
    {
        var plugin = _plugins[pi];
        var entries = _entries[pi]!;
        var seg = plugin.Segment!;
        var result = new List<int>[count];
        for (int k = 0; k < count; k++) result[k] = new List<int>();
        using var fs = new FileStream(plugin.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = fs.Length;
        if (length == 0 || entries.Length == 0) return result;
        using var mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
        using var view = mmf.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
        byte* p = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        try
        {
            byte* file = p + view.PointerOffset;
            const int Piece = 4096;
            int pieces = (entries.Length + Piece - 1) / Piece;
            var hits = new List<(int Pattern, int Entry)>[pieces];
            long records = 0; int failed = 0;
            Parallel.For(0, pieces, n =>
            {
                byte[] plain = ArrayPool<byte>.Shared.Rent(1 << 16);
                var mine = new List<(int, int)>();
                var matched = new List<int>();
                long recs = 0; int bad = 0;
                try
                {
                    int to = Math.Min(entries.Length, (n + 1) * Piece);
                    for (int x = n * Piece; x < to; x++)
                    {
                        int i = entries[x];
                        var e = seg[i];
                        recs++;
                        if (e.Size < 4 || e.Offset + 24 + e.Size > length) continue;
                        byte* data = file + e.Offset + 24;
                        ReadOnlySpan<byte> span;
                        if (e.IsCompressed)
                        {
                            long at = (long)pi << 32 | (uint)i;
                            if (_plain.TryGetValue(at, out var kept))
                            {
                                if (kept == null) continue; // couldn't be inflated: counted the first time
                                span = kept;
                            }
                            else
                            {
                                uint plainSize = *(uint*)data;
                                if (plainSize > int.MaxValue - 64) { bad++; _plain[at] = null; continue; }
                                if (plain.Length < plainSize) { ArrayPool<byte>.Shared.Return(plain); plain = ArrayPool<byte>.Shared.Rent((int)plainSize); }
                                try
                                {
                                    using var packed = new UnmanagedMemoryStream(data + 4, e.Size - 4);
                                    using var zip = new ZLibStream(packed, CompressionMode.Decompress);
                                    zip.ReadExactly(plain, 0, (int)plainSize);
                                }
                                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException) { bad++; _plain[at] = null; continue; }
                                span = plain.AsSpan(0, (int)plainSize);
                                if (Interlocked.Add(ref _plainBytes, plainSize) <= PlainLimit) _plain[at] = span.ToArray();
                                else Interlocked.Add(ref _plainBytes, -plainSize);
                            }
                        }
                        else span = new ReadOnlySpan<byte>(data, (int)e.Size);
                        matched.Clear();
                        matcher.Match(span, matched);
                        foreach (int k in matched) mine.Add((k, i));
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(plain); }
                hits[n] = mine;
                Interlocked.Add(ref records, recs); Interlocked.Add(ref failed, bad);
            });
            // The pieces in order, so each pattern's entries come out in entry order
            foreach (var piece in hits) foreach (var (k, i) in piece) result[k].Add(i);
            Interlocked.Add(ref searched, records); Interlocked.Add(ref notInflated, failed);
        }
        finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
        return result;
    }
}
