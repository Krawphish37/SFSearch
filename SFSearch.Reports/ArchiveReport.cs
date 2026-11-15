// archive: what is inside BA2 archives? A plugin's own archives ("<name> - *.ba2"), one .ba2, or every archive in
// Data. A summary per archive; a list with a pattern; a search for a file across archives; the text of one file;
// and files copied out to a folder. Loose files in Data that the game uses in place of an archived one are noted.
// The archives are read with Mutagen's archive reader, live: nothing about them is stored.
// Nothing is ever written to the Data folder or the game's folder, and a file whose path inside the archive would
// lead outside the target folder is refused.
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SFSearch.Reports;

using static ReportAnswer;

public enum ArchiveMode { Summary, List, Show, Extract }

/// <summary>One archive in a summary.</summary>
public sealed class ArchiveSummary
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public long Length { get; init; }
    public int Files { get; init; }
    /// <summary>File extensions with how many files have each, the most common first.</summary>
    public List<(string Extension, int Count)> Kinds { get; init; } = new();
    /// <summary>Files that also exist as a loose file in Data: the game uses the loose one.</summary>
    public int LooseCopies { get; init; }
    public string? Error { get; init; }
}

/// <summary>One file in a list.</summary>
public sealed class ArchiveFileRow
{
    public required string Archive { get; init; }
    public required string Path { get; init; }
    /// <summary>Bytes. Null for a texture: the archive's table doesn't give a texture's size.</summary>
    public long? Size { get; init; }
    /// <summary>True when Data also holds the path as a loose file: the game uses the loose one.</summary>
    public bool LooseCopy { get; init; }
    /// <summary>The file's extension, lower case with its dot (".nif"); "" for none.</summary>
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
}

/// <summary>
/// The archives a caller named (a plugin's own, one .ba2, or every one in Data) with every file in each: what
/// <c>archive &lt;plugin&gt; list</c> lists, as data for code (the add-on API's <c>GameData.Archives</c>).
/// </summary>
public sealed class ArchiveContents
{
    /// <summary>What was asked about: "darkstar.esm", "all archives in Data", "x.ba2".</summary>
    public required string Target { get; init; }
    /// <summary>The plugin the archives belong to, when a plugin was named.</summary>
    public string? Plugin { get; init; }
    /// <summary>True when the archives sit in the Data folder: only then can a loose file in Data stand in for one of their files.</summary>
    public bool InData { get; init; }
    /// <summary>The archives in name order. An archive that couldn't be read has its <see cref="ArchiveListing.Error"/> and no files.</summary>
    public List<ArchiveListing> Archives { get; } = new();
    /// <summary>Why there are none, an archive that couldn't be read, a plugin file outside Data: one line each.</summary>
    public List<string> Notes { get; } = new();
    public long FileCount => Archives.Sum(a => (long)a.Files.Count);
}

/// <summary>One archive and every file in it, in the archive's own order.</summary>
public sealed class ArchiveListing
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    /// <summary>The archive file's size in bytes.</summary>
    public long Length { get; init; }
    /// <summary>Why it couldn't be read; its file list is then empty.</summary>
    public string? Error { get; init; }
    public List<ArchiveFileRow> Files { get; init; } = new();
}

public sealed class ArchiveAnswer : ReportAnswer
{
    public override string Kind => "archive";
    public ArchiveMode Mode { get; init; }
    /// <summary>What was asked about, for the heading: "darkstar.esm", "all archives in Data", "x.ba2".</summary>
    public required string Target { get; init; }
    public List<ArchiveSummary> Archives { get; } = new();
    public string? Pattern { get; init; }
    /// <summary>How many files the pattern was held against.</summary>
    public long FilesLookedAt { get; set; }
    public List<ArchiveFileRow> Files { get; } = new();

    // show
    public ArchiveFileRow? Shown { get; set; }
    public long ShownBytes { get; set; }
    public bool ShownIsText { get; set; }
    public string? Text { get; set; }

