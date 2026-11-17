// In-game names read from each record's own bytes: its FULL subrecord, with the text of a localized plugin looked up
// in its string files. No link index, no Mutagen overlay, no JSON: the header index gives where each record sits.
//  - Of: the name of one record (find prints a page of rows this way).
//  - All: every name of one plugin, for a search by name before the plugin's link file exists (and, with or without
//    one, the names of cells and worldspaces, which the link index leaves out: GAPS S1). Measured on the whole load
//    order (370 plugins, 3.47 million records outside world data) in design\agent-first\notes\fix-tools.md, with
//    cells and worldspaces in notes\fix-f1-world-names.md.
//  - CellGrid: an exterior cell's grid (XCLC).
// The name is the subrecord of the type's top-level Name field in the schema (FULL for nearly every type; NNAM for
// gameplay options, MNAM for material and movement types, ...), and FULL for a type whose schema has no Name. Held
// against Mutagen's Name of whole plugins in sfsrecord-tests (names-from-bytes).
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Binary.Streams;
using Mutagen.Bethesda.Plugins.Binary.Translations;
using Mutagen.Bethesda.Strings;
using SFSearch.Index;

namespace SFSearch.Records;

public static class RecordNames
{
    static readonly byte[] Full = "FULL"u8.ToArray();

    /// <summary>Where a type keeps its name: the subrecord, and whether it is translated text (a string ID in a localized plugin).</summary>
    /// <param name="FromSchema">False when the type's schema has no top-level Name and FULL is read all the same (ship
    /// modules' GBFM): Mutagen's JSON, and so the link index, has no name for such a record.</param>
    public readonly record struct NamePlace(byte[] Subrecord, bool Translated, bool FromSchema);

    static readonly ConcurrentDictionary<string, NamePlace> Places = new(StringComparer.Ordinal);

    /// <summary>The subrecord a record type's name is read from: its schema's top-level Name, else FULL.</summary>
    public static NamePlace PlaceOf(RecordSchema? schema, string signature) => Places.GetOrAdd(signature, sig =>
    {
        // A signature can stand for several Mutagen types (OMOD: 8): they share where the name is, or FULL is read
        var defs = schema == null ? new List<SFSearch.SchemaGen.FieldDef>() : schema.TypesOf(sig).Select(t => schema.Prop(t, "Name")?.Main).Where(d => d is { Subrecord.Length: 4 }).Select(d => d!).ToList();
        if (defs.Count > 0 && defs.All(d => d.Subrecord == defs[0].Subrecord))
            return new NamePlace(Encoding.ASCII.GetBytes(defs[0].Subrecord!), defs[0].Translated, true);
        return new NamePlace(Full, true, defs.Count > 0);
    });

    static NamePlace[] PlacesOf(RecordSchema? schema, Segment seg) => seg.Signatures.Select(s => PlaceOf(schema, s.Signature)).ToArray();

    // The string files of a localized plugin, per file copy, copy of its string files and language, kept for the life of the program
    static readonly ConcurrentDictionary<(string Path, QuickStamp Stamp, Key128 Strings, Language Language), Lazy<IStringsFolderLookup?>> Strings = new();

