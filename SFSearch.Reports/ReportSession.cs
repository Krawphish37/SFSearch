// What every report stands on: the comparison session's header index and record engine over the Data folder (and
// one more index and engine for each plugin file outside Data), and the few lookups the reports share: the version
// of a record a plugin sees, the version the game uses, which records are world data, a text in one language.
using System.Diagnostics;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Strings;
using SFSearch.Compare;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Reports;

/// <summary>One record of one plugin, with the engine that reads that plugin.</summary>
public readonly record struct Hit(RecordEngine Engine, RecordRef Ref)
{
    public IndexedPlugin Plugin => Engine.Index.Plugins[Ref.Plugin];
    public Entry Entry => Plugin.Segment![Ref.Entry];
    public string Signature => Plugin.Segment!.SignatureOf(Entry);
    public RecordInfo Info => Engine.Index.Describe(Ref);
}

/// <summary>The plugins a command read, each checked before the first read and again at the end.</summary>
public sealed class ReadScope
{
    readonly List<(RecordEngine Engine, SessionUse Use)> _uses = new();
    readonly HashSet<(RecordEngine, string)> _seen = new();

    /// <summary>Call before reading a record of a plugin: its stamp (and its masters') is checked once per command.</summary>
    public void Touch(RecordEngine engine, string plugin)
    {
        if (!_seen.Add((engine, plugin.ToLowerInvariant()))) return;
        try { _uses.Add((engine, engine.Sessions.Use(plugin))); }
        catch (RecordException e) { throw new ReportException(e.Message); }
    }

    /// <summary>At the end: a plugin that changed while it was read makes the answer fail.</summary>
    public void Confirm(ReportAnswer answer)
    {
        foreach (var (engine, use) in _uses)
        {
            try { engine.Sessions.Confirm(use); }
            catch (RecordException e) { throw new ReportException(e.Message, "run the command again"); }
            foreach (var n in use.Notes) if (!answer.Notes.Contains(n)) answer.Notes.Add(n);
        }
    }
}

public sealed class ReportSession : IDisposable
{
    public CompareSession Compare { get; }
    public HeaderIndex Index => Compare.Index;
    public RecordEngine Engine => Compare.Engine;
    public RecordSchema Schema => Compare.Schema;
    public LinkRules Links { get; }
    public string DataFolder => Compare.DataFolder;
    /// <summary>The archives and loose files of the Data folder, for the reports about files.</summary>
    public ArchiveStore Archives { get; }
    /// <summary>Where the small kept files go (decompiled scripts, the history log): a folder outside the Data folder
    /// and outside the repo. They hold no record contents. Null: a report that needs one says so.</summary>
    public string? KeptFolder { get; set; }
    /// <summary>Where Champollion.exe (the Papyrus decompiler) is, for the scripts report. Null: not found.</summary>
    public string? Champollion { get; set; }
    /// <summary>The language texts are given in unless a report is asked for another.</summary>
    public string Language => Compare.Language.ToString();

    /// <param name="scratchFolder">Where index segments and copies go. Never the Data folder or a folder inside it.</param>
    public ReportSession(string dataFolder, string scratchFolder, RecordSchema schema, string? versionText = null, Language language = Mutagen.Bethesda.Strings.Language.English)
    {
        Compare = new CompareSession(dataFolder, scratchFolder, schema, versionText ?? $"sfsreport / mutagen {schema.MutagenVersion} / schema {schema.Version}", language);
        Links = new LinkRules(schema);
        Archives = new ArchiveStore(Compare.DataFolder);
    }

    public void Dispose() => Compare.Dispose();

    // ------------------------------------------------------------------ Plugins
    /// <summary>A plugin by its name in Data, or a file by its path (read with its masters taken from Data).</summary>
    public PluginSide Side(string nameOrPath)
    {
        try { return Compare.Side(nameOrPath); }
        catch (CompareException e) { throw new ReportException(e.Message, e.Fix); }
    }

