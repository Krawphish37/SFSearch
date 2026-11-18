// The card: what an agent sees of a record by default. It is made from the record's JSON and the schema, never from
// Mutagen objects, so it shows exactly what the JSON holds:
//  - only the fields that are set, in the schema's (file) order;
//  - the readable name with the subrecord signature beside it;
//  - links as EditorID (FormID:plugin), from the header index;
//  - enums by name, flags as a list;
//  - translated text in the chosen language, picked by the language's name, never by its place in the list;
//  - fields Mutagen still calls unknown, with their raw bytes and the schema's evidence. Nothing is left out: a JSON
//    value the schema doesn't know is shown under its JSON name.
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SFSearch.SchemaGen;

namespace SFSearch.Records;

public sealed class CardField
{
    /// <summary>The field this one sits in (null at the top of the record).</summary>
    public CardField? Up { get; internal set; }
    /// <summary>Where the value sits in the record's JSON: <c>ObjectTemplates[0].Includes[2].Mod</c>. Worked out
    /// from the fields above it when asked, so a card of millions of values doesn't hold millions of paths.</summary>
    public string Path
    {
        get
        {
            if (Up == null) return Name;
            string up = Up.Path;
            return Name.StartsWith('[') ? up + Name : up + "." + Name;
        }
    }
    /// <summary>The Mutagen name (the JSON key), or <c>[3]</c> for an item of a list.</summary>
    public required string Name { get; init; }
    /// <summary>xEdit's name where a checked pair exists, else the Mutagen name spaced out.</summary>
    public required string Readable { get; init; }
    /// <summary>The subrecord the field is read from, when the schema knows it.</summary>
    public string? Signature { get; init; }
    /// <summary>The schema's kind (Float, FormLink, Enum, Object, List…), or what the JSON value looks like when the schema doesn't know the field.</summary>
    public required string Kind { get; init; }
    /// <summary>The JSON value itself, for a value (not for an object or a list that has <see cref="Children"/>).</summary>
    public JsonNode? Json { get; init; }
    /// <summary>The value as the card prints it.</summary>
    public string? Text { get; init; }
    /// <summary>For an object that can be of several kinds: which one (<c>MutagenObjectType</c>).</summary>
    public string? Variant { get; init; }
    public bool Unknown { get; init; }
    /// <summary>For an unknown field: its bytes.</summary>
    public string? Bytes { get; init; }
    /// <summary>For an unknown field: what the schema knows about it.</summary>
    public string? Evidence { get; init; }
    public string? Unit { get; init; }
    /// <summary>For translated text: the languages the record has.</summary>
    public IReadOnlyList<string>? Languages { get; init; }
    /// <summary>False when the schema has no such field for this object (shown all the same).</summary>
    public bool InSchema { get; init; } = true;
    public List<CardField>? Children { get; init; }
    /// <summary>For an object: the schema object its fields were read as (null when the schema has none).</summary>
    public string? Object { get; init; }

    public bool IsValue => Children == null;
    /// <summary>The record leaves this field out and it is at its default: shown because a reader looks for it on this
    /// type (<see cref="CardDefaults"/>), not because the record sets it.</summary>
    public bool IsDefault { get; init; }
    /// <summary>A remark printed after the value in brackets: "hidden in the CK" for a reflected field the Creation Kit's
    /// editor doesn't show (to-do F30). Null for most fields.</summary>
    public string? Note { get; init; }
    /// <summary>For a value decoded from a reflection blob (<see cref="ReflectionView"/>): its place and size in the blob,
    /// which is the subrecord's content. Null for everything else.</summary>
    public int? Position { get; init; }
    public int? Size { get; init; }
    /// <summary>True for the field that holds a reflection blob's decoded value (<see cref="ReflectionView.Decoded"/>):
    /// it isn't in the record's JSON, which holds the blob's bytes.</summary>
    public bool IsDecodedReflection => Kind == "Reflection" && Name == ReflectionView.DecodedName;
}

/// <summary>
/// The fields a reader looks for on a type, shown on the card even when the record leaves them at their default:
/// object bounds, value, weight, damage, armor rating. A card otherwise shows only what the record sets, and a field
/// that isn't there reads as "not looked at" (the Eon (0476C4:Starfield.esm): its object bounds are all zero).
/// </summary>
public static class CardDefaults
{
    /// <summary>Top-level Mutagen names. A type shows those it has that are always there (not "may be absent").</summary>
    public static readonly string[] LookedFor = { "ObjectBounds", "Value", "BaseValue", "Weight", "BaseWeight", "AttackDamage", "Damage", "ArmorRating" };

