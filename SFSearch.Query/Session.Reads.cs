// What a statement needs read, what that costs, and reading it into the scratch tables.
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Query;

public sealed partial class QueryEngine
{
    sealed partial class Session
    {
        static readonly HashSet<string> NotFields = new(StringComparer.OrdinalIgnoreCase) { "", "FormKey", "EditorID", "Plugin", "IsOverride", "LoadOrder", "IsWinner", "Type", "rowid" };

        bool Heavy(Estimate e) => e.Seconds > _e._options.HeavySeconds || e.MemoryMB > _e._options.HeavyMemoryMB;

        /// <summary>A read is heavy by its estimate, or by how many records it opens (QueryOptions.HeavyRecords).</summary>
        bool Heavy(ReadJob j) => Heavy(j.Estimate)
            || (j.Route == Route.Full || (j.Route == Route.Projection && j.Columns.Count > 0)) && j.Records > _e._options.HeavyRecords;

        List<(IndexedPlugin Plugin, SigLoad Load)> Loads(ScratchFile file, string sig)
        {
            var loads = new List<(IndexedPlugin, SigLoad)>();
            foreach (var m in file.Members)
            {
                var p = Index.Plugin(m.Plugin);
                if (p == null) continue;
                var load = CostModel.Load(p, sig);
                if (load.Count > 0) loads.Add((p, load));
            }
            return loads;
        }

        /// <summary>The reads this statement needs that the scratch tables don't hold yet, each with its estimate.</summary>
        List<ReadJob> PlanReads(Probed probed)
        {
            var jobs = new List<ReadJob>();
            foreach (var group in probed.Views.GroupBy(kv => kv.Key.Root, StringComparer.OrdinalIgnoreCase))
            {
                var typed = _e.Views.Catalog(group.Key).Typed;
                bool listUsed = group.Any(kv => kv.Key.IsList);
                var typedCols = new HashSet<string>(group.Where(kv => !kv.Key.IsList).SelectMany(kv => kv.Value), StringComparer.OrdinalIgnoreCase);
                var needed = typedCols.Where(c => !NotFields.Contains(c)).ToList();
                foreach (string sig in typed.Types.Select(t => _e._schema.SignatureOf(t)!).Distinct(StringComparer.Ordinal))
                {
                    _usedSigs.Add(sig);
                    string label = _e.LabelOf(sig);
                    foreach (var file in _files)
                    {
                        if (_done[file].ContainsKey(sig)) { _usedLabels.Add(label); continue; }
                        var loads = Loads(file, sig);
                        if (loads.Count == 0) continue;
                        var full = loads.Aggregate(Estimate.None, (a, l) => a + CostModel.Full(l.Load));
                        // Narrowed by FormKey (Session.Keyed.cs): only those records, when the whole read would be slow
                        // and the named columns already read don't answer it
                        if (_keyedRoots.TryGetValue(group.Key, out var keys) && (Heavy(full) || full.Seconds >= KeyedSeconds || loads.Sum(l => l.Load.Count) > _e._options.HeavyRecords)
                            && !(_projections.TryGetValue((file, typed.Types[0]), out var covered) && !listUsed && !typedCols.Contains("Json") && needed.All(covered.Columns.Contains)))
                        {
                            _keyedJobs.Add((file, KeyedRefs(file, sig, keys), false, sig, ""));
                            continue;
                        }
                        // A type too big to read whole, asked only for named columns: only those are read
                        bool projectable = !file.IsSet && !listUsed && typed.Types.Count == 1 && !typed.IsUnion && !typedCols.Contains("Json")
                            && !probed.Special.Contains("Links") && !probed.Special.Contains("Records") && Heavy(full) && needed.All(typed.ByName.ContainsKey);
                        if (!projectable) { jobs.Add(new ReadJob { File = file, Route = Route.Full, Sig = sig, Loads = loads, Estimate = full }); continue; }
                        var columns = new HashSet<string>(needed, StringComparer.OrdinalIgnoreCase);
                        if (_projections.TryGetValue((file, typed.Types[0]), out var have))
                        {
                            if (columns.IsSubsetOf(have.Columns)) { _usedLabels.Add($"{label} (named columns)"); continue; }
                            columns.UnionWith(have.Columns);
                        }
                        // In the view's own column order
                        var ordered = typed.Columns.Select(c => c.Name).Where(columns.Contains).ToList();
                        jobs.Add(new ReadJob
                        {
                            File = file, Route = Route.Projection, Sig = sig, Loads = loads, View = typed, Columns = ordered,
                            Estimate = loads.Aggregate(Estimate.None, (a, l) => a + CostModel.Projection(l.Load, ordered.Count)),
                        });
                    }
                }
            }
            if (probed.Special.Contains("Forms"))
                foreach (var file in _files)
                {
                    if (_done[file].ContainsKey(FormsDone)) { _usedLabels.Add("Forms"); continue; }
                    var loads = file.Members.Select(m => Index.Plugin(m.Plugin)).Where(p => p?.Segment != null)
                        .Select(p => (p!, new SigLoad(p!.Segment!.Count, p.Stamp.Length, 0, 0, p.Stamp.Length))).ToList();
                    jobs.Add(new ReadJob { File = file, Route = Route.Forms, Sig = FormsDone, Loads = loads, Estimate = CostModel.Forms(loads.Sum(l => l.Item2.Count)) });
                }
            if (probed.Special.Contains("Links"))
                foreach (var file in _files)
                {
                    var sigs = _done[file].Values.Where(d => !d.LinksDone && d.Sig != FormsDone).ToList();
                    var coming = jobs.Where(j => j.File == file && j.Route == Route.Full).ToList();
                    if (sigs.Count + coming.Count == 0) continue;
                    long jsonBytes = sigs.Sum(d => d.JsonBytes) + coming.Sum(j => j.Estimate.ScratchMB << 20);
                    jobs.Add(new ReadJob { File = file, Route = Route.Links, LinkSigs = sigs.Select(d => d.Sig).Concat(coming.Select(j => j.Sig)).ToList(), Estimate = CostModel.Links(jsonBytes) });
                }
            return jobs;
        }

