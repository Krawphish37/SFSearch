using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SFSearch.Harness;

/// <summary>
/// One command of a replay set: what is asked, never what was answered. A recorded past command has Args. A golden
/// for a new tool has Tool and Params instead (and goes through the service's structured form, or the MCP server).
/// </summary>
public sealed class Recorded
{
    public string Id { get; set; } = "";
    public string[] Args { get; set; } = [];
    /// <summary>SQL that was piped in or read from a file (Query --stdin).</summary>
    public string? Stdin { get; set; }
    public string? Tool { get; set; }
    public System.Text.Json.Nodes.JsonObject? Params { get; set; }

    public Call AsShown() => Tool != null ? new Call { Via = "service", Tool = Tool, Params = Params } : new Call { Args = Args, Stdin = Stdin };
}

/// <summary>One kept answer (in the work folder, never in the repo).</summary>
public sealed class Kept
{
    public string Id { get; set; } = "";
    public string[] Args { get; set; } = [];
    public string? Stdin { get; set; }
    /// <summary>The call as it was made (a new tool's golden has no Args).</summary>
    public string Shown { get; set; } = "";
    /// <summary>The SQL of the call, when it has any: decides whether rows are compared as a set.</summary>
    public string? Sql { get; set; }
    public Answer Answer { get; set; } = new();
}

public sealed class RunInfo
{
    public string When { get; set; } = "";
    public string Set { get; set; } = "";
    public string Via { get; set; } = "";
    public bool Service { get; set; }
    public string Exe { get; set; } = "";
    public string Version { get; set; } = "";
    public string Mutagen { get; set; } = "";
    public string Data { get; set; } = "";
    public string Home { get; set; } = "";
    public int Commands { get; set; }
    public int Ran { get; set; }
    /// <summary>Ran and gave an answer: something printed that isn't an error.</summary>
    public int Answered { get; set; }
    public int NoAnswer { get; set; }
    public double Seconds { get; set; }
    public long MedianMs { get; set; }
    public long SlowestMs { get; set; }
    public string Slowest { get; set; } = "";
    public List<SkipInfo> Skipped { get; set; } = [];
    public long HomeDbBytes { get; set; }
    public int HomeDbFiles { get; set; }
    public long HomeBytes { get; set; }
    /// <summary>Every plugin in Data when the run was made: name, size and date. Tells a changed plugin from a changed SFSearch.</summary>
    public Dictionary<string, string> Plugins { get; set; } = [];
}

public sealed class SkipInfo
{
    public string Id { get; set; } = "";
    public string Command { get; set; } = "";
    public string[] Missing { get; set; } = [];
}

public sealed class ReplayResult
{
    public string Set = "", Via = "";
    public int Commands, Ran, Same, SameAsSet, Different, Skipped, NoBaseline;
    /// <summary>Answers that differ from the baseline in exactly the way replay\explained.json says they may.</summary>
    public int Explained;
    public double Seconds;
    public string Report = "";
    public string? Problem;
    public bool Pass => Problem == null && Different == 0 && NoBaseline == 0 && Ran > 0;
}

/// <summary>
/// One difference from the baseline that can't be avoided, written down with its reason (replay\explained.json).
/// It is narrow on purpose: the answer itself and the exit code must be the baseline's, every warning of the baseline
/// must still be there, and each warning that is new must match one of the patterns. Anything else is a difference.
/// </summary>
public sealed class ExplainedDifference
{
    public string Set { get; set; } = "";
    public string Id { get; set; } = "";
    public string Why { get; set; } = "";
    /// <summary>Patterns: each line only this run has must match one of them.</summary>
    public string[] OnlyThisRun { get; set; } = [];
}

public static class Replay
{
    static List<ExplainedDifference>? _explained;

    public static List<ExplainedDifference> Explained()
    {
        if (_explained != null) return _explained;
        string file = Path.Combine(Paths.Cases, "replay", "explained.json");
        return _explained = File.Exists(file) ? Json.Load<List<ExplainedDifference>>(file) : [];
    }

