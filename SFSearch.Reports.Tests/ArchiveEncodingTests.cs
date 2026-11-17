// Run as part of archive-grep (ArchiveGrepTests.Grep):
//  - J69 (GAPS S93): a character written as a code in --regex (\xC3) matches only itself while letter case is ignored;
//    the letters around it still ignore case. Codes outside and inside classes, ranges, negated classes, subtractions,
//    octal codes in a class, comments, \x next to letters.
//  - J70 (GAPS S94): archive2 encoding. A made archive and loose files with a file of each encoding (ASCII, UTF-8 with
//    and without its mark, Windows-1252, mixed, UTF-16 LE and BE, binary, a control byte past the first 64 KB, a big
//    UTF-8 file with characters across the cuts of the pieces) and string tables (.strings, .dlstrings, an empty
//    .ilstrings, one whose layout is broken): each file's encoding, counts and first byte that isn't UTF-8 at its offset;
//    each string's encoding; the list, --detail, --count, the pages, the estimate.
//  - The game: J55's census of the string tables, again (Starfield's English tables: 228 strings in Windows-1252, none
//    in UTF-8; DarkStar's German, French and Spanish: 27,824 strings in Windows-1252).
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class ArchiveEncodingTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>J69: each pattern's matches in a text (Latin-1 characters, as an 8-bit file's bytes are read).</summary>
    public static string Escapes(Ctx ctx)
    {
        _ = ctx;
        // The UTF-8 bytes of "grö Grö GRö" and gr + E3 B6 80 (a CJK character), as Latin-1 characters
        string utf8 = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes("grö Grö GRö GRÖ gr\u3D80"));
        var cases = new (string Pattern, string Text, string[] Want)[]
        {
            (@"\xC3", "\u00C3\u00E3", new[] { "\u00C3" }),
            (@"\xe3", "\u00C3\u00E3", new[] { "\u00E3" }),
            (@"\x41", "Aa", new[] { "A" }),
            (@"\x4B", "Kk\u212A", new[] { "K" }),
            ("a", "Aa", new[] { "A", "a" }),
            (@"\u00C9", "Éé", new[] { "É" }),
            (@"gr\xC3\xB6", utf8, new[] { "grÃ¶", "GrÃ¶", "GRÃ¶" }),
            (@"GR\xC3\x96", utf8, new[] { "GRÃ\u0096" }),
            (@"\x4Bm", "Km KM km", new[] { "Km", "KM" }),
            (@"\xC3+", "ÃÃã", new[] { "ÃÃ" }),
            (@"(?:\xC3){2}", "ÃÃãã", new[] { "ÃÃ" }),
            (@"[\xC0-\xDE]", "ÀàÞþ", new[] { "À", "Þ" }),
            (@"[\x41-\x43]", "ABCabc", new[] { "A", "B", "C" }),
            (@"[a\xC3]", "aAÃã", new[] { "a", "A", "Ã" }),
            (@"[^a\xC3]", "aAÃãb", new[] { "ã", "b" }),
            (@"[^\xC3]", "Ãã", new[] { "ã" }),
            (@"[\101]", "Aa", new[] { "A" }),
            (@"[a-z\xC0-\xC5]", "aZÀà", new[] { "a", "Z", "À" }),
            (@"[\xC0-\xFF-[\xC9]]", "ÉéÀ", new[] { "é", "À" }),
            (@"[\w-[\xC9]]", "Éé", new[] { "é" }),
            (@"[]\xC3]", "]Ãã", new[] { "]", "Ã" }),
            (@"[\xC3-]", "Ã-ã", new[] { "Ã", "-" }),
            (@"[\x00-\x1F\xC3]+", "\u0001Ãã", new[] { "\u0001Ã" }),
            (@"(?x) \xC3 # [ a comment", "Ãã", new[] { "Ã" }),
            (@"(?#[)\xC3", "Ãã", new[] { "Ã" }),
            (@"\\xC3", @"\XC3 \xc3", new[] { @"\XC3", @"\xc3" }),
            (@"\p{Lu}\xE9", "Aé aé AÉ", new[] { "Aé", "aé" }), // \p{Lu} itself ignores case in .NET
            (@"(?<n>\xC3)\k<n>", "ÃÃ ãã", new[] { "ÃÃ" }),
            (@"(?(\xC3)\xC3|b)", "Ã ã B", new[] { "Ã", "B" }),
            (@"\x2E\x2e", "..", new[] { ".." }),
        };
        foreach (var (pattern, text, want) in cases)
        {
            var m = new ArchiveGrep.Matcher(pattern, regex: true);
            var got = new List<string>();
            m.Find(text.AsSpan(), 0, text.Length, (at, length) => got.Add(text.Substring(at, length)));
            Check(got.SequenceEqual(want) && !m.CodesFold, $"regex {pattern} as {ArchiveGrep.Matcher.ExactCodes(pattern)}: matched {string.Join(", ", got.Select(Show))} in {Show(text)}, expected {string.Join(", ", want.Select(Show))}");
        }
        // What the codes are made into: a pattern without a code of a letter stays as it was
        foreach (var (pattern, made) in new[] {
            (@"gr\xC3\xB6", @"gr(?:(?-i:\xC3))\xB6"), (@"[a\xC3]", @"(?:[a]|(?-i:[\xC3]))"), (@"[^a\xC3]", @"(?:(?!(?-i:[\xC3]))[^a])"),
            (@"[\xC0-\xDE]", @"(?:(?-i:[\xC0-\xDE]))"), (@"[\x00-\x1F]", @"[\x00-\x1F]"), (@"\x00\.\x00", @"\x00\.\x00"), (@"[a-z-[\xE9]]", @"(?:(?!(?:(?-i:[\xE9])))[a-z])"),
            ("plain", "plain"), (ArchiveGrepTests.Census21, ArchiveGrepTests.Census21) })
        {
            string got = ArchiveGrep.Matcher.ExactCodes(pattern);
            Check(got == made, $"{pattern} made into {got}, expected {made}");
        }
        // A broken pattern is refused with the user's own pattern in the error
        bool refused = false;
        try { _ = new ArchiveGrep.Matcher(@"[\xC3", regex: true); } catch (ReportException e) { refused = e.Message.Contains(@"""[\xC3""", StringComparison.Ordinal); }
        Check(refused, @"[\xC3 wasn't refused with its own text");
        return $"{cases.Length} regexes with codes match only the code's character, the letters around them either case (\\xC3 no longer matches E3; gr\\xC3\\xB6 3 hits, not the CJK gr\\u3D80)";
    }

    static string Show(string s) => "\"" + string.Concat(s.Select(c => c < 0x20 || c is >= '\u007F' and <= '\u00A0' ? $"\\x{(int)c:X2}" : c.ToString())) + "\"";

    // ---------------------------------------------------------------- J71: outside ASCII on the bytes

    /// <summary>
    /// J71 (GAPS S95), J80 (GAPS S103): a pattern outside ASCII matched on the bytes finds exactly what it finds on the
    /// bytes read as text (J55's reading, kept for the patterns that can't be made so): the same hits at the same bytes
    /// and lengths, on made bytes that mix UTF-8, Windows-1252, cut and false UTF-8, characters outside the BMP (an
    /// emoji, Deseret letters, which have a case), digits and word joiners outside ASCII, and the Kelvin sign; over a
    /// whole buffer, and from a byte inside it to another (a resume point and a piece's cut).
    /// </summary>
    public static string BytesRoute(Ctx ctx)
    {
        _ = ctx;
        var rnd = new Random(71);
        byte[] palette = { (byte)'a', (byte)'A', (byte)'g', (byte)'r', (byte)'s', (byte)'S', (byte)'e', (byte)'k', (byte)'K', (byte)'x', (byte)'z', (byte)'u', (byte)'t', (byte)'n', (byte)'\n', (byte)' ', (byte)'\\', (byte)'/', (byte)'1',
            0xC3, 0xB6, 0x96, 0xF6, 0xD6, 0xA4, 0x84, 0x9F, 0xBC, 0x9C, 0xFC, 0xDC, 0xE9, 0xC9, 0xA9, 0x89, 0xE2, 0x80, 0x99, 0x92, 0x8C, 0x9D, 0xE0, 0xA0, 0xED, 0xF0, 0x90, 0xF4, 0x8F, 0xBF, 0xC2, 0x81, 0xB5, 0xFF, 0xC5, 0xB8, 0xAA, 0x00, 0x98, 0xA8, 0xD9, 0xA3 };
        string[] words = { "größe", "GRÖSSE", "ausrüstung", "AUSRÜSTUNG", "Captain’s", "œuvre", "écran", "ÉCRAN", "Ÿ", "µ", "k", "K", @"a\ö", "a/ö", "€", "ÿ",
            "\U0001F600", "ö\U0001F600ö", "\U00010400", "\U00010428ö", "日本ö", "٣ö", "ö٣", "a‌ö", "ö‍", "öö", "ÖÖö", "öxö", "ö1", "1ö", "Kö" };
        var buffers = new List<byte[]>();
        for (int b = 0; b < 40; b++)
        {
            var bytes = new List<byte>();
            while (bytes.Count < 6000)
            {
                int pick = rnd.Next(10);
                if (pick < 6) bytes.Add(palette[rnd.Next(palette.Length)]);
                else
                {
                    string w = words[rnd.Next(words.Length)];
                    bytes.AddRange(pick < 8 ? Encoding.UTF8.GetBytes(w) : w.Select(c => c < 0x100 ? (byte)c : Cp1252.TryGetValue(c, out var x) ? x : (byte)'?'));
                }
            }
            buffers.Add(bytes.ToArray());
        }
        var plain = new[] { "ö", "Ö", "größe", "ausrüstung", "AUSRÜSTUNG", "’s", "Œuvre", "é", @"a\ö", "Ÿ", "µ", "€", "ÿ", "üu", "\U0001F600", "\U00010400", "ö\U0001F600", "日本" };
        var regexes = new[] { "a|ö", "ausr[üu]stung", "gr[öo]sse", "[éÉ]CRAN", "(?:x|gr)[öo]sse|zzzö", "ö+", "[à-ÿ]{2}", "(?x) ö # a comment", "^ö", "ö$", @"ü|\xF6", "k[öo]", "€|’", @"ö\s", "(?<n>ö)x", @"[\x80-\xFF]ö", "Ÿ|ÿ", "(?>ö|öx)", @"ä\.k",
            // J80: atoms of many characters, \b, inline options, look-arounds, characters outside the BMP, back-references
            "ö|.", "ö.", "..ö", "[^a]ö", "[^aö]", @"\wö", @"\Wö", @"\w+ö", @"\dö|ö\d", @"\D\d|ö", @"\sö", @"\Sö", @"\p{L}ö", @"\P{L}ö", @"[\w-[a-z]]ö", @"ö|[^\x00-\x7F]+",
            @"\bö", @"ö\b", @"\Bö", @"\b\w+\bö?", "(?i)ö", "(?-i)Ö", "(?-i:Ö)ö", "(?s)ö.", "(?s:.)ö", "(?x) ö \\s # c\n.", "a(?-i)ö|Ö", "(?i:k)ö", "(?m)^ö", "(?-m)^.ö?", "ö(?-m)$", @"\Aö|ö\z",
            "(?=ö)ö", "(?<=a)ö", "ö(?!n)", @"(?<!\w)ö", @"(?<=ö\w)\w", "(?<n>ö)(?(n)x|y)", "(?(?=ö)ö.|a)", "(?(ö)..|x)", "ö{2,3}", @"(?n)(ö)\w",
            "\U0001F600", "\U0001F600|ö", "[\U0001F600]", ".\U0001F600", "\U0001F600.", "\U00010400", @"\uD801|ö", @"[\uDC00-\uDFFFö]",
            @"(ö)\1", @"(ö)x?\1", @"(?<w>[öü])\k<w>", @"(.)\1ö", @"(\w)\1ö", @"(?i)(ö)(?-i)\1",
            // no length limit: in the runs of the characters a hit can hold
            @"\w*ü\w*", ".*ö", @"(\w+)ö\1", @"(?<=\s)\w+ö", @"ö\w+$", @"\bö\w*\b", @"(\wö)+", @"[öü]\W+\d" };
        var chars = new char[8000]; var latin = new char[8000];
        var map = new ArchiveGrep.TextMap();
        long compared = 0, comparedRaw = 0;
        int routedAsText = 0;
        static List<(int, int)> Diff(List<(int, int)> a, List<(int, int)> b) => a.Except(b).Concat(b.Except(a)).Take(4).ToList();
        foreach (var (pattern, regex) in plain.Select(p => (p, false)).Concat(regexes.Select(p => (p, true))))
        {
            string name = pattern + (regex ? " (regex)" : "");
            var m = new ArchiveGrep.Matcher(pattern, regex);
            Check(m.DecodesBytes && (m.OnBytes || m.TextReason != null), $"{name}: read as text with no reason");
            if (!m.OnBytes) routedAsText++;
            // The pattern over the bytes itself, also where reading as text was chosen as faster (no back-reference)
            var made = regex ? ArchiveGrep.Matcher.BytesOfRegexMade(pattern) : default;
            Check(!regex || made.Pattern != null || pattern.Contains('\\') && Regex.IsMatch(pattern, @"\\[1-9k]"), $"{name}: no pattern over the bytes ({made.Reason})");
            var raw = made.Pattern == null ? null : new Regex(made.Pattern, RegexOptions.CultureInvariant | RegexOptions.Multiline);
            foreach (var bytes in buffers)
            {
                int n = ArchiveGrep.ReadText(bytes, chars, map);
                Encoding.Latin1.GetChars(bytes, latin);
                // The whole buffer; then from byte 1000 to byte 4500, as from a resume point to a piece's cut
                foreach (var (from, before) in new[] { (0, bytes.Length), (1000, 4500) })
                {
                    var asText = new List<(int, int)>();
                    m.Find(chars.AsSpan(0, n), map.CharOf(from), map.CharOf(before), (at, length) => { int b = map.ByteOf(at); asText.Add((b, map.ByteOf(at + length) - b)); });
                    if (m.OnBytes)
                    {
                        var onBytes = new List<(int, int)>();
                        m.FindBytes(bytes, latin.AsSpan(0, bytes.Length), from, before, (at, length) => onBytes.Add((at, length)));
                        Check(onBytes.SequenceEqual(asText), $"{name} from {from} before {before}: on the bytes {onBytes.Count} hits, read as text {asText.Count}; first difference {string.Join(" / ", Diff(onBytes, asText))}");
                        compared += asText.Count;
                    }
                    if (raw != null)
                    {
                        var rawHits = new List<(int, int)>();
                        foreach (var x in raw.EnumerateMatches(latin.AsSpan(0, bytes.Length), from)) { if (x.Index >= before) break; if (x.Length > 0) rawHits.Add((x.Index, x.Length)); }
                        Check(rawHits.SequenceEqual(asText), $"{name} from {from} before {before}: the pattern over the bytes {rawHits.Count} hits, read as text {asText.Count}; first difference {string.Join(" / ", Diff(rawHits, asText))}");
                        comparedRaw += asText.Count;
                    }
                }
            }
        }
        // The usual forms of each construct take a route on the bytes (the pattern over them, near a rare character, or in
        // the runs of the characters a hit can hold around it)
        foreach (var pattern in new[] { "[^a]ö", "ö.", @"\wö", @"\dö|ö\d", @"\bö", @"ö\b", "(?i)ö", "(?s)ö.", "(?=ö)ö", "(?<=a)ö", "\U0001F600|ö", ".\U0001F600", @"(ö)\1", @"(?<w>[öü])\k<w>", "[à-ÿ]{2}",
            @"\w+ö", @"\w*ü\w*", ".*ö", @"(\w+)ö\1", @"(?<=\s)\w+ö" })
            Check(new ArchiveGrep.Matcher(pattern, regex: true).OnBytes, $"{pattern}: read as text");
        // What isn't matched on the bytes is read as text, as before, and says why
        var asTextStill = new[] { @"(?<=(ö))\1", @"(ö)\1\G", @"(ö|a)\1", "[^ö]+ö", "ö|.", @"\D\d|ö" };
        foreach (var pattern in asTextStill)
        {
            var m = new ArchiveGrep.Matcher(pattern, regex: true);
            Check(!m.OnBytes && m.TextReason != null, $"{pattern}: matched on the bytes, or no reason given");
        }
        return $"{plain.Length} texts and {regexes.Length} regexes outside ASCII ({regexes.Length + plain.Length - routedAsText} matched on the bytes): the same {N(compared)} hits at the same bytes as on the text read, and the pattern over the bytes of every regex without a back-reference the same {N(comparedRaw)}, over {buffers.Count} made buffers of UTF-8, 1252, cut and false UTF-8 and characters outside the BMP; {asTextStill.Length} regexes read as text, each with its reason";
    }

    // ---------------------------------------------------------------- J70: archive2 encoding

    /// <summary>A string table: an ID and the string's bytes each; with lengths (.dlstrings, .ilstrings) or zero-ended (.strings).</summary>
    static byte[] Table(bool lengths, params (uint Id, byte[] Bytes)[] strings)
    {
        using var data = new MemoryStream();
        var offsets = new List<uint>();
        foreach (var (_, b) in strings)
        {
            offsets.Add((uint)data.Length);
            if (lengths) { var l = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(l, (uint)b.Length + 1); data.Write(l); }
            data.Write(b); data.WriteByte(0);
        }
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((uint)strings.Length); w.Write((uint)data.Length);
        for (int i = 0; i < strings.Length; i++) { w.Write(strings[i].Id); w.Write(offsets[i]); }
        w.Write(data.ToArray());
        w.Flush();
        return ms.ToArray();
    }

    static readonly Dictionary<char, byte> Cp1252 = new() { ['’'] = 0x92, ['€'] = 0x80, ['–'] = 0x96, ['Œ'] = 0x8C };
    static byte[] W(string s) => s.Select(c => c < 0x100 ? (byte)c : Cp1252[c]).ToArray();
    static byte[] U(string s) => Encoding.UTF8.GetBytes(s);
    static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    public static string Encodings(Ctx ctx)
    {
        string data = ctx.Folder("encoding-data");
        // A big UTF-8 file: one ASCII byte, then é (C3 A9) again and again, so that one é is cut by the first 64 KB and
        // others by the 4 MB pieces after it; a 1252 é at the very end
        int bigLength = (9 << 20) + 2;
        var big = new byte[bigLength];
        big[0] = (byte)'x';
        for (int i = 1; i + 2 < bigLength; i += 2) { big[i] = 0xC3; big[i + 1] = 0xA9; }
        big[^1] = 0xE9;
        long bigChars = (bigLength - 2) / 2;
        // A log of ASCII lines with a control byte at 100,000: past the first 64 KB read
        var log = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("a line of a log\r\n", 8000)));
        log[100_000] = 0x01;
        var binary = new byte[1 << 20];
        for (int i = 0; i < binary.Length; i++) binary[i] = (byte)(i % 7 == 0 ? 0 : 0x80 + i % 13);
        var utf16le = Cat(new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes("Größe\r\nÉcran\r\n"));
        var utf16be = Cat(new byte[] { 0xFE, 0xFF }, Encoding.BigEndianUnicode.GetBytes("Größe\r\n"));
        var utf16control = Cat(new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes("ab\u0001c"));
        var utf16odd = Cat(new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes("ab"), new byte[] { 0x41 });
        // String tables: ASCII, UTF-8, 1252 and mixed strings
        var strings = Table(false, (1, U("Plain")), (2, U("Größe")), (3, W("Captain’s Slate")), (4, Cat(U("größe "), W("écran"))), (5, U("")));
        var dlstrings = Table(true, (0x10, W("Œuvre – à")), (0x11, U("Book")), (0x12, U("Ünter")));
        var empty = new byte[8];
        var broken = Cat(new byte[] { 0xFF, 0xFF, 0xFF, 0x0F, 0, 0, 0, 0 }, U("plain bytes"));
        var files = new List<(string Path, byte[] Data, bool Compress)>
        {
            (@"interface\ascii.txt", U("plain text\r\nline two\r\n"), true),
            (@"interface\utf8.txt", U("Größe\n"), true),
            (@"interface\mark.csv", Cat(new byte[] { 0xEF, 0xBB, 0xBF }, U("a,b\n")), false),
            (@"interface\w1252.txt", W("Captain’s écran\n"), true),
            (@"interface\mixed.txt", Cat(U("größe "), W("écran\n")), true),
            (@"interface\utf16le.txt", utf16le, true),
            (@"interface\utf16be.txt", utf16be, false),
            (@"interface\utf16control.txt", utf16control, true),
            (@"interface\utf16odd.txt", utf16odd, true),
            (@"meshes\enc\binary.mesh", binary, true),
            (@"interface\log.txt", log, true),
            (@"interface\big.txt", big, true),
            (@"strings\enc_en.strings", strings, false),
            (@"strings\enc_en.dlstrings", dlstrings, true),
            (@"strings\enc_en.ilstrings", empty, false),
            (@"strings\enc_broken.strings", broken, false),
            (@"textures\enc\skipped.dds", binary, false),
        };
        ArchiveGrepTests.WriteBa2(Path.Combine(data, "enc - main.ba2"), files);
        // A loose copy in Windows-1252: the game reads it in place of the archived UTF-8 one
        Directory.CreateDirectory(Path.Combine(data, "interface"));
        File.WriteAllBytes(Path.Combine(data, @"interface\utf8.txt"), W("Größe in 1252\n"));

        using var session = new ReportSession(data, ctx.Folder("encoding-scratch"), ctx.Schema);
        var report = new ArchiveReport(session);
        EncodingAnswer List(GrepOptions o, int maxChars = int.MaxValue / 2, bool detail = true)
        {
            using var plan = report.PlanEncodings("enc.esm", o);
            return report.Encodings(plan, maxChars, detail);
        }
        var all = List(new GrepOptions { Text = "" });
        EncodingFile F(string path, string where = "enc - main.ba2") => all.Files.Single(f => f.Path == path && f.Where == where);
        void Is(string path, TextEncoding e, long outside, long notUtf8, long? first = null, long? control = null, string where = "enc - main.ba2")
        {
            var f = F(path, where);
            Check(f.Encoding == e && f.OutsideAscii == outside && f.NotUtf8 == notUtf8 && f.FirstNotUtf8 == first && f.ControlAt == control,
                $"{where} {path}: {f.Encoding}, {f.OutsideAscii} outside ASCII, {f.NotUtf8} not UTF-8, first {f.FirstNotUtf8}, control at {f.ControlAt}; expected {e}, {outside}, {notUtf8}, {first}, {control}");
        }
        Check(all.ScopeFiles == 17 && all.Files.Count == 17 && all.NextItem == null && !all.Files.Any(f => f.Path.EndsWith(".dds", StringComparison.Ordinal)), $"the scope: {all.ScopeFiles} files, {all.Files.Count} listed with detail");
        Is(@"interface\ascii.txt", TextEncoding.Ascii, 0, 0);
        Is(@"interface\utf8.txt", TextEncoding.Utf8, 2, 0);
        Is(@"interface\utf8.txt", TextEncoding.Windows1252, 0, 2, 2, where: "loose");
        Is(@"interface\mark.csv", TextEncoding.Utf8, 0, 0);
        Check(F(@"interface\mark.csv").Utf8Mark && F(@"interface\mark.csv").What().Contains("byte-order mark", StringComparison.Ordinal), "mark.csv: its UTF-8 mark");
        Is(@"interface\w1252.txt", TextEncoding.Windows1252, 0, 2, 7);
        Check(F(@"interface\w1252.txt").NotUtf8Around == "Captain«’»s écran", "w1252.txt: the text around the first byte: " + F(@"interface\w1252.txt").NotUtf8Around);
        Is(@"interface\mixed.txt", TextEncoding.Mixed, 2, 1, U("größe ").Length);
        Is(@"interface\utf16le.txt", TextEncoding.Utf16LE, 3, 0);
        Is(@"interface\utf16be.txt", TextEncoding.Utf16BE, 2, 0);
        Is(@"interface\utf16control.txt", TextEncoding.Binary, 0, 0, control: 6);
        Check(F(@"interface\utf16control.txt").What().Contains("U+0001 at 6", StringComparison.Ordinal), "utf16control.txt: " + F(@"interface\utf16control.txt").What());
        Is(@"interface\utf16odd.txt", TextEncoding.Binary, 0, 0, control: 6);
        Is(@"meshes\enc\binary.mesh", TextEncoding.Binary, 0, 0, control: 0);
        Is(@"interface\log.txt", TextEncoding.Binary, 0, 0, control: 100_000);
        Is(@"interface\big.txt", TextEncoding.Mixed, bigChars, 1, bigLength - 1);
        Is(@"strings\enc_en.strings", TextEncoding.Mixed, 4, 2, 69);
        var st = F(@"strings\enc_en.strings").Strings!;
        Check(st.Strings == 5 && st.Ascii == 2 && st.Utf8 == 1 && st.Windows1252 == 1 && st.Mixed == 1, $"enc_en.strings: {st.Strings} strings, {st.Ascii} ASCII, {st.Utf8} UTF-8, {st.Windows1252} 1252, {st.Mixed} mixed");
        Check(st.NotUtf8.Count == 2 && st.NotUtf8[0] is { Id: 3, Offset: 62, Encoding: TextEncoding.Windows1252, Text: "Captain’s Slate" } && st.NotUtf8[1] is { Id: 4, Encoding: TextEncoding.Mixed, Text: "größe écran" },
            "enc_en.strings, the strings not in UTF-8: " + string.Join(", ", st.NotUtf8.Select(x => $"{x.Id:X} at {x.Offset} {x.Encoding} {x.Text}")));
        Is(@"strings\enc_en.dlstrings", TextEncoding.Mixed, 1, 3, 36);
        var dl = F(@"strings\enc_en.dlstrings").Strings!;
        Check(dl.Strings == 3 && dl.Ascii == 1 && dl.Utf8 == 1 && dl.Windows1252 == 1 && dl.NotUtf8.Single() is { Id: 0x10, Offset: 36, Text: "Œuvre – à" }, $"enc_en.dlstrings: {dl.Strings} strings, {dl.Utf8} UTF-8, {dl.Windows1252} 1252: {dl.NotUtf8.FirstOrDefault()?.Text}");
        Is(@"strings\enc_en.ilstrings", TextEncoding.Ascii, 0, 0);
        Check(F(@"strings\enc_en.ilstrings").Strings?.Strings == 0, "an empty .ilstrings is a table of 0 strings");
        Check(F(@"strings\enc_broken.strings").NotATable && F(@"strings\enc_broken.strings").Encoding == TextEncoding.Binary, "a .strings file that isn't laid out as one: read as bytes");
        Check(all.All.ByEncoding.Sum() == 17 && all.All.Strings == 8 && all.All.Strings1252 == 2 && all.All.StringsMixed == 1, $"the totals: {all.All.Sentence()}; {all.All.StringSentence()}");
        // Only the bytes needed: the binary mesh stops in its first 64 KB, the log in its second piece; the estimate counts
        // a binary kind's first 64 KB and a text kind whole
        long sizes = files.Where(f => !f.Path.EndsWith(".dds", StringComparison.Ordinal)).Sum(f => (long)f.Data.Length) + W("Größe in 1252\n").Length;
        Check(all.All.Bytes < sizes - binary.Length + (64 << 10) + 1 && all.EstimateBytes == sizes - binary.Length + (64 << 10), $"read {all.All.Bytes} bytes, estimated {all.EstimateBytes}, of {sizes}");

        // ---- the list without --detail: only the files with text outside ASCII; the answer says the rule and why
        var shortList = List(new GrepOptions { Text = "" }, detail: false);
        Check(shortList.Files.Count == 10 && shortList.Files.All(f => f.Encoding is not (TextEncoding.Ascii or TextEncoding.Binary)), "the list: " + string.Join(", ", shortList.Files.Select(f => f.Path)));
        string text = shortList.ToText(new ReportTextOptions { MaxChars = int.MaxValue });
        Check(text.Contains("are written: 2 ASCII only, 2 UTF-8, 2 Windows-1252, 4 mixed UTF-8 and Windows-1252, 1 UTF-16 LE, 1 UTF-16 BE, 5 binary.", StringComparison.Ordinal)
            && text.Contains("The string tables' 8 strings: 3 ASCII, 2 UTF-8, 2 Windows-1252, 1 mixed", StringComparison.Ordinal)
            && text.Contains("| enc - main.ba2 | strings\\enc_en.strings | Windows-1252 | string 00000003 at 62: «Captain’s Slate» |", StringComparison.Ordinal)
            && text.Contains("| loose | interface\\utf8.txt | Windows-1252 | 2 bytes outside ASCII, none of them UTF-8: read as Windows-1252 (the first at 2: Gr«ö»ße in 1252) |", StringComparison.Ordinal)
            && shortList.Notes.Any(n => n.StartsWith("How a file is told:", StringComparison.Ordinal)), "the list's text:\n" + text);
        ctx.WriteReport("archive-encoding.txt", text);
        // ---- pages: a small page stops early; the next page goes on from the next file, none lost or twice
        var seen = new List<string>();
        int start = 0, pages = 0;
        while (true)
        {
            EncodingAnswer page;
            using (var plan = report.PlanEncodings("enc.esm", new GrepOptions { Text = "", Start = start })) page = report.Encodings(plan, 300, detail: true);
            pages++;
            seen.AddRange(page.Files.Select(f => f.Where + ":" + f.Path));
            if (page.NextItem == null) break;
            start = (int)page.NextItem;
        }
        Check(pages > 3 && seen.SequenceEqual(all.Files.Select(f => f.Where + ":" + f.Path)), $"{pages} pages: {seen.Count} files, the whole answer {all.Files.Count}");
        // ---- --count: per kind and per archive, over the whole scope
        EncodingCountAnswer count;
        using (var plan = report.PlanEncodings("enc.esm", new GrepOptions { Text = "", Count = true })) count = report.EncodingsCount(plan);
        var txt = count.ByKind[".txt"];
        Check(count.All.Files == 17 && txt.Files == 11 && txt.ByEncoding[(int)TextEncoding.Binary] == 3 && count.ByKindWhere[".txt"]["loose"].ByEncoding[(int)TextEncoding.Windows1252] == 1 && count.ByKind[".strings"].Strings1252 == 1,
            "count: " + string.Join(", ", count.ByKind.Select(k => $"{k.Key} {k.Value.Files}: {k.Value.Sentence()}")));
        string ct = count.ToText(new ReportTextOptions { MaxChars = int.MaxValue });
        Check(ct.Contains("| .txt | 11 | 1 | 1 | 2 | 2 | 2 | 3 |  |", StringComparison.Ordinal) && ct.Contains("| .strings | 2 | 0 | 0 | 0 | 1 | 0 | 1 | 1 / 2 |", StringComparison.Ordinal), "count's text:\n" + ct);
        // ---- a scope with a kind named: textures too
        var dds = List(new GrepOptions { Text = "", Extensions = new[] { "dds" } });
        Check(dds.Files.Single().Encoding == TextEncoding.Binary, "--ext dds: " + dds.All.Sentence());
        return $"archive2 encoding on {N(all.ScopeFiles)} made files: each encoding (ASCII, UTF-8 with and without its mark, 1252, mixed, UTF-16 LE and BE, binary at a control byte, past 64 KB too), string tables string by string, a 9 MB UTF-8 file across the cuts; the list, --detail, --count and {pages} pages";
    }

    /// <summary>The game's string tables: J55's census.</summary>
    public static string Game(Ctx ctx)
    {
        var report = new ArchiveReport(ctx.Session);
        EncodingAnswer en;
        using (var plan = report.PlanEncodings("Starfield - Localization.ba2", new GrepOptions { Text = "", Folders = new[] { "strings" }, Extensions = new[] { "strings", "dlstrings", "ilstrings" } }))
            en = report.Encodings(plan, int.MaxValue / 2, detail: true);
        var english = en.Files.Where(f => Path.GetFileName(f.Path).StartsWith("starfield_en.", StringComparison.OrdinalIgnoreCase)).ToList();
        long w1252 = english.Sum(f => f.Strings!.Windows1252), utf8 = english.Sum(f => f.Strings!.Utf8 + f.Strings.Mixed);
        Check(english.Count == 3 && w1252 == 228 && utf8 == 0 && english.All(f => f.Encoding == TextEncoding.Windows1252), $"starfield_en: {english.Count} tables, {w1252} strings in 1252, {utf8} in UTF-8");
        var swaney = english.Single(f => f.Path.EndsWith(".strings", StringComparison.OrdinalIgnoreCase)).Strings!.NotUtf8[0];
        Check(swaney.Text == "Swaney’s Slate", "the first 1252 string of starfield_en.strings: " + swaney.Text);
        Check(en.Files.Where(f => !english.Contains(f)).All(f => f.Encoding == TextEncoding.Utf8), "the other languages' tables: " + string.Join(", ", en.Files.Where(f => f.Encoding != TextEncoding.Utf8 && !english.Contains(f)).Select(f => f.Path)));
        string said = $"Starfield's English tables: {w1252} strings in Windows-1252, none in UTF-8; the other languages UTF-8";
        if (File.Exists(Path.Combine(ctx.Data, "darkstar - main.ba2")))
        {
            EncodingCountAnswer ds;
            using (var plan = report.PlanEncodings("darkstar.esm", new GrepOptions { Text = "", Count = true, Extensions = new[] { "strings", "dlstrings", "ilstrings" } }))
                ds = report.EncodingsCount(plan);
            Check(ds.All.Strings1252 == 27_824 && ds.All.StringsMixed == 0, $"DarkStar's tables: {ds.All.Strings1252} strings in 1252, {ds.All.StringsMixed} mixed");
            said += $"; DarkStar's tables: {N(ds.All.Strings1252)} strings in Windows-1252 and {N(ds.All.StringsUtf8)} in UTF-8";
        }
        return said;
    }
}