    static IStringsFolderLookup? StringsOf(IndexedPlugin plugin, Language language, string? dataFolder) =>
        Strings.GetOrAdd((plugin.Path.ToLowerInvariant(), plugin.Stamp, plugin.Strings.Key, language), _ => new Lazy<IStringsFolderLookup?>(() =>
        {
            try
            {
                var param = StringFiles.ReadParameters(plugin.Path, plugin.Name, dataFolder, language);
                return StringsFolderLookupOverlay.TypicalFactory(GameRelease.Starfield, ModKey.FromFileName(plugin.Name), Path.GetDirectoryName(plugin.Path)!, param);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { return null; }
        })).Value;

    static bool Localized(IndexedPlugin plugin) => ((plugin.Header?.Flags ?? 0) & 0x80) != 0;

    /// <summary>The in-game name of one record of the plugin, or null when it has none (or its bytes can't be read).
    /// The Creation Kit's tag in front of a text is taken off.</summary>
    public static string? Of(IndexedPlugin plugin, int entry, RecordSchema? schema, Language language = Language.English, string? dataFolder = null)
    {
        var seg = plugin.Segment;
        if (seg == null || (uint)entry >= (uint)seg.Count) return null;
        string sig = seg.SignatureOf(seg[entry]);
        var place = PlaceOf(schema, sig);
        var found = SubrecordOf(plugin, entry, place.Subrecord, StopOf(sig, place.Subrecord));
        return found == null ? null : Text(plugin, found, place.Translated, language, dataFolder);
    }

    static readonly byte[] Xclc = "XCLC"u8.ToArray();

    /// <summary>An exterior cell's grid (its XCLC subrecord: X, then Y), read from the record's own bytes. Null for a
    /// record that has none: an interior cell, a worldspace's persistent cell, or another type.</summary>
    public static (int X, int Y)? CellGrid(IndexedPlugin plugin, int entry)
    {
        var found = SubrecordOf(plugin, entry, Xclc, null);
        if (found == null || found.Length < 8) return null;
        return (BinaryPrimitives.ReadInt32LittleEndian(found), BinaryPrimitives.ReadInt32LittleEndian(found.AsSpan(4)));
    }

    /// <summary>The bytes of the first subrecord of this signature in one record of the plugin, or null.</summary>
    static byte[]? SubrecordOf(IndexedPlugin plugin, int entry, byte[] signature, byte[]? stop)
    {
        var seg = plugin.Segment;
        if (seg == null || (uint)entry >= (uint)seg.Count) return null;
        var e = seg[entry];
        if (e.Size == 0) return null;
        byte[]? stored = null;
        try
        {
            using var file = File.OpenHandle(plugin.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (e.Offset + 24 + e.Size > RandomAccess.GetLength(file)) return null;
            stored = ArrayPool<byte>.Shared.Rent((int)e.Size);
            RawRecord.ReadExactly(file, stored.AsSpan(0, (int)e.Size), e.Offset + 24);
            return FindName(stored.AsSpan(0, (int)e.Size), e.IsCompressed, signature, stop);
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or RecordException) { return null; }
        finally { if (stored != null) ArrayPool<byte>.Shared.Return(stored); }
    }

    /// <summary>What <see cref="All"/> read, and what it cost.</summary>
    public sealed class PluginNames
    {
        /// <summary>Entry and name, sorted by entry. Records with no name aren't listed.</summary>
        public List<(int Entry, string Name)> Names { get; } = new();
        public long Records, Bytes;
        /// <summary>Compressed records that couldn't be inflated: their names are missing.</summary>
        public int NotInflated;
        public double Ms;
    }

    /// <summary>
    /// Every in-game name of the plugin's records of the types asked for (by their place in the segment's signature
    /// table; null: every type), read from the file's bytes on several threads.
    /// </summary>
    public static unsafe PluginNames All(IndexedPlugin plugin, bool[]? signatures, RecordSchema? schema, Language language = Language.English, string? dataFolder = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = new PluginNames();
        var seg = plugin.Segment;
        if (seg == null || seg.Count == 0) return result;
        using var fs = new FileStream(plugin.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = fs.Length;
        if (length == 0) return result;
        bool localized = Localized(plugin);
        var places = PlacesOf(schema, seg);
        var stops = seg.Signatures.Select((x, i) => StopOf(x.Signature, places[i].Subrecord)).ToArray();
        IMutagenEncoding translatedText = EncodingOf(true, language), otherText = EncodingOf(false, language);
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
            // A localized plugin's names are string IDs here, looked up after the read on one thread
            var texts = new List<(int Entry, string Name)>[pieces];
            var ids = new List<(int Entry, uint Id)>[pieces];
            long records = 0, bytes = 0; int bad = 0;
            void Work(int n)
            {
                byte[] plain = ArrayPool<byte>.Shared.Rent(1 << 16);
                var t = new List<(int, string)>(); var d = new List<(int, uint)>();
                long recs = 0, size = 0; int failed = 0;
                try
                {
                    int to = Math.Min(count, (n + 1) * Piece);
                    for (int i = n * Piece; i < to; i++)
                    {
                        var e = seg[i];
                        if (signatures != null && !signatures[e.Signature]) continue;
                        recs++; size += e.Size;
                        if (e.Size < 6 || e.Offset + 24 + e.Size > length) continue;
                        var stored = new ReadOnlySpan<byte>(file + e.Offset + 24, (int)e.Size);
                        var place = places[e.Signature];
                        ReadOnlySpan<byte> full;
                        var stop = stops[e.Signature];
                        if (!e.IsCompressed) full = Subrecord(stored, place.Subrecord, stop, out _, out _);
                        else if (!InflatedSubrecord(stored, place.Subrecord, stop, ref plain, out int nameAt, out int nameLength)) { failed++; continue; }
                        else full = nameAt < 0 ? default : plain.AsSpan(nameAt, nameLength);
                        if (full.Length == 0) continue;
                        if (localized && place.Translated && full.Length == 4) { uint id = BinaryPrimitives.ReadUInt32LittleEndian(full); if (id != 0) d.Add((i, id)); }
                        else if (Decode(full, place.Translated ? translatedText : otherText) is { Length: > 0 } s) t.Add((i, s));
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(plain); }
                texts[n] = t; ids[n] = d;
                Interlocked.Add(ref records, recs); Interlocked.Add(ref bytes, size); Interlocked.Add(ref bad, failed);
            }
            if (pieces <= 1) for (int n = 0; n < pieces; n++) Work(n);
            else Parallel.For(0, pieces, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) }, Work);
            var lookup = ids.Any(l => l.Count > 0) ? StringsOf(plugin, language, dataFolder) : null;
            for (int n = 0; n < pieces; n++)
            {
                // Entries of one piece are in order, and pieces are in order: the list comes out sorted by entry
                int a = 0, b = 0;
                var t = texts[n]; var d = ids[n];
                while (a < t.Count || b < d.Count)
                {
                    if (b >= d.Count || a < t.Count && t[a].Entry < d[b].Entry) { Add(t[a].Entry, t[a].Name); a++; }
                    else
                    {
                        if (lookup != null && Lookup(lookup, language, d[b].Id) is { } s) Add(d[b].Entry, s);
                        b++;
                    }
                }
            }
            void Add(int entry, string name) { if (CardBuilder.NoTag(name) is { Length: > 0 } clean) result.Names.Add((entry, clean)); }
            result.Records = records; result.Bytes = bytes; result.NotInflated = bad;
        }
        finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
        result.Ms = clock.Elapsed.TotalMilliseconds;
        return result;
    }

    // ------------------------------------------------------------------ The bytes
    /// <summary>The name subrecord's bytes of a record's stored data (inflated first when compressed), or null.</summary>
    static byte[]? FindName(ReadOnlySpan<byte> stored, bool compressed, byte[] signature, byte[]? stop)
    {
        if (!compressed) { var f = Subrecord(stored, signature, stop, out _, out _); return f.Length == 0 ? null : f.ToArray(); }
        byte[] plain = ArrayPool<byte>.Shared.Rent(FirstInflate);
        try
        {
            if (!InflatedSubrecord(stored, signature, stop, ref plain, out int at, out int length) || at < 0 || length == 0) return null;
            return plain.AsSpan(at, length).ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(plain); }
    }

    /// <summary>The bytes inflated first when a subrecord is looked for in a compressed record. A name sits near the
    /// start (a cell's FULL is its second subrecord, before an XTV2 of up to hundreds of KB), so most records are read
    /// no further; when the subrecords run past this, the whole record is inflated.</summary>
    const int FirstInflate = 1 << 12;

    /// <summary>
    /// Finds a subrecord in a compressed record, inflating only as much as it needs: the first 4 KB, then the whole
    /// record when the subrecords run past that. <paramref name="at"/> and <paramref name="length"/> place it in
    /// <paramref name="plain"/>; <paramref name="at"/> is -1 when the record has none. False when the bytes can't be inflated.
    /// </summary>
    static bool InflatedSubrecord(ReadOnlySpan<byte> stored, byte[] signature, byte[]? stop, ref byte[] plain, out int at, out int length)
    {
        at = -1; length = 0;
        if (stored.Length < 4) return false;
        uint whole = BinaryPrimitives.ReadUInt32LittleEndian(stored);
        if (whole > int.MaxValue - 64) return false;
        int want = (int)Math.Min(whole, FirstInflate);
        while (true)
        {
            if (!Inflate(stored, ref plain, want)) return false;
            var found = Subrecord(plain.AsSpan(0, want), signature, stop, out bool cut, out int offset);
            if (found.Length > 0 || !cut || want == whole)
            {
                if (found.Length > 0) { at = offset; length = found.Length; }
                return true;
            }
            want = (int)whole;
        }
    }

    static string? Text(IndexedPlugin plugin, byte[] full, bool translated, Language language, string? dataFolder)
    {
        if (translated && Localized(plugin) && full.Length == 4)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(full);
            if (id == 0) return null;
            var lookup = StringsOf(plugin, language, dataFolder);
            return lookup != null ? Clean(Lookup(lookup, language, id)) : null;
        }
        return Clean(Decode(full, EncodingOf(translated, language)));
    }

