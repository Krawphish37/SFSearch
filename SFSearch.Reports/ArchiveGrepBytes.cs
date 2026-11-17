// archive grep outside ASCII, on the bytes (J71, GAPS S95; J80, GAPS S103). A text or regex with a character outside
// ASCII is matched as if each 8-bit file were read as text (J55: UTF-8, a byte that isn't part of a valid UTF-8
// character as Windows-1252), but without reading it so: the pattern is made into one over the bytes. Each atom (one
// character of the text read: a letter, an escape, a class, ., \w, \d) becomes the byte forms of the characters it
// matches: UTF-8 bytes, and Windows-1252 bytes with the conditions under which the reading takes them as 1252 (not
// the start of a valid UTF-8 character, not inside one). Matched on the bytes (as Latin-1 characters), it finds the
// same hits at the same bytes as the reading did; only a kept hit's text is read around it.
//
// J80: an atom of many characters (., \w, \d, a negated class, \p{L}) is written as byte ranges (a UTF-8 lead byte
// and the continuation bytes it takes, grouped), not character by character; a character outside the BMP is its
// 4-byte UTF-8 (the text read holds it as two halves: the first stands at the lead byte, the second at the three bytes
// after it, as the offsets of the text read give them); \b and \B are look-arounds over the word characters' forms;
// inline options are applied while the pattern is read (i: the atoms' letter case; s: what . matches; x: blanks and
// comments; m and n are kept as written, as they mean the same over the bytes); look-arounds keep their place, their
// content in byte forms. A back-reference can't be matched on the bytes (it repeats a text in either case and either
// encoding): such a regex finds the places of a character every hit holds on the bytes, and is matched by the real
// regex on the text read around them only (when a hit has no length limit: in the unbroken run of the characters a hit
// can hold around each place). So is a pattern whose hit may start at nearly any byte (., \w, a negated class first):
// over the bytes it would be tried at every byte.
//
// Measured first (notes\fix-j68-grep-encoding.md, notes\fix-j80-regex-bytes.md): reading a piece as text ran 483 MB/s
// a thread on the game's meshes, against 19 GB/s for Latin-1 alone.
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SFSearch.Reports;

public static partial class ArchiveGrep
{
    internal sealed partial class Matcher
    {
        /// <summary>The pattern over an 8-bit file's bytes (as Latin-1 characters), for a pattern outside ASCII; null when
        /// it can't be made (then the files are read as text, as J55 did, or near a character's places: <see cref="_near"/>).</summary>
        Regex? _bytes;
        /// <summary>A plain text: every place it starts is a hit, overlapping or not, as on the text read.</summary>
        bool _bytesPlain;
        /// <summary>A regex with a back-reference, or one whose hit may start at nearly any byte (J80): the byte forms of a
        /// rare character every hit holds. Only the text around its places is read, and the real regex matched there.</summary>
        Regex? _near;
        /// <summary>For <see cref="_near"/>: how many bytes before a place a hit may start, and how many bytes of text
        /// beyond that its look-arounds, ^, $ and \b may look at.</summary>
        int _nearHit, _nearLook;
        /// <summary>For <see cref="_near"/> when a hit has no length limit (\w+ö): the run of the characters a hit can hold
        /// that ends at a place (matched right to left) and the one that starts there. A hit lies inside one such run.</summary>
        Regex? _runBack, _runOn;
        /// <summary>When every character a hit can't hold is ASCII (., [^\n]): those bytes end a run, found at once.</summary>
        SearchValues<byte>? _runStops;

        /// <summary>An 8-bit file is matched on its bytes, or read as text only near the places a hit needs (J71, J80).</summary>
        public bool OnBytes => _bytes != null || _near != null;

        /// <summary>Why a pattern outside ASCII is still matched on the files read as text (J55's reading), for the answer's
        /// note; null when it is matched on the bytes.</summary>
        public string? TextReason { get; private set; }

        void MakeBytes(string text, bool regex)
        {
            if (!regex)
            {
                _bytes = Compile(BytesOfText(_spellings));
                _bytesPlain = true;
                if (_bytes == null) TextReason = "it couldn't be made into a pattern over the bytes";
                return;
            }
            // The pattern over the bytes when the regex can skip to where a hit starts; else near a rare character every hit
            // holds (in the runs of the characters a hit can hold, with no length limit); else read as text (J55), which is
            // faster than trying a pattern of thousands of characters at every byte. A pattern that repeats an atom of many
            // characters (\w*) or has a \b of four look-arounds costs more per byte of a hit over the bytes than the real
            // regex on the text read near a rare character: it goes near when it can
            var made = BytesOfRegexMade(text);
            bool nearFirst = made.Heavy && made.Near != null && made.NearLeadBytes <= FewLeadBytes;
            if (made.Pattern != null && made.LeadBytes <= FewLeadBytes && !nearFirst) _bytes = Compile(made.Pattern);
            if (!OnBytes && made.Near != null && made.NearLeadBytes <= FewLeadBytes && Compile(made.Near) is { } near)
            {
                if (made.Run == null) { _near = near; _nearHit = made.HitBytes; _nearLook = made.LookBytes; }
                else if (made.RunStops != null) { _near = near; _nearLook = made.LookBytes; _runStops = SearchValues.Create(made.RunStops); }
                else if (Compile("(?:" + made.Run + @")*\G", RegexOptions.RightToLeft) is { } back && Compile(@"\G(?:" + made.Run + ")*") is { } on)
                {
                    _near = near; _nearLook = made.LookBytes; _runBack = back; _runOn = on;
                }
            }
            if (!OnBytes && made.Pattern != null && made.LeadBytes <= FewLeadBytes) _bytes = Compile(made.Pattern);
            if (OnBytes) return;
            if (made.Pattern != null)
                TextReason = @"a hit can start at nearly any byte (., \w, \d, a negated class first), and " + (made.Reason ?? "each character every hit holds is common in binary files") +
                    "; on the bytes such a pattern would be tried at almost every byte, slower than reading the files as text (5 to 90 MB/s a thread on the game's meshes, against 120 to 550 MB/s)";
            else TextReason = made.Reason ?? "it couldn't be made into a pattern over the bytes";
        }

        static Regex? Compile(string pattern, RegexOptions more = RegexOptions.None)
        {
            try { return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Compiled | more, TimeSpan.FromSeconds(10)); }
            catch (ArgumentException) { return null; }
        }

