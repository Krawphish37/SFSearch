// archive encoding: how is a file's text written? (J70, GAPS S94) The files inside BA2 archives, and the loose files in
// Data the game reads in their place, each told as ASCII only, UTF-8, Windows-1252, a mix of the two, UTF-16 (by its
// byte-order mark) or binary; a string table (.strings, .dlstrings, .ilstrings) string by string. A translator or a
// modder asks it before editing a string table or a text file: an editor that opens a Windows-1252 table as UTF-8 (or
// the other way) breaks every letter outside ASCII it saves. Data holds both, even in one file: the game's starfield_en
// tables keep 228 strings in Windows-1252 among UTF-8 ones, DarkStar's German, French and Spanish tables are
// Windows-1252 throughout (J55, notes\fix-j55-grep-case.md).
//
// The scope, the cost said before reading and the reading on all cores are archive2 grep's (ArchiveGrep.cs). Only the
// bytes needed are read: a file is read from its start until it is known, so a binary file stops at its first
// control byte (a mesh at its first bytes); the first piece of a file is 64 KB. Nothing is kept between calls.
using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace SFSearch.Reports;

using static ReportAnswer;

/// <summary>How a file's text is written.</summary>
public enum TextEncoding { Ascii, Utf8, Windows1252, Mixed, Utf16LE, Utf16BE, Binary }

/// <summary>A string of a string table that isn't UTF-8: its ID, where it is in the file, and its text.</summary>
public sealed class EncodedString
{
    public uint Id { get; init; }
    public long Offset { get; init; }
    public TextEncoding Encoding { get; init; }
    /// <summary>The text, read as UTF-8 where it is and as Windows-1252 where it isn't (the first 200 characters).</summary>
    public required string Text { get; init; }
}

/// <summary>A string table's strings by how each is written. A string that is valid UTF-8 counts as UTF-8.</summary>
public sealed class StringTableEncoding
{
    public long Strings, Ascii, Utf8, Windows1252, Mixed;
    /// <summary>The strings that aren't UTF-8 (Windows-1252 or mixed), the first <see cref="ArchiveGrep.KeptHits"/>.</summary>
    public List<EncodedString> NotUtf8 { get; } = new();
}

/// <summary>One file's encoding.</summary>
public sealed class EncodingFile
{
    public required string Where { get; init; }
    public required string Path { get; init; }
    public long Size { get; init; }
    public bool LooseCopy { get; init; }
    public TextEncoding Encoding { get; set; }
    /// <summary>It starts with the UTF-8 byte-order mark (EF BB BF).</summary>
    public bool Utf8Mark { get; set; }
    /// <summary>Characters outside ASCII written as valid UTF-8 (in a UTF-16 file: its characters outside ASCII).</summary>
    public long OutsideAscii { get; set; }
    /// <summary>Bytes outside ASCII that aren't part of a valid UTF-8 character: read as Windows-1252.</summary>
    public long NotUtf8 { get; set; }
    /// <summary>The first such byte, and the text around it.</summary>
    public long? FirstNotUtf8 { get; set; }
    public string? NotUtf8Around { get; set; }
    /// <summary>It starts with a UTF-16 byte-order mark (FF FE or FE FF).</summary>
    public bool Utf16Mark { get; set; }
    /// <summary>A binary file: where its first control character is (a byte, or a UTF-16 unit), and its value; -1 for a
    /// UTF-16 file's odd last byte.</summary>
    public long? ControlAt { get; set; }
    public int Control { get; set; }
    /// <summary>A string table, string by string; null for any other file, or one whose layout isn't a string table's.</summary>
    public StringTableEncoding? Strings { get; set; }
    /// <summary>A .strings, .dlstrings or .ilstrings file whose layout isn't a string table's: read as bytes.</summary>
    public bool NotATable { get; set; }

    internal static string Name(TextEncoding e) => e switch
    {
        TextEncoding.Ascii => "ASCII only", TextEncoding.Utf8 => "UTF-8", TextEncoding.Windows1252 => "Windows-1252",
        TextEncoding.Mixed => "mixed UTF-8 and Windows-1252", TextEncoding.Utf16LE => "UTF-16 LE", TextEncoding.Utf16BE => "UTF-16 BE", _ => "binary",
    };

    internal static string JsonName(TextEncoding e) => e switch
    {
        TextEncoding.Ascii => "ascii", TextEncoding.Utf8 => "utf-8", TextEncoding.Windows1252 => "windows-1252",
        TextEncoding.Mixed => "mixed", TextEncoding.Utf16LE => "utf-16le", TextEncoding.Utf16BE => "utf-16be", _ => "binary",
    };

