// A set of plugins a report reads across, named as a caller names it (GAPS S40): one plugin, a list, @official (the
// game's own plugins and Bethesda's Creations), @loaded (every plugin the load order enables), @winners (the same
// plugins, each record only in the version the game uses), @all (every plugin in Data), or a name with * in it.
// The words are resolved by the one resolver every verb uses (SFSearch.Index.PluginSets, GAPS S117).
using SFSearch.Index;

namespace SFSearch.Reports;

public sealed class PluginSet
{
    /// <summary>The plugins, in load order (enabled ones first, then the rest by their place or name).</summary>
    public required List<IndexedPlugin> Plugins { get; init; }
    /// <summary>Only the version of each record the game uses counts (@winners).</summary>
    public bool WinnersOnly { get; init; }
    /// <summary>How the caller named the set, as the answer repeats it: "@official (33 plugins)".</summary>
    public required string Shown { get; init; }

    /// <inheritdoc cref="PluginSets.IsOfficial"/>
    public static bool IsOfficial(string name) => PluginSets.IsOfficial(name);

    /// <inheritdoc cref="PluginSets.Words"/>
    public const string Words = PluginSets.Words;

    /// <summary>
    /// The set a text names: items parted by commas; each a word (<see cref="Words"/>), a plugin's name, or a name with
    /// * (sfta*.esm). Null or empty gives <paramref name="fallback"/>.
    /// </summary>
    public static PluginSet Parse(HeaderIndex index, string? text, string fallback = "@winners")
    {
        if (string.IsNullOrWhiteSpace(text)) text = fallback;
        PluginNaming naming;
        try { naming = PluginSets.Resolve(index, PluginSets.Split(text)); }
        catch (PluginSetException e) { throw new ReportException(e.Message, e.Fix); }
        if (naming.Picks.FirstOrDefault(p => p.Kind == PluginPickKind.Other) is { } other)
            throw new ReportException($"there is no plugin named {other.Item} in the Data folder", "check the name; loadorder2 lists the plugins (a file outside Data can't be profiled)");
        var list = naming.Plugins.OrderBy(p => p.Order.Enabled ? 0 : 1).ThenBy(p => p.Order.Position < 0 ? int.MaxValue : p.Order.Position).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) throw new ReportException($"\"{text}\" names no plugin that is in Data", "loadorder2 lists the plugins");
        return new PluginSet { Plugins = list, WinnersOnly = naming.WinnersOnly, Shown = naming.Shown(list.Select(p => p.Name).ToList()) };
    }

    /// <summary>
    /// The plugins a text names, for a report that reads each plugin by itself (scripts2): set and pattern items become
    /// the names of their plugins in Data; any other item (a name, the path of a plugin file) stays as written for the
    /// report to open. Shown: how they were named, when a set is among them ("@official (33 plugins)"); else null.
    /// </summary>
    public static (List<string> Names, string? Shown) Names(HeaderIndex index, string text)
    {
        PluginNaming naming;
        try { naming = PluginSets.Resolve(index, PluginSets.Split(text), winnersAllowed: false, verb: "this report"); }
        catch (PluginSetException e) { throw new ReportException(e.Message, e.Fix); }
        var names = naming.Picks.Select(p => p.Kind is PluginPickKind.Set or PluginPickKind.Pattern ? p.Plugin!.Name : p.Item).ToList();
        return (names, naming.NamesASet ? naming.Shown(names) : null);
    }
}
