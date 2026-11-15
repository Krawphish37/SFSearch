// An enum the game's code registers with its reflection, read from the executable (GAPS S33f). This is how the texture
// slot names of MaterialsView.Slots were found (notes\fix-s33-rest.md), in C# so it is part of SFSearch and the test can
// derive the table again from the Starfield.exe it finds.
//
// It reads one shape of code, not machine code in general: the function that registers an enum loads the enum's name
// (lea reg,[rip+name]), then, one entry after another, each value's name (lea reg,[rip+name]) and stores the value
// next to it (mov qword [reg+disp],imm32; a value can also come from a register, as 0 and 1 do in Starfield.exe). The
// Creation Kit's copy of the same function also loads display names ("Overlay Color", with a space) and objects that
// aren't text between the entries; both are passed over. The entries end where no further name is loaded within 256
// bytes, or at a name with "::" (the next class).
//   - The executable is read as a PE file: its sections, to turn an address into a place in the file.
//   - The enum's name is found as a C string in the file; every rip-relative lea in .text is checked for loading it.
//   - A value read must be the entry's place in the list (the enums read so far count from 0); a value not read is
//     taken as its place, and the answer says how many were read.
using System.Buffers.Binary;
using System.Text;

namespace SFSearch.Reports.Tests;

static class ExeEnums
{
    public sealed record Entry(string Name, int? Value);

    public sealed class Found
    {
        public required long CodeRva { get; init; }
        public required List<Entry> Entries { get; init; }
    }

    sealed record Section(string Name, uint Va, uint VSize, uint Raw, uint RawSize);

    static List<Section> Sections(byte[] b)
    {
        int pe = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0x3C));
        if (pe <= 0 || pe + 24 > b.Length || !b.AsSpan(pe, 4).SequenceEqual("PE\0\0"u8)) throw new InvalidDataException("not a PE file");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(pe + 6));
        int optional = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(pe + 20));
        var list = new List<Section>();
        for (int i = 0, o = pe + 24 + optional; i < count; i++, o += 40)
        {
            string name = Encoding.ASCII.GetString(b, o, 8).TrimEnd('\0');
            var s = b.AsSpan(o + 8);
            list.Add(new Section(name, BinaryPrimitives.ReadUInt32LittleEndian(s[4..]), BinaryPrimitives.ReadUInt32LittleEndian(s), BinaryPrimitives.ReadUInt32LittleEndian(s[12..]), BinaryPrimitives.ReadUInt32LittleEndian(s[8..])));
        }
        return list;
    }

    static long OffsetOf(List<Section> secs, long rva)
    {
        foreach (var s in secs) if (rva >= s.Va && rva < s.Va + s.RawSize) return rva - s.Va + s.Raw;
        return -1;
    }

    static long RvaOf(List<Section> secs, long offset)
    {
        foreach (var s in secs) if (offset >= s.Raw && offset < s.Raw + s.RawSize) return offset - s.Raw + s.Va;
        return -1;
    }

    /// <summary>The printable C string (up to 100 bytes) at an address, or null.</summary>
    static string? Text(byte[] b, List<Section> secs, long rva)
    {
        long o = OffsetOf(secs, rva);
        if (o < 0) return null;
        int end = b.AsSpan((int)o, (int)Math.Min(100, b.Length - o)).IndexOf((byte)0);
        if (end <= 0) return null;
        var s = b.AsSpan((int)o, end);
        foreach (byte c in s) if (c < 0x20 || c > 0x7E) return null;
        return Encoding.ASCII.GetString(s);
    }

    static bool Identifier(string s) => s.Length > 0 && (char.IsAsciiLetter(s[0]) || s[0] == '_') && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>A lea of a rip-relative address at <paramref name="i"/> in the code: REX.W (48 or 4C), 8D, a ModRM of
    /// mod 00 and r/m 101. The address it loads, or -1.</summary>
    static long Lea(ReadOnlySpan<byte> code, int i, long codeRva)
    {
        if (i + 7 > code.Length || code[i] is not (0x48 or 0x4C) || code[i + 1] != 0x8D || (code[i + 2] & 0xC7) != 5) return -1;
        return codeRva + i + 7 + BinaryPrimitives.ReadInt32LittleEndian(code[(i + 3)..]);
    }

    /// <summary>The enums of that name the executable registers in this shape: one for each place its code loads the
    /// name and then the entries' names.</summary>
    public static List<Found> Read(string exe, string enumName)
    {
        byte[] b = File.ReadAllBytes(exe);
        var secs = Sections(b);
        var text = secs.FirstOrDefault(s => s.Name == ".text") ?? throw new InvalidDataException("no .text section");
        // Where the name is, as a C string
        var names = new HashSet<long>();
        byte[] want = Encoding.ASCII.GetBytes("\0" + enumName + "\0");
        for (int from = 0; ;)
        {
            int at = b.AsSpan(from).IndexOf(want);
            if (at < 0) break;
            long rva = RvaOf(secs, from + at + 1);
            if (rva >= 0) names.Add(rva);
            from += at + 1;
        }
        var found = new List<Found>();
        if (names.Count == 0) return found;
        var code = b.AsSpan((int)text.Raw, (int)Math.Min(text.RawSize, b.Length - text.Raw));
        for (int i = 0; i + 7 <= code.Length; i++)
        {
            if (code[i] is not (0x48 or 0x4C) || code[i + 1] != 0x8D) continue;
            if (!names.Contains(Lea(code, i, text.Va))) continue;
            var entries = new List<Entry>();
            int last = i, j = i + 7;
            while (j + 7 <= code.Length && j - last <= 256)
            {
                long target = Lea(code, j, text.Va);
                if (target >= 0)
                {
                    string? s = Text(b, secs, target);
                    if (s != null && s.Contains("::", StringComparison.Ordinal)) break;
                    if (s != null && Identifier(s)) { entries.Add(new Entry(s, null)); last = j; }
                    j += 7;
                    continue;
                }
                // mov qword [reg+disp8],imm32 (48 C7 4x dd iiii) or [reg+disp32] (48 C7 8x dddd iiii), with a SIB byte
                // after the ModRM when the base is rsp; -1 is another field's
                if (code[j] == 0x48 && code[j + 1] == 0xC7 && entries.Count > 0 && entries[^1].Value == null && (code[j + 2] & 0x38) == 0)
                {
                    int mod = code[j + 2] >> 6, at = j + 3 + ((code[j + 2] & 7) == 4 ? 1 : 0) + (mod == 1 ? 1 : mod == 2 ? 4 : 99);
                    if (mod is 1 or 2 && at + 4 <= code.Length && BinaryPrimitives.ReadInt32LittleEndian(code[at..]) is var value && value != -1)
                        entries[^1] = entries[^1] with { Value = value };
                }
                j++;
            }
            if (entries.Count > 0) found.Add(new Found { CodeRva = text.Va + i, Entries = entries });
        }
        return found;
    }
}
