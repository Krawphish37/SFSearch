// versions: one record across the load order. Every plugin that holds it, in order; who wins; what each changes
// against the one before it. Plugins in Data that hold it and aren't loaded are listed apart: they take no part in
// who wins.
using System.Text.Json.Nodes;
using SFSearch.Index;

namespace SFSearch.Compare;

public sealed class VersionRow
{
    public required string Plugin { get; init; }
    /// <summary>The record as this plugin has it: EditorID (FormID:plugin).</summary>
    public required string Label { get; init; }
    public required string Signature { get; init; }
    public string? Type { get; set; }
    /// <summary>Place in the load order, -1 when the plugin isn't listed.</summary>
    public int Position { get; init; }
    public string Slot { get; init; } = "";
    public bool Loaded { get; init; }
    /// <summary>The plugin the record belongs to.</summary>
    public bool IsOwner { get; init; }
    public bool IsWinner { get; init; }
    /// <summary>The plugin this version was compared with: the loaded version before it.</summary>
    public string? Against { get; set; }
    public RecordDiff? Diff { get; set; }
    /// <summary>For the third version on: the record's own fields that differ from the first version.</summary>
    public List<string>? FieldsAgainstFirst { get; set; }
    public string? FirstPlugin { get; set; }
    /// <summary>Why this version couldn't be read or compared.</summary>
    public string? Problem { get; set; }
    /// <summary>Set when this version is a partial form: it was compared merged over the version before it (GAPS S50).</summary>
    public string? Merged { get; set; }

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["plugin"] = Plugin, ["record"] = Label, ["signature"] = Signature, ["type"] = Type, ["position"] = Position, ["slot"] = Slot,
            ["loaded"] = Loaded, ["owner"] = IsOwner, ["winner"] = IsWinner,
        };
        if (Against != null) o["against"] = Against;
        if (Diff != null) o["changes"] = Diff.ToJson();
        if (FieldsAgainstFirst != null) { o["againstFirst"] = FirstPlugin; o["fieldsAgainstFirst"] = new JsonArray(FieldsAgainstFirst.Select(f => (JsonNode)f).ToArray()); }
        if (Problem != null) o["problem"] = Problem;
        if (Merged != null) o["merged"] = Merged;
        return o;
    }
}

public sealed class VersionsAnswer : CompareAnswer
{
    public override string Kind => "versions";
    public required string FormKey { get; init; }
    /// <summary>The record as the game uses it (the winner's EditorID), or the owner's when no loaded plugin holds it.</summary>
    public required string Label { get; init; }
    public required string Signature { get; init; }
    /// <summary>The plugin whose version the game uses. Null when no loaded plugin holds the record.</summary>
    public string? Winner { get; init; }
    /// <summary>The loaded plugins that hold the record, in load order.</summary>
    public List<VersionRow> Loaded { get; } = new();
    /// <summary>The plugins in Data that hold it and aren't loaded.</summary>
    public List<VersionRow> NotLoaded { get; } = new();

    protected override void Fill(JsonObject json)
    {
        json["formKey"] = FormKey; json["record"] = Label; json["signature"] = Signature; json["winner"] = Winner;
        json["versions"] = new JsonArray(Loaded.Select(r => (JsonNode)r.ToJson()).ToArray());
        json["notLoaded"] = new JsonArray(NotLoaded.Select(r => (JsonNode)r.ToJson()).ToArray());
    }

    protected override void Render(Body body, TextOptions o)
    {
        body.RowNoun = "versions";
        string type = Loaded.Concat(NotLoaded).FirstOrDefault(r => r.Type != null)?.Type ?? "";
        body.Head.Add($"{Label}, {Signature} {type}".TrimEnd() + $": {Plural(Loaded.Count, "loaded plugin")} hold{(Loaded.Count == 1 ? "s" : "")} it" +
            (NotLoaded.Count > 0 ? $", and {N(NotLoaded.Count)} in Data that {(NotLoaded.Count == 1 ? "isn't" : "aren't")} loaded" : "") + ".");
        body.Head.Add(Winner == null ? "No loaded plugin holds it, so the game doesn't have this record." : $"Winner: {Winner}" + (Loaded.Count == 1 ? " (nothing else in the load order holds it)." : "."));
        foreach (var r in Loaded) body.Rows.Add(Row(r, o));
        if (NotLoaded.Count > 0)
        {
            body.Rows.Add($"Not loaded ({N(NotLoaded.Count)}): in Data, not in the load order. They take no part in who wins.");
            foreach (var r in NotLoaded) body.Rows.Add(Row(r, o));
        }
    }

