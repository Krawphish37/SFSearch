// Every typed column the bytes read (RawColumn), of every record type, against Mutagen's object of the same record
// (GSqlTests.RawAgainstMutagen: the same value and kind, or the record left to Mutagen), each column on its own. Over
// Starfield.esm; a type with more records there than a quick check takes (placed objects) on the smaller plugins that
// hold it, and on Starfield.esm with --heavy; a type Starfield.esm has no record of on the smaller plugins.
// Holds what world-reads and direct-columns hold for their sample of types, for all of them: a rule of RawColumn that
// reads a field where Mutagen holds something else shows here by the record and column (GAPS S122).
using System.Text.RegularExpressions;
using SFSearch.Index;
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class TypedAllTests
{
    const string SF = "Starfield.esm";
    /// <summary>Above this many records of a type, Starfield.esm is left to --heavy.</summary>
    const int QuickRecords = 300_000;
    static readonly string[] Smaller = { "sfta03.esm", "kgcdoom.esm" };
    static readonly Regex Counts = new(@": ([\d,]+) records × (\d+) columns read from the bytes .*: ([\d,]+) values equal to Mutagen's, (none left to Mutagen|left to Mutagen: (.*)), in ", RegexOptions.Compiled | RegexOptions.Singleline);

    static readonly Regex ListCounts = new(@": ([\d,]+) records × (\d+) list views from the bytes: ([\d,]+) rows, ([\d,]+) values equal to Mutagen's, (none left to Mutagen|records left to Mutagen: (.*)), in ", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Every list view the bytes read (RawList), of every record type that doesn't share its signature, against Mutagen's
    /// object of the same record walked by DirectList (WorldReadTests.ListsAgainstMutagen: row for row, value for value, or
    /// the record left to Mutagen). Over the same plugins as <see cref="Run"/>. The records left to Mutagen, by list view,
    /// are saved (lists-all-left.txt): where the bytes are read in name only.
    /// </summary>
    public static string Lists(Ctx c)
    {
        int types = 0, views = 0;
        long records = 0, rows = 0, values = 0;
        var left = new List<string>(); var none = new List<string>();
        bool heavy = c.Records.AllowHeavy;
        c.Records.AllowHeavy = true;
        // Why the value walk sends a record to Mutagen, by list (lists-all-why.txt)
        LeftReasons.Start();
        try
        {
            foreach (string root in c.Engine.Views.RootNames.OrderBy(r => r, StringComparer.Ordinal))
            {
                var view = c.Engine.Views.Catalog(root).Typed;
                if (view.IsUnion || view.Types.Count != 1 || c.Schema.SignatureOf(view.Types[0]) is not { } sig) continue;
                int Count(string plugin) => c.Index.Plugin(plugin)?.Segment?.Signatures.Where(s => s.Signature == sig).Select(s => s.Count).FirstOrDefault() ?? 0;
                var plugins = new List<string>();
                if (Count(SF) > 0 && (c.Heavy || Count(SF) <= QuickRecords)) plugins.Add(SF);
                if (plugins.Count == 0) plugins.AddRange(Smaller.Where(p => Count(p) > 0));
                if (plugins.Count == 0) { none.Add(root); continue; }
                types++;
                foreach (string plugin in plugins)
                {
                    string said = WorldReadTests.ListsAgainstMutagen(c, plugin, view.Types[0]);
                    if (said.EndsWith("no list view from the bytes", StringComparison.Ordinal)) continue;
                    var m = ListCounts.Match(said);
                    Check(m.Success, $"{root} of {plugin}: an answer this test doesn't read: {Cut(said, 300)}");
                    long N(int g) => long.Parse(m.Groups[g].Value.Replace(",", ""));
                    records += N(1); rows += N(3); values += N(4);
                    if (plugin == plugins[0]) views += (int)N(2);
                    if (m.Groups[6].Success) left.Add($"{root} of {plugin} ({N(1):N0} records): {m.Groups[6].Value}");
                }
            }
        }
        finally { c.Records.AllowHeavy = heavy; }
        c.Save("lists-all-left.txt", left);
        c.Save("lists-all-why.txt", LeftReasons.Stop());
        return $"{views:N0} list views of {types} types read from the bytes, {records:N0} records, {rows:N0} rows, {values:N0} values equal to Mutagen's"
            + $"; records left to Mutagen for a list view in {left.Count} types: {Cut(string.Join(" | ", left), 6000)}";
    }

    /// <summary>
    /// The list columns whose value is a plain value in some kinds of the entry and a list of plain values in others
    /// (VaryingColumn.cs: a script property's Data), which the JSON gives as the value or as the list's JSON text: for
    /// every record type that has one, each record read through Mutagen once, the column straight from the object
    /// (DirectList) and from the record's own bytes (RawList) against the same record's JSON through the view's own
    /// expression, value for value and in SQLite's kind (WorldReadTests.DirectAgainstJson). Over the same plugins as
    /// <see cref="Run"/>. The columns of varying shape that aren't read this way are named.
    /// </summary>
    public static string Varying(Ctx c)
    {
        int types = 0, columns = 0;
        var notRead = new SortedSet<string>(StringComparer.Ordinal);
        var said = new List<string>();
        foreach (string root in c.Engine.Views.RootNames.OrderBy(r => r, StringComparer.Ordinal))
        {
            var cat = c.Engine.Views.Catalog(root);
            var view = cat.Typed;
            if (view.IsUnion || view.Types.Count != 1 || c.Schema.SignatureOf(view.Types[0]) is not { } sig) continue;
            var varying = cat.Lists.Values.SelectMany(v => v.Columns.Where(col => col.ShapeVaries).Select(col => (v, col))).ToList();
            foreach (var (v, col) in varying.Where(x => !VaryingColumn.Supported(x.col))) notRead.Add(v.Name[(root.Length + 1)..] + "." + col.Name);
            int held = varying.Count(x => VaryingColumn.Supported(x.col));
            // (a type that shares its signature holds other types' records too, which this comparison doesn't tell apart)
            if (held == 0 || c.Schema.TypesOf(sig).Count != 1) continue;
            foreach (var (v, col) in varying.Where(x => VaryingColumn.Supported(x.col)))
            {
                Check(DirectList.For(v, new[] { col.Name }, "English") != null, $"{v.Name}.{col.Name}: no direct reader");
                Check(RawList.For(v, new[] { col.Name }, c.Schema, view) != null, $"{v.Name}.{col.Name}: not read from the bytes");
            }
            int Count(string plugin) => c.Index.Plugin(plugin)?.Segment?.Signatures.Where(s => s.Signature == sig).Select(s => s.Count).FirstOrDefault() ?? 0;
            var plugins = new List<string>();
            if (Count(SF) > 0 && (c.Heavy || Count(SF) <= QuickRecords)) plugins.Add(SF);
            if (plugins.Count == 0) plugins.AddRange(Smaller.Where(p => Count(p) > 0));
            if (plugins.Count == 0) continue;
            types++; columns += held;
            foreach (string plugin in plugins)
                said.Add(WorldReadTests.DirectAgainstJson(c, plugin, new[] { view.Types[0] }, bytesOnly: false, only: col => col.ShapeVaries && VaryingColumn.Supported(col)));
        }
        Check(columns > 0, "no column of varying shape is read straight from the records");
        c.Save("varying-columns.txt", said);
        string made = VaryingMade(c);
        return $"{columns:N0} columns of varying shape in {types} types, read straight from the object and from the bytes, equal to each record's JSON; {made}"
            + $"; of varying shape and read through the JSON: {(notRead.Count == 0 ? "none" : Cut(string.Join(", ", notRead), 1500))}";
    }

    /// <summary>
    /// What the plugins in Data don't hold: an activator made here with one script whose properties are of every kind
    /// with a Data, at the values the files lack (a list with nothing in it, a text with nothing in it, texts with
    /// quotes, backslashes, control characters and letters outside ASCII, a list of yes/no, floats that are -0, NaN, an
    /// infinity and 1E+20). Its Data column straight from the object (DirectList) against its JSON through the view's
    /// own expression, as the serializer writes it and SQLite reads it.
    /// </summary>
    static string VaryingMade(Ctx c)
    {
        var cat = c.Engine.Views.Catalog("Activator");
        var v = cat.Lists["Activator_VirtualMachineAdapter_Scripts_Properties"];
        var cols = new[] { "Data" };
        var direct = DirectList.For(v, cols, "English") ?? throw new TestFailed($"{v.Name}.Data: no direct reader");
        string sql = ViewBuilder.ListProjectSql(v, cols, "English");
        var entry = new Mutagen.Bethesda.Starfield.ScriptEntry { Name = "Made" };
        void Add(Mutagen.Bethesda.Starfield.ScriptProperty p) { p.Name = "p" + entry.Properties.Count.ToString(System.Globalization.CultureInfo.InvariantCulture); entry.Properties.Add(p); }
        foreach (bool b in new[] { false, true }) Add(new Mutagen.Bethesda.Starfield.ScriptBoolProperty { Data = b });
        foreach (int i in new[] { 0, -5, int.MaxValue, int.MinValue }) Add(new Mutagen.Bethesda.Starfield.ScriptIntProperty { Data = i });
        foreach (float f in new[] { 0f, -0f, 1f, 0.1f, 1E+20f, float.NaN, float.PositiveInfinity, float.Epsilon }) Add(new Mutagen.Bethesda.Starfield.ScriptFloatProperty { Data = f });
        foreach (string t in new[] { "", "plain", "a \"quote\"", "back\\slash", "tab\tline\nreturn\r", "\u00FCn\u00EF \u2028 \u0085", "[1,2]", " " }) Add(new Mutagen.Bethesda.Starfield.ScriptStringProperty { Data = t });
        var bools = new Mutagen.Bethesda.Starfield.ScriptBoolListProperty(); bools.Data.Add(true); bools.Data.Add(false); Add(bools);
        Add(new Mutagen.Bethesda.Starfield.ScriptBoolListProperty());
        var ints = new Mutagen.Bethesda.Starfield.ScriptIntListProperty(); foreach (int i in new[] { 0, -1, int.MaxValue }) ints.Data.Add(i); Add(ints);
        Add(new Mutagen.Bethesda.Starfield.ScriptIntListProperty());
        var floats = new Mutagen.Bethesda.Starfield.ScriptFloatListProperty(); foreach (float f in new[] { 0f, -0f, 1.5f, 1E+20f, float.NaN, float.NegativeInfinity }) floats.Data.Add(f); Add(floats);
        Add(new Mutagen.Bethesda.Starfield.ScriptFloatListProperty());
        var texts = new Mutagen.Bethesda.Starfield.ScriptStringListProperty(); foreach (string t in new[] { "", "a \"b\"", "back\\slash", "\u00E9\t\u2028", "plain" }) texts.Data.Add(t); Add(texts);
        Add(new Mutagen.Bethesda.Starfield.ScriptStringListProperty());
        var record = new Mutagen.Bethesda.Starfield.Activator(Mutagen.Bethesda.Plugins.FormKey.Factory("000800:Made.esm"), Mutagen.Bethesda.Starfield.StarfieldRelease.Starfield);
        var adapter = new Mutagen.Bethesda.Starfield.VirtualMachineAdapter();
        adapter.Scripts.Add(entry);
        record.VirtualMachineAdapter = adapter;
        byte[] utf8 = SFSearch.Records.RecordSerializer.ToUtf8(record).ToArray();
        using var maker = new JsonbMaker(con => Functions.RegisterPure(con, "English"));
        var want = maker.ProjectRows(utf8, sql, v.Lists.Count + cols.Length + 1);
        var got = direct.Read(record);
        Check(got.Count == entry.Properties.Count && want.Count == got.Count, $"the made activator: {entry.Properties.Count} properties, {got.Count} rows straight from the object, {want.Count} from its JSON");
        var differ = new List<string>();
        for (int k = 0; k < got.Count; k++)
            if (!WorldReadTests.Same(got[k][v.Lists.Count], want[k][v.Lists.Count]))
                differ.Add($"property {k} ({entry.Properties[k].GetType().Name}): straight from the object {WorldReadTests.Show(got[k][v.Lists.Count])}, the JSON {WorldReadTests.Show(want[k][v.Lists.Count])}");
        Check(differ.Count == 0, $"the made activator's Data differs from its JSON: {string.Join(" | ", differ)}");
        return $"a made activator's {got.Count} properties of every kind and edge value equal to its JSON";
    }

    public static string Run(Ctx c)
    {
        int types = 0, columns = 0;
        long records = 0, values = 0;
        var left = new List<string>(); var none = new List<string>(); var bad = new List<string>();
        bool heavy = c.Records.AllowHeavy;
        c.Records.AllowHeavy = true;
        // Why the value walk sends a record to Mutagen for a typed column (typed-all-why.txt)
        LeftReasons.Start();
        try
        {
            foreach (string root in c.Engine.Views.RootNames.OrderBy(r => r, StringComparer.Ordinal))
            {
                var view = c.Engine.Views.Catalog(root).Typed;
                if (view.IsUnion || view.Types.Count != 1 || c.Schema.SignatureOf(view.Types[0]) is not { } sig) continue;
                // (a type that shares its signature is held on its own records: SharedSignature, in RawAgainstMutagen)
                int Count(string plugin) => c.Index.Plugin(plugin)?.Segment?.Signatures.Where(s => s.Signature == sig).Select(s => s.Count).FirstOrDefault() ?? 0;
                var plugins = new List<string>();
                if (Count(SF) > 0 && (c.Heavy || Count(SF) <= QuickRecords)) plugins.Add(SF);
                if (plugins.Count == 0) plugins.AddRange(Smaller.Where(p => Count(p) > 0));
                if (plugins.Count == 0) { none.Add(root); continue; }
                types++;
                foreach (string plugin in plugins)
                {
                    string said;
                    // (every type is held before the test fails, so that one run says all that differs)
                    try { said = GSqlTests.RawAgainstMutagen(c, plugin, view.Types[0]); }
                    catch (TestFailed e) { bad.Add(e.Message); continue; }
                    var m = Counts.Match(said);
                    Check(m.Success, $"{root} of {plugin}: an answer this test doesn't read: {Cut(said, 300)}");
                    long N(int g) => long.Parse(m.Groups[g].Value.Replace(",", ""));
                    records += N(1); values += N(3);
                    if (plugin == plugins[0]) columns += (int)N(2);
                    if (m.Groups[5].Success) left.Add($"{root} of {plugin}: {Cut(m.Groups[5].Value, 400)}");
                }
            }
        }
        finally { c.Records.AllowHeavy = heavy; }
        c.Save("typed-all-left.txt", left);
        c.Save("typed-all-why.txt", LeftReasons.Stop());
        c.Save("typed-all-differences.txt", bad);
        Check(bad.Count == 0, $"{bad.Count} type(s) differ: " + Cut(string.Join(" || ", bad.Select(b => Cut(b, 700))), 9000));
        return $"{columns:N0} columns of {types} types read from the bytes, {records:N0} records, {values:N0} values equal to Mutagen's"
            + $"; no record anywhere tried: {(none.Count == 0 ? "none" : string.Join(", ", none))}"
            + $"; records left to Mutagen for a column in {left.Count} types: {Cut(string.Join(" | ", left), 6000)}";
    }

    /// <summary>
    /// The types that share a signature (GMST's five game settings, OMOD's eight modifications): a statement that names
    /// only a few columns of one, or only a list view of one, in an engine that has read nothing yet, gives that type's
    /// records alone, as the type read whole does. (Found 2026-11-16: named columns were read from every record of the
    /// signature, so GameSettingInt counted all 2,312 game settings of Starfield.esm, their floats' bytes as numbers.)
    /// </summary>
    public static string SharedSignatures(Ctx c)
    {
        var said = new List<string>();
        int n = 0;
        QueryAnswer Ask(QueryEngine e, string sql)
        {
            var a = e.Run(new QueryRequest { Sql = sql, Plugins = new[] { SF }, PageRows = 1000, AllowHeavy = true });
            Check(a.Error == null, $"{sql}: {a.Error}");
            return a;
        }
        QueryEngine NewEngine()
        {
            string folder = Path.Combine("sql-shared", "e" + ++n);
            string path = Path.Combine(c.Work, folder);
            if (Directory.Exists(path)) Directory.Delete(path, true);
            return c.NewEngine(folder);
        }
        foreach (string sig in c.Schema.Signatures.Where(s => c.Schema.TypesOf(s).Count > 1).OrderBy(s => s, StringComparer.Ordinal))
        {
            long inIndex = c.Index.Plugin(SF)?.Segment?.Signatures.Where(s => s.Signature == sig).Select(s => s.Count).FirstOrDefault() ?? 0;
            long sum = 0;
            foreach (string type in c.Schema.TypesOf(sig))
            {
                if (c.Engine.Views.TypesOf(type) == null) continue;
                var cat = c.Engine.Views.Catalog(type);
                if (cat.Typed.IsUnion || cat.Typed.Types.Count != 1) continue;
                // Read whole: the type's own records, by the Json column
                long whole; var listWhole = new Dictionary<string, long>(StringComparer.Ordinal);
                var lists = cat.Lists.Values.Select(v => v.Name).OrderBy(v => v, StringComparer.Ordinal).Take(3).ToList();
                using (var e = NewEngine())
                {
                    whole = (long)Ask(e, $"SELECT count(*) FROM {type} WHERE Json IS NOT NULL").Rows[0][0]!;
                    foreach (string lv in lists) listWhole[lv] = (long)Ask(e, $"SELECT count(*) FROM {lv}").Rows[0][0]!;
                }
                // Named columns alone, in an engine that has read nothing
                foreach (string col in new[] { "FormVersion", "EditorID" }.Concat(cat.Typed.Columns.Where(x => x.Kind is "Float" or "Int32" or "UInt32" or "FormLink").Select(x => x.Name).Take(2)))
                {
                    using var e = NewEngine();
                    var a = Ask(e, $"SELECT count(*), count({SqlText.Name(col)}) FROM {type}");
                    Check((long)a.Rows[0][0]! == whole, $"{type}: {a.Rows[0][0]} rows when only {col} is named, {whole} when read whole");
                }
                foreach (string lv in lists)
                {
                    using var e = NewEngine();
                    long got = (long)Ask(e, $"SELECT count(*) FROM {lv}").Rows[0][0]!;
                    Check(got == listWhole[lv], $"{lv}: {got} rows when it alone is read, {listWhole[lv]} with the type read whole");
                }
                sum += whole;
            }
            // (a record of the signature's base class, an OMOD that is none of the seven kinds, is a row of the union view alone)
            Check(sum > 0 && sum <= inIndex, $"{sig}: its types hold {sum:N0} records together, Starfield.esm has {inIndex:N0}");
            said.Add($"{sig}: {c.Schema.TypesOf(sig).Count} types, {sum:N0} of its {inIndex:N0} records in a type's own view, each type's own whether read whole or by named columns or a list view alone");
        }
        Check(said.Count > 0, "no signature has several record types");
        return string.Join("; ", said);
    }
}
