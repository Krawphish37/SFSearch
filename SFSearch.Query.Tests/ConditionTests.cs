// The Conditions view (GAPS S44): every condition of the records read, one row each. Held against two things that
// don't go through the view's JSON: the plugin's own bytes (every CTDA subrecord, counted by the function index it
// stores) and Mutagen's objects (each condition at its place, its function, comparison, value and parameters).
using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class ConditionTests
{
    static readonly string[] Plugins = { "sfta01.esm", "kgcdoom.esm", "sfbgs00b.esm", "SFBGS003.esm" };

    public static string Run(Ctx c)
    {
        var said = new List<string>();
        foreach (string plugin in Plugins)
        {
            var e = Fresh(c, "sql-cond-" + plugin.Replace('.', '-'));
            var a = Must(e.Run(new QueryRequest
            {
                Sql = "SELECT FormKey, Type, Path, Field, Idx, Function, FunctionName, Comparison, Value, Global, RunOn, Reference, Param1, Param2, Param3, Flags FROM Conditions ORDER BY FormKey, Path",
                Plugins = new[] { plugin }, PageRows = 1_000_000, AllowHeavy = true,
            }), "Conditions of " + plugin);
            Check(a.Notes.Any(n => n.Contains("every record type that can hold a condition", StringComparison.Ordinal)), $"{plugin}: the reads are said: {string.Join(" / ", a.Notes)}");

            // 1. The bytes: every CTDA subrecord of the plugin, by its function index
            var raw = RawCtda(Path.Combine(c.Data, plugin));
            var byFn = a.Rows.GroupBy(r => Convert.ToInt64(r[5], CultureInfo.InvariantCulture)).ToDictionary(g => g.Key, g => (long)g.Count());
            Same($"{plugin}: conditions by function, the view against the plugin's CTDA subrecords",
                byFn.Select(kv => $"{kv.Key} ×{kv.Value}").ToList(), raw.Select(kv => $"{kv.Key} ×{kv.Value}").ToList(), ordered: false);

            // 2. Mutagen: each list the view found, walked on Mutagen's objects of that type
            var mod = c.Mod(plugin);
            var places = a.Rows.Select(r => ((string)r[1]!, (string)r[3]!)).Distinct().ToList();
            var want = new List<string>();
            var byType = mod.EnumerateMajorRecords().GroupBy(Ctx.Type).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var (type, field) in places)
                foreach (var rec in byType.GetValueOrDefault(type) ?? new())
                    foreach (var (path, cond) in Walk(rec, field))
                        want.Add(MutagenRow(c.Schema, rec, type, path, field, cond));
            var got = a.Rows.Select(r => string.Join(" | ", r[0], r[1], r[2], r[3], r[4], r[5], r[6], r[7], Cell(r[8]), r[9], r[10], r[11], Param(r[12]), Param(r[13]), r[14], r[15])).ToList();
            // The first condition that differs, both sides in full
            var wantByPlace = want.GroupBy(w => string.Join(" | ", w.Split(" | ").Take(3)), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var g in got)
                if (wantByPlace.TryGetValue(string.Join(" | ", g.Split(" | ").Take(3)), out var w) && !w.Equals(g, StringComparison.OrdinalIgnoreCase))
                    throw new TestFailed($"{plugin}: a condition differs. View: [{g}]  Mutagen: [{w}]");
            // FormKeys compare without regard to case (a plugin may write its master's name in lower case)
            Same($"{plugin}: the view's rows against Mutagen's conditions", got.Select(x => x.ToLowerInvariant()).ToList(), want.Select(x => x.ToLowerInvariant()).ToList(), ordered: false);
            said.Add($"{plugin} {a.Rows.Count:N0} conditions, {byFn.Count} functions");
        }

        // 3. Narrowed by type, only that type is read; the versions scope has LoadOrder and IsWinner
        var n = Fresh(c, "sql-cond-narrow");
        var perk = Must(n.Run(new QueryRequest { Sql = "SELECT count(*) FROM Conditions WHERE Type = 'Perk'", Plugins = new[] { "sfta01.esm" }, PageRows = 100 }), "narrowed");
        Check(perk.Cost.Read.Count == 2 && perk.Cost.Read[0].StartsWith("Perk (", StringComparison.Ordinal) && perk.Cost.Read[1].StartsWith("Conditions (", StringComparison.Ordinal),
            $"narrowed by type, Perk alone is read: {string.Join(" / ", perk.Cost.Read)}");
        long perks = c.Mod("sfta01.esm").Perks.Sum(p => Walk(p, "Ranks[*].Effects[*].Conditions[*].Conditions").Count());
        Check(Convert.ToInt64(perk.Rows[0][0], CultureInfo.InvariantCulture) == perks, $"sfta01.esm's perks: {perk.Rows[0][0]} conditions, Mutagen {perks}");
        var cols = c.Engine.ColumnsOf("Conditions", ScopeKind.Versions)!;
        Check(cols.Contains("LoadOrder") && cols.Contains("IsWinner") && !c.Engine.ColumnsOf("Conditions", ScopeKind.Plugins)!.Contains("IsWinner"), $"the versions scope's columns: {string.Join(", ", cols)}");
        said.Add($"WHERE Type = 'Perk' reads Perk alone ({perks})");

        // 4. With some types read and the rest not, a statement that narrows to nothing reads the rest (GAPS S208: the
        //    answer doesn't depend on what an earlier statement read): the count is a fresh engine's
        var note = Must(n.Run(new QueryRequest { Sql = "SELECT count(*) FROM Conditions", Plugins = new[] { "sfta01.esm" }, PageRows = 100 }), "unnarrowed");
        var freshAll = Must(Fresh(c, "sql-cond-all").Run(new QueryRequest { Sql = "SELECT count(*) FROM Conditions", Plugins = new[] { "sfta01.esm" }, PageRows = 100 }), "unnarrowed, fresh");
        Check(note.Notes.Any(x => x.StartsWith("Read first, because Conditions holds only the record types read", StringComparison.Ordinal))
            && Convert.ToInt64(note.Rows[0][0], CultureInfo.InvariantCulture) == Convert.ToInt64(freshAll.Rows[0][0], CultureInfo.InvariantCulture)
            && Convert.ToInt64(note.Rows[0][0], CultureInfo.InvariantCulture) > perks,
            $"after Perk alone was read, an unnarrowed count reads the rest: {note.Rows[0][0]} against a fresh engine's {freshAll.Rows[0][0]}; {string.Join(" / ", note.Notes)}");
        said.Add("the rest is read after one type was");
        return string.Join("; ", said);
    }

    /// <summary>An engine on scratch tables of its own, empty.</summary>
    static QueryEngine Fresh(Ctx c, string folder)
    {
        string path = Path.Combine(c.Work, folder);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        return c.NewEngine(folder);
    }

    static string Param(object? v) => v is double d ? ((float)d).ToString("R", CultureInfo.InvariantCulture) : Cell(v);

    /// <summary>One condition as Mutagen has it, as the same text as a row of the view.</summary>
    static List<string> ParamNames(SFSearch.Records.RecordSchema schema, string cls) =>
        schema.Model.Shallow(cls).Select(r => r.Def).Where(d => d.Object == cls && !d.Name.Contains("Unused", StringComparison.Ordinal)).OrderBy(d => d.Ord).Select(d => d.Name).ToList();

    static string MutagenRow(SFSearch.Records.RecordSchema schema, IMajorRecordGetter rec, string type, string path, string field, IConditionGetter cond)
    {
        var data = cond.Data;
        long fn = Convert.ToInt64(data.Function, CultureInfo.InvariantCulture);
        string name = Enum.IsDefined(data.Function) ? data.Function.ToString() : fn.ToString(CultureInfo.InvariantCulture);
        int idx = int.Parse(path[(path.LastIndexOf('[') + 1)..^1], CultureInfo.InvariantCulture);
        string value = cond is IConditionFloatGetter f ? Fmt(f.ComparisonValue) : "";
        string global = cond is IConditionGlobalGetter g && !g.ComparisonValue.IsNull ? g.ComparisonValue.FormKey.ToString() : "";
        string reference = data.Reference.IsNull ? "" : data.Reference.FormKey.ToString();
        // The parameters by the view's rule (Conditions.cs): the data class's own fields that Mutagen doesn't name
        // unused, in file order (VoiceTypeOrList; GetVMQuestVariable's string SecondParameter), read off Mutagen's object
        var names = ParamNames(schema, data.GetType().Name.Replace("BinaryOverlay", "", StringComparison.Ordinal));
        string p1 = names.Count > 0 ? Par(Prop(data, names[0])) : "", p2 = names.Count > 1 ? Par(Prop(data, names[1])) : "";
        string flags = "[" + string.Join(",", Enum.GetValues(cond.Flags.GetType()).Cast<Enum>().Where(x => Convert.ToInt64(x, CultureInfo.InvariantCulture) != 0 && cond.Flags.HasFlag(x)).Select(x => $"\"{x}\"")) + "]";
        return string.Join(" | ", rec.FormKey, type, path, field, idx, fn, name, cond.CompareOperator, value, global, data.RunOnType, reference, p1, p2, data.ParameterThree, flags);
    }

    static object? Prop(object o, string name) => o.GetType().GetInterfaces().Prepend(o.GetType()).Select(t => t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)).FirstOrDefault(p => p != null)?.GetValue(o);

    /// <summary>A parameter as the view gives it: a link's FormKey (or nothing for a null one), an enum's name, a number.</summary>
    static string Par(object? v) => v switch
    {
        IFormLinkGetter l => l.IsNull ? "" : l.FormKey.ToString(),
        // A FormLinkOrIndex: its link when it is one, else its index (never null since fixes.20, C68)
        not null when v.GetType().GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition().Name.StartsWith("IFormLinkOrIndexGetter", StringComparison.Ordinal))
            => UsesLink(v) ? (Prop(v, "Link") is IFormLinkGetter { IsNull: false } l2 ? l2.FormKey.ToString() : "") : Fmt(Prop(v, "Index")),
        float fl => fl.ToString("R", CultureInfo.InvariantCulture),
        _ => Fmt(v),
    };

    static bool UsesLink(object v) => v.GetType().GetInterfaces().Prepend(v.GetType()).Select(t => t.GetMethod("UsesLink", System.Type.EmptyTypes)).FirstOrDefault(m => m != null)?.Invoke(v, null) is true;

    /// <summary>The conditions of a record along a field of the view (Ranks[*].Effects[*].Conditions), each with its place.</summary>
    static IEnumerable<(string Path, IConditionGetter Cond)> Walk(object rec, string field)
    {
        var steps = field.Split('.');
        IEnumerable<(object Obj, string Path)> at = new[] { (rec, "") };
        for (int s = 0; s < steps.Length; s++)
        {
            bool list = steps[s].EndsWith("[*]", StringComparison.Ordinal) || s == steps.Length - 1;
            string name = steps[s].Replace("[*]", "");
            var next = new List<(object, string)>();
            foreach (var (o, p) in at)
            {
                if (Prop(o, name) is not { } v) continue;
                string here = p.Length == 0 ? name : p + "." + name;
                if (list && v is IEnumerable e and not string)
                {
                    int i = 0;
                    foreach (var item in e) { if (item != null) next.Add((item, $"{here}[{i}]")); i++; }
                }
                else next.Add((v, here));
            }
            at = next;
        }
        return at.Where(x => x.Obj is IConditionGetter).Select(x => (x.Path, (IConditionGetter)x.Obj));
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
                    int inflated = BinaryPrimitives.ReadInt32LittleEndian(body);
                    var buf = new byte[inflated];
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
