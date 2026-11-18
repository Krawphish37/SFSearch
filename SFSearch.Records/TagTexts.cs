// The translations of a plugin that isn't localized but ships strings tables all the same (GAPS S190).
//
// The Creation Kit writes a tag in front of each text of such a plugin (<ID=00008A14>Vile Weapon Crafting): the ID the
// text has in the strings tables the kit exports for it. darkstar.esm keeps its 34,992 texts that way and ships 27
// tables, nine languages, in its archive. Mutagen reads the plugin's own text, as it must: the plugin isn't flagged
// localized. The tables are read here, by Mutagen's own strings lookup, and asked by the tag's ID:
//  - a text asked in English is the plugin's own text, as before. The tables can't say which language the plugin's
//    own text is in; it is taken to be English, the language the kit writes in unless told otherwise, so an English
//    table's older wording never takes the place of what the plugin says now (65 of darkstar's texts were changed
//    after its tables were made);
//  - a text asked in another language is that language's table's text, when the plugin's tables hold the ID;
//  - a record's JSON, in the query's Json and the record views, says beside the text each language whose table holds
//    another text than the plugin does ("Tables": {"German": "…"}); English is among them only for a text changed
//    after the tables were made.
// Whether the game shows a table's text for such a text wasn't looked into: the plugin's own text is what it stores.
using System.Buffers;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Strings;
using Mutagen.Bethesda.Strings.DI;

namespace SFSearch.Records;

public sealed class TagTexts
{
    readonly IStringsFolderLookup _lookup;
    readonly Language[][] _languages;
    static readonly StringsSource[] Sources = { StringsSource.Normal, StringsSource.DL, StringsSource.IL };

    /// <summary>The languages one of the plugin's tables is in.</summary>
    public IReadOnlyList<Language> Languages { get; }
    /// <summary>The language the plugin's own text is given for (see the file's comment).</summary>
    public const Language Own = Language.English;
    /// <summary>How many texts were given from a table so far (a read's cost and notes say so).</summary>
    public long Given;

    TagTexts(IStringsFolderLookup lookup, Language[][] languages)
    {
        _lookup = lookup; _languages = languages;
        Languages = languages.SelectMany(l => l).Distinct().OrderBy(l => l.ToString(), StringComparer.Ordinal).ToArray();
    }

    /// <summary>The tables of a plugin that isn't localized; null when it has none (most plugins).</summary>
    public static TagTexts? For(ModKey plugin, string folder, StringsReadParameters read)
    {
        try
        {
            // A table's bytes as UTF-8 where they are UTF-8, else Windows-1252 (darkstar's German table): Starfield's
            // tables are UTF-8, and one saved in the older games' encoding still reads as its letters
            var lookup = StringsFolderLookupOverlay.TypicalFactory(GameRelease.Starfield, plugin, folder, read with { EncodingProvider = Lenient.Provider });
            var languages = Sources.Select(s => lookup.AvailableLanguages(s).ToArray()).ToArray();
            return languages.All(l => l.Length == 0) ? null : new TagTexts(lookup, languages);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { return null; }
    }

    /// <summary>The ID of the tag in front of a text (&lt;ID=00008A14&gt;) and where the text starts after it.</summary>
    public static bool TryTag(ReadOnlySpan<char> text, out uint id, out int after)
    {
        id = 0; after = 0;
        if (text.Length < 6 || !text.StartsWith("<ID=", StringComparison.Ordinal)) return false;
        int end = text.IndexOf('>');
        if (end < 5 || end > 12) return false;
        if (!uint.TryParse(text[4..end], System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out id)) return false;
        after = end + 1;
        return true;
    }

    /// <summary>The text a language's tables hold under an ID (the names' table, then the two others'); null when none does.</summary>
    public string? TableText(uint id, Language language)
    {
        for (int s = 0; s < Sources.Length; s++)
        {
            if (Array.IndexOf(_languages[s], language) < 0) continue;
            if (_lookup.TryLookup(Sources[s], language, id, out var text) && text != null) return text;
        }
        return null;
    }

    /// <summary>The text to give for a stored text asked in <paramref name="asked"/>: the table's, when the stored
    /// text has a tag, the language isn't the plugin's own (<see cref="Own"/>) and its tables hold the ID; else null
    /// (the stored text stands).</summary>
    public string? Translate(string? stored, Language asked)
    {
        if (stored == null || asked == Own || !TryTag(stored, out uint id, out _)) return null;
        if (TableText(id, asked) is not { } text) return null;
        Interlocked.Increment(ref Given);
        return text;
    }

    static readonly byte[] Tag = "<ID="u8.ToArray();

    /// <summary>
    /// A record's JSON with, in each translated text that has a tag (<c>{"TargetLanguage": …, "Value": "&lt;ID=…&gt;…"}</c>),
    /// the languages whose table holds another text than the plugin's: <c>"Tables": {"German": "…"}</c>. Null when
    /// nothing was added (no tagged text, or every table says what the plugin says).
    /// </summary>
    public byte[]? WithTables(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IndexOf(Tag) < 0) return null;
        using var doc = JsonDocument.Parse(utf8.ToArray());
        var buffer = new ArrayBufferWriter<byte>(utf8.Length + 256);
        bool added = false;
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            Write(doc.RootElement, w, ref added);
        return added ? buffer.WrittenSpan.ToArray() : null;
    }

