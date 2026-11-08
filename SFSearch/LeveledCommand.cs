using System.Text.Json.Nodes;

namespace SFSearch
{
    /// <summary>
    /// Leveled: unfold a leveled list (items, NPCs, ships and other base forms) into everything it can give: each entry's level,
    /// count, chance of nothing and conditions, with nested lists unfolded under it. Each list is read from the version that
    /// wins in the load order (or from --in). The odds themselves depend on the player's level and the list's flags, so they
    /// are shown as the game's raw settings, not computed.
    /// </summary>
    public static class LeveledCommand
    {
        sealed record Row(int Depth, string Level, string Count, string ChanceNone, string Item, string Type, string Conditions, string From);

        public static void Run(List<string> args)
        {
            string? inPlugin = Sfs.TakeOption(args, "--in");
            string? depthOpt = Sfs.TakeOption(args, "--depth");
            string? outFile = Sfs.TakeOption(args, "--out");
            if (args.Count < 1) { Console.WriteLine("Usage: SFSearch Leveled <list FormKey|EditorID> [--in <plugin>] [--depth N] [--out file.md]"); return; }
            int maxDepth = int.TryParse(depthOpt, out int d) ? Math.Clamp(d, 1, 20) : 8;

            var prefer = inPlugin != null ? new List<string> { Program.EnsureCacheForFile(inPlugin) } : null;
            string? fk = Sfs.ResolveUserKey(args[0], prefer);
            if (fk == null) { Console.WriteLine($"Error: no record '{args[0]}' found{(inPlugin != null ? $" in {inPlugin}" : "")}."); return; }

            var positions = LoadOrderIndex.Positions();
            var rows = new List<Row>();
            var headers = new List<string>();
            var onPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var top = Fetch(fk, prefer?[0], positions);
            if (top == null) { Console.WriteLine($"Error: {fk} wasn't found in any plugin's database."); return; }
            if (top.Value.Row.Data["Entries"] is not JsonArray) { Console.WriteLine($"Error: {Label(fk)} is a {top.Value.Row.Signature}, not a leveled list."); return; }

            void Unfold(string key, Sfs.RecordRow rec, string plugin, int depth)
            {
                onPath.Add(key);
                var data = rec.Data;
                string flags = data["Flags"]?.ToString() ?? "";
                headers.Add($"{new string(' ', depth * 2)}- **{Label(key)}** ({rec.Signature}, from {plugin}): chance of nothing {data["ChanceNone"]?.ToString() ?? "0"}%" +
                            $"{(flags is "" or "0" ? "" : $", flags {flags}")}{(data["MaxCount"]?.ToString() is string mc && mc != "0" ? $", at most {mc}" : "")}" +
                            $"{(data["Conditions"] is JsonArray lc && lc.Count > 0 ? $", list conditions: {Conditions(lc)}" : "")}" +
                            $"{(data["FilterKeywordChances"] is JsonArray fkc && fkc.Count > 0 ? $", filter keywords: {string.Join("; ", fkc.Select(x => $"{Label(x?["Keyword"]?.ToString())} {x?["Chance"]}"))}" : "")}");
                foreach (var e in (data["Entries"] as JsonArray)!)
                {
                    if (e is not JsonObject entry) continue;
                    string refKey = Sfs.NormalizeFormKey(entry["Reference"]?.ToString() ?? "");
                    var child = refKey.Length == 0 ? null : Fetch(refKey, null, positions);
                    rows.Add(new Row(depth, entry["Level"]?.ToString() ?? "", entry["Count"]?.ToString() ?? "", entry["ChanceNone"]?.ToString() ?? "0",
                        $"{new string('·', depth * 2)}{(depth > 0 ? " " : "")}{Label(refKey)}", child?.Row.Signature ?? "(not found)",
                        entry["Conditions"] is JsonArray c ? Conditions(c) : "", Label(key)));
                    if (child is { } ch && ch.Row.Data["Entries"] is JsonArray)
                    {
                        if (onPath.Contains(refKey)) { rows.Add(new Row(depth + 1, "", "", "", $"{new string('·', (depth + 1) * 2)} (loops back to {Label(refKey)})", "", "", "")); continue; }
                        if (depth + 1 >= maxDepth) { rows.Add(new Row(depth + 1, "", "", "", $"{new string('·', (depth + 1) * 2)} (deeper than --depth {maxDepth})", "", "", "")); continue; }
                        Unfold(refKey, ch.Row, ch.Plugin, depth + 1);
                    }
                }
                onPath.Remove(key);
            }
            Unfold(fk, top.Value.Row, top.Value.Plugin, 0);

            using var report = Sfs.ReportOut.Begin(outFile);
            Sfs.Say($"### Leveled list: {Label(fk)}");
            Sfs.Say("Each list is read from the version that wins in your load order (or from --in). Chances are the game's settings: what a roll gives also depends on the player's level and the flags (UseAll gives every entry; CalculateFromAllLevelsLessThanOrEqualPlayer picks among all entries at or below the player's level).\n");
            foreach (var h in headers) Sfs.Say(h);
            Sfs.Say("");
            var t = new Sfs.Table("Level", "Count", "Chance none", "Entry", "Type", "Conditions", "In list");
            foreach (var r in rows) t.Add(r.Level, r.Count, r.ChanceNone, r.Item, r.Type, r.Conditions, r.From);
            Sfs.Print(t, annotate: false);
            int leaves = rows.Count(r => r.Type is not ("" or "(not found)") && !r.Type.StartsWith("Leveled"));
            Sfs.Summary($"{headers.Count} list(s), {rows.Count(r => r.Type.Length > 0)} entries, {leaves} of them items or forms (not lists), {rows.Count(r => r.Type == "(not found)")} not found");
        }

