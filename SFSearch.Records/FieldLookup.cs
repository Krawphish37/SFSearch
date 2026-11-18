// "Fields" mode and evidence.
//  - Fields: a named list of fields of one record. A field the JSON leaves out is answered with its default wherever
//    the schema says the field is always there; a field that may be absent is answered "not set".
//  - Evidence: for one field, the raw subrecord bytes from the file, the slice the field occupies when the schema
//    has its offset and size, the Mutagen field, and the xEdit path.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using SFSearch.SchemaGen;

namespace SFSearch.Records;

/// <summary>One answer of "fields" mode.</summary>
public sealed class FieldValue
{
    /// <summary>The name as it was asked.</summary>
    public required string Asked { get; init; }
    /// <summary>The Mutagen path of the value: <c>ObjectTemplates[0].Includes[2].Mod</c>.</summary>
    public required string Path { get; init; }
    public string? Kind { get; init; }
    public string? Signature { get; init; }
    public string? Readable { get; init; }
    /// <summary>The value as JSON: from the record, or the default.</summary>
    public JsonNode? Json { get; init; }
    public string? Text { get; init; }
    /// <summary>True when the record's JSON holds the value.</summary>
    public bool IsSet { get; init; }
    /// <summary>True when the JSON leaves the value out and the schema says the field is always there: this is its default.</summary>
    public bool IsDefault { get; init; }
    public string? Note { get; init; }
    /// <summary>The card's field, when the value is set (a whole object or list has its parts here).</summary>
    public CardField? Field { get; init; }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.Append(Path).Append(" = ");
        if (Field is { Children: not null })
        {
            var card = new Card { Label = "", FormKey = "", Signature = "", Type = "", Plugin = "", Fields = Field.Children };
            sb.Append(Field.Variant ?? (Field.Kind is "List" or "Array" or "Array2D" or "Dictionary" ? $"{Field.Children.Count.ToString("N0", CultureInfo.InvariantCulture)} items" : "")).Append('\n');
            // the lines of the fields below, without the card's two heading lines
            var lines = card.ToText().Split('\n');
            foreach (var l in lines.Skip(2)) if (l.Length > 0) sb.Append("  ").Append(l).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }
        sb.Append(Text ?? "(not set)");
        // (the card's own note on the field too: which strings table a translated text is from, GAPS S190)
        if ((Note ?? Field?.Note) is { } note) sb.Append("   [").Append(note).Append(']');
        return sb.ToString();
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["asked"] = Asked, ["path"] = Path, ["set"] = IsSet, ["default"] = IsDefault, ["value"] = Json?.DeepClone(), ["text"] = Text };
        if (Kind != null) o["kind"] = Kind;
        if (Signature != null) o["signature"] = Signature;
        if (Readable != null) o["readable"] = Readable;
        if ((Note ?? Field?.Note) is { } note) o["note"] = note;
        return o;
    }
}

/// <summary>Where one occurrence of a field's subrecord sits in the record, and the field's slice of it.</summary>
public sealed record EvidenceSlice(int Occurrence, int SubrecordLength, string SubrecordBytes, string? FieldBytes, string? Reads);

public sealed class Evidence
{
    public required string Record { get; init; }
    /// <summary>The Mutagen field: <c>Weapon.AttackDamage</c>.</summary>
    public required string Field { get; init; }
    public required string Kind { get; init; }
    public string? Value { get; init; }
    public bool IsDefault { get; init; }
    public required string RecordSignature { get; init; }
    public string? Subrecord { get; init; }
    public int? Offset { get; init; }
    public int? Size { get; init; }
    public string? XEdit { get; init; }
    public string? Readable { get; init; }
    public bool Unknown { get; init; }
    public string? UnknownEvidence { get; init; }
    public bool Compressed { get; set; }
    public List<EvidenceSlice> Slices { get; } = new();
    public int Occurrences { get; set; }
    /// <summary>How many of the record's <see cref="Occurrences"/> subrecords of this signature can hold the field: for
    /// a field of the record's own, those outside components; for a component's field, those inside its own component
    /// (GAPS S102). Null: every one.</summary>
    public int? Candidates { get; set; }
    /// <summary>For a field inside a list item: where the item and the field were found, or why they couldn't be.</summary>
    public ItemPlace? Item { get; init; }
    public List<string> Notes { get; } = new();
    /// <summary>The field is in the record's 24-byte header, not in a subrecord (its flags, FormID, versions: GAPS S88).</summary>
    public bool InHeader { get; init; }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.Append(Field[(Field.IndexOf('.') + 1)..]).Append(" = ").Append(Value ?? "(not set)").Append(IsDefault ? "   [the default: the record doesn't set it]" : "").Append('\n');
        string address = InHeader ? $"{RecordSignature} record header" : Subrecord == null ? $"{RecordSignature}: the schema doesn't know which subrecord holds it" : $"{RecordSignature}/{Subrecord}";
        if (Offset != null) address += $", offset {Offset}";
        if (Size != null) address += $", {Size} byte{(Size == 1 ? "" : "s")}";
        if (Slices.Count == 0) sb.Append("  file     ").Append(address).Append(Subrecord == null ? "" : Occurrences > 0 && Candidates == 0 ? $": none of the record's {Subrecord} subrecords is this field's" : $": the record has no {Subrecord} subrecord").Append('\n');
        foreach (var s in Slices)
        {
            string which = Occurrences > 1 ? $" (the {Ordinal(s.Occurrence + 1)} of {Occurrences.ToString("N0", CultureInfo.InvariantCulture)} {Subrecord} subrecords)" : "";
            sb.Append("  file     ").Append(address).Append(which);
            if (s.FieldBytes != null) sb.Append(": ").Append(s.FieldBytes);
            if (s.Reads != null) sb.Append("   → ").Append(s.Reads);
            sb.Append('\n');
            sb.Append("           ").Append(InHeader ? "the header" : Subrecord).Append(" is ").Append(s.SubrecordLength.ToString("N0", CultureInfo.InvariantCulture)).Append(" bytes: ").Append(s.SubrecordBytes).Append('\n');
        }
        int candidates = Candidates ?? Occurrences;
        if (candidates > Slices.Count && Slices.Count > 0 && Item is not { Located: true }) sb.Append($"           … {candidates - Slices.Count} more {Subrecord} subrecords\n");
        if (Item is { Located: true }) sb.Append(Field.Contains('[') ? "  item     " : "  found    ").Append(Item.Where).Append(". ").Append(char.ToUpperInvariant(Item.Checked![0])).Append(Item.Checked[1..]).Append(".\n");
        sb.Append("  Mutagen  ").Append(Field).Append(" (").Append(Kind).Append(')').Append(Unknown ? "   [Mutagen still calls this field unknown]" : "").Append('\n');
        sb.Append("  xEdit    ").Append(XEdit ?? "(no checked xEdit name for this field)").Append('\n');
        if (UnknownEvidence != null) sb.Append("  known    ").Append(UnknownEvidence).Append('\n');
        if (Compressed) sb.Append("  [notes] The record is compressed in the file; the bytes are shown inflated.\n");
        foreach (var n in Notes) sb.Append("  [notes] ").Append(n).Append('\n');
        return sb.ToString();
    }

    static string Ordinal(int n)
    {
        string t = n.ToString("N0", CultureInfo.InvariantCulture);
        return (n % 100) is 11 or 12 or 13 ? t + "th" : (n % 10) switch { 1 => t + "st", 2 => t + "nd", 3 => t + "rd", _ => t + "th" };
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["record"] = Record, ["field"] = Field, ["kind"] = Kind, ["value"] = Value, ["default"] = IsDefault,
            ["file"] = new JsonObject { ["record"] = RecordSignature, ["subrecord"] = Subrecord, ["offset"] = Offset, ["size"] = Size, ["occurrences"] = Occurrences },
            ["xedit"] = XEdit, ["readable"] = Readable, ["unknown"] = Unknown, ["compressed"] = Compressed,
        };
        o["slices"] = new JsonArray(Slices.Select(s => (JsonNode)new JsonObject { ["occurrence"] = s.Occurrence, ["subrecordLength"] = s.SubrecordLength, ["subrecordBytes"] = s.SubrecordBytes, ["fieldBytes"] = s.FieldBytes, ["reads"] = s.Reads }).ToArray());
        if (InHeader) ((JsonObject)o["file"]!)["header"] = true;
        if (Candidates is int can && can != Occurrences) ((JsonObject)o["file"]!)["candidates"] = can;
        if (UnknownEvidence != null) o["evidence"] = UnknownEvidence;
        if (Item != null) o["item"] = new JsonObject { ["located"] = Item.Located, ["where"] = Item.Where, ["checked"] = Item.Checked, ["reason"] = Item.Reason };
        if (Notes.Count > 0) o["notes"] = new JsonArray(Notes.Select(n => (JsonNode)n).ToArray());
        return o;
    }
}

