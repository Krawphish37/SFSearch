using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SFSearch.Harness;

/// <summary>
/// The recorded old commands as a mapping check (SFSearch 2.0 removed the older commands; design\agent-first\notes\
/// remove-old-commands.md). The 455 recorded commands (mapping\commands.jsonl) and the 18 recorded SQL files
/// (mapping\sqlfiles.jsonl) can't be replayed any more: each old name now answers with one line that points at its
/// verb. This check holds every recorded command to the call that answers it now:
///   1. its command is an older command with a verb named here, or an add-on's command (those still run);
///   2. SFSearch's own pointer for each old name names that verb (one run of the program per name, no plugin read);
///   3. where mapping\translations.json has a rule for the command, the rule's tool is that verb's tool, and the rule
///      builds the exact new call (old call → new call).
/// It reads no plugin and takes seconds. The table of every recorded command and its new call is written to the work
/// folder (mapping.md).
/// </summary>
public static class Mapping
{
    public static string Folder => Path.Combine(Paths.Cases, "mapping");

    /// <summary>Each older command and the verb its pointer must name ("" = nothing replaces it). The same as SFSearch's Program.Retired.</summary>
    public static readonly Dictionary<string, string> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Query"] = "query2", ["Q"] = "saved", ["Fields"] = "describe", ["Inspect"] = "record", ["Dump"] = "record", ["Read"] = "record",
        ["List"] = "find", ["Diff"] = "compare", ["Check"] = "check2", ["Header"] = "loadorder2", ["Stats"] = "loadorder2", ["Delta"] = "compare",
        ["Names"] = "find", ["Refs"] = "refs2", ["Conflicts"] = "compare", ["LoadOrder"] = "loadorder2", ["Leveled"] = "leveled2",
        ["Dialogue"] = "dialogue2", ["Sources"] = "sources2", ["Scripts"] = "scripts2", ["Changes"] = "changes2", ["Archive"] = "archive2",
        ["Materials"] = "materials2", ["Assets"] = "assets2", ["PluginDiff"] = "compare", ["Live"] = "record", ["VerifyDb"] = "record",
        ["LossCheck"] = "check2", ["Databases"] = "status", ["Session"] = "Service",
        ["Add"] = "", ["BuildCache"] = "", ["LiveScanCheck"] = "",
    };

    /// <summary>The tool each verb is (a verb with no tool of its own goes through sfs_run).</summary>
    static string ToolOf(string verb) => verb switch
    {
        "find" => "sfs_find", "record" => "sfs_record", "describe" => "sfs_describe", "query2" => "sfs_query", "saved" => "sfs_saved",
        "refs2" => "sfs_refs", "compare" => "sfs_compare", "loadorder2" => "sfs_loadorder", "check2" => "sfs_check", "status" => "sfs_status",
        _ => "sfs_run",
    };

    /// <summary>The Patcher add-on's commands among the recorded ones: they were never SFSearch's own and still run.</summary>
    static readonly HashSet<string> AddOn = new(StringComparer.OrdinalIgnoreCase) { "Lint", "MkDiff", "OmodCensus", "RecipeCensus", "Profile", "DarkStarProfile", "TierDiff", "BlueprintScanner", "ExhaustiveDiff", "PatchScan" };

    static List<Recorded> Load(string file) =>
        File.ReadLines(Path.Combine(Folder, file)).Where(l => l.Trim().Length > 0).Select(l => JsonSerializer.Deserialize<Recorded>(l, Json.Read)!).ToList();

    public static async Task<(int Problems, string Report)> Run(Sut sut)
    {
        var recorded = Load("commands.jsonl").Select(r => (Set: "recorded", R: r)).Concat(Load("sqlfiles.jsonl").Select(r => (Set: "sqlfiles", R: r))).ToList();
        var rules = Json.Load<TranslationFile>(Path.Combine(Folder, "translations.json")).Rules;
        var problems = new List<string>();

        // 2. The pointer of every old name, once each, straight from the program (no service, no plugin read)
        var pointers = new Dictionary<string, (int Code, string Line)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (old, verb) in Verbs)
        {
            var (o, e, code) = await Sut.RunProcess(Paths.Exe, [old], sut.Env(), timeoutMs: 60000);
            string line = (o + e).Replace("\r", "").Split('\n').FirstOrDefault(l => l.StartsWith("Error: ", StringComparison.Ordinal)) ?? (o + e).Trim();
            pointers[old] = (code, line);
            string head = $"Error: {old} was one of SFSearch's older commands, removed in 2.0. Fix: ";
            bool right = code == 2 && line.StartsWith(head, StringComparison.Ordinal)
                && (verb.Length == 0 ? line[head.Length..].StartsWith("nothing replaces it", StringComparison.Ordinal) : line[head.Length..].StartsWith("use " + verb + " ", StringComparison.Ordinal) || line[head.Length..].StartsWith("use " + verb + ".", StringComparison.Ordinal));
            if (!right) problems.Add($"{old}: the pointer should name {(verb.Length == 0 ? "nothing (no counterpart)" : verb)}; it gave exit {code}: {line}");
        }

        // 1 and 3. Every recorded command: its new call
        var table = new StringBuilder("| # | Set | Old call | Answered now by |\n|---|---|---|---|\n");
        int byRule = 0, byVerb = 0, addOn = 0;
        foreach (var (set, r) in recorded)
        {
            string old = r.Args.Length > 0 ? r.Args[0] : "";
            string shown = string.Join(" ", r.Args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)) + (r.Stdin != null ? " (SQL piped in)" : "");
            string now;
            if (AddOn.Contains(old)) { addOn++; now = $"the add-on's own {old} (unchanged; SFSearch.Patcher)"; }
            else if (!Verbs.TryGetValue(old, out var verb)) { problems.Add($"#{r.Id}: '{old}' is neither an older command named here nor an add-on's command"); now = "?"; }
            else
            {
                var rule = rules.FirstOrDefault(x => Translate.Fits(x, r));
                if (rule != null)
                {
                    byRule++;
                    var call = Translate.Build(rule, r);
                    if (verb.Length > 0 && call.Tool != ToolOf(verb)) problems.Add($"#{r.Id}: the rule sends {old} to {call.Tool}, but its pointer names {verb} ({ToolOf(verb)})");
                    now = $"{call.Tool} {call.Params?.ToJsonString() ?? "{}"}";
                }
                else { byVerb++; now = verb.Length == 0 ? "nothing (no counterpart)" : $"{verb} ({ToolOf(verb)}), by its pointer"; }
            }
            table.Append($"| {r.Id} | {set} | {Compare.Cut(shown, 160).Replace("|", "\\|")} | {Compare.Cut(now, 200).Replace("|", "\\|")} |\n");
        }
        string file = Paths.Out("mapping.md");
        File.WriteAllText(file, table.ToString(), Json.Utf8);

        var sb = new StringBuilder();
        foreach (var (old, (code, line)) in pointers.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) sb.Append($"  {old,-14} exit {code}: {Compare.Cut(line, 170)}\n");
        foreach (var p in problems) sb.Append("PROBLEM  ").Append(p).Append('\n');
        var byOld = recorded.GroupBy(x => x.R.Args.Length > 0 ? x.R.Args[0].ToLowerInvariant() : "").OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}");
        sb.Append($"{(problems.Count == 0 ? "PASS" : "FAIL")}  mapping: {recorded.Count} recorded calls ({string.Join(", ", byOld)}): {byRule} by a rule to their exact new call, {byVerb} by their command's pointer, {addOn} the add-on's own commands; "
            + $"{Verbs.Count} old names each point at their verb ({Verbs.Count(v => v.Value.Length == 0)} with no counterpart); {problems.Count} problem(s). Every call and its new call: {file}\n");
        return (problems.Count, sb.ToString());
    }
}
