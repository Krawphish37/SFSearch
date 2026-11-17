// profile --subrecords X|* --lengths (GAPS S128): how long a record type's subrecords are across plugins, read from the
// files' bytes on all cores. A modder or a Mutagen author asks it before telling "present but empty" from "absent": which
// subrecords are always empty (end markers such as BFCE, STOP, DSTF; marker fields), which have one fixed length, which
// vary, and above all which data subrecords are sometimes empty (a scene's FNAM, a location's LCUN, a world's WHGT), with
// records of each kind to look at. The Mutagen session's C65 had to count them with Python and a C# probe. Every record of
// the set goes through the record pipeline (found through the header index, inflated when compressed); its subrecords are
// stepped through (XXXX sizes the next one), those inside components (BFCB ... BFCE) too, and each one asked for counts by
// record type and signature: uses, records, shortest and longest, the empty ones (records, plugins, the record itself or
// the component they stand in) and the first records with an empty one and with data. Nothing goes through Mutagen.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Reports;

/// <summary>How a kind's lengths come: every one empty, one length, several (none empty), or empty and not.</summary>
public enum LengthClass { AlwaysEmpty, Fixed, Varying, SometimesEmpty }

/// <summary>One subrecord signature in one record type: its lengths, and its empty ones.</summary>
public sealed class SubrecordLengths
{
    public required string RecordSig { get; init; }
    public required string Subrecord { get; init; }
    /// <summary>The subrecords, and the records that hold at least one.</summary>
    public long Uses, Records;
    public long Min = long.MaxValue, Max;
    /// <summary>The empty subrecords, and the records that hold at least one.</summary>
    public long Empty, RecordsEmpty;
    /// <summary>The records with an empty one, per plugin (in the set's order).</summary>
    public readonly Dictionary<string, long> PluginsEmpty = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The empty ones by where they stand: "" the record itself, else the component's BFCB text.</summary>
    public readonly Dictionary<string, long> EmptyIn = new(StringComparer.Ordinal);
    /// <summary>The first records (set order, then file order) with an empty one, and with one that has data.</summary>
    public readonly List<RecordRef> FirstEmpty = new(), FirstData = new();
    public List<string> EmptySamples { get; internal set; } = new();
    public List<string> DataSamples { get; internal set; } = new();

    public LengthClass Class => Max == 0 ? LengthClass.AlwaysEmpty : Empty > 0 ? LengthClass.SometimesEmpty : Min == Max ? LengthClass.Fixed : LengthClass.Varying;

    public string LengthsText => Class switch
    {
        LengthClass.AlwaysEmpty => "always empty",
        LengthClass.Fixed => $"always {Max} bytes",
        LengthClass.Varying => $"{Min} to {Max} bytes",
        _ => $"empty, or {MinData} to {Max} bytes",
    };

    /// <summary>The shortest length with data (a sometimes-empty kind's; else Min).</summary>
    public long MinData { get; internal set; } = long.MaxValue;
}

public sealed class SubrecordLengthsReport(ReportSession session)
{
    /// <summary>Records with an empty one, and with data, kept per kind for samples.</summary>
    public const int KeptEmpty = 3, KeptData = 2;

    /// <summary>What stepping through the records costs: as the order census (0.003 s a MB as stored on 8 threads).</summary>
    public (double Seconds, long MemoryMB, string What) Estimate(SubrecordsOptions o)
    {
        var (set, bytes, records) = SubrecordsReport.Size(session, o);
        return (0.5 + bytes / 1048576.0 * 0.003, 600, $"The lengths of {o.Signatures} step through {ReportAnswer.N(records)} records ({Sizes.Text(bytes)}) of {set.Shown}");
    }

    /// <summary>One signature in one record: its uses, empties, lengths, and where its empty ones stand.</summary>
    struct Met
    {
        public uint Sig; public int Times, Empty; public long Min, Max, MinData;
        public List<string>? EmptyRuns;
    }

    readonly record struct Stepped(int Set, int Entry, string RecordSig, List<Met>? Met, bool Unreadable);

