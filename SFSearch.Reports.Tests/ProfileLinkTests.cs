// What a link field points at (GAPS S45): profile's link census held against Mutagen read directly; and profile
// --subrecords, which record types hold a subrecord, held against a walk of the files' bytes written here.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class ProfileLinkTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static readonly string[] Plugins = { "kgcdoom.esm", "sfta01.esm", "SFBGS003.esm" };

    public static string Links(Ctx ctx)
    {
        var parts = new List<string>();
        var s = ctx.Session;
        var report = new ProfileReport(s);
        var a = report.Run(report.Plan(new ProfileOptions { Fields = new[] { "PlacedObject.GroupedPackIn.GNAM", "PlacedObject.GroupedPackIn.JNAM", "PlacedObject.GroupedPackIn.HNAM" }, In = string.Join(",", Plugins) }));
        ctx.WriteReport("profile-links.txt", a.ToText(new ReportTextOptions { Detail = true }));

        // Mutagen read directly: each placed object's GroupedPackIn
        var uses = new Dictionary<string, Dictionary<FormKey, long>> { ["GNAM"] = new(), ["JNAM"] = new(), ["HNAM"] = new() };
        var nulls = new Dictionary<string, long> { ["GNAM"] = 0, ["JNAM"] = 0, ["HNAM"] = 0 };
        foreach (string p in Plugins)
            foreach (var r in s.Engine.Sessions.Get(p).Mod.EnumerateMajorRecords<IPlacedObjectGetter>())
            {
                if (r.GroupedPackIn is not { } g) continue;
                foreach (var (name, link) in new[] { ("GNAM", g.GNAM.FormKeyNullable), ("JNAM", g.JNAM.FormKeyNullable), ("HNAM", g.HNAM.FormKeyNullable) })
                {
                    if (link is not { IsNull: false } k) { nulls[name]++; continue; }
                    uses[name][k] = uses[name].GetValueOrDefault(k) + 1;
                }
            }
        foreach (var name in new[] { "GNAM", "JNAM", "HNAM" })
        {
            var f = a.Fields.Single(x => x.Target.Name == "PlacedObject.GroupedPackIn." + name);
            var lc = f.Links ?? throw new TestFailed($"{name}: no link census");
            Check(lc.Links == uses[name].Values.Sum() && lc.Nulls == nulls[name] && lc.Distinct == uses[name].Count,
                $"{name}: {lc.Links} links, {lc.Nulls} null, {lc.Distinct} distinct; Mutagen {uses[name].Values.Sum()}, {nulls[name]}, {uses[name].Count}");
            // Each target's record type: the record its owner holds, in its owner's own file (Mutagen)
            var types = new Dictionary<string, (long, long)>();
            foreach (var (k, n) in uses[name])
            {
                var owner = s.Engine.Sessions.Get(k.ModKey.FileName).Mod;
                string sig = owner.EnumerateMajorRecords().FirstOrDefault(x => x.FormKey == k) is { } rec ? s.Schema.SignatureOf(SFSearch.Records.RecordSerializer.TypeName(rec)) ?? "?" : "(not in Data)";
                var t = types.GetValueOrDefault(sig);
                types[sig] = (t.Item1 + 1, t.Item2 + n);
            }
            string Text(IEnumerable<KeyValuePair<string, (long, long)>> d) => string.Join(", ", d.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key} {x.Value.Item1}/{x.Value.Item2}"));
            Check(Text(lc.Types) == Text(types), $"{name}'s target types: {Text(lc.Types)}; Mutagen {Text(types)}");
            parts.Add($"{name} {lc.Distinct} targets ({Text(lc.Types)})");
        }
        // The three together: each target once
        var all = uses.Values.SelectMany(d => d.Keys).Distinct().Count();
        Check(a.LinksTogether is { } lt && lt.Distinct == all && lt.Links == uses.Values.Sum(d => d.Values.Sum()), $"together: {a.LinksTogether?.Distinct} distinct; Mutagen {all}");
        parts.Add($"together {all} targets");
        return string.Join("; ", parts);
    }

    public static string Subrecords(Ctx ctx)
    {
        var parts = new List<string>();
        var a = new SubrecordsReport(ctx.Session).Census(new SubrecordsOptions { Signatures = "STRD,XTV2,KWDA", In = string.Join(",", Plugins) });
        ctx.WriteReport("profile-subrecords.txt", a.ToText(new ReportTextOptions { Detail = true }));
        // The bytes, walked here
        var want = new Dictionary<(string, string), (long Records, long Times)>();
        foreach (string p in Plugins) Walk(Path.Combine(ctx.Data, p), want);
        string Text(IEnumerable<((string, string) Key, (long, long) V)> d) => string.Join(", ", d.OrderBy(x => x.Key.Item1).ThenBy(x => x.Key.Item2).Select(x => $"{x.Key.Item1}/{x.Key.Item2} {x.V.Item1}/{x.V.Item2}"));
        string got = Text(a.Holders.Select(h => ((h.Subrecord, h.RecordSig), (h.Records, h.Times))));
        Check(got == Text(want.Select(kv => (kv.Key, (kv.Value.Records, kv.Value.Times)))), $"holders: {got}; the bytes: {Text(want.Select(kv => (kv.Key, (kv.Value.Records, kv.Value.Times))))}");
        // Mutagen: the placed objects whose Traversals are there are the REFR that hold XTV2
        long refr = Plugins.Sum(p => (long)ctx.Session.Engine.Sessions.Get(p).Mod.EnumerateMajorRecords<IPlacedObjectGetter>().Count(r => r.Traversals != null));
        Check(a.Holders.FirstOrDefault(h => h.Subrecord == "XTV2" && h.RecordSig == "REFR")?.Records == refr, $"REFR with XTV2: {a.Holders.FirstOrDefault(h => h.Subrecord == "XTV2" && h.RecordSig == "REFR")?.Records}; Mutagen's placed objects with traversals {refr}");
        parts.Add($"{a.Holders.Count} (subrecord, record type) pairs equal the bytes; REFR with XTV2 {refr} as Mutagen");
        try { new SubrecordsReport(ctx.Session).Census(new SubrecordsOptions { Signatures = "STRDX", In = "kgcdoom.esm" }); throw new TestFailed("STRDX was taken"); }
        catch (ReportException e) { Check(e.Message.Contains("isn't a subrecord signature", StringComparison.Ordinal), e.Line); }
        if (ctx.CanRunToday)
        {
            string json = ctx.Today("profile__subrecords", "--json", "profile", "--subrecords", "STRD", "--in", "kgcdoom.esm")!;
            Check(JsonNode.Parse(json)!["data"]!["subrecords"]!.AsArray().Count == 1, "the verb's JSON: " + json[..Math.Min(300, json.Length)]);
            parts.Add("verb JSON");
        }
        return string.Join("; ", parts);
    }

    /// <summary>Every record of a plugin, its subrecords counted (STRD, XTV2, KWDA) by its signature.</summary>
    static void Walk(string path, Dictionary<(string, string), (long Records, long Times)> into)
    {
        var data = File.ReadAllBytes(path);
        string[] want = { "STRD", "XTV2", "KWDA" };
        void Rec(string rsig, ReadOnlySpan<byte> body)
        {
            var n = new long[want.Length];
            int p = 0; uint? big = null;
            while (p + 6 <= body.Length)
            {
                string sig = Encoding.ASCII.GetString(body.Slice(p, 4)); int len = BinaryPrimitives.ReadUInt16LittleEndian(body[(p + 4)..]); p += 6;
                if (sig == "XXXX") { big = BinaryPrimitives.ReadUInt32LittleEndian(body[p..]); p += len; continue; }
                long l = big ?? (uint)len; big = null;
                int i = Array.IndexOf(want, sig);
                if (i >= 0) n[i]++;
                p += (int)l;
            }
            for (int i = 0; i < want.Length; i++)
                if (n[i] > 0) { var have = into.GetValueOrDefault((want[i], rsig)); into[(want[i], rsig)] = (have.Records + 1, have.Times + n[i]); }
        }
        long pos = 0;
        void Group(long p, long end)
        {
            while (p < end)
            {
                string sig = Encoding.ASCII.GetString(data, (int)p, 4); uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)p + 4));
                if (sig == "GRUP") { Group(p + 24, p + size); p += size; continue; }
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)p + 8));
                var body = data.AsSpan((int)p + 24, (int)size);
                if (sig != "TES4")
                {
                    if ((flags & 0x40000) != 0)
                    {
                        var buf = new byte[BinaryPrimitives.ReadInt32LittleEndian(body)];
                        using (var z = new ZLibStream(new MemoryStream(body[4..].ToArray()), CompressionMode.Decompress)) z.ReadExactly(buf);
                        Rec(sig, buf);
                    }
                    else Rec(sig, body);
                }
                p += 24 + size;
            }
        }
        Group(pos, data.Length);
    }
}
