// sources: how does the player get each item a plugin adds? From every item the links are followed backwards,
// through leveled lists, form lists and outfits, until they reach something the player meets: a quest (a stage
// reward, an alias, a script property), a recipe with its conditions and workbench, a vendor or container, a placed
// copy in a cell, an NPC. For each item the answer says whether any path ends at something the player can reach.
// "What points at this" comes from an IBackLinks (BackLinks.cs).
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SFSearch.Compare;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Reports;

using static ReportAnswer;

public sealed class SourcesOptions
{
    /// <summary>The record types that count as items (signatures). Null: weapons and armor.</summary>
    public IReadOnlyList<string>? Signatures { get; init; }
    /// <summary>Only this item (an EditorID or a FormKey).</summary>
    public string? Item { get; init; }
    /// <summary>More plugins whose records are searched for sources.</summary>
    public IReadOnlyList<string> With { get; init; } = Array.Empty<string>();
    /// <summary>Also search every loaded plugin that has the plugin as a master (its patches). On by default.</summary>
    public bool Dependents { get; init; } = true;
    /// <summary>How many lists deep the walk goes.</summary>
    public int Depth { get; init; } = 6;
    /// <summary>What answers "what points at this". Null: a search of the searched plugins' bytes for each record asked about (<see cref="ByteBackLinks"/>).</summary>
    public Func<ReadScope, IReadOnlyList<(RecordEngine Engine, IndexedPlugin Plugin)>, IBackLinks>? BackLinks { get; init; }
}

/// <summary>Can the player get at a source? Yes, no, or it can't be told from the records.</summary>
public enum Reach { No, Unclear, Yes }

public sealed class ItemSource
{
    /// <summary>Recipe, Quest, Vendor, Injected, Container, Placed, NPC, Script, Unused list, Other.</summary>
    public required string Kind { get; init; }
    public required string Text { get; init; }
    /// <summary>A shorter text for the compact answer, when the full one is long (a script that adds to dozens of lists).</summary>
    public string? Compact { get; init; }
    /// <summary>The record the source is (the recipe, the quest, the container; for a placed copy, the cell), as a FormKey.</summary>
    public string? Record { get; init; }
    /// <summary>The lists between the item and the source, from the item outward (the shortest route found).</summary>
    public List<string> Via { get; init; } = new();
    public List<string> ViaLabels { get; init; } = new();
    /// <summary>How many routes through different lists reach the same source.</summary>
    public int Routes { get; set; } = 1;
    public Reach Reach { get; init; }
    /// <summary>For a recipe: its conditions and workbench.</summary>
    public List<ConditionInfo>? Conditions { get; init; }
    public string? Workbench { get; init; }
    /// <summary>For a script that adds the item to lists: those lists (FormKeys).</summary>
    public List<string>? Targets { get; init; }
    /// <summary>For a quest stage reward: the stage.</summary>
    public int? Stage { get; init; }
    /// <summary>For a placed copy or a container: how many copies, and the cells.</summary>
    public int Count { get; init; }
    /// <summary>For a vendor, how it was found: "faction" (a faction's merchant container is a placed copy of the
    /// container) or "editorId" (only an EditorID with "Vendor" in it; no faction names a chest of it).</summary>
    public string? FoundBy { get; init; }
    /// <summary>For a vendor found by its faction: the factions and their chests.</summary>
    public List<VendorChest>? Vendors { get; init; }
    /// <summary>For a script that adds the item to lists, how that was told (<see cref="ListTargets.FoundBy"/>): "code"
    /// (its compiled code calls AddForm on the list with the item), "struct" or "name" (the older rules, when the code
    /// can't be read or shows no such call).</summary>
    public string? InjectorFoundBy { get; init; }

    public bool Gated => Conditions != null && Conditions.Any(c => c.Function != "GetLevel");

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["kind"] = Kind, ["text"] = Text, ["reachable"] = Reach.ToString().ToLowerInvariant() };
        if (Record != null) o["formKey"] = Record;
        if (ViaLabels.Count > 0) o["via"] = new JsonArray(ViaLabels.Select(v => (JsonNode)v).ToArray());
        if (Routes > 1) o["routes"] = Routes;
        if (Workbench != null) o["workbench"] = Workbench;
        if (Conditions != null) o["conditions"] = new JsonArray(Conditions.Select(c => (JsonNode)c.ToJson()).ToArray());
        if (Stage != null) o["stage"] = Stage;
        if (Targets != null) o["addsTo"] = new JsonArray(Targets.Select(t => (JsonNode)t).ToArray());
        if (FoundBy != null) o["foundBy"] = FoundBy;
        if (InjectorFoundBy != null) o["injectorFoundBy"] = InjectorFoundBy;
        if (Vendors != null) o["vendors"] = new JsonArray(Vendors.Select(v => (JsonNode)v.ToJson()).ToArray());
        return o;
    }
}

public sealed class SourcedItem
{
    public required string FormKey { get; init; }
    public required string Label { get; init; }
    public string EditorId { get; init; } = "";
    public string Name { get; init; } = "";
    public required string Signature { get; init; }
    public required string Type { get; init; }
    /// <summary>NonPlayable, "race X" (an armor for another race than the human one).</summary>
    public List<string> Flags { get; init; } = new();
    public List<ItemSource> Sources { get; } = new();
    /// <summary>Records that only test for the item in a condition: not sources.</summary>
    public List<string> CheckedBy { get; } = new();

    /// <summary>Whether any path ends at something the player can reach.</summary>
    public Reach Reach => Sources.Count == 0 ? Reach.No : Sources.Max(s => s.Reach);

