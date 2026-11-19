// The checks around a query: a wrong name answered with the nearest real ones (xEdit's names included), a text
// literal that is an EditorID held against a column of FormKeys, and the reason for an answer with no rows.
using System.Globalization;
using SFSearch.Index;

namespace SFSearch.Query;

public sealed partial class QueryEngine
{
    sealed partial class Session
    {
        // -------------------------------------------------------------- Names
        QueryException UnknownTable(string name)
        {
            var schema = _e._schema;
            // A table SQL had before 2.0: what answers for it now
            if (OldNames.Table(name) is { } old) return new QueryException($"there is no table \"{name}\": {old}", "the old tables and what replaced each are in references\\old-tools.md, \"Old SQL\"");
            // A signature in place of the record type
            var bySig = schema.TypesOf(name.ToUpperInvariant());
            if (bySig.Count > 0)
            {
                string view = _e.LabelOf(name.ToUpperInvariant());
                return new QueryException($"there is no table \"{name}\": {name.ToUpperInvariant()} is the signature of the record type {(bySig.Count == 1 ? view : $"{view} ({string.Join(", ", bySig)})")}",
                    $"use {view}");
            }
            var candidates = new List<(string Name, string What)>();
            foreach (var root in _e.Views.RootNames) candidates.Add((root, ""));
            foreach (var s in Special.Names) candidates.Add((s, ""));
            // Inside a record type: its lists
            string? rootOf = _e.Views.RootOf(name);
            if (rootOf != null)
                foreach (var l in _e.Views.Catalog(rootOf).Lists.Values) candidates.Add((l.Name, ""));
            // A plugin's own old tables
            // A name that is nowhere near a table gets none: no far-fetched one
            var near = Nearest.Pick(name, candidates, c => new[] { c.Name }, 4, always: false);
            // A list's name without its record type, or a field's name: say where the lists are
            string hint = rootOf != null
                ? $"{rootOf} has {_e.Views.Catalog(rootOf).Lists.Count:N0} list views; list them with SELECT Path FROM schema.Fields WHERE Type = '{_e.Views.Catalog(rootOf).Typed.Types[0]}' AND Kind = 'List'"
                : "the record types are in SELECT Sig, Type FROM schema.RecordTypes; a type's lists are views named Type_List (Weapon_Keywords)";
            return new QueryException($"there is no table or record type \"{name}\"." + SFSearch.SchemaGen.NearNames.DidYouMean(near.Select(n => n.Name).ToList()), hint);
        }

        sealed record Candidate(string View, string Text, string[] Names);

