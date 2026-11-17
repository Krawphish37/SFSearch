// scripts: a plugin's Papyrus scripts as source, which of its records attach which script, and the code that reaches
// records without a link in a record.
//  - The compiled scripts (.pex) come from the plugin's own archives, from loose files under Data\Scripts whose path
//    holds the plugin's name, from loose files its records attach (looked for by name in their script blocks), and from
//    loose files its scripts use (their parent, a script their code calls, a type they name; again from each one
//    taken). A loose copy stands in for an archived one, as in the game. A loose script a master holds (its archive,
//    or a master's name in the path) is the master's script, not the plugin's.
//  - Each script is attached by some records, or by none ("attached to nothing"); a script the records attach that no
//    archive and no loose file holds is "missing".
//  - The code: each compiled script is read (Pex.cs, ScriptCode.cs) for Game.GetFormFromFile, Game.GetForm,
//    IsPluginInstalled, and AddItem, AddForm, PlaceAtMe … on a record the code got that way or on a script property;
//    the values held in script properties are read from the records that attach the script. No decompiler.
//  - They are decompiled with Champollion once per copy of the compiled scripts, into a small kept folder outside
//    the Data folder and outside the repo. The folder is named by a hash of the compiled scripts' content: the same
//    scripts are never decompiled twice, and a changed script gets a new folder. Nothing is ever judged by a date.
//    It holds script source only, no record data.
//  - Which record carries which script is read live, from the records' own script lists.
//  - find looks for a text in every line; show prints one script.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SFSearch.Compare;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Reports;

using static ReportAnswer;

public sealed class ScriptsOptions
{
    /// <summary>A text to look for in every line of the scripts (letter case doesn't count).</summary>
    public string? Find { get; init; }
    /// <summary>A script to print: its name, or the part of its name after the last colon.</summary>
    public string? Show { get; init; }
    /// <summary>Read the plugins' records for the scripts they carry. Only the list does; find and show don't.</summary>
    public bool Carriers { get; init; } = true;
    /// <summary>Read placed references and other world data for scripts too: a placed object can carry a script.
    /// Not set: a plugin of up to <see cref="ScriptsReport.WorldLimit"/> records is read whole, a larger one without
    /// its world data, and the answer says so.</summary>
    public bool? World { get; init; }
    /// <summary>For a plugin file outside Data: read the archives beside it, not the ones in Data named for it.</summary>
    public bool Beside { get; init; }
    /// <summary>Only the code that reaches records without a link (every call listed): nothing is decompiled, and the
    /// records aren't read for the scripts they attach.</summary>
    public bool Calls { get; init; }
}

/// <summary>One record that carries a script.</summary>
/// <param name="By">The plugin whose version of the record is read, when it isn't the plugin the record was found in
/// (a later plugin's override, the one the game uses).</param>
public sealed record ScriptCarrier(string Record, string FormKey, string Signature, string Script, string Where, string? By = null);

public sealed class ScriptInfo
{
    public required string Plugin { get; init; }
    /// <summary>The script's name as its source gives it: DarkStar:CodexMachineCore.</summary>
    public required string Name { get; init; }
    /// <summary>The source file inside the kept folder.</summary>
    public required string File { get; init; }
    public int Lines { get; init; }
    /// <summary>The compiled script it was made from, and where that was: an archive's name, or "loose".</summary>
    public string Pex { get; init; } = "";
    public string From { get; init; } = "";
    /// <summary>The plugin's records that carry it.</summary>
    public List<ScriptCarrier> Carriers { get; } = new();
    /// <summary>The script it extends, as its compiled script gives it ("" when it extends nothing, or it couldn't be read).</summary>
    public string Parent { get; set; } = "";
    /// <summary>The plugin's own scripts that extend it.</summary>
    public List<string> ExtendedBy { get; } = new();
    /// <summary>The plugin's own scripts whose code calls it, and those that name it as a type (besides its children).</summary>
    public List<string> CalledFrom { get; } = new();
    public List<string> TypeIn { get; } = new();
    /// <summary>Why a loose script without the plugin's name in its path is the plugin's: "attached by its records",
    /// "called from X", "used by X as its parent", "named as a type in X". Null for the others.</summary>
    public string? TakenBecause { get; set; }
    internal string FullPath = "";

    /// <summary>"attached by 3 records", "attached to nothing", or "attached to nothing; extended by X; called from Y".</summary>
    public string AttachedText => Carriers.Count > 0 ? $"attached by {ReportAnswer.Plural(Carriers.Select(c => c.FormKey).Distinct().Count(), "record")}"
        : "attached to nothing" + (ExtendedBy.Count > 0 ? $"; extended by {string.Join(", ", ExtendedBy)}" : "")
            + (CalledFrom.Count > 0 ? $"; called from {string.Join(", ", CalledFrom)}" : "") + (TypeIn.Count > 0 ? $"; a type in {string.Join(", ", TypeIn)}" : "");

    public JsonObject ToJson(bool carriersRead = false)
    {
        var o = new JsonObject { ["plugin"] = Plugin, ["script"] = Name, ["lines"] = Lines, ["file"] = File, ["compiled"] = Pex, ["from"] = From, ["extends"] = Parent };
        if (carriersRead) o["attached"] = AttachedText;
        o["extendedBy"] = new JsonArray(ExtendedBy.Select(x => (JsonNode)x).ToArray());
        o["calledFrom"] = new JsonArray(CalledFrom.Select(x => (JsonNode)x).ToArray());
        o["typeIn"] = new JsonArray(TypeIn.Select(x => (JsonNode)x).ToArray());
        if (TakenBecause != null) o["takenBecause"] = TakenBecause;
        o["onRecords"] = new JsonArray(Carriers.Select(c => (JsonNode)new JsonObject { ["record"] = c.Record, ["formKey"] = c.FormKey, ["signature"] = c.Signature, ["where"] = c.Where }).ToArray());
        return o;
    }
}

/// <summary>A script the plugin's records attach that isn't one of its own: where the game finds it, or that it is missing.</summary>
/// <param name="FoundIn">"loose", an archive's name, or null when it is missing.</param>
/// <param name="Note">For one found only in an archive the game doesn't load: that archive.</param>
public sealed record ScriptElsewhere(string Plugin, string Script, string? FoundIn, string? Note);

/// <summary>A script the plugin's scripts use (their parent, a script their code calls, a type they name) that no loose
/// file and no archive the game loads holds (S9c). The game can't link it: a script whose parent is missing doesn't
/// load, and code that calls or names a missing script fails (harmless only when it never runs: optional support for
/// another mod).</summary>
/// <param name="UsedBy">Each of the plugin's scripts that uses it, with how: "parent", "calls" or "type".</param>
/// <param name="Note">For one found only in an archive the game doesn't load: that archive.</param>
public sealed record ScriptUsedMissing(string Plugin, string Script, IReadOnlyList<(string How, string By)> UsedBy, string? Note)
{
    /// <summary>"the parent of A; called from B, C; named as a type in D".</summary>
    public string UsedText => string.Join("; ", UsedBy.GroupBy(u => u.How).OrderBy(g => g.Key == "parent" ? 0 : g.Key == "calls" ? 1 : 2)
        .Select(g => (g.Key == "parent" ? "the parent of " : g.Key == "calls" ? "called from " : "named as a type in ") + string.Join(", ", g.Select(u => u.By).Distinct(StringComparer.OrdinalIgnoreCase))));
}

public sealed record ScriptLine(string Plugin, string Script, int Line, string Text);

public sealed class ScriptsAnswer : ReportAnswer
{
    public override string Kind => "scripts";
    public required IReadOnlyList<string> Plugins { get; init; }
    /// <summary>How the plugins were named, when a set was among them: "@official (33 plugins)" (GAPS S117). Null: by their names.</summary>
    public string? Named { get; init; }
    /// <summary>list, find or show.</summary>
    public required string Mode { get; init; }
    public List<ScriptInfo> Scripts { get; } = new();
    public string? Find { get; init; }
    public List<ScriptLine> Lines { get; } = new();
    public long LinesLookedAt { get; set; }
    /// <summary>show: the scripts printed, each with its source.</summary>
    public List<(ScriptInfo Script, string Source)> Shown { get; } = new();
    /// <summary>True when the records were read for the scripts they carry.</summary>
    public bool CarriersRead { get; set; }
    /// <summary>Scripts the plugins' records carry whose source isn't among the plugins' own scripts (the game's, a master's).</summary>
    public List<ScriptCarrier> CarriedFromElsewhere { get; } = new();
    public int RecordsScanned { get; set; }
    public int RecordsWithScripts { get; set; }
    public int WorldLeftOut { get; set; }
    /// <summary>How many compiled scripts were decompiled by this call (0 when the kept sources were used).</summary>
    public int Decompiled { get; set; }
    /// <summary>Where each script of <see cref="CarriedFromElsewhere"/> is found, or that it is missing.</summary>
    public List<ScriptElsewhere> Elsewhere { get; } = new();
    /// <summary>The scripts the records attach that no archive and no loose file holds (or only an archive the game doesn't load).</summary>
    public IEnumerable<ScriptElsewhere> Missing => Elsewhere.Where(e => e.FoundIn == null);
    /// <summary>The scripts the plugins' scripts use (parent, calls, types) that no archive and no loose file the game
    /// loads holds. Looked for in the list only.</summary>
    public List<ScriptUsedMissing> MissingUsed { get; } = new();
    /// <summary>How many different scripts the plugins' scripts use that aren't among their own, looked for (the list only).</summary>
    public int UsedLookedFor { get; set; }
    /// <summary>True when the compiled scripts were read for the code that reaches records without a link.</summary>
    public bool CodeRead { get; set; }
    /// <summary>The calls that reach records (or plugins) without a link in a record.</summary>
    public List<ScriptCall> Calls { get; } = new();
    /// <summary>AddItem, AddForm … whose record is a script property (a link the record holds; listed, with the
    /// value each record that attaches the script gives it), or nothing (counted, not listed).</summary>
    public int LinkedGives => PropertyGives + NothingGives;
    public int PropertyGives { get; set; }
    public int NothingGives { get; set; }
    public int CompiledRead { get; set; }
    /// <summary>Compiled scripts that couldn't be read, with why.</summary>
    public List<(string Plugin, string Pex, string Why)> CompiledUnread { get; } = new();

    static JsonObject CallJson(ScriptCall c)
    {
        var o = new JsonObject
        {
            ["plugin"] = c.Plugin, ["script"] = c.Script, ["function"] = c.Function, ["call"] = c.Call, ["kind"] = c.Kind,
            ["args"] = new JsonArray(c.Args.Select(a => (JsonNode)a).ToArray()), ["names"] = ScriptsAnswer.Names(c),
        };
        if (c.NamedPlugin != null) o["namedPlugin"] = c.NamedPlugin;
        if (c.FormId is { } id) o["formId"] = id.ToString("X8", CultureInfo.InvariantCulture);
        if (c.EditorId != null) o["editorId"] = c.EditorId;
        if (c.FormKey != null) o["formKey"] = c.FormKey;
        if (c.Record != null) { o["record"] = c.Record; o["signature"] = c.Signature; }
        if (c.NotFound != null) o["notFound"] = c.NotFound;
        if (c.ItemOrigin is { } io) { o["itemFrom"] = io.ToString(); o["item"] = c.ItemText; }
        if (c.BuiltBy != null) o["builtBy"] = CallJson(c.BuiltBy);
        if (c.Via != null) o["via"] = c.Via;
        if (c.Targets.Count > 0)
            o["targets"] = new JsonArray(c.Targets.Select(t =>
            {
                var j = new JsonObject();
                if (t.Carrier != null) { j["carrier"] = t.Carrier; j["carrierFormKey"] = t.CarrierFormKey; }
                j["via"] = t.Via;
                if (t.FormId is { } fid) j["formId"] = fid.ToString("X8", CultureInfo.InvariantCulture);
                if (t.NamedPlugin != null) j["namedPlugin"] = t.NamedPlugin;
                if (t.ItemFormKey != null) j["itemFormKey"] = t.ItemFormKey;
                if (t.Alias >= 0) j["alias"] = t.Alias;
                if (t.FormKey != null) j["formKey"] = t.FormKey;
                if (t.Record != null) { j["record"] = t.Record; j["signature"] = t.Signature; }
                if (t.NotFound != null) j["notFound"] = t.NotFound;
                return (JsonNode)j;
            }).ToArray());
        return o;
    }

    /// <summary>What a call names, in words: the record, why there is none, or where a handed-out record comes from;
    /// with several values, each path's or each attaching record's (all of them with <paramref name="detail"/>, else the first 3).</summary>
    public static string Names(ScriptCall c, bool detail = false)
    {
        string text = c.Targets.Count > 0 ? TargetsText(c, detail) : One(c, detail);
        return c.Via is { Length: > 0 } via ? $"{text} ({via})" : text;
    }

    static string One(ScriptCall c, bool detail)
    {
        if (c.Kind == "give")
        {
            if (c.BuiltBy is { } b) return $"a record the code got with {b.Shown} in {b.Function}" + (b.Record != null ? $": {b.Record} {b.Signature}" : b.NotFound != null ? $" ({b.NotFound})" : b.Targets.Count > 0 ? $": {TargetsText(b, detail)}" : "");
            return c.ItemOrigin switch
            {
                // What a method of a parameter returns ("what parameter akSource.GetReference() returns"): the Via says whose
                ValueOrigin.Parameter => (c.ItemText is { } it && it.StartsWith("what ", StringComparison.Ordinal) ? it : $"{c.ItemText} of {c.Function}") + (c.Via == null ? " (decided at run time)" : ""),
                ValueOrigin.ScriptVariable => $"{c.ItemText} (no function of this script sets it to a record it got in code)",
                ValueOrigin.Property => c.ItemText ?? "",
                _ => c.ItemText ?? "",
            };
        }
        if (c.Kind == "plugin") return c.NamedPlugin != null ? $"plugin {c.NamedPlugin}" + (c.NotFound != null ? $" ({c.NotFound})" : "") : "a plugin named by a value the code works out at run time";
        if (c.Record != null) return $"{c.Record} {c.Signature}";
        if (c.NotFound != null) return (c.FormKey ?? c.EditorId ?? "") + (c.FormKey != null || c.EditorId != null ? ": " : "") + c.NotFound;
        if (c.EditorId != null) return $"EditorID {c.EditorId}";
        return "a record named by values the code works out";
    }

