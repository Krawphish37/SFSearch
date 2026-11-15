// A stored query (milestone M10, ARCHITECTURE.md section 8): one .sql file with a short header in SQL comments.
//
//   -- name: flst
//   -- answers: The FormLists of a plugin, or one FormList item by item.
//   -- param: plugin | text | needed | The plugin to read, by file name.
//   -- param: record | text | optional | One FormList, by EditorID or FormKey.
//   -- param: min | number | optional, default 2 | …
//   -- scope: plugins
//   -- reads: every type with VirtualMachineAdapter      (only for queries over Records or Links)
//   -- column: Idx | The item's place in the list, from 0.
//   -- example: plugin=Starfield.esm record=SomeList
//   -- table: The lists                                  (before each SELECT: its heading)
//   -- when: record, pattern                             (run this statement only when one of them is given)
//   -- unless: record                                    (skip this statement when one of them is given)
//   SELECT … WHERE EditorID = :record;
//
// Any other comment line of the header is free text, shown by "show". The format is described for users in
// queries\README.md. This file only reads it and says what is wrong with a header.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SFSearch.Query;

public enum StoredParamKind
{
    /// <summary>Bound as text.</summary>
    Text,
    /// <summary>Bound as a whole number or a decimal number.</summary>
    Number,
    /// <summary>A part of a name, where <c>*</c> stands for any text and <c>?</c> for one character. Bound as the
    /// pattern for <c>LIKE :name ESCAPE '\'</c>: it matches anywhere in the value.</summary>
    Pattern,
}

public sealed record StoredParam(string Name, StoredParamKind Kind, bool Needed, string? Default, string What);

public sealed record StoredColumn(string Name, string What);

/// <summary>One statement of a stored query, with the lines written above it.</summary>
public sealed class StoredStatement
{
    public required string Sql { get; init; }
    /// <summary>The heading of the table a SELECT gives (<c>-- table:</c>).</summary>
    public string? Heading { get; init; }
    /// <summary>Run only when one of these parameters is given.</summary>
    public IReadOnlyList<string> When { get; init; } = Array.Empty<string>();
    /// <summary>Skip when one of these parameters is given.</summary>
    public IReadOnlyList<string> Unless { get; init; } = Array.Empty<string>();
    /// <summary>True for a statement that gives rows (SELECT, WITH, VALUES); false for one that makes a temporary table.</summary>
    public bool GivesRows { get; init; }
    /// <summary>The parameters the statement names.</summary>
    public IReadOnlyList<string> Parameters { get; init; } = Array.Empty<string>();

    public bool Runs(Func<string, bool> given) => (When.Count == 0 || When.Any(given)) && !Unless.Any(given);
}

/// <summary>What a query over Records or Links needs read before it runs (<c>-- reads:</c>).</summary>
public sealed class StoredReads
{
    /// <summary>Every record type.</summary>
    public bool Every { get; init; }
    /// <summary>Every record type that has this field (<c>every type with VirtualMachineAdapter</c>).</summary>
    public string? WithField { get; init; }
    /// <summary>These record types.</summary>
    public IReadOnlyList<string> Types { get; init; } = Array.Empty<string>();
    public string Text => WithField != null ? $"every type with {WithField}" : Every ? "every type" : string.Join(", ", Types);
}

public sealed class StoredQuery
{
    /// <summary>The name asked for: letters, digits, <c>-</c> and <c>_</c>.</summary>
    public string Name { get; init; } = "";
    /// <summary>Where it was found: <c>shipped</c>, <c>user</c>, or an add-on's name.</summary>
    public string Source { get; init; } = "";
    public string FullName => Source.Length == 0 ? Name : $"{Source}.{Name}";
    public string? File { get; init; }
    /// <summary>One line: what it answers.</summary>
    public string Answers { get; init; } = "";
    /// <summary>The free lines of the header.</summary>
    public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();
    public IReadOnlyList<StoredParam> Params { get; init; } = Array.Empty<StoredParam>();
    /// <summary>The scope it runs in when the caller names none. Null when the header has no valid scope line.</summary>
    public ScopeKind? Scope { get; init; }
    /// <summary><c>scope: plugins with masters</c>: one plugin is named, and its masters are read with it, so the
    /// query can look into the records the plugin points at. The SQL keeps to the plugin with <c>Plugin = :plugin</c>.</summary>
    public bool WithMasters { get; init; }
    public StoredReads? Reads { get; init; }
    public IReadOnlyList<StoredColumn> Columns { get; init; } = Array.Empty<StoredColumn>();
    public IReadOnlyList<string> Examples { get; init; } = Array.Empty<string>();
    public IReadOnlyList<StoredStatement> Statements { get; init; } = Array.Empty<StoredStatement>();
    /// <summary>The whole file.</summary>
    public string Text { get; init; } = "";
    /// <summary>What is wrong with the header, in plain lines. Empty when it is in order.</summary>
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();

