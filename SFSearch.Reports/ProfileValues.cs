// profile --subrecords X --values (GAPS S118): the distinct raw values of one subrecord across plugins, read from the
// files' bytes on all cores. A modder asks it of a subrecord no schema field covers, or before trusting one: which
// component kinds exist (every BFCB text), which values a flags or type subrecord takes, which texts a mod writes where
// the game writes others. The Mutagen session's C63 had to scan every BFCB text of the 370 plugins with a raw scanner
// of its own to learn which component kinds exist. Every record of the set goes through the record pipeline (found
// through the header index, inflated when compressed); its subrecords are stepped through (XXXX sizes the next one) and
// each one asked for gives its bytes, with where it stands: the record itself, or the component (a run from BFCB to its
// BFCE) whose BFCB text is given. Per subrecord and run: each distinct value, as text when its bytes are one (printable
// characters and one closing zero) and as hex otherwise, with its uses, its records, the record types and plugins that
// hold it and a sample record. Nothing goes through Mutagen.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Reports;

/// <summary>One distinct value of a subrecord in one run: how often, where, a sample.</summary>
public sealed class SubrecordValue
{
    /// <summary>The value as shown: "text" in quotes, or hex bytes (the first 48, then the length).</summary>
    public required string Shown { get; init; }
    public required bool IsText { get; init; }
    /// <summary>The value's length in bytes.</summary>
    public required int Length { get; init; }
    public long Uses, Records;
    public readonly Dictionary<string, long> RecordTypes = new(StringComparer.Ordinal);
    public readonly Dictionary<string, long> Plugins = new(StringComparer.OrdinalIgnoreCase);
    public RecordRef First;
    public string? Sample;
}

/// <summary>A subrecord's values in one run: the record itself ("") or a component (its BFCB text).</summary>
public sealed class ValueGroup
{
    public required string Subrecord { get; init; }
    /// <summary>"" for the record itself; else the BFCB text of the component it stands in (an inner one after an
    /// outer one, parted by " > ").</summary>
    public required string Run { get; init; }
    public long Uses, Records;
    public readonly Dictionary<string, SubrecordValue> Values = new(StringComparer.Ordinal);
    public readonly Dictionary<string, long> RecordTypes = new(StringComparer.Ordinal);
    /// <summary>The values, most used first (made at the end).</summary>
    public List<SubrecordValue> Sorted { get; internal set; } = new();
    public string RunText => Run.Length == 0 ? "the record" : $"component {Run}";
}

public sealed class SubrecordValuesReport(ReportSession session)
{
    /// <summary>What stepping through the records costs: as the order census (0.003 s a MB as stored on 8 threads),
    /// and memory for the distinct values (at most one per subrecord met).</summary>
    public (double Seconds, long MemoryMB, string What) Estimate(SubrecordsOptions o)
    {
        var (set, bytes, records) = SubrecordsReport.Size(session, o);
        return (0.5 + bytes / 1048576.0 * 0.003, 1000, $"The values of {o.Signatures} step through {ReportAnswer.N(records)} records ({Sizes.Text(bytes)}) of {set.Shown}");
    }

    /// <summary>Bytes of a value shown in hex; text is shown up to <see cref="TextShown"/> characters.</summary>
    public const int HexShown = 48, TextShown = 300;

    /// <summary>One value met in a record: which subrecord asked for, in which run, its key and how it shows.</summary>
    readonly record struct Met(int Sub, string Run, string Key, string Shown, bool IsText, int Length, int Times);

    readonly record struct Stepped(int Set, int Entry, string RecordSig, List<Met>? Values, bool Unreadable);

