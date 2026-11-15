// A set of plugins a report reads across, named as a caller names it (GAPS S40): one plugin, a list, @official (the
// game's own plugins and Bethesda's Creations), @loaded (every plugin the load order enables), @winners (the same
// plugins, each record only in the version the game uses), @all (every plugin in Data), or a name with * in it.
using System.Text.RegularExpressions;
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

    /// <summary>
    /// The plugins Bethesda ships: the game's own (Starfield.esm, Constellation.esm, OldMars.esm, ShatteredSpace.esm and
    /// the rest of the implicit list) and its Creations (SFBGS*, sfta*, BlueprintShips-*). In this Data folder that is
    /// 33 plugins, the set the Mutagen coverage scans call "the official plugins".
    /// </summary>
    public static bool IsOfficial(string name) =>
        LoadOrder.Implicit.Contains(name, StringComparer.OrdinalIgnoreCase) || OfficialName.IsMatch(name);

    static readonly Regex OfficialName = new(@"^(sfbgs[^\\/]*|sfta[^\\/]*|blueprintships-[^\\/]*)\.esm$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The words a set may be named by: @official, @loaded, @winners, @all.</summary>
    public const string Words = "@official (the game's own plugins and Bethesda's Creations), @loaded (every plugin the load order enables), @winners (the same, each record only in the version the game uses), @all (every plugin in Data)";

    /// <summary>
    /// The set a text names: items parted by commas; each a word (<see cref="Words"/>), a plugin's name, or a name with
    /// * (sfta*.esm). Null or empty gives <paramref name="fallback"/>.
    /// </summary>
    public static PluginSet Parse(HeaderIndex index, string? text, string fallback = "@winners")
    {
        if (string.IsNullOrWhiteSpace(text)) text = fallback;
        var items = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var chosen = new List<IndexedPlugin>();
        bool winners = false;
        var inData = index.Plugins.Where(p => p.Segment != null || p.Problems.ReadError != null).ToList();
        foreach (var raw in items)
        {
            string item = raw.Trim('"');
            switch (item.ToLowerInvariant())
            {
                case "@official": chosen.AddRange(inData.Where(p => IsOfficial(p.Name))); continue;
                case "@loaded": chosen.AddRange(inData.Where(p => p.Order.Enabled)); continue;
                case "@winners": chosen.AddRange(inData.Where(p => p.Order.Enabled)); winners = true; continue;
                case "@all": chosen.AddRange(inData); continue;
            }
            if (item.StartsWith('@'))
                throw new ReportException($"\"{item}\" isn't a set of plugins SFSearch knows", $"use {Words}, or plugin names parted by commas");
            if (item.Contains('*'))
            {
                var rx = new Regex("^" + Regex.Escape(item).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var hits = inData.Where(p => rx.IsMatch(p.Name)).ToList();
                if (hits.Count == 0) throw new ReportException($"no plugin in Data is named like \"{item}\"", "check the pattern; loadorder2 lists the plugins");
                chosen.AddRange(hits);
                continue;
            }
            var p = index.Plugin(item) ?? throw new ReportException($"there is no plugin named {item} in the Data folder", "check the name; loadorder2 lists the plugins (a file outside Data can't be profiled)");
            chosen.Add(p);
        }
        if (winners && items.Length > 1)
            throw new ReportException("@winners can't be mixed with other plugins", "ask for @winners alone, or name the plugins (each record then counts in every plugin that holds it)");
        var list = chosen.Distinct().OrderBy(p => p.Order.Enabled ? 0 : 1).ThenBy(p => p.Order.Position < 0 ? int.MaxValue : p.Order.Position).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) throw new ReportException($"\"{text}\" names no plugin that is in Data", "loadorder2 lists the plugins");
        string shown = items.Length == 1 && items[0].StartsWith('@') ? $"{items[0].ToLowerInvariant()} ({ReportAnswer.Plural(list.Count, "plugin")})"
            : list.Count == 1 ? list[0].Name
            : list.Count <= 4 ? string.Join(", ", list.Select(p => p.Name))
            : $"{string.Join(", ", list.Take(3).Select(p => p.Name))} and {list.Count - 3} more ({list.Count} plugins)";
        return new PluginSet { Plugins = list, WinnersOnly = winners, Shown = shown };
    }
}