    /// <summary>Adds a line for each looked-for field the card lacks, in the schema's order, marked as the default.</summary>
    public static void Add(RecordSchema schema, string type, Card card)
    {
        var props = schema.Props(type);
        var have = new HashSet<string>(card.Fields.Select(f => f.Name), StringComparer.Ordinal);
        foreach (var p in props)
        {
            if (have.Contains(p.Name) || p.Main.Nullable || Array.IndexOf(LookedFor, p.Name) < 0) continue;
            var (json, text) = FieldLookup.DefaultOf(schema, p.Main);
            if (json == null) continue;
            var field = new CardField
            {
                Name = p.Name, Readable = schema.ReadablePair(card.Signature, p.Main.Path)?.Trim() ?? (string.IsNullOrWhiteSpace(p.Main.Readable) ? CardBuilder.Spaced(p.Name) : p.Main.Readable!.Trim()),
                Signature = p.Main.Subrecord ?? (p.Main.RefObject != null && schema.Model.Objects.TryGetValue(p.Main.RefObject, out var o) ? o.Sig : null),
                Kind = p.Main.Kind, Json = json, Text = (text ?? json.ToJsonString()) + " (default)", Unit = p.Main.Unit, IsDefault = true,
            };
            // In the schema's order: before the first field the card has that comes after it in the schema
            int at = card.Fields.Count;
            bool after = false;
            foreach (var q in props)
            {
                if (q.Name == p.Name) { after = true; continue; }
                if (!after) continue;
                int i = card.Fields.FindIndex(f => f.Name == q.Name);
                if (i >= 0) { at = i; break; }
            }
            card.Fields.Insert(at, field);
            have.Add(p.Name);
        }
    }
}

public sealed class Card
{
    public required string Label { get; init; }
    public required string FormKey { get; init; }
    public required string Signature { get; init; }
    public required string Type { get; init; }
    public required string Plugin { get; init; }
    public bool IsOverride { get; init; }
    /// <summary>The record this one sits in (a placed reference's cell), labelled.</summary>
    public string? Parent { get; init; }
    public long Offset { get; init; }
    public uint Size { get; init; }
    public bool Compressed { get; init; }
    public string Language { get; init; } = "English";
    public required List<CardField> Fields { get; init; }
    public List<string> Notes { get; } = new();
    /// <summary>For a heavy record read in part: the lists of which only a page was read, by top-level field name.</summary>
    public IReadOnlyDictionary<string, ListPage>? Pages { get; init; }
    /// <summary>A property's value read from Mutagen's record by its path (<c>Weather.DataType</c>), for the properties
    /// Mutagen adds beyond its XML that the serializer doesn't write (GAPS Part 2 row 8: RegionData.DataType is
    /// read-only). Null when the record object isn't at hand, or the path leads nowhere.</summary>
    public Func<string, object?>? FromRecord { get; init; }

    /// <summary>Every value of the card, in order.</summary>
    /// <param name="decoded">Also the values decoded from reflection blobs (<see cref="ReflectionView"/>), which the
    /// record's JSON holds as bytes only.</param>
    public IEnumerable<CardField> Values(bool decoded = true)
    {
        IEnumerable<CardField> Walk(List<CardField> fields)
        {
            foreach (var f in fields)
            {
                if (f.Children == null) yield return f;
                else if (decoded || !f.IsDecodedReflection) foreach (var c in Walk(f.Children)) yield return c;
            }
        }
        return Walk(Fields);
    }

    /// <summary>The lines a list or object may hold and still be printed open in the card's outline (<see cref="ToText"/>'s
    /// closeOver): a sound's two events, a model's few fields and a short list stay open; a weapon's 21 object template
    /// combinations (443 lines) close.</summary>
    public const int OutlineLines = 12;

    /// <summary>The card as text, every list and object open.</summary>
    /// <param name="maxItems">The most items of one list that are printed. The rest is counted on a line of its own; nothing is cut silently.</param>
    /// <param name="maxBytes">The most bytes of one byte field that are printed.</param>
    /// <remarks>(Kept with these two parameters: an add-on built for 2.0 to 2.2 calls it so.)</remarks>
    public string ToText(int maxItems = int.MaxValue, int maxBytes = int.MaxValue) => ToText(maxItems, maxBytes, int.MaxValue, null);

    /// <summary>The card as text, as an outline.</summary>
    /// <param name="maxItems">The most items of one list that are printed. The rest is counted on a line of its own; nothing is cut silently.</param>
    /// <param name="maxBytes">The most bytes of one byte field that are printed.</param>
    /// <param name="closeOver">The outline (GAPS S192): a list or object that would print more lines than this is closed,
    /// one line with its count, the lines it holds and how to open it; of a list of plain values, the first two. The
    /// record's own fields are always printed. int.MaxValue: nothing is closed.</param>
    /// <param name="closed">Gets the path of each part that was closed.</param>
    public string ToText(int maxItems, int maxBytes, int closeOver, List<string>? closed = null)
    {
        var sb = new StringBuilder();
        sb.Append(Label).Append('\n');
        sb.Append(Signature).Append(' ').Append(Type).Append(" in ").Append(Plugin);
        sb.Append(IsOverride ? " (an override)" : " (its own record)");
        if (Parent != null) sb.Append(", inside ").Append(Parent);
        sb.Append($", at 0x{Offset:X}, {Size.ToString("N0", CultureInfo.InvariantCulture)} bytes").Append(Compressed ? ", compressed" : "").Append('\n');
        Write(sb, Fields, 1, maxItems, maxBytes, closeOver, closed);
        foreach (var n in Notes) sb.Append("[notes] ").Append(n).Append('\n');
        return sb.ToString();
    }

    /// <summary>The lines the fields print, with everything under them.</summary>
    static int Lines(List<CardField> fields)
    {
        int n = fields.Count;
        foreach (var f in fields) if (f.Children != null) n += Lines(f.Children);
        return n;
    }

