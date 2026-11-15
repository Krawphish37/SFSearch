// text(value, language) and the language argument, held against Mutagen's own lookup by language (fix A1, 2026-11-12:
// text(Name,'ja') on the Eon gave "Eon" with no word, where the record holds イーオン).
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Strings;
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class TextTests
{
    const string SF = "Starfield.esm";
    /// <summary>A plugin that keeps its text in itself, in English (no strings files).</summary>
    const string OneLanguage = "BountyHunter_Outfit_Z.esm";

    static bool IsTextNote(string n) => n.StartsWith("Some texts", StringComparison.Ordinal) || n.StartsWith("Starfield ships no", StringComparison.Ordinal) || n.StartsWith("text(", StringComparison.Ordinal);

    public static string Languages(Ctx c)
    {
        // Every language Starfield ships, by each of its spellings
        var spellings = new (Language L, string[] Ways)[]
        {
            (Language.English, new[] { "en", "English", "EN" }), (Language.German, new[] { "de", "German" }), (Language.Spanish, new[] { "es", "Spanish" }),
            (Language.French, new[] { "fr", "French" }), (Language.Italian, new[] { "it", "Italian" }), (Language.Japanese, new[] { "ja", "jp", "Japanese" }),
            (Language.Polish, new[] { "pl", "Polish" }), (Language.Portuguese_Brazil, new[] { "ptbr", "pt-br", "pt", "Portuguese_Brazil" }),
            (Language.ChineseSimplified, new[] { "zhhans", "zh", "zh-hans", "cn", "Chinese", "ChineseSimplified" }),
        };
        Check(spellings.Length == TextLanguages.Shipped.Count, "every shipped language is tested");

        // The Eon, and a named armor and book picked the same way every time
        string armor = (string)c.Ok("SELECT FormKey FROM Armor WHERE Name <> '' ORDER BY EditorID LIMIT 1", SF).Rows[0][0]!;
        string book = (string)c.Ok("SELECT FormKey FROM Book WHERE Name <> '' ORDER BY EditorID LIMIT 1", SF).Rows[0][0]!;
        var mod = c.Mod(SF);
        var picks = new (string View, string FormKey, ITranslatedStringGetter? Name)[]
        {
            ("Weapon", "0476C4:Starfield.esm", mod.Weapons.First(w => w.FormKey == FormKey.Factory("0476C4:Starfield.esm")).Name),
            ("Armor", armor, mod.Armors.First(w => w.FormKey == FormKey.Factory(armor)).Name),
            ("Book", book, mod.Books.First(w => w.FormKey == FormKey.Factory(book)).Name),
        };
        int asked = 0, differ = 0;
        foreach (var (view, fk, name) in picks)
        {
            Check(name != null, $"{fk} has a name");
            foreach (var (lang, ways) in spellings)
            {
                Check(name!.TryLookup(lang, out var want), $"Mutagen has {lang} text for {fk}");
                if (lang != Language.English && want != name.String) differ++;
                foreach (string way in ways)
                {
                    // On the view's own text column (what the agent wrote), on the Json, and through the language argument
                    var a = c.Ok($"SELECT text(Name, '{way}'), text(w.Name, '{way}') AS Aliased, text(Json -> '$.Name', '{way}') FROM {view} w WHERE FormKey = '{fk}'", SF);
                    Check(a.Rows.Count == 1, $"{view} {fk}: one row");
                    for (int i = 0; i < 3; i++) Check(Equals(a.Rows[0][i], want), $"{view} {fk} in '{way}', column {i + 1}: '{a.Rows[0][i]}', Mutagen: '{want}'");
                    Check(a.Columns[0] == $"text(Name, '{way}')", $"the column keeps the name the SQL gave it: {a.Columns[0]}");
                    Check(!a.Notes.Any(IsTextNote),$"{view} {fk} in '{way}': no note about the language: {string.Join(" / ", a.Notes)}");
                    // (name(FormKey) reads the version the game uses, which for the Eon is a mod's English-only override)
                    var b = Must(c.Engine.Run(new QueryRequest { Sql = $"SELECT Name FROM {view} WHERE FormKey = '{fk}'", Plugins = new[] { SF }, Language = way }), "language " + way);
                    Check(Equals(b.Rows[0][0], want), $"{view} {fk} with language '{way}': '{b.Rows[0][0]}', Mutagen: '{want}'");
                    asked++;
                }
            }
        }
        Check(differ >= 3, $"the records picked have names that differ by language ({differ})");

        // An unknown language: an error that names the languages, before anything is read; the same for the argument
        foreach (string sql in new[] { "SELECT text(Name, 'xx') FROM Weapon", "SELECT name(FormKey, 'klingon') FROM Weapon", "SELECT text(Json -> '$.Name', '5') FROM Weapon" })
        {
            var e = c.Sql(sql, SF);
            Check(e.Error != null && e.Error.Contains("isn't a language", StringComparison.Ordinal) && e.Error.Contains("Japanese (ja)", StringComparison.Ordinal) && e.Fix != null, $"{sql}: {e.Error}");
        }
        var bad = c.Engine.Run(new QueryRequest { Sql = "SELECT Name FROM Weapon", Plugins = new[] { SF }, Language = "xx" });
        Check(bad.Error != null && bad.Error.Contains("'xx' isn't a language", StringComparison.Ordinal), $"language xx: {bad.Error}");

        // A language Mutagen knows and Starfield doesn't ship: NULL, and a note that says so
        var ru = c.Ok("SELECT text(Name, 'ru') FROM Weapon WHERE FormKey = '0476C4:Starfield.esm'", SF);
        Check(ru.Rows[0][0] == null && ru.Notes.Any(n => n.StartsWith("Starfield ships no Russian text", StringComparison.Ordinal)), $"Russian: '{ru.Rows[0][0]}' / {string.Join(" / ", ru.Notes)}");

        // A plugin with its text in English only: that text, and a note
        var one = c.Ok("SELECT EditorID, Name, text(Name, 'ja') FROM Armor WHERE Name <> '' ORDER BY 1 LIMIT 1", OneLanguage);
        Check(Equals(one.Rows[0][1], one.Rows[0][2]) && one.Notes.Any(n => n.Contains("in English only", StringComparison.Ordinal) && n.Contains("no Japanese text", StringComparison.Ordinal)),
            $"{OneLanguage}: '{one.Rows[0][1]}' / '{one.Rows[0][2]}' / {string.Join(" / ", one.Notes)}");

        // A plain text from a subquery can't be told in another language: NULL and the way to ask
        var plain = c.Ok("SELECT text(n, 'ja') FROM (SELECT Name AS n FROM Weapon WHERE FormKey = '0476C4:Starfield.esm')", SF);
        Check(plain.Rows[0][0] == null && plain.Notes.Any(n => n.Contains("text(Json -> '$.Name', 'Japanese')", StringComparison.Ordinal)), $"plain text: '{plain.Rows[0][0]}' / {string.Join(" / ", plain.Notes)}");

        return $"{asked} asks (3 records × 9 languages × each spelling, on the column, an alias and Json, and as the language argument) equal Mutagen's lookup; {differ} of the 24 non-English names differ from English; an unknown language is an error naming the 9; Russian gives NULL with a note; {OneLanguage} gives its English text with a note; a plain text gives NULL with the way to ask";
    }
}
