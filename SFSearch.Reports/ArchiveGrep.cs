// archive grep: which file holds this text? The contents of the files inside BA2 archives, and of the loose files in
// Data the game reads in their place, searched for a text or a regular expression. A modder asks it to find which
// mod's file names a material, a texture, a mesh, a sound or a script string: a path a record doesn't hold, written
// inside a mesh, a material database, a particle file, an interface file or a script.
//
// The cost is said before anything is read: the scope's files and their inflated bytes come from the archives' name
// tables and file records alone (about 2 s for all 457 archives of a Data folder), and from them an estimate of the
// time. A scope over the heavy limits is answered "Not run" with that estimate by the caller.
//
// The speed order (measured first, notes\fix-j18-archive-grep.md):
//  - only the files the scope needs: textures, sounds and video are passed over by extension unless named, and an
//    archive none of whose names fits the scope is passed over by its name table (no file record is read);
//  - the stretches of each archive that hold the scope's files are read ahead from disk, in the archive's order,
//    neighbours merged, while its files are inflated: a cold archive read file by file came at 55 to 355 MB/s, in one
//    pass at about 4 GB/s. Where a file's bytes are, how many there are on disk and whether they are compressed is
//    Mutagen's IArchiveFile.GetStorage() (fixes.18, C42; GAPS S68): no archive is read whole for a few of its files;
//  - the files are inflated and searched on all cores, in their order, a batch at a time;
//  - the search stops once the page is full; the cursor goes on from the next file (a count reads the whole scope);
//  - a regular expression is compiled, and its own speed is timed on a sample of the scope's bytes before the
//    estimate is made (a 21-way alternation ran at 41 MB/s a thread interpreted, 565 MB/s compiled: J48).
// A file is read in 4 MB pieces. A piece carries the 64 KB before it as context, already searched, so that ^, \b
// and a look-behind see the bytes before the cut and \A is only the file's first byte; a regular expression goes on
// from the end of its last match across the cut, so each match is found once, as in a search of the whole file (J46).
// A text file that starts with a UTF-16 byte-order mark is read as UTF-16 text (J39). For a text with a character
// outside ASCII, an 8-bit file is read as text: UTF-8, a byte that isn't UTF-8 as Windows-1252, so a letter outside
// ASCII matches in either case and either encoding; offsets stay bytes of the file (J55).
// Nothing is kept between calls: no index, no copy.
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace SFSearch.Reports;

using static ReportAnswer;

/// <summary>An answer that is paged by the items it searched, not by its rows: a search that stops once its page is
/// full. The caller turns these into the [more] line and its cursor.</summary>
public interface ISearchPage
{
    /// <summary>The first item searched on this page, counting from 0.</summary>
    long FirstItem { get; }
    /// <summary>How many items this page searched.</summary>
    long ItemsSearched { get; }
    /// <summary>How many items the whole search covers.</summary>
    long ItemsTotal { get; }
    /// <summary>The item the next page starts at, or null when this page ends the search.</summary>
    long? NextItem { get; }
    /// <summary>What an item is called in the [more] line: "files searched".</summary>
    string ItemNoun { get; }
}

/// <summary>What <c>archive2 grep</c> looks for, and where.</summary>
public sealed class GrepOptions
{
    /// <summary>The text (letter case doesn't count), or a .NET regular expression with <see cref="Regex"/>.</summary>
    public required string Text { get; init; }
    public bool Regex { get; init; }
    /// <summary>Only files with these extensions ("mat", ".nif"); "*" for every kind, textures, sounds and video too.
    /// Empty: every kind but textures, sounds and video.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = Array.Empty<string>();
    /// <summary>Only files under these folders ("materials", "meshes\clothes").</summary>
    public IReadOnlyList<string> Folders { get; init; } = Array.Empty<string>();
    /// <summary>For a plugin file outside Data: the archives beside it.</summary>
    public bool Beside { get; init; }
    /// <summary>The file to start at: the cursor of a page that stopped early.</summary>
    public int Start { get; init; }
    /// <summary>Count the hits and files per kind (and per archive) over the whole scope, instead of listing the hits.</summary>
    public bool Count { get; init; }
}

/// <summary>
/// The files a search covers and what reading them will cost, worked out before any file is read. It holds the
/// archives open (one handle each) until it is disposed.
/// </summary>
public sealed class GrepPlan : IDisposable
{
    internal sealed class Source
    {
        public required string Name { get; init; }
        public required string Path { get; init; }
        public long Length { get; init; }
        public IDisposableArchiveReader? Reader { get; set; }
        /// <summary>The inflated bytes of every file in the archive, and of those in the scope.</summary>
        public long Inflated, InScope;
        public int Files;
        /// <summary>Read ahead from disk: the stretches its files in the scope take (<see cref="Stretches"/>), or the
        /// whole archive when Mutagen didn't say where its files are.</summary>
        public bool ReadAhead;
        /// <summary>Where the scope's files are stored in the archive (their chunks, from Mutagen's GetStorage()), in the
        /// archive's order, neighbours less than <see cref="ArchiveGrep.MergeGap"/> apart merged. Null when a file didn't
        /// say: the archive is then read ahead whole when the scope is most of it.</summary>
        public List<(long Offset, long Length)>? Stretches = new();
        /// <summary>The bytes the read-ahead reads of it: its stretches, or the whole archive.</summary>
        public long AheadBytes => Stretches != null ? Stretches.Sum(x => x.Length) : Length;
    }

    internal readonly struct Item
    {
        /// <summary>Index into <see cref="Sources"/>, or -1 for a loose file.</summary>
        public int Source { get; init; }
        public required string Path { get; init; }
        public IArchiveFile? File { get; init; }
        public long Size { get; init; }
        /// <summary>The bytes the work reads of it, for the estimate: grep its size, the encodings its first 64 KB of a binary kind.</summary>
        public long Reads { get; init; }
        /// <summary>Its bytes on disk that the work reads (an archived file: its stored chunks, from Mutagen's GetStorage()).</summary>
        public long Stored { get; init; }
        /// <summary>Its data is compressed (zlib or LZ4) and has to be inflated.</summary>
        public bool Compressed { get; init; }
        public bool LooseCopy { get; init; }
    }

    internal List<Source> Sources { get; } = new();
    internal List<Item> Items { get; } = new();
    internal ArchiveTarget Target { get; init; } = null!;
    internal string DataFolder { get; init; } = "";
    /// <summary>Every archive in Data was asked for: every loose file in Data's folders is in the scope too.</summary>
    internal bool Everything { get; init; }
    internal void Skip(string extension) { SkippedFiles++; SkippedByKind[extension] = SkippedByKind.GetValueOrDefault(extension) + 1; }
    public GrepOptions Options { get; init; } = null!;
    internal HashSet<string>? Kinds { get; init; }
    internal bool AllKinds { get; init; }
    internal string[] Folders { get; init; } = Array.Empty<string>();
    /// <summary>The text or regular expression, made once (a regular expression is compiled). None for the encodings.</summary>
    internal ArchiveGrep.Matcher Matcher { get; init; } = null!;
    /// <summary>What reads the files: "grep" or "encoding".</summary>
    internal string Verb { get; init; } = "grep";

    /// <summary>The files in the scope (loose and archived) from <see cref="GrepOptions.Start"/> on, and their inflated bytes.</summary>
    public long Files => Math.Max(0, Items.Count - Start);
    public long Bytes { get; internal set; }
    public int Start { get; internal set; }
    /// <summary>Archives with a file in the scope; archives passed over by their names alone.</summary>
    public int Archives => Sources.Count(s => s.Files > 0);
    public int ArchivesPassedOver { get; internal set; }
    public int LooseFiles { get; internal set; }
    /// <summary>Files passed over as textures, sounds or video, by extension.</summary>
    public long SkippedFiles { get; internal set; }
    public Dictionary<string, long> SkippedByKind { get; } = new(StringComparer.Ordinal);
    /// <summary>The time the archives' names and file records took.</summary>
    public double ListSeconds { get; internal set; }
    /// <summary>The estimate, made before any file is read.</summary>
    public double Seconds { get; internal set; }
    public long MemoryMB { get; internal set; }
    /// <summary>The bytes read ahead from disk: the stretches of the archives that hold the scope's files.</summary>
    public long ReadAheadBytes { get; internal set; }
    internal List<string> Notes { get; } = new();
    /// <summary>"all": the files at Data's own top level left out (not plugins or archives), by extension.</summary>
    public long TopLevelLeftOut { get; internal set; }
    public Dictionary<string, long> TopLevelByKind { get; } = new(StringComparer.Ordinal);
    /// <summary>A regular expression's own speed, timed before the estimate on a sample of the scope's bytes: the bytes
    /// and the seconds of one thread.</summary>
    public long PatternBytes { get; internal set; }
    public double PatternSeconds { get; internal set; }
    /// <summary>The sample's speed in MB a second on one thread, or 0 when none was timed (an ASCII plain text).</summary>
    public double PatternMBps => PatternSeconds > 0 ? PatternBytes / 1048576.0 / PatternSeconds : 0;

    /// <summary>One line for the heavy gate and the answer: what the search covers.</summary>
    public string What => $"archive2 {Verb} over {Plural(Files, "file")} ({ArchiveStore.SizeText(Bytes)} {(Verb == "grep" ? "inflated" : "to read")}) in {Plural(Archives, "archive")}" +
        (LooseFiles > 0 ? $" and {Plural(LooseFiles, "loose file")}" : "") + (Start > 0 ? $", from file {N(Start + 1)} of {N(Items.Count)}" : "");

    public void Dispose()
    {
        foreach (var s in Sources) { s.Reader?.Dispose(); s.Reader = null; }
    }
}

/// <summary>One place a text was found.</summary>
public sealed class GrepHit
{
    /// <summary>The archive's name, or "loose".</summary>
    public required string Where { get; init; }
    public required string Path { get; init; }
    /// <summary>The byte the hit starts at in the file (in an inflated .swf, as the uncompressed file has it).</summary>
    public long Offset { get; init; }
    /// <summary>The line it is on, for a text file.</summary>
    public long? Line { get; init; }
    /// <summary>What matched, as written in the file.</summary>
    public required string Match { get; init; }
    /// <summary>The text around it: the run of printable characters it sits in, up to 60 on each side.</summary>
    public required string Around { get; init; }
    /// <summary>An archived file that a loose file in Data stands in for: the game uses the loose one.</summary>
    public bool LooseCopy { get; init; }
    /// <summary>A compressed .swf, searched inflated.</summary>
    public bool Inflated { get; init; }
    /// <summary>A text file with a UTF-16 byte-order mark, searched as UTF-16 text (the offset is still in bytes of the file).</summary>
    public bool Utf16 { get; init; }
}

/// <summary>The hits of one file, in the order they are in it.</summary>
public sealed class GrepFile
{
    public required string Where { get; init; }
    public required string Path { get; init; }
    public long Size { get; init; }
    public bool LooseCopy { get; init; }
    /// <summary>Every hit in the file; <see cref="Hits"/> keeps the first <see cref="ArchiveGrep.KeptHits"/>.</summary>
    public long Count { get; set; }
    public List<GrepHit> Hits { get; } = new();
}

