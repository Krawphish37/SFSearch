// Text found by its characters (fix F2, GAPS S19): RecordTexts.Search over the whole load order, held against a byte
// scan of its own (RawRecord, every text subrecord's bytes looked at one by one) and against Mutagen.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Mutagen.Bethesda.Strings;
using SFSearch.Index;

namespace SFSearch.Records.Tests;

static class TextSearch
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    static readonly Regex Indexed = new(@"\[\d+\]", RegexOptions.CultureInvariant);

    /// <summary>One record of a plugin, by its entry, through Mutagen's reader of one record.</summary>
    static object ReadOne(IndexedPlugin p, Ctx ctx, int entry)
    {
        using var records = RecordTexts.Reader(p, Language.English, ctx.Data);
        return records.Read(entry);
    }

    /// <summary>The record types searched: everything outside world data, and cells and worldspaces (as find does).</summary>
    internal static bool[] Searched(Segment seg)
    {
        var mask = Tests.NotWorldOf(seg);
        for (int i = 0; i < mask.Length; i++) if (seg.Signatures[i].Signature is "CELL" or "WRLD") mask[i] = true;
        return mask;
    }

    /// <summary>Which subrecords hold text, from the schema: the names, descriptions, book texts and responses are
    /// there, and nothing that isn't a String field.</summary>
    public static string Places(Ctx ctx)
    {
        var s = ctx.Schema;
        string Of(string sig, string sub) => s.Signatures.Contains(sig) && RecordTexts.PlacesOf(s, sig).Top.Values.FirstOrDefault(p => p.Subrecord == sub) is { } p ? $"{p.Field}{(p.Translated ? " (translated)" : "")}" : "-";
        var want = new (string Sig, string Sub, string Field)[]
        {
            ("WEAP", "FULL", "Name"), ("BOOK", "FULL", "Name"), ("BOOK", "DESC", ""), ("INFO", "NAM1", ""), ("QUST", "FULL", "Name"),
            ("MISC", "FULL", "Name"), ("CELL", "FULL", "Name"), ("WRLD", "FULL", "Name"), ("DIAL", "FULL", "Name"), ("MESG", "DESC", ""),
        };
        var lines = new List<string>();
        foreach (var (sig, sub, field) in want)
        {
            string got = Of(sig, sub);
            lines.Add($"{sig} {sub}: {got}");
            Check(got != "-" && got.Contains("(translated)") && (field.Length == 0 || got.StartsWith(field, StringComparison.Ordinal)), $"{sig} {sub} should be a translated text field; the schema gives {got}");
        }
        // Not text: a weapon's DNAM, a keyword list
        Check(Of("WEAP", "DNAM") == "-" && Of("WEAP", "KWDA") == "-", "WEAP DNAM and KWDA aren't text");

        // GAPS S32: subrecords that text and other values both fill
        RecordTexts.Place? PlaceOf(string sig, string sub) => RecordTexts.PlacesOf(s, sig).Top.Values.FirstOrDefault(p => p.Subrecord == sub);
        var gmst = PlaceOf("GMST", "DATA"); var epfd = PlaceOf("PERK", "EPFD");
        Check(gmst is { Shared: RecordTexts.SharedKind.GameSettingString, Translated: true }, $"GMST DATA should be text told by the EditorID's first letter; it is {gmst}");
        Check(epfd is { Shared: RecordTexts.SharedKind.Typed, Translated: true } && epfd.TextPaths.Contains("Ranks[*].Effects[*].Text"), $"PERK EPFD should be text told by Mutagen's reading (Ranks[*].Effects[*].Text); it is {epfd}");
        // A quest's own subrecords: its log entries (CNAM) are translated text, its NAM2 and NAM3 (a log entry's note,
        // the summary) plain text; NAM1 and NAM4 are its dialogue's (records of their own), not the quest's
        var cnam = PlaceOf("QUST", "CNAM"); var nam2 = PlaceOf("QUST", "NAM2"); var nam3 = PlaceOf("QUST", "NAM3");
        Check(cnam is { Shared: RecordTexts.SharedKind.No, Translated: true, Field: "Stages[*].LogEntries[*].Entry" }, $"QUST CNAM should be the log entries' translated text; it is {cnam}");
        Check(nam2 is { Shared: RecordTexts.SharedKind.No, Translated: false } && nam3 is { Shared: RecordTexts.SharedKind.No, Translated: false, Field: "Summary" }, $"QUST NAM2 and NAM3 should be plain text; they are {nam2} and {nam3}");
        Check(PlaceOf("QUST", "NAM1") == null && PlaceOf("QUST", "NAM4") == null, "QUST NAM1 and NAM4 aren't a quest's own text");
        // Nothing is placed by a record another one holds (a cell's navmeshes, a topic's responses, a quest's scenes)
        var held = s.Signatures.SelectMany(sig => RecordTexts.PlacesOf(s, sig).All.Select(p => (sig, p)))
            .Where(x => x.p.Field.Contains("NavigationMeshes[", StringComparison.Ordinal) || x.sig == "QUST" && (x.p.Field.StartsWith("DialogTopics[", StringComparison.Ordinal) || x.p.Field.StartsWith("Scenes[", StringComparison.Ordinal)) || x.p.Field.StartsWith("TopCell", StringComparison.Ordinal) || x.sig == "DIAL" && x.p.Field.StartsWith("Responses[", StringComparison.Ordinal))
            .Select(x => $"{x.sig} {x.p.Subrecord} {x.p.Field}").ToList();
        Check(held.Count == 0, $"text placed by a record another one holds: {string.Join("; ", held.Take(5))}");
        // Map markers' names, in world data
        Check(Of("REFR", "FULL").StartsWith("MapMarker.Name (translated)", StringComparison.Ordinal), $"REFR FULL should be the map marker's name; it is {Of("REFR", "FULL")}");
        int types = 0, places = 0, translated = 0;
        var ambiguous = new List<string>();
        foreach (var sig in s.Signatures.OrderBy(x => x, StringComparer.Ordinal))
        {
            var m = RecordTexts.PlacesOf(s, sig);
            if (m.Count > 0) types++;
            places += m.Count; translated += m.All.Count(p => p.Translated);
            foreach (var a in m.Ambiguous) ambiguous.Add($"{sig} {a}");
            foreach (var a in m.SharedRead) lines.Add($"shared, read: {sig} {a}");
            foreach (var p in m.All.OrderBy(p => p.Within ?? "").ThenBy(p => p.Subrecord, StringComparer.Ordinal)) lines.Add($"{sig} {p.Subrecord}{(p.Within != null ? $" (in {p.Within})" : "")} {p.Field}{(p.Translated ? " translated" : "")}");
        }
        // Only VMAD (scripts' properties) is left out; every other shared subrecord is read
        static bool Vmad(string a) => a.Split(' ')[1].TrimEnd(':') == "VMAD";
        Check(ambiguous.All(Vmad), $"left out besides VMAD: {string.Join("; ", ambiguous.Where(a => !Vmad(a)).Take(5))}");
        int sharedRead = lines.Count(l => l.StartsWith("shared, read: ", StringComparison.Ordinal));
        int sharedTop = lines.Count(l => l.StartsWith("shared, read: ", StringComparison.Ordinal) && !l.Contains("(in ", StringComparison.Ordinal));
        ctx.WriteReport("text-places.txt", lines.Concat(ambiguous.Select(a => "left out (another field fills it too): " + a)));
        return $"{N(types)} record types have {N(places)} text subrecords ({N(translated)} translated); {N(sharedRead)} of them shared with other values and told apart ({sharedTop} the records' own: GMST DATA by the EditorID, the rest by Mutagen's reading; {sharedRead - sharedTop} inside components); {N(ambiguous.Count)} left out, every one VMAD (scripts' properties). " +
            string.Join("; ", lines.Take(want.Length)) + $"; GMST DATA: {gmst?.Shared}; PERK EPFD: {epfd?.Shared} ({epfd?.Field}); QUST CNAM: {cnam?.Field}";
    }

    /// <summary>
    /// The search for text outside ASCII over the stored text of every plugin, held against a byte scan of its own;
    /// a name search in the strings files of Starfield.esm, held against Mutagen's lookup; and the cost over the
    /// whole load order.
    /// </summary>
    public static string Search(Ctx ctx)
    {
        var index = ctx.DataIndex; var schema = ctx.Schema;
        var plugins = index.Plugins.Where(p => p.Segment != null && p.Header != null).ToList();
        var parts = new List<string>();
        var report = new List<string>();

        // ---- 1. Stored text outside ASCII, every plugin: the search against the byte scan
        var nonAscii = new Regex(@"[^\x00-\x7F]", RegexOptions.CultureInvariant);
        var clock = Stopwatch.StartNew();
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var foundPlugins = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        long texts = 0; int stored = 0;
        var details = new List<string>();
        var hitsByPlugin = new Dictionary<string, List<RecordTexts.Hit>>(StringComparer.OrdinalIgnoreCase);
        int typedHits = 0, typedRead = 0;
        foreach (var p in plugins)
        {
            using var records = RecordTexts.Reader(p, Language.English, ctx.Data);
            var r = RecordTexts.Search(p, Searched(p.Segment!), schema, nonAscii, Language.English, ctx.Data, RecordTexts.From.Stored, records.Read);
            if (r.Localized) continue;
            stored++; texts += r.Texts; typedRead += r.TypedRead;
            Check(r.NotInflated == 0, $"{p.Name}: {r.NotInflated} compressed records couldn't be inflated");
            Check(r.TypedNotRead == 0, $"{p.Name}: {r.TypedNotRead} records couldn't be read through Mutagen's reader");
            if (r.Hits.Count > 0) hitsByPlugin[p.Name] = r.Hits;
            foreach (var h in r.Hits)
            {
                // A text told by Mutagen's reading (its field has each list item's place) isn't the byte scan's to find
                if (Indexed.IsMatch(h.Field)) typedHits++;
                else found.Add($"{p.Name}|{h.Entry}|{h.Subrecord}|{h.Text}");
                foundPlugins.Add(p.Name); details.Add($"{p.Name} | {index.Describe(new RecordRef(p.Id, h.Entry)).Label} {p.Segment!.SignatureOf(p.Segment[h.Entry])} | {h.Subrecord} {h.Field} | {h.Text}");
            }
        }
        double searchMs = clock.Elapsed.TotalMilliseconds;

        // The byte scan: every record of the same types read whole by RawRecord, every text subrecord's bytes up to
        // the zero looked at for a byte over 0x7F
        clock.Restart();
        var scanned = new SortedSet<string>(StringComparer.Ordinal);
        // What fix-lang's Python scan looked at: the name (FULL) of uncompressed records outside cells and worldspaces
        var asBefore = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        long scannedTexts = 0;
        foreach (var p in plugins)
        {
            if (RecordTexts.IsLocalized(p)) continue;
            var seg = p.Segment!;
            var mask = Searched(seg);
            using SafeFileHandle file = File.OpenHandle(p.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            for (int i = 0; i < seg.FirstUnresolved; i++)
            {
                var e = seg[i];
                if (!mask[e.Signature]) continue;
                var places = RecordTexts.PlacesOf(schema, seg.SignatureOf(e));
                if (places.Count == 0) continue;
                var raw = RawRecord.Read(file, e.Offset);
                // Inside a component (BFCB ... BFCE) or an object template list (OBTE ... STOP), told apart here on its own
                string? within = null; int edidFirst = -1;
                foreach (var sub in raw.Subrecords)
                {
                    // A game setting's DATA is text when its EditorID starts with a lower-case s
                    if (sub.Signature == "EDID" && edidFirst < 0) { var c = raw.Content(sub); edidFirst = c.Length > 0 ? c[0] : 0; }
                    if (sub.Signature == "BFCB") { within = "Components"; continue; }
                    if (sub.Signature == "OBTE") { within = "ObjectTemplates"; continue; }
                    if (sub.Signature == "BFCE" && within == "Components" || sub.Signature == "STOP" && within == "ObjectTemplates") { within = null; continue; }
                    var place = places.All.FirstOrDefault(x => x.Within == within && x.Subrecord == sub.Signature);
                    if (place == null || !place.Translated || place.Shared == RecordTexts.SharedKind.Typed) continue;
                    if (place.Shared == RecordTexts.SharedKind.GameSettingString && edidFirst != 's') continue;
                    scannedTexts++;
                    var bytes = raw.Content(sub);
                    int zero = bytes.IndexOf((byte)0);
                    if (zero >= 0) bytes = bytes[..zero];
                    bool high = false;
                    foreach (byte b in bytes) if (b > 0x7F) { high = true; break; }
                    if (!high) continue;
                    scanned.Add($"{p.Name}|{i}|{sub.Signature}|{RecordTexts.Decode(raw.Content(sub), true, Language.English)}");
                    if (sub.Signature == "FULL" && within == null && !e.IsCompressed && seg.SignatureOf(e) is not ("CELL" or "WRLD")) asBefore.Add(p.Name);
                }
            }
        }
        double scanMs = clock.Elapsed.TotalMilliseconds;
        var onlySearch = found.Except(scanned).ToList(); var onlyScan = scanned.Except(found).ToList();
        report.Add($"# Stored text outside ASCII: search {found.Count}, byte scan {scanned.Count}");
        report.AddRange(details.Select(f => "  " + f));
        foreach (var x in onlySearch) report.Add("only the search: " + x);
        foreach (var x in onlyScan) report.Add("only the byte scan: " + x);
        ctx.WriteReport("text-search.txt", report);
        Check(texts == scannedTexts + typedHits, $"the search read {N(texts)} stored text fields ({typedHits} told by Mutagen's reading), the byte scan {N(scannedTexts)}");
        Check(onlySearch.Count == 0 && onlyScan.Count == 0, $"the search and the byte scan disagree: {onlySearch.Count} only in the search ({string.Join("; ", onlySearch.Take(3))}), {onlyScan.Count} only in the scan ({string.Join("; ", onlyScan.Take(3))})");
        // Every text field, compressed records and cells included, finds more plugins than the Python scan of fix-lang
        // (names of uncompressed records only); that scan's part of the answer is its two plugins
        Check(asBefore.SetEquals(new[] { "economy.esm", "apc_galaxysmartcontainers.esm" }), $"the names of uncompressed records outside ASCII are in {string.Join(", ", asBefore)}; fix-lang's scan found economy.esm and apc_galaxysmartcontainers.esm");
        Check(foundPlugins.IsSupersetOf(asBefore), "the search misses a plugin of the names-only scan");
        Check(found.Any(f => f.StartsWith("economy.esm|", StringComparison.Ordinal) && f.EndsWith("|FULL|Samson’s Art", StringComparison.Ordinal)), "Samson’s Art isn't among the hits");
        Check(found.Count(f => f.StartsWith("apc_galaxysmartcontainers.esm|", StringComparison.Ordinal) && f.Contains('—')) >= 6, "the six Galaxy Handbook em dashes aren't among the hits");
        // Each hit's text is the text Mutagen gives for the field (the names: the record's Name)
        var eng = ctx.Engine; int named = 0;
        foreach (var (plugin, hits) in hitsByPlugin)
        {
            var p = index.Plugin(plugin)!;
            eng.Sessions.Use(plugin);
            foreach (var h in hits.Where(h => h.Subrecord == "FULL"))
            {
                var rec = eng.Load(new RecordRef(p.Id, h.Entry));
                string? truth = Tests.MutagenNameOf(rec.Record);
                Check(truth == CardBuilder.NoTag(h.Text), $"{rec.Label}: the search says \"{h.Text}\", Mutagen \"{truth}\"");
                named++;
            }
        }
        parts.Add($"stored text outside ASCII ([^\\x00-\\x7F]) in {N(stored)} plugins with no strings files, {N(texts)} text fields: {found.Count} texts in {foundPlugins.Count} plugins ({string.Join(", ", foundPlugins.Select(f => $"{f} {found.Count(x => x.StartsWith(f + "|", StringComparison.OrdinalIgnoreCase))}"))}), the same {scanned.Count} the byte scan finds; names of uncompressed records only (fix-lang's scan): {string.Join(" and ", asBefore)}; {named} names equal to Mutagen's Name; {typedRead} records read through Mutagen's reader for shared subrecords, {typedHits} texts from them; search {searchMs / 1000:F2} s, byte scan {scanMs / 1000:F2} s");

        // ---- 2. A name pattern in the strings files: Eon in English and Japanese, against Mutagen's lookup
        var sf = index.Plugin("Starfield.esm")!;
        eng.Sessions.Use("Starfield.esm");
        foreach (var (pattern, language, want) in new[] { (@"^Eon$", Language.English, "Eon"), ("^イーオン$", Language.Japanese, "イーオン") })
        {
            clock.Restart();
            var r = RecordTexts.Search(sf, Searched(sf.Segment!), schema, new Regex(pattern, RegexOptions.CultureInvariant), language, ctx.Data, RecordTexts.From.Both);
            var weapons = r.Hits.Where(h => sf.Segment!.SignatureOf(sf.Segment[h.Entry]) == "WEAP").ToList();
            var eon = weapons.Where(h => index.Describe(new RecordRef(sf.Id, h.Entry)).FormKey == "0476C4:Starfield.esm").ToList();
            Check(eon.Count == 1 && eon[0].Field == "Name" && eon[0].Text == want && weapons.All(h => h.Text == want),
                $"{pattern} in {language}: weapons found {string.Join(", ", weapons.Select(h => index.Describe(new RecordRef(sf.Id, h.Entry)).Label + " " + h.Field + " " + h.Text))}");
            Check(r.FilesRead.Count == 3 && r.FilesRead.All(f => f.Contains(language == Language.English ? "_en." : "_ja.", StringComparison.OrdinalIgnoreCase)), $"{language}: files read {string.Join(", ", r.FilesRead)}");
            parts.Add($"{pattern} in Starfield.esm's {language} strings ({string.Join(", ", r.FilesRead)}, {N(r.Strings)} strings): {r.Hits.Count} texts, {weapons.Count} weapons named so, Eon (0476C4:Starfield.esm) Name among them, {r.Ms:F0} ms");
        }
        // Every strings hit's text is Mutagen's own lookup of that ID (books' DESC, responses' NAM1, names)
        var lookup = StringsFolderLookupOverlay.TypicalFactory(Mutagen.Bethesda.GameRelease.Starfield, Mutagen.Bethesda.Plugins.ModKey.FromFileName("Starfield.esm"), ctx.Data, new StringsReadParameters { TargetLanguage = Language.English, BsaFolderOverride = ctx.Data });
        var vol = RecordTexts.Search(sf, Searched(sf.Segment!), schema, new Regex("Constellation", RegexOptions.CultureInvariant), Language.English, ctx.Data, RecordTexts.From.Strings);
        int same = 0; var bySub = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var h in vol.Hits)
        {
            var source = h.File!.EndsWith(".dlstrings", StringComparison.OrdinalIgnoreCase) ? StringsSource.DL : h.File.EndsWith(".ilstrings", StringComparison.OrdinalIgnoreCase) ? StringsSource.IL : StringsSource.Normal;
            Check(lookup.TryLookup(source, Language.English, h.StringId, out var text) && text == h.Text, $"string {h.StringId:X8} of {h.File}: the search has \"{h.Text}\", Mutagen's lookup \"{text}\"");
            same++;
            string key = $"{sf.Segment!.SignatureOf(sf.Segment[h.Entry])} {h.Subrecord} {Path.GetExtension(h.File)}";
            bySub[key] = bySub.GetValueOrDefault(key) + 1;
        }
        Check(bySub.Keys.Any(k => k.StartsWith("BOOK DESC", StringComparison.Ordinal)) && bySub.Keys.Any(k => k.StartsWith("INFO NAM1", StringComparison.Ordinal)), $"'Constellation' should be found in book texts and responses: {string.Join(", ", bySub.Keys)}");
        parts.Add($"'Constellation' in Starfield.esm: {vol.Hits.Count} texts ({string.Join(", ", bySub.Select(k => $"{k.Key} {k.Value}"))}), each the text of Mutagen's lookup of its ID; {vol.StringsNotNamed} matching strings no record outside world data names; {vol.Ms:F0} ms");

        // ---- 3. String IDs shared by two strings files of one plugin, and the whole load order's cost
        clock.Restart();
        int localized = 0, files = 0, shared = 0, stamped = 0; long strings = 0; var sharedIn = new List<string>(); var stampDiffers = new List<string>();
        foreach (var p in plugins.Where(RecordTexts.IsLocalized))
        {
            localized++;
            var fs = RecordTexts.StringsOf(p, Language.English, ctx.Data);
            files += fs.Count; strings += fs.Sum(f => f.Strings.Count);
            var seen = new Dictionary<uint, int>();
            foreach (var f in fs) foreach (var kv in f.Strings) seen[kv.Key] = seen.GetValueOrDefault(kv.Key) + 1;
            int here = seen.Values.Count(c => c > 1);
            if (here > 0) { shared += here; sharedIn.Add($"{p.Name} {here}"); }
            // The strings stamp (the header index's own look at the Strings folder and the archives' name tables, GAPS
            // S15) names the English files Mutagen's lookup lists (EnumerateTables, C184), each from the same place
            if (p.Strings.Stamped)
            {
                stamped++;
                var en = p.Strings.Files.Where(f => f.Name.Contains("_en.", StringComparison.OrdinalIgnoreCase)).ToList();
                bool sameFiles = en.Count == fs.Count && en.All(sf => fs.Any(m => m.Name.Equals(sf.Name, StringComparison.OrdinalIgnoreCase)
                    && (sf.InArchive ? m.From.StartsWith(sf.From, StringComparison.OrdinalIgnoreCase) : m.From.Equals(sf.From, StringComparison.OrdinalIgnoreCase))));
                if (!sameFiles) stampDiffers.Add($"{p.Name}: stamp [{string.Join(", ", en.Select(f => f.Name + " in " + f.From))}], Mutagen [{string.Join(", ", fs.Select(f => f.From))}]");
            }
        }
        double filesMs = clock.Elapsed.TotalMilliseconds;
        Check(stampDiffers.Count == 0, $"the strings stamp and Mutagen's EnumerateTables name other files for {stampDiffers.Count} plugins: {string.Join("; ", stampDiffers.Take(3))}");
        parts.Add($"{localized} localized plugins, {files} English strings files, {N(strings)} strings read in {filesMs / 1000:F2} s (listed by Mutagen's EnumerateTables and EnumerateStrings; the strings stamp names the same files for all {stamped} stamped); IDs in two files of one plugin: {shared}{(sharedIn.Count > 0 ? " (" + string.Join(", ", sharedIn.Take(6)) + ")" : "")}");
        clock.Restart();
        long all = 0;
        foreach (var p in plugins) all += RecordTexts.Search(p, Searched(p.Segment!), schema, nonAscii, Language.English, ctx.Data, RecordTexts.From.Both).Hits.Count;
        parts.Add($"[^\\x00-\\x7F] over stored text and English strings, whole load order ({plugins.Count} plugins), strings already read: {N(all)} texts in {clock.Elapsed.TotalSeconds:F2} s");
        // ---- 4. GAPS S32: game settings, perk entry points, quest log entries, map markers
        parts.Add(Shared(ctx, plugins, report));
        ctx.WriteReport("text-search.txt", report);
        return string.Join("; ", parts);
    }

    /// <summary>
    /// The text fields whose subrecord other values fill too (GAPS S32), held against Mutagen's own reading of every
    /// record of the type: every game setting and every perk of every plugin is read through Mutagen's reader, and the
    /// texts it gives must be exactly the ones the search finds (pattern (?s). : every text that isn't empty). Then a
    /// quest log entry, a map marker's name (world data, searched with --world), and the cost of both.
    /// </summary>
    static string Shared(Ctx ctx, List<IndexedPlugin> plugins, List<string> report)
    {
        var index = ctx.DataIndex; var schema = ctx.Schema;
        var any = new Regex("(?s).", RegexOptions.CultureInvariant);
        var parts = new List<string>();
        foreach (var (sig, path) in new[] { ("GMST", "Data"), ("PERK", "Ranks[*].Effects[*].Text") })
        {
            var clock = Stopwatch.StartNew();
            var searched = new SortedSet<string>(StringComparer.Ordinal); var truth = new SortedSet<string>(StringComparer.Ordinal);
            int read = 0, typedRead = 0; var examples = new List<string>();
            foreach (var p in plugins)
            {
                var seg = p.Segment!;
                var mask = seg.Signatures.Select(x => x.Signature == sig).ToArray();
                if (!mask.Any(m => m)) continue;
                using var records = RecordTexts.Reader(p, Language.English, ctx.Data);
                RecordTexts.ReadRecord reader = records.Read;
                var r = RecordTexts.Search(p, mask, schema, any, Language.English, ctx.Data, RecordTexts.From.Both, reader);
                Check(r.TypedNotRead == 0, $"{p.Name}: {r.TypedNotRead} {sig} records couldn't be read through Mutagen's reader");
                typedRead += r.TypedRead;
                foreach (var h in r.Hits.Where(h => Indexed.Replace(h.Field, "[*]") == path))
                {
                    searched.Add($"{p.Name}|{h.Entry}|{h.Field}|{(h.File == null ? h.Text : h.StringId.ToString("X8", CultureInfo.InvariantCulture))}");
                    if (examples.Count < 4 && (examples.Count < 2 || h.File == null)) examples.Add($"{index.Describe(new RecordRef(p.Id, h.Entry)).Label} {h.Field} \"{h.Text}\" ({p.Name}, {h.File ?? "stored"})");
                }
                // Mutagen's reading of every record of the type: its translated texts that aren't empty (a localized
                // plugin's: string IDs its strings files hold with a text)
                var ids = r.Localized ? RecordTexts.StringsOf(p, Language.English, ctx.Data).SelectMany(f => f.Strings).Where(kv => kv.Value.Length > 0).Select(kv => kv.Key).ToHashSet() : null;
                for (int i = 0; i < seg.FirstUnresolved; i++)
                {
                    if (!mask[seg[i].Signature]) continue;
                    object rec;
                    try { rec = reader(i); read++; }
                    catch (RecordException x) { Check(false, $"{p.Name} entry {i}: {x.Message}"); continue; }
                    foreach (var t in RecordTexts.TextsAt(rec, new[] { path }).Where(t => t.Translated))
                        if (r.Localized ? t.StringId is uint k && ids!.Contains(k) : t.Text.Length > 0)
                            truth.Add($"{p.Name}|{i}|{t.Path}|{(r.Localized ? t.StringId!.Value.ToString("X8", CultureInfo.InvariantCulture) : t.Text)}");
                }
            }
            var onlySearch = searched.Except(truth).ToList(); var onlyMutagen = truth.Except(searched).ToList();
            report.Add($"# {sig} {path}: search {searched.Count}, Mutagen {truth.Count}");
            report.AddRange(onlySearch.Select(x => "only the search: " + x)); report.AddRange(onlyMutagen.Select(x => "only Mutagen: " + x));
            Check(onlySearch.Count == 0 && onlyMutagen.Count == 0, $"{sig}: the search and Mutagen's reading disagree: {onlySearch.Count} only in the search ({string.Join("; ", onlySearch.Take(3))}), {onlyMutagen.Count} only in Mutagen ({string.Join("; ", onlyMutagen.Take(3))})");
            Check(searched.Count > 0, $"{sig}: no text found");
            parts.Add($"{sig} {path}: {searched.Count} texts, the same as Mutagen's reading of all {read} {sig} records ({typedRead} read by the search), e.g. {string.Join("; ", examples)}; {clock.Elapsed.TotalSeconds:F1} s");
        }

        // A quest's log entry (QUST CNAM, left out before: the quest's own dialogue fills CNAM too in Mutagen's model)
        {
            var p = index.Plugin("stlazarusawakened.esm")!;
            var r = RecordTexts.Search(p, Searched(p.Segment!), schema, new Regex("There’s a slate on the desk", RegexOptions.CultureInvariant), Language.English, ctx.Data, RecordTexts.From.Both);
            var h = r.Hits.SingleOrDefault();
            string label = h == null ? "-" : index.Describe(new RecordRef(p.Id, h.Entry)).Label;
            var mutagen = h == null ? new List<RecordTexts.TypedText>() : RecordTexts.TextsAt(ReadOne(p, ctx, h.Entry), new[] { "Stages[*].LogEntries[*].Entry" });
            Check(h != null && label == "001STLazarusAwakened (024A2D:stlazarusawakened.esm)" && h.Field == "Stages[*].LogEntries[*].Entry" && mutagen.Any(t => t.Text == h.Text),
                $"the quest log entry: {r.Hits.Count} hits ({label} {h?.Field})");
            parts.Add($"quest log entry: {label} {h?.Field} \"{h?.Text[..Math.Min(40, h.Text.Length)]}…\", one of Mutagen's {mutagen.Count} log entries of the quest");
        }

        // Map markers' names (REFR FULL, world data): The Lodge in Starfield.esm, against Mutagen's MapMarker.Name
        {
            var sf = index.Plugin("Starfield.esm")!; var seg = sf.Segment!;
            var mask = seg.Signatures.Select(x => x.Signature == "REFR").ToArray();
            var clock = Stopwatch.StartNew();
            var r = RecordTexts.Search(sf, mask, schema, new Regex("^The Lodge$", RegexOptions.CultureInvariant), Language.English, ctx.Data, RecordTexts.From.Both);
            double ms = clock.Elapsed.TotalMilliseconds;
            var h = r.Hits.SingleOrDefault();
            string label = h == null ? "-" : index.Describe(new RecordRef(sf.Id, h.Entry)).Label;
            var name = h == null ? null : RecordTexts.TextsAt(ReadOne(sf, ctx, h.Entry), new[] { "MapMarker.Name" }).SingleOrDefault();
            Check(h != null && label == "NewAtlantisMapMarkerLodge (0F93F1:Starfield.esm)" && h.Field == "MapMarker.Name" && name?.StringId == h.StringId,
                $"the map marker: {r.Hits.Count} hits ({label} {h?.Field}, string {h?.StringId:X8}; Mutagen's MapMarker.Name string {name?.StringId:X8})");
            parts.Add($"map marker: {label} {h?.Field} \"{h?.Text}\" (string {h?.StringId:X8}, Mutagen's MapMarker.Name the same), {N(r.Texts)} REFR texts of Starfield.esm read in {ms:F0} ms (its strings already read)");
        }

        // The cost of world data (--world) over the whole load order: every world type's texts, against today's set
        {
            var clock = Stopwatch.StartNew();
            var pattern = new Regex("^Jemison Mercantile$", RegexOptions.CultureInvariant);
            long without = 0, with = 0; int hitsWithout = 0, hitsWith = 0;
            foreach (var p in plugins) { var r = RecordTexts.Search(p, Searched(p.Segment!), schema, pattern, Language.English, ctx.Data, RecordTexts.From.Both); without += r.Texts; hitsWithout += r.Hits.Count; }
            double msWithout = clock.Elapsed.TotalMilliseconds; clock.Restart();
            var markers = new List<string>();
            foreach (var p in plugins)
            {
                var r = RecordTexts.Search(p, Enumerable.Repeat(true, p.Segment!.Signatures.Count).ToArray(), schema, pattern, Language.English, ctx.Data, RecordTexts.From.Both);
                with += r.Texts; hitsWith += r.Hits.Count;
                foreach (var h in r.Hits.Where(h => p.Segment.SignatureOf(p.Segment[h.Entry]) == "REFR")) markers.Add($"{index.Describe(new RecordRef(p.Id, h.Entry)).Label} ({p.Name})");
            }
            double msWith = clock.Elapsed.TotalMilliseconds;
            Check(markers.Count > 0, "^Jemison Mercantile$ with world data finds no map marker");
            parts.Add($"^Jemison Mercantile$ over the whole load order (strings already read): {hitsWithout} texts in {msWithout / 1000:F2} s; with world data {hitsWith} ({string.Join(", ", markers)}) in {msWith / 1000:F2} s, {N(with - without)} more text fields");
        }

        // check2's reads: the shared subrecords with a byte over 0x7F in the stored text of every plugin (Starfield.esm's
        // fields that are never translated included), each record read through Mutagen's reader
        {
            var clock = Stopwatch.StartNew();
            int records = 0, confirmed = 0, notRead = 0; long offered = 0; string starfield = ""; var where = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in plugins)
            {
                bool localized = RecordTexts.IsLocalized(p);
                var walked = RecordTexts.Walk(p, Searched(p.Segment!), schema, place => place.Shared == RecordTexts.SharedKind.Typed, (place, data) => { foreach (byte b in data) if (b > 0x7F) return true; return false; });
                if (walked.Found.Count == 0) continue;
                offered += walked.Found.Count;
                foreach (var f in walked.Found) { string k = $"{p.Segment!.SignatureOf(p.Segment[f.Entry])} {f.Place.Subrecord}{(f.Place.Within != null ? " in " + f.Place.Within : "")}"; where[k] = where.GetValueOrDefault(k) + 1; }
                using var reader = RecordTexts.Reader(p, Language.English, ctx.Data);
                var c = RecordTexts.Confirm(walked.Found, reader.Read, Language.English, t => !(localized && t.Translated) && t.StringId == null);
                records += c.Read; notRead += c.NotRead;
                if (p.Name.Equals("Starfield.esm", StringComparison.OrdinalIgnoreCase)) starfield = $"Starfield.esm {N(c.Read)} records in {c.Ms / 1000:F1} s";
                confirmed += c.Texts.Count(x => x.Raw!.Any(b => b > 0x7F));
            }
            parts.Add($"check2's shared subrecords with a byte over 0x7F, whole load order: {N(offered)} in {N(records)} records read through Mutagen's reader ({notRead} not read), {confirmed} of them text outside ASCII, {clock.Elapsed.TotalSeconds:F1} s ({starfield}; {string.Join(", ", where.OrderByDescending(w => w.Value).Take(5).Select(w => $"{w.Key} {N(w.Value)}"))})");
        }
        return string.Join("; ", parts);
    }
}
