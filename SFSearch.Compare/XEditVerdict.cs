// The xEdit-style ITM verdict (GAPS S49), on top of the diff engine's rules (FieldDiff.XEditRule). Two things SF1Edit
// does that the field rules don't cover, both measured on the ITM hunt of 2026-11-15 (C:\mtg\itm-hunt):
//  - Its "master" is the record's own plugin's version, not the version before the override: an override identical
//    to an earlier override but not to the record's own plugin's version is an ITPO (identical to previous override),
//    not an ITM. The hunt's 13 SFSearch-only ITMs included such rows ("identical to explorer.esm").
//  - Its "Identical to Master" filter doesn't list an override that is the winning version over another plugin's
//    change in the load order ("Identical to Master, Wins Conflict"): reverting another mod's edit is a choice, not a
//    dirty edit. rabbits_real_lights_starfield.esm's cell 0125A0:Starfield.esm differs from Starfield.esm only in XCLC
//    padding, wins over NewAtlantisPerimeterGate.esm's change, and SF1Edit doesn't list it.
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Compare;

/// <summary>What the xEdit-style verdict says of an override.</summary>
/// <param name="Itm">SF1Edit's "Identical to Master" filter lists it.</param>
/// <param name="Rules">The rules that made it equal (empty when it is equal in every value).</param>
/// <param name="NotListed">Why SF1Edit doesn't list it although it changes nothing against the version before it; null otherwise.</param>
public sealed record XEditVerdict(bool Itm, List<string> Rules, string? NotListed);

public sealed partial class CompareSession
{
    /// <summary>
    /// The xEdit-style verdict on an override, given its comparison with the version before it (the base). Null when
    /// it changes something even by xEdit's rules.
    /// </summary>
    /// <param name="mine">The override, merged when it is a partial form.</param>
    /// <param name="diff">The override against its base.</param>
    /// <param name="basePlugin">The plugin of the base.</param>
    public XEditVerdict? LikeXEdit(PluginSide side, RecordView mine, RecordDiff diff, string basePlugin)
    {
        if (!(diff.Same || diff.SameLikeXEdit)) return null;
        var rules = diff.Same ? new List<string>() : diff.XEditRules;
        var options = new DiffOptions { Label = k => k, Language = Language.ToString() };
        // Against the record's own plugin's version, when the base is a later override of it
        if (HeaderIndex.TryParseFormKey(mine.Info.FormKey, out var owner, out var id) && !owner.Equals(basePlugin, StringComparison.OrdinalIgnoreCase)
            && Find(Index, owner, owner, id) is { } first && !IsHeavy(Engine, first))
        {
            var original = Effective(Engine, first);
            var d = Differ.Diff(original.Type, original.Json, mine.Type, mine.Json, options);
            if (!(d.Same || d.SameLikeXEdit))
                return new XEditVerdict(false, rules, $"identical to {basePlugin}, not to {owner}'s own version: SF1Edit calls that identical to a previous override (ITPO), not an ITM");
            foreach (var r in d.XEditRules) if (!rules.Contains(r)) rules.Add(r);
        }
        if (WinsConflict(side, mine, options) is { } over)
            return new XEditVerdict(false, rules, $"the winning version over {over}'s change: SF1Edit calls that \"Identical to Master, Wins Conflict\" and doesn't list it");
        return new XEditVerdict(true, rules, null);
    }

    /// <summary>
    /// When an override in Data is the winning version of its record and another loaded plugin holds a version that
    /// differs from it (counting only what xEdit counts): that plugin's name. Null otherwise, and for a plugin outside
    /// Data (it has no place in the load order).
    /// </summary>
    string? WinsConflict(PluginSide side, RecordView mine, DiffOptions options)
    {
        if (!side.InData || !HeaderIndex.TryParseFormKey(mine.Info.FormKey, out var owner, out var id)) return null;
        var versions = Index.Versions(owner, id);
        if (versions == null || versions.Winner < 0 || !Index.Plugins[versions.Holders[versions.Winner].Plugin].Name.Equals(side.Name, StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var h in versions.Holders)
        {
            var p = Index.Plugins[h.Plugin];
            if (!p.Order.Enabled || p.Name.Equals(side.Name, StringComparison.OrdinalIgnoreCase)) continue;
            RecordView theirs;
            try
            {
                if (IsHeavy(Engine, h)) continue;
                theirs = Effective(Engine, h);
            }
            catch (CompareException) { continue; }
            var d = Differ.Diff(theirs.Type, theirs.Json, mine.Type, mine.Json, options);
            if (!d.Same && !d.SameLikeXEdit) return p.Name;
        }
        return null;
    }
}
