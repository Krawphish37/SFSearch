// sfsrecord: a small command line over SFSearch.Records, for trying things against the real Data folder and for the
// numbers in design\agent-first\notes\M3.md. It is not wired into SFSearch's commands (that is milestone M5).
//
//   sfsrecord card     <plugin> <record> [--lang <language>]            the card: set fields only, readable names, links labelled
//   sfsrecord json     <plugin> <record> [--assembled]                  Mutagen.Bethesda.Serialization's full-profile JSON
//   sfsrecord fields   <plugin> <record> <field> [<field> ...]          named fields; a field the JSON leaves out gives its default
//   sfsrecord raw      <plugin> <record> [--full]                       the record header and every subrecord's bytes
//   sfsrecord evidence <plugin> <record> <field>                        raw bytes, the field's slice, the Mutagen field, the xEdit path
//   sfsrecord verify   <plugin> <record> [<record> ...]                 JSON back to a record, written by Mutagen, compared with the file
//   sfsrecord list     <plugin> <SIG> [count]                           records of one type, to find one to look at
//
// <record> is a FormKey (0003C2:darkstar.esm), a local ID (0003C2), the FormID as the file writes it, or an EditorID.
// TES4 is the plugin's header (card, json, fields, raw, evidence): sfsrecord evidence Starfield.esm TES4 Stats.NumRecords
// Options: --json (card, fields, raw, evidence, verify: the same parts as a structured object)
//          --data <Data folder> (or SFSEARCH_DATA)   --scratch <folder> (or SFSRECORD_SCRATCH; default %TEMP%\sfsrecord-scratch)
//          --time (print what each step took)   --max-items <n> (card: items printed per list, default 40)   --full (no cuts)
//          --heavy (read a record of over 16 MB too: it takes gigabytes of memory)
//          --direct (read a top-level record straight from its own bytes too, not through its group)
// Read only: nothing is ever written to the Data folder. The scratch folder holds the header index's segments and
// the small test plugins the verify writes.
using System.Diagnostics;
using System.Text.Json;
using Mutagen.Bethesda.Strings;
using SFSearch.Index;
using SFSearch.Records;

var rest = args.ToList();
string? Opt(string name) { int i = rest.IndexOf(name); if (i < 0 || i + 1 >= rest.Count) return null; string v = rest[i + 1]; rest.RemoveRange(i, 2); return v; }
bool Flag(string name) => rest.Remove(name);

string data = Opt("--data") ?? Environment.GetEnvironmentVariable("SFSEARCH_DATA") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Starfield\Data";
string scratch = Opt("--scratch") ?? Environment.GetEnvironmentVariable("SFSRECORD_SCRATCH") ?? Path.Combine(Path.GetTempPath(), "sfsrecord-scratch");
string langText = Opt("--lang") ?? "English";
int maxItems = int.Parse(Opt("--max-items") ?? "40");
bool time = Flag("--time"), asJson = Flag("--json"), full = Flag("--full"), assembled = Flag("--assembled"), heavy = Flag("--heavy"), direct = Flag("--direct");
if (rest.Count < 3)
{
    Console.Error.WriteLine("usage: sfsrecord card|json|fields|raw|evidence|verify|list <plugin> <record> …   (see the top of Program.cs)");
    return 2;
}
if (!Enum.TryParse<Language>(langText, true, out var language))
{
    Console.Error.WriteLine($"Error: \"{langText}\" isn't a language Mutagen knows. Fix: use one of {string.Join(", ", Enum.GetNames<Language>())}.");
    return 1;
}
string command = rest[0].ToLowerInvariant(), plugin = rest[1];
Console.OutputEncoding = System.Text.Encoding.UTF8;
int exit = 0;
var clock = Stopwatch.StartNew();
var steps = new List<string>();
void Step(string what) { steps.Add($"{what} {clock.Elapsed.TotalMilliseconds:F0} ms"); clock.Restart(); }
var pretty = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

