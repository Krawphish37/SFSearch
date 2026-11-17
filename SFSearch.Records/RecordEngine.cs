// The records layer in one place: find a record through the header index, read it through Mutagen, give it as JSON,
// as a card, as raw subrecords, as evidence for a field, and verify it against the file's bytes.
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Strings;
using SFSearch.Index;

namespace SFSearch.Records;

/// <summary>How a record was read.</summary>
public enum ReadRoute
{
    /// <summary>From the overlay, through the record type's own top-level group.</summary>
    Group,
    /// <summary>Straight from the record's own bytes at the header index's position (records inside others, and the
    /// records that hold them).</summary>
    Direct,
}

/// <summary>One record of one plugin, read through Mutagen.</summary>
public sealed class LoadedRecord
{
    public required RecordRef Ref { get; init; }
    public required RecordInfo Info { get; init; }
    /// <summary>The Mutagen class name: Weapon, PlacedObject, WeaponModification.</summary>
    public required string Type { get; init; }
    public required IMajorRecordGetter Record { get; init; }
    public required ReadRoute Route { get; init; }
    public required PluginSession Session { get; init; }
    /// <summary>Records nested in this one, when it was read with its children as stubs.</summary>
    public int Children { get; init; }
    public string Label => Info.Label;
}

public sealed partial class RecordEngine : IDisposable
{
    public HeaderIndex Index { get; }
    public RecordSchema Schema { get; }
    public PluginSessions Sessions { get; }
    /// <summary>Where the one-record verify writes its test plugins. Never a folder that holds plugins.</summary>
    public string ScratchFolder { get; }
    /// <summary>
    /// A record with more data than this (uncompressed) is heavy: read through Mutagen, as JSON and as a card it takes
    /// many times its size in memory. It is not read unless <see cref="AllowHeavy"/> is set; the cost is said first.
    /// Starfield.esm has one such record: its navigation mesh info map (NAVI), 53.9 MB.
    /// </summary>
    public long HeavyRecordBytes { get; set; } = 16 << 20;
    public bool AllowHeavy { get; set; }
    /// <summary>
    /// Read every record of a signature with one Mutagen type straight from its own bytes, not only the records nested
    /// in others. Off by default: a top-level record is read through its own top-level group. Measured in M3: the
    /// same JSON, and a first read in a new process that is many times faster.
    /// </summary>
    public bool PreferDirect { get; set; }

