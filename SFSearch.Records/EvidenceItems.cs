// Evidence: where a field's value sits in the record's bytes, as Mutagen's own reader read it (GAPS S6, S36, S37; W1).
//
// Since Mutagen 0.55.0-fixes.20 (C60) Mutagen's reader notes, as it reads, where each value sits: the record's value
// positions (MajorRecordReader.ReadValuePositions), by the generated readers, the list, gendered and dictionary
// translations and every hand-written reader alike. Evidence takes a value's place from them (FromPositions): the
// subrecord, the offset, the length, the bits of a shared byte, and whether Mutagen converts the bytes. The slice is read
// back by the field's kind and held against Mutagen's value, so the answer shows the file's bytes and Mutagen's value side
// by side. SFSearch keeps no copy of Mutagen's reading rules for evidence any more (its item cutting and walks, and the
// hand-read layout walk's evidence path, are gone). Since fixes.24 the plugin header (TES4) has value positions of its own
// (C82, MajorRecordReader.ReadModHeaderValuePositions: GAPS S172), and the values hand-written readers set have theirs
// (C83, GAPS S173): a dictionary entry's stored key (ValuePositionKind.Key), a perk effect's Modification and
// NumTraversalFluffBytes as converted values; the record XML's layoutMap and after: are no longer read for evidence.
//
// The rest of this file is the subrecord claims of the byte link walk (LinkWalk.cs, "Which field of an object each
// subrecord is"): SFSearch's speed design (the byte walk reads no record through Mutagen), held equal to Mutagen's value
// positions record by record by sfsreport-tests link-positions.
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Strings;
using SFSearch.SchemaGen;

namespace SFSearch.Records;

/// <summary>The texts a string ID names in a localized plugin's strings files of one language: the file (as the
/// stamp names it) and its text, for each file that holds the ID. With a table (the schema's StringsTable, GAPS S35),
/// only that one: .strings, .dlstrings or .ilstrings; without one, all three.</summary>
public delegate IReadOnlyList<(string File, string Text)> StringLookup(uint id, Language language, StringsSource? table);

/// <summary>Where a field inside a list item sits in the record's bytes, or why that can't be told exactly.</summary>
public sealed class ItemPlace
{
    public bool Located { get; init; }
    /// <summary>The index of the subrecord in <see cref="RawRecord.Subrecords"/>.</summary>
    public int Subrecord { get; init; } = -1;
    /// <summary>The field's offset from the start of that subrecord's content.</summary>
    public int Offset { get; init; }
    public int Size { get; init; }
    /// <summary>How the address was worked out, step by step.</summary>
    public string? Where { get; init; }
    /// <summary>What the slice reads as, and that it is Mutagen's value.</summary>
    public string? Checked { get; init; }
    /// <summary>For a link: the record the slice's FormID names.</summary>
    public string? Reads { get; init; }
    /// <summary>When not located: why.</summary>
    public string? Reason { get; init; }
}

internal sealed partial class ItemLocator
{
    readonly RecordSchema _schema;
    readonly SchemaModel _m;
    readonly RawRecord _raw;
    readonly Func<string, uint?>? _formIdOf;
    readonly Func<uint, string> _label;
    readonly StringLookup? _textOf;
    readonly List<string> _where = new();

    ItemLocator(RecordSchema schema, RawRecord raw, Func<string, uint?>? formIdOf, Func<uint, string> label, StringLookup? textOf, Tables? tables = null)
    {
        _schema = schema; _m = schema.Model; _raw = raw; _formIdOf = formIdOf; _label = label; _textOf = textOf;
        _t = tables ?? new Tables();
    }

    sealed class NotFound(string reason) : Exception(reason);
    static NotFound Fail(string reason) => new(reason);

    // ------------------------------------------------------------------ Mutagen's value positions (C60)
    /// <summary>
    /// Where <paramref name="leaf"/>, one value of the card, sits in the record, as Mutagen's own reader read it: the
    /// record's value positions (C60, <c>MajorRecordReader.ReadValuePositions</c>), noted by the generated and the
    /// hand-written readers as they read. The slice is read back by the field's kind and held against Mutagen's value; a
    /// value Mutagen converts (a scaled number, an inverted bit, a byte of 255) is given with its bytes and Mutagen's value.
    /// </summary>
    /// <param name="def">The schema's row of the field (its kind, enum and strings table).</param>
    public static ItemPlace FromPositions(RecordSchema schema, FieldDef def, CardField leaf, RawRecord raw, ValuePositions positions, Func<string, uint?>? formIdOf, Func<uint, string> label, StringLookup? textOf = null, Func<string, string>? positionPath = null)
    {
        var l = new ItemLocator(schema, raw, formIdOf, label, textOf);
        try { return l.Placed(def, leaf, positions, positionPath); }
        catch (NotFound e)
        {
            return new ItemPlace { Located = false, Reason = e.Message, Where = l._where.Count > 0 ? string.Join("; ", l._where) : null };
        }
    }

    /// <summary>
    /// The path of a card's value as Mutagen's value positions name it: the card's path, a dictionary entry named by its
    /// key as Mutagen read it (<c>BipedObjects[Body]</c>, the k-th key among the record's positions) and its Value's fields
    /// directly under it. <paramref name="key"/>: the card's value is an entry's Key, whose stored bytes Mutagen notes as a
    /// Key position at the entry's path (C83: a package data input's UNAM index); the path is the entry's.
    /// </summary>
    static string PositionPath(CardField leaf, ValuePositions positions, out bool key)
    {
        key = false;
        var chain = new List<CardField>();
        for (var c = leaf; c != null; c = c.Up) chain.Add(c);
        chain.Reverse();
        var path = new StringBuilder();
        for (int i = 0; i < chain.Count; i++)
        {
            var c = chain[i];
            if (c.Name.StartsWith('[') && c.Up is { Kind: "Dictionary" })
            {
                int k = int.Parse(c.Name[1..^1], CultureInfo.InvariantCulture);
                string prefix = path + "[";
                // The entry's key as the card has it (a number, or an enum member's name), which is how positions name it
                if (c.Children?.FirstOrDefault(x => x.Name == "Key")?.Json is JsonValue kj && kj.ToString() is { Length: > 0 } keyText
                    && positions.Positions.Any(p => p.Path.StartsWith(prefix + keyText.Trim('"') + "]", StringComparison.Ordinal)))
                {
                    path.Append('[').Append(keyText.Trim('"')).Append(']');
                    if (i + 1 < chain.Count && chain[i + 1].Name == "Key") { KeyAt(chain[i + 1], path.ToString(), positions); key = true; return path.ToString(); }
                    if (i + 1 < chain.Count && chain[i + 1].Name == "Value") i++;
                    continue;
                }
                var keys = new List<string>();
                foreach (var p in positions.Positions)
                    if (p.Path.StartsWith(prefix, StringComparison.Ordinal) && p.Path.IndexOf(']', prefix.Length) is int end and > 0
                        && p.Path[prefix.Length..end] is var kt && !keys.Contains(kt)) keys.Add(kt);
                if (k >= keys.Count) throw Fail($"{c.Path}: Mutagen's reader noted {keys.Count} entries of {path}");
                path.Append('[').Append(keys[k]).Append(']');
                if (i + 1 < chain.Count && chain[i + 1].Name == "Key") { KeyAt(chain[i + 1], path.ToString(), positions); key = true; return path.ToString(); }
                if (i + 1 < chain.Count && chain[i + 1].Name == "Value") i++;
                continue;
            }
            if (c.Name.StartsWith('[')) path.Append(c.Name);
            else { if (path.Length > 0) path.Append('.'); path.Append(c.Name); }
        }
        return path.ToString();
    }

    /// <summary>A dictionary entry's Key is placed only by a Key position at the entry's path (C83); a key that is the
    /// entry's place alone (no stored bytes of its own Mutagen noted) has none.</summary>
    static void KeyAt(CardField keyField, string entry, ValuePositions positions)
    {
        if (!positions[entry].Any(x => x.Kind == ValuePositionKind.Key))
            throw Fail($"{keyField.Path}: the key has no bytes of its own that Mutagen's reader noted (it is the entry's place: {entry})");
    }

