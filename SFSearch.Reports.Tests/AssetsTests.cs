// assets and materials, held against today's Assets and Materials on real mods, and the path fields held against
// Mutagen's own asset links. Every difference from today's must have a cause the test can show: a path found by its
// value in a field today's doesn't read, a kind of file today's leaves out, a record today's database doesn't store.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins.Assets;
using SFSearch.Index;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class AssetsTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The mods the user's notes name (each has 3 materials whose textures are nowhere), darkstar.esm, and two more.</summary>
    public static readonly string[] Plugins = { "aa50_longwatch.esm", "apc_galaxysmartcontainers.esm", "darkstar.esm", "atom74.esm", "kgcdoom.esm" };
    static readonly HashSet<string> TodayKinds = new(StringComparer.OrdinalIgnoreCase) { ".nif", ".mesh", ".mat" };

    public static string Stem(string plugin) => Path.GetFileNameWithoutExtension(plugin);

    /// <summary>One spelling for a path whatever folder it is written from: lower case, backslashes, no "data\", no "meshes\".</summary>
    static string Canon(string path)
    {
        string p = path.Trim().Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
        if (p.StartsWith("data\\", StringComparison.Ordinal)) p = p[5..];
        if (p.StartsWith("meshes\\", StringComparison.Ordinal)) p = p[7..];
        return p;
    }

    sealed record TodayRow(string FormKey, string Signature, string Field, string Path, string Status, string Source);

    public static string Today(Ctx ctx)
    {
        if (!ctx.HasToday) return "SKIP no --today folder and no --sfsearch with --home: today's Assets can't be asked";
        var report = new AssetsReport(ctx.Session);
        var log = new StringBuilder();
        var parts = new List<string>();
        long allSame = 0, allToday = 0, allNew = 0;
        var whyNew = new Dictionary<string, int>(StringComparer.Ordinal);
        var whyStatus = new Dictionary<string, int>(StringComparer.Ordinal);
        int typedFields = 0, typedPlaces = 0, typedTypes = 0;

        foreach (var plugin in Plugins)
        {
            string? today = ctx.Today($"assets__{Stem(plugin)}", "Assets", plugin);
            string? stored = ctx.Today($"records__{Stem(plugin)}", "Query", plugin, "SELECT FormKey FROM Records");
            if (today == null || stored == null) return "SKIP today's answers aren't there and can't be made";
            var storedKeys = Ctx.Keys(stored).ToHashSet(StringComparer.Ordinal);
            var rows = Ctx.Table(today, "FormKey").Where(c => c.Length >= 7).Select(c => new TodayRow(Ctx.Key(c[0]), c[1], c[3], c[4], c[5], c[6])).ToList();
            var m = Regex.Match(today, @"\((\d+) shown, (\d+) MISSING\)");
            Check(m.Success && int.Parse(m.Groups[1].Value) == rows.Count, $"{plugin}: today's heading says {m.Value}, its table has {rows.Count} rows");

            var mine = report.Assets(plugin);
            typedFields = mine.TypedFields; typedPlaces = mine.TypedPlaces; typedTypes = mine.TypedRecordTypes;
            var mineByKey = mine.Rows.GroupBy(r => (Ctx.Key(r.FormKey), Canon(r.Path))).ToDictionary(g => g.Key, g => g.ToList());
            int same = 0, statusSame = 0;
            var used = new HashSet<AssetRow>();
            foreach (var g in rows.GroupBy(r => (r.FormKey, Canon(r.Path))))
            {
                Check(mineByKey.TryGetValue(g.Key, out var have), $"{plugin}: today's lists {g.First().Path} on {g.Key.Item1} ({g.First().Field}), the new report doesn't");
                // Today's rows come from fields named File: pair them with the new report's rows of the same kind first
                var pool = have!.OrderBy(r => r.Typed && r.Field.EndsWith(".File", StringComparison.Ordinal) ? 0 : 1).ToList();
                var todays = g.ToList();
                Check(pool.Count >= todays.Count, $"{plugin}: today's lists {g.First().Path} on {g.Key.Item1} {todays.Count} times, the new report {pool.Count}");
                for (int i = 0; i < todays.Count; i++)
                {
                    var t = todays[i]; var r = pool[i];
                    used.Add(r);
                    same++;
                    bool okToday = t.Status.StartsWith("OK", StringComparison.Ordinal), okNew = r.Status == AssetStatus.Found;
                    if (okToday == okNew) { statusSame++; continue; }
                    // The one way the two may differ: today's looks only in the plugin's and its masters' archives and three of the game's
                    string why = !okToday && okNew && r.Origin == FileOrigin.OtherLoaded ? "found in an archive of another loaded plugin, where today's doesn't look"
                        : !okToday && okNew && r.Origin == FileOrigin.Master ? "found in an archive of a master that today's doesn't read"
                        : "";
                    Check(why.Length > 0, $"{plugin}: {t.Path} on {t.FormKey} is {t.Status} today and {r.Status} ({r.Where}) in the new report");
                    whyStatus[why] = whyStatus.GetValueOrDefault(why) + 1;
                    log.Append($"{plugin}\tstatus\t{t.FormKey}\t{t.Path}\ttoday {t.Status}\tnew {r.Status} in {r.Where}\t{why}\n");
                }
            }
            // What only the new report has
            var todayArmaPaths = rows.Where(r => r.Signature == "Armor->ARMA").Select(r => Canon(r.Path)).ToHashSet(StringComparer.Ordinal);
            int onlyNew = 0;
            foreach (var r in mine.Rows.Where(r => !used.Contains(r)))
            {
                onlyNew++;
                string ext = PathWalker.ExtensionOf(r.Path);
                string why = !r.Typed ? "found by value, in a field that isn't named File"
                    : !TodayKinds.Contains(ext) ? "a kind of file today's leaves out (not .nif, .mesh or .mat)"
                    : r.Via != null && todayArmaPaths.Contains(Canon(r.Path)) ? "an armor addon shared by several armors: today's lists it under the first armor only"
                    : r.Via == null && !storedKeys.Contains(Ctx.Key(r.FormKey)) ? "on a record today's database doesn't store"
                    : "";
                Check(why.Length > 0, $"{plugin}: only the new report lists {r.Path} on {r.Record} ({r.Field}{(r.Via != null ? ", via " + r.Via : "")}), and no cause is known");
                whyNew[why] = whyNew.GetValueOrDefault(why) + 1;
                log.Append($"{plugin}\tonly new\t{r.FormKey}\t{r.Field}\t{r.Path}\t{r.Status}\t{why}\n");
            }
            int missingToday = rows.Count(r => r.Status == "MISSING");
            Check(int.Parse(m.Groups[2].Value) == missingToday, $"{plugin}: today's heading counts {m.Groups[2].Value} missing, its rows {missingToday}");
            allSame += same; allToday += rows.Count; allNew += onlyNew;
            parts.Add($"{plugin}: today {N(rows.Count)} paths ({N(missingToday)} missing), all in the new report's {N(mine.Rows.Count)} ({N(mine.Count(AssetStatus.Missing))} missing, {N(mine.Rows.Count(r => r.Status == AssetStatus.Missing && r.InheritedFrom != null))} of them inherited; {N(mine.Count(AssetStatus.NoFile))} materials that are no file), {N(statusSame)} with the same result, {N(onlyNew)} only new");
            log.Append($"{plugin}\tsummary\ttoday {rows.Count}\tsame {same}\tstatus same {statusSame}\tonly new {onlyNew}\tnew total {mine.Rows.Count}\n");
        }
        ctx.WriteReport("assets-today.txt", log.ToString());
        return $"The schema types {typedFields} fields as file paths, at {N(typedPlaces)} places in {typedTypes} record types. {Plugins.Length} plugins: {N(allToday)} paths in today's Assets, each one in the new report on the same record ({N(allSame)}). {string.Join("; ", parts)}. " +
            $"Results that differ: {(whyStatus.Count == 0 ? "none" : string.Join("; ", whyStatus.Select(k => $"{N(k.Value)} {k.Key}")))}. " +
            $"Only in the new report, {N(allNew)}: {string.Join("; ", whyNew.OrderByDescending(k => k.Value).Select(k => $"{N(k.Value)} {k.Key}"))}.";
    }

    /// <summary>GAPS S25: an armor addon link to a record of another type is reported as the plugin's problem (with the
    /// record's real type from the header index), never read as an addon; and a master's armor addon is listed once,
    /// with every record of the plugin that uses it.</summary>
    public static string ArmorLinks(Ctx ctx)
    {
        var s = ctx.Session;
        var report = new AssetsReport(s);

        // Every path said to be on a master's armor addon is on a record the header index calls ARMA, listed once
        static void OnRealAddonsOnce(ReportSession s, AssetsAnswer a, string plugin)
        {
            foreach (var r in a.Rows.Where(r => r.Via != null))
            {
                Check(r.ViaFormKey != null && r.ViaPlugin != null && r.UsedBy.Count > 0 && r.UsedBy[0] == r.Record, $"{plugin}: {r.Path} via {r.Via} lacks its addon's FormKey, master or users");
                Check(HeaderIndex.TryParseFormKey(r.ViaFormKey!, out var owner, out var id) && ReportSession.In(s.Engine, r.ViaPlugin!, owner, id) is { Signature: "ARMA" },
                    $"{plugin}: {r.Path} is said to be on the armor addon {r.Via}, which isn't an ARMA in {r.ViaPlugin}");
            }
            var twice = a.Rows.Where(r => r.Via != null).GroupBy(r => (Ctx.Key(r.ViaFormKey!), r.Field, r.Path)).FirstOrDefault(g => g.Count() > 1);
            Check(twice == null, $"{plugin}: {twice?.Key} is listed {twice?.Count()} times");
        }

        // ---- the patch whose FormIDs aim at the wrong master: 91 links name an OMOD, a COBJ, a FURN, a MISC …
        const string bad = "darkstarpatch_bountyhunteroutfitz.esm";
        var b = report.Assets(bad);
        OnRealAddonsOnce(s, b, bad);
        var furn = b.WrongLinks.FirstOrDefault(w => Ctx.Key(w.FormKey) == "00084b:bountyhunter_outfit_z.esm" && Ctx.Key(w.TargetFormKey) == "000802:darkstar.esm");
        Check(furn is { TargetSignature: "FURN", InMaster: true } && furn.Field.StartsWith("Armatures[", StringComparison.Ordinal)
            && furn.Text.Contains("points at FURN furn_DarkStar_Workbench_weap_WeaponCraftingBench_INVIS (000802:darkstar.esm), not an armor addon: the FormID likely names the wrong master", StringComparison.Ordinal)
            && furn.SameIdAddons.Any(x => x.Contains("(000802:BountyHunter_Outfit_Z.esm)", StringComparison.OrdinalIgnoreCase)),
            $"{bad}: AA_BountyHunter_Shirt2's link to furn_DarkStar_Workbench_weap_WeaponCraftingBench_INVIS (000802:darkstar.esm) isn't reported as a FURN with the armor addon of the same ID: {furn?.Text ?? "not reported"}");
        Check(b.WrongLinks.Any(w => Ctx.Key(w.FormKey) == "000858:bountyhunter_outfit_z.esm" && Ctx.Key(w.TargetFormKey) == "000939:darkstar.esm" && w.TargetSignature == "MISC"),
            $"{bad}: DarkStar_Mk2_Apparel_bhz_BH_j14's link to DarkStar_EquipmentDatum (000939:darkstar.esm), a MISC, isn't reported");
        foreach (var w in b.WrongLinks)
        {
            Check(HeaderIndex.TryParseFormKey(w.TargetFormKey, out var owner, out var id) && ReportSession.In(s.Engine, w.TargetReadFrom, owner, id) is { } hit && hit.Signature == w.TargetSignature && hit.Signature != "ARMA",
                $"{bad}: {w.Text} names a type the header index doesn't give");
        }
        Check(b.Failed, $"{bad}: {b.WrongLinks.Count} wrong armor addon links, and the answer doesn't fail");
        string bText = b.ToText(new ReportTextOptions { MaxChars = int.MaxValue });
        Check(!bText.Contains(") ARMA, read from darkstar.esm", StringComparison.Ordinal) && bText.Contains($"Armor addon links that don't name an armor addon ({b.WrongLinks.Count})", StringComparison.Ordinal),
            $"{bad}: the text still calls a darkstar.esm record an armor addon, or doesn't list the wrong links");

        // ---- a patch with clean master armor addons, several shared by more than one armor
        const string clean = "darkstarpatch_nomadspacesuit.esm";
        var c = report.Assets(clean);
        OnRealAddonsOnce(s, c, clean);
        Check(c.WrongLinks.Count == 0, $"{clean}: {c.WrongLinks.Count} wrong armor addon links: {c.WrongLinks.FirstOrDefault()?.Text}");
        var neck = c.Rows.Where(r => r.ViaFormKey != null && Ctx.Key(r.ViaFormKey) == "00080a:nomad_spacesuit_z.esm").ToList();
        Check(neck.Count > 0 && neck.All(r => r.UsedBy.Count == 3), $"{clean}: AA_E700_NeckCover (00080A:Nomad_Spacesuit_Z.esm) has {neck.Count} rows used by {string.Join(" / ", neck.Select(r => r.UsedBy.Count))} records; three masks use it");
        Check(!c.Failed && c.OwnProblems.Count() == 0, $"{clean}: the plugin's own problems: {c.OwnProblems.Count()}");
        int addons = c.Rows.Where(r => r.ViaFormKey != null).Select(r => Ctx.Key(r.ViaFormKey!)).Distinct().Count();
        int shared = c.Rows.Where(r => r.ViaFormKey != null).GroupBy(r => Ctx.Key(r.ViaFormKey!)).Count(g => g.First().UsedBy.Count > 1);
        return $"{bad}: {b.WrongLinks.Count} armor addon links name another type ({string.Join(", ", b.WrongLinks.GroupBy(w => w.TargetSignature).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}), each with its real type from the header index; " +
            $"{b.WrongLinks.Count(w => w.SameIdAddons.Count > 0)} of them have an armor addon of the same ID in another master; {b.Rows.Count(r => r.Via != null)} paths read through them. " +
            $"{clean}: no wrong link; {c.Rows.Count(r => r.Via != null)} paths on {addons} master armor addons, each listed once ({shared} used by more than one record), all ARMA.";
    }

    /// <summary>The path fields the schema gives are Mutagen's own asset links, and the paths the walker finds in
    /// them are the paths Mutagen's own enumeration gives, record by record.</summary>
    public static string Paths(Ctx ctx)
    {
        var s = ctx.Session;
        var walker = new PathWalker(s.Schema, AssetsReport.ByValueExtensions);

        // ---- the schema's path fields against Mutagen's own classes: every property that is an asset link
        var assembly = typeof(Mutagen.Bethesda.Starfield.StarfieldMod).Assembly;
        var fromMutagen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in assembly.GetTypes())
        {
            if (!t.IsClass || t.IsAbstract && t.IsSealed || t.Namespace != "Mutagen.Bethesda.Starfield" || t.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal) || t.IsNested) continue;
            foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                if (typeof(IAssetLinkGetter).IsAssignableFrom(p.PropertyType) && !p.Name.Contains('.')) fromMutagen.Add($"{t.Name}.{p.Name}");
        }
        var fromSchema = walker.TypedFields.Select(f => $"{f.Object}.{f.Name}").ToHashSet(StringComparer.Ordinal);
        Check(fromSchema.SetEquals(fromMutagen), $"the schema's path fields ({string.Join(", ", fromSchema.OrderBy(x => x))}) aren't Mutagen's asset link properties ({string.Join(", ", fromMutagen.OrderBy(x => x))})");
        var places = walker.TypedPlaces();

        // ---- record by record: the walker's typed paths against Mutagen's enumeration
        long records = 0, withPaths = 0, typed = 0, byValue = 0, under = 0;
        var byField = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var plugin in new[] { "atom74.esm", "aa50_longwatch.esm", "apc_galaxysmartcontainers.esm", "kgcdoom.esm", "sfta04.esm" })
        {
            var side = s.Side(plugin);
            side.Engine.Sessions.Use(side.Name);
            var seg = side.Segment;
            for (int i = 0; i < seg.FirstUnresolved; i++)
            {
                var e = seg[i];
                if (ReportSession.IsWorld(seg, e) || seg.SignatureOf(e) == "NAVI") continue;
                var r = new RecordRef(side.Plugin.Id, i);
                var rec = side.Engine.Load(r);
                var json = side.Engine.Json(rec);
                records++;
                var mine = new List<string>();
                int before = (int)(typed + byValue);
                walker.Walk(rec.Type, json, p =>
                {
                    if (p.Typed) { mine.Add(p.Value); typed++; }
                    else { byValue++; string k = $"{rec.Info.Signature} {PathWalker.Plain(p.Path)}"; byField[k] = byField.GetValueOrDefault(k) + 1; }
                });
                if (typed + byValue > before) withPaths++;
                var theirs = new List<string>(); var relative = new List<string>();
                if (rec.Record is IAssetLinkContainerGetter container)
                    foreach (var link in container.EnumerateAssetLinks(AssetLinkQuery.Listed, null, null, iterateNestedRecords: false))
                    {
                        if (string.IsNullOrEmpty(link.GivenPath)) continue;
                        theirs.Add(link.GivenPath); relative.Add(link.DataRelativePath.Path);
                    }
                mine.Sort(StringComparer.Ordinal); var sortedTheirs = theirs.OrderBy(x => x, StringComparer.Ordinal).ToList();
                Check(mine.SequenceEqual(sortedTheirs), $"{rec.Label} {rec.Info.Signature}: the walker finds [{string.Join(", ", mine)}] in fields typed as paths, Mutagen's own enumeration [{string.Join(", ", sortedTheirs)}]");
                // The Data-relative path Mutagen works out is one of the spellings the report looks a path up under
                for (int k = 0; k < theirs.Count; k++)
                {
                    string want = ArchiveStore.Normalize(relative[k]);
                    if (PathWalker.ExtensionOf(theirs[k]).Length == 0) continue; // an icon's name: not a file name as written
                    Check(AssetsReport.Candidates(theirs[k]).Any(c => c.Equals(want, StringComparison.OrdinalIgnoreCase)), $"{rec.Label}: Mutagen reads {theirs[k]} as {relative[k]}; the report looks under {string.Join(", ", AssetsReport.Candidates(theirs[k]))}");
                    under++;
                }
            }
        }
        var top = byField.OrderByDescending(k => k.Value).Take(8).Select(k => $"{k.Key} {N(k.Value)}");
        return $"The schema's {fromSchema.Count} path fields ({string.Join(", ", fromSchema.OrderBy(x => x, StringComparer.Ordinal))}) are exactly the properties Mutagen's own classes type as asset links; they can be reached at {N(places.Values.Sum())} places in {places.Count} of {s.Schema.Model.RecordTypes.Count} record types. " +
            $"{N(records)} records of 5 plugins, {N(withPaths)} with a path: the walker's {N(typed)} paths in typed fields equal Mutagen's own enumeration of asset links in every record, and each of the {N(under)} Data-relative paths Mutagen works out is a spelling the report looks under. " +
            $"{N(byValue)} more paths were found by value, in {byField.Count} fields Mutagen types as plain text ({string.Join(", ", top)}{(byField.Count > 8 ? ", …" : "")}).";
    }
}

