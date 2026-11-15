// plugin_vs_masters: what a plugin changes in its masters. Each override is held against the version before it in
// the plugin's own masters (the last master, in the header's order, that holds the record). Overrides that change
// nothing (ITMs) are listed; overrides of nothing (the master has no such record) are listed.
using System.Text.Json.Nodes;
using SFSearch.Index;

namespace SFSearch.Compare;

public enum OverrideClass
{
    /// <summary>The override changes at least one value.</summary>
    Changed,
    /// <summary>Identical to master: no value differs.</summary>
    Itm,
    /// <summary>The record is of another type than the one it overrides (a patch built for an older master).</summary>
    AnotherType,
    /// <summary>No master holds a record with this FormKey.</summary>
    OfNothing,
    /// <summary>The record or its base couldn't be read.</summary>
    NotRead,
    /// <summary>Only with the xEdit-style verdict: it changes nothing against the version before it, but SF1Edit's
    /// "Identical to Master" filter doesn't list it: it wins over another plugin's change, or it is identical to a
    /// previous override and not to the record's own plugin's version (GAPS S49). The row says which.</summary>
    ItmNotListed,
}

public sealed class OverrideRow
{
    public required string FormKey { get; init; }
    public required string Label { get; init; }
    public required string Signature { get; init; }
    public string? Type { get; set; }
    public OverrideClass Class { get; set; }
    /// <summary>The master whose version the override was held against.</summary>
    public string? Base { get; set; }
    public RecordDiff? Diff { get; set; }
    public string? Problem { get; set; }
    /// <summary>For an ITM: how its record header differs from its master's ("form version 582 vs 581; version control
    /// 0 vs 0x002034C1"), read from the file's bytes. Null when the headers are the same too.</summary>
    public string? HeaderDiffers { get; set; }
    /// <summary>Set when either side is a partial form and was compared merged over the version before it:
    /// "SFBGS00D.esm's partial form merged over Starfield.esm" (GAPS S50).</summary>
    public string? Merged { get; set; }
    /// <summary>Set for an ITM told from its bytes alone: a heavy record, not read through Mutagen (GAPS S47).</summary>
    public string? ByBytes { get; set; }
    /// <summary>For an ITM only by the xEdit-style verdict: the rules that made it equal (GAPS S49).</summary>
    public List<string>? XEditRules { get; set; }
    /// <summary>For ItmNotListed: why SF1Edit doesn't list it.</summary>
    public string? NotListed { get; set; }

    public JsonObject ToJson(bool withChanges = true)
    {
        var o = new JsonObject
        {
            ["record"] = Label, ["formKey"] = FormKey, ["signature"] = Signature, ["type"] = Type,
            ["class"] = Class switch { OverrideClass.Changed => "changed", OverrideClass.Itm => "itm", OverrideClass.AnotherType => "another_type", OverrideClass.OfNothing => "override_of_nothing", OverrideClass.ItmNotListed => "itm_not_listed_by_xedit", _ => "not_read" },
        };
        if (Base != null) o["base"] = Base;
        if (Problem != null) o["problem"] = Problem;
        if (HeaderDiffers != null) o["headerDiffers"] = HeaderDiffers;
        if (Merged != null) o["merged"] = Merged;
        if (ByBytes != null) o["byBytes"] = ByBytes;
        if (NotListed != null) o["notListed"] = NotListed;
        if (XEditRules != null) o["xeditRules"] = new JsonArray(XEditRules.Select(x => (JsonNode)x).ToArray());
        if (Diff != null && withChanges && Class is OverrideClass.Changed or OverrideClass.AnotherType) o["changes"] = Diff.ToJson();
        return o;
    }
}

public sealed class PluginVsMastersAnswer : CompareAnswer
{
    public override string Kind => "plugin_vs_masters";
    public required string Plugin { get; init; }
    public required IReadOnlyList<string> Masters { get; init; }
    public string? Filter { get; init; }
    /// <summary>ITMs as SF1Edit counts them (GAPS S49).</summary>
    public bool LikeXEdit { get; init; }
    /// <summary>The language translated texts were compared in.</summary>
    public string Language { get; init; } = "English";
    /// <summary>Every override, in the plugin's FormKey order.</summary>
    public List<OverrideRow> Overrides { get; } = new();
    /// <summary>The plugin's own records: counted by type, not compared with anything.</summary>
    public Dictionary<string, int> NewRecords { get; } = new(StringComparer.Ordinal);
    public int Unresolved { get; set; }
    /// <summary>The overrides that are world data (cells, worldspaces and what they hold, the navmesh info map, surface
    /// trees), by record type. compare always reads them; the answer says so (GAPS S51).</summary>
    public Dictionary<string, int> WorldOverrides { get; } = new(StringComparer.Ordinal);

