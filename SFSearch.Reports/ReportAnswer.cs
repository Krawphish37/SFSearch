// The answer of a report: a structured object, and its text under the output contract of ARCHITECTURE.md section 7
// (the answer, then [more], [notes], [source], [cost]). Compact by default; "detail" prints every row.
// A long answer is cut between rows and says so, with the cursor that continues it. An answer with no rows says why.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace SFSearch.Reports;

/// <summary>Something the caller must be told as one line: <c>Error: what. Fix: fix.</c></summary>
public sealed class ReportException(string what, string? fix = null) : Exception(what)
{
    public string? Fix { get; } = fix;
    /// <summary>The line of the output contract.</summary>
    public string Line => "Error: " + Message.TrimEnd('.') + "." + (Fix == null ? "" : " Fix: " + Fix.TrimEnd('.') + ".");
}

public sealed class ReportTextOptions
{
    /// <summary>Every row, with nothing folded into "… N more".</summary>
    public bool Detail { get; init; }
    /// <summary>The most characters of rows on one page (about 6,000 tokens by default). The heading, notes and source come on top.</summary>
    public int MaxChars { get; init; } = 24000;
    /// <summary>The row to start at: the cursor a cut answer gave.</summary>
    public int Cursor { get; init; }
}

/// <summary>The text of an answer before it is cut into pages.</summary>
public sealed class ReportBody
{
    /// <summary>Printed on every page: what was asked and the summary.</summary>
    public List<string> Head { get; } = new();
    /// <summary>The rows: each may be several lines. Pages are cut between rows.</summary>
    public List<string> Rows { get; } = new();
    /// <summary>What a row is called in the [more] line.</summary>
    public string RowNoun { get; set; } = "rows";
    /// <summary>Why there are no rows, when there are none.</summary>
    public string? Empty { get; set; }
}

public abstract class ReportAnswer
{
    /// <summary>check, leveled, dialogue, sources.</summary>
    public abstract string Kind { get; }
    /// <summary>Warnings the reader must see.</summary>
    public List<string> Notes { get; } = new();
    /// <summary>Which copy of which plugins the answer was read from.</summary>
    public List<string> Sources { get; } = new();
    /// <summary>What was read to make the answer: each item names a thing read ("317 of 317 records of atom74.esm"),
    /// so that the cost line reads "[cost] 0.31 s; read A, B".</summary>
    public List<string> Read { get; } = new();
    public double Seconds { get; set; }
    /// <summary>Why the answer has no rows, when it has none.</summary>
    public string? EmptyReason { get; set; }

    protected abstract void Render(ReportBody body, ReportTextOptions options);
    protected abstract void Fill(JsonObject json);

    /// <summary>The answer as a structured object: the same parts as fields. Nothing is cut.</summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["kind"] = Kind };
        Fill(o);
        if (EmptyReason != null) o["empty"] = EmptyReason;
        o["notes"] = new JsonArray(Notes.Select(n => (JsonNode)n).ToArray());
        o["source"] = new JsonArray(Sources.Select(n => (JsonNode)n).ToArray());
        o["cost"] = new JsonObject { ["seconds"] = Math.Round(Seconds, 3), ["read"] = new JsonArray(Read.Select(n => (JsonNode)n).ToArray()) };
        return o;
    }

    public string ToText(ReportTextOptions? options = null) => ToText(options, out _);

    /// <param name="next">The cursor that continues a cut answer, or -1 when this page ends it.</param>
    public string ToText(ReportTextOptions? options, out int next)
    {
        options ??= new ReportTextOptions();
        var body = new ReportBody();
        Render(body, options);
        var sb = new StringBuilder();
        foreach (var h in body.Head) sb.Append(h).Append('\n');
        int start = Math.Clamp(options.Cursor, 0, body.Rows.Count), at = start, used = 0;
        if (start > 0) sb.Append($"(continued from {body.RowNoun.TrimEnd('s')} {start + 1:N0})\n");
        while (at < body.Rows.Count && (at == start || used + body.Rows[at].Length <= options.MaxChars))
        {
            sb.Append(body.Rows[at]).Append('\n');
            used += body.Rows[at].Length + 1;
            at++;
        }
        if (body.Rows.Count == 0 && (body.Empty ?? EmptyReason) != null) sb.Append(body.Empty ?? EmptyReason).Append('\n');
        next = at < body.Rows.Count ? at : -1;
        if (next >= 0) sb.Append($"[more] {at - start:N0} of {body.Rows.Count:N0} {body.RowNoun} shown ({start + 1:N0} to {at:N0}). Next: cursor \"{at}\".\n");
        foreach (var n in Notes) sb.Append("[notes] ").Append(n).Append('\n');
        if (Sources.Count > 0) sb.Append("[source] ").Append(string.Join("; ", Sources)).Append('\n');
        sb.Append("[cost] ").Append(Seconds.ToString(Seconds < 10 ? "F2" : "F1", CultureInfo.InvariantCulture)).Append(" s");
        if (Read.Count > 0) sb.Append("; read ").Append(string.Join(", ", Read));
        sb.Append('\n');
        return sb.ToString();
    }

    protected internal static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    protected internal static string Plural(long n, string one, string? many = null) => $"{N(n)} {(n == 1 ? one : many ?? one + "s")}";
    /// <summary>A cell of a Markdown table: no bar, no line break.</summary>
    protected internal static string Cell(string? s) => (s ?? "").Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
}