    // extract
    public string? To { get; set; }
    public long ExtractedBytes { get; set; }
    /// <summary>Files not written because their path inside the archive leads outside the target folder.</summary>
    public List<ArchiveFileRow> Refused { get; } = new();

    public long TotalBytes => Archives.Sum(a => a.Length);
    public long TotalFiles => Archives.Sum(a => (long)a.Files);

    protected override void Fill(JsonObject json)
    {
        json["mode"] = Mode.ToString().ToLowerInvariant();
        json["target"] = Target;
        json["archives"] = new JsonArray(Archives.Select(a =>
        {
            var o = new JsonObject { ["archive"] = a.Name, ["files"] = a.Files, ["bytes"] = a.Length };
            o["kinds"] = new JsonObject(a.Kinds.Select(k => new KeyValuePair<string, JsonNode?>(k.Extension.Length == 0 ? "(none)" : k.Extension, k.Count)));
            o["looseCopies"] = a.LooseCopies;
            if (a.Error != null) o["error"] = a.Error;
            return (JsonNode)o;
        }).ToArray());
        if (Pattern != null) json["pattern"] = Pattern;
        if (Mode != ArchiveMode.Summary)
        {
            json["filesLookedAt"] = FilesLookedAt;
            json["files"] = new JsonArray(Files.Select(f => (JsonNode)Row(f)).ToArray());
        }
        if (Shown != null)
        {
            var o = Row(Shown); o["bytes"] = ShownBytes; o["isText"] = ShownIsText;
            if (Text != null) o["text"] = Text;
            json["shown"] = o;
        }
        if (Mode == ArchiveMode.Extract)
        {
            json["to"] = To; json["extracted"] = Files.Count; json["extractedBytes"] = ExtractedBytes;
            json["refused"] = new JsonArray(Refused.Select(f => (JsonNode)Row(f)).ToArray());
        }

        static JsonObject Row(ArchiveFileRow f)
        {
            var o = new JsonObject { ["archive"] = f.Archive, ["file"] = f.Path };
            if (f.Size != null) o["size"] = f.Size;
            if (f.LooseCopy) o["looseCopyInData"] = true;
            return o;
        }
    }

    internal static string Kb(long? bytes) => bytes == null ? "" : SFSearch.Records.Sizes.Text(bytes.Value);

    protected override void Render(ReportBody body, ReportTextOptions o)
    {
        string many = $"{Plural(Archives.Count, "archive")}, {Plural(TotalFiles, "file")}, {ArchiveStore.SizeText(TotalBytes)}";
        switch (Mode)
        {
            case ArchiveMode.Summary:
                body.RowNoun = "archives";
                body.Head.Add($"Archives of {Target}: {many}.");
                if (Archives.Count == 0) break;
                body.Head.Add("| Archive | Files | Size | Kinds | Loose copies in Data |");
                body.Head.Add("| :--- | ---: | ---: | :--- | ---: |");
                foreach (var a in Archives)
                    body.Rows.Add($"| {Cell(a.Name)} | {(a.Error != null ? "couldn't be read: " + Cell(a.Error) : N(a.Files))} | {ArchiveStore.SizeText(a.Length)} | " +
                        $"{string.Join(", ", a.Kinds.Take(o.Detail ? int.MaxValue : 6).Select(k => $"{(k.Extension.Length == 0 ? "(none)" : k.Extension)} {N(k.Count)}"))}{(!o.Detail && a.Kinds.Count > 6 ? ", …" : "")} | {(a.LooseCopies > 0 ? N(a.LooseCopies) : "")} |");
                break;
            case ArchiveMode.List:
                body.RowNoun = "files";
                body.Head.Add($"{(Pattern == null ? "Files" : $"Files matching \"{Pattern}\"")} in {Target} ({many}): {N(Files.Count)}.");
                if (Files.Count > 0)
                {
                    body.Head.Add("| Archive | File | Size | Loose copy in Data |");
                    body.Head.Add("| :--- | :--- | ---: | :--- |");
                }
                foreach (var f in Files) body.Rows.Add($"| {Cell(f.Archive)} | {Cell(f.Path)} | {Kb(f.Size)} | {(f.LooseCopy ? "yes: the game uses the loose file" : "")} |");
                break;
            case ArchiveMode.Show:
                body.RowNoun = "lines";
                body.Head.Add($"{Shown!.Path} ({Shown.Archive}, {N(ShownBytes)} bytes){(Shown.LooseCopy ? ": a loose copy in Data is used by the game in its place" : "")}:");
                if (Text == null) body.Rows.Add("It is a binary file. Copy it out with extract to open it in another tool.");
                else foreach (var line in Text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n')) body.Rows.Add(line);
                break;
            case ArchiveMode.Extract:
                body.RowNoun = "files";
                body.Head.Add($"Extracted {Plural(Files.Count, "file")}, {ArchiveStore.SizeText(ExtractedBytes)}, matching \"{Pattern}\" from {Target} to {To} (their folders inside the archive kept)." +
                    (Refused.Count > 0 ? $" {Plural(Refused.Count, "file")} refused: {(Refused.Count == 1 ? "its path leads" : "their paths lead")} outside that folder." : ""));
                foreach (var f in Refused) body.Rows.Add($"REFUSED {f.Path} ({f.Archive}): its path leads outside the target folder");
                if (o.Detail) foreach (var f in Files) body.Rows.Add($"{f.Path} ({f.Archive})");
                break;
        }
    }
}

