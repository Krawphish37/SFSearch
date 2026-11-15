// Records read by key, for one statement only (fix g-sql, 2026-11-13). Two things read a few records of a type
// instead of the whole type:
//
//   1. A typed view narrowed by FormKey (GAPS S28): SELECT Base FROM PlacedObject WHERE FormKey = '…' (or IN ('…', …))
//      was refused as a read of 7.96 million placed objects. Now the header index gives the copies of those FormKeys
//      and only they are read. It is done only when SQLite itself says every row the statement reads of the view is
//      found by FormKey (the plan of the statement over empty tables with an index on FormKey), and every FormKey the
//      statement compares is a text literal: so no row the statement could use is left out.
//   2. Links WHERE ToKey = '…' for a plugin with no link file (GAPS S3): the records whose bytes hold the FormID are
//      found by a byte search (ByteSearch, as refs does) and read alone; their links answer the query.
//
// The rows go into temporary tables of the query's own connection, never into the scratch files: a scratch file holds
// whole types only (its Done table says so). The statement's views read them instead of the scratch table.
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SFSearch.Index;
using SFSearch.Records;
using SQLitePCL;

namespace SFSearch.Query;

public sealed partial class QueryEngine
{
    sealed partial class Session
    {
        /// <summary>The records of one scratch file read by key in this statement.</summary>
        sealed class Keyed
        {
            public required string Rec, Links;
            /// <summary>Signatures whose typed and list views read the keyed rows instead of the scratch table.</summary>
            public readonly HashSet<string> ViewSigs = new(StringComparer.Ordinal);
            /// <summary>Records and Links have the keyed rows too (the byte search for Links WHERE ToKey).</summary>
            public bool ForLinks;
            public readonly HashSet<RecordRef> Have = new();
        }

        readonly Dictionary<ScratchFile, Keyed> _keyed = new();
        /// <summary>For the statement being run: each typed root narrowed by FormKey, with the FormKeys.</summary>
        Dictionary<string, HashSet<string>> _keyedRoots = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>For the statement being run: the reads by key it needs, per file (by FormKey, or by a byte search).</summary>
        readonly List<(ScratchFile File, List<RecordRef> Refs, bool ForLinks, string Sig, string Why)> _keyedJobs = new();

        /// <summary>A read is done by key when the whole read would take this long or more (by its estimate), or is heavy.</summary>
        const double KeyedSeconds = 1.0;

        void DropKeyed()
        {
            foreach (var k in _keyed.Values)
                ScratchFile.Exec(_con, $"DROP TABLE IF EXISTS temp.{SqlText.Name(k.Rec)}; DROP TABLE IF EXISTS temp.{SqlText.Name(k.Links)};");
            _keyed.Clear();
            _keyedJobs.Clear();
            _keyedRoots = new(StringComparer.OrdinalIgnoreCase);
        }

        Keyed KeyedOf(ScratchFile f)
        {
            if (_keyed.TryGetValue(f, out var k)) return k;
            k = new Keyed { Rec = "sfs_keyed_" + f.Alias, Links = "sfs_keyedlinks_" + f.Alias };
            ScratchFile.Exec(_con, $"CREATE TEMP TABLE {SqlText.Name(k.Rec)}(Seq INTEGER PRIMARY KEY, Ord INT NOT NULL, Type TEXT NOT NULL, Sig TEXT NOT NULL, FormKey TEXT NOT NULL COLLATE NOCASE, "
                + "EditorID TEXT COLLATE NOCASE, IsOverride INT NOT NULL, Pos INT NOT NULL, Plugin TEXT COLLATE NOCASE, LoadOrder INT, IsWinner INT, Json BLOB, Lnk BLOB);"
                + $"CREATE INDEX temp.{SqlText.Name(k.Rec + "_FormKey")} ON {SqlText.Name(k.Rec)}(FormKey);"
                + $"CREATE TEMP TABLE {SqlText.Name(k.Links)}(FromKey TEXT COLLATE NOCASE, FromType TEXT, Field TEXT, Idx INT, ToKey TEXT COLLATE NOCASE, Path TEXT, Plugin TEXT COLLATE NOCASE, IsWinner INT);"
                + $"CREATE INDEX temp.{SqlText.Name(k.Links + "_To")} ON {SqlText.Name(k.Links)}(ToKey);");
            return _keyed[f] = k;
        }

