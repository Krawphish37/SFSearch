// A compiled material database (materialsbeta.cdb): which materials it holds. Read only, and only that far.
//
// Starfield compiles its materials into one database, materials\materialsbeta.cdb in Starfield - Materials.ba2. A mod
// may ship its own (materials\creations\<name>\materialsbeta.cdb). A material in a database is no .mat file in Data,
// so the materials report looks it up here. A material is known by a resource ID made from its path, not by its
// name: the database holds no material paths, so its materials can be looked up but not listed by name.
//
// The format, as this reader understands it. It was learned from the description in fo76utils' NifSkope fork
// (github.com/fo76utils/nifskope, lib/libfo76utils/src/bsrefl.cpp and bsmatcdb.cpp, MIT licence); no code was copied.
// Every point below was checked against the 21 databases of the Data folder this was written on (fix-f4 note).
//  - The file is a run of chunks: a 4-letter type, a 32-bit size, then that many bytes. Little-endian throughout.
//  - "BETH" first: size 8, version 4, the number of chunks (this one included).
//  - "STRT": the string table, names ending in a zero byte. A class or field is named by its offset in this table;
//    a built-in type by 0xFFFFFF01 + n (n: 1 string, 2 list, 3 map, 7 to 14 the integers from int8 to uint64,
//    15 bool, 16 float, 17 double).
//  - "TYPE": the number of class definitions, then one "CLAS" chunk each: the class name, a version (32 bits), flags
//    (16), the number of fields (16), then each field: its name, its type, an offset (16) and a size (16).
//  - Then the objects: OBJT, DIFF, LIST, MAPC, USER and USRD chunks, each starting with the offset of its class name.
//  - The database's index is a BSComponentDB2::DBFileIndex. Its object list is the first LIST chunk whose class is
//    BSComponentDB2::DBFileIndex::ObjectInfo: the class, a count, then that many entries of fixed size, laid out as
//    the CLAS chunk lists them: PersistentID (a BSResource::ID, 12 bytes), DBID (4), Parent (4), ParentPersistentID
//    (12, in databases from game version 1.11.33 on; older ones have 21-byte entries without it) and HasData (1).
//  - A BSResource::ID is stored as three 32-bit numbers: a CRC-32 of the file name without its extension, the
//    extension's first 4 bytes ("mat\0"), a CRC-32 of the folder ("materials\weapons\x"). The CLAS chunk lists them as
//    Dir, File, Ext; the bytes hold File, Ext, Dir. Lower case, backslashes. The CRC-32 is the reflected 0xEDB88320
//    one started at 0 with no final XOR, the same as the name hashes of a BA2 archive.
//  - A material compiled into the database is an entry whose PersistentID has the extension "mat" and whose HasData
//    is 1. An entry with HasData 0 holds no data of its own and isn't counted (each of the 21 databases has 6 such).
//  - Reading stops after that list: in the game's database it ends 17.5 MB into 105 MB.
// The CRC-32 has 2^64 values for a folder and a name together, so two paths that read as one are possible but not
// likely: a lookup that finds a material says "an entry with this path's ID is there".
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace SFSearch.Reports;

public sealed class MaterialDatabase
{
    /// <summary>The database's path as Data names it: "materials\materialsbeta.cdb".</summary>
    public required string Path { get; init; }
    /// <summary>"loose", or the archive's name.</summary>
    public required string Where { get; init; }
    /// <summary>The archive's full path, or the loose file's.</summary>
    public required string Container { get; init; }
    /// <summary>The build that compiled it ("1.16.244.0"), when the database says.</summary>
    public string? BuildVersion { get; private set; }
    /// <summary>Why the database couldn't be read. Null when it was.</summary>
    public string? Error { get; private set; }
    /// <summary>The database's size, and how many of its bytes were read to get its material list.</summary>
    public long Size { get; private set; }
    public long BytesRead { get; private set; }
    /// <summary>How long the read took, and how many bytes it allocated.</summary>
    public double Seconds { get; private set; }
    public long Allocated { get; private set; }
    /// <summary>The entries with a material's ID and no data of their own (HasData 0).</summary>
    public int References { get; private set; }

    ulong[] _ids = Array.Empty<ulong>();
    /// <summary>How many materials the database holds.</summary>
    public int Count => _ids.Length;

    /// <summary>The game's own database: materials\materialsbeta.cdb in one of the game's archives.</summary>
    public bool IsGame => IsGamePath(Path, Where);
    public const string GamePath = @"materials\materialsbeta.cdb";

