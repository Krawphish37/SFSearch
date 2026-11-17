using System.Buffers.Binary;
using System.Text;

namespace SFSearch.Index;

/// <summary>How many FormIDs a plugin takes at runtime. Starfield numbers the three sizes separately.</summary>
public enum MasterStyle : byte
{
    /// <summary>One whole top byte: 0A xxxxxx.</summary>
    Full = 0,
    /// <summary>FD xx yyyy (header flag 0x400).</summary>
    Medium = 1,
    /// <summary>FE xxx yyy (header flag 0x100). Today's SFSearch calls this "Light".</summary>
    Small = 2,
    /// <summary>The master's file isn't there, so its size isn't known.</summary>
    Missing = 255,
}

/// <summary>
/// What the TES4 record at the start of a plugin says: its flags and its masters, in order, and the rest of the
/// header as the file stores it (HEDR, the author, the overridden forms' count, ...), read from the header's bytes alone.
/// </summary>
public sealed class PluginHeader
{
    public const int RecordHeaderLength = 24;
    public const uint SmallFlag = 0x100, MediumFlag = 0x400, MasterFlag = 0x1, LocalizedFlag = 0x80;
    internal const uint Tes4 = 0x34534554, Mast = 0x5453414D, Xxxx = 0x58585858;
    const uint Hedr = 0x52444548, Cnam = 0x4D414E43, Snam = 0x4D414E53, Onam = 0x4D414E4F, Incc = 0x43434E49;

    public uint Flags { get; private init; }
    public IReadOnlyList<string> Masters { get; private init; } = Array.Empty<string>();
    /// <summary>The record header's form version (bytes 20-21; 581 in Starfield.esm 1.16).</summary>
    public ushort FormVersion { get; private init; }
    /// <summary>The bytes after the 24-byte record header (the record header's data size).</summary>
    public uint DataSize { get; private init; }
    /// <summary>HEDR's version (a float, offset 0); null when the header has no HEDR.</summary>
    public float? Version { get; private init; }
    /// <summary>HEDR's number of records (offset 4), as the plugin's writer counted them.</summary>
    public uint? NumRecords { get; private init; }
    /// <summary>HEDR's next object ID (offset 8): the local ID the writer would give the next new record.</summary>
    public uint? NextObjectId { get; private init; }
    /// <summary>CNAM: the author; null when absent.</summary>
    public string? Author { get; private init; }
    /// <summary>SNAM: the description; null when absent.</summary>
    public string? Description { get; private init; }
    /// <summary>ONAM's FormIDs (4 bytes each); 0 when there is no ONAM.</summary>
    public int OverriddenForms { get; private init; }
    /// <summary>INCC: the interior cell count; null when absent.</summary>
    public uint? InteriorCellCount { get; private init; }
    /// <summary>The header's subrecord signatures in the file's order, each once.</summary>
    public IReadOnlyList<string> Subrecords { get; private init; } = Array.Empty<string>();
    /// <summary>Where the first top-level group starts.</summary>
    public long RecordsStart { get; private init; }
    /// <summary>Both size flags are set. Mutagen reads such a file as Small (C180, fixes.14: the Small flag first, as
    /// xEdit does; it refuses only to write one), and so does <see cref="StyleOf"/>; the index says so.</summary>
    public bool BothSizeFlags => (Flags & SmallFlag) != 0 && (Flags & MediumFlag) != 0;
    public MasterStyle Style => StyleOf(Flags);
    /// <summary>The plugin keeps its texts in string files (.strings, .dlstrings, .ilstrings), not in itself.</summary>
    public bool Localized => (Flags & LocalizedFlag) != 0;

    /// <summary>The size a plugin's own header gives it, by Mutagen's rule (MasterStyleConstruction, IModExt.GetMasterStyle):
    /// the Small flag first, then Medium, else Full; both flags read as Small. The file name plays no part.</summary>
    public static MasterStyle StyleOf(uint flags) =>
        (flags & SmallFlag) != 0 ? MasterStyle.Small : (flags & MediumFlag) != 0 ? MasterStyle.Medium : MasterStyle.Full;

