// archive, held against today's Archive (the same archives, files, sizes and loose copies), and its extraction:
// files copied out equal Mutagen's own reading of the archive, nothing goes into the game's folders, and a file
// whose path inside the archive leads outside the target folder is refused.
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class ArchiveTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    static readonly string[] Plugins = { "darkstar.esm", "kgcdoom.esm", "atom74.esm" };
    static readonly (string Name, string Text)[] Finds =
    {
        ("bullpupdummyreceiver", "bullpupdummyreceiver"),
        ("cdb", "*.cdb"),
        ("scripts_darkstar", "scripts\\darkstar\\"),
        ("skeleton_rig", "characterassets\\skeleton.rig"),
    };

    static string Stem(string plugin) => Path.GetFileNameWithoutExtension(plugin);

    public static string Today(Ctx ctx)
    {
        if (!ctx.HasToday) return "SKIP no --today folder and no --sfsearch with --home: today's Archive can't be asked";
        var report = new ArchiveReport(ctx.Session);
        var log = new StringBuilder();
        long files = 0, archives = 0, sized = 0, loose = 0;
        var parts = new List<string>();

        foreach (var plugin in Plugins)
        {
            // ---- the summary
            string? today = ctx.Today($"archive__{Stem(plugin)}", "Archive", plugin);
            if (today == null) return "SKIP today's answers aren't there and can't be made";
            var mine = report.Summary(plugin);
            var rows = Ctx.Table(today, "Archive");
            Check(rows.Count == mine.Archives.Count, $"{plugin}: today's summary has {rows.Count} archives, the new one {mine.Archives.Count}");
            for (int i = 0; i < rows.Count; i++)
            {
                var a = mine.Archives[i];
                Check(rows[i][0] == a.Name, $"{plugin}: archive {i} is {rows[i][0]} today, {a.Name} new");
                Check(rows[i][1] == a.Files.ToString(CultureInfo.InvariantCulture), $"{a.Name}: {rows[i][1]} files today, {a.Files} new");
                Check(rows[i][2] == (a.Length / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture), $"{a.Name}: size {rows[i][2]} MB today, {a.Length / 1048576.0:0.0} new");
                // Today's lists its six most common kinds; ties can come in another order
                var kinds = a.Kinds.ToDictionary(k => k.Extension.Length == 0 ? "(none)" : k.Extension, k => k.Count);
                foreach (var k in rows[i][3].Split(", ", StringSplitOptions.RemoveEmptyEntries))
                {
                    int sp = k.LastIndexOf(' ');
                    Check(kinds.TryGetValue(k[..sp], out int n) && n == int.Parse(k[(sp + 1)..], CultureInfo.InvariantCulture), $"{a.Name}: today counts {k}, the new one {(kinds.TryGetValue(k[..sp], out int have) ? have : 0)}");
                }
                archives++;
            }

            // ---- every file
            today = ctx.Today($"archive__{Stem(plugin)}__list", "Archive", plugin, "--list", "--all")!;
            var list = report.List(plugin);
            var (same, withSize, withLoose) = SameFiles(plugin + " list", today, list);
            files += same; sized += withSize; loose += withLoose;
            Check(list.Files.Count == mine.Archives.Sum(a => a.Files), $"{plugin}: the list has {list.Files.Count} files, the summary {mine.Archives.Sum(a => a.Files)}");
            Check(mine.Archives.Sum(a => a.LooseCopies) == list.Files.Count(f => f.LooseCopy), $"{plugin}: the summary counts {mine.Archives.Sum(a => a.LooseCopies)} loose copies, the list {list.Files.Count(f => f.LooseCopy)}");
            log.Append($"{plugin}: {mine.Archives.Count} archives, {list.Files.Count} files, {withLoose} with a loose copy in Data\n");
            parts.Add($"{plugin} {mine.Archives.Count} archives and {N(list.Files.Count)} files");

            // ---- a pattern
            today = ctx.Today($"archive__{Stem(plugin)}__list_mat", "Archive", plugin, "--list", "*.mat", "--all")!;
            var mats = report.List(plugin, "*.mat");
            SameFiles(plugin + " *.mat", today, mats);
            Check(mats.Files.All(f => f.Path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)), $"{plugin}: *.mat lists a file that isn't a material");
        }

        // ---- find across every archive in Data
        long found = 0, lookedAt = 0; int allArchives = 0;
        var findParts = new List<string>();
        foreach (var (name, text) in Finds)
        {
            string today = ctx.Today($"archive__all__find_{name}", "Archive", "all", "--find", text, "--all")!;
            var mine = report.Find(text);
            var (same, _, _) = SameFiles($"find {text}", today, mine);
            found += same; lookedAt = mine.FilesLookedAt; allArchives = mine.Archives.Count;
            var m = Regex.Match(today, @"in (\d+) archive\(s\): (\d+)");
            Check(m.Success && int.Parse(m.Groups[1].Value) == mine.Archives.Count && int.Parse(m.Groups[2].Value) == mine.Files.Count, $"find {text}: today's heading says {m.Value}, the new one {mine.Archives.Count} archives and {mine.Files.Count} files");
            findParts.Add($"\"{text}\" {N(mine.Files.Count)}");
            log.Append($"find {text}: {mine.Files.Count} files in {mine.Files.Select(f => f.Archive).Distinct().Count()} archives\n");
        }

        // ---- the text of a file
        var mat = report.List("darkstar.esm", "*.mat").Files[0];
        string shownToday = ctx.Today("archive__darkstar__show_mat", "Archive", "darkstar.esm", "--show", mat.Path)!;
        var shown = report.Show("darkstar.esm", mat.Path);
        int open = shownToday.IndexOf("```", StringComparison.Ordinal), close = shownToday.LastIndexOf("```", StringComparison.Ordinal);
        Check(open >= 0 && close > open, "today's --show has no text block");
        string textToday = shownToday[(open + 3)..close].Replace("\r\n", "\n").Trim('\n');
        Check(shown.ShownIsText && shown.Text != null, $"{mat.Path} isn't given as text");
        Check(textToday == shown.Text!.Replace("\r\n", "\n").TrimEnd().Trim('\n'), $"{mat.Path}: the text differs from today's");
        Check(shownToday.Contains($"{shown.ShownBytes:N0} bytes", StringComparison.Ordinal), $"{mat.Path}: today's says another size than {shown.ShownBytes:N0} bytes");
        // The text printed under the output contract is the same lines
        string printed = shown.ToText(new ReportTextOptions { MaxChars = int.MaxValue });
        Check(printed.Contains(textToday.Split('\n')[0], StringComparison.Ordinal) && printed.Contains(textToday.Split('\n')[^1], StringComparison.Ordinal), "the printed text lost its first or last line");

        // A binary file is described, not printed
        var nif = report.List("darkstar.esm", "*.nif").Files[0];
        var binary = report.Show("darkstar.esm", nif.Path);
        Check(!binary.ShownIsText && binary.Text == null && binary.ShownBytes > 0, $"{nif.Path} was taken as text");

        ctx.WriteReport("archive-today.txt", log.ToString());
        return $"{string.Join(", ", parts)}: {N(archives)} summary rows and {N(files)} files the same as today's in the same order, each with the same size ({N(sized)} sizes; a texture has none in either) and the same loose-copy mark ({N(loose)} files have a loose copy in Data). " +
            $"A find across all of Data ({N(allArchives)} archives, {N(lookedAt)} files): {string.Join(", ", findParts)} files, {N(found)} in all, the same as today's. " +
            $"--show of {mat.Path}: the same {N(shown.ShownBytes)} bytes of text; a mesh is described as binary.";
    }

    /// <summary>Today's list and the new one: the same files in the same order with the same sizes and loose marks.</summary>
    static (int Files, int Sized, int Loose) SameFiles(string what, string today, ArchiveAnswer mine)
    {
        var rows = Ctx.Table(today, "Archive");
        Check(!today.Contains(" more; add --all", StringComparison.Ordinal), $"{what}: today's answer is cut");
        Check(rows.Count == mine.Files.Count, $"{what}: {rows.Count} files today, {mine.Files.Count} new");
        int sized = 0, loose = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            var f = mine.Files[i];
            Check(rows[i][0] == f.Archive && rows[i][1] == f.Path, $"{what}: file {i} is {rows[i][1]} ({rows[i][0]}) today, {f.Path} ({f.Archive}) new");
            string size = f.Size == null ? "" : (f.Size.Value / 1024.0).ToString("0.#", CultureInfo.InvariantCulture);
            Check((rows[i].Length > 2 ? rows[i][2] : "") == size, $"{what}: {f.Path} is {(rows[i].Length > 2 ? rows[i][2] : "")} KB today, {size} new");
            if (f.Size != null) sized++;
            bool looseToday = rows[i].Length > 3 && rows[i][3].StartsWith("YES", StringComparison.Ordinal);
            Check(looseToday == f.LooseCopy, $"{what}: {f.Path} has a loose copy {(looseToday ? "today" : "in the new one")} only");
            if (f.LooseCopy) loose++;
        }
        return (rows.Count, sized, loose);
    }

    public static string Extract(Ctx ctx)
    {
        var s = ctx.Session;
        var report = new ArchiveReport(s);

        // ---- where a file may go
        string target = ctx.Folder("extract-rule");
        var bad = new[] { @"..\x.txt", @"a\..\..\x.txt", @"..\..\..\Windows\x.txt", @"C:\Windows\x.txt", @"C:x.txt", @"\\server\share\x.txt", @"a\b:stream", "", @"..", @"a\.." };
        foreach (var p in bad) Check(ArchiveReport.SafeTarget(target, p) == null, $"\"{p}\" would be written, though it leads outside {target}");
        var good = new[] { @"a\b.txt", @"meshes\x\..\y.nif", "x.txt", @"a\...\b.txt" };
        foreach (var p in good)
        {
            string? to = ArchiveReport.SafeTarget(target, p);
            Check(to != null && to.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase), $"\"{p}\" stays inside the folder and is refused (or lands at {to})");
        }

        // ---- real files, held against Mutagen's own reading
        string outFolder = Path.Combine(ctx.Folder("extract"), "mats");
        ctx.MustBeInWork(outFolder);
        var answer = report.Extract("darkstar.esm", "*.mat", outFolder);
        var reader = Archive.CreateReader(GameRelease.Starfield, Path.Combine(ctx.Data, "darkstar - main.ba2"));
        var expected = reader.Files.Where(f => f.Path.ToString().EndsWith(".mat", StringComparison.OrdinalIgnoreCase)).ToList();
        Check(answer.Files.Count == expected.Count && answer.Refused.Count == 0, $"*.mat of darkstar.esm: {answer.Files.Count} extracted and {answer.Refused.Count} refused, the archive holds {expected.Count}");
        long bytes = 0;
        foreach (var f in expected)
        {
            string rel = f.Path.ToString().Replace('/', '\\').TrimStart('\\');
            string at = Path.Combine(outFolder, rel);
            Check(File.Exists(at), $"{rel} wasn't written");
            var want = f.GetBytes();
            Check(File.ReadAllBytes(at).AsSpan().SequenceEqual(want), $"{rel}: the file written differs from the archive's bytes");
            bytes += want.Length;
        }
        Check(answer.ExtractedBytes == bytes, $"the answer says {answer.ExtractedBytes} bytes, the files are {bytes}");
        Check(Directory.GetFiles(outFolder, "*", SearchOption.AllDirectories).Length == expected.Count, "more files were written than the archive's materials");
        // One whole path, without a pattern
        var one = report.Extract("darkstar.esm", answer.Files[0].Path, Path.Combine(ctx.Folder("extract"), "one"));
        Check(one.Files.Count == 1, $"one path extracts {one.Files.Count} files");
        // A part of a path is not a pattern
        var none = report.Extract("darkstar.esm", "materials", Path.Combine(ctx.Folder("extract"), "none"));
        Check(none.Files.Count == 0 && none.EmptyReason != null && !Directory.Exists(Path.Combine(ctx.Folder("extract"), "none", "materials")), "a part of a path extracted files, or the empty answer doesn't say why");
        // A texture in a format Mutagen had no DDS header for until fixes.16 (C202: R8G8B8A8_SNORM, one of 78 in the
        // game's texture archives; before, "Unsupported DDS header format"): the DX10 header naming its format, as
        // texconv writes it (TEXTURE2D, array size 1, the top mip's linear size), then the pixels
        const string snorm = @"textures\actors\human\faces\chargen\female_af_md1_normal.dds";
        string texOut = Path.Combine(ctx.Folder("extract"), "snorm");
        var tex = report.Extract("Starfield - Textures02.ba2", snorm, texOut);
        Check(tex.Files.Count == 1 && tex.Refused.Count == 0, $"{snorm}: {tex.Files.Count} extracted, {tex.Refused.Count} refused");
        var dds = File.ReadAllBytes(Path.Combine(texOut, snorm));
        Check(dds.Length == 148 + 1024 * 1024 * 4 && dds.AsSpan(0, 4).SequenceEqual("DDS "u8) && dds.AsSpan(84, 4).SequenceEqual("DX10"u8)
            && BitConverter.ToUInt32(dds, 128) == 31 && BitConverter.ToUInt32(dds, 132) == 3 && BitConverter.ToUInt32(dds, 140) == 1 && BitConverter.ToUInt32(dds, 20) == 1024 * 1024 * 4,
            $"{snorm}: {dds.Length} bytes, not a 1024x1024 R8G8B8A8_SNORM texture with a DX10 header");

        // ---- never into the game's folders
        int refusedFolders = 0;
        string game = Path.GetDirectoryName(ctx.Data.TrimEnd('\\'))!;
        foreach (var to in new[] { Path.Combine(ctx.Data, "m11b-extract-test"), ctx.Data, Path.Combine(ctx.Data, "Meshes", "..", "m11b-extract-test"), Path.Combine(game, "m11b-extract-test") })
        {
            try { report.Extract("darkstar.esm", "*.mat", to); Check(false, $"an extraction to {to} wasn't refused"); }
            catch (ReportException e) { Check(e.Line.Contains("nothing is ever written", StringComparison.Ordinal), $"the refusal of {to} doesn't say why: {e.Line}"); refusedFolders++; }
        }
        Check(!Directory.Exists(Path.Combine(ctx.Data, "m11b-extract-test")) && !Directory.Exists(Path.Combine(game, "m11b-extract-test")), "a folder was made in the game's folders");

        // ---- an archive whose paths lead outside the target folder: a copy of a real archive with three names changed
        string source = Path.Combine(ctx.Data, "apc_galaxysmartcontainers - main.ba2");
        string evilFolder = ctx.Folder("evil");
        string evil = Path.Combine(evilFolder, "evil - main.ba2");
        ctx.MustBeInWork(evil);
        File.Copy(source, evil, true);
        File.SetAttributes(evil, FileAttributes.Normal);
        var changed = ChangeNames(evil, new[] { @"..\", @"..\..\", @"C:\" });
        changed = changed.Select(ArchiveStore.Normalize).ToList();
        var evilFiles = Archive.CreateReader(GameRelease.Starfield, evil).Files.Select(f => ArchiveStore.Normalize(f.Path.ToString())).ToList();
        foreach (var c in changed) Check(evilFiles.Contains(c), $"the changed archive doesn't hold {c}");
        string evilOut = Path.Combine(evilFolder, "out", "deep", "inner");
        var refused = report.Extract(evil, "*", evilOut);
        Check(refused.Refused.Count == 3 && refused.Refused.Select(f => f.Path).OrderBy(p => p).SequenceEqual(changed.OrderBy(p => p)), $"{refused.Refused.Count} files refused ({string.Join(", ", refused.Refused.Select(f => f.Path))}), 3 expected ({string.Join(", ", changed)})");
        Check(refused.Files.Count == evilFiles.Count - 3, $"{refused.Files.Count} of the other {evilFiles.Count - 3} files extracted");
        // Nothing landed outside the target folder
        var outside = Directory.GetFiles(Path.Combine(evilFolder, "out"), "*", SearchOption.AllDirectories).Where(f => !f.StartsWith(evilOut + "\\", StringComparison.OrdinalIgnoreCase)).ToList();
        Check(outside.Count == 0, $"files were written outside the target folder: {string.Join(", ", outside)}");
        Check(Directory.GetFiles(evilOut, "*", SearchOption.AllDirectories).Length == refused.Files.Count, "the target folder doesn't hold exactly the extracted files");
        foreach (var c in changed.Where(c => c.StartsWith("C:", StringComparison.Ordinal))) Check(!File.Exists(c), $"{c} was written");
        string text = refused.ToText();
        Check(text.Contains("3 files refused", StringComparison.Ordinal) && text.Contains("REFUSED " + changed[0], StringComparison.Ordinal) && refused.Notes.Any(n => n.Contains("not extracted", StringComparison.Ordinal)), "the answer doesn't name the refused files");

        // Today's refuses them too (the 1.0.1 fix)
        string todayNote = "";
        if (ctx.CanRunToday)
        {
            string todayOut = Path.Combine(evilFolder, "today-out", "deep", "inner");
            ctx.MustBeInWork(todayOut);
            string? today = ctx.Today("archive__evil__extract", "Archive", evil, "--extract", "*", "--to", todayOut);
            if (today != null)
            {
                var m = Regex.Match(today, @"Extracted ([\d,]+) file\(s\).*? (\d+) file\(s\) refused");
                Check(m.Success && int.Parse(m.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture) == refused.Files.Count && m.Groups[2].Value == "3", $"today's says: {today.Trim()}");
                todayNote = $" Today's Archive --extract on the same archive: the same {N(refused.Files.Count)} extracted and 3 refused.";
            }
        }
        return $"*.mat of darkstar.esm: {N(expected.Count)} files, {N(bytes)} bytes, each equal to Mutagen's own reading of the archive; one whole path gives 1 file; a part of a path gives none and says why; an R8G8B8A8_SNORM texture extracted with its DX10 header (C202). " +
            $"{refusedFolders} targets inside the Data folder or the game's folder refused, nothing made there. " +
            $"A copy of apc_galaxysmartcontainers - main.ba2 with 3 of its {N(evilFiles.Count)} names changed to lead outside ({string.Join(", ", changed.Select(c => c[..Math.Min(c.Length, 24)] + "…"))}): 3 refused and named, {N(refused.Files.Count)} extracted, no file outside the target folder. " +
            $"{bad.Length} paths that lead outside are refused by the rule, {good.Length} that stay inside pass.{todayNote}";
    }

    /// <summary>
    /// Contents (the add-on API's GameData.Archives, GAPS S26): a plugin's own archives by the game's name rule and every
    /// file in each, held against Mutagen's archive reader read directly (what the Patcher add-on's Profile did) and against
    /// the list report; the loose copies against the Data folder; a plugin with no archives, one .ba2, a name that isn't there.
    /// </summary>
    public static string Contents(Ctx ctx)
    {
        var report = new ArchiveReport(ctx.Session);
        long files = 0; int archives = 0, sized = 0, loose = 0;
        var parts = new List<string>();
        foreach (var plugin in Plugins)
        {
            var c = report.Contents(plugin);
            string stem = Stem(plugin);
            var expectedNames = Directory.GetFiles(ctx.Data, "*.ba2").Select(f => Path.GetFileName(f)!)
                .Where(n => n.StartsWith(stem + " - ", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            Check(c.Archives.Select(a => a.Name).SequenceEqual(expectedNames, StringComparer.OrdinalIgnoreCase), $"{plugin}: archives {string.Join(", ", c.Archives.Select(a => a.Name))}, the Data folder has {string.Join(", ", expectedNames)}");
            Check(c.Plugin != null && c.Plugin.Equals(plugin, StringComparison.OrdinalIgnoreCase) && c.InData, $"{plugin}: the answer names {c.Plugin} (in Data: {c.InData})");
            foreach (var a in c.Archives)
            {
                Check(a.Error == null, $"{a.Name} couldn't be read: {a.Error}");
                var direct = Archive.CreateReader(GameRelease.Starfield, Path.Combine(ctx.Data, a.Name)).Files.ToList();
                Check(a.Files.Count == direct.Count, $"{a.Name}: {a.Files.Count} files, Mutagen's reader {direct.Count}");
                for (int i = 0; i < Math.Min(a.Files.Count, direct.Count); i++)
                {
                    var f = a.Files[i];
                    string want = direct[i].Path.ToString().Replace('/', '\\').TrimStart('\\');
                    Check(f.Path == want && f.Archive == a.Name, $"{a.Name}: file {i} is {f.Path}, Mutagen's reader {want}");
                    bool texture = want.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
                    Check(texture ? f.Size == null : f.Size == direct[i].Size, $"{a.Name}: {f.Path} is {f.Size} bytes, Mutagen's reader {direct[i].Size}");
                    Check(f.Extension == Path.GetExtension(want).ToLowerInvariant(), $"{f.Path}: extension {f.Extension}");
                    bool isLoose = want.Contains('\\') && File.Exists(Path.Combine(ctx.Data, want));
                    Check(f.LooseCopy == isLoose, $"{f.Path}: loose copy {f.LooseCopy}, the Data folder {(isLoose ? "has" : "hasn't")} it");
                    if (f.Size != null) sized++;
                    if (f.LooseCopy) loose++;
                }
                files += a.Files.Count; archives++;
            }
            // The same rows as the list report with no pattern
            var list = report.List(plugin);
            var flat = c.Archives.SelectMany(a => a.Files).ToList();
            Check(list.Files.Count == flat.Count && list.Files.Zip(flat).All(p => p.First.Path == p.Second.Path && p.First.Archive == p.Second.Archive && p.First.Size == p.Second.Size && p.First.LooseCopy == p.Second.LooseCopy),
                $"{plugin}: list gives {list.Files.Count} rows, Contents {flat.Count}, or they differ");
            parts.Add($"{plugin} {c.Archives.Count} archives {N(c.FileCount)} files");
        }
        // A plugin without archives: none, and a note that says why
        var bare = report.Contents("darkstarpatch_atom74.esm");
        Check(bare.Archives.Count == 0 && bare.Notes.Any(n => n.Contains("has no archives", StringComparison.Ordinal)), $"darkstarpatch_atom74.esm: {bare.Archives.Count} archives, notes: {string.Join(" / ", bare.Notes)}");
        // One archive by its name
        var one = report.Contents("atom74 - main.ba2");
        Check(one.Archives.Count == 1 && one.Plugin == null && one.Archives[0].Files.Count > 0, $"atom74 - main.ba2: {one.Archives.Count} archives");
        // A name that is neither: no archives, and the note says there is no such plugin
        var nothing = report.Contents("no_such_plugin_f5.esm");
        Check(nothing.Archives.Count == 0 && nothing.Notes.Any(n => n.Contains("no plugin named", StringComparison.Ordinal)), $"no_such_plugin_f5.esm: notes: {string.Join(" / ", nothing.Notes)}");
        return $"{string.Join("; ", parts)}: {archives} archives, {N(files)} files equal to Mutagen's reader ({N(sized)} with a size, {N(loose)} with a loose copy in Data) and to the list report; a plugin without archives, one .ba2 and a missing name answered";
    }

    /// <summary>
    /// The name-table read (G4, GAPS S16; since fixes.16 Mutagen's EnumerateFilePaths, C201, GAPS Part 2 row 32)
    /// gives the same paths, in the same order, as Mutagen's whole reader, for
    /// a mod's general and texture archives and some of the game's (animations, materials, meshes, textures).
    /// Through a fresh store every path is found by its hash. A file that isn't a BA2, and one whose table is cut
    /// short, fall back to the whole reader and say what it says.
    /// </summary>
    public static string NameTable(Ctx ctx)
    {
        string[] names =
        {
            "atom74 - main.ba2", "atom74 - textures.ba2", "darkstar - main.ba2", "darkstar - textures.ba2",
            "Starfield - Animations.ba2", "Starfield - Materials.ba2", "Starfield - Meshes01.ba2", "Starfield - Textures01.ba2",
        };
        var store = new ArchiveStore(ctx.Data);
        long files = 0; double fast = 0, whole = 0;
        var parts = new List<string>();
        foreach (var name in names)
        {
            string path = Path.Combine(ctx.Data, name);
            Check(File.Exists(path), $"{name} isn't in Data");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var table = ArchiveStore.ReadNameTable(path);
            double tFast = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            var direct = Archive.CreateReader(GameRelease.Starfield, path).Files.Select(f => ArchiveStore.Normalize(f.Path.ToString())).ToList();
            double tWhole = clock.Elapsed.TotalMilliseconds;
            Check(table != null, $"{name}: the name table couldn't be read alone");
            Check(table!.Count == direct.Count, $"{name}: the name table has {table.Count} paths, Mutagen's reader {direct.Count}");
            for (int i = 0; i < direct.Count; i++)
                Check(table[i] == direct[i], $"{name}: path {i} is {table[i]} in the name table, {direct[i]} in Mutagen's reader");
            var paths = store.PathsOf(path);
            Check(paths.Error == null && paths.Paths.SequenceEqual(direct), $"{name}: the store's paths differ from Mutagen's reader ({paths.Error})");
            var hashes = store.Names(path);
            Check(hashes.Count == direct.Count && direct.All(hashes.Has), $"{name}: the store's hashes miss a path");
            // The read-ahead's hash of a path as stored (no text made) is the hash of its normalized text (GAPS S30)
            foreach (var stored in Archive.CreateReader(GameRelease.Starfield, path).EnumerateFilePaths())
                Check(ArchiveStore.HashOfStored(stored) == ArchiveStore.Hash(ArchiveStore.Normalize(stored)), $"{name}: the read-ahead hashes {stored} differently");
            files += direct.Count; fast += tFast; whole += tWhole;
            parts.Add($"{name} {N(direct.Count)}");
        }
        // Not a BA2, and a BA2 whose name table is cut short: the whole reader's answer
        string folder = ctx.Folder("name-table");
        string junk = Path.Combine(folder, "junk - main.ba2");
        File.WriteAllBytes(junk, Encoding.ASCII.GetBytes("this is not an archive, only some text that is long enough"));
        Check(ArchiveStore.ReadNameTable(junk) == null, "junk - main.ba2: the name table reader read a file that isn't a BA2");
        var junkPaths = store.PathsOf(junk);
        var junkWhole = new ArchiveStore(ctx.Data).Open(junk);
        Check(junkPaths.Paths.Count == 0 && junkPaths.Error != null && junkPaths.Error == junkWhole.Error, $"junk - main.ba2: error \"{junkPaths.Error}\", the whole reader's \"{junkWhole.Error}\"");
        string cut = Path.Combine(folder, "cut - main.ba2");
        var bytes = File.ReadAllBytes(Path.Combine(ctx.Data, "atom74 - main.ba2"));
        File.WriteAllBytes(cut, bytes.AsSpan(0, bytes.Length - 100).ToArray());
        Check(ArchiveStore.ReadNameTable(cut) == null, "cut - main.ba2: the name table reader read a table that is cut short");
        return $"{names.Length} archives ({string.Join(", ", parts)}): the same {N(files)} paths in the same order as Mutagen's reader, each found by its hash; " +
            $"the name tables read in {fast:F0} ms, the whole reader {whole:F0} ms; a file that isn't a BA2 and a cut table fall back to the whole reader (\"{junkPaths.Error}\")";
    }

    /// <summary>Changes the start of the first names in an archive's name table (same length), in a copy in the work
    /// folder. Returns the new names.</summary>
    static List<string> ChangeNames(string archive, string[] prefixes)
    {
        var bytes = File.ReadAllBytes(archive);
        Check(Encoding.ASCII.GetString(bytes, 0, 4) == "BTDX", "the archive isn't a BA2");
        long at = (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16));
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        var changed = new List<string>();
        for (int i = 0, done = 0; i < count && done < prefixes.Length; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)at));
            string name = Encoding.UTF8.GetString(bytes, (int)at + 2, len);
            string prefix = prefixes[done];
            if (len > prefix.Length + 4)
            {
                string renamed = prefix + name[prefix.Length..];
                Encoding.UTF8.GetBytes(renamed).CopyTo(bytes, (int)at + 2);
                changed.Add(renamed);
                done++;
            }
            at += 2 + len;
        }
        Check(changed.Count == prefixes.Length, "the archive has too few names to change");
        File.WriteAllBytes(archive, bytes);
        return changed;
    }
}
