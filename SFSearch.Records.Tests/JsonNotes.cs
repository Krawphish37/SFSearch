// What the one-record verify used to copy from the record as read onto the record rebuilt from its JSON: its subrecord
// order, its compressed bytes, a cell's child-group layout and the string IDs of its translated texts. Since
// Mutagen.Bethesda.Serialization 1.38.6-fixes.3/4 the JSON carries all four as round-trip notes (C211, C213, C215,
// C214), so a record rebuilt from the JSON alone has them, as Mutagen's round-trip API and its texts' string IDs show.
// Since 1.38.6-fixes.5 it carries a package's procedure tree data order too (C59 W13: verify's last copy, GiveBack, went),
// and a record rebuilt from its JSON writes its JSON again: the 7 records of GAPS S131 (J100), a condition on alias 0 each,
// threw "Nullable object must have a value" before.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Strings;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Records.Tests;

static class JsonNotes
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    const string Starfield = "Starfield.esm";
    /// <summary>A mod whose compressed records Mutagen compresses to other bytes (zlib's default level, not the best):
    /// the JSON must carry their streams (C213). Starfield.esm has 13 such, all navmeshes.</summary>
    const string OtherCompression = "N12_Watchtower.esm";

    public static string Run(Ctx ctx)
    {
        var e = ctx.Engine;
        var index = ctx.DataIndex;
        var p = index.Plugin(Starfield) ?? throw new TestFailed($"{Starfield} isn't in the Data folder");
        var other = index.Plugin(OtherCompression) ?? throw new TestFailed($"{OtherCompression} isn't in the Data folder");
        var seg = p.Segment!;
        e.Sessions.Use(Starfield, OtherCompression);
        // Up to 30 records of every type, every 40th compressed record and 300 cells of Starfield.esm; every compressed
        // record of the mod
        var picks = Tests.ByType(index, Starfield).OrderBy(t => t.Key, StringComparer.Ordinal).SelectMany(t => Tests.Sample(t.Value, 30)).ToList();
        var compressed = Enumerable.Range(0, seg.FirstUnresolved).Where(i => seg[i].IsCompressed).Select(i => new RecordRef(p.Id, i)).ToList();
        picks.AddRange(Tests.Sample(compressed, Math.Max(1, compressed.Count / 40)));
        picks.AddRange(Tests.Sample(index.RecordsOfType(Starfield, "CELL").ToList(), 300));
        picks.AddRange(Enumerable.Range(0, other.Segment!.FirstUnresolved).Where(i => other.Segment[i].IsCompressed).Select(i => new RecordRef(other.Id, i)));
        picks = picks.Distinct().ToList();

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int records = 0, packages = 0, rewritten = 0;
        foreach (var r in picks)
        {
            if (e.HeavyNote(r) != null) continue;
            var loaded = e.Load(r);
            var json = RecordSplit.WithoutChildren(e.Json(loaded));
            var rebuilt = (IMajorRecordGetter)RecordSerializer.FromJson(json.ToJsonString(), loaded.Type);
            records++;
            foreach (var (key, _) in Notes(json)) counts[key] = counts.GetValueOrDefault(key) + 1;
            // The rebuilt record holds each note, through Mutagen's round-trip API and its texts' string IDs
            var read = loaded.Record;
            foreach (var path in TextPaths(json, ""))
                if (Resolve(read, path) is TranslatedString { StringsKey: { } key } from)
                    Check(Resolve(rebuilt, path) is TranslatedString to && to.StringsKey == key && to.StringsKeySource == from.StringsKeySource
                        && (from.StringsKeyPlugin == null || to.StringsKeyPlugin == from.StringsKeyPlugin),
                        $"{loaded.Label}: the string ID of {path} isn't carried ({key:X8} {from.StringsKeySource} {from.StringsKeyPlugin})");
            Check(read.GetSubrecordOrder() == rebuilt.GetSubrecordOrder(), $"{loaded.Label}: the subrecord order isn't carried");
            // Compressed bytes Mutagen would make again aren't written (C213): only those it wouldn't must come back
            if (read.GetOriginalCompressedDataIfNotReproduced() is { } stream)
                Check(rebuilt.GetOriginalCompressedData() is { } back && back.AsSpan().SequenceEqual(stream), $"{loaded.Label}: the compressed bytes aren't carried");
            if (read is ICellGetter cell && rebuilt is ICellGetter cell2) Check(cell.GetChildGroupLayout() == cell2.GetChildGroupLayout(), $"{loaded.Label}: the child-group layout isn't carried");
            if (read is IPackageGetter pk && rebuilt is IPackageGetter pk2)
            {
                Check((pk.GetProcedureTreeDataOrder() ?? Array.Empty<sbyte>()).SequenceEqual(pk2.GetProcedureTreeDataOrder() ?? Array.Empty<sbyte>()), $"{loaded.Label}: the package's procedure tree data order isn't carried");
                packages++;
            }
        }
        // S131 (J100): each record rebuilt from its JSON writes the same JSON again
        foreach (string key in new[] { "07702B", "24DDC4", "2741AD", "22761C", "32B0E9", "295393", "0263E0" })
        {
            var loaded = e.Load(e.Find(Starfield, key + ":" + Starfield));
            // The serializer's own JSON of the record (its child records inside it), as S131 found it
            string first = RecordSerializer.ToJson(loaded.Record);
            string again;
            try { again = RecordSerializer.ToJson(RecordSerializer.FromJson(first, loaded.Type)); }
            catch (InvalidOperationException x) { throw new TestFailed($"{loaded.Label}: the record rebuilt from its JSON can't be written to JSON again: {x.Message}"); }
            Check(first == again, $"{loaded.Label}: JSON -> record -> JSON differs: {CardCheck.FirstDifference(JsonNode.Parse(first), JsonNode.Parse(again))}");
            rewritten++;
        }
        foreach (var key in new[] { "SubrecordOrder", "OriginalCompressedData", "ChildGroupLayout", "StringsKey" })
            Check(counts.GetValueOrDefault(key) > 0, $"no record of the {records:N0} read has a {key} note: the test doesn't cover it");
        return $"{records:N0} records of {Starfield} and {OtherCompression} rebuilt from their JSON alone hold every round-trip note of the record as read (the notes: " +
            string.Join(", ", counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key} {c.Value:N0}")) + $"); {packages} packages keep their procedure tree data order (C59 W13); S131's {rewritten} records go JSON -> record -> JSON unchanged (C59, J100)";
    }

    /// <summary>Every translated text of a record, by its path in the JSON: a text is written as an object with "TargetLanguage".</summary>
    static IEnumerable<string> TextPaths(JsonNode? n, string p)
    {
        if (n is JsonObject o)
        {
            if (o.ContainsKey("TargetLanguage")) { yield return p; yield break; }
            foreach (var (k, v) in o) if (v != null && k != RecordSplit.ChildKey) foreach (var x in TextPaths(v, p.Length == 0 ? k : $"{p}.{k}")) yield return x;
        }
        else if (n is JsonArray a)
            for (int i = 0; i < a.Count; i++) if (a[i] != null) foreach (var x in TextPaths(a[i], $"{p}[{i}]")) yield return x;
    }

    /// <summary>Follows a JSON path through a Mutagen object by property name and list index.</summary>
    static object? Resolve(object root, string path)
    {
        object? cur = root;
        foreach (Match m in Regex.Matches(path, @"([^.\[\]]+)|\[(\d+)\]"))
        {
            if (cur == null) return null;
            if (m.Groups[2].Success)
            {
                int i = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                cur = cur is System.Collections.IList l ? (i < l.Count ? l[i] : null) : cur is System.Collections.IEnumerable en ? en.Cast<object>().ElementAtOrDefault(i) : null;
            }
            else
            {
                var type = cur.GetType();
                var prop = type.GetProperty(m.Groups[1].Value) ?? type.GetInterfaces().Select(t => t.GetProperty(m.Groups[1].Value)).FirstOrDefault(x => x != null);
                cur = prop?.GetValue(cur);
            }
        }
        return cur;
    }

    /// <summary>Every round-trip note of a record's JSON (the record's own and its texts' string IDs), as path=value.</summary>
    static HashSet<(string Key, string Text)> Notes(JsonNode? root)
    {
        var set = new HashSet<(string, string)>();
        void Walk(JsonNode? n, string path)
        {
            if (n is JsonObject o)
                foreach (var (k, v) in o)
                {
                    if (SerializerNotes.RecordKeys.Contains(k) || SerializerNotes.TextKeys.Contains(k)) set.Add((k, $"{path}.{k}={v?.ToJsonString()}"));
                    else Walk(v, $"{path}.{k}");
                }
            else if (n is JsonArray a)
                for (int i = 0; i < a.Count; i++) Walk(a[i], $"{path}[{i}]");
        }
        Walk(root, "");
        return set;
    }
}
