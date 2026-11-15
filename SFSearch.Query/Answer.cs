// What goes in and what comes out. The answer has the parts of the output contract (ARCHITECTURE.md section 7):
// the rows, [more] with a cursor, [notes], [source], [cost], or one error line with its fix.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SFSearch.Query;

public enum ScopeKind
{
    /// <summary>One or several plugins, each read as it is. The Plugin column tells them apart.</summary>
    Plugins,
    /// <summary>For each record, the version the game uses: the last loaded plugin that holds it.</summary>
    Winners,
    /// <summary>Every copy of each record in every loaded plugin, with LoadOrder and IsWinner.</summary>
    Versions,
}

public sealed class QueryRequest
{
    public required string Sql { get; init; }
    public ScopeKind Scope { get; init; } = ScopeKind.Plugins;
    /// <summary>For <see cref="ScopeKind.Plugins"/>: the plugins, by file name.</summary>
    public IReadOnlyList<string> Plugins { get; init; } = Array.Empty<string>();
    /// <summary>Run it even when the estimate is over the limit.</summary>
    public bool AllowHeavy { get; init; }
    /// <summary>Rows in one page of the answer.</summary>
    public int PageRows { get; init; } = 40;
    /// <summary>The cursor a former answer gave, to get the rows after it.</summary>
    public string? Cursor { get; init; }
    /// <summary>The text form of the answer stays under this many characters (about 6,000 tokens).</summary>
    public int MaxChars { get; init; } = 24000;
    /// <summary>The language of text columns. Null: the engine's.</summary>
    public string? Language { get; init; }
    /// <summary>Values for the parameters the SQL names (<c>:plugin</c>, <c>:record</c>), by name without the colon. They are
    /// bound, never pasted into the SQL. A value of null is SQL NULL. Added for stored queries (milestone M10).</summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } = new Dictionary<string, object?>();
    public CancellationToken Cancel { get; init; }
    /// <summary>A statement over Records or Links reads first the types it narrows to (or every type of the scope when
    /// it narrows to none). Stored queries read their types themselves (their reads line) and switch this off.</summary>
    internal bool NoAutoReads { get; init; }
}

/// <summary>One plugin an answer was read from: which copy of it.</summary>
public sealed record AnswerSource(string Plugin, long Bytes, string ContentKey)
{
    public string Text => $"{Plugin} {Size(Bytes)}, key {ContentKey[..Math.Min(8, ContentKey.Length)]}…";
    /// <summary>A size as every new tool writes it (<see cref="SFSearch.Records.Sizes"/>).</summary>
    public static string Size(long b) => SFSearch.Records.Sizes.Text(b);
}

/// <summary>What was read for an answer, and what it took.</summary>
public sealed class AnswerCost
{
    public double Seconds { get; set; }
    /// <summary>Seconds of the SQL itself, once its tables were there.</summary>
    public double QuerySeconds { get; set; }
    /// <summary>"Weapon (406) from Starfield.esm 0.26 s", one line per read.</summary>
    public List<string> Read { get; } = new();
    /// <summary>Record types the query used that were already in the scratch tables.</summary>
    public List<string> AlreadyRead { get; } = new();
    public long PeakWorkingSetMB { get; set; }
    /// <summary>Where the time went, step by step (the scope, opening the tables, the checks, the reads, the SQL).</summary>
    public List<string> Steps { get; } = new();

    public string Text
    {
        get
        {
            var parts = new List<string> { Seconds < 0.0995 ? $"{Seconds * 1000:F0} ms" : Seconds.ToString("F2", CultureInfo.InvariantCulture) + " s" };
            if (Read.Count > 0) parts.Add("read " + string.Join(", ", Read));
            if (AlreadyRead.Count > 0) parts.Add(string.Join(", ", AlreadyRead) + " already read");
            return string.Join("; ", parts);
        }
    }
}

/// <summary>The estimate given when a query isn't run because it would take too long or too much memory.</summary>
public sealed record HeavyEstimate(double Seconds, long MemoryMB, string What);

public sealed class QueryAnswer
{
    public List<string> Columns { get; } = new();
    /// <summary>The rows of this page.</summary>
    public List<object?[]> Rows { get; } = new();
    /// <summary>Every row the query gave, counted.</summary>
    public long TotalRows { get; set; }
    /// <summary>The place of this page's first row among all rows.</summary>
    public long Offset { get; set; }
    /// <summary>Set when there are rows after this page: pass it back as <see cref="QueryRequest.Cursor"/>.</summary>
    public string? Cursor { get; set; }
    public List<string> Notes { get; } = new();
    public List<AnswerSource> Sources { get; } = new();
    /// <summary>The load order the answer rests on, for the winners and versions scopes.</summary>
    public string? LoadOrder { get; set; }
    public AnswerCost Cost { get; } = new();
    /// <summary>Set when the query failed: what is wrong.</summary>
    public string? Error { get; set; }
    /// <summary>What to do about it.</summary>
    public string? Fix { get; set; }
    /// <summary>Set when nothing was run because the estimate is over the limit.</summary>
    public HeavyEstimate? NotRun { get; set; }
    /// <summary>True when the last statement gave a result (a SELECT), false for statements that only made temporary tables.</summary>
    public bool HasResult { get; set; }
    /// <summary>Which of the tables that aren't one record type's own (Records, Forms, Links) the statements read. SQLite
    /// itself resolved the names: a column or an alias called Records isn't the table.</summary>
    public List<string> SpecialTables { get; } = new();
    /// <summary>The record types the scratch tables of this scope hold once the query has run: what Records and Links
    /// can show. Empty when none has been read yet.</summary>
    public List<string> TypesRead { get; } = new();
    public bool Ok => Error == null && NotRun == null;

