using System.Text;

namespace SFSearch.ServiceTest;

public static partial class T
{
    /// <summary>One command: as sfs.cmd takes it, and as the MCP tool call that means the same.</summary>
    public sealed record Case(string Name, string[] Cli, string Tool, Dictionary<string, object?> Args);

    static Dictionary<string, object?> A(params (string K, object? V)[] kv) => kv.ToDictionary(p => p.K, p => p.V);

    const string Omod = "000808:atom74.esm";         // mod_ccAtom74_Barrel_Standard, overridden by the patch
    const string Patch = "darkstarpatch_atom74.esm";

    /// <summary>
    /// Command lines of the verbs, on the test Data folder (11 plugin copies), each with the MCP tool call that means the
    /// same. Chosen to cover lookups, SQL, the load order, a long answer, usage lines and errors. (Until SFSearch 2.0
    /// these were the older commands and the 25 older tools, removed with them: notes\remove-old-commands.md.)
    /// </summary>
    public static readonly Case[] Cases =
    {
        new("find_id", ["find", "000CF2"], "sfs_find", A(("query", "000CF2"))),
        new("find_edids", ["find", "ccAtom74 LL_Weapon_ccAtom74"], "sfs_find", A(("query", "ccAtom74 LL_Weapon_ccAtom74"))),
        new("find_in", ["find", "000808", "--in", "atom74.esm"], "sfs_find", A(("query", "000808"), ("in", "atom74.esm"))),
        new("loadorder_plugin", ["loadorder2", "plugin", "darkstar.esm"], "sfs_loadorder", A(("view", "plugin"), ("plugin", "darkstar.esm"))),
        new("loadorder_missing", ["loadorder2", "plugin", "nosuch.esm"], "sfs_loadorder", A(("view", "plugin"), ("plugin", "nosuch.esm"))),
        new("query_small", ["query2", "atom74.esm", "SELECT Sig, count(*) AS N FROM Forms GROUP BY 1 ORDER BY 1;"], "sfs_query",
            A(("plugins", "atom74.esm"), ("sql", "SELECT Sig, count(*) AS N FROM Forms GROUP BY 1 ORDER BY 1;"))),
        new("query_long", ["query2", "darkstar.esm", "SELECT FormKey, EditorID, Sig FROM Forms ORDER BY FormKey LIMIT 1500;", "--rows", "1500"], "sfs_query",
            A(("plugins", "darkstar.esm"), ("sql", "SELECT FormKey, EditorID, Sig FROM Forms ORDER BY FormKey LIMIT 1500;"), ("rows", 1500))),
        new("query_two", ["query2", "atom74.esm,darkstarpatch_atom74.esm", "SELECT Plugin, count(*) AS N FROM Forms GROUP BY 1 ORDER BY 1;"], "sfs_query",
            A(("plugins", "atom74.esm,darkstarpatch_atom74.esm"), ("sql", "SELECT Plugin, count(*) AS N FROM Forms GROUP BY 1 ORDER BY 1;"))),
        new("query_bad_sql", ["query2", "atom74.esm", "SELECT Nope FROM Weapon;"], "sfs_query", A(("plugins", "atom74.esm"), ("sql", "SELECT Nope FROM Weapon;"))),
        new("saved_cobj", ["saved", "run", "cobj", "plugin=atom74.esm"], "sfs_saved", A(("action", "run"), ("name", "cobj"), ("args", new Dictionary<string, object?> { ["plugin"] = "atom74.esm" }))),
        new("saved_list", ["saved", "list"], "sfs_saved", A(("action", "list"))),
        new("describe", ["describe", "WEAP", "damage"], "sfs_describe", A(("what", "WEAP"), ("filter", "damage"))),
        new("record", ["record", "ccAtom74", "--plugin", "atom74.esm"], "sfs_record", A(("plugin", "atom74.esm"), ("record", "ccAtom74"))),
        new("record_raw", ["record", "ccAtom74", "--plugin", "atom74.esm", "--view", "raw"], "sfs_record", A(("plugin", "atom74.esm"), ("record", "ccAtom74"), ("view", "raw"))),
        new("record_json", ["record", "LL_Weapon_ccAtom74", "--plugin", "atom74.esm", "--view", "json"], "sfs_record", A(("plugin", "atom74.esm"), ("record", "LL_Weapon_ccAtom74"), ("view", "json"))),
        new("compare_patch", ["compare", "plugin_vs_masters", Patch], "sfs_compare", A(("kind", "plugin_vs_masters"), ("a", Patch))),
        new("compare_versions", ["compare", "versions", Omod], "sfs_compare", A(("kind", "versions"), ("record", Omod))),
        new("refs_in", ["refs2", Omod, "--in", "darkstarpatch_atom74.esm,atom74.esm"], "sfs_refs", A(("record", Omod), ("in", "darkstarpatch_atom74.esm,atom74.esm"))),
        new("check", ["check2", "atom74.esm"], "sfs_check", A(("plugin", "atom74.esm"))),
        new("leveled", ["leveled2", "LL_Weapon_ccAtom74", "--in", "atom74.esm"], "sfs_run", A(("args", new[] { "leveled2", "LL_Weapon_ccAtom74", "--in", "atom74.esm" }))),
        new("help", ["--help"], "sfs_run", A(("args", new[] { "help" }))),
        new("unknown", ["Bogus"], "sfs_run", A(("args", new[] { "Bogus" }))),
        new("retired", ["Names", "000CF2"], "sfs_run", A(("args", new[] { "Names", "000CF2" }))),
        new("extensions", ["Extensions"], "sfs_run", A(("args", new[] { "Extensions" }))),
    };

