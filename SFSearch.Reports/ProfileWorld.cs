// profile <Type.Path> of a world type's link field from the world link index (GAPS S99, to-do J78): when every plugin
// read has its world link file (J32, made only when the user asks), the census of the field (each target and its count,
// the records that hold one) is the index's postings for that field, read with no record opened; only the samples'
// records are read, through Mutagen. The index keeps links, not null ones, so the counts are of links that aren't null.
using Mutagen.Bethesda.Plugins;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Reports;

/// <summary>The world link index as profile reads it (SFSearch's engine makes and keeps it: SFSearch\Engine\WorldLinks.cs).</summary>
public interface IWorldLinkIndex
{
    /// <summary>Records of this type are world records of the plugin (the index would hold their links).</summary>
    bool IsWorld(IndexedPlugin p, string signature);
    /// <summary>The plugin has its world link file, and the file holds the links of every one of its world records.</summary>
    bool Has(IndexedPlugin p);
    /// <summary>Every posting of a field, as the walk names it (no kinds of object: <c>Components[*].Keywords[*]</c>): the
    /// target (its owner's number among the plugin's masters and itself, shifted 24, and its ID), the record's entry in
    /// the plugin's header index, how many times the record holds it there.</summary>
    void Each(IndexedPlugin p, string field, Action<ulong, int, long> each);
    /// <summary>What was read, for the [source] line: "the world link index of 2 plugins (2.1 MB)".</summary>
    string Source(IReadOnlyCollection<IndexedPlugin> plugins);
}

public sealed partial class ProfileReport
{
    /// <summary>The fields of a byte route the world link index answers too: each a world type's link field whose path,
    /// without its kinds of object, is that field's alone. Null when one isn't.</summary>
    static bool WorldAnswers(IReadOnlyList<ByteField> route, RecordSchema schema)
    {
        foreach (var bf in route)
        {
            var fields = LinkByteWalker.FieldsOf(schema, bf.Target.Type);
            string plain = LinkByteWalker.Unmarked(bf.Target.Path);
            if (fields.Paths.Count(p => LinkByteWalker.Unmarked(p) == plain) != 1) return false;
        }
        return true;
    }

    /// <summary>The plugins of the set that hold records of the fields' types, when each of those types is world data
    /// there; null when one isn't.</summary>
    static List<IndexedPlugin>? WorldPlugins(PluginSet set, IReadOnlyList<ProfileTarget> targets, IWorldLinkIndex world)
    {
        var sigs = targets.Select(t => t.Signature).Distinct().ToList();
        var holding = new List<IndexedPlugin>();
        foreach (var p in set.Plugins)
        {
            if (p.Segment is not { } seg) continue;
            var have = sigs.Where(s => seg.EntriesOf(s).Any()).ToList();
            if (have.Count == 0) continue;
            if (have.Any(s => !world.IsWorld(p, s))) return null;
            holding.Add(p);
        }
        return holding;
    }

