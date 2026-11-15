using System.Diagnostics;
using SFSearch.Index;

namespace SFSearch.Index.Tests;

/// <summary>
/// S43 (GAPS.md): a refresh while plugins are written, replaced and deleted in the folder it indexes. Only copies in the
/// tests' work folder are written; the Data folder is only read, to take the copies from.
/// </summary>
static class RaceTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);

    /// <summary>Plugins the tests never copy or change, by the user's rule.</summary>
    static bool HandsOff(string name) => name.StartsWith("darkstarpatch_", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(name, @"^sfta0[1-6]\.esm$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static void MustBeACopy(Ctx ctx, string path)
    {
        if (!Path.GetFullPath(path).StartsWith(ctx.Work + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"refusing to write {path}: it isn't inside the tests' work folder");
    }

    /// <summary>Plugins of Data between these sizes, with a header, at least one master, and the most masters first
    /// (a header that names masters is what the S43 crash needed). Read only: nothing in Data is opened for writing.</summary>
    static List<(string Path, PluginHeader Header)> Sources(Ctx ctx, long min, long max, int count)
    {
        var found = new List<(string, PluginHeader)>();
        foreach (var f in Directory.EnumerateFiles(ctx.Data).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(f);
            if (HandsOff(name) || !(name.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))) continue;
            long len = new FileInfo(f).Length;
            if (len < min || len > max) continue;
            PluginHeader? h;
            try { h = PluginHeader.Read(f); } catch (IOException) { continue; }
            if (h is { Masters.Count: > 0 }) found.Add((f, h));
        }
        Check(found.Count >= count, $"only {found.Count} plugins of Data are between {min:N0} and {max:N0} bytes with a master; the test needs {count}");
        // Different master counts and sizes: spread the picks over the list sorted by master count
        var sorted = found.OrderByDescending(x => x.Item2.Masters.Count).ThenBy(x => new FileInfo(x.Item1).Length).ToList();
        return Enumerable.Range(0, count).Select(i => sorted[i * (sorted.Count - 1) / Math.Max(1, count - 1)]).Distinct().ToList();
    }

    static IndexOptions Options(Ctx ctx, string version) => new() { ScratchFolder = ctx.Scratch, VersionText = Ctx.Version + "/" + version, PluginsTxt = "", Ccc = "" };

    /// <summary>
    /// The crash of S43, made to happen: a plugin that has no header yet when the refresh looks at it (an empty file, as
    /// a patcher's output is when it starts writing), and is written in full while it is read. The refresh reads it again,
    /// with the masters its new header names. Before the fix, the masters' sizes were kept from the first look (none),
    /// and the reading key indexed past their end: ArgumentOutOfRangeException out of Parallel.ForEach.
    /// </summary>
    public static string LateHeader(Ctx ctx)
    {
        var (source, header) = Sources(ctx, 50_000, 2_000_000, 1)[0];
        string folder = ctx.Folder("late");
        string late = Path.Combine(folder, "late.esm");
        MustBeACopy(ctx, late);
        File.WriteAllBytes(late, Array.Empty<byte>());
        int calls = 0;
        using var index = new HeaderIndex(PluginSource.Folder(folder), Options(ctx, "late"));
        index.AfterReadForTests = name =>
        {
            if (name == "late.esm" && Interlocked.Increment(ref calls) == 1)
            {
                File.Copy(source, late, overwrite: true);
                File.SetLastWriteTimeUtc(late, DateTime.UtcNow);
            }
        };
        RefreshReport report;
        try { report = index.Refresh(); }
        catch (Exception e) { throw new TestFailed($"the refresh crashed: {e.GetType().Name}: {e.Message} (inner: {e.InnerException?.GetType().Name}: {e.InnerException?.Message})"); }
        var p = index.Plugin("late.esm");
        Check(p != null, "late.esm isn't in the index");
        Check(calls == 2, $"late.esm was read {calls} time(s); written during the first read, it should be read twice");
        Check(p!.Segment != null && p.Problems.ReadError == null, $"late.esm, written in full during the first read, should be indexed; it says: {p.Problems.ReadError}");
        Check(p.Header != null && p.Header.Masters.Count == header.Masters.Count && p.MasterStyles.Count == header.Masters.Count, $"late.esm has {p.Header?.Masters.Count} masters and {p.MasterStyles.Count} master sizes; its header names {header.Masters.Count}");
        Check(p.ContentKey == Keys.Content(late), "what is indexed isn't the file as it is now");
        Check(report.Added.SequenceEqual(new[] { "late.esm" }), $"added: {string.Join(", ", report.Added)}; expected late.esm");
        return $"late.esm, empty when the refresh looked at it, then written as a copy of {Path.GetFileName(source)} ({header.Masters.Count} masters) during its read: read again, no crash, indexed with {p.Segment!.Count:N0} records and {p.MasterStyles.Count} master sizes";
    }

    /// <summary>
    /// Refreshes in a loop for ~30 s while another thread adds, replaces (whole copies, piece-by-piece writes, a
    /// temporary file moved over) and deletes plugin copies in the folder. No refresh may throw, each must agree with
    /// itself and with the one before (added = new names, removed = names gone, a segment or a reason for every plugin,
    /// a known content read as the right number of records), and once the writing stops one refresh must index every
    /// file exactly.
    /// </summary>
    public static string FolderRace(Ctx ctx, int seconds)
    {
        var sources = Sources(ctx, 40_000, 6_000_000, 6);
        // The sources are copied once into the work folder; the race copies those copies
        string srcFolder = ctx.Folder("race-src"), folder = ctx.Folder("race");
        var src = new List<string>();
        foreach (var (path, _) in sources) { string c = Path.Combine(srcFolder, Path.GetFileName(path)); File.Copy(path, c, true); src.Add(c); }
        var keyOf = src.ToDictionary(s => s, s => Keys.Content(s));
        // The number of records of each content, from an index of the sources themselves
        var countOf = new Dictionary<Key128, int>();
        using (var refIx = new HeaderIndex(PluginSource.FromFiles(src), Options(ctx, "race-src")))
        {
            refIx.Refresh();
            foreach (var p in refIx.Plugins) { Check(p.Segment != null, $"{p.Name} (the source copy) has no segment: {p.Problems.ReadError}"); countOf[p.ContentKey] = p.Segment!.Count; }
        }
        string Name(int i) => $"race_{i:00}.esm";
        const int Names = 10;
        for (int i = 0; i < 4; i++) File.Copy(src[i % src.Count], Path.Combine(folder, Name(i)), true);

        // The writer: one change at a time, at random (seeded, so a failing run can be repeated)
        var stop = new CancellationTokenSource();
        var ops = new Dictionary<string, int>(); int writerErrors = 0;
        void Count(string op) { lock (ops) ops[op] = ops.GetValueOrDefault(op) + 1; }
        var writer = new Thread(() =>
        {
            var rnd = new Random(43);
            var buf = new byte[256 * 1024];
            while (!stop.IsCancellationRequested)
            {
                string target = Path.Combine(folder, Name(rnd.Next(Names)));
                string from = src[rnd.Next(src.Count)];
                MustBeACopy(ctx, target);
                try
                {
                    switch (rnd.Next(5))
                    {
                        case 0: File.Copy(from, target, overwrite: true); Count("copied"); break;
                        case 1:
                            // Written piece by piece, as a patcher writes its output: empty first, then growing
                            using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read))
                            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                            {
                                int n;
                                while ((n = input.Read(buf)) > 0) { output.Write(buf, 0, n); output.Flush(); Thread.Sleep(rnd.Next(3)); }
                            }
                            Count("written in pieces"); break;
                        case 2:
                            string tmp = Path.Combine(folder, "race.tmp");
                            File.Copy(from, tmp, overwrite: true);
                            File.Move(tmp, target, overwrite: true);
                            Count("moved over"); break;
                        case 3:
                            if (File.Exists(target)) { File.Delete(target); Count("deleted"); }
                            break;
                        default:
                            // Cut short, left like that for a moment, then put back whole
                            var bytes = File.ReadAllBytes(from);
                            File.WriteAllBytes(target, bytes.AsSpan(0, bytes.Length * rnd.Next(1, 10) / 10).ToArray());
                            Thread.Sleep(rnd.Next(20));
                            File.WriteAllBytes(target, bytes);
                            Count("cut, then whole"); break;
                    }
                }
                // A file the index has mapped can't be cut or replaced, a file being deleted can't be opened: try another change
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Interlocked.Increment(ref writerErrors); }
                Thread.Sleep(rnd.Next(5));
            }
        }) { IsBackground = true, Name = "race writer" };

        using var index = new HeaderIndex(PluginSource.Folder(folder), Options(ctx, "race"));
        index.Refresh();
        var before = index.Plugins.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int refreshes = 0, withSegment = 0, knownContent = 0;
        var reasons = new Dictionary<string, int>();
        var clock = Stopwatch.StartNew();
        writer.Start();
        try
        {
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                RefreshReport r;
                try { r = index.Refresh(); }
                catch (Exception e)
                {
                    var root = e; while (root.InnerException != null) root = root.InnerException;
                    throw new TestFailed($"refresh {refreshes + 1} crashed: {e.GetType().Name}: {e.Message}; thrown at: {root.GetType().Name}: {root.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
                }
                refreshes++;
                var now = index.Plugins.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                string at = $"refresh {refreshes}";
                Check(r.Added.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(now.Except(before, StringComparer.OrdinalIgnoreCase)), $"{at}: added {string.Join(", ", r.Added)}; the plugins new to it are {string.Join(", ", now.Except(before, StringComparer.OrdinalIgnoreCase))}");
                Check(r.Removed.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(before.Except(now, StringComparer.OrdinalIgnoreCase)), $"{at}: removed {string.Join(", ", r.Removed)}; the plugins gone are {string.Join(", ", before.Except(now, StringComparer.OrdinalIgnoreCase))}");
                Check(now.All(n => n.StartsWith("race_", StringComparison.OrdinalIgnoreCase)), $"{at}: a file that isn't a plugin is indexed: {string.Join(", ", now)}");
                for (int i = 0; i < index.Plugins.Count; i++)
                {
                    var p = index.Plugins[i];
                    Check(p.Id == i && ReferenceEquals(index.Plugin(p.Name), p), $"{at}: {p.Name} has Id {p.Id} at place {i}, or isn't found by its name");
                    Check((p.Segment == null) == (p.Problems.ReadError != null), $"{at}: {p.Name} has {(p.Segment == null ? "no segment and no reason" : "a segment and a read error: " + p.Problems.ReadError)}");
                    if (p.Header != null) Check(p.MasterStyles.Count == p.Header.Masters.Count, $"{at}: {p.Name} has {p.MasterStyles.Count} master sizes for {p.Header.Masters.Count} masters");
                    if (p.Segment != null)
                    {
                        withSegment++;
                        Check(p.Segment.ReadingKey == p.ReadingKey, $"{at}: {p.Name}'s segment isn't the one of its reading key");
                        if (countOf.TryGetValue(p.ContentKey, out int n)) { knownContent++; Check(p.Segment.Count == n, $"{at}: {p.Name} holds a known content read as {p.Segment.Count:N0} records; it has {n:N0}"); }
                    }
                    else
                    {
                        string reason = p.Problems.ReadError!.StartsWith("it changed while", StringComparison.Ordinal) ? "changed while read"
                            : p.Problems.ReadError.StartsWith("it doesn't start with", StringComparison.Ordinal) ? "no header yet"
                            : p.Problems.ReadError.StartsWith("it couldn't be read (", StringComparison.Ordinal) ? "couldn't be read: " + p.Problems.ReadError["it couldn't be read (".Length..].Split(':')[0]
                            : "cut short or broken";
                        reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                    }
                }
                before = now;
            }
        }
        finally
        {
            stop.Cancel();
            writer.Join();
        }

        // Writing has stopped. A file left cut short by a failed change is taken away, then one refresh must index every
        // file exactly as it is.
        foreach (var f in Directory.EnumerateFiles(folder).ToList())
        {
            MustBeACopy(ctx, f);
            if (!f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || !keyOf.ContainsValue(Keys.Content(f))) File.Delete(f);
        }
        var last = index.Refresh();
        var files = Directory.EnumerateFiles(folder).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = index.Plugins.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check(names.SetEquals(files!), $"after the writing stopped the index lists {string.Join(", ", names.Order())}; the folder holds {string.Join(", ", files.Order())}");
        foreach (var p in index.Plugins)
        {
            Check(p.Segment != null && p.Problems.ReadError == null, $"after the writing stopped {p.Name} has no segment: {p.Problems.ReadError}");
            Check(p.ContentKey == Keys.Content(p.Path), $"after the writing stopped {p.Name} is indexed with a content key that isn't its file's");
            Check(p.Segment!.Count == countOf[p.ContentKey], $"after the writing stopped {p.Name} has {p.Segment.Count:N0} records; its content has {countOf[p.ContentKey]:N0}");
        }
        var again = index.Refresh();
        Check(!again.AnythingChanged && again.PassesRun.Count == 0 && again.Hashed.Count == 0, "a refresh with nothing changed read or hashed a plugin");
        Check(refreshes >= 50, $"only {refreshes} refreshes ran in {seconds} s; the test wants at least 50");
        Check(ops.Values.Sum() >= 100, $"only {ops.Values.Sum()} changes were made in {seconds} s; the test wants at least 100");
        return $"{refreshes} refreshes in {clock.Elapsed.TotalSeconds:F0} s while {ops.Values.Sum()} changes were made to {Names} names from {src.Count} copies of Data plugins " +
               $"({string.Join(", ", ops.OrderBy(o => o.Key).Select(o => $"{o.Value} {o.Key}"))}; {writerErrors} refused, as a file being read or deleted is): no crash, every refresh consistent. " +
               $"{withSegment:N0} plugin readings with a segment ({knownContent:N0} of a known content, each with its right count); without one: {(reasons.Count == 0 ? "none" : string.Join(", ", reasons.OrderBy(o => o.Key).Select(o => $"{o.Value} {o.Key}")))}. " +
               $"Once the writing stopped, one refresh indexed all {index.Plugins.Count} files exactly ({last.PassesRun.Count} pass(es)), and the next had nothing to do";
    }
}