    /// <summary>What the file holds, in a sentence: the counts, the first byte that isn't UTF-8, the control byte.</summary>
    internal string What()
    {
        string table = Strings is { } s
            ? $"a string table of {Plural(s.Strings, "string")}: {string.Join(", ", new[] { (s.Ascii, "ASCII"), (s.Utf8, "UTF-8"), (s.Windows1252, "Windows-1252"), (s.Mixed, "mixed") }.Where(x => x.Item1 > 0).Select(x => $"{N(x.Item1)} {x.Item2}"))}"
            : NotATable ? "its layout isn't a string table's, so its bytes were read as they are; " : "";
        if (Strings != null) return table + (FirstNotUtf8 != null ? $"; the first byte that isn't UTF-8 at {N(FirstNotUtf8.Value)}" : "");
        string first = FirstNotUtf8 != null ? $" (the first at {N(FirstNotUtf8.Value)}: {NotUtf8Around})" : "";
        return table + Encoding switch
        {
            TextEncoding.Ascii => Utf8Mark ? "a UTF-8 byte-order mark, then no byte outside ASCII" : "no byte outside ASCII",
            TextEncoding.Utf8 => $"{(Utf8Mark ? "a UTF-8 byte-order mark; " : "")}{Plural(OutsideAscii, "character")} outside ASCII, all valid UTF-8",
            TextEncoding.Windows1252 => $"{Plural(NotUtf8, "byte")} outside ASCII, none of them UTF-8: read as Windows-1252{first}",
            TextEncoding.Mixed => $"{(Utf8Mark ? "a UTF-8 byte-order mark; " : "")}{Plural(OutsideAscii, "UTF-8 character")} and {Plural(NotUtf8, "byte")} that {(NotUtf8 == 1 ? "isn't" : "aren't")} UTF-8, read as Windows-1252{first}",
            TextEncoding.Utf16LE or TextEncoding.Utf16BE => $"a UTF-16 byte-order mark; {Plural(OutsideAscii, "character")} outside ASCII",
            _ => ControlAt == null ? "" : (Control < 0 ? "a UTF-16 byte-order mark, but an odd last byte" : Utf16Mark ? $"a UTF-16 byte-order mark, but the control character U+{Control:X4}" : $"the control byte {Control:X2}") + $" at {N(ControlAt.Value)}: not text (reading stopped there)",
        };
    }
}

/// <summary>Files by encoding, and a string table's strings: over a scope, a kind of file, or a kind in one archive.</summary>
public sealed class EncodingTally
{
    public long Files, Bytes;
    /// <summary>Files by <see cref="TextEncoding"/>; a file that couldn't be read is in none.</summary>
    public long[] ByEncoding { get; } = new long[7];
    public long Strings, StringsAscii, StringsUtf8, Strings1252, StringsMixed;

    internal void Add(long read, EncodingFile? f)
    {
        Files++; Bytes += read;
        if (f == null) return;
        ByEncoding[(int)f.Encoding]++;
        if (f.Strings is { } s) { Strings += s.Strings; StringsAscii += s.Ascii; StringsUtf8 += s.Utf8; Strings1252 += s.Windows1252; StringsMixed += s.Mixed; }
    }

    long Utf16 => ByEncoding[(int)TextEncoding.Utf16LE] + ByEncoding[(int)TextEncoding.Utf16BE];

    /// <summary>"9,398 ASCII only, 21 UTF-8, 9 UTF-16 LE, 5,609 binary": the encodings that have a file.</summary>
    internal string Sentence() => ByEncoding.All(n => n == 0) ? "none read"
        : string.Join(", ", Enum.GetValues<TextEncoding>().Where(e => ByEncoding[(int)e] > 0).Select(e => $"{N(ByEncoding[(int)e])} {EncodingFile.Name(e)}"));

    internal string? StringSentence() => Strings == 0 ? null
        : $"The string tables' {Plural(Strings, "string")}: {N(StringsAscii)} ASCII, {N(StringsUtf8)} UTF-8, {N(Strings1252)} Windows-1252{(StringsMixed > 0 ? $", {N(StringsMixed)} mixed" : "")} (a string that is valid UTF-8 counts as UTF-8).";

    internal JsonObject Json()
    {
        var o = new JsonObject { ["filesRead"] = Files, ["bytesRead"] = Bytes };
        foreach (var e in Enum.GetValues<TextEncoding>()) o[EncodingFile.JsonName(e)] = ByEncoding[(int)e];
        if (Strings > 0) o["strings"] = new JsonObject { ["strings"] = Strings, ["ascii"] = StringsAscii, ["utf-8"] = StringsUtf8, ["windows-1252"] = Strings1252, ["mixed"] = StringsMixed };
        return o;
    }

