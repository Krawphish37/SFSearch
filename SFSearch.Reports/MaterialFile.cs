// A material file (.mat, JSON) and what it inherits (GAPS S8).
//  - The file holds objects: the material itself (the object without an "ID"; its ID is the file's path) and its
//    parts (layers, blenders, a layer's material, texture sets, UV streams), each with an "ID" ("res:DIR:FILE:EXT").
//    An object names a "Parent": another object by its ID, or a material by its path. A component of an object links
//    to a part by its ID (BSMaterial::LayerID, BlenderID, MaterialID, TextureSetID, UVStreamID, LODMaterialID), or
//    names a texture (the "FileName" of a BSMaterial::MRTextureFile or BSMaterial::TextureFile).
//  - A file writes only what its objects set themselves. The rest comes from each object's parent, as in a compiled
//    material database (MaterialContents): an object starts with its parent's components, and a component it writes
//    (by class and index) takes the place of the parent's. Of the 3,763 material files of the Data folder this was
//    written on, all but one name a parent outside the file and 321 name no texture of their own.
//  - A parent or a part is looked for in the file itself, then in the files it leans on (its "Import" list and the
//    materials its objects name as parents, and theirs), then in the compiled material databases the game reads: the
//    game's own first. One found nowhere is counted (Lost): what it would have given isn't known.
//  - So a material file's textures are those of every object reached from the material through its links, each
//    object with what it inherits. The single-material view (MaterialsView) shows the same parts one by one.
using System.Text.Json.Nodes;

namespace SFSearch.Reports;

