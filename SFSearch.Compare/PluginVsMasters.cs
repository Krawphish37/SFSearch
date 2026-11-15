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

    public JsonObject ToJson(bool withChanges = true)
    {
        var o = new JsonObject
        {
            ["record"] = Label, ["formKey"] = FormKey, ["signature"] = Signature, ["type"] = Type,
            ["class"] = Class switch { OverrideClass.Changed => "changed", OverrideClass.Itm => "itm", OverrideClass.AnotherType => "another_type", OverrideClass.OfNothing => "override_of_nothing", _ => "not_read" },
        };
        if (Base != null) o["base"] = Base;
        if (Problem != null) o["problem"] = Problem;
        if (HeaderDiffers != null) o["headerDiffers"] = HeaderDiffers;
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
    /// <summary>The language translated texts were compared in.</summary>
    public string Language { get; init; } = "English";
    /// <summary>Every override, in the plugin's FormKey order.</summary>
    public List<OverrideRow> Overrides { get; } = new();
    /// <summary>The plugin's own records: counted by type, not compared with anything.</summary>
    public Dictionary<string, int> NewRecords { get; } = new(StringComparer.Ordinal);
    public int Unresolved { get; set; }

    public IEnumerable<OverrideRow> Changed => Overrides.Where(r => r.Class == OverrideClass.Changed);
    public IEnumerable<OverrideRow> Itms => Overrides.Where(r => r.Class == OverrideClass.Itm);
    public IEnumerable<OverrideRow> OfNothing => Overrides.Where(r => r.Class == OverrideClass.OfNothing);
    public IEnumerable<OverrideRow> AnotherType => Overrides.Where(r => r.Class == OverrideClass.AnotherType);
    public IEnumerable<OverrideRow> NotRead => Overrides.Where(r => r.Class == OverrideClass.NotRead);

    /// <summary>How many overrides change each field, by record type: the summary by field.</summary>
    public List<(string Signature, string Field, string Readable, int Records)> ByField() =>
        Changed.SelectMany(r => r.Diff!.Values.GroupBy(d => d.Top).Select(g => (r.Signature, Field: g.Key, Readable: Head(g.First()))))
            .GroupBy(x => (x.Signature, x.Field)).Select(g => (g.Key.Signature, g.Key.Field, g.First().Readable, Records: g.Count()))
            .OrderByDescending(x => x.Records).ThenBy(x => x.Signature, StringComparer.Ordinal).ThenBy(x => x.Field, StringComparer.Ordinal).ToList();

    protected override void Fill(JsonObject json)
    {
        json["plugin"] = Plugin; json["masters"] = new JsonArray(Masters.Select(m => (JsonNode)m).ToArray());
        if (Filter != null) json["filter"] = Filter;
        json["counts"] = new JsonObject
        {
            ["overrides"] = Overrides.Count, ["changed"] = Changed.Count(), ["itm"] = Itms.Count(), ["anotherType"] = AnotherType.Count(),
            ["overridesOfNothing"] = OfNothing.Count(), ["notRead"] = NotRead.Count(), ["newRecords"] = NewRecords.Values.Sum(), ["unresolved"] = Unresolved,
        };
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
            (other > 0 ? $", {N(other)} have a different type than their base" : "") + (unread > 0 ? $", {N(unread)} couldn't be read" : "") +
            $". {Plural(NewRecords.Values.Sum(), "new record")} of its own{(Filter == null ? "" : " in all")} (not compared)." + (Unresolved > 0 ? $" {N(Unresolved)} records have a FormID that can't be resolved." : ""));
        var byField = ByField();
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
            body.Rows.Add($"  {r.Label} {r.Signature}: " + HeaderNumbers.ItmText(r.Base!, r.Diff!.RewrittenFields, r.HeaderDiffers));
        if (nothing > 0) body.Rows.Add($"Overrides of nothing (no master holds such a record), {N(nothing)}:");
        foreach (var r in OfNothing) body.Rows.Add($"  {r.Label} {r.Signature}");
        if (other > 0) body.Rows.Add($"Overrides of a different type than their base, {N(other)}:");
        foreach (var r in AnotherType) body.Rows.Add($"  {r.Label}: a {r.Diff!.SignatureB} {r.Diff.TypeB} here, a {r.Diff.SignatureA} {r.Diff.TypeA} in {r.Base}");
        if (unread > 0) body.Rows.Add($"Couldn't be read, {N(unread)}:");
        foreach (var r in NotRead) body.Rows.Add($"  {r.Label} {r.Signature}: {r.Problem}");
        if (changed > 0) body.Rows.Add($"Overrides that change something, {N(changed)} (base → {Plugin}):");
        foreach (var r in Changed)
        {
            if (!o.Detail) { body.Rows.Add($"  {r.Label} {r.Signature}, against {r.Base}: {FieldNames(r.Diff!)}"); continue; }
            body.Rows.Add($"  {r.Label} {r.Signature}, against {r.Base}:\n" + string.Join('\n', FieldLines(r.Diff!, o, "      ")));
        }
    }
}

public sealed partial class Comparer
{
    /// <summary>What a plugin changes in its masters.</summary>
    /// <param name="plugin">A plugin's name in Data, or a file's path.</param>
    /// <param name="filter">Only the overrides whose EditorID holds this text, whose FormKey is this, or whose record type (WEAP) is this.</param>
    public PluginVsMastersAnswer PluginVsMasters(string plugin, string? filter = null)
    {
        Index.Refresh();
        var side = Session.Side(plugin);
        return Run(new[] { side }, () =>
        {
            var seg = side.Segment;
            var answer = new PluginVsMastersAnswer { Plugin = side.Shown, Masters = side.Masters.ToList(), Filter = filter, Unresolved = seg.Unresolved, Language = Session.Language.ToString() };
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
                var before = Before(side, seg.Names[e.Owner], e.LocalId);
                if (before == null) { row.Class = OverrideClass.OfNothing; continue; }
                row.Base = before.Value.Plugin;
                usedMasters.Add(row.Base);
                try
                {
                    var mine = Session.View(side.Engine, r);
                    row.Type = mine.Type;
                    var theirs = Session.View(before.Value.Engine, before.Value.Ref);
                    row.Diff = Diff(theirs, mine, options);
                    row.Class = row.Diff.DifferentTypes ? OverrideClass.AnotherType : row.Diff.Same ? OverrideClass.Itm : OverrideClass.Changed;
                    if (row.Class == OverrideClass.Itm) row.HeaderDiffers = HeaderNumbers.Differences(side.Engine, r, before.Value.Engine, before.Value.Ref);
                }
                catch (CompareException x) { row.Class = OverrideClass.NotRead; row.Problem = x.Message; }
            }
            AddMasterSources(answer, side, usedMasters);
            answer.Read.Add($"{answer.Overrides.Count:N0} override{(answer.Overrides.Count == 1 ? "" : "s")} of {side.Name}, each with the version before it in its masters");
            return answer;
        });
    }
}
