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
        try
        {
            foreach (string root in c.Engine.Views.RootNames.OrderBy(r => r, StringComparer.Ordinal))
            {
                var view = c.Engine.Views.Catalog(root).Typed;
                if (view.IsUnion || view.Types.Count != 1 || c.Schema.SignatureOf(view.Types[0]) is not { } sig || c.Schema.TypesOf(sig).Count != 1) continue;
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
        return $"{views:N0} list views of {types} types read from the bytes, {records:N0} records, {rows:N0} rows, {values:N0} values equal to Mutagen's"
            + $"; records left to Mutagen for a list view in {left.Count} types: {Cut(string.Join(" | ", left), 6000)}";
    }

    public static string Run(Ctx c)
    {
        int types = 0, columns = 0;
        long records = 0, values = 0;
        var left = new List<string>(); var none = new List<string>();
        bool heavy = c.Records.AllowHeavy;
        c.Records.AllowHeavy = true;
        try
        {
            foreach (string root in c.Engine.Views.RootNames.OrderBy(r => r, StringComparer.Ordinal))
            {
                var view = c.Engine.Views.Catalog(root).Typed;
                if (view.IsUnion || view.Types.Count != 1 || c.Schema.SignatureOf(view.Types[0]) is not { } sig) continue;
                // A type that shares its signature isn't read from the bytes (SharedSignatures holds it)
                if (c.Schema.TypesOf(sig).Count != 1) { Check(view.Columns.All(col => RawColumn.For(view, col, c.Schema) == null), $"{root}: a column of a type that shares its signature is read from the bytes"); continue; }
                int Count(string plugin) => c.Index.Plugin(plugin)?.Segment?.Signatures.Where(s => s.Signature == sig).Select(s => s.Count).FirstOrDefault() ?? 0;
                var plugins = new List<string>();
                if (Count(SF) > 0 && (c.Heavy || Count(SF) <= QuickRecords)) plugins.Add(SF);
                if (plugins.Count == 0) plugins.AddRange(Smaller.Where(p => Count(p) > 0));
                if (plugins.Count == 0) { none.Add(root); continue; }
                types++;
                foreach (string plugin in plugins)
                {
                    string said = GSqlTests.RawAgainstMutagen(c, plugin, view.Types[0]);
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