    public SubrecordValuesAnswer Values(SubrecordsOptions o) => session.Run(scope =>
    {
        session.Index.Refresh();
        if (o.Signatures.Split(',', ' ').Any(s => s.Trim() is "*" || s.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)))
            throw new ReportException("--values counts the values of the subrecords named, and * (every subrecord) was given", "name them: --subrecords BFCB --values (--subrecords * --type T gives what every subrecord of a type holds)");
        var sigs = SubrecordsReport.Signatures(o.Signatures);
        var set = PluginSet.Parse(session.Index, o.In, "@official");
        string? onlyType = SubrecordsReport.TypeSig(session, o.Type);
        var want = sigs.Select(s => BinaryPrimitives.ReadUInt32LittleEndian(Encoding.Latin1.GetBytes(s))).ToArray();
        var answer = new SubrecordValuesAnswer { Set = set, Asked = sigs, Type = onlyType };
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
        // The runs' names, one string each
        var runNames = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var stats = new PipelineStats();
        var groups = new Dictionary<(int Sub, string Run), ValueGroup>();
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
            var seenGroups = new HashSet<(int, string)>();
            foreach (var m in r.Values!)
            {
                if (!groups.TryGetValue((m.Sub, m.Run), out var g)) groups[(m.Sub, m.Run)] = g = new ValueGroup { Subrecord = sigs[m.Sub], Run = m.Run };
                g.Uses += m.Times;
                if (seenGroups.Add((m.Sub, m.Run))) { g.Records++; g.RecordTypes[r.RecordSig] = g.RecordTypes.GetValueOrDefault(r.RecordSig) + 1; }
                if (!g.Values.TryGetValue(m.Key, out var v)) g.Values[m.Key] = v = new SubrecordValue { Shown = m.Shown, IsText = m.IsText, Length = m.Length, First = rr };
                v.Uses += m.Times;
                v.Records++;
                v.RecordTypes[r.RecordSig] = v.RecordTypes.GetValueOrDefault(r.RecordSig) + 1;
                v.Plugins[p.Name] = v.Plugins.GetValueOrDefault(p.Name) + 1;
            }
        }
        for (int s = 0; s < sets.Count; s++) { answer.Records += stats.Records[s]; answer.Bytes += stats.StoredBytes[s]; answer.Inflated += stats.Compressed[s]; }
        foreach (var g in groups.Values.OrderBy(g => sigs.IndexOf(g.Subrecord)).ThenBy(g => g.Run.Length == 0 ? 0 : 1).ThenByDescending(g => g.Uses).ThenBy(g => g.Run, StringComparer.Ordinal))
        {
            g.Sorted = g.Values.Values.OrderByDescending(v => v.Uses).ThenBy(v => v.Shown, StringComparer.Ordinal).ToList();
            foreach (var v in g.Sorted.Take(SubrecordValuesAnswer.Kept)) v.Sample = index.Describe(v.First).Label;
            answer.Groups.Add(g);
        }
        answer.Read.Add($"the subrecords of {ReportAnswer.N(answer.Records)} records ({Sizes.Text(answer.Bytes)} as stored, {ReportAnswer.N(answer.Inflated)} inflated) of {ReportAnswer.Plural(answer.PluginsRead, "plugin")}, from the files' bytes on {PipelineOptions.DefaultThreads} threads (no record read through Mutagen)");
        if (answer.Unreadable > 0) answer.Notes.Add($"{ReportAnswer.Plural(answer.Unreadable, "record")} couldn't be read from the file (cut short, or compressed data that doesn't inflate) and aren't counted.");
        if (set.WinnersOnly) answer.Notes.Add("@winners: each record counts once, in the version the game uses.");
        return answer;
    });

    /// <summary>The values of the subrecords asked for in one record, each distinct (subrecord, run, value) once with how
    /// many times it stands there. A BFCB opens a component and its BFCE closes it; the BFCB itself stands in the run
    /// around it. Null when the record holds none of them.</summary>
    static List<Met>? Step(ReadOnlySpan<byte> body, uint[] want, ConcurrentDictionary<string, string> runNames)
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
            int i = Array.IndexOf(want, sig);
            if (i >= 0)
            {
                string key = Key(data);
                met ??= new List<Met>();
                int k = met.FindIndex(m => m.Sub == i && ReferenceEquals(m.Run, run) && m.Key == key);
                if (k >= 0) met[k] = met[k] with { Times = met[k].Times + 1 };
                else
                {
                    var (shown, isText) = Show(data);
                    met.Add(new Met(i, run, key, shown, isText, data.Length, 1));
                }
            }
            if (sig == 0x42434642 /* BFCB */)
            {
                string name = Show(data).Shown;
                string full = run.Length == 0 ? name : run + " > " + name;
                runs.Add(runNames.GetOrAdd(full, full));
            }
            else if (sig == 0x45434642 /* BFCE */ && runs.Count > 0) runs.RemoveAt(runs.Count - 1);
        }
        return met;
    }

    /// <summary>A value's key: its bytes one char each; past 64 bytes, the first 64, its length and a 64-bit FNV-1a hash
    /// of all of them (two long values with the same start, length and hash would be counted as one).</summary>
    static string Key(ReadOnlySpan<byte> data)
    {
        if (data.Length <= 64) return Encoding.Latin1.GetString(data);
        ulong h = 14695981039346656037UL;
        foreach (byte b in data) { h ^= b; h *= 1099511628211UL; }
        return Encoding.Latin1.GetString(data[..64]) + "￿" + data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + h.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
    }

    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// How a value shows: text in quotes when its bytes are one (at least one character, printable, then exactly one
    /// closing zero; read as UTF-8, else as Windows-1252, the game's two encodings), the empty text "" for a lone zero;
    /// otherwise its bytes in hex (the first 48, then the length).
    /// </summary>
    public static (string Shown, bool IsText) Show(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 1 && data[^1] == 0)
        {
            var body = data[..^1];
            bool printable = true;
            foreach (byte b in body) if (b < 0x20 || b == 0x7F) { printable = false; break; }
            if (printable)
            {
                string text;
                try { text = StrictUtf8.GetString(body); }
                catch (DecoderFallbackException) { text = Mutagen.Bethesda.Strings.DI.MutagenEncoding._1252.GetString(body); }
                if (text.Length > TextShown) text = text[..TextShown] + $"… ({data.Length} bytes)";
                return ("\"" + text + "\"", true);
            }
        }
        if (data.Length == 0) return ("(empty: no bytes)", false);
        string hex = Convert.ToHexString(data[..Math.Min(data.Length, HexShown)]);
        var sb = new StringBuilder(hex.Length * 3 / 2);
        for (int i = 0; i < hex.Length; i += 2) { if (i > 0) sb.Append(' '); sb.Append(hex, i, 2); }
        if (data.Length > HexShown) sb.Append($" … ({data.Length} bytes)");
        return (sb.ToString(), false);
    }
}

