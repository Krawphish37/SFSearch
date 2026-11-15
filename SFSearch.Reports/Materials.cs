// materials: the materials a plugin's meshes and records name, where each is, and the textures of those that are
// files, with what is missing along the way.
//  - The meshes are the .nif files in the plugin's own archives. A mesh names its materials as text.
//  - The records: every text ending in .mat on the plugin's records (a landscape texture's material, a biome's water,
//    a planet's ring). A record is read only when its bytes hold ".mat". World data is read for a plugin of up to
//    100,000 records, as scripts does.
//  - A material is looked for as a file first: loose in Data, in the plugin's archives, its masters', the other
//    archives. A .mat file (JSON) is read, and each texture it names is looked for the same way.
//  - A material that is no file is looked up in the compiled material databases (materialsbeta.cdb): the game's own,
//    the plugin's, its masters', the other loaded plugins'. A database is read as far as its material list, once per
//    process, and only when a material is no file. A material in none of them is missing. The databases the game
//    reads are its own and those in a plugin's " - Main" archives (MaterialDatabase.GameReads); one elsewhere is
//    looked in last, and a material only there is a problem.
//  - A compiled material's textures: the database is then read whole (MaterialContents), once per process, and each
//    texture the material's parts name is looked for as a file, like a .mat file's. A texture the game's own material
//    names and the game doesn't ship is listed apart, not counted as missing.
// Nothing is stored.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
    /// <summary>A texture of a material compiled into the game's own database that the game doesn't ship either: the
    /// game's gap, not the plugin's. Not counted as missing.</summary>
    public bool GameLacks { get; init; }
}

