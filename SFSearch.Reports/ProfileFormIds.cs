// profile --formids (GAPS S119): how plugins store the FormIDs in their records, across plugins, read from the files'
// bytes on all cores. A modder asks it to find the plugins that store FormIDs oddly before patching or merging them: a
// FormID past the master list (the game and Mutagen read it as the plugin's own record, but the index names no master),
// a Small (FE) or Medium (FD) form in a plugin of another size, or a plugin that names its own records in two forms. The
// Mutagen session's C64 needed a scratch tool to count the 4,112 FormIDs past the master list in 14 of the 370 plugins.
// Every record of the set goes through the record pipeline (found through the header index, inflated when compressed):
// its header's FormID (4 bytes at 12) and every link the bytes give (LinkPlan.Links: the fixed places, then the byte
// walk, the same links profile --subrecords "*" counts, held against Mutagen by sfsreport-tests link-census) are each
// classified against the plugin's masters with SFSearch's own reading of the three numberings (FormIdResolver,
// FormIdForms; check2's per-plugin "Own FormIDs" line reads the same way). Nothing goes through Mutagen.
// J98: Mutagen's fixes.20 makes the Starfield FormID-form logic public (C58: StoredFormIdForm with PastTheList and the
// master-index reading); when SFSearch moves to it, Kind() below classifies with Mutagen's code instead of
// FormIdResolver/FormIdForms, which C58's list of SFSearch deletions names.
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Reports;

/// <summary>How one stored FormID names its record, against the masters of the plugin that stores it.</summary>
public enum StoredKind : byte
{
    /// <summary>00000000: a null link (counted apart, never a header).</summary>
    Null,
    /// <summary>FFFFFFFF: Mutagen reads it as no record (FormKey.None).</summary>
    AllOnes,
    /// <summary>At or past a master that is missing from Data: whose record it is can't be told.</summary>
    Unresolved,
    MasterFull, MasterMedium, MasterSmall,
    OwnFull, OwnMedium, OwnSmall,
    PastFull, PastMedium, PastSmall,
    /// <summary>The plugin's own record in the Medium form (FD), in a plugin that isn't Medium.</summary>
    OwnMediumOtherSize,
    OwnSmallOtherSize,
    PastMediumOtherSize,
    PastSmallOtherSize,
}

public static class StoredKinds
{
    public const int Count = 16;

    /// <summary>
    /// How a stored FormID names its record in a plugin with these masters: a master's record (its index before the
    /// masters of its numbering end), the plugin's own record at its own index (the full index, FD or FE), or past it
    /// (an index further on: read as the plugin's own record). The game reads the full index the same in a plugin of any
    /// size; FE is the Small plugins' form and FD the Medium plugins', and either one in a plugin of another size is told
    /// apart.
    /// </summary>
    public static StoredKind Kind(uint raw, FormIdResolver ids, MasterStyle self)
    {
        if (raw == 0) return StoredKind.Null;
        if (raw == uint.MaxValue) return StoredKind.AllOnes;
        if (!ids.TryResolve(raw, out int owner, out _)) return StoredKind.Unresolved;
        var form = FormIdForms.FormOf(raw);
        if (owner != ids.SelfOwner) return form switch { StoredForm.Medium => StoredKind.MasterMedium, StoredForm.Small => StoredKind.MasterSmall, _ => StoredKind.MasterFull };
        FormIdForms.IsOwn(raw, ids, out _, out bool atOwn);
        bool other = !FormIdForms.Allowed(form, self);
        return (form, atOwn, other) switch
        {
            (StoredForm.Medium, true, false) => StoredKind.OwnMedium,
            (StoredForm.Medium, true, true) => StoredKind.OwnMediumOtherSize,
            (StoredForm.Medium, false, false) => StoredKind.PastMedium,
            (StoredForm.Medium, false, true) => StoredKind.PastMediumOtherSize,
            (StoredForm.Small, true, false) => StoredKind.OwnSmall,
            (StoredForm.Small, true, true) => StoredKind.OwnSmallOtherSize,
            (StoredForm.Small, false, false) => StoredKind.PastSmall,
            (StoredForm.Small, false, true) => StoredKind.PastSmallOtherSize,
            (_, true, _) => StoredKind.OwnFull,
            _ => StoredKind.PastFull,
        };
    }

    /// <summary>Past the master list, or a form the plugin's size doesn't give: the forms the census names records for.</summary>
    public static bool IsUnusual(StoredKind k) => k is >= StoredKind.PastFull;

