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

/// <summary>What the TES4 record at the start of a plugin says: its flags and its masters, in order.</summary>
public sealed class PluginHeader
{
    public const int RecordHeaderLength = 24;
    public const uint SmallFlag = 0x100, MediumFlag = 0x400, MasterFlag = 0x1, LocalizedFlag = 0x80;
    internal const uint Tes4 = 0x34534554, Mast = 0x5453414D, Xxxx = 0x58585858;

    public uint Flags { get; private init; }
    public IReadOnlyList<string> Masters { get; private init; } = Array.Empty<string>();
    /// <summary>Where the first top-level group starts.</summary>
    public long RecordsStart { get; private init; }
    /// <summary>Both size flags are set. Mutagen refuses such a file; the index reads it as Small and says so.</summary>
    public bool BothSizeFlags => (Flags & SmallFlag) != 0 && (Flags & MediumFlag) != 0;
    public MasterStyle Style => StyleOf(Flags);
    /// <summary>The plugin keeps its texts in string files (.strings, .dlstrings, .ilstrings), not in itself.</summary>
    public bool Localized => (Flags & LocalizedFlag) != 0;

    /// <summary>The size a plugin's own header gives it. The file name plays no part (as in Mutagen).</summary>
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
        var body = start[RecordHeaderLength..(int)need];
        int pos = 0; uint longForm = 0;
        while (pos + 6 <= body.Length)
        {
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]);
            long len = BinaryPrimitives.ReadUInt16LittleEndian(body[(pos + 4)..]);
            if (sig == Xxxx && len == 4 && pos + 10 <= body.Length) { longForm = BinaryPrimitives.ReadUInt32LittleEndian(body[(pos + 6)..]); pos += 10; continue; }
            if (longForm != 0) { len = longForm; longForm = 0; }
            if (pos + 6 + len > body.Length) return null;
            if (sig == Mast && len > 1)
            {
                var name = body.Slice(pos + 6, (int)len);
                int zero = name.IndexOf((byte)0);
                masters.Add(Encoding.UTF8.GetString(zero >= 0 ? name[..zero] : name));
            }
            pos += 6 + (int)len;
        }
        return new PluginHeader { Flags = BinaryPrimitives.ReadUInt32LittleEndian(start[8..]), Masters = masters, RecordsStart = need };
    }

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