    public SubrecordLengthsAnswer Lengths(SubrecordsOptions o) => session.Run(scope =>
    {
        session.Index.Refresh();
        string asked = o.Signatures.Trim();
        bool every = asked is "*" || asked.Equals("all", StringComparison.OrdinalIgnoreCase);
        var sigs = every ? new List<string>() : SubrecordsReport.Signatures(o.Signatures);
        var want = every ? null : sigs.Select(s => BinaryPrimitives.ReadUInt32LittleEndian(Encoding.Latin1.GetBytes(s))).ToArray();
        var set = PluginSet.Parse(session.Index, o.In, "@official");
        string? onlyType = SubrecordsReport.TypeSig(session, o.Type);
        var answer = new SubrecordLengthsAnswer { Set = set, Asked = every ? new List<string> { "*" } : sigs, Type = onlyType };
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
        var runNames = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var stats = new PipelineStats();
        var kinds = new Dictionary<(string Rec, uint Sig), SubrecordLengths>();
        foreach (var r in RecordPipeline.Read<Stepped>(sets, (in PipelineRecord rec, List<Stepped> output) =>
        {
            string rsig = rec.Plugin.Segment!.SignatureOf(rec.Header);
            if (rec.Problem != PipelineProblem.None) { output.Add(new Stepped(rec.Set, rec.EntryIndex, rsig, null, true)); return; }
            var met = Step(rec.Data, want, runNames);
            if (met != null) output.Add(new Stepped(rec.Set, rec.EntryIndex, rsig, met, false));
        }, new PipelineOptions { Stats = stats }))
        {
            if (r.Unreadable) { answer.Unreadable++; continue; }
            var p = sets[r.Set].Plugin;
            var rr = new RecordRef(p.Id, r.Entry);
            foreach (var m in r.Met!)
            {
                if (!kinds.TryGetValue((r.RecordSig, m.Sig), out var k))
                    kinds[(r.RecordSig, m.Sig)] = k = new SubrecordLengths { RecordSig = r.RecordSig, Subrecord = SigText(m.Sig) };
                k.Uses += m.Times; k.Records++;
                k.Min = Math.Min(k.Min, m.Min); k.Max = Math.Max(k.Max, m.Max); k.MinData = Math.Min(k.MinData, m.MinData);
                if (m.Empty > 0)
                {
                    k.Empty += m.Empty; k.RecordsEmpty++;
                    k.PluginsEmpty[p.Name] = k.PluginsEmpty.GetValueOrDefault(p.Name) + 1;
                    foreach (var run in m.EmptyRuns!) k.EmptyIn[run] = k.EmptyIn.GetValueOrDefault(run) + 1;
                    if (k.FirstEmpty.Count < KeptEmpty) k.FirstEmpty.Add(rr);
                }
                if (m.Empty < m.Times && k.FirstData.Count < KeptData) k.FirstData.Add(rr);
            }
        }
        for (int s = 0; s < sets.Count; s++) { answer.Records += stats.Records[s]; answer.Bytes += stats.StoredBytes[s]; answer.Inflated += stats.Compressed[s]; }
        foreach (var k in kinds.Values.OrderBy(k => k.RecordSig, StringComparer.Ordinal).ThenBy(k => k.Subrecord, StringComparer.Ordinal))
        {
            if (k.Empty > 0)
            {
                k.EmptySamples = k.FirstEmpty.Select(x => index.Describe(x).Label).ToList();
                if (k.Class == LengthClass.SometimesEmpty) k.DataSamples = k.FirstData.Select(x => index.Describe(x).Label).ToList();
            }
            answer.Kinds.Add(k);
        }
        foreach (var s in sigs) if (!answer.Kinds.Any(k => k.Subrecord == s)) answer.Missing.Add(s);
        answer.Read.Add($"the subrecords of {ReportAnswer.N(answer.Records)} records ({Sizes.Text(answer.Bytes)} as stored, {ReportAnswer.N(answer.Inflated)} inflated) of {ReportAnswer.Plural(answer.PluginsRead, "plugin")}, from the files' bytes on {PipelineOptions.DefaultThreads} threads (no record read through Mutagen)");
        if (answer.Unreadable > 0) answer.Notes.Add($"{ReportAnswer.Plural(answer.Unreadable, "record")} couldn't be read from the file (cut short, or compressed data that doesn't inflate) and aren't counted.");
        if (set.WinnersOnly) answer.Notes.Add("@winners: each record counts once, in the version the game uses.");
        return answer;
    });

