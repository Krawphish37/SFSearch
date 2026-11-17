// profile --subrecords (GAPS S45): which record types hold a subrecord, across plugins, read from the files' bytes. C221
// asked "which record types hold STRD" with a Python walk of the official plugins; the schema says which types may (99
// for STRD, every one with components), and only the data says which do. Each record of the set is found through the
// header index (its place in the file), inflated when it is compressed, and its subrecords are stepped through (XXXX
// sizes too): nothing is read through Mutagen. Per signature asked: for each record type, the records that hold it,
// how many times, in how many plugins, and a sample record.
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json.Nodes;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Reports;

public sealed class SubrecordsOptions
{
    /// <summary>The subrecord signatures, parted by commas (STRD, XTV2).</summary>
    public required string Signatures { get; init; }
    /// <summary>The plugins (<see cref="PluginSet.Parse"/>). Default: @official.</summary>
    public string? In { get; init; }
    /// <summary>Only records of this type (a signature or a Mutagen name).</summary>
    public string? Type { get; init; }
}

/// <summary>One record type that holds a subrecord asked for.</summary>
public sealed class SubrecordHolder
{
    public required string Subrecord { get; init; }
    public required string RecordSig { get; init; }
    /// <summary>Records of the type that hold the subrecord, and of the type in all (in the plugins read).</summary>
    public long Records, Of, Times;
    public readonly Dictionary<string, long> Plugins = new(StringComparer.OrdinalIgnoreCase);
    public string? Sample;
}

public sealed class SubrecordsReport(ReportSession session)
{
    /// <summary>What stepping through the records costs, from the header index: measured 9 s for the 2.26 GB of the
    /// 33 official plugins (0.004 s a MB, compressed records inflated on the way).</summary>
    public (double Seconds, long MemoryMB, string What) Estimate(SubrecordsOptions o)
    {
        var (set, bytes, records) = Size(session, o);
        return (0.5 + bytes / 1048576.0 * 0.004, 300, $"Looking for {o.Signatures} steps through {ReportAnswer.N(records)} records ({Sizes.Text(bytes)}) of {set.Shown}");
    }

    /// <summary>The plugins, and the records (and their stored bytes) a census steps through.</summary>
    internal static (PluginSet Set, long Bytes, long Records) Size(ReportSession session, SubrecordsOptions o)
    {
        session.Index.Refresh();
        var set = PluginSet.Parse(session.Index, o.In, "@official");
        string? type = TypeSig(session, o.Type);
        long bytes = 0, records = 0;
        foreach (var p in set.Plugins)
            foreach (var s in session.Index.Stats(p.Name))
                if (type == null || s.Signature == type) { bytes += s.Bytes; records += s.Count; }
        return (set, bytes, records);
    }