        /// <summary>Reads the keyed records this statement needs into the temporary tables.</summary>
        void RunKeyed()
        {
            foreach (var (file, refs, forLinks, sig, why) in _keyedJobs)
            {
                var clock = Stopwatch.StartNew();
                var k = KeyedOf(file);
                if (forLinks) k.ForLinks = true; else k.ViewSigs.Add(sig);
                long rows = 0, links = 0; int skipped = 0; string? firstSkip = null;
                var plan = new ReadPlan { Full = true, Winners = file.IsSet };
                var byPlugin = refs.Where(k.Have.Add).GroupBy(r => r.Plugin).ToList();
                using (var tx = _con.BeginTransaction())
                {
                    long seq;
                    using (var top = _con.CreateCommand()) { top.CommandText = $"SELECT coalesce(max(Seq), 0) FROM temp.{SqlText.Name(k.Rec)}"; seq = (long)top.ExecuteScalar()!; }
                    using var ins = new BulkInsert(_con, $"INSERT INTO temp.{SqlText.Name(k.Rec)}(Seq, Ord, Type, Sig, FormKey, EditorID, IsOverride, Pos, Plugin, LoadOrder, IsWinner, Json, Lnk) VALUES (?12,?12,?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11)");
                    using var lnk = new BulkInsert(_con, $"INSERT INTO temp.{SqlText.Name(k.Links)}(FromKey, FromType, Field, Idx, ToKey, Path, Plugin, IsWinner) VALUES (?,?,?,?,?,?,?,?)");
                    foreach (var group in byPlugin)
                    {
                        var plugin = Index.Plugins[group.Key];
                        var member = file.Members.First(m => m.Plugin.Equals(plugin.Name, StringComparison.OrdinalIgnoreCase));
                        try { _e._records.Sessions.Get(plugin.Name); }
                        catch (RecordException e) { skipped += group.Count(); firstSkip ??= $"{plugin.Name}: {e.Message}"; continue; }
                        foreach (var batch in _e._reader.ReadEntries(plugin, group.Select(r => r.Entry).ToArray(), plan, _req.Cancel))
                            foreach (var row in batch)
                            {
                                if (row.Skip != null) { skipped++; firstSkip ??= $"{plugin.Name}: {row.EditorId ?? "(no EditorID)"} ({row.FormKey}): {row.Skip}"; continue; }
                                string rowSig = _e._schema.SignatureOf(row.Type) ?? sig;
                                ins.Text(1, row.Type); ins.Text(2, rowSig); ins.Text(3, row.FormKey); ins.Text(4, row.EditorId); ins.Int(5, row.IsOverride ? 1 : 0); ins.Int(6, row.Pos);
                                if (file.IsSet) { ins.Text(7, plugin.Name); ins.Int(8, member.LoadOrder); ins.Int(9, row.IsWinner ? 1 : 0); }
                                else { ins.Null(7); ins.Null(8); ins.Null(9); }
                                ins.Blob(10, row.Jsonb); ins.Blob(11, row.Links); ins.Int(12, ++seq);
                                ins.Step();
                                rows++;
                                if (forLinks && row.Links != null)
                                    foreach (var (field, idx, path, to) in LinkWalker.Lines(row.Links))
                                    {
                                        lnk.Text(1, row.FormKey); lnk.Text(2, row.Type); lnk.Text(3, field); lnk.Int(4, idx); lnk.Text(5, to); lnk.Text(6, path);
                                        lnk.Text(7, file.IsSet ? plugin.Name : null); lnk.Int(8, file.IsSet ? (row.IsWinner ? 1 : 0) : null);
                                        lnk.Step();
                                        links++;
                                    }
                            }
                        ConfirmUnchanged(plugin);
                    }
                    tx.Commit();
                }
                string label = sig.Length > 0 ? _e.LabelOf(sig) : "records";
                _answer.Cost.Read.Add(forLinks
                    ? $"{rows:N0} record(s) whose bytes hold the FormID, alone ({links:N0} links), {why}, in {Took(clock.Elapsed.TotalSeconds)}"
                    : $"{label} ({rows:N0} by FormKey, from the header index) from {(byPlugin.Count == 1 ? Index.Plugins[byPlugin[0].Key].Name : $"{byPlugin.Count:N0} plugins")} in {Took(clock.Elapsed.TotalSeconds)}");
                _usedLabels.Add(forLinks ? "Links (by bytes)" : $"{label} (by FormKey)");
                _readLabels.Add(forLinks ? "Links (by bytes)" : $"{label} (by FormKey)");
                if (skipped > 0) Note($"{label}: {skipped:N0} record(s) read by key couldn't be read and aren't in the answer. The first: {firstSkip}");
            }
        }

