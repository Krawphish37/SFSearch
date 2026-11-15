// leveled: what can this leveled list give? Items, NPCs, ships and other base forms: every entry with its level,
// count, chance of nothing and conditions, and nested lists unfolded under it to a depth. Each list is read from the
// version that wins in the load order, or from one plugin when asked (for the lists that plugin holds). A list that
// leads back to itself is reported, not followed. The odds themselves depend on the player's level and the list's
// flags, so they are given as the game's settings, not worked out.
using System.Globalization;
using System.Text.Json.Nodes;
using SFSearch.Compare;
using SFSearch.Index;

namespace SFSearch.Reports;

using static ReportAnswer;

public sealed class LeveledOptions
{
    /// <summary>Read the lists this plugin holds from it (a name in Data, or a file's path). Lists it doesn't hold
    /// come from the version that wins in the load order.</summary>
    public string? In { get; init; }
    /// <summary>How many lists deep to unfold (1 to 20).</summary>
    public int Depth { get; init; } = 8;
}

public sealed class LeveledEntry
{
    public int Level { get; init; }
    public int Count { get; init; }
    /// <summary>The entry's own chance of giving nothing, in percent (the file's byte; Mutagen's JSON has it as a fraction).</summary>
    public double ChanceNone { get; init; }
    /// <summary>The record the entry gives (FormKey), and the same as EditorID (FormID:plugin).</summary>
    public required string Target { get; init; }
    public required string TargetLabel { get; init; }
    /// <summary>The target's signature and Mutagen type. Null when no plugin read holds the record.</summary>
    public string? TargetSignature { get; init; }
    public string? TargetType { get; init; }
    public List<ConditionInfo> Conditions { get; init; } = new();
    /// <summary>The target unfolded, when it is a leveled list itself.</summary>
    public LeveledList? Nested { get; set; }
    /// <summary>Why a nested list wasn't unfolded: "loop" (it leads back to a list above it) or "depth".</summary>
    public string? Stopped { get; set; }
    public bool Found => TargetSignature != null;

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["level"] = Level, ["count"] = Count, ["chanceNone"] = ChanceNone, ["record"] = TargetLabel, ["formKey"] = Target };
        if (TargetSignature != null) { o["signature"] = TargetSignature; o["type"] = TargetType; } else o["found"] = false;
        if (Conditions.Count > 0) o["conditions"] = new JsonArray(Conditions.Select(c => (JsonNode)c.ToJson()).ToArray());
        if (Stopped != null) o["notUnfolded"] = Stopped;
        if (Nested != null) o["list"] = Nested.ToJson();
        return o;
    }
}

public sealed class LeveledList
{
    public required string FormKey { get; init; }
    public required string Label { get; init; }
    public required string Signature { get; init; }
    public required string Type { get; init; }
    /// <summary>The plugin this version of the list was read from.</summary>
    public required string Plugin { get; init; }
    public int Depth { get; init; }
    /// <summary>The list's chance of giving nothing, as the game stores it (percent).</summary>
    public double ChanceNone { get; init; }
    /// <summary>A global that gives the chance of nothing instead, when the list names one.</summary>
    public string? ChanceNoneGlobal { get; init; }
    public IReadOnlyList<string> Flags { get; init; } = Array.Empty<string>();
    public int MaxCount { get; init; }
    public List<ConditionInfo> Conditions { get; init; } = new();
    public List<(string Keyword, string Label, double Chance)> FilterKeywords { get; } = new();
    public List<LeveledEntry> Entries { get; } = new();

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["record"] = Label, ["formKey"] = FormKey, ["signature"] = Signature, ["type"] = Type, ["readFrom"] = Plugin, ["chanceNone"] = ChanceNone };
        if (ChanceNoneGlobal != null) o["chanceNoneGlobal"] = ChanceNoneGlobal;
        o["flags"] = new JsonArray(Flags.Select(f => (JsonNode)f).ToArray());
        if (MaxCount != 0) o["maxCount"] = MaxCount;
        if (Conditions.Count > 0) o["conditions"] = new JsonArray(Conditions.Select(c => (JsonNode)c.ToJson()).ToArray());
        if (FilterKeywords.Count > 0) o["filterKeywords"] = new JsonArray(FilterKeywords.Select(k => (JsonNode)new JsonObject { ["keyword"] = k.Label, ["chance"] = k.Chance }).ToArray());
        o["entries"] = new JsonArray(Entries.Select(e => (JsonNode)e.ToJson()).ToArray());
        return o;
    }

    /// <summary>The list's own line: where it was read from and its settings.</summary>
    public string Line()
    {
        var parts = new List<string> { $"chance of nothing {LeveledAnswer.Num(ChanceNone)}%" };
        if (ChanceNoneGlobal != null) parts.Add($"chance of nothing from global {ChanceNoneGlobal}");
        if (Flags.Count > 0) parts.Add("flags " + string.Join(", ", Flags));
        if (MaxCount != 0) parts.Add($"at most {MaxCount}");
        if (Conditions.Count > 0) parts.Add("list conditions: " + ConditionReader.Text(Conditions));
        if (FilterKeywords.Count > 0) parts.Add("filter keywords: " + string.Join("; ", FilterKeywords.Select(k => $"{k.Label} {LeveledAnswer.Num(k.Chance)}")));
        return $"{Label} {Signature}, read from {Plugin}: {string.Join("; ", parts)}";
    }
}

