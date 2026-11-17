// profile --subrecords <signatures> --order (GAPS S87): the orders a record type's subrecords come in. Held against a
// plugin whose records are written here byte by byte (every order known), and against a walk of the official plugins'
// packages written here (C38's question: where UNAM, BNAM and PNAM stand after XNAM and after the procedure tree).
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Starfield;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class ProfileOrderTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    const string Plugin = "sfs_j61_order.esm";

    public static string Order(Ctx ctx)
    {
        var parts = new List<string> { Made(ctx), Official(ctx) };
        // Wrong calls
        try { new SubrecordsReport(ctx.Session).Census(new SubrecordsOptions { Signatures = "*", In = "sfta01.esm" }); throw new TestFailed("* was counted as a signature"); }
        catch (ReportException e) { Check(e.Message.Contains("is a question of its own", StringComparison.Ordinal), e.Line); }
        try { new SubrecordOrderReport(ctx.Session).Orders(new SubrecordsOptions { Signatures = "*,UNAM", In = "sfta01.esm" }); throw new TestFailed("* was taken with a signature"); }
        catch (ReportException e) { Check(e.Message.Contains("was given with signatures", StringComparison.Ordinal), e.Line); }
        try { new SubrecordOrderReport(ctx.Session).Orders(new SubrecordsOptions { Signatures = "UNAM", In = "sfta01.esm", Type = "Nothing" }); throw new TestFailed("a type named Nothing was taken"); }
        catch (ReportException e) { Check(e.Message.Contains("no record type named", StringComparison.Ordinal), e.Line); }
        parts.Add("3 wrong calls refused");
        if (ctx.CanRunToday)
        {
            string json = ctx.Today("profile__subrecords__order", "--json", "profile", "--subrecords", "UNAM,BNAM,PNAM", "--order", "--type", "Package", "--in", "sfta01.esm")!;
            var types = JsonNode.Parse(json)!["data"]!["types"]!.AsArray();
            Check(types.Count == 1 && (string?)types[0]!["recordType"] == "PACK" && types[0]!["orders"]!.AsArray().Count > 0, "the verb's JSON: " + json[..Math.Min(400, json.Length)]);
            string none = ctx.Today("profile__order__alone", "profile", "--order", "--type", "PACK")!;
            Check(none.Contains("no --subrecords was given", StringComparison.Ordinal), "--order alone: " + none);
            parts.Add("verb JSON, --order alone refused");
        }
        return string.Join("; ", parts);
    }

    // ------------------------------------------------------------------ a plugin written byte by byte
    /// <summary>
    /// A Data folder made for the test: one plugin (an empty header by Mutagen, then top groups written here), whose
    /// records' subrecords come in known orders. PACK: 000800 UNAM×3 XNAM; 000801 UNAM XNAM (one alone: the same order);
    /// 000802 two index UNAMs, two branches (ANAM PNAM FNAM), two names entries (UNAM BNAM PNAM); 000803 compressed, one
    /// of each (the same order); 000804 a 70,000-byte DATA (sized by XXXX) that holds the text "UNAM", then XNAM.
    /// QUST: 000806 CTDA CTDA PNAM, CTDA PNAM, CTDA CTDA CTDA PNAM (repeats inside a repeat); 000807 CTDA PNAM once.
    /// KYWD: 000805, an EditorID alone.
    /// </summary>
    static string Made(Ctx ctx)
    {
        string data = ctx.Folder("order-data");
        string path = Path.Combine(data, Plugin);
        ctx.MustBeInWork(path);
        new StarfieldMod(Mutagen.Bethesda.Plugins.ModKey.FromFileName(Plugin), StarfieldRelease.Starfield).WriteToBinary(path);
        static (string, byte[]) S(string sig, int size = 4) => (sig, new byte[size]);
        static (string, byte[]) Edid(string name) => ("EDID", Encoding.ASCII.GetBytes(name + "\0"));
        (string, byte[])[] U(int n) => Enumerable.Repeat(S("UNAM"), n).ToArray();
        (string, byte[])[] Branch() => new[] { S("ANAM"), S("PNAM"), S("FNAM") };
        (string, byte[])[] Entry() => new[] { S("UNAM"), S("BNAM"), S("PNAM") };
        var big = new byte[70000];
        Encoding.ASCII.GetBytes("UNAM").CopyTo(big, 1000);
        var pack = new List<byte[]>
        {
            Record("PACK", 0x800, false, new[] { Edid("sfsOrder1") }.Concat(U(3)).Append(S("XNAM")).Append(S("POBA"))),
            Record("PACK", 0x801, false, new[] { Edid("sfsOrder2") }.Concat(U(1)).Append(S("XNAM")).Append(S("POBA"))),
            Record("PACK", 0x802, false, new[] { Edid("sfsOrder3") }.Concat(U(2)).Append(S("XNAM")).Concat(Branch()).Concat(Branch()).Concat(Entry()).Concat(Entry()).Append(S("POBA"))),
            Record("PACK", 0x803, true, new[] { Edid("sfsOrder4") }.Concat(U(1)).Append(S("XNAM")).Concat(Branch()).Concat(Entry()).Append(S("POBA"))),
            Record("PACK", 0x804, false, new[] { Edid("sfsOrder5"), ("DATA", big), S("XNAM"), S("POBA") }),
        };
        var qust = new List<byte[]>
        {
            Record("QUST", 0x806, false, new[] { Edid("sfsOrderQ1"), S("CTDA"), S("CTDA"), S("PNAM"), S("CTDA"), S("PNAM"), S("CTDA"), S("CTDA"), S("CTDA"), S("PNAM") }),
            Record("QUST", 0x807, false, new[] { Edid("sfsOrderQ2"), S("CTDA"), S("PNAM") }),
        };
        var kywd = new List<byte[]> { Record("KYWD", 0x805, false, new[] { Edid("sfsOrderK") }) };
        using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write))
            foreach (var g in new[] { Group("KYWD", kywd), Group("PACK", pack), Group("QUST", qust) }) fs.Write(g);

        using var session = new ReportSession(data, Path.Combine(ctx.Work, "scratch-order"), ctx.Schema, "sfsreport-tests");
        var report = new SubrecordOrderReport(session);
        var parts = new List<string>();

        // PACK, the four names
        var a = report.Orders(new SubrecordsOptions { Signatures = "xnam,UNAM,BNAM,PNAM", In = Plugin, Type = "Package" });
        ctx.WriteReport("profile-order-made.txt", a.ToText(new ReportTextOptions { Detail = true }));
        Check(a.Records == 5 && a.Inflated == 1 && a.Unreadable == 0, $"PACK: {a.Records} records, {a.Inflated} inflated, {a.Unreadable} unreadable");
        var want = new[]
        {
            "… (UNAM)+ XNAM (… PNAM)+ … (UNAM BNAM PNAM)+ … ×2 [(UNAM)+ 1-2/3/2; (… PNAM)+ 1-2/3/2; (UNAM BNAM PNAM)+ 1-2/3/2] 000802",
            "… (UNAM)+ XNAM … ×2 [(UNAM)+ 1-3/4/2] 000800",
            "… XNAM … ×1 [] 000804",
        };
        Check(Orders(a, "PACK") == string.Join("\n", want), $"PACK orders:\n{Orders(a, "PACK")}\nwritten:\n{string.Join("\n", want)}");
        Check(a.Types.Count == 1, "--type Package gave " + string.Join(", ", a.Types.Select(t => t.Sig)));
        parts.Add("PACK: 3 orders of 5 records as written (a compressed record, a lone unit, a 70,000-byte DATA through XXXX)");

        // Every type: KYWD holds none of them and is left out; QUST holds PNAM
        var b = report.Orders(new SubrecordsOptions { Signatures = "XNAM,UNAM,BNAM,PNAM", In = Plugin });
        Check(string.Join(",", b.Types.Select(t => t.Sig)) == "PACK,QUST", "every type: " + string.Join(", ", b.Types.Select(t => t.Sig)));
        Check(Orders(b, "QUST") == "(… PNAM)+ ×2 [(… PNAM)+ 1-3/4/2] 000806", "QUST, the four names: " + Orders(b, "QUST"));

        // Every subrecord: repeats inside a repeat, the lone one written so, counted exactly
        var c = report.Orders(new SubrecordsOptions { Signatures = "*", In = Plugin, Type = "QUST" });
        Check(Orders(c, "QUST") == "EDID ((CTDA)+ PNAM)+ ×2 [((CTDA)+ PNAM)+ 1-3/4/2; (CTDA)+ 1-3/7/4] 000806", "QUST, every subrecord: " + Orders(c, "QUST"));
        var d = report.Orders(new SubrecordsOptions { Signatures = "*", In = Plugin, Type = "KYWD" });
        Check(Orders(d, "KYWD") == "EDID ×1 [] 000805", "KYWD, every subrecord: " + Orders(d, "KYWD"));
        Check(a.Types[0].Orders.Single(g => g.Key.EndsWith("XNAM …", StringComparison.Ordinal) && g.Key.Contains("UNAM", StringComparison.Ordinal)).Sample == $"sfsOrder1 (000800:{Plugin})", "the sample of the first order");
        parts.Add("QUST: ((CTDA)+ PNAM)+ with its inner counts; KYWD left out of a census of every type");
        return "made plugin: " + string.Join(", ", parts);
    }

    /// <summary>A type's orders as text: order ×records [each repeat: least-most/in all/places] the first record's ID.</summary>
    static string Orders(SubrecordOrderAnswer a, string sig) => string.Join("\n", (a.Types.FirstOrDefault(t => t.Sig == sig).Orders ?? new())
        .OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => $"{a.OrderText(g)} ×{g.Records} [{string.Join("; ", OrderStep.Repeats(g.Steps).Select(r => $"{r.Key} {(r.Min == r.Max ? $"{r.Min}" : $"{r.Min}-{r.Max}")}/{r.Total}/{r.Places}"))}] {Id(g.Sample)}"));

    /// <summary>The object ID of a sample, "EditorID (000800:plugin.esm)".</summary>
    static string? Id(string? sample) => sample?.Split(' ')[^1].Trim('(', ')').Split(':')[0];

    internal static byte[] Record(string sig, uint id, bool compressed, IEnumerable<(string Sig, byte[] Data)> subs)
    {
        var body = new MemoryStream();
        foreach (var (s, d) in subs)
        {
            if (d.Length > ushort.MaxValue)
            {
                body.Write(Encoding.ASCII.GetBytes("XXXX")); body.Write(BitConverter.GetBytes((ushort)4)); body.Write(BitConverter.GetBytes((uint)d.Length));
                body.Write(Encoding.ASCII.GetBytes(s)); body.Write(BitConverter.GetBytes((ushort)0));
            }
            else { body.Write(Encoding.ASCII.GetBytes(s)); body.Write(BitConverter.GetBytes((ushort)d.Length)); }
            body.Write(d);
        }
        byte[] data = body.ToArray();
        if (compressed)
        {
            var z = new MemoryStream();
            z.Write(BitConverter.GetBytes((uint)data.Length));
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(data);
            data = z.ToArray();
        }
        var r = new byte[24 + data.Length];
        Encoding.ASCII.GetBytes(sig).CopyTo(r, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(8), compressed ? 0x40000u : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(12), id);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(20), 0x23);
        data.CopyTo(r, 24);
        return r;
    }

    internal static byte[] Group(string label, List<byte[]> records)
    {
        int size = 24 + records.Sum(r => r.Length);
        var g = new byte[size];
        Encoding.ASCII.GetBytes("GRUP").CopyTo(g, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(g.AsSpan(4), (uint)size);
        Encoding.ASCII.GetBytes(label).CopyTo(g, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(g.AsSpan(20), 0x23);
        int at = 24;
        foreach (var r in records) { r.CopyTo(g, at); at += r.Length; }
        return g;
    }

    // ------------------------------------------------------------------ C38 on the official plugins
    /// <summary>
    /// C38's question over @official: every package's UNAM, BNAM, PNAM and XNAM. The census is held against a walk of the
    /// plugins' PACK groups written here: each record's own sequence matches exactly one order (as a pattern), the
    /// orders' records add up, and C38's numbers (packages with a procedure tree, names entries after it, names after
    /// XNAM without a tree, the index block's UNAMs) come out the same from both.
    /// </summary>
    static string Official(Ctx ctx)
    {
        var a = new SubrecordOrderReport(ctx.Session).Orders(new SubrecordsOptions { Signatures = "XNAM,UNAM,BNAM,PNAM", In = "@official", Type = "PACK" });
        ctx.WriteReport("profile-order-pack.txt", a.ToText(new ReportTextOptions { Detail = true }));
        var orders = a.Types.Single().Orders;
        var patterns = orders.Select(g => (Order: g, Pattern: new Regex("^" + Pattern(g.Steps) + "$", RegexOptions.CultureInvariant))).ToList();
        var asked = new HashSet<string> { "XNAM", "UNAM", "BNAM", "PNAM" };
        var matched = new Dictionary<SubrecordOrder, long>();
        long records = 0, tree = 0, entries = 0, namesNoTree = 0, index = 0;
        bool entriesAreUbp = true;
        foreach (var p in a.Set.Plugins)
            foreach (var subs in Packages(p.Path))
            {
                records++;
                var t = new StringBuilder();
                bool gap = false;
                foreach (var s in subs)
                    if (asked.Contains(s)) { t.Append(s).Append(' '); gap = false; }
                    else if (!gap) { t.Append("… "); gap = true; }
                var hits = patterns.Where(x => x.Pattern.IsMatch(t.ToString())).ToList();
                Check(hits.Count == 1, $"a package in {p.Name} ({t}) matches {hits.Count} orders");
                matched[hits[0].Order] = matched.GetValueOrDefault(hits[0].Order) + 1;
                // C38's reading: a tree when ANAM follows XNAM; the names after it; the index block's UNAMs before XNAM
                int x = subs.IndexOf("XNAM");
                bool hasTree = x >= 0 && x + 1 < subs.Count && subs[x + 1] == "ANAM";
                if (hasTree) tree++;
                index += subs.Take(Math.Max(0, x)).Count(s => s == "UNAM");
                for (int i = x + 1; i < subs.Count; i++)
                    if (subs[i] == "UNAM")
                    {
                        if (hasTree) entries++; else namesNoTree++;
                        entriesAreUbp &= i + 2 < subs.Count && subs[i + 1] == "BNAM" && subs[i + 2] == "PNAM";
                    }
            }
        foreach (var g in orders) Check(matched.GetValueOrDefault(g) == g.Records, $"{g.Key}: {g.Records} records in the census, {matched.GetValueOrDefault(g)} in the files");
        Check(records == a.Records, $"{a.Records} packages in the census, {records} in the files");
        // The same numbers from the census: the order of each, before and after XNAM
        long cTree = 0, cEntries = 0, cNamesNoTree = 0, cIndex = 0;
        foreach (var g in orders)
        {
            int x = g.Steps.FindIndex(s => s.Key == "XNAM");
            Check(x >= 0, "an order without XNAM: " + g.Key);
            var after = g.Steps.Skip(x + 1).ToList();
            bool hasTree = Count(after, "PNAM", g.Records) > Count(after, "BNAM", g.Records);
            if (hasTree) { cTree += g.Records; cEntries += Count(after, "BNAM", g.Records); }
            else cNamesNoTree += Count(after, "UNAM", g.Records);
            cIndex += Count(g.Steps.Take(x), "UNAM", g.Records);
        }
        Check((cTree, cEntries, cNamesNoTree, cIndex) == (tree, entries, namesNoTree, index) && entriesAreUbp,
            $"census: {cTree} with a tree, {cEntries} names entries, {cNamesNoTree} names after XNAM without a tree, {cIndex} index UNAMs; the files: {tree}, {entries}, {namesNoTree}, {index}; every entry UNAM BNAM PNAM: {entriesAreUbp}");
        return $"@official PACK: {records} packages in {orders.Count} orders, each as its bytes; {tree} with a tree ({entries} names entries, all UNAM BNAM PNAM), {records - tree} without ({namesNoTree} names after XNAM), {index} index UNAMs (C38: 4,902, 196, 3,575, 4,706, 0, 44,141)";
    }

    /// <summary>An order as a pattern over a record's steps, each followed by a space.</summary>
    static string Pattern(IEnumerable<OrderStep> steps) => string.Concat(steps.Select(s => s.IsRepeat ? $"(?:{Pattern(s.Unit!)})+" : Regex.Escape(s.Key) + " "));

    /// <summary>How many times a signature stands in steps that stand for <paramref name="places"/> places.</summary>
    static long Count(IEnumerable<OrderStep> steps, string sig, long places) =>
        steps.Sum(s => s.IsRepeat ? Count(s.Unit!, sig, s.Total) : s.Key == sig ? places : 0);

    /// <summary>The subrecords of every PACK of a plugin, its PACK group found by skipping the other top groups.</summary>
    static IEnumerable<List<string>> Packages(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var h = new byte[24];
        fs.ReadExactly(h);
        fs.Position = 24 + BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
        while (fs.Position + 24 <= fs.Length)
        {
            long start = fs.Position;
            fs.ReadExactly(h);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
            if (Encoding.ASCII.GetString(h, 8, 4) != "PACK") { fs.Position = start + size; continue; }
            var g = new byte[size - 24];
            fs.ReadExactly(g);
            for (int p = 0; p + 24 <= g.Length;)
            {
                uint len = BinaryPrimitives.ReadUInt32LittleEndian(g.AsSpan(p + 4)), flags = BinaryPrimitives.ReadUInt32LittleEndian(g.AsSpan(p + 8));
                byte[] body = g.AsSpan(p + 24, (int)len).ToArray();
                if ((flags & 0x40000) != 0)
                {
                    var plain = new byte[BinaryPrimitives.ReadUInt32LittleEndian(body)];
                    using (var z = new ZLibStream(new MemoryStream(body, 4, body.Length - 4), CompressionMode.Decompress)) z.ReadExactly(plain);
                    body = plain;
                }
                var subs = new List<string>();
                uint? bigSize = null;
                for (int q = 0; q + 6 <= body.Length;)
                {
                    string sig = Encoding.ASCII.GetString(body, q, 4); int n = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(q + 4)); q += 6;
                    if (sig == "XXXX") { bigSize = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(q)); q += n; continue; }
                    q += (int)(bigSize ?? (uint)n); bigSize = null;
                    subs.Add(sig);
                }
                yield return subs;
                p += 24 + (int)len;
            }
        }
    }
}
