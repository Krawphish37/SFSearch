using System.IO.Hashing;
using System.Text;

namespace SFSearch.Index;

/// <summary>
/// The cheap check made on every use: size, last-write time, and a hash of the first and last 64 KB.
/// Two stamps are compared for equality only. A date that went backwards is a change like any other.
/// </summary>
public readonly record struct QuickStamp(long Length, long WriteTicksUtc, ulong EndsHash)
{
    public const int EndBytes = 65536;

    /// <summary>Takes a file's stamp. The first bytes read are handed back so the caller can parse the plugin header from them.</summary>
    public static QuickStamp Read(string path, out byte[] start)
    {
        using var fs = Files.OpenShared(path);
        long length = fs.Length;
        long ticks = File.GetLastWriteTimeUtc(fs.SafeFileHandle).Ticks;
        start = new byte[(int)Math.Min(length, EndBytes)];
        fs.ReadExactly(start);
        var h = new XxHash3();
        h.Append(start);
        if (length > EndBytes)
        {
            // The last 64 KB, or what is left after the first 64 KB when the file is shorter than 128 KB
            int tail = (int)Math.Min(EndBytes, length - EndBytes);
            var end = new byte[tail];
            fs.Position = length - tail;
            fs.ReadExactly(end);
            h.Append(end);
        }
        return new QuickStamp(length, ticks, h.GetCurrentHashAsUInt64());
    }

    public static QuickStamp Read(string path) => Read(path, out _);

    /// <summary>True when the whole file is inside the hashed ends, so the stamp alone proves the content.</summary>
    public bool CoversWholeFile => Length <= 2 * EndBytes;
}

/// <summary>A 128-bit key shown as 32 hex digits.</summary>
public readonly record struct Key128(ulong Hi, ulong Lo)
{
    public bool IsZero => Hi == 0 && Lo == 0;
    public override string ToString() => Hi.ToString("x16") + Lo.ToString("x16");
    public string Short => Hi.ToString("x16")[..8];

    public static Key128 Parse(string hex) =>
        new(Convert.ToUInt64(hex[..16], 16), Convert.ToUInt64(hex[16..32], 16));

    internal static Key128 From(UInt128 v) => new((ulong)(v >> 64), (ulong)v);
}

public static class Keys
{
    /// <summary>The content key: XXH3 (128 bit) of the whole file.</summary>
    public static Key128 Content(string path)
    {
        using var fs = Files.OpenShared(path, FileOptions.SequentialScan);
        return Content(fs.SafeFileHandle, fs.Length);
    }

    /// <summary>Hashes the file through plain reads of 2 MB. That is faster than hashing a mapping of it, which pays
    /// for every page it touches for the first time.</summary>
    internal static Key128 Content(Microsoft.Win32.SafeHandles.SafeFileHandle file, long length)
    {
        var h = new XxHash128();
        byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 21);
        try
        {
            for (long pos = 0; pos < length;)
            {
                int n = RandomAccess.Read(file, buf.AsSpan(0, (int)Math.Min(buf.Length, length - pos)), pos);
                if (n <= 0) throw new IOException("the file got shorter while it was being hashed");
                h.Append(buf.AsSpan(0, n));
                pos += n;
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
        return Key128.From(h.GetCurrentHashAsUInt128());
    }

    /// <summary>
    /// The reading key names a plugin's segment. It changes when anything changes that changes how the file reads:
    /// its content, its own name (FormKeys carry it), a master's name or size, or the version text passed in
    /// (SFSearch's, Mutagen's and the schema's versions).
    /// </summary>
    public static Key128 Reading(Key128 content, string pluginName, IReadOnlyList<string> masters, IReadOnlyList<MasterStyle> masterStyles, string versionText)
    {
        var sb = new StringBuilder();
        sb.Append("sfsidx/").Append(Segment.FormatVersion).Append('\n');
        sb.Append(content.ToString()).Append('\n');
        sb.Append(pluginName.ToLowerInvariant()).Append('\n');
        for (int i = 0; i < masters.Count; i++)
            sb.Append(masters[i].ToLowerInvariant()).Append('=').Append(masterStyles[i]).Append('\n');
        sb.Append("version=").Append(versionText);
        return Key128.From(XxHash128.HashToUInt128(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
