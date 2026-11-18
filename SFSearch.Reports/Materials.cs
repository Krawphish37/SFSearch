// materials: the materials a plugin's meshes and records name, where each is, and the textures of those that are
// files, with what is missing along the way.
//  - The meshes are the .nif files in the plugin's own archives (a loose file at the same path is read instead: the
//    game uses it), and every mesh the plugin's records name wherever it is: a loose file in Data, a master's archive
//    (the game's own too), another plugin's archive (GAPS S8). A mesh no loose file or loaded archive holds is
//    counted, and left to the assets report. A mesh names its materials as text (the strings of its header that its
//    shaders have for their names, and length-prefixed texts in its blocks), or by hash: a world's object LOD meshes
//    name them by resource ID (MaterialsMesh.cs, GAPS S46). A ".mat" string of the header that no shader has for its
//    name is left over from editing: counted and told in a note, not listed (GAPS S8). A material named by hash is the path some other mesh or record of the plugin
//    writes when the hashes agree; otherwise it is listed by its hashes, and looked up in the compiled databases.
//  - The records: every text ending in .mat on the plugin's records (a landscape texture's material, a biome's water,
//    a planet's ring), and every mesh (.nif) they name. A record is read only when its bytes hold ".mat" or ".nif".
//    World data is read for a plugin of up to 100,000 records, as scripts does.
//  - A material is looked for as a file first: loose in Data, in the plugin's archives, its masters', the other
//    archives. A .mat file (JSON) is read, and each texture it uses is looked for the same way: those it names, and
//    those its objects inherit from their parents (objects of the game's compiled database, or of another material
//    file) and from the parts they link to outside the file (MaterialFile.cs). A parent or a part found nowhere is
//    told in a note: what it would have given isn't known.
//  - A material that is no file is looked up in the compiled material databases (materialsbeta.cdb): the game's own,
//    the plugin's, its masters', the other loaded plugins'. A database is read as far as its material list, once per
//    process, and only when a material is no file. A material in none of them is missing. The databases the game
//    reads are its own and those in a plugin's " - Main" archives (MaterialDatabase.GameReads); one elsewhere is
//    looked in last, and a material only there is a problem. A material in none that only meshes of the game's own
//    archives name (meshes the plugin's records name) is the game's gap, told apart and not counted as missing.
//  - A compiled material's textures: the database is then read whole (MaterialContents), once per process, and each
//    texture the material's parts name is looked for as a file, like a .mat file's. A texture the game's own material
//    names and the game doesn't ship is listed apart, not counted as missing; so is one a mod's material names (in a
//    file, or compiled) that the game's own compiled materials name too and nothing in Data holds: the game runs its
//    own materials without it (textures\generated\sgtexset\..., named by hundreds of material files made from the
//    game's).
// Nothing is stored.
using System.Text.Json;
using System.Text.Json.Nodes;
using SFSearch.Compare;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Reports;

using static ReportAnswer;

public sealed class MaterialsOptions
{
    /// <summary>For a plugin file outside Data: read the archives beside it, not the ones in Data named for it.</summary>
    public bool Beside { get; init; }
}

/// <summary>One texture a material names.</summary>
public sealed class MaterialTexture
{
    public required string Path { get; init; }
    public FileOrigin Origin { get; init; }
    /// <summary>"loose", or the archive's name. Null when the texture is nowhere.</summary>
    public string? Where { get; init; }
    /// <summary>True when no loose file and no loaded archive holds it.</summary>
    public bool Missing => Origin is FileOrigin.None or FileOrigin.NotLoaded;
    /// <summary>A texture the game's own compiled materials name that the game doesn't ship either (no loose file and
    /// no archive holds it): the game's gap, not the plugin's. Not counted as missing.</summary>
    public bool GameLacks { get; init; }
    /// <summary>For a material file: a texture the file doesn't name itself, from an object's parent or a part outside
    /// the file.</summary>
    public bool Inherited { get; init; }
}

public sealed class MaterialRow
{
    /// <summary>The material's path as Data names it, in lower case: "materials\weapons\x.mat". For a material a mesh
    /// names only by hash (<see cref="Hash"/>), a text that says so: "(named by hash: folder 0xB64DBB92, file 0xF82CFBBB)".</summary>
    public required string Path { get; init; }
    /// <summary>For a material a mesh names only by its resource ID: the folder's and the file name's CRC-32. Null for a
    /// material named by its path.</summary>
    public (uint Dir, uint File)? Hash { get; init; }
    /// <summary>For a material named only by hash: the name its compiled database gives it, when its textures were read.</summary>
    public string? DatabaseName { get; set; }
    /// <summary>The text a material named only by hash is listed as.</summary>
    public static string HashText(uint dir, uint file) => $"(named by hash: folder 0x{dir:X8}, file 0x{file:X8})";
    public FileOrigin Origin { get; set; }
    /// <summary>"loose", or the archive's name. Null when it is no file.</summary>
    public string? Where { get; set; }
    /// <summary>True when no loose file and no archive holds it as a .mat file.</summary>
    public bool NoFile => Where == null;
    /// <summary>The compiled material database that holds it, when it is no file.</summary>
    public MaterialDatabase? Database { get; set; }
    /// <summary>What the database's archive is to the plugin.</summary>
    public FileOrigin DatabaseOrigin { get; set; }
    /// <summary>False when the database is one the game doesn't read (loose, or not in a " - Main" archive).</summary>
    public bool DatabaseCounts { get; set; } = true;
    /// <summary>For a compiled material: its textures were read from the database.</summary>
    public bool TexturesRead { get; set; }
    /// <summary>Links or parents that lead nowhere, so a texture may be left out: for a compiled material, in its
    /// database; for a material file, those of <see cref="NotFound"/> and any in the databases it inherits from.</summary>
    public int Unresolved { get; set; }
    /// <summary>For a material file: the parents and parts its objects name that are in no material file and no
    /// compiled material database the game reads, as the file writes them.</summary>
    public List<string> NotFound { get; } = new();
    /// <summary>For a material file: texture names of its own that aren't .dds files (not looked for).</summary>
    public List<string> NotDds { get; } = new();
    /// <summary>For a material file: something it uses comes from a compiled material database.</summary>
    public bool InheritsFromDatabase { get; set; }
    /// <summary>Compiled: no file, and in a compiled material database.</summary>
    public bool Compiled => NoFile && Database != null;
    /// <summary>No file and in no database, while every database was read. Missing.</summary>
    public bool Missing { get; set; }
    /// <summary>No file and in no database read, but a database couldn't be read: not judged.</summary>
    public bool NotChecked => NoFile && Database == null && !Missing && !GameLacks;
    /// <summary>A .mat file in the plugin's own archives, or named by one of its meshes.</summary>
    public bool FromMeshes { get; set; }
    /// <summary>The plugin's meshes that name it, and the meshes its records name elsewhere ("meshes\x.nif (where)").</summary>
    public List<string> UsedBy { get; } = new();
    /// <summary>Named only by meshes in other plugins' archives (its masters', the game's) that the plugin's records name:
    /// not by a mesh of its own archives, a loose mesh, a .mat file of its own, or a record (S8).</summary>
    public bool OnlyElsewhere => _elsewhere && !_own && NamedBy.Count == 0;
    /// <summary>Named only by meshes of the game's own archives ("Starfield - *.ba2") that the plugin's records name.</summary>
    public bool OnlyGameMeshes => _game && !_other && !_own && NamedBy.Count == 0;
    bool _elsewhere, _own, _game, _other;
    /// <param name="own">A mesh of the plugin's own archives, a loose mesh, or a .mat file of its own.</param>
    /// <param name="gameMesh">A mesh of one of the game's own archives.</param>
    internal void UsedFrom(bool own, bool gameMesh = false)
    {
        if (own) { _own = true; return; }
        _elsewhere = true;
        if (gameMesh) _game = true; else _other = true;
    }
    /// <summary>No file and in no database, and only meshes of the game's own archives name it: the game ships those
    /// meshes without it (materials\weapons\orionrifle\orion_barrel.mat, named by the game's orionrifle.nif). The
    /// game's gap, not the plugin's: not counted as missing. On Starfield.esm itself, whose archives they are, it is
    /// missing.</summary>
    public bool GameLacks { get; set; }
    /// <summary>The plugin's records that name it: "EditorID (FormKey) SIG Field".</summary>
    public List<string> NamedBy { get; } = new();
    public List<MaterialTexture> Textures { get; } = new();
    /// <summary>Why the file couldn't be read as a material.</summary>
    public string? Problem { get; set; }
    public IEnumerable<MaterialTexture> MissingTextures => Textures.Where(t => t.Missing && !t.GameLacks);
    public bool HasProblem => Problem != null || Missing || MissingTextures.Any() || Origin == FileOrigin.NotLoaded
        || (Compiled && (DatabaseOrigin == FileOrigin.NotLoaded || !DatabaseCounts));

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["material"] = Path };
        if (Hash is { } h)
        {
            o["namedByHash"] = new JsonObject { ["folderHash"] = $"0x{h.Dir:X8}", ["fileHash"] = $"0x{h.File:X8}" };
            if (DatabaseName != null) o["databaseName"] = DatabaseName;
        }
        if (!NoFile)
        {
            o["foundIn"] = Where; o["origin"] = Origin.ToString();
            if (Unresolved > 0) o["unresolvedLinks"] = Unresolved;
            if (NotFound.Count > 0) o["notFound"] = new JsonArray(NotFound.Select(n => (JsonNode)n).ToArray());
            if (NotDds.Count > 0) o["notTextureFiles"] = new JsonArray(NotDds.Select(n => (JsonNode)n).ToArray());
        }
        else if (Compiled)
        {
            o["compiled"] = true;
            o["compiledIn"] = new JsonObject { ["database"] = Database!.Path, ["foundIn"] = Database.Where, ["origin"] = DatabaseOrigin.ToString(), ["game"] = Database.IsGame, ["gameReads"] = DatabaseCounts };
            o["texturesRead"] = TexturesRead;
            if (Unresolved > 0) o["unresolvedLinks"] = Unresolved;
        }
        else if (Missing) o["missing"] = true;
        else if (GameLacks) o["gameLacks"] = true;
        else o["notChecked"] = true;
        o["usedBy"] = new JsonArray(UsedBy.Select(m => (JsonNode)m).ToArray());
        o["namedBy"] = new JsonArray(NamedBy.Select(m => (JsonNode)m).ToArray());
        o["textures"] = new JsonArray(Textures.Select(t =>
        {
            var x = new JsonObject { ["texture"] = t.Path, ["foundIn"] = t.Where, ["origin"] = t.Origin.ToString() };
            if (t.GameLacks) x["gameLacks"] = true;
            if (t.Inherited) x["inherited"] = true;
            return (JsonNode)x;
        }).ToArray());
        o["missingTextures"] = new JsonArray(MissingTextures.Select(t => (JsonNode)t.Path).ToArray());
        if (Problem != null) o["problem"] = Problem;
        return o;
    }
}

