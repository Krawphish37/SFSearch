// Holds a card against the record's JSON, walking the JSON on its own: every value of the JSON must be in the card
// at the same place with the same JSON value, the card must have no value the JSON lacks, and the card's text must
// print every value, in order.
using System.Text.Json;
using System.Text.Json.Nodes;
using SFSearch.Records;

namespace SFSearch.Records.Tests;

static class CardCheck
{
    public sealed class Result
    {
        public List<string> Problems = new();
        public long Values, Links, Translated, Unknown, Children, NotInSchema;
        /// <summary>The serializer's round-trip notes met (SerializerNotes.RecordKeys), which the card leaves out.</summary>
        public long Notes;
    }

    static bool IsText(JsonObject o) => SerializerNotes.IsText(o);

    /// <summary>The text a translated value must show in a language: found by the language's name.</summary>
    public static string? Expected(JsonObject o, string language, out bool hasAny)
    {
        hasAny = false;
        if (o["Values"] is JsonArray values)
        {
            foreach (var v in values)
            {
                hasAny = true;
                if (v is JsonObject e && string.Equals(e["Language"]?.GetValue<string>(), language, StringComparison.OrdinalIgnoreCase)) return e["String"]?.GetValue<string>() ?? "";
            }
            return null;
        }
        hasAny = o.ContainsKey("Value");
        return o["Value"]?.GetValue<string>() ?? "";
    }

    public static Result Check(JsonObject json, Card card, bool checkText = true)
    {
        var res = new Result();
        void Problem(string what) { if (res.Problems.Count < 20) res.Problems.Add(what); }

        // One object of the JSON against the card's fields at that place: each JSON value has one field of its name
        void Level(JsonObject o, List<CardField> fields, CardField? parent)
        {
            Dictionary<string, CardField>? byName = null;
            if (fields.Count > 8)
            {
                byName = new Dictionary<string, CardField>(fields.Count, StringComparer.Ordinal);
                foreach (var f in fields) if (!byName.TryAdd(f.Name, f)) Problem($"two card fields named {f.Path}");
            }
            int values = 0;
            foreach (var (k, v) in o)
            {
                if (k == RecordSplit.TypeKey)
                {
                    if (parent?.Variant != v?.GetValue<string>()) Problem($"{parent?.Path}: the JSON says it is a {v}, the card {parent?.Variant}");
                    continue;
                }
                // The serializer's round-trip notes (SerializerNotes.RecordKeys) aren't values: the card leaves them out
                if (SerializerNotes.RecordKeys.Contains(k) && !fields.Any(x => x.Name == k)) { res.Notes++; continue; }
                values++;
                CardField? f = byName != null ? byName.GetValueOrDefault(k) : fields.FirstOrDefault(x => x.Name == k);
                if (f == null) { Problem($"the card has no field for {(parent == null ? k : parent.Path + "." + k)}"); continue; }
                Node(v, f);
            }
            // A reflection blob's decoded value is the card's own addition: the JSON holds the blob's bytes
            if (values != fields.Count(x => !x.IsDecodedReflection)) Problem($"{parent?.Path ?? "the record"}: {values} values in the JSON, {fields.Count} fields in the card");
        }

        void Node(JsonNode? node, CardField f)
        {
            switch (node)
            {
                case null:
                    res.Values++;
                    if (f.Children != null || f.Text != "null") Problem($"{f.Path}: the JSON has null, the card {f.Text}");
                    break;
                case JsonObject o when RecordSplit.ChildOf(o) is string child:
                    res.Values++; res.Children++;
                    if (f.Children != null || !ReferenceEquals(f.Json, o) && !JsonNode.DeepEquals(f.Json, o) || f.Text == null || !f.Text.Contains(child[..6], StringComparison.OrdinalIgnoreCase))
                        Problem($"{f.Path}: the child {child} isn't shown as a child ({f.Text})");
                    break;
                case JsonObject o when IsText(o):
                {
                    res.Values++; res.Translated++;
                    if (f.Children != null || !ReferenceEquals(f.Json, o) && !JsonNode.DeepEquals(f.Json, o)) { Problem($"{f.Path}: the card's value isn't the JSON's translated text"); break; }
                    // A card shows a text without the Creation Kit's tag (milestone M8); the JSON keeps it
                    string? expected = CardBuilder.NoTag(Expected(o, card.Language, out bool any));
                    if (expected != null ? f.Text != expected : any && !(f.Text ?? "").StartsWith("(no ", StringComparison.Ordinal))
                        Problem($"{f.Path}: the card shows \"{f.Text}\", the {card.Language} text is \"{expected}\"");
                    break;
                }
                case JsonObject o:
                    if (f.Children == null) { Problem($"{f.Path}: an object in the JSON, a plain value in the card"); break; }
                    Level(o, f.Children, f);
                    break;
                case JsonArray a:
                    if (f.Children == null)
                    {
                        // Flags: one value holding the list of names
                        res.Values++;
                        if (!ReferenceEquals(f.Json, a) && !JsonNode.DeepEquals(f.Json, a)) Problem($"{f.Path}: the card's value isn't the JSON's list");
                        foreach (var item in a)
                            if (item is not JsonValue || !(f.Text ?? "").Contains(item.GetValue<JsonElement>().ToString(), StringComparison.Ordinal))
                                Problem($"{f.Path}: the card doesn't show {item?.ToJsonString()}");
                    }
                    else
                    {
                        if (f.Children.Count != a.Count) { Problem($"{f.Path}: {a.Count} items in the JSON, {f.Children.Count} in the card"); break; }
                        for (int i = 0; i < a.Count; i++)
                        {
                            var item = f.Children[i];
                            if (item.Name.Length < 3 || item.Name[0] != '[' || !int.TryParse(item.Name.AsSpan(1, item.Name.Length - 2), out int n) || n != i) { Problem($"{f.Path}: item {i} of the card is named {item.Name}"); break; }
                            Node(a[i], item);
                        }
                    }
                    break;
                default:
                {
                    res.Values++;
                    if (f.Children != null || !ReferenceEquals(f.Json, node) && !JsonNode.DeepEquals(f.Json, node)) { Problem($"{f.Path}: the card's value isn't the JSON's {node.ToJsonString()}"); break; }
                    var el = node.GetValue<JsonElement>();
                    bool ok;
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        string s = el.GetString()!;
                        if (RecordSplit.IsFormKey(s)) { res.Links++; ok = f.Text != null && f.Text.Contains(s[..6], StringComparison.OrdinalIgnoreCase) && f.Text.Contains(s[7..], StringComparison.OrdinalIgnoreCase); }
                        else if (f.Kind == "ByteArray" && FieldLookup.IsNoBytes(s)) ok = f.Text == "[]";
                        else if (f.Kind == "ByteArray" && s.StartsWith("0x", StringComparison.Ordinal)) ok = string.Equals((f.Text ?? "").Replace(" ", ""), s[2..], StringComparison.OrdinalIgnoreCase);
                        else ok = f.Text == s;
                    }
                    else ok = f.Text == el.GetRawText();
                    if (!ok) Problem($"{f.Path}: the JSON has {node.ToJsonString()}, the card shows {f.Text}");
                    break;
                }
            }
            if (f.Unknown && f.Children == null) { res.Unknown++; if (f.Bytes == null && f.Kind != "Text") Problem($"{f.Path}: an unknown field with no bytes shown ({f.Kind})"); }
            if (!f.InSchema) res.NotInSchema++;
        }

