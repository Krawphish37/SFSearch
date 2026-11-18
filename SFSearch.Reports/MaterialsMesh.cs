// The materials a mesh (.nif) names, read the way the mesh writes them (GAPS S46). A mesh names a material two ways:
//  - As text: a string of the header ("materials\weapons\x\y.mat") that a shader of the mesh has for its name (a
//    block whose type ends in "ShaderProperty": its first number is its name, an index into the header's strings);
//    or a text inside a block, written as its length (32 bits) and its characters (a BSWeakReferenceNode names
//    "materials/water/watersmooth.mat" so). Only a whole length-prefixed text counts: the bytes before ".mat" are
//    never read as a name on their own.
//    A header string no shader has for its name is not a material of the mesh (GAPS S8): a tool that edits a mesh
//    keeps the strings of what was there before. In the game's own archives every one of the 288,944 ".mat" strings
//    of 108,816 meshes is the name of a BSLightingShaderProperty; in the mods' archives of the Data folder this was
//    written on, 21,941 of 69,249 are the name of no shader (a mesh of Nomad_Spacesuit_Z: 5 used, 27 left over, of
//    which the report called the ones that are nowhere MISSING). They are counted (Unused), not listed.
//  - By hash: a BSWeakReferenceNode (the object LOD meshes of a world, meshes\terrain\<world>\objects\*.nif) names
//    each material of each object it places by its resource ID, 12 bytes: the folder's CRC-32, the file name's
//    CRC-32, then "mat\0" (the order BSResource::ID's class lists them; a compiled material database stores the same
//    three as file, extension, folder). The 6,513 distinct IDs of sfta01.esm's 188 such blocks are all in a compiled
//    material database (fix-s46 note). The old reader took the printable bytes before "mat\0" for a path: a file
//    hash that ends in 0x2E (".") gave ".mat", "2i.mat", "pc.mat" and the like.
// The header: the version line; version, endianness, user version and block count; the Bethesda version, an author
// string, a number, an export string and one more short string; the block types; a type and a size for each block;
// the strings; the groups. The blocks follow, one after the other, each as long as its size says (Starfield writes
// the sizes).
using System.Buffers.Binary;
using System.Text;

namespace SFSearch.Reports;

/// <summary>A mesh's header: its strings, and where each block is.</summary>
internal sealed class MeshHeader
{
    public required List<string> Strings { get; init; }
    /// <summary>Each block's type name, offset in the file and size. Empty when the bytes end before the blocks start.</summary>
    public List<(string Type, int Offset, int Size)> Blocks { get; } = new();
    /// <summary>Where the blocks start; -1 when the bytes end before that.</summary>
    public int BlocksStart { get; init; } = -1;
    /// <summary>How many blocks the header says the mesh has.</summary>
    public int BlockCount { get; init; }

    /// <summary>The header of a mesh (Gamebryo 20.2.0.7, as Starfield writes it). Null when the bytes end before the
    /// strings do or aren't such a header.</summary>
    public static MeshHeader? Read(ReadOnlySpan<byte> b)
    {
        if (!b.StartsWith("Gamebryo File Format, Version 20.2.0.7\n"u8)) return null;
        int q = 39, length = b.Length;
        bool Has(long n) => n >= 0 && q + n <= length;
        if (!Has(17)) return null;
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(b[q..]); q += 4;
        q += 1;
        q += 4; // user version
        uint blocks = BinaryPrimitives.ReadUInt32LittleEndian(b[q..]); q += 4;
        uint bs = BinaryPrimitives.ReadUInt32LittleEndian(b[q..]); q += 4;
        if (version != 0x14020007 || blocks > 1_000_000) return null;
        bool Short(ReadOnlySpan<byte> s) { if (!Has(1)) return false; int n = s[q]; q++; if (!Has(n)) return false; q += n; return true; }
        if (!Short(b)) return null;
        if (bs > 130) { if (!Has(4)) return null; q += 4; } else if (!Short(b)) return null;
        if (!Short(b)) return null;
        if (bs >= 103 && !Short(b)) return null;
        if (!Has(2)) return null;
        int typeCount = BinaryPrimitives.ReadUInt16LittleEndian(b[q..]); q += 2;
        var types = new string[typeCount];
        for (int i = 0; i < typeCount; i++)
        {
            if (!Has(4)) return null;
            uint n = BinaryPrimitives.ReadUInt32LittleEndian(b[q..]); q += 4;
            if (n > 256 || !Has(n)) return null;
            types[i] = Encoding.ASCII.GetString(b.Slice(q, (int)n)); q += (int)n;
        }
        if (!Has((long)blocks * 6 + 8)) return null;
        int typeAt = q, sizeAt = q + (int)blocks * 2;
        q += (int)blocks * 6;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(b[q..]); q += 8;
        if (count > 200_000) return null;
        var strings = new List<string>((int)count);
        for (int i = 0; i < count; i++)
        {
            if (!Has(4)) return null;
            uint n = BinaryPrimitives.ReadUInt32LittleEndian(b[q..]); q += 4;
            if (n > 4096 || !Has(n)) return null;
            strings.Add(Encoding.Latin1.GetString(b.Slice(q, (int)n)));
            q += (int)n;
        }
        // The groups, then the blocks
        if (!Has(4)) return new MeshHeader { Strings = strings };
        uint groups = BinaryPrimitives.ReadUInt32LittleEndian(b[q..]); q += 4;
        if (groups > 1_000_000 || !Has((long)groups * 4)) return new MeshHeader { Strings = strings };
        q += (int)groups * 4;
        var header = new MeshHeader { Strings = strings, BlocksStart = q, BlockCount = (int)blocks };
        long at = q;
        for (int i = 0; i < blocks; i++)
        {
            int t = BinaryPrimitives.ReadUInt16LittleEndian(b[(typeAt + i * 2)..]) & 0x7FFF;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(b[(sizeAt + i * 4)..]);
            if (t >= typeCount || at + size > int.MaxValue) break;
            header.Blocks.Add((types[t], (int)at, (int)size));
            at += size;
        }
        return header;
    }
}