    static void Write(StringBuilder sb, List<CardField> fields, int depth, int maxItems, int maxBytes, int closeOver = int.MaxValue, List<string>? closed = null)
    {
        int items = 0, hidden = 0;
        foreach (var f in fields)
        {
            bool isItem = f.Name.StartsWith('[');
            if (isItem && ++items > maxItems) { hidden++; continue; }
            sb.Append(' ', depth * 2);
            sb.Append(Head(f));
            if (f.Children != null)
            {
                if (f.Variant != null) sb.Append(isItem ? " " : ": ").Append(f.Variant);
                else if (f.Kind is "List" or "Array" or "Array2D" or "Dictionary") sb.Append(": ").Append(f.Children.Count.ToString("N0", CultureInfo.InvariantCulture)).Append(f.Children.Count == 1 ? " item" : " items");
                else if (f.Children.Count == 0) sb.Append(isItem ? " " : ": ").Append("(set, with every field of it at its default)");
                if (f.Unknown) sb.Append("   [unknown field").Append(f.Evidence != null ? "; " + f.Evidence : "").Append(']');
                if (!f.InSchema) sb.Append("   [not in the schema]");
                if (f.Note != null) sb.Append("   [").Append(f.Note).Append(']');
                // The outline: a part that would print more lines than it allows is closed
                if (closeOver != int.MaxValue && Lines(f.Children) is int held && held > closeOver)
                {
                    // (of a list of plain values, the first two)
                    if (f.Children.All(c => c.Children == null))
                        sb.Append(", the first: ").Append(string.Join("; ", f.Children.Take(2).Select(c => ValueText(c, maxBytes))));
                    sb.Append("   [closed: ").Append(held.ToString("N0", CultureInfo.InvariantCulture)).Append(" lines. The fields view of ").Append(f.Path).Append(" opens it; the full view opens every one]\n");
                    closed?.Add(f.Path);
                    continue;
                }
                sb.Append('\n');
                Write(sb, f.Children, depth + 1, maxItems, maxBytes, closeOver, closed);
                continue;
            }
            sb.Append(isItem ? " " : ": ").Append(ValueText(f, maxBytes));
            if (f.Unit != null) sb.Append(' ').Append(f.Unit);
            if (f.Unknown)
            {
                sb.Append("   [unknown field");
                if (f.Bytes != null) sb.Append("; bytes ").Append(Cut(f.Bytes, maxBytes));
                if (f.Evidence != null) sb.Append("; ").Append(f.Evidence);
                sb.Append(']');
            }
            if (!f.InSchema) sb.Append("   [not in the schema]");
            if (f.Note != null) sb.Append("   [").Append(f.Note).Append(']');
            sb.Append('\n');
        }
        if (hidden > 0) sb.Append(' ', depth * 2).Append($"… {hidden.ToString("N0", CultureInfo.InvariantCulture)} more {(hidden == 1 ? "item" : "items")} not printed (the JSON view has them all)\n");
    }

    /// <summary>A field's name as the card's text prints it: the readable name, with the Mutagen name and the subrecord beside it.</summary>
    public static string Head(CardField f)
    {
        if (f.Name.StartsWith('[')) return f.Name;
        // The Mutagen name is added when the readable name isn't just that name spaced out
        bool same = string.Equals(f.Readable.Replace(" ", ""), f.Name, StringComparison.OrdinalIgnoreCase);
        string inner = same ? f.Signature ?? "" : f.Signature == null ? f.Name : f.Name + ", " + f.Signature;
        return inner.Length == 0 ? f.Readable : $"{f.Readable} ({inner})";
    }

