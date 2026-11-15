// sfscompare: a small command line over SFSearch.Compare, for trying the five kinds of comparison against the real
// Data folder and for the numbers in design\agent-first\notes\M7.md. It is not wired into SFSearch's commands.
//
//   sfscompare versions <record> [--in <plugin>]
//       one record across the load order: who holds it, who wins, what each changes against the one before
//   sfscompare records <pluginA> <recordA> <pluginB> [<recordB>] [--expect f1,f2]
//       two records; --expect lists the fields allowed to differ (any other difference: exit code 1)
//   sfscompare plugin_vs_masters <plugin> [<filter>]
//       what a plugin changes in its masters; ITMs and overrides of nothing listed
//   sfscompare plugins <a> <b> [--match editorid] [--as <name>]
//       two versions of one mod, or a patch against the plugin it masters
//   sfscompare patch_vs_reference <a> <b> [--match editorid]
//       two patches for the same masters: same change, only in a, only in b, different
//   sfscompare keys [<text>]
//       how the entries of every list of objects in the schema are paired (by which field, or by position)
//
// <plugin> is a file name in Data or the path of a plugin file (a .bak, a copy outside Data).
// <record> is a FormKey (0003C2:darkstar.esm), a local ID, or an EditorID.
// Options: --detail (every difference with both values)   --json (the structured answer)
//          --max-chars <n> (rows per page, default 24000)  --cursor <n> (continue a cut answer)
//          --data <Data folder> (or SFSEARCH_DATA)   --scratch <folder> (or SFSCOMPARE_SCRATCH; default %TEMP%\sfscompare-scratch)
//          --lang <language>   --out <file> (write the answer there)   --time (what each step took, on stderr)
// Read only: nothing is ever written to the Data folder. The scratch folder holds the header index's segments and
// copies of files that are read under another name.
using System.Diagnostics;
using System.Text.Json;
using Mutagen.Bethesda.Strings;
using SFSearch.Compare;
using SFSearch.Records;

var rest = args.ToList();
string? Opt(string name) { int i = rest.IndexOf(name); if (i < 0 || i + 1 >= rest.Count) return null; string v = rest[i + 1]; rest.RemoveRange(i, 2); return v; }
bool Flag(string name) => rest.Remove(name);