    /// <summary>The usual frame of a report: look at the Data folder again, run, check the stamps again, add the cost.</summary>
    public T Run<T>(Func<ReadScope, T> work) where T : ReportAnswer
    {
        var clock = Stopwatch.StartNew();
        Index.Refresh();
        Archives.Begin();
        var scope = new ReadScope();
        T answer;
        try { answer = work(scope); }
        catch (RecordException e) { throw new ReportException(e.Message); }
        catch (CompareException e) { throw new ReportException(e.Message, e.Fix); }
        catch (IOException e) { throw new ReportException(e.Message, FileFix); }
        catch (UnauthorizedAccessException e) { throw new ReportException(e.Message, FileFix); }
        scope.Confirm(answer);
        answer.Seconds = clock.Elapsed.TotalSeconds;
        return answer;
    }

    /// <summary>The frame of a report that reads files only (archives, loose files): the header index isn't looked at.</summary>
    public T RunFiles<T>(Func<T> work) where T : ReportAnswer
    {
        var clock = Stopwatch.StartNew();
        Archives.Begin();
        T answer;
        try { answer = work(); }
        catch (IOException e) { throw new ReportException(e.Message, FileFix); }
        catch (UnauthorizedAccessException e) { throw new ReportException(e.Message, FileFix); }
        answer.Seconds = clock.Elapsed.TotalSeconds;
        return answer;
    }

    const string FileFix = "check that the file is there and can be read or written (another program may hold it), then ask again";

