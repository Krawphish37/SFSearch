// sources, held against today's Sources on real mods (the same items found the same ways), and the default mode
// (the plugin's patches searched too) against the header index.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SFSearch.Index;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class SourcesTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    sealed record Case(string Name, string Plugin, string[] With, string? Item = null, string? Recorded = null);

    static readonly Case[] Cases =
    {
        new("sources__atom74.esm", "atom74.esm", Array.Empty<string>()),
        new("sources__weap_starlight.esm", "weap_starlight.esm", Array.Empty<string>()),
        new("sources__kgcdoom.esm", "kgcdoom.esm", Array.Empty<string>()),
        new("sources__aa50_longwatch.esm", "aa50_longwatch.esm", Array.Empty<string>()),
        new("sources__sfta04.esm", "sfta04.esm", Array.Empty<string>()),
        new("sources__sfta04.esm__with_sfbgs003.esm", "sfta04.esm", new[] { "sfbgs003.esm" }, Recorded: "451"),
        new("sources__sfta04.esm__with_sfbgs003.esm__item", "sfta04.esm", new[] { "sfbgs003.esm" }, Item: "SFTA04_AphelionLaserRifle", Recorded: "452"),
        new("sources__darkstar.esm", "darkstar.esm", Array.Empty<string>()),
    };

    sealed class TodayItem
    {
        public string EditorId = "", Type = "", Verdict = "", Gate = "", Key = "";
        public List<(string Kind, string Text)> Sources = new();
        public List<string> Checks = new();
        public bool NoSource;
    }

    /// <summary>"0008df:atom74": a FormKey the way today's Sources prints it (no extension), lower case.</summary>
    static string Stem(string formKey) => Regex.Replace(formKey.Trim().ToLowerInvariant(), @"\.(esm|esp|esl)$", "");
    static readonly Regex StemKey = new(@"\b[0-9A-Fa-f]{6}:[\w\-.+]+", RegexOptions.Compiled);
    static List<string> StemKeys(string text) => StemKey.Matches(text).Select(m => Stem(m.Value.TrimEnd('.'))).ToList();

    static List<TodayItem> ParseToday(string text, out List<string> searched)
    {
        var items = new List<TodayItem>();
        var s = Regex.Match(text, @"Searched: (.*?)\. Leveled-list depth");
        searched = s.Success ? s.Groups[1].Value.Split(", ").ToList() : new List<string>();
        foreach (var row in Ctx.Table(text, "Item name"))
        {
            if (row.Length < 4) continue;
            var e = Regex.Match(row[1], @"<sub>\*(.*)\*</sub>");
            items.Add(new TodayItem { EditorId = e.Success ? e.Groups[1].Value : row[1], Type = row[2], Verdict = row[3], Gate = row.Length > 4 ? row[4] : "" });
        }
        var byEdid = items.GroupBy(i => i.EditorId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        TodayItem? current = null; bool details = false;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("#### Details", StringComparison.Ordinal)) { details = true; continue; }
            if (line.StartsWith("#### What this can't see", StringComparison.Ordinal)) break;
            if (!details) continue;
            var h = Regex.Match(line, @"^\*\*.*\*\*: (.+?) \(([0-9A-Fa-f]{6}:[^)]+)\), (Weapon|Armor)");
            if (h.Success) { current = byEdid.GetValueOrDefault(h.Groups[1].Value); if (current != null) current.Key = Stem(h.Groups[2].Value); continue; }
            if (current == null || !line.StartsWith("- ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("- No source found", StringComparison.Ordinal)) { current.NoSource = true; continue; }
            var c = Regex.Match(line, @"^- \*Checked by \(not a source\):\* (.*)$");
            if (c.Success) { current.Checks.Add(c.Groups[1].Value); continue; }
            var k = Regex.Match(line, @"^- \*\*(.+?):\*\* (.*)$");
            if (k.Success) current.Sources.Add((k.Groups[1].Value, k.Groups[2].Value));
        }
        return items;
    }

    /// <summary>What names a source whichever side printed it: its kind, the record it is, what it adds to or which stage, the lists on the way, the other routes.</summary>
    static string Describe(string kind, string firstKey, string extra, IEnumerable<string> via, int otherRoutes) =>
        $"{kind}|{firstKey}|{extra}|via:{string.Join(">", via)}|routes:{otherRoutes}";

    static (List<string> Descriptors, Dictionary<string, int> Folded) TodayDescriptors(TodayItem item)
    {
        var list = new List<string>(); var folded = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (kind, text0) in item.Sources)
        {
            var f = Regex.Match(text0, @"^(\d+) sources, e\.g\. ");
            if (f.Success) { folded[kind] = int.Parse(f.Groups[1].Value); continue; }
            string text = text0; int routes = 0;
            var r = Regex.Match(text, @" \(\+(\d+) other routes?\)$");
            if (r.Success) { routes = int.Parse(r.Groups[1].Value); text = text[..r.Index]; }
            var via = new List<string>();
            int v = text.LastIndexOf(" · via ", StringComparison.Ordinal);
            if (v >= 0) { via = StemKeys(text[(v + 7)..]); text = text[..v]; }
            var keys = StemKeys(text);
            string first = keys.Count > 0 ? keys[0] : "";
            string extra = "";
            var stage = Regex.Match(text, @"reward at stage (\d+)");
            if (stage.Success) extra = "stage " + stage.Groups[1].Value;
            int adds = text.IndexOf(" adds it to ", StringComparison.Ordinal);
            if (adds >= 0) extra = "adds to " + string.Join(",", StemKeys(text[adds..]).OrderBy(x => x, StringComparer.Ordinal));
            if (kind == "Recipe") extra = "bench " + (keys.Count > 1 && text.Contains(" at ", StringComparison.Ordinal) && !text.Contains(" at no bench", StringComparison.Ordinal) ? keys[1] : "none") + $" conditions {RecipeConditions(text)}";
            list.Add(Describe(kind, first, extra, via, routes));
        }
        return (list, folded);
    }

    static int RecipeConditions(string text)
    {
        int at = text.IndexOf("conditions: ", StringComparison.Ordinal);
        if (at < 0) return 0;
        string conds = text[(at + 12)..];
        return conds == "none" ? 0 : conds.Split(" · ").Length;
    }

    static string MyDescriptor(ItemSource s)
    {
        string extra = "";
        if (s.Stage != null) extra = "stage " + s.Stage;
        if (s.Targets != null) extra = "adds to " + string.Join(",", s.Targets.Select(Stem).OrderBy(x => x, StringComparer.Ordinal));
        if (s.Kind == "Recipe") extra = $"bench {(s.Workbench == null ? "none" : StemKeys(s.Workbench).FirstOrDefault() ?? "none")} conditions {s.Conditions?.Count ?? 0}";
        return Describe(s.Kind, s.Record == null ? "" : Stem(s.Record), extra, s.Via.Select(Stem), s.Routes - 1);
    }

    static readonly HashSet<string> FoldedKinds = new(StringComparer.Ordinal) { "NPC", "Placed", "Container" };

    public static string Today(Ctx ctx)
    {
        if (!ctx.HasToday && ctx.Baseline == null) return "SKIP today's Sources wasn't given (--today, --baseline, or --sfsearch and --home)";
        var s = ctx.Session;
        var report = new SourcesReport(s);
        var parts = new List<string>(); var sb = new StringBuilder();
        long itemsAll = 0, sourcesSame = 0, onlyTodayAll = 0, onlyNewAll = 0, verdictSame = 0, otherVerdicts = 0; int unexplained = 0, recorded = 0;
        var causes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in Cases)
        {
            var args = new List<string> { "Sources", c.Plugin };
            if (c.With.Length > 0) { args.Add("--with"); args.Add(string.Join(",", c.With)); }
            if (c.Item != null) { args.Add("--item"); args.Add(c.Item); }
            string? text = c.Recorded != null ? ctx.Recorded(c.Recorded) : null;
            string from = "run on the test home";
            if (text != null) { recorded++; from = $"recorded run {c.Recorded}"; }
            else text = ctx.Today(c.Name, args.ToArray());
            if (text == null) { if (c.Recorded != null && c.Item != null) continue; return "SKIP today's Sources couldn't be run"; }
            var today = ParseToday(text, out var searched);
            var a = report.Sources(c.Plugin, new SourcesOptions { With = c.With, Item = c.Item, Dependents = false });
            ctx.WriteReport(c.Name + ".txt", a.ToText(new ReportTextOptions { Detail = true, MaxChars = int.MaxValue }));
            Check(searched.Count == a.Searched.Count && searched.All(p => a.Searched.Contains(p, StringComparer.OrdinalIgnoreCase)), $"{c.Name}: searched {string.Join(", ", a.Searched)}; today {string.Join(", ", searched)}");
            Check(today.Count == a.Items.Count, $"{c.Name}: {a.Items.Count} items, today {today.Count}");
            var stored = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> Stored(string plugin)
            {
                if (stored.TryGetValue(plugin, out var have)) return have;
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                if (ctx.Today($"stats__{plugin}", "Stats", plugin) is { } stats)
                    foreach (var row in Ctx.Table(stats, "Signature")) if (row.Length >= 5) d[row[0]] = row[4];
                return stored[plugin] = d;
            }
            int same = 0, onlyToday = 0, onlyNew = 0, verdicts = 0, gates = 0, verdictOther = 0;
            foreach (var item in a.Items)
            {
                var t = today.FirstOrDefault(x => x.EditorId.Equals(item.EditorId, StringComparison.OrdinalIgnoreCase) && (x.Key.Length == 0 || x.Key == Stem(item.FormKey)))
                    ?? throw new TestFailed($"{c.Name}: today's answer has no item {item.Label}");
                string at = $"{c.Name} {item.Label}";
                Check((t.Type == "Weapon" ? "WEAP" : "ARMO") == item.Signature, $"{at}: a {item.Signature}; today {t.Type}");
                var (theirs, folded) = TodayDescriptors(t);
                var mine = item.Sources.Where(x => !folded.ContainsKey(x.Kind)).Select(MyDescriptor).ToList();
                // Kinds today's folds into one line give a count only
                var foldedDiffer = new List<string>();
                foreach (var (kind, n) in folded)
                {
                    int have = item.Sources.Count(x => x.Kind == kind);
                    if (have == n) same += n; else foldedDiffer.Add($"{kind}: {have} sources, today {n}");
                }
                var count = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var d in theirs) count[d] = count.GetValueOrDefault(d) + 1;
                var extraMine = new List<string>();
                foreach (var d in mine)
                {
                    if (count.TryGetValue(d, out int n) && n > 0) { count[d] = n - 1; same++; }
                    else extraMine.Add(d);
                }
                var extraToday = count.Where(kv => kv.Value > 0).SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value)).ToList();
                // A source both have, by another number of routes, is one difference, not two
                foreach (var d in extraMine.ToList())
                {
                    string bare = d[..d.LastIndexOf("|routes:", StringComparison.Ordinal)];
                    var twin = extraToday.FirstOrDefault(x => x[..x.LastIndexOf("|routes:", StringComparison.Ordinal)] == bare);
                    if (twin == null) continue;
                    extraMine.Remove(d); extraToday.Remove(twin);
                    sb.AppendLine($"  {at}: other routes: new {d[(d.LastIndexOf(':') + 1)..]}, today {twin[(twin.LastIndexOf(':') + 1)..]} for {bare}");
                    unexplained++;
                }
                // GAPS S10: a container a vendor faction names, or a master's list a vendor's container holds, was a
                // "Container" or an "Unused list" today; it is now a vendor found by its faction
                int vendorsFound = 0;
                foreach (var d in extraToday.ToList())
                {
                    var p = d.Split('|');
                    if (p.Length < 5 || p[0] is not ("Container" or "Unused list")) continue;
                    string via = p[3][4..];
                    string viaNew = p[0] == "Unused list" ? (via.Length == 0 ? p[1] : via + ">" + p[1]) : via;
                    var twin = extraMine.FirstOrDefault(m => { var q = m.Split('|'); return q[0] == "Vendor" && q[1] == p[1] && q[3][4..] == viaNew; });
                    if (twin == null || item.Sources.First(x => MyDescriptor(x) == twin).FoundBy != "faction") continue;
                    extraToday.Remove(d); extraMine.Remove(twin); vendorsFound++;
                    const string why = "a vendor found by its faction's merchant container, which today's Sources called a container or an unused list (GAPS S10)";
                    sb.AppendLine($"  {at}: today {d}, new {twin}: {why}");
                    causes[why] = causes.GetValueOrDefault(why) + 1;
                }
                foreach (var d in extraToday) { sb.AppendLine($"  UNEXPLAINED only today {at}: {d}"); unexplained++; onlyToday++; }
                foreach (var d in extraMine)
                {
                    // The record that is the source: is it of a type today's database stores?
                    var source = item.Sources.First(x => MyDescriptor(x) == d);
                    string? why = null;
                    if (source.Record != null && s.Winner(source.Record) is { } hit)
                    {
                        string type = s.Compare.View(hit.Engine, hit.Ref).Type;
                        var st = Stored(hit.Plugin.Name);
                        if (st.TryGetValue(type, out var how) && how != "Yes") why = $"the source is a {hit.Signature}, a record type today's database doesn't store in full";
                        else if (hit.Signature is "DIAL" or "INFO" or "SCEN") why = $"the source is a {hit.Signature}, a record type today's database doesn't store";
                    }
                    if (why == null) { sb.AppendLine($"  UNEXPLAINED only new {at}: {d}"); unexplained++; }
                    else { sb.AppendLine($"  only new {at}: {d}: {why}"); causes[why] = causes.GetValueOrDefault(why) + 1; }
                    onlyNew++;
                }
                foreach (var d in foldedDiffer) { sb.AppendLine($"  UNEXPLAINED {at}: {d}"); unexplained++; }
                // Verdict, gate, flags, checks
                bool sameSources = extraMine.Count == 0 && extraToday.Count == 0 && foldedDiffer.Count == 0 && vendorsFound == 0;
                string todayNone = t.Verdict.StartsWith("No source found", StringComparison.Ordinal) ? "No source found" : t.Verdict.StartsWith("Unused list only", StringComparison.Ordinal) ? "Unused list only" : "";
                if (item.Verdict == t.Verdict) verdicts++;
                // Today's verdict leaves sources of the kind Other out, and says "No source found" beside them
                else if (todayNone.Length > 0 && item.Sources.Any(x => x.Kind == "Other") && item.Verdict == "Used by other records" + t.Verdict[todayNone.Length..]) { otherVerdicts++; verdictOther++; }
                else if (sameSources) { sb.AppendLine($"  UNEXPLAINED {at}: verdict \"{item.Verdict}\"; today \"{t.Verdict}\""); unexplained++; }
                else sb.AppendLine($"  {at}: verdict \"{item.Verdict}\"; today \"{t.Verdict}\" (the sources differ, above)");
                var gateToday = Regex.Matches(t.Gate, @"(\w+)\(").Select(m => m.Groups[1].Value).OrderBy(x => x).ToList();
                var gateMine = item.Sources.Where(x => x.Kind == "Recipe" && x.Gated).SelectMany(x => x.Conditions!.Where(k => k.Function != "GetLevel").Select(k => k.Function)).ToList();
                if ((t.Gate.Length == 0) == (item.Gate.Length == 0) && gateToday.Distinct().SequenceEqual(gateMine.Distinct().OrderBy(x => x))) gates++;
                else { sb.AppendLine($"  UNEXPLAINED {at}: gate \"{item.Gate}\"; today \"{t.Gate}\""); unexplained++; }
                Check(t.NoSource == (item.Sources.Count == 0) || !sameSources, $"{at}: {(item.Sources.Count == 0 ? "no source" : "sources")}; today {(t.NoSource ? "no source" : "sources")}");
                var checksToday = t.Checks.SelectMany(StemKeys).OrderBy(x => x).ToList();
                var checksMine = item.CheckedBy.Distinct().Select(x => StemKeys(x).FirstOrDefault() ?? "").OrderBy(x => x).ToList();
                if (!checksToday.Distinct().SequenceEqual(checksMine.Distinct())) { sb.AppendLine($"  UNEXPLAINED {at}: checked by {string.Join(",", checksMine)}; today {string.Join(",", checksToday)}"); unexplained++; }
            }
            itemsAll += a.Items.Count; sourcesSame += same; onlyTodayAll += onlyToday; onlyNewAll += onlyNew; verdictSame += verdicts;
            parts.Add($"{c.Plugin}{(c.With.Length > 0 ? " with " + string.Join(",", c.With) : "")}{(c.Item != null ? " item " + c.Item : "")} ({from}): {N(a.Items.Count)} items, {N(verdicts)} with the same verdict{(verdictOther > 0 ? $" and {N(verdictOther)} \"Used by other records\" where today says \"No source found\" or \"Unused list only\"" : "")}, {N(gates)} the same gate, {N(same)} sources the same, {onlyToday} only today, {onlyNew} only new; verdicts: {string.Join(", ", a.Items.GroupBy(i => i.Verdict.Split(',')[0].Trim()).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}; the player can get {a.Items.Count(i => i.Reach == Reach.Yes)}, unclear {a.Items.Count(i => i.Reach == Reach.Unclear)}, no path {a.Items.Count(i => i.Reach == Reach.No)}");
            sb.AppendLine(parts[^1]);
        }
        ctx.WriteReport("sources-today.txt", sb.ToString());
        Check(unexplained == 0, $"{unexplained} differences from today's Sources have no cause the test can show (see sources-today.txt): {string.Join(" | ", sb.ToString().Split('\n').Where(l => l.Contains("UNEXPLAINED") || l.Contains("other routes")).Take(8))}");
        return $"{Cases.Length} runs of today's Sources ({recorded} from the recorded baseline), the same plugins searched: {N(itemsAll)} items, {N(verdictSame)} with the same verdict, {N(otherVerdicts)} more where the new verdict is \"Used by other records\" and today's is \"No source found\" or \"Unused list only\" (sources of the kind Other: today's verdict doesn't count that kind, and can't see records of types its database doesn't store); {N(sourcesSame)} sources the same (kind, record, lists on the way, stage or lists added to, recipe bench and number of conditions), {N(onlyTodayAll)} only today's, {N(onlyNewAll)} only the new one's" +
            (causes.Count > 0 ? $" ({string.Join("; ", causes.Select(k => $"{k.Value} {k.Key}"))})" : "") + $". {string.Join(" || ", parts)}";
    }

    // ---------------------------------------------------------------- Vendors found the way the game finds them
    /// <summary>
    /// GAPS S10: a vendor's chest is the placed container a faction names as its merchant container, whatever its
    /// EditorID. A made plugin holds: a gun in a list in a chest named "ShopChest" that a vendor faction names (with an
    /// NPC in the faction); a gun in a container named "VendorBox" that no faction names (the EditorID fallback); a gun
    /// in a plain loot crate; and a gun added to an override of a Starfield.esm list that a Starfield.esm vendor's chest
    /// holds (nothing in the plugin holds it). Then real mods whose vendor chests have no "Vendor" in their EditorID.
    /// </summary>
    public static string Vendors(Ctx ctx)
    {
        var s = ctx.Session;
        var mod = Made.New("SourcesVendorsTest.esm");
        Weapon Gun(string edid) { var w = new Weapon(mod) { EditorID = edid }; mod.Weapons.Add(w); return w; }
        Mutagen.Bethesda.Starfield.Container Box(string edid, IFormLinkGetter<IItemGetter> item)
        {
            var c = new Mutagen.Bethesda.Starfield.Container(mod) { EditorID = edid, Items = new() { new ContainerEntry { Item = new ContainerItem { Item = item.FormKey.ToLink<IItemGetter>(), Count = 1 } } } };
            mod.Containers.Add(c); return c;
        }
        var gun1 = Gun("SrcVend_ShopGun"); var gun2 = Gun("SrcVend_NamedGun"); var gun3 = Gun("SrcVend_LootGun"); var gun4 = Gun("SrcVend_MasterListGun");
        var list = new LeveledItem(mod) { EditorID = "SrcVend_ShopList", Entries = new() { new LeveledItemEntry { Reference = gun1.ToLink<IItemGetter>(), Level = 1, Count = 1 } } };
        mod.LeveledItems.Add(list);
        var shopChest = Box("SrcVend_ShopChest", list.ToLink<IItemGetter>());
        Box("SrcVend_VendorBox", gun2.ToLink<IItemGetter>());
        Box("SrcVend_LootCrate", gun3.ToLink<IItemGetter>());
        var chestRef = new PlacedObject(mod) { Base = shopChest.ToLink<IPlaceableObjectGetter>() };
        var cell = new Cell(mod) { EditorID = "SrcVend_ShopCell", Flags = Cell.Flag.IsInteriorCell };
        cell.Persistent.Add(chestRef);
        mod.Cells.Records.Add(new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock, SubBlocks = new() { new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = new() { cell } } } });
        var faction = new Faction(mod) { EditorID = "SrcVend_ShopFaction", Flags = Faction.FactionFlag.Vendor, MerchantContainer = chestRef.ToNullableLink<IPlacedObjectGetter>() };
        mod.Factions.Add(faction);
        var npc = new Npc(mod) { EditorID = "SrcVend_Shopkeeper", Factions = new() { new RankPlacement { Faction = faction.ToLink<IFactionGetter>(), Rank = 0 } } };
        mod.Npcs.Add(npc);

        // A Starfield.esm list a Starfield.esm vendor's chest holds, found from the game's own factions
        var game = new VendorIndex(s, new ReadScope(), new[] { (s.Engine, s.Index.Plugin(Made.Game)!) });
        var (vanillaList, vanillaVendor) = game.FirstHeldList() ?? throw new TestFailed("no Starfield.esm vendor's container holds a leveled list");
        var over = new LeveledItem(Made.Key(vanillaList), StarfieldRelease.Starfield) { EditorID = "SrcVend_VanillaListOverride", Entries = new() { new LeveledItemEntry { Reference = gun4.ToLink<IItemGetter>(), Level = 1, Count = 1 } } };
        mod.LeveledItems.Add(over);
        string path = Made.Write(ctx, mod, "sources-vendors");

        var a = new SourcesReport(s).Sources(path);
        ctx.WriteReport("sources-vendors.txt", a.ToText(new ReportTextOptions { Detail = true, MaxChars = int.MaxValue }));
        SourcedItem Item(string edid) => a.Items.FirstOrDefault(i => i.EditorId == edid) ?? throw new TestFailed($"no item {edid} in the answer");
        string Key(IMajorRecordGetter r) => ReportSession.Canon(r.FormKey.ToString());

        var shop = Item("SrcVend_ShopGun").Sources.SingleOrDefault(x => x.Kind == "Vendor");
        Check(shop != null && Item("SrcVend_ShopGun").Sources.All(x => x.Kind != "Container"), $"SrcVend_ShopGun: a chest named \"ShopChest\" that a vendor faction names isn't a vendor: {string.Join(" | ", Item("SrcVend_ShopGun").Sources.Select(x => x.Kind + ": " + x.Text))}");
        Check(shop!.FoundBy == "faction" && shop.Record == Key(shopChest) && shop.Vendors is { Count: 1 } && shop.Vendors[0].Faction == Key(faction) && shop.Vendors[0].Chest == Key(chestRef)
            && shop.Vendors[0].VendorFlag && shop.Vendors[0].Npcs.SequenceEqual(new[] { Key(npc) }) && shop.Reach == Reach.Yes && shop.Via.SequenceEqual(new[] { Key(list) }),
            $"SrcVend_ShopGun: the vendor is {shop.Text} (found by {shop.FoundBy}, via {string.Join(">", shop.Via)}, npcs {string.Join(",", shop.Vendors?.SelectMany(v => v.Npcs) ?? Array.Empty<string>())})");
        Check(shop.Text.StartsWith("SrcVend_ShopFaction", StringComparison.Ordinal) && shop.Text.Contains("(SrcVend_Shopkeeper", StringComparison.Ordinal) && shop.Text.Contains("via chest", StringComparison.Ordinal) && shop.Text.Contains("SrcVend_ShopCell", StringComparison.Ordinal),
            $"SrcVend_ShopGun: the text doesn't say faction (NPC) via chest in cell: {shop.Text}");
        var named = Item("SrcVend_NamedGun").Sources.Single();
        Check(named.Kind == "Vendor" && named.FoundBy == "editorId" && named.Text.Contains("found by its EditorID only", StringComparison.Ordinal), $"SrcVend_NamedGun: {named.Kind} ({named.FoundBy}): {named.Text}");
        var loot = Item("SrcVend_LootGun").Sources.Single();
        Check(loot.Kind == "Container" && loot.FoundBy == null, $"SrcVend_LootGun: {loot.Kind} ({loot.FoundBy}): {loot.Text}");
        var held = Item("SrcVend_MasterListGun").Sources;
        Check(held.Count == 1 && held[0].Kind == "Vendor" && held[0].FoundBy == "faction" && held[0].Record == ReportSession.Canon(vanillaList) && held[0].Vendors!.Any(v => v.Faction == vanillaVendor.Faction)
            && held[0].Via.SequenceEqual(new[] { ReportSession.Canon(vanillaList) }) && held[0].Text.Contains("is in the stock of", StringComparison.Ordinal),
            $"SrcVend_MasterListGun (in an override of {vanillaList}, which {vanillaVendor.Faction}'s chest holds): {string.Join(" | ", held.Select(x => x.Kind + " (" + x.FoundBy + "): " + x.Text))}");
        Check(a.Notes.Any(n => n.StartsWith("Vendors are found the way the game finds them", StringComparison.Ordinal) && n.Contains("Read for vendors:", StringComparison.Ordinal)), "the answer's note doesn't say how vendors are found and what it read");

        // Real mods: a vendor chest named without "Vendor", and one named with it (the old rule was right there)
        var real = new List<string>();
        foreach (var (plugin, item, factionKey, record) in RealVendors)
        {
            if (s.Index.Plugin(plugin) is not { Order.Enabled: true }) { real.Add($"{plugin}: not in the load order, skipped"); continue; }
            var r = new SourcesReport(s).Sources(plugin, new SourcesOptions { Item = item, Dependents = false, Signatures = new[] { "WEAP", "ARMO", "MISC", "ALCH", "BOOK", "AMMO" } });
            var src = r.Items.SelectMany(i => i.Sources).Where(x => x.Kind == "Vendor" && x.FoundBy == "faction" && x.Record == record).ToList();
            Check(src.Count > 0 && src.Any(x => x.Vendors!.Any(v => v.Faction == factionKey)), $"{plugin} {item}: no vendor {factionKey} through {record}: {string.Join(" | ", r.Items.SelectMany(i => i.Sources).Select(x => x.Kind + ": " + x.Text))}");
            Check(r.Items.SelectMany(i => i.Sources).All(x => !(x.Record == record && x.Kind is "Container" or "Unused list")), $"{plugin} {item}: {record} is still shown as a container or an unused list");
            real.Add($"{plugin} {item}: {(src[0].Compact ?? src[0].Text)[..Math.Min(160, (src[0].Compact ?? src[0].Text).Length)]}…");
        }
        return $"made plugin: a chest named ShopChest is a vendor by its faction (SrcVend_ShopFaction, NPC SrcVend_Shopkeeper); VendorBox by its EditorID only; LootCrate a container; an override of {vanillaList} is the stock of {held[0].Vendors!.Count} vendor(s), {vanillaVendor.Faction} among them. Real: {string.Join("; ", real)}";
    }

    /// <summary>
    /// Real vendors the old EditorID rule missed, and one it got right. Record is the source's record: the container,
    /// or for a master's list that nothing searched holds, the list.
    /// </summary>
    static readonly (string Plugin, string Item, string Faction, string Record)[] RealVendors =
    {
        // Groat Meat is in a list SFBGS001_MS01_JandarRestContainer holds: a chest SFBGS001_MS01_JandarRestBarFaction
        // names, with no "Vendor" in its EditorID. The old answer said "Container"
        ("ShatteredSpace.esm", "SFBGS001_Food_Meat_Groat", "035261:ShatteredSpace.esm", "035260:ShatteredSpace.esm"),
        // Longwatch is in aa50_longwatch.esm's override of LL_Weapon_Ranged_Allied (269373:Starfield.esm), which
        // Vendor_City_AC_BelleRowlandFaction's container holds: the old answer said "Unused list"
        ("aa50_longwatch.esm", "Longwatch", "1B2637:Starfield.esm", "269373:Starfield.esm"),
        // A chest named "ASE2Vendor_…": the old rule was right, the faction says the same
        ("aseeverbright.esm", "ASE2Clothes_Service_Uniform_01", "0168BB:aseeverbright.esm", "001FA7:aseeverbright.esm"),
    };

    // ---------------------------------------------------------------- The plugin's patches searched too
    public static string Dependents(Ctx ctx)
    {
        var s = ctx.Session;
        var report = new SourcesReport(s);
        var parts = new List<string>();
        foreach (string plugin in new[] { "atom74.esm", "sfta04.esm" })
        {
            var alone = report.Sources(plugin, new SourcesOptions { Dependents = false });
            var all = report.Sources(plugin);
            var expect = s.Index.Plugins.Where(p => p.Order.Enabled && p.Header != null && p.Header.Masters.Any(m => m.Equals(plugin, StringComparison.OrdinalIgnoreCase))).Select(p => p.Name).ToList();
            Check(all.Searched.Count == expect.Count + 1 && expect.All(e => all.Searched.Contains(e, StringComparer.OrdinalIgnoreCase)), $"{plugin}: searched {string.Join(", ", all.Searched)}; the loaded plugins that master it are {string.Join(", ", expect)}");
            Check(alone.Searched.Count == 1 && alone.Notes.Any(n => n.StartsWith("Only ", StringComparison.Ordinal)), $"{plugin}: the answer for the plugin alone doesn't say that only it was searched");
            int more = 0, lost = 0;
            foreach (var item in all.Items)
            {
                var before = alone.Items.First(i => i.FormKey == item.FormKey);
                var have = item.Sources.Select(x => x.Kind + "|" + x.Record + "|" + string.Join(">", x.Via)).ToHashSet();
                // A list that nothing used in the plugin alone may be used by a patch: that source goes, others come
                lost += before.Sources.Count(x => x.Kind != "Unused list" && !have.Contains(x.Kind + "|" + x.Record + "|" + string.Join(">", x.Via)));
                more += item.Sources.Count(x => !before.Sources.Any(y => y.Kind == x.Kind && y.Record == x.Record && y.Via.SequenceEqual(x.Via)));
            }
            Check(lost == 0, $"{plugin}: {lost} sources found in the plugin alone are gone when its patches are searched too");
            string Verdicts(SourcesAnswer x) => string.Join(", ", x.Items.GroupBy(i => i.Verdict.Split(',')[0].Trim()).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"));
            parts.Add($"{plugin}: alone {N(alone.Items.Sum(i => i.Sources.Count))} sources for {alone.Items.Count} items ({Verdicts(alone)}; the player can get {alone.Items.Count(i => i.Reach == Reach.Yes)}); with the {expect.Count} loaded plugins that master it ({string.Join(", ", expect.Take(6))}{(expect.Count > 6 ? ", …" : "")}) {N(all.Items.Sum(i => i.Sources.Count))} sources, {more} of them new ({Verdicts(all)}; the player can get {all.Items.Count(i => i.Reach == Reach.Yes)})");
        }
        return string.Join("; ", parts) + ". No source found in the plugin alone is lost.";
    }
}