    internal string Cells() => $"{N(Files)} | {N(ByEncoding[0])} | {N(ByEncoding[1])} | {N(ByEncoding[2])} | {N(ByEncoding[3])} | {N(Utf16)} | {N(ByEncoding[6])} | {(Strings > 0 ? $"{N(StringsUtf8)} / {N(Strings1252 + StringsMixed)}" : "")} |";
}

/// <summary>What every encoding answer says: the rule a file is told by.</summary>
static class EncodingRule
{
    internal const string Note = "How a file is told: a control byte (any below 20 but tab, line feed, form feed and carriage return, or 7F) makes it binary, and its reading stops there; a byte-order mark FF FE or FE FF makes it UTF-16; otherwise each byte outside ASCII is read as UTF-8 where it is part of a valid UTF-8 character, and as Windows-1252 where it isn't. A string table (.strings, .dlstrings, .ilstrings) is told string by string.";
}

/// <summary><c>archive2 &lt;target&gt; encoding</c>: the files whose text isn't ASCII only (every file with --detail), each
/// with its encoding; how many files of each encoding were read. Pages by the files read, as grep does.</summary>
public sealed class EncodingAnswer : ReportAnswer, ISearchPage
{
    public override string Kind => "archive";
    public required string Target { get; init; }
    public string Scope { get; init; } = "";
    public long ScopeFiles { get; init; }
    public long ScopeBytes { get; init; }
    public int Archives { get; init; }
    public int LooseFiles { get; init; }
    public long FirstItem { get; init; }
    public long ItemsSearched => All.Files;
    public long ItemsTotal => ScopeFiles;
    public long? NextItem { get; set; }
    public string ItemNoun => "files read";
    public long EstimateFiles { get; init; }
    public long EstimateBytes { get; init; }
    public double EstimateSeconds { get; init; }
    public double SearchSeconds { get; set; }
    /// <summary>Every file read, by encoding.</summary>
    public EncodingTally All { get; } = new();
    /// <summary>The files listed: those with text outside ASCII (UTF-8, Windows-1252, mixed, UTF-16), every one with detail.</summary>
    public List<EncodingFile> Files { get; } = new();
    public bool Detail { get; init; }

    protected override void Fill(JsonObject json)
    {
        json["mode"] = "encoding";
        json["target"] = Target;
        if (Scope.Length > 0) json["scope"] = Scope;
        json["filesInScope"] = ScopeFiles;
        json["bytesInScope"] = ScopeBytes;
        json["archives"] = Archives;
        json["looseFiles"] = LooseFiles;
        json["readFrom"] = FirstItem + 1;
        json["readTo"] = FirstItem + ItemsSearched;
        if (NextItem != null) json["nextFile"] = NextItem + 1;
        json["estimate"] = ArchiveGrep.EstimateJson(EstimateFiles, EstimateBytes, EstimateSeconds, 0, 0);
        json["searchSeconds"] = Math.Round(SearchSeconds, 2);
        json["read"] = All.Json();
        json["files"] = new JsonArray(Files.Select(f => (JsonNode)FileJson(f)).ToArray());
    }

    internal static JsonObject FileJson(EncodingFile f)
    {
        var o = new JsonObject { ["where"] = f.Where, ["file"] = f.Path, ["size"] = f.Size, ["encoding"] = EncodingFile.JsonName(f.Encoding) };
        if (f.LooseCopy) o["looseCopyInData"] = true;
        if (f.Utf8Mark) o["utf8Mark"] = true;
        if (f.Utf16Mark) o["utf16Mark"] = true;
        o["outsideAscii"] = f.OutsideAscii;
        o["notUtf8"] = f.NotUtf8;
        if (f.FirstNotUtf8 != null) o["firstNotUtf8"] = new JsonObject { ["offset"] = f.FirstNotUtf8, ["around"] = f.NotUtf8Around };
        if (f.ControlAt != null) o["control"] = new JsonObject { ["offset"] = f.ControlAt, ["value"] = f.Control };
        if (f.NotATable) o["notAStringTable"] = true;
        if (f.Strings is { } s)
            o["strings"] = new JsonObject
            {
                ["strings"] = s.Strings, ["ascii"] = s.Ascii, ["utf-8"] = s.Utf8, ["windows-1252"] = s.Windows1252, ["mixed"] = s.Mixed,
                ["notUtf8"] = new JsonArray(s.NotUtf8.Select(x => (JsonNode)new JsonObject { ["id"] = x.Id, ["offset"] = x.Offset, ["encoding"] = EncodingFile.JsonName(x.Encoding), ["text"] = x.Text }).ToArray()),
            };
        return o;
    }

