// A list view of a record type too big (or too slow) to read whole (GAPS S45). Joining Starfield.esm's placed
// references' traversals (PlacedObject_Traversals) to its navmesh info map's (NavigationMeshInfoMap_MapInfos_Traversals)
// meant reading all 3.29 million placed objects, and the 51 MB navmesh info map, whole as JSON: refused as heavy, and
// at the size of every plugin, many GB.
//
// A list's entries sit in the list's own subrecords (Traversals in XTV2, a component's traversals in STRD), so a record
// that holds none of them has no row in the list view. When a statement reads only list views of such a type (not its
// typed view, or the typed view joined to them by FormKey), and reading the type whole would be heavy or take a
// second or more:
//   1. the type's records are stepped through in the plugin's bytes (inflated when compressed) for the subrecords the
//      list view's columns sit in: 6,418 of Starfield.esm's 6,959,281... placed objects hold XTV2;
//   2. only those records are read, and only the list view's named columns, straight from Mutagen's objects (no JSON:
//      DirectList), into a scratch table of their own, which later queries use as they use a typed view's named columns
//      (Proj, type "list:<view>"). A column that has to come from the JSON (flags, translated text, a kind of object,
//      the entry's Json) is taken out of each record's JSON on the reading thread, with the view's own expressions
//      (ViewBuilder.ListProjectSql), and the record's JSON let go at once (GAPS S58): into the same kind of table.
//   3. When the statement names the type's typed view too, and joins it to the list by FormKey so that no row of it
//      without the list can reach the answer (JoinedByFormKey), the typed view is read only for those records'
//      FormKeys, its named columns into a temporary table of the statement (RunKeyedProjections).
// Nothing is left out: every row of the list view is in a record that holds one of those subrecords. The answer says
// how many records were stepped through, how many hold the subrecords, and that only those were read.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using SFSearch.Index;

namespace SFSearch.Query;

/// <summary>Which records of a plugin hold one of some subrecords, from the file's bytes, on the shared record pipeline
/// (SFSearch.Index\RecordPipeline.cs).</summary>
static class SubrecordScan
{
    public sealed class Result
    {
        public List<int> Hits = new();
        public long Records, Bytes;
        public int NotInflated;
        public double Ms;
        /// <summary>The largest of the records that hold them, inflated (exact: each one's own size).</summary>
        public long LargestPlain;
    }

    public static Result Find(IndexedPlugin plugin, string signature, IReadOnlyCollection<string> subrecords, int threads, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        var r = new Result();
        var seg = plugin.Segment!;
        var mask = new bool[seg.Signatures.Count];
        bool any = false;
        for (int i = 0; i < mask.Length; i++) if (seg.Signatures[i].Signature.Equals(signature, StringComparison.OrdinalIgnoreCase)) { mask[i] = true; any = seg.Signatures[i].Count > 0; }
        if (!any) return r;
        // No subrecord asked: a list of the record header's own (its flags as a list), which every record holds
        if (subrecords.Count == 0)
        {
            foreach (int e in seg.EntriesOf(signature)) { r.Hits.Add(e); r.Records++; r.LargestPlain = Math.Max(r.LargestPlain, seg[e].Size); }
            r.Ms = clock.Elapsed.TotalMilliseconds;
            return r;
        }
        var want = subrecords.Select(s => BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(s))).ToArray();
        var stats = new PipelineStats();
        foreach (var (entry, plain, problem) in RecordPipeline.Read<(int, long, bool)>(new[] { new ReadSet { Plugin = plugin, Signatures = mask } }, (in PipelineRecord rec, List<(int, long, bool)> output) =>
        {
            // Can't be looked into: read it, so nothing is left out
            if (rec.Problem != PipelineProblem.None) output.Add((rec.EntryIndex, rec.Header.Size, true));
            else if (Holds(rec.Data, want)) output.Add((rec.EntryIndex, rec.Data.Length, false));
        }, new PipelineOptions { Threads = Math.Max(1, threads), Cancel = cancel, Stats = stats }))
        {
            r.Hits.Add(entry);
            if (problem) r.NotInflated++;
            r.LargestPlain = Math.Max(r.LargestPlain, plain);
        }
        r.Records = stats.Records[0]; r.Bytes = stats.StoredBytes[0];
        r.Ms = clock.Elapsed.TotalMilliseconds;
        return r;
    }

    /// <summary>True when one of the record's subrecords is one of those asked for. An XXXX gives the size of the next one.</summary>
    static bool Holds(ReadOnlySpan<byte> body, uint[] want)
    {
        int p = 0; uint? big = null;
        while (p + 6 <= body.Length)
        {
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(body[p..]);
            int n = BinaryPrimitives.ReadUInt16LittleEndian(body[(p + 4)..]);
            p += 6;
            if (sig == 0x58585858 && n == 4 && p + 4 <= body.Length) { big = BinaryPrimitives.ReadUInt32LittleEndian(body[p..]); p += 4; continue; }
            if (Array.IndexOf(want, sig) >= 0) return true;
            long len = big ?? (uint)n; big = null;
            p = (int)Math.Min(body.Length, p + len);
        }
        return false;
    }
}

public sealed partial class QueryEngine
{
    sealed partial class Session
    {
        static readonly HashSet<string> ListFixed = new(StringComparer.OrdinalIgnoreCase) { "", "FormKey", "EditorID", "Plugin", "Idx", "Idx2", "Idx3", "rowid" };

