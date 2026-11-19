using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SFSearch.Mcp;

/// <summary>
/// The 12 tools of the agent-first design (design\agent-first\ARCHITECTURE.md section 7, milestone M8). Each sends
/// the service a tool name and named arguments (never a command line) and renders the structured answer under the
/// output contract: the answer, one [more] line with one cursor, [notes], [source], [cost]; an error is one line,
/// "Error: … Fix: …". Arguments are plain values: FormIDs stay text and SQL can span lines.
/// (The 25 older tools and --legacy-tools were removed with the older commands in SFSearch 2.0.)
///
/// Every session loads these definitions, so they are kept short: the whole tools/list answer stays under 15,000
/// characters ('sfsharness mcp describe' counts it). What every tool shares (cursor, limit, rows, max_chars,
/// language, format) is said once, in the server's instructions, and has no description of its own; allow_heavy takes
/// true, false or "save_index" (J33), which its few words say, as its schema has no one type.
/// An argument left out is an empty text, 0 or false here: it isn't sent to the service. Each verb takes more
/// options than its tool lists; the rest are reached through sfs_run (["refs2", "--help"]).
/// </summary>
[McpServerToolType]
public sealed class NewTools
{
    public const string Instructions =
        "SFSearch reads Starfield plugins (.esm) live from the game's Data folder: no record data is stored, so nothing is stale. " +
        "It keeps only indexes made from the plugin files (each plugin's part named by its content, so a changed plugin's part is never read); sfs_run [\"indexes\"] tells the user what is kept, its size, what it speeds up and how to drop it, and keepStartupIndexes in sfsearch.json turns keeping them off. " +
        "The world link index is made only when the user asks (sfs_run [\"indexes\", \"build\", \"world\"], or a \"save_index\" answer): sfs_refs then answers links from cells, placed objects and navmeshes in a fraction of a second instead of searching gigabytes. " +
        "A record is written EditorID (FormID:plugin); name one by EditorID or FormKey (0476C4:Starfield.esm). The same six hex digits are different records in different plugins, so name the plugin. " +
        "Start with sfs_find (what is this?), sfs_record (one record), sfs_describe (field names: never guess them), then sfs_query (SQL). " +
        "Every answer ends with [notes] (warnings: read them), [source] (which copy of which plugins) and [cost]. " +
        "A [more] line gives a cursor: repeat the call with the same arguments and cursor set. " +
        "Heavy work runs without asking; its cost is in [cost]. Where the user turned asking on, \"Not run\" states the cost; it, and a line about what was left out to save time, give three answers: allow_heavy=true runs it now; leaving it out runs nothing; " +
        "allow_heavy=\"save_index\" (sfs_refs: world \"save_index\") runs it and then saves the index the answer offers, with its size, the time to make it and how fast later questions of that kind get; it is made in the background and kept (sfs_run [\"indexes\"] shows it, [\"indexes\", \"drop\", \"world\"] deletes it). " +
        "Where no index would help, the answer says so. Saving one is the user's choice: tell them the options and costs. " +
        "Shared arguments: limit or rows (rows a page, default 40), max_chars (characters a page, default 24000), language (default English), format=json (the answer's parts as fields).";

