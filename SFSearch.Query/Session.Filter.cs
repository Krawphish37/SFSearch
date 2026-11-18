// A statement's col = literal applied while a big type's named columns are read (J30). Reading one column of
// Starfield.esm's 3.29 million placed objects inserts 3.29 million rows into SQLite, about 3 s on its one inserting
// thread, even when the statement wants the 2,648 with one base. When every row of the typed view the statement can use
// has that column equal to one of a few literals, only the records whose value is one of them are inserted, into a
// temporary table of the statement (as a typed view read by FormKey is: Session.Keyed.cs).
//
// It is done only when nothing the statement could use is left out, by the same two checks as the FormKey narrowing:
//   - every comparison of a column of that name in the statement is with a literal (= or IN, no expression around it);
//   - SQLite's own plan of the statement over the empty stand-ins, with an index on that column of each view, finds
//     every row it reads of the type's views through that index (col=?), and scans none.
// A scratch table of the type's named columns, if one is there already, answers instead: nothing is read.
using SFSearch.Index;
using SQLitePCL;

namespace SFSearch.Query;

public sealed partial class QueryEngine
{
    sealed partial class Session
    {
        /// <summary>For the statement being run: each typed root whose rows all have one column equal to one of some
        /// literals, with the column and the literals.</summary>
        Dictionary<string, (string Column, List<object> Literals)> _filterRoots = new(StringComparer.OrdinalIgnoreCase);

        static string ColumnIndexName(ViewSpec v, string column) => "sfs_col_" + v.Name + "_" + column;

