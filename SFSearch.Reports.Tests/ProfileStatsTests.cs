// profile --subrecords * (GAPS S52): what a record type's records hold, how big, how linked. Held against a plugin
// written here byte by byte, against a walk of real plugins' bytes written here (records, sizes, every subrecord), and
// every link field read from the bytes against Mutagen's own values (each field walked along its path on Mutagen's object).
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Starfield;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class ProfileStatsTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    const string Plugin = "sfs_s52_stats.esm";

    public static string Stats(Ctx ctx)
    {
        var parts = new List<string> { Made(ctx), Real(ctx) };
        if (ctx.CanRunToday)
        {
            string json = ctx.Today("profile__subrecords__all", "--json", "profile", "--subrecords", "*", "--type", "CELL", "--in", "sfta01.esm")!;
            var types = JsonNode.Parse(json)!["data"]!["types"]!.AsArray();
            Check(types.Count == 1 && (string?)types[0]!["recordType"] == "CELL" && types[0]!["subrecords"]!.AsArray().Count > 0, "the verb's JSON: " + json[..Math.Min(400, json.Length)]);
            parts.Add("verb JSON");
        }
        return string.Join("; ", parts);
    }

    static (string, byte[]) Sub(string sig, params uint[] ids)
    {
        var b = new byte[ids.Length * 4];
        for (int i = 0; i < ids.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), ids[i]);
        return (sig, b);
    }

    /// <summary>
    /// Three placed objects written byte by byte (the plugin has no master: every FormID is its own). 000800: NAME 900,
    /// two XLKR (keyword 901 with 902; none with 903), XLMS 904 905 904, a 24-byte DATA. 000801, compressed: NAME 900,
    /// XLYR 906, a component (BFCB … BFCE) holding XLYR 907, which is the component's, XPDO 0 908. 000802: NAME 0.
    /// </summary>
    static string Made(Ctx ctx)
    {
        string data = ctx.Folder("stats-data");
        string path = Path.Combine(data, Plugin);
        ctx.MustBeInWork(path);
        new StarfieldMod(Mutagen.Bethesda.Plugins.ModKey.FromFileName(Plugin), StarfieldRelease.Starfield).WriteToBinary(path);
        var written = new[]
        {
            (0x800u, false, new[] { ("EDID", Encoding.ASCII.GetBytes("sfsStats1\0")), Sub("NAME", 0x900), Sub("XLKR", 0x901, 0x902), Sub("XLKR", 0, 0x903), Sub("XLMS", 0x904, 0x905, 0x904), ("DATA", new byte[24]) }),
            (0x801u, true, new[] { Sub("NAME", 0x900), Sub("XLYR", 0x906), ("BFCB", Encoding.ASCII.GetBytes("BGSSomeComponent\0")), Sub("XLYR", 0x907), ("BFCE", Array.Empty<byte>()), Sub("XPDO", 0, 0x908) }),
            (0x802u, false, new[] { Sub("NAME", 0) }),
        };
        var refr = written.Select(w => ProfileOrderTests.Record("REFR", w.Item1, w.Item2, w.Item3)).ToList();
        long read = written.Sum(w => w.Item3.Sum(x => 6L + x.Item2.Length));
        using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write)) fs.Write(ProfileOrderTests.Group("REFR", refr));
        long file = refr.Sum(r => (long)r.Length);

        using var session = new ReportSession(data, Path.Combine(ctx.Work, "scratch-stats"), ctx.Schema, "sfsreport-tests");
        var a = new TypeStatsReport(session).Stats(new SubrecordsOptions { Signatures = "*", In = Plugin, Type = "PlacedObject" });
        ctx.WriteReport("profile-stats-made.txt", a.ToText(new ReportTextOptions { Detail = true }));
        var t = a.Types.Single();
        Check(t.Sig == "REFR" && t.Records == 3 && t.Compressed == 1 && t.FileBytes == file && t.ReadBytes == read,
            $"REFR: {t.Records} records, {t.Compressed} compressed, {t.FileBytes} bytes in the file ({file} written), {t.ReadBytes} as read ({read} written)");
        string Subs() => string.Join(", ", t.Subrecords.Values.OrderBy(s => s.Signature, StringComparer.Ordinal).Select(s => $"{s.Signature} {s.Records}/{s.Times}/{s.Bytes}/{s.Largest}"));
        const string wantSubs = "BFCB 1/1/17/17, BFCE 1/1/0/0, DATA 1/1/24/24, EDID 1/1/10/10, NAME 3/3/12/4, XLKR 1/2/16/8, XLMS 1/1/12/12, XLYR 1/2/8/4, XPDO 1/1/8/8";
        Check(Subs() == wantSubs, $"REFR's subrecords: {Subs()}; written: {wantSubs}");
        string Links() => string.Join(", ", t.Links.Where(l => l.Links > 0).OrderBy(l => l.Place.Path, StringComparer.Ordinal).Select(l => $"{l.Place.Path} {l.Links}/{l.Rows}/{l.DistinctTargets}/{l.Records}"));
        const string wantLinks = "Base 2/2/1/2, Layer 1/1/1/1, LayeredMaterialSwaps[*] 3/2/2/1, LinkedReferences[*].KeywordOrReference 1/1/1/1, LinkedReferences[*].Reference 2/2/2/1, ProjectedDecalReferences[*] 1/1/1/1";
        Check(Links() == wantLinks, $"REFR's links: {Links()}; written: {wantLinks}");
        // Every link field is read from the bytes (GAPS S92). The component's BFCB text ("BGSSomeComponent") names no kind:
        // since Mutagen 0.55.0-fixes.20 (C63) such a component is an UnknownComponent that keeps the text as its TypeName
        // and its subrecords as Data, so the walk follows it and leaves no field; its XLYR stays the component's (Layer 1)
        Check(t.LinksLeft.Count == 0, "link fields left: " + string.Join(", ", t.LinksLeft.Take(5).Select(l => $"{l.Path} ({l.Why})")));
        Check(a.Notes.Count == 0, "notes on the made plugin: " + string.Join(" ", a.Notes));
        // The component as Mutagen reads it: an UnknownComponent named by its text
        var record = Mutagen.Bethesda.Plugins.Binary.MajorRecordReader.FromPath(path, Mutagen.Bethesda.GameRelease.Starfield)
            .Read(ProfileOrderTests.Record("REFR", 0x801, false, written[1].Item3), 0);
        var component = (record as IPlacedObjectGetter)?.Components.SingleOrDefault();
        Check(component is IUnknownComponentGetter { TypeName: "BGSSomeComponent" }, $"000801's component as Mutagen reads it: {component?.GetType().Name ?? "none"} {(component as IUnknownComponentGetter)?.TypeName}");
        return "made plugin: records, sizes, 9 subrecords and 6 link fields as written (a compressed record, a component's XLYR left out, null links left out; a component of no kind is Mutagen's UnknownComponent \"BGSSomeComponent\", no field left)";
    }

    /// <summary>
    /// sfta01.esm and SFBGS003.esm, their placed objects, cells and navmeshes: records, bytes and every subrecord against
    /// a walk of the files written here; every link field read from the bytes against Mutagen (profile's link census:
    /// links that aren't null and distinct targets).
    /// </summary>
    static string Real(Ctx ctx)
    {
        string[] plugins = { "sfta01.esm", "SFBGS003.esm" };
        var parts = new List<string>();
        foreach (var (type, name) in new[] { ("REFR", "PlacedObject"), ("CELL", "Cell"), ("NAVM", "NavigationMesh") })
        {
            var a = new TypeStatsReport(ctx.Session).Stats(new SubrecordsOptions { Signatures = "*", In = string.Join(",", plugins), Type = type });
            ctx.WriteReport($"profile-stats-{type}.txt", a.ToText(new ReportTextOptions { Detail = true }));
            var t = a.Types.Single();
            // The bytes, walked here
            long records = 0, file = 0, read = 0;
            var subs = new Dictionary<string, (long Records, long Times, long Bytes)>();
            foreach (var p in plugins)
                foreach (var (stored, body) in RecordsOf(Path.Combine(ctx.Data, p), type))
                {
                    records++; file += 24 + stored; read += body.Length;
                    var seen = new HashSet<string>();
                    for (int q = 0; q + 6 <= body.Length;)
                    {
                        string sig = Encoding.Latin1.GetString(body, q, 4); int n = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(q + 4)); q += 6;
                        if (sig == "XXXX") { int big = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(q)); q += n; sig = Encoding.Latin1.GetString(body, q, 4); q += 6; n = big; }
                        var h = subs.GetValueOrDefault(sig);
                        subs[sig] = (h.Records + (seen.Add(sig) ? 1 : 0), h.Times + 1, h.Bytes + n);
                        q += n;
                    }
                }
            Check(t.Records == records && t.FileBytes == file && t.ReadBytes == read, $"{type}: {t.Records} records, {t.FileBytes} / {t.ReadBytes} bytes; the files: {records}, {file} / {read}");
            string S(IEnumerable<(string, long, long, long)> x) => string.Join(", ", x.OrderBy(v => v.Item1, StringComparer.Ordinal).Select(v => $"{v.Item1} {v.Item2}/{v.Item3}/{v.Item4}"));
            string got = S(t.Subrecords.Values.Select(s => (s.Signature, s.Records, s.Times, s.Bytes))), want = S(subs.Select(kv => (kv.Key, kv.Value.Records, kv.Value.Times, kv.Value.Bytes)));
            Check(got == want, $"{type}'s subrecords: {got}\nthe files: {want}");
            // Every link field read from the bytes (at its fixed place, or by the byte walk: GAPS S92), against Mutagen's own
            // object, each field walked along its path; none left
            var (placed, links) = LinkCensusTests.Hold(ctx.Session, string.Join(",", plugins), type, new List<string>());
            parts.Add($"{type} {records} records, {subs.Count} subrecords, {placed} link fields ({links} links) as Mutagen");
        }
        return string.Join(", ", parts);
    }

    /// <summary>Every record of a type in a plugin: its stored size and its data, inflated.</summary>
    static IEnumerable<(long Stored, byte[] Body)> RecordsOf(string path, string type)
    {
        var data = File.ReadAllBytes(path);
        var list = new List<(long, byte[])>();
        void Walk(long p, long end, bool top)
        {
            while (p + 24 <= end)
            {
                string sig = Encoding.ASCII.GetString(data, (int)p, 4); uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)p + 4));
                if (sig == "GRUP") { Walk(p + 24, p + size, false); p += size; continue; }
                if (sig == type && !top)
                {
                    uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)p + 8));
                    byte[] body = data.AsSpan((int)p + 24, (int)size).ToArray();
                    if ((flags & 0x40000) != 0)
                    {
                        var plain = new byte[BinaryPrimitives.ReadUInt32LittleEndian(body)];
                        using (var z = new ZLibStream(new MemoryStream(body, 4, body.Length - 4), CompressionMode.Decompress)) z.ReadExactly(plain);
                        body = plain;
                    }
                    list.Add((size, body));
                }
                p += 24 + size;
            }
        }
        Walk(0, data.Length, true);
        return list;
    }
}
