using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SFSearch.Mcp;

/// <summary>
/// The SFSearch tools. Each one builds the same arguments sfs.cmd would take, so the answers match the command line
/// exactly. Arguments arrive as plain values: FormIDs stay text and SQL can span lines (no PowerShell quoting traps).
/// </summary>
[McpServerToolType]
public sealed class Tools
{
    public const string Instructions =
        "SFSearch reads Starfield plugins (.esm/.esp) from the game's Data folder and answers questions about their records. " +
        "Use these tools for every plugin lookup instead of parsing files. A record is given by EditorID or FormKey (000CF2:darkstar.esm); " +
        "the same six hex digits are usually different records in different plugins, so name the plugin. " +
        "Try the purpose-built tools first (sfs_names, sfs_record, sfs_refs, sfs_conflicts, sfs_q); use sfs_query for anything else. " +
        "Which version wins in the user's game: sfs_conflicts for one record, sfs_query with load_order=true for many (lo.Winners, lo.WinningRecords). " +
        "A mod updated or a patch acting strangely: sfs_changes. Script logic: sfs_scripts. " +
        "The first question about a big plugin (Starfield.esm) can take up to a minute; when a call returns a job number, call sfs_job to collect it. " +
        "Long answers are saved to a file and the path is returned. Lines under [notes] are warnings to read (missing masters, a plugin changed on disk, SF1Edit open).";

    static string[] A(params string?[] parts) => parts.Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToArray();

    static List<string> Fmt(List<string> a, string? format)
    {
        switch ((format ?? "markdown").ToLowerInvariant())
        {
            case "json": a.Add("--json"); break;
            case "csv": a.Add("--csv"); break;
        }
        return a;
    }

