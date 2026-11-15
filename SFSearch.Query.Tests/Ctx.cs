// What every test stands on: the header index, the records layer, one query engine, and the helpers that turn a
// SQL answer and a Mutagen value into the same text so the two can be held against each other.
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Strings;
using SFSearch.Index;
using SFSearch.Query;
using SFSearch.Records;

namespace SFSearch.Query.Tests;

sealed class TestFailed(string message) : Exception(message);

sealed class Ctx : IDisposable
{
    public string Data { get; }
    public string Work { get; }
    public string? Report { get; }
    public bool Heavy { get; }
    public RecordSchema Schema { get; }
    public HeaderIndex Index { get; }
    public RecordEngine Records { get; }
    public QueryEngine Engine { get; }
    readonly List<QueryEngine> _engines = new();

    public Ctx(string data, string work, string? report, bool heavy, string sqlFolder = "sql")
    {
        Data = data; Work = work; Report = report; Heavy = heavy;
        Schema = new RecordSchema(Path.Combine(AppContext.BaseDirectory, "schema.db"));
        Index = new HeaderIndex(PluginSource.Folder(data), new IndexOptions { ScratchFolder = Path.Combine(work, "index"), VersionText = $"sfsquery-tests 1 / mutagen {Schema.MutagenVersion} / schema {Schema.Version}", Refusals = new SFSearch.Records.MutagenRefusals() });
        Index.Refresh();
        Records = new RecordEngine(Index, Schema, Path.Combine(work, "verify"), data);
        Engine = NewEngine(sqlFolder);
    }

    public QueryEngine NewEngine(string folder)
    {
        var e = new QueryEngine(Records, new QueryOptions { ScratchFolder = Path.Combine(Work, folder) });
        _engines.Add(e);
        return e;
    }

    public void Dispose()
    {
        foreach (var e in _engines) e.Dispose();
        Records.Dispose(); Index.Dispose();
    }

    /// <summary>A query over plugins, every row in one page.</summary>
    public QueryAnswer Sql(string sql, params string[] plugins) => Engine.Run(new QueryRequest { Sql = sql, Plugins = plugins, PageRows = 1_000_000 });
    public QueryAnswer Lo(string sql, ScopeKind scope, bool allowHeavy = false) => Engine.Run(new QueryRequest { Sql = sql, Scope = scope, PageRows = 1_000_000, AllowHeavy = allowHeavy });

    /// <summary>The same, and the answer must be rows: an error or a refusal fails the test.</summary>
    public QueryAnswer Ok(string sql, params string[] plugins) => Must(Sql(sql, plugins), sql);
    public QueryAnswer OkLo(string sql, ScopeKind scope) => Must(Lo(sql, scope), sql);

    public static QueryAnswer Must(QueryAnswer a, string sql)
    {
        if (a.Error != null) throw new TestFailed($"\"{sql}\" failed: {a.Error} {a.Fix}");
        if (a.NotRun != null) throw new TestFailed($"\"{sql}\" wasn't run: {a.NotRun.What}");
        return a;
    }

    /// <summary>The plugin as Mutagen has it: the overlay the records layer holds open.</summary>
    public IStarfieldModGetter Mod(string plugin) => Records.Sessions.Get(plugin).Mod;

    public void Save(string name, IEnumerable<string> lines)
    {
        if (Report != null) File.WriteAllLines(Path.Combine(Report, name), lines);
    }