    /// <summary>"on Foo (000801:x.esm) QUST: Item = Bar (001234:x.esm) MISC; on 3 records (…): …", or one value per path.</summary>
    static string TargetsText(ScriptCall c, bool detail)
    {
        bool give = c.Kind == "give";
        // Each record's values in one text, then the records that give the same text together
        var perCarrier = c.Targets.GroupBy(t => t.CarrierFormKey ?? "").Select(g =>
        {
            var ts = g.ToList();
            string text = give
                ? string.Join("; ", ts.GroupBy(t => t.Via).Select(v => (v.Key.Length > 0 ? v.Key + " = " : "") + string.Join(", ", v.Select(t => t.Result).Distinct())))
                : string.Join("; ", ts.Select(t => (t.Via.Length > 0 ? t.Via + " → " : "") + t.Result).Distinct());
            return (Carrier: ts[0].Carrier, Text: text);
        }).ToList();
        var groups = perCarrier.GroupBy(x => x.Text).Select(g => (Text: g.Key, Carriers: g.Where(x => x.Carrier != null).Select(x => x.Carrier!).ToList())).ToList();
        int show = detail ? groups.Count : Math.Min(groups.Count, 3);
        var parts = groups.Take(show).Select(g => g.Carriers.Count == 0 ? g.Text
            : g.Carriers.Count == 1 ? $"on {g.Carriers[0]}: {g.Text}"
            : $"on {N(g.Carriers.Count)} records ({string.Join("; ", g.Carriers.Take(detail ? g.Carriers.Count : 2))}{(!detail && g.Carriers.Count > 2 ? "; …" : "")}): {g.Text}").ToList();
        if (groups.Count > show) parts.Add($"… {N(groups.Count - show)} more values (detail lists every one)");
        // A record got from a property (an element of it, what a method of it returns): say so before the property's values
        string head = give && c.ItemText != null && c.Targets.Any(t => t.Carrier != null) && !c.ItemText.StartsWith("property ", StringComparison.Ordinal) ? $"{c.ItemText}: " : "";
        if (!give && c.Targets.All(t => t.Carrier == null)) head = "one of: ";
        return head + string.Join("; ", parts);
    }

    protected override void Fill(JsonObject json)
    {
        json["plugins"] = new JsonArray(Plugins.Select(p => (JsonNode)p).ToArray());
        if (Named != null) json["named"] = Named;
        json["mode"] = Mode;
        json["counts"] = new JsonObject
        {
            ["scripts"] = Scripts.Count, ["lines"] = Scripts.Sum(s => (long)s.Lines), ["decompiledNow"] = Decompiled,
            ["scriptsOnRecords"] = Scripts.Count(s => s.Carriers.Count > 0), ["recordsWithScripts"] = RecordsWithScripts, ["scriptsFromElsewhere"] = CarriedFromElsewhere.Select(c => c.Script).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            ["attachedToNothing"] = CarriersRead ? Scripts.Count(s => s.Carriers.Count == 0) : null, ["missing"] = Missing.Count(),
            ["usedLookedFor"] = UsedLookedFor, ["missingUsed"] = MissingUsed.Count,
            ["calls"] = Calls.Count, ["scriptsWithCalls"] = Calls.Select(c => c.Plugin + "|" + c.Script).Distinct(StringComparer.OrdinalIgnoreCase).Count(), ["linkedGives"] = LinkedGives,
            ["propertyGives"] = PropertyGives, ["nothingGives"] = NothingGives, ["compiledRead"] = CompiledRead,
        };
        if (Mode == "list")
        {
            json["scripts"] = new JsonArray(Scripts.Select(s => (JsonNode)s.ToJson(CarriersRead)).ToArray());
            json["carriersRead"] = CarriersRead;
            json["fromElsewhere"] = new JsonArray(CarriedFromElsewhere.Select(c => (JsonNode)new JsonObject { ["record"] = c.Record, ["formKey"] = c.FormKey, ["signature"] = c.Signature, ["script"] = c.Script, ["where"] = c.Where }).ToArray());
            json["elsewhere"] = new JsonArray(Elsewhere.Select(e => (JsonNode)new JsonObject { ["plugin"] = e.Plugin, ["script"] = e.Script, ["foundIn"] = e.FoundIn, ["note"] = e.Note }).ToArray());
            json["missing"] = new JsonArray(Missing.Select(e => (JsonNode)e.Script).ToArray());
            json["missingUsed"] = new JsonArray(MissingUsed.Select(m => (JsonNode)new JsonObject
            {
                ["plugin"] = m.Plugin, ["script"] = m.Script, ["usedAs"] = m.UsedText, ["note"] = m.Note,
                ["usedBy"] = new JsonArray(m.UsedBy.Select(u => (JsonNode)new JsonObject { ["script"] = u.By, ["how"] = u.How }).ToArray()),
            }).ToArray());
        }
        if (Mode is "list" or "calls")
        {
            json["codeRead"] = CodeRead;
            json["calls"] = new JsonArray(Calls.Select(c => (JsonNode)CallJson(c)).ToArray());
            json["compiledUnread"] = new JsonArray(CompiledUnread.Select(u => (JsonNode)new JsonObject { ["plugin"] = u.Plugin, ["compiled"] = u.Pex, ["why"] = u.Why }).ToArray());
        }
        if (Mode == "find")
        {
            json["find"] = Find; json["linesLookedAt"] = LinesLookedAt;
            json["lines"] = new JsonArray(Lines.Select(l => (JsonNode)new JsonObject { ["plugin"] = l.Plugin, ["script"] = l.Script, ["line"] = l.Line, ["text"] = l.Text }).ToArray());
        }
        if (Mode == "show")
            json["shown"] = new JsonArray(Shown.Select(s => { var o = s.Script.ToJson(); o["source"] = s.Source; return (JsonNode)o; }).ToArray());
    }

