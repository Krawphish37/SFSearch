// The plugin header (TES4, GAPS S88): the first record of every plugin, the one with no FormID and no EditorID. It
// holds the plugin's flags (Master, Localized, Light, Medium, ...), HEDR (the header version, the record count and the
// next object ID), the author and description, the master list (a MAST, and a DATA when there is one, for each master)
// and the overridden forms. It is read here as every record is: the file's bytes, and Mutagen's own object read from
// those bytes, which the card, the fields and the evidence are made from.
//  - Mutagen's object is the plugin's header read alone (StarfieldModHeader.CreateFromPath, C58 W17, fixes.20: the TES4
//    bytes only, read by the full import's own header reader, with the session's master sizes): the header is read the
//    way Mutagen reads it when it opens the plugin, without opening the rest of the plugin (Starfield.esm's overlay takes
//    about 1.5 s to open; its header 35 KB).
//  - The bytes are read from the file again for each question and checked to be a TES4.
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Starfield;
using SFSearch.Index;

namespace SFSearch.Records;

/// <summary>A plugin's header, read through Mutagen.</summary>
public sealed class LoadedHeader
{
    public required IndexedPlugin Plugin { get; init; }
    public required PluginSession Session { get; init; }
    /// <summary>The TES4 record as the file holds it.</summary>
    public required RawRecord Raw { get; init; }
    /// <summary>Mutagen's object: <see cref="IStarfieldModHeaderGetter"/>.</summary>
    public required IStarfieldModHeaderGetter Header { get; init; }
    /// <summary>What answers call it: "darkstar.esm's plugin header (TES4)".</summary>
    public string Label => LabelOf(Plugin.Name);
    public static string LabelOf(string plugin) => $"{plugin}'s plugin header (TES4)";
    /// <summary>The Mutagen class: StarfieldModHeader.</summary>
    public const string Type = "StarfieldModHeader";
}

public sealed partial class RecordEngine
{
    /// <summary>
    /// The header of a plugin of the index. The plugin's session must be current: call <see cref="PluginSessions.Use"/>
    /// first. A plugin whose first record isn't a TES4 isn't a plugin, and the header index has no such plugin.
    /// </summary>
    public LoadedHeader LoadHeader(string plugin)
    {
        var p = Index.Plugin(plugin) ?? throw new RecordException($"There is no plugin named {plugin} in the header index.");
        var session = Sessions.Get(p.Name);
        var raw = RawRecord.Read(session, 0);
        if (raw.Signature != "TES4")
            throw new RecordException($"{p.Name} doesn't start with a TES4 record any more (it starts with {raw.Signature}). The plugin changed: run the command again.");
        var read = new BinaryReadParameters { MasterFlagsLookup = session.MasterFlags, ThrowOnUnknownSubrecord = false, Parallel = false, FileShare = PluginSession.Sharing };
        IStarfieldModHeaderGetter header;
        try { header = StarfieldModHeader.CreateFromPath(new ModPath(session.ModKey, session.Path), StarfieldRelease.Starfield, read); }
        catch (Exception e) when (e is not RecordException and not OutOfMemoryException) { throw new RecordException($"Mutagen couldn't read {p.Name}'s header: {e.GetType().Name}: {e.Message.Split('\n')[0]}"); }
        return new LoadedHeader { Plugin = p, Session = session, Raw = raw, Header = header };
    }

    /// <summary>The header as Mutagen.Bethesda.Serialization's full-profile JSON, as for any record.</summary>
    public JsonObject HeaderJson(LoadedHeader h) => RecordSerializer.ToJsonObject(h.Header);

    /// <summary>The header's card: the fields that are set, readable names, links labelled.</summary>
    public Card HeaderCard(LoadedHeader h, JsonObject? json = null, bool withDefaults = false)
    {
        json ??= HeaderJson(h);
        var session = h.Session;
        var builder = new CardBuilder(Schema, Label, "TES4", Sessions.Language.ToString(), () => h.Raw,
            key => key != "Null" && HeaderIndex.TryParseFormKey(key, out var plugin, out var id) ? session.FormIds.Raw(plugin, id) : key == "Null" ? 0u : null);
        var card = new Card
        {
            Label = h.Label, FormKey = "", Signature = "TES4", Type = LoadedHeader.Type, Plugin = h.Plugin.Name,
            Offset = 0, Size = h.Raw.StoredSize, Language = Sessions.Language.ToString(),
            Fields = builder.Build(LoadedHeader.Type, json),
            FromRecord = path => PropertyAt(h.Header, path),
        };
        if (withDefaults) CardDefaults.Add(Schema, LoadedHeader.Type, card);
        return card;
    }

    /// <summary>"Fields" mode for the header.</summary>
    public List<FieldValue> HeaderFields(LoadedHeader h, IEnumerable<string> names) => FieldLookup.Fields(Schema, HeaderCard(h), names);

    /// <summary>Evidence for one field of the header: its bytes in the file (the record header, a subrecord, or the
    /// MAST and DATA of one master), beside the Mutagen field and the xEdit path, checked against Mutagen's value.</summary>
    public Evidence HeaderEvidence(LoadedHeader h, string field) =>
        FieldLookup.Evidence(Schema, HeaderCard(h), h.Raw, field, id => Label(h.Session, id),
            formIdOf: key => key == "Null" ? 0u : HeaderIndex.TryParseFormKey(key, out var plugin, out var id) ? h.Session.FormIds.Raw(plugin, id) : null,
            positionsOf: _ => h.Session.HeaderPositions());

    /// <summary>The header as the file holds it: its 24-byte header, then every subrecord with its bytes.</summary>
    public string HeaderRawText(LoadedHeader h, int maxBytes = 64)
    {
        var raw = h.Raw;
        var sb = new System.Text.StringBuilder();
        sb.Append(h.Label).Append('\n');
        sb.Append($"TES4 in {h.Plugin.Name} at 0x0: a 24-byte header and {raw.StoredSize:N0} bytes\n");
        sb.Append($"header  {RawRecord.Hex(raw.Header)}\n");
        sb.Append($"        flags {raw.Flags:X8}, FormID {raw.FormId:X8}, version {raw.VersionControl:X8}, form version {raw.FormVersion}, second version number {raw.Version2}\n");
        foreach (var s in raw.Subrecords)
            sb.Append($"{s.Signature}  {s.Length,6:N0}  {RawRecord.Hex(raw.Content(s), maxBytes)}{(s.LongForm ? "   [long form: its length comes from an XXXX subrecord before it]" : "")}\n");
        if (raw.Trailing != 0) sb.Append($"[notes] {raw.Trailing} bytes after the last whole subrecord.\n");
        sb.Append($"{raw.Subrecords.Count:N0} subrecords, {raw.Data.Length:N0} bytes\n");
        return sb.ToString();
    }
}
