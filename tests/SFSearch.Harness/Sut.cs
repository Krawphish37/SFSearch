using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SFSearch.Service;

namespace SFSearch.Harness;

/// <summary>
/// One call to SFSearch, as data. The same shape is used by the replay, the trap and polish cases (and the agent benchmark's checks), so
/// a later milestone adds its tools by writing calls, not code.
/// </summary>
public sealed class Call
{
    /// <summary>
    /// cli: SFSearch.exe with Args (what sfs.cmd passes on). service: the service's structured form, a Tool by name
    /// with Params ("run" with {"args": [...]} when only Args is given). mcp: an MCP tool with Params (sfs_run when
    /// only Args is given). powershell: Line run by powershell.exe, for the traps that are a shell's doing.
    /// </summary>
    public string Via { get; set; } = "cli";
    public string[]? Args { get; set; }
    public string? Tool { get; set; }
    public JsonObject? Params { get; set; }
    /// <summary>powershell: the command line. {exe} is SFSearch.exe and {sfscmd} is sfs.cmd, each already quoted.</summary>
    public string? Line { get; set; }
    /// <summary>Text piped in (Query --stdin).</summary>
    public string? Stdin { get; set; }

    public string Show()
    {
        string Q(string a) => a.Length == 0 || a.Any(char.IsWhiteSpace) || a.Contains('"') ? "\"" + a.Replace("\n", "\\n").Replace("\"", "\\\"") + "\"" : a;
        if (Line != null) return "powershell> " + Line.Replace("\n", "\\n");
        if (Tool != null) return $"{Tool} {Params?.ToJsonString(Json.Compact) ?? "{}"}" + (Via == "cli" ? "" : $"   (via {Via})");
        return string.Join(" ", (Args ?? []).Select(Q)) + (Stdin != null ? "   <<< SQL piped in" : "") + (Via == "cli" ? "" : $"   (via {Via})");
    }
}

/// <summary>What came back from one call.</summary>
public sealed class Answer
{
    /// <summary>The answer itself: stdout, the structured form's Answer, or the MCP text above [notes].</summary>
    public string Out { get; set; } = "";
    /// <summary>The warnings beside it: stderr, the structured form's Notes, or the MCP text under [notes].</summary>
    public string Notes { get; set; } = "";
    public int Code { get; set; }
    public long Ms { get; set; }
    /// <summary>The call names a tool this SFSearch doesn't have yet.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool NotBuilt { get; set; }
    /// <summary>The call couldn't be made at all (timeout, the service couldn't be reached).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Problem { get; set; }

    [JsonIgnore] public string All => Out + (Out.EndsWith('\n') || Out.Length == 0 ? "" : "\n") + Notes;
}

public static class Json
{
    public static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    public static readonly UTF8Encoding Utf8 = new(false);

    public static T Load<T>(string file) => JsonSerializer.Deserialize<T>(File.ReadAllText(file), Read) ?? throw new InvalidDataException($"{file} is empty.");
    public static void Save<T>(string file, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(value, Pretty) + "\n", Utf8);
    }
}

/// <summary>Where things are. Set once from the command line.</summary>
public static class Paths
{
    /// <summary>The repo (the folder that holds SFSearch\SFSearch.csproj).</summary>
    public static string Repo = "";
    /// <summary>The harness's own data: replay\, mapping\, traps\, polish\ (and the author's agent benchmark, a dev tool not in the public repository).</summary>
    public static string Cases = "";
    /// <summary>Everything the harness writes goes under here. Never inside the repo, never inside a Data folder.</summary>
    public static string Work = "";
    /// <summary>The game's Data folder. Read only.</summary>
    public static string Data = "";
    public static string Exe = "", McpExe = "", ServiceTestExe = "";
    public static int Workers = 4;