        /// <summary>
        /// The hits over an 8-bit file's <paramref name="bytes"/> (<paramref name="window"/>: the same as Latin-1
        /// characters), as <see cref="Find"/> gives them over the text read: the same hits at the same bytes.
        /// </summary>
        public void FindBytes(ReadOnlySpan<byte> bytes, ReadOnlySpan<char> window, int from, int before, Action<int, int> hit)
        {
            try
            {
                if (_near != null) { FindNear(bytes, window, from, before, hit); return; }
                if (_bytesPlain)
                {
                    // Every place the text starts, as the text's own search does: the next search starts a character on
                    for (int at = from; at < before;)
                    {
                        var e = _bytes!.EnumerateMatches(window, at);
                        if (!e.MoveNext() || e.Current.Index >= before) return;
                        hit(e.Current.Index, e.Current.Length);
                        at = e.Current.Index + 1;
                    }
                    return;
                }
                foreach (var m in _bytes!.EnumerateMatches(window, from))
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

        [ThreadStatic] static char[]? t_nearChars;
        [ThreadStatic] static TextMap? t_nearMap;
        [ThreadStatic] static Regex?[]? t_ownOf, t_own;

        /// <summary>
        /// This thread's own copy of a regex used once per place (the real regex in a stretch, a run's ends). A Regex keeps
        /// one runner for its calls; the threads searching side by side each took a new one on nearly every call (the
        /// near route cost about 0.95 µs a place on Starfield - Terrain02.ba2's 29 million places, the pattern over the
        /// bytes 0.18 µs a hit). Made once per thread and regex: compiled again, as the original.
        /// </summary>
        static Regex Own(Regex r, int slot)
        {
            t_ownOf ??= new Regex?[3];
            t_own ??= new Regex?[3];
            if (!ReferenceEquals(t_ownOf[slot], r)) { t_own[slot] = new Regex(r.ToString(), r.Options, r.MatchTimeout); t_ownOf[slot] = r; }
            return t_own[slot]!;
        }

        /// <summary>
        /// A regex with a back-reference, or whose hit may start at nearly any byte: each place of a rare character every
        /// hit holds (<see cref="_near"/>, on the bytes)
        /// gives a stretch where a hit holding it may start (up to <see cref="_nearHit"/> bytes before it) and the text
        /// its look-arounds may see (<see cref="_nearLook"/> bytes more each side); stretches whose text overlaps
        /// are joined. Each is read as text from a character's start and matched by the real regex from the stretch's first
        /// possible start. A hit holds a place, so no hit starts outside the stretches; the text read in a stretch is the
        /// whole piece's there, its edges farther than any look reaches.
        /// </summary>
        void FindNear(ReadOnlySpan<byte> bytes, ReadOnlySpan<char> window, int from, int before, Action<int, int> hit)
        {
            if (_runBack != null || _runStops != null) { FindInRuns(bytes, window, from, before, hit); return; }
            int n = bytes.Length;
            int stop = (int)Math.Min(n, (long)before + _nearHit);
            int searchFrom = -1, lastPlace = -1, contextFrom = -1, contextTo = -1;
            foreach (var m in _near!.EnumerateMatches(window[..stop], from))
            {
                int c = m.Index;
                int s = Math.Max(from, c - _nearHit), cf = Math.Max(0, s - _nearLook), ct = (int)Math.Min(n, (long)c + _nearHit + _nearLook);
                if (searchFrom >= 0 && cf <= contextTo) { lastPlace = c; contextTo = Math.Max(contextTo, ct); continue; }
                if (searchFrom >= 0) Stretch(bytes, contextFrom, contextTo, searchFrom, Math.Min(lastPlace, before - 1), hit);
                searchFrom = s; lastPlace = c; contextFrom = cf; contextTo = ct;
            }
            if (searchFrom >= 0) Stretch(bytes, contextFrom, contextTo, searchFrom, Math.Min(lastPlace, before - 1), hit);
        }

        /// <summary>
        /// A regex with no limit to a hit's length (\w+ö): a hit holds only characters some atom of it can match, so it lies
        /// inside one unbroken run of them, and holds a place of the rare character. Each place's run is found on the bytes
        /// (the forms of those characters from the place backwards and forwards), and read as text with the text its
        /// look-arounds may see; the real regex is matched there from the run's start. A run without a place holds no hit.
        /// </summary>
        void FindInRuns(ReadOnlySpan<byte> bytes, ReadOnlySpan<char> window, int from, int before, Action<int, int> hit)
        {
            int n = bytes.Length;
            int searchFrom = -1, lastPlace = -1, contextFrom = -1, contextTo = -1, runEnd = -1;
            foreach (var m in _near!.EnumerateMatches(window, from))
            {
                int c = m.Index;
                if (c < runEnd) { lastPlace = c; continue; }
                int start, end;
                if (_runStops != null)
                {
                    start = bytes[..c].LastIndexOfAny(_runStops) + 1;
                    int stop = bytes[c..].IndexOfAny(_runStops);
                    end = stop < 0 ? n : c + stop;
                }
                else
                {
                    var back = Own(_runBack!, 1).EnumerateMatches(window, c);
                    start = back.MoveNext() ? back.Current.Index : c;
                    var on = Own(_runOn!, 2).EnumerateMatches(window, c);
                    end = on.MoveNext() ? on.Current.Index + on.Current.Length : c;
                }
                if (start >= before) break;
                int s = Math.Max(from, start), cf = Math.Max(0, s - _nearLook), ct = (int)Math.Min(n, (long)end + _nearLook);
                runEnd = Math.Max(end, c + 1);
                if (searchFrom >= 0 && cf <= contextTo) { lastPlace = c; contextTo = Math.Max(contextTo, ct); continue; }
                if (searchFrom >= 0) Stretch(bytes, contextFrom, contextTo, searchFrom, Math.Min(lastPlace, before - 1), hit);
                searchFrom = s; lastPlace = c; contextFrom = cf; contextTo = ct;
            }
            if (searchFrom >= 0) Stretch(bytes, contextFrom, contextTo, searchFrom, Math.Min(lastPlace, before - 1), hit);
        }

        /// <summary>The real regex on bytes [from, to) read as text: its hits that start from byte
        /// <paramref name="searchFrom"/> to byte <paramref name="lastStart"/>.</summary>
        void Stretch(ReadOnlySpan<byte> bytes, int from, int to, int searchFrom, int lastStart, Action<int, int> hit)
        {
            if (lastStart < searchFrom) return;
            // A byte 80 to BF may be inside a character that starts up to 3 bytes before
            if (bytes[from] is >= 0x80 and <= 0xBF)
                for (int k = 1; k <= 3 && from - k >= 0; k++)
                {
                    byte y = bytes[from - k];
                    if (y is >= 0x80 and <= 0xBF) continue;
                    if (y is >= 0xC2 and <= 0xF4 && Utf8Length(bytes, from - k) > k) from -= k;
                    break;
                }
            if (t_nearChars == null || t_nearChars.Length < to - from) t_nearChars = new char[Math.Max(4096, to - from)];
            var map = t_nearMap ??= new TextMap();
            int count = ReadText(bytes[from..to], t_nearChars, map);
            var text = new ReadOnlySpan<char>(t_nearChars, 0, count);
            foreach (var m in Own(_regex!, 0).EnumerateMatches(text, map.CharOf(searchFrom - from)))
            {
                int at = from + map.ByteOf(m.Index);
                if (at > lastStart) return;
                if (m.Length == 0) continue;
                hit(at, from + map.ByteOf(m.Index + m.Length) - at);
            }
        }

        /// <summary>Every UTF-16 code unit once, surrogate halves included: the characters an atom matches are found by
        /// matching it here.</summary>
        static readonly Lazy<string> AllUnits = new(() => string.Create(0x10000, 0, (span, _) => { for (int c = 0; c < span.Length; c++) span[c] = (char)c; }));

        /// <summary>A plain text's spellings over the bytes: each character as every character its ordinal ignored case
        /// equals, in its byte forms; a character outside the BMP (two halves) as every one its ordinal ignored case
        /// equals (Deseret letters have a case), in its 4-byte UTF-8 (J80).</summary>
        internal static string BytesOfText(string[] spellings)
        {
            var cache = new Dictionary<string, string>(StringComparer.Ordinal);
            var parts = new List<string>();
            foreach (var sp in spellings)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < sp.Length; i++)
                {
                    bool pair = char.IsHighSurrogate(sp[i]) && i + 1 < sp.Length && char.IsLowSurrogate(sp[i + 1]);
                    string one = sp.Substring(i, pair ? 2 : 1);
                    if (!cache.TryGetValue(one, out var unit))
                    {
                        if (pair)
                        {
                            var forms = new List<string>();
                            var two = new char[2];
                            for (int cp = 0x10000; cp <= 0x10FFFF; cp++)
                            {
                                new Rune(cp).EncodeToUtf16(two);
                                if (MemoryExtensions.Equals(two.AsSpan(), one, StringComparison.OrdinalIgnoreCase))
                                    forms.Add(string.Concat(Encoding.UTF8.GetBytes(two).Select(b => Hex(b))));
                            }
                            unit = "(?:" + string.Join("|", forms) + ")";
                        }
                        else
                        {
                            var set = new bool[0x10000];
                            for (int c = 0; c < 0x10000; c++) set[c] = string.Equals(((char)c).ToString(), one, StringComparison.OrdinalIgnoreCase);
                            unit = Unit(set);
                        }
                        cache[one] = unit;
                    }
                    sb.Append(unit);
                    if (pair) i++;
                }
                parts.Add(sb.ToString());
            }
            return string.Join("|", parts);
        }

