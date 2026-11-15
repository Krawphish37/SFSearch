// The diff engine by itself, on the JSON of real records: defaults, lists as sets, lists paired by key and by
// position, records of different types, nested records.
using System.Globalization;
using System.Text.Json.Nodes;
using SFSearch.Compare;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Compare.Tests;

static class EngineTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);
    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    static List<RecordRef> Sample(HeaderIndex index, string plugin, string signature, int max)
    {
        var all = index.RecordsOfType(plugin, signature).ToList();
        if (all.Count <= max) return all;
        var pick = new List<RecordRef>(max);
        for (int i = 0; i < max; i++) pick.Add(all[(int)((long)i * all.Count / max)]);
        return pick;
    }

    static DiffOptions Options(Ctx ctx) => new() { Label = ctx.Session.Labeller() };

    static RecordView View(Ctx ctx, string plugin, string record)
    {
        var s = ctx.Session;
        s.Engine.Sessions.Use(plugin);
        return s.View(s.Engine, s.Engine.Find(plugin, record));
    }

    // ---------------------------------------------------------------- Defaults
    /// <summary>Adds the default of every field the JSON leaves out and the schema says is always there, in the
    /// record and in the objects inside it. Returns the places where a default was added.</summary>
    static void SpellOut(RecordSchema schema, string? obj, JsonObject json, string path, List<(JsonObject Holder, string Name, string Path, string Kind)> added, int depth = 0)
    {
        if (obj == null || depth > 6) return;
        foreach (var p in schema.Props(obj))
        {
            string here = path.Length == 0 ? p.Name : path + "." + p.Name;
            if (!json.TryGetPropertyValue(p.Name, out var v))
            {
                if (p.Main.Nullable) continue;
                var d = FieldLookup.DefaultOf(schema, p.Main).Json;
                if (d == null) continue;
                json[p.Name] = d;
                added.Add((json, p.Name, here, p.Main.Kind));
                continue;
            }
            if (v is JsonObject o && p.Main.Kind == "Object" && RecordSplit.ChildOf(o) == null)
                SpellOut(schema, schema.ObjectFor(p.Main.RefObject, o[RecordSplit.TypeKey]?.GetValue<string>()), o, here, added, depth + 1);
            else if (v is JsonArray a && p.Item is { Kind: "Object" } item)
                for (int i = 0; i < a.Count; i++)
                    if (a[i] is JsonObject io && RecordSplit.ChildOf(io) == null)
                        SpellOut(schema, schema.ObjectFor(item.RefObject, io[RecordSplit.TypeKey]?.GetValue<string>()), io, $"{here}[{i}]", added, depth + 1);
        }
    }

    public static string Defaults(Ctx ctx)
    {
        var s = ctx.Session; var e = s.Engine;
        e.Sessions.Use("Starfield.esm", "weap_starlight.esm", "darkstar.esm");
        var refs = new List<RecordRef>();
        foreach (var sig in new[] { "WEAP", "ARMO", "NPC_", "COBJ", "OMOD", "AMMO", "ALCH", "MISC", "LVLI", "RACE", "PERK", "QUST" }) refs.AddRange(Sample(s.Index, "Starfield.esm", sig, 150));
        foreach (var sig in s.Index.Stats("weap_starlight.esm")) refs.AddRange(s.Index.RecordsOfType("weap_starlight.esm", sig.Signature));
        foreach (var sig in new[] { "WEAP", "ARMO", "COBJ", "OMOD" }) refs.AddRange(Sample(s.Index, "darkstar.esm", sig, 100));

        var options = Options(ctx);
        long records = 0, addedTotal = 0, controls = 0;
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in refs)
        {
            var view = s.View(e, r);
            var spelled = (JsonObject)view.Json.DeepClone();
            var added = new List<(JsonObject Holder, string Name, string Path, string Kind)>();
            SpellOut(s.Schema, view.Type, spelled, "", added);
            records++; addedTotal += added.Count;
            foreach (var a in added) kinds[a.Kind] = kinds.GetValueOrDefault(a.Kind) + 1;
            // 1. One side spells out its defaults, the other leaves them out: no difference, either way round
            var d1 = s.Differ.Diff(view.Type, view.Json, view.Type, spelled, options);
            var d2 = s.Differ.Diff(view.Type, spelled, view.Type, view.Json, options);
            Check(d1.All.Count == 0, $"{view.Label}: {d1.All.Count} difference(s) between the record and the same record with {added.Count} defaults spelled out, the first at {d1.All.FirstOrDefault()?.Path}: {d1.All.FirstOrDefault()?.A} and {d1.All.FirstOrDefault()?.B}");
            Check(d2.All.Count == 0, $"{view.Label}: {d2.All.Count} difference(s) the other way round, the first at {d2.All.FirstOrDefault()?.Path}");
            // 2. The control: one spelled-out number that isn't the default is exactly one difference, at that place
            var pick = added.FirstOrDefault(a => a.Kind is "Float" or "UInt32" or "Int32" or "UInt16" or "UInt8" && !a.Path.Contains('[') && a.Name is not ("MajorRecordFlagsRaw" or "Version2" or "FormVersion" or "VersionControl"));
            if (pick.Holder != null)
            {
                var was = pick.Holder[pick.Name]!.DeepClone();
                pick.Holder[pick.Name] = pick.Kind == "Float" ? 7.5 : 7;
                var d3 = s.Differ.Diff(view.Type, view.Json, view.Type, spelled, options);
                Check(d3.All.Count == 1 && d3.All[0].Path == pick.Path, $"{view.Label}: {pick.Path} set to 7 against a record that leaves it out gave {d3.All.Count} difference(s){(d3.All.Count > 0 ? " at " + d3.All[0].Path : "")}");
                Check(d3.All[0].Change == Change.Changed && d3.All[0].A == was.ToJsonString(), $"{view.Label}: the side that leaves {pick.Path} out is shown as {d3.All[0].A}, its default is {was.ToJsonString()}");
                pick.Holder[pick.Name] = was;
                controls++;
            }
        }
        Check(addedTotal > 10000, $"only {addedTotal} defaults were spelled out");
        return $"{N(records)} records of Starfield.esm, weap_starlight.esm and darkstar.esm, each held against itself with every left-out default spelled out ({N(addedTotal)} values: " +
            string.Join(", ", kinds.OrderByDescending(k => k.Value).Take(6).Select(k => $"{N(k.Value)} {k.Key}")) + $"): 0 differences, both ways round. Controls: in {N(controls)} records one spelled-out number was set to 7; each gave exactly 1 difference, at that field, with the default shown for the side that leaves it out";
    }

    // ---------------------------------------------------------------- Lists
    public static string Lists(Ctx ctx)
    {
        var s = ctx.Session; var options = Options(ctx);
        var done = new List<string>();
        RecordDiff Diff(RecordView v, JsonObject changed) => s.Differ.Diff(v.Type, v.Json, v.Type, changed, options);
        JsonObject Clone(RecordView v) => (JsonObject)v.Json.DeepClone();

        // A list of links is a set
        var weapon = View(ctx, "darkstar.esm", "0003C2:darkstar.esm");
        var keys = weapon.Json["Keywords"]!.AsArray().Select(k => k!.GetValue<string>()).ToList();
        Check(keys.Count == 25, $"the test weapon has {keys.Count} keywords, 25 were expected");
        {
            var b = Clone(weapon);
            b["Keywords"] = new JsonArray(keys.AsEnumerable().Reverse().Select(k => (JsonNode)k).ToArray());
            var d = Diff(weapon, b);
            Check(d.All.Count == 1 && d.All[0].Change == Change.Order && d.All[0].Top == "Keywords", $"the same 25 keywords in reverse order gave {d.All.Count} difference(s): {string.Join("; ", d.All.Select(x => x.Change + " " + x.Path))}");
            Check(d.HowMatched("Keywords") == "links as a set", $"Keywords were paired {d.HowMatched("Keywords")}");
            Check(!d.Same, "a list in another order counts as the same record");
            done.Add("25 keywords in reverse order: 1 difference, of the kind \"order\"");
        }
        {
            var b = Clone(weapon);
            const string extra = "04A0A0:Starfield.esm"; // WeaponTypePistol: not one of the 25
            Check(!keys.Contains(extra), "the test weapon already has the keyword the test adds");
            var list = keys.ToList(); string gone = list[3]; list.RemoveAt(3); list.Insert(0, extra);
            b["Keywords"] = new JsonArray(list.Select(k => (JsonNode)k).ToArray());
            var d = Diff(weapon, b);
            Check(d.All.Count == 2, $"one keyword taken out and one put in gave {d.All.Count} differences");
            var minus = d.All.Single(x => x.Change == Change.OnlyA); var plus = d.All.Single(x => x.Change == Change.OnlyB);
            Check(minus.Element == gone.ToUpperInvariant() && plus.Element == extra.ToUpperInvariant(), $"the entries are {minus.Element} and {plus.Element}");
            Check(plus.B == "WeaponTypePistol (04A0A0:Starfield.esm)", $"the added keyword is shown as {plus.B}");
            Check(minus.A == s.Engine.Label(gone) && System.Text.RegularExpressions.Regex.IsMatch(minus.A!, @"^\S+ \([0-9A-F]{6}:[^)]+\)$"), $"the removed keyword is shown as {minus.A}");
            Check(minus.Signature == "KWDA" && minus.Readable == "Keywords", $"the difference is named {minus.Readable} [{minus.Signature}]");
            done.Add($"one keyword out, one in (at another place): 2 differences, − {minus.A} and + {plus.B}, no order difference");
        }
        {
            // The same links with plugin names in other letter case, and whole numbers written with and without ".0"
            string text = weapon.Json.ToJsonString().Replace(":Starfield.esm\"", ":STARFIELD.ESM\"").Replace(":darkstar.esm\"", ":DarkStar.esm\"");
            var b = (JsonObject)JsonNode.Parse(text)!;
            b["FormKey"] = weapon.Json["FormKey"]!.GetValue<string>();
            b["AttackDamage"] = 12;
            var d = Diff(weapon, b);
            Check(d.All.Count == 0, $"plugin names in other letter case and 12 for 12.0 gave {d.All.Count} difference(s), the first at {d.All.FirstOrDefault()?.Path}");
            done.Add("every link with its plugin's name in other letter case, and 12 for 12.0: 0 differences");
        }
        {
            // A byte field with no bytes: "0x" (the serializer's 1.38.6-fixes.2 on), "[]" (before), and left out when the
            // field is always there, are one value
            var proj = View(ctx, "darkstar.esm", "003A97:darkstar.esm");
            Check(proj.Json["Unused"]?.ToJsonString() == "\"0x\"", $"{proj.Label}'s Unused is {proj.Json["Unused"]?.ToJsonString() ?? "left out"}; \"0x\" (no bytes) was expected");
            var b = Clone(proj);
            b["Unused"] = "[]";
            var d = Diff(proj, b);
            Check(d.All.Count == 0, $"\"0x\" against \"[]\" gave {d.All.Count} difference(s), the first at {d.All.FirstOrDefault()?.Path}");
            var def = s.Engine.Schema.Props(proj.Type).Single(p => p.Name == "Unused").Main;
            b.Remove("Unused");
            d = Diff(proj, b);
            Check(d.All.Count == (def.Nullable ? 1 : 0), $"\"0x\" against the field left out ({(def.Nullable ? "nullable" : "always there")}) gave {d.All.Count} difference(s)");
            done.Add($"an empty byte field of {proj.Label}: \"0x\" against \"[]\" 0 differences; against the field left out {d.All.Count} ({(def.Nullable ? "a field that may be absent" : "always there, so its default \"0x\"")})");
        }
        {
            // Flags: a set, order is nothing
            var npc = View(ctx, "Starfield.esm", "000007:Starfield.esm");
            var b = Clone(npc);
            var flags = npc.Json["Flags"] as JsonArray;
            Check(flags is { Count: >= 2 }, $"the flags of {npc.Label} are {npc.Json["Flags"]?.ToJsonString()}; two or more were expected");
            b["Flags"] = new JsonArray(flags!.Reverse().Select(f => f!.DeepClone()).ToArray());
            Check(Diff(npc, b).All.Count == 0, "the same flags in another order are a difference");
            var fewer = flags!.Skip(1).Select(f => f!.DeepClone()).ToArray();
            b["Flags"] = new JsonArray(fewer);
            var d = Diff(npc, b);
            Check(d.All.Count == 1 && d.All[0].Note!.Contains("cleared: " + flags![0]!.GetValue<string>(), StringComparison.Ordinal), $"one flag cleared gave {d.All.Count} difference(s): {d.All.FirstOrDefault()?.Note}");
            done.Add($"the {flags!.Count} flags of {npc.Label} in another order: 0 differences; one flag cleared: 1 difference (\"{d.All[0].Note}\")");
        }

        // A list of objects paired by its key field
        var recipe = View(ctx, "weap_starlight.esm", "00084A:weap_starlight.esm");
        var parts = recipe.Json["ConstructibleComponents"]!.AsArray();
        Check(parts.Count == 3, $"the test recipe has {parts.Count} components, 3 were expected");
        Check(s.Differ.Keys.For("ConstructibleObjectComponent")?.Field == "Component", $"the schema's key for a recipe component is {s.Differ.Keys.For("ConstructibleObjectComponent")?.Field}");
        {
            var b = Clone(recipe);
            b["ConstructibleComponents"] = new JsonArray(parts.Reverse().Select(p => p!.DeepClone()).ToArray());
            var d = Diff(recipe, b);
            Check(d.All.Count == 1 && d.All[0].Change == Change.Order, $"3 components in reverse order gave {string.Join("; ", d.All.Select(x => x.Change + " " + x.Path))}");
            done.Add("3 recipe components in reverse order: 1 difference, of the kind \"order\"");
        }
        {
            var b = Clone(recipe);
            var list = b["ConstructibleComponents"]!.AsArray();
            string second = list[1]!["Component"]!.GetValue<string>();
            var moved = list[1]!.DeepClone(); moved["RequiredCount"] = 9;
            list.RemoveAt(1); list.Insert(0, moved);                      // changed, and moved to the front
            string first = list[1]!["Component"]!.GetValue<string>();
            list.RemoveAt(1);                                             // the old first one taken out
            list.Add(new JsonObject { ["Component"] = "005570:Starfield.esm", ["RequiredCount"] = 2 });   // a new one
            var d = Diff(recipe, b);
            var changed = d.All.SingleOrDefault(x => x.Change == Change.Changed);
            Check(changed != null && changed.Path == $"ConstructibleComponents[Component={second.ToUpperInvariant()}].RequiredCount" && changed.A == "1" && changed.B == "9",
                $"the component whose count changed is reported as {changed?.Path}: {changed?.A} → {changed?.B}; all: {string.Join("; ", d.All.Select(x => x.Change + " " + x.Path))}");
            Check(changed!.Shown == $"ConstructibleComponents[{s.Engine.Label(second)}].RequiredCount", $"it is shown as {changed.Shown}");
            Check(d.All.Count(x => x.Change == Change.OnlyA) == 1 && d.All.Single(x => x.Change == Change.OnlyA).Path == $"ConstructibleComponents[Component={first.ToUpperInvariant()}]", "the component taken out isn't reported as only in a");
            Check(d.All.Count(x => x.Change == Change.OnlyB) == 1 && d.All.Single(x => x.Change == Change.OnlyB).Path == "ConstructibleComponents[Component=005570:STARFIELD.ESM]", "the new component isn't reported as only in b");
            Check(d.HowMatched("ConstructibleComponents") == "by Component", $"the list was paired {d.HowMatched("ConstructibleComponents")}");
            Check(d.All.Count(x => x.Change == Change.Order) <= 1 && d.All.Count <= 4, $"{d.All.Count} differences in all");
            done.Add($"recipe components paired by Component: one count changed and moved ({changed.Shown}: 1 → 9), one taken out, one new");
        }

        // A list of objects with no key: by position, with wholly equal entries paired first
        {
            s.Engine.Sessions.Use("Starfield.esm");
            RecordView? rec = null;
            foreach (var r in s.Index.RecordsOfType("Starfield.esm", "COBJ"))
            {
                var v = s.View(s.Engine, r);
                if (v.Json["Conditions"] is JsonArray c && c.Count >= 3 && c.Select(x => x!.ToJsonString()).Distinct().Count() == c.Count
                    && c.All(x => x is JsonObject o && o[RecordSplit.TypeKey]?.GetValue<string>() == "ConditionFloat")) { rec = v; break; }
            }
            if (rec == null) throw new TestFailed("no recipe with three different conditions of one kind was found in Starfield.esm");
            var b = Clone(rec);
            var list = b["Conditions"]!.AsArray();
            list[1]!["ComparisonValue"] = 4242.0;
            var d = Diff(rec, b);
            Check(d.All.Count == 1 && d.All[0].Path == "Conditions[1].ComparisonValue" && d.All[0].B == "4242" && d.HowMatched("Conditions") == "by position",
                $"one condition's value changed gave {string.Join("; ", d.All.Select(x => x.Change + " " + x.Path + " " + x.B))}, paired {d.HowMatched("Conditions")}");
            // A new condition put in front: one entry only in b, nothing else
            var b2 = Clone(rec);
            var front = b2["Conditions"]!.AsArray()[0]!.DeepClone(); front["ComparisonValue"] = 777.0;
            b2["Conditions"]!.AsArray().Insert(0, front);
            var d2 = Diff(rec, b2);
            Check(d2.All.Count == 1 && d2.All[0].Change == Change.OnlyB, $"a condition put in front of {list.Count} gave {string.Join("; ", d2.All.Select(x => x.Change + " " + x.Path))}");
            done.Add($"conditions of {rec.Label} (no key field, {list.Count} of one kind) by position: one value changed is 1 difference at Conditions[1].ComparisonValue; a condition put in front is 1 entry only in b, and the {list.Count} others still pair");
        }

        // Translated text: the same English text, one side with one language, the other with nine
        {
            s.Engine.Sessions.Use("atom74.esm");
            var localized = View(ctx, "atom74.esm", "0009B7:atom74.esm");
            var name = localized.Json["Name"]!.AsObject();
            Check(name["Values"] is JsonArray { Count: > 1 }, "atom74.esm's weapon was expected to have its name in several languages");
            string english = name["Values"]!.AsArray().Select(v => v!.AsObject()).First(v => v["Language"]!.GetValue<string>() == "English")["String"]!.GetValue<string>();
            var b = Clone(localized);
            b["Name"] = new JsonObject { ["TargetLanguage"] = "English", ["Value"] = english };
            var d = Diff(localized, b);
            // The weapon's name is the same text in all nine languages: one text for every language is the same value, written another way
            Check(name["Values"]!.AsArray().All(v => v!["String"]!.GetValue<string>() == english), "atom74.esm's weapon was expected to have one text in all its languages");
            Check(d.Same && d.NotValues.Count() == 1 && d.All[0].Class == DiffClass.WrittenAnotherWay && d.All[0].Top == "Name" && d.RewrittenFields.SequenceEqual(new[] { "Name" }),
                $"a name every language shows the same, against that text alone, gave {d.Values.Count()} value difference(s), {d.NotValues.Count()} other ({string.Join(", ", d.NotValues.Select(x => x.Class))})");
            Check(CompareAnswer.FieldNames(d).Contains("the same value, written another way: Name", StringComparison.Ordinal), $"the field names leave the rewritten text out: {CompareAnswer.FieldNames(d)}");
            b["Name"] = new JsonObject { ["TargetLanguage"] = "English", ["Value"] = english + "!" };
            var d2 = Diff(localized, b);
            Check(d2.Values.Count() == 1 && d2.All[0].B!.StartsWith($"\"{english}!\"", StringComparison.Ordinal), $"another English text gave {d2.Values.Count()} value difference(s): {d2.All.FirstOrDefault()?.B}");

            // One language's text changed (German), the English the same: a change of the field, that says which language
            var c = Clone(localized);
            foreach (var v in c["Name"]!["Values"]!.AsArray()) if (v!["Language"]!.GetValue<string>() == "German") v["String"] = "Atom-74 Mk.II";
            var d3 = Diff(localized, c);
            Check(!d3.Same && d3.Values.Count() == 1 && d3.TopFields.SequenceEqual(new[] { "Name" }) && d3.All[0].Note!.Contains("German", StringComparison.Ordinal),
                $"German changed alone gave {d3.Values.Count()} value difference(s): {string.Join("; ", d3.All.Select(x => x.Class + " " + x.Note))}");
            // A language dropped (French), every other text the same: a change of the field, that names the language
            var e = Clone(localized);
            var values = e["Name"]!["Values"]!.AsArray();
            values.Remove(values.First(v => v!["Language"]!.GetValue<string>() == "French"));
            var d4 = Diff(localized, e);
            Check(!d4.Same && d4.Values.Count() == 1 && d4.All[0].Note!.Contains("languages dropped: French", StringComparison.Ordinal),
                $"French dropped gave {d4.Values.Count()} value difference(s): {string.Join("; ", d4.All.Select(x => x.Class + " " + x.Note))}");
            // No text against an empty text: a change of the field
            var f = Clone(localized); var g = Clone(localized);
            f["Description"] = new JsonObject { ["TargetLanguage"] = "English" };
            g["Description"] = new JsonObject { ["TargetLanguage"] = "English", ["Value"] = "" };
            var d5 = s.Differ.Diff(localized.Type, f, localized.Type, g, options);
            Check(!d5.Same && d5.TopFields.SequenceEqual(new[] { "Description" }) && d5.All[0].A == "(no text)" && d5.All[0].B == "\"\"",
                $"no description against an empty one gave {d5.Values.Count()} value difference(s): {string.Join("; ", d5.All.Select(x => $"{x.Class} {x.Top} {x.A} → {x.B}"))}");
            done.Add($"a name every one of its 9 languages shows as \"{english}\", against that text alone: no value differs, the field listed as \"the same value, written another way\"; another English text: 1 difference; " +
                "German's text changed alone: 1 difference, naming German; French dropped: 1 difference, \"languages dropped: French\"; no description against an empty one: 1 difference, (no text) → \"\"");
        }
        return string.Join(". ", done);
    }

    // ---------------------------------------------------------------- Types, nested records
    public static string TypesAndChildren(Ctx ctx)
    {
        var s = ctx.Session; var options = Options(ctx);
        var done = new List<string>();
        // Two records of different types are reported as that
        var weapon = View(ctx, "darkstar.esm", "0003C2:darkstar.esm");
        s.Engine.Sessions.Use("darkstar.esm", "Starfield.esm", "kgcdoom.esm");
        var armor = s.View(s.Engine, s.Index.RecordsOfType("darkstar.esm", "ARMO").First());
        var d = s.Differ.Diff(weapon.Type, weapon.Json, armor.Type, armor.Json, options);
        Check(d.DifferentTypes && d.All.Count == 0 && !d.Same, $"a Weapon against an Armor gave {d.All.Count} field differences");
        var answer = ctx.Compare.Records("darkstar.esm", weapon.Info.FormKey, "darkstar.esm", armor.Info.FormKey, new[] { "Keywords" });
        string text = answer.ToText();
        Check(text.Contains("records of different types: a is a WEAP Weapon, b is a ARMO Armor", StringComparison.Ordinal) && answer.Failed, $"the answer for a Weapon against an Armor:\n{text}");
        done.Add($"{weapon.Label} against {armor.Label}: \"records of different types\", no field compared, a failing result when fields were expected");
        // The same signature, two Mutagen types (weapon and armor modifications are both OMOD)
        var omods = s.Index.RecordsOfType("darkstar.esm", "OMOD").Select(r => s.View(s.Engine, r)).ToList();
        var wm = omods.FirstOrDefault(v => v.Type == "WeaponModification"); var am = omods.FirstOrDefault(v => v.Type == "ArmorModification");
        if (wm != null && am != null)
        {
            var dd = s.Differ.Diff(wm.Type, wm.Json, am.Type, am.Json, options);
            Check(dd.DifferentTypes && dd.All.Count == 0, "a weapon modification against an armor modification was compared field by field");
            done.Add($"{wm.Label} (WeaponModification) against {am.Label} (ArmorModification), both OMOD: different types");
        }

        // A cell that kgcdoom.esm overrides: its nested records are records of their own, not fields
        var mine = View(ctx, "kgcdoom.esm", "024ADB:Starfield.esm");
        var theirs = View(ctx, "Starfield.esm", "024ADB:Starfield.esm");
        int markersMine = Markers(mine.Json), markersTheirs = Markers(theirs.Json);
        var cd = s.Differ.Diff(theirs.Type, theirs.Json, mine.Type, mine.Json, options);
        Check(markersMine > 0 && markersTheirs > 0, $"the cell holds {markersTheirs} nested records in Starfield.esm and {markersMine} in kgcdoom.esm");
        Check(cd.ChildrenOnlyA.Count + cd.ChildrenInBoth.Count == markersTheirs && cd.ChildrenOnlyB.Count + cd.ChildrenInBoth.Count == markersMine,
            $"nested records: {cd.ChildrenOnlyA.Count} only in Starfield.esm, {cd.ChildrenOnlyB.Count} only in kgcdoom.esm, {cd.ChildrenInBoth.Count} in both; the JSON holds {markersTheirs} and {markersMine}");
        Check(!cd.All.Any(x => x.Top is "Persistent" or "Temporary" or "NavigationMeshes"), "the lists of nested records are reported as field differences");
        done.Add($"{mine.Label}, a cell of Starfield.esm that kgcdoom.esm overrides: {N(markersTheirs)} nested records in Starfield.esm, {N(markersMine)} in kgcdoom.esm ({N(cd.ChildrenInBoth.Count)} in both); none is reported as a field, the cell's own fields give {cd.Values.Count()} difference(s)");
        var cells = ctx.Compare.Records("Starfield.esm", "024ADB:Starfield.esm", "kgcdoom.esm", "024ADB:Starfield.esm");
        Check(cells.Children.Count == Math.Min(cd.ChildrenInBoth.Count, 500), $"{cells.Children.Count} nested pairs were compared as records of their own, {cd.ChildrenInBoth.Count} are under both");
        Check(cells.ToText().Contains("Nested records (records of their own, not fields)", StringComparison.Ordinal), "the answer doesn't name the nested records");
        done.Add($"the records answer compares the {cells.Children.Count} nested records under both as records of their own: {cells.Children.Count(c => c.Diff is { Same: false })} differ");
        return string.Join(". ", done);

        static int Markers(JsonNode? n) => n switch
        {
            JsonObject o => RecordSplit.ChildOf(o) != null ? 1 : o.Sum(p => Markers(p.Value)),
            JsonArray a => a.Sum(Markers),
            _ => 0,
        };
    }
}
