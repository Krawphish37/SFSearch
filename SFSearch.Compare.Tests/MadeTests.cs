// Tests on plugins made for the purpose, where the right answer is known exactly. The plugins are written by
// Mutagen into the work folder (never into Data, never into the repo): copies with records edited, added and
// removed, two patches for one master, a record written with and without a spelled-out default.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SFSearch.Compare;
using SFSearch.Index;
using IndexStyle = SFSearch.Index.MasterStyle;
using MutagenStyle = Mutagen.Bethesda.Plugins.MasterStyle;

namespace SFSearch.Compare.Tests;

static partial class MadeTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);

    /// <summary>
    /// The tests the author keeps with the agent benchmark (a dev tool, not in the public repository): each holds a
    /// benchmark question's known answer. Filled in by their own file when it is in the checkout; without it, none.
    /// </summary>
    public static List<(string Name, Func<Ctx, string> Run)> Benchmark() { var tests = new List<(string Name, Func<Ctx, string> Run)>(); AddBenchmark(tests); return tests; }
    static partial void AddBenchmark(List<(string Name, Func<Ctx, string> Run)> tests);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    const string Small = "weap_starlight.esm";

    // ---------------------------------------------------------------- Mutagen
    static MutagenStyle Convert(IndexStyle s) => s switch { IndexStyle.Small => MutagenStyle.Small, IndexStyle.Medium => MutagenStyle.Medium, _ => MutagenStyle.Full };

    /// <summary>The sizes of a plugin's masters and of the plugin itself, as the header index has them.</summary>
    static List<KeyedMasterStyle> Styles(Ctx ctx, string plugin, params string[] more)
    {
        var p = ctx.Session.Index.Plugin(plugin) ?? throw new TestFailed($"{plugin} isn't in the Data folder");
        var list = new List<KeyedMasterStyle>();
        for (int i = 0; i < p.Header!.Masters.Count; i++) list.Add(new KeyedMasterStyle(ModKey.FromFileName(p.Header.Masters[i]), Convert(p.MasterStyles[i])));
        list.Add(new KeyedMasterStyle(ModKey.FromFileName(p.Name), Convert(p.Header.Style)));
        foreach (var m in more) list.Add(new KeyedMasterStyle(ModKey.FromFileName(m), MutagenStyle.Full));
        return list;
    }

    static StarfieldMod Read(string path, List<KeyedMasterStyle> styles) =>
        StarfieldMod.CreateFromBinary(new ModPath(ModKey.FromFileName(Path.GetFileName(path)), path), StarfieldRelease.Starfield,
            new BinaryReadParameters { MasterFlagsLookup = new LoadOrder<IModMasterStyledGetter>(styles), ThrowOnUnknownSubrecord = false });

    /// <summary>Writes a plugin into the work folder. <paramref name="keepHeader"/>: the master list stays as the header has it (a changed copy of a plugin).</summary>
    static void Write(Ctx ctx, StarfieldMod mod, string path, List<KeyedMasterStyle> styles, bool keepHeader)
    {
        ctx.MustBeInWork(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lookup = new LoadOrder<IModMasterStyledGetter>(styles);
        mod.WriteToBinary(path, keepHeader
            ? new BinaryWriteParameters
            {
                MasterFlagsLookup = lookup, ModKey = ModKeyOption.CorrectToPath,
                MastersListContent = MastersListContentOption.NoCheck,
                MastersListOrdering = new MastersListOrderingEnumOption { Option = MastersListOrderingOption.NoCheck },
                NextFormID = NextFormIDOption.NoCheck, FormIDCompaction = FormIDCompactionOption.NoCheck,
                LowerRangeDisallowedHandler = new NoCheckIfLowerRangeDisallowed(), CleanNulls = false, HeaderSizeFlags = HeaderSizeFlagsOption.NoCheck,
            }
            : new BinaryWriteParameters
            {
                MasterFlagsLookup = lookup, ModKey = ModKeyOption.CorrectToPath,
                MastersListOrdering = new MastersListOrderingByLoadOrder(lookup),
                LowerRangeDisallowedHandler = new NoCheckIfLowerRangeDisallowed(), HeaderSizeFlags = HeaderSizeFlagsOption.NoCheck,
            });
    }

    static FormKey Key(string formKey) => FormKey.Factory(formKey);

    // ---------------------------------------------------------------- Defaults: a pair made with Mutagen
    /// <summary>
    /// The aim models of weap_starlight.esm are written the older, shorter way: 64 bytes, without the last field
    /// (ConeOfFireIgnoresMovement), which is then at its default, false. Mutagen writes the same record both ways:
    /// as it is, and with the field spelled out (65 bytes). The two files differ; the two records don't.
    /// </summary>
    public static string DefaultsPair(Ctx ctx)
    {
        var styles = Styles(ctx, Small);
        string source = Path.Combine(ctx.Data, Small);
        string shortPath = Path.Combine(ctx.Folder("defaults-short"), Small), longPath = Path.Combine(ctx.Folder("defaults-long"), Small), otherPath = Path.Combine(ctx.Folder("defaults-other"), Small);
        var key = Key("000847:weap_starlight.esm");
        {
            var mod = Read(source, styles);
            Check(mod.AimModels[key].ANAMDataTypeState.HasFlag(AimModel.ANAMDataType.Break0), "the aim model was expected to be written the shorter way (a version break)");
            Check(!mod.AimModels[key].ConeOfFireIgnoresMovement, "the aim model's last field was expected to be false");
            Write(ctx, mod, shortPath, styles, keepHeader: true);
        }
        {
            var mod = Read(source, styles);
            mod.AimModels[key].ANAMDataTypeState = 0;       // write the whole data block: the last field is spelled out
            Write(ctx, mod, longPath, styles, keepHeader: true);
        }
        {
            var mod = Read(source, styles);
            mod.AimModels[key].ANAMDataTypeState = 0;
            mod.AimModels[key].ConeOfFireIgnoresMovement = true;   // the control: now the value really differs
            Write(ctx, mod, otherPath, styles, keepHeader: true);
        }
        var s = ctx.Session;
        var a = s.Side(shortPath); var b = s.Side(longPath);
        var rawA = a.Engine.Raw(s.FindIn(a, "000847")); var rawB = b.Engine.Raw(s.FindIn(b, "000847"));
        int anamA = rawA.Subrecords.Single(x => x.Signature == "ANAM").Length, anamB = rawB.Subrecords.Single(x => x.Signature == "ANAM").Length;
        Check(anamA == 64 && anamB == 65, $"the two written records have ANAM of {anamA} and {anamB} bytes; 64 and 65 were expected");
        Check(rawB.Data[rawB.Subrecords.Single(x => x.Signature == "ANAM").Start + 64] == 0, "the spelled-out byte isn't 0");

        var answer = ctx.Compare.Records(shortPath, "000847", longPath);
        Check(answer.Diff.Same && !answer.Diff.Values.Any(), $"the pair gave {answer.Diff.Values.Count()} value difference(s): {string.Join("; ", answer.Diff.Values.Select(d => d.Path))}");
        var layout = answer.Diff.NotValues.ToList();
        Check(layout.Count == 1 && layout[0].Class == DiffClass.Layout && layout[0].Path == "ANAMDataTypeState", $"what differs without being a value: {string.Join("; ", layout.Select(d => d.Class + " " + d.Path))}");
        string text = answer.ToText(new TextOptions { Detail = true });
        Check(text.Contains("No value differs", StringComparison.Ordinal) && text.Contains("how long a data block is written", StringComparison.Ordinal), $"the answer's text:\n{text}");
        ctx.WriteReport("defaults-pair.txt", text);
        // As two versions of one plugin: nothing changed, though the bytes differ
        var plugins = ctx.Compare.Plugins(shortPath, longPath);
        Check(plugins.Changed == 0 && plugins.Added == 0 && plugins.Removed == 0, $"the two files as versions of one plugin: {plugins.Changed} changed, {plugins.Added} added, {plugins.Removed} removed");
        Check(plugins.Unchanged - plugins.EqualBytes == 1, $"{plugins.Unchanged - plugins.EqualBytes} records had other bytes and equal values; 1 was expected");
        // Still listed, as the same value written another way (fix A2)
        string pluginsText = plugins.ToText(new TextOptions());
        Check(plugins.SameValue.Count == 1 && pluginsText.Contains("SAME     starlight_Lawgiver_SA_AM (000847:weap_starlight.esm) AMDL: no value differs; the same value, written another way: ANAMDataTypeState", StringComparison.Ordinal),
            $"the pair with the same values isn't listed as the same value written another way:\n{pluginsText}");

        var control = ctx.Compare.Records(shortPath, "000847", otherPath);
        var values = control.Diff.Values.ToList();
        Check(values.Count == 1 && values[0].Path == "ConeOfFireIgnoresMovement" && values[0].A == "false" && values[0].B == "true" && values[0].Signature == "ANAM",
            $"the control gave {string.Join("; ", values.Select(d => $"{d.Path} [{d.Signature}]: {d.A} → {d.B}"))}");
        return $"starlight_Lawgiver_SA_AM (000847:weap_starlight.esm), an AMDL, written by Mutagen twice: with its data block the older, shorter way (ANAM {anamA} bytes, the last field left out) and with the last field spelled out at its default (ANAM {anamB} bytes, the added byte 00). " +
            $"The files differ; the comparison gives 0 value differences and 1 note (\"how long a data block is written\"). As two versions of one plugin: 0 changed, {N(plugins.Unchanged)} unchanged ({N(plugins.EqualBytes)} told from equal bytes, 1 by comparing values). " +
            "Control: the same field set to true gives exactly 1 difference, ConeOfFireIgnoresMovement [ANAM]: false → true";
    }

    // ---------------------------------------------------------------- plugins: a copy with 3 edited, 1 added, 1 removed
    /// <summary>A copy of weap_starlight.esm written by Mutagen with 3 records edited, 1 removed and 1 added.</summary>
    public static string ChangedCopy(Ctx ctx, string folder) => ChangedCopy(ctx, folder, out _, out _);

    static string ChangedCopy(Ctx ctx, string folder, out int before, out FormKey added)
    {
        var styles = Styles(ctx, Small);
        string changed = Path.Combine(ctx.Folder(folder), Small);
        var mod = Read(Path.Combine(ctx.Data, Small), styles);
        before = mod.EnumerateMajorRecords().Count();
        // 3 edited
        mod.Weapons[Key("000806:weap_starlight.esm")].AttackDamage += 5;
        mod.AimModels[Key("000848:weap_starlight.esm")].RecoilArc = 33;
        var recipe = mod.ConstructibleObjects[Key("00084A:weap_starlight.esm")];
        recipe.ConstructibleComponents![1].RequiredCount = 4;
        // 1 removed
        mod.AimModels.Remove(Key("000849:weap_starlight.esm"));
        // 1 added
        added = mod.Keywords.AddNew("zz_m7_added_keyword").FormKey;
        Write(ctx, mod, changed, styles, keepHeader: true);
        return changed;
    }

    /// <summary>The harness's fixture for trap 8: a copy of kgcdoom.esm named zz_trap8.esm with one EditorID overwritten by another.</summary>
    public static string RenamedEditorId(Ctx ctx, string folder, string from, string to) => RenamedEditorId(ctx, folder, from, to, out _);

    static string RenamedEditorId(Ctx ctx, string folder, string from, string to, out int places)
    {
        string old = ctx.Copy("kgcdoom.esm", folder, "zz_trap8.esm");
        var bytes = File.ReadAllBytes(old);
        places = 0;
        for (int at = Find(bytes, Encoding.ASCII.GetBytes(from)); at >= 0; at = Find(bytes, Encoding.ASCII.GetBytes(from), at + 1)) { Encoding.ASCII.GetBytes(to).CopyTo(bytes, at); places++; }
        Check(places > 0, $"{from} wasn't found in kgcdoom.esm");
        File.WriteAllBytes(old, bytes);
        return old;
    }

    public static string PluginsMade(Ctx ctx)
    {
        string changed = ChangedCopy(ctx, "plugins-new", out int before, out var addedKey);

        var answer = ctx.Compare.Plugins(Small, changed);
        string text = answer.ToText(new TextOptions { Detail = true });
        ctx.WriteReport("plugins-made.txt", text);
        Check(answer.Mode == "versions", $"the two files were compared as {answer.Mode}");
        Check(answer.Changed == 3 && answer.Added == 1 && answer.Removed == 1, $"3 changed, 1 added and 1 removed were made; the answer has {answer.Changed}, {answer.Added} and {answer.Removed}:\n{text}");
        Check(answer.RecordsA == before && answer.RecordsB == before && answer.Unchanged == before - 4, $"records: {answer.RecordsA} and {answer.RecordsB}, {answer.Unchanged} unchanged; {before} and {before - 4} were expected");
        var rows = answer.Rows;
        var w = rows.Single(r => r.Class == PairClass.Changed && r.Signature == "WEAP");
        Check(w.Diff!.TopFields.SequenceEqual(new[] { "AttackDamage" }) && w.B!.StartsWith("weap_Starlight (000806:", StringComparison.Ordinal), $"the weapon's changes: {string.Join(", ", w.Diff.TopFields)}");
        var am = rows.Single(r => r.Class == PairClass.Changed && r.Signature == "AMDL");
        Check(am.Diff!.Values.Single().Path == "RecoilArc" && am.Diff.Values.Single().B == "33.0", $"the aim model's changes: {string.Join(", ", am.Diff.Values.Select(d => d.Path + " " + d.B))}");
        var co = rows.Single(r => r.Class == PairClass.Changed && r.Signature == "COBJ");
        Check(co.Diff!.Values.Single().Path.EndsWith("].RequiredCount", StringComparison.Ordinal) && co.Diff.Values.Single().A == "1" && co.Diff.Values.Single().B == "4", $"the recipe's changes: {string.Join(", ", co.Diff.Values.Select(d => d.Path + " " + d.A + "→" + d.B))}");
        Check(rows.Single(r => r.Class == PairClass.Removed).A == "starlight_Novalight_SA_AM (000849:weap_starlight.esm)", $"removed: {rows.Single(r => r.Class == PairClass.Removed).A}");
        Check(rows.Single(r => r.Class == PairClass.Added).B == $"zz_m7_added_keyword ({addedKey.ID:X6}:weap_starlight.esm)", $"added: {rows.Single(r => r.Class == PairClass.Added).B}");

        // The same answer when every pair is read through Mutagen and compared, with equal bytes not trusted
        ctx.Compare.TrustEqualBytes = false;
        PluginsAnswer byValues;
        try { byValues = ctx.Compare.Plugins(Small, changed); } finally { ctx.Compare.TrustEqualBytes = true; }
        Check(byValues.EqualBytes == 0, $"{byValues.EqualBytes} pairs were told equal from bytes with that switched off");
        Check(byValues.Changed == 3 && byValues.Added == 1 && byValues.Removed == 1 && byValues.Unchanged == answer.Unchanged, $"with every pair read: {byValues.Changed} changed, {byValues.Added} added, {byValues.Removed} removed, {byValues.Unchanged} unchanged");
        // The other way round: what was added is removed
        var back = ctx.Compare.Plugins(changed, Small);
        Check(back.Changed == 3 && back.Added == 1 && back.Removed == 1 && back.Rows.Single(r => r.Class == PairClass.Added).B == "starlight_Novalight_SA_AM (000849:weap_starlight.esm)", "the comparison the other way round doesn't mirror the first");
        return $"{Small} ({N(before)} records) and a copy written by Mutagen with 3 records edited, 1 removed and 1 added: the answer has exactly 3 changed (weap_Starlight (000806:weap_starlight.esm) AttackDamage; starlight_M1919_SA_AM (000848:weap_starlight.esm) RecoilArc → 33.0; weap_comod_Starlight_Barrel_Long (00084A:weap_starlight.esm) one component's RequiredCount 1 → 4), " +
            $"1 removed (starlight_Novalight_SA_AM (000849:weap_starlight.esm)), 1 added (zz_m7_added_keyword), {N(answer.Unchanged)} unchanged ({N(answer.EqualBytes)} told from equal bytes). The same 5 with every pair read and compared ({N(byValues.Unchanged - byValues.EqualBytes)} pairs compared by value), and mirrored the other way round";
    }

    static int Find(byte[] hay, byte[] needle, int from = 0)
    {
        for (int i = from; i + needle.Length <= hay.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    // ---------------------------------------------------------------- plugins: repeated EditorIDs (trap 8)
    public static string RepeatedEditorIds(Ctx ctx)
    {
        // The harness's own fixture for trap 8: in a copy of kgcdoom.esm one EditorID is overwritten with another's
        const string from = "kgcDoom_mod_Crucible_Quality_03", to = "kgcDoom_mod_Crucible_Quality_02";
        string old = RenamedEditorId(ctx, "trap8-old", from, to, out int places), fresh = ctx.Copy("kgcdoom.esm", "trap8-new", "zz_trap8.esm");

        var byEditorId = ctx.Compare.Plugins(old, fresh, MatchBy.EditorId);
        string text = byEditorId.ToText();
        ctx.WriteReport("plugins-editorid.txt", text);
        var repeats = byEditorId.Repeated;
        var mine = repeats.SingleOrDefault(r => r.EditorId == to && r.Signature == "OMOD");
        Check(mine != null && mine.InA.Count == 2 && mine.InB.Count == 1, $"the repeated EditorID {to}: {(mine == null ? "not listed" : $"{mine.InA.Count} in a, {mine.InB.Count} in b")}; listed: {string.Join(", ", repeats.Select(r => r.EditorId))}");
        // The trap's own conditions, as the harness words them
        Check(Regex.IsMatch(text, @"(?i)(repeat|duplicate|more than one|twice|shared)[^\n]*EditorID|EditorID[^\n]*(repeat|duplicate|more than once|twice|shared)") && text.Contains(to, StringComparison.Ordinal), "the answer's text doesn't warn of the repeated EditorID");
        // Never collapsed: both records of the repeated EditorID are still paired (by FormKey), and the renamed one shows as changed
        var renamed = byEditorId.Rows.Where(r => r.Class == PairClass.Changed && r.Diff!.TopFields.Contains("EditorID")).ToList();
        Check(renamed.Count >= 1 && renamed.Any(r => r.A!.StartsWith(to + " (", StringComparison.Ordinal) && r.B!.StartsWith(from + " (", StringComparison.Ordinal)), $"the renamed record isn't reported as changed: {string.Join("; ", renamed.Select(r => r.A + " → " + r.B))}");
        Check(byEditorId.Added == 0 && byEditorId.Removed == 0, $"{byEditorId.Added} added and {byEditorId.Removed} removed; no record was added or removed");
        Check(byEditorId.RecordsA == byEditorId.Unchanged + byEditorId.Changed, $"{byEditorId.RecordsA} records in a, but {byEditorId.Unchanged} unchanged and {byEditorId.Changed} changed");
        // Other repeated EditorIDs the plugin has on its own are listed too, none dropped
        int own = repeats.Count - 1;

        var byFormKey = ctx.Compare.Plugins(old, fresh);
        Check(byFormKey.Repeated.Count == 0 && byFormKey.Changed == byEditorId.Changed && byFormKey.Added == 0 && byFormKey.Removed == 0, $"by FormKey: {byFormKey.Changed} changed, {byFormKey.Added} added, {byFormKey.Removed} removed");
        return $"a copy of kgcdoom.esm ({N(byEditorId.RecordsA)} records) with the EditorID {from} overwritten by {to} ({places} place(s) in the file), against the plain copy, paired by EditorID: " +
            $"{repeats.Count} repeated EditorID(s) listed ({N(byEditorId.RepeatedRecords)} records), among them {to} (OMOD) with 2 records in a ({string.Join(", ", mine!.InA)}) and 1 in b; those records are paired by FormKey, none is dropped: " +
            $"{byEditorId.Changed} changed (the renamed record among them), 0 added, 0 removed, {N(byEditorId.Unchanged)} unchanged. {(own > 0 ? $"{own} more EditorID(s) are repeated in kgcdoom.esm as it is. " : "")}Paired by FormKey (the default): the same {byFormKey.Changed} changed, and no warning needed";
    }

    // ---------------------------------------------------------------- patch_vs_reference: a made pair
    public static string PatchMade(Ctx ctx)
    {
        const string nameA = "zz_m7_patch_a.esm", nameB = "zz_m7_patch_b.esm";
        string folder = ctx.Folder("patch-made");
        var styles = Styles(ctx, Small, nameA, nameB);
        var source = Read(Path.Combine(ctx.Data, Small), styles);
        var k1 = Key("04A0A1:Starfield.esm").ToLink<IKeywordGetter>(); var k2 = Key("04A0A2:Starfield.esm").ToLink<IKeywordGetter>(); var k3 = Key("092A86:Starfield.esm").ToLink<IKeywordGetter>();
        FormKey w1 = Key("000806:weap_starlight.esm"), w2 = Key("000839:weap_starlight.esm"), w3 = Key("000876:weap_starlight.esm");
        FormKey a1 = Key("000847:weap_starlight.esm"), a2 = Key("000848:weap_starlight.esm"), a3 = Key("000849:weap_starlight.esm"), c1 = Key("00084A:weap_starlight.esm");
        foreach (var k in new[] { k1, k2, k3 }) Check(!source.Weapons[w3].Keywords!.Any(x => x.FormKey == k.FormKey), $"the weapon already has the keyword {k.FormKey}");

        var a = new StarfieldMod(ModKey.FromFileName(nameA), StarfieldRelease.Starfield);
        var b = new StarfieldMod(ModKey.FromFileName(nameB), StarfieldRelease.Starfield);
        // R1: only a holds it, and changes one field
        a.Weapons.GetOrAddAsOverride(source.Weapons[w1]).AttackDamage = 41;
        // R2: both make the same change
        a.Weapons.GetOrAddAsOverride(source.Weapons[w2]).AttackDamage = 77;
        b.Weapons.GetOrAddAsOverride(source.Weapons[w2]).AttackDamage = 77;
        // R3: both change one field to different values; both add keyword 1; a adds keyword 2, b adds keyword 3
        var wa = a.Weapons.GetOrAddAsOverride(source.Weapons[w3]); wa.AttackDamage = 50; wa.Keywords!.Add(k1); wa.Keywords.Add(k2);
        var wb = b.Weapons.GetOrAddAsOverride(source.Weapons[w3]); wb.AttackDamage = 60; wb.Keywords!.Add(k3); wb.Keywords.Add(k1);
        // R4: each changes another field
        a.AimModels.GetOrAddAsOverride(source.AimModels[a1]).ConeOfFireDegreesX = 9;
        b.AimModels.GetOrAddAsOverride(source.AimModels[a1]).RecoilArc = 9;
        // R5: only b holds it
        b.AimModels.GetOrAddAsOverride(source.AimModels[a2]).RecoilArc = 3;
        // R6: both hold a copy that changes nothing
        a.AimModels.GetOrAddAsOverride(source.AimModels[a3]);
        b.AimModels.GetOrAddAsOverride(source.AimModels[a3]);
        // R7: both change the same entry of a list paired by its key, to different values
        a.ConstructibleObjects.GetOrAddAsOverride(source.ConstructibleObjects[c1]).ConstructibleComponents![0].RequiredCount = 7;
        b.ConstructibleObjects.GetOrAddAsOverride(source.ConstructibleObjects[c1]).ConstructibleComponents![0].RequiredCount = 9;
        // New records: one with the same EditorID and content in both, one only in b, one in both that differs
        a.Keywords.AddNew("zz_m7_new_same"); b.Keywords.AddNew("zz_m7_new_same");
        b.Keywords.AddNew("zz_m7_new_only_b");
        a.Globals.AddNew("zz_m7_new_differs").Data = 1;
        b.Globals.AddNew("zz_m7_new_differs").Data = 2;
        string pathA = Path.Combine(folder, nameA), pathB = Path.Combine(folder, nameB);
        Write(ctx, a, pathA, styles, keepHeader: false);
        Write(ctx, b, pathB, styles, keepHeader: false);

        var answer = ctx.Compare.PatchVsReference(pathA, pathB);
        string text = answer.ToText(new TextOptions { Detail = true });
        ctx.WriteReport("patch-made.txt", text);
        string got = $"{answer.Rows.Count} records: {answer.Both} by both ({answer.Count(PatchRecordClass.Same)} same, {answer.Count(PatchRecordClass.Differ)} differ), {answer.Count(PatchRecordClass.OnlyA)} only in a, {answer.Count(PatchRecordClass.OnlyB)} only in b; " +
            $"fields: {answer.Count(FieldClass.SameChange)} same change, {answer.Count(FieldClass.OnlyInA)} only in a, {answer.Count(FieldClass.OnlyInB)} only in b, {answer.Count(FieldClass.Different)} different";
        const string want = "7 records: 5 by both (2 same, 3 differ), 1 only in a, 1 only in b; fields: 2 same change, 3 only in a, 3 only in b, 2 different";
        Check(got == want, $"made: {want}\n     answer: {got}\n{text}");
        Check(answer.Reconciled == 0, $"{answer.Reconciled} record(s) needed the direct comparison to settle");
        PatchRow Row(FormKey k) => answer.Rows.Single(r => r.FormKey.Equals($"{k.ID:X6}:{k.ModKey.FileName}", StringComparison.OrdinalIgnoreCase));
        string Fields(PatchRow r) => string.Join("; ", r.Fields.Select(f => $"{f.Path}{(f.IsEntry ? " " + (f.Any.Change == Change.OnlyA ? "-" : "+") + f.Any.Element : "")} [{f.Class}] base {f.Base} a {f.ValueA} b {f.ValueB}"));
        var r1 = Row(w1); Check(r1.Class == PatchRecordClass.OnlyA && r1.Fields.Single() is { Class: FieldClass.OnlyInA, Path: "AttackDamage", ValueA: "41.0", ValueB: "(as base)" }, $"R1: {r1.Class} {Fields(r1)}");
        var r2 = Row(w2); Check(r2.Class == PatchRecordClass.Same && r2.Fields.Single() is { Class: FieldClass.SameChange, Path: "AttackDamage", ValueA: "77.0", ValueB: "77.0" }, $"R2: {r2.Class} {Fields(r2)}");
        var r3 = Row(w3);
        Check(r3.Class == PatchRecordClass.Differ && r3.Fields.Count == 4, $"R3: {r3.Class} {Fields(r3)}");
        Check(r3.Fields.Single(f => f.Path == "AttackDamage") is { Class: FieldClass.Different, ValueA: "50.0", ValueB: "60.0" }, $"R3 damage: {Fields(r3)}");
        Check(r3.Fields.Single(f => f.Any.Element == "04A0A1:STARFIELD.ESM").Class == FieldClass.SameChange && r3.Fields.Single(f => f.Any.Element == "04A0A2:STARFIELD.ESM").Class == FieldClass.OnlyInA
            && r3.Fields.Single(f => f.Any.Element == "092A86:STARFIELD.ESM").Class == FieldClass.OnlyInB, $"R3 keywords: {Fields(r3)}");
        var r4 = Row(a1); Check(r4.Class == PatchRecordClass.Differ && r4.Fields.Single(f => f.Path == "ConeOfFireDegreesX").Class == FieldClass.OnlyInA && r4.Fields.Single(f => f.Path == "RecoilArc").Class == FieldClass.OnlyInB && r4.Fields.Count == 2, $"R4: {r4.Class} {Fields(r4)}");
        var r5 = Row(a2); Check(r5.Class == PatchRecordClass.OnlyB && r5.Fields.Single() is { Class: FieldClass.OnlyInB, Path: "RecoilArc", ValueB: "3.0" }, $"R5: {r5.Class} {Fields(r5)}");
        var r6 = Row(a3); Check(r6.Class == PatchRecordClass.Same && r6.Fields.Count == 0 && r6.ItmInA && r6.ItmInB, $"R6: {r6.Class} {Fields(r6)}, ITM {r6.ItmInA}/{r6.ItmInB}");
        var r7 = Row(c1); Check(r7.Class == PatchRecordClass.Differ && r7.Fields.Single() is { Class: FieldClass.Different, Base: "1", ValueA: "7", ValueB: "9" } && r7.Fields[0].Path.EndsWith("].RequiredCount", StringComparison.Ordinal), $"R7: {r7.Class} {Fields(r7)}");
        Check(answer.Rows.All(r => r.BaseA is null or Small && r.BaseB is null or Small), "an override was held against another plugin than weap_starlight.esm");
        Check(text.Contains("Summary: 7 records overridden by at least one of the two: 5 by both (2 the same, 3 differ), 1 only in a, 1 only in b.", StringComparison.Ordinal), "the summary line isn't as expected");
        Check(answer.NewRecords.Sum(n => n.InA) == 2 && answer.NewRecords.Sum(n => n.InB) == 3, $"new records: {answer.NewRecords.Sum(n => n.InA)} in a, {answer.NewRecords.Sum(n => n.InB)} in b");

        // With EditorID pairing the patches' own records are compared too
        var paired = ctx.Compare.PatchVsReference(pathA, pathB, MatchBy.EditorId);
        Check(paired.NewSame == 1 && paired.NewPairs.Count(p => p.Class == PairClass.Changed) == 1 && paired.NewPairs.Count(p => p.Class == PairClass.Added) == 1 && paired.NewPairs.Count(p => p.Class == PairClass.Removed) == 0,
            $"new records paired by EditorID: {paired.NewSame} same, {paired.NewPairs.Count(p => p.Class == PairClass.Changed)} differ, {paired.NewPairs.Count(p => p.Class == PairClass.Removed)} only in a, {paired.NewPairs.Count(p => p.Class == PairClass.Added)} only in b");
        Check(paired.NewPairs.Single(p => p.Class == PairClass.Changed).Diff!.TopFields.SequenceEqual(new[] { "Data" }), "the new record that differs doesn't differ in Data alone");
        return $"two patches for {Small} written by Mutagen, with 7 overridden records made to a plan. The answer: {got}. Every record and field is in the class it was made for: " +
            "only a changes R1; both make the same change to R2; R3 gets another damage in each (different), keyword WeaponTypeRifle (04A0A1:Starfield.esm) in both (same change) and one more keyword in each (only in a, only in b); R4 gets another field changed in each; only b holds R5; both hold R6 unchanged (ITM in both, the same); " +
            $"R7 gets one recipe component's count set to 7 and to 9 (different, base 1). New records: 2 in a, 3 in b; paired by EditorID when asked: {paired.NewSame} the same, 1 differs (in Data), 1 only in b";
    }

    // ---------------------------------------------------------------- Texts: languages only (fix A2, 2026-11-12)
    /// <summary>
    /// An override that changes only a localized text's languages is a change of that field, never an ITM.
    /// MissionCargoType01 (0642A4:Starfield.esm) has its name in 9 languages, each its own text, and no other text.
    /// Copied unchanged by Mutagen into a plugin that isn't localized, it keeps the English name alone: German
    /// players then see the English name. The control, a material swap with no text copied the same way, is an ITM.
    /// </summary>
    public static string LanguagesOnly(Ctx ctx)
    {
        const string misc = "0642A4:Starfield.esm", swap = "03FD14:Starfield.esm", name = "zz_fix_languages_only.esm";
        var s = ctx.Session;
        s.Engine.Sessions.Use("Starfield.esm");
        var miscRec = (IMiscItemGetter)s.Engine.Load("Starfield.esm", misc).Record;
        var swapRec = (ILayeredMaterialSwapGetter)s.Engine.Load("Starfield.esm", swap).Record;
        var mod = new StarfieldMod(ModKey.FromFileName(name), StarfieldRelease.Starfield);
        mod.MiscItems.GetOrAddAsOverride(miscRec);
        mod.LayeredMaterialSwaps.GetOrAddAsOverride(swapRec);
        string path = Path.Combine(ctx.Folder("languages-only"), name);
        Write(ctx, mod, path, new List<KeyedMasterStyle> { new(ModKey.FromFileName("Starfield.esm"), MutagenStyle.Full), new(mod.ModKey, MutagenStyle.Full) }, keepHeader: false);

        var answer = ctx.Compare.PluginVsMasters(path);
        string compact = answer.ToText(new TextOptions()), detail = answer.ToText(new TextOptions { Detail = true });
        ctx.WriteReport("languages-only.txt", compact + "\n" + detail);
        var row = answer.Overrides.Single(r => r.FormKey.Equals(misc, StringComparison.OrdinalIgnoreCase));
        Check(row.Class == OverrideClass.Changed && row.Diff!.TopFields.SequenceEqual(new[] { "Name" }), $"the copied MissionCargoType01 is {row.Class}, fields {string.Join(", ", row.Diff?.TopFields ?? new())}:\n{detail}");
        var d = row.Diff!.Values.Single();
        Check(d.Note != null && d.Note.Contains("the same English text", StringComparison.Ordinal) && d.Note.Contains("German", StringComparison.Ordinal), $"the name's difference doesn't say what changed: {d.A} → {d.B} [{d.Note}]");
        Check(answer.Itms.Count() == 1 && answer.Itms.Single().FormKey.Equals(swap, StringComparison.OrdinalIgnoreCase), $"ITMs: {string.Join(", ", answer.Itms.Select(r => r.Label))}; only the material swap was expected");
        Check(compact.Contains("MissionCargoType01 (0642A4:Starfield.esm) MISC, against Starfield.esm: Name", StringComparison.Ordinal), $"the compact answer doesn't list the name as changed:\n{compact}");
        // The same through the other kinds: a direct comparison and two versions of the record
        var records = ctx.Compare.Records("Starfield.esm", misc, path);
        Check(!records.Diff.Same && records.Diff.TopFields.SequenceEqual(new[] { "Name" }), $"records: {string.Join(", ", records.Diff.TopFields)}");
        return $"MissionCargoType01 ({misc}), its name in 9 languages, copied unchanged by Mutagen into a plugin that isn't localized: plugin_vs_masters calls it changed, in Name ({d.A} → {d.B}; {d.Note}), and the compact line names Name; compare records the same. " +
            $"The control, Ship_ShipsBlackMetalHeavyScratched01_P ({swap}), copied the same way: the 1 ITM";
    }

    // ---------------------------------------------------------------- Trap 12
    public static string Trap12(Ctx ctx)
    {
        const string patch = "darkstarpatch_atom74.esm";     // only read, where it is
        string renamed = ctx.Copy("atom74.esm", "trap12", "zrenamed_atom74.esm");
        CompareException? error = null;
        try { ctx.Compare.Plugins(renamed, patch); } catch (CompareException e) { error = e; }
        Check(error != null, "comparing a patch with its master under another name gave an answer; a clear error was expected");
        string line = error!.Line;
        Check(Regex.IsMatch(line, @"(?im)^Error:[^\n]*\batom74\.esm") && !line.Contains('\n'), $"the error doesn't name atom74.esm on one line: {line}");
        Check(line.Contains("as=\"atom74.esm\"", StringComparison.Ordinal) && line.Contains("Fix:", StringComparison.Ordinal), $"the error doesn't say how to pair them on purpose: {line}");
        // A copy with other content and a name that gives no hint: the error still lists the masters the patch expects
        string odd = ctx.Copy("weap_starlight.esm", "trap12", "zz_something.esm");
        CompareException? second = null;
        try { ctx.Compare.Plugins(odd, patch); } catch (CompareException e) { second = e; }
        Check(second != null && second.Line.Contains("atom74.esm", StringComparison.Ordinal) && second.Line.Contains("as=\"", StringComparison.Ordinal), $"a file that is no master of the patch: {second?.Line}");

        // as="atom74.esm" pairs them on purpose: the same answer as with the real atom74.esm
        var asked = ctx.Compare.Plugins(renamed, patch, readAs: "atom74.esm");
        var real = ctx.Compare.Plugins("atom74.esm", patch);
        Check(asked.Mode == "patch" && real.Mode == "patch", $"modes: {asked.Mode} and {real.Mode}");
        Check(asked.Changed == real.Changed && asked.NotInPatch == real.NotInPatch && asked.NotOfPlugin == real.NotOfPlugin && asked.Unchanged == real.Unchanged && real.Changed > 0,
            $"with as=: {asked.Changed} changed, {asked.NotInPatch} not in the patch; with the real master: {real.Changed} and {real.NotInPatch}");
        Check(asked.Rows.Select(r => r.Label + "|" + string.Join(",", r.Diff?.TopFields ?? new())).SequenceEqual(real.Rows.Select(r => r.Label + "|" + string.Join(",", r.Diff?.TopFields ?? new()))), "the rows with as= aren't the rows with the real master");
        Check(asked.Added == 0 && asked.Removed == 0, "a patch against its master lists records as added or removed");
        Check(File.Exists(Path.Combine(ctx.Data, patch)) && !File.Exists(Path.Combine(ctx.Data, "zrenamed_atom74.esm")), "something was written to the Data folder");
        return $"a copy of atom74.esm named zrenamed_atom74.esm against {patch}: stops with \"{line}\" " +
            $"With as=\"atom74.esm\": {asked.Changed} record changed, {N(asked.NotInPatch)} records of atom74.esm the patch doesn't hold left out, {N(asked.NotOfPlugin)} records of the patch that aren't atom74.esm's left out, nothing listed as added or removed: the same answer as with atom74.esm itself. " +
            $"A file that is no master of the patch stops too, and lists the masters the patch expects";
    }
}