    static string? Lookup(IStringsFolderLookup lookup, Language language, uint id)
    {
        lock (lookup) return lookup.TryLookup(StringsSource.Normal, language, id, out var s) ? s : null;
    }

    static string? Clean(string? s) => CardBuilder.NoTag(s) is { Length: > 0 } c ? c : null;

    // How Mutagen reads text stored in the plugin itself, per language asked: the encodings its own reading state picks
    // (ParsingMeta, the same rule the record layer's sessions get). Since 0.55.0-fixes.12 (C150) it is one reading in
    // every language: Starfield's code page 1252 for a translated text of a plugin with no strings files, and for a
    // text that is never translated.
    static readonly ConcurrentDictionary<Language, EncodingBundle> Encodings = new();

    /// <summary>The encoding Mutagen reads a stored text of this kind with, for the language asked.</summary>
    internal static IMutagenEncoding EncodingOf(bool translated, Language language)
    {
        var bundle = Encodings.GetOrAdd(language, l => ParsingMeta.FactoryNoMasters(
            new BinaryReadParameters { StringsParam = new StringsReadParameters { TargetLanguage = l } }, GameRelease.Starfield, ModKey.Null).Encodings);
        return translated ? bundle.NonLocalized : bundle.NonTranslated;
    }

    /// <summary>A stored text: its bytes up to the closing zero, read by Mutagen's own string reading and encoding, so
    /// the name is the one <c>record</c> and SQL give.</summary>
    static string Decode(ReadOnlySpan<byte> b, IMutagenEncoding encoding) => BinaryStringUtility.ProcessWholeToZString(b, encoding);

