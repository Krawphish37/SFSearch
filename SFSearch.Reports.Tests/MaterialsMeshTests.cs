// How a mesh names its materials (GAPS S46): as text, or by hash.
//  - The texts: only whole length-prefixed texts count, so a header string followed by a length whose first byte is
//    a letter is read whole (the old reader's "\.mat\b" lost it), and the bytes before "mat\0" in a resource ID are
//    never a name.
//  - A world's object LOD mesh (sfta01 - main.ba2, meshes\terrain\sfta01sumaticampworld\objects\
//    sfta01sumaticampworld.8.-4.-4.nif): no material text, 800 materials by resource ID (folder hash, file hash,
//    "mat\0"), each in the game's compiled database. The old reader made "v.mat" of one of them.
//  - The report: sfta01.esm and sfta04.esm list no fragment (".mat", "2i.mat", "pc.mat", "r.mat", "ub.mat", "v.mat";
//    "5.mat") and nothing missing; their materials named by hash are listed by their hashes, each in a compiled
//    database. A real missing material is still missing (aa50_longwatch.esm's laser_ir_glow.mat), and atom74.esm's
//    monocle glass is used by its 3 meshes, not 1.
//  - The structured form stays under the 200,000 characters a caller gets for sfta01.esm and sfta04.esm (the
//    materials named by hash and found are counted per database, not listed), and lists every material with a
//    problem, every missing one and every one named by path, for those two, aa50_longwatch.esm and darkstar.esm.
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class MaterialsMeshTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The reader before fix-s46, for showing what it made of the same bytes.</summary>
    static readonly Regex OldReader = new(@"[A-Za-z0-9_\\/ .\-]+\.mat\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    const string LodMesh = @"meshes\terrain\sfta01sumaticampworld\objects\sfta01sumaticampworld.8.-4.-4.nif";
    static readonly string[] Fragments = { ".mat", "2i.mat", "pc.mat", "r.mat", "ub.mat", "v.mat", "5.mat" };

    /// <summary>The structured form as a caller gets it (ReportTools: without notes, source and cost), checked: every
    /// material with a problem is listed in full, and the listed rows and those counted under namedByHashFound are
    /// all the rows. Its length in characters.</summary>
    static int JsonSize(MaterialsAnswer a, string plugin, StringBuilder log)
    {
        var o = a.ToJson();
        o.Remove("notes"); o.Remove("source"); o.Remove("cost");
        string json = o.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var listed = o["materials"]!.AsArray().Select(m => (string)m!["material"]!).ToHashSet(StringComparer.Ordinal);
        int folded = (int?)o["namedByHashFound"]?["materials"] ?? 0;
        var lost = a.Rows.Where(m => (m.HasProblem || m.NotChecked || m.Hash == null) && !listed.Contains(m.Path)).Select(m => m.Path).ToList();
        Check(lost.Count == 0, $"{plugin}: rows with a problem, or named by path, left out of the structured form: {string.Join(", ", lost.Take(5))}");
        Check(listed.Count + folded == a.Rows.Count && a.Rows.Where(m => m.Missing).All(m => listed.Contains(m.Path)),
            $"{plugin}: the structured form lists {listed.Count} and counts {folded} by hash, of {a.Rows.Count} materials");
        log.Append(plugin).Append(": structured form ").Append(json.Length).Append(" characters, ").Append(listed.Count).Append(" listed, ").Append(folded).Append(" counted by hash").Append('\n');
        return json.Length;
    }

    public static string Names(Ctx ctx)
    {
        var parts = new List<string>();
        var log = new StringBuilder();

        // Made-up bytes: two texts, the second's length a letter ('M' = 77), then a resource ID whose file hash ends in '.'
        var b = new List<byte>();
        void U32(uint v) { var x = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(x, v); b.AddRange(x); }
        string first = @"materials\setdressing\x\first.mat", second = @"materials\" + new string('a', 63) + ".mat";
        U32((uint)first.Length); b.AddRange(Encoding.ASCII.GetBytes(first));
        U32((uint)second.Length); b.AddRange(Encoding.ASCII.GetBytes(second));
        U32(0xB64DBB92); U32(0x2E762E21); b.AddRange("mat\0"u8.ToArray()); // file hash bytes "!.v." before "mat"
        var bytes = b.ToArray();
        var texts = MeshMaterialNames.PrefixedTexts(bytes, 0).Select(s => Encoding.ASCII.GetString(bytes[s.Start..s.End])).ToList();
        var old = OldReader.Matches(Encoding.Latin1.GetString(bytes)).Select(m => m.Value).ToList();
        log.Append("made-up bytes: new ").Append(string.Join(", ", texts)).Append("; old ").Append(string.Join(", ", old)).Append('\n');
        Check(texts.SequenceEqual(new[] { first, second }), $"made-up bytes: the texts read are {string.Join(", ", texts)}");
        Check(!old.Contains(first) && old.Contains(second) && old.Any(o => o.EndsWith("v.mat", StringComparison.Ordinal)), $"made-up bytes: the old reader read {string.Join(", ", old)} (expected it to lose the first text, followed by a length of 77, and make a fragment of the ID)");
        parts.Add("made-up bytes: both length-prefixed texts read (the old reader lost the one followed by a length of 77, 'M'), the resource ID's bytes read as no text (the old reader made \"v.mat\" of them)");

        // The world LOD mesh of the GAPS row
        string archive = Path.Combine(ctx.Data, "sfta01 - main.ba2");
        var file = Mutagen.Bethesda.Archives.Archive.CreateReader(Mutagen.Bethesda.GameRelease.Starfield, archive).Files
            .Single(f => f.Path.ToString().Replace('/', '\\').Equals(LodMesh, StringComparison.OrdinalIgnoreCase));
        var mesh = file.GetBytes();
        var names = MeshMaterialNames.Read(mesh);
        var game = ctx.Session.Archives.Database(Path.Combine(ctx.Data, "Starfield - Materials.ba2"), MaterialDatabase.GamePath);
        int inGame = names.Hashed.Count(h => game.Has(h.File, h.Dir));
        var oldMesh = OldReader.Matches(Encoding.Latin1.GetString(mesh)).Select(m => m.Value).Distinct().ToList();
        log.Append(LodMesh).Append(": texts ").Append(names.Texts.Count).Append(", by hash ").Append(names.Hashed.Count).Append(", in the game's database ").Append(inGame)
            .Append("; old reader: ").Append(string.Join(", ", oldMesh)).Append('\n');
        foreach (var (dir, file2) in names.Hashed) log.Append("  folder 0x").Append(dir.ToString("X8")).Append(" file 0x").Append(file2.ToString("X8")).Append('\n');
        Check(names.HeaderRead && names.Texts.Count == 0 && names.Hashed.Count >= 700 && inGame == names.Hashed.Count,
            $"{LodMesh}: header read {names.HeaderRead}, {names.Texts.Count} texts, {names.Hashed.Count} by hash ({inGame} in the game's database)");
        Check(oldMesh.Contains("v.mat"), $"{LodMesh}: the old reader read {string.Join(", ", oldMesh)}, not v.mat");
        parts.Add($"{LodMesh}: no text, {N(names.Hashed.Count)} materials by hash, all in the game's database (the old reader made \"v.mat\")");

        // The reports
        var report = new MaterialsReport(ctx.Session);
        foreach (var plugin in new[] { "sfta01.esm", "sfta04.esm" })
        {
            var a = report.Materials(plugin);
            var fragments = a.Rows.Where(m => m.Hash == null && (Fragments.Contains(m.Path) || Path.GetFileName(m.Path).Length <= 6)).Select(m => m.Path).ToList();
            var byHash = a.Rows.Where(m => m.Hash != null).ToList();
            log.Append(plugin).Append(": ").Append(a.Rows.Count).Append(" materials, ").Append(byHash.Count).Append(" named only by hash in ").Append(a.MeshesByHash)
                .Append(" meshes, ").Append(a.Missing).Append(" missing\n");
            Check(fragments.Count == 0, $"{plugin}: fragments listed as materials: {string.Join(", ", fragments)}");
            Check(a.Missing == 0 && byHash.Count > 0 && byHash.All(m => m.Compiled && m.Path == MaterialRow.HashText(m.Hash!.Value.Dir, m.Hash.Value.File)),
                $"{plugin}: {a.Missing} missing; {byHash.Count} by hash, {byHash.Count(m => m.Compiled)} of them compiled");
            string text = a.ToText(new ReportTextOptions());
            Check(text.Contains($"{N(byHash.Count)} named only by hash, in ", StringComparison.Ordinal) && !text.Contains("MISSING", StringComparison.Ordinal),
                $"{plugin}: the text doesn't count the materials named by hash, or says MISSING");
            if (plugin == "sfta01.esm")
                Check(byHash.Any(m => m.UsedBy.Contains(LodMesh, StringComparer.OrdinalIgnoreCase)), $"{plugin}: no material named by hash is used by {LodMesh}");
            int size = JsonSize(a, plugin, log);
            Check(size < 200_000, $"{plugin}: the structured form is {N(size)} characters, over the 200,000 a caller gets");
            parts.Add($"{plugin}: {N(a.Rows.Count)} materials, {N(byHash.Count)} named only by hash (in {a.MeshesByHash} meshes, each in a compiled database), no fragment, nothing missing; structured form {N(size)} characters");
        }
        var aa50 = report.Materials("aa50_longwatch.esm");
        var laser = aa50.Rows.SingleOrDefault(m => m.Path == @"materials\weapons\scopereticles\laser_ir_glow.mat");
        Check(laser != null && laser.Missing && aa50.Missing == 1 && aa50.ByHash == 0, $"aa50_longwatch.esm: laser_ir_glow.mat missing {laser?.Missing}; {aa50.Missing} missing, {aa50.ByHash} by hash");
        var atom = report.Materials("atom74.esm");
        var monocle = atom.Rows.SingleOrDefault(m => m.Path == @"materials\weapons\atom74\sights\glasses\atom74_monocle_glass.mat");
        Check(monocle != null && monocle.UsedBy.Count == 3, $"atom74.esm: the monocle glass is used by {monocle?.UsedBy.Count} meshes, not 3");
        int aaSize = JsonSize(aa50, "aa50_longwatch.esm", log);
        var dark = report.Materials("darkstar.esm");
        int darkSize = JsonSize(dark, "darkstar.esm", log);
        parts.Add($"aa50_longwatch.esm: laser_ir_glow.mat still MISSING; atom74.esm: its monocle glass used by its 3 meshes (the old reader saw 1); structured forms with every missing material and every problem listed: aa50_longwatch.esm {N(aaSize)} characters, darkstar.esm {N(darkSize)} ({dark.Missing} missing)");

        ctx.WriteReport("materials-mesh.txt", log.ToString());
        return string.Join("; ", parts) + ".";
    }
}
