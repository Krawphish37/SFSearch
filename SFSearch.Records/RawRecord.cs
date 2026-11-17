// The raw reader. It only slices: the record header, the inflate when the record is compressed, the subrecords, and the
// XXXX long form. It never interprets a field. A FormID it meets is handed to the header index's FormIdResolver, which numbers Full,
// Medium (FD) and Small (FE) masters separately with the sizes the header index holds.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SFSearch.Index;

namespace SFSearch.Records;

/// <summary>One subrecord of a record: where its content sits in <see cref="RawRecord.Data"/>.</summary>
public readonly record struct RawSubrecord(string Signature, int Start, int Length, bool LongForm)
{
    /// <summary>Where the subrecord's own header starts (the XXXX subrecord before it not counted).</summary>
    public int HeaderStart => Start - 6;
}

/// <summary>A record as the file holds it: its 24-byte header and its subrecords, inflated when it was compressed.</summary>
public sealed class RawRecord
{
    public const int HeaderLength = 24;
    public const uint CompressedFlag = 0x00040000;

    public required string Signature { get; init; }
    public required byte[] Header { get; init; }
    /// <summary>The subrecords, uncompressed.</summary>
    public required byte[] Data { get; init; }
    public required IReadOnlyList<RawSubrecord> Subrecords { get; init; }
    public long Offset { get; init; }
    /// <summary>The size the header gives: the bytes the record takes in the file after its header.</summary>
    public uint StoredSize => BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(4));
    public uint Flags => BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(8));
    public uint FormId => BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(12));
    public uint VersionControl => BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(16));
    public ushort FormVersion => BinaryPrimitives.ReadUInt16LittleEndian(Header.AsSpan(20));
    public ushort Version2 => BinaryPrimitives.ReadUInt16LittleEndian(Header.AsSpan(22));
    public bool Compressed => (Flags & CompressedFlag) != 0;
    /// <summary>Bytes after the last whole subrecord (0 in a well-formed record).</summary>
    public int Trailing { get; init; }

    public ReadOnlySpan<byte> Content(RawSubrecord s) => Data.AsSpan(s.Start, s.Length);

    /// <summary>Reads the record at a file position.</summary>
    public static RawRecord Read(SafeFileHandle file, long offset)
    {
        var header = new byte[HeaderLength];
        ReadExactly(file, header, offset);
        string sig = Encoding.ASCII.GetString(header, 0, 4);
        if (sig == "GRUP") throw new RecordException($"The file has a group, not a record, at 0x{offset:X}. The plugin may have changed: run the command again.");
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var stored = new byte[size];
        ReadExactly(file, stored, offset + HeaderLength);
        return Parse(header, stored, offset);
    }

    /// <summary>Reads the record at a position of an open plugin. Several threads can do this at once.</summary>
    internal static RawRecord Read(PluginSession session, long offset)
    {
        var header = new byte[HeaderLength];
        session.ReadAt(offset, header);
        if (Encoding.ASCII.GetString(header, 0, 4) == "GRUP") throw new RecordException($"The file has a group, not a record, at 0x{offset:X}. The plugin may have changed: run the command again.");
        var stored = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4))];
        session.ReadAt(offset + HeaderLength, stored);
        return Parse(header, stored, offset);
    }

    /// <summary>From a whole record as stored (header and data), for a record Mutagen wrote.</summary>
    public static RawRecord FromBytes(ReadOnlySpan<byte> record, long offset = 0)
    {
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
        return Parse(record[..HeaderLength].ToArray(), record.Slice(HeaderLength, (int)size).ToArray(), offset);
    }

    /// <summary>From a record's 24-byte header and its subrecords already inflated (the record pipeline's): the header
    /// copy says the data isn't compressed and gives its size.</summary>
    public static RawRecord FromInflated(ReadOnlySpan<byte> head, ReadOnlySpan<byte> data)
    {
        var header = head[..HeaderLength].ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) & ~CompressedFlag);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)data.Length);
        return Parse(header, data.ToArray(), 0);
    }

    static RawRecord Parse(byte[] header, byte[] stored, long offset)
    {
        string sig = Encoding.ASCII.GetString(header, 0, 4);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        byte[] data = stored;
        if ((flags & CompressedFlag) != 0 && stored.Length >= 4)
        {
            uint full = BinaryPrimitives.ReadUInt32LittleEndian(stored);
            data = new byte[full];
            using var z = new ZLibStream(new MemoryStream(stored, 4, stored.Length - 4), CompressionMode.Decompress);
            int got = 0;
            while (got < data.Length)
            {
                int n = z.Read(data, got, data.Length - got);
                if (n <= 0) break;
                got += n;
            }
            if (got != data.Length) throw new RecordException($"The compressed record at 0x{offset:X} inflates to {got:N0} bytes, and its header says {full:N0}.");
        }
        var subs = new List<RawSubrecord>();
        int pos = 0; uint longLength = 0; bool longForm = false;
        while (pos + 6 <= data.Length)
        {
            string s = Encoding.ASCII.GetString(data, pos, 4);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 4));
            if (s == "XXXX" && len == 4 && pos + 10 <= data.Length)
            {
                // The long form: XXXX holds the length of the subrecord that follows, whose own length field is 0
                longLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 6));
                longForm = true; pos += 10;
                continue;
            }
            if (longForm) { len = checked((int)longLength); }
            if (pos + 6 + (long)len > data.Length) break;
            subs.Add(new RawSubrecord(s, pos + 6, len, longForm));
            pos += 6 + len; longForm = false;
        }
        return new RawRecord { Signature = sig, Header = header, Data = data, Subrecords = subs, Offset = offset, Trailing = data.Length - pos };
    }

    internal static void ReadExactly(SafeFileHandle file, Span<byte> into, long offset)
    {
        int got = 0;
        while (got < into.Length)
        {
            int n = RandomAccess.Read(file, into[got..], offset + got);
            if (n <= 0) throw new RecordException($"The file ends at 0x{offset + got:X}, inside what the header index says is a record. The plugin may have changed: run the command again.");
            got += n;
        }
    }

    public static string Hex(ReadOnlySpan<byte> bytes, int max = int.MaxValue)
    {
        var sb = new StringBuilder(Math.Min(bytes.Length, max) * 3);
        for (int i = 0; i < bytes.Length && i < max; i++) { if (i > 0) sb.Append(' '); sb.Append(bytes[i].ToString("X2")); }
        if (bytes.Length > max) sb.Append($" … ({bytes.Length - max:N0} more)");
        return sb.ToString();
    }
}
