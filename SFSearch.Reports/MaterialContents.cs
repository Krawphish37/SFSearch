// The rest of a compiled material database (materialsbeta.cdb): its objects, their components, and so the textures
// each material uses. Read whole, once per process (the database keeps it), and only when a report needs the
// textures of a material compiled into it.
//
// The format past the material list, as this reader understands it. Learned from the description in fo76utils'
// NifSkope fork (github.com/fo76utils/nifskope, lib/libfo76utils/src/bsrefl.cpp, bsrefl.hpp and bsmatcdb.cpp, MIT
// licence); no code was copied. Each point was checked on the databases of the Data folder this was written on
// (notes\fix-f4r-materials-rest.md): every chunk of all 21 reads to its last byte.
//  - An object is read by its class definition (MaterialDatabase.cs). A built-in type 0xFFFFFF01 + n: 0 nothing,
//    1 a string (a 16-bit length, then that many bytes, a zero byte last), 2 a list, 3 a map, 4 a reference (a type,
//    then a value of that type in place), 7 to 17 the numbers. A list or a map is the next chunk of the file (LIST: the
//    element type, a count, the elements; MAPC: the key type, the value type, a count, the pairs), so the chunks of an
//    object's lists follow the object's own chunk. A class's fields follow in place, one after another (OBJT), or, in
//    a DIFF, only the fields that differ: a 16-bit field number before each, ended by a number of 0x8000 or more. A
//    class flagged "user" (flag 4) is the next chunk (USER, or USRD for a diff): its class, then the class again
//    (its fields follow, as above) or a built-in type (a value of that type, then the next type, and so on).
//  - The index, an OBJT of class BSComponentDB2::DBFileIndex, holds four lists: the component types, the objects
//    (ObjectInfo: PersistentID, DBID, Parent, ParentPersistentID, HasData), the components (ComponentInfo: the
//    object's DBID, an index, a type) and the edges (an object owned by another: not needed here).
//  - After the index, each OBJT or DIFF chunk is the data of the next component in the components list, in order.
//    A DIFF holds only what differs from the component the object inherits from its parent: an object starts with a
//    copy of its parent's components (Parent, a DBID in the same database; in a mod's database a parent may be one
//    of the game's, then found by ParentPersistentID).
//  - A material (an object whose PersistentID has the extension mat) links to its parts with components holding a
//    BSComponentDB2::ID (a DBID): LayerID, BlenderID, LODMaterialID; a layer to a MaterialID and a UVStreamID; a
//    material part to a TextureSetID. A texture is a BSMaterial::MRTextureFile or BSMaterial::TextureFile (its
//    FileName): a texture set's own components, and fields of others (a blender's mask, flow and detail maps, ...).
//  - An object's name is its BSComponentDB::CTName component (its field Name), kept one for each object, its bytes
//    found again by a hash. The game's: 48,740 of its 48,749 materials have one, the name of their file.
//  - So a material's textures are the FileNames of every object reached from it through those links, each object
//    with what it inherits. Checked: the 48,749 materials of the game's database name 87,279 textures; 86,770 of them
//    are in the game's own archives (the rest are test and work files the game ships without).
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace SFSearch.Reports;

/// <summary>What a compiled material database holds past its material list: enough to tell each material's textures.</summary>
public sealed class MaterialContents
{
    /// <summary>The database's size, and how many of its bytes were read (all of them, when the read worked).</summary>
    public long Size { get; private set; }
    public long BytesRead { get; private set; }
    public double Seconds { get; private set; }
    public long Allocated { get; private set; }
    /// <summary>About how many bytes this keeps in memory.</summary>
    public long Kept { get; private set; }
    public int Objects => _dbid.Length;
    public int Components { get; private set; }
    public int Textures => _textAt.Length - 1;
    /// <summary>True when some object's parent is in another database (a mod's material made from one of the game's).</summary>
    public bool HasForeignParents => _foreignParent.Count > 0;

    // The objects, in the order of the object list
    uint[] _dbid = Array.Empty<uint>();
    int[] _parent = Array.Empty<int>();          // the parent's index in this database, -1 for none, -2 for one found by persistent ID
    uint[] _pidExt = Array.Empty<uint>();
    bool[] _hasData = Array.Empty<bool>();
    readonly Dictionary<int, (ulong Key, uint Ext)> _foreignParent = new();
    // Lookups
    int[]? _byDbidAt;                            // null when the object list is in DBID order (it is in every database seen)
    uint[] _byDbid = Array.Empty<uint>();
    ulong[] _byPid = Array.Empty<ulong>(); int[] _byPidAt = Array.Empty<int>();  // folder and file hashes of each PersistentID, sorted
    // The components that hold a link or a texture, grouped by object
    int[] _compStart = Array.Empty<int>();      // per object, into the component arrays (length Objects + 1)
    uint[] _compKey = Array.Empty<uint>();      // the class (ClassId) << 16 | the component's index
    bool[] _compDiff = Array.Empty<bool>();
    int[] _compEntry = Array.Empty<int>();      // into the entries (length components + 1)
    Entry[] _entries = Array.Empty<Entry>();
    // The texture names, one after another (Latin-1), and where each starts
    byte[] _text = Array.Empty<byte>();
    int[] _textAt = new int[1];
    string Text(int i) => Encoding.Latin1.GetString(_text, _textAt[i], _textAt[i + 1] - _textAt[i]);
    // Each object's own name (its BSComponentDB::CTName): an index into the names, -1 for none
    int[] _objName = Array.Empty<int>();
    byte[] _names = Array.Empty<byte>();
    int[] _nameAt = new int[1];
    string NameText(int i) => Encoding.Latin1.GetString(_names, _nameAt[i], _nameAt[i + 1] - _nameAt[i]);

    /// <summary>A link (a DBID) or a texture (an index into the strings) at a place in a component.</summary>
    internal struct Entry { public int Path; public uint Value; public bool Texture; }

    int IndexOf(uint dbid)
    {
        int i = Array.BinarySearch(_byDbid, dbid);
        return i < 0 ? -1 : _byDbidAt == null ? i : _byDbidAt[i];
    }