public static class FieldLookup
{
    readonly record struct Segment(string Name, int? Index, bool All);

    static List<Segment> Parse(string path)
    {
        var list = new List<Segment>();
        foreach (var raw in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.Trim();
            // A variant marker ({WeaponModification}) may be written; every kind is tried anyway
            int brace = part.IndexOf('{');
            if (brace >= 0) part = part[..brace];
            int open = part.IndexOf('[');
            if (open < 0) { list.Add(new Segment(part, null, false)); continue; }
            string inside = part[(open + 1)..].TrimEnd(']');
            string name = part[..open];
            if (inside is "*" or "") list.Add(new Segment(name, null, true));
            else if (int.TryParse(inside, NumberStyles.None, CultureInfo.InvariantCulture, out int i)) list.Add(new Segment(name, i, false));
            else throw new RecordException($"\"{raw}\" isn't a field name: an index is a number or *, as in Keywords[0] or Keywords[*].");
        }
        if (list.Count == 0) throw new RecordException("No field was named.");
        return list;
    }

    /// <summary>The named fields of a record, from its card and the schema.</summary>
    public static List<FieldValue> Fields(RecordSchema schema, Card card, IEnumerable<string> names)
    {
        var results = new List<FieldValue>();
        foreach (var asked in names)
        {
            var segs = Parse(asked);
            int before = results.Count;
            var other = new OtherKinds();
            Walk(schema, card, asked, card.Fields, card.Type, segs, 0, "", results, other);
            if (results.Count == before)
                results.Add(new FieldValue { Asked = asked, Path = asked, Note = other.Items > 0
                    ? $"none of the {other.Items} item(s) of that list has a field \"{other.Field}\": they are of other kinds ({string.Join(", ", other.Kinds.Take(6))})"
                    : "no item of that list is set" });
        }
        return results;
    }

    /// <summary>Items of a list asked for with [*] that were passed over: their kind of object has no such field.</summary>
    sealed class OtherKinds
    {
        public int Items;
        public string Field = "";
        public SortedSet<string> Kinds = new(StringComparer.Ordinal);
    }

    /// <param name="inAll">The walk is below a [*]: a list's items can be of several kinds (a record's Components), and
    /// an item whose kind has no such field is passed over. Outside a [*] a field that doesn't exist is an error.</param>
    static void Walk(RecordSchema schema, Card card, string asked, List<CardField> level, string? obj, List<Segment> segs, int i, string pathSoFar, List<FieldValue> results, OtherKinds other, bool inAll = false)
    {
        var seg = segs[i];
        bool last = i == segs.Count - 1;
        var cf = level.FirstOrDefault(f => f.Name == seg.Name) ?? level.FirstOrDefault(f => f.Name.Equals(seg.Name, StringComparison.OrdinalIgnoreCase))
            ?? level.FirstOrDefault(f => f.Readable.Equals(seg.Name, StringComparison.OrdinalIgnoreCase));
        SchemaProp? prop = obj == null ? null : schema.Prop(obj, cf?.Name ?? seg.Name)
            ?? schema.Props(obj).FirstOrDefault(p => p.Name.Equals(seg.Name, StringComparison.OrdinalIgnoreCase))
            ?? schema.Props(obj).FirstOrDefault(p => string.Equals(p.Main.Readable, seg.Name, StringComparison.OrdinalIgnoreCase));
        string name = cf?.Name ?? prop?.Name ?? seg.Name;
        string path = pathSoFar.Length == 0 ? name : pathSoFar + "." + name;
        if (cf == null && prop == null && inAll && obj != null)
        {
            other.Items++; other.Field = seg.Name; other.Kinds.Add(obj);
            return;
        }
        if (cf == null && prop == null)
            throw new RecordException($"{card.Type}{(pathSoFar.Length > 0 ? "." + pathSoFar + (obj != null && obj != card.Type ? $" ({obj})" : "") : "")} has no field \"{seg.Name}\".{Nearest(schema, obj, level, seg.Name)}");

        if (cf == null)
        {
            // The JSON leaves it out
            var def = prop!.Main;
            if (seg.Index != null || seg.All)
            {
                if (def.Nullable) results.Add(NotSet(asked, path, def, card));
                return; // an empty list has no items
            }
            if (last) { results.Add(def.Nullable ? NotSet(asked, path, def, card) : Default(schema, asked, path, def, card)); return; }
            if (def.Nullable) { results.Add(NotSet(asked, path, def, card, $"{string.Join('.', segs.Skip(i + 1).Select(s => s.Name))} is below it")); return; }
            // An object that is always there, with every field at its default: go on below it
            Walk(schema, card, asked, new List<CardField>(), def.RefObject, segs, i + 1, path, results, other, inAll);
            return;
        }

        if (seg.Index != null || seg.All)
        {
            if (cf.Children == null || !(cf.Kind is "List" or "Array" or "Array2D" or "Dictionary" || cf.Children.All(c => c.Name.StartsWith('['))))
                throw new RecordException($"{path} isn't a list, so it has no [{(seg.All ? "*" : seg.Index.ToString())}].");
            // An item by its name, [5]: a page of a heavy record's list (HeavyRecord.cs) holds items whose index in
            // the whole list isn't their place on the page. Everywhere else the name is the place.
            var picked = seg.All ? cf.Children : cf.Children.Where((c, n) => c.Name.StartsWith('[') && c.Kind != "Entry" ? c.Name == $"[{seg.Index}]" : n == seg.Index).ToList();
            if (!seg.All && picked.Count == 0)
                throw new RecordException(card.Pages?.GetValueOrDefault(cf.Name) is { } page && cf.Up == null
                    ? $"{path} has {page.Total} item(s); there is no [{seg.Index}]."
                    : $"{path} has {cf.Children.Count} item(s); there is no [{seg.Index}].");
            foreach (var item in picked)
            {
                if (last) results.Add(Set(asked, item));
                else Walk(schema, card, asked, item.Children ?? new List<CardField>(), item.Object ?? prop?.Item?.RefObject, segs, i + 1, item.Path, results, other, inAll || seg.All);
            }
            return;
        }
        if (last) { results.Add(Set(asked, cf)); return; }
        if (cf.Children == null) throw new RecordException($"{path} is a plain value ({cf.Kind}); it has no field \"{segs[i + 1].Name}\".");
        Walk(schema, card, asked, cf.Children, cf.Object, segs, i + 1, cf.Path, results, other, inAll);
    }

