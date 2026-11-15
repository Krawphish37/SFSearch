// What a read will cost, said before it starts. The inputs come from the header index alone (records and bytes of a
// signature in a plugin, and its largest record); the rates were measured on this PC on Starfield.esm with 8 reading
// threads (design\agent-first\notes\M6.md, "The cost-estimate rates"; perf-reads.md for the ones measured again after
// the reads got faster). An estimate is a round figure, not a promise.
using System.Runtime.InteropServices;
using SFSearch.Index;

namespace SFSearch.Query;

/// <summary>What the header index says about one signature in one plugin.</summary>
/// <summary><c>Inflated</c> is the bytes of the records as Mutagen reads them: the stored bytes, with compressed records counted at their inflated size.</summary>
public readonly record struct SigLoad(long Count, long Bytes, long Compressed, long LargestRecord, long Inflated)
{
    /// <summary>The signature, when the load is one signature's: some have rates of their own (<see cref="CostModel.Measured"/>).</summary>
    public string? Signature { get; init; }
}

public readonly record struct Estimate(double Seconds, long MemoryMB, long ScratchMB)
{
    public static Estimate operator +(Estimate a, Estimate b) => new(a.Seconds + b.Seconds, Math.Max(a.MemoryMB, b.MemoryMB), a.ScratchMB + b.ScratchMB);
    public static readonly Estimate None = new(0, 0, 0);
}

public static class CostModel
{
    // ---- The rates. Each is "what one unit costs", measured as said in M6.md.
    /// <summary>Seconds per record read whole (Mutagen, JSON, links, JSONB, insert), on top of the per-byte part.
    /// It was 12 while records nested in others were read through one file handle (perf-reads.md).</summary>
    public static double SecondsPerRecord = 8e-6;
    /// <summary>Seconds per MB of record data (a compressed record counts at its inflated size). Kept as M6 measured
    /// it: the two research types with many MB now take 0.01 to 0.04 (Planet, GenericBaseForm), and the world types
    /// this rate speaks for (cells, navmeshes) were never read whole.</summary>
    public static double SecondsPerFileMB = 0.045;

    /// <summary>
    /// Rates of their own for the world types, measured by reading the whole of each in Starfield.esm once through
    /// this library with heavy work allowed, one at a time, on an empty scratch folder, 2026-11-12 (design\agent-first\
    /// notes\fix-sql.md, C14; other sessions' jobs ran beside them). Measured: read time, the program's peak working
    /// set, the scratch folder's size at its largest and at the end.
    ///   Cell 30,717 records (760 MB inflated): 60.9 s, 3.0 GB, 2.8 GB scratch
    ///   NavigationMesh 56,576 (509 MB): 14.9 s, 2.7 GB, 2.9 GB
    ///   NavigationMeshInfoMap 1 record of 51 MB: 8.7 s, 4.2 GB, 0.8 GB
    ///   SurfaceTree 1,505 (941 MB inflated, 4.9 GB of JSON): 71.9 s, 2.2 GB, 24.4 GB at its largest (the WAL), 13.4 GB at the end
    ///   PlacedObject 3,291,860, whole (305 MB): 22.4 s, 2.2 GB, 2.3 GB
    /// Before, these used the general rates (SurfaceTree had 0.12 s/MB and 14 bytes of scratch a byte from 8 records of
    /// ShatteredSpace.esm); Cell was estimated at 34 s and 0.7 GB, NavigationMeshInfoMap at 0.3 GB of scratch.
    /// <c>MemoryMB</c> is added to the base and the largest record's share (never more than the scratch, so a small
    /// plugin's few records aren't charged it). <c>ScratchPeakPerByte</c> is what the disk must have free while it reads.
    /// </summary>
    public static readonly Dictionary<string, MeasuredRate> Measured = new(StringComparer.Ordinal)
    {
        ["CELL"] = new(SecondsPerRecord: 8e-6, SecondsPerMB: 0.080, ScratchPerByte: 3.72, ScratchPeakPerByte: 3.73, MemoryMB: 2400),
        ["NAVM"] = new(SecondsPerRecord: 8e-6, SecondsPerMB: 0.028, ScratchPerByte: 5.65, ScratchPeakPerByte: 5.67, MemoryMB: 2200),
        ["NAVI"] = new(SecondsPerRecord: 8e-6, SecondsPerMB: 0.170, ScratchPerByte: 16.5, ScratchPeakPerByte: 16.5, MemoryMB: 700),
        ["SFTR"] = new(SecondsPerRecord: 8e-6, SecondsPerMB: 0.076, ScratchPerByte: 14.2, ScratchPeakPerByte: 26.0, MemoryMB: 1700),
        ["REFR"] = new(SecondsPerRecord: 6.8e-6, SecondsPerMB: 0, ScratchPerByte: 7.46, ScratchPeakPerByte: 7.48, MemoryMB: 1700),
    };

