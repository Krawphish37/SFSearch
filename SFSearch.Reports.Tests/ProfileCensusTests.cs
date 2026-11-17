// profile --conditions (GAPS S44): the condition census, held against the plugins' own bytes (every CTDA subrecord,
// counted by the function index it stores), read here on its own; and the census of one function.
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class ProfileCensusTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);

    public static string Conditions(Ctx ctx)
    {
        var parts = new List<string>();
        var report = new ProfileReport(ctx.Session);
        string[] plugins = { "sfta01.esm", "kgcdoom.esm", "sfbgs00b.esm", "SFBGS003.esm" };
        var plan = report.Plan(new ProfileOptions { Conditions = true, In = string.Join(",", plugins) });
        Check(plan.Census && plan.Targets.Any(t => t.Name.StartsWith("Perk.Ranks[*].Effects[*]", StringComparison.Ordinal))
            && plan.Targets.Any(t => t.Name == "ConstructibleObject.Conditions[*]"), "the places a census reads: " + string.Join(", ", plan.Targets.Where(t => t.Type is "Perk" or "ConstructibleObject").Select(t => t.Name)));
        var a = report.Run(plan);
        ctx.WriteReport("profile-conditions.txt", a.ToText(new ReportTextOptions { Detail = true }));

        // 1. Every function's count, against the CTDA subrecords of the four plugins' bytes
        var raw = new SortedDictionary<long, long>();
        foreach (string p in plugins)
            foreach (var (fn, n) in RawCtda(Path.Combine(ctx.Data, p))) raw[fn] = raw.GetValueOrDefault(fn) + n;
        var got = a.Functions.ToDictionary(kv => kv.Key, kv => kv.Value.Conditions);
        string Text(IEnumerable<KeyValuePair<long, long>> d) => string.Join(", ", d.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}×{kv.Value}"));
        Check(Text(got) == Text(raw), $"the census by function: {Text(got)}; the bytes: {Text(raw)}");
        Check(a.CensusConditions == raw.Values.Sum(), $"{a.CensusConditions} conditions counted, {raw.Values.Sum()} CTDA subrecords");
        // Per plugin too
        foreach (string p in plugins)
        {
            long want = RawCtda(Path.Combine(ctx.Data, p)).Values.Sum();
            long have = a.Functions.Values.Sum(f => f.Plugins.GetValueOrDefault(p));
            Check(have == want, $"{p}: {have} conditions counted, {want} CTDA subrecords");
        }
        parts.Add($"{a.CensusConditions:N0} conditions, {a.Functions.Count} functions, equal to the bytes");

        // 2. One function, by its index and by its name: the same numbers as in the census of every one
        var one = report.Run(report.Plan(new ProfileOptions { Conditions = true, Function = "934", In = string.Join(",", plugins) }));
        var byName = report.Run(report.Plan(new ProfileOptions { Conditions = true, Function = one.Functions.Values.Single().Name, In = string.Join(",", plugins) }));
        Check(one.Functions.Count == 1 && one.Functions[934].Conditions == raw[934] && byName.Functions.Single().Value.Conditions == raw[934]
            && one.Functions[934].Samples.Count > 0 && one.Functions[934].Samples.All(s => s.Number == 934),
            $"function 934: {string.Join(", ", one.Functions.Values.Select(f => $"{f.Index} {f.Name} ×{f.Conditions}"))}; the bytes ×{raw.GetValueOrDefault(934)}");
        string text = one.ToText(new ReportTextOptions());
        Check(text.Contains($"## 934 {one.Functions[934].Name}", StringComparison.Ordinal) && text.Contains("By plugin:", StringComparison.Ordinal), "one function's text: " + text[..Math.Min(400, text.Length)]);
        parts.Add($"function 934 ({one.Functions[934].Name}) ×{raw[934]}, by index and by name");

        // 3. Wrong calls
        Fails(() => report.Plan(new ProfileOptions { Conditions = true, Function = "GetGameplayOptionCurentValue", In = "sfta01.esm" }), "no condition function named", "GetGameplayOptionCurrentValue");
        Fails(() => report.Plan(new ProfileOptions { Conditions = true, Fields = new[] { "Weapon.AttackDamage" }, In = "sfta01.esm" }), "conditions was given with fields", null);
        Fails(() => report.Plan(new ProfileOptions { Function = "961", Fields = new[] { "Weapon.AttackDamage" }, In = "sfta01.esm" }), "function was given without conditions", "--conditions");
        Fails(() => report.Plan(new ProfileOptions { Conditions = true, Type = "TextureSet", In = "sfta01.esm" }), "can't hold a condition", null);
        parts.Add("wrong calls");

        // 4. The verb's JSON
        if (ctx.CanRunToday)
        {
            string json = ctx.Today("profile__conditions", "--json", "profile", "--conditions", "--in", string.Join(",", plugins))!;
            var o = JsonNode.Parse(json)!["data"]!;
            Check(o["conditions"]!.GetValue<long>() == raw.Values.Sum() && o["functions"]!.AsArray().Count == raw.Count, "the verb's JSON: " + json[..Math.Min(300, json.Length)]);
            parts.Add("verb JSON");
        }
        return string.Join("; ", parts);
    }

    static void Fails(Action run, string message, string? fix)
    {
        try { run(); }
        catch (ReportException e)
        {
            Check(e.Message.Contains(message, StringComparison.Ordinal) && (fix == null || (e.Fix ?? "").Contains(fix, StringComparison.Ordinal)), $"the error \"{e.Line}\" should hold \"{message}\"{(fix != null ? $" and a fix with \"{fix}\"" : "")}");
            return;
        }
        throw new TestFailed($"no error where one was due: \"{message}\"");
    }

    /// <summary>Every CTDA subrecord in a plugin's bytes, by the function index it stores (bytes 8-9 of its data).</summary>
    public static SortedDictionary<long, long> RawCtda(string path)
    {
        var data = File.ReadAllBytes(path);
        var hits = new SortedDictionary<long, long>();
        void Subs(ReadOnlySpan<byte> body)
        {
            int p = 0; uint? big = null;
            while (p + 6 <= body.Length)
            {
                var sig = body.Slice(p, 4); int n = BinaryPrimitives.ReadUInt16LittleEndian(body[(p + 4)..]); p += 6;
                if (sig.SequenceEqual("XXXX"u8)) { big = BinaryPrimitives.ReadUInt32LittleEndian(body[p..]); p += n; continue; }
                long len = big ?? (uint)n; big = null;
                if (sig.SequenceEqual("CTDA"u8) && len >= 10)
                {
                    long fn = BinaryPrimitives.ReadUInt16LittleEndian(body[(p + 8)..]);
                    hits[fn] = hits.GetValueOrDefault(fn) + 1;
                }
                p += (int)len;
            }
        }
        void Walk(long p, long end)
        {
            while (p < end)
            {
                var sig = data.AsSpan((int)p, 4); uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)p + 4));
                if (sig.SequenceEqual("GRUP"u8)) { Walk(p + 24, p + size); p += size; continue; }
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)p + 8));
                var body = data.AsSpan((int)p + 24, (int)size);
                if ((flags & 0x40000) != 0)
                {
                    var buf = new byte[BinaryPrimitives.ReadInt32LittleEndian(body)];
                    using (var z = new ZLibStream(new MemoryStream(body[4..].ToArray()), CompressionMode.Decompress)) z.ReadExactly(buf);
                    Subs(buf);
                }
                else Subs(body);
                p += 24 + size;
            }
        }
        Walk(0, data.Length);
        return hits;
    }
}
