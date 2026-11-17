// archive2 grep (J18, GAPS S55): the contents of archived and loose files searched for a text.
//  - A made Data folder: two BA2 archives written here (a general one with known texts, compressed and stored, and
//    one of textures only) and a loose file. Every hit at its known offset and line, none twice, none lost: across the
//    4 MB pieces the search reads, in a compressed .swf, in a loose copy; textures and sounds passed over unless named;
//    the scope by folder and kind; a regular expression; pages that stop early and go on by the cursor.
//  - The game's archives: SFBGS007's material database names Data\Materials\Test\Test\MGoulet_Pattern05_Test.mat
//    (the finding of the J8 probe, notes\fix-j8-11-materials.md).
//  - The estimate made before reading against the real read: the same files and bytes, and the time.
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class ArchiveGrepTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    const string Needle = "Needle_Path\\Made.mat";

    public static string Grep(Ctx ctx)
    {
        var parts = new List<string>();
        parts.Add(Made(ctx));
        parts.Add(Anchors(ctx));
        parts.Add(Wide(ctx));
        parts.Add(CaseOutsideAscii(ctx));
        parts.Add(ArchiveEncodingTests.Escapes(ctx));
        parts.Add(ArchiveEncodingTests.BytesRoute(ctx));
        parts.Add(ArchiveEncodingTests.Encodings(ctx));
        parts.Add(Game(ctx));
        parts.Add(ArchiveEncodingTests.Game(ctx));
        return string.Join("; ", parts);
    }

    /// <summary>A BA2 archive of general files as Starfield writes them (version 2: zlib), from Mutagen's reader's layout:
    /// a 32-byte header, a 36-byte record per file, the files' data, the name table.</summary>
    internal static void WriteBa2(string path, IReadOnlyList<(string Path, byte[] Data, bool Compress)> files)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("BTDX"u8); w.Write(2u); w.Write("GNRL"u8); w.Write((uint)files.Count); w.Write(0UL); w.Write(1u); w.Write(0u);
        long records = ms.Position;
        w.Write(new byte[36 * files.Count]);
        var placed = new List<(long Offset, uint Packed, uint Size)>();
        foreach (var f in files)
        {
            long at = ms.Position;
            byte[] stored = f.Data;
            if (f.Compress)
            {
                using var z = new MemoryStream();
                using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(f.Data);
                stored = z.ToArray();
            }
            w.Write(stored);
            placed.Add((at, f.Compress ? (uint)stored.Length : 0u, (uint)f.Data.Length));
        }
        long names = ms.Position;
        foreach (var f in files) { var b = Encoding.UTF8.GetBytes(f.Path); w.Write((ushort)b.Length); w.Write(b); }
        ms.Position = 16; w.Write((ulong)names);
        ms.Position = records;
        for (int i = 0; i < files.Count; i++)
        {
            string ext = Path.GetExtension(files[i].Path).TrimStart('.');
            var e = new byte[4]; Encoding.ASCII.GetBytes(ext.Length > 4 ? ext[..4] : ext).CopyTo(e, 0);
            w.Write((uint)i); w.Write(e); w.Write(0u); w.Write(0x00100100u); w.Write((ulong)placed[i].Offset); w.Write(placed[i].Packed); w.Write(placed[i].Size); w.Write(0xBAADF00Du);
        }
        w.Flush();
        File.WriteAllBytes(path, ms.ToArray());
    }

    static byte[] Ascii(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>Bytes with the needle at the given offsets, the rest a binary filler that holds no letters of it.</summary>
    static byte[] Binary(int length, params int[] at)
    {
        var b = new byte[length];
        for (int i = 0; i < length; i++) b[i] = (byte)(i % 7 == 0 ? 0 : 0x80 + i % 13);
        foreach (int a in at) Ascii(Needle).CopyTo(b, a);
        return b;
    }

    static GrepAnswer Run(ArchiveReport report, string target, GrepOptions o, int maxChars = int.MaxValue / 2)
    {
        using var plan = report.PlanGrep(target, o);
        return report.Grep(plan, maxChars, detail: true);
    }

    static string Made(Ctx ctx)
    {
        string data = ctx.Folder("grep-data");
        // The .mat: lines 3 and 5 hold the needle, as JSON writes a path (\\) and with / in capitals
        string matFirst = Needle.Replace("\\", "\\\\"), matSecond = Needle.Replace('\\', '/').ToUpperInvariant();
        string mat = "{\n  \"Objects\": [\n    \"" + matFirst + "\",\n    \"other\",\n    \"" + matSecond + "\"\n  ]\n}\n";
        int piece = 4 << 20;
        // A file of 9 MB: the needle across the first cut of the 4 MB pieces, right after it, and at the very end
        int big = 9 << 20;
        int[] bigAt = { piece - 5, piece + 100, big - Needle.Length };
        // A compressed Flash file: "CWS", a version, the uncompressed length, then zlib
        var swfBody = Binary(5000, 1234);
        byte[] swf;
        using (var z = new MemoryStream())
        {
            z.Write("CWS"u8); z.WriteByte(10);
            var len = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)(swfBody.Length + 8)); z.Write(len);
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(swfBody);
            swf = z.ToArray();
        }
        // A compiled script that names it 1,500 times: every hit counted, the first 1,000 kept
        var many = new StringBuilder();
        for (int i = 0; i < 1500; i++) many.Append("x").Append(Needle).Append('\0');
        var files = new List<(string Path, byte[] Data, bool Compress)>
        {
            (@"materials\made\one.mat", Ascii(mat), true),
            (@"meshes\made\two.nif", Binary(3000, 17, 2000), false),
            (@"meshes\made\big.mesh", Binary(big, bigAt), true),
            (@"textures\made\three.dds", Binary(500, 10), false),
            (@"sound\made\four.wem", Binary(500, 20), true),
            (@"interface\made\five.swf", swf, false),
            (@"scripts\made\six.pex", Ascii(many.ToString()), true),
            (@"meshes\made\none.nif", Binary(4000), true),
        };
        WriteBa2(Path.Combine(data, "made - main.ba2"), files);
        WriteBa2(Path.Combine(data, "made - textures.ba2"), new[] { (@"textures\made\seven.dds", Binary(800, 30), true) });
        // A loose copy of the .mat (the game reads it in place of the archived one), and a loose file of nothing archived
        Directory.CreateDirectory(Path.Combine(data, @"materials\made"));
        File.WriteAllText(Path.Combine(data, @"materials\made\one.mat"), "loose copy\nline two " + Needle + "\n");
        File.WriteAllBytes(Path.Combine(data, @"materials\made\eight.mat"), Ascii("eight " + Needle));
        // Files at Data's own top level (J47): left out by "all", and said so; the archives themselves are not counted
        File.WriteAllBytes(Path.Combine(data, "readme.txt"), Ascii("read me " + Needle));
        File.WriteAllBytes(Path.Combine(data, "made - main.ba2.vortex_backup"), Ascii("a backup " + Needle));

        using var session = new ReportSession(data, ctx.Folder("grep-scratch"), ctx.Schema);
        var report = new ArchiveReport(session);

        // ---- the plugin's archives: its own two, and the loose copy of one of their files
        var a = Run(report, "made.esm", new GrepOptions { Text = Needle.ToLowerInvariant() });
        string Hits(GrepAnswer x) => string.Join(", ", x.Files.Select(f => $"{f.Where}:{f.Path}x{f.Count}"));
        Check(a.Files.Count == 6, $"made.esm: hits in {a.Files.Count} files, 6 expected: {Hits(a)}");
        var loose = a.Files.Single(f => f.Where == "loose");
        Check(loose.Path == @"materials\made\one.mat" && loose.Count == 1 && loose.Hits[0].Offset == 20 && loose.Hits[0].Line == 2, $"the loose copy: {loose.Path} {loose.Count} hits at {loose.Hits[0].Offset} line {loose.Hits[0].Line}");
        var one = a.Files.Single(f => f.Where == "made - main.ba2" && f.Path.EndsWith("one.mat"));
        int first = mat.IndexOf(matFirst, StringComparison.Ordinal), second = mat.IndexOf(matSecond, StringComparison.Ordinal);
        Check(one.LooseCopy && one.Count == 2 && one.Hits[0].Offset == first && one.Hits[0].Line == 3 && one.Hits[1].Offset == second && one.Hits[1].Line == 5,
            $"the archived .mat: {one.Count} hits at {string.Join(", ", one.Hits.Select(h => $"{h.Offset} line {h.Line}"))}, expected {first} line 3 and {second} line 5, marked as having a loose copy");
        Check(one.Hits[0].Around.Contains("«" + matFirst + "»", StringComparison.Ordinal) && one.Hits[0].Match == matFirst && one.Hits[1].Match == matSecond, $"a path as JSON writes it and with /: the text around {one.Hits[0].Around}; the matches {one.Hits[0].Match}, {one.Hits[1].Match}");
        var two = a.Files.Single(f => f.Path.EndsWith("two.nif"));
        Check(two.Hits.Select(h => h.Offset).SequenceEqual(new long[] { 17, 2000 }) && two.Hits.All(h => h.Line == null), $"the stored mesh: offsets {string.Join(", ", two.Hits.Select(h => h.Offset))}");
        var bigHits = a.Files.Single(f => f.Path.EndsWith("big.mesh"));
        Check(bigHits.Hits.Select(h => h.Offset).SequenceEqual(bigAt.Select(x => (long)x)), $"the 9 MB file: offsets {string.Join(", ", bigHits.Hits.Select(h => h.Offset))}, expected {string.Join(", ", bigAt)} (across the cut of the pieces, and at the end)");
        var five = a.Files.Single(f => f.Path.EndsWith("five.swf"));
        Check(five.Count == 1 && five.Hits[0].Offset == 8 + 1234 && five.Hits[0].Inflated, $"the compressed .swf: {five.Count} hits at {five.Hits.FirstOrDefault()?.Offset}, expected one at {8 + 1234}, inflated");
        var six = a.Files.Single(f => f.Path.EndsWith("six.pex"));
        Check(six.Count == 1500 && six.Hits.Count == ArchiveGrep.KeptHits, $"the script: {six.Count} hits counted, {six.Hits.Count} kept");
        Check(!a.Files.Any(f => f.Path.EndsWith(".dds") || f.Path.EndsWith(".wem")), "a texture or a sound was searched without being named");
        Check(a.Notes.Any(n => n.StartsWith("Passed over as textures, sounds and video: 3 files", StringComparison.Ordinal)), "the note on the files passed over: " + string.Join(" | ", a.Notes));
        Check(a.Notes.Any(n => n.Contains("1 loose file in Data with the path of one of these archives", StringComparison.Ordinal)), "the note on the loose copy: " + string.Join(" | ", a.Notes));
        Check(a.NextItem == null && a.FirstItem == 0 && a.ItemsSearched == a.ScopeFiles && a.ScopeFiles == 7, $"one page: {a.ItemsSearched} of {a.ScopeFiles} files searched, next {a.NextItem}");
        // The estimate: the same files and bytes as were read (the .swf is read inflated, so its bytes differ)
        long swfDiff = swfBody.Length + 8 - swf.Length;
        Check(a.EstimateFiles == a.ItemsSearched && a.EstimateBytes + swfDiff == a.BytesSearched, $"estimated {a.EstimateFiles} files and {a.EstimateBytes} bytes, read {a.ItemsSearched} and {a.BytesSearched} (the .swf inflated: {swfDiff} more)");

        // ---- S68: the read-ahead reads the stretches that hold the scope's files, from Mutagen's GetStorage() (fixes.18,
        // C42), not the whole archive; the estimate takes each file's own stored bytes and compression
        using (var plan = report.PlanGrep("made.esm", new GrepOptions { Text = Needle, Extensions = new[] { "mat" } }))
        {
            var main = plan.Sources.Single(s => s.Name == "made - main.ba2");
            var matItem = plan.Items.Single(i => i.Source >= 0 && i.Path.EndsWith("one.mat", StringComparison.Ordinal));
            var st = matItem.File!.GetStorage()!;
            Check(main.ReadAhead && main.Stretches is { Count: 1 } && main.Stretches[0] == (st.Offset, st.StoredSize) && main.AheadBytes == st.StoredSize && main.AheadBytes < main.Length / 100
                && matItem.Compressed && matItem.Stored == st.StoredSize && st.StoredSize < matItem.Size,
                $"--ext mat: read ahead {main.AheadBytes} bytes in {main.Stretches?.Count} stretches of the {main.Length}-byte archive; the .mat stored {st.StoredSize} at {st.Offset}, compressed {matItem.Compressed}");
            Check(plan.ReadAheadBytes == st.StoredSize, $"--ext mat: the plan reads ahead {plan.ReadAheadBytes} bytes, the .mat's stored bytes are {st.StoredSize}");
        }
        using (var plan = report.PlanGrep("made.esm", new GrepOptions { Text = Needle, Extensions = new[] { "mat", "pex", "nif" } }))
        {
            // The files lie close together (big.mesh compresses to little): one stretch from the .mat's chunk to the last
            // file's. A stored file is only copied, a compressed one inflated
            var main = plan.Sources.Single(s => s.Name == "made - main.ba2");
            var mine = plan.Items.Where(i => i.Source == plan.Sources.IndexOf(main)).ToList();
            var chunks = mine.SelectMany(i => i.File!.GetStorage()!.Chunks).ToList();
            long from = chunks.Min(c => c.Offset), to = chunks.Max(c => c.Offset + c.StoredSize);
            Check(main.Stretches is { Count: 1 } && main.Stretches[0] == (from, to - from), $"--ext mat,pex,nif: {main.Stretches?.Count} stretches ({string.Join(", ", main.Stretches?.Select(x => $"{x.Offset}+{x.Length}") ?? Array.Empty<string>())}), the files' chunks from {from} to {to}");
            var nif = mine.Single(i => i.Path.EndsWith("two.nif", StringComparison.Ordinal));
            var pex = mine.Single(i => i.Path.EndsWith("six.pex", StringComparison.Ordinal));
            Check(!nif.Compressed && nif.Stored == nif.Size && pex.Compressed && pex.Stored < pex.Size, $"two.nif compressed {nif.Compressed} stored {nif.Stored}; six.pex compressed {pex.Compressed} stored {pex.Stored} of {pex.Size}");
        }
        // Neighbours less than a MB apart (or overlapping) are read as one stretch; farther ones each on its own
        var merged = ArchiveGrep.Merge(new() { (5000, 100), (0, 10), (5, 10), (5100 + ArchiveGrep.MergeGap, 7), (20, 0) });
        Check(merged.SequenceEqual(new (long, long)[] { (0, 5100), (5100 + ArchiveGrep.MergeGap, 7) }), "merged stretches: " + string.Join(", ", merged.Select(x => $"{x.Offset}+{x.Length}")));

        // ---- textures and sounds when named; every kind with *; a folder
        var dds = Run(report, "made.esm", new GrepOptions { Text = Needle, Extensions = new[] { "dds", ".wem" } });
        Check(dds.Files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).SequenceEqual(new[] { @"sound\made\four.wem", @"textures\made\seven.dds", @"textures\made\three.dds" }), $"--ext dds,.wem: {Hits(dds)}");
        var every = Run(report, "made.esm", new GrepOptions { Text = Needle, Extensions = new[] { "*" } });
        Check(every.Files.Count == 9 && !every.Notes.Any(n => n.StartsWith("Passed over", StringComparison.Ordinal)), $"--ext *: {Hits(every)}");
        var folder = Run(report, "made.esm", new GrepOptions { Text = Needle, Folders = new[] { "Meshes/Made" } });
        Check(folder.Files.Count == 2 && folder.Files.All(f => f.Path.StartsWith(@"meshes\made\", StringComparison.Ordinal)) && folder.ScopeFiles == 3, $"--folder meshes/made: {Hits(folder)} in {folder.ScopeFiles} files");
        // ---- a regular expression: the letters after "needle_" and the folder
        var rx = Run(report, "made.esm", new GrepOptions { Text = @"needle_[a-z]+\\m[a-z]+\.mat", Regex = true, Extensions = new[] { "mat" } });
        Check(rx.Files.Sum(f => f.Count) == 1 && rx.Files.All(f => f.Hits.All(h => h.Match.Equals(Needle, StringComparison.OrdinalIgnoreCase))), $"the regex (one backslash only): {Hits(rx)}");
        bool refused = false;
        try { Run(report, "made.esm", new GrepOptions { Text = "(abc", Regex = true }); } catch (ReportException e) { refused = e.Message.Contains("isn't a regular expression", StringComparison.Ordinal); }
        Check(refused, "a regular expression .NET can't read wasn't refused");
        // ---- every archive in Data: the textures archive is passed over by its names; every loose file is searched
        var all = Run(report, "all", new GrepOptions { Text = Needle });
        using (var plan = report.PlanGrep("all", new GrepOptions { Text = Needle })) Check(plan.ArchivesPassedOver == 1 && plan.LooseFiles == 2, $"all: {plan.ArchivesPassedOver} archives passed over by their names, {plan.LooseFiles} loose files");
        Check(all.Files.Count == 7 && all.Files.Any(f => f.Path.EndsWith("eight.mat")), $"all: {Hits(all)}");
        Check(!all.Files.Any(f => f.Path.Contains("readme") || f.Path.Contains("vortex")) && all.Notes.Any(n => n.StartsWith("2 files at Data's own top level were left out (.txt 1, .vortex_backup 1)", StringComparison.Ordinal)),
            "all: the 2 files at Data's top level are left out and said: " + string.Join(" | ", all.Notes));
        Check(!a.Notes.Any(n => n.Contains("top level", StringComparison.Ordinal)), "a plugin's archives: no note on Data's top level");

        // ---- --count (J45): the same hits and files as the list, per kind and per archive, over the whole scope
        GrepCountAnswer Count(string target, GrepOptions o)
        {
            using var plan = report.PlanGrep(target, o);
            return report.GrepCount(plan);
        }
        var c = Count("made.esm", new GrepOptions { Text = Needle, Count = true, Start = 3 });
        Check(c.All.Hits == a.Hits && c.All.FilesWithHits == a.Files.Count && c.All.Files == a.ScopeFiles && c.All.Bytes == a.BytesSearched,
            $"count: {c.All.Hits} hits in {c.All.FilesWithHits} of {c.All.Files} files ({c.All.Bytes} bytes); the list {a.Hits} in {a.Files.Count} of {a.ScopeFiles} ({a.BytesSearched}); a count ignores a start");
        var byKind = a.Files.GroupBy(f => Path.GetExtension(f.Path)).ToDictionary(g => g.Key, g => (Files: g.Count(), Hits: g.Sum(f => f.Count)));
        Check(byKind.All(k => c.ByKind[k.Key].FilesWithHits == k.Value.Files && c.ByKind[k.Key].Hits == k.Value.Hits) && c.ByKind[".mat"].Files == 2 && c.ByKind[".pex"].Hits == 1500 && c.ByKind[".nif"].Files == 2 && c.ByKind[".nif"].FilesWithHits == 1,
            "count per kind: " + string.Join(", ", c.ByKind.Select(k => $"{k.Key} {k.Value.FilesWithHits}/{k.Value.Files} {k.Value.Hits}")));
        Check(c.ByKindWhere[".mat"]["loose"].Hits == 1 && c.ByKindWhere[".mat"]["made - main.ba2"].Hits == 2, "count per archive: .mat " + string.Join(", ", c.ByKindWhere[".mat"].Select(w => $"{w.Key} {w.Value.Hits}")));
        string ct = c.ToText(new ReportTextOptions { MaxChars = int.MaxValue }), cd = c.ToText(new ReportTextOptions { MaxChars = int.MaxValue, Detail = true });
        Check(ct.Contains("1,509 hits in 6 of 7 files searched, 5 kinds of file.", StringComparison.Ordinal) && ct.Contains("| .pex | 1 | 1 | 1,500 |", StringComparison.Ordinal) && ct.Contains("| .nif | 2 | 1 | 2 |", StringComparison.Ordinal),
            "count text: " + ct);
        Check(cd.Contains("| .mat | (every archive) | 2 | 2 | 3 |\n| .mat | made - main.ba2 | 1 | 1 | 2 |\n| .mat | loose | 1 | 1 | 1 |", StringComparison.Ordinal), "count --detail text: " + cd);
        string cp = c.ToText(new ReportTextOptions { MaxChars = 30 }, out int cnext);
        Check(cnext == 1 && cp.Contains("[more] 1 of 5 kinds shown", StringComparison.Ordinal), "count pages by its rows: " + cp);
        ctx.WriteReport("archive-grep-count.txt", cd);

        // ---- pages: a small page stops early; the cursors go on; together they are the one-page answer, none twice
        var pages = new List<GrepFile>(); int start = 0, count = 0;
        while (true)
        {
            using var plan = report.PlanGrep("made.esm", new GrepOptions { Text = Needle, Start = start });
            var p = report.Grep(plan, 300, detail: false);
            count++;
            Check(p.FirstItem == start && p.ItemsSearched > 0, $"page {count} starts at {p.FirstItem}, searched {p.ItemsSearched}");
            pages.AddRange(p.Files);
            if (p.NextItem == null) break;
            Check(p.NextItem == p.FirstItem + p.ItemsSearched && p.Files.Count > 0, $"page {count}: next {p.NextItem}, searched {p.FirstItem}+{p.ItemsSearched}");
            start = (int)p.NextItem;
            Check(count < 20, "the pages don't end");
        }
        Check(count >= 3 && pages.Select(f => f.Where + f.Path).SequenceEqual(a.Files.Select(f => f.Where + f.Path)) && pages.Sum(f => f.Count) == a.Hits,
            $"{count} pages hold {pages.Count} files and {pages.Sum(f => f.Count)} hits, one page {a.Files.Count} and {a.Hits}");
        // The text: the rows of one file's hits, the estimate, the cut said
        string text = a.ToText(new ReportTextOptions { MaxChars = int.MaxValue });
        Check(text.Contains("Estimated before reading: 7 files", StringComparison.Ordinal) && text.Contains("| loose | materials\\made\\one.mat | 20 (line 2) |", StringComparison.Ordinal)
            && text.Contains("1,497 more hits in this file", StringComparison.Ordinal), "the text: " + text[..Math.Min(text.Length, 1500)]);
        ctx.WriteReport("archive-grep-made.txt", text);
        return $"made Data (2 archives, 2 loose files): {N(a.Hits)} hits in {a.Files.Count} files at their offsets and lines (across the 4 MB cut, in a compressed .swf, a loose copy), textures and sounds only when named, the scope by folder and kind, a regex, {count} pages = one page; estimate {N(a.EstimateFiles)} files, {N(a.EstimateBytes)} bytes = read";
    }

    /// <summary>
    /// J46: anchors and look-behinds across the 4 MB pieces, held against the same regular expression over the whole
    /// file at once. Three 9 MB binary files (.mesh: not read as UTF-16 though they start with FF FE), each with the
    /// same features in every 4 KB block at different places (four files), so that the cuts of the pieces (8 bytes past a multiple of
    /// 4 KB, as the 8-byte head read first shifts them) fall on a line-start "marker", inside a run of q, on a false
    /// byte-order mark, and right after an "x".
    /// </summary>
    static string Anchors(Ctx ctx)
    {
        string data = ctx.Folder("grep-anchors");
        int size = (9 << 20) + 1234;
        byte[] Layout(int lineStart, int midLine, int run, int bom, int afterX)
        {
            var b = new byte[size];
            for (int i = 0; i < size; i++) b[i] = (byte)(i % 64 == 50 ? '\n' : 'z');
            void Put(int at, string s) { for (int k = 0; k < s.Length && at + k < size; k++) if (at + k >= 0) b[at + k] = (byte)s[k]; }
            for (int block = 0; block < size; block += 4096)
            {
                Put(block + lineStart - 1, "\nmarker");
                Put(block + midLine - 1, "xmarker");
                Put(block + afterX - 1, "xmarker");
                for (int k = 0; k < 300; k++) Put(block + run - 150 + k, "q");
                if (block > 0) Put(block + bom, "ÿþ");
            }
            b[0] = 0xFF; b[1] = 0xFE;
            return b;
        }
        var files = new List<(string Path, byte[] Data, bool Compress)>
        {
            (@"meshes\anchors\one.mesh", Layout(8, 100, 2000, 3000, 3500), true),
            (@"meshes\anchors\two.mesh", Layout(1000, 2000, 8, 3000, 3500), true),
            (@"meshes\anchors\three.mesh", Layout(1000, 2000, 3000, 8, 3500), false),
            (@"meshes\anchors\four.mesh", Layout(1000, 2000, 3000, 2500, 8), true),
        };
        WriteBa2(Path.Combine(data, "anchors - main.ba2"), files);
        using var session = new ReportSession(data, ctx.Folder("grep-anchors-scratch"), ctx.Schema);
        var report = new ArchiveReport(session);
        var patterns = new[] { @"\A\xFF\xFE", @"\xFF\xFE", "^marker", @"\bmarker", "(?<=x)marker", "q+", "marker$|^z{5}" };
        var said = new List<string>();
        foreach (var pattern in patterns)
        {
            var got = Run(report, "anchors.esm", new GrepOptions { Text = pattern, Regex = true });
            var whole = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Multiline);
            long total = 0;
            foreach (var f in files)
            {
                var expected = whole.Matches(Encoding.Latin1.GetString(f.Data)).Where(m => m.Length > 0).Select(m => (long)m.Index).ToList();
                var file = got.Files.SingleOrDefault(x => x.Path == f.Path);
                var offsets = file?.Hits.Select(h => h.Offset).ToList() ?? new List<long>();
                Check(file == null ? expected.Count == 0 : file.Count == expected.Count && offsets.SequenceEqual(expected.Take(KeptHitsOf(file))),
                    $"{pattern} in {f.Path}: {file?.Count ?? 0} hits, the whole file {expected.Count}; first differences: {string.Join(", ", offsets.Except(expected).Take(5))} / {string.Join(", ", expected.Except(offsets).Take(5))}");
                total += expected.Count;
            }
            said.Add($"{pattern} {N(total)}");
        }
        var start = Run(report, "anchors.esm", new GrepOptions { Text = @"\A\xFF\xFE", Regex = true });
        Check(start.Hits == files.Count && start.Files.All(f => f.Hits.Single().Offset == 0 && !f.Hits[0].Utf16), "\\A\\xFF\\xFE: one hit at 0 in each file, " + start.Hits);
        // A plain text: every place, none twice, none lost
        var plain = Run(report, "anchors.esm", new GrepOptions { Text = "xmarker" });
        long plainExpected = files.Sum(f => (long)System.Text.RegularExpressions.Regex.Matches(Encoding.Latin1.GetString(f.Data), "xmarker").Count);
        Check(plain.Hits == plainExpected, $"plain xmarker: {plain.Hits}, expected {plainExpected}");
        return $"anchors across the 4 MB pieces = the whole file's matches ({string.Join(", ", said)}); \\A only at the file's first byte";
    }

    static int KeptHitsOf(GrepFile f) => Math.Min((int)f.Count, ArchiveGrep.KeptHits);

    /// <summary>
    /// J39: a text file that starts with a UTF-16 byte-order mark is searched as UTF-16 text (LE and BE); its offsets
    /// are bytes of the file, its lines right; across the 4 MB pieces too. A binary .lod that starts with FE FF (an
    /// int32, as the game's 548 .lod do) is not read as UTF-16.
    /// </summary>
    static string Wide(Ctx ctx)
    {
        string data = ctx.Folder("grep-wide");
        const string table = "$ABORT JUMP\tAbort jump\r\n$Aim Horizontal Sensitivity\tAim Horizontal Sensitivity\r\n$Size\tBildschirmgröße\r\n";
        byte[] Le(string s) => new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(s)).ToArray();
        byte[] Be(string s) => new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(s)).ToArray();
        // A table of 2.5 million characters (5 MB): the text right at the first cut of the pieces and at the end
        var big = new StringBuilder();
        while (big.Length < 2_500_000) big.Append("$FILLER_").Append(big.Length).Append("\tfiller text\r\n");
        int cutChar = ((4 << 20) + 8 - (64 << 10)) / 2 - 2; // the first cut, in characters after the mark
        big.Insert(cutChar - 10, "Aim Horizontal Sensitivity").Append("Aim Horizontal Sensitivity");
        string bigText = big.ToString();
        var lod = new byte[4000];
        new byte[] { 0xFE, 0xFF, 0xFF, 0xFF }.CopyTo(lod, 0);
        Encoding.Unicode.GetBytes("Aim Horizontal Sensitivity").CopyTo(lod, 100);
        Encoding.ASCII.GetBytes("Aim Horizontal Sensitivity").CopyTo(lod, 1000);
        var files = new List<(string Path, byte[] Data, bool Compress)>
        {
            (@"interface\translate_en.txt", Le(table), true),
            (@"interface\translate_be.txt", Be(table), false),
            (@"interface\translate_big.txt", Le(bigText), true),
            (@"interface\eight.txt", Encoding.UTF8.GetBytes(table), true),
            (@"meshes\wide\x.lod", lod, true),
        };
        WriteBa2(Path.Combine(data, "wide - interface.ba2"), files);
        using var session = new ReportSession(data, ctx.Folder("grep-wide-scratch"), ctx.Schema);
        var report = new ArchiveReport(session);
        var a = Run(report, "wide.esm", new GrepOptions { Text = "aim horizontal sensitivity" });
        GrepFile F(GrepAnswer x, string name) => x.Files.Single(f => f.Path.EndsWith(name, StringComparison.Ordinal));
        long Off(string text, string what, int nth = 0) { int i = -1; for (int k = 0; k <= nth; k++) i = text.IndexOf(what, i + 1, StringComparison.Ordinal); return 2 + 2L * i; }
        var en = F(a, "translate_en.txt");
        Check(en.Count == 2 && en.Hits.All(h => h.Utf16) && en.Hits[0].Offset == Off(table, "Aim Horizontal") && en.Hits[1].Offset == Off(table, "Aim Horizontal", 1) && en.Hits.All(h => h.Line == 2) && en.Hits[1].Around.Contains("Sensitivity «Aim Horizontal Sensitivity»", StringComparison.Ordinal),
            $"UTF-16 LE: {en.Count} hits at {string.Join(", ", en.Hits.Select(h => $"{h.Offset} line {h.Line} {h.Around}"))}");
        var be = F(a, "translate_be.txt");
        Check(be.Count == 2 && be.Hits.Select(h => h.Offset).SequenceEqual(en.Hits.Select(h => h.Offset)) && be.Hits[0].Match == "Aim Horizontal Sensitivity", $"UTF-16 BE: {string.Join(", ", be.Hits.Select(h => $"{h.Offset} {h.Match}"))}");
        var bigHits = F(a, "translate_big.txt");
        Check(bigHits.Hits.Select(h => h.Offset).SequenceEqual(new[] { Off(bigText, "Aim Horizontal"), Off(bigText, "Aim Horizontal", 1) }), $"UTF-16 across the cut: {string.Join(", ", bigHits.Hits.Select(h => h.Offset))}");
        Check(F(a, "eight.txt").Count == 2 && !F(a, "eight.txt").Hits[0].Utf16, "the UTF-8 copy is searched as bytes");
        var lodHits = F(a, "x.lod");
        Check(lodHits.Count == 1 && lodHits.Hits[0].Offset == 1000 && !lodHits.Hits[0].Utf16, $"the .lod (FE FF, binary): {lodHits.Count} hits at {string.Join(", ", lodHits.Hits.Select(h => h.Offset))}, only its 8-bit text");
        // A text outside ASCII, and a regular expression, in UTF-16
        var gross = Run(report, "wide.esm", new GrepOptions { Text = "bildschirmgröße" });
        Check(gross.Files.Select(f => f.Path).Order(StringComparer.Ordinal).SequenceEqual(new[] { @"interface\eight.txt", @"interface\translate_be.txt", @"interface\translate_en.txt" }) && F(gross, "translate_en.txt").Hits[0].Match == "Bildschirmgröße",
            $"non-ASCII text in UTF-16 and UTF-8: {string.Join(", ", gross.Files.Select(f => f.Path))}");
        var rx = Run(report, "wide.esm", new GrepOptions { Text = @"^\$aim\s+\w+", Regex = true, Extensions = new[] { "txt" } });
        Check(rx.Files.Count == 3 && rx.Files.All(f => f.Count == 1), "a regular expression in UTF-16 (and in UTF-8): " + string.Join(", ", rx.Files.Select(f => $"{f.Path}x{f.Count}")));
        Check(a.Notes.Any(n => n.Contains("UTF-16 byte-order mark", StringComparison.Ordinal)), "the note on UTF-16 files: " + string.Join(" | ", a.Notes));
        ctx.WriteReport("archive-grep-utf16.txt", a.ToText(new ReportTextOptions { MaxChars = int.MaxValue, Detail = true }));
        return $"UTF-16 by the byte-order mark: LE and BE tables found at their byte offsets and lines (across the 4 MB cut too), non-ASCII text, a regex; a .lod starting FE FF read as bytes";
    }

    /// <summary>
    /// J55: a text or regex with a letter outside ASCII matches it in either case in an 8-bit file, written as UTF-8 or
    /// as Windows-1252, and in a file that mixes them (as the game's Starfield_en.strings does); offsets are bytes of the
    /// file, across the 4 MB cuts too, after characters of 2 to 4 bytes. CJK bytes whose lead byte is a Latin-1 letter
    /// of the other case (E3 B6 80 against ö's C3 B6) are no longer a hit. An ASCII text is matched on the bytes as before.
    /// </summary>
    static string CaseOutsideAscii(Ctx ctx)
    {
        string data = ctx.Folder("grep-case");
        // A file is made of parts; each part is UTF-8, Windows-1252 or raw bytes, and names the searches that must find it
        var t1252 = new Dictionary<char, byte> { ['’'] = 0x92, ['€'] = 0x80, ['Œ'] = 0x8C, ['œ'] = 0x9C };
        (byte[] Bytes, string[] Tags) U(string s, params string[] tags) => (Encoding.UTF8.GetBytes(s), tags);
        (byte[] Bytes, string[] Tags) W(string s, params string[] tags) => (s.Select(c => c < 0x100 ? (byte)c : t1252[c]).ToArray(), tags);
        (byte[] Bytes, string[] Tags) Raw(byte[] b) => (b, Array.Empty<string>());
        (byte[] Bytes, string[] Tags) Filler(int bytes)
        {
            // Lines of characters of two bytes (13 bytes a line), so the file's characters and bytes part ways; x up to the length
            const string line = "é filler ü\n";
            var sb = new StringBuilder();
            for (int k = 0; k < bytes / 13; k++) sb.Append(line);
            return U(sb.Append('x', bytes % 13).ToString());
        }
        // X1: the hits of the ASCII regex gr\xC3\xB6 (grö's UTF-8 bytes C3 B6, g and r in either case; J69)
        const string T1 = "grösse", T2 = "ÉCRAN", R1 = "gr[öo]sse", X1 = "X1";
        // The cuts of the 4 MB pieces (the 8-byte head read first shifts them): grÖSSE's Ö and Écran's É across them
        int cut1 = (4 << 20) + 8 - (64 << 10), cut2 = (8 << 20) + 8 - (64 << 10);
        var big = new List<(byte[] Bytes, string[] Tags)> { Filler(cut1 - 3), U("grÖSSE", T1, R1), Filler(97), U("GRösse", T1, R1, X1), Filler(cut2 - 1 - (cut1 + 108)), U("Écran", T2), Filler(1 << 20), U("grösse", T1, R1, X1) };
        var made = new List<(string Path, List<(byte[] Bytes, string[] Tags)> Parts, bool Text)>
        {
            (@"interface\case_utf8.txt", new() { U("$Size\tBildschirm"), U("größe", X1), U("\r\n$A\t"), U("GRÖSSE", T1, R1), U(" "), U("grösse", T1, R1, X1), U("\r\n\U0001F600"), U("Grösse", T1, R1, X1),
                U("\r\n$B\t"), U("Écran", T2), U(" "), U("écran", T2), U(" "), U("ÉCRAN", T2), U("\r\n"), U("gr㶀sse"), U(" "), U("grosse", R1), U("\r\n") }, true),
            (@"interface\case_1252.txt", new() { W("$A\t"), W("GRÖSSE", T1, R1), W("\r\nCaptain’s "), W("écran", T2), W(" Œuvre\r\n") }, true),
            (@"strings\case_mixed.strings", new() { Raw(new byte[] { 3, 0, 0, 0 }), U("Grösse", T1, R1, X1), Raw(new byte[] { 0 }), W("ÉCRAN", T2), Raw(new byte[] { 0 }), W("grÖsse", T1, R1), Raw(new byte[] { 0 }), U("’ ü"), Raw(new byte[] { 0 }) }, false),
            (@"meshes\case\binary.mesh", new() { Raw(Binary(3001)), U("GRÖSSE", T1, R1), Raw(Binary(777)), W("écran", T2), Raw(Binary(500)) }, false),
            (@"interface\case_big.txt", big, true),
        };
        var files = made.Select(m => (m.Path, m.Parts.SelectMany(p => p.Bytes).ToArray(), Compress: m.Path.EndsWith(".txt", StringComparison.Ordinal))).ToList();
        WriteBa2(Path.Combine(data, "case - main.ba2"), files);
        // Each search's hits: file, byte offset, line (text files)
        List<(string Path, long Offset, long? Line)> Expected(string tag)
        {
            var list = new List<(string, long, long?)>();
            foreach (var m in made)
            {
                long at = 0, lines = 1;
                foreach (var p in m.Parts)
                {
                    if (p.Tags.Contains(tag)) list.Add((m.Path, at, m.Text ? lines : null));
                    at += p.Bytes.Length; lines += p.Bytes.Count(x => x == '\n');
                }
            }
            return list.OrderBy(x => x.Item1, StringComparer.Ordinal).ThenBy(x => x.Item2).ToList();
        }
        List<(string Path, long Offset, long? Line)> Got(GrepAnswer x) =>
            x.Files.SelectMany(f => f.Hits.Select(h => (f.Path, h.Offset, h.Line))).OrderBy(h => h.Path, StringComparer.Ordinal).ThenBy(h => h.Offset).ToList();
        string Show(IEnumerable<(string Path, long Offset, long? Line)> hits) => string.Join(", ", hits.Select(h => $"{Path.GetFileName(h.Path)}@{h.Offset}{(h.Line != null ? ":" + h.Line : "")}"));
        using var session = new ReportSession(data, ctx.Folder("grep-case-scratch"), ctx.Schema);
        var report = new ArchiveReport(session);
        var said = new List<string>();
        // A piece is read as text only when its bytes hold the text's anchor; the anchor of a regex is read from it
        foreach (var (pattern, regex, want) in new[] {
            ("AUSRÜSTUNG", false, "STUNG"), ("ö", false, ""), (@"meshes\größe", false, "meshes"), ("ausr[üu]stung", true, "stung"), ("gr[öo]sse", true, "sse"),
            ("a|ö", true, ""), ("(?:x|gr)[öo]sse|zö", true, ""), ("(?x)ö # comment", true, ""), ("abcde*ö", true, "abcd"), ("abc+ö", true, "abc"), ("ab{12}ö", true, "a"),
            (@"\x41bcö", true, "bc"), (@"\p{Lu}ö", true, ""), ("[a|b]cdö", true, "cd"), ("[a-z-[aeiou]]xyzö", true, "xyz"), ("(?#a|(b)stuvö", true, "stuv"), ("parkö", true, "par"), (@"(?<n>x)ö\k<n>yz", true, "yz") })
        {
            string got = ArchiveGrep.Matcher.Anchor(pattern, regex);
            Check(got == want, $"the anchor of {pattern}{(regex ? " (regex)" : "")}: \"{got}\", expected \"{want}\"");
        }
        foreach (var (text, regex, tag) in new[] { (T1, false, T1), ("GRÖSSE", false, T1), (T2, false, T2), ("écran", false, T2), (R1, true, R1), ("GR[ÖO]SSE", true, R1),
            ("[éÉ]CRAN", true, T2), ("(?:x|gr)[öo]sse|zzzö", true, R1) })
        {
            var a = Run(report, "case.esm", new GrepOptions { Text = text, Regex = regex });
            var want = Expected(tag);
            Check(Got(a).SequenceEqual(want), $"{text}{(regex ? " (regex)" : "")}: got {Show(Got(a))}; expected {Show(want)}");
            Check(a.Notes.Any(n => n.Contains("outside ASCII", StringComparison.Ordinal)), $"{text}: the note on reading as UTF-8 text: " + string.Join(" | ", a.Notes));
            var count = CountOf(report, "case.esm", new GrepOptions { Text = text, Regex = regex, Count = true });
            Check(count.All.Hits == want.Count, $"{text} --count: {count.All.Hits} hits, expected {want.Count}");
            said.Add($"{text}{(regex ? " (regex)" : "")} {want.Count}");
        }
        // J69: an ASCII regex with byte codes matches those bytes only: gr\xC3\xB6 is grö's UTF-8 bytes (g and r in either
        // case), not gr + E3 B6 (a CJK character), which matched before (6 hits, the CJK one among them)
        var codes = Run(report, "case.esm", new GrepOptions { Text = @"gr\xC3\xB6", Regex = true });
        Check(Got(codes).SequenceEqual(Expected(X1)), $"gr\\xC3\\xB6 (regex): got {Show(Got(codes))}; expected {Show(Expected(X1))}");
        said.Add($"gr\\xC3\\xB6 (regex) {Expected(X1).Count}");
        // The text around a hit is the text read: the 1252 GRÖSSE shown as it is
        var t1 = Run(report, "case.esm", new GrepOptions { Text = T1 });
        var w1252 = t1.Files.Single(f => f.Path.EndsWith("case_1252.txt", StringComparison.Ordinal)).Hits.Single();
        Check(w1252.Match == "GRÖSSE" && w1252.Around.Contains("«GRÖSSE»", StringComparison.Ordinal), $"1252: match {w1252.Match}, around {w1252.Around}");
        var u8 = t1.Files.Single(f => f.Path.EndsWith("case_utf8.txt", StringComparison.Ordinal)).Hits;
        Check(u8.Count == 3 && u8[2].Match == "Grösse", $"after a 4-byte character: {string.Join(", ", u8.Select(h => h.Match))}");
        Check(t1.PatternMBps > 0 && t1.PatternSampleBytes > 0, $"the text outside ASCII is timed on a sample: {t1.PatternMBps:F0} MB/s");
        // An ASCII text: matched on the bytes as before (no note, no sample)
        var ascii = Run(report, "case.esm", new GrepOptions { Text = "CAPTAIN" });
        Check(ascii.Hits == 1 && ascii.Files[0].Hits[0].Offset == "$A\tGRÖSSE\r\n".Length && !ascii.Notes.Any(n => n.Contains("outside ASCII", StringComparison.Ordinal)) && ascii.PatternMBps == 0,
            $"ASCII text: {ascii.Hits} hits at {string.Join(", ", ascii.Files.SelectMany(f => f.Hits).Select(h => h.Offset))}; notes {string.Join(" | ", ascii.Notes)}");
        // Nothing found: the sentence says how the files were read
        var none = Run(report, "case.esm", new GrepOptions { Text = "größer" });
        Check(none.Hits == 0 && none.EmptyReason != null && none.EmptyReason.Contains("Windows-1252", StringComparison.Ordinal), "nothing found: " + none.EmptyReason);
        ctx.WriteReport("archive-grep-case.txt", t1.ToText(new ReportTextOptions { MaxChars = int.MaxValue, Detail = true }));
        return $"a letter outside ASCII in either case, in UTF-8, 1252 and mixed 8-bit files, at its byte offset and line (across the 4 MB cuts too): {string.Join(", ", said)}; the CJK false friend gr\\u3D80sse not found; an ASCII text on the bytes as before";
    }

    static GrepCountAnswer CountOf(ArchiveReport report, string target, GrepOptions o)
    {
        using var plan = report.PlanGrep(target, o);
        return report.GrepCount(plan);
    }

    static string Game(Ctx ctx)
    {
        var report = new ArchiveReport(ctx.Session);
        // ---- SFBGS007's database names a material of the developers' test folder (J8's probe)
        var a = Run(report, "SFBGS007.esm", new GrepOptions { Text = "MGoulet_Pattern05_Test.mat" });
        Check(a.Files.Count == 1 && a.Files[0].Where == "SFBGS007 - Main.ba2" && a.Files[0].Path.Equals(@"materials\creations\sfbgs007\materialsbeta.cdb", StringComparison.OrdinalIgnoreCase) && a.Files[0].Count >= 2,
            $"SFBGS007: {string.Join(", ", a.Files.Select(f => $"{f.Where}:{f.Path}x{f.Count}"))}");
        Check(a.Files[0].Hits.All(h => h.Around.Contains(@"Data\Materials\Test\Test\«MGoulet_Pattern05_Test.mat»", StringComparison.Ordinal)), "SFBGS007: a hit isn't in Data\\Materials\\Test\\Test: " + a.Files[0].Hits[0].Around);
        Check(a.EstimateFiles == a.ItemsSearched && a.EstimateBytes == a.BytesSearched, $"SFBGS007: estimated {a.EstimateFiles} files {a.EstimateBytes} bytes, read {a.ItemsSearched} and {a.BytesSearched}");
        ctx.WriteReport("archive-grep-sfbgs007.txt", a.ToText(new ReportTextOptions { MaxChars = int.MaxValue, Detail = true }));

        // ---- the estimate against the real read of a larger scope: the game's animations archive (47,000 files)
        var b = Run(report, "Starfield - Animations.ba2", new GrepOptions { Text = "zz_not_in_any_file_zz" });
        Check(b.Hits == 0 && b.EstimateFiles == b.ItemsSearched && b.EstimateBytes == b.BytesSearched, $"Animations: estimated {b.EstimateFiles} files {b.EstimateBytes} bytes, read {b.ItemsSearched} and {b.BytesSearched}");
        double ratio = b.SearchSeconds / b.EstimateSeconds;
        Check(ratio < 3, $"Animations: the search took {b.SearchSeconds:F1} s, {ratio:F1} times the estimate of {b.EstimateSeconds:F1} s");
        // The same archive with the S69 census's 21-way UTF-16 alternation (J48): its own speed timed on a sample first,
        // and the time under 3 times the estimate (the census's call took 5 times its estimate), the estimate under 5 times
        // the time (the archive is warm here: the estimate is for a cold disk)
        var r = Run(report, "Starfield - Animations.ba2", new GrepOptions { Text = Census21, Regex = true });
        double rratio = r.SearchSeconds / r.EstimateSeconds;
        Check(r.PatternMBps > 0 && r.PatternSampleBytes > 0 && r.EstimateSeconds > b.EstimateSeconds && rratio < 3 && rratio > 1 / 5.0,
            $"Animations, 21-way: the pattern {r.PatternMBps:F0} MB/s on {r.PatternSampleBytes} bytes; estimate {r.EstimateSeconds:F1} s (the plain text's {b.EstimateSeconds:F1} s), took {r.SearchSeconds:F1} s");

        // ---- J39: the interface's UTF-16 translate tables (the S69 census: 18 hits in the 9 translate_*.txt)
        var t = Run(report, "Starfield - Interface.ba2", new GrepOptions { Text = "Aim Horizontal Sensitivity" });
        Check(t.Hits == 18 && t.Files.Count == 9 && t.Files.All(f => f.Path.StartsWith(@"interface\translate_", StringComparison.OrdinalIgnoreCase) && f.Hits.All(h => h.Utf16 && h.Line != null)),
            $"Interface: {t.Hits} hits in {string.Join(", ", t.Files.Select(f => $"{f.Path}x{f.Count}"))}");
        ctx.WriteReport("archive-grep-translate.txt", t.ToText(new ReportTextOptions { MaxChars = int.MaxValue, Detail = true }));

        // ---- J55: the game's string tables. starfield_en.strings keeps "Swaney’s Slate" in Windows-1252 among UTF-8
        // strings (found 0 times before); starfield_de.strings is UTF-8, its Ausrüstung found in capitals too
        var swaney = Run(report, "Starfield - Localization.ba2", new GrepOptions { Text = "SWANEY’S SLATE" });
        Check(swaney.Hits == 1 && swaney.Files.Single().Path.Equals(@"strings\starfield_en.strings", StringComparison.OrdinalIgnoreCase) && swaney.Files[0].Hits[0].Match == "Swaney’s Slate",
            $"Swaney’s Slate (1252): {string.Join(", ", swaney.Files.Select(f => $"{f.Path}x{f.Count} {f.Hits[0].Match}"))}");
        long lower = Run(report, "Starfield - Localization.ba2", new GrepOptions { Text = "ausrüstung", Extensions = new[] { "strings" } }).Hits;
        long upper = Run(report, "Starfield - Localization.ba2", new GrepOptions { Text = "AUSRÜSTUNG", Extensions = new[] { "strings" } }).Hits;
        Check(lower > 0 && upper == lower, $"Ausrüstung in the string tables: {lower} in lower case, {upper} in capitals");
        return $"SFBGS007: {N(a.Hits)} hits in its materialsbeta.cdb under Data\\Materials\\Test\\Test, as J8's probe found; Starfield - Animations.ba2: {N(b.ItemsSearched)} files, {b.BytesSearched / 1048576.0:F0} MB as estimated, {b.SearchSeconds:F1} s for an estimate of {b.EstimateSeconds:F1} s; the 21-way regex {r.SearchSeconds:F1} s for {r.EstimateSeconds:F1} s ({r.PatternMBps:F0} MB/s a thread on the sample); Interface: \"Aim Horizontal Sensitivity\" {t.Hits} hits in {t.Files.Count} UTF-16 tables; Localization: \"SWANEY’S SLATE\" 1 hit in 1252, Ausrüstung {lower} hits in either case";
    }

    /// <summary>The S69 census's call B: UTF-16 LE spellings of 16 file types and 5 Data folders, one alternation.</summary>
    internal const string Census21 = @"[A-Za-z0-9_]\x00\.\x00(?:d\x00d\x00s|n\x00i\x00f|m\x00a\x00t|m\x00e\x00s\x00h|w\x00e\x00m|p\x00e\x00x|p\x00s\x00c|b\x00a\x002|e\x00s\x00[mpl]|s\x00w\x00f|g\x00f\x00x|t\x00x\x00t|i\x00n\x00i|j\x00s\x00o\x00n|x\x00m\x00l|a\x00f)\x00|(?:m\x00e\x00s\x00h\x00e\x00s|t\x00e\x00x\x00t\x00u\x00r\x00e\x00s|m\x00a\x00t\x00e\x00r\x00i\x00a\x00l\x00s|s\x00c\x00r\x00i\x00p\x00t\x00s|i\x00n\x00t\x00e\x00r\x00f\x00a\x00c\x00e)\x00[\/]\x00";
}
