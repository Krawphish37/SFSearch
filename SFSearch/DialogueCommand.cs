using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace SFSearch
{
    /// <summary>
    /// Dialogue: a quest's (or one topic's) dialogue as readable lines: each topic, then its lines in order with the speaker,
    /// the English text, the conditions, and what a line starts (a scene, a quest stage, a speech challenge). Dialogue isn't
    /// stored by default, so the plugin's DialogTopic and DialogResponses are added to its database the first time.
    /// </summary>
    public static class DialogueCommand
    {
        public static void Run(List<string> args)
        {
            string? inPlugin = Sfs.TakeOption(args, "--in");
            string? outFile = Sfs.TakeOption(args, "--out");
            if (args.Count < 1) { Console.WriteLine("Usage: SFSearch Dialogue <quest|topic FormKey|EditorID> [--in <plugin>] [--out file.md]"); return; }

            var prefer = inPlugin != null ? new List<string> { Program.EnsureCacheForFile(inPlugin) } : null;
            string? fk = Sfs.ResolveUserKey(args[0], prefer);
            if (fk == null) { Console.WriteLine($"Error: no record '{args[0]}' found."); return; }
            string plugin = prefer?[0] ?? Program.EnsureCacheForFile(Sfs.PluginOf(fk));

            EnsureDialogueStored(plugin);
            using var conn = Sfs.OpenCache(plugin, ensureUpToDate: false);
            var target = Sfs.FindRecord(conn, plugin, fk, "*");
            if (target == null) { Console.WriteLine($"Error: {LeveledCommand.Label(fk)} isn't in {plugin}. Give the plugin that has its dialogue with --in."); return; }

            var topics = new List<Sfs.RecordRow>();
            if (target.Signature == "DialogTopic") topics.Add(target);
            else if (target.Signature == "Quest")
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT FormKey, EditorID, Data FROM Records WHERE Signature = 'DialogTopic' AND upper(json_extract(Data, '$.Quest')) = upper(@q) ORDER BY FormKey";
                cmd.Parameters.AddWithValue("@q", fk);
                using var r = cmd.ExecuteReader();
                while (r.Read()) topics.Add(new Sfs.RecordRow { FormKey = r.GetString(0), EditorID = r.IsDBNull(1) ? "" : r.GetString(1), Signature = "DialogTopic", Plugin = plugin, Data = (JsonNode.Parse(r.GetString(2)) as JsonObject)! });
            }
            else { Console.WriteLine($"Error: {LeveledCommand.Label(fk)} is a {target.Signature}; give a Quest or a DialogTopic."); return; }

            var infos = Responses(conn);
            using var report = Sfs.ReportOut.Begin(outFile);
            Sfs.Say($"### Dialogue: {LeveledCommand.Label(fk)} ({target.Signature}, {plugin})");
            if (topics.Count == 0) { Sfs.Say("_No dialogue topics belong to it in this plugin. Another plugin may hold them: try --in <plugin>._"); return; }
            int lines = 0;
            foreach (var group in topics.GroupBy(t => $"{t.Data["Category"]} / {t.Data["Subtype"]}").OrderBy(g => g.Key))
            {
                Sfs.Say($"\n#### {group.Key} ({group.Count()} topic(s))");
                foreach (var topic in group)
                {
                    Sfs.Say($"\n**{LeveledCommand.Label(topic.FormKey)}**{(topic.Data["Priority"]?.ToString() is string pr && pr != "50" ? $" (priority {pr})" : "")}");
                    if (topic.Data["TopicInfoList"] is not JsonArray list || list.Count == 0) { Sfs.Say("- _(no lines)_"); continue; }
                    foreach (var item in list)
                    {
                        string infoKey = Sfs.NormalizeFormKey(item?.ToString() ?? "");
                        if (!infos.TryGetValue(infoKey, out var info)) { Sfs.Say($"- {infoKey}: _not in this plugin_"); continue; }
                        Sfs.Say("- " + Describe(infoKey, info));
                        lines++;
                    }
                }
            }
            Sfs.Summary($"{topics.Count} topic(s), {lines} dialogue entr(y/ies)");
        }

        static string Describe(string key, JsonObject d)
        {
            var parts = new List<string>();
            string speaker = Speaker(d);
            var texts = (d["Responses"] as JsonArray)?.OfType<JsonObject>()
                .Select(r => (Sfs.English(r["ResponseText"]) ?? "").Trim()).Where(t => t.Length > 0).Select(t => $"\"{t}\"").ToList() ?? new();
            parts.Add($"{(speaker.Length > 0 ? $"**{speaker}:** " : "")}{(texts.Count == 0 ? "_(no text)_" : string.Join(" ", texts))}");
            if (d["Conditions"] is JsonArray c && c.Count > 0)
            {
                var shown = new JsonArray(c.OfType<JsonObject>().Where(x => x["Data"]?["Function"]?.ToString() is not ("GetIsID" or "GetIsVoiceType")).Select(x => x.DeepClone()).ToArray());
                if (shown.Count > 0) parts.Add($"if {LeveledCommand.Conditions(shown)}");
            }
            if (d["StartScene"]?.ToString() is string sc && sc.Length > 0) parts.Add($"starts scene {LeveledCommand.Label(Sfs.NormalizeFormKey(sc))}");
            if (d["SetParentQuestStage"] is JsonObject st && st["OnEnd"]?.ToString() is string end && end != "-1") parts.Add($"sets quest stage {end}");
            if (d["SpeechChallenge"]?.ToString() is string ch && ch.Length > 0) parts.Add($"speech challenge {LeveledCommand.Label(Sfs.NormalizeFormKey(ch))}");
            if (d["SharedDialog"]?.ToString() is string sh && sh.Length > 0) parts.Add($"shares lines of {LeveledCommand.Label(Sfs.NormalizeFormKey(sh))}");
            return $"{string.Join("; ", parts)} <sub>{key}</sub>";
        }

        /// <summary>Who says it: the Speaker field, or a GetIsID / GetIsVoiceType condition.</summary>
        static string Speaker(JsonObject d)
        {
            if (d["Speaker"]?.ToString() is string s && s.Length > 0) return LeveledCommand.Label(Sfs.NormalizeFormKey(s));
            if (d["Conditions"] is JsonArray c)
                foreach (var x in c.OfType<JsonObject>())
                {
                    var data = x["Data"];
                    string fn = data?["Function"]?.ToString() ?? "";
                    string? p = fn == "GetIsID" ? data?["FirstParameter"]?.ToString() : fn == "GetIsVoiceType" ? (data?["VoiceTypeOrList"] ?? data?["FirstParameter"])?.ToString() : null;
                    if (!string.IsNullOrEmpty(p)) return LeveledCommand.Label(Sfs.NormalizeFormKey(p.Split(' ')[0]));
                }
            return "";
        }

        static Dictionary<string, JsonObject> Responses(SqliteConnection conn)
        {
            var map = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT FormKey, Data FROM Records WHERE Signature = 'DialogResponses'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (JsonNode.Parse(r.GetString(1)) is JsonObject o) map[Sfs.NormalizeFormKey(r.GetString(0))] = o;
            return map;
        }

        /// <summary>Dialogue types aren't stored by default: add them to the plugin's database (quietly) when missing.</summary>
        static void EnsureDialogueStored(string plugin)
        {
            using (var conn = Sfs.OpenCache(plugin))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Records WHERE Signature IN ('DialogTopic','DialogResponses')";
                long have = (long)(cmd.ExecuteScalar() ?? 0L);
                cmd.CommandText = "SELECT COUNT(*) FROM Forms WHERE Signature IN ('DialogTopic','DialogResponses')";
                long exist = Sfs.TableExists(conn, "Forms") ? (long)(cmd.ExecuteScalar() ?? 0L) : 1;
                if (have > 0 || exist == 0) return;
            }
            var orig = Console.Out;
            try
            {
                Console.SetOut(TextWriter.Null);
                AddCommand.Run(new List<string> { plugin, "DialogTopic", "DialogResponses" });
            }
            finally { Console.SetOut(orig); }
        }
    }
}