    internal static readonly string[] KindOrder = { "Recipe", "Quest", "Vendor", "Injected", "Container", "Placed", "NPC", "Script", "Unused list", "Other" };

    /// <summary>The item's sources in a few words: Craftable (gated), Quest, Vendor, World loot, …</summary>
    public string Verdict
    {
        get
        {
            var kinds = Sources.Select(s => s.Kind).Distinct().OrderBy(k => Array.IndexOf(KindOrder, k)).ToList();
            var words = new List<string>();
            if (Flags.Contains("NonPlayable")) words.Add("Unplayable");
            foreach (var k in kinds)
                switch (k)
                {
                    case "Recipe": words.Add(Sources.Any(s => s.Kind == "Recipe" && s.Gated) ? "Craftable (gated)" : "Craftable"); break;
                    case "Quest": words.Add("Quest"); break;
                    case "Vendor": words.Add("Vendor"); break;
                    case "Injected": words.Add("Injected into lists"); break;
                    case "Container": case "Placed": if (!words.Contains("World loot")) words.Add("World loot"); break;
                    case "NPC": words.Add(Sources.Where(s => s.Kind == "NPC").All(s => s.Reach == Reach.No) ? "NPC skin" : "On NPCs"); break;
                    case "Script": words.Add("Script"); break;
                }
            string? race = Flags.FirstOrDefault(f => f.StartsWith("race ", StringComparison.Ordinal));
            if (race != null) words.Add(race);
            if (words.Count == 0 || words.All(w => w.StartsWith("race ", StringComparison.Ordinal)))
                words.Insert(0, Sources.Any(s => s.Kind == "Other") ? "Used by other records" : Sources.Any(s => s.Kind == "Unused list") ? "Unused list only" : "No source found");
            return string.Join(", ", words);
        }
    }

    /// <summary>What gates the item's recipes: their conditions other than the player's level.</summary>
    public string Gate => string.Join(" | ", Sources.Where(s => s.Kind == "Recipe" && s.Gated)
        .Select(s => string.Join(" · ", s.Conditions!.Where(c => c.Function != "GetLevel").Select(c => c.Text))).Distinct());

    public string ReachText => Reach switch { Reach.Yes => "yes", Reach.Unclear => "unclear", _ => "no" };

    public JsonObject ToJson() => new()
    {
        ["record"] = Label, ["formKey"] = FormKey, ["name"] = Name, ["signature"] = Signature, ["type"] = Type,
        ["flags"] = new JsonArray(Flags.Select(f => (JsonNode)f).ToArray()), ["verdict"] = Verdict, ["gate"] = Gate, ["reachable"] = ReachText,
        ["sources"] = new JsonArray(Sources.Select(s => (JsonNode)s.ToJson()).ToArray()),
        ["checkedBy"] = new JsonArray(CheckedBy.Select(c => (JsonNode)c).ToArray()),
    };
}

public sealed class SourcesAnswer : ReportAnswer
{
    public override string Kind => "sources";
    public required string Plugin { get; init; }
    public List<string> Searched { get; } = new();
    public IReadOnlyList<string> Signatures { get; init; } = Array.Empty<string>();
    public int Depth { get; init; }
    public string? ItemFilter { get; init; }
    /// <summary>How many records of the item types the plugin adds, before any filter.</summary>
    public int ItemsInPlugin { get; set; }
    public List<SourcedItem> Items { get; } = new();

    protected override void Fill(JsonObject json)
    {
        json["plugin"] = Plugin; json["searched"] = new JsonArray(Searched.Select(s => (JsonNode)s).ToArray());
        json["itemTypes"] = new JsonArray(Signatures.Select(s => (JsonNode)s).ToArray()); json["depth"] = Depth;
        json["counts"] = new JsonObject
        {
            ["items"] = Items.Count, ["reachable"] = Items.Count(i => i.Reach == Reach.Yes), ["unclear"] = Items.Count(i => i.Reach == Reach.Unclear), ["notReachable"] = Items.Count(i => i.Reach == Reach.No),
        };
        json["items"] = new JsonArray(Items.Select(i => (JsonNode)i.ToJson()).ToArray());
    }

    /// <summary>
    /// How many items have each verdict. An item's Verdict cell can hold several ("Quest, World loot"): it counts
    /// under each of them, so the numbers are the ones the rows show. A race the item is for isn't a verdict.
    /// </summary>
    string VerdictCounts()
    {
        if (Items.Count == 0) return "By verdict: none.";
        var words = Items.Select(i => i.Verdict.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(w => !w.StartsWith("race ", StringComparison.Ordinal)).Distinct().ToList()).ToList();
        var counts = words.SelectMany(w => w).GroupBy(w => w).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {N(g.Count())}");
        bool several = words.Any(w => w.Count > 1);
        return $"By verdict{(several ? " (an item with several counts under each)" : "")}: {string.Join(", ", counts)}.";
    }

