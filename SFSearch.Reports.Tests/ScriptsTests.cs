// scripts, held against today's Scripts on darkstar.esm (the same scripts, the same lines found, the same source),
// the scripts on records held against Mutagen's own records, and the kept folder: decompiled once per content, never
// by date, usable without Champollion once made.
using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SFSearch.Index;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class ScriptsTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    const string Plugin = "darkstar.esm";
    static readonly (string Name, string Text)[] Finds = { ("AddItem", "AddItem"), ("GetFormFromFile", "GetFormFromFile"), ("Debug.Trace", "Debug.Trace"), ("event_on", "Event On") };

    public static string Today(Ctx ctx)
    {
        if (ctx.Champollion == null) return "SKIP Champollion (the Papyrus decompiler) wasn't found: name it with --champollion, or an SFSearch home with --champollion-home";
        if (!ctx.HasToday) return "SKIP no --today folder and no --sfsearch with --home: today's Scripts can't be asked";
        var s = ctx.Session;
        var report = new ScriptsReport(s);

        // ---- the list
        string? today = ctx.Today("scripts__darkstar", "Scripts", Plugin);
        if (today == null) return "SKIP today's answers aren't there and can't be made";
        var rows = Ctx.Table(today, "Plugin");
        var mine = report.Scripts(Plugin, new ScriptsOptions { Carriers = false });
        Check(rows.Count == 59, $"today's Scripts lists {rows.Count} scripts of {Plugin}; the task says 59");
        Check(mine.Scripts.Count == rows.Count, $"{rows.Count} scripts today, {mine.Scripts.Count} new");
        var byName = mine.Scripts.ToDictionary(x => x.Name, StringComparer.Ordinal);
        long lines = 0;
        foreach (var r in rows)
        {
            Check(byName.TryGetValue(r[1], out var sc), $"today's script {r[1]} isn't in the new report");
            Check(r[2] == sc!.Lines.ToString(CultureInfo.InvariantCulture), $"{r[1]}: {r[2]} lines today, {sc.Lines} new");
            Check(r[3].Equals(sc.File, StringComparison.OrdinalIgnoreCase), $"{r[1]}: file {r[3]} today, {sc.File} new");
            lines += sc.Lines;
        }
        Check(rows.Select(r => r[1]).SequenceEqual(mine.Scripts.Select(x => x.Name), StringComparer.OrdinalIgnoreCase), "the scripts come in another order than today's");

        // ---- find
        var findParts = new List<string>();
        long found = 0;
        foreach (var (name, text) in Finds)
        {
            string t = ctx.Today($"scripts__darkstar__find_{name}", "Scripts", Plugin, "--find", text)!;
            var hits = Ctx.Table(t, "Plugin");
            Check(!t.Contains("first 1000 shown", StringComparison.Ordinal), $"today's find of {text} is cut at 1,000 lines");
            var a = report.Scripts(Plugin, new ScriptsOptions { Find = text });
            Check(hits.Count == a.Lines.Count, $"find {text}: {hits.Count} lines today, {a.Lines.Count} new");
            for (int i = 0; i < hits.Count; i++)
            {
                var l = a.Lines[i];
                // Today's table writes a bar inside a cell with a backslash before it
                string textToday = string.Join(" | ", hits[i].Skip(3)).Replace("\\|", "|").Trim();
                Check(hits[i][1] == l.Script && hits[i][2] == l.Line.ToString(CultureInfo.InvariantCulture) && textToday == l.Text.Trim(),
                    $"find {text}, line {i}: today {hits[i][1]} {hits[i][2]} \"{textToday}\", new {l.Script} {l.Line} \"{l.Text.Trim()}\"");
            }
            var m = Regex.Match(t, @"(\d+) line\(s\) contain");
            Check(hits.Count == 0 || (m.Success && int.Parse(m.Groups[1].Value) == a.Lines.Count), $"find {text}: today's count line says {m.Value}");
            found += a.Lines.Count;
            findParts.Add($"\"{text}\" {N(a.Lines.Count)} lines in {a.Lines.Select(x => x.Script).Distinct().Count()} scripts");
        }
        // A text that is nowhere says why
        var nothing = report.Scripts(Plugin, new ScriptsOptions { Find = "zz no such text zz" });
        Check(nothing.Lines.Count == 0 && nothing.EmptyReason != null && nothing.ToText().Contains("lines of 59 scripts were looked at", StringComparison.Ordinal), "a find with no hit doesn't say why");

        // ---- show: one script that is only in the archive, one that has a loose copy
        var loose = mine.Scripts.First(x => x.From == "loose");
        var archived = mine.Scripts.First(x => x.From != "loose" && x.Lines > 100);
        foreach (var sc in new[] { archived, loose })
        {
            string t = ctx.Today($"scripts__darkstar__show_{sc.Name.Replace(':', '_')}", "Scripts", Plugin, "--show", sc.Name)!;
            int open = t.IndexOf("```papyrus", StringComparison.Ordinal), close = t.LastIndexOf("```", StringComparison.Ordinal);
            Check(open >= 0 && close > open, $"today's --show {sc.Name} has no source block");
            string sourceToday = t[(open + 10)..close].Replace("\r\n", "\n").Trim('\n');
            var a = report.Scripts(Plugin, new ScriptsOptions { Show = sc.Name });
            Check(a.Shown.Count == 1 && a.Shown[0].Source.Replace("\r\n", "\n").TrimEnd().Trim('\n') == sourceToday, $"--show {sc.Name}: the source differs from today's");
        }
        // The short name works, and a wrong one is one line with the near names
        Check(report.Scripts(Plugin, new ScriptsOptions { Show = loose.Name[(loose.Name.LastIndexOf(':') + 1)..] }).Shown.Count >= 1, "a script isn't found by the last part of its name");
        try { report.Scripts(Plugin, new ScriptsOptions { Show = "NoSuchScriptAtAll" }); Check(false, "a wrong script name gave an answer"); }
        catch (ReportException e) { Check(Regex.IsMatch(e.Line, @"^Error: [^\n]+\. Fix: [^\n]+\.$"), $"the error isn't one line: {e.Line}"); }

        // ---- where the compiled scripts came from
        int fromLoose = mine.Scripts.Count(x => x.From == "loose");
        var listed = new ArchiveReport(s).List(Plugin, "*.pex");
        Check(listed.Files.Count == 59 && listed.Files.Count(f => f.LooseCopy) == fromLoose, $"the archive holds {listed.Files.Count} compiled scripts, {listed.Files.Count(f => f.LooseCopy)} with a loose copy; the report took {fromLoose} from loose files");
        Check(mine.Notes.Any(n => n.Contains($"{fromLoose} scripts in its archives also exist as a loose file", StringComparison.Ordinal)), "the answer doesn't say that loose copies were decompiled");
        ctx.WriteReport("scripts-today.txt", mine.ToText(new ReportTextOptions { Detail = true, MaxChars = int.MaxValue }));
        return $"{Plugin}: the same {rows.Count} scripts as today's Scripts, each with the same number of lines ({N(lines)} in all) and the same file. " +
            $"find: {string.Join("; ", findParts)}: the same {N(found)} lines as today's, each with the same script, line number and text; a text that is nowhere says how many lines were looked at. " +
            $"show: {archived.Name} ({archived.Lines} lines, from the archive) and {loose.Name} ({loose.Lines} lines, from a loose file) have the same source as today's. " +
            $"{fromLoose} of the 59 compiled scripts in darkstar - main.ba2 have a loose copy under Data\\Scripts; the loose copy is the one decompiled, and the answer says so.";
    }

    /// <summary>The top-level scripts of a record as Mutagen itself gives them.</summary>
    static List<string> MutagenScripts(object record)
    {
        var names = new List<string>();
        var vmad = record.GetType().GetProperty("VirtualMachineAdapter")?.GetValue(record);
        if (vmad?.GetType().GetProperty("Scripts")?.GetValue(vmad) is IEnumerable scripts)
            foreach (var sc in scripts)
                if (sc?.GetType().GetProperty("Name")?.GetValue(sc) is string name && name.Length > 0) names.Add(name);
        return names;
    }

    /// <summary>The FormKeys an object property of that name holds in a record's top-level scripts, as Mutagen gives them.</summary>
    static List<string> MutagenObjectProperties(object record, string property)
    {
        var keys = new List<string>();
        var vmad = record.GetType().GetProperty("VirtualMachineAdapter")?.GetValue(record);
        if (vmad?.GetType().GetProperty("Scripts")?.GetValue(vmad) is not IEnumerable list) return keys;
        // The record's scripts, and a quest's fragment script (its Script)
        var scripts = list.Cast<object?>().ToList();
        if (vmad.GetType().GetProperty("Script")?.GetValue(vmad) is { } fragments) scripts.Add(fragments);
        foreach (var sc in scripts)
            if (sc?.GetType().GetProperty("Properties")?.GetValue(sc) is IEnumerable props)
                foreach (var p in props)
                {
                    if (p is IScriptObjectPropertyGetter o && string.Equals(o.Name, property, StringComparison.OrdinalIgnoreCase)) keys.Add(ReportSession.Canon(o.Object.FormKey.ToString()));
                    else if (p is IScriptObjectListPropertyGetter l && string.Equals(l.Name, property, StringComparison.OrdinalIgnoreCase))
                        foreach (var x in l.Objects) keys.Add(ReportSession.Canon(x.Object.FormKey.ToString()));
                }
        return keys;
    }

    /// <summary>
    /// One target of a give held against Mutagen's own reading of the records (the version the game uses): the carrier's
    /// property holds the record; for a two-step value ("core = Q (00180A:x.esm) QUST → workerContainer"), the carrier's
    /// property holds Q and Q's property holds the record. Returns 1.
    /// </summary>
    static int HeldAgainstMutagen(ReportSession s, ScriptCall c, CallTarget t)
    {
        // The version the game uses; when it is a partial form with no script block (a quest override that holds only
        // some fields), the game keeps the script block of the version before it
        object Record(string formKey)
        {
            var h = s.Winner(formKey) ?? throw new TestFailed($"{formKey} isn't found");
            var record = h.Engine.Load(h.Ref).Record;
            if (record.GetType().GetProperty("VirtualMachineAdapter")?.GetValue(record) != null) return record;
            var versions = s.Index.Versions(formKey);
            if (versions == null || versions.Winner < 0) return record;
            for (int i = versions.Winner - 1; i >= 0; i--)
            {
                var older = s.Engine.Load(versions.Holders[i]).Record;
                if (older.GetType().GetProperty("VirtualMachineAdapter")?.GetValue(older) != null) return older;
            }
            return record;
        }
        // "an entry of X": the record is an entry of the form list X holds (FormList.GetAt): X is read
        string via = t.Via.StartsWith("an entry of ", StringComparison.Ordinal) ? t.Via["an entry of ".Length..] : t.Via;
        int arrow = via.IndexOf(" → ", StringComparison.Ordinal);
        if (arrow < 0)
        {
            var values = MutagenObjectProperties(Record(t.CarrierFormKey!), via);
            Check(values.Contains(t.ItemFormKey!, StringComparer.OrdinalIgnoreCase), $"{c.Script}.{c.Function} {c.Shown}: on {t.Carrier} the report gives {via} = {t.ItemFormKey}; Mutagen gives [{string.Join(", ", values)}]");
            return 1;
        }
        string first = via[..via.IndexOf(" = ", StringComparison.Ordinal)], chain = via[(arrow + 3)..];
        var holder = Regex.Matches(via[..arrow], @"\(([0-9A-F]{6}:[^)]+)\)").Select(m => m.Groups[1].Value).LastOrDefault() ?? throw new TestFailed($"no record in {via}");
        var held = MutagenObjectProperties(Record(t.CarrierFormKey!), first);
        Check(held.Contains(holder, StringComparer.OrdinalIgnoreCase), $"{c.Shown}: on {t.Carrier} the report gives {first} = {holder}; Mutagen gives [{string.Join(", ", held)}]");
        var values2 = MutagenObjectProperties(Record(holder), chain);
        Check(values2.Contains(t.ItemFormKey!, StringComparer.OrdinalIgnoreCase), $"{c.Shown}: on {holder} the report gives {chain} = {t.ItemFormKey}; Mutagen gives [{string.Join(", ", values2)}]");
        return 2;
    }

    /// <summary>The value of a member of a struct property in a record's top-level scripts, as Mutagen gives it (its
    /// Data), found by walking Mutagen's objects by reflection.</summary>
    static object? MutagenMember(object record, string property, string member)
    {
        var vmad = record.GetType().GetProperty("VirtualMachineAdapter")?.GetValue(record);
        if (vmad?.GetType().GetProperty("Scripts")?.GetValue(vmad) is not IEnumerable scripts) return null;
        object? Find(object? o, bool inside, int depth)
        {
            if (o == null || depth > 8 || o is string) return null;
            var name = o.GetType().GetProperty("Name")?.GetValue(o) as string;
            if (inside && string.Equals(name, member, StringComparison.OrdinalIgnoreCase) && o.GetType().GetProperty("Data") is { } data) return data.GetValue(o);
            bool now = inside || (string.Equals(name, property, StringComparison.OrdinalIgnoreCase) && depth > 0);
            foreach (var p in o.GetType().GetProperties())
            {
                if (p.GetIndexParameters().Length > 0 || p.PropertyType.IsPrimitive || p.PropertyType == typeof(string)) continue;
                if (p.Name is not ("Properties" or "Members" or "Structs")) continue;
                if (p.GetValue(o) is IEnumerable items) foreach (var x in items) if (Find(x, now, depth + 1) is { } hit) return hit;
            }
            return null;
        }
        foreach (var sc in scripts) if (Find(sc, false, 0) is { } v) return v;
        return null;
    }

    public static string Kept(Ctx ctx)
    {
        if (ctx.Champollion == null) return "SKIP Champollion (the Papyrus decompiler) wasn't found: name it with --champollion, or an SFSearch home with --champollion-home";
        var s = ctx.Session;
        string keptBefore = s.KeptFolder!;
        string kept = ctx.Folder("kept-scripts-test");
        var parts = new List<string>();
        try
        {
            s.KeptFolder = kept;
            var report = new ScriptsReport(s);

            // ---- once per content
            var first = report.Scripts(Plugin, new ScriptsOptions { Carriers = false });
            Check(first.Decompiled == 59 && first.Scripts.Count == 59, $"a new kept folder: {first.Decompiled} decompiled, {first.Scripts.Count} scripts");
            var again = report.Scripts(Plugin, new ScriptsOptions { Carriers = false });
            Check(again.Decompiled == 0 && again.Scripts.Select(x => x.Name + x.Lines).SequenceEqual(first.Scripts.Select(x => x.Name + x.Lines)), $"the second call decompiled {again.Decompiled} scripts");
            var folders = Directory.GetDirectories(Path.Combine(kept, "scripts")).Where(d => File.Exists(Path.Combine(d, "manifest.json"))).ToList();
            Check(folders.Count == 1 && Path.GetFileName(folders[0]).Length == 32, $"{folders.Count} kept folders after two calls");
            string key = Path.GetFileName(folders[0]);
            // The quick stamps forgotten: the content key is worked out again and names the same folder
            foreach (var f in Directory.GetFiles(Path.Combine(kept, "scripts", "stamps"))) File.Delete(f);
            var third = report.Scripts(Plugin, new ScriptsOptions { Carriers = false });
            Check(third.Decompiled == 0 && Directory.GetDirectories(Path.Combine(kept, "scripts")).Count(d => File.Exists(Path.Combine(d, "manifest.json"))) == 1, "with the quick stamps gone the scripts were decompiled again");
            // Without Champollion: the kept sources still answer, and a plugin not yet decompiled says so in one line
            string? champollion = s.Champollion;
            try
            {
                s.Champollion = null;
                var without = report.Scripts(Plugin, new ScriptsOptions { Find = "AddItem" });
                Check(without.Lines.Count > 0, "without Champollion the kept sources gave no lines");
                try { report.Scripts("shipselfdestruct.esm", new ScriptsOptions { Carriers = false }); Check(false, "a plugin that was never decompiled gave an answer without Champollion"); }
                catch (ReportException e) { Check(Regex.IsMatch(e.Line, @"^Error: Champollion [^\n]+\. Fix: [^\n]+\.$"), $"without Champollion: {e.Line}"); parts.Add($"without Champollion the kept sources still answer, and a plugin not yet decompiled stops with one line (\"{e.Line[..60]}…\")"); }
            }
            finally { s.Champollion = champollion; }
            // The kept folder holds script source and a manifest, nothing else
            var keptFiles = Directory.GetFiles(folders[0], "*", SearchOption.AllDirectories);
            Check(keptFiles.All(f => f.EndsWith(".psc", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f) == "manifest.json"), "the kept folder holds something other than script source");
            long keptBytes = keptFiles.Sum(f => new FileInfo(f).Length);
            parts.Insert(0, $"{Plugin}: 59 scripts decompiled once into a folder named by the hash of the compiled scripts ({key[..8]}…, {keptFiles.Length} files, {keptBytes / 1024:N0} KB); the second call and a call with the quick stamps deleted decompile nothing");

            // ---- by content, not by place or date: a copy of a mod and its archive in the work folder
            const string Small = "shipselfdestruct.esm";
            string copyFolder = ctx.Folder("scripts-copy");
            string esm = Path.Combine(copyFolder, Small), ba2 = Path.Combine(copyFolder, "shipselfdestruct - main.ba2");
            ctx.MustBeInWork(esm); ctx.MustBeInWork(ba2);
            File.Copy(Path.Combine(ctx.Data, Small), esm, true); File.SetAttributes(esm, FileAttributes.Normal);
            File.Copy(Path.Combine(ctx.Data, "shipselfdestruct - main.ba2"), ba2, true); File.SetAttributes(ba2, FileAttributes.Normal);
            var inData = report.Scripts(Small, new ScriptsOptions { Carriers = false });
            Check(inData.Decompiled == inData.Scripts.Count && inData.Scripts.Count > 0, $"{Small}: {inData.Decompiled} decompiled, {inData.Scripts.Count} scripts");
            int before = Directory.GetDirectories(Path.Combine(kept, "scripts")).Count(d => File.Exists(Path.Combine(d, "manifest.json")));
            var beside = report.Scripts(esm, new ScriptsOptions { Carriers = false, Beside = true });
            Check(beside.Decompiled == 0 && beside.Scripts.Count == inData.Scripts.Count, $"the copy outside Data, with the same compiled scripts: {beside.Decompiled} decompiled");
            // An older date on the copy changes nothing
            File.SetLastWriteTimeUtc(ba2, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var older = report.Scripts(esm, new ScriptsOptions { Carriers = false, Beside = true });
            Check(older.Decompiled == 0, $"the same archive with an older date: {older.Decompiled} decompiled");
            Check(Directory.GetDirectories(Path.Combine(kept, "scripts")).Count(d => File.Exists(Path.Combine(d, "manifest.json"))) == before, "a copy or a date made a new kept folder");
            // Another content (one compiled script renamed inside the archive) is decompiled anew, into another folder
            RenameOnePex(ba2);
            var changed = report.Scripts(esm, new ScriptsOptions { Carriers = false, Beside = true });
            Check(changed.Decompiled == inData.Scripts.Count, $"the archive with one script renamed: {changed.Decompiled} decompiled");
            Check(Directory.GetDirectories(Path.Combine(kept, "scripts")).Count(d => File.Exists(Path.Combine(d, "manifest.json"))) == before + 1, "the changed archive didn't get its own kept folder");
            Check(!Directory.Exists(Path.Combine(kept, "scripts", "work")) || !Directory.EnumerateFileSystemEntries(Path.Combine(kept, "scripts", "work")).Any(), "the work folder wasn't emptied");
            parts.Add($"{Small} ({inData.Scripts.Count} scripts): a copy of the plugin and its archive in the work folder uses the same kept folder, with the archive's date set back to 2020 too; with one compiled script renamed inside the archive it is decompiled again into a second folder");

            // ---- which record carries which script, against Mutagen's own records
            var withRecords = report.Scripts(Plugin);
            var side = s.Side(Plugin);
            side.Engine.Sessions.Use(side.Name);
            var seg = side.Segment;
            var mineTop = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            int elsewhere = 0, nested = 0;
            foreach (var c in withRecords.Scripts.SelectMany(x => x.Carriers).Concat(withRecords.CarriedFromElsewhere))
            {
                if (Regex.IsMatch(c.Where, @"^VirtualMachineAdapter\.Scripts\[\d+\]$")) (mineTop.TryGetValue(c.FormKey, out var l) ? l : mineTop[c.FormKey] = new()).Add(c.Script);
                else nested++;
            }
            elsewhere = withRecords.CarriedFromElsewhere.Count;
            int records = 0, withScripts = 0, scripts = 0, worldRecords = 0;
            for (int i = 0; i < seg.FirstUnresolved; i++)
            {
                var e = seg[i];
                if (seg.SignatureOf(e) == "NAVI") continue;
                var rec = side.Engine.Load(new RecordRef(side.Plugin.Id, i));
                records++;
                if (ReportSession.IsWorld(seg, e)) worldRecords++;
                var theirs = MutagenScripts(rec.Record);
                string key2 = ReportSession.Canon(rec.Info.FormKey);
                mineTop.TryGetValue(key2, out var have);
                Check((have ?? new List<string>()).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(theirs.OrderBy(x => x, StringComparer.Ordinal)), $"{rec.Label}: Mutagen gives the scripts [{string.Join(", ", theirs)}], the report [{string.Join(", ", have ?? new List<string>())}]");
                if (theirs.Count > 0) { withScripts++; scripts += theirs.Count; }
            }
            Check(records == withRecords.RecordsScanned, $"{records} records held against Mutagen, {withRecords.RecordsScanned} looked at by the report");
            Check(mineTop.Count == withScripts, $"the report has scripts on {mineTop.Count} records, Mutagen on {withScripts}");
            int carried = withRecords.Scripts.Count(x => x.Carriers.Count > 0);
            // A plugin of this size is read whole: its placed references carry scripts too. Without world data the answer says what was left out.
            Check(withRecords.WorldLeftOut == 0 && seg.Count <= ScriptsReport.WorldLimit, $"{withRecords.WorldLeftOut} world records were left out of a plugin of {seg.Count} records");
            var noWorld = report.Scripts(Plugin, new ScriptsOptions { World = false });
            int carriersAll = withRecords.Scripts.SelectMany(x => x.Carriers).Select(c => c.FormKey).Distinct().Count(), carriersNoWorld = noWorld.Scripts.SelectMany(x => x.Carriers).Select(c => c.FormKey).Distinct().Count();
            Check(noWorld.WorldLeftOut == worldRecords && noWorld.RecordsScanned == records - worldRecords && noWorld.Notes.Any(n => n.StartsWith("World data was left out", StringComparison.Ordinal)) && carriersNoWorld < carriersAll,
                $"without world data: {noWorld.WorldLeftOut} left out of {worldRecords} world records, {noWorld.RecordsScanned} looked at, {carriersNoWorld} records carry the scripts ({carriersAll} with world data)");
            parts.Add($"scripts on records: all {N(records)} records of {Plugin} held against Mutagen's own records, placed references included: the same {N(scripts)} scripts on the same {N(withScripts)} records ({string.Join(", ", report.ScriptSubrecords)} is the subrecord the schema gives for a script list); {nested} more sit on quest aliases and in script fragments; " +
                $"{carried} of the 59 scripts are on {N(carriersAll)} records of the plugin, {59 - carried} on none; {N(elsewhere)} script entries name scripts of the game or a master; with the {N(worldRecords)} world records left out ({N(noWorld.RecordsScanned)} looked at) {N(carriersNoWorld)} records carry them, and the answer says what was left out");
        }
        finally { s.KeptFolder = keptBefore; }
        return string.Join(". ", parts) + ".";
    }

    /// <summary>
    /// S9: the code that reaches records without a link (held against what the old Sources --scripts found, and against
    /// the decompiled source when Champollion is there), loose scripts a plugin's records attach without its name in
    /// their path, compiled scripts of another compiler, and a missing script on a plugin made for the test.
    /// </summary>
    public static string Code(Ctx ctx)
    {
        var s = ctx.Session;
        var report = new ScriptsReport(s);
        var parts = new List<string>();

        // ---- darkstar.esm: the calls, read from the compiled scripts with no decompiler
        var a = report.Scripts(Plugin, new ScriptsOptions { Calls = true });
        Check(a.Mode == "calls" && a.Decompiled == 0 && a.CompiledRead == 59 && a.CompiledUnread.Count == 0, $"{Plugin}: {a.CompiledRead} compiled scripts read, {a.CompiledUnread.Count} not, {a.Decompiled} decompiled");
        var gff = a.Calls.Where(c => c.Call == "Game.GetFormFromFile").ToList();
        // The old Sources --scripts, as its code did it: a compiled script (its own archives, loose ones with the plugin's
        // name in their path) whose bytes hold the text "GetFormFromFile"
        var oldPex = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var arch in ArchiveReport.Resolve(s, Plugin).Archives)
            foreach (var f in s.Archives.Open(arch.Path).Files.Where(f => f.Extension == ".pex")) oldPex[ScriptsReport.ScriptOfPath(f.Path)] = f.Bytes();
        foreach (var rel in s.Archives.Loose.Where(l => l.StartsWith("scripts\\", StringComparison.OrdinalIgnoreCase) && l.EndsWith(".pex", StringComparison.OrdinalIgnoreCase) && l.Contains("darkstar", StringComparison.OrdinalIgnoreCase)))
            oldPex[ScriptsReport.ScriptOfPath(rel)] = File.ReadAllBytes(Path.Combine(s.DataFolder, rel));
        var oldGff = oldPex.Where(kv => Encoding.ASCII.GetString(kv.Value).Contains("GetFormFromFile", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newGff = gff.Select(c => c.Script).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check(oldGff.SetEquals(newGff), $"the old --scripts finds GetFormFromFile in [{string.Join(", ", oldGff.Order())}], the new report in [{string.Join(", ", newGff.Order())}]");
        Check(gff.All(c => c.FormId != null && c.NamedPlugin != null), "a GetFormFromFile call of darkstar.esm has no FormID or plugin, though each gives both as constants");
        Check(gff.Any(c => c.FormKey == "002A82:darkstar.esm" && c.Record == "DarkStar_TGO_Master_v1 (002A82:darkstar.esm)" && c.Signature == "QUST"), "Game.GetFormFromFile(0x2A82, \"DarkStar.esm\") isn't named DarkStar_TGO_Master_v1 (002A82:darkstar.esm) QUST");
        Check(gff.Any(c => c.NamedPlugin == "DarkStar_Test.esm" && c.NotFound == "DarkStar_Test.esm isn't in Data"), "the calls on DarkStar_Test.esm don't say that it isn't in Data");
        Check(a.Calls.Any(c => c.Call == "Game.IsPluginInstalled" && c.NamedPlugin == "skkuniversalstash.esm"), "IsPluginInstalled(\"skkuniversalstash.esm\") isn't listed");
        Check(a.PropertyGives > 0 && a.ToText().Contains("hand out a record named in a script property", StringComparison.Ordinal), "the gives of records named in script properties aren't said");
        string partA = $"{Plugin}: {a.CompiledRead} compiled scripts read with no decompiler: {gff.Count} GetFormFromFile calls in {newGff.Count} scripts, the same scripts as the old Sources --scripts flags; each with its FormID and plugin, resolved (DarkStar_TGO_Master_v1 (002A82:darkstar.esm) QUST; DarkStar_Test.esm isn't in Data)";

        // ---- S9a: a record handed out from a script property, named per record that attaches the script, held against
        // Mutagen's own reading of each record's script properties
        var onProperty = a.Calls.Where(c => c.Kind == "give" && c.ItemOrigin == ValueOrigin.Property).ToList();
        Check(onProperty.Count == a.PropertyGives && onProperty.All(c => c.Targets.Count > 0 || c.Via != null), $"{onProperty.Count} gives on a script property listed, {a.PropertyGives} counted; {onProperty.Count(c => c.Targets.Count == 0 && c.Via == null)} with no value and no reason");
        int heldAgainst = 0;
        foreach (var c in onProperty)
            foreach (var t in c.Targets.Where(t => t.ItemFormKey != null && t.CarrierFormKey != null && !t.Via.Contains("from its base", StringComparison.Ordinal) && !t.Carrier!.Contains("(its ", StringComparison.Ordinal)))
            {
                var hit = s.Winner(t.CarrierFormKey!);
                Check(hit != null, $"{c.Shown}: the record {t.Carrier} isn't found");
                heldAgainst += HeldAgainstMutagen(s, c, t);
            }
        Check(heldAgainst >= 20, $"only {heldAgainst} property values held against Mutagen");
        static ScriptCall One(ScriptsAnswer x, string script, string function, string call) =>
            x.Calls.FirstOrDefault(c => c.Script == script && c.Function == function && c.Shown.StartsWith(call, StringComparison.Ordinal)) ?? throw new TestFailed($"no call {call} in {script}.{function}");
        var armor = One(a, "DarkStar:TGO:Rewards:ForceAssaultArmor", "IssueArmor", "AddItem(property DarkStar_VIP_ForceArmor_Suit_MkI,");
        Check(armor.Targets.Count == 1 && armor.Targets[0].Record == "DarkStar_VIP_ForceArmor_Suit_MkI (001E37:darkstar.esm)" && armor.Targets[0].Signature == "ARMO" && armor.Targets[0].CarrierFormKey == "008C74:darkstar.esm"
            && ScriptsAnswer.Names(armor) == "on menu_DarkStar_Sub_ForceAssault_Armor (008C74:darkstar.esm) TMLM: DarkStar_VIP_ForceArmor_Suit_MkI = DarkStar_VIP_ForceArmor_Suit_MkI (001E37:darkstar.esm) ARMO", $"IssueArmor's first AddItem names: {ScriptsAnswer.Names(armor)}");
        // A placed reference that doesn't set the property has its base object's value
        var vendor = One(a, "DarkStar:ShipbuilderActivator", "EnableVendor", "PlaceAtMe(property OutpostShipbuilderVendor,");
        var omen = vendor.Targets.FirstOrDefault(t => t.CarrierFormKey == "0029B5:darkstar.esm");
        Check(omen != null && omen.Record == "ghost_DarkStar_Master_OutpostShipbuilderVendor (0079C1:darkstar.esm)" && omen.Via.Contains("from its base deprecated_act_DarkStar_Master_OutpostShipbuilder (0079BB:darkstar.esm)", StringComparison.Ordinal),
            $"OmenShipbuildActivator (0029B5:darkstar.esm) REFR: {omen?.Via} = {omen?.Result}");
        // A parameter followed to the calls of its function: ActivateRemote(akFurniture) is called by ActivateBenchRemotely.OnActivate with the property TargetBench
        var remote = One(a, "DarkStar:RemoteWorkbench", "ActivateRemote", "PlaceAtMe(property TargetBench,");
        Check(remote.Via != null && remote.Via.Contains("akFurniture of ActivateRemote is given by ActivateBenchRemotely.OnActivate", StringComparison.Ordinal) && remote.Targets.Any(t => t.Record == "furn_DarkStar_Workbench_std_WorkbenchArmor_INVIS (002B46:darkstar.esm)") && remote.Targets.Count >= 15,
            $"DarkStar:RemoteWorkbench.ActivateRemote's PlaceAtMe: {remote.Targets.Count} targets, via {remote.Via}");
        var noCaller = One(a, "DarkStar:TGO:Core:RemoteWorkbench", "ActivateRemote", "PlaceAtMe(parameter akFurniture,");
        Check(noCaller.Targets.Count == 0 && noCaller.Via != null && noCaller.Via.Contains("no script of the plugin calls RemoteWorkbench.ActivateRemote", StringComparison.Ordinal), $"DarkStar:TGO:Core:RemoteWorkbench.ActivateRemote: {ScriptsAnswer.Names(noCaller)}");
        // Constants the callers pass: CheckAll calls CheckForESM with five plugin names
        var esm = One(a, "DarkStar:TGO:Master:PlayerTriggerHandler", "CheckForESM", "Game.IsPluginInstalled(");
        Check(esm.Targets.Select(t => t.NamedPlugin).SequenceEqual(new[] { "Matilija.esm", "MatilijaAF.esm", "Antares.esm", "AntaresSupra.esm", "Falkland.esm" }) && esm.Via!.Contains("asName of CheckForESM is given by PlayerTriggerHandler.CheckAll", StringComparison.Ordinal),
            $"CheckForESM's IsPluginInstalled names [{string.Join(", ", esm.Targets.Select(t => t.NamedPlugin))}], via {esm.Via}");
        var later = new List<string>();
        // ---- S9a: a FormID and plugin name held in a struct property, on the record that attaches the script, held against Mutagen
        const string Kgc = "kinggathcreations_spaceship.esm";
        if (File.Exists(Path.Combine(ctx.Data, Kgc)))
        {
            var k = report.Scripts(Kgc, new ScriptsOptions { Calls = true });
            var dummy = One(k, "kgcShip:ObjectReference:DLCRefDummyPlacement", "OnInit", "Game.getFormFromFile(iFormID of property kFormPluginPair,");
            var t = dummy.Targets.SingleOrDefault();
            var hit = s.Winner("11AFF3:" + Kgc);
            var rec = hit is { } h ? h.Engine.Load(h.Ref).Record : null;
            object? id = rec == null ? null : MutagenMember(rec, "kFormPluginPair", "iFormID"), plugin = rec == null ? null : MutagenMember(rec, "kFormPluginPair", "sPluginName");
            Check(t != null && t.CarrierFormKey == "11AFF3:" + Kgc && id is int i && t.FormId == (uint)i && plugin is string pl && t.NamedPlugin == pl && t.FormKey == $"{i:X6}:{pl}" && t.NotFound == $"{pl} isn't in Data",
                $"{Kgc} DLCRefDummyPlacement.OnInit: target {t?.Via} → {t?.Result}; Mutagen gives iFormID {id}, sPluginName {plugin}");
            // The game's default when a record doesn't set the property: the script's start value
            var input = One(k, "kgcShip:ObjectReference:TransferLegendaryCONTInput", "OnItemAdded", "Game.getFormFromFile(property iMcClarenceHandlerID,");
            Check(input.Targets.Count == 2 && input.Targets.All(x => x.FormId == 387 && x.Via.Contains("isn't set on the record (the script's default 387)", StringComparison.Ordinal)), $"{Kgc} TransferLegendaryCONTInput: {ScriptsAnswer.Names(input)}");
            later.Add($"{Kgc}: Game.getFormFromFile(iFormID of property kFormPluginPair, sPluginName of property kFormPluginPair) on kgcShip_ACTI_Lgnd_ModTerminal (11AFF3) = {t!.FormKey} (the struct's members as Mutagen reads them; {t.NotFound}); a property no record sets takes the script's default (387 on 2 records)");
        }
        // ---- S9a: branches: an if whose paths hand out four different script properties lists each, with each record's value
        const string Mojo = "MojoSTC.esm";
        if (File.Exists(Path.Combine(ctx.Data, Mojo)))
        {
            var m0 = report.Scripts(Mojo, new ScriptsOptions { Calls = true });
            var dying = One(m0, "MojoEnemyDeathExplosion", "OnDying", "PlaceAtMe(");
            Check(dying.Args[0] == "property DeathExplosionEffect_Default or property DeathExplosionEffect_ET3 or property DeathExplosionEffect_ET2 or property DeathExplosionEffect_ET1"
                && dying.Targets.Count == 4 && dying.Targets.Any(x => x.Via == "DeathExplosionEffect_Default" && x.Record == "MojoSTC_EnemyDeathExplosion (000812:MojoSTC.esm)"),
                $"{Mojo} OnDying: {dying.Shown}: {ScriptsAnswer.Names(dying)}");
            later.Add($"{Mojo}: the 4 paths of MojoEnemyDeathExplosion.OnDying each hand out another property; all 4 listed, DeathExplosionEffect_Default = MojoSTC_EnemyDeathExplosion (000812:MojoSTC.esm) EXPL on the quest alias that attaches it");
        }
        // ---- J9 (S9a): two steps, a property of the script on the record a property holds
        var codex = One(a, "DarkStar:CodexMachineInputContainer", "DetectComponents", "PlaceAtMe(workerContainer of property core,");
        Check(codex.Targets.Count == 2 && codex.Targets.All(t => t.Record == "cont_DarkStar_invis_CodexWorker (00176F:darkstar.esm)" && t.Signature == "CONT"
                && t.Via.Contains("core = deprecated_quest_DarkStar_CodexCore_", StringComparison.Ordinal) && t.Via.Contains("→ workerContainer", StringComparison.Ordinal)),
            $"CodexMachineInputContainer.DetectComponents: {ScriptsAnswer.Names(codex)}");
        // ---- J9: the scripts of the plugins that have it as a master were looked in for a caller
        Check(noCaller.Via!.Contains("nor does a script of the", StringComparison.Ordinal) && noCaller.Via.Contains("plugins that have darkstar.esm as a master", StringComparison.Ordinal), $"RemoteWorkbench.ActivateRemote: {noCaller.Via}");
        Check(!a.Calls.Any(c => c.Args.Any(x => x.Contains("one of several", StringComparison.Ordinal))), "darkstar.esm: an argument is still \"one of several values\"");
        // The raw searches by script name pass over records of types with no script block: every record of darkstar.esm
        // that carries a script (world data included) is of one of the types they look at
        var listed = report.Scripts(Plugin, new ScriptsOptions { World = true });
        var carrierTypes = listed.Scripts.SelectMany(x => x.Carriers).Concat(listed.CarriedFromElsewhere).Select(x => x.Signature).Distinct().ToList();
        Check(carrierTypes.Count >= 5 && carrierTypes.All(t => report.ScriptRecordTypes.Contains(t)), $"record types that carry scripts in darkstar.esm [{string.Join(", ", carrierTypes)}]; the searches look at [{string.Join(", ", report.ScriptRecordTypes.Order())}]");
        later.Add($"the raw searches by script name look at the {report.ScriptRecordTypes.Count} record types with a script block; darkstar.esm's records with scripts are of {carrierTypes.Count} of them ({string.Join(", ", carrierTypes.Order())})");
        later.AddRange(OtherPlugins(ctx, report));
        partA += $"; {onProperty.Count} AddItem/PlaceAtMe/AddPerk … on a script property, each with the record per record that attaches the script ({heldAgainst} values held against Mutagen's own script properties; a placed reference's value from its base object); " +
            $"a parameter followed to its function's calls (DarkStar:RemoteWorkbench.ActivateRemote gets property TargetBench of ActivateBenchRemotely on {remote.Targets.Count} activators; CheckForESM gets 5 plugin names from CheckAll)";
        // With Champollion: one call for each line of the decompiled source that calls GetFormFromFile, in the same script, with the same FormID and plugin
        if (ctx.Champollion != null)
        {
            var lines = report.Scripts(Plugin, new ScriptsOptions { Find = "GetFormFromFile(" }).Lines;
            Check(lines.Count == gff.Count, $"the decompiled source has {lines.Count} lines that call GetFormFromFile, the bytecode {gff.Count} calls");
            foreach (var g in gff.GroupBy(c => c.Script, StringComparer.OrdinalIgnoreCase))
            {
                var mine = lines.Where(l => l.Script.Equals(g.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                Check(mine.Count == g.Count(), $"{g.Key}: {g.Count()} calls, {mine.Count} lines of source");
                foreach (var c in g)
                {
                    // The plugin as a text in the call, or the variable the bytecode shows it was set from ("modName = "DarkStar.esm"")
                    string? variable = c.Args.Count > 1 && c.Args[1].Contains(" = ", StringComparison.Ordinal) ? c.Args[1][..c.Args[1].IndexOf(" = ", StringComparison.Ordinal)] : null;
                    Check(mine.Any(l => (l.Text.Contains($"\"{c.NamedPlugin}\"", StringComparison.OrdinalIgnoreCase) || (variable != null && l.Text.Contains(variable, StringComparison.Ordinal)))
                        && (l.Text.Contains(c.FormId!.Value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) || l.Text.Contains(c.FormId.Value.ToString("X"), StringComparison.OrdinalIgnoreCase))),
                        $"{g.Key}: no line of source calls GetFormFromFile with {c.FormId:X} and \"{c.NamedPlugin}\" ({c.Shown})");
                }
            }
            partA += $"; the same {lines.Count} calls, script by script with the same FormID and plugin, as the decompiled source's lines";
        }
        parts.Add(partA);
        parts.AddRange(later);
        ctx.WriteReport("scripts-code-darkstar.txt", a.ToText(new ReportTextOptions { Detail = true, MaxChars = int.MaxValue }));

        // ---- loose scripts its records attach, with no plugin name in their path
        const string Loose = "ExitToLandingBay.esm";
        if (File.Exists(Path.Combine(ctx.Data, Loose)))
        {
            var b = report.Scripts(Loose, new ScriptsOptions { Calls = true });
            Check(b.CompiledRead == 2 && b.Notes.Any(n => n.Contains("Mod:MOD_CameraTrackerAlias", StringComparison.OrdinalIgnoreCase) && n.Contains("MOD_PlayerExitAliasScript", StringComparison.OrdinalIgnoreCase) && n.Contains("because its records attach them", StringComparison.Ordinal)),
                $"{Loose}: {b.CompiledRead} compiled scripts; its two loose scripts (Scripts\\Mod\\MOD_CameraTrackerAlias.pex, Scripts\\MOD_PlayerExitAliasScript.pex) weren't taken as its own");
            parts.Add($"{Loose}: no archive and no loose file with its name; its 2 loose scripts (Mod:MOD_CameraTrackerAlias, MOD_PlayerExitAliasScript) are taken because its records attach them (the old Scripts finds none)");
        }
        else parts.Add($"{Loose} isn't in Data: its part was left out");
        // ---- S9b: a loose library its script calls, with no plugin name in its path and on no record, and the types that library names
        const string Library = "UnityHealthScaler.esm";
        if (File.Exists(Path.Combine(ctx.Data, Library)) && File.Exists(Path.Combine(ctx.Data, "Scripts", "CassiopeiaPapyrusExtender.pex")))
        {
            var u = report.Scripts(Library, new ScriptsOptions { Calls = true });
            string? note = u.Notes.FirstOrDefault(n => n.Contains("because its scripts use", StringComparison.Ordinal));
            Check(u.CompiledRead == 4 && note != null && note.Contains("CassiopeiaPapyrusExtender (called from UnityHealthScaler)", StringComparison.Ordinal)
                && note.Contains("ArmorAddon (named as a type in CassiopeiaPapyrusExtender)", StringComparison.Ordinal),
                $"{Library}: {u.CompiledRead} compiled scripts; the loose library CassiopeiaPapyrusExtender its script calls wasn't taken: {note}");
            // The library isn't darkstar.esm's: no darkstar script calls it
            Check(!a.Notes.Any(n => n.Contains("CassiopeiaPapyrusExtender", StringComparison.Ordinal)), "darkstar.esm took CassiopeiaPapyrusExtender");
            parts.Add($"{Library}: its loose UnityHealthScaler calls the loose library CassiopeiaPapyrusExtender (no plugin name in its path, on no record): taken as its own, with ArmorAddon and Biome that the library names as types ({u.CompiledRead} compiled scripts; before, 1)");
        }
        else parts.Add($"{Library} or Scripts\\CassiopeiaPapyrusExtender.pex isn't in Data: its part was left out");
        // ---- compiled by another compiler (object sizes without the size field), and loose copies of the game's scripts
        const string Other = "MissionsGalore.esm";
        if (File.Exists(Path.Combine(ctx.Data, Other)))
        {
            var c = report.Scripts(Other, new ScriptsOptions { Calls = true });
            Check(c.CompiledUnread.Count == 0 && c.CompiledRead > 0, $"{Other}: {c.CompiledUnread.Count} compiled scripts couldn't be read: {string.Join("; ", c.CompiledUnread.Take(2).Select(u => u.Why))}");
            Check(c.Notes.Any(n => n.Contains("missionparentscript", StringComparison.OrdinalIgnoreCase) && n.Contains("a master's archive holds too", StringComparison.Ordinal)), $"{Other}: the loose copy of the game's missionparentscript was taken as its own");
            parts.Add($"{Other}: all {c.CompiledRead} compiled scripts read (another compiler writes an object's size without its own 4 bytes); the loose missionparentscript its records attach is the game's (Starfield - Misc.ba2 holds it), not its own");
        }

        // ---- a plugin made for the test: a script that is nowhere, and one of the game
        var mod = Made.New("sfs_scripts_missing.esm");
        var act = new Mutagen.Bethesda.Starfield.Activator(mod.GetNextFormKey(), StarfieldRelease.Starfield) { EditorID = "sfsTestScriptedActivator" };
        mod.Activators.Add(act);
        act.VirtualMachineAdapter = new VirtualMachineAdapter();
        act.VirtualMachineAdapter.Scripts.Add(new ScriptEntry { Name = "SFSearchTest:NoSuchScript" });
        act.VirtualMachineAdapter.Scripts.Add(new ScriptEntry { Name = "elitecrewdebugscript" });
        string made = Made.Write(ctx, mod, "scripts-missing");
        var m = report.Scripts(made, new ScriptsOptions());
        var missing = m.Missing.Select(x => x.Script).ToList();
        Check(missing.Count == 1 && missing[0] == "SFSearchTest:NoSuchScript", $"the made plugin: missing [{string.Join(", ", missing)}]");
        Check(m.Elsewhere.Any(e => e.Script == "elitecrewdebugscript" && e.FoundIn == "Starfield - Misc.ba2"), $"the made plugin: elitecrewdebugscript found in [{string.Join(", ", m.Elsewhere.Where(e => e.Script == "elitecrewdebugscript").Select(e => e.FoundIn))}]");
        string text = m.ToText();
        Check(text.Contains("Missing: 1 script the records attach is in no archive and no loose file the game loads: SFSearchTest:NoSuchScript (on 1 record).", StringComparison.Ordinal) && text.Contains("SFSearchTest:NoSuchScript (MISSING)", StringComparison.Ordinal), "the answer doesn't say the script is missing:\n" + text);
        parts.Add("a plugin made for the test: SFSearchTest:NoSuchScript is missing (\"Missing: 1 script …\"), elitecrewdebugscript is found in Starfield - Misc.ba2");
        Check(m.MissingUsed.Count == 0, $"the made plugin has no compiled scripts, yet scripts they use are missing: {string.Join(", ", m.MissingUsed.Select(x => x.Script))}");

        // ---- S9c: the scripts a mod's scripts use (parent, calls, types) that are nowhere; the game's own are found
        if (File.Exists(Path.Combine(ctx.Data, Library)) && File.Exists(Path.Combine(ctx.Data, "Scripts", "CassiopeiaPapyrusExtender.pex")))
        {
            var u = report.Scripts(Library, new ScriptsOptions());
            Check(u.MissingUsed.Count == 0 && u.UsedLookedFor >= 20, $"{Library}: {u.UsedLookedFor} used scripts looked for (the game's Form, ObjectReference, Game … that the library names), missing [{string.Join(", ", u.MissingUsed.Select(x => x.Script))}]");
            parts.Add($"{Library}: the {u.UsedLookedFor} scripts its scripts use that aren't its own (the game's, in Starfield - Misc.ba2) are all found: none missing");
        }
        parts.Add(MissingUsed(ctx));
        parts.Add(Dependents(ctx));
        return string.Join(". ", parts) + ".";
    }

    /// <summary>
    /// J9 (S9a) on a Data folder made for the test (in the work folder), with a Starfield.ccc beside it that loads
    /// sfs_j9_lib.esm, then sfs_j9_patch.esm (which has it as a master). The library's script sfs_j9_lib:Lib has a
    /// property Reward and three functions: Give(akItem) hands out its parameter and no script of the library calls it
    /// (the patch's script sfs_j9_patch:Caller does, with its own property Item); GiveReward hands out Reward; GivePicked
    /// hands out what its own function Pick returns (Reward). The library's activator attaches Lib with Reward = its
    /// MISC; the patch overrides that activator with Reward = the patch's first MISC, and has an activator of its own
    /// that attaches Lib (Reward = its second MISC) and Caller (Item = its first MISC).
    /// </summary>
    static string Dependents(Ctx ctx)
    {
        const string Lib = "sfs_j9_lib.esm", Patch = "sfs_j9_patch.esm";
        string root = ctx.Folder("scripts-j9"), data = Path.Combine(root, "Data");
        Directory.CreateDirectory(data);
        ctx.MustBeInWork(Path.Combine(data, Lib));
        File.WriteAllText(Path.Combine(root, "Starfield.ccc"), Lib + "\r\n" + Patch + "\r\n");
        static ScriptObjectProperty Obj(string name, Mutagen.Bethesda.Plugins.FormKey key) { var p = new ScriptObjectProperty { Name = name }; p.Object.SetTo(key); return p; }

        var lib = Made.New(Lib);
        var rewardA = new MiscItem(lib.GetNextFormKey(), StarfieldRelease.Starfield) { EditorID = "sfsJ9RewardLib" };
        lib.MiscItems.Add(rewardA);
        var actA = new Mutagen.Bethesda.Starfield.Activator(lib.GetNextFormKey(), StarfieldRelease.Starfield) { EditorID = "sfsJ9LibActivator" };
        actA.VirtualMachineAdapter = new VirtualMachineAdapter();
        actA.VirtualMachineAdapter.Scripts.Add(new ScriptEntry { Name = "sfs_j9_lib:Lib", Properties = { Obj("Reward", rewardA.FormKey) } });
        lib.Activators.Add(actA);
        string libPath = Made.Write(ctx, lib, Path.Combine("scripts-j9", "Data"));

        var patch = Made.New(Patch);
        var rewardB = new MiscItem(patch.GetNextFormKey(), StarfieldRelease.Starfield) { EditorID = "sfsJ9RewardPatch" };
        var rewardB2 = new MiscItem(patch.GetNextFormKey(), StarfieldRelease.Starfield) { EditorID = "sfsJ9RewardPatchOwn" };
        patch.MiscItems.Add(rewardB); patch.MiscItems.Add(rewardB2);
        var over = actA.DeepCopy();
        over.VirtualMachineAdapter = new VirtualMachineAdapter();
        over.VirtualMachineAdapter.Scripts.Add(new ScriptEntry { Name = "sfs_j9_lib:Lib", Properties = { Obj("Reward", rewardB.FormKey) } });
        patch.Activators.Add(over);
        var actB = new Mutagen.Bethesda.Starfield.Activator(patch.GetNextFormKey(), StarfieldRelease.Starfield) { EditorID = "sfsJ9PatchActivator" };
        actB.VirtualMachineAdapter = new VirtualMachineAdapter();
        actB.VirtualMachineAdapter.Scripts.Add(new ScriptEntry { Name = "sfs_j9_lib:Lib", Properties = { Obj("Reward", rewardB2.FormKey) } });
        actB.VirtualMachineAdapter.Scripts.Add(new ScriptEntry { Name = "sfs_j9_patch:Caller", Properties = { Obj("Item", rewardB.FormKey) } });
        patch.Activators.Add(actB);
        Made.Write(ctx, patch, Path.Combine("scripts-j9", "Data"), extraMaster: Lib);

        // The compiled scripts, loose under Data\Scripts with each plugin's name in their path
        var I = PexValue.Identifier;
        PexValue Id(string n) => new(I, n, 0, 0, false);
        var none = Id("::nonevar");
        var libPex = new MadeScript("sfs_j9_lib:Lib", "ObjectReference");
        libPex.Property("Reward", "Form", "::Reward_var");
        libPex.Function("Give", "none", new[] { ("akItem", "Form") }, new[] { ("::nonevar", "none") },
            (PexFile.OpCallMethod, new[] { Id("AddItem"), Id("self"), none }, new[] { Id("akItem") }));
        libPex.Function("GiveReward", "none", Array.Empty<(string, string)>(), new[] { ("::nonevar", "none") },
            (PexFile.OpCallMethod, new[] { Id("AddItem"), Id("self"), none }, new[] { Id("::Reward_var") }));
        libPex.Function("Pick", "Form", Array.Empty<(string, string)>(), Array.Empty<(string, string)>(),
            (PexFile.OpReturn, new[] { Id("::Reward_var") }, null));
        libPex.Function("GivePicked", "none", Array.Empty<(string, string)>(), new[] { ("::nonevar", "none"), ("::temp0", "Form") },
            (PexFile.OpCallMethod, new[] { Id("Pick"), Id("self"), Id("::temp0") }, Array.Empty<PexValue>()),
            (PexFile.OpCallMethod, new[] { Id("AddItem"), Id("self"), none }, new[] { Id("::temp0") }));
        var callerPex = new MadeScript("sfs_j9_patch:Caller", "ObjectReference");
        callerPex.Property("Item", "Form", "::Item_var");
        callerPex.Variable("::lib_var", "sfs_j9_lib:Lib");
        callerPex.Function("Run", "none", Array.Empty<(string, string)>(), new[] { ("::nonevar", "none") },
            (PexFile.OpCallMethod, new[] { Id("Give"), Id("::lib_var"), none }, new[] { Id("::Item_var") }));
        Directory.CreateDirectory(Path.Combine(data, "Scripts", "sfs_j9_lib"));
        Directory.CreateDirectory(Path.Combine(data, "Scripts", "sfs_j9_patch"));
        File.WriteAllBytes(Path.Combine(data, "Scripts", "sfs_j9_lib", "Lib.pex"), libPex.Bytes());
        File.WriteAllBytes(Path.Combine(data, "Scripts", "sfs_j9_patch", "Caller.pex"), callerPex.Bytes());

        using var session = new ReportSession(data, Path.Combine(ctx.Work, "scratch-j9"), ctx.Schema, "sfsreport-tests") { KeptFolder = Path.Combine(ctx.Work, "kept"), Champollion = ctx.Champollion };
        var a = new ScriptsReport(session).Scripts(Lib, new ScriptsOptions { Calls = true });
        ctx.WriteReport("scripts-j9-dependents.txt", a.ToText(new ReportTextOptions { Detail = true, MaxChars = int.MaxValue }));
        ScriptCall One(string function) => a.Calls.FirstOrDefault(c => c.Script == "sfs_j9_lib:Lib" && c.Function == function) ?? throw new TestFailed($"no call in Lib.{function}:\n{a.ToText()}");
        string B(MiscItem m) => $"{m.EditorID} ({m.FormKey.ID:X6}:{Patch})";
        // The patch's script calls the library's Give with its property Item: the patch's record gives it
        var give = One("Give");
        Check(give.Via != null && give.Via.Contains($"akItem of Give is given by Caller.Run ({Patch})", StringComparison.Ordinal)
            && give.Targets.Count == 1 && give.Targets[0].Record == B(rewardB) && give.Targets[0].CarrierFormKey == $"{actB.FormKey.ID:X6}:{Patch}",
            $"Lib.Give: {ScriptsAnswer.Names(give)}\n{a.ToText()}");
        // Reward: on the library's activator as the patch's override sets it, and on the patch's own activator
        foreach (var call in new[] { One("GiveReward"), One("GivePicked") })
        {
            var onA = call.Targets.FirstOrDefault(t => t.CarrierFormKey == $"{actA.FormKey.ID:X6}:{Lib}");
            var onB = call.Targets.FirstOrDefault(t => t.CarrierFormKey == $"{actB.FormKey.ID:X6}:{Patch}");
            Check(call.Targets.Count == 2 && onA?.Record == B(rewardB) && onA.Carrier!.Contains($"as {Patch} sets it", StringComparison.Ordinal) && onB?.Record == B(rewardB2),
                $"Lib.{call.Function}: {ScriptsAnswer.Names(call)}");
        }
        Check(One("GivePicked").Via!.Contains("what Lib.Pick() returns is read from its code", StringComparison.Ordinal), $"Lib.GivePicked: {One("GivePicked").Via}");
        return $"a Data folder made for the test ({Lib}, and {Patch} that has it as a master, loaded by a Starfield.ccc beside it): Lib.Give's parameter is given by the patch's script Caller.Run (its property Item, on the patch's activator); Lib's property Reward is read on the library's activator as the patch's override sets it and on the patch's own activator; GivePicked hands out what Lib.Pick returns (Reward), with the same two values";
    }

    /// <summary>
    /// GAPS S57: scripts2 --calls on a plugin that ships a copy of a game script reads the game's side faster by reading
    /// less. (1) A compiled script is read for its code only when its string table names the function looked for: the
    /// table check (PexFile.StringTableHolds) is held against the whole read on every compiled script of Starfield -
    /// Misc.ba2, for its own name, names it calls and a name no script has. (2) Aurie_TheGangsAllHere.esm --calls, each
    /// call in a new report session as the service makes one: the same answer as the first, and the warm call well under
    /// the 5 to 7.6 s it took before (the game's archives' file lists aren't read when their name tables hold no script,
    /// Starfield.esm's records are looked in only of the types that can attach the scripts: QUST, NPC_).
    /// </summary>
    public static string Speed(Ctx ctx)
    {
        var s = ctx.Session;
        string misc = Path.Combine(ctx.Data, "Starfield - Misc.ba2");
        var archive = s.Archives.Open(misc);
        int scripts = 0, checks = 0;
        foreach (var f in archive.Files.Where(f => f.Extension == ".pex"))
        {
            var bytes = f.Bytes();
            var file = PexFile.Read(bytes);
            scripts++;
            var names = new[] { file.Objects.FirstOrDefault()?.Name ?? "", "LockInCompanion", "AddItem", "GetFormFromFile", "SFSearchNoSuchName" };
            foreach (var n in names.Where(n => n.Length > 0))
            {
                checks++;
                bool want = file.Strings.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
                Check(PexFile.StringTableHolds(bytes, new[] { Encoding.ASCII.GetBytes(n.ToLowerInvariant()) }) == want, $"{f.Path}: the string table check says {!want} for {n}, the whole read {want}");
            }
        }
        Check(scripts > 4000, $"{misc}: {scripts} compiled scripts");
        string part2 = "Aurie_TheGangsAllHere.esm not in Data, skipped";
        const string Aurie = "Aurie_TheGangsAllHere.esm";
        if (File.Exists(Path.Combine(ctx.Data, Aurie)))
        {
            ScriptsAnswer Run(int n)
            {
                using var session = new ReportSession(ctx.Data, Path.Combine(ctx.Work, $"scratch-speed{n}"), ctx.Schema, "sfsreport-tests") { KeptFolder = Path.Combine(ctx.Work, "kept"), Champollion = ctx.Champollion };
                return new ScriptsReport(session).Scripts(Aurie, new ScriptsOptions { Calls = true });
            }
            var first = Run(1);
            var second = Run(2);
            string Calls(ScriptsAnswer x) => string.Join("\n", x.Calls.Select(c => ScriptsAnswer.Names(c, true)));
            Check(Calls(first) == Calls(second), $"{Aurie}: the second call's answer differs from the first");
            Check(first.Calls.Any(c => c.Function == "LockInCompanion" && c.Targets.Count >= 10), $"{Aurie}: LockInCompanion's values from the game's fragments are gone");
            Check(second.Seconds < 3.0, $"{Aurie} --calls took {second.Seconds:0.00} s warm (5 to 7.6 s before S57)");
            part2 = $"{Aurie} --calls in a new session: {first.Seconds:0.00} s the first time in this process, {second.Seconds:0.00} s the second (5 to 7.6 s before), the same answer ({first.Calls.Count} calls)";
        }
        return $"the string table check held against the whole read of {scripts:N0} compiled scripts of Starfield - Misc.ba2 ({checks:N0} names); {part2}";
    }

    /// <summary>A compiled script (Starfield's 3.12, game 4) made for the tests: one object, its variables, auto
    /// properties and functions in the empty state.</summary>
    internal sealed class MadeScript(string name, string parent)
    {
        readonly List<string> _strings = new();
        readonly List<(string Name, string Type)> _vars = new();
        readonly List<(string Name, string Type, string Auto)> _props = new();
        readonly List<(string Name, string Returns, (string, string)[] Params, (string, string)[] Locals, (byte Op, PexValue[] Args, PexValue[]? More)[] Code)> _functions = new();
        public void Variable(string n, string type) => _vars.Add((n, type));
        public void Property(string n, string type, string auto) { _props.Add((n, type, auto)); _vars.Add((auto, type)); }
        public void Function(string n, string returns, (string, string)[] parameters, (string, string)[] locals, params (byte, PexValue[], PexValue[]?)[] code) => _functions.Add((n, returns, parameters, locals, code));

        ushort S(string s) { int i = _strings.IndexOf(s); if (i < 0) { _strings.Add(s); i = _strings.Count - 1; } return (ushort)i; }
        void Value(BinaryWriter w, PexValue v)
        {
            w.Write(v.Kind);
            switch (v.Kind)
            {
                case PexValue.Identifier: case PexValue.String: w.Write(S(v.Text ?? "")); break;
                case PexValue.Integer: w.Write(v.Int); break;
                case PexValue.FloatKind: w.Write(v.Float); break;
                case PexValue.BoolKind: w.Write((byte)(v.Bool ? 1 : 0)); break;
            }
        }

        public byte[] Bytes()
        {
            ushort nameIndex = S(name);
            var obj = new MemoryStream();
            var o = new BinaryWriter(obj);
            o.Write(S(parent)); o.Write(S("")); o.Write((byte)0); o.Write(0u); o.Write(S(""));
            o.Write((ushort)0);
            o.Write((ushort)_vars.Count);
            foreach (var (vn, vt) in _vars) { o.Write(S(vn)); o.Write(S(vt)); o.Write(0u); o.Write(PexValue.None); o.Write((byte)0); }
            o.Write((ushort)0);
            o.Write((ushort)_props.Count);
            foreach (var (pn, pt, auto) in _props) { o.Write(S(pn)); o.Write(S(pt)); o.Write(S("")); o.Write(0u); o.Write((byte)7); o.Write(S(auto)); }
            o.Write((ushort)1); o.Write(S(""));
            o.Write((ushort)_functions.Count);
            foreach (var (fn, returns, ps, ls, code) in _functions)
            {
                o.Write(S(fn)); o.Write(S(returns)); o.Write(S("")); o.Write(0u); o.Write((byte)0);
                o.Write((ushort)ps.Length); foreach (var (n, t) in ps) { o.Write(S(n)); o.Write(S(t)); }
                o.Write((ushort)ls.Length); foreach (var (n, t) in ls) { o.Write(S(n)); o.Write(S(t)); }
                o.Write((ushort)code.Length);
                foreach (var (op, args, more) in code)
                {
                    o.Write(op);
                    foreach (var v in args) Value(o, v);
                    if (more != null) { Value(o, new PexValue(PexValue.Integer, null, more.Length, 0, false)); foreach (var v in more) Value(o, v); }
                }
            }
            o.Flush();
            var file = new MemoryStream();
            var w = new BinaryWriter(file);
            void WStr(string s) { var b = Encoding.UTF8.GetBytes(s); w.Write((ushort)b.Length); w.Write(b); }
            w.Write(PexFile.Magic); w.Write((byte)3); w.Write((byte)12); w.Write((ushort)4); w.Write(0UL);
            WStr(name.Replace(':', '_') + ".psc"); WStr("sfsreport-tests"); WStr("test");
            w.Write((ushort)_strings.Count);
            foreach (var s in _strings) WStr(s);
            w.Write((byte)0);
            w.Write((ushort)0);
            w.Write((ushort)1);
            w.Write(nameIndex);
            w.Write((uint)(obj.Length + 4));
            w.Write(obj.ToArray());
            w.Flush();
            return file.ToArray();
        }
    }

    /// <summary>
    /// J9 (S9a) on real plugins: what a function of the plugin returns, a copy of a game script whose callers and
    /// property values are in Starfield.esm, winning overrides, placed references of a plugin of more than 100,000
    /// records, a struct member set in an array of structs, a plugin name with spaces, many values in one argument.
    /// Each part is left out when its plugin isn't in Data.
    /// </summary>
    static IEnumerable<string> OtherPlugins(Ctx ctx, ScriptsReport report)
    {
        var s = ctx.Session;
        bool Has(string plugin) => File.Exists(Path.Combine(ctx.Data, plugin));
        static ScriptCall One(ScriptsAnswer x, string script, string function, string call) =>
            x.Calls.FirstOrDefault(c => c.Script == script && c.Function == function && c.Shown.StartsWith(call, StringComparison.Ordinal)) ?? throw new TestFailed($"no call {call} in {script}.{function}");
        const string Fuel = "realfuel_creations.esm";
        if (Has(Fuel))
        {
            // A script variable another function sets, and what the plugin's own function returns
            var r = report.Scripts(Fuel, new ScriptsOptions { Calls = true });
            var spawn = One(r, "RF:SpawnItemScript", "DoCargoHoldSpawn", "AddItem(");
            Check(spawn.Via != null && spawn.Via.Contains("what SpawnItemScript.GetRandomFuelItem() returns is read from its code", StringComparison.Ordinal)
                && spawn.Targets.Any(t => t.Record == "_RF_LIST_FuelItems (0008EA:realfuel_creations.esm)" && t.Signature == "FLST"), $"{Fuel} DoCargoHoldSpawn: {spawn.Shown}: {ScriptsAnswer.Names(spawn)}");
            yield return $"{Fuel}: AddItem(GetRandomFuelItem()) follows what the function returns (an element of the script variable another function sets from property _RF_LIST_FuelItems) to _RF_LIST_FuelItems (0008EA) FLST on the quest that attaches it";
        }
        const string Aurie = "Aurie_TheGangsAllHere.esm";
        if (Has(Aurie))
        {
            // A copy of the game's SQ_CompanionsScript: its callers are the game's quest fragments, their properties on Starfield.esm's quests
            var r = report.Scripts(Aurie, new ScriptsOptions { Calls = true });
            var lockIn = One(r, "SQ_CompanionsScript", "LockInCompanion", "PlaceAtMe(");
            Check(lockIn.Via != null && lockIn.Via.Contains("TextReplaceActivator of LockInCompanion is given by QF_", StringComparison.Ordinal) && lockIn.Via.Contains("(Starfield.esm)", StringComparison.Ordinal)
                && lockIn.Targets.Count >= 10 && lockIn.Targets.All(t => t.CarrierFormKey!.EndsWith(":Starfield.esm", StringComparison.Ordinal)), $"{Aurie} LockInCompanion: {lockIn.Targets.Count} targets, via {lockIn.Via}");
            int held = 0;
            foreach (var t in lockIn.Targets.Where(t => t.ItemFormKey != null)) held += HeldAgainstMutagen(s, lockIn, t);
            // Records of the game whose winning override is another mod's: the values the game uses
            var give = One(r, "COM_CREW_GiveItemActorScript", "GiveItems", "AddItem(");
            var won = give.Targets.Where(t => t.Carrier!.Contains("sets it, the version the game uses", StringComparison.Ordinal)).ToList();
            Check(won.Count >= 5 && give.Targets.Count >= 10, $"{Aurie} GiveItems: {give.Targets.Count} targets, {won.Count} from a winning override");
            foreach (var t in give.Targets.Where(t => t.ItemFormKey != null)) held += HeldAgainstMutagen(s, give, t);
            yield return $"{Aurie}: its copy of the game's SQ_CompanionsScript: LockInCompanion's parameter is given by the game's quest fragments (Starfield - Misc.ba2), their properties read on {lockIn.Targets.Count} of Starfield.esm's quests; COM_CREW_GiveItemActorScript's GiveItemList on {give.Targets.Count} NPCs of Starfield.esm, {won.Count} as a later mod's winning override sets it ({held} values held against Mutagen on the version the game uses)";
        }
        const string Kgc = "kinggathcreations_spaceship.esm";
        if (Has(Kgc))
        {
            // Placed references (world data) of a plugin of 371,811 records attach the script; a member no element sets
            var r = report.Scripts(Kgc, new ScriptsOptions { Calls = true });
            var check = One(r, "kgcShip:ObjectReference:EnableOnLoadWhenRequirementsMet", "CheckRequirements", "Game.IsPluginInstalled(ConditionPlugin");
            var board = check.Targets.FirstOrDefault(t => t.CarrierFormKey == "0CB076:" + Kgc);
            var hit = s.Winner("0CB076:" + Kgc);
            var member = hit is { } h ? MutagenMember(h.Engine.Load(h.Ref).Record, "ConditionRequirements", "ConditionPlugin") : "no record";
            Check(board != null && board.NotFound == "no plugin named (the check is false)" && board.Via.Contains("isn't set in any element", StringComparison.Ordinal) && member == null && check.Targets.Count >= 6,
                $"{Kgc} CheckRequirements: {ScriptsAnswer.Names(check)}; Mutagen's ConditionPlugin on 0CB076: {member}");
            yield return $"{Kgc}: its placed references (world data) are looked in for the scripts no other record attaches: EnableOnLoadWhenRequirementsMet on {check.Targets.Select(t => t.CarrierFormKey).Distinct().Count()} mission boards (REFR), where no element of ConditionRequirements sets ConditionPlugin (as Mutagen reads it)";
        }
        const string Astra = "astra-19 sidearm.esm";
        if (Has(Astra))
        {
            var r = report.Scripts(Astra, new ScriptsOptions { Calls = true });
            var add = One(r, "LLInjectionAstra19", "InjectList5", "AddForm(");
            Check(add.Targets.Count == 1 && add.Targets[0].Record == "Astra19 (000808:astra-19 sidearm.esm)" && add.Targets[0].Signature == "WEAP", $"{Astra} InjectList5: {ScriptsAnswer.Names(add)}");
            yield return $"{Astra}: a struct member that names a record of a plugin whose name has a space: Custom_5 = Astra19 (000808) WEAP (was \"decided at run time\")";
        }
        const string Apc = "apc_galaxysmartcontainers.esm";
        if (Has(Apc))
        {
            var r = report.Scripts(Apc, new ScriptsOptions { Calls = true });
            var v = One(r, "APCG_SC_RSBox_Resources", "V", "Game.GetFormFromFile(");
            Check(!v.Args[0].Contains("one of several", StringComparison.Ordinal) && v.Targets.Count >= 100, $"{Apc} V: {v.Targets.Count} targets, {v.Shown[..Math.Min(200, v.Shown.Length)]}");
            Check(!r.Calls.Any(c => c.Args.Any(x => x.Contains("one of several", StringComparison.Ordinal))), $"{Apc}: an argument is still \"one of several values\"");
            yield return $"{Apc}: GetFormFromFile(fid) in V gets {v.Targets.Count} FormIDs from FillList (was \"one of several values\" past 64)";
        }
    }

    /// <summary>
    /// S9c on a Data folder made for the test (in the work folder): a copy of Starfield - Misc.ba2 (the game's compiled
    /// scripts), an empty Starfield.esm, a plugin, and a loose compiled script of its that extends a script that is
    /// nowhere, calls a library that is nowhere, names a type that is nowhere, and uses the game's Debug and ObjectReference.
    /// </summary>
    static string MissingUsed(Ctx ctx)
    {
        const string Name = "sfs_scripts_parentless.esm", Script = "sfs_scripts_parentless:Orphan";
        string data = ctx.Folder("scripts-parentless-data");
        ctx.MustBeInWork(Path.Combine(data, Name));
        string misc = Path.Combine(ctx.Data, "Starfield - Misc.ba2");
        if (!File.Exists(misc)) return "Starfield - Misc.ba2 isn't in Data: the made Data folder was left out";
        File.Copy(misc, Path.Combine(data, "Starfield - Misc.ba2"), overwrite: true);
        // An empty Starfield.esm: the made plugin's master, whose archives are the game's
        new StarfieldMod(Mutagen.Bethesda.Plugins.ModKey.FromFileName(Made.Game), StarfieldRelease.Starfield).WriteToBinary(Path.Combine(data, Made.Game));
        var mod = Made.New(Name);
        var act = new Mutagen.Bethesda.Starfield.Activator(mod.GetNextFormKey(), StarfieldRelease.Starfield) { EditorID = "sfsTestOrphanActivator" };
        // A link to the game makes Starfield.esm its master
        act.Keywords = new() { new Mutagen.Bethesda.Plugins.FormLink<IKeywordGetter>(Made.Key("000001:Starfield.esm")) };
        act.VirtualMachineAdapter = new VirtualMachineAdapter();
        act.VirtualMachineAdapter.Scripts.Add(new ScriptEntry { Name = Script });
        mod.Activators.Add(act);
        string made = Made.Write(ctx, mod, "scripts-parentless-data");
        string scripts = Path.Combine(data, "Scripts", "sfs_scripts_parentless");
        Directory.CreateDirectory(scripts);
        File.WriteAllBytes(Path.Combine(scripts, "Orphan.pex"), MadePex(Script, "SFSearchTest:NoSuchParent",
            variables: new[] { ("::lib_var", "SFSearchTest:NoSuchType"), ("::ref_var", "ObjectReference") },
            calls: new[] { ("SFSearchTest:NoSuchLib", "DoIt"), ("Debug", "Trace") }));

        using var session = new ReportSession(data, Path.Combine(ctx.Work, "scratch-parentless"), ctx.Schema, "sfsreport-tests") { KeptFolder = Path.Combine(ctx.Work, "kept"), Champollion = ctx.Champollion };
        var a = new ScriptsReport(session).Scripts(Path.GetFileName(made), new ScriptsOptions());
        string text = a.ToText();
        ctx.WriteReport("scripts-missing-used.txt", text);
        var got = a.MissingUsed.Select(x => $"{x.Script} = {x.UsedText}").ToList();
        var want = new[] { "SFSearchTest:NoSuchLib = called from " + Script, "SFSearchTest:NoSuchParent = the parent of " + Script, "SFSearchTest:NoSuchType = named as a type in " + Script };
        Check(got.SequenceEqual(want), $"the made Data folder: missing used scripts [{string.Join("; ", got)}], expected [{string.Join("; ", want)}]\n{text}");
        // Debug and ObjectReference are the game's, found in the copy of Starfield - Misc.ba2; the script itself is the plugin's
        Check(a.UsedLookedFor == 5 && !a.Missing.Any(), $"the made Data folder: {a.UsedLookedFor} used scripts looked for (5: 3 nowhere, Debug, ObjectReference), {a.Missing.Count()} attached scripts missing\n{text}");
        Check(text.Contains("Missing: 3 scripts the plugin's scripts use are in no archive and no loose file the game loads", StringComparison.Ordinal)
            && text.Contains($"  MISSING: SFSearchTest:NoSuchParent, the parent of {Script}", StringComparison.Ordinal)
            && text.Contains($"  MISSING: SFSearchTest:NoSuchLib, called from {Script}", StringComparison.Ordinal)
            && text.Contains($"  MISSING: SFSearchTest:NoSuchType, named as a type in {Script}", StringComparison.Ordinal), "the answer doesn't name the missing used scripts:\n" + text);
        var json = a.ToJson();
        Check(json["counts"]?["missingUsed"]?.GetValue<int>() == 3 && json["missingUsed"]?.AsArray().Count == 3, "the JSON doesn't count the 3 missing used scripts");
        return $"a Data folder made for the test (a copy of Starfield - Misc.ba2, an empty Starfield.esm, {Name} and its loose {Script}): its parent SFSearchTest:NoSuchParent, the library it calls and a type it names are MISSING; the game's Debug and ObjectReference it uses are found in Starfield - Misc.ba2";
    }

    /// <summary>A compiled script (Starfield's 3.12, game 4) with one object, the given script variables, and one function
    /// that makes each static call.</summary>
    static byte[] MadePex(string name, string parent, (string Name, string Type)[] variables, (string Script, string Function)[] calls)
    {
        var strings = new List<string>();
        ushort S(string s) { int i = strings.IndexOf(s); if (i < 0) { strings.Add(s); i = strings.Count - 1; } return (ushort)i; }
        ushort nameIndex = S(name);
        var obj = new MemoryStream();
        var o = new BinaryWriter(obj);
        o.Write(S(parent)); o.Write(S("")); o.Write((byte)0); o.Write(0u); o.Write(S(""));   // parent, doc, const, user flags, auto state
        o.Write((ushort)0);                                                                  // structs
        o.Write((ushort)variables.Length);
        foreach (var (vn, vt) in variables) { o.Write(S(vn)); o.Write(S(vt)); o.Write(0u); o.Write(PexValue.None); o.Write((byte)0); } // name, type, user flags, start None, const
        o.Write((ushort)0); o.Write((ushort)0);                                              // guards, properties
        o.Write((ushort)1); o.Write(S(""));                                                  // one state, the empty one
        o.Write((ushort)1); o.Write(S("OnInit"));                                            // one function
        o.Write(S("none")); o.Write(S("")); o.Write(0u); o.Write((byte)0);                   // returns, doc, user flags, flags
        o.Write((ushort)0);                                                                  // parameters
        o.Write((ushort)1); o.Write(S("::nonevar")); o.Write(S("none"));                     // locals
        o.Write((ushort)calls.Length);
        foreach (var (script, function) in calls)
        {
            o.Write(PexFile.OpCallStatic);
            o.Write(PexValue.Identifier); o.Write(S(script));
            o.Write(PexValue.Identifier); o.Write(S(function));
            o.Write(PexValue.Identifier); o.Write(S("::nonevar"));
            o.Write(PexValue.Integer); o.Write(0);
        }
        o.Flush();
        var file = new MemoryStream();
        var w = new BinaryWriter(file);
        void WStr(string s) { var b = Encoding.UTF8.GetBytes(s); w.Write((ushort)b.Length); w.Write(b); }
        w.Write(PexFile.Magic); w.Write((byte)3); w.Write((byte)12); w.Write((ushort)4); w.Write(0UL);
        WStr("Orphan.psc"); WStr("sfsreport-tests"); WStr("test");
        w.Write((ushort)strings.Count);
        foreach (var s in strings) WStr(s);
        w.Write((byte)0);              // no debug info
        w.Write((ushort)0);            // user flags
        w.Write((ushort)1);            // one object
        w.Write(nameIndex);
        w.Write((uint)(obj.Length + 4));
        w.Write(obj.ToArray());
        w.Flush();
        return file.ToArray();
    }

    /// <summary>Changes one letter in the name of the first compiled script in an archive's name table (a copy in the work folder).</summary>
    static void RenameOnePex(string archive)
    {
        var bytes = File.ReadAllBytes(archive);
        long at = (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16));
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        for (int i = 0; i < count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)at));
            string name = Encoding.UTF8.GetString(bytes, (int)at + 2, len);
            if (name.EndsWith(".pex", StringComparison.OrdinalIgnoreCase))
            {
                int letter = (int)at + 2 + len - 5;
                bytes[letter] = bytes[letter] == (byte)'z' ? (byte)'y' : (byte)'z';
                File.WriteAllBytes(archive, bytes);
                return;
            }
            at += 2 + len;
        }
        throw new TestFailed("the archive holds no compiled script");
    }
}
