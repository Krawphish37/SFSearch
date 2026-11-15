// sfsindex: a small command line over SFSearch.Index, for measuring and for looking at what the header index holds.
//
//   sfsindex index <Data folder> [options]        index every plugin; print the time, the record count and the segment sizes
//   sfsindex loadorder <Data folder> [options]    the load order: position, slot, plugin, size, loaded, notes
//   sfsindex problems <Data folder> [options]     what the pass found wrong, plugin by plugin
//   sfsindex find <Data folder> <text> [options]  a FormKey (0476C4:Starfield.esm), a bare local ID (000CF2) or an EditorID
//   sfsindex stats <Data folder> <plugin>         records and bytes per record type
//   sfsindex bench <Data folder> [options]        lookup timings
//   sfsindex map <segment file> [--hold <ms>]     map one segment read only, print its checksum, keep it mapped for a while
//
// Options: --scratch <folder> (default %TEMP%\sfsindex-scratch)   --threads <n>   --plugins-txt <path>   --version <text>
//          --fresh (delete the scratch folder's segments first)   --top <n>   --all   --verify (index: also time a full content check)
// Read only: nothing is ever written to the Data folder.
using System.Diagnostics;
using SFSearch.Index;

if (args.Length < 2) { Console.Error.WriteLine("usage: sfsindex index|loadorder|problems|find|stats|bench <Data folder> … | map <segment file>   (see the top of Program.cs)"); return 2; }
var rest = args.Skip(2).ToList();
string? Opt(string name) { int i = rest.IndexOf(name); if (i < 0 || i + 1 >= rest.Count) return null; string v = rest[i + 1]; rest.RemoveRange(i, 2); return v; }
bool Flag(string name) => rest.Remove(name);

if (args[0] == "map")
{
    using var seg = Segment.TryOpen(args[1]);
    if (seg == null) { Console.Error.WriteLine($"Error: {args[1]} isn't a segment. Fix: give the path of a .seg file in the scratch folder."); return 1; }
    Console.WriteLine($"mapped {seg.Plugin} {seg.Count} {seg.Checksum():x16}");
    Console.Out.Flush();
    if (Opt("--hold") is string hold) Thread.Sleep(int.Parse(hold));
    Console.WriteLine($"still {seg.Plugin} {seg.Count} {seg.Checksum():x16}");
    return 0;
}

string data = args[1];
string scratch = Opt("--scratch") ?? Path.Combine(Path.GetTempPath(), "sfsindex-scratch");
int threads = int.Parse(Opt("--threads") ?? Math.Min(Environment.ProcessorCount, 8).ToString());
string? pluginsTxt = Opt("--plugins-txt");
string version = Opt("--version") ?? "sfsindex-runner";
int top = int.Parse(Opt("--top") ?? "12");
bool all = Flag("--all");
bool verify = Flag("--verify");
if (Flag("--fresh") && Directory.Exists(scratch))
    foreach (var f in Directory.EnumerateFiles(scratch).Where(f => f.EndsWith(".seg") || f.EndsWith(".tmp") || Path.GetFileName(f) == "stamps.tsv")) File.Delete(f);

var total = Stopwatch.StartNew();
using var index = new HeaderIndex(PluginSource.Folder(data), new IndexOptions { ScratchFolder = scratch, Threads = threads, PluginsTxt = pluginsTxt, VersionText = version });
var report = index.Refresh();
string Mb(long bytes) => (bytes / 1048576.0).ToString("N1") + " MB";