static class MaterialsTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    static readonly Regex MatInMesh = new(@"[A-Za-z0-9_\\/ .\-]+\.mat\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>How many times the meshes in a plugin's archives name a material, read with Mutagen's own archive reader.</summary>
    static int Occurrences(Ctx ctx, IEnumerable<string> archives, string material)
    {
        int n = 0;
        foreach (var a in archives)
            foreach (var f in Mutagen.Bethesda.Archives.Archive.CreateReader(Mutagen.Bethesda.GameRelease.Starfield, Path.Combine(ctx.Data, a)).Files)
            {
                if (!f.Path.ToString().EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (Match m in MatInMesh.Matches(Encoding.Latin1.GetString(f.GetBytes())))
                {
                    string p = m.Value.Trim().Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
                    if (p.StartsWith("data\\", StringComparison.Ordinal)) p = p[5..];
                    if (p == material) n++;
                }
            }
        return n;
    }

    public static string Today(Ctx ctx)
    {
        if (!ctx.HasToday) return "SKIP no --today folder and no --sfsearch with --home: today's Materials can't be asked";
        var report = new MaterialsReport(ctx.Session);
        var log = new StringBuilder();
        var parts = new List<string>();
        long materials = 0, textures = 0;
        int differences = 0, countedTwice = 0;
        foreach (var plugin in AssetsTests.Plugins)
        {
            string? today = ctx.Today($"materials__{AssetsTests.Stem(plugin)}", "Materials", plugin, "--all");
            if (today == null) return "SKIP today's answers aren't there and can't be made";
            var answer = report.Materials(plugin);
            // Today's reads the meshes only: the materials only the plugin's records name are new (materials-cdb)
            var mine = (Rows: answer.Rows.Where(m => m.FromMeshes).ToList(), answer.OwnArchives, answer.Meshes);
            var head = Regex.Match(today, @"\((\d+) archive\(s\), (\d+) mesh\(es\), (\d+) material\(s\)\)");
            var sum = Regex.Match(today, @"(\d+) material file\(s\) checked, (\d+) compiled \(not checked\); (\d+) missing texture\(s\); (\d+) unreadable");
            Check(head.Success && sum.Success, $"{plugin}: today's answer has no heading or no summary");
            int I(Match m, int g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
            Check(I(head, 1) == mine.OwnArchives.Count && I(head, 2) == mine.Meshes && I(head, 3) == mine.Rows.Count, $"{plugin}: today {head.Value}, new ({mine.OwnArchives.Count} archives, {mine.Meshes} meshes, {mine.Rows.Count} materials)");
            int noFile = mine.Rows.Count(m => m.NoFile), unreadable = mine.Rows.Count(m => m.Problem != null);
            Check(I(sum, 1) == mine.Rows.Count - noFile && I(sum, 2) == noFile && I(sum, 4) == unreadable, $"{plugin}: today {sum.Value}; new {mine.Rows.Count - noFile} checked, {noFile} no file, {unreadable} unreadable");

            var rows = Ctx.Table(today, "Material");
            Check(rows.Count == mine.Rows.Count, $"{plugin}: today's table has {rows.Count} materials, the new one {mine.Rows.Count}");
            int missingToday = 0, onlyTodayFinds = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                var t = rows[i]; var m = mine.Rows[i];
                Check(t[0] == m.Path, $"{plugin}: material {i} is {t[0]} today, {m.Path} new");
                bool compiledToday = t[1].StartsWith("compiled", StringComparison.Ordinal);
                Check(compiledToday == m.NoFile, $"{m.Path}: {(compiledToday ? "compiled today, a file in the new report" : "a file today, no file in the new report")}");
                if (!m.NoFile)
                {
                    Check(t[1] == m.Where, $"{m.Path}: found in {t[1]} today, {m.Where} new");
                    Check(t[3] == m.Textures.Count.ToString(CultureInfo.InvariantCulture), $"{m.Path}: {t[3]} textures today, {m.Textures.Count} new");
                }
                string usedBy = m.UsedBy.Count == 0 ? "(no mesh of this mod)" : m.UsedBy.Count == 1 ? m.UsedBy[0] : $"{m.UsedBy.Count} meshes";
                if (t[2] != usedBy)
                {
                    // Today's counts a mesh once for each time it names the material: counted here the same way, from the meshes themselves
                    int named = Occurrences(ctx, mine.OwnArchives, m.Path);
                    Check(named > m.UsedBy.Count && t[2] == $"{named} meshes", $"{m.Path}: used by {t[2]} today, {usedBy} new; the meshes name it {named} times");
                    countedTwice++;
                    log.Append($"{plugin}\t{m.Path}\ttoday \"{t[2]}\", new \"{usedBy}\": {m.UsedBy.Count} meshes name it {named} times\n");
                }
                var missT = (t.Length > 4 ? t[4] : "").Split(", ", StringSplitOptions.RemoveEmptyEntries).ToList();
                // Today's checks only a material file's textures; a compiled material's (read from its database now) aren't compared
                var missN = m.NoFile ? new List<string>() : m.MissingTextures.Select(x => x.Path).ToList();
                missingToday += missT.Count;
                if (!missT.SequenceEqual(missN))
                {
                    // The one way the two may differ: a texture found only in an archive the game doesn't load
                    var notLoaded = m.Textures.Where(x => x.Origin == FileOrigin.NotLoaded).Select(x => x.Path).ToList();
                    Check(missN.Except(missT).All(notLoaded.Contains) && !missT.Except(missN).Any(), $"{m.Path}: missing today [{string.Join(", ", missT)}], new [{string.Join(", ", missN)}]");
                    onlyTodayFinds += missN.Except(missT).Count();
                    log.Append($"{plugin}\t{m.Path}\tonly in an archive the game doesn't load: {string.Join(", ", missN.Except(missT))}\n");
                }
                materials++; textures += m.Textures.Count;
            }
            differences += onlyTodayFinds;
            int missingNew = mine.Rows.Where(m => !m.NoFile).Sum(m => m.MissingTextures.Count());
            Check(I(sum, 3) == missingToday && missingToday + onlyTodayFinds == missingNew, $"{plugin}: today counts {I(sum, 3)} missing textures, the new report {missingNew}");
            parts.Add($"{plugin} {mine.Meshes} meshes, {mine.Rows.Count} materials ({mine.Rows.Count - noFile} read, {noFile} no file: {mine.Rows.Count(m => m.Compiled)} compiled, {mine.Rows.Count(m => m.Missing)} missing), {N(mine.Rows.Sum(x => x.Textures.Count))} textures, {missingNew} missing in {mine.Rows.Count(m => m.MissingTextures.Any())} materials");
            log.Append($"{plugin}\t{mine.Meshes} meshes\t{mine.Rows.Count} materials\t{mine.Rows.Count - noFile} read\t{noFile} no file\t{missingNew} missing\n");
            foreach (var m in mine.Rows.Where(m => m.MissingTextures.Any())) log.Append($"{plugin}\tmissing\t{m.Path}\t{string.Join(", ", m.MissingTextures.Select(x => x.Path))}\n");
        }
        // The two mods the user's notes name
        foreach (var plugin in new[] { "aa50_longwatch.esm", "apc_galaxysmartcontainers.esm" })
        {
            var a = report.Materials(plugin);
            Check(a.MaterialsWithMissing == 3 && a.MissingTextures == 3 && a.Rows.Where(m => m.MissingTextures.Any()).All(m => m.MissingTextures.All(t => t.Origin == FileOrigin.None)),
                $"{plugin}: {a.MaterialsWithMissing} materials name {a.MissingTextures} textures that are nowhere; the notes say 3");
        }
        ctx.WriteReport("materials-today.txt", log.ToString());
        return $"{AssetsTests.Plugins.Length} plugins, {N(materials)} materials with {N(textures)} textures: the same materials in the same order as today's Materials, each found in the same place, used by the same meshes, with the same number of textures and the same missing ones" +
            (countedTwice > 0 ? $" ({countedTwice} materials have a higher mesh count today: today's counts a mesh once for each time it names the material, which the test counted again from the meshes)" : "") +
            (differences > 0 ? $" ({differences} more missing in the new report: only in an archive the game doesn't load)" : "") + $". {string.Join("; ", parts)}. " +
            "aa50_longwatch.esm and apc_galaxysmartcontainers.esm each have 3 materials naming a texture that is in no loose file and no archive.";
    }
}