string data = Opt("--data") ?? Environment.GetEnvironmentVariable("SFSEARCH_DATA") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data";
string scratch = Opt("--scratch") ?? Environment.GetEnvironmentVariable("SFSCOMPARE_SCRATCH") ?? Path.Combine(Path.GetTempPath(), "sfscompare-scratch");
string langText = Opt("--lang") ?? "English";
string? outFile = Opt("--out"), expect = Opt("--expect"), matchText = Opt("--match"), readAs = Opt("--as"), inPlugin = Opt("--in");
int maxChars = int.Parse(Opt("--max-chars") ?? "24000"), cursor = int.Parse((Opt("--cursor") ?? "0").Trim('"'));
bool detail = Flag("--detail"), asJson = Flag("--json"), time = Flag("--time");
Console.OutputEncoding = System.Text.Encoding.UTF8;
if (rest.Count < 1)
{
    Console.Error.WriteLine("usage: sfscompare versions|records|plugin_vs_masters|plugins|patch_vs_reference|keys …   (see the top of Program.cs)");
    return 2;
}
if (!Enum.TryParse<Language>(langText, true, out var language))
{
    Console.Error.WriteLine($"Error: \"{langText}\" isn't a language Mutagen knows. Fix: use one of {string.Join(", ", Enum.GetNames<Language>())}.");
    return 1;
}
MatchBy match = MatchBy.FormKey;
if (matchText != null)
{
    if (matchText.Equals("editorid", StringComparison.OrdinalIgnoreCase)) match = MatchBy.EditorId;
    else if (!matchText.Equals("formkey", StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine($"Error: there is no way of pairing named \"{matchText}\". Fix: use --match formkey or --match editorid."); return 2; }
}
string kind = rest[0].ToLowerInvariant().Replace('-', '_');
var clock = Stopwatch.StartNew();
var steps = new List<string>();
void Step(string what) { steps.Add($"{what} {clock.Elapsed.TotalMilliseconds:F0} ms"); clock.Restart(); }

try
{
    var schema = new RecordSchema(Path.Combine(AppContext.BaseDirectory, "schema.db"));
    Step("schema");
    if (kind == "keys")
    {
        var keys = new ListKeys(schema);
        string? filter = rest.Count > 1 ? rest[1] : null;
        var table = keys.Table().Where(t => filter == null || t.Object.Contains(filter, StringComparison.OrdinalIgnoreCase) || t.List.Contains(filter, StringComparison.OrdinalIgnoreCase) || t.ItemObject.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine("| Object | List | Entries are | Paired by |");
        Console.WriteLine("| :--- | :--- | :--- | :--- |");
        foreach (var t in table) Console.WriteLine($"| {t.Object} | {t.List} | {t.ItemObject} | {t.How} |");
        Console.WriteLine($"{table.Count:N0} lists of objects: " + string.Join(", ", table.GroupBy(t => t.How == "position" ? "position" : t.How == "kind" ? "the kind of object" : "a key field").Select(g => $"{g.Count():N0} by {g.Key}")));
        return 0;
    }
    using var session = new CompareSession(data, scratch, schema, language: language);
    Step("header index");
    var compare = new Comparer(session);
    CompareAnswer answer;
    switch (kind)
    {
        case "versions":
            if (rest.Count < 2) return Usage("sfscompare versions <record> [--in <plugin>]");
            answer = compare.Versions(rest[1], inPlugin);
            break;
        case "records":
            if (rest.Count < 4) return Usage("sfscompare records <pluginA> <recordA> <pluginB> [<recordB>] [--expect f1,f2]");
            answer = compare.Records(rest[1], rest[2], rest[3], rest.Count > 4 ? rest[4] : null, expect?.Split(','));
            break;
        case "plugin_vs_masters":
            if (rest.Count < 2) return Usage("sfscompare plugin_vs_masters <plugin> [<filter>]");
            answer = compare.PluginVsMasters(rest[1], rest.Count > 2 ? rest[2] : null);
            break;
        case "plugins":
            if (rest.Count < 3) return Usage("sfscompare plugins <a> <b> [--match editorid] [--as <name>]");
            answer = compare.Plugins(rest[1], rest[2], match, readAs);
            break;
        case "patch_vs_reference":
            if (rest.Count < 3) return Usage("sfscompare patch_vs_reference <a> <b> [--match editorid]");
            answer = compare.PatchVsReference(rest[1], rest[2], match);
            break;
        default:
            Console.Error.WriteLine($"Error: there is no kind of comparison named \"{rest[0]}\". Fix: use versions, records, plugin_vs_masters, plugins or patch_vs_reference.");
            return 2;
    }
    Step(kind);
    string text = asJson
        ? answer.ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
        : answer.ToText(new TextOptions { Detail = detail, MaxChars = maxChars, Cursor = cursor }).TrimEnd('\n');
    Step("text");
    if (outFile != null) { File.WriteAllText(outFile, text + "\n"); Console.WriteLine($"Written to {Path.GetFullPath(outFile)} ({text.Length:N0} characters)."); }
    else Console.WriteLine(text);
    if (time) Console.Error.WriteLine("[cost] " + string.Join("; ", steps) + $"; peak memory {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576:N0} MB");
    return answer.Failed ? 1 : 0;
}
catch (CompareException e)
{
    Console.WriteLine(e.Line);
    return 1;
}
catch (RecordException e)
{
    Console.WriteLine("Error: " + e.Message);
    return 1;
}

static int Usage(string line)
{
    Console.Error.WriteLine("usage: " + line);
    return 2;
}
