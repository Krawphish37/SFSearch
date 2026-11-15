// The two things the link checks stand on, each held against Mutagen itself:
//  - the rule "may a field with this link target point at a record of this type" (the schema's link targets,
//    interfaces included) against Mutagen's own types: is the link's getter type assignable from the record's;
//  - the walk that finds a record's links in its JSON against Mutagen's EnumerateFormLinks on the same record.
using System.Globalization;
using System.Text;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SFSearch.Index;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class LinkTests
{
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    static Type? Getter(string name)
    {
        var asm = typeof(IWeaponGetter).Assembly;
        return asm.GetType($"Mutagen.Bethesda.Starfield.I{name}Getter", false) ?? asm.GetType($"Mutagen.Bethesda.Starfield.{name}Getter", false)
            ?? (name is "MajorRecord" or "IMajorRecord" ? typeof(IMajorRecordGetter) : null);
    }

    /// <summary>Every link target the schema's fields name, with every record type: the schema's answer and Mutagen's.</summary>
    public static string Rules(Ctx ctx)
    {
        var s = ctx.Session;
        var model = s.Schema.Model;
        var targets = model.Objects.Values.SelectMany(o => o.Fields).Where(f => f.LinkTo != null).Select(f => f.LinkTo!).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        int pairs = 0, allowed = 0, interfaces = 0;
        var wrong = new List<string>(); var unknown = new List<string>(); var notInMutagen = new List<string>();
        foreach (string linkTo in targets)
        {
            if (!s.Links.Knows(linkTo)) unknown.Add(linkTo);
            if (s.Links.Interfaces.ContainsKey(linkTo)) interfaces++;
            var link = Getter(linkTo);
            if (link == null) { notInMutagen.Add(linkTo); continue; }
            foreach (var t in model.RecordTypes)
            {
                var record = Getter(t.Name);
                if (record == null) { if (!notInMutagen.Contains(t.Name)) notInMutagen.Add(t.Name); continue; }
                pairs++;
                bool schema = s.Links.Knows(linkTo) && s.Links.AllowsType(linkTo, t.Name);
                bool mutagen = link.IsAssignableFrom(record);
                if (schema) allowed++;
                if (schema != mutagen && s.Links.Knows(linkTo)) wrong.Add($"{linkTo} ← {t.Name}: schema {(schema ? "allows" : "refuses")}, Mutagen {(mutagen ? "allows" : "refuses")}");
            }
        }
        // A link target the schema can't judge must be one that no record type carries in Mutagen either
        var unknownButUsed = new List<string>();
        foreach (string linkTo in unknown)
        {
            var link = Getter(linkTo);
            if (link == null) continue;
            var carriers = model.RecordTypes.Where(t => Getter(t.Name) is { } r && link.IsAssignableFrom(r)).Select(t => t.Name).ToList();
            // "Any record" names are known by the rules themselves
            if (carriers.Count > 0 && carriers.Count < model.RecordTypes.Count) unknownButUsed.Add($"{linkTo} ({string.Join(", ", carriers.Take(5))})");
        }
        var sb = new StringBuilder();
        sb.AppendLine($"{targets.Count} link targets, {model.RecordTypes.Count} record types, {pairs} pairs held against Mutagen; {allowed} allowed.");
        sb.AppendLine("Disagreements:"); foreach (var w in wrong) sb.AppendLine("  " + w);
        sb.AppendLine("Link targets the schema can't judge: " + string.Join(", ", unknown));
        sb.AppendLine("Of those, carried by some record types in Mutagen: " + string.Join("; ", unknownButUsed));
        sb.AppendLine("Names with no Mutagen type: " + string.Join(", ", notInMutagen));
        ctx.WriteReport("link-rules.txt", sb.ToString());
        Ctx.Check(wrong.Count == 0, $"{wrong.Count} of {N(pairs)} pairs differ from Mutagen: {string.Join("; ", wrong.Take(8))}");
        Ctx.Check(notInMutagen.Count == 0, $"no Mutagen type was found for: {string.Join(", ", notInMutagen)}");
        return $"{targets.Count} link targets of the schema ({interfaces} of them link interfaces) × {model.RecordTypes.Count} record types = {N(pairs)} pairs, each held against Mutagen's own types: 0 differ ({N(allowed)} allowed). " +
            $"{unknown.Count} link target(s) the schema can't judge ({string.Join(", ", unknown)}): links there are checked for existing only" +
            (unknownButUsed.Count > 0 ? $"; Mutagen knows carriers for {string.Join("; ", unknownButUsed)}" : "; \"any record\" or carried by no record type") + ".";
    }

    /// <summary>The links the walk finds in a record's JSON, against Mutagen's EnumerateFormLinks on the same record.</summary>
    public static string Walk(Ctx ctx)
    {
        var s = ctx.Session;
        var walker = new LinkWalker(s.Schema);
        string[] plugins = { "atom74.esm", "weap_starlight.esm", "darkstarpatch_crimsonx.esm", "kgcdoom.esm", "sfta04.esm", "MissionsGalore.esm", "darkstar.esm" };
        long records = 0, links = 0, differing = 0, unread = 0, gapRecords = 0, gapLinks = 0, bone = 0;
        var examples = new List<string>();
        var perPlugin = new List<string>();
        string? gapExample = null;
        foreach (string name in plugins)
        {
            var p = s.Index.Plugin(name) ?? throw new TestFailed($"{name} isn't in the Data folder");
            var seg = p.Segment!;
            s.Engine.Sessions.Use(name);
            long r0 = records, l0 = links, d0 = differing, g0 = gapRecords;
            for (int i = 0; i < seg.FirstUnresolved; i++)
            {
                var e = seg[i];
                if (seg.SignatureOf(e) == "NAVI") continue;
                SFSearch.Records.LoadedRecord rec;
                try { rec = s.Engine.Load(new RecordRef(p.Id, i)); }
                catch (SFSearch.Records.RecordException) { unread++; continue; }
                records++;
                var mutagen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var l in rec.Record.EnumerateFormLinks())
                {
                    if (l.FormKey.IsNull) continue;
                    string k = $"{l.FormKey.ID:X6}:{l.FormKey.ModKey.FileName}";
                    mutagen[k] = mutagen.GetValueOrDefault(k) + 1;
                }
                var walked = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var gap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                walker.Walk(rec.Type, s.Engine.Json(rec), l =>
                {
                    string k = ReportSession.Canon(l.FormKey);
                    links++;
                    // Since Mutagen fixes.14 (C165) EnumerateFormLinks gives an armor addon's bone data modifiers too, so they
                    // are compared like every other link; the count of them is kept for the answer (GAPS Part 2 row 2)
                    if (rec.Type == "ArmorAddon" && l.Top == "BoneDataModifiers") bone++;
                    walked[k] = walked.GetValueOrDefault(k) + 1;
                });
                if (gap.Count > 0)
                {
                    // The record does hold those links: Mutagen's own property gives them
                    var held = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    var mods = ((IArmorAddonGetter)rec.Record).BoneDataModifiers;
                    foreach (var list in new[] { mods?.Male, mods?.Female })
                        foreach (var l in list ?? Enumerable.Empty<Mutagen.Bethesda.Plugins.IFormLinkGetter<IBoneModifierGetter>>())
                        {
                            if (l.FormKey.IsNull) continue;
                            string k = $"{l.FormKey.ID:X6}:{l.FormKey.ModKey.FileName}";
                            held[k] = held.GetValueOrDefault(k) + 1;
                        }
                    Ctx.Check(held.Count == gap.Count && held.All(kv => gap.TryGetValue(kv.Key, out int n) && n == kv.Value), $"{rec.Label}: the walk finds {gap.Values.Sum()} bone data modifier links; Mutagen's BoneDataModifiers property holds {held.Values.Sum()}");
                    gapRecords++; gapLinks += gap.Values.Sum();
                    gapExample ??= $"{rec.Label}: {string.Join(", ", gap.Keys.Select(s.Engine.Label))}";
                }
                bool same = mutagen.Count == walked.Count && mutagen.All(kv => walked.TryGetValue(kv.Key, out int n) && n == kv.Value);
                if (same) continue;
                differing++;
                if (examples.Count < 40)
                {
                    var onlyM = mutagen.Where(kv => walked.GetValueOrDefault(kv.Key) < kv.Value).Select(kv => $"{kv.Key}×{kv.Value - walked.GetValueOrDefault(kv.Key)}").Take(4);
                    var onlyW = walked.Where(kv => mutagen.GetValueOrDefault(kv.Key) < kv.Value).Select(kv => $"{kv.Key}×{kv.Value - mutagen.GetValueOrDefault(kv.Key)}").Take(4);
                    examples.Add($"{rec.Label} {rec.Info.Signature}: only Mutagen [{string.Join(", ", onlyM)}], only the walk [{string.Join(", ", onlyW)}]");
                }
            }
            perPlugin.Add($"{name} {N(records - r0)} records, {N(links - l0)} links, {N(differing - d0)} differ" + (gapRecords > g0 ? $", {gapRecords - g0} with bone data modifiers" : ""));
        }
        ctx.WriteReport("link-walk.txt", string.Join('\n', perPlugin) + "\n\n" + string.Join('\n', examples) + "\n\n" + gapExample + "\n");
        Ctx.Check(differing == 0, $"{N(differing)} of {N(records)} records give other links than Mutagen's EnumerateFormLinks: {string.Join(" | ", examples.Take(6))}");
        return $"{N(records)} records of {plugins.Length} plugins ({string.Join("; ", perPlugin)}): the walk over the JSON finds the same links as Mutagen's EnumerateFormLinks in every record ({N(links)} links, each record's links counted), including an armor addon's BoneDataModifiers ({N(bone)} links, given by EnumerateFormLinks since Mutagen fixes.14, C165). {N(unread)} records couldn't be read.";
    }
}
