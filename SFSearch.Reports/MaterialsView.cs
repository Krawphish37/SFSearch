// materials2 --material and --texture (GAPS S33): what one material is made of, and which compiled materials use a
// texture. Nothing is stored; the databases read whole stay in memory, as for the materials
// report, until no view has used them for 5 minutes (ArchiveStore.IdleRelease, S33e).
//  - A material is looked for at its path as the materials report looks for one: a .mat file (loose in Data, or in an
//    archive), and every compiled material database in Data (the game's, the mods', MaterialsReport.Databases). Each
//    holder is said, with whether the game reads that database (MaterialDatabase.GameReads).
//  - A compiled material's parts come from its database read whole (MaterialContents): the material, its layers, each
//    layer's material and texture set and UV stream, its blenders and LOD materials, each with what it inherits from
//    its parent and the name the format gives it (its BSComponentDB::CTName). Each texture is looked for as a file, as
//    in the materials report.
//  - A .mat file (JSON) is shown the same way: its objects, linked by their IDs. What an object doesn't set itself
//    comes from its parent (an object of the game's database, named by its "res:" ID, or for the material, the parent
//    material's path) and is shown as inherited.
//  - Texture slots: a texture set's textures are BSMaterial::MRTextureFile components, one an index. The name of each
//    index is the game's own: the enum BSMaterial::MRTextureChannel, read from the code that registers it in
//    Starfield.exe and CreationKit.exe (notes\fix-s33-rest.md). It agrees with the 14 names the .mat files' own
//    "Summary" gives (notes\fix-f10-material-parts.md). An index past the enum's 21 values is shown by its number.
//  - A database holds no paths, only hashes of them. A material it names (a parent, a material that uses a texture) is
//    given a path when its name is its file's name (the hash agrees) and its folder is one of those tried: the asked
//    material's, the folders of the database's textures with textures\ read as materials\, and materials\layered\root
//    and materials\layered\shadermodels (the folders .mat files name their parents in most). The game's database:
//    48,740 of its 48,749 materials are named after their file, and 39,434 get a path this way. The others are shown
//    by name and ID, with a line that says how many and how to get them: --all-paths (AllPaths) gathers what Data
//    names (MaterialPaths, S33a; with the plugins' records, S33g: 48,629 told). What a gather found is kept in the scratch folder, and once it is there
//    (the service gathers it once in its indexer lane, after the header index) every call uses it.
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using SFSearch.Compare;

namespace SFSearch.Reports;

using static ReportAnswer;

/// <summary>A texture of a material's part.</summary>
public sealed class PartTexture
{
    /// <summary>"Albedo (slot 0)", or the component that names it ("FlowSettingsComponent").</summary>
    public required string Slot { get; init; }
    public required string Class { get; init; }
    public int Index { get; init; }
    /// <summary>The path as Data names it, in lower case; "" for a slot set to no file.</summary>
    public required string Path { get; init; }
    public FileOrigin Origin { get; init; }
    public string? Where { get; init; }
    public bool Missing => Path.Length > 0 && Origin is FileOrigin.None or FileOrigin.NotLoaded;
    /// <summary>A texture of the game's own material that the game doesn't ship either: not counted as missing.</summary>
    public bool GameLacks { get; init; }
    /// <summary>In a .mat file: a texture the file doesn't set, from its parent.</summary>
    public bool Inherited { get; init; }
    /// <summary>The Creation Kit's name for the slot (its display name of the MRTextureChannel member, or of the
    /// component's class), when the schema has the CK's names and the name isn't the engine's (to-do F30). Null otherwise.</summary>
    public string? CkName { get; init; }
}

/// <summary>A material, or one of its parts.</summary>
public sealed class MaterialPart
{
    /// <summary>"material", "layer", "blender", "texture set", "UV stream", "LOD material", ...</summary>
    public required string Kind { get; init; }
    public int Index { get; init; }
    public string? Name { get; set; }
    /// <summary>"res:DIR:FILE:EXT", as a .mat file writes an object's ID. Null for the material of a .mat file.</summary>
    public string? Id { get; set; }
    /// <summary>What it is made from: its parent's name, its path when the parent is a material, its ID.</summary>
    public string? Parent { get; set; }
    public string? ParentPath { get; set; }
    public string? ParentId { get; set; }
    /// <summary>It has a parent that isn't in any database read.</summary>
    public bool ParentLost { get; set; }
    /// <summary>Where it is: "the .mat file", or the database's name.</summary>
    public string? From { get; set; }
    /// <summary>Reached a second time: its parts are shown the first time.</summary>
    public bool ShownAbove { get; set; }
    /// <summary>A link to a part that isn't there.</summary>
    public bool Lost { get; set; }
    /// <summary>In a .mat file: a part the file doesn't name, from its parent.</summary>
    public bool Inherited { get; set; }
    public List<PartTexture> Textures { get; } = new();
    public List<MaterialPart> Parts { get; } = new();

    public IEnumerable<MaterialPart> All() => new[] { this }.Concat(Parts.SelectMany(p => p.All()));

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["kind"] = Kind, ["index"] = Index, ["name"] = Name, ["id"] = Id };
        if (Parent != null || ParentPath != null || ParentId != null) o["madeFrom"] = new JsonObject { ["name"] = Parent, ["path"] = ParentPath, ["id"] = ParentId };
        if (ParentLost) o["parentNotFound"] = true;
        if (From != null) o["in"] = From;
        if (Inherited) o["inherited"] = true;
        if (ShownAbove) o["shownAbove"] = true;
        if (Lost) o["lost"] = true;
        o["textures"] = new JsonArray(Textures.Select(t =>
        {
            var x = new JsonObject { ["slot"] = t.Slot, ["class"] = t.Class, ["index"] = t.Index, ["texture"] = t.Path };
            if (t.CkName != null) x["ckName"] = t.CkName;
            if (t.Path.Length > 0) { x["foundIn"] = t.Where; x["origin"] = t.Origin.ToString(); x["missing"] = t.Missing && !t.GameLacks; }
            if (t.GameLacks) x["gameLacks"] = true;
            if (t.Inherited) x["inherited"] = true;
            return (JsonNode)x;
        }).ToArray());
        o["parts"] = new JsonArray(Parts.Select(p => (JsonNode)p.ToJson()).ToArray());
        return o;
    }
}

/// <summary>Where a material at one path is, and what it is made of there.</summary>
public sealed class MaterialPartsAnswer : ReportAnswer
{
    public override string Kind => "material";
    /// <summary>The path asked, as Data names it, in lower case.</summary>
    public required string Material { get; init; }
    /// <summary>The plugin whose archives were looked in first, when one was named.</summary>
    public string? Plugin { get; init; }
    /// <summary>A .mat file at this path: "loose", or the archive's name. Null when there is none.</summary>
    public string? FileWhere { get; set; }
    public FileOrigin FileOrigin { get; set; }
    /// <summary>The compiled material databases that hold it, in the order they are looked in (the game's first).</summary>
    public List<(MaterialDatabase Db, FileOrigin Origin, bool GameReads)> CompiledIn { get; } = new();
    public int DatabasesLookedIn { get; set; }
    /// <summary>The material's parts: from the .mat file first, then from each database that holds it.</summary>
    public List<(string Source, MaterialPart Tree)> Trees { get; } = new();

    public bool Missing => FileWhere == null && CompiledIn.Count == 0;
    /// <summary>When more than one place holds the path (S33c): which copy the game uses, as far as the evidence goes.
    /// Null when one place holds it, or none.</summary>
    public string? GameUses { get; set; }