        // -------------------------------------------------------------- 1. A typed view narrowed by FormKey
        static readonly HashSet<string> CompareWords = new(StringComparer.OrdinalIgnoreCase) { "IS", "NOT", "LIKE", "GLOB", "BETWEEN", "MATCH", "REGEXP", "COLLATE", "ESCAPE", "IN", "ISNULL", "NOTNULL" };
        /// <summary>Words before a parenthesis that make it something other than a function's arguments.</summary>
        static readonly HashSet<string> NotFunctions = new(StringComparer.OrdinalIgnoreCase)
        {
            "AND", "OR", "WHERE", "ON", "SELECT", "WHEN", "THEN", "ELSE", "CASE", "BY", "USING", "EXISTS", "HAVING", "VALUES", "FROM", "JOIN", "AS",
            "likely", "unlikely", "likelihood", "IS", "NOT", "LIKE", "GLOB", "BETWEEN", "MATCH", "REGEXP", "IN", "RETURNING", "SET",
        };

        static bool IsCompare(Tok t) => t.Kind == TokKind.Punct && t.Text is "=" or "==" or "<" or ">" or "<=" or ">=" or "!=" or "<>"
            || t.Kind == TokKind.Word && CompareWords.Contains(t.Text);

        /// <summary>
        /// Every text the statement compares a column named FormKey with: <c>FormKey = '…'</c>, <c>'…' = FormKey</c>,
        /// <c>FormKey IN ('…', …)</c>. Null when a FormKey may be compared with anything else (another column, a
        /// subquery, a parameter, an expression, a row value), is joined by USING, or the statement has a NATURAL
        /// join: then nothing is narrowed. A FormKey that isn't compared (in the column list, ORDER BY, a function's
        /// argument) is fine.
        /// </summary>
        static HashSet<string>? FormKeyLiterals(List<Tok> toks)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (toks.Any(t => t.Is("NATURAL"))) return null;
            Tok At(int i) => i >= 0 && i < toks.Count ? toks[i] : new Tok(TokKind.Punct, ";", 0, 0, 0);
            // A value ends here: nothing after it continues the expression
            bool Ends(int i) => i >= toks.Count || At(i).IsPunct(")") || At(i).IsPunct(";") || At(i).IsPunct(",")
                || At(i).Kind == TokKind.Word && !CompareWords.Contains(At(i).Text);
            // A value starts here: nothing before it belongs to the expression
            bool Starts(int i) => i < 0 || At(i).IsPunct("(") || At(i).IsPunct(",") || At(i).Kind == TokKind.Word && !CompareWords.Contains(At(i).Text);
            for (int i = 0; i < toks.Count; i++)
            {
                if (!toks[i].IsName || !toks[i].Text.Equals("FormKey", StringComparison.OrdinalIgnoreCase)) continue;
                var next = At(i + 1);
                if (next.IsPunct(".")) continue;   // a table or alias named FormKey
                int before = i - 1;
                if (At(before).IsPunct(".")) before -= 2;   // p.FormKey
                var prev = At(before);
                // Inside parentheses compared as a whole: (FormKey, Base) IN (…), (FormKey) = '…'
                if (toks[i].Depth > 0)
                {
                    int j = i + 1;
                    while (j < toks.Count && !(toks[j].IsPunct(")") && toks[j].Depth == toks[i].Depth - 1)) j++;
                    if (j + 1 < toks.Count && IsCompare(toks[j + 1])) return null;
                }
                if (next.IsPunct("=") || next.IsPunct("=="))
                {
                    if (At(i + 2).Kind == TokKind.String && Starts(before) && Ends(i + 3)) { keys.Add(At(i + 2).Text); continue; }
                    return null;
                }
                if (next.Is("IN"))
                {
                    if (!Starts(before) || !At(i + 2).IsPunct("(")) return null;
                    int j = i + 3;
                    var found = new List<string>();
                    while (true)
                    {
                        if (At(j).Kind != TokKind.String) return null;
                        found.Add(At(j).Text);
                        if (At(j + 1).IsPunct(")")) break;
                        if (!At(j + 1).IsPunct(",")) return null;
                        j += 2;
                    }
                    keys.UnionWith(found);
                    continue;
                }
                if (prev.IsPunct("=") || prev.IsPunct("=="))
                {
                    // '…' = FormKey
                    if (At(before - 1).Kind == TokKind.String && Starts(before - 2) && Ends(i + 1)) { keys.Add(At(before - 1).Text); continue; }
                    return null;
                }
                // Compared in any other way, or part of a longer expression
                if (next.Kind == TokKind.Punct && !(next.IsPunct(")") || next.IsPunct(",") || next.IsPunct(";")) || IsCompare(next)) return null;
                if (prev.Kind == TokKind.Punct && !(prev.IsPunct("(") || prev.IsPunct(",") || prev.IsPunct(";")) || IsCompare(prev)) return null;
                // A parenthesis that isn't a function's: USING (FormKey), WHERE (FormKey) …
                if (prev.IsPunct("(") && !(At(before - 1).Kind == TokKind.Word && !NotFunctions.Contains(At(before - 1).Text))) return null;
            }
            return keys;
        }

