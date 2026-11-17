// The paths of materials a compiled database names only by hashes (GAPS S33a). A database holds, for each material, a
// CRC-32 of its folder and one of its file name, never the path. A path is told two ways:
//  - Whole, from a path some file in Data writes out: a .mat file's own path, the material paths .mat files name
//    (their parent, their imports), the material paths every mesh (.nif) names in its header's strings, and the .mat
//    texts the plugins' records hold (S33g: a material swap's materials, a landscape texture's, a biome's water; read
//    from the records' bytes, RecordMats), the material paths the particle files (.blob) name, and the paths the
//    databases' own object names hold ("Data\Materials\x\y.mat_Material1", j811). Both hashes must agree: 64 bits.
//  - From its name and a folder: the material's name (its BSComponentDB::CTName) is its file's name when the file hash
//    agrees (48,740 of the game's 48,749), and the folder is one whose hash is the material's folder hash. The folders
//    tried are those Data names: every folder of every archive, as it is and with its first folder read as materials\
//    (meshes\x and textures\x give materials\x), the folders of the textures the databases name read the same way, and
//    the folders of the material paths above. Only the folder hash is tested here (32 bits): of the about 430,000
//    folders tried, a wrong one would agree with one of the about 1,000 folder hashes left unknown about once in ten
//    thousand tries, so a wrong path is unlikely but not ruled out.
// When (fix-s33h): the gather reads every mesh's header, so a plain call doesn't run it. It runs when asked
// (materials2 --all-paths), and in the service's indexer lane after each look of the header index. What it finds is
// kept for the process and in the scratch folder (material-paths.tsv, about 2 MB), keyed on the size and date of every
// archive, loose .mat file and plugin it read: every later call and every worker takes it from there while nothing
// changed. It is derived from file names and the paths files write, as the strings-archive list (fix-h3) is. A path
// a record writes is kept only as the name of a database's material (its hashes agree); which record wrote it, or
// anything else of the record, isn't kept.
// A mesh's header is read through Mutagen's archive reader that keeps the archive open (Archive.CreateDisposableReader,
// C199: one handle, positional reads) and its stream of the entry (C193, inflated by .NET's ZLibStream since C198),
// only the first few KB of each entry. SFSearch's own BA2 reader of the entry table is gone (GAPS Part 2 row 32): the
// same 28,798 paths from the 129,599 meshes, about 0.5 s more for the mesh part (warm), in the background lane.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using SFSearch.Index;

namespace SFSearch.Reports;

/// <summary>Material paths and folders found in Data, by their hashes (GAPS S33a).</summary>
public sealed class MaterialPathEvidence
{
    /// <summary>Whole paths, by (folder hash &lt;&lt; 32 | file hash).</summary>
    internal readonly Dictionary<ulong, string> Paths = new();
    /// <summary>Folders, by their hash.</summary>
    internal readonly Dictionary<uint, string> Folders = new();
    public int PathsFound => Paths.Count;
    public int FoldersFound => Folders.Count;
    public int FoldersTried { get; internal set; }
    /// <summary>The distinct material paths Data writes (every text the gather tries as a path), and the pairs of
    /// different paths among them with the same folder and file hashes: a database, and the game, would take them for
    /// one material. Counted when gathered, not kept in the scratch file (0 there).</summary>
    public int PathTexts { get; internal set; }
    public List<(string A, string B)> Collisions { get; } = new();
    public int MatFiles { get; internal set; }
    public int Meshes { get; internal set; }
    /// <summary>Meshes read whole because Mutagen's stream of the entry failed (GAPS Part 2 row 32). Always 0 since
    /// Mutagen 0.55.0-fixes.14 fixed the stream (C193); kept for the scratch files gathered before.</summary>
    public int MeshesReadWhole { get; internal set; }
    public int ArchivesNamed { get; internal set; }
    /// <summary>The bytes of the meshes inflated to read their headers.</summary>
    public long MeshBytes { get; internal set; }
    /// <summary>The bytes read from the archives for them (packed). 0 since fixes.15: Mutagen's reader reads them and
    /// doesn't say how many; kept for the scratch files gathered before.</summary>
    public long MeshDiskBytes { get; internal set; }
    public double Seconds { get; internal set; }
    public double MeshSeconds { get; internal set; }
    public double MatSeconds { get; internal set; }
    /// <summary>The plugins whose records were read for the material paths they name (S33g).</summary>
    public int RecordPlugins { get; internal set; }
    /// <summary>The records read: those of <see cref="MaterialPaths.RecordTypes"/>.</summary>
    public long Records { get; internal set; }
    /// <summary>Their bytes as read (a compressed record's inflated).</summary>
    public long RecordBytes { get; internal set; }
    /// <summary>The distinct .mat texts they hold.</summary>
    public int RecordTexts { get; internal set; }
    public double RecordSeconds { get; internal set; }
    /// <summary>The particle files (.blob) read for the material paths they name (j811), and their bytes.</summary>
    public int Blobs { get; internal set; }
    public long BlobBytes { get; internal set; }
    /// <summary>The names of the databases' objects that hold a material's path ("Data\Materials\x\y.mat_Material1": a
    /// layer's material named for the file it was made in, j811).</summary>
    public int DatabaseNames { get; internal set; }
    public DateTime GatheredUtc { get; internal set; }
    /// <summary>Taken by a plain call although a plugin changed since it was gathered (MaterialPaths.KeyOf).</summary>
    public bool OlderPlugins { get; internal set; }
    internal string Key = "";
}