    /// <summary>A value as the card's text prints it.</summary>
    public static string ValueText(CardField f, int maxBytes = int.MaxValue)
    {
        string t = f.Text ?? "";
        if (f.Kind == "ByteArray") return Cut(t, maxBytes);
        if (f.Kind is "String" or "Text" && (t.Length == 0 || t != t.Trim() || t.Contains('\n'))) return JsonSerializer.Serialize(t, Relaxed);
        if (f.Languages is { Count: > 1 }) return $"{t}   [{f.Languages.Count} languages]";
        // A GUID (a sound reference's Wwise events): the form Wwise and xEdit show first, since that is the one that
        // finds the event; Mutagen's .NET form of the same 16 bytes, which the JSON and the views' columns give, beside it
        // Before it the event's name and after it its path in the Wwise project, as xEdit shows them, where a sound
        // bank's description in the Data folder names the GUID (GAPS S191, WwiseEvents.cs)
        if (f.Kind == "Guid" && WwiseGuid(t) is { } wwise)
            return $"{(WwiseEvents.Default is { } events && events.TryGet(wwise, out var ev) ? $"{ev.Name} {wwise} \"{ev.Path}\"" : wwise)}   [Wwise and xEdit's form; {t} as Mutagen reads it]";
        return t;
    }

    /// <summary>
    /// A GUID as Wwise and xEdit show it, from the text .NET (and so Mutagen and the JSON) gives for the same 16 bytes.
    /// .NET reads bytes 0-3, 4-5 and 6-7 as little-endian numbers and prints 8-15 in order; Wwise reads two
    /// little-endian 64-bit halves. So "A97E454F-4307-41EE-45D0-7BD23FC3DE8D" is "{41EE4307-A97E-454F-8DDE-C33FD27BD045}",
    /// the event ITM_Generic_Weapon_Rifle_Light_Up. Null when the text is no GUID.
    /// </summary>
    public static string? WwiseGuid(string? netText)
    {
        if (netText == null || !Guid.TryParse(netText, out var g)) return null;
        var b = g.ToByteArray();
        ulong first = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(b), second = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(8));
        string a = first.ToString("X16", CultureInfo.InvariantCulture), c = second.ToString("X16", CultureInfo.InvariantCulture);
        return $"{{{a[..8]}-{a[8..12]}-{a[12..]}-{c[..4]}-{c[4..]}}}";
    }

    static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static string Cut(string bytes, int maxBytes)
    {
        // "AA BB CC": 3 characters a byte
        if (maxBytes == int.MaxValue || bytes.Length <= maxBytes * 3) return bytes;
        int total = (bytes.Length + 1) / 3;
        return bytes[..(maxBytes * 3 - 1)] + $" … ({(total - maxBytes).ToString("N0", CultureInfo.InvariantCulture)} more bytes)";
    }

    /// <summary>The card as a structured object: the same parts as fields.</summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["record"] = Label, ["formKey"] = FormKey, ["signature"] = Signature, ["type"] = Type, ["plugin"] = Plugin,
            ["isOverride"] = IsOverride, ["offset"] = Offset, ["size"] = Size, ["compressed"] = Compressed, ["language"] = Language,
        };
        if (Parent != null) o["parent"] = Parent;
        o["fields"] = Arr(Fields);
        if (Notes.Count > 0) o["notes"] = new JsonArray(Notes.Select(n => (JsonNode)n).ToArray());
        return o;

        static JsonArray Arr(List<CardField> fields)
        {
            var a = new JsonArray();
            foreach (var f in fields)
            {
                var j = new JsonObject { ["path"] = f.Path, ["name"] = f.Name, ["readable"] = f.Readable, ["kind"] = f.Kind };
                if (f.Signature != null) j["signature"] = f.Signature;
                if (f.Variant != null) j["variant"] = f.Variant;
                if (f.Children == null) { j["value"] = f.Json?.DeepClone(); j["text"] = f.Text; }
                // A sound's event: its GUID in Wwise's form, and its name and path where a sound bank names it (GAPS S191)
                if (f.Children == null && f.Kind == "Guid" && WwiseGuid(f.Text) is { } wwise)
                {
                    j["wwise"] = wwise;
                    if (WwiseEvents.Default is { } events && events.TryGet(wwise, out var ev)) { j["wwiseName"] = ev.Name; j["wwisePath"] = ev.Path; }
                }
                if (f.Unit != null) j["unit"] = f.Unit;
                if (f.Languages != null) j["languages"] = new JsonArray(f.Languages.Select(l => (JsonNode)l).ToArray());
                if (f.IsDefault) j["default"] = true;
                if (f.Unknown) { j["unknown"] = true; if (f.Bytes != null) j["bytes"] = f.Bytes; if (f.Evidence != null) j["evidence"] = f.Evidence; }
                if (!f.InSchema) j["inSchema"] = false;
                if (f.Note != null) j["note"] = f.Note;
                if (f.Position != null) j["blobOffset"] = f.Position;
                if (f.Children != null) j["fields"] = Arr(f.Children);
                a.Add(j);
            }
            return a;
        }
    }
}

/// <summary>Where a field sits in the file, carried down the walk the way the schema's own expansion carries it.</summary>
readonly record struct FileContext(string? Subrecord, string? Map, int? Base, string? Generic);

/// <summary>Walks a record's JSON beside the schema.</summary>
public sealed class CardBuilder
{
    readonly RecordSchema _schema;
    readonly Func<string, string> _label;
    readonly string _signature;
    readonly string _language;
    readonly Func<RawRecord?>? _raw;
    readonly Func<string, uint?>? _formId;
    RawRecord? _rawRecord; bool _rawAsked;
    Dictionary<string, List<RawSubrecord>>? _bySignature;
    static readonly List<RawSubrecord> NoSubrecords = new();

    /// <param name="label">Turns a FormKey into EditorID (FormID:plugin).</param>
    /// <param name="raw">Gives the record's raw bytes when an unknown field's slice is wanted (may give null).</param>
    /// <param name="formId">Gives the FormID the plugin's file writes for a FormKey (for an unknown link's bytes).</param>
    public CardBuilder(RecordSchema schema, Func<string, string> label, string signature, string language = "English", Func<RawRecord?>? raw = null, Func<string, uint?>? formId = null)
    {
        _schema = schema; _label = label; _signature = signature; _language = language; _raw = raw; _formId = formId;
    }

    /// <summary>For a page of a heavy record's list: the indexes in the whole list of the items read, by the list's
    /// top-level field name. The items are named by those indexes ([5000], not [0]).</summary>
    public IReadOnlyDictionary<string, int[]>? ListItems { get; init; }

    public List<CardField> Build(string type, JsonObject json) => Object(type, json, "", new FileContext(null, null, 0, null));

    readonly Dictionary<(string, string), string> _plains = new();
    readonly Dictionary<string, string> _spaced = new(StringComparer.Ordinal);
    static readonly string[] IndexNames = Enumerable.Range(0, 4096).Select(i => $"[{i}]").ToArray();

    /// <summary>The path of a field with indexes left out (ObjectTemplates[*].Includes[*].Mod): the key of the checked
    /// xEdit names. The same text for every item of a list, so it is made once.</summary>
    string Plain(string parent, string add)
    {
        if (parent.Length == 0) return add;
        if (!_plains.TryGetValue((parent, add), out var p)) _plains[(parent, add)] = p = add.Length == 0 ? parent : parent + "." + add;
        return p;
    }

