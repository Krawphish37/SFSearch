// A component the byte readers don't read sends its record to Mutagen (GAPS S153).
// No plugin of the Data folder this was written on holds an unknown component (one whose BFCB text names no kind the
// schema has) or a block height adjustment's grid of surface blocks, so lists-all never meets one. Here a real
// activator's bytes get such a component added after its last one. The unknown one's own column (X_Components.TypeName)
// must then leave the record to Mutagen, and a reader that still answers must count the added component; the grid is
// read from the bytes (lists-all holds the real ones, of the surface patterns, against Mutagen's). As the file has the
// record, they answer from the bytes: no type name, no grid row.
// A component put after the record's other subrecords is a second run of components, which Mutagen's reader reads in
// the first one's place: every byte reader leaves such a record to Mutagen.
using System.Buffers.Binary;
using System.Text;
using SFSearch.Index;
using SFSearch.Query;

namespace SFSearch.Query.Tests;

static class ComponentFallbackTests
{
    static void Check(bool ok, string what) { if (!ok) throw new TestFailed(what); }
    const string Language = "English";

    static byte[] Sub(string sig, byte[] content)
    {
        var b = new byte[6 + content.Length];
        Encoding.ASCII.GetBytes(sig).CopyTo(b, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)content.Length);
        content.CopyTo(b, 6);
        return b;
    }

    static byte[] Component(string text, params byte[][] subrecords) =>
        Sub("BFCB", Encoding.ASCII.GetBytes(text + "\0")).Concat(subrecords.SelectMany(x => x)).Concat(Sub("BFCE", Array.Empty<byte>())).ToArray();

    /// <summary>The record's data with a component put in after its last one (-1 places: none found, or a subrecord of
    /// more than 65,535 bytes, which this doesn't walk).</summary>
    static byte[]? WithComponent(byte[] data, byte[] component)
    {
        int p = 0, after = -1;
        while (p + 6 <= data.Length)
        {
            string sig = Encoding.ASCII.GetString(data, p, 4);
            if (sig == "XXXX") return null;
            int n = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 4));
            p += 6 + n;
            if (sig == "BFCE") after = p;
        }
        if (after < 0 || p != data.Length) return null;
        return data[..after].Concat(component).Concat(data[after..]).ToArray();
    }

    public static string Run(Ctx c)
    {
        var p = c.Index.Plugin("Starfield.esm")!;
        var session = c.Records.Sessions.Get("Starfield.esm");
        var catalog = c.Engine.Views.Catalog("Activator");
        var components = catalog.Lists["Activator_Components"];
        var grid = catalog.Lists["Activator_Components_SurfaceBlocks"];
        var typeName = RawList.For(components, new[] { "ObjectType", "TypeName" }, c.Schema, catalog.Typed);
        var state = RawList.For(components, new[] { "ObjectType", "DAT2DataTypeState", "DAT2DataTypeStateRaw" }, c.Schema, catalog.Typed);
        var kinds = RawList.For(components, new[] { "ObjectType" }, c.Schema, catalog.Typed);
        var together = RawList.For(components, new[] { "ObjectType", "TypeName", "DAT2DataTypeState" }, c.Schema, catalog.Typed);
        var value = RawList.For(grid, new[] { "Value" }, c.Schema, catalog.Typed);
        Check(typeName != null && state != null && kinds != null && value != null && together != null,
            $"a byte reader is missing: TypeName {typeName != null}, DAT2DataTypeState {state != null}, ObjectType {kinds != null}, the grid's Value {value != null}, TypeName with DAT2DataTypeState {together != null}");

        // The first activators of Starfield.esm that have components
        var entries = p.Segment!.EntriesOf("ACTI").Take(400).ToArray();
        int tried = 0, rowsRead = 0, added = 0, secondRuns = 0;
        var plugin = new RawPlugin(p, session, Language);
        var problems = new List<string>();
        foreach (var _ in RecordPipeline.Read<string>(new[] { new ReadSet { Plugin = p, Entries = entries } }, (in PipelineRecord r, List<string> o) =>
        {
            if (r.Problem != PipelineProblem.None || tried >= 25 || r.Data.IndexOf("BFCB"u8) < 0) return;
            var head = r.Head.ToArray();
            var data = r.Data.ToArray();
            var rows = new List<object?[]>();
            // As the file has it: read from the bytes, no type name, no grid row
            if (!typeName!.Read(head, data, plugin, rows)) { problems.Add("TypeName isn't read from the bytes of an activator as the file has it"); return; }
            if (rows.Count == 0 || rows.Any(x => x[^1] != null)) { problems.Add($"an activator as the file has it: {rows.Count} components, a type name among them"); return; }
            int had = rows.Count;
            rowsRead += had;
            rows.Clear();
            if (!value!.Read(head, data, plugin, rows) || rows.Count != 0) { problems.Add($"an activator as the file has it: the grid read {rows.Count} rows, or wasn't read from the bytes"); return; }
            tried++;

            // With a component whose BFCB text names no kind (Mutagen's UnknownComponent): its type name is Mutagen's to
            // read, so that column leaves the record to Mutagen; a reader that still answers gives the added component
            // its row; the grid, which an unknown component doesn't have, still has no row
            var unknown = WithComponent(data, Component("Made_NoSuch_Component", Sub("DATA", new byte[] { 1, 2, 3, 4 })));
            var cells = new byte[16 * 16 * 8];
            for (int k = 0; k < 256; k++) BinaryPrimitives.WriteSingleLittleEndian(cells.AsSpan(k * 8 + 4), float.MinValue);
            BinaryPrimitives.WriteSingleLittleEndian(cells.AsSpan((2 * 16 + 1) * 8), 1.5f);
            var height = WithComponent(data, Component("BlockHeightAdjustment_Component", Sub("DAT2", cells)));
            if (unknown == null || height == null) return;
            rows.Clear();
            if (typeName.Read(head, unknown, plugin, rows)) problems.Add($"with an unknown component added, TypeName was read from the bytes ({rows.Count} rows)");
            rows.Clear();
            if (together!.Read(head, unknown, plugin, rows)) problems.Add($"with an unknown component added, TypeName with DAT2DataTypeState was read from the bytes ({rows.Count} rows)");
            rows.Clear();
            if (!together.Read(head, data, plugin, rows) || rows.Count != had || rows.Any(x => x[2] != null)) problems.Add($"an activator as the file has it: TypeName with DAT2DataTypeState gave {rows.Count} rows of {had}, or a type name, or wasn't read from the bytes");
            foreach (var (reader, what) in new[] { (state!, "DAT2DataTypeState"), (kinds!, "ObjectType") })
            {
                rows.Clear();
                if (reader.Read(head, unknown, plugin, rows) && (rows.Count != had + 1 || !rows.Any(x => (string?)x[1] == "UnknownComponent")))
                    problems.Add($"with an unknown component added, {what} was read from the bytes with {rows.Count} rows (expected {had + 1}, one an UnknownComponent)");
            }
            rows.Clear();
            if (!value.Read(head, unknown, plugin, rows) || rows.Count != 0) problems.Add($"with an unknown component added, the grid: {rows.Count} rows, or not read from the bytes");

            // With a block height adjustment (a 16 x 16 grid of two floats in DAT2: here every cell's water height the
            // lowest float, as the files have it, and one cell's terrain height 1.5): the grid's 16 rows, each the JSON
            // text of its 16 cells as the cell's own writer writes them; the components' own fields are still read
            rows.Clear();
            if (!value.Read(head, height, plugin, rows) || rows.Count != 16 || (long)rows[0][0]! != had || (long)rows[3][1]! != 3
                || (string?)rows[0][2] != "[" + string.Join(",", Enumerable.Repeat("{\"WaterHeight\":-3.4028235E+38}", 16)) + "]"
                || !((string?)rows[2][2] ?? "").StartsWith("[{\"WaterHeight\":-3.4028235E+38},{\"TerrainHeight\":1.5,\"WaterHeight\":-3.4028235E+38},", StringComparison.Ordinal))
                problems.Add($"with a block height adjustment added, the grid: {rows.Count} rows{(rows.Count > 2 ? ", the third " + ((string?)rows[2][2] ?? "").Substring(0, Math.Min(120, ((string?)rows[2][2] ?? "").Length)) : "")}");
            rows.Clear();
            if (!typeName.Read(head, height, plugin, rows) || rows.Count != had + 1 || rows.Any(x => x[^1] != null) || !rows.Any(x => (string?)x[1] == "BlockHeightAdjustmentComponent"))
                problems.Add($"with a block height adjustment added, the components' own fields: {rows.Count} rows (expected {had + 1}, one a BlockHeightAdjustmentComponent, no type name)");

            // A component after the record's other subrecords, a second run: Mutagen's reader sets the list to the run
            // it meets, so the second takes the first one's place. No byte reader follows that: each leaves the record
            // to Mutagen (when the record's last subrecord isn't a component's already)
            if (!data.AsSpan(data.Length - 6).StartsWith("BFCE"u8))
            {
                var second = data.Concat(Component("BGSKeywordForm_Component")).ToArray();
                foreach (var (reader, what) in new[] { (typeName, "TypeName"), (state!, "DAT2DataTypeState"), (kinds!, "ObjectType"), (value, "the grid") })
                {
                    rows.Clear();
                    if (reader.Read(head, second, plugin, rows)) problems.Add($"with a second run of components after the record's other subrecords, {what} was read from the bytes ({rows.Count} rows; the record has {had} components in its first run)");
                }
                secondRuns++;
            }
            added++;
        }, new PipelineOptions { Data = PipelineData.Plain })) { }
        Check(tried >= 10 && added >= 10 && secondRuns >= 5, $"only {tried} activators with components were tried, {added} with a component added, {secondRuns} with a second run of components");
        Check(problems.Count == 0, $"{problems.Count} problems: {string.Join(" | ", problems.Distinct().Take(4))}");
        return $"{tried} activators of Starfield.esm with components ({rowsRead} components): as the file has them, TypeName and the grid of surface blocks are read from the bytes (no type name, no grid row); with an unknown component added after the last, TypeName leaves the record to Mutagen, a reader that still answers counts the added component, and the grid still has no row; with a block height adjustment added, the grid's 16 rows are read from the bytes as its cells' own writer writes them and the components' own fields are still read; with a second run of components after the record's other subrecords ({secondRuns} records), every reader leaves the record to Mutagen";
    }
}