    /// <summary>The size of a record's data without its header, inflated when the record is compressed.</summary>
    public long DataBytes(RecordRef r)
    {
        var p = Index.Plugins[r.Plugin]; var e = p.Segment![r.Entry];
        if (!e.IsCompressed || e.Size < 4) return e.Size;
        // The inflated size is the first four bytes of a compressed record's data. Read from the record's start, so
        // that the read of the record itself, which comes next, finds its bytes in the thread's window.
        Span<byte> b = stackalloc byte[RawRecord.HeaderLength + 4];
        Sessions.Get(p.Name).ReadAt(e.Offset, b);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b[RawRecord.HeaderLength..]);
    }

    /// <summary>Null when a record may be read; otherwise the line that says what it would cost.</summary>
    public string? HeavyNote(RecordRef r)
    {
        long bytes = DataBytes(r);
        if (bytes <= HeavyRecordBytes || AllowHeavy) return null;
        var info = Index.Describe(r);
        return $"{info.Label} is a {info.Signature} of {Sizes.Text(bytes)}. Read through Mutagen, as JSON and as a card it takes many times that in memory (gigabytes), so it isn't read unless asked for. Its raw subrecords can be shown at once. To read it anyway, ask for that record by itself with heavy records allowed (record with allow_heavy; sfsrecord --heavy), with enough free memory.";
    }

    public RecordEngine(HeaderIndex index, RecordSchema schema, string scratchFolder, string? dataFolder = null, Language language = Language.English)
    {
        Index = index; Schema = schema; ScratchFolder = scratchFolder;
        Sessions = new PluginSessions(index, dataFolder, language);
    }

    public void Dispose() => Sessions.Dispose();

    // ------------------------------------------------------------------ Finding a record
    /// <summary>
    /// A record of a plugin, named by FormKey (0003C2:darkstar.esm), by local ID (0003C2), by the FormID the file
    /// writes (8 digits) or by EditorID. A name that fits several records lists them all and picks none.
    /// </summary>
    public RecordRef Find(string plugin, string text)
    {
        var p = Index.Plugin(plugin) ?? throw new RecordException($"There is no plugin named {plugin} in the header index.");
        var seg = p.Segment ?? throw new RecordException($"{p.Name} has no header index: {p.Problems.ReadError ?? "it couldn't be read"}.");
        text = text.Trim();
        var hits = new List<int>();
        if (HeaderIndex.TryParseFormKey(text, out var owner, out var localId))
        {
            int o = seg.OwnerOf(owner);
            if (o < 0) throw new RecordException($"{p.Name} has no record of {owner}: {owner} isn't {p.Name} or one of its masters ({string.Join(", ", seg.Names)}).");
            Copies(seg, seg.Find(o, localId), hits);
        }
        else if (text.Length is > 0 and <= 8 && text.All(Uri.IsHexDigit))
        {
            uint id = Convert.ToUInt32(text, 16);
            if (text.Length > 6)
            {
                if (!p.FormIds.TryResolve(id, out string op, out uint local)) throw new RecordException($"The FormID {id:X8} can't be resolved in {p.Name}: a master is missing.");
                int o = seg.OwnerOf(op);
                Copies(seg, o < 0 ? -1 : seg.Find(o, local), hits);
            }
            else
                for (int o = 0; o <= seg.SelfOwner; o++) Copies(seg, seg.Find(o, id), hits);
            // Hex digits can also spell an EditorID (a record named "BEEF")
            if (hits.Count == 0) seg.FindEditorId(text, hits);
        }
        else seg.FindEditorId(text, hits);

        if (hits.Count == 0) throw new RecordException($"{p.Name} has no record \"{text}\". Give a FormKey (0003C2:darkstar.esm), a local ID or an EditorID.");
        if (hits.Count > 1)
            throw new RecordException($"\"{text}\" fits {hits.Count} records of {p.Name}; name one by its FormKey: " +
                string.Join("; ", hits.Take(8).Select(h => { var d = Index.Describe(new RecordRef(p.Id, h)); return $"{d.Label} {d.Signature}"; })) + (hits.Count > 8 ? "; …" : "") + ".");
        return new RecordRef(p.Id, hits[0]);
    }

    /// <summary>A plugin can hold a FormKey more than once (MissionsGalore.esm does). Every copy is a hit, so none is picked silently.</summary>
    static void Copies(Segment seg, int at, List<int> hits)
    {
        if (at < 0) return;
        var first = seg[at];
        for (int i = at; i < seg.Count && seg[i].Owner == first.Owner && seg[i].LocalId == first.LocalId; i++) hits.Add(i);
    }

    /// <summary>A record's Mutagen type from the header index's signature and the schema. A signature with several
    /// types (OMOD, GMST) is settled by the record itself once it is read.</summary>
    public string TypeFor(string signature)
    {
        var types = Schema.TypesOf(signature);
        if (types.Count == 0) throw new RecordException($"The schema has no record type for the signature {signature}: Mutagen doesn't read it.");
        return types[0];
    }

    /// <summary>True when records of this signature are read straight from their own bytes: they sit inside another
    /// record, or they hold records themselves.</summary>
    public bool IsDirect(RecordInfo info)
    {
        var types = Schema.TypesOf(info.Signature);
        return info.Parent != null || (types.Count == 1 && (Schema.HoldsRecords(types[0]) || types[0] == "Cell"));
    }

    // ------------------------------------------------------------------ Reading a record
    /// <summary>
    /// Reads one record through Mutagen. The plugin's session must be current: call <see cref="PluginSessions.Use"/> first.
    /// Several threads can read records of open plugins at once (the plugins are opened before, on one thread).
    /// </summary>
    public LoadedRecord Load(RecordRef r) => Load(r, Index.Describe(r));

    /// <summary>
    /// The same, and a record that holds others (a cell, a worldspace, a quest) is read alone: its children group isn't
    /// laid out as stubs. Its own fields and links are the same; only <see cref="LoadedRecord.Children"/> is 0 and the
    /// fields Mutagen fills from the children group's header (a cell's group timestamps) are left at their defaults.
    /// For the link confirmation of a byte search (refs), where a big cell's thousands of stubs cost more than the cell.
    /// A top-level record of a type with one Mutagen class is read from its own bytes too (as <see cref="PreferDirect"/>
    /// does), so its plugin's overlay isn't opened for it: Starfield.esm's takes about 1.5 s to open.
    /// </summary>
    public LoadedRecord LoadAlone(RecordRef r) => Load(r, Index.Describe(r), childStubs: false, direct: true);

    /// <summary>
    /// Opens, on this thread, everything that reading these records on many threads needs: each plugin's session and the
    /// top-level groups their records are read through. <see cref="PluginSessions"/> is for one caller at a time; after
    /// this, <see cref="Load(RecordRef)"/> of these records can run on many threads at once.
    /// </summary>
    /// <param name="alone">The records will be read with <see cref="LoadAlone"/>: only the groups of types with several
    /// Mutagen classes (OMOD, GMST) are needed.</param>
    public void OpenForThreads(IEnumerable<RecordRef> records, bool alone = false)
    {
        var done = new HashSet<(int Plugin, int Signature)>();
        foreach (var r in records)
        {
            var p = Index.Plugins[r.Plugin];
            var e = p.Segment![r.Entry];
            if (!done.Add((r.Plugin, e.Signature))) continue;
            var session = Sessions.Get(p.Name);
            var info = Index.Describe(r);
            if (Schema.TypesOf(info.Signature).Count == 0 || IsDirect(info) || (alone || PreferDirect) && Schema.TypesOf(info.Signature).Count == 1) continue;
            // The group's record cache is made at its first use: made here, not by several threads at once
            if (session.TopLevelGroup(TypeFor(info.Signature)) is { } group) _ = group.RecordCache.Count;
        }
    }

    /// <summary>The same, for a caller that already has the header index's line for the record.</summary>
    public LoadedRecord Load(RecordRef r, RecordInfo info) => Load(r, info, childStubs: true, direct: false);

    LoadedRecord Load(RecordRef r, RecordInfo info, bool childStubs, bool direct)
    {
        if (info.Unresolved) throw new RecordException($"The record at 0x{info.Offset:X} of {info.Plugin} has a FormID that can't be resolved: a master of {info.Plugin} is missing.");
        var session = Sessions.Get(info.Plugin);
        if (HeavyNote(r) is { } heavy) throw new RecordException(heavy);
        string type = TypeFor(info.Signature);
        if (!IsDirect(info) && !((PreferDirect || direct) && Schema.TypesOf(info.Signature).Count == 1))
        {
            var group = session.TopLevelGroup(type);
            if (group != null)
            {
                if (!HeaderIndex.TryParseFormKey(info.FormKey, out var owner, out var id)) throw new RecordException($"The header index gives no FormKey for the record at 0x{info.Offset:X} of {info.Plugin}.");
                var key = new FormKey(ModKey.FromFileName(owner), id);
                bool twice = Index.Plugins[r.Plugin].Problems.Duplicates > 0;
                if (group.RecordCache.TryGetValue(key) is { } rec && (!twice || Same(rec, info)))
                    return new LoadedRecord { Ref = r, Info = info, Type = RecordSerializer.TypeName(rec), Record = rec, Route = ReadRoute.Group, Session = session };
            }
            // Not in its group: a FormKey the plugin holds twice (Mutagen keeps the later record), or a type with
            // no top-level group. A type with one Mutagen class can still be read from its own bytes.
            if (Schema.TypesOf(info.Signature).Count != 1)
                throw new RecordException($"{info.Label} is a {info.Signature} at 0x{info.Offset:X} of {info.Plugin}, but Mutagen's {type} group doesn't give it (the plugin may hold the FormKey twice). Its raw subrecords can still be shown.");
        }
        bool holder = Schema.HoldsRecords(type);
        var obj = (IMajorRecordGetter)session.ReadDirect(type, info.Offset, holder && childStubs, out int stubs);
        return new LoadedRecord { Ref = r, Info = info, Type = RecordSerializer.TypeName(obj), Record = obj, Route = ReadRoute.Direct, Session = session, Children = stubs };
    }

    /// <summary>A group gives the last record of a FormKey the plugin holds twice. The record asked for is the one
    /// at the header index's position, so the two must be the same record.</summary>
    static bool Same(IMajorRecordGetter rec, RecordInfo info)
    {
        return string.Equals(rec.EditorID, info.EditorId, StringComparison.Ordinal) || info.EditorId == null && string.IsNullOrEmpty(rec.EditorID);
    }

    public LoadedRecord Load(string plugin, string text) => Load(Find(plugin, text));

    // ------------------------------------------------------------------ The one record form
    /// <summary>
    /// The record as Mutagen.Bethesda.Serialization's full-profile JSON. Records nested in it are not part of it:
    /// each is a <c>{"$Child": "FormKey"}</c> marker, and a record of its own.
    /// </summary>
    public JsonObject Json(LoadedRecord rec) => Json(rec.Record, rec.Type);

    /// <summary>The same for a record of this Mutagen type that no plugin holds as it is (a partial form merged over the
    /// version before it).</summary>
    public JsonObject Json(IMajorRecordGetter record, string type)
    {
        var json = RecordSerializer.ToJsonObject(record);
        // Only a record that holds others has anything to split out
        if (Schema.HoldsRecords(type)) RecordSplit.Split(json, null);
        return json;
    }

    /// <summary>The record with every record nested in it put back, each read from its own bytes: the JSON Mutagen's
    /// serializer writes for the whole nested record.</summary>
    public JsonObject Assembled(LoadedRecord rec)
    {
        var json = Json(rec);
        RecordSplit.Assemble(json, rec.Type, Schema, key =>
        {
            var v = Index.Versions(key) ?? throw new RecordException($"The child {key} of {rec.Label} isn't in the header index.");
            var mine = v.Holders.Where(h => h.Plugin == rec.Ref.Plugin).ToList();
            if (mine.Count == 0) throw new RecordException($"The child {key} of {rec.Label} isn't in {rec.Info.Plugin}.");
            var child = Load(mine[0]);
            return (Json(child), child.Type);
        });
        return json;
    }

    // ------------------------------------------------------------------ Names
    /// <summary>EditorID (FormID:plugin) for a FormKey, from the header index: the version the game uses, or the
    /// owner's when no loaded plugin holds it.</summary>
    public string Label(string formKey)
    {
        if (formKey == "Null") return "Null";
        if (!HeaderIndex.TryParseFormKey(formKey, out var plugin, out var id)) return formKey;
        var p = Index.Plugin(plugin);
        string shown = $"{id:X6}:{p?.Name ?? plugin}";
        if (p == null) return $"(plugin not in Data) ({shown})";
        var v = Index.Versions(p.Name, id);
        if (v == null)
            return id < 0x800 && p.Name.Equals("Starfield.esm", StringComparison.OrdinalIgnoreCase)
                ? $"{(id == 0x14 ? "PlayerRef " : id == 0x7 ? "Player " : "")}[built into the game] ({shown})"
                : $"(no such record) ({shown})";
        var pick = v.Winner >= 0 ? v.Holders[v.Winner] : v.Holders[0];
        var seg = Index.Plugins[pick.Plugin].Segment!;
        return $"{seg.EditorId(pick.Entry) ?? "(no EditorID)"} ({shown})";
    }

    /// <summary>The same for a FormID as a plugin's file writes it.</summary>
    public string Label(PluginSession session, uint rawFormId) => rawFormId == 0 ? "Null" : Label(session.FormIds.FormKey(rawFormId));

    // ------------------------------------------------------------------ Raw
    public RawRecord Raw(RecordRef r)
    {
        var info = Index.Describe(r);
        var session = Sessions.Get(info.Plugin);
        var raw = RawRecord.Read(session, info.Offset);
        if (raw.Signature != info.Signature || raw.StoredSize != info.Size)
            throw new RecordException($"{info.Plugin} doesn't hold a {info.Signature} of {info.Size:N0} bytes at 0x{info.Offset:X} any more. The plugin changed: run the command again.");
        return raw;
    }

    // ------------------------------------------------------------------ Card, fields, evidence, verify
    /// <summary>The card of a record: set fields only, readable names, links labelled, unknown fields with their bytes.</summary>
    /// <param name="withDefaults">Also show the fields a reader looks for on this type when the record leaves them at
    /// their default (<see cref="CardDefaults"/>). The card view asks for them; the fields view and the checks don't.</param>
    public Card Card(LoadedRecord rec, JsonObject? json = null, bool withDefaults = false)
    {
        json ??= Json(rec);
        var info = rec.Info;
        var builder = new CardBuilder(Schema, Label, info.Signature, Sessions.Language.ToString(), () => Raw(rec.Ref),
            key => key != "Null" && HeaderIndex.TryParseFormKey(key, out var plugin, out var id) ? rec.Session.FormIds.Raw(plugin, id) : key == "Null" ? 0u : null);
        var card = new Card
        {
            Label = info.Label, FormKey = info.FormKey, Signature = info.Signature, Type = rec.Type, Plugin = info.Plugin,
            IsOverride = info.IsOverride, Parent = info.Parent == null ? null : Label(info.Parent), Offset = info.Offset, Size = info.Size,
            Compressed = (info.Flags & RawRecord.CompressedFlag) != 0, Language = Sessions.Language.ToString(),
            Fields = builder.Build(rec.Type, json),
            FromRecord = path => PropertyAt(rec.Record, path),
        };
        var problems = Index.Plugins[rec.Ref.Plugin].Problems;
        if (problems.TypeMismatches.Contains(rec.Ref.Entry))
            card.Notes.Add($"This {info.Signature} overrides a record of another type in its master: {info.Plugin} was probably built for an older version of that master.");
        if (problems.OverridesOfNothing.Contains(rec.Ref.Entry))
            card.Notes.Add($"This record's FormKey belongs to a master that has no such record.");
        if (withDefaults) CardDefaults.Add(Schema, rec.Type, card);
        return card;
    }

    /// <summary>
    /// What an answer must say about the translated texts among <paramref name="values"/> (the record's JSON, or the
    /// fields asked for) in the session's language. A plugin with no strings files keeps one text in itself: the game
    /// shows it in every language, so it is given as it is, and the note says so and names the plugins that hold the
    /// record with strings files. A text of a localized plugin with no text in the language asked is named too.
    /// </summary>
    public List<string> TextNotes(LoadedRecord rec, IEnumerable<JsonNode?> values)
    {
        string language = Sessions.Language.ToString();
        var census = new TextCensus();
        foreach (var v in values) census.Scan(v, language);
        var notes = new List<string>();
        string plugin = rec.Info.Plugin;
        if (census.OneLanguage > 0)
        {
            bool one = census.OneLanguage == 1;
            string stored = string.Join(" and ", census.Stored);
            string note = $"{plugin} isn't localized (it has no strings files): {(one ? "the text here is" : $"the {census.OneLanguage:N0} texts here are")} stored in the plugin in {stored} only, so {(one ? "it has" : "they have")} no {language} text of {(one ? "its" : "their")} own. The game shows {(one ? "that text" : "those texts")} in every language, and {(one ? "it is" : "they are")} given as stored.";
            var localized = LocalizedHolders(rec);
            if (localized.Count > 0)
                note += localized.Count == 1
                    ? $" {localized[0]}, which also holds this record, is localized: ask for its version (plugin {localized[0]}) for its {language} text."
                    : $" {string.Join(", ", localized)}, which also hold this record, are localized: ask for one of their versions (plugin {localized[^1]}, the last loaded) for its {language} text.";
            notes.Add(note);
        }
        if (census.Missing > 0)
            notes.Add($"{(census.Missing == 1 ? "1 text" : $"{census.Missing:N0} texts")} here {(census.Missing == 1 ? "has" : "have")} no {language} text in {plugin}'s strings files: {(census.Missing == 1 ? "it holds" : "they hold")} {string.Join(", ", census.Held)}.");
        return notes;
    }

    /// <summary>The other plugins that hold the record and are localized, in load order.</summary>
    List<string> LocalizedHolders(LoadedRecord rec)
    {
        var v = Index.Versions(rec.Info.FormKey);
        if (v == null) return new List<string>();
        return v.Holders.Where(h => h.Plugin != rec.Ref.Plugin)
            .Select(h => Index.Plugins[h.Plugin])
            .Where(p => ((p.Header?.Flags ?? 0) & 0x80) != 0)
            .Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The value of a property of a Mutagen object by its path (<c>Weather.DataType</c>, <c>Stages[0].Flags</c>),
    /// through the getters' public properties. Null when a step is missing or null.</summary>
    static object? PropertyAt(object? obj, string path)
    {
        foreach (var part in path.Split('.'))
        {
            if (obj == null) return null;
            string name = part; int? index = null;
            int open = part.IndexOf('[');
            if (open >= 0)
            {
                name = part[..open];
                if (!int.TryParse(part[(open + 1)..].TrimEnd(']'), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int i)) return null;
                index = i;
            }
            var t = obj.GetType();
            var prop = t.GetProperty(name) ?? t.GetInterfaces().Select(x => x.GetProperty(name)).FirstOrDefault(x => x != null);
            if (prop == null || prop.GetIndexParameters().Length > 0) return null;
            obj = prop.GetValue(obj);
            if (index is int n)
            {
                if (obj is not System.Collections.IEnumerable items) return null;
                obj = items.Cast<object?>().Skip(n).FirstOrDefault();
            }
        }
        return obj;
    }

    /// <summary>"Fields" mode: the named fields. A field the JSON leaves out gives its default where the schema says the field is always there.</summary>
    public List<FieldValue> Fields(LoadedRecord rec, IEnumerable<string> names) => FieldLookup.Fields(Schema, Card(rec), names);

    /// <summary>Evidence for one field: the raw subrecord bytes, the field's slice, the Mutagen field, the xEdit path.</summary>
    public Evidence Evidence(LoadedRecord rec, string field) =>
        FieldLookup.Evidence(Schema, Card(rec), Raw(rec.Ref), field, id => Label(rec.Session, id),
            formIdOf: key => key == "Null" ? 0u : HeaderIndex.TryParseFormKey(key, out var plugin, out var id) ? rec.Session.FormIds.Raw(plugin, id) : null,
            textOf: rec.Session.Localized ? rec.Session.StringsFor : null, positionsOf: rec.Session.Positions);

    /// <summary>The one-record verify, for records of one plugin.</summary>
    public List<VerifyResult> Verify(IReadOnlyList<RecordRef> records) => new Verifier(this).Verify(records);

    /// <summary>The record as the file holds it: its header, then every subrecord with its bytes.</summary>
    public string RawText(RecordRef r, int maxBytes = 64)
    {
        var info = Index.Describe(r);
        var raw = Raw(r);
        var session = Sessions.Get(info.Plugin);
        var sb = new System.Text.StringBuilder();
        sb.Append(info.Label).Append('\n');
        sb.Append($"{raw.Signature} in {info.Plugin} at 0x{info.Offset:X}: a 24-byte header and {raw.StoredSize:N0} bytes");
        if (raw.Compressed) sb.Append($", compressed ({raw.Data.Length:N0} bytes inflated; the subrecords below are shown inflated)");
        sb.Append('\n');
        sb.Append($"header  {RawRecord.Hex(raw.Header)}\n");
        sb.Append($"        flags {raw.Flags:X8}, FormID {raw.FormId:X8} → {Label(session, raw.FormId)}, version control {raw.VersionControl:X8}, form version {raw.FormVersion}, second version number {raw.Version2}\n");
        sb.Append($"        FormID as stored: {StoredFormText(info.Plugin, session, raw.FormId)}\n");
        foreach (var s in raw.Subrecords)
            sb.Append($"{s.Signature}  {s.Length,6:N0}  {RawRecord.Hex(raw.Content(s), maxBytes)}{(s.LongForm ? "   [long form: its length comes from an XXXX subrecord before it]" : "")}\n");
        if (raw.Trailing != 0) sb.Append($"[notes] {raw.Trailing} bytes after the last whole subrecord.\n");
        sb.Append($"{raw.Subrecords.Count:N0} subrecords, {raw.Data.Length:N0} bytes\n");
        return sb.ToString();
    }

    /// <summary>What a FormID stored in a plugin means: its form (the full index, FE, FD) and whose record it names
    /// ("FE001863: the Small (light) form, Small number 001, own record 0x863"). GAPS S39.</summary>
    public string StoredFormText(string plugin, PluginSession session, uint rawFormId) =>
        FormIdForms.Describe(rawFormId, session.FormIds, Index.Plugin(plugin)?.Header?.Style ?? SFSearch.Index.MasterStyle.Full);

    /// <summary>The same as a structured object.</summary>
    public JsonObject RawJson(RecordRef r)
    {
        var info = Index.Describe(r);
        var raw = Raw(r);
        var session = Sessions.Get(info.Plugin);
        return new JsonObject
        {
            ["record"] = info.Label, ["signature"] = raw.Signature, ["plugin"] = info.Plugin, ["offset"] = info.Offset, ["storedSize"] = raw.StoredSize,
            ["compressed"] = raw.Compressed, ["size"] = raw.Data.Length, ["header"] = RawRecord.Hex(raw.Header), ["flags"] = raw.Flags.ToString("X8"),
            ["formId"] = raw.FormId.ToString("X8"), ["formIdStored"] = StoredFormText(info.Plugin, session, raw.FormId), ["formKey"] = Label(session, raw.FormId), ["versionControl"] = raw.VersionControl, ["formVersion"] = raw.FormVersion, ["version2"] = raw.Version2,
            ["subrecords"] = new JsonArray(raw.Subrecords.Select(s => (JsonNode)new JsonObject { ["signature"] = s.Signature, ["length"] = s.Length, ["longForm"] = s.LongForm, ["bytes"] = RawRecord.Hex(raw.Content(s)) }).ToArray()),
        };
    }
}
