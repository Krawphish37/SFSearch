using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SFSearch.Harness;

/// <summary>
/// A test written as data: files to prepare, calls to make, and what must be true of the answers. The trap cases
/// (traps\traps.json) and the polish cases (polish\*.json) are this, and so are the author's agent benchmark's truth checks.
/// </summary>
public sealed class Form
{
    /// <summary>Files to make first, under the work folder (copies of plugins from Data, with or without a change).</summary>
    public List<Fixture> Fixtures { get; set; } = [];
    /// <summary>Tools the form needs, for calls that don't name a tool themselves (a command line run through a shell). Missing: "not built yet".</summary>
    public List<string> Needs { get; set; } = [];
    public List<Call> Calls { get; set; } = [];
    /// <summary>The form passes when every condition of any one of these holds.</summary>
    public List<PassIf> PassIf { get; set; } = [];
}

public sealed class Fixture
{
    /// <summary>The file to copy. {data} is the Data folder; {fx} is this case's own folder.</summary>
    public string Copy { get; set; } = "";
    /// <summary>Where to, relative to the case's own folder.</summary>
    public string To { get; set; } = "";
    /// <summary>Optional: every place this text stands in the file (ASCII) is overwritten by With, which must be as long.</summary>
    public string? Replace { get; set; }
    public string? With { get; set; }
}

public sealed class PassIf
{
    /// <summary>What kind of answer this is: "clear error", "clear warning", "right value".</summary>
    public string Name { get; set; } = "";
    public List<Condition> All { get; set; } = [];
}

public sealed class Condition
{
    /// <summary>Which call's answer, counting from 1.</summary>
    public int Call { get; set; } = 1;
    /// <summary>out (the answer), notes (the warnings beside it) or all (both).</summary>
    public string Part { get; set; } = "all";
    /// <summary>A regular expression the part must match. (?m) for line starts.</summary>
    public string? Matches { get; set; }
    public string? NotMatches { get; set; }
    /// <summary>The answer's lines must equal this other call's (after the replay's normalising).</summary>
    public int? SameAsCall { get; set; }
    /// <summary>The answer's lines must differ from this other call's.</summary>
    public int? DiffersFromCall { get; set; }
    /// <summary>"zero" or "nonzero".</summary>
    public string? Exit { get; set; }
    /// <summary>The exact exit code the call must end with (0 answered, 1 a failure of what was asked, 2 an error, 7 not run, 8 no memory).</summary>
    public int? Code { get; set; }
    /// <summary>True: every table in the part must have a separator row under its header.</summary>
    public bool? TablesRuled { get; set; }
}

public sealed class FormResult
{
    /// <summary>pass | fail | not built yet | problem</summary>
    public string Status = "";
    public string Detail = "";
    public List<(Call Call, Answer Answer)> Answers = [];
    public long Ms;
}

public static class Cases
{
    static readonly TimeSpan RegexTime = TimeSpan.FromSeconds(5);

    static void Prepare(Sut sut, Form form, string fx)
    {
        if (form.Fixtures.Count == 0) return;
        if (!Paths.Under(fx, Paths.Work)) throw new InvalidOperationException($"Refused: fixtures go under the work folder, not {fx}.");
        Directory.CreateDirectory(fx);
        foreach (var f in form.Fixtures)
        {
            string from = sut.Fill(f.Copy, fx), to = Path.GetFullPath(Path.Combine(fx, f.To));
            if (!Paths.Under(to, fx)) throw new InvalidOperationException($"Refused: {to} is outside the case's folder.");
            if (!File.Exists(from)) throw new FileNotFoundException($"The fixture's source isn't there: {from}");
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            byte[] bytes = File.ReadAllBytes(from);
            if (f.Replace != null)
            {
                if (f.With == null || f.With.Length != f.Replace.Length) throw new InvalidDataException($"Fixture {f.To}: 'with' must be as long as 'replace'.");
                int hits = TestData.ReplaceAscii(bytes, f.Replace, f.With);
                if (hits == 0) throw new InvalidDataException($"Fixture {f.To}: '{f.Replace}' isn't in {from}.");
            }
            // Same bytes already there: leave the file (and its date) alone, so SFSearch doesn't read it again for nothing
            if (File.Exists(to) && File.ReadAllBytes(to).AsSpan().SequenceEqual(bytes)) continue;
            File.WriteAllBytes(to, bytes);
        }
    }