    static IEnumerable<PartTexture> TexturesOf(MaterialPart tree) =>
        tree.All().SelectMany(p => p.Textures).Where(t => t.Path.Length > 0).GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First());

    protected override void Fill(JsonObject json)
    {
        json["material"] = Material;
        if (Plugin != null) json["plugin"] = Plugin;
        json["file"] = FileWhere == null ? null : new JsonObject { ["foundIn"] = FileWhere, ["origin"] = FileOrigin.ToString() };
        json["compiledIn"] = new JsonArray(CompiledIn.Select(c => (JsonNode)new JsonObject
        {
            ["database"] = c.Db.Path, ["foundIn"] = c.Db.Where, ["origin"] = c.Origin.ToString(), ["game"] = c.Db.IsGame, ["gameReads"] = c.GameReads,
        }).ToArray());
        json["databasesLookedIn"] = DatabasesLookedIn;
        json["missing"] = Missing;
        json["gameUses"] = GameUses == null ? null : new JsonObject { ["answer"] = GameUses, ["settled"] = false, ["settledBy"] = "a test in the game" };
        json["parts"] = new JsonArray(Trees.Select(t =>
        {
            var tex = TexturesOf(t.Tree).ToList();
            return (JsonNode)new JsonObject
            {
                ["source"] = t.Source, ["textures"] = tex.Count, ["missingTextures"] = new JsonArray(tex.Where(x => x.Missing && !x.GameLacks).Select(x => (JsonNode)x.Path).ToArray()),
                ["material"] = t.Tree.ToJson(),
            };
        }).ToArray());
    }

    static string Where(PartTexture t) =>
        t.Path.Length == 0 ? "no file"
        : t.Origin == FileOrigin.NotLoaded ? $"{t.Path}: only in {t.Where}, which the game doesn't load"
        : t.Missing ? (t.GameLacks ? $"{t.Path}: not in Data (the game ships this material without it)" : $"{t.Path}: MISSING")
        : $"{t.Path} ({t.Where})";

    static string Label(MaterialPart p, bool root)
    {
        string name = p.Name is { Length: > 0 } n ? n : "(no name)";
        string label = root ? $"Material {name}"
            : $"{char.ToUpperInvariant(p.Kind[0])}{p.Kind[1..]}{(p.Kind is "layer" or "blender" or "LOD material" || p.Index > 0 ? " " + N(p.Index) : "")}: {(p.Lost ? "a link to a part that isn't in the database" : name)}";
        // A material's parent is its parent material: shown for the material and its LOD materials
        if ((root || p.Kind == "LOD material") && (p.Parent != null || p.ParentPath != null))
            label += $", made from {p.Parent ?? System.IO.Path.GetFileNameWithoutExtension(p.ParentPath)} ({p.ParentPath ?? "path not known: " + p.ParentId})";
        if (p.ParentLost) label += ", made from a part that isn't in any database read";
        if (p.Inherited) label += " (from its parent)";
        if (p.ShownAbove) label += " (shown above)";
        return label;
    }

    static void Lines(MaterialPart p, int depth, bool root, List<string> rows)
    {
        string indent = new(' ', depth * 2);
        rows.Add($"{indent}- {Label(p, root)}");
        foreach (var t in p.Textures) rows.Add($"{indent}  - {t.Slot}{(t.CkName != null ? $" (CK: \"{t.CkName}\")" : "")}: {Where(t)}{(t.Inherited ? " (from its parent)" : "")}");
        foreach (var c in p.Parts) Lines(c, depth + 1, false, rows);
    }

    protected override void Render(ReportBody body, ReportTextOptions o)
    {
        body.RowNoun = "lines";
        var holders = new List<string>();
        if (FileWhere != null) holders.Add(FileOrigin == FileOrigin.NotLoaded ? $"a .mat file only in {FileWhere}, which the game doesn't load" : $"a .mat file ({FileWhere})");
        var dbs = CompiledIn.Select(c => $"{c.Db.Shown}, which the game {(c.Origin == FileOrigin.NotLoaded ? "doesn't load (its plugin isn't loaded)" : c.GameReads ? "reads" : "doesn't read (it reads its own and those in a plugin's \" - Main\" archives)")}").ToList();
        if (dbs.Count == 1) holders.Add("compiled into " + dbs[0]);
        else if (dbs.Count > 1) holders.Add($"compiled into {N(dbs.Count)} databases: {string.Join("; ", dbs)}");
        body.Head.Add(Missing
            ? $"Material {Material}: MISSING. No .mat file has this path, loose or in an archive, and none of the {Plural(DatabasesLookedIn, "compiled material database")} in Data holds it."
            : $"Material {Material}: {string.Join("; ", holders)}.{(FileWhere == null ? " No .mat file has this path." : CompiledIn.Count == 0 ? $" None of the {Plural(DatabasesLookedIn, "compiled material database")} holds it." : "")}");
        if (GameUses != null) body.Head.Add($"Which copy the game uses: {GameUses}");
        foreach (var (source, tree) in Trees)
        {
            var tex = TexturesOf(tree).ToList();
            int missing = tex.Count(t => t.Missing && !t.GameLacks), lacks = tex.Count(t => t.GameLacks);
            int layers = tree.Parts.Count(p => p.Kind == "layer"), blenders = tree.Parts.Count(p => p.Kind == "blender"), lods = tree.Parts.Count(p => p.Kind == "LOD material");
            body.Rows.Add($"Its parts in {source}: {Plural(layers, "layer")}, {Plural(blenders, "blender")}, {Plural(lods, "LOD material")}; {Plural(tex.Count, "texture")}, {N(tex.Count - missing - lacks)} found, {N(missing)} missing{(lacks > 0 ? $", {N(lacks)} the game ships without" : "")}.");
            Lines(tree, 0, true, body.Rows);
        }
    }
}

/// <summary>One compiled material that uses a texture asked about.</summary>
public sealed class TextureUser
{
    /// <summary>The material's path, when it could be told (see the file's head), else null.</summary>
    public string? Path { get; init; }
    public string? Name { get; init; }
    public required string Id { get; init; }
    public required MaterialDatabase Db { get; init; }
    public bool GameReads { get; init; }
    /// <summary>"Albedo (slot 0) in Water_TextureSet4", one for each place that names the texture.</summary>
    public List<string> Uses { get; } = new();
    public List<string> Textures { get; } = new();
    /// <summary>A .mat file has the same path too (S33c): "loose", or the archive's name.</summary>
    public string? AlsoFile { get; set; }
}

/// <summary>A .mat file (loose, or in an archive) that names a texture asked about itself.</summary>
public sealed class TextureFileUser
{
    public required string Path { get; init; }
    /// <summary>"loose", or the archive's name.</summary>
    public required string Where { get; init; }
    public FileOrigin Origin { get; init; }
    public List<string> Uses { get; } = new();
    public List<string> Textures { get; } = new();
    /// <summary>The compiled databases that hold the same path too (S33c), as <see cref="MaterialDatabase.Shown"/>.</summary>
    public List<string> AlsoCompiled { get; } = new();
}

public sealed class TextureUsersAnswer : ReportAnswer
{
    public override string Kind => "texture-users";
    public required string Texture { get; init; }
    /// <summary>The texture names that fit, as Data names them, each with where the file is.</summary>
    public List<(string Path, FileOrigin Origin, string? Where)> Matched { get; } = new();
    public int DatabasesLookedIn { get; set; }
    public int TexturesLookedAt { get; set; }
    public List<TextureUser> Users { get; } = new();
    /// <summary>The .mat files that name a texture asked about (S33b).</summary>
    public List<TextureFileUser> FileUsers { get; } = new();
    public int MatFilesLookedIn { get; set; }

    protected override void Fill(JsonObject json)
    {
        json["texture"] = Texture;
        json["textures"] = new JsonArray(Matched.Select(m => (JsonNode)new JsonObject { ["texture"] = m.Path, ["foundIn"] = m.Where, ["origin"] = m.Origin.ToString() }).ToArray());
        json["databasesLookedIn"] = DatabasesLookedIn;
        json["matFilesLookedIn"] = MatFilesLookedIn;
        json["materials"] = new JsonArray(Users.Select(u => (JsonNode)new JsonObject
        {
            ["material"] = u.Path, ["name"] = u.Name, ["id"] = u.Id, ["database"] = u.Db.Path, ["foundIn"] = u.Db.Where, ["game"] = u.Db.IsGame, ["gameReads"] = u.GameReads,
            ["textures"] = new JsonArray(u.Textures.Select(t => (JsonNode)t).ToArray()), ["uses"] = new JsonArray(u.Uses.Select(t => (JsonNode)t).ToArray()),
            ["alsoFile"] = u.AlsoFile,
        }).ToArray());
        json["matFiles"] = new JsonArray(FileUsers.Select(u => (JsonNode)new JsonObject
        {
            ["material"] = u.Path, ["foundIn"] = u.Where, ["origin"] = u.Origin.ToString(),
            ["textures"] = new JsonArray(u.Textures.Select(t => (JsonNode)t).ToArray()), ["uses"] = new JsonArray(u.Uses.Select(t => (JsonNode)t).ToArray()),
            ["alsoCompiledIn"] = new JsonArray(u.AlsoCompiled.Select(t => (JsonNode)t).ToArray()),
        }).ToArray());
    }

