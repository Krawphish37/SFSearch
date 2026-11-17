// The link census from the files' bytes (GAPS S92, S99) held against Mutagen's: every link field of a record type, read
// here through Mutagen's object (the record layer), each field walked along its schema path the way profile's Mutagen
// walk does, against profile --subrecords "*" (the byte walk), profile <Type.Path> (its bytes route) and profile's census
// from a world link index.
//  - link-census: every record type of Starfield.esm (NAVI's 51 MB map read whole), of the other 32 official plugins
//    read together, and of two mods (kgcdoom.esm, cryomancer_z.esm): for each link field the links that aren't null
//    and the distinct targets, from the bytes and from Mutagen, equal; no link field left but those kept on Mutagen's
//    count for a Mutagen bug (each with its GAPS row). Every difference is listed before the test fails. Heavy: run it
//    alone (heavy.sh, HEAVY_FULL). link-census-mods and link-census-official run one set each.
//  - Census (used by profile-stats and profile-links): the same on small plugins.
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using SFSearch.Index;
using SFSearch.Records;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class LinkCensusTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);

    /// <summary>One link field's values as Mutagen's object holds them.</summary>
    internal sealed class Census
    {
        public long Records, Values, NonZero;
        public readonly HashSet<FormKey> Targets = new();
        /// <summary>Each record's links: entry, target, count (for a world link index made here).</summary>
        public readonly List<(int Plugin, int Entry, FormKey Target)> Links = new();
    }

    /// <summary>Every link field (schema paths) of a record type over plugins, read through Mutagen on all cores.</summary>
    internal static Dictionary<string, Census> Mutagen(ReportSession s, IEnumerable<string> plugins, string type, IReadOnlyList<string> paths, bool keepLinks = false)
    {
        var root = new Node();
        for (int i = 0; i < paths.Count; i++)
        {
            var n = root;
            foreach (var seg in Segments(paths[i])) n = n.Child(seg);
            n.Leaves.Add(i);
        }
        var all = paths.Select(_ => new Census()).ToArray();
        var gate = new object();
        foreach (var name in plugins)
        {
            var p = s.Index.Plugin(name)!;
            var seg = p.Segment!;
            string sig = s.Schema.Model.Objects[type].Sig!;
            var entries = seg.EntriesOf(sig).ToArray();
            if (entries.Length == 0) continue;
            // The plugin opened once, by one caller; then many threads read its records
            s.Engine.Load(new RecordRef(p.Id, entries[0]));
            Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, entries.Length, 2048), new ParallelOptions { MaxDegreeOfParallelism = 8 }, range =>
            {
                var local = paths.Select(_ => new Census()).ToArray();
                for (int k = range.Item1; k < range.Item2; k++)
                {
                    var rec = s.Engine.Load(new RecordRef(p.Id, entries[k]));
                    var seen = new bool[paths.Count];
                    Walk(rec.Record, root, (field, link) =>
                    {
                        var c = local[field];
                        c.Values++;
                        if (!seen[field]) { seen[field] = true; c.Records++; }
                        if (link.IsNull) return;
                        c.NonZero++; c.Targets.Add(link.FormKey);
                        if (keepLinks) c.Links.Add((p.Id, entries[k], link.FormKey));
                    });
                }
                lock (gate)
                    for (int i = 0; i < paths.Count; i++)
                    {
                        all[i].Records += local[i].Records; all[i].Values += local[i].Values; all[i].NonZero += local[i].NonZero;
                        all[i].Targets.UnionWith(local[i].Targets); all[i].Links.AddRange(local[i].Links);
                    }
            });
        }
        return paths.Select((x, i) => (x, i)).ToDictionary(x => x.x, x => all[x.i], StringComparer.Ordinal);
    }

    sealed class Node
    {
        public readonly Dictionary<string, Node> Children = new(StringComparer.Ordinal);
        public readonly List<int> Leaves = new();
        public Node Child(string seg) => Children.TryGetValue(seg, out var n) ? n : Children[seg] = new Node();
    }

    static List<string> Segments(string path)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '{') depth++;
            else if (path[i] == '}') depth--;
            else if (path[i] == '.' && depth == 0) { parts.Add(path[start..i]); start = i + 1; }
        }
        parts.Add(path[start..]);
        return parts;
    }

    static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> Props = new();
    static readonly ConcurrentDictionary<(Type, string), bool> Kinds = new();

    static PropertyInfo? Prop(Type t, string name) => Props.GetOrAdd((t, name), k =>
    {
        foreach (var type in k.Item1.GetInterfaces().Prepend(k.Item1))
        {
            PropertyInfo? p;
            try { p = type.GetProperty(k.Item2, BindingFlags.Public | BindingFlags.Instance); }
            catch (AmbiguousMatchException) { p = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == k.Item2); }
            if (p != null && p.GetIndexParameters().Length == 0 && p.GetMethod != null) return p;
        }
        return null;
    });

    /// <summary>The object is of the kind a path names: its class or getter interface, a generic one without its arity.</summary>
    static bool IsKind(object o, string kind) => Kinds.GetOrAdd((o.GetType(), kind), k =>
    {
        static string Plain(string n) => n.Split('`')[0];
        string n = Plain(k.Item1.Name);
        return n == k.Item2 || n == k.Item2 + "BinaryOverlay" || k.Item1.GetInterfaces().Any(i => Plain(i.Name) == "I" + k.Item2 + "Getter");
    });

    static readonly ConcurrentDictionary<Type, bool> OrIndex = new();

    static bool IsOrIndex(object o) => OrIndex.GetOrAdd(o.GetType(), t => t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IFormLinkOrIndexGetter<>)));

    static void Walk(object obj, Node node, Action<int, IFormLinkGetter> value)
    {
        foreach (var (seg, child) in node.Children)
        {
            string name = seg; string? kind = null;
            int b = name.IndexOf('{');
            if (b >= 0) { kind = name[(b + 1)..name.IndexOf('}')]; name = name[..b]; }
            bool list = name.EndsWith("[*]", StringComparison.Ordinal);
            if (list) name = name[..^3];
            var prop = Prop(obj.GetType(), name);
            if (prop == null) continue;
            object? v = prop.GetValue(obj);
            if (v == null) continue;
            IEnumerable<object?> items = list && v is IEnumerable en && v is not string ? en.Cast<object?>() : new[] { v };
            foreach (var raw in items)
            {
                if (raw == null) continue;
                var item = raw;
                var t = item.GetType();
                if (t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>) || t.GetGenericTypeDefinition() == typeof(Noggog.KeyValue<,>))) item = t.GetProperty("Value")!.GetValue(item)!;
                if (kind != null && !IsKind(item, kind)) continue;
                foreach (int leaf in child.Leaves)
                {
                    if (item is IFormLinkGetter link) value(leaf, link);
                    // A condition's FormLinkOrIndex parameter (GAPS S156): its link when it is one (not an alias or package
                    // data index), as Mutagen's EnumerateFormLinks gives it
                    else if (IsOrIndex(item)) foreach (var l in ((IFormLinkContainerGetter)item).EnumerateFormLinks()) value(leaf, l);
                }
                if (child.Children.Count > 0) Walk(item, child, value);
            }
        }
    }

    /// <summary>profile --subrecords "*" of a type against Mutagen's census of every link field: links that aren't null and
    /// distinct targets equal, none left. Gives the fields and links checked.</summary>
    internal static (int Fields, long Links) Hold(ReportSession s, string plugins, string sig, List<string> lines, bool noneLeft = true)
    {
        var r = HoldAll(s, plugins, sig, lines, noneLeft, null);
        return (r.Fields, r.Links);
    }

    /// <summary>The words a link field's why has when it is kept on Mutagen's count for a Mutagen bug (LinkWalk.cs's HeldBack).</summary>
    const string KeptForMutagen = "kept on Mutagen's count";

    /// <summary>
    /// <see cref="Hold"/>, with each difference added to <paramref name="wrong"/> (when given) instead of failing at the
    /// first: a field whose links or targets differ, a field left (unless kept on Mutagen's count), a record the walk
    /// couldn't follow. Gives the fields, links, fields equal to Mutagen's and fields left.
    /// </summary>
    internal static (int Fields, long Links, int Equal, int Left) HoldAll(ReportSession s, string plugins, string sig, List<string> lines, bool noneLeft, List<string>? wrong)
    {
        void Fail(bool ok, string what) { if (ok) return; if (wrong != null) wrong.Add(what); else Check(false, what); }
        var a = new TypeStatsReport(s).Stats(new SubrecordsOptions { Signatures = "*", In = plugins, Type = sig });
        var t = a.Types.SingleOrDefault();
        if (t == null) return (0, 0, 0, 0);
        foreach (var l in t.LinksLeft) lines.Add($"{plugins} {sig} {l.Path}: left ({l.Why})");
        var unkept = t.LinksLeft.Where(l => !l.Why.Contains(KeptForMutagen, StringComparison.Ordinal)).ToList();
        if (wrong != null) foreach (var l in unkept) wrong.Add($"{sig} {l.Path} in {plugins}: left ({l.Why})");
        else Check(!noneLeft || unkept.Count == 0, $"{sig} in {plugins}: {unkept.Count} link fields left: {string.Join("; ", unkept.Take(6).Select(l => $"{l.Path} ({l.Why})"))}");
        var census = Mutagen(s, plugins.Split(','), t.Name, t.Links.Select(l => l.Place.Path).ToList());
        long links = 0;
        int equal = 0;
        foreach (var l in t.Links)
        {
            var c = census[l.Place.Path];
            bool same = l.Links == c.NonZero && l.DistinctTargets == c.Targets.Count;
            Fail(same, $"{sig} {l.Place.Path} in {plugins}: {l.Links} links to {l.DistinctTargets} records from the bytes ({l.Place.Where}); Mutagen {c.NonZero} to {c.Targets.Count}");
            if (same) equal++;
            links += l.Links;
            if (l.Links > 0) lines.Add($"{plugins} {sig} {l.Place.Path}: {l.Links} links, {l.DistinctTargets} targets ({l.Place.Where}){(same ? " = Mutagen" : $"; Mutagen {c.NonZero} to {c.Targets.Count}")}");
        }
        foreach (var n in a.Notes.Where(n => n.Contains("couldn't follow", StringComparison.Ordinal)))
            Fail(!noneLeft, $"{sig} in {plugins}: {n}");
        return (t.Links.Count, links, equal, t.LinksLeft.Count);
    }

    /// <summary>link-census: see the file's head.</summary>
    public static string Run(Ctx ctx) => RunSets(ctx, "link-census", starfield: true, official: true, mods: true);

    /// <summary>link-census-mods: the two mods' set alone.</summary>
    public static string Mods(Ctx ctx) => RunSets(ctx, "link-census-mods", starfield: false, official: false, mods: true);

    /// <summary>link-census-official: the 32 official plugins after Starfield.esm, read together.</summary>
    public static string Official(Ctx ctx) => RunSets(ctx, "link-census-official", starfield: false, official: true, mods: false);

    static string RunSets(Ctx ctx, string name, bool starfield, bool official, bool mods)
    {
        var s = ctx.Session;
        s.Engine.AllowHeavy = true;
        var lines = new List<string>();
        var table = new List<string> { "| Set | Type | Link fields | Links | = Mutagen | Left |", "|---|---|---|---|---|---|" };
        var parts = new List<string>();
        var wrong = new List<string>();
        var sets = new List<(string Shown, string Plugins)>();
        if (starfield) sets.Add(("Starfield.esm", "Starfield.esm"));
        if (official)
        {
            var others = s.Index.Plugins.Where(p => p.Segment != null && PluginSet.IsOfficial(p.Name) && !p.Name.Equals("Starfield.esm", StringComparison.OrdinalIgnoreCase)).Select(p => p.Name).ToList();
            sets.Add(($"the other {others.Count} official plugins", string.Join(",", others)));
        }
        if (mods) sets.Add(("kgcdoom.esm + cryomancer_z.esm", "kgcdoom.esm,cryomancer_z.esm"));
        try
        {
            foreach (var (shown, plugins) in sets)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var types = plugins.Split(',').SelectMany(m => s.Index.Plugin(m)!.Segment!.Signatures.Select(x => x.Signature)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                int fields = 0, equal = 0, left = 0, held = 0; long all = 0;
                foreach (var sig in types)
                {
                    if (s.Schema.Model.RecordTypes.All(r => r.Sig != sig)) continue;
                    var r = HoldAll(s, plugins, sig, lines, noneLeft: true, wrong);
                    if (r.Fields + r.Left == 0) continue;
                    fields += r.Fields; all += r.Links; equal += r.Equal; left += r.Left; held++;
                    table.Add($"| {shown} | {sig} | {r.Fields + r.Left} | {r.Links:N0} | {r.Equal} | {r.Left} |");
                }
                parts.Add($"{shown}: {held} record types, {fields + left} link fields ({equal} = Mutagen, {left} left), {all:N0} links ({sw.Elapsed.TotalSeconds:0} s)");
                Console.WriteLine("      " + parts[^1]);
            }
        }
        finally { s.Engine.AllowHeavy = false; }
        ctx.WriteReport(name + ".txt", string.Join(Environment.NewLine, wrong.Prepend($"{wrong.Count} differences:").Concat(new[] { "" }).Concat(table).Concat(new[] { "" }).Concat(lines)));
        Check(wrong.Count == 0, $"{wrong.Count} differences from Mutagen's census or fields left: {string.Join(" | ", wrong.Take(30))}");
        return string.Join("; ", parts) + ": every link field from the bytes equals Mutagen's census (links that aren't null, distinct targets); none left but those kept on Mutagen's count";
    }

    /// <summary>
    /// profile <Type.Path> of link fields: from the bytes (the plan says so), its values, null ones, records and census
    /// equal to Mutagen's; and the same fields from a world link index (one made here from Mutagen's links, the postings
    /// a world link file holds) give the same census of links, records with one and no null.
    /// </summary>
    internal static string Profile(Ctx ctx)
    {
        var s = ctx.Session;
        const string plugins = "sfta01.esm,SFBGS003.esm";
        string[] fields =
        {
            "PlacedObject.Base", "PlacedObject.Layer", "PlacedObject.LinkedReferences[*].Reference", "PlacedObject.Lock.Key", "PlacedObject.TimeOfDay",
            "PlacedObject.VolumeData.ImageSpace", "PlacedObject.Traversals[*].Traversal", "PlacedObject.Components[*]{KeywordFormComponent}.Keywords[*]",
            "PlacedObject.VirtualMachineAdapter.Scripts[*].Properties[*]{ScriptObjectProperty}.Object",
        };
        var report = new ProfileReport(s);
        var plan = report.Plan(new ProfileOptions { Fields = fields, In = plugins });
        Check(plan.What.EndsWith("from the files' bytes", StringComparison.Ordinal), "the plan doesn't read the bytes: " + plan.What);
        var a = report.Run(plan, 3);
        ctx.WriteReport("profile-bytes.txt", a.ToText(new ReportTextOptions { Detail = true }));
        Check(a.Read.Any(r => r.Contains("from the files' bytes", StringComparison.Ordinal)), "the answer's read line: " + string.Join("; ", a.Read));
        var census = Mutagen(s, plugins.Split(','), "PlacedObject", fields.Select(f => f["PlacedObject.".Length..]).ToList(), keepLinks: true);
        foreach (var f in a.Fields)
        {
            var c = census[f.Target.Path];
            Check(f.Values == c.Values && f.NonZero == c.NonZero && f.Records == c.Records && (f.Links?.Distinct ?? 0) == c.Targets.Count && (f.Links?.Links ?? 0) == c.NonZero,
                $"{f.Target.Name}: the bytes give {f.Values} values ({f.NonZero} not null) in {f.Records} records, {f.Links?.Distinct ?? 0} targets; Mutagen {c.Values} ({c.NonZero}) in {c.Records}, {c.Targets.Count}");
        }
        // The world link index: a file's postings (target in the plugin's own numbering, record, count), made from Mutagen's links
        var world = new MadeWorld(census.ToDictionary(kv => LinkByteWalker.Unmarked(kv.Key), kv => kv.Value.Links, StringComparer.Ordinal));
        var wplan = report.Plan(new ProfileOptions { Fields = fields.Where(f => f != "PlacedObject.VirtualMachineAdapter.Scripts[*].Properties[*]{ScriptObjectProperty}.Object").ToArray(), In = plugins, World = world });
        Check(wplan.What.EndsWith("from the world link index", StringComparison.Ordinal), "the plan doesn't read the world link index: " + wplan.What);
        var w = report.Run(wplan, 3);
        Check(w.Sources.Any(x => x.Contains("made here", StringComparison.Ordinal)), "[source] doesn't name the world link index: " + string.Join("; ", w.Sources));
        foreach (var f in w.Fields)
        {
            var b = a.Fields.Single(x => x.Target.Name == f.Target.Name);
            Check(f.NonZero == b.NonZero && f.Values == b.NonZero && f.Zero == 0 && (f.Links?.Distinct ?? 0) == (b.Links?.Distinct ?? 0) && (f.Links?.Links ?? 0) == (b.Links?.Links ?? 0)
                && f.Plugins.Sum(p => p.Value.RecordsNonZero) == b.Plugins.Sum(p => p.Value.RecordsNonZero)
                && string.Join(",", f.Links?.Types.Select(t => $"{t.Key}:{t.Value}") ?? Array.Empty<string>()) == string.Join(",", b.Links?.Types.Select(t => $"{t.Key}:{t.Value}") ?? Array.Empty<string>()),
                $"{f.Target.Name}: the world link index gives {f.NonZero} links in {f.Plugins.Sum(p => p.Value.RecordsNonZero)} records, {f.Links?.Distinct} targets; the bytes {b.NonZero} in {b.Plugins.Sum(p => p.Value.RecordsNonZero)}, {b.Links?.Distinct}");
        }
        return $"{a.Fields.Count} link fields from the bytes = Mutagen (values, nulls, records, targets); {w.Fields.Count} from a world link index = the bytes' census";
    }

    /// <summary>
    /// profile-bytes-records (J83, GAPS S105): profile <Type.Path> of link fields of record types outside the world types
    /// that only the walk of a type's own fields reads (inside the runs a hand-read layout cuts, picked by a selector, a
    /// gendered half after its marker): from the bytes (the plan says so), values, null ones, records and targets equal
    /// to Mutagen's. The fields are the ones with the most links of each type in these plugins, so the test follows the
    /// data. And a 2D array's entries through Mutagen (a surface pattern's rotations): numbers, not Noggog's KeyValue.
    /// </summary>
    internal static string ProfileRecords(Ctx ctx)
    {
        var s = ctx.Session;
        const string plugins = "sfta01.esm,SFBGS003.esm,ShatteredSpace.esm";
        var report = new ProfileReport(s);
        var parts = new List<string>();
        foreach (var sig in new[] { "QUST", "PERK", "SCEN", "PACK", "MGEF", "RACE", "TMLM", "INFO", "PNDT" })
        {
            var stats = new TypeStatsReport(s).Stats(new SubrecordsOptions { Signatures = "*", In = plugins, Type = sig }).Types.Single();
            var picked = stats.Links.Where(l => l.Place.Walked && l.Links > 0 && l.Place.By == "the generated layout"
                    && !l.Place.Path.StartsWith("Components[*]", StringComparison.Ordinal) && !l.Place.Path.StartsWith("VirtualMachineAdapter", StringComparison.Ordinal)
                    // a condition's data field profiles as one field per function (its own census), not this one
                    && !l.Place.Path.Contains("{Condition", StringComparison.Ordinal))
                .OrderByDescending(l => l.Links).Take(2).Select(l => l.Place.Path).ToList();
            // A perk's walked fields here are all its effects' conditions: none to pick
            if (picked.Count == 0) continue;
            var fields = picked.Select(f => stats.Name + "." + f).ToArray();
            var plan = report.Plan(new ProfileOptions { Fields = fields, In = plugins });
            Check(plan.What.EndsWith("from the files' bytes", StringComparison.Ordinal), $"{sig}: the plan doesn't read the bytes: {plan.What}");
            var a = report.Run(plan, 3);
            var census = Mutagen(s, plugins.Split(','), stats.Name, picked);
            foreach (var f in a.Fields)
            {
                var c = census[f.Target.Path];
                Check(f.Values == c.Values && f.NonZero == c.NonZero && f.Records == c.Records && (f.Links?.Distinct ?? 0) == c.Targets.Count,
                    $"{f.Target.Name}: the bytes give {f.Values} values ({f.NonZero} not null) in {f.Records} records, {f.Links?.Distinct ?? 0} targets; Mutagen {c.Values} ({c.NonZero}) in {c.Records}, {c.Targets.Count}");
                parts.Add($"{f.Target.Name} {f.NonZero:N0}");
            }
        }
        Check(parts.Count >= 10, $"only {parts.Count} fields picked: {string.Join("; ", parts)}");
        // A 2D array through Mutagen (not a link field): each entry's value
        var rot = report.Run(report.Plan(new ProfileOptions { Fields = new[] { "SurfacePattern.MasterBlockRotations[*]" }, In = "sfta01.esm" }), 3);
        var text = rot.ToText(new ReportTextOptions { Detail = true });
        Check(!text.Contains("KeyValue", StringComparison.Ordinal) && rot.Fields[0].Values == 512, "a 2D array's entries: " + text);
        return $"{parts.Count} fields from the bytes = Mutagen ({string.Join("; ", parts)}); SurfacePattern.MasterBlockRotations[*] 512 numbers through Mutagen";
    }

    /// <summary>A world link index made here: each plugin's postings of a field from Mutagen's links.</summary>
    sealed class MadeWorld(Dictionary<string, List<(int Plugin, int Entry, FormKey Target)>> links) : IWorldLinkIndex
    {
        public bool IsWorld(IndexedPlugin p, string signature) => signature is "REFR" or "CELL" or "NAVM";
        public bool Has(IndexedPlugin p) => true;
        public void Each(IndexedPlugin p, string field, Action<ulong, int, long> each)
        {
            if (!links.TryGetValue(field, out var list)) return;
            var names = p.Segment!.Names;
            foreach (var g in list.Where(x => x.Plugin == p.Id).GroupBy(x => (x.Entry, x.Target)))
            {
                int owner = -1;
                for (int i = 0; i < names.Count; i++) if (string.Equals(names[i], g.Key.Target.ModKey.FileName, StringComparison.OrdinalIgnoreCase)) { owner = i; break; }
                Check(owner >= 0, $"{p.Name}: {g.Key.Target} names no master");
                each((ulong)owner << 24 | g.Key.Target.ID, g.Key.Entry, g.Count());
            }
        }
        public string Source(IReadOnlyCollection<IndexedPlugin> plugins) => $"a world link index made here of {plugins.Count} plugins";
    }
}