    List<CardField> Object(string? obj, JsonObject json, string plain, FileContext ctx)
    {
        var list = new List<CardField>(json.Count);
        HashSet<string>? seen = null;
        if (obj != null)
            foreach (var p in _schema.Props(obj))
            {
                if (!json.TryGetPropertyValue(p.Name, out var v)) continue;
                (seen ??= new HashSet<string>(StringComparer.Ordinal)).Add(p.Name);
                list.Add(Field(p.Main, p, p.Name, v, Plain(plain, p.Main.Path), ctx));
            }
        // What the schema doesn't know is shown all the same, after the fields it knows
        if (seen == null || seen.Count < json.Count)
            foreach (var (k, v) in json)
            {
                if (k == RecordSplit.TypeKey || (seen != null && seen.Contains(k))) continue;
                // The serializer's round-trip notes (how the record was stored, 1.38.6-fixes.3) are no value of the record
                if (SerializerNotes.RecordKeys.Contains(k)) continue;
                list.Add(Field(null, null, k, v, Plain(plain, k), ctx));
            }
        return list;
    }

    /// <summary>The subrecord and offset of a field in its context: the rule of SchemaModel's own expansion.</summary>
    (string? Sub, int? Abs, FileContext Child, string? Enum) Place(FieldDef f, FileContext ctx)
    {
        var owner = _schema.Model.Objects.GetValueOrDefault(f.Object);
        string? map = f.SigMap ?? ctx.Map;
        string? raw = f.Subrecord ?? (f.RefObject != null && _schema.Model.Objects.TryGetValue(f.RefObject, out var ro) ? ro.Sig : null) ?? (owner is { Struct: true } ? ctx.Subrecord : null);
        string? sub = SchemaModel.MapSig(map, raw);
        int? abs = f.Offset == null ? null : f.OffsetRel ? ctx.Base + f.Offset : f.Offset;
        string? gen = f.GenericArg == "T" ? ctx.Generic : f.GenericArg;
        return (sub, abs, new FileContext(sub, map, f.StartsSubrecord ? 0 : abs, gen), f.Enum == "T" ? ctx.Generic : f.Enum);
    }

    string Readable(string name, string plain, FieldDef? def)
    {
        if (_schema.ReadablePair(_signature, plain) is { } pair) return pair.Trim();
        if (def?.Readable is { Length: > 0 } r) return r.Trim();
        if (!_spaced.TryGetValue(name, out var s)) _spaced[name] = s = Spaced(name);
        return s;
    }