    /// <summary>allow_heavy and sfs_refs' world (J33): true, false or "save_index" (the third answer: yes, and save the index
    /// that makes the question fast). The value to send (null: leave it out; false is kept for world, where it means
    /// never), and whether it is a yes.</summary>
    static (object? Value, bool Yes) Answer(object? given, bool keepFalse = false)
    {
        string? text = given switch
        {
            null => null,
            bool b => b ? "true" : "false",
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.True } => "true",
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.False } => "false",
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } e => e.GetString(),
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined } => null,
            _ => given.ToString(),
        };
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return (null, false);
        if (text.Equals("save_index", StringComparison.OrdinalIgnoreCase) || text.Equals("save-index", StringComparison.OrdinalIgnoreCase)) return ("save_index", true);
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text is "1" || text.Equals("yes", StringComparison.OrdinalIgnoreCase)) return (true, true);
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text is "0" || text.Equals("no", StringComparison.OrdinalIgnoreCase)) return (keepFalse ? "false" : null, false);
        return (text, false); // the tool says what it takes
    }

    const string ThreeAnswers = "true, false or \"save_index\"";

    static Dictionary<string, object?> P(params (string Name, object? Value)[] given)
    {
        var p = new Dictionary<string, object?>();
        foreach (var (name, value) in given)
        {
            if (value == null || value is string { Length: 0 } || value is false || value is 0 || value is string[] { Length: 0 }) continue;
            p[name] = value;
        }
        return p;
    }

    [McpServerTool(Name = "sfs_find", ReadOnly = true), Description(
        "What a FormID, FormKey, EditorID or in-game name is; records by pattern or type. Rows: record, type, name, the plugins that hold it (winner first). " +
        "A bare FormID that fits several plugins lists them all and picks none. " +
        "text: a regex held against every name, description, book text and dialogue line, stored or in strings files (language).")]
    public static Task<CallToolResult> sfs_find(
        [Description("FormKeys, FormIDs, EditorIDs, words of a name (cells and worldspaces too), or a pattern (*, ?)")] string query = "",
        [Description("WEAP or Weapon")] string type = "",
        [Description("Only these plugins: names (wildcards), a set (@official, @loaded, @all) or a file path")] string @in = "",
        [Description("A regex for text, e.g. [^\\x00-\\x7F] (outside ASCII); used instead of query")] string text = "",
        [Description("text: stored, strings or both (default)")] string from = "",
        [Description("text: placed references too (map markers' names)")] bool world = false,
        [Description("text: EditorIDs, scripts too")] bool plain = false,
        int limit = 0, string language = "", string cursor = "", string format = "",
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default) =>
        Bridge.Tool("sfs_find", P(("query", query), ("type", type), ("in", @in), ("text", text), ("from", from), ("world", world), ("plain", plain), ("language", language), ("limit", limit)), cursor, format, null, false, progress, ct);

    [McpServerTool(Name = "sfs_record", ReadOnly = true), Description(
        "One record, read live. view: card (default: the set fields, links labelled; long lists and objects closed to a line), full (the card with all of them open), fields, json, raw (the file's bytes), versions (every plugin that holds it), evidence, verify. " +
        "Says which plugin's version was read.")]
    public static Task<CallToolResult> sfs_record(
        [Description("FormKey, EditorID, or FormID with plugin; a plugin's name is its header (TES4)")] string record,
        [Description("Whose version (default: the winner's), or the path of a plugin file outside Data")] string plugin = "",
        string view = "",
        [Description("fields, evidence: Mutagen paths (AttackDamage, Keywords[0])")] string fields = "",
        string language = "", int max_chars = 0, string cursor = "", [Description(ThreeAnswers)] object? allow_heavy = null, string format = "",
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
    {
        var heavy = Answer(allow_heavy);
        return Bridge.Tool("sfs_record", P(("record", record), ("plugin", plugin), ("view", view), ("fields", fields), ("max_chars", max_chars), ("language", language), ("allow_heavy", heavy.Value)), cursor, format, max_chars, heavy.Yes, progress, ct);
    }

    [McpServerTool(Name = "sfs_describe", ReadOnly = true), Description(
        "The schema, no plugin read: a record type's fields, one field, an enum, or a search of field names. " +
        "Each field: its Mutagen path (the name SQL and sfs_record use), file address, xEdit name, type, link target. A wrong name gets the nearest real ones.")]
    public static Task<CallToolResult> sfs_describe(
        [Description("WEAP, Weapon, Weapon.AttackDamage, WEAP/WDMG, \"Base Damage\", an enum, or a word")] string what,
        [Description("Only fields that hold this text")] string filter = "",
        string cursor = "", string format = "",
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default) =>
        Bridge.Tool("sfs_describe", P(("what", what), ("filter", filter)), cursor, format, null, false, progress, ct);

    [McpServerTool(Name = "sfs_query", ReadOnly = true), Description(
        "SQL (SQLite) over typed views, read live. A view per record type (Weapon: FormKey, EditorID, Plugin, IsOverride, a column per plain field, Json) and per list (Weapon_Keywords: FormKey, Idx, Value); " +
        "ObjectModification spans all OMOD types; Records; Forms (every record's header: Sig, Type, IsWinner); Links (FromKey, Field, ToKey); Conditions (each CTDA: Function, Param1); Plugins (each plugin's header: Size, Flags, NumRecords, Masters); schema.Fields. " +
        "Functions: edid(fk), name(fk), winner(fk), type(fk), formkey(text), text(value,'de'). " +
        "Links are FormKeys: compare with a FormKey or edid(col), never an EditorID in Json. Column names: sfs_describe.")]
    public static Task<CallToolResult> sfs_query(
        [Description("May span lines; the last SELECT is the answer")] string sql,
        [Description("Names, comma separated (wildcards), a set (@official, @loaded, @all; @winners: the winners scope), or one file path")] string plugins = "",
        [Description("plugins, winners (default with no plugins) or versions")] string scope = "",
        int rows = 0, string language = "", string cursor = "", [Description(ThreeAnswers)] object? allow_heavy = null, string format = "",
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
    {
        var heavy = Answer(allow_heavy);
        return Bridge.Tool("sfs_query", P(("sql", sql), ("plugins", plugins), ("scope", scope), ("rows", rows), ("language", language), ("allow_heavy", heavy.Value)), cursor, format, null, heavy.Yes, progress, ct);
    }

    [McpServerTool(Name = "sfs_refs", ReadOnly = true), Description(
        "What points at a record (direction in, the default), what it points at (out), or both. Says what was searched. World data (cells, placed references) is searched when small or indexed; else the answer says it was left out, and what world true or \"save_index\" would cost. How the player gets an item: sfs_run sources2.")]
    public static Task<CallToolResult> sfs_refs(
        string record,
        [Description("out: whose version")] string plugin = "",
        string direction = "",
        [Description("Only these plugins: names (wildcards) or a set (@official, @loaded, @all)")] string @in = "",
        [Description("Only these record types")] string types = "",
        [Description("true: always; false: never; \"save_index\": now, then save its world link index")] object? world = null, int limit = 0, string cursor = "", string format = "",
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default) =>
        Bridge.Tool("sfs_refs", P(("record", record), ("plugin", plugin), ("direction", direction), ("in", @in), ("types", types), ("world", Answer(world, keepFalse: true).Value), ("limit", limit)), cursor, format, null, false, progress, ct);

    [McpServerTool(Name = "sfs_compare", ReadOnly = true), Description(
        "kind: versions (record: one record across the load order, who wins, what each changes); records (a, b: two FormKeys, or two plugins with record); " +
        "plugin_vs_masters (a: what it changes in its masters; ITMs listed); plugins (a, b: two versions of a mod, or a plugin and its patch); " +
        "patch_vs_reference (a against b: two patches of the same masters).")]
    public static Task<CallToolResult> sfs_compare(
        string kind,
        [Description("Plugin (name or path) or FormKey")] string a = "",
        string b = "", string record = "",
        [Description("records: the record in b")] string record_b = "",
        [Description("records: fields allowed to differ")] string expect = "",
        [Description("editorid: pair own records by EditorID")] string match = "",
        [Description("plugins: read a under this plugin name")] string @as = "",
        [Description("plugin_vs_masters: EditorID text, FormKey or type")] string filter = "",
        [Description("ITMs as SF1Edit counts them")] bool like_xedit = false,
        bool detail = false, string cursor = "", string format = "",
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default) =>
        Bridge.Tool("sfs_compare", P(("kind", kind), ("a", a), ("b", b), ("record", record), ("record_b", record_b), ("expect", expect), ("match", match), ("as", @as), ("filter", filter), ("like_xedit", like_xedit), ("detail", detail)), cursor, format, null, false, progress, ct);

    [McpServerTool(Name = "sfs_loadorder", ReadOnly = true), Description(
        "view: order (default: slot, size, records, problems of every loaded plugin), plugin (its masters, records per type), " +
        "problems (missing or late masters, patches built for an older master), changes (what changed in Data since last time; patches at risk).")]
    public static Task<CallToolResult> sfs_loadorder(
        string view = "", string plugin = "",
        [Description("order: also plugins that aren't loaded; changes: every change")] bool all = false,
        string cursor = "", string format = "",
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default) =>
        Bridge.Tool("sfs_loadorder", P(("view", view), ("plugin", plugin), ("all", all)), cursor, format, null, false, progress, ct);

    [McpServerTool(Name = "sfs_check", ReadOnly = true), Description(
        "Is a plugin built right? what: records (default: 19 checks of masters, FormIDs and their stored forms, links, overrides, ITMs, stored text outside ASCII), itm, assets (missing files), " +
        "materials (missing textures), roundtrip (written back through Mutagen, compared byte for byte), all.")]
    public static Task<CallToolResult> sfs_check(
        [Description("Name or file path")] string plugin,
        string what = "",
        [Description("Include world data")] bool world = false,
        [Description("A file outside Data: use the archives beside it")] bool beside = false,
        [Description("ITMs as SF1Edit counts them")] bool like_xedit = false,
        bool detail = false, string cursor = "", [Description(ThreeAnswers)] object? allow_heavy = null, string format = "",
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
    {
        var heavy = Answer(allow_heavy);
        return Bridge.Tool("sfs_check", P(("plugin", plugin), ("what", what), ("world", world), ("beside", beside), ("like_xedit", like_xedit), ("detail", detail), ("allow_heavy", heavy.Value)), cursor, format, null, heavy.Yes, progress, ct);
    }

    [McpServerTool(Name = "sfs_saved"), Description(
        "Stored queries with named parameters (recipes, keywords, attach points, templates, leveled lists, items, broken links, …). action: list (default), show, run, check, save.")]
    public static Task<CallToolResult> sfs_saved(
        string action = "", string name = "",
        [Description("run: {\"plugin\": \"x.esm\"} (or @official)")] object? args = null,
        [Description("save: the query file's text")] string sql = "",
        int rows = 0, string cursor = "", [Description(ThreeAnswers)] object? allow_heavy = null, string format = "",
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
    {
        var heavy = Answer(allow_heavy);
        return Bridge.Tool("sfs_saved", P(("action", action), ("name", name), ("args", args), ("rows", rows), ("allow_heavy", heavy.Value), ("sql", sql)), cursor, format, null, heavy.Yes, progress, ct);
    }

    [McpServerTool(Name = "sfs_run"), Description(
        "Other verbs: leveled2, dialogue2, sources2 <plugin> --item <item> (how the player gets it: vendor, quest, recipe, loot), archive2 <plugin|.ba2|all> [list|find|show|grep <text> [--count]|encoding], scripts2, assets2, materials2, changes2, indexes (what SFSearch keeps on disk, its size; drop it), profile <Type.Path> --in @official (a field across plugins; --conditions, --subrecords, --subrecords X,Y --order: the orders subrecords come in; --subrecords * --type T: what a type holds, sizes, links per field; --subrecords BFCB --values: a subrecord's distinct values; --subrecords * --lengths: subrecord lengths, which are empty; --formids: how FormIDs are stored, past the master list, FE/FD), add-ons. " +
        "[\"help\"] lists them; [\"<verb>\", \"--help\"] a verb's options.")]
    public static Task<CallToolResult> sfs_run(
        [Description("One item per word")] string[] args,
        [Description(ThreeAnswers)] object? allow_heavy = null, string format = "",
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
    {
        var heavy = Answer(allow_heavy);
        return Bridge.Tool("sfs_run", P(("args", args), ("allow_heavy", heavy.Value)), null, format, null, heavy.Yes, progress, ct);
    }

    [McpServerTool(Name = "sfs_job"), Description("Collect a call that came back \"still working\" with a job number, or cancel it.")]
    public static Task<CallToolResult> sfs_job(string job, bool cancel = false, int wait_seconds = 30, string format = "",
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default) =>
        Bridge.Job(job, wait_seconds, cancel, format, progress, ct);

    [McpServerTool(Name = "sfs_status", ReadOnly = true), Description("Service health: workers, queue, heavy lane, both indexes, open plugins, scratch size, memory.")]
    public static Task<CallToolResult> sfs_status(string format = "", CancellationToken ct = default) => Bridge.StatusNew(format, ct);
}