    public static bool IsOwn(StoredKind k) => k is StoredKind.OwnFull or StoredKind.OwnMedium or StoredKind.OwnSmall or StoredKind.OwnMediumOtherSize or StoredKind.OwnSmallOtherSize;

    public static bool IsMaster(StoredKind k) => k is StoredKind.MasterFull or StoredKind.MasterMedium or StoredKind.MasterSmall;

    /// <summary>What the kind means, in words.</summary>
    public static string Text(StoredKind k) => k switch
    {
        StoredKind.Null => "null (00000000)",
        StoredKind.AllOnes => "all ones (FFFFFFFF, read as no record)",
        StoredKind.Unresolved => "at or past a master missing from Data (whose record can't be told)",
        StoredKind.MasterFull => "a master's record, by its full index",
        StoredKind.MasterMedium => "a Medium master's record (FD)",
        StoredKind.MasterSmall => "a Small master's record (FE)",
        StoredKind.OwnFull => "its own record, the full index",
        StoredKind.OwnMedium => "its own record, the Medium form (FD)",
        StoredKind.OwnSmall => "its own record, the Small (light) form (FE)",
        StoredKind.PastFull => "past the master list, a full index (read as its own record)",
        StoredKind.PastMedium => "past its Medium masters, FD (read as its own record)",
        StoredKind.PastSmall => "past its Small masters, FE (read as its own record)",
        StoredKind.OwnMediumOtherSize => "its own number in the Medium form (FD), in a plugin that isn't Medium (Mutagen reads it as past the master list: its own record)",
        StoredKind.OwnSmallOtherSize => "its own number in the Small form (FE), in a plugin that isn't Small (Mutagen reads it as past the master list: its own record)",
        StoredKind.PastMediumOtherSize => "past the Medium masters, FD, in a plugin that isn't Medium",
        _ => "past the Small masters, FE, in a plugin that isn't Small",
    };

    /// <summary>The part of a stored FormID that says its numbering and index: the top byte (full), FD and 2 hex
    /// digits, FE and 3.</summary>
    public static uint Prefix(uint raw) => (raw >> 24) switch { 0xFE => raw >> 12, 0xFD => raw >> 16, _ => raw >> 24 };

    /// <summary>A prefix as a pattern: 04xxxxxx, FD01xxxx, FE002xxx.</summary>
    public static string Pattern(uint prefix) => prefix >= 0xFE000 ? $"{prefix:X5}xxx" : prefix >= 0xFD00 ? $"{prefix:X4}xxxx" : $"{prefix:X2}xxxxxx";
}

/// <summary>Stored forms counted: of record headers and of links, by <see cref="StoredKind"/>.</summary>
public sealed class FormCounts
{
    public long Records;
    public readonly long[] Headers = new long[StoredKinds.Count], Links = new long[StoredKinds.Count];

    public void Add(FormCounts o)
    {
        Records += o.Records;
        for (int k = 0; k < StoredKinds.Count; k++) { Headers[k] += o.Headers[k]; Links[k] += o.Links[k]; }
    }

    public long Unusual => Enumerable.Range(0, StoredKinds.Count).Where(k => StoredKinds.IsUnusual((StoredKind)k)).Sum(k => Headers[k] + Links[k]);
    public long LinksNotNull => Links.Sum() - Links[(int)StoredKind.Null];
}

/// <summary>One plugin of the census.</summary>
public sealed class PluginForms
{
    public required IndexedPlugin Plugin { get; init; }
    public required MasterStyle Size { get; init; }
    public int Full, Medium, Small;
    public readonly FormCounts Counts = new();
    public readonly Dictionary<string, FormCounts> ByType = new(StringComparer.Ordinal);
    /// <summary>The prefixes met of each kind (the own and unusual kinds: which index the plugin uses).</summary>
    public readonly SortedSet<uint>[] Prefixes = Enumerable.Range(0, StoredKinds.Count).Select(_ => new SortedSet<uint>()).ToArray();

    /// <summary>The own forms the plugin uses (headers and links together).</summary>
    public int OwnForms => Enumerable.Range(0, StoredKinds.Count).Count(k => StoredKinds.IsOwn((StoredKind)k) && Counts.Headers[k] + Counts.Links[k] > 0);
}