    CardField Field(FieldDef? def, SchemaProp? prop, string name, JsonNode? value, string plain, FileContext ctx, bool structural = false)
    {
        var (sub, abs, childCtx, enumName) = def == null ? (null, null, ctx, null) : Place(def, ctx);
        bool isItem = name.StartsWith('[');
        string readable = isItem ? name : Readable(name, plain, def);
        string kind = def?.Kind ?? Looks(value);
        bool unknown = def is { Unknown: true };
        string? evidence = unknown ? _schema.Evidence(_signature, def!.Object, def.Name) : null;

        // The card's value is the JSON's own node, not a copy: the record's JSON must not be changed afterwards
        CardField Value(string k, string text, string? bytes = null, IReadOnlyList<string>? languages = null, string? note = null) => new()
        {
            Name = name, Readable = readable, Signature = sub, Kind = k, Json = value, Text = text,
            Unknown = unknown, Bytes = unknown ? bytes ?? SliceOrEncode(def!, sub, abs, value) : bytes, Evidence = evidence, Unit = def?.Unit,
            Languages = languages, InSchema = def != null || structural || isItem, Note = note,
        };
        CardField Parent(string k, List<CardField> children, string? variant = null, string? obj = null)
        {
            var p = new CardField
            {
                Name = name, Readable = readable, Signature = sub, Kind = k, Children = children, Variant = variant, Object = obj,
                Unknown = unknown, Evidence = evidence, InSchema = def != null || structural || isItem,
            };
            foreach (var c in children) c.Up = p;
            return p;
        }

        switch (value)
        {
            case null:
                return Value(kind, "null");
            case JsonObject o when RecordSplit.ChildOf(o) is string childKey:
                return Value("Child", "→ " + _label(childKey));
            case JsonObject o when SerializerNotes.IsText(o):
            {
                var (text, languages, note) = Translated(o);
                return Value("Text", text, null, languages, note);
            }
            case JsonObject o:
            {
                if (prop != null && prop.Parts.Count > 0 && def!.Kind == "Gendered")
                {
                    // The two halves of a gendered value are described by their own schema rows
                    var halves = new List<CardField>();
                    foreach (var (k, v) in o)
                    {
                        prop.Parts.TryGetValue(k, out var part);
                        var partProp = part == null ? null : new SchemaProp { Name = k, Main = part, Item = prop.PartItems.GetValueOrDefault(k) };
                        halves.Add(Field(part, partProp, k, v, part == null ? Plain(plain, k) : PlainOf(plain, def, part), ctx));
                    }
                    return Parent("Gendered", halves);
                }
                string? variant = o[RecordSplit.TypeKey] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : null;
                string? inner = _schema.ObjectFor(def?.RefObject, variant);
                var fields = Object(inner, o, plain, childCtx);
                // A reflection blob (REFL and the like): its value decoded below its bytes, with the Creation Kit's names (to-do F30)
                if (inner == ReflectionView.ObjectName && ReflectionView.Decoded(o, _label, _schema.Model.Ck) is { } decoded) fields.Add(decoded);
                return Parent(def == null ? "Object" : kind, fields, variant, inner);
            }
            case JsonArray a:
            {
                // Flags: an enum whose JSON is a list of member names
                if (def is { Kind: "Enum" } && a.All(x => x is JsonValue))
                    return Value("Flags", "[" + string.Join(", ", a.Select(x => x!.GetValue<JsonElement>().ToString())) + "]");
                var itemDef = prop?.Item;
                var itemProp = itemDef == null ? null : new SchemaProp { Name = name, Main = itemDef };
                string itemPlain = itemDef == null || def == null ? Plain(plain + "[*]", "") : PlainOf(plain, def, itemDef);
                var items = new List<CardField>(a.Count);
                int[]? read = ListItems != null && ListItems.TryGetValue(plain, out var r) && r.Length == a.Count ? r : null;
                for (int i = 0; i < a.Count; i++)
                {
                    int n = read != null ? read[i] : i;
                    string index = n < IndexNames.Length ? IndexNames[n] : $"[{n}]";
                    if (a[i] is JsonObject pair && def is { Kind: "Dictionary" } && pair.Count == 2 && pair.ContainsKey("Key") && pair.ContainsKey("Value"))
                    {
                        // A dictionary is a list of key and value; the value is what the item row describes
                        var kv = new List<CardField>
                        {
                            Field(null, null, "Key", pair["Key"], Plain(itemPlain, "Key"), childCtx, structural: true),
                            Field(itemDef, itemProp, "Value", pair["Value"], itemPlain, childCtx),
                        };
                        var entry = new CardField { Name = index, Readable = index, Kind = "Entry", Children = kv };
                        foreach (var c in kv) c.Up = entry;
                        items.Add(entry);
                    }
                    // A grid is a list of rows: each row is the same field again, and its items are the grid's items
                    else if (a[i] is JsonArray && def is { Kind: "Array2D" } && itemDef != null) items.Add(Field(def, prop, index, a[i], plain, ctx));
                    else items.Add(Field(itemDef, itemProp, index, a[i], itemPlain, itemDef == null ? childCtx : ctx));
                }
                return Parent(def == null ? "List" : kind, items);
            }
            default:
            {
                var el = value.GetValue<JsonElement>();
                if (el.ValueKind == JsonValueKind.String)
                {
                    string s = el.GetString()!;
                    if (kind is "FormLink" or "FormKey" || RecordSplit.IsFormKey(s) || (s == "Null" && kind == "FormLinkOrIndex"))
                        return Value(def == null ? "FormLink" : kind, kind == "FormKey" ? s : _label(s));
                    // A byte field with no bytes: the serializer writes "0x" (1.38.6-fixes.2 on; "[]" before). The card
                    // shows it as it always has: "[]", no bytes.
                    if (kind == "ByteArray" && FieldLookup.IsNoBytes(s)) return Value("ByteArray", "[]", "(none)");
                    if (kind == "ByteArray" && s.StartsWith("0x", StringComparison.Ordinal))
                    {
                        string spaced = SpacedHex(s.AsSpan(2));
                        return Value("ByteArray", spaced, spaced);
                    }
                    return Value(enumName != null && def!.Kind == "Enum" ? "Enum" : kind, s);
                }
                return Value(kind, el.GetRawText());
            }
        }
    }

    /// <summary>The plain path of a row below a property: the parent's plain path with the row's own path in place of the property's.</summary>
    static string PlainOf(string plainOfMain, FieldDef main, FieldDef below) =>
        plainOfMain.EndsWith(main.Path, StringComparison.Ordinal) ? plainOfMain[..^main.Path.Length] + below.Path : plainOfMain + below.Path[main.Path.Length..];

    static string Looks(JsonNode? v) => v switch
    {
        null => "Null",
        JsonObject => "Object",
        JsonArray => "List",
        _ => v.GetValue<JsonElement>().ValueKind switch { JsonValueKind.String => "String", JsonValueKind.Number => "Number", JsonValueKind.True or JsonValueKind.False => "Bool", _ => "Value" },
    };