/// <summary>A material file's objects, as the file writes them.</summary>
internal sealed class MaterialFile
{
    /// <summary>The file's Data-relative path, in lower case.</summary>
    public required string Path { get; init; }
    public List<Obj> Objects { get; } = new();
    /// <summary>The material itself: the object without an ID (the first object when every one has an ID).</summary>
    public Obj? Root { get; private set; }
    public Dictionary<string, Obj> ById { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The materials the file says it leans on ("Import"), as Data names them, in lower case.</summary>
    public List<string> Imports { get; } = new();

    /// <summary>One object of a file.</summary>
    internal sealed class Obj
    {
        public required MaterialFile File { get; init; }
        public string? Id { get; init; }
        /// <summary>"" for none; "res:DIR:FILE:EXT"; or a material's path as the file writes it.</summary>
        public string Parent { get; init; } = "";
        /// <summary>What its components set: a link (the ID as written; "" for a link set to nothing) or a texture
        /// (the name as written; "" for a slot set to no file), by the component's class and index.</summary>
        public List<(string Class, int Index, bool Texture, string Value)> Entries { get; } = new();
    }

    static string? Text(JsonNode? x) => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static bool IsTextureClass(string? cls) => cls is "BSMaterial::MRTextureFile" or "BSMaterial::TextureFile";

    /// <summary>The file's objects, or null when the JSON holds no "Objects" list.</summary>
    public static MaterialFile? Read(JsonNode? json, string path)
    {
        if (json is not JsonObject top || top["Objects"] is not JsonArray objects) return null;
        var file = new MaterialFile { Path = path };
        if (top["Import"] is JsonArray imports)
            foreach (var i in imports)
                if (Text(i) is { Length: > 0 } s) file.Imports.Add(MaterialsReport.RecordMaterial(s));
        foreach (var o in objects.OfType<JsonObject>())
        {
            var obj = new Obj { File = file, Id = Text(o["ID"]) is { Length: > 0 } id ? id : null, Parent = Text(o["Parent"]) ?? "" };
            foreach (var comp in (o["Components"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                string type = Text(comp["Type"]) ?? "";
                int index = comp["Index"] is JsonValue iv && iv.TryGetValue<int>(out int n) ? n : 0;
                if (comp["Data"] is not JsonObject data) continue;
                // A link: the component's own "ID"
                if (Text(data["ID"]) is { } link) obj.Entries.Add((type, index, false, link));
                // Textures: the FileName of a texture file, the component itself or a value inside it
                Textures(data, type, name => obj.Entries.Add((type, index, true, name)));
            }
            file.Objects.Add(obj);
            if (obj.Id != null) file.ById.TryAdd(obj.Id, obj);
        }
        file.Root = file.Objects.FirstOrDefault(o => o.Id == null) ?? file.Objects.FirstOrDefault();
        return file;
    }

    /// <summary>Every "FileName" of a texture file in a component's data: <paramref name="cls"/> is the class of the
    /// value <paramref name="x"/> is the data of.</summary>
    internal static void Textures(JsonNode? x, string cls, Action<string> found)
    {
        switch (x)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                {
                    if (k.Equals("FileName", StringComparison.OrdinalIgnoreCase) && IsTextureClass(cls) && Text(v) is { } s) found(s);
                    else if (v is JsonObject inner && inner["Data"] is { } data) Textures(data, Text(inner["Type"]) ?? "", found);
                    else Textures(v, cls, found);
                }
                break;
            case JsonArray a:
                foreach (var i in a)
                {
                    if (i is JsonObject inner && inner["Data"] is { } data) Textures(data, Text(inner["Type"]) ?? "", found);
                    else Textures(i, cls, found);
                }
                break;
        }
    }
}

/// <summary>The textures of material files, each object with what it inherits. One for a report's run: the files it
/// loads and what each object inherits are worked out once.</summary>
internal sealed class MaterialFileWalk
{
    readonly Func<string, MaterialFile?> _load;
    readonly Func<MaterialContents?> _game;
    readonly Func<IEnumerable<MaterialContents>> _others;
    readonly MaterialContents.Walk _walk = new();
    readonly Dictionary<MaterialFile, List<MaterialFile>> _scope = new();
    readonly Dictionary<MaterialFile.Obj, Dictionary<(string Class, int Index, bool Texture), List<Val>>> _effective = new();
    readonly Dictionary<string, MaterialContents.Node?> _byId = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="load">A material file by its Data-relative path in lower case, from where the game takes it; null
    /// when it is no file, or not readable as one.</param>
    /// <param name="game">The game's compiled material database, read whole; null when there is none.</param>
    /// <param name="others">The other databases the game reads, each read whole when the search gets to it.</param>
    public MaterialFileWalk(Func<string, MaterialFile?> load, Func<MaterialContents?> game, Func<IEnumerable<MaterialContents>> others)
    {
        _load = load; _game = game; _others = others;
    }

    /// <summary>A link or a texture an object has, its own or inherited.</summary>
    readonly record struct Val(string? Texture, MaterialFile.Obj? Obj, MaterialContents.Node? Node, string? Lost, bool Own, bool Game);

    public sealed class Result
    {
        /// <summary>The textures, as written: <c>Inherited</c> when the file doesn't name it itself; <c>Game</c> when it
        /// comes from an object of the game's own database.</summary>
        public List<(string Name, bool Inherited, bool Game)> Textures { get; } = new();
        /// <summary>The parents and parts found nowhere, as the file writes them.</summary>
        public List<string> Lost { get; } = new();
        /// <summary>Links and parents inside a database that lead nowhere.</summary>
        public int LostInDatabase { get; set; }
        /// <summary>True when something was inherited from a compiled material database.</summary>
        public bool UsedDatabase { get; set; }
    }

    static bool TryRes(string text, out (uint Dir, uint File, uint Ext) id)
    {
        id = default;
        if (!text.StartsWith("res:", StringComparison.OrdinalIgnoreCase)) return false;
        var p = text[4..].Split(':');
        if (p.Length != 3) return false;
        var h = System.Globalization.NumberStyles.HexNumber;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!uint.TryParse(p[0], h, inv, out uint d) || !uint.TryParse(p[1], h, inv, out uint f) || !uint.TryParse(p[2], h, inv, out uint e)) return false;
        id = (d, f, e);
        return true;
    }

    /// <summary>The files a file leans on: its imports and the materials its objects name as parents, and theirs.</summary>
    List<MaterialFile> Scope(MaterialFile file)
    {
        if (_scope.TryGetValue(file, out var have)) return have;
        var list = new List<MaterialFile>();
        _scope[file] = list;
        var seen = new HashSet<MaterialFile> { file };
        var todo = new Queue<MaterialFile>();
        todo.Enqueue(file);
        while (todo.Count > 0 && seen.Count < 64)
        {
            var f = todo.Dequeue();
            var paths = f.Imports.Concat(f.Objects.Where(o => o.Parent.Length > 0 && !o.Parent.StartsWith("res:", StringComparison.OrdinalIgnoreCase)).Select(o => MaterialsReport.RecordMaterial(o.Parent)));
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                if (_load(path) is { } other && seen.Add(other)) { list.Add(other); todo.Enqueue(other); }
        }
        return list;
    }

    MaterialContents.Node? InDatabases((uint Dir, uint File, uint Ext) id, string key)
    {
        if (_byId.TryGetValue(key, out var have)) return have;
        MaterialContents.Node? found = _game()?.Find(id.Dir, id.File, id.Ext);
        if (found == null)
            foreach (var c in _others())
                if (c.Find(id.Dir, id.File, id.Ext) is { } n) { found = n; break; }
        return _byId[key] = found;
    }

    /// <summary>An object named by its ID from a file: in the file, in a file it leans on (a part, or the material
    /// itself: its ID is its path's), in a database.</summary>
    (MaterialFile.Obj? Obj, MaterialContents.Node? Node) ById(MaterialFile from, string text)
    {
        if (from.ById.TryGetValue(text, out var own)) return (own, null);
        var scope = Scope(from);
        foreach (var f in scope) if (f.ById.TryGetValue(text, out var o)) return (o, null);
        if (!TryRes(text, out var id)) return (null, null);
        if (id.Ext == 0x0074616D)
            foreach (var f in scope.Prepend(from))
            {
                var (file, _, dir) = MaterialDatabase.ResourceId(f.Path);
                if (file == id.File && dir == id.Dir && f.Root != null) return (f.Root, null);
            }
        return (null, InDatabases(id, text));
    }

    /// <summary>A material named by its path: a file first (as the report looks for a material), then a database.</summary>
    (MaterialFile.Obj? Obj, MaterialContents.Node? Node) ByPath(string written)
    {
        string path = MaterialsReport.RecordMaterial(written);
        if (_load(path)?.Root is { } root) return (root, null);
        var (file, ext, dir) = MaterialDatabase.ResourceId(path);
        return (null, InDatabases((dir, file, ext), "path:" + path));
    }

    Dictionary<(string Class, int Index, bool Texture), List<Val>> Effective(MaterialFile.Obj o, Result r, int depth)
    {
        if (_effective.TryGetValue(o, out var have)) return have;
        var mine = new Dictionary<(string, int, bool), List<Val>>();
        _effective[o] = mine; // a parent that leads back to the object gives it nothing more
        if (o.Parent.Length > 0 && depth < 64)
        {
            var (po, pn) = o.Parent.StartsWith("res:", StringComparison.OrdinalIgnoreCase) ? ById(o.File, o.Parent) : ByPath(o.Parent);
            if (po != null)
            {
                foreach (var (k, v) in Effective(po, r, depth + 1))
                    mine[k] = v.Select(x => x with { Own = false }).ToList();
            }
            else if (pn is { } node)
            {
                bool game = node.Db == _game();
                foreach (var e in MaterialContents.EntriesOf(node, _game(), _walk))
                {
                    var key = (e.Class, e.Index, e.Texture != null);
                    if (!mine.TryGetValue(key, out var list)) mine[key] = list = new List<Val>();
                    list.Add(e.Texture != null ? new Val(e.Texture, null, null, null, false, game)
                        : e.Link is { } l ? new Val(null, null, l, null, false, game)
                        : new Val(null, null, null, "", false, game)); // a link inside the database that leads nowhere
                }
            }
            else mine[("", -1 - _effective.Count, false)] = new List<Val> { new(null, null, null, o.Parent, false, false) };
        }
        // Its own components take the place of the parent's of the same class and index
        var set = new HashSet<(string, int, bool)>();
        foreach (var (cls, index, texture, value) in o.Entries)
        {
            var key = (cls, index, texture);
            if (set.Add(key) || !mine.TryGetValue(key, out var list)) mine[key] = list = new List<Val>();
            if (texture) { list.Add(new Val(value, null, null, null, true, false)); continue; }
            if (value.Length == 0 || value == "<this>" || !value.StartsWith("res:", StringComparison.OrdinalIgnoreCase)) continue; // set to nothing, or to the material itself
            var (lo, ln) = ById(o.File, value);
            list.Add(lo != null ? new Val(null, lo, null, null, true, false)
                : ln != null ? new Val(null, null, ln, null, true, false)
                : new Val(null, null, null, value, true, false));
        }
        return mine;
    }

    /// <summary>The textures a material file uses: those of every object reached from the material, each with what it
    /// inherits.</summary>
    public Result Textures(MaterialFile file)
    {
        var r = new Result();
        if (file.Root == null) return r;
        var found = new Dictionary<string, (bool Inherited, bool Game)>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, bool inherited, bool game)
        {
            if (name.Length == 0) return;
            // A texture the file names itself is the file's, whatever else names it too
            if (!found.TryGetValue(name, out var have) || (have.Inherited && !inherited)) found[name] = (inherited, game && inherited);
        }
        var seen = new HashSet<MaterialFile.Obj>();
        var seenNodes = new HashSet<MaterialContents.Node>();
        var todo = new Stack<(MaterialFile.Obj? Obj, MaterialContents.Node? Node)>();
        todo.Push((file.Root, null));
        int lostBefore = _walk.Lost;
        while (todo.Count > 0 && seen.Count + seenNodes.Count < 10_000)
        {
            var (obj, node) = todo.Pop();
            if (node is { } n)
            {
                if (!seenNodes.Add(n)) continue;
                r.UsedDatabase = true;
                bool game = n.Db == _game();
                var names = n.Db.TexturesOf(n, _game(), out int lost);
                foreach (var name in names) Add(name, true, game);
                r.LostInDatabase += lost;
                continue;
            }
            if (obj == null || !seen.Add(obj)) continue;
            foreach (var list in Effective(obj, r, 0).Values)
                foreach (var v in list)
                {
                    if (v.Texture != null) { Add(v.Texture, !v.Own || obj.File != file, v.Game); if (v.Game) r.UsedDatabase = true; }
                    else if (v.Obj != null) todo.Push((v.Obj, null));
                    else if (v.Node != null) todo.Push((null, v.Node));
                    else if (v.Lost is { Length: 0 }) r.LostInDatabase++;
                    else if (v.Lost != null && !r.Lost.Contains(v.Lost, StringComparer.OrdinalIgnoreCase)) r.Lost.Add(v.Lost);
                }
        }
        r.LostInDatabase += _walk.Lost - lostBefore;
        foreach (var (name, (inherited, game)) in found.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)) r.Textures.Add((name, inherited, game));
        return r;
    }
}