    protected override void Render(ReportBody body, ReportTextOptions o)
    {
        body.RowNoun = "materials";
        string what = Matched.Count == 1 ? Matched[0].Path : Matched.Count == 0 ? $"a texture that fits \"{Texture}\"" : $"{Plural(Matched.Count, "texture")} that fit \"{Texture}\"";
        var dbs = Users.Select(u => u.Db).Distinct().ToList();
        string compiled = $"{N(Users.Count)} compiled{(dbs.Count > 0 ? $" (in {Plural(dbs.Count, "database")}: {string.Join(", ", dbs.Select(d => d.IsGame ? "the game's" : d.Where))})" : "")}";
        body.Head.Add($"Materials that use {what}: {compiled}, and {Plural(FileUsers.Count, ".mat file")} of the {N(MatFilesLookedIn)} in Data.");
        if (Matched.Count == 1)
        {
            var (_, origin, where) = Matched[0];
            body.Head.Add(origin is FileOrigin.None ? "The texture itself is in no archive and isn't a loose file." : origin == FileOrigin.NotLoaded ? $"The texture itself is only in {where}, which the game doesn't load." : $"The texture itself: {where}.");
        }
        else if (Matched.Count > 1) body.Head.Add($"Textures: {string.Join(", ", Matched.Take(8).Select(m => m.Path))}{(Matched.Count > 8 ? ", …" : "")}.");
        if (Users.Count + FileUsers.Count > 0)
        {
            body.Head.Add(Matched.Count == 1 ? "| Material | Held in | Uses it as |" : "| Material | Held in | Uses |");
            body.Head.Add("| :--- | :--- | :--- |");
        }
        foreach (var u in Users)
        {
            string material = u.Path ?? $"{u.Name ?? "(no name)"} (path not known: {u.Id})";
            string db = (u.Db.IsGame ? "the game's" : u.Db.Where) + (u.GameReads ? "" : " (NOT READ BY THE GAME)") + (u.AlsoFile != null ? $" (a .mat file has this path too: {u.AlsoFile})" : "");
            body.Rows.Add($"| {Cell(material)} | {Cell(db)} | {Cell(string.Join("; ", u.Uses))} |");
        }
        foreach (var u in FileUsers)
        {
            string where = (u.Origin == FileOrigin.Loose ? "a loose .mat file" : $"a .mat file in {u.Where}") + (u.Origin == FileOrigin.NotLoaded ? " (NOT LOADED BY THE GAME)" : "")
                + (u.AlsoCompiled.Count > 0 ? $" (also compiled into {string.Join(", ", u.AlsoCompiled)})" : "");
            body.Rows.Add($"| {Cell(u.Path)} | {Cell(where)} | {Cell(string.Join("; ", u.Uses))} |");
        }
        if (Users.Count + FileUsers.Count == 0)
            body.Empty = Matched.Count == 0
                ? $"No material names a texture that fits \"{Texture}\": the {Plural(DatabasesLookedIn, "database")} and the {Plural(MatFilesLookedIn, ".mat file")} name {N(TexturesLookedAt)} textures. A path is matched whole, in any letter case; a pattern with * or ? must fit the whole path; any other text is looked for anywhere in it."
                : "No material ends up with it: a database or a .mat file names it, but every material that could reach it sets another texture in that place.";
    }
}

public sealed class MaterialsView
{
    internal readonly ReportSession _s;
    public MaterialsView(ReportSession session) { _s = session; }

    // ---------------------------------------------------------------- Names of the format
    /// <summary>The slots of a texture set (BSMaterial::MRTextureFile's index): the values of the game's own enum
    /// BSMaterial::MRTextureChannel, 0 to 20 (21 is its "Count"). Read from the code that registers the enum in
    /// Starfield.exe and CreationKit.exe, which load each name and store its value in turn; both give the same table
    /// (notes\fix-s33-rest.md). The 14 names the .mat files' "Summary" gives (0 to 9, 13 to 15, 19) are the same.</summary>
    internal static readonly string[] Slots =
    {
        "Albedo", "Normal", "Opacity", "Roughness", "Metalness", "AmbientOcclusion", "Height", "Emissive", "Transmissive", "Curvature",
        "Mask", "Flow", "DepthOffset", "Frost", "OverlayColor", "OverlayRoughness", "OverlayMetalness", "ScatteringDirections",
        "ScatteringForwardAlpha", "SecondaryOpacity", "HairID",
    };

    // ---------------------------------------------------------------- Which copy the game uses (S33c)
    /// <summary>What the evidence says about the copy the game uses when more than one place holds a material's path.
    /// The game's code turns both on by default: the compiled databases (bUseCompiledDB:Materials) and .mat files
    /// (bLoadLooseFiles:Materials), each registered with the value 1 in Starfield.exe; no INI in this game folder or
    /// in My Games sets either. The Creation Kit, built on the same material code, logs how many materials "were
    /// loaded from a loose file" when it loads them all. So a loose .mat file most likely replaces the compiled copy.
    /// Nothing says which of two databases wins, or whether a .mat file inside an archive does: NifSkope's makers
    /// (fo76utils) load an archived .mat file only when no database holds the path. None of it is proven: only a test
    /// in the game settles it (notes\fix-s33-rest.md, GAPS S33c).</summary>
    internal static string? WhichCopy(string? fileWhere, FileOrigin fileOrigin, int databases)
    {
        int places = (fileWhere != null ? 1 : 0) + databases;
        if (places < 2) return null;
        if (fileWhere != null && fileOrigin == FileOrigin.Loose)
            return "most likely the loose .mat file, over the compiled " + (databases == 1 ? "copy" : "copies") + ". Not proven: the game reads both kinds by default, and the Creation Kit (the same material code) counts the materials it takes \"from a loose file\"; only a test in the game settles it.";
        if (fileWhere != null)
            return $"not known: a .mat file in an archive ({fileWhere}) and {Plural(databases, "compiled database")} hold it; nothing in the files or the game's code read here says which wins, and only a test in the game settles it.";
        return $"not known: {Plural(databases, "compiled database")} hold it; nothing in the files or the game's code read here says which wins, and only a test in the game settles it.";
    }

    internal const string BothNote = "Which copy the game uses when more than one place holds a material isn't written in any file. The game turns on both its compiled databases and .mat files by default (bUseCompiledDB:Materials and bLoadLooseFiles:Materials are 1 in its code, and no INI here changes them), so a loose .mat file most likely replaces a compiled copy; which of two databases wins, and whether a .mat file in an archive does, isn't known. All copies are shown. A test in the game settles it: give the loose file's copy a color no other copy has and look at the object in the game.";

    static string Short(string cls) { int at = cls.LastIndexOf("::", StringComparison.Ordinal); return at < 0 ? cls : cls[(at + 2)..]; }

    /// <summary>The Creation Kit's name for a texture's slot, when the schema has the CK's names (to-do F30): its display
    /// name of the BSMaterial::MRTextureChannel member, or of the component's class. Null when it has none, or when it
    /// is the engine's name.</summary>
    internal static string? SlotCkName(string cls, int index, SFSearch.SchemaGen.CkData ck)
    {
        if (!ck.Present) return null;
        if (cls == "BSMaterial::MRTextureFile")
            return index >= 0 && index < Slots.Length && ck.Member("BSMaterial::MRTextureChannel", Slots[index]) is { } m ? SFSearch.SchemaGen.CkData.Shown(m.DisplayName, Slots[index]) : null;
        return SFSearch.SchemaGen.CkData.Shown(ck.Type(cls)?.DisplayName, Short(cls));
    }

    internal static string SlotOf(string cls, int index) =>
        cls == "BSMaterial::MRTextureFile" ? (index >= 0 && index < Slots.Length ? $"{Slots[index]} (slot {index})" : $"slot {index}")
        : Short(cls) + (index > 0 ? " " + index : "");

    /// <summary>What a link's class leads to, and the order the parts are shown in.</summary>
    static (string Kind, int Rank) KindOf(string cls) => Short(cls) switch
    {
        "LayerID" => ("layer", 0),
        "BlenderID" => ("blender", 1),
        "MaterialID" => ("material", 2),
        "TextureSetID" => ("texture set", 3),
        "UVStreamID" => ("UV stream", 4),
        "LODMaterialID" => ("LOD material", 5),
        var s => (s.EndsWith("ID", StringComparison.Ordinal) ? s[..^2] : s, 6),
    };

    // ---------------------------------------------------------------- Paths from names and hashes
    static readonly string[] KnownFolders = { "materials", @"materials\layered\root", @"materials\layered\shadermodels" };
    static readonly ConditionalWeakTable<MaterialContents, Dictionary<uint, string>> FolderCache = new();

    static void AddFolder(Dictionary<uint, string> into, string folder)
    {
        while (folder.Length > 0)
        {
            into.TryAdd(MaterialDatabase.ResourceId(folder + @"\x.mat").Dir, folder);
            int at = folder.LastIndexOf('\\');
            if (at < 0) break;
            folder = folder[..at];
        }
    }

