// What a material file inherits (GAPS S8, MaterialFile.cs).
//  - Made-up files: an object's own component takes the place of its parent's of the same class and index; a slot
//    set to no file and a link set to nothing take the parent's away; a part of a parent file is found through the
//    file's parents and its "Import" list; a parent found nowhere is told by the name the file writes; a link back
//    to the material itself ("<this>") and a parent that leads back to its child end the walk.
//  - Every material file in Data against the summary the Creation Kit wrote into it ("Summary": each layer's and
//    blender's textures as the editor saw them when the file was saved, with what was inherited then): the textures
//    listed hold every texture of the summary, but for a file whose parent isn't in Data or was changed since; there,
//    no object of the file itself names the texture left out. (A file a database also holds is
//    no twin to hold it against: the 8 here are loose files that replace a material of the game's, one on purpose
//    with other textures.)
//  - The reports: a material file that names no texture of its own lists the ones it inherits, each marked so; a
//    texture the file inherits from the game's database that the game doesn't ship isn't counted as missing; a parent
//    in no file and no database is told in a note.
//  - A material that only meshes of the game's own archives name (meshes a plugin's records name) and nothing holds is
//    the game's gap: told apart, not counted as missing (SFBGS050.esm and the game's orionrifle.nif).
//  - The single-material view (materials2 --material) looks for a part in the databases the game reads as the report
//    does, reading each whole when the search gets to it.
//  - The single-material view (materials2 --material) shows the same: for every fourth material file of seven plugins, the
//    textures of its parts, and the missing ones, are the ones the report lists for it.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class MaterialFileTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    static string Component(string type, int index, string data) => $"{{\"Data\": {data}, \"Index\": {index}, \"Type\": \"{type}\"}}";
    static string Texture(int index, string file) => Component("BSMaterial::MRTextureFile", index, $"{{\"FileName\": {JsonSerializer.Serialize(file)}}}");
    static string Link(string type, int index, string id) => Component("BSMaterial::" + type, index, $"{{\"ID\": \"{id}\"}}");
    static string Obj(string? id, string parent, params string[] components) =>
        "{\"Components\": [" + string.Join(", ", components) + "]" + (id != null ? $", \"ID\": \"{id}\"" : "") + $", \"Parent\": {JsonSerializer.Serialize(parent)}}}";
    static string Mat(string[] imports, params string[] objects) =>
        "{\"Import\": [" + string.Join(", ", imports.Select(i => JsonSerializer.Serialize(i))) + "], \"Objects\": [" + string.Join(", ", objects) + "], \"Version\": 1}";

    static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static string Files(Ctx ctx)
    {
        var parts = new List<string>();
        var log = new StringBuilder();

        // ---------------------------------------------------------------- Made-up files
        {
            // The parent: a layer with a material with a texture set of three textures, a blender with a mask, a LOD material
            string parent = Mat(Array.Empty<string>(),
                Obj(null, "",
                    Link("LayerID", 0, "res:00000001:00000001:AAAAAAAA"), Link("BlenderID", 0, "res:00000005:00000001:AAAAAAAA"), Link("LODMaterialID", 1, "res:00000007:00000001:AAAAAAAA"),
                    Component("BSMaterial::ShaderModelComponent", 0, "{\"FileName\": \"1LayerStandard\"}")),
                Obj("res:00000001:00000001:AAAAAAAA", "", Link("MaterialID", 0, "res:00000002:00000001:AAAAAAAA"), Link("UVStreamID", 0, "res:00000004:00000001:AAAAAAAA")),
                Obj("res:00000002:00000001:AAAAAAAA", "", Link("TextureSetID", 0, "res:00000003:00000001:AAAAAAAA")),
                Obj("res:00000003:00000001:AAAAAAAA", "", Texture(0, @"Data\Textures\p\color.dds"), Texture(1, @"Data\Textures\p\normal.dds"), Texture(3, @"Data\Textures\p\rough.dds")),
                Obj("res:00000004:00000001:AAAAAAAA", ""),
                Obj("res:00000005:00000001:AAAAAAAA", "",
                    Component("BSMaterial::DetailBlenderSettingsComponent", 0, "{\"DetailBlenderSettings\": {\"Data\": {\"DetailBlendMask\": {\"Data\": {\"Texture\": {\"Data\": {\"FileName\": \"Data\\\\Textures\\\\p\\\\mask.dds\"}, \"Type\": \"BSMaterial::MRTextureFile\"}}, \"Type\": \"BSMaterial::SourceTextureWithReplacement\"}}, \"Type\": \"BSMaterial::DetailBlenderSettings\"}}")),
                Obj("res:00000007:00000001:AAAAAAAA", "", Texture(0, @"Data\Textures\p\lod.dds")));
            // The child: its layer, material and texture set each made from the parent's; it sets the normal map,
            // takes the rough map away and adds an emissive one; its LOD material 1 is set to nothing, 2 to itself
            string child = Mat(new[] { @"Data\Materials\p\parent.mat" },
                Obj(null, @"Data\Materials\p\parent.mat", Link("LayerID", 0, "res:00000011:00000002:BBBBBBBB"), Link("LODMaterialID", 1, ""), Link("LODMaterialID", 2, "<this>")),
                Obj("res:00000011:00000002:BBBBBBBB", "res:00000001:00000001:AAAAAAAA", Link("MaterialID", 0, "res:00000012:00000002:BBBBBBBB")),
                Obj("res:00000012:00000002:BBBBBBBB", "res:00000002:00000001:AAAAAAAA", Link("TextureSetID", 0, "res:00000013:00000002:BBBBBBBB")),
                Obj("res:00000013:00000002:BBBBBBBB", "res:00000003:00000001:AAAAAAAA", Texture(1, @"Data\Textures\c\normal.dds"), Texture(3, ""), Texture(7, @"Data\Textures\c\emissive.dds")));
            // One whose parent and whose part's parent are nowhere, and whose part leads back to itself
            string lost = Mat(Array.Empty<string>(),
                Obj(null, @"Data\MATERIALS\NULL.mat", Link("LayerID", 0, "res:00000021:00000003:CCCCCCCC"), Link("BlenderID", 0, "res:000000FF:00000003:CCCCCCCC")),
                Obj("res:00000021:00000003:CCCCCCCC", "res:00000022:00000003:CCCCCCCC", Texture(0, @"Data\Textures\l\own.dds")),
                Obj("res:00000022:00000003:CCCCCCCC", "res:00000021:00000003:CCCCCCCC", Texture(1, @"Data\Textures\l\loop.dds")));
            var made = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [@"materials\p\parent.mat"] = parent, [@"materials\c\child.mat"] = child, [@"materials\l\lost.mat"] = lost,
            };
            var loaded = new Dictionary<string, MaterialFile?>(StringComparer.OrdinalIgnoreCase);
            MaterialFile? Load(string path) => loaded.TryGetValue(path, out var f) ? f
                : loaded[path] = made.TryGetValue(path, out var text) ? MaterialFile.Read(JsonNode.Parse(text, documentOptions: Options), path) : null;
            var walk = new MaterialFileWalk(Load, () => null, () => Array.Empty<MaterialContents>());
            string Show(MaterialFileWalk.Result r) => string.Join(", ", r.Textures.Select(t => MaterialsReport.Norm(t.Name) + (t.Inherited ? " (inherited)" : "")))
                + (r.Lost.Count > 0 ? "; not found: " + string.Join(", ", r.Lost) : "");

            var p = walk.Textures(Load(@"materials\p\parent.mat")!);
            log.Append("made-up parent: ").Append(Show(p)).Append('\n');
            Check(Show(p) == @"textures\p\color.dds, textures\p\lod.dds, textures\p\mask.dds, textures\p\normal.dds, textures\p\rough.dds",
                $"the made-up parent's textures: {Show(p)} (the shader model's name is no texture; the blender's mask, inside its settings, is one)");
            var c = walk.Textures(Load(@"materials\c\child.mat")!);
            log.Append("made-up child: ").Append(Show(c)).Append('\n');
            Check(Show(c) == @"textures\c\emissive.dds, textures\c\normal.dds, textures\p\color.dds (inherited), textures\p\mask.dds (inherited)",
                $"the made-up child's textures: {Show(c)} (expected its own normal and emissive maps, the parent's color map and blender mask; not the parent's normal map, the rough map it takes away, or the LOD material it sets to nothing)");
            var l = walk.Textures(Load(@"materials\l\lost.mat")!);
            log.Append("made-up lost: ").Append(Show(l)).Append('\n');
            Check(Show(l) == @"textures\l\loop.dds (inherited), textures\l\own.dds; not found: Data\MATERIALS\NULL.mat, res:000000FF:00000003:CCCCCCCC",
                $"the made-up material with a parent and a part nowhere: {Show(l)}");
            Check(MaterialFile.Read(JsonNode.Parse("{\"Version\": 1}"), "x") == null && MaterialFile.Read(JsonNode.Parse("[]"), "x") == null, "a JSON text without an Objects list was read as a material file");
            parts.Add("made-up files: an own component replaces the parent's of its class and index, an empty slot or link takes it away, a parent file's parts are found, what is nowhere is named, loops end");
        }

        // ---------------------------------------------------------------- Every material file against its compiled twin
        var store = ctx.Session.Archives;
        var databases = new List<(MaterialDatabase Db, MaterialContents C)>();
        var files = new Dictionary<string, (string Where, Func<byte[]> Bytes)>(StringComparer.OrdinalIgnoreCase);
        string materials = Path.Combine(ctx.Data, "materials");
        if (Directory.Exists(materials))
            foreach (var f in Directory.EnumerateFiles(materials, "*.mat", SearchOption.AllDirectories))
                files[Path.GetRelativePath(ctx.Data, f).ToLowerInvariant()] = ("loose", () => File.ReadAllBytes(f));
        foreach (var archive in store.AllInData())
        {
            var info = store.Open(archive);
            if (info.Error != null) continue;
            foreach (var f in info.Files)
            {
                if (f.Extension == ".mat") { var ff = f; files.TryAdd(f.Path.ToLowerInvariant(), (info.Name, () => ff.Bytes())); }
                else if (f.Extension == ".cdb" && MaterialDatabase.GameReads(f.Path, archive))
                {
                    var db = store.Database(archive, f.Path);
                    if (db.Error == null && store.Contents(db) is { } contents) databases.Add((db, contents));
                }
            }
        }
        databases = databases.OrderBy(d => d.Db.IsGame ? 0 : 1).ToList();
        Check(databases.Count > 0 && databases[0].Db.IsGame, "the game's compiled material database wasn't read");
        var game = databases[0].C;
        var all = new Dictionary<string, MaterialFile?>(StringComparer.OrdinalIgnoreCase);
        var summaries = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        int unreadable = 0;
        MaterialFile? LoadReal(string path)
        {
            if (all.TryGetValue(path, out var have)) return have;
            MaterialFile? file = null;
            if (files.TryGetValue(path, out var at))
                try
                {
                    var json = JsonNode.Parse(at.Bytes(), documentOptions: Options);
                    file = MaterialFile.Read(json, path);
                    // The file's own summary, when the Creation Kit wrote one: each layer's textures, with what the layer inherits
                    if (json?["Summary"] is JsonObject summary)
                    {
                        var named = new HashSet<string>(StringComparer.Ordinal);
                        void Named(JsonNode? x)
                        {
                            if (x is JsonObject o)
                                foreach (var (k, v) in o)
                                {
                                    if (k == "File" && v is JsonValue value && value.TryGetValue<string>(out var name)) { if (name.Length > 0) named.Add(MaterialsReport.Norm(name)); }
                                    else Named(v);
                                }
                            else if (x is JsonArray a) foreach (var i in a) Named(i);
                        }
                        Named(summary);
                        summaries[path] = named;
                    }
                }
                catch (JsonException) { unreadable++; }
            return all[path] = file;
        }
        var real = new MaterialFileWalk(LoadReal, () => game, () => databases.Skip(1).Select(d => d.C));
        int twins = 0, same = 0, withInherited = 0, noneOfOwn = 0, lostFiles = 0, read = 0, inheritedTextures = 0;
        int withSummary = 0, summaryHeld = 0, summaryTextures = 0, summaryInherited = 0, pastSummary = 0, summaryParentLost = 0;
        var differ = new List<string>();
        var summaryLacks = new List<string>();
        var summaryLost = new List<string>();
        var past = new List<string>();
        var lostNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (LoadReal(path) is not { Root: not null } file) continue;
            read++;
            var uses = real.Textures(file);
            var mine = uses.Textures.Select(t => MaterialsReport.Norm(t.Name)).ToHashSet(StringComparer.Ordinal);
            int inherited = uses.Textures.Count(t => t.Inherited);
            inheritedTextures += inherited;
            if (inherited > 0) withInherited++;
            if (uses.Textures.All(t => t.Inherited)) noneOfOwn++;
            if (uses.Lost.Count > 0) { lostFiles++; lostNames.UnionWith(uses.Lost); }
            if (summaries.TryGetValue(path, out var summed))
            {
                withSummary++;
                summaryTextures += summed.Count;
                summaryInherited += summed.Count(t => uses.Textures.Any(u => u.Inherited && MaterialsReport.Norm(u.Name) == t));
                pastSummary += mine.Count(t => !summed.Contains(t));
                if (past.Count < 40 && mine.FirstOrDefault(t => !summed.Contains(t)) is { } extra) past.Add($"{path}: {extra}");
                if (summed.IsSubsetOf(mine)) summaryHeld++;
                else
                {
                    if (uses.Lost.Count > 0) summaryParentLost++;
                    summaryLacks.Add($"{path} ({files[path].Where}): its summary names {string.Join(", ", summed.Except(mine).Take(4))}{(uses.Lost.Count > 0 ? "; not found: " + string.Join(", ", uses.Lost.Take(3)) : "; its parents give other textures now")}");
                    // A summary is what the Creation Kit saw when the file was saved, with what the file inherited then.
                    // What isn't listed now must be something the file inherited: a texture of a parent that isn't in
                    // Data, or that was changed since. A texture one of the file's own objects names must be listed
                    foreach (var t in summed.Except(mine))
                        if (file.Objects.Any(o => o.Entries.Any(e => e.Texture && MaterialsReport.Norm(e.Value) == t))) summaryLost.Add($"{path} ({files[path].Where}): {t}");
                }
            }
            // Its twin: the same path, compiled into a database the game reads
            foreach (var (db, contents) in databases)
            {
                if (contents.Find(path) is not { } node) continue;
                twins++;
                var theirs = contents.TexturesOf(node, game, out _).Select(MaterialsReport.Norm).ToHashSet(StringComparer.Ordinal);
                if (mine.SetEquals(theirs)) same++;
                else differ.Add($"{path} ({files[path].Where}) against {db.Shown}: only the file {string.Join(", ", mine.Except(theirs).Take(4))}; only the database {string.Join(", ", theirs.Except(mine).Take(4))}");
                break;
            }
        }
        log.Append($"{N(files.Count)} material files in Data, {N(read)} read ({unreadable} not readable as JSON), {N(withInherited)} with a texture they inherit ({N(inheritedTextures)} textures), {N(noneOfOwn)} that name none of their own, {N(lostFiles)} with a parent or part nowhere ({N(lostNames.Count)} names)\n");
        log.Append($"{N(twins)} are also compiled into a database the game reads; {N(same)} give the same textures\n");
        foreach (var d in differ) log.Append("  differs: ").Append(d).Append('\n');
        log.Append($"{N(withSummary)} have a summary of their layers' textures ({N(summaryTextures)} textures, {N(summaryInherited)} of them inherited); {N(summaryHeld)} list every texture of their summary; {N(pastSummary)} textures listed are in no summary (a blender's, a LOD material's, a part's the summary leaves out)\n");
        foreach (var d in summaryLacks) log.Append("  summary: ").Append(d).Append('\n');
        foreach (var d in summaryLost) log.Append("  summary, named by an object of the file and not listed: ").Append(d).Append('\n');
        foreach (var d in past) log.Append("  past its summary: ").Append(d).Append('\n');
        foreach (var n in lostNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)) log.Append("  nowhere: ").Append(n).Append('\n');
        ctx.WriteReport("materials-files.txt", log.ToString());
        Check(read >= 3000 && withInherited >= 500, $"{N(read)} material files read, {N(withInherited)} with an inherited texture: fewer than this Data folder held when the test was written (3,762 and 1,018)");
        Check(withSummary >= 2000 && summaryInherited >= 1000 && summaryHeld >= withSummary - 40,
            $"{N(withSummary)} material files have a summary, {N(summaryInherited)} of its textures are inherited ones, and {N(summaryHeld)} files list every texture of their summary: fewer than when the test was written (2,617, 1,339, 2,588)");
        Check(summaryLost.Count == 0, $"{N(summaryLost.Count)} textures a file's summary names, and an object of the file itself names, aren't listed: {string.Join(" | ", summaryLost.Take(3))}");
        parts.Add($"{N(read)} material files of Data read with what they inherit ({N(withInherited)} inherit a texture; {N(lostFiles)} name a parent or part that is nowhere); {N(summaryHeld)} of the {N(withSummary)} with the Creation Kit's summary of their layers list every texture it names ({N(summaryInherited)} of them inherited), and what the other {N(withSummary - summaryHeld)} leave out is what they inherited when the summary was written ({N(summaryParentLost)} with a parent that isn't in Data, {N(withSummary - summaryHeld - summaryParentLost)} whose parents give other textures now): none of it is named by an object of the file itself");

        // ---------------------------------------------------------------- The reports
        var report = new MaterialsReport(ctx.Session);
        {
            // darkstar.esm: a decal made from another of its material files sets only the normal map
            var a = report.Materials("darkstar.esm");
            var row = a.Rows.FirstOrDefault(m => m.Path == @"materials\darkstar\decalmetalboltstrims1_normal.mat");
            Check(row != null && !row.NoFile, "darkstar.esm: materials\\darkstar\\decalmetalboltstrims1_normal.mat isn't listed as a file");
            var own = row!.Textures.Where(t => !t.Inherited).Select(t => t.Path).ToList();
            var inherited = row.Textures.Where(t => t.Inherited).Select(t => t.Path).ToList();
            log.Append($"darkstar.esm decalmetalboltstrims1_normal.mat: own {string.Join(", ", own)}; inherited {string.Join(", ", inherited)}\n");
            Check(own.SequenceEqual(new[] { @"textures\common\decal\decalmetalboltstrimsdetails01_normal.dds" }) && inherited.Count >= 2 && inherited.Contains(@"textures\common\decal\decalmetalboltstrimsdetails01_rough.dds"),
                $"darkstar.esm: the decal's own textures are {string.Join(", ", own)}, inherited {string.Join(", ", inherited)}");
            var json = a.ToJson();
            Check((int?)json["counts"]?["texturesInheritedByFiles"] == a.InheritedTextures && a.InheritedTextures > 0, "darkstar.esm: the structured form doesn't count the inherited textures");
            var listed = json["materials"]!.AsArray().First(m => (string?)m!["material"] == row.Path)!["textures"]!.AsArray();
            Check(listed.Count(t => (bool?)t!["inherited"] == true) == inherited.Count, "darkstar.esm: the structured form doesn't mark the decal's inherited textures");
            string text = a.ToText(new ReportTextOptions());
            Check(text.Contains($"{N(a.InheritedTextures)} inherited by material files", StringComparison.Ordinal), "darkstar.esm: the text doesn't count the inherited textures");
            // A texture inherited from the game's own database that the game doesn't ship is the game's gap
            var lacks = a.Rows.Where(m => !m.NoFile).SelectMany(m => m.Textures).Where(t => t.GameLacks).ToList();
            Check(lacks.All(t => t.Inherited) && a.Rows.Where(m => !m.NoFile).All(m => m.MissingTextures.All(t => !t.GameLacks)), "darkstar.esm: a texture the game lacks is counted as missing, or isn't an inherited one");
            parts.Add($"darkstar.esm: {N(a.InheritedTextures)} textures its material files inherit are listed and marked; the decal made from another file lists its own normal map and the {inherited.Count} it inherits");
        }
        {
            // BountyHunter_Outfit_Z.esm: a material whose parent, "Data\MATERIALS\NULL.mat", is nowhere
            const string plugin = "BountyHunter_Outfit_Z.esm";
            if (File.Exists(Path.Combine(ctx.Data, plugin)))
            {
                var a = report.Materials(plugin);
                var row = a.Rows.FirstOrDefault(m => m.Path == @"materials\bh_outfit\bh_trench.mat");
                Check(row != null && row.NotFound.Contains(@"Data\MATERIALS\NULL.mat") && row.Unresolved >= 1 && !row.HasProblem == !(row.MissingTextures.Any() || row.Problem != null),
                    $"{plugin}: bh_trench.mat's parent that is nowhere isn't told ({(row == null ? "no row" : string.Join(", ", row.NotFound))})");
                Check(a.Notes.Any(n => n.Contains("a parent or a part that is in no material file and no compiled material database", StringComparison.Ordinal) && n.Contains(@"Data\MATERIALS\NULL.mat", StringComparison.Ordinal)),
                    $"{plugin}: no note names the parent that is nowhere");
                Check(row!.Textures.Any(t => t.Inherited), $"{plugin}: bh_trench.mat lists no inherited texture");
                parts.Add($"{plugin}: bh_trench.mat's parent Data\\MATERIALS\\NULL.mat is told as found nowhere, in a note and on its row, and isn't counted as a problem");
            }
            else parts.Add($"{plugin} isn't in Data: the parent found nowhere wasn't checked on a report");
        }
        // ---------------------------------------------------------------- The single-material view
        {
            // One material's parts list the textures the report lists for it: every material file of these plugins
            var view = new MaterialsView(ctx.Session);
            int compared = 0, viewInherited = 0;
            var viewDiffer = new List<string>();
            var asked = new List<string>();
            foreach (var plugin in new[] { "darkstar.esm", "nfvc0003vigilance.esm", "Nomad_Spacesuit_Z.esm", "BountyHunter_Outfit_Z.esm", "aa50_longwatch.esm", "apc_galaxysmartcontainers.esm", "cryomancer_z.esm" })
            {
                if (!File.Exists(Path.Combine(ctx.Data, plugin))) continue;
                asked.Add(plugin);
                // every fourth, to keep the test short: all 1,031 agreed when this was written (70 s)
                foreach (var row in report.Materials(plugin).Rows.Where(m => !m.NoFile && m.Problem == null && m.Origin == FileOrigin.Own).Where((_, k) => k % 4 == 0))
                {
                    var a = view.Material(row.Path, plugin);
                    var tree = a.Trees.FirstOrDefault(t => t.Source.StartsWith("the .mat file", StringComparison.Ordinal)).Tree;
                    if (tree == null) { viewDiffer.Add($"{row.Path} ({plugin}): the view shows no parts of the file"); continue; }
                    var shown = tree.All().SelectMany(p => p.Textures).Where(t => t.Path.Length > 0 && !t.NotDds).Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var listed = row.Textures.Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    compared++;
                    if (row.Textures.Any(t => t.Inherited)) viewInherited++;
                    if (!shown.SetEquals(listed))
                        viewDiffer.Add($"{row.Path} ({plugin}): only the view {string.Join(", ", shown.Except(listed).Take(3))}; only the report {string.Join(", ", listed.Except(shown).Take(3))}");
                    var shownMissing = tree.All().SelectMany(p => p.Textures).Where(t => t.Missing && !t.GameLacks).Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var listedMissing = row.MissingTextures.Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (!shownMissing.SetEquals(listedMissing))
                        viewDiffer.Add($"{row.Path} ({plugin}): missing in the view {string.Join(", ", shownMissing.Take(3))}; in the report {string.Join(", ", listedMissing.Take(3))}");
                }
            }
            log.Append($"the single-material view against the report: {N(compared)} material files of {string.Join(", ", asked)}, {N(viewInherited)} with an inherited texture, {N(viewDiffer.Count)} differ\n");
            foreach (var d in viewDiffer) log.Append("  view: ").Append(d).Append('\n');
            ctx.WriteReport("materials-files.txt", log.ToString());
            Check(compared >= 100 && viewInherited >= 20, $"only {N(compared)} material files were held against the view, {N(viewInherited)} with an inherited texture");
            Check(viewDiffer.Count == 0, $"the single-material view lists other textures than the report for {N(viewDiffer.Count)} of {N(compared)} material files: {string.Join(" | ", viewDiffer.Take(3))}");

            // The decal made from another file: its texture set shows its own normal map and what the parent file gives
            var decal = view.Material(@"materials\darkstar\decalmetalboltstrims1_normal.mat", "darkstar.esm");
            string text = decal.ToText(new ReportTextOptions { Detail = true });
            log.Append(text).Append('\n');
            Check(text.Contains(@"decalmetalboltstrimsdetails01_rough.dds (Starfield - Textures05.ba2) (from its parent)", StringComparison.Ordinal)
                && text.Contains("comes in part from another .mat file (materials\\darkstar\\decalmetalboltstrims01.mat)", StringComparison.Ordinal)
                && !text.Contains("isn't in any file or database read", StringComparison.Ordinal),
                "darkstar.esm's decal made from another .mat file: the view doesn't show what its parent file gives, or calls a part of the parent file not found");
            // A loose file whose parts are made from the game's root materials, and whose shader model has a FileName
            var neon = view.Material(@"materials\neon_entertainer\neon_entertainer_upperbody.mat");
            if (neon.FileWhere != null)
            {
                string neonText = neon.ToText(new ReportTextOptions { Detail = true });
                Check(!neonText.Contains("MISSING", StringComparison.Ordinal) && !neonText.Contains("ShaderModelComponent", StringComparison.Ordinal) && !neonText.Contains("isn't in any file or database read", StringComparison.Ordinal),
                    "neon_entertainer_upperbody.mat: the view shows its shader model's name as a texture, or calls the game's root materials not found");
            }
            // A part the game's database doesn't hold is looked for in the other databases the game reads, each read
            // whole when the search gets to it, as the report does: with none in memory, a material whose parent is
            // nowhere takes the search through all of them
            const string lostPlugin = "BountyHunter_Outfit_Z.esm";
            if (File.Exists(Path.Combine(ctx.Data, lostPlugin)))
            {
                ArchiveStore.ReleaseContents();
                Check(ArchiveStore.ContentsHeld().Databases == 0, "the databases read whole weren't let go");
                var trench = view.Material(@"materials\bh_outfit\bh_trench.mat", lostPlugin);
                int heldByView = ArchiveStore.ContentsHeld().Databases;
                string trenchText = trench.ToText(new ReportTextOptions { Detail = true });
                ArchiveStore.ReleaseContents();
                var trenchRow = report.Materials(lostPlugin).Rows.First(m => m.Path == @"materials\bh_outfit\bh_trench.mat");
                int heldByReport = ArchiveStore.ContentsHeld().Databases;
                log.Append($"bh_trench.mat, no database in memory: the view read {heldByView} whole, the report {heldByReport}\n");
                Check(heldByView >= 2 && heldByView == heldByReport, $"bh_trench.mat (its parent is nowhere): the view read {heldByView} databases whole looking for it, the report {heldByReport}");
                Check(trenchText.Contains($"the whole of {heldByView} compiled material databases", StringComparison.Ordinal), $"bh_trench.mat: the view's cost line doesn't say the {heldByView} databases it read whole");
                Check(trenchRow.NotFound.Count > 0 && trenchText.Contains("isn't in any file or in a database the game reads", StringComparison.Ordinal), "bh_trench.mat: the view doesn't say its parent is nowhere");
                parts.Add($"with no database in memory, the view reads the {heldByView} the game reads whole to look for a parent that is nowhere, as the report does");
            }
            parts.Add($"the single-material view lists the report's textures, and its missing ones, for each of {N(compared)} material files of {asked.Count} plugins ({N(viewInherited)} with an inherited texture); a part made from a parent file's shows what that gives; a shader model's name is no texture; the game's root materials are found");
        }
        // ---------------------------------------------------------------- A material only the game's own meshes name
        {
            // SFBGS050.esm's records name the game's orionrifle.nif, whose shaders name five materials nothing holds
            const string plugin = "SFBGS050.esm";
            if (File.Exists(Path.Combine(ctx.Data, plugin)))
            {
                var a = report.Materials(plugin);
                var lacks = a.Rows.Where(m => m.GameLacks).ToList();
                log.Append($"{plugin}: {a.Missing} missing, {lacks.Count} the game lacks: {string.Join(", ", lacks.Select(m => m.Path))}\n");
                Check(lacks.Count >= 5 && lacks.Any(m => m.Path == @"materials\weapons\orionrifle\orion_barrel.mat")
                    && lacks.All(m => !m.Missing && !m.HasProblem && !m.NotChecked && m.NoFile && m.Database == null && m.UsedBy.Count > 0 && m.UsedBy.All(u => u.Contains("(Starfield - ", StringComparison.Ordinal))),
                    $"{plugin}: {lacks.Count} materials are told as the game's gap ({string.Join(", ", lacks.Select(m => m.Path).Take(3))}); each must be named only by meshes of the game's archives, and not be missing");
                Check(a.Rows.Where(m => m.Missing).All(m => !m.OnlyGameMeshes), $"{plugin}: a material only the game's own meshes name is still called missing");
                var json = a.ToJson();
                var listed = json["materials"]!.AsArray().Where(m => (bool?)m!["gameLacks"] == true).ToList();
                Check((int?)json["counts"]?["materialsTheGameLacks"] == lacks.Count && listed.Count == lacks.Count && listed.All(m => m!["missing"] == null),
                    $"{plugin}: the structured form doesn't count and list the {lacks.Count} materials the game lacks");
                Check(a.Notes.Any(n => n.Contains("named only by the game's own meshes", StringComparison.Ordinal) && n.Contains("Not counted as missing", StringComparison.Ordinal)), $"{plugin}: no note tells the materials the game lacks");
                Check(a.ToText(new ReportTextOptions { Detail = true, MaxChars = 2_000_000 }).Contains("only the game's own meshes name it, and the game ships without it", StringComparison.Ordinal), $"{plugin}: the detailed text doesn't say where such a material is");
                parts.Add($"{plugin}: the {lacks.Count} materials only the game's own meshes name, in no file and no database, are told as the game's gap and not counted as missing ({a.Missing} missing)");
            }
            else parts.Add($"{plugin} isn't in Data: a material only the game's own meshes name wasn't checked");
        }
        ctx.WriteReport("materials-files.txt", log.ToString());
        return string.Join("; ", parts);
    }
}
