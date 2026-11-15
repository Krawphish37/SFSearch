using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Starfield;

namespace SFSearch.Harness;

public sealed class RoundTripResult
{
    public string Plugin { get; set; } = "";
    public string When { get; set; } = "";
    public string Mutagen { get; set; } = "";
    public bool Identical { get; set; }
    public long Bytes { get; set; }
    public long WrittenBytes { get; set; }
    public long Records { get; set; }
    public double Seconds { get; set; }
    public long PeakMB { get; set; }
    public long FirstDifferenceAt { get; set; } = -1;
    public long RecordsThatDiffer { get; set; }
    public List<string> First { get; set; } = [];
    public string? Problem { get; set; }
    public string Line() => Problem != null ? $"problem: {Problem}"
        : Identical ? $"identical: {Bytes:N0} bytes, {Records:N0} records, {Seconds:0.0} s, {PeakMB:N0} MB peak"
        : $"DIFFERENT: first at byte {FirstDifferenceAt:N0}; {RecordsThatDiffer:N0} record(s) differ; {Bytes:N0} bytes read, {WrittenBytes:N0} written";
}

/// <summary>
/// The whole-plugin round trip (ARCHITECTURE.md section 11): read a plugin and write it through Mutagen to a folder
/// under the work folder, then compare with the file byte for byte. On a difference the first differing records are named.
/// </summary>
public static class RoundTrip
{
    public static string ResultsFile => Paths.Out("roundtrip", "results.json");

    public static string Resolve(string plugin) => File.Exists(plugin) ? Path.GetFullPath(plugin) : Path.Combine(Paths.Data, plugin);

    public static async Task<RoundTripResult> Run(string plugin)
    {
        string path = Resolve(plugin);
        var res = new RoundTripResult { Plugin = Path.GetFileName(path), When = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Mutagen = typeof(StarfieldMod).Assembly.GetName().Version?.ToString() ?? "?" };
        res.Mutagen = (typeof(StarfieldMod).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).FirstOrDefault()
            as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion.Split('+')[0] ?? res.Mutagen;
        if (!File.Exists(path)) { res.Problem = $"{plugin} isn't in Data and isn't a path"; return res; }
        string outDir = Paths.Out("roundtrip", Path.GetFileNameWithoutExtension(path));
        string written = Path.Combine(outDir, Path.GetFileName(path));
        Directory.CreateDirectory(outDir);
        if (File.Exists(written)) File.Delete(written);
        var clock = Stopwatch.StartNew();
        try
        {
            // The read and the write of design\agent-first: masters from the plugin's own header, their sizes from the Data folder
            using (var mod = StarfieldMod.Create(StarfieldRelease.Starfield).FromPath(path).WithLoadOrderFromHeaderMasters().WithDataFolder(Paths.Data).Construct())
                await mod.BeginWrite.ToPath(written).WithLoadOrderFromHeaderMasters().WithDataFolder(Paths.Data).WriteAsync();
        }
        catch (Exception e)
        {
            res.Problem = $"Mutagen couldn't read and write it: {e.GetType().Name}: {e.Message.Split('\n')[0]}";
            res.Seconds = Math.Round(clock.Elapsed.TotalSeconds, 1);
            return res;
        }
        res.Bytes = new FileInfo(path).Length;
        res.WrittenBytes = new FileInfo(written).Length;
        res.FirstDifferenceAt = FirstDifference(path, written);
        res.Identical = res.FirstDifferenceAt < 0;
        var diff = RawPlugin.Diff(path, written, res.Identical ? 0 : 10);
        res.Records = diff.RecordsA;
        res.RecordsThatDiffer = diff.Differ + diff.OnlyA + diff.OnlyB;
        res.First = diff.First;
        res.Seconds = Math.Round(clock.Elapsed.TotalSeconds, 1);
        res.PeakMB = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576;
        if (res.Identical) { File.Delete(written); try { Directory.Delete(outDir); } catch (IOException) { } }
        else res.First.Add($"the written file is kept for a closer look: {written}");
        Remember(res);
        return res;
    }

    /// <summary>Keeps each plugin's last result, so the one table can show Starfield.esm's without running it again.</summary>
    static void Remember(RoundTripResult r)
    {
        var all = Remembered();
        all[r.Plugin.ToLowerInvariant()] = r;
        Json.Save(ResultsFile, all);
    }

    public static Dictionary<string, RoundTripResult> Remembered() =>
        File.Exists(ResultsFile) ? Json.Load<Dictionary<string, RoundTripResult>>(ResultsFile) : [];

    /// <summary>The first byte position where the two files differ (their common length when one is a prefix of the other), or -1.</summary>
    public static long FirstDifference(string a, string b)
    {
        using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
        using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
        byte[] ba = new byte[1 << 20], bb = new byte[1 << 20];
        long pos = 0;
        while (true)
        {
            int na = fa.ReadAtLeast(ba, ba.Length, false), nb = fb.ReadAtLeast(bb, bb.Length, false);
            int n = Math.Min(na, nb);
            int at = ba.AsSpan(0, n).CommonPrefixLength(bb.AsSpan(0, n));
            if (at < n) return pos + at;
            if (na != nb) return pos + n;
            if (n == 0) return -1;
            pos += n;
        }
    }
}

