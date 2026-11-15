// Reading a plugin's records of one signature for the scratch tables. The work per record (read through Mutagen,
// to JSON, the links found in the JSON, the JSON turned into SQLite's JSONB) is done on reading threads; the one
// inserting thread only binds what they made. Rows come out in file order, in batches, a bounded number at a time.
using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SFSearch.Index;
using SFSearch.Records;
using SQLitePCL;

namespace SFSearch.Query;

sealed class ReadRow
{
    public string FormKey = "";
    public string? EditorId;
    public string Type = "";
    public string Sig = "";
    public string? Parent;
    public bool IsOverride, IsWinner;
    public long Pos;
    public byte[]? Jsonb;
    /// <summary>The record's links as lines of "path TAB FormKey", UTF-8.</summary>
    public byte[]? Links;
    public int JsonBytes;
    /// <summary>Why the record isn't in the table, when it couldn't be read.</summary>
    public string? Skip;
    /// <summary>The projected columns, when only named columns were read.</summary>
    public object?[]? Values;
}

/// <summary>Finds the links in a record's JSON: every text value that is a FormKey, with its path.</summary>
static class LinkWalker
{
    public static bool IsFormKey(ReadOnlySpan<byte> s)
    {
        if (s.Length < 11 || s[6] != (byte)':') return false;
        for (int i = 0; i < 6; i++) { byte c = s[i]; if (!(c >= '0' && c <= '9' || c >= 'A' && c <= 'F' || c >= 'a' && c <= 'f')) return false; }
        int n = s.Length;
        if (s[n - 4] != (byte)'.' || (s[n - 3] | 0x20) != 'e' || (s[n - 2] | 0x20) != 's') return false;
        byte last = (byte)(s[n - 1] | 0x20);
        if (last != 'm' && last != 'p' && last != 'l') return false;
        for (int i = 7; i < n - 4; i++) if (s[i] == (byte)':' || s[i] < 0x20) return false;
        return true;
    }

    /// <summary>Lines of "path TAB FormKey LF". The record's own FormKey and the markers of records nested in it
    /// aren't links. Null when the record has none.</summary>
    public static byte[]? Walk(ReadOnlySpan<byte> utf8, out int count)
    {
        count = 0;
        var reader = new Utf8JsonReader(utf8);
        var names = new List<string?>(16);   // the property name of each open level (null for an array)
        var index = new List<int>(16);       // the next index of each open array
        ArrayBufferWriter<byte>? buf = null;
        string? pending = null;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    pending = reader.GetString();
                    continue;
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    names.Add(Take(names, index, ref pending));
                    index.Add(reader.TokenType == JsonTokenType.StartArray ? 0 : -1);
                    continue;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    names.RemoveAt(names.Count - 1); index.RemoveAt(index.Count - 1);
                    continue;
                case JsonTokenType.String:
                {
                    string? name = Take(names, index, ref pending);
                    if (reader.ValueIsEscaped || reader.HasValueSequence)
                    {
                        string s = reader.GetString() ?? "";
                        byte[] b = Encoding.UTF8.GetBytes(s);
                        if (IsFormKey(b)) Emit(ref buf, names, name, b, ref count);
                    }
                    else if (IsFormKey(reader.ValueSpan)) Emit(ref buf, names, name, reader.ValueSpan, ref count);
                    continue;
                }
                default:
                    Take(names, index, ref pending);
                    continue;
            }
        }
        return buf?.WrittenSpan.ToArray();
    }

    /// <summary>The path piece of the value that starts here: its property name, or its place in the array it is in.</summary>
    static string? Take(List<string?> names, List<int> index, ref string? pending)
    {
        if (pending != null) { string p = pending; pending = null; return p; }
        if (index.Count > 0 && index[^1] >= 0) { int i = index[^1]; index[^1] = i + 1; return "[" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]"; }
        return null;
    }

    static void Emit(ref ArrayBufferWriter<byte>? buf, List<string?> names, string? leaf, ReadOnlySpan<byte> key, ref int count)
    {
        // The record's own FormKey, and the markers of nested records
        if (names.Count == 1 && leaf == "FormKey") return;
        if (leaf == RecordSplit.ChildKey) return;
        buf ??= new ArrayBufferWriter<byte>(256);
        var sb = new StringBuilder(64);
        for (int i = 1; i < names.Count; i++) Append(sb, names[i]);
        Append(sb, leaf);
        int n = Encoding.UTF8.GetByteCount(sb.ToString());
        var span = buf.GetSpan(n + key.Length + 2);
        Encoding.UTF8.GetBytes(sb.ToString(), span);
        span[n] = (byte)'\t';
        key.CopyTo(span[(n + 1)..]);
        span[n + 1 + key.Length] = (byte)'\n';
        buf.Advance(n + key.Length + 2);
        count++;
    }

    static void Append(StringBuilder sb, string? piece)
    {
        if (piece == null) return;
        if (piece[0] != '[' && sb.Length > 0) sb.Append('.');
        sb.Append(piece);
    }

    /// <summary>One line of a record's links, taken apart: the field with every list position as <c>[*]</c> (and a
    /// last <c>[*]</c> dropped: Keywords), the innermost list position, the exact path, the target.</summary>
    public static IEnumerable<(string Field, int? Idx, string Path, string ToKey)> Lines(byte[] links)
    {
        string text = Encoding.UTF8.GetString(links);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int tab = line.IndexOf('\t');
            string path = line[..tab], to = line[(tab + 1)..];
            int? idx = null;
            var field = new StringBuilder(path.Length);
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] != '[') { field.Append(path[i]); continue; }
                int e = path.IndexOf(']', i);
                idx = int.Parse(path.AsSpan(i + 1, e - i - 1), System.Globalization.CultureInfo.InvariantCulture);
                field.Append("[*]");
                i = e;
            }
            string f = field.ToString();
            if (f.EndsWith("[*]", StringComparison.Ordinal)) f = f[..^3];
            yield return (f, idx, path, to);
        }
    }
}