    public IEnumerable<OverrideRow> Changed => Overrides.Where(r => r.Class == OverrideClass.Changed);
    public IEnumerable<OverrideRow> Itms => Overrides.Where(r => r.Class == OverrideClass.Itm);
    public IEnumerable<OverrideRow> OfNothing => Overrides.Where(r => r.Class == OverrideClass.OfNothing);
    public IEnumerable<OverrideRow> AnotherType => Overrides.Where(r => r.Class == OverrideClass.AnotherType);
    public IEnumerable<OverrideRow> NotRead => Overrides.Where(r => r.Class == OverrideClass.NotRead);
    public IEnumerable<OverrideRow> NotListed => Overrides.Where(r => r.Class == OverrideClass.ItmNotListed);

    /// <summary>How many overrides change each field, by record type: the summary by field.</summary>
    public List<(string Signature, string Field, string Readable, int Records)> ByField() =>
        Changed.SelectMany(r => r.Diff!.Values.GroupBy(d => d.Top).Select(g => (r.Signature, Field: g.Key, Readable: Head(g.First()))))
            .GroupBy(x => (x.Signature, x.Field)).Select(g => (g.Key.Signature, g.Key.Field, g.First().Readable, Records: g.Count()))
            .OrderByDescending(x => x.Records).ThenBy(x => x.Signature, StringComparer.Ordinal).ThenBy(x => x.Field, StringComparer.Ordinal).ToList();

