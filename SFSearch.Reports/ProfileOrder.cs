// profile --subrecords <signatures> --order (GAPS S87): the orders a record type's subrecords come in, across plugins,
// read from the files' bytes. C38 asked, for every package (PACK), where UNAM, BNAM and PNAM stand after XNAM and after
// the procedure tree, with a Python walk of 5,499 records; a patcher's author asks the same of any type ("in what order
// does the game write these", "is BNAM always right after UNAM", "what comes after XNAM"). Every record of the set is
// stepped through on all cores (RecordPipeline: found through the header index, inflated when compressed); its
// subrecords become a sequence: the ones asked for by signature, a run of any others as one "…" (with *, every
// subrecord). A unit repeated in a row becomes "(unit)+", innermost first; a unit that repeats in some record of the type
// is written so wherever it stands alone too, so 1 and 114 entries are one order. Records are grouped by their order,
// per record type: records, plugins, a sample, and for each repeat how many times in a row (least, most) and in all.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Reports;

/// <summary>One step of an order: a subrecord, a run of other subrecords ("…"), or a unit repeated one or more times in
/// a row. A repeat counts, over every place it stands for, how many times in a row (least, most) and in all.</summary>
public sealed class OrderStep
{
    public const string Gap = "…";
    /// <summary>The subrecord's signature; "…" for a run of others; null for a repeat.</summary>
    public string? Signature { get; }
    /// <summary>What repeats (a repeat only).</summary>
    public IReadOnlyList<OrderStep>? Unit => _unit;
    readonly List<OrderStep>? _unit;
    /// <summary>The step as text, its counts left out: what orders are grouped by.</summary>
    public string Key { get; }
    /// <summary>A repeat: the units in all, the least and most in a row, and the places it stands for (records, or the
    /// times the repeat around it came).</summary>
    public long Total, Min, Max, Places;

    OrderStep(string signature) { Signature = signature; Key = signature; }
    OrderStep(List<OrderStep> unit) { _unit = unit; Key = "(" + Text(unit) + ")+"; }

    public bool IsRepeat => _unit != null;
    public static readonly OrderStep Others = new(Gap);
    public static OrderStep Of(string signature) => new(signature);

    public static string Text(IEnumerable<OrderStep> steps) => string.Join(" ", steps.Select(s => s.Key));

    /// <summary>A unit that stands <paramref name="times"/> times in a row at each of <paramref name="places"/> places.</summary>
    internal static OrderStep Repeat(List<OrderStep> unit, long times, long places) => new(unit) { Total = times * places, Min = times, Max = times, Places = places };

    /// <summary>A repeat like this one with another unit (the same counts).</summary>
    internal OrderStep With(List<OrderStep> unit) => new(unit) { Total = Total, Min = Min, Max = Max, Places = Places };

    /// <summary>Adds the counts of a step with the same key into this one (and into its unit's repeats).</summary>
    internal void Add(OrderStep other)
    {
        if (_unit == null) return;
        Total += other.Total; Places += other.Places;
        Min = Math.Min(Min, other.Min); Max = Math.Max(Max, other.Max);
        for (int j = 0; j < _unit.Count; j++) _unit[j].Add(other._unit![j]);
    }

    /// <summary>The units that repeat in a sequence, by where they stand: "" at the top, else the key of the repeat
    /// they are inside of.</summary>
    internal static void UnitsByPlace(IEnumerable<OrderStep> steps, string within, Dictionary<string, Dictionary<string, string[]>> into)
    {
        foreach (var s in steps)
            if (s._unit != null)
            {
                if (!into.TryGetValue(within, out var d)) into[within] = d = new Dictionary<string, string[]>(StringComparer.Ordinal);
                d.TryAdd(s.Key, s._unit.Select(u => u.Key).ToArray());
                UnitsByPlace(s._unit, s.Key, into);
            }
    }

    /// <summary>The repeats of a sequence, outermost first, depth by depth.</summary>
    public static IEnumerable<OrderStep> Repeats(IEnumerable<OrderStep> steps)
    {
        foreach (var s in steps)
            if (s._unit != null)
            {
                yield return s;
                foreach (var inner in Repeats(s._unit)) yield return inner;
            }
    }

    /// <summary>The longest unit looked for, in steps (a repeat inside it is one step).</summary>
    public const int MaxUnit = 16;