/// <summary>A reading thread's own SQLite, in memory: it turns JSON text into JSONB, and evaluates the typed
/// view's own column expressions over one record when only named columns are read.</summary>
/// <remarks>The two statements are run through SQLite's own calls, like <see cref="BulkInsert"/>: they run once
/// for every record, and Microsoft.Data.Sqlite's command and reader objects cost more than the statements do.</remarks>
sealed class JsonbMaker : IDisposable
{
    readonly SqliteConnection _con;
    readonly sqlite3 _db;
    readonly sqlite3_stmt _jsonb;
    sqlite3_stmt? _project;
    string? _projectSql;

    public JsonbMaker(Action<SqliteConnection> registerFunctions)
    {
        _con = new SqliteConnection("Data Source=:memory:;Pooling=False");
        _con.Open();
        registerFunctions(_con);
        _db = _con.Handle ?? throw new InvalidOperationException("the connection isn't open");
        _jsonb = Prepare("SELECT jsonb(CAST($j AS TEXT))");
    }

    sqlite3_stmt Prepare(string sql)
    {
        int rc = raw.sqlite3_prepare_v2(_db, sql, out var stmt);
        if (rc != raw.SQLITE_OK) throw Error(rc);
        return stmt;
    }

    /// <summary>The error as Microsoft.Data.Sqlite words it.</summary>
    SqliteException Error(int rc) => new($"SQLite Error {rc}: '{raw.sqlite3_errmsg(_db).utf8_to_string()}'.", rc, raw.sqlite3_extended_errcode(_db));

    public byte[] ToJsonb(ReadOnlySpan<byte> utf8)
    {
        raw.sqlite3_bind_blob(_jsonb, 1, utf8);
        try
        {
            int rc = raw.sqlite3_step(_jsonb);
            if (rc != raw.SQLITE_ROW) throw Error(rc);
            return raw.sqlite3_column_blob(_jsonb, 0).ToArray();
        }
        finally { raw.sqlite3_reset(_jsonb); }
    }