    public static string SfsCmd
    {
        get
        {
            // sfs.cmd sits in the project folder of the SFSearch.exe under test
            for (var d = new DirectoryInfo(Path.GetDirectoryName(Exe)!); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "sfs.cmd"))) return Path.Combine(d.FullName, "sfs.cmd");
            return Path.Combine(Repo, "SFSearch", "sfs.cmd");
        }
    }

    public static bool Under(string path, string folder)
    {
        string p = Path.GetFullPath(path).TrimEnd('\\', '/') + "\\", f = Path.GetFullPath(folder).TrimEnd('\\', '/') + "\\";
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every file the harness writes goes through here: under the work folder, and never in a game folder.</summary>
    public static string Out(params string[] parts)
    {
        string path = Path.GetFullPath(Path.Combine(new[] { Work }.Concat(parts).ToArray()));
        if (!Under(path, Work)) throw new InvalidOperationException($"Refused: {path} is outside the work folder {Work}.");
        if (Data.Length > 0 && Under(path, Data)) throw new InvalidOperationException($"Refused: {path} is inside the Data folder.");
        return path;
    }

    /// <summary>The checks that keep the harness away from the user's SFSearch and the game.</summary>
    public static string? Problem()
    {
        if (Work.Length == 0) return "Give a work folder outside the repo with --work <folder> (or SFSHARNESS_WORK). The test home, the baseline and every other file the harness writes go there.";
        if (Under(Work, Repo)) return $"The work folder {Work} is inside the repo. Pick one outside it.";
        if (!Directory.Exists(Data)) return $"The Data folder wasn't found: '{Data}'. Give it with --data (or SFSEARCH_DATA).";
        if (Under(Work, Data) || Under(Data, Work)) return "The work folder and the Data folder must be apart.";
        if (File.Exists(Path.Combine(Work, "SFSearch.csproj")) || File.Exists(Path.Combine(Work, "sfs.cmd"))) return $"The work folder {Work} is an SFSearch folder. Pick an empty one.";
        if (!File.Exists(Exe)) return $"SFSearch.exe wasn't found at {Exe}. Build it first (dotnet build SFSearch -c Release), or give it with --exe.";
        return null;
    }
}

/// <summary>
/// The SFSearch under test: one build, one test home, one Data folder. It starts the service on the home, makes calls
/// by any route, and stops the service when done.
/// </summary>
public sealed class Sut : IAsyncDisposable
{
    public readonly string Home, Data;
    public readonly string? PluginsTxt;
    bool _serviceUp;
    // The MCP server: the 12 tools (the 25 older ones and --legacy-tools went with the older commands in 2.0)
    McpClient? _mcpNew;
    HashSet<string>? _mcpNewTools;
    public readonly Dictionary<string, string> Extra = new();
    /// <summary>After starting the service, let the link index finish before the first call (see StartService).</summary>
    public bool WaitForLinks = true;
    /// <summary>What the link index's job said when the harness waited for it.</summary>
    public string LinksLine = "";

    public Sut(string home, string data, string? pluginsTxt = null)
    {
        Home = Path.GetFullPath(home); Data = Path.GetFullPath(data); PluginsTxt = pluginsTxt;
        if (!Paths.Under(Home, Paths.Work)) throw new InvalidOperationException($"Refused: the test home {Home} is outside the work folder.");
        Directory.CreateDirectory(Home);
    }

    /// <summary>The real Data folder, with the test home work\home.</summary>
    public static Sut Real() => new(Paths.Out("home"), Paths.Data);

    /// <summary>
    /// The real Data folder, with a home of its own for the trap cases. They read fixture files by path, and today's
    /// SFSearch then carries a fixture's warnings into later, unrelated answers of the same home. The replay's home stays clean.
    /// </summary>
    public static Sut ForTraps() => new(Paths.Out("home-traps"), Paths.Data);

    public Dictionary<string, string?> Env()
    {
        var e = new Dictionary<string, string?>
        {
            ["SFSEARCH_HOME"] = Home, ["SFSEARCH_DATA"] = Data, ["SFSEARCH_WARMUP"] = "0", ["SFSEARCH_WORKERS"] = Paths.Workers.ToString(),
            ["SFSEARCH_PLUGINS_TXT"] = PluginsTxt, ["SFSEARCH_AGENT"] = "harness",
        };
        foreach (var kv in Extra) e[kv.Key] = kv.Value;
        return e;
    }