    protected override void Render(ReportBody body, ReportTextOptions o)
    {
        string who = Named ?? string.Join(", ", Plugins);
        bool multi = Plugins.Count > 1;
        switch (Mode)
        {
            case "find":
                body.RowNoun = "lines";
                body.Head.Add($"Lines of the scripts of {who} that contain \"{Find}\": {N(Lines.Count)} in {Plural(Lines.Select(l => l.Plugin + "|" + l.Script).Distinct().Count(), "script")} ({Plural(Scripts.Count, "script")}, {N(LinesLookedAt)} lines looked at).");
                if (Lines.Count > 0)
                {
                    body.Head.Add(multi ? "| Plugin | Script | Line | Text |" : "| Script | Line | Text |");
                    body.Head.Add(multi ? "| :--- | :--- | ---: | :--- |" : "| :--- | ---: | :--- |");
                }
                foreach (var l in Lines) body.Rows.Add($"{(multi ? $"| {l.Plugin} " : "")}| {Cell(l.Script)} | {l.Line} | {Cell(l.Text.Trim())} |");
                break;
            case "show":
                body.RowNoun = "lines";
                foreach (var (s, source) in Shown)
                {
                    body.Rows.Add($"{s.Name} ({s.Plugin}, {Plural(s.Lines, "line")}, from {s.Pex} in {s.From}):");
                    foreach (var line in source.Replace("\r\n", "\n").TrimEnd('\n').Split('\n')) body.Rows.Add(line);
                }
                break;
            default:
                body.RowNoun = "scripts";
                body.Head.Add($"Scripts of {who}: {Plural(Scripts.Count, "script")}, {N(Scripts.Sum(s => (long)s.Lines))} lines" +
                    (Scripts.Count > 0 ? $", from {string.Join(", ", Scripts.GroupBy(s => s.From, StringComparer.OrdinalIgnoreCase).Select(g => $"{g.Key} ({N(g.Count())})"))}." : "."));
                if (CarriersRead && Scripts.Count == 0)
                    body.Head.Add($"On records: {Plural(RecordsWithScripts, "record")} with scripts among {N(RecordsScanned)} looked at.");
                else if (CarriersRead)
                {
                    int none = Scripts.Count(s => s.Carriers.Count == 0), parents = Scripts.Count(s => s.Carriers.Count == 0 && s.ExtendedBy.Count > 0);
                    body.Head.Add($"On records: {N(Scripts.Count(s => s.Carriers.Count > 0))} of the {N(Scripts.Count)} scripts are attached by {Plural(Scripts.SelectMany(s => s.Carriers).Select(c => c.FormKey).Distinct().Count(), "record")} of {(multi ? "these plugins" : "the plugin")}; " +
                        (none == 0 ? "none is attached to nothing. " : $"{N(none)} {(none == 1 ? "is" : "are")} attached to nothing{(parents > 0 ? $" ({N(parents)} of them extended by another of its scripts)" : "")}: used by other scripts, attached by another plugin, or unused. ") +
                        $"{Plural(RecordsWithScripts, "record")} with scripts among {N(RecordsScanned)} looked at.");
                }
                int missing = Missing.Count();
                if (missing > 0)
                    body.Head.Add($"Missing: {Plural(missing, "script")} the records attach {(missing == 1 ? "is" : "are")} in no archive and no loose file the game loads: " +
                        string.Join("; ", Missing.Select(m => $"{m.Script} (on {Plural(CarriedFromElsewhere.Where(c => c.Script.Equals(m.Script, StringComparison.OrdinalIgnoreCase)).Select(c => c.FormKey).Distinct().Count(), "record")}{(m.Note != null ? $"; {m.Note}" : "")})")) + ".");
                if (MissingUsed.Count > 0)
                    body.Head.Add($"Missing: {Plural(MissingUsed.Count, "script")} {(multi ? "these plugins'" : "the plugin's")} scripts use {(MissingUsed.Count == 1 ? "is" : "are")} in no archive and no loose file the game loads, so the game can't link {(MissingUsed.Count == 1 ? "it" : "them")} (a script whose parent is missing doesn't load; code that calls or names a missing script fails, harmless only when that code never runs, as in optional support for another mod): " +
                        string.Join("; ", MissingUsed.Select(m => $"{(multi ? m.Plugin + ": " : "")}{m.Script}, {m.UsedText}{(m.Note != null ? $" ({m.Note})" : "")}")) + ".");
                if (CodeRead) body.Head.Add(CallsLine(multi));
                if (Scripts.Count > 0)
                {
                    body.Head.Add((multi ? "| Plugin " : "") + "| Script | Lines | From |" + (CarriersRead ? " Attached by |" : ""));
                    body.Head.Add((multi ? "| :--- " : "") + "| :--- | ---: | :--- |" + (CarriersRead ? " :--- |" : ""));
                }
                foreach (var s in Scripts)
                {
                    string on = "";
                    if (CarriersRead)
                    {
                        var records = s.Carriers.GroupBy(c => c.FormKey).Select(g => $"{g.First().Record} {g.First().Signature}").ToList();
                        int show = o.Detail ? records.Count : Math.Min(records.Count, 3);
                        on = records.Count == 0 ? $" {Cell(s.AttachedText)} |" : $" {N(records.Count)}: {Cell(string.Join("; ", records.Take(show)))}{(records.Count > show ? $"; … {N(records.Count - show)} more" : "")} |";
                    }
                    body.Rows.Add($"{(multi ? $"| {s.Plugin} " : "")}| {Cell(s.Name)} | {N(s.Lines)} | {Cell(s.From)} |{on}");
                }
                if (MissingUsed.Count > 0)
                {
                    body.Rows.Add($"Scripts {(multi ? "these plugins'" : "its")} scripts use that no loose file and no archive the game loads holds ({N(MissingUsed.Count)} of the {N(UsedLookedFor)} used scripts that aren't {(multi ? "theirs" : "its own")}):");
                    foreach (var m in MissingUsed) body.Rows.Add($"  MISSING: {(multi ? m.Plugin + ": " : "")}{m.Script}, {m.UsedText}{(m.Note != null ? $" ({m.Note})" : "")}");
                }
                if (CarriersRead && CarriedFromElsewhere.Count > 0)
                {
                    var where = Elsewhere.GroupBy(e => e.Script, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                    var names = CarriedFromElsewhere.GroupBy(c => c.Script, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
                    body.Rows.Add($"Scripts on the records that aren't among these ({Plural(names.Count, "script")} of the game, of a master or of another mod, on {Plural(CarriedFromElsewhere.Select(c => c.FormKey).Distinct().Count(), "record")}):");
                    int show = o.Detail ? names.Count : Math.Min(names.Count, 15);
                    // The missing ones first: they are the ones to act on
                    foreach (var g in names.OrderBy(g => where.TryGetValue(g.Key, out var e) && e.FoundIn != null ? 1 : 0).Take(show))
                    {
                        var records = g.GroupBy(c => c.FormKey).Select(x => $"{x.First().Record} {x.First().Signature}").ToList();
                        int n = o.Detail ? records.Count : Math.Min(records.Count, 2);
                        string at = where.TryGetValue(g.Key, out var w) ? w.FoundIn != null ? $" (in {w.FoundIn})" : $" (MISSING{(w.Note != null ? $": {w.Note}" : "")})" : "";
                        body.Rows.Add($"  {g.Key}{at}: {N(records.Count)}: {string.Join("; ", records.Take(n))}{(records.Count > n ? $"; … {N(records.Count - n)} more" : "")}");
                    }
                    if (names.Count > show) body.Rows.Add($"  … {N(names.Count - show)} more scripts (detail lists every one)");
                }
                if (CodeRead && Calls.Count > 0) CallRows(body, o, multi, o.Detail ? int.MaxValue : 30, true);
                break;
            case "calls":
                body.RowNoun = "calls";
                body.Head.Add(CallsLine(multi));
                CallRows(body, o, multi, int.MaxValue, false);
                if (Calls.Count == 0)
                    body.Empty = CompiledRead == 0 ? $"{who}: no compiled scripts {(CompiledUnread.Count > 0 ? "could be read" : "to read")}." : $"No call of {who}'s {Plural(CompiledRead, "compiled script")} reaches a record without a link in a record, or hands one out.";
                break;
        }
    }

    string CallsLine(bool multi)
    {
        int scripts = Calls.Select(c => c.Plugin + "|" + c.Script).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var kinds = Calls.GroupBy(c => c.Kind == "give" ? (c.BuiltBy != null ? c.Call + " on a record got in code" : c.ItemOrigin == ValueOrigin.Property ? c.Call + " on a script property" : c.Call + " on a record decided at run time") : c.Call)
            .OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {N(g.Count())}");
        int onProperty = Calls.Count(c => c.Kind == "give" && c.ItemOrigin == ValueOrigin.Property);
        return $"Code that reaches records without a link in a record, and the records it hands out: {Plural(Calls.Count, "call")} in {Plural(scripts, "script")}{(Calls.Count > 0 ? $" ({string.Join(", ", kinds)})" : "")}, among the {Plural(CompiledRead, "compiled script")} read." +
            (onProperty > 0 ? $" {N(onProperty)} of them hand out a record named in a script property (a link the record holds), each with the value the plugin's records that attach the script give it." : "") +
            (NothingGives > 0 ? $" {Plural(NothingGives, "more AddItem, AddForm, PlaceAtMe … call", "more AddItem, AddForm, PlaceAtMe … calls")} {(NothingGives == 1 ? "hands" : "hand")} out nothing (None)." : "") +
            (CompiledUnread.Count > 0 ? $" {Plural(CompiledUnread.Count, "compiled script")} couldn't be read." : "");
    }

    void CallRows(ReportBody body, ReportTextOptions o, bool multi, int max, bool heading)
    {
        if (Calls.Count == 0) return;
        var rows = new List<string>();
        if (heading) rows.Add("Code that reaches records without a link in a record (GetFormFromFile and the like, and what the code hands out):");
        rows.Add((multi ? "| Plugin " : "") + "| Script | Function | Call | Names |");
        rows.Add((multi ? "| :--- " : "") + "| :--- | :--- | :--- | :--- |");
        // The calls that build a record first, then what is handed out
        var ordered = Calls.OrderBy(c => c.Kind == "give" ? 1 : 0).ThenBy(c => c.Plugin, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Script, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var c in ordered.Take(max))
            rows.Add($"{(multi ? $"| {c.Plugin} " : "")}| {Cell(c.Script)} | {Cell(c.Function)} | {Cell(c.Shown)} | {Cell(Names(c, o.Detail))} |");
        if (ordered.Count > max) rows.Add($"… {N(ordered.Count - max)} more calls (detail, or calls, lists every one)");
        if (heading) { body.Rows.Add(""); body.Rows.AddRange(rows); }
        else
        {
            body.Head.Add(rows[0]); body.Head.Add(rows[1]);
            body.Rows.AddRange(rows.Skip(2));
        }
    }
}

public sealed class ScriptsReport
{
    /// <summary>What Champollion is run with. A change here changes the kept folders' names.</summary>
    const string DecompileOptions = "-r -s --no-debug-line -t";
    const int Format = 1;
    static readonly Regex ScriptNameRx = new(@"^\s*ScriptName\s+([\w:]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    static readonly Regex ScriptsFolderRx = new(@"^(data\\)?scripts\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A plugin with more records than this is read for scripts without its world data, unless asked
    /// (Starfield.esm has 3.4 million world records).</summary>
    public const int WorldLimit = 100_000;

    readonly ReportSession _s;
    readonly HashSet<string> _scriptSubrecords = new(StringComparer.Ordinal);

    public ScriptsReport(ReportSession session)
    {
        _s = session;
        // The subrecords a script list sits in, from the schema: the objects with a signature of their own that
        // hold script entries (the script block, VMAD)
        foreach (var o in session.Schema.Model.Objects.Values)
            if (o.Fields.Any(f => f.RefObject == "ScriptEntry"))
                foreach (var a in session.Schema.Model.Ancestry(o.Name).Concat(session.Schema.Model.Variants(o.Name).Select(v => session.Schema.Model.Objects[v])))
                    if (a.Sig is { Length: 4 }) _scriptSubrecords.Add(a.Sig);
        // The record types that can carry a script block: the raw searches by script name pass over the rest by their
        // signature alone (Starfield.esm: about 400,000 records outside world data, most of them of types with none)
        var model = session.Schema.Model;
        var adapters = new HashSet<string>(model.Objects.Values.Where(o => o.Sig != null && _scriptSubrecords.Contains(o.Sig)).Select(o => o.Name), StringComparer.Ordinal);
        foreach (var o in model.Objects.Values.Where(o => adapters.Contains(o.Name)).ToList())
            foreach (var a in model.Ancestry(o.Name)) adapters.Add(a.Name);
        foreach (var o in model.Objects.Values)
            if (o.ObjType == "Record" && o.Sig is { Length: 4 } && !o.Abstract
                && model.Ancestry(o.Name).Any(a => a.Fields.Any(f => (f.Subrecord != null && _scriptSubrecords.Contains(f.Subrecord)) || (f.RefObject != null && adapters.Contains(f.RefObject)))))
                _scriptRecords.Add(o.Sig);
    }

    /// <summary>The record signatures whose type has a script block (empty when the schema gives none: then all are looked at).</summary>
    readonly HashSet<string> _scriptRecords = new(StringComparer.Ordinal);
    internal IReadOnlyCollection<string> ScriptRecordTypes => _scriptRecords;
    bool MayCarry(string sig) => _scriptRecords.Count == 0 || _scriptRecords.Contains(sig);

    /// <summary>The subrecords a record's script lists sit in, as the schema gives them.</summary>
    public IReadOnlyCollection<string> ScriptSubrecords => _scriptSubrecords;

    // ------------------------------------------------------------------ Champollion
    /// <summary>
    /// Where Champollion.exe is: the path given, the SFSEARCH_CHAMPOLLION variable, then for each SFSearch home
    /// folder its sfsearch.json's "champollion" setting and its tools\champollion folder. Null when it is nowhere.
    /// The home folders are only read.
    /// </summary>
    public static string? FindChampollion(string? given, params string?[] homes)
    {
        if (!string.IsNullOrWhiteSpace(given) && System.IO.File.Exists(given)) return Path.GetFullPath(given);
        string? env = Environment.GetEnvironmentVariable("SFSEARCH_CHAMPOLLION");
        if (!string.IsNullOrWhiteSpace(env) && System.IO.File.Exists(env)) return Path.GetFullPath(env);
        foreach (var home in homes)
        {
            if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home)) continue;
            string settings = Path.Combine(home, "sfsearch.json");
            if (System.IO.File.Exists(settings))
            {
                try
                {
                    var json = JsonNode.Parse(System.IO.File.ReadAllText(settings), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    if (json?["champollion"] is JsonValue v && v.TryGetValue<string>(out var path) && !string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path)) return Path.GetFullPath(path);
                }
                catch (JsonException) { }
                catch (IOException) { }
            }
            string inHome = Path.Combine(home, "tools", "champollion", "Champollion.exe");
            if (System.IO.File.Exists(inHome)) return inHome;
        }
        return null;
    }

    // ------------------------------------------------------------------ The compiled scripts
    sealed class Pex
    {
        public required string Name;     // scripts\darkstar\x.pex, as archives and Data name it
        public required string From;     // the archive's name, or "loose"
        public required Func<byte[]> Read;
        public byte[]? Bytes;
        public string Relative => ScriptsFolderRx.Replace(Name, "");
        PexFile? _parsed;
        string? _error;
        /// <summary>The compiled script read (once), or null with <see cref="Error"/> when it can't be.</summary>
        public PexFile? Parsed
        {
            get
            {
                if (_parsed != null || _error != null) return _parsed;
                try { _parsed = PexFile.Read(Bytes ??= Read()); }
                catch (InvalidDataException e) { _error = e.Message; }
                return _parsed;
            }
        }
        public string? Error => Parsed == null ? _error : null;
    }

    sealed class Collected
    {
        public List<Pex> Pex = new();
        public List<string> StampParts = new();
        public int Archived, Loose, LooseOverArchived;
        /// <summary>Loose scripts without the plugin's name in their path, taken because its records attach them.</summary>
        public List<string> LooseByRecords = new();
        /// <summary>Loose scripts its records attach that a master's archive holds too: the master's script, not taken.</summary>
        public List<string> LooseOfMasters = new();
        /// <summary>The loose scripts that were looked for in the plugin's script blocks, and how many records were looked at.</summary>
        public int LooseLookedFor, RecordsLookedAt;
        /// <summary>Loose scripts without the plugin's name in their path, taken because its scripts use them (their
        /// parent, a script their code calls, a type they name), with why; and those a master holds instead.</summary>
        public List<(string Rel, string Why)> LooseByCode = new();
        public List<(string Rel, string Why)> LooseOfMastersByCode = new();
        /// <summary>The scripts each of the plugin's scripts uses, and how (parent, calls, type).</summary>
        public Dictionary<string, List<(string Script, string How)>> Uses = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The script a compiled script's path holds: scripts\DarkStar\TGO\X.pex → DarkStar:TGO:X.</summary>
    public static string ScriptOfPath(string rel) => Path.ChangeExtension(ScriptsFolderRx.Replace(rel, ""), null).Replace('\\', ':');
    /// <summary>The path the game loads a script from: DarkStar:TGO:X → scripts\DarkStar\TGO\X.pex.</summary>
    public static string PathOfScript(string script) => "scripts\\" + script.Replace(':', '\\') + ".pex";

    /// <summary>The plugin's compiled scripts: from its own archives, loose files under Data\Scripts whose path holds
    /// the plugin's name, loose files its records attach (by name, in their script blocks), and, again and again, loose
    /// files those scripts use (their parent, a script their code calls, a type they name), when no master holds them
    /// (a master's archive, or a master's name in the path). A loose file stands in for an archived one of the same path.</summary>
    /// <paramref name="listing"/>: false when only the compiled scripts are wanted (another plugin's, read for calls): what
    /// each script uses isn't kept, and a script is read for the loose scripts it uses only when its string table names one.
    Collected Collect(ArchiveTarget own, string pluginName, Func<IReadOnlyCollection<string>, (HashSet<string> Named, int Records)>? namedByRecords, Func<string, bool>? masterHolds, bool listing = true)
    {
        var store = _s.Archives;
        var c = new Collected();
        var byPath = new Dictionary<string, Pex>(StringComparer.OrdinalIgnoreCase);
        foreach (var stamp in own.Archives)
        {
            // A texture archive (DX10) holds no script: passed over by its header, without reading its file table; nor
            // does an archive whose name table (kept for the process) has no compiled script: its file list isn't read
            if (TexturesOnly(stamp.Path) || _s.Archives.Names(stamp.Path) is { Error: null, Scripts: 0 }) continue;
            var a = store.Open(stamp.Path);
            bool any = false;
            foreach (var f in a.Files)
            {
                if (f.Extension != ".pex") continue;
                any = true;
                var file = f;
                if (byPath.TryAdd(f.Path, new Pex { Name = f.Path, From = a.Name, Read = () => file.Bytes() })) c.Archived++;
            }
            // The quick stamp of each archive that holds scripts: its size, its date, and a hash of its first and last 64 KB
            if (any) c.StampParts.Add($"{a.Name}|{QuickStamp.Read(a.Path)}");
        }
        if (own.InData)
        {
            string stem = Path.GetFileNameWithoutExtension(pluginName);
            var others = new List<string>();
            void Take(string rel)
            {
                string full = Path.Combine(store.DataFolder, rel);
                if (byPath.ContainsKey(rel)) c.LooseOverArchived++;
                byPath[rel] = new Pex { Name = rel, From = "loose", Read = () => System.IO.File.ReadAllBytes(full) };
                c.Loose++;
                c.StampParts.Add($"{rel}|{QuickStamp.Read(full)}");
            }
            foreach (var rel in store.Loose.Where(l => l.EndsWith(".pex", StringComparison.OrdinalIgnoreCase) && l.StartsWith("scripts\\", StringComparison.OrdinalIgnoreCase)).OrderBy(l => l, StringComparer.OrdinalIgnoreCase))
            {
                // The plugin's name in the path, or a loose copy of one of its archived scripts (the game uses that)
                if (rel.Contains(stem, StringComparison.OrdinalIgnoreCase) || byPath.ContainsKey(rel)) Take(rel);
                else others.Add(rel);
            }
            // The other loose scripts: the plugin's when its records attach them, unless a master's archive holds them too
            if (others.Count > 0 && namedByRecords != null)
            {
                var byScript = others.GroupBy(ScriptOfPath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                c.LooseLookedFor = byScript.Count;
                var (named, records) = namedByRecords(byScript.Keys);
                c.RecordsLookedAt = records;
                foreach (var script in named.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    string rel = byScript[script];
                    if (masterHolds?.Invoke(rel) == true) { c.LooseOfMasters.Add(rel); continue; }
                    Take(rel);
                    c.LooseByRecords.Add(rel);
                }
            }
            // The loose scripts the plugin's scripts use (S9b): a parent, a library its code calls, a type it names.
            // The game links them when it loads the script, so they are the plugin's, unless a master holds them.
            var unused = others.Where(r => !byPath.ContainsKey(r)).GroupBy(ScriptOfPath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<Pex>(byPath.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase));
            // Not listing: a script can use a loose script only when its string table holds that script's name
            var unusedNames = listing ? null : unused.Keys.Select(k => Encoding.ASCII.GetBytes(k.ToLowerInvariant())).ToList();
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                if (unusedNames != null)
                {
                    if (unused.Count == 0) break;
                    try { p.Bytes ??= p.Read(); } catch (Exception e) when (e is IOException or InvalidDataException) { continue; }
                    if (!PexFile.StringTableHolds(p.Bytes, unusedNames)) continue;
                }
                foreach (var o in p.Parsed?.Objects ?? new List<PexObject>())
                {
                    var uses = o.Uses();
                    if (listing) c.Uses[o.Name] = uses;
                    foreach (var (script, how) in uses)
                    {
                        if (unused.Count == 0 || !unused.Remove(script, out var rel)) continue;
                        string why = how switch { "parent" => $"used by {o.Name} as its parent", "calls" => $"called from {o.Name}", _ => $"named as a type in {o.Name}" };
                        if (masterHolds?.Invoke(rel) == true) { c.LooseOfMastersByCode.Add((rel, why)); continue; }
                        Take(rel);
                        c.LooseByCode.Add((rel, why));
                        queue.Enqueue(byPath[rel]);
                    }
                }
            }
        }
        else if (listing)
            foreach (var p in byPath.Values)
                foreach (var o in p.Parsed?.Objects ?? new List<PexObject>()) c.Uses[o.Name] = o.Uses();
        c.Pex = byPath.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return c;
    }

    /// <summary>True for a BA2 whose header says it holds textures only (type DX10): it can't hold a compiled script.</summary>
    static bool TexturesOnly(string path)
    {
        try
        {
            using var f = System.IO.File.OpenRead(path);
            Span<byte> head = stackalloc byte[12];
            return f.Read(head) == 12 && head[..4].SequenceEqual("BTDX"u8) && head[8..12].SequenceEqual("DX10"u8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>The content key of a set of compiled scripts: a hash of every script's name and bytes, and of how they are decompiled.</summary>
    static string ContentKey(Collected c)
    {
        var h = new XxHash128();
        h.Append(Encoding.UTF8.GetBytes($"sfsearch scripts {Format} {DecompileOptions}\n"));
        Span<byte> len = stackalloc byte[8];
        foreach (var p in c.Pex)
        {
            p.Bytes ??= p.Read();
            h.Append(Encoding.UTF8.GetBytes(p.Name.ToLowerInvariant() + "\n"));
            BitConverter.TryWriteBytes(len, (long)p.Bytes.Length);
            h.Append(len);
            h.Append(p.Bytes);
        }
        Span<byte> key = stackalloc byte[16];
        h.GetCurrentHash(key);
        return Hex(key);
    }

    sealed class Sources
    {
        public string Key = "";
        public string Folder = "";
        public List<ScriptInfo> Scripts = new();
        public string? Problem;
        public int Decompiled;
        public Collected Collected = new();
    }

    /// <summary>
    /// The plugin's scripts as source: from the kept folder when this copy of its compiled scripts was decompiled
    /// before, decompiled now otherwise.
    /// </summary>
    Sources Ensure(ArchiveTarget own, string pluginName, Collected c, bool decompile = true)
    {
        string kept = _s.Kept("scripts");
        var result = new Sources { Collected = c };
        if (c.Pex.Count == 0 || !decompile) return result;

        // The quick way: the same archives and loose files (by quick stamp) as when a content key was last worked out
        string stampFile = Path.Combine(kept, "stamps", pluginName.ToLowerInvariant() + (own.InData ? "" : "-" + Hex(XxHash64.Hash(Encoding.UTF8.GetBytes(own.Shown.ToLowerInvariant())))) + ".json");
        string stamp = Hex(XxHash128.Hash(Encoding.UTF8.GetBytes(string.Join("\n", c.StampParts) + $"\n{Format} {DecompileOptions}")));
        string? key = null;
        if (System.IO.File.Exists(stampFile))
        {
            try
            {
                var j = JsonNode.Parse(System.IO.File.ReadAllText(stampFile));
                if (j?["stamp"]?.GetValue<string>() == stamp) key = j["key"]?.GetValue<string>();
            }
            catch (JsonException) { }
            catch (IOException) { }
        }
        if (key == null || !System.IO.File.Exists(Path.Combine(kept, key, "manifest.json")))
        {
            key = ContentKey(c);
            Directory.CreateDirectory(Path.GetDirectoryName(stampFile)!);
            WriteAtomic(stampFile, new JsonObject { ["plugin"] = pluginName, ["stamp"] = stamp, ["key"] = key, ["sources"] = new JsonArray(c.StampParts.Select(p => (JsonNode)p).ToArray()) }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        result.Key = key;
        result.Folder = Path.Combine(kept, key);
        string manifest = Path.Combine(result.Folder, "manifest.json");
        if (!System.IO.File.Exists(manifest)) { Decompile(c, kept, key, pluginName); result.Decompiled = c.Pex.Count; }

        JsonNode? m;
        try { m = JsonNode.Parse(System.IO.File.ReadAllText(manifest)); }
        catch (Exception e) when (e is JsonException or IOException) { throw new ReportException($"the kept scripts in {result.Folder} can't be read: {e.Message}", "delete that folder and ask again: the scripts are decompiled again"); }
        result.Problem = m?["problem"]?.GetValue<string>();
        foreach (var x in m?["scripts"] as JsonArray ?? new JsonArray())
        {
            if (x is not JsonObject o) continue;
            string file = o["file"]!.GetValue<string>();
            result.Scripts.Add(new ScriptInfo
            {
                Plugin = pluginName, Name = o["script"]!.GetValue<string>(), File = file, Lines = o["lines"]!.GetValue<int>(),
                Pex = o["compiled"]?.GetValue<string>() ?? "", From = o["from"]?.GetValue<string>() ?? "", FullPath = Path.Combine(result.Folder, "psc", file),
            });
        }
        result.Scripts.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
        return result;
    }

    static void WriteAtomic(string path, string text)
    {
        string tmp = path + "." + Environment.ProcessId + ".tmp";
        System.IO.File.WriteAllText(tmp, text);
        System.IO.File.Move(tmp, path, true);
    }

    void Decompile(Collected c, string kept, string key, string pluginName)
    {
        string? champollion = _s.Champollion;
        if (champollion == null || !System.IO.File.Exists(champollion))
            throw new ReportException($"Champollion (the Papyrus decompiler) wasn't found, and the {Plural(c.Pex.Count, "compiled script")} of {pluginName} {(c.Pex.Count == 1 ? "hasn't" : "haven't")} been decompiled before",
                "put Champollion.exe in tools\\champollion in the SFSearch home, or name it with the SFSEARCH_CHAMPOLLION variable or \"champollion\" in sfsearch.json; it is at https://github.com/Orvid/Champollion/releases");
        string work = Path.Combine(kept, "work", $"{Environment.ProcessId}-{DateTime.UtcNow.Ticks:x}");
        string input = Path.Combine(work, "pex"), output = Path.Combine(work, "out", "psc");
        try
        {
            var relOf = new Dictionary<string, Pex>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in c.Pex)
            {
                string rel = p.Relative;
                // A compiled script's path is data from whoever made the archive: it must stay inside the work folder
                string? dest = ArchiveReport.SafeTarget(input, rel);
                if (dest == null) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                System.IO.File.WriteAllBytes(dest, p.Bytes ??= p.Read());
                relOf[Path.ChangeExtension(rel, null)] = p;
            }
            Directory.CreateDirectory(output);
            var psi = new ProcessStartInfo(champollion) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            psi.ArgumentList.Add(input);
            foreach (var a in DecompileOptions.Split(' ')) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(output);
            using (var proc = Process.Start(psi) ?? throw new ReportException($"Champollion couldn't be started: {champollion}"))
            {
                proc.StandardInput.Close();
                var so = proc.StandardOutput.ReadToEndAsync();
                var se = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(15 * 60 * 1000))
                {
                    try { proc.Kill(true); } catch (InvalidOperationException) { }
                    throw new ReportException("Champollion took longer than 15 minutes", "ask again; if it happens again, decompile fewer scripts at a time");
                }
                so.Wait(); se.Wait();
            }
            var scripts = new JsonArray();
            int made = 0;
            foreach (var f in Directory.GetFiles(output, "*.psc", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string text = System.IO.File.ReadAllText(f);
                var m = ScriptNameRx.Match(text);
                string rel = Path.GetRelativePath(output, f);
                relOf.TryGetValue(Path.ChangeExtension(rel, null), out var pex);
                scripts.Add(new JsonObject
                {
                    ["script"] = m.Success ? m.Groups[1].Value : Path.GetFileNameWithoutExtension(f), ["file"] = rel,
                    ["lines"] = text.Replace("\r\n", "\n").Split('\n').Length, ["compiled"] = pex?.Name ?? "", ["from"] = pex?.From ?? "",
                });
                made++;
            }
            var manifest = new JsonObject
            {
                ["format"] = Format, ["key"] = key, ["madeFor"] = pluginName, ["made"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["champollion"] = champollion, ["options"] = DecompileOptions, ["compiledScripts"] = c.Pex.Count, ["scripts"] = scripts,
            };
            if (made < c.Pex.Count) manifest["problem"] = $"Champollion decompiled {made} of {c.Pex.Count} scripts";
            System.IO.File.WriteAllText(Path.Combine(work, "out", "manifest.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            string final = Path.Combine(kept, key);
            // Another process may have made the same folder meanwhile: the same content gives the same sources
            if (Directory.Exists(final) && !System.IO.File.Exists(Path.Combine(final, "manifest.json"))) Directory.Delete(final, true);
            if (!Directory.Exists(final))
            {
                try { Directory.Move(Path.Combine(work, "out"), final); }
                catch (IOException) when (System.IO.File.Exists(Path.Combine(final, "manifest.json"))) { }
            }
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            // The folder for work in progress goes too, when no other process has work in it
            try { Directory.Delete(Path.Combine(kept, "work")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ------------------------------------------------------------------ Scripts on records
    /// <summary>Every script a record carries, found by walking its JSON beside the schema: the name of each script
    /// entry (on the record, on a quest's aliases, in script fragments). A script's own properties aren't walked.</summary>
    public void WalkScripts(string type, JsonObject record, Action<string, string> found) => WalkObject(type, record, "", (n, p, _) => found(n, p));

    void WalkObject(string type, JsonObject o, string path, Action<string, string, JsonObject> found)
    {
        if (type == "ScriptEntry")
        {
            if (o["Name"] is JsonValue v && v.TryGetValue<string>(out var name) && name.Length > 0) found(name, path, o);
            return;
        }
        foreach (var (name, value) in o)
        {
            if (value == null || name == RecordSplit.TypeKey) continue;
            var prop = _s.Schema.Prop(type, name);
            if (prop == null) continue;
            string at = path.Length == 0 ? name : path + "." + name;
            switch (value)
            {
                case JsonObject child:
                    if (RecordSplit.ChildOf(child) != null) break;
                    if (ObjectOf(prop.Main.RefObject, child) is { } t) WalkObject(t, child, at, found);
                    break;
                case JsonArray list:
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] is JsonObject item && RecordSplit.ChildOf(item) == null && ObjectOf(prop.Item?.RefObject ?? prop.Main.RefObject, item) is { } it)
                            WalkObject(it, item, $"{at}[{i}]", found);
                    break;
            }
        }
    }

    string? ObjectOf(string? refObject, JsonObject o) =>
        _s.Schema.ObjectFor(refObject, o[RecordSplit.TypeKey] is JsonValue t && t.TryGetValue<string>(out var name) ? name : null);

    /// <summary>The scripts the plugin's records carry. A record is read only when its bytes hold a script block (and,
    /// with <paramref name="only"/>, when the block holds one of those names: then the answer's counts are left alone).
    /// <paramref name="entries"/> gets each script entry with its properties, by script.</summary>
    List<ScriptCarrier> Carriers(PluginSide side, ReadScope scope, ScriptsAnswer answer, bool? worldAsked, IReadOnlyCollection<string>? only = null, Dictionary<string, List<(ScriptCarrier Carrier, JsonObject Entry)>>? entries = null, Dictionary<string, string>? baseOf = null,
        IReadOnlySet<string>? types = null)
    {
        var onlyBytes = only?.Select(x => Encoding.ASCII.GetBytes(x.ToLowerInvariant())).ToList();
        bool count = only == null;
        var carriers = new List<ScriptCarrier>();
        var p = side.Plugin;
        if (p.Segment == null || p.Problems.MissingMasters.Count > 0)
        {
            answer.Notes.Add($"{side.Shown}: its records couldn't be read for the scripts they carry ({(p.Problems.MissingMasters.Count > 0 ? "missing master: " + string.Join(", ", p.Problems.MissingMasters) : p.Problems.ReadError ?? "no header index")}).");
            return carriers;
        }
        var seg = side.Segment;
        bool world = worldAsked ?? seg.Count <= WorldLimit;
        var label = _s.Compare.Labeller(side);
        scope.Touch(side.Engine, side.Name);
        // With names given: the records whose script block holds one, found first from the raw bytes on several threads
        var pick = onlyBytes != null ? Naming(side, seg, world, onlyBytes, types) : null;
        for (int i = 0; i < seg.FirstUnresolved; i++)
        {
            if (pick != null && !pick.Contains(i)) continue;
            var e = seg[i];
            if (!world && ReportSession.IsWorld(seg, e)) { if (count) answer.WorldLeftOut++; continue; }
            string sig = seg.SignatureOf(e);
            if (sig == "NAVI") continue;
            var r = new RecordRef(p.Id, i);
            if (count) answer.RecordsScanned++;
            if (_scriptSubrecords.Count > 0)
            {
                RawRecord raw;
                try { raw = side.Engine.Raw(r); } catch (RecordException) { continue; }
                if (!raw.Subrecords.Any(s => _scriptSubrecords.Contains(s.Signature))) continue;
                if (onlyBytes != null && !raw.Subrecords.Any(s => _scriptSubrecords.Contains(s.Signature) && HoldsAny(raw.Content(s), onlyBytes))) continue;
            }
            RecordView view;
            try { view = _s.Compare.View(side.Engine, r); } catch (CompareException) { continue; }
            string key = ReportSession.Canon(view.Info.FormKey);
            int before = carriers.Count;
            // A placed object's script properties that it doesn't set come from its base object's
            if (baseOf != null && view.Json["Base"] is JsonValue bv && bv.TryGetValue<string>(out var baseKey) && RecordSplit.IsFormKey(baseKey)) baseOf[key] = ReportSession.Canon(baseKey);
            WalkObject(view.Type, view.Json, "", (script, where, entry) =>
            {
                var carrier = new ScriptCarrier(label(key), key, sig, script, where);
                carriers.Add(carrier);
                if (entries != null) (entries.TryGetValue(script, out var l) ? l : entries[script] = new()).Add((carrier, entry));
            });
            if (count && carriers.Count > before) answer.RecordsWithScripts++;
        }
        return carriers;
    }

    /// <summary>The records of the plugin whose script block holds one of the names, read raw straight from the file.</summary>
    /// <paramref name="types"/>: only records of these types can attach the scripts (<see cref="CarrierTypes"/>); null: any.
    HashSet<int> Naming(PluginSide side, Segment seg, bool world, List<byte[]> names, IReadOnlySet<string>? types = null)
    {
        var at = new List<(long Offset, int Index)>(seg.FirstUnresolved);
        for (int i = 0; i < seg.FirstUnresolved; i++)
        {
            var e = seg[i];
            if ((!world && ReportSession.IsWorld(seg, e)) || seg.SignatureOf(e) == "NAVI" || !MayCarry(seg.SignatureOf(e)) || (types != null && !types.Contains(seg.SignatureOf(e)))) continue;
            at.Add((e.Offset, i));
        }
        at.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        if (at.Count == 0) return new HashSet<int>();
        var found = new System.Collections.Concurrent.ConcurrentBag<int>();
        using var file = System.IO.File.OpenHandle(side.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, at.Count, 2048), range =>
        {
            for (int k = range.Item1; k < range.Item2; k++)
            {
                RawRecord raw;
                try { raw = RawRecord.Read(file, at[k].Offset); } catch (Exception ex) when (ex is RecordException or IOException or InvalidDataException) { continue; }
                if (raw.Subrecords.Any(s => _scriptSubrecords.Contains(s.Signature) && HoldsAny(raw.Content(s), names))) found.Add(at[k].Index);
            }
        });
        return found.ToHashSet();
    }

    /// <summary>True when the bytes hold one of the names (lower case), letter case not counting.</summary>
    static bool HoldsAny(ReadOnlySpan<byte> bytes, List<byte[]> names)
    {
        var lower = bytes.ToArray();
        for (int k = 0; k < lower.Length; k++) if (lower[k] is >= (byte)'A' and <= (byte)'Z') lower[k] += 32;
        foreach (var n in names) if (lower.AsSpan().IndexOf(n) >= 0) return true;
        return false;
    }

    // ------------------------------------------------------------------ Loose scripts the records name
    /// <summary>
    /// Which of <paramref name="scripts"/> the plugin's script blocks name: each name is looked for, as the length
    /// and text a script entry writes, in the raw bytes of every script block (letter case doesn't count). Nothing is
    /// read through Mutagen; world data is left out as when the records are read for their scripts.
    /// </summary>
    (HashSet<string> Named, int Records) NamedInScriptBlocks(PluginSide side, ReadScope scope, IReadOnlyCollection<string> scripts, bool? worldAsked)
    {
        var found = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var p = side.Plugin;
        if (p.Segment == null || _scriptSubrecords.Count == 0 || scripts.Count == 0) return (new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
        var wanted = scripts.Select(s => (Name: s, Bytes: Encoding.ASCII.GetBytes(s.ToLowerInvariant()))).ToList();
        var seg = side.Segment;
        bool world = worldAsked ?? seg.Count <= WorldLimit;
        scope.Touch(side.Engine, side.Name);
        // Where each record sits in the file, in file order
        var offsets = new List<long>(seg.FirstUnresolved);
        for (int i = 0; i < seg.FirstUnresolved; i++)
        {
            var e = seg[i];
            if ((!world && ReportSession.IsWorld(seg, e)) || seg.SignatureOf(e) == "NAVI" || !MayCarry(seg.SignatureOf(e))) continue;
            offsets.Add(e.Offset);
        }
        offsets.Sort();
        // A plugin with no record to look at (all world data left out, or none at all): Partitioner.Create takes no empty range
        if (offsets.Count == 0) return (new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
        // Read straight from the file, on several threads (positional reads of one handle may run at once)
        using var file = System.IO.File.OpenHandle(side.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, offsets.Count, 2048), (range, loop) =>
        {
            for (int i = range.Item1; i < range.Item2; i++)
            {
                if (found.Count >= wanted.Count) { loop.Stop(); return; }
                RawRecord raw;
                try { raw = RawRecord.Read(file, offsets[i]); } catch (Exception ex) when (ex is RecordException or IOException or InvalidDataException) { continue; }
                foreach (var s in raw.Subrecords)
                {
                    if (!_scriptSubrecords.Contains(s.Signature)) continue;
                    var original = raw.Content(s);
                    var lower = original.ToArray();
                    for (int k = 0; k < lower.Length; k++) if (lower[k] is >= (byte)'A' and <= (byte)'Z') lower[k] += 32;
                    foreach (var (name, bytes) in wanted)
                    {
                        if (found.ContainsKey(name)) continue;
                        int at = 0;
                        while (at < lower.Length)
                        {
                            int k = lower.AsSpan(at).IndexOf(bytes);
                            if (k < 0) break;
                            k += at;
                            if (k >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(original[(k - 2)..]) == bytes.Length) { found.TryAdd(name, true); break; }
                            at = k + 1;
                        }
                    }
                }
            }
        });
        return (new HashSet<string>(found.Keys, StringComparer.OrdinalIgnoreCase), offsets.Count);
    }

    // ------------------------------------------------------------------ Beyond the plugin (S9a)
    /// <summary>A plugin of up to this many records has its world data read too when its records are looked for by the
    /// names of a few scripts (raw, on several threads): kinggathcreations_spaceship.esm's 371,811 records take about
    /// a second. Starfield.esm (3.8 million) is read without it unless world is asked for.</summary>
    public const int NamedWorldLimit = 1_000_000;

    ScriptInjections? _parentFinder;

    /// <summary>The record types that can attach a script of each of these game types (the type a script extends, at
    /// the top of its chain): the game binds a script only to a record of its type. A quest's script, its aliases' and
    /// its fragments' are on quests, an actor's on NPCs and placed actors, and so on.</summary>
    static readonly Dictionary<string, string[]> CarriedBy = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Quest"] = new[] { "QUST" }, ["ReferenceAlias"] = new[] { "QUST" }, ["RefCollectionAlias"] = new[] { "QUST" }, ["LocationAlias"] = new[] { "QUST" },
        ["Actor"] = new[] { "NPC_", "ACHR" }, ["ActiveMagicEffect"] = new[] { "MGEF" }, ["TopicInfo"] = new[] { "INFO" }, ["Scene"] = new[] { "SCEN" },
        ["Package"] = new[] { "PACK" }, ["Perk"] = new[] { "PERK" },
    };

    /// <summary>
    /// The record types that can attach one of these scripts: each script's chain of parents (as the compiled scripts
    /// read give it) up to a game type of <see cref="CarriedBy"/>. Null when a script's chain doesn't end in one (an
    /// ObjectReference's script can be on many types of records): then any type that carries a script block is looked in.
    /// </summary>
    HashSet<string>? CarrierTypes(IEnumerable<string> scripts, Dictionary<string, string> parents)
    {
        // A script none of the plugin's compiled scripts extends or is (a game script whose property a caller in the
        // game's code uses, a parent the plugin doesn't ship): its parent from its compiled script, where the game finds it
        _parentFinder ??= new ScriptInjections(_s.Archives);
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var script in scripts)
        {
            string? at = script;
            string[]? found = null;
            for (int depth = 0; depth < 32 && at is { Length: > 0 } && found == null; depth++)
                if (!CarriedBy.TryGetValue(at, out found)) at = parents.TryGetValue(at, out var parent) ? parent : _parentFinder.ParentOf(at);
            if (found == null) return null;
            types.UnionWith(found);
        }
        return types;
    }

    /// <summary>The plugins the game loads that have the plugin as a master, in load order (none that Mutagen refuses
    /// or whose masters are missing).</summary>
    List<IndexedPlugin> Dependents(PluginSide side) =>
        _s.Index.Plugins.Where(p => p.Order.Enabled && p.LeftOut == null && p.Segment != null && p.Problems.MissingMasters.Count == 0
                && !p.Name.Equals(side.Name, StringComparison.OrdinalIgnoreCase)
                && (p.Header?.Masters ?? Array.Empty<string>()).Any(m => m.Equals(side.Name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Order.Position).ToList();

    /// <summary>
    /// Other plugins' compiled scripts, read for the calls of a function no script of the plugin calls: those of the
    /// plugins the game loads that have the plugin as a master (their archives, their loose scripts), and, for a script
    /// the plugin holds a copy of a master's, the masters' (the game's own scripts call a game script the plugin
    /// replaces). A script is read only when its string table names the function, and each once.
    /// </summary>
    sealed class OtherScripts
    {
        readonly ScriptsReport _r;
        readonly PluginSide _side;
        readonly List<IndexedPlugin> _deps;
        readonly FileFinder _finder;
        List<(string Plugin, Pex Pex)>? _depFiles, _masterFiles;
        readonly Dictionary<Pex, ScriptCodeResult> _read = new();
        readonly Dictionary<string, bool> _copy = new(StringComparer.OrdinalIgnoreCase);
        public OtherScripts(ScriptsReport r, PluginSide side, List<IndexedPlugin> deps, FileFinder finder) { _r = r; _side = side; _deps = deps; _finder = finder; }
        /// <summary>How many other plugins' scripts were looked at, and read for their code.</summary>
        public int LookedAt => (_depFiles?.Count ?? 0) + (_masterFiles?.Count ?? 0);
        public int Read => _read.Count;
        public bool MastersLooked => _masterFiles != null;
        public string TextFor(string script) => Deps + (_copy.TryGetValue(script, out bool copy) && copy ? $" or of its masters ({string.Join(", ", _side.Masters)})" : "");
        string Deps => _deps.Count == 0 ? $"a plugin that has {_side.Name} as a master (none does)" : _deps.Count == 1 ? $"the plugin that has {_side.Name} as a master ({_deps[0].Name})" : $"the {N(_deps.Count)} plugins that have {_side.Name} as a master";
        public string Text => (_deps.Count == 0 ? $"a plugin that has {_side.Name} as a master (none does)" : _deps.Count == 1 ? $"the plugin that has {_side.Name} as a master ({_deps[0].Name})" : $"the {N(_deps.Count)} plugins that have {_side.Name} as a master")
            + (_masterFiles != null ? $" or of its masters ({string.Join(", ", _side.Masters)})" : "");

        List<(string, Pex)> Load(IEnumerable<string> plugins)
        {
            var list = new List<(string, Pex)>();
            foreach (var name in plugins)
            {
                ArchiveTarget own;
                try { own = ArchiveReport.Resolve(_r._s, name); } catch (ReportException) { continue; }
                foreach (var p in _r.Collect(own, name, null, null, listing: false).Pex) list.Add((name, p));
            }
            return list;
        }

        public IEnumerable<CallSite> Calls(string script, string function)
        {
            _depFiles ??= Load(_deps.Select(d => d.Name));
            IEnumerable<(string Plugin, Pex Pex)> files = _depFiles;
            if (!_copy.TryGetValue(script, out bool copy))
                _copy[script] = copy = _finder.FindNonTexture(PathOfScript(script), loose: false, only: FileOrigin.Master).Origin == FileOrigin.Master;
            if (copy) files = files.Concat(_masterFiles ??= Load(_side.Masters));
            var found = new List<CallSite>();
            // Only a script whose string table names the function can call it: the tables are looked at on every core
            // (the bytes are read first, one archive at a time), and only those scripts are read for their code
            var list = files.ToList();
            foreach (var (_, p) in list)
                if (p.Bytes == null) try { p.Bytes = p.Read(); } catch (Exception e) when (e is IOException or InvalidDataException) { }
            var name = new[] { Encoding.ASCII.GetBytes(function.ToLowerInvariant()) };
            var hits = new bool[list.Count];
            Parallel.For(0, list.Count, i => hits[i] = _read.ContainsKey(list[i].Pex) || (list[i].Pex.Bytes is { } b && PexFile.StringTableHolds(b, name)));
            for (int i = 0; i < list.Count; i++)
            {
                if (!hits[i]) continue;
                var (plugin, p) = list[i];
                if (!_read.TryGetValue(p, out var r))
                {
                    var file = p.Parsed;
                    if (file == null) continue;
                    r = new ScriptCodeResult { Plugin = plugin };
                    ScriptCode.Read(file, plugin, r);
                    _read[p] = r;
                }
                foreach (var s in r.Sites) if (s.Function.Equals(function, StringComparison.OrdinalIgnoreCase)) found.Add(s with { CallerPlugin = plugin });
            }
            return found;
        }
    }

    /// <summary>What was read beyond the plugin's own records for the values of script properties.</summary>
    sealed class Widened
    {
        public int Overridden, Dependents, DependentRecords, MasterRecords, WorldRecords, Removed;
        public readonly List<string> MastersLooked = new();
        public readonly List<string> RemovedBy = new();
        public readonly HashSet<string> WorldLeftOut = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The records the game takes the scripts' property values from, beyond the plugin's own versions of its records:
    /// each record of the plugin that attaches a script, as the load order's winner sets it (a later plugin's override);
    /// the records of the plugins the game loads that have the plugin as a master (each as its winner sets it); and,
    /// for a script none of those attaches that is a copy of a master's script, the masters' records.
    /// </summary>
    void Widen(PluginSide side, ReadScope scope, ScriptsAnswer answer, IReadOnlyCollection<string> names, Dictionary<string, List<(ScriptCarrier Carrier, JsonObject Entry)>> entries,
        Dictionary<string, string> baseOf, FileFinder finder, Dictionary<string, RecordView?> views, Widened info, Dictionary<string, string> parents)
    {
        // The winner of each record found: a later plugin's override sets the values the game uses
        void TakeWinners(Dictionary<string, List<(ScriptCarrier Carrier, JsonObject Entry)>> found, string plugin)
        {
            foreach (var (script, list) in found)
            {
                var into = entries.TryGetValue(script, out var l) ? l : entries[script] = new();
                foreach (var (carrier, entry) in list)
                {
                    if (into.Any(x => x.Carrier.FormKey.Equals(carrier.FormKey, StringComparison.OrdinalIgnoreCase) && x.Carrier.Where == carrier.Where)) continue;
                    var hit = _s.Winner(carrier.FormKey);
                    string? winner = hit?.Plugin.Name;
                    if (hit == null || winner == null || winner.Equals(plugin, StringComparison.OrdinalIgnoreCase)) { into.Add((carrier, entry)); continue; }
                    var view = ViewOf(hit.Value, views);
                    JsonObject? won = null;
                    if (view != null) WalkObject(view.Type, view.Json, "", (s2, where, e2) => { if (won == null && s2.Equals(script, StringComparison.OrdinalIgnoreCase) && where == carrier.Where) won = e2; });
                    if (won == null && view != null) WalkObject(view.Type, view.Json, "", (s2, _, e2) => { if (won == null && s2.Equals(script, StringComparison.OrdinalIgnoreCase)) won = e2; });
                    if (won == null) { info.Removed++; info.RemovedBy.Add($"{carrier.Record} {carrier.Signature} ({script}; overridden by {winner})"); continue; }
                    info.Overridden++;
                    if (view!.Json["Base"] is JsonValue bv && bv.TryGetValue<string>(out var baseKey) && RecordSplit.IsFormKey(baseKey)) baseOf[carrier.FormKey] = ReportSession.Canon(baseKey);
                    into.Add((carrier with { By = winner }, won));
                }
            }
        }
        var own = entries.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase);
        entries.Clear();
        TakeWinners(own, side.Name);

        // The plugins that have it as a master: a patch that attaches the script, or overrides a record that does
        var deps = Dependents(side);
        info.Dependents = deps.Count;
        foreach (var dep in deps)
        {
            var depSide = _s.Side(dep.Name);
            var found = new Dictionary<string, List<(ScriptCarrier Carrier, JsonObject Entry)>>(StringComparer.OrdinalIgnoreCase);
            info.DependentRecords += Carriers(depSide, scope, answer, WorldFor(depSide, null, info), names, found, baseOf, CarrierTypes(names, parents)).Select(x => x.FormKey).Distinct().Count();
            TakeWinners(found, dep.Name);
        }

        // A copy of a master's script that nothing above attaches: the masters' records attach it
        var copies = names.Where(n => !(entries.TryGetValue(n, out var l) && l.Count > 0)
            && finder.FindNonTexture(PathOfScript(n), loose: false, only: FileOrigin.Master).Origin == FileOrigin.Master).ToList();
        if (copies.Count > 0)
            foreach (var m in side.Masters)
            {
                if (_s.Index.Plugin(m) is not { Segment: not null } mp || mp.LeftOut != null) continue;
                var mSide = _s.Side(m);
                // A master's world data only up to the list's limit: Starfield.esm and the official plugins are read without it
                bool mWorld = mSide.Segment.Count <= WorldLimit;
                if (!mWorld) info.WorldLeftOut.Add(m);
                info.MastersLooked.Add(m);
                var found = new Dictionary<string, List<(ScriptCarrier Carrier, JsonObject Entry)>>(StringComparer.OrdinalIgnoreCase);
                info.MasterRecords += Carriers(mSide, scope, answer, mWorld, copies, found, baseOf, CarrierTypes(copies, parents)).Select(x => x.FormKey).Distinct().Count();
                TakeWinners(found, m);
            }
    }

    /// <summary>Whether a plugin's world data is read when its records are looked for by script names.</summary>
    bool WorldFor(PluginSide side, bool? asked, Widened info)
    {
        if (asked is bool b) return b;
        bool world = side.Segment.Count <= NamedWorldLimit;
        if (!world) info.WorldLeftOut.Add(side.Name);
        return world;
    }

    RecordView? ViewOf(Hit hit, Dictionary<string, RecordView?> views)
    {
        string key = hit.Plugin.Name + "|" + hit.Ref.Entry;
        if (views.TryGetValue(key, out var v)) return v;
        // A partial form (a quest override that holds only some fields) is merged over the version before it, as the game does
        try { v = _s.Compare.Effective(hit.Engine, hit.Ref); } catch (CompareException) { v = null; }
        return views[key] = v;
    }

    /// <summary>
    /// The values of a property of the script on the record a property holds (two steps: <c>workerContainer of
    /// property core</c>), as the load order's winner of that record sets it: the record's own scripts, or with an
    /// alias, that alias's scripts. A placed reference that doesn't set it takes its base object's; one set nowhere
    /// takes the script's default (when the script is one of the plugin's). The script whose property is read is the
    /// property's declared type when the record attaches it (or a script extending it), else any of its scripts with
    /// a property of that name.
    /// </summary>
    List<Choice> ChainValues(string formKey, int alias, string chain, string? type, Dictionary<string, string> parents, Dictionary<string, PexValue> defaults, Dictionary<string, RecordView?> views, int depth = 0)
    {
        var none = new List<Choice>();
        if (_s.Winner(formKey) is not { } hit) return new List<Choice> { new($"{formKey} isn't in Data", null, null, -1, Unset: true) };
        var view = ViewOf(hit, views);
        if (view == null) return new List<Choice> { new("its record can't be read", null, null, -1, Unset: true) };
        string? aliasPath = null;
        if (alias >= 0 && view.Json["VirtualMachineAdapter"]?["Aliases"] is JsonArray aliases)
            for (int i = 0; i < aliases.Count; i++)
                if (aliases[i]?["Property"]?["Alias"] is JsonValue av && av.TryGetValue<int>(out int id) && id == alias) { aliasPath = $"VirtualMachineAdapter.Aliases[{i}]."; break; }
        var found = new List<(string Script, JsonObject Entry)>();
        WalkObject(view.Type, view.Json, "", (script, where, entry) =>
        {
            bool onAlias = where.StartsWith("VirtualMachineAdapter.Aliases[", StringComparison.Ordinal);
            if (alias >= 0 ? aliasPath != null && where.StartsWith(aliasPath, StringComparison.Ordinal) : !onAlias) found.Add((script, entry));
        });
        var typed = type == null ? new List<(string Script, JsonObject Entry)>() : found.Where(f => SameOrChild(type, parents).Contains(f.Script, StringComparer.OrdinalIgnoreCase)).ToList();
        var holders = typed.Count > 0 ? typed : found.Where(f => PropertyOf(f.Entry, chain) != null).ToList();
        foreach (var (script, entry) in holders)
            if (PropertyOf(entry, chain) is { } p) return PropertyValues(p, null).ToList();
        // A placed reference that doesn't set it: its base object's
        if (depth == 0 && alias < 0 && view.Json["Base"] is JsonValue bv && bv.TryGetValue<string>(out var baseKey) && RecordSplit.IsFormKey(baseKey))
        {
            var fromBase = ChainValues(ReportSession.Canon(baseKey), -1, chain, type, parents, defaults, views, depth + 1);
            if (fromBase.Any(c => !c.Unset)) return fromBase.Select(c => c with { Text = $" (from its base {Find(uint.Parse(ReportSession.Canon(baseKey)[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture), ReportSession.Canon(baseKey)[7..]).Record ?? baseKey})" }).ToList();
        }
        string? script0 = typed.Count > 0 ? typed[0].Script : type;
        if (holders.Count == 0 && typed.Count == 0)
            return new List<Choice> { new(type != null ? $"it attaches no {type}" : "no script on it has that property", null, null, -1, Unset: true) };
        var d = script0 != null && defaults.TryGetValue(script0 + "|" + chain, out var dv) ? dv : new PexValue(PexValue.None, null, 0, 0, false);
        return new List<Choice> { new(d.Kind == PexValue.None ? "not set (None)" : $"not set (the script's default {d})", d.Kind == PexValue.None ? null : d, null, -1, Unset: true) };
    }

    // ------------------------------------------------------------------ The code
    /// <summary>Reads the plugin's compiled scripts for the calls that reach records without a link and the records
    /// they hand out, and for the script each extends. A parameter is followed to the calls of its function in the
    /// plugin's scripts. The calls are located later, once the values of script properties are known.</summary>
    ScriptCodeResult ReadCode(PluginSide side, Collected c, ScriptsAnswer answer, Dictionary<string, string> parents, Dictionary<string, PexValue> propertyDefaults, FileFinder finder)
    {
        var result = new ScriptCodeResult();
        foreach (var p in c.Pex)
        {
            var file = p.Parsed;
            if (file == null) { answer.CompiledUnread.Add((side.Name, p.Name, p.Error ?? "unreadable")); continue; }
            answer.CompiledRead++;
            foreach (var o in file.Objects)
            {
                parents.TryAdd(o.Name, o.Parent);
                // What an auto property is when a record attaching the script doesn't set it: its variable's start value
                foreach (var prop in o.Properties)
                    if (prop.AutoVariable != null && o.Variables.FirstOrDefault(v => v.Name.Equals(prop.AutoVariable, StringComparison.OrdinalIgnoreCase)) is { } v)
                        propertyDefaults.TryAdd(o.Name + "|" + prop.Name, v.Start);
            }
            ScriptCode.Read(file, side.Name, result);
        }
        // Parameters to their callers (in the plugin's scripts, else in the scripts of the plugins that have it as a
        // master), what a call of the plugin's own function returns, and two-step property chains
        var deps = Dependents(side);
        var others = new OtherScripts(this, side, deps, finder);
        ScriptCode.Resolve(result, new ScriptCode.ResolveScope
        {
            SameOrChild = script => SameOrChild(script, parents),
            ParentOf = script => parents.TryGetValue(script, out var parent) && parent.Length > 0 ? parent : null,
            Others = deps.Count > 0 || side.Masters.Count > 0 ? others.Calls : null,
            OthersText = others.TextFor,
        });
        if (others.LookedAt > 0)
            answer.Read.Add($"{Plural(others.LookedAt, "compiled script")} of {others.Text} for calls of functions no script of {side.Name} calls ({N(others.Read)} of them read for their code)");
        answer.PropertyGives += result.PropertyGives;
        answer.NothingGives += result.NothingGives;
        answer.Calls.AddRange(result.Calls);
        return result;
    }

    /// <summary>A script and every script of <paramref name="parents"/> that extends it, at any depth.</summary>
    static IEnumerable<string> SameOrChild(string script, Dictionary<string, string> parents)
    {
        yield return script;
        foreach (var (child, _) in parents)
        {
            string? at = child;
            for (int depth = 0; depth < 32 && at != null && parents.TryGetValue(at, out var parent) && parent.Length > 0; depth++)
            {
                if (parent.Equals(script, StringComparison.OrdinalIgnoreCase)) { yield return child; break; }
                at = parent;
            }
        }
    }

    /// <summary>
    /// The values a call takes from script properties, per record of the plugin that attaches the script (or a script
    /// that extends it): one target for each record, with the property's value there, or its default when the record
    /// doesn't set it.
    /// </summary>
    void JoinProperties(ScriptCall call, string pluginName, Dictionary<string, string> parents, Dictionary<string, List<(ScriptCarrier Carrier, JsonObject Entry)>> entries, Dictionary<string, PexValue> defaults, Dictionary<string, string> baseOf,
        Dictionary<string, RecordView?> views, Widened info)
    {
        var used = call.Values.Take(call.Kind == "get" ? 2 : 1).ToList();
        var scripts = call.PropertyScripts.ToList();
        if (scripts.Count == 0) return;
        // The records that attach one of those scripts, or a script that extends one
        var carriers = new Dictionary<string, (ScriptCarrier Carrier, Dictionary<string, JsonObject> Entries)>(StringComparer.OrdinalIgnoreCase);
        foreach (var script in scripts)
            foreach (var attached in SameOrChild(script, parents).Distinct(StringComparer.OrdinalIgnoreCase))
                if (entries.TryGetValue(attached, out var list))
                    foreach (var (carrier, entry) in list)
                    {
                        if (!carriers.TryGetValue(carrier.FormKey + "|" + carrier.Where, out var have)) carriers[carrier.FormKey + "|" + carrier.Where] = have = (carrier, new(StringComparer.OrdinalIgnoreCase));
                        have.Entries[script] = entry;
                    }
        if (carriers.Count == 0)
        {
            string where = pluginName + (info.Dependents > 1 ? $" or of the {N(info.Dependents)} plugins that have it as a master" : info.Dependents == 1 ? " or of the plugin that has it as a master" : "")
                + (info.MastersLooked.Count > 0 ? $" or of its masters ({string.Join(", ", info.MastersLooked)})" : "");
            string left = info.WorldLeftOut.Count > 0 ? $"; the world data of {string.Join(", ", info.WorldLeftOut)} wasn't looked in (too many records: ask with world)" : "";
            string none = $"no record of {where} attaches {string.Join(" or ", scripts)}{(scripts.Any(x => SameOrChild(x, parents).Skip(1).Any()) ? " or a script that extends it" : "")}"
                + (info.Dependents == 0 ? " (no plugin the game loads has it as a master)" : "") + $"{left}: the code runs only where a plugin outside these attaches it";
            call.Via = call.Via is { Length: > 0 } v0 ? v0 + "; " + none : none;
            return;
        }
        call.Targets.RemoveAll(t => t.Carrier != null);
        foreach (var (carrier, byScript) in carriers.Values.OrderBy(x => x.Carrier.Record, StringComparer.OrdinalIgnoreCase))
        {
            string carrierText = $"{carrier.Record} {carrier.Signature}" + (carrier.Where.StartsWith("VirtualMachineAdapter.Scripts[", StringComparison.Ordinal) ? "" : $" ({ShortWhere(carrier.Where)})")
                + (carrier.By != null ? $" (as {carrier.By} sets it, the version the game uses)" : "");
            // Each used argument's values on this record
            var perArg = new List<List<Choice>>();
            foreach (var values in used)
            {
                var choices = new List<Choice>();
                foreach (var v in values)
                {
                    if (v.Kind == ValueOrigin.Property && v.Script != null && v.Property != null)
                    {
                        // A record that attaches another of the scripts has no value for this script's property
                        if (!byScript.TryGetValue(v.Script, out var entry)) continue;
                        var prop = PropertyOf(entry, v.Property);
                        string from = "";
                        // A placed object that doesn't set the property has its base object's value
                        // (the base may attach the script or one that extends it)
                        if (prop == null && baseOf.TryGetValue(carrier.FormKey, out var baseKey)
                            && SameOrChild(v.Script, parents).SelectMany(x => entries.TryGetValue(x, out var l) ? l : new()).FirstOrDefault(x => x.Carrier.FormKey.Equals(baseKey, StringComparison.OrdinalIgnoreCase) && PropertyOf(x.Entry, v.Property!) != null) is { Entry: not null } be
                            && PropertyOf(be.Entry, v.Property) is { } bp)
                        { prop = bp; from = $" (from its base {be.Carrier.Record})"; }
                        string name = (v.Member != null ? $"{v.Member} of {(v.Element ? "an element of " : "")}" : "") + v.Property + from;
                        // FormList.GetAt on it: the record handed out is an entry of the form list it holds
                        if (v.Entries) name = "an entry of " + name;
                        if (prop == null && v.Chain != null)
                        {
                            choices.Add(new Choice(call.Kind == "give" ? $"{name} (not set on the record) → {v.Chain}" : $"{name} isn't set on the record → {v.Chain}", null, null, -1, Unset: true));
                            continue;
                        }
                        if (prop == null)
                        {
                            var d = v.Member == null && defaults.TryGetValue(v.Script + "|" + v.Property, out var dv) ? dv : new PexValue(PexValue.None, null, 0, 0, false);
                            choices.Add(new Choice(call.Kind == "give" ? $"{name} (not set on the record)" : $"{name} isn't set on the record ({(d.Kind == PexValue.None ? "None" : $"the script's default {d}")})",
                                d.Kind == PexValue.None ? null : d, null, -1, Unset: true));
                            continue;
                        }
                        if (v.Chain != null)
                        {
                            // Two steps: the record the property holds, then the property of its script there
                            foreach (var x in PropertyValues(prop, null))
                            {
                                string holder = x.FormKey == null ? "None" : (LabelOf(x.FormKey) + (x.Alias >= 0 ? $" alias {x.Alias}" : ""));
                                if (x.FormKey == null) { choices.Add(new Choice($"{name} = None → {v.Chain}", null, null, -1, Unset: true, Property: v.Property)); continue; }
                                foreach (var y in ChainValues(x.FormKey, x.Alias, v.Chain, v.ChainType, parents, defaults, views))
                                {
                                    string step = $"{name} = {holder} → {v.Chain}" + (y.Unset ? $" ({y.Text})" : y.Text);
                                    choices.Add(y with { Text = call.Kind == "give" ? step : $"{step} = {(y.Value is { } yv ? yv.ToString() : y.FormKey ?? "None")}", Property = v.Property });
                                }
                            }
                            continue;
                        }
                        int had = choices.Count;
                        foreach (var x in PropertyValues(prop, v.Member))
                            choices.Add(x with { Text = call.Kind == "give" ? name : $"{name}{(x.Element >= 0 && v.Member != null ? $" [{x.Element}]" : "")} = {(x.Value is { } xv ? xv.ToString() : x.FormKey ?? "None")}", Property = v.Property });
                        // A struct that doesn't set the member (or an empty array): the member's default, None
                        if (choices.Count == had)
                            choices.Add(new Choice(call.Kind == "give" ? $"{name} (not set in {(v.Element ? "any element" : "the struct")})" : $"{name} isn't set in {(v.Element ? "any element" : "the struct")} (None)", null, null, -1, Unset: true, Property: v.Property));
                    }
                    else if (v.Constant is { } k) choices.Add(new Choice(v.Text, k, null, -1));
                    else if (v.Kind != ValueOrigin.None && !choices.Any(x => x.Text == v.Text && x.FormKey == null && x.Value == null)) choices.Add(new Choice(v.Text, null, null, -1));
                }
                if (choices.Count == 0) choices.Add(new Choice("", null, null, -1));
                perArg.Add(choices);
            }
            if (call.Kind == "give")
            {
                foreach (var x in perArg[0])
                {
                    var t = new CallTarget { Carrier = carrierText, CarrierFormKey = carrier.FormKey, Via = x.Text, ItemFormKey = x.FormKey, Alias = x.Alias };
                    if (x.FormKey == null) t.NotFound = x.Unset ? "None" : x.Value is { } pv ? pv.ToString() : "decided at run time";
                    call.Targets.Add(t);
                }
                continue;
            }
            var ids = perArg[0];
            var pls = perArg.Count > 1 ? perArg[1] : new List<Choice> { new("", null, null, -1) };
            bool byId = call.Call.EndsWith("GetFormFromFile", StringComparison.OrdinalIgnoreCase) || call.Call.EndsWith("GetForm", StringComparison.OrdinalIgnoreCase);
            foreach (var id in ids)
                foreach (var pl in pls)
                {
                    // Two members of the same array of structs: only those of the same element go together
                    if (id.Property != null && id.Property == pl.Property && id.Element >= 0 && pl.Element >= 0 && id.Element != pl.Element) continue;
                    var t = new CallTarget { Carrier = carrierText, CarrierFormKey = carrier.FormKey, Via = string.Join(", ", new[] { id.Text, pl.Text }.Where(x => x.Length > 0)) };
                    if (call.Kind == "plugin") t.NamedPlugin = id.Value is { Kind: PexValue.String } ps ? ps.Text : null;
                    else if (byId)
                    {
                        t.FormId = id.Value is { Kind: PexValue.Integer } pi ? (uint)pi.Int : null;
                        t.NamedPlugin = pl.Value is { Kind: PexValue.String } ps2 ? ps2.Text : null;
                    }
                    else t.EditorId = id.Value is { Kind: PexValue.String } pe ? pe.Text : null;
                    if (t.FormId == null && t.NamedPlugin == null && t.EditorId == null)
                        t.NotFound = id.Unset || pl.Unset ? (call.Kind == "plugin" ? "no plugin named (the check is false)" : "nothing named (the call gets None)") : "decided at run time";
                    call.Targets.Add(t);
                }
        }
    }

    /// <summary>One value an argument has on one record: as the answer writes it, the value, the record it names (a
    /// quest's alias), and for an array of structs, which element.</summary>
    sealed record Choice(string Text, PexValue? Value, string? FormKey, int Alias, int Element = -1, string? Property = null, bool Unset = false);

    static JsonObject? PropertyOf(JsonObject entry, string name) =>
        (entry["Properties"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(x => x["Name"] is JsonValue n && n.TryGetValue<string>(out var nn) && nn.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>"Aliases[2].Scripts[0]" → "alias 2"; "ScriptFragments.Script" → "its fragments".</summary>
    static string ShortWhere(string where) =>
        where.StartsWith("VirtualMachineAdapter.ScriptFragments", StringComparison.Ordinal) || where == "VirtualMachineAdapter.Script" ? "its fragments' script"
        : Regex.Match(where, @"Aliases\[(\d+)\]") is { Success: true } m ? $"its alias {m.Groups[1].Value}'s script" : where;

    /// <summary>The values a script property holds on a record: a record (with its alias), several (an array), a number,
    /// a text; with <paramref name="member"/>, that member of its struct, or of each struct of its array (with its place).</summary>
    static IEnumerable<Choice> PropertyValues(JsonObject prop, string? member)
    {
        static int AliasOf(JsonObject o) => o["Alias"] is JsonValue a && a.TryGetValue<int>(out var n) ? n : o["Alias"] is JsonValue a2 && a2.TryGetValue<short>(out var s16) ? s16 : -1;
        static string? KeyOf(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && RecordSplit.IsFormKey(s) ? ReportSession.Canon(s) : null;
        // A struct's members: script properties, held in its Members (or in the Properties of each of them)
        static JsonObject? MemberOf(JsonNode? holder, string name)
        {
            if (holder is not JsonObject h || h["Members"] is not JsonArray members) return null;
            foreach (var m in members.OfType<JsonObject>())
            {
                if (m["Properties"] is JsonArray inner)
                {
                    if (inner.OfType<JsonObject>().FirstOrDefault(x => x["Name"] is JsonValue n2 && n2.TryGetValue<string>(out var s2) && s2.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } found) return found;
                }
                else if (m["Name"] is JsonValue n && n.TryGetValue<string>(out var nn) && nn.Equals(name, StringComparison.OrdinalIgnoreCase)) return m;
            }
            return null;
        }
        if (member != null)
        {
            if (prop["Structs"] is JsonArray structs)
            {
                for (int i = 0; i < structs.Count; i++)
                    if (MemberOf(structs[i], member) is { } m)
                        foreach (var x in PropertyValues(m, null)) yield return x with { Element = i };
            }
            else if (MemberOf(prop, member) is { } m)
                foreach (var x in PropertyValues(m, null)) yield return x;
            yield break;
        }
        if (prop.ContainsKey("Object"))
        {
            yield return new Choice("", null, KeyOf(prop["Object"]), AliasOf(prop));
            yield break;
        }
        if (prop["Objects"] is JsonArray objects)
        {
            int i = 0;
            foreach (var o in objects.OfType<JsonObject>()) yield return new Choice("", null, KeyOf(o["Object"]), AliasOf(o), i++);
            yield break;
        }
        var data = prop["Data"];
        IEnumerable<JsonNode?> items = data is JsonArray arr ? arr : new[] { data };
        int k = data is JsonArray ? 0 : -1;
        foreach (var d in items)
        {
            int at = k >= 0 ? k++ : -1;
            if (d is not JsonValue v) continue;
            if (v.TryGetValue<string>(out var text)) yield return new Choice("", new PexValue(PexValue.String, text, 0, 0, false), null, -1, at);
            else if (v.TryGetValue<bool>(out var b)) yield return new Choice("", new PexValue(PexValue.BoolKind, null, 0, 0, b), null, -1, at);
            else if (v.TryGetValue<int>(out var i)) yield return new Choice("", new PexValue(PexValue.Integer, null, i, 0, false), null, -1, at);
            else if (v.TryGetValue<long>(out var l)) yield return new Choice("", new PexValue(PexValue.Integer, null, unchecked((int)l), 0, false), null, -1, at);
            else if (v.TryGetValue<double>(out var f)) yield return new Choice("", new PexValue(PexValue.FloatKind, null, 0, (float)f, false), null, -1, at);
        }
    }

    /// <summary>Every call, its record got in code, and each target: the record it names, or why there is none.</summary>
    void LocateAll(ScriptCall call)
    {
        Locate(call);
        if (call.BuiltBy != null) Locate(call.BuiltBy);
        foreach (var t in call.Targets) Locate(call, t);
    }

    void Locate(ScriptCall call, CallTarget t)
    {
        if (t.Record != null || t.NotFound != null) return;
        if (t.ItemFormKey != null)
        {
            int colon = t.ItemFormKey.IndexOf(':');
            if (colon == 6 && uint.TryParse(t.ItemFormKey[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var local))
                (t.FormKey, t.Record, t.Signature, t.NotFound) = Find(local, t.ItemFormKey[(colon + 1)..]);
            return;
        }
        if (call.Kind == "plugin")
        {
            if (t.NamedPlugin != null && _s.Index.Plugin(t.NamedPlugin) == null) t.NotFound = $"{t.NamedPlugin} isn't in Data";
            else if (t.NamedPlugin != null) t.Record = $"plugin {t.NamedPlugin}";
            return;
        }
        if (t.EditorId != null)
        {
            var info = _s.Index.FindEditorId(t.EditorId).Select(r => _s.Index.Describe(r)).FirstOrDefault(i => !i.Unresolved);
            if (info != null) { t.Record = info.Label; t.Signature = info.Signature; t.FormKey = ReportSession.Canon(info.FormKey); }
            else t.NotFound = "no plugin in Data has a record with this EditorID";
            return;
        }
        if (t.FormId is not uint id) return;
        string? plugin = t.NamedPlugin;
        if (plugin == null)
        {
            if (call.Call.EndsWith("GetFormFromFile", StringComparison.OrdinalIgnoreCase)) { t.NotFound = "no plugin name"; return; }
            if (id >> 24 != 0) { t.NotFound = $"FormID {id:X8} names its plugin by its place in the load order ({id >> 24:X2}), which depends on the player's load order"; return; }
            plugin = "Starfield.esm";
        }
        (t.FormKey, t.Record, t.Signature, t.NotFound) = Find(id & 0xFFFFFF, plugin, id);
    }

    /// <summary>"Foo (001234:x.esm) MISC" for a FormKey, as the game finds the record; the FormKey when it isn't in Data.</summary>
    string LabelOf(string formKey)
    {
        int colon = formKey.IndexOf(':');
        if (colon != 6 || !uint.TryParse(formKey[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var local)) return formKey;
        var (_, record, signature, _) = Find(local, formKey[(colon + 1)..]);
        return record != null ? $"{record} {signature}" : formKey;
    }

    /// <summary>The record a plugin's FormID names as the game finds it in this Data folder (label, signature), or why there is none.</summary>
    (string FormKey, string? Record, string? Signature, string? NotFound) Find(uint local, string plugin, uint? full = null)
    {
        var ip = _s.Index.Plugin(plugin);
        if (ip == null) return ($"{local:X6}:{plugin}", null, null, $"{plugin} isn't in Data");
        // A light plugin's IDs are the low 12 bits: the game reads them so whatever the code puts above
        uint id = full ?? local;
        var hit = _s.Winner(ip.Name, local) ?? ((id & 0xFFF) != local ? _s.Winner(ip.Name, id & 0xFFF) : null);
        if (hit is { } h) return (ReportSession.Canon(h.Info.FormKey), h.Info.Label, h.Info.Signature, null);
        return ($"{local:X6}:{ip.Name}", null, null, "no plugin in Data holds this record");
    }

    /// <summary>The record a call names, as the game would find it in this Data folder; or why there is none.</summary>
    void Locate(ScriptCall c)
    {
        if (c.Record != null || c.NotFound != null) return;
        if (c.Kind == "plugin")
        {
            if (c.NamedPlugin != null && _s.Index.Plugin(c.NamedPlugin) == null) c.NotFound = "not in Data";
            return;
        }
        if (c.Kind != "get") return;
        if (c.EditorId != null)
        {
            var info = _s.Index.FindEditorId(c.EditorId).Select(r => _s.Index.Describe(r)).FirstOrDefault(i => !i.Unresolved);
            if (info != null) { c.Record = info.Label; c.Signature = info.Signature; c.FormKey = ReportSession.Canon(info.FormKey); }
            else c.NotFound = "no plugin in Data has a record with this EditorID";
            return;
        }
        if (c.FormId is not uint id) return;
        string? plugin = c.NamedPlugin;
        if (plugin == null)
        {
            // Game.GetForm takes a FormID with the load-order place in its top byte: only 00 (Starfield.esm) is fixed
            if (id >> 24 != 0) { c.NotFound = $"FormID {id:X8} names its plugin by its place in the load order ({id >> 24:X2}), which depends on the player's load order"; return; }
            plugin = "Starfield.esm";
        }
        uint local = id & 0xFFFFFF;
        var ip = _s.Index.Plugin(plugin);
        if (ip == null) { c.FormKey = $"{local:X6}:{plugin}"; c.NotFound = $"{plugin} isn't in Data"; return; }
        c.FormKey = $"{local:X6}:{ip.Name}";
        // A light plugin's IDs are the low 12 bits: the game reads them so whatever the code puts above
        var hit = _s.Winner(ip.Name, local) ?? ((id & 0xFFF) != local ? _s.Winner(ip.Name, id & 0xFFF) : null);
        if (hit is { } h)
        {
            var info = h.Info;
            c.Record = info.Label; c.Signature = info.Signature; c.FormKey = ReportSession.Canon(info.FormKey);
        }
        else c.NotFound = "no plugin in Data holds this record";
    }

    // ------------------------------------------------------------------ The report
    /// <summary>The scripts of one plugin, or of several (names separated by commas).</summary>
    public ScriptsAnswer Scripts(string plugins, ScriptsOptions? options = null)
    {
        options ??= new ScriptsOptions();
        // Names, patterns and sets (@official) through the one resolver (GAPS S117); a path or a name not in Data stays for Side
        var (names, named) = string.IsNullOrWhiteSpace(plugins) ? (new List<string>(), (string?)null) : PluginSet.Names(_s.Index, plugins);
        if (names.Count == 0) throw new ReportException("no plugin was named", "give a plugin's file name in Data, or the path of a plugin file");
        string mode = options.Show != null ? "show" : options.Find != null ? "find" : options.Calls ? "calls" : "list";
        if (mode == "find" && options.Find!.Length == 0) throw new ReportException("the text to find is empty", "give a text to look for");
        return _s.Run(scope =>
        {
            var sides = names.Select(n => _s.Side(n)).ToList();
            var answer = new ScriptsAnswer { Plugins = sides.Select(s => s.Shown).ToList(), Named = named, Mode = mode, Find = options.Find };
            var all = new List<(PluginSide Side, Sources Sources, FileFinder Finder)>();
            var usesOf = new List<(PluginSide Side, Dictionary<string, List<(string Script, string How)>> Uses, HashSet<string> Own, FileFinder Finder)>();
            var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var propertyDefaults = new Dictionary<string, PexValue>(StringComparer.OrdinalIgnoreCase);
            var code = new List<(PluginSide Side, ScriptCodeResult Result, FileFinder Finder)>();
            var uses = new Dictionary<string, List<(string Script, string How)>>(StringComparer.OrdinalIgnoreCase);
            var taken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int recordsLookedAt = 0, looseLookedFor = 0;
            foreach (var side in sides)
            {
                var own = ArchiveReport.Resolve(_s, side.InData ? side.Name : side.Path, options.Beside);
                foreach (var n in own.Notes) answer.Notes.Add(n);
                var finder = new FileFinder(_s, side.Masters, own);
                // A master holds a loose script when a master's archive holds it, or its path has a master's name
                var masterStems = side.Masters.Select(m => Path.GetFileNameWithoutExtension(m)).Where(m => m.Length >= 4).ToList();
                var c = Collect(own, side.Name, found => NamedInScriptBlocks(side, scope, found, options.World),
                    rel => masterStems.Any(m => rel.Contains(m, StringComparison.OrdinalIgnoreCase)) || finder.FindNonTexture(rel, loose: false, only: FileOrigin.Master).Origin == FileOrigin.Master);
                foreach (var (k, v) in c.Uses) uses.TryAdd(k, v);
                usesOf.Add((side, c.Uses, c.Pex.Select(x => ScriptOfPath(x.Name)).Concat(c.Uses.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase), finder));
                foreach (var rel in c.LooseByRecords) taken.TryAdd(ScriptOfPath(rel), "attached by its records");
                foreach (var (rel, why) in c.LooseByCode) taken.TryAdd(ScriptOfPath(rel), why);
                var sources = Ensure(own, side.Name, c, decompile: mode != "calls");
                all.Add((side, sources, finder));
                answer.Scripts.AddRange(sources.Scripts);
                answer.Decompiled += sources.Decompiled;
                recordsLookedAt += c.RecordsLookedAt; looseLookedFor += c.LooseLookedFor;
                ReportSession.AddSource(answer, side);
                if (c.Pex.Count > 0)
                    answer.Sources.Add($"{Plural(c.Pex.Count, "compiled script")} of {side.Name} ({string.Join(", ", c.Pex.GroupBy(x => x.From, StringComparer.OrdinalIgnoreCase).Select(g => $"{g.Key} {N(g.Count())}"))})" +
                        (mode == "calls" ? "" : $", content key {sources.Key[..8]}, " + (sources.Decompiled > 0 ? "decompiled now" : "sources kept from an earlier call")));
                if (sources.Problem != null) answer.Notes.Add($"{side.Name}: {sources.Problem}.");
                if (c.LooseOverArchived > 0) answer.Notes.Add($"{side.Name}: {Plural(c.LooseOverArchived, "script")} in its archives also {(c.LooseOverArchived == 1 ? "exists" : "exist")} as a loose file under Data\\Scripts. The game uses the loose file, so the loose file is the one {(mode == "calls" ? "read" : "decompiled")}.");
                if (c.LooseByRecords.Count > 0)
                    answer.Notes.Add($"{side.Name}: {Plural(c.LooseByRecords.Count, "loose script")} without \"{Path.GetFileNameWithoutExtension(side.Name)}\" in {(c.LooseByRecords.Count == 1 ? "its path is" : "their paths are")} taken as its own, because its records attach {(c.LooseByRecords.Count == 1 ? "it" : "them")}: {string.Join(", ", c.LooseByRecords.Select(ScriptOfPath))}. Another plugin may attach {(c.LooseByRecords.Count == 1 ? "it" : "them")} too.");
                if (c.LooseByCode.Count > 0)
                    answer.Notes.Add($"{side.Name}: {Plural(c.LooseByCode.Count, "loose script")} without \"{Path.GetFileNameWithoutExtension(side.Name)}\" in {(c.LooseByCode.Count == 1 ? "its path is" : "their paths are")} taken as its own, because its scripts use {(c.LooseByCode.Count == 1 ? "it" : "them")} (the game loads a script's parent and the scripts it calls with it): {string.Join(", ", c.LooseByCode.Select(x => $"{ScriptOfPath(x.Rel)} ({x.Why})"))}. Another plugin may use {(c.LooseByCode.Count == 1 ? "it" : "them")} too.");
                if (c.LooseOfMasters.Count > 0)
                    answer.Notes.Add($"{side.Name}: its records attach {Plural(c.LooseOfMasters.Count, "loose script")} that a master's archive holds too ({string.Join(", ", c.LooseOfMasters.Select(ScriptOfPath))}): the loose file stands in for the master's script, and it is listed with the scripts of the game and masters.");
                if (c.LooseOfMastersByCode.Count > 0)
                    answer.Notes.Add($"{side.Name}: its scripts use {Plural(c.LooseOfMastersByCode.Count, "loose script")} that a master holds ({string.Join(", ", c.LooseOfMastersByCode.Select(x => $"{ScriptOfPath(x.Rel)} ({x.Why})"))}): the master's scripts, not taken as its own.");
                if (c.Pex.Count == 0)
                    answer.Notes.Add($"{side.Name} has no compiled scripts: none in {(own.Archives.Count == 0 ? "archives (it has none)" : string.Join(", ", own.Archives.Select(a => a.Name)))}, no loose file under Data\\Scripts with \"{Path.GetFileNameWithoutExtension(side.Name)}\" in its path, and no other loose script that its records attach.");
                if (mode is "list" or "calls") code.Add((side, ReadCode(side, c, answer, parents, propertyDefaults, finder), finder));
            }
            if (looseLookedFor > 0) answer.Read.Add($"the script blocks of {Plural(recordsLookedAt, "record")}, raw, for {Plural(looseLookedFor, "loose script")} without the plugin's name in {(looseLookedFor == 1 ? "its path" : "their paths")}");

            var entriesOf = new Dictionary<string, Dictionary<string, List<(ScriptCarrier Carrier, JsonObject Entry)>>>(StringComparer.OrdinalIgnoreCase);
            var baseOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            switch (mode)
            {
                case "show":
                {
                    string want = options.Show!.Trim();
                    var hits = answer.Scripts.Where(s => s.Name.Equals(want, StringComparison.OrdinalIgnoreCase) || s.Name.EndsWith(":" + want, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (hits.Count == 0)
                    {
                        var near = answer.Scripts.Where(s => s.Name.Contains(want, StringComparison.OrdinalIgnoreCase)).Take(6).Select(s => s.Name).ToList();
                        throw new ReportException($"{string.Join(", ", answer.Plugins)} {(sides.Count == 1 ? "has" : "have")} no script named \"{want}\" among {(sides.Count == 1 ? "its" : "their")} {Plural(answer.Scripts.Count, "script")}",
                            near.Count > 0 ? $"did you mean {string.Join(", ", near)}" : "list the scripts first, and give a name as shown there");
                    }
                    foreach (var s in hits) answer.Shown.Add((s, System.IO.File.ReadAllText(s.FullPath)));
                    answer.Read.Add($"{Plural(hits.Count, "script source")}");
                    break;
                }
                case "find":
                {
                    string text = options.Find!;
                    foreach (var s in answer.Scripts)
                    {
                        var lines = System.IO.File.ReadAllText(s.FullPath).Replace("\r\n", "\n").Split('\n');
                        answer.LinesLookedAt += lines.Length;
                        for (int i = 0; i < lines.Length; i++)
                            if (lines[i].Contains(text, StringComparison.OrdinalIgnoreCase)) answer.Lines.Add(new ScriptLine(s.Plugin, s.Name, i + 1, lines[i]));
                    }
                    if (answer.Lines.Count == 0)
                        answer.EmptyReason = answer.Scripts.Count == 0 ? $"{string.Join(", ", answer.Plugins)}: no scripts to look in."
                            : $"No line contains \"{text}\": {N(answer.LinesLookedAt)} lines of {Plural(answer.Scripts.Count, "script")} were looked at (letter case doesn't count; the text is taken as it is, with no wildcards).";
                    answer.Read.Add($"{N(answer.LinesLookedAt)} lines of {Plural(answer.Scripts.Count, "script source")}");
                    break;
                }
                case "calls":
                    answer.CodeRead = true;
                    answer.Read.Add($"{Plural(answer.CompiledRead, "compiled script")}, nothing decompiled");
                    break;
                default:
                {
                    answer.CodeRead = true;
                    answer.Read.Add($"{Plural(answer.CompiledRead, "compiled script")} for the code that reaches records");
                    // Which script each extends, and which of the plugin's own scripts extend it
                    var byName = new Dictionary<string, List<ScriptInfo>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var s in answer.Scripts) (byName.TryGetValue(s.Name, out var l) ? l : byName[s.Name] = new()).Add(s);
                    foreach (var s in answer.Scripts)
                    {
                        if (parents.TryGetValue(s.Name, out var parent) && parent.Length > 0)
                        {
                            s.Parent = parent;
                            if (byName.TryGetValue(parent, out var ps)) foreach (var ps1 in ps) if (!ps1.ExtendedBy.Contains(s.Name, StringComparer.OrdinalIgnoreCase)) ps1.ExtendedBy.Add(s.Name);
                        }
                        if (taken.TryGetValue(s.Name, out var why)) s.TakenBecause = why;
                        // The plugin's scripts that call it, or name it as a type
                        if (uses.TryGetValue(s.Name, out var used))
                            foreach (var (other, how) in used)
                                if (byName.TryGetValue(other, out var os))
                                    foreach (var o1 in os)
                                    {
                                        var list = how == "calls" ? o1.CalledFrom : how == "type" ? o1.TypeIn : null;
                                        if (list != null && !list.Contains(s.Name, StringComparer.OrdinalIgnoreCase)) list.Add(s.Name);
                                    }
                    }
                    // The scripts the plugin's scripts use that aren't its own (S9c): each looked for as the game does,
                    // loose first, then its archives, its masters' (the game's own scripts are in Starfield - Misc.ba2),
                    // the other loaded plugins'; one that is nowhere is missing
                    var ownAll = new HashSet<string>(byName.Keys, StringComparer.OrdinalIgnoreCase);
                    foreach (var (_, _, own, _) in usesOf) ownAll.UnionWith(own);
                    foreach (var (side, sideUses, _, finder) in usesOf)
                    {
                        var usedBy = new Dictionary<string, List<(string How, string By)>>(StringComparer.OrdinalIgnoreCase);
                        foreach (var (by, list) in sideUses)
                            foreach (var (script, how) in list)
                                if (!ownAll.Contains(script)) (usedBy.TryGetValue(script, out var l) ? l : usedBy[script] = new()).Add((how, by));
                        answer.UsedLookedFor += usedBy.Count;
                        foreach (var (script, by) in usedBy.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                        {
                            var hit = finder.FindNonTexture(PathOfScript(script));
                            if (hit.Found) continue;
                            answer.MissingUsed.Add(new ScriptUsedMissing(side.Name, script, by.OrderBy(x => x.By, StringComparer.OrdinalIgnoreCase).ToList(),
                                hit.Origin == FileOrigin.NotLoaded ? $"only in {hit.Where}, which the game doesn't load" : null));
                        }
                    }
                    if (answer.UsedLookedFor > 0)
                        answer.Read.Add($"the archives' name tables for {Plural(answer.UsedLookedFor, "script")} the scripts use (their parents, the scripts they call, the types they name) that aren't the plugin's own");
                    if (options.Carriers)
                    {
                        answer.CarriersRead = true;
                        foreach (var (side, _, finder) in all)
                        {
                            var elsewhere = new List<string>();
                            var entries = new Dictionary<string, List<(ScriptCarrier, JsonObject)>>(StringComparer.OrdinalIgnoreCase);
                            entriesOf[side.Name] = entries;
                            foreach (var c in Carriers(side, scope, answer, options.World, entries: entries, baseOf: baseOf))
                            {
                                if (byName.TryGetValue(c.Script, out var targets)) foreach (var t in targets) t.Carriers.Add(c);
                                else { answer.CarriedFromElsewhere.Add(c); elsewhere.Add(c.Script); }
                            }
                            // Where the game finds each of the others: a loose file, an archive of a master or another mod; or nowhere
                            foreach (var script in elsewhere.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                            {
                                var hit = finder.FindNonTexture(PathOfScript(script));
                                answer.Elsewhere.Add(hit.Found ? new ScriptElsewhere(side.Name, script, hit.Where, null)
                                    : new ScriptElsewhere(side.Name, script, null, hit.Origin == FileOrigin.NotLoaded ? $"only in {hit.Where}, which the game doesn't load" : null));
                            }
                            if (elsewhere.Count > 0) answer.Sources.Add($"{side.Name}: " + finder.SourceLine().Replace("files looked for", "scripts of the game and other plugins looked for") + " (texture archives passed over)");
                        }
                        if (answer.WorldLeftOut > 0)
                            answer.Notes.Add(SFSearch.Records.WorldData.LeftOut("was left out when the records were read for scripts", answer.WorldLeftOut, noIndex: "scripts are read from each record") + $" A placed object can carry a script. A plugin of more than {N(WorldLimit)} records is read without them unless world is asked for.");
                        answer.Read.Add($"{Plural(answer.RecordsWithScripts, "record")} with a script block ({string.Join(", ", _scriptSubrecords.OrderBy(x => x, StringComparer.Ordinal))}), found among the {N(answer.RecordsScanned)} looked at");
                    }
                    if (answer.Scripts.Count == 0 && answer.CarriedFromElsewhere.Count == 0)
                        answer.EmptyReason = $"{string.Join(", ", answer.Plugins)}: no scripts in {(sides.Count == 1 ? "its" : "their")} archives or loose under Data\\Scripts" + (answer.CarriersRead ? $", and none of the {N(answer.RecordsScanned)} records looked at carries a script." : ".");
                    break;
                }
            }
            // The values the code takes from script properties, on the records that attach the script (S9a). The list
            // has read every record's scripts; the calls read only the records whose script block names those scripts.
            int carrierRecords = 0;
            var views = new Dictionary<string, RecordView?>(StringComparer.OrdinalIgnoreCase);
            var widened = new Widened();
            foreach (var (side, result, finder) in code)
            {
                var needs = result.Calls.Where(x => x.PropertyScripts.Any()).ToList();
                if (needs.Count > 0 && (mode == "calls" || options.Carriers))
                {
                    var names = needs.SelectMany(x => x.PropertyScripts).SelectMany(x => SameOrChild(x, parents)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var entries = new Dictionary<string, List<(ScriptCarrier Carrier, JsonObject Entry)>>(StringComparer.OrdinalIgnoreCase);
                    if (entriesOf.TryGetValue(side.Name, out var listed))
                    {
                        foreach (var n in names) if (listed.TryGetValue(n, out var l)) entries[n] = l.Select(x => ((ScriptCarrier)x.Item1, (JsonObject)x.Item2)).ToList();
                        // The list leaves a large plugin's world data out: its placed references are looked in for the
                        // scripts no other record of it attaches
                        var unattached = names.Where(n => !entries.ContainsKey(n)).ToList();
                        if (answer.WorldLeftOut > 0 && unattached.Count > 0 && options.World == null && WorldFor(side, null, widened))
                        {
                            var more = new Dictionary<string, List<(ScriptCarrier Carrier, JsonObject Entry)>>(StringComparer.OrdinalIgnoreCase);
                            widened.WorldRecords += Carriers(side, scope, answer, true, unattached, more, baseOf, CarrierTypes(unattached, parents)).Select(x => x.FormKey).Distinct().Count();
                            foreach (var (n, l) in more) entries[n] = l;
                        }
                    }
                    else carrierRecords += Carriers(side, scope, answer, WorldFor(side, options.World, widened), names, entries, baseOf, CarrierTypes(names, parents)).Select(x => x.FormKey).Distinct().Count();
                    Widen(side, scope, answer, names, entries, baseOf, finder, views, widened, parents);
                    foreach (var call in needs) JoinProperties(call, side.Name, parents, entries, propertyDefaults, baseOf, views, widened);
                }
                else foreach (var call in needs) call.Via = (call.Via is { Length: > 0 } v0 ? v0 + "; " : "") + "a script property: the records that attach the script weren't read";
                foreach (var call in result.Calls) LocateAll(call);
            }
            if (carrierRecords > 0) answer.Read.Add($"{Plural(carrierRecords, "record")} that {(carrierRecords == 1 ? "attaches" : "attach")} the scripts whose properties the code uses, for the properties' values");
            if (widened.WorldRecords > 0) answer.Read.Add($"{Plural(widened.WorldRecords, "placed reference")} (world data, looked in for the scripts no other record attaches) that attach the scripts whose properties the code uses");
            if (widened.Dependents > 0) answer.Read.Add($"the script blocks of the {(widened.Dependents == 1 ? "plugin that has" : N(widened.Dependents) + " plugins that have")} {string.Join(", ", answer.Plugins)} as a master, raw, for the scripts whose properties the code uses: {Plural(widened.DependentRecords, "record")} {(widened.DependentRecords == 1 ? "attaches" : "attach")} them");
            if (widened.Overridden + widened.Removed > 0) answer.Read.Add($"{Plural(widened.Overridden + widened.Removed, "winning override")} of the records that attach those scripts (the version the game uses)");
            if (widened.MastersLooked.Count > 0) answer.Read.Add($"the script blocks of {string.Join(", ", widened.MastersLooked)}, raw, for the copies of its scripts: {Plural(widened.MasterRecords, "record")} {(widened.MasterRecords == 1 ? "attaches" : "attach")} them");
            if (widened.Removed > 0) answer.Notes.Add($"{Plural(widened.Removed, "record")} that {(widened.Removed == 1 ? "attaches" : "attach")} a script whose properties the code uses {(widened.Removed == 1 ? "is" : "are")} overridden by a later plugin that doesn't attach it: the game doesn't run it there, so {(widened.Removed == 1 ? "its values are" : "their values are")} left out: {string.Join("; ", widened.RemovedBy.Distinct().Take(5))}{(widened.RemovedBy.Distinct().Count() > 5 ? "; …" : "")}.");
            if (answer.CompiledUnread.Count > 0)
                answer.Notes.Add($"{Plural(answer.CompiledUnread.Count, "compiled script")} couldn't be read for {(answer.CompiledUnread.Count == 1 ? "its" : "their")} code: " + string.Join("; ", answer.CompiledUnread.Take(5).Select(u => $"{u.Pex} ({u.Why})")) + (answer.CompiledUnread.Count > 5 ? "; …" : "") + ".");
            if (answer.CodeRead && answer.Calls.Count > 0)
                answer.Notes.Add("The code is read from the compiled scripts, function by function along its branches: a record the code gets with GetFormFromFile is followed through variables to the AddItem, AddForm, PlaceAtMe … that hands it out, also through a script variable set in another function, and an if whose paths set different constants gives each. " +
                    "A parameter is followed to the calls of its function in the plugin's scripts, and when none calls it, in the scripts of the plugins that have it as a master (and its masters', for a copy of a master's script); what a call of the plugin's own function returns is followed into that function; each up to 3 calls deep. " +
                    "A value held in a script property is read on the records that attach the script, as the load order's winner of each sets it: the plugin's records, those of the plugins that have it as a master, and for a copy of a master's script, the masters'. A property of the record a property holds (workerContainer of property core) is read on that record. " +
                    "What the game's own functions return (GetValue, GetAt …) and what scripts outside those plugins pass in stay decided at run time.");
            if (answer.Decompiled > 0) answer.Read.Add($"{Plural(answer.Decompiled, "script")} decompiled with Champollion");
            if (mode != "calls") answer.Sources.Add($"script sources kept in {_s.Kept("scripts")}");
            foreach (var (side, _, _) in all) ReportSession.AddProblemNotes(answer, side.Plugin);
            return answer;
        });
    }
}
