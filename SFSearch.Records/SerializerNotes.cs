// What Mutagen.Bethesda.Serialization writes beside a record's fields so that a record rebuilt from its JSON writes back
// like its file (1.38.6-fixes.3): notes of how the record was stored, not values of the record. SFSearch's card, fields
// and compare leave them out of the values; the JSON view and the Json column keep them as the serializer wrote them.
using System.Text.Json.Nodes;

namespace SFSearch.Records;

public static class SerializerNotes
{
    /// <summary>
    /// A record's round-trip notes, each written only when the record has one: its header FormID as stored when Mutagen
    /// would write another (C205), its own links stored in another form (C210, "OF1:…"), its child groups' labels stored
    /// in another form (C223, "GL1:…"), its subrecords' order when it isn't Mutagen's (C211), its compressed bytes when
    /// Mutagen's compression wouldn't give them again (C213, base64), and a cell's child-group layout (C215).
    /// </summary>
    public static readonly IReadOnlySet<string> RecordKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "HeaderFormIdAsStored", "OwnLinkFormsAsStored", "GroupLabelFormsAsStored", "SubrecordOrder", "OriginalCompressedData", "ChildGroupLayout",
    };

    /// <summary>
    /// A translated text's string ID in its plugin's strings tables, written beside the texts when the plugin is
    /// localized (C214): the ID, its table (Normal, DL, IL) and the plugin whose tables hold it.
    /// </summary>
    public static readonly IReadOnlySet<string> TextKeys = new HashSet<string>(StringComparer.Ordinal) { "StringsKey", "StringsSource", "StringsPlugin" };

    /// <summary>A translated text as the JSON writes it: an object with "TargetLanguage", "Value" or "Values", and its
    /// string ID when it has one (StringsKey, StringsSource, StringsPlugin).</summary>
    public static bool IsText(JsonObject o)
        => o.ContainsKey("TargetLanguage") && o.All(p => p.Key is "TargetLanguage" or "Value" or "Values" || TextKeys.Contains(p.Key));

    /// <summary>Two translated texts that hold the same texts, whatever string IDs they carry (a plugin's own strings
    /// against its master's, as before the serializer wrote IDs).</summary>
    public static bool SameText(JsonObject a, JsonObject b)
    {
        if (a.Count != b.Count || a.Any(p => TextKeys.Contains(p.Key)) || b.Any(p => TextKeys.Contains(p.Key)))
        {
            var ka = a.Where(p => !TextKeys.Contains(p.Key)).ToList();
            var kb = b.Where(p => !TextKeys.Contains(p.Key)).ToList();
            return ka.Count == kb.Count && ka.All(p => b.TryGetPropertyValue(p.Key, out var v) && JsonNode.DeepEquals(p.Value, v));
        }
        return JsonNode.DeepEquals(a, b);
    }

    /// <summary>The string ID the serializer wrote for a translated text, or null.</summary>
    public static (uint Key, string Source, string Plugin)? StringId(JsonObject o)
        => o["StringsKey"] is JsonValue k && k.TryGetValue<long>(out long key) && key >= 0 && key <= uint.MaxValue
            ? ((uint)key, o["StringsSource"]?.GetValue<string>() ?? "", o["StringsPlugin"]?.GetValue<string>() ?? "") : null;
}