        /// <summary>For the statement being run: the roots whose typed view it joins to one of their list views by
        /// FormKey (<see cref="JoinedByFormKey"/>), so only the FormKeys the list holds can give it rows.</summary>
        HashSet<string> _joinedRoots = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>For the statement being run: a typed view's named columns of some records, read for it alone into
        /// a temporary table (the typed view joined to a narrowed list), and those tables once read.</summary>
        readonly List<KeyedProjJob> _keyedProjJobs = new();

        /// <summary>A typed view's named columns of some records, read for one statement into a temporary table: the
        /// FormKeys a list it is joined to holds (Refs), or every record of the type with a filter (Session.Filter.cs).</summary>
        sealed class KeyedProjJob
        {
            public required ScratchFile File;
            public required ViewSpec View;
            public required List<string> Columns;
            public required List<(IndexedPlugin Plugin, int[] Entries)> Sets;
            public required string Sig;
            public RowFilter? Filter;
        }
        readonly Dictionary<(ScratchFile, string), string> _keyedProj = new();

        /// <summary>The subrecords a list view's entries sit in, from its columns; null when one of them isn't known.</summary>
        HashSet<string>? SubrecordsOf(ViewSpec v)
        {
            var model = _e._schema.Model;
            // A list of components: every entry starts with its BFCB (Mutagen's AComponent reader takes BFCB to BFCE), so
            // a record that holds BFCB holds the list's entries, whatever fields they have (a field derived from no
            // subrecord, an entry with no field but its kind)
            bool components = v.Columns.Any(c => c.Row is { } r && r.Stack.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(o => model.EndMarkerOf(o) == "BFCE"));
            // Only BFCB: a component's fields sit between its BFCB and BFCE, and their signatures (DATA, NAM1, …) are the
            // record's own fields' too, which every placed object holds
            if (components) return new HashSet<string>(StringComparer.Ordinal) { "BFCB" };
            // A flags list of the record header (MajorFlags): every record holds it; read from the bytes, no record is
            // opened through Mutagen (RawLists.cs), so every record is read that way (no subrecord to look for)
            if (v.Lists.Count == 1 && v.Lists[0].Kind == "Flags" && v.Columns.All(c => c.Subrecord == null)
                && RawList.For(v, v.Columns.Select(c => c.Name).ToList(), _e._schema, _e.Views.Catalog(v.Root).Typed) != null)
                return new HashSet<string>(StringComparer.Ordinal);
            var subs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in v.Columns)
            {
                if (c.Kind == "ObjectType") continue;
                if (c.Subrecord is not { Length: 4 } s)
                {
                    // A field with no subrecord of its own (a perk effect's Modification, from its DATA's function): a list
                    // the value walk reads names every subrecord an entry is read from (RawWalkLists)
                    return RawList.For(v, Array.Empty<string>(), _e._schema, _e.Views.Catalog(v.Root).Typed) is { Walks: true, EntrySubrecords: { Count: > 0 } es }
                        ? es.ToHashSet(StringComparer.Ordinal) : null;
                }
                subs.Add(s);
            }
            return subs.Count == 0 ? null : subs;
        }