/// <summary>The materials one mesh names (see the file's head).</summary>
internal sealed class MeshMaterialNames
{
    /// <summary>The material texts, as the mesh writes them (not normalised), each once.</summary>
    public List<string> Texts { get; } = new();
    /// <summary>The materials named by resource ID: (folder hash, file hash), each once.</summary>
    public List<(uint Dir, uint File)> Hashed { get; } = new();
    /// <summary>The ".mat" strings of the header that no shader of the mesh has for its name, as written, each once:
    /// left over from editing, not materials of the mesh.</summary>
    public List<string> Unused { get; } = new();
    /// <summary>False when the header couldn't be read: the texts were then looked for in the whole file, and the
    /// materials named by hash weren't (the blocks can't be told apart).</summary>
    public bool HeaderRead { get; init; }

    const uint MatExt = 0x0074616D; // "mat\0"

    /// <summary>The materials a mesh names.</summary>
    public static MeshMaterialNames Read(ReadOnlySpan<byte> b)
    {
        var header = MeshHeader.Read(b);
        var names = new MeshMaterialNames { HeaderRead = header != null };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string text)
        {
            text = text.TrimEnd('\0').Trim();
            if (text.Length > 4 && !text.Contains("$(", StringComparison.Ordinal) && seen.Add(text)) names.Texts.Add(text);
        }
        if (header != null)
        {
            // The strings the mesh's shaders have for their names. When the blocks couldn't all be placed, every
            // ".mat" string counts, as before
            HashSet<int>? shaderNames = null;
            if (header.BlocksStart >= 0 && header.Blocks.Count == header.BlockCount)
            {
                shaderNames = new HashSet<int>();
                foreach (var (type, offset, size) in header.Blocks)
                    if (type.EndsWith("ShaderProperty", StringComparison.Ordinal) && size >= 4 && offset + 4 <= b.Length)
                        shaderNames.Add(BinaryPrimitives.ReadInt32LittleEndian(b[offset..]));
            }
            for (int i = 0; i < header.Strings.Count; i++)
            {
                string s = header.Strings[i];
                string trimmed = s.TrimEnd('\0').Trim();
                if (!trimmed.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) continue;
                if (shaderNames == null || shaderNames.Contains(i)) Add(s);
                else if (trimmed.Length > 4 && !names.Unused.Contains(trimmed)) names.Unused.Add(trimmed);
            }
        }
        // Length-prefixed texts in the blocks (in the whole file when the header couldn't be read)
        int from = header == null ? 0 : header.BlocksStart < 0 ? b.Length : header.BlocksStart;
        var spans = new List<(int Start, int End)>();
        foreach (var (start, end) in PrefixedTexts(b, from))
        {
            spans.Add((start, end));
            Add(Encoding.ASCII.GetString(b[start..end]));
        }
        if (header == null) return names;
        // Resource IDs in the BSWeakReferenceNode blocks
        var ids = new HashSet<(uint, uint)>();
        foreach (var (type, offset, size) in header.Blocks)
        {
            if (type != "BSWeakReferenceNode" || offset + size > b.Length) continue;
            var block = b.Slice(offset, size);
            int i = 0;
            while (i < block.Length)
            {
                int k = block[i..].IndexOf("mat\0"u8);
                if (k < 0) break;
                int p = i + k;
                i = p + 1;
                if (p < 8 || spans.Any(s => offset + p >= s.Start && offset + p < s.End)) continue;
                uint dir = BinaryPrimitives.ReadUInt32LittleEndian(block[(p - 8)..]);
                uint file = BinaryPrimitives.ReadUInt32LittleEndian(block[(p - 4)..]);
                if (ids.Add((dir, file))) names.Hashed.Add((dir, file));
            }
        }
        return names;
    }

    static bool TextByte(byte c) => c >= 0x20 && c < 0x7F && c is not ((byte)'"' or (byte)'*' or (byte)'<' or (byte)'>' or (byte)'?' or (byte)'|' or (byte)':');

    /// <summary>Each text from <paramref name="from"/> on that ends in ".mat" (any letter case) and is written whole
    /// after its length: a 32-bit length, then that many characters (a zero byte at the end may be counted in the
    /// length). Its start and end (the end after ".mat"). What follows the text doesn't matter: the length says where
    /// it ends (a next string's length may start with a letter: 77 is 'M').</summary>
    internal static List<(int Start, int End)> PrefixedTexts(ReadOnlySpan<byte> b, int from)
    {
        var found = new List<(int, int)>();
        while (from < b.Length)
        {
            int k = b[from..].IndexOf((byte)'.');
            if (k < 0) break;
            int dot = from + k;
            from = dot + 1;
            if (dot + 3 >= b.Length || (b[dot + 1] | 0x20) != 'm' || (b[dot + 2] | 0x20) != 'a' || (b[dot + 3] | 0x20) != 't') continue;
            int end = dot + 4;
            int earliest = dot;
            while (earliest > 0 && dot - earliest < 1024 && TextByte(b[earliest - 1])) earliest--;
            for (int start = earliest; start < dot; start++)
            {
                if (start < 4) continue;
                uint n = BinaryPrimitives.ReadUInt32LittleEndian(b[(start - 4)..]);
                if (n == end - start || (n == end + 1 - start && end < b.Length && b[end] == 0)) { found.Add((start, end)); break; }
            }
        }
        return found;
    }
}
