// A heavy record's whole JSON, whole card and verify, a part at a time (GAPS row S7, fix J7).
//
// HeavyRecord.cs reads a heavy record in part: its small fields and a page of a big list, through Mutagen's own reader
// of the kept subrecords. Here that gives the views that need the whole record, without holding it:
//  - The JSON and the card are written as the whole record's would be, line for line, as a run of parts: each item of
//    a big list is a part, and so is each run of the other fields between the lists. The other fields come from one
//    read that keeps every big list's first item (so each list stands where Mutagen's serializer puts it); the items
//    come a batch at a time from reads that keep only that batch. A cursor is a part and a line in it.
//  - Verify writes each part read back through Mutagen and compares it with the bytes it was read from: every item of
//    the big lists in parts of at most a few megabytes, each with every other subrecord of the record. Together the
//    parts hold every subrecord of the record. What a part can't show is said: a list whose subrecords are not one
//    run in the file, a list's count (a part holds the page's count), a compressed record's stream.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins.Records;

namespace SFSearch.Records;

/// <summary>A heavy record's whole JSON or card as parts: runs of lines between the big lists, and each list item.</summary>
public sealed class HeavyText
{
    public required HeavyLayout Layout { get; init; }
    /// <summary>"json" or "card".</summary>
    public required string Kind { get; init; }
    /// <summary>The read of every field but the big lists, which keep their first item.</summary>
    public required PartRead Head { get; init; }
    internal List<HeavySegment> Segments { get; } = new();
    /// <summary>The parts of the whole text: one for each run of lines between big lists, one for each list item.</summary>
    public int Parts => Segments.Sum(s => s.Count);
    /// <summary>The big lists given a part an item, in the order they stand in the text.</summary>
    public IEnumerable<HeavyField> Lists => Segments.Where(s => s.List != null).Select(s => s.List!);
    /// <summary>What the answer must say about the head's texts and the card (the notes of the whole card's view).</summary>
    public List<string> Notes { get; } = new();
}

internal sealed class HeavySegment
{
    public List<string>? Lines;
    public HeavyField? List;
    public int First;
    public int Count => List?.Items ?? 1;
}

/// <summary>One page of a <see cref="HeavyText"/>.</summary>
public sealed class HeavyPage
{
    public List<string> Lines { get; } = new();
    /// <summary>The part the page starts in (from 0), and the line of it.</summary>
    public int FromPart { get; init; }
    public int FromLine { get; init; }
    /// <summary>Where the next page starts: a part and a line in it. <see cref="End"/> when there is none.</summary>
    public int NextPart { get; internal set; }
    public int NextLine { get; internal set; }
    public bool End { get; internal set; }
    /// <summary>The parts this page shows, whole or in part.</summary>
    public int PartsShown { get; internal set; }
    /// <summary>The list items shown, by list: the first and last index.</summary>
    public List<(HeavyField List, int First, int Last)> Items { get; } = new();
    /// <summary>The part reads made for the page, and the subrecords and bytes they gave Mutagen.</summary>
    public int Reads { get; internal set; }
    public int ReadSubrecords { get; internal set; }
    public long ReadBytes { get; internal set; }
    public List<string> Notes { get; } = new();
}

/// <summary>One part of a heavy record verified: its bytes against Mutagen's write of the part read from them.</summary>
public sealed class HeavyVerifyPart
{
    public required string What { get; init; }
    public required VerifyResult Result { get; init; }
    public HeavyField? List { get; init; }
    public int From { get; init; }
    public int Count { get; init; }
    public int Subrecords { get; init; }
    public long Bytes { get; init; }
    /// <summary>When the part differs: the record's own subrecord the first difference is in.</summary>
    public string? Where { get; init; }
}