    static string Cell(object? v) => v switch
    {
        null => "",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        byte[] b => "0x" + Convert.ToHexString(b),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    /// <summary>The answer as text under the output contract.</summary>
    public string ToText(int maxChars = 24000, int maxCell = 600)
    {
        var sb = new StringBuilder();
        if (Error != null)
        {
            sb.Append("Error: ").Append(Error.TrimEnd('.'));
            if (!Error.EndsWith('?')) sb.Append('.');
            if (Fix != null) sb.Append(" Fix: ").Append(Fix.TrimEnd('.')).Append('.');
            sb.Append('\n');
            Tail(sb, 0, false);
            return sb.ToString();
        }
        if (NotRun != null)
        {
            sb.Append("Not run. ").Append(NotRun.What).Append('\n');
            Tail(sb, 0, false);
            return sb.ToString();
        }
        int shown = 0; bool cutCell = false;
        if (HasResult)
        {
            sb.Append("| ").Append(string.Join(" | ", Columns)).Append(" |\n");
            sb.Append('|').Append(string.Concat(Columns.Select(_ => "---|"))).Append('\n');
            foreach (var row in Rows)
            {
                var line = new StringBuilder("| ");
                for (int i = 0; i < row.Length; i++)
                {
                    string cell = Cell(row[i]).Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
                    if (cell.Length > maxCell) { cell = cell[..maxCell] + $"… ({cell.Length:N0} characters)"; cutCell = true; }
                    line.Append(cell).Append(i + 1 < row.Length ? " | " : " |");
                }
                // The page ends early when the text would grow past its limit; the cursor then starts at the first row left out
                if (shown > 0 && sb.Length + line.Length > maxChars) break;
                sb.Append(line).Append('\n');
                shown++;
            }
        }
        Tail(sb, shown, cutCell);
        return sb.ToString();
    }

    void Tail(StringBuilder sb, int shown, bool cutCell)
    {
        if (HasResult && Ok)
        {
            long after = Offset + shown;
            if (after < TotalRows)
                sb.Append($"[more] {(Offset == 0 ? $"{shown:N0}" : $"rows {Offset + 1:N0} to {after:N0}")} of {TotalRows:N0} rows shown. Next: cursor \"{CursorAt(after)}\".\n");
        }
        var notes = new List<string>(Notes);
        if (cutCell) notes.Add("Long values were cut to fit; ask for that column alone, or use the JSON form of the answer, to get them whole.");
        foreach (var n in notes) sb.Append("[notes] ").Append(n).Append('\n');
        if (Sources.Count > 0 || LoadOrder != null)
        {
            var parts = Sources.Take(6).Select(s => s.Text).ToList();
            if (Sources.Count > 6) parts.Add($"and {Sources.Count - 6:N0} more plugins ({AnswerSource.Size(Sources.Skip(6).Sum(s => s.Bytes))})");
            if (LoadOrder != null) parts.Add(LoadOrder);
            sb.Append("[source] ").Append(string.Join("; ", parts)).Append('\n');
        }
        sb.Append("[cost] ").Append(Cost.Text).Append('\n');
    }

    internal string CursorBase = "";
    /// <summary>The cursor for the rows from this offset on (public since milestone M8: the tool prints its own [more] line).</summary>
    public string CursorAt(long offset) => $"{CursorBase}{offset}";

    /// <summary>The same parts as a structured object.</summary>
    public JsonObject ToJson()
    {
        static JsonNode? Val(object? v) => v switch
        {
            null => null,
            long l => JsonValue.Create(l),
            int i => JsonValue.Create(i),
            double d => double.IsFinite(d) ? JsonValue.Create(d) : JsonValue.Create(d.ToString(CultureInfo.InvariantCulture)),
            string s => JsonValue.Create(s),
            byte[] b => JsonValue.Create("0x" + Convert.ToHexString(b)),
            _ => JsonValue.Create(Cell(v)),
        };
        var o = new JsonObject();
        if (Error != null) o["error"] = new JsonObject { ["what"] = Error, ["fix"] = Fix };
        if (NotRun != null) o["notRun"] = new JsonObject { ["what"] = NotRun.What, ["seconds"] = Math.Round(NotRun.Seconds, 1), ["memoryMB"] = NotRun.MemoryMB, ["allow"] = "allow_heavy=true" };
        if (HasResult && Ok)
        {
            o["columns"] = new JsonArray(Columns.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
            o["rows"] = new JsonArray(Rows.Select(r => (JsonNode?)new JsonArray(r.Select(Val).ToArray())).ToArray());
            o["totalRows"] = TotalRows;
            o["offset"] = Offset;
            if (Offset + Rows.Count < TotalRows) o["more"] = new JsonObject { ["shown"] = Rows.Count, ["of"] = TotalRows, ["cursor"] = CursorAt(Offset + Rows.Count) };
        }
        o["notes"] = new JsonArray(Notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        o["source"] = new JsonObject
        {
            ["plugins"] = new JsonArray(Sources.Select(s => (JsonNode?)new JsonObject { ["plugin"] = s.Plugin, ["bytes"] = s.Bytes, ["contentKey"] = s.ContentKey }).ToArray()),
            ["loadOrder"] = LoadOrder,
        };
        o["cost"] = new JsonObject
        {
            ["seconds"] = Math.Round(Cost.Seconds, 4), ["querySeconds"] = Math.Round(Cost.QuerySeconds, 4),
            ["read"] = new JsonArray(Cost.Read.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
            ["alreadyRead"] = new JsonArray(Cost.AlreadyRead.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
        };
        return o;
    }

    public string ToJsonText() => ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

/// <summary>An error the caller must be told in one line, with what to do about it.</summary>
public sealed class QueryException(string what, string? fix = null) : Exception(what)
{
    public string? Fix { get; } = fix;
}
