// dialogue: what do this quest's (or one topic's) characters say? Topics and their lines with the speaker, the text
// in the chosen language, the conditions, and what a line starts: a scene, a quest stage, a speech challenge.
// Topics and lines are found through the header index (a topic's parent is its quest, a line's parent its topic),
// in every loaded plugin, each read from the version that wins in the load order; or in one plugin when asked.
using System.Text.Json.Nodes;
using SFSearch.Compare;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Reports;

using static ReportAnswer;

public sealed class DialogueOptions
{
    /// <summary>Read the dialogue from this plugin only (a name in Data, or a file's path).</summary>
    public string? In { get; init; }
    /// <summary>The language of the texts. Null: the session's.</summary>
    public string? Language { get; init; }
}

public sealed class DialogueLine
{
    public required string FormKey { get; init; }
    public required string Label { get; init; }
    /// <summary>The plugin this version of the line was read from.</summary>
    public required string Plugin { get; init; }
    /// <summary>Who says it (EditorID (FormID:plugin)), and where that comes from: "Speaker" (the line's own field),
    /// "GetIsID" or "GetIsVoiceType" (a condition of the line), or "its group" (the line it is grouped under).</summary>
    public string? Speaker { get; set; }
    public string? SpeakerKey { get; set; }
    public string? SpeakerFrom { get; set; }
    /// <summary>The line's responses, in order, in the chosen language. Empty texts are left out.</summary>
    public List<string> Texts { get; } = new();
    /// <summary>The line whose responses this line shares (SharedDialog), as a label and as a FormKey.</summary>
    public string? SharedFrom { get; set; }
    public string? SharedKey { get; set; }
    /// <summary>True when the texts are the shared line's (this line has none of its own).</summary>
    public bool TextsShared { get; set; }
    /// <summary>The line this one is grouped under (DialogGroup): its speaker and conditions hold for this line too.</summary>
    public string? GroupKey { get; set; }
    /// <summary>The text the player picks, when the line has one.</summary>
    public string? Prompt { get; set; }
    public List<ConditionInfo> Conditions { get; set; } = new();
    public string? StartsScene { get; set; }
    public string? StartsSceneKey { get; set; }
    public string? ScenePhase { get; set; }
    /// <summary>The quest stage the line sets when it begins and when it ends. Null: none.</summary>
    public int? StageOnBegin { get; set; }
    public int? StageOnEnd { get; set; }
    public string? SpeechChallenge { get; set; }
    public string? SpeechChallengeKey { get; set; }
    /// <summary>Set when the topic's list names the line but no plugin read holds it.</summary>
    public bool Missing { get; init; }

    /// <summary>The conditions that aren't the one the speaker was taken from.</summary>
    public IEnumerable<ConditionInfo> ShownConditions => Conditions.Where(c => c.Function is not ("GetIsID" or "GetIsVoiceType"));

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["record"] = Label, ["formKey"] = FormKey, ["readFrom"] = Plugin };
        if (Missing) { o["missing"] = true; return o; }
        if (Speaker != null) { o["speaker"] = Speaker; o["speakerFrom"] = SpeakerFrom; }
        if (GroupKey != null) o["group"] = GroupKey;
        if (TextsShared) o["textsAreTheSharedLines"] = true;
        if (Prompt != null) o["prompt"] = Prompt;
        o["texts"] = new JsonArray(Texts.Select(t => (JsonNode)t).ToArray());
        if (SharedFrom != null) o["sharesLinesOf"] = SharedFrom;
        if (Conditions.Count > 0) o["conditions"] = new JsonArray(Conditions.Select(c => (JsonNode)c.ToJson()).ToArray());
        if (StartsScene != null) o["startsScene"] = StartsScene;
        if (ScenePhase != null) o["scenePhase"] = ScenePhase;
        if (StageOnBegin != null) o["setsQuestStageOnBegin"] = StageOnBegin;
        if (StageOnEnd != null) o["setsQuestStageOnEnd"] = StageOnEnd;
        if (SpeechChallenge != null) o["speechChallenge"] = SpeechChallenge;
        return o;
    }

    public string Line()
    {
        string key = $"[{FormKey}]";
        if (Missing) return $"  - {key}: the topic's list names it, but no plugin read holds it";
        var parts = new List<string>();
        string said = Texts.Count == 0 ? "(no text)" : string.Join(" ", Texts.Select(t => $"\"{t}\""));
        parts.Add((Speaker != null ? Speaker + ": " : "") + (Prompt != null ? $"[player: \"{Prompt}\"] " : "") + said);
        if (SharedFrom != null) parts.Add($"shares the lines of {SharedFrom}");
        var conds = ShownConditions.ToList();
        if (conds.Count > 0) parts.Add("if " + ConditionReader.Text(conds));
        if (StartsScene != null) parts.Add($"starts scene {StartsScene}{(ScenePhase != null ? $" at phase \"{ScenePhase}\"" : "")}");
        if (StageOnBegin != null) parts.Add($"sets quest stage {StageOnBegin} when it begins");
        if (StageOnEnd != null) parts.Add($"sets quest stage {StageOnEnd}");
        if (SpeechChallenge != null) parts.Add($"speech challenge {SpeechChallenge}");
        return $"  - {string.Join("; ", parts)}   {key}";
    }
}