    protected override void Render(ReportBody body, ReportTextOptions o)
    {
        body.RowNoun = "files";
        string scope = $"{Plural(ScopeFiles, "file")}, {ArchiveStore.SizeText(ScopeBytes)}, in {Plural(Archives, "archive")}" + (LooseFiles > 0 ? $" and {Plural(LooseFiles, "loose file")}" : "");
        bool all = FirstItem == 0 && NextItem == null;
        string where = $"the files of {Target}{(Scope.Length > 0 ? " (" + Scope + ")" : "")} ({scope})";
        body.Head.Add(all
            ? $"How {where} are written: {All.Sentence()}."
            : $"How {where} are written, files {N(FirstItem + 1)} to {N(FirstItem + ItemsSearched)}{(NextItem != null ? " (then the page was full)" : "")}: {All.Sentence()}.");
        if (All.StringSentence() is { } strings) body.Head.Add(strings);
        body.Head.Add(ArchiveGrep.EstimateLine(EstimateFiles, EstimateBytes, EstimateSeconds, 0, 0, false) + $" The reading took {GrepAnswer.Time(SearchSeconds)} for {Plural(ItemsSearched, "file")}, {ArchiveStore.SizeText(All.Bytes)}.");
        if (Files.Count == 0) return;
        body.Head.Add("| Where | File | Encoding | What it holds |");
        body.Head.Add("| :--- | :--- | :--- | :--- |");
        int show = o.Detail ? int.MaxValue : 3;
        foreach (var f in Files)
        {
            string at = f.Where + (f.LooseCopy ? " (the game uses the loose file)" : "");
            var rows = new StringBuilder($"| {Cell(at)} | {Cell(f.Path)} | {EncodingFile.Name(f.Encoding)} | {Cell(f.What())} |");
            if (f.Strings is { } s)
            {
                foreach (var x in s.NotUtf8.Take(show))
                    rows.Append($"\n| {Cell(at)} | {Cell(f.Path)} | {EncodingFile.Name(x.Encoding)} | string {x.Id:X8} at {N(x.Offset)}: «{Cell(x.Text)}» |");
                long more = s.Windows1252 + s.Mixed - Math.Min(show, s.NotUtf8.Count);
                if (more > 0) rows.Append($"\n| {Cell(at)} | {Cell(f.Path)} | | {Plural(more, "more string")} not in UTF-8{(o.Detail ? $" (only the first {N(ArchiveGrep.KeptHits)} are kept)" : "; --detail lists them")} |");
            }
            body.Rows.Add(rows.ToString());
        }
    }
}

/// <summary><c>archive2 &lt;target&gt; encoding --count</c>: files by encoding per kind of file (and per archive with
/// --detail), over the whole scope.</summary>
public sealed class EncodingCountAnswer : ReportAnswer
{
    public override string Kind => "archive";
    public required string Target { get; init; }
    public string Scope { get; init; } = "";
    public long ScopeFiles { get; init; }
    public long ScopeBytes { get; init; }
    public int Archives { get; init; }
    public int LooseFiles { get; init; }
    public long EstimateFiles { get; init; }
    public long EstimateBytes { get; init; }
    public double EstimateSeconds { get; init; }
    public double SearchSeconds { get; set; }
    public EncodingTally All { get; } = new();
    /// <summary>Per extension (".txt"; "(none)").</summary>
    public Dictionary<string, EncodingTally> ByKind { get; } = new(StringComparer.Ordinal);
    /// <summary>Per extension, then per archive ("loose" for the loose files).</summary>
    public Dictionary<string, Dictionary<string, EncodingTally>> ByKindWhere { get; } = new(StringComparer.Ordinal);

