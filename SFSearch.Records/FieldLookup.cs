// "Fields" mode and evidence.
//  - Fields: a named list of fields of one record. A field the JSON leaves out is answered with its default wherever
//    the schema says the field is always there; a field that may be absent is answered "not set".
//  - Evidence: for one field, the raw subrecord bytes from the file, the slice the field occupies when the schema
//    has its offset and size, the Mutagen field, and the xEdit path.
using System.Globalization;
using System.Text;
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
        if (Note != null) sb.Append("   [").Append(Note).Append(']');
        return sb.ToString();
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["asked"] = Asked, ["path"] = Path, ["set"] = IsSet, ["default"] = IsDefault, ["value"] = Json?.DeepClone(), ["text"] = Text };
        if (Kind != null) o["kind"] = Kind;
        if (Signature != null) o["signature"] = Signature;
        if (Readable != null) o["readable"] = Readable;
        if (Note != null) o["note"] = Note;
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
    /// <summary>For a field inside a list item: where the item and the field were found, or why they couldn't be.</summary>
    public ItemPlace? Item { get; init; }
    public List<string> Notes { get; } = new();

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.Append(Field[(Field.IndexOf('.') + 1)..]).Append(" = ").Append(Value ?? "(not set)").Append(IsDefault ? "   [the default: the record doesn't set it]" : "").Append('\n');
        string address = Subrecord == null ? $"{RecordSignature}: the schema doesn't know which subrecord holds it" : $"{RecordSignature}/{Subrecord}";
        if (Offset != null) address += $", offset {Offset}";
        if (Size != null) address += $", {Size} byte{(Size == 1 ? "" : "s")}";
        if (Slices.Count == 0) sb.Append("  file     ").Append(address).Append(Subrecord == null ? "" : $": the record has no {Subrecord} subrecord").Append('\n');
        foreach (var s in Slices)
        {
            string which = Occurrences > 1 ? $" (the {Ordinal(s.Occurrence + 1)} of {Occurrences.ToString("N0", CultureInfo.InvariantCulture)} {Subrecord} subrecords)" : "";
            sb.Append("  file     ").Append(address).Append(which);
            if (s.FieldBytes != null) sb.Append(": ").Append(s.FieldBytes);
            if (s.Reads != null) sb.Append("   → ").Append(s.Reads);
            sb.Append('\n');
            sb.Append("           ").Append(Subrecord).Append(" is ").Append(s.SubrecordLength.ToString("N0", CultureInfo.InvariantCulture)).Append(" bytes: ").Append(s.SubrecordBytes).Append('\n');
        }
        if (Occurrences > Slices.Count && Slices.Count > 0 && Item is not { Located: true }) sb.Append($"           … {Occurrences - Slices.Count} more {Subrecord} subrecords\n");
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
                ulong bits = Convert.ToUInt64(e, CultureInfo.InvariantCulture), named = 0;
                var parts = new List<string>();
                foreach (var m in Enum.GetValues(t).Cast<Enum>())
                {
                    ulong mv = Convert.ToUInt64(m, CultureInfo.InvariantCulture);
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

    /// <summary>A half-precision float (Mutagen's <c>Half</c>, fixes.17 C221) as the serializer writes it (1.38.6-fixes.3,
    /// C222): the number it holds (every Half is exactly a double), "Infinity", "-Infinity", "NaN", or "NaN:" and its 4 hex
    /// digits for a NaN whose bits aren't .NET's own.</summary>
    public static bool TryReadHalf(System.Text.Json.JsonElement el, out Half h)
    {
        h = default;
        if (el.ValueKind == System.Text.Json.JsonValueKind.Number) { h = (Half)el.GetDouble(); return true; }
        if (el.ValueKind != System.Text.Json.JsonValueKind.String) return false;
        string s = el.GetString()!.Trim();
        if (s.StartsWith("NaN:", StringComparison.OrdinalIgnoreCase))
        {
            if (!ushort.TryParse(s.AsSpan(4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort bits)) return false;
            h = BitConverter.UInt16BitsToHalf(bits);
            return true;
        }
        switch (s)
        {
            case "Infinity": h = Half.PositiveInfinity; return true;
            case "-Infinity": h = Half.NegativeInfinity; return true;
            case "NaN": h = Half.NaN; return true;
        }
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return false;
        h = (Half)d;
        return true;
    }

    /// <summary>How the serializer writes a byte field with no bytes (Mutagen.Bethesda.Serialization 1.38.6-fixes.2 on).</summary>
    public const string NoBytes = "0x";

    /// <summary>True for a byte field's value that holds no bytes: "0x", or "[]" as the serializer wrote it before 1.38.6-fixes.2.</summary>
    public static bool IsNoBytes(string? s) => s is NoBytes or "[]";

    /// <summary>The value a field has when the JSON leaves it out and the schema says the field is always there.</summary>
    public static (JsonNode? Json, string? Text) DefaultOf(RecordSchema schema, FieldDef def)
    {
        static (JsonNode?, string?) Raw(string json, string? text = null) => (JsonNode.Parse(json), text ?? json);
        static (JsonNode?, string?) Str(string s) => (JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(s)), s);
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
    public static Evidence Evidence(RecordSchema schema, Card card, RawRecord raw, string field, Func<uint, string> label, int maxOccurrences = 4, int maxBytes = 64, Func<string, uint?>? formIdOf = null, StringLookup? textOf = null)
    {
        var values = Fields(schema, card, new[] { field });
        var value = values[0];
        // A value decoded from a reflection blob (to-do F30): its place in the blob is its place in the subrecord
        if (value.Field is { Position: not null } decoded && ReflectionEvidence(card, raw, value, decoded, label, maxBytes) is { } fromBlob) return fromBlob;
        // The schema's row for the field: by Mutagen path first (indexes and variant markers aside)
        string schemaPath = string.Join('.', Parse(value.IsSet || value.IsDefault ? Unindexed(value.Path) : Unindexed(field)).Select(s => s.Name + (s.All ? "[*]" : "")));
        var found = schema.Model.Resolve(card.Type, schemaPath, ignoreCase: true).Found;
        if (found.Count == 0) found = schema.Model.Resolve(card.Type, schemaPath.Replace("[*]", ""), ignoreCase: true).Found;
        FieldRow? row = found.Count > 0 ? Pick(found.Select(f => f.Row).ToList(), value) : null;
        if (row == null)
            throw new RecordException($"The schema has no field \"{field}\" for {card.Type}, so there is no file address for it. Its value: {value.Text ?? "(not set)"}.");

        string plain = row.PlainPath;
        // A field inside a list item: the schema's offset counts from the item, so the item itself is found in the
        // record's bytes first (EvidenceItems.cs). Every slice found is checked against Mutagen's value.
        // A translated text, and a half of a gendered value, are located too, inside a list or not: the one subrecord
        // that is this field's is found and checked (fix F6r). A text's subrecord holds the text, or in a localized
        // plugin a string ID; a gendered value's halves are told apart as Mutagen's gendered reader tells them apart.
        bool inItem = value.Path.Contains('['), text = row.Def.Translated && row.Def.Kind == "String", gendered = InGendered(row);
        // A field inside an object's subrecord with no offset in the schema (a text or a list before it takes a size of
        // its own in each record, or hand-written code places it): its place is walked and checked, or the reason given
        // (GAPS S36, S37)
        bool unplaced = row.Offset == null && row.Subrecord != null && row.Parent?.Subrecord == row.Subrecord && (row.Def.Size is > 0 || row.Def.Kind is "String" or "AssetLink");
        CardField? leaf = value.IsSet ? value.Field : value.IsDefault && (inItem || gendered || unplaced) ? DefaultLeaf(card, value) : null;
        ItemPlace? place = leaf != null && (inItem || text || gendered || unplaced) ? ItemLocator.Locate(schema, card, leaf, raw, formIdOf, label, textOf) : null;
        // A field outside list items that Mutagen reads by hand and its hand-read layouts name (HandReadLayouts.xml,
        // C186: an object mod's MaxRank, a weather's ambient colors): located by the layout and checked
        if (place == null && leaf != null) place = ItemLocator.LocateHand(schema, card, leaf, raw, formIdOf, label, textOf);
        string? subrecord = place is { Located: true } ? raw.Subrecords[place.Subrecord].Signature : row.Subrecord;
        var ev = new Evidence
        {
            Record = card.Label, Field = card.Type + "." + (value.IsSet || place is { Located: true } ? value.Path : row.Path), Kind = row.Def.Kind,
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
            // The one subrecord that holds this item, and the field's slice of it
            var s = raw.Subrecords[place.Subrecord];
            int occurrence = 0;
            for (int i = 0; i < place.Subrecord; i++) if (raw.Subrecords[i].Signature == subrecord) occurrence++;
            var content = raw.Content(s);
            ev.Slices.Add(new EvidenceSlice(occurrence, s.Length, RawRecord.Hex(content, maxBytes), RawRecord.Hex(content.Slice(place.Offset, place.Size)), place.Reads));
            if (row.Def.Custom) ev.Notes.Add("Mutagen reads this field with hand-written code; the address above follows that code, and the bytes were checked against Mutagen's value.");
            return ev;
        }
        int n = 0;
        foreach (var s in raw.Subrecords.Where(s => s.Signature == row.Subrecord))
        {
            if (n >= maxOccurrences) break;
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
            ev.Slices.Add(new EvidenceSlice(n++, s.Length, RawRecord.Hex(content, maxBytes), slice, reads));
        }
        if (place != null)
            ev.Notes.Add($"No exact address for this field{(inItem ? " inside a list item" : "")}: {place.Reason}. The whole subrecord is shown{(row.InList && row.Offset != null && !ReadByHand(row) ? $"; the schema's offset {row.Offset} counts from the start of the item, not of the subrecord" : "")}.");
        else if (row.InList && row.Offset != null) ev.Notes.Add("The field is inside a list: the offset counts from the start of each item, not of the subrecord.");
        if (row.Def.Custom) ev.Notes.Add(row.Offset == null && schema.Model.HandReadOf(row.Def.Object) is { } code
            ? $"Mutagen reads this field with hand-written code ({code}), so the schema gives no address for it: the real layout is that code's."
            : "Mutagen reads this field with hand-written code, so the schema's address for it is less certain.");
        return ev;
    }

    /// <summary>Evidence for a value the card decoded from a reflection blob (<see cref="ReflectionView"/>): the
    /// subrecord whose content is the blob (found by its bytes, not guessed), and the value's slice at the place
    /// Mutagen's reflection reader read it. Null when the field isn't below a decoded blob.</summary>
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

    /// <summary>True when the field is a half of a gendered value, or inside one.</summary>
    static bool InGendered(FieldRow row)
    {
        // The halves are rows of the object that holds the gendered value: WorldModel.Male, WorldModel.Female
        for (var r = row; r != null; r = r.Parent)
            if (r.Def.Kind == "Gendered" || r.Def.Path.EndsWith(".Male", StringComparison.Ordinal) || r.Def.Path.EndsWith(".Female", StringComparison.Ordinal)) return true;
        return false;
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