        /// <summary>
        /// What a regex outside ASCII is made into: a pattern over the bytes (none with a back-reference), and how many
        /// byte values can start a hit (<see cref="LeadBytes"/>); the forms of a character every hit holds and the lengths
        /// around it, for a search near its places (<see cref="_near"/>), and how many byte values start those forms; why
        /// a route can't be had.
        /// </summary>
        internal readonly record struct Made(string? Pattern, int LeadBytes, string? Near, int NearLeadBytes, int HitBytes, int LookBytes, string? Run, string? Reason)
        {
            /// <summary>The pattern repeats an atom of many characters with no limit, or has a \b of four look-arounds.</summary>
            public bool Heavy { get; init; }
            /// <summary>For a run: the ASCII bytes that end it, when every character a hit can't hold is ASCII.</summary>
            public byte[]? RunStops { get; init; }
        }

        /// <summary>A pattern over the bytes whose hits start with at most this many byte values is matched over them: the
        /// regex skips the other bytes at once ([à-ÿ], 63 values, ran 208 MB/s a thread on the game's meshes against 190
        /// read as text). One that may start at more (., \w, \p{Lu}, a negated class first: 123 values and up) is tried
        /// at nearly every byte of a binary file: 5-90 MB/s against 120-550 read as text (notes\fix-j80-regex-bytes.md).
        /// Such a pattern is matched near a rare character every hit holds, or read as text.</summary>
        internal const int FewLeadBytes = 64;

        internal static Made BytesOfRegexMade(string p)
        {
            var reader = new Reader(p);
            Node tree;
            try { tree = reader.Read(); }
            catch (FormatException) { return new(null, 256, null, 256, 0, 0, null, "it couldn't be read into a pattern over the bytes"); }
            var writer = new Writer();
            string? pattern = reader.HasBackReference ? null : writer.Emit(tree);
            int lead = pattern == null ? 256 : writer.LeadBytes(tree);
            // Near a character's places: the real regex on the text read around them
            string? reason = null, near = null, run = null;
            byte[]? runStops = null;
            int nearLead = 256, hitBytes = 0, lookBytes = 0;
            int max = Length(tree, reader).Max, look = Look(tree, reader);
            var place = Needed(tree).Select(a => (Atom: a, Set: writer.Chars(a))).Where(x => x.Set != null)
                .OrderBy(x => FirstBytes(x.Set!).Count).ThenBy(x => x.Set!.Count(b => b)).FirstOrDefault();
            if (reader.HasG) reason = "it has \\G (where the last hit ended), which a search near a character's places can't keep";
            else if (look < 0) reason = "its look-arounds have no limit to their length, so no stretch of text around a character can be sure to hold what they see";
            else if (place.Set == null) reason = "it has no character every hit holds outside a look-around, alternative or optional part";
            else
            {
                // A character of the text read is 1 to 3 bytes (one outside the BMP is two halves: 1 and 3 bytes)
                near = Unit(place.Set);
                nearLead = FirstBytes(place.Set).Count;
                hitBytes = 3 * Math.Max(max, 0) + 3;
                lookBytes = 3 * (look + 1) + 8;
                if (max < 0)
                {
                    // No length limit: the run of the characters a hit can hold around each place (a back-reference repeats a
                    // text in either case: its characters' other cases too)
                    var chars = writer.Consumed(tree, reader.HasBackReference);
                    if (chars != null && chars.Contains(false))
                    {
                        run = Unit(chars);
                        if (Array.IndexOf(chars, false, 0x80) < 0) runStops = Enumerable.Range(0, 0x80).Where(b => !chars[b]).Select(b => (byte)b).ToArray();
                    }
                    else { near = null; reason = "it has no limit to a hit's length (*, + or {n,}) and can hold any character, so no stretch of text around a character can be sure to hold a whole hit"; }
                }
            }
            if (pattern == null && reader.HasBackReference) reason = reason == null ? null : "it has a back-reference (\\1, \\k<name>: the same text again, in either case and either encoding), which can't be matched on the bytes, and " + reason;
            else if (pattern == null) reason = "a part of it couldn't be made into byte forms";
            return new(pattern, lead, near, nearLead, hitBytes, lookBytes, run, reason) { Heavy = pattern != null && writer.Heavy(tree), RunStops = runStops };
        }