    // ------------------------------------------------------------------ One text for both sides
    /// <summary>A value of a SQL answer as text.</summary>
    public static string Cell(object? v) => v switch
    {
        null => "",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        byte[] b => "0x" + Convert.ToHexString(b),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    public static List<string> Rows(QueryAnswer a) => a.Rows.Select(r => string.Join(" | ", r.Select(Cell))).ToList();

    /// <summary>A Mutagen value as the same text a typed view gives for it.</summary>
    public static string Fmt(object? v) => v switch
    {
        null => "",
        string s => s,
        bool b => b ? "1" : "0",
        // A float is written to JSON with the fewest digits that give the same float back
        float f => double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        // A typed view's text column gives the text without the Creation Kit's tag (milestone M8)
        ITranslatedStringGetter t => Functions.Display(t.String ?? "") ?? "",
        IFormLinkGetter l => l.IsNull ? "" : l.FormKey.ToString(),
        FormKey k => k.IsNull ? "" : k.ToString(),
        Enum e => e.ToString(),
        IFormattable n => n.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    public static string Row(params object?[] values) => string.Join(" | ", values.Select(Fmt));

    /// <summary>A property of a Mutagen object by name ("ObjectBounds.First"), through its public getter.</summary>
    public static object? P(object? o, string path)
    {
        foreach (string name in path.Split('.'))
        {
            if (o == null) return null;
            var t = o.GetType();
            var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? t.GetInterfaces().Select(i => i.GetProperty(name)).FirstOrDefault(p => p != null)
                ?? throw new TestFailed($"Mutagen's {t.Name} has no property {name}");
            o = prop.GetValue(o);
        }
        return o;
    }

    public static IEnumerable<object?> List(object? o, string path) => P(o, path) is IEnumerable e and not string ? e.Cast<object?>() : Enumerable.Empty<object?>();

    public static string Type(object record) => RecordSerializer.TypeName(record);

    /// <summary>A record of a plugin by its FormKey text, looked for among the records of one Mutagen type.</summary>
    public T Find<T>(string plugin, string formKey) where T : class, IMajorRecordGetter
    {
        var key = FormKey.Factory(formKey);
        return Mod(plugin).EnumerateMajorRecords<T>().FirstOrDefault(r => r.FormKey == key)
            ?? throw new TestFailed($"Mutagen finds no {typeof(T).Name} {formKey} in {plugin}");
    }

    /// <summary>The EditorID of a record as Mutagen has it, looked for in the plugin that owns the FormKey.</summary>
    public string EdidOf<T>(FormKey key) where T : class, IMajorRecordGetter
    {
        var owner = Index.Plugin(key.ModKey.FileName) ?? throw new TestFailed($"{key.ModKey.FileName} isn't in Data");
        return Mod(owner.Name).EnumerateMajorRecords<T>().FirstOrDefault(r => r.FormKey == key)?.EditorID ?? "";
    }

    /// <summary>Two lists of rows held against each other. Fails with the first rows that differ.</summary>
    public static void Same(string what, IReadOnlyList<string> got, IReadOnlyList<string> want, bool ordered)
    {
        var g = got.ToList(); var w = want.ToList();
        if (!ordered) { g.Sort(StringComparer.Ordinal); w.Sort(StringComparer.Ordinal); }
        if (g.SequenceEqual(w, StringComparer.Ordinal)) return;
        var onlyGot = g.Except(w).Take(3).ToList(); var onlyWant = w.Except(g).Take(3).ToList();
        string detail = onlyGot.Count + onlyWant.Count > 0
            ? $"SQL only: [{string.Join(" ; ", onlyGot)}]  Mutagen only: [{string.Join(" ; ", onlyWant)}]"
            : $"the same rows in another order or number: SQL starts [{string.Join(" ; ", g.Take(3))}], Mutagen starts [{string.Join(" ; ", w.Take(3))}]";
        throw new TestFailed($"{what}: SQL gave {g.Count:N0} rows, Mutagen {w.Count:N0}. {Cut(detail)}");
    }

    public static string Cut(string s, int max = 500) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>JSON text with its spaces and line breaks taken out, so two writers of the same JSON compare equal.</summary>
    public static string Compact(string json) => JsonNode.Parse(json)!.ToJsonString();

    public static void Check(bool ok, string what) { if (!ok) throw new TestFailed(what); }
}
