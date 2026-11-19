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
    /// <summary>For the Forms table: the record's bytes after its header as stored, as read (inflated), and whether
    /// it is compressed (GAPS S81).</summary>
    public long Size, DataSize;
    public bool Compressed;
    /// <summary>For the Forms table: the record header's flags as stored, bytes 8-11 (GAPS S108).</summary>
    public uint Flags;
    public byte[]? Jsonb;
    /// <summary>The record's links as lines of "path TAB FormKey", UTF-8.</summary>
    public byte[]? Links;
    public int JsonBytes;
    /// <summary>Why the record isn't in the table, when it couldn't be read.</summary>
    public string? Skip;
    /// <summary>The projected columns, when only named columns were read.</summary>
    public object?[]? Values;
    /// <summary>A list view's rows of the record (its list places, then its columns), when a list's named columns were read.</summary>
    public List<object?[]>? ListRows;
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
    sqlite3_stmt _jsonb;
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
        finally { raw.sqlite3_reset(_jsonb); if (utf8.Length > ForgetBytes) Forget(); }
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
        finally { raw.sqlite3_reset(stmt); if (utf8.Length > ForgetBytes) Forget(); }
    }

    /// <summary>Every row a statement (written over a value named Json) gives for one record's JSON: a list view's rows
    /// of the record (ViewBuilder.ListProjectSql, GAPS S58).</summary>
    public List<object?[]> ProjectRows(ReadOnlySpan<byte> utf8, string sql, int columns)
    {
        if (!ReferenceEquals(_projectSql, sql) && _projectSql != sql)
        {
            _project?.Dispose();
            _project = Prepare(sql);
            _projectSql = sql;
        }
        var stmt = _project!;
        raw.sqlite3_bind_blob(_jsonb, 1, utf8);
        try
        {
            int rc = raw.sqlite3_step(_jsonb);
            if (rc != raw.SQLITE_ROW) throw Error(rc);
            raw.sqlite3_bind_blob(stmt, 1, raw.sqlite3_column_blob(_jsonb, 0));
        }
        finally { raw.sqlite3_reset(_jsonb); }
        var rows = new List<object?[]>();
        try
        {
            while (true)
            {
                int rc = raw.sqlite3_step(stmt);
                if (rc == raw.SQLITE_DONE) break;
                if (rc != raw.SQLITE_ROW) throw Error(rc);
                var values = new object?[columns];
                for (int i = 0; i < columns; i++) values[i] = Value(stmt, i);
                rows.Add(values);
            }
            return rows;
        }
        finally { raw.sqlite3_reset(stmt); if (utf8.Length > ForgetBytes) Forget(); }
    }

    /// <summary>A record's JSON over this size isn't kept after it is read (see <see cref="Forget"/>).</summary>
    const int ForgetBytes = 1 << 20;

    /// <summary>
    /// SQLite keeps the last few JSON values each statement parsed, for as long as the statement lives (its JSON
    /// cache): after a cell with 6.8 MB of JSON, a reading thread held about 60 MB until it was given bigger ones. The
    /// statements are made again, which lets that go.
    /// </summary>
    void Forget()
    {
        _jsonb.Dispose();
        _jsonb = Prepare("SELECT jsonb(CAST($j AS TEXT))");
        _project?.Dispose();
        _project = null; _projectSql = null;
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
    /// <summary>With <see cref="Full"/> off: a list view's named columns, straight from the object (GAPS S45).</summary>
    public DirectList? List;
    /// <summary>With <see cref="List"/>: the same columns from the record's own bytes (RawLists.cs, GAPS S123). A record
    /// the bytes can't answer is read through Mutagen with <see cref="List"/>.</summary>
    public RawList? RawList;
    /// <summary>With <see cref="Full"/> off: a list view's rows taken out of each record's JSON, the record's JSON let
    /// go at once (ViewBuilder.ListProjectSql, GAPS S58), and how many values a row has (its places, then its columns).</summary>
    public string? ListSql;
    public int ListValues;
    /// <summary>With <see cref="Full"/> off: no field the JSON is read for can hold a nested record, so a record that
    /// holds others needn't have them split out of its JSON first (RecordSchema.FieldHoldsRecords): the serializer's own
    /// UTF-8 is read, with no parsed copy of the whole record (a cell with 6.8 MB of JSON took about 100 MB parsed).</summary>
    public bool Unsplit;

    /// <summary>With <see cref="Full"/> off and every column direct: the columns read from the record's own bytes
    /// (RawColumns.cs, J30), in the same order as <see cref="Direct"/>. A record the bytes can't answer is read through
    /// Mutagen with <see cref="Direct"/>.</summary>
    public RawRecordColumns? Raw;
    /// <summary>The query's language, for translated texts read from the records' own bytes (<see cref="Raw"/>,
    /// <see cref="RawList"/>; GAPS S152). Null: such a text is read through Mutagen.</summary>
    public string? TextLanguage;
    /// <summary>With <see cref="Full"/> off: only the records whose named columns pass it give a row (a statement's
    /// <c>col = literal</c>, applied while reading). Records that couldn't be read still give their row, with
    /// <see cref="ReadRow.Skip"/>.</summary>
    public RowFilter? Filter;

    /// <summary>With <see cref="Full"/> off, for a record type that shares its signature with others (a game setting's,
    /// an object modification's): the type whose records give rows; a record of another type of the signature gives
    /// none. Null for a type with a signature of its own.</summary>
    public string? OnlyType;
    /// <summary>With <see cref="OnlyType"/>: which type a record is, from its own bytes (SFSearch.Records.RecordClasses);
    /// null from it, or with none, Mutagen's read of the record says.</summary>
    public Func<RawRecord, string?>? ClassOf;
    /// <summary>Counted while reading: the records of a read from the records' own bytes (<see cref="Raw"/>,
    /// <see cref="RawList"/>) that were read through Mutagen instead, because their bytes alone don't say the answer.
    /// The read's cost line says so.</summary>
    public long ViaMutagen;

    /// <summary>True when the records are opened through Mutagen (not the header index alone).</summary>
    public bool Reads => Full || ProjectSql != null || Direct != null || List != null || ListSql != null;
}