try
{
    var schema = new RecordSchema(Path.Combine(AppContext.BaseDirectory, "schema.db"));
    Step("schema");
    using var index = new HeaderIndex(PluginSource.Folder(data), new IndexOptions { ScratchFolder = Path.Combine(scratch, "index"), VersionText = $"sfsrecord / mutagen {schema.MutagenVersion} / schema {schema.Version}", Refusals = new MutagenRefusals() });
    index.Refresh();
    Step("header index");
    using var engine = new RecordEngine(index, schema, Path.Combine(scratch, "verify"), data, language);
    engine.AllowHeavy = heavy;
    engine.PreferDirect = direct;
    var use = engine.Sessions.Use(plugin);
    Step("open " + plugin);

    // The plugin header (GAPS S88): its record name is its signature, TES4
    if (rest.Count > 2 && rest[2].Equals("TES4", StringComparison.OrdinalIgnoreCase) && command is "card" or "json" or "fields" or "raw" or "evidence")
    {
        var h = engine.LoadHeader(plugin);
        Step("read the header");
        switch (command)
        {
            case "card": { var card = engine.HeaderCard(h); Console.WriteLine(asJson ? card.ToJson().ToJsonString(pretty) : card.ToText(full ? int.MaxValue : maxItems, full ? int.MaxValue : 64).TrimEnd('\n')); break; }
            case "json": Console.WriteLine(engine.HeaderJson(h).ToJsonString(pretty)); break;
            case "raw": Console.WriteLine(engine.HeaderRawText(h, full ? int.MaxValue : 64).TrimEnd('\n')); break;
            case "fields":
                Console.WriteLine(h.Label);
                foreach (var v in engine.HeaderFields(h, rest.Skip(3))) Console.WriteLine(asJson ? v.ToJson().ToJsonString(pretty) : "  " + v.ToText().Replace("\n", "\n  "));
                break;
            case "evidence":
                foreach (var field in rest.Skip(3)) { var ev = engine.HeaderEvidence(h, field); Console.WriteLine(asJson ? ev.ToJson().ToJsonString(pretty) : ev.ToText().TrimEnd('\n')); }
                break;
        }
        Step(command);
        engine.Sessions.Confirm(use);
        foreach (var n in use.Notes) Console.WriteLine("[notes] " + n);
        if (time) Console.Error.WriteLine("[cost] " + string.Join("; ", steps) + $"; peak memory {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576:N0} MB");
        return exit;
    }

    switch (command)
    {
        case "list":
        {
            int count = rest.Count > 3 ? int.Parse(rest[3]) : 20;
            foreach (var r in index.RecordsOfType(plugin, rest[2].ToUpperInvariant()).Take(count))
            {
                var d = index.Describe(r);
                Console.WriteLine($"{d.Label}\t{d.Signature}\t0x{d.Offset:X}\t{d.Size:N0} bytes{(d.Parent != null ? "\tin " + d.Parent : "")}");
            }
            break;
        }
        case "json":
        {
            var rec = engine.Load(plugin, rest[2]);
            Step($"read {rec.Type} ({rec.Route})");
            var json = assembled ? engine.Assembled(rec) : engine.Json(rec);
            Step("to JSON");
            Console.WriteLine(json.ToJsonString(pretty));
            break;
        }
        case "card":
        {
            var rec = engine.Load(plugin, rest[2]);
            Step($"read {rec.Type} ({rec.Route})");
            var card = engine.Card(rec);
            Step("card");
            Console.WriteLine(asJson ? card.ToJson().ToJsonString(pretty) : card.ToText(full ? int.MaxValue : maxItems, full ? int.MaxValue : 64).TrimEnd('\n'));
            break;
        }
        case "fields":
        {
            if (rest.Count < 4) { Console.Error.WriteLine("Error: no field was named. Fix: sfsrecord fields <plugin> <record> <field> [<field> ...]"); return 2; }
            var rec = engine.Load(plugin, rest[2]);
            Step($"read {rec.Type} ({rec.Route})");
            var values = engine.Fields(rec, rest.Skip(3));
            Step("fields");
            if (asJson) Console.WriteLine(new System.Text.Json.Nodes.JsonArray(values.Select(v => (System.Text.Json.Nodes.JsonNode)v.ToJson()).ToArray()).ToJsonString(pretty));
            else { Console.WriteLine(rec.Label); foreach (var v in values) Console.WriteLine("  " + v.ToText().Replace("\n", "\n  ")); }
            break;
        }
        case "raw":
        {
            var r = engine.Find(plugin, rest[2]);
            Console.WriteLine(asJson ? engine.RawJson(r).ToJsonString(pretty) : engine.RawText(r, full ? int.MaxValue : 64).TrimEnd('\n'));
            Step("raw");
            break;
        }
        case "evidence":
        {
            if (rest.Count < 4) { Console.Error.WriteLine("Error: no field was named. Fix: sfsrecord evidence <plugin> <record> <field>"); return 2; }
            var rec = engine.Load(plugin, rest[2]);
            Step($"read {rec.Type} ({rec.Route})");
            foreach (var field in rest.Skip(3))
            {
                var ev = engine.Evidence(rec, field);
                Console.WriteLine(asJson ? ev.ToJson().ToJsonString(pretty) : ev.ToText().TrimEnd('\n'));
            }
            Step("evidence");
            break;
        }
        case "verify":
        {
            var refs = rest.Skip(2).Select(t => engine.Find(plugin, t)).ToList();
            var results = engine.Verify(refs);
            Step($"verify {refs.Count}");
            if (asJson) Console.WriteLine(new System.Text.Json.Nodes.JsonArray(results.Select(v => (System.Text.Json.Nodes.JsonNode)v.ToJson()).ToArray()).ToJsonString(pretty));
            else foreach (var v in results) Console.WriteLine(v.ToText());
            if (results.Any(v => !v.Equal)) exit = 3;
            break;
        }
        default:
            Console.Error.WriteLine($"Error: there is no command \"{command}\". Fix: use card, json, fields, raw, evidence, verify or list.");
            return 2;
    }
    engine.Sessions.Confirm(use);
    foreach (var n in use.Notes) Console.WriteLine("[notes] " + n);
    if (time) Console.Error.WriteLine("[cost] " + string.Join("; ", steps) + $"; peak memory {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576:N0} MB");
    return exit;
}
catch (RecordException e)
{
    Console.Error.WriteLine("Error: " + e.Message);
    return 1;
}