/// <summary>A run of a heavy record's verify parts, from a cursor.</summary>
public sealed class HeavyVerifyPage
{
    public List<HeavyVerifyPart> Parts { get; } = new();
    /// <summary>The verify's units: every item of the big lists that can be paged (one, when there are none).</summary>
    public int Units { get; init; }
    public int FromUnit { get; init; }
    /// <summary>The unit the next call starts at, or -1 when the record is done (or a part differed).</summary>
    public int NextUnit { get; internal set; } = -1;
    /// <summary>What the parts together can't show, in words; empty when they show the whole record.</summary>
    public List<string> Limits { get; } = new();
    /// <summary>What the parts together do show, in words (the lists' subrecords one run in the file …).</summary>
    public List<string> Shown { get; } = new();
    public bool AllEqual => Parts.All(p => p.Result.Equal);
}

public sealed partial class RecordEngine
{
    /// <summary>The JSON the record tool writes: indented, text as it is.</summary>
    public static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Items read in a page's first batch of a list, to learn how long an item's text is.</summary>
    public int HeavyProbeItems { get; internal set; } = 16;
    /// <summary>The most bytes of items the first batch reads (one item at least).</summary>
    public long HeavyProbeBytes { get; internal set; } = 256 << 10;
    /// <summary>The most items of one batch.</summary>
    public int HeavyBatchItems { get; internal set; } = 20_000;
    /// <summary>The most bytes of items one batch (and one verify part) reads through Mutagen; one item at least.</summary>
    public long HeavyBatchBytes { get; internal set; } = 4 << 20;
    /// <summary>The most bytes of items one verify call reads, in parts of <see cref="HeavyBatchBytes"/>.</summary>
    public long HeavyVerifyBytes { get; internal set; } = 16 << 20;

    const string Marker = "@@sfsearch-heavy-list:";

    /// <summary>
    /// The whole JSON (<paramref name="kind"/> "json") or card ("card") of a heavy record as parts. One part read is made
    /// here: every field, the big lists that can be paged with their first item only (they stand where the serializer
    /// puts them), the other big fields whole. A big field that can't be paged and doesn't fit in that read is refused
    /// with its reason.
    /// </summary>
    public HeavyText HeavyText(HeavyLayout layout, string kind)
    {
        if (kind is not ("json" or "card")) throw new ArgumentException("kind is json or card", nameof(kind));
        // Where each big list stands: Mutagen's own serializer puts it there when the read keeps one of its items (its
        // smallest, when that is small); a list whose items are all big is put in the schema's order of the fields,
        // as the card's defaults are
        var asked = new Dictionary<string, IReadOnlyCollection<int>?>(StringComparer.Ordinal);
        var placed = new List<HeavyField>();
        foreach (var f in layout.Fields.Where(f => f.Big))
        {
            if (!f.Paged) { asked[f.Name] = null; continue; }
            int smallest = Enumerable.Range(0, f.Items).MinBy(k => layout.ItemBytes(f, k));
            if (layout.ItemBytes(f, smallest) <= HeavyPlaceBytes) asked[f.Name] = new[] { smallest };
            else placed.Add(f);
        }
        var head = ReadPart(layout, asked);
        var json = Json(head.Record);
        CheckPages(head, json);
        var text = new HeavyText { Layout = layout, Kind = kind, Head = head };
        text.Notes.AddRange(TextNotes(head.Record, new JsonNode?[] { json }));
        foreach (var f in placed) Place(json, layout.Type, f.Name);
        var paged = layout.Fields.Where(f => f.Big && f.Paged).ToList();
        List<string> lines;
        if (kind == "json")
        {
            foreach (var f in paged) json[f.Name] = new JsonArray(Marker + f.Name);
            lines = Split(json.ToJsonString(PrettyJson));
        }
        else
        {
            var card = PartCard(head, json, withDefaults: true);
            text.Notes.InsertRange(0, card.Notes);
            card.Notes.Clear();
            lines = Split(card.ToText(int.MaxValue, 64)).Skip(2).ToList();
            foreach (var f in paged)
            {
                // The list's lines in the head's card: its header (one item) and that item's lines. Each is put back as
                // the header with the whole list's count and a marker where the items go
                var cf = card.Fields.FirstOrDefault(x => x.Name == f.Name) ?? throw new RecordException($"The card of {layout.Info.Label} read in part has no {f.Name}, so it can't be given a part at a time.");
                var one = FieldLines(card, cf);
                int at = IndexOf(lines, one);
                string prefix = "  " + SFSearch.Records.Card.Head(cf) + ": ";
                string shown = placed.Contains(f) ? "0 items" : "1 item";
                if (at < 0 || !one[0].StartsWith(prefix + shown, StringComparison.Ordinal))
                    throw new RecordException($"The card of {layout.Info.Label} doesn't show {f.Name} as a list of {shown} where SFSearch looks for it, so it can't be given a part at a time.");
                string header = prefix + Count(f.Items) + one[0][(prefix.Length + shown.Length)..];
                lines.RemoveRange(at, one.Count);
                lines.InsertRange(at, new[] { header, "    \"" + Marker + f.Name + "\"" });
            }
        }
        // Cut at the markers
        var block = new List<string>();
        int first = 0;
        foreach (var l in lines)
        {
            string t = l.Trim();
            if (t.StartsWith("\"" + Marker, StringComparison.Ordinal))
            {
                string name = t[(Marker.Length + 1)..^1];
                var f = paged.First(x => x.Name == name);
                if (block.Count > 0) { text.Segments.Add(new HeavySegment { Lines = block, First = first }); first++; block = new List<string>(); }
                text.Segments.Add(new HeavySegment { List = f, First = first });
                first += f.Items;
                continue;
            }
            block.Add(l);
        }
        if (block.Count > 0) text.Segments.Add(new HeavySegment { Lines = block, First = first });
        if (text.Segments.Count(s => s.List != null) != paged.Count)
            throw new RecordException($"The {kind} of {layout.Info.Label} read in part doesn't show each big list once, so it can't be given a part at a time.");
        return text;
    }

