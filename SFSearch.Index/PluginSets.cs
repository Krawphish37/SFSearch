// How a caller names the plugins a command reads (GAPS S40, S117): the one resolver behind every verb and tool that
// takes several plugins (query2, saved run, find, refs2, scripts2, indexes, profile, stored queries' plugin=).
// Items are parted by commas; each is a plugin's name in Data, a name with * or ? in it (sfta*.esm), or a named set:
// @official (the game's own plugins and Bethesda's Creations), @loaded (every plugin the load order enables), @winners
// (the same plugins, each record only in the version the game uses), @all (every plugin in Data).
// Anything else (the path of a plugin file elsewhere, a name that isn't in Data) is handed back to the caller, which
// reads a file outside Data or says which names are near.
using System.Text.RegularExpressions;

namespace SFSearch.Index;

/// <summary>How an item named its plugins.</summary>
public enum PluginPickKind
{
    /// <summary>The plugin's own name.</summary>
    Name,
    /// <summary>A name with * or ? in it.</summary>
    Pattern,
    /// <summary>A named set: @official, @loaded, @winners, @all.</summary>
    Set,
    /// <summary>Not a plugin in Data: a path, or a name that isn't there. The caller decides (<see cref="PluginPick.Plugin"/> is null).</summary>
    Other,
}

/// <summary>One plugin a caller's words named, and the item that named it.</summary>
public sealed record PluginPick(IndexedPlugin? Plugin, string Item, PluginPickKind Kind);

/// <summary>What a caller's words named: each plugin once, in the order named (a set's or a pattern's plugins in load order).</summary>
public sealed class PluginNaming
{
    /// <summary>The items as the caller wrote them (a set's word in lower case).</summary>
    public required IReadOnlyList<string> Items { get; init; }
    public required IReadOnlyList<PluginPick> Picks { get; init; }
    /// <summary>@winners: only the version of each record the game uses counts.</summary>
    public bool WinnersOnly { get; init; }
    /// <summary>A set's word is among the items.</summary>
    public bool NamesASet => Items.Any(PluginSets.IsSetWord);
    /// <summary>The plugins in Data, in the order named.</summary>
    public IEnumerable<IndexedPlugin> Plugins => Picks.Where(p => p.Plugin != null).Select(p => p.Plugin!);
    /// <summary>How the caller named them, as an answer repeats it: "@official (33 plugins)".</summary>
    public string Shown(IReadOnlyList<string> names) => PluginSets.Shown(Items, names);
}

/// <summary>Words that name no plugin, or plugins that can't go together. Message: what is wrong; <see cref="Fix"/>: what to do.</summary>
public sealed class PluginSetException(string what, string fix) : Exception(what)
{
    public string Fix { get; } = fix;
}

public static class PluginSets
{
    /// <summary>The words a set may be named by, as help texts and errors say them.</summary>
    public const string Words = "@official (the game's own plugins and Bethesda's Creations), @loaded (every plugin the load order enables), @winners (the same, each record only in the version the game uses), @all (every plugin in Data)";

    static readonly string[] SetWords = { "@official", "@loaded", "@winners", "@all" };

    /// <summary>
    /// The plugins Bethesda ships: the game's own (Starfield.esm, Constellation.esm, OldMars.esm, ShatteredSpace.esm and
    /// the rest of the implicit list) and its Creations (SFBGS*, sfta*, BlueprintShips-*). In this Data folder that is
    /// 33 plugins, the set the Mutagen coverage scans call "the official plugins".
    /// </summary>
    public static bool IsOfficial(string name) =>
        LoadOrder.Implicit.Contains(name, StringComparer.OrdinalIgnoreCase) || OfficialName.IsMatch(name);