        /// <summary>
        /// For <see cref="Probe"/>, while the stand-ins are there: each column of a typed view the statement reads that it
        /// compares only with literals, with the literals and SQLite's plan over the stand-ins with an index on that
        /// column of each view that has it (the index dropped again, so the FormKey plan is made without it).
        /// </summary>
        Dictionary<string, (List<object> Literals, List<string> Plan)> ColumnPlans(string sql, List<Tok> toks, IEnumerable<ViewSpec> views, Dictionary<string, HashSet<string>> reads)
        {
            var plans = new Dictionary<string, (List<object>, List<string>)>(StringComparer.OrdinalIgnoreCase);
            var typed = views.Where(v => !v.IsList).Distinct().ToList();
            var columns = typed.SelectMany(v => reads.TryGetValue(v.Name, out var r) ? r : Enumerable.Empty<string>())
                .Where(c => !NotFields.Contains(c) && !c.Equals("Json", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string col in columns)
            {
                if (ColumnLiterals(toks, col, numbers: true) is not { Count: > 0 } literals) continue;
                var on = typed.Where(v => v.ByName.ContainsKey(col)).ToList();
                if (on.Count == 0) continue;
                foreach (var v in on)
                    ScratchFile.Exec(_con, $"CREATE INDEX temp.{SqlText.Name(ColumnIndexName(v, col))} ON {SqlText.Name(v.Name)}({SqlText.Name(col)})");
                var lines = new List<string>();
                var db = _con.Handle!;
                if (raw.sqlite3_prepare_v2(db, "EXPLAIN QUERY PLAN " + sql, out var stmt) == raw.SQLITE_OK)
                    using (stmt)
                        while (raw.sqlite3_step(stmt) == raw.SQLITE_ROW) lines.Add(raw.sqlite3_column_text(stmt, 3).utf8_to_string());
                else stmt?.Dispose();
                foreach (var v in on) ScratchFile.Exec(_con, $"DROP INDEX IF EXISTS temp.{SqlText.Name(ColumnIndexName(v, col))}");
                plans[col] = (literals, lines);
            }
            return plans;
        }

        /// <summary>The typed roots whose reads this statement can filter by a column while reading (the top of this
        /// file). Only a root the statement names by its typed view alone (no list view of it).</summary>
        void FindFilterRoots(Probed probed, List<Tok> toks)
        {
            _filterRoots = new(StringComparer.OrdinalIgnoreCase);
            if (probed.ColumnPlans.Count == 0) return;
            foreach (var group in probed.Views.Keys.GroupBy(v => v.Root, StringComparer.OrdinalIgnoreCase))
            {
                if (group.Count() != 1 || group.First().IsList) continue;
                var v = group.First();
                foreach (var (col, (literals, plan)) in probed.ColumnPlans)
                {
                    if (!v.ByName.TryGetValue(col, out var spec) || !probed.Views[v].Contains(col)) continue;
                    // The literals of the column's own kind: texts for links, names and texts, numbers for numbers
                    bool text = spec.Kind is "FormLink" or "String" or "Enum" or "P2Float" or "P3Float" or "P2Int16" or "P3Int16";
                    bool number = spec.Kind is "Bool" or "Int8" or "Int16" or "Int32" or "Int64" or "UInt8" or "UInt16" or "UInt32" or "Float" or "Double";
                    if (!(text && literals.All(l => l is string) || number && literals.All(l => l is long or double))) continue;
                    if (!OnlyThrough(toks, v, plan, $" INDEX {ColumnIndexName(v, col)} ({col}=?)")) continue;
                    _filterRoots[group.Key] = (spec.Name, literals);
                    break;
                }
            }
        }

        /// <summary>True when SQLite's plan reads the view only through the given index (FindKeyedRoots' rule).</summary>
        static bool OnlyThrough(List<Tok> toks, ViewSpec v, List<string> plan, string byIndex)
        {
            // The names a plan line can give this view by: its own and each alias the statement gives it
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { v.Name };
            for (int i = 0; i < toks.Count; i++)
            {
                if (!toks[i].IsName || !toks[i].Text.Equals(v.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (i + 1 < toks.Count && toks[i + 1].IsName) names.Add(toks[i + 1].Text);
                if (i + 2 < toks.Count && toks[i + 1].Is("AS") && toks[i + 2].IsName) names.Add(toks[i + 2].Text);
            }
            bool found = false;
            foreach (string line in plan)
            {
                if (!(line.StartsWith("SCAN ", StringComparison.Ordinal) || line.StartsWith("SEARCH ", StringComparison.Ordinal))) continue;
                string rest = line[(line.IndexOf(' ') + 1)..];
                int sp = rest.IndexOf(' ');
                string name = sp < 0 ? rest : rest[..sp];
                if (line.Contains(byIndex, StringComparison.Ordinal)) { found = true; continue; }
                if (names.Contains(name)) return false;
            }
            return found;
        }

        /// <summary>The filter of a read of a typed view's named columns, when the statement allows one: the column's
        /// place among them, its literals, how it compares.</summary>
        RowFilter? FilterOf(ViewSpec view, IReadOnlyList<string> columns)
        {
            if (!_filterRoots.TryGetValue(view.Root, out var f)) return null;
            int at = columns.ToList().FindIndex(c => c.Equals(f.Column, StringComparison.OrdinalIgnoreCase));
            if (at < 0) return null;
            return new RowFilter { Column = at, Name = f.Column, Literals = f.Literals, NoCase = view.ByName[f.Column].IsLink };
        }

        /// <summary>The filter in words: "Base = '00003B:Starfield.esm'", "Base IN ('…', '…')".</summary>
        static string FilterText(RowFilter f)
        {
            string Lit(object o) => o is string s ? SqlText.Literal(s) : Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            return f.Literals.Count == 1 ? $"{f.Name} = {Lit(f.Literals[0])}" : $"{f.Name} IN ({string.Join(", ", f.Literals.Take(5).Select(Lit))}{(f.Literals.Count > 5 ? ", …" : "")})";
        }

        /// <summary>How the named columns of a type are read: each straight from Mutagen's object where it can be
        /// (DirectColumns.cs), the rest from the JSON; all from the records' own bytes when each one can be (RawColumns.cs).</summary>
        ReadPlan ProjectionPlan(ViewSpec view, IReadOnlyList<string> columns, bool winners, RowFilter? filter)
        {
            var direct = columns.Select(c => _e._options.DirectColumns ? DirectColumn.For(view.ByName[c], _language) : null).ToArray();
            var fromJson = columns.Where((c, i) => direct[i] == null).ToList();
            RawRecordColumns? raw = null;
            if (columns.Count > 0 && fromJson.Count == 0)
            {
                var raws = columns.Select(c => RawColumn.For(view, view.ByName[c], _e._schema)).ToArray();
                if (raws.All(r => r != null)) raw = new RawRecordColumns(raws!);
            }
            // A type that shares its signature with others: only its own records give rows (ReadPlan.OnlyType)
            string? sharedSig = _e._schema.SignatureOf(view.Types[0]) is { } vs && _e._schema.TypesOf(vs).Count > 1 ? vs : null;
            return new ReadPlan
            {
                OnlyType = sharedSig != null ? view.Types[0] : null, ClassOf = sharedSig != null ? SFSearch.Records.RecordClasses.For(_e._schema, sharedSig) : null,
                Full = false, ProjectColumns = fromJson.Count, Winners = winners, Raw = raw, Filter = filter, TextLanguage = _language,
                Unsplit = fromJson.All(c => view.ByName[c].RelPath is { Length: > 0 } rel && !_e._schema.FieldHoldsRecords(view.Types[0], rel.Split('.')[0])),
                ProjectSql = fromJson.Count == 0 ? null : JsonbMaker.ProjectSql(ViewBuilder.ProjectionExprs(view, fromJson, _language)),
                Direct = direct.Any(d => d != null) ? direct : null,
            };
        }

        /// <summary>"; 3 records through Mutagen, where the bytes alone don't say": after a read from the records' own
        /// bytes, the records that weren't read from them.</summary>
        static string ViaMutagen(ReadPlan plan)
        {
            long n = Interlocked.Read(ref plan.ViaMutagen);
            return n == 0 ? "" : $"; {n:N0} record{(n == 1 ? "" : "s")} through Mutagen, where the bytes alone don't say";
        }

        /// <summary>"straight from the records", "from the records' bytes", "2 of them straight from the records".</summary>
        static string HowRead(ReadPlan plan, int columns)
        {
            if (columns == 0) return "";
            if (plan.Raw != null) return ", from the records' own bytes" + ViaMutagen(plan);
            int directs = plan.Direct?.Count(d => d != null) ?? 0;
            return directs == 0 ? "" : directs == columns ? ", straight from the records" : $", {directs:N0} of them straight from the records";
        }
    }
}
