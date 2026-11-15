// profile (GAPS S40): a field across plugins, held against Mutagen read directly; and the GRUP-header view, held
// against a walk of the file's bytes written here on its own.
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SFSearch.Index;
using SFSearch.Reports;

namespace SFSearch.Reports.Tests;

static class ProfileTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Four fields of different shapes over Starfield.esm, ShatteredSpace.esm and sfta01.esm: a top-level float, a 4-byte number some
    /// of whose values are FormIDs, a run of bytes in a list of kinds of object, and the same path asked without its kind
    /// (which gives every kind that has it). Each plugin's counts and the values are held against the plugin's Mutagen
    /// overlay read group by group (not through the header index), each sample against its record's value there, and
    /// the FormID reads against the header index.
    /// </summary>
    public static string Fields(Ctx ctx)
    {
        var s = ctx.Session;
        var report = new ProfileReport(s);
        string[] plugins = { "Starfield.esm", "ShatteredSpace.esm", "sfta01.esm" };
        var plan = report.Plan(new ProfileOptions
        {
            Fields = new[] { "Static.UnknownDNAMFloat", "PNDT.OrbitalData.Unknown1", "Activator.Conditions[*]{ConditionFloat}.Unknown1", "Activator.Conditions[*].Unknown2" },
            In = string.Join(",", plugins),
        });
        var names = plan.Targets.Select(t => t.Name).ToList();
        Check(names.SequenceEqual(new[]
        {
            "Static.UnknownDNAMFloat", "Planet.OrbitalData.Unknown1", "Activator.Conditions[*]{ConditionFloat}.Unknown1",
            "Activator.Conditions[*]{ConditionFloat}.Unknown2", "Activator.Conditions[*]{ConditionGlobal}.Unknown2",
        }), "the fields asked: " + string.Join(", ", names));
        var a = report.Run(plan, 3);
        ctx.WriteReport("profile-fields.txt", a.ToText(new ReportTextOptions { Detail = true }));
        FieldStats F(string name) => a.Fields.Single(f => f.Target.Name == name);

        // Mutagen read directly: each plugin's overlay, its groups enumerated
        var expect = new Dictionary<string, Dictionary<string, (long Records, long RecordsNonZero, long Values, long NonZero)>>();
        var values = new Dictionary<string, Dictionary<string, long>>();
        var where = new Dictionary<(string Field, string Plugin, string FormKey, string Place), string>();
        void Add(string field, string plugin, string formKey, string place, string repr, bool zero, ref bool any, ref bool anyNz)
        {
            var byPlugin = expect.TryGetValue(field, out var d) ? d : expect[field] = new();
            var c = byPlugin.GetValueOrDefault(plugin);
            c.Values++; if (!zero) c.NonZero++;
            if (!any) { c.Records++; any = true; }
            if (!zero && !anyNz) { c.RecordsNonZero++; anyNz = true; }
            byPlugin[plugin] = c;
            var v = values.TryGetValue(field, out var vd) ? vd : values[field] = new();
            v[repr] = v.GetValueOrDefault(repr) + 1;
            where[(field, plugin, formKey, place)] = repr;
        }
        static string F32(float f) => f.ToString("R", CultureInfo.InvariantCulture);
        static string Hex(ReadOnlySpan<byte> b) => "0x" + Convert.ToHexString(b);
        foreach (var p in plugins)
        {
            var mod = s.Engine.Sessions.Get(p).Mod;
            foreach (var st in mod.Statics)
            {
                bool any = false, nz = false;
                Add("Static.UnknownDNAMFloat", p, st.FormKey.ToString(), "UnknownDNAMFloat", F32(st.UnknownDNAMFloat), st.UnknownDNAMFloat == 0, ref any, ref nz);
            }
            foreach (var pl in mod.Planets)
            {
                if (pl.OrbitalData is not { } od) continue;
                bool any = false, nz = false;
                Add("Planet.OrbitalData.Unknown1", p, pl.FormKey.ToString(), "OrbitalData.Unknown1", od.Unknown1.ToString(CultureInfo.InvariantCulture), od.Unknown1 == 0, ref any, ref nz);
            }
            foreach (var ac in mod.Activators)
            {
                bool any1 = false, nz1 = false, any2 = false, nz2 = false, any3 = false, nz3 = false;
                int i = 0;
                foreach (var c in ac.Conditions ?? (IReadOnlyList<IConditionGetter>)Array.Empty<IConditionGetter>())
                {
                    int at = i++;
                    if (c is IConditionFloatGetter cf)
                    {
                        var b = cf.Unknown1.Span;
                        Add("Activator.Conditions[*]{ConditionFloat}.Unknown1", p, ac.FormKey.ToString(), $"Conditions[{at}].Unknown1", Hex(b), !b.ContainsAnyExcept((byte)0), ref any1, ref nz1);
                        Add("Activator.Conditions[*]{ConditionFloat}.Unknown2", p, ac.FormKey.ToString(), $"Conditions[{at}].Unknown2", cf.Unknown2.ToString(CultureInfo.InvariantCulture), cf.Unknown2 == 0, ref any2, ref nz2);
                    }
                    else if (c is IConditionGlobalGetter cg)
                        Add("Activator.Conditions[*]{ConditionGlobal}.Unknown2", p, ac.FormKey.ToString(), $"Conditions[{at}].Unknown2", cg.Unknown2.ToString(CultureInfo.InvariantCulture), cg.Unknown2 == 0, ref any3, ref nz3);
                }
            }
        }
        var parts = new List<string>();
        foreach (var name in names)
        {
            var f = F(name);
            var want = expect.GetValueOrDefault(name) ?? new();
            foreach (var p in plugins)
            {
                var w = want.GetValueOrDefault(p);
                var got = f.Plugins.GetValueOrDefault(p) ?? new PluginCounts();
                Check(got.Records == w.Records && got.RecordsNonZero == w.RecordsNonZero && got.Values == w.Values && got.NonZero == w.NonZero,
                    $"{name} in {p}: profile {got.Records}/{got.RecordsNonZero}/{got.Values}/{got.NonZero}, Mutagen {w.Records}/{w.RecordsNonZero}/{w.Values}/{w.NonZero} (records, nonzero records, values, nonzero)");
            }
            var wantValues = values.GetValueOrDefault(name) ?? new();
            Check(f.Counts.Count == wantValues.Count && f.Counts.All(kv => wantValues.GetValueOrDefault(kv.Key) == kv.Value),
                $"{name}: the values differ from Mutagen's: profile {string.Join("; ", f.Top(6).Select(v => v.Key + " ×" + v.Value))}; Mutagen {string.Join("; ", wantValues.OrderByDescending(v => v.Value).Take(6).Select(v => v.Key + " ×" + v.Value))}");
            // Each sample's value is its record's value at that place
            foreach (var smp in f.Samples)
            {
                string? have = where.FirstOrDefault(x => x.Key.Field == name && x.Key.Plugin == smp.Plugin && x.Key.FormKey.Equals(smp.FormKey, StringComparison.OrdinalIgnoreCase) && x.Key.Place == smp.Place).Value;
                Check(have != null && (have == smp.Value || smp.Value.StartsWith(have + " (", StringComparison.Ordinal)), $"{name}: the sample {smp.Label} {smp.Place} = {smp.Value}, Mutagen gives {have ?? "nothing there"}");
            }
            Check(f.Samples.Select(x => (x.Plugin, x.FormKey)).Distinct().Count() == f.Samples.Count, $"{name}: two samples of one record");
            parts.Add($"{name.Split('.')[0]}.{name.Split('.').Last()} {N(f.Values)} values, {f.Distinct} distinct, {f.Samples.Count} samples");
        }
        // The FormID reads of OrbitalData.Unknown1: each value of 0x1000 or more that names a record its owner holds
        {
            var f = F("Planet.OrbitalData.Unknown1");
            long hits = 0, tested = 0;
            foreach (var (repr, n) in values["Planet.OrbitalData.Unknown1"])
            {
                uint w = uint.Parse(repr, CultureInfo.InvariantCulture);
                if (w == 0 || (w & 0xFFFFFF) < 0x1000) continue;
                tested += n;
                // Starfield.esm has no masters; the first master of ShatteredSpace.esm and sfta01.esm is Starfield.esm: 00xxxxxx is Starfield.esm's
                if (w >> 24 == 0 && s.Index.Plugin("Starfield.esm")!.Segment is { } seg && seg.Find(seg.SelfOwner, w) >= 0) hits += n;
            }
            var ws = f.Words[-1];
            Check(ws.Tested == tested && ws.FormIdHits == hits && hits > 0 && ws.FormIdTypes.Keys.SequenceEqual(new[] { "REFR" }),
                $"OrbitalData.Unknown1's FormID reads: profile {ws.FormIdHits} of {ws.Tested} ({string.Join(", ", ws.FormIdTypes.Select(t => t.Key + " " + t.Value))}), expected {hits} of {tested}, all REFR");
            parts.Add($"OrbitalData.Unknown1 {hits} FormIDs (REFR)");
        }
        // The answer reads the same whatever the threads did: a second run gives the same text
        var again = report.Run(report.Plan(new ProfileOptions { Fields = new[] { "Activator.Conditions[*]{ConditionFloat}.Unknown1" }, In = string.Join(",", plugins) }), 3);
        var first = a.Fields.Single(x => x.Target.Name == "Activator.Conditions[*]{ConditionFloat}.Unknown1");
        Check(again.Fields[0].Samples.Select(x => x.FormKey + x.Place).SequenceEqual(first.Samples.Select(x => x.FormKey + x.Place)), "the samples differ between two runs");

        // Wrong names say what is there
        Fails(() => report.Plan(new ProfileOptions { Fields = new[] { "Weapon.Nope" }, In = "kgcdoom.esm" }), "has no field Nope", "describe Weapon");
        Fails(() => report.Plan(new ProfileOptions { Fields = new[] { "Wepon.AttackDamage" }, In = "kgcdoom.esm" }), "no record type named \"Wepon\"", "Weapon");
        Fails(() => report.Plan(new ProfileOptions { Fields = new[] { "Weapon.AttackDamage" }, In = "@winners,kgcdoom.esm" }), "@winners can't be mixed", null);
        Fails(() => report.Plan(new ProfileOptions { Fields = new[] { "Weapon.AttackDamage" }, In = "@offical" }), "isn't a set of plugins", "@official");
        // @official is the game's own plugins and Bethesda's Creations
        var off = PluginSet.Parse(s.Index, "@official");
        Check(off.Plugins.Count == 33 && off.Plugins[0].Name == "Starfield.esm" && off.Plugins.All(p => PluginSet.IsOfficial(p.Name)) && !PluginSet.IsOfficial("darkstar.esm"),
            $"@official: {off.Plugins.Count} plugins ({string.Join(", ", off.Plugins.Take(4).Select(p => p.Name))}, …)");
        // *.Path: the field in every record type that has it
        var every = report.Plan(new ProfileOptions { Fields = new[] { "*.UnknownDNAMFloat" }, In = "kgcdoom.esm" });
        Check(every.Targets.Select(t => t.Name).SequenceEqual(new[] { "Static.UnknownDNAMFloat" }), "*.UnknownDNAMFloat: " + string.Join(", ", every.Targets.Select(t => t.Name)));

        // The verb: its JSON holds the same numbers, and a wrong field is one error line with exit code 2
        if (ctx.CanRunToday)
        {
            string json = ctx.Today("profile__json", "--json", "profile", "Static.UnknownDNAMFloat", "--in", string.Join(",", plugins))!;
            var o = JsonNode.Parse(json)!["data"]!["fields"]![0]!;
            Check(o["values"]!.GetValue<long>() == F("Static.UnknownDNAMFloat").Values && o["field"]!.GetValue<string>() == "Static.UnknownDNAMFloat", "the verb's JSON: " + json[..Math.Min(300, json.Length)]);
            string err = ctx.Today("profile__wrong", "profile", "Weapon.Nope", "kgcdoom.esm")!;
            Check(err.StartsWith("Error: Weapon has no field Nope", StringComparison.Ordinal) && err.Contains("Fix: describe Weapon", StringComparison.Ordinal), "the verb's error: " + err);
            parts.Add("verb JSON and error line");
        }
        return string.Join("; ", parts);
    }

    static void Fails(Action run, string message, string? fix)
    {
        try { run(); }
        catch (ReportException e)
        {
            Check(e.Message.Contains(message, StringComparison.Ordinal) && (fix == null || (e.Fix ?? "").Contains(fix, StringComparison.Ordinal)), $"the error \"{e.Line}\" should hold \"{message}\"{(fix != null ? $" and a fix with \"{fix}\"" : "")}");
            return;
        }
        throw new TestFailed($"no error where one was due: \"{message}\"");
    }

    // ---------------------------------------------------------------- GRUP headers
    /// <summary>One GRUP header as the walk here reads it.</summary>
    sealed record Raw(long Offset, int Depth, uint Label, int Type, ushort Stamp, ushort Version, uint Unknown, uint Size, string Top);

    /// <summary>A plugin's GRUP headers, read with a plain stream: a group's children are read by recursion.</summary>
    static List<Raw> RawGroups(string path)
    {
        var list = new List<Raw>();
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var h = new byte[24];
        f.ReadExactly(h);
        long start = 24 + BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
        void Walk(long from, long to, int depth, string top)
        {
            long pos = from;
            while (pos < to)
            {
                f.Position = pos;
                f.ReadExactly(h);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
                if (Encoding.ASCII.GetString(h, 0, 4) != "GRUP") { pos += 24 + size; continue; }
                int type = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(12));
                string t = depth == 0 ? Encoding.ASCII.GetString(h, 8, 4) : top;
                list.Add(new Raw(pos, depth, BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8)), type, BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(16)),
                    BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(18)), BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(20)), size, t));
                Walk(pos + 24, pos + size, depth + 1, t);
                pos += size;
            }
        }
        Walk(start, f.Length, 0, "");
        return list;
    }

    /// <summary>
    /// The GRUP headers of BlueprintShips-SFBGS050.esm (whose groups set the Unknown at bytes 20-23) and kgcdoom.esm (a mod with
    /// every kind of group), one row each, held against the walk above; the labels of top groups and of a cell's
    /// children; a type's and a group's narrowing; and over both plugins each group type's counts.
    /// </summary>
    public static string Groups(Ctx ctx)
    {
        var s = ctx.Session;
        var report = new GroupsReport(s);
        var parts = new List<string>();
        string[] plugins = { "BlueprintShips-SFBGS050.esm", "kgcdoom.esm" };
        var all = new List<Raw>();
        var byPlugin = new Dictionary<string, List<Raw>>();
        foreach (var p in plugins)
        {
            var raw = byPlugin[p] = RawGroups(s.Index.Plugin(p)!.Path);
            all.AddRange(raw);
            var a = report.Groups(new GroupsOptions { In = p });
            ctx.WriteReport($"profile-groups-{p}.txt", a.ToText(new ReportTextOptions { MaxChars = 1 << 20 }));
            Check(a.Rows.Count == raw.Count, $"{p}: {a.Rows.Count} groups listed, the walk finds {raw.Count}");
            for (int i = 0; i < raw.Count; i++)
            {
                var r = a.Rows[i]; var w = raw[i];
                Check(r.Offset == w.Offset && r.Depth == w.Depth && r.Label == w.Label && r.GroupType == w.Type && r.Stamp == w.Stamp && r.Version == w.Version && r.Unknown == w.Unknown && r.Size == w.Size && r.Top == w.Top,
                    $"{p} group {i} at 0x{w.Offset:X}: listed {r.Offset:X}/{r.GroupType}/{r.Label:X8}/{r.Unknown}/{r.Size}/{r.Top}, the walk {w.Offset:X}/{w.Type}/{w.Label:X8}/{w.Unknown}/{w.Size}/{w.Top}");
                if (r.GroupType == 0) Check(r.LabelText == w.Top, $"{p}: a top group's label reads {r.LabelText}, its bytes {w.Top}");
            }
            // A cell's children are labelled with the cell: its EditorID and FormKey, and CELL
            if (a.Rows.FirstOrDefault(r => r.GroupType == 6) is { } cc)
                Check(cc.LabelText.EndsWith(" CELL", StringComparison.Ordinal) && cc.LabelText.Contains(':'), $"{p}: a cell's children group is labelled \"{cc.LabelText}\"");
            parts.Add($"{p} {N(raw.Count)} groups, {raw.Count(g => g.Unknown != 0)} with the Unknown set");
        }
        // Over both: each group type's counts and the Unknown's values
        var both = report.Groups(new GroupsOptions { In = string.Join(",", plugins) });
        Check(!both.Listed && both.Rows.Count == 0, "two plugins: no row per group unless asked");
        foreach (var g in all.GroupBy(r => r.Type))
        {
            var st = both.ByType[g.Key];
            Check(st.N == g.Count() && st.NonZero == g.Count(r => r.Unknown != 0) && st.StampZero == g.Count(r => r.Stamp == 0) && st.Values.Count == g.Select(r => r.Unknown).Distinct().Count(),
                $"group type {g.Key}: {st.N} groups, {st.NonZero} set; the walk {g.Count()}, {g.Count(r => r.Unknown != 0)}");
        }
        Check(all.Any(r => r.Unknown != 0), "BlueprintShips-SFBGS050.esm sets the Unknown in some groups (C208 counted 10 interior cell blocks)");
        // --type: the top group of a type and what is inside it; --group: one group and the groups right inside it
        var cells = report.Groups(new GroupsOptions { In = plugins[0], Type = "CELL" });
        Check(cells.Rows.Count > 0 && cells.Rows.All(r => r.Top == "CELL") && cells.Rows.Count == byPlugin[plugins[0]].Count(r => r.Top == "CELL"), $"--type CELL: {cells.Rows.Count} groups, the walk {byPlugin[plugins[0]].Count(r => r.Top == "CELL")}");
        var block = cells.Rows.First(r => r.GroupType == 2);
        var one = report.Groups(new GroupsOptions { In = plugins[0], Group = "0x" + block.Offset.ToString("X", CultureInfo.InvariantCulture) });
        Check(one.Rows[0].Offset == block.Offset && one.Rows.Skip(1).All(r => r.Depth == block.Depth + 1 && r.Offset > block.Offset && r.Offset < block.Offset + block.Size) && one.Rows.Count == 1 + block.Groups,
            $"--group 0x{block.Offset:X}: {one.Rows.Count} rows, the block holds {block.Groups} groups");
        parts.Add($"--type CELL {cells.Rows.Count} groups; --group a block and its {block.Groups} sub-blocks");

        if (ctx.CanRunToday)
        {
            string json = ctx.Today("profile__groups", "--json", "profile", "--groups", "--in", "kgcdoom.esm")!;
            var o = JsonNode.Parse(json)!["data"]!;
            Check(o["groupsTotal"]!.GetValue<int>() == byPlugin["kgcdoom.esm"].Count && o["groupTypes"]!.AsArray().Count == byPlugin["kgcdoom.esm"].Select(r => r.Type).Distinct().Count(), "the verb's JSON: " + json[..Math.Min(300, json.Length)]);
            parts.Add("verb JSON");
        }
        return string.Join("; ", parts);
    }
}
