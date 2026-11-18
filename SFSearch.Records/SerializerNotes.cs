// What Mutagen.Bethesda.Serialization writes beside a record's fields so that a record rebuilt from its JSON writes back
// like its file (1.38.6-fixes.3 on): notes of how the record was stored, not values of the record. SFSearch's card, fields
// and compare leave them out of the values; the JSON view and the Json column keep them as the serializer wrote them.
// The names are the serializer's own since 1.38.6-fixes.5 (C59 W14): RoundTripNoteNames, StringsKeyNote,
// TranslatedStringNames.
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Serialization;
using Mutagen.Bethesda.Serialization.Utility;

namespace SFSearch.Records;

public static class SerializerNotes
{
    /// <summary>A record's round-trip notes (the serializer's RoundTripNoteNames.RecordNotes), each written only when the
    /// record has one: its header FormID as stored, its own links and its child groups' labels stored in another form,
    /// its subrecords' order, its compressed bytes, a cell's child-group layout, a package's procedure tree data order.</summary>
    public static IReadOnlySet<string> RecordKeys => RoundTripNoteNames.RecordNotes;

    /// <summary>A translated text's string ID in its plugin's strings tables, written beside the texts when the plugin is
    /// localized: the ID, its table (Normal, DL, IL) and the plugin whose tables hold it (StringsKeyNote.Names).</summary>
    public static IReadOnlySet<string> TextKeys => StringsKeyNote.Names;

    /// <summary>A translated text as the JSON writes it (TranslatedStringNames.IsTranslatedStringObject): an object with
    /// "TargetLanguage", "Value" or "Values", and its string ID when it has one.</summary>
    /// <remarks>And, beside them, "Tables": what the strings tables of a plugin that isn't localized hold for the text
    /// (TagTexts.cs, GAPS S190), which SFSearch adds.</remarks>
    public static bool IsText(JsonObject o) => TranslatedStringNames.IsTranslatedStringObject(o.Select(p => p.Key).Where(k => k != "Tables"));

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
