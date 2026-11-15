// The answer of a comparison: a structured object, and its text under the output contract of ARCHITECTURE.md
// section 7 (the answer, then [more], [notes], [source], [cost]). Compact by default; "detail" prints every field.
// A long answer is cut at a row and says so, with the cursor that continues it.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace SFSearch.Compare;

public sealed class TextOptions
{
    /// <summary>Every difference on a line of its own, with both values, and what differs without being a value.</summary>
    public bool Detail { get; init; }
    /// <summary>The most characters of rows on one page (about 6,000 tokens by default). The heading, notes and source come on top.</summary>
    public int MaxChars { get; init; } = 24000;
    /// <summary>The row to start at: the cursor a cut answer gave.</summary>
    public int Cursor { get; init; }
    /// <summary>Compact mode: the most characters of one value.</summary>
    public int MaxValueChars { get; init; } = 100;
    /// <summary>Compact mode: the most entries or differences printed for one field.</summary>
    public int MaxPerField { get; init; } = 4;
}

/// <summary>The text of an answer before it is cut into pages.</summary>
public sealed class Body
{
    /// <summary>Printed on every page: what was compared and the summary.</summary>
    public List<string> Head { get; } = new();
    /// <summary>The rows: each may be several lines. Pages are cut between rows.</summary>
    public List<string> Rows { get; } = new();
    /// <summary>What a row is called in the [more] line.</summary>
    public string RowNoun { get; set; } = "rows";
}

public abstract class CompareAnswer
{
    /// <summary>versions, records, plugin_vs_masters, plugins, patch_vs_reference.</summary>
    public abstract string Kind { get; }
    /// <summary>Warnings the reader must see.</summary>
    public List<string> Notes { get; } = new();
    /// <summary>Which copy of which plugins the answer was read from.</summary>
    public List<string> Sources { get; } = new();
    /// <summary>What was read to make the answer ("1,204 records of darkstarpatch_x.esm against their masters").</summary>
    public List<string> Read { get; } = new();
    public double Seconds { get; set; }
    /// <summary>True when the comparison gives a failing result: a difference outside the fields that were allowed to differ.</summary>
    public bool Failed { get; protected set; }

    protected abstract void Render(Body body, TextOptions options);
    protected abstract void Fill(JsonObject json);