    /// <summary>The game's own database by its path and archive: materials\materialsbeta.cdb (or materialsfinal.cdb, the
    /// other name the game's executable has for it) in a "Starfield - " archive.</summary>
    public static bool IsGamePath(string path, string? archive) =>
        archive != null && System.IO.Path.GetFileName(archive).StartsWith("Starfield - ", StringComparison.OrdinalIgnoreCase)
        && (path.Equals(GamePath, StringComparison.OrdinalIgnoreCase) || path.Equals(@"materials\materialsfinal.cdb", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the game reads a database at this path in this archive (null: loose in Data). Learned from the game's
    /// executable (Starfield.exe; the Creation Kit's is the same), not from a test in the game: it names its own
    /// database as "Materials/" with "MaterialsBeta.cdb" or "MaterialsFinal.cdb", and, in the code that reads a
    /// plugin's archives, keeps the extension ".cdb" beside the pattern ".* - Main[0-9]*\.ba2". So the databases
    /// that count are the game's own and every .cdb, at any path, in an archive named "&lt;plugin&gt; - Main.ba2",
    /// "- Main01.ba2" and so on: all 21 of the Data folder this was written on. A .cdb loose in Data, or in another
    /// archive (" - Textures.ba2"), isn't read.
    /// </summary>
    public static bool GameReads(string path, string? archive) =>
        IsGamePath(path, archive) || (archive != null && MainArchive.IsMatch(System.IO.Path.GetFileName(archive)));
    static readonly System.Text.RegularExpressions.Regex MainArchive = new(@"^.* - Main[0-9]*\.ba2$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>"the game's materials\materialsbeta.cdb (Starfield - Materials.ba2)", or a mod's.</summary>
    public string Shown => $"{(IsGame ? "the game's " : "")}{Path} ({Where})";

    /// <summary>True when the database holds the material at this Data-relative path ("materials\x\y.mat").</summary>
    public bool Has(string materialPath)
    {
        var (file, ext, dir) = ResourceId(materialPath);
        return ext == MatExt && Array.BinarySearch(_ids, Key(file, dir)) >= 0;
    }

    /// <summary>True when the database holds a material with this resource ID's file and folder hashes.</summary>
    public bool Has(uint file, uint dir) => Array.BinarySearch(_ids, Key(file, dir)) >= 0;

    /// <summary>The folder hash of each of its materials (a database holds no paths): for telling paths (GAPS S33a).</summary>
    internal IEnumerable<uint> Dirs() => _ids.Select(k => (uint)(k >> 32));

    const uint MatExt = 0x0074616D; // "mat\0"
    static ulong Key(uint file, uint dir) => ((ulong)dir << 32) | file;

    static readonly uint[] Crc = MakeCrc();
    static uint[] MakeCrc()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
            t[i] = c;
        }
        return t;
    }

    /// <summary>The resource ID the game knows a file by: CRC-32 of the name without extension, the extension's first
    /// 4 bytes, CRC-32 of the folder. Lower case (A to Z only), '/' read as '\'.</summary>
    public static (uint File, uint Ext, uint Dir) ResourceId(string path)
    {
        var b = Encoding.Latin1.GetBytes(path);
        int sep = -1, dot = -1;
        for (int i = 0; i < b.Length; i++)
        {
            if (b[i] is (byte)'/' or (byte)'\\') { sep = i; dot = -1; }
            else if (b[i] == (byte)'.') dot = i;
        }
        static byte Low(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c | 0x20) : c;
        uint dir = 0;
        for (int i = 0; i < sep; i++)
        {
            byte c = Low(b[i]);
            if (c == (byte)'/') c = (byte)'\\';
            dir = (dir >> 8) ^ Crc[(dir ^ c) & 0xFF];
        }
        int end = dot < 0 ? b.Length : dot;
        uint file = 0;
        for (int i = sep + 1; i < end; i++) file = (file >> 8) ^ Crc[(file ^ Low(b[i])) & 0xFF];
        uint ext = 0;
        if (dot >= 0)
            for (int i = 0; i < 4 && dot + 1 + i < b.Length; i++) ext |= (uint)Low(b[dot + 1 + i]) << (8 * i);
        return (file, ext, dir);
    }

    /// <summary>Reads a database's material list from a stream that starts at the database's first byte.</summary>
    /// <param name="size">The database's size, for the answer; the stream is read only as far as the list.</param>
    public static MaterialDatabase Read(Stream stream, long size, string path, string where, string container)
    {
        var db = new MaterialDatabase { Path = path, Where = where, Container = container, Size = size };
        var watch = Stopwatch.StartNew();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        try { db.ReadList(stream); }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException)
        {
            db.Error = e is EndOfStreamException ? "it ends before its material list does: the file is cut short" : e.Message.TrimEnd('.');
            db._ids = Array.Empty<ulong>();
        }
        db.Seconds = watch.Elapsed.TotalSeconds;
        db.Allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        return db;
    }