public sealed class MaterialsAnswer : ReportAnswer
{
    public override string Kind => "materials";
    public required string Plugin { get; init; }
    public List<string> OwnArchives { get; } = new();
    public int Meshes { get; set; }
    /// <summary>The meshes the plugin's records name (S8), by path.</summary>
    public int MeshesNamed { get; set; }
    /// <summary>Of those, the ones read outside the plugin's own archives, by where each was found: loose, a master's
    /// archive (the game's own too), another loaded plugin's.</summary>
    public int MeshesLoose { get; set; }
    public int MeshesMasters { get; set; }
    public int MeshesOther { get; set; }
    public int MeshesOutside => MeshesLoose + MeshesMasters + MeshesOther;
    /// <summary>Meshes the records name that no loose file and no loaded archive holds: not read (the assets report
    /// lists them).</summary>
    public int MeshesNamedMissing => MeshesNotFound.Count;
    public List<string> MeshesNotFound { get; } = new();
    /// <summary>Meshes of the plugin's own archives read from a loose file at the same path (the game uses that one).</summary>
    public int MeshesLooseOverOwn { get; set; }
    /// <summary>The ".mat" strings in the meshes' string tables that no shader of the mesh has for its name (left over
    /// from editing: not materials of the mesh), each mesh's counted, and the meshes that hold one.</summary>
    public int NamesUnused { get; set; }
    public int MeshesWithUnused { get; set; }
    /// <summary>The first few, as "name (mesh)".</summary>
    public List<string> UnusedExamples { get; } = new();
    /// <summary>The materials a mesh names without the "materials\" folder, looked for under it; the first few.</summary>
    public int NamesBare { get; set; }
    public List<string> BareExamples { get; } = new();
    /// <summary>The meshes that name a material by hash, and those whose header couldn't be read.</summary>
    public int MeshesByHash { get; set; }
    public int MeshesHeaderUnread { get; set; }
    /// <summary>The materials named only by hash (no mesh or record of the plugin names their path).</summary>
    public int ByHash => Rows.Count(m => m.Hash != null);
    /// <summary>The plugin's records looked at, those read because their bytes hold ".mat", and world records left out.</summary>
    public int RecordsLooked { get; set; }
    public int RecordsRead { get; set; }
    public int WorldLeftOut { get; set; }
    /// <summary>The compiled material databases (materialsbeta.cdb) the plugin ships, in its own archives.</summary>
    public List<string> CompiledDatabases { get; } = new();
    /// <summary>The compiled material databases looked in, in the order they were looked in, with what each is to the plugin.</summary>
    public List<(MaterialDatabase Db, FileOrigin Origin)> DatabasesRead { get; } = new();
    public List<MaterialRow> Rows { get; } = new();

    /// <summary>Material files read.</summary>
    public int Checked => Rows.Count(m => !m.NoFile);
    public int Compiled => Rows.Count(m => m.Compiled);
    public int CompiledInGame => Rows.Count(m => m.Compiled && m.Database!.IsGame);
    public int Missing => Rows.Count(m => m.Missing);
    /// <summary>The materials only the game's own meshes name that the game doesn't ship either: not counted as missing.</summary>
    public int MaterialsGameLacks => Rows.Count(m => m.GameLacks);
    public int NotChecked => Rows.Count(m => m.NotChecked);
    public int MissingTextures => Rows.Sum(m => m.MissingTextures.Count());
    /// <summary>The textures named by compiled materials (read from their databases).</summary>
    public int CompiledTextures => Rows.Where(m => m.Compiled).Sum(m => m.Textures.Count);
    /// <summary>The textures material files don't name themselves: inherited from a parent, or of a part outside the file.</summary>
    public int InheritedTextures => Rows.Where(m => !m.NoFile).Sum(m => m.Textures.Count(t => t.Inherited));
    /// <summary>Textures of the game's own compiled materials that the game doesn't ship either.</summary>
    public int GameLacks => Rows.Sum(m => m.Textures.Count(t => t.GameLacks));
    public int MaterialsWithMissing => Rows.Count(m => m.MissingTextures.Any());
    public int Unreadable => Rows.Count(m => m.Problem != null);
    public bool Failed => Rows.Any(m => m.HasProblem);

