// The one diff engine. It walks two records' JSON (Mutagen.Bethesda.Serialization's full profile, as SFSearch.Records
// gives it) beside the schema and reports each difference with the readable name, the subrecord and both values.
//  - Defaults: a field the schema says is always there, left out on one side, is given its default before comparing.
//  - Links are compared by the record they name (plugin names letter case aside), and shown as EditorID (FormID:plugin).
//  - A list of links is a set (entries counted). When only the order differs, that is one difference of its own kind.
//  - A list of objects is paired by a key field the schema makes evident (ListKeys), else by position. Entries that
//    are wholly equal are paired first, wherever they stand.
//  - Records nested in a record ({"$Child": FormKey}) are records of their own: they are collected, never compared
//    as fields of the record that holds them.
//  - What differs without being a value of the record is kept apart (DiffClass): the header's numbers, a data
//    block's length, the other languages of a translated text.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SFSearch.Records;
using SFSearch.SchemaGen;

namespace SFSearch.Compare;

public sealed class DiffOptions
{
    /// <summary>Turns a FormKey into EditorID (FormID:plugin).</summary>
    public required Func<string, string> Label { get; init; }
    /// <summary>Turns a FormKey of the first record into what it is compared by. Null: the FormKey itself. Used when
    /// records are paired by EditorID (a link to the plugin's own record is compared by that record's EditorID) and
    /// when a file is read under another name.</summary>
    public Func<string, string>? CanonA { get; init; }
    public Func<string, string>? CanonB { get; init; }
    /// <summary>The language a translated text is compared and shown in.</summary>
    public string Language { get; init; } = "English";
    /// <summary>The most characters of one value in a difference's text. The whole value is always in the JSON.</summary>
    public int MaxValueChars { get; init; } = 400;
}

public sealed class RecordDiffer
{
    readonly RecordSchema _schema;
    readonly ListKeys _keys;

    public RecordDiffer(RecordSchema schema) { _schema = schema; _keys = new ListKeys(schema); }

    public ListKeys Keys => _keys;

    /// <summary>The header's own numbers and the file's way of storing the record: never a value of the record.</summary>
    static readonly HashSet<string> HeaderNames = new(StringComparer.Ordinal) { "FormVersion", "Version2", "VersionControl", "IsCompressed" };
    /// <summary>The same bits as MajorRecordFlagsRaw, by name. They are shown with it, not compared a second time.</summary>
    static readonly HashSet<string> FlagViews = new(StringComparer.Ordinal) { "StarfieldMajorRecordFlags", "MajorFlags" };
    const string RawFlags = "MajorRecordFlagsRaw";
    const long CompressedBit = 0x00040000;
    const long PartialFormBit = 0x00004000;

    /// <summary>Compares two records given as JSON. Children markers may be in the JSON; they are collected.</summary>
    /// <param name="typeA">The Mutagen type of the first record (Weapon, WeaponModification).</param>
    public RecordDiff Diff(string typeA, JsonObject a, string typeB, JsonObject b, DiffOptions options)
    {
        var result = new RecordDiff { TypeA = typeA, TypeB = typeB, SignatureA = _schema.SignatureOf(typeA), SignatureB = _schema.SignatureOf(typeB) };
        if (result.DifferentTypes) return result;
        new Walk(_schema, _keys, options, result, typeA, a, b).Run();
        return result;
    }

    /// <summary>Where a field sits in the file, carried down the walk the way the schema's own expansion carries it.</summary>
    readonly record struct Place(string? Subrecord, string? Map, int? Base, string? Generic);

    sealed class Walk
    {
        readonly RecordSchema _s; readonly ListKeys _keys; readonly DiffOptions _o; readonly RecordDiff _r;
        readonly string _type, _sig; readonly JsonObject _a, _b; readonly bool _holder;
        string _top = "", _topReadable = ""; string? _topSig; DiffClass _topClass;
        readonly HashSet<string> _childA = new(StringComparer.OrdinalIgnoreCase), _childB = new(StringComparer.OrdinalIgnoreCase);

        public Walk(RecordSchema s, ListKeys keys, DiffOptions o, RecordDiff r, string type, JsonObject a, JsonObject b)
        {
            _s = s; _keys = keys; _o = o; _r = r; _type = type; _a = a; _b = b;
            _sig = s.SignatureOf(type) ?? ""; _holder = s.HoldsRecords(type);
        }

        public void Run()
        {
            Object(_type, _a, _b, "", "", "", new Place(null, null, 0, null), root: true);
            foreach (var k in _childA) (_childB.Contains(k) ? _r.ChildrenInBoth : _r.ChildrenOnlyA).Add(k);
            foreach (var k in _childB) if (!_childA.Contains(k)) _r.ChildrenOnlyB.Add(k);
            _r.PartialFormA = IsPartial(_a); _r.PartialFormB = IsPartial(_b);

            static bool IsPartial(JsonObject record) => record[RawFlags] is { } raw && long.TryParse(raw.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long bits) && (bits & PartialFormBit) != 0;
        }

        // ------------------------------------------------------------------ Names and places
        static string Join(string path, string name) => path.Length == 0 ? name : name.StartsWith('[') ? path + name : path + "." + name;
        static string Plain(string parent, string add) => parent.Length == 0 ? add : add.Length == 0 ? parent : parent + "." + add;
        static string PlainOf(string plainOfMain, FieldDef main, FieldDef below) =>
            plainOfMain.EndsWith(main.Path, StringComparison.Ordinal) ? plainOfMain[..^main.Path.Length] + below.Path : plainOfMain + below.Path[main.Path.Length..];