    /// <summary>A signature's own rates (<see cref="Measured"/>).</summary>
    public readonly record struct MeasuredRate(double SecondsPerRecord, double SecondsPerMB, double ScratchPerByte, double ScratchPeakPerByte, long MemoryMB);

    /// <summary>Scratch bytes written per byte of record data (JSONB with field names against packed binary, and the links).</summary>
    public static double ScratchPerFileByte = 5.0;
    /// <summary>Memory while a signature is read: the reading threads' batches, SQLite's cache, the plugin's group.</summary>
    public static long BaseMemoryMB = 500;
    /// <summary>Memory per MB of the largest single record: its Mutagen object, its JSON text and its JSONB at once.</summary>
    public static double MemoryPerLargestRecordMB = 60;
    /// <summary>Seconds per record when only named columns are read (Mutagen, JSON, the column expressions, insert).
    /// Measured on the 3.29 million placed objects of Starfield.esm, the first read of a new process: 8.1 to 8.8 s,
    /// and 5.7 to 6.5 s in a host that makes hot code fast at once (perf-reads.md). It was 5.5.</summary>
    public static double SecondsPerProjectedRecord = 2.0e-6;
    /// <summary>Seconds per record when no column needs the record itself: rows straight from the header index.</summary>
    public static double SecondsPerIndexRow = 0.6e-6;
    /// <summary>Seconds per MB of a read type's JSON when its links are put into the Links table.</summary>
    public static double LinkSecondsPerJsonMB = 0.012;
    /// <summary>Seconds per record of the header index put into the Forms table, with its two indexes.</summary>
    public static double SecondsPerFormRow = 1.6e-6;
    /// <summary>Time the first read of any type takes in a new process, not counted against the limit (code start-up).</summary>
    public const double StartupSeconds = 1.0;

    static readonly System.Collections.Concurrent.ConcurrentDictionary<(Key128, string), SigLoad> LoadCache = new();