        /// <summary>The byte values that can start one of the characters' forms (<see cref="Unit"/>).</summary>
        static HashSet<int> FirstBytes(bool[] set)
        {
            var first = new HashSet<int>();
            for (int c = 0; c < 0x10000; c++)
            {
                if (!set[c]) continue;
                if (c < 0x80) first.Add(c);
                else if (c < 0x800) first.Add(0xC0 | c >> 6);
                else if (c is >= 0xD800 and <= 0xDBFF) first.Add(0xF0 | (0x10000 + ((c - 0xD800) << 10)) >> 18);
                else if (c is >= 0xDC00 and <= 0xDFFF) { for (int b = 0x80; b <= 0xBF; b++) first.Add(b); }
                else first.Add(0xE0 | c >> 12);
            }
            for (int b = 0x80; b <= 0xFF; b++) if (set[b < 0xA0 ? Cp1252High[b - 0x80] : b]) first.Add(b);
            return first;
        }

        // ---------------------------------------------------------------- the regex, read

        abstract record Node;
        /// <summary>One character of the text read: a letter, an escape, a class, the dot; with the options it is read under.</summary>
        sealed record AtomNode(string Text, bool IgnoreCase, bool Singleline) : Node;
        /// <summary>A zero-width part written as it is: ^ $ \A \z \Z \G, an option (?m) or (?n).</summary>
        sealed record Literal(string Text) : Node;
        sealed record Boundary(bool Not) : Node;
        sealed record BackReference : Node;
        sealed record Sequence(List<Node> Items) : Node;
        sealed record Alternation(List<Node> Items) : Node;
        /// <summary>A group: its opening as written in the pattern over the bytes; Kind 'c' captures, 'n' doesn't,
        /// 'a' a look-ahead, 'b' a look-behind.</summary>
        sealed record Group(string Open, Node Body, char Kind) : Node;
        sealed record Quantified(Node Body, string Quantifier, int Min, int Max) : Node;
        /// <summary>A conditional: (?(name)yes|no) (Test null, Open with the name), or a test of an expression.</summary>
        sealed record Conditional(string Open, Group? Test, Node Body) : Node;

        /// <summary>
        /// Reads a .NET regular expression into its parts, the inline options applied as .NET applies them (to the rest of
        /// the group they are in, past a |). Throws <see cref="FormatException"/> on what it doesn't know.
        /// </summary>
        sealed class Reader(string p)
        {
            int _i;
            bool _ic = true, _sl, _x;
            readonly HashSet<string> _names = Regex.Matches(p, @"\(\?[<']([A-Za-z_]\w*)(?:-\w+)?[>']").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            public bool HasBackReference { get; private set; }
            public bool HasG { get; private set; }
            /// <summary>The capture groups, for a back-reference's length.</summary>
            public List<Group> Captures { get; } = new();

            public Node Read()
            {
                var n = ReadAlternation();
                if (_i < p.Length) throw new FormatException("unbalanced )");
                return n;
            }

            /// <summary>.NET's blanks in x mode: tab, line feed, form feed, carriage return, space (not \v, not U+00A0).</summary>
            static bool Blank(char c) => c is '\t' or '\n' or '\f' or '\r' or ' ';

            void SkipBlank()
            {
                while (_i < p.Length)
                {
                    if (string.CompareOrdinal(p, _i, "(?#", 0, 3) == 0) { int close = p.IndexOf(')', _i); if (close < 0) throw new FormatException("comment"); _i = close + 1; }
                    else if (_x && Blank(p[_i])) _i++;
                    else if (_x && p[_i] == '#') { int nl = p.IndexOf('\n', _i); _i = nl < 0 ? p.Length : nl + 1; }
                    else break;
                }
            }

            Node ReadAlternation()
            {
                var branches = new List<Node> { ReadSequence() };
                while (_i < p.Length && p[_i] == '|') { _i++; branches.Add(ReadSequence()); }
                return branches.Count == 1 ? branches[0] : new Alternation(branches);
            }

            Node ReadSequence()
            {
                var items = new List<Node>();
                while (true)
                {
                    SkipBlank();
                    if (_i >= p.Length || p[_i] is '|' or ')') break;
                    var atom = ReadPrimary();
                    if (atom == null) continue;
                    SkipBlank();
                    items.Add(ReadQuantifier(atom));
                }
                return new Sequence(items);
            }

            Node ReadQuantifier(Node body)
            {
                if (_i >= p.Length) return body;
                int start = _i, min, max;
                char c = p[_i];
                if (c == '*') { min = 0; max = -1; _i++; }
                else if (c == '+') { min = 1; max = -1; _i++; }
                else if (c == '?') { min = 0; max = 1; _i++; }
                else if (c == '{' && Regex.Match(p[_i..], @"^\{(\d+)(,(\d*))?\}") is { Success: true } q)
                {
                    min = int.Parse(q.Groups[1].Value, CultureInfo.InvariantCulture);
                    max = !q.Groups[2].Success ? min : q.Groups[3].Value.Length == 0 ? -1 : int.Parse(q.Groups[3].Value, CultureInfo.InvariantCulture);
                    _i += q.Length;
                }
                else return body;
                if (_i < p.Length && p[_i] == '?') _i++;
                return new Quantified(body, p[start.._i], min, max);
            }

            AtomNode Atom(string text) => new(text, _ic, _sl);

            /// <summary>One part; null for an option that only changes how the rest is read (i, s, x).</summary>
            Node? ReadPrimary()
            {
                char c = p[_i];
                switch (c)
                {
                    case '(': return ReadGroup();
                    case '[':
                    {
                        int end = SkipClass(p, _i);
                        if (end < 0) throw new FormatException("class");
                        var a = Atom(p[_i..(end + 1)]); _i = end + 1; return a;
                    }
                    case '\\': return ReadEscape();
                    case '.': _i++; return Atom(".");
                    case '^' or '$': _i++; return new Literal(c.ToString());
                    case '{' when !Regex.IsMatch(p[_i..], @"^\{\d+(,\d*)?\}"): _i++; return Atom("\\{");
                    case '*' or '+' or '?' or '{': throw new FormatException("quantifier following nothing");
                    default: _i++; return Atom(Regex.Escape(c.ToString()));
                }
            }

            Node ReadEscape()
            {
                if (_i + 1 >= p.Length) throw new FormatException("escape");
                char e = p[_i + 1];
                if (e is 'b' or 'B') { _i += 2; return new Boundary(e == 'B'); }
                if (e is 'A' or 'z' or 'Z' or 'G') { HasG |= e == 'G'; _i += 2; return new Literal("\\" + e); }
                if (e is >= '1' and <= '9')
                {
                    int j = _i + 1;
                    while (j < p.Length && char.IsAsciiDigit(p[j])) j++;
                    _i = j; HasBackReference = true; return new BackReference();
                }
                if (e == 'k' && _i + 2 < p.Length && p[_i + 2] is '<' or '\'' || e is '<' or '\'')
                {
                    int open = e == 'k' ? _i + 2 : _i + 1;
                    int close = p.IndexOf(p[open] == '<' ? '>' : '\'', open + 1);
                    if (close < 0) throw new FormatException("back-reference");
                    _i = close + 1; HasBackReference = true; return new BackReference();
                }
                if (e == '0')
                {
                    // An octal code: up to three octal digits, the 0 among them (\012 is a line feed)
                    int j = _i + 1;
                    while (j < p.Length && j < _i + 4 && p[j] is >= '0' and <= '7') j++;
                    var a = Atom(p[_i..j]); _i = j; return a;
                }
                int end = SkipEscape(p, _i);
                if (end < 0) throw new FormatException("escape");
                var atom = Atom(p[_i..(end + 1)]); _i = end + 1; return atom;
            }

