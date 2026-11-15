using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SFSearch.Harness;

/// <summary>
/// The rules for calling two answers the same (ARCHITECTURE.md section 11, the golden replay):
///   - rows are compared as sets where the SQL has no ORDER BY;
///   - SFSearch's own counters (CachedRecordCount) and timing lines are left out;
///   - every other difference counts.
/// </summary>
public static class Compare
{
    public enum Kind { Same, SameAsSet, Different }

    public sealed record Verdict(Kind Kind, string Why, List<string> OnlyA, List<string> OnlyB);

    static readonly Regex Timing = new(@"\bin [0-9.,]+ ?(ms|s|min)\b", RegexOptions.Compiled);
    static readonly Regex SavedFile = new(@"[A-Za-z]:\\[^\n|]*\\Service\\results\\\d{8}_\d{6}_j[0-9a-f]+-\d+\.md", RegexOptions.Compiled);
    static readonly Regex JobNumber = new(@"\bj[0-9a-f]+-\d+\b", RegexOptions.Compiled);
    static readonly Regex Digits = new(@"\d[\d,]*", RegexOptions.Compiled);
    static readonly Regex TempStdin = new(@"stdin_\d+_\d+\.sql", RegexOptions.Compiled);
    static readonly string[] Progress = { "[loadorder]", "[session]", "[live]", "[lq]" };

    /// <summary>The answer's lines, with what legitimately differs between two runs taken out.</summary>
    public static List<string> Lines(string text)
    {
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (Progress.Any(p => line.StartsWith(p, StringComparison.Ordinal))) continue;
            line = Timing.Replace(line, "in T");
            line = SavedFile.Replace(line, "<saved file>");
            line = TempStdin.Replace(line, "<piped sql>");
            // SFSearch's own counter of what it has read so far: the name stays, the number goes
            if (line.Contains("CachedRecordCount", StringComparison.Ordinal)) line = Digits.Replace(line, "#");
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>The notes that are warnings or errors ("[sfs] WARNING: ...", "Error: ...", "SQL Error on ..."). Progress notes ("Reading Weapon from x.esm ...") are not part of an answer.</summary>
    public static List<string> Warnings(string notes) =>
        Lines(notes).Where(l => l.Contains("WARNING", StringComparison.OrdinalIgnoreCase) || l.Contains("Error", StringComparison.OrdinalIgnoreCase))
            .Select(l => JobNumber.Replace(l, "<job>")).Distinct().OrderBy(l => l, StringComparer.Ordinal).ToList();

    /// <summary>True when the command is SQL with no ORDER BY anywhere: its rows have no fixed order (trap 16).</summary>
    public static bool Unordered(string[] args, string? stdin)
    {
        // The SQL is given apart (piped in, or a new tool's "sql" argument), or it is one of a Query command's arguments
        if (stdin == null && (args.Length == 0 || !args[0].Equals("query", StringComparison.OrdinalIgnoreCase))) return false;
        string sql = stdin ?? string.Join("\n", args.Skip(1).Where(a => Regex.IsMatch(a, @"^\s*(--[^\n]*\n\s*)*(select|with|create)\b", RegexOptions.IgnoreCase)));
        if (sql.Length == 0) return false;
        return !Regex.IsMatch(sql, @"\border\s+by\b", RegexOptions.IgnoreCase);
    }

    /// <param name="exitCodes">False when one of the two routes can't give a command's exit code (the MCP server doesn't pass it on).</param>
    /// <param name="allNotes">A new tool's golden: every note counts, and the [source] line. Its notes are part of the
    /// answer's contract (problems lead them), where an old command's stderr is mostly progress.</param>
    public static Verdict Answers(string[] args, string? stdin, Answer a, Answer b, bool exitCodes = true, bool allNotes = false)
    {
        var la = Lines(a.Out); var lb = Lines(b.Out);
        var wa = allNotes ? Lines(a.Notes) : Warnings(a.Notes); var wb = allNotes ? Lines(b.Notes) : Warnings(b.Notes);
        string? exit = exitCodes && a.Code != b.Code ? $"exit code {a.Code} against {b.Code}" : null;
        bool warningsSame = wa.SequenceEqual(wb);
        if (la.SequenceEqual(lb) && warningsSame && exit == null) return new(Kind.Same, "", [], []);

        var onlyA = Minus(la, lb); var onlyB = Minus(lb, la);
        if (!warningsSame) { onlyA.AddRange(Minus(wa, wb).Select(l => "[notes] " + l)); onlyB.AddRange(Minus(wb, wa).Select(l => "[notes] " + l)); }
        if (exit != null || !warningsSame)
            return new(Kind.Different, exit ?? (allNotes ? "the notes differ" : "the warnings differ"), onlyA, onlyB);

        if (Unordered(args, stdin))
        {
            if (SameRows(args, a.Out, b.Out, la, lb)) return new(Kind.SameAsSet, "the same rows in another order (SQL with no ORDER BY)", [], []);
            // group_concat with no ORDER BY: the list inside a cell has no fixed order either
            string sql = stdin ?? string.Join(" ", args);
            if (sql.Contains("group_concat", StringComparison.OrdinalIgnoreCase) && Tokens(la).SequenceEqual(Tokens(lb)))
                return new(Kind.SameAsSet, "the same values, a group_concat list in another order (SQL with no ORDER BY)", [], []);
        }
        return new(Kind.Different, onlyA.Count + onlyB.Count == 0 ? "the same lines in another order" : "the answers differ", onlyA, onlyB);
    }

    /// <summary>Rows as a set: JSON output by its array's elements, anything else by its lines.</summary>
    static bool SameRows(string[] args, string outA, string outB, List<string> la, List<string> lb)
    {
        if (args.Any(x => x.Equals("--json", StringComparison.OrdinalIgnoreCase)))
        {
            var ja = JsonRows(outA); var jb = JsonRows(outB);
            if (ja != null && jb != null) return ja.SequenceEqual(jb);
        }
        return la.OrderBy(l => l, StringComparer.Ordinal).SequenceEqual(lb.OrderBy(l => l, StringComparer.Ordinal));
    }

    static List<string>? JsonRows(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            return doc.RootElement.EnumerateArray().Select(e => e.GetRawText()).OrderBy(s => s, StringComparer.Ordinal).ToList();
        }
        catch (JsonException) { return null; }
    }