/// <summary>A place of one plugin and record type where FormIDs of an unusual kind are stored.</summary>
public sealed class UnusualPlace
{
    public required string Plugin { get; init; }
    public required string RecordSig { get; init; }
    /// <summary>"header", or the subrecord and field.</summary>
    public required string Subrecord { get; init; }
    public string? Field { get; init; }
    public required StoredKind Kind { get; init; }
    public long FormIds, Records;
    public int MinOffset = int.MaxValue, MaxOffset = int.MinValue;
    public readonly SortedSet<uint> Prefixes = new();
    public readonly List<(RecordRef Ref, uint Raw)> Samples = new();
    public readonly List<string> SampleText = new();
    internal RecordRef Last = new(-1, -1);

    public string Where => Subrecord == "header" ? "the record header" : $"{Subrecord}{(MinOffset < 0 ? "" : MinOffset == MaxOffset ? $" @{MinOffset}" : $" @{MinOffset}-{MaxOffset}")} ({Field})";
}

public sealed class FormIdsReport(ReportSession session)
{
    /// <summary>What reading the records costs: as profile --subrecords "*" (the same stepping and link walk, on all
    /// cores): 0.003 s a MB as stored and 1 µs a record.</summary>
    public (double Seconds, long MemoryMB, string What) Estimate(SubrecordsOptions o)
    {
        var (set, bytes, records) = SubrecordsReport.Size(session, o);
        return (0.5 + bytes / 1048576.0 * 0.003 + records * 1e-6, 1500, $"How FormIDs are stored steps through {ReportAnswer.N(records)} records ({Sizes.Text(bytes)}) of {set.Shown}");
    }

    /// <summary>Samples kept per place (the text shows 3).</summary>
    public const int Samples = 12;

    sealed class Acc
    {
        public readonly Dictionary<(int Set, string Sig), FormCounts> Counts = new();
        public readonly Dictionary<(int Set, int Kind), HashSet<uint>> Prefixes = new();
    }

    /// <summary>An unusual FormID, a record that couldn't be read, or a record the walk couldn't follow.</summary>
    readonly record struct Item(int Set, int Entry, string RecordSig, StoredKind Kind, string Subrecord, string? Field, int Offset, uint Raw, string? Problem = null, string[]? Lost = null, bool Unreadable = false);