    ItemPlace Placed(FieldDef def, CardField leaf, ValuePositions positions, Func<string, string>? positionPath = null)
    {
        if (leaf.Children != null) throw Fail($"{leaf.Path} is a whole {(leaf.Kind is "List" or "Array" ? "list" : "object")}, not one value: name one of its fields to get its bytes");
        if (leaf.Json == null) throw Fail($"{leaf.Path} has no value in the record's JSON");
        // The row of the field on the object's own kind (a perk effect's Modification is its kind's, with its own layoutMap)
        if (leaf.Up?.Object is { } kind && !leaf.Name.StartsWith('[')
            && _m.AllFields(kind).Select(x => x.F).FirstOrDefault(x => x.Path == leaf.Name && x.Name == leaf.Name) is { } own) def = own;
        string path = PositionPath(leaf, positions, out bool isKey);
        if (positionPath != null) path = positionPath(path);
        var all = positions[path];
        // Every kind of position (C83 added Key): a value's own bytes, a marker's presence, an entry's stored key; an object,
        // a list, its count or a hand-written step only hold values
        ValuePosition? p = null;
        foreach (var x in all)
        {
            bool takes = x.Kind switch
            {
                ValuePositionKind.Value => !isKey,
                ValuePositionKind.Presence => !isKey && (p == null || p.Kind != ValuePositionKind.Value),
                ValuePositionKind.Key => isKey,
                ValuePositionKind.Object or ValuePositionKind.List or ValuePositionKind.Count or ValuePositionKind.HandRead => false,
                _ => false,
            };
            if (takes && (p == null || x.Kind == ValuePositionKind.Value || x.Kind == ValuePositionKind.Key)) p = x;
        }
        // The key's own kind (a dictionary's key: an sbyte, an enum …), as the schema says it
        if (isKey && KeyDef(def) is { } keyDef) def = keyDef;
        if (p == null)
        {
            // What Mutagen's reader did note around it: the nearest object, list or hand-written step that holds the path
            string around = path;
            ValuePosition? holder = null;
            while (holder == null && around.Length > 0)
            {
                int cut = Math.Max(around.LastIndexOf('.'), around.LastIndexOf('['));
                around = cut > 0 ? around[..cut] : "";
                holder = positions[around].FirstOrDefault();
            }
            throw Fail(all.Count > 0
                ? $"Mutagen's reader noted {path} only as {string.Join(", ", all.Select(x => x.Kind.ToString().ToLowerInvariant()))}, not as {(isKey ? "a stored key" : "a value")}"
                : holder != null && holder.SubrecordIndex >= 0
                    ? $"Mutagen's reader noted no position for {path}; {(around.Length > 0 ? around : "the record")} ({holder.Kind.ToString().ToLowerInvariant()}) is {RunText(holder)}"
                    : $"Mutagen's reader noted no position for {path}");
        }
        if (p.SubrecordIndex < 0) throw Fail($"{path} is in the record header (offset {p.Offset}), not in a subrecord");
        if (p.SubrecordIndex >= _raw.Subrecords.Count || _raw.Subrecords[p.SubrecordIndex].Signature != p.Subrecord?.Type)
            throw Fail($"{path}: Mutagen's reader read it in subrecord #{p.SubrecordIndex + 1} ({p.Subrecord}), which the record's bytes don't have there");
        // The list item or object it belongs to, when that is a run of subrecords of its own
        for (string up = path; up.Length > 0;)
        {
            int cut = Math.Max(up.LastIndexOf('.'), up.LastIndexOf('['));
            up = cut > 0 ? up[..cut] : "";
            if (positions[up].FirstOrDefault(x => x.Kind is ValuePositionKind.Object or ValuePositionKind.List) is { SubrecordCount: > 1, SubrecordIndex: >= 0 } run)
            {
                _where.Add($"{up} is {RunText(run)}");
                break;
            }
        }
        string sig = p.Subrecord!.Value.Type;
        string at = $"offset {p.Offset} of {sig} (the record's {Ordinal(p.SubrecordTurn + 1)} {sig}, subrecord #{p.SubrecordIndex + 1})";
        string? en = def.Enum == "T" ? null : def.Enum;
        if (p.Kind == ValuePositionKind.Presence)
        {
            _where.Add($"{path} is given by {sig} being there (Mutagen's value positions: presence)");
            return new ItemPlace { Located = true, Subrecord = p.SubrecordIndex, Offset = 0, Size = 0, Where = string.Join("; ", _where), Checked = $"{sig} is there: Mutagen's value is {leaf.Text}" };
        }
        _where.Add($"Mutagen's reader read {(isKey ? $"the key of {path}" : path)} at {at}, {p.Length} byte{(p.Length == 1 ? "" : "s")}{(p.BitMask != 0 ? $", bits 0x{p.BitMask:X} of them" : "")} (its value positions{(isKey ? ": the entry's stored key" : "")})");
        if (p.Converted)
        {
            // Mutagen computes the value from these bytes (the record XML's layoutEncoding names how, where it does)
            _where.Add($"Mutagen converts these bytes to its value{(def.LayoutEncoding != null ? $" (layoutEncoding {def.LayoutEncoding})" : "")}");
            var bytes = _raw.Content(_raw.Subrecords[p.SubrecordIndex]).Slice(p.Offset, p.Length);
            string? reads = def.Kind is "FormLink" or "FormLinkOrIndex" && p.Length == 4 ? _label(BinaryPrimitives.ReadUInt32LittleEndian(bytes)) : null;
            return new ItemPlace
            {
                Located = true, Subrecord = p.SubrecordIndex, Offset = p.Offset, Size = p.Length, Where = string.Join("; ", _where), Reads = reads,
                Checked = $"Mutagen's value, {leaf.Text}, is computed from these bytes by its reader",
            };
        }
        if (p.BitMask != 0)
        {
            // Some bits of the little-endian value of these bytes (a byte two fields share; a flags value without the bit
            // another field stands for): those bits, shifted down, are Mutagen's value
            int shift = System.Numerics.BitOperations.TrailingZeroCount(p.BitMask);
            var content = _raw.Content(_raw.Subrecords[p.SubrecordIndex]);
            if (p.Length is not (1 or 2 or 4 or 8) || p.Offset + p.Length > content.Length) throw Fail($"{path}: bits 0x{p.BitMask:X} of {p.Length} bytes at {at}, which this reader doesn't read back");
            ulong bits = (Unsigned(content.Slice(p.Offset, p.Length)) & p.BitMask) >> shift;
            var value = new byte[p.Length];
            for (int k = 0; k < value.Length; k++) value[k] = (byte)(bits >> (8 * k));
            var (ok, reads, said) = Decode(def, en, value, leaf.Json);
            if (ok == null) throw Fail($"{path}: bits 0x{p.BitMask:X} at {at} can't be checked against Mutagen's value for a {def.Kind}{(said != null ? $" ({said})" : "")}");
            if (ok == false) throw Fail($"{path}: bits 0x{p.BitMask:X} of {RawRecord.Hex(content.Slice(p.Offset, p.Length))} at {at} read as {said}, and Mutagen's value is {leaf.Text}");
            return new ItemPlace
            {
                Located = true, Subrecord = p.SubrecordIndex, Offset = p.Offset, Size = p.Length, Where = string.Join("; ", _where), Reads = reads,
                Checked = $"checked: the bytes read {said} in bits 0x{p.BitMask:X}, Mutagen's value",
            };
        }
        if (def.Kind is "String" or "AssetLink" && def.TextForm is { } form && form.StartsWith("PrependLength", StringComparison.Ordinal))
        {
            // Mutagen read the text's length and the text: the length is said, the slice is the text's own bytes
            int prefix = form switch { "PrependLengthUShort" => 2, "PrependLengthUInt8" => 1, _ => 4 };
            var content = _raw.Content(_raw.Subrecords[p.SubrecordIndex]);
            if (p.Length >= prefix && (long)ReadCount(content, p.Offset, prefix) == p.Length - prefix)
            {
                _where.Add($"a {prefix}-byte length at offset {p.Offset} reads {p.Length - prefix}, the text is at offset {p.Offset + prefix} ({form})");
                if (p.Length == prefix)
                    return new ItemPlace { Located = true, Subrecord = p.SubrecordIndex, Offset = p.Offset, Size = prefix, Where = string.Join("; ", _where), Checked = "checked: the length reads 0, and Mutagen's text is empty" };
                return Leaf(p.SubrecordIndex, p.Offset + prefix, p.Length - prefix, def, en, leaf);
            }
        }
        return Leaf(p.SubrecordIndex, p.Offset, p.Length, def, en, leaf);
    }

    static long ReadCount(ReadOnlySpan<byte> c, int at, int length) => at + length > c.Length ? -1 : length switch
    {
        1 => c[at], 2 => BinaryPrimitives.ReadUInt16LittleEndian(c[at..]), _ => BinaryPrimitives.ReadUInt32LittleEndian(c[at..]),
    };

    /// <summary>A position's subrecords in words: "subrecords OBTE … STOP" (#4 to #9) or "subrecord #4 (DATA)".</summary>
    string RunText(ValuePosition p) => p.SubrecordCount <= 1
        ? $"subrecord #{p.SubrecordIndex + 1} ({Sig(p.SubrecordIndex)})"
        : $"subrecords {Run(p.SubrecordIndex, Math.Min(_raw.Subrecords.Count, p.SubrecordIndex + p.SubrecordCount))} (#{p.SubrecordIndex + 1} to #{p.SubrecordIndex + p.SubrecordCount})";

    string Sig(int index) => _hidden != null && _hidden.Contains(index) ? HiddenSig : _raw.Subrecords[index].Signature;

    /// <summary>The signatures of subrecords [s, e): all of them up to 12, else the first and last few and how many.</summary>
    string Run(int s, int e) => e - s <= 12
        ? string.Join(" ", Enumerable.Range(s, e - s).Select(Sig))
        : $"{string.Join(" ", Enumerable.Range(s, 5).Select(Sig))} … {string.Join(" ", Enumerable.Range(e - 2, 2).Select(Sig))} ({e - s} subrecords)";

    string? Enum(FieldDef def, string? gen) => def.Enum == "T" ? gen : def.Enum;

    // ------------------------------------------------------------------ Entries and fields of variable size (GAPS S37)
    // Mutagen's generated reader reads an object's fields one after another. A field whose size isn't fixed takes, in
    // each record, what the file says: a list its count (CounterLength bytes before the entries, the XML's
    // counterLength) and its entries, a text its length (TextForm, the XML's binaryType: a length before it, or a zero
    // byte after it). The walk reads those counts and lengths from the file and checks each one against Mutagen's value
    // (its count of the list, its text), so a walk that isn't the one Mutagen read stops with the reason.

    static bool CountedBySubrecord(FieldDef f) => f.Note != null && f.Note.Contains("counted by ", StringComparison.Ordinal);

    static CardField? Child(CardField? parent, string name) => parent?.Children?.FirstOrDefault(x => x.Name == name);

    /// <summary>The bytes of an entry's length after a list's count (the XML's expectedLengthLength: a planet biome's
    /// flora), which Mutagen's reader checks; null when there is none.</summary>
    internal static int? EntryLength(FieldDef list) => list.Note != null && EntryLengthNote.Match(list.Note) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    static readonly Regex EntryLengthNote = new(@"then the length of an entry \((\d+) bytes\)", RegexOptions.Compiled);

    // ------------------------------------------------------------------ The value check
    /// <param name="mask">Not 0: the field is some bits of the one byte at <paramref name="offset"/>; its value is
    /// <c>(byte &amp; mask) &gt;&gt; shift</c>.</param>
    /// <param name="map">Not null: the value the field has, worked out from the byte at <paramref name="offset"/> the
    /// way Mutagen's reader works it out (a perk effect's kind of modification is its function's number).</param>
    ItemPlace Leaf(int sub, int offset, int size, FieldDef def, string? enumName, CardField leaf, byte mask = 0, int shift = 0, byte? map = null)
    {
        var content = _raw.Content(_raw.Subrecords[sub]);
        if (size <= 0) throw Fail($"{leaf.Path} has no bytes of its own (a {def.Kind} that is true when its subrecord is there)");
        if (offset < 0 || offset + size > content.Length)
            throw Fail($"{leaf.Path}: offset {offset}, {size} bytes, is past the end of {Sig(sub)} ({content.Length} bytes)");
        var bytes = map is byte mapped ? new[] { mapped } : mask == 0 ? content.Slice(offset, size) : new[] { (byte)((content[offset] & mask) >> shift) };
        var (ok, reads, said) = Decode(def, enumName, bytes, leaf.Json);
        if (ok == null) throw Fail($"{leaf.Path}: the bytes at offset {offset} of {Sig(sub)} can't be checked against Mutagen's value for a {def.Kind}{(said != null ? $" ({said})" : "")}, so no slice is given");
        if (ok == false) throw Fail($"{leaf.Path}: the bytes at offset {offset} of {Sig(sub)} ({RawRecord.Hex(content.Slice(offset, size))}{(mask != 0 ? $", bits {mask:X2}" : "")}) read as {said}, and Mutagen's value is {leaf.Text}: the layout worked out isn't the one Mutagen read");
        return new ItemPlace
        {
            Located = true, Subrecord = sub, Offset = offset, Size = size, Where = string.Join("; ", _where), Reads = reads,
            Checked = $"checked: the bytes read {said}, Mutagen's value",
        };
    }

    /// <summary>The slice as its kind: (true, …) when it is Mutagen's value, (false, …) when it isn't, (null, …) when the kind can't be checked.</summary>
    (bool? Ok, string? Reads, string? Said) Decode(FieldDef def, string? enumName, ReadOnlySpan<byte> b, JsonNode? json)
    {
        if (def.Kind == "Enum" && json is JsonArray flags && _schema.Enum(enumName) is { } fm)
        {
            // Flags: the JSON lists the names of the bits that are set
            ulong want = 0;
            foreach (var f in flags)
            {
                string n = f?.GetValue<JsonElement>().ToString() ?? "";
                var m = fm.FirstOrDefault(x => x.Name == n);
                if (m != null) want |= (ulong)m.Value;
                else if (ulong.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong bit)) want |= bit;
                else return (null, null, $"a flag named {n} that the enum doesn't have");
            }
            ulong have = Unsigned(b);
            var named = fm.Where(m => m.Value != 0 && (have & (ulong)m.Value) == (ulong)m.Value).Select(m => m.Name).ToList();
            return (have == want, null, $"0x{have:X} ({(named.Count == 0 ? "none" : string.Join(", ", named))})");
        }
        if (def.Kind == "String" && json is JsonObject text && text.ContainsKey("TargetLanguage"))
        {
            // A translated text. Mutagen's value is the text in the language read (TargetLanguage): one "Value", or
            // one of the "Values" of every language a localized plugin's strings files have
            string target = text["TargetLanguage"]?.GetValue<string>() ?? "";
            string? want = text["Value"] is JsonValue tv && tv.TryGetValue<string>(out var one) ? one
                : text["Values"] is JsonArray all
                    ? all.OfType<JsonObject>().FirstOrDefault(v => string.Equals(v["Language"]?.GetValue<string>(), target, StringComparison.OrdinalIgnoreCase))?["String"]?.GetValue<string>()
                    : null;
            if (_textOf != null)
            {
                // A localized plugin: the subrecord holds a 4-byte string ID, and the text is in the plugin's strings
                // files. The ID is looked up the way Mutagen's read looked it up, and the text must be Mutagen's.
                if (b.Length != 4) return (null, null, $"a text of a localized plugin in {b.Length} bytes, where a 4-byte string ID was expected");
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(b);
                if (!System.Enum.TryParse<Language>(target, true, out var language)) return (null, null, $"string ID {id:X8}, in a language Mutagen doesn't know ({target})");
                if (want == null) return (null, null, $"string ID {id:X8}, and Mutagen has no {target} text for it");
                if (id == 0) return (want.Length == 0, null, "string ID 00000000 (no text)");
                // The one table the field is read from, as the schema has it from the XML (translated="Normal|DL|IL")
                StringsSource? table = System.Enum.TryParse<StringsSource>(def.StringsTable, out var t) ? t : null;
                var held = _textOf(id, language, table);
                if (held.Count == 0) return (false, null, $"string ID {id:X8}, which {(table is { } tb ? $"the plugin's {target} {StringsExtension(tb)} file doesn't hold" : $"none of the plugin's {target} strings files holds")}");
                var same = held.Where(h => h.Text == want).ToList();
                var shown = same.Count > 0 ? same : held;
                string files = string.Join(" and ", shown.Select(h => h.File));
                string reads = $"string ID {id:X8}: \"{CardBuilder.NoTag(shown[0].Text)}\" in {files}";
                return (same.Count > 0, reads, same.Count > 0 ? $"string ID {id:X8} (its {target} text in {files})" : reads);
            }
            if (want == null) return (null, null, $"a translated text with no {target} text stored in the plugin");
            json = JsonNode.Parse(JsonSerializer.Serialize(want));
        }
        if (json is not JsonValue jv) return (null, null, null);
        var el = jv.GetValue<JsonElement>();
        string? note = def.Note;
        if (note != null && (note.Contains("divided by", StringComparison.Ordinal) || note.Contains("multiplied by", StringComparison.Ordinal))) return (null, null, "the file stores it scaled");
        switch (def.Kind)
        {
            case "Color" when b.Length == 4 && el.ValueKind == JsonValueKind.String:
            {
                // R G B A in the file; Mutagen's JSON writes #AARRGGBB
                string color = $"#{b[3]:X2}{b[0]:X2}{b[1]:X2}{b[2]:X2}";
                return (string.Equals(el.GetString(), color, StringComparison.OrdinalIgnoreCase), null, $"R G B A {color}");
            }
            case "FormLink" or "FormLinkOrIndex" when b.Length == 4 && el.ValueKind == JsonValueKind.String:
            {
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(b);
                string key = el.GetString()!;
                uint? want = key == "Null" ? 0u : _formIdOf?.Invoke(key);
                string reads = _label(id);
                if (want == null) return (null, reads, $"FormID {id:X8}");
                return (want == id, id == 0 ? null : reads, id == 0 ? "a null link" : $"FormID {id:X8} → {reads}");
            }
            case "Int8" or "Int16" or "Int32" or "Int64" when el.ValueKind == JsonValueKind.Number:
            {
                long v = b.Length switch { 1 => (sbyte)b[0], 2 => BinaryPrimitives.ReadInt16LittleEndian(b), 4 => BinaryPrimitives.ReadInt32LittleEndian(b), 8 => BinaryPrimitives.ReadInt64LittleEndian(b), _ => long.MinValue };
                if (v == long.MinValue && b.Length != 8) return (null, null, null);
                return (el.TryGetInt64(out long j) && j == v, null, v.ToString(CultureInfo.InvariantCulture));
            }
            case "UInt8" or "UInt16" or "UInt32" or "UInt64" when el.ValueKind == JsonValueKind.Number:
            {
                ulong v = Unsigned(b);
                return (el.TryGetUInt64(out ulong j) && j == v, null, v.ToString(CultureInfo.InvariantCulture));
            }
            case "Float" when b.Length == 4 && el.ValueKind == JsonValueKind.Number:
            {
                float v = BinaryPrimitives.ReadSingleLittleEndian(b);
                return (el.GetSingle().Equals(v), null, v.ToString("R", CultureInfo.InvariantCulture));
            }
            case "Double" when b.Length == 8 && el.ValueKind == JsonValueKind.Number:
            {
                double v = BinaryPrimitives.ReadDoubleLittleEndian(b);
                return (el.GetDouble().Equals(v), null, v.ToString("R", CultureInfo.InvariantCulture));
            }
            case "P3Float" when b.Length == 12 && el.ValueKind == JsonValueKind.String:
            {
                // Three floats; Mutagen's JSON writes them "x, y, z", each as float.ToString("R")
                var parts = el.GetString()!.Split(',', StringSplitOptions.TrimEntries);
                var have = new[] { BinaryPrimitives.ReadSingleLittleEndian(b), BinaryPrimitives.ReadSingleLittleEndian(b[4..]), BinaryPrimitives.ReadSingleLittleEndian(b[8..]) };
                bool same = parts.Length == 3 && parts.Zip(have).All(x => float.TryParse(x.First, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && f.Equals(x.Second));
                return (same, null, string.Join(", ", have.Select(f => f.ToString("R", CultureInfo.InvariantCulture))));
            }
            case "P3Int16" when b.Length == 6 && el.ValueKind == JsonValueKind.String:
            {
                // Three int16; Mutagen's JSON writes them "x, y, z"
                var parts = el.GetString()!.Split(',', StringSplitOptions.TrimEntries);
                var have = new[] { BinaryPrimitives.ReadInt16LittleEndian(b), BinaryPrimitives.ReadInt16LittleEndian(b[2..]), BinaryPrimitives.ReadInt16LittleEndian(b[4..]) };
                bool same = parts.Length == 3 && parts.Zip(have).All(x => short.TryParse(x.First, NumberStyles.Integer, CultureInfo.InvariantCulture, out short v) && v == x.Second);
                return (same, null, string.Join(", ", have.Select(v => v.ToString(CultureInfo.InvariantCulture))));
            }
            case "RecordType" when b.Length == 4 && el.ValueKind == JsonValueKind.String:
            {
                string chars = Encoding.Latin1.GetString(b);
                return (chars == el.GetString(), null, "\"" + chars + "\"");
            }
            case "Half" when b.Length == 2:
            {
                // Held bit for bit, as the serializer keeps it (C222): the 16 bits, NaN payloads and -0 included
                System.Half v = BinaryPrimitives.ReadHalfLittleEndian(b);
                bool same = FieldLookup.TryReadHalf(el, out System.Half j) && BitConverter.HalfToUInt16Bits(j) == BitConverter.HalfToUInt16Bits(v);
                return (same, null, System.Half.IsNaN(v) || System.Half.IsInfinity(v) ? v.ToString(CultureInfo.InvariantCulture) : ((double)v).ToString("R", CultureInfo.InvariantCulture));
            }
            case "Percent" when el.ValueKind == JsonValueKind.Number:
            {
                double v;
                if (b.Length == 1 && note != null && note.Contains("ByteHundred", StringComparison.Ordinal)) v = b[0] / 100.0;
                else if (b.Length == 4 && (note == null || !note.Contains("stored as", StringComparison.Ordinal))) v = BinaryPrimitives.ReadSingleLittleEndian(b);
                else return (null, null, null);
                return (Math.Abs(el.GetDouble() - v) < 1e-6, null, v.ToString("R", CultureInfo.InvariantCulture));
            }
            case "Bool" when el.ValueKind is JsonValueKind.True or JsonValueKind.False:
            {
                bool v = false;
                foreach (var x in b) v |= x != 0;
                return (el.GetBoolean() == v, null, v ? "true" : "false");
            }
            case "Enum":
            {
                var members = _schema.Enum(enumName);
                ulong v = Unsigned(b);
                if (members == null) return (null, null, $"{v} (the enum isn't known)");
                string said = members.FirstOrDefault(m => (ulong)m.Value == v)?.Name ?? v.ToString(CultureInfo.InvariantCulture);
                if (el.ValueKind == JsonValueKind.String)
                {
                    string s = el.GetString()!;
                    var m = members.FirstOrDefault(x => x.Name == s);
                    if (m != null) return ((ulong)m.Value == v, null, said);
                    return (ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong n) ? n == v : null, null, said);
                }
                if (el.ValueKind == JsonValueKind.Number) return (el.TryGetUInt64(out ulong n) && n == v, null, said);
                return (null, null, said);
            }
            case "ByteArray" when el.ValueKind == JsonValueKind.String:
            {
                string hex = el.GetString()!;
                if (!hex.StartsWith("0x", StringComparison.Ordinal)) return (null, null, null);
                byte[] want;
                try { want = Convert.FromHexString(hex.AsSpan(2)); } catch (FormatException) { return (null, null, null); }
                return (b.SequenceEqual(want), null, $"{b.Length} bytes{(b.SequenceEqual(want) ? ", every one equal to" : ", not those of")} Mutagen's {want.Length}");
            }
            case "String" or "AssetLink" when el.ValueKind == JsonValueKind.String:
            {
                var body = b.Length > 0 && b[^1] == 0 ? b[..^1] : b;
                string s = el.GetString()!;
                bool same = body.SequenceEqual(Encoding.UTF8.GetBytes(s)) || body.SequenceEqual(Encoding.Latin1.GetBytes(s));
                // A 4-byte text of an object-mod property: Mutagen stops it at the first zero
                if (!same) { int z = body.IndexOf((byte)0); if (z >= 0) same = body[..z].SequenceEqual(Encoding.Latin1.GetBytes(s)); }
                return (same, null, "\"" + Encoding.Latin1.GetString(body).TrimEnd('\0') + "\"");
            }
        }
        return (null, null, null);
    }

    static string StringsExtension(StringsSource s) => s switch { StringsSource.DL => ".dlstrings", StringsSource.IL => ".ilstrings", _ => ".strings" };

    static ulong Unsigned(ReadOnlySpan<byte> b) => b.Length switch
    {
        1 => b[0], 2 => BinaryPrimitives.ReadUInt16LittleEndian(b), 4 => BinaryPrimitives.ReadUInt32LittleEndian(b), 8 => BinaryPrimitives.ReadUInt64LittleEndian(b), _ => 0,
    };

    bool IsCondition(string obj) => _m.Ancestry(obj).Any(o => o.Name == "Condition");

    /// <summary>The subrecords a perk effect holds, from its hand-read layout (PERK's Segments PRKE … PRKF: the
    /// subrecords it describes and the generated ones it names).</summary>
    HashSet<string> PerkEffectSigs => _t.PerkEffectSigs ??= HandLayoutWalk.SegmentSigsOf(_m, "PERK", "PRKE")
        ?? throw Fail("the schema has no hand-read layout of a perk's effects (PERK in HandReadLayouts.xml)");
    /// <summary>A dictionary's key as a field row, from the dictionary's own ("keyed by Int8", "keyed by Enum BipedObject").</summary>
    internal static FieldDef? KeyDef(FieldDef dict)
    {
        var m = dict.Note == null ? null : Regex.Match(dict.Note, @"keyed by (Enum (\w+)|(\w+))");
        if (m is not { Success: true }) return null;
        string kind = m.Groups[2].Success ? "Enum" : m.Groups[3].Value;
        return new FieldDef
        {
            Object = dict.Object, Path = dict.Path + "[*].Key", Name = "Key", Kind = kind, Enum = m.Groups[2].Success ? m.Groups[2].Value : null,
            Size = kind switch { "Int8" or "UInt8" => 1, "Int16" or "UInt16" => 2, "Int32" or "UInt32" or "Enum" => 4, _ => null },
        };
    }

    // ------------------------------------------------------------------ Which field of an object each subrecord is
    internal enum GroupKind { Single, Marker, StructObj, MultiObj, ListArray, ListSub, ListMulti, InCustom, PerkEffects, GenderedSplit, GenderedMarkers, GenderedOne, Other }

    /// <summary>One property of an object (its rows: the property, its item) and the subrecords it can take.</summary>
    internal sealed class Group
    {
        public required string Name;
        public required FieldDef Main;
        public FieldDef? Item;
        public ObjDef? ItemObject;
        public GroupKind Kind;
        public string? Own, Counter, ItemSig;
        public string? Ref;
        public string? Map;
        /// <summary>The field's own markers (the XML's markerType and endMarkerType on the field): empty subrecords
        /// written before and after it.</summary>
        public string? Marker, EndMarker;
        /// <summary>A gendered value: its two rows, and the markers before each half when it has them.</summary>
        public FieldDef? Male, Female;
        public string? MaleMarker, FemaleMarker;
        /// <summary>A gendered value whose halves are objects, each after its marker subrecord (MNAM, FNAM), as Mutagen's
        /// GenderedItemBinaryOverlay.FactorySkipMarkersPreRead reads them (a race's body data): the gendered group. On a
        /// parsing step that fills such a value from its markers (a race's MNAMLogic fills SkeletalModel), that value's group.</summary>
        public Group? Halves;
        public HashSet<string> Sigs = new(StringComparer.Ordinal);
    }


    static readonly Regex Counted = new(@"counted by ([A-Z0-9_]{4})", RegexOptions.Compiled);


    /// <summary>What the schema says of an object's fields and subrecords, worked out once: one per record read for
    /// evidence, or kept from record to record by one thread's link walk (LinkWalk.cs).</summary>
    internal sealed class Tables
    {
        public readonly Dictionary<(string, string?), List<Group>> Groups = new();
        public readonly Dictionary<(string, string?), HashSet<string>> ObjSigs = new();
        public readonly Dictionary<(string, string?), HashSet<string>> Triggers = new();
        public HashSet<string>? Counters, PerkEffectSigs;
        /// <summary>For an object's groups (by the list itself): the groups that accept each signature, in order.</summary>
        public readonly Dictionary<List<Group>, Dictionary<string, int[]>> Accepting = new(ReferenceEqualityComparer.Instance);
        /// <summary>For an object's groups: the signatures several of its fields each take whole.</summary>
        public readonly Dictionary<List<Group>, HashSet<string>> Repeated = new(ReferenceEqualityComparer.Instance);
    }

    readonly Tables _t;
    readonly HashSet<(string, string?)> _visiting = new();
    readonly HashSet<(string, string?)> _visitingTriggers = new();

    /// <summary>The object's end marker, from the schema (the XML's endMarkerType, its own or a base's).</summary>
    string? EndMarker(string obj) => _m.EndMarkerOf(obj);
    /// <summary>The object's start marker, from the schema (the XML's markerType, its own or a base's).</summary>
    string? StartMarker(string obj) => _m.MarkerOf(obj);

    /// <summary>
    /// The subrecords that follow an item's own subrecord as part of it: the other subrecords its object holds, as the
    /// XML lists them (AdditionalContainedRecordType), less any list's counter (a condition holds CITC, CIS1 and CIS2;
    /// CITC counts the list, CIS1 and CIS2 follow the CTDA). Only for an item that is one subrecord of its own. That
    /// they follow the item is how Mutagen's hand-written condition reader takes them; the XML doesn't say where they
    /// stand (GAPS Part 2).
    /// </summary>
    string[] TrailingOf(string obj)
    {
        if (!_m.Objects.TryGetValue(obj, out var od) || od.ObjType != "Subrecord" || OwnSig(obj) is not { } own) return Array.Empty<string>();
        _t.Counters ??= _m.Objects.Values.SelectMany(o => o.Fields).Select(f => f.Note != null ? Counted.Match(f.Note) : null)
            .Where(x => x is { Success: true }).Select(x => x!.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        return _m.ContainsOf(obj).Where(s => s != own && !_t.Counters.Contains(s)).ToArray();
    }
    string? OwnSig(string obj) => _m.Ancestry(obj).Select(o => o.Sig).FirstOrDefault(x => x != null);

    List<Group> Groups(string obj, string? map)
    {
        if (_t.Groups.TryGetValue((obj, map), out var have)) return have;
        var list = new List<Group>();
        var byName = new Dictionary<string, Group>(StringComparer.Ordinal);
        foreach (var (f, _) in _m.AllFields(obj))
        {
            if (f.Kind == "Break") continue;
            if (!byName.TryGetValue(f.Name, out var g))
            {
                if (f.Path != f.Name) continue;
                g = new Group { Name = f.Name, Main = f, Map = f.SigMap ?? map };
                byName[f.Name] = g; list.Add(g);
                continue;
            }
            if (f.Path == f.Name + "[*]") g.Item = f;
            else
            {
                if (f.Path == f.Name + ".Male") g.Male = f;
                else if (f.Path == f.Name + ".Female") g.Female = f;
                foreach (var s in RowSigs(f, g.Map)) g.Sigs.Add(s);
            }
        }
        var customSigs = list.Where(g => g.Main.Kind == "CustomLogic" && g.Main.Subrecord != null).Select(g => g.Main.Subrecord!).ToHashSet(StringComparer.Ordinal);
        foreach (var g in list) Classify(g, customSigs);
        foreach (var g in list)
        {
            // Halves that are objects after markers: the gendered value itself, or the parsing step that fills one from
            // the markers it reads (its CustomFills names it)
            if (g.Main.Kind == "Gendered" && g.MaleMarker != null && g.Male?.RefObject != null && g.Female?.RefObject != null) g.Halves = g;
            else if (g.Main.Kind == "CustomLogic" && g.Main.CustomFills is { } fills && g.Own != null
                && list.FirstOrDefault(x => x.Name == fills) is { Main.Kind: "Gendered", Male.RefObject: not null, Female.RefObject: not null } filled
                && filled.Main.CustomSubrecordList is { Count: 2 } markers && markers.Contains(g.Own))
            {
                filled.MaleMarker ??= markers[0]; filled.FemaleMarker ??= markers[1];
                g.Halves = filled;
            }
        }
        _t.Groups[(obj, map)] = list;
        return list;
    }

    void Classify(Group g, HashSet<string> customSigs)
    {
        var f = g.Main;
        string? own = SchemaModel.MapSig(g.Map, f.Subrecord);
        // A perk rank's effects: read by hand-written code from PRKE to PRKF (PerkRankBinaryCreateTranslation.ParseEffects)
        if (f.Custom && f.Kind == "List" && g.Item?.RefObject == "APerkEffect") { g.Kind = GroupKind.PerkEffects; g.Sigs.Clear(); g.Sigs.UnionWith(PerkEffectSigs); return; }
        // A list of conditions Mutagen reads by hand is still one CTDA (and its CIS1, CIS2) per item, in the record's own
        // subrecords: cut like any list (a scene phase's start and completion conditions, the latter between two NEXT)
        bool conditions = f.Kind == "List" && g.Item?.RefObject is { } ro0 && IsCondition(ro0);
        if (f.Custom && !conditions && f.Kind != "CustomLogic" && (f.Subrecord == null || customSigs.Contains(f.Subrecord))) { g.Kind = GroupKind.InCustom; g.Sigs.Clear(); return; }
        // A parsing step of its own subrecords (a terminal menu item's TargetParse: UNAM, TNAM, BNAM): Mutagen's generated
        // reader hands them to it, so they are the object's (an item doesn't end at them)
        if (f.Kind == "CustomLogic" && f.Subrecord == null && f.CustomSubrecordList.Count > 0) { g.Kind = GroupKind.Other; g.Sigs.Clear(); g.Sigs.UnionWith(f.CustomSubrecordList); return; }
        g.Marker = f.Marker; g.EndMarker = f.EndMarker;
        if (g.Marker != null) g.Sigs.Add(g.Marker);
        if (g.EndMarker != null) g.Sigs.Add(g.EndMarker);
        if (f.Note != null && Counted.Match(f.Note) is { Success: true } cm) g.Counter = cm.Groups[1].Value;
        if (g.Counter != null) g.Sigs.Add(g.Counter);
        if (f.Kind == "Marker") { g.Kind = GroupKind.Marker; g.Own = own; }
        else if (f.Kind is "List" or "Array")
        {
            var item = g.Item;
            if (item == null) { g.Kind = GroupKind.Other; return; }
            string? itemMap = item.SigMap ?? g.Map;
            if (item.RefObject == null)
            {
                string? itemSig = SchemaModel.MapSig(itemMap, item.Subrecord);
                if (own != null && itemSig == own) { g.Kind = GroupKind.ListArray; g.Own = own; }
                else if (own == null && itemSig != null) { g.Kind = GroupKind.ListSub; g.ItemSig = itemSig; }
                else g.Kind = GroupKind.Other;
            }
            else if (_m.Objects.TryGetValue(item.RefObject, out var ro))
            {
                g.ItemObject = ro; g.Ref = ro.Name;
                string? roSig = SchemaModel.MapSig(itemMap, OwnSig(ro.Name));
                if (ro.Struct && own == null && roSig != null) { g.Kind = GroupKind.ListSub; g.ItemSig = roSig; }
                else if (ro.Struct && own != null) { g.Kind = GroupKind.ListArray; g.Own = own; }
                else if (!ro.Struct) g.Kind = GroupKind.ListMulti;
                else g.Kind = GroupKind.Other;
                foreach (var s in ObjSigs(ro.Name, itemMap)) g.Sigs.Add(s);
                // Items between markers of their own (the XML's itemMarkerType and itemEndMarkerType)
                if (g.Kind == GroupKind.ListMulti && f.ItemMarker != null) { g.Sigs.Add(f.ItemMarker); if (f.ItemEndMarker != null) g.Sigs.Add(f.ItemEndMarker); }
            }
            else g.Kind = GroupKind.Other;
            if (g.Own != null) g.Sigs.Add(g.Own);
            if (g.ItemSig != null) g.Sigs.Add(g.ItemSig);
            return;
        }
        else if (f.Kind == "Object" && f.RefObject != null && _m.Objects.TryGetValue(f.RefObject, out var ro))
        {
            g.Ref = ro.Name;
            if (ro.Struct) { g.Kind = GroupKind.StructObj; g.Own = own ?? SchemaModel.MapSig(g.Map, OwnSig(ro.Name)); }
            else g.Kind = GroupKind.MultiObj;
            foreach (var s in ObjSigs(ro.Name, g.Map)) g.Sigs.Add(s);
        }
        else if (f.Kind == "Gendered" && g.Male != null && g.Female != null && !f.Custom)
        {
            // How Mutagen's gendered reader takes the pair (GenderedItemBinaryTranslation):
            //  - after a marker each: each half is the content of its marker subrecord (a plain value);
            //  - in one subrecord of its own: the male value, then the female one;
            //  - otherwise each half is an object read with its own signatures (MaleTypeOverrides, FemaleTypeOverrides)
            // The XML's maleMarker/femaleMarker and genderEnumRecord (C57 W3; the rule is Code-Generation.md's)
            if (f.MaleMarker != null && f.FemaleMarker != null)
            {
                g.MaleMarker = f.MaleMarker; g.FemaleMarker = f.FemaleMarker;
                g.Sigs.Clear(); g.Sigs.Add(g.MaleMarker); g.Sigs.Add(g.FemaleMarker);
                g.Kind = g.Male.RefObject == null && g.Female.RefObject == null ? GroupKind.GenderedMarkers : GroupKind.Other;
            }
            else if (own != null) { g.Kind = GroupKind.GenderedOne; g.Own = own; }
            else if (g.Male.RefObject != null && g.Female.RefObject != null && f.GenderRecord == null) g.Kind = GroupKind.GenderedSplit;
            else g.Kind = GroupKind.Other;
        }
        else if (f.Kind is "Gendered" or "Dictionary" or "Array2D") g.Kind = GroupKind.Other;
        else { g.Kind = GroupKind.Single; g.Own = own; }
        if (g.Own != null) g.Sigs.Add(g.Own);
    }

    IEnumerable<string> RowSigs(FieldDef f, string? map)
    {
        string? m = f.SigMap ?? map;
        if (SchemaModel.MapSig(m, f.Subrecord) is { } s) yield return s;
        if (f.RefObject != null && _m.Objects.ContainsKey(f.RefObject))
            foreach (var x in ObjSigs(f.RefObject, m)) yield return x;
    }

    /// <summary>Every signature an object (or any kind of it) can hold.</summary>
    HashSet<string> ObjSigs(string obj, string? map)
    {
        if (_t.ObjSigs.TryGetValue((obj, map), out var have)) return have;
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!_visiting.Add((obj, map))) return set;
        try
        {
            foreach (var o in new[] { obj }.Concat(_m.Variants(obj)))
            {
                if (SchemaModel.MapSig(map, OwnSig(o)) is { } own) set.Add(own);
                if (StartMarker(o) is { } start) set.Add(start);
                if (EndMarker(o) is { } end) set.Add(end);
                foreach (var t in TrailingOf(o)) set.Add(t);
                foreach (var g in Groups(o, map)) set.UnionWith(g.Sigs);
            }
        }
        finally { _visiting.Remove((obj, map)); }
        return _t.ObjSigs[(obj, map)] = set;
    }

    /// <summary>The signatures an item of an object can start with.</summary>
    HashSet<string> Triggers(string obj, string? map)
    {
        if (_t.Triggers.TryGetValue((obj, map), out var have)) return have;
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!_visitingTriggers.Add((obj, map))) return set;
        if (_m.Objects.TryGetValue(obj, out var od) && od.Struct) { if (SchemaModel.MapSig(map, OwnSig(obj)) is { } s) set.Add(s); }
        // An item that reads its fields in any order (shortCircuitToLastRequired) starts at any of them, as its trigger
        // specs (all record types, no triggering ones) say
        else if (od?.ShortCircuit != null) set.UnionWith(ObjSigs(obj, map));
        else
            foreach (var o in new[] { obj }.Concat(_m.Variants(obj)))
            {
                // An object with a start marker starts with it
                if (StartMarker(o) is { } start) { set.Add(start); continue; }
                bool marker = EndMarker(o) != null;
                foreach (var g in Groups(o, map))
                {
                    if (g.Sigs.Count == 0) continue;
                    if (g.Marker != null) set.Add(g.Marker);
                    else if (g.Kind == GroupKind.ListMulti && g.Main.ItemMarker != null) set.Add(g.Main.ItemMarker);
                    else if (g.Kind is GroupKind.ListMulti or GroupKind.MultiObj && g.Ref != null) set.UnionWith(g.Counter != null ? new[] { g.Counter } : Triggers(g.Ref, g.Map));
                    else set.UnionWith(g.Sigs);
                    // An object with an end marker starts with its first subrecord; any other with its first one that is always there
                    if (marker || (!g.Main.Nullable && !(g.Main.Kind == "Bool" && g.Main.Size == 0))) break;
                }
            }
        _visitingTriggers.Remove((obj, map));
        return _t.Triggers[(obj, map)] = set;
    }