    static FieldValue Set(string asked, CardField f) => new()
    {
        Asked = asked, Path = f.Path, Kind = f.Kind, Signature = f.Signature, Readable = f.Readable, Json = f.Json, Text = f.Text, IsSet = true, Field = f,
        Note = f.Unknown ? "unknown field" + (f.Bytes != null ? "; bytes " + f.Bytes : "") : null,
    };

    static FieldValue NotSet(string asked, string path, FieldDef def, Card card, string? more = null) => new()
    {
        Asked = asked, Path = path, Kind = def.Kind, Signature = def.Subrecord, Readable = def.Readable,
        Note = "not set: the field may be absent, and this record doesn't have it" + (more == null ? "" : "; " + more),
    };

    static FieldValue Default(RecordSchema schema, string asked, string path, FieldDef def, Card card)
    {
        // A property Mutagen adds beyond its XML (GAPS Part 2 row 8) is read from Mutagen's record: the serializer
        // leaves such a property out at its default, and RegionData's DataType (read-only) always
        if (def.Source != null && card.FromRecord?.Invoke(path) is { } got)
        {
            var (rj, rt) = FromObject(got);
            return new FieldValue
            {
                Asked = asked, Path = path, Kind = def.Kind, Signature = def.Subrecord, Readable = def.Readable, Json = rj, Text = rt, IsSet = true,
                Note = "read from Mutagen's record: a property Mutagen adds beyond its XML, which the record's JSON doesn't carry",
            };
        }
        var (json, text) = DefaultOf(schema, def);
        return new FieldValue
        {
            Asked = asked, Path = path, Kind = def.Kind, Signature = def.Subrecord, Readable = def.Readable, Json = json, Text = text, IsDefault = json != null,
            Note = json != null ? "the default: the record's JSON leaves it out, and the schema says the field is always there"
                : $"left out of the record's JSON, so it is at its default; the schema has no default written down for a {def.Kind}",
        };
    }

    /// <summary>A plain value read from a Mutagen object (an enum, a bool, a number) as the JSON writes it: an enum by its
    /// member names (flags as a list), a bool and a number as themselves.</summary>
    static (JsonNode? Json, string Text) FromObject(object v)
    {
        switch (v)
        {
            case bool b: return (JsonValue.Create(b), b ? "true" : "false");
            // A Half as the serializer writes it: the double that holds it
            case Half h when !Half.IsNaN(h) && !Half.IsInfinity(h): return (JsonValue.Create((double)h), ((double)h).ToString("R", CultureInfo.InvariantCulture));
            case Enum e when e.GetType().IsDefined(typeof(FlagsAttribute), false):
            {
                var t = e.GetType();
                // The bits as an unsigned number of the enum's width (FlagBits.Raw): an int's bit 31 is no overflow (GAPS S162)
                ulong bits = FlagBits.Raw(e), named = 0;
                var parts = new List<string>();
                foreach (var m in Enum.GetValues(t).Cast<Enum>())
                {
                    ulong mv = FlagBits.Raw(m);
                    if (mv != 0 && (bits & mv) == mv && (named & mv) != mv) { parts.Add(m.ToString()); named |= mv; }
                }
                if ((bits & ~named) != 0) parts.Add("0x" + (bits & ~named).ToString("X", CultureInfo.InvariantCulture));
                var arr = new JsonArray(parts.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
                return (arr, "[" + string.Join(", ", parts) + "]");
            }
            case Enum e: return (JsonValue.Create(e.ToString()), e.ToString());
            default:
                string s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
                return (JsonValue.Create(s), s);
        }
    }

    /// <summary>True when every field of the object is a number (or a point of numbers) whose default is zero: ObjectBounds.</summary>
    static bool AllZero(RecordSchema schema, string? obj)
    {
        if (obj == null) return false;
        var props = schema.Props(obj);
        return props.Count > 0 && props.All(p => p.Main.Kind is "Int8" or "Int16" or "Int32" or "Int64" or "UInt8" or "UInt16" or "UInt32" or "UInt64" or "Float" or "Double" or "Half"
            or "P2Float" or "P2Int16" or "P2Int32" or "P3Float" or "P3Int16" or "P3Int32" && (p.Main.Default == null || p.Main.Default is "0" or "0f" or "0.0"));
    }

    /// <summary>A half-precision float (Mutagen's <c>Half</c>) as the serializer writes it: read back by the serializer's
    /// own rule (JsonScalarText.TryParseHalf, 1.38.6-fixes.5, C59 W14): a number, or a text ("Infinity", "NaN", "NaN:" and
    /// its 4 hex digits).</summary>
    public static bool TryReadHalf(System.Text.Json.JsonElement el, out Half h)
    {
        h = default;
        string? text = el.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number => el.GetRawText(),
            System.Text.Json.JsonValueKind.String => el.GetString(),
            _ => null,
        };
        return text != null && Mutagen.Bethesda.Serialization.Newtonsoft.JsonScalarText.TryParseHalf(text, out h);
    }

    /// <summary>How the serializer writes a byte field with no bytes (Mutagen.Bethesda.Serialization 1.38.6-fixes.2 on).</summary>
    public const string NoBytes = "0x";

    /// <summary>True for a byte field's value that holds no bytes: "0x", or "[]" as the serializer wrote it before 1.38.6-fixes.2.</summary>
    public static bool IsNoBytes(string? s) => s is NoBytes or "[]";

