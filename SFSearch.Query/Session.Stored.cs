// What stored queries (milestone M10) need of a session: the SQL's named parameters bound as values, and a check
// of a statement list that reads no plugin (the statements are compiled and run against empty tables that have each
// view's columns, so a wrong name is found, and the columns of each SELECT are known, without any record read).
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SFSearch.Query;

public sealed partial class QueryEngine
{
    /// <summary>
    /// Checks SQL without reading anything: every table and column it names must exist in the scope's views, and
    /// every statement must compile and run against empty tables. The answer has the error and the nearest real
    /// names, or the columns of the last SELECT with no rows. Plugins named in the request are not looked at.
    /// </summary>
    public QueryAnswer Check(QueryRequest request) => new Session(this, request).CheckOnly();

    sealed partial class Session
    {
        /// <summary>The names of the parameters a statement uses (<c>:name</c>, <c>@name</c>, <c>$name</c>), without the mark.</summary>
        internal static List<string> ParameterNames(List<Tok> toks)
        {
            var names = new List<string>();
            foreach (var t in toks)
                if (t.Kind == TokKind.Word && t.Text.Length > 1 && t.Text[0] is ':' or '@' or '$' && !names.Contains(t.Text[1..], StringComparer.OrdinalIgnoreCase))
                    names.Add(t.Text[1..]);
            return names;
        }

        /// <summary>
        /// Binds the request's parameters to a statement, each under the mark the statement wrote it with. A parameter
        /// the statement names and the request doesn't have stops the query with a plain line: before this, such a
        /// query ended in an exception of Microsoft.Data.Sqlite that Run didn't catch.
        /// </summary>
        /// <summary>
        /// A positional parameter (<c>?</c>, <c>?NNN</c>) stops the query with a plain line before anything is read (GAPS
        /// S161): the request gives its parameters by name, so such a parameter can't have a value, and Microsoft.Data.Sqlite
        /// ended the query in an exception Run didn't catch ("Must add values for the following parameters").
        /// </summary>
        static void CheckPositionalParameters(List<Tok> toks)
        {
            int at = toks.FindIndex(t => t.IsPunct("?"));
            if (at < 0) return;
            string p = at + 1 < toks.Count && toks[at + 1].Kind == TokKind.Number && toks[at + 1].Start == toks[at].End ? "?" + toks[at + 1].Text : "?";
            throw new QueryException($"the SQL has the positional parameter {p}, and parameters are given by name",
                "write it as a named parameter (:name) and give its value with the request, or write the value into the SQL");
        }

        void BindParameters(SqliteCommand cmd, List<Tok> toks)
        {
            foreach (var t in toks)
            {
                if (t.Kind != TokKind.Word || t.Text.Length < 2 || t.Text[0] is not (':' or '@' or '$')) continue;
                if (cmd.Parameters.Contains(t.Text)) continue;
                string name = t.Text[1..];
                var hit = _req.Parameters.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (hit.Key == null)
                    throw new QueryException($"the SQL names the parameter {t.Text}, and the request gives no value for it",
                        _req.Parameters.Count > 0 ? $"the parameters given are {string.Join(", ", _req.Parameters.Keys)}" : "give its value with the request, or write the value into the SQL");
                cmd.Parameters.AddWithValue(t.Text, hit.Value switch
                {
                    null => DBNull.Value,
                    bool b => b ? 1L : 0L,
                    int i => (long)i,
                    float f => (double)f,
                    decimal m => (double)m,
                    _ => hit.Value,
                });
            }
        }

        /// <summary>See <see cref="QueryEngine.Check"/>.</summary>
        public QueryAnswer CheckOnly()
        {
            try
            {
                if (_language.Length == 0) throw Languages.Unknown(_languageAsked);
                _winners = _req.Scope == ScopeKind.Winners; _versions = _req.Scope == ScopeKind.Versions;
                Open();
                var statements = SqlText.SplitStatements(_req.Sql);
                if (statements.Count == 0) throw new QueryException("the SQL is empty", "write a SELECT");
                for (int i = 0; i < statements.Count; i++) CheckStatement(statements[i], i == statements.Count - 1);
            }
            catch (QueryException e) { Fail(e.Message, e.Fix); }
            catch (OperationCanceledException) { Fail("the check was cancelled", null); }
            catch (SqliteException e) { Fail(SqliteText(e.Message), "check the SQL"); }
            finally
            {
                Close();
                _answer.Cost.Seconds = _clock.Elapsed.TotalSeconds;
            }
            return _answer;
        }

        void CheckStatement(string sql, bool last)
        {
            var toks = SqlText.Tokenize(sql);
            Guard.CheckFirstWord(toks);
            CheckLanguageLiterals(toks);
            // The names, with the nearest real ones for a wrong one
            var probed = Probe(sql);
            var stubs = new List<string>();
            void Stub(string name, IEnumerable<string> columns)
            {
                ScratchFile.Exec(_con, ViewBuilder.StubSql(name, columns));
                stubs.Add(name);
            }
            try
            {
                foreach (var v in probed.Views.Keys) Stub(v.Name, ViewBuilder.ColumnNames(v, _versions));
                foreach (string s in probed.Special) Stub(s, Special.Columns(s, _versions)!);
                for (int round = 0; ; round++)
                {
                    _guard.Denied = null; _guard.Reads = null; _guard.Current = Guard.Mode.User;
                    try
                    {
                        using var cmd = _con.CreateCommand();
                        cmd.CommandText = sql;
                        BindParameters(cmd, toks);
                        using var r = cmd.ExecuteReader();
                        if (r.FieldCount > 0 && last)
                        {
                            _answer.HasResult = true;
                            _answer.Columns.Clear();
                            for (int i = 0; i < r.FieldCount; i++) _answer.Columns.Add(r.GetName(i));
                        }
                        while (r.Read()) { }
                        return;
                    }
                    catch (SqliteException) when (_guard.Denied != null)
                    {
                        throw new QueryException($"the query isn't allowed {_guard.Denied}", "a query may read, and may make temporary tables, indexes and views of its own (CREATE TEMP …)");
                    }
                    catch (SqliteException e) when (round < 200 && System.Text.RegularExpressions.Regex.Match(e.Message, @"no such table: (?:(?:main|temp)\.)?([^']+)") is { Success: true } m)
                    {
                        // A view the statement names and reads nothing of
                        string name = m.Groups[1].Value.Trim().Trim('"');
                        if (stubs.Contains(name, StringComparer.OrdinalIgnoreCase)) throw;
                        if (Special.Columns(name, _versions) is { } special) Stub(Special.Canonical(name), special);
                        else if (_e.Views.Find(name) is { } view) Stub(view.Name, ViewBuilder.ColumnNames(view, _versions));
                        else throw UnknownTable(name);
                    }
                    finally { _guard.Current = Guard.Mode.Off; }
                }
            }
            finally
            {
                _guard.Current = Guard.Mode.Off;
                foreach (string s in stubs) ScratchFile.Exec(_con, $"DROP TABLE IF EXISTS temp.{SqlText.Name(s)}");
            }
        }

        internal static string ParameterText(object? value) => value switch
        {
            null => "",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
    }
}
