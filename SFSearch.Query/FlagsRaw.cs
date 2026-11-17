// A flags field as its number (GAPS S168, J120): beside each flags column of a typed or list view (a JSON list of the
// flags' names), a column of the same flags as one integer, <Field>Raw, so that a bit is tested by its number:
// WHERE (EntryTypesRaw & 0x80000000) <> 0. The names alone let a bit be tested only by its name (or an unnamed bit's hex
// text), and a census of one bit across types needed each type's name for it.
//
// The number is the bits of Mutagen's value as an unsigned number of the enum's own width (FlagBits.Raw: an int enum's
// bit 31 is 2147483648, as a uint's); a 64-bit flags value with bit 63 is SQLite's negative integer of the same bits.
// Each route gives the same number:
//   - from the record's JSON: the flags' names mapped back to their bits by the enum (sfs_flagbits, this file); an
//     unnamed bit is in the list as its hex text ("0x80000000"), as the serializer writes it;
//   - straight from Mutagen's object (DirectColumns.cs) and from the record's own bytes (RawColumns.cs, RawLists.cs,
//     RawWalkLists.cs): the enum's value, by FlagBits.Raw.
// The default (a value the serializer leaves out) is the number of the flags column's default list.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using SFSearch.Records;

namespace SFSearch.Query;

static class FlagsRaw
{
    /// <summary>The kind of a flags field's number column.</summary>
    public const string Kind = "FlagsRaw";
    /// <summary>The number column's name: the flags column's name and this.</summary>
    public const string Suffix = "Raw";

    /// <summary>The kinds a column reading an enum from the record has: an enum's name, a flags list, a flags number.</summary>
    public static bool IsEnumKind(string kind) => kind is "Enum" or "Flags" or Kind;

    /// <summary>A flags value as the number column gives it.</summary>
    public static long Number(Enum e) => unchecked((long)FlagBits.Raw(e));

    static readonly Lazy<Dictionary<string, Type>> Enums = new(MakeEnums);

    /// <summary>
    /// Every enum of Mutagen's assemblies by the schema's name for it (SchemaGen's Reflect.EnumName: the full name with
    /// '.' for '+', without "Mutagen.Bethesda.Starfield."), so that a view's column and the SQL function find the same
    /// type.
    /// </summary>
    static Dictionary<string, Type> MakeEnums()
    {
        const string ns = "Mutagen.Bethesda.Starfield.";
        var map = new Dictionary<string, Type>(StringComparer.Ordinal);
        var assemblies = new[] { typeof(Mutagen.Bethesda.Starfield.StarfieldMod).Assembly, typeof(Mutagen.Bethesda.Plugins.FormKey).Assembly };
        foreach (var asm in assemblies.Distinct())
            foreach (var t in asm.GetTypes())
            {
                if (!t.IsEnum || t.FullName is not { } full) continue;
                string n = full.Replace('+', '.');
                if (n.StartsWith(ns, StringComparison.Ordinal)) n = n[ns.Length..];
                map.TryAdd(n, t);
            }
        return map;
    }

    /// <summary>The CLR flags enum the schema names, or null when there is none of that name or it isn't a flags enum.</summary>
    public static Type? EnumOf(string? schemaName) =>
        schemaName != null && Enums.Value.TryGetValue(schemaName, out var t) && t.IsDefined(typeof(FlagsAttribute), false) ? t : null;

    static readonly ConcurrentDictionary<Type, Dictionary<string, ulong>> Members = new();

    /// <summary>Each member's name with its bits (two names of one value both).</summary>
    static Dictionary<string, ulong> BitsOf(Type t) => Members.GetOrAdd(t, x =>
    {
        var d = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (string name in Enum.GetNames(x)) d[name] = FlagBits.Raw((Enum)Enum.Parse(x, name));
        return d;
    });

    /// <summary>
    /// The number of a flags value given as its JSON list of names (the flags column's value), for the enum the schema
    /// names: each name's bits, an unnamed bit's hex text ("0x80000000") as its number. NULL for NULL. A name the enum
    /// hasn't is an error: the JSON and the enum come from the same Mutagen, so it would be a bug, never a value.
    /// </summary>
    public static long? FromNames(string? json, string? enumName)
    {
        if (json == null) return null;
        var t = EnumOf(enumName) ?? throw new InvalidOperationException($"sfs_flagbits: {enumName} isn't a flags enum of Mutagen's");
        var bits = BitsOf(t);
        ulong n = 0;
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
        if (!reader.Read()) return null;
        if (reader.TokenType == JsonTokenType.String) return unchecked((long)One(reader.GetString()!));
        if (reader.TokenType != JsonTokenType.StartArray) throw new InvalidOperationException($"sfs_flagbits: {Short(json)} isn't a list of {t.Name}'s flags");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String) throw new InvalidOperationException($"sfs_flagbits: {Short(json)} isn't a list of {t.Name}'s flags");
            n |= One(reader.GetString()!);
        }
        return unchecked((long)n);

        ulong One(string name)
        {
            if (bits.TryGetValue(name, out ulong b)) return b;
            if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(name.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out b)) return b;
            throw new InvalidOperationException($"sfs_flagbits: {t.Name} has no flag {name}");
        }
    }

    static string Short(string s) => s.Length <= 80 ? s : s[..80] + "…";
}