    IEnumerable<KeyValuePair<string, EncodingTally>> Kinds => ByKind.OrderByDescending(k => k.Value.Files).ThenBy(k => k.Key, StringComparer.Ordinal);
    IEnumerable<KeyValuePair<string, EncodingTally>> Where(string kind) => ByKindWhere.TryGetValue(kind, out var w)
        ? w.OrderByDescending(x => x.Value.Files).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase) : Enumerable.Empty<KeyValuePair<string, EncodingTally>>();

    protected override void Fill(JsonObject json)
    {
        json["mode"] = "encoding-count";
        json["target"] = Target;
        if (Scope.Length > 0) json["scope"] = Scope;
        json["filesInScope"] = ScopeFiles;
        json["bytesInScope"] = ScopeBytes;
        json["archives"] = Archives;
        json["looseFiles"] = LooseFiles;
        json["estimate"] = ArchiveGrep.EstimateJson(EstimateFiles, EstimateBytes, EstimateSeconds, 0, 0);
        json["searchSeconds"] = Math.Round(SearchSeconds, 2);
        json["read"] = All.Json();
        json["kinds"] = new JsonArray(Kinds.Select(k =>
        {
            var o = k.Value.Json();
            o["kind"] = k.Key;
            o["where"] = new JsonArray(Where(k.Key).Select(w => { var x = w.Value.Json(); x["where"] = w.Key; return (JsonNode)x; }).ToArray());
            return (JsonNode)o;
        }).ToArray());
    }

    protected override void Render(ReportBody body, ReportTextOptions o)
    {
        body.RowNoun = "kinds";
        string scope = $"{Plural(ScopeFiles, "file")}, {ArchiveStore.SizeText(ScopeBytes)}, in {Plural(Archives, "archive")}" + (LooseFiles > 0 ? $" and {Plural(LooseFiles, "loose file")}" : "");
        body.Head.Add($"How the files of {Target}{(Scope.Length > 0 ? " (" + Scope + ")" : "")} ({scope}) are written: {All.Sentence()}; {Plural(ByKind.Count, "kind")} of file.");
        if (All.StringSentence() is { } strings) body.Head.Add(strings);
        body.Head.Add(ArchiveGrep.EstimateLine(EstimateFiles, EstimateBytes, EstimateSeconds, 0, 0, false) + $" The reading took {GrepAnswer.Time(SearchSeconds)} for {Plural(All.Files, "file")}, {ArchiveStore.SizeText(All.Bytes)}.");
        if (ByKind.Count == 0) return;
        const string columns = "Files | ASCII only | UTF-8 | Windows-1252 | Mixed | UTF-16 | Binary | Strings UTF-8 / not |";
        body.Head.Add(o.Detail ? "| Kind | Where | " + columns : "| Kind | " + columns);
        body.Head.Add(o.Detail ? "| :--- | :--- |" + string.Concat(Enumerable.Repeat(" ---: |", 8)) : "| :--- |" + string.Concat(Enumerable.Repeat(" ---: |", 8)));
        foreach (var (kind, t) in Kinds)
        {
            if (!o.Detail) { body.Rows.Add($"| {Cell(kind)} | {t.Cells()}"); continue; }
            var rows = new StringBuilder($"| {Cell(kind)} | (every archive) | {t.Cells()}");
            foreach (var (where, w) in Where(kind)) rows.Append($"\n| {Cell(kind)} | {Cell(where)} | {w.Cells()}");
            body.Rows.Add(rows.ToString());
        }
    }
}

public static partial class ArchiveGrep
{
    /// <summary>The first bytes of a file read to tell its encoding: a binary file nearly always shows a control byte in
    /// them, so the rest isn't read. Then 4 MB pieces.</summary>
    internal const int FirstBytes = 64 << 10;

    internal static bool IsStringTable(string ext) => ext is ".strings" or ".dlstrings" or ".ilstrings";

    /// <summary>The bytes the encoding reads of a file, for the estimate: a text kind or a string table whole, any other
    /// kind its first 64 KB (a binary file stops there).</summary>
    internal static long EncodingReads(string path, long size) => TextKinds.Contains(Ext(path)) || IsStringTable(Ext(path)) ? size : Math.Min(size, FirstBytes);