        /// <summary>
        /// The typed roots this statement may read by FormKey alone, with the FormKeys: every view of the root the
        /// statement reads is found only by FormKey in SQLite's own plan, and every FormKey it compares is a literal.
        /// </summary>
        void FindKeyedRoots(Probed probed, List<Tok> toks)
        {
            if (probed.Plan == null || probed.Views.Count == 0) return;
            var keys = FormKeyLiterals(toks);
            if (keys == null || keys.Count == 0) return;
            foreach (var group in probed.Views.Keys.GroupBy(v => v.Root, StringComparer.OrdinalIgnoreCase))
            {
                bool ok = true;
                foreach (var v in group)
                {
                    // The names a plan line can give this view by: its own and each alias the statement gives it
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { v.Name };
                    for (int i = 0; i < toks.Count; i++)
                    {
                        if (!toks[i].IsName || !toks[i].Text.Equals(v.Name, StringComparison.OrdinalIgnoreCase)) continue;
                        if (i + 1 < toks.Count && toks[i + 1].IsName) names.Add(toks[i + 1].Text);
                        if (i + 2 < toks.Count && toks[i + 1].Is("AS") && toks[i + 2].IsName) names.Add(toks[i + 2].Text);
                    }
                    string byKey = $" INDEX {KeyIndexName(v)} (FormKey=?)";
                    bool keyed = false;
                    foreach (string line in probed.Plan)
                    {
                        if (!(line.StartsWith("SCAN ", StringComparison.Ordinal) || line.StartsWith("SEARCH ", StringComparison.Ordinal))) continue;
                        string rest = line[(line.IndexOf(' ') + 1)..];
                        int sp = rest.IndexOf(' ');
                        string name = sp < 0 ? rest : rest[..sp];
                        if (line.Contains(byKey, StringComparison.Ordinal)) { keyed = true; continue; }
                        if (names.Contains(name)) { ok = false; break; }
                    }
                    if (!keyed) ok = false;
                    if (!ok) break;
                }
                if (ok) _keyedRoots[group.Key] = keys;
            }
        }

        static string KeyIndexName(ViewSpec v) => "sfs_fk_" + v.Name;

        /// <summary>The plan SQLite makes for the statement over the empty stand-in tables, with an index on each view's
        /// FormKey: one line each (SCAN …, SEARCH … USING INDEX …). Run through SQLite's own calls, so a parameter that
        /// isn't given yet is simply NULL.</summary>
        List<string> PlanLines(string sql, IEnumerable<ViewSpec> views)
        {
            foreach (var v in views.Distinct())
                ScratchFile.Exec(_con, $"CREATE INDEX temp.{SqlText.Name(KeyIndexName(v))} ON {SqlText.Name(v.Name)}(FormKey)");
            var lines = new List<string>();
            var db = _con.Handle!;
            if (raw.sqlite3_prepare_v2(db, "EXPLAIN QUERY PLAN " + sql, out var stmt) != raw.SQLITE_OK) { stmt?.Dispose(); return lines; }
            using (stmt)
                while (raw.sqlite3_step(stmt) == raw.SQLITE_ROW)
                    lines.Add(raw.sqlite3_column_text(stmt, 3).utf8_to_string());
            return lines;
        }

        /// <summary>The copies of the statement's FormKeys that a file holds, of one signature.</summary>
        List<RecordRef> KeyedRefs(ScratchFile file, string sig, IEnumerable<string> keys)
        {
            var members = new HashSet<string>(file.Members.Select(m => m.Plugin), StringComparer.OrdinalIgnoreCase);
            var refs = new List<RecordRef>();
            foreach (string key in keys)
            {
                if (Index.Versions(key) is not { } v) continue;
                foreach (var r in v.Holders)
                    if (members.Contains(Index.Plugins[r.Plugin].Name) && Index.Describe(r).Signature == sig) refs.Add(r);
            }
            return refs;
        }
    }
}