    /// <summary>The values of the given expressions (written over a column named Json) for one record's JSON.</summary>
    public object?[] Project(ReadOnlySpan<byte> utf8, string sql, int columns)
    {
        if (!ReferenceEquals(_projectSql, sql) && _projectSql != sql)
        {
            _project?.Dispose();
            _project = Prepare(sql);
            _projectSql = sql;
        }
        var stmt = _project!;
        // The JSON becomes JSONB once, as it would be stored, and the expressions read that
        raw.sqlite3_bind_blob(_jsonb, 1, utf8);
        try
        {
            int rc = raw.sqlite3_step(_jsonb);
            if (rc != raw.SQLITE_ROW) throw Error(rc);
            raw.sqlite3_bind_blob(stmt, 1, raw.sqlite3_column_blob(_jsonb, 0));
        }
        finally { raw.sqlite3_reset(_jsonb); }
        try
        {
            var values = new object?[columns];
            int rc = raw.sqlite3_step(stmt);
            if (rc == raw.SQLITE_ROW)
                for (int i = 0; i < columns; i++) values[i] = Value(stmt, i);
            else if (rc != raw.SQLITE_DONE) throw Error(rc);
            return values;
        }
        finally { raw.sqlite3_reset(stmt); }
    }

    /// <summary>A column's value as Microsoft.Data.Sqlite's reader gives it: integer, real, text, bytes or nothing.</summary>
    static object? Value(sqlite3_stmt stmt, int i) => raw.sqlite3_column_type(stmt, i) switch
    {
        raw.SQLITE_NULL => null,
        raw.SQLITE_INTEGER => raw.sqlite3_column_int64(stmt, i),
        raw.SQLITE_FLOAT => raw.sqlite3_column_double(stmt, i),
        raw.SQLITE_TEXT => raw.sqlite3_column_text(stmt, i).utf8_to_string(),
        _ => raw.sqlite3_column_blob(stmt, i).ToArray(),
    };

    /// <summary>The statement <see cref="Project"/> runs: the expressions over one value named Json, which is the
    /// record's JSONB. (It was one statement that made the JSONB in a materialized common table expression: SQLite
    /// then made a table in memory for every record, which cost more than the expressions.)</summary>
    public static string ProjectSql(IEnumerable<string> exprs) =>
        "SELECT " + string.Join(", ", exprs) + " FROM (SELECT $j AS Json)";

    public void Dispose()
    {
        _jsonb.Dispose(); _project?.Dispose(); _con.Dispose();
    }
}

/// <summary>What to read of each record.</summary>
sealed class ReadPlan
{
    /// <summary>The whole record as JSONB, with its links.</summary>
    public bool Full = true;
    /// <summary>With <see cref="Full"/> off: the statement that gives the named columns (null: none are needed, so
    /// nothing is read through Mutagen and the rows come from the header index alone).</summary>
    public string? ProjectSql;
    public int ProjectColumns;
    /// <summary>With <see cref="Full"/> off: the columns read straight from the Mutagen object (DirectColumns.cs), by
    /// their place in the row; null at a place the JSON gives (<see cref="ProjectSql"/> has those, in order). When every
    /// column is direct, no JSON is made at all.</summary>
    public DirectColumn?[]? Direct;
    /// <summary>Mark each row with whether it is the version the game uses.</summary>
    public bool Winners;
}

sealed class RecordReader : IDisposable
{
    readonly RecordEngine _engine;
    readonly int _threads;
    readonly Action<SqliteConnection> _registerFunctions;
    readonly ConcurrentBag<JsonbMaker> _makers = new();
    readonly ThreadLocal<JsonbMaker> _maker;

    /// <summary>Stopwatch ticks spent on the reading threads, by step: reading through Mutagen, to JSON, in SQLite (JSONB or the named columns).</summary>
    public static long TicksLoad, TicksJson, TicksSql;
    /// <summary>Stopwatch ticks the inserting thread spent waiting for the reading threads' next batch. The rest of a
    /// read's time is the inserting thread's own work.</summary>
    public static long TicksWait;

