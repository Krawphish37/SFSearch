// Text found by its characters (GAPS S19): every text field of a plugin's records read from the file's bytes, and
// the texts of its strings files, held against a pattern. find --text uses Search; check2's "stored-text" rule uses
// Walk with Readings. No Mutagen overlay, no JSON: the header index says where each record sits.
//  - Which subrecords are text: the schema's String fields that fill a whole subrecord (Name FULL, a book's DESC, a
//    response's NAM1, a quest objective's NNAM, ...), per record type. A subrecord that the schema also gives a field
//    of another kind in the same record type is left out (it can't be told apart from its bytes).
//  - Text stored in the plugin is read with Mutagen's own encoding (RecordNames.EncodingOf), so the text found is the
//    text record and SQL give.
//  - Text in strings files (a localized plugin's translated fields) is listed by Mutagen's own strings folder lookup
//    (StringsFolderLookupOverlay.EnumerateTables and EnumerateStrings, C184, fixes.14): the same files and encoding its
//    lookups read (loose in Data\Strings, else the plugin's archives). GAPS Part 2 row 29. Held against Mutagen's lookup
//    of the same IDs in sfsrecord-tests (text-search).
// design\agent-first\notes\fix-f2-text-search.md.
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Binary.Translations;
using Mutagen.Bethesda.Plugins.Meta;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Strings;
using Mutagen.Bethesda.Strings.DI;
using SFSearch.Index;

namespace SFSearch.Records;

public static class RecordTexts
{
    /// <summary>One text field of a record type: its path (Name, Text, Responses[*].Text) and whether it is translated
    /// (a string ID in a localized plugin, the text itself otherwise).</summary>
    /// <param name="Within">Null for the record's own subrecords, else the list it sits in (Components, ObjectTemplates).</param>
    public sealed record Place(string Subrecord, string Field, bool Translated, string? Within)
    {
        /// <summary>How the subrecord is told to be text when another field of the type fills it too (GAPS S32):
        /// null for a subrecord only text fills.</summary>
        public SharedKind Shared { get; init; }
        /// <summary>For a shared subrecord: the schema paths of its text fields (without kinds of object), the fields a
        /// record read through Mutagen is asked for.</summary>
        public IReadOnlyList<string> TextPaths { get; init; } = Array.Empty<string>();
    }

    /// <summary>How a subrecord that text and other values both fill is told apart (GAPS S32).</summary>
    public enum SharedKind
    {
        /// <summary>Only text fills it.</summary>
        No,
        /// <summary>A game setting's DATA: text when the EditorID starts with a lower-case s (Mutagen's own rule,
        /// GameSettingUtility.GetGameSettingType), told from the bytes.</summary>
        GameSettingString,
        /// <summary>Told by reading the record through Mutagen: the subrecord is text when one of its text fields has
        /// this text (or, in a localized plugin, this string ID). Only records whose bytes could hold a match are read.</summary>
        Typed,
    }

    /// <summary>
    /// A list whose items reuse the record's own subrecords, told by the subrecords that open and close it: a Starfield
    /// record's components (BFCB ... BFCE; a weapon's FULL is its name, a component's FULL that component's) and its
    /// object templates (OBTE ... STOP; a template's FULL is the template's name).
    /// </summary>
    public sealed record Nested(string Path, string Opens, string Closes);

    public static readonly IReadOnlyList<Nested> NestedLists = new Nested[] { new("Components", "BFCB", "BFCE"), new("ObjectTemplates", "OBTE", "STOP") };

    /// <summary>The text subrecords of one record type, by their four letters read as a number: the record's own
    /// (Maps[0]) and those inside each of <see cref="NestedLists"/> (Maps[1 + its place]).</summary>
    public sealed class TextPlaces
    {
        public required IReadOnlyList<IReadOnlyDictionary<uint, Place>> Maps { get; init; }
        public IReadOnlyDictionary<uint, Place> Top => Maps[0];
        public IEnumerable<Place> All => Maps.SelectMany(m => m.Values);
        public int Count => Maps.Sum(m => m.Count);
        /// <summary>Text subrecords left out because another field of the type fills the same subrecord, and why
        /// (the text fields and the other fields).</summary>
        public required IReadOnlyList<string> Ambiguous { get; init; }
        /// <summary>Shared subrecords that are read (<see cref="Place.Shared"/>), each with its text and other fields.</summary>
        public IReadOnlyList<string> SharedRead { get; init; } = Array.Empty<string>();

        public TextPlaces Where(Func<Place, bool> which) => new()
        {
            Maps = Maps.Select(m => (IReadOnlyDictionary<uint, Place>)m.Where(kv => which(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value)).ToList(),
            Ambiguous = Ambiguous,
            SharedRead = SharedRead,
        };
    }

    /// <summary>
    /// Shared subrecords left out on purpose (GAPS S32, notes\fix-s32-shared-text.md): VMAD, a script's properties.
    /// Its strings are values a script reads, never a translated text, so find --text (translated text only) wouldn't
    /// search them anyway; and VMAD is binary (FormIDs, floats, object handles), so no byte test can narrow which
    /// records hold text outside ASCII for check2: every scripted record would be read through Mutagen.
    /// </summary>
    static bool IsUnshownText(string sub) => sub == "VMAD";

    static readonly ConcurrentDictionary<string, TextPlaces> Places = new(StringComparer.Ordinal);

    static uint Code(string sig) => BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(sig));