    protected override void Fill(JsonObject json)
    {
        json["plugin"] = Plugin; json["masters"] = new JsonArray(Masters.Select(m => (JsonNode)m).ToArray());
        if (Filter != null) json["filter"] = Filter;
        if (LikeXEdit) json["likeXEdit"] = true;
        json["counts"] = new JsonObject
        {
            ["overrides"] = Overrides.Count, ["changed"] = Changed.Count(), ["itm"] = Itms.Count(), ["anotherType"] = AnotherType.Count(),
            ["overridesOfNothing"] = OfNothing.Count(), ["notRead"] = NotRead.Count(), ["itmNotListedByXEdit"] = NotListed.Count(), ["newRecords"] = NewRecords.Values.Sum(), ["unresolved"] = Unresolved,
        };
        json["worldOverrides"] = new JsonObject(WorldOverrides.OrderByDescending(k => k.Value).Select(k => new KeyValuePair<string, JsonNode?>(k.Key, k.Value)));
        json["byField"] = new JsonArray(ByField().Select(f => (JsonNode)new JsonObject { ["signature"] = f.Signature, ["field"] = f.Field, ["readable"] = f.Readable, ["records"] = f.Records }).ToArray());
        json["itms"] = new JsonArray(Itms.Select(r => (JsonNode)r.ToJson()).ToArray());
        json["overridesOfNothing"] = new JsonArray(OfNothing.Select(r => (JsonNode)r.ToJson()).ToArray());
        json["overrides"] = new JsonArray(Overrides.Where(r => r.Class is not (OverrideClass.Itm or OverrideClass.OfNothing)).Select(r => (JsonNode)r.ToJson()).ToArray());
        json["newRecords"] = new JsonObject(NewRecords.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new KeyValuePair<string, JsonNode?>(k.Key, k.Value)));
    }

    protected override void Render(Body body, TextOptions o)
    {
        body.RowNoun = "records";
        int changed = Changed.Count(), itm = Itms.Count(), nothing = OfNothing.Count(), other = AnotherType.Count(), unread = NotRead.Count();
        body.Head.Add($"{Plugin} against its masters ({string.Join(", ", Masters)}){(Filter != null ? $", records matching \"{Filter}\"" : "")}:");
        body.Head.Add($"{Plural(Overrides.Count, "override")}: {N(changed)} {(changed == 1 ? "changes" : "change")} something, {N(itm)} {(itm == 1 ? "changes" : "change")} nothing (ITM), {N(nothing)} {(nothing == 1 ? "overrides" : "override")} nothing" +
            (other > 0 ? $", {N(other)} have a different type than their base" : "") + (unread > 0 ? $", {N(unread)} couldn't be read" : "") + (NotListed.Any() ? $", {N(NotListed.Count())} change nothing but aren't SF1Edit ITMs" : "") +
            $". {Plural(NewRecords.Values.Sum(), "new record")} of its own{(Filter == null ? "" : " in all")} (not compared)." + (Unresolved > 0 ? $" {N(Unresolved)} records have a FormID that can't be resolved." : ""));
        var byField = ByField();
        // What was read, said plainly: world data is in, and what wasn't compared is named (GAPS S51)
        int world = WorldOverrides.Values.Sum();
        if (world > 0)
            body.Head.Add($"World data included: {N(world)} of the {N(Overrides.Count)} overrides are world records ({string.Join(", ", WorldOverrides.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key} {N(k.Value)}"))}{(WorldOverrides.Count > 6 ? ", …" : "")}). compare always reads them; check2 leaves them out unless --world.");
        if (unread > 0)
            body.Head.Add($"Left out: {Plural(unread, "override")} couldn't be compared (listed under \"Couldn't be read\" with the reason; a heavy record is read when asked for by itself with allow_heavy).");
        if (LikeXEdit) body.Head.Add(HeaderNumbers.XEditExplanation);
        int merged = Overrides.Count(r => r.Merged != null);
        if (merged > 0) body.Head.Add($"{Plural(merged, "override")} {(merged == 1 ? "was" : "were")} compared through a partial form. " + HeaderNumbers.PartialExplanation);
        int rewritten = Overrides.Count(r => r.Diff != null && r.Diff.RewrittenFields.Count > 0);
        if (rewritten > 0)
            body.Head.Add($"{Plural(rewritten, "override")} hold{(rewritten == 1 ? "s" : "")} a field that is the same value written another way (a text every language shows the same, held as localized strings on one side and as the text itself on the other; or a data block written shorter or longer). It is listed with the record, under \"the same value, written another way\".");
        if (byField.Count > 0)
        {
            body.Head.Add("Summary by field (how many overrides change it):");
            body.Head.Add("| Type | Field | Overrides |");
            body.Head.Add("| :--- | :--- | ---: |");
            int show = o.Detail ? byField.Count : Math.Min(byField.Count, 25);
            foreach (var f in byField.Take(show)) body.Head.Add($"| {f.Signature} | {f.Readable} | {N(f.Records)} |");
            if (show < byField.Count) body.Head.Add($"| … | {N(byField.Count - show)} more fields (detail lists them) | |");
        }
        if (itm > 0) body.Rows.Add($"Overrides that change nothing (ITM), {N(itm)}:" + (Itms.Any(r => r.HeaderDiffers != null) ? " " + HeaderNumbers.ItmExplanation : ""));
        foreach (var r in Itms)
            body.Rows.Add($"  {r.Label} {r.Signature}: " + HeaderNumbers.ItmText(r.Base!, r.Diff!.RewrittenFields, r.HeaderDiffers, r.Merged, r.ByBytes, r.XEditRules));
        int notListed = NotListed.Count();
        if (notListed > 0) body.Rows.Add($"Overrides that change nothing against the version before them but that SF1Edit doesn't list as ITMs, {N(notListed)}:");
        foreach (var r in NotListed) body.Rows.Add($"  {r.Label} {r.Signature}: {r.NotListed}{(r.XEditRules is { Count: > 0 } x ? $" (like_xedit: {string.Join("; ", x)})" : "")}");
        if (nothing > 0) body.Rows.Add($"Overrides of nothing (no master holds such a record), {N(nothing)}:");
        foreach (var r in OfNothing) body.Rows.Add($"  {r.Label} {r.Signature}");
        if (other > 0) body.Rows.Add($"Overrides of a different type than their base, {N(other)}:");
        foreach (var r in AnotherType) body.Rows.Add($"  {r.Label}: a {r.Diff!.SignatureB} {r.Diff.TypeB} here, a {r.Diff.SignatureA} {r.Diff.TypeA} in {r.Base}");
        if (unread > 0) body.Rows.Add($"Couldn't be read, {N(unread)}:");
        foreach (var r in NotRead) body.Rows.Add($"  {r.Label} {r.Signature}: {r.Problem}");
        if (changed > 0) body.Rows.Add($"Overrides that change something, {N(changed)} (base → {Plugin}):");
        foreach (var r in Changed)
        {
            string against = r.Merged == null ? r.Base! : $"{r.Base} ({r.Merged})";
            if (!o.Detail) { body.Rows.Add($"  {r.Label} {r.Signature}, against {against}: {FieldNames(r.Diff!)}"); continue; }
            body.Rows.Add($"  {r.Label} {r.Signature}, against {against}:\n" + string.Join('\n', FieldLines(r.Diff!, o, "      ")));
        }
    }
}