    static string SigText(uint sig) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, sig); return Encoding.Latin1.GetString(b); }

    /// <summary>The lengths of the subrecords asked for (all when <paramref name="want"/> is null) in one record, one
    /// entry a signature. A BFCB opens a component and its BFCE closes it; the BFCB itself stands in the run around it.
    /// Null when the record holds none of them.</summary>
    static List<Met>? Step(ReadOnlySpan<byte> body, uint[]? want, ConcurrentDictionary<string, string> runNames)
    {
        List<Met>? met = null;
        var runs = new List<string>();
        int p = 0; uint? big = null;
        while (p + 6 <= body.Length)
        {
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(body[p..]);
            int n = BinaryPrimitives.ReadUInt16LittleEndian(body[(p + 4)..]);
            p += 6;
            if (sig == 0x58585858 /* XXXX */ && n == 4 && p + 4 <= body.Length) { big = BinaryPrimitives.ReadUInt32LittleEndian(body[p..]); p += 4; continue; }
            long len = big ?? (uint)n; big = null;
            var data = body.Slice(p, (int)Math.Min(len, body.Length - p));
            p = (int)Math.Min(body.Length, p + len);
            string run = runs.Count == 0 ? "" : runs[^1];
            if (want == null || Array.IndexOf(want, sig) >= 0)
            {
                met ??= new List<Met>();
                int k = met.FindIndex(m => m.Sig == sig);
                var m = k >= 0 ? met[k] : new Met { Sig = sig, Min = long.MaxValue, MinData = long.MaxValue };
                m.Times++;
                m.Min = Math.Min(m.Min, data.Length); m.Max = Math.Max(m.Max, data.Length);
                if (data.Length == 0) { m.Empty++; (m.EmptyRuns ??= new()).Add(run); }
                else m.MinData = Math.Min(m.MinData, data.Length);
                if (k >= 0) met[k] = m; else met.Add(m);
            }
            if (sig == 0x42434642 /* BFCB */)
            {
                string name = SubrecordValuesReport.Show(data).Shown;
                string full = run.Length == 0 ? name : run + " > " + name;
                runs.Add(runNames.GetOrAdd(full, full));
            }
            else if (sig == 0x45434642 /* BFCE */ && runs.Count > 0) runs.RemoveAt(runs.Count - 1);
        }
        return met;
    }
}

public sealed class SubrecordLengthsAnswer : ReportAnswer
{
    public override string Kind => "profile";
    public required PluginSet Set { get; init; }
    public required List<string> Asked { get; init; }
    public string? Type { get; init; }
    public long Records, Bytes, Inflated, Unreadable;
    public int PluginsRead;
    /// <summary>Every (record type, signature) met, by record type then signature.</summary>
    public List<SubrecordLengths> Kinds { get; } = new();
    /// <summary>Signatures asked for that no record holds.</summary>
    public List<string> Missing { get; } = new();