/// <summary>
/// A statement's <c>col = 'literal'</c> (or <c>IN (…)</c>) on a column it reads, applied while the records are read
/// (J30): only the matching rows are inserted (2,648 of Starfield.esm's 3.29 million placed objects for one base).
/// The values compare as SQLite compares them in the column's table: a link column with NOCASE (ASCII letters folded),
/// any other column as it is; a text never equals a number, numbers by their value.
/// </summary>
sealed class RowFilter
{
    public required int Column { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<object> Literals { get; init; }
    public bool NoCase { get; init; }

    public bool Accept(object? value)
    {
        if (value == null) return false;
        foreach (var lit in Literals)
            switch (lit, value)
            {
                case (string a, string b) when NoCase ? AsciiNoCase(a, b) : string.Equals(a, b, StringComparison.Ordinal): return true;
                case (long a, long b) when a == b: return true;
                case (long a, double b) when a == b: return true;
                case (double a, long b) when a == b: return true;
                case (double a, double b) when a == b: return true;
            }
        return false;
    }

    /// <summary>SQLite's NOCASE: equal once A to Z are folded to a to z, nothing else folded.</summary>
    static bool AsciiNoCase(string a, string b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            char x = a[i], y = b[i];
            if (x == y) continue;
            if (x is >= 'A' and <= 'Z') x = (char)(x + 32);
            if (y is >= 'A' and <= 'Z') y = (char)(y + 32);
            if (x != y) return false;
        }
        return true;
    }
}


sealed class RecordReader : IDisposable
{
    readonly RecordEngine _engine;
    readonly int _threads;
    readonly Action<SqliteConnection> _registerFunctions;
    readonly ConcurrentBag<JsonbMaker> _makers = new();
    readonly ThreadLocal<JsonbMaker> _maker;

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

    /// <summary>A plugin's records of one signature in file order.</summary>
    public IEnumerable<ReadRow> Read(IndexedPlugin plugin, string signature, ReadPlan plan, CancellationToken cancel) =>
        ReadEntries(plugin, plugin.Segment!.EntriesOf(signature).ToArray(), plan, cancel);