    static List<string> Tokens(List<string> lines) =>
        lines.SelectMany(l => l.Split(',', '|')).Select(t => t.Trim()).Where(t => t.Length > 0).OrderBy(t => t, StringComparer.Ordinal).ToList();

    /// <summary>The lines of a that b doesn't have, counting repeats.</summary>
    static List<string> Minus(List<string> a, List<string> b)
    {
        var left = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var l in b) left[l] = left.GetValueOrDefault(l) + 1;
        var result = new List<string>();
        foreach (var l in a)
            if (left.TryGetValue(l, out int n) && n > 0) left[l] = n - 1; else result.Add(l);
        return result;
    }

    public static string Cut(string s, int max = 220) => s.Length > max ? s[..max] + "…" : s;

    /// <summary>One difference, with both answers, for the report.</summary>
    public static string Describe(string id, string shown, Verdict v, Answer a, Answer b, string nameA, string nameB, int linesEach = 8)
    {
        var sb = new StringBuilder();
        sb.Append($"DIFFERENT  #{id}  {Cut(shown, 300)}\n  {v.Why}\n");
        void Side(string name, List<string> only, Answer ans)
        {
            sb.Append($"  --- {name} (exit {ans.Code}, {Lines(ans.Out).Count} lines)\n");
            var show = only.Count > 0 ? only : Lines(ans.Out);
            foreach (var l in show.Take(linesEach)) sb.Append("    ").Append(Cut(l)).Append('\n');
            if (show.Count > linesEach) sb.Append($"    … {show.Count - linesEach} more line(s) only here\n");
            if (show.Count == 0) sb.Append("    (nothing)\n");
        }
        Side(nameA, v.OnlyA, a);
        Side(nameB, v.OnlyB, b);
        return sb.ToString();
    }
}