    /// <summary>
    /// The value a field has when the JSON leaves it out and the schema says the field is always there. A value the
    /// serializer compares with a default is left out at the default its registry lists (FieldDefaults, 1.38.6-fixes.5,
    /// C59 S124: default(T), or the class's own {Name}Default such as BookTeachesNothing.RawContent's uint.MaxValue), given
    /// here as the serializer writes it. A list, an object and a translated text are left out by the serializer's rules
    /// (empty, every field at its default, no text), not at a value.
    /// </summary>
    public static (JsonNode? Json, string? Text) DefaultOf(RecordSchema schema, FieldDef def)
    {
        static (JsonNode?, string?) Raw(string json, string? text = null) => (JsonNode.Parse(json), text ?? json);
        static (JsonNode?, string?) Str(string s) => (JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(s)), s);
        if (ListedDefault(schema, def) is { } listed) return listed;
        string? d = def.Default;
        // A record's form version has no default in Mutagen's definitions. The serializer before 1.38.6-fixes.2 left it
        // out when it was the form version Mutagen gives a new record of the game (576 for Starfield), not when it was 0;
        // fixes.2 always writes it, so this only answers for JSON written before. (Found in M5: asked for FormVersion,
        // fields mode answered 0 for a record whose header says 576.)
        if (def.Name == "FormVersion" && def.Object == "StarfieldMajorRecord" && d == null)
            return Raw((Mutagen.Bethesda.Plugins.Meta.GameConstants.Get(Mutagen.Bethesda.GameRelease.Starfield).DefaultFormVersion ?? 0).ToString(CultureInfo.InvariantCulture));
        switch (def.Kind)
        {
            case "Enum":
            {
                var members = schema.Enum(def.Enum);
                bool flags = members != null && members.Any(m => m.IsFlags);
                if (d != null) { string name = d[(d.LastIndexOf('.') + 1)..]; return flags ? Raw($"[\"{name}\"]", $"[{name}]") : Str(name); }
                if (flags) return Raw("[]");
                var zero = members?.FirstOrDefault(m => m.Value == 0);
                return zero != null ? Str(zero.Name) : members == null ? (null, null) : Str("0");
            }
            case "Bool": return Raw(d != null && d.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : "false");
            case "Int8" or "Int16" or "Int32" or "Int64" or "UInt8" or "UInt16" or "UInt32" or "UInt64":
                // Mutagen's definitions write some defaults in hex (NavmeshGeometry.CrcHash is 0xA5E9A03C). (Found in M5:
                // fields mode answered "no default written down" for it.)
                if (d != null && d.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(d.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hex))
                    return Raw(hex.ToString(CultureInfo.InvariantCulture));
                return d != null && long.TryParse(d, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) || d != null && ulong.TryParse(d, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? Raw(d) : d == null ? Raw("0") : (null, null);
            case "Float" or "Double" or "Percent" or "Half":
            {
                if (d == null) return Raw("0.0");
                string num = d.TrimEnd('f', 'F', 'd', 'D');
                return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? Raw(v.ToString("R", CultureInfo.InvariantCulture) is var s && (s.Contains('.') || s.Contains('E')) ? s : s + ".0") : (null, null);
            }
            case "String": return def.Translated ? Raw("{\"TargetLanguage\":\"English\"}", "") : Str(d ?? "");
            case "FormLink": return Str("Null");
            case "P2Float" or "P2Int16" or "P2Int32": return Str("0, 0");
            case "P3Float" or "P3Int16" or "P3Int32": return Str("0, 0, 0");
            case "ByteArray": return def.Size is int n && n > 0 ? (JsonNode.Parse("\"0x" + new string('0', n * 2) + "\""), string.Join(' ', Enumerable.Repeat("00", n))) : Raw("\"" + NoBytes + "\"", "(no bytes)");
            case "List" or "Array" or "Array2D" or "Dictionary": return Raw("[]", "0 items");
            case "Object": return Raw("{}", AllZero(schema, def.RefObject) ? "all zero" : "(every field of it is at its default)");
            case "Guid": return Str("00000000-0000-0000-0000-000000000000");
            default: return (null, null);
        }
    }