    public static async Task<FormResult> Run(Sut sut, Form form, string fx)
    {
        var res = new FormResult();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var tool in form.Needs)
            if ((await sut.Run(new Call { Via = "service", Tool = tool, Params = [] }, fx, 60000)).NotBuilt) { res.Status = "not built yet"; res.Detail = "no tool named " + tool; return res; }
        try { Prepare(sut, form, fx); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { res.Status = "problem"; res.Detail = "fixture: " + e.Message; return res; }
        foreach (var c in form.Calls)
        {
            var a = await sut.Run(c, fx, 600000);
            res.Answers.Add((c, a));
        }
        res.Ms = clock.ElapsedMilliseconds;
        if (res.Answers.Any(x => x.Answer.NotBuilt)) { res.Status = "not built yet"; res.Detail = "no tool named " + string.Join(", ", res.Answers.Where(x => x.Answer.NotBuilt).Select(x => x.Call.Tool).Distinct()); return res; }
        if (res.Answers.FirstOrDefault(x => x.Answer.Problem != null) is { Call: not null } bad) { res.Status = "problem"; res.Detail = bad.Answer.Problem!; return res; }
        var why = new List<string>();
        foreach (var alt in form.PassIf)
        {
            string? failed = null;
            foreach (var cond in alt.All)
            {
                failed = Check(cond, res.Answers);
                if (failed != null) break;
            }
            if (failed == null) { res.Status = "pass"; res.Detail = alt.Name; return res; }
            why.Add($"not '{alt.Name}': {failed}");
        }
        res.Status = "fail";
        res.Detail = string.Join("; ", why);
        return res;
    }

    static string? Check(Condition c, List<(Call Call, Answer Answer)> answers)
    {
        if (c.Call < 1 || c.Call > answers.Count) return $"the case has no call {c.Call}";
        var a = answers[c.Call - 1].Answer;
        string text = (c.Part.ToLowerInvariant() switch { "out" => a.Out, "notes" => a.Notes, _ => a.All }).Replace("\r\n", "\n");
        if (c.Matches != null && !Regex.IsMatch(text, c.Matches, RegexOptions.None, RegexTime)) return $"call {c.Call}'s {Part(c)} has nothing like /{c.Matches}/";
        if (c.NotMatches != null && Regex.Match(text, c.NotMatches, RegexOptions.None, RegexTime) is { Success: true } m) return $"call {c.Call}'s {Part(c)} has \"{Compare.Cut(m.Value.Trim(), 80)}\"";
        if (c.SameAsCall is int s)
        {
            if (s < 1 || s > answers.Count) return $"the case has no call {s}";
            if (!Compare.Lines(text).SequenceEqual(Compare.Lines(PartOf(answers[s - 1].Answer, c.Part)))) return $"call {c.Call} and call {s} answer differently";
        }
        if (c.DiffersFromCall is int d)
        {
            if (d < 1 || d > answers.Count) return $"the case has no call {d}";
            if (Compare.Lines(text).SequenceEqual(Compare.Lines(PartOf(answers[d - 1].Answer, c.Part)))) return $"call {c.Call} and call {d} give the same answer";
        }
        if (c.Exit == "zero" && a.Code != 0) return $"call {c.Call} ended with exit code {a.Code}";
        if (c.Exit == "nonzero" && a.Code == 0) return $"call {c.Call} ended with exit code 0";
        if (c.Code is int code && a.Code != code) return $"call {c.Call} ended with exit code {a.Code}, not {code}";
        if (c.TablesRuled == true && Tables.Unruled(text) is { Count: > 0 } unruled) return $"call {c.Call}'s {Part(c)} has a table with no separator row under \"{Compare.Cut(unruled[0], 80)}\"";
        return null;
    }

