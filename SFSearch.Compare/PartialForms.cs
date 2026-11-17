// Partial forms (GAPS S50). A record with the header flag 0x4000 (PartialForm) holds only some of its fields: the
// game lays the fields it holds over the version before it and keeps that version's values for the rest. Starfield
// writes them for cells, quests and topics, and the DLCs for locations and worldspaces too (Mutagen's XML marks the
// types partialForm="true"). A comparison that took the partial form literally showed every field it leaves out as
// changed: SFBGS00D.esm's CityNewAtlantisLodgeInt (06BB29:Starfield.esm) holds only its EDID.
//
// Which records are partial forms, and the merge, are Mutagen's (IsPartialForm and MergeOnto, C58 W7, fixes.20): the
// version before the partial form, with each field the partial form's bytes hold laid over it. Since fixes.23 (C75,
// GAPS S169) the fields a record's bytes hold include what Mutagen's hand-written readers fill (a quest's unused
// conditions and NextAliasID, a cell's NumTraversalFluffBytes, a worldspace's object bounds), so
// Aurie_UpgradedCompanions.esm's 13 quests merge to their Starfield.esm versions and SFBGS00D.esm's lodge keeps its
// base's traversals. The header's values and the records nested in it are the partial form's; its flags lose the
// PartialForm bit, since the merged record is whole. A partial form that sets a field to its default holds it (its bytes
// have the subrecord).
using Mutagen.Bethesda.Plugins.Records;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Compare;

public sealed partial class CompareSession
{
    /// <summary>True when the record is a partial form (Mutagen's IsPartialForm: its type can be one, header flag 0x4000).
    /// A merged view is whole.</summary>
    public static bool IsPartial(RecordView v) => v.Merged == null && v.Record.IsPartialForm();

    /// <summary>
    /// The record as the game has it after a partial form: the partial form's fields laid over the version under it.
    /// A record that isn't a partial form is given back as it is.
    /// </summary>
    public RecordView Merge(RecordView partial, RecordView under)
    {
        if (!IsPartial(partial) || partial.Type != under.Type) return partial;
        MajorRecord merged;
        try { merged = partial.Record.MergeOnto(under.Record); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { throw new CompareException($"{partial.Label} of {partial.Plugin} can't be merged over {under.Plugin}: {e.Message}"); }
        string over = under.Merged != null ? $"{under.Plugin} ({under.Merged})" : under.Plugin;
        return new RecordView
        {
            Ref = partial.Ref, Info = partial.Info, Type = partial.Type, Json = partial.Engine.Json(merged, partial.Type), Engine = partial.Engine, Record = merged,
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
}