    /// <summary>The census of each field from the world link index of every plugin read; the samples' records read
    /// through Mutagen (see the file's head).</summary>
    void RunWorld(ProfilePlan plan, ProfileAnswer answer, List<FieldStats> fields, List<ByteField> route, Walker walker, ReadScope scope, IWorldLinkIndex world)
    {
        var index = session.Index;
        var engine = session.Engine;
        var seeds = plan.Targets.Select(t => Walker.Seed(t.Name)).ToArray();
        var picks = fields.Select(_ => new List<(ulong Mark, int Plugin, int Entry)>()).ToArray();
        var read = new List<IndexedPlugin>();
        foreach (var p in plan.Set.Plugins)
        {
            if (p.Segment is not { } seg)
            {
                answer.Notes.Add($"{p.Name} can't be read ({p.Problems.ReadError ?? "it has no header index"}): left out.");
                continue;
            }
            ReportSession.AddSource(answer, p);
            ReportSession.AddProblemNotes(answer, p);
            answer.PluginsRead++;
            var sigs = plan.Targets.Select(t => t.Signature).ToHashSet(StringComparer.Ordinal);
            long records = 0;
            foreach (var s in sigs) records += plan.Set.WinnersOnly ? seg.EntriesOf(s).Count(e => index.IsWinner(new RecordRef(p.Id, e))) : seg.EntriesOf(s).Count();
            answer.RecordsRead += records;
            if (records == 0) continue;
            read.Add(p);
            for (int i = 0; i < route.Count; i++)
            {
                var t = route[i].Target;
                // The postings of the field, for records of the field's type (a path the walk shares across types)
                var rows = new List<(long Offset, int Entry, ulong Target, long Count)>();
                world.Each(p, LinkByteWalker.Unmarked(t.Path), (target, entry, count) =>
                {
                    var e = seg[entry];
                    if (seg.SignatureOf(e) != t.Signature) return;
                    if (plan.Set.WinnersOnly && !index.IsWinner(new RecordRef(p.Id, entry))) return;
                    rows.Add((e.Offset, entry, target, count));
                });
                if (rows.Count == 0) continue;
                // In the file's order, as the bytes and Mutagen's walk meet them
                rows.Sort((x, y) => x.Offset != y.Offset ? x.Offset.CompareTo(y.Offset) : x.Target.CompareTo(y.Target));
                var keys = new Dictionary<ulong, (FormKey Key, string Text)>();
                var counts = new Dictionary<string, long>(StringComparer.Ordinal);
                var targets = new Dictionary<FormKey, long>();
                long links = 0, withLinks = 0;
                for (int r = 0; r < rows.Count;)
                {
                    int e = rows[r].Entry, n = 0;
                    for (; r < rows.Count && rows[r].Entry == e; r++)
                    {
                        var (_, _, target, count) = rows[r];
                        if (!keys.TryGetValue(target, out var key))
                        {
                            int owner = (int)(target >> 24);
                            var fk = new FormKey(ModKey.FromFileName(owner < seg.Names.Count ? seg.Names[owner] : p.Name), (uint)(target & 0xFFFFFF));
                            keys[target] = key = (fk, fk.ToString());
                        }
                        counts[key.Text] = counts.GetValueOrDefault(key.Text) + count;
                        targets[key.Key] = targets.GetValueOrDefault(key.Key) + count;
                        links += count; n += (int)count;
                    }
                    withLinks++;
                    // The record's sample mark: its smallest over the links it holds there
                    string formKey = index.FormKeyOf(new RecordRef(p.Id, e));
                    ulong mark = ulong.MaxValue;
                    for (int k = 0; k < n; k++) mark = Math.Min(mark, Walker.Mark(p.Name, formKey, seeds[i], k));
                    Pick(picks[i], KeptSamples, mark, p.Id, e);
                }
                var fs = fields[i];
                var ps = fs.Plugin(p.Name);
                ps.Records += withLinks; ps.RecordsNonZero += withLinks; ps.Values += links; ps.NonZero += links;
                fs.Values += links; fs.NonZero += links;
                foreach (var (repr, c) in counts)
                {
                    if (fs.Counts.TryGetValue(repr, out var have)) fs.Counts[repr] = have + c;
                    else if (fs.Counts.Count < MaxDistinct) fs.Counts[repr] = c;
                    else fs.OtherValues += c;
                }
                fs.Targets ??= new();
                foreach (var (k, c) in targets) fs.Targets[k] = fs.Targets.GetValueOrDefault(k) + c;
            }
        }
        // The samples' records, read through Mutagen for the values beside the field
        int sampled = 0;
        foreach (var g in picks.SelectMany(x => x).Select(x => (x.Plugin, x.Entry)).Distinct().GroupBy(x => x.Plugin))
        {
            var p = index.Plugins[g.Key];
            scope.Touch(engine, p.Name);
            var arr = g.Select(x => x.Entry).ToArray();
            Array.Sort(arr.Select(e => p.Segment![e].Offset).ToArray(), arr);
            var res = walker.Batch(engine, index, new PluginCtx(p, plan.Set.WinnersOnly), arr, 0, arr.Length);
            sampled += arr.Length;
            foreach (var (i, acc) in res.Fields)
            {
                var mine = picks[i].Where(x => x.Plugin == g.Key).Select(x => index.FormKeyOf(new RecordRef(x.Plugin, x.Entry))).ToHashSet(StringComparer.Ordinal);
                fields[i].Offer(acc.Samples.Where(s => mine.Contains(s.FormKey)).ToList());
            }
            foreach (var (why, n) in res.Skipped) answer.Skipped[why] = answer.Skipped.GetValueOrDefault(why) + n;
        }
        answer.Sources.Add(world.Source(read));
        answer.ReadFromBytes = $"the field's links from {world.Source(read)} (GAPS S99); {ReportAnswer.Plural(sampled, "sample record")} read through Mutagen for the values beside the field";
        answer.Notes.Add("The census came from the world link index, which keeps every link that isn't null: the values counted are those links (a null link isn't kept, so none is counted, and the records counted are those with a link).");
    }
}
