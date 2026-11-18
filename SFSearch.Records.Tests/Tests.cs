using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Strings;
using SFSearch.Index;
using SFSearch.Records;
using MasterStyle = SFSearch.Index.MasterStyle;

namespace SFSearch.Records.Tests;

static class Tests
{
    static readonly string[] Mods = { "weap_starlight.esm", "kgcdoom.esm", "darkstar.esm" };
    const string Starfield = "Starfield.esm";

    static void Check(bool ok, string what) => Ctx.Check(ok, what);

    /// <summary>The one function every new tool writes a size with: 1 KB is 1,024 bytes; whole KB rounded down; MB with one decimal, GB with two.</summary>
    public static string Sizes(Ctx ctx)
    {
        var cases = new (long Bytes, string Text)[]
        {
            (0, "0 bytes"), (1, "1 byte"), (1023, "1023 bytes"), (1024, "1 KB"), (2047, "1 KB"), (53_235, "51 KB"), (84_868, "82 KB"),
            (1_048_575, "1023 KB"), (1_048_576, "1 MB"), (3_661_403, "3.5 MB"), (7_340_032, "7 MB"), (26_474_052, "25.2 MB"),
            (1_073_741_823, "1024 MB"), (1_073_741_824, "1 GB"), (1_457_098_709, "1.36 GB"), (172_950_000_000, "161.07 GB"),
        };
        foreach (var (bytes, text) in cases)
            Check(SFSearch.Records.Sizes.Text(bytes) == text, $"{bytes:N0} bytes is written \"{SFSearch.Records.Sizes.Text(bytes)}\", not \"{text}\"");
        return $"{cases.Length} sizes, each written the one way: bytes under 1 KB, whole KB rounded down under 1 MB, one decimal for MB, two for GB.";
    }
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    static long PrivateMb() { using var p = Process.GetCurrentProcess(); return p.PrivateMemorySize64 / 1048576; }
    static long HeapMb() => GC.GetTotalMemory(true) / 1048576;