            Node? ReadGroup()
            {
                bool ic = _ic, sl = _sl, x = _x;
                try
                {
                    if (_i + 1 >= p.Length || p[_i + 1] != '?') { _i++; return Close("(", 'c'); }
                    string rest = p[_i..];
                    foreach (var (open, kind) in new[] { ("(?:", 'n'), ("(?>", 'n'), ("(?=", 'a'), ("(?!", 'a'), ("(?<=", 'b'), ("(?<!", 'b') })
                        if (rest.StartsWith(open, StringComparison.Ordinal)) { _i += open.Length; return Close(open, kind); }
                    if (rest.StartsWith("(?<", StringComparison.Ordinal) || rest.StartsWith("(?'", StringComparison.Ordinal))
                    {
                        int close = p.IndexOf(rest[2] == '<' ? '>' : '\'', _i + 3);
                        if (close < 0) throw new FormatException("group name");
                        string open = p[_i..(close + 1)];
                        _i = close + 1;
                        return Close(open, 'c');
                    }
                    if (rest.StartsWith("(?(", StringComparison.Ordinal)) return ReadConditional();
                    var o = Regex.Match(rest, @"^\(\?([imnsx]*)(?:-([imnsx]*))?([:)])");
                    if (!o.Success) throw new FormatException("group");
                    _i += o.Length;
                    string on = o.Groups[1].Value, off = o.Groups[2].Value;
                    if (on.Contains('i')) _ic = true;
                    if (off.Contains('i')) _ic = false;
                    if (on.Contains('s')) _sl = true;
                    if (off.Contains('s')) _sl = false;
                    if (on.Contains('x')) _x = true;
                    if (off.Contains('x')) _x = false;
                    // m and n mean the same over the bytes: kept as written
                    string keepOn = string.Concat(on.Where(ch => ch is 'm' or 'n')), keepOff = string.Concat(off.Where(ch => ch is 'm' or 'n'));
                    string kept = keepOn + (keepOff.Length > 0 ? "-" + keepOff : "");
                    if (o.Groups[3].Value == ")")
                    {
                        // To the end of the group it is in: the options stay changed
                        ic = _ic; sl = _sl; x = _x;
                        return kept.Length > 0 ? new Literal("(?" + kept + ")") : null;
                    }
                    return Close(kept.Length > 0 ? "(?" + kept + ":" : "(?:", 'n');
                }
                finally { _ic = ic; _sl = sl; _x = x; }
            }

            Group Close(string open, char kind)
            {
                var body = ReadAlternation();
                if (_i >= p.Length || p[_i] != ')') throw new FormatException("unclosed group");
                _i++;
                var g = new Group(open, body, kind);
                if (kind == 'c') Captures.Add(g);
                return g;
            }

            Node ReadConditional()
            {
                int at = _i + 2;
                Group? test = null;
                string open;
                string after = p[at..];
                if (after.StartsWith("(?=", StringComparison.Ordinal) || after.StartsWith("(?!", StringComparison.Ordinal) || after.StartsWith("(?<=", StringComparison.Ordinal) || after.StartsWith("(?<!", StringComparison.Ordinal))
                {
                    _i = at;
                    test = ReadGroup() as Group ?? throw new FormatException("conditional");
                    open = "(?";
                }
                else
                {
                    int close = p.IndexOf(')', at);
                    if (close < 0) throw new FormatException("conditional");
                    string name = p[(at + 1)..close];
                    if (name.Length > 0 && (name.All(char.IsAsciiDigit) || _names.Contains(name))) { _i = close + 1; open = "(?(" + name + ")"; }
                    else
                    {
                        // An expression: .NET tests it as a look-ahead
                        _i = at + 1;
                        var body = ReadAlternation();
                        if (_i >= p.Length || p[_i] != ')') throw new FormatException("conditional");
                        _i++;
                        test = new Group("(?=", body, 'a');
                        open = "(?";
                    }
                }
                var yesNo = ReadAlternation();
                if (_i >= p.Length || p[_i] != ')') throw new FormatException("conditional");
                _i++;
                return new Conditional(open, test, yesNo);
            }
        }

        // ---------------------------------------------------------------- the pattern over the bytes

        /// <summary>Writes the parts as a pattern over the bytes; the characters of each atom found once per pattern.</summary>
        sealed class Writer
        {
            readonly Dictionary<(string, bool, bool), bool[]?> _atoms = new();
            /// <summary>A \b or \B next to an atom whose characters are all word characters, or none: one look-around.</summary>
            readonly Dictionary<Boundary, string> _bounds = new(ReferenceEqualityComparer.Instance);

            public string? Emit(Node n)
            {
                switch (n)
                {
                    case AtomNode a: return Chars(a) is { } set ? Unit(set) : null;
                    case Literal l: return l.Text;
                    case Boundary b:
                    {
                        if (_bounds.TryGetValue(b, out var one)) return one;
                        string w = WordUnit.Value;
                        return b.Not ? $"(?:(?<={w})(?={w})|(?<!{w})(?!{w}))" : $"(?:(?<={w})(?!{w})|(?<!{w})(?={w}))";
                    }
                    case Sequence s:
                    {
                        var sb = new StringBuilder();
                        for (int k = 0; k < s.Items.Count; k++)
                        {
                            if (s.Items[k] is Boundary b && !_bounds.ContainsKey(b) && Simple(b, k > 0 ? s.Items[k - 1] : null, k + 1 < s.Items.Count ? s.Items[k + 1] : null) is { } one) _bounds[b] = one;
                            if (Emit(s.Items[k]) is not { } e) return null;
                            sb.Append(e);
                        }
                        return sb.ToString();
                    }
                    case Alternation alt:
                    {
                        var parts = new List<string>();
                        foreach (var item in alt.Items) { if (Emit(item) is not { } e) return null; parts.Add(e); }
                        return string.Join("|", parts);
                    }
                    case Group g: return Emit(g.Body) is { } inside ? g.Open + inside + ")" : null;
                    case Quantified q: return Emit(q.Body) is { } inner ? (q.Body is Literal ? "(?:" + inner + ")" : inner) + q.Quantifier : null;
                    case Conditional c:
                    {
                        if (Emit(c.Body) is not { } body) return null;
                        if (c.Test == null) return c.Open + body + ")";
                        return Emit(c.Test) is { } test ? "(?" + test + body + ")" : null;
                    }
                    default: return null;
                }
            }