    protected override void Render(ReportBody body, ReportTextOptions o)
    {
        body.RowNoun = "lines";
        body.Head.Add($"Sources of the items {Plugin} adds ({string.Join(", ", Signatures)}){(ItemFilter != null ? $", item \"{ItemFilter}\"" : "")}. Searched: {string.Join(", ", Searched.Take(12))}{(Searched.Count > 12 ? $", … {N(Searched.Count)} plugins" : "")}. Lists followed to depth {Depth}.");
        body.Head.Add($"{Plural(Items.Count, "item")}: the player can get {N(Items.Count(i => i.Reach == Reach.Yes))}, unclear for {N(Items.Count(i => i.Reach == Reach.Unclear))}, no path found for {N(Items.Count(i => i.Reach == Reach.No))}. {VerdictCounts()}");
        if (Items.Count == 0)
        {
            body.Empty = ItemsInPlugin == 0
                ? $"{Plugin} adds no record of the types {string.Join(", ", Signatures)} (its overrides of other plugins' items aren't its own items)."
                : $"{Plugin} adds {Plural(ItemsInPlugin, "such record")}, and none is \"{ItemFilter}\".";
            return;
        }
        body.Head.Add("| Item | Name | Type | Player can get it | Verdict | Gate |");
        body.Head.Add("| :--- | :--- | :--- | :--- | :--- | :--- |");
        int table = o.Detail ? Items.Count : Math.Min(Items.Count, 40);
        foreach (var i in Items.Take(table)) body.Head.Add($"| {Cell(i.Label)} | {Cell(i.Name.Length == 0 ? "(no name)" : i.Name)} | {i.Signature} | {i.ReachText} | {Cell(i.Verdict)} | {Cell(i.Gate)} |");
        if (table < Items.Count) body.Head.Add($"| … {N(Items.Count - table)} more items | | | | (the lines below and detail list them) | |");
        foreach (var i in Items)
        {
            body.Rows.Add($"{i.Label} {i.Signature}{(i.Name.Length > 0 ? $" \"{i.Name}\"" : "")}{(i.Flags.Count > 0 ? " · " + string.Join(" · ", i.Flags) : "")}: {i.Verdict}; the player can get it: {i.ReachText}");
            if (i.Sources.Count == 0) body.Rows.Add("  - No source found in the searched plugins.");
            foreach (var group in i.Sources.GroupBy(s => s.Kind).OrderBy(g => Array.IndexOf(SourcedItem.KindOrder, g.Key)))
            {
                var list = group.OrderBy(s => s.Via.Count).ThenBy(s => s.Text, StringComparer.OrdinalIgnoreCase).ToList();
                // Loot spread through shared lists reaches hundreds of NPCs and cells: those kinds fold into one line
                if (!o.Detail && list.Count > 5 && group.Key is "NPC" or "Placed" or "Container")
                {
                    body.Rows.Add($"  - {group.Key}: {N(list.Count)} sources, e.g. {string.Join("; ", list.Take(3).Select(s => s.Text))}");
                    continue;
                }
                foreach (var s in list)
                    body.Rows.Add($"  - {s.Kind}: {(o.Detail ? s.Text : s.Compact ?? s.Text)}{(s.ViaLabels.Count > 0 ? " · via " + string.Join(" → ", s.ViaLabels) : "")}{(s.Routes > 1 ? $" (+{s.Routes - 1} other route{(s.Routes > 2 ? "s" : "")})" : "")}");
            }
            foreach (var c in i.CheckedBy.Distinct()) body.Rows.Add($"  - Checked by (not a source): {c}");
        }
    }
}

public sealed partial class SourcesReport
{
    readonly ReportSession _s;

    public SourcesReport(ReportSession session) { _s = session; }

    /// <summary>The human race: an armor made for another race is flagged.</summary>
    const string HumanRace = "00347D:Starfield.esm";
    /// <summary>Records that only pass an item along: the walk goes through them to whatever uses them.</summary>
    static readonly HashSet<string> Hops = new(StringComparer.Ordinal) { "LVLI", "FLST", "OTFT" };
    static readonly HashSet<string> PlacedSignatures = new(StringComparer.Ordinal) { "REFR", "ACHR" };
    /// <summary>How a vendor was found, said on each vendor source.</summary>
    const string ByFaction = "found by the faction's merchant container";
    const string ByEditorId = "found by its EditorID only (no faction of the searched plugins or their masters names a chest of it)";

    [GeneratedRegex(@"^Stages\[(\d+)\]\..*\.Rewards\[\d+\]")] private static partial Regex StageReward();