    public RecordReader(RecordEngine engine, int threads, int directReaders, Action<SqliteConnection> registerFunctions)
    {
        _engine = engine; _threads = Math.Max(1, threads); _registerFunctions = registerFunctions;
        _directGate = directReaders > 0 && directReaders < _threads ? new SemaphoreSlim(directReaders) : null;
        _maker = new ThreadLocal<JsonbMaker>(() => { var m = new JsonbMaker(_registerFunctions); _makers.Add(m); return m; });
    }

    public void Dispose()
    {
        foreach (var m in _makers) m.Dispose();
        _directGate?.Dispose();
        _maker.Dispose();
    }

    /// <summary>A plugin's records of one signature in file order, as batches.</summary>
    public IEnumerable<List<ReadRow>> Read(IndexedPlugin plugin, string signature, ReadPlan plan, CancellationToken cancel)
    {
        foreach (var batch in ReadEntries(plugin, plugin.Segment!.EntriesOf(signature).ToArray(), plan, cancel))
            yield return batch;
    }

    /// <summary>Some records of a plugin, by their entries in its header index, in file order, as batches: what a query
    /// narrowed by FormKey reads (Session.Keyed.cs).</summary>
    public IEnumerable<List<ReadRow>> ReadEntries(IndexedPlugin plugin, int[] entries, ReadPlan plan, CancellationToken cancel)
    {
        var seg = plugin.Segment!;
        if (entries.Length == 0) yield break;
        var offsets = new long[entries.Length];
        for (int i = 0; i < entries.Length; i++) offsets[i] = seg[entries[i]].Offset;
        Array.Sort(offsets, entries);
        // The plugin is opened on this thread, before any reading thread asks for it
        if (plan.Full || plan.ProjectSql != null || plan.Direct != null) _engine.Sessions.Get(plugin.Name);

        foreach (var batch in OrderedParallel(Batches(seg, entries), b => Process(plugin, b, plan), _threads, cancel))
            yield return batch;
    }