public sealed class GrepAnswer : ReportAnswer, ISearchPage
{
    public override string Kind => "archive";
    public required string Target { get; init; }
    public required string Text { get; init; }
    public bool Regex { get; init; }
    public string Scope { get; init; } = "";
    /// <summary>Every file in the scope, and their inflated bytes.</summary>
    public long ScopeFiles { get; init; }
    public long ScopeBytes { get; init; }
    public int Archives { get; init; }
    public int LooseFiles { get; init; }
    public long FirstItem { get; init; }
    public long ItemsSearched { get; set; }
    public long BytesSearched { get; set; }
    public long ItemsTotal => ScopeFiles;
    public long? NextItem { get; set; }
    public string ItemNoun => "files searched";
    /// <summary>The estimate made before reading: the files and bytes from <see cref="FirstItem"/> on, and the time.</summary>
    public long EstimateFiles { get; init; }
    public long EstimateBytes { get; init; }
    public double EstimateSeconds { get; init; }
    /// <summary>A regular expression's speed on the sample timed before the estimate (MB a second, one thread), and the sample's bytes.</summary>
    public double PatternMBps { get; init; }
    public long PatternSampleBytes { get; init; }
    /// <summary>The time the search took, from the first file read to the last (the lists not counted).</summary>
    public double SearchSeconds { get; set; }
    public List<GrepFile> Files { get; } = new();
    public long Hits => Files.Sum(f => f.Count);

    protected override void Fill(JsonObject json)
    {
        json["mode"] = "grep";
        json["target"] = Target;
        json["text"] = Text;
        json["regex"] = Regex;
        if (Scope.Length > 0) json["scope"] = Scope;
        json["filesInScope"] = ScopeFiles;
        json["bytesInScope"] = ScopeBytes;
        json["archives"] = Archives;
        json["looseFiles"] = LooseFiles;
        json["searchedFrom"] = FirstItem + 1;
        json["searchedTo"] = FirstItem + ItemsSearched;
        json["bytesSearched"] = BytesSearched;
        if (NextItem != null) json["nextFile"] = NextItem + 1;
        json["estimate"] = ArchiveGrep.EstimateJson(EstimateFiles, EstimateBytes, EstimateSeconds, PatternMBps, PatternSampleBytes);
        json["searchSeconds"] = Math.Round(SearchSeconds, 2);
        json["hits"] = Hits;
        json["files"] = new JsonArray(Files.Select(f =>
        {
            var o = new JsonObject { ["where"] = f.Where, ["file"] = f.Path, ["size"] = f.Size, ["hits"] = f.Count };
            if (f.LooseCopy) o["looseCopyInData"] = true;
            o["found"] = new JsonArray(f.Hits.Select(h =>
            {
                var x = new JsonObject { ["offset"] = h.Offset };
                if (h.Line != null) x["line"] = h.Line;
                x["match"] = h.Match; x["around"] = h.Around;
                if (h.Inflated) x["inflated"] = true;
                if (h.Utf16) x["utf16"] = true;
                return (JsonNode)x;
            }).ToArray());
            return (JsonNode)o;
        }).ToArray());
    }

    protected override void Render(ReportBody body, ReportTextOptions o)
    {
        body.RowNoun = "hits";
        string scope = $"{Plural(ScopeFiles, "file")}, {ArchiveStore.SizeText(ScopeBytes)} inflated, in {Plural(Archives, "archive")}" + (LooseFiles > 0 ? $" and {Plural(LooseFiles, "loose file")}" : "");
        string what = Regex ? $"the regular expression {Text}" : $"\"{Text}\"";
        long files = Files.Count;
        bool all = FirstItem == 0 && NextItem == null;
        body.Head.Add(all
            ? $"{what} in the files of {Target}{(Scope.Length > 0 ? " (" + Scope + ")" : "")} ({scope}): {Plural(Hits, "hit")} in {Plural(files, "file")}."
            : $"{what} in the files of {Target}{(Scope.Length > 0 ? " (" + Scope + ")" : "")} ({scope}): files {N(FirstItem + 1)} to {N(FirstItem + ItemsSearched)} searched{(NextItem != null ? ", then the page was full" : "")}: {Plural(Hits, "hit")} in {Plural(files, "file")}.");
        body.Head.Add(ArchiveGrep.EstimateLine(EstimateFiles, EstimateBytes, EstimateSeconds, PatternMBps, PatternSampleBytes, Regex) + $" The search took {Time(SearchSeconds)} for {Plural(ItemsSearched, "file")}, {ArchiveStore.SizeText(BytesSearched)}.");
        if (Files.Count == 0) return;
        body.Head.Add("| Where | File | Offset | Text around it |");
        body.Head.Add("| :--- | :--- | ---: | :--- |");
        int show = o.Detail ? int.MaxValue : 3;
        foreach (var f in Files)
        {
            string where = f.Where + (f.LooseCopy ? " (the game uses the loose file)" : "");
            var rows = new StringBuilder();
            foreach (var h in f.Hits.Take(show))
            {
                if (rows.Length > 0) rows.Append('\n');
                rows.Append($"| {Cell(where)} | {Cell(f.Path)} | {N(h.Offset)}{(h.Line != null ? $" (line {N(h.Line.Value)})" : "")}{(h.Inflated ? " inflated" : "")}{(h.Utf16 ? " UTF-16" : "")} | {Cell(h.Around)} |");
            }
            long more = f.Count - Math.Min(show, f.Hits.Count);
            if (more > 0) rows.Append($"\n| {Cell(where)} | {Cell(f.Path)} | | {N(more)} more hit{(more == 1 ? "" : "s")} in this file{(o.Detail ? $" (only the first {N(ArchiveGrep.KeptHits)} are kept)" : "; --detail lists them")} |");
            body.Rows.Add(rows.ToString());
        }
    }

