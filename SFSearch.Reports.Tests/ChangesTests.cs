// changes, on snapshots the test makes itself: a copy of a real plugin in the work folder, then changed by Mutagen
// (records removed and added); and a made master with a patch, where the master then changes size. The history log
// of each is a folder in the work folder. The Data folder is only read.
using System.Globalization;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SFSearch.Index;
using SFSearch.Reports;
using IndexStyle = SFSearch.Index.MasterStyle;
using MutagenStyle = Mutagen.Bethesda.Plugins.MasterStyle;

namespace SFSearch.Reports.Tests;

static class ChangesTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    /// <summary>A row's text with the size marks taken out (they are resolved when the answer is written: SizeWords, fix-tools B11).</summary>
    static string U(string s) => s.Replace(SFSearch.Records.SizeWords.Mark.ToString(), "");
    const string Plugin = "weap_starlight.esm";

    static MutagenStyle Convert(IndexStyle s) => s switch { IndexStyle.Small => MutagenStyle.Small, IndexStyle.Medium => MutagenStyle.Medium, _ => MutagenStyle.Full };

    static HeaderIndex Index(Ctx ctx, string name, IEnumerable<string> files) =>
        new(PluginSource.FromFiles(files), new IndexOptions { ScratchFolder = Path.Combine(ctx.Folder(name), "scratch"), VersionText = "sfsreport-tests changes", PluginsTxt = "", Ccc = "" });

    public static string Made(Ctx ctx)
    {
        var s = ctx.Session;
        var real = s.Index.Plugin(Plugin) ?? throw new TestFailed($"{Plugin} isn't in the Data folder");
        string folder = Path.Combine(ctx.Folder("changes"), "data");
        Directory.CreateDirectory(folder);
        string copy = Path.Combine(folder, Plugin);
        ctx.MustBeInWork(copy);
        File.Copy(real.Path, copy, true);
        File.SetAttributes(copy, FileAttributes.Normal);

        // The copy in the work folder, and its masters where they are in Data (only read)
        var masters = new List<string>();
        var queue = new Queue<string>(real.Header!.Masters);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Plugin };
        while (queue.Count > 0)
        {
            string m = queue.Dequeue();
            if (!seen.Add(m)) continue;
            var mp = s.Index.Plugin(m) ?? throw new TestFailed($"{m}, a master of {Plugin}, isn't in Data");
            masters.Add(mp.Path);
            foreach (var mm in mp.Header?.Masters ?? Array.Empty<string>()) queue.Enqueue(mm);
        }
        using var index = Index(ctx, "changes", new[] { copy }.Concat(masters));
        string history = Path.Combine(ctx.Folder("changes"), "history");
        var report = new ChangesReport(s, index, history, "the test's folder");
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0);

        // ---- 1. the history begins
        var first = report.Changes(new ChangesOptions { Now = t0 });
        Check(first.FirstRun && first.SnapshotsAdded == 1 + masters.Count && first.Changes.Count == 0, $"the first look: {first.SnapshotsAdded} snapshots, {first.Changes.Count} changes");
        Check(first.ToText().Contains("The history begins now", StringComparison.Ordinal), "the first look doesn't say that the history begins");
        var log = new HistoryLog(history);
        var snap1 = log.Snapshots.Single(x => x.Plugin.Equals(Plugin, StringComparison.OrdinalIgnoreCase));
        var list1 = log.ReadList(snap1) ?? throw new TestFailed("no record list was kept for the copy");
        Check(list1.Records.Count == index.Plugin(Plugin)!.Segment!.Count && snap1.Records == list1.Records.Count, $"the kept list has {list1.Records.Count} records, the plugin {index.Plugin(Plugin)!.Segment!.Count}");
        Check(snap1.Key == real.ContentKey.ToString(), "the copy's content key isn't the real file's");
        // Starfield.esm is too large for a record list: its counts per type are kept
        var big = log.Snapshots.FirstOrDefault(x => x.Plugin.Equals("Starfield.esm", StringComparison.OrdinalIgnoreCase));
        string bigNote = "";
        if (big != null)
        {
            Check(big.List == null && big.Counts.Count > 100 && big.Records > HistoryLog.ListLimit, $"Starfield.esm: list {big.List}, {big.Counts.Count} types, {big.Records} records");
            bigNote = $" Starfield.esm ({N(big.Records)} records) gets its counts for {big.Counts.Count} record types and no list.";
        }

        // ---- 2. nothing changed
        var second = report.Changes(new ChangesOptions { Now = t0.AddMinutes(5) });
        Check(!second.FirstRun && second.SnapshotsAdded == 0 && second.Changes.Count == 0, $"a second look with nothing changed: {second.SnapshotsAdded} snapshots, {second.Changes.Count} changes");
        // The same content with another date is the same copy
        File.SetLastWriteTimeUtc(copy, new DateTime(2019, 5, 5, 0, 0, 0, DateTimeKind.Utc));
        var redated = report.Changes(new ChangesOptions { Now = t0.AddMinutes(6) });
        Check(redated.SnapshotsAdded == 0 && redated.Changes.Count == 0, $"the same file with an older date: {redated.SnapshotsAdded} snapshots");

        // ---- 3. the copy changed by Mutagen: 2 records removed, 3 added
        var styles = new List<KeyedMasterStyle>();
        for (int i = 0; i < real.Header.Masters.Count; i++) styles.Add(new KeyedMasterStyle(ModKey.FromFileName(real.Header.Masters[i]), Convert(real.MasterStyles[i])));
        styles.Add(new KeyedMasterStyle(ModKey.FromFileName(Plugin), Convert(real.Header.Style)));
        var lookup = new LoadOrder<IModMasterStyledGetter>(styles);
        var mod = StarfieldMod.CreateFromBinary(new ModPath(ModKey.FromFileName(Plugin), copy), StarfieldRelease.Starfield, new BinaryReadParameters { MasterFlagsLookup = lookup, ThrowOnUnknownSubrecord = false });
        var seg = index.Plugin(Plugin)!.Segment!;
        // Two of its own records to remove: a keyword and an aim model, by what the header index says it holds
        var removed = new List<string>();
        foreach (string sig in new[] { "KYWD", "AMDL" })
        {
            int at = seg.EntriesOf(sig).First(i => seg[i].Owner == seg.SelfOwner);
            removed.Add($"{seg[at].LocalId:X6}:{Plugin}");
        }
        Check(mod.Keywords.Remove(FormKey.Factory(removed[0])) && mod.AimModels.Remove(FormKey.Factory(removed[1])), $"Mutagen didn't find {removed[0]} or {removed[1]} to remove");
        var added = new List<string>();
        for (int i = 1; i <= 3; i++)
        {
            var k = mod.Keywords.AddNew($"zz_m11b_added_{i}");
            added.Add($"{k.FormKey.ID:X6}:{Plugin}");
        }
        mod.WriteToBinary(copy, new BinaryWriteParameters
        {
            MasterFlagsLookup = lookup, ModKey = ModKeyOption.CorrectToPath,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = new MastersListOrderingEnumOption { Option = MastersListOrderingOption.NoCheck },
            NextFormID = NextFormIDOption.NoCheck, FormIDCompaction = FormIDCompactionOption.NoCheck,
            LowerRangeDisallowedHandler = new NoCheckIfLowerRangeDisallowed(), CleanNulls = false, HeaderSizeFlags = HeaderSizeFlagsOption.NoCheck,
        });

        var third = report.Changes(new ChangesOptions { Now = t0.AddHours(1) });
        Check(third.SnapshotsAdded == 1 && third.Changes.Count == 1, $"after the change: {third.SnapshotsAdded} snapshots, {third.Changes.Count} changes ({string.Join("; ", third.Changes.Select(c => c.Plugin + ": " + c.What))})");
        var row = third.Changes[0];
        Check(row.Plugin.Equals(Plugin, StringComparison.OrdinalIgnoreCase) && row.Kind == "updated" && row.FoundNow && row.RecordsAdded == 3 && row.RecordsRemoved == 2 && row.RecordsRetyped == 0,
            $"the change row: {row.Plugin} {row.Kind}, {row.RecordsAdded} added, {row.RecordsRemoved} removed, {row.RecordsRetyped} changed type: {row.What}");
        Check(third.ToText().Contains($"| now | {Plugin} | updated: 3 records added, 2 removed", StringComparison.Ordinal), $"the text doesn't say what changed:\n{third.ToText()}");

        // ---- 4. the plugin's own history: exactly the records added and removed
        var one = report.Changes(new ChangesOptions { Plugin = Plugin, Now = t0.AddHours(1).AddMinutes(1) });
        Check(one.History.Count == 2 && one.SnapshotsAdded == 0, $"the plugin's history has {one.History.Count} snapshots");
        var gotAdded = one.RecordChanges.Where(r => r.Change == "added").Select(r => r.FormKey).OrderBy(x => x).ToList();
        var gotRemoved = one.RecordChanges.Where(r => r.Change == "removed").Select(r => r.FormKey).OrderBy(x => x).ToList();
        Check(gotAdded.SequenceEqual(added.OrderBy(x => x), StringComparer.OrdinalIgnoreCase), $"added: [{string.Join(", ", gotAdded)}], made: [{string.Join(", ", added)}]");
        Check(gotRemoved.SequenceEqual(removed.OrderBy(x => x), StringComparer.OrdinalIgnoreCase), $"removed: [{string.Join(", ", gotRemoved)}], made: [{string.Join(", ", removed)}]");
        Check(one.Retyped == 0 && one.RecordChanges.Count == 5, $"{one.RecordChanges.Count} record changes, 5 made");
        // An added record is named from the plugin as it is now; a removed one that no plugin holds has its FormKey only
        Check(one.RecordChanges.Where(r => r.Change == "added").All(r => r.Record.StartsWith("zz_m11b_added_", StringComparison.Ordinal) && r.Signature == "KYWD"), $"the added records aren't named: {string.Join("; ", one.RecordChanges.Where(r => r.Change == "added").Select(r => r.Record))}");
        Check(one.RecordChanges.Where(r => r.Change == "removed").All(r => r.Record.StartsWith("(in no plugin now)", StringComparison.Ordinal)), "a removed record has a name, though the history keeps none");
        Check(one.RecordChanges.Where(r => r.Change == "removed").Select(r => r.Signature).OrderBy(x => x).SequenceEqual(new[] { "AMDL", "KYWD" }), "the removed records' types are wrong");
        // The new copy read by Mutagen holds exactly what the history says
        log = new HistoryLog(history);
        var snap2 = log.Snapshots.Last(x => x.Plugin.Equals(Plugin, StringComparison.OrdinalIgnoreCase));
        Check(snap2.Records == snap1.Records + 1 && snap2.New == snap1.New + 1 && snap2.Overrides == snap1.Overrides, $"records {snap1.Records} → {snap2.Records}, new {snap1.New} → {snap2.New}");
        Check(snap2.Key != snap1.Key, "the changed copy has the same content key");

        // ---- 5. removed from the folder, and back
        File.Delete(copy);
        var gone = report.Changes(new ChangesOptions { Now = t0.AddHours(2) });
        Check(gone.Changes.Count(c => c.FoundNow) == 1 && gone.Changes.First(c => c.FoundNow).Kind == "removed", $"with the file gone: {string.Join("; ", gone.Changes.Where(c => c.FoundNow).Select(c => c.Kind + " " + c.Plugin))}");
        File.Copy(real.Path, copy, true);
        File.SetAttributes(copy, FileAttributes.Normal);
        var back = report.Changes(new ChangesOptions { Now = t0.AddHours(3) });
        Check(back.Changes.Count(c => c.FoundNow) == 1 && back.Changes.First(c => c.FoundNow).Kind == "back", $"with the file back: {string.Join("; ", back.Changes.Where(c => c.FoundNow).Select(c => c.Kind + " " + c.Plugin))}");
        // The original copy again: its record list is the one kept at the start (one file per copy)
        int lists = Directory.GetFiles(Path.Combine(history, "lists")).Length;
        Check(lists == 2 + masters.Count(m => new HistoryLog(history).Snapshots.Any(x => x.Plugin.Equals(Path.GetFileName(m), StringComparison.OrdinalIgnoreCase) && x.List != null)), $"{lists} record lists are kept");

        // ---- 6. since
        var all = report.Changes(new ChangesOptions { All = true, Now = t0.AddHours(4) });
        Check(all.Changes.Count == 3 && all.Changes.All(c => !c.FoundNow), $"every change since the history began: {all.Changes.Count}");
        var since = report.Changes(new ChangesOptions { Since = t0.AddHours(2).AddMinutes(30), Now = t0.AddHours(4) });
        Check(since.Changes.Count == 1 && since.Changes[0].Kind == "back", $"since 12:30: {since.Changes.Count} changes");
        try { report.Changes(new ChangesOptions { Plugin = "no_such_plugin.esm", Now = t0.AddHours(4) }); Check(false, "a plugin the history doesn't know gave an answer"); }
        catch (ReportException e) { Check(e.Line.StartsWith("Error: the history has no plugin named no_such_plugin.esm", StringComparison.Ordinal), e.Line); }

        long bytes = Directory.GetFiles(history, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        ctx.WriteReport("changes-made.txt", third.ToText() + "\n" + one.ToText(new ReportTextOptions { Detail = true }) + "\n" + all.ToText());
        return $"A copy of {Plugin} ({N(snap1.Records)} records) in the work folder with its {masters.Count} masters read from Data: the first look begins the history ({1 + masters.Count} snapshots, nothing reported); a second look, and one with the file's date set back to 2019, find nothing.{bigNote} " +
            $"The copy changed by Mutagen (2 records removed: {string.Join(", ", removed)}; 3 keywords added: {string.Join(", ", added)}): one row, \"updated: 3 records added, 2 removed ({N(snap1.Records)} → {N(snap2.Records)} records)\". " +
            $"The plugin's own history: 2 snapshots, and exactly those 3 added and 2 removed records, none changed type; the added ones named from the plugin as it is now, the removed ones by FormKey and type. " +
            $"The file deleted: \"removed from Data\"; put back: \"back in Data\". All: 3 changes; since 12:30: 1. The history folder: {Directory.GetFiles(history, "*", SearchOption.AllDirectories).Length} files, {bytes / 1024.0:F1} KB.";
    }

    public static string MasterSize(Ctx ctx)
    {
        var s = ctx.Session;
        string folder = Path.Combine(ctx.Folder("changes-master"), "data");
        Directory.CreateDirectory(folder);
        const string Master = "m11b_master.esm", Patch = "m11b_patch.esm";
        string masterPath = Path.Combine(folder, Master), patchPath = Path.Combine(folder, Patch);
        ctx.MustBeInWork(masterPath); ctx.MustBeInWork(patchPath);

        // A master with records of two types and no master of its own, and a patch that overrides two of them
        StarfieldMod MakeMaster(bool small)
        {
            var m = new StarfieldMod(ModKey.FromFileName(Master), StarfieldRelease.Starfield);
            if (small) m.IsSmallMaster = true;
            for (int i = 0; i < 3; i++) m.Keywords.AddNew($"m11b_keyword_{i}");
            for (int i = 0; i < 2; i++) m.Globals.AddNew($"m11b_global_{i}").Data = i + 1;
            return m;
        }
        void Write(StarfieldMod mod, string path, MutagenStyle self, MutagenStyle? master)
        {
            var order = new List<KeyedMasterStyle>();
            if (master != null) order.Add(new KeyedMasterStyle(ModKey.FromFileName(Master), master.Value));
            order.Add(new KeyedMasterStyle(mod.ModKey, self));
            var lookup = new LoadOrder<IModMasterStyledGetter>(order);
            mod.WriteToBinary(path, new BinaryWriteParameters
            {
                MasterFlagsLookup = lookup, ModKey = ModKeyOption.CorrectToPath, MastersListOrdering = new MastersListOrderingByLoadOrder(lookup),
                LowerRangeDisallowedHandler = new NoCheckIfLowerRangeDisallowed(), FormIDCompaction = FormIDCompactionOption.NoCheck, HeaderSizeFlags = HeaderSizeFlagsOption.NoCheck,
            });
        }
        var master = MakeMaster(false);
        Write(master, masterPath, MutagenStyle.Full, null);
        var patch = new StarfieldMod(ModKey.FromFileName(Patch), StarfieldRelease.Starfield);
        var keyword = master.Keywords.First();
        var global = master.Globals.First();
        patch.Keywords.GetOrAddAsOverride(keyword).EditorID = "m11b_keyword_0_patched";
        patch.Globals.GetOrAddAsOverride(global).EditorID = "m11b_global_0_patched";
        patch.Keywords.AddNew("m11b_patch_own_keyword");
        Write(patch, patchPath, MutagenStyle.Full, MutagenStyle.Full);

        using var index = new HeaderIndex(PluginSource.Folder(folder), new IndexOptions { ScratchFolder = Path.Combine(ctx.Folder("changes-master"), "scratch"), VersionText = "sfsreport-tests changes", PluginsTxt = "", Ccc = "" });
        string history = Path.Combine(ctx.Folder("changes-master"), "history");
        var report = new ChangesReport(s, index, history, "the test's folder");
        var t0 = new DateTime(2026, 2, 1, 9, 0, 0);

        var first = report.Changes(new ChangesOptions { Now = t0 });
        var p0 = index.Plugin(Patch)!;
        Check(first.FirstRun && first.SnapshotsAdded == 2 && first.AtRisk.Count == 0, $"the first look: {first.SnapshotsAdded} snapshots, {first.AtRisk.Count} at risk");
        Check(p0.Segment!.Count == 3 && p0.Problems.TypeMismatches.Count == 0 && p0.Problems.OverridesOfNothing.Count == 0 && !p0.Problems.Any, $"the patch as built: {p0.Segment.Count} records, problems: {string.Join("; ", p0.Problems.Lines())}");
        string keyBefore = p0.ContentKey.ToString();
        var before = new HistoryLog(history).ReadList(new HistoryLog(history).Snapshots.Single(x => x.Plugin == Patch))!;
        Check(before.Records.Keys.Count(k => k.EndsWith(":" + Master, StringComparison.OrdinalIgnoreCase)) == 2, $"the patch as built holds {before.Records.Keys.Count(k => k.EndsWith(":" + Master, StringComparison.OrdinalIgnoreCase))} records of its master");

        // The master becomes Small: the same records, the header's Small flag. The patch file isn't touched.
        Write(MakeMaster(true), masterPath, MutagenStyle.Small, null);
        Check((Tests.Made.HeaderFlags(masterPath) & 0x100) != 0, "Mutagen wrote the master without the Small flag");
        var second = report.Changes(new ChangesOptions { Now = t0.AddHours(1) });
        var p1 = index.Plugin(Patch)!;
        Check(p1.ContentKey.ToString() == keyBefore, "the patch file changed");
        var rows = second.Changes.Where(c => c.FoundNow).ToDictionary(c => c.Plugin, StringComparer.OrdinalIgnoreCase);
        Check(rows.Count == 2 && rows.ContainsKey(Master) && rows.ContainsKey(Patch), $"changes found: {string.Join("; ", second.Changes.Select(c => c.Plugin + ": " + c.What))}");
        Check(rows[Master].Kind == "updated" && U(rows[Master].What).Contains("size Full → Small", StringComparison.Ordinal), $"the master's row: {rows[Master].What}");
        Check(rows[Patch].Kind == "masters" && U(rows[Patch].What).Contains($"a master changed size: {Master} Full → Small", StringComparison.Ordinal), $"the patch's row: {rows[Patch].What}");
        var risk = second.AtRisk.SingleOrDefault(r => r.Plugin == Patch);
        Check(second.AtRisk.Count == 1 && risk != null && risk.Why.Any(w => U(w).Contains($"changed size after this copy was first seen: {Master} Full → Small", StringComparison.Ordinal)), $"at risk: {string.Join("; ", second.AtRisk.Select(r => r.Plugin + ": " + string.Join(" / ", r.Why)))}");
        string text = second.ToText();
        Check(text.Contains("Patches at risk (1)", StringComparison.Ordinal) && text.Contains($"| {Patch} | ", StringComparison.Ordinal) && second.Notes.Count > 0, $"the text doesn't flag the patch:\n{text}");
        // What the size change did to the patch: its two overrides no longer read as the master's records
        var after = new HistoryLog(history).ReadList(new HistoryLog(history).Snapshots.Last(x => x.Plugin == Patch))!;
        int stillMasters = after.Records.Keys.Count(k => k.EndsWith(":" + Master, StringComparison.OrdinalIgnoreCase));
        string reads = $"its 2 overrides now read as {(stillMasters == 0 ? "its own records" : $"{stillMasters} of the master's records")} ({string.Join(", ", after.Records.Keys.OrderBy(k => k))}; before: {string.Join(", ", before.Records.Keys.OrderBy(k => k))})";
        Check(stillMasters < 2 || p1.Problems.Any, $"the size change left the patch reading the same: {reads}");
        // The patch's own history says the same
        var one = report.Changes(new ChangesOptions { Plugin = Patch, Now = t0.AddHours(1).AddMinutes(1) });
        Check(one.History.Count == 2 && one.AtRisk.Count == 1 && one.NoRecordChanges != null && one.ToText().Contains("At risk:", StringComparison.Ordinal), $"the patch's history: {one.History.Count} snapshots, {one.AtRisk.Count} at risk");

        // The master Full again: the patch reads as built, and is no longer at risk
        Write(MakeMaster(false), masterPath, MutagenStyle.Full, null);
        var third = report.Changes(new ChangesOptions { Now = t0.AddHours(2) });
        Check(third.AtRisk.Count == 0, $"with the master Full again: {string.Join("; ", third.AtRisk.Select(r => r.Plugin + ": " + string.Join(" / ", r.Why)))}");
        Check(third.Changes.Any(c => c.FoundNow && c.Plugin == Patch && U(c.What).Contains($"{Master} Small → Full", StringComparison.Ordinal)), "the change back isn't reported");

        // The real Data folder: the plugins the header index knows as built for another master are the ones at risk
        var realIndex = s.Index;
        var expected = realIndex.Plugins.Where(p => p.Problems.TypeMismatches.Count > 0).Select(p => p.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var real = new ChangesReport(s, null, Path.Combine(ctx.Folder("changes-real"), "history")).Changes(new ChangesOptions { Now = t0 });
        Check(real.AtRisk.Select(r => r.Plugin).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).SequenceEqual(expected), $"the real Data folder: at risk [{string.Join(", ", real.AtRisk.Select(r => r.Plugin))}], the index's problems [{string.Join(", ", expected)}]");
        var crimson = real.AtRisk.FirstOrDefault(r => r.Plugin == "darkstarpatch_crimsonx.esm");
        long realBytes = Directory.GetFiles(Path.Combine(ctx.Folder("changes-real"), "history"), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        ctx.WriteReport("changes-master-size.txt", text + "\n" + one.ToText() + "\n" + real.ToText());
        return $"A made master ({Master}, Full, 5 records) and a patch ({Patch}: 2 overrides, 1 new record), both written by Mutagen. The master written again as Small, the patch file untouched (the same content key): " +
            $"two rows, the master \"…size Full → Small\" and the patch \"a master changed size: {Master} Full → Small\"; the patch is the one plugin at risk, with the reason; {reads}. Its own history shows 2 snapshots of the one copy and the risk. With the master Full again nothing is at risk. " +
            $"The real Data folder, first look: {N(real.PluginsInData)} plugins remembered in {real.Seconds:F1} s ({realBytes / 1048576.0:F1} MB of history), {real.AtRisk.Count} at risk, the same {expected.Count} plugins whose overrides the header index finds of another type than their base" +
            (crimson != null ? $" (darkstarpatch_crimsonx.esm: {crimson.Why[0]})" : "") + ".";
    }

    /// <summary>
    /// GAPS S15: a localized plugin's string files are in its snapshot. A copy of a small localized plugin with its
    /// string files loose beside it (taken out of its archive in Data): the history begins with an index that doesn't
    /// look for string files (a log from before they were recorded), then one that does records them without calling it
    /// a change, then the English .strings file changes and changes says "strings changed".
    /// </summary>
    public static string Strings(Ctx ctx)
    {
        var s = ctx.Session;
        Check(s.Index.StampsStrings, "the session's index doesn't look for string files");
        var real = s.Index.Plugins
            .Where(p => p.Header is { Localized: true } && p.Segment != null && p.Problems.MissingMasters.Count == 0 && p.Stamp.Length < 4_000_000 && !p.Name.StartsWith("darkstarpatch_", StringComparison.OrdinalIgnoreCase)
                && !System.Text.RegularExpressions.Regex.IsMatch(p.Name, @"^sfta0[1-6]\.esm$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                && p.Strings.Files.Any(f => f.InArchive && f.Name.EndsWith("_en.strings", StringComparison.Ordinal)))
            .OrderBy(p => p.Stamp.Length).FirstOrDefault() ?? throw new TestFailed("no small localized plugin with its string files in an archive");
        string folder = Path.Combine(ctx.Folder("changes-strings"), "data");
        string loose = Path.Combine(folder, "Strings");
        Directory.CreateDirectory(loose);
        string copy = Path.Combine(folder, real.Name);
        ctx.MustBeInWork(copy);
        File.Copy(real.Path, copy, true);
        File.SetAttributes(copy, FileAttributes.Normal);
        var archive = real.Strings.Files.First(f => f.InArchive).From;
        var reader = Mutagen.Bethesda.Archives.Archive.CreateReader(GameRelease.Starfield, archive);
        Check(reader.TryGetFolder("strings", out var inArchive), $"{Path.GetFileName(archive)} has no Strings folder");
        foreach (var item in inArchive!.Files)
        {
            string name = Path.GetFileName(item.Path.ToString().Replace('/', '\\')).ToLowerInvariant();
            if (real.Strings.Files.Any(f => f.Name == name)) File.WriteAllBytes(Path.Combine(loose, name), item.GetBytes());
        }
        string en = Path.Combine(loose, real.Strings.Files.First(f => f.Name.EndsWith("_en.strings", StringComparison.Ordinal)).Name);
        ctx.MustBeInWork(en);
        var files = new[] { copy }.Concat(real.Header!.Masters.Select(m => s.Index.Plugin(m)!.Path)).ToList();
        string history = Path.Combine(ctx.Folder("changes-strings"), "history");
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0);

        // ---- 1. the history begins with an index that doesn't look for string files
        using (var old = Index(ctx, "changes-strings", files))
        {
            var first = new ChangesReport(s, old, history, "the test's folder").Changes(new ChangesOptions { Now = t0 });
            Check(first.FirstRun && first.Changes.Count == 0, $"the first look: {first.Changes.Count} changes");
        }
        var options = new IndexOptions { ScratchFolder = Path.Combine(ctx.Folder("changes-strings"), "scratch"), VersionText = "sfsreport-tests changes", PluginsTxt = "", Ccc = "", StringFiles = new SFSearch.Records.StringFiles(ctx.Data, null) };
        using var index = new HeaderIndex(PluginSource.FromFiles(files), options);
        var report = new ChangesReport(s, index, history, "the test's folder");
        // ---- 2. the same files with string files recorded: a line is added, and no change is listed
        var second = report.Changes(new ChangesOptions { Now = t0.AddMinutes(5) });
        var p = index.Plugin(real.Name)!;
        Check(p.Strings.Files.Count > 0 && p.Strings.Files.All(f => !f.InArchive), $"the copy's string files should be the loose ones: {string.Join(", ", p.Strings.Files.Take(3).Select(f => f.Where))}");
        Check(second.Changes.Count == 0 && second.SnapshotsAdded >= 1, $"recording the string files: {second.SnapshotsAdded} lines, {second.Changes.Count} changes ({string.Join("; ", second.Changes.Select(c => c.Plugin + ": " + c.What))})");
        var third = report.Changes(new ChangesOptions { Now = t0.AddMinutes(6) });
        Check(third.SnapshotsAdded == 0 && third.Changes.Count == 0, $"nothing changed: {third.SnapshotsAdded} lines");
        // ---- 3. the English .strings file changes (one byte of a text), the plugin doesn't
        var bytes = File.ReadAllBytes(en);
        Check(bytes.Length > 16, $"{Path.GetFileName(en)} holds no text");
        bytes[^2] = bytes[^2] == (byte)'x' ? (byte)'y' : (byte)'x';
        File.WriteAllBytes(en, bytes);
        var fourth = report.Changes(new ChangesOptions { Now = t0.AddHours(1) });
        Check(fourth.SnapshotsAdded == 1 && fourth.Changes.Count == 1, $"after the change: {fourth.SnapshotsAdded} lines, {fourth.Changes.Count} changes ({string.Join("; ", fourth.Changes.Select(c => c.Plugin + ": " + c.What))})");
        var row = fourth.Changes[0];
        Check(row.Plugin == real.Name && row.Kind == "strings" && row.What.Contains("strings changed", StringComparison.Ordinal) && !row.What.Contains("updated", StringComparison.Ordinal),
            $"the change reads: {row.Kind}: {row.What}");
        Check(fourth.ToText().Contains("strings changed", StringComparison.Ordinal), "the answer's text doesn't say strings changed");
        var one = report.Changes(new ChangesOptions { Plugin = real.Name, Now = t0.AddHours(1).AddMinutes(1) });
        Check(one.History.Count == 3 && one.History[1].What.Contains("recorded from now on", StringComparison.Ordinal), $"the plugin's history: {string.Join(" | ", one.History.Select(h => h.What))}");
        return $"a copy of {real.Name} with its {p.Strings.Files.Count} string files loose beside it. A log begun without string files gets one line that records them, listed as no change; then one byte of {Path.GetFileName(en)} changed: \"{row.Kind}: {row.What}\". The plugin's history: {string.Join(" | ", one.History.Select(h => h.What))}";
    }
}