    List<Group>? _lastGroups;
    Dictionary<int, List<(int, int)>> _items = new();
    /// <summary>For a gendered value read in halves: the runs of subrecords each half was read from.</summary>
    Dictionary<int, (List<(int, int)> Male, List<(int, int)> Female)> _halves = new();

    /// <summary>Which property of the object takes each subrecord in [from, to): an index into <see cref="Groups"/>, or -1.</summary>
    int[] Claim(string obj, string? map, int from, int to)
    {
        var groups = Groups(obj, map);
        _lastGroups = groups;
        _items.Clear();
        _halves.Clear();
        var claims = new int[to - from];
        Array.Fill(claims, -1);
        int cur = -1, i = from;
        var done = new HashSet<string>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (i < to)
        {
            int g = Choose(groups, Sig(i), cur, done, AcceptingOf(groups, Sig(i)), counts);
            if (g < 0) { i++; continue; }
            counts[Sig(i)] = counts.GetValueOrDefault(Sig(i)) + 1;
            int end = Consume(groups[g], i, to, _items, g);
            Done(groups, g, done);
            for (int x = i; x < end; x++) claims[x - from] = g;
            cur = g; i = end;
        }
        return claims;
    }

    /// <summary>Notes that group <paramref name="g"/> took its subrecord: a plain field by its signature (the fields of
    /// one data block share it), a marker field by itself.</summary>
    static void Done(List<Group> groups, int g, HashSet<string> done)
    {
        var x = groups[g];
        if (x.Kind == GroupKind.Marker) done.Add("#" + g.ToString(CultureInfo.InvariantCulture));
        else if (x.Kind is GroupKind.Single or GroupKind.StructObj or GroupKind.GenderedOne && x.Own != null) done.Add(x.Own);
    }

