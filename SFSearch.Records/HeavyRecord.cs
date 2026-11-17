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
    /// <summary>Set when the field is a list whose items are one subrecord each, all of one signature: that signature.</summary>
    public string? ItemSignature { get; internal set; }
    /// <summary>The items of a big list that can be read a page at a time: each item's subrecords, as indexes
    /// [Start, End) into <see cref="RawRecord.Subrecords"/>. One subrecord an item (the NAVI's NVMI), or several (a
    /// container's CNTO and COED). Null when the field can't be paged (<see cref="NotPaged"/> says why).</summary>
    public IReadOnlyList<(int Start, int End)>? Runs { get; internal set; }
    /// <summary>The subrecord that holds the list's count (a container's COCT, a leveled list's LLCT), or -1. A page read
    /// writes the page's count into it.</summary>
    public int Counter { get; internal set; } = -1;
    /// <summary>For a big field that can't be read a page at a time: why, in words.</summary>
    public string? NotPaged { get; internal set; }
    public bool Paged => Runs != null;
    public int Items { get; internal set; }
    /// <summary>Set when Mutagen's hand-written code fills the field: the CustomLogic step(s) the schema names
    /// (<c>FilledBy</c>, with the step's subrecords in <c>CustomSubrecords</c>, GAPS S38). A PERS's Items: ItemsParse,
    /// from DAT2 (XXXX long forms). Its subrecords are the schema's; that each holds one item of a list is what the
    /// code does, and a page read checks it against Mutagen's count.</summary>
    public string? FilledBy { get; internal set; }
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
    /// <summary>For each subrecord: the item of its field's <see cref="HeavyField.Runs"/> it belongs to, or -1.</summary>
    public required int[] ItemOf { get; init; }
    public long DataBytes => Raw.Data.Length;
    /// <summary>The bytes a subrecord takes in the file: its 6-byte header, and the XXXX before it for a long one.</summary>
    public long Bytes(int subrecord) { var s = Raw.Subrecords[subrecord]; return 6 + s.Length + (s.LongForm ? 10 : 0); }
    /// <summary>The bytes of item <paramref name="k"/> of a paged field.</summary>
    public long ItemBytes(HeavyField f, int k) { var (s, e) = f.Runs![k]; long b = 0; for (int i = s; i < e; i++) b += Bytes(i); return b; }

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
    /// <summary>For each subrecord of <see cref="PartRaw"/>: its index in the whole record's subrecords.</summary>
    public required int[] KeptIndex { get; init; }
    /// <summary>The counter subrecords given the page's count instead of the whole list's (whole-record indexes).</summary>
    public required HashSet<int> Recounted { get; init; }
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

        // Which top-level field each signature belongs to, from the schema's rows of the type. A field Mutagen's
        // hand-written code fills has its subrecords in the schema too (CustomSubrecords: a PERS's Items, from DAT2 with
        // XXXX long forms, GAPS S38). A CustomLogic step at the top of the record is a parsing step, not a field: the
        // field it fills owns its subrecords.
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Own(string? sig, string field)
        {
            if (string.IsNullOrEmpty(sig)) return;
            (owners.TryGetValue(sig, out var set) ? set : owners[sig] = new HashSet<string>(StringComparer.Ordinal)).Add(field);
        }
        var filledBy = new Dictionary<string, string>(StringComparer.Ordinal);
        var countedBy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in Schema.Model.Expand(type))
        {
            string top = Top(row.Path);
            if (row.Def.Kind == "CustomLogic" && row.Path == top) continue;
            Own(row.Subrecord, top);
            if (row.Def.RefObject != null && Schema.Model.Objects.TryGetValue(row.Def.RefObject, out var o)) Own(o.Sig, top);
            foreach (var cs in row.Def.CustomSubrecordList) Own(SchemaGen.SchemaModel.MapSig(row.Map, cs), top);
            if (row.Path == top && row.Def.FilledBy is { } by && row.Def.CustomSubrecords != null) filledBy[top] = by;
            // A list's count in a subrecord of its own ("counted by COCT"): it belongs to the list, so a page read can
            // give it the page's count, and a read that leaves the list out leaves it out too
            if (row.Path == top && row.Def.Note is { } note && CountedBy.Match(note) is { Success: true } cm) { Own(cm.Groups[1].Value, top); countedBy[top] = cm.Groups[1].Value; }
        }

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
        var itemOf = new int[raw.Subrecords.Count];
        Array.Fill(itemOf, -1);
        Dictionary<string, ItemLocator.TopList>? tops = null;
        string? topsFailed = null;
        if (fields.Any(f => f.Kind is "List" or "Array"))
        {
            try { tops = ItemLocator.TopLists(Schema, raw, type); }
            catch (Exception e) when (e is not OutOfMemoryException) { topsFailed = e.Message; }
        }
        // A list whose items hold a signature other fields hold too (a leveled list entry's CTDA, which the record's own
        // Conditions also hold) gets those subrecords from the walk of Mutagen's reader: the schema gives them to no
        // one field, the walk to this one. Only subrecords no field has, and only when the walk took all of the field's.
        foreach (var f in fields.Where(f => f.Kind is "List" or "Array"))
        {
            if (tops == null || !tops.TryGetValue(f.Name, out var t)) continue;
            if (!t.Claimed.All(i => owner[i] == null || owner[i] == f.Name) || !Enumerable.Range(0, owner.Length).Where(i => owner[i] == f.Name).All(t.Claimed.Contains)) continue;
            foreach (int i in t.Claimed.Where(i => owner[i] == null).OrderBy(i => i))
            {
                owner[i] = f.Name;
                var s = raw.Subrecords[i];
                if (!f.Signatures.Contains(s.Signature)) f.Signatures.Add(s.Signature);
                f.Subrecords++; f.Bytes += 6 + s.Length + (s.LongForm ? 10 : 0);
            }
        }
        foreach (var f in fields)
        {
            f.Big = f.Bytes > HeavyPartFieldBytes;
            if (!f.Big) continue;
            var prop = Schema.Prop(type, f.Name);
            if (f.Kind is not ("List" or "Array") || prop?.Item is not { } item)
            {
                f.NotPaged = $"it is one value{(f.Kind.Length > 0 ? $" (a Mutagen {f.Kind})" : "")}, not a list, and Mutagen reads it from its {string.Join(", ", f.Signatures)} subrecord{(f.Subrecords == 1 ? "" : "s")} at once";
                continue;
            }
            var mine = Enumerable.Range(0, owner.Length).Where(i => owner[i] == f.Name).ToList();
            // 1. The list cut into items the way Mutagen's generated reader takes them, as the evidence walk follows it
            //    (EvidenceItems.cs): a counted list as many items as its count, an item of several subrecords to its
            //    end marker or to a subrecord it doesn't hold. It must have taken exactly the field's subrecords.
            string? itemSig = item.Subrecord ?? (item.RefObject != null && Schema.Model.Objects.TryGetValue(item.RefObject, out var io) ? io.Sig : null);
            string? byHand = filledBy.GetValueOrDefault(f.Name);
            List<(int, int)>? runs = null;
            string? why = null;
            if (tops != null && tops.TryGetValue(f.Name, out var t))
            {
                if (!t.Claimed.SetEquals(mine))
                    why = $"the schema gives it {N(mine.Count)} subrecords, and Mutagen's reader, as SFSearch follows it, takes {N(t.Claimed.Count)} for it";
                else if (t.Counter >= 0 && t.CounterValue != t.Items.Count)
                    why = $"its count ({raw.Subrecords[t.Counter].Signature}) says {t.CounterValue?.ToString(CultureInfo.InvariantCulture) ?? "a size SFSearch doesn't read"}, and its subrecords cut into {N(t.Items.Count)} items";
                else if (t.Items.Count == 0)
                    why = "its subrecords cut into no items";
                else { runs = t.Items; f.Counter = t.Counter; }
            }
            // 2. A list of one signature that is the item's own (or that Mutagen's hand-written code fills, a PERS's Items:
            //    one DAT2 an item): one subrecord an item. The page read checks the items against Mutagen's count.
            if (runs == null && why == null && f.Signatures.Count == 1 && (itemSig == f.Signatures[0] || byHand != null))
                runs = mine.Select(i => (i, i + 1)).ToList();
            if (runs == null)
            {
                var body = mine.Where(i => raw.Subrecords[i].Signature != countedBy.GetValueOrDefault(f.Name)).ToList();
                f.NotPaged = why ?? (body.Count == 1
                    ? $"its {(CountOf(f) is var n && n > 0 ? N(n) + " " : "")}items are entries inside one {raw.Subrecords[body[0]].Signature} subrecord, which Mutagen reads at once"
                    : $"its subrecords ({string.Join(", ", f.Signatures)}) can't be cut into items that Mutagen's reader takes one by one{(topsFailed != null ? $" (the cut failed: {topsFailed.TrimEnd('.')})" : "")}");
                continue;
            }
            f.Runs = runs; f.Items = runs.Count; f.FilledBy = byHand;
            if (runs.All(x => x.Item2 - x.Item1 == 1) && f.Signatures.Count == 1 + (f.Counter >= 0 ? 1 : 0))
                f.ItemSignature = raw.Subrecords[runs[0].Item1].Signature;
            for (int k = 0; k < runs.Count; k++) for (int i = runs[k].Item1; i < runs[k].Item2; i++) itemOf[i] = k;
        }
        return new HeavyLayout
        {
            Ref = r, Info = info, Type = type, Raw = raw, Fields = fields, Owner = owner, ItemOf = itemOf,
            Signatures = sigs.Select(s => { var c = counts[s]; return new SignatureCount(s, c.Count, c.Bytes, c.Min, c.Max, owners.TryGetValue(s, out var o) && o.Count == 1 ? o.First() : null); }).ToList(),
        };

        // The entries of a list inside one subrecord, when the schema gives their size (for the words only)
        int CountOf(HeavyField f)
        {
            var p = Schema.Prop(type, f.Name);
            int? size = p?.Item?.Size ?? (p?.Item?.RefObject is { } ro && Schema.Model.Objects.TryGetValue(ro, out var o) ? o.FixedSize : null);
            long content = f.Bytes - 6 * f.Subrecords;
            return size is int z && z > 0 ? (int)(content / z) : 0;
        }
    }

    static readonly System.Text.RegularExpressions.Regex CountedBy = new(@"counted by ([A-Z0-9_]{4})", System.Text.RegularExpressions.RegexOptions.Compiled);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    static long? CounterOf(RawRecord raw, int index)
    {
        var c = raw.Content(raw.Subrecords[index]);
        return c.Length switch { 1 => c[0], 2 => BinaryPrimitives.ReadUInt16LittleEndian(c), 4 => BinaryPrimitives.ReadUInt32LittleEndian(c), _ => null };
    }

    /// <summary>The top-level field of a schema path: <c>MapInfos[*].NavigationMesh</c> → <c>MapInfos</c>.</summary>
    internal static string Top(string path)
    {
        int end = path.IndexOfAny(new[] { '.', '[', '{' });
        return end < 0 ? path : path[..end];
    }

    /// <summary>
    /// Reads a heavy record in part through Mutagen: every subrecord except those of the big fields not named in
    /// <paramref name="asked"/>. A named field with a range (a big list that can be paged: <see cref="HeavyField.Runs"/>)
    /// keeps only the subrecords of the items of that range, and its count subrecord, when it has one, is given the
    /// page's count. The plugin's session must be current (<see cref="PluginSessions.Use"/>).
    /// </summary>
    /// <param name="asked">Top-level field names; the value is the indexes of the items to keep (null: the whole field).</param>
    public PartRead ReadPart(HeavyLayout layout, IReadOnlyDictionary<string, IReadOnlyCollection<int>?> asked)
    {
        var raw = layout.Raw;
        var pages = new Dictionary<string, ListPage>(StringComparer.Ordinal);
        var paged = new Dictionary<string, HeavyField>(StringComparer.Ordinal);
        foreach (var (name, range) in asked)
        {
            var f = layout.Fields.FirstOrDefault(x => x.Name == name) ?? throw new RecordException($"{layout.Info.Label} has no subrecord of a field {name}.");
            if (range == null) continue;
            if (!f.Paged) throw new RecordException($"{f.Name} of {layout.Info.Label} can't be read a page at a time: {f.NotPaged ?? "it isn't a big list"}.");
            var items = range.Distinct().OrderBy(x => x).ToArray();
            if (items.Length > 0 && (items[0] < 0 || items[^1] >= f.Items)) throw new RecordException($"{f.Name} of {layout.Info.Label} has {f.Items:N0} item(s); there is no [{(items[0] < 0 ? items[0] : items[^1])}].");
            pages[f.Name] = new ListPage(f.Name, f.ItemSignature ?? string.Join(", ", f.Signatures), items, f.Items);
            paged[f.Name] = f;
        }
        var left = layout.Fields.Where(f => f.Big && !asked.ContainsKey(f.Name)).ToList();
        var leftNames = left.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

        // The kept subrecords, in their own order. Of a paged list: the items of the page, its count, and anything else
        // the list holds outside its items (its markers)
        var chosen = new HashSet<int>();
        foreach (var (name, page) in pages)
            foreach (int k in page.Items) { var (s, e) = paged[name].Runs![k]; for (int i = s; i < e; i++) chosen.Add(i); }
        var keep = new bool[raw.Subrecords.Count];
        var recount = new Dictionary<int, int>();
        long kept = 0; int keptCount = 0;
        for (int i = 0; i < keep.Length; i++)
        {
            string? o = layout.Owner[i];
            bool k = o == null || !leftNames.Contains(o);
            if (k && o != null && paged.TryGetValue(o, out var pf))
            {
                if (i == pf.Counter) recount[i] = pages[o].Count;
                else if (layout.ItemOf[i] >= 0) k = chosen.Contains(i);
            }
            if (!k) continue;
            keep[i] = true; keptCount++;
            kept += layout.Bytes(i);
        }
        if (kept > HeavyRecordBytes)
        {
            var whole = layout.Fields.Where(f => f.Big && asked.TryGetValue(f.Name, out var range) && range == null).ToList();
            throw new RecordException($"The part of {layout.Info.Label} that would be read is {Sizes.Text(kept)}, still over the {Sizes.Text(HeavyRecordBytes)} of a heavy record. " +
                (whole.Count > 0 ? $"It holds {string.Join(" and ", whole.Select(f => $"{f.Name} whole ({Sizes.Text(f.Bytes)}{(f.Paged ? $"; a list of {f.Items:N0} items: ask for {f.Name} with an index, or read it a page at a time" : $", which can't be read in parts: {f.NotPaged}")})"))}. "
                    : asked.Count > 0 ? "Ask for fewer items. " : "Its subrecords that belong to no single big field of the schema are that big. ") +
                "The whole record can be read with allow_heavy.");
        }

        // The kept subrecords as a record of their own: the header with the size changed and the compression taken off
        var buffer = new byte[RawRecord.HeaderLength + kept];
        raw.Header.CopyTo(buffer, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), checked((uint)kept));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), raw.Flags & ~RawRecord.CompressedFlag);
        int at = RawRecord.HeaderLength;
        var keptIndex = new int[keptCount];
        for (int i = 0, n = 0; i < keep.Length; i++)
        {
            if (!keep[i]) continue;
            keptIndex[n++] = i;
            var s = raw.Subrecords[i];
            int from = s.HeaderStart - (s.LongForm ? 10 : 0), length = s.Start + s.Length - from;
            raw.Data.AsSpan(from, length).CopyTo(buffer.AsSpan(at));
            if (recount.TryGetValue(i, out int count))
            {
                // The page's count, in the counter's own width
                var content = buffer.AsSpan(at + (s.Start - from), s.Length);
                switch (s.Length)
                {
                    case 1: content[0] = checked((byte)count); break;
                    case 2: BinaryPrimitives.WriteUInt16LittleEndian(content, checked((ushort)count)); break;
                    case 4: BinaryPrimitives.WriteUInt32LittleEndian(content, (uint)count); break;
                    case 8: BinaryPrimitives.WriteUInt64LittleEndian(content, (ulong)count); break;
                    default: throw new RecordException($"The count of a list of {layout.Info.Label} is a {s.Signature} of {s.Length} bytes, a size SFSearch doesn't write: the list can't be read a page at a time.");
                }
            }
            at += length;
        }

        var session = Sessions.Get(layout.Info.Plugin);
        var obj = (Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter)session.ReadBytes(layout.Type, buffer);
        var rec = new LoadedRecord { Ref = layout.Ref, Info = layout.Info, Type = RecordSerializer.TypeName(obj), Record = obj, Route = ReadRoute.Direct, Session = session };
        return new PartRead
        {
            Layout = layout, Record = rec, PartRaw = RawRecord.FromBytes(buffer, layout.Info.Offset), Pages = pages, Left = left,
            KeptIndex = keptIndex, Recounted = recount.Keys.ToHashSet(), KeptSubrecords = keptCount, KeptBytes = kept,
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
            FromRecord = path => PropertyAt(rec.Record, path),
        };
        // The notes the whole card has (RecordEngine.Card)
        var problems = Index.Plugins[rec.Ref.Plugin].Problems;
        if (problems.TypeMismatches.Contains(rec.Ref.Entry))
            card.Notes.Add($"This {info.Signature} overrides a record of another type in its master: {info.Plugin} was probably built for an older version of that master.");
        if (problems.OverridesOfNothing.Contains(rec.Ref.Entry))
            card.Notes.Add($"This record's FormKey belongs to a master that has no such record.");
        if (withDefaults) CardDefaults.Add(Schema, rec.Type, card);
        return card;
    }

    /// <summary>Evidence for a field of a part read. The occurrences are counted in the whole record, not in the part:
    /// each slice's subrecord is found in the part, then in the whole record by where the part took it from.</summary>
    public Evidence PartEvidence(PartRead part, Card card, string field)
    {
        var rec = part.Record;
        var ev = FieldLookup.Evidence(Schema, card, part.PartRaw, field, id => Label(rec.Session, id),
            formIdOf: key => key == "Null" ? 0u : HeaderIndex.TryParseFormKey(key, out var plugin, out var id) ? rec.Session.FormIds.Raw(plugin, id) : null,
            positionsOf: rec.Session.Positions, positionPath: path =>
            {
                // A paged list's item, numbered in the whole list on the card, is numbered among the items read in the part
                foreach (var page in part.Pages.Values)
                    if (path.StartsWith(page.Field + "[", StringComparison.Ordinal) && path.IndexOf(']') is int close and > 0
                        && int.TryParse(path.AsSpan(page.Field.Length + 1, close - page.Field.Length - 1), out int whole) && page.Place(whole) is int at and >= 0)
                        return $"{page.Field}[{at}]{path[(close + 1)..]}";
                return path;
            });
        if (ev.Subrecord == null) return ev;
        var whole = part.Layout.Raw.Subrecords;
        // The part's subrecords of that signature, and the occurrence of each in the whole record
        var inPart = new List<int>();
        for (int j = 0; j < part.PartRaw.Subrecords.Count; j++) if (part.PartRaw.Subrecords[j].Signature == ev.Subrecord) inPart.Add(j);
        var occurrence = new Dictionary<int, int>();
        int total = 0;
        for (int w = 0; w < whole.Count; w++) if (whole[w].Signature == ev.Subrecord) occurrence[w] = total++;
        for (int i = 0; i < ev.Slices.Count; i++)
        {
            int o = ev.Slices[i].Occurrence;
            if (o < 0 || o >= inPart.Count) throw new RecordException($"The evidence for {field} names {ev.Subrecord} occurrence {o + 1}, and the part read holds {inPart.Count}.");
            int w = part.KeptIndex[inPart[o]];
            if (part.Recounted.Contains(w))
                throw new RecordException($"{field} is the count of a list read a page at a time: the part read holds the page's count, not the record's. The raw view gives the record's {ev.Subrecord}.");
            ev.Slices[i] = ev.Slices[i] with { Occurrence = occurrence[w] };
        }
        ev.Occurrences = total;
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

/// <summary>The top-level lists of a heavy record cut into items (HeavyRecord.cs, GAPS S7).</summary>
internal sealed partial class ItemLocator
{
    /// <summary>A top-level list as the walk cut it: its items' subrecords [Start, End), its count subrecord (or -1)
    /// and the count that subrecord holds, and every subrecord the list took.</summary>
    internal sealed record TopList(string Name, List<(int, int)> Items, int Counter, long? CounterValue, HashSet<int> Claimed);

    /// <summary>
    /// The record's top-level lists cut into items the way the evidence walk cuts a record (<see cref="Claim"/>): the
    /// subrecords handed to the record's fields in file order as Mutagen's generated reader does, a counted list taking
    /// as many items as its count says, an item of several subrecords ending at its end marker, at a subrecord it
    /// doesn't hold or at a field it already had. Lists whose items are one subrecord (with what follows it, a
    /// condition's CIS1 and CIS2) and lists of several subrecords an item; nothing else.
    /// </summary>
    internal static Dictionary<string, TopList> TopLists(RecordSchema schema, RawRecord raw, string type)
    {
        var l = new ItemLocator(schema, raw, null, _ => "", null);
        var claims = l.Claim(type, null, 0, raw.Subrecords.Count);
        var groups = l.Groups(type, null);
        var result = new Dictionary<string, TopList>(StringComparer.Ordinal);
        foreach (var (gi, items) in l._items)
        {
            var g = groups[gi];
            if (g.Kind is not (GroupKind.ListSub or GroupKind.ListMulti or GroupKind.PerkEffects)) continue;
            var claimed = new HashSet<int>();
            int counter = -1;
            for (int i = 0; i < claims.Length; i++)
            {
                if (claims[i] != gi) continue;
                claimed.Add(i);
                if (counter < 0 && g.Counter != null && l.Sig(i) == g.Counter) counter = i;
            }
            result[g.Name] = new TopList(g.Name, new List<(int, int)>(items), counter, counter >= 0 ? l.CounterValue(counter) : null, claimed);
        }
        return result;
    }
}