    public StoredParam? Param(string name) => Params.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    static readonly Regex HeaderLine = new(@"^--\s*(name|answers|param|scope|reads|column|example|table|when|unless)\s*:\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex NameRule = new(@"^[A-Za-z0-9][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant);

    public static bool ValidName(string name) => NameRule.IsMatch(name) && name.Length <= 60;

    /// <summary>Reads a stored query from its text. It never throws for a faulty file: <see cref="Problems"/> says what is wrong.</summary>
    public static StoredQuery Parse(string text, string source, string? file, string? fileName = null)
    {
        var problems = new List<string>();
        string name = "", answers = "";
        var details = new List<string>();
        var pars = new List<StoredParam>();
        var columns = new List<StoredColumn>();
        var examples = new List<string>();
        ScopeKind? scope = null; bool scopeSeen = false, withMasters = false;
        StoredReads? reads = null;

        // The header: the comment lines before the first line of SQL
        var lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (!line.StartsWith("--", StringComparison.Ordinal)) break;
            var m = HeaderLine.Match(line);
            if (!m.Success)
            {
                string free = line.TrimStart('-').Trim();
                if (free.Length > 0) details.Add(free);
                continue;
            }
            string key = m.Groups[1].Value.ToLowerInvariant(), value = m.Groups[2].Value.Trim();
            switch (key)
            {
                case "name":
                    if (name.Length > 0) problems.Add("the header has two name lines");
                    name = value;
                    break;
                case "answers":
                    answers = answers.Length == 0 ? value : answers + " " + value;
                    break;
                case "param":
                {
                    var parts = value.Split('|').Select(p => p.Trim()).ToArray();
                    if (parts.Length < 4) { problems.Add($"the param line \"{value}\" needs four parts: name | text, number or pattern | needed or optional | what it is"); break; }
                    string pname = parts[0].TrimStart(':');
                    if (!NameRule.IsMatch(pname)) { problems.Add($"\"{parts[0]}\" isn't a parameter name (letters, digits and _)"); break; }
                    if (pars.Any(p => p.Name.Equals(pname, StringComparison.OrdinalIgnoreCase))) { problems.Add($"the parameter {pname} is declared twice"); break; }
                    if (!Enum.TryParse<StoredParamKind>(parts[1], true, out var kind) || int.TryParse(parts[1], out _))
                    { problems.Add($"the parameter {pname} has the kind \"{parts[1]}\"; the kinds are text, number and pattern"); break; }
                    string need = parts[2]; string? def = null; bool needed;
                    var dm = Regex.Match(need, @"^(needed|optional)\s*(?:,\s*default\s+(.*))?$", RegexOptions.IgnoreCase);
                    if (!dm.Success) { problems.Add($"the parameter {pname} says \"{need}\" where it must say needed, optional, or optional, default <value>"); break; }
                    needed = dm.Groups[1].Value.Equals("needed", StringComparison.OrdinalIgnoreCase);
                    if (dm.Groups[2].Success) def = dm.Groups[2].Value.Trim().Trim('\'', '"');
                    if (needed && def != null) problems.Add($"the parameter {pname} is needed and has a default: one of the two");
                    if (def != null && kind == StoredParamKind.Number && !double.TryParse(def, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                        problems.Add($"the parameter {pname} is a number and its default \"{def}\" isn't one");
                    string what = string.Join(" | ", parts.Skip(3));
                    if (what.Length == 0) problems.Add($"the parameter {pname} doesn't say what it is");
                    pars.Add(new StoredParam(pname, kind, needed, def, what));
                    break;
                }
                case "scope":
                {
                    scopeSeen = true;
                    string word = value.Split(' ', '(', ',')[0];
                    if (Enum.TryParse<ScopeKind>(word, true, out var s) && !int.TryParse(word, out _)) scope = s;
                    else problems.Add($"the scope \"{value}\" isn't one of plugins, plugins with masters, winners, versions");
                    withMasters = scope == ScopeKind.Plugins && Regex.IsMatch(value, @"^plugins\s+with\s+masters\b", RegexOptions.IgnoreCase);
                    break;
                }
                case "reads":
                {
                    var wm = Regex.Match(value, @"^every\s+type(?:\s+with\s+([A-Za-z0-9_.]+))?$", RegexOptions.IgnoreCase);
                    if (wm.Success) reads = new StoredReads { Every = !wm.Groups[1].Success, WithField = wm.Groups[1].Success ? wm.Groups[1].Value : null };
                    else
                    {
                        var types = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (types.Length == 0 || types.Any(t => !NameRule.IsMatch(t))) problems.Add($"the reads line \"{value}\" must say every type, every type with <Field>, or a list of record types");
                        else reads = new StoredReads { Types = types };
                    }
                    break;
                }
                case "column":
                {
                    int bar = value.IndexOf('|');
                    string cname = (bar < 0 ? value : value[..bar]).Trim(), what = bar < 0 ? "" : value[(bar + 1)..].Trim();
                    if (cname.Length == 0 || what.Length == 0) { problems.Add($"the column line \"{value}\" needs a name and what it holds: Name | what it is"); break; }
                    if (columns.Any(c => c.Name.Equals(cname, StringComparison.OrdinalIgnoreCase))) { problems.Add($"the column {cname} is described twice"); break; }
                    columns.Add(new StoredColumn(cname, what));
                    break;
                }
                case "example":
                    if (value.Length > 0) examples.Add(value);
                    break;
                // table, when, unless belong to the statement below them
            }
        }

        if (name.Length == 0) problems.Add("the header has no name line (-- name: my-query)");
        else if (!ValidName(name)) problems.Add($"the name \"{name}\" may hold letters, digits, - and _ only, and no more than 60 characters");
        else if (fileName != null && !Path.GetFileNameWithoutExtension(fileName).Equals(name, StringComparison.OrdinalIgnoreCase))
            problems.Add($"the header names the query \"{name}\" and the file is {fileName}: they must be the same");
        if (answers.Length == 0) problems.Add("the header doesn't say what the query answers (-- answers: one line)");
        if (!scopeSeen) problems.Add("the header has no scope line (-- scope: plugins, winners or versions)");

        // The statements, each with the comment lines above it
        var statements = new List<StoredStatement>();
        foreach (string part in SplitWithComments(text))
        {
            var toks = SqlText.Tokenize(part);
            if (toks.Count == 0) continue;
            string? heading = null; var when = new List<string>(); var unless = new List<string>();
            foreach (string raw in part.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (!line.StartsWith("--", StringComparison.Ordinal)) break;
                var m = HeaderLine.Match(line);
                if (!m.Success) continue;
                string value = m.Groups[2].Value.Trim();
                switch (m.Groups[1].Value.ToLowerInvariant())
                {
                    case "table": heading = value; break;
                    case "when": when.AddRange(Names(value)); break;
                    case "unless": unless.AddRange(Names(value)); break;
                }
            }
            bool gives = toks[0].Is("SELECT") || toks[0].Is("WITH") || toks[0].Is("VALUES");
            string sql = part[toks[0].Start..].Trim();
            statements.Add(new StoredStatement { Sql = sql, Heading = heading, When = when, Unless = unless, GivesRows = gives, Parameters = QueryEngine.ParametersOf(toks) });
        }
        if (statements.Count == 0) problems.Add("the file has no SQL statement");
        else if (!statements.Any(s => s.GivesRows)) problems.Add("no statement gives rows: a stored query needs at least one SELECT");

        // Parameters: what the SQL names must be declared, and the other way round
        var used = statements.SelectMany(s => s.Parameters).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (string u in used)
            if (!pars.Any(p => p.Name.Equals(u, StringComparison.OrdinalIgnoreCase)))
                problems.Add($"the SQL names the parameter :{u}, and the header has no param line for it");
        foreach (var p in pars)
        {
            if (used.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) continue;
            // plugin names the scope and type narrows what is read: both may go unnamed in the SQL
            bool scopeParam = p.Name.Equals("plugin", StringComparison.OrdinalIgnoreCase);
            bool readsParam = p.Name.Equals("type", StringComparison.OrdinalIgnoreCase) && reads != null;
            if (!scopeParam && !readsParam) problems.Add($"the parameter {p.Name} is declared and the SQL never names :{p.Name}");
        }
        foreach (var s in statements)
            foreach (string w in s.When.Concat(s.Unless))
                if (!pars.Any(p => p.Name.Equals(w, StringComparison.OrdinalIgnoreCase)))
                    problems.Add($"a when or unless line names \"{w}\", which isn't a parameter of this query");
        if (scope == ScopeKind.Plugins && !pars.Any(p => p.Name.Equals("plugin", StringComparison.OrdinalIgnoreCase)))
            problems.Add("a query with the scope plugins needs the parameter plugin (-- param: plugin | text | needed | The plugin to read)");

        return new StoredQuery
        {
            Name = name.Length > 0 ? name : Path.GetFileNameWithoutExtension(fileName ?? ""), Source = source, File = file, Answers = answers, Details = details,
            Params = pars, Scope = scope, WithMasters = withMasters, Reads = reads, Columns = columns, Examples = examples, Statements = statements, Text = text, Problems = problems,
        };
    }

    static IEnumerable<string> Names(string value) => value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.TrimStart(':'));

    /// <summary>The statements of a text, each with the comments that stand before it (SqlText.SplitStatements cuts
    /// at the same places; this keeps the text between them whole).</summary>
    static List<string> SplitWithComments(string sql)
    {
        var parts = new List<string>();
        int from = 0;
        foreach (var t in SqlText.Tokenize(sql))
        {
            if (!t.IsPunct(";")) continue;
            parts.Add(sql[from..t.Start]);
            from = t.End;
        }
        parts.Add(sql[from..]);
        return parts;
    }

    /// <summary>The header as a reader sees it: what it answers, its parameters, scope and columns.</summary>
    public string Describe(bool withSql)
    {
        var sb = new StringBuilder();
        sb.Append(FullName).Append(": ").Append(Answers).Append('\n');
        foreach (string d in Details) sb.Append("  ").Append(d).Append('\n');
        sb.Append("Scope when none is named: ").Append(Scope?.ToString().ToLowerInvariant() ?? "(none)").Append(WithMasters ? " with masters (one plugin, read together with its masters)" : "").Append('\n');
        if (Reads != null) sb.Append("Reads first: ").Append(Reads.Text).Append('\n');
        sb.Append(Params.Count == 0 ? "Parameters: none\n" : "Parameters:\n");
        foreach (var p in Params)
            sb.Append("  ").Append(p.Name).Append(" (").Append(p.Kind.ToString().ToLowerInvariant()).Append(", ")
                .Append(p.Needed ? "needed" : p.Default != null ? $"optional, default {p.Default}" : "optional").Append("): ").Append(p.What).Append('\n');
        var tables = Statements.Where(s => s.GivesRows).ToList();
        if (tables.Count > 1 || tables.Any(t => t.Heading != null))
        {
            sb.Append("Tables:\n");
            foreach (var t in tables)
                sb.Append("  ").Append(t.Heading ?? "(no heading)")
                    .Append(t.When.Count > 0 ? $" (only with {string.Join(" or ", t.When)})" : "").Append(t.Unless.Count > 0 ? $" (not with {string.Join(" or ", t.Unless)})" : "").Append('\n');
        }
        sb.Append("Columns:\n");
        foreach (var c in Columns) sb.Append("  ").Append(c.Name).Append(": ").Append(c.What).Append('\n');
        foreach (string e in Examples) sb.Append("Example: ").Append(Name).Append(' ').Append(e).Append('\n');
        if (File != null) sb.Append("File: ").Append(File).Append('\n');
        foreach (string p in Problems) sb.Append("Problem: ").Append(p).Append('\n');
        if (withSql)
        {
            sb.Append("SQL:\n");
            foreach (var s in Statements) sb.Append(s.Sql).Append(";\n");
        }
        return sb.ToString();
    }
}

public sealed partial class QueryEngine
{
    /// <summary>The parameters a tokenized statement names, without their marks.</summary>
    internal static IReadOnlyList<string> ParametersOf(List<Tok> toks) => Session.ParameterNames(toks);
}