    /// <summary>The written-down reason when a difference is exactly the explained one, else null.</summary>
    static ExplainedDifference? ExplainedBy(string set, string id, Compare.Verdict v, Answer then, Answer now)
    {
        var e = Explained().FirstOrDefault(x => x.Set == set && x.Id == id);
        if (e == null || then.Code != now.Code || v.OnlyA.Count > 0 || v.OnlyB.Count == 0) return null;
        if (!Compare.Lines(then.Out).SequenceEqual(Compare.Lines(now.Out))) return null;
        return v.OnlyB.All(l => e.OnlyThisRun.Any(p => Regex.IsMatch(l, p))) ? e : null;
    }

    /// <summary>
    /// Every set in replay: one .jsonl file each, the goldens of the tools (replay\m5a-find.jsonl is the set "m5a-find").
    /// The 455 recorded past commands and 18 SQL files of the older commands are in mapping\ since SFSearch 2.0 removed
    /// those commands: 'sfsharness mapping' holds them to the calls that answer them now.
    /// </summary>
    public static string[] Sets => Directory.GetFiles(Path.Combine(Paths.Cases, "replay"), "*.jsonl").Select(f => Path.GetFileNameWithoutExtension(f))
        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();

    public static string SetFile(string set) => Path.Combine(Paths.Cases, "replay", set + ".jsonl");

    public static List<Recorded> Load(string set) =>
        File.ReadLines(SetFile(set)).Where(l => l.Trim().Length > 0).Select(l => JsonSerializer.Deserialize<Recorded>(l, Json.Read)!).ToList();