    void Write(JsonElement e, Utf8JsonWriter w, ref bool added)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
            {
                w.WriteStartObject();
                var tables = e.TryGetProperty("TargetLanguage", out _) && !e.TryGetProperty("Tables", out _)
                    && e.TryGetProperty("Value", out var stored) && stored.ValueKind == JsonValueKind.String ? Differing(stored.GetString()!) : null;
                foreach (var p in e.EnumerateObject())
                {
                    w.WritePropertyName(p.Name);
                    Write(p.Value, w, ref added);
                }
                if (tables is { Count: > 0 })
                {
                    w.WriteStartObject("Tables");
                    foreach (var (language, text) in tables) w.WriteString(language, text);
                    w.WriteEndObject();
                    added = true;
                }
                w.WriteEndObject();
                break;
            }
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var x in e.EnumerateArray()) Write(x, w, ref added);
                w.WriteEndArray();
                break;
            default:
                e.WriteTo(w);
                break;
        }
    }

    /// <summary>The same for a record's JSON held as nodes (the record views). <paramref name="tables"/> is asked
    /// only when the record has a tagged text: making a plugin's lookup looks through Data's archives.</summary>
    public static void AddTables(System.Text.Json.Nodes.JsonNode? node, Func<TagTexts?> tables)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject o:
            {
                if (o.ContainsKey("TargetLanguage") && !o.ContainsKey("Tables") && o["Value"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var stored)
                    && stored.StartsWith("<ID=", StringComparison.Ordinal) && tables()?.Differing(stored) is { Count: > 0 } differing)
                {
                    var t = new System.Text.Json.Nodes.JsonObject();
                    // (values backed by a JsonElement, as the serializer's own are: the views read them so)
                    foreach (var (language, text) in differing) t[language] = System.Text.Json.Nodes.JsonValue.Create(JsonSerializer.SerializeToElement(text));
                    o["Tables"] = t;
                }
                foreach (var (_, child) in o.ToList()) AddTables(child, tables);
                break;
            }
            case System.Text.Json.Nodes.JsonArray a:
                foreach (var child in a) AddTables(child, tables);
                break;
        }
    }

    /// <summary>The languages whose table holds, under the stored text's ID, another text than the stored one (its
    /// tag left out), in the languages' order by name; null when the text has no tag.</summary>
    Dictionary<string, string>? Differing(string stored)
    {
        if (!TryTag(stored, out uint id, out int after)) return null;
        var own = stored.AsSpan(after);
        Dictionary<string, string>? found = null;
        foreach (var language in Languages)
            if (TableText(id, language) is { } text && !own.SequenceEqual(text))
                (found ??= new(StringComparer.Ordinal))[language.ToString()] = text;
        return found;
    }

    /// <summary>Mutagen's UTF-8 with Windows-1252 for bytes that aren't UTF-8, for every language.</summary>
    sealed class Lenient : IMutagenEncodingProvider
    {
        public static readonly Lenient Provider = new();
        public IMutagenEncoding GetEncoding(GameRelease release, Language language) => MutagenEncoding._utf8_1252;
    }
}