    int IndexOfPid(ulong key, uint ext, bool needData)
    {
        int i = Array.BinarySearch(_byPid, key);
        if (i < 0) return -1;
        while (i > 0 && _byPid[i - 1] == key) i--;
        for (; i < _byPid.Length && _byPid[i] == key; i++)
        {
            int at = _byPidAt[i];
            if (_pidExt[at] == ext && (!needData || _hasData[at])) return at;
        }
        return -1;
    }

    /// <summary>The textures of the material at this Data-relative path, as the database writes them, or null when it
    /// doesn't hold that material. <paramref name="unresolved"/> counts links and parents that lead nowhere (a parent in
    /// the game's database when <paramref name="game"/> is null).</summary>
    public List<string>? TexturesOf(string materialPath, MaterialContents? game, out int unresolved)
    {
        unresolved = 0;
        var (file, ext, dir) = MaterialDatabase.ResourceId(materialPath);
        int start = IndexOfPid(((ulong)dir << 32) | file, ext, needData: true);
        if (start < 0) return null;
        return TexturesFrom(start, game, out unresolved);
    }

    /// <summary>The textures of one material of this database (the tests hold the search for a texture's users against it).</summary>
    internal List<string> TexturesOf(Node material, MaterialContents? game, out int unresolved) => TexturesFrom(material.At, game, out unresolved);