    /// <summary>
    /// Some records of a plugin, by their entries in its header index, in file order: what a query narrowed by FormKey
    /// reads (Session.Keyed.cs). Read on the shared record pipeline (SFSearch.Index\RecordPipeline.cs, J35): the
    /// records' own bytes, inflated, when every column is read from them (RawColumns.cs); otherwise each record's
    /// place, and the record read through Mutagen.
    /// </summary>
    public IEnumerable<ReadRow> ReadEntries(IndexedPlugin plugin, int[] entries, ReadPlan plan, CancellationToken cancel)
    {
        var seg = plugin.Segment!;
        if (entries.Length == 0) yield break;
        // File order. With a filter only the rows it keeps are put in that order, after the read: sorting 3.29 million
        // placed objects' places first took about a third of a read that keeps 2,648 of them
        if (plan.Filter == null)
        {
            var offsets = new long[entries.Length];
            for (int i = 0; i < entries.Length; i++) offsets[i] = seg[entries[i]].Offset;
            entries = (int[])entries.Clone();
            Array.Sort(offsets, entries);
        }
        // The plugin is opened on this thread, before any reading thread asks for it
        if (plan.Reads) _engine.Sessions.Get(plugin.Name);
        bool raw = !plan.Full && (plan.Raw != null || plan.List != null && plan.RawList != null);
        bool json = plan.Full || plan.ProjectSql != null || plan.ListSql != null;
        var rawPlugin = raw ? new RawPlugin(plugin, plan.Reads ? _engine.Sessions.Get(plugin.Name) : null, plan.TextLanguage) : null;
        var types = new string?[seg.Signatures.Count];
        var index = _engine.Index;
        var options = new PipelineOptions
        {
            Threads = _threads, Cancel = cancel,
            Data = raw || plan.OnlyType != null && plan.ClassOf != null ? PipelineData.Plain : PipelineData.None,
            CostPerByte = plan.Reads && !raw ? PipelineOptions.MutagenCostPerByte : 1,
            // A record's JSON is many times its bytes, and every finished piece holds its rows until they are
            // inserted: small pieces and few ahead (a surface tree is 57 KB stored and 3 MB of JSONB)
            // A list read by the value walk costs far more per record than a read at fixed places: smaller pieces keep
            // every thread busy to the end
            PieceRecords = raw && plan.RawList?.Walks != true ? 4096 : 512,
            PieceBytes = json ? 256 << 10 : 4 << 20,
            Ahead = json ? 2 * _threads : 0,
        };
        var rows = RecordPipeline.Read<ReadRow>(new[] { new ReadSet { Plugin = plugin, Entries = entries } }, (in PipelineRecord r, List<ReadRow> output) =>
        {
            // A signature several record types share: only the plan's type's records give rows. Which type a record is,
            // its bytes say (RecordClasses); where they don't, Mutagen's read does (Process)
            bool mine = true;
            if (plan.OnlyType != null)
            {
                string? cls = plan.ClassOf != null && r.Problem == PipelineProblem.None ? plan.ClassOf(RawRecord.FromInflated(r.Head, r.Data)) : null;
                if (cls != null && cls != plan.OnlyType) return;
                mine = cls != null;
            }
            if (!mine) { if (raw) Interlocked.Increment(ref plan.ViaMutagen); if (Process(r.Ref, plan) is { } read) output.Add(read); return; }
            // (a row from the header index alone, of a record the bytes say is the type's)
            if (plan.OnlyType != null && !plan.Reads && !raw)
            {
                var e = r.Header;
                var row = new ReadRow { FormKey = index.FormKeyOf(r.Ref), EditorId = seg.EditorId(r.EntryIndex), IsOverride = e.Owner < seg.SelfOwner, Pos = e.Offset };
                if (plan.Winners) row.IsWinner = index.IsWinner(r.Ref);
                if (e.IsUnresolved) row.Skip = "its FormID can't be resolved: a master of the plugin is missing";
                else row.Type = plan.OnlyType;
                output.Add(row);
                return;
            }
            if (raw && r.Problem == PipelineProblem.None && plan.RawList != null)
            {
                var listRows = new List<object?[]>();
                if (plan.RawList.Read(r.Head, r.Data, rawPlugin!, listRows))
                {
                    var e = r.Header;
                    var row = new ReadRow { FormKey = index.FormKeyOf(r.Ref), EditorId = seg.EditorId(r.EntryIndex), IsOverride = e.Owner < seg.SelfOwner, Pos = e.Offset };
                    if (plan.Winners) row.IsWinner = index.IsWinner(r.Ref);
                    if (e.IsUnresolved) row.Skip = "its FormID can't be resolved: a master of the plugin is missing";
                    else { row.Type = plan.OnlyType ?? (types[e.Signature] ??= _engine.TypeFor(seg.SignatureOf(e))); row.ListRows = listRows; }
                    output.Add(row);
                    return;
                }
            }
            else if (raw && r.Problem == PipelineProblem.None)
            {
                var values = new object?[plan.Raw!.Count];
                if (plan.Raw.Read(r.Head, r.Data, rawPlugin!, values))
                {
                    if (plan.Filter != null && !plan.Filter.Accept(values[plan.Filter.Column])) return;
                    // The header index's line without what this row doesn't keep (Describe's parent, sizes, plugin): every
                    // object made per row here is the garbage collector's, and its pauses stop the one inserting thread too
                    var e = r.Header;
                    var row = new ReadRow { FormKey = index.FormKeyOf(r.Ref), EditorId = seg.EditorId(r.EntryIndex), IsOverride = e.Owner < seg.SelfOwner, Pos = e.Offset };
                    if (plan.Winners) row.IsWinner = index.IsWinner(r.Ref);
                    if (e.IsUnresolved) row.Skip = "its FormID can't be resolved: a master of the plugin is missing";
                    else { row.Type = plan.OnlyType ?? (types[e.Signature] ??= _engine.TypeFor(seg.SignatureOf(e))); row.Values = values; }
                    output.Add(row);
                    return;
                }
            }
            if (raw) Interlocked.Increment(ref plan.ViaMutagen);
            if (Process(r.Ref, plan) is { } made) output.Add(made);
        }, options);
        if (plan.Filter != null) rows = rows.ToList().OrderBy(x => x.Pos);
        foreach (var row in rows) yield return row;
    }

