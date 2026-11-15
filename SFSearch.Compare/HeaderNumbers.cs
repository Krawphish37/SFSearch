// The record header's own numbers: version control, form version, the second version number. They say which tool
// saved a record, not what it holds, so the diff engine never counts them. An ITM still names them when they differ
// from its master's: xEdit may not show such a record as an ITM. Read from the 24 header bytes in the file (no
// Mutagen read): bytes 16-19 version control, 20-21 form version, 22-23 the second version number.
using System.Buffers.Binary;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Compare;

public readonly record struct HeaderNumbers(uint VersionControl, ushort FormVersion, ushort Version2)
{
    /// <summary>The one line an ITM answer gives once, when any of its ITMs has a header that differs from its master's.</summary>
    public const string ItmExplanation = "SFSearch compares a record's data (its subrecords and flags), not its header; a row names the header numbers that differ (this plugin's first, then the master's). xEdit may not show such a record as an ITM.";

    /// <summary>An ITM row's text: "identical to Starfield.esm", and when the header differs, "identical to Starfield.esm
    /// in data; header differs: form version 582 vs 581; version control 0 vs 0x002034C1".</summary>
    public static string ItmText(string master, IReadOnlyCollection<string> rewritten, string? header) =>
        $"identical to {master}" + (rewritten.Count > 0 ? $" in every value; the same value, written another way: {string.Join(", ", rewritten)}" : header != null ? " in data" : "")
        + (header != null ? $"; header differs: {header}" : "");

    /// <summary>The header numbers of a record, from the file's bytes.</summary>
    public static HeaderNumbers Read(RecordEngine engine, RecordRef r)
    {
        var p = engine.Index.Plugins[r.Plugin];
        Span<byte> b = stackalloc byte[RawRecord.HeaderLength];
        engine.Sessions.Get(p.Name).ReadAt(p.Segment![r.Entry].Offset, b);
        return new HeaderNumbers(BinaryPrimitives.ReadUInt32LittleEndian(b[16..]), BinaryPrimitives.ReadUInt16LittleEndian(b[20..]), BinaryPrimitives.ReadUInt16LittleEndian(b[22..]));
    }

    /// <summary>What differs, the plugin's number first: "form version 582 vs 581; version control 0 vs 0x002034C1".
    /// Null when the headers' numbers are the same.</summary>
    public static string? Differences(HeaderNumbers mine, HeaderNumbers master)
    {
        var parts = new List<string>(3);
        if (mine.FormVersion != master.FormVersion) parts.Add($"form version {mine.FormVersion} vs {master.FormVersion}");
        if (mine.VersionControl != master.VersionControl) parts.Add($"version control {Hex(mine.VersionControl)} vs {Hex(master.VersionControl)}");
        if (mine.Version2 != master.Version2) parts.Add($"second version number {mine.Version2} vs {master.Version2}");
        return parts.Count == 0 ? null : string.Join("; ", parts);

        static string Hex(uint v) => v == 0 ? "0" : $"0x{v:X8}";
    }

    /// <summary>The header difference of an override against its master's version, or null when the numbers are the same
    /// (or can't be read: the data comparison already read both records, so that is a plugin that changed meanwhile).</summary>
    public static string? Differences(RecordEngine mineEngine, RecordRef mine, RecordEngine masterEngine, RecordRef master)
    {
        try { return Differences(Read(mineEngine, mine), Read(masterEngine, master)); }
        catch (Exception e) when (e is IOException or RecordException) { return null; }
    }
}