public sealed class LeveledAnswer : ReportAnswer
{
    public override string Kind => "leveled";
    public required LeveledList Top { get; init; }
    public string? In { get; init; }
    public int MaxDepth { get; init; }

    /// <summary>Every list, the top one first, each before the lists nested in it.</summary>
    public IEnumerable<LeveledList> Lists()
    {
        var stack = new Stack<LeveledList>();
        stack.Push(Top);
        while (stack.Count > 0)
        {
            var l = stack.Pop();
            yield return l;
            for (int i = l.Entries.Count - 1; i >= 0; i--) if (l.Entries[i].Nested is { } n) stack.Push(n);
        }
    }

    /// <summary>Every entry of every list, in the order the text prints them, with the list it is in.</summary>
    public IEnumerable<(LeveledList List, LeveledEntry Entry)> Entries()
    {
        return Walk(Top);
        static IEnumerable<(LeveledList, LeveledEntry)> Walk(LeveledList l)
        {
            foreach (var e in l.Entries)
            {
                yield return (l, e);
                if (e.Nested != null) foreach (var x in Walk(e.Nested)) yield return x;
            }
        }
    }

    public int ListCount => Lists().Count();
    public int EntryCount => Entries().Count();
    /// <summary>Entries that give a record that isn't a leveled list.</summary>
    public int Leaves => Entries().Count(x => x.Entry.Found && x.Entry.Nested == null && x.Entry.Stopped == null);
    public int NotFound => Entries().Count(x => !x.Entry.Found);
    public int Loops => Entries().Count(x => x.Entry.Stopped == "loop");
    public int TooDeep => Entries().Count(x => x.Entry.Stopped == "depth");

    internal static string Num(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);

    protected override void Fill(JsonObject json)
    {
        json["list"] = Top.ToJson();
        if (In != null) json["in"] = In;
        json["counts"] = new JsonObject { ["lists"] = ListCount, ["entries"] = EntryCount, ["leaves"] = Leaves, ["notFound"] = NotFound, ["loops"] = Loops, ["deeperThanDepth"] = TooDeep, ["depth"] = MaxDepth };
    }