public sealed class DialogueTopic
{
    public required string FormKey { get; init; }
    public required string Label { get; init; }
    public required string Plugin { get; init; }
    public string Category { get; init; } = "";
    public string Subtype { get; init; } = "";
    public double Priority { get; init; } = 50;
    public string? Quest { get; init; }
    public List<DialogueLine> Lines { get; } = new();
    public string Group => $"{Category} / {Subtype}";

    public JsonObject ToJson() => new()
    {
        ["record"] = Label, ["formKey"] = FormKey, ["readFrom"] = Plugin, ["category"] = Category, ["subtype"] = Subtype, ["priority"] = Priority,
        ["lines"] = new JsonArray(Lines.Select(l => (JsonNode)l.ToJson()).ToArray()),
    };
}

public sealed class DialogueAnswer : ReportAnswer
{
    public override string Kind => "dialogue";
    /// <summary>The quest or topic asked for.</summary>
    public required string Target { get; init; }
    public required string TargetFormKey { get; init; }
    public required string TargetSignature { get; init; }
    public string? In { get; init; }
    public string Language { get; init; } = "English";
    /// <summary>The plugins that were looked through for topics and lines.</summary>
    public List<string> Searched { get; } = new();
    public List<DialogueTopic> Topics { get; } = new();
    public int LineCount => Topics.Sum(t => t.Lines.Count(l => !l.Missing));
    public int Missing => Topics.Sum(t => t.Lines.Count(l => l.Missing));

    protected override void Fill(JsonObject json)
    {
        json["target"] = Target; json["formKey"] = TargetFormKey; json["signature"] = TargetSignature; json["language"] = Language;
        if (In != null) json["in"] = In;
        json["searched"] = new JsonArray(Searched.Select(s => (JsonNode)s).ToArray());
        json["counts"] = new JsonObject { ["topics"] = Topics.Count, ["lines"] = LineCount, ["linesNotFound"] = Missing };
        json["topics"] = new JsonArray(Topics.Select(t => (JsonNode)t.ToJson()).ToArray());
    }