    /// <summary>A big list's item kept in the head read to place the list is at most this big.</summary>
    public long HeavyPlaceBytes { get; internal set; } = 256 << 10;

    /// <summary>Puts an empty list into the JSON where the schema's order of the type's fields puts it: before the first
    /// field the JSON has that comes after it.</summary>
    void Place(JsonObject json, string type, string name)
    {
        var order = Schema.Props(type).Select(p => p.Name).ToList();
        int mine = order.IndexOf(name);
        if (mine < 0) throw new RecordException($"The schema has no field {name} on {type}, so the list can't be placed in its JSON.");
        int at = json.Count;
        for (int i = 0; i < json.Count; i++)
        {
            int o = order.IndexOf(json.GetAt(i).Key);
            if (o > mine) { at = i; break; }
        }
        json.Insert(at, name, new JsonArray());
    }

    static string Count(int n) => n.ToString("N0", CultureInfo.InvariantCulture) + (n == 1 ? " item" : " items");

    static List<string> Split(string text) => text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();

    /// <summary>The lines one field of a card takes in its text, at depth 1.</summary>
    static List<string> FieldLines(SFSearch.Records.Card card, CardField f)
    {
        var alone = new SFSearch.Records.Card { Label = "", FormKey = "", Signature = "", Type = card.Type, Plugin = "", Fields = new List<CardField> { f } };
        return Split(alone.ToText(int.MaxValue, 64)).Skip(2).ToList();
    }

    static int IndexOf(List<string> lines, List<string> run)
    {
        for (int i = 0; i + run.Count <= lines.Count; i++)
        {
            if (lines[i] != run[0]) continue;
            int j = 1;
            while (j < run.Count && lines[i + j] == run[j]) j++;
            if (j == run.Count) return i;
        }
        return -1;
    }

    /// <summary>Every page of a part read must give exactly as many items as were kept for it.</summary>
    void CheckPages(PartRead part, JsonObject json)
    {
        foreach (var page in part.Pages.Values)
        {
            int got = json[page.Field] is JsonArray a ? a.Count : 0;
            if (got != page.Count)
                throw new RecordException($"{part.Layout.Info.Label} can't be read in part: Mutagen read {got} item(s) of {page.Field} from the subrecords of the {page.Count} item(s) given to it. Read it whole with allow_heavy.");
        }
    }