        /// <summary>The record's version that wins in the load order (or the one in <paramref name="forced"/>), with its plugin.</summary>
        static (Sfs.RecordRow Row, string Plugin)? Fetch(string fk, string? forced, Dictionary<string, (int Pos, bool Enabled)> positions)
        {
            if (forced != null) return Sfs.FindRecord(forced, fk) is { } f ? (f, forced) : null;
            var holders = LoadOrderIndex.HoldersIfFresh(fk) ?? new List<string> { Sfs.PluginOf(fk) };
            foreach (var p in holders.OrderByDescending(h => positions.TryGetValue(h, out var x) && x.Enabled ? x.Pos : -1))
            {
                try { if (Sfs.FindRecord(p, fk) is { } r) return (r, p); }
                catch (FileNotFoundException) { }
            }
            return null;
        }

        internal static string Label(string? fk)
        {
            if (string.IsNullOrEmpty(fk)) return "";
            string? e = Sfs.EditorIdOf(fk);
            return string.IsNullOrEmpty(e) ? fk : $"{e} ({fk})";
        }

        internal static string Conditions(JsonArray conds) =>
            string.Join(" ", conds.OfType<JsonObject>().Select((c, i) =>
            {
                var data = c["Data"];
                string fn = data?["Function"]?.ToString() ?? "?";
                string p1 = data?["FirstParameter"]?.ToString() ?? "";
                string on = data?["RunOnType"]?.ToString() is string ro && ro != "Subject" ? $" on {ro}" : "";
                string op = (c["CompareOperator"]?.ToString() ?? "") switch
                {
                    "EqualTo" => "=", "NotEqualTo" => "≠", "GreaterThan" => ">", "GreaterThanOrEqualTo" => "≥", "LessThan" => "<", "LessThanOrEqualTo" => "≤", var o => o
                };
                string join = i < conds.Count - 1 ? ((c["Flags"]?.ToString() ?? "").Contains("OR") ? " OR" : " AND") : "";
                string arg = p1.Length > 0 && p1 != "0" ? $"({Label(Sfs.NormalizeFormKey(p1.Split(' ')[0]))})" : "";
                return $"{fn}{arg}{on} {op} {c["ComparisonValue"]}{join}";
            }));
    }
}
