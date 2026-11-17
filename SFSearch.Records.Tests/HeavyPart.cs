// Heavy records read in part (GAPS row S7, fix G2): a field of a heavy record is read through Mutagen from only the
// subrecords it needs, never the whole card, and gives Mutagen's own value of that field.
//  - heavy-list: every record of the Data folder over 16 MB of data, from the header index and the 4 bytes that give
//    a compressed record's inflated size. Nothing is read through Mutagen.
//  - heavy-part: with a test limit, the biggest form list of Starfield.esm is heavy. Its default part read, a page of
//    its items, one item far down the list and the evidence for it are each held against Mutagen's typed record read
//    whole. With --heavy (run with HEAVY_FULL=1): the same for Starfield.esm's navigation mesh info map, against
//    Mutagen's own overlay of it, which reads an item only when it is asked for. The PERS of BlueprintShips-Starfield.esm:
//    its layout (from its bytes) gives its DAT2 subrecords to Items by the schema (GAPS S38); with --heavy an item read
//    in part against Mutagen's read of the whole record.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32.SafeHandles;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Records.Tests;

static class HeavyPart
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    public static string List(Ctx ctx)
    {
        var ix = ctx.DataIndex;
        long limit = ctx.Engine.HeavyRecordBytes;
        var found = new List<string>();
        var four = new byte[4];
        long records = 0, compressed = 0;
        foreach (var p in ix.Plugins)
        {
            var seg = p.Segment;
            if (seg == null) continue;
            SafeFileHandle? file = null;
            try
            {
                for (int i = 0; i < seg.Count; i++)
                {
                    var e = seg[i];
                    records++;
                    long bytes = e.Size;
                    if (e.IsCompressed && e.Size >= 4)
                    {
                        compressed++;
                        file ??= File.OpenHandle(p.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        RandomAccess.Read(file, four, e.Offset + RawRecord.HeaderLength);
                        bytes = BinaryPrimitives.ReadUInt32LittleEndian(four);
                    }
                    if (bytes <= limit) continue;
                    var d = ix.Describe(new RecordRef(p.Id, i));
                    found.Add($"{d.Label} {d.Signature} in {p.Name}: {N(bytes)} bytes of data ({N(e.Size)} in the file{(e.IsCompressed ? ", compressed" : "")})");
                }
            }
            finally { file?.Dispose(); }
        }
        ctx.WriteReport("heavy-list.txt", found);
        foreach (var f in found) Console.WriteLine("      " + f);
        Check(found.Count > 0, "no record of the Data folder is over the limit: the NAVI of Starfield.esm should be");
        return $"{found.Count} record(s) over {Sizes.Text(limit)} of data among {N(records)} records of {ix.Plugins.Count} plugins ({N(compressed)} compressed ones had their inflated size read)";
    }

    public static string Run(Ctx ctx)
    {
        var e = ctx.Engine;
        e.Sessions.Use("Starfield.esm");
        var sf = ctx.DataIndex.Plugin("Starfield.esm")!;
        var seg = sf.Segment!;
        // Of the 40 form lists with the most data, the one with the most items
        var biggest = Enumerable.Range(0, seg.Count).Where(i => seg.SignatureOf(seg[i]) == "FLST").OrderByDescending(i => seg[i].Size).Take(40).ToList();
        Check(biggest.Count > 0, "Starfield.esm has no FLST");
        int best = biggest.MaxBy(i => ((IFormListGetter)e.Load(new RecordRef(sf.Id, i)).Record).Items.Count);
        var r = new RecordRef(sf.Id, best);
        var info = ctx.DataIndex.Describe(r);

        long limit = e.HeavyRecordBytes, fieldLimit = e.HeavyPartFieldBytes;
        string flst;
        try
        {
            // Mutagen's typed record, read whole, before the test limit makes it heavy
            var whole = (IFormListGetter)e.Load(r).Record;
            var items = whole.Items;
            Check(items.Count >= 100, $"{info.Label} has only {items.Count} items");
            long data = e.DataBytes(r);
            e.HeavyRecordBytes = data / 2;
            e.HeavyPartFieldBytes = 1024;
            Check(e.IsHeavy(r), $"{info.Label} isn't heavy with the test limit");
            bool refused = false;
            try { e.Load(r); } catch (RecordException) { refused = true; }
            Check(refused, "Load read the heavy record whole without allow_heavy");

            var layout = e.Layout(r);
            var list = layout.Field("Items");
            Check(list is { Big: true, ItemSignature: "LNAM" } && list.Items == items.Count, $"the layout gives Items as {list?.Items} {list?.ItemSignature} items (big: {list?.Big}); Mutagen has {items.Count}");

            // The default part read: every field but the big list, which is left out
            var part = e.ReadPart(layout, new Dictionary<string, IReadOnlyCollection<int>?>());
            var card = e.PartCard(part, withDefaults: true);
            Check(part.Left.Count == 1 && part.Left[0].Name == "Items", $"left out: {string.Join(", ", part.Left.Select(f => f.Name))}");
            Check(card.Fields.All(f => f.Name != "Items"), "the default part read has Items");
            var edid = FieldLookup.Fields(e.Schema, card, new[] { "EditorID" })[0];
            Check(edid.Text == whole.EditorID, $"EditorID {edid.Text}, Mutagen {whole.EditorID}");

            // A page in the middle of the list: each item is Mutagen's
            int from = items.Count / 3, count = 25;
            part = e.ReadPart(layout, new Dictionary<string, IReadOnlyCollection<int>?> { ["Items"] = Enumerable.Range(from, count).ToArray() });
            card = e.PartCard(part);
            Check(part.KeptBytes < data / 4, $"the page read {N(part.KeptBytes)} of {N(data)} bytes");
            var names = Enumerable.Range(from, count).Select(k => $"Items[{k}]").ToList();
            var values = FieldLookup.Fields(e.Schema, card, names);
            for (int i = 0; i < count; i++)
            {
                string want = items[from + i].FormKey.ToString(), got = values[i].Json?.GetValue<string>() ?? "";
                Check(values[i].Path == names[i] && got == want, $"{names[i]}: the part read gives {values[i].Path} = {got}, Mutagen {want}");
            }
            bool past = false;
            try { FieldLookup.Fields(e.Schema, card, new[] { $"Items[{from + count}]" }); } catch (RecordException) { past = true; }
            Check(past, "an item outside the page was answered");

            // One item far down the list, and its evidence: the slice is that item's FormID, counted in the whole record
            int last = items.Count - 1;
            part = e.ReadPart(layout, new Dictionary<string, IReadOnlyCollection<int>?> { ["Items"] = new[] { last } });
            card = e.PartCard(part);
            var one = FieldLookup.Fields(e.Schema, card, new[] { $"Items[{last}]" })[0];
            Check(one.Json?.GetValue<string>() == items[last].FormKey.ToString(), $"Items[{last}] = {one.Text}, Mutagen {items[last].FormKey}");
            var ev = e.PartEvidence(part, card, $"Items[{last}]");
            Check(ev.Subrecord == "LNAM" && ev.Occurrences == items.Count && ev.Slices.Count == 1 && ev.Slices[0].Occurrence == last,
                $"evidence: {ev.Subrecord}, {ev.Occurrences} occurrences, slice of occurrence {ev.Slices.FirstOrDefault()?.Occurrence}");
            Check(ev.Slices[0].Reads?.EndsWith("(" + items[last].FormKey + ")", StringComparison.Ordinal) == true, $"the slice reads {ev.Slices[0].Reads}, Mutagen {items[last].FormKey}");
            var lnam = layout.Raw.Subrecords.Where(s => s.Signature == "LNAM").ElementAt(last);
            Check(ev.Slices[0].FieldBytes == RawRecord.Hex(layout.Raw.Content(lnam)), $"the slice {ev.Slices[0].FieldBytes} isn't the {N(last + 1)}th LNAM of the record");

            flst = $"{info.Label} ({N(data)} bytes, {N(items.Count)} items; test limit {N(e.HeavyRecordBytes)}): default read without Items, a page of {count} items from [{from}], and Items[{last}] with its evidence, each equal to Mutagen's.";
        }
        finally { e.HeavyRecordBytes = limit; e.HeavyPartFieldBytes = fieldLimit; }
        // The json, the card and verify a part at a time, against the same views of the whole record (fix J7)
        string views = Views(ctx, r, "Items");
        string lvl = Counted(ctx);
        string cont = Container(ctx);
        string value = OneValue(ctx);
        // The PERS's layout (its bytes only, nothing read through Mutagen), then the real heavy records with the real limit
        string pers = PersLayout(ctx);
        return ctx.Heavy ? $"{flst} {views} {lvl} {cont} {value} {pers} {Navi(ctx)} {Pers(ctx)}" : $"{flst} {views} {lvl} {cont} {value} {pers}";
    }

    static List<string> Lines(string text) => text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();

    /// <summary>The first line where two texts differ, in words, or null when they are the same.</summary>
    static string? Diff(List<string> want, List<string> got)
    {
        for (int i = 0; i < Math.Max(want.Count, got.Count); i++)
        {
            string? a = i < want.Count ? want[i] : null, b = i < got.Count ? got[i] : null;
            if (a != b) return $"line {i + 1} of {want.Count}: whole \"{a ?? "(end)"}\", in parts \"{b ?? "(end)"}\" ({got.Count} lines in parts)";
        }
        return null;
    }

    /// <summary>
    /// The json, card and verify views of a record made heavy by a test limit, a part at a time (fix J7), against the
    /// same views of the record read whole: the pages put together are the whole record's JSON and card line for line,
    /// and every verify part writes back to its own bytes. Small pages and batches, so that many pages, batches, parts
    /// cut inside an item and verify calls are made.
    /// </summary>
    static string Views(Ctx ctx, RecordRef r, string list, long fieldLimit = 512)
    {
        var e = ctx.Engine;
        var info = ctx.DataIndex.Describe(r);
        // The whole record, before the test limit
        var loaded = e.Load(r);
        var json = e.Json(loaded);
        var wantJson = Lines(json.ToJsonString(RecordEngine.PrettyJson));
        var card = e.Card(loaded, json, withDefaults: true);
        card.Notes.Clear();
        var wantCard = Lines(card.ToText(int.MaxValue, 64)).Skip(2).ToList();
        bool wholeEqual = e.Verify(new[] { r })[0].Equal;
        long data = e.DataBytes(r);
        var saved = (e.HeavyRecordBytes, e.HeavyPartFieldBytes, e.HeavyProbeItems, e.HeavyBatchBytes, e.HeavyVerifyBytes);
        try
        {
            e.HeavyRecordBytes = data - 1; e.HeavyPartFieldBytes = Math.Min(fieldLimit, data / 4);
            e.HeavyProbeItems = 3; e.HeavyBatchBytes = Math.Max(64, data / 10); e.HeavyVerifyBytes = data / 3;
            var layout = e.Layout(r);
            var f = layout.Field(list);
            Check(f is { Big: true, Paged: true }, $"{list} of {info.Label} isn't a big list that can be paged: {f?.NotPaged}");
            var said = new List<string>();
            foreach (var kind in new[] { "json", "card" })
            {
                var want = kind == "json" ? wantJson : wantCard;
                var text = e.HeavyText(layout, kind);
                var got = new List<string>();
                int pages = 0, part = 0, line = 0, cut = 0, reads = 0;
                long most = 0;
                while (true)
                {
                    var page = e.HeavyTextPage(text, part, line, 1_500);
                    pages++; reads += page.Reads; most = Math.Max(most, page.ReadBytes);
                    if (page.NextLine > 0) cut++;
                    Check(page.Lines.Count > 0, $"{kind}: page {pages} from part {part}, line {line} is empty");
                    got.AddRange(page.Lines);
                    if (page.End) break;
                    Check(page.NextPart > part || page.NextLine > line, $"{kind}: page {pages} doesn't move on");
                    (part, line) = (page.NextPart, page.NextLine);
                }
                Check(Diff(want, got) is null, $"{kind} of {info.Label} in parts differs from the whole record's: {Diff(want, got)}");
                Check(text.Parts > f!.Items && most < data, $"{kind}: {text.Parts} parts for {f.Items} items; a page read {N(most)} of the record's {N(data)} bytes");
                said.Add($"its {kind} in {pages} pages ({N(want.Count)} lines, {reads} part reads, {cut} pages ending inside a part) is the whole record's line for line");
            }
            // Verify in parts, call after call, against verify of the whole record
            int unit = 0, calls = 0, parts = 0;
            var limits = new List<string>();
            while (unit >= 0)
            {
                var v = e.VerifyParts(layout, unit);
                calls++; parts += v.Parts.Count;
                foreach (var p in v.Parts) Check(p.Result.Equal, $"verify part {p.What}: {p.Result.ToText()} {p.Where}");
                limits = v.Limits;
                Check(v.Shown.Any(x => x.Contains("one run in the file", StringComparison.Ordinal)), $"verify: {string.Join(" ", v.Shown.Concat(v.Limits))}");
                unit = v.NextUnit;
            }
            Check(wholeEqual && parts > 1 && calls > 1, $"verify: whole {wholeEqual}, {parts} parts in {calls} calls");
            said.Add($"verify in {parts} parts over {calls} calls, each equal, as the whole record is{(limits.Count > 0 ? $" ({limits.Count} thing(s) the parts say they can't show)" : "")}");
            return $"{info.Label}, {list} ({N(f!.Items)} items, {(f.Counter >= 0 ? "counted, " : "")}{(f.ItemSignature != null ? "one subrecord an item" : "several subrecords an item")}): {string.Join("; ", said)}.";
        }
        finally { (e.HeavyRecordBytes, e.HeavyPartFieldBytes, e.HeavyProbeItems, e.HeavyBatchBytes, e.HeavyVerifyBytes) = saved; }
    }

    /// <summary>
    /// A list with a count of its own and items of several subrecords (a leveled list's Entries: LLCT, then LVLO and
    /// what follows it): of Starfield.esm's 40 biggest leveled lists, the one with the most entries. Its layout cuts
    /// the list into Mutagen's items with the count, a page read writes the page's count, each item read in part is
    /// Mutagen's, and the json, card and verify views in parts are the whole record's.
    /// </summary>
    static string Counted(Ctx ctx)
    {
        var e = ctx.Engine;
        e.Sessions.Use("Starfield.esm");
        var sf = ctx.DataIndex.Plugin("Starfield.esm")!;
        var seg = sf.Segment!;
        var candidates = Enumerable.Range(0, seg.Count).Where(i => seg.SignatureOf(seg[i]) is "LVLI" or "LVLN").OrderByDescending(i => seg[i].Size).Take(40).ToList();
        Check(candidates.Count > 0, "Starfield.esm has no leveled list");
        // The most entries; among equals, the one with the most subrecords
        var best = candidates.Select(i => (i, json: e.Json(e.Load(new RecordRef(sf.Id, i))), raw: e.Raw(new RecordRef(sf.Id, i))))
            .OrderByDescending(x => (x.json["Entries"] as System.Text.Json.Nodes.JsonArray)?.Count ?? 0).ThenByDescending(x => x.raw.Subrecords.Count).First();
        var r = new RecordRef(sf.Id, best.i);
        var info = ctx.DataIndex.Describe(r);
        var wantItems = (System.Text.Json.Nodes.JsonArray)best.json["Entries"]!;
        long data = e.DataBytes(r);
        var saved = (e.HeavyRecordBytes, e.HeavyPartFieldBytes);
        string what;
        try
        {
            e.HeavyRecordBytes = data / 2; e.HeavyPartFieldBytes = 256;
            var layout = e.Layout(r);
            var f = layout.Field("Entries");
            Check(f is { Big: true, Paged: true } && f.Counter >= 0 && layout.Raw.Subrecords[f.Counter].Signature == "LLCT" && f.Items == wantItems.Count,
                $"Entries of {info.Label}: big {f?.Big}, paged {f?.Paged} ({f?.NotPaged}), count {(f?.Counter >= 0 ? layout.Raw.Subrecords[f.Counter].Signature : "none")}, {f?.Items} items of Mutagen's {wantItems.Count}");
            int multi = f!.Runs!.Count(x => x.End - x.Start > 1);
            // A page in the middle: its count is the page's, and each item is Mutagen's
            int from = f.Items / 3, count = Math.Min(7, f.Items - from);
            var part = e.ReadPart(layout, new Dictionary<string, IReadOnlyCollection<int>?> { ["Entries"] = Enumerable.Range(from, count).ToArray() });
            var llct = part.PartRaw.Subrecords.Single(s => s.Signature == "LLCT");
            Check(part.PartRaw.Content(llct)[0] == count && part.Recounted.Count == 1, $"the page's LLCT holds {part.PartRaw.Content(llct)[0]}, the page has {count} entries");
            var got = (System.Text.Json.Nodes.JsonArray)e.Json(part.Record)["Entries"]!;
            for (int i = 0; i < count; i++)
                Check(System.Text.Json.Nodes.JsonNode.DeepEquals(got[i], wantItems[from + i]), $"Entries[{from + i}] read in part: {got[i]?.ToJsonString()}, Mutagen whole: {wantItems[from + i]?.ToJsonString()}");
            // Evidence for a field of an item on the page: its LVLO, counted in the whole record
            var card = e.PartCard(part);
            int k = from + count - 1;
            var ev = e.PartEvidence(part, card, $"Entries[{k}].Reference");
            int lvlo = layout.Raw.Subrecords.Take(f.Runs![k].Start + 1).Count(s => s.Signature == "LVLO") - 1;
            Check(ev.Subrecord == "LVLO" && ev.Occurrences == layout.Raw.Subrecords.Count(s => s.Signature == "LVLO") && ev.Slices.Any(x => x.Occurrence == lvlo),
                $"evidence for Entries[{k}].Reference: {ev.Subrecord}, {ev.Occurrences} occurrences, slices of occurrences {string.Join(",", ev.Slices.Select(x => x.Occurrence))}; the item's LVLO is occurrence {lvlo}");
            what = $"{info.Label} ({N(data)} bytes): Entries is {N(f.Items)} items behind an LLCT count ({multi} of several subrecords); a page of {count} from [{from}] writes LLCT {count}, gives Mutagen's items, and the evidence of Entries[{k}] is LVLO occurrence {lvlo} of the whole record";
        }
        finally { (e.HeavyRecordBytes, e.HeavyPartFieldBytes) = saved; }
        return $"{what}. {Views(ctx, r, "Entries")}";
    }

    /// <summary>
    /// A container's Items (COCT, then for each item a CNTO and, when set, a COED): Starfield.esm's container with the
    /// most items, made heavy. Its layout cuts the list at each CNTO, as many items as
    /// COCT says, and its json, card and verify in parts are the whole record's.
    /// </summary>
    static string Container(Ctx ctx)
    {
        var e = ctx.Engine;
        e.Sessions.Use("Starfield.esm");
        var sf = ctx.DataIndex.Plugin("Starfield.esm")!;
        var seg = sf.Segment!;
        // The container with the most items (from the bytes only; Starfield.esm's containers give no item a COED), then
        // read through Mutagen
        var conts = Enumerable.Range(0, seg.Count).Where(i => seg.SignatureOf(seg[i]) == "CONT").Select(i => (i, raw: e.Raw(new RecordRef(sf.Id, i))))
            .ToList();
        Check(conts.Count > 0, "Starfield.esm has no container");
        var pickRaw = conts.MaxBy(x => x.raw.Subrecords.Count(s => s.Signature == "CNTO"));
        var best = (pickRaw.i, json: e.Json(e.Load(new RecordRef(sf.Id, pickRaw.i))));
        var r = new RecordRef(sf.Id, best.i);
        var info = ctx.DataIndex.Describe(r);
        int want = (best.json["Items"] as System.Text.Json.Nodes.JsonArray)?.Count ?? 0;
        long data = e.DataBytes(r);
        var saved = (e.HeavyRecordBytes, e.HeavyPartFieldBytes);
        string what;
        try
        {
            e.HeavyRecordBytes = data - 1; e.HeavyPartFieldBytes = 64;
            var layout = e.Layout(r);
            var f = layout.Field("Items");
            Check(f is { Big: true, Paged: true } && f.Counter >= 0 && layout.Raw.Subrecords[f.Counter].Signature == "COCT" && f.Items == want,
                $"Items of {info.Label}: big {f?.Big}, paged {f?.Paged} ({f?.NotPaged}), {f?.Items} items of Mutagen's {want}");
            int multi = f!.Runs!.Count(x => x.End - x.Start > 1);
            what = $"{info.Label} ({N(data)} bytes): Items is {N(f.Items)} items behind a COCT count, cut at each CNTO ({multi} with a COED)";
        }
        finally { (e.HeavyRecordBytes, e.HeavyPartFieldBytes) = saved; }
        return $"{what}. {Views(ctx, r, "Items", 64)}";
    }

    /// <summary>
    /// A big field that is one value, not a list (a navigation mesh's NVNM, its geometry): made heavy with a test limit,
    /// the layout says it can't be read a page at a time and why, and the json and card in parts refuse it plainly,
    /// naming it, its size and that reason, while the fields that are small are still read.
    /// </summary>
    static string OneValue(Ctx ctx)
    {
        var e = ctx.Engine;
        e.Sessions.Use("Starfield.esm");
        var sf = ctx.DataIndex.Plugin("Starfield.esm")!;
        var seg = sf.Segment!;
        int pick = Enumerable.Range(0, seg.Count).First(i => seg.SignatureOf(seg[i]) == "NAVM" && seg[i].Size is > 5_000 and < 60_000);
        var r = new RecordRef(sf.Id, pick);
        var info = ctx.DataIndex.Describe(r);
        long data = e.DataBytes(r);
        var saved = (e.HeavyRecordBytes, e.HeavyPartFieldBytes);
        try
        {
            e.HeavyRecordBytes = data / 2; e.HeavyPartFieldBytes = 1024;
            var layout = e.Layout(r);
            var big = layout.Fields.Where(f => f.Big).ToList();
            Check(big.Count == 1 && !big[0].Paged && big[0].NotPaged?.Contains("one value", StringComparison.Ordinal) == true, $"big fields of {info.Label}: {string.Join("; ", big.Select(f => $"{f.Name} paged {f.Paged}: {f.NotPaged}"))}");
            string? refused = null;
            try { e.HeavyText(layout, "json"); } catch (RecordException x) { refused = x.Message; }
            Check(refused != null && refused.Contains(big[0].Name, StringComparison.Ordinal) && refused.Contains("can't be read in parts", StringComparison.Ordinal), $"the json in parts of {info.Label}: {refused ?? "not refused"}");
            var part = e.ReadPart(layout, new Dictionary<string, IReadOnlyCollection<int>?>());
            Check(part.Left.Count == 1 && e.PartCard(part).Fields.Count > 0, "the small fields aren't read without the big one");
            return $"{info.Label} ({N(data)} bytes, test limit {N(e.HeavyRecordBytes)}): {big[0].Name} ({N(big[0].Bytes)} bytes) can't be read a page at a time ({big[0].NotPaged}); the json in parts says so: \"{refused}\"";
        }
        finally { (e.HeavyRecordBytes, e.HeavyPartFieldBytes) = saved; }
    }

    /// <summary>
    /// The layout of BlueprintShips-Starfield.esm's PERS (17 MB), from its bytes: its Items are filled by Mutagen's
    /// hand-written code (the CustomLogic step ItemsParse), and the schema names that step's subrecords (XXXX, DAT2:
    /// GAPS S38), so the layout gives DAT2 to Items from the schema, with no rule of its own. The record's other
    /// signature, DATA, is the schema's DATA field.
    /// </summary>
    static string PersLayout(Ctx ctx)
    {
        var e = ctx.Engine;
        var row = e.Schema.Model.Objects["PERS"].Fields.Single(f => f.Path == "Items");
        Check(row.CustomSubrecords == "XXXX,DAT2" && row.FilledBy == "ItemsParse", $"the schema gives PERS.Items the subrecords {row.CustomSubrecords}, filled by {row.FilledBy}");
        if (ctx.DataIndex.Plugin("BlueprintShips-Starfield.esm") == null) return "(BlueprintShips-Starfield.esm isn't in Data: the PERS layout is skipped.)";
        e.Sessions.Use("BlueprintShips-Starfield.esm");
        var r = e.Find("BlueprintShips-Starfield.esm", "0CB015");
        Check(e.IsHeavy(r), "the PERS isn't heavy");
        var layout = e.Layout(r);
        var items = layout.Field("Items");
        Check(items is { Big: true, ItemSignature: "DAT2", FilledBy: "ItemsParse" } && items.Signatures.SequenceEqual(new[] { "DAT2" }),
            $"Items is {items?.ItemSignature} (big: {items?.Big}, filled by: {items?.FilledBy}, signatures {string.Join(",", items?.Signatures ?? new())})");
        int dat2 = layout.Raw.Subrecords.Count(s => s.Signature == "DAT2");
        Check(items!.Items == dat2 && layout.Signatures.All(s => s.Field != null), $"Items has {items.Items} items of {dat2} DAT2; signatures with no field: {string.Join(", ", layout.Signatures.Where(s => s.Field == null).Select(s => s.Signature))}");
        return $"The PERS of BlueprintShips-Starfield.esm: the schema's subrecords give Items its {N(items.Items)} DAT2 subrecords (ItemsParse), every signature has its field.";
    }

    /// <summary>
    /// BlueprintShips-Starfield.esm's PERS (17 MB), read whole: an item read in part (from one DAT2, given to Items by
    /// the schema) against Mutagen's read of the whole record.
    /// </summary>
    static string Pers(Ctx ctx)
    {
        var e = ctx.Engine;
        if (ctx.DataIndex.Plugin("BlueprintShips-Starfield.esm") == null) return "(BlueprintShips-Starfield.esm isn't in Data: the PERS case is skipped.)";
        e.Sessions.Use("BlueprintShips-Starfield.esm");
        var r = e.Find("BlueprintShips-Starfield.esm", "0CB015");
        Check(e.IsHeavy(r), "the PERS isn't heavy");
        var layout = e.Layout(r);
        var items = layout.Field("Items");
        Check(items is { Big: true, ItemSignature: "DAT2", FilledBy: "ItemsParse" }, $"Items is {items?.ItemSignature} (big: {items?.Big}, filled by: {items?.FilledBy})");
        int k = items!.Items - 1;
        var part = e.ReadPart(layout, new Dictionary<string, IReadOnlyCollection<int>?> { ["Items"] = new[] { k } });
        var card = e.PartCard(part);
        var v = FieldLookup.Fields(e.Schema, card, new[] { $"Items[{k}].Items[3].Record", $"Items[{k}].Items[3].Unknown1" });
        bool allow = e.AllowHeavy;
        e.AllowHeavy = true;
        try
        {
            var pers = (IPERSGetter)e.Load(r).Record;
            Check(pers.Items!.Count == items.Items, $"Mutagen has {pers.Items.Count} items, the layout {items.Items}");
            var item = pers.Items[k].Items[3];
            Check(v[0].Json?.GetValue<string>() == item.Record.FormKey.ToString(), $"Items[{k}].Items[3].Record = {v[0].Json}, Mutagen {item.Record.FormKey}");
            Check(v[1].Json?.ToJsonString() == item.Unknown1.ToString(CultureInfo.InvariantCulture), $"Items[{k}].Items[3].Unknown1 = {v[1].Json}, Mutagen {item.Unknown1}");
        }
        finally { e.AllowHeavy = allow; }
        return $"The PERS of BlueprintShips-Starfield.esm: Items[{k}].Items[3] of {N(items.Items)} DAT2 items read in part, equal to Mutagen's.";
    }

    /// <summary>The navigation mesh info map of Starfield.esm, against Mutagen's overlay of it.</summary>
    static string Navi(Ctx ctx)
    {
        var e = ctx.Engine;
        var r = e.Find("Starfield.esm", "000FF1");
        Check(e.IsHeavy(r), "the NAVI isn't heavy");
        var proc = Process.GetCurrentProcess();
        var sw = Stopwatch.StartNew();
        var layout = e.Layout(r);
        var maps = layout.Field("MapInfos");
        Check(maps is { Big: true, ItemSignature: "NVMI" }, $"MapInfos is {maps?.ItemSignature} (big: {maps?.Big})");
        int k = maps!.Items - 7;
        var part = e.ReadPart(layout, new Dictionary<string, IReadOnlyCollection<int>?> { ["MapInfos"] = new[] { k } });
        var card = e.PartCard(part);
        var v = FieldLookup.Fields(e.Schema, card, new[] { $"MapInfos[{k}].NavigationMesh", $"MapInfos[{k}].ApproxLocation" });
        var ev = e.PartEvidence(part, card, $"MapInfos[{k}].NavigationMesh");
        double partSeconds = sw.Elapsed.TotalSeconds;
        proc.Refresh();
        long partPeak = proc.PeakWorkingSet64;

        // Mutagen's overlay of the whole record: it parses an item only when it is asked for
        bool allow = e.AllowHeavy;
        e.AllowHeavy = true;
        try
        {
            var navi = (INavigationMeshInfoMapGetter)e.Load(r).Record;
            Check(navi.MapInfos.Count == maps.Items, $"Mutagen has {navi.MapInfos.Count} map infos, the layout {maps.Items}");
            var item = navi.MapInfos[k];
            Check(v[0].Json?.GetValue<string>() == item.NavigationMesh.FormKey.ToString(), $"MapInfos[{k}].NavigationMesh = {v[0].Json}, Mutagen {item.NavigationMesh.FormKey}");
            var xyz = (v[1].Json?.GetValue<string>() ?? "").Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray();
            Check(xyz.Length == 3 && xyz[0] == item.ApproxLocation.X && xyz[1] == item.ApproxLocation.Y && xyz[2] == item.ApproxLocation.Z, $"MapInfos[{k}].ApproxLocation = {v[1].Json}, Mutagen {item.ApproxLocation}");
            Check(ev.Slices.Count == 1 && ev.Slices[0].Occurrence == k && ev.Occurrences == maps.Items && ev.Slices[0].Reads?.EndsWith("(" + item.NavigationMesh.FormKey + ")", StringComparison.Ordinal) == true,
                $"evidence: occurrence {ev.Slices.FirstOrDefault()?.Occurrence} of {ev.Occurrences}, reads {ev.Slices.FirstOrDefault()?.Reads}");
        }
        finally { e.AllowHeavy = allow; }
        return $"The NAVI: MapInfos[{k}] of {N(maps.Items)} read in part in {partSeconds:F1} s (peak working set {N(partPeak >> 20)} MB by then), equal to Mutagen's overlay, evidence included.";
    }
}
