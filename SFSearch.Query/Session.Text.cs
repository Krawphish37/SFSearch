// Text in another language than the query's: text(Name, 'ja') on a typed view's text column, and the language names
// text() and name() are given. (Fix A1, 2026-11-12: text(Name,'ja') gave the English name with no word, because the
// view's Name column is already one language's plain text and text() gave a plain text back as it was.)
using System.Text;

namespace SFSearch.Query;

public sealed partial class QueryEngine
{
    sealed partial class Session
    {
        static readonly HashSet<string> NotAlias = new(StringComparer.OrdinalIgnoreCase)
        {
            "WHERE", "JOIN", "LEFT", "RIGHT", "FULL", "INNER", "OUTER", "CROSS", "NATURAL", "ON", "USING", "GROUP", "ORDER", "LIMIT",
            "UNION", "EXCEPT", "INTERSECT", "HAVING", "WINDOW", "AS", "SELECT", "FROM", "VALUES", "INDEXED", "NOT", "AND", "OR", "SET", "WITH",
        };

        /// <summary>The calls of a function in a statement: where its name is, and each argument's tokens.</summary>
        static IEnumerable<(int Name, List<(int From, int To)> Args, int Close)> Calls(List<Tok> toks, params string[] functions)
        {
            for (int i = 0; i + 1 < toks.Count; i++)
            {
                if (toks[i].Kind != TokKind.Word || !functions.Any(f => toks[i].Is(f)) || !toks[i + 1].IsPunct("(")) continue;
                if (i > 0 && toks[i - 1].IsPunct(".")) continue;
                int depth = toks[i + 1].Depth, from = i + 2, close = -1;
                var args = new List<(int, int)>();
                for (int k = i + 2; k < toks.Count; k++)
                {
                    if (toks[k].IsPunct(")") && toks[k].Depth == depth) { if (k > from) args.Add((from, k)); close = k; break; }
                    if (toks[k].IsPunct(",") && toks[k].Depth == depth + 1) { args.Add((from, k)); from = k + 1; }
                }
                if (close > 0) yield return (i, args, close);
            }
        }

        /// <summary>A language written into the SQL (text(x, 'xx'), name(fk, 'xx')) that isn't one stops the query
        /// before anything is read, with the languages there are.</summary>
        void CheckLanguageLiterals(List<Tok> toks)
        {
            foreach (var (name, args, _) in Calls(toks, "text", "name"))
            {
                if (args.Count != 2 || args[1].To - args[1].From != 1) continue;
                var lit = toks[args[1].From];
                if (lit.Kind != TokKind.String || TextLanguages.TryName(lit.Text, out _)) continue;
                throw new QueryException($"'{lit.Text}' in {toks[name].Text.ToLowerInvariant()}(…, '{lit.Text}') isn't a language. Starfield's text comes in {TextLanguages.ShippedText}",
                    "give one of those names or codes, e.g. text(Json -> '$.Name', 'ja')");
            }
        }