    /// <summary>The signatures asked for, upper case, each once. <paramref name="all"/>: "*" (or "all") alone asks for
    /// every subrecord.</summary>
    internal static List<string> Signatures(string text, bool all = false)
    {
        var sigs = text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToUpperInvariant()).Distinct().ToList();
        if (sigs.Count == 0) throw new ReportException("no subrecord signature was given", all ? "give one or more, parted by commas (--subrecords UNAM,BNAM,PNAM), or * for every subrecord" : "give one or more, parted by commas: --subrecords STRD,XTV2");
        if (sigs.Any(s => s is "*" or "ALL"))
        {
            if (!all) throw new ReportException("* (every subrecord) is a question of its own", "ask for --subrecords * alone (what every subrecord of a type holds), with --order for their order, or name the subrecords: --subrecords STRD,XTV2");
            if (sigs.Count > 1) throw new ReportException("* (every subrecord) was given with signatures", "give * alone, or the signatures alone");
            return new List<string> { "*" };
        }
        foreach (var s in sigs)
            if (s.Length != 4 || s.Any(ch => ch < 0x20 || ch > 0x7E))
                throw new ReportException($"\"{s}\" isn't a subrecord signature", "give its 4 characters, parted by commas: STRD, XTV2");
        return sigs;
    }

    /// <summary>The signature of the record type named (a signature or a Mutagen name), or null when none was.</summary>
    internal static string? TypeSig(ReportSession session, string? type)
    {
        if (type is not { Length: > 0 } t) return null;
        var model = session.Schema.Model;
        return model.RecordTypes.FirstOrDefault(r => r.Sig!.Equals(t, StringComparison.OrdinalIgnoreCase) || r.Name.Equals(t, StringComparison.OrdinalIgnoreCase))?.Sig
            ?? throw new ReportException($"there is no record type named \"{t}\"", "give a signature (GBFM) or a Mutagen name (GenericBaseForm)");
    }

    public SubrecordsAnswer Census(SubrecordsOptions o) => session.Run(scope =>
    {
        session.Index.Refresh();
        var sigs = Signatures(o.Signatures);
        var set = PluginSet.Parse(session.Index, o.In, "@official");
        string? onlyType = TypeSig(session, o.Type);
        var want = sigs.Select(s => BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(s))).ToArray();
        var answer = new SubrecordsAnswer { Set = set, Asked = sigs, Type = onlyType };
        var index = session.Index;
        var allOfType = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var p in set.Plugins)
        {
            if (p.Segment is not { } seg) { answer.Notes.Add($"{p.Name} can't be read ({p.Problems.ReadError ?? "it has no header index"}): left out."); continue; }
            ReportSession.AddSource(answer, p);
            answer.PluginsRead++;
            var found = new Dictionary<(int Sub, string RecordSig), (long Records, long Times, int First)>();
            var ofType = new Dictionary<string, long>(StringComparer.Ordinal);
            long length = new FileInfo(p.Path).Length;
            using var stream = new FileStream(p.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
            using var view = file.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
            var counts = new int[want.Length];
            for (int e = 0; e < seg.Count; e++)
            {
                var entry = seg[e];
                string rsig = seg.SignatureOf(entry);
                if (onlyType != null && rsig != onlyType) continue;
                if (set.WinnersOnly && !index.IsWinner(new RecordRef(p.Id, e))) continue;
                ofType[rsig] = ofType.GetValueOrDefault(rsig) + 1;
                answer.Records++;
                answer.Bytes += entry.Size;
                if (entry.Offset + 24 + entry.Size > length) { answer.Unreadable++; continue; }
                var data = new byte[entry.Size];
                view.ReadArray(entry.Offset + 24, data, 0, data.Length);
                byte[] body = data;
                if (entry.IsCompressed)
                {
                    try
                    {
                        body = new byte[BinaryPrimitives.ReadUInt32LittleEndian(data)];
                        using var z = new ZLibStream(new MemoryStream(data, 4, data.Length - 4), CompressionMode.Decompress);
                        z.ReadExactly(body);
                        answer.Inflated++;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException) { answer.Unreadable++; continue; }
                }
                Array.Clear(counts);
                Step(body, want, counts);
                for (int i = 0; i < want.Length; i++)
                {
                    if (counts[i] == 0) continue;
                    var key = (i, rsig);
                    var have = found.TryGetValue(key, out var h0) ? h0 : (Records: 0L, Times: 0L, First: -1);
                    found[key] = (have.Records + 1, have.Times + counts[i], have.First < 0 ? e : have.First);
                }
            }
            foreach (var ((sub, rsig), (records, times, first)) in found)
            {
                var h = answer.Holder(sigs[sub], rsig);
                h.Records += records; h.Times += times;
                h.Plugins[p.Name] = h.Plugins.GetValueOrDefault(p.Name) + records;
                if (h.Sample == null)
                {
                    var info = index.Describe(new RecordRef(p.Id, first));
                    h.Sample = $"{info.EditorId ?? "(no EditorID)"} ({info.FormKey})";
                }
            }
            foreach (var (rs, n) in ofType) allOfType[rs] = allOfType.GetValueOrDefault(rs) + n;
        }
        foreach (var h in answer.Holders) h.Of = allOfType.GetValueOrDefault(h.RecordSig);
        answer.Read.Add($"the subrecords of {ReportAnswer.N(answer.Records)} records ({Sizes.Text(answer.Bytes)} as stored, {ReportAnswer.N(answer.Inflated)} inflated) of {ReportAnswer.Plural(answer.PluginsRead, "plugin")}, from the files' bytes (no record read through Mutagen)");
        if (answer.Unreadable > 0) answer.Notes.Add($"{ReportAnswer.Plural(answer.Unreadable, "record")} couldn't be read from the file (cut short, or compressed data that doesn't inflate) and aren't counted.");
        if (set.WinnersOnly) answer.Notes.Add("@winners: each record counts once, in the version the game uses.");
        // What the schema allows, beside what the data holds
        foreach (var s in sigs)
        {
            var may = session.Schema.Model.RecordTypes.Where(r => session.Schema.Model.Expand(r.Name).Any(row => row.Subrecord == s || row.Def.Subrecord == s)).Select(r => r.Sig!).Distinct().ToList();
            answer.SchemaTypes[s] = may;
        }
        return answer;
    });

    /// <summary>Counts the subrecords asked for among a record's subrecords. An XXXX gives the size of the next one.</summary>
    static void Step(byte[] body, uint[] want, int[] counts)
    {
        int p = 0; uint? big = null;
        while (p + 6 <= body.Length)
        {
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(p));
            int n = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(p + 4));
            p += 6;
            if (sig == 0x58585858 /* XXXX */ && n == 4 && p + 4 <= body.Length) { big = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(p)); p += 4; continue; }
            long len = big ?? (uint)n; big = null;
            for (int i = 0; i < want.Length; i++) if (want[i] == sig) counts[i]++;
            p = (int)Math.Min(body.Length, p + len);
        }
    }
}

