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

        /// <summary>A read is heavy by its estimate, or by how many records it opens (QueryOptions.HeavyRecords).
        /// A read of a type's columns filtered while reading from the records' own bytes (Session.Filter.cs) opens no
        /// record through Mutagen and inserts only the matching rows: its estimate alone says.</summary>
        bool Heavy(ReadJob j) => Heavy(j.Estimate)
            || (j.Route == Route.Full || (j.Route == Route.Projection && j.Columns.Count > 0) || (j.Route == Route.KeyedEstimate && j.Filter != null && !j.Raw)) && j.Records > _e._options.HeavyRecords;

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

        /// <summary>Named columns of a typed view that all come from the records' own bytes (RawColumn.For): read so whatever
        /// the type's size, as stepping through the bytes costs less than Mutagen's read of every record (an NPC's Name, a
        /// weapon's: GAPS S165).</summary>
        bool FromBytes(ViewSpec typed, IReadOnlyCollection<string> needed)
        {
            if (needed.Count == 0 || !_e._options.DirectColumns) return false;
            var asked = new HashSet<string>(needed, StringComparer.OrdinalIgnoreCase);
            var ordered = typed.Columns.Select(c => c.Name).Where(asked.Contains).ToList();
            return ordered.Count == asked.Count && ProjectionPlan(typed, ordered, false, null).Raw != null;
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
                            && !(!listUsed && !typedCols.Contains("Json") && ProjectionTables(file, typed.Types[0], needed) != null))
                        {
                            _keyedJobs.Add((file, KeyedRefs(file, sig, keys), false, sig, ""));
                            continue;
                        }
                        // List views alone of a type too big or slow to read whole: the records that hold the lists' subrecords (ListReads.cs)
                        if (PlanListReads(group, typed, file, sig, loads, full, probed, jobs)) continue;
                        // A type too big to read whole, asked only for named columns: only those are read. In a set of
                        // plugins (the load order, the versions, a long list) each plugin's records in turn, each row with
                        // its plugin, its place in the load order and whether it wins, from the header index (GAPS S89)
                        // (a type that shares its signature with others, GMST's and OMOD's, too: the read takes only the
                        // records that are the type's, by their bytes or by Mutagen's read: ReadPlan.OnlyType)
                        bool projectable = !listUsed && typed.Types.Count == 1 && !typed.IsUnion && !typedCols.Contains("Json")
                            && !probed.Special.Contains("Links") && !probed.Special.Contains("Records") && needed.All(typed.ByName.ContainsKey)
                            && (Heavy(full) || FromBytes(typed, needed));
                        if (!projectable) { jobs.Add(new ReadJob { File = file, Route = Route.Full, Sig = sig, Loads = loads, Estimate = full }); continue; }
                        var columns = new HashSet<string>(needed, StringComparer.OrdinalIgnoreCase);
                        if (ProjectionTables(file, typed.Types[0], columns) != null) { _usedLabels.Add($"{label} (named columns)"); continue; }
                        // Only the rows with the statement's col = literal, into a table of the statement (Session.Filter.cs)
                        var asked = typed.Columns.Select(c => c.Name).Where(columns.Contains).ToList();
                        if (FilterOf(typed, asked) is { } filter)
                        {
                            var filterPlan = ProjectionPlan(typed, asked, false, filter);
                            bool rawRead = filterPlan.Raw != null, filterJson = filterPlan.ProjectSql != null;
                            _keyedProjJobs.Add(new KeyedProjJob
                            {
                                File = file, View = typed, Columns = asked, Sig = sig, Filter = filter,
                                Sets = loads.Select(l => (l.Plugin, l.Plugin.Segment!.EntriesOf(sig).ToArray())).ToList(),
                            });
                            jobs.Add(new ReadJob
                            {
                                File = file, Route = Route.KeyedEstimate, Sig = sig, Loads = loads, View = typed, Columns = asked, Filter = filter, Raw = rawRead, Json = filterJson,
                                Estimate = loads.Aggregate(Estimate.None, (a, l) => a + (rawRead ? CostModel.Raw(l.Load, l.Load.Count / 100) : CostModel.Projection(l.Load, asked.Count, filterJson))),
                            });
                            continue;
                        }
                        // Only the columns no table of the type holds yet, each read on its own best route: a column read before
                        // through Mutagen doesn't send this read through Mutagen too (GAPS S146); the view joins the tables
                        if (_projectionTables.TryGetValue((file, typed.Types[0]), out var tables))
                            foreach (var t in tables) columns.ExceptWith(t.Columns);
                        // In the view's own column order
                        var ordered = typed.Columns.Select(c => c.Name).Where(columns.Contains).ToList();
                        var plan = ProjectionPlan(typed, ordered, false, null);
                        bool raw = ordered.Count > 0 && plan.Raw != null, json = plan.ProjectSql != null;
                        jobs.Add(new ReadJob
                        {
                            File = file, Route = Route.Projection, Sig = sig, Loads = loads, View = typed, Columns = ordered, Raw = raw, Json = json,
                            Estimate = loads.Aggregate(Estimate.None, (a, l) => a + (raw ? CostModel.Raw(l.Load, l.Load.Count) : CostModel.Projection(l.Load, ordered.Count, json))),
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
            // Conditions (Conditions.cs): taken out of the JSON of the types read so far that can hold one
            if (probed.Special.Contains("Conditions"))
                foreach (var file in _files)
                {
                    var holding = _e.ConditionSigs;
                    var sigs = _done[file].Values.Where(d => !d.CondDone && d.Sig != FormsDone).ToList();
                    var coming = jobs.Where(j => j.File == file && j.Route == Route.Full).ToList();
                    if (sigs.Count + coming.Count == 0) continue;
                    long jsonBytes = sigs.Where(d => holding.Contains(d.Sig)).Sum(d => d.JsonBytes) + coming.Where(j => holding.Contains(j.Sig)).Sum(j => j.Estimate.ScratchMB << 20);
                    jobs.Add(new ReadJob { File = file, Route = Route.Conditions, LinkSigs = sigs.Select(d => d.Sig).Concat(coming.Select(j => j.Sig)).ToList(), Estimate = CostModel.Links(jsonBytes) });
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
                + (j.Columns.Count == 0 ? "(no record is opened: the query needs only what the header index has)" : $"(only the column{(j.Columns.Count == 1 ? "" : "s")} the query names: {string.Join(", ", j.Columns)}{(j.Raw ? ", from the records' own bytes" : "")}{JsonWords(j)})"),
            Route.Forms => $"Forms for {Where(j)} is {j.Records:N0} records of the header index: {Seconds(j.Estimate.Seconds)} to put in a table",
            Route.ListProjection => $"{j.View!.Name}: {j.Records:N0} {_e.LabelOf(j.Sig)} record{(j.Records == 1 ? "" : "s")} of {Where(j)} that hold its subrecords: {Seconds(j.Estimate.Seconds)} and {Memory(j.Estimate.MemoryMB)} to read ({(j.Columns.Count == 0 ? "no column of the entries, only their places" : j.List != null ? $"only the column{(j.Columns.Count == 1 ? "" : "s")} the query names: {string.Join(", ", j.Columns)}, straight from the records" : $"{string.Join(", ", j.Columns.Select(c => c == ViewBuilder.EntryColumn ? "each entry" : c))}, out of each record's JSON, one record at a time")})",
            Route.KeyedEstimate when j.Filter != null => $"{j.View!.Name} in {Where(j)} is {Count(j.Records)} ({AnswerSource.Size(j.Bytes)}): {Seconds(j.Estimate.Seconds)} and {Memory(j.Estimate.MemoryMB)} to read "
                + $"(only the column{(j.Columns.Count == 1 ? "" : "s")} the query names: {string.Join(", ", j.Columns)}{(j.Raw ? ", from the records' own bytes" : "")}{JsonWords(j)}; only the records with {FilterText(j.Filter)} kept)",
            Route.KeyedEstimate => $"{j.View!.Name}: the {Count(j.Records)} of {Where(j)} whose FormKeys hold the list it is joined to, {(j.Whole ? "whole" : j.Columns.Count == 0 ? "from the header index alone" : $"only the column{(j.Columns.Count == 1 ? "" : "s")} the query names: {string.Join(", ", j.Columns)}")}: {Seconds(j.Estimate.Seconds)} and {Memory(j.Estimate.MemoryMB)} to read",
            Route.Conditions => $"Conditions of {j.LinkSigs.Count:N0} record type(s) read so far: {Seconds(j.Estimate.Seconds)} to take out",
            _ => $"Links of {j.LinkSigs.Count:N0} record type(s) read so far: {Seconds(j.Estimate.Seconds)} to index",
        };

        /// <summary>", out of each record's JSON; rate measured on Starfield.esm's Cell, 2026-11-15": what a read of named
        /// columns costs when one of them comes from the JSON (GAPS S101).</summary>
        string JsonWords(ReadJob j) => !j.Json ? "" : ", out of each record's JSON" + (CostModel.IsProjectionMeasured(j.Sig) ? $"; rate measured on Starfield.esm's {_e.LabelOf(j.Sig)}, 2026-11-15" : "");

        /// <summary>Cost said first: over the limit, the answer is the estimate, and nothing runs unless the request allows it.</summary>
        bool Admit(List<ReadJob> jobs)
        {
            if (jobs.Count == 0) return true;
            var total = jobs.Aggregate(Estimate.None, (a, j) => a + j.Estimate);
            var heavy = jobs.Where(Heavy).ToList();
            if (!Heavy(total) && heavy.Count == 0) return true;
            string what = heavy.Count > 0 ? string.Join(". ", heavy.Select(Describe)) + "."
                : $"This query reads {jobs.Count:N0} record types that aren't read yet ({string.Join(", ", jobs.Select(j => j.Route == Route.Forms ? "Forms" : j.Route == Route.Links ? "Links" : j.Route == Route.Conditions ? "Conditions" : _e.LabelOf(j.Sig)).Distinct().Take(12))}): {Seconds(total.Seconds)} and {Memory(total.MemoryMB)} in all.";
            if (heavy.Count > 0 && jobs.Count > heavy.Count) what += $" With the other {jobs.Count - heavy.Count:N0} read(s): {Seconds(total.Seconds)} in all.";
            if (total.ScratchMB > 1024 && heavy.All(h => h.Estimate.ScratchMB <= 1024 || h.Route != Route.Full)) what += $" It writes about {total.ScratchMB / 1024.0:F1} GB of scratch files.";
            if (!_req.AllowHeavy)
            {
                // The three answers (J33): run it now, leave it, or (where the world link index would answer it) run it and
                // save that index; else why no index would help
                var offer = !_anyOr && jobs.Any(j => _worldHelpSigs.Contains(j.Sig) || j.Route is Route.Links && j.LinkSigs.Any(_worldHelpSigs.Contains)) ? WorldOffer() : null;
                _answer.NotRun = new HeavyEstimate(total.Seconds, total.MemoryMB, what + HeavyAnswers.NotRunTail(offer,
                    "SFSearch keeps indexes of where links are, not of the fields this query reads; the world link index answers Links WHERE ToKey = '…' over world data"))
                    { SaveIndex = offer };
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
                                // Or its named columns alone: the tables it left hold them, so they aren't read again
                                if (ProjHolds(w, job.View!.Types[0], job.Columns, acrossTables: true)) { _usedLabels.Add($"{_e.LabelOf(job.Sig)} (named columns)"); break; }
                                FillProjection(w, file, job);
                                break;
                            case Route.Forms:
                                if (!done.ContainsKey(FormsDone)) FillForms(w, file, job);
                                break;
                            case Route.Links:
                                FillLinks(w, file, done);
                                break;
                            case Route.Conditions:
                                FillConditions(w, done);
                                break;
                            case Route.ListProjection:
                                if (done.ContainsKey(job.Sig)) { _usedLabels.Add(_e.LabelOf(job.Sig)); break; }
                                if (ProjHolds(w, "list:" + job.View!.Name, job.Columns, acrossTables: false)) { _usedLabels.Add($"{job.View!.Name} (named columns)"); break; }
                                FillListProjection(w, file, job);
                                break;
                        }
                    }
                    file.FoldWal(w);
                }
            }
            finally { _e._records.AllowHeavy = heavyRecords; }
            foreach (var f in jobs.Select(j => j.File).Distinct()) RefreshDone(f);
        }

        /// <summary>
        /// Whether the scratch file already holds a type's named columns, asked of the file itself once this process
        /// has the right to write it: another process may have read the same columns while this one waited, and the
        /// plan was made before that (two processes on one scratch folder read Starfield.esm's Perk columns twice,
        /// sfsquery-tests two-processes). A typed view's columns may come from several of its tables (GAPS S146); a
        /// list view's from one.
        /// </summary>
        bool ProjHolds(SqliteConnection w, string type, IReadOnlyCollection<string> columns, bool acrossTables)
        {
            var left = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
            bool any = false;
            using var cmd = w.CreateCommand();
            cmd.CommandText = "SELECT Cols FROM Proj WHERE Type = $t";
            cmd.Parameters.AddWithValue("$t", type);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                // Cols: the language, a bar, the column names (RefreshDone)
                string cols = r.GetString(0);
                int bar = cols.IndexOf('|');
                if (bar < 0 || !cols[..bar].Equals(_language, StringComparison.OrdinalIgnoreCase)) continue;
                var set = new HashSet<string>(cols[(bar + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
                any = true;
                if (acrossTables) left.ExceptWith(set);
                else if (left.IsSubsetOf(set)) return true;
            }
            return acrossTables && any && left.Count == 0;
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
                    foreach (var row in _e._reader.Read(plugin, job.Sig, plan, _req.Cancel))
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
            var plan = ProjectionPlan(view, job.Columns, file.IsSet, null);
            long rows = 0; int skipped = 0;
            using var tx = w.BeginTransaction();
            long n;
            using (var count = w.CreateCommand()) { count.CommandText = "SELECT count(*) FROM Proj WHERE Type = $t"; count.Parameters.AddWithValue("$t", type); n = (long)count.ExecuteScalar()!; }
            string table = $"X_{type}_{n + 1}";
            var defs = job.Columns.Select(c => SqlText.Name(c) + (view.ByName[c].IsLink ? " TEXT COLLATE NOCASE" : ""));
            // A set's rows say whose they are: the plugin, its place in the load order, whether it is the version that wins
            string setCols = file.IsSet ? ", Plugin TEXT COLLATE NOCASE, LoadOrder INT, IsWinner INT" : "";
            ScratchFile.Exec(w, $"CREATE TABLE {SqlText.Name(table)}(Seq INTEGER PRIMARY KEY, FormKey TEXT NOT NULL COLLATE NOCASE, EditorID TEXT COLLATE NOCASE, IsOverride INT NOT NULL, Pos INT NOT NULL{setCols}{string.Concat(defs.Select(d => ", " + d))})");
            int first = file.IsSet ? 8 : 5;
            string marks = string.Concat(Enumerable.Repeat(",?", job.Columns.Count + first - 5));
            using (var ins = new BulkInsert(w, $"INSERT INTO {SqlText.Name(table)} VALUES (NULL,?,?,?,?{marks})"))
            using (var skip = new BulkInsert(w, "INSERT INTO Skipped(Sig, Plugin, FormKey, Reason) VALUES (?,?,?,?)"))
            {
                foreach (var (plugin, _) in job.Loads)
                {
                    var member = file.Members.First(m => m.Plugin.Equals(plugin.Name, StringComparison.OrdinalIgnoreCase));
                    foreach (var row in _e._reader.Read(plugin, job.Sig, plan, _req.Cancel))
                    {
                        if (row.Skip != null)
                        {
                            skip.Text(1, job.Sig); skip.Text(2, plugin.Name); skip.Text(3, row.FormKey); skip.Text(4, $"{row.EditorId ?? "(no EditorID)"} ({row.FormKey}): {row.Skip}"); skip.Step();
                            skipped++;
                            continue;
                        }
                        ins.Text(1, row.FormKey); ins.Text(2, row.EditorId); ins.Int(3, row.IsOverride ? 1 : 0); ins.Int(4, row.Pos);
                        if (file.IsSet) { ins.Text(5, plugin.Name); ins.Int(6, member.LoadOrder); ins.Int(7, row.IsWinner ? 1 : 0); }
                        for (int i = 0; i < job.Columns.Count; i++) ins.Value(first + i, row.Values![i]);
                        ins.Step();
                        rows++;
                    }
                    ConfirmUnchanged(plugin);
                }
            }
            // A type's second table of columns: the view joins its tables by FormKey (GAPS S146), so each gets that index
            if (n > 0)
            {
                var names = new List<string> { table };
                using (var cmd = w.CreateCommand())
                {
                    cmd.CommandText = "SELECT TableName FROM Proj WHERE Type = $t";
                    cmd.Parameters.AddWithValue("$t", type);
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) names.Add(r.GetString(0));
                }
                foreach (string t in names)
                    ScratchFile.Exec(w, $"CREATE INDEX IF NOT EXISTS {SqlText.Name(t + "_Key")} ON {SqlText.Name(t)}(FormKey{(file.IsSet ? ", Plugin" : "")})");
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
            string cols = job.Columns.Count == 0 ? "from the header index alone" : $"column{(job.Columns.Count == 1 ? "" : "s")} {string.Join(", ", job.Columns)}" + HowRead(plan, job.Columns.Count);
            _answer.Cost.Read.Add($"{_e.LabelOf(job.Sig)} ({rows:N0}, {cols}) from {Where(job)} in {Took(clock.Elapsed.TotalSeconds)}");
            if (skipped > 0) Note($"{_e.LabelOf(job.Sig)}: {skipped:N0} record(s) couldn't be read and aren't counted.");
        }

        void FillForms(SqliteConnection w, ScratchFile file, ReadJob job)
        {
            var clock = Stopwatch.StartNew();
            long rows = 0;
            using var tx = w.BeginTransaction();
            using (var ins = new BulkInsert(w, "INSERT INTO FormRows(FormKey, Sig, EditorID, Plugin, Parent, IsOverride, Pos, LoadOrder, IsWinner, Size, DataSize, Compressed, Flags) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)"))
            {
                foreach (var (plugin, _) in job.Loads)
                {
                    var member = file.Members.First(m => m.Plugin.Equals(plugin.Name, StringComparison.OrdinalIgnoreCase));
                    foreach (var row in _e._reader.ReadIndex(plugin, file.IsSet, _req.Cancel))
                    {
                        ins.Text(1, row.FormKey); ins.Text(2, row.Sig); ins.Text(3, row.EditorId);
                        if (file.IsSet) ins.Text(4, plugin.Name); else ins.Null(4);
                        ins.Text(5, row.Parent); ins.Int(6, row.IsOverride ? 1 : 0); ins.Int(7, row.Pos);
                        if (file.IsSet) { ins.Int(8, member.LoadOrder); ins.Int(9, row.IsWinner ? 1 : 0); } else { ins.Null(8); ins.Null(9); }
                        ins.Int(10, row.Size); ins.Int(11, row.DataSize); ins.Int(12, row.Compressed ? 1 : 0); ins.Int(13, row.Flags);
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

        /// <summary>
        /// The type's tables of named columns that together hold <paramref name="columns"/> (GAPS S146): the one with the
        /// most of them first (it gives the rows), then each that adds a column; null when they don't hold them all. With
        /// no column asked, the first table (its rows).
        /// </summary>
        List<(string Table, HashSet<string> Columns)>? ProjectionTables(ScratchFile f, string type, IEnumerable<string> columns)
        {
            if (!_projectionTables.TryGetValue((f, type), out var all) || all.Count == 0) return null;
            var left = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
            var chosen = new List<(string Table, HashSet<string> Columns)>();
            while (left.Count > 0)
            {
                var best = all.Where(t => !chosen.Contains(t)).OrderByDescending(t => t.Columns.Count(left.Contains)).FirstOrDefault();
                if (best.Table == null || !best.Columns.Any(left.Contains)) return null;
                chosen.Add(best);
                left.ExceptWith(best.Columns);
            }
            if (chosen.Count == 0) chosen.Add(all[0]);
            return chosen;
        }

        /// <summary>A typed view's named columns from several of its tables: the first gives the rows, each other one its
        /// columns, joined by FormKey (and plugin, in a set) through the index FillProjection makes when a type gets a
        /// second table (GAPS S146). A record a later table couldn't read gives NULL there.</summary>
        string ProjectionSelect(ScratchFile f, ViewSpec view, List<(string Table, HashSet<string> Columns)> tables, HashSet<string> only)
        {
            string plugin = PluginExpr(f, "x");
            string versions = !_versions ? "" : f.IsSet ? ", x.LoadOrder AS LoadOrder, x.IsWinner AS IsWinner"
                : $", {(f.Members[0].LoadOrder is int lo ? lo.ToString(CultureInfo.InvariantCulture) : "NULL")} AS LoadOrder, sfs_iswinner(x.FormKey, {plugin}) AS IsWinner";
            var json = new ViewBuilder.JsonJoins();
            var cols = new List<string>();
            foreach (var c in view.Columns.Where(c => only.Contains(c.Name)))
            {
                int i = tables.FindIndex(t => t.Columns.Contains(c.Name));
                string src = $"{(i == 0 ? "x" : $"x{i + 1}")}.{SqlText.Name(c.Name)}";
                cols.Add(", " + (ViewBuilder.IsJson(c) ? json.Column($"json({src})", c.Name) : $"{src} AS {SqlText.Name(c.Name)}"));
            }
            var from = new System.Text.StringBuilder($"{f.Alias}.{SqlText.Name(tables[0].Table)} x");
            for (int i = 1; i < tables.Count; i++)
                from.Append($" LEFT JOIN {f.Alias}.{SqlText.Name(tables[i].Table)} x{i + 1} ON x{i + 1}.FormKey = x.FormKey" + (f.IsSet ? $" AND x{i + 1}.Plugin = x.Plugin" : ""));
            return $"SELECT x.FormKey AS FormKey, x.EditorID AS EditorID, {plugin} AS Plugin, x.IsOverride AS IsOverride{versions}{string.Concat(cols)} FROM {from}{json.From}"
                + (f.IsSet && _winners ? " WHERE x.IsWinner = 1" : "");
        }

        // -------------------------------------------------------------- The views
        ViewBuilder.Member MemberOf(ScratchFile f) => f.IsSet
            ? new ViewBuilder.Member(f.Alias, null, _winners ? "r.IsWinner = 1" : null, true, null)
            : new ViewBuilder.Member(f.Alias, f.Members[0].Plugin, null, false, f.Members[0].LoadOrder);

        string PluginExpr(ScratchFile f, string alias) => f.IsSet ? $"{alias}.Plugin" : SqlText.Literal(f.Members[0].Plugin) + " COLLATE NOCASE";

        /// <summary>
        /// True when the statement groups rows (GROUP BY) or has a window function (OVER, WINDOW): then SQLite keeps,
        /// for each row of a join, the columns a grouped value is made from in a sorter until the group is done.
        /// </summary>
        static bool GroupsRows(List<Tok> toks)
        {
            for (int i = 0; i < toks.Count; i++)
                if (toks[i].Is("GROUP") && i + 1 < toks.Count && toks[i + 1].Is("BY") || toks[i].Is("OVER") || toks[i].Is("WINDOW")) return true;
            return false;
        }

        /// <summary>
        /// True when the statement reads rows of more than one source: a JOIN, a comma between the items of a FROM, or
        /// more than one FROM (a subquery, which may run for each row of the statement around it).
        /// </summary>
        static bool JoinsRows(List<Tok> toks)
        {
            int froms = 0;
            for (int i = 0; i < toks.Count; i++)
            {
                if (toks[i].Is("JOIN")) return true;
                if (!toks[i].Is("FROM")) continue;
                if (++froms > 1) return true;
                for (int k = i + 1; k < toks.Count && toks[k].Depth >= toks[i].Depth; k++)
                {
                    if (toks[k].Depth != toks[i].Depth) continue;
                    if (toks[k].IsPunct(",")) return true;
                    if (FromEnds.Any(toks[k].Is)) break;
                }
            }
            return false;
        }

        static readonly string[] FromEnds = { "WHERE", "GROUP", "HAVING", "WINDOW", "ORDER", "LIMIT", "UNION", "INTERSECT", "EXCEPT" };

        /// <summary>The second part of a view of two parts: as many NULLs as <paramref name="select"/> has columns, and no
        /// rows. A part without a FROM is one SQLite never folds into the statement around it (its query flattener's
        /// rule 17c), whatever that statement is; the view's columns keep the first part's names, types and collations.
        /// A WHERE on the view's own columns is still pushed into the first part.</summary>
        static string EmptyPart(string select)
        {
            var t = SqlText.Tokenize(select);
            int columns = 1;
            for (int k = 1; k < t.Count && !(t[k].Depth == 0 && t[k].Is("FROM")); k++)
                if (t[k].Depth == 0 && t[k].IsPunct(",")) columns++;
            return "SELECT " + string.Join(", ", Enumerable.Repeat("NULL", columns)) + " WHERE 0";
        }

        /// <summary>
        /// The views the statement names, made for it. A typed view over records read whole is given as two parts joined
        /// by UNION ALL, the second without rows (<see cref="EmptyPart"/>), so that SQLite works its columns out once per
        /// record instead of folding it into the statement, when
        /// <list type="bullet">
        /// <item><paramref name="groups"/>: the statement groups rows (<see cref="GroupsRows"/>; GAPS S104). A typed
        /// view's column is an expression over the record's JSON (Location is r.Json ->> '$.Location'); SQLite folds a
        /// plain view into a grouped statement, and then its sorter keeps the whole JSON of the record for every row of
        /// the join and works the column out after sorting: Cell joined to Cell_Traversals over SFBGS00D.esm and 8 small
        /// plugins, grouped, with c.Location, carried each cell's JSON (up to 1.5 MB) for each of its traversals and
        /// passed 16 GB in 5 s. In two parts the sorter keeps only the view's columns (0.2 GB, 0.7 s).</item>
        /// <item><paramref name="joins"/>: the statement reads more than one source (<see cref="JoinsRows"/>) and names
        /// the view's Json or a column that is a part of the JSON (GAPS S126). Folded into a join, the view makes the
        /// record's JSON text from its stored JSONB again for each row of the join: the cell NAViewport's JSON for each
        /// of its 29,070 traversals took 270 s. In two parts each record's JSON is made once; where the view goes in the
        /// join is then settled from SQLite's plan (Session.JsonJoins.cs). Its typed columns are cheap to work out again
        /// (one lookup in the JSONB), so a join that names only those keeps the folded view.</item>
        /// </list>
        /// A WHERE on the view's own columns still narrows the view before its columns are worked out.
        /// </summary>
        void CreateViews(Probed probed, bool groups = false, bool joins = false)
        {
            _jsonSources.Clear();
            _jsonJoinViews.Clear();
            _groupedViews.Clear();
            foreach (var (view, used) in probed.Views)
            {
                bool withJson = used.Contains("Json");
                var only = new HashSet<string>(used, StringComparer.OrdinalIgnoreCase);
                var selects = new List<string>();
                bool whole = false;
                foreach (var f in _files)
                {
                    string sig = _e._schema.SignatureOf(view.Types[0])!;
                    if (view.IsList && !_done[f].ContainsKey(sig) && _projections.TryGetValue((f, "list:" + view.Name), out var lp)
                        && only.Where(c => !ListFixed.Contains(c)).All(lp.Columns.Contains))
                    {
                        // Only the named columns of this list, of the records that hold it, were read (ListReads.cs); they
                        // hold every row of the list, so they answer it even when the type's records are read by key too
                        // A table read through the records' JSON holds each entry too: the other columns come from it
                        var wanted = view.Columns.Select(c => c.Name).Append("Key").Append("Json")
                            .Where(c => only.Contains(c) && (c != "Key" || ViewBuilder.IsListKey(view, c)) && (c != "Json" || !view.ScalarEntries));
                        var (cols, from) = ViewBuilder.ListEntrySql(view, wanted, TableColumns(f, lp.Table), _language);
                        var idx = Enumerable.Range(0, view.Lists.Count).Select(i => i == 0 ? ", x.Idx AS Idx" : $", x.Idx{i + 1} AS Idx{i + 1}");
                        selects.Add($"SELECT x.FormKey AS FormKey, x.EditorID AS EditorID, {PluginExpr(f, "x")} AS Plugin{string.Concat(idx)}{string.Concat(cols.Select(c => ", " + c))} FROM {f.Alias}.{SqlText.Name(lp.Table)} x{from}"
                            + (f.IsSet && _winners ? " WHERE x.IsWinner = 1" : ""));
                        continue;
                    }
                    if (!view.IsList && _keyedProj.TryGetValue((f, view.Name), out var kp))
                    {
                        // Its named columns of the FormKeys a list it is joined to holds, read for this statement (ListReads.cs)
                        selects.Add(ProjectionSelect(f, view, $"temp.{SqlText.Name(kp)}", only));
                        continue;
                    }
                    if (_keyed.TryGetValue(f, out var keyed) && keyed.ViewSigs.Contains(sig))
                    {
                        // Read by FormKey for this statement: the view reads those rows
                        var member = MemberOf(f) with { Table = "temp." + SqlText.Name(keyed.Rec) };
                        selects.Add(ViewBuilder.SelectSql(view, member, _language, _versions, only, withJson));
                        if (withJson) NoteJsonSource(view, member);
                        whole = true;
                        continue;
                    }
                    if (!view.IsList && !_done[f].ContainsKey(sig) && ProjectionTables(f, view.Types[0], only.Where(view.ByName.ContainsKey)) is { } tables)
                    {
                        // Only the named columns of this type were read: the view gives those (a set's with its plugin,
                        // place and winner mark; the winners scope only the versions that win), from the tables that hold them
                        selects.Add(tables.Count == 1 ? ProjectionSelect(f, view, $"{f.Alias}.{SqlText.Name(tables[0].Table)}", only) : ProjectionSelect(f, view, tables, only));
                        continue;
                    }
                    selects.Add(ViewBuilder.SelectSql(view, MemberOf(f), _language, _versions, only, withJson));
                    if (withJson) NoteJsonSource(view, MemberOf(f));
                    whole = true;
                }
                string one = string.Join(" UNION ALL ", selects);
                if (whole && !view.IsList && groups && selects.Count == 1)
                {
                    selects.Add(EmptyPart(selects[0]));
                    _groupedViews[view.Name] = one;
                }
                else if (whole && !view.IsList && joins && (withJson || view.Columns.Any(c => c.Kind == "Json" && only.Contains(c.Name))))
                {
                    // Where it goes in the join is settled once the plan is known (PlaceJsonViews)
                    selects.Add(EmptyPart(selects[0]));
                    _jsonJoinViews[view.Name] = one;
                }
                ScratchFile.Exec(_con, $"CREATE TEMP VIEW {SqlText.Name(view.Name)} AS {string.Join(" UNION ALL ", selects)}");
                _ownViews.Add(view.Name);
            }
            foreach (string special in probed.Special)
            {
                // Every plugin's header (PluginRows.cs): one table for the whole Data folder, whatever the scope
                if (special == "Plugins")
                {
                    MakePluginRows();
                    // Masters stays JSON inside SQLite's JSON functions through the view (GAPS S80), as Conditions' Flags do
                    var json = new ViewBuilder.JsonJoins();
                    ScratchFile.Exec(_con, $"CREATE TEMP VIEW Plugins AS SELECT {string.Join(", ", PluginColumns.Select(c => c == "Masters" ? json.Column("json(r.Masters)", c) : $"r.{c} AS {c}"))} FROM temp.sfs_plugins r{json.From}");
                    _ownViews.Add(special);
                    continue;
                }
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
                                + (_versions ? ", r.LoadOrder AS LoadOrder, r.IsWinner AS IsWinner" : "") + $", sfs_j1.value AS Json FROM {f.Alias}.Rec r, json_each(json_array(r.Json)) sfs_j1{where}");
                            if (byBytes != null) selects[^1] += " UNION ALL " + selects[^1].Replace($" FROM {f.Alias}.Rec r", $" FROM temp.{SqlText.Name(byBytes.Rec)} r");
                            NoteJsonSource($"{f.Alias}.Rec", plugin, where);
                            if (byBytes != null) NoteJsonSource($"temp.{SqlText.Name(byBytes.Rec)}", plugin, where);
                            break;
                        case "Forms":
                            selects.Add($"SELECT r.FormKey AS FormKey, r.Sig AS Sig, (SELECT t.Type FROM temp.sfs_sigtype t WHERE t.Sig = r.Sig) AS Type, r.EditorID AS EditorID, {plugin} AS Plugin, r.Parent AS Parent, "
                                + (f.IsSet ? "r.IsWinner" : $"sfs_iswinner(r.FormKey, {plugin})") + " AS IsWinner, r.IsOverride AS IsOverride, r.Pos AS Pos, "
                                + (f.IsSet ? "r.LoadOrder" : f.Members[0].LoadOrder is int lo ? lo.ToString(CultureInfo.InvariantCulture) : "NULL") + $" AS LoadOrder, r.Size AS Size, r.DataSize AS DataSize, r.Compressed AS Compressed, r.Flags AS Flags, sfs_flagnames(r.Sig, r.Flags) AS FlagNames FROM {f.Alias}.FormRows r{where}");
                            break;
                        case "Links":
                            selects.Add($"SELECT r.FromKey AS FromKey, r.FromType AS FromType, r.Field AS Field, r.Idx AS Idx, r.ToKey AS ToKey, r.Path AS Path, {plugin} AS Plugin FROM {f.Alias}.LinkRows r{where}");
                            if (byBytes != null) selects[^1] += " UNION ALL " + selects[^1].Replace($" FROM {f.Alias}.LinkRows r", $" FROM temp.{SqlText.Name(byBytes.Links)} r");
                            break;
                        case "Conditions":
                        {
                            // The JSON columns (Flags, Json) stay JSON through the view (GAPS S80)
                            var json = new ViewBuilder.JsonJoins();
                            selects.Add("SELECT " + string.Join(", ", ConditionRows.Columns.Select(c => c == "Plugin" ? $"{plugin} AS Plugin" : ConditionRows.Computed.TryGetValue(c, out var made) ? $"{made} AS {c}" : ConditionRows.JsonColumns.Contains(c) ? json.Column($"json(r.{c})", c) : $"r.{c} AS {c}"))
                                + (_versions ? ", r.LoadOrder AS LoadOrder, r.IsWinner AS IsWinner" : "") + $" FROM {f.Alias}.CondRows r{json.From}{where}");
                        }
                            break;
                    }
                }
                // Records' Json in a join: made once per record, as a typed view's (GAPS S126)
                if (special == "Records" && joins && selects.Count > 0 && probed.SpecialColumns.TryGetValue(special, out var rc) && rc.Contains("Json"))
                {
                    _jsonJoinViews[special] = string.Join(" UNION ALL ", selects);
                    selects.Add(EmptyPart(selects[0]));
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
            if (probed.Special.Contains("Conditions") && !_narrowed)
            {
                var holding = _e.ConditionSigs;
                var inScope = ScopeSignatures().Keys.Where(holding.Contains).ToList();
                var read = inScope.Where(s => _files.All(f => _done[f].ContainsKey(s))).Select(_e.LabelOf).OrderBy(t => t, StringComparer.Ordinal).ToList();
                var unread = inScope.Where(s => !_files.All(f => _done[f].ContainsKey(s))).Select(_e.LabelOf).OrderBy(t => t, StringComparer.Ordinal).ToList();
                if (unread.Count > 0)
                    Note($"Conditions holds the conditions of the record types read so far in this scope: {read.Count:N0} of the {inScope.Count:N0} types here that can hold one ({(read.Count == 0 ? "none yet" : string.Join(", ", read.Take(12)) + (read.Count > 12 ? ", …" : ""))}). "
                        + $"Not read: {string.Join(", ", unread.Take(12))}{(unread.Count > 12 ? ", …" : "")}. Narrow it (WHERE Type = '…', FormKey = '…', EditorID = '…') and that type is read first, or call again with allow_heavy=true to read every type that can hold a condition. No saved index would make that faster: conditions are read out of each record.");
            }
        }
    }
}
