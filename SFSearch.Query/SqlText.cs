// A small reader of SQL text: words, strings and punctuation with comments left out. It is not a SQL parser. SQLite
// itself decides which tables and columns a statement names (see QueryEngine.Probe); this only splits statements,
// finds text literals and their neighbours, and looks for ORDER BY.
using System.Text;

namespace SFSearch.Query;

enum TokKind { Word, QuotedName, String, Number, Punct }

readonly record struct Tok(TokKind Kind, string Text, int Start, int End, int Depth)
{
    public bool Is(string word) => Kind == TokKind.Word && Text.Equals(word, StringComparison.OrdinalIgnoreCase);
    public bool IsPunct(string p) => Kind == TokKind.Punct && Text == p;
    public bool IsName => Kind is TokKind.Word or TokKind.QuotedName;
}

static class SqlText
{
    /// <summary>The tokens of a SQL text. <c>Text</c> of a string or quoted name is its content, unquoted.</summary>
    public static List<Tok> Tokenize(string sql)
    {
        var list = new List<Tok>();
        int depth = 0, i = 0, n = sql.Length;
        while (i < n)
        {
            char c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && i + 1 < n && sql[i + 1] == '-') { while (i < n && sql[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < n && sql[i + 1] == '*') { int e = sql.IndexOf("*/", i + 2, StringComparison.Ordinal); i = e < 0 ? n : e + 2; continue; }
            int start = i;
            if (c == '\'' || c == '"' || c == '`')
            {
                var sb = new StringBuilder();
                i++;
                while (i < n)
                {
                    if (sql[i] == c) { if (i + 1 < n && sql[i + 1] == c) { sb.Append(c); i += 2; continue; } i++; break; }
                    sb.Append(sql[i++]);
                }
                list.Add(new Tok(c == '\'' ? TokKind.String : TokKind.QuotedName, sb.ToString(), start, i, depth));
                continue;
            }
            if (c == '[')
            {
                int e = sql.IndexOf(']', i);
                if (e < 0) e = n - 1;
                list.Add(new Tok(TokKind.QuotedName, sql[(i + 1)..e], start, e + 1, depth));
                i = e + 1;
                continue;
            }
            if (char.IsLetter(c) || c == '_' || c == '$' && i + 1 < n && char.IsLetter(sql[i + 1]) || c == '@' || c == ':' && i + 1 < n && char.IsLetter(sql[i + 1]))
            {
                i++;
                while (i < n && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$')) i++;
                list.Add(new Tok(TokKind.Word, sql[start..i], start, i, depth));
                continue;
            }
            if (char.IsDigit(c) || c == '.' && i + 1 < n && char.IsDigit(sql[i + 1]))
            {
                i++;
                while (i < n && (char.IsLetterOrDigit(sql[i]) || sql[i] == '.' || (sql[i] is '+' or '-') && (sql[i - 1] is 'e' or 'E'))) i++;
                list.Add(new Tok(TokKind.Number, sql[start..i], start, i, depth));
                continue;
            }
            if (c == '(') { list.Add(new Tok(TokKind.Punct, "(", start, ++i, depth)); depth++; continue; }
            if (c == ')') { depth = Math.Max(0, depth - 1); list.Add(new Tok(TokKind.Punct, ")", start, ++i, depth)); continue; }
            // Two-character operators
            if (i + 1 < n)
            {
                string two = sql.Substring(i, 2);
                if (two is "<>" or "!=" or "<=" or ">=" or "==" or "||" or "->" or "<<" or ">>")
                {
                    if (two == "->" && i + 2 < n && sql[i + 2] == '>') { list.Add(new Tok(TokKind.Punct, "->>", start, i += 3, depth)); continue; }
                    list.Add(new Tok(TokKind.Punct, two, start, i += 2, depth));
                    continue;
                }
            }
            list.Add(new Tok(TokKind.Punct, c.ToString(), start, ++i, depth));
        }
        return list;
    }

    /// <summary>
    /// The names a statement gives its own WITH parts: <c>WITH a AS (…), b(x, y) AS (…)</c> gives a and b. A name
    /// counts when it follows WITH, RECURSIVE or a comma and is followed by AS ( or by a column list and AS (.
    /// </summary>
    public static HashSet<string> WithNames(List<Tok> toks)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i + 2 < toks.Count; i++)
        {
            if (!toks[i].IsName || !(toks[i - 1].Is("WITH") || toks[i - 1].Is("RECURSIVE") || toks[i - 1].IsPunct(","))) continue;
            int j = i + 1;
            if (toks[j].IsPunct("("))
            {
                // a column list: to its closing bracket
                int level = 0;
                for (; j < toks.Count; j++)
                {
                    if (toks[j].IsPunct("(")) level++;
                    else if (toks[j].IsPunct(")") && --level == 0) { j++; break; }
                }
            }
            if (j + 1 < toks.Count && toks[j].Is("AS") && (toks[j + 1].IsPunct("(") || toks[j + 1].Is("MATERIALIZED") || toks[j + 1].Is("NOT"))) names.Add(toks[i].Text);
        }
        return names;
    }

