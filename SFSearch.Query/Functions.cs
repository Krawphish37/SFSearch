// The SQL functions: edid(fk), name(fk), winner(fk), versions(fk), text(value, 'de').
// All but text() answer from the header index; name() reads the record through the records layer.
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Query;

/// <summary>
/// The languages text can be asked in, by Mutagen's name or a short code. Starfield ships nine: every localized
/// plugin in Data has strings files for exactly these (Starfield - Localization.ba2 and each mod's own archive hold
/// <c>_en</c>, <c>_de</c>, <c>_es</c>, <c>_fr</c>, <c>_it</c>, <c>_ja</c>, <c>_pl</c>, <c>_ptbr</c> and <c>_zhhans</c>).
/// Mutagen knows 14 more by name (Russian, Korean, …); they are accepted, and a text that has none of them says so.
/// </summary>
public static class TextLanguages
{
    /// <summary>The languages Starfield ships, Mutagen's name and the code of its strings files, in the game's order.</summary>
    public static readonly IReadOnlyList<(string Name, string Code)> Shipped = new[]
    {
        ("English", "en"), ("German", "de"), ("Spanish", "es"), ("French", "fr"), ("Italian", "it"), ("Japanese", "ja"),
        ("Polish", "pl"), ("Portuguese_Brazil", "ptbr"), ("ChineseSimplified", "zhhans"),
    };

    // Codes and other spellings. Starfield's Chinese is ChineseSimplified (its files are _zhhans), so every spelling of
    // Chinese gives that; Mutagen's own code "cn" stands for the Chinese of older games, which Starfield doesn't ship.
    static readonly Dictionary<string, string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["en-us"] = "English", ["en-gb"] = "English", ["de"] = "German", ["es"] = "Spanish", ["fr"] = "French", ["it"] = "Italian",
        ["ja"] = "Japanese", ["jp"] = "Japanese", ["jpn"] = "Japanese", ["pl"] = "Polish",
        ["pt"] = "Portuguese_Brazil", ["ptbr"] = "Portuguese_Brazil", ["pt-br"] = "Portuguese_Brazil", ["pt_br"] = "Portuguese_Brazil", ["portuguese"] = "Portuguese_Brazil",
        ["zh"] = "ChineseSimplified", ["zhhans"] = "ChineseSimplified", ["zh-hans"] = "ChineseSimplified", ["zh_hans"] = "ChineseSimplified", ["zh-cn"] = "ChineseSimplified",
        ["cn"] = "ChineseSimplified", ["chinese"] = "ChineseSimplified",
        ["esmx"] = "Spanish_Mexico", ["es-mx"] = "Spanish_Mexico", ["ru"] = "Russian", ["ko"] = "Korean", ["tr"] = "Turkish", ["cs"] = "Czech", ["hu"] = "Hungarian",
        ["da"] = "Danish", ["fi"] = "Finnish", ["el"] = "Greek", ["no"] = "Norwegian", ["sv"] = "Swedish", ["ar"] = "Arabic", ["th"] = "Thai",
    };

    /// <summary>Mutagen's name for a language given by that name or by a code ('de', 'German', 'ptbr'). False for
    /// anything else, a number included.</summary>
    public static bool TryName(string? text, out string name)
    {
        name = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        if (Codes.TryGetValue(t, out var n)) { name = n; return true; }
        if (t.All(c => char.IsLetter(c) || c == '_') && Enum.TryParse<Mutagen.Bethesda.Strings.Language>(t, true, out var l)) { name = l.ToString(); return true; }
        return false;
    }

    /// <summary>True for the nine languages Starfield ships.</summary>
    public static bool IsShipped(string name) => Shipped.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>"English (en), German (de), …": the languages Starfield ships, for messages.</summary>
    public static string ShippedText => string.Join(", ", Shipped.Select(s => $"{s.Name} ({s.Code})"));
}

/// <summary>What text() and the text columns met while a query ran, said once each in the answer's notes.</summary>
sealed class TextNotes
{
    /// <summary>Languages asked for that a translated text didn't have, with the languages it did have.</summary>
    public readonly Dictionary<string, SortedSet<string>> Missing = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Languages asked for where the text is stored in one other language only (a plugin with no strings files).</summary>
    public readonly Dictionary<string, HashSet<string>> OneLanguage = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Languages text() was asked for on a plain text, which is in one language already.</summary>
    public readonly HashSet<string> Plain = new(StringComparer.OrdinalIgnoreCase);
    public readonly object Lock = new();
}

