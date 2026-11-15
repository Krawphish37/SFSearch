using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SFSearch.Service;

namespace SFSearch.Harness;

/// <summary>
/// The MCP server's own test (milestone M8): the 12 new tools driven through the harness's MCP client against a
/// test home. The server lists the 12 and their definitions stay small; sfs_status answers while other calls run; a
/// long answer is paged with the one cursor; a heavy call is refused and then run; a job is cancelled; and
/// --legacy-tools (removed in 2.0 with the 25 older tools) gives the 12 tools and a line that says so.
/// The author's agent benchmark (a dev tool, not in the public repository) adds its own calls of every tool, with
/// their known answers, through the partial method CallList; without it that part is skipped.
/// </summary>
public static partial class McpTest
{
    public static readonly string[] NewTools = { "sfs_find", "sfs_record", "sfs_describe", "sfs_query", "sfs_refs", "sfs_compare", "sfs_loadorder", "sfs_check", "sfs_saved", "sfs_run", "sfs_job", "sfs_status" };
    /// <summary>A tool's definition as the server sends it: fields that aren't set are left out.</summary>
    static readonly JsonSerializerOptions AsSent = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static async Task<McpClient> Start(Sut sut, bool legacyFlag = false) => await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
    {
        Name = "sfsearch", Command = Paths.McpExe, Arguments = legacyFlag ? ["--home", sut.Home, "--legacy-tools"] : ["--home", sut.Home], EnvironmentVariables = sut.Env(),
    }));

    // ---------------------------------------------------------------- describe: how long the tool descriptions are
    /// <summary>
    /// Counts what every session loads: each tool's description and its arguments' descriptions, in characters, and
    /// the size of the whole tools/list answer, for the 12 tools.
    /// </summary>
    /// <summary>The description texts alone, from the last Describe.</summary>
    public static int Descriptions;

    public static async Task<(int Chars, string Report)> Describe(Sut sut)
    {
        var sb = new StringBuilder();
        int[] totals = new int[1], descriptions = new int[1];
        for (int kind = 0; kind < 1; kind++)
        {
            await using var client = await Start(sut);
            var tools = (await client.ListToolsAsync()).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
            sb.Append($"The {tools.Count} tools the server lists:\n");
            sb.Append("| Tool | Its description | Its arguments' descriptions | Arguments | Together |\n|---|---:|---:|---:|---:|\n");
            int all = 0, schema = 0;
            foreach (var t in tools)
            {
                int own = (t.Description ?? "").Length, args = 0, count = 0;
                if (t.JsonSchema.TryGetProperty("properties", out var props))
                    foreach (var p in props.EnumerateObject())
                    {
                        count++;
                        if (p.Value.TryGetProperty("description", out var d)) args += (d.GetString() ?? "").Length;
                    }
                schema += JsonSerializer.Serialize(t.ProtocolTool, AsSent).Length;
                all += own + args;
                sb.Append($"| {t.Name} | {own:N0} | {args:N0} | {count} | {own + args:N0} |\n");
                if (Environment.GetEnvironmentVariable("SFSHARNESS_SHOW_TOOL") == t.Name) sb.Append(JsonSerializer.Serialize(t.ProtocolTool, AsSent)).Append('\n');
            }
            string instructions = client.ServerInstructions ?? "";
            sb.Append($"All {tools.Count} descriptions together (each tool's and its arguments'): {all:N0} characters. The {tools.Count} definitions as the server sends them, names and schemas included: {schema:N0} characters (the limit for the new tools is 10,000). The server's instructions: {instructions.Length:N0} characters.\n");
            totals[kind] = schema; descriptions[kind] = all;
        }
        Descriptions = descriptions[0];
        return (totals[0], sb.ToString());
    }

    // ---------------------------------------------------------------- test
    public sealed class Result
    {
        public int Checks, Failed, Calls;
        public Dictionary<string, int> PerTool = new(StringComparer.Ordinal);
        public List<string> Lines = [];
        public bool Pass => Failed == 0 && Checks > 0;
        public string Line = "", Memory = "";
    }

    public static async Task<Result> Run(Sut sut, bool verbose, bool withHeavy = true)
    {
        var res = new Result();
        void Check(string what, bool ok, string detail = "")
        {
            res.Checks++;
            if (!ok) res.Failed++;
            string line = $"{(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? ": " + detail : "")}";
            res.Lines.Add(line);
            Console.WriteLine(line);
        }
        static string Cut(string s, int n = 300) => s.Length > n ? s[..n].Replace("\n", "\\n") + "…" : s.Replace("\n", "\\n");

        sut.Extra["SFSEARCH_TEST_HOOKS"] = "1";
        await sut.StartService();
        var client = await sut.NewMcpClient();

        // ---- 1. The server lists the 12 tools, and nothing else
        var listed = (await client.ListToolsAsync()).Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Check("the server lists the 12 new tools by default", listed.SequenceEqual(NewTools.OrderBy(n => n, StringComparer.Ordinal)), string.Join(", ", listed));
        var (newChars, _) = await Describe(sut);
        Check("the 12 tools' definitions together stay under 10,000 characters", newChars < 10000,
            $"{newChars:N0} characters as the server sends them, names and schemas included; the description texts alone: {Descriptions:N0}");

        // ---- 2. Every tool, several ways, with known answers: the author's agent benchmark brings these calls
        Task? calls = null;
        CallList(sut, res, Check, verbose, ref calls);
        if (calls != null) await calls;
        else Console.WriteLine("SKIP  every tool's calls with known answers: the author keeps them with the agent benchmark (a dev tool), not in this checkout. replay compare --via mcp calls every tool through the server.");
        string? job = null;

        // ---- 3. sfs_status, five times, while other calls run: it always answers, with every part
        int statusOk = 0;
        for (int i = 0; i < 5; i++)
        {
            var other = i % 2 == 1 ? sut.McpText("sfs_find", new JsonObject { ["type"] = "WEAP", ["limit"] = 5 }) : null;
            var (st, stErr) = await sut.McpText("sfs_status", i == 4 ? new JsonObject { ["format"] = "json" } : null);
            if (other != null) await other;
            bool ok = !stErr && (i == 4 ? st.TrimStart().StartsWith('{') && st.Contains("Header index") : Regex.IsMatch(st, @"(?m)^Workers: ") && st.Contains("Header index: ") && st.Contains("Link index: ") && st.Contains("Scratch folder: ") && st.Contains("Open in the worker") && Regex.IsMatch(st, @"(?m)^Memory: Windows has"));
            if (ok) statusOk++; else Console.WriteLine("      status answered: " + Cut(st, 600));
            res.PerTool["sfs_status"] = res.PerTool.GetValueOrDefault("sfs_status") + 1; res.Calls++;
        }
        Check("sfs_status answered 5 of 5 times with the service, both indexes, the scratch folder, what is open and memory", statusOk == 5, $"{statusOk} of 5");

        // ---- 4. A long answer, paged with the one cursor
        {
            var pars = new JsonObject { ["type"] = "WEAP", ["limit"] = 2000 };
            var rows = new List<string>();
            int pages = 0, total = -1; string? cursor = null; bool serviceCursor = false, toolCursor = false, twoMore = false; int longest = 0;
            while (pages < 60)
            {
                var p = (JsonObject)pars.DeepClone();
                if (cursor != null) p["cursor"] = cursor;
                var (text, isError) = await sut.McpText("sfs_find", p);
                res.Calls++;
                if (isError) { Console.WriteLine("      a page came back as an error: " + Cut(text)); break; }
                pages++;
                longest = Math.Max(longest, text.Length);
                rows.AddRange(text.Split('\n').Where(l => l.StartsWith("| ") && !l.StartsWith("| Record ") && !l.StartsWith("| :---")));
                var more = text.Split('\n').Where(l => l.StartsWith("[more] ")).ToList();
                if (more.Count > 1) twoMore = true;
                if (more.Count == 0) break;
                var m = Regex.Match(more[0], @"of ([\d,]+) (?:rows )?shown\. Next: cursor ""([^""]+)""");
                if (!m.Success) break; // "That is the end."
                total = int.Parse(m.Groups[1].Value.Replace(",", ""));
                cursor = m.Groups[2].Value;
                if (cursor.Contains('@')) serviceCursor = true; else toolCursor = true;
            }
            Check("a long answer comes in pages, each under the page size, with one [more] line and one cursor", pages > 2 && !twoMore && longest <= 40000,
                $"{pages} pages for sfs_find type WEAP limit 2000; the longest page {longest:N0} characters");
            Check("the pages together are every row, each once", total > 0 && rows.Count == total && rows.Distinct().Count() == rows.Count,
                $"{rows.Count:N0} rows collected, {rows.Distinct().Count():N0} different, the answer said {total:N0}");
            Check("the one cursor carried both kinds: the service's pages of a long answer, then the tool's next rows", serviceCursor && toolCursor,
                $"service cursor seen: {serviceCursor}; tool cursor seen: {toolCursor}");
            var (stale, staleErr) = await sut.McpText("sfs_find", new JsonObject { ["type"] = "WEAP", ["cursor"] = "5.zzzz" });
            Check("a cursor of another question is refused with a clear error", staleErr && Regex.IsMatch(stale, @"(?m)^Error: .*cursor.* Fix: "), Cut(stale, 200));
        }

        // ---- 5. A heavy call: refused with its cost, then run when allowed
        if (withHeavy)
        {
            var sql = new JsonObject { ["plugins"] = "Starfield.esm", ["sql"] = "SELECT count(*) AS N FROM PlacedObject WHERE Base = '0002F6:Starfield.esm'" };
            var (refused, refusedErr) = await sut.McpText("sfs_query", sql);
            Check("a heavy call is refused without allow_heavy: the answer is its cost", !refusedErr && Regex.IsMatch(refused, @"(?m)^Not run\. .*allow_heavy=true") && !refused.Contains("| N |"), Cut(refused, 260));
            var allowed = (JsonObject)sql.DeepClone(); allowed["allow_heavy"] = true;
            var clock = Stopwatch.StartNew();
            var (ran, ranErr) = await sut.McpText("sfs_query", allowed);
            Check("the same call runs with allow_heavy=true", !ranErr && Regex.IsMatch(ran, @"(?m)^\| \d+ \|"), $"{clock.Elapsed.TotalSeconds:0.0} s; {Cut(ran, 200)}");
            res.Calls += 2;
            // The heavy lane itself: a round trip of a plugin big enough that its estimate is over the service's 2 GB limit
            var big = BigPlugin(sut.Data);
            if (big == null) Check("a call through the heavy lane", false, "no plugin of 190 to 330 MB in Data to make a round trip of");
            else
            {
                var rt = new JsonObject { ["plugin"] = big, ["what"] = "roundtrip" };
                var (no, noErr) = await sut.McpText("sfs_check", rt);
                Check($"the round trip of {big} is refused without allow_heavy", !noErr && Regex.IsMatch(no, @"(?m)^Not run\. .*GB of memory.*allow_heavy=true"), Cut(no, 260));
                rt["allow_heavy"] = true;
                clock.Restart();
                var (yes, yesErr) = await sut.McpText("sfs_check", rt);
                string status = await sut.ServiceStatus();
                var lane = Regex.Match(status, @"Heavy lane: .*");
                Check($"with allow_heavy it goes through the heavy lane and answers", Regex.IsMatch(yes, @"(?m)^Round trip of .*: (IDENTICAL|DIFFERENT)") || Regex.IsMatch(yes, @"(?m)^Error: needs about .* GB, .* GB is free"),
                    $"{clock.Elapsed.TotalSeconds:0.0} s; {Cut(yes, 220)}; {Cut(lane.Value, 200)}");
                Check("the service counted it in its heavy lane", Regex.IsMatch(lane.Value, @"Since start: [1-9]\d* run|[1-9]\d* refused \(memory short\)"), Cut(lane.Value, 220));
                res.Calls += 2;
            }
        }

        // ---- 6. A cancel stops a job (sfs_job with cancel), and a job's answer can be collected
        {
            var slow = await Wire.CallAsync(sut.Home, new Request { Op = "run", Args = ["__test", "sleep", "61000"], Agent = "harness", WaitMs = 300 }, 10000, CancellationToken.None);
            job = slow.Job;
            bool running = !slow.Done && job != null && (await sut.ServiceStatus()).Contains("__test sleep 61000");
            var (waiting, _) = job == null ? ("", false) : await sut.McpText("sfs_job", new JsonObject { ["job"] = job, ["wait_seconds"] = 1 }, collect: false);
            var (cancelled, cancelErr) = job == null ? ("no job", true) : await sut.McpText("sfs_job", new JsonObject { ["job"] = job, ["cancel"] = true });
            var clock = Stopwatch.StartNew();
            bool gone = false;
            while (clock.ElapsedMilliseconds < 8000 && !(gone = !(await sut.ServiceStatus()).Contains("__test sleep 61000"))) await Task.Delay(100);
            Check("sfs_job says a running job is still working", running && waiting.Contains("Still working on it (job " + job), Cut(waiting, 200));
            Check("sfs_job with cancel stops the job", running && !cancelErr && gone, $"gone from the service's status {clock.ElapsedMilliseconds} ms after the cancel; it answered: {Cut(cancelled, 160)}");
            // A finished job's answer is collected by its number
            var done = await Wire.CallAsync(sut.Home, new Request { Op = "run", Tool = "sfs_find", Params = new() { ["query"] = JsonSerializer.SerializeToElement("0476C4:Starfield.esm") }, Structured = true, Agent = "harness", WaitMs = -1 }, 10000, CancellationToken.None);
            var (collected, collectedErr) = await sut.McpText("sfs_job", new JsonObject { ["job"] = done.Job });
            Check("sfs_job collects a finished job's answer in the contract's parts", !collectedErr && collected.Contains("Eon (0476C4:Starfield.esm)") && collected.Contains("[source] ") && collected.Contains("[cost] "), Cut(collected, 200));
            var (asJson, _) = await sut.McpText("sfs_job", new JsonObject { ["job"] = done.Job, ["format"] = "json" });
            Check("sfs_job gives the same answer as JSON", asJson.TrimStart().StartsWith('{') && asJson.Contains("\"table\"") && asJson.Contains("\"source\""), Cut(asJson, 160));
            var (none, noneErr) = await sut.McpText("sfs_job", new JsonObject { ["job"] = "j0-999999" });
            Check("sfs_job for a job that doesn't exist is a clear error", noneErr && none.Contains("No job"), Cut(none, 160));
            res.PerTool["sfs_job"] = res.PerTool.GetValueOrDefault("sfs_job") + 5; res.Calls += 5;
        }

        // ---- 7. What the workers hold after the calls of the new tools (before the older tools are asked anything)
        {
            string status = await sut.ServiceStatus();
            var workers = Regex.Matches(status, @"worker (\d+) \(process \d+\): [^,]+, (\d+) done, (\d+) MB").Select(m => $"worker {m.Groups[1].Value}: {m.Groups[3].Value} MB after {m.Groups[2].Value} commands").ToList();
            var together = Regex.Match(status, @"the workers together hold (\d+) MB");
            res.Memory = $"the workers together hold {together.Groups[1].Value} MB ({string.Join("; ", workers)})";
            Console.WriteLine("Memory after the test: " + res.Memory);
        }

        // ---- 8. --legacy-tools (removed in 2.0 with the 25 older tools): the server still starts, and lists the 12
        {
            await using var legacy = await Start(sut, legacyFlag: true);
            var withFlag = (await legacy.ListToolsAsync()).Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Check("--legacy-tools (removed in 2.0) still starts the server, with the 12 tools", withFlag.SequenceEqual(NewTools.OrderBy(n => n, StringComparer.Ordinal)), $"{withFlag.Count} tools: {string.Join(", ", withFlag)}");
        }

        res.Line = $"{res.Checks - res.Failed} of {res.Checks} checks pass; {res.Calls} calls of the 12 tools through the MCP server ({string.Join(", ", NewTools.Select(t => $"{t[4..]} {res.PerTool.GetValueOrDefault(t)}"))})";
        return res;
    }

    /// <summary>Every tool's calls with known answers, from the author's agent benchmark: sets run to their work, or leaves it null when the benchmark isn't here.</summary>
    static partial void CallList(Sut sut, Result res, Action<string, bool, string> check, bool verbose, ref Task? run);

    /// <summary>A plugin in Data whose round trip is estimated over the service's 2 GB limit and under about 3.5 GB: 190 to 330 MB.</summary>
    static string? BigPlugin(string data) =>
        new DirectoryInfo(data).EnumerateFiles("*.esm").Where(f => f.Length is > 190L << 20 and < 330L << 20 && !f.Name.StartsWith("darkstarpatch_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Length).Select(f => f.Name).FirstOrDefault();
}