    /// <summary>One file's encoding (<see cref="EncodingFile"/>), read from its start until it is known; null when it
    /// couldn't be read.</summary>
    internal static EncodingFile? ReadEncoding(GrepPlan plan, GrepPlan.Item it, Buffers buf, List<string> errors, out long read)
    {
        read = 0;
        string where = it.Source < 0 ? "loose" : plan.Sources[it.Source].Name;
        var f = new EncodingFile { Where = where, Path = it.Path, Size = it.Size, LooseCopy = it.LooseCopy };
        string ext = Ext(it.Path);
        Stream? stream = null;
        try
        {
            stream = Open(plan, it);
            if (IsStringTable(ext) && it.Size is >= 8 and < 1L << 30)
            {
                var all = new byte[it.Size];
                int n = stream.ReadAtLeast(all, all.Length, throwOnEndOfStream: false);
                read = n;
                if (StringTable(all.AsSpan(0, n), ext != ".strings", f, buf)) return f;
                f.NotATable = true;
                Classify(all.AsSpan(0, n), f, buf);
                return f;
            }
            int keep = 0, mode = 0; // 0: 8-bit, 1: UTF-16 LE, 2: UTF-16 BE
            long pos = 0;
            bool first = true;
            while (true)
            {
                int want = first ? FirstBytes : Piece;
                int n = stream.ReadAtLeast(buf.Bytes.AsSpan(keep, want), want, throwOnEndOfStream: false);
                read += n;
                int len = keep + n, start = 0;
                bool last = n < want;
                var bytes = buf.Bytes.AsSpan(0, len);
                if (first)
                {
                    first = false;
                    if (len >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { f.Utf8Mark = true; start = 3; }
                    // FF FE 00 00 is UTF-32's mark: its zeros make it binary
                    else if (len >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE && !(len >= 4 && bytes[2] == 0 && bytes[3] == 0)) { mode = 1; start = 2; f.Utf16Mark = true; }
                    else if (len >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { mode = 2; start = 2; f.Utf16Mark = true; }
                }
                int used = mode == 0 ? Scan8(bytes[start..], pos + start, last, f, buf) : Scan16(bytes[start..], pos + start, last, mode == 2, f);
                if (used < 0 || last) break;
                used += start;
                keep = len - used;
                bytes[used..].CopyTo(buf.Bytes);
                pos += used;
            }
            f.Encoding = f.ControlAt != null ? TextEncoding.Binary : mode == 1 ? TextEncoding.Utf16LE : mode == 2 ? TextEncoding.Utf16BE : Decide(f.OutsideAscii, f.NotUtf8, f.Utf8Mark);
            return f;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotImplementedException or ArgumentException or OverflowException or ObjectDisposedException)
        {
            lock (errors) errors.Add($"{it.Path} ({where}): {e.Message.Split('\n')[0].TrimEnd('.')}");
            return null;
        }
        finally { stream?.Dispose(); }
    }

    static TextEncoding Decide(long outside, long notUtf8, bool mark) =>
        notUtf8 == 0 ? (outside > 0 || mark ? TextEncoding.Utf8 : TextEncoding.Ascii) : (outside > 0 || mark ? TextEncoding.Mixed : TextEncoding.Windows1252);

    /// <summary>A whole file's bytes (a .strings file that isn't a string table), told as any 8-bit file.</summary>
    static void Classify(ReadOnlySpan<byte> bytes, EncodingFile f, Buffers buf)
    {
        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        f.Utf8Mark = start == 3;
        Scan8(bytes[start..], start, true, f, buf);
        f.Encoding = f.ControlAt != null ? TextEncoding.Binary : Decide(f.OutsideAscii, f.NotUtf8, f.Utf8Mark);
    }

    /// <summary>
    /// 8-bit bytes (at <paramref name="pos"/> in the file): the characters outside ASCII that are valid UTF-8, the bytes
    /// that aren't (the first with its text), a control byte. Returns the bytes used (a character that may go on in
    /// the next piece is left), or -1 at a control byte: the file is binary.
    /// </summary>
    static int Scan8(ReadOnlySpan<byte> b, long pos, bool last, EncodingFile f, Buffers buf)
    {
        int i = 0, n = b.Length;
        while (i < n)
        {
            int k = b[i..].IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E);
            if (k < 0) return n;
            i += k;
            byte x = b[i];
            if (x < 0x80)
            {
                if (x is 9 or 10 or 12 or 13) { i++; continue; }
                f.ControlAt = pos + i; f.Control = x;
                return -1;
            }
            if (!last && n - i < 4) return i;
            int length = x is >= 0xC2 and <= 0xF4 ? Utf8Length(b, i) : 0;
            if (length > 0) { f.OutsideAscii++; i += length; continue; }
            if (f.NotUtf8++ == 0) { f.FirstNotUtf8 = pos + i; f.NotUtf8Around = AroundByte(b, i, buf); }
            i++;
        }
        return n;
    }

    /// <summary>UTF-16 units after the mark: those outside ASCII, a control character (then -1: binary). Returns the bytes used.</summary>
    static int Scan16(ReadOnlySpan<byte> b, long pos, bool last, bool bigEndian, EncodingFile f)
    {
        int n = b.Length & ~1;
        for (int i = 0; i < n; i += 2)
        {
            int u = bigEndian ? b[i] << 8 | b[i + 1] : b[i] | b[i + 1] << 8;
            if (u >= 0x20 && u != 0x7F) { if (u >= 0x80) f.OutsideAscii++; continue; }
            if (u is 9 or 10 or 12 or 13) continue;
            f.ControlAt = pos + i; f.Control = u;
            return -1;
        }
        // A last odd byte: not UTF-16
        if (last && n < b.Length) { f.ControlAt = pos + n; f.Control = -1; return -1; }
        return n;
    }

    /// <summary>The text around byte <paramref name="i"/> (up to 60 bytes each side), read as text, the byte in « ».</summary>
    static string AroundByte(ReadOnlySpan<byte> b, int i, Buffers buf)
    {
        int lo = Math.Max(0, i - 60), hi = Math.Min(b.Length, i + 61);
        // Not from inside a UTF-8 character
        while (lo > 0 && lo < i && (b[lo] & 0xC0) == 0x80) lo--;
        int count = ReadText(b[lo..hi], buf.Chars, buf.Map);
        int at = buf.Map.CharOf(i - lo);
        return Around(new ReadOnlySpan<char>(buf.Chars, 0, count), at, 1, true);
    }

    /// <summary>
    /// A string table, string by string: a count and the data's size, then an ID and an offset per string, then the
    /// strings: in a .strings file each ends with a zero byte, in a .dlstrings or .ilstrings file each starts with its
    /// length (<paramref name="lengths"/>). False when the bytes aren't laid out so.
    /// </summary>
    static bool StringTable(ReadOnlySpan<byte> b, bool lengths, EncodingFile f, Buffers buf)
    {
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(b), size = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
        long data = 8 + 8L * count, end = data + size;
        if (end > b.Length) return false;
        var t = new StringTableEncoding();
        long outside = 0, notUtf8 = 0;
        long? firstBad = null;
        for (int k = 0; k < count; k++)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(b[(8 + 8 * k)..]);
            long p = data + BinaryPrimitives.ReadUInt32LittleEndian(b[(12 + 8 * k)..]);
            if (p >= end) return false;
            ReadOnlySpan<byte> s;
            if (lengths)
            {
                if (p + 4 > end) return false;
                long length = BinaryPrimitives.ReadUInt32LittleEndian(b[(int)p..]);
                p += 4;
                if (p + length > end) return false;
                s = b.Slice((int)p, (int)length).TrimEnd((byte)0);
            }
            else
            {
                int zero = b[(int)p..(int)end].IndexOf((byte)0);
                if (zero < 0) return false;
                s = b.Slice((int)p, zero);
            }
            t.Strings++;
            int i = s.IndexOfAnyExceptInRange((byte)0, (byte)0x7F);
            if (i < 0) { t.Ascii++; continue; }
            long good = 0, bad = 0;
            while (i < s.Length)
            {
                if (s[i] < 0x80) { i++; continue; }
                int length = s[i] is >= 0xC2 and <= 0xF4 ? Utf8Length(s, i) : 0;
                if (length > 0) { good++; i += length; continue; }
                if (bad++ == 0) firstBad ??= p + i;
                i++;
            }
            outside += good; notUtf8 += bad;
            if (bad == 0) { t.Utf8++; continue; }
            var e = good > 0 ? TextEncoding.Mixed : TextEncoding.Windows1252;
            if (e == TextEncoding.Mixed) t.Mixed++; else t.Windows1252++;
            if (t.NotUtf8.Count < KeptHits)
            {
                var text = s[..Math.Min(s.Length, 600)];
                int c = ReadText(text, buf.Chars, buf.Map);
                string read = Decode(new ReadOnlySpan<char>(buf.Chars, 0, c), true);
                t.NotUtf8.Add(new EncodedString { Id = id, Offset = p, Encoding = e, Text = read.Length > 200 ? read[..200] + "…" : read });
            }
        }
        f.Strings = t;
        f.OutsideAscii = outside; f.NotUtf8 = notUtf8; f.FirstNotUtf8 = firstBad;
        f.Encoding = t.Windows1252 + t.Mixed == 0 ? (t.Utf8 > 0 ? TextEncoding.Utf8 : TextEncoding.Ascii) : t.Utf8 + t.Mixed == 0 ? TextEncoding.Windows1252 : TextEncoding.Mixed;
        return true;
    }
}