public sealed class MaterialRow
{
    /// <summary>The material's path as Data names it, in lower case: "materials\weapons\x.mat".</summary>
    public required string Path { get; init; }
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
    /// <summary>For a compiled material: links or parents in the database that lead nowhere, so a texture may be left out.</summary>
    public int Unresolved { get; set; }
    /// <summary>Compiled: no file, and in a compiled material database.</summary>
    public bool Compiled => NoFile && Database != null;
    /// <summary>No file and in no database, while every database was read. Missing.</summary>
    public bool Missing { get; set; }
    /// <summary>No file and in no database read, but a database couldn't be read: not judged.</summary>
    public bool NotChecked => NoFile && Database == null && !Missing;
    /// <summary>A .mat file in the plugin's own archives, or named by one of its meshes.</summary>
    public bool FromMeshes { get; set; }
    /// <summary>The plugin's meshes that name it.</summary>
    public List<string> UsedBy { get; } = new();
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
        if (!NoFile) { o["foundIn"] = Where; o["origin"] = Origin.ToString(); }
        else if (Compiled)
        {
            o["compiled"] = true;
            o["compiledIn"] = new JsonObject { ["database"] = Database!.Path, ["foundIn"] = Database.Where, ["origin"] = DatabaseOrigin.ToString(), ["game"] = Database.IsGame, ["gameReads"] = DatabaseCounts };
            o["texturesRead"] = TexturesRead;
            if (Unresolved > 0) o["unresolvedLinks"] = Unresolved;
        }
        else if (Missing) o["missing"] = true;
        else o["notChecked"] = true;
        o["usedBy"] = new JsonArray(UsedBy.Select(m => (JsonNode)m).ToArray());
        o["namedBy"] = new JsonArray(NamedBy.Select(m => (JsonNode)m).ToArray());
        o["textures"] = new JsonArray(Textures.Select(t =>
        {
            var x = new JsonObject { ["texture"] = t.Path, ["foundIn"] = t.Where, ["origin"] = t.Origin.ToString() };
            if (t.GameLacks) x["gameLacks"] = true;
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
    public int NotChecked => Rows.Count(m => m.NotChecked);
    public int MissingTextures => Rows.Sum(m => m.MissingTextures.Count());
    /// <summary>The textures named by compiled materials (read from their databases).</summary>
    public int CompiledTextures => Rows.Where(m => m.Compiled).Sum(m => m.Textures.Count);
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
            ["meshes"] = Meshes, ["recordsLookedAt"] = RecordsLooked, ["recordsRead"] = RecordsRead, ["materials"] = Rows.Count, ["checked"] = Checked,
            ["compiled"] = Compiled, ["compiledInGame"] = CompiledInGame, ["missing"] = Missing, ["notChecked"] = NotChecked, ["textures"] = Rows.Sum(m => m.Textures.Count),
            ["missingTextures"] = MissingTextures, ["materialsWithMissingTextures"] = MaterialsWithMissing, ["unreadable"] = Unreadable,
            ["texturesOfCompiled"] = CompiledTextures, ["texturesTheGameLacks"] = GameLacks,
        };
        json["materials"] = new JsonArray(Rows.Select(m => (JsonNode)m.ToJson()).ToArray());
    }

    static string Row(MaterialRow m)
    {
        var users = new List<string>();
        if (m.UsedBy.Count == 1) users.Add(m.UsedBy[0]); else if (m.UsedBy.Count > 1) users.Add($"{N(m.UsedBy.Count)} meshes");
        if (m.NamedBy.Count == 1) users.Add(m.NamedBy[0]); else if (m.NamedBy.Count > 1) users.Add($"{N(m.NamedBy.Count)} records ({m.NamedBy[0]}, …)");
        string used = users.Count == 0 ? "(no mesh or record of this plugin)" : string.Join("; ", users);
        string where = !m.NoFile ? (m.Origin == FileOrigin.NotLoaded ? $"NOT LOADED: {m.Where}" : m.Where!)
            : m.Compiled ? (m.DatabaseOrigin == FileOrigin.NotLoaded ? "NOT LOADED: compiled into " : !m.DatabaseCounts ? "NOT READ BY THE GAME: compiled into " : "compiled into ") + m.Database!.Shown
            : m.Missing ? "MISSING: no file, and in no compiled material database"
            : "no file; not judged (a material database couldn't be read)";
        string missing = m.Problem ?? string.Join(", ", m.MissingTextures.Select(t => t.Origin == FileOrigin.NotLoaded ? $"{t.Path} (only in {t.Where}, which the game doesn't load)" : t.Path));
        return $"| {Cell(m.Path)} | {Cell(where)} | {Cell(used)} | {(m.NoFile && !m.TexturesRead ? "" : N(m.Textures.Count))} | {Cell(missing)} |";
    }

    protected override void Render(ReportBody body, ReportTextOptions o)
    {
        body.RowNoun = "materials";
        int fromRecords = Rows.Count(m => m.NamedBy.Count > 0);
        body.Head.Add($"Materials of {Plugin}: {Plural(OwnArchives.Count, "archive")}, {Plural(Meshes, "mesh", "meshes")}, {Plural(Rows.Count, "material")}" +
            (fromRecords > 0 ? $" ({N(fromRecords)} named by its records)." : "."));
        string compiled = $"{N(Compiled)} compiled into a material database" + (Compiled > 0 ? $" ({N(CompiledInGame)} the game's, {N(Compiled - CompiledInGame)} a mod's)" : "");
        body.Head.Add($"{N(Checked)} material files read, {compiled}, {N(Missing)} missing{(NotChecked > 0 ? $", {N(NotChecked)} not judged" : "")}; " +
            $"{Plural(Rows.Sum(m => m.Textures.Count), "texture")} named{(CompiledTextures > 0 ? $" ({N(CompiledTextures)} by compiled materials)" : "")}, {N(MissingTextures)} missing in {Plural(MaterialsWithMissing, "material")}; {N(Unreadable)} unreadable.");
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
    static readonly Regex MatInMesh = new(@"[A-Za-z0-9_\\/ .\-]+\.mat\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

    /// <summary>The material paths on the plugin's records, each added to its row with the record and field that name it.</summary>
    void FromRecords(PluginSide side, ReadScope scope, MaterialsAnswer answer, Func<string, MaterialRow> get)
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
    static void CompiledTextures(ArchiveStore store, FileFinder finder, MaterialsAnswer answer, List<MaterialRow> compiled)
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
            var names = contents.TexturesOf(m.Path, contents.HasForeignParents ? Game() : null, out int lost);
            if (names == null) { if (!failed.Contains(db)) failed.Add(db); continue; }
            m.TexturesRead = true;
            m.Unresolved = lost;
            if (lost > 0) { unresolved += lost; rowsUnresolved++; }
            foreach (var name in names.Select(Norm).Distinct().OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
            {
                var t = finder.Find(name);
                m.Textures.Add(new MaterialTexture { Path = name, Origin = t.Origin, Where = t.Where, GameLacks = db.IsGame && t.Origin == FileOrigin.None });
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
                        byte[] bytes;
                        try { bytes = f.Bytes(); }
                        catch (Exception e) when (e is IOException or InvalidDataException) { answer.Notes.Add($"{f.Path} couldn't be read from {a.Name}: {e.Message}."); continue; }
                        meshBytes += bytes.Length;
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (Match m in MatInMesh.Matches(Encoding.Latin1.GetString(bytes)))
                        {
                            string mat = Norm(m.Value);
                            if (seen.Add(mat)) { var row = Get(mat); row.UsedBy.Add(f.Path); row.FromMeshes = true; }
                        }
                    }
                    else if (ext == ".mat") Get(Norm(f.Path)).FromMeshes = true;
                    else if (ext == ".cdb") answer.CompiledDatabases.Add($"{f.Path} ({a.Name})");
                }
            }

            // The materials its records name
            if (side != null) FromRecords(side, scope, answer, Get);

            // Each material: where it is, and when it is a file, its textures
            var noFile = new List<MaterialRow>();
            foreach (var m in mats.Values.OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase))
            {
                answer.Rows.Add(m);
                var hit = finder.Find(m.Path);
                if (hit.Origin == FileOrigin.None) { noFile.Add(m); continue; }
                m.Origin = hit.Origin; m.Where = hit.Where;
                JsonNode? json;
                try { json = JsonNode.Parse(finder.Read(hit), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
                catch (JsonException) { m.Problem = "not readable as a material (bad JSON)"; continue; }
                catch (IOException e) { m.Problem = "couldn't be read: " + e.Message; continue; }
                foreach (var tex in TextureNames(json))
                {
                    var t = finder.Find(tex);
                    m.Textures.Add(new MaterialTexture { Path = tex, Origin = t.Origin, Where = t.Where });
                }
            }

            // A material that is no file: which compiled material database holds it. Each database is read when first
            // looked in; a material is looked for in the next only when the ones before don't hold it.
            if (noFile.Count > 0)
            {
                var candidates = Databases(store, finder);
                var read = new MaterialDatabase?[candidates.Count];
                MaterialDatabase Db(int i)
                {
                    if (read[i] is { } have) return have;
                    var (archive, path, origin, _) = candidates[i];
                    MaterialDatabase db;
                    try { db = store.Database(archive, path); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        db = MaterialDatabase.Failed(path, archive == null ? "loose" : Path.GetFileName(archive), archive ?? Path.Combine(store.DataFolder, path), e.Message);
                    }
                    answer.DatabasesRead.Add((db, origin));
                    return read[i] = db;
                }
                foreach (var m in noFile)
                {
                    for (int i = 0; i < candidates.Count && m.Database == null; i++)
                    {
                        var db = Db(i);
                        if (db.Error == null && db.Has(m.Path)) { m.Database = db; m.DatabaseOrigin = candidates[i].Origin; m.DatabaseCounts = candidates[i].Counts; }
                    }
                    m.Missing = m.Database == null && answer.DatabasesRead.All(d => d.Db.Error == null);
                }
                if (candidates.Count == 0)
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
                if (candidates.Count > 0)
                    answer.Sources.Add("the databases the game reads: its own materials\\materialsbeta.cdb and every .cdb in a plugin's \" - Main\" archives, as the game's executable names them (a .cdb loose in Data or in another archive isn't read)");
                CompiledTextures(store, finder, answer, noFile.Where(m => m.Compiled).ToList());
            }

            if (own.Archives.Count == 0 && answer.Rows.Count == 0)
                answer.EmptyReason = $"{shown} has no archives: no file named \"{Path.GetFileNameWithoutExtension(own.Plugin ?? plugin)} - <part>.ba2\" is in {store.DataFolder}, and none of its records names a material. Its meshes, if it has any, are loose files or its masters'; this report reads the meshes in a plugin's own archives.";
            else if (answer.Rows.Count == 0)
                answer.EmptyReason = $"The {Plural(own.Archives.Count, "archive")} of {shown} {(own.Archives.Count == 1 ? "holds" : "hold")} {Plural(answer.Meshes, "mesh", "meshes")} and no material file, no mesh names a material, and no record does.";
            else if (own.Archives.Count == 0)
                answer.Notes.Add($"{shown} has no archives: only the materials its records name are listed.");
            if (answer.Missing > 0)
                answer.Notes.Add($"{Plural(answer.Missing, "material")} {(answer.Missing == 1 ? "is" : "are")} MISSING: no loose file, no archive and none of the {Plural(answer.DatabasesRead.Count, "compiled material database")} in Data holds {(answer.Missing == 1 ? "it" : "them")}. " +
                    "A database knows a material by a hash of its path, so this is a lookup by the path the mesh or record writes.");
            int compiledOther = answer.Rows.Count(m => m.Compiled && m.DatabaseOrigin == FileOrigin.OtherLoaded);
            if (compiledOther > 0)
                answer.Notes.Add($"{Plural(compiledOther, "material")} {(compiledOther == 1 ? "is" : "are")} only in the compiled database of a loaded plugin that isn't a master of {shown} ({string.Join(", ", answer.Rows.Where(m => m.Compiled && m.DatabaseOrigin == FileOrigin.OtherLoaded).Select(m => m.Database!.Where).Distinct(StringComparer.OrdinalIgnoreCase).Take(5))}). {(compiledOther == 1 ? "It is" : "They are")} there while that plugin stays installed and loaded.");
            int notRead = answer.Rows.Count(m => m.Compiled && !m.DatabaseCounts);
            if (notRead > 0)
                answer.Notes.Add($"{Plural(notRead, "material")} {(notRead == 1 ? "is" : "are")} only in a compiled material database the game doesn't read ({string.Join(", ", answer.Rows.Where(m => m.Compiled && !m.DatabaseCounts).Select(m => m.Database!.Shown).Distinct(StringComparer.OrdinalIgnoreCase).Take(3))}): the game reads its own and those in a plugin's \" - Main\" archives. Counted as a problem.");
            if (answer.GameLacks > 0)
                answer.Notes.Add($"{Plural(answer.GameLacks, "texture")} of the game's own compiled materials {(answer.GameLacks == 1 ? "isn't" : "aren't")} in Data, loose or in any archive: the game ships {(answer.GameLacks == 1 ? "that material without it" : "those materials without them")}. Not counted as missing ({string.Join(", ", answer.Rows.SelectMany(m => m.Textures.Where(t => t.GameLacks).Select(t => t.Path)).Distinct().Take(3))}{(answer.GameLacks > 3 ? ", …" : "")}).");
            int notLoaded = answer.Rows.Sum(m => m.Textures.Count(t => t.Origin == FileOrigin.NotLoaded)) + answer.Rows.Count(m => m.Origin == FileOrigin.NotLoaded) + answer.Rows.Count(m => m.Compiled && m.DatabaseOrigin == FileOrigin.NotLoaded);
            if (notLoaded > 0) answer.Notes.Add($"{Plural(notLoaded, "file")} {(notLoaded == 1 ? "is" : "are")} only in an archive the game doesn't load (its plugin isn't in the load order, or isn't in Data): counted as missing.");
            int other = answer.Rows.Sum(m => m.Textures.Count(t => t.Origin == FileOrigin.OtherLoaded));
            if (other > 0) answer.Notes.Add($"{Plural(other, "texture")} {(other == 1 ? "is" : "are")} found only in archives of loaded plugins that aren't masters of {shown}.");
            if (answer.WorldLeftOut > 0) answer.Notes.Add($"{Plural(answer.WorldLeftOut, "world record")} (cells and what they hold) {(answer.WorldLeftOut == 1 ? "was" : "were")} left out: a plugin of more than {N(ScriptsReport.WorldLimit)} records is read without them.");
            if (side != null) ReportSession.AddProblemNotes(answer, side.Plugin);
            answer.Read.Add($"{Plural(answer.Meshes, "mesh", "meshes")} ({ArchiveStore.SizeText(meshBytes)}) and {Plural(answer.Checked, "material file")}");
            if (side != null) answer.Read.Add($"{N(answer.RecordsRead)} of {N(answer.RecordsLooked)} records (those whose bytes hold \".mat\")");
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
                var whole = answer.DatabasesRead.Select(d => d.Db).Where(d => d.Contents != null && (d.IsGame || answer.Rows.Any(m => m.TexturesRead && m.Database == d))).Distinct().ToList();
                if (whole.Count > 0)
                {
                    var wholeNow = whole.Where(d => store.ContentsRead.Contains(d.Container + "|" + d.Path)).ToList();
                    string Whole(MaterialDatabase d) => $"{(d.IsGame ? "the game's" : d.Where)}: {ArchiveStore.SizeText(d.Contents!.BytesRead)} in {d.Contents.Seconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} s, {ArchiveStore.SizeText(d.Contents.Allocated)} allocated, {ArchiveStore.SizeText(d.Contents.Kept)} kept";
                    var wparts = wholeNow.Select(Whole).ToList();
                    if (whole.Count > wholeNow.Count) wparts.Add($"{N(whole.Count - wholeNow.Count)} taken from memory");
                    answer.Read.Add($"the whole of {Plural(whole.Count, "compiled material database")}, for the textures of {Plural(answer.Compiled, "compiled material")} ({string.Join("; ", wparts)})");
                    if (ArchiveStore.CompactAfterWholeRead(wholeNow.Count) is { } given) answer.Read.Add(given);
                }
            }
            return answer;
        });
    }
}