    /// <summary>
    /// Records, bytes and the largest record of a signature in a plugin, from the header index. For a signature whose
    /// records are compressed the index only has the stored size, and the JSON follows the size after inflating
    /// (Starfield.esm's surface trees are 86 MB stored and 4.9 GB as JSON). Each compressed record says its inflated
    /// size in its first four bytes: up to 2,000 of them are read, which takes milliseconds.
    /// </summary>
    public static SigLoad Load(IndexedPlugin plugin, string signature)
    {
        var seg = plugin.Segment;
        if (seg == null) return default;
        if (LoadCache.TryGetValue((plugin.ReadingKey, signature), out var cached)) return cached;
        foreach (var s in seg.Signatures)
            if (s.Signature == signature)
            {
                long largest = 0, inflated = s.Bytes;
                bool walk = s.Count > 0 && (s.Bytes / s.Count > 256 << 10 || s.Count <= 64 || s.Compressed > 0);
                if (!walk) largest = s.Count == 0 ? 0 : Math.Min(s.Bytes, 4 * (s.Bytes / s.Count) + (64 << 10));
                else
                {
                    var compressed = new List<Entry>();
                    long plainBytes = 0, storedCompressed = 0;
                    foreach (int e in seg.EntriesOf(signature))
                    {
                        var entry = seg[e];
                        if (entry.IsCompressed && entry.Size >= 4) { compressed.Add(entry); storedCompressed += entry.Size; }
                        else { plainBytes += entry.Size; largest = Math.Max(largest, entry.Size); }
                    }
                    if (compressed.Count > 0)
                    {
                        try
                        {
                            using var file = File.OpenHandle(plugin.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            int step = Math.Max(1, compressed.Count / 2000);
                            long sampledStored = 0, sampledInflated = 0;
                            Span<byte> four = stackalloc byte[4];
                            for (int i = 0; i < compressed.Count; i += step)
                            {
                                if (RandomAccess.Read(file, four, compressed[i].Offset + 24) != 4) continue;
                                long size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(four);
                                sampledStored += compressed[i].Size; sampledInflated += size;
                                largest = Math.Max(largest, size);
                            }
                            inflated = plainBytes + (sampledStored == 0 ? storedCompressed : (long)(storedCompressed * (double)sampledInflated / sampledStored));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { inflated = plainBytes + storedCompressed * 8; }
                    }
                }
                var load = new SigLoad(s.Count, s.Bytes, s.Compressed, largest, inflated) { Signature = signature };
                LoadCache[(plugin.ReadingKey, signature)] = load;
                return load;
            }
        return default;
    }

    /// <summary>Reading a signature whole into the scratch tables. Its scratch is what the disk must have free while
    /// it reads (for SurfaceTree nearly twice what stays).</summary>
    public static Estimate Full(SigLoad l)
    {
        if (l.Count == 0) return Estimate.None;
        double mb = l.Inflated / 1048576.0;
        double perRecord = SecondsPerRecord, perMB = SecondsPerFileMB, scratchPerByte = ScratchPerFileByte; long more = 0;
        if (l.Signature != null && Measured.TryGetValue(l.Signature, out var m)) { perRecord = m.SecondsPerRecord; perMB = m.SecondsPerMB; scratchPerByte = m.ScratchPeakPerByte; more = m.MemoryMB; }
        double seconds = l.Count * perRecord + mb * perMB;
        long scratch = (long)(mb * scratchPerByte);
        long memory = BaseMemoryMB + (long)(l.LargestRecord / 1048576.0 * MemoryPerLargestRecordMB) + Math.Min(more, scratch);
        return new Estimate(seconds, memory, scratch);
    }

    /// <summary>True when a signature's rates were measured whole on Starfield.esm.</summary>
    public static bool IsMeasured(string signature) => Measured.ContainsKey(signature);

    /// <summary>Reading only named columns of a signature (or none: the header index alone).</summary>
    public static Estimate Projection(SigLoad l, int columns)
    {
        if (l.Count == 0) return Estimate.None;
        double seconds = columns == 0 ? l.Count * SecondsPerIndexRow : l.Count * (SecondsPerProjectedRecord + SecondsPerIndexRow);
        return new Estimate(seconds, columns == 0 ? 300 : 1000, l.Count * (60 + 24 * columns) / 1048576);
    }

    /// <summary>The header index of a plugin set as the Forms table.</summary>
    public static Estimate Forms(long records) => new(records * SecondsPerFormRow, 500, records * 120 / 1048576);

    public static Estimate Links(long jsonBytes) => new(jsonBytes / 1048576.0 * LinkSecondsPerJsonMB, 300, jsonBytes / 1048576 / 4);
}

/// <summary>How much memory Windows has to give right now ("available": free plus standby, what Task Manager shows).</summary>
public static class MemoryNow
{
    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>Available physical memory in MB, or -1 when it can't be asked (not Windows).</summary>
    public static long AvailableMB()
    {
        if (!OperatingSystem.IsWindows()) return -1;
        var s = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref s) ? (long)(s.AvailPhys >> 20) : -1;
    }
}