public sealed partial class ArchiveReport
{
    /// <summary>
    /// Reads the files of a plan (<see cref="PlanEncodings"/>) from its start until the page holds about
    /// <paramref name="maxChars"/> characters of rows: each file's encoding. Lists the files with text outside ASCII,
    /// every file with <paramref name="detail"/>. The caller disposes the plan.
    /// </summary>
    public EncodingAnswer Encodings(GrepPlan plan, int maxChars = 24000, bool detail = false) => _s.RunFiles(() =>
    {
        var t = plan.Target;
        var answer = new EncodingAnswer
        {
            Target = t.Shown, Scope = GrepScope(plan), ScopeFiles = plan.Items.Count, ScopeBytes = plan.Items.Sum(i => i.Size), Archives = plan.Archives, LooseFiles = plan.LooseFiles,
            FirstItem = plan.Start, EstimateFiles = plan.Files, EstimateBytes = plan.Bytes, EstimateSeconds = plan.Seconds, Detail = detail,
        };
        GrepHead(plan, answer);
        int perFile = detail ? int.MaxValue : 3;
        long used = 0;
        if (plan.Start < plan.Items.Count)
            ArchiveGrep.Run<EncodingFile>(plan, answer, (it, b, errors) => (ArchiveGrep.ReadEncoding(plan, it, b, errors, out long read), read), (i, f, read) =>
            {
                answer.All.Add(read, f);
                if (f == null || !detail && f.Encoding is TextEncoding.Ascii or TextEncoding.Binary) return false;
                answer.Files.Add(f);
                used += f.Path.Length + f.Where.Length + 120 + (f.Strings?.NotUtf8.Take(perFile).Sum(x => x.Text.Length + f.Path.Length + f.Where.Length + 40) ?? 0);
                if (used >= maxChars && i + 1 < plan.Items.Count) { answer.NextItem = i + 1; return true; }
                return false;
            }, s => answer.SearchSeconds = s);
        GrepTail(plan, answer, answer.All.Files, answer.All.Bytes);
        answer.Notes.Add(EncodingRule.Note);
        answer.EmptyReason = NoArchives(t) is { } none && plan.LooseFiles == 0 ? none
            : plan.Items.Count == 0 ? $"No file of {t.Shown} is in the scope{(answer.Scope.Length > 0 ? " (" + answer.Scope + ")" : "")}; {Plural(plan.SkippedFiles, "file")} {(plan.SkippedFiles == 1 ? "was" : "were")} passed over as textures, sounds or video."
            : plan.Start >= plan.Items.Count ? $"The cursor is past the last of the {Plural(plan.Items.Count, "file")}: nothing is left to read."
            : answer.Files.Count == 0 ? $"No file read holds text outside ASCII: {answer.All.Sentence()}. --detail lists every file."
            : null;
        return answer;
    });