        /// <summary>
        /// text(Name, 'ja') where Name is a text column of a typed view (or of Records): the column is already the
        /// query's language as plain text, so the call is made to read the translated value from the view's Json
        /// instead (sfs_text(w.Json -> '$.Name', 'ja'), which also takes off the Creation Kit's tag as the column does).
        /// Null when nothing is to be changed. Only a call whose first argument is the column itself, written
        /// <c>Name</c> or <c>w.Name</c>, is changed, and only when it is plain which view the column is of.
        /// </summary>
        string? RewriteTextCalls(string sql, List<Tok> toks)
        {
            var calls = Calls(toks, "text").Where(c => c.Args.Count == 2 && c.Args[0].To - c.Args[0].From is 1 or 3).ToList();
            if (calls.Count == 0) return null;

            // The views the statement names, and the names it gives them (FROM Weapon w, JOIN Npc AS n)
            var byAlias = new Dictionary<string, (ViewSpec? View, bool Records)>(StringComparer.OrdinalIgnoreCase);
            var named = new List<(ViewSpec? View, bool Records, string Qualifier)>();
            for (int i = 0; i < toks.Count; i++)
            {
                var t = toks[i];
                if (!t.IsName || i > 0 && toks[i - 1].IsPunct(".") || i + 1 < toks.Count && (toks[i + 1].IsPunct("(") || toks[i + 1].IsPunct("."))) continue;
                bool records = t.Text.Equals("Records", StringComparison.OrdinalIgnoreCase);
                var view = records ? null : _e.Views.Find(t.Text);
                if (view == null && !records) continue;
                // Only where a table stands: after FROM, JOIN or a comma of a FROM list
                if (i == 0 || !(toks[i - 1].Is("FROM") || toks[i - 1].Is("JOIN") || toks[i - 1].IsPunct(","))) continue;
                string qualifier = t.Text;
                int a = i + 1;
                if (a < toks.Count && toks[a].Is("AS")) a++;
                if (a < toks.Count && toks[a].IsName && !NotAlias.Contains(toks[a].Text)) qualifier = toks[a].Text;
                byAlias[qualifier] = (view, records);
                named.Add((view, records, qualifier));
            }
            if (named.Count == 0) return null;

            var sb = new StringBuilder();
            int at = 0;
            foreach (var (name, args, close) in calls)
            {
                if (toks[name].Start < at) continue;   // inside a call already changed
                var (from, to) = args[0];
                string? qualifier = to - from == 3 && toks[from].IsName && toks[from + 1].IsPunct(".") && toks[from + 2].IsName ? toks[from].Text : null;
                if (to - from == 3 && qualifier == null) continue;
                var colTok = toks[to - 1];
                if (!colTok.IsName) continue;
                string col = colTok.Text;

                (ViewSpec? View, bool Records, string Qualifier) target;
                if (qualifier != null)
                {
                    if (!byAlias.TryGetValue(qualifier, out var hit)) continue;
                    target = (hit.View, hit.Records, qualifier);
                }
                else
                {
                    // The one view the statement names that has the column; a view named twice needs its name
                    var having = named.Where(n => n.Records ? col.Equals("Name", StringComparison.OrdinalIgnoreCase) : n.View!.ByName.ContainsKey(col)).ToList();
                    if (having.Count != 1) continue;
                    target = having[0];
                }

                string? path;
                if (target.Records) path = col.Equals("Name", StringComparison.OrdinalIgnoreCase) ? "Name" : null;
                else
                {
                    var v = target.View!;
                    path = v.ByName.TryGetValue(col, out var spec) && spec.Kind == "String" && spec.Expr.Contains("sfs_text(", StringComparison.Ordinal)
                        && !string.IsNullOrEmpty(spec.RelPath) && (!v.IsList || !v.ScalarEntries) ? spec.RelPath : null;
                }
                if (path == null) continue;

                sb.Append(sql, at, toks[name].Start - at);
                // The comment makes each changed call's text its own, for its column's name
                string call = $"sfs_text(/*{_renamed.Count + 1}*/{SqlText.Name(target.Qualifier)}.Json -> {SqlText.Literal("$." + path)}, {sql[toks[args[1].From].Start..toks[close].End]}";
                sb.Append(call);
                // A result column with no name of its own is named by its text: it keeps the name the SQL gave it
                _renamed[call] = sql[toks[name].Start..toks[close].End];
                at = toks[close].End;
            }
            if (at == 0) return null;
            sb.Append(sql, at, sql.Length - at);
            return sb.ToString();
        }

        /// <summary>A changed text() call's text, and the text the SQL wrote it with.</summary>
        readonly Dictionary<string, string> _renamed = new(StringComparer.Ordinal);

        /// <summary>The name of a result column, as the SQL wrote it.</summary>
        string ColumnName(string name) => _renamed.TryGetValue(name, out var written) ? written : name;

        /// <summary>The notes text() and the text columns gave while the statements ran.</summary>
        void NoteTexts()
        {
            if (_functions == null) return;
            // A read of this query gave texts from a plugin's own strings tables (the reading threads have no notes)
            foreach (var s in _e._records.Sessions.Open)
                if (s.TagTextsMade is { } made && Interlocked.Read(ref made.Given) > _tagGiven.GetValueOrDefault(s.Name))
                    lock (_functions.Notes.Lock) _functions.Notes.FromTables.Add(_language);
            foreach (var line in Languages.NoteLines(_functions.Notes, _language)) Note(line);
            lock (_functions.BuiltInAsked)
                if (_functions.BuiltInAsked.Count > 0)
                {
                    var asked = _functions.BuiltInAsked.OrderBy(k => k, StringComparer.Ordinal).ToList();
                    Note($"{string.Join(", ", asked.Take(4))}{(asked.Count > 4 ? $" and {asked.Count - 4:N0} more" : "")} {(asked.Count == 1 ? "is a form" : "are forms")} built into the game (Starfield.esm IDs below 000800, such as 000014, the player's reference): in no plugin file, so edid(), name() and type() give nothing for {(asked.Count == 1 ? "it" : "them")}.");
                    _functions.BuiltInAsked.Clear();
                }
        }
    }
}
