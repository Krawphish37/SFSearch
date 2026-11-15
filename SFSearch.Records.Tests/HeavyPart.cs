// Heavy records read in part (GAPS row S7, fix G2): a field of a heavy record is read through Mutagen from only the
// subrecords it needs, never the whole card, and gives Mutagen's own value of that field.
//  - heavy-list: every record of the Data folder over 16 MB of data, from the header index and the 4 bytes that give
//    a compressed record's inflated size. Nothing is read through Mutagen.
//  - heavy-part: with a test limit, the biggest form list of Starfield.esm is heavy. Its default part read, a page of
//    its items, one item far down the list and the evidence for it are each held against Mutagen's typed record read
//    whole. With --heavy (run with HEAVY_FULL=1): the same for Starfield.esm's navigation mesh info map, against
//    Mutagen's own overlay of it, which reads an item only when it is asked for.
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
        // The real heavy records, with the real limit
        return ctx.Heavy ? $"{flst} {Navi(ctx)} {Pers(ctx)}" : flst;
    }

    /// <summary>
    /// BlueprintShips-Starfield.esm's PERS (17 MB): its Items are read by Mutagen's hand-written code, so no schema row
    /// names their DAT2 subrecords; the layout takes DAT2 to be theirs. An item read in part against Mutagen's read of
    /// the whole record.
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
        Check(items is { Big: true, ItemSignature: "DAT2", Guessed: true }, $"Items is {items?.ItemSignature} (big: {items?.Big}, taken: {items?.Guessed})");
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
