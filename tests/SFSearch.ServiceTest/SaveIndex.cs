using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using SFSearch.Service;

namespace SFSearch.ServiceTest;

/// <summary>
/// `saveindex --exe &lt;SFSearch.exe&gt;` (J33, 2026-11-15): the third answer at a permission point, "Yes, but save an index
/// to speed up future queries", through the service. Its own home and test Data folder under the test home, with the
/// world index test's two plugins (a master with one static; a plugin with an interior cell of 30 placed objects of it),
/// written by Mutagen there, never into the game's Data. No test hook. Checks:
///   1. sfs_refs with world "save_index" (a tool call, as the MCP server sends it) answers now, from the bytes, and says
///      the index is being made; the indexer lane then makes it (one world link file, the plugin kept in wanted.txt);
///   2. the same question again answers from it, with the same rows;
///   3. after indexes drop world, query2 --allow-heavy=save-index over Links WHERE ToKey = '…' answers, saves the index
///      again, and the same query then takes the world rows from it;
///   4. allow_heavy "save_index" where no index helps (a count of statics) runs as a yes and says no index is made;
///   5. without the third answer, nothing is made;
///   6. a build of a plugin no longer asked for (dropped while the lane was making it) writes no file.
/// Stops its service at the end.
/// </summary>
public static partial class T
{
    static async Task SaveIndex()
    {
        if (!File.Exists(Exe)) { Check("saveindex: --exe names SFSearch.exe", false, Exe); return; }
        string root = Path.Combine(Home, "saveindex"), home = Path.Combine(root, "home"), data = Path.Combine(root, "plugins"), txt = Path.Combine(root, "plugins.txt");
        if (Path.GetFullPath(data).Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)) { Check("saveindex: the test Data folder is outside the game's", false, data); return; }
        string outer = Home;
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(home); Directory.CreateDirectory(data);
        var stat = WriteWorldMaster(data);
        WriteWorld(data, stat, placed: 30);
        string key = $"{stat.ID:X6}:{WMaster}";
        File.WriteAllLines(txt, new[] { WMaster, WWorld }.Select(p => "*" + p));
        var env = new Dictionary<string, string> { ["SFSEARCH_HOME"] = home, ["SFSEARCH_DATA"] = data, ["SFSEARCH_PLUGINS_TXT"] = txt, ["SFSEARCH_WORKERS"] = "2" };
        string world = Path.Combine(home, "indexes", "world"), wanted = Path.Combine(world, "wanted.txt");
        string logDir = Path.Combine(home, "Service", "logs");
        static List<string> Rows(string answer) => answer.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("| ") && !l.StartsWith("| Record") && !l.StartsWith("| :")).ToList();
        static Request Tool(string tool, Dictionary<string, object> pars) => new()
        {
            Op = "run", Tool = tool, Structured = true, Agent = "test", WaitMs = -1,
            Params = pars.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)),
        };
        // The indexer lane makes the file after the answer: wait for it (the plugins are tiny, so it takes a moment)
        async Task<(bool Made, long Ms)> WaitForWorldFile()
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 60)
            {
                if (Files(world, "*.wlinks").Count == 1 && Regex.IsMatch(await Status(), @"Indexer: idle; 0 waiting")) return (true, clock.ElapsedMilliseconds);
                await Task.Delay(200);
            }
            return (false, clock.ElapsedMilliseconds);
        }
        Home = home;
        try
        {
            var (ok, _, log) = await StartAndSettle(env);
            if (!ok) { Check("saveindex: the service starts and makes the header and link indexes", false, log); return; }
            int before = NewLog(logDir, 0).Length;

            // 1. world "save_index" through the tool, as the MCP server sends it
            var first = await Call(Tool("sfs_refs", new() { ["record"] = key, ["world"] = "save_index", ["limit"] = 100 }));
            string firstText = first.Answer ?? first.Out;
            string firstAll = Wire.Render(first);
            var firstRows = Rows(firstText);
            Check("saveindex: sfs_refs world \"save_index\" answers now, by the world data's bytes, and says the index is being made",
                first.Code == 0 && firstText.Contains("30 records point at j32_stat") && firstText.Contains("by its bytes") && firstRows.Count == 30
                    && firstAll.Contains("Saving the world link index of j32_world.esm") && firstAll.Contains("the service makes it in the background") && firstAll.Contains("indexes drop world deletes it"),
                $"{Line(firstText, "point at")}; {Line(firstAll, "[notes] Saving")}");
            var (made, ms) = await WaitForWorldFile();
            string after = NewLog(logDir, before);
            Check("saveindex: the indexer lane makes it after the answer: one world link file, the choice kept in wanted.txt",
                made && File.Exists(wanted) && File.ReadAllLines(wanted).Contains("*") && after.Contains("(save_index) is queued") && Regex.IsMatch(after, @"world link index: 1 of 1 plugins[^\n]*This build: 1 plugin\(s\)"),
                $"made in {ms} ms: {made}; wanted.txt {(File.Exists(wanted) ? string.Join(" ", File.ReadAllLines(wanted).Where(l => !l.StartsWith('#'))) : "missing")}; {Grab(after, @"Indexer: the world link index asked for[^\n]*")}; {Grab(after, @"world link index: [^\n]*")}");

            // 2. The same question again: from the index
            var clock = Stopwatch.StartNew();
            var second = await Call(Tool("sfs_refs", new() { ["record"] = key, ["limit"] = 100 }));
            long secondMs = clock.ElapsedMilliseconds;
            string secondText = second.Answer ?? second.Out;
            Check("saveindex: the same question then answers from the world link index, with the same rows",
                second.Code == 0 && secondText.Contains("from the world link index of 1 plugin") && !secondText.Contains("by its bytes") && Rows(secondText).SequenceEqual(firstRows),
                $"{secondMs} ms; {Line(secondText, "Searched:")}; rows the same: {Rows(secondText).SequenceEqual(firstRows)}");

            // 3. query2 --allow-heavy=save-index over Links WHERE ToKey (a command line through the service)
            var drop = await Call(Cmd("indexes", "drop", "world"));
            string sql = $"SELECT FromType, Field, count(*) AS N FROM Links WHERE ToKey = '{key}' GROUP BY FromType, Field;";
            before = NewLog(logDir, 0).Length;
            var q1 = await Call(Cmd("query2", sql, "--allow-heavy=save-index"));
            (made, ms) = await WaitForWorldFile();
            var q2 = await Call(Cmd("query2", sql));
            Check("saveindex: query2 --allow-heavy=save-index answers, saves the world link index, and the same query then takes the world rows from it",
                drop.Code == 0 && q1.Code == 0 && q1.Out.Contains("| PlacedObject | Base | 30 |") && q1.Out.Contains("Saving the world link index of j32_world.esm")
                    && made && q2.Code == 0 && q2.Out.Contains("| PlacedObject | Base | 30 |") && q2.Out.Contains("came from the world link index of 1 plugin"),
                $"{Line(q1.Out, "| PlacedObject")}; {Line(q1.Out, "[notes] Saving")}; made in {ms} ms: {made}; then {Line(q2.Out, "[notes] Links WHERE ToKey")}");
            // The type named too: its records that link to the key come from the index, the type isn't read whole (after a
            // restart, so that the scratch tables the reads above made are gone; the index is kept)
            await RunWith(env, "Service", "stop");
            (ok, _, log) = await StartAndSettle(env);
            var q3 = await Call(Cmd("query2", $"SELECT FromType, Field, count(*) AS N FROM Links WHERE ToKey = '{key}' AND FromType = 'PlacedObject' GROUP BY Field;"));
            Check("saveindex: with the index, FromType = 'PlacedObject' AND ToKey reads only the placed objects that link to it, not the type whole",
                ok && q3.Code == 0 && q3.Out.Contains("| PlacedObject | Base | 30 |") && q3.Out.Contains("came from the world link index of 1 plugin: 30 record(s) (PlacedObject), read alone") && !Regex.IsMatch(q3.Out, @"read [^;\n]*PlacedObject \(\d+\) from"),
                $"{Line(q3.Out, "| PlacedObject")}; {Line(q3.Out, "[cost]")}");

            // 4. save_index where no index helps
            var none = await Call(Tool("sfs_query", new() { ["sql"] = "SELECT count(*) AS N FROM Static;", ["allow_heavy"] = "save_index" }));
            string noneAll = Wire.Render(none);
            Check("saveindex: allow_heavy \"save_index\" where no index helps runs as a yes and says no index is made",
                none.Code == 0 && noneAll.Contains("| 1 |") && noneAll.Contains("save_index was asked, but no saved index would make this question faster") && !noneAll.Contains("Saving the world"),
                Line(noneAll, "[notes] save_index was asked"));

            // 5. Without the third answer nothing is made
            await Call(Cmd("indexes", "drop", "world"));
            var plain = await Call(Tool("sfs_refs", new() { ["record"] = key, ["world"] = true, ["limit"] = 100 }));
            await Task.Delay(3000);
            Check("saveindex: world true without save_index makes no index",
                plain.Code == 0 && Files(world, "*.wlinks").Count == 0 && !File.Exists(wanted) && !Wire.Render(plain).Contains("Saving the world"),
                $"{Files(world, "*.wlinks").Count} file(s); wanted.txt {(File.Exists(wanted) ? "there" : "not there")}");

            // 6. A build whose plugin is no longer asked for (dropped while the indexer lane was making it) writes nothing:
            // the indexer's own job, named with the plugin, is the build a save_index answer queues
            var orphan = await Call(Cmd("__links", "world", WWorld));
            Check("saveindex: a world build of a plugin no longer asked for (dropped while it was being made) writes no file",
                orphan.Code == 0 && Files(world, "*.wlinks").Count == 0 && orphan.Out.Contains("was dropped (indexes drop world) while it was being made"),
                $"{Files(world, "*.wlinks").Count} file(s); {Line(orphan.Out, "j32_world.esm:")}");
            await RunWith(env, "Service", "stop");
        }
        finally
        {
            await RunWith(env, "Service", "stop");
            Home = outer;
        }
    }
}