    static string Part(Condition c) => c.Part.ToLowerInvariant() switch { "out" => "answer", "notes" => "notes", _ => "answer and notes" };
    static string PartOf(Answer a, string part) => (part.ToLowerInvariant() switch { "out" => a.Out, "notes" => a.Notes, _ => a.All }).Replace("\r\n", "\n");

    /// <summary>The calls and what each answered, cut short, for a failing case's report.</summary>
    public static string Show(FormResult r, int lines = 6)
    {
        var sb = new StringBuilder();
        int n = 0;
        foreach (var (call, a) in r.Answers)
        {
            sb.Append($"      call {++n}: {Compare.Cut(call.Show(), 260)}\n");
            var shown = Compare.Lines(a.All);
            foreach (var l in shown.Take(lines)) sb.Append("        | ").Append(Compare.Cut(l, 200)).Append('\n');
            if (shown.Count > lines) sb.Append($"        | … {shown.Count - lines} more line(s)\n");
            if (shown.Count == 0) sb.Append($"        | (nothing, exit code {a.Code})\n");
        }
        return sb.ToString();
    }
}

// ---------------------------------------------------------------- The trap cases
public sealed class TrapFile
{
    public string About { get; set; } = "";
    public List<Trap> Traps { get; set; } = [];
}

public sealed class Trap
{
    public int N { get; set; }
    public string Title { get; set; } = "";
    /// <summary>What the design promises: impossible, clear error or clear warning (ARCHITECTURE.md section 11).</summary>
    public string Design { get; set; } = "";
    /// <summary>What today's SFSearch does, in one line.</summary>
    public string TodayShows { get; set; } = "";
    /// <summary>The case through today's commands. Run until the new form's tools exist.</summary>
    public Form? Today { get; set; }
    /// <summary>The case through the new tools (ARCHITECTURE.md section 7). "Not built yet" until they exist.</summary>
    public Form? New { get; set; }
}

public sealed class TrapResult
{
    public Trap Trap = new();
    public FormResult? Today, New;
    /// <summary>pass | fail | not built yet | problem: the new form's result once its tools exist, else today's.</summary>
    public string Status = "";
    public string Detail = "";
}

public static class Traps
{
    public static string File => Path.Combine(Paths.Cases, "traps", "traps.json");

    public static async Task<List<TrapResult>> Run(Sut sut, Func<Trap, bool>? only = null, bool verbose = false)
    {
        var file = Json.Load<TrapFile>(File);
        var results = new List<TrapResult>();
        foreach (var t in file.Traps.Where(t => only?.Invoke(t) ?? true).OrderBy(t => t.N))
        {
            string fx = Paths.Out("fixtures", $"trap{t.N:D2}");
            var r = new TrapResult { Trap = t };
            if (t.New != null) r.New = await Cases.Run(sut, t.New, fx);
            bool newBuilt = r.New != null && r.New.Status != "not built yet";
            if (t.Today != null && !newBuilt) r.Today = await Cases.Run(sut, t.Today, fx);
            if (newBuilt) { r.Status = r.New!.Status; r.Detail = "new tools: " + r.New.Detail; }
            else if (r.Today != null) { r.Status = r.Today.Status; r.Detail = "today's commands: " + r.Today.Detail; }
            else { r.Status = "not built yet"; r.Detail = r.New?.Detail ?? "no case"; }
            results.Add(r);
            Console.WriteLine($"{Label(r.Status),-13} trap {t.N,2}  {t.Title}");
            Console.WriteLine($"      design: {t.Design}. {r.Detail}");
            if (r.New is { Status: "not built yet" } && r.Today != null) Console.WriteLine($"      new tools: not built yet ({string.Join("; ", t.New!.Calls.Select(c => Compare.Cut(c.Show(), 150)))})");
            if (verbose || r.Status is "fail" or "problem") Console.Write(Cases.Show(newBuilt ? r.New! : r.Today ?? r.New!));
        }
        return results;
    }

    public static string Label(string status) => status switch { "pass" => "PASS", "fail" => "FAIL", "not built yet" => "NOT BUILT YET", _ => "PROBLEM" };
}