    /// <summary>The number of bytes of the file that hold the header record, or 0 when the start isn't a TES4 record.</summary>
    public static long LengthNeeded(ReadOnlySpan<byte> start) =>
        start.Length < RecordHeaderLength || BinaryPrimitives.ReadUInt32LittleEndian(start) != Tes4
            ? 0 : RecordHeaderLength + (long)BinaryPrimitives.ReadUInt32LittleEndian(start[4..]);

    /// <summary>Reads the header from the first bytes of a file. Null when it isn't a plugin or the bytes stop short.</summary>
    public static PluginHeader? Parse(ReadOnlySpan<byte> start)
    {
        long need = LengthNeeded(start);
        if (need == 0 || need > start.Length) return null;
        var masters = new List<string>();
        var subs = new List<string>();
        var body = start[RecordHeaderLength..(int)need];
        int pos = 0; uint longForm = 0;
        float? version = null; uint? numRecords = null, nextId = null, incc = null;
        string? author = null, description = null;
        int onam = 0;
        while (pos + 6 <= body.Length)
        {
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]);
            long len = BinaryPrimitives.ReadUInt16LittleEndian(body[(pos + 4)..]);
            if (sig == Xxxx && len == 4 && pos + 10 <= body.Length) { longForm = BinaryPrimitives.ReadUInt32LittleEndian(body[(pos + 6)..]); pos += 10; continue; }
            if (longForm != 0) { len = longForm; longForm = 0; }
            if (pos + 6 + len > body.Length) return null;
            var data = body.Slice(pos + 6, (int)len);
            string name4 = Encoding.ASCII.GetString(body.Slice(pos, 4));
            if (!subs.Contains(name4)) subs.Add(name4);
            if (sig == Mast && len > 1) masters.Add(Text(data, Encoding.UTF8));
            else if (sig == Hedr && len >= 12)
            {
                version = BinaryPrimitives.ReadSingleLittleEndian(data);
                numRecords = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
                nextId = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
            }
            else if (sig == Cnam) author = Text(data, Windows1252);
            else if (sig == Snam) description = Text(data, Windows1252);
            else if (sig == Onam) onam += (int)(len / 4);
            else if (sig == Incc && len >= 4) incc = BinaryPrimitives.ReadUInt32LittleEndian(data);
            pos += 6 + (int)len;
        }
        return new PluginHeader
        {
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(start[8..]), Masters = masters, RecordsStart = need,
            DataSize = BinaryPrimitives.ReadUInt32LittleEndian(start[4..]), FormVersion = BinaryPrimitives.ReadUInt16LittleEndian(start[20..]),
            Version = version, NumRecords = numRecords, NextObjectId = nextId, Author = author, Description = description,
            OverriddenForms = onam, InteriorCellCount = incc, Subrecords = subs,
        };
    }

    /// <summary>A zero-ended text, as the header stores its master names, author and description.</summary>
    static string Text(ReadOnlySpan<byte> data, Encoding encoding)
    {
        int zero = data.IndexOf((byte)0);
        return encoding.GetString(zero >= 0 ? data[..zero] : data);
    }

    /// <summary>
    /// The author and the description are read as Windows-1252, as Mutagen reads Starfield's untranslated texts
    /// (GameConstants.Starfield: NonTranslated and NonLocalized are MutagenEncoding._1252) and xEdit too ("general string
    /// encoding: 1252"): darkstar.esm's German description has "Komplett" then byte FC, which is the letter u with umlaut.
    /// </summary>
    static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    /// <summary>Reads a plugin's header from its file, opened for reading with every kind of sharing. Null when it isn't a plugin.</summary>
    public static PluginHeader? Read(string path)
    {
        using var fs = Files.OpenShared(path);
        var buf = new byte[(int)Math.Min(fs.Length, 65536)];
        fs.ReadExactly(buf);
        long need = LengthNeeded(buf);
        if (need > buf.Length && need <= fs.Length)
        {
            Array.Resize(ref buf, (int)need);
            fs.Position = 0; fs.ReadExactly(buf);
        }
        return Parse(buf);
    }
}

internal static class Files
{
    /// <summary>Read only, sharing read, write and delete: the index never stands in the way of the game, xEdit or a mod manager.</summary>
    public static FileStream OpenShared(string path, FileOptions options = FileOptions.None) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, options);

    public static bool IsPluginName(string name) =>
        name.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".esl", StringComparison.OrdinalIgnoreCase);
}
