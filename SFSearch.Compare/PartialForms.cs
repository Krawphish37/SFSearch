// Partial forms (GAPS S50). A record with the header flag 0x4000 (PartialForm) holds only some of its fields: the
// game lays the fields it holds over the version before it and keeps that version's values for the rest. Starfield
// writes them for cells, quests and topics (Mutagen's XML marks Cell, Quest and DialogTopic partialForm="true"), and
// the DLCs write them for worldspaces too. Mutagen reads such a record as it is written: the fields it leaves out are
// absent (or at their default), and Mutagen doesn't merge it with anything (0.55.0-fixes.17 has no merge; only
// IsPartialFormable on the type). So a comparison that took the partial form literally showed every field it leaves
// out as changed: SFBGS00D.esm's CityNewAtlantisLodgeInt (06BB29:Starfield.esm) holds only its EDID.
//
// The merged record, made here: the version before the partial form, with each of the record's own fields the partial
// form holds put in its place. A field is held when the partial form's JSON has it and, when the schema knows the
// field's subrecord, that subrecord is in the record's bytes (so a default Mutagen fills in for a field that isn't
// there doesn't count). The serializer leaves out a field at its default value: a partial form that sets a field to
// its default would read as not holding it, and keep the version before it's value (none seen in the official files). The header's own values (flags, form version, version control) and the
// records nested in it are the partial form's; its flags lose the PartialForm bit, since the merged record is whole.
using System.Text.Json.Nodes;
using SFSearch.Index;
using SFSearch.Records;
using SFSearch.SchemaGen;

namespace SFSearch.Compare;

public sealed partial class CompareSession
{
    public const long PartialFormFlag = 0x00004000;
    static readonly HashSet<string> HeaderKeys = new(StringComparer.Ordinal) { "FormKey", "MajorRecordFlagsRaw", "FormVersion", "Version2", "VersionControl", "IsCompressed", "IsDeleted", "StarfieldMajorRecordFlags", "MajorFlags" };

    /// <summary>True when the record is a partial form: header flag 0x4000.</summary>
    public static bool IsPartial(RecordView v) =>
        v.Json["MajorRecordFlagsRaw"] is JsonValue raw && long.TryParse(raw.ToJsonString(), out long bits) && (bits & PartialFormFlag) != 0;

    /// <summary>
    /// The record as the game has it after a partial form: the partial form's fields laid over the version under it.
    /// A record that isn't a partial form is given back as it is.
    /// </summary>
    public RecordView Merge(RecordView partial, RecordView under)
    {
        if (!IsPartial(partial) || partial.Type != under.Type) return partial;
        var held = new HashSet<string>(StringComparer.Ordinal);
        try { foreach (var s in partial.Engine.Raw(partial.Ref).Subrecords) held.Add(s.Signature); }
        catch (RecordException e) { throw new CompareException(e.Message); }
        var merged = (JsonObject)under.Json.DeepClone();
        var keys = under.Json.Select(k => k.Key).Union(partial.Json.Select(k => k.Key)).ToList();
        foreach (string key in keys)
        {
            bool has = partial.Json.TryGetPropertyValue(key, out var mine);
            bool take;
            if (HeaderKeys.Contains(key) || SerializerNotes.RecordKeys.Contains(key) || HoldsChildren(mine) || HoldsChildren(under.Json[key])) take = true;
            // Held: in the JSON, and (when the schema knows its subrecord) that subrecord is in the bytes: Mutagen gives a
            // field that isn't there its default. The JSON decides first, since a nested part can use the same
            // subrecord name (a worldspace's component holds a BNAM; the worldspace's own Biome is BNAM too).
            else take = has && (SubrecordOf(partial.Type, key) is not { } sig || held.Contains(sig));
            if (!take) continue;
            if (has) merged[key] = mine?.DeepClone(); else merged.Remove(key);
        }
        // The merged record is whole: the PartialForm bit goes, from the flags and from their views
        if (merged["MajorRecordFlagsRaw"] is JsonValue fv && long.TryParse(fv.ToJsonString(), out long flags)) merged["MajorRecordFlagsRaw"] = flags & ~PartialFormFlag;
        foreach (string view in new[] { "StarfieldMajorRecordFlags", "MajorFlags" })
            if (merged[view] is JsonArray list)
            {
                var keep = list.Where(x => !(x is JsonValue xv && xv.TryGetValue<string>(out var s) && s is "PartialForm" or "0x4000")).Select(x => x?.DeepClone()).ToArray();
                if (keep.Length == 0) merged.Remove(view); else merged[view] = new JsonArray(keep);
            }
        string over = under.Merged != null ? $"{under.Plugin} ({under.Merged})" : under.Plugin;
        return new RecordView
        {
            Ref = partial.Ref, Info = partial.Info, Type = partial.Type, Json = merged, Engine = partial.Engine,
            Merged = $"{partial.Plugin}'s partial form merged over {over}", Through = under.Through.Append(under.Plugin).ToList(),
        };
    }

    /// <summary>
    /// The record as the game has it at this plugin: when it is a partial form, merged over the version before it in
    /// the plugin's own masters (that version merged too, when it is a partial form itself). With no version before
    /// it, the partial form as it is.
    /// </summary>
    public RecordView Effective(RecordEngine engine, RecordRef r) => Effective(engine, r, 0);

    RecordView Effective(RecordEngine engine, RecordRef r, int depth)
    {
        var view = View(engine, r);
        if (!IsPartial(view) || depth > 64) return view;
        return Under(engine, r, depth) is { } under ? Merge(view, under) : view;
    }

    /// <summary>The version a plugin's record sits on: the last of the plugin's masters that holds it, as the game has it there.</summary>
    RecordView? Under(RecordEngine engine, RecordRef r, int depth)
    {
        var p = engine.Index.Plugins[r.Plugin];
        var seg = p.Segment;
        if (seg == null || p.Header == null) return null;
        var e = seg[r.Entry];
        string owner = seg.Names[e.Owner];
        var masters = p.Header.Masters;
        for (int i = masters.Count - 1; i >= 0; i--)
        {
            // A master of a file outside Data is read from Data
            foreach (var eng in engine == Engine ? new[] { engine } : new[] { engine, Engine })
                if (Find(eng.Index, masters[i], owner, e.LocalId) is { } at) return Effective(eng, at, depth + 1);
        }
        return null;
    }

    static bool HoldsChildren(JsonNode? n) => n switch
    {
        JsonObject o => RecordSplit.ChildOf(o) != null || o.Any(p => HoldsChildren(p.Value)),
        JsonArray a => a.Any(HoldsChildren),
        _ => false,
    };

    /// <summary>The subrecord a record's own field is read from, when the schema knows one.</summary>
    string? SubrecordOf(string type, string field)
    {
        lock (_subrecords)
        {
            if (_subrecords.TryGetValue((type, field), out var known)) return known;
            string? sig = null;
            var p = Schema.Props(type).FirstOrDefault(x => x.Name == field);
            if (p != null)
            {
                var f = p.Main;
                string? raw = f.Subrecord ?? (f.RefObject != null && Schema.Model.Objects.TryGetValue(f.RefObject, out var ro) ? ro.Sig : null);
                sig = SchemaModel.MapSig(f.SigMap, raw);
            }
            return _subrecords[(type, field)] = sig;
        }
    }

    readonly Dictionary<(string, string), string?> _subrecords = new();
}