        (string? Sub, Place Child) PlaceOf(FieldDef f, Place ctx)
        {
            var owner = _s.Model.Objects.GetValueOrDefault(f.Object);
            string? map = f.SigMap ?? ctx.Map;
            string? raw = f.Subrecord ?? (f.RefObject != null && _s.Model.Objects.TryGetValue(f.RefObject, out var ro) ? ro.Sig : null) ?? (owner is { Struct: true } ? ctx.Subrecord : null);
            string? sub = SchemaModel.MapSig(map, raw);
            int? abs = f.Offset == null ? null : f.OffsetRel ? ctx.Base + f.Offset : f.Offset;
            string? gen = f.GenericArg == "T" ? ctx.Generic : f.GenericArg;
            return (sub, new Place(sub, map, f.StartsSubrecord ? 0 : abs, gen));
        }

        string Readable(string name, string plain, FieldDef? def)
        {
            if (_s.ReadablePair(_sig, plain) is { } pair) return pair;
            if (def?.Readable is { Length: > 0 } r) return r;
            return CardBuilder.Spaced(name);
        }

        // ------------------------------------------------------------------ Objects and fields
        void Object(string? obj, JsonObject? a, JsonObject? b, string path, string shown, string plain, Place ctx, bool root = false)
        {
            HashSet<string>? seen = null;
            string? before = null;
            if (obj != null)
                foreach (var p in _s.Props(obj))
                {
                    JsonNode? va = null, vb = null;
                    bool ha = a != null && a.TryGetPropertyValue(p.Name, out va), hb = b != null && b.TryGetPropertyValue(p.Name, out vb);
                    (seen ??= new HashSet<string>(StringComparer.Ordinal)).Add(p.Name);
                    string? previous = before; before = p.Name;
                    if (!ha && !hb) continue;
                    if (root && !BeginTop(p.Name, p.Main, previous, Plain(plain, p.Main.Path), ctx)) continue;
                    Field(p.Main, p, p.Name, ha, va, hb, vb, Join(path, p.Name), Join(shown, p.Name), Plain(plain, p.Main.Path), ctx);
                }
            // What the schema doesn't know is compared all the same, after the fields it knows
            var extra = new List<string>();
            if (a != null) foreach (var (k, _) in a) if (k != RecordSplit.TypeKey && (seen == null || !seen.Contains(k))) extra.Add(k);
            if (b != null) foreach (var (k, _) in b) if (k != RecordSplit.TypeKey && (seen == null || !seen.Contains(k)) && !extra.Contains(k)) extra.Add(k);
            foreach (var k in extra)
            {
                JsonNode? va = null, vb = null;
                bool ha = a != null && a.TryGetPropertyValue(k, out va), hb = b != null && b.TryGetPropertyValue(k, out vb);
                if (root && !BeginTop(k, null, null, Plain(plain, k), ctx)) continue;
                Field(null, null, k, ha, va, hb, vb, Join(path, k), Join(shown, k), Plain(plain, k), ctx);
            }
        }

        /// <summary>Starts one of the record's own fields. False: the field isn't compared at all.</summary>
        bool BeginTop(string name, FieldDef? def, string? previous, string plain, Place ctx)
        {
            // The serializer's round-trip notes (how each record was stored: SerializerNotes.RecordKeys) aren't compared
            if (name == "FormKey" || FlagViews.Contains(name) || (def == null && SerializerNotes.RecordKeys.Contains(name))) return false;
            _top = name; _topReadable = Readable(name, plain, def);
            _topSig = def == null ? null : PlaceOf(def, ctx).Sub;
            _topClass = HeaderNames.Contains(name) ? DiffClass.Header
                : IsLayout(name) ? DiffClass.Layout
                // The header values of the group that holds a record's children: not part of the record
                : _holder && def is { Kind: "Int32" } && (name.EndsWith("Timestamp", StringComparison.Ordinal) || name.EndsWith("UnknownGroupData", StringComparison.Ordinal)
                    || name == "SubCellsUnknown" || (name == "Unknown" && previous == "Timestamp")) ? DiffClass.Header
                : DiffClass.Value;
            return true;
        }

        static bool IsLayout(string name) => name == "Versioning" || name.EndsWith("DataTypeState", StringComparison.Ordinal);

