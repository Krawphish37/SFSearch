// The Forms view's FlagNames: a record header's flags (bytes 8-11) by Mutagen's names (GAPS S108, fix J86). Each record
// type has its own enum of header flags in Mutagen (Weapon.MajorFlag, Keyword.MajorFlag, AObjectModification.MajorFlag
// for every OMOD type), and Mutagen says which by the record's signature (StarfieldMajorFlagRegistry, fixes.20, C61:
// the trap types' APlacedTrap.MajorFlag included), so the names follow the Mutagen package SFSearch is built on. Since
// fixes.20 no enum gives one bit two names (C56 moved Container's NavMeshIgnoreErosion to 0x20000000; Mutagen's test
// holds it). Mutagen reads two bits on every record (IsCompressed, IsDeleted); those two are named from its
// StarfieldMajorRecordFlag. Any other bit is written as its number (0x40000000), as the Plugins view does.
using System.Collections.Concurrent;
using System.Globalization;
using Mutagen.Bethesda.Starfield;
using SFSearch.Records;

namespace SFSearch.Query;

/// <summary>A record header's flags by name, for one signature.</summary>
public sealed class RecordFlags
{
    readonly RecordSchema _schema;
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<uint, string>> _bySig = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<(string, uint), string> _text = new();

    RecordFlags(RecordSchema schema) => _schema = schema;

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RecordSchema, RecordFlags> Made = new();

    /// <summary>The names of a schema's record types (one for each schema, its lookups kept while it lives).</summary>
    public static RecordFlags For(RecordSchema schema) => Made.GetValue(schema, s => new RecordFlags(s));

    /// <summary>The bits every record has by Mutagen's name: the two it reads on every record (IsCompressed, IsDeleted).</summary>
    static readonly (uint Bit, string Name)[] Common =
    {
        ((uint)StarfieldMajorRecord.StarfieldMajorRecordFlag.Deleted, nameof(StarfieldMajorRecord.StarfieldMajorRecordFlag.Deleted)),
        ((uint)StarfieldMajorRecord.StarfieldMajorRecordFlag.Compressed, nameof(StarfieldMajorRecord.StarfieldMajorRecordFlag.Compressed)),
    };

    /// <summary>
    /// The flags as names: the bits with a name (the record type's MajorFlag enum, Compressed, Deleted), lowest first,
    /// then every bit with no name as its number. "" for no flags; null for no signature.
    /// </summary>
    public string? Names(string? signature, uint flags)
    {
        if (signature == null) return null;
        if (flags == 0) return "";
        return _text.GetOrAdd((signature, flags), k =>
        {
            var names = Of(k.Item1);
            var named = new List<string>();
            var numbers = new List<string>();
            for (int b = 0; b < 32; b++)
            {
                uint bit = 1u << b;
                if ((k.Item2 & bit) == 0) continue;
                if (names.TryGetValue(bit, out var n)) named.Add(n);
                else numbers.Add("0x" + bit.ToString("X", CultureInfo.InvariantCulture));
            }
            return string.Join(", ", named.Concat(numbers));
        });
    }

    /// <summary>The names of one signature's bits: its MajorFlag enum as Mutagen's registry gives it, then Compressed and
    /// Deleted for bits it doesn't name.</summary>
    public IReadOnlyDictionary<uint, string> Of(string signature) => _bySig.GetOrAdd(signature, sig =>
    {
        var names = new Dictionary<uint, string>();
        if (MajorFlagEnum(sig) is { } e)
            foreach (var value in Enum.GetValues(e))
            {
                uint bit = unchecked((uint)Convert.ToInt64(value, CultureInfo.InvariantCulture));
                if (bit == 0 || (bit & (bit - 1)) != 0) continue;
                names.TryAdd(bit, Enum.GetName(e, value)!);
            }
        foreach (var (bit, name) in Common) names.TryAdd(bit, name);
        return names;
    });

    /// <summary>The header-flag enum of a record signature (Mutagen's StarfieldMajorFlagRegistry, C61: WEAP's
    /// Weapon.MajorFlag, every OMOD's AObjectModification.MajorFlag). Null when the type names none of its bits.</summary>
    public static Type? MajorFlagEnum(string signature)
        => signature.Length == 4 && StarfieldMajorFlagRegistry.TryGetEnumType(new Mutagen.Bethesda.Plugins.RecordType(signature), out var e) ? e : null;
}