            /// <summary>
            /// \b between a known character and its neighbour: the character after it (an atom, or one repeated at least
            /// once) all word characters means "no word character before" ((?&lt;!W)), none of them "a word character
            /// before"; else the character before it the same way, looking ahead. \B the other way round. One look-around,
            /// not four, and the regex can skip to the atom (\bö first ran 262 MB/s a thread on the game's meshes as four).
            /// </summary>
            string? Simple(Boundary b, Node? before, Node? after)
            {
                bool[]? Of(Node? n) => n switch { AtomNode a => Chars(a), Quantified { Min: >= 1, Body: AtomNode a } => Chars(a), _ => null };
                var word = WordSet.Value;
                string w = WordUnit.Value;
                static bool All(bool[] s, bool[] word) { for (int c = 0; c < s.Length; c++) if (s[c] && !word[c]) return false; return true; }
                static bool None(bool[] s, bool[] word) { for (int c = 0; c < s.Length; c++) if (s[c] && word[c]) return false; return true; }
                if (Of(after) is { } next && next.Any(x => x))
                {
                    if (All(next, word)) return b.Not ? $"(?<={w})" : $"(?<!{w})";
                    if (None(next, word)) return b.Not ? $"(?<!{w})" : $"(?<={w})";
                }
                if (Of(before) is { } last && last.Any(x => x))
                {
                    if (All(last, word)) return b.Not ? $"(?={w})" : $"(?!{w})";
                    if (None(last, word)) return b.Not ? $"(?!{w})" : $"(?={w})";
                }
                return null;
            }

            /// <summary>How many byte values can start a hit of the pattern (256 when it may start anywhere: an atom of
            /// many characters first, a \b of four look-arounds, a hit that may be empty). Call after <see cref="Emit"/>.</summary>
            public int LeadBytes(Node tree)
            {
                var (first, empty) = First(tree);
                return first == null || empty ? 256 : first.Count;
            }

            (HashSet<int>? Bytes, bool Empty) First(Node n)
            {
                switch (n)
                {
                    case AtomNode a: return (Chars(a) is { } set ? FirstBytes(set) : null, false);
                    case Literal: return (new(), true);
                    case Boundary b: return (_bounds.ContainsKey(b) ? new() : null, true);
                    case Group { Kind: 'a' or 'b' }: return (new(), true);
                    case Group g: return First(g.Body);
                    case Sequence s:
                    {
                        var all = new HashSet<int>();
                        foreach (var item in s.Items)
                        {
                            var (f, e) = First(item);
                            if (f == null) return (null, true);
                            all.UnionWith(f);
                            if (!e) return (all, false);
                        }
                        return (all, true);
                    }
                    case Alternation alt:
                    {
                        var all = new HashSet<int>();
                        bool empty = false;
                        foreach (var item in alt.Items) { var (f, e) = First(item); if (f == null) return (null, true); all.UnionWith(f); empty |= e; }
                        return (all, empty);
                    }
                    case Quantified q: { var (f, e) = First(q.Body); return (f, e || q.Min == 0); }
                    default: return (null, true);
                }
            }

            /// <summary>The pattern repeats an atom of many characters (its forms start with more than
            /// <see cref="FewLeadBytes"/> byte values) with no limit, or holds a \b that stayed four look-arounds. Call after
            /// <see cref="Emit"/>.</summary>
            public bool Heavy(Node n) => n switch
            {
                Boundary b => !_bounds.ContainsKey(b),
                Quantified { Max: < 0 } q => Atoms(q.Body, true).Any(a => Chars(a) is { } set && FirstBytes(set).Count > FewLeadBytes) || Heavy(q.Body),
                Quantified q => Heavy(q.Body),
                Sequence s => s.Items.Any(Heavy),
                Alternation alt => alt.Items.Any(Heavy),
                Group g => Heavy(g.Body),
                Conditional c => Heavy(c.Body) || c.Test != null && Heavy(c.Test),
                _ => false,
            };

            /// <summary>Every character a hit can hold: what its atoms outside look-arounds match (<paramref name="all"/>, for a
            /// back-reference: every atom's, with letter case ignored, as a back-reference repeats a text in either case);
            /// null when an atom isn't known.</summary>
            public bool[]? Consumed(Node tree, bool all)
            {
                var set = new bool[0x10000];
                foreach (var a in Atoms(tree, all))
                {
                    if (Chars(all ? a with { IgnoreCase = true } : a) is not { } one) return null;
                    for (int c = 0; c < one.Length; c++) set[c] |= one[c];
                }
                return set;
            }

            static IEnumerable<AtomNode> Atoms(Node n, bool look) => n switch
            {
                AtomNode a => new[] { a },
                Sequence s => s.Items.SelectMany(i => Atoms(i, look)),
                Alternation alt => alt.Items.SelectMany(i => Atoms(i, look)),
                Group { Kind: 'a' or 'b' } g => look ? Atoms(g.Body, look) : Array.Empty<AtomNode>(),
                Group g => Atoms(g.Body, look),
                Quantified q => Atoms(q.Body, look),
                Conditional c => Atoms(c.Body, look).Concat(look && c.Test != null ? Atoms(c.Test, look) : Array.Empty<AtomNode>()),
                _ => Array.Empty<AtomNode>(),
            };

            /// <summary>The characters (UTF-16 code units, surrogate halves included) an atom matches under its options,
            /// its codes exact (<see cref="ExactCodes"/>); null when it matches anything but one code unit.</summary>
            public bool[]? Chars(AtomNode a)
            {
                var key = (a.Text, a.IgnoreCase, a.Singleline);
                if (_atoms.TryGetValue(key, out var known)) return known;
                bool[]? set = new bool[0x10000];
                try
                {
                    var options = RegexOptions.CultureInvariant | (a.IgnoreCase ? RegexOptions.IgnoreCase : 0) | (a.Singleline ? RegexOptions.Singleline : 0);
                    var r = new Regex(a.IgnoreCase ? ExactCodes(a.Text) : a.Text, options);
                    foreach (var m in r.EnumerateMatches(AllUnits.Value))
                    {
                        if (m.Length != 1) { set = null; break; }
                        set[m.Index] = true;
                    }
                }
                catch (ArgumentException) { set = null; }
                return _atoms[key] = set;
            }
        }