    MaterialContents? _contents;
    string? _contentsError;
    readonly object _contentsGate = new();
    /// <summary>The rest of the database (its objects and their textures), once <see cref="LoadContents"/> read it.</summary>
    public MaterialContents? Contents => _contents;
    /// <summary>Why the rest of the database couldn't be read. Null when it was, or wasn't tried.</summary>
    public string? ContentsError => _contentsError;

    long _lastUsedTicks;
    /// <summary>When a view last asked for the contents (<see cref="ArchiveStore.Contents"/>).</summary>
    internal DateTime LastUsedUtc => new(Interlocked.Read(ref _lastUsedTicks), DateTimeKind.Utc);
    internal void Touch() => Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);

    /// <summary>Forgets the contents read whole (S33e): the next view that needs them reads them again. A view that
    /// holds them now keeps its copy. The bytes they kept, or 0 when none were held.</summary>
    internal long UnloadContents()
    {
        lock (_contentsGate)
        {
            if (_contents is not { } c) return 0;
            _contents = null;
            return c.Kept;
        }
    }

    /// <summary>Reads the whole database once (<see cref="MaterialContents"/>); later calls find it in memory. True
    /// when it was read now. <paramref name="open"/> gives a stream at the database's first byte, and its size.</summary>
    public bool LoadContents(Func<(Stream Stream, long Size)> open)
    {
        lock (_contentsGate)
        {
            if (_contents != null || _contentsError != null) return false;
            try
            {
                var (s, size) = open();
                using (s) _contents = MaterialContents.Read(s, size);
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException or UnauthorizedAccessException)
            {
                _contentsError = e is EndOfStreamException ? "it ends before its last object does: the file is cut short" : e.Message.TrimEnd('.');
            }
            return true;
        }
    }

    /// <summary>A database that couldn't be opened at all.</summary>
    public static MaterialDatabase Failed(string path, string where, string container, string error) =>
        new() { Path = path, Where = where, Container = container, Error = error.TrimEnd('.') };

    sealed class ClassDef
    {
        public required string Name { get; init; }
        public List<(string Name, uint Type)> Fields { get; } = new();
    }

    const uint Builtin = 0xFFFFFF01;

    void ReadList(Stream s)
    {
        var head = new byte[8];
        var four = new byte[4];
        long pos = 0;
        void Exactly(Span<byte> into) { s.ReadExactly(into); pos += into.Length; }
        uint U32() { Exactly(four); return BinaryPrimitives.ReadUInt32LittleEndian(four); }
        (string Type, uint Size) Chunk()
        {
            Exactly(head);
            return (Encoding.ASCII.GetString(head, 0, 4), BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)));
        }
        byte[] skip = Array.Empty<byte>();
        void Skip(long n)
        {
            if (s.CanSeek) { s.Seek(n, SeekOrigin.Current); pos += n; return; }
            if (skip.Length == 0) skip = new byte[81920];
            while (n > 0) { int k = (int)Math.Min(n, skip.Length); Exactly(skip.AsSpan(0, k)); n -= k; }
        }

        var (type, size) = Chunk();
        if (type != "BETH" || size != 8) throw new InvalidDataException("it doesn't start with a BETH header: not a compiled material database");
        uint version = U32();
        if (version != 4) throw new InvalidDataException($"its format version is {version}; this reader knows version 4");
        uint chunks = U32();
        (type, size) = Chunk();
        if (type != "STRT") throw new InvalidDataException("it has no string table after its header");
        var table = new byte[size];
        Exactly(table);
        string? Str(uint offset)
        {
            if (offset >= Builtin) return null;
            if (offset >= table.Length) throw new InvalidDataException($"a name points outside the string table (offset {offset})");
            int end = Array.IndexOf(table, (byte)0, (int)offset);
            return Encoding.ASCII.GetString(table, (int)offset, (end < 0 ? table.Length : end) - (int)offset);
        }

        var classes = new Dictionary<string, ClassDef>(StringComparer.Ordinal);
        var byOffset = new Dictionary<uint, ClassDef>();
        const string ObjectInfo = "BSComponentDB2::DBFileIndex::ObjectInfo";
        for (uint i = 2; i < chunks; i++)
        {
            (type, size) = Chunk();
            switch (type)
            {
                case "TYPE":
                    Skip(size);
                    break;
                case "CLAS":
                {
                    var data = new byte[size];
                    Exactly(data);
                    if (size < 12) throw new InvalidDataException("a class definition is cut short");
                    uint nameAt = BinaryPrimitives.ReadUInt32LittleEndian(data);
                    var c = new ClassDef { Name = Str(nameAt) ?? "" };
                    int fields = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10));
                    if (12 + fields * 12 > size) throw new InvalidDataException($"the class definition of {c.Name} is cut short");
                    for (int f = 0; f < fields; f++)
                        c.Fields.Add((Str(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12 + f * 12))) ?? "",
                            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16 + f * 12))));
                    classes[c.Name] = c;
                    byOffset[nameAt] = c;
                    break;
                }
                case "OBJT" when size >= 6 && BuildVersion == null:
                {
                    var data = new byte[size];
                    Exactly(data);
                    // BSMaterial::Internal::CompiledDB starts with its build version, a string: a 16-bit length, then the text
                    if (Str(BinaryPrimitives.ReadUInt32LittleEndian(data)) == "BSMaterial::Internal::CompiledDB")
                    {
                        int len = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
                        if (6 + len <= size) BuildVersion = Encoding.ASCII.GetString(data, 6, len).TrimEnd('\0');
                    }
                    break;
                }
                case "LIST" when size >= 8:
                {
                    uint cls = U32();
                    if (Str(cls) != ObjectInfo) { Skip(size - 4); break; }
                    uint count = U32();
                    var (entry, idAt, hasDataAt) = Layout(classes, byOffset, ObjectInfo);
                    if ((long)count * entry != size - 8)
                        throw new InvalidDataException($"its object list holds {size - 8:N0} bytes for {count:N0} entries of {entry} bytes");
                    // The list is read a block of entries at a time: the game's is 16.5 MB
                    var block = new byte[entry * Math.Max(1, Math.Min((int)count, 65536 / entry))];
                    var ids = new List<ulong>();
                    int references = 0;
                    for (long done = 0; done < count; )
                    {
                        int n = (int)Math.Min(count - done, block.Length / entry);
                        Exactly(block.AsSpan(0, n * entry));
                        for (int k = 0; k < n; k++)
                        {
                            var e = block.AsSpan(k * entry, entry);
                            uint file = BinaryPrimitives.ReadUInt32LittleEndian(e[idAt..]);
                            uint ext = BinaryPrimitives.ReadUInt32LittleEndian(e[(idAt + 4)..]);
                            uint dir = BinaryPrimitives.ReadUInt32LittleEndian(e[(idAt + 8)..]);
                            if (ext != MatExt) continue;
                            if (e[hasDataAt] == 0) { references++; continue; }
                            ids.Add(Key(file, dir));
                        }
                        done += n;
                    }
                    if (count > 0 && ids.Count == 0 && references == 0)
                        throw new InvalidDataException($"none of the {count:N0} entries of its object list names a material: the layout isn't the one this reader knows");
                    var sorted = ids.ToArray();
                    Array.Sort(sorted);
                    _ids = sorted;
                    References = references;
                    BytesRead = pos;
                    return;
                }
                default:
                    Skip(size);
                    break;
            }
        }
        throw new InvalidDataException("it has no object list (BSComponentDB2::DBFileIndex::ObjectInfo)");
    }

    /// <summary>The size of an object list entry, and where its PersistentID and HasData sit, from the class definitions.</summary>
    static (int Entry, int IdAt, int HasDataAt) Layout(Dictionary<string, ClassDef> classes, Dictionary<uint, ClassDef> byOffset, string name)
    {
        if (!classes.TryGetValue(name, out var c)) throw new InvalidDataException($"it has no class definition for {name}");
        int at = 0, idAt = -1, hasDataAt = -1, idSize = 0;
        foreach (var (field, type) in c.Fields)
        {
            int n = SizeOf(type, 0);
            if (field == "PersistentID") { idAt = at; idSize = n; }
            if (field == "HasData") hasDataAt = at;
            at += n;
        }
        if (idAt < 0 || hasDataAt < 0) throw new InvalidDataException($"its {name} has no PersistentID or no HasData field");
        if (idSize != 12) throw new InvalidDataException($"its resource IDs are {idSize} bytes, not 12");
        return (at, idAt, hasDataAt);

        // A built-in number's size, or a class's: the sum of its fields
        int SizeOf(uint type, int depth)
        {
            if (type >= Builtin)
                return (type - Builtin) switch
                {
                    7 or 8 or 15 => 1,
                    9 or 10 => 2,
                    11 or 12 or 16 => 4,
                    13 or 14 or 17 => 8,
                    _ => throw new InvalidDataException($"a field of {name} has a type of no fixed size (built-in type {type - Builtin})"),
                };
            if (depth > 4) throw new InvalidDataException($"the types of {name} nest too deep");
            if (!byOffset.TryGetValue(type, out var t)) throw new InvalidDataException($"a field of {name} has a type with no class definition");
            return t.Fields.Sum(f => SizeOf(f.Type, depth + 1));
        }
    }
}