static class Languages
{
    /// <summary>Mutagen's name for a language given by that name or by a code. An unknown one stops the query with
    /// the languages Starfield ships.</summary>
    public static string Name(string language) =>
        TextLanguages.TryName(language, out var n) ? n : throw Unknown(language);

    public static QueryException Unknown(string? language) => new(
        $"'{language}' isn't a language. Starfield's text comes in {TextLanguages.ShippedText}",
        "give one of those names or codes, e.g. 'ja' or 'Japanese'");

    /// <summary>
    /// The text of a translated value in one language, found by the language's name and never by its place.
    /// The value is the JSON Mutagen writes: <c>{"TargetLanguage": …, "Values": [{"Language": …, "String": …}]}</c>
    /// for a localized plugin (Starfield.esm and most mods: all nine languages), or <c>{"TargetLanguage": …, "Value": …}</c>
    /// for a plugin that keeps its text in itself, in one language: the game shows that text whatever its language,
    /// so it is given for every language, and <paramref name="notes"/> hears of it.
    /// A language the text doesn't have gives NULL, never another language's text.
    /// A plain text (not a translated value) is already in one language: with <paramref name="plainLanguage"/> the
    /// language it is in, it is given for that language and NULL for any other.
    /// </summary>
    public static string? Text(string? value, string language, TextNotes? notes = null, string? plainLanguage = null)
    {
        if (value == null) return null;
        var s = value.AsSpan().TrimStart();
        if (s.Length == 0 || s[0] != '{' && s[0] != '"') return Plain(value, language, notes, plainLanguage);
        try
        {
            using var doc = JsonDocument.Parse(value);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String) return Plain(root.GetString(), language, notes, plainLanguage);
            if (root.ValueKind != JsonValueKind.Object) return Plain(value, language, notes, plainLanguage);
            if (root.TryGetProperty("Values", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in values.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("Language", out var l) && string.Equals(l.GetString(), language, StringComparison.OrdinalIgnoreCase))
                        return e.TryGetProperty("String", out var str) ? str.GetString() : null;
                if (notes != null)
                    lock (notes.Lock)
                    {
                        if (!notes.Missing.TryGetValue(language, out var have)) notes.Missing[language] = have = new SortedSet<string>(StringComparer.Ordinal);
                        foreach (var e in values.EnumerateArray())
                            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("Language", out var l) && l.GetString() is { } ln) have.Add(ln);
                    }
                return null;
            }
            if (root.TryGetProperty("Value", out var one))
            {
                if (notes != null && root.TryGetProperty("TargetLanguage", out var target) && target.GetString() is { } t && !t.Equals(language, StringComparison.OrdinalIgnoreCase))
                    lock (notes.Lock)
                    {
                        if (!notes.OneLanguage.TryGetValue(language, out var set)) notes.OneLanguage[language] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        set.Add(t);
                    }
                return one.ValueKind == JsonValueKind.String ? one.GetString() : one.GetRawText();
            }
            return root.TryGetProperty("TargetLanguage", out _) ? null : Plain(value, language, notes, plainLanguage);
        }
        catch (JsonException) { return Plain(value, language, notes, plainLanguage); }
    }

    static string? Plain(string? value, string language, TextNotes? notes, string? plainLanguage)
    {
        if (plainLanguage == null || value == null || language.Equals(plainLanguage, StringComparison.OrdinalIgnoreCase)) return value;
        if (notes != null) lock (notes.Lock) notes.Plain.Add(language);
        return null;
    }

    /// <summary>The answer's notes for what <paramref name="notes"/> heard, in a query whose text columns are in <paramref name="queryLanguage"/>.</summary>
    public static IEnumerable<string> NoteLines(TextNotes notes, string queryLanguage)
    {
        lock (notes.Lock)
        {
            foreach (var (lang, have) in notes.Missing.OrderBy(k => k.Key, StringComparer.Ordinal))
                yield return TextLanguages.IsShipped(lang)
                    ? $"Some texts have no {lang} text and read as NULL: they hold {(have.Count == 0 ? "no language" : string.Join(", ", have))}."
                    : $"Starfield ships no {lang} text, so every text asked in {lang} reads as NULL. Its languages: {TextLanguages.ShippedText}.";
            foreach (var (lang, stored) in notes.OneLanguage.OrderBy(k => k.Key, StringComparer.Ordinal))
                yield return $"Some texts are stored in their plugin in {string.Join(" and ", stored.OrderBy(x => x, StringComparer.Ordinal))} only (that plugin isn't localized: it has no strings files), so they have no {lang} text of their own. The game shows that text in every language, and it is given as it is. (name() reads the version the game uses, which may be such a plugin's.)";
            foreach (var lang in notes.Plain.OrderBy(k => k, StringComparer.Ordinal))
                yield return $"text(…, '{lang}') got a plain text, which is in one language already ({queryLanguage}), and gave NULL for it. Give text() the translated value from Json, e.g. text(Json -> '$.Name', '{lang}'), or ask the whole query in {lang} (language {lang}): every text column is then {lang}.";
        }
    }
}

