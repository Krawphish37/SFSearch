// The card's view of a reflection blob (to-do F30): a REFL subrecord (and PTCL, RDIF, PSDF, PCCC, XNSE where a record
// uses those signatures) holds a value of one of the engine's reflected classes, serialized by Bethesda's reflection
// serializer. Mutagen keeps the bytes (Reflection.Data) and decodes them on request (ReflectionMixIn.Parse,
// ReflectionData: C101). The card shows that decoded value below the bytes, as "Decoded": each field by the Creation
// Kit's display name (every schema has the CK's names, from SchemaGen's inputs\ck-names-*.tsv), with the engine's name
// beside it; "hidden in the CK" where the CK's editor doesn't show the field. A schema built without them (schemagen
// --ck-names none) shows the engine's names alone.
//  - Each value keeps its place in the blob (Position, Size), which is the place in the subrecord's content: the
//    evidence view slices the subrecord there.
//  - A FormID inside the blob is labelled from the record's own Reflection.FormLinks, which Mutagen lists in the order
//    of ReflectionData.FormIds.
//  - Nothing is cut silently: a blob Mutagen can't read to its end says why, and the values read before that are shown.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Starfield;
using SFSearch.SchemaGen;

namespace SFSearch.Records;

public static class ReflectionView
{
    /// <summary>The Mutagen object that holds a reflection blob.</summary>
    public const string ObjectName = "Reflection";
    /// <summary>The name of the card field that holds the decoded value.</summary>
    public const string DecodedName = "Decoded";
    /// <summary>The most values one blob shows on the card; the rest is counted on a line of its own.</summary>
    public const int MaxValues = 20000;

    /// <summary>The bytes of a byte field's JSON value ("0x4245…"), or null.</summary>
    public static byte[]? Bytes(JsonNode? data)
    {
        if (data is not JsonValue v || !v.TryGetValue<string>(out var s) || !s.StartsWith("0x", StringComparison.Ordinal) || s.Length < 4 || s.Length % 2 != 0) return null;
        try { return Convert.FromHexString(s.AsSpan(2)); } catch (FormatException) { return null; }
    }

    /// <summary>The decoded value of a Reflection object's JSON, as one card field, or null when its bytes aren't a
    /// reflection blob.</summary>
    /// <param name="label">Turns a FormKey into EditorID (FormID:plugin).</param>
    public static CardField? Decoded(JsonObject reflection, Func<string, string> label, CkData ck)
    {
        var bytes = Bytes(reflection["Data"]);
        if (bytes == null || !ReflectionData.IsReflection(bytes)) return null;
        var data = ReflectionData.TryParse(bytes);
        if (data == null) return null;
        var links = reflection["FormLinks"] as JsonArray;
        var b = new Builder(data, links, label, ck);
        var tops = new List<CardField>();
        for (int i = 0; i < data.Objects.Count; i++) tops.Add(b.Value($"[{i}]", null, data.Objects[i], null));
        string what = data.Objects.Count == 1 && data.Objects[0] is ReflectionObject or ReflectionUser ? b.ClassLabel(ClassOf(data.Objects[0])!) : $"{data.Objects.Count} values";
        var notes = new List<string> { "decoded by Mutagen's reflection reader; the evidence view gives a value's bytes (fields \"<path>.Decoded.<field>\")" };
        if (data.Objects.Any(o => o is ReflectionObject { IsDiff: true } or ReflectionUser { IsDiff: true }))
            notes.Add("a diff (DIFF): only the fields that differ from the record's reflection parent are stored, and shown");
        if (ck.Present) notes.Add($"names are the Creation Kit's ({ck.Source}), the engine's beside them");
        if (data.Error != null) notes.Add($"Mutagen's reader stopped before the end: {data.Error}; the values read before that are shown");
        if (b.Cut) notes.Add($"only the first {MaxValues.ToString("N0", CultureInfo.InvariantCulture)} values are shown: the blob holds more (its bytes are above)");
        // One top object (the usual case): its fields sit right under Decoded
        var children = data.Objects.Count == 1 && tops[0].Children is { } only && tops[0].Kind == "Object" ? only : tops;
        var decoded = new CardField
        {
            Name = DecodedName, Readable = DecodedName, Kind = "Reflection", Children = children, Variant = what,
            Note = string.Join("; ", notes), Position = 0, Size = bytes.Length,
        };
        foreach (var c in children) c.Up = decoded;
        return decoded;
    }