    // ---------------------------------------------------------------- Processes
    public static async Task<(string Out, string Err, int Code)> RunProcess(string file, IEnumerable<string> args, Dictionary<string, string?>? env = null, string? stdin = null, int timeoutMs = 1200000, string? cwd = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = Json.Utf8,
        };
        if (cwd != null) psi.WorkingDirectory = cwd;
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var kv in env ?? new())
            if (kv.Value == null) psi.Environment.Remove(kv.Key); else psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi)!;
        if (stdin != null) await p.StandardInput.WriteAsync(stdin);
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch (Exception) { } return ("", $"TIMEOUT: no answer in {timeoutMs / 1000} s", -99); }
        return (await o, await e, p.ExitCode);
    }

    // ---------------------------------------------------------------- The service
    public bool ServiceRunning => Wire.RunningService(Home) != null;

    /// <summary>
    /// Starts the service on the test home. One that is already running there is stopped first: it may have been
    /// started for another Data folder, and the harness is the home's only user.
    /// </summary>
    public async Task StartService()
    {
        if (_serviceUp) return;
        if (ServiceRunning) await RunProcess(Paths.Exe, ["Service", "stop"], Env(), timeoutMs: 120000);
        var (o, e, _) = await RunProcess(Paths.Exe, ["Service", "start"], Env(), timeoutMs: 120000);
        if (!ServiceRunning) throw new IOException($"The service didn't start on {Home}: {o.Trim()} {e.Trim()}");
        _serviceUp = true;
        // The link index is made in the service's indexer lane after the start (about 16 s for the real Data folder).
        // Until it is whole, refs answers some plugins from their bytes and says so: the same links, another "Searched"
        // line. A golden holds that line, so the harness lets the index finish first. A build that has no such job
        // answers "Unknown command", which is fine.
        if (WaitForLinks)
        {
            using var cts = new CancellationTokenSource(600000);
            try { LinksLine = (await Wire.CallAsync(Home, new Request { Op = "run", Args = ["__links", "build"], Agent = "harness", WaitMs = -1 }, 10000, cts.Token)).Out.Split('\n')[0].Trim(); }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { LinksLine = "the link index couldn't be waited for: " + ex.Message; }
        }
    }

    public async Task StopService()
    {
        if (_mcpNew != null) { try { await _mcpNew.DisposeAsync(); } catch (Exception) { } _mcpNew = null; }
        if (ServiceRunning) await RunProcess(Paths.Exe, ["Service", "stop"], Env(), timeoutMs: 120000);
        _serviceUp = false;
    }

    public async ValueTask DisposeAsync() => await StopService();

    public async Task<string> ServiceStatus() => (await RunProcess(Paths.Exe, ["Service", "status"], Env(), timeoutMs: 60000)).Out;

    // ---------------------------------------------------------------- Calls
    public string Fill(string text, string fixtures = "") => text
        .Replace("{data}", Data).Replace("{home}", Home).Replace("{fx}", fixtures)
        .Replace("{exe}", "'" + Paths.Exe + "'").Replace("{sfscmd}", "'" + Paths.SfsCmd + "'");

    JsonNode? Fill(JsonNode? n, string fixtures) => n switch
    {
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, Fill(kv.Value, fixtures)))),
        JsonArray a => new JsonArray(a.Select(x => Fill(x, fixtures)).ToArray()),
        JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(Fill(s, fixtures)),
        null => null,
        _ => n.DeepClone(),
    };

    public async Task<Answer> Run(Call call, string fixtures = "", int timeoutMs = 1200000)
    {
        var clock = Stopwatch.StartNew();
        Answer a;
        try
        {
            string[] args = (call.Args ?? []).Select(x => Fill(x, fixtures)).ToArray();
            var pars = (JsonObject?)Fill(call.Params, fixtures);
            string? stdin = call.Stdin == null ? null : Fill(call.Stdin, fixtures);
            a = call.Via.ToLowerInvariant() switch
            {
                "cli" when call.Tool == null => await Cli(args, stdin, timeoutMs),
                "cli" or "service" => await Structured(call.Tool, pars, args, timeoutMs),
                // A call that names a tool goes to that tool; a bare command line to sfs_run
                "mcp" => call.Tool != null ? await McpNew(call.Tool, pars, timeoutMs)
                    : await McpNew("sfs_run", new JsonObject { ["args"] = new JsonArray(args.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) }, timeoutMs),
                "powershell" => await PowerShell(Fill(call.Line ?? "", fixtures), stdin, timeoutMs),
                _ => new Answer { Problem = $"unknown route '{call.Via}' (use cli, service, mcp or powershell)", Code = -98 },
            };
        }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or InvalidOperationException or JsonException)
        {
            a = new Answer { Problem = $"{e.GetType().Name}: {e.Message}", Code = -98 };
        }
        a.Ms = clock.ElapsedMilliseconds;
        return a;
    }

    async Task<Answer> Cli(string[] args, string? stdin, int timeoutMs)
    {
        var (o, e, code) = await RunProcess(Paths.Exe, args, Env(), stdin, timeoutMs);
        return new Answer { Out = o, Notes = e, Code = code, Problem = code == -99 ? e.Trim() : null };
    }

    async Task<Answer> PowerShell(string line, string? stdin, int timeoutMs)
    {
        var (o, e, code) = await RunProcess("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", line], Env(), stdin, timeoutMs);
        return new Answer { Out = o, Notes = e, Code = code, Problem = code == -99 ? e.Trim() : null };
    }

    /// <summary>The service's structured form: a tool by name. A tool the service doesn't have is "not built yet".</summary>
    async Task<Answer> Structured(string? tool, JsonObject? pars, string[] args, int timeoutMs)
    {
        await StartService();
        var names = tool == null ? new[] { "run" } : tool.StartsWith("sfs_", StringComparison.OrdinalIgnoreCase) ? new[] { tool, tool[4..] } : new[] { tool };
        pars ??= new JsonObject { ["args"] = new JsonArray(args.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) };
        var p = pars.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));
        Reply? r = null;
        foreach (var name in names)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            r = await Wire.CallAsync(Home, new Request { Op = "run", Tool = name, Params = p, Structured = true, Agent = "harness", WaitMs = -1, AllowHeavy = pars.ContainsKey("allow_heavy") }, 10000, cts.Token);
            if (!NoSuchTool(r)) break;
        }
        var a = new Answer { Out = r!.Answer ?? r.Out, Notes = string.Join("\n", r.Notes ?? []), Code = r.Code, Problem = r.Error };
        if (r.Failure != null && !a.Out.Contains(r.Failure.Message)) a.Out += (a.Out.Length > 0 && !a.Out.EndsWith('\n') ? "\n" : "") + "Error: " + r.Failure.Message + (r.Failure.Fix != null ? ". Fix: " + r.Failure.Fix : "") + "\n";
        if (r.Source is { Length: > 0 } || r.Cost != null)
        {
            // The contract's [source] line, so a case can ask what an answer was read from
            string rendered = Wire.Render(r);
            // The [more] line too: what a tool left out, and its cursor, are part of the answer (a golden holds them)
            foreach (var part in new[] { "[more] ", "[source] " })
                if (rendered.Split('\n').FirstOrDefault(l => l.StartsWith(part)) is { } line) a.Notes += (a.Notes.Length > 0 ? "\n" : "") + line;
        }
        a.NotBuilt = NoSuchTool(r);
        return a;
    }

    /// <summary>One tool call through the service's structured form, as the reply came (for 'sfsharness call').</summary>
    public async Task<Reply> Tool(string tool, JsonObject pars, int pageChars = 0)
    {
        await StartService();
        var p = pars.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));
        return await Wire.CallAsync(Home, new Request { Op = "run", Tool = tool, Params = p, Structured = true, PageChars = pageChars, Agent = "harness", WaitMs = -1 }, 10000, CancellationToken.None);
    }

    static bool NoSuchTool(Reply r) => (r.Failure?.Message ?? r.Answer ?? r.Out).Contains("there is no tool named", StringComparison.OrdinalIgnoreCase);

    /// <summary>The MCP server that lists the 12 new tools, started on this home.</summary>
    public async Task<McpClient> NewMcpClient()
    {
        await StartService();
        if (_mcpNew != null) return _mcpNew;
        if (!File.Exists(Paths.McpExe)) throw new IOException($"SFSearch.Mcp.exe wasn't found at {Paths.McpExe}. Build it (dotnet build SFSearch.Mcp -c Release), or give it with --mcp.");
        _mcpNew = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "sfsearch", Command = Paths.McpExe, Arguments = ["--home", Home], EnvironmentVariables = Env(),
        }));
        _mcpNewTools = (await _mcpNew.ListToolsAsync()).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _mcpNew;
    }

    /// <summary>One call of a new tool through the MCP server, as the text it gave back.</summary>
    /// <param name="collect">False: a "still working" answer is handed back as it is, not collected with sfs_job.</param>
    public async Task<(string Text, bool IsError)> McpText(string tool, JsonObject? pars, int timeoutMs = 1200000, bool collect = true)
    {
        var client = await NewMcpClient();
        var given = (pars ?? new JsonObject()).ToDictionary(kv => kv.Key, kv => (object?)JsonSerializer.SerializeToElement(kv.Value));
        using var cts = new CancellationTokenSource(timeoutMs);
        var (text, isError) = await Tool(client, tool, given, cts.Token);
        // A long call hands back a job number: collect it
        while (collect && text.StartsWith("Still working on it (job "))
        {
            string job = text["Still working on it (job ".Length..].Split(':')[0];
            (text, isError) = await Tool(client, "sfs_job", new() { ["job"] = job, ["wait_seconds"] = 45 }, cts.Token);
        }
        return (text.Replace("\r\n", "\n"), isError);
    }

    /// <summary>
    /// A new tool through the MCP server, read back into the parts a case looks at: the answer, and beside it the
    /// [notes] with the [more] and [source] lines (as the service route gives them). The [cost] line is left out.
    /// </summary>
    async Task<Answer> McpNew(string tool, JsonObject? pars, int timeoutMs)
    {
        await NewMcpClient();
        if (!_mcpNewTools!.Contains(tool)) return new Answer { NotBuilt = true, Out = $"The MCP server has no tool named '{tool}'.", Code = 2 };
        var (text, isError) = await McpText(tool, pars, timeoutMs);
        var o = new StringBuilder(); var n = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("[notes] ", StringComparison.Ordinal)) n.Append(line["[notes] ".Length..]).Append('\n');
            else if (line.StartsWith("[more] ", StringComparison.Ordinal) || line.StartsWith("[source] ", StringComparison.Ordinal)) n.Append(line).Append('\n');
            else if (line.StartsWith("[cost] ", StringComparison.Ordinal)) continue;
            else o.Append(line).Append('\n');
        }
        string outText = o.ToString().TrimEnd('\n');
        return new Answer { Out = outText.Length > 0 ? outText + "\n" : "", Notes = n.ToString().TrimEnd('\n'), Code = isError ? 1 : outText.StartsWith("Not run.", StringComparison.Ordinal) ? 7 : 0 };
    }

    static async Task<(string Text, bool IsError)> Tool(McpClient client, string tool, Dictionary<string, object?> args, CancellationToken ct)
    {
        var r = await client.CallToolAsync(tool, args, cancellationToken: ct);
        return (string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text)), r.IsError == true);
    }

    // ---------------------------------------------------------------- Sizes
    /// <summary>How much SFSearch has written into the test home: its databases, and everything.</summary>
    public (long DbBytes, int DbFiles, long AllBytes) HomeSize()
    {
        long db = 0, all = 0; int n = 0;
        foreach (var f in new DirectoryInfo(Home).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            long len; try { len = f.Length; } catch (IOException) { continue; }
            all += len;
            if (f.Name.Contains(".db", StringComparison.OrdinalIgnoreCase)) { db += len; if (f.Extension.Equals(".db", StringComparison.OrdinalIgnoreCase)) n++; }
        }
        return (db, n, all);
    }
}