sealed class Functions
{
    readonly RecordEngine _records;
    readonly HeaderIndex _index;
    readonly string _language;
    readonly Dictionary<string, string?> _names = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Plugins name() read a record from, for the answer's source line.</summary>
    public HashSet<string> PluginsRead { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Functions(RecordEngine records, string language)
    {
        _records = records; _index = records.Index; _language = language;
    }

    /// <summary>What text() and the text columns met that the answer must say (a language a text doesn't have, …).</summary>
    public TextNotes Notes { get; } = new();

    /// <summary>The functions that need no plugin: registered on every connection, the reading threads' too.
    /// <paramref name="notes"/> hears of a language a text doesn't have; the reading threads pass none.</summary>
    public static void RegisterPure(SqliteConnection c, string language, TextNotes? notes = null)
    {
        // The typed views' text columns (Name and the like): the text in one language, without the Creation Kit's tag
        c.CreateFunction("sfs_text", (string? v, string? lang) => Functions.Display(Languages.Text(v, lang == null ? language : Languages.Name(lang), notes)), isDeterministic: true);
        // A plain text (a view's text column) is in the query's language already: text() gives it for that language
        // and NULL with a note for any other (the session rewrites text(Name, 'ja') on a view's column to read Json)
        c.CreateFunction("text", (string? v) => Languages.Text(v, language, notes, language), isDeterministic: true);
        c.CreateFunction("text", (string? v, string? lang) => Languages.Text(v, lang == null ? language : Languages.Name(lang), notes, language), isDeterministic: true);
    }

    public void Register(SqliteConnection c)
    {
        RegisterPure(c, _language, Notes);
        c.CreateFunction("edid", (string? fk) => EditorId(fk), isDeterministic: true);
        c.CreateFunction("name", (string? fk) => Name(fk, _language), isDeterministic: true);
        c.CreateFunction("name", (string? fk, string? lang) => Name(fk, lang == null ? _language : Languages.Name(lang)), isDeterministic: true);
        c.CreateFunction("winner", (string? fk) => Winner(fk), isDeterministic: true);
        c.CreateFunction("versions", (string? fk) => Versions(fk), isDeterministic: true);
        c.CreateFunction("sfs_iswinner", (string? fk, string? plugin) => IsWinner(fk, plugin), isDeterministic: true);
        // Added for stored queries (M10): what kind of record a FormKey is, from the header index
        c.CreateFunction("sig", (string? fk) => Signature(fk), isDeterministic: true);
        c.CreateFunction("type", (string? fk) => Signature(fk) is { } s ? Label?.Invoke(s) ?? s : null, isDeterministic: true);
        c.CreateFunction("formkey", (string? text) => FormKeyOf(text), isDeterministic: true);
        c.CreateFunction("display", (string? name) => Display(name), isDeterministic: true);
    }

    /// <summary>
    /// A name as a person reads it: the tag the Creation Kit writes in front of a name in a plugin that isn't
    /// localized (<c>&lt;ID=0000793F&gt;AA-99 Mk.II</c>) is taken off. Since milestone M8 the text columns of the typed
    /// views, Records.Name and name() take it off themselves, so display() changes nothing there. Json keeps the text
    /// as the plugin stores it, tag and all, and so does text(Json -> '$.Name').
    /// </summary>
    public static string? Display(string? name)
    {
        if (name == null || name.Length < 5 || !name.StartsWith("<ID=", StringComparison.Ordinal)) return name;
        int end = name.IndexOf('>');
        if (end < 5) return name;
        for (int i = 4; i < end; i++) if (!Uri.IsHexDigit(name[i])) return name;
        return name[(end + 1)..];
    }

    /// <summary>
    /// The FormKey a text stands for: the text itself when it is a FormKey, otherwise the FormKey of the record with
    /// that EditorID. Null when no plugin in Data has such a record. When several records share the EditorID, the one
    /// of the first plugin in the header index's order: pass the FormKey to be exact.
    /// </summary>
    public string? FormKeyOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (HeaderIndex.TryParseFormKey(text, out _, out _)) return text;
        RecordRef? first = null;
        foreach (var r in _index.FindEditorId(text))
            if (first is not { } f || r.Plugin < f.Plugin || r.Plugin == f.Plugin && r.Entry < f.Entry) first = r;
        return first is { } hit ? _index.Describe(hit).FormKey : null;
    }

