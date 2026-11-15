// Links that sit in script properties (a record's VirtualMachineAdapter): which script and which property hold one,
// and what a leveled-list injector script adds the linked record to. sources uses it to tell a quest that sells an
// item (its script adds the item's list to a vendor's list) from one that only names it; refs uses it to say the same
// in plain words beside a script-property row. Both read the record's one JSON form (RecordEngine.Json).
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SFSearch.Reports;

/// <summary>One link in a script property.</summary>
/// <param name="Path">The link's path in the record's JSON, list indexes included.</param>
/// <param name="Script">The script's name ("" when the record gives none, e.g. an empty quest fragment script).</param>
/// <param name="Property">The property's name.</param>
/// <param name="Member">For a struct property: the struct member that holds the link.</param>
/// <param name="Alias">For a script on a quest alias: the alias's ID, when the record gives it.</param>
/// <param name="Targets">For a leveled-list injector script: the leveled lists it adds the linked record to (FormKeys).</param>
public sealed record ScriptLink(string Path, string Script, string Property, string? Member, int? Alias, IReadOnlyList<string> Targets);

public static partial class ScriptLinks
{
    [GeneratedRegex(@"^(VirtualMachineAdapter\.(?:Scripts\[\d+\]|Script|Aliases\[(\d+)\]\.Scripts\[\d+\]))\.Properties\[(\d+)\]")] private static partial Regex ScriptProperty();
    [GeneratedRegex(@"^(.*\.Structs\[\d+\])\.Members\[(\d+)\]")] private static partial Regex StructMember();
    [GeneratedRegex("AddItemToLeveledList|LLInjector|Inject", RegexOptions.IgnoreCase)] private static partial Regex InjectorName();
    [GeneratedRegex(@"^(\w+)(?:\[(\d+)\])?$")] private static partial Regex PathPart();
    [GeneratedRegex(@"\[\d+\]")] private static partial Regex ListIndex();

    /// <summary>True when a link's path (with or without list indexes) is inside a script property.</summary>
    public static bool IsScriptProperty(string path) =>
        path.StartsWith("VirtualMachineAdapter", StringComparison.Ordinal) && path.Contains(".Properties[", StringComparison.Ordinal);

    /// <summary>A path with its list indexes written [*], as refs and the link index write fields.</summary>
    public static string Unindexed(string path) => ListIndex().Replace(path, "[*]");

    /// <summary>
    /// Every link to <paramref name="targetFormKey"/> in the record's script properties: the script, the property,
    /// the struct member, and for an injector script the leveled lists it adds the target to.
    /// </summary>
    /// <param name="signatureOf">A FormKey's record type (its signature), or null when it isn't known.</param>
    public static List<ScriptLink> Into(JsonObject record, string targetFormKey, Func<string, string?> signatureOf)
    {
        var found = new List<ScriptLink>();
        string target = ReportSession.Canon(targetFormKey);
        void Walk(JsonNode? n, string path)
        {
            switch (n)
            {
                case JsonValue when ReportSession.IsLink(n, out var key):
                    if (ReportSession.Canon(key).Equals(target, StringComparison.OrdinalIgnoreCase) && Describe(record, path, signatureOf) is { } link) found.Add(link);
                    break;
                case JsonObject o: foreach (var (name, x) in o) Walk(x, path + "." + name); break;
                case JsonArray a: for (int i = 0; i < a.Count; i++) Walk(a[i], $"{path}[{i}]"); break;
            }
        }
        Walk(record["VirtualMachineAdapter"], "VirtualMachineAdapter");
        return found;
    }

    /// <summary>The script and property that hold the link at <paramref name="path"/>; null when it isn't in a script property.</summary>
    public static ScriptLink? Describe(JsonObject record, string path, Func<string, string?> signatureOf)
    {
        var m = ScriptProperty().Match(path);
        if (!m.Success) return null;
        var script = Navigate(record, m.Groups[1].Value);
        var property = script?["Properties"] is JsonArray props && int.Parse(m.Groups[3].Value) is var pi && pi < props.Count ? props[pi] : null;
        string? member = null;
        if (StructMember().Match(path) is { Success: true } sm && Navigate(record, sm.Groups[1].Value)?["Members"] is JsonArray members && int.Parse(sm.Groups[2].Value) is var mi && mi < members.Count)
            member = ReportSession.Str(members[mi]?["Name"]);
        int? alias = null;
        if (m.Groups[2].Success && Navigate(record, $"VirtualMachineAdapter.Aliases[{m.Groups[2].Value}].Property")?["Alias"] is JsonValue av && av.TryGetValue<int>(out int id)) alias = id;
        var (name, targets) = AddToListTargets(record, path, signatureOf);
        return new ScriptLink(path, name, ReportSession.Str(property?["Name"]) ?? "", member, alias, targets);
    }

    /// <summary>
    /// Leveled-list injector scripts. Two shapes: a struct that pairs the item's list with a target list (the other
    /// members of the same struct), or a script whose name says it adds to a leveled list (its other properties).
    /// Gives the script's name and the leveled lists it adds to.
    /// </summary>
    public static (string Script, List<string> Targets) AddToListTargets(JsonObject record, string path, Func<string, string?> signatureOf)
    {
        var result = new List<string>();
        var m = ScriptProperty().Match(path);
        if (!m.Success) return ("", result);
        var script = Navigate(record, m.Groups[1].Value);
        string name = ReportSession.Str(script?["Name"]) ?? "";

        void AddLists(JsonNode? n)
        {
            switch (n)
            {
                case JsonValue when ReportSession.IsLink(n, out var key):
                    string k = ReportSession.Canon(key);
                    if (signatureOf(k) == "LVLI" && !result.Contains(k, StringComparer.OrdinalIgnoreCase)) result.Add(k);
                    break;
                case JsonObject o: foreach (var (_, x) in o) AddLists(x); break;
                case JsonArray a: foreach (var x in a) AddLists(x); break;
            }
        }

        // A struct that pairs the two: the other members of the same struct
        var sm = StructMember().Match(path);
        if (sm.Success && Navigate(record, sm.Groups[1].Value)?["Members"] is JsonArray members && members.Count > 1)
        {
            int self = int.Parse(sm.Groups[2].Value);
            for (int i = 0; i < members.Count; i++) if (i != self) AddLists(members[i]);
            if (result.Count > 0) return (name, result);
        }
        // The whole script: only for scripts that say what they do, so a big quest script's other properties don't count
        if (!InjectorName().IsMatch(name) || script?["Properties"] is not JsonArray props) return (name, result);
        int own = int.Parse(m.Groups[3].Value);
        for (int i = 0; i < props.Count; i++) if (i != own) AddLists(props[i]);
        return (name, result);
    }

    static JsonNode? Navigate(JsonNode? node, string dottedPath)
    {
        foreach (var part in dottedPath.Split('.'))
        {
            var pm = PathPart().Match(part);
            if (!pm.Success) return null;
            node = node?[pm.Groups[1].Value];
            if (pm.Groups[2].Success) node = node is JsonArray a && int.Parse(pm.Groups[2].Value) < a.Count ? a[int.Parse(pm.Groups[2].Value)] : null;
        }
        return node;
    }
}