    static ReflectionClass? ClassOf(ReflectionValue v) => v switch { ReflectionObject o => o.Class, ReflectionUser u => u.Class, _ => null };

    sealed class Builder(ReflectionData data, JsonArray? links, Func<string, string> label, CkData ck)
    {
        readonly Dictionary<int, int> _formIdAt = data.FormIds.Select((f, i) => (f.Position, i)).GroupBy(x => x.Position).ToDictionary(g => g.Key, g => g.First().i);
        int _values;
        /// <summary>True once the card's limit stopped the walk: the values after the first MaxValues aren't shown.</summary>
        public bool Cut;
        bool Full => _values >= MaxValues;

        public string ClassLabel(ReflectionClass cls) =>
            ck.Type(cls.Name)?.DisplayName is { Length: > 0 } dn && dn != cls.Name ? $"{cls.Name} (CK: \"{dn}\")" : cls.Name;

        /// <summary>One value as a card field. <paramref name="field"/> is the class field it is the value of (null for
        /// a list item, a map's key or value, a top object).</summary>
        public CardField Value(string name, ReflectionField? field, ReflectionValue value, CkField? ckField)
        {
            string readable = name.StartsWith('[') ? name : ckField?.DisplayName is { Length: > 0 } dn ? dn : name;
            string? note = ckField is { Hidden: true } ? "hidden in the CK" : null;
            _values++;
            switch (value)
            {
                case ReflectionObject o:
                {
                    var kids = new List<CardField>();
                    foreach (var m in o.Members)
                    {
                        if (Full) { Cut = true; break; }
                        kids.Add(Value(m.Field.Name, m.Field, m.Value, ck.Field(o.Class.Name, m.Field.Name)));
                    }
                    return Parent(name, readable, "Object", kids, ClassLabel(o.Class), note, o.Position, null);
                }
                case ReflectionUser u:
                {
                    var kids = new List<CardField>();
                    for (int i = 0; i < u.Values.Count && !(Cut |= Full); i++)
                    {
                        // The class itself, written in its own chunk as a plain class: its fields, not one more level
                        if (u.Values[i] is ReflectionObject self && self.Class.Name == u.Class.Name)
                            foreach (var m in self.Members) kids.Add(Value(m.Field.Name, m.Field, m.Value, ck.Field(self.Class.Name, m.Field.Name)));
                        else kids.Add(Value($"[{i}]", null, u.Values[i], null));
                    }
                    return Parent(name, readable, "Object", kids, ClassLabel(u.Class), note, u.Position, null);
                }
                case ReflectionList l:
                {
                    var kids = new List<CardField>();
                    for (int i = 0; i < l.Items.Count && !(Cut |= Full); i++) kids.Add(Value($"[{i}]", null, l.Items[i], null));
                    return Parent(name, readable, "List", kids, null, note, l.Position, null);
                }
                case ReflectionMap mp:
                {
                    var kids = new List<CardField>();
                    for (int i = 0; i < mp.Pairs.Count && !(Cut |= Full); i++)
                    {
                        var kv = new List<CardField> { Value("Key", null, mp.Pairs[i].Key, null), Value("Value", null, mp.Pairs[i].Value, null) };
                        var entry = new CardField { Name = $"[{i}]", Readable = $"[{i}]", Kind = "Entry", Children = kv, Position = mp.Pairs[i].Key.Position };
                        foreach (var c in kv) c.Up = entry;
                        kids.Add(entry);
                    }
                    return Parent(name, readable, "Dictionary", kids, null, note, mp.Position, null);
                }
                case ReflectionRef r:
                {
                    if (r.Target == null) return Leaf(name, readable, "Ref", "None", Join(note, "a pointer to nothing"), r.Position, 4);
                    // A pointer to a form: its FormID, labelled from the record's own links
                    if (r.TargetTypeId == (uint)ReflectionBuiltInType.UInt32 && r.Target is ReflectionScalar { Value: uint })
                        return Leaf(name, readable, "FormLink", FormText(r.Target), note, r.Target.Position, 4);
                    var inner = Value(name, field, r.Target, ckField);
                    return inner.Children != null
                        ? Parent(name, readable, inner.Kind, inner.Children, inner.Variant, Join(note, inner.Note), r.Position, null)
                        : Leaf(name, readable, inner.Kind, inner.Text ?? "", Join(note, inner.Note), inner.Position ?? r.Position, inner.Size);
                }
                case ReflectionScalar s:
                {
                    if (s.Value is uint && _formIdAt.ContainsKey(s.Position)) return Leaf(name, readable, "FormLink", FormText(s), note, s.Position, 4);
                    var (text, kind, size) = Scalar(s, field, ckField);
                    return Leaf(name, readable, kind, text, note, s.Position, size);
                }
                default:
                    return Leaf(name, readable, "Value", value.ToString() ?? "", note, value.Position, null);
            }
        }