switch (args[0])
{
    case "index":
    {
        long edids = index.Plugins.Sum(p => (long)(p.Segment?.EditorIdCount ?? 0));
        long pluginBytes = index.Plugins.Sum(p => p.Stamp.Length);
        Console.WriteLine($"indexed {index.Plugins.Count} plugins ({pluginBytes / 1073741824.0:F2} GiB) in {report.Seconds:F2} s on {threads} threads");
        Console.WriteLine($"  records        {index.TotalRecords:N0}");
        Console.WriteLine($"  EditorIDs      {edids:N0}");
        Console.WriteLine($"  segments       {Mb(index.TotalSegmentBytes)} in {index.Plugins.Count(p => p.Segment != null)} files ({index.TotalSegmentBytes / (double)Math.Max(1, index.TotalRecords):F1} bytes a record)");
        Console.WriteLine($"  header passes  {report.PassesRun.Count}, content hashes {report.Hashed.Count}, segments already in scratch {report.SegmentsReused.Count}");
        Console.WriteLine($"  override map   {index.OverriddenRecords:N0} records held by more than their owner, {index.OverrideEntries:N0} overrides; load order and map {report.MapSeconds * 1000:F0} ms");
        Console.WriteLine($"  load order     {index.LoadOrderEntries.Count(e => e.Enabled)} loaded, {index.LoadOrderEntries.Count(e => e.InData)} in Data");
        Console.WriteLine($"  problems       {index.Plugins.Count(p => p.Problems.Any)} plugins (run 'problems' for the list)");
        Console.WriteLine($"biggest segments:");
        foreach (var p in index.Plugins.Where(p => p.Segment != null).OrderByDescending(p => p.Segment!.FileBytes).Take(top))
            Console.WriteLine($"  {p.Name,-44} {p.Segment!.Count,10:N0} records  {p.Segment.EditorIdCount,9:N0} EditorIDs  {Mb(p.Segment.FileBytes),10}  plugin {Mb(p.Stamp.Length),10}");
        if (index.Plugins.Any(p => p.Timings != null))
        {
            Console.WriteLine("slowest plugins (seconds: header pass = cut and read + sort and merge; content hash; segment write):");
            foreach (var p in index.Plugins.Where(p => p.Timings != null).OrderByDescending(p => p.Timings!.Pass + p.Timings.Write).Take(5))
                Console.WriteLine($"  {p.Name,-44} pass {p.Timings!.Pass:F2} = {p.Timings.Scan:F2} + {p.Timings.Merge:F2} in {p.Timings.Pieces} pieces; hash {p.Timings.Hash:F2}; write {p.Timings.Write:F2}");
        }
        var again = Stopwatch.StartNew();
        var second = index.Refresh();
        Console.WriteLine($"a second refresh with nothing changed: {again.Elapsed.TotalMilliseconds:F0} ms ({second.PassesRun.Count} passes, {second.Hashed.Count} hashes)");
        again.Restart();
        index.RefreshLoadOrder();
        Console.WriteLine($"load order and winners alone: {again.Elapsed.TotalMilliseconds:F0} ms");
        if (verify)
        {
            again.Restart();
            var full = index.Refresh(verifyContent: true);
            Console.WriteLine($"a refresh that hashes every file again (verifyContent): {again.Elapsed.TotalSeconds:F2} s ({full.Hashed.Count} hashes, {full.PassesRun.Count} passes, {full.ContentChanged.Count} changed)");
        }
        break;
    }
    case "loadorder":
    {
        Console.WriteLine("Pos\tSlot\tPlugin\tSize\tLoaded\tNotes");
        var byName = index.Plugins.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var e in index.LoadOrderEntries)
        {
            if (!all && !e.Enabled) continue;
            var notes = new List<string>();
            if (!e.InData) notes.Add("listed in plugins.txt but not in Data");
            if (byName.TryGetValue(e.Name, out var p)) notes.AddRange(p.Problems.Lines());
            Console.WriteLine($"{(e.Position < 0 ? "-" : e.Position.ToString())}\t{e.Slot}\t{e.Name}\t{(e.InData ? e.Size.ToString() : "")}\t{(e.Enabled ? "yes" : "no")}\t{string.Join("; ", notes)}");
        }
        Console.WriteLine($"{index.LoadOrderEntries.Count(e => e.Enabled)} plugin(s) loaded, {index.LoadOrderEntries.Count(e => e.InData)} in Data.");
        foreach (var bad in index.BadLoadOrderLines) Console.WriteLine($"[notes] a line of plugins.txt names no plugin: {bad}");
        break;
    }
    case "problems":
    {
        foreach (var p in index.Plugins.Where(p => p.Problems.Any))
        {
            Console.WriteLine(p.Name);
            foreach (var line in p.Problems.Lines()) Console.WriteLine("    " + line);
            foreach (int e in p.Problems.TypeMismatches.Take(all ? int.MaxValue : 3))
            {
                var r = index.Describe(new RecordRef(p.Id, e));
                var v = index.Versions(r.FormKey);
                var b = v == null ? null : index.Describe(v.Holders[0]);
                Console.WriteLine($"        {r.Signature} {r.Label} overrides {b?.Signature} {b?.Label}");
            }
            if (p.Problems.Duplicates > 0 && p.Segment is { } seg)
                for (int i = 1; i < seg.Count; i++)
                    if (seg[i].Key == seg[i - 1].Key)
                    {
                        var a = index.Describe(new RecordRef(p.Id, i - 1)); var b = index.Describe(new RecordRef(p.Id, i));
                        Console.WriteLine($"        {a.FormKey} is held twice: {a.Signature} {a.EditorId} at 0x{a.Offset:X} and {b.Signature} {b.EditorId} at 0x{b.Offset:X}");
                    }
            foreach (int e in p.Problems.OverridesOfNothing.Take(all ? int.MaxValue : 3))
                Console.WriteLine($"        {index.Describe(new RecordRef(p.Id, e)).Signature} {index.Describe(new RecordRef(p.Id, e)).Label} overrides nothing");
        }
        Console.WriteLine($"{index.Plugins.Count(p => p.Problems.Any)} of {index.Plugins.Count} plugins have something to say.");
        break;
    }
    case "find":
    {
        if (rest.Count == 0) { Console.Error.WriteLine("Error: nothing to find. Fix: give a FormKey, a local ID or an EditorID."); return 2; }
        string text = rest[0];
        void Print(RecordRef r)
        {
            var d = index.Describe(r);
            var v = index.Versions(d.FormKey);
            string held = v == null ? "" : string.Join(" > ", Enumerable.Range(0, v.Holders.Count).Reverse().Select(i => index.Plugins[v.Holders[i].Plugin].Name + (i == v.Winner ? " (wins)" : index.Plugins[v.Holders[i].Plugin].Order.Enabled ? "" : " (not loaded)")));
            Console.WriteLine($"{d.Label}\t{d.Signature}\tin {d.Plugin} at 0x{d.Offset:X}, {d.Size:N0} bytes{(d.Parent != null ? $", parent {d.Parent}" : "")}\theld by: {held}");
        }
        if (HeaderIndex.TryParseFormKey(text, out var plugin, out var id))
        {
            var v = index.Versions(plugin, id);
            if (v == null) Console.WriteLine($"No plugin holds {id:X6}:{plugin}{(index.Plugin(plugin) == null ? $" ({plugin} isn't in Data)" : "")}.");
            else Print(v.Winner >= 0 ? v.Holders[v.Winner] : v.Holders[0]);
        }
        else
        {
            var hits = index.FindEditorId(text);
            if (hits.Count == 0 && text.Length <= 6 && uint.TryParse(text, System.Globalization.NumberStyles.AllowHexSpecifier, null, out uint local)) hits = index.FindLocalId(local);
            if (hits.Count == 0) Console.WriteLine($"Nothing has the EditorID or local ID \"{text}\" ({index.Plugins.Count} plugins searched).");
            foreach (var h in hits) Print(h);
        }
        break;
    }
    case "stats":
    {
        if (rest.Count == 0 || index.Plugin(rest[0]) is not { Segment: not null } p) { Console.Error.WriteLine("Error: that plugin isn't indexed. Fix: name a plugin in the Data folder."); return 1; }
        Console.WriteLine($"{p.Name}: {p.Segment.Count:N0} records in {p.Segment.Groups:N0} groups, reading key {p.ReadingKey}, content key {p.ContentKey}");
        Console.WriteLine("Type\tRecords\tBytes\tCompressed");
        foreach (var s in p.Segment.Signatures.OrderByDescending(s => s.Bytes)) Console.WriteLine($"{s.Signature}\t{s.Count:N0}\t{s.Bytes:N0}\t{s.Compressed:N0}");
        break;
    }
    case "bench":
    {
        var sw = Stopwatch.StartNew();
        // EditorIDs to look up: every 30th of Starfield.esm's, up to 100,000
        var sf = index.Plugin("Starfield.esm")?.Segment ?? index.Plugins.Where(p => p.Segment != null).OrderByDescending(p => p.Segment!.Count).First().Segment!;
        var names = new List<string>(); var keys = new List<uint>();
        for (int i = 0; i < sf.Count && names.Count < 100000; i += 7) { if (sf.EditorId(i) is string s) names.Add(s); keys.Add(sf[i].LocalId); }
        Console.WriteLine($"picked {names.Count:N0} EditorIDs and {keys.Count:N0} FormKeys of {sf.Plugin}: {sw.Elapsed.TotalMilliseconds:F0} ms"); sw.Restart();
        long hits = 0; foreach (var n in names) hits += index.FindEditorId(n).Count;
        Console.WriteLine($"{names.Count:N0} EditorID lookups over all {index.Plugins.Count} plugins: {sw.Elapsed.TotalMilliseconds:F0} ms ({hits:N0} hits, {sw.Elapsed.TotalMilliseconds * 1000 / Math.Max(1, names.Count):F1} microseconds each)"); sw.Restart();
        var one = new List<int>(); hits = 0; foreach (var n in names) { one.Clear(); sf.FindEditorId(n, one); hits += one.Count; }
        Console.WriteLine($"the same in {sf.Plugin}'s segment alone: {sw.Elapsed.TotalMilliseconds:F0} ms ({hits:N0} hits)"); sw.Restart();
        hits = 0; foreach (var k in keys) { var v = index.Versions(sf.Plugin, k); if (v != null) hits += v.Holders.Count; }
        Console.WriteLine($"{keys.Count:N0} FormKey lookups with every holder and the winner: {sw.Elapsed.TotalMilliseconds:F0} ms ({hits:N0} holders)"); sw.Restart();
        int weapons = index.RecordsOfType(sf.Plugin, "WEAP").Count();
        Console.WriteLine($"every WEAP of {sf.Plugin} (a scan of {sf.Count:N0} entries): {sw.Elapsed.TotalMilliseconds:F1} ms ({weapons:N0})"); sw.Restart();
        long refs = 0; foreach (var p in index.Plugins) refs += index.RecordsOfType(p.Name, "REFR").LongCount();
        Console.WriteLine($"every REFR of every plugin (a scan of {index.TotalRecords:N0} entries): {sw.Elapsed.TotalMilliseconds:F0} ms ({refs:N0})"); sw.Restart();
        long winners = 0; foreach (var p in index.Plugins) foreach (var r in index.RecordsOfType(p.Name, "WEAP")) if (index.IsWinner(r)) winners++;
        Console.WriteLine($"winning weapons across the load order: {sw.Elapsed.TotalMilliseconds:F0} ms ({winners:N0})");
        break;
    }
    default: Console.Error.WriteLine($"Error: unknown command {args[0]}."); return 2;
}
var me = Process.GetCurrentProcess();
Console.WriteLine($"[{args[0]}] total {total.Elapsed.TotalSeconds:F2} s, peak private memory {me.PeakPagedMemorySize64 / 1048576} MB, peak working set {me.PeakWorkingSet64 / 1048576} MB (the working set counts the mapped plugin files too)");
return 0;
