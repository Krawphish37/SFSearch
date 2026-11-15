// Heavy records read in part (GAPS row S7, fix G2).
//
// A record of over 16 MB of data (Starfield.esm's navigation mesh info map, a NAVI of 51.4 MB) takes gigabytes as a
// whole card: Mutagen's objects, the JSON and the card are each many times the record's size. Most of such a record is
// one or two big fields (the NAVI's 56,631 NVMI subrecords). So a heavy record is read in part:
//  - Its bytes are read once (inflated once when compressed) and cut into subrecords, as the raw view does. Each
//    subrecord is given to the top-level field whose schema rows name its signature.
//  - A field whose subrecords hold more than a megabyte is big. A part read keeps every subrecord except those of the
//    big fields nobody asked for, and of a list asked for whose items are one subrecord each only the items of the
//    page. The kept subrecords, in their own order, make a small record (not compressed) that Mutagen's own binary
//    reader reads as if the plugin held it. JSON, card, fields and evidence then work on that small record as always.
//  - Nothing is guessed: a page read must give Mutagen exactly as many items as subrecords were kept, and evidence
//    counts its subrecords in the whole record. A record that can't be cut this way says so and why; the full read
//    (allow_heavy) is still there.
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using SFSearch.Index;

namespace SFSearch.Records;

/// <summary>The items of a list read in part: their indexes in the whole list (sorted), of <see cref="Total"/>.</summary>
public sealed record ListPage(string Field, string Signature, int[] Items, int Total)
{
    public int Count => Items.Length;
    /// <summary>Where item <paramref name="index"/> of the whole list is among the items read, or -1.</summary>
    public int Place(int index) { int i = Array.BinarySearch(Items, index); return i < 0 ? -1 : i; }
}

/// <summary>One top-level field of a heavy record and the subrecords that hold it.</summary>
public sealed class HeavyField
{
    public required string Name { get; init; }
    public required string Readable { get; init; }
    public required string Kind { get; init; }
    public List<string> Signatures { get; } = new();
    public int Subrecords { get; internal set; }
    /// <summary>The bytes of its subrecords, their 6-byte headers included.</summary>
    public long Bytes { get; internal set; }
    /// <summary>Set when the field is a list whose items are one subrecord each: that signature. Such a list can be paged.</summary>
    public string? ItemSignature { get; internal set; }
    public int Items { get; internal set; }
    /// <summary>True when no schema row names the field's signature: it was taken to be the field's as the only
    /// signature left and the only list left (a list Mutagen reads with hand-written code). A part read checks it.</summary>
    public bool Guessed { get; internal set; }
    /// <summary>True when the field's subrecords are more than <see cref="RecordEngine.HeavyPartFieldBytes"/>: left out unless asked for.</summary>
    public bool Big { get; internal set; }
}

/// <summary>The subrecords of one signature in a heavy record.</summary>
public sealed record SignatureCount(string Signature, int Count, long Bytes, int Smallest, int Largest, string? Field);

/// <summary>A heavy record as its bytes lay it out: its subrecords by signature and by top-level field.</summary>
public sealed class HeavyLayout
{
    public required RecordRef Ref { get; init; }
    public required RecordInfo Info { get; init; }
    public required string Type { get; init; }
    public required RawRecord Raw { get; init; }
    /// <summary>The fields that have subrecords in this record, in the order of their first subrecord.</summary>
    public required List<HeavyField> Fields { get; init; }
    /// <summary>Every signature of the record, in the order of its first subrecord.</summary>
    public required List<SignatureCount> Signatures { get; init; }
    /// <summary>For each subrecord of <see cref="RawRecord.Subrecords"/>: the field it belongs to, or null when it belongs
    /// to no field the schema knows or to several (it is then always kept).</summary>
    public required string?[] Owner { get; init; }
    public long DataBytes => Raw.Data.Length;