public sealed class SubrecordsAnswer : ReportAnswer
{
    public override string Kind => "profile";
    public required PluginSet Set { get; init; }
    public required List<string> Asked { get; init; }
    public string? Type { get; init; }
    public long Records, Bytes, Inflated, Unreadable;
    public int PluginsRead;
    public List<SubrecordHolder> Holders { get; } = new();
    /// <summary>For each signature asked: the record types whose fields the schema puts in it.</summary>
    public Dictionary<string, List<string>> SchemaTypes { get; } = new(StringComparer.Ordinal);

    internal SubrecordHolder Holder(string sub, string rsig)
    {
        var h = Holders.FirstOrDefault(x => x.Subrecord == sub && x.RecordSig == rsig);
        if (h == null) Holders.Add(h = new SubrecordHolder { Subrecord = sub, RecordSig = rsig });
        return h;
    }

    protected override void Render(ReportBody body, ReportTextOptions options)
    {
        body.Head.Add($"Which record types hold {string.Join(", ", Asked)} in {Set.Shown}{(Type != null ? $" (only {Type})" : "")}: {N(Records)} records of {Plural(PluginsRead, "plugin")} stepped through, from the files' bytes.");
        body.RowNoun = "rows";
        body.Head.Add("");
        body.Head.Add("| Subrecord | Record type | Records that hold it | Of the type's records | Times | Plugins | Sample |");
        body.Head.Add("|---|---|---|---|---|---|---|");
        foreach (var h in Holders.OrderBy(h => Asked.IndexOf(h.Subrecord)).ThenByDescending(h => h.Records).ThenBy(h => h.RecordSig, StringComparer.Ordinal))
        {
            var plugins = h.Plugins.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();
            int show = options.Detail ? plugins.Count : 3;
            string pl = $"{plugins.Count}: {string.Join(", ", plugins.Take(show).Select(x => $"{x.Key} {N(x.Value)}"))}{(plugins.Count > show ? ", …" : "")}";
            body.Rows.Add($"| {h.Subrecord} | {h.RecordSig} | {N(h.Records)} | {N(h.Of)} | {N(h.Times)} | {Cell(pl)} | {Cell(h.Sample)} |");
        }
        foreach (var s in Asked)
        {
            var held = Holders.Where(h => h.Subrecord == s).Select(h => h.RecordSig).ToHashSet(StringComparer.Ordinal);
            var may = SchemaTypes.GetValueOrDefault(s) ?? new();
            string mayText = may.Count == 0 ? "the schema has no field in it" : $"the schema puts fields of {Plural(may.Count, "record type")} in it";
            body.Rows.Add(held.Count == 0 ? $"{s}: no record in {Set.Shown} holds it ({mayText})."
                : $"{s}: held by {string.Join(", ", held.OrderBy(x => x, StringComparer.Ordinal))} only; {mayText}{(may.Count > 0 && may.Count <= 12 ? $" ({string.Join(", ", may)})" : "")}.");
        }
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
        json["holders"] = new JsonArray(Holders.Select(h => (JsonNode)new JsonObject
        {
            ["subrecord"] = h.Subrecord, ["recordType"] = h.RecordSig, ["records"] = h.Records, ["of"] = h.Of, ["times"] = h.Times, ["sample"] = h.Sample,
            ["byPlugin"] = new JsonObject(h.Plugins.OrderBy(p => Set.Plugins.FindIndex(x => x.Name.Equals(p.Key, StringComparison.OrdinalIgnoreCase))).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
        }).ToArray());
        json["schemaTypes"] = new JsonObject(SchemaTypes.Select(s => KeyValuePair.Create(s.Key, (JsonNode?)new JsonArray(s.Value.Select(x => (JsonNode)x).ToArray()))));
    }
}