    static readonly uint[] Opens = NestedLists.Select(n => Code(n.Opens)).ToArray(), Closes = NestedLists.Select(n => Code(n.Closes)).ToArray();

    /// <summary>The map a subrecord is looked up in, given the one in use before it: a list's opening subrecord moves
    /// into that list, its closing one back to the record's own (the opening and closing subrecords hold no text).</summary>
    public static int Context(uint sig, int context, out bool marker)
    {
        marker = true;
        for (int i = 0; i < Opens.Length; i++)
        {
            if (sig == Opens[i]) return 1 + i;
            if (sig == Closes[i] && context == 1 + i) return 0;
        }
        marker = false;
        return context;
    }

    /// <summary>The subrecords of a record type that hold text: the schema's String fields that fill a whole subrecord.</summary>
    public static TextPlaces PlacesOf(RecordSchema schema, string signature) => Places.GetOrAdd(signature, sig =>
    {
        // Per context (the record's own subrecords, a component's): the text fields of each subrecord, and the
        // subrecords another kind of value fills too
        int contexts = 1 + NestedLists.Count;
        var text = Enumerable.Range(0, contexts).Select(_ => new Dictionary<string, (List<string> Fields, bool Translated)>(StringComparer.Ordinal)).ToArray();
        var other = Enumerable.Range(0, contexts).Select(_ => new Dictionary<string, List<string>>(StringComparer.Ordinal)).ToArray();
        foreach (var type in schema.TypesOf(sig))
            foreach (var row in schema.Model.Expand(type))
            {
                if (row.Subrecord is not { Length: 4 } sub) continue;
                // A field of a record this one holds (a cell's navmeshes, a topic's responses, a quest's scenes) is in
                // that record's own bytes, never in this one's
                if (InChildRecord(schema, row)) continue;
                int c = 0;
                for (int n = 0; n < NestedLists.Count; n++) if (row.PlainPath.StartsWith(NestedLists[n].Path + "[", StringComparison.Ordinal)) c = 1 + n;
                if (row.Def.Kind == "String" && row.Offset == null)
                {
                    if (!text[c].TryGetValue(sub, out var t)) text[c][sub] = t = (new List<string>(), false);
                    if (!t.Fields.Contains(row.PlainPath)) t.Fields.Add(row.PlainPath);
                    text[c][sub] = (t.Fields, t.Translated || row.Def.Translated);
                }
                // Another value that fills the same subrecord (not an object, which only holds fields, and not a
                // list, whose items are told by their own rows)
                else if (row.Def.Kind is not ("Object" or "List" or "Array" or "Array2D" or "Break" or "Marker" or "CustomLogic") && row.Offset is null or 0 && row.Def.Size != 0)
                {
                    if (!other[c].TryGetValue(sub, out var o)) other[c][sub] = o = new List<string>();
                    if (!o.Contains(row.PlainPath)) o.Add(row.PlainPath);
                }
            }
        var maps = Enumerable.Range(0, contexts).Select(_ => new Dictionary<uint, Place>()).ToArray();
        var ambiguous = new List<string>();
        var sharedRead = new List<string>();
        for (int c = 0; c < contexts; c++)
            foreach (var (sub, t) in text[c])
            {
                string? within = c == 0 ? null : NestedLists[c - 1].Path;
                // The shortest paths first: a record's Name before a list item's
                var fields = t.Fields.OrderBy(f => f.Count(ch => ch is '.' or '['), Comparer<int>.Default).ThenBy(f => f.Length).ToList();
                string field = fields.Count <= 2 ? string.Join(" or ", fields) : $"{fields[0]} or {fields.Count - 1} more";
                if (other[c].TryGetValue(sub, out var others))
                {
                    string why = $"{sub}{(within == null ? "" : $" (in {within})")}: text {string.Join(", ", fields.Take(4))}{(fields.Count > 4 ? ", …" : "")}; also {string.Join(", ", others.Take(3))}{(others.Count > 3 ? $" and {others.Count - 3} more" : "")}";
                    var kind = sig == "GMST" && sub == "DATA" && within == null ? SharedKind.GameSettingString
                        : IsUnshownText(sub) ? SharedKind.No : SharedKind.Typed;
                    if (kind == SharedKind.No) { ambiguous.Add(why); continue; }
                    sharedRead.Add(why);
                    maps[c][Code(sub)] = new Place(sub, field, t.Translated, within) { Shared = kind, TextPaths = fields };
                    continue;
                }
                maps[c][Code(sub)] = new Place(sub, field, t.Translated, within);
            }
        ambiguous.Sort(StringComparer.Ordinal);
        sharedRead.Sort(StringComparer.Ordinal);
        return new TextPlaces { Maps = maps, Ambiguous = ambiguous, SharedRead = sharedRead };
    });