        void Field(FieldDef? def, SchemaProp? prop, string name, bool ha, JsonNode? va, bool hb, JsonNode? vb, string path, string shown, string plain, Place ctx)
        {
            var (sub, childCtx) = def == null ? (null, ctx) : PlaceOf(def, ctx);
            // A field that is always there and left out on one side has its default there

            if (ha != hb && def is { Nullable: false } && FieldLookup.DefaultOf(_s, def).Json is { } d)
            {
                if (!ha) { va = d; ha = true; } else { vb = d; hb = true; }
            }
            if (ha && hb && va == null && vb == null) return;
            if (ContainsChild(va) | ContainsChild(vb)) { Children(va, _childA); Children(vb, _childB); return; }

            if (!ha || !hb)
            {
                Leaf(def, name, path, shown, plain, sub, ha ? Change.OnlyA : Change.OnlyB, va, vb, ha, hb);
                return;
            }
            switch (va, vb)
            {
                case (JsonObject oa, JsonObject ob) when IsTranslated(oa) || IsTranslated(ob):
                    Translated(def, name, path, shown, plain, sub, oa, ob);
                    return;
                case (JsonObject oa, JsonObject ob):
                {
                    if (prop != null && prop.Parts.Count > 0 && def!.Kind == "Gendered")
                    {
                        foreach (var k in oa.Select(p => p.Key).Union(ob.Select(p => p.Key)).ToList())
                        {
                            prop.Parts.TryGetValue(k, out var part);
                            var partProp = part == null ? null : new SchemaProp { Name = k, Main = part, Item = prop.PartItems.GetValueOrDefault(k) };
                            bool pa = oa.TryGetPropertyValue(k, out var xa), pb = ob.TryGetPropertyValue(k, out var xb);
                            Field(part, partProp, k, pa, xa, pb, xb, Join(path, k), Join(shown, k), part == null ? Plain(plain, k) : PlainOf(plain, def, part), ctx);
                        }
                        return;
                    }
                    string? ka = Variant(oa), kb = Variant(ob);
                    if (!string.Equals(ka, kb, StringComparison.Ordinal))
                    {
                        Leaf(def, name, path, shown, plain, sub, Change.Changed, va, vb, true, true, note: $"another kind of object: {ka ?? "(none named)"} and {kb ?? "(none named)"}");
                        return;
                    }
                    Object(_s.ObjectFor(def?.RefObject, ka), oa, ob, path, shown, plain, childCtx);
                    return;
                }
                case (JsonArray aa, JsonArray ab):
                    List(def, prop, name, aa, ab, path, shown, plain, sub, ctx, childCtx);
                    return;
                case (JsonValue, JsonValue) or (null, _) or (_, null):
                    if (!ValueEqual(va, vb, name == RawFlags && path == RawFlags)) Leaf(def, name, path, shown, plain, sub, Change.Changed, va, vb, true, true);
                    return;
                default:
                    // An object on one side and a plain value on the other: the two can only be shown
                    Leaf(def, name, path, shown, plain, sub, Change.Changed, va, vb, true, true);
                    return;
            }
        }

        static string? Variant(JsonObject o) => o[RecordSplit.TypeKey] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        // ------------------------------------------------------------------ Nested records
        static bool ContainsChild(JsonNode? n) => n switch
        {
            JsonObject o => RecordSplit.ChildOf(o) != null || o.Any(p => ContainsChild(p.Value)),
            JsonArray a => a.Any(ContainsChild),
            _ => false,
        };

        static void Children(JsonNode? n, HashSet<string> into)
        {
            if (n is JsonObject o)
            {
                if (RecordSplit.ChildOf(o) is string key) into.Add(key);
                else foreach (var p in o) Children(p.Value, into);
            }
            else if (n is JsonArray a) foreach (var x in a) Children(x, into);
        }

        // ------------------------------------------------------------------ Values
        string Canon(bool sideA, string formKey)
        {
            string c = (sideA ? _o.CanonA : _o.CanonB)?.Invoke(formKey) ?? formKey;
            return c.ToUpperInvariant();
        }

        static bool IsLink(string s) => s == "Null" || RecordSplit.IsFormKey(s);

        bool ValueEqual(JsonNode? a, JsonNode? b, bool rawFlags = false)
        {
            if (a == null || b == null) return a == null && b == null;
            var ka = a.GetValueKind(); var kb = b.GetValueKind();
            if (ka == JsonValueKind.String && kb == JsonValueKind.String)
            {
                string sa = a.GetValue<string>(), sb = b.GetValue<string>();
                if (RecordSplit.IsFormKey(sa) && RecordSplit.IsFormKey(sb)) return Canon(true, sa) == Canon(false, sb);
                // A byte field with no bytes is "0x" (the serializer's 1.38.6-fixes.2 on) or "[]" (before): one value
                if (FieldLookup.IsNoBytes(sa) && FieldLookup.IsNoBytes(sb)) return true;
                return string.Equals(sa, sb, StringComparison.Ordinal);
            }
            if (ka == JsonValueKind.Number && kb == JsonValueKind.Number)
            {
                string ra = Raw(a), rb = Raw(b);
                // The compressed bit says how the file stores the record, not what the record is
                if (rawFlags && long.TryParse(ra, NumberStyles.Integer, CultureInfo.InvariantCulture, out long fa) && long.TryParse(rb, NumberStyles.Integer, CultureInfo.InvariantCulture, out long fb))
                    return (fa & ~CompressedBit) == (fb & ~CompressedBit);
                return ra == rb || (double.TryParse(ra, NumberStyles.Float, CultureInfo.InvariantCulture, out double da) && double.TryParse(rb, NumberStyles.Float, CultureInfo.InvariantCulture, out double db) && da.Equals(db));
            }
            return ka == kb && Raw(a) == Raw(b);
        }

        /// <summary>A plain value as JSON writes it, whether it was parsed or made in code.</summary>
        static string Raw(JsonNode n) => n.ToJsonString();
        /// <summary>A plain value as the text it stands for: a string's own text, anything else as JSON writes it.</summary>
        static string PlainText(JsonNode n) => n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString();

