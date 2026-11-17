// Tests on real plugins of the Data folder, read where they are: two real patches that override some of the same
// records, the output contract (cut and continued answers, error lines), and the times.
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using SFSearch.Compare;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Compare.Tests;

static class RealTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    static readonly HashSet<string> Official = new(LoadOrder.Implicit, StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- Two real patches
    /// <summary>Pairs of plugins in Data that override some of the same records, neither a master of the other and
    /// neither one of the game's own plugins: the pairs with the most records in common first. A darkstarpatch_ file
    /// comes second in its pair (it is the reference).</summary>
    static List<(string A, string B, int Shared)> PatchPairs(Ctx ctx)
    {
        var index = ctx.Session.Index;
        var shared = new Dictionary<(int, int), int>();
        foreach (var p in index.Plugins)
        {
            if (p.Segment == null || Official.Contains(p.Name)) continue;
            var seg = p.Segment;
            for (int i = 0; i < seg.FirstOwn; i++)
            {
                var e = seg[i];
                var v = index.Versions(seg.Names[e.Owner], e.LocalId);
                if (v == null) continue;
                foreach (var h in v.Holders)
                {
                    var q = index.Plugins[h.Plugin];
                    // Each pair once; the other one overrides the record too (it isn't the record's owner)
                    if (q.Id <= p.Id || q.Name.Equals(seg.Names[e.Owner], StringComparison.OrdinalIgnoreCase) || Official.Contains(q.Name)) continue;
                    if (p.Header!.Masters.Contains(q.Name, StringComparer.OrdinalIgnoreCase) || q.Header!.Masters.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    shared[(p.Id, q.Id)] = shared.GetValueOrDefault((p.Id, q.Id)) + 1;
                }
            }
        }
        static bool Reference(string name) => name.StartsWith("darkstarpatch_", StringComparison.OrdinalIgnoreCase);
        return shared.Select(s => (A: index.Plugins[s.Key.Item1].Name, B: index.Plugins[s.Key.Item2].Name, Shared: s.Value))
            .Select(s => Reference(s.A) && !Reference(s.B) ? (A: s.B, B: s.A, s.Shared) : s)
            .OrderByDescending(s => s.Shared).ThenBy(s => s.A, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.B, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string PatchReal(Ctx ctx)
    {
        var s = ctx.Session;
        var pairs = PatchPairs(ctx);
        Check(pairs.Count > 0, "no two patches in Data override the same record");
        string Line((string A, string B, int Shared) p) => $"{p.Shared,6} records overridden by both {p.A} ({s.Index.Plugin(p.A)!.Segment!.FirstOwn:N0} overrides, {s.Index.Plugin(p.A)!.Problems.TypeMismatches.Count} of another type than their base) and {p.B} ({s.Index.Plugin(p.B)!.Segment!.FirstOwn:N0} overrides, {s.Index.Plugin(p.B)!.Problems.TypeMismatches.Count} of another type)";
        ctx.WriteReport("patch-pairs.txt", string.Join('\n', pairs.Take(400).Select(Line)) + '\n');
        bool Sound(string name) => s.Index.Plugin(name)!.Problems.TypeMismatches.Count == 0;
        bool Reference(string name) => name.StartsWith("darkstarpatch_", StringComparison.OrdinalIgnoreCase);
        int Overrides(string name) => s.Index.Plugin(name)!.Segment!.FirstOwn;
        // Which pairs: the two patch-sized files (1,000 overrides at most) with the most records in common; the pair
        // with the most in common of all; the three pairs with a darkstarpatch_ file that have the most in common;
        // and the pair with the most in common where a darkstarpatch_ file was built for an older master (trap 15).
        var sound = pairs.Where(p => Sound(p.A) && Sound(p.B)).ToList();
        var chosen = new List<(string What, (string A, string B, int Shared) Pair)>();
        var patchSized = sound.FirstOrDefault(p => Overrides(p.A) <= 1000 && Overrides(p.B) <= 1000);
        if (patchSized.A != null) chosen.Add(("the two patch-sized files with the most records in common", patchSized));
        if (sound.Count > 0 && sound[0] != patchSized) chosen.Add(("the pair with the most records in common of all", sound[0]));
        foreach (var p in sound.Where(p => Reference(p.B)).Take(3)) chosen.Add(("a pair with a darkstarpatch_ file", p));
        var broken = pairs.FirstOrDefault(p => Reference(p.B) && !Sound(p.B));
        Check(chosen.Count >= 2, $"only {chosen.Count} pairs of sound patches were found");
        var said = new List<string>();
        long allRows = 0, allFields = 0, allPlain = 0, allEntries = 0, allNested = 0;
        var total = new Dictionary<FieldClass, long>();
        foreach (var (what, p) in chosen) said.Add($"{what}: {One(p.A, p.B, p.Shared)}");
        if (broken.A != null)
        {
            // A reference patch built for an older master: its overrides of another type aren't compared, and the answer says why
            var answer = ctx.Compare.PatchVsReference(broken.A, broken.B);
            ctx.WriteReport($"patch-real-{broken.A}-vs-{broken.B}.txt", answer.ToText(new TextOptions { Detail = true, MaxChars = int.MaxValue }));
            int unread = answer.Count(PatchRecordClass.NotRead);
            var problems = s.Index.Plugin(broken.B)!.Problems;
            int otherType = answer.Rows.Count(r => r.Class == PatchRecordClass.NotRead && r.Problem!.Contains("built for an older master", StringComparison.Ordinal));
            int ofNothing = answer.Rows.Count(r => r.Class == PatchRecordClass.NotRead && r.Problem!.Contains("an override of nothing", StringComparison.Ordinal));
            Check(otherType == problems.TypeMismatches.Count && ofNothing == problems.OverridesOfNothing.Count && unread == otherType + ofNothing,
                $"{broken.B} has {problems.TypeMismatches.Count} overrides of another type than their base and {problems.OverridesOfNothing.Count} of nothing by the header index; the answer leaves {unread} uncompared: {otherType} of another type, {ofNothing} of nothing");
            Check(answer.Notes.Any(n => n.Contains("of a different type than their base", StringComparison.Ordinal)), $"the answer for {broken.B} has no note that says the patch was built for an older master");
            said.Add($"a darkstarpatch_ file built for an older master: a = {broken.A}, b = {broken.B}: of b's {N(Overrides(broken.B))} overrides, {N(otherType)} are of another type than their base and {N(ofNothing)} override a record no master has; they are listed as not compared with the reason, two notes say so, and the other {N(answer.Rows.Count - unread)} records are classed ({N(answer.Rows.Count(r => r.InA && r.InB && r.Class != PatchRecordClass.NotRead))} by both)");
        }
        return $"{N(pairs.Count)} pairs of plugins in Data override some of the same records (neither a master of the other, the game's own plugins left out). Compared, each read where it is: " + string.Join(". ", said) +
            $". In all {N(allRows)} records and {N(allFields)} field changes: {N(total.GetValueOrDefault(FieldClass.SameChange))} the same change, {N(total.GetValueOrDefault(FieldClass.OnlyInA))} only in a, {N(total.GetValueOrDefault(FieldClass.OnlyInB))} only in b, {N(total.GetValueOrDefault(FieldClass.Different))} different. " +
            $"Every one was held against the cards of the base's, a's and b's record: {N(allPlain)} plain fields (the three values on the cards equal the answer's, and the class fits them), {N(allEntries)} entries of link lists (in or not in each card's list, as the class says), {N(allNested)} inside lists of objects (not held against cards)";

        string One(string nameA, string nameB, int shared)
        {
        var answer = ctx.Compare.PatchVsReference(nameA, nameB);
        string text = answer.ToText(new TextOptions { Detail = true, MaxChars = int.MaxValue });
        ctx.WriteReport($"patch-real-{nameA}-vs-{nameB}.txt", text);
        Check(answer.Both == shared, $"{nameA} and {nameB} override {shared} records in common by the header index; the answer has {answer.Both}");
        // The one reason a record of a sound patch may go unread: it is a heavy record, refused with its cost said first
        var heavy = answer.Rows.Where(r => r.Class == PatchRecordClass.NotRead && r.Problem!.Contains("isn't read unless asked for", StringComparison.Ordinal)).ToList();
        Check(answer.Count(PatchRecordClass.NotRead) == heavy.Count, $"{nameA} against {nameB}: {answer.Count(PatchRecordClass.NotRead)} records couldn't be read: {answer.Rows.FirstOrDefault(r => r.Class == PatchRecordClass.NotRead && !heavy.Contains(r))?.Problem}");
        int same = answer.Count(FieldClass.SameChange), onlyA = answer.Count(FieldClass.OnlyInA), onlyB = answer.Count(FieldClass.OnlyInB), different = answer.Count(FieldClass.Different);
        Check(answer.Count(PatchRecordClass.Same) + answer.Count(PatchRecordClass.Differ) + heavy.Count(r => r.InA && r.InB) == answer.Both, "the records both hold aren't all classed as the same or as differing");
        foreach (var cls in Enum.GetValues<FieldClass>()) total[cls] = total.GetValueOrDefault(cls) + answer.Count(cls);

        // Every row held against the cards of the three records (the base's, a's and b's): the cards are made by
        // another walk over the record (CardBuilder), with its own labels and its own defaults.
        var a = s.Side(nameA); var b = s.Side(nameB);
        s.Use(a, b);
        long checkedFields = 0, plain = 0, entries = 0, nested = 0, rows = 0;
        var sample = new List<string>();
        foreach (var row in answer.Rows.Where(r => r.Class != PatchRecordClass.NotRead))
        {
            HeaderIndex.TryParseFormKey(row.FormKey, out var owner, out var id);
            Card? Card(string? plugin)
            {
                if (plugin == null) return null;
                var r = CompareSession.Find(s.Index, plugin, owner, id);
                return r == null ? null : s.Engine.Card(s.Engine.Load(r.Value));
            }
            var cardA = row.InA ? Card(nameA) : null; var cardB = row.InB ? Card(nameB) : null;
            var baseA = Card(row.BaseA); var baseB = Card(row.BaseB);
            rows++;
            foreach (var f in row.Fields)
            {
                if (f.Direct != null) continue;
                checkedFields++;
                var d = f.Any;
                if (f.IsEntry)
                {
                    // An entry of a set of links: on the card of each record the link is in the list, or isn't
                    bool In(Card? c) => c != null && c.Fields.FirstOrDefault(x => x.Name == d.Top)?.Children?.Any(x => string.Equals(x.Json?.GetValue<string>(), d.Element, StringComparison.OrdinalIgnoreCase)) == true;
                    bool added = d.Change == Change.OnlyB;
                    bool wantA = f.A != null ? added : !added, wantB = f.B != null ? added : !added;
                    if (f.A != null) Check(In(baseA) != added && In(cardA) == added, $"{row.Label}: {d.Top} {(added ? "+" : "−")} {d.Element} [{f.Class}], but the cards say base {In(baseA)}, a {In(cardA)}");
                    if (f.B != null) Check(In(baseB) != added && In(cardB) == added, $"{row.Label}: {d.Top} {(added ? "+" : "−")} {d.Element} [{f.Class}], but the cards say base {In(baseB)}, b {In(cardB)}");
                    if (row.InA && f.A == null) Check(In(cardA) == In(baseA), $"{row.Label}: {d.Top} {d.Element} is classed {f.Class}, but a's card differs from its base's there");
                    if (row.InB && f.B == null) Check(In(cardB) == In(baseB), $"{row.Label}: {d.Top} {d.Element} is classed {f.Class}, but b's card differs from its base's there");
                    _ = wantA; _ = wantB;
                    entries++;
                    continue;
                }
                if (d.Path.Contains('[') || d.Change == Change.Order) { nested++; continue; }
                // A plain field at a path: the value the card holds there (found by the card's own walk over the record),
                // as JSON with every link's plugin name in one letter case
                string? At(Card? c) => c == null ? null : Value(c, d.Path);
                if (f.A != null)
                {
                    if (At(baseA) is { } bt && f.A.AJson != null) Check(bt == Canon(f.A.AJson), $"{row.Label}: {d.Path} base is {Canon(f.A.AJson)} in the answer, {bt} on the card of {row.BaseA}");
                    if (At(cardA) is { } at && f.A.BJson != null) Check(at == Canon(f.A.BJson), $"{row.Label}: {d.Path} of a is {Canon(f.A.BJson)} in the answer, {at} on the card of {nameA}");
                }
                if (f.B != null)
                {
                    if (At(baseB) is { } bt && f.B.AJson != null) Check(bt == Canon(f.B.AJson), $"{row.Label}: {d.Path} base is {Canon(f.B.AJson)} in the answer, {bt} on the card of {row.BaseB}");
                    if (At(cardB) is { } at && f.B.BJson != null) Check(at == Canon(f.B.BJson), $"{row.Label}: {d.Path} of b is {Canon(f.B.BJson)} in the answer, {at} on the card of {nameB}");
                }
                if (f.Class == FieldClass.SameChange) Check(At(cardA) == At(cardB), $"{row.Label}: {d.Path} is classed as the same change, but the two cards show \"{At(cardA)}\" and \"{At(cardB)}\"");
                if (f.Class == FieldClass.Different) Check(At(cardA) != At(cardB), $"{row.Label}: {d.Path} is classed as different, but the two cards show the same \"{At(cardA)}\"");
                if (f.Class == FieldClass.OnlyInA && row.InB) Check(At(cardB) == At(baseB), $"{row.Label}: {d.Path} is classed as only in a, but b's card shows \"{At(cardB)}\" and its base's \"{At(baseB)}\"");
                if (f.Class == FieldClass.OnlyInB && row.InA) Check(At(cardA) == At(baseA), $"{row.Label}: {d.Path} is classed as only in b, but a's card shows \"{At(cardA)}\" and its base's \"{At(baseA)}\"");
                plain++;
            }
            if (sample.Count < 10 && row.Fields.Count > 0 && (row.Class == PatchRecordClass.Differ || sample.Count >= 5)) sample.Add($"{row.Label} {row.Signature} [{row.Class}]");
        }
        ctx.WriteReport($"patch-real-sample-{nameA}-vs-{nameB}.txt", string.Join(Environment.NewLine, sample) + Environment.NewLine);
        allRows += rows; allFields += checkedFields; allPlain += plain; allEntries += entries; allNested += nested;
        return $"a = {nameA} ({N(Overrides(nameA))} overrides), b = {nameB} ({N(Overrides(nameB))}): {N(answer.Rows.Count)} records overridden by at least one, {N(answer.Both)} by both ({N(answer.Count(PatchRecordClass.Same))} the same, {N(answer.Count(PatchRecordClass.Differ))} differ), {N(answer.Count(PatchRecordClass.OnlyA))} only in a, {N(answer.Count(PatchRecordClass.OnlyB))} only in b; " +
            $"field changes: {N(same)} the same change, {N(onlyA)} only in a, {N(onlyB)} only in b, {N(different)} different" + (answer.Reconciled > 0 ? $"; {answer.Reconciled} records needed the direct comparison to settle" : "") + (heavy.Count > 0 ? $"; {heavy.Count} not read: {heavy[0].Label}, a heavy {heavy[0].Signature} that is refused with its cost said first" : "");
        }

        static string? Value(Card card, string path)
        {
            List<CardField>? level = card.Fields; CardField? at = null;
            foreach (var part in path.Split('.'))
            {
                at = level?.FirstOrDefault(x => x.Name == part);
                if (at == null) return null;
                level = at.Children;
            }
            // A translated text is held by the text the card shows (the language asked for), anything else by its JSON
            return at?.Json == null ? null : at.Kind == "Text" ? "text:" + at.Text : Canon(at.Json);
        }
        static string Canon(System.Text.Json.Nodes.JsonNode n)
        {
            if (n is System.Text.Json.Nodes.JsonObject o && o.ContainsKey("TargetLanguage"))
            {
                string? english = o["Values"] is System.Text.Json.Nodes.JsonArray list
                    ? list.Select(v => v!.AsObject()).FirstOrDefault(v => v["Language"]?.GetValue<string>() == "English")?["String"]?.GetValue<string>()
                    : o["Value"]?.GetValue<string>();
                // A card shows a text without the Creation Kit's tag (milestone M8); the comparison's answer holds the stored text
                return "text:" + (SFSearch.Records.CardBuilder.NoTag(english) ?? "");
            }
            return Regex.Replace(n.ToJsonString(), @"[0-9A-Fa-f]{6}:[^""\:]+.es[mpl]", m => m.Value.ToUpperInvariant(), RegexOptions.IgnoreCase);
        }
    }

    static readonly System.Text.Json.JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A patch in Data with over 1,000 overrides: not one of the game's own plugins, and the one whose
    /// records are overrides to the largest part (a patch is mostly overrides); among equals, the fewest overrides.</summary>
    static (string Name, int Overrides) BigPatch(Ctx ctx)
    {
        var all = ctx.Session.Index.Plugins.Where(p => p.Segment is { FirstOwn: > 1000 } && !Official.Contains(p.Name) && !Ctx.HandsOff(p.Name) && p.Problems.MissingMasters.Count == 0)
            .Select(p => (p.Name, Overrides: p.Segment!.FirstOwn, Share: Math.Round((double)p.Segment.FirstOwn / p.Segment.Count, 2)))
            .OrderByDescending(p => p.Share).ThenBy(p => p.Overrides).ToList();
        Check(all.Count > 0, "no plugin in Data but the game's own has over 1,000 overrides");
        return (all[0].Name, all[0].Overrides);
    }

    // ---------------------------------------------------------------- The output contract
    // ---------------------------------------------------------------- ITMs name their header difference
    /// <summary>
    /// darkstar.esm's 20 ITMs (FORMID-TODO item 6, 2026-11-13): each holds its master's data, but its record header's
    /// numbers differ (form version, version control), and xEdit may not show such a record as an ITM. Each ITM row
    /// names what differs, checked against the raw reader's header; the explanation is given once; an ITM whose header
    /// is the same too gets nothing extra.
    /// </summary>
    public static string ItmHeaders(Ctx ctx)
    {
        const string plugin = "darkstar.esm";
        // Equal headers: nothing extra; a difference: plugin's number first, version control in hex
        var a = new HeaderNumbers(0, 582, 0); var b = new HeaderNumbers(0x002034C1, 581, 0);
        Check(HeaderNumbers.Differences(a, a) == null, "the same header numbers gave a difference");
        Check(HeaderNumbers.ItmText("Starfield.esm", Array.Empty<string>(), null) == "identical to Starfield.esm", "an ITM with the same header says more than \"identical to Starfield.esm\"");
        Check(HeaderNumbers.Differences(a, b) == "form version 582 vs 581; version control 0 vs 0x002034C1", $"the header difference reads \"{HeaderNumbers.Differences(a, b)}\"");

        var answer = ctx.Compare.PluginVsMasters(plugin);
        var itms = answer.Itms.ToList();
        Check(itms.Count == 20, $"{plugin} has {itms.Count} ITMs; 20 were expected");
        var s = ctx.Session;
        var shapes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in itms)
        {
            var rawBase = s.Engine.Raw(CompareSession.Find(s.Index, r.Base!, r.FormKey)!.Value); var rawMine = s.Engine.Raw(CompareSession.Find(s.Index, plugin, r.FormKey)!.Value);
            string? expected = HeaderNumbers.Differences(new HeaderNumbers(rawMine.VersionControl, rawMine.FormVersion, rawMine.Version2), new HeaderNumbers(rawBase.VersionControl, rawBase.FormVersion, rawBase.Version2));
            Check(r.HeaderDiffers == expected, $"{r.Label}: the answer says the header differs by \"{r.HeaderDiffers}\"; the raw reader's headers give \"{expected}\"");
            Check(r.HeaderDiffers != null, $"{r.Label}: its header is the same as {r.Base}'s; every ITM of {plugin} was expected to differ there");
            Check((string?)r.ToJson()["headerDiffers"] == r.HeaderDiffers, $"{r.Label}: the JSON's headerDiffers isn't the row's");
            string shape = Regex.Replace(r.HeaderDiffers!, "0x[0-9A-F]{8}", "0x…");
            shapes[shape] = shapes.GetValueOrDefault(shape) + 1;
            // Every ITM held against the files (fix A4, 2026-11-12; carried over from the retired known-itms, whose 1.x side
            // went with 2.0): the subrecords, decompressed, are the same signatures in the same order with the same bytes,
            // the EDID included, and the record flags are the same but for the compressed bit
            var subsBase = rawBase.Subrecords.Select(x => x.Signature + " " + RawRecord.Hex(rawBase.Content(x))).ToList();
            var subsMine = rawMine.Subrecords.Select(x => x.Signature + " " + RawRecord.Hex(rawMine.Content(x))).ToList();
            Check(subsBase.SequenceEqual(subsMine), $"{r.Label}: an ITM, but its subrecords aren't the same bytes in {r.Base} and {plugin}: " +
                string.Join(", ", subsBase.Except(subsMine).Concat(subsMine.Except(subsBase)).Select(x => x.Split(' ')[0]).Distinct()));
            Check((rawBase.Flags & ~0x40000u) == (rawMine.Flags & ~0x40000u), $"{r.Label}: an ITM, but the record flags differ: {rawBase.Flags:X8} and {rawMine.Flags:X8}");
        }
        string text = answer.ToText(new TextOptions { MaxChars = 200000 });
        ctx.WriteReport("itm-headers.txt", text);
        int explained = Regex.Matches(text, Regex.Escape(HeaderNumbers.ItmExplanation)).Count;
        Check(explained == 1, $"the explanation is in the answer {explained} times; once was expected\n{text}");
        foreach (var r in itms)
            Check(text.Contains($"{r.Label} {r.Signature}: identical to {r.Base} in data; header differs: {r.HeaderDiffers}", StringComparison.Ordinal), $"{r.Label}: its row doesn't name its header difference\n{text}");
        return $"{plugin}: {itms.Count} ITMs, each the same subrecord bytes and flags as its master's version, each names its header difference, as the raw reader reads both headers ({string.Join("; ", shapes.OrderByDescending(k => k.Value).Select(k => $"{k.Value}× {k.Key}"))}); the explanation once; equal headers add nothing";
    }

    /// <summary>
    /// GAPS S50: a partial form (record flag 0x4000) holds only some fields; the game lays them over the version before
    /// it. SFBGS00D.esm's CityNewAtlantisLodgeInt (06BB29:Starfield.esm) holds only its EDID: taken literally, every
    /// other field of the cell read as changed. Merged over Starfield.esm's cell it equals it; 00vv-unfamiliarfaces.esm's
    /// override, held against that merged cell, changes only Name and Ownership. A worldspace's partial form whose
    /// component holds a BNAM keeps the worldspace's own Biome (BNAM) from the version before it.
    /// </summary>
    public static string PartialForms(Ctx ctx)
    {
        var s = ctx.Session; var c = ctx.Compare;
        var label = s.Labeller();
        var options = new DiffOptions { Label = label };
        RecordView View(string plugin, string formKey) => s.View(s.Engine, CompareSession.Find(s.Index, plugin, formKey) ?? throw new TestFailed($"{plugin} doesn't hold {formKey}"));

        // The merge itself
        var full = View("Starfield.esm", "06BB29:Starfield.esm"); var partial = View("SFBGS00D.esm", "06BB29:Starfield.esm");
        Check(CompareSession.IsPartial(partial) && !CompareSession.IsPartial(full), "SFBGS00D.esm's 06BB29 isn't a partial form, or Starfield.esm's is");
        var literal = s.Differ.Diff(full.Type, full.Json, partial.Type, partial.Json, options);
        Check(literal.TopFields.Count >= 10, $"taken literally the partial form differs in {literal.TopFields.Count} fields; it holds only its EDID, so most fields were expected to differ");
        var merged = s.Merge(partial, full);
        Check(merged.Merged == "SFBGS00D.esm's partial form merged over Starfield.esm", $"the merged record says \"{merged.Merged}\"");
        Check(!CompareSession.IsPartial(merged), "the merged record still has the PartialForm flag");
        var mergedDiff = s.Differ.Diff(full.Type, full.Json, merged.Type, merged.Json, options);
        Check(mergedDiff.Same, $"merged over Starfield.esm, the cell still differs in {string.Join(", ", mergedDiff.TopFields)}");
        var effective = s.Effective(s.Engine, partial.Ref);
        Check(effective.Merged == merged.Merged, $"Effective gives \"{effective.Merged}\"");

        // plugin_vs_masters: the partial form as the base, and as the override
        var lodge = c.PluginVsMasters("00vv-unfamiliarfaces.esm", "06BB29:Starfield.esm").Overrides.Single();
        Check(lodge.Class == OverrideClass.Changed && lodge.Base == "SFBGS00D.esm" && lodge.Merged == merged.Merged, $"00vv-unfamiliarfaces.esm's 06BB29: {lodge.Class} against {lodge.Base}, merged \"{lodge.Merged}\"");
        Check(string.Join(",", lodge.Diff!.TopFields) == "Name,Ownership", $"00vv-unfamiliarfaces.esm's 06BB29 changes {string.Join(", ", lodge.Diff.TopFields)}; Name and Ownership were expected (the literal comparison gave 15 fields)");
        var own = c.PluginVsMasters("SFBGS00D.esm", "06BB29:Starfield.esm");
        var ownRow = own.Overrides.Single();
        Check(ownRow.Class == OverrideClass.Itm && ownRow.Merged == merged.Merged, $"SFBGS00D.esm's own partial form: {ownRow.Class}, merged \"{ownRow.Merged}\"; an ITM was expected");
        string text = own.ToText(new TextOptions { MaxChars = 200000 });
        Check(text.Contains($"identical to Starfield.esm ({merged.Merged})", StringComparison.Ordinal) && Regex.Matches(text, Regex.Escape(HeaderNumbers.PartialExplanation)).Count == 1, $"the ITM row doesn't say it was merged, or the explanation isn't there once:\n{text}");
        Check((string?)ownRow.ToJson()["merged"] == merged.Merged, "the JSON row has no merged");
        Check(own.Sources.Any(x => x.StartsWith("Starfield.esm ", StringComparison.Ordinal)), "Starfield.esm, read for the merge, isn't among the sources");

        // A worldspace: the component's BNAM isn't the worldspace's own Biome
        var red = c.PluginVsMasters("SFBGS050.esm", "141E6E:Starfield.esm").Overrides.Single();
        Check(red.Merged != null && !red.Diff!.TopFields.Contains("Biome"), $"SFBGS050.esm's settleredmile (141E6E:Starfield.esm): merged \"{red.Merged}\", changes {string.Join(", ", red.Diff!.TopFields)}; Biome was expected from Starfield.esm");

        // versions: the partial form's row is compared merged
        var versions = c.Versions("06BB29:Starfield.esm");
        var v = versions.Loaded.First(r => r.Plugin == "SFBGS00D.esm");
        Check(v.Merged != null && v.Diff != null && v.Diff.Same, $"versions: SFBGS00D.esm's row: merged \"{v.Merged}\", same {v.Diff?.Same}");
        return $"06BB29:Starfield.esm: the literal partial form differs in {literal.TopFields.Count} fields, merged in none; 00vv-unfamiliarfaces.esm changes {string.Join(", ", lodge.Diff.TopFields)} against the merged cell; SFBGS00D.esm's own partial form is an ITM; settleredmile keeps its Biome; versions merges too";
    }

    /// <summary>
    /// GAPS S47: a heavy record (over 16 MB) isn't read through Mutagen without allow_heavy; an override of one is held
    /// against its base by its bytes first. SFBGS006.esm's override of the navmesh info map (000FF1:Starfield.esm) is
    /// 12,630 bytes against Starfield.esm's 53,882,334: not an ITM, and the row says the bytes differ. With the heavy
    /// line lowered to 64 bytes for this test, every record of darkstar.esm is heavy: its 20 ITMs are still found, each
    /// told from its bytes. Bytes of two plugins that number FormIDs differently prove nothing (null).
    /// </summary>
    public static string HeavyBytes(Ctx ctx)
    {
        var s = ctx.Session; var c = ctx.Compare;
        var navi = c.PluginVsMasters("SFBGS006.esm", "000FF1:Starfield.esm").Overrides.Single();
        var mine = CompareSession.Find(s.Index, "SFBGS006.esm", "000FF1:Starfield.esm")!.Value; var theirs = CompareSession.Find(s.Index, "Starfield.esm", "000FF1:Starfield.esm")!.Value;
        Check(CompareSession.SameBytes(s.Engine, mine, s.Engine, theirs) == false, "SFBGS006.esm's 000FF1 reads as the same bytes as Starfield.esm's");
        Check(navi.Class == OverrideClass.NotRead && navi.Problem!.StartsWith("Its bytes differ from Starfield.esm's (", StringComparison.Ordinal) && navi.Problem.Contains("so it isn't an ITM", StringComparison.Ordinal),
            $"SFBGS006.esm's 000FF1: {navi.Class}, \"{navi.Problem}\"");

        // FormIDs numbered another way: the bytes prove nothing
        var dark = s.Index.Plugin("darkstar.esm")!; var d00 = s.Index.Plugin("SFBGS00D.esm")!;
        bool sameSpace = dark.Header!.Masters.Count > d00.Header!.Masters.Count && dark.Header.Masters[d00.Header.Masters.Count].Equals("SFBGS00D.esm", StringComparison.OrdinalIgnoreCase);
        var lodgeDark = CompareSession.Find(s.Index, "darkstar.esm", "06BB29:Starfield.esm")!.Value; var lodge00D = CompareSession.Find(s.Index, "SFBGS00D.esm", "06BB29:Starfield.esm")!.Value;
        var spaced = CompareSession.SameBytes(s.Engine, lodgeDark, s.Engine, lodge00D);
        Check(sameSpace ? spaced != null : spaced == null, $"darkstar.esm against SFBGS00D.esm (darkstar.esm's masters: {string.Join(", ", dark.Header.Masters)}): SameBytes gives {spaced?.ToString() ?? "null"}");

        // Every record heavy: darkstar.esm's ITMs told from their bytes alone
        long was = s.Engine.HeavyRecordBytes;
        PluginVsMastersAnswer heavy;
        try { s.Engine.HeavyRecordBytes = 64; heavy = c.PluginVsMasters("darkstar.esm"); }
        finally { s.Engine.HeavyRecordBytes = was; }
        var itms = heavy.Itms.ToList();
        var strict = c.PluginVsMasters("darkstar.esm").Itms.Select(r => r.FormKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check(itms.Count == 20 && itms.All(r => r.ByBytes != null && strict.Contains(r.FormKey)), $"darkstar.esm with every record heavy: {itms.Count} ITMs ({itms.Count(r => r.ByBytes != null)} told from bytes); the 20 of the full comparison were expected");
        string text = heavy.ToText(new TextOptions { MaxChars = 400000 });
        Check(text.Contains("identical to Starfield.esm byte for byte (a heavy record of ", StringComparison.Ordinal) && (string?)itms[0].ToJson()["byBytes"] == itms[0].ByBytes, "an ITM row told from its bytes doesn't say so, or its JSON has no byBytes");
        return $"SFBGS006.esm's navmesh info map: {navi.Problem![..navi.Problem!.IndexOf(", so", StringComparison.Ordinal)]}: not an ITM; darkstar.esm with every record heavy: its 20 ITMs told from their bytes; darkstar.esm against SFBGS00D.esm: {(spaced?.ToString() ?? "null (FormIDs numbered another way)")}";
    }

    /// <summary>
    /// GAPS S49: the opt-in xEdit-style ITM verdict. Overrides SF1Edit 4.1.5q lists as "Identical to Master" in the ITM
    /// hunt of 2026-11-15 (C:\mtg\itm-hunt\xedit-itm.tsv) that the strict verdict calls changed, one per rule; each is an
    /// ITM with like_xedit and its row names the rule. Overrides SF1Edit doesn't list stay out: a navmesh whose edge
    /// links changed, a lock's named Unknown field, a cell that wins over another plugin's change, an override
    /// identical to a previous override but not to the record's own plugin's version.
    /// </summary>
    public static string LikeXEdit(Ctx ctx)
    {
        var c = ctx.Compare;
        var cases = new (string Plugin, string FormKey, string Rule)[]
        {
            ("00vv-unfamiliarfaces.esm", "27F189:Starfield.esm", "unknown or unused bytes (Unknown)"),                 // CELL XCLC padding
            ("sfta02.esm", "08AE04:Starfield.esm", "unknown or unused bytes (LevelUnknown)"),                           // REFR XLOC padding
            ("MissionsGalore.esm", "13F2C4:Starfield.esm", "unknown or unused bytes (Unknown2)"),                       // SMQN condition padding
            ("aa50_longwatch.esm", "2F99F8:Starfield.esm", "unknown or unused bytes (Unused)"),                         // LVLO Unused
            ("N12_EnergyShields.esm", "132305:Starfield.esm", "a location added (XLRL) to a temporary reference"),      // REFR XLRL added
            ("darkstarpatch_terranarmada.esm", "0A9B43:SFBGS050.esm", "factions (SNAM) in another order"),              // NPC_ factions
            ("starfield_crowd_faces_z.esm", "0A6FF8:Starfield.esm", "keywords (KWDA) in another order"),                // ARMO keywords, Name, Description
            ("economy.esm", "2C9BCD:Starfield.esm", "the same English text"),                                          // MISC Name, plain against localized
            ("darkstar.esm", "06BB29:Starfield.esm", "the same English text"),                                         // CELL through a partial form
        };
        var done = new List<string>();
        foreach (var (plugin, formKey, rule) in cases)
        {
            var strict = c.PluginVsMasters(plugin, formKey).Overrides.Single();
            var like = c.PluginVsMasters(plugin, formKey, likeXEdit: true);
            var row = like.Overrides.Single();
            Check(strict.Class == OverrideClass.Changed, $"{plugin} {formKey}: the strict verdict says {strict.Class}; changed was expected");
            Check(row.Class == OverrideClass.Itm && row.XEditRules != null && row.XEditRules.Any(r => r.StartsWith(rule, StringComparison.Ordinal)),
                $"{plugin} {formKey} with like_xedit: {row.Class}, rules {string.Join(" | ", row.XEditRules ?? new())}{(row.NotListed != null ? ", " + row.NotListed : "")}; an ITM by \"{rule}\" was expected (strict: {string.Join(", ", strict.Diff!.TopFields)})");
            string text = like.ToText(new TextOptions { MaxChars = 200000 });
            Check(text.Contains("as SF1Edit compares (like_xedit: ", StringComparison.Ordinal) && text.Contains(HeaderNumbers.XEditExplanation, StringComparison.Ordinal), $"{plugin} {formKey}: the row doesn't name its rule, or the answer doesn't explain like_xedit:\n{text}");
            done.Add($"{strict.Signature} {string.Join("/", strict.Diff!.TopFields)}");
        }
        // Not ITMs for SF1Edit either
        var navmesh = c.PluginVsMasters("NewAtlantisPerimeterGate.esm", "123E16:Starfield.esm", likeXEdit: true).Overrides.Single();
        Check(navmesh.Class == OverrideClass.Changed, $"NewAtlantisPerimeterGate.esm's navmesh 123E16 with like_xedit: {navmesh.Class}; its edge links change, so changed was expected");
        var lockRef = c.PluginVsMasters("aa50_longwatch.esm", "14E991:Starfield.esm", likeXEdit: true).Overrides.Single();
        Check(lockRef.Class == OverrideClass.Changed, $"aa50_longwatch.esm's 14E991 (its lock's Unknown, which xEdit names, 8 against 1) with like_xedit: {lockRef.Class}");
        var wins = c.PluginVsMasters("rabbits_real_lights_starfield.esm", "0125A0:Starfield.esm", likeXEdit: true).Overrides.Single();
        Check(wins.Class == OverrideClass.ItmNotListed && wins.NotListed!.Contains("NewAtlantisPerimeterGate.esm", StringComparison.Ordinal), $"rabbits_real_lights_starfield.esm's cell 0125A0: {wins.Class}, \"{wins.NotListed}\"; not listed (it wins over NewAtlantisPerimeterGate.esm) was expected");
        // darkstar.esm's 20 strict ITMs stay 20 without like_xedit
        Check(c.PluginVsMasters("darkstar.esm").Itms.Count() == 20, "darkstar.esm's strict ITMs aren't 20");
        return $"{cases.Length} overrides SF1Edit calls ITMs, changed in the strict verdict ({string.Join("; ", done)}), are ITMs with like_xedit, each naming its rule; a navmesh with new edge links, a lock's named Unknown and a cell winning a conflict aren't; darkstar.esm strict: 20";
    }

    /// <summary>
    /// GAPS S51: the ITM hunt read compare plugin_vs_masters as leaving out world data (no rows for
    /// rabbits_real_lights_starfield.esm's 122 disputed cells and worldspaces). compare reads world data: every override,
    /// the cells and worldspaces among them, is compared, and the answer says so in one line; what it couldn't compare
    /// is named in one line too (SFBGS006.esm's 51 MB navmesh info map's base).
    /// </summary>
    public static string WorldIncluded(Ctx ctx)
    {
        var c = ctx.Compare;
        var rabbits = c.PluginVsMasters("rabbits_real_lights_starfield.esm");
        int world = rabbits.WorldOverrides.Values.Sum();
        Check(world >= 1000 && rabbits.WorldOverrides.ContainsKey("CELL") && rabbits.WorldOverrides.ContainsKey("WRLD"), $"rabbits_real_lights_starfield.esm: {world} world overrides counted ({string.Join(", ", rabbits.WorldOverrides.Select(k => $"{k.Key} {k.Value}"))})");
        foreach (string fk in new[] { "01251B:Starfield.esm", "0125A0:Starfield.esm", "08DA69:Starfield.esm", "056B1F:Starfield.esm" })
        {
            var row = rabbits.Overrides.SingleOrDefault(r => r.FormKey.Equals(fk, StringComparison.OrdinalIgnoreCase));
            Check(row != null && row.Diff != null && row.Class is OverrideClass.Changed or OverrideClass.Itm, $"rabbits_real_lights_starfield.esm: the world record {fk} isn't compared ({row?.Class.ToString() ?? "not listed"})");
        }
        string text = rabbits.ToText(new TextOptions { MaxChars = 4000 });
        Check(text.Contains($"World data included: {world:N0} of the {rabbits.Overrides.Count:N0} overrides are world records (", StringComparison.Ordinal) && text.Contains("check2 leaves them out unless --world", StringComparison.Ordinal), $"no line says world data is included:\n{text}");
        Check(rabbits.ToJson()["worldOverrides"]?["CELL"] != null, "the JSON has no worldOverrides");
        var navi = c.PluginVsMasters("SFBGS006.esm");
        string naviText = navi.ToText(new TextOptions { MaxChars = 4000 });
        Check(navi.NotRead.Count() == 1 && naviText.Contains("Left out: 1 override couldn't be compared", StringComparison.Ordinal), $"SFBGS006.esm: {navi.NotRead.Count()} not read; no line names what was left out:\n{naviText}");
        return $"rabbits_real_lights_starfield.esm: {rabbits.Overrides.Count:N0} overrides compared, {world:N0} of them world records ({string.Join(", ", rabbits.WorldOverrides.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value:N0}"))}), the line says so; SFBGS006.esm: the one override left out is named";
    }

    public static string OutputContract(Ctx ctx)
    {
        var c = ctx.Compare;
        var done = new List<string>();
        var big = BigPatch(ctx);
        var answers = new List<(string Name, CompareAnswer Answer)>
        {
            ("versions", c.Versions("0476C4:Starfield.esm")),
            ("records", c.Records("Starfield.esm", "219447:Starfield.esm", "darkstar.esm", "0082B0:darkstar.esm")),
            ("plugin_vs_masters", c.PluginVsMasters(big.Name)),
            ("plugins", c.Plugins("atom74.esm", "darkstarpatch_atom74.esm")),
        };
        var formKey = new Regex(@"[0-9A-F]{6}:[^\s()\[\]|,;]+\.es[mpl]", RegexOptions.IgnoreCase);
        foreach (var (name, answer) in answers)
        {
            Check(answer.Kind == name, $"the {name} answer says it is {answer.Kind}");
            foreach (bool detail in new[] { false, true })
            {
                string text = answer.ToText(new TextOptions { Detail = detail, MaxChars = int.MaxValue });
                var lines = text.TrimEnd('\n').Split('\n');
                Check(lines[^1].StartsWith("[cost] ", StringComparison.Ordinal) && lines.Any(l => l.StartsWith("[source] ", StringComparison.Ordinal)), $"{name}: the text lacks [source] or doesn't end with [cost]");
                Check(!lines.Any(l => l.StartsWith("[more]", StringComparison.Ordinal)), $"{name}: an answer that wasn't cut says [more]");
                // Every record is printed as EditorID (FormID:plugin): a FormKey always sits in brackets after a name
                foreach (var l in lines.Where(l => !l.StartsWith("[source]", StringComparison.Ordinal)))
                    foreach (Match m in formKey.Matches(l))
                        Check(m.Index > 0 && l[m.Index - 1] == '(', $"{name}: a bare FormKey in the line \"{l}\"");
            }
            var json = answer.ToJson();
            Check(json["kind"]!.GetValue<string>() == name && json["notes"] != null && json["source"]!.AsArray().Count > 0 && json["cost"]!["seconds"] != null, $"{name}: the structured answer lacks kind, notes, source or cost");
        }
        done.Add("the four answers on real plugins, compact and in detail: each ends with [source] and [cost], none says [more] uncut, every FormKey sits in brackets after an EditorID, each has a structured form with kind, notes, source and cost");

        // A long answer, cut and continued: the pages together are the whole answer
        var longAnswer = answers[2].Answer;
        string whole = longAnswer.ToText(new TextOptions { MaxChars = int.MaxValue });
        var wholeLines = whole.TrimEnd('\n').Split('\n');
        int headLines = 0;
        string first = longAnswer.ToText(new TextOptions { MaxChars = 6000 }, out int next);
        Check(next > 0, $"the answer for {big.Name} ({whole.Length:N0} characters) wasn't cut at 6,000");
        var pages = new List<string> { first };
        var cursors = new List<int> { 0 };
        while (next >= 0)
        {
            cursors.Add(next);
            pages.Add(longAnswer.ToText(new TextOptions { MaxChars = 6000, Cursor = next }, out next));
            Check(pages.Count < 2000, "the answer never ends");
        }
        var joined = new List<string>();
        for (int p = 0; p < pages.Count; p++)
        {
            var lines = pages[p].TrimEnd('\n').Split('\n').ToList();
            bool last = p == pages.Count - 1;
            int more = lines.FindIndex(l => l.StartsWith("[more] ", StringComparison.Ordinal));
            Check(last == (more < 0), $"page {p + 1} of {pages.Count}: {(last ? "the last page says [more]" : "no [more] line")}");
            if (!last)
            {
                var m = Regex.Match(lines[more], @"^\[more\] ([\d,]+) of ([\d,]+) records shown \(([\d,]+) to ([\d,]+)\)\. Next: cursor ""(\d+)""\.$");
                Check(m.Success && int.Parse(m.Groups[5].Value) == cursors[p + 1] && int.Parse(m.Groups[3].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture) == cursors[p] + 1, $"page {p + 1}: \"{lines[more]}\" doesn't give the next cursor {cursors[p + 1]}");
            }
            // The rows of a page: after the heading (the same on every page) and before [more], [notes], [source], [cost]
            int end = lines.FindIndex(l => l.StartsWith("[more] ", StringComparison.Ordinal) || l.StartsWith("[notes] ", StringComparison.Ordinal) || l.StartsWith("[source] ", StringComparison.Ordinal) || l.StartsWith("[cost] ", StringComparison.Ordinal));
            if (p == 0)
            {
                // The heading is what the first page and the whole answer share before the rows
                var second = pages[1].Split('\n');
                while (headLines < lines.Count && headLines < second.Length && lines[headLines] == second[headLines]) headLines++;
            }
            int start = headLines + (p > 0 ? 1 : 0); // a continued page says "(continued from record N)"
            if (p > 0) Check(lines[headLines].StartsWith("(continued from record ", StringComparison.Ordinal), $"page {p + 1} doesn't say where it continues: \"{lines[headLines]}\"");
            var rows = lines.GetRange(start, end - start);
            Check(rows.Sum(r => r.Length + 1) <= 6000 || rows.Count == 1, $"page {p + 1} has {rows.Sum(r => r.Length + 1):N0} characters of rows; the limit was 6,000");
            joined.AddRange(rows);
        }
        int wholeEnd = Array.FindIndex(wholeLines, l => l.StartsWith("[notes] ", StringComparison.Ordinal) || l.StartsWith("[source] ", StringComparison.Ordinal) || l.StartsWith("[cost] ", StringComparison.Ordinal));
        var wholeRows = wholeLines.Skip(headLines).Take(wholeEnd - headLines).ToList();
        Check(joined.SequenceEqual(wholeRows), $"the {pages.Count} pages together have {joined.Count} lines of rows, the uncut answer {wholeRows.Count}; they differ");
        done.Add($"the answer for {big.Name} ({N(big.Overrides)} overrides, {N(whole.Length)} characters uncut) cut at 6,000 characters of rows a page: {pages.Count} pages, each with the heading and a [more] line that gives the next cursor; the pages' rows together are the uncut answer's {N(wholeRows.Count)} lines, none lost and none twice");

        // Errors: one line, with what to do
        var errors = new (string What, Action Call, string Must)[]
        {
            ("a plugin that isn't there", () => c.PluginVsMasters("zz_no_such_plugin.esm"), "zz_no_such_plugin.esm"),
            ("a record that isn't there", () => c.Versions("zz_no_such_editor_id"), "zz_no_such_editor_id"),
            ("an EditorID that names several records", () => c.Versions(SharedEditorId(ctx)), "different records"),
            ("two names for one file", () => c.Plugins("atom74.esm", Path.Combine(ctx.Data, "atom74.esm")), "same file"),
            ("two files with no master in common", () => c.PatchVsReference("Starfield.esm", "darkstarpatch_atom74.esm"), "no master in common"),
        };
        foreach (var (what, call, must) in errors)
        {
            CompareException? error = null;
            try { call(); } catch (CompareException e) { error = e; }
            Check(error != null, $"{what}: no error");
            Check(Regex.IsMatch(error!.Line, @"^Error: [^\n]+\. Fix: [^\n]+\.$") && error.Line.Contains(must, StringComparison.Ordinal), $"{what}: the error line is \"{error.Line}\"");
        }
        done.Add($"{errors.Length} wrong calls (a plugin that isn't there, a record that isn't there, an EditorID that names several records, one file named twice, two files that aren't patches for the same masters): each stops with one line \"Error: …. Fix: ….\"");
        return string.Join(". ", done);
    }

    /// <summary>An EditorID that two different records have (in different plugins).</summary>
    static string SharedEditorId(Ctx ctx)
    {
        var index = ctx.Session.Index;
        foreach (var name in new[] { "darkstar.esm", "kgcdoom.esm", "atom74.esm" })
        {
            var p = index.Plugin(name)!; var seg = p.Segment!;
            for (int i = seg.FirstOwn; i < seg.FirstUnresolved; i++)
            {
                string? id = seg.EditorId(i);
                if (id == null || id.Length < 6) continue;
                var keys = index.FindEditorId(id).Select(h => index.Describe(h).FormKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (keys > 1) return id;
            }
        }
        throw new TestFailed("no EditorID that two records share was found");
    }

    // ---------------------------------------------------------------- Time
    public static string Timing(Ctx ctx)
    {
        var c = ctx.Compare;
        static double Ms(Func<object> work, out object result) { var sw = Stopwatch.StartNew(); result = work(); return sw.Elapsed.TotalMilliseconds; }
        var lines = new List<string>();

        // versions of one record
        double v1 = Ms(() => c.Versions("0476C4:Starfield.esm"), out var va);
        double v2 = Ms(() => c.Versions("0476C4:Starfield.esm"), out _);
        double v3 = Ms(() => c.Versions("2377CF:Starfield.esm"), out _);
        var versions = (VersionsAnswer)va;
        lines.Add($"versions of {versions.Label} ({versions.Loaded.Count + versions.NotLoaded.Count} versions): {v1:F0} ms the first time in this process, {v2:F0} ms again; of dn_CommonGun (2377CF:Starfield.esm): {v3:F0} ms the first time");

        // plugin_vs_masters
        double p1 = Ms(() => c.PluginVsMasters("darkstarpatch_atom74.esm"), out var pa);
        double p2 = Ms(() => c.PluginVsMasters("darkstarpatch_atom74.esm"), out _);
        lines.Add($"plugin_vs_masters of darkstarpatch_atom74.esm ({((PluginVsMastersAnswer)pa).Overrides.Count} override): {p1:F0} ms the first time, {p2:F0} ms again");
        var big = BigPatch(ctx);
        double b1 = Ms(() => c.PluginVsMasters(big.Name), out var ba);
        double b2 = Ms(() => c.PluginVsMasters(big.Name), out _);
        var bigAnswer = (PluginVsMastersAnswer)ba;
        double text = Ms(() => bigAnswer.ToText(new TextOptions { Detail = true, MaxChars = int.MaxValue }), out var bt);
        Check(bigAnswer.Overrides.Count == big.Overrides && bigAnswer.Overrides.Count > 1000, $"{big.Name} has {bigAnswer.Overrides.Count} overrides in the answer, {big.Overrides} by the header index");
        lines.Add($"plugin_vs_masters of {big.Name} ({N(bigAnswer.Overrides.Count)} overrides: {N(bigAnswer.Changed.Count())} change something, {N(bigAnswer.Itms.Count())} ITM, {N(bigAnswer.OfNothing.Count())} of nothing, {N(bigAnswer.AnotherType.Count())} of another type, {N(bigAnswer.NotRead.Count())} not read): {b1 / 1000:F2} s the first time, {b2 / 1000:F2} s again ({b2 / bigAnswer.Overrides.Count:F2} ms an override); its text in detail ({N(((string)bt).Length)} characters) {text:F0} ms");

        // plugins: darkstar.esm against a copy
        string copy = ctx.Copy("darkstar.esm", "timing");
        double d1 = Ms(() => c.Plugins("darkstar.esm", copy), out var da);
        var same = (PluginsAnswer)da;
        Check(same.Changed == 0 && same.Added == 0 && same.Removed == 0 && same.Unchanged == same.RecordsA && same.EqualBytes == same.RecordsA, $"darkstar.esm against its copy: {same.Changed} changed, {same.Added} added, {same.Removed} removed, {same.Unchanged} unchanged, {same.EqualBytes} by bytes");
        lines.Add($"plugins on darkstar.esm against a copy of it ({N(same.RecordsA)} records, all told equal from the file's bytes): {d1 / 1000:F2} s");
        // The same with every pair read through Mutagen and compared, with equal bytes not trusted
        c.TrustEqualBytes = false;
        double d2; object db;
        try { d2 = Ms(() => c.Plugins("darkstar.esm", copy), out db); } finally { c.TrustEqualBytes = true; }
        var byValue = (PluginsAnswer)db;
        Check(byValue.Changed == 0 && byValue.Added == 0 && byValue.Removed == 0 && byValue.Unchanged == byValue.RecordsA && byValue.EqualBytes == 0 && !byValue.Rows.Any(),
            $"darkstar.esm against its copy, every pair read: {byValue.Changed} changed, {byValue.Unchanged} unchanged, {byValue.Rows.Count(r => r.Class == PairClass.NotRead)} not read{(byValue.Rows.Count > 0 ? ": " + byValue.Rows[0].Label + " " + (byValue.Rows[0].Problem ?? string.Join(", ", byValue.Rows[0].Diff!.TopFields)) : "")}");
        lines.Add($"the same with every one of the {N(byValue.RecordsA)} pairs read through Mutagen and compared (equal bytes not trusted): {d2 / 1000:F1} s ({d2 / byValue.RecordsA:F2} ms a pair), 0 differences");
        ctx.WriteReport("timing.txt", string.Join('\n', lines) + "\n");
        return string.Join(". ", lines) + $". Session: {ctx.Session.FirstRefresh.Seconds:F1} s for the header index of {ctx.Session.Index.Plugins.Count} plugins at the start; peak working set of the process {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576:N0} MB";
    }
}