    protected override void Fill(JsonObject json)
    {
        json["plugin"] = Plugin;
        json["ownArchives"] = new JsonArray(OwnArchives.Select(e => (JsonNode)e).ToArray());
        json["compiledDatabases"] = new JsonArray(CompiledDatabases.Select(e => (JsonNode)e).ToArray());
        json["databasesRead"] = new JsonArray(DatabasesRead.Select(d => (JsonNode)new JsonObject
        {
            ["database"] = d.Db.Path, ["foundIn"] = d.Db.Where, ["origin"] = d.Origin.ToString(), ["game"] = d.Db.IsGame, ["materials"] = d.Db.Count,
            ["buildVersion"] = d.Db.BuildVersion, ["error"] = d.Db.Error,
        }).ToArray());
        json["counts"] = new JsonObject
        {
            ["meshes"] = Meshes, ["meshesNamedByRecords"] = MeshesNamed, ["meshesOutsideOwnArchives"] = MeshesOutside, ["meshesLoose"] = MeshesLoose, ["meshesOfMasters"] = MeshesMasters,
            ["meshesOfOtherPlugins"] = MeshesOther, ["meshesNamedNotFound"] = MeshesNamedMissing, ["meshesLooseOverOwn"] = MeshesLooseOverOwn, ["meshesNamingByHash"] = MeshesByHash, ["namedByHash"] = ByHash, ["namedWithoutMaterialsFolder"] = NamesBare, ["namesNoShaderUses"] = NamesUnused, ["meshesWithNamesNoShaderUses"] = MeshesWithUnused, ["recordsLookedAt"] = RecordsLooked, ["recordsRead"] = RecordsRead, ["materials"] = Rows.Count, ["checked"] = Checked,
            ["compiled"] = Compiled, ["compiledInGame"] = CompiledInGame, ["missing"] = Missing, ["materialsTheGameLacks"] = MaterialsGameLacks, ["notChecked"] = NotChecked, ["textures"] = Rows.Sum(m => m.Textures.Count),
            ["missingTextures"] = MissingTextures, ["materialsWithMissingTextures"] = MaterialsWithMissing, ["unreadable"] = Unreadable,
            ["texturesOfCompiled"] = CompiledTextures, ["texturesTheGameLacks"] = GameLacks, ["texturesInheritedByFiles"] = InheritedTextures,
        };
        // A material named only by hash that a database holds with nothing wrong is counted, not listed: a world's LOD
        // meshes name thousands (sfta01.esm: 6,512, 10.9 MB of rows with their textures). Every other row is listed in
        // full, so every missing material and every problem is in the structured form; the text form lists them all.
        // Likewise a material only the meshes of other plugins' archives name (its masters', the game's: meshes its records
        // name, S8), with nothing wrong: darkstar.esm's records name 360 of the game's meshes.
        static bool Folded(MaterialRow m) => m.Hash != null && m.Compiled && !m.HasProblem;
        static bool Elsewhere(MaterialRow m) => !Folded(m) && m.OnlyElsewhere && !m.HasProblem && !m.GameLacks;
        json["materials"] = new JsonArray(Rows.Where(m => !Folded(m) && !Elsewhere(m)).Select(m => (JsonNode)m.ToJson()).ToArray());
        var elsewhere = Rows.Where(Elsewhere).ToList();
        if (elsewhere.Count > 0)
            json["ofOtherMeshesFound"] = new JsonObject
            {
                ["about"] = "Materials named only by meshes in other plugins' archives (its masters', the game's) that its records name, each a file or in a compiled material database the game reads, with all their textures found: counted here, not listed in \"materials\". The text form with detail lists each one.",
                ["materials"] = elsewhere.Count,
                ["meshes"] = elsewhere.SelectMany(m => m.UsedBy).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                ["compiled"] = elsewhere.Count(m => m.Compiled),
                ["compiledInGame"] = elsewhere.Count(m => m.Compiled && m.Database!.IsGame),
                ["files"] = elsewhere.Count(m => !m.NoFile),
                ["textures"] = elsewhere.Sum(m => m.Textures.Count),
                ["texturesTheGameLacks"] = elsewhere.Sum(m => m.Textures.Count(t => t.GameLacks)),
            };
        var folded = Rows.Where(Folded).ToList();
        if (folded.Count > 0)
            json["namedByHashFound"] = new JsonObject
            {
                ["about"] = "Materials the meshes name only by hash, each in a compiled material database the game reads, with all their textures found: counted here, not listed in \"materials\". The text form with detail lists each one.",
                ["materials"] = folded.Count,
                ["meshes"] = folded.SelectMany(m => m.UsedBy).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                ["textures"] = folded.Sum(m => m.Textures.Count),
                ["texturesTheGameLacks"] = folded.Sum(m => m.Textures.Count(t => t.GameLacks)),
                ["byDatabase"] = new JsonArray(folded.GroupBy(m => m.Database!).Select(g => (JsonNode)new JsonObject
                {
                    ["database"] = g.Key.Path, ["foundIn"] = g.Key.Where, ["origin"] = g.First().DatabaseOrigin.ToString(), ["game"] = g.Key.IsGame,
                    ["materials"] = g.Count(), ["textures"] = g.Sum(m => m.Textures.Count),
                }).ToArray()),
            };
    }

    static string Row(MaterialRow m)
    {
        var users = new List<string>();
        if (m.UsedBy.Count == 1) users.Add(m.UsedBy[0]); else if (m.UsedBy.Count > 1) users.Add($"{N(m.UsedBy.Count)} meshes");
        if (m.NamedBy.Count == 1) users.Add(m.NamedBy[0]); else if (m.NamedBy.Count > 1) users.Add($"{N(m.NamedBy.Count)} records ({m.NamedBy[0]}, …)");
        string used = users.Count == 0 ? "(no mesh or record of this plugin)" : string.Join("; ", users);
        string where = !m.NoFile ? (m.Origin == FileOrigin.NotLoaded ? $"NOT LOADED: {m.Where}" : m.Where!)
            : m.Compiled ? (m.DatabaseOrigin == FileOrigin.NotLoaded ? "NOT LOADED: compiled into " : !m.DatabaseCounts ? "NOT READ BY THE GAME: compiled into " : "compiled into ") + m.Database!.Shown
            : m.Missing ? (m.Hash != null ? "MISSING: in no compiled material database" : "MISSING: no file, and in no compiled material database")
            : m.GameLacks ? "not in Data: only the game's own meshes name it, and the game ships without it"
            : "no file; not judged (a material database couldn't be read)";
        if (m.DatabaseName != null) where += $"; its name there: {m.DatabaseName}";
        string missing = m.Problem ?? string.Join(", ", m.MissingTextures.Select(t => t.Origin == FileOrigin.NotLoaded ? $"{t.Path} (only in {t.Where}, which the game doesn't load)" : t.Path));
        return $"| {Cell(m.Path)} | {Cell(where)} | {Cell(used)} | {(m.NoFile && !m.TexturesRead ? "" : N(m.Textures.Count))} | {Cell(missing)} |";
    }

    protected override void Render(ReportBody body, ReportTextOptions o)
    {
        body.RowNoun = "materials";
        int fromRecords = Rows.Count(m => m.NamedBy.Count > 0);
        var named = new List<string>();
        if (fromRecords > 0) named.Add($"{N(fromRecords)} named by its records");
        if (ByHash > 0) named.Add($"{N(ByHash)} named only by hash, in {Plural(MeshesByHash, "mesh", "meshes")}");
        string outside = "";
        if (MeshesOutside > 0)
        {
            var where = new List<string>();
            if (MeshesLoose > 0) where.Add($"{N(MeshesLoose)} loose");
            if (MeshesMasters > 0) where.Add($"{N(MeshesMasters)} in its masters' archives");
            if (MeshesOther > 0) where.Add($"{N(MeshesOther)} in other plugins' archives");
            outside = $", {Plural(MeshesOutside, "mesh", "meshes")} its records name elsewhere ({string.Join(", ", where)})";
        }
        body.Head.Add($"Materials of {Plugin}: {Plural(OwnArchives.Count, "archive")}, {Plural(Meshes, "mesh", "meshes")}{outside}, {Plural(Rows.Count, "material")}" +
            (named.Count > 0 ? $" ({string.Join("; ", named)})." : "."));
        string compiled = $"{N(Compiled)} compiled into a material database" + (Compiled > 0 ? $" ({N(CompiledInGame)} the game's, {N(Compiled - CompiledInGame)} a mod's)" : "");
        body.Head.Add($"{N(Checked)} material files read, {compiled}, {N(Missing)} missing{(NotChecked > 0 ? $", {N(NotChecked)} not judged" : "")}; " +
            $"{Plural(Rows.Sum(m => m.Textures.Count), "texture")} named{By()}, {N(MissingTextures)} missing in {Plural(MaterialsWithMissing, "material")}; {N(Unreadable)} unreadable.");
        string By()
        {
            var by = new List<string>();
            if (CompiledTextures > 0) by.Add($"{N(CompiledTextures)} by compiled materials");
            if (InheritedTextures > 0) by.Add($"{N(InheritedTextures)} inherited by material files");
            return by.Count > 0 ? $" ({string.Join(", ", by)})" : "";
        }
        var shown = o.Detail ? Rows : Rows.Where(m => m.HasProblem).ToList();
        if (shown.Count > 0)
        {
            if (!o.Detail) body.Head.Add("Materials with something missing (detail lists every material):");
            body.Head.Add("| Material | Found in | Used by | Textures | Missing textures |");
            body.Head.Add("| :--- | :--- | :--- | ---: | :--- |");
        }
        foreach (var m in shown) body.Rows.Add(Row(m));
        if (shown.Count == 0)
            body.Empty = Rows.Count == 0 ? EmptyReason
                : $"Nothing is missing: the {N(Checked)} material files name {Plural(Rows.Where(m => !m.NoFile).Sum(m => m.Textures.Count), "texture")}, each a loose file or in a loaded archive" +
                  (Compiled > 0 ? $", and {(Compiled == 1 ? "the other material is" : $"the {N(Compiled)} other materials are")} in a loaded compiled material database, naming {Plural(CompiledTextures, "texture")} found the same way" : "") + ". Detail lists every material.";
    }
}

