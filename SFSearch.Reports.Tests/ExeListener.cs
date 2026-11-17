// Which compiled material databases the game reads, read from the game's code (GAPS S8, j811), so the test holds
// MaterialDatabase.GameReads against the Starfield.exe (and CreationKit.exe) it finds.
//
// The game keeps a file listener for materials and particles: a class of its own, MaterialAndParticleFileListener in
// Starfield.exe (MaterialFileListener in the Creation Kit), found by its RTTI name. Read with Visual Studio's dumpbin
// (notes\fix-j8-11-materials.md):
//   - Its constructor (the code that stores the class's vtable) takes the resource ID of ".cdb" (and of ".blob", the
//     particle files) and keeps each ID's extension in the listener.
//   - Its method 1, called for an archive as it is opened, matches the archive's name against the pattern
//     ".* - Main[0-9]*\.ba2" (std::regex, case ignored: flag 0x100), returns at once when the name doesn't match, and
//     otherwise collects the resource ID of every file of the archive whose extension is the .cdb one (or the .blob
//     one), at any folder.
// So a database is read when it is in an archive named "<plugin> - Main.ba2", "- Main01.ba2" and so on; the game's
// own is loaded by its fixed path (Materials/MaterialsBeta.cdb). Nothing else in the code names ".cdb".
//
// This file reads the same shape of code, not machine code in general: the class's type descriptor by its name, the
// complete object locator that points at it, the vtable after the pointer to that locator; then rip-relative lea
// instructions (REX.W 8D, ModRM mod 00 r/m 101) in the method and in the constructor, up to the int3 padding that ends
// a function.
using System.Buffers.Binary;
using System.Text;

namespace SFSearch.Reports.Tests;

static class ExeListener
{
    public sealed class Found
    {
        public required string ClassName { get; init; }
        public long VtableRva { get; init; }
        public long MethodRva { get; init; }
        /// <summary>The C strings method 1 loads.</summary>
        public List<string> MethodStrings { get; } = new();
        /// <summary>The functions that store the vtable (the constructors), each with the C strings it loads.</summary>
        public List<(long Rva, List<string> Strings)> Constructors { get; } = new();
    }

    sealed record Section(string Name, uint Va, uint RawSize, uint Raw);

    public static Found? Read(string exe, string classPrefix)
    {
        byte[] b = File.ReadAllBytes(exe);
        int pe = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0x3C));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(pe + 6));
        int optional = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(pe + 20));
        ulong imageBase = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(pe + 24 + 24));
        var secs = new List<Section>();
        for (int i = 0, o = pe + 24 + optional; i < count; i++, o += 40)
            secs.Add(new Section(Encoding.ASCII.GetString(b, o, 8).TrimEnd('\0'), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o + 12)), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o + 16)), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o + 20))));
        long Off(long rva) { foreach (var s in secs) if (rva >= s.Va && rva < s.Va + s.RawSize) return rva - s.Va + s.Raw; return -1; }
        long Rva(long off) { foreach (var s in secs) if (off >= s.Raw && off < s.Raw + s.RawSize) return off - s.Raw + s.Va; return -1; }
        string? CStr(long rva)
        {
            long o = Off(rva);
            if (o < 0) return null;
            int end = b.AsSpan((int)o, (int)Math.Min(200, b.Length - o)).IndexOf((byte)0);
            if (end <= 0) return null;
            foreach (byte c in b.AsSpan((int)o, end)) if (c < 0x20 || c > 0x7E) return null;
            return Encoding.ASCII.GetString(b, (int)o, end);
        }
        var text = secs.First(s => s.Name == ".text");
        var rdata = secs.First(s => s.Name == ".rdata");

        // The type descriptor: its name follows 16 bytes of pointers
        int nameAt = b.AsSpan().IndexOf(Encoding.ASCII.GetBytes(".?AV" + classPrefix + "@"));
        if (nameAt < 0) return null;
        int nameEnd = Array.IndexOf(b, (byte)0, nameAt);
        string className = Encoding.ASCII.GetString(b, nameAt, nameEnd - nameAt);
        uint td = (uint)Rva(nameAt - 16);
        // The complete object locators: signature 1, two offsets, then the type descriptor's RVA. The class has one per
        // base with a vtable (the listener is a base at offset 16 in Starfield.exe); each vtable starts right after a
        // pointer to its locator. The listener's is the one whose method 1 is code.
        var rd = b.AsSpan((int)rdata.Raw, (int)rdata.RawSize);
        var cols = new HashSet<ulong>();
        for (int i = 12; i + 4 <= rd.Length; i += 4)
            if (BinaryPrimitives.ReadUInt32LittleEndian(rd[i..]) == td && BinaryPrimitives.ReadUInt32LittleEndian(rd[(i - 12)..]) == 1) cols.Add(imageBase + (ulong)(rdata.Va + i - 12));
        Found? found = null;
        for (int i = 0; i + 24 <= rd.Length && found == null; i += 8)
        {
            if (!cols.Contains(BinaryPrimitives.ReadUInt64LittleEndian(rd[i..]))) continue;
            long method = (long)(BinaryPrimitives.ReadUInt64LittleEndian(rd[(i + 16)..]) - imageBase);
            if (method >= text.Va && method < text.Va + text.RawSize) found = new Found { ClassName = className, VtableRva = rdata.Va + i + 8, MethodRva = method };
        }
        if (found == null) return null;

        byte[] code = b.AsSpan((int)text.Raw, (int)text.RawSize).ToArray();
        long Lea(ReadOnlySpan<byte> c, int i) => i + 7 <= c.Length && c[i] is 0x48 or 0x4C && c[i + 1] == 0x8D && (c[i + 2] & 0xC7) == 5 ? text.Va + i + 7 + BinaryPrimitives.ReadInt32LittleEndian(c[(i + 3)..]) : -1;
        int End(int from) { int i = from; while (i + 1 < code.Length && !(code[i] == 0xCC && code[i + 1] == 0xCC)) i++; return i; }
        int Start(int at) { int i = at; while (i > 1 && !(code[i - 1] == 0xCC && code[i - 2] == 0xCC)) i--; return i; }
        List<string> Strings(int from, int to)
        {
            var list = new List<string>();
            for (int i = from; i < to; i++) if (Lea(code, i) is var t && t >= 0 && CStr(t) is { } s) list.Add(s);
            return list;
        }
        int m = (int)(found.MethodRva - text.Va);
        found.MethodStrings.AddRange(Strings(m, End(m)));
        // The constructors: the code that loads the vtable's address
        for (int i = 0; i + 7 <= code.Length; i++)
        {
            if (code[i] is not (0x48 or 0x4C) || code[i + 1] != 0x8D || Lea(code, i) != found.VtableRva) continue;
            int s = Start(i);
            found.Constructors.Add((text.Va + s, Strings(s, End(i))));
        }
        return found;
    }
}