        string FormText(ReflectionValue v)
        {
            uint raw = v is ReflectionScalar { Value: uint u } ? u : 0;
            if (_formIdAt.TryGetValue(v.Position, out int i) && links != null && i < links.Count && links[i] is JsonValue jv && jv.TryGetValue<string>(out var key))
                return key == "Null" ? "Null" : label(key);
            return raw.ToString("X8", CultureInfo.InvariantCulture);
        }

        (string Text, string Kind, int? Size) Scalar(ReflectionScalar s, ReflectionField? field, CkField? ckField)
        {
            switch (s.Value)
            {
                case string str:
                {
                    int size = 2 + System.Text.Encoding.Latin1.GetByteCount(str) + 1;
                    // An enum is stored by its member's name: the CK's name for that member beside it
                    bool isEnum = field is { MemorySize: > 0 and <= 4 } || ck.Type(ckField?.FieldType) is { Kind: "Enumeration" };
                    if (isEnum && ck.Member(ckField?.FieldType, str) is { } mem && CkData.Shown(mem.DisplayName, str) is { } dn)
                        return ($"{str} (CK: \"{dn}\")", "Enum", size);
                    return (str.Length == 0 || str != str.Trim() ? JsonSerializer.Serialize(str) : str, isEnum ? "Enum" : "String", size);
                }
                case bool b: return (b ? "true" : "false", "Bool", 1);
                case float f: return (f.ToString("R", CultureInfo.InvariantCulture), "Float", 4);
                case double d: return (d.ToString("R", CultureInfo.InvariantCulture), "Double", 8);
                case sbyte or byte: return (Convert.ToString(s.Value, CultureInfo.InvariantCulture)!, "Int", 1);
                case short or ushort: return (Convert.ToString(s.Value, CultureInfo.InvariantCulture)!, "Int", 2);
                case int or uint: return (Convert.ToString(s.Value, CultureInfo.InvariantCulture)!, "Int", 4);
                case long or ulong: return (Convert.ToString(s.Value, CultureInfo.InvariantCulture)!, "Int", 8);
                default: return (Convert.ToString(s.Value, CultureInfo.InvariantCulture) ?? "", "Value", null);
            }
        }

        static string? Join(string? a, string? b) => a == null ? b : b == null ? a : a + "; " + b;

        static CardField Leaf(string name, string readable, string kind, string text, string? note, int position, int? size) =>
            new() { Name = name, Readable = readable, Kind = kind, Text = text, Json = JsonValue.Create(text), Note = note, Position = position, Size = size };

        static CardField Parent(string name, string readable, string kind, List<CardField> kids, string? variant, string? note, int position, int? size)
        {
            var p = new CardField { Name = name, Readable = readable, Kind = kind, Children = kids, Variant = variant, Note = note, Position = position, Size = size };
            foreach (var c in kids) c.Up = p;
            return p;
        }
    }
}