    internal static string Time(double s) => s < 100 ? s.ToString(s < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " s" : (s / 60).ToString("0.0", CultureInfo.InvariantCulture) + " min";
}

/// <summary>The search itself: <see cref="ArchiveReport.PlanGrep"/>, then <see cref="ArchiveReport.Grep"/>.</summary>
public static partial class ArchiveGrep
{
    /// <summary>Files passed over unless their extension is named: textures and images, sounds, video.</summary>
    public static readonly IReadOnlyDictionary<string, string> SkippedKinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".dds"] = "texture", [".png"] = "texture", [".tga"] = "texture", [".jpg"] = "texture", [".jpeg"] = "texture", [".bmp"] = "texture",
        [".wem"] = "sound", [".bnk"] = "sound", [".wav"] = "sound", [".xwm"] = "sound", [".fuz"] = "sound", [".ogg"] = "sound", [".mp3"] = "sound",
        [".bk2"] = "video", [".bik"] = "video", [".mp4"] = "video", [".webm"] = "video",
    };

    /// <summary>Files whose hits get a line number.</summary>
    static readonly HashSet<string> TextKinds = new(StringComparer.OrdinalIgnoreCase)
        { ".mat", ".json", ".txt", ".psc", ".xml", ".ini", ".csv", ".cfg", ".lst", ".html", ".css", ".js", ".bat", ".log", ".md", ".yaml", ".yml", ".toml", ".xtbl" };

    /// <summary>Hits kept per file; the rest are counted.</summary>
    public const int KeptHits = 1000;
    /// <summary>The bytes searched at a time, and the bytes kept from one piece to the next so that a hit across the
    /// cut is found: a regular expression's hit is at most this long.</summary>
    internal const int Piece = 4 << 20, Overlap = 64 << 10;
    /// <summary>A batch: the files inflated side by side before the page is looked at.</summary>
    const long BatchBytes = 512L << 20;
    const int BatchFiles = 8192;
    /// <summary>How far the read-ahead may run before the files being searched.</summary>
    const long AheadBytes = 6L << 30;
    /// <summary>Two stretches of an archive less than this apart are read ahead as one: reading the gap costs less than
    /// another seek.</summary>
    internal const long MergeGap = 1L << 20;

    // The estimate's rates, measured on an 8-core, 16-thread CPU with an NVMe disk (notes\fix-j18-archive-grep.md),
    // in seconds of the whole machine: a GB inflated, a GB stored, a file besides its bytes; a GB read ahead in one
    // pass, a GB read file by file from a cold disk; a stretch read ahead besides its bytes (a seek). The disk and the
    // processors work side by side: the larger counts.
    const double ReadAheadPerGB = 0.28, ColdPerGB = 3.0, CompressedPerGB = 0.8, StoredPerGB = 0.45, PerFile = 8e-6, Fixed = 0.2, PerStretch = 1e-4;

    internal static string Ext(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant();

    /// <summary>Stretches of an archive in its order, neighbours less than <see cref="MergeGap"/> apart (or overlapping)
    /// made one.</summary>
    internal static List<(long Offset, long Length)> Merge(List<(long Offset, long Length)> stretches)
    {
        var merged = new List<(long Offset, long Length)>();
        foreach (var (offset, length) in stretches.Where(x => x.Length > 0).OrderBy(x => x.Offset))
        {
            if (merged.Count > 0 && offset - (merged[^1].Offset + merged[^1].Length) < MergeGap)
            {
                var last = merged[^1];
                merged[^1] = (last.Offset, Math.Max(last.Offset + last.Length, offset + length) - last.Offset);
            }
            else merged.Add((offset, length));
        }
        return merged;
    }

    internal static (HashSet<string>? Kinds, bool All) Kinds(IReadOnlyList<string> extensions)
    {
        if (extensions.Count == 0) return (null, false);
        if (extensions.Any(e => e.Trim() is "*" or "*.*")) return (null, true);
        return (extensions.Select(e => e.Trim().TrimStart('*')).Where(e => e.Length > 0).Select(e => (e.StartsWith('.') ? e : "." + e).ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase), false);
    }

    internal static string[] FolderPrefixes(IReadOnlyList<string> folders) =>
        folders.Select(f => ArchiveStore.DataRelative(f).TrimEnd('\\', '*')).Where(f => f.Length > 0).Select(f => f + "\\").ToArray();

    /// <summary>Whether a path is in the scope, and if not because of its kind, which skipped kind.</summary>
    internal static bool InScope(GrepPlan plan, string path, out string? skippedKind)
    {
        skippedKind = null;
        if (plan.Folders.Length > 0 && !plan.Folders.Any(f => path.StartsWith(f, StringComparison.OrdinalIgnoreCase))) return false;
        string ext = Ext(path);
        if (plan.Kinds != null) return plan.Kinds.Contains(ext);
        if (!plan.AllKinds && SkippedKinds.TryGetValue(ext, out var kind)) { skippedKind = ext; _ = kind; return false; }
        return true;
    }

    /// <summary>A regular expression's own cost, per thread, is timed on this many files of the scope (evenly spread by
    /// their bytes), the first MB of each, for at most a quarter of a second.</summary>
    const int SampleFiles = 8, SampleBytes = 1 << 20, SampleChunk = 64 << 10;
    const double SampleMaxSeconds = 0.25;
    /// <summary>How many threads' worth of a regular expression's time run side by side: the threads search while others
    /// inflate, on 8 cores with 16 threads (measured on Starfield - Meshes01.ba2, notes\fix-j45-grep-census.md).</summary>
    static double PatternThreads => Math.Max(1, Environment.ProcessorCount * 0.75);

    /// <summary>The estimate: the larger of the disk's time and the processors' time, for a cold disk; a regular
    /// expression's own time (timed on a sample: <see cref="SamplePattern"/>) is added to the processors'.</summary>
    internal static void Estimate(GrepPlan plan)
    {
        double disk = 0, cpu = 0;
        long bytes = 0;
        var started = new HashSet<int>();
        for (int i = plan.Start; i < plan.Items.Count; i++)
        {
            var it = plan.Items[i];
            long size = it.Reads;
            bytes += size;
            if (it.Source < 0) { disk += size / 1e9 * ColdPerGB / 4; cpu += size / 1e9 * StoredPerGB; continue; }
            var s = plan.Sources[it.Source];
            // The disk: the file's stored bytes read cold, or its archive's stretches read ahead once; the processors:
            // its bytes inflated, or only copied when stored as they are (Mutagen's GetStorage() says which, S68)
            if (!s.ReadAhead) disk += it.Stored / 1e9 * ColdPerGB;
            else if (started.Add(it.Source)) disk += s.AheadBytes / 1e9 * ReadAheadPerGB + (s.Stretches?.Count ?? 1) * PerStretch;
            cpu += size / 1e9 * (it.Compressed ? CompressedPerGB : StoredPerGB);
        }
        cpu += plan.Files * PerFile;
        if (plan.PatternSeconds > 0 && plan.PatternBytes > 0) cpu += bytes * (plan.PatternSeconds / plan.PatternBytes) / PatternThreads;
        plan.Bytes = bytes;
        plan.ReadAheadBytes = plan.Sources.Where((s, k) => started.Contains(k)).Sum(s => s.AheadBytes);
        plan.Seconds = Fixed + Math.Max(disk, cpu);
        // Each thread holds a piece of bytes and of characters; the archives' file records and the paths about 900 bytes a file listed (1.68 GB at most for all of Data, measured), the process itself about 200 MB
        plan.MemoryMB = (long)(Environment.ProcessorCount * (Piece + 2L * Overlap) * 3L / 1048576 + plan.Items.Count * 900L / 1048576 + 200);
    }

    /// <summary>
    /// A regular expression's speed on the scope's own bytes, before the estimate: the speed of a pattern depends on it
    /// and on the bytes far more than a plain text's does (J48: from 41 MB/s to 15 GB/s a thread, measured). The first MB
    /// of up to 8 files spread evenly over the scope's bytes, at most a quarter of a second of matching. A plain text
    /// outside ASCII is timed too, with the reading of the files as text it needs (J55); an ASCII text's speed is in the rates.
    /// </summary>
    internal static void SamplePattern(GrepPlan plan)
    {
        if (plan.Matcher == null) return;
        // Read as text only when the pattern isn't matched on the bytes (J71)
        bool asText = plan.Matcher.DecodesBytes && !plan.Matcher.OnBytes;
        if (!plan.Options.Regex && !plan.Matcher.DecodesBytes || plan.Start >= plan.Items.Count) return;
        long total = 0;
        for (int i = plan.Start; i < plan.Items.Count; i++) total += plan.Items[i].Size;
        if (total == 0) return;
        var picks = new List<int>();
        double step = (double)total / SampleFiles, next = step / 2;
        long acc = 0;
        for (int i = plan.Start; i < plan.Items.Count && picks.Count < SampleFiles; i++)
        {
            acc += plan.Items[i].Size;
            if (plan.Items[i].Size == 0 || acc <= next) continue;
            picks.Add(i);
            while (next < acc) next += step;
        }
        var bytes = new byte[SampleBytes];
        var chars = new char[2 * SampleBytes];
        var map = new TextMap();
        // The first use compiles the expression's code: not part of its speed
        plan.Matcher.Find("compile".AsSpan(), 0, 7, (_, _) => { });
        if (plan.Matcher.OnBytes) plan.Matcher.FindBytes("compile"u8, "compile".AsSpan(), 0, 7, (_, _) => { });
        var clock = new Stopwatch();
        long searched = 0;
        foreach (int i in picks)
        {
            int n;
            try
            {
                using var stream = Open(plan, plan.Items[i]);
                n = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotImplementedException or ArgumentException or OverflowException or ObjectDisposedException) { continue; }
            Encoding.Latin1.GetChars(bytes.AsSpan(0, n), chars);
            // Read as text only where the search would: the sampled MB holds the text's ASCII anchor (a piece is 4 MB)
            clock.Start();
            bool decode = asText && plan.Matcher.MayHold(new ReadOnlySpan<char>(chars, 0, n));
            clock.Stop();
            for (int at = 0; at < n && clock.Elapsed.TotalSeconds < SampleMaxSeconds; at += SampleChunk)
            {
                int len = Math.Min(SampleChunk, n - at);
                clock.Start();
                if (decode)
                {
                    int cLen = ReadText(bytes.AsSpan(at, len), chars.AsSpan(n), map);
                    plan.Matcher.Find(chars.AsSpan(n, cLen), 0, cLen, (_, _) => { });
                }
                else if (plan.Matcher.OnBytes) plan.Matcher.FindBytes(bytes.AsSpan(at, len), new ReadOnlySpan<char>(chars, at, len), 0, len, (_, _) => { });
                else if (!asText) plan.Matcher.Find(new ReadOnlySpan<char>(chars, at, len), 0, len, (_, _) => { });
                clock.Stop();
                searched += len;
            }
            if (clock.Elapsed.TotalSeconds >= SampleMaxSeconds) break;
        }
        plan.PatternBytes = searched;
        plan.PatternSeconds = clock.Elapsed.TotalSeconds;
    }

    /// <summary>The estimate as one line of the answer.</summary>
    internal static string EstimateLine(long files, long bytes, double seconds, double patternMBps, long sampleBytes, bool regex) =>
        $"Estimated before reading: {ReportAnswer.Plural(files, "file")}, {ArchiveStore.SizeText(bytes)}, about {GrepAnswer.Time(seconds)}" +
        (patternMBps > 0 ? $" (the {(regex ? "regular expression" : "text")} searched {MBps(patternMBps)} a thread on a {ArchiveStore.SizeText(sampleBytes)} sample of these files)." : ".");

    internal static JsonObject EstimateJson(long files, long bytes, double seconds, double patternMBps, long sampleBytes)
    {
        var o = new JsonObject { ["files"] = files, ["bytes"] = bytes, ["seconds"] = Math.Round(seconds, 1) };
        if (patternMBps > 0) { o["patternMBps"] = Math.Round(patternMBps); o["patternSampleBytes"] = sampleBytes; }
        return o;
    }

    static string MBps(double mbps) => mbps >= 1024 ? (mbps / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " GB/s" : mbps.ToString(mbps < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " MB/s";

    /// <summary>
    /// A text or a regular expression, matched over characters: an 8-bit file's bytes one to one (Latin-1), an 8-bit
    /// file read as text (<see cref="DecodesBytes"/>), or the text of a UTF-16 file. A regular expression is compiled (a
    /// 21-way alternation: 41 MB/s a thread interpreted, 565 MB/s compiled, on the game's meshes); ^ and $ are a line's
    /// start and end. Letter case doesn't count (.NET's ordinal rule for a text, the invariant one for a regex).
    /// </summary>
    internal sealed partial class Matcher
    {
        readonly SearchValues<string>? _text;
        /// <summary>The spellings of the text, the longest first: a \ or / in it as \, \\ or /.</summary>
        readonly string[] _spellings = Array.Empty<string>();
        readonly Regex? _regex;

        /// <summary>
        /// The text holds a character outside ASCII, so an 8-bit file is matched as text: read as UTF-8, a byte that isn't
        /// UTF-8 as Windows-1252 (<see cref="ReadText"/>). A letter outside ASCII then matches in either case and in
        /// either encoding (J55). An ASCII text is matched on the bytes as before: a plain text finds the same there, as
        /// an ASCII byte is always its own character and no other byte reads as one, without the cost of reading.
        /// </summary>
        public bool DecodesBytes { get; }

        /// <summary>
        /// For a text or regex outside ASCII: a run of ASCII letters and digits (two or more) that every hit holds as the
        /// same bytes (<see cref="Anchor"/>). A piece whose bytes don't hold it can't hold a hit, so it isn't read as
        /// text: in binary files that is nearly every piece (J55, measured: notes\fix-j55-grep-case.md).
        /// </summary>
        readonly SearchValues<string>? _anchor;

        /// <summary>
        /// The regular expression's characters written as a code (\xE9) couldn't be made exact, so each still matches
        /// the other case of its letter too (<see cref="ExactCodes"/>; never seen: what it makes is always read).
        /// </summary>
        public bool CodesFold { get; }

        public Matcher(string text, bool regex)
        {
            DecodesBytes = !Ascii.IsValid(text);
            if (regex)
            {
                static Regex Make(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Compiled, TimeSpan.FromSeconds(10));
                ReportException Refused(ArgumentException e) => new($"\"{text}\" isn't a regular expression .NET can read ({e.Message.TrimEnd('.')})", "fix the expression, or leave --regex out to look for the text as it is");
                string exact = ExactCodes(text);
                try { _regex = Make(exact); }
                catch (ArgumentException e)
                {
                    if (ReferenceEquals(exact, text)) throw Refused(e);
                    // What the codes were made into isn't read, the pattern as given may be: it is used, its codes folding (said)
                    try { _regex = Make(text); CodesFold = true; }
                    catch (ArgumentException e2) { throw Refused(e2); }
                }
            }
            else
            {
                _spellings = Spellings(text);
                _text = SearchValues.Create(_spellings, StringComparison.OrdinalIgnoreCase);
            }
            if (DecodesBytes && Anchor(text, regex) is { Length: >= 2 } anchor)
                _anchor = SearchValues.Create(new[] { anchor }, StringComparison.OrdinalIgnoreCase);
            // J71: matched on an 8-bit file's bytes, not read as text, when the pattern can be made into one over them
            if (DecodesBytes && !CodesFold) MakeBytes(text, regex);
        }

        /// <summary>Whether bytes (as Latin-1 characters) may hold a hit of a text or regex outside ASCII: they hold its
        /// ASCII anchor. Always true without one.</summary>
        public bool MayHold(ReadOnlySpan<char> bytes) => _anchor == null || bytes.IndexOfAny(_anchor) >= 0;

        /// <summary>
        /// The longest run of ASCII letters and digits that every hit holds, or "". A plain text: its own runs (a
        /// separator \ or / is spelled three ways, so it ends a run). A regex: its runs of plain letters outside any group,
        /// class or escape, when it has no | outside a group; a letter a quantifier follows is left out (it may be
        /// missing or repeated), and k is left out (the regex's letter case lets it match the Kelvin sign, U+212A: the
        /// only character outside ASCII that matches an ASCII letter, measured over every character). An ASCII letter of
        /// a hit is always the same byte in the file, and no other byte is read as one, so the bytes hold the run.
        /// Anything not understood gives "" (every piece read).
        /// </summary>
        internal static string Anchor(string text, bool regex)
        {
            if (!regex) return System.Text.RegularExpressions.Regex.Matches(text, "[A-Za-z0-9]+").Select(m => m.Value).MaxBy(s => s.Length) ?? "";
            // Whitespace mode (?x) makes # a comment: its letters aren't the pattern's
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\(\?[A-Za-z-]*x")) return "";
            var runs = new List<string>();
            var run = new StringBuilder();
            void End() { if (run.Length > 0) runs.Add(run.ToString()); run.Clear(); }
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\') { End(); i = SkipEscape(text, i); if (i < 0) return ""; continue; }
                if (c == '[') { End(); i = SkipClass(text, i); if (i < 0) return ""; continue; }
                if (c == '(')
                {
                    End();
                    if (string.CompareOrdinal(text, i, "(?#", 0, 3) == 0) { i = text.IndexOf(')', i); if (i < 0) return ""; continue; }
                    depth++;
                    continue;
                }
                if (c == ')') { End(); if (--depth < 0) return ""; continue; }
                if (depth > 0) continue;
                if (c == '|') return "";
                if (c is '*' or '?' or '{')
                {
                    if (run.Length > 0) run.Length--;
                    End();
                    // A count {2}, {2,} or {2,5}: its digits aren't letters of the text
                    if (c == '{' && System.Text.RegularExpressions.Regex.Match(text[i..], @"^\{\d+(,\d*)?\}") is { Success: true } q) i += q.Length - 1;
                    continue;
                }
                if (c == '+') { End(); continue; }
                if (char.IsAsciiLetterOrDigit(c) && c is not ('k' or 'K')) run.Append(c);
                else End();
            }
            End();
            return depth == 0 ? runs.MaxBy(s => s.Length) ?? "" : "";
        }

        /// <summary>The index of an escape's last character (\x41, A, \cA, \p{Lu}, \k&lt;name&gt;, \12, \n), or -1.</summary>
        static int SkipEscape(string p, int i)
        {
            if (i + 1 >= p.Length) return -1;
            char e = p[i + 1];
            int end = e switch
            {
                'x' => i + 3,
                'u' => i + 5,
                'c' => i + 2,
                'p' or 'P' => p.IndexOf('}', i + 2),
                'k' when i + 2 < p.Length && p[i + 2] is '<' or '\'' => p.IndexOf(p[i + 2] == '<' ? '>' : '\'', i + 3),
                _ when char.IsAsciiDigit(e) => Digits(p, i + 1),
                _ => i + 1,
            };
            return end >= 0 && end < p.Length ? end : -1;
            static int Digits(string p, int j) { while (j + 1 < p.Length && char.IsAsciiDigit(p[j + 1])) j++; return j; }
        }

        /// <summary>The index of a character class's closing ], a subtraction ([a-z-[aeiou]]) inside it too, or -1.</summary>
        static int SkipClass(string p, int i)
        {
            int j = i + 1;
            if (j < p.Length && p[j] == '^') j++;
            if (j < p.Length && p[j] == ']') j++;
            for (; j < p.Length; j++)
            {
                if (p[j] == '\\') { j = SkipEscape(p, j); if (j < 0) return -1; }
                else if (p[j] == '-' && j + 1 < p.Length && p[j + 1] == '[') { j = SkipClass(p, j + 1); if (j < 0) return -1; }
                else if (p[j] == ']') return j;
            }
            return -1;
        }

        /// <summary>
        /// A character written as a code matches only itself (J69, GAPS S93). Letter case doesn't count in a search, and
        /// .NET applies that to a code as to a letter: \xC3 also matches E3 (its case in Latin-1, as an 8-bit file's bytes
        /// are read), \x41 also a; so gr\xC3\xB6 found gr + E3 B6, a CJK character. Someone who writes a byte as a code
        /// means that byte. So each code of a letter (\xNN, \uNNNN, and in a class \NNN in octal, measured: a code of
        /// anything but a letter has no other case) is made case-exact, and the letters around it keep ignoring case:
        /// outside a class \xC3 becomes (?:(?-i:\xC3)) (a group of its own: .NET refuses (?-i:…) right after a
        /// conditional's test, (?(x)(?-i:y)|z), measured); a class with such a code is split, its codes in a case-exact class:
        /// [a\xC3] -> (?:[a]|(?-i:[\xC3])), [^a\xC3] -> (?:(?!(?-i:[\xC3]))[^a]), and a subtraction [B-[S]] -> (?:(?!S)B).
        /// A range is exact when an end of it is a code. A comment ((?#…), # in (?x) mode) is left as it is. Anything not
        /// understood leaves the pattern as given.
        /// </summary>
        internal static string ExactCodes(string p)
        {
            if (p.IndexOf('\\') < 0) return p;
            bool extended = System.Text.RegularExpressions.Regex.IsMatch(p, @"\(\?[A-Za-z-]*x");
            var sb = new StringBuilder(p.Length + 16);
            bool changed = false;
            for (int i = 0; i < p.Length; i++)
            {
                char c = p[i];
                int end;
                if (c == '\\')
                {
                    end = SkipEscape(p, i);
                    if (end < 0) return p;
                    string esc = p[i..(end + 1)];
                    if (Code(esc) is int v && Cased(v)) { sb.Append("(?:(?-i:").Append(esc).Append("))"); changed = true; }
                    else sb.Append(esc);
                }
                else if (c == '[')
                {
                    end = SkipClass(p, i);
                    if (end < 0) return p;
                    string cls = p[i..(end + 1)];
                    if (ExactClass(cls) is not { } made) return p;
                    changed |= !ReferenceEquals(made, cls);
                    sb.Append(made);
                }
                else if (c == '(' && string.CompareOrdinal(p, i, "(?#", 0, 3) == 0)
                {
                    end = p.IndexOf(')', i);
                    if (end < 0) return p;
                    sb.Append(p, i, end - i + 1);
                }
                else if (c == '#' && extended)
                {
                    end = p.IndexOf('\n', i);
                    if (end < 0) end = p.Length - 1;
                    sb.Append(p, i, end - i + 1);
                }
                else { sb.Append(c); continue; }
                i = end;
            }
            return changed ? sb.ToString() : p;
        }

        /// <summary>Whether a character has another case (a letter, or one with a case mapping): measured over every
        /// character, .NET's ignored case matches another character only for these.</summary>
        static bool Cased(int v) { char c = (char)v; return char.IsLetter(c) || char.ToUpperInvariant(c) != c || char.ToLowerInvariant(c) != c; }

        /// <summary>The character an escape outside a class writes as a code (\xNN, \uNNNN), or null. (An octal code
        /// outside a class, \0 and two digits, goes up to 3F: no letter.)</summary>
        static int? Code(string esc) =>
            esc.Length == 4 && esc[1] == 'x' || esc.Length == 6 && esc[1] == 'u'
                ? int.TryParse(esc.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int v) ? v : null
                : null;

        /// <summary>One item of a class from <paramref name="j"/>: its text, its character (-1 for a set: \d, \p{L}),
        /// whether it is written as a code, and the index after it; -1 when not understood.</summary>
        static (string Text, int Value, bool Code, int Next) ClassItem(string p, int j)
        {
            if (p[j] != '\\') return (p[j].ToString(), p[j], false, j + 1);
            if (j + 1 >= p.Length) return ("", 0, false, -1);
            char e = p[j + 1];
            if (e is >= '0' and <= '7')
            {
                // An octal code in a class: up to three digits, as .NET reads them (\101 is A)
                int k = j + 1, v = 0;
                while (k < p.Length && k < j + 4 && p[k] is >= '0' and <= '7') v = v * 8 + (p[k++] - '0');
                return (p[j..k], v & 0xFF, true, k);
            }
            int end = SkipEscape(p, j);
            if (end < 0) return ("", 0, false, -1);
            string text = p[j..(end + 1)];
            if (Code(text) is int code) return (text, code, true, end + 1);
            int value = e switch
            {
                'd' or 'D' or 'w' or 'W' or 's' or 'S' or 'p' or 'P' => -1,
                'a' => 7, 'b' => 8, 't' => 9, 'n' => 10, 'v' => 11, 'f' => 12, 'r' => 13, 'e' => 27,
                'c' => end == j + 2 && char.IsAsciiLetter(p[j + 2]) ? char.ToUpperInvariant(p[j + 2]) - '@' : -2,
                'x' or 'u' => -2,
                _ => e,
            };
            return value == -2 ? ("", 0, false, -1) : (text, value, false, end + 1);
        }

        /// <summary>A class (from its [ to its ]) with its codes of letters made case-exact (<see cref="ExactCodes"/>):
        /// the class itself when it has none, null when not understood.</summary>
        static string? ExactClass(string cls)
        {
            int j = 1, last = cls.Length - 1;
            bool negated = j < last && cls[j] == '^';
            if (negated) j++;
            var items = new List<(string Lo, string? Hi, bool Exact)>();
            string? subtracted = null;
            for (bool first = true; j < last; first = false)
            {
                // A subtraction, -[…], ends the class
                if (!first && cls[j] == '-' && j + 1 < last && cls[j + 1] == '[') { subtracted = cls[(j + 1)..last]; break; }
                var a = ClassItem(cls, j);
                if (a.Next < 0) return null;
                // A range: a character, -, a character (a - before the ] or a subtraction is the character -)
                if (a.Value >= 0 && a.Next + 1 < last && cls[a.Next] == '-' && cls[a.Next + 1] != '[')
                {
                    var b = ClassItem(cls, a.Next + 1);
                    if (b.Next < 0 || b.Value < a.Value) return null;
                    bool exact = false;
                    if (a.Code || b.Code) for (int v = a.Value; v <= b.Value && !exact; v++) exact = Cased(v);
                    items.Add((a.Text, b.Text, exact));
                    j = b.Next;
                    continue;
                }
                items.Add((a.Text, null, a.Code && Cased(a.Value)));
                j = a.Next;
            }
            static string Esc(string t) => t.Length == 1 && t[0] is '-' or '^' or '[' or ']' ? "\\" + t : t;
            static string Body(IEnumerable<(string Lo, string? Hi, bool Exact)> items) => string.Concat(items.Select(x => Esc(x.Lo) + (x.Hi != null ? "-" + Esc(x.Hi) : "")));
            if (subtracted != null)
            {
                string baseClass = "[" + (negated ? "^" : "") + Body(items) + "]";
                if (items.Count == 0 || ExactClass(baseClass) is not { } b || ExactClass(subtracted) is not { } s) return null;
                return ReferenceEquals(b, baseClass) && ReferenceEquals(s, subtracted) ? cls : $"(?:(?!{s}){b})";
            }
            var exactOnes = items.Where(x => x.Exact).ToList();
            if (exactOnes.Count == 0) return cls;
            var folded = items.Where(x => !x.Exact).ToList();
            string e = "[" + (negated ? "^" : "") + Body(exactOnes) + "]";
            if (folded.Count == 0) return $"(?:(?-i:{e}))";
            string f = "[" + (negated ? "^" : "") + Body(folded) + "]";
            return negated ? $"(?:(?!(?-i:[{Body(exactOnes)}])){f})" : $"(?:{f}|(?-i:{e}))";
        }

        /// <summary>
        /// A path is written three ways in the game's files: with \ (meshes, databases, scripts), with \\ (a .mat or
        /// another JSON file escapes it) and with /. A text with a \ or / is looked for in each spelling.
        /// </summary>
        internal static string[] Spellings(string text)
        {
            if (text.IndexOfAny(new[] { '\\', '/' }) < 0) return new[] { text };
            var parts = System.Text.RegularExpressions.Regex.Split(text, @"[\\/]+");
            return new[] { string.Join("\\\\", parts), string.Join("\\", parts), string.Join("/", parts) }.Distinct(StringComparer.Ordinal).ToArray();
        }

        /// <summary>
        /// Every hit that starts at or after <paramref name="from"/> and before <paramref name="before"/>: its start and
        /// length. The characters before <paramref name="from"/> are context: ^, \b and a look-behind see them, and \A
        /// matches only at the window's first character.
        /// </summary>
        public void Find(ReadOnlySpan<char> window, int from, int before, Action<int, int> hit)
        {
            if (_text != null)
            {
                int at = from;
                while (at < before)
                {
                    int i = window[at..].IndexOfAny(_text);
                    if (i < 0 || at + i >= before) return;
                    int length = 0;
                    foreach (var sp in _spellings)
                        if (window[(at + i)..].StartsWith(sp, StringComparison.OrdinalIgnoreCase)) { length = sp.Length; break; }
                    hit(at + i, length);
                    at += i + 1;
                }
                return;
            }
            try
            {
                foreach (var m in _regex!.EnumerateMatches(window, from))
                {
                    if (m.Index >= before) return;
                    if (m.Length == 0) continue;
                    hit(m.Index, m.Length);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                throw new ReportException("the regular expression took longer than 10 seconds on one piece of a file", "write it so it can't try the same text many ways (no nested * or +), or look for a plain text");
            }
        }
    }

    /// <summary>Windows-1252's characters for the bytes 80 to 9F (its five unused ones kept as the byte); A0 to FF are Latin-1's.</summary>
    const string Cp1252High = "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F" +
                              "\u0090‘’“”•–—˜™š›œ\u009DžŸ";

    /// <summary>
    /// Where the characters of an 8-bit file read as text stand in its bytes: after each character of more than one byte,
    /// the index of the next character and how many more bytes than characters come before it. Only those are kept: every
    /// other character is one byte.
    /// </summary>
    internal sealed class TextMap
    {
        int[] _charAfter = new int[256], _extra = new int[256];
        int _count;

        public void Clear() => _count = 0;

        public void Add(int charAfter, int extra)
        {
            if (_count == _charAfter.Length) { Array.Resize(ref _charAfter, _count * 2); Array.Resize(ref _extra, _count * 2); }
            _charAfter[_count] = charAfter; _extra[_count] = extra; _count++;
        }

        /// <summary>The byte where character <paramref name="c"/> starts.</summary>
        public int ByteOf(int c)
        {
            int lo = 0, hi = _count - 1, k = -1;
            while (lo <= hi) { int mid = (lo + hi) >>> 1; if (_charAfter[mid] <= c) { k = mid; lo = mid + 1; } else hi = mid - 1; }
            return c + (k >= 0 ? _extra[k] : 0);
        }

        /// <summary>The first character that starts at or after byte <paramref name="b"/>.</summary>
        public int CharOf(int b)
        {
            // The last character of more than one byte that ends at or before b: each character from there to b is one byte
            int lo = 0, hi = _count - 1, k = -1;
            while (lo <= hi) { int mid = (lo + hi) >>> 1; if (_charAfter[mid] + _extra[mid] <= b) { k = mid; lo = mid + 1; } else hi = mid - 1; }
            int c = b - (k >= 0 ? _extra[k] : 0);
            // b inside the next such character: the character after it
            return k + 1 < _count ? Math.Min(c, _charAfter[k + 1]) : c;
        }
    }

    /// <summary>
    /// An 8-bit file's bytes as text (J55): UTF-8, and a byte that isn't part of a valid UTF-8 character as Windows-1252.
    /// Data holds both, even in one file: Starfield_en.strings keeps 57 strings in 1252 among its UTF-8 ones, and
    /// DarkStar's German, French and Spanish tables are 1252 throughout (notes\fix-j55-grep-case.md). Decided byte by
    /// byte, so a file that mixes them reads right, and a piece cut inside a character changes only the context before
    /// the part searched. The characters go into <paramref name="chars"/> (as many as the bytes at most), where they
    /// stand into <paramref name="map"/>; returns how many there are. <paramref name="widened"/>: the bytes are in
    /// <paramref name="chars"/> as Latin-1 already.
    /// </summary>
    internal static int ReadText(ReadOnlySpan<byte> bytes, Span<char> chars, TextMap map, bool widened = false)
    {
        map.Clear();
        int n = bytes.Length;
        if (!widened) Encoding.Latin1.GetChars(bytes, chars);
        int i = bytes.IndexOfAnyExceptInRange((byte)0, (byte)0x7F);
        if (i < 0) return n;
        int o = i;
        while (i < n)
        {
            byte b = bytes[i];
            if (b < 0x80)
            {
                // A run of ASCII: already its characters, moved back by the bytes the characters before it took. Short
                // runs (binary files) one by one, a long one found and moved at once (10% faster on the game's meshes)
                int j = i + 1, end = Math.Min(n, i + 16);
                while (j < end && bytes[j] < 0x80) j++;
                int run = j - i;
                if (j == end && end < n) { run = bytes[i..].IndexOfAnyExceptInRange((byte)0, (byte)0x7F); if (run < 0) run = n - i; }
                if (o != i)
                {
                    if (run <= 16) for (int k = 0; k < run; k++) chars[o + k] = (char)bytes[i + k];
                    else chars.Slice(i, run).CopyTo(chars[o..]);
                }
                i += run; o += run;
                continue;
            }
            int length = b is >= 0xC2 and <= 0xF4 ? Utf8Length(bytes, i) : 0;
            if (length == 0)
            {
                chars[o++] = b < 0xA0 ? Cp1252High[b - 0x80] : (char)b;
                i++;
                continue;
            }
            int cp = length switch
            {
                2 => (b & 0x1F) << 6 | bytes[i + 1] & 0x3F,
                3 => (b & 0x0F) << 12 | (bytes[i + 1] & 0x3F) << 6 | bytes[i + 2] & 0x3F,
                _ => (b & 0x07) << 18 | (bytes[i + 1] & 0x3F) << 12 | (bytes[i + 2] & 0x3F) << 6 | bytes[i + 3] & 0x3F,
            };
            if (cp < 0x10000) chars[o++] = (char)cp;
            else { cp -= 0x10000; chars[o++] = (char)(0xD800 + (cp >> 10)); chars[o++] = (char)(0xDC00 + (cp & 0x3FF)); }
            i += length;
            map.Add(o, i - o);
        }
        return o;
    }

    /// <summary>The length (2 to 4) of the valid UTF-8 character that starts at <paramref name="i"/>, or 0. Valid as .NET's
    /// own decoder takes it: no overlong form, no surrogate, nothing past U+10FFFF.</summary>
    static int Utf8Length(ReadOnlySpan<byte> bytes, int i)
    {
        static bool Tail(byte x) => (x & 0xC0) == 0x80;
        byte b = bytes[i];
        int left = bytes.Length - i;
        if (b is >= 0xC2 and <= 0xDF) return left >= 2 && Tail(bytes[i + 1]) ? 2 : 0;
        if (b is >= 0xE0 and <= 0xEF)
        {
            if (left < 3) return 0;
            byte c = bytes[i + 1];
            bool ok = b == 0xE0 ? c is >= 0xA0 and <= 0xBF : b == 0xED ? c is >= 0x80 and <= 0x9F : Tail(c);
            return ok && Tail(bytes[i + 2]) ? 3 : 0;
        }
        if (b is >= 0xF0 and <= 0xF4)
        {
            if (left < 4) return 0;
            byte c = bytes[i + 1];
            bool ok = b == 0xF0 ? c is >= 0x90 and <= 0xBF : b == 0xF4 ? c is >= 0x80 and <= 0x8F : Tail(c);
            return ok && Tail(bytes[i + 2]) && Tail(bytes[i + 3]) ? 4 : 0;
        }
        return 0;
    }

    /// <summary>The buffers of one thread: a piece, the 64 KB of context before it and the 64 KB after it; where the
    /// characters stand when an 8-bit file is read as text.</summary>
    internal sealed class Buffers
    {
        public readonly byte[] Bytes = new byte[Piece + 2 * Overlap];
        public readonly char[] Chars = new char[Piece + 2 * Overlap];
        public readonly TextMap Map = new();
        /// <summary>A hit's text read around it, for a pattern matched on the bytes (J71).</summary>
        public char[] AroundChars = new char[1024];
        public readonly TextMap AroundMap = new();
    }

    /// <summary>A file's bytes: a loose file from disk, an archived one through Mutagen's reader (inflated).</summary>
    static Stream Open(GrepPlan plan, GrepPlan.Item it) => it.Source < 0
        ? new FileStream(System.IO.Path.Combine(plan.DataFolder, it.Path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan)
        : it.File!.AsStream();

    /// <summary>UTF-16 code units, one character each (a broken one stays as it is): the offsets stay two bytes a character.</summary>
    static void Utf16(ReadOnlySpan<byte> bytes, char[] chars, bool bigEndian)
    {
        var units = MemoryMarshal.Cast<byte, ushort>(bytes);
        var into = MemoryMarshal.Cast<char, ushort>(chars.AsSpan(0, units.Length));
        if (bigEndian == BitConverter.IsLittleEndian) BinaryPrimitives.ReverseEndianness(units, into);
        else units.CopyTo(into);
    }

    /// <summary>Searches one file, keeping its first <paramref name="keptHits"/> hits (the rest are counted). Null when it has no hit.</summary>
    static GrepFile? SearchFile(GrepPlan plan, GrepPlan.Item it, Buffers buf, int keptHits, out long read, List<string> errors)
    {
        read = 0;
        string where = it.Source < 0 ? "loose" : plan.Sources[it.Source].Name;
        GrepFile? result = null;
        string ext = Ext(it.Path);
        bool text = TextKinds.Contains(ext);
        var matcher = plan.Matcher;
        Stream? stream = null;
        try
        {
            stream = Open(plan, it);
            // A compressed Flash file (.swf "CWS", Scaleform .gfx "CFX"): its content after the 8-byte header is zlib
            long offsetBase = 0;
            bool inflated = false;
            int have = stream.ReadAtLeast(buf.Bytes.AsSpan(0, 8), 8, throwOnEndOfStream: false);
            read += have;
            var head = buf.Bytes;
            if (have == 8 && ext is ".swf" or ".gfx" && head[0] == 'C' && (head[1] == 'W' && head[2] == 'S' || head[1] == 'F' && head[2] == 'X'))
            {
                stream = new ZLibStream(stream, CompressionMode.Decompress, leaveOpen: false);
                offsetBase = 8; inflated = true; have = 0;
            }
            // A text file with a UTF-16 byte-order mark (FF FE, not the FF FE 00 00 of UTF-32; or FE FF) is UTF-16 text:
            // the interface's translate_*.txt tables. Only text kinds: binary files (548 .lod) start with FE FF as a number
            int wide = 0;
            if (text && have >= 2)
            {
                if (head[0] == 0xFF && head[1] == 0xFE && !(have >= 4 && head[2] == 0 && head[3] == 0)) wide = 1;
                else if (head[0] == 0xFE && head[1] == 0xFF) wide = 2;
            }
            int unit = wide != 0 ? 2 : 1;
            // An 8-bit file read as text, for a text outside ASCII (J55): its characters stand where the map says. Not when
            // the pattern is matched on the bytes (J71): then only a kept hit's text is read around it
            bool onBytes = wide == 0 && matcher.OnBytes;
            var map = wide == 0 && matcher.DecodesBytes && !onBytes ? buf.Map : null;
            // In bytes: the bytes at the start of the buffer, the first byte not yet searched; in the file: the buffer's
            // first byte, and where the last match of a regular expression ended
            int keep = have, scanFrom = 0;
            long windowStart = offsetBase, lines = 0, resume = 0;
            while (true)
            {
                int n = stream.ReadAtLeast(buf.Bytes.AsSpan(keep, Piece), Piece, throwOnEndOfStream: false);
                read += n;
                int len = keep + n;
                bool last = n < Piece;
                if (len <= scanFrom) break;
                int before = last ? len : len - Overlap;
                // The same in characters
                int cLen, cBefore, cScan, from;
                bool search = true, decoded = false;
                if (map != null)
                {
                    // Read as text only when the bytes to search hold the text's ASCII anchor; else the piece is passed
                    // over as bytes (no hit can start in it), its lines still counted
                    Encoding.Latin1.GetChars(buf.Bytes.AsSpan(0, len), buf.Chars);
                    search = decoded = matcher.MayHold(new ReadOnlySpan<char>(buf.Chars, scanFrom, len - scanFrom));
                }
                if (decoded)
                {
                    cLen = ReadText(buf.Bytes.AsSpan(0, len), buf.Chars, map!, widened: true);
                    cBefore = map!.CharOf(before); cScan = map.CharOf(scanFrom);
                    from = Math.Clamp(map.CharOf((int)Math.Clamp(resume - windowStart, 0, len)), cScan, cBefore);
                }
                else
                {
                    cLen = len / unit; cBefore = before / unit; cScan = scanFrom / unit;
                    if (wide == 0 && map == null) Encoding.Latin1.GetChars(buf.Bytes.AsSpan(0, len), buf.Chars);
                    else if (wide != 0) Utf16(buf.Bytes.AsSpan(0, cLen * 2), buf.Chars, wide == 2);
                    from = (int)Math.Clamp((resume - windowStart) / unit, cScan, cBefore);
                }
                var window = new ReadOnlySpan<char>(buf.Chars, 0, cLen);
                long linesAt = lines; int countedTo = cScan;
                var chars = buf.Chars;
                long start = windowStart;
                long ByteOf(int c) => decoded ? map!.ByteOf(c) : (long)c * unit;
                var bytesRead = buf.Bytes;
                int bytesLen = len;
                Action<int, int> found = (at, length) =>
                {
                    resume = start + ByteOf(at + length);
                    result ??= new GrepFile { Where = where, Path = it.Path, Size = it.Size, LooseCopy = it.LooseCopy };
                    result.Count++;
                    if (result.Hits.Count >= keptHits) return;
                    long? line = null;
                    if (text)
                    {
                        linesAt += new ReadOnlySpan<char>(chars, countedTo, at - countedTo).Count('\n');
                        countedTo = at;
                        line = linesAt + 1;
                    }
                    string match, around;
                    if (onBytes)
                    {
                        // The text read around the hit, as reading the whole piece gives it
                        int need = Math.Min(length + 2 * 256 + 4, bytesLen);
                        if (buf.AroundChars.Length < need) buf.AroundChars = new char[need];
                        var (s, n, count) = ReadAround(bytesRead.AsSpan(0, bytesLen), at, length, buf.AroundChars, buf.AroundMap, out _);
                        match = Decode(new ReadOnlySpan<char>(buf.AroundChars, s, Math.Min(n, 200)), true);
                        around = Around(new ReadOnlySpan<char>(buf.AroundChars, 0, count), s, n, true);
                    }
                    else
                    {
                        match = Decode(new ReadOnlySpan<char>(chars, at, Math.Min(length, 200)), wide != 0 || decoded);
                        around = Around(new ReadOnlySpan<char>(chars, 0, cLen), at, length, wide != 0 || decoded);
                    }
                    result.Hits.Add(new GrepHit
                    {
                        Where = where, Path = it.Path, Offset = start + ByteOf(at), Line = line, LooseCopy = it.LooseCopy, Inflated = inflated, Utf16 = wide != 0,
                        Match = match, Around = around,
                    });
                };
                if (search)
                {
                    if (onBytes) matcher.FindBytes(buf.Bytes.AsSpan(0, len), window, from, cBefore, found);
                    else matcher.Find(window, from, cBefore, found);
                }
                if (last) break;
                if (text) lines += window[cScan..cBefore].Count('\n');
                // The 64 KB before the cut go round as context, already searched, and the 64 KB after it to be searched:
                // a hit across the cut is whole in the next window, and the bytes before it are seen
                int carryFrom = before - Math.Min(Overlap, before);
                Buffer.BlockCopy(buf.Bytes, carryFrom, buf.Bytes, 0, len - carryFrom);
                keep = len - carryFrom;
                windowStart += carryFrom;
                scanFrom = before - carryFrom;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotImplementedException or ArgumentException or OverflowException or ObjectDisposedException)
        {
            lock (errors) errors.Add($"{it.Path} ({where}): {e.Message.Split('\n')[0].TrimEnd('.')}");
        }
        finally { stream?.Dispose(); }
        return result;
    }

    /// <summary>Characters as the text they are: bytes (Latin-1) as UTF-8 text, or text already (<paramref name="wide"/>: UTF-16, or an 8-bit file read as text); a character that isn't text as a dot.</summary>
    static string Decode(ReadOnlySpan<char> chars, bool wide)
    {
        string s;
        if (wide) s = new string(chars);
        else
        {
            var bytes = new byte[chars.Length];
            for (int i = 0; i < chars.Length; i++) bytes[i] = (byte)chars[i];
            s = Encoding.UTF8.GetString(bytes);
        }
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(c is '\t' ? ' ' : c is < ' ' or '�' or '﻿' || c == 127 || char.IsSurrogate(c) || char.IsControl(c) ? '.' : c);
        return sb.ToString();
    }

    static bool Printable(char c, bool wide) => c is >= ' ' and < (char)127 or '\t' || wide && c > 127 && !char.IsControl(c) && !char.IsSurrogate(c) && c is not ('�' or '﻿');

    /// <summary>The run of printable characters a hit sits in, up to 60 on each side, the hit in « ».</summary>
    static string Around(ReadOnlySpan<char> window, int at, int length, bool wide)
    {
        int from = at, to = Math.Min(window.Length, at + length);
        while (from > 0 && at - from < 60 && Printable(window[from - 1], wide)) from--;
        while (to < window.Length && to - (at + length) < 60 && Printable(window[to], wide)) to++;
        int end = Math.Min(at + length, window.Length);
        string match = Decode(window[at..end], wide);
        if (match.Length > 200) match = match[..200] + "…";
        return (from > 0 && Printable(window[from - 1], wide) ? "…" : "") + Decode(window[from..at], wide).TrimStart() + "«" + match + "»" + Decode(window[end..to], wide).TrimEnd() + (to < window.Length && Printable(window[to], wide) ? "…" : "");
    }

    /// <summary>Reads archives ahead from disk, in the order their files are searched: of each, the stretches that hold
    /// the scope's files, in the archive's order (or the whole archive, when Mutagen didn't say where its files are).</summary>
    sealed class ReadAhead
    {
        readonly GrepPlan _plan;
        readonly CancellationToken _stop;
        public volatile int Current = -1;
        public long Bytes;
        public Task Task { get; }

        public ReadAhead(GrepPlan plan, CancellationToken stop)
        {
            _plan = plan; _stop = stop;
            var order = new List<int>();
            for (int i = plan.Start; i < plan.Items.Count; i++)
            {
                int s = plan.Items[i].Source;
                if (s >= 0 && plan.Sources[s].ReadAhead && (order.Count == 0 || order[^1] != s) && !order.Contains(s)) order.Add(s);
            }
            Task = order.Count == 0 ? Task.CompletedTask : Task.Factory.StartNew(() => Run(order), stop, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        void Run(List<int> order)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4 << 20);
            try
            {
                for (int k = 0; k < order.Count; k++)
                {
                    // Not more than AheadBytes beyond the archive being searched
                    while (!_stop.IsCancellationRequested)
                    {
                        long ahead = 0;
                        for (int j = k - 1; j >= 0 && order[j] > Current; j--) ahead += _plan.Sources[order[j]].AheadBytes;
                        if (ahead < AheadBytes) break;
                        Thread.Sleep(20);
                    }
                    if (_stop.IsCancellationRequested) return;
                    var src = _plan.Sources[order[k]];
                    try
                    {
                        using var fs = new FileStream(src.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
                        foreach (var (offset, length) in src.Stretches ?? new() { (0L, src.Length) })
                        {
                            fs.Position = offset;
                            long left = length;
                            int n;
                            while (left > 0 && !_stop.IsCancellationRequested && (n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, left))) > 0) { left -= n; Interlocked.Add(ref Bytes, n); }
                            if (_stop.IsCancellationRequested) break;
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
    }

    /// <summary>
    /// Searches the plan's files from its start, a batch at a time on all cores, handing each file's result to
    /// <paramref name="take"/> in their order (its index, its hits or null, the bytes read) until it says stop.
    /// Notes what couldn't be read and what was read ahead on <paramref name="answer"/>; gives the time the search took.
    /// </summary>
    internal static void Run(GrepPlan plan, ReportAnswer answer, int keptHits, Func<int, GrepFile?, long, bool> take, Action<double> took) =>
        Run<GrepFile>(plan, answer, (it, b, errors) => (SearchFile(plan, it, b, keptHits, out long read, errors), read), take, took);

    /// <summary>The same with any work on a file: its result (or null) and the bytes it read.</summary>
    internal static void Run<T>(GrepPlan plan, ReportAnswer answer, Func<GrepPlan.Item, Buffers, List<string>, (T? Result, long Read)> work, Func<int, T?, long, bool> take, Action<double> took) where T : class
    {
        var errors = new List<string>();
        using var stop = new CancellationTokenSource();
        var ahead = new ReadAhead(plan, stop.Token);
        var clock = Stopwatch.StartNew();
        var buffers = new System.Collections.Concurrent.ConcurrentBag<Buffers>();
        int at = plan.Start;
        try
        {
            while (at < plan.Items.Count)
            {
                int end = at; long batch = 0;
                while (end < plan.Items.Count && end - at < BatchFiles && (batch < BatchBytes || end == at)) { batch += plan.Items[end].Reads; end++; }
                ahead.Current = plan.Items[at].Source;
                var found = new T?[end - at];
                var read = new long[end - at];
                int first = at;
                Parallel.For(first, end, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
                {
                    if (!buffers.TryTake(out var b)) b = new Buffers();
                    try { (found[i - first], read[i - first]) = work(plan.Items[i], b, errors); }
                    finally { buffers.Add(b); }
                });
                for (int i = first; i < end; i++)
                    if (take(i, found[i - first], read[i - first])) return;
                at = end;
            }
        }
        finally
        {
            stop.Cancel();
            try { ahead.Task.Wait(); } catch (AggregateException) { }
            took(clock.Elapsed.TotalSeconds);
            if (errors.Count > 0)
                answer.Notes.Add($"{ReportAnswer.Plural(errors.Count, "file")} couldn't be read, so {(errors.Count == 1 ? "it was" : "they were")} not searched: {string.Join("; ", errors.Take(5))}{(errors.Count > 5 ? "; …" : "")}.");
            if (ahead.Bytes > 0) answer.Read.Add($"{ArchiveStore.SizeText(Interlocked.Read(ref ahead.Bytes))} of archives read ahead from disk (the stretches that hold the scope's files, in each archive's order)");
        }
    }
}

/// <summary>The hits and files of one kind of file, or of one kind in one archive.</summary>
public sealed class GrepTally
{
    public long Files, Bytes, FilesWithHits, Hits;
    internal void Add(long read, GrepFile? f) { Files++; Bytes += read; if (f != null) { FilesWithHits++; Hits += f.Count; } }
}

/// <summary><c>archive2 grep --count</c>: the hits and the files that hold them, per kind of file (and per archive),
/// over the whole scope. Answers "how many of the game's meshes name this" without paging through the hits.</summary>
public sealed class GrepCountAnswer : ReportAnswer
{
    public override string Kind => "archive";
    public required string Target { get; init; }
    public required string Text { get; init; }
    public bool Regex { get; init; }
    public string Scope { get; init; } = "";
    public long ScopeFiles { get; init; }
    public long ScopeBytes { get; init; }
    public int Archives { get; init; }
    public int LooseFiles { get; init; }
    public long EstimateFiles { get; init; }
    public long EstimateBytes { get; init; }
    public double EstimateSeconds { get; init; }
    public double PatternMBps { get; init; }
    public long PatternSampleBytes { get; init; }
    public double SearchSeconds { get; set; }
    public GrepTally All { get; } = new();
    /// <summary>Per extension (".mesh"; "(none)").</summary>
    public Dictionary<string, GrepTally> ByKind { get; } = new(StringComparer.Ordinal);
    /// <summary>Per extension, then per archive ("loose" for the loose files).</summary>
    public Dictionary<string, Dictionary<string, GrepTally>> ByKindWhere { get; } = new(StringComparer.Ordinal);

    IEnumerable<KeyValuePair<string, GrepTally>> Kinds => ByKind.OrderByDescending(k => k.Value.Hits).ThenByDescending(k => k.Value.FilesWithHits).ThenByDescending(k => k.Value.Files).ThenBy(k => k.Key, StringComparer.Ordinal);
    IEnumerable<KeyValuePair<string, GrepTally>> Where(string kind) => ByKindWhere.TryGetValue(kind, out var w)
        ? w.Where(x => x.Value.FilesWithHits > 0).OrderByDescending(x => x.Value.Hits).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
        : Enumerable.Empty<KeyValuePair<string, GrepTally>>();

    protected override void Fill(JsonObject json)
    {
        json["mode"] = "grep-count";
        json["target"] = Target;
        json["text"] = Text;
        json["regex"] = Regex;
        if (Scope.Length > 0) json["scope"] = Scope;
        json["filesInScope"] = ScopeFiles;
        json["bytesInScope"] = ScopeBytes;
        json["archives"] = Archives;
        json["looseFiles"] = LooseFiles;
        json["estimate"] = ArchiveGrep.EstimateJson(EstimateFiles, EstimateBytes, EstimateSeconds, PatternMBps, PatternSampleBytes);
        json["searchSeconds"] = Math.Round(SearchSeconds, 2);
        json["filesSearched"] = All.Files;
        json["bytesSearched"] = All.Bytes;
        json["filesWithHits"] = All.FilesWithHits;
        json["hits"] = All.Hits;
        json["kinds"] = new JsonArray(Kinds.Select(k =>
        {
            var o = Tally(k.Value);
            o["kind"] = k.Key;
            var where = Where(k.Key).ToList();
            if (where.Count > 0) o["where"] = new JsonArray(where.Select(w => { var x = Tally(w.Value); x["where"] = w.Key; return (JsonNode)x; }).ToArray());
            return (JsonNode)o;
        }).ToArray());
    }

    static JsonObject Tally(GrepTally t) => new() { ["filesSearched"] = t.Files, ["bytesSearched"] = t.Bytes, ["filesWithHits"] = t.FilesWithHits, ["hits"] = t.Hits };

    protected override void Render(ReportBody body, ReportTextOptions o)
    {
        body.RowNoun = "kinds";
        string scope = $"{Plural(ScopeFiles, "file")}, {ArchiveStore.SizeText(ScopeBytes)} inflated, in {Plural(Archives, "archive")}" + (LooseFiles > 0 ? $" and {Plural(LooseFiles, "loose file")}" : "");
        string what = Regex ? $"the regular expression {Text}" : $"\"{Text}\"";
        body.Head.Add($"{what} in the files of {Target}{(Scope.Length > 0 ? " (" + Scope + ")" : "")} ({scope}): {Plural(All.Hits, "hit")} in {N(All.FilesWithHits)} of {Plural(All.Files, "file")} searched, {Plural(ByKind.Count, "kind")} of file.");
        body.Head.Add(ArchiveGrep.EstimateLine(EstimateFiles, EstimateBytes, EstimateSeconds, PatternMBps, PatternSampleBytes, Regex) + $" The search took {GrepAnswer.Time(SearchSeconds)} for {Plural(All.Files, "file")}, {ArchiveStore.SizeText(All.Bytes)}.");
        if (ByKind.Count == 0) return;
        if (o.Detail)
        {
            body.Head.Add("| Kind | Where | Files searched | Files with a hit | Hits |");
            body.Head.Add("| :--- | :--- | ---: | ---: | ---: |");
        }
        else
        {
            body.Head.Add("| Kind | Files searched | Files with a hit | Hits |");
            body.Head.Add("| :--- | ---: | ---: | ---: |");
        }
        foreach (var (kind, t) in Kinds)
        {
            if (!o.Detail) { body.Rows.Add($"| {Cell(kind)} | {N(t.Files)} | {N(t.FilesWithHits)} | {N(t.Hits)} |"); continue; }
            var rows = new StringBuilder($"| {Cell(kind)} | (every archive) | {N(t.Files)} | {N(t.FilesWithHits)} | {N(t.Hits)} |");
            foreach (var (where, w) in Where(kind)) rows.Append($"\n| {Cell(kind)} | {Cell(where)} | {N(w.Files)} | {N(w.FilesWithHits)} | {N(w.Hits)} |");
            body.Rows.Add(rows.ToString());
        }
    }
}

public sealed partial class ArchiveReport
{
    /// <summary>
    /// The files a search covers and its cost, before any file is read: the archives' name tables (all at once), the
    /// file records of the archives with a name in the scope, the loose files; for a regular expression, its speed on
    /// a sample of these files. The plan holds the archives open: dispose it. A regular expression .NET can't read
    /// fails here.
    /// </summary>
    public GrepPlan PlanGrep(string target, GrepOptions options)
    {
        if (string.IsNullOrEmpty(options.Text)) throw new ReportException("no text was given to look for", "archive2 <plugin | file.ba2 | all> grep <text>");
        return Plan(target, options, new ArchiveGrep.Matcher(options.Text, options.Regex), "grep");
    }

    /// <summary>The files <c>archive2 encoding</c> reads, and its cost, worked out as for a search (<see cref="PlanGrep"/>):
    /// the same scope (<see cref="GrepOptions.Extensions"/>, <see cref="GrepOptions.Folders"/>, the loose files); the text isn't used.</summary>
    public GrepPlan PlanEncodings(string target, GrepOptions options) => Plan(target, options, null, "encoding");

    GrepPlan Plan(string target, GrepOptions options, ArchiveGrep.Matcher? matcher, string verb)
    {
        bool encoding = verb == "encoding";
        var clock = Stopwatch.StartNew();
        var store = _s.Archives;
        store.Begin();
        var t = Resolve(_s, target, options.Beside);
        string named = target.Trim().Trim('"');
        bool everything = named == "*" || named.Equals("all", StringComparison.OrdinalIgnoreCase);
        var (kinds, allKinds) = ArchiveGrep.Kinds(options.Extensions);
        var plan = new GrepPlan
        {
            Target = t, Options = options, Kinds = kinds, AllKinds = allKinds, Folders = ArchiveGrep.FolderPrefixes(options.Folders), DataFolder = store.DataFolder,
            Everything = everything, Matcher = matcher!, Verb = verb,
        };
        try
        {
            // The names of every archive, side by side: an archive with no name in the scope is passed over
            var stamps = t.Archives;
            var lists = new (List<string>? Names, string? Error, IDisposableArchiveReader? Reader)[stamps.Count];
            Parallel.For(0, stamps.Count, k =>
            {
                IDisposableArchiveReader? r = null;
                try
                {
                    r = Archive.CreateDisposableReader(GameRelease.Starfield, stamps[k].Path);
                    lists[k] = (r.EnumerateFilePaths().Select(ArchiveStore.Normalize).ToList(), null, r);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotImplementedException or ArgumentException or OverflowException)
                {
                    r?.Dispose();
                    lists[k] = (null, e.Message, null);
                }
            });
            var archived = new List<GrepPlan.Item>();
            for (int k = 0; k < stamps.Count; k++)
            {
                var (names, error, reader) = lists[k];
                var src = new GrepPlan.Source { Name = stamps[k].Name, Path = stamps[k].Path, Length = stamps[k].Length, Reader = reader };
                plan.Sources.Add(src);
                if (names == null) { plan.Notes.Add($"{src.Name} couldn't be read: {error}."); continue; }
                bool any = false;
                foreach (var p in names)
                {
                    if (ArchiveGrep.InScope(plan, p, out var skipped)) any = true;
                    else if (skipped != null) plan.Skip(skipped);
                }
                if (!any) { plan.ArchivesPassedOver++; reader!.Dispose(); src.Reader = null; continue; }
                // The file records, for the sizes: the same files in the same order as the names. Where each file's bytes
                // are, how many there are on disk and whether they are compressed: Mutagen's GetStorage() (fixes.18, C42),
                // from the file record, no read (GAPS S68)
                var stretches = new List<(long Offset, long Length)>();
                foreach (var f in reader!.Files)
                {
                    src.Inflated += f.Size;
                    string p = ArchiveStore.Normalize(f.Path);
                    if (!ArchiveGrep.InScope(plan, p, out _)) continue;
                    long reads = encoding ? ArchiveGrep.EncodingReads(p, f.Size) : f.Size;
                    var storage = f.GetStorage();
                    long stored = f.Size;
                    if (storage == null) src.Stretches = null;
                    else
                    {
                        // The work reads all of it, or (the encodings) its first bytes: then the start of its first chunk
                        stored = reads >= f.Size ? storage.StoredSize : Math.Min(storage.StoredSize, reads);
                        if (reads >= f.Size) foreach (var c in storage.Chunks) stretches.Add((c.Offset, c.StoredSize));
                        else if (storage.Chunks.Count > 0) stretches.Add((storage.Chunks[0].Offset, Math.Min(storage.Chunks[0].StoredSize, stored)));
                    }
                    archived.Add(new GrepPlan.Item { Source = k, Path = p, File = f, Size = f.Size, Reads = reads, Stored = stored, Compressed = storage?.Compressed ?? true, LooseCopy = t.InData && store.IsLoose(p) });
                    src.InScope += reads; src.Files++;
                }
                if (src.Stretches != null)
                {
                    src.Stretches = ArchiveGrep.Merge(stretches);
                    src.ReadAhead = src.Stretches.Count > 0;
                }
                else src.ReadAhead = src.InScope * 2 >= src.Inflated;
            }
            // The loose files the game reads in place of archived ones: every one in Data's folders for "all",
            // else the loose copies of these archives' files
            var loose = new List<string>();
            if (t.InData)
            {
                if (everything)
                {
                    foreach (var p in store.Loose)
                    {
                        if (ArchiveGrep.InScope(plan, p, out var skipped)) loose.Add(p);
                        else if (skipped != null) plan.Skip(skipped);
                    }
                    // The files at Data's own top level are left out: the game reads plugins and archives there, never
                    // a loose file as an asset (an asset's path starts with its folder). Said, and counted (J47)
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(store.DataFolder))
                        {
                            string name = System.IO.Path.GetFileName(f), ext = ArchiveGrep.Ext(name);
                            if (ext is ".esm" or ".esp" or ".esl" or ".ba2" || !ArchiveGrep.InScope(plan, name, out _)) continue;
                            plan.TopLevelLeftOut++;
                            string kind = ext.Length > 0 ? ext : "(none)";
                            plan.TopLevelByKind[kind] = plan.TopLevelByKind.GetValueOrDefault(kind) + 1;
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                else loose.AddRange(archived.Where(a => a.LooseCopy).Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase));
                loose.Sort(StringComparer.OrdinalIgnoreCase);
            }
            foreach (var p in loose)
            {
                long size = 0;
                try { size = new FileInfo(System.IO.Path.Combine(store.DataFolder, p)).Length; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                plan.Items.Add(new GrepPlan.Item { Source = -1, Path = p, Size = size, Reads = encoding ? ArchiveGrep.EncodingReads(p, size) : size });
            }
            plan.LooseFiles = loose.Count;
            plan.Items.AddRange(archived);
            plan.Start = options.Count ? 0 : Math.Clamp(options.Start, 0, plan.Items.Count);
            plan.ListSeconds = clock.Elapsed.TotalSeconds;
            ArchiveGrep.SamplePattern(plan);
            ArchiveGrep.Estimate(plan);
            return plan;
        }
        catch
        {
            plan.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Searches the files of a plan, from its start, until the page holds about <paramref name="maxChars"/> characters
    /// of hits: then <see cref="GrepAnswer.NextItem"/> says where the next page starts. The caller disposes the plan.
    /// </summary>
    public GrepAnswer Grep(GrepPlan plan, int maxChars = 24000, bool detail = false) => _s.RunFiles(() =>
    {
        var t = plan.Target;
        var o = plan.Options;
        var answer = new GrepAnswer
        {
            Target = t.Shown, Text = o.Text, Regex = o.Regex, Scope = GrepScope(plan),
            ScopeFiles = plan.Items.Count, ScopeBytes = plan.Items.Sum(i => i.Size), Archives = plan.Archives, LooseFiles = plan.LooseFiles,
            FirstItem = plan.Start, EstimateFiles = plan.Files, EstimateBytes = plan.Bytes, EstimateSeconds = plan.Seconds,
            PatternMBps = plan.PatternMBps, PatternSampleBytes = plan.PatternBytes,
        };
        GrepHead(plan, answer);
        int perFile = detail ? int.MaxValue : 3;
        long used = 0;
        if (plan.Start < plan.Items.Count)
            ArchiveGrep.Run(plan, answer, ArchiveGrep.KeptHits, (i, f, read) =>
            {
                answer.ItemsSearched++;
                answer.BytesSearched += read;
                if (f == null) return false;
                answer.Files.Add(f);
                used += f.Hits.Take(perFile).Sum(h => h.Around.Length + h.Path.Length + h.Where.Length + 24) + (f.Count > perFile ? f.Path.Length + 60 : 0);
                if (used >= maxChars && i + 1 < plan.Items.Count) { answer.NextItem = i + 1; return true; }
                return false;
            }, s => answer.SearchSeconds = s);
        GrepTail(plan, answer, answer.ItemsSearched, answer.BytesSearched);
        if (answer.Files.Any(f => f.Hits.Any(h => h.Inflated)))
            answer.Notes.Add("A compressed Flash file (.swf, .gfx) is searched inflated: its offsets are those of the uncompressed file.");
        if (answer.Files.Any(f => f.Hits.Any(h => h.Utf16)))
            answer.Notes.Add("A text file that starts with a UTF-16 byte-order mark is searched as UTF-16 text (marked UTF-16): its offsets are still bytes of the file, two a character.");
        answer.EmptyReason = NoArchives(t) is { } none && plan.LooseFiles == 0 ? none
            : plan.Items.Count == 0 ? $"No file of {t.Shown} is in the scope{(answer.Scope.Length > 0 ? " (" + answer.Scope + ")" : "")}; {Plural(plan.SkippedFiles, "file")} {(plan.SkippedFiles == 1 ? "was" : "were")} passed over as textures, sounds or video."
            : plan.Start >= plan.Items.Count ? $"The cursor is past the last of the {Plural(plan.Items.Count, "file")}: nothing is left to search."
            : answer.Files.Count == 0 ? $"No file holds {(o.Regex ? "a match of " + o.Text : "\"" + o.Text + "\"")}: {Plural(answer.ItemsSearched, "file")} ({ArchiveStore.SizeText(answer.BytesSearched)} inflated) {(answer.ItemsSearched == 1 ? "was" : "were")} searched. Letter case doesn't count. " + (plan.Matcher.DecodesBytes
                ? "A file is read as UTF-8 text, a byte that isn't UTF-8 as Windows-1252, and as UTF-16 text when it is a text file that starts with a UTF-16 byte-order mark."
                : "The text is looked for in a file's bytes, and as UTF-16 text in a text file that starts with a UTF-16 byte-order mark.")
            : null;
        return answer;
    });

    /// <summary>
    /// Counts the hits and the files that hold them over the plan's whole scope, per kind of file and per archive: no
    /// hit is listed, nothing stops early. The caller disposes the plan.
    /// </summary>
    public GrepCountAnswer GrepCount(GrepPlan plan) => _s.RunFiles(() =>
    {
        var t = plan.Target;
        var o = plan.Options;
        var answer = new GrepCountAnswer
        {
            Target = t.Shown, Text = o.Text, Regex = o.Regex, Scope = GrepScope(plan),
            ScopeFiles = plan.Items.Count, ScopeBytes = plan.Items.Sum(i => i.Size), Archives = plan.Archives, LooseFiles = plan.LooseFiles,
            EstimateFiles = plan.Files, EstimateBytes = plan.Bytes, EstimateSeconds = plan.Seconds, PatternMBps = plan.PatternMBps, PatternSampleBytes = plan.PatternBytes,
        };
        GrepHead(plan, answer);
        if (plan.Items.Count > 0)
            ArchiveGrep.Run(plan, answer, 0, (i, f, read) =>
            {
                var it = plan.Items[i];
                string ext = ArchiveGrep.Ext(it.Path), kind = ext.Length > 0 ? ext : "(none)", where = it.Source < 0 ? "loose" : plan.Sources[it.Source].Name;
                answer.All.Add(read, f);
                if (!answer.ByKind.TryGetValue(kind, out var k)) answer.ByKind[kind] = k = new GrepTally();
                k.Add(read, f);
                if (!answer.ByKindWhere.TryGetValue(kind, out var byWhere)) answer.ByKindWhere[kind] = byWhere = new Dictionary<string, GrepTally>(StringComparer.OrdinalIgnoreCase);
                if (!byWhere.TryGetValue(where, out var w)) byWhere[where] = w = new GrepTally();
                w.Add(read, f);
                return false;
            }, s => answer.SearchSeconds = s);
        GrepTail(plan, answer, answer.All.Files, answer.All.Bytes);
        answer.EmptyReason = NoArchives(t) is { } none && plan.LooseFiles == 0 ? none
            : plan.Items.Count == 0 ? $"No file of {t.Shown} is in the scope{(answer.Scope.Length > 0 ? " (" + answer.Scope + ")" : "")}; {Plural(plan.SkippedFiles, "file")} {(plan.SkippedFiles == 1 ? "was" : "were")} passed over as textures, sounds or video."
            : null;
        return answer;
    });

    static string GrepScope(GrepPlan plan)
    {
        var scope = new List<string>();
        if (plan.Kinds != null) scope.Add("files " + string.Join(", ", plan.Kinds.Order(StringComparer.Ordinal)));
        else if (plan.AllKinds) scope.Add("every kind of file");
        if (plan.Folders.Length > 0) scope.Add("under " + string.Join(", ", plan.Folders));
        return string.Join(", ", scope);
    }

    /// <summary>The notes and sources a search's answer starts with.</summary>
    void GrepHead(GrepPlan plan, ReportAnswer answer)
    {
        var t = plan.Target;
        foreach (var n in t.Notes) answer.Notes.Add(n);
        foreach (var n in plan.Notes) answer.Notes.Add(n);
        if (t.Archives.Count <= 6) foreach (var a in t.Archives) answer.Sources.Add(a.SourceLine);
        else answer.Sources.Add($"{N(t.Archives.Count)} archives in {_s.Archives.DataFolder}, {ArchiveStore.SizeText(t.Archives.Sum(a => a.Length))}");
        if (plan.LooseFiles > 0) answer.Sources.Add($"{Plural(plan.LooseFiles, "loose file")} in {_s.Archives.DataFolder}");
    }

    /// <summary>What was read, and the notes on what the scope left out, after a search.</summary>
    static void GrepTail(GrepPlan plan, ReportAnswer answer, long files, long bytes)
    {
        var t = plan.Target;
        answer.Read.Insert(0, $"{Plural(files, "file")} ({ArchiveStore.SizeText(bytes)} inflated)");
        answer.Read.Insert(1, $"the names of {Plural(t.Archives.Count, "archive")} and the file records of {N(plan.Archives)} ({N(plan.ArchivesPassedOver)} passed over by their names) in {plan.ListSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s");
        if (plan.PatternBytes > 0)
            answer.Read.Insert(2, $"a {ArchiveStore.SizeText(plan.PatternBytes)} sample for the {(plan.Options.Regex ? "regular expression's" : "text's")} speed ({(plan.PatternSeconds * 1000).ToString("0", CultureInfo.InvariantCulture)} ms)");
        if (plan.Matcher?.CodesFold == true)
            answer.Notes.Add("The regular expression's characters written as a code (\\xE9) couldn't be made exact, so each also matches the other case of its letter: write (?-i:\\xE9) for one that must not.");
        if (plan.Matcher?.DecodesBytes == true)
            answer.Notes.Add("The text has a character outside ASCII, so an 8-bit file is read as UTF-8 text, and a byte that isn't UTF-8 as Windows-1252 (as some string tables hold): a letter is found in either case and either encoding. Offsets are still bytes of the file.");
        if (plan.Matcher?.TextReason is { } why)
            answer.Notes.Add($"This regular expression is matched on the files read as text (a piece only where it holds the expression's ASCII letters, if it has some), not on their bytes: {why}.");
        if (plan.SkippedFiles > 0)
            answer.Notes.Add($"Passed over as textures, sounds and video: {Plural(plan.SkippedFiles, "file")} ({string.Join(", ", plan.SkippedByKind.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Take(4).Select(k => $"{k.Key} {N(k.Value)}"))}{(plan.SkippedByKind.Count > 4 ? ", …" : "")}). Name a kind with --ext to search it (--ext * searches every kind).");
        if (plan.LooseFiles > 0)
            answer.Notes.Add(plan.Everything
                ? $"The {Plural(plan.LooseFiles, "loose file")} in Data's folders {(plan.LooseFiles == 1 ? "was" : "were")} searched too: the game reads a loose file in place of an archived one with the same path."
                : $"{Plural(plan.LooseFiles, "loose file")} in Data with the path of one of these archives' files {(plan.LooseFiles == 1 ? "was" : "were")} searched too: the game reads {(plan.LooseFiles == 1 ? "it" : "them")} in place of the archived {(plan.LooseFiles == 1 ? "one" : "ones")}.");
        if (plan.TopLevelLeftOut > 0)
            answer.Notes.Add($"{Plural(plan.TopLevelLeftOut, "file")} at Data's own top level {(plan.TopLevelLeftOut == 1 ? "was" : "were")} left out ({string.Join(", ", plan.TopLevelByKind.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Take(5).Select(k => $"{k.Key} {N(k.Value)}"))}{(plan.TopLevelByKind.Count > 5 ? ", …" : "")}): the game reads only plugins and archives there, never a loose file as an asset (an asset's path starts with its folder). What sits there is a mod manager's backups, read-mes and settings.");
    }
}