    static readonly Regex OfficialName = new(@"^(sfbgs[^\\/]*|sfta[^\\/]*|blueprintships-[^\\/]*)\.esm$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>An item that names a set (it starts with @).</summary>
    public static bool IsSetWord(string item) => item.StartsWith('@');

    /// <summary>An item that is a name with * or ? (not a path: a path is read as it is).</summary>
    public static bool IsPattern(string item) => (item.Contains('*') || item.Contains('?')) && !item.Contains('/') && !item.Contains('\\');

    /// <summary>Text parted by commas into items, each trimmed of spaces and quotes.</summary>
    public static List<string> Split(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(i => i.Trim('"')).Where(i => i.Length > 0).ToList();

    /// <summary>Does the text name plugins, and nothing else? Each item a plugin file name (.esm, .esp, .esl, a * allowed) or a set's word.
    /// For a command line whose first word may name the plugins (query2 @official "SELECT …;").</summary>
    public static bool LooksLikePlugins(string text) =>
        Split(text) is { Count: > 0 } items && items.All(i => IsSetWord(i) ? i.Length > 1 && i.Skip(1).All(char.IsLetter)
            : i.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || i.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || i.EndsWith(".esl", StringComparison.OrdinalIgnoreCase) || (IsPattern(i) && i.EndsWith('*')));

    /// <summary>The plugins a set may hold: those in Data that were read, or found unreadable (a command that reads one says why).</summary>
    static List<IndexedPlugin> InData(HeaderIndex index) => index.Plugins.Where(p => p.Segment != null || p.Problems.ReadError != null).ToList();

    /// <summary>Load order: enabled plugins by their place, then the rest by their place in plugins.txt, then by name.</summary>
    static IEnumerable<IndexedPlugin> InOrder(IEnumerable<IndexedPlugin> plugins) =>
        plugins.OrderBy(p => p.Order.Enabled ? 0 : 1).ThenBy(p => p.Order.Position < 0 ? int.MaxValue : p.Order.Position).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What the items name. Each plugin comes once, at the first item that names it. An item that is neither a set, a
    /// pattern, nor a plugin's name in Data (with or without .esm) comes back as <see cref="PluginPickKind.Other"/> for the
    /// caller. Throws <see cref="PluginSetException"/> for a set SFSearch doesn't know, a pattern that fits no plugin, a
    /// set with no plugin in it, and @winners beside other items (or where <paramref name="winnersAllowed"/> is false).
    /// </summary>
    /// <param name="verb">The command, for the error when @winners doesn't apply ("find").</param>
    public static PluginNaming Resolve(HeaderIndex index, IEnumerable<string> items, bool winnersAllowed = true, string verb = "this command")
    {
        var given = items.Select(i => i.Trim().Trim('"')).Where(i => i.Length > 0).Select(i => IsSetWord(i) ? i.ToLowerInvariant() : i).ToList();
        var inData = InData(index);
        var picks = new List<PluginPick>();
        var seen = new HashSet<IndexedPlugin>();
        bool winners = false;
        void Add(IEnumerable<IndexedPlugin> plugins, string item, PluginPickKind kind)
        {
            foreach (var p in plugins) if (seen.Add(p)) picks.Add(new PluginPick(p, item, kind));
        }
        foreach (var item in given)
        {
            if (IsSetWord(item))
            {
                List<IndexedPlugin> members = item switch
                {
                    "@official" => inData.Where(p => IsOfficial(p.Name)).ToList(),
                    "@loaded" or "@winners" => inData.Where(p => p.Order.Enabled).ToList(),
                    "@all" => inData,
                    _ => throw Unknown(item),
                };
                if (item == "@winners")
                {
                    if (!winnersAllowed)
                        throw new PluginSetException($"@winners counts each record only in the version the game uses, and {verb} doesn't count records that way: it looks at every plugin it is given",
                            "use @loaded: the same plugins, every one the load order enables");
                    winners = true;
                }
                if (members.Count == 0)
                    throw new PluginSetException($"{item} names no plugin that is in Data", item is "@loaded" or "@winners" ? "no plugin is enabled in plugins.txt: loadorder2 shows the load order" : "loadorder2 lists the plugins");
                Add(InOrder(members), item, PluginPickKind.Set);
                continue;
            }
            if (IsPattern(item))
            {
                var rx = new Regex("^" + Regex.Escape(item).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
                var hits = inData.Where(p => rx.IsMatch(p.Name)).ToList();
                if (hits.Count == 0) throw new PluginSetException($"no plugin in Data fits \"{item}\" ({index.Plugins.Count:N0} plugins are there)", "check the pattern; loadorder2 lists the plugins");
                Add(InOrder(hits), item, PluginPickKind.Pattern);
                continue;
            }
            bool pathLike = item.Contains('\\') || item.Contains('/') || Path.IsPathRooted(item);
            var one = pathLike ? null : index.Plugin(item) ?? index.Plugin(item + ".esm");
            if (one != null) Add(new[] { one }, item, PluginPickKind.Name);
            else picks.Add(new PluginPick(null, item, PluginPickKind.Other));
        }
        if (winners && given.Count > 1)
            throw new PluginSetException("@winners can't be mixed with other plugins", "ask for @winners alone, or name the plugins (each record then counts in every plugin that holds it)");
        return new PluginNaming { Items = given, Picks = picks, WinnersOnly = winners };
    }

    static PluginSetException Unknown(string item)
    {
        string? near = SetWords.Select(w => (w, d: Distance(w, item))).Where(x => x.d <= 2).OrderBy(x => x.d).Select(x => x.w).FirstOrDefault();
        return new PluginSetException($"\"{item}\" isn't a set of plugins SFSearch knows{(near != null ? $" (did you mean {near}?)" : "")}", $"use {Words}, or plugin names parted by commas");
    }

    static int Distance(string a, string b)
    {
        a = a.ToLowerInvariant(); b = b.ToLowerInvariant();
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int diag = row[0]; row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int up = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diag + (a[i - 1] == b[j - 1] ? 0 : 1));
                diag = up;
            }
        }
        return row[b.Length];
    }

    /// <summary>
    /// How the caller named the plugins, as an answer repeats it. With a set among the items: the items as written and
    /// the number of plugins ("@official (33 plugins)", "@official, darkstar.esm (34 plugins)"). Otherwise the names:
    /// one, up to four, or three and how many more.
    /// </summary>
    public static string Shown(IReadOnlyList<string> items, IReadOnlyList<string> names)
    {
        string count = $"{names.Count:N0} plugin{(names.Count == 1 ? "" : "s")}";
        if (items.Any(IsSetWord)) return $"{string.Join(", ", items)} ({count})";
        return names.Count == 1 ? names[0]
            : names.Count <= 4 ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more ({count})";
    }
}