/// <summary>
/// A plugin file as its records, read straight from the bytes (no Mutagen): header, groups, records. Used only to
/// name where two files differ.
/// </summary>
public static class RawPlugin
{
    public readonly record struct Rec(uint Sig, uint FormId, long Offset, int Length);

    public sealed class DiffResult
    {
        public long RecordsA, RecordsB, Differ, OnlyA, OnlyB;
        public bool SameOrder = true;
        public List<string> First = [];
    }

    public static List<Rec> Index(SafeFileHandle file, long length)
    {
        var list = new List<Rec>();
        Span<byte> head = stackalloc byte[24];
        long pos = 0;
        while (pos + 24 <= length)
        {
            if (RandomAccess.Read(file, head, pos) < 24) break;
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(head);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
            if (sig == 0x50555247) { pos += 24; continue; } // "GRUP": its records follow
            list.Add(new Rec(sig, BinaryPrimitives.ReadUInt32LittleEndian(head[12..]), pos, 24 + (int)size));
            pos += 24 + size;
        }
        return list;
    }

    const uint Tes4 = 0x34534554; // the plugin's header record, not counted as a record

    static string SigText(uint sig) => Encoding.ASCII.GetString(BitConverter.GetBytes(sig));

    static byte[] Read(SafeFileHandle file, Rec r)
    {
        var b = new byte[r.Length];
        RandomAccess.Read(file, b, r.Offset);
        return b;
    }

    /// <summary>The record's EditorID, from its own bytes (inflated when the record is compressed).</summary>
    public static string? EditorId(byte[] record)
    {
        try
        {
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(8));
            ReadOnlySpan<byte> data = record.AsSpan(24);
            if ((flags & 0x00040000) != 0 && data.Length > 4)
            {
                using var z = new ZLibStream(new MemoryStream(record, 28, record.Length - 28), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                z.CopyTo(ms);
                data = ms.ToArray();
            }
            int pos = 0; uint big = 0;
            while (pos + 6 <= data.Length)
            {
                uint sig = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
                uint len = big != 0 ? big : BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 4)..]);
                if (sig == 0x58585858) { big = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 6)..]); pos += 10; continue; } // XXXX: the next one's real length
                big = 0;
                if (sig == 0x44494445) return Encoding.ASCII.GetString(data.Slice(pos + 6, (int)len)).TrimEnd('\0'); // EDID
                pos += 6 + (int)len;
            }
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IOException) { }
        return null;
    }

    /// <summary>A record's name for the report: EditorID, type, and its FormID as the file stores it.</summary>
    static string Name(SafeFileHandle file, Rec r, string fileName) =>
        $"{EditorId(Read(file, r)) ?? "(no EditorID)"} ({SigText(r.Sig)}, FormID {r.FormId:X8} as stored in {fileName})";

    /// <summary>Compares two plugin files record by record. Names up to <paramref name="show"/> of the first that differ, in the first file's order.</summary>
    public static DiffResult Diff(string pathA, string pathB, int show, string second = "written")
    {
        var res = new DiffResult();
        using var fa = File.OpenHandle(pathA, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var fb = File.OpenHandle(pathB, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var a = Index(fa, RandomAccess.GetLength(fa));
        res.RecordsA = a.Count(r => r.Sig != Tes4);
        if (show == 0) { res.RecordsB = res.RecordsA; return res; }
        var b = Index(fb, RandomAccess.GetLength(fb));
        res.RecordsB = b.Count(r => r.Sig != Tes4);
        string nameA = Path.GetFileName(pathA);
        var inB = new Dictionary<(uint, uint), Rec>(b.Count);
        foreach (var r in b) inB.TryAdd((r.Sig, r.FormId), r);
        var seen = new HashSet<(uint, uint)>();
        for (int i = 0; i < a.Count; i++)
        {
            var r = a[i];
            seen.Add((r.Sig, r.FormId));
            if (i < b.Count && (b[i].Sig != r.Sig || b[i].FormId != r.FormId)) res.SameOrder = false;
            if (!inB.TryGetValue((r.Sig, r.FormId), out var other))
            {
                res.OnlyA++;
                if (res.First.Count < show) res.First.Add($"only in the file: {Name(fa, r, nameA)}");
                continue;
            }
            if (r.Length == other.Length && Read(fa, r).AsSpan().SequenceEqual(Read(fb, other))) continue;
            res.Differ++;
            if (res.First.Count < show)
            {
                byte[] x = Read(fa, r), y = Read(fb, other);
                int at = x.AsSpan().CommonPrefixLength(y);
                res.First.Add($"differs: {Name(fa, r, nameA)}: {x.Length} bytes in the file at {r.Offset:N0}, {y.Length} in the {second} one; first different byte {at} of the record"
                    + (at < 24 ? " (in its header)" : ""));
            }
        }
        foreach (var r in b)
            if (!seen.Contains((r.Sig, r.FormId)))
            {
                res.OnlyB++;
                if (res.First.Count < show) res.First.Add($"only in the {second} file: {Name(fb, r, Path.GetFileName(pathB))}");
            }
        if (res.Differ + res.OnlyA + res.OnlyB == 0)
            res.First.Add(res.SameOrder ? "every record's bytes are the same: the difference is in a group header or between records" : "every record's bytes are the same, but their order differs");
        return res;
    }
}
