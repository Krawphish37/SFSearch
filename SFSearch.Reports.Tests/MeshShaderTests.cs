// Which strings of a mesh are its materials (GAPS S8, MaterialsMesh.cs): the ".mat" strings of the header that a
// shader of the mesh has for its name, not the ones left over from editing.
//  - A made-up mesh: three ".mat" strings; a shader names the second; a node's first number happens to be the third's
//    index. The second is the mesh's material; the first and the third are left over.
//  - The game's own archives ("Starfield - *.ba2"): every ".mat" string of every mesh is the name of a shader
//    property block. This is what the rule rests on: the game's own meshes never carry a material string no shader
//    names, so a string no shader names isn't how a mesh names a material.
//  - The mods' archives: how many ".mat" strings no shader names (the report used to list them, and call the ones
//    that are nowhere MISSING).
//  - A shader's name that isn't under "materials\": counted for the game's and the mods' meshes, with whether the
//    game's database holds it under "materials\".
//  - The reports: a mesh of Nomad_Spacesuit_Z.esm whose five shaders name five materials and whose string table
//    holds 27 more; the plugin's answer lists none of the 27, counts them, and says so in a note.
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class MeshShaderTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A mesh as Starfield writes one: the header, then the blocks, each given as its type and its bytes.</summary>
    static byte[] Mesh(string[] strings, params (string Type, byte[] Bytes)[] blocks)
    {
        var b = new List<byte>();
        void U16(int v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }
        void U32(uint v) { var x = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(x, v); b.AddRange(x); }
        void Short(string s) { b.Add((byte)s.Length); b.AddRange(Encoding.ASCII.GetBytes(s)); }
        void Long(string s) { U32((uint)s.Length); b.AddRange(Encoding.ASCII.GetBytes(s)); }
        b.AddRange(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7\n"));
        U32(0x14020007); b.Add(1); U32(12); U32((uint)blocks.Length); U32(172);
        Short("test"); U32(0); Short(""); Short("");
        var types = blocks.Select(k => k.Type).Distinct().ToList();
        U16(types.Count);
        foreach (var t in types) Long(t);
        foreach (var k in blocks) U16(types.IndexOf(k.Type));
        foreach (var k in blocks) U32((uint)k.Bytes.Length);
        U32((uint)strings.Length); U32((uint)strings.Max(s => s.Length));
        foreach (var s in strings) Long(s);
        U32(0);
        foreach (var k in blocks) b.AddRange(k.Bytes);
        return b.ToArray();
    }

    static byte[] Block(int first, int more = 8)
    {
        var x = new byte[4 + more];
        BinaryPrimitives.WriteInt32LittleEndian(x, first);
        return x;
    }

    public static string Shaders(Ctx ctx)
    {
        var parts = new List<string>();
        var log = new StringBuilder();

        // ---------------------------------------------------------------- A made-up mesh
        {
            var strings = new[] { @"Materials\Old\WasHere.mat", @"Materials\Weapons\X\Used.mat", @"materials/old/also.mat", "Scene Root" };
            var mesh = Mesh(strings, ("NiNode", Block(3)), ("BSGeometry", Block(2)), ("BSLightingShaderProperty", Block(1)));
            var names = MeshMaterialNames.Read(mesh);
            log.Append($"made-up mesh: materials {string.Join(", ", names.Texts)}; left over {string.Join(", ", names.Unused)}\n");
            Check(names.HeaderRead && names.Texts.SequenceEqual(new[] { strings[1] }) && names.Unused.SequenceEqual(new[] { strings[0], strings[2] }),
                $"the made-up mesh: materials {string.Join(", ", names.Texts)}; left over {string.Join(", ", names.Unused)} (a geometry whose first number is a string's index doesn't name it)");
            // Cut short inside its blocks: the blocks can't all be placed, so every string counts, as before
            var cut = MeshMaterialNames.Read(Mesh(strings, ("NiNode", Block(3)), ("BSLightingShaderProperty", Block(1))).AsSpan(0, 0).ToArray());
            Check(!cut.HeaderRead, "an empty file was read as a mesh");
            var none = MeshMaterialNames.Read(Mesh(strings, ("NiNode", Block(3))));
            Check(none.Texts.Count == 0 && none.Unused.Count == 3, $"a made-up mesh without a shader: materials {string.Join(", ", none.Texts)}");
            parts.Add("made-up mesh: the string its shader names is its material; the two no shader names are left over (one of them the first number of a geometry)");
        }

        // ---------------------------------------------------------------- Every mesh in the archives
        var store = ctx.Session.Archives;
        var gameDb = store.Database(Path.Combine(ctx.Data, "Starfield - Materials.ba2"), MaterialDatabase.GamePath);
        int gameMeshes = 0, gameStrings = 0, gameUnused = 0, modMeshes = 0, modStrings = 0, modUnused = 0, modMeshesUnused = 0, unread = 0, notPlaced = 0;
        var gameExamples = new List<string>();
        var shaderTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        var bare = new SortedDictionary<string, (int Count, string Example)>(StringComparer.Ordinal);
        var unusedBy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in store.AllInData())
        {
            string name = Path.GetFileName(archive);
            bool game = name.StartsWith("Starfield - ", StringComparison.OrdinalIgnoreCase);
            var info = store.Open(archive);
            if (info.Error != null) continue;
            foreach (var f in info.Files)
            {
                if (f.Extension != ".nif") continue;
                byte[] b;
                try { b = f.Bytes(); } catch (Exception e) when (e is IOException or InvalidDataException) { unread++; continue; }
                var h = MeshHeader.Read(b);
                if (h == null) { unread++; continue; }
                if (h.BlocksStart < 0 || h.Blocks.Count != h.BlockCount) { notPlaced++; continue; }
                if (game) gameMeshes++; else modMeshes++;
                var named = new Dictionary<int, string>();
                foreach (var (type, offset, size) in h.Blocks)
                    if (type.EndsWith("ShaderProperty", StringComparison.Ordinal) && size >= 4 && offset + 4 <= b.Length)
                        named[BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(offset))] = type;
                int unused = 0;
                for (int i = 0; i < h.Strings.Count; i++)
                {
                    string s = h.Strings[i].TrimEnd('\0').Trim();
                    if (!s.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) continue;
                    if (game) gameStrings++; else modStrings++;
                    if (named.TryGetValue(i, out var type))
                    {
                        shaderTypes[type] = shaderTypes.GetValueOrDefault(type) + 1;
                        string n = MaterialsReport.Norm(s);
                        if (!n.StartsWith(@"materials\", StringComparison.Ordinal))
                        {
                            string key = (game ? "the game's" : "the mods'") + " shader names not under materials\\, " + (gameDb.Has(@"materials\" + n) ? "in the game's database under materials\\" : "not in the game's database under materials\\");
                            var have = bare.GetValueOrDefault(key);
                            bare[key] = (have.Count + 1, have.Example ?? $"{s} ({f.Path}, {name})");
                        }
                    }
                    else
                    {
                        unused++;
                        if (game) { gameUnused++; if (gameExamples.Count < 10) gameExamples.Add($"{s} ({f.Path}, {name})"); }
                        else modUnused++;
                    }
                }
                if (unused > 0 && !game) { modMeshesUnused++; unusedBy[name] = unusedBy.GetValueOrDefault(name) + unused; }
            }
        }
        log.Append($"the game's archives: {N(gameMeshes)} meshes, {N(gameStrings)} .mat strings, {N(gameUnused)} the name of no shader\n");
        log.Append($"the mods' archives: {N(modMeshes)} meshes, {N(modStrings)} .mat strings, {N(modUnused)} the name of no shader, in {N(modMeshesUnused)} meshes; {N(unread)} meshes not read, {N(notPlaced)} whose blocks couldn't all be placed\n");
        foreach (var (type, n) in shaderTypes.OrderByDescending(k => k.Value)) log.Append($"  named by a {type}: {N(n)}\n");
        foreach (var (key, (n, example)) in bare) log.Append($"  {key}: {N(n)}, such as {example}\n");
        foreach (var e in gameExamples) log.Append("  the game's, the name of no shader: ").Append(e).Append('\n');
        foreach (var (a, n) in unusedBy.OrderByDescending(k => k.Value).Take(25)) log.Append($"  the name of no shader in {a}: {N(n)}\n");
        ctx.WriteReport("materials-shaders.txt", log.ToString());
        Check(gameMeshes >= 50_000 && gameStrings >= 100_000, $"only {N(gameMeshes)} meshes with {N(gameStrings)} .mat strings were read from the game's archives");
        Check(gameUnused == 0, $"{N(gameUnused)} .mat strings of the game's own meshes are the name of no shader, so the rule doesn't hold: {string.Join("; ", gameExamples.Take(3))}");
        parts.Add($"the game's archives: every one of the {N(gameStrings)} .mat strings of {N(gameMeshes)} meshes is a shader's name; the mods': {N(modUnused)} of {N(modStrings)} are the name of no shader ({N(modMeshesUnused)} meshes)");

        // ---------------------------------------------------------------- The reports
        const string plugin = "Nomad_Spacesuit_Z.esm";
        const string polearm = @"meshes\weapons\nomadweapons\nomad_energy_polearm_blue.nif";
        if (File.Exists(Path.Combine(ctx.Data, plugin)) && File.Exists(Path.Combine(ctx.Data, "Nomad_Spacesuit_Z - Main.ba2")))
        {
            var file = store.Open(Path.Combine(ctx.Data, "Nomad_Spacesuit_Z - Main.ba2")).Find(polearm);
            Check(file != null, $"{polearm} isn't in Nomad_Spacesuit_Z - Main.ba2");
            var names = MeshMaterialNames.Read(file!.Bytes());
            log.Append($"{polearm}: materials {string.Join(", ", names.Texts)}; {names.Unused.Count} left over: {string.Join(", ", names.Unused)}\n");
            Check(names.Texts.Count == 5 && names.Unused.Count >= 20 && names.Texts.Contains(@"Materials\E700\E700_Spacesuit\E700_weaponPart.mat") && names.Unused.Contains(@"Materials\Infiltrator_Blade\Infiltrator_Blade.mat"),
                $"{polearm}: {names.Texts.Count} materials ({string.Join(", ", names.Texts)}), {names.Unused.Count} left over");
            var a = new MaterialsReport(ctx.Session).Materials(plugin);
            var leftOver = names.Unused.Select(MaterialsReport.Norm).Where(u => !names.Texts.Select(MaterialsReport.Norm).Contains(u)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var listed = a.Rows.Where(m => leftOver.Contains(m.Path) && m.UsedBy.Any(u => u.StartsWith(polearm, StringComparison.OrdinalIgnoreCase))).Select(m => m.Path).ToList();
            Check(listed.Count == 0, $"{plugin}: names no shader of {polearm} uses are listed as its materials: {string.Join(", ", listed.Take(4))}");
            Check(a.Rows.All(m => m.Path != @"materials\infiltrator_blade\infiltrator_blade.mat" || !m.Missing), $"{plugin}: materials\\infiltrator_blade\\infiltrator_blade.mat, a name no shader uses, is called missing");
            Check(a.NamesUnused >= 100 && a.MeshesWithUnused >= 20 && (int?)a.ToJson()["counts"]?["namesNoShaderUses"] == a.NamesUnused, $"{plugin}: {a.NamesUnused} names no shader uses in {a.MeshesWithUnused} meshes are counted");
            Check(a.Notes.Any(n => n.Contains("the name of no shader of the mesh", StringComparison.Ordinal)), $"{plugin}: no note says that names no shader uses were left out");
            log.Append($"{plugin}: {a.Rows.Count} materials, {a.Missing} missing; {a.NamesUnused} names no shader uses in {a.MeshesWithUnused} meshes\n");
            parts.Add($"{plugin}: {polearm} has 5 materials (its shaders' names) and {names.Unused.Count} names left over; the answer lists none of those, counts {N(a.NamesUnused)} in {N(a.MeshesWithUnused)} meshes and says so");
        }
        else parts.Add($"{plugin} isn't in Data: its mesh with names left over wasn't checked");
        ctx.WriteReport("materials-shaders.txt", log.ToString());
        return string.Join("; ", parts);
    }
}