    [McpServerTool(Name = "sfs_query", ReadOnly = true, Idempotent = true), Description(
        "Run SQL on one or more plugins' databases. Tables: Records (FormKey, Signature, EditorID, Name, Origin, IsOverride, Data JSON), " +
        "Forms (every record: FormKey, Signature, EditorID, IsOverride, Base, Parent, HasScripts), Refs (FromFormKey, FromSignature, FromEditorID, Path, ToFormKey), " +
        "Masters, Meta, SignatureCounts. Functions: edid(fk), fkname(fk), en(json), tier(edid). Several plugins: a comma list or a glob (darkstarpatch_*); a Plugin column is added. " +
        "Search raw Data by FormKey ('%27CBC3:Starfield.esm%'), never by EditorID. Weapon/armor OMODs are WeaponModification / ArmorModification. " +
        "Don't guess JSON paths: sfs_fields lists them for a type. Add ORDER BY whenever row order matters.")]
    public static Task<CallToolResult> sfs_query(
        [Description("Plugin file name(s): darkstar.esm, a comma list, or a glob")] string plugins,
        [Description("The SQL; may span several lines and hold several statements")] string sql,
        [Description("Also attach the target's masters (aliases: sf for Starfield.esm, else the lower-case name without extension); builds temp table AllRecords")] bool with_masters = false,
        [Description("Extra plugins to attach, comma separated")] string? with = null,
        [Description("markdown (default), json or csv")] string? format = null,
        [Description("Attach the load-order index as lo: lo.Plugins (Pos, Slot, Size, Enabled...), lo.Versions (every record in every plugin, with Pos), " +
            "lo.Winners (the version that wins in game), lo.WinningRecords (full Data of the winners of the types the SQL names with Signature = '...'); " +
            "functions winner(fk), versions(fk). plugins may then be empty to query the load order alone.")] bool load_order = false,
        [Description("Attach the plugins' decompiled Papyrus scripts as scr: scr.Scripts (Plugin, Script, Source), scr.Lines (Plugin, Script, Line, Text)")] bool scripts = false,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
    {
        var a = new List<string> { "Query" };
        if (!string.IsNullOrWhiteSpace(plugins) || !load_order) a.Add(plugins);
        a.Add(sql);
        if (with_masters) a.Add("--with-masters");
        if (!string.IsNullOrWhiteSpace(with)) { a.Add("--with"); a.Add(with); }
        if (load_order) a.Add("--loadorder");
        if (scripts) a.Add("--scripts");
        return Bridge.Run(Fmt(a, format).ToArray(), progress, ct);
    }

    [McpServerTool(Name = "sfs_loadorder", ReadOnly = true, Idempotent = true), Description(
        "The real load order (plugins.txt): position, runtime slot (console prefix: 0A, FD:03, FE:09A), size (Full/Medium/Light), records, " +
        "missing or late masters, and overrides of a different type than their base (a patch built for an older master). The first call indexes every plugin's headers (about a minute).")]
    public static Task<CallToolResult> sfs_loadorder([Description("One plugin only (optional)")] string? plugin = null,
        [Description("Also list plugins in Data that aren't loaded")] bool all = false,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
        => Bridge.Run(A("LoadOrder", plugin, all ? "--all" : null), progress, ct);

    [McpServerTool(Name = "sfs_changes", ReadOnly = true, Idempotent = true), Description(
        "What changed in Data since SFSearch's history began: mods added, removed or updated (size Full/Light changes, records added or removed), " +
        "and patches at risk because a master changed size after they were built. With a plugin: its full history and the records added or removed in its last update.")]
    public static Task<CallToolResult> sfs_changes(string? plugin = null, [Description("yyyy-mm-dd (default: last 30 days)")] string? since = null,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
        => Bridge.Run(A("Changes", plugin, since != null ? "--since" : null, since), progress, ct);

    [McpServerTool(Name = "sfs_scripts", ReadOnly = true, Idempotent = true), Description(
        "A plugin's Papyrus scripts as source (decompiled once from its archives and loose files). No option: list them. find: every line containing the text. show: one script's source.")]
    public static Task<CallToolResult> sfs_scripts([Description("Plugin, or a comma list")] string plugin, string? find = null, [Description("Script name, e.g. DarkStar:TGO:Scaling:Master or just Master")] string? show = null,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
        => Bridge.Run(A("Scripts", plugin, find != null ? "--find" : null, find, show != null ? "--show" : null, show), progress, ct);

    [McpServerTool(Name = "sfs_fields", ReadOnly = true, Idempotent = true), Description(
        "Every JSON path a record type has in the database's Data, how many records have it, and a sample value: run it before writing json_extract(Data, '$...') for a type you haven't queried yet.")]
    public static Task<CallToolResult> sfs_fields(
        [Description("Plugin(s): a name, comma list or glob")] string plugins,
        [Description("Record type, e.g. Weapon, Armor, ConstructibleObject, WeaponModification, Npc")] string type,
        [Description("Only paths containing this text (optional)")] string? filter = null,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
        => Bridge.Run(A("Fields", plugins, type, filter != null ? "--filter" : null, filter), progress, ct);

    [McpServerTool(Name = "sfs_names", ReadOnly = true, Idempotent = true), Description(
        "What are these FormIDs or EditorIDs? Lists every plugin that has each one, with EditorID, type and in-game name. Use it before writing 'EditorID (FormID)'.")]
    public static Task<CallToolResult> sfs_names(
        [Description("One or more FormIDs, FormKeys or EditorIDs separated by spaces, e.g. \"003A8F 0003E6\"")] string ids,
        [Description("Only these plugins (comma list)")] string? @in = null,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
        => Bridge.Run(A("Names", ids, @in != null ? "--in" : null, @in), progress, ct);

    [McpServerTool(Name = "sfs_record", ReadOnly = true, Idempotent = true), Description(
        "Show one record. view: fields (Inspect), xedit (Read, xEdit layout), json (Dump, raw), file (Live: straight from the plugin file, including types the database skips).")]
    public static Task<CallToolResult> sfs_record(
        [Description("Plugin file name or path")] string plugin,
        [Description("EditorID or FormKey")] string record,
        [Description("fields | xedit | json | file")] string view = "fields",
        [Description("file view: also list what it links to and whether they resolve")] bool links = false,
        [Description("file view: show the override chain across the load order")] bool chain = false,
        [Description("file view: raw subrecords")] bool subrecords = false,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
    {
        string cmd = view.ToLowerInvariant() switch { "xedit" => "Read", "json" => "Dump", "file" or "live" => "Live", _ => "Inspect" };
        var a = new List<string> { cmd, plugin, record };
        if (cmd == "Live") { if (links) a.Add("--links"); if (chain) a.Add("--chain"); if (subrecords) a.Add("--subrecords"); }
        return Bridge.Run(a.ToArray(), progress, ct);
    }

    [McpServerTool(Name = "sfs_refs", ReadOnly = true, Idempotent = true), Description(
        "What points at this record? With no 'in', searches every plugin in Data that can point at it.")]
    public static Task<CallToolResult> sfs_refs(
        [Description("EditorID or FormKey")] string record,
        [Description("Only these plugins (comma list)")] string? @in = null,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
        => Bridge.Run(A("Refs", record, @in != null ? "--in" : null, @in), progress, ct);

    [McpServerTool(Name = "sfs_conflicts", ReadOnly = true, Idempotent = true), Description("Who wins this record, and which plugins edit it.")]
    public static Task<CallToolResult> sfs_conflicts(
        [Description("EditorID or FormKey")] string record,
        [Description("Only these plugins (comma list)")] string? @in = null,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
        => Bridge.Run(A("Conflicts", record, @in != null ? "--in" : null, @in), progress, ct);

    [McpServerTool(Name = "sfs_delta", ReadOnly = true, Idempotent = true), Description("What does this plugin change in its masters? Optionally one record, with field detail.")]
    public static Task<CallToolResult> sfs_delta(string plugin, [Description("FormKey or EditorID (optional)")] string? record = null, bool detail = false,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
        => Bridge.Run(A("Delta", plugin, record, detail ? "--detail" : null), progress, ct);

    [McpServerTool(Name = "sfs_diff", ReadOnly = true, Idempotent = true), Description("Compare two records field by field (in two plugins, or two records in one).")]
    public static Task<CallToolResult> sfs_diff(string plugin1, string plugin2, string record1, string? record2 = null, bool summary = true,
        [Description("Comma list of fields allowed to differ; any other change is reported as unexpected")] string? expect = null,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
        => Bridge.Run(A("Diff", plugin1, plugin2, record1, record2, summary ? "--summary" : null, expect != null ? "--expect" : null, expect), progress, ct);

    [McpServerTool(Name = "sfs_plugin_diff", ReadOnly = true, Idempotent = true), Description("What changed between two versions of a plugin, or what a patch changes in the plugin it masters.")]
    public static Task<CallToolResult> sfs_plugin_diff(string old_plugin, string new_plugin, bool detail = false,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
        => Bridge.Run(A("PluginDiff", old_plugin, new_plugin, detail ? "--detail" : null), progress, ct);

    [McpServerTool(Name = "sfs_header", ReadOnly = true, Idempotent = true), Description("A plugin's header: flags (Light/Medium/Full), masters, author, room left for new records.")]
    public static Task<CallToolResult> sfs_header(string plugin, IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
        => Bridge.Run(A("Header", plugin), progress, ct);

    [McpServerTool(Name = "sfs_stats", ReadOnly = true, Idempotent = true), Description("How many records of each type a plugin has, and which types are stored in full.")]
    public static Task<CallToolResult> sfs_stats(string plugin, IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
        => Bridge.Run(A("Stats", plugin), progress, ct);

    [McpServerTool(Name = "sfs_q", ReadOnly = true, Idempotent = true), Description(
        "Saved queries: flst (FormLists), vmad (scripts and properties), appr (attach points), kw (keywords), templates, cobj (recipes), omod (with mesh), arma (armor meshes), new, overrides. 'list' shows them all.")]
    public static Task<CallToolResult> sfs_q(
        [Description("flst | vmad | appr | kw | templates | cobj | omod | arma | new | overrides | list")] string macro,
        string? plugin = null, [Description("Optional filter (an EditorID pattern, or a Signature for overrides)")] string? filter = null,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
        => Bridge.Run(macro.Equals("list", StringComparison.OrdinalIgnoreCase) ? A("Q", "--list") : A("Q", macro, plugin, filter), progress, ct);

    [McpServerTool(Name = "sfs_profile", ReadOnly = true, Idempotent = true), Description("Full dossier on a mod before patching it.")]
    public static Task<CallToolResult> sfs_profile(string plugin, string? against = null, IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
        => Bridge.Run(A("Profile", plugin, against != null ? "--against" : null, against), progress, ct);

    [McpServerTool(Name = "sfs_add", Idempotent = true), Description(
        "Store record types the database leaves out (cells, statics, dialogue...) for one plugin. list=true shows what's missing with row counts. " +
        "Ask the user first before 'all', or before adding SurfaceTree, NavigationMesh, Cell, Worldspace or PlacedObject to Starfield.esm or ShatteredSpace.esm (gigabytes).")]
    public static Task<CallToolResult> sfs_add(string plugin, [Description("Type names, e.g. [\"Static\",\"Light\"]")] string[]? types = null, bool list = false, bool all = false,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
    {
        var a = new List<string> { "Add", plugin };
        if (list) a.Add("--list"); else if (all) a.Add("--all"); else if (types != null) a.AddRange(types);
        return Bridge.Run(a.ToArray(), progress, ct);
    }

    [McpServerTool(Name = "sfs_verifydb", ReadOnly = true, Idempotent = true), Description("Check a plugin's database against the plugin file. Prints PASS or the problems.")]
    public static Task<CallToolResult> sfs_verifydb(string plugin, IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
        => Bridge.Run(A("VerifyDb", plugin), progress, ct);

    [McpServerTool(Name = "sfs_run"), Description(
        "Any other SFSearch command, as a list of arguments exactly as sfs.cmd takes them, e.g. [\"RecipeCensus\",\"darkstar.esm\",\"--bench\",\"x\"] or [\"--help\"]. " +
        "Commands: Names, Fields, Inspect, Read, Dump, Live, Diff, Delta, Conflicts, Refs, Sources, Header, Stats, Assets, PluginDiff, LossCheck, Add, VerifyDb, Profile, MkDiff, OmodCensus, RecipeCensus, Q, Query, LoadOrder, Changes, Scripts.")]
    public static Task<CallToolResult> sfs_run([Description("The arguments, one per item")] string[] args,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
        => Bridge.Run(args, progress, ct);

    [McpServerTool(Name = "sfs_job", ReadOnly = true), Description("Collect the answer of a command that was still running (a call returned a job number).")]
    public static Task<CallToolResult> sfs_job(string job, [Description("Seconds to wait for it, up to 45")] int wait_seconds = 45,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default)
        => Bridge.Collect(job, wait_seconds, progress, ct);

    [McpServerTool(Name = "sfs_status", ReadOnly = true), Description("The SFSearch service's health: workers, queue, memory, data folder, crashes.")]
    public static Task<CallToolResult> sfs_status(CancellationToken ct = default) => Bridge.Status(ct);
}