    /// <summary>Every record of a plugin as the header index has it, in file order: what the Forms table is made of.
    /// No record is read.</summary>
    public IEnumerable<List<ReadRow>> ReadIndex(IndexedPlugin plugin, bool winners, CancellationToken cancel)
    {
        var seg = plugin.Segment!;
        int n = seg.Count;
        if (n == 0) yield break;
        var entries = new int[n]; var offsets = new long[n];
        for (int i = 0; i < n; i++) { entries[i] = i; offsets[i] = seg[i].Offset; }
        Array.Sort(offsets, entries);
        var index = _engine.Index;
        IEnumerable<ArraySegment<int>> Pieces() { for (int i = 0; i < n; i += 8192) yield return new ArraySegment<int>(entries, i, Math.Min(8192, n - i)); }
        foreach (var batch in OrderedParallel(Pieces(), piece =>
        {
            var rows = new List<ReadRow>(piece.Count);
            foreach (int entry in piece)
            {
                var r = new RecordRef(plugin.Id, entry);
                var info = index.Describe(r);
                rows.Add(new ReadRow { FormKey = info.FormKey, EditorId = info.EditorId, IsOverride = info.IsOverride, Pos = info.Offset, Parent = info.Parent, Sig = info.Signature, IsWinner = winners && index.IsWinner(r) });
            }
            return rows;
        }, _threads, cancel))
            yield return batch;
    }
    static IEnumerable<ArraySegment<int>> Batches(Segment seg, int[] entries)
    {
        // A batch is at most 512 records and about 4 MB of record data. The header index has a compressed record's
        // stored size only, so it counts sixteen times: a batch's rows wait in memory for the inserting thread, and
        // 70 surface trees (57 KB each as stored, 3 MB each as JSONB) in one batch were 200 MB a batch.
        const int maxCount = 512; const long maxBytes = 4 << 20; const int inflates = 16;
        int start = 0; long bytes = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            var entry = seg[entries[i]];
            bytes += entry.IsCompressed ? (long)entry.Size * inflates : entry.Size;
            if (i - start + 1 >= maxCount || bytes >= maxBytes)
            {
                yield return new ArraySegment<int>(entries, start, i - start + 1);
                start = i + 1; bytes = 0;
            }
        }
        if (start < entries.Length) yield return new ArraySegment<int>(entries, start, entries.Length - start);
    }

    /// <summary>
    /// A host's own limit on how many threads read records straight from their own bytes at once
    /// (QueryOptions.DirectReaders). Null: none, every reading thread does. There was a limit of two here while those
    /// reads went through one file handle, which Windows lets one thread at a time read through. Each reading thread
    /// now has a handle and a window of its own (PluginSession.ReadAt), and the reads scale with the threads:
    /// design\agent-first\notes\perf-reads.md has the numbers.
    /// </summary>
    readonly SemaphoreSlim? _directGate;

    List<ReadRow> Process(IndexedPlugin plugin, ArraySegment<int> batch, ReadPlan plan)
    {
        var rows = new List<ReadRow>(batch.Count);
        var index = _engine.Index;
        bool read = plan.Full || plan.ProjectSql != null || plan.Direct != null;
        var maker = read ? _maker.Value! : null;
        // Each record is read, turned into JSON and let go before the next one is read: a record that lives no
        // longer than that costs the garbage collector nothing. Only under a host's limit on direct readers are a
        // batch's records read first and turned into JSON after the turn has been given up.
        LoadedRecord?[]? held = null;
        bool gated = false;
        var took = new Took();
        try
        {
            for (int i = 0; i < batch.Count; i++)
            {
                var r = new RecordRef(plugin.Id, batch.Array![batch.Offset + i]);
                var info = index.Describe(r);
                // The parent isn't kept: only the Forms table has it, and that is made by ReadIndex
                var row = new ReadRow { FormKey = info.FormKey, EditorId = info.EditorId, IsOverride = info.IsOverride, Pos = info.Offset };
                if (plan.Winners) row.IsWinner = index.IsWinner(r);
                rows.Add(row);
                if (!read) continue;
                if (info.Unresolved) { row.Skip = "its FormID can't be resolved: a master of the plugin is missing"; continue; }
                if (i == 0 && _directGate != null && _engine.IsDirect(info)) { _directGate.Wait(); gated = true; held = new LoadedRecord?[batch.Count]; }
                LoadedRecord? rec = null;
                long a = System.Diagnostics.Stopwatch.GetTimestamp();
                try { rec = _engine.Load(r, info); }
                catch (RecordException e) { row.Skip = e.Message; }
                catch (Exception e) when (e is not OperationCanceledException) { row.Skip = Failure(e); }
                took.Load += System.Diagnostics.Stopwatch.GetTimestamp() - a;
                if (rec == null) continue;
                if (held != null) held[i] = rec;
                else Finish(rec, row, plan, maker!, ref took);
            }
        }
        finally { if (gated) _directGate!.Release(); }
        if (held != null)
            for (int i = 0; i < held.Length; i++)
                if (held[i] is { } rec) { Finish(rec, rows[i], plan, maker!, ref took); held[i] = null; }
        if (read) { Interlocked.Add(ref TicksLoad, took.Load); Interlocked.Add(ref TicksJson, took.Json); Interlocked.Add(ref TicksSql, took.Sql); }
        return rows;
    }

    /// <summary>Stopwatch ticks of one batch, by step.</summary>
    struct Took { public long Load, Json, Sql; }

    /// <summary>A record that was read becomes its row: its JSON, then its links and JSONB, or the named columns.</summary>
    void Finish(LoadedRecord rec, ReadRow row, ReadPlan plan, JsonbMaker maker, ref Took took)
    {
        try
        {
            long a = System.Diagnostics.Stopwatch.GetTimestamp();
            row.Type = rec.Type;
            if (!plan.Full && plan.Direct != null && plan.ProjectSql == null)
            {
                // Every column straight from the object: no JSON
                var direct = new object?[plan.Direct.Length];
                for (int i = 0; i < direct.Length; i++) direct[i] = plan.Direct[i]!.Read(rec.Record);
                row.Values = direct;
                took.Sql += System.Diagnostics.Stopwatch.GetTimestamp() - a;
                return;
            }
            // A record that holds others has its children taken out of its JSON first. Every other record's JSON
            // comes as the serializer's own UTF-8, in memory the serializer writes again for the next record.
            ReadOnlySpan<byte> utf8 = _engine.Schema.HoldsRecords(rec.Type)
                ? Encoding.UTF8.GetBytes(_engine.Json(rec).ToJsonString())
                : RecordSerializer.ToUtf8(rec.Record, rec.Type);
            row.JsonBytes = utf8.Length;
            long b = System.Diagnostics.Stopwatch.GetTimestamp();
            if (plan.Full)
            {
                row.Links = LinkWalker.Walk(utf8, out _);
                row.Jsonb = maker.ToJsonb(utf8);
            }
            else if (plan.Direct == null) row.Values = maker.Project(utf8, plan.ProjectSql!, plan.ProjectColumns);
            else
            {
                // Some columns from the JSON, the rest straight from the object
                var fromJson = maker.Project(utf8, plan.ProjectSql!, plan.ProjectColumns);
                var values = new object?[plan.Direct.Length];
                for (int i = 0, j = 0; i < values.Length; i++) values[i] = plan.Direct[i] is { } d ? d.Read(rec.Record) : fromJson[j++];
                row.Values = values;
            }
            took.Json += b - a; took.Sql += System.Diagnostics.Stopwatch.GetTimestamp() - b;
        }
        catch (RecordException e) { row.Skip = e.Message; }
        catch (Exception e) when (e is not OperationCanceledException) { row.Skip = Failure(e); }
    }

    /// <summary>Mutagen couldn't read or write a record: it is left out of the table, and the answer says so.</summary>
    static string Failure(Exception e)
    {
        var inner = e is System.Reflection.TargetInvocationException { InnerException: { } ie } ? ie : e;
        return $"Mutagen couldn't read it: {inner.GetType().Name}: {inner.Message}";
    }

    /// <summary>Runs <paramref name="work"/> on up to <paramref name="threads"/> threads and gives the results in the
    /// order of the items. At most twice that many results wait for the consumer, so memory stays bounded.</summary>
    static IEnumerable<TOut> OrderedParallel<TIn, TOut>(IEnumerable<TIn> items, Func<TIn, TOut> work, int threads, CancellationToken cancel)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        using var queue = new BlockingCollection<Task<TOut>>(threads * 2);
        using var slots = new SemaphoreSlim(threads);
        var producer = Task.Run(() =>
        {
            try
            {
                foreach (var item in items)
                {
                    slots.Wait(stop.Token);
                    var it = item;
                    var task = Task.Run(() => { try { return work(it); } finally { try { slots.Release(); } catch (ObjectDisposedException) { } } });
                    try { queue.Add(task, stop.Token); }
                    catch (OperationCanceledException) { try { task.Wait(); } catch (AggregateException) { } throw; }
                }
            }
            catch (OperationCanceledException) { }
            finally { queue.CompleteAdding(); }
        });
        try
        {
            long waitFrom = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var task in queue.GetConsumingEnumerable())
            {
                cancel.ThrowIfCancellationRequested();
                var result = task.GetAwaiter().GetResult();
                Interlocked.Add(ref TicksWait, System.Diagnostics.Stopwatch.GetTimestamp() - waitFrom);
                yield return result;
                waitFrom = System.Diagnostics.Stopwatch.GetTimestamp();
            }
        }
        finally
        {
            stop.Cancel();
            try { producer.Wait(); } catch (AggregateException) { }
            // Let the tasks still running finish before their semaphore goes away
            while (queue.TryTake(out var left)) { try { left.Wait(); } catch (AggregateException) { } }
        }
    }
}