public sealed class SubrecordValuesAnswer : ReportAnswer
{
    public override string Kind => "profile";
    public required PluginSet Set { get; init; }
    public required List<string> Asked { get; init; }
    public string? Type { get; init; }
    public long Records, Bytes, Inflated, Unreadable;
    public int PluginsRead;
    /// <summary>Per subrecord asked (in the order asked) and run (the record itself first, then by uses).</summary>
    public List<ValueGroup> Groups { get; } = new();

    /// <summary>Values shown per group in the text without --detail; kept in the JSON (and with --detail).</summary>
    public const int Shown = 25, Kept = 200;

    protected override void Render(ReportBody body, ReportTextOptions options)
    {
        long uses = Groups.Sum(g => g.Uses);
        body.Head.Add($"The values of {string.Join(", ", Asked)} in {(Type != null ? $"the {Type} records" : "the records")} of {Set.Shown}: {Plural(Records, "record")} of {Plural(PluginsRead, "plugin")} stepped through, from the files' bytes; {Plural(uses, "subrecord")} in {Plural(Groups.Count, "place")}.");
        body.Head.Add("A value is text (\"...\", shown without its closing zero) when its bytes are printable characters and one closing zero; otherwise hex. Where: the record itself, or the component (BFCB ... BFCE) whose BFCB text is given. Uses: the subrecords; Records: the records that hold the value there.");
        body.RowNoun = "rows";
        if (Groups.Count > 0)
        {
            body.Head.Add("");
            body.Head.Add("| Subrecord | Where | Value | Uses | Records | Record types | Plugins | Sample |");
            body.Head.Add("|---|---|---|---|---|---|---|---|");
        }
        foreach (var g in Groups)
        {
            int show = options.Detail ? Kept : Shown;
            foreach (var v in g.Sorted.Take(show))
            {
                var types = v.RecordTypes.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
                int ts = options.Detail ? types.Count : 3;
                string tt = $"{types.Count}: {string.Join(", ", types.Take(ts).Select(x => $"{x.Key} {N(x.Value)}"))}{(types.Count > ts ? ", …" : "")}";
                var plugins = v.Plugins.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();
                int ps = options.Detail ? plugins.Count : 2;
                string pl = $"{plugins.Count}: {string.Join(", ", plugins.Take(ps).Select(x => $"{x.Key} {N(x.Value)}"))}{(plugins.Count > ps ? ", …" : "")}";
                body.Rows.Add($"| {g.Subrecord} | {Cell(g.RunText)} | {Cell(v.Shown)} | {N(v.Uses)} | {N(v.Records)} | {Cell(tt)} | {Cell(pl)} | {Cell(v.Sample)} |");
            }
            var rest = g.Sorted.Skip(show).ToList();
            string more = rest.Count > 0 ? $"; {Plural(rest.Count, "more value")} with {Plural(rest.Sum(v => v.Uses), "use")}{(options.Detail ? "" : $" (--detail shows up to {Kept})")}" : "";
            var gt = g.RecordTypes.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
            body.Rows.Add($"{g.Subrecord} in {g.RunText}: {Plural(g.Values.Count, "distinct value")}, {Plural(g.Uses, "use")} in {Plural(g.Records, "record")} of {Plural(gt.Count, "record type")} ({string.Join(", ", gt.Take(options.Detail ? gt.Count : 6).Select(x => $"{x.Key} {N(x.Value)}"))}{(!options.Detail && gt.Count > 6 ? ", …" : "")}){more}.");
        }
        foreach (var s in Asked.Where(s => Groups.All(g => g.Subrecord != s)))
            body.Rows.Add($"{s}: no record in {Set.Shown}{(Type != null ? $" of type {Type}" : "")} holds it.");
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
        json["places"] = new JsonArray(Groups.Select(g => (JsonNode)new JsonObject
        {
            ["subrecord"] = g.Subrecord, ["run"] = g.Run.Length == 0 ? null : g.Run, ["uses"] = g.Uses, ["records"] = g.Records, ["distinct"] = g.Values.Count,
            ["byRecordType"] = new JsonObject(g.RecordTypes.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))),
            ["values"] = new JsonArray(g.Sorted.Take(Kept).Select(v => (JsonNode)new JsonObject
            {
                ["value"] = v.Shown, ["text"] = v.IsText, ["bytes"] = v.Length, ["uses"] = v.Uses, ["records"] = v.Records, ["sample"] = v.Sample,
                ["byRecordType"] = new JsonObject(v.RecordTypes.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))),
                ["byPlugin"] = new JsonObject(v.Plugins.OrderBy(x => Set.Plugins.FindIndex(p => p.Name.Equals(x.Key, StringComparison.OrdinalIgnoreCase))).Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))),
            }).ToArray()),
            ["moreValues"] = Math.Max(0, g.Sorted.Count - Kept),
        }).ToArray());
    }
}