        /// <summary>A value as one text that is equal exactly when the two values are: links by what they are compared by.</summary>
        string Canonical(JsonNode? n, bool sideA)
        {
            var sb = new StringBuilder();
            Write(n);
            return sb.ToString();

            void Write(JsonNode? x)
            {
                switch (x)
                {
                    case null: sb.Append("null"); break;
                    case JsonObject o:
                        sb.Append('{');
                        // A translated text is held by its texts, not by the string IDs it carries (C214)
                        bool text = SerializerNotes.IsText(o);
                        foreach (var (k, v) in o) { if (text && SerializerNotes.TextKeys.Contains(k)) continue; sb.Append(k).Append(':'); Write(v); sb.Append(','); }
                        sb.Append('}');
                        break;
                    case JsonArray a:
                        sb.Append('[');
                        foreach (var v in a) { Write(v); sb.Append(','); }
                        sb.Append(']');
                        break;
                    default:
                        if (x.GetValueKind() == JsonValueKind.String)
                        {
                            string s = x.GetValue<string>();
                            sb.Append('"').Append(RecordSplit.IsFormKey(s) ? Canon(sideA, s) : FieldLookup.IsNoBytes(s) ? FieldLookup.NoBytes : s).Append('"');
                        }
                        else sb.Append(Raw(x));
                        break;
                }
            }
        }

        // ------------------------------------------------------------------ Text of a value
        string Text(JsonNode? n, FieldDef? def = null, int depth = 0)
        {
            switch (n)
            {
                case null: return "null";
                case JsonObject o:
                {
                    if (RecordSplit.ChildOf(o) is string child) return "→ " + _o.Label(child);
                    if (IsTranslated(o)) return Quote(Chosen(o).Text ?? "");
                    if (depth > 3) return "{…}";
                    var parts = new List<string>();
                    string? variant = Variant(o);
                    foreach (var (k, v) in o) if (k != RecordSplit.TypeKey) parts.Add($"{k}: {Text(v, null, depth + 1)}");
                    return (variant == null ? "" : variant + " ") + "{" + string.Join(", ", parts) + "}";
                }
                case JsonArray a:
                {
                    if (depth > 3) return $"[{a.Count} items]";
                    var parts = a.Take(12).Select(x => Text(x, null, depth + 1)).ToList();
                    if (a.Count > 12) parts.Add($"… {a.Count - 12} more");
                    return "[" + string.Join(", ", parts) + "]";
                }
                default:
                    if (n.GetValueKind() != JsonValueKind.String) return Raw(n);
                    string s = n.GetValue<string>();
                    if (RecordSplit.IsFormKey(s)) return _o.Label(s);
                    if (s.StartsWith("0x", StringComparison.Ordinal) && s.Length > 66 && s.AsSpan(2).IndexOfAnyExcept("0123456789ABCDEFabcdef") < 0)
                        return s[..66] + $"… ({(s.Length - 2) / 2:N0} bytes)";
                    return depth > 0 || s.Length == 0 || s != s.Trim() || s.Contains('\n') ? Quote(s) : s;
            }
        }

        static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        static string Quote(string s) => JsonSerializer.Serialize(s, Relaxed);

        string Cut(string s) => s.Length <= _o.MaxValueChars ? s : s[.._o.MaxValueChars] + $"… ({s.Length:N0} characters)";

        void Leaf(FieldDef? def, string name, string path, string shown, string plain, string? sub, Change change, JsonNode? va, JsonNode? vb, bool ha, bool hb,
            string? note = null, string? element = null, string? textA = null, string? textB = null, DiffClass? cls = null)
        {
            bool top = path == _top;
            string? a = !ha ? null : textA ?? Cut(Text(va, def)), b = !hb ? null : textB ?? Cut(Text(vb, def));
            if (top && name == RawFlags) { a = ha ? FlagsText(va, _a) : null; b = hb ? FlagsText(vb, _b) : null; }
            _r.All.Add(new FieldDiff
            {
                Top = _top, Path = path, Shown = shown, Element = element, Readable = _topReadable,
                LeafReadable = top ? null : name.StartsWith('[') ? null : Readable(name, plain, def),
                Signature = sub ?? _topSig, TopSignature = _topSig, Change = change,
                Class = cls ?? (_topClass != DiffClass.Value ? _topClass : IsLayout(name) ? DiffClass.Layout : DiffClass.Value),
                A = a, B = b, AJson = ha ? va : null, BJson = hb ? vb : null, Note = note,
                CanonA = ha ? Canonical(va, true) : null, CanonB = hb ? Canonical(vb, false) : null,
            });
        }