    /// <summary>
    /// Writes each unit repeated in a row as one repeat, innermost first: runs of one step, then of two, and so on, back to
    /// one step after each change. <paramref name="places"/> is how many places the sequence stands for (1 for a
    /// record's own; the records of a group; a repeat's units). Two repeats of the same unit side by side become one.
    /// </summary>
    internal static List<OrderStep> Collapse(List<OrderStep> t, long places)
    {
        for (int len = 1; len <= MaxUnit && 2 * len <= t.Count; len++)
        {
            bool changed = false;
            var o = new List<OrderStep>(t.Count);
            int i = 0;
            while (i < t.Count)
            {
                int k = 1;
                while (i + (k + 1) * len <= t.Count && Same(t, i, i + k * len, len)) k++;
                if (k < 2) { o.Add(t[i]); i++; continue; }
                if (len == 1 && t[i].IsRepeat)
                {
                    // (A)+ (A)+: one repeat, its runs added
                    var first = t[i];
                    var unit = new List<OrderStep>(first._unit!);
                    var r = first.With(unit);
                    for (int c = 1; c < k; c++)
                    {
                        var next = t[i + c];
                        r.Total += next.Total; r.Min += next.Min; r.Max += next.Max;
                        for (int j = 0; j < unit.Count; j++) unit[j].Add(next._unit![j]);
                    }
                    o.Add(r);
                }
                else
                {
                    var unit = t.GetRange(i, len);
                    for (int c = 1; c < k; c++)
                        for (int j = 0; j < len; j++) unit[j].Add(t[i + c * len + j]);
                    o.Add(Repeat(unit, k, places));
                }
                i += k * len;
                changed = true;
            }
            t = o;
            if (changed) len = 0;
        }
        return t;
    }