    /// <summary>The game's folder a path is inside (the Data folder, or the folder the game is installed in), or null.
    /// Nothing is ever written there.</summary>
    public string? ReadOnlyFolderOf(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        string data = Path.GetFullPath(DataFolder).TrimEnd('\\');
        if (full.StartsWith(data + "\\", StringComparison.OrdinalIgnoreCase)) return data;
        string? game = Path.GetDirectoryName(data);
        if (game != null && Path.GetFileName(data).Equals("Data", StringComparison.OrdinalIgnoreCase) && Path.GetPathRoot(game) != game
            && full.StartsWith(game.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return game;
        return null;
    }

    /// <summary>A folder for one kind of kept file under the kept folder. Refused when no kept folder was named, or
    /// when it is inside the game's folders.</summary>
    public string Kept(string kind)
    {
        if (string.IsNullOrWhiteSpace(KeptFolder)) throw new ReportException("no folder for kept files was named", "name one (the runner's --kept, or SFSREPORT_KEPT): a folder outside the game's folders");
        string folder = Path.Combine(Path.GetFullPath(KeptFolder), kind);
        if (ReadOnlyFolderOf(folder) is { } game) throw new ReportException($"the folder for kept files, {KeptFolder}, is inside {game}, and nothing is ever written to the game's folders", "name a folder outside them");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static void AddSource(ReportAnswer answer, IndexedPlugin p, string? path = null)
    {
        string line = CompareSession.SourceLine(p, path);
        if (!answer.Sources.Contains(line)) answer.Sources.Add(line);
    }

    public static void AddSource(ReportAnswer answer, PluginSide side) => AddSource(answer, side.Plugin, side.InData ? null : side.Path);

    /// <summary>What the header index knows is wrong with a plugin, as notes.</summary>
    /// <param name="missingMasters">False when the report itself says that a master is missing: the note isn't said twice.</param>
    public static void AddProblemNotes(ReportAnswer answer, IndexedPlugin p, bool missingMasters = true)
    {
        if (!missingMasters && p.Problems.MissingMasters.Count > 0 && CompareSession.ProblemNote != null) return;
        foreach (string note in CompareSession.ProblemNotes(null, p, p.Name))
            if (!answer.Notes.Contains(note)) answer.Notes.Add(note);
    }

    // ------------------------------------------------------------------ Records
    /// <summary>The entry of a FormKey in one plugin, or null.</summary>
    public static Hit? In(RecordEngine engine, string plugin, string owner, uint localId) =>
        CompareSession.Find(engine.Index, plugin, owner, localId) is { } r ? new Hit(engine, r) : null;

    /// <summary>The version of a record a plugin sees: its own, else the last of its masters (in the order its header
    /// lists them) that holds it. Null when neither the plugin nor a master holds it.</summary>
    public Hit? Seen(PluginSide side, string owner, uint localId, bool mastersOnly = false)
    {
        if (!mastersOnly && In(side.Engine, side.Name, owner, localId) is { } own) return own;
        var masters = side.Masters;
        for (int i = masters.Count - 1; i >= 0; i--)
            if (In(Compare.EngineFor(side, masters[i]), masters[i], owner, localId) is { } hit) return hit;
        return null;
    }

    /// <summary>The version the game uses: the last loaded plugin in Data that holds it; when no loaded plugin does, the first holder.</summary>
    public Hit? Winner(string owner, uint localId)
    {
        var v = Index.Versions(owner, localId);
        if (v == null) return null;
        return new Hit(Engine, v.Winner >= 0 ? v.Holders[v.Winner] : v.Holders[0]);
    }

    public Hit? Winner(string formKey) => HeaderIndex.TryParseFormKey(formKey, out var owner, out var id) ? Winner(owner, id) : null;

    /// <summary>
    /// The record a caller names: a FormKey (0019F5:Starfield.esm), an EditorID, or a bare ID that only one plugin
    /// has. With a plugin given, the record is looked for in that plugin. Without, the version the game uses is
    /// given. A name that fits several records lists them and picks none.
    /// </summary>
    public Hit Resolve(string text, PluginSide? side, string what = "record")
    {
        text = text.Trim().Trim('"');
        if (text.Length == 0) throw new ReportException($"no {what} was named", "give a FormKey (0019F5:Starfield.esm) or an EditorID");
        // A bare ID is six hex digits (a local ID) or eight (a FormID as a file writes it). Anything else is an
        // EditorID: quests are named CF03 and FC01, which are hex digits too.
        bool bareId = text.Length is 6 or 8 && text.All(Uri.IsHexDigit);
        if (side != null)
        {
            if (!bareId && !HeaderIndex.TryParseFormKey(text, out _, out _))
            {
                var hits = new List<int>();
                side.Segment.FindEditorId(text, hits);
                if (hits.Count == 1) return new Hit(side.Engine, new RecordRef(side.Plugin.Id, hits[0]));
                if (hits.Count == 0) throw new ReportException($"{side.Shown} has no {what} with the EditorID \"{text}\"", "give a FormKey (0019F5:Starfield.esm) or an EditorID of a record that plugin holds, or leave the plugin out to take the version that wins in the load order");
                throw new ReportException($"\"{text}\" fits {hits.Count} records of {side.Name}: {string.Join("; ", hits.Take(8).Select(h => { var d = side.Index.Describe(new RecordRef(side.Plugin.Id, h)); return $"{d.Label} {d.Signature}"; }))}{(hits.Count > 8 ? "; …" : "")}", "name one by its FormKey");
            }
            try { return new Hit(side.Engine, side.Engine.Find(side.Name, text)); }
            catch (RecordException e) { throw new ReportException(e.Message, "name a record that plugin holds, or leave the plugin out to take the version that wins in the load order"); }
        }
        if (HeaderIndex.TryParseFormKey(text, out var owner, out var id))
        {
            if (Index.Plugin(owner) == null) throw new ReportException($"there is no plugin named {owner} in the Data folder", "check the plugin's name in the FormKey");
            return Winner(owner, id) ?? throw new ReportException($"no plugin in Data holds a record {id:X6}:{Index.Plugin(owner)!.Name}", "check the ID, or find the record by its EditorID");
        }
        var refs = Index.FindEditorId(text);
        // Six hex digits can be a local ID as well as an EditorID: every record either way is a hit, and none is picked
        if (text.Length == 6 && bareId) refs.AddRange(Index.FindLocalId(Convert.ToUInt32(text, 16)));
        var keys = refs.Select(r => Index.Describe(r)).Where(i => !i.Unresolved).GroupBy(i => i.FormKey, StringComparer.OrdinalIgnoreCase).ToList();
        if (keys.Count == 0) throw new ReportException($"no plugin in Data holds a {what} named \"{text}\"", "give a FormKey (0019F5:Starfield.esm) or an EditorID as the plugin spells it");
        if (keys.Count > 1)
            throw new ReportException($"\"{text}\" fits {keys.Count} records: {string.Join("; ", keys.Take(8).Select(g => $"{g.First().Label} {g.First().Signature}"))}{(keys.Count > 8 ? "; …" : "")}", "name one by its FormKey");
        return Winner(keys[0].Key) ?? throw new ReportException($"{keys[0].First().Label} couldn't be found again", "run the command again");
    }

    /// <summary>Reads one record and gives it in the one record form.</summary>
    public RecordView View(ReadScope scope, Hit hit)
    {
        scope.Touch(hit.Engine, hit.Plugin.Name);
        try { return Compare.View(hit.Engine, hit.Ref); }
        catch (CompareException e) { throw new ReportException(e.Message, e.Fix); }
    }

    /// <summary>"0003C2:darkstar.esm": six upper-case digits, then the plugin.</summary>
    public static string Canon(string formKey) =>
        HeaderIndex.TryParseFormKey(formKey, out var plugin, out var id) ? $"{id:X6}:{plugin}" : formKey;

    // ------------------------------------------------------------------ World data
    static readonly HashSet<string> WorldSignatures = new(StringComparer.Ordinal) { "CELL", "WRLD", "NAVI", "SFTR", "SFBK", "SFPT" };

    /// <summary>
    /// True for world data: cells, worldspaces and everything nested in them (placed references, navmeshes, terrain),
    /// the navmesh info map, and the surface trees, blocks and patterns. These are the groups the design leaves out
    /// of everyday reads: in Starfield.esm they are about 3.4 of 3.8 million records.
    /// </summary>
    public static bool IsWorld(Segment seg, in Entry e)
    {
        if (WorldSignatures.Contains(seg.SignatureOf(e))) return true;
        // A record nested in a cell or a worldspace. (Topics and scenes are nested in quests, responses in topics.)
        if (!seg.TryGetParent(e, out int owner, out uint localId)) return false;
        int at = seg.Find(owner, localId);
        return at < 0 || seg.SignatureOf(seg[at]) is "CELL" or "WRLD";
    }

    // ------------------------------------------------------------------ Values
    /// <summary>The text of a translated field in one language, found by the language's name.</summary>
    public static string? Text(JsonNode? node, string language)
    {
        if (node is JsonValue v) return v.TryGetValue<string>(out var s) ? s : v.ToJsonString();
        if (node is not JsonObject o) return null;
        if (o["Values"] is JsonArray values)
        {
            string? target = o["TargetLanguage"] is JsonValue t && t.TryGetValue<string>(out var tl) ? tl : null;
            string? found = null, inTarget = null, first = null;
            foreach (var x in values)
            {
                if (x is not JsonObject e) continue;
                string lang = e["Language"]?.GetValue<string>() ?? "";
                string text = e["String"]?.GetValue<string>() ?? "";
                first ??= text;
                if (found == null && lang.Equals(language, StringComparison.OrdinalIgnoreCase)) found = text;
                if (inTarget == null && target != null && lang.Equals(target, StringComparison.OrdinalIgnoreCase)) inTarget = text;
            }
            // A text held in one language only is that text for everyone
            return found ?? (values.Count == 1 ? first : inTarget ?? first);
        }
        return o.ContainsKey("Value") ? o["Value"]?.GetValue<string>() ?? "" : null;
    }

    /// <summary>A field of an object as the JSON has it, or its default where the schema says the field is always there.</summary>
    public JsonNode? Value(JsonObject o, string objectType, string name)
    {
        if (o.TryGetPropertyValue(name, out var v)) return v;
        var prop = Schema.Prop(objectType, name);
        return prop is { Main.Nullable: false } ? FieldLookup.DefaultOf(Schema, prop.Main).Json : null;
    }

    public static string? Str(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : null;

    public static bool IsLink(JsonNode? n, out string formKey)
    {
        formKey = "";
        if (n is JsonValue v && v.TryGetValue<string>(out var s) && RecordSplit.IsFormKey(s)) { formKey = s; return true; }
        return false;
    }
}