        static string FlagsText(JsonNode? raw, JsonObject record)
        {
            long bits = raw != null && long.TryParse(Raw(raw), NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : 0;
            var names = new List<string>();
            foreach (var view in FlagViews)
                if (record[view] is JsonArray list)
                    foreach (var x in list) if (x is JsonValue xv && xv.TryGetValue<string>(out var s) && !s.StartsWith("0x", StringComparison.Ordinal) && !names.Contains(s)) names.Add(s);
            return $"0x{bits:X8}" + (names.Count > 0 ? " [" + string.Join(", ", names) + "]" : "");
        }

        // ------------------------------------------------------------------ Translated text
        // With its string ID since the serializer's 1.38.6-fixes.3 (C214): the texts are compared, not where they are kept
        static bool IsTranslated(JsonObject o) => SerializerNotes.IsText(o);

        /// <summary>The text in the language asked for, found by the language's name, and how many languages the record holds.</summary>
        (string? Text, int Languages) Chosen(JsonObject o)
        {
            if (o["Values"] is JsonArray values)
            {
                string? found = null; int n = 0;
                foreach (var v in values)
                {
                    if (v is not JsonObject e) continue;
                    n++;
                    if (found == null && string.Equals(e["Language"]?.GetValue<string>(), _o.Language, StringComparison.OrdinalIgnoreCase)) found = e["String"]?.GetValue<string>() ?? "";
                }
                return (found, n);
            }
            if (!o.ContainsKey("Value")) return (null, 0);

            // A text with one language is that text in whatever language is asked for: the game shows it to everyone
            return (o["Value"]?.GetValue<string>() ?? "", 1);
        }

        /// <summary>What a translated text holds: no text at all, one text that every language shows (a plugin that
        /// isn't localized), or a text for each language it names (a localized plugin's strings).</summary>
        sealed record Texts(bool None, string? One, List<(string Language, string Text)>? Each)
        {
            public string? In(string language) => None ? null : One ?? Each!.FirstOrDefault(e => string.Equals(e.Language, language, StringComparison.OrdinalIgnoreCase)).Text;
            public string Count => None ? "no text" : One != null ? "one text for every language" : $"{Each!.Count} language{(Each.Count == 1 ? "" : "s")}";
        }

        /// <summary>A few language names: all of three or fewer, else the first two and how many more.</summary>
        static string Few(List<string> languages) => languages.Count <= 3 ? string.Join(", ", languages) : $"{languages[0]}, {languages[1]} and {languages.Count - 2} more";

        static Texts TextsOf(JsonObject o)
        {
            if (o["Values"] is JsonArray values)
                return new Texts(false, null, values.OfType<JsonObject>().Select(e => (e["Language"]?.GetValue<string>() ?? "", e["String"]?.GetValue<string>() ?? "")).ToList());
            if (o.TryGetPropertyValue("Value", out var v) && v != null) return new Texts(false, v.GetValue<string>(), null);
            return new Texts(true, null, null);
        }

        /// <summary>A translated text. Every difference in what a player in any language sees is a change of the field:
        /// the text in the language asked for, a language added or dropped, a language's text, no text against an
        /// empty one. Only the same text in every language, held another way (localized strings against the text
        /// itself), is the same value; it is kept apart as that and still listed.</summary>
        void Translated(FieldDef? def, string name, string path, string shown, string plain, string? sub, JsonObject a, JsonObject b)
        {
            // The same texts with other string IDs (C214, the serializer's 1.38.6-fixes.3) are the same value, as before
            if (SerializerNotes.SameText(a, b)) return;
            var xa = TextsOf(a); var xb = TextsOf(b);
            string? ta = xa.In(_o.Language), tb = xb.In(_o.Language);

            // The languages that show another text (a language a side doesn't hold shows nothing there)
            var languages = (xa.Each ?? new()).Concat(xb.Each ?? new()).Select(e => e.Language).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var differ = languages.Where(l => !string.Equals(xa.In(l), xb.In(l), StringComparison.Ordinal)).ToList();
            var dropped = xa.Each == null || xb.One != null ? new List<string>() : xa.Each.Select(e => e.Language).Where(l => xb.Each == null || !xb.Each.Any(e => string.Equals(e.Language, l, StringComparison.OrdinalIgnoreCase))).ToList();
            var added = xb.Each == null || xa.One != null ? new List<string>() : xb.Each.Select(e => e.Language).Where(l => xa.Each == null || !xa.Each.Any(e => string.Equals(e.Language, l, StringComparison.OrdinalIgnoreCase))).ToList();
            var notes = new List<string>();
            if (xa.None != xb.None)
                notes.Add(xa.None ? (string.IsNullOrEmpty(tb) && (xb.Each == null || xb.Each.All(e => e.Text.Length == 0)) ? "no text, then an empty text" : "no text, then a text")
                    : (string.IsNullOrEmpty(ta) && (xa.Each == null || xa.Each.All(e => e.Text.Length == 0)) ? "an empty text, then no text" : "a text, then no text"));
            if (dropped.Count > 0) notes.Add("languages dropped: " + string.Join(", ", dropped));
            if (added.Count > 0) notes.Add("languages added: " + string.Join(", ", added));
            if ((xa.One == null) != (xb.One == null) && !xa.None && !xb.None)
                notes.Add(xa.One != null ? "one text for every language, then a text for each language" : "a text for each language, then one text for every language");
            var changedIn = differ.Where(l => !dropped.Contains(l, StringComparer.OrdinalIgnoreCase) && !added.Contains(l, StringComparer.OrdinalIgnoreCase)
                && !string.Equals(l, _o.Language, StringComparison.OrdinalIgnoreCase)).ToList();
            if (changedIn.Count > 0)
                notes.Add($"the text changes in {changedIn.Count} other language{(changedIn.Count == 1 ? "" : "s")}: " + string.Join(", ", changedIn.Take(4).Select(l => $"{l} {Quote(xa.In(l) ?? "")} → {Quote(xb.In(l) ?? "")}")) + (changedIn.Count > 4 ? ", …" : ""));

            bool sameShown = string.Equals(ta, tb, StringComparison.Ordinal) && xa.None == xb.None;
            // The two values as text. How many languages each holds, when that differs; and when the text in the
            // language asked for is the same, what changed in the others, so that a compact line says it too
            bool counts = xa.Count != xb.Count && !xa.None && !xb.None;
            var briefly = new List<string>();
            if (sameShown)
            {
                if (dropped.Count > 0) briefly.Add($"{Few(dropped)} dropped");
                if (added.Count > 0) briefly.Add($"{Few(added)} added");
                if (changedIn.Count > 0) briefly.Add($"{Few(changedIn)} change{(changedIn.Count == 1 ? "s" : "")}");
            }
            string Shown(Texts x, string? t, bool second)
            {
                if (x.None) return "(no text)";
                var inside = new List<string>();
                if (counts || (sameShown && !second)) inside.Add(x.Count);
                if (second) inside.AddRange(briefly);
                return Cut(Quote(t ?? "")) + (inside.Count > 0 ? $" ({string.Join("; ", inside)})" : "");
            }
            if (!sameShown || differ.Count > 0 || xa.None != xb.None)
            {
                if (sameShown) notes.Insert(0, $"the same {_o.Language} text");
                Leaf(def, name, path, shown, plain, sub, Change.Changed, a, b, true, true, textA: Shown(xa, ta, false), textB: Shown(xb, tb, true),
                    note: notes.Count == 0 ? null : string.Join("; ", notes));
                return;
            }
            // Every language shows the same text: the same value, written another way
            if (xa.Each == null && xb.Each == null) return; // one text on both sides; only the JSON's form differs
            int n = Math.Max(xa.Each?.Count ?? 0, xb.Each?.Count ?? 0);
            Leaf(def, name, path, shown, plain, sub, Change.Changed, a, b, true, true, cls: DiffClass.WrittenAnotherWay,
                textA: Shown(xa, ta, false), textB: Shown(xb, tb, true),
                note: $"every one of the {n} language{(n == 1 ? "" : "s")} shows the same text on both sides" + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "; the same strings in another order"));
        }

