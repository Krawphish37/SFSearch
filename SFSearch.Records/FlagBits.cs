// The flags a flags enum's value holds, by SFSearch's own bounded loop: one pass over the bits of the enum's underlying
// type, lowest first, a bit no member names only when asked for. Noggog's EnumerateContainedFlags (Noggog.CSharpExt
// 4.3.0) goes wrong on an int-based enum and bit 31: with no member there (Mutagen fixes.19) a value with bit 31 set gives
// the bit's number and a spurious 0, even when undefined bits aren't asked for (GAPS S162: the 13 navmeshes of
// Starfield.esm with NavmeshGenCell); with a member there (fixes.20's NavigationMesh.MajorFlag.NavmeshGenCell and
// BiomeMarker.MajorFlag.MustBeUnique) its flag loop never ends. SFSearch never calls it: whatever an enum's base type
// and members, this ends after at most 64 steps.
using System.Globalization;

namespace SFSearch.Records;

public static class FlagBits
{
    /// <summary>The value's bits as an unsigned number of its underlying type's width, whatever that type (an int's bit 31
    /// is 0x80000000, as a uint's; fixes.21 makes NavigationMesh's and BiomeMarker's MajorFlag uint-based).</summary>
    public static ulong Raw(Enum e)
    {
        var u = Enum.GetUnderlyingType(e.GetType());
        return Type.GetTypeCode(u) switch
        {
            TypeCode.SByte => unchecked((byte)Convert.ToSByte(e, CultureInfo.InvariantCulture)),
            TypeCode.Int16 => unchecked((ushort)Convert.ToInt16(e, CultureInfo.InvariantCulture)),
            TypeCode.Int32 => unchecked((uint)Convert.ToInt32(e, CultureInfo.InvariantCulture)),
            TypeCode.Int64 => unchecked((ulong)Convert.ToInt64(e, CultureInfo.InvariantCulture)),
            _ => Convert.ToUInt64(e, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Each bit set in the value, as the enum, lowest first; a bit no member names only with
    /// <paramref name="includeUndefined"/>.</summary>
    public static IEnumerable<Enum> Contained(Enum e, bool includeUndefined = true)
    {
        var t = e.GetType();
        var u = Enum.GetUnderlyingType(t);
        int width = System.Runtime.InteropServices.Marshal.SizeOf(u) * 8;
        ulong raw = Raw(e);
        for (int b = 0; b < width; b++)
        {
            if (((raw >> b) & 1) == 0) continue;
            var bit = (Enum)Enum.ToObject(t, Underlying(1UL << b, u));
            if (includeUndefined || Enum.IsDefined(t, bit)) yield return bit;
        }
    }

    /// <summary>A member's name as the serializer's JSON writes it (Noggog's ToStringFast, which is .NET's ToString for a
    /// member: of two members with one value, the one .NET names, as fixes.19's Container NavMeshIgnoreErosion), or for a
    /// value no member names its bits in hex, "0x80000000".</summary>
    public static string Name(Enum e) => Enum.IsDefined(e.GetType(), e) ? e.ToString() : "0x" + Raw(e).ToString("X", CultureInfo.InvariantCulture);

    /// <summary>The names of the bits a flags value holds (<see cref="Contained"/>, <see cref="Name"/>).</summary>
    public static IEnumerable<string> Names(Enum e, bool includeUndefined = true) => Contained(e, includeUndefined).Select(Name);

    static object Underlying(ulong bit, Type u) => Type.GetTypeCode(u) switch
    {
        TypeCode.SByte => unchecked((sbyte)bit),
        TypeCode.Byte => (byte)bit,
        TypeCode.Int16 => unchecked((short)bit),
        TypeCode.UInt16 => (ushort)bit,
        TypeCode.Int32 => unchecked((int)bit),
        TypeCode.UInt32 => (uint)bit,
        TypeCode.Int64 => unchecked((long)bit),
        _ => bit,
    };
}
