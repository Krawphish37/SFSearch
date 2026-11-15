using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SFSearch.Index;

namespace SFSearch.Index.Tests;

static class Tests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string Mb(long bytes) => (bytes / 1048576.0).ToString("N1") + " MB";

    /// <summary>Plugins the tests never copy or change, by the user's rule.</summary>
    static bool HandsOff(string name) => name.StartsWith("darkstarpatch_", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(name, @"^sfta0[1-6]\.esm$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // ---------------------------------------------------------------- Helpers for tests on copies
    /// <summary>A plugin's masters, and theirs, as paths in Data.</summary>
    static List<string> MasterClosure(HeaderIndex data, IEnumerable<IndexedPlugin> plugins, params string[] except)
    {
        var seen = new HashSet<string>(except, StringComparer.OrdinalIgnoreCase); var paths = new List<string>();
        void Add(IndexedPlugin p)
        {
            foreach (var m in p.Header?.Masters ?? Array.Empty<string>())
                if (seen.Add(m) && data.Plugin(m) is { } mp) { paths.Add(mp.Path); Add(mp); }
        }
        var list = plugins.ToList();
        foreach (var p in list) seen.Add(p.Name);
        foreach (var p in list) Add(p);
        return paths;
    }

    static string Copy(Ctx ctx, string folder, IndexedPlugin p)
    {
        string dest = Path.Combine(ctx.Folder(folder), p.Name);
        File.Copy(p.Path, dest, overwrite: true);
        return dest;
    }

    static void MustBeACopy(Ctx ctx, string path)
    {
        if (!Path.GetFullPath(path).StartsWith(ctx.Work + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"refusing to change {path}: it isn't inside the tests' work folder");
    }

    /// <summary>Changes one byte of a copy and keeps the file's size. With keepDate, its date too.</summary>
    static void FlipByte(Ctx ctx, string path, long at, bool keepDate, byte xor = 0xFF)
    {
        MustBeACopy(ctx, path);
        var date = File.GetLastWriteTimeUtc(path);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.Position = at; int b = fs.ReadByte();
            fs.Position = at; fs.WriteByte((byte)(b ^ xor));
        }
        if (keepDate) File.SetLastWriteTimeUtc(path, date);
        else File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
    }

    /// <summary>The last data byte of a record between two file positions: changing it leaves every record header as it was.</summary>
    static long HarmlessByte(Segment seg, long min, long max)
    {
        foreach (var e in seg.Entries)
        {
            long at = e.Offset + 24 + e.Size - 1;
            if (!e.IsCompressed && e.Size >= 16 && at >= min && at < max) return at;
        }
        throw new TestFailed($"{seg.Plugin}: no record ends between 0x{min:X} and 0x{max:X}");
    }

    static IndexedPlugin PickPlugin(HeaderIndex data, long minLength, long maxLength, Func<IndexedPlugin, bool>? also = null) =>
        data.Plugins.Where(p => p.Segment != null && !p.Problems.Any && !HandsOff(p.Name) && p.Stamp.Length >= minLength && p.Stamp.Length <= maxLength && p.Segment.Count >= 10 && p.Header!.Masters.Count >= 1 && (also == null || also(p)))
            .OrderBy(p => p.Stamp.Length).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
        ?? throw new TestFailed($"no plugin in Data between {minLength:N0} and {maxLength:N0} bytes fits this test");

    // ---------------------------------------------------------------- The tests
    public static string IndexAll(Ctx ctx)
    {
        var sw = Stopwatch.StartNew();
        var index = ctx.DataIndex;
        double seconds = sw.Elapsed.TotalSeconds;
        Check(seconds < 10, $"indexing took {seconds:F1} s; the target is under 10 s");
        long edids = 0, sorted = 0;
        foreach (var p in index.Plugins)
        {
            Check(p.Segment != null || p.Problems.ReadError != null, $"{p.Name} has neither a segment nor a stated reason");
            if (p.Segment is not { } seg) continue;
            edids += seg.EditorIdCount;
            Check(seg.Signatures.Sum(s => (long)s.Count) == seg.Count, $"{p.Name}: the counts per type don't add up to its {seg.Count:N0} records");
            // Every byte of the file is accounted for: the header record, every group header, every record
            long bytes = p.Header!.RecordsStart + 24L * seg.Groups + seg.Signatures.Sum(s => s.Bytes);
            Check(bytes == p.Stamp.Length, $"{p.Name}: records and groups add up to {bytes:N0} bytes, the file has {p.Stamp.Length:N0}");
            var entries = seg.Entries;
            for (int i = 1; i < entries.Length; i++) { if (entries[i - 1].Key > entries[i].Key) throw new TestFailed($"{p.Name}: entries {i - 1} and {i} are out of order"); sorted++; }
            Check(seg.FirstOwn <= seg.FirstUnresolved && (seg.FirstOwn == seg.Count || entries[seg.FirstOwn].Owner >= seg.SelfOwner) && (seg.FirstOwn == 0 || entries[seg.FirstOwn - 1].Owner < seg.SelfOwner), $"{p.Name}: the border between overrides and own records is wrong");
        }
        sw.Restart();
        var again = index.Refresh();
        Check(again.PassesRun.Count == 0 && again.Hashed.Count == 0 && !again.AnythingChanged, "a second refresh with nothing changed read or hashed a plugin");
        return $"{index.Plugins.Count} plugins ({index.Plugins.Sum(p => p.Stamp.Length) / 1073741824.0:F2} GiB) in {seconds:F2} s, hashes included: {index.TotalRecords:N0} records, {edids:N0} EditorIDs, segments {Mb(index.TotalSegmentBytes)}; " +
               $"every file's bytes accounted for; a second refresh with nothing changed {sw.Elapsed.TotalMilliseconds:F0} ms, 0 passes, 0 hashes";
    }

    public static string Lookups(Ctx ctx)
    {
        var index = ctx.DataIndex;
        // Every 97th record of every plugin: found again by FormKey, and by EditorID in another letter case
        long byKey = 0, byEdid = 0; var sw = Stopwatch.StartNew();
        foreach (var p in index.Plugins)
        {
            if (p.Segment is not { } seg) continue;
            for (int i = 0; i < seg.Count; i += 97)
            {
                var e = seg[i];
                if (e.IsUnresolved) continue;
                var me = new RecordRef(p.Id, i);
                var info = index.Describe(me);
                var versions = index.Versions(info.FormKey);
                Check(versions != null && versions.Holders.Any(h => h.Plugin == p.Id), $"{info.Label} of {p.Name} isn't found by its FormKey");
                byKey++;
                if (info.EditorId is { Length: > 0 } edid && byEdid < 20000)
                {
                    string other = edid.ToUpperInvariant() == edid ? edid.ToLowerInvariant() : edid.ToUpperInvariant();
                    Check(index.FindEditorId(other).Any(h => h.Plugin == p.Id && index.Describe(h).EditorId == edid), $"{info.Label} of {p.Name} isn't found by its EditorID written as {other}");
                    byEdid++;
                }
            }
            foreach (var s in seg.Signatures.Take(3))
                Check(index.RecordsOfType(p.Name, s.Signature).Count() == s.Count, $"{p.Name}: listing {s.Signature} gives another number than its count {s.Count}");
        }
        double seconds = sw.Elapsed.TotalSeconds;
        Check(index.Versions("ZZZZZZ:Starfield.esm") == null && index.Versions("FFFFFF:Starfield.esm") == null && index.Versions("000001:NotThere.esm") == null, "a FormKey that doesn't exist was found");
        Check(index.FindEditorId("NoSuchEditorIdAnywhere_xyz").Count == 0, "an EditorID that doesn't exist was found");
        // An ID that many plugins have as their own: all are listed, none is picked
        var local = index.FindLocalId(0x000CF2);
        Check(local.Count == 0 || local.Select(r => r.Plugin).Distinct().Count() == local.Count, "a bare local ID listed a plugin twice");
        return $"{byKey:N0} records found again by FormKey and {byEdid:N0} by EditorID in another letter case, in {seconds:F1} s; local ID 000CF2 is a record of {local.Count} plugins";
    }

    public static string LoadOrderMatchesMutagen(Ctx ctx)
    {
        var index = ctx.DataIndex;
        var mine = index.LoadOrderEntries.Where(e => e.Position >= 0).ToList();
        var theirs = MutagenCheck.LoadOrderListings(ctx.Data);
        Check(mine.Count == theirs.Count, $"the index lists {mine.Count} plugins, Mutagen {theirs.Count}");
        for (int i = 0; i < mine.Count; i++)
        {
            Check(mine[i].Name.Equals(theirs[i].Name, StringComparison.OrdinalIgnoreCase), $"place {i}: the index has {mine[i].Name}, Mutagen has {theirs[i].Name}");
            Check(mine[i].Position == i, $"place {i}: {mine[i].Name} has position {mine[i].Position}");
            if (mine[i].InData) Check(mine[i].Enabled == theirs[i].Enabled, $"{mine[i].Name}: the index says enabled = {mine[i].Enabled}, Mutagen says {theirs[i].Enabled}");
        }
        // Slots, worked out again the way today's LoadOrder command does it
        int full = 0, medium = 0, small = 0;
        foreach (var e in mine.Where(e => e.Enabled))
        {
            var p = index.Plugin(e.Name)!;
            bool light = (p.Header!.Flags & 0x100) != 0 || e.Name.EndsWith(".esl", StringComparison.OrdinalIgnoreCase);
            string slot = light ? $"FE:{small++:X3}" : (p.Header.Flags & 0x400) != 0 ? $"FD:{medium++:X2}" : $"{full++:X2}";
            Check(e.Slot == slot, $"{e.Name}: slot {e.Slot}, expected {slot}");
        }
        int unlisted = index.LoadOrderEntries.Count(e => e.Position < 0);
        return $"{mine.Count} listed plugins in Mutagen's order with the same enabled marks; {mine.Count(e => e.Enabled)} loaded ({full} Full, {medium} Medium, {small} Small slots), {mine.Count(e => !e.InData)} listed but not in Data, {unlisted} in Data but not listed";
    }

    public static string EqualsMutagen(Ctx ctx)
    {
        var index = ctx.DataIndex;
        var named = new[] { "Starfield.esm", "kgcdoom.esm", "darkstar.esm", "atom74.esm", "SFBGS050.esm" };
        foreach (var n in named) Check(index.Plugin(n)?.Segment != null, $"{n} isn't in Data: the comparison needs it");
        var set = new List<string>(named);
        var others = index.Plugins.Where(p => p.Segment != null && !named.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && p.Problems.MissingMasters.Count == 0).ToList();
        if (ctx.AllPlugins) set.AddRange(others.Select(p => p.Name));
        else
        {
            // 24 more: every Medium plugin, then Small and Full ones by size, biggest first, up to 60 MB
            var pick = others.Where(p => p.Stamp.Length <= 60 << 20).OrderByDescending(p => p.Stamp.Length).ToList();
            set.AddRange(pick.Where(p => p.Header!.Style == MasterStyle.Medium).Take(6).Select(p => p.Name));
            set.AddRange(pick.Where(p => p.Header!.Style == MasterStyle.Small).Take(8).Select(p => p.Name));
            set.AddRange(pick.Where(p => p.Header!.Style == MasterStyle.Full && !set.Contains(p.Name)).Take(24 - (set.Count - named.Length)).Select(p => p.Name));
        }
        long records = 0, edids = 0, parents = 0; int compared = 0, leftOut = 0; var byStyle = new Dictionary<MasterStyle, int>(); var skipped = new List<string>(); var bad = new List<string>();
        foreach (var name in set)
        {
            bool whole = ctx.StarfieldWhole || !name.Equals("Starfield.esm", StringComparison.OrdinalIgnoreCase);
            var sw = Stopwatch.StartNew();
            var r = MutagenCheck.Compare(index, name, ctx.Data, whole);
            if (r.Skipped != null) { skipped.Add($"{name}: {r.Skipped}"); continue; }
            compared++; records += r.Records; edids += r.EditorIds; parents += r.Parents;
            byStyle[r.Style] = byStyle.GetValueOrDefault(r.Style) + 1;
            if (named.Contains(name) || r.Differences.Count > 0 || r.LeftOut.Count > 0)
                Console.WriteLine($"      {name} ({r.Style}{(whole ? "" : ", every group but Cells and Worldspaces")}): {r.Records:N0} records, {r.EditorIds:N0} EditorIDs, {r.Parents:N0} parents, {r.Differences.Count} differences, {sw.Elapsed.TotalSeconds:F1} s");
            foreach (var d in r.Differences.Take(8)) Console.WriteLine("          " + d);
            foreach (var d in r.LeftOut) Console.WriteLine("          " + d);
            leftOut += r.LeftOut.Count;
            if (r.Differences.Count > 0) bad.Add(name);
            if ((compared & 7) == 0) GC.Collect();
        }
        foreach (var s in skipped) Console.WriteLine("      skipped " + s);
        Check(!skipped.Any(s => named.Any(n => s.StartsWith(n + ":"))), "one of the named plugins couldn't be compared");
        Check(bad.Count == 0, $"{bad.Count} plugin(s) differ from Mutagen: {string.Join(", ", bad.Take(10))}");
        Check(compared >= 25, $"only {compared} plugins were compared; the test asks for 25");
        return $"{compared} plugins ({byStyle.GetValueOrDefault(MasterStyle.Full)} Full, {byStyle.GetValueOrDefault(MasterStyle.Medium)} Medium, {byStyle.GetValueOrDefault(MasterStyle.Small)} Small): {records:N0} records with FormKey and signature equal to Mutagen's, {edids:N0} EditorIDs equal, {parents:N0} parents equal, 0 differences" +
               (leftOut > 0 ? $"; {leftOut} record(s) that repeat a FormKey of their plugin are left out by Mutagen (listed above)" : "") +
               (skipped.Count > 0 ? $"; {skipped.Count} skipped (Mutagen can't read them)" : "");
    }

    static string? TodayDb(Ctx ctx)
    {
        string? path = ctx.TodayDb ?? (Environment.GetEnvironmentVariable("SFSEARCH_HOME") is { Length: > 0 } home ? Path.Combine(home, "Cache", "_loadorder.db") : null);
        if (path == null)
        {
            // The checkout this worktree belongs to: ...\SFSearch\.claude\worktrees\<name>\...
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && dir.Name != ".claude") dir = dir.Parent;
            if (dir?.Parent != null) path = Path.Combine(dir.Parent.FullName, "SFSearch", "Cache", "_loadorder.db");
        }
        return path != null && File.Exists(path) ? path : null;
    }

    /// <summary>Today's load-order index, opened so that nothing is written beside it (immutable: no -shm or -wal file is made).</summary>
    static SqliteConnection OpenToday(string path)
    {
        var c = new SqliteConnection($"Data Source=file:///{path.Replace('\\', '/').Replace(" ", "%20")}?immutable=1;Mode=ReadOnly;Pooling=False");
        c.Open();
        return c;
    }

    static bool Official(string plugin) => new[] { "starfield.esm", "constellation.esm", "oldmars.esm", "blueprintships-starfield.esm", "shatteredspace.esm", "sfbgs" }.Any(x => plugin.StartsWith(x, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether today's load-order index lists this record: it leaves out navmeshes, landscape, and the
    /// placed references (records inside a cell) of the game's own files.</summary>
    static bool ListedToday(IndexedPlugin p, int entry)
    {
        var seg = p.Segment!; var e = seg[entry];
        string sig = seg.SignatureOf(e);
        if (sig is "NAVM" or "LAND") return false;
        if (!Official(p.Name) || sig == "CELL" || !seg.TryGetParent(e, out int po, out uint pl)) return true;
        int at = seg.Find(po, pl);
        return !(at >= 0 && seg.SignatureOf(seg[at]) == "CELL");
    }

    public static string OlderMaster(Ctx ctx)
    {
        var index = ctx.DataIndex;
        var crimson = index.Plugin("darkstarpatch_crimsonx.esm");
        Check(crimson != null, "darkstarpatch_crimsonx.esm isn't in Data");
        Check(crimson!.Problems.TypeMismatches.Count == 105, $"darkstarpatch_crimsonx.esm: {crimson.Problems.TypeMismatches.Count} overrides of another type; today's SFSearch finds 105");
        var mine = index.Plugins.Where(p => p.Problems.TypeMismatches.Count > 0).ToDictionary(p => p.Name, p => p.Problems.TypeMismatches.Count, StringComparer.OrdinalIgnoreCase);
        // The same count as today's index would make it: only where it lists both the override and its base
        int Comparable(IndexedPlugin p) => p.Problems.TypeMismatches.Count(i =>
        {
            var e = p.Segment![i];
            var origin = index.Plugin(p.Segment.Names[e.Owner])!;
            return ListedToday(p, i) && ListedToday(origin, origin.Segment!.Find(origin.Segment.SelfOwner, e.LocalId));
        });
        string list = string.Join(", ", mine.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase).Select(m => $"{m.Key} {m.Value}"));
        string today = "today's load-order index wasn't found, so only the known case was checked";
        if (TodayDb(ctx) is string db)
        {
            var rows = new List<(string Name, string Stamp, long N)>();
            using (var c = OpenToday(db))
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT Plugin, coalesce(Stamp,''), coalesce(TypeMismatches,0) FROM Plugins";
                using var r = q.ExecuteReader();
                while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.GetInt64(2)));
            }
            int same = 0, stale = 0, more = 0; var missed = new List<string>(); var extra = new List<string>(); var counts = new List<string>();
            foreach (var (name, stamp, n) in rows)
            {
                var p = index.Plugin(name);
                if (p == null || stamp != $"{p.Stamp.Length}|{p.Stamp.WriteTicksUtc}") { if (n > 0) stale++; continue; }
                int comparable = mine.ContainsKey(name) ? Comparable(p) : 0;
                if (n == 0) { if (comparable > 0) extra.Add(name); continue; }
                if (!mine.TryGetValue(name, out int m)) missed.Add(name);
                else if (comparable != n) counts.Add($"{name}: today {n}, index {comparable} (of {m})");
                else { same++; if (m > n) more++; }
            }
            Check(missed.Count == 0, $"today's SFSearch flags {string.Join(", ", missed)}; the index doesn't");
            Check(counts.Count == 0, $"the counts differ: {string.Join("; ", counts)}");
            Check(extra.Count == 0, $"the index flags {string.Join(", ", extra)} on records today's index lists too, and today's SFSearch doesn't");
            today = $"today's SFSearch flags {same} plugin(s) whose file is unchanged since its index was made; the index flags the same ones with the same count on the records today's index lists" +
                    (more > 0 ? $"; for {more} of them the index finds more, on records today's index leaves out (the game's own placed references)" : "") +
                    (stale > 0 ? $"; {stale} flagged plugin(s) changed since, not compared" : "");
        }
        return $"darkstarpatch_crimsonx.esm: 105 overrides of another type than their base. Flagged in all: {list}. {today}";
    }

    public static string EqualsTodaysIndex(Ctx ctx)
    {
        var index = ctx.DataIndex;
        if (TodayDb(ctx) is not string db) return "today's load-order index (_loadorder.db) wasn't found: nothing compared";
        using var c = OpenToday(db);
        var fresh = new List<(long Id, IndexedPlugin P)>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT Id, Plugin, coalesce(Stamp,'') FROM Plugins";
            using var r = q.ExecuteReader();
            while (r.Read())
                if (index.Plugin(r.GetString(1)) is { Segment: not null } p && r.GetString(2) == $"{p.Stamp.Length}|{p.Stamp.WriteTicksUtc}") fresh.Add((r.GetInt64(0), p));
        }
        long rows = 0, edids = 0; int plugins = 0; var bad = new List<string>();
        foreach (var (id, p) in fresh.OrderBy(f => f.P.Name, StringComparer.OrdinalIgnoreCase))
        {
            var seg = p.Segment!;
            bool official = Official(p.Name);
            // What today's index lists: no navmeshes or landscape, and no placed references of the game's own files
            var mine = new Dictionary<string, (string Sig, string? Edid, bool Ovr)>(StringComparer.OrdinalIgnoreCase);
            var parentIsCell = new Dictionary<uint, bool>();
            for (int i = 0; i < seg.Count; i++)
            {
                var e = seg[i];
                string sig = seg.SignatureOf(e);
                if (sig is "NAVM" or "LAND" || e.IsUnresolved) continue;
                if (official && sig != "CELL" && e.Parent != Entry.NoParent)
                {
                    if (!parentIsCell.TryGetValue(e.Parent, out bool cell))
                    {
                        seg.TryGetParent(e, out int po, out uint pl);
                        int at = seg.Find(po, pl);
                        parentIsCell[e.Parent] = cell = at >= 0 && seg.SignatureOf(seg[at]) == "CELL";
                    }
                    if (cell) continue;
                }
                mine.TryAdd($"{e.LocalId:X6}:{seg.Names[e.Owner]}", (sig, seg.EditorId(i), e.Owner < seg.SelfOwner));
            }
            int differences = 0, n = 0;
            using var q = c.CreateCommand();
            q.CommandText = "SELECT FormKey, Sig4, EditorID, IsOverride FROM VersionRows WHERE PluginId = @i";
            q.Parameters.AddWithValue("@i", id);
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                n++;
                string fk = r.GetString(0); string? edid = r.IsDBNull(2) ? null : r.GetString(2);
                if (!mine.TryGetValue(fk, out var m)) { if (differences++ < 3) bad.Add($"{p.Name}: today has {fk}, the index doesn't"); continue; }
                if (m.Sig != r.GetString(1) || m.Edid != edid || m.Ovr != (r.GetInt64(3) == 1)) { if (differences++ < 3) bad.Add($"{p.Name} {fk}: today {r.GetString(1)} {edid} override={r.GetInt64(3)}, index {m.Sig} {m.Edid} override={m.Ovr}"); }
                if (edid != null) edids++;
            }
            if (n != mine.Count && differences++ < 3) bad.Add($"{p.Name}: today lists {n:N0} records, the index {mine.Count:N0}");
            rows += n; plugins++;
        }
        foreach (var b in bad.Take(20)) Console.WriteLine("      " + b);
        if (bad.Count > 0) throw new TestFailed($"{bad.Count} difference(s) from today's index, the first: {bad[0]}");
        Check(plugins >= 20, $"only {plugins} plugins of today's index are unchanged since it was made; the test asks for 20");
        return $"{plugins} plugins unchanged since today's index was made: {rows:N0} rows (FormKey, type, EditorID, override or not) equal, {edids:N0} of them with an EditorID";
    }

    public static string DateBackwards(Ctx ctx)
    {
        var data = ctx.DataIndex;
        var p = PickPlugin(data, 20_000, 120_000);
        string copy = Copy(ctx, "date", p);
        using var index = new HeaderIndex(PluginSource.FromFiles(MasterClosure(data, new[] { p }).Append(copy)), ctx.Options());
        index.Refresh();
        var before = index.Plugin(p.Name)!;
        Check(before.Segment != null, $"{p.Name}: the copy has no segment");
        var old = File.GetLastWriteTimeUtc(copy); var back = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(copy, back);
        var report = index.Refresh();
        var after = index.Plugin(p.Name)!;
        Check(report.StampChangedOnly.Contains(p.Name), $"{p.Name}: the date went from {old:yyyy-MM-dd} back to {back:yyyy-MM-dd} and the refresh didn't notice");
        Check(report.Hashed.Contains(p.Name) && report.PassesRun.Count == 0 && report.ContentChanged.Count == 0, "a date change alone should hash the file again and read nothing");
        Check(after.ContentKey == before.ContentKey && after.ReadingKey == before.ReadingKey && ReferenceEquals(after.Segment, before.Segment), "the content didn't change, so the keys and the segment must stay");
        Check(after.Stamp != before.Stamp && after.Stamp.WriteTicksUtc == back.Ticks, "the remembered stamp wasn't updated");
        return $"{p.Name} ({p.Stamp.Length:N0} bytes, a copy): date set from {old:yyyy-MM-dd} back to {back:yyyy-MM-dd}; noticed by the quick stamp; hashed again, content key unchanged, 0 header passes, segment kept";
    }

    public static string ContentChangeSmall(Ctx ctx)
    {
        var data = ctx.DataIndex;
        var p = PickPlugin(data, 20_000, 120_000);
        string copy = Copy(ctx, "small", p);
        using var index = new HeaderIndex(PluginSource.FromFiles(MasterClosure(data, new[] { p }).Append(copy)), ctx.Options(version: Ctx.Version + "/own-segments"));
        index.Refresh();
        var before = index.Plugin(p.Name)!;
        long at = HarmlessByte(before.Segment!, p.Stamp.Length / 3, p.Stamp.Length);
        FlipByte(ctx, copy, at, keepDate: true);
        var info = new FileInfo(copy);
        Check(info.Length == before.Stamp.Length && info.LastWriteTimeUtc.Ticks == before.Stamp.WriteTicksUtc, "the copy's size or date moved; the test wants them kept");
        var report = index.Refresh();
        var after = index.Plugin(p.Name)!;
        Check(report.ContentChanged.Contains(p.Name), $"{p.Name}: one byte changed at 0x{at:X} with size and date kept, and the refresh didn't notice");
        Check(after.ContentKey != before.ContentKey && after.ReadingKey != before.ReadingKey && report.PassesRun.Contains(p.Name), "a changed file needs a new content key, a new reading key and a new pass");
        Check(after.Stamp.Length == before.Stamp.Length && after.Stamp.WriteTicksUtc == before.Stamp.WriteTicksUtc && after.Stamp.EndsHash != before.Stamp.EndsHash, "only the hashed ends of the quick stamp should differ");
        return $"{p.Name} ({p.Stamp.Length:N0} bytes, a copy): 1 byte changed at 0x{at:X}, size and date kept; noticed by the quick stamp (a file up to 128 KB lies wholly inside the hashed first and last 64 KB); new content key, new segment";
    }

    public static string ContentChangeMiddle(Ctx ctx)
    {
        var data = ctx.DataIndex;
        var p = PickPlugin(data, 500_000, 5_000_000);
        string copy = Copy(ctx, "middle", p);
        using var index = new HeaderIndex(PluginSource.FromFiles(MasterClosure(data, new[] { p }).Append(copy)), ctx.Options(version: Ctx.Version + "/own-segments"));
        index.Refresh();
        var before = index.Plugin(p.Name)!;
        long at = HarmlessByte(before.Segment!, QuickStamp.EndBytes + 4096, p.Stamp.Length - QuickStamp.EndBytes - 4096);
        FlipByte(ctx, copy, at, keepDate: true);
        var quick = index.Refresh();
        bool quickNoticed = quick.AnythingChanged;
        var full = index.Refresh(verifyContent: true);
        var after = index.Plugin(p.Name)!;
        Check(full.ContentChanged.Contains(p.Name) && after.ContentKey != before.ContentKey && after.ReadingKey != before.ReadingKey, $"{p.Name}: one byte changed at 0x{at:X} and the full content check didn't notice");
        Check(!quickNoticed, "the quick stamp noticed a change outside the bytes it hashes: the test's byte is in the wrong place");
        return $"{p.Name} ({p.Stamp.Length:N0} bytes, a copy): 1 byte changed at 0x{at:X} (outside the first and last 64 KB), size and date kept; NOT noticed by the quick stamp; noticed by the content key when every file is hashed again (Refresh with verifyContent, {full.Seconds:F2} s for this set); new segment";
    }

    public static string MissingMaster(Ctx ctx)
    {
        var data = ctx.DataIndex;
        // A plugin that overrides records of a Small or Medium master (trap 14), else of any master that isn't its first
        (IndexedPlugin P, int Master, int Through)? best = null;
        foreach (var p in data.Plugins.Where(p => p.Segment != null && !p.Problems.Any && !HandsOff(p.Name) && p.Stamp.Length <= 10 << 20 && p.Header!.Masters.Count >= 2).OrderBy(p => p.Stamp.Length))
        {
            var seg = p.Segment!;
            for (int k = 1; k < p.Header!.Masters.Count; k++)
            {
                int through = seg.LowerBound(k + 1, 0) - seg.LowerBound(k, 0);
                if (through == 0) continue;
                bool sized = p.MasterStyles[k] != MasterStyle.Full;
                // Prefer a Small or Medium master, then the one with the most records through it
                bool bestSized = best != null && best.Value.P.MasterStyles[best.Value.Master] != MasterStyle.Full;
                if (best == null || (sized && !bestSized) || (sized == bestSized && through > best.Value.Through)) best = (p, k, through);
            }
        }
        Check(best != null, "no plugin in Data overrides a record of a master that isn't its first");
        var (plugin, mi, n) = best!.Value;
        string missing = plugin.Header!.Masters[mi]; var style = plugin.MasterStyles[mi];
        var full = plugin.Segment!;
        string copy = Copy(ctx, "missing", plugin);
        var others = MasterClosure(data, new[] { plugin }, missing);
        using (var index = new HeaderIndex(PluginSource.FromFiles(others.Append(copy)), ctx.Options()))
        {
            index.Refresh();
            var p = index.Plugin(plugin.Name)!; var seg = p.Segment!;
            Check(p.Problems.MissingMasters.SequenceEqual(new[] { missing }), $"missing masters reported: {string.Join(", ", p.Problems.MissingMasters)}; expected {missing}");
            int throughMissing = 0, resolved = 0, wrong = 0;
            for (int i = 0; i < seg.Count; i++)
            {
                var e = seg[i];
                if (e.IsUnresolved) continue;
                resolved++;
                if (e.Owner >= mi) throughMissing++;
                // What still resolves must be exactly what the whole Data folder gives
                int at = full.Find(e.Owner, e.LocalId);
                if (at < 0 || full.SignatureOf(full[at]) != seg.SignatureOf(e)) wrong++;
            }
            Check(throughMissing == 0, $"{throughMissing} records were mapped to the missing master, to a master after it, or to the plugin itself");
            Check(wrong == 0, $"{wrong} records resolved to something else than with the master there");
            int expectUnresolved = full.Count - full.LowerBound(mi, 0);
            Check(seg.Unresolved == expectUnresolved && p.Problems.Unresolved == expectUnresolved, $"{seg.Unresolved:N0} records are unresolved; expected {expectUnresolved:N0}");
            Check(index.Versions(missing, full[full.LowerBound(mi, 0)].LocalId) == null, "a record of the missing master was found");
            Check(p.ReadingKey != plugin.ReadingKey, "the reading key must differ when a master is missing");
            // The master comes back: a new reading key, and everything resolves again
            using var whole = new HeaderIndex(PluginSource.FromFiles(others.Append(copy).Append(data.Plugin(missing)!.Path)), ctx.Options());
            whole.Refresh();
            var back = whole.Plugin(plugin.Name)!;
            Check(back.ReadingKey == plugin.ReadingKey && back.Segment!.Unresolved == 0 && back.Problems.MissingMasters.Count == 0, "with the master back, the plugin should read as it does in Data");
            return $"{plugin.Name} (a copy) without its {style} master {missing}: the missing master is reported; {seg.Unresolved:N0} of {seg.Count:N0} records stay unresolved (the {n:N0} through {missing}, and everything numbered after it, the plugin's own records included); " +
                   $"{resolved:N0} still resolve, all through the {mi} master(s) listed before it and all as in Data; 0 mapped through the missing master. With the master back: the reading key is the one in Data and 0 are unresolved";
        }
    }

    public static string MasterSizeChange(Ctx ctx)
    {
        var data = ctx.DataIndex;
        (IndexedPlugin P, IndexedPlugin M, int K, int Through)? pick = null;
        foreach (var p in data.Plugins.Where(p => p.Segment != null && !p.Problems.Any && !HandsOff(p.Name) && p.Stamp.Length <= 10 << 20).OrderBy(p => p.Stamp.Length))
        {
            for (int k = 0; k < p.Header!.Masters.Count; k++)
            {
                var m = data.Plugin(p.Header.Masters[k]);
                if (m == null || HandsOff(m.Name) || m.Stamp.Length > 30 << 20 || m.Problems.Any) continue;
                int through = p.Segment!.LowerBound(k + 1, 0) - p.Segment.LowerBound(k, 0);
                if (through > 0 && (pick == null || through > pick.Value.Through)) pick = (p, m, k, through);
            }
        }
        Check(pick != null, "no plugin in Data overrides a record of a master small enough to copy");
        var (plugin, master, mi, n) = pick!.Value;
        string pCopy = Copy(ctx, "resize", plugin), mCopy = Copy(ctx, "resize", master);
        var others = MasterClosure(data, new[] { plugin, master });
        using var index = new HeaderIndex(PluginSource.FromFiles(others.Append(pCopy).Append(mCopy)), ctx.Options(version: Ctx.Version + "/own-segments"));
        index.Refresh();
        var before = index.Plugin(plugin.Name)!; var keep = index.Plugins.Where(x => x.Name != plugin.Name && x.Name != master.Name).ToDictionary(x => x.Name, x => x.Segment);
        // The master's copy changes size: its Small flag is switched (bit 0x100 of the header flags, byte 9 of the file)
        var was = master.Header!.Style;
        MustBeACopy(ctx, mCopy);
        using (var fs = new FileStream(mCopy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fs.Position = 9; int b = fs.ReadByte();
            b = was == MasterStyle.Small ? b & ~0x01 : (b | 0x01) & ~0x04;
            fs.Position = 9; fs.WriteByte((byte)b);
        }
        var report = index.Refresh();
        var after = index.Plugin(plugin.Name)!; var now = index.Plugin(master.Name)!.Header!.Style;
        Check(now != was, $"the copy of {master.Name} is still {was}");
        Check(report.ContentChanged.Contains(master.Name), $"{master.Name}'s copy changed and wasn't noticed");
        Check(report.Rekeyed.Contains(plugin.Name) && after.ReadingKey != before.ReadingKey && after.ContentKey == before.ContentKey, $"{plugin.Name} lists {master.Name}, so it needs a new reading key (its content key stays)");
        Check(report.PassesRun.Contains(plugin.Name), $"{plugin.Name} wasn't read again");
        Check(!report.Hashed.Contains(plugin.Name), $"{plugin.Name}'s content didn't change: it shouldn't be hashed again");
        int throughNow = after.Segment!.LowerBound(mi + 1, 0) - after.Segment.LowerBound(mi, 0);
        Check(throughNow != n, $"{plugin.Name} still maps {n} records to {master.Name}: its FormIDs should read differently now");
        var untouched = index.Plugins.Where(x => keep.ContainsKey(x.Name)).ToList();
        var reread = report.PassesRun.Where(x => x != plugin.Name && x != master.Name).ToList();
        var dependents = untouched.Where(x => x.Header!.Masters.Contains(master.Name, StringComparer.OrdinalIgnoreCase)).Select(x => x.Name).ToHashSet();
        Check(reread.All(dependents.Contains), $"plugins that don't list {master.Name} were read again: {string.Join(", ", reread.Where(x => !dependents.Contains(x)))}");
        Check(untouched.Where(x => !dependents.Contains(x.Name)).All(x => ReferenceEquals(x.Segment, keep[x.Name])), "a segment of a plugin that doesn't list the master was replaced");
        return $"{master.Name} (a copy) changed from {was} to {now}: its dependent {plugin.Name} got a new reading key and a new pass with no new hash; records it maps to {master.Name}: {n:N0} before, {throughNow:N0} after; " +
               $"{report.PassesRun.Count} passes in all ({string.Join(", ", report.PassesRun)}), the other {untouched.Count(x => !dependents.Contains(x.Name))} plugins of the set kept their segments";
    }

    public static string OneSegmentReplaced(Ctx ctx)
    {
        var data = ctx.DataIndex;
        var four = data.Plugins.Where(p => p.Segment != null && !p.Problems.Any && !HandsOff(p.Name) && p.Stamp.Length is >= 30_000 and <= 2_000_000 && p.Segment.Count >= 10)
            .OrderBy(p => p.Stamp.Length).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Take(4).ToList();
        Check(four.Count == 4, "fewer than 4 small plugins in Data");
        var copies = four.Select(p => Copy(ctx, "replace", p)).ToList();
        // Its own version text, so its segments are its own files and no other index of this run holds them mapped
        using var index = new HeaderIndex(PluginSource.FromFiles(MasterClosure(data, four).Concat(copies)), ctx.Options(version: Ctx.Version + "/own-segments"));
        index.Refresh();
        var before = index.Plugins.ToDictionary(p => p.Name, p => p.Segment);
        var target = index.Plugin(four[1].Name)!;
        string oldPath = target.Segment!.Path;
        long at = HarmlessByte(target.Segment, target.Stamp.Length / 2, target.Stamp.Length);
        FlipByte(ctx, copies[1], at, keepDate: false);
        var report = index.Refresh();
        Check(report.PassesRun.SequenceEqual(new[] { target.Name }) && report.Hashed.SequenceEqual(new[] { target.Name }), $"passes: {string.Join(", ", report.PassesRun)}; hashes: {string.Join(", ", report.Hashed)}; expected {target.Name} alone");
        Check(report.ContentChanged.SequenceEqual(new[] { target.Name }), "the changed plugin wasn't the one reported");
        int kept = index.Plugins.Count(p => p.Name != target.Name && ReferenceEquals(p.Segment, before[p.Name]));
        Check(kept == index.Plugins.Count - 1, $"only {kept} of {index.Plugins.Count - 1} other segments were kept");
        var fresh = index.Plugin(target.Name)!.Segment!;
        Check(fresh.Path != oldPath && !File.Exists(oldPath), "the changed plugin's old segment should be gone and a new one in its place");
        Check(fresh.Count == before[target.Name]!.Count, "one byte inside a record shouldn't change the number of records");
        return $"{index.Plugins.Count} plugins indexed, {target.Name} (a copy) changed: 1 pass and 1 hash (that plugin), {kept} other segments kept as they were, its old segment file removed and a new one made, in {report.Seconds * 1000:F0} ms";
    }

    public static string PluginsTxtChange(Ctx ctx)
    {
        var data = ctx.DataIndex;
        string txt = Path.Combine(ctx.Folder("order"), "plugins.txt");
        File.Copy(LoadOrder.DefaultPluginsTxt, txt, overwrite: true);
        var lines = File.ReadAllLines(txt).ToList();
        int LineOf(string plugin) => lines.FindIndex(l => l.Trim().TrimStart('*').Equals(plugin, StringComparison.OrdinalIgnoreCase));
        using var index = new HeaderIndex(PluginSource.Folder(ctx.Data), ctx.Options(txt));
        var first = index.Refresh();
        Check(first.PassesRun.Count == 0 && first.Hashed.Count == 0, $"the segments were all in the scratch folder, yet {first.PassesRun.Count} passes and {first.Hashed.Count} hashes ran");
        // A record that two enabled plugins of plugins.txt override
        (string Owner, uint Id, string Winner, string Second)? pick = null;
        foreach (var p in index.Plugins.Where(p => p.Segment != null && p.Order.Enabled))
        {
            var seg = p.Segment!;
            for (int i = 0; i < seg.FirstOwn && pick == null; i++)
            {
                var v = index.Versions(seg.Names[seg[i].Owner], seg[i].LocalId);
                if (v == null || v.Winner < 2) continue;
                var w = index.Plugins[v.Holders[v.Winner].Plugin]; var s = index.Plugins[v.Holders[v.Winner - 1].Plugin];
                bool listedOnly = !LoadOrder.Implicit.Contains(w.Name, StringComparer.OrdinalIgnoreCase) && !LoadOrder.Implicit.Contains(s.Name, StringComparer.OrdinalIgnoreCase);
                if (s.Order.Enabled && listedOnly && LineOf(w.Name) >= 0 && LineOf(s.Name) >= 0) pick =(seg.Names[seg[i].Owner], seg[i].LocalId, w.Name, s.Name);
            }
            if (pick != null) break;
        }
        Check(pick != null, "no record is overridden by two enabled plugins of plugins.txt");
        var (owner, id, winner, second) = pick!.Value;
        string label = index.Describe(index.Winner(owner, id)!.Value).Label;
        int records = index.OverriddenRecords;
        // 1. The two swap places
        int a = LineOf(winner), b = LineOf(second);
        (lines[a], lines[b]) = (lines[b], lines[a]);
        File.WriteAllLines(txt, lines);
        var sw = Stopwatch.StartNew();
        Check(index.RefreshLoadOrder(), "plugins.txt changed and the load order didn't");
        double ms = sw.Elapsed.TotalMilliseconds;
        string now = index.Plugins[index.Winner(owner, id)!.Value.Plugin].Name;
        Check(now == second, $"after the swap the winner of {label} is {now}; expected {second}");
        // 2. The new winner is switched off
        lines[LineOf(second)] = second;
        File.WriteAllLines(txt, lines);
        Check(index.RefreshLoadOrder(), "plugins.txt changed and the load order didn't");
        string then = index.Plugins[index.Winner(owner, id)!.Value.Plugin].Name;
        Check(then == winner, $"with {second} switched off the winner of {label} is {then}; expected {winner}");
        Check(index.OverriddenRecords == records, "the number of overridden records changed though no plugin did");
        // 3. A master is moved below a plugin that needs it, then switched off
        var needy = index.Plugins.Where(p => p.Order.Enabled && !p.Problems.Any && LineOf(p.Name) >= 0)
            .Select(p => (P: p, M: p.Header!.Masters.Select(m => index.Plugin(m)).FirstOrDefault(m => m != null && m.Order.Enabled && LineOf(m.Name) >= 0 && !LoadOrder.Implicit.Contains(m.Name, StringComparer.OrdinalIgnoreCase))))
            .FirstOrDefault(x => x.M != null);
        Check(needy.P != null, "no enabled plugin of plugins.txt has a master that plugins.txt lists too");
        string dependent = needy.P!.Name, master = needy.M!.Name;
        string masterLine = lines[LineOf(master)];
        lines.RemoveAt(LineOf(master));
        lines.Insert(LineOf(dependent) + 1, masterLine);
        File.WriteAllLines(txt, lines);
        index.RefreshLoadOrder();
        Check(index.Plugin(dependent)!.Problems.MastersLoadedAfter.Contains(master, StringComparer.OrdinalIgnoreCase), $"{master} now loads after {dependent} and that isn't reported");
        lines[LineOf(master)] = master;
        File.WriteAllLines(txt, lines);
        index.RefreshLoadOrder();
        Check(index.Plugin(dependent)!.Problems.MastersNotLoaded.Contains(master, StringComparer.OrdinalIgnoreCase), $"{master} is switched off, {dependent} needs it, and that isn't reported");
        var check = index.Refresh();
        Check(check.PassesRun.Count == 0 && check.Hashed.Count == 0 && !check.LoadOrderChanged, "a refresh after the load-order change read a plugin again");
        return $"{label}: winner {winner}; {second} moved after it in a copy of plugins.txt -> winner {second}; {second} switched off -> winner {winner}. Each rework took about {ms:F0} ms with 0 passes and 0 hashes ({index.Plugins.Count} plugins, {records:N0} overridden records). " +
               $"{master} moved below {dependent}: reported as a master that loads after it; switched off: reported as a master not loaded";
    }

    public static string ChangedDuringRead(Ctx ctx)
    {
        var data = ctx.DataIndex;
        var p = PickPlugin(data, 100_000, 2_000_000);
        long at = HarmlessByte(p.Segment!, p.Stamp.Length / 2, p.Stamp.Length);
        string copy = Copy(ctx, "during", p);
        var files = MasterClosure(data, new[] { p }).Append(copy).ToList();
        // 1. The file changes once while it is being read: it is read again, and what is indexed is the new content
        int calls = 0;
        using (var index = new HeaderIndex(PluginSource.FromFiles(files), ctx.Options(version: Ctx.Version + "/own-segments")))
        {
            index.AfterReadForTests = name => { if (name == p.Name && Interlocked.Increment(ref calls) == 1) FlipByte(ctx, copy, at, keepDate: false); };
            index.Refresh();
            var got = index.Plugin(p.Name)!;
            Check(calls == 2, $"the plugin was read {calls} time(s); a change during the read should make it 2");
            Check(got.Segment != null && got.Problems.ReadError == null, "after one change during the read the plugin should be indexed");
            Check(got.ContentKey == Keys.Content(copy) && got.ContentKey != p.ContentKey, "what is indexed isn't the file as it is now");
        }
        // 2. It changes during every read: the plugin gets no segment and says why. Left alone, the next refresh reads it.
        int reads = 0;
        using (var index = new HeaderIndex(PluginSource.FromFiles(files), ctx.Options(version: Ctx.Version + "/own-segments")))
        {
            index.AfterReadForTests = name => { if (name == p.Name) FlipByte(ctx, copy, at, keepDate: false, xor: (byte)(1 << (Interlocked.Increment(ref reads) % 7))); };
            index.Refresh();
            var got = index.Plugin(p.Name)!;
            Check(got.Segment == null && got.Problems.ReadError != null && got.Problems.ReadError.Contains("changed while"), $"a plugin that changes during every read should be refused; it has {(got.Segment == null ? "no" : "a")} segment and says: {got.Problems.ReadError}");
            index.AfterReadForTests = null;
            index.Refresh();
            var later = index.Plugin(p.Name)!;
            Check(later.Segment != null && later.Problems.ReadError == null && later.ContentKey == Keys.Content(copy), "left alone, the plugin should be indexed by the next refresh");
        }
        return $"{p.Name} (a copy) changed while it was being read: read a second time, and the new content is what is indexed ({calls} reads). Changed during both reads: no segment, and the reason given is \"it changed while it was being read: run the command again\" ({reads} reads); the next refresh, with the file left alone, indexes it";
    }

    public static string BrokenFiles(Ctx ctx)
    {
        var data = ctx.DataIndex;
        var p = PickPlugin(data, 100_000, 2_000_000);
        string folder = ctx.Folder("broken");
        string good = Copy(ctx, "broken", p);
        // The same plugin cut off at 60% of its length, an empty file, and a text file, each with a plugin's name
        string cut = Path.Combine(folder, "cut_short.esm"), empty = Path.Combine(folder, "empty.esm"), text = Path.Combine(folder, "notaplugin.esp");
        var bytes = File.ReadAllBytes(good);
        File.WriteAllBytes(cut, bytes.AsSpan(0, bytes.Length * 6 / 10).ToArray());
        File.WriteAllBytes(empty, Array.Empty<byte>());
        File.WriteAllText(text, "this is not a plugin\n");
        using var index = new HeaderIndex(PluginSource.FromFiles(MasterClosure(data, new[] { p }).Concat(new[] { good, cut, empty, text })), ctx.Options());
        index.Refresh();
        Check(index.Plugin(p.Name)!.Segment != null && index.Plugin(p.Name)!.Segment!.Count == p.Segment!.Count, "the whole copy should read as in Data");
        var errors = new List<string>();
        foreach (var name in new[] { "cut_short.esm", "empty.esm", "notaplugin.esp" })
        {
            var b = index.Plugin(name);
            Check(b != null && b.Segment == null && b.Problems.ReadError != null, $"{name} should have no segment and a stated reason");
            errors.Add($"{name}: {b!.Problems.ReadError}");
        }
        var again = index.Refresh();
        Check(again.PassesRun.All(n => n != p.Name), "the good plugin was read again");
        return $"3 broken files beside a good one: no crash, no segment, each with its reason ({string.Join("; ", errors)}); the good plugin's {p.Segment!.Count:N0} records are indexed as in Data";
    }

    public static string TwoProcesses(Ctx ctx)
    {
        var data = ctx.DataIndex;
        string runner = ctx.Runner ?? FindRunner() ?? throw new TestFailed("sfsindex wasn't found: build SFSearch.Index.Runner in Release, or pass --runner");
        var segs = new[] { data.Plugin("Starfield.esm")?.Segment ?? data.Plugins.First(p => p.Segment != null).Segment!, PickPlugin(data, 20_000, 2_000_000).Segment! };
        var lines = new List<string>();
        foreach (var seg in segs)
        {
            var procs = Enumerable.Range(0, 2).Select(_ =>
            {
                var psi = new ProcessStartInfo { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                if (runner.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { psi.FileName = "dotnet"; psi.ArgumentList.Add(runner); } else psi.FileName = runner;
                foreach (var a in new[] { "map", seg.Path, "--hold", "2500" }) psi.ArgumentList.Add(a);
                return Process.Start(psi)!;
            }).ToList();
            try
            {
                var firsts = procs.Select(p => p.StandardOutput.ReadLine() ?? "").ToList();
                // Both runners hold the segment mapped now; this process maps it a third time
                using var third = Segment.TryOpen(seg.Path);
                Check(third != null, $"the test couldn't map {seg.Path} while two other processes had it mapped");
                string mine = $"mapped {third!.Plugin} {third.Count} {third.Checksum():x16}";
                Check(procs.All(p => !p.HasExited), "a runner had already let go of the segment");
                Check(firsts.All(f => f == mine), $"the mappings differ: {string.Join(" | ", firsts.Append(mine))}");
                var lasts = procs.Select(p => { string rest = p.StandardOutput.ReadToEnd(); p.WaitForExit(); return (rest.Trim(), p.ExitCode); }).ToList();
                Check(lasts.All(l => l.ExitCode == 0 && l.Item1 == mine.Replace("mapped", "still")), $"a runner ended badly: {string.Join(" | ", lasts)}");
                lines.Add($"{third.Plugin} ({Mb(third.FileBytes)}, {third.Count:N0} records, checksum {third.Checksum():x16})");
            }
            finally { foreach (var p in procs) { if (!p.HasExited) p.Kill(); p.Dispose(); } }
        }
        return $"3 processes (2 copies of sfsindex and the test) held the same segment mapped read only at once and read the same bytes: {string.Join("; ", lines)}";
    }

    static string? FindRunner()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "SFSearch.Index.Runner"))) dir = dir.Parent;
        if (dir == null) return null;
        foreach (var config in new[] { "Release", "Debug" })
        {
            string exe = Path.Combine(dir.FullName, "SFSearch.Index.Runner", "bin", config, "net10.0", "sfsindex.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    public static string ScratchGuard(Ctx ctx)
    {
        var data = ctx.DataIndex;
        var p = PickPlugin(data, 20_000, 120_000);
        string folder = Path.GetDirectoryName(Copy(ctx, "guard", p))!;
        int refused = 0;
        foreach (var scratch in new[] { folder, Path.Combine(folder, "scratch"), Path.Combine(folder, "a", "b") })
        {
            using var index = new HeaderIndex(PluginSource.Folder(folder), new IndexOptions { ScratchFolder = scratch, VersionText = Ctx.Version, PluginsTxt = "" });
            try { index.Refresh(); }
            catch (InvalidOperationException) { refused++; }
        }
        Check(refused == 3, $"{3 - refused} scratch folder(s) inside a plugin folder were accepted");
        var left = Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).ToList();
        Check(left.Count == 1 && left[0] == p.Name, $"the plugin folder now holds: {string.Join(", ", left)}");
        // And the real Data folder: the index of all of it wrote nothing there
        int strays = Directory.EnumerateFiles(ctx.Data).Count(f => f.EndsWith(".seg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals("stamps.tsv", StringComparison.OrdinalIgnoreCase));
        Check(strays == 0, $"{strays} index file(s) are in the Data folder");
        return $"a scratch folder that is the plugin folder, or inside it, is refused (3 of 3) before anything is written; the folder still holds only {p.Name}; no index file is in the Data folder";
    }
}