    /// <summary>Every material's textures together (for the tests): how many materials, and the textures they name.</summary>
    public (int Materials, HashSet<string> Textures, int Unresolved) AllMaterialTextures(MaterialContents? game)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int materials = 0, unresolved = 0;
        for (int i = 0; i < _dbid.Length; i++)
        {
            if (_pidExt[i] != 0x0074616D || !_hasData[i]) continue;
            materials++;
            all.UnionWith(TexturesFrom(i, game, out int lost));
            unresolved += lost;
        }
        return (materials, all, unresolved);
    }

    List<string> TexturesFrom(int start, MaterialContents? game, out int unresolved)
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<(MaterialContents, int)>();
        var todo = new Stack<(MaterialContents Db, int At)>();
        var memo = new Dictionary<(MaterialContents, int), Dictionary<(uint, int, bool), (MaterialContents, uint)>>();
        todo.Push((this, start));
        int lost = 0;
        while (todo.Count > 0)
        {
            var (db, at) = todo.Pop();
            if (!seen.Add((db, at)) || seen.Count > 10_000) continue;
            foreach (var ((_, _, texture), (from, value)) in db.Effective(at, game, memo, 0, ref lost))
            {
                if (texture) { if (from._textAt[value + 1] > from._textAt[value]) found.Add(from.Text((int)value)); continue; }
                if (value == 0) continue;
                int next = from.IndexOf(value);
                if (next < 0) { lost++; continue; }
                todo.Push((from, next));
            }
        }
        unresolved = lost;
        return found.ToList();
    }

    // ---------------------------------------------------------------- One material's parts, and the textures' users
    /// <summary>An object of a database: a material, or one of its parts (a layer, a blender, a texture set, ...).</summary>
    public readonly record struct Node(MaterialContents Db, int At)
    {
        /// <summary>True for a material: an object whose resource ID has the extension mat and that holds data.</summary>
        public bool IsMaterial => Db._pidExt[At] == MatExt && Db._hasData[At];
        /// <summary>True when its resource ID is a material's (the extension mat), with data of its own or not.</summary>
        public bool HasMaterialId => Db._pidExt[At] == MatExt;
        /// <summary>Its resource ID: the folder's hash, the file's hash, the extension's first 4 bytes.</summary>
        public (uint Dir, uint File, uint Ext) Id => Db.IdOf(At);
        /// <summary>"res:DIR:FILE:EXT", the way a .mat file writes an object's ID.</summary>
        public string IdText { get { var (d, f, e) = Id; return $"res:{d:X8}:{f:X8}:{e:X8}"; } }
        /// <summary>Its own name (BSComponentDB::CTName), or null when it has none of its own.</summary>
        public string? OwnName => Db._objName[At] is int n and >= 0 ? Db.NameText(n) : null;
    }

    /// <summary>One link or texture of an object, with what it inherits: the component's class and index, and either
    /// the texture's file name (as the database writes it; "" for a slot set to no file) or the object linked to.</summary>
    public readonly record struct NodeEntry(string Class, int Index, string? Texture, Node? Link, bool Lost);

    /// <summary>The working state of a walk over a database's objects, shared by many questions in one answer so that
    /// what an object inherits is worked out once.</summary>
    public sealed class Walk
    {
        internal readonly Dictionary<(MaterialContents, int), Dictionary<(uint, int, bool), (MaterialContents, uint)>> Memo = new();
        internal int Lost;
    }

    const uint MatExt = 0x0074616D;

    /// <summary>The material at this Data-relative path, when the database holds it with data.</summary>
    public Node? Find(string materialPath)
    {
        var (file, ext, dir) = MaterialDatabase.ResourceId(materialPath);
        int at = IndexOfPid(((ulong)dir << 32) | file, ext, needData: true);
        return at < 0 ? null : new Node(this, at);
    }

    /// <summary>Any object by its resource ID (a .mat file writes a part's parent as "res:DIR:FILE:EXT").</summary>
    public Node? Find(uint dir, uint file, uint ext)
    {
        int at = IndexOfPid(((ulong)dir << 32) | file, ext, needData: false);
        return at < 0 ? null : new Node(this, at);
    }

    int[]? _pidOf;
    (uint Dir, uint File, uint Ext) IdOf(int at)
    {
        var of = _pidOf;
        if (of == null)
        {
            of = new int[_byPidAt.Length];
            for (int i = 0; i < _byPidAt.Length; i++) of[_byPidAt[i]] = i;
            _pidOf = of;
        }
        ulong key = _byPid[of[at]];
        return ((uint)(key >> 32), (uint)key, _pidExt[at]);
    }

    /// <summary>An object's parent: in this database, or for a mod's object made from one of the game's, in
    /// <paramref name="game"/>. <paramref name="lost"/> is true when it has a parent that can't be found.</summary>
    public static Node? ParentOf(Node n, MaterialContents? game, out bool lost)
    {
        lost = false;
        int p = n.Db._parent[n.At];
        if (p >= 0) return new Node(n.Db, p);
        if (p != -2) return null;
        var (key, ext) = n.Db._foreignParent[n.At];
        int g = game == null || game == n.Db ? -1 : game.IndexOfPid(key, ext, needData: false);
        if (g >= 0) return new Node(game!, g);
        lost = true;
        return null;
    }

    /// <summary>An object's name: its own, or the nearest one it inherits from a parent.</summary>
    public static string? NameOf(Node n, MaterialContents? game)
    {
        for (int depth = 0; depth < 64; depth++)
        {
            if (n.OwnName is { } own) return own;
            if (ParentOf(n, game, out _) is not { } p) return null;
            n = p;
        }
        return null;
    }

    /// <summary>An object's links and textures with what it inherits, in the order of their classes and indexes.</summary>
    public static List<NodeEntry> EntriesOf(Node n, MaterialContents? game, Walk walk)
    {
        var list = new List<(int Path, NodeEntry Entry)>();
        foreach (var ((key, path, texture), (from, value)) in n.Db.Effective(n.At, game, walk.Memo, 0, ref walk.Lost))
        {
            string cls = ClassName((int)(key >> 16));
            int index = (int)(key & 0xFFFF);
            if (texture) { list.Add((path, new NodeEntry(cls, index, from.Text((int)value), null, false))); continue; }
            if (value == 0) continue;
            int next = from.IndexOf(value);
            list.Add((path, new NodeEntry(cls, index, null, next < 0 ? null : new Node(from, next), next < 0)));
        }
        return list.OrderBy(e => e.Entry.Class, StringComparer.Ordinal).ThenBy(e => e.Entry.Index).ThenBy(e => e.Path).Select(e => e.Entry).ToList();
    }

    /// <summary>The indexes of the texture names in this database that fit a test (the name as the database writes it).</summary>
    public HashSet<int> TexturesFitting(Func<string, bool> fits)
    {
        var found = new HashSet<int>();
        for (int i = 0; i + 1 < _textAt.Length; i++) if (fits(Text(i))) found.Add(i);
        return found;
    }

    /// <summary>Every texture name in the database, as it writes them.</summary>
    public IEnumerable<string> TextureNames()
    {
        for (int i = 0; i + 1 < _textAt.Length; i++) yield return Text(i);
    }

    /// <summary>Every texture an object of the database sets itself (not what it inherits), with its component's
    /// class and index: for a test of the slot names against the textures' own file names.</summary>
    internal IEnumerable<(string Class, int Index, string Texture)> OwnTextures()
    {
        for (int o = 0; o < _dbid.Length; o++)
            for (int c = _compStart[o]; c < _compStart[o + 1]; c++)
                for (int e = _compEntry[c]; e < _compEntry[c + 1]; e++)
                    if (_entries[e].Texture) yield return (ClassName((int)(_compKey[c] >> 16)), (int)(_compKey[c] & 0xFFFF), Text((int)_entries[e].Value));
    }

    /// <summary>
    /// The materials of this database that use one of the textures given (by database, the indexes of
    /// <see cref="TexturesFitting"/>), each with the entries that name them. A database whose objects all have their
    /// parents in it is searched from the objects that hold one of the textures up: the objects made from them, and
    /// those that link to them, up to the materials; each material found is then walked, as <see cref="TexturesOf"/>
    /// does, to keep only those that really end up with the texture. A database with parents in the game's has all
    /// its materials walked.
    /// </summary>
    public List<(Node Material, List<NodeEntry> Uses)> MaterialsUsing(IReadOnlyDictionary<MaterialContents, HashSet<int>> textures, MaterialContents? game, Walk walk)
    {
        var result = new List<(Node, List<NodeEntry>)>();
        IEnumerable<int> candidates;
        if (!textures.Any(t => t.Value.Count > 0 && (t.Key == this || (HasForeignParents && t.Key == game)))) return result;
        if (HasForeignParents) candidates = Enumerable.Range(0, _dbid.Length).Where(i => _pidExt[i] == MatExt && _hasData[i]);
        else
        {
            if (!textures.TryGetValue(this, out var mine) || mine.Count == 0) return result;
            int n = _dbid.Length;
            // Who is made from whom (children by parent), who links to whom (linkers by target), who holds a texture
            var children = new int[n + 1];
            for (int i = 0; i < n; i++) if (_parent[i] >= 0) children[_parent[i] + 1]++;
            for (int i = 0; i < n; i++) children[i + 1] += children[i];
            var childList = new int[children[n]];
            var fill = (int[])children.Clone();
            for (int i = 0; i < n; i++) if (_parent[i] >= 0) childList[fill[_parent[i]]++] = i;
            var linkers = new int[n + 1];
            var reached = new bool[n];
            var queue = new Queue<int>();
            for (int o = 0; o < n; o++)
                for (int c = _compStart[o]; c < _compStart[o + 1]; c++)
                    for (int e = _compEntry[c]; e < _compEntry[c + 1]; e++)
                    {
                        var en = _entries[e];
                        if (en.Texture) { if (!reached[o] && mine.Contains((int)en.Value)) { reached[o] = true; queue.Enqueue(o); } }
                        else if (en.Value != 0 && IndexOf(en.Value) is int t and >= 0) linkers[t + 1]++;
                    }
            for (int i = 0; i < n; i++) linkers[i + 1] += linkers[i];
            var linkerList = new int[linkers[n]];
            fill = (int[])linkers.Clone();
            for (int o = 0; o < n; o++)
                for (int c = _compStart[o]; c < _compStart[o + 1]; c++)
                    for (int e = _compEntry[c]; e < _compEntry[c + 1]; e++)
                        if (!_entries[e].Texture && _entries[e].Value != 0 && IndexOf(_entries[e].Value) is int t and >= 0) linkerList[fill[t]++] = o;
            var found = new List<int>();
            while (queue.Count > 0)
            {
                int o = queue.Dequeue();
                if (_pidExt[o] == MatExt && _hasData[o]) found.Add(o);
                for (int k = children[o]; k < children[o + 1]; k++) if (!reached[childList[k]]) { reached[childList[k]] = true; queue.Enqueue(childList[k]); }
                for (int k = linkers[o]; k < linkers[o + 1]; k++) if (!reached[linkerList[k]]) { reached[linkerList[k]] = true; queue.Enqueue(linkerList[k]); }
            }
            found.Sort();
            candidates = found;
        }
        foreach (int m in candidates)
        {
            var uses = Uses(new Node(this, m), textures, game, walk);
            if (uses.Count > 0) result.Add((new Node(this, m), uses));
        }
        return result;
    }

    /// <summary>The entries, anywhere in a material's parts, that name one of the textures given; each entry's Link is
    /// the object (with what it inherits) that names it.</summary>
    static List<NodeEntry> Uses(Node start, IReadOnlyDictionary<MaterialContents, HashSet<int>> textures, MaterialContents? game, Walk walk)
    {
        var uses = new List<NodeEntry>();
        var seen = new HashSet<(MaterialContents, int)>();
        var todo = new Stack<(MaterialContents Db, int At)>();
        todo.Push((start.Db, start.At));
        while (todo.Count > 0)
        {
            var (db, at) = todo.Pop();
            if (!seen.Add((db, at)) || seen.Count > 10_000) continue;
            foreach (var ((key, _, texture), (from, value)) in db.Effective(at, game, walk.Memo, 0, ref walk.Lost))
            {
                if (texture)
                {
                    if (textures.TryGetValue(from, out var set) && set.Contains((int)value))
                        uses.Add(new NodeEntry(ClassName((int)(key >> 16)), (int)(key & 0xFFFF), from.Text((int)value), new Node(db, at), false));
                    continue;
                }
                if (value == 0) continue;
                int next = from.IndexOf(value);
                if (next >= 0) todo.Push((from, next));
            }
        }
        return uses;
    }

    /// <summary>An object's links and textures with what it inherits: its parent's, then its own components over them
    /// (an OBJT component replaces the parent's of the same type and index, a DIFF changes the fields it holds).</summary>
    Dictionary<(uint Key, int Path, bool Texture), (MaterialContents From, uint Value)> Effective(int at, MaterialContents? game,
        Dictionary<(MaterialContents, int), Dictionary<(uint, int, bool), (MaterialContents, uint)>> memo, int depth, ref int lost)
    {
        if (memo.TryGetValue((this, at), out var have)) return have;
        var mine = new Dictionary<(uint, int, bool), (MaterialContents, uint)>();
        if (depth < 64)
        {
            int p = _parent[at];
            if (p >= 0) foreach (var kv in Effective(p, game, memo, depth + 1, ref lost)) mine[kv.Key] = kv.Value;
            else if (p == -2)
            {
                var (key, ext) = _foreignParent[at];
                int g = game == null || game == this ? -1 : game.IndexOfPid(key, ext, needData: false);
                if (g >= 0) foreach (var kv in game!.Effective(g, game, memo, depth + 1, ref lost)) mine[kv.Key] = kv.Value;
                else lost++;
            }
        }
        for (int c = _compStart[at]; c < _compStart[at + 1]; c++)
        {
            uint key = _compKey[c];
            if (!_compDiff[c]) foreach (var k in mine.Keys.Where(k => k.Item1 == key).ToList()) mine.Remove(k);
            for (int e = _compEntry[c]; e < _compEntry[c + 1]; e++)
                mine[(key, _entries[e].Path, _entries[e].Texture)] = (this, _entries[e].Value);
        }
        return memo[(this, at)] = mine;
    }

    /// <summary>Reads a whole database from a stream that starts at its first byte. Throws InvalidDataException or
    /// EndOfStreamException when the file isn't one this reader knows.</summary>
    public static MaterialContents Read(Stream stream, long size)
    {
        var c = new MaterialContents { Size = size };
        var watch = Stopwatch.StartNew();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        new Reader(stream, c).Run();
        c.Seconds = watch.Elapsed.TotalSeconds;
        c.Allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        c.Kept = c._dbid.Length * (4L + 4 + 1 + 4 + 8 + 4 + 4 + 4) + (c._byDbidAt == null ? 0 : c._dbid.Length * 8L) + c._compKey.Length * (4L + 1 + 4)
            + c._entries.Length * 12L + c._text.Length + c._textAt.Length * 4L + c._foreignParent.Count * 40L + c._names.Length + c._nameAt.Length * 4L;
        return c;
    }

    const uint Builtin = 0xFFFFFF01;
    const int KString = 1, KList = 2, KMap = 3, KRef = 4;

    /// <summary>A number for each component class name, the same for every database of the process, so that a mod's
    /// component and the game's it inherits are matched by class.</summary>
    static readonly Dictionary<string, int> ClassIds = new(StringComparer.Ordinal);
    static readonly List<string> ClassNames = new() { "" };
    static int ClassId(string name)
    {
        lock (ClassIds)
        {
            if (ClassIds.TryGetValue(name, out int id)) return id;
            ClassNames.Add(name);
            return ClassIds[name] = ClassNames.Count - 1;
        }
    }
    static string ClassName(int id) { lock (ClassIds) return id > 0 && id < ClassNames.Count ? ClassNames[id] : "?"; }

    sealed class ClassDef
    {
        public required string Name { get; init; }
        public ushort Flags { get; init; }
        public (string Name, uint Type)[] Fields { get; init; } = Array.Empty<(string, uint)>();
        public bool User => (Flags & 4) != 0;
        /// <summary>0 any class; 1 a BSComponentDB2::ID (a link); 2 a texture file (FileName); 3 an object's name
        /// (BSComponentDB::CTName, its field Name in FileNameField).</summary>
        public int Special { get; set; }
        public int FileNameField { get; set; } = -1;
        public int Global { get; init; }
    }

    /// <summary>Reads the chunks one after another; a list's or a map's chunk is read when its object reaches it.</summary>
    sealed class Reader
    {
        readonly Stream _s;
        readonly MaterialContents _c;
        long _pos;
        uint _chunksLeft;
        byte[] _table = Array.Empty<byte>();
        readonly Dictionary<uint, ClassDef> _classes = new();
        readonly List<byte[]> _buffers = new();
        readonly Dictionary<string, int> _stringAt = new(StringComparer.Ordinal);
        readonly List<string> _strings = new();
        readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _stringLookup;
        // The objects' names: their bytes one after another, found again by a hash of the bytes (no string is made)
        byte[] _nameBytes = new byte[1 << 16];
        readonly List<int> _nameStarts = new() { 0 };
        Dictionary<ulong, int> _nameByHash = new();
        int _pendingName = -1;
        readonly char[] _chars = new char[65536];
        // The index, while the components are read
        uint[] _compObject = Array.Empty<uint>();
        uint[] _compTypeIndex = Array.Empty<uint>();
        UInt128[] _parentPid = Array.Empty<UInt128>();
        uint[] _parentDbid = Array.Empty<uint>();
        bool _haveIndex;
        // The components read so far that hold something
        readonly List<(int Object, uint Key, bool Diff, int Start, int Count)> _found = new();
        readonly List<Entry> _entries = new();
        int _componentsSeen;

        public Reader(Stream s, MaterialContents c) { _s = s; _c = c; _stringLookup = _stringAt.GetAlternateLookup<ReadOnlySpan<char>>(); }

        void Exactly(Span<byte> into) { _s.ReadExactly(into); _pos += into.Length; }
        uint U32() { Span<byte> b = stackalloc byte[4]; Exactly(b); return BinaryPrimitives.ReadUInt32LittleEndian(b); }
        (uint Type, uint Size) Header()
        {
            if (_chunksLeft == 0) throw new InvalidDataException("an object needs more chunks than the file holds");
            _chunksLeft--;
            Span<byte> b = stackalloc byte[8];
            Exactly(b);
            return (BinaryPrimitives.ReadUInt32LittleEndian(b), BinaryPrimitives.ReadUInt32LittleEndian(b[4..]));
        }
        static uint Tag(string s) => BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(s));
        static readonly uint BETH = Tag("BETH"), STRT = Tag("STRT"), TYPE = Tag("TYPE"), CLAS = Tag("CLAS"), OBJT = Tag("OBJT"), DIFF = Tag("DIFF"),
            LIST = Tag("LIST"), MAPC = Tag("MAPC"), USER = Tag("USER"), USRD = Tag("USRD");

        string Str(uint offset)
        {
            if (offset >= _table.Length) throw new InvalidDataException($"a name points outside the string table (offset {offset})");
            int end = Array.IndexOf(_table, (byte)0, (int)offset);
            return Encoding.ASCII.GetString(_table, (int)offset, (end < 0 ? _table.Length : end) - (int)offset);
        }

        ClassDef Class(uint type) => _classes.TryGetValue(type, out var c) ? c : throw new InvalidDataException($"an object has a type with no class definition ({type:X8})");

        /// <summary>A chunk's bytes, in a buffer kept for its depth (a list's chunk is read while its object's is still in use).</summary>
        Span<byte> Body(uint size, int depth)
        {
            while (_buffers.Count <= depth) _buffers.Add(new byte[4096]);
            if (_buffers[depth].Length < size) _buffers[depth] = new byte[Math.Max(size, _buffers[depth].Length * 2)];
            var span = _buffers[depth].AsSpan(0, (int)size);
            Exactly(span);
            return span;
        }

        public void Run()
        {
            var (type, size) = (U32(), U32());
            if (type != BETH || size != 8) throw new InvalidDataException("it doesn't start with a BETH header: not a compiled material database");
            uint version = U32();
            if (version != 4) throw new InvalidDataException($"its format version is {version}; this reader knows version 4");
            _chunksLeft = U32() - 1;
            (type, size) = Header();
            if (type != STRT) throw new InvalidDataException("it has no string table after its header");
            _table = new byte[size];
            Exactly(_table);
            (type, size) = Header();
            if (type != TYPE || size < 4) throw new InvalidDataException("it has no class definitions after its string table");
            uint classes = U32();
            if (size > 4) { var rest = new byte[size - 4]; Exactly(rest); }
            for (uint i = 0; i < classes; i++)
            {
                (type, size) = Header();
                if (type != CLAS || size < 12) throw new InvalidDataException("a class definition is missing or cut short");
                var d = Body(size, 0);
                uint nameAt = BinaryPrimitives.ReadUInt32LittleEndian(d);
                var fields = new List<(string, uint)>();
                for (int at = 12; at + 12 <= d.Length; at += 12)
                    fields.Add((Str(BinaryPrimitives.ReadUInt32LittleEndian(d[at..])), BinaryPrimitives.ReadUInt32LittleEndian(d[(at + 4)..])));
                string className = Str(nameAt);
                var def = new ClassDef { Name = className, Global = ClassId(className), Flags = BinaryPrimitives.ReadUInt16LittleEndian(d[8..]), Fields = fields.ToArray() };
                if (def.Name == "BSComponentDB2::ID" && def.Fields.Length == 1 && def.Fields[0].Type == Builtin + 12) def.Special = 1;
                if (def.Name is "BSMaterial::MRTextureFile" or "BSMaterial::TextureFile")
                {
                    def.FileNameField = Array.FindIndex(def.Fields, f => f.Name == "FileName" && f.Type == Builtin + KString);
                    if (def.FileNameField >= 0) def.Special = 2;
                }
                if (def.Name == "BSComponentDB::CTName")
                {
                    def.FileNameField = Array.FindIndex(def.Fields, f => f.Name == "Name" && f.Type == Builtin + KString);
                    if (def.FileNameField >= 0) def.Special = 3;
                }
                _classes[nameAt] = def;
            }
            if (!_classes.Values.Any(c => c.Special == 2)) throw new InvalidDataException("it has no texture file class (BSMaterial::MRTextureFile): the layout isn't the one this reader knows");

            while (_chunksLeft > 0)
            {
                (type, size) = Header();
                if (type != OBJT && type != DIFF) throw new InvalidDataException($"a {Encoding.ASCII.GetString(BitConverter.GetBytes(type))} chunk stands where an object was expected");
                var body = Body(size, 0);
                if (body.Length < 4) throw new InvalidDataException("an object chunk is cut short");
                uint cls = BinaryPrimitives.ReadUInt32LittleEndian(body);
                var def = Class(cls);
                bool component = _haveIndex;
                int start = _entries.Count;
                _pendingName = -1;
                var c = new Cur(_buffers[0], (int)size) { P = 4 };
                Item(ref c, cls, type == DIFF, 0, 1, component);
                if (c.P != c.End) throw new InvalidDataException($"an object of class {def.Name} holds {c.End - c.P} bytes more than its fields");
                if (def.Name == "BSComponentDB2::DBFileIndex") { _haveIndex = true; continue; }
                if (!component) continue;
                if (_componentsSeen >= _compObject.Length) throw new InvalidDataException("it holds more component objects than its component list");
                int k = _componentsSeen++;
                if (_entries.Count > start || _pendingName >= 0)
                {
                    int obj = _c.IndexOf(_compObject[k]);
                    if (obj < 0) throw new InvalidDataException($"a component belongs to object {_compObject[k]}, which isn't in the object list");
                    if (_pendingName >= 0) _c._objName[obj] = _pendingName;
                    if (_entries.Count == start) continue;
                    // Keyed by the class's name (the same in every database) and the component's index
                    _found.Add((obj, ((uint)def.Global << 16) | (_compTypeIndex[k] & 0xFFFF), type == DIFF, start, _entries.Count - start));
                }
            }
            if (!_haveIndex) throw new InvalidDataException("it has no index (BSComponentDB2::DBFileIndex)");
            if (_componentsSeen != _compObject.Length) throw new InvalidDataException($"its component list has {_compObject.Length:N0} components and the file {_componentsSeen:N0}");
            Finish();
        }

        struct Cur
        {
            public byte[] B; public int P; public int End;
            public Cur(byte[]? b, int end) { B = b ?? Array.Empty<byte>(); P = 0; End = end; }
            public void Need(int n) { if (P + n > End) throw new InvalidDataException("a value runs past the end of its chunk"); }
            public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(B.AsSpan(P)); P += 2; return v; }
            public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(B.AsSpan(P)); P += 4; return v; }
        }

        static int Down(int path, int step) => unchecked(path * 31 + step);

        /// <summary>Reads one value of a type at the cursor. <paramref name="keep"/>: links and textures found are kept
        /// (a component's data), at their place <paramref name="path"/>.</summary>
        void Item(ref Cur c, uint type, bool diff, int path, int depth, bool keep)
        {
            if (depth > 32) throw new InvalidDataException("the objects nest too deep");
            if (type >= Builtin)
            {
                switch ((int)(type - Builtin))
                {
                    case 0: return;
                    case KString: { int n = c.U16(); c.Need(n); c.P += n; return; }
                    case KList: List(type, diff, path, depth, keep); return;
                    case KMap: Map(diff, path, depth, keep); return;
                    case KRef: { uint t = c.U32(); Item(ref c, t, diff, path, depth + 1, keep); return; }
                    case 7 or 8 or 15: c.Need(1); c.P += 1; return;
                    case 9 or 10: c.Need(2); c.P += 2; return;
                    case 11 or 12 or 16: c.Need(4); c.P += 4; return;
                    case 13 or 14 or 17: c.Need(8); c.P += 8; return;
                    default: throw new InvalidDataException($"a value has a built-in type this reader doesn't know ({type - Builtin})");
                }
            }
            var def = Class(type);
            if (def.User) { User(type, def, path, depth, keep); return; }
            Fields(ref c, def, diff, path, depth, keep);
        }

        void Fields(ref Cur c, ClassDef def, bool diff, int path, int depth, bool keep)
        {
            int n = -1;
            while (true)
            {
                if (!diff) { if (++n >= def.Fields.Length) return; }
                else
                {
                    if (c.P + 2 > c.End) return;
                    n = c.U16();
                    if (n >= 0x8000) return;
                    if (n >= def.Fields.Length) throw new InvalidDataException($"a change to {def.Name} names field {n} of {def.Fields.Length}");
                }
                var (_, ftype) = def.Fields[n];
                if (keep && def.Special == 1)
                {
                    c.Need(4);
                    _entries.Add(new Entry { Path = path, Value = BinaryPrimitives.ReadUInt32LittleEndian(c.B.AsSpan(c.P)) });
                    c.P += 4;
                    continue;
                }
                if (keep && def.Special == 2 && n == def.FileNameField)
                {
                    int len = c.U16();
                    c.Need(len);
                    _entries.Add(new Entry { Path = path, Value = (uint)Intern(c.B.AsSpan(c.P, len), _stringLookup, _stringAt, _strings), Texture = true });
                    c.P += len;
                    continue;
                }
                if (keep && def.Special == 3 && n == def.FileNameField && depth <= 2)
                {
                    // An object's name, kept apart from its links and textures: one for each object
                    int len = c.U16();
                    c.Need(len);
                    _pendingName = InternName(c.B.AsSpan(c.P, len));
                    c.P += len;
                    continue;
                }
                Item(ref c, ftype, diff, Down(path, n + 1), depth + 1, keep);
            }
        }

        int InternName(ReadOnlySpan<byte> bytes)
        {
            int end = bytes.IndexOf((byte)0);
            if (end >= 0) bytes = bytes[..end];
            ulong hash = System.IO.Hashing.XxHash3.HashToUInt64(bytes);
            int count = _nameStarts.Count - 1;
            if (_nameByHash.TryGetValue(hash, out int at) && _nameBytes.AsSpan(_nameStarts[at], _nameStarts[at + 1] - _nameStarts[at]).SequenceEqual(bytes)) return at;
            int start = _nameStarts[^1];
            if (start + bytes.Length > _nameBytes.Length) Array.Resize(ref _nameBytes, Math.Max(_nameBytes.Length * 2, start + bytes.Length));
            bytes.CopyTo(_nameBytes.AsSpan(start));
            _nameStarts.Add(start + bytes.Length);
            // Two names with one hash: the second is kept as its own name, not found again
            _nameByHash.TryAdd(hash, count);
            return count;
        }

        int Intern(ReadOnlySpan<byte> bytes, Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> lookup, Dictionary<string, int> index, List<string> list)
        {
            int end = bytes.IndexOf((byte)0);
            if (end >= 0) bytes = bytes[..end];
            int n = Encoding.Latin1.GetChars(bytes, _chars);
            var span = _chars.AsSpan(0, n);
            if (lookup.TryGetValue(span, out int at)) return at;
            string s = new(span);
            list.Add(s);
            return index[s] = list.Count - 1;
        }

        void List(uint listType, bool diff, int path, int depth, bool keep)
        {
            var (type, size) = Header();
            if (type != LIST) throw new InvalidDataException("a list's chunk isn't a LIST chunk");
            if (size < 4) throw new InvalidDataException("a list's chunk is cut short");
            uint element = U32();
            if (size == 4) return;
            uint count = U32();
            if (element < Builtin && !_haveIndex && depth <= 2)
            {
                string name = Class(element).Name;
                if (name == "BSComponentDB2::DBFileIndex::ObjectInfo") { Objects(element, count, size - 8); return; }
                if (name == "BSComponentDB2::DBFileIndex::ComponentInfo") { ComponentList(element, count, size - 8); return; }
                if (name == "BSComponentDB2::DBFileIndex::EdgeInfo") { Skip(size - 8); return; }
            }
            Body(size - 8, depth);
            var c = new Cur(_buffers[depth], (int)(size - 8));
            for (uint i = 0; i < count; i++) Item(ref c, element, diff, Down(path, 0x10000 + (int)Math.Min(i, 0xFFFF)), depth + 1, keep);
            if (c.P != c.End) throw new InvalidDataException("a list holds more bytes than its elements");
        }

        void Map(bool diff, int path, int depth, bool keep)
        {
            var (type, size) = Header();
            if (type != MAPC || size < 12) throw new InvalidDataException("a map's chunk isn't a MAPC chunk, or is cut short");
            Body(size, depth);
            var c = new Cur(_buffers[depth], (int)size);
            uint keyType = c.U32(), valueType = c.U32(), count = c.U32();
            for (uint i = 0; i < count; i++)
            {
                Item(ref c, keyType, false, 0, depth + 1, false);
                Item(ref c, valueType, diff, Down(path, 0x20000 + (int)Math.Min(i, 0xFFFF)), depth + 1, keep);
            }
            if (c.P != c.End) throw new InvalidDataException("a map holds more bytes than its pairs");
        }

        void User(uint type, ClassDef def, int path, int depth, bool keep)
        {
            var (chunk, size) = Header();
            if ((chunk != USER && chunk != USRD) || size < 8) throw new InvalidDataException($"the value of {def.Name} isn't a USER chunk");
            Body(size, depth);
            var c = new Cur(_buffers[depth], (int)size);
            if (c.U32() != type) throw new InvalidDataException($"a USER chunk for {def.Name} holds another class");
            bool diff = chunk == USRD;
            uint as_ = c.U32();
            if (as_ == type) Fields(ref c, def, diff, path, depth + 1, keep);
            else if (as_ >= Builtin)
            {
                // Values one after another, each with its type before the next; a type that isn't built-in ends them
                for (int i = 0; ; i++)
                {
                    Item(ref c, as_, diff, Down(path, i + 1), depth + 1, keep);
                    if (c.P + 4 > c.End) break;
                    as_ = c.U32();
                    if (as_ < Builtin) break;
                }
            }
            // A user chunk may end with a 32-bit number past its fields (BSBind::Directory, BSFloatCurve): not needed
        }

        byte[] _skip = Array.Empty<byte>();
        void Skip(long n)
        {
            if (_skip.Length == 0) _skip = new byte[1 << 16];
            while (n > 0) { int k = (int)Math.Min(n, _skip.Length); Exactly(_skip.AsSpan(0, k)); n -= k; }
        }

        /// <summary>Where a fixed-size list entry's fields sit, from the class definition.</summary>
        Dictionary<string, (int At, int Size)> Layout(uint element, out int entry)
        {
            var at = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            int pos = 0;
            foreach (var (name, type) in Class(element).Fields) { int n = SizeOf(type, 0); at[name] = (pos, n); pos += n; }
            entry = pos;
            return at;
            int SizeOf(uint t, int depth)
            {
                if (t >= Builtin)
                    return (t - Builtin) switch
                    {
                        7 or 8 or 15 => 1, 9 or 10 => 2, 11 or 12 or 16 => 4, 13 or 14 or 17 => 8,
                        _ => throw new InvalidDataException($"a field of {Class(element).Name} has a type of no fixed size"),
                    };
                if (depth > 4) throw new InvalidDataException($"the types of {Class(element).Name} nest too deep");
                return Class(t).Fields.Sum(f => SizeOf(f.Type, depth + 1));
            }
        }

        /// <summary>Reads a fixed-size list a block of entries at a time (the game's object list is 16.5 MB).</summary>
        void Blocks(uint count, int entry, long size, string what, Action<int, ReadOnlySpan<byte>> each)
        {
            if ((long)count * entry != size) throw new InvalidDataException($"its {what} list holds {size:N0} bytes for {count:N0} entries of {entry} bytes");
            var block = new byte[entry * Math.Max(1, Math.Min((int)Math.Max(1, count), 65536 / Math.Max(1, entry)))];
            for (long done = 0; done < count;)
            {
                int n = (int)Math.Min(count - done, block.Length / entry);
                Exactly(block.AsSpan(0, n * entry));
                for (int k = 0; k < n; k++) each((int)done + k, block.AsSpan(k * entry, entry));
                done += n;
            }
        }

        void Objects(uint element, uint count, long size)
        {
            var at = Layout(element, out int entry);
            if (!at.TryGetValue("PersistentID", out var id) || id.Size != 12 || !at.TryGetValue("DBID", out var dbid) || !at.TryGetValue("Parent", out var parent) || !at.TryGetValue("HasData", out var hasData))
                throw new InvalidDataException("its object list lacks PersistentID, DBID, Parent or HasData");
            bool hasParentId = at.TryGetValue("ParentPersistentID", out var parentId) && parentId.Size == 12;
            int n = (int)count;
            _c._dbid = new uint[n]; _c._byPid = new ulong[n]; _c._pidExt = new uint[n]; _c._hasData = new bool[n];
            _c._objName = new int[n];
            Array.Fill(_c._objName, -1);
            // Nearly every object has a name of its own (the game's: 48,740 of 48,749 materials): room for one each, about 26 bytes
            _nameByHash = new Dictionary<ulong, int>(n);
            _nameStarts.Capacity = n + 1;
            _nameBytes = new byte[Math.Max(1 << 16, n * 26L > int.MaxValue / 2 ? 1 << 16 : n * 26)];
            _parentDbid = new uint[n]; _parentPid = new UInt128[n];
            Blocks(count, entry, size, "object", (i, e) =>
            {
                // A BSResource::ID's bytes: the file's hash, the extension, the folder's hash
                uint file = BinaryPrimitives.ReadUInt32LittleEndian(e[id.At..]);
                _c._pidExt[i] = BinaryPrimitives.ReadUInt32LittleEndian(e[(id.At + 4)..]);
                _c._byPid[i] = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(e[(id.At + 8)..]) << 32) | file;
                _c._dbid[i] = BinaryPrimitives.ReadUInt32LittleEndian(e[dbid.At..]);
                _parentDbid[i] = BinaryPrimitives.ReadUInt32LittleEndian(e[parent.At..]);
                if (hasParentId)
                {
                    uint pf = BinaryPrimitives.ReadUInt32LittleEndian(e[parentId.At..]);
                    uint pe = BinaryPrimitives.ReadUInt32LittleEndian(e[(parentId.At + 4)..]);
                    uint pd = BinaryPrimitives.ReadUInt32LittleEndian(e[(parentId.At + 8)..]);
                    _parentPid[i] = ((UInt128)pe << 64) | ((UInt128)pd << 32) | pf;
                }
                _c._hasData[i] = e[hasData.At] != 0;
            });
            bool ordered = true;
            for (int i = 1; i < n && ordered; i++) ordered = _c._dbid[i - 1] < _c._dbid[i];
            if (ordered) _c._byDbid = _c._dbid;
            else
            {
                _c._byDbid = (uint[])_c._dbid.Clone();
                _c._byDbidAt = Enumerable.Range(0, n).ToArray();
                Array.Sort(_c._byDbid, _c._byDbidAt);
            }
            _c._byPidAt = Enumerable.Range(0, n).ToArray();
            Array.Sort(_c._byPid, _c._byPidAt);
        }

        void ComponentList(uint element, uint count, long size)
        {
            var at = Layout(element, out int entry);
            if (!at.TryGetValue("ObjectID", out var obj) || obj.Size != 4 || !at.TryGetValue("Index", out var index) || index.Size != 2 || !at.TryGetValue("Type", out var type) || type.Size != 2)
                throw new InvalidDataException("its component list lacks ObjectID, Index or Type");
            _compObject = new uint[count];
            _compTypeIndex = new uint[count];
            // About half the components hold a link or a texture (the game's: 699,839 of 1,438,778)
            _found.Capacity = (int)Math.Min(count / 2 + 16, int.MaxValue / 64);
            _entries.Capacity = _found.Capacity;
            Blocks(count, entry, size, "component", (i, e) =>
            {
                _compObject[i] = BinaryPrimitives.ReadUInt32LittleEndian(e[obj.At..]);
                _compTypeIndex[i] = ((uint)BinaryPrimitives.ReadUInt16LittleEndian(e[type.At..]) << 16) | BinaryPrimitives.ReadUInt16LittleEndian(e[index.At..]);
            });
        }

        void Finish()
        {
            int n = _c._dbid.Length;
            _c._parent = new int[n];
            for (int i = 0; i < n; i++)
            {
                uint p = _parentDbid[i];
                if (p == 0) { _c._parent[i] = -1; continue; }
                int local = _c.IndexOf(p);
                if (local >= 0) { _c._parent[i] = local; continue; }
                var pid = _parentPid[i];
                if (pid == 0) { _c._parent[i] = -1; continue; }
                // A parent in another database (the game's), known by its PersistentID: folder and file hashes, extension
                _c._parent[i] = -2;
                _c._foreignParent[i] = ((ulong)(pid & ulong.MaxValue), (uint)(pid >> 64));
            }
            // The components that hold something, grouped by object in the order they came (a counting sort)
            _c._compStart = new int[n + 1];
            foreach (var f in _found) _c._compStart[f.Object + 1]++;
            for (int i = 0; i < n; i++) _c._compStart[i + 1] += _c._compStart[i];
            var order = new int[_found.Count];
            var next = (int[])_c._compStart.Clone();
            for (int i = 0; i < _found.Count; i++) order[next[_found[i].Object]++] = i;
            _c._compKey = new uint[order.Length];
            _c._compDiff = new bool[order.Length];
            _c._compEntry = new int[order.Length + 1];
            var entries = new Entry[_entries.Count];
            int e = 0;
            for (int k = 0; k < order.Length; k++)
            {
                var f = _found[order[k]];
                _c._compKey[k] = f.Key;
                _c._compDiff[k] = f.Diff;
                _c._compEntry[k] = e;
                for (int j = 0; j < f.Count; j++) entries[e++] = _entries[f.Start + j];
            }
            _c._compEntry[order.Length] = e;
            _c._entries = entries;
            _c._textAt = new int[_strings.Count + 1];
            for (int i = 0; i < _strings.Count; i++) _c._textAt[i + 1] = _c._textAt[i] + _strings[i].Length;
            _c._text = new byte[_c._textAt[^1]];
            for (int i = 0; i < _strings.Count; i++) Encoding.Latin1.GetBytes(_strings[i], _c._text.AsSpan(_c._textAt[i]));
            _c._nameAt = _nameStarts.ToArray();
            int used = _nameStarts[^1];
            _c._names = _nameBytes.Length - used > used / 8 ? _nameBytes.AsSpan(0, used).ToArray() : _nameBytes;
            _c.Components = _componentsSeen;
            _c.BytesRead = _pos;
        }
    }
}
