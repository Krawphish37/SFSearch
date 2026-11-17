// The one resolver of plugin names (GAPS S117): sets, patterns, names, and what it leaves to the caller.
using SFSearch.Index;

namespace SFSearch.Index.Tests;

static class PluginSetsTests
{
    public static string Run(Ctx ctx)
    {
        var index = ctx.DataIndex;
        static void Check(bool ok, string what) => Ctx.Check(ok, what);
        static string Thrown(Action a) { try { a(); return ""; } catch (PluginSetException e) { return e.Message + " | " + e.Fix; } }

        // @official: the game's own plugins and Bethesda's Creations, in load order, Starfield.esm first
        var off = PluginSets.Resolve(index, new[] { "@official" });
        var offNames = off.Plugins.Select(p => p.Name).ToList();
        Check(offNames.Count == 33 && offNames[0].Equals("Starfield.esm", StringComparison.OrdinalIgnoreCase) && offNames.All(PluginSets.IsOfficial) && !PluginSets.IsOfficial("darkstar.esm"),
            $"@official gave {offNames.Count} plugins ({string.Join(", ", offNames.Take(4))} …), not the 33 official ones with Starfield.esm first");
        Check(off.Picks.All(p => p.Kind == PluginPickKind.Set && p.Item == "@official") && off.NamesASet && !off.WinnersOnly, "@official's picks aren't all the set's");
        string shown = off.Shown(offNames);
        Check(shown == "@official (33 plugins)", $"@official is shown as \"{shown}\"");
        // A set's word in any letter case; a plugin named twice comes once, at its first place
        var mixed = PluginSets.Resolve(index, new[] { "ShatteredSpace.esm", "@OFFICIAL" });
        var mixedNames = mixed.Plugins.Select(p => p.Name).ToList();
        Check(mixedNames.Count == 33 && mixedNames[0].Equals("ShatteredSpace.esm", StringComparison.OrdinalIgnoreCase) && mixed.Picks[0].Kind == PluginPickKind.Name, $"ShatteredSpace.esm,@OFFICIAL gave {mixedNames.Count} plugins, first {mixedNames.FirstOrDefault()}");
        string mixedShown = mixed.Shown(mixedNames);
        Check(mixedShown == "ShatteredSpace.esm, @official (33 plugins)", $"the mixed set is shown as \"{mixedShown}\"");

        // @loaded and @winners: the enabled plugins; @all every plugin in Data
        var loaded = PluginSets.Resolve(index, new[] { "@loaded" }).Plugins.ToList();
        Check(loaded.Count > 0 && loaded.All(p => p.Order.Enabled), "@loaded holds a plugin that isn't enabled");
        var winners = PluginSets.Resolve(index, new[] { "@winners" });
        Check(winners.WinnersOnly && winners.Plugins.Count() == loaded.Count, "@winners isn't the loaded plugins, winners only");
        int all = PluginSets.Resolve(index, new[] { "@all" }).Plugins.Count();
        Check(all >= loaded.Count && all <= index.Plugins.Count, $"@all gave {all} of {index.Plugins.Count} plugins");

        // A pattern: * and ?, in load order
        var sfta = PluginSets.Resolve(index, new[] { "SFTA0?.esm" }).Plugins.Select(p => p.Name).ToList();
        var byHand = index.Plugins.Where(p => p.Name.Length == 10 && p.Name.StartsWith("sfta0", StringComparison.OrdinalIgnoreCase) && p.Name.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check(sfta.Count > 0 && sfta.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(byHand), $"SFTA0?.esm gave {string.Join(", ", sfta)}");

        // Left to the caller: a path, a name that isn't in Data. A name without .esm is found
        var others = PluginSets.Resolve(index, new[] { "Starfield", "C:/somewhere/mod.esm", "nosuchplugin.esm" });
        Check(others.Picks.Count == 3 && others.Picks[0].Plugin?.Name.Equals("Starfield.esm", StringComparison.OrdinalIgnoreCase) == true
            && others.Picks[1].Kind == PluginPickKind.Other && others.Picks[2].Kind == PluginPickKind.Other, "a path or a missing name wasn't handed back to the caller");

        // Errors: a set SFSearch doesn't know (with the near one), a pattern that fits nothing, @winners mixed or where it doesn't apply
        string unknown = Thrown(() => PluginSets.Resolve(index, new[] { "@offical" }));
        Check(unknown.Contains("isn't a set of plugins SFSearch knows") && unknown.Contains("did you mean @official"), $"@offical: \"{unknown}\"");
        string none = Thrown(() => PluginSets.Resolve(index, new[] { "zzz*.esm" }));
        Check(none.StartsWith("no plugin in Data fits \"zzz*.esm\""), $"zzz*.esm: \"{none}\"");
        string mixWinners = Thrown(() => PluginSets.Resolve(index, new[] { "@winners", "Starfield.esm" }));
        Check(mixWinners.StartsWith("@winners can't be mixed"), $"@winners,Starfield.esm: \"{mixWinners}\"");
        string noWinners = Thrown(() => PluginSets.Resolve(index, new[] { "@winners" }, winnersAllowed: false, verb: "find"));
        Check(noWinners.Contains("find doesn't count records that way") && noWinners.Contains("@loaded"), $"@winners in find: \"{noWinners}\"");

        // The first word of query2 names plugins when it is names, patterns or sets
        Check(PluginSets.LooksLikePlugins("@official") && PluginSets.LooksLikePlugins("kgcdoom.esm,@all") && PluginSets.LooksLikePlugins("sfta*")
            && !PluginSets.LooksLikePlugins("SELECT 1;") && !PluginSets.LooksLikePlugins("@") && !PluginSets.LooksLikePlugins("@1;"), "LooksLikePlugins is wrong");
        return $"@official {offNames.Count}, @loaded {loaded.Count}, @all {all}, SFTA0?.esm {sfta.Count}; errors: {unknown.Split(" | ")[0]}";
    }
}
