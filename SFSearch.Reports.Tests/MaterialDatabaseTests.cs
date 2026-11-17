// The compiled material databases (materialsbeta.cdb) and what the materials report makes of them (GAPS.md S8).
//  - The game's database is read twice, two ways: the reader's object list, and, here in the test, the build's own
//    list of compiled files (the hash map of BSMaterial::Internal::CompiledDB, which the reader doesn't use). The two
//    must hold the same materials.
//  - Every database in the Data folder reads without an error, and only as far as its material list.
//  - A database cut short, and a file that isn't one, give an error, not an exception or an empty list.
//  - The report on real mods: atom74.esm (its one material that is no file is the game's), aa50_longwatch.esm (42
//    in the game's database, laser_ir_glow.mat in none), kgcdoom.esm (its own database, the game's, and 8 in none),
//    ProjectRework-Akila.esm (a material its records name, in the game's database).
//  - The whole databases (fix-f4r): the game's read to its last byte, its 48,749 materials' textures (nearly all in
//    the game's own archives), one material's whole list, every database in Data, the rule of which ones the game
//    reads, a database cut short, the databases kept for the process (a new store finds them), and the report:
//    aa50_longwatch.esm, kgcdoom.esm and darkstar.esm with the textures of their compiled materials.
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class MaterialDatabaseTests
{
    /// <summary>Every material's textures together: how many materials, and the textures they name (was
    /// MaterialContents.AllMaterialTextures in the library, kept there for these tests until J37).</summary>
    static (int Materials, HashSet<string> Textures, int Unresolved) AllMaterialTextures(MaterialContents contents, MaterialContents? game)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int materials = 0, unresolved = 0;
        for (int i = 0; i < contents.Objects; i++)
        {
            var n = new MaterialContents.Node(contents, i);
            if (!n.IsMaterial) continue;
            materials++;
            all.UnionWith(contents.TexturesOf(n, game, out int lost));
            unresolved += lost;
        }
        return (materials, all, unresolved);
    }

    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    static string MB(long n) => (n / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>The CompiledDB's hash map keys (file, ext, dir) of a whole database, read the plain way.</summary>
    static List<(uint File, uint Ext, uint Dir)> HashMapKeys(byte[] b)
    {
        int pos = 16;
        Check(Encoding.ASCII.GetString(b, 0, 4) == "BETH", "the game's database doesn't start with BETH");
        byte[]? table = null;
        bool afterCompiledDb = false;
        string Str(uint at) { int end = Array.IndexOf(table!, (byte)0, (int)at); return Encoding.ASCII.GetString(table!, (int)at, end - (int)at); }
        while (pos + 8 <= b.Length)
        {
            string type = Encoding.ASCII.GetString(b, pos, 4);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(pos + 4));
            var d = b.AsSpan(pos + 8, size);
            if (type == "STRT") table = d.ToArray();
            else if (type == "OBJT" && Str(BinaryPrimitives.ReadUInt32LittleEndian(d)) == "BSMaterial::Internal::CompiledDB") afterCompiledDb = true;
            else if (type == "MAPC" && afterCompiledDb)
            {
                // key class, value type, count, then count pairs of a BSResource::ID (12 bytes) and a uint64
                Check(Str(BinaryPrimitives.ReadUInt32LittleEndian(d)) == "BSResource::ID", "the CompiledDB's hash map isn't keyed by BSResource::ID");
                int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[8..]);
                Check(size == 12 + n * 20, $"the hash map holds {size} bytes for {n} entries");
                var keys = new List<(uint, uint, uint)>(n);
                for (int i = 0; i < n; i++)
                {
                    var e = d.Slice(12 + i * 20);
                    keys.Add((BinaryPrimitives.ReadUInt32LittleEndian(e), BinaryPrimitives.ReadUInt32LittleEndian(e[4..]), BinaryPrimitives.ReadUInt32LittleEndian(e[8..])));
                }
                return keys;
            }
            pos += 8 + size;
        }
        throw new TestFailed("the game's database has no CompiledDB hash map");
    }

    public static string Databases(Ctx ctx)
    {
        var parts = new List<string>();
        var log = new StringBuilder();
        const string gamePath = @"materials\materialsbeta.cdb";
        string gameArchive = Path.Combine(ctx.Data, "Starfield - Materials.ba2");
        var file = Mutagen.Bethesda.Archives.Archive.CreateReader(Mutagen.Bethesda.GameRelease.Starfield, gameArchive).Files
            .First(f => f.Path.ToString().Replace('/', '\\').Equals(gamePath, StringComparison.OrdinalIgnoreCase));

        // The reader, on a stream of the archive entry, as the report reads it
        MaterialDatabase game;
        using (var s = file.AsStream()) game = MaterialDatabase.Read(s, file.Size, gamePath, "Starfield - Materials.ba2", gameArchive);
        Check(game.Error == null, $"the game's database couldn't be read: {game.Error}");
        Check(game.IsGame && game.Count > 40_000 && game.BuildVersion != null, $"the game's database: {game.Count} materials, build {game.BuildVersion}");
        Check(game.BytesRead < game.Size / 2, $"the reader read {game.BytesRead} of {game.Size} bytes: it should stop after the material list");
        parts.Add($"the game's database: {N(game.Count)} materials (build {game.BuildVersion}, {game.References} entries without data), {MB(game.BytesRead)} of {MB(game.Size)} read in {game.Seconds:0.00} s, {MB(game.Allocated)} allocated");

        // The other way: the whole file, and the build's hash map of compiled files
        var keys = HashMapKeys(file.GetBytes());
        Check(keys.All(k => k.Ext == 0x0074616D), "the hash map holds a key whose extension isn't mat");
        int inBoth = keys.Count(k => game.Has(k.File, k.Dir));
        Check(keys.Count == game.Count && inBoth == keys.Count, $"the hash map has {keys.Count} materials, the object list {game.Count}, {inBoth} in both");
        parts.Add($"its CompiledDB hash map holds the same {N(keys.Count)} materials");

        // A path is looked up by the hashes of its folder and name, in any letter case and either slash
        Check(game.Has(@"materials\water\waterchoppylarge.mat") && game.Has("Materials/Water/WaterChoppyLarge.MAT"),
            "the lookup of materials\\water\\waterchoppylarge.mat by path, case and slash");
        Check(!game.Has(@"materials\weapons\scopereticles\laser_ir_glow.mat") && !game.Has(@"materials\water\waterchoppylarge.nif"), "a path the game's database must not hold");

        // Every database in Data, through the report's store
        var store = ctx.Session.Archives;
        int dbs = 0; long read = 0, size = 0, allocated = 0; double seconds = 0;
        foreach (var archive in store.AllInData())
            foreach (var cdb in store.Names(archive).Databases)
            {
                var db = store.Database(archive, cdb);
                Check(db.Error == null && db.Count > 0 && db.BytesRead <= db.Size, $"{cdb} in {Path.GetFileName(archive)}: {db.Error ?? $"{db.Count} materials, {db.BytesRead} of {db.Size} bytes"}");
                dbs++; read += db.BytesRead; size += db.Size; allocated += db.Allocated; seconds += db.Seconds;
                log.Append($"{Path.GetFileName(archive)}\t{cdb}\t{db.Count}\tbuild {db.BuildVersion}\t{db.References} without data\t{db.BytesRead} of {db.Size} bytes\t{db.Seconds:0.000} s\t{db.Allocated} allocated\n");
            }
        Check(dbs > 0, "no compiled material database was found in the archives of Data");
        parts.Add($"{dbs} databases in Data read without an error: {MB(read)} of {MB(size)} in {seconds:0.00} s, {MB(allocated)} allocated");

        // A database cut short, and a file that isn't one
        var whole = file.GetBytes();
        var cut = MaterialDatabase.Read(new MemoryStream(whole, 0, 400_000), 400_000, "cut.cdb", "test", "test");
        Check(cut.Error != null && cut.Count == 0, $"a database cut short read as {cut.Count} materials with no error");
        var notOne = MaterialDatabase.Read(new MemoryStream(Encoding.ASCII.GetBytes("{\"Objects\": []}")), 15, "x.cdb", "test", "test");
        Check(notOne.Error != null && notOne.Error.Contains("BETH", StringComparison.Ordinal), $"a file that isn't a database: {notOne.Error ?? "no error"}");
        parts.Add($"a database cut short and a file that isn't one each give an error (\"{cut.Error}\")");

        // The report
        var report = new MaterialsReport(ctx.Session);
        var atom = report.Materials("atom74.esm");
        Check(atom.Rows.Count(m => m.NoFile) >= 1 && atom.Rows.Where(m => m.NoFile).All(m => m.Compiled && m.Database!.IsGame) && atom.Missing == 0 && !atom.Failed,
            $"atom74.esm: {atom.Rows.Count(m => m.NoFile)} materials that are no file, {atom.CompiledInGame} in the game's database, {atom.Missing} missing");
        parts.Add($"atom74.esm: its {atom.Compiled} material that is no file is in the game's database ({atom.Rows.First(m => m.NoFile).Path}), nothing missing");

        var aa50 = report.Materials("aa50_longwatch.esm");
        var laser = aa50.Rows.Single(m => m.Path == @"materials\weapons\scopereticles\laser_ir_glow.mat");
        Check(laser.Missing && aa50.Missing == 1 && aa50.Compiled == aa50.CompiledInGame && aa50.Compiled >= 40,
            $"aa50_longwatch.esm: {aa50.Compiled} compiled ({aa50.CompiledInGame} the game's), {aa50.Missing} missing (laser_ir_glow.mat missing: {laser.Missing})");
        Check(aa50.DatabasesRead.Count == dbs && aa50.DatabasesRead.All(d => !d.Db.Has(laser.Path)), "aa50_longwatch.esm: not every database was looked in for the missing material");
        string text = aa50.ToText(new ReportTextOptions());
        Check(text.Contains("laser_ir_glow.mat | MISSING", StringComparison.Ordinal) && text.Contains("[cost]", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.IsMatch(text, @"the material lists of \d+ compiled material databases \((the game's: [\d.]+ MB of [\d.]+ MB in [\d.]+ s, [\d.]+ (KB|MB) allocated|.*taken from memory)"),
            "aa50_longwatch.esm: the text doesn't say MISSING, or its cost doesn't say what reading the databases cost (or that they were taken from memory)");
        parts.Add($"aa50_longwatch.esm: {aa50.CompiledInGame} in the game's database, laser_ir_glow.mat in none of the {dbs} (MISSING)");

        var kgc = report.Materials("kgcdoom.esm");
        int own = kgc.Rows.Count(m => m.Compiled && m.DatabaseOrigin == FileOrigin.Own);
        Check(own > 0 && kgc.CompiledInGame > 0 && kgc.Missing > 0 && kgc.Rows.Where(m => m.Compiled && m.DatabaseOrigin == FileOrigin.Own).All(m => m.Database!.Path.Contains("kgcdoom", StringComparison.OrdinalIgnoreCase)),
            $"kgcdoom.esm: {own} in its own database, {kgc.CompiledInGame} in the game's, {kgc.Missing} missing");
        parts.Add($"kgcdoom.esm: {own} in its own database, {kgc.CompiledInGame} in the game's, {kgc.Missing} missing");

        var akila = report.Materials("ProjectRework-Akila.esm");
        var water = akila.Rows.SingleOrDefault(m => m.NamedBy.Count > 0);
        Check(water != null && water.Compiled && water.Database!.IsGame && water.UsedBy.Count == 0, $"ProjectRework-Akila.esm: the material its records name: {water?.Path ?? "none"}");
        parts.Add($"ProjectRework-Akila.esm: {water!.Path}, named by {water.NamedBy[0]}, in the game's database");

        Textures(ctx, file, game, gamePath, gameArchive, parts, log);
        Parts(ctx, gamePath, gameArchive, parts, log);

        ctx.WriteReport("materials-cdb.txt", log.ToString());
        return string.Join("; ", parts) + ".";
    }

    /// <summary>The rest of the databases (fix-f4r): read whole, each material's textures, and what the report makes of them.</summary>
    static void Textures(Ctx ctx, Mutagen.Bethesda.Archives.IArchiveFile file, MaterialDatabase game, string gamePath, string gameArchive, List<string> parts, StringBuilder log)
    {
        var store = ctx.Session.Archives;

        // The game's database read whole, on a stream of the archive entry: every chunk to its last byte
        MaterialContents whole;
        using (var s = new BufferedStream(file.AsStream(), 1 << 20)) whole = MaterialContents.Read(s, file.Size);
        Check(whole.BytesRead == whole.Size && whole.Objects > 400_000 && whole.Components > 1_000_000,
            $"the game's database read whole: {whole.BytesRead} of {whole.Size} bytes, {whole.Objects} objects, {whole.Components} components");
        var (materials, textures, lost) = AllMaterialTextures(whole, whole);
        Check(materials == game.Count, $"the whole read finds {materials} materials, the material list {game.Count}");
        // Each texture a game material names is looked for in the game's own archives: nearly all are there
        var gameNames = store.AllInData().Where(a => Path.GetFileName(a).StartsWith("Starfield - ", StringComparison.OrdinalIgnoreCase)).Select(a => store.Names(a)).ToList();
        int inGame = textures.Count(t => { string n = ArchiveStore.DataRelative(t).ToLowerInvariant(); return gameNames.Any(a => a.Has(n)); });
        Check(textures.Count > 80_000 && inGame >= textures.Count * 0.99, $"the game's {N(materials)} materials name {N(textures.Count)} textures, {N(inGame)} of them in the game's archives");
        parts.Add($"the game's database read whole: {N(whole.Objects)} objects, {N(whole.Components)} components, {MB(whole.BytesRead)} in {whole.Seconds:0.00} s, {MB(whole.Allocated)} allocated, {MB(whole.Kept)} kept; " +
            $"its {N(materials)} materials name {N(textures.Count)} textures, {N(inGame)} in the game's archives ({lost} links lead nowhere)");
        log.Append($"game\twhole\t{whole.Objects} objects\t{whole.Components} components\t{whole.Seconds:0.000} s\t{whole.Allocated} allocated\t{whole.Kept} kept\t{materials} materials\t{textures.Count} textures\t{inGame} in game archives\t{lost} unresolved\n");
        foreach (var t in textures.Where(t => { string n = ArchiveStore.DataRelative(t).ToLowerInvariant(); return !gameNames.Any(a => a.Has(n)); }).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).Take(50))
            log.Append($"game\tnot in the game's archives\t{t}\n");

        // One material, the whole list: the water of ProjectRework-Akila's biome
        var water = whole.TexturesOf(@"Materials/Water/WaterChoppyLarge.MAT", whole, out int waterLost) ?? new List<string>();
        Check(water.Count == 8 && waterLost == 0 && water.Contains(@"Data\Textures\water\FoamDefault_color.dds") && water.Contains(@"Data\Textures\water\WavesChurn_height.dds"),
            $"materials\\water\\waterchoppylarge.mat names {water.Count} textures: {string.Join(", ", water)}");
        Check(whole.TexturesOf(@"materials\weapons\scopereticles\laser_ir_glow.mat", whole, out _) == null, "a material the database doesn't hold has textures");

        // Every database in Data read whole, through the store (once per process)
        int dbs = 0, mods = 0, modMaterials = 0, modTextures = 0, modLost = 0; double seconds = 0;
        foreach (var archive in store.AllInData())
            foreach (var cdb in store.Names(archive).Databases)
            {
                var db = store.Database(archive, cdb);
                var c = store.Contents(db);
                Check(c != null && db.ContentsError == null && c.BytesRead == c.Size, $"{cdb} in {Path.GetFileName(archive)} read whole: {db.ContentsError ?? $"{c?.BytesRead} of {c?.Size} bytes"}");
                dbs++; seconds += c!.Seconds;
                Check(MaterialDatabase.GameReads(cdb, archive), $"{cdb} in {Path.GetFileName(archive)}: a database the game doesn't read, by the rule");
                if (db.IsGame) continue;
                var (m, t, l) = AllMaterialTextures(c, store.Contents(store.Database(gameArchive, gamePath)));
                Check(m == db.Count, $"{cdb}: the whole read finds {m} materials, the list {db.Count}");
                mods++; modMaterials += m; modTextures += t.Count; modLost += l;
                log.Append($"{Path.GetFileName(archive)}\t{cdb}\twhole\t{c.Objects} objects\t{c.Seconds:0.000} s\t{c.Allocated} allocated\t{c.Kept} kept\t{m} materials\t{t.Count} textures\t{l} unresolved\n");
            }
        parts.Add($"all {dbs} databases read whole without an error in {seconds:0.00} s; the {mods} mods' {N(modMaterials)} materials name {N(modTextures)} textures ({modLost} links lead nowhere)");
        Check(!MaterialDatabase.GameReads(@"materials\x\materialsbeta.cdb", null) && !MaterialDatabase.GameReads(@"materials\x\materialsbeta.cdb", @"C:\x\mod - textures.ba2")
            && MaterialDatabase.GameReads(@"materials\kgcship\materialsbeta.cdb", @"C:\x\kinggathcreations_spaceship - main.ba2") && MaterialDatabase.GameReads(@"materials\x\a.cdb", @"C:\x\ShatteredSpace - Main01.ba2"),
            "the rule of the databases the game reads: the game's own and any .cdb in a \" - Main\" archive, not loose, not in another archive");

        // Cut short: an error, not an exception the report sees
        var bytes = file.GetBytes();
        var cut = MaterialDatabase.Read(new MemoryStream(bytes), bytes.Length, "cut.cdb", "test", "test");
        cut.LoadContents(() => (new MemoryStream(bytes, 0, 30_000_000), 30_000_000));
        Check(cut.Contents == null && cut.ContentsError != null && cut.ContentsError.Contains("cut short", StringComparison.Ordinal), $"a database cut short read whole: {cut.ContentsError ?? "no error"}");

        // Kept for the process: a new store (a new report session in the service) finds the same database, read
        var other = new ArchiveStore(ctx.Data);
        var again = other.Database(gameArchive, gamePath);
        Check(ReferenceEquals(again, store.Database(gameArchive, gamePath)) && again.Contents != null && other.DatabasesRead.Count == 0 && other.Contents(again) == again.Contents && other.ContentsRead.Count == 0,
            "a new store read the game's database again: it should be kept for the process");
        parts.Add("a new store finds the databases and their contents in memory");

        // The report
        var report = new MaterialsReport(ctx.Session);
        var aa50 = report.Materials("aa50_longwatch.esm");
        var aaCompiled = aa50.Rows.Where(m => m.Compiled).ToList();
        Check(aaCompiled.Count >= 40 && aaCompiled.All(m => m.TexturesRead && m.Textures.Count > 0 && !m.MissingTextures.Any()) && aa50.CompiledTextures >= 200,
            $"aa50_longwatch.esm: {aaCompiled.Count(m => m.TexturesRead)} of {aaCompiled.Count} compiled materials have their textures read, {aa50.CompiledTextures} textures, {aaCompiled.Sum(m => m.MissingTextures.Count())} missing");
        Check(aa50.MissingTextures == 3 && aa50.GameLacks >= 1 && aa50.Rows.SelectMany(m => m.Textures).Where(t => t.GameLacks).All(t => t.Path == @"textures\weapons\ar99\ar99_reflex_curvature.dds"),
            $"aa50_longwatch.esm: {aa50.MissingTextures} missing textures (3 expected, those of its .mat files), {aa50.GameLacks} the game's materials lack");
        string text = aa50.ToText(new ReportTextOptions());
        Check(text.Contains("by compiled materials)", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(text, @"the file names of \d+ archives")
            && System.Text.RegularExpressions.Regex.IsMatch(text, @"the whole of \d+ compiled material databases?, for the textures of \d+ compiled materials \("),
            "aa50_longwatch.esm: the text doesn't count the compiled materials' textures, or its cost doesn't say the archives' names and the whole databases read");
        parts.Add($"aa50_longwatch.esm: its {aaCompiled.Count} compiled materials name {N(aa50.CompiledTextures)} textures, none missing (ar99_reflex_curvature.dds, which one of the game's materials names, is in no archive: listed as the game's gap, not missing)");

        var kgc = report.Materials("kgcdoom.esm");
        var own = kgc.Rows.Where(m => m.Compiled && m.DatabaseOrigin == FileOrigin.Own).ToList();
        Check(own.Count > 0 && own.All(m => m.TexturesRead && m.Textures.Count > 0) && kgc.Rows.Where(m => m.Compiled).All(m => m.TexturesRead) && kgc.MissingTextures == 0,
            $"kgcdoom.esm: {own.Count(m => m.TexturesRead)} of its {own.Count} own compiled materials have their textures read; {kgc.MissingTextures} missing");
        Check(own.SelectMany(m => m.Textures).Any(t => t.Origin == FileOrigin.Own), "kgcdoom.esm: none of its own compiled materials' textures is in its own archives");
        parts.Add($"kgcdoom.esm: its {own.Count} materials in its own database name {N(own.Sum(m => m.Textures.Count))} textures ({N(own.Sum(m => m.Textures.Count(t => t.Origin == FileOrigin.Own)))} in its own archives), {N(kgc.CompiledTextures)} for its {kgc.Compiled} compiled, none missing");

        var ds = report.Materials("darkstar.esm");
        Check(ds.Rows.Where(m => m.Compiled).All(m => m.TexturesRead) && ds.CompiledTextures > 0, $"darkstar.esm: {ds.Rows.Count(m => m.TexturesRead)} of {ds.Compiled} compiled materials have their textures read");
        parts.Add($"darkstar.esm: {ds.Compiled} compiled materials name {N(ds.CompiledTextures)} textures, {N(ds.Rows.Where(m => m.Compiled).Sum(m => m.MissingTextures.Count()))} missing");

        // A second answer in the same process reads no database again
        var aa50Again = report.Materials("aa50_longwatch.esm");
        string again2 = aa50Again.ToText(new ReportTextOptions());
        Check(store.ContentsRead.Count == 0 && again2.Contains("taken from memory)", StringComparison.Ordinal) && again2.Contains("from memory)", StringComparison.Ordinal),
            "aa50_longwatch.esm asked again: a database or an archive's names were read again");
        log.Append(text).Append('\n').Append(again2).Append('\n');
    }

    /// <summary>One material's parts, and the materials that use a texture (fix-f10, GAPS S33): materials2 --material
    /// and --texture.</summary>
    /// <summary>S33f: the slot table (MaterialsView.Slots) derived again from the game's own code, the enum
    /// BSMaterial::MRTextureChannel that Starfield.exe registers, when the game folder holds Starfield.exe (and the
    /// Creation Kit's, when it is there too). Its 21 values must be Slots in order, then Count.</summary>
    internal static void SlotsFromExe(Ctx ctx, List<string> parts, StringBuilder log)
    {
        string game = Path.GetDirectoryName(ctx.Data.TrimEnd('\\', '/'))!;
        foreach (var exeName in new[] { "Starfield.exe", "CreationKit.exe" })
        {
            string exe = Path.Combine(game, exeName);
            if (!File.Exists(exe)) { parts.Add($"S33f: {exeName} not in {game}: its slot table not derived"); continue; }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var found = ExeEnums.Read(exe, "BSMaterial::MRTextureChannel");
            Check(found.Count == 1, $"S33f: {exeName} registers BSMaterial::MRTextureChannel {found.Count} times in the shape read");
            var e = found[0].Entries;
            string table = string.Join(", ", e.Select((x, i) => $"{(x.Value ?? i)} {x.Name}"));
            log.Append($"S33f: {exeName}, BSMaterial::MRTextureChannel registered by the code at RVA 0x{found[0].CodeRva:x}: {table}\n");
            Check(e.Count == MaterialsView.Slots.Length + 1 && e[^1].Name == "Count" && e.Select(x => x.Name).Take(MaterialsView.Slots.Length).SequenceEqual(MaterialsView.Slots)
                && e.Select((x, i) => x.Value == null || x.Value == i).All(ok => ok) && e.Count(x => x.Value != null) >= e.Count - 2,
                $"S33f: {exeName}'s BSMaterial::MRTextureChannel isn't MaterialsView.Slots then Count: {table}");
            parts.Add($"S33f: {exeName} (code at RVA 0x{found[0].CodeRva:x}) registers BSMaterial::MRTextureChannel as MaterialsView.Slots, then Count {e[^1].Value}; {e.Count(x => x.Value != null)} of {e.Count} values read as numbers, in {clock.Elapsed.TotalSeconds:0.0} s");
        }
    }

    /// <summary>S8 (j811): the rule of the databases the game reads (MaterialDatabase.GameReads), held against the
    /// game's own code: the file listener for materials (ExeListener) keeps the extension ".cdb" and, for each archive
    /// opened, matches its name against the pattern GameReads uses.</summary>
    internal static void RuleFromExe(Ctx ctx, List<string> parts, StringBuilder log)
    {
        const string pattern = @".* - Main[0-9]*\.ba2";
        string game = Path.GetDirectoryName(ctx.Data.TrimEnd('\\', '/'))!;
        foreach (var (exeName, cls) in new[] { ("Starfield.exe", "MaterialAndParticleFileListener"), ("CreationKit.exe", "MaterialFileListener") })
        {
            string exe = Path.Combine(game, exeName);
            if (!File.Exists(exe)) { parts.Add($"S8: {exeName} not in {game}: the database rule not read from its code"); continue; }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var l = ExeListener.Read(exe, cls);
            Check(l != null, $"S8: {exeName} has no class {cls} with a vtable");
            var ctor = l!.Constructors.Where(c => c.Strings.Contains(".cdb")).ToList();
            log.Append($"S8: {exeName}: {l.ClassName}, vtable at RVA 0x{l.VtableRva:x}, method 1 at 0x{l.MethodRva:x} loads {string.Join(", ", l.MethodStrings)}; the code that stores the vtable: {string.Join("; ", l.Constructors.Select(c => $"0x{c.Rva:x} loads {string.Join(", ", c.Strings)}"))}\n");
            Check(l.MethodStrings.SequenceEqual(new[] { pattern }) && ctor.Count == 1,
                $"S8: {exeName}'s {l.ClassName}: method 1 loads {string.Join(", ", l.MethodStrings)} (expected only {pattern}); {ctor.Count} constructors load \".cdb\"");
            parts.Add($"S8: {exeName}'s {cls} keeps \".cdb\" (its constructor at RVA 0x{ctor[0].Rva:x}{(ctor[0].Strings.Contains(".blob") ? ", with \".blob\"" : "")}) and matches each archive's name against {pattern} (method 1 at 0x{l.MethodRva:x}), in {clock.Elapsed.TotalSeconds:0.0} s");
        }
        // GameReads is that pattern, whole and case ignored (std::regex flag 0x100), with the game's own database by its path
        Check(MaterialDatabase.GameReads(@"materials\a\b.cdb", @"C:\x\Mod - MAIN.ba2") && MaterialDatabase.GameReads(@"x\y.cdb", @"C:\x\Mod - Main12.ba2")
            && !MaterialDatabase.GameReads(@"materials\a\b.cdb", @"C:\x\Mod - Main.ba2.bak") && !MaterialDatabase.GameReads(@"materials\a\b.cdb", @"C:\x\Mod - MainA.ba2")
            && !MaterialDatabase.GameReads(@"materials\a\b.cdb", @"C:\x\Mod - Textures.ba2") && !MaterialDatabase.GameReads(@"materials\a\b.cdb", null),
            "S8: GameReads doesn't follow the pattern the game's code matches");
    }

    /// <summary>The game's materials with a path: without the plugins' records, and with everything the gather reads.</summary>
    const int Told0 = 47_983, Told = 48_644;

    static void Parts(Ctx ctx, string gamePath, string gameArchive, List<string> parts, StringBuilder log)
    {
        var store = ctx.Session.Archives;
        var view = new MaterialsView(ctx.Session);
        static List<PartTexture> Tex(MaterialPart t) => t.All().SelectMany(p => p.Textures).Where(x => x.Path.Length > 0).ToList();
        var gameDb = store.Database(gameArchive, gamePath);
        var gameC = store.Contents(gameDb)!;

        // The game's: its 8 textures, as the materials test finds them, in a tree with names; its parent's path
        var water = view.Material(@"materials\water\waterchoppylarge.mat");
        Check(water.FileWhere == null && water.CompiledIn.Any(c => c.Db.IsGame && c.GameReads) && water.Trees.Count == water.CompiledIn.Count,
            $"waterchoppylarge.mat: file {water.FileWhere}, in {water.CompiledIn.Count} databases, {water.Trees.Count} trees");
        var wt = water.Trees.First(t => t.Source.StartsWith("the game's", StringComparison.Ordinal)).Tree;
        var expected = gameC.TexturesOf(@"materials\water\waterchoppylarge.mat", gameC, out _)!.Select(MaterialsReport.Norm).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shown = Tex(wt).Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check(shown.Count == 8 && shown.SetEquals(expected) && Tex(wt).All(t => !t.Missing), $"waterchoppylarge.mat's tree names {shown.Count} textures ({string.Join(", ", shown)}), the database's walk {expected.Count}");
        var foam = wt.Parts.FirstOrDefault(p => p.Kind == "layer" && p.Name == "Foam");
        Check(wt.Name == "WaterChoppyLarge" && wt.ParentPath == @"materials\water\waterplaceholder.mat" && wt.Parts.Count(p => p.Kind == "layer") == 5 && wt.Parts.Count(p => p.Kind == "blender") == 3
            && foam != null && foam.All().SelectMany(p => p.Textures).Any(t => t.Slot == "Albedo (slot 0)" && t.Path == @"textures\water\foamdefault_color.dds" && t.Where == "Starfield - Textures10.ba2"),
            $"waterchoppylarge.mat's tree: name {wt.Name}, parent {wt.ParentPath}, {wt.Parts.Count(p => p.Kind == "layer")} layers; the Foam layer's albedo");
        // A part reached twice is shown whole the first time it is printed
        var printed = water.ToText(new ReportTextOptions { MaxChars = int.MaxValue });
        int firstB = printed.IndexOf("UV stream: Water_UVStreamB\n", StringComparison.Ordinal), aboveB = printed.IndexOf("UV stream: Water_UVStreamB (shown above)", StringComparison.Ordinal);
        Check(firstB >= 0 && (aboveB < 0 || firstB < aboveB) && printed.Contains("- Material WaterChoppyLarge, made from WaterPlaceholder (materials\\water\\waterplaceholder.mat)", StringComparison.Ordinal),
            "waterchoppylarge.mat's text: a part said to be shown above isn't, or the material's line");
        parts.Add($"--material waterchoppylarge.mat: in {water.CompiledIn.Count} databases ({string.Join(", ", water.CompiledIn.Select(c => c.Db.Where))}); the game's tree: 5 layers, 3 blenders, its 8 textures, made from waterplaceholder.mat");
        log.Append(printed).Append('\n');

        // A slot the Creation Kit names otherwise (to-do F30): OverlayRoughness (slot 15) is "Overlay Roughness" in the
        // CK, shown beside the engine's name (every schema has the CK's names); Albedo has no name of its own
        var helmet = view.Material(@"materials\clothes\outfit_gang_strikers_01\outfit_gang_strikers_helmet_01.mat");
        var overlay = helmet.Trees.SelectMany(t => Tex(t.Tree)).Where(t => t.Slot == "OverlayRoughness (slot 15)").ToList();
        Check(ctx.Session.Schema.Model.Ck.Present, "the schema has no Creation Kit names: every build reads them from SFSearch.SchemaGen\\inputs\\ck-names-*.tsv");
        string helmetText = helmet.ToText(new ReportTextOptions { MaxChars = int.MaxValue });
        Check(overlay.Count > 0 && overlay.All(t => t.CkName == "Overlay Roughness")
            && helmetText.Contains("OverlayRoughness (slot 15) (CK: \"Overlay Roughness\"): ", StringComparison.Ordinal)
            && helmet.Trees.SelectMany(t => Tex(t.Tree)).Where(t => t.Slot == "Albedo (slot 0)").All(t => t.CkName == null),
            $"outfit_gang_strikers_helmet_01.mat: {overlay.Count} OverlayRoughness textures, CK names {string.Join(", ", overlay.Select(t => t.CkName ?? "none"))}");
        parts.Add($"--material outfit_gang_strikers_helmet_01.mat: {overlay.Count} OverlayRoughness (slot 15) textures, named \"Overlay Roughness\" by the Creation Kit");

        // A mod's database: kgcdoom's material, made from one of the game's
        var arms = view.Material(@"materials\clothes\kgcdoom\praetor\spacesuit_doom_praetor_arms01.mat");
        var at = arms.Trees.SingleOrDefault().Tree;
        Check(arms.CompiledIn.Count == 1 && !arms.CompiledIn[0].Db.IsGame && arms.CompiledIn[0].Db.Where.StartsWith("kgcdoom", StringComparison.OrdinalIgnoreCase) && arms.CompiledIn[0].GameReads && at != null,
            $"kgcdoom's arms material: {string.Join(", ", arms.CompiledIn.Select(c => c.Db.Shown))}");
        Check(Tex(at!).Count == 5 && Tex(at!).All(t => t.Where == "kgcdoom - textures.ba2") && at!.ParentPath == @"materials\layered\shadermodels\1layerstandard.mat",
            $"kgcdoom's arms material: {Tex(at!).Count} textures ({string.Join(", ", Tex(at!).Select(t => t.Where))}), made from {at!.ParentPath}");
        parts.Add("--material kgcdoom's arms01.mat: its own database, 5 textures in kgcdoom - textures.ba2, made from the game's 1layerstandard.mat");
        log.Append(arms.ToText(new ReportTextOptions { MaxChars = int.MaxValue })).Append('\n');

        // A loose .mat file: shown from the file, linked by IDs, made from the game's objects
        var glass = view.Material("Materials/Weapons/Bridger/ReflexGlass.mat");
        var gt = glass.Trees.FirstOrDefault(t => t.Source.StartsWith("the .mat file", StringComparison.Ordinal)).Tree;
        Check(glass.FileWhere == "loose" && gt != null && gt.Name == "ReflexGlass" && gt.ParentPath == @"materials\layered\shadermodels\1layereffectglass.mat"
            && gt.All().Any(p => p.Kind == "texture set" && p.Name == "ReflexGlass_TextureSet1" && p.Parent == "1LayerEffectGlass_TextureSet1" && !p.ParentLost),
            $"the loose ReflexGlass.mat: file {glass.FileWhere}, tree {gt?.Name}, parent {gt?.ParentPath}");
        // One in a mod's archive, with what it takes from its parent marked
        var laser = view.Material(@"materials\weapons\scopereticles\laser_green_glow.mat", "aa50_longwatch.esm");
        var lt = laser.Trees.FirstOrDefault().Tree;
        Check(laser.FileWhere == "aa50_longwatch - main.ba2" && laser.CompiledIn.Count == 0 && lt != null && lt.ParentPath == @"materials\weapons\beowulf\reticle_beowulf_1.mat"
            && lt.All().SelectMany(p => p.Textures).Any(t => t.Inherited) && lt.Textures.Any(t => t.Path == @"textures\effects\powerpersonalatmospherelines_flow.dds" && !t.Missing),
            $"aa50's laser_green_glow.mat: file {laser.FileWhere}, parent {lt?.ParentPath}");
        parts.Add("--material a loose .mat (ReflexGlass.mat) and one in a mod's archive (laser_green_glow.mat, with a slot from its parent)");
        log.Append(glass.ToText(new ReportTextOptions { MaxChars = int.MaxValue })).Append('\n').Append(laser.ToText(new ReportTextOptions { MaxChars = int.MaxValue })).Append('\n');

        var none = view.Material(@"materials\weapons\scopereticles\laser_ir_glow.mat");
        Check(none.Missing && none.ToText().Contains("MISSING", StringComparison.Ordinal), "laser_ir_glow.mat isn't said to be missing");

        // S33c: a path that is both a loose .mat file and compiled: which copy the game uses, as far as the evidence
        // goes, and that it isn't proven
        string glassText = glass.ToText(new ReportTextOptions { MaxChars = int.MaxValue });
        var glassJson = glass.ToJson();
        Check(glass.CompiledIn.Count >= 1 && glass.Trees.Count == 1 + glass.CompiledIn.Count && glass.GameUses != null
            && glass.GameUses.StartsWith("most likely the loose .mat file", StringComparison.Ordinal) && glass.GameUses.Contains("only a test in the game settles it", StringComparison.Ordinal)
            && glassText.Contains("Which copy the game uses: most likely the loose .mat file", StringComparison.Ordinal) && glassText.Contains("bLoadLooseFiles:Materials", StringComparison.Ordinal)
            && glassJson["gameUses"]?["settled"]?.GetValue<bool>() == false,
            $"ReflexGlass.mat, which copy the game uses: {glass.GameUses ?? "no answer"}");
        Check(water.GameUses != null && water.GameUses.StartsWith("not known", StringComparison.Ordinal) && arms.GameUses == null,
            $"which copy: waterchoppylarge.mat (2 databases) {water.GameUses ?? "no answer"}; kgcdoom's arms (1 place) {arms.GameUses ?? "none"}");
        parts.Add($"S33c: ReflexGlass.mat ({glass.CompiledIn.Count + 1} copies): \"{glass.GameUses}\"");

        // S33d: every slot has the game's own name (its enum BSMaterial::MRTextureChannel); the textures in the slots
        // the .mat files never named alone end as the Creation Kit names such files
        string[] want = { "Mask", "Flow", "DepthOffset", "OverlayMetalness", "ScatteringDirections", "ScatteringForwardAlpha", "HairID" };
        int[] slotAt = { 10, 11, 12, 16, 17, 18, 20 };
        Check(slotAt.Select((i, k) => MaterialsView.SlotOf("BSMaterial::MRTextureFile", i) == $"{want[k]} (slot {i})").All(x => x)
            && MaterialsView.SlotOf("BSMaterial::MRTextureFile", 0) == "Albedo (slot 0)" && MaterialsView.SlotOf("BSMaterial::MRTextureFile", 19) == "SecondaryOpacity (slot 19)"
            && MaterialsView.SlotOf("BSMaterial::MRTextureFile", 21) == "slot 21",
            $"slot names: {string.Join(", ", Enumerable.Range(0, 22).Select(i => MaterialsView.SlotOf("BSMaterial::MRTextureFile", i)))}");
        var ending = new Dictionary<int, Dictionary<string, int>>();
        foreach (var (cls, index, tex) in OwnTextures(gameC))
        {
            if (cls != "BSMaterial::MRTextureFile" || tex.Length == 0) continue;
            string stem = Path.GetFileNameWithoutExtension(tex.Replace('/', '\\').Split('\\')[^1]).ToLowerInvariant();
            int us = stem.LastIndexOf('_');
            if (!ending.TryGetValue(index, out var d)) ending[index] = d = new();
            string end = us < 0 ? "" : stem[us..];
            d[end] = d.GetValueOrDefault(end) + 1;
        }
        string Top(int i) => ending.TryGetValue(i, out var d) ? d.MaxBy(x => x.Value).Key : "(none)";
        // The Creation Kit names such files _mask, _zoffset, _directions, _forward_alpha (CreationKit.exe's table); HairID files end _id
        Check(Top(10) == "_mask" && Top(12) == "_zoffset" && Top(17) == "_directions" && Top(18) == "_alpha" && Top(20) == "_id" && Top(0) == "_color" && Top(1) == "_normal",
            $"the game's textures by slot, the most common ending: {string.Join("; ", ending.OrderBy(x => x.Key).Select(x => $"{x.Key} {MaterialsView.Slots.ElementAtOrDefault(x.Key)} {Top(x.Key)} ({x.Value.Values.Sum()})"))}");
        parts.Add($"S33d: 21 slot names from the game's enum; textures in Mask (slot 10) end {Top(10)}, in DepthOffset (slot 12) {Top(12)}");
        SlotsFromExe(ctx, parts, log);
        RuleFromExe(ctx, parts, log);

        // Which compiled materials use a texture: the search held against a walk of every material of the game's.
        // A plain call gathers nothing (fix-s33h): nothing is kept yet, as in a new process on an empty scratch folder
        string scratch = ctx.Session.Compare.ScratchFolder;
        Forget(scratch);
        var plainClock = System.Diagnostics.Stopwatch.StartNew();
        var users = view.Texture(@"Data\Textures\water\FoamDefault_color.dds");
        double plainSeconds = plainClock.Elapsed.TotalSeconds;
        const string foamTex = @"textures\water\foamdefault_color.dds";
        int brute = 0;
        for (int i = 0; i < gameC.Objects; i++)
        {
            var n = new MaterialContents.Node(gameC, i);
            if (n.IsMaterial && gameC.TexturesOf(n, gameC, out _).Any(t => MaterialsReport.Norm(t) == foamTex)) brute++;
        }
        var gameUsers = users.Users.Where(u => u.Db.IsGame).ToList();
        Check(users.Matched.Count == 1 && users.Matched[0].Path == foamTex && gameUsers.Count == brute && brute > 0
            && gameUsers.Any(u => u.Path == @"materials\water\waterchoppylarge.mat" && u.Uses.Contains("Albedo (slot 0) in Water_TextureSet4")),
            $"--texture foamdefault_color.dds: {gameUsers.Count} of the game's materials, a walk of every one finds {brute}");
        var kgcUsers = view.Texture(@"textures\clothes\kgcdoom\praetor\doom_praetor_uppersuit_01_arms_color.dds");
        Check(kgcUsers.Users.Count == 1 && kgcUsers.Users[0].Path == @"materials\clothes\kgcdoom\praetor\spacesuit_doom_praetor_arms01.mat" && !kgcUsers.Users[0].Db.IsGame,
            $"--texture of kgcdoom's arms: {string.Join(", ", kgcUsers.Users.Select(u => u.Path ?? u.Name))}");
        string usersText = users.ToText(new ReportTextOptions { MaxChars = int.MaxValue });
        Check(usersText.Contains("| materials\\water\\waterchoppylarge.mat | the game's | Albedo (slot 0) in Water_TextureSet4 |", StringComparison.Ordinal), "--texture's text: the row of waterchoppylarge.mat");
        parts.Add($"--texture foamdefault_color.dds: {users.Users.Count} compiled materials ({gameUsers.Count} the game's, as a walk of all {N(gameC.Objects)} objects finds); kgcdoom's arms texture: its one material");
        log.Append(usersText).Append('\n');

        // fix-s33h: the plain call left fix-f10's 16 without a path, said how many and offered --all-paths, and read
        // no mesh
        int plainNoPath = users.Users.Count(u => u.Path == null);
        string plainText = users.ToText(new ReportTextOptions { MaxChars = int.MaxValue });
        Check(plainNoPath == 16 && users.Notes.Any(n => n.StartsWith($"The path of 16 of the {users.Users.Count} materials isn't told by the quick tries", StringComparison.Ordinal) && n.Contains("--all-paths", StringComparison.Ordinal))
            && users.Read.Any(r => r.Contains("not gathered (--all-paths)", StringComparison.Ordinal)) && !users.Read.Any(r => r.Contains("meshes", StringComparison.Ordinal))
            && !File.Exists(Path.Combine(scratch, MaterialPaths.FileName)),
            $"S33h: a plain --texture foamdefault_color.dds: {plainNoPath} without a path; notes: {string.Join(" | ", users.Notes)}; cost: {string.Join(" | ", users.Read)}");
        parts.Add($"S33h: plain --texture foamdefault_color.dds, nothing kept: {plainNoPath} of {users.Users.Count} without a path, the line offers --all-paths, in {plainSeconds:0.0} s");

        // S33a: the paths of the game's materials, told from what Data names. Every path told has both hashes of
        // the material; the cheap tries alone give fix-f10's 39,434
        // S33g: first without the plugins' records (as before), then with them
        var types = MaterialPaths.RecordTypes.ToArray();
        MaterialPaths.RecordTypes.Clear();
        var sw0 = System.Diagnostics.Stopwatch.StartNew();
        (int Materials, int Cheap, int Told, List<(MaterialContents.Node Node, string? Path)> Paths) cov0;
        try { cov0 = GamePathCoverage(new MaterialsView(ctx.Session)); }
        finally { MaterialPaths.RecordTypes.UnionWith(types); }
        double cov0Seconds = sw0.Elapsed.TotalSeconds;
        var e0 = KeptNow()!;
        Forget(scratch);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cov = GamePathCoverage(view);
        double covSeconds = sw.Elapsed.TotalSeconds;
        var e1 = KeptNow()!;
        var before = cov0.Paths.Where(x => x.Path != null).Select(x => x.Node.At).ToHashSet();
        var onlyRecords = cov.Paths.Where(x => x.Path != null && !before.Contains(x.Node.At)).Select(x => x.Path!).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Check(cov0.Told == Told0 && e0.Records == 0 && e1.RecordPlugins > 100 && e1.RecordTexts > 13_000 && cov.Told == Told && onlyRecords.Count == cov.Told - cov0.Told,
            $"S33g: the game's materials with a path: {N(cov0.Told)} without the plugins' records, {N(cov.Told)} with them ({N(e1.Records)} records of {N(e1.RecordPlugins)} plugins, {N(e1.RecordTexts)} .mat texts); {onlyRecords.Count} told only by them");
        // A plugin saved since the gather: --all-paths (and the service) gather again; a plain call still takes the kept
        // gather, whose paths all still hold (the archives are the same), and says a plugin changed
        string filesPart = e1.Key[..e1.Key.IndexOf('-')], newer = filesPart + "-0000000000000000";
        var plainOlder = MaterialPaths.Kept(newer, scratch, out _, olderPlugins: true);
        Check(MaterialPaths.Kept(newer, scratch, out _) == null && plainOlder != null && plainOlder.OlderPlugins && plainOlder.PathsFound == e1.PathsFound
            && MaterialPaths.Kept(filesPart.Replace(filesPart[0], filesPart[0] == '0' ? '1' : '0') + "-0", scratch, out _, olderPlugins: true) == null,
            "S33g: a gather with an older plugins part: --all-paths should gather again, a plain call should take it");
        MaterialPaths.Kept(e1.Key, scratch, out _);
        // The plugins are in the gather's key: a plugin saved again (or added, or removed) makes it gather again
        var keyArchives = new[] { gameArchive };
        Check(MaterialPaths.KeyOf(keyArchives, Array.Empty<string>(), ctx.Session.Index.Plugins) != MaterialPaths.KeyOf(keyArchives, Array.Empty<string>(), ctx.Session.Index.Plugins.Skip(1)),
            "S33g: the gather's key doesn't change with the plugins");
        parts.Add($"S33g: the plugins' records: {N(e1.Records)} records of {N(e1.RecordPlugins)} plugins ({MB(e1.RecordBytes)}), {N(e1.RecordTexts)} .mat texts, in {e1.RecordSeconds:0.00} s; the game's materials with a path {N(cov0.Told)} -> {N(cov.Told)} (+{N(onlyRecords.Count)}, e.g. {string.Join(", ", onlyRecords.Take(3))}); the gather {e0.Seconds:0.00} s without them ({cov0Seconds:0.0} s in all), {e1.Seconds:0.00} s with ({covSeconds:0.0} s)");
        log.Append($"S33g: told only by the plugins' records ({onlyRecords.Count}):\n").Append(string.Join("\n", onlyRecords)).Append('\n');
        // fix-s33h: the gather is kept in the scratch folder
        var scratchFile = new FileInfo(Path.Combine(scratch, MaterialPaths.FileName));
        Check(scratchFile.Exists && scratchFile.Length < 8 << 20, $"S33h: the gather's scratch file: {(scratchFile.Exists ? MB(scratchFile.Length) : "not written")}");
        parts.Add($"S33h: the gather, kept in the scratch folder ({MB(scratchFile.Exists ? scratchFile.Length : 0)})");
        int wrongHash = cov.Paths.Count(x => x.Path != null && !gameDb.Has(x.Path));
        var glassNode = cov.Paths.FirstOrDefault(x => x.Path == @"materials\weapons\bridger\reflexglass.mat");
        Check(cov.Materials == gameDb.Count && cov.Cheap == 39_434 && cov.Told >= 47_000 && wrongHash == 0 && glassNode.Path != null,
            $"S33a: of the game's {N(cov.Materials)} materials, {N(cov.Cheap)} get a path from the cheap tries and {N(cov.Told)} with what Data names; {wrongHash} told paths don't hash to their material");
        parts.Add($"S33a: the game's materials with a path: {N(cov.Told)} of {N(cov.Materials)} (was {N(cov.Cheap)}), in {covSeconds:0.0} s");
        // j811: the paths told by the particle files, the databases' own names, and a record's text whose folder ends
        // in "Materials" (the gather cut it at "Materials\" before); no two paths Data writes share both hashes; the
        // materials left without a path, each in the log
        string? PathOfName(string name) => cov.Paths.FirstOrDefault(x => x.Node.OwnName == name).Path;
        var toldJ811 = new Dictionary<string, string>
        {
            ["BloodSpurt_Medium_01"] = @"materials\effects\blood\bloodspurt_medium_01.mat",                         // sfbgs00b's completepsfx.blob
            ["MGoulet_Pattern05_Test"] = @"materials\test\test\mgoulet_pattern05_test.mat",                          // SFBGS007's database: a name
            ["Test_ShipScreen_Avionics01_Decal"] = @"materials\test\additive_multiply\test_shipscreen_avionics01_decal.mat",
            ["Neon_MetalRust01"] = @"materials\architecture\city\neon\basematerials\neon_metalrust01.mat",           // Starfield.esm's LMSW
            ["NeonPaintedScuffed01_Base"] = @"materials\architecture\city\neon\basematerials\neonpaintedscuffed01_base.mat", // its folder
        };
        var wrongJ811 = toldJ811.Where(w => PathOfName(w.Key) != w.Value).Select(w => $"{w.Key}: {PathOfName(w.Key) ?? "none"}").ToList();
        Check(wrongJ811.Count == 0 && e1.Blobs > 30 && e1.DatabaseNames > 0, $"j811: paths told: {string.Join("; ", wrongJ811)}; {e1.Blobs} particle files, {e1.DatabaseNames} database names read");
        Check(e1.Collisions.Count == 0 && e1.PathTexts > 30_000, $"S8: {e1.Collisions.Count} pairs of material paths share both hashes ({string.Join("; ", e1.Collisions.Take(3))}) among {e1.PathTexts}");
        var left = cov.Paths.Where(x => x.Path == null).ToList();
        foreach (var (n, _) in left)
        {
            var (dir, file, _) = n.Id;
            var folder = cov.Paths.FirstOrDefault(x => x.Path != null && x.Node.Id.Dir == dir).Path;
            log.Append($"S33a left\t{n.IdText}\t{n.OwnName ?? "(no name)"}\t{(n.OwnName != null && MaterialDatabase.ResourceId(n.OwnName + ".mat").File == file ? "its name is its file's" : "no name of its file")}\t{(folder != null ? "folder known: " + Path.GetDirectoryName(folder) : "folder 0x" + dir.ToString("X8") + " named nowhere in Data")}\n");
        }
        parts.Add($"j811: {e1.Blobs} particle files ({MB(e1.BlobBytes)}) and {N(e1.DatabaseNames)} database names read; {N(cov.Materials - left.Count)} told, {left.Count} left; no two of the {N(e1.PathTexts)} material paths Data writes share both hashes");
        // fix-f10's answer left 16 of them without a path. A plain call in a new process (or another worker): the
        // process's memory forgotten, the gather is taken from the scratch folder, nothing is read again
        Forget(null);
        var fresh = new MaterialsView(ctx.Session);
        var keptClock = System.Diagnostics.Stopwatch.StartNew();
        var users2 = fresh.Texture(@"textures\water\foamdefault_color.dds");
        double keptSeconds = keptClock.Elapsed.TotalSeconds;
        Check(users2.Users.Count(u => u.Path == null) < 16 && users2.Users.Count == users.Users.Count && users2.Read.Any(r => r.Contains("taken from the scratch folder", StringComparison.Ordinal))
            && !users2.Notes.Any(n => n.Contains("--all-paths", StringComparison.Ordinal)),
            $"S33a/S33h: --texture foamdefault_color.dds with the gather kept, materials without a path: {users2.Users.Count(u => u.Path == null)} ({string.Join(", ", users2.Users.Where(u => u.Path == null).Select(u => u.Name))}); cost: {string.Join(" | ", users2.Read)}");
        parts.Add($"S33a: --texture foamdefault_color.dds with the gather kept: {users2.Users.Count(u => u.Path == null)} of {users2.Users.Count} materials without a path (fix-f10: 16), taken from the scratch folder in {keptSeconds:0.0} s");
        // --all-paths with the gather kept reads nothing more
        var all = new MaterialsView(ctx.Session) { AllPaths = true }.Material(@"materials\water\waterchoppylarge.mat");
        Check(all.Read.Any(r => r.Contains("taken from memory", StringComparison.Ordinal) && r.Contains("material paths", StringComparison.Ordinal)), $"S33h: --material --all-paths with the gather kept: {string.Join(" | ", all.Read)}");

        // S33b: --texture finds .mat files that name a texture themselves (loose and archived), with the slot
        var flow = view.Texture(@"textures\effects\powerpersonalatmospherelines_flow.dds");
        var green = flow.FileUsers.FirstOrDefault(u => u.Path == @"materials\weapons\scopereticles\laser_green_glow.mat");
        Check(flow.MatFilesLookedIn > 3_000 && green != null && green.Where == "aa50_longwatch - main.ba2" && green.Uses.Any(x => x.StartsWith("FlowSettingsComponent", StringComparison.Ordinal)),
            $"S33b: --texture powerpersonalatmospherelines_flow.dds: {flow.FileUsers.Count} .mat files of {flow.MatFilesLookedIn} ({string.Join(", ", flow.FileUsers.Take(5).Select(u => $"{u.Path} in {u.Where}: {string.Join("; ", u.Uses)}"))})");
        log.Append(flow.ToText(new ReportTextOptions { MaxChars = int.MaxValue })).Append('\n');
        // A texture only a .mat file names (a loose one; no database names it)
        var neon = view.Texture(@"textures\clothes\neon_entertainer\neon_entertainer_upperbody_color.dds");
        var neonFile = neon.FileUsers.SingleOrDefault();
        string neonText = neon.ToText(new ReportTextOptions { MaxChars = int.MaxValue });
        Check(neon.Users.Count == 0 && neon.Matched.Count == 1 && neonFile != null && neonFile.Path == @"materials\neon_entertainer\neon_entertainer_upperbody.mat"
            && neonFile.Where == "loose" && neonFile.Uses.Any(x => x.StartsWith("Albedo (slot 0) in ", StringComparison.Ordinal))
            && neonText.Contains("0 compiled", StringComparison.Ordinal) && neonText.Contains("a loose .mat file", StringComparison.Ordinal),
            $"S33b: a .mat-only texture: {neon.Users.Count} compiled, .mat files: {string.Join(", ", neon.FileUsers.Select(u => $"{u.Path} ({u.Where}): {string.Join("; ", u.Uses)}"))}");
        log.Append(neonText).Append('\n');
        parts.Add($"S33b: neon_entertainer_upperbody_color.dds, which no database names: {neonFile?.Path} ({neonFile?.Where}), {string.Join("; ", neonFile?.Uses ?? new())}");
        parts.Add($"S33b: --texture powerpersonalatmospherelines_flow.dds: {flow.Users.Count} compiled, {flow.FileUsers.Count} .mat files of {flow.MatFilesLookedIn}");

        // Asked again: every database comes from memory
        var again = view.Material(@"materials\water\waterchoppylarge.mat");
        Check(store.ContentsRead.Count == 0 && store.DatabasesRead.Count == 0 && again.Read.Any(r => r.Contains("taken from memory", StringComparison.Ordinal)), "--material asked again read a database again");
        parts.Add($"asked again: in {again.Seconds:0.00} s, every database from memory");

        // S33e: the databases read whole are let go once no view has used them for a while (ArchiveStore.IdleRelease),
        // and read again when next needed
        static long Private() { using var me = System.Diagnostics.Process.GetCurrentProcess(); return me.PrivateMemorySize64; }
        var held = ArchiveStore.ContentsHeld();
        Check(held.Databases == 21 && ArchiveStore.ReleaseIfIdle(TimeSpan.FromHours(1)).Databases == 0 && ArchiveStore.ContentsHeld().Databases == 21,
            $"S33e: {held.Databases} databases held; one used within the hour, nothing should be let go");
        long privBefore = Private();
        var letClock = System.Diagnostics.Stopwatch.StartNew();
        var let = ArchiveStore.ReleaseIfIdle(TimeSpan.Zero);
        double letSeconds = letClock.Elapsed.TotalSeconds;
        long privAfter = Private();
        Check(let.Databases == held.Databases && let.Kept == held.Kept && ArchiveStore.ContentsHeld().Databases == 0, $"S33e: let go of {let.Databases} of {held.Databases} databases");
        var back = view.Material(@"materials\water\waterchoppylarge.mat");
        Check(store.ContentsRead.Count == 2 && back.Trees.Count == water.Trees.Count
            && Tex(back.Trees.First(t => t.Source.StartsWith("the game's", StringComparison.Ordinal)).Tree).Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(shown),
            $"S33e: --material after the databases were let go: {store.ContentsRead.Count} read again, {back.Trees.Count} trees");
        // The gather (the service's indexer job) lets go of the databases it read whole itself
        Forget(scratch);
        ArchiveStore.ReleaseContents();
        string warm = new MaterialsView(ctx.Session).WarmPaths();
        Check(warm.Contains("let go of the 21 databases it read whole", StringComparison.Ordinal) && ArchiveStore.ContentsHeld().Databases == 0, $"S33e: the gather's line: {warm}; {ArchiveStore.ContentsHeld().Databases} databases still held");
        parts.Add($"S33e: {held.Databases} databases read whole ({MB(held.Kept)}) let go after an idle time: this process {MB(privBefore)} -> {MB(privAfter)} private (let go and compacted in {letSeconds:0.00} s); read again when next needed ({back.Seconds:0.00} s); the gather lets go of what it read ({warm})");
    }

    // ---------------------------------------------------------------- Helpers moved out of SFSearch.Reports (J81)
    // They were test-only members of the library (MaterialsView.GamePathCoverage, MaterialContents.OwnTextures,
    // MaterialPaths.Current and Forget). They read the library's own internals through InternalsVisibleTo.

    /// <summary>What this process holds of the material paths' evidence now (was MaterialPaths.Current).</summary>
    static MaterialPathEvidence? KeptNow() { lock (MaterialPaths.Gate) return MaterialPaths._kept; }

    /// <summary>Forget what this process holds, as a new process (or another worker) would start, and with
    /// <paramref name="scratch"/> also the scratch file (was MaterialPaths.Forget).</summary>
    static void Forget(string? scratch)
    {
        lock (MaterialPaths.Gate)
        {
            MaterialPaths._kept = null;
            if (scratch != null) File.Delete(Path.Combine(scratch, MaterialPaths.FileName));
        }
    }

    /// <summary>Every texture an object of the database sets itself (not what it inherits), with its component's class
    /// and index: for the test of the slot names against the textures' own file names (was MaterialContents.OwnTextures).</summary>
    static IEnumerable<(string Class, int Index, string Texture)> OwnTextures(MaterialContents db)
    {
        for (int o = 0; o < db.Objects; o++)
            for (int c = db._compStart[o]; c < db._compStart[o + 1]; c++)
                for (int e = db._compEntry[c]; e < db._compEntry[c + 1]; e++)
                    if (db._entries[e].Texture) yield return (MaterialContents.ClassName((int)(db._compKey[c] >> 16)), (int)(db._compKey[c] & 0xFFFF), db.Text((int)db._entries[e].Value));
    }

    /// <summary>S33a: how many of the game's materials get a path from the cheap tries, and how many with what Data
    /// names; and the path told for each material (was MaterialsView.GamePathCoverage). Turns the view's AllPaths on.</summary>
    static (int Materials, int Cheap, int Told, List<(MaterialContents.Node Node, string? Path)> Paths) GamePathCoverage(MaterialsView view)
    {
        var probe = new MaterialPartsAnswer { Material = "" };
        var finder = view.Finder(null, false, probe, out _);
        var dbs = view.Databases(finder, probe);
        view.AllPaths = true;
        var gameDb = dbs.First(d => d.Db.IsGame && d.Db.Error == null).Db;
        var game = view._s.Archives.Contents(gameDb) ?? throw new InvalidDataException("the game's database couldn't be read whole");
        int materials = 0, cheap = 0, told = 0;
        var paths = new List<(MaterialContents.Node, string?)>();
        for (int i = 0; i < game.Objects; i++)
        {
            var n = new MaterialContents.Node(game, i);
            if (!n.IsMaterial) continue;
            materials++;
            string? p = MaterialsView.PathOf(n, game, null);
            if (p != null) cheap++;
            p ??= view.More(n, finder, dbs);
            if (p != null) told++;
            paths.Add((n, p));
        }
        return (materials, cheap, told, paths);
    }
}
