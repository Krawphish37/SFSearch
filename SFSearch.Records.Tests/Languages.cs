// Translated text in every language Starfield ships, through every view of a record (fix-lang, 2026-11-12).
using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Strings;

namespace SFSearch.Records.Tests;

static class Languages
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);

    /// <summary>A record and one of its texts: where the text sits in the JSON, and how Mutagen gives it directly.</summary>
    sealed record Case(string Plugin, string Record, string Path, bool Localized, string Why);

    static readonly Case[] Cases =
    {
        new("Starfield.esm", "0476C4:Starfield.esm", "Name", true, "Eon, read through its group"),
        new("SFBGS003.esm", "006C1C:SFBGS003.esm", "Name", true, "PlasmaCutter, a DLC weapon"),
        new("GB_ShipBuilderConfigurator_free.esm", "000003:GB_ShipBuilderConfigurator_free.esm", "Name", true, "GB_SBC_FloorChangeSpeed, a mod with its own strings files"),
        new("Starfield.esm", "000812:Starfield.esm", "Responses[0].ResponseText", true, "a response inside a topic, read from its own bytes"),
        new("df_firearms_modifications.esm", "0476C4:Starfield.esm", "Name", false, "Eon as the game uses it: a plugin with no strings files"),
        new("economy.esm", "111EA8:Starfield.esm", "Name", false, "City_newatlantis_z_theartcollector_artwork: a stored text with a code-page 1252 quote"),
    };

    /// <summary>
    /// Each case's text in each of the nine languages Starfield ships, through the card, the fields view and the JSON
    /// of an engine opened in that language, held against Mutagen read directly in that language. A plugin with no
    /// strings files has one text: every language gives it, the same as English, and the answer's note says so.
    /// </summary>
    public static string EveryView(Ctx ctx)
    {
        var languages = SFSearch.Query.TextLanguages.Shipped.Select(s => Enum.Parse<Language>(s.Name)).ToList();
        int checks = 0;
        var english = new Dictionary<Case, string>();
        var samples = new List<string>();
        foreach (var language in languages)
        {
            using var e = new RecordEngine(ctx.DataIndex, ctx.Schema, Path.Combine(ctx.Work, "verify-lang"), ctx.Data, language);
            foreach (var c in Cases)
            {
                e.Sessions.Use(c.Plugin);
                var rec = e.Load(e.Find(c.Plugin, c.Record));
                string expected = Direct(ctx, e, rec, c, language)
                    ?? throw new TestFailed($"{c.Why}: Mutagen read directly gives no {language} text at {c.Path}");
                if (language == Language.English) english[c] = expected;
                string label = $"{rec.Label} of {c.Plugin} in {language}";

                // The JSON: every language by name for a localized plugin, the one stored text otherwise
                var json = e.Json(rec);
                var node = At(json, c.Path) as JsonObject ?? throw new TestFailed($"{label}: the JSON has no text at {c.Path}");
                string? fromJson = c.Localized
                    ? (node["Values"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(v => v["Language"]?.GetValue<string>() == language.ToString())?["String"]?.GetValue<string>()
                    : node["Value"]?.GetValue<string>();
                Check(fromJson == expected, $"{label}: the JSON's {c.Path} gives \"{fromJson}\", Mutagen read directly \"{expected}\"");
                if (c.Localized) Check(node["TargetLanguage"]?.GetValue<string>() == language.ToString(), $"{label}: the JSON's target language is {node["TargetLanguage"]}");

                // The card and the fields view show the text without the Creation Kit's tag
                string shown = CardBuilder.NoTag(expected)!;
                var card = e.Card(rec, json);
                var field = card.Values().FirstOrDefault(f => f.Path == c.Path) ?? throw new TestFailed($"{label}: the card has no {c.Path}");
                Check(field.Text == shown, $"{label}: the card shows \"{field.Text}\", Mutagen read directly \"{shown}\"");
                var value = e.Fields(rec, new[] { c.Path })[0];
                Check(value.Text == shown, $"{label}: the fields view gives \"{value.Text}\", Mutagen read directly \"{shown}\"");

                // A plugin with no strings files: the same text in every language, and a note in every language but English
                var notes = e.TextNotes(rec, new[] { (JsonNode?)node });
                if (c.Localized) Check(notes.Count == 0, $"{label}: a note for a localized text: {string.Join(" ", notes)}");
                else
                {
                    Check(expected == english[c], $"{label}: the stored text reads \"{expected}\", in English \"{english[c]}\"");
                    Check(language == Language.English ? notes.Count == 0 : notes.Count == 1 && notes[0].Contains("isn't localized", StringComparison.Ordinal) && notes[0].Contains($"no {language} text", StringComparison.Ordinal),
                        $"{label}: the notes are [{string.Join(" | ", notes)}]");
                    if (language == Language.Japanese && c.Plugin.StartsWith("df_", StringComparison.Ordinal))
                    {
                        Check(notes[0].Contains("plugin Starfield.esm", StringComparison.Ordinal), $"{label}: the note doesn't name Starfield.esm's version: {notes[0]}");
                        samples.Add(notes[0]);
                    }
                }
                if (language == Language.Japanese && c.Localized) samples.Add($"{c.Why}: {shown}");
                checks += 3;
            }
        }
        return $"{Cases.Length} texts ({string.Join("; ", Cases.Select(c => c.Why))}) in {languages.Count} languages ({string.Join(", ", languages)}): {checks:N0} checks of the JSON, the card and the fields view against Mutagen read directly, all equal. In Japanese: {string.Join(" | ", samples)}";
    }

    static JsonNode? At(JsonObject json, string path)
    {
        JsonNode? n = json;
        foreach (var part in path.Split('.'))
        {
            int open = part.IndexOf('[');
            n = n?[open < 0 ? part : part[..open]];
            if (open >= 0) n = n?[int.Parse(part[(open + 1)..^1], CultureInfo.InvariantCulture)];
        }
        return n;
    }

    /// <summary>
    /// The text as Mutagen gives it, read directly in that language. A top-level record: through an overlay of its
    /// own plugin opened in that language. A record inside another: its string's number from the file's bytes,
    /// looked up in the plugin's strings files. A plugin with no strings files: its stored text as Mutagen reads it
    /// in English (the one text it has; Mutagen reads it the same in every language since 0.55.0-fixes.12, C150).
    /// </summary>
    static string? Direct(Ctx ctx, RecordEngine e, LoadedRecord rec, Case c, Language language)
    {
        var session = e.Sessions.Get(c.Plugin);
        if (c.Path != "Name")
        {
            var raw = e.Raw(rec.Ref);
            var nam1 = raw.Subrecords.First(s => s.Signature == "NAM1");
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(raw.Content(nam1));
            var lookup = StringsFolderLookupOverlay.TypicalFactory(GameRelease.Starfield, session.ModKey, Path.GetDirectoryName(session.Path)!,
                new StringsReadParameters { TargetLanguage = language, BsaFolderOverride = ctx.Data });
            return lookup.TryLookup(StringsSource.IL, language, id, out var s) ? s : null;
        }
        var read = new BinaryReadParameters
        {
            MasterFlagsLookup = new LoadOrder<IModMasterStyledGetter>(session.MasterStyles),
            ThrowOnUnknownSubrecord = false,
            StringsParam = new StringsReadParameters { TargetLanguage = c.Localized ? language : Language.English, BsaFolderOverride = ctx.Data },
        };
        using var mod = StarfieldMod.CreateFromBinaryOverlay(new ModPath(session.ModKey, session.Path), StarfieldRelease.Starfield, read);
        var group = mod.TryGetTopLevelGroup(((Loqui.ILoquiObject)rec.Record).Registration.GetterType) ?? throw new TestFailed($"{c.Plugin} has no group of {rec.Type}");
        var direct = group.RecordCache.TryGetValue(rec.Record.FormKey) ?? throw new TestFailed($"{c.Plugin}'s overlay has no {rec.Record.FormKey}");
        var name = ((ITranslatedNamedGetter)direct).Name;
        return name == null ? null : c.Localized ? (name.TryLookup(language, out var t) ? t : null) : name.String;
    }

    /// <summary>
    /// What it costs to load every language against one. Every view writes the record's JSON, which holds each text in
    /// every language (the strings files of all nine are read on first use); a reader that wanted only the language
    /// asked would read one. Measured: one record cold and warm, the strings files themselves, 1,000 cards and
    /// 7,131 NPCs to JSON, in an English and a Japanese engine.
    /// </summary>
    public static string Cost(Ctx ctx)
    {
        const string Sf = "Starfield.esm";
        double T(Action a) { var c = System.Diagnostics.Stopwatch.StartNew(); a(); return c.Elapsed.TotalMilliseconds; }
        var lines = new List<string>();
        var index = ctx.DataIndex;

        // One record, cold: a new engine each time (Starfield.esm opened, its weapon group read). An English card
        // first, so that neither pays the process's start-up (code compiled, the archive list read).
        using (var first = new RecordEngine(index, ctx.Schema, Path.Combine(ctx.Work, "verify-cost"), ctx.Data))
        {
            double startUp = T(() => { first.Sessions.Use(Sf); first.Card(first.Load(Sf, "0476C4:Starfield.esm")).ToText(); });
            lines.Add($"start-up: Eon in English as a card in a new process, {startUp:F0} ms");
        }
        foreach (var everyLanguage in new[] { false, true })
        {
            using var e = new RecordEngine(index, ctx.Schema, Path.Combine(ctx.Work, "verify-cost"), ctx.Data, Language.Japanese);
            string text = "";
            double cold = T(() =>
            {
                e.Sessions.Use(Sf);
                var rec = e.Load(Sf, "0476C4:Starfield.esm");
                text = everyLanguage ? e.Card(rec).ToText() : ((ITranslatedNamedGetter)rec.Record).Name!.String ?? "";
            });
            double warm = Enumerable.Range(0, 20).Select(_ => T(() =>
            {
                var rec = e.Load(Sf, "0476C4:Starfield.esm");
                text = everyLanguage ? e.Card(rec).ToText() : ((ITranslatedNamedGetter)rec.Record).Name!.String ?? "";
            })).Order().ElementAt(10);
            lines.Add(everyLanguage
                ? $"Eon in Japanese as a card (its JSON holds all nine languages): {cold:F0} ms cold (a new engine), {warm:F2} ms warm (median of 20)"
                : $"Eon's Japanese name alone (only the Japanese strings file read): {cold:F0} ms cold (a new engine), {warm:F2} ms warm (median of 20)");
        }

        // The strings files of Starfield.esm: one language against nine, each in a new lookup
        var session = new RecordEngine(index, ctx.Schema, Path.Combine(ctx.Work, "verify-cost"), ctx.Data);
        using (session)
        {
            var s = session.Sessions.Get(Sf);
            double Load(IReadOnlyList<Language> langs)
            {
                var lookup = StringsFolderLookupOverlay.TypicalFactory(GameRelease.Starfield, s.ModKey, Path.GetDirectoryName(s.Path)!, new StringsReadParameters { BsaFolderOverride = ctx.Data });
                return T(() => { foreach (var l in langs) foreach (var src in new[] { StringsSource.Normal, StringsSource.DL, StringsSource.IL }) lookup.TryLookup(src, l, 0x4819, out _); });
            }
            var all = SFSearch.Query.TextLanguages.Shipped.Select(x => Enum.Parse<Language>(x.Name)).ToList();
            Load(new[] { Language.English });
            double one = Load(new[] { Language.Japanese }), nine = Load(all);
            lines.Add($"Starfield.esm's strings files (.strings, .dlstrings, .ilstrings) from its archive: {one:F0} ms for Japanese alone, {nine:F0} ms for all nine languages");
        }

        // 1,000 cards and 7,131 NPCs to JSON, as the timing test, in English and in Japanese
        var byType = Tests.ByType(index, Sf);
        var thousand = new[] { "WEAP", "ARMO", "NPC_", "COBJ", "OMOD", "MISC", "ALCH", "AMMO", "BOOK", "LVLI" }.SelectMany(x => Tests.Sample(byType[x], 100)).ToList();
        var npcs = byType["NPC_"];
        foreach (var language in new[] { Language.English, Language.Japanese })
        {
            using var e = new RecordEngine(index, ctx.Schema, Path.Combine(ctx.Work, "verify-cost"), ctx.Data, language);
            e.Sessions.Use(Sf);
            double cold = T(() => { foreach (var r in thousand) e.Card(e.Load(r)).ToText(); });
            double warm = T(() => { foreach (var r in thousand) e.Card(e.Load(r)).ToText(); });
            long bytes = 0;
            double npc = T(() => { foreach (var r in npcs) { var rec = e.Load(r); bytes += RecordSerializer.ToUtf8(rec.Record).Length; } });
            lines.Add($"{language}: 1,000 cards of Starfield.esm {cold / 1000:F2} s the first time, {warm / 1000:F2} s after; {npcs.Count:N0} NPCs to JSON {npc / 1000:F2} s ({bytes / 1048576.0:F1} MB)");
        }
        ctx.WriteReport("languages-cost.txt", lines);
        return "\n      " + string.Join("\n      ", lines);
    }
}