/// <summary>Where a call got the evidence: gathered now, from the process's memory, or from the scratch folder.</summary>
public enum EvidenceFrom { Now, Memory, Scratch }

public static class MaterialPaths
{
    // Internal (J81): the tests read what this process holds, and forget it as a new process would start, through
    // InternalsVisibleTo (sfsreport-tests' MaterialDatabaseTests.KeptNow and Forget)
    internal static MaterialPathEvidence? _kept;
    internal static readonly object Gate = new();
    /// <summary>The scratch file's name.</summary>
    public const string FileName = "material-paths.tsv";
    /// <summary>Changes when what the gather keeps changes, so an older file isn't used.</summary>
    const string Version = "s33a-2";

    static uint DirHash(string folder) => MaterialDatabase.ResourceId(folder + @"\x.mat").Dir;

    /// <summary>
    /// The key of what the gather reads, in two parts, "files-plugins": every archive's and every loose .mat file's
    /// name, size and date; then every plugin's (S33g: their records are read too) as the header index stamped it
    /// (size, date, a hash of its two ends). --all-paths and the service's job gather again when either part changed.
    /// A plain call also takes a gather whose plugins part is older (<see cref="Kept"/>): its databases are the same
    /// files, so every path it tells is still the path of a material they hold (both hashes agreed when it was kept);
    /// a plugin saved since can only have added paths it doesn't know yet.
    /// </summary>
    internal static string KeyOf(IEnumerable<string> archives, IEnumerable<string> looseMats, IEnumerable<IndexedPlugin> plugins)
    {
        var files = new StringBuilder(Version).Append('|');
        foreach (var a in archives.OrderBy(a => a, StringComparer.OrdinalIgnoreCase)) { var s = ArchiveStore.Stamp(a); files.Append(s.Name).Append(s.Length).Append(s.WriteUtc.Ticks).Append('|'); }
        foreach (var m in looseMats.OrderBy(a => a, StringComparer.OrdinalIgnoreCase)) { var s = ArchiveStore.Stamp(m); files.Append(m).Append(s.Length).Append(s.WriteUtc.Ticks).Append('|'); }
        var records = new StringBuilder(Version).Append('|');
        foreach (var p in plugins.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            records.Append(p.Name).Append(p.Stamp.Length).Append(p.Stamp.WriteTicksUtc).Append(p.Stamp.EndsHash).Append(p.Segment == null ? "-" : "").Append('|');
        static string H(StringBuilder b) => Convert.ToHexString(System.IO.Hashing.XxHash3.Hash(Encoding.UTF8.GetBytes(b.ToString())));
        return H(files) + "-" + H(records);
    }

    /// <summary>The files part of a key (<see cref="KeyOf"/>).</summary>
    static string FilesPart(string key) { int at = key.IndexOf('-'); return at < 0 ? key : key[..at]; }

    /// <summary>A kept gather fits a key: the same key; or, when <paramref name="olderPlugins"/>, the same files part.</summary>
    static bool Fits(string kept, string key, bool olderPlugins) => kept == key || olderPlugins && FilesPart(kept) == FilesPart(key);

    /// <summary>The evidence for this key when it is at hand: in memory, else in the scratch folder. Null when it would
    /// have to be gathered. With <paramref name="olderPlugins"/> (a plain call), one gathered before a plugin changed
    /// is taken too (<see cref="KeyOf"/>); <see cref="MaterialPathEvidence.OlderPlugins"/> then says so.</summary>
    internal static MaterialPathEvidence? Kept(string key, string? scratch, out EvidenceFrom from, bool olderPlugins = false)
    {
        lock (Gate)
        {
            from = EvidenceFrom.Memory;
            if (_kept is { } have && Fits(have.Key, key, olderPlugins)) return Mark(have);
            from = EvidenceFrom.Scratch;
            if (scratch != null && Load(Path.Combine(scratch, FileName), key, olderPlugins) is { } loaded) return Mark(_kept = loaded);
            return null;
        }
        MaterialPathEvidence Mark(MaterialPathEvidence e) { e.OlderPlugins = e.Key != key; return e; }
    }

    /// <summary>The evidence: from memory or the scratch folder when it is there for this key, else gathered now and
    /// kept in both (in the scratch folder only when <paramref name="save"/>).</summary>
    /// <param name="archives">Every archive in Data.</param>
    /// <param name="databases">Every compiled material database, its material list read: only the hashes their
    /// materials have are kept.</param>
    /// <param name="textureNames">The texture names of the databases read whole.</param>
    /// <param name="objectNames">The names of the databases' objects that hold ".mat" (<see cref="MaterialContents.MatNames"/>).</param>
    /// <param name="matFiles">Every .mat file in Data, each with the material paths it names.</param>
    /// <param name="plugins">Every plugin of the header index: their records' material paths (S33g).</param>
    internal static MaterialPathEvidence Get(string key, string? scratch, bool save, ArchiveStore store, IReadOnlyList<string> archives, IReadOnlyList<MaterialDatabase> databases,
        Func<IEnumerable<string>> textureNames, Func<IEnumerable<string>> objectNames, Func<IReadOnlyList<(string Path, string[] Names)>> matFiles, IReadOnlyList<IndexedPlugin> plugins, out EvidenceFrom from)
    {
        lock (Gate)
        {
            if (Kept(key, scratch, out from) is { } have) return have;
            var e = Gather(key, store, archives, databases, textureNames, objectNames, matFiles, plugins);
            from = EvidenceFrom.Now;
            if (save && scratch != null) Save(Path.Combine(scratch, FileName), e);
            return _kept = e;
        }
    }

    /// <summary>Adds what Data names to the evidence: the hashes some database's material has (the gather's and the
    /// probe's one way of doing it).</summary>
    internal sealed class Collector
    {
        readonly MaterialPathEvidence _e;
        readonly HashSet<uint> _wantDirs = new();
        readonly IReadOnlyList<MaterialDatabase> _databases;
        readonly Dictionary<ulong, string> _every = new();

        public Collector(MaterialPathEvidence e, IReadOnlyList<MaterialDatabase> databases)
        {
            _e = e;
            _databases = databases.Where(d => d.Error == null).ToList();
            foreach (var db in _databases) foreach (var d in db.Dirs()) _wantDirs.Add(d);
        }

        public void Folder(string folder)
        {
            folder = folder.Trim('\\');
            while (folder.Length > 0)
            {
                _e.FoldersTried++;
                uint h = DirHash(folder);
                if (_wantDirs.Contains(h)) _e.Folders.TryAdd(h, folder);
                int at = folder.LastIndexOf('\\');
                if (at < 0) break;
                folder = folder[..at];
            }
        }

        /// <summary>A folder as it is, and with its first folder read as materials\</summary>
        public void Both(string folder)
        {
            Folder(folder);
            int at = folder.IndexOf('\\');
            if (at > 0 && !folder.StartsWith(@"materials\", StringComparison.Ordinal)) Folder("materials" + folder[at..]);
        }

        /// <summary>A material path some file writes: kept whole when a database has a material with both its hashes,
        /// and its folders tried.</summary>
        public void MatPath(string text)
        {
            string p = MaterialsReport.RecordMaterial(text.Trim());
            var (file, _, dir) = MaterialDatabase.ResourceId(p);
            ulong id = ((ulong)dir << 32) | file;
            string same = p.Replace('/', '\\');
            if (!_every.TryGetValue(id, out var had)) { _every[id] = same; _e.PathTexts = _every.Count; }
            else if (!had.Equals(same, StringComparison.OrdinalIgnoreCase)) _e.Collisions.Add((had, same));
            if (!_wantDirs.Contains(dir)) { Both(System.IO.Path.GetDirectoryName(p) ?? ""); return; }
            if (_databases.Any(d => d.Has(file, dir))) _e.Paths.TryAdd(((ulong)dir << 32) | file, p);
            Folder(System.IO.Path.GetDirectoryName(p) ?? "");
        }
    }

    static MaterialPathEvidence Gather(string k, ArchiveStore store, IReadOnlyList<string> archives, IReadOnlyList<MaterialDatabase> databases,
        Func<IEnumerable<string>> textureNames, Func<IEnumerable<string>> objectNames, Func<IReadOnlyList<(string Path, string[] Names)>> matFiles, IReadOnlyList<IndexedPlugin> plugins)
    {
        var sw = Stopwatch.StartNew();
        var e = new MaterialPathEvidence { Key = k, GatheredUtc = DateTime.UtcNow };
        var c = new Collector(e, databases);

        // .mat files: their own paths, and the paths they name
        var part = Stopwatch.StartNew();
        foreach (var (path, names) in matFiles())
        {
            e.MatFiles++;
            c.MatPath(path);
            foreach (var n in names) c.MatPath(n);
        }
        e.MatSeconds = part.Elapsed.TotalSeconds;
        // Every folder of every archive; and the material paths every mesh names, as header strings
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var withMeshes = new List<string>();
        var withBlobs = new List<string>();
        foreach (var a in archives)
        {
            var paths = store.PathsOf(a);
            e.ArchivesNamed++;
            bool meshes = false, blobs = false;
            foreach (var p in paths.Paths)
            {
                int at = p.LastIndexOf('\\');
                if (at > 0)
                {
                    string f = p[..at].ToLowerInvariant();
                    if (seen.Add(f)) c.Both(f);
                }
                if (!meshes && p.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) meshes = true;
                if (!blobs && p.EndsWith(".blob", StringComparison.OrdinalIgnoreCase)) blobs = true;
            }
            if (meshes) withMeshes.Add(a);
            if (blobs) withBlobs.Add(a);
        }
        // The particle files (.blob), loose and in the archives: a particle system names its materials by path
        // (sfbgs00b - main.ba2's completepsfx.blob: materials\effects\blood\bloodspurt_medium_01.mat, which nothing
        // else in Data writes)
        // A file read before (the same bytes: nine archives hold the same 27 MB file) isn't searched again
        var blobsSeen = new HashSet<ulong>();
        foreach (var loose in store.Loose.Where(f => f.EndsWith(".blob", StringComparison.OrdinalIgnoreCase)))
        {
            byte[] b;
            try { b = File.ReadAllBytes(System.IO.Path.Combine(store.DataFolder, loose)); }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException) { continue; }
            Blob(b, e, c, blobsSeen);
        }
        foreach (var a in withBlobs) Blobs(a, e, c, blobsSeen);
        part.Restart();
        var named = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var counts = new MeshCounts();
        void Strings(List<string>? strings)
        {
            if (strings == null) return;
            foreach (var t in strings) if (t.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) named.TryAdd(t, 0);
        }
        foreach (var a in withMeshes) MeshHeaders(a, Strings, counts);
        e.Meshes = counts.Meshes; e.MeshBytes = counts.Inflated;
        e.MeshSeconds = part.Elapsed.TotalSeconds;
        foreach (var t in named.Keys) c.MatPath(t);
        // The material paths the plugins' records name (S33g)
        part.Restart();
        var rec = RecordMats(plugins, RecordTypes);
        foreach (var t in rec.Texts.Keys) c.MatPath(t);
        e.RecordPlugins = rec.Plugins; e.Records = rec.Records; e.RecordBytes = rec.Bytes; e.RecordTexts = rec.Texts.Count;
        e.RecordSeconds = part.Elapsed.TotalSeconds;
        // The databases' own names that hold a material's path (a layer's material made in a file: "Data\Materials\Test\
        // Test\MGoulet_Pattern05_Test.mat_Material2" in SFBGS007's database): the text to ".mat"
        foreach (var n in objectNames())
        {
            int at = n.IndexOf(".mat", StringComparison.OrdinalIgnoreCase);
            if (at <= 0 || (at + 4 < n.Length && char.IsAsciiLetterOrDigit(n[at + 4]))) continue;
            e.DatabaseNames++;
            c.MatPath(n[..(at + 4)]);
        }
        // The textures' folders
        var texFolders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in textureNames())
        {
            string n = MaterialsReport.Norm(t);
            int at = n.LastIndexOf('\\');
            if (at > 0 && texFolders.Add(n[..at])) c.Both(n[..at]);
        }
        e.Seconds = sw.Elapsed.TotalSeconds;
        return e;
    }

    // ---------------------------------------------------------------- The plugins' records (S33g)
    /// <summary>
    /// The record types whose records are read for the material paths they name. Chosen by a raw read of every record
    /// of the 363 plugins in this Data folder (12.2 million records, 5.9 GB inflated, 55 s; notes\fix-s33efg.md): these
    /// 14 types and CELL are the only ones whose bytes hold a .mat text. LMSW (material swaps) holds most (11,965
    /// texts), then MTPT, LTEX, EFSQ, PNDT, BIOM, STDT. CELL is left out: 1.6 GB of inflated records for 7 texts,
    /// which tell no path the other types don't. Not settable (J81); the test that gathers without the records empties
    /// it for that gather and puts the types back (through InternalsVisibleTo).
    /// </summary>
    internal static readonly HashSet<string> RecordTypes = new(StringComparer.Ordinal)
    {
        "LMSW", "MTPT", "LTEX", "EFSQ", "PNDT", "BIOM", "STDT", "WRLD", "ACTI", "STAT", "DOOR", "MSTT", "CONT", "TERM",
    };

    /// <summary>What a raw read of the plugins' records found.</summary>
    internal sealed class RecordScan
    {
        /// <summary>Every .mat text, as the record writes it.</summary>
        public readonly ConcurrentDictionary<string, byte> Texts = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>For the probe: per record type, the records read, their bytes, the records with a .mat text, and
        /// the texts.</summary>
        public readonly ConcurrentDictionary<string, TypeCount> ByType = new(StringComparer.Ordinal);
        public int Plugins;
        public long Records, Bytes, NotInflated;
    }

    internal sealed class TypeCount
    {
        public long Records, Bytes, WithMat;
        public readonly ConcurrentDictionary<string, byte> Texts = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The .mat texts the plugins' records hold, read from the records' bytes (S33g): the header index gives each
    /// record's place, type and size, so no record is parsed; a record of one of <paramref name="types"/> is read
    /// (inflated when it is compressed) and every ".mat" in it taken with the text before it. A text runs back over
    /// path characters to the byte before it (a zstring's start, a length, the subrecord's size: none is a path
    /// character unless it is a length of 32 to 126, then "materials\" starts the path). Nothing goes through
    /// Mutagen. A text that isn't a material's path does no harm: it is kept only when a database has a material
    /// with both its hashes, or its folder's hash is one a database's materials have.
    /// </summary>
    internal static RecordScan RecordMats(IReadOnlyList<IndexedPlugin> plugins, HashSet<string>? types, bool byType = false)
    {
        var scan = new RecordScan();
        foreach (var p in plugins)
        {
            if (p.Segment is not { } seg || p.Problems.ReadError != null) continue;
            bool[] want = new bool[seg.Signatures.Count];
            bool any = false;
            for (int i = 0; i < want.Length; i++) any |= want[i] = types == null || types.Contains(seg.Signatures[i].Signature);
            if (!any) continue;
            var rows = new List<int>();
            for (int i = 0; i < seg.Count; i++) if (want[seg[i].Signature]) rows.Add(i);
            if (rows.Count == 0) continue;
            scan.Plugins++;
            SafeFileHandle handle;
            try { handle = File.OpenHandle(p.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess); }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException) { continue; }
            using (handle)
            {
                long length = RandomAccess.GetLength(handle);
                Parallel.ForEach(Partitioner.Create(0, rows.Count, 512), new ParallelOptions { MaxDegreeOfParallelism = 4 }, range =>
                {
                    byte[] raw = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 16), plain = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 16);
                    long records = 0, bytes = 0, bad = 0;
                    try
                    {
                        for (int k = range.Item1; k < range.Item2; k++)
                        {
                            var e = seg[rows[k]];
                            if (e.Size < 4 || e.Offset + 24 + e.Size > length) continue;
                            if (raw.Length < e.Size) { System.Buffers.ArrayPool<byte>.Shared.Return(raw); raw = System.Buffers.ArrayPool<byte>.Shared.Rent((int)e.Size); }
                            if (RandomAccess.Read(handle, raw.AsSpan(0, (int)e.Size), e.Offset + 24) < e.Size) continue;
                            ReadOnlySpan<byte> data;
                            if (e.IsCompressed)
                            {
                                uint size = BitConverter.ToUInt32(raw, 0);
                                if (size > 256u << 20) { bad++; continue; }
                                if (plain.Length < size) { System.Buffers.ArrayPool<byte>.Shared.Return(plain); plain = System.Buffers.ArrayPool<byte>.Shared.Rent((int)size); }
                                try
                                {
                                    using var z = new ZLibStream(new MemoryStream(raw, 4, (int)e.Size - 4, false), CompressionMode.Decompress);
                                    z.ReadExactly(plain, 0, (int)size);
                                }
                                catch (Exception x) when (x is InvalidDataException or EndOfStreamException) { bad++; continue; }
                                data = plain.AsSpan(0, (int)size);
                            }
                            else data = raw.AsSpan(0, (int)e.Size);
                            records++; bytes += data.Length;
                            TypeCount? tc = byType ? scan.ByType.GetOrAdd(seg.SignatureOf(e), _ => new TypeCount()) : null;
                            if (tc != null) { Interlocked.Increment(ref tc.Records); Interlocked.Add(ref tc.Bytes, data.Length); }
                            bool found = false;
                            foreach (var t in MatTexts(data))
                            {
                                found = true;
                                scan.Texts.TryAdd(t, 0);
                                tc?.Texts.TryAdd(t, 0);
                            }
                            if (found && tc != null) Interlocked.Increment(ref tc.WithMat);
                        }
                    }
                    finally { System.Buffers.ArrayPool<byte>.Shared.Return(raw); System.Buffers.ArrayPool<byte>.Shared.Return(plain); }
                    Interlocked.Add(ref scan.Records, records); Interlocked.Add(ref scan.Bytes, bytes); Interlocked.Add(ref scan.NotInflated, bad);
                });
            }
        }
        return scan;
    }

    static bool PathByte(byte b) => b >= 0x20 && b < 0x7F && b is not ((byte)'"' or (byte)'*' or (byte)'<' or (byte)'>' or (byte)'?' or (byte)'|' or (byte)':');

    /// <summary>Every text in the bytes that ends in ".mat" (any letter case, then a byte that isn't a path's), with
    /// the path characters before it. When the text holds "materials\" (or /) after its start, the text from there is
    /// listed too: the byte before a path may be a length that reads as a letter.</summary>
    internal static List<string> MatTexts(ReadOnlySpan<byte> d) => PathTexts(d, "mat", "materials");

    /// <summary>Every text in the bytes that ends in "." and the three letters of <paramref name="ext"/> (any letter
    /// case, then a byte that isn't a path's), with the path characters before it; with <paramref name="root"/>, a text
    /// that holds it (and a slash) after its start is listed from there too. The materials report reads a record's
    /// mesh paths so (S8), one subrecord at a time.</summary>
    internal static List<string> PathTexts(ReadOnlySpan<byte> d, string ext, string? root)
    {
        var list = new List<string>();
        int from = 0;
        while (true)
        {
            int i = d[from..].IndexOf((byte)'.');
            if (i < 0) break;
            int dot = from + i;
            from = dot + 1;
            if (dot + 3 >= d.Length || (d[dot + 1] | 0x20) != ext[0] || (d[dot + 2] | 0x20) != ext[1] || (d[dot + 3] | 0x20) != ext[2]) continue;
            int end = dot + 4;
            if (end < d.Length && (char.IsAsciiLetterOrDigit((char)d[end]) || d[end] == (byte)'_')) continue; // .material, .mat2
            int start = dot;
            while (start > 0 && PathByte(d[start - 1])) start--;
            if (dot - start < 1) continue;
            string text = Encoding.ASCII.GetString(d[start..end]).Trim();
            if (text.Contains("$(", StringComparison.Ordinal)) continue;
            list.Add(text);
            // The byte before a path may be a length that reads as a letter ("Xmaterials\..."): the text from the root
            // is taken too. Both are kept: a folder may end in the root's name ("BaseMaterials\"), and a text that
            // isn't a material's path does no harm (j811: Neon_MetalRust01.mat was lost so)
            int m = root == null ? -1 : text.IndexOf(root, 1, StringComparison.OrdinalIgnoreCase);
            if (m > 0 && m + root!.Length < text.Length && text[m + root.Length] is '\\' or '/' && text[m - 1] is not ('\\' or '/')) list.Add(text[m..]);
        }
        return list;
    }

    /// <summary>The material paths an archive's particle files (.blob) name, read whole through Mutagen's reader.</summary>
    static void Blobs(string archive, MaterialPathEvidence e, Collector c, HashSet<ulong> seen)
    {
        try
        {
            using var reader = Archive.CreateDisposableReader(GameRelease.Starfield, archive);
            foreach (var f in reader.Files.Where(f => f.Path.EndsWith(".blob", StringComparison.OrdinalIgnoreCase)))
            {
                byte[] b;
                try { b = f.GetBytes(); }
                catch (Exception x) when (x is IOException or InvalidDataException) { continue; }
                Blob(b, e, c, seen);
            }
        }
        catch (Exception x) when (x is IOException or InvalidDataException or UnauthorizedAccessException or NotImplementedException or ArgumentException or OverflowException) { }
    }

    static void Blob(byte[] b, MaterialPathEvidence e, Collector c, HashSet<ulong> seen)
    {
        e.Blobs++; e.BlobBytes += b.Length;
        if (!seen.Add(System.IO.Hashing.XxHash3.HashToUInt64(b))) return;
        foreach (var t in MatTexts(b)) c.MatPath(t);
    }

    sealed class MeshCounts
    {
        public int Meshes;
        public long Inflated;
    }

    /// <summary>
    /// The header strings of every mesh in an archive, through Mutagen's reader that keeps the archive open
    /// (Archive.CreateDisposableReader, C199): for each .nif entry only its first few KB are read from the entry's
    /// stream (2 KB, more only while the header goes on), on four threads. An archive Mutagen can't read is passed over.
    /// </summary>
    static void MeshHeaders(string archive, Action<List<string>?> found, MeshCounts counts)
    {
        IDisposableArchiveReader reader;
        List<IArchiveFile> meshes;
        try
        {
            reader = Archive.CreateDisposableReader(GameRelease.Starfield, archive);
            try { meshes = reader.Files.Where(f => f.Path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)).ToList(); }
            catch { reader.Dispose(); throw; }
        }
        catch (Exception x) when (x is IOException or InvalidDataException or UnauthorizedAccessException or NotImplementedException or ArgumentException or OverflowException) { return; }
        using (reader)
        {
            if (meshes.Count == 0) return;
            var local = new ThreadLocal<byte[]>(() => new byte[65536]);
            Parallel.ForEach(Partitioner.Create(0, meshes.Count, 256), new ParallelOptions { MaxDegreeOfParallelism = 4 }, range =>
            {
                var buffer = local.Value!;
                int n = 0; long inflated = 0;
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    List<string>? strings = null;
                    int got = 0;
                    try
                    {
                        using var st = meshes[i].AsStream();
                        // 2 KB first (a header is about 600 bytes), more only while the header goes on
                        for (int want = 2048, r; ; want = Math.Min(want * 4, buffer.Length))
                        {
                            while (got < want && (r = st.Read(buffer, got, want - got)) > 0) got += r;
                            strings = MeshStrings(buffer, got);
                            if (strings != null || got < want || want == buffer.Length) break;
                        }
                    }
                    catch (Exception x) when (x is IOException or InvalidDataException) { }
                    n++; inflated += got;
                    found(strings);
                }
                lock (counts) { counts.Meshes += n; counts.Inflated += inflated; }
            });
            local.Dispose();
        }
    }

    // ---------------------------------------------------------------- The scratch file
    /// <summary>Writes the evidence to the scratch folder (a new file, then moved into place, so a reader never sees
    /// half of one). A failure to write is no error: it is gathered again when needed.</summary>
    static void Save(string path, MaterialPathEvidence e)
    {
        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("# SFSearch material paths, made from file names in Data; delete it at will\t").Append(e.Key).Append('\n');
            sb.Append(string.Join("\t", "#stats", e.MatFiles, e.Meshes, e.MeshesReadWhole, e.ArchivesNamed, e.FoldersTried, e.MeshBytes, e.MeshDiskBytes,
                e.Seconds.ToString("R", inv), e.MeshSeconds.ToString("R", inv), e.MatSeconds.ToString("R", inv), e.GatheredUtc.Ticks,
                e.RecordPlugins, e.Records, e.RecordBytes, e.RecordTexts, e.RecordSeconds.ToString("R", inv), e.Blobs, e.BlobBytes, e.DatabaseNames)).Append('\n');
            foreach (var (k, v) in e.Paths) sb.Append("P\t").Append(k.ToString("x16")).Append('\t').Append(v).Append('\n');
            foreach (var (k, v) in e.Folders) sb.Append("F\t").Append(k.ToString("x8")).Append('\t').Append(v).Append('\n');
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The evidence in a scratch file made for this key, or null (none, another key, damaged).</summary>
    static MaterialPathEvidence? Load(string path, string key, bool olderPlugins)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var r = new StreamReader(path, new UTF8Encoding(false));
            string? first = r.ReadLine();
            int tab = first?.LastIndexOf('\t') ?? -1;
            if (first == null || tab < 0 || !Fits(first[(tab + 1)..], key, olderPlugins)) return null;
            var st = r.ReadLine()?.Split('\t');
            if (st is not { Length: 20 } || st[0] != "#stats") return null;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var e = new MaterialPathEvidence
            {
                Key = first[(tab + 1)..], MatFiles = int.Parse(st[1], inv), Meshes = int.Parse(st[2], inv), MeshesReadWhole = int.Parse(st[3], inv), ArchivesNamed = int.Parse(st[4], inv),
                FoldersTried = int.Parse(st[5], inv), MeshBytes = long.Parse(st[6], inv), MeshDiskBytes = long.Parse(st[7], inv), Seconds = double.Parse(st[8], inv),
                MeshSeconds = double.Parse(st[9], inv), MatSeconds = double.Parse(st[10], inv), GatheredUtc = new DateTime(long.Parse(st[11], inv), DateTimeKind.Utc),
                RecordPlugins = int.Parse(st[12], inv), Records = long.Parse(st[13], inv), RecordBytes = long.Parse(st[14], inv), RecordTexts = int.Parse(st[15], inv),
                RecordSeconds = double.Parse(st[16], inv), Blobs = int.Parse(st[17], inv), BlobBytes = long.Parse(st[18], inv), DatabaseNames = int.Parse(st[19], inv),
            };
            var hex = System.Globalization.NumberStyles.HexNumber;
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                var p = line.Split('\t');
                if (p.Length != 3) return null;
                if (p[0] == "P" && ulong.TryParse(p[1], hex, inv, out ulong pk)) e.Paths[pk] = p[2];
                else if (p[0] == "F" && uint.TryParse(p[1], hex, inv, out uint fk)) e.Folders[fk] = p[2];
                else return null;
            }
            return e;
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or FormatException or OverflowException) { return null; }
    }

    /// <summary>
    /// The strings of a mesh's header (Gamebryo 20.2.0.7, as Starfield writes it): a mesh names its materials as
    /// header strings (a sample of 321 of the game's meshes: every .mat text is one; the header ends after 581 bytes
    /// at the median, 7,400 at most). The header: the version line; version, endianness, user version and block
    /// count; the Bethesda version, an author string, a number, an export string and one more short string; the block
    /// types; a type and a size for each block; then the strings. Null when the bytes end before the strings do or
    /// aren't such a header.
    /// </summary>
    internal static List<string>? MeshStrings(byte[] b, int length) => MeshHeader.Read(b.AsSpan(0, length))?.Strings;

    /// <summary>A material's path from the evidence: whole, or from its name (when it is its file's) and a folder.</summary>
    internal static string? PathOf(MaterialContents.Node n, MaterialPathEvidence e)
    {
        if (!n.HasMaterialId) return null;
        var (dir, file, _) = n.Id;
        if (e.Paths.TryGetValue(((ulong)dir << 32) | file, out var whole)) return whole;
        if (n.OwnName is not { Length: > 0 } name || MaterialDatabase.ResourceId(name + ".mat").File != file) return null;
        return e.Folders.TryGetValue(dir, out var folder) ? $@"{folder}\{name.ToLowerInvariant()}.mat" : null;
    }
}
