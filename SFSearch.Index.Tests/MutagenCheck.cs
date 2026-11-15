using System.Collections.Concurrent;
using System.Reflection;
using Loqui;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SFSearch.Index;

namespace SFSearch.Index.Tests;

/// <summary>
/// Checks a plugin's segment against what Mutagen's overlay says: every record's FormKey, signature and EditorID,
/// and for nested records their parent. Mutagen is asked group by group; its untyped link lookup is never used.
/// </summary>
static class MutagenCheck
{
    public sealed class Result
    {
        public string Plugin = "";
        public MasterStyle Style;
        public bool Whole;
        public long Records, EditorIds, Parents;
        public List<string> Differences = new();
        /// <summary>Records that share their FormKey with another record of the plugin and that Mutagen doesn't give.</summary>
        public List<string> LeftOut = new();
        public string? Skipped;
    }

    /// <summary>Starfield.esm's world groups: millions of placed references. Left out unless the whole plugin is asked for.</summary>
    static readonly HashSet<string> WorldGroups = new() { "Cells", "Worldspaces" };

    static readonly ConcurrentDictionary<Type, string> Sigs = new();

    static string SigOf(IMajorRecordGetter rec) => Sigs.GetOrAdd(((ILoquiObject)rec).Registration.GetType(), t =>
    {
        const BindingFlags f = BindingFlags.Public | BindingFlags.Static;
        object? v = t.GetField("TriggeringRecordType", f)?.GetValue(null) ?? t.GetProperty("TriggeringRecordType", f)?.GetValue(null);
        return v is RecordType rt ? rt.ToString() : "????";
    });

    /// <summary>The top-level groups of a plugin file: label and byte range.</summary>
    static List<(string Label, long Start, long End)> TopGroups(string path)
    {
        var list = new List<(string, long, long)>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var h = new byte[24];
        fs.ReadExactly(h);
        long pos = 24 + BitConverter.ToUInt32(h, 4);
        while (pos + 24 <= fs.Length)
        {
            fs.Position = pos; fs.ReadExactly(h);
            uint size = BitConverter.ToUInt32(h, 4);
            bool group = h[0] == 'G' && h[1] == 'R' && h[2] == 'U' && h[3] == 'P';
            long end = group ? pos + size : pos + 24 + size;
            if (group) list.Add((System.Text.Encoding.ASCII.GetString(h, 8, 4), pos, end));
            pos = end;
        }
        return list;
    }