    protected override void Render(ReportBody body, ReportTextOptions o)
    {
        body.RowNoun = "lines";
        body.Head.Add($"Leveled list {Top.Label} {Top.Signature} {Top.Type}:");
        body.Head.Add($"{Plural(ListCount, "list")}, {Plural(EntryCount, "entry", "entries")}: {N(Leaves)} give an item or form, {N(NotFound)} not found" +
            (Loops > 0 ? $", {N(Loops)} {(Loops == 1 ? "leads back to a list above it" : "lead back to a list above them")} (not followed)" : "") + (TooDeep > 0 ? $", {N(TooDeep)} deeper than depth {MaxDepth} (not unfolded)" : "") + ".");
        body.Head.Add((In != null ? $"Lists that {In} holds are read from it; the others from" : "Each list is read from") + " the version that wins in the load order. Chances are the game's settings: what a roll gives also depends on the player's level and the flags (UseAll gives every entry; CalculateFromAllLevelsLessThanOrEqualPlayer picks among all entries at or below the player's level).");
        body.Head.Add("Each entry: level, count, its own chance of nothing, the record it gives, conditions.");
        Rows(Top, 0);
        if (Top.Entries.Count == 0) body.Rows.Add($"The list has no entries in {Top.Plugin}.");

        void Rows(LeveledList l, int depth)
        {
            string pad = new string(' ', depth * 4);
            body.Rows.Add($"{pad}{l.Line()}");
            foreach (var e in l.Entries)
            {
                string what = e.Found ? $"{e.TargetLabel} {e.TargetSignature}" : $"{e.TargetLabel} (not found)";
                string cond = e.Conditions.Count > 0 ? $"; if {ConditionReader.Text(e.Conditions)}" : "";
                string stop = e.Stopped == "loop" ? "   [leads back to a list above it: not followed]" : e.Stopped == "depth" ? $"   [a list deeper than depth {MaxDepth}: not unfolded]" : "";
                body.Rows.Add($"{pad}  - level {e.Level}, count {e.Count}, {Num(e.ChanceNone)}% nothing: {what}{cond}{stop}");
                if (e.Nested != null) Rows(e.Nested, depth + 1);
            }
        }
    }
}

public sealed class LeveledReport
{
    readonly ReportSession _s;
    readonly HashSet<string> _leveledSignatures = new(StringComparer.Ordinal);

    public LeveledReport(ReportSession session)
    {
        _s = session;
        foreach (var t in session.Schema.Model.RecordTypes)
            if (EntryObject(t.Name) != null) _leveledSignatures.Add(t.Sig!);
    }

    /// <summary>The signatures of the record types that are leveled lists: they have entries that each name a record.</summary>
    public IReadOnlyCollection<string> LeveledSignatures => _leveledSignatures;

    /// <summary>The object an entry of this record type is, when the type is a leveled list.</summary>
    string? EntryObject(string type)
    {
        var entries = _s.Schema.Prop(type, "Entries");
        string? obj = entries?.Item?.RefObject;
        return obj != null && _s.Schema.Prop(obj, "Reference") != null && _s.Schema.Prop(obj, "Level") != null ? obj : null;
    }

