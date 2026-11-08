using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace SFSearch
{
    /// <summary>
    /// `Fields <plugin(s)> <Type>` (2026-11-07): every JSON path a record type has in the database's `Data`, how many records
    /// have it, and a sample value, so an agent writes `json_extract(Data, '$.AmmoType')` right the first time instead of
    /// guessing. Arrays show as `[*]`. Reads at most --max records per plugin (2,000 by default).
    /// </summary>
    public static class FieldsCommand
    {
        public static void Run(List<string> args)
        {
            int max = int.TryParse(Sfs.TakeOption(args, "--max"), out int m) && m > 0 ? m : 2000;
            string? filter = Sfs.TakeOption(args, "--filter");
            if (args.Count < 2)
            {
                Console.WriteLine("Usage: SFSearch Fields <plugin[,plugin2|glob]> <Type> [--filter text] [--max N]   (the JSON paths a record type has, for json_extract)");
                return;
            }
            string type = Program.NormalizeRecordTypePublic(args[1]);
            var targets = Program.TargetResolver(args[0], false);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var samples = new Dictionary<string, string>(StringComparer.Ordinal);
            int records = 0, plugins = 0;
            foreach (var plugin in targets)
            {
                // The service and --live read only the types a command needs: ask for this one
                LiveDb.Forced = new LiveDb.Needs { Records = true, Types = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { type } };
                try { Program.EnsureCacheUpToDatePublic(plugin); }
                catch (Exception e) { Console.Error.WriteLine($"[sfs] {plugin}: {e.Message}"); continue; }
                finally { LiveDb.Forced = null; }
                if (!Sfs.HasCache(plugin)) continue;
                using var conn = Sfs.OpenCache(plugin, ensureUpToDate: false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Data FROM Records WHERE Signature = @t ORDER BY FormKey LIMIT @n";
                cmd.Parameters.AddWithValue("@t", type);
                cmd.Parameters.AddWithValue("@n", max);
                using var r = cmd.ExecuteReader();
                bool any = false;
                while (r.Read())
                {
                    if (r.IsDBNull(0)) continue;
                    JsonNode? node;
                    try { node = JsonNode.Parse(r.GetString(0)); } catch (System.Text.Json.JsonException) { continue; }
                    if (node == null) continue;
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    Walk(node, "$", seen, samples);
                    foreach (var p in seen) counts[p] = counts.TryGetValue(p, out int c) ? c + 1 : 1;
                    records++;
                    any = true;
                }
                if (any) plugins++;
            }
            if (records == 0)
            {
                Console.WriteLine($"No {type} records found in {string.Join(", ", targets)}. Check the type name with 'Stats <plugin>' (OMODs are WeaponModification / ArmorModification).");
                return;
            }
            var t = new Sfs.Table("Path", "Records", "Sample");
            foreach (var (path, n) in counts.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (filter != null && !path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                t.Add(path, n, samples.TryGetValue(path, out var s) ? s : "");
            }
            Sfs.Say($"### {type}: fields in {records} record(s) of {plugins} plugin(s)\n");
            Sfs.Print(t);
            Sfs.Say($"\nUse a path in SQL as json_extract(Data, '<path>'); for a [*] path, json_each over the part before [*] (e.g. FROM Records r, json_each(r.Data, '$.Keywords') k).");
        }

        static void Walk(JsonNode node, string path, HashSet<string> seen, Dictionary<string, string> samples)
        {
            switch (node)
            {
                case JsonObject o:
                    if (o.Count == 0) { Leaf(path, "{}", seen, samples); return; }
                    foreach (var (k, v) in o)
                    {
                        string child = path + "." + (IsSimple(k) ? k : "\"" + k.Replace("\"", "\\\"") + "\"");
                        if (v == null) Leaf(child, "null", seen, samples);
                        else Walk(v, child, seen, samples);
                    }
                    return;
                case JsonArray a:
                    if (a.Count == 0) { Leaf(path, "[]", seen, samples); return; }
                    foreach (var item in a)
                        if (item != null) Walk(item, path + "[*]", seen, samples);
                    return;
                default:
                    Leaf(path, node.ToJsonString().Trim('"'), seen, samples);
                    return;
            }
        }

        static bool IsSimple(string key) => key.Length > 0 && key.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

        static void Leaf(string path, string value, HashSet<string> seen, Dictionary<string, string> samples)
        {
            seen.Add(path);
            if (!samples.TryGetValue(path, out var have) || (have is "" or "null" or "[]" or "{}" && value is not ("" or "null" or "[]" or "{}")))
                samples[path] = value.Length > 70 ? value[..70] + "…" : value;
        }
    }
}
