// The tests of fix g-sql (2026-11-13): GAPS S5 (the named columns of a big type read straight from the records, no
// JSON), S28 (a typed view narrowed by FormKey reads only those records) and S3 (Links WHERE ToKey with no link index
// answers from a byte search, not with an estimate). Each holds the new way against the old one or against Mutagen.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Microsoft.Data.Sqlite;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SFSearch.Index;
using SFSearch.Records;
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class GSqlTests
{
    const string SF = "Starfield.esm", KGC = "kgcdoom.esm";

    static QueryEngine Fresh(Ctx c, string folder, Func<string, QueryOptions>? options = null)
    {
        string path = Path.Combine(c.Work, folder);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        if (options == null) return c.NewEngine(folder);
        return new QueryEngine(c.Records, options(path));
    }

    static QueryAnswer Run(QueryEngine e, string sql, string plugin, bool heavy = false) =>
        e.Run(new QueryRequest { Sql = sql, Plugins = new[] { plugin }, PageRows = 1_000_000, AllowHeavy = heavy });

    // ------------------------------------------------------------------ S5: columns straight from the records
    /// <summary>
    /// Two engines read a type for its named columns only (a limit this low makes any type "too big to read whole"):
    /// one reads each column it can straight from Mutagen's object, the other every column through the record's JSON,
    /// as before. The two column tables they write must be equal, value and SQLite type, row by row and column by
    /// column. With --heavy, Starfield.esm's 3.29 million placed objects too, on columns of every kind the direct way
    /// reads, and the count with a real condition is timed both ways.
    /// </summary>
    public static string DirectColumns(Ctx c)
    {
        var said = new List<string>();
        // J30: the columns read from the records' own bytes, against Mutagen record by record
        foreach (var (plugin, type) in c.Heavy ? new[] { (KGC, "Cell"), (KGC, "PlacedObject"), (SF, "Cell"), (SF, "PlacedObject") } : new[] { (KGC, "Cell"), (KGC, "PlacedObject") })
            said.Add(RawAgainstMutagen(c, plugin, type));
        string[] RawColumns(string type) { var v = c.Engine.Views.Catalog(type).Typed; return v.Columns.Where(col => RawColumn.For(v, col, c.Schema) != null).Select(col => col.Name).ToArray(); }
        var cases = new List<(string Plugin, string Type, string[]? Columns)>
        {
            (KGC, "PlacedObject", null), (SF, "Weapon", null), (SF, "Armor", null), (SF, "Book", null), (KGC, "Npc", null),
            // Every column read from the bytes at once: the table read so, against the one read through the JSON
            (KGC, "PlacedObject", RawColumns("PlacedObject")), (KGC, "Cell", RawColumns("Cell")),
        };
        // (Starfield.esm's cells and placed objects are held against Mutagen record by record above; their tables read
        // through the JSON too took 42 s and 69 s more, past the tests' 5 minutes)
        if (c.Heavy)
            cases.Add((SF, "PlacedObject", new[]
            {
                "Base", "Emittance", "XTRV_Traversal", "MajorRecordFlagsRaw", "FormVersion", "FactionRank", "Radius", "Scale", "LevelModifier",
                "VolumeData_Unknown4", "ShipArrival_GravJumpFx", "Primitive_Bounds", "Primitive_Type", "DebugText_String", "Position", "Rotation",
            }));
        int n = 0;
        foreach (var (plugin, type, only) in cases)
        {
            n++;
            var view = c.Engine.Views.Catalog(type).Typed;
            var columns = (only ?? view.Columns.Select(col => col.Name).ToArray()).Where(view.ByName.ContainsKey).ToList();
            int direct = columns.Count(col => DirectColumn.For(view.ByName[col]) != null);
            string sql = $"SELECT count(*) FROM {type} WHERE " + string.Join(" OR ", columns.Select(col => $"{SqlText.Name(col)} IS NOT NULL"));
            var withDirect = Fresh(c, $"sql-direct-{n}-a", f => new QueryOptions { ScratchFolder = f, HeavySeconds = 0.0001 });
            var withJson = Fresh(c, $"sql-direct-{n}-b", f => new QueryOptions { ScratchFolder = f, HeavySeconds = 0.0001, DirectColumns = false });
            try
            {
                var clock = Stopwatch.StartNew();
                var a = Must(Run(withDirect, sql, plugin, heavy: true), $"{type} of {plugin}, direct");
                double secondsDirect = clock.Elapsed.TotalSeconds;
                clock.Restart();
                var b = Must(Run(withJson, sql, plugin, heavy: true), $"{type} of {plugin}, through the JSON");
                double secondsJson = clock.Elapsed.TotalSeconds;
                Check(a.Cost.Read.Count == 1 && a.Cost.Read[0].Contains("column", StringComparison.Ordinal), $"{type} of {plugin}: the named-columns route must be taken: {a.Cost.Text}");
                Check(Rows(a).SequenceEqual(Rows(b)), $"{type} of {plugin}: the counts differ: {Rows(a)[0]} direct, {Rows(b)[0]} through the JSON");
                long rows = SameTables(withDirect, withJson, c.Index.Plugin(plugin)!, type, columns);
                said.Add($"{type} of {plugin}: {rows:N0} records × {columns.Count} columns ({direct} direct) equal value for value; read in {secondsDirect:F2} s direct, {secondsJson:F2} s through the JSON");
            }
            finally { withDirect.Dispose(); withJson.Dispose(); }
        }
        if (c.Heavy)
        {
            // The count with a real condition, both ways, each on empty scratch tables: the answer and the time
            var top = Must(c.Engine.Run(new QueryRequest { Sql = "SELECT Base FROM PlacedObject WHERE Base IS NOT NULL GROUP BY Base ORDER BY count(*) DESC LIMIT 1", Plugins = new[] { SF }, AllowHeavy = true }), "the most placed base");
            string sql = $"SELECT count(*) FROM PlacedObject WHERE Base = '{top.Rows[0][0]}'";
            var times = new List<string>();
            object? answer = null;
            foreach (bool on in new[] { true, false, true, false })
            {
                using var e = Fresh(c, "sql-direct-count", f => new QueryOptions { ScratchFolder = f, DirectColumns = on });
                var clock = Stopwatch.StartNew();
                var a = Must(Run(e, sql, SF, heavy: true), sql);
                times.Add($"{(on ? "direct" : "JSON")} {clock.Elapsed.TotalSeconds:F2} s");
                answer ??= a.Rows[0][0];
                Check(Equals(answer, a.Rows[0][0]), $"{sql}: {answer} one way, {a.Rows[0][0]} the other");
            }
            said.Add($"{sql} = {answer} both ways ({string.Join(", ", times)})");
        }
        said.Add(Filters(c));
        // J63 (GAPS S89): the same routes over a set of plugins, the winners and the versions scope
        said.Add(SetScopeTests.Run(c));
        return string.Join("; ", said);
    }

    /// <summary>
    /// J30: every record of a type, each column RawColumn reads from the record's bytes against DirectColumn on
    /// Mutagen's object of the same record: the same value and kind, or the record left to Mutagen (the bytes can't
    /// say it for sure). Each column on its own, so one column read through Mutagen doesn't hide the others.
    /// <paramref name="only"/>: the columns held (all the bytes read, when null).
    /// </summary>
    internal static string RawAgainstMutagen(Ctx c, string plugin, string type, Func<RawColumn, bool>? only = null)
    {
        var view = c.Engine.Views.Catalog(type).Typed;
        var cols = view.Columns.Select(col => (Spec: col, Raw: RawColumn.For(view, col, c.Schema), Direct: DirectColumn.For(col, "English"))).Where(x => x.Raw != null && (only == null || only(x.Raw))).ToList();
        Check(cols.Count > 0, $"{type}: no column is read from the bytes");
        Check(cols.All(x => x.Direct != null), $"{type}: a column read from the bytes has no direct reader");
        var p = c.Index.Plugin(plugin)!;
        var entries = p.Segment!.EntriesOf(c.Schema.SignatureOf(type)!).ToArray();
        var session = c.Records.Sessions.Get(plugin);
        c.Records.OpenForThreads(entries.Select(e => new RecordRef(p.Id, e)));
        // A translated text in English, as the direct reader gives it (GAPS S152)
        var rawPlugin = new RawPlugin(p, session, "English");
        var readers = cols.Select(x => new RawRecordColumns(new[] { x.Raw! })).ToArray();
        var fromBytes = new long[cols.Count]; var toMutagen = new long[cols.Count];
        long records = 0, values = 0; var bad = new List<string>(); long badCount = 0;
        var clock = Stopwatch.StartNew();
        foreach (string problem in RecordPipeline.Read<string>(new[] { new ReadSet { Plugin = p, Entries = entries } }, (in PipelineRecord r, List<string> output) =>
        {
            Interlocked.Increment(ref records);
            if (r.Problem != PipelineProblem.None) { output.Add($"{c.Index.Describe(r.Ref).Label}: its bytes can't be read ({r.Problem})"); return; }
            object rec;
            try { rec = c.Records.Load(r.Ref).Record; }
            catch (Exception e) { output.Add($"{c.Index.Describe(r.Ref).Label}: Mutagen couldn't read it ({e.Message})"); return; }
            var one = new object?[1];
            for (int k = 0; k < cols.Count; k++)
            {
                if (!readers[k].Read(r.Head, r.Data, rawPlugin, one)) { Interlocked.Increment(ref toMutagen[k]); continue; }
                Interlocked.Increment(ref fromBytes[k]); Interlocked.Increment(ref values);
                object? m;
                try { m = cols[k].Direct!.Read(rec); }
                catch (InvalidOperationException e) { output.Add($"{c.Index.Describe(r.Ref).Label}, column {cols[k].Spec.Name}: read straight from the record, {e.Message}"); continue; }
                if (!SameValue(one[0], m)) output.Add($"{c.Index.Describe(r.Ref).Label}, column {cols[k].Spec.Name}: from the bytes {Show(one[0])}, Mutagen {Show(m)}");
            }
        }, new PipelineOptions { Data = PipelineData.Plain, CostPerByte = PipelineOptions.MutagenCostPerByte, PieceRecords = 512 }))
        {
            badCount++;
            if (bad.Count < 5) bad.Add(problem);
        }
        Check(badCount == 0, $"{type} of {plugin}: {badCount:N0} difference(s) between the bytes and Mutagen. The first: {string.Join("; ", bad)}");
        var left = cols.Select((x, k) => (x.Spec.Name, N: toMutagen[k])).Where(x => x.N > 0).Select(x => $"{x.Name} {x.N:N0}").ToList();
        return $"{type} of {plugin}: {records:N0} records × {cols.Count} columns read from the bytes ({string.Join(", ", cols.Select(x => x.Spec.Name))}): {values:N0} values equal to Mutagen's, "
            + $"{(left.Count == 0 ? "none left to Mutagen" : "left to Mutagen: " + string.Join(", ", left))}, in {clock.Elapsed.TotalSeconds:F1} s";
    }

    static bool SameValue(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (long x, long y) => x == y,
        (double x, double y) => BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y),
        (string x, string y) => string.Equals(x, y, StringComparison.Ordinal),
        _ => false,
    };

    static string Show(object? v) => v == null ? "NULL" : $"{v} ({v.GetType().Name})";

    /// <summary>The column tables two engines wrote for one type of one plugin, held against each other in SQLite:
    /// every value and its type. Returns the rows.</summary>
    static long SameTables(QueryEngine a, QueryEngine b, IndexedPlugin plugin, string type, List<string> columns)
    {
        using var con = new SqliteConnection($"Data Source={a.PluginFile(plugin).Path};Mode=ReadOnly;Pooling=False");
        con.Open();
        using (var at = con.CreateCommand()) { at.CommandText = $"ATTACH DATABASE {SqlText.Literal(b.PluginFile(plugin).Path)} AS b"; at.ExecuteNonQuery(); }
        string Table(string db)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"SELECT TableName FROM {db}.Proj WHERE Type = $t ORDER BY Rows DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$t", type);
            return cmd.ExecuteScalar() as string ?? throw new TestFailed($"{type} of {plugin.Name}: no column table in {db}");
        }
        string ta = Table("main"), tb = Table("b");
        long Scalar(string sql) { using var cmd = con.CreateCommand(); cmd.CommandText = sql; return (long)cmd.ExecuteScalar()!; }
        long rows = Scalar($"SELECT count(*) FROM main.{SqlText.Name(ta)}");
        Check(rows == Scalar($"SELECT count(*) FROM b.{SqlText.Name(tb)}"), $"{type} of {plugin.Name}: the two column tables have different numbers of rows");
        Check(Scalar($"SELECT count(*) FROM main.{SqlText.Name(ta)} x JOIN b.{SqlText.Name(tb)} y ON x.Seq = y.Seq AND x.FormKey = y.FormKey AND x.Pos = y.Pos") == rows, $"{type} of {plugin.Name}: the rows aren't the same records in the same order");
        foreach (string col in columns)
        {
            string q = SqlText.Name(col);
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"SELECT x.FormKey, quote(x.{q}), typeof(x.{q}), quote(y.{q}), typeof(y.{q}), count(*) OVER () FROM main.{SqlText.Name(ta)} x JOIN b.{SqlText.Name(tb)} y ON x.Seq = y.Seq WHERE x.{q} IS NOT y.{q} OR typeof(x.{q}) <> typeof(y.{q}) LIMIT 1";
            using var r = cmd.ExecuteReader();
            if (r.Read())
                throw new TestFailed($"{type} of {plugin.Name}, column {col}: {r.GetInt64(5):N0} record(s) differ. The first, {r.GetString(0)}: direct {r.GetString(1)} ({r.GetString(2)}), through the JSON {r.GetString(3)} ({r.GetString(4)})");
        }
        return rows;
    }

    /// <summary>
    /// J30: a statement's col = literal applied while reading. On kgcdoom.esm under a low limit (the route Starfield.esm's
    /// 3.29 million placed objects take): each filtered statement gives the rows the whole read gives, says it kept only
    /// the matching records, and stores nothing in the scratch file; each statement where a row with another value could
    /// count is read without the filter (and gives the same rows too). With --heavy, Starfield.esm's count for one base,
    /// filtered, against Mutagen's.
    /// </summary>
    static string Filters(Ctx c)
    {
        var placed = c.Mod(KGC).EnumerateMajorRecords<IPlacedObjectGetter>().ToList();
        var bases = placed.Where(p => !p.Base.IsNull).GroupBy(p => p.Base.FormKey).OrderByDescending(g => g.Count()).Select(g => g.Key.ToString()).ToList();
        string b0 = bases[0], b1 = bases[1];
        var filtered = new[]
        {
            $"SELECT FormKey, Base FROM PlacedObject WHERE Base = '{b0}' ORDER BY FormKey",
            $"SELECT FormKey, Base FROM PlacedObject WHERE '{b0.ToLowerInvariant()}' = Base ORDER BY FormKey",
            $"SELECT count(*) FROM PlacedObject p WHERE p.Base IN ('{b0}', '{b1}', '000000:Nothing.esm')",
            $"SELECT FormKey, Base, Scale FROM PlacedObject WHERE Base = '{b1}' AND Scale IS NULL ORDER BY FormKey",
        };
        var whole = new[]
        {
            $"SELECT FormKey, Base FROM PlacedObject WHERE Base = '{b0}' OR Scale > 1 ORDER BY FormKey",
            $"SELECT FormKey, Base FROM PlacedObject WHERE Base LIKE '{b0}' ORDER BY FormKey",
            $"SELECT FormKey, Base FROM PlacedObject WHERE Base = '{b0}' COLLATE BINARY ORDER BY FormKey",
            $"SELECT a.FormKey FROM PlacedObject a JOIN PlacedObject b ON b.Base = a.Base WHERE a.Base = '{b1}' ORDER BY 1",
            $"SELECT FormKey, Base FROM PlacedObject WHERE Base = '{b0}' UNION ALL SELECT FormKey, Base FROM PlacedObject WHERE Scale > 1 ORDER BY 1",
            $"SELECT count(*) FROM PlacedObject WHERE Base = (SELECT Base FROM PlacedObject WHERE Base IS NOT NULL LIMIT 1)",
        };
        int n = 0;
        foreach (string sql in filtered.Concat(whole))
        {
            bool expect = n++ < filtered.Length;
            using var low = Fresh(c, "sql-filter", f => new QueryOptions { ScratchFolder = f, HeavySeconds = 0.0001 });
            var a = Must(Run(low, sql, KGC, heavy: true), sql);
            var w = c.Ok(sql, KGC);
            Same($"\"{sql}\" against the whole read", Rows(a), Rows(w), ordered: true);
            bool kept = a.Cost.Read.Any(r => r.Contains("only those kept", StringComparison.Ordinal));
            Check(kept == expect, $"\"{sql}\" must {(expect ? "" : "not ")}be filtered while reading: {a.Cost.Text}");
            if (expect)
                using (var con = new SqliteConnection($"Data Source={low.PluginFile(c.Index.Plugin(KGC)!).Path};Mode=ReadOnly;Pooling=False"))
                {
                    con.Open();
                    using var cmd = con.CreateCommand();
                    cmd.CommandText = "SELECT (SELECT count(*) FROM Rec) + (SELECT count(*) FROM Proj)";
                    Check((long)cmd.ExecuteScalar()! == 0, $"\"{sql}\": a filtered read stores nothing in the scratch file");
                }
        }
        string said = $"{filtered.Length} statements filtered while reading and {whole.Length} not, each equal to the whole read";
        if (c.Heavy)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in c.Mod(SF).EnumerateMajorRecords<IPlacedObjectGetter>()) if (!r.Base.IsNull) { string k = r.Base.FormKey.ToString(); counts[k] = counts.GetValueOrDefault(k) + 1; }
            foreach (string k in new[] { "00003B:Starfield.esm", counts.OrderByDescending(x => x.Value).First().Key })
            {
                using var e = Fresh(c, "sql-filter-sf");
                var a = Must(Run(e, $"SELECT count(*) FROM PlacedObject WHERE Base = '{k}'", SF), k);
                Check((long)a.Rows[0][0]! == counts.GetValueOrDefault(k), $"Starfield.esm, base {k}: {a.Rows[0][0]} placed objects, Mutagen {counts.GetValueOrDefault(k)}");
                said += $"; Starfield.esm WHERE Base = '{k}': {a.Rows[0][0]:N0}, equal to Mutagen, in {a.Cost.Seconds:F2} s ({a.Cost.Text})";
            }
        }
        return said;
    }

    // ------------------------------------------------------------------ S81: a record's sizes in Forms
    /// <summary>
    /// GAPS S81: Forms gives each record's Size (stored after its header), DataSize (inflated) and Compressed. On
    /// SFBGS00D.esm, every record's three values against the file read here (the header's size, and a compressed
    /// record's first four bytes), and the biggest cell by DataSize (J21 found 03D1E5:SFBGS00D.esm, 1,519,435 bytes
    /// inflated, with a Python walk of the file).
    /// </summary>
    public static string FormsSizes(Ctx c)
    {
        const string plugin = "SFBGS00D.esm";
        var p = c.Index.Plugin(plugin)!;
        var seg = p.Segment!;
        var want = new Dictionary<long, (long Size, long Data, long Zip)>();
        using (var file = File.OpenHandle(p.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var header = new byte[28];
            for (int i = 0; i < seg.Count; i++)
            {
                var e = seg[i];
                int n = RandomAccess.Read(file, header, e.Offset);
                Check(n >= 24, $"{plugin}: no record header at {e.Offset}");
                long size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                bool zip = (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) & 0x00040000) != 0;
                long data = zip ? BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)) : size;
                want[e.Offset] = (size, data, zip ? 1 : 0);
            }
        }
        using var e2 = Fresh(c, "sql-forms");
        var all = Must(Run(e2, "SELECT Pos, Size, DataSize, Compressed FROM Forms", plugin), "Forms sizes");
        Check(all.Rows.Count == want.Count, $"{plugin}: Forms has {all.Rows.Count:N0} rows, the file {want.Count:N0} records");
        int bad = 0; string? first = null;
        foreach (var r in all.Rows)
        {
            var w = want[(long)r[0]!];
            if ((long)r[1]! != w.Size || (long)r[2]! != w.Data || (long)r[3]! != w.Zip) { bad++; first ??= $"at {r[0]}: {r[1]}/{r[2]}/{r[3]}, the file {w.Size}/{w.Data}/{w.Zip}"; }
        }
        Check(bad == 0, $"{plugin}: {bad:N0} records' sizes differ from the file's. The first {first}");
        // The biggest cell as stored is the one J21's walk found; as read (inflated) another one is bigger
        var stored = Must(Run(e2, "SELECT FormKey, Size, DataSize FROM Forms WHERE Type = 'Cell' ORDER BY Size DESC LIMIT 1", plugin), "the biggest cell stored");
        Check(Row(stored.Rows[0][0], stored.Rows[0][1], stored.Rows[0][2]) == Row("03D1E5:SFBGS00D.esm", 982494L, 1519435L), $"the biggest cell of {plugin} as stored: {Row(stored.Rows[0])}");
        var cells = seg.EntriesOf("CELL").Select(i => want[seg[i].Offset]).ToList();
        var top = Must(Run(e2, "SELECT FormKey, Size, DataSize FROM Forms WHERE Type = 'Cell' ORDER BY DataSize DESC LIMIT 1", plugin), "the biggest cell");
        Check((long)top.Rows[0][2]! == cells.Max(x => x.Data), $"the biggest cell of {plugin} as read: {Row(top.Rows[0])}, the file's biggest is {cells.Max(x => x.Data)} bytes");
        return $"{plugin}: {want.Count:N0} records' Size, DataSize and Compressed equal to the file's; the biggest cell stored {Row(stored.Rows[0])}, as read {Row(top.Rows[0])}";
    }

    // ------------------------------------------------------------------ S28: a typed view narrowed by FormKey
    public static string KeyedReads(Ctx c)
    {
        var said = new List<string>();
        var ix = c.Index;
        var sf = ix.Plugin(SF)!;
        var seg = sf.Segment!;
        // Three placed objects of Starfield.esm: the first, one in the middle and the last of the file
        var refr = seg.EntriesOf("REFR").ToArray();
        var picks = new[] { refr[0], refr[refr.Length / 2], refr[^1] }.Select(e => new RecordRef(sf.Id, e)).ToList();
        var keys = picks.Select(r => ix.Describe(r).FormKey).ToList();
        string Base(RecordRef r) { var rec = c.Records.Load(r); return Fmt(((IPlacedObjectGetter)rec.Record).Base); }
        var want = picks.Select(r => Row(ix.Describe(r).FormKey, Base(r))).ToList();

        var e = Fresh(c, "sql-keyed");
        var clock = Stopwatch.StartNew();
        var one = Must(Run(e, $"SELECT FormKey, Base FROM PlacedObject WHERE FormKey = '{keys[1]}'", SF), "one FormKey");
        double oneSeconds = clock.Elapsed.TotalSeconds;
        Same("one placed object by FormKey", Rows(one), new[] { want[1] }, ordered: true);
        Check(one.Cost.Read.Count == 1 && one.Cost.Read[0].Contains("by FormKey", StringComparison.Ordinal), $"only that record is read: {one.Cost.Text}");
        said.Add($"PlacedObject WHERE FormKey = '{keys[1]}' on {SF}: 1 row, equal to Mutagen, in {oneSeconds:F2} s ({one.Cost.Read[0]})");

        var list = Must(Run(e, $"SELECT p.FormKey, p.Base FROM PlacedObject AS p WHERE p.FormKey IN ({string.Join(", ", keys.Select(k => $"'{k}'"))}) ORDER BY p.FormKey", SF), "IN");
        Same("three placed objects by FormKey IN (…)", Rows(list), want, ordered: false);
        var flipped = Must(Run(e, $"SELECT Base FROM PlacedObject WHERE '{keys[0].ToLowerInvariant()}' = FormKey", SF), "flipped");
        Check(Rows(flipped).SequenceEqual(new[] { want[0].Split(" | ")[1] }), $"'…' = FormKey, in lower case: {string.Join(" / ", Rows(flipped))}");
        said.Add("IN (three), '…' = FormKey in lower case: equal to Mutagen");

        // The load order: the version the game uses, and every copy
        var w = c.Lo($"SELECT FormKey, Plugin FROM PlacedObject WHERE FormKey IN ({string.Join(", ", keys.Select(k => $"'{k}'"))})", ScopeKind.Winners);
        Must(w, "winners");
        var wantWinners = keys.Select(k => ix.Versions(k) is { Winner: >= 0 } kv ? Row(k, ix.Plugins[kv.Holders[kv.Winner].Plugin].Name) : null).Where(x => x != null).Select(x => x!).ToList();
        Same("the winners of three placed objects", Rows(w), wantWinners, ordered: false);
        var v = Must(c.Lo($"SELECT count(*) FROM PlacedObject WHERE FormKey IN ({string.Join(", ", keys.Select(k => $"'{k}'"))})", ScopeKind.Versions), "versions");
        long copies = keys.Sum(k => (long)(ix.Versions(k)?.Holders.Count(h => ix.Plugins[h.Plugin].Order.Enabled) ?? 0));
        Check(Equals(v.Rows[0][0], copies), $"versions: {v.Rows[0][0]} copies, the header index has {copies} in loaded plugins");
        said.Add($"winners and versions: {wantWinners.Count} winners, {copies} copies, as the header index has them");

        // Where a row of the view could be one with another FormKey, nothing is narrowed: the whole read is announced
        string k0 = keys[0];
        var refused = new[]
        {
            $"SELECT Base FROM PlacedObject WHERE FormKey = '{k0}' OR Base = '000001:Starfield.esm'",
            $"SELECT Base FROM PlacedObject WHERE FormKey >= '{k0}'",
            $"SELECT Base FROM PlacedObject WHERE FormKey IN (SELECT FormKey FROM Weapon)",
            $"SELECT p.Base FROM PlacedObject p JOIN Weapon w ON w.FormKey = p.FormKey WHERE w.FormKey = '{k0}'",
            $"SELECT Base FROM PlacedObject WHERE (FormKey, Base) IN (VALUES ('{k0}', 'x'))",
            $"SELECT Base FROM PlacedObject WHERE FormKey = '{k0}' COLLATE BINARY",
            $"SELECT Base FROM PlacedObject WHERE FormKey = '{k0}' || ''",
            $"SELECT p.Base FROM PlacedObject p JOIN PlacedObject_LinkedReferences l USING (FormKey) WHERE l.FormKey = '{k0}'",
            $"SELECT Base FROM PlacedObject NATURAL JOIN PlacedObject_LinkedReferences WHERE FormKey = '{k0}'",
            $"SELECT Base FROM PlacedObject WHERE NOT FormKey = '{k0}'",
            $"SELECT Base FROM PlacedObject WHERE FormKey = :fk",
        };
        foreach (string sql in refused)
        {
            var a = e.Run(new QueryRequest { Sql = sql, Plugins = new[] { SF }, Parameters = new Dictionary<string, object?> { ["fk"] = k0 } });
            // Not by FormKey from the header index. The typed view joined to a list by FormKey (USING) is read for the
            // list's FormKeys instead since J21 (ListReads.cs), which gives every row: that statement may run
            bool joined = a.Cost.Read.Any(r => r.Contains("with the FormKeys that hold the list", StringComparison.Ordinal));
            Check(a.Error == null && (a.NotRun != null && !a.Cost.Read.Any() || joined) && !a.Cost.Read.Any(r => r.Contains("by FormKey, from the header index", StringComparison.Ordinal)),
                $"\"{sql}\" must not be narrowed by FormKey: {a.Error ?? (a.NotRun == null ? "it ran: " + a.Cost.Text : "")}");
        }
        said.Add($"{refused.Length} statements where a row could have another FormKey are announced, not narrowed");

        // On a small plugin, made to take the same route by a low limit: equal to the whole read, Json too
        var placed = c.Mod(KGC).EnumerateMajorRecords<IPlacedObjectGetter>().Select(p => p.FormKey.ToString()).ToList();
        var some = Enumerable.Range(0, 6).Select(i => placed[i * (placed.Count - 1) / 5]).ToList();
        string q = $"SELECT FormKey, EditorID, Base, Scale, Position, json_extract(Json, '$.Placement') AS Placement, IsOverride FROM PlacedObject WHERE FormKey IN ({string.Join(", ", some.Select(k => $"'{k}'"))}) ORDER BY FormKey";
        using (var low = Fresh(c, "sql-keyed-low", f => new QueryOptions { ScratchFolder = f, HeavySeconds = 0.0001 }))
        {
            var keyed = Must(Run(low, q, KGC), "kgcdoom keyed");
            Check(keyed.Cost.Read.Count == 1 && keyed.Cost.Read[0].Contains("by FormKey", StringComparison.Ordinal), $"kgcdoom.esm under a low limit reads by FormKey: {keyed.Cost.Text}");
            var whole = c.Ok(q, KGC);
            Same("six placed objects of kgcdoom.esm by FormKey against the whole read", Rows(keyed), Rows(whole), ordered: true);
        }
        said.Add($"kgcdoom.esm under a low limit: six placed objects by FormKey equal the whole read (Json too)");
        return string.Join("; ", said);
    }

    // ------------------------------------------------------------------ S3: Links WHERE ToKey with no link index
    public static string LinksByBytes(Ctx c)
    {
        if (!c.Heavy) return "Starfield.esm's case runs with --heavy (kgcdoom.esm's is in auto-reads)";
        const string target = "04A0A0:Starfield.esm";
        var e = Fresh(c, "sql-bytes");
        var clock = Stopwatch.StartNew();
        var a = Must(Run(e, $"SELECT DISTINCT FromKey, FromType FROM Links WHERE ToKey = '{target}'", SF), "Links by bytes");
        double seconds = clock.Elapsed.TotalSeconds;
        string bytesNote = a.Notes.FirstOrDefault(n => n.Contains("searched by their bytes", StringComparison.Ordinal))
            ?? throw new TestFailed($"the answer must say the records were found by a byte search: {string.Join(" / ", a.Notes)}");
        // The world types too big to search by themselves are said (placed objects, cells, navmeshes, surface trees)
        string leftNote = a.Notes.FirstOrDefault(n => n.StartsWith("Not read for Links", StringComparison.Ordinal))
            ?? throw new TestFailed($"the types left out aren't said: {string.Join(" / ", a.Notes)}");
        // Mutagen's own links to the target, of every record of a type the answer didn't leave out. A cell, a
        // worldspace, a topic or a quest gives Mutagen the links of the records inside it too: those are left out on
        // both sides, and the records inside are counted themselves.
        var containers = new HashSet<string>(StringComparer.Ordinal) { "Cell", "Worldspace", "DialogTopic", "Quest" };
        var key = FormKey.Factory(target);
        var mutagen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var leftOut = new HashSet<string>(StringComparer.Ordinal);
        var mclock = Stopwatch.StartNew();
        foreach (var rec in c.Mod(SF).EnumerateMajorRecords())
        {
            string type = Ctx.Type(rec);
            if (containers.Contains(type)) continue;
            if (leftNote.Contains($" {type} (", StringComparison.Ordinal)) { leftOut.Add(type); continue; }
            foreach (var l in rec.EnumerateFormLinks())
                if (l.FormKey == key) { mutagen.Add(rec.FormKey.ToString()); break; }
        }
        double mutagenSeconds = mclock.Elapsed.TotalSeconds;
        var sql = a.Rows.Where(r => !containers.Contains((string)r[1]!)).Select(r => (string)r[0]!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Same($"the records of {SF} that link to {target}", sql, mutagen.ToList(), ordered: false);
        Check(seconds < 30, $"the answer took {seconds:F1} s");
        return $"Links WHERE ToKey = '{target}' on {SF} with no link index: {sql.Count} records in {seconds:F1} s, equal to Mutagen's links ({mutagenSeconds:F1} s for Mutagen's walk; left out on both sides: {string.Join(", ", leftOut.OrderBy(x => x))}). Note: {Cut(bytesNote, 300)}";
    }

    // ------------------------------------------------------------------ S31: the string files name the scratch table
    /// <summary>
    /// A copy of the smallest localized plugin whose English texts come from an archive, with its string files taken
    /// out beside it. A name is read through SQL; the English .strings file is changed (that one text); the same query
    /// gives the new text, from a scratch table of its own. Before the fix the table read first was used again and gave
    /// the old text.
    /// </summary>
    public static string StringsStamp(Ctx c)
    {
        var data = new HeaderIndex(PluginSource.Folder(c.Data), new IndexOptions { ScratchFolder = Path.Combine(c.Work, "index"), VersionText = "sfsquery-tests strings", StringFiles = new StringFiles(c.Data) });
        using var _ = data;
        data.Refresh();
        static bool HandsOff(string name) => name.StartsWith("darkstarpatch_", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(name, @"^sfta0[1-6]\.esm$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var source = data.Plugins
            .Where(p => p.Header is { Localized: true } && p.Segment != null && !HandsOff(p.Name) && p.Problems.MissingMasters.Count == 0 && p.Stamp.Length < 4_000_000
                && p.Strings.Files.Any(f => f.InArchive && f.Name.EndsWith("_en.strings", StringComparison.Ordinal)))
            .OrderBy(p => p.Stamp.Length).FirstOrDefault() ?? throw new TestFailed("no small localized plugin with its English strings in an archive");
        var archived = source.Strings.Files.First(f => f.Name.EndsWith("_en.strings", StringComparison.Ordinal));
        string folder = Path.Combine(c.Work, "sql-strings");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        // The copy in a folder of its own: the index and the scratch tables may not be inside a folder that holds plugins
        string plugins = Path.Combine(folder, "data");
        string loose = Path.Combine(plugins, "Strings");
        Directory.CreateDirectory(loose);
        string copy = Path.Combine(plugins, source.Name);
        File.Copy(source.Path, copy);
        var reader = Archive.CreateReader(GameRelease.Starfield, archived.From);
        Check(reader.TryGetFolder("strings", out var inArchive), $"{Path.GetFileName(archived.From)} has no Strings folder");
        foreach (var item in inArchive!.Files)
        {
            string name = Path.GetFileName(item.Path.ToString().Replace('/', '\\')).ToLowerInvariant();
            if (source.Strings.Files.Any(f => f.Name == name)) File.WriteAllBytes(Path.Combine(loose, name), item.GetBytes());
        }
        string strings = Path.Combine(loose, archived.Name);
        Check(File.Exists(strings) && strings.StartsWith(c.Work, StringComparison.OrdinalIgnoreCase), $"{archived.Name} wasn't taken out into the work folder");

        // The copy and its masters, theirs too (a master with masters of its own can't be loaded without them)
        var files = new List<string> { copy };
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddMasters(IndexedPlugin of) { foreach (var m in of.Header!.Masters) if (named.Add(m) && data.Plugin(m) is { } mp) { files.Add(mp.Path); AddMasters(mp); } }
        AddMasters(source);
        using var index = new HeaderIndex(PluginSource.FromFiles(files), new IndexOptions { ScratchFolder = Path.Combine(folder, "index"), VersionText = "sfsquery-tests strings", PluginsTxt = "", Ccc = "", StringFiles = new StringFiles(c.Data) });
        index.Refresh();
        using var records = new RecordEngine(index, c.Schema, Path.Combine(folder, "verify"), c.Data);
        using var engine = new QueryEngine(records, new QueryOptions { ScratchFolder = Path.Combine(folder, "sql") });
        // One of the plugin's own records whose name is a text of its string files
        var p = index.Plugin(source.Name)!;
        var seg = p.Segment!;
        string fk = ""; uint id = 0;
        for (int i = 0; i < seg.FirstUnresolved && id == 0; i++)
        {
            if (seg[i].Owner != seg.SelfOwner || seg[i].IsCompressed) continue;
            var raw = records.Raw(new RecordRef(p.Id, i));
            var full = raw.Subrecords.FirstOrDefault(x => x.Signature == "FULL" && x.Length == 4);
            if (full.Signature == null) continue;
            uint sid = BinaryPrimitives.ReadUInt32LittleEndian(raw.Content(full));
            if (sid != 0) { id = sid; fk = index.Describe(new RecordRef(p.Id, i)).FormKey; }
        }
        Check(id != 0, $"no record of {source.Name} has a name from its string files");
        string sql = $"SELECT Name FROM Records WHERE FormKey = '{fk}'";
        QueryAnswer Ask() => Must(engine.Run(new QueryRequest { Sql = sql, Plugins = new[] { source.Name } }), sql);
        var first = Ask();
        string before = first.Rows.Count == 1 ? (string?)first.Rows[0][0] ?? "" : "";
        Check(before.Length > 0, $"{fk}: no name read ({string.Join(" / ", Rows(first))})");
        var tablesBefore = Directory.GetFiles(Path.Combine(folder, "sql"), "q*.db").Select(Path.GetFileName).ToHashSet();

        string after = "S31 test: " + before;
        Rewrite(strings, id, after);
        var second = Ask();
        Check(second.Rows.Count == 1 && (string?)second.Rows[0][0] == after, $"after the string file changed, {fk} reads \"{(second.Rows.Count == 1 ? second.Rows[0][0] : "")}\", not \"{after}\"");
        var tablesAfter = Directory.GetFiles(Path.Combine(folder, "sql"), "q*.db").Select(Path.GetFileName).ToHashSet();
        Check(tablesAfter.Except(tablesBefore).Any(n => n!.Contains("-s", StringComparison.Ordinal)), $"no new scratch table named by the strings stamp: {string.Join(", ", tablesAfter)}");
        return $"{source.Name} (a copy, its {source.Strings.Files.Count} string files loose): {fk} read \"{before}\", then \"{after}\" after its English .strings file changed, from a new scratch table ({string.Join(", ", tablesAfter.Except(tablesBefore))})";
    }

    /// <summary>
    /// A scratch table written by another build (another Mutagen, another record layer) is never read: the file's name
    /// carries the build key. Before (notes\fixes14.md), a command-line query after the move to fixes.14 read the
    /// table fixes.13 had written under the same name. Here a table under the name the old rule gave is left in the
    /// folder, holding rows no build would write, and the query must not see them.
    /// </summary>
    public static string ScratchBuildKey(Ctx c)
    {
        string folder = Path.Combine(c.Work, "sql-buildkey");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        const string plugin = "atom74.esm";
        var p = c.Index.Plugin(plugin) ?? throw new TestFailed($"{plugin} isn't in Data");
        string newName = ScratchFile.PluginFileName(p.ReadingKey, "English", p.Strings);
        Check(System.Text.RegularExpressions.Regex.IsMatch(ScratchFile.BuildKey, "^[0-9a-f]{8}$") && newName.Contains("-" + ScratchFile.BuildKey + "-", StringComparison.Ordinal),
            $"the file name {newName} doesn't carry the build key {ScratchFile.BuildKey}");
        string oldName = newName.Replace("-" + ScratchFile.BuildKey, "", StringComparison.Ordinal);
        const string sql = "SELECT count(*) FROM Keyword WHERE EditorID = 'OLD BUILD'";
        using (var warm = new QueryEngine(c.Records, new QueryOptions { ScratchFolder = folder }))
            Must(warm.Run(new QueryRequest { Sql = sql, Plugins = new[] { plugin } }), sql);
        SqliteConnection.ClearAllPools();
        // The table as another build left it: under the old name, every keyword renamed
        File.Move(Path.Combine(folder, newName), Path.Combine(folder, oldName));
        foreach (var x in new[] { "-wal", "-shm" }) if (File.Exists(Path.Combine(folder, newName + x))) File.Delete(Path.Combine(folder, newName + x));
        int planted;
        using (var con = new SqliteConnection($"Data Source={Path.Combine(folder, oldName)};Pooling=False"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "UPDATE Rec SET EditorID = 'OLD BUILD' WHERE Type = 'Keyword'";
            planted = cmd.ExecuteNonQuery();
        }
        Check(planted > 0, "the planted table has no Keyword rows");
        using var fresh = new QueryEngine(c.Records, new QueryOptions { ScratchFolder = folder });
        var a = Must(fresh.Run(new QueryRequest { Sql = sql, Plugins = new[] { plugin } }), sql);
        Check(a.Rows.Count == 1 && Convert.ToInt64(a.Rows[0][0], System.Globalization.CultureInfo.InvariantCulture) == 0, $"the query read the table another build wrote ({string.Join(" / ", Rows(a))})");
        Check(File.Exists(Path.Combine(folder, newName)), $"the query made no table named {newName}");
        return $"{plugin}: its table is {newName}; a table under the old rule's name ({oldName}) with its {planted} keywords renamed \"OLD BUILD\" was left beside it, and the query read none of them";
    }

    /// <summary>One text of a .strings file replaced (the same as SFSearch.Records.Tests' StringsChanged).</summary>
    static void Rewrite(string path, uint id, string text)
    {
        var bytes = File.ReadAllBytes(path);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        int dataStart = 8 + (int)count * 8;
        var add = Encoding.UTF8.GetBytes(text + "\0");
        int at = -1;
        for (int i = 0; i < count; i++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8 + i * 8)) == id) { at = 8 + i * 8 + 4; break; }
        Check(at > 0, $"string {id:X8} isn't in {Path.GetFileName(path)}");
        uint offset = (uint)(bytes.Length - dataStart);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), offset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), offset + (uint)add.Length);
        File.WriteAllBytes(path, bytes.Concat(add).ToArray());
    }
}