        // ------------------------------------------------------------------ Lists
        void List(FieldDef? def, SchemaProp? prop, string name, JsonArray a, JsonArray b, string path, string shown, string plain, string? sub, Place ctx, Place childCtx)
        {
            if (a.Count == 0 && b.Count == 0) return;
            // Flags: an enum whose JSON is a list of member names. A set, and its order is nothing.
            if (def is { Kind: "Enum" } && a.All(x => x is JsonValue) && b.All(x => x is JsonValue))
            {
                var sa = a.Select(x => PlainText(x!)).ToHashSet(StringComparer.Ordinal);
                var sb = b.Select(x => PlainText(x!)).ToHashSet(StringComparer.Ordinal);
                if (sa.SetEquals(sb)) return;
                var gone = sa.Where(x => !sb.Contains(x)).ToList(); var came = sb.Where(x => !sa.Contains(x)).ToList();
                Leaf(def, name, path, shown, plain, sub, Change.Changed, a, b, true, true, textA: "[" + string.Join(", ", sa) + "]", textB: "[" + string.Join(", ", sb) + "]",
                    note: string.Join("; ", new[] { came.Count > 0 ? "set: " + string.Join(", ", came) : null, gone.Count > 0 ? "cleared: " + string.Join(", ", gone) : null }.Where(x => x != null)));
                return;
            }
            var itemDef = prop?.Item;
            bool allValues = a.All(x => x is JsonValue) && b.All(x => x is JsonValue);
            bool allTexts = allValues && a.Concat(b).All(x => x!.GetValueKind() == JsonValueKind.String);
            if (allTexts && (itemDef is { Kind: "FormLink" } || a.Concat(b).All(x => IsLink(x!.GetValue<string>()))))
            {
                LinkSet(def, name, a, b, path, shown, plain, sub);
                return;
            }
            if (allValues) { Values(def, name, a, b, path, shown, plain, sub); return; }

            var itemProp = itemDef == null ? null : new SchemaProp { Name = name, Main = itemDef };
            string itemPlain = itemDef == null || def == null ? Plain(plain + "[*]", "") : PlainOf(plain, def, itemDef);
            var itemCtx = itemDef == null ? childCtx : ctx;
            // A grid is a list of rows: each row is the same field again
            if (def is { Kind: "Array2D" } && a.Concat(b).All(x => x is JsonArray))
            {
                for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
                    Field(def, prop, $"[{i}]", i < a.Count, i < a.Count ? a[i] : null, i < b.Count, i < b.Count ? b[i] : null, $"{path}[{i}]", $"{shown}[{i}]", plain, ctx);
                return;
            }
            Objects(def, itemDef, itemProp, name, a, b, path, shown, plain, itemPlain, sub, itemCtx);
        }