    /// <summary>Plugins the tests never copy or change, by the user's rule.</summary>
    static bool HandsOff(string name) => name.StartsWith("darkstarpatch_", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(name, @"^sfta0[1-6]\.esm$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static void MustBeACopy(Ctx ctx, string path)
    {
        if (!Path.GetFullPath(path).StartsWith(ctx.Work + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"refusing to change {path}: it isn't inside the tests' work folder");
    }

    static List<RecordRef> AllOf(HeaderIndex index, string plugin)
    {
        var p = index.Plugin(plugin) ?? throw new TestFailed($"{plugin} isn't in the Data folder");
        var list = new List<RecordRef>(p.Segment!.Count);
        for (int i = 0; i < p.Segment.Count; i++) list.Add(new RecordRef(p.Id, i));
        return list;
    }

    /// <summary>Up to <paramref name="max"/> records, evenly spread over the list: the same records on every run.</summary>
    internal static List<RecordRef> Sample(List<RecordRef> all, int max)
    {
        if (all.Count <= max) return all;
        var pick = new List<RecordRef>(max);
        for (int i = 0; i < max; i++) pick.Add(all[(int)((long)i * all.Count / max)]);
        return pick;
    }

    internal static Dictionary<string, List<RecordRef>> ByType(HeaderIndex index, string plugin) =>
        index.Stats(plugin).ToDictionary(s => s.Signature, s => index.RecordsOfType(plugin, s.Signature).ToList(), StringComparer.Ordinal);

    // ---------------------------------------------------------------- A known case
    public static string KnownWeapon(Ctx ctx)
    {
        var e = ctx.Engine;
        e.Sessions.Use("darkstar.esm");
        var rec = e.Load("darkstar.esm", "0003C2:darkstar.esm");
        Check(rec.Label == "DarkStar_Mk3_MagShear (0003C2:darkstar.esm)", $"the record is named {rec.Label}");
        // A list whose items are of several kinds (this weapon has an AnimationGraphComponent and an EffectSequenceComponent):
        // [*] gives the items that have the field, and passes over the others. (Found in M5: it stopped at the first other kind.)
        var mixed = e.Fields(rec, new[] { "Components[*].Reflection" });
        Check(mixed.Count == 1 && mixed[0].IsSet && mixed[0].Path == "Components[1].Reflection", $"Components[*].Reflection gives {mixed.Count} value(s), the first at {mixed[0].Path}");
        var noSuch = e.Fields(rec, new[] { "Components[*].NoSuchField" });
        Check(noSuch.Count == 1 && !noSuch[0].IsSet && (noSuch[0].Note ?? "").Contains("other kinds", StringComparison.Ordinal), $"Components[*].NoSuchField answers: {noSuch[0].Note}");
        Check(rec.Type == "Weapon" && rec.Info.Signature == "WEAP" && rec.Route == ReadRoute.Group, $"it is a {rec.Info.Signature} {rec.Type}, read by {rec.Route}");
        var json = e.Json(rec);
        Check(json["AttackDamage"]!.ToJsonString() == "12.0", $"AttackDamage is {json["AttackDamage"]?.ToJsonString()} in the JSON");
        var ev = e.Evidence(rec, "AttackDamage");
        Check(ev.RecordSignature == "WEAP" && ev.Subrecord == "WDMG" && ev.Offset == 0 && ev.Size == 4, $"evidence gives {ev.RecordSignature}/{ev.Subrecord} offset {ev.Offset}, {ev.Size} bytes");
        Check(ev.XEdit == @"\WDMG - Damage\Base Damage", $"the xEdit path is {ev.XEdit}");
        Check(ev.Value == "12.0" && ev.Field == "Weapon.AttackDamage" && ev.Kind == "Float", $"evidence says {ev.Field} ({ev.Kind}) = {ev.Value}");
        Check(ev.Slices.Count == 1 && ev.Slices[0].FieldBytes == "00 00 40 41", $"the slice is {ev.Slices.FirstOrDefault()?.FieldBytes}");
        Check(BitConverter.ToSingle(new byte[] { 0x00, 0x00, 0x40, 0x41 }) == 12.0f, "00 00 40 41 isn't 12.0");
        string text = ev.ToText();
        foreach (var want in new[] { "AttackDamage = 12.0", "WEAP/WDMG, offset 0, 4 bytes: 00 00 40 41", "Weapon.AttackDamage (Float)", @"\WDMG - Damage\Base Damage" })
            Check(text.Contains(want, StringComparison.Ordinal), $"the evidence text lacks \"{want}\":\n{text}");
        // The same field by its readable name, and a link whose slice is read through the header index
        var byName = e.Evidence(rec, "Base Damage");
        Check(byName.Field == "Weapon.AttackDamage", $"\"Base Damage\" leads to {byName.Field}");
        var ammo = e.Evidence(rec, "AmmoType");
        Check(ammo.Slices[0].FieldBytes == "E3 03 00 02" && ammo.Slices[0].Reads == "ammo_DarkStar_VolatileMIArray50 (0003E3:darkstar.esm)", $"AmmoType's slice {ammo.Slices[0].FieldBytes} reads as {ammo.Slices[0].Reads}");
        var card = e.Card(rec, json);
        string cardText = card.ToText();
        Check(cardText.Contains("Base Damage (AttackDamage, WDMG): 12.0", StringComparison.Ordinal), "the card lacks the line \"Base Damage (AttackDamage, WDMG): 12.0\"");
        Check(cardText.Contains("[23] WeaponTypeRifle (04A0A1:Starfield.esm)", StringComparison.Ordinal), "the card doesn't label the keyword 04A0A1:Starfield.esm as WeaponTypeRifle");
        // Fields mode: a value the JSON leaves out is the default
        Check(json["HasScope"] == null, "this record's JSON was expected to leave HasScope out");
        var fields = e.Fields(rec, new[] { "AttackDamage", "HasScope", "WAMM" });
        Check(fields[0].IsSet && fields[0].Text == "12.0", $"fields: AttackDamage = {fields[0].Text}");
        Check(!fields[1].IsSet && fields[1].IsDefault && fields[1].Text == "false", $"fields: HasScope = {fields[1].Text} (default {fields[1].IsDefault})");
        Check(!fields[2].IsSet && !fields[2].IsDefault && fields[2].Json == null, $"fields: WAMM = {fields[2].Text}");
        Check(((IWeaponGetter)rec.Record).HasScope == false, "Mutagen says HasScope is true");
        string wrong = "";
        try { e.Fields(rec, new[] { "BaseDamage" }); } catch (RecordException x) { wrong = x.Message; }
        // The nearest real names, chosen and worded as describe and the SQL layer do it (SFSearch.SchemaGen.NearNames)
        Check(wrong == "Weapon has no field \"BaseDamage\". Did you mean: AttackDamage (Float, WDMG, xEdit name \"Base Damage\"); MeleeBashDamage (Float, WMEL, xEdit name \"Bash Damage\"); BaseValue (UInt32, WGEN)?", $"a wrong field name answers: {wrong}");
        string nested = "";
        try { e.Fields(rec, new[] { "Model.Fil" }); } catch (RecordException x) { nested = x.Message; }
        Check(nested.StartsWith("Weapon.Model (Model) has no field \"Fil\". Did you mean: File (AssetLink, MODL", StringComparison.Ordinal), $"a wrong name inside an object answers: {nested}");
        string far = "";
        try { e.Fields(rec, new[] { "zzzz" }); } catch (RecordException x) { far = x.Message; }
        Check(far == "Weapon has no field \"zzzz\".", $"a name that is nowhere near a field answers: {far}");
        var verify = e.Verify(new[] { rec.Ref })[0];
        Check(verify.Equal, $"the one-record verify: {verify.ToText()}");
        // The verify must catch a record that doesn't write back as the file holds it. J37: no hook in the verifier; a copy
        // of the plugin with the weapon's bytes changed in place (size kept), read and verified like any plugin.
        // The keyword count one less than the keywords the weapon holds: Mutagen writes the count of what it read
        var changed = VerifyChangedCopy(ctx, rec, "ksiz", (raw, write) => write(raw.Subrecords.First(s => s.Signature == "KSIZ").Start, BitConverter.GetBytes(BitConverter.ToUInt32(raw.Content(raw.Subrecords.First(s => s.Signature == "KSIZ"))) - 1)));
        Check(!changed.Equal && (changed.Cause ?? "").StartsWith("WEAP/K", StringComparison.Ordinal), $"a keyword count one short gave: {changed.ToText()}");
        // The weapon's last subrecord renamed to a signature Mutagen doesn't know: its read leaves it out
        var dropped = VerifyChangedCopy(ctx, rec, "zzzz", (raw, write) => write(raw.Subrecords[^1].Start - 6, "ZZZZ"u8.ToArray()));
        Check(!dropped.Equal && (dropped.Detail ?? "").Contains("the file has ZZZZ", StringComparison.Ordinal), $"an unknown last subrecord gave: {dropped.ToText()}");
        return $"{rec.Label} is a {rec.Type}; AttackDamage 12.0; evidence WEAP/WDMG offset 0, 4 bytes (00 00 40 41), xEdit {ev.XEdit}; card of {card.Values().Count()} values; HasScope answered false as the default; verify equal over {verify.Bytes:N0} bytes. Controls (copies of the plugin with the weapon's bytes changed): a keyword count one short is caught: {changed.Cause} (file {changed.FileBytes}, written {changed.WrittenBytes}); an unknown last subrecord is caught: {dropped.Cause} (file {dropped.FileBytes}, written {dropped.WrittenBytes})";
    }

    /// <summary>
    /// A copy of the record's plugin in the work folder, with bytes of the record changed in place (the size kept), read
    /// through an index and an engine of its own, and the record verified there. <paramref name="change"/> gets the
    /// record's subrecords and writes bytes at a position counted from the start of the record's data.
    /// </summary>
    static VerifyResult VerifyChangedCopy(Ctx ctx, LoadedRecord rec, string name, Action<RawRecord, Action<int, byte[]>> change)
    {
        var data = ctx.DataIndex;
        var source = data.Plugin(rec.Info.Plugin)!;
        Check(!HandsOff(source.Name), "the plugin to copy is one the tests must not touch");
        string copy = Path.Combine(ctx.Folder("verify-control-" + name), source.Name);
        File.Copy(source.Path, copy, overwrite: true);
        MustBeACopy(ctx, copy);
        var raw = ctx.Engine.Raw(rec.Ref);
        Check(!raw.Compressed, $"{rec.Label} is compressed: its bytes can't be changed in place");
        using (var fs = new FileStream(copy, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            change(raw, (at, bytes) => { fs.Position = raw.Offset + RawRecord.HeaderLength + at; fs.Write(bytes); });
        var files = new List<string> { copy };
        files.AddRange(source.Header!.Masters.Select(m => data.Plugin(m)!.Path));
        using var index = new HeaderIndex(PluginSource.FromFiles(files), ctx.Options());
        index.Refresh();
        using var engine = new RecordEngine(index, ctx.Schema, Path.Combine(ctx.Work, "verify-control-" + name + "-scratch"), ctx.Data);
        engine.Sessions.Use(source.Name);
        return engine.Verify(new[] { engine.Find(source.Name, rec.Info.FormKey) })[0];
    }

    // ---------------------------------------------------------------- Properties Mutagen adds beyond its XML (GAPS Part 2 row 8)
    /// <summary>MajorFlags, IsCompressed, IsDeleted, Versioning, &lt;Sig&gt;DataTypeState and RegionData.DataType are
    /// fields of the schema (derived from Mutagen's generator rules): a card shows them as fields, and fields mode reads
    /// one the JSON doesn't carry from Mutagen's record (RegionData.DataType is read-only, the serializer never writes it).</summary>
    public static string DerivedFields(Ctx ctx)
    {
        var e = ctx.Engine;
        e.Sessions.Use("Starfield.esm");
        var eon = e.Load("Starfield.esm", "0476C4:Starfield.esm");
        var card = e.Card(eon);
        var flags = card.Fields.FirstOrDefault(f => f.Name == "MajorFlags");
        // Eon sets 0x40000000, which Mutagen names HighResFirstPersonOnly since fixes.19 (C54; it named 0x20000000 before)
        Check(flags != null && flags.InSchema && flags.Text == "[HighResFirstPersonOnly]", $"Eon's MajorFlags on its card: {(flags == null ? "missing" : $"{flags.Text}, in the schema {flags.InSchema}")}");
        Check(card.Values().All(f => f.InSchema) && !card.ToText().Contains("[not in the schema]", StringComparison.Ordinal), "Eon's card still marks a value \"[not in the schema]\"");
        var region = e.Load("Starfield.esm", "045A50:Starfield.esm");
        var v = e.Fields(region, new[] { "Weather.DataType", "Weather.Priority", "IsCompressed", "IsDeleted" });
        Check(v[0].IsSet && v[0].Text == "Weather" && !v[0].IsDefault, $"Region112's Weather.DataType: {v[0].Text} (set {v[0].IsSet}, default {v[0].IsDefault})");
        Check(v[1].Text == "50" && v[2].Text == "false" && v[3].Text == "false", $"Region112's Priority, IsCompressed, IsDeleted: {v[1].Text}, {v[2].Text}, {v[3].Text}");
        var ev = e.Evidence(region, "Weather.DataType");
        Check(ev.Subrecord == "RDAT" && ev.Value == "Weather" && ev.Slices.Count == 1 && ev.Slices[0].SubrecordBytes.StartsWith("03 00 00 00", StringComparison.Ordinal),
            $"Region112's DataType evidence: {ev.Subrecord}, {ev.Value}, {ev.Slices.FirstOrDefault()?.SubrecordBytes}");
        var sounds = e.Fields(e.Load("Starfield.esm", "117EBA:Starfield.esm"), new[] { "Sounds.DataType" })[0];
        Check(sounds.Text == "Sound", $"AudioExtGoodneighbor's Sounds.DataType: {sounds.Text}");
        return $"Eon's card: MajorFlags {flags!.Text} in the schema, no value marked \"[not in the schema]\" ({card.Values().Count()} values); Region112 Weather.DataType {v[0].Text} (RDAT 03 00 00 00), AudioExtGoodneighbor Sounds.DataType {sounds.Text}, read from Mutagen's record";
    }

    // ---------------------------------------------------------------- A field Mutagen still calls unknown
    public static string UnknownField(Ctx ctx)
    {
        var e = ctx.Engine;
        e.Sessions.Use("darkstar.esm", Starfield);
        // 1. The value the task names: WTRM's fifth field was "WTRMUnknown5" when today's SFSearch dropped it.
        //    Mutagen has since named it AiFiringArc. It is 30.0 on this record and it must be on the card.
        var magshear = e.Load("darkstar.esm", "0003C2:darkstar.esm");
        var card = e.Card(magshear);
        var arc = card.Values().FirstOrDefault(f => f.Path == "AiFiringArc");
        Check(arc != null && arc.Text == "30.0" && arc.Signature == "WTRM", $"AiFiringArc on the card: {arc?.Text} [{arc?.Signature}]");
        var ev = e.Evidence(magshear, "AiFiringArc");
        Check(ev.Subrecord == "WTRM" && ev.Offset == 13 && ev.Slices[0].FieldBytes == "00 00 F0 41", $"AiFiringArc sits at WTRM offset {ev.Offset}: {ev.Slices[0].FieldBytes}");
        Check(e.Schema.Model.Resolve("Weapon", "WTRMUnknown5").Found.Count == 0, "the schema still has a field WTRMUnknown5");

        // 2. The nearest real case: fields Mutagen still calls unknown, on every weapon and on sampled records of
        //    five more types of Starfield.esm
        int records = 0, withUnknown = 0, unknownValues = 0, nonZero = 0, fromFile = 0;
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        CardField? example = null; string exampleLabel = ""; RecordRef exampleRef = default;
        var unknownTypes = ByType(ctx.DataIndex, Starfield);
        var sample = unknownTypes["WEAP"].Concat(new[] { "NPC_", "ARMO", "PERK", "RACE", "LIGH" }.SelectMany(s => Sample(unknownTypes[s], 300))).ToList();
        foreach (var r in sample)
        {
            var rec = e.Load(r);
            var c = e.Card(rec);
            records++;
            var unknown = c.Values().Where(f => f.Unknown).ToList();
            if (unknown.Count > 0) withUnknown++;
            foreach (var f in unknown)
            {
                unknownValues++;
                Check(f.Bytes != null, $"{rec.Label}: the unknown field {f.Path} has no bytes");
                string key = System.Text.RegularExpressions.Regex.Replace(f.Path, @"\[\d+\]", "[*]");
                names[key] = names.GetValueOrDefault(key) + 1;
                bool zero = f.Bytes!.Replace(" ", "").All(ch => ch == '0');
                if (!zero) nonZero++;
                if (!zero && example == null && f.Signature != null && !f.Path.Contains('[')) { example = f; exampleLabel = rec.Label; exampleRef = r; }
            }
            string text = c.ToText();
            foreach (var f in unknown) Check(text.Contains("[unknown field; bytes " + f.Bytes, StringComparison.Ordinal), $"{rec.Label}: the card's text doesn't show {f.Path} with its bytes");
        }
        Check(example != null, "no sampled record of Starfield.esm has an unknown field with a value other than zero");
        // The example's bytes are in the file's own subrecord
        var raw = e.Raw(exampleRef);
        var sub = raw.Subrecords.Where(s => s.Signature == example!.Signature).ToList();
        string hex = sub.Count == 0 ? "" : RawRecord.Hex(raw.Content(sub[0]));
        Check(sub.Count > 0 && hex.Contains(example!.Bytes!, StringComparison.Ordinal), $"{exampleLabel}: the bytes {example!.Bytes} of {example.Path} aren't in the file's {example.Signature} subrecord ({hex})");
        fromFile++;
        string top = string.Join(", ", names.OrderByDescending(n => n.Value).Take(6).Select(n => $"{n.Key} {N(n.Value)}"));
        return $"WTRMUnknown5 is named AiFiringArc now: 30.0 on the card of {magshear.Label}, at WTRM offset 13 (00 00 F0 41). Still unknown: {N(unknownValues)} values in {N(withUnknown)} of {N(records)} records of Starfield.esm (every WEAP, 300 each of NPC_, ARMO, PERK, LIGH, every RACE), every one on its card with its bytes ({N(nonZero)} not zero); most common: {top}. Example: {exampleLabel} {example.Path} [{example.Signature}] = {example.Text}, bytes {example.Bytes}, found in the file's {example.Signature} subrecord";
    }

    // ---------------------------------------------------------------- The FormID reader against the index's own resolver
    public static string FormIdReaderEqualsIndex(Ctx ctx)
    {
        var index = ctx.DataIndex;
        // The three mods, and every plugin that has a Medium or a Small master (where a wrong numbering would show)
        var plugins = Mods.Select(m => index.Plugin(m)!).ToList();
        plugins.AddRange(index.Plugins.Where(p => p.Segment != null && !Mods.Contains(p.Name, StringComparer.OrdinalIgnoreCase)
            && p.MasterStyles.Any(s => s is MasterStyle.Medium or MasterStyle.Small) && p.Problems.MissingMasters.Count == 0).Take(40));
        plugins.Add(index.Plugin(Starfield)!);
        long checkedRecords = 0, pastMasters = 0; int medium = 0, small = 0;
        foreach (var p in plugins)
        {
            // Since M5 the raw reader has no resolver of its own: this is the header index's, made public
            var reader = p.FormIds;
            medium += p.MasterStyles.Count(s => s == MasterStyle.Medium); small += p.MasterStyles.Count(s => s == MasterStyle.Small);
            using var file = File.OpenHandle(p.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var refs = Sample(AllOf(index, p.Name), 100_000);
            var id = new byte[4];
            foreach (var r in refs)
            {
                var info = index.Describe(r);
                RandomAccess.Read(file, id, info.Offset + 12);
                uint raw = BinaryPrimitives.ReadUInt32LittleEndian(id);
                string mine = reader.FormKey(raw);
                if (!mine.Equals(info.FormKey, StringComparison.OrdinalIgnoreCase))
                    throw new TestFailed($"{p.Name}: the record at 0x{info.Offset:X} is {info.FormKey} in the header index and {mine} by the raw reader");
                // And back: the FormID the plugin's file would write for that FormKey is the one the file has. The one
                // exception is a plugin's own record written in another of the forms that read as its own (any index at or past
                // the masters of a numbering): a Small plugin made as Full writes 01000834, not FE000834.
                if (!HeaderIndex.TryParseFormKey(info.FormKey, out var owner, out var local)) throw new TestFailed($"{p.Name}: {info.FormKey} isn't a FormKey");
                uint? back = reader.Raw(owner, local);
                if (back != raw)
                {
                    if (back == null || !owner.Equals(p.Name, StringComparison.OrdinalIgnoreCase))
                        throw new TestFailed($"{p.Name}: the record at 0x{info.Offset:X} has the FormID {raw:X8} ({info.FormKey}); the resolver writes {(back == null ? "nothing" : back.Value.ToString("X8"))} for it");
                    pastMasters++;
                }
                checkedRecords++;
            }
        }
        return $"{N(checkedRecords)} record FormIDs of {plugins.Count} plugins (with {medium} Medium and {small} Small masters among them) read by the header index's resolver: all equal to the header index's FormKeys, and all written back as the file has them ({N(pastMasters)} are a plugin's own record written in another form than the one its size gives, as a Small plugin that writes 01000834 does: every such form reads as the plugin's own)";
    }

    // ---------------------------------------------------------------- The raw reader
    public static string RawReader(Ctx ctx)
    {
        var e = ctx.Engine; var index = ctx.DataIndex;
        e.Sessions.Use(Mods.Append(Starfield).ToArray());
        long records = 0, subrecords = 0, bytes = 0; int compressed = 0, longForms = 0;
        foreach (var plugin in Mods)
            foreach (var r in AllOf(index, plugin))
            {
                var raw = e.Raw(r);
                records++; subrecords += raw.Subrecords.Count; bytes += raw.Data.Length;
                if (raw.Compressed) compressed++;
                longForms += raw.Subrecords.Count(s => s.LongForm);
                Check(raw.Trailing == 0, $"{index.Describe(r).Label}: {raw.Trailing} bytes are left after the last subrecord");
                // The subrecords and their headers add up to the record's data, XXXX subrecords counted
                long sum = raw.Subrecords.Sum(s => 6L + s.Length + (s.LongForm ? 10 : 0));
                Check(sum == raw.Data.Length, $"{index.Describe(r).Label}: subrecords add up to {sum}, the data is {raw.Data.Length} bytes");
            }
        // Starfield.esm: samples of the compressed types and of the types with long forms
        var byType = ByType(index, Starfield);
        foreach (var sig in new[] { "NPC_", "NAVM", "CELL", "SFTR", "PNDT", "NAVI", "STDT", "GBFM" })
            foreach (var r in Sample(byType[sig], 200))
            {
                var raw = e.Raw(r);
                records++; subrecords += raw.Subrecords.Count; bytes += raw.Data.Length;
                if (raw.Compressed) compressed++;
                longForms += raw.Subrecords.Count(s => s.LongForm);
                long sum = raw.Subrecords.Sum(s => 6L + s.Length + (s.LongForm ? 10 : 0));
                Check(raw.Trailing == 0 && sum == raw.Data.Length, $"{index.Describe(r).Label}: subrecords add up to {sum} of {raw.Data.Length} bytes, {raw.Trailing} left over");
            }
        Check(compressed > 0 && longForms > 0, $"the sample has {compressed} compressed records and {longForms} long forms: both kinds are needed");
        string text = e.RawText(e.Find("darkstar.esm", "0003C2"));
        Check(text.Contains("FormID 020003C2 → DarkStar_Mk3_MagShear (0003C2:darkstar.esm)", StringComparison.Ordinal), "the raw view doesn't resolve the record's own FormID through the header index");
        return $"{N(records)} records sliced: {N(subrecords)} subrecords, {N(bytes)} bytes, {N(compressed)} compressed records inflated, {N(longForms)} XXXX long forms; in every record the subrecords add up to the record's data";
    }

    // ---------------------------------------------------------------- A plugin that changes on disk between two reads
    public static string PluginChanged(Ctx ctx)
    {
        var data = ctx.DataIndex;
        var source = data.Plugin("weap_starlight.esm") ?? throw new TestFailed("weap_starlight.esm isn't in the Data folder");
        Check(!HandsOff(source.Name), "the plugin to copy is one the tests must not touch");
        string copy = Path.Combine(ctx.Folder("changed"), source.Name);
        File.Copy(source.Path, copy, overwrite: true);
        MustBeACopy(ctx, copy);
        var files = new List<string> { copy };
        files.AddRange(source.Header!.Masters.Select(m => data.Plugin(m)!.Path));
        using var index = new HeaderIndex(PluginSource.FromFiles(files), ctx.Options());
        index.Refresh();
        using var engine = new RecordEngine(index, ctx.Schema, Path.Combine(ctx.Work, "verify-changed"), ctx.Data);

        var use1 = engine.Sessions.Use(source.Name);
        Check(use1.Notes.Count == 0 && engine.Sessions.Opens == 1, $"the first use: {use1.Notes.Count} notes, {engine.Sessions.Opens} opens");
        var weapon = index.RecordsOfType(source.Name, "WEAP").First();
        var rec = engine.Load(weapon);
        float before = ((IWeaponGetter)rec.Record).AttackDamage;
        string cardBefore = engine.Fields(rec, new[] { "AttackDamage" })[0].Text!;
        engine.Sessions.Confirm(use1);
        // A second use with nothing changed opens nothing and says nothing
        var use2 = engine.Sessions.Use(source.Name);
        Check(use2.Notes.Count == 0 && engine.Sessions.Opens == 1 && use2.Refresh == null, "an unchanged plugin was opened again");

        // Change the copy while the session holds it open: the damage, in place, size kept
        var raw = engine.Raw(weapon);
        Check(!raw.Compressed, "the weapon is compressed: its damage can't be changed in place");
        var wdmg = raw.Subrecords.First(s => s.Signature == "WDMG");
        float after = before + 1;
        var bytes = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(bytes, after);
        using (var fs = new FileStream(copy, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            fs.Position = raw.Offset + RawRecord.HeaderLength + wdmg.Start;
            fs.Write(bytes);
        }
        // Rule 5: a command that was reading while the plugin changed must fail
        string during = "";
        try { engine.Sessions.Confirm(use2); } catch (RecordException x) { during = x.Message; }
        Check(during.Contains("changed during the read", StringComparison.Ordinal), $"a change during a command wasn't noticed: \"{during}\"");

        var use3 = engine.Sessions.Use(source.Name);
        Check(use3.Reopened.Contains(source.Name, StringComparer.OrdinalIgnoreCase) && use3.Notes.Count == 1, $"the change wasn't told: {use3.Notes.Count} notes, reopened {string.Join(", ", use3.Reopened)}");
        Check(use3.Notes[0].Contains("changed on disk", StringComparison.Ordinal), $"the note says: {use3.Notes[0]}");
        Check(engine.Sessions.Opens == 2, $"{engine.Sessions.Opens} opens after the change");
        var rec2 = engine.Load(engine.Find(source.Name, rec.Info.FormKey));
        float now = ((IWeaponGetter)rec2.Record).AttackDamage;
        string cardNow = engine.Fields(rec2, new[] { "AttackDamage" })[0].Text!;
        Check(now == after, $"after the change Mutagen reads {now}, the file holds {after}");
        Check(cardNow != cardBefore && float.Parse(cardNow, CultureInfo.InvariantCulture) == after, $"the card shows {cardNow} after the change ({cardBefore} before)");
        Check(index.Plugin(source.Name)!.ContentKey != source.ContentKey, "the header index's content key didn't change");

        // A date set backwards with the same content: noticed, told, and the value stays
        File.SetLastWriteTimeUtc(copy, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var use4 = engine.Sessions.Use(source.Name);
        Check(use4.Reopened.Count == 1 && use4.Notes[0].Contains("same content", StringComparison.Ordinal), $"a date set backwards: {string.Join(" | ", use4.Notes)}");
        var rec3 = engine.Load(engine.Find(source.Name, rec.Info.FormKey));
        Check(((IWeaponGetter)rec3.Record).AttackDamage == after, "the value changed with the date");
        // The plugin replaced by another file (deleted and written again) while it is held open
        engine.Sessions.Confirm(use4);
        File.Delete(copy);
        File.Copy(source.Path, copy);
        var use5 = engine.Sessions.Use(source.Name);
        var rec4 = engine.Load(engine.Find(source.Name, rec.Info.FormKey));
        Check(use5.Reopened.Count == 1 && ((IWeaponGetter)rec4.Record).AttackDamage == before, $"after the file was replaced by the original the damage reads {((IWeaponGetter)rec4.Record).AttackDamage}, not {before}");
        return $"a copy of {source.Name} ({N(source.Stamp.Length)} bytes), held open: {rec.Label} AttackDamage {before} changed to {after} in the file. The command that was running fails with \"changed during the read\"; the next use says \"{use3.Notes[0]}\" and reads {now}. A date set back to 2001 with the same content is told too. The file deleted and written again while open: read as the new file. {engine.Sessions.Opens} opens in all, none for the use with nothing changed";
    }

    // ---------------------------------------------------------------- A missing master is an error, not a guess
    public static string MissingMaster(Ctx ctx)
    {
        var data = ctx.DataIndex;
        // A plugin with a Small master, copied without it
        var source = data.Plugin("mr_atom74.esm");
        string missing = "atom74.esm";
        if (source == null || HandsOff(source.Name) || !source.Header!.Masters.Contains(missing, StringComparer.OrdinalIgnoreCase))
        {
            source = data.Plugins.FirstOrDefault(p => p.Segment != null && !HandsOff(p.Name) && p.Stamp.Length < 2_000_000 && p.Header!.Masters.Count >= 2 && p.MasterStyles.Skip(1).Any(s => s == MasterStyle.Small) && p.Problems.MissingMasters.Count == 0)
                ?? throw new TestFailed("no small plugin with a Small master was found to copy");
            missing = source.Header!.Masters.Where((_, i) => i > 0 && source.MasterStyles[i] == MasterStyle.Small).First();
        }
        string copy = Path.Combine(ctx.Folder("missing-master"), source.Name);
        File.Copy(source.Path, copy, overwrite: true);
        var files = new List<string> { copy };
        files.AddRange(source.Header!.Masters.Where(m => !m.Equals(missing, StringComparison.OrdinalIgnoreCase)).Select(m => data.Plugin(m)!.Path));
        using var index = new HeaderIndex(PluginSource.FromFiles(files), ctx.Options());
        index.Refresh();
        using var engine = new RecordEngine(index, ctx.Schema, Path.Combine(ctx.Work, "verify-missing"), ctx.Data);
        string error = "";
        try { engine.Sessions.Use(source.Name); } catch (RecordException x) { error = x.Message; }
        Check(error.Contains(missing, StringComparison.OrdinalIgnoreCase) && error.Contains("nothing is guessed", StringComparison.Ordinal), $"opening {source.Name} without {missing} gave: \"{error}\"");
        Check(engine.Sessions.Open.Count == 0, "the plugin was opened all the same");
        // The raw view of a record whose FormID can't be resolved says so, and names no plugin for it
        var p = index.Plugin(source.Name)!;
        Check(p.Segment != null && p.Segment.Unresolved > 0, "the header index resolved every record without the master");
        // With the master back the plugin opens, and a record reads the same as in Data
        files.Add(data.Plugin(missing)!.Path);
        using var index2 = new HeaderIndex(PluginSource.FromFiles(files), ctx.Options());
        index2.Refresh();
        using var engine2 = new RecordEngine(index2, ctx.Schema, Path.Combine(ctx.Work, "verify-missing2"), ctx.Data);
        engine2.Sessions.Use(source.Name);
        ctx.Engine.Sessions.Use(source.Name);
        int same = 0;
        foreach (var r in Sample(AllOf(index2, source.Name), 50))
        {
            var info = index2.Describe(r);
            string a = engine2.Json(engine2.Load(r)).ToJsonString();
            string b = ctx.Engine.Json(ctx.Engine.Load(ctx.Engine.Find(source.Name, info.FormKey))).ToJsonString();
            Check(a == b, $"{info.Label} reads differently from the copy and from Data");
            same++;
        }
        return $"a copy of {source.Name} without its Small master {missing}: refused with \"{error}\". Nothing was opened ({p.Segment!.Unresolved} records stay unresolved in the header index). With the master back, {same} sampled records read the same as from Data";
    }

    // ---------------------------------------------------------------- Split and put back together; the direct route against the overlay
    public static string SplitAndAssemble(Ctx ctx)
    {
        var e = ctx.Engine; var index = ctx.DataIndex;
        long records = 0, direct = 0, viaGroup = 0, holders = 0, markers = 0, differ = 0, assembledDiffer = 0;
        var examples = new List<string>();
        var perPlugin = new List<string>();
        foreach (var plugin in Mods)
        {
            e.Sessions.Use(plugin);
            var session = e.Sessions.Get(plugin);
            long before = records, beforeHolders = holders;
            // Every record of the plugin as the overlay gives it, records nested in others included
            foreach (var rec in session.Mod.EnumerateMajorRecords())
            {
                string type = RecordSerializer.TypeName(rec);
                var overlayJson = (JsonObject)JsonNode.Parse(RecordSerializer.ToJson(rec))!;
                bool holder = e.Schema.HoldsRecords(type);
                string? nested = holder ? overlayJson.ToJsonString() : null;
                RecordSplit.Split(overlayJson, null);
                var loaded = e.Load(e.Find(plugin, rec.FormKey.ToString()));
                var mine = e.Json(loaded);
                records++;
                if (loaded.Route == ReadRoute.Direct) direct++; else viaGroup++;
                if (mine.ToJsonString() != overlayJson.ToJsonString())
                {
                    differ++;
                    if (examples.Count < 12) examples.Add($"{loaded.Label} {loaded.Info.Signature} ({loaded.Route}): {CardCheck.FirstDifference(overlayJson, mine)}");
                }
                if (!holder) continue;
                holders++;
                markers += loaded.Children;
                var assembled = e.Assembled(loaded);
                if (assembled.ToJsonString() != nested)
                {
                    assembledDiffer++;
                    if (examples.Count < 12) examples.Add($"{loaded.Label} {loaded.Info.Signature} put back together: {CardCheck.FirstDifference(JsonNode.Parse(nested!), assembled)}");
                }
            }
            perPlugin.Add($"{plugin} {N(records - before)} records, {N(holders - beforeHolders)} of them holding others");
        }
        // Starfield.esm: 100 sampled quests with their topics, responses and scenes, read through the overlay's quest group
        e.Sessions.Use(Starfield);
        var sf = e.Sessions.Get(Starfield);
        var quests = sf.TopLevelGroup("Quest")!.Records.Cast<IQuestGetter>().ToList();
        int sfQuests = 0; long sfChildren = 0, sfDiffer = 0;
        for (int i = 0; i < 100; i++)
        {
            var q = quests[(int)((long)i * quests.Count / 100)];
            string nested = JsonNode.Parse(RecordSerializer.ToJson(q))!.ToJsonString();
            var loaded = e.Load(e.Find(Starfield, q.FormKey.ToString()));
            var assembled = e.Assembled(loaded);
            sfQuests++; sfChildren += Count(assembled) - 1;
            if (assembled.ToJsonString() != nested)
            {
                sfDiffer++;
                if (examples.Count < 12) examples.Add($"{loaded.Label} QUST of Starfield.esm put back together: {CardCheck.FirstDifference(JsonNode.Parse(nested), assembled)}");
            }
        }
        ctx.WriteReport("split-assemble.txt", examples);
        Check(differ == 0 && assembledDiffer == 0 && sfDiffer == 0,
            $"{N(differ)} of {N(records)} records read differently than through the overlay, {N(assembledDiffer)} of {N(holders)} holders don't come back together, {N(sfDiffer)} of {sfQuests} Starfield.esm quests don't. First: {string.Join(" || ", examples.Take(6))}");
        return $"{string.Join("; ", perPlugin)}. {N(records)} records, each read by the engine ({N(viaGroup)} from a top-level group, {N(direct)} straight from their own bytes) and by walking the overlay: the same JSON for every one. {N(holders)} records hold others ({N(markers)} markers): each put back together equals the overlay's nested JSON. Starfield.esm: {sfQuests} sampled quests with {N(sfChildren)} records inside them put back together, all equal";

        static long Count(JsonNode? n) => n switch
        {
            JsonObject o => (o["FormKey"] is JsonValue v && v.TryGetValue<string>(out var s) && RecordSplit.IsFormKey(s) ? 1 : 0) + o.Sum(p => Count(p.Value)),
            JsonArray a => a.Sum(Count),
            _ => 0,
        };
    }

    // ---------------------------------------------------------------- Translated text by the language's name
    public static string LanguagesByName(Ctx ctx)
    {
        var e = ctx.Engine; var index = ctx.DataIndex;
        e.Sessions.Use(Starfield);
        var languages = new[] { "English", "German", "French", "Japanese", "Polish" };
        long texts = 0, records = 0; int notFirst = 0;
        var seenLanguages = new HashSet<string>(StringComparer.Ordinal);
        var byType = ByType(index, Starfield);
        foreach (var sig in new[] { "WEAP", "ARMO", "BOOK", "NPC_", "MESG", "PERK", "ALCH" })
            foreach (var r in Sample(byType[sig], 60))
            {
                var rec = e.Load(r);
                var json = e.Json(rec);
                records++;
                foreach (var language in languages)
                {
                    var builder = new CardBuilder(e.Schema, e.Label, rec.Info.Signature, language);
                    var card = new Card { Label = rec.Label, FormKey = rec.Info.FormKey, Signature = rec.Info.Signature, Type = rec.Type, Plugin = rec.Info.Plugin, Language = language, Fields = builder.Build(rec.Type, json) };
                    foreach (var f in card.Values().Where(f => f.Kind == "Text" && f.Json is JsonObject))
                    {
                        var o = (JsonObject)f.Json!;
                        string? expected = CardCheck.Expected(o, language, out bool any);
                        if (o["Values"] is JsonArray values)
                        {
                            foreach (var v in values) seenLanguages.Add(v!["Language"]!.GetValue<string>());
                            // English is not the first language in the file's list: a card that took the first would be wrong
                            if (language == "English" && values.Count > 0 && values[0]!["Language"]!.GetValue<string>() != "English" && values[0]!["String"]!.GetValue<string>() != expected) notFirst++;
                        }
                        if (expected == null) { Check(!any || f.Text!.StartsWith("(no " + language, StringComparison.Ordinal), $"{rec.Label} {f.Path}: no {language} text, the card shows {f.Text}"); continue; }
                        Check(f.Text == expected, $"{rec.Label} {f.Path} in {language}: the card shows \"{f.Text}\", the record has \"{expected}\"");
                        texts++;
                    }
                }
            }
        Check(notFirst > 0, "no sampled text had another language first with another string: the test proves nothing");
        return $"{N(texts)} translated texts of {N(records)} records, each asked in {languages.Length} languages ({string.Join(", ", languages)}): the card always shows the text of the language asked for, found by its name. In {N(notFirst)} of them the first text in the record is another language's and differs from the English one. Languages in the records: {seenLanguages.Count}";
    }

    // ---------------------------------------------------------------- Cards
    sealed class CardTally
    {
        public long Records, Values, Links, Translated, Unknown, Children, NotInSchema, Failed;
        public List<string> Heavy = new(), HeavyChecked = new();
        public Dictionary<string, int> PerType = new(StringComparer.Ordinal);
        public List<string> Failures = new();
        public Dictionary<string, int> NotInSchemaNames = new(StringComparer.Ordinal);
    }

    static void CardOf(RecordEngine e, RecordRef r, CardTally tally)
    {
        string label = "?";
        if (e.HeavyNote(r) is { } heavyNote) { tally.Heavy.Add(heavyNote); return; }
        try
        {
            var rec = e.Load(r);
            label = $"{rec.Label} {rec.Info.Signature} of {rec.Info.Plugin}";
            var json = e.Json(rec);
            var card = e.Card(rec, json);
            // A heavy record (read only when the run allows it): its card is held against its JSON value by value,
            // but its text and its structured copy, each hundreds of megabytes, aren't made a second time
            bool heavy = e.DataBytes(r) > e.HeavyRecordBytes;
            var check = CardCheck.Check(json, card, checkText: !heavy);
            if (heavy) tally.HeavyChecked.Add($"{rec.Label} {rec.Info.Signature}: {check.Values:N0} values");
            // The structured form holds the same fields
            if (!heavy && (card.ToJson()["fields"] is not JsonArray fields || fields.Count != card.Fields.Count)) check.Problems.Add("the structured card has another number of fields");
            tally.Records++; tally.Values += check.Values; tally.Links += check.Links; tally.Translated += check.Translated; tally.Unknown += check.Unknown; tally.Children += check.Children; tally.NotInSchema += check.NotInSchema;
            tally.PerType[rec.Info.Signature] = tally.PerType.GetValueOrDefault(rec.Info.Signature) + 1;
            if (check.NotInSchema > 0)
                foreach (var f in AllFields(card.Fields).Where(f => !f.InSchema))
                {
                    string key = $"{rec.Type}: {System.Text.RegularExpressions.Regex.Replace(f.Path, @"\[\d+\]", "[*]")}";
                    tally.NotInSchemaNames[key] = tally.NotInSchemaNames.GetValueOrDefault(key) + 1;
                }
            if (check.Problems.Count > 0) { tally.Failed++; if (tally.Failures.Count < 200) tally.Failures.Add($"{label}: {check.Problems[0]}{(check.Problems.Count > 1 ? $" (+{check.Problems.Count - 1} more)" : "")}"); }
        }
        catch (Exception x) when (x is not OutOfMemoryException)
        {
            tally.Records++; tally.Failed++;
            if (tally.Failures.Count < 200) tally.Failures.Add($"{label} at {e.Index.Describe(r).Label}: {x.GetType().Name}: {x.Message}");
        }

        static IEnumerable<CardField> AllFields(List<CardField> fields)
        {
            foreach (var f in fields) { yield return f; if (f.Children != null) foreach (var c in AllFields(f.Children)) yield return c; }
        }
    }

    static string CardSummary(CardTally t) =>
        $"{N(t.Values)} JSON values, every one in its card at the same path and printed in the card's text ({N(t.Links)} links labelled from the header index, {N(t.Translated)} translated texts, {N(t.Children)} child markers, {N(t.Unknown)} values of unknown fields with their bytes, {N(t.NotInSchema)} values the schema has no field for, shown all the same)";

    public static string CardsOfTheMods(Ctx ctx)
    {
        var e = ctx.Engine;
        var tally = new CardTally();
        var per = new List<string>();
        foreach (var plugin in Mods)
        {
            e.Sessions.Use(plugin);
            long before = tally.Records;
            foreach (var r in AllOf(ctx.DataIndex, plugin)) CardOf(e, r, tally);
            per.Add($"{plugin} {N(tally.Records - before)}");
        }
        ctx.WriteReport("cards-mods-failures.txt", tally.Failures);
        ctx.WriteReport("cards-mods-not-in-schema.txt", tally.NotInSchemaNames.OrderByDescending(n => n.Value).Select(n => $"{n.Value}\t{n.Key}"));
        Check(tally.Failed == 0, $"{N(tally.Failed)} of {N(tally.Records)} cards fail. First: {string.Join(" || ", tally.Failures.Take(8))}");
        return $"every record of {string.Join(", ", per)}: {N(tally.Records)} cards of {tally.PerType.Count} record types, none fails. {CardSummary(tally)}";
    }

    public static string CardsOfStarfield(Ctx ctx)
    {
        var e = ctx.Engine;
        e.Sessions.Use(Starfield);
        e.AllowHeavy = ctx.HeavyCards;
        var tally = new CardTally();
        var byType = ByType(ctx.DataIndex, Starfield);
        int full = 0;
        foreach (var (sig, all) in byType.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (all.Count <= 200) full++;
            foreach (var r in Sample(all, 200)) CardOf(e, r, tally);
        }
        ctx.WriteReport("cards-starfield-failures.txt", tally.Failures);
        ctx.WriteReport("cards-starfield-not-in-schema.txt", tally.NotInSchemaNames.OrderByDescending(n => n.Value).Select(n => $"{n.Value}\t{n.Key}"));
        string notIn = string.Join("; ", tally.NotInSchemaNames.OrderByDescending(n => n.Value).Take(5).Select(n => $"{n.Key} {N(n.Value)}"));
        Check(tally.Failed == 0, $"{N(tally.Failed)} of {N(tally.Records)} cards fail. First: {string.Join(" || ", tally.Failures.Take(8))}");
        string heavy = tally.Heavy.Count == 0 ? "" : $" {tally.Heavy.Count} record was not read, and no card was made of it: \"{tally.Heavy[0]}\"";
        if (tally.HeavyChecked.Count > 0) heavy += $" Heavy records read in this run, their cards held against their JSON value by value (their text not made a second time): {string.Join("; ", tally.HeavyChecked)}.";
        return (tally.Heavy.Count == 0 ? "" : "DONE ") + $"up to 200 sampled records of each of the {byType.Count} record types of Starfield.esm ({full} types whole): {N(tally.Records)} cards, none fails. {CardSummary(tally)}. Most common values with no schema field: {notIn}.{heavy}";
    }

    // ---------------------------------------------------------------- Plugin names with spaces (GAPS S56)
    /// <summary>
    /// A FormKey whose plugin's name holds a space ("000808:astra-19 sidearm.esm") is a FormKey: every plugin in Data
    /// whose name holds one, every record. The texts RecordSplit.IsFormKey takes in a record's JSON are exactly
    /// Mutagen's links of it (none missed, no other text taken); every record nested in another is split out of it; the
    /// card labels every link. And a few texts that only look like FormKeys aren't taken.
    /// </summary>
    public static string NamesWithSpaces(Ctx ctx)
    {
        var shapes = new (string Text, bool Is)[]
        {
            ("000808:astra-19 sidearm.esm", true), ("00F1A2:Non-Lethal Framework.esm", true), ("000800:unofficial starfield patch.esm", true),
            ("000808:Starfield.esm", true), ("000808:x.ESP", true), ("000808:a b.esl", true),
            ("000808: astra.esm", false), ("000808:astra .esm", false), ("000808:a/b.esm", false), ("000808:a\\b.esm", false), ("000808:a\"b.esm", false),
            ("000808:a:b.esm", false), ("000808:a\nb.esm", false), ("000808:x.esm and more", false), ("see 000808:x.esm", false),
            ("00080:x.esm", false), ("0008G8:x.esm", false), ("Null", false), ("000808:.esm", false),
        };
        foreach (var (text, isKey) in shapes)
            Check(RecordSplit.IsFormKey(text) == isKey, $"\"{text.Replace("\n", "\\n")}\" is {(isKey ? "" : "not ")}a FormKey, but IsFormKey says {!isKey}");

        var e = ctx.Engine; var index = ctx.DataIndex;
        var plugins = index.Plugins.Where(p => p.Name.Contains(' ') && p.Segment != null).Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        Check(plugins.Count > 0, "no plugin in Data has a space in its name");
        long records = 0, links = 0, nested = 0, holders = 0;
        var problems = new List<string>();
        var tally = new CardTally();
        foreach (var plugin in plugins)
        {
            e.Sessions.Use(plugin);
            var p = index.Plugin(plugin)!;
            for (int i = 0; i < p.Segment!.FirstUnresolved; i++)
            {
                var r = new RecordRef(p.Id, i);
                if (e.HeavyNote(r) != null) continue;
                LoadedRecord rec;
                try { rec = e.Load(r); } catch (RecordException) { continue; }
                records++;
                var json = e.Json(rec);
                // Every record nested in this one is a marker now: no object below the top holds a FormKey of its own
                if (e.Schema.HoldsRecords(rec.Type))
                {
                    holders++;
                    long inside = Keys(json, top: true);
                    if (inside > 0 && problems.Count < 12) problems.Add($"{rec.Label} {rec.Info.Signature}: {inside} records nested in it weren't split out");
                    var whole = e.Assembled(rec);
                    nested += Keys(whole, top: true);
                    // Read from its group, Mutagen's record holds the records nested in it, and its links theirs: the whole
                    // record is held against them. Read from its own bytes, it holds none (they are read on their own)
                    if (rec.Route == ReadRoute.Group) json = whole;
                }
                // The texts taken as FormKeys against Mutagen's links (the records nested in it: theirs too)
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Texts(json, taken);
                var theirs = rec.Record.EnumerateFormLinks().Where(l => !l.IsNull).Select(l => l.FormKey.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                links += theirs.Count;
                var missed = theirs.Except(taken, StringComparer.OrdinalIgnoreCase).ToList();
                var extra = taken.Except(theirs, StringComparer.OrdinalIgnoreCase).ToList();
                if ((missed.Count > 0 || extra.Count > 0) && problems.Count < 12)
                    problems.Add($"{rec.Label} {rec.Info.Signature}: links not taken {string.Join(", ", missed.Take(3))}; texts taken that aren't links {string.Join(", ", extra.Take(3))}");
                CardOf(e, r, tally);
            }
        }
        Check(problems.Count == 0 && tally.Failed == 0, $"{string.Join(" || ", problems)} || cards: {string.Join(" || ", tally.Failures.Take(4))}");
        return $"{shapes.Length} texts told right; {plugins.Count} plugins whose names hold a space ({string.Join(", ", plugins.Take(3))} …): {N(records)} records, the FormKeys in their JSON exactly Mutagen's {N(links)} links, {N(holders)} holders with {N(nested)} records nested in them, each split out; {N(tally.Records)} cards, {N(tally.Links)} links labelled";

        // Objects below the top that hold a FormKey of their own (a record nested in this one)
        static long Keys(JsonNode? n, bool top = false) => n switch
        {
            JsonObject o => (!top && o["FormKey"] is JsonValue v && v.TryGetValue<string>(out var s) && RecordSplit.IsFormKey(s) ? 1 : 0) + o.Sum(x => Keys(x.Value)),
            JsonArray a => a.Sum(x => Keys(x)),
            _ => 0,
        };
        // Every text IsFormKey takes, but a record's own FormKey (the top's and those of the records nested in it) and the
        // markers of the records split out of it
        static void Texts(JsonNode? n, HashSet<string> into)
        {
            switch (n)
            {
                case JsonObject o: foreach (var (k, x) in o) if (k != "FormKey" && k != RecordSplit.ChildKey) Texts(x, into); break;
                case JsonArray a: foreach (var x in a) Texts(x, into); break;
                case JsonValue v when v.TryGetValue<string>(out var s) && RecordSplit.IsFormKey(s): into.Add(s); break;
            }
        }
    }

    // ---------------------------------------------------------------- Fields mode: the default against Mutagen's own value
    // ---------------------------------------------------------------- The fields a reader looks for, at their default
    /// <summary>The card shows object bounds, value, weight and damage even when the record leaves them at their default
    /// (the Eon (0476C4:Starfield.esm): its object bounds are all zero, and the card showed nothing).</summary>
    public static string CardDefaultsShown(Ctx ctx)
    {
        var e = ctx.Engine;
        e.Sessions.Use(Starfield, "darkstar.esm");
        var eon = e.Load(Starfield, "0476C4:Starfield.esm");
        var json = e.Json(eon);
        Check(json["ObjectBounds"] == null, "the Eon's JSON was expected to leave ObjectBounds out (all zero)");
        var plain = e.Card(eon, json);
        Check(plain.Fields.All(f => f.Name != "ObjectBounds"), "a card made without defaults shows ObjectBounds");
        var card = e.Card(eon, json, withDefaults: true);
        string text = card.ToText();
        Check(text.Contains("  Object Bounds (OBND): all zero (default)\n", StringComparison.Ordinal), $"the card lacks \"Object Bounds (OBND): all zero (default)\":\n{text[..Math.Min(text.Length, 600)]}");
        var ob = card.Fields.First(f => f.Name == "ObjectBounds");
        Check(ob.IsDefault && card.ToJson()["fields"]!.AsArray().Any(f => f!["name"]!.GetValue<string>() == "ObjectBounds" && f["default"]?.GetValue<bool>() == true), "the card's JSON doesn't mark ObjectBounds as the default");
        // In the schema's order: after the record's header fields, before Transforms
        int obAt = card.Fields.FindIndex(f => f.Name == "ObjectBounds"), trAt = card.Fields.FindIndex(f => f.Name == "Transforms");
        Check(trAt < 0 || obAt < trAt, $"ObjectBounds is at {obAt}, Transforms at {trAt}");
        // Fields that are set aren't repeated, and the fields view says "all zero" for the object
        Check(card.Fields.Count(f => f.Name == "BaseValue") <= 1 && card.Fields.Count(f => f.Name == "AttackDamage") <= 1, "a field shows twice");
        var asked = e.Fields(eon, new[] { "ObjectBounds" })[0];
        Check(asked.IsDefault && asked.Text == "all zero", $"fields: ObjectBounds = {asked.Text}");
        // A weapon whose bounds are set shows them as set, with no default line
        var mag = e.Load("darkstar.esm", "0003C2:darkstar.esm");
        var magCard = e.Card(mag, null, withDefaults: true);
        var added = magCard.Fields.Where(f => f.IsDefault).Select(f => f.Name).ToList();
        Check(magCard.Fields.Count(f => f.Name == "ObjectBounds") == 1, "DarkStar_Mk3_MagShear's card has ObjectBounds more than once or not at all");
        return $"Eon: \"Object Bounds (OBND): all zero (default)\" on the card, marked in its JSON, before Transforms; fields says \"all zero\". DarkStar_Mk3_MagShear: defaults added for {(added.Count == 0 ? "nothing" : string.Join(", ", added))}.";
    }

    /// <summary>GAPS S192: the card as an outline. The record's own fields are all there; a list or object that would
    /// print more than Card.OutlineLines lines is closed to one line that says how many lines it holds, and the fields
    /// view of it has exactly those lines; everything else is the full card's lines, in their order. A short record's
    /// outline is its full card.</summary>
    public static string CardOutline(Ctx ctx)
    {
        var e = ctx.Engine;
        e.Sessions.Use(Starfield, "darkstar.esm");
        var said = new List<string>();
        foreach (var (plugin, key) in new[] { (Starfield, "04716C:Starfield.esm"), (Starfield, "0476C4:Starfield.esm"), ("darkstar.esm", "0003C2:darkstar.esm") })
        {
            var rec = e.Load(plugin, key);
            var card = e.Card(rec, null, withDefaults: true);
            string full = card.ToText(int.MaxValue, 64);
            var closed = new List<string>();
            string outline = card.ToText(int.MaxValue, 64, Card.OutlineLines, closed);
            var fullLines = full.TrimEnd('\n').Split('\n');
            var lines = outline.TrimEnd('\n').Split('\n');
            const string Mark = "   [closed: ";
            // Every line of the record's own fields, as the full card has it (a closed one up to its mark)
            foreach (string top in fullLines.Where(l => l.StartsWith("  ", StringComparison.Ordinal) && !l.StartsWith("   ", StringComparison.Ordinal)))
                Check(lines.Any(l => l == top || l.StartsWith(top + Mark, StringComparison.Ordinal) || l.StartsWith(top + ", the first: ", StringComparison.Ordinal)), $"{rec.Label}: the outline lacks the field \"{top.Trim()}\"");
            // Every line that isn't a closed part's is a line of the full card, in the full card's order
            int at = 0, closedLines = 0;
            foreach (string l in lines)
            {
                int mark = l.IndexOf(Mark, StringComparison.Ordinal);
                if (mark >= 0)
                {
                    string count = l[(mark + Mark.Length)..].Split(' ')[0];
                    Check(int.TryParse(count, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out int held) && held > Card.OutlineLines, $"{rec.Label}: a closed part of {count} lines: {l.Trim()}");
                    closedLines += held;
                    continue;
                }
                int found = Array.IndexOf(fullLines, l, at);
                Check(found >= 0, $"{rec.Label}: the outline's line \"{l.Trim()}\" isn't in the full card after line {at}");
                at = found + 1;
            }
            Check(lines.Length + closedLines == fullLines.Length, $"{rec.Label}: {lines.Length:N0} lines and {closedLines:N0} closed are not the full card's {fullLines.Length:N0}");
            Check(lines.Count(l => l.Contains(Mark, StringComparison.Ordinal)) == closed.Count, $"{rec.Label}: {closed.Count} parts reported closed");
            // The fields view of a closed part has the lines the mark counts
            foreach (string path in closed)
            {
                string line = lines.First(l => l.Contains($"The fields view of {path} opens it", StringComparison.Ordinal));
                int held = int.Parse(line[(line.IndexOf(Mark, StringComparison.Ordinal) + Mark.Length)..].Split(' ')[0], NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
                var opened = e.Fields(rec, new[] { path })[0].ToText().TrimEnd('\n').Split('\n');
                Check(opened.Length - 1 == held, $"{rec.Label}: the fields view of {path} has {opened.Length - 1:N0} lines under its own, the outline says {held:N0}");
            }
            said.Add($"{rec.Label} {fullLines.Length:N0} lines to {lines.Length:N0} ({closed.Count} closed: {string.Join(", ", closed)})");
        }
        // The Beowulf: the 21 object template combinations and the keywords are closed, the whole outline fits one answer
        var beowulf = e.Card(e.Load(Starfield, "04716C:Starfield.esm"), null, withDefaults: true);
        var parts = new List<string>();
        string text = beowulf.ToText(int.MaxValue, 64, Card.OutlineLines, parts);
        Check(parts.Contains("ObjectTemplates") && parts.Contains("Keywords") && text.Length < 12_000, $"the Beowulf's outline: {text.Length:N0} characters, closed {string.Join(", ", parts)}");
        Check(text.Contains("Keywords (KWDA): 16 items, the first: ", StringComparison.Ordinal), "a closed list of plain values shows its first ones");
        // A short record: nothing to close
        var small = e.Card(e.Load("darkstar.esm", "000066:darkstar.esm"), null, withDefaults: true);
        var none = new List<string>();
        Check(small.ToText(int.MaxValue, 64, Card.OutlineLines, none) == small.ToText(int.MaxValue, 64) && none.Count == 0, "a keyword's outline differs from its full card");
        return $"the outline keeps every field of the record and every line of the parts it leaves open, in order; each closed part says its lines, which the fields view of it gives: {string.Join("; ", said)}. A keyword's outline is its full card";
    }

    // ---------------------------------------------------------------- In-game names from the record's own bytes
    static readonly HashSet<string> WorldTop = new(StringComparer.Ordinal) { "CELL", "WRLD", "SFTR", "SFBK", "SFPT", "NAVI" };

    /// <summary>The engine's world types (WorldTypes in SFSearch\Engine): the six top-level kinds and what sits in a cell or a worldspace.</summary>
    internal static bool[] NotWorldOf(Segment seg) => NotWorld(seg);
    internal static string? MutagenNameOf(IMajorRecordGetter r) => MutagenName(r);

    static bool[] NotWorld(Segment seg)
    {
        var world = new bool[seg.Signatures.Count];
        var seen = new bool[seg.Signatures.Count];
        for (int i = 0; i < seg.Count; i++)
        {
            var e = seg[i];
            if (seen[e.Signature]) continue;
            seen[e.Signature] = true;
            string sig = seg.SignatureOf(e);
            if (WorldTop.Contains(sig)) { world[e.Signature] = true; continue; }
            if (!seg.TryGetParent(e, out int owner, out uint local)) continue;
            int at = owner < Entry.UnresolvedOwner ? seg.Find(owner, local) : -1;
            string? parent = at < 0 ? null : seg.SignatureOf(seg[at]);
            world[e.Signature] = parent != null ? parent is "CELL" or "WRLD" : sig is not ("DIAL" or "INFO" or "SCEN");
        }
        return world.Select(w => !w).ToArray();
    }

    static string? MutagenName(IMajorRecordGetter r) => CardBuilder.NoTag(r switch
    {
        ITranslatedNamedGetter t => t.Name?.String,
        INamedGetter n => n.Name,
        _ => null,
    }) is { Length: > 0 } s ? s : null;

    /// <summary>
    /// RecordNames reads a record's name from its own FULL, with no link index and no overlay. Held against Mutagen's
    /// top-level Name of every record of four mods (one of them localized) and of a spread sample of Starfield.esm.
    /// </summary>
    public static string NamesFromBytes(Ctx ctx)
    {
        var e = ctx.Engine; var index = ctx.DataIndex;
        var lines = new List<string>();
        long compared = 0, named = 0; int differ = 0;
        var extra = new Dictionary<string, int>(StringComparer.Ordinal);
        var parts = new List<string>();
        // economy.esm and apc_galaxysmartcontainers.esm: no strings files, and the two plugins in Data whose stored text
        // isn't plain ASCII (code page 1252: Samson’s Art's 0x92, an em dash's 0x97). GAPS S18.
        foreach (var (plugin, max) in new[] { ("weap_starlight.esm", int.MaxValue), ("kgcdoom.esm", int.MaxValue), ("darkstar.esm", int.MaxValue), (Economy, int.MaxValue), (Galaxy, int.MaxValue), ("SFBGS003.esm", 6000), (Starfield, 12000) })
        {
            var p = index.Plugin(plugin);
            if (p?.Segment == null) { parts.Add($"{plugin} isn't in Data"); continue; }
            e.Sessions.Use(plugin);
            var mask = NotWorld(p.Segment);
            var all = RecordNames.All(p, mask, e.Schema, dataFolder: ctx.Data);
            var byEntry = all.Names.ToDictionary(n => n.Entry, n => n.Name);
            var refs = Enumerable.Range(0, p.Segment.FirstUnresolved).Where(i => mask[p.Segment[i].Signature]).Select(i => new RecordRef(p.Id, i)).ToList();
            int here = 0, hereDiffer = 0;
            foreach (var r in Sample(refs, max))
            {
                LoadedRecord rec;
                try { rec = e.Load(r); } catch (RecordException) { continue; }
                string? truth = MutagenName(rec.Record);
                byEntry.TryGetValue(r.Entry, out var mine);
                string? one = RecordNames.Of(p, r.Entry, e.Schema, dataFolder: ctx.Data);
                compared++; here++;
                if (truth != null) named++;
                if (truth == null && mine != null && one == mine)
                {
                    // A FULL that Mutagen doesn't give as the record's top-level Name (a ship module's, a planet's): the bytes have a name, the link index had none
                    extra[rec.Info.Signature] = extra.GetValueOrDefault(rec.Info.Signature) + 1;
                    continue;
                }
                if (truth != mine || one != mine)
                {
                    differ++; hereDiffer++;
                    if (lines.Count < 200) lines.Add($"{rec.Label} ({rec.Info.Signature}): Mutagen \"{truth}\", All \"{mine}\", Of \"{one}\"");
                }
            }
            string loc = (p.Header!.Flags & 0x80) != 0 ? "(localized) " : "";
            parts.Add($"{plugin} {loc}{N(here)} records, {N(all.Names.Count)} names in all ({all.Ms:N0} ms){(hereDiffer > 0 ? $", {hereDiffer} differ" : "")}");
        }
        // The stored text of the two plugins with no strings files, in every shipped language: the names from the bytes
        // against Mutagen read in that language (one reading in every language since Mutagen fixes.12, C150)
        var languages = SFSearch.Query.TextLanguages.Shipped.Select(s => Enum.Parse<Language>(s.Name)).ToList();
        long otherCompared = 0; int nonAscii = 0;
        foreach (var language in languages.Where(l => l != Language.English))
        {
            using var le = new RecordEngine(index, ctx.Schema, Path.Combine(ctx.Work, "verify-names-lang"), ctx.Data, language);
            foreach (var plugin in new[] { Economy, Galaxy })
            {
                var p = index.Plugin(plugin);
                if (p?.Segment == null) continue;
                var mask = NotWorld(p.Segment);
                var byEntry = RecordNames.All(p, mask, e.Schema, language, ctx.Data).Names.ToDictionary(n => n.Entry, n => n.Name);
                for (int i = 0; i < p.Segment.FirstUnresolved; i++)
                {
                    if (!mask[p.Segment[i].Signature]) continue;
                    LoadedRecord rec;
                    try { rec = le.Load(new RecordRef(p.Id, i)); } catch (RecordException) { continue; }
                    string? truth = MutagenName(rec.Record);
                    if (truth == null) continue;
                    byEntry.TryGetValue(i, out var mine);
                    string? one = RecordNames.Of(p, i, e.Schema, language, ctx.Data);
                    otherCompared++;
                    if (truth.Any(c => c > 0x7F)) nonAscii++;
                    if (truth != mine || one != mine)
                    {
                        differ++;
                        if (lines.Count < 200) lines.Add($"{rec.Label} ({rec.Info.Signature}) in {language}: Mutagen \"{truth}\", All \"{mine}\", Of \"{one}\"");
                    }
                }
            }
        }
        // The two texts of GAPS S18, by name, in English and German
        foreach (var language in new[] { Language.English, Language.German })
        {
            Check(NameOf(Economy, "111EA8:Starfield.esm", language) == "Samson’s Art", $"Samson's Art from the bytes in {language}: \"{NameOf(Economy, "111EA8:Starfield.esm", language)}\"");
            string? book = NameOf(Galaxy, "000A27:apc_galaxysmartcontainers.esm", language);
            Check(book != null && book.Contains("Galaxy Handbook — Vol 1", StringComparison.Ordinal), $"APCG_BookVol1 from the bytes in {language}: \"{book}\"");
        }
        string? NameOf(string plugin, string formKey, Language language)
        {
            var r = e.Load(plugin, formKey).Ref;
            return RecordNames.Of(index.Plugin(plugin)!, r.Entry, e.Schema, language, ctx.Data);
        }
        // Cells and worldspaces (GAPS S1): find reads their names from their own records' bytes, and an exterior cell's
        // grid; never what they hold. Held against Mutagen's Name and Grid: two mods whole, a spread sample of Starfield.esm
        long worldCompared = 0, worldNamed = 0, gridsCompared = 0;
        var worldParts = new List<string>();
        foreach (var (plugin, max) in new[] { ("kgcdoom.esm", int.MaxValue), ("stlazarusawakened.esm", int.MaxValue), (Starfield, 3000) })
        {
            var p = index.Plugin(plugin);
            if (p?.Segment == null) { worldParts.Add($"{plugin} isn't in Data"); continue; }
            e.Sessions.Use(plugin);
            var mask = p.Segment.Signatures.Select(sg => sg.Signature is "CELL" or "WRLD").ToArray();
            var all = RecordNames.All(p, mask, e.Schema, dataFolder: ctx.Data);
            var byEntry = all.Names.ToDictionary(n => n.Entry, n => n.Name);
            var refs = Enumerable.Range(0, p.Segment.FirstUnresolved).Where(i => mask[p.Segment[i].Signature]).Select(i => new RecordRef(p.Id, i)).ToList();
            int here = 0;
            foreach (var r in Sample(refs, max))
            {
                LoadedRecord rec;
                try { rec = e.Load(r); } catch (RecordException) { continue; }
                string? truth = MutagenName(rec.Record);
                byEntry.TryGetValue(r.Entry, out var mine);
                string? one = RecordNames.Of(p, r.Entry, e.Schema, dataFolder: ctx.Data);
                worldCompared++; here++;
                if (truth != null) worldNamed++;
                if (truth != mine || one != mine)
                {
                    differ++;
                    if (lines.Count < 200) lines.Add($"{rec.Label} ({rec.Info.Signature}): Mutagen \"{truth}\", All \"{mine}\", Of \"{one}\"");
                }
                if (rec.Record is ICellGetter cell)
                {
                    var grid = RecordNames.CellGrid(p, r.Entry);
                    var want = cell.Grid is { } g ? (g.Point.X, g.Point.Y) : ((int, int)?)null;
                    gridsCompared++;
                    if (grid != want)
                    {
                        differ++;
                        if (lines.Count < 200) lines.Add($"{rec.Label} (CELL): Mutagen's grid {want}, from the bytes {grid}");
                    }
                }
            }
            worldParts.Add($"{plugin} {N(here)} cells and worldspaces, {N(all.Names.Count)} names ({all.Ms:N0} ms)");
        }
        // By name: the worldspace and an interior cell the user asked for (GAPS S1)
        foreach (var (formKey, name) in new[] { ("01251B:Starfield.esm", "New Atlantis"), ("06BB29:Starfield.esm", "The Lodge") })
        {
            var r = e.Load(Starfield, formKey).Ref;
            string? got = RecordNames.Of(index.Plugin(Starfield)!, r.Entry, e.Schema, dataFolder: ctx.Data);
            Check(got == name, $"{formKey} from the bytes: \"{got}\", not \"{name}\"");
        }
        ctx.WriteReport("names-from-bytes.txt", lines);
        Check(differ == 0, $"{differ} of {N(compared + otherCompared + worldCompared)} names differ from Mutagen's: {string.Join("; ", lines.Take(5))}");
        return $"{N(compared)} records compared, {N(named)} with a name: every name read from the bytes equals Mutagen's top-level Name. "
            + $"{Economy} and {Galaxy} in the {languages.Count - 1} other languages: {N(otherCompared)} names equal to Mutagen's in that language ({nonAscii} outside ASCII); Samson’s Art and \"Galaxy Handbook — Vol 1\" read right. "
            + $"Names in the bytes that Mutagen doesn't give as the top-level Name (shown now): {(extra.Count == 0 ? "none" : string.Join(", ", extra.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {N(k.Value)}")))}. {string.Join("; ", parts)}. "
            + $"Cells and worldspaces: {N(worldCompared)} compared, {N(worldNamed)} with a name, {N(gridsCompared)} cells' grids, every one equal to Mutagen's; New Atlantis and The Lodge read right. {string.Join("; ", worldParts)}.";
    }

    const string Economy = "economy.esm", Galaxy = "apc_galaxysmartcontainers.esm";

    /// <summary>What reading every name of the load order from the files costs (no link index): the measure behind find's choice.</summary>
    public static string NamesWholeLoadOrder(Ctx ctx)
    {
        var index = ctx.DataIndex;
        var clock = Stopwatch.StartNew();
        long names = 0, records = 0, bytes = 0; int plugins = 0, bad = 0;
        double slowest = 0; string slowestName = "";
        foreach (var p in index.Plugins.Where(p => p.Segment != null))
        {
            var all = RecordNames.All(p, NotWorld(p.Segment!), ctx.Schema, dataFolder: ctx.Data);
            plugins++; names += all.Names.Count; records += all.Records; bytes += all.Bytes; bad += all.NotInflated;
            if (all.Ms > slowest) { slowest = all.Ms; slowestName = p.Name; }
        }
        double first = clock.Elapsed.TotalSeconds;
        clock.Restart();
        foreach (var p in index.Plugins.Where(p => p.Segment != null)) RecordNames.All(p, NotWorld(p.Segment!), ctx.Schema, dataFolder: ctx.Data);
        double again = clock.Elapsed.TotalSeconds;
        GC.Collect();
        return $"DONE {N(plugins)} plugins, {N(records)} records outside world data ({bytes / 1048576.0:N0} MB): {N(names)} names in {first:F1} s the first time (string files opened), {again:F1} s again; slowest {slowestName} {slowest:N0} ms; {bad} compressed records couldn't be inflated; heap {HeapMb():N0} MB.";
    }

    public static string FieldsDefaults(Ctx ctx)
    {
        var e = ctx.Engine; var index = ctx.DataIndex;
        e.AllowHeavy = false;
        long records = 0, checkedDefaults = 0, noDefault = 0, wrong = 0, formVersionLeftOut = 0, fromRecord = 0;
        var wrongBy = new Dictionary<string, (int N, string Example)>(StringComparer.Ordinal);
        var noDefaultBy = new Dictionary<string, int>(StringComparer.Ordinal);
        void One(RecordRef r)
        {
            LoadedRecord rec;
            try { rec = e.Load(r); } catch (RecordException) { return; }
            var json = e.Json(rec);
            var card = e.Card(rec, json);
            records++;
            // The form version is in the record's header. The serializer before 1.38.6-fixes.2 left it out when it was
            // Mutagen's own default for a new record (not 0); fixes.2 writes it always. Whatever the JSON does, fields
            // mode must give what the file has.
            {
                if (!json.ContainsKey("FormVersion")) formVersionLeftOut++;
                string asked = FieldLookup.Fields(e.Schema, card, new[] { "FormVersion" })[0].Text ?? "";
                string inFile = e.Raw(r).FormVersion.ToString(CultureInfo.InvariantCulture);
                if (asked != inFile)
                {
                    wrong++;
                    string k = json.ContainsKey("FormVersion") ? "FormVersion" : "FormVersion left out of the JSON";
                    wrongBy[k] = wrongBy.TryGetValue(k, out var w) ? (w.N + 1, w.Example) : (1, $"{rec.Label}: fields says {asked}, the record's header has {inFile}");
                }
            }
            foreach (var p in e.Schema.Props(rec.Type))
            {
                var def = p.Main;
                if (def.Nullable || json.ContainsKey(p.Name)) continue;
                if (def.Kind is not ("Bool" or "Int8" or "Int16" or "Int32" or "Int64" or "UInt8" or "UInt16" or "UInt32" or "UInt64" or "Float" or "Double" or "Percent" or "Enum" or "String" or "FormLink")) continue;
                object? actual = Property(rec.Record, p.Name);
                var value = FieldLookup.Fields(e.Schema, card, new[] { p.Name })[0];
                string key = $"{rec.Type}.{p.Name} ({def.Kind})";
                // A property Mutagen adds beyond its XML (GAPS Part 2 row 8) is read from the record, not defaulted
                bool read = def.Source != null && value.IsSet;
                if (!read && (!value.IsDefault || value.Json == null)) { noDefault++; noDefaultBy[key] = noDefaultBy.GetValueOrDefault(key) + 1; continue; }
                if (read) fromRecord++; else checkedDefaults++;
                if (!SameAsMutagen(def.Kind, value, actual))
                {
                    wrong++;
                    wrongBy[key] = wrongBy.TryGetValue(key, out var w) ? (w.N + 1, w.Example) : (1, $"{rec.Label}: fields says {value.Text}, Mutagen has {Show(actual)}");
                }
            }
        }
        foreach (var plugin in Mods) { e.Sessions.Use(plugin); foreach (var r in Sample(AllOf(index, plugin), 6000)) One(r); }
        e.Sessions.Use(Starfield);
        foreach (var (_, all) in ByType(index, Starfield)) foreach (var r in Sample(all, 40)) One(r);
        // The three mods and Starfield.esm have no record at the form version Mutagen gives a new record, which is the one
        // the serializer before 1.38.6-fixes.2 left out. The first small plugin in Data whose first own record has it is sampled too.
        ushort newRecordVersion = Mutagen.Bethesda.Plugins.Meta.GameConstants.Get(Mutagen.Bethesda.GameRelease.Starfield).DefaultFormVersion ?? 0;
        string? withDefaultVersion = null;
        foreach (var p in index.Plugins.Where(p => p.Segment != null && p.Segment.FirstOwn < p.Segment.FirstUnresolved && p.Stamp.Length < 8 << 20 && p.Problems.MissingMasters.Count == 0
                     && !Mods.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            using var file = File.OpenHandle(p.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var two = new byte[2];
            RandomAccess.Read(file, two, p.Segment![p.Segment.FirstOwn].Offset + 20);
            if (BinaryPrimitives.ReadUInt16LittleEndian(two) != newRecordVersion) continue;
            withDefaultVersion = p.Name;
            break;
        }
        if (withDefaultVersion != null) { e.Sessions.Use(withDefaultVersion); foreach (var r in Sample(AllOf(index, withDefaultVersion), 2000)) One(r); }
        ctx.WriteReport("fields-defaults-wrong.txt", wrongBy.OrderByDescending(w => w.Value.N).Select(w => $"{w.Value.N}\t{w.Key}\t{w.Value.Example}"));
        ctx.WriteReport("fields-defaults-none.txt", noDefaultBy.OrderByDescending(w => w.Value).Select(w => $"{w.Value}\t{w.Key}"));
        string summary = $"{N(records)} records: {N(checkedDefaults + fromRecord)} fields the JSON leaves out and the schema says are always there, each answered by fields mode with a default ({N(fromRecord)} of them properties Mutagen adds beyond its XML, read from its record) and held against the value Mutagen itself has: {N(checkedDefaults + fromRecord - wrong)} equal, {N(wrong)} not equal in {wrongBy.Count} fields; {N(noDefault)} more where the schema has no default to give ({noDefaultBy.Count} fields). {N(formVersionLeftOut)} of the records have their form version left out of the JSON (the serializer writes it always since 1.38.6-fixes.2; records at {newRecordVersion}, the one Mutagen gives a new record, sampled from {withDefaultVersion ?? "no plugin: none in Data has such a record"}): fields mode gives the one in the record's header for each";
        if (wrong > 0) summary += ". Not equal: " + string.Join("; ", wrongBy.OrderByDescending(w => w.Value.N).Take(12).Select(w => $"{w.Key} ×{w.Value.N}, e.g. {w.Value.Example}"));
        return (wrong == 0 ? "" : "DONE ") + summary;

        static string Show(object? o) => o switch { null => "null", Noggog.Percent pc => pc.Value.ToString("R", CultureInfo.InvariantCulture), IFormLinkGetter l => l.IsNull ? "Null" : l.FormKey.ToString(), float f => f.ToString("R", CultureInfo.InvariantCulture), _ => Convert.ToString(o, CultureInfo.InvariantCulture) ?? "" };

        static bool SameAsMutagen(string kind, FieldValue value, object? actual)
        {
            var el = value.Json!.GetValueKind();
            switch (kind)
            {
                case "Bool": return actual is bool b && b == (el == JsonValueKind.True);
                case "Float" or "Double" or "Percent":
                    return actual != null && double.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && (actual is Noggog.Percent pc ? pc.Value : Convert.ToDouble(actual, CultureInfo.InvariantCulture)) == d;
                case "Enum":
                    if (el == JsonValueKind.Array) return actual is Enum fl && string.Join(",", ((JsonArray)value.Json!).Select(x => x!.GetValue<string>())) == (Convert.ToInt64(fl, CultureInfo.InvariantCulture) == 0 ? "" : fl.ToString().Replace(", ", ","));
                    return actual is Enum en && (en.ToString() == value.Text || Convert.ToInt64(en, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) == value.Text);
                case "String": return (actual as string ?? actual?.ToString() ?? "") == (value.Text ?? "");
                case "FormLink": return actual is IFormLinkGetter l && l.IsNull && value.Text == "Null";
                default: return actual != null && Convert.ToString(actual, CultureInfo.InvariantCulture) == value.Text;
            }
        }
    }

    /// <summary>A property of a Mutagen object by name, on its class or one of its interfaces.</summary>
    static object? Property(object o, string name)
    {
        var type = o.GetType();
        var prop = type.GetProperty(name) ?? type.GetInterfaces().Select(t => t.GetProperty(name)).FirstOrDefault(x => x != null);
        return prop?.GetValue(o);
    }

    // ---------------------------------------------------------------- The one-record verify
    static string VerifyRun(Ctx ctx, string report, IEnumerable<(string Plugin, List<RecordRef> Records)> sets, out long total, out long equal)
    {
        var e = ctx.Engine;
        var verifier = new Verifier(e);
        var causes = new Dictionary<string, (int N, VerifyResult Example)>(StringComparer.Ordinal);
        var perType = new Dictionary<string, (int Total, int Equal)>(StringComparer.Ordinal);
        var lines = new List<string>();
        total = 0; equal = 0; long bytes = 0; int noVerdict = 0;
        foreach (var (plugin, records) in sets)
        {
            e.Sessions.Use(plugin);
            foreach (var v in verifier.Verify(records))
            {
                total++;
                if (v.Equal) { equal++; bytes += v.Bytes; }
                else if (v.Cause == null) noVerdict++;
                else
                {
                    causes[v.Cause] = causes.TryGetValue(v.Cause, out var c) ? (c.N + 1, c.Example) : (1, v);
                    lines.Add($"{v.Cause}\t{v.Label}\t{v.Detail}\t{v.FileBytes}\t{v.WrittenBytes}");
                }
                var t = perType.GetValueOrDefault(v.Signature);
                perType[v.Signature] = (t.Total + 1, t.Equal + (v.Equal ? 1 : 0));
            }
        }
        ctx.WriteReport(report + "-not-equal.tsv", lines);
        ctx.WriteReport(report + "-causes.tsv", causes.OrderByDescending(c => c.Value.N).Select(c => $"{c.Value.N}\t{c.Key}\t{c.Value.Example.Label}\t{c.Value.Example.Detail}\t{c.Value.Example.FileBytes}\t{c.Value.Example.WrittenBytes}"));
        ctx.WriteReport(report + "-per-type.tsv", perType.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key}\t{t.Value.Total}\t{t.Value.Equal}\t{t.Value.Total - t.Value.Equal}"));
        Check(noVerdict == 0, $"{noVerdict} records got no verdict");
        int typesAllEqual = perType.Count(t => t.Value.Total == t.Value.Equal);
        string s = $"{N(equal)} of {N(total)} records equal ({N(bytes)} bytes compared); {typesAllEqual} of {perType.Count} record types wholly equal";
        if (causes.Count > 0)
            s += $". {N(total - equal)} not equal, in {causes.Count} causes:\n" + string.Join("\n", causes.OrderByDescending(c => c.Value.N).Select(c =>
                $"      {c.Value.N,6}  {c.Key}   e.g. {c.Value.Example.Label}: {c.Value.Example.Detail}{(c.Value.Example.FileBytes != null ? $" file [{c.Value.Example.FileBytes}] written [{c.Value.Example.WrittenBytes}]" : "")}"));
        return s;
    }

    public static string VerifyTheMods(Ctx ctx)
    {
        string s = VerifyRun(ctx, "verify-mods", Mods.Select(m => (m, AllOf(ctx.DataIndex, m))), out long total, out long equal);
        return (total == equal ? "" : "DONE ") + $"every record of {string.Join(", ", Mods)}: " + s;
    }

    public static string VerifyStarfield(Ctx ctx)
    {
        ctx.Engine.AllowHeavy = ctx.Heavy;
        var byType = ByType(ctx.DataIndex, Starfield);
        var all = byType.OrderBy(t => t.Key, StringComparer.Ordinal).SelectMany(t => Sample(t.Value, 1000)).ToList();
        string s = VerifyRun(ctx, "verify-starfield", new[] { (Starfield, all) }, out long total, out long equal);
        return (total == equal ? "" : "DONE ") + $"up to 1,000 sampled records of each of the {byType.Count} record types of Starfield.esm: " + s;
    }

    // ---------------------------------------------------------------- Time and memory
    public static string Timing(Ctx ctx)
    {
        var index = ctx.DataIndex;
        var lines = new List<string>();
        long startMb = HeapMb();
        // A new engine: no plugin is open yet. Run this test alone (--only timing) for the cold numbers of a new process.
        using var e = new RecordEngine(index, ctx.Schema, Path.Combine(ctx.Work, "verify-timing"), ctx.Data);
        double T(Action a) { var c = Stopwatch.StartNew(); a(); return c.Elapsed.TotalMilliseconds; }

        // One record by FormKey, through its top-level group
        string CardOnce(string plugin, string key)
        {
            e.Sessions.Use(plugin);
            var rec = e.Load(plugin, key);
            return e.Card(rec).ToText();
        }
        double coldGroup = T(() => CardOnce("darkstar.esm", "0003C2:darkstar.esm"));
        double warmGroup = Enumerable.Range(0, 20).Select(_ => T(() => CardOnce("darkstar.esm", "0003C2:darkstar.esm"))).Order().ElementAt(10);
        double otherGroup = T(() => CardOnce("darkstar.esm", "0003C3:darkstar.esm"));
        lines.Add($"one weapon of darkstar.esm by FormKey, as a card: {coldGroup:F0} ms the first time (the plugin opened, its weapon group read, Mutagen's and the serializer's start-up), {warmGroup:F1} ms after (median of 20, the stamp check included), {otherGroup:F1} ms for another weapon");

        // One record nested in another, straight from its own bytes
        var refr = index.RecordsOfType(Starfield, "REFR").Skip(1_000_000).First();
        string refrKey = index.Describe(refr).FormKey;
        long beforeSf = HeapMb();
        double coldDirect = T(() => CardOnce(Starfield, refrKey));
        double warmDirect = Enumerable.Range(0, 20).Select(_ => T(() => CardOnce(Starfield, refrKey))).Order().ElementAt(10);
        lines.Add($"one placed reference of Starfield.esm by FormKey ({index.Describe(refr).Label}), read from its own bytes: {coldDirect:F0} ms the first time (Starfield.esm opened), {warmDirect:F1} ms after; {HeapMb() - beforeSf:N0} MB more on the managed heap");
        var eon = T(() => CardOnce(Starfield, "Eon"));
        var eonWarm = Enumerable.Range(0, 20).Select(_ => T(() => CardOnce(Starfield, "Eon"))).Order().ElementAt(10);
        lines.Add($"Eon of Starfield.esm by EditorID: {eon:F0} ms the first time (the weapon group read), {eonWarm:F1} ms after");

        // 1,000 cards
        var byType = ByType(index, Starfield);
        var thousand = new[] { "WEAP", "ARMO", "NPC_", "COBJ", "OMOD", "MISC", "ALCH", "AMMO", "BOOK", "LVLI" }.SelectMany(s => Sample(byType[s], 100)).ToList();
        long chars = 0;
        e.Sessions.Use(Starfield);
        double cardsCold = T(() => { foreach (var r in thousand) chars += e.Card(e.Load(r)).ToText().Length; });
        long chars2 = 0;
        double cardsWarm = T(() => { foreach (var r in thousand) chars2 += e.Card(e.Load(r)).ToText().Length; });
        Check(chars == chars2, "the same 1,000 cards have another length the second time");
        lines.Add($"1,000 cards (100 each of WEAP, ARMO, NPC_, COBJ, OMOD, MISC, ALCH, AMMO, BOOK, LVLI of Starfield.esm; read, to JSON, card, text): {cardsCold / 1000:F2} s the first time (10 groups read), {cardsWarm / 1000:F2} s after, {cardsWarm:F0} ms/1000 = {cardsWarm / 1000:F2} ms a card; {N(chars)} characters of text");

        // 7,131 NPCs to JSON
        var npcs = byType["NPC_"];
        long beforeNpc = HeapMb();
        long jsonBytes = 0;
        var texts = new string[npcs.Count];
        double npcOne = T(() => { for (int i = 0; i < npcs.Count; i++) { var rec = e.Load(npcs[i]); texts[i] = RecordSerializer.ToJson(rec.Record); jsonBytes += texts[i].Length; } });
        long afterNpc = HeapMb();
        // The same on 8 threads (Mutagen doesn't promise overlays are safe to read from several threads: the result is compared)
        var records = npcs.Select(r => e.Load(r)).ToList();
        var texts8 = new string[npcs.Count];
        double npcEight = T(() => Parallel.For(0, records.Count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => texts8[i] = RecordSerializer.ToJson(records[i].Record)));
        int differ = 0; for (int i = 0; i < texts.Length; i++) if (texts[i] != texts8[i]) differ++;
        lines.Add($"{N(npcs.Count)} NPCs of Starfield.esm to JSON: {npcOne / 1000:F2} s on one thread ({npcOne / npcs.Count:F3} ms each), {npcEight / 1000:F2} s on 8 threads ({(differ == 0 ? "the same JSON for every one" : differ + " differ from the one-thread JSON")}); {jsonBytes / 1048576.0:F1} MB of JSON; {afterNpc - beforeNpc:N0} MB more on the managed heap with all of it held as text");
        Check(npcs.Count == 7131, $"Starfield.esm has {npcs.Count} NPCs, not 7,131");
        using var proc = Process.GetCurrentProcess();
        lines.Add($"managed heap: {startMb:N0} MB at the start of this test, {HeapMb():N0} MB at its end with Starfield.esm and darkstar.esm open and 13 groups read; peak working set of the process {proc.PeakWorkingSet64 / 1048576:N0} MB (most of it the header index being made for all of Data in this process)");
        ctx.WriteReport("timing.txt", lines);
        return "\n      " + string.Join("\n      ", lines);
    }

    // ---------------------------------------------------------------- Top-level records straight from their bytes: the same JSON?
    public static string DirectTopLevel(Ctx ctx)
    {
        var index = ctx.DataIndex; var e = ctx.Engine;
        e.AllowHeavy = false;
        using var direct = new RecordEngine(index, ctx.Schema, Path.Combine(ctx.Work, "verify-direct"), ctx.Data) { PreferDirect = true };
        long records = 0, same = 0, notDirect = 0;
        var types = new HashSet<string>(StringComparer.Ordinal);
        var differ = new List<string>();
        void One(RecordRef r)
        {
            var info = index.Describe(r);
            if (e.IsDirect(info) || e.HeavyNote(r) != null) return; // read from its own bytes anyway, or not read at all
            LoadedRecord a, b;
            try { a = e.Load(r); b = direct.Load(r); }
            catch (RecordException x) { differ.Add($"{info.Label} {info.Signature}: {x.Message}"); return; }
            if (b.Route != ReadRoute.Direct) { notDirect++; return; }
            records++; types.Add(info.Signature);
            string ja = e.Json(a).ToJsonString(), jb = direct.Json(b).ToJsonString();
            if (ja == jb) same++;
            else if (differ.Count < 50) differ.Add($"{info.Label} {info.Signature}: {CardCheck.FirstDifference(JsonNode.Parse(ja), JsonNode.Parse(jb))}");
        }
        foreach (var plugin in Mods)
        {
            e.Sessions.Use(plugin); direct.Sessions.Use(plugin);
            foreach (var r in AllOf(index, plugin)) One(r);
        }
        e.Sessions.Use(Starfield); direct.Sessions.Use(Starfield);
        foreach (var (_, all) in ByType(index, Starfield)) foreach (var r in Sample(all, 200)) One(r);
        ctx.WriteReport("direct-top-level-differences.txt", differ);
        string s = $"{N(records)} top-level records of {types.Count} record types (every one of the three mods, up to 200 of each type of Starfield.esm) read both through their top-level group and straight from their own bytes: {N(same)} give the same JSON, {N(records - same)} differ; {N(notDirect)} more are of a signature with several Mutagen types (OMOD, GMST) and can only be read through their group";
        if (records != same) s += ". Differences: " + string.Join(" || ", differ.Take(8));
        return (records == same ? "" : "DONE ") + s;
    }

    // ---------------------------------------------------------------- Records nested in others: the two routes, measured
    public static string NestedRoute(Ctx ctx)
    {
        var index = ctx.DataIndex;
        var lines = new List<string>();
        double T(Action a) { var c = Stopwatch.StartNew(); a(); return c.Elapsed.TotalMilliseconds; }

        // The three mods: every nested record by both routes was compared in split-assemble. Here: what each route costs.
        foreach (var plugin in new[] { "kgcdoom.esm", "darkstar.esm" })
        {
            using var e = new RecordEngine(index, ctx.Schema, Path.Combine(ctx.Work, "verify-route"), ctx.Data);
            e.Sessions.Use(plugin);
            var refs = Sample(index.RecordsOfType(plugin, "REFR").ToList(), 2000);
            var session = e.Sessions.Get(plugin);
            long n = 0;
            double direct = T(() => { foreach (var r in refs) { e.Load(r); n++; } });
            // The overlay route: find the reference's cell among the plugin's cells, then the reference in the cell
            var wanted = refs.Select(r => index.Describe(r)).ToDictionary(i => FormKey.Factory(i.FormKey), i => i.Parent);
            int found = 0;
            double walk = T(() =>
            {
                foreach (var cell in session.Mod.EnumerateMajorRecords<ICellGetter>())
                    foreach (var placed in cell.Persistent.Concat(cell.Temporary))
                        if (wanted.ContainsKey(placed.FormKey)) found++;
            });
            lines.Add($"{plugin}: {N(refs.Count)} placed references read from their own bytes in {direct:F0} ms ({direct / refs.Count:F3} ms each); one walk of the overlay over every cell to find the same {N(found)} took {walk:F0} ms");
            Check(found == refs.Count, $"{plugin}: the overlay walk found {found} of {refs.Count} references");
        }
        if (!ctx.Heavy)
        {
            lines.Add("Starfield.esm through the overlay was not run (it reads the cell and worldspace groups, about 1.1 GB): add --heavy, on a PC with memory to spare");
            return "\n      " + string.Join("\n      ", lines);
        }

        // Starfield.esm: one placed reference of an interior cell and one of a worldspace cell, by both routes
        using (var e = new RecordEngine(index, ctx.Schema, Path.Combine(ctx.Work, "verify-route"), ctx.Data))
        {
            e.Sessions.Use(Starfield);
            var session = e.Sessions.Get(Starfield);
            var all = index.RecordsOfType(Starfield, "REFR").ToList();
            RecordInfo? interior = null, exterior = null; RecordRef interiorRef = default, exteriorRef = default;
            foreach (var r in Sample(all, 4000))
            {
                var info = index.Describe(r);
                if (info.Parent == null) continue;
                var cell = index.Versions(info.Parent)!.Holders.First(h => index.Plugins[h.Plugin].Name == Starfield);
                bool inWorld = index.Describe(cell).Parent != null;
                if (inWorld && exterior == null) { exterior = info; exteriorRef = r; }
                if (!inWorld && interior == null) { interior = info; interiorRef = r; }
                if (interior != null && exterior != null) break;
            }
            Check(interior != null && exterior != null, "no interior or no worldspace reference in the sample");
            foreach (var (info, r, where) in new[] { (interior!, interiorRef, "an interior cell"), (exterior!, exteriorRef, "a worldspace cell") })
            {
                long before = PrivateMb();
                string? directJson = null;
                double direct = T(() => directJson = e.Json(e.Load(r)).ToJsonString());
                long afterDirect = PrivateMb();
                var key = FormKey.Factory(info.FormKey); var cellKey = FormKey.Factory(info.Parent!);
                string? overlayJson = null;
                double walk = T(() =>
                {
                    ICellGetter? cell = null;
                    if (where.StartsWith("an interior", StringComparison.Ordinal))
                    {
                        foreach (var block in session.Mod.Cells.Records)
                        {
                            foreach (var sub in block.SubBlocks) { cell = sub.Cells.FirstOrDefault(c => c.FormKey == cellKey); if (cell != null) break; }
                            if (cell != null) break;
                        }
                    }
                    else
                    {
                        var cellInfo = index.Describe(index.Versions(info.Parent!)!.Holders.First(h => index.Plugins[h.Plugin].Name == Starfield));
                        var world = (IWorldspaceGetter)session.TopLevelGroup("Worldspace")!.RecordCache.TryGetValue(FormKey.Factory(cellInfo.Parent!))!;
                        cell = world.TopCell?.FormKey == cellKey ? world.TopCell : world.SubCells.SelectMany(b => b.Items).SelectMany(s => s.Items).FirstOrDefault(c => c.FormKey == cellKey);
                    }
                    var placed = cell!.Persistent.Concat(cell.Temporary).First(p => p.FormKey == key);
                    var j = (JsonObject)JsonNode.Parse(RecordSerializer.ToJson(placed))!;
                    RecordSplit.Split(j, null);
                    overlayJson = j.ToJsonString();
                });
                Check(directJson == overlayJson, $"{info.Label}: the two routes give other JSON: {CardCheck.FirstDifference(JsonNode.Parse(overlayJson!), JsonNode.Parse(directJson!))}");
                lines.Add($"Starfield.esm, {info.Label} in {where}: {direct:F1} ms and {afterDirect - before:N0} MB from its own bytes; {walk:F0} ms and {PrivateMb() - afterDirect:N0} MB through the overlay (its group read, the cell found, the reference found). The same JSON");
            }
            // 30 sampled cells put back together against the overlay's nested cell
            int cells = 0; long children = 0;
            var cellRefs = Sample(index.RecordsOfType(Starfield, "CELL").Where(r => index.Describe(r).Parent == null).ToList(), 30);
            var want = cellRefs.ToDictionary(r => FormKey.Factory(index.Describe(r).FormKey), r => r);
            foreach (var block in session.Mod.Cells.Records)
                foreach (var sub in block.SubBlocks)
                    foreach (var cell in sub.Cells)
                    {
                        if (!want.TryGetValue(cell.FormKey, out var r)) continue;
                        string nested = JsonNode.Parse(RecordSerializer.ToJson(cell))!.ToJsonString();
                        var loaded = e.Load(r);
                        var assembled = e.Assembled(loaded);
                        Check(assembled.ToJsonString() == nested, $"{loaded.Label}: put back together it isn't the overlay's cell: {CardCheck.FirstDifference(JsonNode.Parse(nested), assembled)}");
                        cells++; children += loaded.Children;
                    }
            Check(cells == cellRefs.Count, $"the overlay gave {cells} of the {cellRefs.Count} sampled cells");
            lines.Add($"Starfield.esm: {cells} sampled interior cells with {N(children)} records inside them, each put back together from records read from their own bytes: equal to the overlay's nested cell");
            using var proc = Process.GetCurrentProcess();
            lines.Add($"peak working set of the process {proc.PeakWorkingSet64 / 1048576:N0} MB");
        }
        return "\n      " + string.Join("\n      ", lines);
    }
}