        /// <summary>The characters .NET's \b counts as a word's (measured: \w and U+200C, U+200D), in their byte forms.</summary>
        static readonly Lazy<bool[]> WordSet = new(() =>
        {
            var word = new Regex(@"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var set = new bool[0x10000];
            for (int c = 0; c < 0x10000; c++) set[c] = word.IsMatch(((char)c).ToString());
            return set;
        });
        static readonly Lazy<string> WordUnit = new(() => Unit(WordSet.Value));

        /// <summary>The atoms every hit holds (outside look-arounds, alternatives and optional parts).</summary>
        static IEnumerable<AtomNode> Needed(Node n) => n switch
        {
            AtomNode a => new[] { a },
            Sequence s => s.Items.SelectMany(Needed),
            Group { Kind: 'c' or 'n' } g => Needed(g.Body),
            Quantified { Min: >= 1 } q => Needed(q.Body),
            _ => Array.Empty<AtomNode>(),
        };

        /// <summary>A part's shortest and longest length in characters of the text read (-1: no limit).</summary>
        static (int Min, int Max) Length(Node n, Reader r)
        {
            const long Most = 1 << 20;
            static int Add(int a, int b) => a < 0 || b < 0 ? -1 : (int)Math.Min(Most, (long)a + b);
            switch (n)
            {
                case AtomNode: return (1, 1);
                case Literal or Boundary: return (0, 0);
                case BackReference:
                {
                    // As long as the longest group; two more for digits read as letters after a group's number (\12)
                    int max = 0;
                    foreach (var g in r.Captures) { int l = Length(g.Body, r).Max; if (l < 0) return (0, -1); max = Math.Max(max, l); }
                    return (0, max + 2);
                }
                case Sequence s:
                {
                    int min = 0, max = 0;
                    foreach (var item in s.Items) { var l = Length(item, r); min = Add(min, l.Min); max = Add(max, l.Max); }
                    return (min, max);
                }
                case Alternation alt:
                {
                    var ls = alt.Items.Select(i => Length(i, r)).ToList();
                    return (ls.Min(l => l.Min), ls.Any(l => l.Max < 0) ? -1 : ls.Max(l => l.Max));
                }
                case Group g: return g.Kind is 'a' or 'b' ? (0, 0) : Length(g.Body, r);
                case Quantified q:
                {
                    var l = Length(q.Body, r);
                    int max = l.Max == 0 ? 0 : q.Max < 0 || l.Max < 0 ? -1 : (int)Math.Min(Most, (long)l.Max * q.Max);
                    return ((int)Math.Min(Most, (long)Math.Max(0, l.Min) * q.Min), max);
                }
                case Conditional c: return Length(c.Body, r);
                default: return (0, -1);
            }
        }

        /// <summary>How far, in characters, the look-arounds may look past a hit's ends: each look-around's longest
        /// content, added up (-1: no limit).</summary>
        static int Look(Node n, Reader r)
        {
            int Sum(IEnumerable<Node> items) { int t = 0; foreach (var i in items) { int l = Look(i, r); if (l < 0) return -1; t += l; } return t; }
            switch (n)
            {
                case Sequence s: return Sum(s.Items);
                case Alternation alt: return Sum(alt.Items);
                case Group g:
                {
                    int inner = Look(g.Body, r);
                    if (g.Kind is not ('a' or 'b')) return inner;
                    int len = Length(g.Body, r).Max;
                    return inner < 0 || len < 0 ? -1 : inner + len;
                }
                case Quantified q: return Look(q.Body, r);
                case Conditional c:
                {
                    int body = Look(c.Body, r);
                    if (c.Test == null) return body;
                    int test = Look(c.Test, r);
                    return body < 0 || test < 0 ? -1 : body + test;
                }
                default: return 0;
            }
        }

        // ---------------------------------------------------------------- characters as byte forms

        /// <summary>
        /// Characters (UTF-16 code units, by <paramref name="set"/>) as one unit of a pattern over the bytes, matching one
        /// character of the text read exactly where the reading puts it:
        /// - an ASCII character: its byte;
        /// - one of 2 or 3 bytes in UTF-8: a lead byte and the continuation bytes it takes, grouped by lead;
        /// - a byte read as Windows-1252 (its character in the set): with the conditions under which the reading takes it
        ///   alone (<see cref="Forms1252"/>);
        /// - a half of a character outside the BMP: the first half is the 4-byte character's lead byte (its other bytes
        ///   looked at ahead), the second its three other bytes (the lead looked at behind), as the reading's offsets
        ///   put them.
        /// </summary>
        internal static string Unit(bool[] set)
        {
            var alternatives = new List<string>();
            var ascii = Enumerable.Range(0, 0x80).Where(c => set[c]).ToList();
            if (ascii.Count > 0) alternatives.Add(Class(ascii));
            // 2 bytes: U+0080 to U+07FF
            var two = new Dictionary<int, ulong>();
            for (int c = 0x80; c < 0x800; c++)
                if (set[c]) { int lead = 0xC0 | c >> 6; two[lead] = two.GetValueOrDefault(lead) | 1UL << (c & 0x3F); }
            foreach (var g in two.GroupBy(kv => kv.Value).OrderBy(g => g.Min(kv => kv.Key)))
                alternatives.Add(Class(g.Select(kv => kv.Key)) + Tails(g.Key));
            // 3 bytes: U+0800 to U+FFFF but the surrogates
            var three = new SortedDictionary<int, string>();
            for (int lead = 0xE0; lead <= 0xEF; lead++)
            {
                var seconds = new Dictionary<int, ulong>();
                for (int b2 = 0; b2 < 64; b2++)
                {
                    ulong tails = 0;
                    for (int b3 = 0; b3 < 64; b3++)
                    {
                        int c = (lead & 0x0F) << 12 | b2 << 6 | b3;
                        if (c >= 0x800 && c is < 0xD800 or > 0xDFFF && set[c]) tails |= 1UL << b3;
                    }
                    if (tails != 0) seconds[0x80 | b2] = tails;
                }
                if (seconds.Count == 0) continue;
                var parts = seconds.GroupBy(kv => kv.Value).OrderBy(g => g.Min(kv => kv.Key)).Select(g => Class(g.Select(kv => kv.Key)) + Tails(g.Key)).ToList();
                three[lead] = parts.Count == 1 ? parts[0] : "(?:" + string.Join("|", parts) + ")";
            }
            foreach (var g in three.GroupBy(kv => kv.Value).OrderBy(g => g.Min(kv => kv.Key)))
                alternatives.Add(Class(g.Select(kv => kv.Key)) + g.Key);
            // A byte read as Windows-1252
            alternatives.AddRange(Forms1252(Enumerable.Range(0x80, 0x80).Where(b => set[b < 0xA0 ? Cp1252High[b - 0x80] : b]).ToList()));
            // The first half of a character outside the BMP: its lead byte
            var high = new SortedDictionary<int, Dictionary<int, int>>();
            for (int h = 0xD800; h <= 0xDBFF; h++)
            {
                if (!set[h]) continue;
                int cp = 0x10000 + ((h - 0xD800) << 10);
                int lead = 0xF0 | cp >> 18, b2 = 0x80 | (cp >> 12 & 0x3F), t = cp >> 10 & 3;
                if (!high.TryGetValue(lead, out var bySecond)) high[lead] = bySecond = new();
                bySecond[b2] = bySecond.GetValueOrDefault(b2) | 1 << t;
            }
            var highs = new SortedDictionary<int, string>();
            foreach (var (lead, bySecond) in high)
            {
                var parts = bySecond.GroupBy(kv => kv.Value).OrderBy(g => g.Min(kv => kv.Key))
                    .Select(g => Class(g.Select(kv => kv.Key)) + Class(Enumerable.Range(0, 4).Where(t => (g.Key >> t & 1) != 0).SelectMany(t => Enumerable.Range(0x80 | t << 4, 16)))).ToList();
                highs[lead] = "(?=" + (parts.Count == 1 ? parts[0] : "(?:" + string.Join("|", parts) + ")") + Cont + ")";
            }
            foreach (var g in highs.GroupBy(kv => kv.Value).OrderBy(g => g.Min(kv => kv.Key)))
                alternatives.Add(Class(g.Select(kv => kv.Key)) + g.Key);
            // The second half: the three bytes after a valid lead
            var low = new Dictionary<int, ulong>();
            for (int l = 0xDC00; l <= 0xDFFF; l++)
                if (set[l]) { int v = l - 0xDC00; low[v >> 6] = low.GetValueOrDefault(v >> 6) | 1UL << (v & 0x3F); }
            if (low.Count > 0)
            {
                var parts = low.GroupBy(kv => kv.Value).OrderBy(g => g.Min(kv => kv.Key))
                    .Select(g => Class(g.SelectMany(kv => Enumerable.Range(0, 4).Select(t => 0x80 | t << 4 | kv.Key))) + Tails(g.Key)).ToList();
                alternatives.Add(@"(?:(?<=\xF0)[\x90-\xBF]|(?<=[\xF1-\xF3])[\x80-\xBF]|(?<=\xF4)[\x80-\x8F])" + (parts.Count == 1 ? parts[0] : "(?:" + string.Join("|", parts) + ")"));
            }
            return alternatives.Count == 0 ? "(?!)" : "(?:" + string.Join("|", alternatives) + ")";
        }

        /// <summary>The continuation bytes 80 to BF for the bits of <paramref name="mask"/> (bit k: 80 + k).</summary>
        static string Tails(ulong mask) => Class(Enumerable.Range(0, 64).Where(k => (mask >> k & 1) != 0).Select(k => 0x80 | k));

        static string Hex(int c) => $"\\x{c:X2}";

        /// <summary>Bytes (or ASCII characters) as one byte or a class of ranges.</summary>
        static string Class(IEnumerable<int> values)
        {
            var v = values.Distinct().Order().ToList();
            if (v.Count == 1) return Hex(v[0]);
            var sb = new StringBuilder("[");
            for (int i = 0; i < v.Count;)
            {
                int j = i;
                while (j + 1 < v.Count && v[j + 1] == v[j] + 1) j++;
                sb.Append(Hex(v[i]));
                if (j > i) sb.Append(j > i + 1 ? "-" : "").Append(Hex(v[j]));
                i = j + 1;
            }
            return sb.Append(']').ToString();
        }

        const string Cont = @"[\x80-\xBF]", Lead3 = @"(?:\xE0[\xA0-\xBF]|[\xE1-\xEC\xEE\xEF][\x80-\xBF]|\xED[\x80-\x9F])", Lead4 = @"(?:\xF0[\x90-\xBF]|[\xF1-\xF3][\x80-\xBF]|\xF4[\x80-\x8F])";

        /// <summary>
        /// Bytes read as their Windows-1252 characters, grouped: a continuation byte (80 to BF) when it isn't inside a
        /// valid UTF-8 character (not after a 2-byte lead; not the second, third or fourth byte of a valid 3- or 4-byte
        /// one); a lead byte (C2 to F4) when the bytes after it don't make a valid character; C0, C1, F5 to FF always. As
        /// <see cref="ReadText"/> decides it.
        /// </summary>
        static IEnumerable<string> Forms1252(List<int> bytes)
        {
            var cont = bytes.Where(b => b is >= 0x80 and <= 0xBF).ToList();
            if (cont.Count > 0)
                yield return "(?=" + Class(cont) + ")" + @"(?<![\xC2-\xDF])" + $"(?<!{Lead3})(?<!{Lead4}{Cont})" +
                    @"(?!(?<=\xE0)[\xA0-\xBF][\x80-\xBF])(?!(?<=[\xE1-\xEC\xEE\xEF])[\x80-\xBF]{2})(?!(?<=\xED)[\x80-\x9F][\x80-\xBF])" +
                    @"(?!(?<=\xF0)[\x90-\xBF][\x80-\xBF]{2})(?!(?<=[\xF1-\xF3])[\x80-\xBF]{3})(?!(?<=\xF4)[\x80-\x8F][\x80-\xBF]{2})" +
                    $"(?!(?<={Lead4}){Cont}{{2}})" + Class(cont);
            var leads2 = bytes.Where(b => b is >= 0xC2 and <= 0xDF).ToList();
            if (leads2.Count > 0) yield return Class(leads2) + @"(?![\x80-\xBF])";
            if (bytes.Contains(0xE0)) yield return @"\xE0(?![\xA0-\xBF][\x80-\xBF])";
            var leads3 = bytes.Where(b => b is >= 0xE1 and <= 0xEF and not 0xED).ToList();
            if (leads3.Count > 0) yield return Class(leads3) + @"(?![\x80-\xBF]{2})";
            if (bytes.Contains(0xED)) yield return @"\xED(?![\x80-\x9F][\x80-\xBF])";
            if (bytes.Contains(0xF0)) yield return @"\xF0(?![\x90-\xBF][\x80-\xBF]{2})";
            var leads4 = bytes.Where(b => b is >= 0xF1 and <= 0xF3).ToList();
            if (leads4.Count > 0) yield return Class(leads4) + @"(?![\x80-\xBF]{3})";
            if (bytes.Contains(0xF4)) yield return @"\xF4(?![\x80-\x8F][\x80-\xBF]{2})";
            var alone = bytes.Where(b => b is 0xC0 or 0xC1 or >= 0xF5).ToList();
            if (alone.Count > 0) yield return Class(alone);
        }
    }

    /// <summary>
    /// A hit found on the bytes, as the text read shows it: the bytes around it (256 each side, from a character's
    /// start) read as text, the hit's characters, and its index there; the same text as reading the whole piece gives.
    /// </summary>
    static (int Start, int Length, int Count) ReadAround(ReadOnlySpan<byte> bytes, int at, int length, char[] chars, TextMap map, out int from)
    {
        from = Math.Max(0, at - 256);
        // A byte 80 to BF may be inside a character that starts up to 3 bytes before
        if (bytes[from] is >= 0x80 and <= 0xBF)
            for (int k = 1; k <= 3 && from - k >= 0; k++)
            {
                byte y = bytes[from - k];
                if (y is >= 0x80 and <= 0xBF) continue;
                if (y is >= 0xC2 and <= 0xF4 && Utf8Length(bytes, from - k) > k) from -= k;
                break;
            }
        int to = Math.Min(bytes.Length, at + length + 256);
        int count = ReadText(bytes[from..to], chars, map);
        int start = map.CharOf(at - from);
        return (start, map.CharOf(at + length - from) - start, count);
    }
}