        QueryException UnknownColumn(string asked, List<ViewSpec> views, List<string> specials)
        {
            string column = asked[(asked.LastIndexOf('.') + 1)..];
            // A column SQL had before 2.0, when no view of the statement has one of that name: what answers for it now
            if (OldNames.Column(column) is { } old && !views.Any(v => v.FixedColumns.Concat(v.Columns.Select(c => c.Name)).Contains(column, StringComparer.OrdinalIgnoreCase)))
                return new QueryException($"{string.Join(", ", views.Select(v => v.Name).Concat(specials).Take(4))} {(views.Count + specials.Count > 1 ? "have" : "has")} no column \"{column}\": {old}",
                    "the old names and what replaced each are in references\\old-tools.md, \"Old SQL\"");
            var candidates = new List<Candidate>();
            foreach (var v in views)
            {
                foreach (string f in v.FixedColumns) candidates.Add(new Candidate(v.Name, f, new[] { f }));
                foreach (var c in v.Columns)
                {
                    // Shown and scored as describe and the record layer do it (NearNames), so the three name the same fields
                    string leaf = c.SchemaPath[(c.SchemaPath.LastIndexOf('.') + 1)..].Replace("[*]", "");
                    candidates.Add(new Candidate(v.Name, SFSearch.SchemaGen.NearNames.FieldText(c.Name, c.Kind, c.Subrecord, c.Readable),
                        SFSearch.SchemaGen.NearNames.FieldNames(c.Name, c.Readable, c.XEdit).Append(leaf).ToArray()));
                }
                // Fields of the record type that aren't columns of this view: lists, and fields too deep
                if (!v.IsList)
                    foreach (var h in _e.Views.Catalog(v.Root).Hints.OrderBy(h => h.SchemaPath.Length))
                        candidates.Add(new Candidate(v.Name, $"{h.Name} is {h.Where}", new[] { h.Name, h.Readable ?? "" }));
            }
            foreach (string s in specials)
                foreach (string c in Special.Columns(s, _versions)!) candidates.Add(new Candidate(s, c, new[] { c }));
            var tables = views.Select(v => v.Name).Concat(specials).ToList();
            if (candidates.Count == 0)
                return new QueryException($"SQLite: no such column: {asked}", "the statement names none of the record tables; check the column against the tables it does name");
            bool several = tables.Count > 1;
            // A column that is nowhere near any field gets no far-fetched name, as in describe and the record layer
            var near = Nearest.Pick(column, candidates, c => c.Names, 4, always: false);
            string who = string.Join(", ", tables.Take(4)) + (tables.Count > 4 ? ", …" : "");
            string list = string.Join("; ", near.Select(n => several ? $"{n.View}.{n.Text}" : n.Text).Distinct());
            string fix = tables.Count > 0 && views.Count > 0
                ? $"every field of a type is in SELECT Path, Kind, Readable FROM schema.Fields WHERE Type = '{views[0].Types[0]}'; a field that isn't a column can be read with Json ->> '$.Path'"
                : "check the column's name";
            // A list view has few columns, and they differ by list (a list of links has Value; a list of objects has a
            // column for each field of an item): say them, so the right one isn't a second guess (GAPS S202)
            string own = string.Concat(views.Where(v => v.IsList).Take(2).Select(v =>
                $" {v.Name} has the columns {string.Join(", ", ViewBuilder.ColumnNames(v, _versions).Take(24))}{(ViewBuilder.ColumnNames(v, _versions).Count > 24 ? ", …" : "")}."));
            return new QueryException($"{who} {(several ? "have" : "has")} no column \"{column}\"." + (near.Count > 0 ? $" Did you mean: {list}?" : "") + own, fix);
        }

        // -------------------------------------------------------------- EditorID literals
        static readonly HashSet<string> Comparers = new(StringComparer.OrdinalIgnoreCase) { "=", "==", "!=", "<>", "LIKE", "GLOB", "IS", "IN" };

        /// <summary>
        /// A text literal that is an EditorID, compared against a column of FormKeys or against Json, finds nothing:
        /// EditorIDs aren't inside records. The answer gets a note that names edid() and the FormKey.
        /// </summary>
        void CheckEditorIdLiterals(List<Tok> toks, Probed probed)
        {
            // Columns that hold FormKeys, by name, in the views this statement reads
            var linkColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Json", "ToKey", "FromKey", "FormKey", "Parent" };
            foreach (var v in probed.Views.Keys)
                foreach (var c in v.Columns) if (c.IsLink) linkColumns.Add(c.Name);
            for (int i = 0; i < toks.Count; i++)
            {
                if (toks[i].Kind != TokKind.String) continue;
                string text = toks[i].Text.Trim('%', '_', '*').Trim();
                if (text.Length < 3 || text.Contains(' ') || LinkWalker.IsFormKey(System.Text.Encoding.UTF8.GetBytes(text))) continue;
                string? column = ComparedColumn(toks, i);
                if (column == null || !linkColumns.Contains(column)) continue;
                var hits = Index.FindEditorId(text);
                if (hits.Count == 0) continue;
                var info = Index.Describe(hits[0]);
                string more = hits.Count > 1 ? $" (and {hits.Count - 1:N0} more record(s) with that EditorID in other plugins)" : "";
                Note(column.Equals("Json", StringComparison.OrdinalIgnoreCase)
                    ? $"'{toks[i].Text}' is the EditorID of {info.Label}{more}. EditorIDs aren't inside a record's JSON: links are written as FormKeys. Search for '{info.FormKey}', or compare a link column with edid(column) = '{text}'."
                    : $"'{toks[i].Text}' is the EditorID of {info.Label}{more}, but {column} holds FormKeys, so this comparison finds nothing. Use edid({column}) = '{text}', or {column} = '{info.FormKey}'.");
            }
        }