    /// <summary>The answer as a structured object: the same parts as fields. Nothing is cut.</summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["kind"] = Kind, ["failed"] = Failed };
        Fill(o);
        o["notes"] = new JsonArray(Notes.Select(n => (JsonNode)n).ToArray());
        o["source"] = new JsonArray(Sources.Select(n => (JsonNode)n).ToArray());
        o["cost"] = new JsonObject { ["seconds"] = Math.Round(Seconds, 3), ["read"] = new JsonArray(Read.Select(n => (JsonNode)n).ToArray()) };
        return o;
    }

    /// <summary>The answer as text under the output contract.</summary>
    public string ToText(TextOptions? options = null) => ToText(options, out _);

    /// <param name="next">The cursor that continues a cut answer, or -1 when this page ends it.</param>
    public string ToText(TextOptions? options, out int next)
    {
        options ??= new TextOptions();
        var body = new Body();
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
        next = at < body.Rows.Count ? at : -1;
        if (next >= 0) sb.Append($"[more] {at - start:N0} of {body.Rows.Count:N0} {body.RowNoun} shown ({start + 1:N0} to {at:N0}). Next: cursor \"{at}\".\n");
        foreach (var n in Notes) sb.Append("[notes] ").Append(n).Append('\n');
        if (Sources.Count > 0) sb.Append("[source] ").Append(string.Join("; ", Sources)).Append('\n');
        sb.Append("[cost] ").Append(Seconds.ToString(Seconds < 10 ? "F2" : "F1", CultureInfo.InvariantCulture)).Append(" s");
        if (Read.Count > 0) sb.Append("; read ").Append(string.Join(", ", Read));
        sb.Append('\n');
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Shared text of differences
    protected static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    protected static string Plural(long n, string one, string? many = null) => $"{N(n)} {(n == 1 ? one : many ?? one + "s")}";

    /// <summary>The name of one of a record's own fields: the readable name, with the Mutagen name and the subrecord beside it.</summary>
    public static string Head(FieldDiff d) => Head(d.Readable, d.Top, d.TopSignature);

    public static string Head(string readable, string name, string? signature)
    {
        bool same = string.Equals(readable.Replace(" ", ""), name, StringComparison.OrdinalIgnoreCase);
        string inner = same ? signature ?? "" : signature == null ? name : name + ", " + signature;
        return inner.Length == 0 ? readable : $"{readable} ({inner})";
    }

    static string Short(string? s, int max) => s == null ? "(not there)" : s.Length <= max ? s : s[..max] + "…";

    /// <summary>One difference as "a → b", or "+ b" and "− a" for something only one side has.</summary>
    public static string Arrow(FieldDiff d, int max, bool entry = false) => d.Change switch
    {
        Change.OnlyA => entry ? "− " + Short(d.A, max) : Short(d.A, max) + " → (not there)",
        Change.OnlyB => entry ? "+ " + Short(d.B, max) : "(not there) → " + Short(d.B, max),
        Change.Order => "the same entries in another order",
        _ => Short(d.A, max) + " → " + Short(d.B, max),
    };

    /// <summary>The differences of one record as lines. Compact: one line for each of the record's own fields.
    /// Detail: one line for each difference, then what differs without being a value. A field that holds the same value
    /// written another way is listed too, on a line that says so.</summary>
    public static List<string> FieldLines(RecordDiff diff, TextOptions o, string indent = "  ")
    {
        var lines = new List<string>();
        if (diff.DifferentTypes)
        {
            lines.Add($"{indent}They are records of different types: {diff.SignatureA} {diff.TypeA} and {diff.SignatureB} {diff.TypeB}. Fields of different types aren't compared.");
            return lines;
        }
        int max = o.Detail ? int.MaxValue : o.MaxValueChars;
        foreach (var group in diff.Values.GroupBy(d => d.Top))
        {
            var all = group.ToList();
            var first = all[0];
            // Every list under this field that was paired, and how
            var hows = diff.Lists.Where(l => l.Path == first.Top || l.Path.StartsWith(first.Top + ".", StringComparison.Ordinal) || l.Path.StartsWith(first.Top + "[", StringComparison.Ordinal))
                .Select(l => l.How).Distinct().ToList();
            string head = Head(first) + (hows.Count > 0 ? $" [{(hows[0].StartsWith("links", StringComparison.Ordinal) || hows[0].StartsWith("whole", StringComparison.Ordinal) ? "" : "matched ")}{string.Join(", ", hows)}]" : "");
            if (o.Detail)
            {
                if (all.Count == 1 && first.Path == first.Top) { lines.Add($"{indent}{head}: {Arrow(first, max)}{Note(first)}"); continue; }
                lines.Add($"{indent}{head}: {Plural(all.Count, "difference")}");
                foreach (var d in all) lines.Add($"{indent}  {Where(d)}{Arrow(d, max, entry: true)}{Note(d)}");
                continue;
            }
            if (all.Count == 1 && first.Path == first.Top) { lines.Add($"{indent}{head}: {Arrow(first, max)}"); continue; }
            var parts = all.Take(o.MaxPerField).Select(d => Entry(d) ?? Where(d) + Arrow(d, Math.Max(40, max / 2), entry: true)).ToList();
            if (all.Count > o.MaxPerField) parts.Add($"… {N(all.Count - o.MaxPerField)} more");
            lines.Add($"{indent}{head}: {string.Join("; ", parts)}");
        }
        // The same value written another way: still listed, under a heading that says so
        foreach (var group in diff.Rewritten.Where(d => diff.RewrittenFields.Contains(d.Top)).GroupBy(d => d.Top))
        {
            var first = group.First();
            if (o.Detail) continue; // detail lists each one below
            string what = first.Class == DiffClass.Layout ? "how long a data block is written" : first.Note ?? "a text held another way";
            lines.Add($"{indent}{Head(first)}: the same value, written another way ({what})");
        }
        if (diff.PartialFormA != diff.PartialFormB && diff.Values.Any(d => d.Change is Change.OnlyA or Change.OnlyB))
            lines.Add($"{indent}[note] {(diff.PartialFormB ? "b" : "a")} is a partial form (record flag 0x4000): it holds only part of the record. Fields it leaves out are shown as (not there); the game keeps the other version's values for them.");
        if (o.Detail)
            foreach (var d in diff.NotValues)
                lines.Add($"{indent}[{d.Class switch { DiffClass.Header => "not a value: the record header", DiffClass.Layout => "the same value, written another way: how long a data block is written", _ => "the same value, written another way" }}] {d.Shown}: {Arrow(d, max)}{Note(d)}");
        return lines;

        // Compact: an entry of a list paired by a key, only on one side, is named by its key alone ("+ [Item X]")
        static string? Entry(FieldDiff d)
        {
            if (d.Element != null || d.Change is not (Change.OnlyA or Change.OnlyB) || !d.Shown.EndsWith(']')) return null;
            int open = d.Shown.LastIndexOf('[');
            string name = d.Shown[(open + 1)..^1];
            // Paired by position (no "key=" in the place): the entry itself says more than its number
            if (open < 0 || name.Length == 0 || !d.Path[(d.Path.LastIndexOf('[') + 1)..].Contains('=')) return null;
            string before = d.Shown[..open].StartsWith(d.Top, StringComparison.Ordinal) ? d.Shown[..open][d.Top.Length..].TrimStart('.') : d.Shown[..open];
            return $"{(d.Change == Change.OnlyA ? "−" : "+")} {before}[{name}]";
        }
        static string Where(FieldDiff d)
        {
            if (d.Path == d.Top) return "";
            string rest = d.Shown.StartsWith(d.Top, StringComparison.Ordinal) ? d.Shown[d.Top.Length..].TrimStart('.') : d.Shown;
            return rest.Length == 0 ? "" : rest + ": ";
        }
        static string Note(FieldDiff d) => d.Note == null ? "" : $"   [{d.Note}]";
    }

    /// <summary>The fields that differ as one short text: "Keywords (+2 −1), AttackDamage". Fields that hold the same
    /// value written another way follow, under a heading that says so.</summary>
    public static string FieldNames(RecordDiff diff)
    {
        if (diff.DifferentTypes) return $"another type ({diff.SignatureA} {diff.TypeA} and {diff.SignatureB} {diff.TypeB})";
        var parts = new List<string>();
        foreach (var g in diff.Values.GroupBy(d => d.Top))
        {
            int minus = g.Count(d => d.Change == Change.OnlyA && (d.Path != d.Top || d.Element != null));
            int plus = g.Count(d => d.Change == Change.OnlyB && (d.Path != d.Top || d.Element != null));
            int other = g.Count() - plus - minus;
            string counts = plus + minus == 0 ? "" : " (" + string.Join(" ", new[] { plus > 0 ? "+" + plus : null, minus > 0 ? "−" + minus : null, other > 0 ? "~" + other : null }.Where(x => x != null)) + ")";
            parts.Add(g.Key + counts);
        }
        var rewritten = diff.RewrittenFields;
        string same = rewritten.Count == 0 ? "" : $"{(parts.Count == 0 ? "" : "; ")}the same value, written another way: {string.Join(", ", rewritten)}";
        return parts.Count == 0 && same.Length == 0 ? "nothing" : string.Join(", ", parts) + same;
    }
}