public sealed partial class Comparer
{
    /// <summary>What a plugin changes in its masters.</summary>
    /// <param name="plugin">A plugin's name in Data, or a file's path.</param>
    /// <param name="filter">Only the overrides whose EditorID holds this text, whose FormKey is this, or whose record type (WEAP) is this.</param>
    /// <param name="likeXEdit">Count ITMs as SF1Edit does: differences xEdit leaves aside don't count (GAPS S49).</param>
    public PluginVsMastersAnswer PluginVsMasters(string plugin, string? filter = null, bool likeXEdit = false)
    {
        Index.Refresh();
        var side = Session.Side(plugin);
        return Run(new[] { side }, () =>
        {
            var seg = side.Segment;
            var answer = new PluginVsMastersAnswer { Plugin = side.Shown, Masters = side.Masters.ToList(), Filter = filter, LikeXEdit = likeXEdit, Unresolved = seg.Unresolved, Language = Session.Language.ToString() };
            var label = Session.Labeller(side);
            var options = Options(label);
            var usedMasters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = seg.FirstOwn; i < seg.FirstUnresolved; i++)
            {
                string sig = seg.SignatureOf(seg[i]);
                answer.NewRecords[sig] = answer.NewRecords.GetValueOrDefault(sig) + 1;
            }
            for (int i = 0; i < seg.FirstOwn; i++)
            {
                var r = new RecordRef(side.Plugin.Id, i);
                var info = side.Index.Describe(r);
                if (filter != null && !Matches(info, filter)) continue;
                var row = new OverrideRow { FormKey = info.FormKey, Label = info.Label, Signature = info.Signature };
                answer.Overrides.Add(row);
                var e = seg[i];
                if (IsWorld(seg, e)) answer.WorldOverrides[info.Signature] = answer.WorldOverrides.GetValueOrDefault(info.Signature) + 1;
                var before = Before(side, seg.Names[e.Owner], e.LocalId);
                if (before == null) { row.Class = OverrideClass.OfNothing; continue; }
                row.Base = before.Value.Plugin;
                usedMasters.Add(row.Base);
                string? bytesSaid = null;
                try
                {
                    // A heavy record isn't read through Mutagen without allow_heavy: the same bytes as its base are an ITM all the same (GAPS S47)
                    bool? same = CompareSession.IsHeavy(side.Engine, r) || CompareSession.IsHeavy(before.Value.Engine, before.Value.Ref)
                        ? CompareSession.SameBytes(side.Engine, r, before.Value.Engine, before.Value.Ref) : null;
                    if (same == false) bytesSaid = CompareSession.BytesDiffer(side.Engine, r, before.Value.Engine, before.Value.Ref, row.Base) + ", so it isn't an ITM. ";
                    if (same == true)
                    {
                        string type = Session.Schema.TypesOf(info.Signature).FirstOrDefault() ?? info.Signature;
                        row.Type = type; row.Class = OverrideClass.Itm;
                        row.Diff = new RecordDiff { TypeA = type, TypeB = type, SignatureA = info.Signature, SignatureB = info.Signature };
                        row.ByBytes = CompareSession.BytesNote(side.Engine, r);
                        row.HeaderDiffers = HeaderNumbers.Differences(side.Engine, r, before.Value.Engine, before.Value.Ref);
                        continue;
                    }
                    var mine = Session.View(side.Engine, r);
                    row.Type = mine.Type;
                    // A partial form on either side is compared as the game merges it (GAPS S50)
                    var theirs = Session.Effective(before.Value.Engine, before.Value.Ref);
                    mine = Session.Merge(mine, theirs);
                    row.Merged = Merged(theirs, mine);
                    usedMasters.UnionWith(mine.Through); usedMasters.UnionWith(theirs.Through);
                    row.Diff = Diff(theirs, mine, options);
                    row.Class = row.Diff.DifferentTypes ? OverrideClass.AnotherType : row.Diff.Same ? OverrideClass.Itm : OverrideClass.Changed;
                    // The xEdit-style verdict (GAPS S49): its rules, its master (the record's own plugin), its filter
                    if (likeXEdit && row.Class is OverrideClass.Changed or OverrideClass.Itm && Session.LikeXEdit(side, mine, row.Diff, row.Base!) is { } verdict)
                    {
                        row.Class = verdict.Itm ? OverrideClass.Itm : OverrideClass.ItmNotListed;
                        row.XEditRules = verdict.Rules.Count > 0 ? verdict.Rules : null;
                        row.NotListed = verdict.NotListed;
                    }
                    if (row.Class == OverrideClass.Itm) row.HeaderDiffers = HeaderNumbers.Differences(side.Engine, r, before.Value.Engine, before.Value.Ref);
                }
                catch (CompareException x) { row.Class = OverrideClass.NotRead; row.Problem = bytesSaid + x.Message; }
            }
            AddMasterSources(answer, side, usedMasters);
            answer.Read.Add($"{answer.Overrides.Count:N0} override{(answer.Overrides.Count == 1 ? "" : "s")} of {side.Name}, each with the version before it in its masters");
            return answer;
        });
    }
}
