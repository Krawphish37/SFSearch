// A form list's entries as the game uses it (GAPS S61). A form list can name another form list in AddToList (ANAM;
// Mutagen's FormList.AddToList, a link to a FormList; xEdit's "ANAM - Add To List"): the game adds its entries to that
// list. So a list's entries are its own Items, then the entries of every form list whose winning version names it in
// AddToList, and on from those to the lists that name them. Each list is taken once: a cycle (A adds to B, B adds to A)
// ends at a list already taken, and every list in it gets the entries of the others.
// Which lists name which is read from the bytes of the FLST records that win in the load order (all cores), once per
// command: 3,909 records in the 223 loaded plugins of this Data folder.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Reports;

/// <summary>A form list's entries as the game uses it: its own Items and those other form lists add through
/// AddToList. <paramref name="AddedBy"/>: the form lists whose entries were added (FormKeys), nearest first.</summary>
public sealed record ListEntries(IReadOnlyList<string> Entries, IReadOnlyList<string> AddedBy)
{
    public static ListEntries Own(IReadOnlyList<string> entries) => new(entries, Array.Empty<string>());
}

/// <summary>Which form lists add their entries to which through AddToList, on the versions that win in the load order.</summary>
public sealed class FormListAdds
{
    readonly Dictionary<string, List<string>> _into = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _addsTo = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, IReadOnlyList<string>?> _own = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The form lists read (winning versions), their plugins, and those that couldn't be read.</summary>
    public int Records { get; private init; }
    public int Plugins { get; private init; }
    public int Unreadable { get; private init; }
    public double Milliseconds { get; private set; }
    /// <summary>How many winning form lists name a list in AddToList.</summary>
    public int Adding => _addsTo.Count;
    /// <summary>For the answer's cost line.</summary>
    public string Cost => $"the AddToList of {Records:N0} form list{(Records == 1 ? "" : "s")} of {Plugins:N0} plugin{(Plugins == 1 ? "" : "s")}, from the files' bytes (winning versions; {Adding:N0} name a list; {Milliseconds:N0} ms)";

    /// <summary>The form lists whose winning version names this list in AddToList (FormKeys), in load order.</summary>
    public IReadOnlyList<string> Into(string formKey) =>
        _into.TryGetValue(ReportSession.Canon(formKey), out var l) ? l : Array.Empty<string>();

    /// <summary>The list this form list's winning version names in AddToList, or null.</summary>
    public string? AddsTo(string formKey) => _addsTo.GetValueOrDefault(ReportSession.Canon(formKey));

    /// <summary>
    /// A form list's entries as the game uses it: <paramref name="own"/> (its winning version's Items), then the entries
    /// of each list that adds to it, nearest first, each entry once. Null when its own entries can't be read.
    /// </summary>
    public ListEntries? Entries(string formKey, Func<string, IReadOnlyList<string>?> read)
    {
        // Each list's own entries are read once per command
        IReadOnlyList<string>? own(string k) => _own.TryGetValue(k, out var have) ? have : _own[k] = read(k);
        string key = ReportSession.Canon(formKey);
        if (own(key) is not { } mine) return null;
        if (Into(key).Count == 0) return ListEntries.Own(mine);
        var entries = new List<string>(mine);
        var have = new HashSet<string>(mine, StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
        var queue = new Queue<string>();
        foreach (var x in Into(key)) if (taken.Add(x)) queue.Enqueue(x);
        while (queue.Count > 0)
        {
            string x = queue.Dequeue();
            added.Add(x);
            if (own(x) is { } theirs) foreach (var e in theirs) if (have.Add(e)) entries.Add(e);
            foreach (var y in Into(x)) if (taken.Add(y)) queue.Enqueue(y);
        }
        return new ListEntries(entries, added);
    }

    /// <summary>The lists this one's entries go on to through AddToList, nearest first (each once; a cycle stops).</summary>
    public IReadOnlyList<string> Chain(string formKey)
    {
        var chain = new List<string>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ReportSession.Canon(formKey) };
        for (string? next = AddsTo(formKey); next != null && taken.Add(next); next = AddsTo(next)) chain.Add(next);
        return chain;
    }

    static readonly ConditionalWeakTable<object, FormListAdds> PerCommand = new();

    /// <summary>Read once per command (<paramref name="command"/>: the command's ReadScope, or any object that lives as long).</summary>
    public static FormListAdds For(object command, HeaderIndex index) => PerCommand.GetValue(command, _ => Read(index));

    /// <summary>Reads the AddToList of every form list that wins in the load order, from the plugins' bytes.</summary>
    public static FormListAdds Read(HeaderIndex index)
    {
        var clock = Stopwatch.StartNew();
        var found = new ConcurrentBag<(long Rank, string From, string To)>();
        int records = 0, plugins = 0, unreadable = 0;
        var loaded = index.Plugins.Where(p => p.Order.Enabled && p.LeftOut == null && p.Segment != null).ToList();
        Parallel.ForEach(loaded, p =>
        {
            var seg = p.Segment!;
            List<int>? flst = null;
            foreach (int i in seg.EntriesOf("FLST"))
                if (i < seg.FirstUnresolved && index.IsWinner(new RecordRef(p.Id, i))) (flst ??= new()).Add(i);
            if (flst == null) return;
            Interlocked.Increment(ref plugins);
            Interlocked.Add(ref records, flst.Count);
            var ids = p.FormIds;
            using var file = File.OpenHandle(p.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            foreach (int i in flst)
            {
                var e = seg[i];
                RawRecord raw;
                try { raw = RawRecord.Read(file, e.Offset); }
                catch (Exception ex) when (ex is RecordException or IOException or InvalidDataException or ArgumentException) { Interlocked.Increment(ref unreadable); continue; }
                if (raw.Signature != "FLST" || raw.StoredSize != e.Size) { Interlocked.Increment(ref unreadable); continue; }
                foreach (var s in raw.Subrecords)
                {
                    if (s.Signature != "ANAM" || s.Length != 4) continue;
                    uint id = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(raw.Content(s));
                    if (id == 0 || !ids.TryResolve(id, out string owner, out uint local)) continue;
                    found.Add((p.Order.Position, ReportSession.Canon(index.Describe(new RecordRef(p.Id, i)).FormKey), $"{local:X6}:{owner}"));
                }
            }
        });
        var adds = new FormListAdds { Records = records, Plugins = plugins, Unreadable = unreadable };
        foreach (var (_, from, to) in found.OrderBy(f => f.Rank).ThenBy(f => f.From, StringComparer.OrdinalIgnoreCase))
        {
            // Only a form list is added to (Mutagen's AddToList links a FormList); a link to another type adds nothing
            if (index.Versions(to) is not { } v || index.Describe(v.Winner >= 0 ? v.Holders[v.Winner] : v.Holders[0]).Signature != "FLST") continue;
            adds._addsTo[from] = to;
            if (!adds._into.TryGetValue(to, out var list)) adds._into[to] = list = new List<string>();
            list.Add(from);
        }
        adds.Milliseconds = clock.Elapsed.TotalMilliseconds;
        return adds;
    }
}