    public HeavyField? Field(string name) =>
        Fields.FirstOrDefault(f => f.Name == name) ?? Fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? Fields.FirstOrDefault(f => f.Readable.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The subrecords as a table: signature, count, bytes, smallest, largest, field.</summary>
    public List<string> SubrecordTable()
    {
        var lines = new List<string>
        {
            "| Signature | Count | Bytes | Smallest | Largest | Field |",
            "| :--- | ---: | ---: | ---: | ---: | :--- |",
        };
        foreach (var s in Signatures)
            lines.Add($"| {s.Signature} | {N(s.Count)} | {N(s.Bytes)} | {N(s.Smallest)} | {N(s.Largest)} | {s.Field ?? "(no field of the schema, or several: always read)"} |");
        return lines;
    }

    internal static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>What a part read gave: Mutagen's read of the record cut down to the subrecords kept.</summary>
public sealed class PartRead
{
    public required HeavyLayout Layout { get; init; }
    /// <summary>The record as Mutagen read it from the kept subrecords.</summary>
    public required LoadedRecord Record { get; init; }
    /// <summary>The kept subrecords as a record of their own (the bytes Mutagen read).</summary>
    public required RawRecord PartRaw { get; init; }
    public required Dictionary<string, ListPage> Pages { get; init; }
    /// <summary>The big fields left out of this read.</summary>
    public required List<HeavyField> Left { get; init; }
    public int KeptSubrecords { get; init; }
    public long KeptBytes { get; init; }
}

public sealed partial class RecordEngine
{
    /// <summary>A top-level field of a heavy record whose subrecords hold more than this is big: a part read leaves it
    /// out unless it is asked for.</summary>
    public long HeavyPartFieldBytes { get; set; } = 1 << 20;

    /// <summary>True when the record has more data than <see cref="HeavyRecordBytes"/>.</summary>
    public bool IsHeavy(RecordRef r) => DataBytes(r) > HeavyRecordBytes;

    /// <summary>
    /// A heavy record's bytes, read once and cut into subrecords, each given to the top-level field the schema says
    /// holds it. Nothing is read through Mutagen.
    /// </summary>
    public HeavyLayout Layout(RecordRef r)
    {
        var info = Index.Describe(r);
        if (info.Unresolved) throw new RecordException($"The record at 0x{info.Offset:X} of {info.Plugin} has a FormID that can't be resolved: a master of {info.Plugin} is missing.");
        string type = TypeFor(info.Signature);
        if (Schema.TypesOf(info.Signature).Count != 1)
            throw new RecordException($"{info.Label} is a {info.Signature}, a signature Mutagen reads as several types ({string.Join(", ", Schema.TypesOf(info.Signature))}); a part of it can't be read on its own.");
        if (Schema.HoldsRecords(type))
            throw new RecordException($"{info.Label} is a {info.Signature}, which holds other records; a part of it isn't read on its own.");
        var raw = Raw(r);

        // Which top-level field each signature belongs to, from the schema's rows of the type
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Own(string? sig, string field)
        {
            if (string.IsNullOrEmpty(sig)) return;
            (owners.TryGetValue(sig, out var set) ? set : owners[sig] = new HashSet<string>(StringComparer.Ordinal)).Add(field);
        }
        foreach (var row in Schema.Model.Expand(type))
        {
            string top = Top(row.Path);
            Own(row.Subrecord, top);
            if (row.Def.RefObject != null && Schema.Model.Objects.TryGetValue(row.Def.RefObject, out var o)) Own(o.Sig, top);
        }

        // A list read by Mutagen's hand-written code can have no schema row that names its subrecords (a PERS's Items,
        // one DAT2 subrecord an item). When the type has exactly one such list, and the record exactly one signature no
        // row names, that signature is taken to be the list's. Taken, not known: the part read checks that Mutagen
        // reads as many items as subrecords were given to it.
        var tops = new Dictionary<string, bool>(StringComparer.Ordinal);   // top-level field → some row names a subrecord
        foreach (var row in Schema.Model.Expand(type))
        {
            string top = Top(row.Path);
            bool named = row.Subrecord != null || row.Def.RefObject != null && Schema.Model.Objects.TryGetValue(row.Def.RefObject, out var ro) && ro.Sig != null;
            tops[top] = tops.GetValueOrDefault(top) || named;
        }
        var unnamedLists = Schema.Props(type).Where(p => p.Main.Kind is "List" or "Array" && p.Item != null && tops.TryGetValue(p.Name, out bool n) && !n).Select(p => p.Name).ToList();
        var unnamedSigs = raw.Subrecords.Select(x => x.Signature).Where(x => !owners.ContainsKey(x)).Distinct(StringComparer.Ordinal).ToList();
        string? guessedList = null;
        if (unnamedLists.Count == 1 && unnamedSigs.Count == 1) { guessedList = unnamedLists[0]; Own(unnamedSigs[0], guessedList); }

        var fields = new List<HeavyField>();
        var byName = new Dictionary<string, HeavyField>(StringComparer.Ordinal);
        var owner = new string?[raw.Subrecords.Count];
        var sigs = new List<string>();
        var counts = new Dictionary<string, (int Count, long Bytes, int Min, int Max)>(StringComparer.Ordinal);
        for (int i = 0; i < raw.Subrecords.Count; i++)
        {
            var s = raw.Subrecords[i];
            long bytes = 6 + s.Length + (s.LongForm ? 10 : 0);
            if (!counts.TryGetValue(s.Signature, out var c)) { sigs.Add(s.Signature); c = (0, 0, int.MaxValue, 0); }
            counts[s.Signature] = (c.Count + 1, c.Bytes + bytes, Math.Min(c.Min, s.Length), Math.Max(c.Max, s.Length));
            if (!owners.TryGetValue(s.Signature, out var set) || set.Count != 1) continue;
            string name = set.First();
            owner[i] = name;
            if (!byName.TryGetValue(name, out var f))
            {
                var prop = Schema.Prop(type, name);
                f = new HeavyField
                {
                    Name = name, Kind = prop?.Main.Kind ?? "",
                    Readable = (prop == null ? null : Schema.ReadablePair(info.Signature, prop.Main.Path)?.Trim() ?? prop.Main.Readable?.Trim()) is { Length: > 0 } rd ? rd : CardBuilder.Spaced(name),
                };
                byName[name] = f; fields.Add(f);
            }
            if (!f.Signatures.Contains(s.Signature)) f.Signatures.Add(s.Signature);
            f.Subrecords++; f.Bytes += bytes;
        }
        foreach (var f in fields)
        {
            f.Big = f.Bytes > HeavyPartFieldBytes;
            // A list whose items are one subrecord each: the item's own signature is the field's only one
            var prop = Schema.Prop(type, f.Name);
            if (prop?.Item is { } item && f.Kind is "List" or "Array" && f.Signatures.Count == 1)
            {
                string? itemSig = item.Subrecord ?? (item.RefObject != null && Schema.Model.Objects.TryGetValue(item.RefObject, out var io) ? io.Sig : null);
                if (itemSig == f.Signatures[0] || f.Name == guessedList) { f.ItemSignature = f.Signatures[0]; f.Items = f.Subrecords; f.Guessed = f.Name == guessedList; }
            }
        }
        return new HeavyLayout
        {
            Ref = r, Info = info, Type = type, Raw = raw, Fields = fields, Owner = owner,
            Signatures = sigs.Select(s => { var c = counts[s]; return new SignatureCount(s, c.Count, c.Bytes, c.Min, c.Max, owners.TryGetValue(s, out var o) && o.Count == 1 ? o.First() : null); }).ToList(),
        };
    }

    /// <summary>The top-level field of a schema path: <c>MapInfos[*].NavigationMesh</c> → <c>MapInfos</c>.</summary>
    internal static string Top(string path)
    {
        int end = path.IndexOfAny(new[] { '.', '[', '{' });
        return end < 0 ? path : path[..end];
    }

    /// <summary>
    /// Reads a heavy record in part through Mutagen: every subrecord except those of the big fields not named in
    /// <paramref name="asked"/>. A named field with a range (a list whose items are one subrecord each) keeps only the
    /// items of that range. The plugin's session must be current (<see cref="PluginSessions.Use"/>).
    /// </summary>
    /// <param name="asked">Top-level field names; the value is the indexes of the items to keep (null: the whole field).</param>
    public PartRead ReadPart(HeavyLayout layout, IReadOnlyDictionary<string, IReadOnlyCollection<int>?> asked)
    {
        var raw = layout.Raw;
        var pages = new Dictionary<string, ListPage>(StringComparer.Ordinal);
        foreach (var (name, range) in asked)
        {
            var f = layout.Fields.FirstOrDefault(x => x.Name == name) ?? throw new RecordException($"{layout.Info.Label} has no subrecord of a field {name}.");
            if (range == null) continue;
            if (f.ItemSignature == null) throw new RecordException($"{f.Name} of {layout.Info.Label} isn't a list of one subrecord an item, so it can't be read a page at a time.");
            var items = range.Distinct().OrderBy(x => x).ToArray();
            if (items.Length > 0 && (items[0] < 0 || items[^1] >= f.Items)) throw new RecordException($"{f.Name} of {layout.Info.Label} has {f.Items:N0} item(s); there is no [{(items[0] < 0 ? items[0] : items[^1])}].");
            pages[f.Name] = new ListPage(f.Name, f.ItemSignature, items, f.Items);
        }
        var left = layout.Fields.Where(f => f.Big && !asked.ContainsKey(f.Name)).ToList();
        var leftNames = left.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

        // The kept subrecords, in their own order
        var keep = new bool[raw.Subrecords.Count];
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        long kept = 0; int keptCount = 0;
        for (int i = 0; i < keep.Length; i++)
        {
            string? o = layout.Owner[i];
            bool k = o == null || !leftNames.Contains(o);
            if (k && o != null && pages.TryGetValue(o, out var page))
            {
                int n = seen.TryGetValue(o, out var v) ? v : 0;
                seen[o] = n + 1;
                k = page.Place(n) >= 0;
            }
            if (!k) continue;
            var s = raw.Subrecords[i];
            keep[i] = true; keptCount++;
            kept += 6 + s.Length + (s.LongForm ? 10 : 0);
        }
        if (kept > HeavyRecordBytes)
            throw new RecordException($"The part of {layout.Info.Label} that would be read is {Sizes.Text(kept)}, still over the {Sizes.Text(HeavyRecordBytes)} of a heavy record. " +
                (asked.Count > 0 ? "Ask for fewer fields, or for one item of a list (a list whose items are one subrecord each is read a page at a time)." : "Its subrecords that belong to no single field of the schema are that big.") +
                " The whole record can be read with allow_heavy.");

        // The kept subrecords as a record of their own: the header with the size changed and the compression taken off
        var buffer = new byte[RawRecord.HeaderLength + kept];
        raw.Header.CopyTo(buffer, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), checked((uint)kept));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), raw.Flags & ~RawRecord.CompressedFlag);
        int at = RawRecord.HeaderLength;
        for (int i = 0; i < keep.Length; i++)
        {
            if (!keep[i]) continue;
            var s = raw.Subrecords[i];
            int from = s.HeaderStart - (s.LongForm ? 10 : 0), length = s.Start + s.Length - from;
            raw.Data.AsSpan(from, length).CopyTo(buffer.AsSpan(at));
            at += length;
        }