    /// <summary>
    /// One page of a heavy record's JSON or card: from part <paramref name="part"/>, line <paramref name="line"/> of it,
    /// as many whole parts as fit in <paramref name="maxChars"/>. A part longer than the page is given a page at a time
    /// by its lines. List items are read in batches: a first small one measures an item's text, the next ones are sized
    /// to what is left of the page.
    /// </summary>
    public HeavyPage HeavyTextPage(HeavyText text, int part, int line, int maxChars)
    {
        if (part < 0 || part >= text.Parts) throw new RecordException($"The cursor points past the end ({text.Parts:N0} parts).");
        var page = new HeavyPage { FromPart = part, FromLine = line };
        int used = 0;
        int p = part, l = line;
        bool full = false;

        // Adds the lines of part p from line l. False when the page is full (p and l say where the next page starts).
        bool Add(List<string> lines)
        {
            int size = 0;
            for (int i = l; i < lines.Count; i++) size += lines[i].Length + 1;
            if (l >= lines.Count) { l = 0; return true; }
            if (used + size <= maxChars)
            {
                for (int i = l; i < lines.Count; i++) page.Lines.Add(lines[i]);
                used += size; page.PartsShown++; l = 0;
                return true;
            }
            if (page.Lines.Count > 0) return false;
            // A part longer than the page: its lines from l, as many as fit (one at least; a line longer than the page cut)
            page.PartsShown++;
            while (l < lines.Count)
            {
                string s = lines[l];
                if (page.Lines.Count > 0 && used + s.Length + 1 > maxChars) break;
                if (s.Length + 1 > maxChars)
                {
                    int keep = Math.Max(200, maxChars - used - 120);
                    if (keep < s.Length) s = s[..keep] + $" … [this line is cut: {lines[l].Length - keep:N0} more characters. A larger max_chars shows them]";
                }
                page.Lines.Add(s); used += s.Length + 1; l++;
            }
            if (l >= lines.Count) { l = 0; return true; }
            return false;
        }

        while (p < text.Parts && !full)
        {
            var seg = text.Segments.Last(s => s.First <= p);
            if (seg.List == null)
            {
                if (!Add(seg.Lines!)) { full = true; break; }
                p++;
                continue;
            }
            // Items of a list, a batch at a time
            var f = seg.List;
            int k = p - seg.First;
            double perItem = 0; int measured = 0;
            while (k < f.Items && !full)
            {
                int most = measured == 0 ? Math.Min(HeavyProbeItems, f.Items - k)
                    : Math.Clamp((int)((maxChars - used) / Math.Max(1.0, perItem)) + 1, 1, Math.Min(HeavyBatchItems, f.Items - k));
                long cap = measured == 0 ? HeavyProbeBytes : HeavyBatchBytes;
                // An item whose bytes alone are more than what is left of the page won't fit (its text is longer than
                // its bytes): the page ends before it, unread
                if (page.Lines.Count > 0 && l == 0 && text.Layout.ItemBytes(f, k) > maxChars - used) { full = true; break; }
                int n = 0; long bytes = 0;
                while (n < most && (n == 0 || bytes + text.Layout.ItemBytes(f, k + n) <= cap)) bytes += text.Layout.ItemBytes(f, k + n++);
                var items = RenderItems(text, f, k, n, page);
                for (int i = 0; i < items.Count; i++)
                {
                    perItem = (perItem * measured + items[i].Sum(x => x.Length + 1)) / (measured + 1); measured++;
                    if (!Add(items[i])) { full = true; break; }
                    Shown(page, f, k);
                    k++; p++;
                }
                if (full && l > 0) Shown(page, f, k);   // the item begun on this page
            }
        }
        page.NextPart = p; page.NextLine = l;
        page.End = p >= text.Parts;
        return page;

        static void Shown(HeavyPage page, HeavyField f, int k)
        {
            int at = page.Items.FindIndex(x => x.List == f);
            if (at < 0) page.Items.Add((f, k, k));
            else page.Items[at] = (f, page.Items[at].First, Math.Max(page.Items[at].Last, k));
        }
    }