    /// <summary>
    /// Calls of the 12 new tools (milestone M8), on the same test Data folder, for the soak test: sent through an MCP
    /// server that lists the new tools. Each names its plugins, so a plugin copy whose date the test changes (and that
    /// has no link file for a while afterwards) doesn't change the answer.
    /// </summary>
    public static readonly Case[] NewCases =
    {
        new("find_id", [], "sfs_find", A(("query", "000808"), ("in", "atom74.esm"))),
        new("find_type", [], "sfs_find", A(("type", "WEAP"), ("in", "kgcdoom.esm"))),
        new("find_name", [], "sfs_find", A(("query", "Super Shotgun"), ("in", "kgcdoom.esm"))),
        new("record_card", [], "sfs_record", A(("record", Omod), ("plugin", "atom74.esm"))),
        new("record_fields", [], "sfs_record", A(("record", "kgcDoom_WEAP_SuperShotgun"), ("plugin", "kgcdoom.esm"), ("view", "fields"), ("fields", "Name,AttackDamage"))),
        new("record_versions", [], "sfs_record", A(("record", Omod), ("view", "versions"))),
        new("describe_filter", [], "sfs_describe", A(("what", "WEAP"), ("filter", "damage"))),
        new("describe_wrong", [], "sfs_describe", A(("what", "Weapon.BaseDamage"))),
        new("query_weapons", [], "sfs_query", A(("plugins", "atom74.esm"), ("sql", "SELECT EditorID, AttackDamage FROM Weapon ORDER BY 1"))),
        new("query_count", [], "sfs_query", A(("plugins", "kgcdoom.esm"), ("sql", "SELECT count(*) AS N FROM Static"))),
        new("query_lists", [], "sfs_query", A(("plugins", "kgcdoom.esm"), ("sql", "SELECT w.EditorID, count(*) AS Keywords\nFROM Weapon w JOIN Weapon_Keywords k ON k.FormKey = w.FormKey\nGROUP BY 1 ORDER BY 1"))),
        new("query_bad", [], "sfs_query", A(("plugins", "atom74.esm"), ("sql", "SELECT Nope FROM Weapon ORDER BY 1"))),
        new("refs_in", [], "sfs_refs", A(("record", Omod), ("in", "darkstarpatch_atom74.esm,atom74.esm"))),
        new("refs_out", [], "sfs_refs", A(("record", "kgcDoom_WEAP_SuperShotgun"), ("plugin", "kgcdoom.esm"), ("direction", "out"), ("limit", 10))),
        new("compare_patch", [], "sfs_compare", A(("kind", "plugin_vs_masters"), ("a", Patch))),
        new("compare_versions", [], "sfs_compare", A(("kind", "versions"), ("record", Omod))),
        new("loadorder_plugin", [], "sfs_loadorder", A(("view", "plugin"), ("plugin", "atom74.esm"))),
        new("check_records", [], "sfs_check", A(("plugin", "atom74.esm"))),
        new("check_roundtrip", [], "sfs_check", A(("plugin", "atom74.esm"), ("what", "roundtrip"))),
        new("saved_list", [], "sfs_saved", A(("action", "list"))),
        new("saved_run", [], "sfs_saved", A(("action", "run"), ("name", "cobj"), ("args", new Dictionary<string, object?> { ["plugin"] = "atom74.esm" }))),
        new("run_leveled", [], "sfs_run", A(("args", new[] { "leveled2", "LL_Weapon_ccAtom74", "--in", "atom74.esm" }))),
        new("run_help", [], "sfs_run", A(("args", new[] { "help" }))),
    };