        /// <summary>A list of links is a set. Each link only one side has is a difference; the same links in another
        /// order is one difference of its own kind.</summary>
        void LinkSet(FieldDef? def, string name, JsonArray a, JsonArray b, string path, string shown, string plain, string? sub)
        {
            var ka = a.Select(x => Canon(true, x!.GetValue<string>())).ToList();
            var kb = b.Select(x => Canon(false, x!.GetValue<string>())).ToList();
            var count = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var k in ka) count[k] = count.GetValueOrDefault(k) + 1;
            foreach (var k in kb) count[k] = count.GetValueOrDefault(k) - 1;
            int before = _r.All.Count;
            var done = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < ka.Count; i++)
                if (count[ka[i]] > 0 && done.Add(ka[i]))
                    Leaf(def, name, path, shown, plain, sub, Change.OnlyA, a[i], null, true, false, element: ka[i], note: count[ka[i]] > 1 ? $"{count[ka[i]]} times more often" : null);
            for (int i = 0; i < kb.Count; i++)
                if (count[kb[i]] < 0 && done.Add(kb[i]))
                    Leaf(def, name, path, shown, plain, sub, Change.OnlyB, null, b[i], false, true, element: kb[i], note: count[kb[i]] < -1 ? $"{-count[kb[i]]} times more often" : null);
            if (_r.All.Count == before && !ka.SequenceEqual(kb))
            {
                int at = 0; while (at < ka.Count && ka[at] == kb[at]) at++;
                Leaf(def, name, path, shown, plain, sub, Change.Order, a, b, true, true, textA: $"{ka.Count} links", textB: $"the same {kb.Count} links in another order",
                    note: $"the order first differs at [{at}]: {_o.Label(a[at]!.GetValue<string>())} and {_o.Label(b[at]!.GetValue<string>())}");
            }
            if (_r.All.Count > before) _r.Lists.Add(new ListMatch(path, "links as a set"));
        }

        /// <summary>A list of plain values (numbers, texts): by position, as one difference that says where.</summary>
        void Values(FieldDef? def, string name, JsonArray a, JsonArray b, string path, string shown, string plain, string? sub)
        {
            var differ = new List<int>();
            int common = Math.Min(a.Count, b.Count);
            for (int i = 0; i < common; i++) if (!ValueEqual(a[i], b[i])) differ.Add(i);
            if (differ.Count == 0 && a.Count == b.Count) return;
            var parts = new List<string>();
            if (a.Count != b.Count) parts.Add($"{a.Count} and {b.Count} values");
            if (differ.Count > 0)
                parts.Add($"{differ.Count} of {common} differ: " + string.Join("; ", differ.Take(6).Select(i => $"[{i}] {Text(a[i], null, 1)} and {Text(b[i], null, 1)}")) + (differ.Count > 6 ? "; …" : ""));
            Leaf(def, name, path, shown, plain, sub, Change.Changed, a, b, true, true, note: string.Join("; ", parts));
            _r.Lists.Add(new ListMatch(path, "by position"));
        }

        /// <summary>A list of objects. Entries that are wholly equal are paired first, wherever they stand. What is
        /// left is paired by the key field when the schema makes one evident, else in order. Paired entries that
        /// differ are compared field by field; unpaired ones are only on one side.</summary>
        void Objects(FieldDef? def, FieldDef? itemDef, SchemaProp? itemProp, string name, JsonArray a, JsonArray b, string path, string shown, string plain, string itemPlain, string? sub, Place itemCtx)
        {
            bool dictionary = def is { Kind: "Dictionary" } && a.Concat(b).All(x => x is JsonObject o && o.Count == 2 && o.ContainsKey("Key") && o.ContainsKey("Value"));
            var ca = a.Select(x => Canonical(x, true)).ToList();
            var cb = b.Select(x => Canonical(x, false)).ToList();
            var pairOfA = Enumerable.Repeat(-1, a.Count).ToArray(); var pairOfB = Enumerable.Repeat(-1, b.Count).ToArray();
            var equal = new bool[a.Count];

            // 1. wholly equal entries
            var free = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
            for (int j = 0; j < cb.Count; j++) (free.TryGetValue(cb[j], out var q) ? q : free[cb[j]] = new Queue<int>()).Enqueue(j);
            for (int i = 0; i < ca.Count; i++)
                if (free.TryGetValue(ca[i], out var q) && q.Count > 0) { int j = q.Dequeue(); pairOfA[i] = j; pairOfB[j] = i; equal[i] = true; }
            if (pairOfA.All(p => p >= 0) && pairOfB.All(p => p >= 0))
            {
                // The same entries. In another order?
                if (!IsRising(pairOfA))
                {
                    int at = 0; while (at < pairOfA.Length && pairOfA[at] == at) at++;
                    Leaf(def, name, path, shown, plain, sub, Change.Order, a, b, true, true, textA: $"{a.Count} entries", textB: $"the same {b.Count} entries in another order", note: $"the order first differs at [{at}]");
                    _r.Lists.Add(new ListMatch(path, "whole entries"));
                }
                return;
            }

            // 2. the rest: by key, or in order
            ListKey? key = dictionary ? new ListKey("Key", "Key") : _keys.For(itemDef?.RefObject);
            var restA = Enumerable.Range(0, a.Count).Where(i => pairOfA[i] < 0).ToList();
            var restB = Enumerable.Range(0, b.Count).Where(j => pairOfB[j] < 0).ToList();
            var keyOfA = new Dictionary<int, string>(); var keyOfB = new Dictionary<int, string>();
            string how;
            if (key != null && key.Field == RecordSplit.TypeKey)
            {
                // The kind of object is a key only when no kind is there twice
                var kindsA = a.Select(x => x is JsonObject o ? Variant(o) : null).ToList(); var kindsB = b.Select(x => x is JsonObject o ? Variant(o) : null).ToList();
                if (kindsA.Any(k => k == null) || kindsB.Any(k => k == null) || kindsA.Distinct().Count() != kindsA.Count || kindsB.Distinct().Count() != kindsB.Count) key = null;
            }
            if (key != null)
            {
                string KeyText(JsonNode? item, bool sideA)
                {
                    // An entry that leaves its key field out has the key's default there
                    var v = item is JsonObject o && o.TryGetPropertyValue(key.Field, out var kv) ? kv : key.Def is { Nullable: false } ? FieldLookup.DefaultOf(_s, key.Def).Json : null;
                    return v is JsonValue jv && jv.TryGetValue<string>(out var s) ? (RecordSplit.IsFormKey(s) ? Canon(sideA, s) : s) : Canonical(v, sideA);
                }
                var seenA = new Dictionary<string, int>(StringComparer.Ordinal); var seenB = new Dictionary<string, int>(StringComparer.Ordinal);
                // The nth entry with a key on one side pairs with the nth with that key on the other, counted among all entries
                for (int i = 0; i < a.Count; i++) { string k = KeyText(a[i], true); int n = seenA[k] = seenA.GetValueOrDefault(k) + 1; keyOfA[i] = n == 1 ? k : $"{k}#{n}"; }
                for (int j = 0; j < b.Count; j++) { string k = KeyText(b[j], false); int n = seenB[k] = seenB.GetValueOrDefault(k) + 1; keyOfB[j] = n == 1 ? k : $"{k}#{n}"; }
                var waiting = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
                foreach (int j in restB) { string k = BaseKey(keyOfB[j]); (waiting.TryGetValue(k, out var q) ? q : waiting[k] = new Queue<int>()).Enqueue(j); }
                foreach (int i in restA)
                    if (waiting.TryGetValue(BaseKey(keyOfA[i]), out var q) && q.Count > 0) { int j = q.Dequeue(); pairOfA[i] = j; pairOfB[j] = i; }
                how = "by " + key.Shown;
            }
            else
            {
                for (int n = 0; n < Math.Min(restA.Count, restB.Count); n++) { pairOfA[restA[n]] = restB[n]; pairOfB[restB[n]] = restA[n]; }
                how = "by position";
            }

            int before = _r.All.Count;
            string At(int i, int j) => key != null ? $"[{key.Shown}={(i >= 0 ? keyOfA[i] : keyOfB[j])}]" : i >= 0 ? $"[{i}]" : $"[+{j}]";
            string AtShown(int i, int j)
            {
                if (key == null) return i >= 0 && j >= 0 && i != j ? $"[{i}→{j}]" : i >= 0 ? $"[{i}]" : $"[{j}]";
                var item = (i >= 0 ? a[i] : b[j]) as JsonObject;
                var v = item != null && item.TryGetPropertyValue(key.Field, out var kv) ? kv : key.Def is { Nullable: false } ? FieldLookup.DefaultOf(_s, key.Def).Json : null;
                string text = v is JsonValue jv && jv.TryGetValue<string>(out var s) ? (RecordSplit.IsFormKey(s) ? _o.Label(s) : key.Field == RecordSplit.TypeKey ? s : Text(v, null, 1)) : Text(v, null, 1);
                string full = i >= 0 ? keyOfA[i] : keyOfB[j];
                int hash = full.LastIndexOf('#');
                return $"[{text}{(hash > 0 && int.TryParse(full[(hash + 1)..], out _) ? " " + full[hash..] : "")}]";
            }
            for (int i = 0; i < a.Count; i++)
            {
                int j = pairOfA[i];
                if (j < 0) { Leaf(itemDef, name, path + At(i, -1), shown + AtShown(i, -1), itemPlain, sub, Change.OnlyA, a[i], null, true, false); continue; }
                if (equal[i]) continue;
                if (dictionary)
                {
                    var pa = (JsonObject)a[i]!; var pb = (JsonObject)b[j]!;
                    Field(itemDef, itemProp, "Value", true, pa["Value"], true, pb["Value"], path + At(i, j), shown + AtShown(i, j), itemPlain, itemCtx);
                }
                else Field(itemDef, itemProp, At(i, j), true, a[i], true, b[j], path + At(i, j), shown + AtShown(i, j), itemPlain, itemCtx);
            }
            for (int j = 0; j < b.Count; j++)
                if (pairOfB[j] < 0) Leaf(itemDef, name, path + At(-1, j), shown + AtShown(-1, j), itemPlain, sub, Change.OnlyB, null, b[j], false, true);
            // The entries both sides have, in another order
            var paired = pairOfA.Where(p => p >= 0).ToArray();
            if (!IsRising(paired))
                Leaf(def, name, path, shown, plain, sub, Change.Order, a, b, true, true, textA: $"{a.Count} entries", textB: $"{b.Count} entries, those of both in another order");
            if (_r.All.Count > before) _r.Lists.Add(new ListMatch(path, how));
        }

        static string BaseKey(string k)
        {
            int hash = k.LastIndexOf('#');
            return hash > 0 && int.TryParse(k.AsSpan(hash + 1), out _) ? k[..hash] : k;
        }

        static bool IsRising(int[] order)
        {
            for (int i = 1; i < order.Length; i++) if (order[i] < order[i - 1]) return false;
            return true;
        }
    }
}