        /// <summary>The column a literal is compared with: "col = 'x'", "col LIKE 'x'", "col IN ('x', …)", "'x' = col".</summary>
        static string? ComparedColumn(List<Tok> toks, int lit)
        {
            int i = lit - 1;
            // Inside an IN list: back to the opening bracket
            if (i >= 0 && (toks[i].IsPunct(",") || toks[i].IsPunct("(")))
            {
                while (i >= 0 && !toks[i].IsPunct("(")) { if (toks[i].Kind is not (TokKind.String or TokKind.Number) && !toks[i].IsPunct(",")) return null; i--; }
                i--;
                if (i < 0 || !toks[i].Is("IN")) return null;
            }
            if (i >= 0 && (toks[i].Kind == TokKind.Punct || toks[i].Kind == TokKind.Word) && Comparers.Contains(toks[i].Text))
            {
                i--;
                if (i >= 0 && toks[i].Is("NOT")) i--;
                // edid(col) = 'x' is the right way: the thing before the comparison is a function call
                if (i >= 0 && toks[i].IsName && !(i + 1 < toks.Count && toks[i + 1].IsPunct("("))) return toks[i].Text;
                return null;
            }
            // The literal first: 'x' = col
            int j = lit + 1;
            if (j < toks.Count && Comparers.Contains(toks[j].Text))
            {
                j++;
                while (j + 2 < toks.Count && toks[j].IsName && toks[j + 1].IsPunct(".")) j += 2;
                if (j < toks.Count && toks[j].IsName && !(j + 1 < toks.Count && toks[j + 1].IsPunct("("))) return toks[j].Text;
            }
            return null;
        }

        // -------------------------------------------------------------- No silent empty answers
        /// <summary>An answer with no rows says why, from facts the engine already has.</summary>
        void NoteWhyEmpty(List<Tok> toks)
        {
            var reasons = new List<string>();
            var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var view in _allViewsUsed.OrderBy(v => v.IsList).ThenBy(v => v.Name, StringComparer.Ordinal))
            {
                if (!counted.Add(view.Root)) continue;
                var typed = _e.Views.Catalog(view.Root).Typed;
                var sigs = typed.Types.Select(t => _e._schema.SignatureOf(t)!).Distinct().ToList();
                // What the header index has of the type, plugin by plugin
                var have = new List<(string Plugin, long Count)>();
                foreach (var p in _members)
                {
                    long n = sigs.Sum(s => p.Segment!.Signatures.Where(x => x.Signature == s).Sum(x => (long)x.Count));
                    if (n > 0) have.Add((p.Name, n));
                }
                string sigText = string.Join("/", sigs);
                if (have.Count == 0)
                {
                    reasons.Add(_members.Count == 1
                        ? $"{_members[0].Name} has no {typed.Name} records (the header index has 0 {sigText} in it)"
                        : $"none of the {_members.Count:N0} plugins of this scope has a {typed.Name} record (the header index has 0 {sigText} in them)");
                    continue;
                }
                long rows = RowsOf(typed);
                string where = have.Count == 1 ? have[0].Plugin : $"{have.Count:N0} plugins";
                string of = _winners ? $"{rows:N0} winning {typed.Name} rows of {where}" : $"{rows:N0} {typed.Name} rows of {where}";
                if (typed.Types.Count == 1 && !typed.IsUnion || rows > 0)
                    reasons.Add(view.IsList || _allViewsUsed.Any(v => v.IsList && v.Root.Equals(view.Root, StringComparison.OrdinalIgnoreCase))
                        ? $"the filter matched nothing among the list entries of {of}" : $"the filter matched nothing among {of}");
                else reasons.Add($"{where} has {have.Sum(h => h.Count):N0} {sigText} records, but none of the type {typed.Name}");
                foreach (var p in _members.Where(p => have.All(h => h.Plugin != p.Name)).Take(3))
                    if (_members.Count <= 8) reasons.Add($"{p.Name} has no {typed.Name} records");
            }
            // Records, Links and Conditions hold the record types read: say how many of the scope's that is. With all of
            // them read the empty answer is complete, and a note about narrowing would put that in doubt.
            var readTypes = _files.SelectMany(f => _done[f].Keys).Where(k => k != FormsDone).ToHashSet(StringComparer.Ordinal);
            var notRead = ScopeSignatures().Keys.Where(s => _e._schema.TypesOf(s).Count > 0 && !_files.All(f => _done[f].ContainsKey(s))).Select(_e.LabelOf).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            string Held(string table, string of, string narrow) =>
                _req.NoAutoReads ? "nothing among what this stored query read matched what was asked"
                : notRead.Count == 0 ? $"the filter matched nothing among {of} of every record type of this scope ({readTypes.Count:N0} types read)"
                : $"the filter matched nothing among {of} of the {readTypes.Count:N0} record type(s) read so far; {table} doesn't hold the other {notRead.Count:N0} of this scope ({string.Join(", ", notRead.Take(6))}{(notRead.Count > 6 ? ", …" : "")}): {narrow}";
            foreach (string s in _specialUsed)
                reasons.Add(s switch
                {
                    "Records" => Held("Records", "the records", "narrow it (WHERE Type = '…', FormKey = '…' or EditorID = '…') and that type is read first, or name a type's own table"),
                    "Links" => Held("Links", "the links", "narrow it (WHERE ToKey = '…', FromKey = '…' or FromType = '…') and the types that needs are read first"),
                    "Conditions" => Held("Conditions", "the conditions", "narrow it (WHERE Type = '…', FormKey = '…' or EditorID = '…') and that type is read first"),
                    _ => $"the filter matched nothing among the {_members.Sum(p => (long)p.Segment!.Count):N0} records of the header index for this scope",
                });
            if (reasons.Count == 0 && toks.Any(t => t.Is("Meta") || t.Is("Masters")))
                reasons.Add("Meta and Masters were tables of the old databases (a plugin's source stamp, its masters): a plugin's header is in the table Plugins now (SELECT Plugin, Masters, Version, NumRecords FROM Plugins), and loadorder2 <plugin> shows it");
            if (reasons.Count == 0)
                reasons.Add(toks.Any(t => t.IsName && (t.Text.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase) || t.Text.StartsWith("pragma_", StringComparison.OrdinalIgnoreCase)))
                    ? "the record tables aren't in SQLite's own list of tables: a type's view is made when a query names it. The record types are in SELECT Sig, Type FROM schema.RecordTypes, a type's fields and lists in SELECT Path, Kind, Readable FROM schema.Fields WHERE Type = '…' (a list's view is Type_List: Npc_Factions), and SELECT * FROM <view> LIMIT 1 shows a view's columns"
                    : "the query names no record table");