    public FormIdsAnswer Census(SubrecordsOptions o) => session.Run(scope =>
    {
        session.Index.Refresh();
        var set = PluginSet.Parse(session.Index, o.In, "@official");
        string? onlyType = SubrecordsReport.TypeSig(session, o.Type);
        var model = session.Schema.Model;
        var names = model.RecordTypes.GroupBy(r => r.Sig!).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        var answer = new FormIdsAnswer { Set = set, Type = onlyType };
        var index = session.Index;
        var sets = new List<ReadSet>();
        var plugins = new List<(PluginForms Forms, FormIdResolver Ids, bool Localized, Func<uint, string?> SigOf)>();
        foreach (var p in set.Plugins)
        {
            if (p.Segment is not { } seg) { answer.Notes.Add($"{p.Name} can't be read ({p.Problems.ReadError ?? "it has no header index"}): left out."); continue; }
            ReportSession.AddSource(answer, p);
            var ids = p.FormIds;
            var forms = new PluginForms { Plugin = p, Size = p.Header?.Style ?? MasterStyle.Full, Full = ids.FullMasters, Medium = ids.MediumMasters, Small = ids.SmallMasters };
            answer.Plugins.Add(forms);
            if (p.Problems.MissingMasters.Count > 0) answer.Notes.Add($"{p.Name}: a master is missing ({string.Join(", ", p.Problems.MissingMasters)}), so a FormID at or past its index can't be told: counted as such.");
            var types = seg.Signatures.Select(s => onlyType == null || s.Signature == onlyType).ToArray();
            if (!types.Any(x => x)) continue;
            List<int>? entries = null;
            if (set.WinnersOnly)
            {
                entries = new List<int>();
                for (int e = 0; e < seg.Count; e++)
                    if (types[seg[e].Signature] && index.IsWinner(new RecordRef(p.Id, e))) entries.Add(e);
            }
            sets.Add(new ReadSet { Plugin = p, Signatures = types, Entries = entries });
            plugins.Add((forms, ids, p.Header is { } h && (h.Flags & 0x80) != 0, TypeStatsReport.SigOf(index, p)));
        }
        answer.PluginsRead = answer.Plugins.Count;
        var plans = new ConcurrentDictionary<string, LinkPlan?>(StringComparer.Ordinal);
        LinkPlan? PlanOf(string sig) => plans.GetOrAdd(sig, s => names.TryGetValue(s, out var n) ? LinkPlan.Of(session.Schema, n) : null);
        using var walkers = new ThreadLocal<LinkByteWalker>(() => new LinkByteWalker(session.Schema));
        using var accs = new ThreadLocal<Acc>(() => new Acc(), trackAllValues: true);
        var places = new Dictionary<(int Set, string Sig, string Sub, string? Field, StoredKind Kind), UnusualPlace>();
        var problems = new Dictionary<string, (long Records, List<(RecordRef, string)> Samples)>(StringComparer.Ordinal);
        var lost = new Dictionary<(string Sig, string Path), long>();
        foreach (var it in RecordPipeline.Read<Item>(sets, (in PipelineRecord rec, List<Item> output) =>
        {
            var (forms, ids, localized, sigOf) = plugins[rec.Set];
            string rsig = rec.Plugin.Segment!.SignatureOf(rec.Header);
            var acc = accs.Value!;
            if (!acc.Counts.TryGetValue((rec.Set, rsig), out var c)) acc.Counts[(rec.Set, rsig)] = c = new FormCounts();
            c.Records++;
            if (rec.Head.Length >= 16)
            {
                uint head = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(rec.Head[12..]);
                var k = StoredKinds.Kind(head, ids, forms.Size);
                c.Headers[(int)k]++;
                Prefix(acc, rec.Set, k, head);
                if (StoredKinds.IsUnusual(k)) output.Add(new Item(rec.Set, rec.EntryIndex, rsig, k, "header", null, -1, head));
            }
            if (rec.Problem != PipelineProblem.None) { output.Add(new Item(rec.Set, rec.EntryIndex, rsig, StoredKind.Null, "", null, 0, 0, Unreadable: true)); return; }
            if (PlanOf(rsig) is not { } plan) return;
            var set0 = rec.Set; int entry = rec.EntryIndex;
            var (problem, lostPaths) = plan.Links(rec.Head, rec.Data, localized, sigOf, walkers.Value!, (place, raw, sub, offset) =>
            {
                var k = StoredKinds.Kind(raw, ids, forms.Size);
                c.Links[(int)k]++;
                Prefix(acc, set0, k, raw);
                if (StoredKinds.IsUnusual(k)) output.Add(new Item(set0, entry, rsig, k, sub, plan.Places[place].Path, offset, raw));
            });
            if (problem != null) output.Add(new Item(rec.Set, rec.EntryIndex, rsig, StoredKind.Null, "", null, 0, 0, problem, lostPaths));
        }))
        {
            var pf = plugins[it.Set].Forms;
            var rr = new RecordRef(pf.Plugin.Id, it.Entry);
            if (it.Unreadable) { answer.Unreadable++; continue; }
            if (it.Problem != null)
            {
                if (!problems.TryGetValue(it.RecordSig, out var pr)) problems[it.RecordSig] = pr = (0, new());
                problems[it.RecordSig] = (pr.Records + 1, pr.Samples);
                if (pr.Samples.Count < 3 && !pr.Samples.Any(x => x.Item2 == it.Problem)) pr.Samples.Add((rr, it.Problem));
                foreach (var l in it.Lost ?? Array.Empty<string>()) lost[(it.RecordSig, l)] = lost.GetValueOrDefault((it.RecordSig, l)) + 1;
                continue;
            }
            var key = (it.Set, it.RecordSig, it.Subrecord, it.Field, it.Kind);
            if (!places.TryGetValue(key, out var u))
                places[key] = u = new UnusualPlace { Plugin = pf.Plugin.Name, RecordSig = it.RecordSig, Subrecord = it.Subrecord, Field = it.Field, Kind = it.Kind };
            u.FormIds++;
            if (u.Last != rr) { u.Records++; u.Last = rr; if (u.Samples.Count < Samples) u.Samples.Add((rr, it.Raw)); }
            if (it.Offset >= 0) { u.MinOffset = Math.Min(u.MinOffset, it.Offset); u.MaxOffset = Math.Max(u.MaxOffset, it.Offset); }
            else if (it.Subrecord != "header") u.MinOffset = u.MaxOffset = -1;
            u.Prefixes.Add(StoredKinds.Prefix(it.Raw));
        }
        // The threads' counts, added up per plugin and record type
        foreach (var acc in accs.Values)
        {
            foreach (var ((s, sig), c) in acc.Counts)
            {
                var pf = plugins[s].Forms;
                if (!pf.ByType.TryGetValue(sig, out var t)) pf.ByType[sig] = t = new FormCounts();
                t.Add(c);
                pf.Counts.Add(c);
            }
            foreach (var ((s, k), ps) in acc.Prefixes) plugins[s].Forms.Prefixes[k].UnionWith(ps);
        }
        foreach (var pf in answer.Plugins)
        {
            answer.Totals.Add(pf.Counts);
            foreach (var (sig, c) in pf.ByType)
            {
                if (!answer.ByType.TryGetValue(sig, out var t)) answer.ByType[sig] = t = new FormCounts();
                t.Add(c);
            }
        }
        foreach (var u in places.Values)
        {
            if (u.MinOffset == int.MaxValue) u.MinOffset = u.MaxOffset = -1;
            foreach (var (r, raw) in u.Samples) u.SampleText.Add($"{index.Describe(r).Label} {raw:X8}");
            answer.Unusual.Add(u);
        }
        answer.Unusual.Sort((a, b) => answer.PluginPlace(a.Plugin).CompareTo(answer.PluginPlace(b.Plugin)) is var c0 && c0 != 0 ? c0 : b.FormIds.CompareTo(a.FormIds) is var c1 && c1 != 0 ? c1 : string.CompareOrdinal(a.RecordSig + a.Where, b.RecordSig + b.Where));
        answer.Read.Add($"the header FormID and the links of {ReportAnswer.N(answer.Totals.Records)} records of {ReportAnswer.Plural(answer.PluginsRead, "plugin")}, from the files' bytes on {PipelineOptions.DefaultThreads} threads (no record read through Mutagen)");
        var unknown = answer.ByType.Keys.Where(s => !names.ContainsKey(s)).OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0) answer.Notes.Add($"Record types the schema doesn't know ({string.Join(", ", unknown)}): only their headers are counted.");
        foreach (var (sig, (n, samples)) in problems.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var paths = lost.Where(x => x.Key.Sig == sig).Select(x => x.Key.Path).OrderBy(x => x, StringComparer.Ordinal).ToList();
            answer.Notes.Add($"{sig}: in {ReportAnswer.Plural(n, "record")} the byte walk couldn't follow every link field (the links found before that are counted{(paths.Count > 0 ? $"; links below {string.Join(", ", paths)} may be missing" : "")}): {string.Join("; ", samples.Select(x => $"{x.Item2} ({index.Describe(x.Item1).Label})"))}.");
        }
        if (answer.Unreadable > 0) answer.Notes.Add($"{ReportAnswer.Plural(answer.Unreadable, "record")} couldn't be read from the file (cut short, or compressed data that doesn't inflate): their headers are counted, their links aren't.");
        if (set.WinnersOnly) answer.Notes.Add("@winners: each record counts once, in the version the game uses.");
        return answer;

        static void Prefix(Acc acc, int set, StoredKind k, uint raw)
        {
            if (!StoredKinds.IsOwn(k) && !StoredKinds.IsUnusual(k)) return;
            if (!acc.Prefixes.TryGetValue((set, (int)k), out var ps)) acc.Prefixes[(set, (int)k)] = ps = new HashSet<uint>();
            ps.Add(StoredKinds.Prefix(raw));
        }
    });
}

