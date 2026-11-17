// link-positions: SFSearch's byte link walk (LinkWalk.cs) held against Mutagen's value positions (C60), record by record.
//
// The byte walk is SFSearch's speed design: it reads a record's links from its bytes without building Mutagen's object
// (measured 2026-11-16: 7 times faster than Mutagen's positions over Starfield.esm's types, 11 to 25 times on the world
// types; fix-w1-value-positions.md). It no longer copies Mutagen's reading rules where Mutagen's files carry them (the
// layouts, selectorEnum, layoutPresentWhen, class=, genderEnumRecord), and this test holds what it finds to what Mutagen's
// own reader noted: every link the walk places (a subrecord, an offset) is a value Mutagen read at those 4 bytes, under a
// path of the same link field; and every non-null link value Mutagen noted under a link field is one the walk found there.
// A sample of each record type of Starfield.esm (up to 150 records a type, 20 of the huge ones), and of two mods.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins.Records;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Records.Tests;

static partial class LinkPositions
{
    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex Index();

    public static string Run(Ctx ctx)
    {
        var e = ctx.Engine;
        var schema = e.Schema;
        var problems = new ConcurrentBag<string>();
        long links = 0, held = 0, mutagenLinks = 0, records = 0, reflection = 0, whole = 0;
        var perType = new ConcurrentDictionary<string, (long Links, long Held)>(StringComparer.Ordinal);
        foreach (var plugin in new[] { "Starfield.esm", "kgcdoom.esm", "sfta01.esm" })
        {
            e.Sessions.Use(plugin);
            var p = ctx.DataIndex.Plugin(plugin);
            if (p?.Segment is not { } seg) { problems.Add($"{plugin}: not in the Data folder's index"); continue; }
            var session = e.Sessions.Get(plugin);
            foreach (var t in schema.Model.RecordTypes)
            {
                var fields = LinkByteWalker.FieldsOf(schema, t.Name);
                if (fields.Paths.Count == 0) continue;
                int take = t.Sig is "SFTR" or "NAVI" or "NAVM" or "LAND" ? 20 : 150;
                var entries = seg.EntriesOf(t.Sig!).Take(take).ToList();
                Parallel.ForEach(entries, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount / 2 }, () => new LinkByteWalker(schema), (entry, _, walker) =>
                {
                    var raw = e.Raw(new RecordRef(p.Id, entry));
                    if (raw.Data.Length > 16_000_000) return walker;
                    var found = walker.Walk(fields, raw, session.Localized, id => null);
                    RecordValuePositions pos;
                    try { pos = session.Positions(raw); }
                    catch (RecordException x) { problems.Add($"{plugin} {t.Sig} {raw.FormId:X8}: {x.Message}"); return walker; }
                    // Mutagen's values by place, and the link field each path names
                    var byPlace = new Dictionary<(int, int), List<ValuePosition>>();
                    foreach (var v in pos.Positions)
                        if (v.Kind == ValuePositionKind.Value && v.SubrecordIndex >= 0)
                            (byPlace.TryGetValue((v.SubrecordIndex, v.Offset), out var l) ? l : byPlace[(v.SubrecordIndex, v.Offset)] = new()).Add(v);
                    // A path of Mutagen's names the field without its kinds of object: the walk's field, its kinds left out
                    bool Same(string path, int field) => LinkByteWalker.Unmarked(fields.Paths[field]) == Index().Replace(path, "[*]");
                    long l1 = 0, h1 = 0;
                    var walked = new HashSet<(int, int)>();
                    foreach (var f in found)
                    {
                        if (f.Offset < 0) continue; // a bulky subrecord's reader (WorldBytes) counts, it places no link
                        if (f.Raw == 0) continue;   // a null link: none Mutagen holds (a linked reference keyword not there)
                        l1++;
                        walked.Add((f.Subrecord, f.Offset));
                        if (byPlace.TryGetValue((f.Subrecord, f.Offset), out var at) && at.Any(v => v.Length == 4 && Same(v.Path, f.Field))) h1++;
                        // A reflection's FormLinks: Mutagen's ReflectionData.FindFormIds finds them in the blob, and its value
                        // positions note none of them (GAPS S173); the walk's are held to Mutagen's links by link-census
                        else if (at == null && fields.Paths[f.Field].EndsWith(".FormLinks[*]", StringComparison.Ordinal)) Interlocked.Increment(ref reflection);
                        // Inside a value Mutagen noted as a whole (a package input's Target, 12 bytes: S173)
                        else if (at == null && pos.Positions.Any(v => v.Kind == ValuePositionKind.Value && v.SubrecordIndex == f.Subrecord && v.Offset < f.Offset && f.Offset + 4 <= v.Offset + v.Length)) Interlocked.Increment(ref whole);
                        else problems.Add($"{plugin} {t.Sig} {raw.FormId:X8}: the walk's {fields.Paths[f.Field]} at #{f.Subrecord + 1} {raw.Subrecords[f.Subrecord].Signature} offset {f.Offset}; Mutagen read there: {(at == null ? "nothing" : string.Join(", ", at.Select(v => $"{v.Path} ({v.Length} bytes)")))}");
                    }
                    // Every link value Mutagen noted (not null) is one the walk found at its place
                    foreach (var v in pos.Positions)
                    {
                        if (v.Kind != ValuePositionKind.Value || v.SubrecordIndex < 0 || v.Length != 4 || v.Converted) continue;
                        string plain = Index().Replace(v.Path, "[*]");
                        int fi = Enumerable.Range(0, fields.Paths.Count).FirstOrDefault(i => LinkByteWalker.Unmarked(fields.Paths[i]) == plain, -1);
                        if (fi < 0 || fields.Left[fi] != null || fields.Bulky != null && fields.By[fi]?.EndsWith("(WorldBytes)", StringComparison.Ordinal) == true) continue;
                        // A link on one kind of the object and not on this one (a condition's ComparisonValue is a global's
                        // link on a ConditionGlobal, a float on a ConditionFloat): what Mutagen made there says
                        if (!IsLink(pos.Record, v.Path)) continue;
                        uint value = BinaryPrimitives.ReadUInt32LittleEndian(raw.Content(raw.Subrecords[v.SubrecordIndex]).Slice(v.Offset, 4));
                        if (value == 0) continue;
                        Interlocked.Increment(ref mutagenLinks);
                        if (!walked.Contains((v.SubrecordIndex, v.Offset)) && !IndexNotLink(schema, t.Name, v.Path, raw, v))
                            problems.Add($"{plugin} {t.Sig} {raw.FormId:X8}: Mutagen read the link {v.Path} ({value:X8}) at #{v.SubrecordIndex + 1} {v.Subrecord} offset {v.Offset}; the walk found none there");
                    }
                    Interlocked.Add(ref links, l1); Interlocked.Add(ref held, h1); Interlocked.Increment(ref records);
                    perType.AddOrUpdate(t.Sig!, (l1, h1), (_, o) => (o.Links + l1, o.Held + h1));
                    return walker;
                }, _ => { });
            }
        }
        var list = problems.OrderBy(x => x, StringComparer.Ordinal).ToList();
        ctx.WriteReport("link-positions.txt", list.Prepend($"{list.Count} differences").Concat(perType.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}: {k.Value.Links} links, {k.Value.Held} where Mutagen read them")));
        Ctx.Check(list.Count == 0, $"{list.Count} differences between the byte walk and Mutagen's value positions: {string.Join(" | ", list.Take(20))}");
        return $"{records:N0} records of {perType.Count} types (Starfield.esm, kgcdoom.esm, sfta01.esm): {held:N0} of {links:N0} non-null links the byte walk places are where Mutagen read a value of the same link field ({reflection:N0} more are a reflection's FormLinks, which Mutagen's positions don't note, and {whole:N0} inside a value Mutagen noted as a whole, a package input's target: S173); Mutagen's {mutagenLinks:N0} non-null link values all found by the walk";
    }