    /// <param name="plugin">A plugin's name in Data, or a file's path.</param>
    public SourcesAnswer Sources(string plugin, SourcesOptions? options = null)
    {
        options ??= new SourcesOptions();
        int depth = options.Depth > 0 ? options.Depth : 6;
        return _s.Run(scope =>
        {
            var side = _s.Side(plugin);
            var p = side.Plugin;
            if (p.Segment == null) throw new ReportException($"{side.Shown} can't be read: {p.Problems.ReadError ?? "it has no header index"}", "give a plugin file");
            if (p.Problems.MissingMasters.Count > 0) throw new ReportException($"{side.Shown} can't be read: its master {string.Join(", ", p.Problems.MissingMasters)} isn't there", "put the master back, then run the command again");
            var seg = p.Segment;
            var sigs = (options.Signatures is { Count: > 0 } given ? given : new[] { "WEAP", "ARMO" }).Select(Signature).Distinct(StringComparer.Ordinal).ToList();
            var answer = new SourcesAnswer { Plugin = side.Shown, Signatures = sigs, Depth = depth, ItemFilter = options.Item };
            var label = _s.Compare.Labeller(side);
            var conditions = new ConditionReader(_s, label);

            // ---- the plugins whose records count as sources: the plugin, the ones asked for, and its patches.
            // Its masters don't: a list of the game's that happens to hold a record of the same ID isn't a source of a mod's item.
            var searched = new List<(RecordEngine Engine, IndexedPlugin Plugin)> { (side.Engine, p) };
            void AddPlugin(RecordEngine engine, IndexedPlugin plugin)
            {
                if (!searched.Any(x => x.Plugin.Name.Equals(plugin.Name, StringComparison.OrdinalIgnoreCase))) searched.Add((engine, plugin));
            }
            foreach (var w in options.With) { var ws = _s.Side(w); AddPlugin(ws.Engine, ws.Plugin); }
            if (options.Dependents && side.InData)
                foreach (var d in _s.Index.Plugins.Where(d => d.Order.Enabled && d.Header != null && d.Header.Masters.Any(m => m.Equals(p.Name, StringComparison.OrdinalIgnoreCase))).OrderBy(d => d.Order.Position))
                    AddPlugin(_s.Engine, d);
            answer.Searched.AddRange(searched.Select(x => x.Plugin.Name));
            var links = options.BackLinks?.Invoke(scope, searched) ?? new ByteBackLinks(_s, scope, searched);
            // Vendors as the game finds them: from the factions of the searched plugins and their masters, read when first needed
            var vendors = new VendorIndex(_s, scope, VendorIndex.PluginsFor(_s, searched)) { Depth = depth };
            var searchedNames = new HashSet<string>(searched.Select(x => x.Plugin.Name), StringComparer.OrdinalIgnoreCase);
            // The compiled code of the scripts that hold an item in a property: does it add it to a list (an injector)?
            var code = new ScriptInjections(_s.Archives);
            var codeClock = new System.Diagnostics.Stopwatch();
            // A form list's entries as the game uses it, for an injector whose target lists are its entries: each read once
            var formLists = new Dictionary<string, ListEntries?>(StringComparer.OrdinalIgnoreCase);
            ListEntries? EntriesOf(string formKey) => formLists.TryGetValue(formKey, out var have) ? have : formLists[formKey] = _s.FormListEntries(scope, formKey);

            // ---- the items: the plugin's own records of the item types
            var items = new List<int>();
            for (int i = seg.FirstOwn; i < seg.FirstUnresolved; i++) if (sigs.Contains(seg.SignatureOf(seg[i]))) items.Add(i);
            answer.ItemsInPlugin = items.Count;
            if (options.Item != null)
            {
                string want = options.Item.Trim();
                string? wantKey = HeaderIndex.TryParseFormKey(want, out var wo, out var wi) ? $"{wi:X6}:{wo}" : null;
                items = items.Where(i => string.Equals(seg.EditorId(i), want, StringComparison.OrdinalIgnoreCase)
                    || (wantKey != null && side.Index.Describe(new RecordRef(p.Id, i)).FormKey.Equals(wantKey, StringComparison.OrdinalIgnoreCase))).ToList();
            }
            var views = new Dictionary<string, RecordView?>(StringComparer.OrdinalIgnoreCase);
            RecordView? ViewOf(Hit hit)
            {
                string key = hit.Plugin.Name + "|" + hit.Ref.Entry;
                if (views.TryGetValue(key, out var have)) return have;
                try { return views[key] = _s.View(scope, hit); }
                catch (ReportException) { return views[key] = null; }
            }
            string? SignatureOf(string formKey) =>
                HeaderIndex.TryParseFormKey(formKey, out var o, out var id) ? (_s.Seen(side, o, id) ?? _s.Winner(o, id))?.Signature : null;
            string EditorIdOf(string formKey)
            {
                string l = label(formKey);
                int at = l.LastIndexOf(" (", StringComparison.Ordinal);
                return at > 0 ? l[..at] : l;
            }

            // The form list a form list adds its entries to through AddToList (ANAM), on the version the game uses (for a
            // plugin outside Data, its own version): an item in the one is in the other too. Each read once.
            var addsTo = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            string? AddsTo(string listKey)
            {
                if (addsTo.TryGetValue(listKey, out var have)) return have;
                string? to = null;
                Hit? at = HeaderIndex.TryParseFormKey(listKey, out var lo, out var lid) ? (side.InData ? null : ReportSession.In(side.Engine, side.Name, lo, lid)) ?? _s.Winner(lo, lid) : null;
                if (at is { Signature: "FLST" } atHit && ViewOf(atHit) is { } lv && ReportSession.IsLink(lv.Json["AddToList"], out var k)
                    && SignatureOf(ReportSession.Canon(k)) == "FLST") to = ReportSession.Canon(k);
                return addsTo[listKey] = to;
            }
            // The lists between an item and a source, labelled; a list reached through another's AddToList says so
            List<string> Labels(List<string> via) =>
                via.Select((k, i) => i > 0 && addsTo.TryGetValue(via[i - 1], out var t) && string.Equals(t, k, StringComparison.OrdinalIgnoreCase)
                    ? $"{label(k)} (by {EditorIdOf(via[i - 1])}'s AddToList)" : label(k)).ToList();

            // Every item's links are searched for together (a search of the files' bytes goes over each plugin once), and
            // then each level of lists every item's walk reaches, all items' together: the walk below asks about the same
            // records one item at a time and finds them searched, so a search of the bytes makes one pass a level, not one
            // a level for each item (GAPS S100: a whole plugin's items over Starfield.esm's placed references)
            var level = items.Select(i => ReportSession.Canon(side.Index.Describe(new RecordRef(p.Id, i)).FormKey)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            links.Prefetch(level);
            var reached = new HashSet<string>(level, StringComparer.OrdinalIgnoreCase);
            for (int d = 0; d <= depth && level.Count > 0; d++)
            {
                var inbound = level.SelectMany(k => links.Into(k)).ToList();
                // The containers that hold this level's records (for their placed copies), and, below the depth, the lists
                // that hold them or that they add to: the walk's own two prefetches of a level
                var next = d < depth
                    ? inbound.Where(l => Hops.Contains(l.Signature) && !(l.Signature == "FLST" && l.Path == "AddToList")).Select(l => l.FromKey)
                        .Concat(d == 0 ? Enumerable.Empty<string>() : level.Select(AddsTo).OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    : new List<string>();
                links.Prefetch(inbound.Where(l => l.Signature == "CONT").Select(l => l.FromKey).Concat(next));
                level = next.Where(reached.Add).ToList();
            }
            foreach (int i in items)
            {
                var hit = new Hit(side.Engine, new RecordRef(p.Id, i));
                var info = hit.Info;
                var view = ViewOf(hit);
                var flags = new List<string>();
                if ((seg[i].Flags & 0x4) != 0) flags.Add("NonPlayable");
                if (info.Signature == "ARMO" && view != null && ReportSession.IsLink(view.Json["Race"], out var race) && !ReportSession.Canon(race).Equals(HumanRace, StringComparison.OrdinalIgnoreCase))
                    flags.Add($"race {EditorIdOf(ReportSession.Canon(race))}");
                var item = new SourcedItem
                {
                    FormKey = ReportSession.Canon(info.FormKey), Label = info.Label, EditorId = info.EditorId ?? "", Signature = info.Signature, Type = view?.Type ?? string.Join("/", _s.Schema.TypesOf(info.Signature)),
                    Name = view == null ? "" : (ReportSession.Text(view.Json["Name"], _s.Language) ?? "").Trim(), Flags = flags,
                };
                Walk(item);
                answer.Items.Add(item);
            }
            answer.Items.Sort((a, b) => a.Signature != b.Signature ? string.CompareOrdinal(a.Signature, b.Signature) : string.Compare(a.EditorId, b.EditorId, StringComparison.OrdinalIgnoreCase));

            // ---- the walk
            List<(string Cell, int Count)> PlacedIn(string formKey) =>
                links.Into(formKey).Where(l => PlacedSignatures.Contains(l.Signature) && l.Path == "Base")
                    .GroupBy(l => l.Parent ?? "(no cell)", StringComparer.OrdinalIgnoreCase).Select(g => (g.Key, g.Count())).ToList();

            void Walk(SourcedItem item)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { item.FormKey };
                var queue = new Queue<(string Key, List<string> Via)>();
                queue.Enqueue((item.FormKey, new List<string>()));

                void Add(ItemSource s)
                {
                    // The walk goes breadth first, so the first route to a source is the shortest; later ones are only counted
                    var same = item.Sources.FirstOrDefault(x => x.Kind == s.Kind && x.Text == s.Text);
                    if (same != null) { if (!same.Via.SequenceEqual(s.Via, StringComparer.OrdinalIgnoreCase)) same.Routes++; return; }
                    item.Sources.Add(s);
                }
                ItemSource Source(string kind, string text, List<string> via, Reach reach, string? record = null) =>
                    new() { Kind = kind, Text = text, Via = via, ViaLabels = Labels(via), Reach = reach, Record = record };

                int level = 0; // what is left of the list depth the queue is at: its keys are asked about together
                while (queue.Count > 0)
                {
                    if (level == 0)
                    {
                        level = queue.Count;
                        links.Prefetch(queue.Select(q => q.Key)); // nothing to do when the level before searched for them
                        // What is asked about next, searched for together: the containers that hold this level's records
                        // (for their placed copies) and the lists that hold them or that they add to (the next level)
                        links.Prefetch(queue.SelectMany(q => links.Into(q.Key))
                            .Where(l => l.Signature == "CONT" || (Hops.Contains(l.Signature) && !(l.Signature == "FLST" && l.Path == "AddToList"))).Select(l => l.FromKey)
                            .Concat(queue.Where(q => q.Via.Count > 0 && q.Key == q.Via[^1]).Select(q => AddsTo(q.Key)).OfType<string>()));
                    }
                    level--;
                    var (key, via) = queue.Dequeue();
                    var placed = PlacedIn(key);
                    foreach (var (cell, n) in placed)
                        Add(new ItemSource { Kind = "Placed", Text = $"placed in cell {(cell == "(no cell)" ? cell : label(cell))}{(n > 1 ? $" ×{n}" : "")}", Via = via, ViaLabels = Labels(via), Reach = Reach.Yes, Count = n, Record = cell == "(no cell)" ? null : cell });

                    // (a form list whose AddToList names this one adds to it; it doesn't hold what this one holds)
                    var inbound = links.Into(key).Where(l => !(PlacedSignatures.Contains(l.Signature) && l.Path == "Base") && !(l.Signature == "FLST" && l.Path == "AddToList")).ToList();
                    // A form list's entries are added to the list its AddToList names: the item is in that list too
                    string? addedTo = via.Count > 0 && key == via[^1] ? AddsTo(key) : null;
                    if (inbound.Count == 0 && via.Count > 0 && key == via[^1] && placed.Count == 0 && addedTo == null)
                    {
                        // A master's list that nothing searched holds can still be a vendor's stock: its container is the master's
                        var held = HeldByVendors(key);
                        if (held.Count > 0)
                            Add(HeldSource(key, held, via));
                        else
                            Add(Source("Unused list", $"{Labels(via)[^1]} is used by nothing in the searched plugins", via.Take(via.Count - 1).ToList(), Reach.No, key));
                    }

                    foreach (var r in inbound)
                    {
                        if (r.Path.Contains("Conditions", StringComparison.Ordinal))
                        {
                            item.CheckedBy.Add($"{r.Signature} {label(r.FromKey)} ({ShortPath(r.Path)})");
                            continue;
                        }
                        if (PlacedSignatures.Contains(r.Signature)) continue;
                        if (Hops.Contains(r.Signature))
                        {
                            if (seen.Add(r.FromKey) && via.Count < depth) queue.Enqueue((r.FromKey, via.Append(r.FromKey).ToList()));
                            continue;
                        }
                        Add(Classify(r, via));
                    }
                    if (addedTo is { } to && seen.Add(to) && via.Count < depth)
                        queue.Enqueue((to, via.Append(to).ToList()));
                }
            }

            ItemSource Classify(BackLink r, List<string> via)
            {
                string who = label(r.FromKey);
                string path = r.Path;
                bool vendor = (r.EditorId ?? "").Contains("Vendor", StringComparison.OrdinalIgnoreCase);
                bool script = path.StartsWith("VirtualMachineAdapter", StringComparison.Ordinal);
                ItemSource Make(string kind, string text, Reach reach) =>
                    new() { Kind = kind, Text = text, Via = via, ViaLabels = Labels(via), Reach = reach, Record = r.FromKey };

                switch (r.Signature)
                {
                    case "QUST":
                    {
                        var view = ViewOf(r.From);
                        if (StageReward().Match(path) is { Success: true } stage)
                        {
                            int entry = int.Parse(stage.Groups[1].Value);
                            int? index = view?.Json["Stages"] is JsonArray stages && entry < stages.Count && stages[entry] is JsonObject so
                                ? (so["Index"] is JsonValue iv && iv.TryGetValue<int>(out int n) ? n : 0) : null;
                            return new ItemSource { Kind = "Quest", Text = $"{who} reward at stage {(index?.ToString() ?? $"entry {entry}")}", Via = via, ViaLabels = Labels(via), Reach = Reach.Yes, Record = r.FromKey, Stage = index };
                        }
                        // Scripts that add the item's list to other lists while the game runs (leveled-list injectors)
                        if (script && Injected(r, via, view, true) is { } injected) return injected;
                        if (path.StartsWith("Aliases[", StringComparison.Ordinal) && path.Contains("CreateReferenceToObject", StringComparison.Ordinal)) return Make("Quest", $"{who} alias creates it ({ShortPath(path)})", Reach.Yes);
                        if (path.StartsWith("Aliases[", StringComparison.Ordinal) && path.Contains(".Items[", StringComparison.Ordinal)) return Make("Quest", $"{who} alias inventory ({ShortPath(path)})", Reach.Yes);
                        if (script) return Make("Quest", $"{who} script property ({ShortPath(path)})", Reach.Yes);
                        return Make("Quest", $"{who} ({ShortPath(path)})", Reach.Yes);
                    }
                    case "DIAL" or "INFO" or "SCEN":
                    {
                        // Dialogue and scenes belong to a quest: a script property on one is the quest's
                        string quest = QuestOf(r);
                        return Make("Quest", $"{label(quest)} {(r.Signature == "SCEN" ? "scene" : "dialogue")} {(script ? "script property" : "link")} ({r.Signature} {who}, {ShortPath(path)})", Reach.Yes);
                    }
                    case "COBJ":
                    {
                        if (!path.StartsWith("CreatedObject", StringComparison.Ordinal)) return Make("Other", $"consumed by recipe {who} ({ShortPath(path)})", Reach.No);
                        var view = ViewOf(r.From);
                        string? bench = view != null && ReportSession.IsLink(view.Json["WorkbenchKeyword"], out var wb) ? label(ReportSession.Canon(wb)) : null;
                        var conds = view == null ? new List<ConditionInfo>() : conditions.Read(view.Json["Conditions"]);
                        return new ItemSource
                        {
                            Kind = "Recipe", Text = $"made by {who} at {bench ?? "no bench"}; conditions: {(conds.Count == 0 ? "none" : string.Join(" · ", conds.Select(c => c.Text)))}",
                            Via = via, ViaLabels = Labels(via), Reach = Reach.Yes, Record = r.FromKey, Conditions = conds, Workbench = bench,
                        };
                    }
                    case "CONT":
                    {
                        // A vendor's chest is the placed container a faction names as its merchant container
                        var sellers = vendors.OfContainer(r.FromKey);
                        if (sellers.Count > 0) return VendorSource(sellers.ToList(), r.FromKey, via);
                        var cells = PlacedIn(r.FromKey);
                        string where = cells.Count == 0 ? "not placed in the searched plugins"
                            : "placed in " + string.Join(", ", cells.Take(4).Select(c => label(c.Cell) + (c.Count > 1 ? $" ×{c.Count}" : ""))) + (cells.Count > 4 ? $" (+{cells.Count - 4} more)" : "");
                        return new ItemSource
                        {
                            Kind = vendor ? "Vendor" : "Container", Text = $"in container {who}, {where}{(vendor ? "; " + ByEditorId : "")}", Via = via, ViaLabels = Labels(via),
                            Reach = vendor || cells.Count > 0 ? Reach.Yes : Reach.Unclear, Record = r.FromKey, Count = cells.Sum(c => c.Count), FoundBy = vendor ? "editorId" : null,
                        };
                    }
                    case "NPC_":
                        if (path == "Skin") return Make("NPC", $"skin of NPC {who} (not lootable)", Reach.No);
                        if (path.StartsWith("DeathItem", StringComparison.Ordinal)) return Make("NPC", $"death item of NPC {who}", Reach.Yes);
                        if (path.Contains("Outfit", StringComparison.Ordinal)) return Make("NPC", $"worn by NPC {who} ({path})", Reach.Yes);
                        if (path.StartsWith("Items", StringComparison.Ordinal)) return Make("NPC", $"carried by NPC {who}", Reach.Yes);
                        if (script) return Injected(r, via, ViewOf(r.From), false) ?? Make("Script", $"script property on NPC {who} ({ShortPath(path)})", Reach.Unclear);
                        return Make("NPC", $"NPC {who} ({ShortPath(path)})", Reach.Yes);
                    case "RACE":
                        return Make("NPC", $"skin of race {who} (not lootable)", Reach.No);
                    case "LVLN":
                        return Make("NPC", $"leveled NPC {who} ({ShortPath(path)})", Reach.Yes);
                    case "FACT":
                    {
                        bool sells = vendor || path.Contains("Vendor", StringComparison.OrdinalIgnoreCase);
                        return Make(sells ? "Vendor" : "Other", $"faction {who} ({ShortPath(path)})", sells ? Reach.Yes : Reach.Unclear);
                    }
                }
                if (script) return Injected(r, via, ViewOf(r.From), false) ?? Make("Script", $"script property on {r.Signature} {who} ({ShortPath(path)})", Reach.Unclear);
                if (vendor) return new ItemSource { Kind = "Vendor", Text = $"{r.Signature} {who} ({ShortPath(path)}); {ByEditorId}", Via = via, ViaLabels = Labels(via), Reach = Reach.Yes, Record = r.FromKey, FoundBy = "editorId" };
                return Make("Other", $"{r.Signature} {who} ({ShortPath(path)})", Reach.Unclear);
            }

            // A script on the record that adds the item (or the list it is in) to other lists while the game runs: told by
            // the script's compiled code (AddForm on a list held in a property); for a quest, when the code can't be read
            // or shows no such call, by the older rules (a struct that pairs the two, a script whose name says it injects).
            ItemSource? Injected(BackLink r, List<string> via, RecordView? view, bool olderRules)
            {
                if (view == null) return null;
                codeClock.Start();
                var lt = ScriptLinks.AddToListTargets(view.Json, r.Path, SignatureOf, name => code.Of(name, r.Plugin), entriesOf: EntriesOf);
                codeClock.Stop();
                var targets = lt.Targets;
                if (targets.Count == 0 || (!olderRules && lt.FoundBy != "code")) return null;
                string who = label(r.FromKey), scriptName = lt.Script, found = $" ({lt.How}{AddedWords(lt.AddedBy)})";
                // A target list a vendor's container holds makes it a vendor; else a target named "Vendor"
                var sold = targets.Select(t => (List: t, Vendors: HeldByVendors(t))).Where(t => t.Vendors.Count > 0).ToList();
                bool byName = sold.Count == 0 && targets.Any(t => EditorIdOf(t).Contains("Vendor", StringComparison.OrdinalIgnoreCase));
                string how = sold.Count > 0
                    ? "; " + string.Join("; ", sold.Select(t => $"{label(t.List)} is stock of {string.Join(", ", t.Vendors.Select(v => label(v.Vendor.Faction)).Distinct().Take(3))}{(t.Vendors.Select(v => v.Vendor.Faction).Distinct().Count() > 3 ? $" +{t.Vendors.Select(v => v.Vendor.Faction).Distinct().Count() - 3} more" : "")}")) + "; " + ByFaction
                    : byName ? "; " + ByEditorId : "";
                return new ItemSource
                {
                    Kind = sold.Count > 0 || byName ? "Vendor" : "Injected", Text = $"{who} script {scriptName} adds it to {string.Join(", ", targets.Select(label))}{found}{how}",
                    Compact = targets.Count <= 4 ? null : $"{who} script {scriptName} adds it to {string.Join(", ", targets.Take(4).Select(label))}, … {targets.Count - 4} more lists (detail names them){found}{how}",
                    Via = via, ViaLabels = Labels(via), Reach = Reach.Yes, Record = r.FromKey, Targets = targets,
                    FoundBy = sold.Count > 0 ? "faction" : byName ? "editorId" : null, InjectorFoundBy = lt.FoundBy,
                    Vendors = sold.Count > 0 ? sold.SelectMany(t => t.Vendors.Select(v => v.Vendor)).Distinct().ToList() : null,
                };
            }

            // The form lists that added entries to an injector's form lists through AddToList, named
            string AddedWords(IReadOnlyList<string>? by) => by is { Count: > 0 }
                ? $"; added through AddToList by {string.Join(", ", by.Take(4).Select(label))}{(by.Count > 4 ? $" and {by.Count - 4} more" : "")}" : "";

            // The vendors of a master's list. Only a master's list is looked up: a list of a searched plugin that
            // nothing searched holds is held by nothing (a master can't name it), so the vendors aren't read for it.
            IReadOnlyList<(VendorChest Vendor, List<string> Path)> HeldByVendors(string listKey) =>
                HeaderIndex.TryParseFormKey(listKey, out var owner, out _) && !searchedNames.Contains(owner)
                    ? vendors.Holding(listKey) : Array.Empty<(VendorChest, List<string>)>();

            // One vendor: "<faction> (<NPC>) via chest <chest> in <cell>"
            string OneVendor(VendorChest v)
            {
                var npcs = vendors.Npcs(v);
                string who = npcs.Count == 0 ? "no NPC of the faction found" : label(npcs[0]) + (npcs.Count > 1 ? $" +{npcs.Count - 1} more" : "");
                return $"{label(v.Faction)} ({who}) via chest {label(v.Chest)}{(v.Cell != null ? " in " + label(v.Cell) : "")}{(v.VendorFlag ? "" : " [the faction's Vendor flag is off]")}";
            }
            ItemSource Sold(string text, string? compact, string record, List<VendorChest> sellers, List<string> via) => new()
            {
                Kind = "Vendor", Text = text, Compact = compact, Via = via, ViaLabels = Labels(via), Reach = Reach.Yes,
                Record = record, Count = sellers.Count, FoundBy = "faction", Vendors = sellers,
            };
            static List<T> Ordered<T>(IEnumerable<T> xs, Func<T, VendorChest> v, Func<string, string> label) =>
                xs.OrderBy(x => v(x).VendorFlag ? 0 : 1).ThenBy(x => label(v(x).Faction), StringComparer.OrdinalIgnoreCase).ThenBy(x => v(x).Chest, StringComparer.OrdinalIgnoreCase).ToList();

            // A container that vendors' chests are copies of: "Vendor: <faction> (<NPC>) via chest <chest> in <cell>; container <container>"
            ItemSource VendorSource(List<VendorChest> sellers, string container, List<string> via)
            {
                sellers = Ordered(sellers, v => v, label);
                string tail = $"; container {label(container)}; {ByFaction}";
                return Sold(string.Join("; ", sellers.Select(OneVendor)) + tail,
                    sellers.Count <= 3 ? null : string.Join("; ", sellers.Take(3).Select(OneVendor)) + $"; … {sellers.Count - 3} more vendors (detail names them){tail}",
                    container, sellers, via);
            }

            // A master's list that vendors' containers hold: one source for the list, every vendor named
            ItemSource HeldSource(string list, IReadOnlyList<(VendorChest Vendor, List<string> Path)> held, List<string> via)
            {
                var each = Ordered(held.GroupBy(h => h.Vendor).Select(g => g.OrderBy(h => h.Path.Count).First()), h => h.Vendor, label);
                string One((VendorChest Vendor, List<string> Path) h) =>
                    $"{OneVendor(h.Vendor)}, container {label(h.Vendor.Container)}{(h.Path.Count > 0 ? " (inside " + string.Join(" → ", h.Path.Select(label)) + ")" : "")}";
                string head = $"{label(list)} is in the stock of {(each.Count == 1 ? "1 vendor" : $"{each.Count:N0} vendors")}: ";
                return Sold(head + string.Join("; ", each.Select(One)) + $"; {ByFaction}",
                    each.Count <= 3 ? null : head + string.Join("; ", each.Take(3).Select(One)) + $"; … {each.Count - 3} more (detail names them); {ByFaction}",
                    list, each.Select(h => h.Vendor).ToList(), via);
            }

            // The quest a topic, a response or a scene belongs to, from the header index
            string QuestOf(BackLink r)
            {
                string? parent = r.Parent;
                for (int guard = 0; parent != null && guard < 3; guard++)
                {
                    if (!HeaderIndex.TryParseFormKey(parent, out var o, out var id)) break;
                    if (ReportSession.In(r.From.Engine, r.Plugin, o, id) is not { } ph) break;
                    if (ph.Signature == "QUST") return parent;
                    parent = ph.Info.Parent == null ? null : ReportSession.Canon(ph.Info.Parent);
                }
                return parent ?? r.FromKey;
            }

            foreach (var (_, sp) in searched)
            {
                bool own = sp == p;
                ReportSession.AddSource(answer, sp, own && !side.InData ? side.Path : null);
                ReportSession.AddProblemNotes(answer, sp);
            }
            foreach (var n in links.Notes) answer.Notes.Add(n);
            answer.Notes.Add("Papyrus code is read only to tell an injector: a script on a record that holds the item (or its list) in a property is one when its compiled code calls AddForm on a leveled list held in a property with it. Each such source says how it was found: in its code, or, when no compiled script is found or its code shows no such call, by a struct that pairs the item with a list or by the script's name (quests only). A script that finds an item with GetFormFromFile, or adds it with AddItem on a FormID it builds itself, leaves no link in a record. scripts2 <plugin> --calls lists those calls, with the plugin and FormID each one names.");
            answer.Notes.Add("Vendors are found the way the game finds them: a faction's merchant container (Faction.MerchantContainer, VENC) is a placed chest, its base container's items are the faction's stock, and the NPCs in the faction sell them. The factions of the searched plugins and their masters are read." +
                " A container or list with \"Vendor\" in its EditorID that no such faction names still counts as a vendor, marked \"found by its EditorID only\"." +
                (vendors.Cost is { } vc ? $" Read for vendors: {vc}." : ""));
            if (!options.Dependents || !side.InData)
                answer.Notes.Add($"Only {string.Join(", ", answer.Searched)} {(answer.Searched.Count == 1 ? "was" : "were")} searched. A patch that masters {p.Name} can add its items to more lists.");
            answer.Read.Add(links.Cost);
            if (code.ScriptsLookedUp > 0)
                answer.Read.Add($"the compiled code of {code.ScriptsLookedUp:N0} script{(code.ScriptsLookedUp == 1 ? "" : "s")} on records that hold an item in a property, for AddForm calls ({code.ScriptsRead:N0} read from their files, {codeClock.Elapsed.TotalMilliseconds:N0} ms)");
            if (formLists.Count > 0)
                answer.Read.Add($"{formLists.Count:N0} form list{(formLists.Count == 1 ? "" : "s")} for the lists an injector adds to (each as the game uses it)");
            return answer;
        });
    }

    /// <summary>A record type given as a signature (WEAP) or as a Mutagen type name (Weapon).</summary>
    string Signature(string text)
    {
        text = text.Trim();
        if (_s.Schema.TypesOf(text.ToUpperInvariant()).Count > 0) return text.ToUpperInvariant();
        var type = _s.Schema.Model.RecordTypes.FirstOrDefault(t => t.Name.Equals(text, StringComparison.OrdinalIgnoreCase));
        return type?.Sig ?? throw new ReportException($"there is no record type \"{text}\"", "give signatures such as WEAP,ARMO,AMMO,ALCH,MISC,BOOK");
    }

    static string ShortPath(string p)
    {
        p = p.Replace("VirtualMachineAdapter.", "VMAD.");
        return p.Length > 90 ? p[..90] + "…" : p;
    }
}