    /// <summary>The lines of items [from, from + count) of a big list, as the whole record's JSON or card has them.</summary>
    List<List<string>> RenderItems(HeavyText text, HeavyField f, int from, int count, HeavyPage page)
    {
        var part = ReadPart(text.Layout, new Dictionary<string, IReadOnlyCollection<int>?>(StringComparer.Ordinal) { [f.Name] = Enumerable.Range(from, count).ToArray() });
        page.Reads++; page.ReadSubrecords += part.KeptSubrecords; page.ReadBytes += part.KeptBytes;
        var json = Json(part.Record);
        CheckPages(part, json);
        var result = new List<List<string>>(count);
        if (text.Kind == "json")
        {
            var a = (JsonArray)json[f.Name]!;
            foreach (var n in TextNotes(part.Record, a)) if (!page.Notes.Contains(n)) page.Notes.Add(n);
            for (int i = 0; i < a.Count; i++)
            {
                var lines = Split(a[i]?.ToJsonString(PrettyJson) ?? "null").Select(x => "    " + x).ToList();
                if (from + i < f.Items - 1) lines[^1] += ",";
                result.Add(lines);
            }
        }
        else
        {
            var card = PartCard(part, json, withDefaults: true);
            var cf = card.Fields.First(x => x.Name == f.Name);
            foreach (var n in TextNotes(part.Record, new JsonNode?[] { json[f.Name] })) if (!page.Notes.Contains(n)) page.Notes.Add(n);
            foreach (var item in cf.Children!)
                result.Add(FieldLines(card, item).Select(x => "  " + x).ToList());
        }
        return result;
    }