    /// <summary>Every record of a plugin as the header index has it, in file order: what the Forms table is made of.
    /// No record is read.</summary>
    public IEnumerable<ReadRow> ReadIndex(IndexedPlugin plugin, bool winners, CancellationToken cancel, int[]? only = null)
    {
        var seg = plugin.Segment!;
        int n = only?.Length ?? seg.Count;
        if (n == 0) yield break;
        // Every record, or only the given ones (Forms narrowed by FormKey), in file order
        var entries = new int[n]; var offsets = new long[n];
        for (int i = 0; i < n; i++) { entries[i] = only?[i] ?? i; offsets[i] = seg[entries[i]].Offset; }
        Array.Sort(offsets, entries);
        var index = _engine.Index;
        foreach (var row in RecordPipeline.Read<ReadRow>(new[] { new ReadSet { Plugin = plugin, Entries = entries } }, (in PipelineRecord p, List<ReadRow> output) =>
        {
            var r = p.Ref;
            var info = index.Describe(r);
            // A compressed record's first four bytes are its size inflated
            bool zipped = p.Header.IsCompressed;
            long data = zipped && p.Data.Length >= 4 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Data) : p.Header.Size;
            output.Add(new ReadRow { FormKey = info.FormKey, EditorId = info.EditorId, IsOverride = info.IsOverride, Pos = info.Offset, Parent = info.Parent, Sig = info.Signature, IsWinner = winners && index.IsWinner(r),
                Size = p.Header.Size, DataSize = data, Compressed = zipped, Flags = p.Header.Flags });
        }, new PipelineOptions { Threads = _threads, Cancel = cancel, Data = PipelineData.Stored, PieceRecords = 8192, PieceBytes = long.MaxValue }))
            yield return row;
    }

    /// <summary>
    /// A host's own limit on how many threads read records straight from their own bytes through Mutagen at once
    /// (QueryOptions.DirectReaders). Null: none, every reading thread does. There was a limit of two here while those
    /// reads went through one file handle, which Windows lets one thread at a time read through. Each reading thread
    /// now has a handle and a window of its own (PluginSession.ReadAt), and the reads scale with the threads:
    /// design\agent-first\notes\perf-reads.md has the numbers.
    /// </summary>
    readonly SemaphoreSlim? _directGate;

    /// <summary>One record read through Mutagen (or from the header index alone), made into its row; null when the
    /// plan's filter leaves it out. Each record is read, turned into JSON and let go before the next one is read: a
    /// record that lives no longer than that costs the garbage collector nothing.</summary>
    ReadRow? Process(RecordRef r, ReadPlan plan)
    {
        var index = _engine.Index;
        var info = index.Describe(r);
        var row = new ReadRow { FormKey = info.FormKey, EditorId = info.EditorId, IsOverride = info.IsOverride, Pos = info.Offset };
        if (plan.Winners) row.IsWinner = index.IsWinner(r);
        // (a type that shares its signature: the record is opened even for a row of the header index alone, to know its type)
        if (!plan.Reads && plan.OnlyType == null) return row;
        if (info.Unresolved) { row.Skip = "its FormID can't be resolved: a master of the plugin is missing"; return row; }
        LoadedRecord? rec = null;
        bool gated = _directGate != null && _engine.IsDirect(info);
        if (gated) _directGate!.Wait();
        try { rec = _engine.Load(r, info); }
        catch (RecordException e) { row.Skip = e.Message; }
        catch (Exception e) when (e is not OperationCanceledException) { row.Skip = Failure(e); }
        finally { if (gated) _directGate!.Release(); }
        // A record of another type of the signature isn't a row of this read
        if (rec != null && plan.OnlyType != null && rec.Type != plan.OnlyType) return null;
        if (rec != null && plan.Reads) Finish(rec, row, plan, _maker.Value!);
        else if (rec != null) row.Type = rec.Type;
        if (row.Skip == null && plan.Filter != null && row.Values != null && !plan.Filter.Accept(row.Values[plan.Filter.Column])) return null;
        return row;
    }


    /// <summary>A record that was read becomes its row: its JSON, then its links and JSONB, or the named columns.</summary>
    void Finish(LoadedRecord rec, ReadRow row, ReadPlan plan, JsonbMaker maker)
    {
        try
        {
            row.Type = rec.Type;
            DirectColumn.TextSession = rec.Session.Localized ? null : rec.Session;
            if (!plan.Full && plan.List != null)
            {
                row.ListRows = plan.List.Read(rec.Record);
                return;
            }
            if (!plan.Full && plan.Direct != null && plan.ProjectSql == null)
            {
                // Every column straight from the object: no JSON
                var direct = new object?[plan.Direct.Length];
                for (int i = 0; i < direct.Length; i++) direct[i] = plan.Direct[i]!.Read(rec.Record);
                row.Values = direct;
                return;
            }
            // A record that holds others has its children taken out of its JSON first. Every other record's JSON
            // comes as the serializer's own UTF-8, in memory the serializer writes again for the next record.
            ReadOnlySpan<byte> utf8 = _engine.Schema.HoldsRecords(rec.Type) && !(!plan.Full && plan.Unsplit)
                ? Encoding.UTF8.GetBytes(_engine.Json(rec).ToJsonString())
                : RecordSerializer.ToUtf8(rec.Record);
            // A plugin that isn't localized but ships strings tables: each tagged text says what its tables hold for it
            // (GAPS S190, TagTexts.cs)
            if (!rec.Session.Localized && utf8.IndexOf("<ID="u8) >= 0 && rec.Session.TagTexts?.WithTables(utf8) is { } withTables) utf8 = withTables;
            row.JsonBytes = utf8.Length;
            if (!plan.Full && plan.ListSql != null) row.ListRows = maker.ProjectRows(utf8, plan.ListSql, plan.ListValues);
            else if (plan.Full)
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
        }
        catch (RecordException e) { row.Skip = e.Message; }
        catch (Exception e) when (e is not OperationCanceledException) { row.Skip = Failure(e); }
        finally { DirectColumn.TextSession = null; }
    }

    /// <summary>Mutagen couldn't read or write a record: it is left out of the table, and the answer says so.</summary>
    static string Failure(Exception e)
    {
        var inner = e is System.Reflection.TargetInvocationException { InnerException: { } ie } ? ie : e;
        return $"Mutagen couldn't read it: {inner.GetType().Name}: {inner.Message}";
    }
}