        /// <summary>
        /// Every view the statement names of the type is a list view whose named columns come from the records' own bytes
        /// (RawList.For): its records are found by their subrecords and read that way whatever the type's size, as stepping
        /// through the bytes costs less than Mutagen's read of every record of the type (a weapon's object templates, an
        /// NPC's: GAPS S164).
        /// </summary>
        bool ListsFromBytes(IGrouping<string, KeyValuePair<ViewSpec, HashSet<string>>> group, ViewSpec typed)
        {
            if (!_e._options.DirectColumns || group.Any(kv => !kv.Key.IsList)) return false;
            foreach (var (v, used) in group)
            {
                var cols = used.Where(c => !ListFixed.Contains(c)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (ListColumns(v, cols) is not { } ordered || DirectList.For(v, ordered, _language) == null || RawList.For(v, ordered, _e._schema, typed) == null) return false;
            }
            return true;
        }

        /// <summary>A list view's columns in its own order: its fields, then its Key (a dictionary's) and the entry's Json.
        /// Null when one of them isn't the view's.</summary>
        static List<string>? ListColumns(ViewSpec v, HashSet<string> cols)
        {
            var ordered = v.Columns.Select(c => c.Name).Where(cols.Contains).ToList();
            if (cols.Any(c => ViewBuilder.IsListKey(v, c))) ordered.Add("Key");
            if (cols.Contains("Json") && !v.ScalarEntries) ordered.Add("Json");
            return ordered.Count == cols.Count ? ordered : null;
        }

        /// <summary>
        /// The reads of a statement's list views of a type too big or slow to read whole, narrowed to the records that
        /// hold their subrecords (the top of this file); with the type's typed view when the statement joins it to one of
        /// them by FormKey. False when the narrowing doesn't apply: the type is then read as before.
        /// </summary>
        bool PlanListReads(IGrouping<string, KeyValuePair<ViewSpec, HashSet<string>>> group, ViewSpec typed, ScratchFile file, string sig,
            List<(IndexedPlugin Plugin, SigLoad Load)> loads, Estimate full, Probed probed, List<ReadJob> jobs)
        {
            if (typed.Types.Count != 1 || typed.IsUnion || probed.Special.Count > 0) return false;
            var typedUse = group.Where(kv => !kv.Key.IsList).ToList();
            if (typedUse.Count > 0 && !_joinedRoots.Contains(group.Key)) return false;
            if (!(Heavy(full) || full.Seconds >= KeyedSeconds || loads.Sum(l => l.Load.Count) > _e._options.HeavyRecords || ListsFromBytes(group, typed))) return false;
            var lists = group.Where(kv => kv.Key.IsList).Select(kv => (View: kv.Key, Used: kv.Value)).ToList();
            if (lists.Count == 0) return false;
            var subs = new HashSet<string>(StringComparer.Ordinal);
            bool headerOnly = false;
            foreach (var (v, _) in lists)
            {
                if (SubrecordsOf(v) is not { } s) return false;
                headerOnly |= s.Count == 0;
                subs.UnionWith(s);
            }
            // A header's list beside a list of subrecords: the records the subrecords narrow to aren't every record
            if (headerOnly && subs.Count > 0) return false;
            // Each list's named columns straight from the records, or (a column only the JSON gives) taken out of each
            // record's JSON on the reading thread, the JSON let go at once
            var reads = new List<(ViewSpec View, List<string> Columns, DirectList? Direct, bool Bytes)>();
            foreach (var (v, used) in lists)
            {
                var cols = used.Where(c => !ListFixed.Contains(c)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (_projections.TryGetValue((file, "list:" + v.Name), out var have))
                {
                    if (cols.IsSubsetOf(have.Columns)) { _usedLabels.Add($"{v.Name} (named columns)"); continue; }
                    cols.UnionWith(have.Columns);
                    cols.Remove(ViewBuilder.EntryColumn);
                }
                if (ListColumns(v, cols) is not { } ordered) return false;
                var dl = _e._options.DirectColumns ? DirectList.For(v, ordered, _language) : null;
                reads.Add((v, ordered, dl, dl != null && RawList.For(v, ordered, _e._schema, typed) != null));
            }
            // The typed view joined to a list by FormKey: its named columns, of the copies of the FormKeys that hold the list
            var typedCols = typedUse.SelectMany(kv => kv.Value).Where(c => !NotFields.Contains(c)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool typedWhole = typedUse.Any(kv => kv.Value.Contains("Json")) || typedCols.Any(c => !typed.ByName.ContainsKey(c));
            if (reads.Count == 0 && typedUse.Count == 0) return true;   // every list's columns are in the scratch tables already
            // The records that hold the subrecords
            var label = _e.LabelOf(sig);
            long stepped = 0, held = 0; int notInflated = 0; double ms = 0;
            var refs = new List<RecordRef>();
            var byPlugin = new List<(IndexedPlugin Plugin, int[] Entries, SigLoad Load)>();
            foreach (var (p, _) in loads)
            {
                var r = SubrecordScan.Find(p, sig, subs, _e._options.Threads, _req.Cancel);
                stepped += r.Records; ms += r.Ms; notInflated += r.NotInflated;
                if (r.Hits.Count == 0) continue;
                long b = 0, comp = 0, largest = 0;
                foreach (int e in r.Hits) { var en = p.Segment![e]; b += en.Size; if (en.IsCompressed) comp++; largest = Math.Max(largest, en.Size); }
                held += r.Hits.Count;
                var load = CostModel.Load(p, sig);
                // The held records' share of the type's bytes as Mutagen reads them (compressed ones inflated)
                double inflate = load.Bytes > 0 ? (double)load.Inflated / load.Bytes : 1;
                // The largest held record as it is read (inflated), exact: the scan inflated each one
                byPlugin.Add((p, r.Hits.ToArray(), new SigLoad(r.Hits.Count, b, comp, Math.Max(largest, r.LargestPlain), (long)(b * inflate)) { Signature = sig }));
                refs.AddRange(r.Hits.Select(e => new RecordRef(p.Id, e)));
            }
            string where = loads.Count == 1 ? loads[0].Plugin.Name : $"{loads.Count:N0} plugins";
            var names = lists.Select(l => l.View.Name).ToList();
            bool fromJson = reads.Any(x => x.Direct == null), fromBytes = reads.Count > 0 && reads.All(x => x.Bytes);
            string how = (subs.Count == 0 ? $"{string.Join(", ", names)}: every one of the {stepped:N0} {label} records of {where} holds it (its record header's flags), "
                : $"{string.Join(", ", names)}: {held:N0} of the {stepped:N0} {label} records of {where} hold its subrecord{(subs.Count == 1 ? "" : "s")} ({string.Join(", ", subs.OrderBy(s => s, StringComparer.Ordinal))}), "
                + $"found by stepping through their bytes in {Took(ms / 1000)}; only those were read, ")
                + (reads.Count == 0 ? "" : fromJson ? "the list's rows taken out of each record's JSON (a column asked for comes from the JSON), one record at a time" : fromBytes ? "the named columns from the records' own bytes" : "the named columns straight from the records")
                + (typedUse.Count == 0 ? "" : $"{(reads.Count == 0 ? "" : "; ")}{typed.Name} (joined to it by FormKey) for those FormKeys alone{(typedWhole ? ", whole" : "")}")
                + (notInflated > 0 ? $" ({notInflated:N0} that couldn't be looked into were read too)" : "") + ".";
            Note(how);
            foreach (var (v, cols, dl, bytes) in reads)
            {
                // Through the JSON: the plain columns asked for as they are, and each entry, which gives the rest
                var plain = dl != null ? cols : cols.Where(c => v.ByName.TryGetValue(c, out var spec) && DirectColumn.ForEntry(spec, _language) != null).ToList();
                var table = dl != null ? cols : plain.Append(ViewBuilder.EntryColumn).ToList();
                if (held == 0) { EmptyListTable(file, v, table); continue; }
                var est = byPlugin.Aggregate(Estimate.None, (a, x) => a + (bytes ? CostModel.Raw(x.Load, x.Load.Count) : ListEstimate(x.Load, table.Count, dl == null)));
                jobs.Add(new ReadJob
                {
                    File = file, Route = Route.ListProjection, Sig = sig, View = v, Columns = table, List = dl, Estimate = est,
                    ListSql = dl == null ? ViewBuilder.ListProjectSql(v, plain, _language) : null,
                    Loads = byPlugin.Select(x => (x.Plugin, x.Load)).ToList(), Entries = byPlugin.ToDictionary(x => x.Plugin, x => x.Entries),
                });
            }
            if (typedUse.Count > 0)
            {
                var keys = refs.Select(r => Index.Describe(r).FormKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var copies = KeyedRefs(file, sig, keys);
                var typedOrdered = typed.Columns.Select(c => c.Name).Where(typedCols.Contains).ToList();
                bool typedJson = !typedWhole && ProjectionPlan(typed, typedOrdered, false, null).ProjectSql != null;
                var est = byPlugin.Aggregate(Estimate.None, (a, x) => a + (typedWhole ? CostModel.Full(x.Load) : CostModel.Projection(x.Load, typedCols.Count, typedJson)));
                if (typedWhole) _keyedJobs.Add((file, copies, false, sig, ""));
                else _keyedProjJobs.Add(new KeyedProjJob
                {
                    File = file, View = typed, Columns = typed.Columns.Select(c => c.Name).Where(typedCols.Contains).ToList(), Sig = sig,
                    Sets = copies.GroupBy(r => r.Plugin).Select(g => (Index.Plugins[g.Key], g.Select(r => r.Entry).ToArray())).ToList(),
                });
                if (held > 0)
                    jobs.Add(new ReadJob { File = file, Route = Route.KeyedEstimate, Sig = sig, View = typed, Columns = typedWhole ? new List<string>() : typedCols.ToList(), Whole = typedWhole,
                        Loads = byPlugin.Select(x => (x.Plugin, x.Load)).ToList(), Estimate = est });
            }
            return true;
        }

        /// <summary>Memory a list read through the records' JSON takes for each MB of its largest record (inflated): one
        /// record's JSON on each reading thread and the rows of the records waiting to be inserted. Measured 2026-11-15:
        /// Starfield.esm's Cell_Traversals (largest cell 3.5 MB) 3.1 GB, SFBGS00D.esm's (1.5 MB) 1.1 GB.</summary>
        const double JsonListMemoryPerLargestMB = 750;

        /// <summary>What reading some records' list columns costs: the records opened, their bytes parsed (straight from
        /// Mutagen: the general per-MB rate's half; through each record's JSON: as a whole read), rows inserted. A round
        /// figure. Through the JSON a reading thread holds one record's JSON at a time.</summary>
        static Estimate ListEstimate(SigLoad load, int columns, bool json)
        {
            double mb = load.Inflated / 1048576.0;
            double seconds = json ? CostModel.Full(load).Seconds : load.Count * CostModel.SecondsPerProjectedRecord + mb * CostModel.SecondsPerFileMB / 2;
            long memory = CostModel.BaseMemoryMB + (long)(load.LargestRecord / 1048576.0 * (json ? JsonListMemoryPerLargestMB : 20));
            return new Estimate(seconds, memory, (long)(mb * (1 + columns / 4.0)));
        }

        /// <summary>A list projection with no rows: none of the type's records holds the list's subrecords.</summary>
        void EmptyListTable(ScratchFile file, ViewSpec v, List<string> cols)
        {
            using var hold = new ScratchFile.WriterLock(file.Path, _req.Cancel);
            using var w = file.OpenWriter();
            using var tx = w.BeginTransaction();
            string table = CreateListTable(w, v, cols);
            Register(w, v, table, cols, 0);
            tx.Commit();
            RefreshDone(file);
        }

        string CreateListTable(SqliteConnection w, ViewSpec v, List<string> cols)
        {
            long n;
            using (var count = w.CreateCommand()) { count.CommandText = "SELECT count(*) FROM Proj WHERE Type = $t"; count.Parameters.AddWithValue("$t", "list:" + v.Name); n = (long)count.ExecuteScalar()!; }
            string table = $"XL_{v.Name}_{n + 1}";
            var idx = Enumerable.Range(0, v.Lists.Count).Select(i => (i == 0 ? "Idx" : "Idx" + (i + 1)) + " INT");
            var defs = cols.Select(c => SqlText.Name(c) + (v.ByName.TryGetValue(c, out var spec) && spec.IsLink ? " TEXT COLLATE NOCASE" : ""));
            ScratchFile.Exec(w, $"CREATE TABLE {SqlText.Name(table)}(Seq INTEGER PRIMARY KEY, FormKey TEXT NOT NULL COLLATE NOCASE, EditorID TEXT COLLATE NOCASE, Plugin TEXT COLLATE NOCASE, LoadOrder INT, IsWinner INT, "
                + string.Join(", ", idx.Concat(defs)) + ")");
            return table;
        }

        /// <summary>The columns of a list projection a query may take from it: those it holds, or, when it holds each
        /// entry (read through the JSON), every column of the view.</summary>
        static List<string> Covers(ViewSpec v, List<string> cols)
        {
            if (!cols.Contains(ViewBuilder.EntryColumn)) return cols;
            var all = v.Columns.Select(c => c.Name).ToList();
            if (v.Lists[^1].Kind == "Dictionary") all.Add("Key");
            if (!v.ScalarEntries) all.Add("Json");
            all.Add(ViewBuilder.EntryColumn);
            return all;
        }

        void Register(SqliteConnection w, ViewSpec v, string table, List<string> cols, long rows)
        {
            cols = Covers(v, cols);
            using var cmd = w.CreateCommand();
            cmd.CommandText = "INSERT INTO Proj(Type, TableName, Cols, Rows) VALUES ($t, $n, $c, $r)";
            cmd.Parameters.AddWithValue("$t", "list:" + v.Name); cmd.Parameters.AddWithValue("$n", table);
            cmd.Parameters.AddWithValue("$c", _language + "|" + string.Join(",", cols)); cmd.Parameters.AddWithValue("$r", rows);
            cmd.ExecuteNonQuery();
        }

        /// <summary>The columns a scratch table of a file holds.</summary>
        HashSet<string> TableColumns(ScratchFile f, string table)
        {
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = _con.CreateCommand();
            cmd.CommandText = $"SELECT name FROM pragma_table_info({SqlText.Literal(table)}, {SqlText.Literal(f.Alias)})";
            using var r = cmd.ExecuteReader();
            while (r.Read()) cols.Add(r.GetString(0));
            return cols;
        }

        /// <summary>Reads the records that hold a list's subrecords, the list's named columns straight from Mutagen (or
        /// out of each record's JSON), into a scratch table.</summary>
        void FillListProjection(SqliteConnection w, ScratchFile file, ReadJob job)
        {
            var clock = Stopwatch.StartNew();
            var v = job.View!;
            long rows = 0, records = 0; int skipped = 0; string? firstSkip = null;
            using var tx = w.BeginTransaction();
            string table = CreateListTable(w, v, job.Columns);
            int levels = v.Lists.Count;
            // The same columns from the records' own bytes where the list is laid out exactly (RawLists.cs)
            var rawList = job.List != null ? RawList.For(v, job.Columns, _e._schema, _e.Views.Catalog(v.Root).Typed) : null;
            // A type that shares its signature with others: only its own records give rows (ReadPlan.OnlyType)
            string? sharedSig = _e._schema.SignatureOf(v.Types[0]) is { } vs && _e._schema.TypesOf(vs).Count > 1 ? vs : null;
            var plan = new ReadPlan
            {
                OnlyType = sharedSig != null ? v.Types[0] : null, ClassOf = sharedSig != null ? SFSearch.Records.RecordClasses.For(_e._schema, sharedSig) : null,
                Full = false, List = job.List, RawList = rawList, ListSql = job.ListSql, ListValues = levels + job.Columns.Count, Winners = file.IsSet, TextLanguage = _language,
                Unsplit = job.ListSql != null && !_e._schema.FieldHoldsRecords(v.Types[0], v.Lists[0].Path.Split('.')[0]),
            };
            string marks = string.Concat(Enumerable.Repeat(",?", levels + job.Columns.Count));
            using (var ins = new BulkInsert(w, $"INSERT INTO {SqlText.Name(table)} VALUES (NULL,?,?,?,?,?{marks})"))
            {
                foreach (var (plugin, entries) in job.Entries!)
                {
                    var member = file.Members.First(m => m.Plugin.Equals(plugin.Name, StringComparison.OrdinalIgnoreCase));
                    foreach (var row in _e._reader.ReadEntries(plugin, entries, plan, _req.Cancel))
                    {
                        if (row.Skip != null) { skipped++; firstSkip ??= $"{plugin.Name}: {row.EditorId ?? "(no EditorID)"} ({row.FormKey}): {row.Skip}"; continue; }
                        records++;
                        foreach (var lr in row.ListRows ?? new List<object?[]>())
                        {
                            ins.Text(1, row.FormKey); ins.Text(2, row.EditorId);
                            if (file.IsSet) { ins.Text(3, plugin.Name); ins.Int(4, member.LoadOrder); ins.Int(5, row.IsWinner ? 1 : 0); }
                            else { ins.Null(3); ins.Null(4); ins.Null(5); }
                            for (int i = 0; i < lr.Length; i++) ins.Value(6 + i, lr[i]);
                            ins.Step();
                            rows++;
                        }
                        row.ListRows = null;
                    }
                    ConfirmUnchanged(plugin);
                }
            }
            Register(w, v, table, job.Columns, rows);
            tx.Commit();
            string label = $"{v.Name} (named columns)";
            _readLabels.Add(label); _usedLabels.Add(label);
            var named = job.Columns.Select(c => c == ViewBuilder.EntryColumn ? "each entry" : c).ToList();
            _answer.Cost.Read.Add($"{v.Name} ({rows:N0} rows of {records:N0} records that hold its subrecords, {(named.Count == 0 ? "no column but the places" : (named.Count == 1 ? "" : "columns ") + string.Join(", ", named))}, "
                + $"{(rawList != null ? "from the records' own bytes" + ViaMutagen(plan) : job.List != null ? "straight from the records" : "out of each record's JSON, one record at a time")}) from {Where(job)} in {Took(clock.Elapsed.TotalSeconds)}");
            if (skipped > 0) Note($"{v.Name}: {skipped:N0} record(s) couldn't be read and aren't counted. The first: {firstSkip}");
        }

        /// <summary>Reads a typed view's named columns of the records a narrowed list's FormKeys have, or of every record
        /// of the type that passes a filter (Session.Filter.cs), for this statement alone, into temporary tables of the
        /// query's own connection (as Session.Keyed.cs does with whole records).</summary>
        void RunKeyedProjections()
        {
            foreach (var job in _keyedProjJobs)
            {
                var (file, view, cols, sig) = (job.File, job.View, job.Columns, job.Sig);
                var clock = Stopwatch.StartNew();
                string table = $"sfs_keyedproj_{file.Alias}_{view.Name}";
                var defs = cols.Select(c => ", " + SqlText.Name(c) + (view.ByName[c].IsLink ? " TEXT COLLATE NOCASE" : ""));
                ScratchFile.Exec(_con, $"DROP TABLE IF EXISTS temp.{SqlText.Name(table)}; CREATE TEMP TABLE {SqlText.Name(table)}(Seq INTEGER PRIMARY KEY, FormKey TEXT NOT NULL COLLATE NOCASE, EditorID TEXT COLLATE NOCASE, "
                    + $"IsOverride INT NOT NULL, Plugin TEXT COLLATE NOCASE, LoadOrder INT, IsWinner INT{string.Concat(defs)}); CREATE INDEX temp.{SqlText.Name(table + "_FormKey")} ON {SqlText.Name(table)}(FormKey);");
                var plan = ProjectionPlan(view, cols, file.IsSet, job.Filter);
                long rows = 0, read = 0; int skipped = 0; string? firstSkip = null;
                string marks = string.Concat(Enumerable.Repeat(",?", cols.Count));
                using (var tx = _con.BeginTransaction())
                {
                    using (var ins = new BulkInsert(_con, $"INSERT INTO temp.{SqlText.Name(table)} VALUES (NULL,?,?,?,?,?,?{marks})"))
                        foreach (var (plugin, entries) in job.Sets)
                        {
                            var member = file.Members.First(m => m.Plugin.Equals(plugin.Name, StringComparison.OrdinalIgnoreCase));
                            read += entries.Length;
                            foreach (var row in _e._reader.ReadEntries(plugin, entries, plan, _req.Cancel))
                            {
                                if (row.Skip != null) { skipped++; firstSkip ??= $"{plugin.Name}: {row.EditorId ?? "(no EditorID)"} ({row.FormKey}): {row.Skip}"; continue; }
                                ins.Text(1, row.FormKey); ins.Text(2, row.EditorId); ins.Int(3, row.IsOverride ? 1 : 0);
                                if (file.IsSet) { ins.Text(4, plugin.Name); ins.Int(5, member.LoadOrder); ins.Int(6, row.IsWinner ? 1 : 0); }
                                else { ins.Null(4); ins.Null(5); ins.Null(6); }
                                for (int i = 0; i < cols.Count; i++) ins.Value(7 + i, row.Values![i]);
                                ins.Step();
                                rows++;
                            }
                            ConfirmUnchanged(plugin);
                        }
                    tx.Commit();
                }
                _keyedProj[(file, view.Name)] = table;
                string from = job.Sets.Count == 1 ? job.Sets[0].Plugin.Name : $"{job.Sets.Count:N0} plugins";
                string columns = cols.Count == 0 ? "from the header index alone" : $"column{(cols.Count == 1 ? "" : "s")} {string.Join(", ", cols)}";
                if (job.Filter is { } f)
                {
                    string label = $"{_e.LabelOf(sig)} (where {f.Name} =)";
                    _usedLabels.Add(label); _readLabels.Add(label);
                    _answer.Cost.Read.Add($"{_e.LabelOf(sig)} ({rows:N0} of {read:N0} with {FilterText(f)}, only those kept, {columns}{HowRead(plan, cols.Count)}) from {from} in {Took(clock.Elapsed.TotalSeconds)}");
                    if (skipped > 0) Note($"{_e.LabelOf(sig)}: {skipped:N0} record(s) couldn't be read and aren't in the answer. The first: {firstSkip}");
                }
                else
                {
                    string label = $"{_e.LabelOf(sig)} (by FormKey)";
                    _usedLabels.Add(label); _readLabels.Add(label);
                    _answer.Cost.Read.Add($"{_e.LabelOf(sig)} ({rows:N0} with the FormKeys that hold the list, {columns}{HowRead(plan, cols.Count)}) from {from} in {Took(clock.Elapsed.TotalSeconds)}");
                    if (skipped > 0) Note($"{_e.LabelOf(sig)}: {skipped:N0} record(s) read by FormKey couldn't be read and aren't in the answer. The first: {firstSkip}");
                }
            }
        }

        void DropKeyedProjections()
        {
            foreach (string table in _keyedProj.Values) ScratchFile.Exec(_con, $"DROP TABLE IF EXISTS temp.{SqlText.Name(table)}");
            _keyedProj.Clear();
            _keyedProjJobs.Clear();
            _joinedRoots = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The typed view as its named columns of a table that holds only those: the records read for this
        /// statement (a temporary table), or a type's named columns in the scratch file. The fixed columns as
        /// ViewBuilder.SelectSql gives them; a set's rows carry their plugin, place and winner mark.</summary>
        string ProjectionSelect(ScratchFile f, ViewSpec view, string table, HashSet<string> only)
        {
            string plugin = PluginExpr(f, "x");
            string versions = !_versions ? "" : f.IsSet ? ", x.LoadOrder AS LoadOrder, x.IsWinner AS IsWinner"
                : $", {(f.Members[0].LoadOrder is int lo ? lo.ToString(CultureInfo.InvariantCulture) : "NULL")} AS LoadOrder, sfs_iswinner(x.FormKey, {plugin}) AS IsWinner";
            var json = new ViewBuilder.JsonJoins();
            var cols = view.Columns.Where(c => only.Contains(c.Name)).Select(c => ", " + (ViewBuilder.IsJson(c) ? json.Column($"json(x.{SqlText.Name(c.Name)})", c.Name) : $"x.{SqlText.Name(c.Name)} AS {SqlText.Name(c.Name)}")).ToList();
            return $"SELECT x.FormKey AS FormKey, x.EditorID AS EditorID, {plugin} AS Plugin, x.IsOverride AS IsOverride{versions}{string.Concat(cols)} FROM {table} x{json.From}"
                + (f.IsSet && _winners ? " WHERE x.IsWinner = 1" : "");
        }

        // -------------------------------------------------------------- A typed view joined to a list by FormKey
        /// <summary>For the statement being run: each root whose typed view it joins to one of its list views by FormKey.</summary>
        void FindJoinedRoots(Probed probed, List<Tok> toks)
        {
            _joinedRoots = new(StringComparer.OrdinalIgnoreCase);
            foreach (var group in probed.Views.Keys.GroupBy(v => v.Root, StringComparer.OrdinalIgnoreCase))
            {
                var typed = group.FirstOrDefault(v => !v.IsList);
                var lists = group.Where(v => v.IsList).Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (typed != null && lists.Count > 0 && JoinedByFormKey(toks, typed.Name, lists)) _joinedRoots.Add(group.Key);
            }
        }

        static readonly HashSet<string> ClauseWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "WHERE", "ON", "USING", "JOIN", "LEFT", "RIGHT", "FULL", "INNER", "OUTER", "CROSS", "NATURAL", "GROUP", "ORDER", "LIMIT", "HAVING",
            "WINDOW", "UNION", "EXCEPT", "INTERSECT", "RETURNING",
        };

        /// <summary>
        /// True when every row of the typed view the statement can use has the FormKey of a row of one of the list views:
        /// the typed view is named once, and in its own SELECT it is joined to a list view by <c>t.FormKey = l.FormKey</c>
        /// as one of the ANDed conditions of the WHERE (any join), or of the ON of an inner join or of a LEFT JOIN whose
        /// right side is the typed view; or by <c>USING (FormKey)</c> when the SELECT reads only those two. No OR or BETWEEN
        /// in that WHERE or ON, no RIGHT, FULL or NATURAL join in that SELECT. Then reading the typed view only for the
        /// list's FormKeys changes no answer. Anything else: false, and the type is read as before.
        /// </summary>
        static bool JoinedByFormKey(List<Tok> toks, string typedName, IReadOnlySet<string> listNames)
        {
            Tok At(int i) => i >= 0 && i < toks.Count ? toks[i] : new Tok(TokKind.Punct, ";", 0, 0, 0);
            bool IsRef(int i, string name) => toks[i].IsName && toks[i].Text.Equals(name, StringComparison.OrdinalIgnoreCase) && !At(i + 1).IsPunct(".") && !At(i + 1).IsPunct("(");
            string Alias(int i)
            {
                int a = i + 1;
                if (At(a).Is("AS")) a++;
                return At(a).IsName && !ClauseWords.Contains(At(a).Text) && !At(a).Is("AS") && !At(a).Is("SELECT") && !At(a).Is("FROM") && !At(a).Is("AND") && !At(a).Is("OR") && !At(a).Is("NOT") && !At(a).Is("INDEXED")
                    ? At(a).Text : toks[i].Text;
            }
            var refs = Enumerable.Range(0, toks.Count).Where(i => IsRef(i, typedName)).ToList();
            if (refs.Count != 1) return false;
            int at = refs[0], d = toks[at].Depth;
            if (!(At(at - 1).Is("FROM") || At(at - 1).Is("JOIN") || At(at - 1).IsPunct(","))) return false;
            string typedAlias = Alias(at);
            // Its SELECT: back to the SELECT at its level, on to the end of that level
            int start = at;
            while (start >= 0 && !(toks[start].Depth == d && toks[start].Is("SELECT"))) { if (toks[start].Depth < d) return false; start--; }
            if (start < 0) return false;
            int end = at;
            while (end < toks.Count && toks[end].Depth >= d && !(toks[end].Depth == d && (toks[end].Is("UNION") || toks[end].Is("EXCEPT") || toks[end].Is("INTERSECT") || toks[end].IsPunct(";")))) end++;
            bool Level(int i) => i >= start && i < end && toks[i].Depth == d;
            // The tables of that SELECT, and the list views' names in it
            var tables = new List<int>();
            bool inFrom = false;
            for (int i = start; i < end; i++)
            {
                if (!Level(i)) continue;
                if (toks[i].Is("RIGHT") || toks[i].Is("FULL") || toks[i].Is("NATURAL")) return false;
                if (toks[i].Is("FROM")) inFrom = true;
                else if (toks[i].Is("WHERE") || toks[i].Is("GROUP") || toks[i].Is("ORDER") || toks[i].Is("LIMIT") || toks[i].Is("HAVING") || toks[i].Is("WINDOW")) inFrom = false;
                if (inFrom && Level(i - 1) && (At(i - 1).Is("FROM") || At(i - 1).Is("JOIN") || At(i - 1).IsPunct(",")) && (toks[i].IsName || toks[i].IsPunct("("))) tables.Add(i);
            }
            var listAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (int i in tables) if (toks[i].IsName && listNames.Contains(toks[i].Text)) listAliases.Add(Alias(i));
            if (listAliases.Count == 0) return false;
            bool Pair(string a, string b) => a.Equals(typedAlias, StringComparison.OrdinalIgnoreCase) && listAliases.Contains(b)
                || b.Equals(typedAlias, StringComparison.OrdinalIgnoreCase) && listAliases.Contains(a);
            // t.FormKey = l.FormKey as one of the ANDed conditions of a WHERE or ON at that level
            for (int i = start; i + 6 < end; i++)
            {
                if (!Level(i) || !(toks[i].IsName && At(i + 1).IsPunct(".") && At(i + 2).Text.Equals("FormKey", StringComparison.OrdinalIgnoreCase) && At(i + 2).IsName
                    && (At(i + 3).IsPunct("=") || At(i + 3).IsPunct("==")) && At(i + 4).IsName && At(i + 5).IsPunct(".") && At(i + 6).IsName && At(i + 6).Text.Equals("FormKey", StringComparison.OrdinalIgnoreCase))) continue;
                if (!Pair(toks[i].Text, At(i + 4).Text)) continue;
                var before = At(i - 1);
                if (!(Level(i - 1) && (before.Is("WHERE") || before.Is("ON") || before.Is("AND")))) continue;
                int n = i + 7;
                if (!(n >= end || !Level(n) || At(n).Is("AND") || ClauseWords.Contains(At(n).Text) && At(n).Kind == TokKind.Word || At(n).IsPunct(",") || At(n).IsPunct(";"))) continue;
                // Its clause: the WHERE or ON before it, and where that ends
                int kw = i - 1;
                while (kw >= start && !(Level(kw) && (toks[kw].Is("WHERE") || toks[kw].Is("ON")))) kw--;
                if (kw < start) continue;
                int stop = kw + 1;
                while (stop < end && !(Level(stop) && (ClauseWords.Contains(toks[stop].Text) && toks[stop].Kind == TokKind.Word || toks[stop].IsPunct(",")))) stop++;
                if (n > stop) continue;
                bool plain = true;
                for (int k = kw + 1; k < stop; k++) if (Level(k) && (toks[k].Is("OR") || toks[k].Is("BETWEEN") || toks[k].Is("CASE"))) plain = false;
                if (!plain) continue;
                if (toks[kw].Is("WHERE")) return true;
                // ON: of the join just before it. A LEFT JOIN keeps its left side's rows: only the typed view as its right side
                int join = kw - 1;
                while (join >= start && !(Level(join) && toks[join].Is("JOIN"))) join--;
                if (join < start) continue;
                bool left = Level(join - 1) && (At(join - 1).Is("LEFT") || At(join - 1).Is("OUTER") && At(join - 2).Is("LEFT"));
                if (!left || join + 1 == at) return true;
            }
            // USING (FormKey) between the only two tables of that SELECT
            if (tables.Count == 2 && tables.Contains(at))
            {
                int other = tables[0] == at ? tables[1] : tables[0];
                if (!(toks[other].IsName && listNames.Contains(toks[other].Text))) return false;
                int join = tables[1] - 1;
                if (!At(join).Is("JOIN")) return false;
                int u = tables[1] + 1;
                while (u < end && !(Level(u) && toks[u].Is("USING"))) { if (Level(u) && (toks[u].Is("ON") || toks[u].Is("WHERE"))) return false; u++; }
                if (u >= end || !At(u + 1).IsPunct("(")) return false;
                bool formKey = false;
                for (int k = u + 2; k < end && toks[k].Depth > d; k++) if (toks[k].IsName && toks[k].Text.Equals("FormKey", StringComparison.OrdinalIgnoreCase)) formKey = true;
                if (!formKey) return false;
                bool left = At(join - 1).Is("LEFT") || At(join - 1).Is("OUTER") && At(join - 2).Is("LEFT");
                return !left || tables[1] == at;
            }
            return false;
        }
    }
}