    public LeveledAnswer Leveled(string list, LeveledOptions? options = null)
    {
        options ??= new LeveledOptions();
        int maxDepth = Math.Clamp(options.Depth, 1, 20);
        return _s.Run(scope =>
        {
            PluginSide? side = options.In == null ? null : _s.Side(options.In);
            var top = _s.Resolve(list, side, "leveled list");
            if (!_leveledSignatures.Contains(top.Signature))
                throw new ReportException($"{top.Info.Label} is a {top.Signature}, not a leveled list", $"give a record of one of these types: {string.Join(", ", _leveledSignatures.OrderBy(x => x, StringComparer.Ordinal))}");
            var label = side == null ? _s.Engine.Label : _s.Compare.Labeller(side);
            var conditions = new ConditionReader(_s, label);
            var onPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usedPlugins = new List<IndexedPlugin>();

            Hit? Lookup(string formKey)
            {
                if (!HeaderIndex.TryParseFormKey(formKey, out var owner, out var id)) return null;
                if (side != null && ReportSession.In(side.Engine, side.Name, owner, id) is { } own) return own;
                return _s.Winner(owner, id);
            }

            LeveledList Unfold(Hit hit, int depth)
            {
                var view = _s.View(scope, hit);
                if (!usedPlugins.Contains(hit.Plugin)) usedPlugins.Add(hit.Plugin);
                var json = view.Json; string type = view.Type;
                string key = ReportSession.Canon(view.Info.FormKey);
                string? global = null;
                foreach (var name in new[] { "UseGlobal", "UseChanceNoneGlobal" })
                    if (ReportSession.IsLink(json[name], out var gk)) global = label(ReportSession.Canon(gk));
                var l = new LeveledList
                {
                    FormKey = key, Label = label(key), Signature = view.Info.Signature, Type = type, Plugin = hit.Plugin.Name, Depth = depth,
                    ChanceNone = Number(_s.Value(json, type, "ChanceNone")), ChanceNoneGlobal = global,
                    Flags = (_s.Value(json, type, "Flags") as JsonArray)?.Select(f => ReportSession.Str(f) ?? "").Where(f => f.Length > 0).ToList() ?? new List<string>(),
                    MaxCount = (int)Number(_s.Value(json, type, "MaxCount")),
                    Conditions = conditions.Read(json["Conditions"]),
                };
                if (json["FilterKeywordChances"] is JsonArray chances)
                    foreach (var c in chances.OfType<JsonObject>())
                    {
                        string kw = ReportSession.IsLink(c["Keyword"], out var kk) ? ReportSession.Canon(kk) : "";
                        l.FilterKeywords.Add((kw, kw.Length == 0 ? "(no keyword)" : label(kw), Number(c["Chance"])));
                    }
                string entryObject = EntryObject(type)!;
                // An entry's chance of nothing is one byte of percent in the file; Mutagen reads it as a fraction (15 becomes 0.15)
                double entryPercent = _s.Schema.Prop(entryObject, "ChanceNone")?.Main.Kind == "Percent" ? 100 : 1;
                onPath.Add(key);
                if (json["Entries"] is JsonArray entries)
                    foreach (var x in entries.OfType<JsonObject>())
                    {
                        string target = ReportSession.IsLink(x["Reference"], out var rk) ? ReportSession.Canon(rk) : "";
                        var child = target.Length == 0 ? null : Lookup(target);
                        string? sig = child?.Signature;
                        var e = new LeveledEntry
                        {
                            Level = (int)Number(_s.Value(x, entryObject, "Level")), Count = (int)Number(_s.Value(x, entryObject, "Count")),
                            ChanceNone = Number(_s.Value(x, entryObject, "ChanceNone")) * entryPercent,
                            Target = target, TargetLabel = target.Length == 0 ? "(no record named)" : label(target),
                            TargetSignature = sig, TargetType = sig == null ? null : string.Join("/", _s.Schema.TypesOf(sig)),
                            Conditions = conditions.Read(x["Conditions"]),
                        };
                        l.Entries.Add(e);
                        if (child is not { } ch || !_leveledSignatures.Contains(sig!)) continue;
                        if (onPath.Contains(target)) e.Stopped = "loop";
                        else if (depth + 1 >= maxDepth) e.Stopped = "depth";
                        else e.Nested = Unfold(ch, depth + 1);
                    }
                onPath.Remove(key);
                return l;
            }

            var answer = new LeveledAnswer { Top = Unfold(top, 0), In = side?.Shown, MaxDepth = maxDepth };
            foreach (var p in usedPlugins)
            {
                ReportSession.AddSource(answer, p, side != null && p == side.Plugin && !side.InData ? side.Path : null);
                ReportSession.AddProblemNotes(answer, p);
            }
            answer.Sources.Add(_s.Compare.LoadOrderLine());
            if (answer.Loops > 0) answer.Notes.Add($"{Plural(answer.Loops, "entry", "entries")} {(answer.Loops == 1 ? "leads back to a list above it" : "lead back to a list above them")}. The game can't finish such a roll; the loop is reported, not followed.");
            if (answer.NotFound > 0) answer.Notes.Add($"{Plural(answer.NotFound, "entry", "entries")} name a record no plugin in Data holds.");
            answer.Read.Add($"{Plural(answer.ListCount, "list")} from {Plural(usedPlugins.Count, "plugin")}");
            return answer;
        });
    }

    static double Number(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
}