        static string Seconds(double s) => s < 1 ? "under 1 s" : s < 90 ? $"about {Math.Round(s):F0} s" : $"about {Math.Round(s / 60):F0} min";
        static string Memory(long mb) => mb < 1024 ? "under 1 GB" : $"about {Math.Ceiling(mb / 512.0) / 2:0.#} GB";

        static string Count(long records) => records == 1 ? "1 record" : $"{records:N0} records";

        string Where(ReadJob j) => j.Loads.Count == 1 ? j.Loads[0].Plugin.Name : $"{j.Loads.Count:N0} plugins";

        string Describe(ReadJob j) => j.Route switch
        {
            Route.Full => $"{_e.LabelOf(j.Sig)} in {Where(j)} is {Count(j.Records)} ({AnswerSource.Size(j.Bytes)}): {Seconds(j.Estimate.Seconds)} and {Memory(j.Estimate.MemoryMB)} to read"
                + (j.Estimate.ScratchMB > 1024 ? $", and about {j.Estimate.ScratchMB / 1024.0:F1} GB of free disk for scratch files while it reads" : "")
                + (CostModel.IsMeasured(j.Sig) ? " (rates measured on Starfield.esm's whole " + _e.LabelOf(j.Sig) + ", 2026-11-12)" : ""),
            Route.Projection => $"{_e.LabelOf(j.Sig)} in {Where(j)} is {Count(j.Records)} ({AnswerSource.Size(j.Bytes)}): {Seconds(j.Estimate.Seconds)} and {Memory(j.Estimate.MemoryMB)} to read "
                + (j.Columns.Count == 0 ? "(no record is opened: the query needs only what the header index has)" : $"(only the column{(j.Columns.Count == 1 ? "" : "s")} the query names: {string.Join(", ", j.Columns)})"),
            Route.Forms => $"Forms for {Where(j)} is {j.Records:N0} records of the header index: {Seconds(j.Estimate.Seconds)} to put in a table",
            _ => $"Links of {j.LinkSigs.Count:N0} record type(s) read so far: {Seconds(j.Estimate.Seconds)} to index",
        };