    /// <summary>
    /// A new tool's answer without what legitimately differs between two calls: the [cost] line (times, and what had
    /// to be read this time), notes that a plugin copy changed on disk (the test changes one's date), and times in
    /// the cost of a round trip.
    /// </summary>
    public static string NormalNew(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in Normal(text).Split('\n'))
        {
            if (raw.StartsWith("[cost] ", StringComparison.Ordinal)) continue;
            if (raw.StartsWith("[notes] ", StringComparison.Ordinal) && (raw.Contains("changed on disk", StringComparison.Ordinal) || raw.Contains("reads differently now", StringComparison.Ordinal)
                || raw.Contains("The load order changed", StringComparison.Ordinal) || raw.Contains("this program held", StringComparison.Ordinal))) continue;
            if (raw.Length > 0) sb.Append(raw).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Runs every case through the command line, then through the MCP server, twice. The first pass lets first reads
    /// happen (their progress notes carry timings); the second is the one kept. One file per case and route.
    /// </summary>
    static async Task Capture(string outDir)
    {
        if (!File.Exists(Exe) || !File.Exists(Mcp)) { Console.WriteLine("capture needs --exe <SFSearch.exe> and --mcp <SFSearch.Mcp.exe>"); _fail++; return; }
        Directory.CreateDirectory(outDir);
        await using var client = await StartMcp(Mcp);
        for (int pass = 1; pass <= 2; pass++)
        {
            foreach (var c in Cases)
            {
                var (o, e, code) = await RunExe(Exe, c.Cli);
                var (text, isError) = await Tool(client, c.Tool, c.Args);
                if (pass == 1) continue;
                File.WriteAllText(Path.Combine(outDir, $"cli_{c.Name}.txt"), $"exit {code}\n--- stdout\n{o}\n--- stderr\n{e}", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(outDir, $"mcp_{c.Name}.txt"), $"isError {isError}\n--- text\n{text}", new UTF8Encoding(false));
            }
            Say($"pass {pass}: {Cases.Length} commands through the command line and {Cases.Length} through the MCP server");
        }
        Check("capture", true, $"{Cases.Length * 2} answers written to {Path.GetFullPath(outDir)}");
    }

    /// <summary>compare a b: every file of a against the same file of b, after Normal(). Lists what differs.</summary>
    static int Compare(List<string> rest)
    {
        string a = rest[0], b = rest[1];
        int same = 0, exact = 0, differ = 0, missing = 0;
        foreach (var fa in Directory.GetFiles(a, "*.txt").OrderBy(f => f))
        {
            string fb = Path.Combine(b, Path.GetFileName(fa));
            if (!File.Exists(fb)) { missing++; Console.WriteLine($"MISSING in {b}: {Path.GetFileName(fa)}"); continue; }
            string ta = File.ReadAllText(fa), tb = File.ReadAllText(fb);
            if (ta == tb) { exact++; same++; continue; }
            if (Normal(ta) == Normal(tb)) { same++; continue; }
            differ++;
            Console.WriteLine($"DIFFERENT: {Path.GetFileName(fa)}");
            var la = Normal(ta).Split('\n'); var lb = Normal(tb).Split('\n');
            int shown = 0;
            for (int i = 0; i < Math.Max(la.Length, lb.Length) && shown < 6; i++)
            {
                string x = i < la.Length ? la[i] : "<end>", y = i < lb.Length ? lb[i] : "<end>";
                if (x == y) continue;
                Console.WriteLine($"  line {i + 1}\n    a: {Cut(x)}\n    b: {Cut(y)}");
                shown++;
            }
        }
        int total = same + differ + missing;
        Console.WriteLine($"{(differ + missing == 0 ? "PASS" : "FAIL")}  compare {Path.GetFileName(a.TrimEnd('\\', '/'))} with {Path.GetFileName(b.TrimEnd('\\', '/'))}: {total} answers, {same} the same ({exact} byte for byte, {same - exact} after taking out file names of saved answers, job numbers and progress notes), {differ} different, {missing} missing.");
        return differ + missing;
    }

    static string Cut(string s) => s.Length > 200 ? s[..200] + "…" : s;
}
