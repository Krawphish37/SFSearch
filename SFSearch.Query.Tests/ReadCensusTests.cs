// The census of every record type (GAPS S122): each typed column and each list view's column, by how it is read when a
// query names it alone: from the record's own bytes, straight from Mutagen's object, or through the record's JSON.
// world-reads' census says the same for its 38 types; this one is all of them, with what the schema says of each field
// that isn't read from the bytes (its subrecord, place, kind, what reads it), so that the next thing to move is found by
// reading a file. It reads no record: 25 s. The numbers it gives are the figure to watch from one change to the next.
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class ReadCensusTests
{
    public static string Run(Ctx c)
    {
        const string T = "\t";
        var typedLines = new List<string> { string.Join(T, "Type", "Column", "Kind", "How", "Walk", "RelPath", "Subrecord", "Offset", "Size", "FieldKind", "Custom", "InList", "Marker", "Derived", "LayoutOffset", "LayoutEncoding", "LayoutBits", "LayoutPresentWhen", "FilledBy", "Default", "Nullable", "Translated", "TextForm", "UnderAKind", "Parents", "Object", "Note") };
        var listLines = new List<string> { string.Join(T, "Type", "View", "Column", "Kind", "How", "Walk", "RelPath", "Subrecord", "FieldKind", "Custom", "Object") };
        int bytes = 0, mutagen = 0, json = 0, listBytes = 0, listMutagen = 0, listJson = 0, types = 0, shared = 0;
        foreach (string root in c.Engine.Views.RootNames.OrderBy(r => r, StringComparer.Ordinal))
        {
            var cat = c.Engine.Views.Catalog(root);
            var v = cat.Typed;
            if (v.IsUnion || v.Types.Count != 1) continue;
            types++;
            if (c.Schema.SignatureOf(v.Types[0]) is { } sig && c.Schema.TypesOf(sig).Count != 1) shared++;
            foreach (var col in v.Columns)
            {
                var how = WorldReadTests.Typed(c, v, col);
                if (how == WorldReadTests.How.Bytes) { bytes++; continue; }
                if (how == WorldReadTests.How.Json) json++; else mutagen++;
                var row = col.Row ?? col.FirstRow; var d = row?.Def;
                // The objects on the field's way: C read by Mutagen's own code, L in a list, M at a marker, ? may be absent
                string parents = "";
                for (var up = row?.Parent; up != null; up = up.Parent)
                    parents += (up.Def.Custom ? "C" : "-") + (up.InList ? "L" : "") + (up.Def.Marker != null ? "M" : "") + (up.Def.Nullable ? "?" : "") + ":" + up.Def.Name
                        + (up.Def.RefObject is { } ro && c.Schema.Model.HandReadOf(ro) != null ? "(hand-read)" : "") + " ";
                // Why the record's value walk doesn't read the column (RawList.TypedWalk.For)
                string? walk = null;
                RawList.TypedWalk.For(v.Types[0], col, c.Schema, out walk);
                typedLines.Add(string.Join(T, root, col.Name, col.Kind, how, walk, col.RelPath, row?.Subrecord, row?.Offset, d?.Size, d?.Kind, d?.Custom, row?.InList, d?.Marker,
                    d?.Source == null ? "" : "derived", d?.LayoutOffset, d?.LayoutEncoding, d?.LayoutBits, d?.LayoutPresentWhen, d?.FilledBy, d?.Default, d?.Nullable, d?.Translated,
                    d?.TextForm, !col.FromBase || col.VariantGuards.Count > 0, parents.Trim(), d?.Object, d?.Note?.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')));
            }
            foreach (var lv in cat.Lists.Values)
                foreach (var col in lv.Columns)
                {
                    var how = WorldReadTests.List(c, lv, col.Name);
                    if (how == WorldReadTests.How.Bytes) { listBytes++; continue; }
                    if (how == WorldReadTests.How.Json) listJson++; else listMutagen++;
                    var row = col.Row ?? col.FirstRow; var d = row?.Def;
                    // What the value walk says of the column's field (LinkByteWalker.ValueReach)
                    string at = string.Join(".", lv.Lists.Select(l => l.Path + "[*]")) + (col.RelPath is { Length: > 0 } rp ? "." + rp : "");
                    string walk = lv.Types.Count != 1 ? "" : string.Join("; ", SFSearch.Records.LinkByteWalker.ValueReach(c.Schema, lv.Types[0]).Where(kv => SFSearch.Records.LinkByteWalker.Unmarked(kv.Key) == at).Select(kv => kv.Value ?? "reached").Distinct());
                    listLines.Add(string.Join(T, root, lv.Name, col.Name, col.Kind, how, walk, col.RelPath, row?.Subrecord, d?.Kind, d?.Custom, d?.Object));
                }
        }
        c.Save("read-census-typed.tsv", typedLines);
        c.Save("read-census-lists.tsv", listLines);
        Check(bytes > 0 && listBytes > 0, "no column of any type is read from the bytes");
        return $"{types} types ({shared} share a signature: their own records are told by their bytes): typed columns {bytes:N0} from the bytes, {mutagen:N0} straight from Mutagen's object, {json:N0} through the JSON; "
            + $"list columns {listBytes:N0} from the bytes, {listMutagen:N0} straight from Mutagen's object, {listJson:N0} through the JSON";
    }
}