    /// <summary>
    /// Verifies a heavy record in parts, from verify unit <paramref name="fromUnit"/> (an item of the big lists, in their
    /// order), until <see cref="HeavyVerifyBytes"/> of items are verified, a part differs, or the record ends. Each part
    /// keeps every subrecord of the record but the big lists, which keep only the part's items (and the first item of
    /// each other list): Mutagen reads it, its JSON is turned back into a record, Mutagen writes that, and the written
    /// record is compared with the part's bytes, as the one-record verify compares a whole record.
    /// </summary>
    public HeavyVerifyPage VerifyParts(HeavyLayout layout, int fromUnit)
    {
        var lists = layout.Fields.Where(f => f.Big && f.Paged).ToList();
        int units = Math.Max(1, lists.Sum(f => f.Items));
        if (fromUnit < 0 || fromUnit >= units) throw new RecordException($"The cursor points past the end ({units:N0} parts).");
        var page = new HeavyVerifyPage { Units = units, FromUnit = fromUnit };
        Limits(layout, lists, page);
        var verifier = new Verifier(this);
        long done = 0;
        int u = fromUnit;
        while (u < units && done < HeavyVerifyBytes)
        {
            var asked = new Dictionary<string, IReadOnlyCollection<int>?>(StringComparer.Ordinal);
            foreach (var f in layout.Fields.Where(f => f.Big)) asked[f.Name] = f.Paged ? new[] { 0 } : null;
            HeavyField? list = null; int from = 0, n = 1;
            if (lists.Count > 0)
            {
                int start = 0;
                foreach (var f in lists) { if (u < start + f.Items) { list = f; break; } start += f.Items; }
                from = u - start;
                long bytes = 0; n = 0;
                while (from + n < list!.Items && (n == 0 || bytes + layout.ItemBytes(list, from + n) <= HeavyBatchBytes)) bytes += layout.ItemBytes(list, from + n++);
                asked[list.Name] = Enumerable.Range(from, n).ToArray();
                done += bytes;
            }
            else done = long.MaxValue;
            var part = ReadPart(layout, asked);
            var result = verifier.VerifyPart(part);
            string what = list == null ? "the whole record" : n == 1 ? $"{list.Name}[{from}]" : $"{list.Name}[{from}] to [{from + n - 1}]";
            string? where = null;
            if (!result.Equal && SubrecordNumber.Match(result.Detail ?? "") is { Success: true } m
                && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int at) && at >= 1 && at <= part.KeptIndex.Length)
            {
                int w = part.KeptIndex[at - 1];
                where = $"Subrecord {at} of the part is subrecord {w + 1:N0} of the record ({layout.Raw.Subrecords[w].Signature}{(layout.Owner[w] is { } o ? ", " + o + (layout.ItemOf[w] >= 0 ? $"[{layout.ItemOf[w]}]" : "") : "")}).";
            }
            page.Parts.Add(new HeavyVerifyPart { What = what, Result = result, List = list, From = from, Count = n, Subrecords = part.KeptSubrecords, Bytes = part.KeptBytes, Where = where });
            u += n;
            if (!result.Equal) { u = units; break; }
        }
        page.NextUnit = u < units ? u : -1;
        return page;
    }

    static readonly System.Text.RegularExpressions.Regex SubrecordNumber = new(@"^Subrecord (\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>What the parts show of the whole record, and what they can't.</summary>
    static void Limits(HeavyLayout layout, List<HeavyField> lists, HeavyVerifyPage page)
    {
        var raw = layout.Raw;
        foreach (var f in lists)
        {
            var mine = Enumerable.Range(0, raw.Subrecords.Count).Where(i => layout.Owner[i] == f.Name).ToList();
            bool oneRun = mine[^1] - mine[0] + 1 == mine.Count;
            if (oneRun)
                page.Shown.Add($"The {mine.Count:N0} subrecords of {f.Name} are one run in the file (subrecords {mine[0] + 1:N0} to {mine[^1] + 1:N0} of {raw.Subrecords.Count:N0}), and each part puts its items at that place among the other subrecords: the parts together are the record's bytes.");
            else
                page.Limits.Add($"The subrecords of {f.Name} are not one run in the file (the first is subrecord {mine[0] + 1:N0}, the last {mine[^1] + 1:N0}, and {mine[^1] - mine[0] + 1 - mine.Count:N0} subrecords of other fields stand between them): each part shows its own bytes, not that Mutagen writes the list back in the file's order.");
            if (f.Counter >= 0)
                page.Limits.Add($"The count of {f.Name} ({raw.Subrecords[f.Counter].Signature}) holds each part's count of items: the parts show it is written for the items there; the file's own count, {f.Items:N0}, is the number of items its subrecords cut into (checked when the record was laid out).");
        }
        foreach (var f in layout.Fields.Where(f => f.Big && !f.Paged))
            page.Shown.Add($"{f.Name} ({Sizes.Text(f.Bytes)}) can't be read a page at a time ({f.NotPaged}); every part holds it whole.");
        if (raw.Compressed)
            page.Limits.Add($"The record is compressed in the file ({Sizes.Text(raw.StoredSize)}): the parts are compared uncompressed, so they don't show that Mutagen writes the same compressed stream.");
    }
}

public sealed partial class Verifier
{
    /// <summary>
    /// Verifies a part read of a heavy record (<see cref="RecordEngine.ReadPart"/>): its JSON back to a record, written by
    /// Mutagen, compared with the part's own bytes (the kept subrecords, the header with the part's size).
    /// </summary>
    public VerifyResult VerifyPart(PartRead part)
    {
        var info = part.Layout.Info;
        var session = part.Record.Session;
        var item = new Item { Ref = part.Layout.Ref, Info = info, Result = new VerifyResult { Ref = part.Layout.Ref, Label = info.Label, Signature = info.Signature, Type = part.Record.Type } };
        try
        {
            var json = RecordSplit.WithoutChildren(_engine.Json(part.Record));
            var rebuilt = (IMajorRecord)RecordSerializer.FromJson(json.ToJsonString(), part.Record.Type);
            item.File = part.PartRaw;
            item.RawFormId = item.File.FormId;
            item.Rebuilt = rebuilt;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            var inner = e is System.Reflection.TargetInvocationException { InnerException: { } ie } ? ie : e;
            item.Result.Cause = $"{info.Signature}: the JSON couldn't be turned back into a record ({inner.GetType().Name})";
            item.Result.Detail = Short(inner.Message);
            return item.Result;
        }
        Write(session, new List<Item> { item });
        return item.Result;
    }
}
