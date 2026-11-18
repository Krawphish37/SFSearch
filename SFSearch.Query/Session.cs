// One query, start to end: the scope, the query's own connection with the scratch files attached read only, each
// statement checked, its record types read if they aren't there yet, then run.
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SFSearch.Index;
using SFSearch.Records;
using SQLitePCL;

namespace SFSearch.Query;

public sealed partial class QueryEngine
{
    /// <summary>What a statement reads, as SQLite itself resolved it.</summary>
    sealed class Probed
    {
        /// <summary>Each view of the schema the statement names, with the columns it reads.</summary>
        public Dictionary<ViewSpec, HashSet<string>> Views = new();
        public HashSet<string> Special = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The columns the statement reads of each of <see cref="Special"/>.</summary>
        public Dictionary<string, HashSet<string>> SpecialColumns = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>SQLite's plan for the statement over the stand-ins, with an index on each view's FormKey; null when
        /// the statement compares no FormKey with a text (Session.Keyed.cs).</summary>
        public List<string>? Plan;
        /// <summary>Each column of a typed view the statement compares only with literals: the literals, and SQLite's
        /// plan over the stand-ins with an index on that column (Session.Filter.cs).</summary>
        public Dictionary<string, (List<object> Literals, List<string> Plan)> ColumnPlans = new(StringComparer.OrdinalIgnoreCase);
    }

    enum Route { Full, Projection, Forms, Links, Conditions, ListProjection, KeyedEstimate }

    sealed class ReadJob
    {
        public required ScratchFile File;
        public required Route Route;
        public string Sig = "";
        public List<(IndexedPlugin Plugin, SigLoad Load)> Loads = new();
        public Estimate Estimate;
        public ViewSpec? View;
        public List<string> Columns = new();
        public List<string> LinkSigs = new();
        /// <summary>A list view's named columns read straight from the records (ListReads.cs), and which records.</summary>
        public DirectList? List;
        public Dictionary<IndexedPlugin, int[]>? Entries;
        /// <summary>A list view's rows taken out of each record's JSON instead (ListReads.cs, GAPS S58).</summary>
        public string? ListSql;
        /// <summary>Route.KeyedEstimate: the records whole (else the named columns).</summary>
        public bool Whole;
        /// <summary>Read from the records' own bytes (RawColumns.cs), not through Mutagen.</summary>
        public bool Raw;
        /// <summary>A named column comes out of each record's JSON (the whole record's JSON is made for it).</summary>
        public bool Json;
        /// <summary>A typed view's columns read with the statement's col = literal applied while reading (Session.Filter.cs).</summary>
        public RowFilter? Filter;
        public long Records => Loads.Sum(l => l.Load.Count);
        public long Bytes => Loads.Sum(l => l.Load.Bytes);
    }

    sealed partial class Session
    {
        readonly QueryEngine _e;
        readonly QueryRequest _req;
        readonly QueryAnswer _answer = new();
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly string _language;
        readonly List<ScratchFile> _files = new();
        readonly List<IndexedPlugin> _members = new();
        readonly Dictionary<ScratchFile, Dictionary<string, DoneRow>> _done = new();
        /// <summary>For a file and a record type: the table that holds named columns only, and which columns.</summary>
        readonly Dictionary<(ScratchFile, string), (string Table, HashSet<string> Columns)> _projections = new();
        /// <summary>For a file and a record type: every table of its named columns, each read on its own route (GAPS S146).</summary>
        readonly Dictionary<(ScratchFile, string), List<(string Table, HashSet<string> Columns)>> _projectionTables = new();
        readonly List<string> _ownViews = new();
        readonly HashSet<string> _usedLabels = new(StringComparer.Ordinal);
        readonly HashSet<string> _readLabels = new(StringComparer.Ordinal);
        readonly HashSet<ViewSpec> _allViewsUsed = new();
        readonly HashSet<string> _specialUsed = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _noted = new(StringComparer.Ordinal);
        SqliteConnection _con = null!;
        Guard _guard = null!;
        Functions _functions = null!;
        /// <summary>How many texts each open plugin's own strings tables had given when the query began (GAPS S190).</summary>
        readonly Dictionary<string, long> _tagGiven = new(StringComparer.OrdinalIgnoreCase);
        bool _versions, _winners;
        string _cursorBase = "";
        long _cursorOffset;

        public Session(QueryEngine engine, QueryRequest request)
        {
            _e = engine; _req = request;
            // A language that isn't one stops the query in Run, with the languages there are
            _languageAsked = request.Language ?? engine._options.Language;
            _language = TextLanguages.TryName(_languageAsked, out var name) ? name : "";
        }

        readonly string _languageAsked;

