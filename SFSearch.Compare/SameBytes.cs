// Heavy records compared by their bytes (GAPS S47). A record of over 16 MB isn't read through Mutagen without
// allow_heavy (it takes gigabytes as JSON). An override of one that is the same bytes as the version before it is an
// ITM all the same: SFBGS006.esm's override of the 51 MB navmesh info map (000FF1:Starfield.esm) is byte for byte
// Starfield.esm's. So before such a record is read, its bytes are held against the version before it: the subrecords
// (decompressed) and the record flags (the compressed bit aside). Equal bytes are the same record only when every
// FormID in them names the same plugin in both files: the override's plugin must list the base's masters first, in
// the same order and sizes, and then the base's plugin itself. Otherwise the bytes tell nothing and the record is read
// (or not) as before.
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Compare;

public sealed partial class CompareSession
{
    /// <summary>True when a record is heavy: the record engine reads it through Mutagen only with allow_heavy.</summary>
    public static bool IsHeavy(RecordEngine engine, RecordRef r) => engine.HeavyNote(r) != null;

    /// <summary>
    /// Whether an override holds the same record as the version before it, told from the bytes alone: true when the
    /// decompressed subrecords and the record flags (the compressed bit aside) are equal and the two plugins number
    /// FormIDs the same way; false when the bytes differ; null when the plugins number FormIDs differently, so equal
    /// bytes wouldn't prove anything.
    /// </summary>
    public static bool? SameBytes(RecordEngine mineEngine, RecordRef mine, RecordEngine baseEngine, RecordRef @base)
    {
        var pm = mineEngine.Index.Plugins[mine.Plugin]; var pb = baseEngine.Index.Plugins[@base.Plugin];
        if (!SameFormIdSpace(pm, pb)) return null;
        RawRecord a, b;
        try { a = mineEngine.Raw(mine); b = baseEngine.Raw(@base); }
        catch (RecordException e) { throw new CompareException(e.Message); }
        if (a.Signature != b.Signature || (a.Flags & ~RawRecord.CompressedFlag) != (b.Flags & ~RawRecord.CompressedFlag)) return false;
        return a.Data.AsSpan().SequenceEqual(b.Data);
    }

    /// <summary>The override's plugin numbers every FormID the base's plugin can write the way the base's plugin does:
    /// it lists the base's masters first (same names, same sizes), then the base's plugin, at the base's size.</summary>
    static bool SameFormIdSpace(IndexedPlugin mine, IndexedPlugin @base)
    {
        if (mine.Header == null || @base.Header == null) return false;
        var mm = mine.Header.Masters; var bm = @base.Header.Masters;
        if (mm.Count <= bm.Count) return false;
        for (int i = 0; i < bm.Count; i++)
            if (!mm[i].Equals(bm[i], StringComparison.OrdinalIgnoreCase) || i >= mine.MasterStyles.Count || i >= @base.MasterStyles.Count || mine.MasterStyles[i] != @base.MasterStyles[i]) return false;
        return mm[bm.Count].Equals(@base.Name, StringComparison.OrdinalIgnoreCase) && bm.Count < mine.MasterStyles.Count && mine.MasterStyles[bm.Count] == @base.Header.Style;
    }

    /// <summary>"Its bytes differ from Starfield.esm's (12.3 KB here, 51.4 MB there)": for a heavy record that isn't read.</summary>
    public static string BytesDiffer(RecordEngine mineEngine, RecordRef mine, RecordEngine baseEngine, RecordRef @base, string baseName) =>
        $"Its bytes differ from {baseName}'s ({Sizes.Text(mineEngine.DataBytes(mine))} here, {Sizes.Text(baseEngine.DataBytes(@base))} there)";

    /// <summary>What an ITM row says when it was told from the bytes: "byte for byte (a heavy record of 51 MB, compared by its bytes, not read through Mutagen)".</summary>
    public static string BytesNote(RecordEngine engine, RecordRef r) =>
        $"byte for byte (a heavy record of {Sizes.Text(engine.DataBytes(r))}, compared by its bytes, not read through Mutagen)";
}