            // A plugin named in the SQL that isn't there, or isn't part of the scope
            foreach (var t in toks)
            {
                if (t.Kind != TokKind.String || t.Text.Length < 5 || t.Text.Contains(':')) continue;
                string lit = t.Text.Trim('%');
                if (!(lit.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || lit.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || lit.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))) continue;
                var p = Index.Plugin(lit);
                if (p == null) reasons.Add($"{lit} isn't in the Data folder");
                else if (_req.Scope != ScopeKind.Plugins && !p.Order.Enabled) reasons.Add($"{p.Name} is in Data but not in the load order, so it has no rows in the {_req.Scope.ToString().ToLowerInvariant()} scope: read it with the plugins scope");
                else if (_req.Scope == ScopeKind.Plugins && !_members.Contains(p)) reasons.Add($"{p.Name} isn't one of this query's plugins ({string.Join(", ", _members.Take(4).Select(m => m.Name))}): add it to the scope");
            }
            Note("No rows: " + string.Join("; ", reasons.Distinct()) + ".");
        }

        long RowsOf(ViewSpec typed)
        {
            long rows = 0;
            string types = string.Join(", ", typed.Types.Select(SqlText.Literal));
            foreach (var f in _files)
            {
                string sig = _e._schema.SignatureOf(typed.Types[0])!;
                using var cmd = _con.CreateCommand();
                cmd.CommandText = !_done[f].ContainsKey(sig) && _projections.TryGetValue((f, typed.Types[0]), out var proj)
                    ? $"SELECT count(*) FROM {f.Alias}.{SqlText.Name(proj.Table)}{(f.IsSet && _winners ? " WHERE IsWinner = 1" : "")}"
                    : $"SELECT count(*) FROM {f.Alias}.Rec WHERE Type IN ({types}){(f.IsSet && _winners ? " AND IsWinner = 1" : "")}";
                rows += Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            return rows;
        }
    }
}
