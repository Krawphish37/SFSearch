// Conditions as the reports show them: the function, its parameters with links labelled, what it runs on, the
// comparison, and whether it is joined to the next with OR. Read from a record's JSON beside the schema, so a value
// the JSON leaves out is its default (an operator left out is "equal to", a value left out is 0).
using System.Globalization;
using System.Text.Json.Nodes;
using SFSearch.Records;

namespace SFSearch.Reports;

public sealed class ConditionParameter
{
    public required string Name { get; init; }
    /// <summary>The value as text: a number, or a link as EditorID (FormID:plugin).</summary>
    public required string Text { get; init; }
    /// <summary>The record a parameter names, when it is a link.</summary>
    public string? FormKey { get; init; }
}

public sealed class ConditionInfo
{
    /// <summary>The condition function: GetStageDone, GetIsID, GetLevel.</summary>
    public required string Function { get; init; }
    public List<ConditionParameter> Parameters { get; } = new();
    /// <summary>What the function runs on: Subject, Target, Reference, …</summary>
    public string RunOn { get; init; } = "Subject";
    /// <summary>The reference it runs on, when RunOn is Reference (a link).</summary>
    public string? Reference { get; init; }
    public string? ReferenceLabel { get; init; }
    /// <summary>EqualTo, GreaterThanOrEqualTo, …</summary>
    public string Operator { get; init; } = "EqualTo";
    /// <summary>The value compared with: a number, or a global.</summary>
    public double? Value { get; init; }
    public string? Global { get; init; }
    public string? GlobalLabel { get; init; }
    /// <summary>True when the condition is joined to the next one with OR (AND otherwise).</summary>
    public bool Or { get; init; }
    public IReadOnlyList<string> Flags { get; init; } = Array.Empty<string>();

    public string OperatorSign => Operator switch
    {
        "EqualTo" => "=", "NotEqualTo" => "≠", "GreaterThan" => ">", "GreaterThanOrEqualTo" => "≥", "LessThan" => "<", "LessThanOrEqualTo" => "≤", var o => o,
    };

    public string ValueText => Global != null ? $"global {GlobalLabel ?? Global}" : (Value ?? 0).ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>GetStageDone(MQ101 (…), 300) = 1</summary>
    public string Text
    {
        get
        {
            string args = Parameters.Count == 0 ? "" : "(" + string.Join(", ", Parameters.Select(p => p.Text)) + ")";
            string on = RunOn == "Subject" ? "" : Reference != null ? $" on {RunOn} {ReferenceLabel ?? Reference}" : $" on {RunOn}";
            return $"{Function}{args}{on} {OperatorSign} {ValueText}";
        }
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["function"] = Function };
        if (Parameters.Count > 0)
            o["parameters"] = new JsonArray(Parameters.Select(p => (JsonNode)(p.FormKey == null ? new JsonObject { ["name"] = p.Name, ["value"] = p.Text } : new JsonObject { ["name"] = p.Name, ["value"] = p.Text, ["formKey"] = p.FormKey })).ToArray());
        if (RunOn != "Subject") o["runOn"] = RunOn;
        if (Reference != null) o["reference"] = ReferenceLabel ?? Reference;
        o["operator"] = Operator;
        if (Global != null) o["global"] = GlobalLabel ?? Global; else o["value"] = Value ?? 0;
        if (Or) o["or"] = true;
        o["text"] = Text;
        return o;
    }
}

public sealed class ConditionReader
{
    readonly ReportSession _s;
    readonly Func<string, string> _label;

    public ConditionReader(ReportSession session, Func<string, string> label) { _s = session; _label = label; }

    static readonly HashSet<string> NotParameters = new(StringComparer.Ordinal)
    {
        RecordSplit.TypeKey, "RunOnType", "Reference", "ParameterThree", "LinkedReferenceKeyword", "UseAliases", "UsePackageData", "RunOnTypeIndex",
    };

    /// <summary>The conditions of a list as the JSON has it. Null or empty gives none.</summary>
    public List<ConditionInfo> Read(JsonNode? list)
    {
        var result = new List<ConditionInfo>();
        if (list is not JsonArray a) return result;
        foreach (var x in a)
            if (x is JsonObject c) result.Add(One(c));
        return result;
    }

    ConditionInfo One(JsonObject c)
    {
        string kind = ReportSession.Str(c[RecordSplit.TypeKey]) ?? "ConditionFloat";
        var data = c["Data"] as JsonObject ?? new JsonObject();
        string dataType = ReportSession.Str(data[RecordSplit.TypeKey]) ?? "ConditionData";
        string function = dataType.EndsWith("ConditionData", StringComparison.Ordinal) && dataType.Length > 13 ? dataType[..^13] : dataType;
        var flags = (_s.Value(c, kind, "Flags") as JsonArray)?.Select(f => ReportSession.Str(f) ?? "").Where(f => f.Length > 0).ToList() ?? new List<string>();
        string runOn = ReportSession.Str(_s.Value(data, dataType, "RunOnType")) ?? "Subject";
        string? reference = ReportSession.IsLink(data["Reference"], out var rk) ? ReportSession.Canon(rk) : null;
        var value = c["ComparisonValue"];
        string? global = ReportSession.IsLink(value, out var gk) ? ReportSession.Canon(gk) : null;
        double? number = global == null && value is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
        var info = new ConditionInfo
        {
            Function = function, RunOn = runOn, Reference = reference, ReferenceLabel = reference == null ? null : _label(reference),
            Operator = ReportSession.Str(_s.Value(c, kind, "CompareOperator")) ?? "EqualTo",
            Value = global == null ? number ?? 0 : null, Global = global, GlobalLabel = global == null ? null : _label(global),
            Or = flags.Contains("OR"), Flags = flags,
        };
        foreach (var (name, x) in data)
        {
            if (x == null || NotParameters.Contains(name) || name.Contains("Unused", StringComparison.Ordinal)) continue;
            if (ReportSession.IsLink(x, out var fk)) { string key = ReportSession.Canon(fk); info.Parameters.Add(new ConditionParameter { Name = name, Text = _label(key), FormKey = key }); continue; }
            if (x is not JsonValue pv) continue;
            string text = ReportSession.Str(pv) ?? "";
            // A parameter left at nothing says nothing
            if (text.Length == 0 || text == "0" || text == "Null" || text == "-1") continue;
            info.Parameters.Add(new ConditionParameter { Name = name, Text = text });
        }
        return info;
    }

    /// <summary>Conditions as one line: each with AND or OR to the next.</summary>
    public static string Text(IReadOnlyList<ConditionInfo> conditions) =>
        string.Join(" ", conditions.Select((c, i) => c.Text + (i < conditions.Count - 1 ? (c.Or ? " OR" : " AND") : "")));
}