    /// <summary>The names that stand where a table stands: after FROM or JOIN, and after each comma of a FROM list. A bracket there is a subquery, not a name.</summary>
    public static List<string> TableNames(List<Tok> toks)
    {
        var names = new List<string>();
        for (int i = 0; i + 1 < toks.Count; i++)
        {
            if (!(toks[i].Is("FROM") || toks[i].Is("JOIN"))) continue;
            int j = i + 1;
            while (j < toks.Count && toks[j].IsName)
            {
                // schema.name: the name is the last part
                if (j + 2 < toks.Count && toks[j + 1].IsPunct(".") && toks[j + 2].IsName) j += 2;
                names.Add(toks[j].Text);
                j++;
                if (j < toks.Count && toks[j].Is("AS")) j++;
                if (j < toks.Count && toks[j].IsName && !IsClauseWord(toks[j])) j++;   // the alias
                if (j < toks.Count && toks[j].IsPunct(",") && toks[i].Is("FROM")) { j++; continue; }
                break;
            }
        }
        return names;
    }

    static bool IsClauseWord(Tok t) => t.Kind == TokKind.Word && (t.Is("WHERE") || t.Is("JOIN") || t.Is("LEFT") || t.Is("INNER") || t.Is("CROSS") || t.Is("NATURAL") || t.Is("ON") || t.Is("USING")
        || t.Is("GROUP") || t.Is("ORDER") || t.Is("LIMIT") || t.Is("UNION") || t.Is("EXCEPT") || t.Is("INTERSECT") || t.Is("HAVING") || t.Is("WINDOW") || t.Is("RIGHT") || t.Is("FULL") || t.Is("OUTER"));

    /// <summary>The statements of a SQL text, cut at each <c>;</c> that is outside strings, names and comments.</summary>
    public static List<string> SplitStatements(string sql)
    {
        var parts = new List<string>();
        int from = 0;
        foreach (var t in Tokenize(sql))
        {
            if (!t.IsPunct(";")) continue;
            string s = sql[from..t.Start].Trim();
            if (HasContent(s)) parts.Add(s);
            from = t.End;
        }
        string last = sql[from..].Trim();
        if (HasContent(last)) parts.Add(last);
        return parts;
    }

    static bool HasContent(string s) => Tokenize(s).Count > 0;

    /// <summary>True when the statement's outermost SELECT has an ORDER BY.</summary>
    public static bool HasTopLevelOrderBy(List<Tok> toks)
    {
        for (int i = 0; i + 1 < toks.Count; i++)
            if (toks[i].Depth == 0 && toks[i].Is("ORDER") && toks[i + 1].Is("BY")) return true;
        return false;
    }

    public static string Literal(string s) => "'" + s.Replace("'", "''") + "'";
    public static string Name(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}

/// <summary>Nearest names, for "did you mean": the one way of choosing them that describe and the record layer use too
/// (<see cref="SFSearch.SchemaGen.NearNames"/>).</summary>
static class Nearest
{
    public static string Norm(string s) => SFSearch.SchemaGen.NearNames.Norm(s);
    public static List<T> Pick<T>(string asked, IEnumerable<T> names, Func<T, IEnumerable<string>> texts, int take = 4, bool always = true)
        => SFSearch.SchemaGen.NearNames.Pick(asked, names, texts, take, always);
}