    /// <param name="done">Signatures a plain field of this object has had: the fields of one data block share their
    /// subrecord, so a second one is another item's (or no field's), not the next field of the block.</param>
    /// <summary>The groups of an object's own list (<see cref="Groups"/>) that accept a signature, in order, worked out
    /// once per signature: <see cref="Claim"/> asks for each subrecord.</summary>
    int[] AcceptingOf(List<Group> groups, string s)
    {
        if (!_t.Accepting.TryGetValue(groups, out var bySig)) _t.Accepting[groups] = bySig = new Dictionary<string, int[]>(StringComparer.Ordinal);
        if (!bySig.TryGetValue(s, out var found))
        {
            var l = new List<int>();
            for (int g = 0; g < groups.Count; g++) if (Accepts(groups[g], s)) l.Add(g);
            bySig[s] = found = l.ToArray();
        }
        return found;
    }

    /// <param name="counts">How many subrecords of each signature the object's fields took so far (the record's own
    /// claim): of several fields that each take a whole subrecord of one signature, one that comes after none of them
    /// takes the next of them in order, as Mutagen's generated reader counts them (recordParseCount).</param>
    int Choose(List<Group> groups, string s, int cur, HashSet<string> done, int[]? accepting = null, Dictionary<string, int>? counts = null)
    {
        int first = -1;
        bool repeated = RepeatedSigs(groups).Contains(s);
        List<int>? candidates = repeated ? new List<int>() : null;
        for (int k = 0, n = accepting?.Length ?? groups.Count; k < n; k++)
        {
            int g = accepting != null ? accepting[k] : k;
            if (accepting == null && !Accepts(groups[g], s)) continue;
            // The fields of one data block share their subrecord: a second one is another item's (or no field's). Fields
            // that each take a whole subrecord of the signature (a scene action's StartPhase and a timer's MaxSeconds,
            // both SNAM) take one each, in their order
            if (groups[g].Kind is GroupKind.Single or GroupKind.StructObj or GroupKind.GenderedOne && done.Contains(s) && !repeated) continue;
            // A marker field is taken once; another marker field with the same signature takes the next one (a scene
            // phase's StartMarker and EndMarker are both HNAM)
            if (groups[g].Kind == GroupKind.Marker && done.Contains("#" + g.ToString(CultureInfo.InvariantCulture))) continue;
            if (g > cur) return g;
            if (first < 0) first = g;
            candidates?.Add(g);
        }
        // Past all of them: the next by how many were taken (Mutagen's generated reader: recordParseCount)
        if (candidates is { Count: > 1 } && counts != null) return candidates[Math.Min(counts.GetValueOrDefault(s), candidates.Count - 1)];
        return first;
    }