    /// <summary>The default the serializer's registry lists for the field (its class's FieldDefaults; for a field of an
    /// abstract object, the first kind of it the registry has), as the serializer writes that value. Null when the
    /// registry lists none or the value is of a kind written otherwise.</summary>
    static (JsonNode?, string?)? ListedDefault(RecordSchema schema, FieldDef def)
    {
        var defaults = ListedDefaults.GetOrAdd((schema.Model, def.Object), k =>
        {
            foreach (string obj in new[] { k.Item2 }.Concat(k.Item1.Variants(k.Item2)))
                if (RecordSerializer.ClassOf(obj) is { } cls && RecordSerializer.Registry.TryGetFieldDefaults(cls, out var d)) return d;
            return null;
        });
        if (defaults == null || def.Path != def.Name || !defaults.TryGetValue(def.Name, out var v)) return null;
        static (JsonNode?, string?) Raw(string json, string? text = null) => (JsonNode.Parse(json), text ?? json);
        static (JsonNode?, string?) Str(string s) => (JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(s)), s);
        switch (v)
        {
            case bool b: return Raw(b ? "true" : "false");
            case Enum e:
            {
                var t = e.GetType();
                if (t.IsDefined(typeof(FlagsAttribute), false))
                {
                    // As the JSON lists a flags value: each bit once, by SFSearch's bounded loop (FlagBits, GAPS S162)
                    var names = FlagBits.Names(e, includeUndefined: true).ToList();
                    return Raw(System.Text.Json.JsonSerializer.Serialize(names), $"[{string.Join(", ", names)}]");
                }
                return Enum.IsDefined(t, e) ? Str(e.ToString()) : Raw(Convert.ToInt64(e, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
            }
            case sbyte or byte or short or ushort or int or uint or long or ulong: return Raw(Convert.ToString(v, CultureInfo.InvariantCulture)!);
            case float f: return Number(Mutagen.Bethesda.Serialization.Newtonsoft.JsonScalarText.Format(f));
            case double x: return Number(Mutagen.Bethesda.Serialization.Newtonsoft.JsonScalarText.Format(x));
            case Half h: return Number(Mutagen.Bethesda.Serialization.Newtonsoft.JsonScalarText.Format(h));
            case string text: return Str(text);
            case Mutagen.Bethesda.Plugins.FormKey key: return Str(key.IsNull ? "Null" : key.ToString());
            default: return null;
        }
        // A float's token as the JSON holds it: a number, or a text with its quotes
        static (JsonNode?, string?) Number(string token) => token.StartsWith('"') ? (JsonNode.Parse(token), token[1..^1]) : Raw(token);
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<(SchemaModel, string), IReadOnlyDictionary<string, object?>?> ListedDefaults = new();

    /// <summary>"Did you mean": the nearest fields of the object at this level, chosen and worded as describe and the
    /// SQL layer do it (<see cref="SFSearch.SchemaGen.NearNames"/>). Fields the record itself shows there that the
    /// schema's object doesn't have (a list item of another kind) are candidates by their name.</summary>
    static string Nearest(RecordSchema schema, string? obj, List<CardField> level, string asked)
    {
        if (obj != null && schema.Model.Objects.ContainsKey(obj))
        {
            var fields = SFSearch.SchemaGen.NearNames.Fields(schema.Db, new[] { obj }, asked);
            if (fields.Count > 0) return SFSearch.SchemaGen.NearNames.DidYouMean(fields);
        }
        var shown = level.Where(f => !f.Name.StartsWith('[')).Select(f => (f.Name, Readable: (string?)f.Readable)).Distinct().ToList();
        var best = SFSearch.SchemaGen.NearNames.Pick(asked, shown, n => new[] { n.Name, n.Readable ?? "" }, always: false)
            .Select(n => n.Readable != null && Norm(n.Readable) != Norm(n.Name) ? $"{n.Name} (xEdit name \"{n.Readable}\")" : n.Name).ToList();
        return SFSearch.SchemaGen.NearNames.DidYouMean(best);
    }

    static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // ------------------------------------------------------------------ Evidence
    /// <summary>
    /// Evidence for one field of a record. The raw reader only slices: a FormID in the slice is read through
    /// <paramref name="label"/>, which goes through the header index.
    /// </summary>
    /// <param name="formIdOf">Gives the FormID the plugin's file writes for a FormKey: a link's slice inside a list
    /// item is checked against Mutagen's value with it.</param>
    /// <param name="textOf">For a localized plugin: the texts a string ID names in its strings files. A translated
    /// text's 4-byte ID is shown with the file and text it names, checked against Mutagen's text.</param>
    /// <param name="positionsOf">Mutagen's value positions of the record's bytes (C60: the plugin's
    /// <c>MajorRecordReader.ReadValuePositions</c>; for the plugin header, C82's <c>ReadModHeaderValuePositions</c>).</param>
    /// <param name="positionPath">For a part read of a heavy record: a value's path in the part's own positions (a paged
    /// list's items are numbered in the whole list on the card, from 0 in the part).</param>
    public static Evidence Evidence(RecordSchema schema, Card card, RawRecord raw, string field, Func<uint, string> label, int maxOccurrences = 4, int maxBytes = 64, Func<string, uint?>? formIdOf = null, StringLookup? textOf = null, Func<RawRecord, Mutagen.Bethesda.Plugins.Records.ValuePositions>? positionsOf = null, Func<string, string>? positionPath = null)
    {
        var values = Fields(schema, card, new[] { field });
        var value = values[0];
        // A value decoded from a reflection blob (to-do F30): its place in the blob is its place in the subrecord
        if (value.Field is { Position: not null } decoded && ReflectionEvidence(card, raw, value, decoded, label, maxBytes) is { } fromBlob) return fromBlob;
        // A reflection's FormLinks (Mutagen's hand-written reader, ReflectionBinaryCreateTranslation.CustomBinaryEndImport:
        // the FormIDs ReflectionData.FindFormIds finds in the blob, in order): the k-th is 4 bytes at its place (GAPS S67)
        if (value.Field is { Json: not null, Up: { Name: "FormLinks" } links } link && link.Name.StartsWith('[') && ReflectionLinkEvidence(card, raw, value, link, links, label, formIdOf, maxBytes) is { } fromLinks) return fromLinks;
        // The schema's row for the field: by Mutagen path first (indexes and variant markers aside)
        string schemaPath = string.Join('.', Parse(value.IsSet || value.IsDefault ? Unindexed(value.Path) : Unindexed(field)).Select(s => s.Name + (s.All ? "[*]" : "")));
        var found = schema.Model.Resolve(card.Type, schemaPath, ignoreCase: true).Found;
        if (found.Count == 0) found = schema.Model.Resolve(card.Type, schemaPath.Replace("[*]", ""), ignoreCase: true).Found;
        // A dictionary entry's Key has no row of its own: the dictionary's row stands for it, its kind from "keyed by …"
        string? keyKind = null;
        if (found.Count == 0 && schemaPath.EndsWith("[*].Key", StringComparison.Ordinal)
            && schema.Model.Resolve(card.Type, schemaPath[..^"[*].Key".Length], ignoreCase: true).Found is { Count: > 0 } dict
            && dict.Select(f => f.Row).FirstOrDefault(r => r.Def.Kind == "Dictionary") is { } dictRow && ItemLocator.KeyDef(dictRow.Def) is { } keyDef)
        {
            found = dict.Where(f => f.Row == dictRow).ToList();
            keyKind = keyDef.Kind;
        }
        FieldRow? row = found.Count > 0 ? Pick(found.Select(f => f.Row).ToList(), value) : null;
        if (row == null)
            throw new RecordException($"The schema has no field \"{field}\" for {card.Type}, so there is no file address for it. Its value: {value.Text ?? "(not set)"}.");
        // A field of the record's own 24-byte header: a plugin header's flags and versions, a record's flags and FormID
        if (row.Subrecord == null && !row.InList && HandLayouts.HeaderPlace(schema.Model, row.Def) is { } inHeader)
            return HeaderEvidence(schema, card, raw, value, row, inHeader, label, formIdOf);

        // A record of the record's child group (a quest's scenes, a cell's placed objects, a topic's responses): Mutagen's
        // hand-written code reads it from the group after the record (the XML's layoutGroup, customBinaryEnd), so it has
        // no bytes in this record. The answer names that record, whose own evidence holds its fields (GAPS S67)
        var rowParts = row.Path.Split('.');
        for (int depth = 1; depth <= rowParts.Length; depth++)
        {
            string prefix = string.Join('.', rowParts.Take(depth));
            if (prefix.EndsWith("[*]", StringComparison.Ordinal)) prefix = prefix[..^3];
            var at0 = schema.Model.Resolve(card.Type, prefix, ignoreCase: true).Found.Select(f => f.Row).FirstOrDefault(x => x.Path == prefix);
            if (at0 == null) continue;
            var r = at0;
            // (customBinaryEnd on a record reads its child group; on a subrecord object, a reflection's links, it reads that subrecord)
            if (r.Def.Kind is not ("List" or "Object") || r.Def.LayoutGroup == null
                && (r.Def.FilledBy != "customBinaryEnd" || schema.Model.Objects.GetValueOrDefault(r.Def.Object)?.ObjType != "Record")) continue;
            var m = Regex.Match(field, $@"^(.*?\b{Regex.Escape(r.Def.Name)}(\[\d+\])?)(\.|$)");
            string at = m.Success ? m.Groups[1].Value : r.Def.Name;
            var child = Fields(schema, card, new[] { at })[0];
            var inGroup = new Evidence
            {
                Record = card.Label, Field = card.Type + "." + (value.IsSet ? value.Path : row.Path), Kind = row.Def.Kind, Value = value.Text,
                IsDefault = value.IsDefault, RecordSignature = card.Signature, Compressed = raw.Compressed,
                XEdit = row.Def.XEdit, Readable = row.Def.Readable,
            };
            string group = r.Def.LayoutGroup ?? "the record's child group";
            inGroup.Notes.Add(child.IsSet && child.Field?.Children == null && child.Text is { Length: > 0 } rec && at.EndsWith(']')
                ? $"{at} is a record of its own, {rec.TrimStart('→', ' ')}, in {card.Label}'s child group ({group}), which Mutagen's hand-written code reads after the record: it has no bytes in this record. Ask for the evidence of that record{(at.Length < field.Length ? $" (its {field[(at.Length + 1)..]})" : "")}."
                : $"{r.Def.Name} holds records of {card.Label}'s child group ({group}), which Mutagen's hand-written code reads after the record: they have no bytes in this record. Name one item ({r.Def.Name}[0]) to get the record it is.");
            return inGroup;
        }

        string plain = row.PlainPath;
        // Where Mutagen's reader read the value: the record's value positions (C60), for every value (a list item's, a
        // translated text's, a gendered half's, one hand-written code places, the plugin header's: C82), each slice read
        // back and held against Mutagen's value
        CardField? leaf = value.IsSet ? value.Field : value.IsDefault ? DefaultLeaf(card, value) : null;
        ItemPlace? place = leaf == null || leaf.Children != null || positionsOf?.Invoke(raw) is not { } positions ? null
            : ItemLocator.FromPositions(schema, row.Def, leaf, raw, positions, formIdOf, label, textOf, positionPath);
        string? subrecord = place is { Located: true } ? raw.Subrecords[place.Subrecord].Signature : row.Subrecord;
        var ev = new Evidence
        {
            Record = card.Label, Field = card.Type + "." + (value.IsSet || place is { Located: true } ? value.Path : row.Path), Kind = keyKind ?? row.Def.Kind,
            Value = value.Field is { Children: not null } ? value.ToText()[(value.Path.Length + 3)..].Split('\n')[0] : value.Text,
            IsDefault = value.IsDefault, RecordSignature = card.Signature,
            // Not located: the schema's offset counts from the item, so it isn't an address in the subrecord
            Subrecord = subrecord, Offset = place is { Located: true } ? place.Offset : place != null ? null : row.Offset, Size = place is { Located: true } ? place.Size : row.Def.Size,
            Item = place,
            XEdit = schema.XEditPath(card.Signature, plain) ?? (row.Def.XEdit is { Length: > 0 } x ? (row.Subrecord != null ? $"(in {row.Subrecord}) " : "") + x : null),
            Readable = schema.ReadablePair(card.Signature, plain) ?? row.Def.Readable,
            Unknown = row.Def.Unknown, UnknownEvidence = row.Def.Unknown ? schema.Evidence(card.Signature, row.Def.Object, row.Def.Name) : null,
            Compressed = raw.Compressed,
            Occurrences = subrecord == null ? 0 : raw.Subrecords.Count(s => s.Signature == subrecord),
        };
        if (subrecord == null) return ev;
        if (place is { Located: true })
        {
            // The one subrecord that holds this item, and the field's slice of it: the one candidate
            ev.Candidates = 1;
            var s = raw.Subrecords[place.Subrecord];
            int occurrence = 0;
            for (int i = 0; i < place.Subrecord; i++) if (raw.Subrecords[i].Signature == subrecord) occurrence++;
            var content = raw.Content(s);
            ev.Slices.Add(new EvidenceSlice(occurrence, s.Length, RawRecord.Hex(content, maxBytes), RawRecord.Hex(content.Slice(place.Offset, place.Size)), place.Reads));
            if (row.Def.Custom) ev.Notes.Add("Mutagen reads this field with hand-written code; the address above follows that code, and the bytes were checked against Mutagen's value.");
            return ev;
        }
        // The subrecords that can hold the field (GAPS S102): a field of the record's own is in a subrecord outside
        // components (BFCB … BFCE), a component's field in its own component; one of the same signature elsewhere is
        // another field's (a world's LOD Water Height NAM4 and its planet content manager component's Debug Color NAM4)
        var holders = Holders(schema.Model, raw, row, value.IsSet ? value.Path : field);
        ev.Candidates = holders.Count;
        int n = 0;
        foreach (var (at, ordinal) in holders)
        {
            if (n >= maxOccurrences) break;
            n++;
            var s = raw.Subrecords[at];
            var content = raw.Content(s);
            string? slice = null, reads = null;
            // Not when the field was looked for and not found: the schema's offset isn't then known to be its address
            if (place == null && row.Offset is int off && row.Def.Size is int size && size > 0 && !row.InList)
            {
                if (off + size <= content.Length)
                {
                    var bytes = content.Slice(off, size);
                    slice = RawRecord.Hex(bytes);
                    if (row.Def.Kind == "FormLink" && size == 4) reads = label(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes));
                }
                else slice = $"(the subrecord is {content.Length} bytes: it ends before this field)";
            }
            ev.Slices.Add(new EvidenceSlice(ordinal, s.Length, RawRecord.Hex(content, maxBytes), slice, reads));
        }
        if (holders.Count < ev.Occurrences)
        {
            int other = ev.Occurrences - holders.Count;
            bool own = !InComponent(schema.Model, row);
            ev.Notes.Add(own
                ? $"{other} of the record's {ev.Occurrences} {subrecord} subrecords {(other == 1 ? "is" : "are")} inside a component (between BFCB and BFCE): {(other == 1 ? "a component's field" : "components' fields")}, not this one."
                : $"{other} of the record's {ev.Occurrences} {subrecord} subrecords {(other == 1 ? "is" : "are")} the record's own or another component's, not this component's.");
        }
        if (place != null)
            ev.Notes.Add($"No exact address for this field{(value.Path.Contains('[') ? " inside a list item" : "")}: {place.Reason}. The whole subrecord is shown{(row.InList && row.Offset != null && !ReadByHand(row) ? $"; the schema's offset {row.Offset} counts from the start of the item, not of the subrecord" : "")}.");
        else if (row.InList && row.Offset != null) ev.Notes.Add("The field is inside a list: the offset counts from the start of each item, not of the subrecord.");
        if (row.Def.Custom) ev.Notes.Add(row.Offset == null && schema.Model.HandReadOf(row.Def.Object) is { } code
            ? $"Mutagen reads this field with hand-written code ({code}), so the schema gives no address for it: the real layout is that code's."
            : "Mutagen reads this field with hand-written code, so the schema's address for it is less certain.");
        return ev;
    }

    /// <summary>Evidence for a value the card decoded from a reflection blob (<see cref="ReflectionView"/>): the
    /// subrecord whose content is the blob (found by its bytes, not guessed), and the value's slice at the place
    /// Mutagen's reflection reader read it. Null when the field isn't below a decoded blob.</summary>
    /// <summary>
    /// The k-th of a reflection's FormLinks: Mutagen reads them from the blob by ReflectionData.FindFormIds, in order, so
    /// its bytes are the 4 at the k-th FormID's place in the subrecord that holds the blob, checked against Mutagen's link.
    /// Null when the field isn't a reflection's.
    /// </summary>
    static Evidence? ReflectionLinkEvidence(Card card, RawRecord raw, FieldValue value, CardField link, CardField links, Func<uint, string> label, Func<string, uint?>? formIdOf, int maxBytes)
    {
        var holder = links.Up;
        if (holder?.Children?.FirstOrDefault(c => c.Name == "Data") is not { } dataField || ReflectionView.Bytes(dataField.Json) is not { } blob
            || !Mutagen.Bethesda.Starfield.ReflectionData.IsReflection(blob) || Mutagen.Bethesda.Starfield.ReflectionData.TryParse(blob) is not { } data) return null;
        int k = int.Parse(link.Name[1..^1], CultureInfo.InvariantCulture);
        int at = -1, occurrence = 0, sameSig = 0;
        for (int i = 0; i < raw.Subrecords.Count; i++)
        {
            var sub = raw.Subrecords[i];
            if (holder.Signature != null && sub.Signature != holder.Signature) continue;
            if (raw.Content(sub).SequenceEqual(blob)) { at = i; occurrence = sameSig; break; }
            sameSig++;
        }
        var ids = data.FormIds;
        bool placed = at >= 0 && k < ids.Count && ids.Count == links.Children?.Count;
        int pos = placed ? ids[k].Position : 0;
        uint id = placed ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(raw.Content(raw.Subrecords[at]).Slice(pos, 4)) : 0;
        string key = link.Json is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue<string>(out var t) ? t : "";
        uint? want = key == "Null" ? 0u : formIdOf?.Invoke(key);
        bool same = placed && want == id;
        string? sig = at >= 0 ? raw.Subrecords[at].Signature : holder.Signature;
        var ev = new Evidence
        {
            Record = card.Label, Field = card.Type + "." + value.Path, Kind = "FormLink", Value = value.Text,
            RecordSignature = card.Signature, Compressed = raw.Compressed, Readable = link.Readable,
            Subrecord = sig, Offset = same ? pos : null, Size = 4,
            Occurrences = sig == null ? 0 : raw.Subrecords.Count(x => x.Signature == sig),
        };
        if (!same)
        {
            ev.Notes.Add(at < 0 ? $"No subrecord of the record holds exactly the bytes of {holder.Path}.Data."
                : !placed ? $"{links.Path}: Mutagen has {links.Children?.Count ?? 0} links and the blob holds {ids.Count} FormIDs, so the place isn't certain."
                : $"The {Ordinal(k + 1)} FormID of the blob, at offset {pos} of {sig}, reads {id:X8}, and Mutagen's link is {key}: not shown.");
            return ev;
        }
        var content = raw.Content(raw.Subrecords[at]);
        ev.Slices.Add(new EvidenceSlice(occurrence, content.Length, RawRecord.Hex(content, maxBytes), RawRecord.Hex(content.Slice(pos, 4)), label(id)));
        ev.Notes.Add($"{links.Path}[{k}] is the {Ordinal(k + 1)} of the {ids.Count} FormIDs Mutagen's reflection reader finds in the blob (ReflectionData.FindFormIds), at offset {pos} of {sig}. Checked: the bytes read FormID {id:X8}, Mutagen's link.");
        return ev;
    }

    static string Ordinal(int n) => (n % 100) is 11 or 12 or 13 ? n + "th" : (n % 10) switch { 1 => n + "st", 2 => n + "nd", 3 => n + "rd", _ => n + "th" };

    /// <summary>True for a field of a component (Components[*]{…}, or an object a component holds, BFCB … BFCE).</summary>
    internal static bool InComponent(SchemaModel model, FieldRow row)
        => row.Path.StartsWith("Components[*]", StringComparison.Ordinal)
            || row.Stack.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(o => model.EndMarkerOf(o) == "BFCE");

    /// <summary>
    /// The subrecords of the field's signature that can hold it, each with its ordinal among all the record's
    /// subrecords of that signature (GAPS S102). A field of the record's own: those outside components (between BFCB and
    /// BFCE a subrecord is a component's). A component's field: those inside its own component, the k-th BFCB … BFCE
    /// run when the path names Components[k], else inside any component.
    /// </summary>
    internal static List<(int At, int Ordinal)> Holders(SchemaModel model, RawRecord raw, FieldRow row, string path)
    {
        var list = new List<(int, int)>();
        string? sig = row.Subrecord;
        if (sig == null) return list;
        bool component = InComponent(model, row);
        var m = component ? Regex.Match(path, @"^Components\[(\d+)\]") : null;
        int? want = m is { Success: true } ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        int depth = 0, index = -1, ordinal = 0;
        for (int i = 0; i < raw.Subrecords.Count; i++)
        {
            string s = raw.Subrecords[i].Signature;
            if (s == "BFCB") { if (depth == 0) index++; depth++; }
            if (s == sig)
            {
                bool inside = depth > 0;
                if (component ? inside && (want == null || want == index) : !inside) list.Add((i, ordinal));
                ordinal++;
            }
            if (s == "BFCE" && depth > 0) depth--;
        }
        return list;
    }

    /// <summary>xEdit's path of a record's header flags, which every view of them shares (S106).</summary>
    const string XEditHeaderFlags = @"\Record Header\Record Flags";

    /// <summary>Where Mutagen lays out the record header, in words (its layout file's Header entries, C57).</summary>
    static string HeaderLayout(HandLayouts.HeaderField at) => at.Sig is { } sig ? $"Mutagen's layout of the {sig} record header (HandReadLayouts.xml, Header sig=\"{sig}\")" : "Mutagen's layout of the record header (HandReadLayouts.xml, Header)";

    /// <summary>Evidence for a field of the record header: its bytes there, checked against Mutagen's own property (read
    /// from Mutagen's object, so a value the JSON leaves out at its default is checked too). A FormKey is held against
    /// the FormID the plugin's file writes for it.</summary>
    static Evidence HeaderEvidence(RecordSchema schema, Card card, RawRecord raw, FieldValue value, FieldRow row, HandLayouts.HeaderField at, Func<uint, string> label, Func<string, uint?>? formIdOf)
    {
        var bytes = raw.Header.AsSpan(at.Offset, at.Size);
        ulong stored = at.Size == 2 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes) : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        bool isId = row.Def.Kind == "FormKey" || row.Def.Name == "FormID";
        string? reads = isId ? label((uint)stored) : null;
        object? mutagen = card.FromRecord?.Invoke(row.Def.Name);
        uint bit = at.Bit;
        ulong? want = mutagen switch
        {
            null => null,
            bool b when bit != 0 => b ? bit : 0u,
            Mutagen.Bethesda.Plugins.FormKey k => formIdOf?.Invoke(k.IsNull ? "Null" : k.ToString()),
            Enum e => FlagBits.Raw(e),
            uint or ushort or ulong or byte => Convert.ToUInt64(mutagen, CultureInfo.InvariantCulture),
            int or short or long or sbyte => unchecked((ulong)Convert.ToInt64(mutagen, CultureInfo.InvariantCulture)),
            _ => null,
        };
        ulong mask = at.Size == 2 ? 0xFFFFu : 0xFFFF_FFFFu;
        if (bit != 0) mask = bit;
        string Shown(ulong v) => isId ? $"FormID {v:X8}" : at.ViewOf != null || row.Def.Name is "Flags" or "MajorRecordFlagsRaw" ? $"0x{v:X}" : row.Def.Kind == "Int32" ? unchecked((int)(uint)v).ToString(CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture);
        bool same = want is ulong w && (w & mask) == (stored & mask);
        string where = at.ViewOf == null
            ? $"{row.Def.Name} is the record header's {at.Size} bytes at offset {at.Offset} (after the signature and the data size), as {HeaderLayout(at)} places them"
            : bit != 0 ? $"{row.Def.Name} is the record header's flag 0x{bit:X} in the {at.Size} bytes at offset {at.Offset}, a view of {at.ViewOf} ({HeaderLayout(at)})"
            : $"{row.Def.Name} is the record header's {at.Size} bytes of flags at offset {at.Offset}, a view of {at.ViewOf} (its {(row.Def.Enum ?? "enum")} names the bits; {HeaderLayout(at)})";
        var ev = new Evidence
        {
            Record = card.Label, Field = card.Type + "." + row.Path, Kind = row.Def.Kind, Value = value.Text, IsDefault = value.IsDefault,
            RecordSignature = card.Signature, InHeader = true, Offset = at.Offset, Size = at.Size, Occurrences = 1,
            Item = same ? new ItemPlace { Located = true, Offset = at.Offset, Size = at.Size, Where = where, Checked = bit != 0 ? $"the bytes read {Shown(stored)}: flag 0x{bit:X} is {((stored & bit) != 0 ? "set" : "clear")}, as Mutagen's value ({((stored & bit) != 0 ? "true" : "false")})" : $"the bytes read {Shown(stored)}, Mutagen's value", Reads = reads } : null,
            XEdit = (at.ViewOf != null ? XEditHeaderFlags : null)
                ?? schema.XEditPath(card.Signature, row.PlainPath) ?? (row.Def.XEdit is { Length: > 0 } x ? x : null), Readable = schema.ReadablePair(card.Signature, row.PlainPath) ?? row.Def.Readable,
            Unknown = row.Def.Unknown,
        };
        ev.Slices.Add(new EvidenceSlice(0, RawRecord.HeaderLength, RawRecord.Hex(raw.Header), RawRecord.Hex(bytes), reads));
        if (!same)
            ev.Notes.Add(want is ulong m ? $"{where}; they read {Shown(stored)}, and Mutagen's value is {Shown(m & mask)}: not the same."
                : $"{where}; Mutagen's value couldn't be read to check them against.");
        return ev;
    }

    static Evidence? ReflectionEvidence(Card card, RawRecord raw, FieldValue value, CardField decoded, Func<uint, string> label, int maxBytes)
    {
        CardField? root = decoded;
        while (root != null && !root.IsDecodedReflection) root = root.Up;
        var holder = root?.Up;
        if (holder?.Children?.FirstOrDefault(c => c.Name == "Data") is not { } dataField || ReflectionView.Bytes(dataField.Json) is not { } blob) return null;
        int at = -1, occurrence = 0, sameSig = 0;
        string? sig = null;
        for (int i = 0; i < raw.Subrecords.Count; i++)
        {
            var sub = raw.Subrecords[i];
            if (holder.Signature != null && sub.Signature != holder.Signature) continue;
            if (raw.Content(sub).SequenceEqual(blob)) { at = i; sig = sub.Signature; occurrence = sameSig; break; }
            sameSig++;
        }
        int pos = decoded.Position ?? 0;
        int? size = decoded.Children == null ? decoded.Size : null;
        string where = $"{holder.Path}.Data, the blob Mutagen's reflection reader decoded";
        var ev = new Evidence
        {
            Record = card.Label, Field = card.Type + "." + value.Path, Kind = decoded.Kind,
            Value = decoded.Children != null ? decoded.Variant ?? $"{decoded.Children.Count} values" : decoded.Text,
            RecordSignature = card.Signature, Subrecord = sig ?? holder.Signature, Offset = pos, Size = size,
            Readable = decoded.Readable, Compressed = raw.Compressed,
            Occurrences = sig == null ? 0 : raw.Subrecords.Count(x => x.Signature == sig),
        };
        if (at < 0)
        {
            ev.Notes.Add($"No subrecord of the record holds exactly the bytes of {where}: the offset {pos} counts from the start of that blob.");
            return ev;
        }
        var content = raw.Content(raw.Subrecords[at]);
        string? slice = size is int n && n > 0 && pos + n <= content.Length ? RawRecord.Hex(content.Slice(pos, n)) : null;
        string? reads = decoded.Kind == "FormLink" && size == 4 && pos + 4 <= content.Length ? label(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(content.Slice(pos, 4))) : null;
        ev.Slices.Add(new EvidenceSlice(occurrence, content.Length, RawRecord.Hex(content, maxBytes), slice, reads));
        ev.Notes.Add($"Decoded from {where} (ReflectionData): the {sig} subrecord's content is the whole blob, so the offset counts from its start.{(size == null ? " An object, list or map: its offset is where its first byte is; its parts follow in the blob's own order (a list's items in a LIST chunk, a map's in a MAPC chunk)." : "")}");
        if (decoded.Readable != decoded.Name && !decoded.Name.StartsWith('[')) ev.Notes.Add($"The Creation Kit calls it \"{decoded.Readable}\"; the engine's name is {decoded.Name}.");
        if (decoded.Note != null) ev.Notes.Add(decoded.Note);
        return ev;
    }

    /// <summary>A value the record's JSON leaves out at its default, inside a list item the record has: a card field
    /// for it under that item, so its bytes can be found and checked like any other (Mutagen writes it all the same).</summary>
    static CardField? DefaultLeaf(Card card, FieldValue value)
    {
        int dot = value.Path.LastIndexOf('.');
        if (dot < 0 || value.Json == null) return null;
        string parentPath = value.Path[..dot], name = value.Path[(dot + 1)..];
        var parent = Find(card.Fields, parentPath);
        if (parent?.Children == null) return null;
        return new CardField { Name = name, Readable = value.Readable ?? name, Kind = value.Kind ?? "", Signature = value.Signature, Json = value.Json, Text = value.Text, Up = parent, IsDefault = true };

        static CardField? Find(List<CardField> level, string path)
        {
            foreach (var f in level)
            {
                string p = f.Path;
                if (p == path) return f;
                if (f.Children != null && path.StartsWith(p, StringComparison.Ordinal) && path.Length > p.Length && path[p.Length] is '.' or '[')
                    return Find(f.Children, path);
            }
            return null;
        }
    }

    /// <summary>True when the field sits below a list Mutagen reads with hand-written code: the schema's offsets there
    /// follow the record definitions' field order, not the bytes.</summary>
    static bool ReadByHand(FieldRow row)
    {
        for (var r = row; r != null; r = r.Parent) if (r.Def.Custom) return true;
        return false;
    }

    static string Unindexed(string path) => System.Text.RegularExpressions.Regex.Replace(path, @"\[\d+\]", "[*]");

    /// <summary>Several kinds of object can sit at one path. The row whose kind and subrecord fit the value read is taken.</summary>
    static FieldRow Pick(List<FieldRow> rows, FieldValue value)
    {
        if (rows.Count == 1) return rows[0];
        var same = rows.Where(r => value.Signature != null && r.Subrecord == value.Signature && r.Def.Kind == value.Kind).ToList();
        if (same.Count > 0) return same[0];
        same = rows.Where(r => r.Def.Kind == value.Kind).ToList();
        return same.Count > 0 ? same[0] : rows[0];
    }
}