    /// <summary>Inflates the first <paramref name="full"/> bytes of a compressed record's data into <paramref name="plain"/>.</summary>
    static bool Inflate(ReadOnlySpan<byte> stored, ref byte[] plain, int full)
    {
        if (stored.Length < 4) return false;
        if (plain.Length < full) { ArrayPool<byte>.Shared.Return(plain); plain = ArrayPool<byte>.Shared.Rent(full); }
        try
        {
            unsafe
            {
                fixed (byte* s = stored)
                {
                    using var packed = new UnmanagedMemoryStream(s + 4, stored.Length - 4);
                    using var zip = new ZLibStream(packed, CompressionMode.Decompress);
                    zip.ReadExactly(plain, 0, full);
                }
            }
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or ArgumentException) { return false; }
        return true;
    }

    static readonly byte[] CellData = "DATA"u8.ToArray();

    /// <summary>
    /// A subrecord that, once met, says the one looked for isn't in the record: a cell's FULL comes before its DATA
    /// (EDID, FULL, DATA, XCLC, …, then XTV2 of up to hundreds of KB). Without it, the 30,000 cells of Starfield.esm with
    /// no name (of 31,150) would each be inflated whole to learn they have none (0.5 s). Held against Mutagen's Name of
    /// every cell of two mods and a sample of Starfield.esm in names-from-bytes; notes\fix-f1-world-names.md checked
    /// every cell in Data.
    /// </summary>
    static byte[]? StopOf(string signature, byte[] looked) => signature == "CELL" && looked.AsSpan().SequenceEqual(Full) ? CellData : null;

    /// <summary>The first subrecord of this signature in a record's data (its long form through XXXX read too), or empty.
    /// The search ends at <paramref name="stop"/> when one is given. <paramref name="cut"/>: the data ended inside a
    /// subrecord before one was found (only the start of a record was given). <paramref name="offset"/>: where the
    /// subrecord's bytes start in the data.</summary>
    static ReadOnlySpan<byte> Subrecord(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, byte[]? stop, out bool cut, out int offset)
    {
        int pos = 0; uint longLength = 0; bool longForm = false;
        cut = false; offset = -1;
        while (pos + 6 <= data.Length)
        {
            var sig = data.Slice(pos, 4);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 4)..]);
            if (sig.SequenceEqual("XXXX"u8) && len == 4)
            {
                if (pos + 10 > data.Length) { cut = true; return default; }
                longLength = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 6)..]);
                longForm = true; pos += 10;
                continue;
            }
            if (longForm) { if (longLength > int.MaxValue) return default; len = (int)longLength; longForm = false; }
            if (stop != null && sig.SequenceEqual(stop)) return default;
            if (pos + 6 + (long)len > data.Length) { cut = true; return default; }
            if (sig.SequenceEqual(signature)) { offset = pos + 6; return data.Slice(pos + 6, len); }
            pos += 6 + len;
        }
        cut = pos < data.Length;
        return default;
    }
}
