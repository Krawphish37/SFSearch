// GAPS S15: a localized plugin's string files are stamped with it. A changed string file (loose, or the archive it is
// read from) reopens the plugin's session, and its texts are read from the files as they are now.
using System.Buffers.Binary;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins.Aspects;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Records.Tests;

static class StringsChanged
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);

    static bool HandsOff(string name) => name.StartsWith("darkstarpatch_", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(name, @"^sfta0[1-6]\.esm$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static void MustBeACopy(Ctx ctx, string path)
    {
        if (!Path.GetFullPath(path).StartsWith(ctx.Work + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"refusing to change {path}: it isn't inside the tests' work folder");
    }

    /// <summary>
    /// A copy of a small localized plugin with its string files loose beside it (taken out of its archive in Data). A
    /// text is read; the copy's English .strings file is changed (that one text) while the session holds the plugin
    /// open; the next use reopens it and reads the new text. Then the file's date alone, then a change during a read.
    /// </summary>
    public static string Run(Ctx ctx)
    {
        var data = ctx.DataIndex;
        Check(data.StampsStrings, "the tests' index doesn't look for string files");
        // What the stamps cost: a look with everything known, over every localized plugin in Data. (The first look's
        // count and time of archives' name tables read came from counters in StringFiles until J81: no test code in the
        // library. The archives the string files come from are counted here from the stamps.)
        var localized = data.Plugins.Where(p => p.Header is { Localized: true }).ToList();
        var lookClock = System.Diagnostics.Stopwatch.StartNew();
        var again = data.StringsNow(localized);
        double lookMs = lookClock.Elapsed.TotalMilliseconds;
        Check(again.Zip(localized).All(x => x.First.SameAs(x.Second.Strings)), "a second look at Data's string files gave other stamps with nothing changed");
        string cost = $"Stamps of Data's {localized.Count} localized plugins ({localized.Sum(p => p.Strings.Files.Count):N0} string files, {localized.Count(p => p.Strings.Files.Count == 0)} with none): {localized.SelectMany(p => p.Strings.Files.Where(f => f.InArchive).Select(f => f.From)).Distinct(StringComparer.OrdinalIgnoreCase).Count()} archives hold them; a look with them known takes {lookMs:F1} ms.";
        // The smallest localized plugin whose English texts come from an archive, whose masters are all there, and one
        // of whose own records has a name from its string files
        var source = data.Plugins
            .Where(p => p.Header is { Localized: true } && p.Segment != null && !HandsOff(p.Name) && p.Problems.MissingMasters.Count == 0 && p.Stamp.Length < 4_000_000
                && p.Strings.Files.Any(f => f.InArchive && f.Name.EndsWith("_en.strings", StringComparison.Ordinal)))
            .OrderBy(p => p.Stamp.Length)
            .FirstOrDefault(p => Enumerable.Range(0, p.Segment!.FirstUnresolved).Any(i => p.Segment[i].Owner == p.Segment.SelfOwner && !p.Segment[i].IsCompressed && RecordNames.Of(p, i, ctx.Schema, dataFolder: ctx.Data) is { Length: > 0 }))
            ?? throw new TestFailed("no small localized plugin with its English string files in an archive and a named record was found");
        var archived = source.Strings.Files.First(f => f.Name.EndsWith("_en.strings", StringComparison.Ordinal));
        Check(archived.InArchive && File.Exists(archived.From), $"{source.Name}'s English strings file is said to be in {archived.From}");

        // The copy, and its string files taken out of the archive into Strings\ beside it
        string folder = ctx.Folder("strings");
        string copy = Path.Combine(folder, source.Name);
        File.Copy(source.Path, copy, overwrite: true);
        MustBeACopy(ctx, copy);
        string loose = Path.Combine(folder, "Strings");
        Directory.CreateDirectory(loose);
        var reader = Archive.CreateReader(GameRelease.Starfield, archived.From);
        Check(reader.TryGetFolder("strings", out var inArchive), $"{Path.GetFileName(archived.From)} has no Strings folder");
        int taken = 0;
        foreach (var item in inArchive!.Files)
        {
            string name = Path.GetFileName(item.Path.ToString().Replace('/', '\\')).ToLowerInvariant();
            if (!source.Strings.Files.Any(f => f.Name == name)) continue;
            File.WriteAllBytes(Path.Combine(loose, name), item.GetBytes());
            taken++;
        }
        Check(taken == source.Strings.Files.Count, $"{taken} of {source.Strings.Files.Count} string files were taken out of {Path.GetFileName(archived.From)}");
        string strings = Path.Combine(loose, archived.Name);
        MustBeACopy(ctx, strings);

        var files = new List<string> { copy };
        files.AddRange(source.Header!.Masters.Select(m => data.Plugin(m)!.Path));
        using var index = new HeaderIndex(PluginSource.FromFiles(files), ctx.Options());
        index.Refresh();
        var p = index.Plugin(source.Name)!;
        Check(p.Strings.Files.Count == taken && p.Strings.Files.All(f => !f.InArchive && f.From.StartsWith(loose, StringComparison.OrdinalIgnoreCase)),
            $"the copy's string files should be the loose ones beside it: {string.Join(", ", p.Strings.Files.Select(f => f.Where))}");
        Check(StringFiles.LooseFolder(copy, source.Name, ctx.Data).Equals(loose, StringComparison.OrdinalIgnoreCase), "the copy's loose folder isn't the Strings folder beside it");
        using var engine = new RecordEngine(index, ctx.Schema, Path.Combine(ctx.Work, "verify-strings"), ctx.Data);

        // A record whose name is a text of the English .strings file
        var use1 = engine.Sessions.Use(source.Name);
        Check(use1.Notes.Count == 0 && engine.Sessions.Opens == 1, $"the first use: {use1.Notes.Count} notes, {engine.Sessions.Opens} opens");
        LoadedRecord? rec = null; uint id = 0; string before = "";
        var seg = p.Segment!;
        for (int i = 0; i < seg.FirstUnresolved && rec == null; i++)
        {
            if (seg[i].Owner != seg.SelfOwner || seg[i].IsCompressed) continue;
            var raw = engine.Raw(new RecordRef(p.Id, i));
            var full = raw.Subrecords.FirstOrDefault(s => s.Signature == "FULL" && s.Length == 4);
            if (full.Signature == null) continue;
            uint sid = BinaryPrimitives.ReadUInt32LittleEndian(raw.Content(full));
            if (sid == 0) continue;
            var r = engine.Load(new RecordRef(p.Id, i));
            if ((r.Record as ITranslatedNamedGetter)?.Name?.String is { Length: > 0 } text) { rec = r; id = sid; before = text; }
        }
        Check(rec != null, $"no record of {source.Name} has a name from its string files");
        Check(Card(engine, rec!).Contains(before, StringComparison.Ordinal), $"the card of {rec!.Label} doesn't show its name \"{before}\"");
        engine.Sessions.Confirm(use1);
        var keyBefore = p.Strings.Key;
        var use2 = engine.Sessions.Use(source.Name);
        Check(use2.Notes.Count == 0 && use2.Refresh == null && engine.Sessions.Opens == 1, "with nothing changed, the plugin was looked at again or reopened");

        // The English .strings file changed: that one text, while the session holds the plugin open
        string after = "S15 test: " + before;
        Rewrite(strings, id, after);
        string during = "";
        try { engine.Sessions.Confirm(use2); } catch (RecordException x) { during = x.Message; }
        Check(during.Contains("string files", StringComparison.Ordinal) && during.Contains("changed during the read", StringComparison.Ordinal), $"a change of a string file during a command wasn't noticed: \"{during}\"");
        var use3 = engine.Sessions.Use(source.Name);
        Check(use3.Refresh != null && use3.Refresh.StringsChanged.Contains(source.Name, StringComparer.OrdinalIgnoreCase), $"the index's refresh didn't list the plugin's string files as changed: {string.Join(", ", use3.Refresh?.StringsChanged ?? new())}");
        Check(use3.Refresh!.ContentChanged.Count == 0 && use3.Refresh.PassesRun.Count == 0, "the plugin itself didn't change, yet its records were read again");
        Check(use3.Reopened.Contains(source.Name, StringComparer.OrdinalIgnoreCase) && use3.Notes.Count == 1 && use3.Notes[0].Contains("string files", StringComparison.Ordinal) && use3.Notes[0].Contains(archived.Name, StringComparison.Ordinal),
            $"the change wasn't told: reopened {string.Join(", ", use3.Reopened)}; notes: {string.Join(" | ", use3.Notes)}");
        var p2 = index.Plugin(source.Name)!;
        Check(p2.Strings.Key != keyBefore && p2.ContentKey == p.ContentKey && p2.ReadingKey == p.ReadingKey, "the strings stamp didn't change, or the plugin's own keys did");
        var rec2 = engine.Load(engine.Find(source.Name, rec.Info.FormKey));
        string now = (rec2.Record as ITranslatedNamedGetter)?.Name?.String ?? "";
        Check(now == after, $"after the change Mutagen reads \"{now}\", the strings file holds \"{after}\"");
        Check(Card(engine, rec2).Contains(after, StringComparison.Ordinal), "the card doesn't show the new text");
        string? named = RecordNames.Of(p2, rec2.Ref.Entry, ctx.Schema, dataFolder: ctx.Data);
        Check(named == after, $"the names read from bytes give \"{named}\", not the new text");
        engine.Sessions.Confirm(use3);

        // The date alone, set back: a change like any other
        File.SetLastWriteTimeUtc(strings, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var use4 = engine.Sessions.Use(source.Name);
        Check(use4.Reopened.Count == 1 && use4.Notes[0].Contains("string files", StringComparison.Ordinal), $"a date set backwards: {string.Join(" | ", use4.Notes)}");
        engine.Sessions.Confirm(use4);

        // The loose files gone: the texts come from the archive in Data again
        Directory.Delete(loose, true);
        var use5 = engine.Sessions.Use(source.Name);
        var p3 = index.Plugin(source.Name)!;
        var rec3 = engine.Load(engine.Find(source.Name, rec.Info.FormKey));
        string back = (rec3.Record as ITranslatedNamedGetter)?.Name?.String ?? "";
        Check(use5.Reopened.Count == 1 && p3.Strings.Files.All(f => f.InArchive) && back == before, $"with the loose files gone the text reads \"{back}\" from {string.Join(", ", p3.Strings.Files.Take(2).Select(f => f.Where))}");
        int opens = engine.Sessions.Opens;

        // The stamp of an archive: its size and date. The same look at a copy of the archive in a folder of its own.
        string archiveNote = ArchiveStamp(ctx, source, archived);

        return $"a copy of {source.Name} ({source.Stamp.Length:N0} bytes) with its {taken} string files taken out of {Path.GetFileName(archived.From)} into Strings\\ beside it. {rec.Label}: name \"{before}\". " +
            $"{archived.Name} changed while the plugin was open: the running command fails (\"{during}\"); the next use says \"{use3.Notes[0]}\" and reads \"{now}\" (card and names from bytes too). " +
            $"A date set back is told; the loose files removed, the text comes from the archive again. {opens} opens in all. {archiveNote} {cost}";
    }

    static string Card(RecordEngine engine, LoadedRecord rec) => engine.Card(rec).ToText();

    /// <summary>Gives one string of a .strings file another text: the new text goes at the end and the string's entry points at it.</summary>
    static void Rewrite(string path, uint id, string text)
    {
        var bytes = File.ReadAllBytes(path);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        int dataStart = 8 + (int)count * 8;
        var add = Encoding.UTF8.GetBytes(text + "\0");
        int at = -1;
        for (int i = 0; i < count; i++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8 + i * 8)) == id) { at = 8 + i * 8 + 4; break; }
        Check(at > 0, $"string {id:X8} isn't in {Path.GetFileName(path)}");
        uint offset = (uint)(bytes.Length - dataStart);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), offset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), offset + (uint)add.Length);
        File.WriteAllBytes(path, bytes.Concat(add).ToArray());
    }

    /// <summary>
    /// The stamp of a file in an archive is the archive's size and date. Checked on a copy of the archive (when it is
    /// under 64 MB) in a folder of the tests' own, with a copy of the plugin: the archive's date set back changes the
    /// key; a loose file beside it then wins over the archive.
    /// </summary>
    static string ArchiveStamp(Ctx ctx, IndexedPlugin source, StringFile archived)
    {
        long size = new FileInfo(archived.From).Length;
        if (size > 64L << 20) return $"(the archive copy check was left out: {Path.GetFileName(archived.From)} is {size:N0} bytes)";
        string folder = ctx.Folder("strings-data");
        string plugin = Path.Combine(folder, source.Name), archive = Path.Combine(folder, Path.GetFileName(archived.From));
        File.Copy(source.Path, plugin, true); File.Copy(archived.From, archive, true);
        MustBeACopy(ctx, archive);
        var files = new StringFiles(folder);
        StringsStamp Look() => files.Stamps(new[] { (plugin, source.Name) })[0];
        var first = Look();
        Check(first.Files.Count == source.Strings.Files.Count && first.Files.All(f => f.InArchive && f.From.Equals(archive, StringComparison.OrdinalIgnoreCase) && f.Stamp.Length == size),
            $"the copy's string files should come from the archive copy: {string.Join(", ", first.Files.Take(3).Select(f => f.Where))}");
        Check(Look().SameAs(first), "two looks with nothing changed give two stamps");
        File.SetLastWriteTimeUtc(archive, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var dated = Look();
        Check(!dated.SameAs(first), "the archive's date changed and the stamp didn't");
        Directory.CreateDirectory(Path.Combine(folder, "Strings"));
        File.WriteAllBytes(Path.Combine(folder, "Strings", archived.Name), new byte[8]);
        var withLoose = Look();
        var en = withLoose.Files.First(f => f.Name == archived.Name);
        Check(!en.InArchive && !withLoose.SameAs(dated) && withLoose.Files.Count(f => f.InArchive) == first.Files.Count - 1, $"a loose {archived.Name} should win over the archive: {en.Where}");
        return $"A copy of {Path.GetFileName(archived.From)} ({size:N0} bytes): its date set back changes the stamp; a loose {archived.Name} beside it then wins over it ({StringsStamp.Changes(dated, withLoose)}).";
    }
}