    static string Row(VersionRow r, TextOptions o)
    {
        var lines = new List<string>();
        string where = r.Loaded ? $"load order {r.Position}{(r.Slot.Length > 0 ? ", slot " + r.Slot : "")}" : "not loaded";
        string role = (r.IsOwner ? "the record's own plugin" : "override") + (r.IsWinner ? ", WINNER" : "");
        string head = $"- {r.Plugin} ({where}; {role}{(r.Merged != null ? "; a partial form, compared merged over the version before it" : "")})";
        if (r.Problem != null) lines.Add($"{head}: {r.Problem}");
        else if (r.Diff == null) lines.Add($"{head}: the first version.");
        else if (r.Diff.DifferentTypes) lines.Add($"{head}: a record of a different type than in {r.Against}.");
        else if (r.Diff.Same) lines.Add($"{head}: changes nothing against {r.Against} (an identical copy{(r.Diff.RewrittenFields.Count > 0 ? $" in every value; the same value, written another way: {string.Join(", ", r.Diff.RewrittenFields)}" : "")}).");
        else lines.Add($"{head}: changes {Plural(r.Diff.TopFields.Count, "field")} against {r.Against}:");
        if (r.Diff != null && !r.Diff.Same) lines.AddRange(FieldLines(r.Diff, o, "    "));
        else if (r.Diff != null && o.Detail) lines.AddRange(FieldLines(r.Diff, o, "    "));
        if (r.FieldsAgainstFirst != null)
            lines.Add($"    against {r.FirstPlugin}, the first version: {(r.FieldsAgainstFirst.Count == 0 ? "nothing differs" : string.Join(", ", r.FieldsAgainstFirst))}");
        return string.Join('\n', lines);
    }
}