public sealed class MaterialsReport
{
    readonly ReportSession _s;
    readonly PathWalker _walker;

    public MaterialsReport(ReportSession session)
    {
        _s = session;
        _walker = new PathWalker(session.Schema, new[] { ".mat" });
    }

    /// <summary>"Data\Textures\x.dds" in a material → "textures\x.dds", the way archives and the Data folder name it, in lower case.</summary>
    internal static string Norm(string path) => ArchiveStore.DataRelative(path).ToLowerInvariant();

    /// <summary>A material path on a record, as Data names it: under "materials\" when it isn't written so.</summary>
    internal static string RecordMaterial(string value)
    {
        string n = Norm(value);
        return n.StartsWith("materials\\", StringComparison.Ordinal) ? n : "materials\\" + n;
    }

    /// <summary>The textures a material file names: every "FileName" that ends in .dds, wherever it sits in the JSON.</summary>
    internal static List<string> TextureNames(JsonNode? material)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(JsonNode? x)
        {
            switch (x)
            {
                case JsonObject o:
                    foreach (var (k, v) in o)
                    {
                        if (k.Equals("FileName", StringComparison.OrdinalIgnoreCase) && v is JsonValue value && value.TryGetValue<string>(out var s) && s.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                            found.Add(Norm(s));
                        else Walk(v);
                    }
                    break;
                case JsonArray a: foreach (var i in a) Walk(i); break;
            }
        }
        Walk(material);
        return found.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A mesh path on a record, as Data may name it: as written, then under "meshes\" (a model "Weapons\x.nif"
    /// is the file "meshes\weapons\x.nif").</summary>
    internal static List<string> MeshCandidates(string value)
    {
        string n = Norm(value);
        return n.StartsWith("meshes\\", StringComparison.Ordinal) ? new List<string> { n } : new List<string> { n, "meshes\\" + n };
    }

    /// <summary>True when the bytes hold ".mat" in any letter case: a record worth reading for its material paths.</summary>
    static bool HoldsMat(byte[] data)
    {
        for (int i = data.AsSpan().IndexOf((byte)'.'); i >= 0 && i + 3 < data.Length; )
        {
            if ((data[i + 1] | 0x20) == 'm' && (data[i + 2] | 0x20) == 'a' && (data[i + 3] | 0x20) == 't') return true;
            int next = data.AsSpan(i + 1).IndexOf((byte)'.');
            i = next < 0 ? -1 : i + 1 + next;
        }
        return false;
    }

    /// <summary>The material paths on the plugin's records, each added to its row with the record and field that name it;
    /// and the meshes they name (<paramref name="meshes"/>, as written, in lower case): every text that ends in ".nif" in
    /// a subrecord of a record read anyway, so a record is parsed only when it holds ".mat" (S8).</summary>
    void FromRecords(PluginSide side, ReadScope scope, MaterialsAnswer answer, Func<string, MaterialRow> get, HashSet<string> meshes)
    {
        var p = side.Plugin;
        if (p.Segment == null || p.Problems.MissingMasters.Count > 0 || p.Problems.ReadError != null)
        {
            answer.Notes.Add($"{side.Shown}: its records couldn't be read for the materials they name ({(p.Problems.MissingMasters.Count > 0 ? "missing master: " + string.Join(", ", p.Problems.MissingMasters) : p.Problems.ReadError ?? "no header index")}).");
            return;
        }
        var seg = side.Segment;
        bool world = seg.Count <= ScriptsReport.WorldLimit;
        var label = _s.Compare.Labeller(side);
        scope.Touch(side.Engine, side.Name);
        for (int i = 0; i < seg.FirstUnresolved; i++)
        {
            var e = seg[i];
            if (!world && ReportSession.IsWorld(seg, e)) { answer.WorldLeftOut++; continue; }
            string sig = seg.SignatureOf(e);
            if (sig == "NAVI") continue;
            var r = new RecordRef(p.Id, i);
            answer.RecordsLooked++;
            RawRecord raw;
            try { raw = side.Engine.Raw(r); } catch (RecordException) { continue; }
            foreach (var sub in raw.Subrecords)
                foreach (var t in MaterialPaths.PathTexts(raw.Content(sub), "nif", null))
                    if (!t.Contains("$(", StringComparison.Ordinal)) meshes.Add(Norm(t));
            if (!HoldsMat(raw.Data)) continue;
            RecordView view;
            try { view = _s.Compare.View(side.Engine, r); } catch (CompareException) { continue; }
            answer.RecordsRead++;
            string key = ReportSession.Canon(view.Info.FormKey);
            _walker.Walk(view.Type, view.Json, found =>
            {
                if (PathWalker.ExtensionOf(found.Value) != ".mat" || found.Value.Contains("$(", StringComparison.Ordinal)) return;
                var row = get(RecordMaterial(found.Value));
                string by = $"{label(key)} {sig} {PathWalker.Plain(found.Path)}";
                if (!row.NamedBy.Contains(by)) row.NamedBy.Add(by);
            });
        }
    }

    /// <summary>The compiled material databases a material may be in, in the order they are looked in: the game's own
    /// first (so that a material the game has is said to be the game's, even when a mod's database has it too), then
    /// the archives in the finder's order (the plugin's own, its masters', other loaded plugins', not loaded), then
    /// the databases the game doesn't read (<see cref="MaterialDatabase.GameReads"/>: loose in Data, or not in a
    /// " - Main" archive). Each is read when first looked in.</summary>
    internal static List<(string? Archive, string Path, FileOrigin Origin, bool Counts)> Databases(ArchiveStore store, FileFinder finder)
    {
        var list = new List<(string? Archive, string Path, FileOrigin Origin, bool Counts)>();
        foreach (var (path, origin) in finder.Order)
            foreach (var cdb in store.Names(path).Databases)
                list.Add((path, cdb, origin, MaterialDatabase.GameReads(cdb, path)));
        foreach (var loose in store.Loose.Where(f => f.EndsWith(".cdb", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            list.Add((null, loose, FileOrigin.Loose, false));
        bool Game((string? Archive, string Path, FileOrigin Origin, bool Counts) d) => MaterialDatabase.IsGamePath(d.Path, d.Archive);
        return list.Where(Game).Concat(list.Where(d => !Game(d) && d.Counts)).Concat(list.Where(d => !Game(d) && !d.Counts)).ToList();
    }

    /// <summary>The textures of the materials compiled into a database: the database is read whole (once per process),
    /// each material's parts followed to the textures they name, and each texture looked for as a file. A mod's
    /// material may inherit from one of the game's, so the game's database is read whole too when a mod's names a
    /// parent there.</summary>
    static void CompiledTextures(ArchiveStore store, FileFinder finder, MaterialsAnswer answer, List<MaterialRow> compiled, Func<string, bool> gameNames)
    {
        if (compiled.Count == 0) return;
        var gameDb = answer.DatabasesRead.Select(d => d.Db).FirstOrDefault(d => d.IsGame && d.Error == null);
        MaterialContents? game = null;
        bool gameTried = false;
        MaterialContents? Game()
        {
            if (!gameTried && gameDb != null) { gameTried = true; game = store.Contents(gameDb); }
            return game;
        }
        var failed = new List<MaterialDatabase>();
        int unresolved = 0, rowsUnresolved = 0;
        foreach (var m in compiled.OrderBy(m => m.Database!.IsGame ? 0 : 1))
        {
            var db = m.Database!;
            var contents = db.IsGame ? Game() : store.Contents(db);
            if (contents == null) { if (!failed.Contains(db)) failed.Add(db); continue; }
            List<string>? names;
            int lost = 0;
            if (m.Hash is { } h)
            {
                var node = contents.Find(h.Dir, h.File, 0x0074616D);
                names = node == null ? null : contents.TexturesOf(node.Value, contents.HasForeignParents ? Game() : null, out lost);
                if (node != null) m.DatabaseName = node.Value.OwnName;
            }
            else names = contents.TexturesOf(m.Path, contents.HasForeignParents ? Game() : null, out lost);
            if (names == null) { if (!failed.Contains(db)) failed.Add(db); continue; }
            m.TexturesRead = true;
            m.Unresolved = lost;
            if (lost > 0) { unresolved += lost; rowsUnresolved++; }
            foreach (var name in names.Select(Norm).Distinct().OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
            {
                var t = finder.Find(name);
                m.Textures.Add(new MaterialTexture { Path = name, Origin = t.Origin, Where = t.Where, GameLacks = t.Origin == FileOrigin.None && (db.IsGame || gameNames(name)) });
            }
        }
        foreach (var db in failed)
            answer.Notes.Add($"The textures of the materials compiled into {db.Shown} weren't checked: {db.ContentsError ?? "the database's contents don't hold a material its list holds"}.");
        if (rowsUnresolved > 0)
            answer.Notes.Add($"{Plural(rowsUnresolved, "compiled material")} {(rowsUnresolved == 1 ? "has" : "have")} {Plural(unresolved, "link")} in {(rowsUnresolved == 1 ? "its" : "their")} database that {(unresolved == 1 ? "leads" : "lead")} nowhere (a part or a parent that isn't there): a texture may be left out.");
    }

    public MaterialsAnswer Materials(string plugin, MaterialsOptions? options = null)
    {
        options ??= new MaterialsOptions();
        return _s.Run(scope =>
        {
            // A plugin is named by a file in Data, by a path, or only by a name whose archives are in Data
            ArchiveTarget own;
            IReadOnlyList<string> masters = Array.Empty<string>();
            string shown;
            PluginSide? side = null;
            try { side = _s.Side(plugin); } catch (ReportException) when (plugin.IndexOfAny(new[] { '\\', '/' }) < 0) { }
            if (side != null)
            {
                own = ArchiveReport.Resolve(_s, side.InData ? side.Name : side.Path, options.Beside);
                masters = side.Masters; shown = side.Shown;
            }
            else { own = ArchiveReport.Resolve(_s, plugin, options.Beside); shown = own.Shown; }
            var store = _s.Archives;
            var finder = new FileFinder(_s, masters, own);
            var answer = new MaterialsAnswer { Plugin = shown };
            foreach (var n in own.Notes) answer.Notes.Add(n);
            if (side != null) ReportSession.AddSource(answer, side);
            else answer.Notes.Add($"There is no plugin named {plugin} in Data: its archives were read by its name, its records weren't read, and its masters' archives couldn't be told from other plugins'.");
            foreach (var a in own.Archives) answer.OwnArchives.Add(a.Name);
            foreach (var line in own.SourceLines("archives of its own")) answer.Sources.Add(line);
            answer.Sources.Add(finder.SourceLine());
            answer.Sources.Add(_s.Compare.LoadOrderLine());

            // The plugin's own meshes and material files
            var mats = new Dictionary<string, MaterialRow>(StringComparer.OrdinalIgnoreCase);
            MaterialRow Get(string path) => mats.TryGetValue(path, out var m) ? m : mats[path] = new MaterialRow { Path = path };
            long meshBytes = 0;
            var hashed = new Dictionary<(uint Dir, uint File), List<(string User, bool Own, bool Game)>>();
            var bare = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // A mesh's materials, each added to its row with the mesh as its user ("meshes\x.nif", with where it was
            // read when that isn't the plugin's own archives)
            void FromMesh(byte[] bytes, string user, bool ownOrLoose, bool gameMesh = false)
            {
                meshBytes += bytes.Length;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var names = MeshMaterialNames.Read(bytes);
                if (!names.HeaderRead) answer.MeshesHeaderUnread++;
                if (names.Unused.Count > 0)
                {
                    answer.NamesUnused += names.Unused.Count; answer.MeshesWithUnused++;
                    if (answer.UnusedExamples.Count < 3) answer.UnusedExamples.Add($"{names.Unused[0]} in {user}");
                }
                foreach (var text in names.Texts)
                {
                    // A shader's name that isn't written under "materials\" is looked for there, like a record's
                    // material path: the files of the meshes that write it so are there (bublumi - main.ba2:
                    // "BubLumi\bub_lumi_barrels.mat" is materials\bublumi\bub_lumi_barrels.mat). None of the game's
                    // own meshes writes one so; that the game looks there isn't shown from its code.
                    string mat = Norm(text);
                    if (!mat.StartsWith("materials\\", StringComparison.Ordinal))
                    {
                        mat = RecordMaterial(text);
                        if (bare.Add(mat) && answer.BareExamples.Count < 3) answer.BareExamples.Add($"{text.Trim()} in {user}");
                    }
                    if (seen.Add(mat)) { var row = Get(mat); row.UsedBy.Add(user); row.FromMeshes = true; row.UsedFrom(ownOrLoose, gameMesh); }
                }
                if (names.Hashed.Count > 0) answer.MeshesByHash++;
                foreach (var id in names.Hashed)
                {
                    if (!hashed.TryGetValue(id, out var users)) hashed[id] = users = new List<(string, bool, bool)>();
                    users.Add((user, ownOrLoose, gameMesh));
                }
            }
            foreach (var stamp in own.Archives)
            {
                var a = store.Open(stamp.Path);
                if (a.Error != null) { answer.Notes.Add($"{a.Name} couldn't be read: {a.Error}."); continue; }
                foreach (var f in a.Files)
                {
                    string ext = f.Extension;
                    if (ext == ".nif")
                    {
                        answer.Meshes++;
                        // A loose file at the same path is the one the game uses
                        bool loose = store.IsLoose(f.Path);
                        byte[] bytes;
                        try { bytes = loose ? File.ReadAllBytes(Path.Combine(store.DataFolder, f.Path)) : f.Bytes(); }
                        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { answer.Notes.Add($"{f.Path} couldn't be read from {(loose ? "its loose file" : a.Name)}: {e.Message}."); continue; }
                        if (loose) answer.MeshesLooseOverOwn++;
                        FromMesh(bytes, loose ? $"{f.Path} (loose)" : f.Path, true);
                    }
                    else if (ext == ".mat") { var row = Get(Norm(f.Path)); row.FromMeshes = true; row.UsedFrom(true); }
                    else if (ext == ".cdb") answer.CompiledDatabases.Add($"{f.Path} ({a.Name})");
                }
            }

            // The materials its records name, and the meshes they name outside its own archives: loose, a master's (the
            // game's own too), another plugin's (S8). Each is read once, from where the game takes it.
            var namedMeshes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (side != null) FromRecords(side, scope, answer, Get, namedMeshes);
            answer.MeshesNamed = namedMeshes.Count;
            var readOutside = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mesh in namedMeshes.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
            {
                var hit = finder.Find(MeshCandidates(mesh));
                if (!hit.Found) { answer.MeshesNotFound.Add(mesh); continue; }
                if (hit.Origin == FileOrigin.Own || !readOutside.Add(hit.FoundAs!)) continue; // read above, or already
                byte[] bytes;
                try { bytes = finder.Read(hit); }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { answer.Notes.Add($"{hit.FoundAs} couldn't be read from {hit.Where}: {e.Message}."); continue; }
                if (hit.Origin == FileOrigin.Loose) answer.MeshesLoose++;
                else if (hit.Origin == FileOrigin.Master) answer.MeshesMasters++;
                else answer.MeshesOther++;
                FromMesh(bytes, $"{hit.FoundAs} ({hit.Where})", hit.Origin == FileOrigin.Loose,
                    gameMesh: hit.Origin != FileOrigin.Loose && hit.Where != null && hit.Where.StartsWith("Starfield - ", StringComparison.OrdinalIgnoreCase));
            }

            // The materials meshes name by hash: the path a mesh or record of the plugin writes when the hashes agree,
            // else a row of their own, listed by the hashes
            if (hashed.Count > 0)
            {
                var byId = new Dictionary<(uint, uint), MaterialRow>();
                foreach (var row in mats.Values)
                {
                    var (file, ext, dir) = MaterialDatabase.ResourceId(row.Path);
                    if (ext == 0x0074616D) byId.TryAdd((dir, file), row);
                }
                foreach (var (id, users) in hashed)
                {
                    if (!byId.TryGetValue(id, out var row)) mats[MaterialRow.HashText(id.Dir, id.File)] = row = new MaterialRow { Path = MaterialRow.HashText(id.Dir, id.File), Hash = id };
                    row.FromMeshes = true;
                    foreach (var (u, ownUse, gameUse) in users) { if (!row.UsedBy.Contains(u, StringComparer.OrdinalIgnoreCase)) row.UsedBy.Add(u); row.UsedFrom(ownUse, gameUse); }
                }
            }
            answer.NamesBare = bare.Count;
            if (bare.Count > 0)
                answer.Notes.Add($"{Plural(bare.Count, "material")} {(bare.Count == 1 ? "is" : "are")} named by a mesh without the materials\\ folder ({string.Join("; ", answer.BareExamples)}{(bare.Count > answer.BareExamples.Count ? "; …" : "")}): each is looked for under materials\\, as a record's material path is. None of the game's own meshes names a material so.");
            if (answer.NamesUnused > 0)
                answer.Notes.Add($"{Plural(answer.NamesUnused, "material name")} in the string {(answer.MeshesWithUnused == 1 ? "table" : "tables")} of {Plural(answer.MeshesWithUnused, "mesh", "meshes")} {(answer.NamesUnused == 1 ? "is" : "are")} the name of no shader of the mesh (left over from editing: the game takes a mesh's materials from its shaders' names): not listed, and not looked for ({string.Join("; ", answer.UnusedExamples)}{(answer.NamesUnused > answer.UnusedExamples.Count ? "; …" : "")}).");
            if (answer.MeshesHeaderUnread > 0)
                answer.Notes.Add($"The header of {Plural(answer.MeshesHeaderUnread, "mesh", "meshes")} couldn't be read (not a Starfield mesh of version 20.2.0.7): {(answer.MeshesHeaderUnread == 1 ? "its" : "their")} material texts were looked for in the whole file, and materials named by hash weren't looked for.");

            // The compiled material databases a material may be in, each read when first looked in
            List<(string? Archive, string Path, FileOrigin Origin, bool Counts)>? candidates = null;
            var read = Array.Empty<MaterialDatabase?>();
            List<(string? Archive, string Path, FileOrigin Origin, bool Counts)> Candidates()
            {
                if (candidates == null) { candidates = Databases(store, finder); read = new MaterialDatabase?[candidates.Count]; }
                return candidates;
            }
            MaterialDatabase Db(int i)
            {
                var (archive, path, origin, _) = Candidates()[i];
                if (read[i] is { } have) return have;
                MaterialDatabase db;
                try { db = store.Database(archive, path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    db = MaterialDatabase.Failed(path, archive == null ? "loose" : Path.GetFileName(archive), archive ?? Path.Combine(store.DataFolder, path), e.Message);
                }
                answer.DatabasesRead.Add((db, origin));
                return read[i] = db;
            }
            // What a material file inherits: the game's database read whole, and a mod's the game reads when the game's
            // doesn't hold a parent or a part
            var wholeForFiles = new HashSet<MaterialDatabase>();
            MaterialContents? gameContents = null;
            bool gameTried = false;
            MaterialContents? GameContents()
            {
                if (gameTried) return gameContents;
                gameTried = true;
                int i = Candidates().FindIndex(d => MaterialDatabase.IsGamePath(d.Path, d.Archive));
                if (i < 0) return null;
                var db = Db(i);
                if (db.Error == null) gameContents = store.Contents(db);
                return gameContents;
            }
            IEnumerable<MaterialContents> OtherContents()
            {
                var all = Candidates();
                for (int i = 0; i < all.Count; i++)
                {
                    if (!all[i].Counts || all[i].Origin == FileOrigin.NotLoaded || MaterialDatabase.IsGamePath(all[i].Path, all[i].Archive)) continue;
                    var db = Db(i);
                    if (db.Error != null || store.Contents(db) is not { } contents) continue;
                    wholeForFiles.Add(db);
                    yield return contents;
                }
            }
            // A texture nowhere in Data that the game's own compiled materials name too: the game's gap (asked only
            // for a texture that is nowhere)
            HashSet<string>? gameTextures = null;
            bool GameNames(string texture)
            {
                if (GameContents() is not { } contents) return false;
                gameTextures ??= contents.TextureNames().Select(Norm).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return gameTextures.Contains(texture);
            }
            var jsonOptions = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
            var matFiles = new Dictionary<string, MaterialFile?>(StringComparer.OrdinalIgnoreCase);
            MaterialFile? LoadFile(string path)
            {
                if (matFiles.TryGetValue(path, out var have)) return have;
                MaterialFile? file = null;
                var at = finder.Find(path);
                if (at.Origin != FileOrigin.None) // one only in an archive the game doesn't load is read, as a material's own file is
                    try { file = MaterialFile.Read(JsonNode.Parse(finder.Read(at), documentOptions: jsonOptions), path); }
                    catch (Exception e) when (e is JsonException or IOException or InvalidDataException or UnauthorizedAccessException) { }
                return matFiles[path] = file;
            }
            var fileWalk = new MaterialFileWalk(LoadFile, GameContents, OtherContents);

            // Each material: where it is, and when it is a file, its textures
            var noFile = new List<MaterialRow>();
            foreach (var m in mats.Values.OrderBy(m => m.Hash != null).ThenBy(m => m.Path, StringComparer.OrdinalIgnoreCase))
            {
                answer.Rows.Add(m);
                if (m.Hash != null) { noFile.Add(m); continue; } // a database knows it by these hashes; a file can't be told by them
                var hit = finder.Find(m.Path);
                if (hit.Origin == FileOrigin.None) { noFile.Add(m); continue; }
                m.Origin = hit.Origin; m.Where = hit.Where;
                JsonNode? json;
                try { json = JsonNode.Parse(finder.Read(hit), documentOptions: jsonOptions); }
                catch (JsonException) { m.Problem = "not readable as a material (bad JSON)"; continue; }
                catch (IOException e) { m.Problem = "couldn't be read: " + e.Message; continue; }
                if (!matFiles.TryGetValue(m.Path, out var file) || file == null) matFiles[m.Path] = file = MaterialFile.Read(json, m.Path);
                if (file?.Root == null)
                {
                    // No list of objects: every texture the JSON names
                    foreach (var tex in TextureNames(json))
                    {
                        var t = finder.Find(tex);
                        m.Textures.Add(new MaterialTexture { Path = tex, Origin = t.Origin, Where = t.Where, GameLacks = t.Origin == FileOrigin.None && GameNames(tex) });
                    }
                    continue;
                }
                // Its textures: those its objects name, and those they inherit (MaterialFile.cs)
                var uses = fileWalk.Textures(file);
                var textures = new SortedDictionary<string, (bool Inherited, bool Game)>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, inherited, game) in uses.Textures)
                {
                    if (!inherited && !name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) { m.NotDds.Add(name); continue; }
                    string tex = Norm(name);
                    if (!textures.TryGetValue(tex, out var have) || (have.Inherited && !inherited)) textures[tex] = (inherited, game);
                }
                foreach (var (tex, (inherited, game)) in textures)
                {
                    var t = finder.Find(tex);
                    m.Textures.Add(new MaterialTexture { Path = tex, Origin = t.Origin, Where = t.Where, Inherited = inherited, GameLacks = t.Origin == FileOrigin.None && (game || GameNames(tex)) });
                }
                m.NotFound.AddRange(uses.Lost);
                m.Unresolved = uses.Lost.Count + uses.LostInDatabase;
                m.InheritsFromDatabase = uses.UsedDatabase;
            }
            var filesLost = answer.Rows.Where(m => m.NotFound.Count > 0).ToList();
            if (filesLost.Count > 0)
            {
                var names = filesLost.SelectMany(m => m.NotFound).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                answer.Notes.Add(gameTried && gameContents == null
                    ? $"The game's compiled material database isn't in Data, or couldn't be read: what {Plural(filesLost.Count, "material file")} {(filesLost.Count == 1 ? "inherits" : "inherit")} from it wasn't followed, so a texture may be left out."
                    : $"{Plural(filesLost.Count, "material file")} {(filesLost.Count == 1 ? "names" : "name")} a parent or a part that is in no material file and no compiled material database the game reads ({string.Join(", ", names.Take(3))}{(names.Count > 3 ? ", …" : "")}): what {(filesLost.Count == 1 ? "it" : "they")} would inherit from there isn't known, so a texture may be left out.");
            }
            int notDds = answer.Rows.Sum(m => m.NotDds.Count);
            if (notDds > 0)
                answer.Notes.Add($"{Plural(notDds, "texture name")} of {Plural(answer.Rows.Count(m => m.NotDds.Count > 0), "material file")} {(notDds == 1 ? "isn't" : "aren't")} a .dds file ({string.Join(", ", answer.Rows.SelectMany(m => m.NotDds).Distinct(StringComparer.OrdinalIgnoreCase).Take(3))}{(notDds > 3 ? ", …" : "")}): not looked for.");

            // A material that is no file: which compiled material database holds it. Each database is read when first
            // looked in; a material is looked for in the next only when the ones before don't hold it.
            if (noFile.Count > 0)
            {
                var all = Candidates();
                foreach (var m in noFile)
                {
                    for (int i = 0; i < all.Count && m.Database == null; i++)
                    {
                        var db = Db(i);
                        if (db.Error == null && (m.Hash is { } h ? db.Has(h.File, h.Dir) : db.Has(m.Path))) { m.Database = db; m.DatabaseOrigin = all[i].Origin; m.DatabaseCounts = all[i].Counts; }
                    }
                    m.Missing = m.Database == null && answer.DatabasesRead.All(d => d.Db.Error == null);
                    // Only the game's own meshes name it, and nothing holds it: the game's gap, not the plugin's
                    if (m.Missing && m.OnlyGameMeshes) { m.Missing = false; m.GameLacks = true; }
                }
                if (all.Count == 0)
                    answer.Notes.Add($"No compiled material database (.cdb) is in Data, loose or in an archive: the game's own is materials\\materialsbeta.cdb in Starfield - Materials.ba2. The {Plural(noFile.Count, "material")} that {(noFile.Count == 1 ? "is" : "are")} no file can't be judged.");
                foreach (var (db, _) in answer.DatabasesRead.Where(d => d.Db.Error != null))
                    answer.Notes.Add($"{db.Path} in {db.Where} couldn't be read as a compiled material database: {db.Error}. A material that is no file and in no other database is counted as not judged, not missing.");
                // The databases that hold a material of the plugin get a line each; the others are counted
                var holding = noFile.Where(m => m.Database != null).Select(m => m.Database!).ToHashSet();
                foreach (var (db, origin) in answer.DatabasesRead.Where(d => d.Db.Error == null && holding.Contains(d.Db)))
                    answer.Sources.Add($"{db.Shown}: {N(db.Count)} materials{(db.BuildVersion != null ? $", compiled by {db.BuildVersion}" : "")}{(origin == FileOrigin.NotLoaded ? "; the game doesn't load this archive" : "")}");
                int none = answer.DatabasesRead.Count(d => d.Db.Error == null && !holding.Contains(d.Db));
                if (none > 0)
                    answer.Sources.Add($"{Plural(none, "other compiled material database")} looked in, holding none of these materials: {string.Join(", ", answer.DatabasesRead.Where(d => d.Db.Error == null && !holding.Contains(d.Db)).Select(d => d.Db.Where).Take(6))}{(none > 6 ? ", …" : "")}");
                if (all.Count > 0)
                    answer.Sources.Add("the databases the game reads: its own materials\\materialsbeta.cdb and every .cdb in a plugin's \" - Main\" archives, as the game's code reads them (a .cdb loose in Data or in another archive isn't read)");
                CompiledTextures(store, finder, answer, noFile.Where(m => m.Compiled).ToList(), GameNames);
            }

            if (own.Archives.Count == 0 && answer.Rows.Count == 0)
                answer.EmptyReason = $"{shown} has no archives: no file named \"{Path.GetFileNameWithoutExtension(own.Plugin ?? plugin)} - <part>.ba2\" is in {store.DataFolder}, and none of its records names a material or a mesh that names one. This report reads the meshes in a plugin's own archives and the meshes its records name, wherever they are.";
            else if (answer.Rows.Count == 0)
                answer.EmptyReason = $"The {Plural(own.Archives.Count, "archive")} of {shown} {(own.Archives.Count == 1 ? "holds" : "hold")} {Plural(answer.Meshes, "mesh", "meshes")} and no material file, no mesh names a material, and no record names one or a mesh that does.";
            else if (own.Archives.Count == 0)
                answer.Notes.Add($"{shown} has no archives: only the materials its records name, and those of the meshes they name, are listed.");
            if (answer.MaterialsGameLacks > 0)
            {
                var lacking = answer.Rows.Where(m => m.GameLacks).ToList();
                answer.Notes.Add($"{Plural(lacking.Count, "material")} {(lacking.Count == 1 ? "is" : "are")} named only by the game's own meshes ({string.Join(", ", lacking.SelectMany(m => m.UsedBy).Distinct(StringComparer.OrdinalIgnoreCase).Take(2))}{(lacking.SelectMany(m => m.UsedBy).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 2 ? ", …" : "")}) and {(lacking.Count == 1 ? "is" : "are")} in no file and no compiled material database: the game ships {(lacking.Count == 1 ? "that mesh without it" : "those meshes without them")}. Not counted as missing ({string.Join(", ", lacking.Select(m => m.Path).Take(3))}{(lacking.Count > 3 ? ", …" : "")}).");
            }
            if (answer.Missing > 0)
                answer.Notes.Add($"{Plural(answer.Missing, "material")} {(answer.Missing == 1 ? "is" : "are")} MISSING: no loose file, no archive and none of the {Plural(answer.DatabasesRead.Count, "compiled material database")} in Data holds {(answer.Missing == 1 ? "it" : "them")}. " +
                    "A database knows a material by a hash of its path, so this is a lookup by the path the mesh or record writes" +
                    (answer.Rows.Any(m => m.Missing && m.Hash != null) ? "; a material a mesh names only by hash is looked up by those hashes, and no .mat file is matched to them." : "."));
            int compiledOther = answer.Rows.Count(m => m.Compiled && m.DatabaseOrigin == FileOrigin.OtherLoaded);
            if (compiledOther > 0)
                answer.Notes.Add($"{Plural(compiledOther, "material")} {(compiledOther == 1 ? "is" : "are")} only in the compiled database of a loaded plugin that isn't a master of {shown} ({string.Join(", ", answer.Rows.Where(m => m.Compiled && m.DatabaseOrigin == FileOrigin.OtherLoaded).Select(m => m.Database!.Where).Distinct(StringComparer.OrdinalIgnoreCase).Take(5))}). {(compiledOther == 1 ? "It is" : "They are")} there while that plugin stays installed and loaded.");
            int notRead = answer.Rows.Count(m => m.Compiled && !m.DatabaseCounts);
            if (notRead > 0)
                answer.Notes.Add($"{Plural(notRead, "material")} {(notRead == 1 ? "is" : "are")} only in a compiled material database the game doesn't read ({string.Join(", ", answer.Rows.Where(m => m.Compiled && !m.DatabaseCounts).Select(m => m.Database!.Shown).Distinct(StringComparer.OrdinalIgnoreCase).Take(3))}): the game reads its own and those in a plugin's \" - Main\" archives. Counted as a problem.");
            if (answer.GameLacks > 0)
                answer.Notes.Add($"{Plural(answer.GameLacks, "texture")} the game's own compiled materials name {(answer.GameLacks == 1 ? "isn't" : "aren't")} in Data, loose or in any archive: the game ships without {(answer.GameLacks == 1 ? "it" : "them")}. Not counted as missing ({string.Join(", ", answer.Rows.SelectMany(m => m.Textures.Where(t => t.GameLacks).Select(t => t.Path)).Distinct().Take(3))}{(answer.GameLacks > 3 ? ", …" : "")}).");
            int notLoaded = answer.Rows.Sum(m => m.Textures.Count(t => t.Origin == FileOrigin.NotLoaded)) + answer.Rows.Count(m => m.Origin == FileOrigin.NotLoaded) + answer.Rows.Count(m => m.Compiled && m.DatabaseOrigin == FileOrigin.NotLoaded);
            if (notLoaded > 0) answer.Notes.Add($"{Plural(notLoaded, "file")} {(notLoaded == 1 ? "is" : "are")} only in an archive the game doesn't load (its plugin isn't in the load order, or isn't in Data): counted as missing.");
            int other = answer.Rows.Sum(m => m.Textures.Count(t => t.Origin == FileOrigin.OtherLoaded));
            if (other > 0) answer.Notes.Add($"{Plural(other, "texture")} {(other == 1 ? "is" : "are")} found only in archives of loaded plugins that aren't masters of {shown}.");
            if (answer.WorldLeftOut > 0) answer.Notes.Add($"{Plural(answer.WorldLeftOut, "world record")} (cells and what they hold) {(answer.WorldLeftOut == 1 ? "was" : "were")} left out: a plugin of more than {N(ScriptsReport.WorldLimit)} records is read without them.");
            if (side != null) ReportSession.AddProblemNotes(answer, side.Plugin);
            answer.Read.Add($"{Plural(answer.Meshes + answer.MeshesOutside, "mesh", "meshes")} ({ArchiveStore.SizeText(meshBytes)}) and {Plural(answer.Checked, "material file")}");
            if (side != null) answer.Read.Add($"{N(answer.RecordsLooked)} records' bytes for the meshes they name, {N(answer.RecordsRead)} of them read whole (those whose bytes hold \".mat\")");
            if (answer.MeshesNamedMissing > 0)
                answer.Notes.Add($"{Plural(answer.MeshesNamedMissing, "mesh", "meshes")} its records name {(answer.MeshesNamedMissing == 1 ? "is" : "are")} in no loose file and no loaded archive, so {(answer.MeshesNamedMissing == 1 ? "its" : "their")} materials aren't known ({string.Join(", ", answer.MeshesNotFound.Take(3))}{(answer.MeshesNamedMissing > 3 ? ", …" : "")}; the assets report lists {(answer.MeshesNamedMissing == 1 ? "it" : "them")} with the records that name {(answer.MeshesNamedMissing == 1 ? "it" : "them")}).");
            if (answer.MeshesLooseOverOwn > 0)
                answer.Notes.Add($"{Plural(answer.MeshesLooseOverOwn, "mesh", "meshes")} of its own archives {(answer.MeshesLooseOverOwn == 1 ? "is" : "are")} also a loose file in Data: the loose {(answer.MeshesLooseOverOwn == 1 ? "file was" : "files were")} read, as the game uses {(answer.MeshesLooseOverOwn == 1 ? "it" : "them")}.");
            answer.Read.Add($"the file names of {Plural(store.NamesUsed, "archive")}{(store.ListsRead < store.NamesUsed ? $" ({N(store.NamesUsed - store.ListsRead)} from memory)" : "")}");
            if (answer.DatabasesRead.Count > 0)
            {
                // What reading the lists cost: the game's database by itself, the others together
                static string Cost(IReadOnlyCollection<MaterialDatabase> dbs) =>
                    $"{ArchiveStore.SizeText(dbs.Sum(d => d.BytesRead))} of {ArchiveStore.SizeText(dbs.Sum(d => d.Size))} in {dbs.Sum(d => d.Seconds).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} s, {ArchiveStore.SizeText(dbs.Sum(d => d.Allocated))} allocated";
                var now = answer.DatabasesRead.Where(d => d.Db.Error == null && store.DatabasesRead.Contains(d.Db.Container + "|" + d.Db.Path)).Select(d => d.Db).ToList();
                var parts = new List<string>();
                var game = now.Where(d => d.IsGame).ToList();
                var mods = now.Where(d => !d.IsGame).ToList();
                if (game.Count > 0) parts.Add($"the game's: {Cost(game)}");
                if (mods.Count > 0) parts.Add($"{Plural(mods.Count, "mod database")}: {Cost(mods)}");
                int kept = answer.DatabasesRead.Count(d => d.Db.Error == null) - now.Count;
                if (kept > 0) parts.Add($"{N(kept)} taken from memory");
                answer.Read.Add($"the material lists of {Plural(answer.DatabasesRead.Count, "compiled material database")}" + (parts.Count > 0 ? $" ({string.Join("; ", parts)})" : ""));
                // And reading the whole of those that hold a compiled material, for its textures
                var whole = answer.DatabasesRead.Select(d => d.Db).Where(d => d.Contents != null && (d.IsGame || wholeForFiles.Contains(d) || answer.Rows.Any(m => m.TexturesRead && m.Database == d))).Distinct().ToList();
                int inheriting = answer.Rows.Count(m => m.InheritsFromDatabase);
                string forWhat = (answer.Compiled > 0 || inheriting == 0 ? $"the textures of {Plural(answer.Compiled, "compiled material")}" : "")
                    + (inheriting > 0 ? $"{(answer.Compiled > 0 ? " and " : "")}what {Plural(inheriting, "material file")} {(inheriting == 1 ? "inherits" : "inherit")}" : "");
                if (whole.Count > 0)
                {
                    var wholeNow = whole.Where(d => store.ContentsRead.Contains(d.Container + "|" + d.Path)).ToList();
                    string Whole(MaterialDatabase d) => $"{(d.IsGame ? "the game's" : d.Where)}: {ArchiveStore.SizeText(d.Contents!.BytesRead)} in {d.Contents.Seconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} s, {ArchiveStore.SizeText(d.Contents.Allocated)} allocated, {ArchiveStore.SizeText(d.Contents.Kept)} kept";
                    var wparts = wholeNow.Select(Whole).ToList();
                    if (whole.Count > wholeNow.Count) wparts.Add($"{N(whole.Count - wholeNow.Count)} taken from memory");
                    answer.Read.Add($"the whole of {Plural(whole.Count, "compiled material database")}, for {forWhat} ({string.Join("; ", wparts)})");
                    if (ArchiveStore.CompactAfterWholeRead(wholeNow.Count) is { } given) answer.Read.Add(given);
                }
            }
            return answer;
        });
    }
}