    /// <summary>Whether Mutagen's object holds a link at a path of its value positions (list items by index; a dictionary
    /// entry by its key's text).</summary>
    static bool IsLink(object root, string path)
    {
        object? o = root;
        foreach (System.Text.RegularExpressions.Match m in Regex.Matches(path, @"([^.\[\]]+)|\[([^\]]*)\]"))
        {
            if (o == null) return false;
            if (m.Groups[1].Success)
            {
                var prop = o.GetType().GetProperty(m.Groups[1].Value);
                if (prop == null) return false;
                o = prop.GetValue(o);
            }
            else
            {
                string key = m.Groups[2].Value;
                if (o is System.Collections.IDictionary d) { o = null; foreach (System.Collections.DictionaryEntry de in d) if (de.Key?.ToString() == key) { o = de.Value; break; } }
                else if (o is System.Collections.IEnumerable list && int.TryParse(key, out int k)) o = list.Cast<object?>().ElementAtOrDefault(k);
                else return false;
            }
        }
        return o is Mutagen.Bethesda.Plugins.IFormLinkGetter;
    }

    /// <summary>A condition parameter Mutagen holds as a FormLinkOrIndex that is an alias or package data index here (the
    /// CTDA's flags, UseAliases or UsePackageData: the XML's layoutBits on ConditionData), not a link.</summary>
    static bool IndexNotLink(RecordSchema schema, string type, string path, RawRecord raw, ValuePosition v)
    {
        if (!path.Contains(".Data.", StringComparison.Ordinal) || raw.Subrecords[v.SubrecordIndex].Signature != "CTDA") return false;
        byte flags = raw.Content(raw.Subrecords[v.SubrecordIndex])[0];
        var data = schema.Model.Ancestry("ConditionData").SelectMany(o => o.Fields).ToList();
        int Bit(string name) => int.TryParse(data.FirstOrDefault(f => f.Name == name)?.LayoutBits, out int b) ? b : -1;
        int aliases = Bit("UseAliases"), packageData = Bit("UsePackageData");
        return aliases >= 0 && (flags >> aliases & 1) != 0 || packageData >= 0 && (flags >> packageData & 1) != 0;
    }
}
