// GAPS S152 (J114): translated text columns read from the records' own bytes (the string ID, the plugin's strings tables
// through the lookup Mutagen's reads of it use) against what DirectColumn gives from Mutagen's own TranslatedString, in
// every language Starfield ships and one it doesn't, with the plugins read in English and in German (Mutagen's
// TargetLanguage: what TranslatedString.String gives when the text has one language or none). Localized plugins
// (Starfield.esm, the ShatteredSpace.esm DLC) and plugins that keep their texts in themselves (darkstar.esm,
// BountyHunter_Outfit_Z.esm).
using System.Diagnostics;
using Mutagen.Bethesda.Strings;
using SFSearch.Index;
using SFSearch.Records;
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class TextBytesTests
{
    /// <summary>The plugins, each with the types whose translated columns are held, and whether it is localized.</summary>
    static readonly (string Plugin, bool Localized, string[] Types)[] Plugins =
    {
        ("Starfield.esm", true, new[] { "Cell", "Worldspace", "PlacedObject", "Weapon", "Book" }),
        ("ShatteredSpace.esm", true, new[] { "Cell", "Worldspace", "PlacedObject", "Armor" }),
        ("darkstar.esm", false, new[] { "Cell", "PlacedObject", "Weapon", "Armor", "Book" }),
        ("BountyHunter_Outfit_Z.esm", false, new[] { "Armor" }),
    };

    public static string Run(Ctx c)
    {
        string rule = Rule();
        // Every language Starfield ships, and Russian: one Mutagen knows and Starfield doesn't ship
        var asked = TextLanguages.Shipped.Select(s => s.Name).Append("Russian").ToArray();
        var said = new List<string>();
        var clock = Stopwatch.StartNew();
        long values = 0, nonNull = 0, byLanguage = 0, fromTables = 0;
        foreach (var target in new[] { Language.English, Language.German })
        {
            var engine = target == Language.English ? c.Records : new RecordEngine(c.Index, c.Schema, Path.Combine(c.Work, "verify-" + target), c.Data, target);
            try
            {
                foreach (var (plugin, localized, types) in Plugins)
                {
                    var p = c.Index.Plugin(plugin) ?? throw new TestFailed($"{plugin} isn't in the Data folder");
                    var session = engine.Sessions.Get(plugin);
                    Check(session.Localized == localized, $"{plugin}: localized {session.Localized}, expected {localized}");
                    Check(session.TargetLanguage == target, $"{plugin}: read in {session.TargetLanguage}, expected {target}");
                    var raws = asked.Select(l => new RawPlugin(p, session, l)).ToArray();
                    foreach (string type in types)
                    {
                        var typed = c.Engine.Views.Catalog(type).Typed;
                        var cols = typed.Columns.Where(col => col.Kind == "String" && col.Expr.Contains("sfs_text", StringComparison.Ordinal) && RawColumn.For(typed, col, c.Schema) != null).ToList();
                        Check(cols.Count > 0, $"{type}: no translated column is read from the bytes");
                        var readers = cols.Select(col => new RawRecordColumns(new[] { RawColumn.For(typed, col, c.Schema)! })).ToArray();
                        var subs = cols.Select(col => col.Row!.Subrecord!).Distinct().Select(s => System.Text.Encoding.ASCII.GetBytes(s)).ToArray();
                        var direct = cols.Select(col => asked.Select(l => DirectColumn.For(col, l)!).ToArray()).ToArray();
                        var entries = p.Segment!.EntriesOf(c.Schema.SignatureOf(type)!).ToArray();
                        if (entries.Length == 0) continue;
                        long typeValues = 0, typeRecords = 0, problems = 0;
                        var first = new List<string>();
                        engine.OpenForThreads(entries.Select(e => new RecordRef(p.Id, e)));
                        foreach (string problem in RecordPipeline.Read<string>(new[] { new ReadSet { Plugin = p, Entries = entries } }, (in PipelineRecord r, List<string> output) =>
                        {
                            if (r.Problem != PipelineProblem.None) return;
                            // A record without the columns' subrecords gives NULL both ways (the typed checks hold that): the
                            // records that hold one are read through Mutagen, and every 500th of the rest
                            bool holds = false;
                            foreach (var s in subs) holds |= r.Data.IndexOf((ReadOnlySpan<byte>)s) >= 0;
                            if (!holds && r.EntryIndex % 500 != 0) return;
                            Interlocked.Increment(ref typeRecords);
                            object rec;
                            try { rec = engine.Load(r.Ref).Record; }
                            catch (Exception e) { output.Add($"{c.Index.Describe(r.Ref).Label}: Mutagen couldn't read it ({e.Message})"); return; }
                            string label = c.Index.Describe(r.Ref).Label;
                            // As the record reader sets it for each record: a plugin that isn't localized gives a tagged
                            // text from its own strings tables (GAPS S190)
                            DirectColumn.TextSession = session.Localized ? null : session;
                            for (int i = 0; i < cols.Count; i++)
                                for (int k = 0; k < asked.Length; k++)
                                {
                                    var one = new object?[1];
                                    if (!readers[i].Read(r.Head, r.Data, raws[k], one)) { output.Add($"{label} {type}.{cols[i].Name} in {asked[k]}: not read from the bytes"); continue; }
                                    object? want = direct[i][k].Read(rec);
                                    Interlocked.Increment(ref typeValues);
                                    if (want != null) Interlocked.Increment(ref nonNull);
                                    if (!Equals(one[0], want)) output.Add($"{label} {type}.{cols[i].Name} in {asked[k]} (read in {target}): from the bytes {Show(one[0])}, Mutagen {Show(want)}");
                                    else if (k > 0 && !Equals(want, direct[i][0].Read(rec))) { Interlocked.Increment(ref byLanguage); if (!localized) Interlocked.Increment(ref fromTables); }
                                }
                        }, new PipelineOptions { Data = PipelineData.Plain, CostPerByte = PipelineOptions.MutagenCostPerByte, PieceRecords = 256 }))
                        {
                            problems++;
                            if (first.Count < 5) first.Add(problem);
                        }
                        Check(problems == 0, $"{type} of {plugin} read in {target}: {problems:N0} difference(s). The first: {string.Join(" | ", first)}");
                        values += typeValues;
                        said.Add($"{plugin} {type} ({target}) {typeRecords:N0} records × {cols.Count} × {asked.Length}");
                    }
                }
            }
            finally { if (!ReferenceEquals(engine, c.Records)) engine.Dispose(); }
        }
        Check(nonNull > 0 && byLanguage > 0, $"the records held have texts ({nonNull:N0}) that differ by language ({byLanguage:N0})");
        // S190: a plugin that isn't localized but ships strings tables (darkstar.esm) gives its tables' texts both ways
        Check(fromTables > 0, "no text of a plugin that isn't localized differed by language: darkstar.esm's own strings tables weren't asked");
        return $"{rule}; S152: {values:N0} translated values from the bytes equal to Mutagen's, {nonNull:N0} of them a text, {byLanguage:N0} differing from the first language's, {fromTables:N0} of those from the own strings tables of a plugin that isn't localized ({string.Join("; ", said)}) in {clock.Elapsed.TotalSeconds:F0} s";
    }

    /// <summary>
    /// The language rule on its own, against Mutagen's own TranslatedString (FromStringsKey over a lookup of three
    /// languages' tables) read by DirectColumn's rule: an ID held by every subset of the three tables, Mutagen reading in
    /// two of them, every language asked (one with no table, one that isn't Mutagen's), each table asked only as far as
    /// the answer needs. The real plugins hold their IDs in every language, so they don't reach the cases of one or none.
    /// </summary>
    static string Rule()
    {
        var langs = new[] { Language.English, Language.German, Language.French };
        int cases = 0;
        for (int held = 0; held < 8; held++)
        {
            var lookup = new Tables(langs.Where((l, i) => (held >> i & 1) != 0).ToDictionary(l => l, l => $"text in {l}"), langs);
            foreach (var target in new[] { Language.English, Language.German })
                foreach (string asked in new[] { "English", "German", "French", "Polish", "Klingon" })
                {
                    var mutagen = TranslatedString.FromStringsKey(7, StringsSource.Normal, target, lookup);
                    string? want = DirectColumn.Text(mutagen, asked);
                    Language? a = Enum.TryParse<Language>(asked, out var x) ? x : null;
                    string? got = RawPlugin.LookupText(lookup, StringsSource.Normal, target, a, 7);
                    Check(got == want, $"held in {string.Join("+", lookup.Held)} read in {target}, asked {asked}: the rule {got ?? "NULL"}, Mutagen {want ?? "NULL"}");
                    cases++;
                }
        }
        return $"the language rule equals Mutagen's TranslatedString in {cases} cases";
    }

    /// <summary>Strings tables of some languages, holding ID 7 in some of them.</summary>
    sealed class Tables(Dictionary<Language, string> held, Language[] languages) : IStringsFolderLookup
    {
        public IEnumerable<Language> Held => held.Keys;
        public IReadOnlyCollection<Language> AvailableLanguages(StringsSource source) => languages;
        public bool TryLookup(StringsSource source, Language language, uint key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out string str)
        {
            str = null;
            return key == 7 && held.TryGetValue(language, out str);
        }
        public TranslatedString CreateString(StringsSource source, uint key, Language targetLanguage) => TranslatedString.FromStringsKey(key, source, targetLanguage, this);
    }

    static string Show(object? v) => v == null ? "NULL" : $"'{Cut(v.ToString() ?? "", 120)}' ({v.GetType().Name})";
}