        HeaderIndex Index => _e._index;

        public QueryAnswer Run()
        {
            try
            {
                if (_language.Length == 0) throw Languages.Unknown(_languageAsked);
                ReadCursor();
                Lap("start");
                PlanScope();
                Lap("scope");
                Open();
                Lap("open");
                var statements = SqlText.SplitStatements(_req.Sql);
                if (statements.Count == 0) throw new QueryException("the SQL is empty", "write a SELECT");
                for (int i = 0; i < statements.Count; i++)
                    if (!RunStatement(statements[i], i == statements.Count - 1, statements.Count)) break;
                Finish();
            }
            catch (QueryException e) { Fail(e.Message, e.Fix); }
            catch (RecordException e) { Fail(e.Message, null); }
            catch (OperationCanceledException) { Fail("the query was cancelled", null); }
            catch (SqliteException e) { Fail(SqliteText(e.Message), "check the SQL"); }
            finally
            {
                Close();
                _answer.Cost.Seconds = _clock.Elapsed.TotalSeconds;
                _answer.Cost.PeakWorkingSetMB = Process.GetCurrentProcess().PeakWorkingSet64 >> 20;
            }
            return _answer;
        }

        void Fail(string what, string? fix)
        {
            _answer.Error = what; _answer.Fix = fix;
            _answer.HasResult = false; _answer.Rows.Clear(); _answer.Columns.Clear();
        }

        static string SqliteText(string message)
        {
            // Microsoft.Data.Sqlite wraps the text: SQLite Error 1: 'no such function: foo'.
            var m = Regex.Match(message, @"^SQLite Error \d+: '(.*)'\.?$", RegexOptions.Singleline);
            return "SQLite: " + (m.Success ? m.Groups[1].Value : message);
        }

        void Note(string text) { if (_noted.Add(text)) _answer.Notes.Add(text); }

        double _lap;
        void Lap(string step)
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            _answer.Cost.Steps.Add($"{step} {now - _lap:F1} ms");
            _lap = now;
        }