    /// <summary>True for a row inside a record the record type holds (its Parent chain passes through a field whose
    /// object is a record type, or a list of placed objects).</summary>
    static bool InChildRecord(RecordSchema schema, SFSearch.SchemaGen.FieldRow row)
    {
        for (var r = row.Parent; r != null; r = r.Parent)
        {
            if (r.Def.ClrType != null && r.Def.ClrType.Contains("IPlacedGetter", StringComparison.Ordinal)) return true;
            if (r.Def.RefObject != null && schema.Model.Objects.TryGetValue(r.Def.RefObject, out var o) && o.ObjType == "Record") return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ The walk over a plugin's records
    /// <summary>Says whether a text subrecord is kept: its place and its bytes (a string ID in a localized plugin's
    /// translated field). Called on several threads at once.</summary>
    public delegate bool Keep(Place place, ReadOnlySpan<byte> data);

    /// <summary>A text subrecord kept by the walk: the record's entry, where it sits, its bytes.</summary>
    public readonly record struct Found(int Entry, Place Place, byte[] Data);

    public sealed class Walked
    {
        /// <summary>In entry order, then in the record's own order.</summary>
        public List<Found> Found { get; } = new();
        public long Records, Bytes, Texts;
        /// <summary>Compressed records that couldn't be inflated: their texts weren't read.</summary>
        public List<int> NotInflated { get; } = new();
        public double Ms;
    }

    /// <summary>
    /// Reads every text subrecord of the plugin's records of the types asked for (by their place in the segment's
    /// signature table) and keeps the ones <paramref name="keep"/> wants. <paramref name="which"/> picks the kinds of
    /// text field read at all (translated ones only, say). Compressed records are inflated whole.
    /// </summary>
    public static unsafe Walked Walk(IndexedPlugin plugin, bool[] signatures, RecordSchema schema, Func<Place, bool> which, Keep keep)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = new Walked();
        var seg = plugin.Segment;
        if (seg == null || seg.Count == 0) return result;
        // The text subrecords of each type of the segment; null when the type has none (its records aren't read)
        var maps = seg.Signatures.Select((s, i) =>
        {
            if (i >= signatures.Length || !signatures[i]) return null;
            var m = PlacesOf(schema, s.Signature).Where(which);
            return m.Count == 0 ? null : m;
        }).ToArray();
        if (maps.All(m => m == null)) { result.Ms = clock.Elapsed.TotalMilliseconds; return result; }
        using var fs = new FileStream(plugin.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = fs.Length;
        if (length == 0) return result;
        using var mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
        using var view = mmf.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
        byte* p = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        try
        {
            p += view.PointerOffset;
            byte* file = p;
            int count = seg.FirstUnresolved;
            const int Piece = 1 << 14;
            int pieces = (count + Piece - 1) / Piece;
            var found = new List<Found>[pieces];
            var bad = new List<int>[pieces];
            long records = 0, bytes = 0, texts = 0;
            void Work(int n)
            {
                byte[] plain = ArrayPool<byte>.Shared.Rent(1 << 16);
                var f = new List<Found>(); var failed = new List<int>();
                long recs = 0, size = 0, seen = 0;
                try
                {
                    int to = Math.Min(count, (n + 1) * Piece);
                    for (int i = n * Piece; i < to; i++)
                    {
                        var e = seg[i];
                        var map = maps[e.Signature];
                        if (map == null) continue;
                        recs++; size += e.Size;
                        if (e.Size < 6 || e.Offset + 24 + e.Size > length) continue;
                        byte* data = file + e.Offset + 24;
                        ReadOnlySpan<byte> span;
                        if (e.IsCompressed)
                        {
                            uint plainSize = *(uint*)data;
                            if (plainSize > int.MaxValue - 64) { failed.Add(i); continue; }
                            if (plain.Length < plainSize) { ArrayPool<byte>.Shared.Return(plain); plain = ArrayPool<byte>.Shared.Rent((int)plainSize); }
                            try
                            {
                                using var packed = new UnmanagedMemoryStream(data + 4, e.Size - 4);
                                using var zip = new ZLibStream(packed, CompressionMode.Decompress);
                                zip.ReadExactly(plain, 0, (int)plainSize);
                            }
                            catch (Exception x) when (x is InvalidDataException or EndOfStreamException or ArgumentException) { failed.Add(i); continue; }
                            span = plain.AsSpan(0, (int)plainSize);
                        }
                        else span = new ReadOnlySpan<byte>(data, (int)e.Size);
                        seen += Subrecords(span, map, keep, i, f);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(plain); }
                found[n] = f; bad[n] = failed;
                Interlocked.Add(ref records, recs); Interlocked.Add(ref bytes, size); Interlocked.Add(ref texts, seen);
            }
            if (pieces <= 1) for (int n = 0; n < pieces; n++) Work(n);
            else Parallel.For(0, pieces, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) }, Work);
            foreach (var l in found) result.Found.AddRange(l);
            foreach (var l in bad) result.NotInflated.AddRange(l);
            result.Records = records; result.Bytes = bytes; result.Texts = texts;
        }
        finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
        result.Ms = clock.Elapsed.TotalMilliseconds;
        return result;
    }

    /// <summary>Every subrecord of one record's data (the long form through XXXX read too): the text ones are offered
    /// to <paramref name="keep"/>. Returns how many text subrecords there were.</summary>
    static int Subrecords(ReadOnlySpan<byte> data, TextPlaces map, Keep keep, int entry, List<Found> found)
    {
        int pos = 0, seen = 0, context = 0; uint longLength = 0; bool longForm = false;
        // The EditorID's first byte: a game setting's DATA is text when it is a lower-case s (Mutagen's rule)
        int editorIdFirst = -1;
        while (pos + 6 <= data.Length)
        {
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
            long len = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 4)..]);
            if (sig == Xxxx && len == 4)
            {
                if (pos + 10 > data.Length) break;
                longLength = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 6)..]);
                longForm = true; pos += 10;
                continue;
            }
            if (longForm) { len = longLength; longForm = false; }
            if (pos + 6 + len > data.Length) break;
            context = Context(sig, context, out bool marker);
            if (sig == Edid && editorIdFirst < 0) editorIdFirst = len > 0 ? data[pos + 6] : 0;
            if (!marker && map.Maps[context].TryGetValue(sig, out var place)
                && (place.Shared != SharedKind.GameSettingString || editorIdFirst == 's'))
            {
                // A subrecord read through Mutagen later (Confirm) isn't counted until it is
                if (place.Shared != SharedKind.Typed) seen++;
                var bytes = data.Slice(pos + 6, (int)len);
                if (keep(place, bytes)) found.Add(new Found(entry, place, bytes.ToArray()));
            }
            pos += 6 + (int)len;
        }
        return seen;
    }

    static readonly uint Xxxx = Code("XXXX"), Edid = Code("EDID");

    // ------------------------------------------------------------------ Shared subrecords, told by Mutagen (GAPS S32)
    /// <summary>Reads one record of the plugin (by its entry) through Mutagen; throws when it can't.</summary>
    public delegate object ReadRecord(int entry);

    /// <summary>One text field of a record as Mutagen reads it: its path with each list item's place
    /// (Ranks[0].Effects[2].Text), its text, whether Mutagen holds it as a translated text, and its string ID (a
    /// localized plugin's translated text) with the strings table Mutagen read it from (.strings, .dlstrings, .ilstrings:
    /// <c>TranslatedString.StringsKeySource</c>, set by <c>TranslatedString.FromStringsKey</c>, C187).</summary>
    public sealed record TypedText(string Path, string Text, bool Translated, uint? StringId, StringsSource? Table = null);

    /// <summary>A shared subrecord found to be text: the walk's find, the field Mutagen filled from it, and the text's
    /// bytes in the subrecord (stored text; null for a string ID).</summary>
    public sealed record Confirmed(Found Found, TypedText Text, byte[]? Raw);

    public sealed class Confirmation
    {
        public List<Confirmed> Texts { get; } = new();
        /// <summary>Records read through Mutagen, and those it couldn't read (their shared subrecords were left out).</summary>
        public int Read, NotRead;
        public List<int> NotReadEntries { get; } = new();
        public double Ms;
    }

    /// <summary>
    /// Tells which of the walk's shared subrecords (<see cref="SharedKind.Typed"/>) are text: each record is read once
    /// through Mutagen, and a subrecord is a text field when one of its place's text fields has this text (stored: the
    /// field's text is in the subrecord's bytes, as Mutagen's encoding reads them) or this string ID (in the subrecord's
    /// bytes). <paramref name="take"/> says which fields count (translated ones, ones that fit a pattern).
    /// Records are read one at a time, in entry order. Each field is matched to one subrecord at most.
    /// </summary>
    public static Confirmation Confirm(IEnumerable<Found> found, ReadRecord read, Language language, Func<TypedText, bool> take)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var r = new Confirmation();
        foreach (var group in found.Where(f => f.Place.Shared == SharedKind.Typed).GroupBy(f => f.Entry).OrderBy(g => g.Key))
        {
            object record;
            try { record = read(group.Key); }
            catch (Exception x) when (x is not (OutOfMemoryException or StackOverflowException)) { r.NotRead++; r.NotReadEntries.Add(group.Key); continue; }
            r.Read++;
            var paths = group.SelectMany(f => f.Place.TextPaths).Distinct(StringComparer.Ordinal).ToList();
            var texts = TextsAt(record, paths).Where(take).ToList();
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in group)
                foreach (var t in texts)
                {
                    if (used.Contains(t.Path) || !f.Place.TextPaths.Contains(Plain(t.Path), StringComparer.Ordinal)) continue;
                    byte[]? raw = null;
                    if (t.StringId is uint id)
                    {
                        if (!HoldsId(f.Data, id)) continue;
                    }
                    else if ((raw = Locate(f.Data, t.Text, t.Translated, language)) == null) continue;
                    used.Add(t.Path);
                    r.Texts.Add(new Confirmed(f, t, raw));
                    break;
                }
        }
        r.Ms = clock.Elapsed.TotalMilliseconds;
        return r;
    }

    /// <summary>A path with each list item's place taken out: Ranks[0].Effects[2].Text is Ranks[*].Effects[*].Text.</summary>
    static string Plain(string path) => Regex.Replace(path, @"\[\d+\]", "[*]");

    /// <summary>True when the 4 bytes of a string ID stand anywhere in a subrecord.</summary>
    public static bool HoldsId(ReadOnlySpan<byte> data, uint id)
    {
        for (int i = 0; i + 4 <= data.Length; i++) if (BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) == id) return true;
        return false;
    }

    /// <summary>
    /// The pieces of a subrecord that may be one text: each run of bytes between zeros, from its start or from up to 4
    /// bytes in (past a length written before the text), whole or cut to the text's length. A text field fills one of
    /// them: a whole-subrecord text, a length-prefixed one (a planet's details), several texts in a row.
    /// </summary>
    static IEnumerable<(int Start, int Length)> Pieces(ReadOnlySpan<byte> data, int textLength)
    {
        var list = new List<(int, int)>();
        int start = 0;
        for (int i = 0; i <= data.Length; i++)
        {
            if (i < data.Length && data[i] != 0) continue;
            for (int skip = 0; skip <= 4 && start + skip < i; skip++)
            {
                list.Add((start + skip, i - start - skip));
                if (textLength > 0 && textLength < i - start - skip) list.Add((start + skip, textLength));
            }
            start = i + 1;
        }
        return list;
    }

    /// <summary>The bytes of a stored text in a subrecord: a piece that reads as this text with Mutagen's encoding.</summary>
    static byte[]? Locate(ReadOnlySpan<byte> data, string text, bool translated, Language language)
    {
        if (text.Length == 0) return null;
        var encoding = RecordNames.EncodingOf(translated, language);
        foreach (var (start, length) in Pieces(data, text.Length))
        {
            var piece = data.Slice(start, length);
            if (encoding.GetString(piece) == text) return piece.ToArray();
        }
        return null;
    }

    /// <summary>
    /// The text fields of a record read through Mutagen at the schema's paths (Ranks[*].Effects[*].Text): each list
    /// item followed, each property found by its name on the item's own type (an item of a list of several kinds
    /// without the field is passed over). A field Mutagen holds as a translated text gives its text in the reading's
    /// language and its string ID; a plain string gives its text.
    /// </summary>
    public static List<TypedText> TextsAt(object record, IEnumerable<string> plainPaths)
    {
        var list = new List<TypedText>();
        foreach (var path in plainPaths)
        {
            var segs = path.Split('.');
            void Walk(object? o, int i, string at)
            {
                if (o == null) return;
                if (i == segs.Length)
                {
                    switch (o)
                    {
                        case ITranslatedStringGetter ts:
                            list.Add(new TypedText(at, ts.String ?? "", true, (ts as IOptionalStringsKeyGetter)?.StringsKey,
                                ts is TranslatedString { StringsKey: not null } keyed ? keyed.StringsKeySource : null)); break;
                        case string s:
                            list.Add(new TypedText(at, s, false, null)); break;
                    }
                    return;
                }
                string seg = segs[i];
                bool items = seg.EndsWith("[*]", StringComparison.Ordinal);
                string name = items ? seg[..^3] : seg;
                var prop = PropertyOf(o.GetType(), name);
                if (prop == null) return;
                object? v;
                try { v = prop.GetValue(o); }
                catch (System.Reflection.TargetInvocationException) { return; }
                string here = at.Length == 0 ? name : at + "." + name;
                if (!items) { Walk(v, i + 1, here); return; }
                if (v is not System.Collections.IEnumerable e || v is string) return;
                int n = 0;
                foreach (var item in e) Walk(item, i + 1, $"{here}[{n++}]");
            }
            Walk(record, 0, "");
        }
        return list;
    }

    // ------------------------------------------------------------------ One record through Mutagen, cheaply
    /// <summary>
    /// A reader of single records of a plugin through Mutagen's own reader of one record (<c>MajorRecordReader</c>,
    /// C176, as PluginSession.ReadDirect reads), without opening the plugin as an overlay or its strings files: the
    /// masters come from the header index, and in a localized plugin a translated field holds its string ID (its text
    /// isn't looked up). Opening a session costs over 1 s the first time (Mutagen looks through the archives for
    /// strings); this costs the record's own bytes. Each call reads the record anew from the file.
    /// </summary>
    public static RecordReader Reader(IndexedPlugin plugin, Language language, string? dataFolder) => new(plugin, language, dataFolder);

    /// <summary>Reads records of one plugin, each by its entry in the header index. Holds a handle on the file (shared
    /// for reading, writing and deleting) from the first read until disposed.</summary>
    public sealed class RecordReader : IDisposable
    {
        readonly IndexedPlugin _plugin;
        readonly Lazy<MajorRecordReader> _reader;

        internal RecordReader(IndexedPlugin plugin, Language language, string? dataFolder)
        {
            _plugin = plugin;
            _reader = new Lazy<MajorRecordReader>(() => Open(plugin, language, dataFolder), LazyThreadSafetyMode.ExecutionAndPublication);
        }

        static MajorRecordReader Open(IndexedPlugin plugin, Language language, string? dataFolder)
        {
            var header = plugin.Header ?? throw new RecordException($"{plugin.Name} has no header index.");
            var styles = new List<KeyedMasterStyle>();
            var seen = new HashSet<ModKey>();
            var self = ModKey.FromFileName(plugin.Name);
            for (int i = 0; i < header.Masters.Count; i++)
            {
                var key = ModKey.FromFileName(header.Masters[i]);
                if (seen.Add(key)) styles.Add(new KeyedMasterStyle(key, PluginSession.Convert(plugin.MasterStyles[i])));
            }
            if (seen.Add(self)) styles.Add(new KeyedMasterStyle(self, PluginSession.Convert(header.Style)));
            // A localized plugin's translated fields keep their string IDs, with no text: Mutagen's StringsKeysOnlyLookup
            // (C197) opens no strings file, and gives each text its ID, its table and the plugin. The read parameters'
            // strings part still gives the language and the encodings (GAPS Part 2 row 34).
            var read = new BinaryReadParameters
            {
                MasterFlagsLookup = new LoadOrder<IModMasterStyledGetter>(styles),
                ThrowOnUnknownSubrecord = false,
                Parallel = false,
                FileShare = PluginSession.Sharing,
                KeepCompressedRecords = true,
                StringsParam = StringFiles.ReadParameters(plugin.Path, plugin.Name, dataFolder, language),
            };
            return MajorRecordReader.FromPath(new ModPath(self, plugin.Path), GameRelease.Starfield, read, new StringsKeysOnlyLookup(self));
        }

        /// <summary>The record at an entry of the header index, read from the file as it is now.</summary>
        public object Read(int entry)
        {
            var seg = _plugin.Segment ?? throw new RecordException($"{_plugin.Name} has no header index.");
            var e = seg[entry];
            var reader = _reader.Value;
            var bytes = new byte[24 + e.Size];
            using (var file = File.OpenHandle(_plugin.Path, FileMode.Open, FileAccess.Read, PluginSession.Sharing))
                if (RandomAccess.Read(file, bytes, e.Offset) != bytes.Length) throw new RecordException($"{_plugin.Name} ends inside the record at 0x{e.Offset:X}.");
            string sig = seg.SignatureOf(e);
            if (MajorRecordReader.GetRecordClass(GameCategory.Starfield, new RecordType(sig)) == null)
                throw new RecordException($"Mutagen reads no {sig} record on its own.");
            try { return reader.Read(bytes, e.Offset); }
            catch (Exception x) when (x is not RecordException)
            {
                throw new RecordException($"Mutagen couldn't read the {sig} at 0x{e.Offset:X} of {_plugin.Name}: {x.GetType().Name}: {x.Message}");
            }
        }

        public void Dispose()
        {
            if (_reader.IsValueCreated) _reader.Value.Dispose();
        }
    }

    static readonly ConcurrentDictionary<(Type, string), System.Reflection.PropertyInfo?> Props = new();

    /// <summary>A public property of an object by name: its class's, else one of its interfaces' (Mutagen's getters).</summary>
    static System.Reflection.PropertyInfo? PropertyOf(Type type, string name) => Props.GetOrAdd((type, name), k =>
    {
        try
        {
            if (k.Item1.GetProperty(k.Item2, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance) is { } p && p.GetIndexParameters().Length == 0) return p;
        }
        catch (System.Reflection.AmbiguousMatchException) { }
        foreach (var i in k.Item1.GetInterfaces())
            if (i.GetProperty(k.Item2) is { } q && q.GetIndexParameters().Length == 0) return q;
        return null;
    });

    // ------------------------------------------------------------------ Reading a stored text
    /// <summary>A stored text as Mutagen reads it for the language asked: up to the closing zero, in the encoding
    /// Mutagen's own reading state picks (RecordNames.EncodingOf).</summary>
    public static string Decode(ReadOnlySpan<byte> data, bool translated, Language language) =>
        BinaryStringUtility.ProcessWholeToZString(data, RecordNames.EncodingOf(translated, language));

    /// <summary>A stored text's bytes up to the closing zero.</summary>
    public static ReadOnlySpan<byte> Bytes(ReadOnlySpan<byte> data)
    {
        int zero = data.IndexOf((byte)0);
        return zero < 0 ? data : data[..zero];
    }

    public static bool IsLocalized(IndexedPlugin plugin) => ((plugin.Header?.Flags ?? 0) & 0x80) != 0;

    // ------------------------------------------------------------------ Strings files
    /// <summary>One strings file of a plugin in one language, read by Mutagen.</summary>
    public sealed class StringsFile
    {
        public required StringsSource Source { get; init; }
        /// <summary>Where it was read from: a loose file's path, or an archive's path and the file's path inside it.</summary>
        public required string From { get; init; }
        /// <summary>The file's name as the game names it: starfield_en.dlstrings.</summary>
        public required string Name { get; init; }
        public required IReadOnlyList<KeyValuePair<uint, string>> Strings { get; init; }
    }

    static readonly ConcurrentDictionary<(string Path, QuickStamp Stamp, Language Language, string Data), Lazy<IReadOnlyList<StringsFile>>> Files = new();

    /// <summary>
    /// The strings files of a plugin in one language, every string read: the files Mutagen's StringsFolderLookupOverlay
    /// would read (Data\Strings beside the plugin first, then the plugin's archives in <paramref name="dataFolder"/>),
    /// each through Mutagen's StringsLookupOverlay. Kept for the life of the program per file copy and language.
    /// Empty for a plugin that isn't localized or has no such files.
    /// </summary>
    public static IReadOnlyList<StringsFile> StringsOf(IndexedPlugin plugin, Language language, string dataFolder) =>
        Files.GetOrAdd((plugin.Path.ToLowerInvariant(), plugin.Stamp, language, dataFolder.ToLowerInvariant()), _ => new Lazy<IReadOnlyList<StringsFile>>(() => ReadStrings(plugin, language, dataFolder))).Value;

    static IReadOnlyList<StringsFile> ReadStrings(IndexedPlugin plugin, Language language, string dataFolder)
    {
        var list = new List<StringsFile>();
        if (!IsLocalized(plugin)) return list;
        // Mutagen's own strings folder lookup, made as every reading of the plugin makes it (loose files in the Strings
        // folder first, then the plugin's archives in Data, in Mutagen's order), lists the plugin's tables and every
        // string of each (C184, fixes.14: EnumerateTables, EnumerateStrings). GAPS Part 2 row 29.
        StringsFolderLookupOverlay lookup;
        try
        {
            lookup = StringsFolderLookupOverlay.TypicalFactory(GameRelease.Starfield, ModKey.FromFileName(plugin.Name), Path.GetDirectoryName(plugin.Path)!,
                StringFiles.ReadParameters(plugin.Path, plugin.Name, dataFolder, language));
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { return list; }
        foreach (var table in lookup.EnumerateTables())
        {
            if (table.Language != language) continue;
            try
            {
                var strings = lookup.EnumerateStrings(table.Source, language).ToList();
                string name = Path.GetFileName((table.ArchiveEntryPath ?? table.SourcePath).Replace('/', '\\'));
                list.Add(new StringsFile { Source = table.Source, From = table.SourcePath, Name = name, Strings = strings });
            }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { }
        }
        return list.OrderBy(f => f.Source).ToList();
    }

    // ------------------------------------------------------------------ The search
    /// <summary>What the search reads: text stored in plugins, text in strings files, or both.</summary>
    [Flags] public enum From { Stored = 1, Strings = 2, Both = 3 }

    /// <summary>One text that fits the pattern.</summary>
    /// <param name="Entry">The record's entry in the plugin's segment; -1 for a string no record of the searched types names.</param>
    /// <param name="File">Null for a text stored in the plugin; else the strings file it is in.</param>
    public sealed record Hit(int Entry, string Field, string Subrecord, string Text, string? File, uint StringId);

    public sealed class PluginHits
    {
        public List<Hit> Hits { get; } = new();
        public bool Localized;
        /// <summary>Text fields read from the records (stored text, or string IDs of a localized plugin).</summary>
        public long Texts;
        /// <summary>Strings read from the strings files, and the files.</summary>
        public long Strings;
        public List<string> FilesRead { get; } = new();
        /// <summary>A localized plugin with no strings files in the language asked.</summary>
        public bool NoStrings;
        /// <summary>Strings that fit the pattern but no record of the searched types names (world data, or unused).</summary>
        public int StringsNotNamed;
        /// <summary>String IDs found in more than one of the plugin's strings files: each file's text is listed.</summary>
        public int SharedIds;
        public int NotInflated;
        /// <summary>Records read through Mutagen to tell a shared subrecord (a perk's entry point data) is text, and
        /// those Mutagen couldn't read; shared subrecords left out because no reader was given.</summary>
        public int TypedRead, TypedNotRead, TypedSkipped;
        public double TypedMs;
        public double Ms;
    }

    /// <summary>
    /// The translated text fields (names, descriptions, book text, dialogue, ...) of the plugin's records of the types
    /// asked for whose text fits <paramref name="pattern"/>: stored text read as Mutagen reads it, and a localized
    /// plugin's strings files in <paramref name="language"/>.
    /// </summary>
    /// <remarks>A subrecord that text and other values both fill (a perk's entry point data, GAPS S32) is told by
    /// reading its record through <paramref name="read"/>, only when its bytes could hold a match; without a reader such
    /// subrecords are left out (<see cref="PluginHits.TypedSkipped"/>).</remarks>
    public static PluginHits Search(IndexedPlugin plugin, bool[] signatures, RecordSchema schema, Regex pattern, Language language, string dataFolder, From from, ReadRecord? read = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var r = new PluginHits { Localized = IsLocalized(plugin) };
        if (!r.Localized)
        {
            if ((from & From.Stored) != 0)
            {
                // A translated text stored in a plugin always fills its whole subrecord (Mutagen reads it with
                // ProcessWholeToZString), so a shared subrecord is offered by the same test
                var walked = Walk(plugin, signatures, schema, p => p.Translated, (place, data) => pattern.IsMatch(Decode(data, true, language)));
                r.Texts = walked.Texts; r.NotInflated = walked.NotInflated.Count;
                foreach (var f in walked.Found)
                    if (f.Place.Shared != SharedKind.Typed) r.Hits.Add(new Hit(f.Entry, f.Place.Field, f.Place.Subrecord, Decode(f.Data, true, language), null, 0));
                var typed = walked.Found.Where(f => f.Place.Shared == SharedKind.Typed).ToList();
                if (typed.Count > 0 && read == null) r.TypedSkipped += typed.Count;
                else if (typed.Count > 0)
                {
                    var c = Confirm(typed, read!, language, t => t.Translated && pattern.IsMatch(t.Text));
                    r.TypedRead += c.Read; r.TypedNotRead += c.NotRead; r.TypedMs += c.Ms; r.Texts += c.Texts.Count;
                    foreach (var t in c.Texts) r.Hits.Add(new Hit(t.Found.Entry, t.Text.Path, t.Found.Place.Subrecord, t.Text.Text, null, 0));
                    InEntryOrder(r.Hits);
                }
            }
        }
        else if ((from & From.Strings) != 0)
        {
            var files = StringsOf(plugin, language, dataFolder);
            r.NoStrings = files.Count == 0;
            var hits = new Dictionary<uint, List<(string File, string Text, StringsSource Source)>>();
            foreach (var file in files)
            {
                r.FilesRead.Add(file.Name);
                r.Strings += file.Strings.Count;
                foreach (var (id, text) in file.Strings)
                    if (pattern.IsMatch(text)) (hits.TryGetValue(id, out var l) ? l : hits[id] = new()).Add((file.Name, text, file.Source));
            }
            if (hits.Count > 0)
            {
                var named = new HashSet<uint>();
                // A translated text of a localized plugin is a string ID that fills its whole subrecord (Mutagen
                // refuses any other length), shared subrecord or not
                var walked = Walk(plugin, signatures, schema, p => p.Translated, (place, data) => data.Length == 4 && hits.ContainsKey(BinaryPrimitives.ReadUInt32LittleEndian(data)));
                r.Texts = walked.Texts; r.NotInflated = walked.NotInflated.Count;
                foreach (var f in walked.Found)
                {
                    if (f.Place.Shared == SharedKind.Typed) continue;
                    uint id = BinaryPrimitives.ReadUInt32LittleEndian(f.Data);
                    named.Add(id);
                    foreach (var (file, text, _) in hits[id]) r.Hits.Add(new Hit(f.Entry, f.Place.Field, f.Place.Subrecord, text, file, id));
                }
                var typed = walked.Found.Where(f => f.Place.Shared == SharedKind.Typed).ToList();
                if (typed.Count > 0 && read == null) r.TypedSkipped += typed.Count;
                else if (typed.Count > 0)
                {
                    var c = Confirm(typed, read!, language, t => t.Translated && t.StringId is uint k && hits.ContainsKey(k));
                    r.TypedRead += c.Read; r.TypedNotRead += c.NotRead; r.TypedMs += c.Ms; r.Texts += c.Texts.Count;
                    foreach (var t in c.Texts)
                    {
                        uint id = t.Text.StringId!.Value;
                        named.Add(id);
                        // The table Mutagen read the field from (C187); every file holding the ID when none of them is it
                        var mine = hits[id].Where(h => t.Text.Table == null || h.Source == t.Text.Table).ToList();
                        foreach (var (file, text, _) in mine.Count > 0 ? mine : hits[id]) r.Hits.Add(new Hit(t.Found.Entry, t.Text.Path, t.Found.Place.Subrecord, text, file, id));
                    }
                    InEntryOrder(r.Hits);
                }
                r.SharedIds = hits.Values.Count(l => l.Count > 1);
                r.StringsNotNamed = hits.Keys.Count(k => !named.Contains(k));
            }
        }
        r.Ms = clock.Elapsed.TotalMilliseconds;
        return r;
    }

    static void InEntryOrder(List<Hit> hits)
    {
        var sorted = hits.OrderBy(h => h.Entry).ToList();
        hits.Clear();
        hits.AddRange(sorted);
    }

    // ------------------------------------------------------------------ How a stored text reads
    /// <summary>The languages Starfield ships (SQL's TextLanguages.Shipped), as Mutagen names them.</summary>
    public static readonly IReadOnlyList<Language> Shipped = new[]
    {
        Language.English, Language.German, Language.Spanish, Language.French, Language.Italian, Language.Japanese,
        Language.Polish, Language.Portuguese_Brazil, Language.ChineseSimplified,
    };
    static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <summary>How the bytes of one stored text read: as Mutagen reads them in each language, as UTF-8, and whether
    /// each byte has a character in code page 1252. Null for a text of plain ASCII, which reads one way everywhere.</summary>
    public sealed class Readings
    {
        /// <summary>Mutagen's reading in English (the one record, SQL and find give).</summary>
        public required string Mutagen { get; init; }
        /// <summary>The languages whose reading differs from English's, with that reading.</summary>
        public required IReadOnlyList<(Language Language, string Text)> OtherLanguages { get; init; }
        /// <summary>The bytes read as UTF-8, or null when they aren't valid UTF-8.</summary>
        public string? Utf8 { get; init; }
        /// <summary>The first byte that isn't valid UTF-8, and where it is; -1 when the bytes are valid UTF-8.</summary>
        public int Utf8BadAt { get; init; } = -1;
        /// <summary>Bytes with no character in code page 1252 (0x81, 0x8D, 0x8F, 0x90, 0x9D), or empty.</summary>
        public required IReadOnlyList<byte> Not1252 { get; init; }
        /// <summary>The bytes over 0x7F, each with where it is.</summary>
        public required IReadOnlyList<(int At, byte Byte)> High { get; init; }

        /// <summary>The bytes read as UTF-8 with a replacement character for each byte that isn't valid.</summary>
        public required string Utf8Lossy { get; init; }
        /// <summary>The text's bytes up to the closing zero.</summary>
        public required byte[] Raw { get; init; }
    }

    /// <summary>The ways a stored text's bytes read, or null when they are plain ASCII.</summary>
    public static Readings? ReadingsOf(ReadOnlySpan<byte> data, bool translated, IReadOnlyList<Language> languages)
    {
        var b = Bytes(data);
        var high = new List<(int, byte)>();
        for (int i = 0; i < b.Length; i++) if (b[i] > 0x7F) high.Add((i, b[i]));
        if (high.Count == 0) return null;
        string english = Decode(data, translated, Language.English);
        var others = new List<(Language, string)>();
        foreach (var l in languages)
        {
            if (l == Language.English) continue;
            string s = Decode(data, translated, l);
            if (s != english) others.Add((l, s));
        }
        string? utf8 = null; int badAt = -1;
        try { utf8 = StrictUtf8.GetString(b); }
        catch (DecoderFallbackException x) { badAt = x.Index; }
        var not1252 = new List<byte>();
        foreach (var (_, v) in high) if (v is 0x81 or 0x8D or 0x8F or 0x90 or 0x9D && !not1252.Contains(v)) not1252.Add(v);
        return new Readings { Mutagen = english, OtherLanguages = others, Utf8 = utf8, Utf8BadAt = badAt, Not1252 = not1252, High = high, Utf8Lossy = Encoding.UTF8.GetString(b), Raw = b.ToArray() };
    }
}