/// <summary>The archives a file report reads, and how the answer names them.</summary>
public sealed class ArchiveTarget
{
    public required string Shown { get; init; }
    public List<ArchiveStamp> Archives { get; } = new();
    /// <summary>The plugin the archives belong to, when a plugin was named.</summary>
    public string? Plugin { get; init; }
    /// <summary>True when the archives sit in the Data folder: only then can a loose file in Data stand in for one of their files.</summary>
    public bool InData { get; init; } = true;
    public List<string> Notes { get; } = new();

    /// <summary>Which copies of the archives were read, for an answer's source: each one for up to 6, a count and total size for more.</summary>
    public IEnumerable<string> SourceLines(string what = "archives") =>
        Archives.Count <= 6 ? Archives.Select(a => a.SourceLine)
        : new[] { $"{ReportAnswer.N(Archives.Count)} {what} ({string.Join(", ", Archives.Take(2).Select(a => a.Name))}, …), {ArchiveStore.SizeText(Archives.Sum(a => a.Length))}" };
}

public sealed class ArchiveReport
{
    static readonly HashSet<string> TextKinds = new(StringComparer.OrdinalIgnoreCase)
        { ".mat", ".json", ".txt", ".psc", ".xml", ".ini", ".csv", ".cfg", ".lst", ".html", ".css", ".js", ".bat", ".log", ".md" };

    readonly ReportSession _s;

    public ArchiveReport(ReportSession session) { _s = session; }

