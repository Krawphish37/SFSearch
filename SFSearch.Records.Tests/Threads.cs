// The threads-equal test: records read straight from their own bytes give the same JSON, byte for byte, when eight
// threads read them at once as when one thread reads them. It holds the reads of PluginSession.ReadAt (a window and
// a file handle for each reading thread) against the plain one-thread read.
using System.Globalization;
using System.Text;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Records.Tests;

static class Threads
{
    const int Wanted = 5000, ReadingThreads = 8;
    const string Starfield = "Starfield.esm";

    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>What one read gave: the JSON as the SQL layer makes it, or the reason there is none; and the raw record.</summary>
    sealed record Outcome(byte[] Json, string Type, int Children, byte[] Raw);

    static Outcome Read(RecordEngine e, RecordRef r, PluginSession[] sessions)
    {
        // The thread's window against the file itself, at this record's position in both plugins in turn: a window
        // must know which plugin's bytes it holds
        long at = e.Index.Describe(r).Offset;
        Span<byte> throughWindow = stackalloc byte[64], throughHandle = stackalloc byte[64];
        foreach (var s in sessions)
        {
            if (at + 64 > s.FileLength) continue;
            s.ReadAt(at, throughWindow);
            RawRecord.ReadExactly(s.File, throughHandle, at);
            Ctx.Check(throughWindow.SequenceEqual(throughHandle), $"ReadAt gives other bytes than the file has at 0x{at:X} of {s.Name}");
        }
        byte[] raw;
        { var file = e.Raw(r); raw = new byte[file.Header.Length + file.Data.Length]; file.Header.CopyTo(raw, 0); file.Data.CopyTo(raw, file.Header.Length); }
        try
        {
            var rec = e.Load(r);
            Ctx.Check(rec.Route == ReadRoute.Direct, $"{rec.Label} wasn't read from its own bytes");
            string json = e.Schema.HoldsRecords(rec.Type) ? e.Json(rec).ToJsonString() : RecordSerializer.ToJson(rec.Record);
            return new Outcome(Encoding.UTF8.GetBytes(json), rec.Type, rec.Children, raw);
        }
        catch (RecordException x) { return new Outcome(Encoding.UTF8.GetBytes("RecordException: " + x.Message), "", 0, raw); }
    }

    /// <summary>Records of a plugin that are read straight from their own bytes, the same number of each signature
    /// as far as each has them, spread over the file.</summary>
    static List<RecordRef> Pick(RecordEngine e, HeaderIndex index, string plugin, int want, List<string> sigs)
    {
        var direct = new List<(string Sig, List<RecordRef> All)>();
        foreach (var s in index.Stats(plugin))
        {
            if (s.Count == 0 || e.Schema.TypesOf(s.Signature).Count == 0) continue;
            var all = index.RecordsOfType(plugin, s.Signature).ToList();
            if (e.IsDirect(index.Describe(all[0]))) direct.Add((s.Signature, all));
        }
        var pick = new List<RecordRef>(want);
        int left = direct.Count;
        foreach (var (sig, all) in direct.OrderBy(d => d.All.Count))
        {
            int quota = (want - pick.Count) / left--;
            int before = pick.Count;
            int step = Math.Max(1, all.Count / Math.Max(1, quota));
            for (int i = 0; i < all.Count && pick.Count - before < quota; i += step)
            {
                var r = all[i];
                if (e.IsDirect(index.Describe(r)) && e.HeavyNote(r) == null) pick.Add(r);
            }
            if (pick.Count > before) sigs.Add($"{sig} {N(pick.Count - before)}");
        }
        return pick;
    }

    /// <summary>
    /// The JSON writer a thread keeps from one record to the next (RecordSerializer.ToUtf8) against a new writer
    /// for every record, the way the serializer package itself starts and ends an object: the same bytes, for
    /// records of every type one after the other on one writer.
    /// </summary>
    public static string SameWriter(Ctx ctx)
    {
        var index = ctx.DataIndex; var e = ctx.Engine;
        e.AllowHeavy = false;
        const int each = 100;
        long records = 0, bytes = 0; int types = 0, plugins = 0;
        var differ = new List<string>();
        foreach (string plugin in new[] { Starfield, "ShatteredSpace.esm", "kgcdoom.esm", "darkstar.esm" })
        {
            if (index.Plugin(plugin)?.Segment == null) continue;
            plugins++;
            e.Sessions.Use(plugin);
            // One record of each type in turn, then the next of each: the kept writer changes type at every record
            var lists = new List<List<RecordRef>>();
            foreach (var s in index.Stats(plugin))
            {
                if (s.Count == 0 || e.Schema.TypesOf(s.Signature).Count == 0) continue;
                var all = index.RecordsOfType(plugin, s.Signature).ToList();
                var pick = new List<RecordRef>(each);
                for (int i = 0; i < each && i < all.Count; i++) pick.Add(all[(int)((long)i * all.Count / Math.Min(each, all.Count))]);
                lists.Add(pick);
            }
            if (plugin == Starfield) types = lists.Count;
            for (int round = 0; round < each; round++)
                foreach (var list in lists)
                {
                    if (round >= list.Count) continue;
                    var r = list[round];
                    if (e.HeavyNote(r) != null) continue;
                    LoadedRecord rec;
                    try { rec = e.Load(r); } catch (RecordException) { continue; }
                    byte[] kept = RecordSerializer.ToUtf8(rec.Record).ToArray();
                    byte[] made = ToUtf8WithNewWriter(rec.Record);
                    string text = RecordSerializer.ToJson(rec.Record);
                    records++; bytes += made.Length;
                    if (!kept.AsSpan().SequenceEqual(made) || !Encoding.UTF8.GetBytes(text).AsSpan().SequenceEqual(made))
                        if (differ.Count < 20) differ.Add($"{rec.Label} {rec.Info.Signature} of {plugin}: {kept.Length:N0} bytes with the kept writer, {made.Length:N0} with a new one");
                }
        }
        ctx.WriteReport("json-writer-differences.txt", differ);
        Ctx.Check(differ.Count == 0, $"{N(differ.Count)} records give other JSON through the kept writer: {string.Join(" || ", differ.Take(6))}");
        Ctx.Check(records > 10_000, $"only {N(records)} records were compared");
        return $"{N(records)} records of {plugins} plugins (up to {each} of each of the {types} record types of {Starfield}, and of three mods), one type after the other: the JSON through the writer a thread keeps ({bytes / 1048576.0:F1} MB) is byte-equal to the JSON through a new writer for every record, and to the text ToJson gives";
    }

