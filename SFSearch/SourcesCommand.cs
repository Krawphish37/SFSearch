using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SFSearch
{
    /// <summary>
    /// Sources: how does the player get each weapon and armor piece a plugin adds? Walks the Refs table backwards from every
    /// item, through leveled lists, form lists and outfits, until it reaches something the player meets: a quest (stage reward,
    /// alias, script or dialogue property), a recipe and its conditions, a container or vendor, a placed copy in a cell, an NPC.
    /// Replaces following `Refs` by hand one step at a time.
    /// </summary>
    public static class SourcesCommand
    {
        const string HumanRace = "00347D:Starfield.esm";

        // Records that only pass an item along: the walk goes through them to whatever uses them.
        static readonly HashSet<string> HopSignatures = new(StringComparer.OrdinalIgnoreCase) { "LeveledItem", "FormList", "Outfit" };

        // Verdict order: the first kinds are the ones a player would normally use.
        static readonly string[] KindOrder = { "Recipe", "Quest", "Vendor", "Injected", "Container", "Placed", "NPC", "Script", "Unused list", "Other" };

        sealed class Source
        {
            public string Kind = "";
            public string Text = "";
            public List<string> Via = new(); // hop records from the item outward (the shortest route found)
            public int Routes = 1;           // how many routes through different lists reach the same source
        }

        sealed class Inbound
        {
            public string Plugin = "", From = "", Signature = "", EditorID = "", Path = "";
        }

        public static void Run(List<string> args)
        {
            string? outFile = Sfs.TakeOption(args, "--out");
            string? sigOpt = Sfs.TakeOption(args, "--sig");
            string? itemOpt = Sfs.TakeOption(args, "--item");
            string? withOpt = Sfs.TakeOption(args, "--with");
            int depth = int.TryParse(Sfs.TakeOption(args, "--depth"), out int d) && d > 0 ? d : 6;
            bool scripts = Sfs.TakeFlag(args, "--scripts");
            if (args.Count < 1)
            {
                Console.WriteLine("Usage: SFSearch Sources <plugin> [--sig Weapon|Armor] [--item <EditorID|FormKey>] [--with p1,p2] [--depth N] [--scripts] [--out report.md]");
                return;
            }

            var extra = withOpt == null ? new List<string>() : Program.TargetResolver(withOpt);
            using var store = ResearchCommands.RecordStore.Open(args[0], extra);
            using var report = Sfs.ReportOut.Begin(outFile);

            // The plugins whose records count as sources: the target and the --with plugins (not its masters: a vanilla
            // leveled list that happens to share an ID is not a source of a mod item).
            var sourcePlugins = new List<string> { store.Target };
            sourcePlugins.AddRange(extra.Where(p => !p.Equals(store.Target, StringComparison.OrdinalIgnoreCase)));
            var conns = new List<(string Plugin, SqliteConnection Conn)>();
            foreach (var p in sourcePlugins.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Sfs.HasCache(p)) continue;
                conns.Add((p, Sfs.OpenCache(p, false)));
            }

            try
            {
                var items = store.TargetRecords("Weapon", "Armor").Where(r => !r.IsOverride).ToList();
                if (sigOpt != null) items = items.Where(r => r.Signature.Equals(Program.NormalizeRecordTypePublic(sigOpt), StringComparison.OrdinalIgnoreCase)).ToList();
                if (itemOpt != null)
                {
                    string want = Sfs.IsFormKey(itemOpt) ? Sfs.NormalizeFormKey(itemOpt) : itemOpt;
                    items = items.Where(r => r.EditorID.Equals(want, StringComparison.OrdinalIgnoreCase) || r.FormKey.Equals(want, StringComparison.OrdinalIgnoreCase)).ToList();
                }
                items = items.OrderBy(r => r.Signature).ThenBy(r => r.EditorID, StringComparer.OrdinalIgnoreCase).ToList();

                Sfs.Say($"### Sources: {store.TargetFile}");
                Sfs.Say($"How the player gets each weapon and armor piece the plugin adds. Searched: {string.Join(", ", conns.Select(c => c.Plugin))}. Leveled-list depth: {depth}.");
                Sfs.Say();

                var results = new List<(ResearchCommands.Rec Item, List<string> Flags, List<Source> Sources, List<string> Checks)>();
                foreach (var item in items)
                {
                    var (sources, checks) = Walk(item.FormKey, store, conns, depth);
                    results.Add((item, Flags(item), sources, checks));
                }

                var t = new Sfs.Table("Item name", "Editor ID", "Type", "Verdict", "Gate");
                foreach (var (item, flags, sources, _) in results)
                    t.Add(Clean(item.Name), $"<sub>*{item.EditorID}*</sub>", item.Signature, Verdict(flags, sources), Gate(sources));
                Sfs.Print(t, annotate: false);

                var counts = results.GroupBy(r => Verdict(r.Flags, r.Sources).Split(',')[0].Trim()).OrderByDescending(g => g.Count());
                Sfs.Summary($"\n{results.Count} item(s): " + string.Join(", ", counts.Select(g => $"{g.Key} {g.Count()}")));

                Sfs.Say("\n#### Details");
                foreach (var (item, flags, sources, checks) in results)
                {
                    Sfs.Say($"\n**{Clean(item.Name)}**: {store.Show(item.FormKey)}, {item.Signature}{(flags.Count > 0 ? " · " + string.Join(" · ", flags) : "")}");
                    if (sources.Count == 0) Sfs.Say("- No source found in the searched plugins.");
                    foreach (var group in sources.GroupBy(s => s.Kind).OrderBy(g => Array.IndexOf(KindOrder, g.Key)))
                    {
                        var list = group.OrderBy(s => s.Via.Count).ThenBy(s => s.Text, StringComparer.OrdinalIgnoreCase).ToList();
                        // Loot spread through shared lists reaches hundreds of NPCs and cells: fold those kinds into one line.
                        if (list.Count > 5 && group.Key is "NPC" or "Placed" or "Container")
                        {
                            Sfs.Say($"- **{group.Key}:** {list.Count} sources, e.g. {string.Join("; ", list.Take(3).Select(s => s.Text))}");
                            continue;
                        }
                        foreach (var s in list)
                            Sfs.Say($"- **{s.Kind}:** {s.Text}{(s.Via.Count > 0 ? " · via " + string.Join(" → ", s.Via.Select(store.Show)) : "")}{(s.Routes > 1 ? $" (+{s.Routes - 1} other route{(s.Routes > 2 ? "s" : "")})" : "")}");
                    }
                    foreach (var c in checks.Distinct()) Sfs.Say($"- *Checked by (not a source):* {c}");
                }

                Sfs.Say("\n#### What this can't see");
                Sfs.Say("- Script code: a script that finds an item with `GetFormFromFile`, or adds it with `AddItem` on a FormID it builds itself, leaves no reference. Run with `--scripts` to list the scripts that call those functions.");
                Sfs.Say("- Vendors are recognised by an EditorID containing \"Vendor\" on the container or leveled list. A vendor chest named otherwise shows as a container.");
                Sfs.Say("- Cells without an EditorID show as their FormKey. Read one with `Live` to see where it is.");

                if (scripts) ScriptScan(store.TargetFile);
            }
            finally
            {
                foreach (var c in conns) c.Conn.Dispose();
            }
        }

        // ---------------------------------------------------------------- The walk
        static (List<Source> Sources, List<string> Checks) Walk(string itemFk, ResearchCommands.RecordStore store, List<(string Plugin, SqliteConnection Conn)> conns, int depth)
        {
            var sources = new List<Source>();
            var checks = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { itemFk };
            var queue = new Queue<(string Fk, List<string> Via)>();
            queue.Enqueue((itemFk, new List<string>()));

            void Add(string kind, string text, List<string> via)
            {
                // The walk is breadth-first, so the first route to a source is the shortest; later ones are only counted.
                var same = sources.FirstOrDefault(s => s.Kind == kind && s.Text == text);
                if (same != null) { if (!same.Via.SequenceEqual(via)) same.Routes++; return; }
                sources.Add(new Source { Kind = kind, Text = text, Via = via });
            }

            while (queue.Count > 0)
            {
                var (fk, via) = queue.Dequeue();

                foreach (var (cell, n) in Placed(conns, fk))
                    Add("Placed", $"placed in cell {store.Show(cell)}{(n > 1 ? $" ×{n}" : "")}", via);

                var inbound = InboundRefs(conns, fk);
                if (inbound.Count == 0 && via.Count > 0 && fk == via[^1] && !Placed(conns, fk).Any())
                    Add("Unused list", $"{store.Show(fk)} is used by nothing in the searched plugins", via.Take(via.Count - 1).ToList());

                foreach (var r in inbound)
                {
                    if (r.Path.Contains("Conditions", StringComparison.OrdinalIgnoreCase))
                    {
                        checks.Add($"{r.Signature} {store.Show(r.From)} ({ShortPath(r.Path)})");
                        continue;
                    }
                    if (r.Signature is "PlacedObject" or "PlacedNpc") continue; // placed copies come from Forms.Base

                    if (HopSignatures.Contains(r.Signature))
                    {
                        if (seen.Add(r.From) && via.Count < depth) queue.Enqueue((r.From, via.Append(r.From).ToList()));
                        continue;
                    }

                    var (kind, text) = Classify(r, store, conns);
                    Add(kind, text, via);
                }
            }
            return (sources, checks);
        }

        static List<Inbound> InboundRefs(List<(string Plugin, SqliteConnection Conn)> conns, string fk)
        {
            var list = new List<Inbound>();
            foreach (var (plugin, conn) in conns)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT FromFormKey, FromSignature, FromEditorID, Path FROM Refs WHERE {Sfs.FormKeyMatch("ToFormKey", "@fk")}";
                Sfs.AddFormKeyParams(cmd, fk, "@fk");
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new Inbound { Plugin = plugin, From = r.GetString(0), Signature = r.GetString(1), EditorID = r.IsDBNull(2) ? "" : r.GetString(2), Path = r.GetString(3) });
            }
            return list;
        }

        /// <summary>Placed copies of a base object, grouped by cell (Forms has every placed reference, in every cache mode).</summary>
        static List<(string Cell, int Count)> Placed(List<(string Plugin, SqliteConnection Conn)> conns, string baseFk)
        {
            var byCell = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, conn) in conns)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT Parent FROM Forms WHERE {Sfs.FormKeyMatch("Base", "@fk")} AND Signature IN ('PlacedObject','PlacedNpc')";
                Sfs.AddFormKeyParams(cmd, baseFk, "@fk");
                try
                {
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        string cell = r.IsDBNull(0) ? "(no cell)" : r.GetString(0);
                        byCell[cell] = byCell.TryGetValue(cell, out int n) ? n + 1 : 1;
                    }
                }
                catch (SqliteException) { } // an old cache without Forms.Base
            }
            return byCell.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        static (string Kind, string Text) Classify(Inbound r, ResearchCommands.RecordStore store, List<(string Plugin, SqliteConnection Conn)> conns)
        {
            string who = store.Show(r.From);
            string p = r.Path;
            bool vendor = r.EditorID.Contains("Vendor", StringComparison.OrdinalIgnoreCase);

            switch (r.Signature)
            {
                case "Quest":
                {
                    var stage = Regex.Match(p, @"^Stages\[(\d+)\].*Rewards");
                    if (stage.Success) return ("Quest", $"{who} reward at {StageName(store, r.From, int.Parse(stage.Groups[1].Value))}");
                    // Leveled-list injector scripts (DefaultAddItemToLeveledList on the TwoShot quests, LL injector arrays):
                    // the script names the list the item's list gets added to at runtime.
                    var (scriptName, targets) = AddToListTargets(store, r.From, p);
                    if (targets.Count > 0)
                    {
                        bool toVendor = targets.Any(s => store.Edid(s).Contains("Vendor", StringComparison.OrdinalIgnoreCase));
                        return (toVendor ? "Vendor" : "Injected", $"{who} script {scriptName} adds it to {string.Join(", ", targets.Select(store.Show))}");
                    }
                    if (Regex.IsMatch(p, @"^Aliases\[\d+\]\.CreateReferenceToObject")) return ("Quest", $"{who} alias creates it ({ShortPath(p)})");
                    if (Regex.IsMatch(p, @"^Aliases\[\d+\]\.Items")) return ("Quest", $"{who} alias inventory ({ShortPath(p)})");
                    if (p.StartsWith("DialogTopics", StringComparison.OrdinalIgnoreCase)) return ("Quest", $"{who} dialogue script property ({ShortPath(p)})");
                    if (p.Contains("VirtualMachineAdapter", StringComparison.OrdinalIgnoreCase)) return ("Quest", $"{who} script property ({ShortPath(p)})");
                    return ("Quest", $"{who} ({ShortPath(p)})");
                }
                case "ConstructibleObject":
                {
                    var rec = store.Get(r.From);
                    if (p.StartsWith("CreatedObject", StringComparison.OrdinalIgnoreCase))
                    {
                        string bench = rec?.Data["WorkbenchKeyword"] is JsonNode wb ? store.Show(wb.ToString()) : "no bench";
                        var conds = rec?.Data["Conditions"] is JsonArray ca ? ca.Select(c => ResearchCommands.CondText(store, c, out _)).ToList() : new List<string>();
                        return ("Recipe", $"made by {who} at {bench}; conditions: {(conds.Count == 0 ? "none" : string.Join(" · ", conds))}");
                    }
                    return ("Other", $"consumed by recipe {who} ({ShortPath(p)})");
                }
                case "Container":
                {
                    var cells = Placed(conns, r.From);
                    string where = cells.Count == 0 ? "not placed in the searched plugins" : "placed in " + string.Join(", ", cells.Take(4).Select(c => store.Show(c.Cell) + (c.Count > 1 ? $" ×{c.Count}" : ""))) + (cells.Count > 4 ? $" (+{cells.Count - 4} more)" : "");
                    return (vendor ? "Vendor" : "Container", $"in container {who}, {where}");
                }
                case "Npc":
                    if (p.Equals("Skin", StringComparison.OrdinalIgnoreCase)) return ("NPC", $"skin of NPC {who} (not lootable)");
                    if (p.StartsWith("DeathItem", StringComparison.OrdinalIgnoreCase)) return ("NPC", $"death item of NPC {who}");
                    if (p.Contains("Outfit", StringComparison.OrdinalIgnoreCase)) return ("NPC", $"worn by NPC {who} ({p})");
                    if (p.StartsWith("Items", StringComparison.OrdinalIgnoreCase)) return ("NPC", $"carried by NPC {who}");
                    if (p.Contains("VirtualMachineAdapter", StringComparison.OrdinalIgnoreCase)) return ("Script", $"script property on NPC {who} ({ShortPath(p)})");
                    return ("NPC", $"NPC {who} ({ShortPath(p)})");
                case "Race":
                    return ("NPC", $"skin of race {who} (not lootable)");
                case "LeveledNpc":
                    return ("NPC", $"leveled NPC {who} ({ShortPath(p)})");
                case "Faction":
                    return (vendor || p.Contains("Vendor", StringComparison.OrdinalIgnoreCase) ? "Vendor" : "Other", $"faction {who} ({ShortPath(p)})");
            }

            if (p.Contains("VirtualMachineAdapter", StringComparison.OrdinalIgnoreCase))
                return ("Script", $"script property on {r.Signature} {who} ({ShortPath(p)})");
            if (vendor) return ("Vendor", $"{r.Signature} {who} ({ShortPath(p)})");
            return ("Other", $"{r.Signature} {who} ({ShortPath(p)})");
        }

        /// <summary>The Refs path counts stage entries in list order; the stage number the game uses is the entry's Index.</summary>
        static string StageName(ResearchCommands.RecordStore store, string questFk, int entry)
        {
            var q = store.Get(questFk);
            if (q?.Data["Stages"] is JsonArray stages && entry < stages.Count && stages[entry]?["Index"] is JsonNode idx)
                return $"stage {idx}";
            return $"stage entry {entry}";
        }

        /// <summary>
        /// Leveled-list injector scripts. Two shapes:
        /// a struct that pairs the item's list with a target list (Conner_Template_LLInjectorArrayScript: FormToInject + TargetList),
        /// or a script whose name says it adds to a leveled list (DefaultAddItemToLeveledList: ItemToAdd struct + LeveledList property).
        /// Returns the script's name and the target leveled lists.
        /// </summary>
        static (string Script, List<string> Targets) AddToListTargets(ResearchCommands.RecordStore store, string fk, string path)
        {
            var result = new List<string>();
            var m = Regex.Match(path, @"^(VirtualMachineAdapter\.(?:Scripts\[\d+\]|Script))\.Properties\[(\d+)\]");
            if (!m.Success || store.Get(fk)?.Data is not JsonObject data) return ("", result);
            JsonNode? script = Navigate(data, m.Groups[1].Value);
            string name = script?["Name"]?.ToString() ?? "";

            void AddLists(JsonNode? n)
            {
                foreach (var o in Sfs.EmbeddedFormKey.Matches(n?.ToJsonString() ?? "").Select(x => Sfs.NormalizeFormKey(x.Value)))
                    if (store.Get(o)?.Signature == "LeveledItem" && !result.Contains(o, StringComparer.OrdinalIgnoreCase)) result.Add(o);
            }

            // Struct pair: the other members of the same struct.
            var sm = Regex.Match(path, @"^(.*\.Structs\[\d+\])\.Members\[(\d+)\]");
            if (sm.Success && Navigate(data, sm.Groups[1].Value)?["Members"] is JsonArray members && members.Count > 1)
            {
                int selfMember = int.Parse(sm.Groups[2].Value);
                for (int i = 0; i < members.Count; i++) if (i != selfMember) AddLists(members[i]);
                if (result.Count > 0) return (name, result);
            }

            // Whole script: only for scripts that say what they do, so a big quest script's other properties don't count.
            if (!Regex.IsMatch(name, "AddItemToLeveledList|LLInjector|Inject", RegexOptions.IgnoreCase) || script?["Properties"] is not JsonArray props) return (name, result);
            int self = int.Parse(m.Groups[2].Value);
            for (int i = 0; i < props.Count; i++) if (i != self) AddLists(props[i]);
            return (name, result);
        }

        static JsonNode? Navigate(JsonNode? node, string dottedPath)
        {
            foreach (var part in dottedPath.Split('.'))
            {
                var pm = Regex.Match(part, @"^(\w+)(?:\[(\d+)\])?$");
                if (!pm.Success) return null;
                node = node?[pm.Groups[1].Value];
                if (pm.Groups[2].Success) node = node is JsonArray a && int.Parse(pm.Groups[2].Value) < a.Count ? a[int.Parse(pm.Groups[2].Value)] : null;
            }
            return node;
        }

        static string ShortPath(string p)
        {
            p = p.Replace("VirtualMachineAdapter.", "VMAD.").Replace(".StageCompleteDatas[0].RewardDatas[0]", "");
            return p.Length > 90 ? p.Substring(0, 90) + "…" : p;
        }

        // ---------------------------------------------------------------- Flags and verdicts
        static List<string> Flags(ResearchCommands.Rec item)
        {
            var flags = new List<string>();
            string mf = item.Data["MajorFlags"]?.ToString() ?? "";
            bool nonPlayable = mf.Contains("NonPlayable", StringComparison.OrdinalIgnoreCase)
                || (long.TryParse(mf, out long bits) && (bits & 0x4) != 0)
                || string.Equals(item.Data["NonPlayable"]?.ToString(), "True", StringComparison.OrdinalIgnoreCase);
            if (nonPlayable) flags.Add("NonPlayable");
            if (item.Signature == "Armor" && item.Data["Race"]?.ToString() is string race && race.Length > 0 && !race.Equals(HumanRace, StringComparison.OrdinalIgnoreCase))
                flags.Add($"race {Sfs.EditorIdOf(Sfs.NormalizeFormKey(race)) ?? race}");
            return flags;
        }

        static string Verdict(List<string> flags, List<Source> sources)
        {
            var kinds = sources.Select(s => s.Kind).Distinct().OrderBy(k => Array.IndexOf(KindOrder, k)).ToList();
            var words = new List<string>();
            if (flags.Contains("NonPlayable")) words.Add("Unplayable");
            foreach (var k in kinds)
            {
                switch (k)
                {
                    case "Recipe": words.Add(sources.Any(s => s.Kind == "Recipe" && IsGated(s.Text)) ? "Craftable (gated)" : "Craftable"); break;
                    case "Quest": words.Add("Quest"); break;
                    case "Vendor": words.Add("Vendor"); break;
                    case "Injected": words.Add("Injected into lists"); break;
                    case "Container": case "Placed": if (!words.Contains("World loot")) words.Add("World loot"); break;
                    case "NPC": words.Add(sources.Where(s => s.Kind == "NPC").All(s => s.Text.Contains("not lootable")) ? "NPC skin" : "On NPCs"); break;
                    case "Script": words.Add("Script"); break;
                }
            }
            if (flags.Any(f => f.StartsWith("race "))) words.Add(flags.First(f => f.StartsWith("race ")));
            if (words.Count == 0 || words.All(w => w.StartsWith("race "))) words.Insert(0, sources.Any(s => s.Kind == "Unused list") ? "Unused list only" : "No source found");
            return string.Join(", ", words);
        }

        // A recipe is gated when it has any condition besides GetLevel.
        static bool IsGated(string recipeText)
        {
            int i = recipeText.IndexOf("conditions: ", StringComparison.Ordinal);
            if (i < 0) return false;
            string conds = recipeText.Substring(i + 12);
            return conds != "none" && conds.Split(" · ").Any(c => !c.StartsWith("GetLevel(", StringComparison.Ordinal));
        }

        static string Gate(List<Source> sources)
        {
            var gates = sources.Where(s => s.Kind == "Recipe").Select(s =>
            {
                int i = s.Text.IndexOf("conditions: ", StringComparison.Ordinal);
                string conds = i < 0 ? "" : s.Text.Substring(i + 12);
                var extra = conds.Split(" · ").Where(c => c != "none" && !c.StartsWith("GetLevel(", StringComparison.Ordinal)).ToList();
                return extra.Count == 0 ? "" : string.Join(" · ", extra);
            }).Where(g => g.Length > 0).Distinct().ToList();
            return string.Join(" | ", gates);
        }

        static string Clean(string name) => string.IsNullOrWhiteSpace(name) ? "(no name)" : name.Replace("|", "/");

        // ---------------------------------------------------------------- --scripts
        /// <summary>
        /// Lists the plugin's scripts whose compiled code calls GetFormFromFile or AddItem. Compiled Papyrus keeps every
        /// function name it calls in its string table, so a plain text search finds the calls without decompiling.
        /// Looks at loose files under Data\Scripts and inside the plugin's own BA2s.
        /// </summary>
        static void ScriptScan(string pluginFile)
        {
            Sfs.Say("\n#### Scripts that may hand out items in code (--scripts)");
            // Texture and voice archives never hold scripts; loose scripts count when their path mentions the plugin's name.
            var (pex, archives, looseCount, note) = ScriptsCommand.CollectPex(pluginFile);
            Sfs.Say($"Archives read: {(archives.Length == 0 ? "none found" : string.Join(", ", archives.Select(Path.GetFileName)))}. Loose scripts matched by name: {looseCount}. Scripts scanned: {pex.Count}.");
            if (note.Length > 0) Sfs.Say($"Note: {note}");
            var t = new Sfs.Table("Script", "GetFormFromFile", "AddItem", "Mentions a .esm");
            foreach (var (name, bytes) in pex.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                string text = Encoding.ASCII.GetString(bytes);
                bool gff = text.Contains("GetFormFromFile", StringComparison.OrdinalIgnoreCase);
                bool add = text.Contains("AddItem", StringComparison.OrdinalIgnoreCase);
                var esm = Regex.Matches(text, @"[A-Za-z0-9_\- ]+\.es[mpl]", RegexOptions.IgnoreCase).Select(m => m.Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (gff || add) t.Add(name, gff ? "yes" : "", add ? "yes" : "", string.Join(", ", esm));
            }
            if (t.Rows.Count == 0) Sfs.Say("No scanned script calls GetFormFromFile or AddItem.");
            else
            {
                Sfs.Print(t, annotate: false);
                Sfs.Say($"Read their source with `Scripts {pluginFile} --show <ScriptName>` (or `--find AddItem`) to see which items they hand out.");
            }
        }
    }
}