    /// <summary>"AttackDamage" as "Attack Damage".</summary>
    public static string Spaced(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])))) sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The text in the chosen language, found by the language's name. When the record has no text in that
    /// language, the first text it has is given with its language named.</summary>
    (string Text, IReadOnlyList<string> Languages, string? Note) Translated(JsonObject o)
    {
        if (o["Values"] is JsonArray values)
        {
            var languages = new List<string>(values.Count);
            string? found = null, first = null, firstLanguage = null;
            foreach (var v in values)
            {
                if (v is not JsonObject e) continue;
                string lang = e["Language"]?.GetValue<string>() ?? "";
                string text = e["String"]?.GetValue<string>() ?? "";
                languages.Add(lang);
                if (first == null) { first = text; firstLanguage = lang; }
                if (found == null && string.Equals(lang, _language, StringComparison.OrdinalIgnoreCase)) found = text;
            }
            return (NoTag(found) ?? (first == null ? "" : $"(no {_language} text; {firstLanguage}: {NoTag(first)})"), languages, null);
        }
        string target = o["TargetLanguage"]?.GetValue<string>() ?? "";
        // A plugin that isn't localized but ships strings tables (GAPS S190, TagTexts.cs): the tables' texts that differ
        // from the plugin's own. Asked in another language than English, the table's text; in English the plugin's
        // own; the field's note says which it is, and what the other says
        if (o["Tables"] is JsonObject tables && o["Value"] is JsonValue sv && sv.TryGetValue<string>(out var stored))
        {
            string own = NoTag(stored) ?? "";
            string? Table(string language) => tables.FirstOrDefault(t => t.Key.Equals(language, StringComparison.OrdinalIgnoreCase)).Value is JsonValue tv && tv.TryGetValue<string>(out var tt) ? tt : null;
            var has = new List<string> { "English" };
            has.AddRange(tables.Select(t => t.Key).Where(k => !k.Equals("English", StringComparison.OrdinalIgnoreCase)));
            if (!_language.Equals("English", StringComparison.OrdinalIgnoreCase) && Table(_language) is { } asked)
                return (asked, has, $"from the plugin's own {_language} strings table; the plugin itself stores: {own}");
            return (own, has, Table("English") is { } older ? $"the plugin's own English strings table says: {older}" : null);
        }
        return (NoTag(o["Value"]?.GetValue<string>()) ?? "", o.ContainsKey("Value") ? new[] { target } : Array.Empty<string>(), null);
    }

    /// <summary>
    /// A text as a person reads it: the tag the Creation Kit writes in front of a text in a plugin with no string
    /// files (<c>&lt;ID=0000793F&gt;AA-99 Mk.II</c>) is taken off. Cards and the fields view show texts this way; the
    /// JSON, the raw view and the evidence view keep the text as the plugin stores it. (Milestone M8.)
    /// </summary>
    public static string? NoTag(string? text)
    {
        if (text == null || text.Length < 5 || !text.StartsWith("<ID=", StringComparison.Ordinal)) return text;
        int end = text.IndexOf('>');
        if (end < 5) return text;
        for (int i = 4; i < end; i++) if (!Uri.IsHexDigit(text[i])) return text;
        return text[(end + 1)..];
    }

    static string SpacedHex(ReadOnlySpan<char> hex)
    {
        var sb = new StringBuilder(hex.Length * 3 / 2);
        for (int i = 0; i + 1 < hex.Length; i += 2) { if (i > 0) sb.Append(' '); sb.Append(char.ToUpperInvariant(hex[i])).Append(char.ToUpperInvariant(hex[i + 1])); }
        return sb.ToString();
    }

    /// <summary>
    /// An unknown field's bytes. The file's own bytes wherever they can be told: the field's slice when the schema
    /// knows its offset and size and the record has that subrecord once; the whole subrecord when the field is all
    /// of it; for a text, the subrecord that holds that text. Otherwise the value written out the way the file
    /// stores its kind.
    /// </summary>
    string? SliceOrEncode(FieldDef def, string? sub, int? abs, JsonNode? value)
    {
        if (sub != null)
        {
            if (!_rawAsked) { _rawAsked = true; _rawRecord = _raw?.Invoke(); }
            if (_rawRecord != null)
            {
                // The record's subrecords by signature, made once: a record can have tens of thousands
                if (_bySignature == null)
                {
                    _bySignature = new Dictionary<string, List<RawSubrecord>>(StringComparer.Ordinal);
                    foreach (var s in _rawRecord.Subrecords)
                        (_bySignature.TryGetValue(s.Signature, out var l) ? l : _bySignature[s.Signature] = new List<RawSubrecord>()).Add(s);
                }
                var hits = _bySignature.TryGetValue(sub, out var found) ? found : NoSubrecords;
                if (hits.Count == 1 && !def.InList)
                {
                    if (abs != null && def.Size is int size && size > 0 && abs.Value + size <= hits[0].Length)
                        return RawRecord.Hex(_rawRecord.Content(hits[0]).Slice(abs.Value, size));
                    if (def.Offset == null && def.Subrecord == sub)
                        return RawRecord.Hex(_rawRecord.Content(hits[0]));
                }
                if (def.Kind == "String" && hits.Count <= 256 && value is JsonValue sv && sv.TryGetValue<string>(out var text))
                {
                    // Several subrecords of this kind (one per list item): the one that holds this text
                    var utf8 = Encoding.UTF8.GetBytes(text); var latin = Encoding.Latin1.GetBytes(text);
                    foreach (var h in hits)
                    {
                        var content = _rawRecord.Content(h);
                        var body = content.Length > 0 && content[^1] == 0 ? content[..^1] : content;
                        if (body.SequenceEqual(utf8) || body.SequenceEqual(latin)) return RawRecord.Hex(content);
                    }
                }
            }
        }
        if (def.Kind == "FormLink" && value is JsonValue lv && lv.TryGetValue<string>(out var key) && _formId?.Invoke(key) is uint id)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, id);
            return RawRecord.Hex(b);
        }
        return Encode(def.Kind, def.Size, value);
    }

    /// <summary>A value as little-endian bytes of its kind, or null when the kind has no one fixed form.</summary>
    public static string? Encode(string kind, int? size, JsonNode? value)
    {
        if (value is not JsonValue v) return null;
        var el = v.GetValue<JsonElement>();
        Span<byte> b = stackalloc byte[8];
        try
        {
            switch (kind)
            {
                case "UInt8" when el.ValueKind == JsonValueKind.Number: b[0] = el.GetByte(); return RawRecord.Hex(b[..1]);
                case "Int8" when el.ValueKind == JsonValueKind.Number: b[0] = (byte)el.GetSByte(); return RawRecord.Hex(b[..1]);
                case "UInt16" when el.ValueKind == JsonValueKind.Number: BinaryPrimitives.WriteUInt16LittleEndian(b, el.GetUInt16()); return RawRecord.Hex(b[..2]);
                case "Int16" when el.ValueKind == JsonValueKind.Number: BinaryPrimitives.WriteInt16LittleEndian(b, el.GetInt16()); return RawRecord.Hex(b[..2]);
                case "UInt32" when el.ValueKind == JsonValueKind.Number: BinaryPrimitives.WriteUInt32LittleEndian(b, el.GetUInt32()); return RawRecord.Hex(b[..4]);
                case "Int32" when el.ValueKind == JsonValueKind.Number: BinaryPrimitives.WriteInt32LittleEndian(b, el.GetInt32()); return RawRecord.Hex(b[..4]);
                case "UInt64" when el.ValueKind == JsonValueKind.Number: BinaryPrimitives.WriteUInt64LittleEndian(b, el.GetUInt64()); return RawRecord.Hex(b[..8]);
                case "Int64" when el.ValueKind == JsonValueKind.Number: BinaryPrimitives.WriteInt64LittleEndian(b, el.GetInt64()); return RawRecord.Hex(b[..8]);
                case "Float" when el.ValueKind == JsonValueKind.Number: BinaryPrimitives.WriteSingleLittleEndian(b, el.GetSingle()); return RawRecord.Hex(b[..4]);
                case "Double" when el.ValueKind == JsonValueKind.Number: BinaryPrimitives.WriteDoubleLittleEndian(b, el.GetDouble()); return RawRecord.Hex(b[..8]);
                case "Half" when FieldLookup.TryReadHalf(el, out Half h): BinaryPrimitives.WriteHalfLittleEndian(b, h); return RawRecord.Hex(b[..2]);
                case "String" when el.ValueKind == JsonValueKind.String:
                    // A text as the file stores it: its bytes and a closing zero
                    return RawRecord.Hex(Encoding.UTF8.GetBytes(el.GetString()! + (char)0));
                case "P2Float" or "P3Float" when el.ValueKind == JsonValueKind.String:
                {
                    var parts = el.GetString()!.Split(',');
                    var all = new byte[parts.Length * 4];
                    for (int i = 0; i < parts.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(all.AsSpan(i * 4), float.Parse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture));
                    return RawRecord.Hex(all);
                }
                case "Bool" when el.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    b.Clear(); b[0] = el.GetBoolean() ? (byte)1 : (byte)0; return RawRecord.Hex(b[..Math.Clamp(size ?? 1, 1, 8)]);
            }
        }
        catch (FormatException) { }
        catch (OverflowException) { }
        return null;
    }
}

