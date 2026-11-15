using System.Text;
using System.Text.RegularExpressions;

namespace SFSearch.Harness;

/// <summary>
/// A Markdown table needs a separator row under its header (| :--- | ---: |), or it doesn't render as a table.
/// This looks at an answer's text for a table that has none. A table is a run of lines that begin and end with a
/// bar; the line under its first must be made of bars, dashes, colons and blanks only.
/// </summary>
public static class Tables
{
    static readonly Regex Rule = new(@"^\|[\s:|-]*-[\s:|-]*\|$", RegexOptions.Compiled);

    static bool IsRow(string line) => line.Length >= 2 && line[0] == '|' && line.TrimEnd().EndsWith('|');

    /// <summary>The header lines of the tables in a text that aren't followed by a separator row.</summary>
    public static List<string> Unruled(string text)
    {
        var bad = new List<string>();
        // An answer that is one JSON object (format json) holds no table to render
        if (text.TrimStart().StartsWith('{')) return bad;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!IsRow(lines[i]) || (i > 0 && IsRow(lines[i - 1]))) continue;
            // The first line of a table: a rule itself (a page that starts in the middle is no table start), or a header
            if (Rule.IsMatch(lines[i].TrimEnd())) continue;
            if (i + 1 >= lines.Length || !Rule.IsMatch(lines[i + 1].TrimEnd())) bad.Add(lines[i]);
        }
        return bad;
    }

    /// <summary>
    /// Every kept answer of one run of a set (a golden's folder): the answers with a table that has no separator row.
    /// </summary>
    public static (int Answers, int Tables, List<string> Problems) CheckRun(string runDir)
    {
        int answers = 0, tables = 0;
        var problems = new List<string>();
        if (!Directory.Exists(runDir)) return (0, 0, problems);
        foreach (var file in Directory.GetFiles(runDir, "*.json").Where(f => !Path.GetFileName(f).StartsWith("run", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var kept = Json.Load<Kept>(file);
            answers++;
            string text = kept.Answer.Out.Replace("\r\n", "\n");
            tables += text.Split('\n').Count(l => Rule.IsMatch(l.TrimEnd()));
            foreach (var header in Unruled(text))
                problems.Add($"#{kept.Id}  {Compare.Cut(kept.Shown, 160)}\n      a table with no separator row under its header: {Compare.Cut(header, 160)}");
        }
        return (answers, tables, problems);
    }
}

// ---------------------------------------------------------------- The polish cases
public sealed class PolishFile
{
    public string About { get; set; } = "";
    public List<PolishCase> Cases { get; set; } = [];
}

public sealed class PolishCase
{
    public int N { get; set; }
    public string Title { get; set; } = "";
    /// <summary>What the tools did before the polish pass, in one line.</summary>
    public string Before { get; set; } = "";
    public Form Form { get; set; } = new();
}

/// <summary>
/// The polish cases (every .json file in polish\): the polish pass's points (polish.json; one per point of
/// design\agent-first\notes\polish.md) and later fixes, written as data like the trap cases. Each makes the exact
/// calls of the point and says what must be true of the answers, so the fault can't come back unseen. The author
/// keeps a few more with the agent benchmark (a dev tool), in a file of their own that isn't in the public repository.
/// </summary>
public static class Polish
{
    public static IEnumerable<string> Files => Directory.GetFiles(Path.Combine(Paths.Cases, "polish"), "*.json").Order(StringComparer.OrdinalIgnoreCase);

    public static async Task<List<(PolishCase Case, FormResult Result)>> Run(Sut sut, Func<PolishCase, bool>? only = null, bool verbose = false)
    {
        var cases = Files.SelectMany(f => Json.Load<PolishFile>(f).Cases);
        var results = new List<(PolishCase, FormResult)>();
        foreach (var c in cases.Where(c => only?.Invoke(c) ?? true).OrderBy(c => c.N))
        {
            string fx = Paths.Out("fixtures", $"polish{c.N:D2}");
            var r = await Cases.Run(sut, c.Form, fx);
            results.Add((c, r));
            Console.WriteLine($"{Traps.Label(r.Status),-13} polish {c.N,2}  {c.Title}");
            if (r.Status != "pass") Console.WriteLine($"      {r.Detail}");
            if (verbose || r.Status is "fail" or "problem") Console.Write(Cases.Show(r, verbose ? 40 : 8));
        }
        return results;
    }

    public static List<string[]> Rows(List<(PolishCase Case, FormResult Result)> results) =>
        results.Select(x => new[]
        {
            $"Polish {x.Case.N}: {x.Case.Title}", Traps.Label(x.Result.Status),
            x.Result.Status == "pass" ? $"{x.Case.Form.Calls.Count} call(s), {x.Case.Form.PassIf.Sum(p => p.All.Count)} condition(s) hold" : x.Result.Detail,
        }).ToList();
}
