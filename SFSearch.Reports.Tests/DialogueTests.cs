// dialogue, held against today's Dialogue on quests of Starfield.esm and of a mod (the same topics and lines),
// and the default mode (each topic and line from the version that wins in the load order) against the header index.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SFSearch.Index;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class DialogueTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    sealed record Case(string Name, string Target, string In, string What);

    /// <summary>Today's Dialogue reads the dialogue the record's own plugin holds (or --in): the same plugin is asked for here.</summary>
    static readonly Case[] Cases =
    {
        new("dialogue__MQ101", "MQ101", "Starfield.esm", "a main quest"),
        new("dialogue__MQ105", "MQ105", "Starfield.esm", "a main quest"),
        new("dialogue__SpeechChallenge", "0128D0:Starfield.esm", "Starfield.esm", "the speech challenges' shared lines"),
        new("dialogue__CF03", "CF03", "Starfield.esm", "a faction quest"),
        new("dialogue__DialogueGenericGuards", "DialogueGenericGuards", "Starfield.esm", "generic guards: few topics, many lines"),
        new("dialogue__ASEMQDialogue", "00A22B:aseveil.esm", "aseveil.esm", "a mod's quest"),
        new("dialogue__Barrett__in_Aurie", "1C7187:Starfield.esm", "Aurie_UpgradedCompanions.esm", "a mod's part of a quest of Starfield.esm"),
    };

    sealed class TodayLine { public string Key = "", Text = ""; public bool NotHere; }
    sealed class TodayTopic { public string Key = "", Group = ""; public string? Priority; public List<TodayLine> Lines = new(); }

    static List<TodayTopic> ParseToday(string text)
    {
        var topics = new List<TodayTopic>(); string group = ""; TodayTopic? current = null;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            var g = Regex.Match(line, @"^#### (.*) \((\d+) topic\(s\)\)$");
            if (g.Success) { group = g.Groups[1].Value; continue; }
            var t = Regex.Match(line, @"^\*\*(.+?)\*\*(?: \(priority ([^)]+)\))?$");
            if (t.Success && Ctx.FirstKey(t.Groups[1].Value) is { } key)
            {
                current = new TodayTopic { Key = key, Group = group, Priority = t.Groups[2].Success ? t.Groups[2].Value : null };
                topics.Add(current);
                continue;
            }
            if (current == null || !line.StartsWith("- ", StringComparison.Ordinal)) continue;
            if (line == "- _(no lines)_") continue;
            var missing = Regex.Match(line, @"^- (\S+): _not in this plugin_$");
            if (missing.Success) { current.Lines.Add(new TodayLine { Key = Ctx.Key(missing.Groups[1].Value), NotHere = true }); continue; }
            var sub = Regex.Match(line, @" <sub>([^<]+)</sub>$");
            if (!sub.Success) continue;
            current.Lines.Add(new TodayLine { Key = Ctx.Key(sub.Groups[1].Value), Text = line[2..sub.Index] });
        }
        return topics;
    }

    static readonly string[] Markers = { "; if ", "; starts scene ", "; sets quest stage ", "; speech challenge ", "; shares lines of " };

    public static string Today(Ctx ctx)
    {
        if (!ctx.HasToday) return "SKIP today's Dialogue wasn't given (--today, or --sfsearch and --home)";
        var s = ctx.Session;
        var report = new DialogueReport(s);
        var parts = new List<string>(); var sb = new StringBuilder();
        long topicsAll = 0, linesAll = 0, groupSpeakers = 0, sharedTexts = 0, extraLines = 0, onBegin = 0;
        foreach (var c in Cases)
        {
            string? text = ctx.Today(c.Name, c.Name.Contains("__in_") ? new[] { "Dialogue", c.Target, "--in", c.In } : new[] { "Dialogue", c.Target });
            if (text == null) return "SKIP today's Dialogue couldn't be run";
            var today = ParseToday(text);
            Check(today.Count > 0, $"{c.Name}: today's answer has no topic:\n{text[..Math.Min(300, text.Length)]}");
            var a = report.Dialogue(c.Target, new DialogueOptions { In = c.In });
            ctx.WriteReport(c.Name + ".txt", a.ToText(new ReportTextOptions { MaxChars = int.MaxValue }));

            // ---- the same topics, in the same groups
            var mine = a.Topics.ToDictionary(t => Ctx.Key(t.FormKey));
            var theirs = today.ToDictionary(t => t.Key);
            Check(mine.Count == theirs.Count && mine.Keys.All(theirs.ContainsKey), $"{c.Name}: {mine.Count} topics, today {theirs.Count}; only new: {string.Join(", ", mine.Keys.Where(k => !theirs.ContainsKey(k)).Take(5))}; only today: {string.Join(", ", theirs.Keys.Where(k => !mine.ContainsKey(k)).Take(5))}");
            int lines = 0, caseExtra = 0, caseGroup = 0, caseShared = 0, missing = 0, scenes = 0, stages = 0, challenges = 0, withSpeaker = 0, withConditions = 0;
            foreach (var (key, t) in mine)
            {
                var tt = theirs[key];
                Check(t.Group == tt.Group, $"{c.Name}: topic {key} is in \"{t.Group}\"; today \"{tt.Group}\"");
                Check((tt.Priority == null && t.Priority == 50) || (tt.Priority != null && double.Parse(tt.Priority, CultureInfo.InvariantCulture) == t.Priority), $"{c.Name}: topic {key} priority {t.Priority}; today {tt.Priority ?? "50"}");
                // The lines today's lists are the first ones here, in the same order. Lines the topic holds and its
                // own list doesn't name follow them here; today's leaves them out.
                Check(t.Lines.Count >= tt.Lines.Count, $"{c.Name}: topic {key} has {t.Lines.Count} lines, today {tt.Lines.Count}");
                for (int i = 0; i < t.Lines.Count; i++)
                {
                    var l = t.Lines[i];
                    if (i >= tt.Lines.Count)
                    {
                        // Held against the index: the line sits in this topic in the plugin read
                        var info = s.Engine.Index.Describe((s.Winner(l.FormKey) ?? throw new TestFailed($"{l.FormKey} isn't anywhere")).Ref);
                        var p = s.Index.Plugin(c.In)!;
                        HeaderIndex.TryParseFormKey(l.FormKey, out var lo, out var lid);
                        var held = ReportSession.In(s.Engine, p.Name, lo, lid) ?? throw new TestFailed($"{c.Name}: the extra line {l.FormKey} isn't in {c.In}");
                        Check(held.Info.Parent != null && Ctx.Key(held.Info.Parent) == key, $"{c.Name}: the extra line {l.FormKey} isn't nested in topic {key}");
                        caseExtra++;
                        continue;
                    }
                    var tl = tt.Lines[i];
                    string at = $"{c.Name} topic {key} line {i} [{tl.Key}]";
                    Check(Ctx.Key(l.FormKey) == tl.Key, $"{at}: the new answer has {l.FormKey} there");
                    Check(l.Missing == tl.NotHere, $"{at}: {(l.Missing ? "not in the plugin" : "in the plugin")}; today {(tl.NotHere ? "not in the plugin" : "in the plugin")}");
                    if (l.Missing) { missing++; continue; }
                    lines++;
                    string rest = tl.Text;
                    // The speaker: today's takes it from the line itself only
                    var sp = Regex.Match(rest, @"^\*\*(.+?):\*\* ");
                    string? todaySpeaker = sp.Success ? Ctx.FirstKey(sp.Groups[1].Value) ?? sp.Groups[1].Value : null;
                    if (sp.Success) rest = rest[sp.Length..];
                    string? mySpeaker = l.SpeakerFrom == "its group" ? null : l.SpeakerKey;
                    if (l.SpeakerFrom == "its group") caseGroup++;
                    if (l.SpeakerKey != null) withSpeaker++;
                    Check((mySpeaker == null) == (todaySpeaker == null) && (mySpeaker == null || Ctx.Key(mySpeaker) == todaySpeaker || todaySpeaker!.Equals(mySpeaker, StringComparison.OrdinalIgnoreCase)), $"{at}: speaker {mySpeaker ?? "(none)"}; today {todaySpeaker ?? "(none)"}");
                    // The text: today's prints the line's own responses
                    var own = l.TextsShared ? new List<string>() : l.Texts;
                    if (l.TextsShared) caseShared++;
                    string said = own.Count == 0 ? "_(no text)_" : string.Join(" ", own.Select(t2 => $"\"{t2}\""));
                    Check(rest.StartsWith(said, StringComparison.Ordinal) && (rest.Length == said.Length || rest[said.Length..].StartsWith("; ", StringComparison.Ordinal)), $"{at}: text {said}; today {rest}");
                    rest = rest[said.Length..];
                    // What follows the text
                    string Part(string marker)
                    {
                        int from = rest.IndexOf(marker, StringComparison.Ordinal);
                        if (from < 0) return "";
                        int to = Markers.Where(m => m != marker).Select(m => rest.IndexOf(m, from + marker.Length, StringComparison.Ordinal)).Where(x => x >= 0).DefaultIfEmpty(rest.Length).Min();
                        return rest[(from + marker.Length)..to];
                    }
                    var conds = l.ShownConditions.ToList();
                    var todayConds = LeveledTests_Conditions(Part("; if "));
                    if (conds.Count > 0) withConditions++;
                    Check(conds.Count == todayConds.Count, $"{at}: {conds.Count} conditions ({ConditionReader.Text(conds)}); today {todayConds.Count} ({Part("; if ")})");
                    for (int k = 0; k < conds.Count; k++)
                        Check(conds[k].Function == todayConds[k].Function && conds[k].OperatorSign == todayConds[k].Op && (conds[k].Global != null || (conds[k].Value ?? double.NaN).Equals(todayConds[k].Value)),
                            $"{at}: condition {k} is {conds[k].Text}; today {todayConds[k].Function} {todayConds[k].Op} {todayConds[k].Value}");
                    string scene = Part("; starts scene ");
                    Check((l.StartsSceneKey == null) == (scene.Length == 0) && (scene.Length == 0 || Ctx.FirstKey(scene) == Ctx.Key(l.StartsSceneKey!)), $"{at}: starts scene {l.StartsSceneKey ?? "(none)"}; today {scene}");
                    if (l.StartsSceneKey != null) scenes++;
                    string stage = Part("; sets quest stage ");
                    Check((l.StageOnEnd == null) == (stage.Length == 0) && (stage.Length == 0 || stage.Trim() == l.StageOnEnd.ToString()), $"{at}: sets quest stage {l.StageOnEnd?.ToString() ?? "(none)"}; today {stage}");
                    if (l.StageOnEnd != null) stages++;
                    if (l.StageOnBegin != null) onBegin++;
                    string challenge = Part("; speech challenge ");
                    Check((l.SpeechChallengeKey == null) == (challenge.Length == 0) && (challenge.Length == 0 || Ctx.FirstKey(challenge) == Ctx.Key(l.SpeechChallengeKey!)), $"{at}: speech challenge {l.SpeechChallengeKey ?? "(none)"}; today {challenge}");
                    if (l.SpeechChallengeKey != null) challenges++;
                    string shares = Part("; shares lines of ");
                    Check((l.SharedKey == null) == (shares.Length == 0) && (shares.Length == 0 || Ctx.FirstKey(shares) == Ctx.Key(l.SharedKey!)), $"{at}: shares the lines of {l.SharedKey ?? "(none)"}; today {shares}");
                }
            }
            topicsAll += mine.Count; linesAll += lines; groupSpeakers += caseGroup; sharedTexts += caseShared; extraLines += caseExtra;
            parts.Add($"{a.Target} in {c.In} ({c.What}): {N(mine.Count)} topics, {N(lines)} lines ({N(withSpeaker)} with a speaker, {N(withConditions)} with conditions, {scenes} start a scene, {stages} set a quest stage, {challenges} a speech challenge" +
                (missing > 0 ? $", {N(missing)} named by a topic and not in the plugin" : "") + (caseExtra > 0 ? $", {caseExtra} more that the topic holds and its list doesn't name" : "") + ")");
            sb.AppendLine(parts[^1]);
        }
        ctx.WriteReport("dialogue-today.txt", sb.ToString());
        return $"{Cases.Length} quests against today's Dialogue: the same {N(topicsAll)} topics in the same groups and the same {N(linesAll)} lines in the same order, each with the same text, speaker, conditions, scene, quest stage, speech challenge and shared line. " +
            $"More in the new report: {N(groupSpeakers)} lines get their speaker from the line they are grouped under (today's shows none), {N(sharedTexts)} lines that share another line's responses show that line's text (today's shows \"(no text)\"), " +
            $"{N(extraLines)} lines sit in a topic whose own list doesn't name them (today's leaves them out; each was found nested in the topic in the plugin), {N(onBegin)} lines set a quest stage when they begin (today's shows only the stage set at the end). {string.Join("; ", parts)}.";
    }

    static List<(string Function, string Op, double Value, string? Key)> LeveledTests_Conditions(string text) => LeveledTests.ParseConditionsOf(text);

    // ---------------------------------------------------------------- The version that wins
    public static string Winners(Ctx ctx)
    {
        var s = ctx.Session;
        var report = new DialogueReport(s);
        const string quest = "1C7187:Starfield.esm";   // Com_Companion_Barrett: a loaded mod overrides some of its lines
        var a = report.Dialogue(quest);
        var only = report.Dialogue(quest, new DialogueOptions { In = "Starfield.esm" });
        int fromOthers = 0, differ = 0, checkedLines = 0, added = 0;
        var byPlugin = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var own = only.Topics.SelectMany(t => t.Lines).Where(l => !l.Missing).ToDictionary(l => Ctx.Key(l.FormKey));
        foreach (var t in a.Topics)
        {
            var tw = s.Winner(t.FormKey) ?? throw new TestFailed($"{t.FormKey} has no version");
            Check(t.Plugin.Equals(tw.Plugin.Name, StringComparison.OrdinalIgnoreCase), $"topic {t.Label} was read from {t.Plugin}; the index says {tw.Plugin.Name} wins");
            foreach (var l in t.Lines.Where(l => !l.Missing))
            {
                var w = s.Winner(l.FormKey) ?? throw new TestFailed($"{l.FormKey} has no version");
                Check(l.Plugin.Equals(w.Plugin.Name, StringComparison.OrdinalIgnoreCase), $"line {l.FormKey} was read from {l.Plugin}; the index says {w.Plugin.Name} wins");
                byPlugin[l.Plugin] = byPlugin.GetValueOrDefault(l.Plugin) + 1;
                checkedLines++;
                if (l.Plugin.Equals("Starfield.esm", StringComparison.OrdinalIgnoreCase)) continue;
                fromOthers++;
                if (!own.TryGetValue(Ctx.Key(l.FormKey), out var mine)) { added++; continue; }
                if (!mine.Texts.SequenceEqual(l.Texts) || ConditionReader.Text(mine.Conditions) != ConditionReader.Text(l.Conditions)) differ++;
            }
        }
        Check(a.Topics.Count >= only.Topics.Count && a.LineCount >= only.LineCount, $"the winners' answer has {a.Topics.Count} topics and {a.LineCount} lines; Starfield.esm alone {only.Topics.Count} and {only.LineCount}");
        Check(fromOthers > 0, "no line of the quest is won by another plugin: pick another quest for this test");
        ctx.WriteReport("dialogue-winners.txt", a.ToText(new ReportTextOptions { MaxChars = 60000 }));
        return $"{a.Target}, each topic and line from the version that wins: {N(a.Topics.Count)} topics, {N(checkedLines)} lines, every one read from the plugin the header index names as the winner ({string.Join(", ", byPlugin.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {N(k.Value)}"))}). " +
            $"{N(fromOthers)} lines come from another plugin than Starfield.esm: {N(added)} it adds, {N(differ)} whose text or conditions differ from Starfield.esm's version. Starfield.esm alone: {N(only.Topics.Count)} topics, {N(only.LineCount)} lines. {N(a.Searched.Count)} plugins were looked through.";
    }

    // ---------------------------------------------------------------- The chosen language
    /// <summary>The texts in German, held against Mutagen's own lookup of each response's text in that language.</summary>
    public static string Language(Ctx ctx)
    {
        var s = ctx.Session;
        var report = new DialogueReport(s);
        const string quest = "MQ101", plugin = "Starfield.esm";
        var english = report.Dialogue(quest, new DialogueOptions { In = plugin });
        var german = report.Dialogue(quest, new DialogueOptions { In = plugin, Language = "German" });
        Check(german.Language == "German" && german.Topics.Count == english.Topics.Count && german.LineCount == english.LineCount, $"German: {german.Topics.Count} topics and {german.LineCount} lines; English {english.Topics.Count} and {english.LineCount}");
        int lines = 0, texts = 0, differ = 0;
        var en = english.Topics.SelectMany(t => t.Lines).ToDictionary(l => l.FormKey);
        s.Engine.Sessions.Use(plugin);
        foreach (var l in german.Topics.SelectMany(t => t.Lines).Where(l => !l.Missing && !l.TextsShared))
        {
            HeaderIndex.TryParseFormKey(l.FormKey, out var owner, out var id);
            var hit = ReportSession.In(s.Engine, plugin, owner, id) ?? throw new TestFailed($"{l.FormKey} isn't in {plugin}");
            var rec = (Mutagen.Bethesda.Starfield.IDialogResponsesGetter)s.Engine.Load(hit.Ref).Record;
            var expect = new List<string>();
            foreach (var r in rec.Responses)
                if (r.ResponseText is { } t && t.TryLookup(Mutagen.Bethesda.Strings.Language.German, out var text) && text.Trim().Length > 0) expect.Add(text.Trim());
            Check(expect.SequenceEqual(l.Texts), $"line {l.FormKey}: {l.Texts.Count} German texts in the answer, {expect.Count} from Mutagen's own lookup, or they differ");
            lines++; texts += expect.Count;
            if (!en[l.FormKey].Texts.SequenceEqual(l.Texts)) differ++;
        }
        Check(differ > lines / 2, $"only {differ} of {lines} lines have another text in German than in English");
        return $"{english.Target} in German: the same {german.Topics.Count} topics and {N(german.LineCount)} lines as in English; {N(texts)} texts of {N(lines)} lines each equal to Mutagen's own lookup of that response in German; {N(differ)} lines read differently than in English.";
    }
}