    /// <summary>
    /// The archives a caller names: "all" (every archive in Data), a .ba2 (a name in Data, or a path), or a plugin
    /// (a name, or a path): its own archives, "&lt;plugin name&gt; - &lt;part&gt;.ba2". A plugin file outside Data
    /// is matched with the archives in Data that carry its name, unless <paramref name="beside"/> asks for the ones
    /// beside the file.
    /// </summary>
    public static ArchiveTarget Resolve(ReportSession s, string target, bool beside = false)
    {
        var store = s.Archives;
        string text = target.Trim().Trim('"');
        if (text.Length == 0) throw new ReportException("no archive or plugin was named", "give a plugin's name, a .ba2 file, or all");
        if (text.Equals("all", StringComparison.OrdinalIgnoreCase) || text == "*")
        {
            var all = new ArchiveTarget { Shown = "all archives in Data" };
            foreach (var a in store.AllInData()) all.Archives.Add(ArchiveStore.Stamp(a));
            return all;
        }
        bool looksLikePath = text.IndexOfAny(new[] { '\\', '/' }) >= 0 || System.IO.Path.IsPathRooted(text);
        if (text.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase))
        {
            string inData = System.IO.Path.Combine(store.DataFolder, text);
            string path = !looksLikePath && File.Exists(inData) ? inData : System.IO.Path.GetFullPath(text);
            if (!File.Exists(path))
                throw new ReportException(looksLikePath ? $"there is no file {path}" : $"there is no archive named {text} in the Data folder", "give an archive's file name in Data or its full path, a plugin's name, or all");
            bool dataOwn = IsInside(store.DataFolder, path);
            var one = new ArchiveTarget { Shown = System.IO.Path.GetFileName(path) + (dataOwn ? "" : $" ({System.IO.Path.GetDirectoryName(path)})"), InData = dataOwn };
            one.Archives.Add(ArchiveStore.Stamp(path));
            return one;
        }
        // A plugin: by its name (it needn't be a file: its archives are found by the name), or by a path
        string name = System.IO.Path.GetFileName(text);
        string? folder = looksLikePath ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(text)) : null;
        bool outside = folder != null && !string.Equals(folder.TrimEnd('\\'), store.DataFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        if (looksLikePath && !File.Exists(System.IO.Path.GetFullPath(text))) throw new ReportException($"there is no file {System.IO.Path.GetFullPath(text)}", "give a plugin's name in Data or the full path of a plugin file");
        string pluginName = SFSearch.Compare.CompareSession.PluginNameOf(name) ?? name;
        var besideIt = outside ? ArchiveStore.OfPlugin(folder!, pluginName) : new List<string>();
        var t = new ArchiveTarget
        {
            Shown = outside && beside ? $"{pluginName} (archives beside it in {folder})" : pluginName,
            Plugin = pluginName, InData = !(outside && beside),
        };
        foreach (var a in outside && beside ? besideIt : ArchiveStore.OfPlugin(store.DataFolder, pluginName)) t.Archives.Add(ArchiveStore.Stamp(a));
        if (outside && !beside)
            t.Notes.Add($"{name} is a file outside Data. The archives read are the ones in Data named for {pluginName}" +
                (besideIt.Count > 0 ? $"; {Plural(besideIt.Count, "archive")} named for it {(besideIt.Count == 1 ? "sits" : "sit")} beside the file ({string.Join(", ", besideIt.Select(System.IO.Path.GetFileName))}): ask for the ones beside it to read {(besideIt.Count == 1 ? "that" : "those")} instead." : "; none sits beside the file."));
        return t;
    }

    static bool IsInside(string folder, string path) =>
        System.IO.Path.GetFullPath(path).StartsWith(System.IO.Path.GetFullPath(folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>A pattern with * or ? is held against the whole path. Any other text is looked for anywhere in it. Letter case doesn't count.</summary>
    internal static Func<string, bool> Matcher(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return _ => true;
        string p = pattern.Replace('/', '\\');
        if (p.Contains('*') || p.Contains('?'))
        {
            var rx = new Regex("^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return s => rx.IsMatch(s);
        }
        return s => s.Contains(p, StringComparison.OrdinalIgnoreCase);
    }

    ArchiveAnswer Start(ArchiveMode mode, ArchiveTarget t, string? pattern)
    {
        var answer = new ArchiveAnswer { Mode = mode, Target = t.Shown, Pattern = pattern };
        foreach (var n in t.Notes) answer.Notes.Add(n);
        if (t.Archives.Count <= 6) foreach (var a in t.Archives) answer.Sources.Add(a.SourceLine);
        else answer.Sources.Add($"{N(t.Archives.Count)} archives in {_s.Archives.DataFolder}, {ArchiveStore.SizeText(t.Archives.Sum(a => a.Length))}");
        return answer;
    }

    /// <summary>Opens each archive in turn, adds its line to the answer, and hands it on. An archive's file list
    /// is let go again when the lists in memory pass the store's limit, so a walk over every archive stays small.
    /// With nothing to hand on (the summary) only the archive's name table is read (<see cref="ArchiveStore.PathsOf"/>).</summary>
    void Each(ArchiveAnswer answer, ArchiveTarget t, bool kinds, Action<ArchiveInfo>? use = null)
    {
        var store = _s.Archives;
        foreach (var stamp in t.Archives)
        {
            ArchiveInfo? a = use != null ? store.Open(stamp.Path) : null;
            ArchivePaths? names = a == null ? store.PathsOf(stamp.Path) : null;
            ArchiveStamp at = (ArchiveStamp?)a ?? names!;
            string? error = a != null ? a.Error : names!.Error;
            int files = a?.Files.Count ?? names!.Paths.Count;
            if (error != null) answer.Notes.Add($"{at.Name} couldn't be read: {error}.");
            int loose = 0;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (kinds)
                foreach (var path in a?.Files.Select(f => f.Path) ?? names!.Paths)
                {
                    string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                    counts[ext] = counts.GetValueOrDefault(ext) + 1;
                    if (t.InData && store.IsLoose(path)) loose++;
                }
            answer.Archives.Add(new ArchiveSummary
            {
                Name = at.Name, Path = at.Path, Length = at.Length, Files = files, Error = error, LooseCopies = loose,
                Kinds = counts.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Select(k => (k.Key, k.Value)).ToList(),
            });
            if (a != null) use!(a);
        }
        answer.Read.Add($"the file lists of {Plural(t.Archives.Count, "archive")}{(store.ListsRead < t.Archives.Count ? $" ({N(t.Archives.Count - store.ListsRead)} from memory)" : "")}");
    }

    ArchiveFileRow Row(ArchiveTarget t, ArchivedFile f) =>
        new() { Archive = f.Archive.Name, Path = f.Path, Size = f.Size, LooseCopy = t.InData && _s.Archives.IsLoose(f.Path) };

    string? NoArchives(ArchiveTarget t) =>
        t.Archives.Count > 0 ? null
        : t.Plugin != null ? $"{t.Plugin} has no archives: no file named \"{System.IO.Path.GetFileNameWithoutExtension(t.Plugin)} - <part>.ba2\" is in {(t.InData ? _s.Archives.DataFolder : "the folder beside it")}. A plugin without archives uses loose files, its masters' files or the game's."
            + (t.InData && _s.Index.Plugin(t.Plugin) == null ? $" There is no plugin named {t.Plugin} in Data either: check the name." : "")
        : $"There is no archive in {_s.Archives.DataFolder}.";

    /// <summary>One line per archive: how many files, its size, the kinds of file, how many have a loose copy in Data.</summary>
    public ArchiveAnswer Summary(string target, bool beside = false) => _s.RunFiles(() =>
    {
        var t = Resolve(_s, target, beside);
        var answer = Start(ArchiveMode.Summary, t, null);
        Each(answer, t, true);
        answer.EmptyReason = NoArchives(t);
        int loose = answer.Archives.Sum(a => a.LooseCopies);
        if (loose > 0) answer.Notes.Add($"{Plural(loose, "file")} in {(t.Archives.Count == 1 ? "this archive" : "these archives")} also {(loose == 1 ? "exists" : "exist")} as a loose file in Data. The game uses the loose file. List them with a pattern to see which.");
        return answer;
    });

    /// <summary>The files whose path fits a pattern (a glob with * and ?, or a text looked for anywhere in the path). No pattern: every file.</summary>
    public ArchiveAnswer List(string target, string? pattern = null, bool beside = false) => _s.RunFiles(() =>
    {
        var t = Resolve(_s, target, beside);
        var answer = Start(ArchiveMode.List, t, string.IsNullOrEmpty(pattern) ? null : pattern);
        var match = Matcher(pattern);
        Each(answer, t, false, a =>
        {
            foreach (var f in a.Files)
            {
                answer.FilesLookedAt++;
                if (match(f.Path)) answer.Files.Add(Row(t, f));
            }
        });
        answer.EmptyReason = NoArchives(t) ?? (answer.Files.Count == 0 ? $"No file fits \"{pattern}\": {N(answer.FilesLookedAt)} files in {Plural(t.Archives.Count, "archive")} were looked at. A pattern with * or ? must fit the whole path (*.mat, meshes\\*); any other text is looked for anywhere in the path." : null);
        int loose = answer.Files.Count(f => f.LooseCopy);
        if (loose > 0) answer.Notes.Add($"{Plural(loose, "file")} listed also {(loose == 1 ? "exists" : "exist")} as a loose file in Data. The game uses the loose file, not the archived one.");
        if (answer.Files.Any(f => f.Size == null)) answer.Notes.Add("Textures (.dds) are listed without a size: an archive's table doesn't hold a texture's size as a file.");
        return answer;
    });

    /// <summary>
    /// Every file of the archives a caller names, as data: the same archives and rows as <see cref="List"/> with no
    /// pattern (a plugin's own archives by the name rule of <see cref="Resolve"/>), grouped by archive. Nothing is
    /// printed and no file's bytes are read.
    /// </summary>
    public ArchiveContents Contents(string target, bool beside = false)
    {
        _s.Archives.Begin();
        var t = Resolve(_s, target, beside);
        var c = new ArchiveContents { Target = t.Shown, Plugin = t.Plugin, InData = t.InData };
        c.Notes.AddRange(t.Notes);
        foreach (var stamp in t.Archives)
        {
            var a = _s.Archives.Open(stamp.Path);
            if (a.Error != null) c.Notes.Add($"{a.Name} couldn't be read: {a.Error}.");
            c.Archives.Add(new ArchiveListing { Name = a.Name, Path = a.Path, Length = a.Length, Error = a.Error, Files = a.Files.Select(f => Row(t, f)).ToList() });
        }
        if (NoArchives(t) is { } none) c.Notes.Add(none);
        return c;
    }

    /// <summary>The same as <see cref="List"/> over every archive in Data: which archive has this file?</summary>
    public ArchiveAnswer Find(string text, string target = "all") => List(target, text);

    /// <summary>One file's content. A text file (.mat, .psc, .json, …) is given as text; a binary file is only described.</summary>
    public ArchiveAnswer Show(string target, string path, bool beside = false) => _s.RunFiles(() =>
    {
        var t = Resolve(_s, target, beside);
        string want = ArchiveStore.Normalize(path);
        if (want.Length == 0) throw new ReportException("no file was named", "give the file's path inside the archive, as a list shows it");
        ArchivedFile? hit = null;
        foreach (var a in t.Archives) { hit = _s.Archives.Open(a.Path).Find(want); if (hit != null) break; }
        if (hit == null)
        {
            string file = System.IO.Path.GetFileName(want);
            var near = t.Archives.SelectMany(a => _s.Archives.Open(a.Path).Files).Where(f => f.Path.EndsWith("\\" + file, StringComparison.OrdinalIgnoreCase) || f.Path.Equals(file, StringComparison.OrdinalIgnoreCase)).Take(5).Select(f => f.Path).ToList();
            throw new ReportException($"\"{want}\" isn't in {(t.Archives.Count == 0 ? t.Shown + " (it has no archives)" : t.Archives.Count <= 4 ? string.Join(", ", t.Archives.Select(a => a.Name)) : t.Shown)}",
                near.Count > 0 ? $"give the whole path: {string.Join("; ", near)}" : "find it with a list or a find first, and give the path as shown there");
        }
        var answer = Start(ArchiveMode.Show, t, null);
        byte[] bytes;
        try { bytes = hit.Bytes(); }
        catch (Exception e) when (e is IOException or InvalidDataException) { throw new ReportException($"{hit.Path} couldn't be read from {hit.Archive.Name}: {e.Message}", "check the archive: it may be damaged"); }
        answer.Shown = Row(t, hit);
        answer.ShownBytes = bytes.Length;
        answer.ShownIsText = TextKinds.Contains(hit.Extension) || LooksLikeText(bytes);
        if (answer.ShownIsText) answer.Text = new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
        answer.Read.Add($"{hit.Path} from {hit.Archive.Name}");
        return answer;
    });

    static bool LooksLikeText(byte[] b)
    {
        int n = Math.Min(b.Length, 4096);
        for (int i = 0; i < n; i++) if (b[i] < 9) return false;
        return n > 0;
    }

    /// <summary>
    /// Where a file of an archive goes inside a target folder, or null when its path would lead outside that folder
    /// (".." steps, a drive, a rooted path). A path inside an archive is data from whoever made the archive.
    /// </summary>
    internal static string? SafeTarget(string targetFolder, string archivedPath)
    {
        string inside = System.IO.Path.GetFullPath(targetFolder).TrimEnd('\\') + "\\";
        if (archivedPath.Length == 0 || archivedPath.Contains(':') || System.IO.Path.IsPathRooted(archivedPath)) return null;
        string full;
        try { full = System.IO.Path.GetFullPath(System.IO.Path.Combine(inside, archivedPath)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        return full.StartsWith(inside, StringComparison.OrdinalIgnoreCase) && full.Length > inside.Length ? full : null;
    }

    /// <summary>Copies the files that fit a pattern (a glob, or one whole path) into a folder, keeping their folders
    /// inside the archive. Never into the Data folder or the game's folder.</summary>
    public ArchiveAnswer Extract(string target, string pattern, string toFolder, bool beside = false) => _s.RunFiles(() =>
    {
        if (string.IsNullOrWhiteSpace(toFolder)) throw new ReportException("no target folder was named", "say where to put the files: a folder outside the game's folders");
        if (string.IsNullOrWhiteSpace(pattern)) throw new ReportException("no file or pattern was named", "give one file's path, or a pattern such as *.mat or scripts\\*");
        string dest = System.IO.Path.GetFullPath(toFolder.Trim().Trim('"'));
        string? forbidden = _s.ReadOnlyFolderOf(dest);
        if (forbidden != null)
            throw new ReportException($"{dest} is inside {forbidden}, and nothing is ever written to the game's folders", "name a folder outside them");
        var t = Resolve(_s, target, beside);
        var answer = Start(ArchiveMode.Extract, t, pattern);
        answer.To = dest;
        string p = ArchiveStore.Normalize(pattern);
        Func<string, bool> match = p.Contains('*') || p.Contains('?') ? Matcher(p) : s => s.Equals(p, StringComparison.OrdinalIgnoreCase);
        Each(answer, t, false, a =>
        {
            foreach (var f in a.Files)
            {
                answer.FilesLookedAt++;
                if (!match(f.Path)) continue;
                string? outPath = SafeTarget(dest, f.Path);
                if (outPath == null) { answer.Refused.Add(Row(t, f)); continue; }
                byte[] bytes;
                try { bytes = f.Bytes(); }
                catch (Exception e) when (e is IOException or InvalidDataException) { answer.Notes.Add($"{f.Path} couldn't be read from {a.Name}: {e.Message}."); continue; }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outPath)!);
                File.WriteAllBytes(outPath, bytes);
                answer.Files.Add(new ArchiveFileRow { Archive = a.Name, Path = f.Path, Size = bytes.Length, LooseCopy = t.InData && _s.Archives.IsLoose(f.Path) });
                answer.ExtractedBytes += bytes.Length;
            }
        });
        if (answer.Refused.Count > 0)
            answer.Notes.Add($"{Plural(answer.Refused.Count, "file")} {(answer.Refused.Count == 1 ? "was" : "were")} not extracted: {(answer.Refused.Count == 1 ? "its path" : "their paths")} inside the archive (\"..\", a drive, a rooted path) would write outside {dest}. An archive built that way is not to be trusted.");
        if (answer.Files.Count == 0 && answer.Refused.Count == 0)
            answer.EmptyReason = NoArchives(t) ?? $"No file fits \"{pattern}\": {N(answer.FilesLookedAt)} files in {Plural(t.Archives.Count, "archive")} were looked at. Without * or ? the pattern must be one file's whole path.";
        int loose = answer.Files.Count(f => f.LooseCopy);
        if (loose > 0) answer.Notes.Add($"{Plural(loose, "file")} extracted also {(loose == 1 ? "exists" : "exist")} as a loose file in Data. The game uses the loose file; what was extracted is the archived one.");
        return answer;
    });
}
