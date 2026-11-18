// A placed object's NumTraversalFluffBytes from the bytes, where there are such bytes (GAPS S122).
// No placed object of the Data folder this was written on has fluff after its traversals (8,735 have traversals, in
// 113 plugins), so typed-all and world-reads only ever hold the byte reader's 0 against Mutagen's. Cells have it: 1,810
// of Starfield.esm's. A cell's XTV2 and a placed object's are read by the same code of Mutagen's
// (TraversalReferenceBinaryCreateTranslation.Parse), so here each cell's XTV2, as the file has it, is put into a real
// placed object's bytes, and the byte reader must give for it the count Mutagen gave for the cell. (The cells whose
// XTV2 is no more than 65,535 bytes and comes before any larger subrecord: 333 of them, 330 with fluff bytes.)
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SFSearch.Index;
using SFSearch.Query;

namespace SFSearch.Query.Tests;

static class TraversalFluffTests
{
    static void Check(bool ok, string what) { if (!ok) throw new TestFailed(what); }
    const string Language = "English";

    /// <summary>The content of the record's first subrecord of a signature; null when it has none, or a subrecord
    /// of more than 65,535 bytes comes first (which this doesn't walk).</summary>
    static byte[]? ContentOf(ReadOnlySpan<byte> data, string wanted)
    {
        int p = 0;
        while (p + 6 <= data.Length)
        {
            string sig = Encoding.ASCII.GetString(data.Slice(p, 4));
            if (sig == "XXXX") return null;
            int n = BinaryPrimitives.ReadUInt16LittleEndian(data[(p + 4)..]);
            if (p + 6 + n > data.Length) return null;
            if (sig == wanted) return data.Slice(p + 6, n).ToArray();
            p += 6 + n;
        }
        return null;
    }

    static long Number(object? o) => o == null ? 0 : Convert.ToInt64(o, CultureInfo.InvariantCulture);

    public static string Run(Ctx c)
    {
        var p = c.Index.Plugin("Starfield.esm")!;
        var session = c.Records.Sessions.Get("Starfield.esm");
        var plugin = new RawPlugin(p, session, Language);
        var cellView = c.Engine.Views.Catalog("Cell").Typed;
        var placedView = c.Engine.Views.Catalog("PlacedObject").Typed;
        var mutagen = DirectColumn.For(cellView.ByName["NumTraversalFluffBytes"], Language);
        Check(mutagen != null, "a cell's NumTraversalFluffBytes has no reader from Mutagen's object");
        var column = RawColumn.For(placedView, placedView.ByName["NumTraversalFluffBytes"], c.Schema);
        Check(column != null, "a placed object's NumTraversalFluffBytes has no byte reader");
        var reader = new RawRecordColumns(new[] { column! });

        // A placed object to carry them: the first of Starfield.esm that has no traversals and reads 0 from its bytes
        byte[]? head = null, host = null;
        var some = p.Segment!.EntriesOf("REFR").Take(2000).ToArray();
        foreach (var _ in RecordPipeline.Read<string>(new[] { new ReadSet { Plugin = p, Entries = some } }, (in PipelineRecord r, List<string> o) =>
        {
            if (r.Problem != PipelineProblem.None || r.Header.IsCompressed || r.Data.IndexOf("XTV2"u8) >= 0 || r.Data.IndexOf("XXXX"u8) >= 0) return;
            var one = new object?[1];
            if (!reader.Read(r.Head, r.Data, plugin, one) || Number(one[0]) != 0) return;
            lock (some) { if (host == null) { head = r.Head.ToArray(); host = r.Data.ToArray(); } }
        }, new PipelineOptions { Data = PipelineData.Plain, Threads = 1 })) { }
        Check(host != null, "no placed object without traversals among the first 2,000 of Starfield.esm reads 0 from its bytes");

        // Each cell that has an XTV2: Mutagen's count for the cell, and the byte reader's for the placed object with
        // the cell's XTV2 put after its own subrecords
        var cells = p.Segment.EntriesOf("CELL").ToArray();
        c.Records.OpenForThreads(cells.Select(e => new RecordRef(p.Id, e)));
        long tried = 0, withFluff = 0, most = 0;
        var problems = new List<string>();
        var gate = new object();
        foreach (var _ in RecordPipeline.Read<string>(new[] { new ReadSet { Plugin = p, Entries = cells } }, (in PipelineRecord r, List<string> o) =>
        {
            if (r.Problem != PipelineProblem.None || r.Data.IndexOf("XTV2"u8) < 0) return;
            if (ContentOf(r.Data, "XTV2") is not { } xtv2) return;
            long said = Number(mutagen!.Read(c.Records.Load(r.Ref).Record));
            var sub = new byte[6 + xtv2.Length];
            "XTV2"u8.CopyTo(sub);
            BinaryPrimitives.WriteUInt16LittleEndian(sub.AsSpan(4), (ushort)xtv2.Length);
            xtv2.CopyTo(sub, 6);
            var data = host!.Concat(sub).ToArray();
            var one = new object?[1];
            bool read = reader.Read(head!, data, plugin, one);
            lock (gate)
            {
                tried++;
                if (said > 0) withFluff++;
                most = Math.Max(most, said);
                if (!read) problems.Add($"{c.Index.FormKeyOf(r.Ref)}: its XTV2 ({xtv2.Length} bytes) in a placed object isn't read from the bytes (Mutagen's count for the cell: {said})");
                else if (Number(one[0]) != said) problems.Add($"{c.Index.FormKeyOf(r.Ref)}: its XTV2 ({xtv2.Length} bytes) in a placed object reads {Number(one[0])} fluff bytes, Mutagen's count for the cell is {said}");
            }
        }, new PipelineOptions { Data = PipelineData.Plain })) { }
        Check(problems.Count == 0, $"{problems.Count:N0} of {tried:N0} differ: {string.Join(" | ", problems.Take(4))}");
        Check(tried >= 200 && withFluff >= 200, $"only {tried:N0} cells with traversals were tried, {withFluff:N0} of them with fluff bytes");
        return $"{tried:N0} cells of Starfield.esm with traversals, {withFluff:N0} of them with fluff bytes after them (up to {most:N0}): each cell's XTV2 put into a placed object's bytes reads, from the bytes, the count Mutagen gave for the cell";
    }
}