    // ---------------------------------------------------------------- Files a command names
    static readonly Regex PluginName = new(@"\.es[mpl]$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The plugin files a command names that aren't there any more: a path that doesn't exist, or a file name that
    /// isn't in Data. Such a command is reported apart, not counted as a failure.
    /// </summary>
    public static List<string> MissingFiles(string[] args, string data)
    {
        var missing = new List<string>();
        bool isQuery = args.Length > 0 && args[0].Equals("query", StringComparison.OrdinalIgnoreCase);
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal)) continue;
            if (isQuery && Regex.IsMatch(a, @"^\s*(select|with|create|--)\b", RegexOptions.IgnoreCase)) continue;
            if (a.Contains('\n')) continue;
            foreach (var part in a.Split(','))
            {
                string t = part.Trim();
                if (t.Length == 0) continue;
                if ((t.Contains('\\') || t.Contains('/')) && PluginName.IsMatch(t))
                {
                    if (!File.Exists(t)) missing.Add(t);
                    continue;
                }
                // "000CF2:x.esm 0003E6:y.esm": a list of FormKeys. Otherwise the whole text is one file name (names can hold spaces).
                var names = Regex.IsMatch(t, @"(^|\s)[0-9A-Fa-f]{1,8}:") ? t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => Regex.Replace(x, @"^[0-9A-Fa-f]{1,8}:", "")) : new[] { t };
                foreach (var name in names)
                    if (PluginName.IsMatch(name) && !name.Contains('*') && !name.Contains('?') && !File.Exists(Path.Combine(data, name))) missing.Add(name);
            }
        }
        return missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The plugins a command names (lower case), for picking the commands a small test Data folder can answer.</summary>
    public static List<string> NamedPlugins(string[] args)
    {
        var names = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal) || a.Contains('\n') || Regex.IsMatch(a, @"^\s*(select|with|create)\b", RegexOptions.IgnoreCase)) continue;
            foreach (var part in a.Split(','))
            {
                string t = part.Trim();
                var each = Regex.IsMatch(t, @"(^|\s)[0-9A-Fa-f]{1,8}:") ? t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => Regex.Replace(x, @"^[0-9A-Fa-f]{1,8}:", "")) : new[] { t };
                names.AddRange(each.Where(n => PluginName.IsMatch(n)).Select(n => Path.GetFileName(n).ToLowerInvariant()));
            }
        }
        return names.Distinct().ToList();
    }

    // ---------------------------------------------------------------- Running
    public static string Dir(string root, string via, string set) => Path.Combine(root, via, set);

    /// <summary>What the command becomes on a route. Piped SQL goes inline where there is no shell to cut it.</summary>
    public static Call ToCall(Recorded r, string via)
    {
        if (r.Tool != null) return new Call { Via = via == "mcp" ? "mcp" : "service", Tool = r.Tool, Params = r.Params };
        if (via == "cli") return new Call { Via = "cli", Args = r.Args, Stdin = r.Stdin };
        var args = r.Args.ToList();
        if (r.Stdin != null)
        {
            args.RemoveAll(a => a.Equals("--stdin", StringComparison.OrdinalIgnoreCase));
            args.Insert(Math.Min(2, args.Count), r.Stdin);
        }
        return new Call { Via = via, Args = args.ToArray() };
    }

    public static async Task<RunInfo> Run(Sut sut, string set, string via, bool service, string outRoot, Func<Recorded, bool>? only = null)
    {
        var all = Load(set);
        var commands = only == null ? all : all.Where(only).ToList();
        string dir = Dir(outRoot, via, set);
        if (!Paths.Under(dir, Paths.Work)) throw new InvalidOperationException($"Refused: answers are kept under the work folder, not in {dir}.");
        Directory.CreateDirectory(dir);
        // A whole run replaces what was kept. A run of some commands (--only) replaces just those, and keeps the run's own summary.
        bool partial = only != null && File.Exists(Path.Combine(dir, "run.json"));
        if (!partial) foreach (var old in Directory.GetFiles(dir, "*.json")) File.Delete(old);
        if (service || via != "cli") await sut.StartService();
        else if (sut.ServiceRunning) await sut.StopService();

        var info = new RunInfo
        {
            When = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Set = set, Via = via, Service = service || via != "cli", Exe = Paths.Exe, Data = sut.Data, Home = sut.Home,
            Commands = commands.Count, Version = (await Sut.RunProcess(Paths.Exe, ["version"], sut.Env(), timeoutMs: 60000)).Out.Trim(), Mutagen = MutagenVersion(),
            Plugins = PluginStamps(sut.Data),
        };
        var clock = Stopwatch.StartNew();
        var times = new List<(long Ms, string Id)>();
        int done = 0;
        foreach (var r in commands)
        {
            var missing = MissingFiles(r.Args, sut.Data);
            if (missing.Count > 0) { info.Skipped.Add(new SkipInfo { Id = r.Id, Command = r.AsShown().Show(), Missing = missing.ToArray() }); continue; }
            var call = ToCall(r, via);
            var a = await sut.Run(call);
            Json.Save(Path.Combine(dir, r.Id + ".json"), new Kept
            {
                Id = r.Id, Args = r.Args, Stdin = r.Stdin, Shown = call.Show(), Answer = a,
                Sql = r.Stdin ?? (r.Params != null && r.Params.TryGetPropertyValue("sql", out var sqlNode) && sqlNode is System.Text.Json.Nodes.JsonValue sv && sv.TryGetValue<string>(out var sqlText) ? sqlText : null),
            });
            info.Ran++;
            if (Answered(a)) info.Answered++; else info.NoAnswer++;
            times.Add((a.Ms, r.Id));
            if (++done % 50 == 0) Console.Error.WriteLine($"  [{set} via {via}] {done} of {commands.Count - info.Skipped.Count} run, {clock.Elapsed.TotalSeconds:0} s");
        }
        info.Seconds = Math.Round(clock.Elapsed.TotalSeconds, 1);
        if (times.Count > 0)
        {
            var sorted = times.OrderBy(t => t.Ms).ToList();
            info.MedianMs = sorted[sorted.Count / 2].Ms;
            info.SlowestMs = sorted[^1].Ms;
            info.Slowest = "#" + sorted[^1].Id;
        }
        (info.HomeDbBytes, info.HomeDbFiles, info.HomeBytes) = sut.HomeSize();
        Json.Save(Path.Combine(dir, partial ? "run-partial.json" : "run.json"), info);
        return info;
    }

    /// <summary>Something was printed that isn't an error. (Lint ends with exit code 1 when it finds problems: that is an answer.)</summary>
    public static bool Answered(Answer a) => a.Problem == null && a.Out.Trim().Length > 0 && !a.Out.TrimStart().StartsWith("Error", StringComparison.Ordinal);

    public static string MutagenVersion()
    {
        string deps = Path.Combine(Path.GetDirectoryName(Paths.Exe)!, "SFSearch.deps.json");
        if (!File.Exists(deps)) return "?";
        var m = Regex.Match(File.ReadAllText(deps), @"""Mutagen\.Bethesda\.Starfield/([^""]+)""");
        return m.Success ? m.Groups[1].Value : "?";
    }

    public static Dictionary<string, string> PluginStamps(string data) =>
        new DirectoryInfo(data).EnumerateFiles().Where(f => PluginName.IsMatch(f.Name)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(f => f.Name, f => $"{f.Length}|{f.LastWriteTimeUtc:yyyy-MM-ddTHH:mm:ssZ}", StringComparer.OrdinalIgnoreCase);

    public static string Summary(RunInfo i) =>
        $"{i.Set} via {i.Via}{(i.Service ? " (service running)" : " (no service)")}: {i.Commands} commands, {i.Ran} ran, {i.Answered} answered, " +
        $"{i.NoAnswer} ran without an answer (an error line, or nothing printed), {i.Skipped.Count} skipped (they name files that aren't there). " +
        $"{i.Seconds:0.0} s in all, median {i.MedianMs} ms, slowest {i.SlowestMs / 1000.0:0.0} s ({i.Slowest}). " +
        $"Test home: {i.HomeDbFiles} database(s), {i.HomeDbBytes / 1048576.0:N0} MB of databases, {i.HomeBytes / 1048576.0:N0} MB in all.";

    // ---------------------------------------------------------------- Comparing two kept runs
    public static ReplayResult CompareRuns(string set, string baseDir, string runDir, string nameA = "baseline", string nameB = "this run")
    {
        var res = new ReplayResult { Set = set };
        if (!File.Exists(Path.Combine(baseDir, "run.json"))) { res.Problem = $"no baseline in {baseDir}: take one with 'sfsharness replay baseline'"; return res; }
        var a = Json.Load<RunInfo>(Path.Combine(baseDir, "run.json"));
        var b = Json.Load<RunInfo>(Path.Combine(runDir, "run.json"));
        res.Via = b.Via; res.Commands = b.Commands; res.Ran = b.Ran; res.Skipped = b.Skipped.Count; res.Seconds = b.Seconds;
        var sb = new StringBuilder();
        sb.Append($"Replay '{set}': {nameB} ({b.Via}, {b.When}, SFSearch {Short(b.Version)}, Mutagen {b.Mutagen}) against {nameA} ({a.Via}, {a.When}, SFSearch {Short(a.Version)}, Mutagen {a.Mutagen}).\n");

        var changed = b.Plugins.Where(p => a.Plugins.TryGetValue(p.Key, out var s) && s != p.Value).Select(p => p.Key).ToList();
        var added = b.Plugins.Keys.Where(p => !a.Plugins.ContainsKey(p)).ToList();
        var gone = a.Plugins.Keys.Where(p => !b.Plugins.ContainsKey(p)).ToList();
        if (!string.Equals(a.Data, b.Data, StringComparison.OrdinalIgnoreCase)) sb.Append($"The Data folders differ: {a.Data} then, {b.Data} now ({b.Plugins.Count} plugins against {a.Plugins.Count}).\n");
        if (changed.Count > 0) sb.Append($"Plugins whose size or date changed since the baseline ({changed.Count}): {string.Join(", ", changed.Take(12))}{(changed.Count > 12 ? ", …" : "")}\n");
        if (string.Equals(a.Data, b.Data, StringComparison.OrdinalIgnoreCase))
        {
            if (added.Count > 0) sb.Append($"Plugins added since the baseline ({added.Count}): {string.Join(", ", added.Take(12))}{(added.Count > 12 ? ", …" : "")}\n");
            if (gone.Count > 0) sb.Append($"Plugins gone since the baseline ({gone.Count}): {string.Join(", ", gone.Take(12))}{(gone.Count > 12 ? ", …" : "")}\n");
        }

        // The MCP server gives a command's text, not its exit code. Across such routes the exit codes aren't compared.
        bool exitCodes = a.Via == b.Via || (a.Via != "mcp" && b.Via != "mcp");
        int exitOnly = 0;
        var differences = new List<string>();
        foreach (var file in Directory.GetFiles(runDir, "*.json").Where(f => Path.GetFileName(f) != "run.json").OrderBy(IdOrder))
        {
            var now = Json.Load<Kept>(file);
            string baseFile = Path.Combine(baseDir, Path.GetFileName(file));
            if (!File.Exists(baseFile)) { res.NoBaseline++; differences.Add($"NO BASELINE  #{now.Id}  {Compare.Cut(now.Shown, 300)}\n  the baseline skipped it or was taken without it\n"); continue; }
            var then = Json.Load<Kept>(baseFile);
            var v = Compare.Answers(now.Args, now.Sql, then.Answer, now.Answer, exitCodes, allNotes: now.Args.Length == 0);
            if (!exitCodes && then.Answer.Code != now.Answer.Code) exitOnly++;
            switch (v.Kind)
            {
                case Compare.Kind.Same: res.Same++; break;
                case Compare.Kind.SameAsSet: res.SameAsSet++; sb.Append($"  #{now.Id}: {v.Why}\n"); break;
                default:
                    if (ExplainedBy(set, now.Id, v, then.Answer, now.Answer) is { } known)
                    {
                        res.Explained++;
                        sb.Append($"  EXPLAINED  #{now.Id}  {Compare.Cut(now.Shown, 200)}\n    {known.Why}\n");
                        foreach (var l in v.OnlyB) sb.Append("    only this run: ").Append(Compare.Cut(l, 160)).Append('\n');
                        break;
                    }
                    res.Different++;
                    differences.Add(Compare.Describe(now.Id, now.Shown, v, then.Answer, now.Answer, nameA, nameB) + $"  both answers in full: {baseFile}\n                        {file}\n");
                    break;
            }
        }
        if (exitOnly > 0) sb.Append($"Not compared: the exit code. {exitOnly} command(s) end with another exit code on the {(a.Via == "mcp" ? b.Via : a.Via)} route than the MCP server shows (it passes on the text, not the code).\n");
        foreach (var d in differences) sb.Append(d);
        if (b.Skipped.Count > 0)
        {
            sb.Append($"Reported apart, not failures: {b.Skipped.Count} command(s) name files that aren't there any more.\n");
            foreach (var s in b.Skipped) sb.Append($"  #{s.Id}  {Compare.Cut(s.Command, 160)}\n      missing: {string.Join(", ", s.Missing)}\n");
        }
        sb.Append($"{(res.Different + res.NoBaseline == 0 && res.Ran > 0 ? "PASS" : "FAIL")}  replay '{set}': {b.Commands} commands, {res.Ran} ran, {res.Same + res.SameAsSet} equal ({res.Same} line for line, {res.SameAsSet} as sets), {(res.Explained > 0 ? $"{res.Explained} different in a way that is written down and explained, " : "")}{res.Different} different, {res.Skipped} skipped{(res.NoBaseline > 0 ? $", {res.NoBaseline} with no baseline" : "")}. {b.Seconds:0.0} s.\n");
        res.Report = sb.ToString();
        return res;
    }

    static string Short(string version) => version.Split('|')[0].Replace("SFSearch", "").Trim();

    static string IdOrder(string file)
    {
        string id = Path.GetFileNameWithoutExtension(file);
        return int.TryParse(id, out int n) ? n.ToString("D6") : "z" + id.PadLeft(8, '0');
    }
}