        /// <summary>Cost said first: over the limit, the answer is the estimate, and nothing runs unless the request allows it.</summary>
        bool Admit(List<ReadJob> jobs)
        {
            if (jobs.Count == 0) return true;
            var total = jobs.Aggregate(Estimate.None, (a, j) => a + j.Estimate);
            var heavy = jobs.Where(Heavy).ToList();
            if (!Heavy(total) && heavy.Count == 0) return true;
            string what = heavy.Count > 0 ? string.Join(". ", heavy.Select(Describe)) + "."
                : $"This query reads {jobs.Count:N0} record types that aren't read yet ({string.Join(", ", jobs.Select(j => j.Route == Route.Forms ? "Forms" : j.Route == Route.Links ? "Links" : _e.LabelOf(j.Sig)).Distinct().Take(12))}): {Seconds(total.Seconds)} and {Memory(total.MemoryMB)} in all.";
            if (heavy.Count > 0 && jobs.Count > heavy.Count) what += $" With the other {jobs.Count - heavy.Count:N0} read(s): {Seconds(total.Seconds)} in all.";
            if (total.ScratchMB > 1024 && heavy.All(h => h.Estimate.ScratchMB <= 1024 || h.Route != Route.Full)) what += $" It writes about {total.ScratchMB / 1024.0:F1} GB of scratch files.";
            if (!_req.AllowHeavy)
            {
                _answer.NotRun = new HeavyEstimate(total.Seconds, total.MemoryMB, what + " Call again with allow_heavy=true (command line: --allow-heavy).");
                return false;
            }
            // Allowed: it still starts only when the memory is there, and the disk has room for the scratch files.
            // It never waits, and never starts and pages.
            if (total.ScratchMB > 1024)
            {
                long freeMB = ScratchFreeMB(), needMB = total.ScratchMB + _e._options.ScratchSpareMB;
                if (freeMB >= 0 && freeMB < needMB)
                    throw new QueryException($"needs about {needMB / 1024.0:F1} GB of free disk for its scratch files ({what.TrimEnd('.')}), and {freeMB / 1024.0:F1} GB is free where they go ({_e._options.ScratchFolder})",
                        "free some disk space, or give the engine a scratch folder on a drive with more room");
            }
            if (total.MemoryMB > _e._options.HeavyMemoryMB || heavy.Count > 0)
            {
                if (_e._options.HeavyGate != null)
                {
                    if (_e._options.HeavyGate(total.MemoryMB, what) is { } refused) throw new QueryException(refused, "try again later, or close other programs");
                }
                else
                {
                    long available = MemoryNow.AvailableMB(), need = total.MemoryMB + _e._options.HeavySpareMB;
                    if (available >= 0 && available < need)
                        throw new QueryException($"needs about {need / 1024.0:F1} GB, {available / 1024.0:F1} GB is free", "try again later, or close other programs");
                }
            }
            return true;
        }

