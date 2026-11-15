using System.Text.Json;
using SFSearch.Service;

namespace SFSearch.ServiceTest;

public static partial class T
{
    /// <summary>
    /// The texts that came from the older program (fix list C17): --help lists the verbs, a mistyped verb answers
    /// "Error: ... Fix: ..." with the nearest verbs and exit code 2, an older command's name (removed in 2.0) answers
    /// with the verb to use, and nothing the service or the tools print speaks of "stored databases" (there are none).
    /// Through the service and through the command line.
    /// </summary>
    static async Task Wording()
    {
        static bool Stored(string text) => text.Contains("stored database", StringComparison.OrdinalIgnoreCase);

        var help = await Call(Cmd("--help"));
        int verbs = help.Out.IndexOf("  find ", StringComparison.Ordinal), older = help.Out.IndexOf("were removed in 2.0", StringComparison.Ordinal);
        Check("wording: --help lists the verbs, then says the older commands were removed", verbs > 0 && older > verbs, $"find at {verbs}, the removal at {older}");
        Check("wording: --help never says stored databases", !Stored(help.Out));

        foreach (var (typed, nearest) in new[] { ("fnd", "find"), ("query3", "query2"), ("Refz", "refs2") })
        {
            var r = await Call(Cmd(typed));
            string all = r.Out + r.Err;
            Check($"wording: the mistyped verb {typed} (service)", r.Code == 2 && all.Contains($"Error: there is no verb or command named '{typed}'") && all.Contains("Fix: the nearest real names: " + nearest),
                $"exit {r.Code}: {all.Trim().Split('\n')[0]}");
        }
        if (File.Exists(Exe))
        {
            var (o, e, code) = await RunExe(Exe, ["fnd"]);
            Check("wording: a mistyped verb (command line)", code == 2 && (o + e).Contains("Fix: the nearest real names: find"), $"exit {code}: {(o + e).Trim().Split('\n')[0]}");
        }
        var build = await Call(Cmd("BuildCache", "atom74.esm"));
        Check("wording: BuildCache says it was removed and that nothing replaces it", build.Code == 2 && !Stored(build.Out) && build.Out.Contains("removed in 2.0") && build.Out.Contains("Fix: nothing replaces it"), build.Out.Trim());
        foreach (var (old, verb) in new[] { ("Query", "query2"), ("Names", "find"), ("Refs", "refs2"), ("Check", "check2"), ("Inspect", "record") })
        {
            var r = await Call(Cmd(old, "atom74.esm"));
            Check($"wording: the older command {old} points at {verb}", r.Code == 2 && r.Out.StartsWith($"Error: {old} was one of SFSearch's older commands, removed in 2.0. Fix: use {verb}"), r.Out.Trim());
        }
        if (File.Exists(Exe))
        {
            var (o, e, code) = await RunExe(Exe, ["Names", "000CF2"]);
            Check("wording: an older command's name points at its verb (command line)", code == 2 && o.StartsWith("Error: Names was one of SFSearch's older commands, removed in 2.0. Fix: use find"), $"exit {code}: {(o + e).Trim()}");
        }

        // sfs_run ["help"], as the MCP server sends it: the tool is "run" in the service
        Reply? tool = null;
        foreach (var name in new[] { "sfs_run", "run" })
        {
            tool = await Wire.CallAsync(Home, new Request { Op = "run", Tool = name, Params = new() { ["args"] = JsonSerializer.SerializeToElement(new[] { "help" }) }, Structured = true, Agent = "test", WaitMs = -1 }, 10000, CancellationToken.None);
            string said = (tool.Answer ?? tool.Out) + tool.Error;
            if (!said.Contains("no tool", StringComparison.OrdinalIgnoreCase) && !said.Contains("Unknown tool", StringComparison.OrdinalIgnoreCase)) break;
        }
        string text = (tool?.Answer ?? tool?.Out ?? "") + tool?.Error;
        Check("wording: sfs_run [\"help\"] never says stored databases", text.Contains("| find |") && !Stored(text), Cut(text.Split('\n').LastOrDefault(l => l.Contains("ends in 2")) ?? text));
    }
}