        var session = Sessions.Get(layout.Info.Plugin);
        var obj = (Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter)session.ReadBytes(layout.Type, buffer);
        var rec = new LoadedRecord { Ref = layout.Ref, Info = layout.Info, Type = RecordSerializer.TypeName(obj), Record = obj, Route = ReadRoute.Direct, Session = session };
        return new PartRead
        {
            Layout = layout, Record = rec, PartRaw = RawRecord.FromBytes(buffer, layout.Info.Offset), Pages = pages, Left = left,
            KeptSubrecords = keptCount, KeptBytes = kept,
        };
    }

    /// <summary>
    /// The card of a part read: list items keep their index in the whole record (MapInfos[5000] stays [5000]). Every
    /// page must give exactly as many items as subrecords were kept for it, or the part read is refused.
    /// </summary>
    public Card PartCard(PartRead part, JsonObject? json = null, bool withDefaults = false)
    {
        json ??= Json(part.Record);
        foreach (var page in part.Pages.Values)
        {
            int got = json[page.Field] is JsonArray a ? a.Count : 0;
            if (got != page.Count)
                throw new RecordException($"{part.Layout.Info.Label} can't be read in part: Mutagen read {got} item(s) of {page.Field} from the {page.Count} {page.Signature} subrecords given to it. Read it whole with allow_heavy.");
        }
        var rec = part.Record;
        var info = rec.Info;
        var builder = new CardBuilder(Schema, Label, info.Signature, Sessions.Language.ToString(), () => part.PartRaw,
            key => key != "Null" && HeaderIndex.TryParseFormKey(key, out var plugin, out var id) ? rec.Session.FormIds.Raw(plugin, id) : key == "Null" ? 0u : null)
        {
            ListItems = part.Pages.Values.ToDictionary(p => p.Field, p => p.Items, StringComparer.Ordinal),
        };
        var card = new Card
        {
            Label = info.Label, FormKey = info.FormKey, Signature = info.Signature, Type = rec.Type, Plugin = info.Plugin,
            IsOverride = info.IsOverride, Parent = info.Parent == null ? null : Label(info.Parent), Offset = info.Offset, Size = info.Size,
            Compressed = (info.Flags & RawRecord.CompressedFlag) != 0, Language = Sessions.Language.ToString(),
            Fields = builder.Build(rec.Type, json), Pages = part.Pages,
        };
        if (withDefaults) CardDefaults.Add(Schema, rec.Type, card);
        return card;
    }

    /// <summary>Evidence for a field of a part read. The occurrences are counted in the whole record, not in the part.</summary>
    public Evidence PartEvidence(PartRead part, Card card, string field)
    {
        var rec = part.Record;
        var ev = FieldLookup.Evidence(Schema, card, part.PartRaw, field, id => Label(rec.Session, id),
            formIdOf: key => key == "Null" ? 0u : HeaderIndex.TryParseFormKey(key, out var plugin, out var id) ? rec.Session.FormIds.Raw(plugin, id) : null);
        if (ev.Subrecord == null) return ev;
        int whole = part.Layout.Raw.Subrecords.Count(s => s.Signature == ev.Subrecord);
        var page = part.Pages.Values.FirstOrDefault(p => p.Signature == ev.Subrecord);
        if (page != null)
            for (int i = 0; i < ev.Slices.Count; i++) ev.Slices[i] = ev.Slices[i] with { Occurrence = page.Items[ev.Slices[i].Occurrence] };
        else if (whole != ev.Occurrences)
            throw new RecordException($"{ev.Subrecord} is held {whole} times in {part.Layout.Info.Label} and {ev.Occurrences} times in the part read; ask for the field with its index.");
        ev.Occurrences = whole;
        ev.Compressed = part.Layout.Raw.Compressed;
        return ev;
    }

    /// <summary>The line that says what a heavy record is and what a full read of it costs.</summary>
    public static string HeavyLine(HeavyLayout layout)
    {
        var sb = new StringBuilder();
        sb.Append($"A heavy record: {Sizes.Text(layout.DataBytes)} of data in {HeavyLayout.N(layout.Raw.Subrecords.Count)} subrecords");
        if (layout.Raw.Compressed) sb.Append($" ({Sizes.Text(layout.Raw.StoredSize)} compressed in the file, inflated once here)");
        sb.Append('.');
        return sb.ToString();
    }
}