    static string Where(SubrecordLengths k) => string.Join(", ", k.EmptyIn.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal)
        .Select(x => (x.Key.Length == 0 ? "the record" : "component " + x.Key) + " " + N(x.Value)));

    protected override void Render(ReportBody body, ReportTextOptions options)
    {
        var empty = Kinds.Where(k => k.Empty > 0).OrderBy(k => k.Class == LengthClass.SometimesEmpty ? 0 : 1).ThenByDescending(k => k.Empty).ThenBy(k => k.RecordSig, StringComparer.Ordinal).ThenBy(k => k.Subrecord, StringComparer.Ordinal).ToList();
        var some = empty.Where(k => k.Class == LengthClass.SometimesEmpty).ToList();
        var rest = Kinds.Where(k => k.Empty == 0).ToList();
        string what = Asked.SequenceEqual(new[] { "*" }) ? "every subrecord" : string.Join(", ", Asked);
        body.Head.Add($"The lengths of {what} in {(Type != null ? $"the {Type} records" : "the records")} of {Set.Shown}: {Plural(Records, "record")} of {Plural(PluginsRead, "plugin")} stepped through, from the files' bytes (subrecords inside components, BFCB ... BFCE, too); {Plural(Kinds.Count, "kind")} (record type and subrecord), {Plural(Kinds.Sum(k => k.Uses), "subrecord")}.");
        body.Head.Add($"{Plural(empty.Count, "kind")} hold an empty subrecord (no bytes), {N(empty.Sum(k => k.Empty))} in all: {N(empty.Count - some.Count)} always empty (end markers such as BFCE, STOP, DSTF; marker fields), {N(some.Count)} sometimes empty and sometimes with data (present-but-empty against absent matters there). "
            + $"The other {N(rest.Count)}: {N(rest.Count(k => k.Class == LengthClass.Fixed))} of one length, {N(rest.Count(k => k.Class == LengthClass.Varying))} varying{(options.Detail ? " (listed after)" : " (--detail lists them)")}.");
        body.RowNoun = "rows";
        if (empty.Count > 0)
        {
            body.Head.Add("");
            body.Head.Add("| Record type | Subrecord | Lengths | Subrecords (empty) | Records (with an empty) | Where the empty ones stand | Plugins (records with an empty) | Records with an empty | Records with data |");
            body.Head.Add("|---|---|---|---|---|---|---|---|---|");
        }
        foreach (var k in empty)
        {
            var plugins = k.PluginsEmpty.OrderByDescending(x => x.Value).ThenBy(x => Set.Plugins.FindIndex(p => p.Name.Equals(x.Key, StringComparison.OrdinalIgnoreCase))).ToList();
            int ps = options.Detail ? plugins.Count : 3;
            string pl = $"{plugins.Count}: {string.Join(", ", plugins.Take(ps).Select(x => $"{x.Key} {N(x.Value)}"))}{(plugins.Count > ps ? ", …" : "")}";
            body.Rows.Add($"| {k.RecordSig} | {k.Subrecord} | {k.LengthsText} | {N(k.Uses)} ({N(k.Empty)}) | {N(k.Records)} ({N(k.RecordsEmpty)}) | {Cell(Where(k))} | {Cell(pl)} | {Cell(string.Join("; ", k.EmptySamples.Take(options.Detail || k.Class == LengthClass.SometimesEmpty ? KeptOf(k) : 1)))} | {Cell(string.Join("; ", k.DataSamples))} |");
        }
        if (options.Detail && rest.Count > 0)
        {
            body.Rows.Add("");
            body.Rows.Add("Kinds with no empty subrecord:");
            body.Rows.Add("| Record type | Subrecord | Lengths | Subrecords | Records |");
            body.Rows.Add("|---|---|---|---|---|");
            foreach (var k in rest) body.Rows.Add($"| {k.RecordSig} | {k.Subrecord} | {k.LengthsText} | {N(k.Uses)} | {N(k.Records)} |");
        }
        foreach (var s in Missing)
            body.Rows.Add($"{s}: no record in {Set.Shown}{(Type != null ? $" of type {Type}" : "")} holds it.");

        static int KeptOf(SubrecordLengths k) => k.EmptySamples.Count;
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
        static string ClassName(LengthClass c) => c switch { LengthClass.AlwaysEmpty => "alwaysEmpty", LengthClass.Fixed => "fixed", LengthClass.Varying => "varying", _ => "sometimesEmpty" };
        // Every kind with an empty subrecord in full
        json["withEmpty"] = new JsonArray(Kinds.Where(k => k.Empty > 0).Select(k => (JsonNode)new JsonObject
        {
            ["recordType"] = k.RecordSig, ["subrecord"] = k.Subrecord, ["lengths"] = ClassName(k.Class), ["min"] = k.Min, ["max"] = k.Max,
            ["minWithData"] = k.MinData == long.MaxValue ? null : k.MinData,
            ["subrecords"] = k.Uses, ["records"] = k.Records, ["empty"] = k.Empty, ["recordsWithEmpty"] = k.RecordsEmpty,
            ["emptyIn"] = new JsonObject(k.EmptyIn.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => KeyValuePair.Create(x.Key.Length == 0 ? "record" : x.Key, (JsonNode?)x.Value))),
            ["byPlugin"] = new JsonObject(k.PluginsEmpty.OrderBy(x => Set.Plugins.FindIndex(p => p.Name.Equals(x.Key, StringComparison.OrdinalIgnoreCase))).Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))),
            ["emptySamples"] = new JsonArray(k.EmptySamples.Select(s => (JsonNode)s).ToArray()),
            ["dataSamples"] = new JsonArray(k.DataSamples.Select(s => (JsonNode)s).ToArray()),
        }).ToArray());
        // The rest compact: one array a kind
        json["otherColumns"] = new JsonArray("recordType", "subrecord", "lengths", "min", "max", "subrecords", "records");
        json["other"] = new JsonArray(Kinds.Where(k => k.Empty == 0).Select(k => (JsonNode)new JsonArray(k.RecordSig, k.Subrecord, ClassName(k.Class), k.Min, k.Max, k.Uses, k.Records)).ToArray());
        json["missing"] = new JsonArray(Missing.Select(s => (JsonNode)s).ToArray());
    }
}