        Level(json, card.Fields, null);

        // The text prints every value, in order: the field's name as the card prints it, then its value.
        // (Not for a heavy record: its text alone is hundreds of megabytes. Its values are only counted.)
        long cardValues = 0;
        if (!checkText)
        {
            foreach (var _ in card.Values(decoded: false)) cardValues++;
            if (cardValues != res.Values) Problem($"the JSON has {res.Values} values, the card has {cardValues}");
            return res;
        }
        string text = card.ToText();
        int cursor = 0;
        foreach (var f in card.Values(decoded: false))
        {
            cardValues++;
            string shown = Card.Head(f) + (f.Name.StartsWith('[') ? " " : ": ") + Card.ValueText(f);
            int at = text.IndexOf(shown, cursor, StringComparison.Ordinal);
            if (at < 0) { Problem($"the card's text doesn't print {f.Path} as \"{(shown.Length > 80 ? shown[..80] + "…" : shown)}\""); break; }
            cursor = at + shown.Length;
            if (f.Unknown && f.Bytes != null)
            {
                int eol = text.IndexOf('\n', at);
                if (eol < 0) eol = text.Length;
                if (text.IndexOf("[unknown field; bytes ", at, eol - at, StringComparison.Ordinal) < 0) { Problem($"the card's text doesn't mark {f.Path} as unknown with its bytes"); break; }
            }
        }
        if (cardValues != res.Values) Problem($"the JSON has {res.Values} values, the card has {cardValues}");
        return res;
    }

    /// <summary>Where two JSON values first differ, as one line; null when they are equal.</summary>
    public static string? FirstDifference(JsonNode? a, JsonNode? b, string path = "")
    {
        if (a is JsonObject oa && b is JsonObject ob)
        {
            var ka = oa.Select(p => p.Key).ToList(); var kb = ob.Select(p => p.Key).ToList();
            foreach (var k in ka) if (!ob.ContainsKey(k)) return $"{Join(path, k)}: only in the first ({Cut(oa[k])})";
            foreach (var k in kb) if (!oa.ContainsKey(k)) return $"{Join(path, k)}: only in the second ({Cut(ob[k])})";
            foreach (var k in ka) if (FirstDifference(oa[k], ob[k], Join(path, k)) is { } d) return d;
            if (!ka.SequenceEqual(kb)) return $"{path}: the same fields in another order";
            return null;
        }
        if (a is JsonArray aa && b is JsonArray ab)
        {
            if (aa.Count != ab.Count) return $"{path}: {aa.Count} items in the first, {ab.Count} in the second";
            for (int i = 0; i < aa.Count; i++) if (FirstDifference(aa[i], ab[i], $"{path}[{i}]") is { } d) return d;
            return null;
        }
        return (a?.ToJsonString() ?? "null") == (b?.ToJsonString() ?? "null") ? null : $"{path}: {Cut(a)} in the first, {Cut(b)} in the second";

        static string Join(string p, string k) => p.Length == 0 ? k : p + "." + k;
        static string Cut(JsonNode? n) { string s = n?.ToJsonString() ?? "null"; return s.Length > 80 ? s[..80] + "…" : s; }
    }
}