    /// <summary>The files of a plan by encoding, per kind of file and per archive, over its whole scope.</summary>
    public EncodingCountAnswer EncodingsCount(GrepPlan plan) => _s.RunFiles(() =>
    {
        var t = plan.Target;
        var answer = new EncodingCountAnswer
        {
            Target = t.Shown, Scope = GrepScope(plan), ScopeFiles = plan.Items.Count, ScopeBytes = plan.Items.Sum(i => i.Size), Archives = plan.Archives, LooseFiles = plan.LooseFiles,
            EstimateFiles = plan.Files, EstimateBytes = plan.Bytes, EstimateSeconds = plan.Seconds,
        };
        GrepHead(plan, answer);
        if (plan.Items.Count > 0)
            ArchiveGrep.Run<EncodingFile>(plan, answer, (it, b, errors) => (ArchiveGrep.ReadEncoding(plan, it, b, errors, out long read), read), (i, f, read) =>
            {
                var it = plan.Items[i];
                string ext = ArchiveGrep.Ext(it.Path), kind = ext.Length > 0 ? ext : "(none)", where = it.Source < 0 ? "loose" : plan.Sources[it.Source].Name;
                answer.All.Add(read, f);
                if (!answer.ByKind.TryGetValue(kind, out var k)) answer.ByKind[kind] = k = new EncodingTally();
                k.Add(read, f);
                if (!answer.ByKindWhere.TryGetValue(kind, out var byWhere)) answer.ByKindWhere[kind] = byWhere = new Dictionary<string, EncodingTally>(StringComparer.OrdinalIgnoreCase);
                if (!byWhere.TryGetValue(where, out var w)) byWhere[where] = w = new EncodingTally();
                w.Add(read, f);
                return false;
            }, s => answer.SearchSeconds = s);
        GrepTail(plan, answer, answer.All.Files, answer.All.Bytes);
        answer.Notes.Add(EncodingRule.Note);
        answer.EmptyReason = NoArchives(t) is { } none && plan.LooseFiles == 0 ? none
            : plan.Items.Count == 0 ? $"No file of {t.Shown} is in the scope{(answer.Scope.Length > 0 ? " (" + answer.Scope + ")" : "")}; {Plural(plan.SkippedFiles, "file")} {(plan.SkippedFiles == 1 ? "was" : "were")} passed over as textures, sounds or video."
            : null;
        return answer;
    });
}