    protected override void Render(ReportBody body, ReportTextOptions o)
    {
        body.RowNoun = "lines";
        body.Head.Add($"Dialogue of {Target} {TargetSignature}, {(In != null ? $"as {In} holds it" : "each topic and line from the version that wins in the load order")}, texts in {Language}:");
        body.Head.Add($"{Plural(Topics.Count, "topic")}, {Plural(LineCount, "line")}" + (Missing > 0 ? $", {N(Missing)} named by a topic but not found" : "") + ".");
        foreach (var group in Topics.GroupBy(t => t.Group).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            body.Rows.Add($"{group.Key} ({Plural(group.Count(), "topic")}):");
            foreach (var t in group)
            {
                body.Rows.Add($" {t.Label}{(t.Priority != 50 ? $" (priority {t.Priority:0.##})" : "")}{(t.Lines.Count == 0 ? ": no lines" : ":")}");
                foreach (var l in t.Lines) body.Rows.Add(l.Line());
            }
        }
        if (Topics.Count == 0)
            body.Empty = TargetSignature == "QUST"
                ? $"No dialogue topic belongs to this quest in {(In ?? $"the {N(Searched.Count)} loaded plugins that hold dialogue and know the quest's plugin")}." + (In != null ? " Another plugin may hold them: leave the plugin out, or name another." : "")
                : "The topic wasn't read.";
    }
}

public sealed class DialogueReport
{
    readonly ReportSession _s;

    public DialogueReport(ReportSession session) { _s = session; }

    /// <summary>The DIAL and INFO records of one plugin, by the record they sit in.</summary>
    sealed class Children
    {
        public required Hit Plugin;
        public Dictionary<string, List<int>> ByParent = new(StringComparer.OrdinalIgnoreCase);
    }

    static string Key(Segment seg, int owner, uint id) => $"{id:X6}:{seg.Names[owner]}";

    public DialogueAnswer Dialogue(string questOrTopic, DialogueOptions? options = null)
    {
        options ??= new DialogueOptions();
        string language = options.Language ?? _s.Language;
        return _s.Run(scope =>
        {
            PluginSide? side = options.In == null ? null : _s.Side(options.In);
            var target = _s.Resolve(questOrTopic, side, "quest or dialogue topic");
            var info = target.Info;
            if (info.Signature is not ("QUST" or "DIAL"))
                throw new ReportException($"{info.Label} is a {info.Signature}", "give a quest (QUST) or a dialogue topic (DIAL)");
            var label = side == null ? _s.Engine.Label : _s.Compare.Labeller(side);
            var conditions = new ConditionReader(_s, label);
            string targetKey = ReportSession.Canon(info.FormKey);
            HeaderIndex.TryParseFormKey(targetKey, out var targetOwner, out _);
            var answer = new DialogueAnswer { Target = info.Label, TargetFormKey = targetKey, TargetSignature = info.Signature, In = side?.Shown, Language = language };
            var used = new List<IndexedPlugin> { target.Plugin };

            // ---- the plugins to look through, and their topics and lines by parent
            var plugins = new List<(RecordEngine Engine, IndexedPlugin Plugin)>();
            if (side != null) plugins.Add((side.Engine, side.Plugin));
            else
                foreach (var p in _s.Index.Plugins.Where(p => p.Order.Enabled && p.Segment != null).OrderBy(p => p.Order.Position))
                    if (p.Segment!.OwnerOf(targetOwner) >= 0 && p.Segment.Signatures.Any(x => x.Signature is "DIAL" or "INFO" && x.Count > 0)) plugins.Add((_s.Engine, p));
            // A record no loaded plugin holds: look where it is
            if (plugins.Count == 0) plugins.Add((target.Engine, target.Plugin));
            var children = new List<Children>();
            foreach (var (engine, p) in plugins)
            {
                answer.Searched.Add(p.Name);
                var c = new Children { Plugin = new Hit(engine, new RecordRef(p.Id, 0)) };
                var seg = p.Segment!;
                foreach (string sig in new[] { "DIAL", "INFO" })
                    foreach (int i in seg.EntriesOf(sig))
                    {
                        if (!seg.TryGetParent(seg[i], out int po, out uint pl)) continue;
                        string parent = Key(seg, po, pl);
                        (c.ByParent.TryGetValue(parent, out var l) ? l : c.ByParent[parent] = new()).Add(i);
                    }
                children.Add(c);
            }

            Hit? Version(string formKey)
            {
                if (!HeaderIndex.TryParseFormKey(formKey, out var owner, out var id)) return null;
                if (side != null) return ReportSession.In(side.Engine, side.Name, owner, id);
                return _s.Winner(owner, id);
            }

            RecordView? Read(string formKey, out Hit hit)
            {
                hit = default;
                if (Version(formKey) is not { } h) return null;
                hit = h;
                if (!used.Contains(h.Plugin)) used.Add(h.Plugin);
                return _s.View(scope, h);
            }

            // ---- the topics
            var topicKeys = new List<string>();
            if (info.Signature == "DIAL") topicKeys.Add(targetKey);
            else
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in children)
                    if (c.ByParent.TryGetValue(targetKey, out var list))
                        foreach (int i in list)
                        {
                            var seg = c.Plugin.Plugin.Segment!;
                            if (seg.SignatureOf(seg[i]) != "DIAL") continue;
                            string key = Key(seg, seg[i].Owner, seg[i].LocalId);
                            if (seen.Add(key)) topicKeys.Add(key);
                        }
                topicKeys.Sort(StringComparer.OrdinalIgnoreCase);
            }

            foreach (string topicKey in topicKeys)
            {
                var view = Read(topicKey, out var topicHit);
                if (view == null) continue;
                var json = view.Json;
                var topic = new DialogueTopic
                {
                    FormKey = ReportSession.Canon(topicKey), Label = label(ReportSession.Canon(topicKey)), Plugin = topicHit.Plugin.Name,
                    Category = ReportSession.Str(_s.Value(json, view.Type, "Category")) ?? "", Subtype = ReportSession.Str(_s.Value(json, view.Type, "Subtype")) ?? "",
                    Priority = _s.Value(json, view.Type, "Priority") is JsonValue pv && pv.TryGetValue<double>(out var pr) ? pr : 50,
                    Quest = ReportSession.IsLink(json["Quest"], out var qk) ? ReportSession.Canon(qk) : null,
                };
                answer.Topics.Add(topic);

                // The lines: the topic's own list gives the order; lines the list doesn't name follow in file order
                var order = new List<string>(); var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in children)
                    if (c.ByParent.TryGetValue(topicKey, out var list))
                    {
                        var seg = c.Plugin.Plugin.Segment!;
                        foreach (int i in list) if (seg.SignatureOf(seg[i]) == "INFO") held.Add(Key(seg, seg[i].Owner, seg[i].LocalId));
                    }
                if (json["TopicInfoList"] is JsonArray infoList)
                    foreach (var x in infoList)
                        if (ReportSession.IsLink(x, out var ik) && have.Add(ik)) order.Add(ReportSession.Canon(ik));
                if (json["Responses"] is JsonArray nested)
                    foreach (var x in nested)
                        if (RecordSplit.ChildOf(x) is string ck && have.Add(ck)) order.Add(ReportSession.Canon(ck));
                foreach (string k in held.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) if (have.Add(k)) order.Add(ReportSession.Canon(k));

                foreach (string lineKey in order)
                {
                    // With one plugin asked for, a line that plugin doesn't hold isn't part of the answer's dialogue
                    var lv = (held.Contains(lineKey) || side == null) ? Read(lineKey, out _) : null;
                    if (lv == null || lv.Info.Signature != "INFO")
                    {
                        topic.Lines.Add(new DialogueLine { FormKey = lineKey, Label = label(lineKey), Plugin = "", Missing = true });
                        continue;
                    }
                    topic.Lines.Add(Line(lv, lineKey, Version(lineKey)!.Value.Plugin.Name));
                }
            }

            DialogueLine Line(RecordView lv, string key, string plugin)
            {
                var d = lv.Json;
                var line = new DialogueLine { FormKey = key, Label = label(key), Plugin = plugin, Conditions = conditions.Read(d["Conditions"]) };
                Texts(d, line.Texts);
                if (ReportSession.Text(d["Prompt"], language) is { Length: > 0 } prompt) line.Prompt = prompt.Trim();
                if (ReportSession.IsLink(d["SharedDialog"], out var shared))
                {
                    string sk = ReportSession.Canon(shared);
                    line.SharedFrom = label(sk); line.SharedKey = sk;
                    // The shared line's texts are this line's texts
                    if (line.Texts.Count == 0 && Read(sk, out _) is { } sv) { Texts(sv.Json, line.Texts); line.TextsShared = line.Texts.Count > 0; }
                }
                var who = SpeakerOf(d, line.Conditions);
                if (ReportSession.IsLink(d["DialogGroup"], out var group))
                {
                    line.GroupKey = ReportSession.Canon(group);
                    // A line in a group with no speaker of its own is said by the group's speaker
                    if (who.Key == null && Read(line.GroupKey, out _) is { } gv && SpeakerOf(gv.Json, conditions.Read(gv.Json["Conditions"])) is { Key: not null } fromGroup)
                        who = (fromGroup.Key, fromGroup.Label, "its group");
                }
                if (who.Key != null) { line.SpeakerKey = who.Key; line.Speaker = who.Label; line.SpeakerFrom = who.From; }
                if (ReportSession.IsLink(d["StartScene"], out var scene)) { line.StartsSceneKey = ReportSession.Canon(scene); line.StartsScene = label(line.StartsSceneKey); }
                if (ReportSession.Str(d["StartScenePhase"]) is { Length: > 0 } phase) line.ScenePhase = phase;
                if (d["SetParentQuestStage"] is JsonObject stage)
                {
                    if (stage["OnBegin"] is JsonValue b && b.TryGetValue<int>(out int onBegin) && onBegin != -1) line.StageOnBegin = onBegin;
                    if (stage["OnEnd"] is JsonValue e && e.TryGetValue<int>(out int onEnd) && onEnd != -1) line.StageOnEnd = onEnd;
                    // Left out is the default, 0: the stage the record names
                    if (!stage.ContainsKey("OnBegin")) line.StageOnBegin = 0;
                    if (!stage.ContainsKey("OnEnd")) line.StageOnEnd = 0;
                }
                if (ReportSession.IsLink(d["SpeechChallenge"], out var challenge)) { line.SpeechChallengeKey = ReportSession.Canon(challenge); line.SpeechChallenge = label(line.SpeechChallengeKey); }
                return line;
            }

            // Who says a line: its Speaker field, or the record a GetIsID or GetIsVoiceType condition names
            (string? Key, string? Label, string? From) SpeakerOf(JsonObject d, List<ConditionInfo> conds)
            {
                if (ReportSession.IsLink(d["Speaker"], out var speaker)) { string k = ReportSession.Canon(speaker); return (k, label(k), "Speaker"); }
                foreach (var c in conds)
                    if (c.Function is "GetIsID" or "GetIsVoiceType" && c.Parameters.FirstOrDefault(p => p.FormKey != null) is { } who)
                        return (who.FormKey, who.Text, c.Function);
                return (null, null, null);
            }

            void Texts(JsonObject d, List<string> into)
            {
                if (d["Responses"] is not JsonArray responses) return;
                foreach (var r in responses.OfType<JsonObject>())
                    if (ReportSession.Text(r["ResponseText"], language) is { } t && t.Trim().Length > 0) into.Add(t.Trim());
            }

            foreach (var p in used)
            {
                ReportSession.AddSource(answer, p, side != null && p == side.Plugin && !side.InData ? side.Path : null);
                ReportSession.AddProblemNotes(answer, p);
            }
            if (side == null) answer.Sources.Add(_s.Compare.LoadOrderLine());
            if (answer.Missing > 0)
                answer.Notes.Add($"{Plural(answer.Missing, "line")} named by a topic's list {(side != null ? $"aren't in {side.Name}" : "aren't in any loaded plugin")}." + (side != null ? " Another plugin may hold them." : ""));
            answer.Read.Add($"{Plural(answer.Topics.Count, "topic")} and {Plural(answer.LineCount, "line")} from {Plural(used.Count, "plugin")} ({Plural(plugins.Count, "plugin")} looked through for topics)");
            return answer;
        });
    }
}