    public static string Equal(Ctx ctx)
    {
        var index = ctx.DataIndex;
        using var e = new RecordEngine(index, ctx.Schema, Path.Combine(ctx.Work, "verify-threads"), ctx.Data);
        // Two plugins, taken in turn: a reading thread's window changes plugin from one read to the next
        string second = new[] { "ShatteredSpace.esm", "kgcdoom.esm" }.FirstOrDefault(n => index.Plugin(n)?.Segment != null)
            ?? throw new TestFailed("neither ShatteredSpace.esm nor kgcdoom.esm is in the Data folder");
        e.Sessions.Use(Starfield, second);
        var sigsA = new List<string>(); var sigsB = new List<string>();
        var a = Pick(e, index, Starfield, Wanted * 4 / 5, sigsA);
        var b = Pick(e, index, second, Wanted - a.Count, sigsB);
        var picks = new List<RecordRef>(Wanted);
        for (int i = 0, ai = 0, bi = 0; ai < a.Count || bi < b.Count; i++)
            picks.Add(bi < b.Count && (i % 5 == 4 || ai >= a.Count) ? b[bi++] : a[ai++]);
        Ctx.Check(picks.Count == Wanted, $"{N(picks.Count)} records to read, not {N(Wanted)}: {N(a.Count)} of {Starfield}, {N(b.Count)} of {second}");

        var one = new Outcome[picks.Count];
        var sessions = new[] { e.Sessions.Get(Starfield), e.Sessions.Get(second) };
        for (int i = 0; i < picks.Count; i++) one[i] = Read(e, picks[i], sessions);
        int failed = one.Count(o => o.Type.Length == 0), holders = one.Count(o => o.Children > 0);
        long jsonBytes = one.Sum(o => (long)o.Json.Length);
        if (failed >= picks.Count / 100) throw new TestFailed($"{N(failed)} of {N(picks.Count)} records couldn't be read at all: {Encoding.UTF8.GetString(one.First(o => o.Type.Length == 0).Json)}");

        // Eight threads at once, each taking the next record: neighbours in the file are read by different threads
        // at the same moment. Twice: in the same order, then backwards.
        var differ = new List<string>();
        foreach (bool backwards in new[] { false, true })
        {
            var eight = new Outcome[picks.Count];
            int next = -1; Exception? failure = null;
            var threads = Enumerable.Range(0, ReadingThreads).Select(_ => new Thread(() =>
            {
                try
                {
                    for (int i; (i = Interlocked.Increment(ref next)) < picks.Count;)
                    {
                        int at = backwards ? picks.Count - 1 - i : i;
                        eight[at] = Read(e, picks[at], sessions);
                    }
                }
                catch (Exception x) { Interlocked.CompareExchange(ref failure, x, null); }
            }) { IsBackground = true }).ToList();
            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();
            if (failure != null) throw new TestFailed($"a read on {ReadingThreads} threads failed: {failure.GetType().Name}: {failure.Message}");
            for (int i = 0; i < picks.Count; i++)
            {
                var x = one[i]; var y = eight[i];
                string? what = !x.Raw.AsSpan().SequenceEqual(y.Raw) ? "the raw bytes" : !x.Json.AsSpan().SequenceEqual(y.Json) ? "the JSON" : x.Type != y.Type ? "the type" : x.Children != y.Children ? "the number of children" : null;
                if (what != null && differ.Count < 20) differ.Add($"{index.Describe(picks[i]).Label} of {index.Describe(picks[i]).Plugin}: {what}{(backwards ? " (read backwards)" : "")}");
            }
        }
        ctx.WriteReport("threads-equal-differences.txt", differ);
        Ctx.Check(differ.Count == 0, $"{N(differ.Count)} records differ between one thread and {ReadingThreads}: {string.Join(" || ", differ.Take(6))}");
        string deleted = DeletedWhileOpen(ctx);
        int handles = e.Sessions.Get(Starfield).ExtraHandles + e.Sessions.Get(second).ExtraHandles;
        Ctx.Check(e.Sessions.Get(Starfield).ExtraHandles <= ReadingThreads && e.Sessions.Get(second).ExtraHandles <= ReadingThreads, $"the sessions opened {N(handles)} more file handles for {ReadingThreads} reading threads");
        return $"{N(picks.Count)} records read straight from their own bytes, {N(a.Count)} of {Starfield} ({string.Join(", ", sigsA)}) and {N(b.Count)} of {second} ({string.Join(", ", sigsB)}) in turn, {N(holders)} of them with their children as stubs, {N(failed)} that Mutagen can't read: "
            + $"the JSON of every one ({jsonBytes / 1048576.0:F1} MB) and its raw bytes are byte-equal on one thread and on {ReadingThreads} threads at once, in file order and backwards, and each thread's window gave the file's own bytes at every one of those positions in both plugins; the two sessions opened {N(handles)} file handles beyond their first for it. {deleted}";
    }