    public static Result Compare(HeaderIndex index, string plugin, string dataFolder, bool whole)
    {
        var res = new Result { Plugin = plugin, Whole = whole };
        var p = index.Plugin(plugin);
        if (p?.Segment == null) { res.Skipped = "no segment: " + (p?.Problems.ReadError ?? "not in the index"); return res; }
        var seg = p.Segment;
        res.Style = p.Header!.Style;
        IStarfieldModDisposableGetter mod;
        try { mod = StarfieldMod.Create(StarfieldRelease.Starfield).FromPath(p.Path).WithLoadOrderFromHeaderMasters().WithDataFolder(dataFolder).Construct(); }
        catch (Exception e) { res.Skipped = $"Mutagen can't open it: {e.GetType().Name}: {e.Message.Split('\n')[0]}"; return res; }
        using var _ = mod;

        var visited = new bool[seg.Count];
        var owners = new Dictionary<ModKey, int>();
        int OwnerOf(ModKey k) { if (!owners.TryGetValue(k, out int o)) owners[k] = o = seg.OwnerOf(k.FileName.String); return o; }
        void Differ(string text) { if (res.Differences.Count < 25) res.Differences.Add(text); else if (res.Differences.Count == 25) res.Differences.Add("… more"); }

        int EntryOf(FormKey key, string? sig, string? edid)
        {
            int owner = OwnerOf(key.ModKey);
            if (owner < 0) return -1;
            int i = seg.Find(owner, key.ID);
            if (i < 0) return -1;
            if (i + 1 >= seg.Count || seg[i + 1].Key != seg[i].Key) return i;
            // The plugin holds this FormKey more than once: take the copy of the same type and EditorID, one not matched yet first
            int exact = -1, sameType = -1;
            for (int j = i; j < seg.Count && seg[j].Key == seg[i].Key; j++)
            {
                if (sig != null && seg.SignatureOf(seg[j]) != sig) continue;
                if (string.Equals(seg.EditorId(j), edid, StringComparison.Ordinal)) { if (!visited[j]) return j; if (exact < 0) exact = j; }
                else if (!visited[j] && sameType < 0) sameType = j;
            }
            return exact >= 0 ? exact : sameType >= 0 ? sameType : i;
        }

        void Check(IMajorRecordGetter rec)
        {
            string sig = SigOf(rec);
            int i = EntryOf(rec.FormKey, sig, rec.EditorID);
            if (i < 0) { Differ($"Mutagen has {sig} {rec.EditorID} ({rec.FormKey}); the index doesn't"); return; }
            if (visited[i]) return;
            visited[i] = true; res.Records++;
            string indexSig = seg.SignatureOf(seg[i]);
            if (indexSig != sig) Differ($"{rec.FormKey}: Mutagen says {sig}, the index says {indexSig}");
            string? edid = seg.EditorId(i);
            if (edid != null) res.EditorIds++;
            if (!string.Equals(edid, rec.EditorID, StringComparison.Ordinal)) Differ($"{sig} {rec.FormKey}: Mutagen's EditorID is {Show(rec.EditorID)}, the index's is {Show(edid)}");
        }

        void CheckParent(IMajorRecordGetter child, IMajorRecordGetter parent)
        {
            int i = EntryOf(child.FormKey, SigOf(child), child.EditorID);
            if (i < 0) return; // reported by Check
            res.Parents++;
            int owner = OwnerOf(parent.FormKey.ModKey);
            if (!seg.TryGetParent(seg[i], out int po, out uint pl) || po != owner || pl != parent.FormKey.ID)
                Differ($"{SigOf(child)} {child.FormKey}: Mutagen holds it inside {SigOf(parent)} {parent.FormKey}; the index says {(po < 0 ? "no parent" : $"{pl:X6}:{(po < seg.Names.Count ? seg.Names[po] : "?")}")}");
        }

        void Nested(IMajorRecordGetter rec)
        {
            switch (rec)
            {
                case ICellGetter cell:
                    foreach (var r in cell.Persistent) CheckParent(r, cell);
                    foreach (var r in cell.Temporary) CheckParent(r, cell);
                    break;
                case IWorldspaceGetter world:
                    foreach (var c in world.EnumerateMajorRecords<ICellGetter>()) CheckParent(c, world);
                    break;
                case IQuestGetter quest:
                    foreach (var t in quest.DialogTopics) { CheckParent(t, quest); foreach (var r in t.Responses) CheckParent(r, t); }
                    foreach (var s in quest.Scenes) CheckParent(s, quest);
                    break;
            }
        }

        List<(long Start, long End)>? ranges = null;
        try
        {
            if (whole)
            {
                foreach (var rec in mod.EnumerateMajorRecords()) { Check(rec); Nested(rec); }
            }
            else
            {
                var labels = new HashSet<string>();
                foreach (var prop in mod.GetType().GetProperties().Where(x => x.GetIndexParameters().Length == 0))
                {
                    if (WorldGroups.Contains(prop.Name)) continue;
                    object? v; try { v = prop.GetValue(mod); } catch { continue; }
                    if (v is not IGroupGetter g) continue;
                    foreach (var rec in g.Records)
                    {
                        labels.Add(SigOf(rec));
                        Check(rec); Nested(rec);
                        if (rec is IMajorRecordGetterEnumerable inner) foreach (var n in inner.EnumerateMajorRecords()) Check(n);
                    }
                }
                ranges = TopGroups(p.Path).Where(t => labels.Contains(t.Label)).Select(t => (t.Start, t.End)).ToList();
            }
        }
        catch (Exception e)
        {
            // Mutagen refuses a group that holds a FormKey twice. The index reads such a plugin and reports the repeats.
            if (e.Message.Contains("Two records with the same FormKey") && seg.Duplicates > 0)
            {
                res.Skipped = $"Mutagen refuses to read it ({e.GetType().Name}: {e.Message.Split('\n')[0]}); the index reports {seg.Duplicates} repeated FormKey(s) in it";
                return res;
            }
            Differ($"Mutagen threw while reading: {e.GetType().Name}: {e.Message.Split('\n')[0]}");
        }

        // Records the index has and Mutagen didn't give
        for (int i = 0; i < seg.Count; i++)
        {
            if (visited[i]) continue;
            var e = seg[i];
            if (ranges != null && !ranges.Any(r => e.Offset >= r.Start && e.Offset < r.End)) continue;
            // A record that shares its FormKey with one Mutagen did give: Mutagen keeps one copy and leaves the others out
            int kept = -1;
            for (int j = i - 1; j >= 0 && seg[j].Key == e.Key && kept < 0; j--) if (visited[j]) kept = j;
            for (int j = i + 1; j < seg.Count && seg[j].Key == e.Key && kept < 0; j++) if (visited[j]) kept = j;
            if (kept >= 0)
            {
                res.LeftOut.Add($"{seg.SignatureOf(e)} {seg.EditorId(i)} ({e.LocalId:X6}:{seg.Names[e.Owner]}) at 0x{e.Offset:X} is left out; Mutagen keeps {seg.SignatureOf(seg[kept])} {seg.EditorId(kept)} at 0x{seg[kept].Offset:X}, the {(seg[kept].Offset > e.Offset ? "later" : "earlier")} one in the file");
                continue;
            }
            Differ($"the index has {seg.SignatureOf(e)} {seg.EditorId(i)} ({e.LocalId:X6}:{(e.IsUnresolved ? "unresolved" : seg.Names[e.Owner])}) at 0x{e.Offset:X}; Mutagen didn't give it");
        }
        return res;
    }

    static string Show(string? s) => s == null ? "null" : $"\"{s}\"";

    /// <summary>The load order as Mutagen lists it: name and enabled mark.</summary>
    public static List<(string Name, bool Enabled)> LoadOrderListings(string dataFolder) =>
        Mutagen.Bethesda.Plugins.Order.LoadOrder.GetLoadOrderListings(GameRelease.Starfield, dataFolder, throwOnMissingMods: false)
            .Select(l => (l.FileName.ToString(), l.Enabled)).ToList();
}