public sealed partial class Comparer
{
    /// <summary>One record across the load order.</summary>
    /// <param name="record">A FormKey (0476C4:Starfield.esm) or an EditorID. An EditorID that names several records lists them and picks none.</param>
    /// <param name="inPlugin">Look the EditorID or local ID up in this plugin only.</param>
    public VersionsAnswer Versions(string record, string? inPlugin = null)
    {
        Index.Refresh();
        string formKey = ResolveFormKey(record, inPlugin);
        var versions = Index.Versions(formKey) ?? throw new CompareException($"no plugin in Data holds {formKey}", "check the FormKey: six digits, a colon, the plugin that owns the record");
        HeaderIndex.TryParseFormKey(formKey, out var owner, out _);
        var holders = versions.Holders.Select(h => (Ref: h, Plugin: Index.Plugins[h.Plugin])).ToList();
        var sides = holders.Select(h => Session.Side(h.Plugin.Name)).ToArray();

        var result = Run(sides, () =>
        {
            var label = Session.Labeller();
            var winnerRef = versions.Winner >= 0 ? versions.Holders[versions.Winner] : versions.Holders[0];
            var winnerInfo = Index.Describe(winnerRef);
            var answer = new VersionsAnswer
            {
                FormKey = winnerInfo.FormKey, Label = winnerInfo.Label, Signature = winnerInfo.Signature,
                Winner = versions.Winner >= 0 ? holders[versions.Winner].Plugin.Name : null,
            };
            RecordView? first = null, previous = null;
            foreach (var (r, p) in holders)
            {
                var info = Index.Describe(r);
                bool loaded = p.Order.Enabled;
                var row = new VersionRow
                {
                    Plugin = p.Name, Label = info.Label, Signature = info.Signature, Position = p.Order.Position, Slot = p.Order.Slot, Loaded = loaded,
                    IsOwner = p.Name.Equals(owner, StringComparison.OrdinalIgnoreCase), IsWinner = versions.Winner >= 0 && versions.Holders[versions.Winner] == r,
                };
                (loaded ? answer.Loaded : answer.NotLoaded).Add(row);
                RecordView view;
                try { view = Session.View(Session.Engine, r); }
                catch (CompareException e) { row.Problem = e.Message; continue; }
                row.Type = view.Type;
                // A plugin that isn't loaded is held against the first version: there is no "one before it" for it
                var against = loaded ? previous : first;
                if (against != null)
                {
                    // A partial form is what the game makes of it: its fields over the version before it (GAPS S50)
                    view = Session.Merge(view, against);
                    row.Merged = view.Merged;
                    row.Against = against.Plugin;
                    row.Diff = Diff(against, view, Options(label));
                    if (loaded && first != null && !ReferenceEquals(first, previous))
                    {
                        var d = Diff(first, view, Options(label));
                        row.FirstPlugin = first.Plugin;
                        row.FieldsAgainstFirst = d.DifferentTypes ? new List<string> { "(another type)" } : d.TopFields;
                    }
                }
                if (loaded) { previous = view; first ??= view; }
            }
            // No loaded plugin holds it: the ones that aren't loaded are held against the first of them
            if (answer.Loaded.Count == 0 && answer.NotLoaded.Count > 1)
            {
                RecordView? head = null;
                foreach (var (r, _) in holders)
                {
                    var row = answer.NotLoaded.First(x => x.Plugin == Index.Plugins[r.Plugin].Name);
                    if (row.Problem != null) continue;
                    var view = Session.View(Session.Engine, r);
                    if (head == null) { head = view; continue; }
                    row.Against = head.Plugin; row.Diff = Diff(head, view, Options(label));
                }
            }
            answer.Read.Add($"{holders.Count} version{(holders.Count == 1 ? "" : "s")} of one {winnerInfo.Signature}");
            return answer;
        });
        result.Sources.Add(Session.LoadOrderLine());
        return result;
    }

    /// <summary>A FormKey from what the caller gave: a FormKey, or an EditorID or local ID that names one record.</summary>
    string ResolveFormKey(string record, string? inPlugin)
    {
        record = record.Trim();
        if (inPlugin != null)
        {
            var side = Session.Side(inPlugin);
            return side.Index.Describe(Session.FindIn(side, record)).FormKey;
        }
        if (HeaderIndex.TryParseFormKey(record, out var plugin, out var id))
        {
            var p = Index.Plugin(plugin) ?? throw new CompareException($"there is no plugin named {plugin} in the Data folder", "a FormKey names the plugin that owns the record");
            return $"{id:X6}:{p.Name}";
        }
        var hits = Index.FindEditorId(record);
        var keys = hits.Select(h => Index.Describe(h)).GroupBy(d => d.FormKey, StringComparer.OrdinalIgnoreCase).ToList();
        if (keys.Count == 0)
        {
            if (record.Length <= 8 && record.All(Uri.IsHexDigit))
            {
                var byId = Index.FindLocalId(Convert.ToUInt32(record, 16) & 0xFFFFFF).Select(h => Index.Describe(h)).ToList();
                if (byId.Count == 1) return byId[0].FormKey;
                if (byId.Count > 1)
                    throw new CompareException($"several plugins have a record with the ID {record}: " + string.Join("; ", byId.Take(8).Select(d => $"{d.Label} {d.Signature}")) + (byId.Count > 8 ? "; …" : ""),
                        "name the plugin: give the FormKey, as in " + byId[0].FormKey);
            }
            throw new CompareException($"no plugin in Data has a record \"{record}\"", "give a FormKey (0476C4:Starfield.esm) or an EditorID");
        }
        if (keys.Count > 1)
            throw new CompareException($"\"{record}\" is the EditorID of {keys.Count} different records: " + string.Join("; ", keys.Take(8).Select(g => $"{g.First().Label} {g.First().Signature}")) + (keys.Count > 8 ? "; …" : ""),
                "name one by its FormKey");
        return keys[0].First().FormKey;
    }
}