    /// <summary>A signature's record type, or its union view when it has several (the engine's LabelOf).</summary>
    public Func<string, string>? Label { get; set; }

    /// <summary>The signature of a record (WEAP), from the header index. Null when no plugin in Data holds the FormKey.</summary>
    public string? Signature(string? formKey) => Pick(formKey, out _) is { } r ? _index.Describe(r).Signature : null;

    RecordRef? Pick(string? formKey, out RecordVersions? versions)
    {
        versions = null;
        if (formKey == null || !HeaderIndex.TryParseFormKey(formKey, out var plugin, out var id)) return null;
        versions = _index.Versions(plugin, id);
        if (versions == null) return null;
        return versions.Winner >= 0 ? versions.Holders[versions.Winner] : versions.Holders[0];
    }

    /// <summary>The EditorID of a record: the version the game uses, or its owner's when no loaded plugin holds it.</summary>
    public string? EditorId(string? formKey) =>
        Pick(formKey, out _) is { } r ? _index.Plugins[r.Plugin].Segment!.EditorId(r.Entry) : null;

    /// <summary>The plugin whose version of the record the game uses. Null when no loaded plugin holds it.</summary>
    public string? Winner(string? formKey)
    {
        Pick(formKey, out var v);
        return v == null || v.Winner < 0 ? null : _index.Plugins[v.Holders[v.Winner].Plugin].Name;
    }

    /// <summary>The loaded plugins that hold the record, first loaded first (the winner is last), as a JSON list.</summary>
    public string? Versions(string? formKey)
    {
        Pick(formKey, out var v);
        if (v == null) return null;
        var loaded = v.Holders.Where(h => _index.Plugins[h.Plugin].Order.Enabled).Select(h => (JsonNode?)JsonValue.Create(_index.Plugins[h.Plugin].Name)).ToArray();
        return new JsonArray(loaded).ToJsonString();
    }

    public long? IsWinner(string? formKey, string? plugin)
    {
        if (plugin == null) return null;
        string? w = Winner(formKey);
        return w != null && w.Equals(plugin, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    /// <summary>The record's name (its Name field) in one language, read from the version the game uses.</summary>
    public string? Name(string? formKey, string language)
    {
        if (formKey == null) return null;
        string cacheKey = language + "|" + formKey;
        if (_names.TryGetValue(cacheKey, out var have)) return have;
        string? name = null;
        try
        {
            if (Pick(formKey, out _) is { } r)
            {
                var info = _index.Describe(r);
                if (_records.Schema.TypesOf(info.Signature).Count > 0 && !info.Unresolved)
                {
                    _records.Sessions.Get(info.Plugin);
                    PluginsRead.Add(info.Plugin);
                    var rec = _records.Load(r);
                    var json = _records.Json(rec);
                    if (json["Name"] is { } n) name = n is JsonValue v && v.TryGetValue<string>(out var plain) ? plain : Languages.Text(n.ToJsonString(), language, Notes);
                }
            }
        }
        catch (RecordException) { name = null; }
        return _names[cacheKey] = Display(name);
    }
}