    /// <summary>The folders a database's materials may be in, by their hashes: its textures' folders under
    /// materials\, and the folders .mat files name their parents in most. Kept with the database.</summary>
    static Dictionary<uint, string> FoldersOf(MaterialContents c) => FolderCache.GetValue(c, db =>
    {
        var d = new Dictionary<uint, string>();
        foreach (var f in KnownFolders) AddFolder(d, f);
        foreach (var t in db.TextureNames())
        {
            string n = MaterialsReport.Norm(t);
            int at = n.LastIndexOf('\\');
            if (at > 9 && n.StartsWith(@"textures\", StringComparison.Ordinal)) AddFolder(d, @"materials\" + n[9..at]);
        }
        return d;
    });

    /// <summary>A material's path from its name and hashes, when its name is its file's and its folder is known.</summary>
    internal static string? PathOf(MaterialContents.Node n, MaterialContents? game, Dictionary<uint, string>? extra)
    {
        if (!n.HasMaterialId || n.OwnName is not { Length: > 0 } name) return null;
        var (dir, file, _) = n.Id;
        if (MaterialDatabase.ResourceId(name + ".mat").File != file) return null;
        string? folder = null;
        if (extra != null && extra.TryGetValue(dir, out var e)) folder = e;
        else if (FoldersOf(n.Db).TryGetValue(dir, out var f)) folder = f;
        else if (game != null && game != n.Db && FoldersOf(game).TryGetValue(dir, out var g)) folder = g;
        return folder == null ? null : $@"{folder}\{name.ToLowerInvariant()}.mat";
    }

    /// <summary>materials2 --all-paths: tell the paths the cheap tries can't from everything Data names (S33a), gathered
    /// now when it isn't kept. Off, a call uses what is kept (in memory, or in the scratch folder) and gathers nothing.</summary>
    public bool AllPaths { get; set; }

    /// <summary>What a gather of the whole Data folder takes in a new process on this PC (fix-s33h), for the line that
    /// offers --all-paths.</summary>
    internal const string GatherCost = "a few seconds";

    MaterialPathEvidence? _evidence;
    EvidenceFrom _evidenceFrom;
    bool _evidenceLooked, _evidenceSaved;
    /// <summary>Materials whose path the gather might tell, asked about while it isn't kept (and --all-paths is off).</summary>
    readonly HashSet<(MaterialContents, int)> _untold = new();
    string Scratch => _s.Compare.ScratchFolder;

    /// <summary>For the service's indexer lane, after each look of the header index (fix-s33h, S33g): the material paths
    /// Data names, gathered and kept in the scratch folder for every worker, unless they are kept there already for the
    /// files as they are. A line for the log.</summary>
    public string WarmPaths()
    {
        (int Databases, long Kept) released = default;
        var probe = _s.Run(scope =>
        {
            var a = new MaterialPartsAnswer { Material = "" };
            var finder = Finder(null, false, a, out _);
            var dbs = Databases(finder, a);
            var held = dbs.Where(d => d.Db.Contents != null).Select(d => d.Db).ToHashSet();
            AllPaths = true;
            _evidence = Evidence(finder, dbs);
            // S33e: the gather reads every database whole only for its textures' folders. What it read itself is let go
            // and the memory compacted, so the worker that ran it doesn't keep them (nor the garbage of the read)
            released = ArchiveStore.ReleaseContents(null, dbs.Select(d => d.Db).Where(d => !held.Contains(d)));
            return a;
        });
        if (released.Databases > 0) ArchiveStore.Compact();
        var e = _evidence!;
        static string S(double s) => s.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        string let = released.Databases > 0 ? $"; let go of the {Plural(released.Databases, "database")} it read whole ({ArchiveStore.SizeText(released.Kept)})" : "";
        return _evidenceFrom == EvidenceFrom.Now
            ? $"material paths: gathered in {S(probe.Seconds)} s ({N(e.PathsFound)} paths, {N(e.FoldersFound)} folders; meshes {S(e.MeshSeconds)} s, records {S(e.RecordSeconds)} s){(_evidenceSaved ? ", kept in the scratch folder" : "")}{let}"
            : $"material paths: already kept ({(_evidenceFrom == EvidenceFrom.Memory ? "in memory" : "in the scratch folder")}), nothing read{let}";
    }

    /// <summary>The evidence for the archives the finder looks in: kept (memory, scratch folder), else gathered now
    /// when <see cref="AllPaths"/> is on, else null. The databases' textures are those of every database read whole,
    /// so a gather gives the same whatever the call read before.</summary>
    MaterialPathEvidence? Evidence(FileFinder finder, List<(MaterialDatabase Db, FileOrigin Origin, bool Counts)> dbs)
    {
        var store = _s.Archives;
        var archives = finder.Order.Select(o => o.Path).ToList();
        var looseMats = store.Loose.Where(p => p.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)).Select(p => System.IO.Path.Combine(store.DataFolder, p)).ToList();
        var plugins = _s.Index.Plugins;
        string key = MaterialPaths.KeyOf(archives, looseMats, plugins);
        // A plain call takes a gather made before a plugin changed too: its paths still hold (MaterialPaths.KeyOf)
        if (!AllPaths) return MaterialPaths.Kept(key, Scratch, out _evidenceFrom, olderPlugins: true);
        // Kept in the scratch folder only for Data's own archives: a plugin outside Data with --beside adds others
        string data = store.DataFolder.TrimEnd('\\') + "\\";
        bool save = archives.All(a => System.IO.Path.GetFullPath(a).StartsWith(data, StringComparison.OrdinalIgnoreCase));
        var e = MaterialPaths.Get(key, Scratch, save, store, archives, dbs.Select(d => d.Db).ToList(),
            () => dbs.Select(d => store.Contents(d.Db)).OfType<MaterialContents>().SelectMany(c => c.TextureNames()),
            () => dbs.Select(d => store.Contents(d.Db)).OfType<MaterialContents>().SelectMany(c => c.MatNames()),
            () => MatFiles(finder, out _, out _, out _).Select(f => (f.Path, f.Mats)).ToList(), plugins, out _evidenceFrom);
        _evidenceSaved = save && _evidenceFrom == EvidenceFrom.Now && File.Exists(System.IO.Path.Combine(Scratch, MaterialPaths.FileName));
        return e;
    }

    /// <summary>A material's path from what Data names (S33a): gathered on the first material the cheap tries can't
    /// place, once per process.</summary>
    internal string? More(MaterialContents.Node n, FileFinder finder, List<(MaterialDatabase Db, FileOrigin Origin, bool Counts)> dbs)
    {
        if (!n.HasMaterialId) return null;
        if (_evidence == null && (!_evidenceLooked || AllPaths))
        {
            _evidenceLooked = true;
            _evidence = Evidence(finder, dbs);
        }
        if (_evidence == null) { _untold.Add((n.Db, n.At)); return null; }
        return MaterialPaths.PathOf(n, _evidence);
    }

    /// <summary>The line that offers --all-paths, when the cheap tries left materials without a path and nothing was
    /// gathered.</summary>
    string? UntoldNote(string what) => _untold.Count == 0 ? null
        : $"{what} isn't told by the quick tries (the folders of the database's own textures). --all-paths tells most of them: it gathers once the material paths Data names (every .mat file, the header of every mesh, the folders of every archive, the records of every plugin, the particle files, the databases' own names; {GatherCost} in a new process), keeps them in the scratch folder, and every later call uses them. The service gathers them by itself when it is idle.";

    /// <summary>The cost line's part for the material paths, when they were needed.</summary>
    void EvidenceCost(ReportAnswer answer)
    {
        static string S(double s) => s.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        if (_evidence is not { } e)
        {
            if (_untold.Count > 0) answer.Read.Add("the material paths Data names: not gathered (--all-paths), none kept");
            return;
        }
        string older = e.OlderPlugins ? "; a plugin changed since, so paths only it names may be missing: --all-paths gathers again" : "";
        answer.Read.Add(_evidenceFrom switch
        {
            EvidenceFrom.Memory => $"the material paths Data names (taken from memory: {N(e.PathsFound)} paths and {N(e.FoldersFound)} folders{older})",
            EvidenceFrom.Scratch => $"the material paths Data names (taken from the scratch folder: {N(e.PathsFound)} paths and {N(e.FoldersFound)} folders, gathered {e.GatheredUtc.ToLocalTime():yyyy-MM-dd HH:mm} in {S(e.Seconds)} s{older})",
            _ => $"the material paths Data names, gathered now to tell the paths of materials a database knows only by hash: {N(e.MatFiles)} .mat files ({S(e.MatSeconds)} s), the headers of {N(e.Meshes)} meshes ({(e.MeshDiskBytes > 0 ? $"{ArchiveStore.SizeText(e.MeshDiskBytes)} read, " : "")}{ArchiveStore.SizeText(e.MeshBytes)} inflated{(e.MeshesReadWhole > 0 ? $", {N(e.MeshesReadWhole)} read whole" : "")}; {S(e.MeshSeconds)} s), the folders of {Plural(e.ArchivesNamed, "archive")}, {N(e.Records)} records of {Plural(e.RecordPlugins, "plugin")} ({ArchiveStore.SizeText(e.RecordBytes)}, {N(e.RecordTexts)} .mat texts; {S(e.RecordSeconds)} s), {Plural(e.Blobs, "particle file")} ({ArchiveStore.SizeText(e.BlobBytes)}), {Plural(e.DatabaseNames, "database name")} that hold a path ({N(e.PathsFound)} paths and {N(e.FoldersFound)} folders kept) in {S(e.Seconds)} s{(_evidenceSaved ? "; kept in the scratch folder for later calls" : "")}",
        });
    }

    // ---------------------------------------------------------------- Where to look
    /// <summary>The finder of files: a plugin's (its archives first, as the materials report), or Data's as a whole.</summary>
    internal FileFinder Finder(string? plugin, bool beside, ReportAnswer answer, out string? shown)
    {
        shown = null;
        if (plugin == null)
        {
            var all = new FileFinder(_s, new[] { "Starfield.esm" }, new ArchiveTarget { Shown = "Data" });
            answer.Sources.Add($"files looked for in {N(_s.Archives.Loose.Count)} loose files and {N(all.Archives)} archives in Data ({N(all.NotLoadedCount)} of them the game doesn't load)");
            return all;
        }
        ArchiveTarget own;
        IReadOnlyList<string> masters = Array.Empty<string>();
        PluginSide? side = null;
        try { side = _s.Side(plugin); } catch (ReportException) when (plugin.IndexOfAny(new[] { '\\', '/' }) < 0) { }
        if (side != null) { own = ArchiveReport.Resolve(_s, side.InData ? side.Name : side.Path, beside); masters = side.Masters; shown = side.Shown; }
        else { own = ArchiveReport.Resolve(_s, plugin, beside); shown = own.Shown; }
        foreach (var n in own.Notes) answer.Notes.Add(n);
        if (side != null) ReportSession.AddSource(answer, side);
        var finder = new FileFinder(_s, masters, own);
        answer.Sources.Add(finder.SourceLine());
        return finder;
    }

    /// <summary>Every compiled material database, its material list read (once per process).</summary>
    internal List<(MaterialDatabase Db, FileOrigin Origin, bool Counts)> Databases(FileFinder finder, ReportAnswer answer)
    {
        var store = _s.Archives;
        var list = new List<(MaterialDatabase, FileOrigin, bool)>();
        foreach (var (archive, path, origin, counts) in MaterialsReport.Databases(store, finder))
        {
            MaterialDatabase db;
            try { db = store.Database(archive, path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                db = MaterialDatabase.Failed(path, archive == null ? "loose" : System.IO.Path.GetFileName(archive), archive ?? System.IO.Path.Combine(store.DataFolder, path), e.Message);
            }
            if (db.Error != null) answer.Notes.Add($"{db.Path} in {db.Where} couldn't be read as a compiled material database: {db.Error}.");
            list.Add((db, origin, counts));
        }
        return list;
    }

    /// <summary>The cost line's parts: the archives' names, the databases' lists and their whole contents.</summary>
    void Cost(ReportAnswer answer, IReadOnlyCollection<MaterialDatabase> listed, IReadOnlyCollection<MaterialDatabase> whole)
    {
        var store = _s.Archives;
        static string S(double s) => s.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        answer.Read.Add($"the file names of {Plural(store.NamesUsed, "archive")}{(store.ListsRead < store.NamesUsed ? $" ({N(store.NamesUsed - store.ListsRead)} from memory)" : "")}");
        var listedNow = listed.Where(d => d.Error == null && store.DatabasesRead.Contains(d.Container + "|" + d.Path)).ToList();
        if (listed.Count > 0)
            answer.Read.Add($"the material lists of {Plural(listed.Count, "compiled material database")} ({(listedNow.Count > 0 ? $"{N(listedNow.Count)} read now in {S(listedNow.Sum(d => d.Seconds))} s; " : "")}{N(listed.Count - listedNow.Count)} taken from memory)");
        var wholeNow = whole.Where(d => store.ContentsRead.Contains(d.Container + "|" + d.Path) && d.Contents != null).ToList();
        if (whole.Count > 0)
            answer.Read.Add($"the whole of {Plural(whole.Count, "compiled material database")} ({(wholeNow.Count > 0 ? $"{N(wholeNow.Count)} read now: {ArchiveStore.SizeText(wholeNow.Sum(d => d.Contents!.BytesRead))} in {S(wholeNow.Sum(d => d.Contents!.Seconds))} s, {ArchiveStore.SizeText(wholeNow.Sum(d => d.Contents!.Kept))} kept; " : "")}{N(whole.Count - wholeNow.Count)} taken from memory)");
        if (ArchiveStore.CompactAfterWholeRead(wholeNow.Count) is { } given) answer.Read.Add(given);
    }

    // ---------------------------------------------------------------- One material
    /// <summary>Where the material at a path is (a .mat file, compiled databases), and its parts in each.</summary>
    public MaterialPartsAnswer Material(string materialPath, string? plugin = null, bool beside = false) => _s.Run(scope =>
    {
        string path = MaterialsReport.RecordMaterial(materialPath.Trim().Trim('"'));
        if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            throw new ReportException($"\"{materialPath}\" isn't a material's path", "give the path of a .mat file as a mesh or a record names it, such as materials\\water\\waterchoppylarge.mat");
        var probe = new MaterialPartsAnswer { Material = path };
        var finder = Finder(plugin, beside, probe, out string? shown);
        var answer = new MaterialPartsAnswer { Material = path, Plugin = shown };
        answer.Notes.AddRange(probe.Notes); answer.Sources.AddRange(probe.Sources);
        var store = _s.Archives;

        // Every database's list, to say which hold it
        var dbs = Databases(finder, answer);
        answer.DatabasesLookedIn = dbs.Count(d => d.Db.Error == null);
        foreach (var (db, origin, counts) in dbs)
            if (db.Error == null && db.Has(path)) answer.CompiledIn.Add((db, origin, counts));
        var gameDb = dbs.Select(d => d.Db).FirstOrDefault(d => d.IsGame && d.Error == null);
        MaterialContents? game = null;
        bool gameTried = false;
        MaterialContents? Game() { if (!gameTried) { gameTried = true; game = gameDb == null ? null : store.Contents(gameDb); } return game; }
        var whole = new List<MaterialDatabase>();
        var extra = new Dictionary<uint, string>();
        AddFolder(extra, System.IO.Path.GetDirectoryName(path) ?? "materials");

        // A .mat file at this path
        var hit = finder.Find(path);
        if (hit.Origin != FileOrigin.None)
        {
            answer.FileWhere = hit.Where; answer.FileOrigin = hit.Origin;
            try
            {
                var json = JsonNode.Parse(finder.Read(hit), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (json is JsonObject root)
                {
                    var g = Game();
                    if (gameDb != null && g != null) whole.Add(gameDb);
                    var b = new Builder(finder, g, extra, Holders(dbs, Game)) { More = n => More(n, finder, dbs), Ck = _s.Schema.Model.Ck };
                    answer.Trees.Add(($"the .mat file ({hit.Where})", b.FromFile(root, path)));
                    if (b.Lost > 0) answer.Notes.Add($"The .mat file has {Plural(b.Lost, "link")} to a part that is neither in the file nor in a database read (the game's): {(b.Lost == 1 ? "it is" : "they are")} shown as such.");
                    if (b.ParentFiles.Count > 0) answer.Notes.Add($"Its parent material {string.Join(", ", b.ParentFiles)} is a .mat file, not compiled: what it gives isn't shown (look at it with --material).");
                }
                else answer.Notes.Add("The .mat file isn't a JSON object: its parts can't be shown.");
            }
            catch (JsonException) { answer.Notes.Add("The .mat file isn't readable as a material (bad JSON): its parts can't be shown."); }
            catch (IOException e) { answer.Notes.Add($"The .mat file couldn't be read: {e.Message}"); }
        }

        // Each database that holds it
        foreach (var (db, origin, counts) in answer.CompiledIn)
        {
            var c = db.IsGame ? Game() : store.Contents(db);
            if (c == null) { answer.Notes.Add($"The parts of the material in {db.Shown} can't be shown: {db.ContentsError ?? "the database couldn't be read whole"}."); continue; }
            if (!whole.Contains(db)) whole.Add(db);
            var g = c.HasForeignParents ? Game() : c;
            if (c.HasForeignParents && gameDb != null && g != null && !whole.Contains(gameDb)) whole.Add(gameDb);
            if (c.Find(path) is not { } node) { answer.Notes.Add($"{db.Shown}: its material list holds the path, but its contents don't: its parts can't be shown."); continue; }
            var b = new Builder(finder, g, extra, Holders(dbs, Game)) { GameOwn = db.IsGame, More = n => More(n, finder, dbs), Ck = _s.Schema.Model.Ck };
            answer.Trees.Add((db.Shown, b.FromDb(node, "material", 0, depth: 0, inherited: false)));
            if (b.Lost > 0) answer.Notes.Add($"{db.Shown}: {Plural(b.Lost, "link")} of this material {(b.Lost == 1 ? "leads" : "lead")} to a part or a parent that isn't there: {(b.Lost == 1 ? "it is" : "they are")} shown as such.");
        }

        answer.GameUses = WhichCopy(answer.FileWhere, answer.FileOrigin, answer.CompiledIn.Count);
        if (answer.GameUses != null) answer.Notes.Add(BothNote);
        if (answer.Trees.Count > 1)
        {
            // The same path in two places: do they give the material the same textures?
            static HashSet<string> Set(MaterialPart t) => t.All().SelectMany(p => p.Textures).Where(x => x.Path.Length > 0).Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var first = Set(answer.Trees[0].Tree);
            foreach (var (source, tree) in answer.Trees.Skip(1))
            {
                var other = Set(tree);
                if (first.SetEquals(other)) continue;
                var only1 = first.Except(other, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList();
                var only2 = other.Except(first, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList();
                static string Few(List<string> l) => string.Join(", ", l.Take(4)) + (l.Count > 4 ? ", …" : "");
                answer.Notes.Add($"{answer.Trees[0].Source} and {source} give this material different textures: " +
                    string.Join("; ", new[] { only1.Count > 0 ? $"only the first names {Few(only1)}" : null, only2.Count > 0 ? $"only the second names {Few(only2)}" : null }.Where(x => x != null)) +
                    ".");
            }
        }
        if (answer.Trees.Count > 0)
            answer.Notes.Add("Names are the ones the format gives each part (its CTName). A slot's name is the game's own (its enum BSMaterial::MRTextureChannel); a part's own texture or link overrides what it inherits from its parent. A parent material's path is told from its name and hash when its folder is known.");
        if (answer.Missing)
            answer.Notes.Add("A database knows a material by a hash of its path: check the path as a mesh or a record writes it (materials2 <plugin> lists those).");
        if (UntoldNote($"The path of {Plural(_untold.Count, "parent material")}") is { } untold) answer.Notes.Add(untold + " Until then it is shown by name and ID.");
        answer.Sources.Add("the databases the game reads: its own materials\\materialsbeta.cdb and every .cdb in a plugin's \" - Main\" archives, as the game's code reads them (a .cdb loose in Data or in another archive isn't read)");
        Cost(answer, dbs.Select(d => d.Db).ToList(), whole);
        EvidenceCost(answer);
        return answer;
    });

    /// <summary>The databases to look in for an object a .mat file names by its ID: the game's first, then those
    /// already read whole.</summary>
    static Func<IEnumerable<MaterialContents>> Holders(List<(MaterialDatabase Db, FileOrigin Origin, bool Counts)> dbs, Func<MaterialContents?> game) => () =>
    {
        var list = new List<MaterialContents>();
        if (game() is { } g) list.Add(g);
        foreach (var (db, _, _) in dbs) if (db.Contents is { } c && !list.Contains(c)) list.Add(c);
        return list;
    };

    /// <summary>Builds a material's tree of parts, from its database or its .mat file.</summary>
    sealed class Builder(FileFinder finder, MaterialContents? game, Dictionary<uint, string> extra, Func<IEnumerable<MaterialContents>> holders)
    {
        /// <summary>The Creation Kit's names, from the schema (to-do F30).</summary>
        public SFSearch.SchemaGen.CkData Ck { get; init; } = new();
        readonly MaterialContents.Walk _walk = new();
        readonly HashSet<object> _seen = new();
        int _nodes;
        public int Lost;
        /// <summary>For a material of the game's own database: a texture nowhere in Data is the game's gap.</summary>
        public bool GameOwn { get; init; }
        /// <summary>A material's path when the cheap tries don't tell it: from what Data names (S33a).</summary>
        public Func<MaterialContents.Node, string?>? More { get; init; }
        string? PathFor(MaterialContents.Node n) => PathOf(n, game, extra) ?? More?.Invoke(n);
        public List<string> ParentFiles { get; } = new();

        PartTexture Texture(string cls, int index, string name, bool inherited)
        {
            string path = name.Length == 0 ? "" : MaterialsReport.Norm(name);
            var hit = path.Length == 0 ? default : finder.Find(path);
            return new PartTexture
            {
                Slot = SlotOf(cls, index), CkName = SlotCkName(cls, index, Ck), Class = cls, Index = index, Path = path, Origin = hit.Origin, Where = hit.Where,
                GameLacks = GameOwn && path.Length > 0 && hit.Origin == FileOrigin.None, Inherited = inherited,
            };
        }

        void Parent(MaterialPart part, MaterialContents.Node n)
        {
            var p = MaterialContents.ParentOf(n, game, out bool lost);
            if (lost) { part.ParentLost = true; Lost++; }
            if (p is not { } pp) return;
            part.Parent = MaterialContents.NameOf(pp, game);
            part.ParentId = pp.IdText;
            if (pp.HasMaterialId) part.ParentPath = PathFor(pp);
        }

        /// <summary>A part's parts, made in the order they are shown (layers, blenders, a layer's material, texture
        /// set, UV stream, LOD materials), so that a part reached twice is shown in full the first time it is printed.</summary>
        static void Build(MaterialPart part, List<(int Rank, int Index, Func<MaterialPart> Make)> kids)
        {
            foreach (var k in kids.OrderBy(k => k.Rank).ThenBy(k => k.Index)) part.Parts.Add(k.Make());
        }

        public MaterialPart FromDb(MaterialContents.Node n, string kind, int index, int depth, bool inherited)
        {
            var part = new MaterialPart { Kind = kind, Index = index, Name = MaterialContents.NameOf(n, game), Id = n.IdText, Inherited = inherited };
            Parent(part, n);
            if (!_seen.Add((n.Db, n.At)) || depth > 12 || ++_nodes > 2000) { part.ShownAbove = true; return part; }
            var kids = new List<(int, int, Func<MaterialPart>)>();
            foreach (var e in MaterialContents.EntriesOf(n, game, _walk))
            {
                if (e.Texture != null) { part.Textures.Add(Texture(e.Class, e.Index, e.Texture, false)); continue; }
                var (k, rank) = KindOf(e.Class);
                int at = e.Index;
                if (e.Link is { } l) kids.Add((rank, at, () => FromDb(l, k, at, depth + 1, false)));
                else kids.Add((rank, at, () => { Lost++; return new MaterialPart { Kind = k, Index = at, Lost = true }; }));
            }
            Build(part, kids);
            return part;
        }

        static string? Text(JsonNode? x) => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        static bool TryRes(string? text, out (uint Dir, uint File, uint Ext) id)
        {
            id = default;
            if (text == null || !text.StartsWith("res:", StringComparison.OrdinalIgnoreCase)) return false;
            var p = text[4..].Split(':');
            if (p.Length != 3) return false;
            var h = System.Globalization.NumberStyles.HexNumber;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (!uint.TryParse(p[0], h, inv, out uint d) || !uint.TryParse(p[1], h, inv, out uint f) || !uint.TryParse(p[2], h, inv, out uint e)) return false;
            id = (d, f, e);
            return true;
        }

        MaterialContents.Node? FindRes(string? text)
        {
            if (!TryRes(text, out var id)) return null;
            foreach (var c in holders()) if (c.Find(id.Dir, id.File, id.Ext) is { } n) return n;
            return null;
        }

        MaterialContents.Node? FindMaterial(string path)
        {
            foreach (var c in holders()) if (c.Find(path) is { } n) return n;
            return null;
        }

        Dictionary<string, JsonObject> _byId = new(StringComparer.OrdinalIgnoreCase);

        public MaterialPart FromFile(JsonObject root, string path)
        {
            var objects = (root["Objects"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            foreach (var o in objects) if (Text(o["ID"]) is { } id) _byId.TryAdd(id, o);
            var top = objects.FirstOrDefault(o => o["ID"] == null) ?? objects.FirstOrDefault();
            if (top == null) return new MaterialPart { Kind = "material", Name = System.IO.Path.GetFileNameWithoutExtension(path), From = "the .mat file" };
            return FromJson(top, "material", 0, 0, root: true);
        }

        /// <summary>Every "FileName" in a component's data, wherever it sits.</summary>
        static IEnumerable<string> FileNames(JsonNode? x)
        {
            switch (x)
            {
                case JsonObject o:
                    foreach (var (k, v) in o)
                    {
                        if (k.Equals("FileName", StringComparison.OrdinalIgnoreCase) && Text(v) is { } s) yield return s;
                        else foreach (var s2 in FileNames(v)) yield return s2;
                    }
                    break;
                case JsonArray a:
                    foreach (var i in a) foreach (var s in FileNames(i)) yield return s;
                    break;
            }
        }

        MaterialPart FromJson(JsonObject o, string kind, int index, int depth, bool root)
        {
            var part = new MaterialPart { Kind = kind, Index = index, Id = Text(o["ID"]), From = "the .mat file" };
            string? parentText = Text(o["Parent"]);
            MaterialContents.Node? parent = null;
            if (TryRes(parentText, out _))
            {
                parent = FindRes(parentText);
                part.ParentId = parentText;
                if (parent is { } p) { part.Parent = MaterialContents.NameOf(p, game); if (p.HasMaterialId) part.ParentPath = PathFor(p); }
                else if (_byId.ContainsKey(parentText!)) part.Parent = "an object of this file";
                else { part.ParentLost = true; Lost++; }
            }
            else if (parentText is { Length: > 0 })
            {
                string pp = MaterialsReport.RecordMaterial(parentText);
                part.ParentPath = pp;
                parent = FindMaterial(pp);
                if (parent is { } p) part.Parent = MaterialContents.NameOf(p, game);
                else if (finder.Find(pp).Origin != FileOrigin.None) { if (!ParentFiles.Contains(pp)) ParentFiles.Add(pp); }
                else { part.ParentLost = true; Lost++; }
            }
            if (!_seen.Add(o) || depth > 12 || ++_nodes > 2000) { part.ShownAbove = true; return part; }

            // Its own components: a name, links by ID, textures
            var set = new HashSet<(string, int, bool)>();
            var kids = new List<(int, int, Func<MaterialPart>)>();
            foreach (var comp in (o["Components"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                string type = Text(comp["Type"]) ?? "";
                int at = comp["Index"] is JsonValue iv && iv.TryGetValue<int>(out int ii) ? ii : 0;
                var data = comp["Data"] as JsonObject;
                if (type == "BSComponentDB::CTName") { part.Name = Text(data?["Name"]); continue; }
                if (Text(data?["ID"]) is { } link && link.StartsWith("res:", StringComparison.OrdinalIgnoreCase))
                {
                    set.Add((type, at, false));
                    var (k, rank) = KindOf(type);
                    if (_byId.TryGetValue(link, out var inFile)) kids.Add((rank, at, () => FromJson(inFile, k, at, depth + 1, false)));
                    else if (FindRes(link) is { } n) kids.Add((rank, at, () => FromDb(n, k, at, depth + 1, false)));
                    else kids.Add((rank, at, () => { Lost++; return new MaterialPart { Kind = k, Index = at, Lost = true, Id = link }; }));
                    continue;
                }
                foreach (var name in FileNames(data))
                {
                    set.Add((type, at, true));
                    part.Textures.Add(Texture(type, at, name, false));
                }
            }
            // What it doesn't set comes from its parent
            if (parent is { } from)
            {
                part.Name ??= MaterialContents.NameOf(from, game);
                foreach (var e in MaterialContents.EntriesOf(from, game, _walk))
                {
                    if (set.Contains((e.Class, e.Index, e.Texture != null))) continue;
                    if (e.Texture != null) { part.Textures.Add(Texture(e.Class, e.Index, e.Texture, true)); continue; }
                    var (k, rank) = KindOf(e.Class);
                    int at = e.Index;
                    if (e.Link is { } l) kids.Add((rank, at, () => FromDb(l, k, at, depth + 1, true)));
                    else kids.Add((rank, at, () => { Lost++; return new MaterialPart { Kind = k, Index = at, Lost = true, Inherited = true }; }));
                }
            }
            var textures = part.Textures.OrderBy(t => t.Class, StringComparer.Ordinal).ThenBy(t => t.Index).ToList();
            part.Textures.Clear(); part.Textures.AddRange(textures);
            Build(part, kids);
            if (root && part.Name == null) part.Name = System.IO.Path.GetFileNameWithoutExtension(part.ParentPath);
            return part;
        }
    }

    // ---------------------------------------------------------------- Which compiled materials use a texture
    /// <summary>The compiled materials, in every database in Data, that use a texture: a path (matched whole, in any
    /// letter case), a pattern with * or ? (the whole path), or a text looked for anywhere in the path.</summary>
    public TextureUsersAnswer Texture(string texture, string? plugin = null, bool beside = false) => _s.Run(scope =>
    {
        string wanted = MaterialsReport.Norm(texture.Trim().Trim('"'));
        if (wanted.Length == 0) throw new ReportException("no texture was named", "give a texture's path, such as textures\\water\\foamdefault_color.dds, or a part of one");
        var probe = new TextureUsersAnswer { Texture = wanted };
        var finder = Finder(plugin, beside, probe, out _);
        var answer = new TextureUsersAnswer { Texture = wanted };
        answer.Notes.AddRange(probe.Notes); answer.Sources.AddRange(probe.Sources);
        var store = _s.Archives;
        var dbs = Databases(finder, answer).Where(d => d.Db.Error == null).ToList();
        answer.DatabasesLookedIn = dbs.Count;

        // Every database read whole (once per process), and the texture names in each that fit
        var contents = new List<(MaterialDatabase Db, FileOrigin Origin, bool Counts, MaterialContents C)>();
        foreach (var (db, origin, counts) in dbs)
        {
            var c = store.Contents(db);
            if (c == null) { answer.Notes.Add($"{db.Shown} couldn't be read whole: {db.ContentsError}. Its materials weren't searched."); continue; }
            contents.Add((db, origin, counts, c));
        }
        // The .mat files, loose and in every archive: the texture names each one's bytes hold (read once per process)
        var matClock = System.Diagnostics.Stopwatch.StartNew();
        var matFiles = MatFiles(finder, out int matArchivesRead, out int matArchivesKept, out long matBytesRead);
        double matSeconds = matClock.Elapsed.TotalSeconds;
        answer.MatFilesLookedIn = matFiles.Count;
        var all = contents.SelectMany(c => c.C.TextureNames()).Select(MaterialsReport.Norm)
            .Concat(matFiles.SelectMany(f => f.Textures)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        answer.TexturesLookedAt = all.Count;
        Func<string, bool> fits = all.Contains(wanted, StringComparer.OrdinalIgnoreCase)
            ? t => t.Equals(wanted, StringComparison.OrdinalIgnoreCase)
            : ArchiveReport.Matcher(wanted);
        var matched = all.Where(fits).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var m in matched) { var hit = finder.Find(m); answer.Matched.Add((m, hit.Origin, hit.Where)); }
        var textures = new Dictionary<MaterialContents, HashSet<int>>();
        foreach (var (_, _, _, c) in contents) textures[c] = c.TexturesFitting(t => fits(MaterialsReport.Norm(t)));

        var game = contents.FirstOrDefault(c => c.Db.IsGame).C;
        var walk = new MaterialContents.Walk();
        foreach (var (db, origin, counts, c) in contents)
        {
            foreach (var (material, uses) in c.MaterialsUsing(textures, game, walk))
            {
                var u = new TextureUser
                {
                    Path = PathOf(material, game, null) ?? More(material, finder, dbs), Name = material.OwnName ?? MaterialContents.NameOf(material, game), Id = material.IdText,
                    Db = db, GameReads = counts && origin != FileOrigin.NotLoaded,
                };
                foreach (var e in uses)
                {
                    string t = MaterialsReport.Norm(e.Texture ?? "");
                    if (!u.Textures.Contains(t, StringComparer.OrdinalIgnoreCase)) u.Textures.Add(t);
                    string holder = e.Link is { } h ? MaterialContents.NameOf(h, game) ?? h.IdText : "?";
                    string? ckSlot = SlotCkName(e.Class, e.Index, _s.Schema.Model.Ck);
                    string use = $"{SlotOf(e.Class, e.Index)}{(ckSlot != null ? $" (CK: \"{ckSlot}\")" : "")}{(matched.Count > 1 ? $": {t}" : "")} in {holder}";
                    if (!u.Uses.Contains(use)) u.Uses.Add(use);
                }
                answer.Users.Add(u);
            }
        }
        // The game's first, then the mods' in the order looked in; by path within each
        var sorted = answer.Users.OrderBy(u => u.Db.IsGame ? 0 : 1).ThenBy(u => contents.FindIndex(c => c.Db == u.Db))
            .ThenBy(u => u.Path == null ? 1 : 0).ThenBy(u => u.Path ?? u.Name, StringComparer.OrdinalIgnoreCase).ToList();
        answer.Users.Clear(); answer.Users.AddRange(sorted);
        int noPath = answer.Users.Count(u => u.Path == null);
        if (_evidence == null && noPath > 0)
            answer.Notes.Add(UntoldNote($"The path of {N(noPath)} of the {Plural(answer.Users.Count, "material")}") + $" Until then {(noPath == 1 ? "it is" : "they are")} shown by name and ID.");
        else if (noPath > 0)
            answer.Notes.Add($"The path of {Plural(noPath, "material")} isn't known: a database holds only a hash of each path, and no file in Data names a path or a folder with the same hashes (.mat files, mods' meshes, the archives' folders, the textures' folders). {(noPath == 1 ? "It is" : "They are")} shown by name and ID; --material takes a path to check a guess.");

        // The .mat files that name one of the textures themselves: only those are read as JSON, and built as a tree
        // to find the slot. A path held twice (loose and in an archive, or in two archives) is the copy the finder
        // finds first, as for --material.
        var matchedSet = matched.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var holders = Holders(dbs, () => game);
        var extra = new Dictionary<uint, string>();
        int parsed = 0;
        foreach (var f in matFiles.Where(f => f.Textures.Any(matchedSet.Contains)).GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            var hit = finder.Find(f.Path);
            if (hit.Origin == FileOrigin.None) continue;
            var u = new TextureFileUser { Path = f.Path, Where = hit.Where!, Origin = hit.Origin };
            try
            {
                parsed++;
                if (JsonNode.Parse(finder.Read(hit), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) is not JsonObject root) continue;
                var tree = new Builder(finder, game, extra, holders) { More = n => More(n, finder, dbs), Ck = _s.Schema.Model.Ck }.FromFile(root, f.Path);
                foreach (var part in tree.All())
                    foreach (var t in part.Textures.Where(t => !t.Inherited && matchedSet.Contains(t.Path)))
                    {
                        if (!u.Textures.Contains(t.Path, StringComparer.OrdinalIgnoreCase)) u.Textures.Add(t.Path);
                        string use = $"{t.Slot}{(matched.Count > 1 ? $": {t.Path}" : "")} in {part.Name ?? part.Kind}";
                        if (!u.Uses.Contains(use)) u.Uses.Add(use);
                    }
            }
            catch (JsonException) { answer.Notes.Add($"{f.Path} ({hit.Where}) names the texture but isn't readable as a material (bad JSON)."); continue; }
            catch (IOException e) { answer.Notes.Add($"{f.Path} ({hit.Where}) couldn't be read: {e.Message}"); continue; }
            if (u.Uses.Count == 0) continue;
            foreach (var (db, _, _) in dbs) if (db.Has(f.Path)) u.AlsoCompiled.Add(db.IsGame ? "the game's database" : db.Where);
            answer.FileUsers.Add(u);
        }
        var fileOrder = finder.Order.Select(o => System.IO.Path.GetFileName(o.Path)).ToList();
        var sortedFiles = answer.FileUsers.OrderBy(u => u.Origin == FileOrigin.Loose ? -1 : fileOrder.FindIndex(n => n.Equals(u.Where, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(u => u.Path, StringComparer.OrdinalIgnoreCase).ToList();
        answer.FileUsers.Clear(); answer.FileUsers.AddRange(sortedFiles);
        // A path both compiled and a .mat file (S33c)
        var filePaths = matFiles.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Where, StringComparer.OrdinalIgnoreCase);
        foreach (var u in answer.Users) if (u.Path != null && filePaths.TryGetValue(u.Path, out var w)) u.AlsoFile = w;
        if (answer.Users.Any(u => u.AlsoFile != null) || answer.FileUsers.Any(u => u.AlsoCompiled.Count > 0))
            answer.Notes.Add(BothNote);
        answer.Notes.Add("A .mat file is listed when it names the texture itself; one that only inherits it from its parent isn't (a compiled material is listed either way).");
        answer.Sources.Add("the databases the game reads: its own materials\\materialsbeta.cdb and every .cdb in a plugin's \" - Main\" archives, as the game's code reads them (a .cdb loose in Data or in another archive isn't read)");
        Cost(answer, dbs.Select(d => d.Db).ToList(), contents.Select(c => c.Db).ToList());
        EvidenceCost(answer);
        answer.Read.Add($"the .mat files of {Plural(matArchivesKept + matArchivesRead, "archive")} that hold any ({N(matArchivesRead)} read now: {ArchiveStore.SizeText(matBytesRead)}{(matArchivesRead > 0 ? $" in {matSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} s" : "")}; {N(matArchivesKept)} taken from memory) and {N(matFiles.Count(f => f.Where == "loose"))} loose; {N(parsed)} read as JSON because they name the texture");
        return answer;
    });

    // ---------------------------------------------------------------- .mat files and the textures they name (S33b)
    /// <summary>A .mat file, the texture names its bytes hold ("FileName" values, as Data names them) and the material
    /// paths it names (its own "Filename", its parents and imports).</summary>
    public sealed record MatFile(string Path, string Where, string[] Textures, string[] Mats);

    /// <summary>Each archive's .mat files with their texture names, kept for the process while the archive keeps its
    /// size and date. Only the names are kept, not the files.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (ArchiveStamp Stamp, MatFile[] Files)> MatIndex = new(StringComparer.OrdinalIgnoreCase);
    static readonly System.Text.RegularExpressions.Regex FileNameValue = new(@"""FileName""\s*:\s*""((?:[^""\\]|\\.)*)""",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    static readonly System.Text.RegularExpressions.Regex MatValue = new(@"""([^""]{1,400}?\.mat)""",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>A .mat file read for its names: the "FileName" values that look like a file (they hold a dot), and
    /// every text value that ends in .mat, as Data names them.</summary>
    static MatFile ReadMat(string path, string where, byte[] bytes)
    {
        string text = System.Text.Encoding.Latin1.GetString(bytes);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in FileNameValue.Matches(text))
        {
            string v = m.Groups[1].Value.Replace(@"\\", @"\");
            if (v.Length == 0 || !v.Contains('.')) continue;
            found.Add(MaterialsReport.Norm(v));
        }
        var mats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in MatValue.Matches(text)) mats.Add(MaterialsReport.Norm(m.Groups[1].Value.Replace(@"\\", @"\")));
        return new MatFile(path, where, found.ToArray(), mats.ToArray());
    }

    /// <summary>Every .mat file in Data: the loose ones (read now, they are few) and those of every archive the finder
    /// looks in (from memory when kept). An archive is opened only when its name table holds a .mat file.</summary>
    List<MatFile> MatFiles(FileFinder finder, out int archivesRead, out int archivesKept, out long bytesRead)
    {
        archivesRead = 0; archivesKept = 0; bytesRead = 0;
        var store = _s.Archives;
        var list = new List<MatFile>();
        foreach (var loose in store.Loose.Where(p => p.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try { list.Add(ReadMat(MaterialsReport.Norm(loose), "loose", File.ReadAllBytes(System.IO.Path.Combine(store.DataFolder, loose)))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        foreach (var (archive, _) in finder.Order)
        {
            if (MatIndex.TryGetValue(archive, out var have) && have.Stamp.SameCopy(new FileInfo(archive)))
            {
                if (have.Files.Length > 0) archivesKept++;
                list.AddRange(have.Files);
                continue;
            }
            var stamp = ArchiveStore.Stamp(archive);
            var paths = store.PathsOf(archive);
            var files = Array.Empty<MatFile>();
            if (paths.Paths.Any(p => p.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)))
            {
                var a = store.Open(archive);
                var got = new List<MatFile>();
                foreach (var f in a.Files.Where(f => f.Extension == ".mat"))
                {
                    byte[] bytes;
                    try { bytes = f.Bytes(); }
                    catch (Exception e) when (e is IOException or InvalidDataException) { continue; }
                    bytesRead += bytes.Length;
                    got.Add(ReadMat(MaterialsReport.Norm(f.Path), a.Name, bytes));
                }
                files = got.ToArray();
                archivesRead++;
            }
            MatIndex[archive] = (stamp, files);
            list.AddRange(files);
        }
        return list;
    }
}
