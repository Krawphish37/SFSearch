using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SFSearch.Harness;

/// <summary>
/// The rules that turn a recorded old command into the call of the tool that answers it now (mapping\translations.json).
/// Until SFSearch 2.0 they put the new tools under the golden replay and the agreement check against the older
/// commands; with those commands removed, 'sfsharness mapping' uses them to show each recorded call's exact new call.
/// The file's "also" and "explained" parts are what the agreement check found (kept as the record; not read any more).
/// </summary>
public sealed class TranslationFile
{
    public string About { get; set; } = "";
    public List<Rule> Rules { get; set; } = [];
}

public sealed class Rule
{
    /// <summary>The recorded command's name (Names, Inspect, Q, ...), ignoring case.</summary>
    public string Command { get; set; } = "";
    /// <summary>Only commands whose arguments, joined by spaces, match this. Optional.</summary>
    public string? When { get; set; }
    /// <summary>Options of the old command that take a value (--in, --with). Everything else starting with -- is a flag.</summary>
    public List<string> Options { get; set; } = [];
    public string Tool { get; set; } = "";
    /// <summary>
    /// The new tool's arguments. In a text value: {1}, {2} are the old command's plain arguments in order, {--in} an
    /// option's value, {?--csv} true when the flag was given, {args} every argument as a list. A value that names
    /// something the command didn't have is left out.
    /// </summary>
    public JsonObject Params { get; set; } = [];
    /// <summary>service (default) or mcp.</summary>
    public string Via { get; set; } = "service";
    /// <summary>The comparison the agreement check (removed in 2.0 with the older commands) held the two answers to.</summary>
    public string? Agree { get; set; }
}

public static class Translate
{
    internal static bool Fits(Rule rule, Recorded r) =>
        r.Args.Length > 0 && r.Args[0].Equals(rule.Command, StringComparison.OrdinalIgnoreCase) &&
        (rule.When == null || Regex.IsMatch(string.Join(" ", r.Args.Skip(1)), rule.When, RegexOptions.IgnoreCase));

    internal static Call Build(Rule rule, Recorded r)
    {
        var plain = new List<string>(); var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < r.Args.Length; i++)
        {
            string a = r.Args[i];
            if (rule.Options.Contains(a, StringComparer.OrdinalIgnoreCase) && i + 1 < r.Args.Length) options[a] = r.Args[++i];
            else if (a.StartsWith("--", StringComparison.Ordinal)) flags.Add(a);
            else plain.Add(a);
        }
        if (r.Stdin != null) plain.Insert(Math.Min(1, plain.Count), r.Stdin);
        var pars = new JsonObject();
        foreach (var (key, value) in rule.Params)
        {
            if (value is JsonValue v && v.TryGetValue<string>(out var text))
            {
                // "{*}": every plain argument, joined by spaces (Names takes its IDs as one argument or as several)
                if (text == "{*}") { if (plain.Count > 0) pars[key] = string.Join(" ", plain); continue; }
                var whole = Regex.Match(text, @"^\{(\?)?(--[\w-]+|\d+|args)\}$");
                if (whole.Success)
                {
                    string name = whole.Groups[2].Value;
                    if (whole.Groups[1].Success) { pars[key] = flags.Contains(name); continue; }
                    if (name == "args") { pars[key] = new JsonArray(r.Args.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()); continue; }
                    string? found = int.TryParse(name, out int n) ? (n >= 1 && n <= plain.Count ? plain[n - 1] : null) : options.GetValueOrDefault(name);
                    if (found != null) pars[key] = found;
                    continue;
                }
                pars[key] = Regex.Replace(text, @"\{(--[\w-]+|\d+)\}", m =>
                    int.TryParse(m.Groups[1].Value, out int n) ? (n >= 1 && n <= plain.Count ? plain[n - 1] : "") : options.GetValueOrDefault(m.Groups[1].Value, ""));
            }
            else pars[key] = value?.DeepClone();
        }
        return new Call { Via = rule.Via, Tool = rule.Tool, Params = pars };
    }
}