        // -------------------------------------------------------------- The cursor
        void ReadCursor()
        {
            var h = new XxHash64();
            h.Append(Encoding.UTF8.GetBytes(_req.Sql.Trim()));
            h.Append(Encoding.UTF8.GetBytes("|" + _req.Scope + "|" + string.Join(",", _req.Plugins.Select(p => p.ToLowerInvariant()))));
            // The same SQL with other parameter values is another query
            foreach (var p in _req.Parameters.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                h.Append(Encoding.UTF8.GetBytes("|" + p.Key.ToLowerInvariant() + "=" + Convert.ToString(p.Value, CultureInfo.InvariantCulture)));
            _cursorBase = Convert.ToHexString(h.GetCurrentHash())[..6].ToLowerInvariant() + "-";
            _answer.CursorBase = _cursorBase;
            if (string.IsNullOrWhiteSpace(_req.Cursor)) return;
            string c = _req.Cursor.Trim().Trim('"');
            if (!c.StartsWith(_cursorBase, StringComparison.OrdinalIgnoreCase) || !long.TryParse(c[_cursorBase.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out _cursorOffset))
                throw new QueryException($"the cursor \"{c}\" doesn't belong to this query", "pass the cursor together with the same SQL and scope that gave it");
        }

        // -------------------------------------------------------------- The scope
        void PlanScope()
        {
            var index = Index;
            var names = _req.Plugins.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (_req.Scope == ScopeKind.Plugins)
            {
                if (names.Count == 0) throw new QueryException("the query names no plugin", "name one or more plugins, or use the winners or the versions scope");
                // The quick stamp of each plugin named and of its masters, held against the header index's
                bool refresh = index.Plugins.Count == 0;
                var check = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var n in names)
                {
                    var p = index.Plugin(n);
                    if (p == null) { refresh = true; continue; }
                    check.Add(p.Name);
                    foreach (var m in p.Header?.Masters ?? Array.Empty<string>()) check.Add(m);
                }
                foreach (var n in check)
                {
                    if (refresh) break;
                    var p = index.Plugin(n);
                    if (p == null) { refresh = true; break; }
                    try { if (QuickStamp.Read(p.Path) != p.Stamp) refresh = true; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { refresh = true; }
                }
                // A localized plugin's string files that changed while the plugin didn't (GAPS S31): one look, about a millisecond
                if (!refresh && index.StampsStrings)
                {
                    var held = check.Select(n => index.Plugin(n)).Where(p => p != null).Select(p => p!).ToList();
                    var now = index.StringsNow(held);
                    for (int i = 0; i < held.Count; i++) if (!now[i].SameAs(held[i].Strings)) { refresh = true; break; }
                }
                if (refresh) NoteRefresh(index.Refresh());
            }
            else NoteRefresh(index.Refresh());
            foreach (var note in _e._records.Sessions.Use().Notes) Note(note);

            if (_req.Scope == ScopeKind.Plugins)
            {
                foreach (var n in names)
                {
                    var p = index.Plugin(n);
                    if (p == null)
                    {
                        var near = SFSearch.SchemaGen.NearNames.Plugins(n, index.Plugins.Select(x => x.Name));
                        throw new QueryException($"there is no plugin named \"{n}\" in the Data folder." + SFSearch.SchemaGen.NearNames.DidYouMean(near), "name a plugin that is in the Data folder: a plugin must be in Data to be read");
                    }
                    CheckReadable(p, fail: true);
                    _members.Add(p);
                }
                if (_members.Count <= _e._options.MaxAttached)
                    foreach (var p in _members) _files.Add(_e.PluginFile(p));
                else _files.Add(SetFile("set", _members));
                foreach (var p in _members) NoteProblems(p);
            }
            else
            {
                _winners = _req.Scope == ScopeKind.Winners; _versions = _req.Scope == ScopeKind.Versions;
                var loaded = index.Plugins.Where(p => p.Order.Enabled).OrderBy(p => p.Order.Position).ToList();
                var enabled = loaded.ToList();
                // A loaded plugin Mutagen refuses (C204), and any built on it, has no rows: the versions before it win
                foreach (var p in loaded.Where(p => p.LeftOut != null).ToList())
                {
                    Note($"{p.Name} is left out: {p.LeftOut}. Its records have no rows here, and the version before each of its overrides wins.");
                    loaded.Remove(p);
                }
                if (loaded.Count == 0) throw new QueryException("the load order has no enabled plugin", "check plugins.txt, or name plugins with the plugins scope");
                var unread = new List<string>();
                foreach (var p in loaded)
                    if (CheckReadable(p, fail: false) is { } why) unread.Add($"{p.Name} ({why})"); else _members.Add(p);
                if (unread.Count > 0)
                    Note($"{unread.Count:N0} loaded plugin(s) can't be read and have no rows here: {string.Join("; ", unread.Take(5))}{(unread.Count > 5 ? "; …" : "")}.");
                var old = _members.Where(p => p.Problems.TypeMismatches.Count > 0).ToList();
                if (old.Count > 0)
                    Note($"{old.Count:N0} loaded plugin(s) hold overrides of a different type than their base (built for an older version of a master), so some of their rows read wrong: {string.Join(", ", old.Take(5).Select(p => $"{p.Name} ({p.Problems.TypeMismatches.Count:N0})"))}{(old.Count > 5 ? ", …" : "")}.");
                _files.Add(SetFile("lo", _members));
                string txt = _e._options.PluginsTxt ?? LoadOrder.DefaultPluginsTxt;
                string date = File.Exists(txt) ? File.GetLastWriteTime(txt).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "no plugins.txt";
                _answer.LoadOrder = LoadOrderText(date, enabled);
            }
            for (int i = 0; i < _files.Count; i++) { _files[i].Alias = "p" + i; _files[i].EnsureCreated(); }
            _answer.Sources.AddRange(_members.Select(p => new AnswerSource(p.Name, p.Stamp.Length, p.ContentKey.ToString())));
        }

        /// <summary>
        /// The load order an answer was read from, in numbers that agree with the game's and loadorder2's: the plugins
        /// loaded, then how many were read and which loaded ones were left out and why (refused by Mutagen, C204), and the
        /// enabled ones that can't be loaded (a missing master, or a master that can't be loaded: Mutagen's load order
        /// drops them, xEdit doesn't load them).
        /// </summary>
        string LoadOrderText(string date, List<IndexedPlugin> enabled)
        {
            var read = _members.ToHashSet();
            var cant = enabled.Where(p => p.CantLoad).ToList();
            var leftOut = enabled.Where(p => !p.CantLoad && !read.Contains(p)).ToList();
            static string List(List<IndexedPlugin> ps, Func<IndexedPlugin, string> why) =>
                string.Join(", ", ps.Take(5).Select(p => $"{p.Name} ({why(p)})")) + (ps.Count > 5 ? $", +{ps.Count - 5:N0} more" : "");
            string text = $"load order of {date} ({enabled.Count - cant.Count:N0} loaded plugins";
            if (leftOut.Count > 0) text += $", {_members.Count:N0} read: {List(leftOut, p => p.LeftOutShort ?? (p.Problems.ReadError != null ? "can't be read" : "left out"))} left out";
            if (cant.Count > 0) text += $"; {cant.Count:N0} more enabled that can't be loaded: {List(cant, p => p.Problems.MissingMasters.Count > 0 ? "missing master " + string.Join(", ", p.Problems.MissingMasters) : $"its master {p.Problems.MasterUnreadable} can't be loaded")}";
            return text + ")";
        }

        void NoteRefresh(RefreshReport r)
        {
            foreach (var n in r.ContentChanged) Note($"{n} changed on disk since it was last read: it is read again from the file as it is now.");
            foreach (var n in r.Rekeyed) Note($"{n} has a master that changed size, so its FormIDs read differently: it is read again.");
        }

        /// <summary>Null when the plugin can be read; otherwise why not.</summary>
        string? CheckReadable(IndexedPlugin p, bool fail)
        {
            string? why = p.Segment == null || p.Header == null ? p.Problems.ReadError ?? "it has no header index"
                : p.Problems.MissingMasters.Count > 0 ? $"its master {string.Join(", ", p.Problems.MissingMasters)} isn't in Data, and without it the FormIDs in {p.Name} can't be told apart"
                // Mutagen refuses it (C204: one FormKey held twice), or a master of it: left out, as Mutagen's load order leaves it
                : p.LeftOut;
            if (why != null && fail) throw new QueryException($"{p.Name} can't be read: {why}", p.Problems.MissingMasters.Count > 0 ? "put the master back, then run the query again"
                : p.LeftOut != null ? "leave it out of the query; record (any view, raw included) and check2 still read it, to see what is wrong" : null);
            return why;
        }

        void NoteProblems(IndexedPlugin p)
        {
            if (p.Problems.TypeMismatches.Count > 0)
                Note($"{p.Name}: {p.Problems.TypeMismatches.Count:N0} override(s) have another type than the record they override (built for an older version of a master), so those rows read wrong.");
            if (p.Problems.Duplicates > 0)
                Note($"{p.Name} holds {p.Problems.Duplicates:N0} FormKey(s) more than once, so FormKey isn't a unique key in its tables.");
        }

        ScratchFile SetFile(string kind, List<IndexedPlugin> members)
        {
            // Who wins depends on every plugin there is, so the file's name takes in all of them and the load order
            var parts = new List<string> { kind, _e._recordLanguage };
            // A localized plugin's string files too (GAPS S31): a changed one gives a new file
            foreach (var p in Index.Plugins) parts.Add($"{p.Name.ToLowerInvariant()}|{p.ReadingKey}|{(p.Order.Enabled ? p.Order.Position : -1)}{(p.Strings.Stamped ? "|" + p.Strings.Key : "")}");
            if (kind != "lo") { parts.Add("members"); parts.AddRange(members.Select(m => m.Name.ToLowerInvariant())); }
            return new ScratchFile
            {
                Path = Path.Combine(_e._options.ScratchFolder, ScratchFile.SetFileName(kind, _e._recordLanguage, parts)), IsSet = true,
                Members = members.Select(p => new ScratchMember(p.Name, p.Path, p.Stamp.Length, p.ReadingKey, p.ContentKey, p.Order.Enabled ? p.Order.Position : null)).ToList(),
            };
        }

        // -------------------------------------------------------------- The query's own connection
        void Open()
        {
            // A private database in memory. The "file:" form switches on URI names, which the read-only attaches need.
            _con = new SqliteConnection($"Data Source=file:sfsq{Guid.NewGuid():N}?mode=memory&cache=private;Pooling=False");
            _con.Open();
            SqliteTuning.TempFolder(Path.Combine(_e._options.ScratchFolder, "temp"));
            ScratchFile.Exec(_con, SpillPragmas(MemoryLimit()));
            string schemaUri = "file:///" + _e._schemaDb.Replace('\\', '/').Replace("%", "%25").Replace("?", "%3f").Replace("#", "%23") + "?mode=ro";
            ScratchFile.Exec(_con, $"ATTACH DATABASE {SqlText.Literal(schemaUri)} AS schema");
            foreach (var f in _files)
            {
                ScratchFile.Exec(_con, $"ATTACH DATABASE {SqlText.Literal(f.ReadOnlyUri)} AS {f.Alias}; PRAGMA {f.Alias}.cache_size = -131072; PRAGMA {f.Alias}.mmap_size = 17179869184;");
                RefreshDone(f);
            }
            _functions = new Functions(_e._records, _language) { Label = _e.LabelOf };
            foreach (var s in _e._records.Sessions.Open) if (s.TagTextsMade is { } made) _tagGiven[s.Name] = Interlocked.Read(ref made.Given);
            _functions.Register(_con);
            // Signature to record type, for the Forms view (a signature with several types gives its union view)
            ScratchFile.Exec(_con, "CREATE TEMP TABLE sfs_sigtype(Sig TEXT PRIMARY KEY, Type TEXT) WITHOUT ROWID");
            using (var ins = _con.CreateCommand())
            {
                ins.CommandText = "INSERT INTO sfs_sigtype VALUES ($s, $t)";
                var ps = ins.Parameters.Add("$s", SqliteType.Text); var pt = ins.Parameters.Add("$t", SqliteType.Text);
                foreach (var sig in _e._schema.Signatures) { ps.Value = sig; pt.Value = _e.LabelOf(sig); ins.ExecuteNonQuery(); }
            }
            _guard = new Guard(_con);
            _req.Cancel.Register(() => { try { if (_con.Handle is { } h) raw.sqlite3_interrupt(h); } catch (ObjectDisposedException) { } });
        }

        /// <summary>The host's memory limit for this command (MB), or 0 for none.</summary>
        long MemoryLimit()
        {
            long mb = _e._options.MemoryLimitMB?.Invoke() ?? 0;
            return mb > 0 ? mb : 0;
        }

        /// <summary>
        /// How the query's connection keeps big sorts and temporary tables (GAPS S104). temp_store FILE: what doesn't fit
        /// in memory goes to temporary files (SqliteTuning.TempFolder) instead of growing in memory without end, as
        /// temp_store MEMORY let a GROUP BY do (16 GB in 5 s). A sort keeps 8 MB in memory, then writes that as a sorted
        /// run to its file and merges the runs on 4 threads; a sort that fits writes nothing. 8 MB is the fastest size
        /// measured, not a saving: every Form of Starfield.esm (3.8 million rows) sorted by EditorID in 1.3-1.6 s with
        /// 8 MB runs, 2.1-2.6 s with 32 or 64 MB runs, 5.2-6.8 s all in memory as before. Temporary tables (the records a
        /// statement reads by FormKey, a query's own CREATE TEMP TABLE) keep up to an eighth of the command's memory limit
        /// in SQLite's cache (at least 64 MB; 1 GB when there is no limit); Windows keeps the temporary files in its
        /// file cache while it has memory to spare, so settings that allow all memory use it.
        /// </summary>
        internal static string SpillPragmas(long limitMB)
        {
            long tempMB = limitMB > 0 ? Math.Max(64, limitMB / 8) : 1024;
            return $"PRAGMA temp_store = FILE; PRAGMA main.cache_size = -{SortRunKB}; PRAGMA temp.cache_size = -{tempMB * 1024}; PRAGMA threads = 4;";
        }

        /// <summary>What a sort keeps in memory before it writes a sorted run (SQLite takes it from the main database's
        /// cache_size; the main database is in memory and holds nothing else).</summary>
        const int SortRunKB = 8192;

        void Close()
        {
            if (_con == null) return;
            try { _con.Dispose(); } catch (SqliteException) { }
        }

        void RefreshDone(ScratchFile f)
        {
            _done[f] = ScratchFile.ReadDone(_con, f.Alias);
            foreach (var k in _projections.Keys.Where(k => k.Item1 == f).ToList()) _projections.Remove(k);
            foreach (var k in _projectionTables.Keys.Where(k => k.Item1 == f).ToList()) _projectionTables.Remove(k);
            using var cmd = _con.CreateCommand();
            cmd.CommandText = $"SELECT Type, TableName, Cols FROM {f.Alias}.Proj ORDER BY Rows";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                // Cols: the language, a bar, the column names
                string cols = r.GetString(2);
                int bar = cols.IndexOf('|');
                if (bar < 0 || !cols[..bar].Equals(_language, StringComparison.OrdinalIgnoreCase)) continue;
                var set = new HashSet<string>(cols[(bar + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
                // The table with the most columns wins when several fit
                if (!_projections.TryGetValue((f, r.GetString(0)), out var have) || set.Count >= have.Columns.Count)
                    _projections[(f, r.GetString(0))] = (r.GetString(1), set);
                if (!_projectionTables.TryGetValue((f, r.GetString(0)), out var all)) _projectionTables[(f, r.GetString(0))] = all = new();
                all.Add((r.GetString(1), set));
            }
        }

        // -------------------------------------------------------------- One statement
        bool RunStatement(string sql, bool last, int count)
        {
            var toks = SqlText.Tokenize(sql);
            Guard.CheckFirstWord(toks);
            CheckLanguageLiterals(toks);
            CheckPositionalParameters(toks);
            DropOwnViews();
            DropKeyed();
            // text(Name, 'ja') on a view's text column reads the translated value instead; if the changed statement
            // doesn't compile (the column came from a subquery, say), the statement runs as written
            Probed probed;
            if (RewriteTextCalls(sql, toks) is { } rewritten)
            {
                try { probed = Probe(rewritten); sql = rewritten; toks = SqlText.Tokenize(sql); }
                catch (QueryException) { probed = Probe(sql); }
            }
            else probed = Probe(sql);
            Lap("probe");
            foreach (var v in probed.Views.Keys) _allViewsUsed.Add(v);
            foreach (var s in probed.Special) _specialUsed.Add(s);
            // The plugin header's typed view never has rows: the header (TES4) is no record of the plugin's groups (GAPS S91)
            if (probed.Views.Keys.Any(v => v.Types.Contains("StarfieldModHeader")))
                Note("StarfieldModHeader and its list views have no rows: a plugin's header (TES4) isn't one of its records. The view Plugins has every plugin's header (flags, size, record count, masters); record <plugin> reads one header field by field.");
            CheckEditorIdLiterals(toks, probed);
            // A typed view narrowed by FormKey reads only those records (Session.Keyed.cs)
            FindKeyedRoots(probed, toks);
            // A typed view joined to one of its list views by FormKey reads only the list's FormKeys (ListReads.cs)
            FindJoinedRoots(probed, toks);
            // A typed view whose rows all have one column equal to a literal: only those rows are inserted (Session.Filter.cs)
            FindFilterRoots(probed, toks);
            // Records and Links: the types the statement narrows to are read first (Session.AutoReads.cs)
            var plan = probed;
            _autoReadNote = _autoReadRefused = null;
            if (AutoReads(probed, toks) is { Count: > 0 } extra)
            {
                plan = new Probed { Special = probed.Special };
                foreach (var kv in probed.Views) plan.Views[kv.Key] = kv.Value;
                foreach (var v in extra) plan.Views[v] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            var jobs = PlanReads(plan);
            if (!Admit(jobs)) { if (_autoReadRefused != null) Note(_autoReadRefused); return false; }
            if (_autoReadNote != null) Note(_autoReadNote);
            Lap("plan");
            RunJobs(jobs);
            RunKeyed();
            RunKeyedProjections();
            Lap("read");
            CreateViews(probed, GroupsRows(toks), JoinsRows(toks));
            // A record view whose Json a join names goes first in the join, or stays folded into it (GAPS S126)
            sql = PlaceJsonViews(sql, ref toks, probed);
            Lap("views");
            // A sort that would keep each record's whole Json for every row of a join is said before it runs (GAPS S110)
            if (!AdmitTempFiles(sql, toks, probed)) return false;
            Lap("temp files");
            Execute(sql, toks, last, count);
            Lap("run");
            return true;
        }

        void DropOwnViews()
        {
            foreach (var v in _ownViews) ScratchFile.Exec(_con, $"DROP VIEW IF EXISTS temp.{SqlText.Name(v)}");
            _ownViews.Clear();
        }

        /// <summary>
        /// Compiles the statement against empty tables that have each view's columns, and lets SQLite say which tables
        /// and columns it reads. A name SQLite doesn't know is answered with the nearest real names.
        /// </summary>
        Probed Probe(string sql)
        {
            var stubs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);   // name -> ViewSpec, or the special table's name
            var reads = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            // The names that stand for the plugins' records in this statement: it may read them, never change them
            var guarded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var firstToks = SqlText.Tokenize(sql);
            List<string>? plan = null;
            Dictionary<string, (List<object>, List<string>)>? columnPlans = null;
            string firstWord = firstToks.Count > 0 && firstToks[0].Kind == TokKind.Word ? firstToks[0].Text.ToUpperInvariant() : "";
            try
            {
                for (int round = 0; ; round++)
                {
                    reads.Clear();
                    _guard.Reads = reads; _guard.Denied = null; _guard.DeniedChange = null; _guard.Protected = guarded; _guard.Current = Guard.Mode.User;
                    int rc = raw.sqlite3_prepare_v2(_con.Handle!, sql, out var stmt);
                    string message = rc == raw.SQLITE_OK ? "" : raw.sqlite3_errmsg(_con.Handle!).utf8_to_string();
                    stmt?.Dispose();
                    _guard.Current = Guard.Mode.Off; _guard.Reads = null; _guard.Protected = null;
                    if (rc == raw.SQLITE_OK) break;
                    if (_guard.DeniedChange != null) throw Guard.ChangeRefused(_guard.DeniedChange);
                    if (_guard.Denied != null)
                        throw new QueryException($"the query isn't allowed {_guard.Denied}", "a query may read, and may make temporary tables, indexes and views of its own (CREATE TEMP …)");
                    // DROP VIEW of a record type's view: SQLite looks for a view of that name and finds none
                    var noView = Regex.Match(message, @"no such view: (?:(?:main|temp)\.)?(.+)$");
                    if (noView.Success && noView.Groups[1].Value.Trim().Trim('"') is var viewName && (Special.Columns(viewName, _versions) != null || _e.Views.Find(viewName) != null))
                        throw Guard.ChangeRefused($"DROP on {viewName}");
                    // A change to a table of the schema (schema.Fields is a view of the schema database)
                    var modify = Regex.Match(message, @"cannot modify (.+?) because it is a view");
                    if (modify.Success) throw Guard.ChangeRefused($"{(firstWord.Length > 0 ? firstWord : "a change")} on {modify.Groups[1].Value}", "the schema (read only, the same for every query)");
                    var m = Regex.Match(message, @"no such table: (?:(?:main|temp)\.)?(.+)$");
                    if (m.Success && round < 200)
                    {
                        string name = m.Groups[1].Value.Trim().Trim('"');
                        // A statement that isn't a read and still can't see the name once it stands there: it asks for
                        // the table in the stored part of the database, to change it (CREATE INDEX … ON Weapon)
                        if (stubs.ContainsKey(name) && firstWord is not ("" or "SELECT" or "WITH" or "EXPLAIN" or "VALUES"))
                            throw Guard.ChangeRefused($"{firstWord} on {name}");
                        if (!stubs.ContainsKey(name))
                        {
                            if (Special.Columns(name, _versions) is { } special)
                            {
                                ScratchFile.Exec(_con, ViewBuilder.StubSql(Special.Canonical(name), special));
                                stubs[name] = Special.Canonical(name);
                                guarded.Add(Special.Canonical(name));
                                continue;
                            }
                            if (_e.Views.Find(name) is { } view)
                            {
                                ScratchFile.Exec(_con, ViewBuilder.StubSql(view.Name, ViewBuilder.ColumnNames(view, _versions)));
                                stubs[name] = view;
                                guarded.Add(view.Name);
                                continue;
                            }
                        }
                        throw UnknownTable(name);
                    }
                    m = Regex.Match(message, @"no such column: (?:""([^""]+)""|(\S+))");
                    if (m.Success) throw UnknownColumn(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, stubs.Values.OfType<ViewSpec>().ToList(), stubs.Values.OfType<string>().ToList());
                    throw new QueryException("SQLite: " + message, "check the SQL");
                }
                columnPlans = ColumnPlans(sql, firstToks, stubs.Values.OfType<ViewSpec>(), reads);
                if (FormKeyLiterals(firstToks) is { Count: > 0 })
                    plan = PlanLines(sql, stubs.Values.OfType<ViewSpec>());
            }
            finally
            {
                _guard.Current = Guard.Mode.Off; _guard.Reads = null; _guard.Protected = null;
                foreach (var s in stubs.Values) ScratchFile.Exec(_con, $"DROP TABLE IF EXISTS temp.{SqlText.Name(s is ViewSpec v ? v.Name : (string)s)}");
            }
            var probed = new Probed { Plan = plan };
            if (columnPlans != null) foreach (var (k, v) in columnPlans) probed.ColumnPlans[k] = v;
            foreach (var (name, target) in stubs)
            {
                string table = target is ViewSpec vs ? vs.Name : (string)target;
                // Named, and no column of it read: still a table of the statement (a view joined by USING (FormKey)
                // has its FormKey read from the other side; GAPS S79)
                if (!reads.TryGetValue(table, out var cols)) cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (target is ViewSpec v) probed.Views[v] = cols;
                else { probed.Special.Add((string)target); probed.SpecialColumns[(string)target] = cols; }
            }
            return probed;
        }

        // -------------------------------------------------------------- Running it
        void Execute(string sql, List<Tok> toks, bool last, int count)
        {
            var clock = Stopwatch.StartNew();
            _guard.Denied = null; _guard.Reads = null; _guard.Current = Guard.Mode.User;
            WatchStart();
            try
            {
                using var cmd = _con.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 0;
                BindParameters(cmd, toks);
                using var r = cmd.ExecuteReader();
                if (r.FieldCount == 0) return;
                if (!last)
                {
                    Note($"Only the last statement's rows are the answer: an earlier one of the {count} gave rows that aren't shown.");
                    return;
                }
                _answer.HasResult = true;
                _answer.Columns.Clear(); _answer.Rows.Clear();
                for (int i = 0; i < r.FieldCount; i++) _answer.Columns.Add(ColumnName(r.GetName(i)));
                long n = 0;
                int page = Math.Max(1, _req.PageRows);
                while (r.Read())
                {
                    if ((n & 0x3FF) == 0) _req.Cancel.ThrowIfCancellationRequested();
                    if (n >= _cursorOffset && _answer.Rows.Count < page)
                    {
                        var row = new object?[r.FieldCount];
                        for (int i = 0; i < row.Length; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
                        _answer.Rows.Add(row);
                    }
                    n++;
                }
                _answer.TotalRows = n;
                _answer.Offset = Math.Min(_cursorOffset, n);
                if (_answer.Offset + _answer.Rows.Count < n) _answer.Cursor = _answer.CursorAt(_answer.Offset + _answer.Rows.Count);
                bool explain = toks.Count > 0 && (toks[0].Is("EXPLAIN") || toks[0].Is("PRAGMA"));
                if (n > 1 && !explain && !SqlText.HasTopLevelOrderBy(toks))
                    Note("The query has no ORDER BY, so the order of its rows isn't fixed (they come in file order today). Add ORDER BY to get the same order every time.");
                if (n == 0 && !explain) NoteWhyEmpty(toks);
            }
            catch (SqliteException) when (_guard.Denied != null)
            {
                throw new QueryException($"the query isn't allowed {_guard.Denied}", "a query may read, and may make temporary tables, indexes and views of its own (CREATE TEMP …)");
            }
            catch (SqliteException e) when (e.SqliteErrorCode == raw.SQLITE_INTERRUPT && _stoppedBy != null)
            {
                // Stopped by the watch (Session.Memory.cs): past the command's memory, or its temporary files filling the drive
                throw MemoryStop();
            }
            catch (SqliteException e) when (e.SqliteErrorCode == raw.SQLITE_INTERRUPT)
            {
                throw new OperationCanceledException();
            }
            catch (SqliteException e) when (e.SqliteErrorCode == raw.SQLITE_NOMEM)
            {
                throw MemoryStop(outOfMemory: true);
            }
            catch (SqliteException e) when (e.SqliteErrorCode == raw.SQLITE_FULL)
            {
                _stoppedBy = "disk"; _stoppedAtMB = Math.Max(0, FreeMB(_tempRoot));
                throw MemoryStop();
            }
            catch (SqliteException e) when (e.SqliteErrorCode == raw.SQLITE_READONLY)
            {
                throw new QueryException("the query tried to write to a read-only table: the scratch tables and the schema can't be changed", "make a temporary table of your own (CREATE TEMP TABLE … AS SELECT …) and change that");
            }
            finally
            {
                WatchStop();
                _guard.Current = Guard.Mode.Off;
                _answer.Cost.QuerySeconds += clock.Elapsed.TotalSeconds;
            }
        }

        void Finish()
        {
            NoteTexts();
            // Records that couldn't be read aren't in the tables: every answer that used their type says so
            foreach (var f in _files)
                foreach (var sig in _usedSigs)
                {
                    if (!_done[f].TryGetValue(sig, out var d) || d.Skipped == 0) continue;
                    using var cmd = _con.CreateCommand();
                    cmd.CommandText = $"SELECT count(*), min(coalesce(Plugin, '') || ': ' || Reason), sum(FormKey IS NULL) FROM {f.Alias}.Skipped WHERE Sig = $s";
                    cmd.Parameters.AddWithValue("$s", sig);
                    using var r = cmd.ExecuteReader();
                    if (r.Read() && r.GetInt64(0) > 0)
                        Note($"{_e.LabelOf(sig)}: {d.Skipped:N0} record(s) aren't in the table because they couldn't be read. The first: {r.GetString(1)}");
                }
            foreach (var p in _functions.PluginsRead)
                if (!_answer.Sources.Any(s => s.Plugin.Equals(p, StringComparison.OrdinalIgnoreCase)) && Index.Plugin(p) is { } ip)
                    _answer.Sources.Add(new AnswerSource(ip.Name, ip.Stamp.Length, ip.ContentKey.ToString()));
            foreach (var label in _usedLabels.Where(l => !_readLabels.Contains(l)).OrderBy(l => l, StringComparer.Ordinal))
                _answer.Cost.AlreadyRead.Add(label);
            // Which of Records, Forms and Links the statements read (as SQLite resolved the names, so a column or an
            // alias of that name doesn't count), and the record types the scratch tables of this scope hold now
            _answer.SpecialTables.AddRange(_specialUsed.OrderBy(s => s, StringComparer.Ordinal));
            _answer.TypesRead.AddRange(_files.SelectMany(f => _done.TryGetValue(f, out var d) ? d.Keys : Enumerable.Empty<string>()).Where(k => k != FormsDone)
                .Select(_e.LabelOf).Distinct().OrderBy(t => t, StringComparer.Ordinal));
        }

        readonly HashSet<string> _usedSigs = new(StringComparer.Ordinal);
    }
}