public sealed class FormIdsAnswer : ReportAnswer
{
    public override string Kind => "profile";
    public required PluginSet Set { get; init; }
    public string? Type { get; init; }
    public int PluginsRead;
    public long Unreadable;
    public List<PluginForms> Plugins { get; } = new();
    public FormCounts Totals { get; } = new();
    public Dictionary<string, FormCounts> ByType { get; } = new(StringComparer.Ordinal);
    /// <summary>Where unusual forms are stored: by plugin (in the set's order), most FormIDs first.</summary>
    public List<UnusualPlace> Unusual { get; } = new();

    /// <summary>Rows shown without --detail: plugins, places, record types. The JSON has every one.</summary>
    public const int ShownPlugins = 40, ShownPlaces = 25, ShownTypes = 25;

    internal int PluginPlace(string name) => Plugins.FindIndex(p => p.Plugin.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A plugin the by-plugin table shows without --detail: one with an unusual form, or with two own forms.</summary>
    public static bool Notable(PluginForms p) => p.Counts.Unusual > 0 || p.OwnForms > 1;

    static string SizeText(PluginForms p) => $"{p.Size}, {Plural(p.Full + p.Medium + p.Small, "master")}{(p.Medium + p.Small > 0 ? $" ({p.Full} Full, {p.Medium} Medium, {p.Small} Small)" : "")}";

    /// <summary>Counts of the kinds chosen, each with the patterns the plugin used: "116 03xxxxxx, 102 FE001xxx".</summary>
    static string Kinds(PluginForms p, long[] counts, Func<StoredKind, bool> which, bool patterns = true) => string.Join(", ",
        Enumerable.Range(0, StoredKinds.Count).Where(k => which((StoredKind)k) && counts[k] > 0)
            .Select(k => patterns && p.Prefixes[k].Count > 0 ? $"{N(counts[k])} {string.Join("/", p.Prefixes[k].Take(4).Select(StoredKinds.Pattern))}{(p.Prefixes[k].Count > 4 ? "/…" : "")}" : $"{N(counts[k])} {Short((StoredKind)k)}"));

    /// <summary>A kind in a few words, for the record-type table.</summary>
    static string Short(StoredKind k) => k switch
    {
        StoredKind.OwnFull or StoredKind.PastFull or StoredKind.MasterFull => "full index",
        StoredKind.OwnMedium or StoredKind.PastMedium or StoredKind.MasterMedium => "FD",
        StoredKind.OwnSmall or StoredKind.PastSmall or StoredKind.MasterSmall => "FE",
        StoredKind.OwnMediumOtherSize or StoredKind.PastMediumOtherSize => "FD in another size",
        StoredKind.OwnSmallOtherSize or StoredKind.PastSmallOtherSize => "FE in another size",
        _ => k.ToString(),
    };

    static long Sum(long[] c, Func<StoredKind, bool> which) => Enumerable.Range(0, StoredKinds.Count).Where(k => which((StoredKind)k)).Sum(k => c[k]);

    protected override void Render(ReportBody body, ReportTextOptions options)
    {
        var t = Totals;
        body.Head.Add($"How the FormIDs in {(Type != null ? $"the {Type} records" : "the records")} of {Set.Shown} are stored, from the files' bytes: {N(t.Records)} records of {Plural(PluginsRead, "plugin")}; {N(t.Headers.Sum())} record headers and {N(t.LinksNotNull)} links that aren't null ({N(t.Links[(int)StoredKind.Null])} null).");
        body.Head.Add("A FormID names a master's record by its index in one of three numberings (the full index: its top byte; Medium: FD and 2 hex digits; Small: FE and 3), or the plugin's own record by the index right after its masters of that numbering. An index further on is past the master list: the game and Mutagen read it as the plugin's own record. A Small plugin's own form is FE, a Medium one's FD; the full index reads the same in any plugin (the Creation Kit writes it). check2 <plugin> names each record of a plugin that mixes its own forms.");
        body.RowNoun = "rows";
        body.Head.Add("");
        body.Head.Add("| Stored as | Record headers | Links | Plugins | Record types (most first) |");
        body.Head.Add("|---|---|---|---|---|");
        for (int k = 0; k < StoredKinds.Count; k++)
        {
            long h = t.Headers[k], l = t.Links[k];
            if (h + l == 0) continue;
            var types = ByType.Where(x => x.Value.Headers[k] + x.Value.Links[k] > 0).OrderByDescending(x => x.Value.Headers[k] + x.Value.Links[k]).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
            int plugins = Plugins.Count(p => p.Counts.Headers[k] + p.Counts.Links[k] > 0);
            body.Head.Add($"| {StoredKinds.Text((StoredKind)k)} | {N(h)} | {N(l)} | {N(plugins)} | {Cell($"{types.Count}: {string.Join(", ", types.Take(4).Select(x => $"{x.Key} {N(x.Value.Headers[k] + x.Value.Links[k])}"))}{(types.Count > 4 ? ", …" : "")}")} |");
        }
        // The plugins
        var shown = options.Detail ? Plugins : Plugins.Where(Notable).ToList();
        body.Rows.Add("");
        body.Rows.Add(options.Detail ? "Every plugin read:" : $"Plugins that store a FormID past the master list or in another size's form, or name their own records in two forms ({N(shown.Count)} of {N(Plugins.Count)}):");
        if (shown.Count > 0)
        {
            body.Rows.Add("| Plugin | Size, masters | Records | Own record headers | Links to masters | Links to own records | Past the master list (headers, links) | Another size's form (headers, links) |");
            body.Rows.Add("|---|---|---|---|---|---|---|---|");
            foreach (var p in shown.Take(options.Detail ? int.MaxValue : ShownPlugins))
            {
                var c = p.Counts;
                static bool Past(StoredKind k) => k is StoredKind.PastFull or StoredKind.PastMedium or StoredKind.PastSmall or StoredKind.PastMediumOtherSize or StoredKind.PastSmallOtherSize;
                static bool Other(StoredKind k) => k >= StoredKind.OwnMediumOtherSize;
                string Pair(Func<StoredKind, bool> w) => Sum(c.Headers, w) + Sum(c.Links, w) == 0 ? "" : $"{N(Sum(c.Headers, w))}, {N(Sum(c.Links, w))}: {string.Join("/", Enumerable.Range(0, StoredKinds.Count).Where(k => w((StoredKind)k)).SelectMany(k => p.Prefixes[k]).Distinct().Take(4).Select(StoredKinds.Pattern))}";
                body.Rows.Add($"| {Cell(p.Plugin.Name)} | {SizeText(p)} | {N(c.Records)} | {Cell(Kinds(p, c.Headers, k => StoredKinds.IsOwn(k) && k < StoredKind.OwnMediumOtherSize))} | {N(Sum(c.Links, StoredKinds.IsMaster))} | {Cell(Kinds(p, c.Links, k => StoredKinds.IsOwn(k) && k < StoredKind.OwnMediumOtherSize))} | {Cell(Pair(Past))} | {Cell(Pair(Other))} |");
            }
            if (!options.Detail && shown.Count > ShownPlugins) body.Rows.Add($"{Plural(shown.Count - ShownPlugins, "more plugin")} (--detail shows them).");
        }
        if (!options.Detail)
            body.Rows.Add((Plugins.Count - shown.Count) switch
            {
                0 => "",
                1 => "The other plugin stores no FormID past the master list or in another size's form, and names its own records in one form (or has none); --detail lists every plugin.",
                var n => $"The other {N(n)} plugins store no FormID past the master list or in another size's form, and name their own records in one form (or have none); --detail lists every plugin.",
            });
        // Where the unusual ones are
        body.Rows.Add("");
        if (Unusual.Count == 0) body.Rows.Add("No FormID is stored past the master list or in another size's form.");
        else
        {
            body.Rows.Add($"Where FormIDs are stored past the master list or in another size's form ({Plural(Unusual.Sum(u => u.FormIds), "FormID")} in {Plural(Unusual.Count, "place")}; a place is a plugin, record type and field):");
            body.Rows.Add("| Plugin | Record type | Where | Stored as | FormIDs | Records | Samples (EditorID (FormKey) and the FormID as stored) |");
            body.Rows.Add("|---|---|---|---|---|---|---|");
            int show = options.Detail ? 200 : ShownPlaces;
            foreach (var u in Unusual.Take(show))
                body.Rows.Add($"| {Cell(u.Plugin)} | {u.RecordSig} | {Cell(u.Where)} | {Cell($"{string.Join("/", u.Prefixes.Take(4).Select(StoredKinds.Pattern))}: {StoredKinds.Text(u.Kind)}")} | {N(u.FormIds)} | {N(u.Records)} | {Cell(string.Join("; ", u.SampleText.Take(3)))} |");
            if (Unusual.Count > show) body.Rows.Add($"{Plural(Unusual.Count - show, "more place")} with {Plural(Unusual.Skip(show).Sum(u => u.FormIds), "FormID")}{(options.Detail ? " (the JSON has every one)" : " (--detail shows up to 200; the JSON has every one)")}.");
        }
        // By record type
        body.Rows.Add("");
        body.Rows.Add("By record type (every plugin read together):");
        body.Rows.Add("| Record type | Records | Overrides | Own record headers | Links to masters | Links to own records | Past the master list or another size's form | Null links |");
        body.Rows.Add("|---|---|---|---|---|---|---|---|");
        var typeRows = ByType.OrderByDescending(x => x.Value.Records).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
        foreach (var (sig, c) in typeRows.Take(options.Detail ? int.MaxValue : ShownTypes))
        {
            string Own(long[] a) => string.Join(", ", Enumerable.Range(0, StoredKinds.Count).Where(k => (StoredKind)k is StoredKind.OwnFull or StoredKind.OwnMedium or StoredKind.OwnSmall && a[k] > 0).Select(k => $"{N(a[k])} {Short((StoredKind)k)}"));
            body.Rows.Add($"| {sig} | {N(c.Records)} | {N(Sum(c.Headers, StoredKinds.IsMaster))} | {Own(c.Headers)} | {N(Sum(c.Links, StoredKinds.IsMaster))} | {Own(c.Links)} | {(c.Unusual > 0 ? N(c.Unusual) : "")} | {N(c.Links[(int)StoredKind.Null])} |");
        }
        if (!options.Detail && typeRows.Count > ShownTypes) body.Rows.Add($"{Plural(typeRows.Count - ShownTypes, "more record type")} (--detail shows them).");
        if (Plugins.Count == 0) body.Rows.Add($"No plugin in {Set.Shown} could be read.");
    }

    static JsonObject KindsJson(long[] c) => new(Enumerable.Range(0, StoredKinds.Count).Where(k => c[k] > 0).Select(k => KeyValuePair.Create(((StoredKind)k).ToString(), (JsonNode?)c[k])));

    protected override void Fill(JsonObject json)
    {
        json["plugins"] = Set.Shown;
        json["type"] = Type;
        json["records"] = Totals.Records;
        json["pluginsRead"] = PluginsRead;
        json["unreadable"] = Unreadable;
        json["kinds"] = new JsonArray(Enumerable.Range(0, StoredKinds.Count).Where(k => Totals.Headers[k] + Totals.Links[k] > 0).Select(k => (JsonNode)new JsonObject
        {
            ["kind"] = ((StoredKind)k).ToString(), ["means"] = StoredKinds.Text((StoredKind)k), ["unusual"] = StoredKinds.IsUnusual((StoredKind)k),
            ["headers"] = Totals.Headers[k], ["links"] = Totals.Links[k], ["plugins"] = Plugins.Count(p => p.Counts.Headers[k] + p.Counts.Links[k] > 0),
        }).ToArray());
        json["byPlugin"] = new JsonArray(Plugins.Select(p => (JsonNode)new JsonObject
        {
            ["plugin"] = p.Plugin.Name, ["size"] = p.Size.ToString(), ["fullMasters"] = p.Full, ["mediumMasters"] = p.Medium, ["smallMasters"] = p.Small,
            ["records"] = p.Counts.Records, ["unusual"] = p.Counts.Unusual, ["ownForms"] = p.OwnForms,
            ["headers"] = KindsJson(p.Counts.Headers), ["links"] = KindsJson(p.Counts.Links),
            ["patterns"] = new JsonObject(Enumerable.Range(0, StoredKinds.Count).Where(k => p.Prefixes[k].Count > 0).Select(k => KeyValuePair.Create(((StoredKind)k).ToString(), (JsonNode?)new JsonArray(p.Prefixes[k].Select(x => (JsonNode)StoredKinds.Pattern(x)).ToArray())))),
            // By record type for the plugins the text names (an unusual form, or two own forms); the totals by type are below
            ["byRecordType"] = Notable(p) ? new JsonObject(p.ByType.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => KeyValuePair.Create(x.Key, (JsonNode?)new JsonObject
            {
                ["records"] = x.Value.Records, ["headers"] = KindsJson(x.Value.Headers), ["links"] = KindsJson(x.Value.Links),
            }))) : null,
        }).ToArray());
        json["unusual"] = new JsonArray(Unusual.Select(u => (JsonNode)new JsonObject
        {
            ["plugin"] = u.Plugin, ["recordType"] = u.RecordSig, ["subrecord"] = u.Subrecord, ["field"] = u.Field,
            ["offsetFrom"] = u.MinOffset, ["offsetTo"] = u.MaxOffset, ["kind"] = u.Kind.ToString(), ["means"] = StoredKinds.Text(u.Kind),
            ["stored"] = new JsonArray(u.Prefixes.Select(x => (JsonNode)StoredKinds.Pattern(x)).ToArray()),
            ["formIds"] = u.FormIds, ["records"] = u.Records,
            ["samples"] = new JsonArray(u.SampleText.Select(s => (JsonNode)s).ToArray()),
        }).ToArray());
        json["byRecordType"] = new JsonObject(ByType.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => KeyValuePair.Create(x.Key, (JsonNode?)new JsonObject
        {
            ["records"] = x.Value.Records, ["headers"] = KindsJson(x.Value.Headers), ["links"] = KindsJson(x.Value.Links),
        })));
    }
}