/// <summary>
/// What the translated texts of a record hold against the language asked for: texts a plugin with no strings files
/// keeps in itself in one language, and texts of a localized plugin that have no text in the language asked.
/// </summary>
public sealed class TextCensus
{
    /// <summary>Texts stored in their plugin in one language that isn't the one asked for.</summary>
    public int OneLanguage { get; private set; }
    /// <summary>The languages those texts are in, as Mutagen names them.</summary>
    public SortedSet<string> Stored { get; } = new(StringComparer.Ordinal);
    /// <summary>Texts from strings files with no text in the language asked for.</summary>
    public int Missing { get; private set; }
    /// <summary>The languages those texts do have.</summary>
    public SortedSet<string> Held { get; } = new(StringComparer.Ordinal);
    /// <summary>Texts given from the plugin's own strings tables, by the ID in their tag (GAPS S190).</summary>
    public int FromTables { get; private set; }

    /// <summary>A translated text as the JSON writes it: an object with "TargetLanguage" and "Value" or "Values" (and its
    /// string ID, since the serializer's 1.38.6-fixes.3).</summary>
    public static bool IsText(JsonObject o) => SerializerNotes.IsText(o);

    /// <summary>Looks at every translated text in <paramref name="node"/> and below it.</summary>
    public void Scan(JsonNode? node, string language)
    {
        switch (node)
        {
            case JsonObject o when IsText(o):
                if (o["Values"] is JsonArray values)
                {
                    var have = values.OfType<JsonObject>().Select(v => v["Language"]?.GetValue<string>() ?? "").ToList();
                    if (have.Count > 0 && !have.Contains(language, StringComparer.OrdinalIgnoreCase))
                    {
                        Missing++;
                        foreach (var l in have) Held.Add(l);
                    }
                }
                // (a plugin that isn't localized but ships strings tables, asked in another language than English: the
                // table's text is given, TagTexts.cs)
                else if (!language.Equals("English", StringComparison.OrdinalIgnoreCase) && o["Tables"] is JsonObject tables && tables.Any(t => t.Key.Equals(language, StringComparison.OrdinalIgnoreCase)))
                    FromTables++;
                else if (o["Value"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0
                         && o["TargetLanguage"]?.GetValue<string>() is { } stored && !stored.Equals(language, StringComparison.OrdinalIgnoreCase))
                {
                    OneLanguage++;
                    Stored.Add(stored);
                }
                break;
            case JsonObject o:
                foreach (var (_, child) in o) Scan(child, language);
                break;
            case JsonArray a:
                foreach (var child in a) Scan(child, language);
                break;
        }
    }
}