    /// <summary>The signatures several of an object's fields each take whole (no data block: no field of them at an
    /// offset): a scene action's StartPhase and a timer action's MaxSeconds (SNAM), a surface tree's GNAM and GNAM2; and
    /// those several objects of one subrecord each take (a pack-in's ObjectPaletteDefaults and ObjectPlacementDefaults2,
    /// both OPDS), or an object and a plain field (a planet's OrbitedData and SurfaceTree, both FNAM). Mutagen's
    /// generated reader gives each such subrecord to the first of the fields after the last one it filled, and past
    /// them all to the next by how many it has had (Choose).</summary>
    HashSet<string> RepeatedSigs(List<Group> groups)
    {
        if (_t.Repeated.TryGetValue(groups, out var have)) return have;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var own in groups.Where(g => g.Kind is GroupKind.Single or GroupKind.StructObj && g.Own != null).GroupBy(g => g.Own!))
        {
            var list = own.ToList();
            var all = groups.Where(g => g.Own == own.Key).ToList();
            if (list.Count >= 2 && all.All(g => g.Main.Kind != "CustomLogic" && !g.Main.Custom
                    && g.Kind is GroupKind.StructObj or GroupKind.Single && (g.Main.Offset is null or 0) && !g.Main.OffsetRel))
                set.Add(own.Key);
        }
        return _t.Repeated[groups] = set;
    }

    bool Accepts(Group g, string s) => (g.Marker != null && s == g.Marker) || AcceptsBody(g, s);

    bool AcceptsBody(Group g, string s) => g.Kind switch
    {
        GroupKind.Single or GroupKind.Marker or GroupKind.StructObj => s == g.Own,
        GroupKind.ListArray => s == g.Own || s == g.Counter,
        GroupKind.ListSub => s == g.ItemSig || s == g.Counter,
        // (a list whose items sit between markers starts at an item's marker, as Mutagen's generated reader switches on it)
        GroupKind.ListMulti when g.Main.ItemMarker != null => s == g.Main.ItemMarker,
        GroupKind.ListMulti => s == g.Counter || (g.Ref != null && Triggers(g.Ref, g.Item?.SigMap ?? g.Map).Contains(s)),
        // An object of several subrecords starts only on a subrecord that can start it (its triggering types, as Mutagen's
        // generated reader switches on them): a CTDA after a response with no animation is the record's, not the animation's
        GroupKind.MultiObj => g.Ref != null ? Triggers(g.Ref, g.Map).Contains(s) : g.Sigs.Contains(s),
        GroupKind.PerkEffects => s == "PRKE",
        GroupKind.GenderedOne => s == g.Own,
        GroupKind.GenderedMarkers => s == g.MaleMarker || s == g.FemaleMarker,
        GroupKind.GenderedSplit => g.Sigs.Contains(s),
        GroupKind.Other => g.Sigs.Contains(s),
        _ => false,
    };

    /// <summary>Takes the subrecords a property reads from position <paramref name="i"/>, with its markers; gives where it stops.</summary>
    int Consume(Group g, int i, int to, Dictionary<int, List<(int, int)>> items, int gi)
    {
        if (g.Marker != null && Sig(i) == g.Marker)
        {
            // The field's start marker, then the field itself when it follows
            if (i + 1 >= to || !AcceptsBody(g, Sig(i + 1)))
            {
                // No body: the field's end marker may follow at once (a scene phase's empty completion conditions: NEXT NEXT)
                return g.EndMarker != null && i + 1 < to && Sig(i + 1) == g.EndMarker ? i + 2 : i + 1;
            }
            i++;
        }
        int end = ConsumeBody(g, i, to, items, gi);
        if (g.EndMarker != null && end < to && Sig(end) == g.EndMarker) end++;
        return end;
    }

    int ConsumeBody(Group g, int i, int to, Dictionary<int, List<(int, int)>> items, int gi)
    {
        if (g.Halves is { } hg)
        {
            // Up to two markers, each followed by its half, read by the half's object as far as it goes
            if (!_halves.TryGetValue(gi, out var hv)) _halves[gi] = hv = (new List<(int, int)>(), new List<(int, int)>());
            int p = i;
            for (int n = 0; n < 2 && p < to && (Sig(p) == hg.MaleMarker || Sig(p) == hg.FemaleMarker); n++)
            {
                bool male = Sig(p) == hg.MaleMarker;
                var part = male ? hg.Male! : hg.Female!;
                int s = p + 1, e = ParseItem(part.RefObject!, part.SigMap ?? hg.Map, s, to);
                if (e > s) (male ? hv.Male : hv.Female).Add((s, e));
                p = Math.Max(e, s);
            }
            return Math.Max(p, i + 1);
        }
        switch (g.Kind)
        {
            case GroupKind.ListArray:
                if (Sig(i) == g.Counter) return i + 1 < to && Sig(i + 1) == g.Own ? i + 2 : i + 1;
                return i + 1;
            case GroupKind.ListSub:
            {
                int p = i;
                if (Sig(p) == g.Counter) p++;
                var list = items.TryGetValue(gi, out var l) ? l : items[gi] = new();
                var trailing = g.Ref != null ? TrailingOf(g.Ref) : Array.Empty<string>();
                while (p < to && Sig(p) == g.ItemSig)
                {
                    int e = p + 1;
                    while (e < to && trailing.Contains(Sig(e))) e++;
                    list.Add((p, e)); p = e;
                }
                return Math.Max(p, i + 1);
            }
            case GroupKind.ListMulti:
            {
                int p = i;
                long? n = null;
                if (Sig(p) == g.Counter) { n = CounterValue(p); p++; }
                var list = items.TryGetValue(gi, out var l) ? l : items[gi] = new();
                string? itemMap = g.Item?.SigMap ?? g.Map;
                // Items between markers (Mutagen's ListBinaryTranslation.Parse with itemStartMarker and itemEndMarker):
                // while the next subrecord is the start marker it is stepped over, an item is read as far as it goes,
                // and the end marker is stepped over when it follows. An item is what lies between (a sound echo
                // marker's Markers, ECHO to ECHE; a perk's Ranks, PRRK to PRRF)
                if (g.Main.ItemMarker is { } startMarker)
                {
                    while (p < to && Sig(p) == startMarker)
                    {
                        int s = p + 1, e = Math.Max(ParseItem(g.Ref!, itemMap, s, to), s);
                        list.Add((s, e)); p = e;
                        if (g.Main.ItemEndMarker is { } endMarker && p < to && Sig(p) == endMarker) p++;
                    }
                    return Math.Max(p, i + 1);
                }
                var triggers = Triggers(g.Ref!, itemMap);
                while (p < to && (n != null ? list.Count < n : triggers.Contains(Sig(p))))
                {
                    int e = ParseItem(g.Ref!, itemMap, p, to);
                    if (e <= p) break;
                    list.Add((p, e)); p = e;
                }
                return Math.Max(p, i + 1);
            }
            case GroupKind.MultiObj:
                return Math.Max(ParseItem(g.Ref!, g.Map, i, to), i + 1);
            case GroupKind.GenderedMarkers:
            {
                // At most a male and a female marker subrecord, each holding its half
                int p = i;
                for (int n = 0; n < 2 && p < to && (Sig(p) == g.MaleMarker || Sig(p) == g.FemaleMarker); n++) p++;
                return Math.Max(p, i + 1);
            }
            case GroupKind.GenderedSplit:
                return Math.Max(ConsumeHalves(g, i, to, gi), i + 1);
            case GroupKind.PerkEffects:
            {
                // PerkRankBinaryCreateTranslation.ParseEffects: each effect is a PRKE, then what ReadPayload takes (any
                // of DATA, EPF2, EPF3, EPFD, EPFT, EPFB and the conditions' PRKC, CTDA, CIS1, CIS2), then an EPFT and a
                // PRKF when they are there
                var list = items.TryGetValue(gi, out var l) ? l : items[gi] = new();
                int p = i;
                while (p < to && Sig(p) == "PRKE")
                {
                    // An effect runs to its PRKF, or to the next PRKE or a subrecord no effect holds (the layout's Segments)
                    int e = p + 1;
                    while (e < to && Sig(e) != "PRKE" && PerkEffectSigs.Contains(Sig(e))) { e++; if (Sig(e - 1) == "PRKF") break; }
                    list.Add((p, e)); p = e;
                }
                return Math.Max(p, i + 1);
            }
            default:
                return i + 1;
        }
    }

    /// <summary>
    /// One call of Mutagen's reader of a gendered pair split by signatures (<c>GenderedItemBinaryTranslation.Parse</c>
    /// with record type converters, and <c>ParseHalf</c> as changed in fixes.12, C155/C156): the male half, then the
    /// female one. A half not read yet reads what follows (nothing, when what follows isn't its own); a half already
    /// read takes what follows when it starts with one of the half's own signatures, or, once both halves are read,
    /// whatever follows. The record's reader calls it again each time a signature of the pair comes up later.
    /// </summary>
    int ConsumeHalves(Group g, int p, int to, int gi)
    {
        if (!_halves.TryGetValue(gi, out var h)) _halves[gi] = h = (new List<(int, int)>(), new List<(int, int)>());
        p = ReadHalf(g.Male!, h.Male, h.Female, p, to);
        p = ReadHalf(g.Female!, h.Female, h.Male, p, to);
        return p;
    }

    int ReadHalf(FieldDef part, List<(int, int)> held, List<(int, int)> other, int p, int to)
    {
        if (p >= to) return p;
        // The half's own signatures: those its converter maps to (none when it has no converter)
        bool own = part.SigMap != null && part.SigMap.Split(',').Any(m => m.EndsWith("=" + Sig(p), StringComparison.Ordinal));
        if (held.Count == 0 || own || other.Count > 0)
        {
            int e = ParseItem(part.RefObject!, part.SigMap, p, to);
            if (e > p) { held.Add((p, e)); return e; }
        }
        return p;
    }

    long? CounterValue(int index)
    {
        var c = _raw.Content(_raw.Subrecords[index]);
        return c.Length switch { 1 => c[0], 2 => BinaryPrimitives.ReadUInt16LittleEndian(c), 4 => BinaryPrimitives.ReadUInt32LittleEndian(c), 8 => (long)BinaryPrimitives.ReadUInt64LittleEndian(c), _ => null };
    }

    /// <summary>Where one item of an object that is several subrecords ends, starting at <paramref name="p"/>.</summary>
    int ParseItem(string obj, string? map, int p, int to)
    {
        var sigs = ObjSigs(obj, map);
        // An item that starts with a marker (XWPK before a grouped pack-in) begins after it; its end marker may be the same signature
        if (StartMarker(obj) is { } start && p < to && Sig(p) == start) p++;
        if (EndMarker(obj) is { } end)
        {
            for (int j = p; j < to; j++)
            {
                if (Sig(j) == end) return j + 1;
                if (!sigs.Contains(Sig(j))) return j;
            }
            return to;
        }
        // Without an end marker: the item's fields in order; it ends at a subrecord it can't hold or a field it already had
        var groups = new List<Group>();
        foreach (var o in new[] { obj }.Concat(_m.Variants(obj))) groups.AddRange(Groups(o, map).Where(g => !groups.Any(x => x.Name == g.Name)));
        var scratch = new Dictionary<int, List<(int, int)>>();
        int cur = -1, i = p;
        var done = new HashSet<string>(StringComparer.Ordinal);
        // shortCircuitToLastRequired: the fields come in any order until the last one that is never null was read
        // (a field another field's reader fills, the XML's NoGeneration: a subgraph's Perspective, which Role's reader fills
        // from SRAF, is no step of the generated reader)
        var lastRequired = _m.Objects.TryGetValue(obj, out var od) && od.ShortCircuit != null ? groups.LastOrDefault(x => !x.Main.Nullable && x.Main.FilledBy == null) : null;
        bool pastLast = false;
        while (i < to)
        {
            string s = Sig(i);
            if (!sigs.Contains(s)) break;
            int g = Choose(groups, s, cur, done);
            if (g < 0 || (lastRequired != null ? pastLast : g <= cur)) break;
            var halvesWere = _halves;
            _halves = new();
            i = Consume(groups[g], i, to, scratch, g);
            _halves = halvesWere;
            Done(groups, g, done);
            if (groups[g] == lastRequired) pastLast = true;
            cur = g;
        }
        return i;
    }

    static string Ordinal(int n) => (n % 100) is 11 or 12 or 13 ? n + "th" : (n % 10) switch { 1 => n + "st", 2 => n + "nd", 3 => n + "rd", _ => n + "th" };
}