        /// <summary>Free space where the scratch files go, in MB; -1 when it can't be asked.</summary>
        long ScratchFreeMB()
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_e._options.ScratchFolder))!).AvailableFreeSpace >> 20; }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return -1; }
        }

        void RunJobs(List<ReadJob> jobs)
        {
            if (jobs.Count == 0) return;
            bool heavyRecords = _e._records.AllowHeavy;
            _e._records.AllowHeavy = _req.AllowHeavy;
            try
            {
                foreach (var group in jobs.GroupBy(j => j.File))
                {
                    var file = group.Key;
                    using var hold = new ScratchFile.WriterLock(file.Path, _req.Cancel);
                    using var w = file.OpenWriter();
                    // The WAL is folded back into the file once, after the last read, not page by page while reading
                    ScratchFile.Exec(w, "PRAGMA wal_autocheckpoint = 0;");
                    foreach (var job in group)
                    {
                        _req.Cancel.ThrowIfCancellationRequested();
                        // Another process may have read it while this one waited for the file
                        var done = ScratchFile.ReadDone(w, "main");
                        switch (job.Route)
                        {
                            case Route.Full:
                                if (done.ContainsKey(job.Sig)) { _usedLabels.Add(_e.LabelOf(job.Sig)); break; }
                                FillFull(w, file, job);
                                break;
                            case Route.Projection:
                                if (done.ContainsKey(job.Sig)) { _usedLabels.Add(_e.LabelOf(job.Sig)); break; }
                                FillProjection(w, file, job);
                                break;
                            case Route.Forms:
                                if (!done.ContainsKey(FormsDone)) FillForms(w, file, job);
                                break;
                            case Route.Links:
                                FillLinks(w, file, done);
                                break;
                        }
                    }
                    file.FoldWal(w);
                }
            }
            finally { _e._records.AllowHeavy = heavyRecords; }
            foreach (var f in jobs.Select(j => j.File).Distinct()) RefreshDone(f);
        }

        static string Took(double seconds) => seconds < 0.0995 ? $"{seconds * 1000:F0} ms" : seconds.ToString("F2", CultureInfo.InvariantCulture) + " s";

        /// <summary>Rule 5 of section 6: a plugin that changed while it was read makes the read worthless; so do its string
        /// files (GAPS S31).</summary>
        void ConfirmUnchanged(IndexedPlugin p)
        {
            QuickStamp now;
            try { now = QuickStamp.Read(p.Path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { now = default; }
            if (now != p.Stamp) throw new QueryException($"{p.Name} changed during the read", "run the query again");
            if (p.Strings.Stamped && Index.StringsNow(new[] { p })[0] is var strings && !strings.SameAs(p.Strings))
                throw new QueryException($"the string files of {p.Name} changed during the read ({StringsStamp.Changes(p.Strings, strings)})", "run the query again");
        }

        void FillFull(SqliteConnection w, ScratchFile file, ReadJob job)
        {
            var clock = Stopwatch.StartNew();
            long rows = 0, jsonBytes = 0; int skipped = 0;
            var plan = new ReadPlan { Full = true, Winners = file.IsSet };
            using var tx = w.BeginTransaction();
            // Ord is the row's place in file order. It sits in the index beside the columns every view has, so a query
            // that reads no field of the record is answered from the index alone, in file order.
            long ord;
            using (var top = w.CreateCommand()) { top.CommandText = "SELECT coalesce(max(Seq), 0) FROM Rec"; ord = (long)top.ExecuteScalar()!; }
            using (var ins = new BulkInsert(w, "INSERT INTO Rec(Seq, Ord, Type, Sig, FormKey, EditorID, IsOverride, Pos, Plugin, LoadOrder, IsWinner, Json, Lnk) VALUES (?12,?12,?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11)"))
            using (var skip = new BulkInsert(w, "INSERT INTO Skipped(Sig, Plugin, FormKey, Reason) VALUES (?,?,?,?)"))
            {
                foreach (var (plugin, load) in job.Loads)
                {
                    var member = file.Members.First(m => m.Plugin.Equals(plugin.Name, StringComparison.OrdinalIgnoreCase));
                    try { _e._records.Sessions.Get(plugin.Name); }
                    catch (RecordException e)
                    {
                        skip.Text(1, job.Sig); skip.Text(2, plugin.Name); skip.Null(3); skip.Text(4, e.Message); skip.Step();
                        skipped += (int)load.Count;
                        continue;
                    }
                    foreach (var batch in _e._reader.Read(plugin, job.Sig, plan, _req.Cancel))
                        foreach (var row in batch)
                        {
                            if (row.Skip != null)
                            {
                                skip.Text(1, job.Sig); skip.Text(2, plugin.Name); skip.Text(3, row.FormKey); skip.Text(4, $"{row.EditorId ?? "(no EditorID)"} ({row.FormKey}): {row.Skip}"); skip.Step();
                                skipped++;
                                continue;
                            }
                            ins.Text(1, row.Type); ins.Text(2, job.Sig); ins.Text(3, row.FormKey); ins.Text(4, row.EditorId); ins.Int(5, row.IsOverride ? 1 : 0); ins.Int(6, row.Pos);
                            if (file.IsSet) { ins.Text(7, plugin.Name); ins.Int(8, member.LoadOrder); ins.Int(9, row.IsWinner ? 1 : 0); }
                            else { ins.Null(7); ins.Null(8); ins.Null(9); }
                            ins.Blob(10, row.Jsonb); ins.Blob(11, row.Links); ins.Int(12, ++ord);
                            ins.Step();
                            rows++; jsonBytes += row.JsonBytes;
                        }
                    ConfirmUnchanged(plugin);
                }
            }
            using (var cmd = w.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO Done(Sig, Rows, JsonBytes, Seconds, Skipped, LinksDone) VALUES ($s, $r, $j, $t, $k, 0)";
                cmd.Parameters.AddWithValue("$s", job.Sig); cmd.Parameters.AddWithValue("$r", rows); cmd.Parameters.AddWithValue("$j", jsonBytes);
                cmd.Parameters.AddWithValue("$t", clock.Elapsed.TotalSeconds); cmd.Parameters.AddWithValue("$k", skipped);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            string label = _e.LabelOf(job.Sig);
            _readLabels.Add(label); _usedLabels.Add(label);
            _answer.Cost.Read.Add($"{label} ({rows:N0}) from {Where(job)} in {Took(clock.Elapsed.TotalSeconds)}");
        }

        void FillProjection(SqliteConnection w, ScratchFile file, ReadJob job)
        {
            var clock = Stopwatch.StartNew();
            var view = job.View!;
            string type = view.Types[0];
            long rows = 0; int skipped = 0;
            using var tx = w.BeginTransaction();
            long n;
            using (var count = w.CreateCommand()) { count.CommandText = "SELECT count(*) FROM Proj WHERE Type = $t"; count.Parameters.AddWithValue("$t", type); n = (long)count.ExecuteScalar()!; }
            string table = $"X_{type}_{n + 1}";
            var defs = job.Columns.Select(c => SqlText.Name(c) + (view.ByName[c].IsLink ? " TEXT COLLATE NOCASE" : ""));
            ScratchFile.Exec(w, $"CREATE TABLE {SqlText.Name(table)}(Seq INTEGER PRIMARY KEY, FormKey TEXT NOT NULL COLLATE NOCASE, EditorID TEXT COLLATE NOCASE, IsOverride INT NOT NULL, Pos INT NOT NULL{string.Concat(defs.Select(d => ", " + d))})");
            // Each column straight from Mutagen's object where it can be (DirectColumns.cs), the rest from the JSON
            var direct = job.Columns.Select(c => _e._options.DirectColumns ? DirectColumn.For(view.ByName[c]) : null).ToArray();
            var fromJson = job.Columns.Where((c, i) => direct[i] == null).ToList();
            var plan = new ReadPlan
            {
                Full = false, ProjectColumns = fromJson.Count,
                ProjectSql = fromJson.Count == 0 ? null : JsonbMaker.ProjectSql(ViewBuilder.ProjectionExprs(view, fromJson, _language)),
                Direct = direct.Any(d => d != null) ? direct : null,
            };
            string marks = string.Concat(Enumerable.Repeat(",?", job.Columns.Count));
            using (var ins = new BulkInsert(w, $"INSERT INTO {SqlText.Name(table)} VALUES (NULL,?,?,?,?{marks})"))
            using (var skip = new BulkInsert(w, "INSERT INTO Skipped(Sig, Plugin, FormKey, Reason) VALUES (?,?,?,?)"))
            {
                foreach (var (plugin, _) in job.Loads)
                {
                    foreach (var batch in _e._reader.Read(plugin, job.Sig, plan, _req.Cancel))
                        foreach (var row in batch)
                        {
                            if (row.Skip != null)
                            {
                                skip.Text(1, job.Sig); skip.Text(2, plugin.Name); skip.Text(3, row.FormKey); skip.Text(4, $"{row.EditorId ?? "(no EditorID)"} ({row.FormKey}): {row.Skip}"); skip.Step();
                                skipped++;
                                continue;
                            }
                            ins.Text(1, row.FormKey); ins.Text(2, row.EditorId); ins.Int(3, row.IsOverride ? 1 : 0); ins.Int(4, row.Pos);
                            for (int i = 0; i < job.Columns.Count; i++) ins.Value(5 + i, row.Values![i]);
                            ins.Step();
                            rows++;
                        }
                    ConfirmUnchanged(plugin);
                }
            }
            using (var cmd = w.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO Proj(Type, TableName, Cols, Rows) VALUES ($t, $n, $c, $r)";
                cmd.Parameters.AddWithValue("$t", type); cmd.Parameters.AddWithValue("$n", table);
                cmd.Parameters.AddWithValue("$c", _language + "|" + string.Join(",", job.Columns)); cmd.Parameters.AddWithValue("$r", rows);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            string label = $"{_e.LabelOf(job.Sig)} (named columns)";
            _readLabels.Add(label); _usedLabels.Add(label);
            int directs = plan.Direct?.Count(d => d != null) ?? 0;
            string cols = job.Columns.Count == 0 ? "from the header index alone" : $"column{(job.Columns.Count == 1 ? "" : "s")} {string.Join(", ", job.Columns)}"
                + (directs == 0 ? "" : directs == job.Columns.Count ? ", straight from the records" : $", {directs:N0} of them straight from the records");
            _answer.Cost.Read.Add($"{_e.LabelOf(job.Sig)} ({rows:N0}, {cols}) from {Where(job)} in {Took(clock.Elapsed.TotalSeconds)}");
            if (skipped > 0) Note($"{_e.LabelOf(job.Sig)}: {skipped:N0} record(s) couldn't be read and aren't counted.");
        }

        void FillForms(SqliteConnection w, ScratchFile file, ReadJob job)
        {
            var clock = Stopwatch.StartNew();
            long rows = 0;
            using var tx = w.BeginTransaction();
            using (var ins = new BulkInsert(w, "INSERT INTO FormRows(FormKey, Sig, EditorID, Plugin, Parent, IsOverride, Pos, LoadOrder, IsWinner) VALUES (?,?,?,?,?,?,?,?,?)"))
            {
                foreach (var (plugin, _) in job.Loads)
                {
                    var member = file.Members.First(m => m.Plugin.Equals(plugin.Name, StringComparison.OrdinalIgnoreCase));
                    foreach (var batch in _e._reader.ReadIndex(plugin, file.IsSet, _req.Cancel))
                        foreach (var row in batch)
                        {
                            ins.Text(1, row.FormKey); ins.Text(2, row.Sig); ins.Text(3, row.EditorId);
                            if (file.IsSet) ins.Text(4, plugin.Name); else ins.Null(4);
                            ins.Text(5, row.Parent); ins.Int(6, row.IsOverride ? 1 : 0); ins.Int(7, row.Pos);
                            if (file.IsSet) { ins.Int(8, member.LoadOrder); ins.Int(9, row.IsWinner ? 1 : 0); } else { ins.Null(8); ins.Null(9); }
                            ins.Step();
                            rows++;
                        }
                }
            }
            ScratchFile.Exec(w, "CREATE INDEX IF NOT EXISTS Forms_FormKey ON FormRows(FormKey); CREATE INDEX IF NOT EXISTS Forms_EditorID ON FormRows(EditorID) WHERE EditorID IS NOT NULL;");
            using (var cmd = w.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO Done(Sig, Rows, JsonBytes, Seconds, Skipped, LinksDone) VALUES ($s, $r, 0, $t, 0, 1)";
                cmd.Parameters.AddWithValue("$s", FormsDone); cmd.Parameters.AddWithValue("$r", rows); cmd.Parameters.AddWithValue("$t", clock.Elapsed.TotalSeconds);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            _answer.Cost.Read.Add($"Forms ({rows:N0} records of the header index) for {Where(job)} in {Took(clock.Elapsed.TotalSeconds)}");
        }

        /// <summary>Puts the links of every record type read so far into the Links table. The links themselves were
        /// found on the reading threads when each record was read; here they only become rows with two indexes.</summary>
        void FillLinks(SqliteConnection w, ScratchFile file, Dictionary<string, DoneRow> done)
        {
            var clock = Stopwatch.StartNew();
            var sigs = done.Values.Where(d => !d.LinksDone && d.Sig != FormsDone).Select(d => d.Sig).ToList();
            if (sigs.Count == 0) return;
            long links = 0;
            using var tx = w.BeginTransaction();
            using (var ins = new BulkInsert(w, "INSERT INTO LinkRows(FromKey, FromType, Field, Idx, ToKey, Path, Plugin, IsWinner) VALUES (?,?,?,?,?,?,?,?)"))
            using (var read = w.CreateCommand())
            {
                read.CommandText = $"SELECT FormKey, Type, Plugin, IsWinner, Lnk FROM Rec WHERE Sig IN ({string.Join(", ", sigs.Select(SqlText.Literal))}) AND Lnk IS NOT NULL ORDER BY Seq";
                using var r = read.ExecuteReader();
                while (r.Read())
                {
                    string from = r.GetString(0), type = r.GetString(1);
                    string? plugin = r.IsDBNull(2) ? null : r.GetString(2);
                    long? winner = r.IsDBNull(3) ? null : r.GetInt64(3);
                    foreach (var (field, idx, path, to) in LinkWalker.Lines((byte[])r.GetValue(4)))
                    {
                        ins.Text(1, from); ins.Text(2, type); ins.Text(3, field); ins.Int(4, idx); ins.Text(5, to); ins.Text(6, path); ins.Text(7, plugin); ins.Int(8, winner);
                        ins.Step();
                        links++;
                    }
                }
            }
            ScratchFile.Exec(w, $"UPDATE Done SET LinksDone = 1 WHERE Sig IN ({string.Join(", ", sigs.Select(SqlText.Literal))})");
            tx.Commit();
            _answer.Cost.Read.Add($"Links ({links:N0}) of {sigs.Count:N0} record type(s) indexed in {Took(clock.Elapsed.TotalSeconds)}");
        }

        // -------------------------------------------------------------- The views
        ViewBuilder.Member MemberOf(ScratchFile f) => f.IsSet
            ? new ViewBuilder.Member(f.Alias, null, _winners ? "r.IsWinner = 1" : null, true, null)
            : new ViewBuilder.Member(f.Alias, f.Members[0].Plugin, null, false, f.Members[0].LoadOrder);

        string PluginExpr(ScratchFile f, string alias) => f.IsSet ? $"{alias}.Plugin" : SqlText.Literal(f.Members[0].Plugin) + " COLLATE NOCASE";

        void CreateViews(Probed probed)
        {
            foreach (var (view, used) in probed.Views)
            {
                bool withJson = used.Contains("Json");
                var only = new HashSet<string>(used, StringComparer.OrdinalIgnoreCase);
                var selects = new List<string>();
                foreach (var f in _files)
                {
                    string sig = _e._schema.SignatureOf(view.Types[0])!;
                    if (_keyed.TryGetValue(f, out var keyed) && keyed.ViewSigs.Contains(sig))
                    {
                        // Read by FormKey for this statement: the view reads those rows
                        selects.Add(ViewBuilder.SelectSql(view, MemberOf(f) with { Table = "temp." + SqlText.Name(keyed.Rec) }, _language, _versions, only, withJson));
                        continue;
                    }
                    if (!view.IsList && !_done[f].ContainsKey(sig) && _projections.TryGetValue((f, view.Types[0]), out var proj))
                    {
                        // Only the named columns of this type were read: the view gives those
                        var cols = view.Columns.Where(c => only.Contains(c.Name)).Select(c => $", x.{SqlText.Name(c.Name)} AS {SqlText.Name(c.Name)}");
                        selects.Add($"SELECT x.FormKey AS FormKey, x.EditorID AS EditorID, {PluginExpr(f, "x")} AS Plugin, x.IsOverride AS IsOverride{string.Concat(cols)} FROM {f.Alias}.{SqlText.Name(proj.Table)} x");
                        continue;
                    }
                    selects.Add(ViewBuilder.SelectSql(view, MemberOf(f), _language, _versions, only, withJson));
                }
                ScratchFile.Exec(_con, $"CREATE TEMP VIEW {SqlText.Name(view.Name)} AS {string.Join(" UNION ALL ", selects)}");
                _ownViews.Add(view.Name);
            }
            foreach (string special in probed.Special)
            {
                var selects = new List<string>();
                foreach (var f in _files)
                {
                    string plugin = PluginExpr(f, "r");
                    string where = f.IsSet && _winners ? " WHERE r.IsWinner = 1" : "";
                    // The records a byte search found for Links WHERE ToKey, read alone (Session.Keyed.cs)
                    var byBytes = _keyed.TryGetValue(f, out var k) && k.ForLinks ? k : null;
                    switch (special)
                    {
                        case "Records":
                            selects.Add($"SELECT r.FormKey AS FormKey, r.Type AS Type, r.Sig AS Sig, r.EditorID AS EditorID, sfs_text(r.Json -> '$.Name', {SqlText.Literal(_language)}) AS Name, {plugin} AS Plugin, r.IsOverride AS IsOverride"
                                + (_versions ? ", r.LoadOrder AS LoadOrder, r.IsWinner AS IsWinner" : "") + $", json(r.Json) AS Json FROM {f.Alias}.Rec r{where}");
                            if (byBytes != null) selects[^1] += " UNION ALL " + selects[^1].Replace($" FROM {f.Alias}.Rec r", $" FROM temp.{SqlText.Name(byBytes.Rec)} r");
                            break;
                        case "Forms":
                            selects.Add($"SELECT r.FormKey AS FormKey, r.Sig AS Sig, (SELECT t.Type FROM temp.sfs_sigtype t WHERE t.Sig = r.Sig) AS Type, r.EditorID AS EditorID, {plugin} AS Plugin, r.Parent AS Parent, "
                                + (f.IsSet ? "r.IsWinner" : $"sfs_iswinner(r.FormKey, {plugin})") + " AS IsWinner, r.IsOverride AS IsOverride, r.Pos AS Pos, "
                                + (f.IsSet ? "r.LoadOrder" : f.Members[0].LoadOrder is int lo ? lo.ToString(CultureInfo.InvariantCulture) : "NULL") + $" AS LoadOrder FROM {f.Alias}.FormRows r{where}");
                            break;
                        case "Links":
                            selects.Add($"SELECT r.FromKey AS FromKey, r.FromType AS FromType, r.Field AS Field, r.Idx AS Idx, r.ToKey AS ToKey, r.Path AS Path, {plugin} AS Plugin FROM {f.Alias}.LinkRows r{where}");
                            if (byBytes != null) selects[^1] += " UNION ALL " + selects[^1].Replace($" FROM {f.Alias}.LinkRows r", $" FROM temp.{SqlText.Name(byBytes.Links)} r");
                            break;
                    }
                }
                ScratchFile.Exec(_con, $"CREATE TEMP VIEW {SqlText.Name(special)} AS {string.Join(" UNION ALL ", selects)}");
                _ownViews.Add(special);
            }
            // With a byte search's records in it the answer already says how it was found (Session.AutoReads.cs)
            if (probed.Special.Contains("Links") && !_keyed.Values.Any(k => k.ForLinks))
            {
                var types = _files.SelectMany(f => _done[f].Values).Where(d => d.Sig != FormsDone).Select(d => _e.LabelOf(d.Sig)).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();
                Note($"Links holds the links of the record types read so far in this scope ({(types.Count == 0 ? "none yet" : $"{types.Count:N0}: {string.Join(", ", types.Take(12))}{(types.Count > 12 ? ", …" : "")}")}). It is not a search of every type: narrow it (WHERE ToKey = '…', FromKey = '…' or FromType = '…') and what that needs is read first, or name a type's view.");
            }
        }
    }
}
