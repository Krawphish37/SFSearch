// GAPS S190: a plugin that isn't localized but ships strings tables (darkstar.esm: each text behind the Creation Kit's
// <ID=…> tag, 27 tables in its archive). Asked in another language than English, a tagged text is that language's
// table's text when the tables hold its ID; in English, and where they don't, the plugin's own. Held here against a
// strings lookup of the test's own (Mutagen's, over the same Data folder), through the JSON (text() on Json and on a
// view's column) and through the query's language; text-bytes holds the bytes against Mutagen's object the same way.
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Strings;
using Mutagen.Bethesda.Strings.DI;
using SFSearch.Records;
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class TagTextTests
{
    const string DS = "darkstar.esm";

    sealed class Utf8Or1252 : IMutagenEncodingProvider
    {
        public IMutagenEncoding GetEncoding(GameRelease release, Language language) => MutagenEncoding._utf8_1252;
    }

    public static string Run(Ctx c)
    {
        if (c.Index.Plugin(DS) == null) throw new TestFailed($"{DS} isn't in the Data folder");
        var lookup = StringsFolderLookupOverlay.TypicalFactory(GameRelease.Starfield, ModKey.FromFileName(DS), c.Data, new StringsReadParameters { EncodingProvider = new Utf8Or1252() });
        string? Table(uint id, Language l)
        {
            foreach (var source in new[] { StringsSource.Normal, StringsSource.DL, StringsSource.IL })
                if (lookup.TryLookup(source, l, id, out var text) && text != null) return text;
            return null;
        }

        // One text, as it is known to stand in the plugin and in its German table
        var known = c.Ok("SELECT Name, text(Name, 'de'), text(Name, 'ja'), text(Json -> '$.Name', 'de'), json(Json -> '$.Name') FROM Keyword WHERE FormKey = '000066:darkstar.esm'", DS);
        Check(known.Rows.Count == 1, "000066:darkstar.esm is a keyword of darkstar.esm");
        var row = known.Rows[0];
        Check((string?)row[0] == "Vile Weapon Crafting", $"its name in English is the plugin's own: {row[0]}");
        Check((string?)row[1] == "Böswillige Waffenherstellung" && (string?)row[3] == (string?)row[1], $"its name in German is the German table's: {row[1]} / {row[3]}");
        Check((string?)row[2] == "Vile Weapon Crafting", $"its name in Japanese, whose table says what the plugin says: {row[2]}");
        string json = (string?)row[4] ?? "";
        Check(json.Contains("\"Value\":\"<ID=00008A14>Vile Weapon Crafting\"", StringComparison.Ordinal) && json.Contains("\"Tables\":{", StringComparison.Ordinal) && json.Contains("\"German\":\"Böswillige Waffenherstellung\"", StringComparison.Ordinal)
            && !json.Contains("\"Japanese\"", StringComparison.Ordinal) && !json.Contains("\"English\":", StringComparison.Ordinal), $"its Json keeps the plugin's text and names the tables that say another: {json}");
        Check(known.Notes.Any(n => n.Contains("own strings tables", StringComparison.Ordinal) && n.Contains("German", StringComparison.Ordinal)), $"a note says where the German text is from: {string.Join(" / ", known.Notes)}");

        // Every tagged name of some types, in four languages: on the Json, and with the query asked in the language
        long texts = 0, fromTables = 0, changed = 0;
        var said = new List<string>();
        foreach (string type in new[] { "Weapon", "Armor", "Keyword", "ConstructibleObject", "Book", "Activator" })
        {
            var stored = c.Ok($"SELECT FormKey, Json ->> '$.Name.Value', Name, Json ->> '$.Name.Tables.English' FROM {type} WHERE Json ->> '$.Name.Value' LIKE '<ID=%' ORDER BY FormKey", DS);
            Check(stored.Rows.Count > 0, $"{type} of {DS} has tagged names");
            var ids = new Dictionary<string, (uint Id, string Own)>(StringComparer.Ordinal);
            foreach (var r in stored.Rows)
            {
                string text = (string)r[1]!;
                Check(TagTexts.TryTag(text, out uint id, out int after), $"{r[0]}: the tag of '{text}'");
                string own = text[after..];
                ids[(string)r[0]!] = (id, own);
                // In English the plugin's own text, whatever its English table says
                Check((string?)r[2] == own, $"{type} {r[0]} in English: '{r[2]}', the plugin stores '{own}'");
                string? english = Table(id, Language.English);
                Check((string?)r[3] == (english != null && english != own ? english : null), $"{type} {r[0]}: Json's English table text '{r[3]}', the table's '{english}', the plugin's '{own}'");
                if (r[3] != null) changed++;
            }
            foreach (var (language, code) in new[] { (Language.German, "de"), (Language.French, "fr"), (Language.Spanish, "es"), (Language.Japanese, "ja") })
            {
                // (text() on the Json keeps the plugin's own text as stored, tag and all: display() takes the tag off)
                var onJson = c.Ok($"SELECT FormKey, display(text(Json -> '$.Name', '{code}')), text(Name, '{code}') FROM {type} WHERE Json ->> '$.Name.Value' LIKE '<ID=%' ORDER BY FormKey", DS);
                var asked = Must(c.Engine.Run(new QueryRequest { Sql = $"SELECT FormKey, Name FROM {type} ORDER BY FormKey", Plugins = new[] { DS }, Language = code, PageRows = 1_000_000, MaxChars = int.MaxValue }), $"{type} in {code}");
                var byLanguage = asked.Rows.ToDictionary(r => (string)r[0]!, r => (string?)r[1], StringComparer.Ordinal);
                Check(onJson.Rows.Count == ids.Count, $"{type} in {code}: {onJson.Rows.Count} rows, {ids.Count} tagged names");
                foreach (var r in onJson.Rows)
                {
                    var (id, own) = ids[(string)r[0]!];
                    string want = Table(id, language) ?? own;
                    Check((string?)r[1] == want && (string?)r[2] == want, $"{type} {r[0]} in {code}: text(Json) '{r[1]}', text(Name) '{r[2]}', the table '{Table(id, language)}', the plugin '{own}'");
                    Check(byLanguage.TryGetValue((string)r[0]!, out var name) && name == want, $"{type} {r[0]} with the query in {code}: '{name}', expected '{want}'");
                    texts++;
                    if (want != own) fromTables++;
                }
            }
            said.Add($"{type} {ids.Count:N0}");
        }
        Check(fromTables > 100, $"only {fromTables:N0} of {texts:N0} texts came from a table");
        // (an activator named "Part Disable: Cargo/Fuel" whose English table still says "Ship Part Disabler")
        Check(changed > 0, "no name was changed after the tables were made: the plugin's own text against its English table wasn't held");
        return $"S190: {DS}'s tagged names ({string.Join(", ", said)}) in German, French, Spanish and Japanese, on the Json, on the view's column and with the query in the language: {texts:N0} texts equal to the plugin's own strings tables' where those hold the ID ({fromTables:N0} another text than the plugin's), the plugin's own text in English ({changed:N0} of them changed since the tables were made, which the Json says)";
    }
}