    /// <summary>A plugin deleted while a session holds it open: Windows won't open the file again, so every reading
    /// thread reads through the session's first handle, and gets the bytes the file had.</summary>
    static string DeletedWhileOpen(Ctx ctx)
    {
        var data = ctx.DataIndex;
        var source = data.Plugin("weap_starlight.esm") ?? throw new TestFailed("weap_starlight.esm isn't in the Data folder");
        string copy = Path.Combine(ctx.Folder("deleted"), source.Name);
        File.Copy(source.Path, copy, overwrite: true);
        Ctx.Check(Path.GetFullPath(copy).StartsWith(ctx.Work + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "the copy isn't inside the tests' work folder");
        byte[] before = File.ReadAllBytes(copy);
        var files = new List<string> { copy };
        files.AddRange(source.Header!.Masters.Select(m => data.Plugin(m)!.Path));
        using var index = new HeaderIndex(PluginSource.FromFiles(files), ctx.Options());
        index.Refresh();
        using var engine = new RecordEngine(index, ctx.Schema, Path.Combine(ctx.Work, "verify-deleted"), ctx.Data);
        engine.Sessions.Use(source.Name);
        var session = engine.Sessions.Get(source.Name);
        File.Delete(copy);
        Ctx.Check(!File.Exists(copy), "the copy is still there after it was deleted");
        int wrong = 0; Exception? failure = null;
        var threads = Enumerable.Range(0, ReadingThreads).Select(t => new Thread(() =>
        {
            try
            {
                var got = new byte[97];
                for (int at = t * 13; at + got.Length <= before.Length; at += 1009)
                {
                    session.ReadAt(at, got);
                    if (!got.AsSpan().SequenceEqual(before.AsSpan(at, got.Length))) Interlocked.Increment(ref wrong);
                }
            }
            catch (Exception x) { Interlocked.CompareExchange(ref failure, x, null); }
        }) { IsBackground = true }).ToList();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
        if (failure != null) throw new TestFailed($"reading a plugin that was deleted while open failed: {failure.GetType().Name}: {failure.Message}");
        Ctx.Check(wrong == 0, $"{wrong} reads of a plugin that was deleted while open gave other bytes than it had");
        string how = session.ExtraHandles == 0 ? "Windows wouldn't open the deleted file again, so they read through the session's first handle" : $"through {N(session.ExtraHandles)} more handles on the same file";
        return $"A copy of {source.Name} deleted while a session held it open was still read by {ReadingThreads} threads, with the bytes it had ({how})";
    }

    /// <summary>The same JSON through a new writer, the way the serializer package itself starts and ends an object
    /// (its JSON kernel's GetNewObject and Finalize around the registry's serializer of the object's class): the test
    /// holds RecordSerializer.ToUtf8, the thread's kept converter (C59 V4), against it.</summary>
    static byte[] ToUtf8WithNewWriter(object o)
    {
        using var ms = new MemoryStream();
        var pkg = new Mutagen.Bethesda.Serialization.Streams.StreamPackage(ms, null);
        var kernel = new Mutagen.Bethesda.Serialization.Newtonsoft.NewtonsoftJsonSerializationWriterKernel();
        var w = kernel.GetNewObject(pkg);
        RecordSerializer.Registry.Serialize(w, o, Mutagen.Bethesda.Serialization.MutagenSerializationWriterKernel<Mutagen.Bethesda.Serialization.Newtonsoft.NewtonsoftJsonSerializationWriterKernel, Mutagen.Bethesda.Serialization.Newtonsoft.JsonWritingUnit>.Instance,
            new Mutagen.Bethesda.Serialization.SerializationMetaData(Mutagen.Bethesda.GameRelease.Starfield, null!, null!, null!, CancellationToken.None)).GetAwaiter().GetResult();
        kernel.Finalize(pkg, w);
        return ms.ToArray();
    }
}