    static bool Same(List<OrderStep> t, int a, int b, int len)
    {
        for (int j = 0; j < len; j++)
            if (!ReferenceEquals(t[a + j], t[b + j]) && !string.Equals(t[a + j].Key, t[b + j].Key, StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>
    /// A unit that repeats somewhere in the same place (<paramref name="units"/>: at the top of a record, or inside a
    /// repeat of the same unit; longest first) is written as a repeat where it stands alone too (once in a row), unless a
    /// repeat of it stands right beside, or it is the whole of a repeat's unit ((A)+ stays (A)+); then the sequence is
    /// collapsed again. Inner units first. The counts stay exact: a unit written so stands once at each of the
    /// sequence's places. <paramref name="within"/>: the key of the repeat this is the unit of ("" at the top).
    /// </summary>
    internal static List<OrderStep> Normalize(List<OrderStep> t, long places, IReadOnlyDictionary<string, List<(string Key, string[] Steps)>> units, string within = "")
    {
        var inner = new List<OrderStep>(t.Count);
        foreach (var s in t) inner.Add(s._unit == null ? s : s.With(Normalize(new List<OrderStep>(s._unit), s.Total, units, s.Key)));
        if (!units.TryGetValue(within, out var here)) return Collapse(inner, places);
        var o = new List<OrderStep>(inner.Count);
        int i = 0;
        while (i < inner.Count)
        {
            bool wrapped = false;
            foreach (var (key, steps) in here)
            {
                int len = steps.Length;
                if (i + len > inner.Count) continue;
                bool match = true;
                for (int j = 0; j < len && match; j++) match = string.Equals(inner[i + j].Key, steps[j], StringComparison.Ordinal);
                if (!match || within.Length > 0 && len == inner.Count) continue;
                if (o.Count > 0 && o[^1].Key == key || i + len < inner.Count && inner[i + len].Key == key) continue;
                o.Add(Repeat(inner.GetRange(i, len), 1, places));
                i += len; wrapped = true;
                break;
            }
            if (!wrapped) { o.Add(inner[i]); i++; }
        }
        return Collapse(o, places);
    }
}

/// <summary>Records of one record type whose subrecords come in one order.</summary>
public sealed class SubrecordOrder
{
    public required string RecordSig { get; init; }
    public required List<OrderStep> Steps { get; set; }
    public string Key => OrderStep.Text(Steps);
    public long Records;
    public readonly Dictionary<string, long> Plugins = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The first record in this order (in the plugins' order, then the file's).</summary>
    public RecordRef First;
    public string? Sample;

    internal void Add(SubrecordOrder other)
    {
        Records += other.Records;
        foreach (var (p, n) in other.Plugins) Plugins[p] = Plugins.GetValueOrDefault(p) + n;
        for (int j = 0; j < Steps.Count; j++) Steps[j].Add(other.Steps[j]);
    }
}

public sealed class SubrecordOrderReport(ReportSession session)
{
    /// <summary>What stepping through the records costs, from the header index: measured 4.4 s (two signatures) to
    /// 6.8 s (every subrecord) for the 2.26 GB of the 33 official plugins on 8 threads, at most 2.1 GB of working set
    /// with the mapped files' pages.</summary>
    public (double Seconds, long MemoryMB, string What) Estimate(SubrecordsOptions o)
    {
        var (set, bytes, records) = SubrecordsReport.Size(session, o);
        return (0.5 + bytes / 1048576.0 * SecondsPerMB, 1000, $"The order of {o.Signatures} steps through {ReportAnswer.N(records)} records ({Sizes.Text(bytes)}) of {set.Shown}");
    }

    /// <summary>Seconds a MB of records as stored takes (measured, see <see cref="Estimate"/>).</summary>
    const double SecondsPerMB = 0.003;

    public SubrecordOrderAnswer Orders(SubrecordsOptions o) => session.Run(scope =>
    {
        session.Index.Refresh();
        var asked = SubrecordsReport.Signatures(o.Signatures, all: true);
        bool all = asked.Count == 1 && asked[0] == "*";
        var set = PluginSet.Parse(session.Index, o.In, "@official");
        string? onlyType = SubrecordsReport.TypeSig(session, o.Type);
        var want = all ? null : asked.Select(s => BinaryPrimitives.ReadUInt32LittleEndian(Encoding.Latin1.GetBytes(s))).ToHashSet();
        var answer = new SubrecordOrderAnswer { Set = set, Asked = asked, All = all, Type = onlyType };
        var index = session.Index;
        var sets = new List<ReadSet>();
        foreach (var p in set.Plugins)
        {
            if (p.Segment is not { } seg) { answer.Notes.Add($"{p.Name} can't be read ({p.Problems.ReadError ?? "it has no header index"}): left out."); continue; }
            ReportSession.AddSource(answer, p);
            answer.PluginsRead++;
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
        }
        // The steps of a subrecord, one object for each signature (they are never changed)
        var sigSteps = new ConcurrentDictionary<uint, OrderStep>();
        var stats = new PipelineStats();
        var byType = new Dictionary<string, Dictionary<string, SubrecordOrder>>(StringComparer.Ordinal);
        foreach (var r in RecordPipeline.Read<Stepped>(sets, (in PipelineRecord rec, List<Stepped> output) =>
        {
            string rsig = rec.Plugin.Segment!.SignatureOf(rec.Header);
            if (rec.Problem != PipelineProblem.None) { output.Add(new Stepped(rec.Set, rec.EntryIndex, rsig, null)); return; }
            output.Add(new Stepped(rec.Set, rec.EntryIndex, rsig, OrderStep.Collapse(Steps(rec.Data, want, sigSteps), 1)));
        }, new PipelineOptions { Stats = stats }))
        {
            if (r.Steps == null) { answer.Unreadable++; continue; }
            var p = sets[r.Set].Plugin;
            if (!byType.TryGetValue(r.RecordSig, out var orders)) byType[r.RecordSig] = orders = new Dictionary<string, SubrecordOrder>(StringComparer.Ordinal);
            var one = new SubrecordOrder { RecordSig = r.RecordSig, Steps = r.Steps, Records = 1, First = new RecordRef(p.Id, r.Entry) };
            one.Plugins[p.Name] = 1;
            string key = one.Key;
            if (orders.TryGetValue(key, out var have)) have.Add(one);
            else orders[key] = one;
        }
        for (int s = 0; s < sets.Count; s++) { answer.Records += stats.Records[s]; answer.Bytes += stats.StoredBytes[s]; answer.Inflated += stats.Compressed[s]; }
        // Per record type: a unit that repeats in some record is written as a repeat where it stands alone too
        foreach (var (rsig, orders) in byType)
        {
            // Again with the units the last round made ((A)+ B, after B was written so), until nothing changes
            var groups = orders.Values.OrderBy(g => Place(g.First)).ToList();
            for (int round = 0; round < NormalizeRounds; round++)
            {
                var found = new Dictionary<string, Dictionary<string, string[]>>(StringComparer.Ordinal);
                foreach (var g in groups) OrderStep.UnitsByPlace(g.Steps, "", found);
                if (found.Count == 0) break;
                var units = found.ToDictionary(kv => kv.Key, kv => kv.Value.Select(u => (u.Key, Steps: u.Value))
                    .OrderByDescending(u => u.Steps.Length).ThenBy(u => u.Key, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
                var merged = new Dictionary<string, SubrecordOrder>(StringComparer.Ordinal);
                bool changed = false;
                foreach (var g in groups)
                {
                    string before = g.Key;
                    g.Steps = OrderStep.Normalize(g.Steps, g.Records, units);
                    changed |= g.Key != before;
                    if (merged.TryGetValue(g.Key, out var have)) have.Add(g);
                    else merged[g.Key] = g;
                }
                groups = merged.Values.ToList();
                if (!changed) break;
            }
            var list = groups.OrderByDescending(g => g.Records).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
            // A record holding none of the subrecords asked for, in a census of every type: its type is left out
            if (onlyType == null && !all && list.All(g => g.Key is "" or OrderStep.Gap)) continue;
            foreach (var g in list)
            {
                var info = index.Describe(g.First);
                g.Sample = $"{info.EditorId ?? "(no EditorID)"} ({info.FormKey})";
            }
            answer.Types.Add((rsig, list));
        }
        answer.Types.Sort((a, b) => b.Orders.Sum(g => g.Records).CompareTo(a.Orders.Sum(g => g.Records)) is var c && c != 0 ? c : string.CompareOrdinal(a.Sig, b.Sig));
        answer.Read.Add($"the subrecords of {ReportAnswer.N(answer.Records)} records ({Sizes.Text(answer.Bytes)} as stored, {ReportAnswer.N(answer.Inflated)} inflated) of {ReportAnswer.Plural(answer.PluginsRead, "plugin")}, from the files' bytes on {PipelineOptions.DefaultThreads} threads (no record read through Mutagen)");
        if (answer.Unreadable > 0) answer.Notes.Add($"{ReportAnswer.Plural(answer.Unreadable, "record")} couldn't be read from the file (cut short, or compressed data that doesn't inflate) and aren't counted.");
        if (set.WinnersOnly) answer.Notes.Add("@winners: each record counts once, in the version the game uses.");
        return answer;

        // The place of a record in the census: its plugin's place in the set, then its entry
        long Place(RecordRef r) => (long)set.Plugins.FindIndex(x => x.Id == r.Plugin) << 32 | (uint)r.Entry;
    });

    /// <summary>Rounds of writing lone units as repeats: one more for each unit that only the last round made.</summary>
    const int NormalizeRounds = 4;

    readonly record struct Stepped(int Set, int Entry, string RecordSig, List<OrderStep>? Steps);

    /// <summary>A record's subrecords as steps: those asked for (all with null), a run of others as one "…". An XXXX
    /// gives the size of the next subrecord and isn't a step.</summary>
    static List<OrderStep> Steps(ReadOnlySpan<byte> body, HashSet<uint>? want, ConcurrentDictionary<uint, OrderStep> sigSteps)
    {
        var t = new List<OrderStep>();
        int p = 0; uint? big = null;
        while (p + 6 <= body.Length)
        {
            int p0 = p;
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(body[p..]);
            int n = BinaryPrimitives.ReadUInt16LittleEndian(body[(p + 4)..]);
            p += 6;
            if (sig == 0x58585858 /* XXXX */ && n == 4 && p + 4 <= body.Length) { big = BinaryPrimitives.ReadUInt32LittleEndian(body[p..]); p += 4; continue; }
            long len = big ?? (uint)n; big = null;
            p = (int)Math.Min(body.Length, p + len);
            if (want == null || want.Contains(sig))
            {
                t.Add(sigSteps.TryGetValue(sig, out var step) ? step : sigSteps.GetOrAdd(sig, OrderStep.Of(Encoding.Latin1.GetString(body.Slice(p0, 4)))));
            }
            else if (t.Count == 0 || !ReferenceEquals(t[^1], OrderStep.Others)) t.Add(OrderStep.Others);
        }
        return t;
    }
}

public sealed class SubrecordOrderAnswer : ReportAnswer
{
    public override string Kind => "profile";
    public required PluginSet Set { get; init; }
    public required List<string> Asked { get; init; }
    /// <summary>Every subrecord is a step (--subrecords *).</summary>
    public bool All { get; init; }
    public string? Type { get; init; }
    public long Records, Bytes, Inflated, Unreadable;
    public int PluginsRead;
    /// <summary>Per record type (most records first): its orders, most records first.</summary>
    public List<(string Sig, List<SubrecordOrder> Orders)> Types { get; } = new();

    /// <summary>Orders shown per record type: in the text without --detail, and in the JSON.</summary>
    public const int Shown = 10, Kept = 200;

    string Asking => All ? "every subrecord" : string.Join(", ", Asked);

    public string OrderText(SubrecordOrder g) => g.Key switch
    {
        "" => "(no subrecords)",
        OrderStep.Gap => "(none of them)",
        var k => k,
    };

    public static string RepeatText(OrderStep r) =>
        $"{r.Key}: {(r.Min == r.Max ? N(r.Min) : $"{N(r.Min)}-{N(r.Max)}")} in a row, {N(r.Total)} in all";

    protected override void Render(ReportBody body, ReportTextOptions options)
    {
        long orders = Types.Sum(t => t.Orders.Count);
        body.Head.Add($"The order of {Asking} in {(Type != null ? $"the {Type} records" : "the records")} of {Set.Shown}: {N(Records)} records of {Plural(PluginsRead, "plugin")} stepped through, from the files' bytes; {Plural(orders, "order")}{(Type == null ? $" in {Plural(Types.Count, "record type")}" : "")}.");
        body.Head.Add(All ? "`(A B)+` is A B one or more times in a row; a unit that repeats in some record is written so where it stands once too. Repeats: how many times in a row (least-most) and in all."
                          : "`…` is one or more other subrecords; `(A B)+` is A B one or more times in a row; a unit that repeats in some record is written so where it stands once too. Repeats: how many times in a row (least-most) and in all.");
        body.RowNoun = "rows";
        body.Head.Add("");
        body.Head.Add("| Record type | Order | Records | Repeats | Plugins | Sample |");
        body.Head.Add("|---|---|---|---|---|---|");
        foreach (var (sig, list) in Types)
        {
            int show = options.Detail ? Kept : Shown;
            foreach (var g in list.Take(show))
            {
                var plugins = g.Plugins.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();
                int ps = options.Detail ? plugins.Count : 3;
                string pl = $"{plugins.Count}: {string.Join(", ", plugins.Take(ps).Select(x => $"{x.Key} {N(x.Value)}"))}{(plugins.Count > ps ? ", …" : "")}";
                string reps = string.Join("; ", OrderStep.Repeats(g.Steps).Select(RepeatText));
                body.Rows.Add($"| {sig} | {Cell(OrderText(g))} | {N(g.Records)} | {Cell(reps)} | {Cell(pl)} | {Cell(g.Sample)} |");
            }
            if (list.Count > show)
                body.Rows.Add($"{sig}: {Plural(list.Count - show, "more order")} of {Plural(list.Skip(show).Sum(g => g.Records), "record")}{(options.Detail ? "" : " (--detail shows up to " + Kept + ")")}.");
        }
        if (Types.Count == 0) body.Rows.Add($"No record in {Set.Shown}{(Type != null ? $" of type {Type}" : "")} holds {Asking}.");
    }

    protected override void Fill(JsonObject json)
    {
        json["plugins"] = Set.Shown;
        json["subrecords"] = new JsonArray(Asked.Select(a => (JsonNode)a).ToArray());
        json["type"] = Type;
        json["records"] = Records;
        json["pluginsRead"] = PluginsRead;
        json["inflated"] = Inflated;
        json["unreadable"] = Unreadable;
        json["types"] = new JsonArray(Types.Select(t => (JsonNode)new JsonObject
        {
            ["recordType"] = t.Sig,
            ["records"] = t.Orders.Sum(g => g.Records),
            ["orderCount"] = t.Orders.Count,
            ["orders"] = new JsonArray(t.Orders.Take(Kept).Select(g => (JsonNode)new JsonObject
            {
                ["order"] = OrderText(g),
                ["records"] = g.Records,
                ["sample"] = g.Sample,
                ["repeats"] = new JsonArray(OrderStep.Repeats(g.Steps).Select(r => (JsonNode)new JsonObject
                {
                    ["unit"] = r.Key, ["least"] = r.Min, ["most"] = r.Max, ["total"] = r.Total, ["places"] = r.Places,
                }).ToArray()),
                ["byPlugin"] = new JsonObject(g.Plugins.OrderBy(p => Set.Plugins.FindIndex(x => x.Name.Equals(p.Key, StringComparison.OrdinalIgnoreCase))).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
            }).ToArray()),
        }).ToArray());
    }
}
