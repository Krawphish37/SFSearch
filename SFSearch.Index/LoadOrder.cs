using System.IO.Hashing;

namespace SFSearch.Index;

/// <summary>One line of the load order: a plugin the game's lists name, or a plugin in Data that no list names.</summary>
public sealed class LoadOrderEntry
{
    public string Name { get; init; } = "";
    /// <summary>Place in the load order, from 0. -1 when no list names the plugin.</summary>
    public int Position { get; init; } = -1;
    /// <summary>Listed as enabled, and its file is there.</summary>
    public bool Enabled { get; internal set; }
    public bool InData { get; internal set; }
    public MasterStyle Size { get; internal set; }
    /// <summary>The runtime prefix of the plugin's own records: "0A", "FD:03", "FE:09A". Empty when it isn't loaded.</summary>
    public string Slot { get; internal set; } = "";
    public long Length { get; internal set; }
}

/// <summary>
/// The load order, read the way Mutagen reads it for Starfield and with no Mutagen:
/// the game's implicit plugins that are in Data, then Starfield.ccc's (those in Data), then plugins.txt, each plugin once.
/// A line of plugins.txt is enabled when it starts with '*'.
/// </summary>
public static class LoadOrder
{
    /// <summary>The plugins Starfield loads without being told to, in order (Mutagen 0.55's list).</summary>
    public static readonly IReadOnlyList<string> Implicit = new[]
    {
        "Starfield.esm", "Constellation.esm", "OldMars.esm", "BlueprintShips-Starfield.esm", "ShatteredSpace.esm",
        "SFBGS006.esm", "SFBGS007.esm", "SFBGS003.esm", "SFBGS008.esm", "SFBGS004.esm", "SFBGS00D.esm", "SFBGS047.esm",
        "SFBGS050.esm", "BlueprintShips-SFBGS050.esm",
    };

    /// <summary>Starfield's own plugins.txt: %LOCALAPPDATA%\Starfield\Plugins.txt.</summary>
    public static string DefaultPluginsTxt =>
        Path.Combine(Environment.GetEnvironmentVariable("LocalAppData") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Starfield", "Plugins.txt");

    /// <summary>Starfield.ccc sits in the game folder, beside Data.</summary>
    public static string DefaultCcc(string dataFolder) =>
        Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataFolder))) ?? dataFolder, "Starfield.ccc");

    /// <summary>The names plugins.txt lists, in order, with their enabled mark. Lines that name no plugin are returned in <paramref name="badLines"/>.</summary>
    public static List<(string Name, bool Enabled)> ParsePluginsTxt(IEnumerable<string> lines, List<string>? badLines = null)
    {
        var list = new List<(string, bool)>();
        foreach (var raw in lines)
        {
            var line = TrimComment(raw).Trim();
            if (line.Length == 0) continue;
            bool enabled = line[0] == '*';
            if (enabled) line = line[1..];
            if (IsPluginFileName(line)) list.Add((line, enabled));
            else badLines?.Add(raw);
        }
        return list;
    }

    /// <summary>A '#' starts a comment, except inside the plugin's name (anything up to the last .esm, .esp or .esl).</summary>
    static string TrimComment(string s)
    {
        int from = 0;
        foreach (var ext in new[] { ".esp", ".esm", ".esl" })
        {
            int at = s.LastIndexOf(ext, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) from = Math.Max(from, at + 4);
        }
        int hash = s.IndexOf('#', from);
        return hash >= 0 ? s[..hash] : s;
    }

    static bool IsPluginFileName(string s) => s.Length > 4 && Files.IsPluginName(s) && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    static int TypeRank(string name) => name.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ? 0 : name.EndsWith(".esl", StringComparison.OrdinalIgnoreCase) ? 1 : 2;

    /// <summary>
    /// Every plugin: first the listed ones in load order (enabled or not, in Data or not), then the plugins in Data that
    /// no list names, by name. Slots are given to the enabled plugins that are in Data, counted per size.
    /// </summary>
    /// <param name="inData">Every plugin file that is there: its name, size style and length.</param>
    public static List<LoadOrderEntry> Build(IReadOnlyDictionary<string, (MasterStyle Size, long Length)> inData, IEnumerable<string>? pluginsTxtLines, IEnumerable<string>? cccLines, List<string>? badLines = null)
    {
        var plugins = pluginsTxtLines == null ? new List<(string Name, bool Enabled)>() : ParsePluginsTxt(pluginsTxtLines, badLines);
        var listed = new List<(string Name, bool Enabled)>();
        foreach (var name in Implicit) if (inData.ContainsKey(name)) listed.Add((name, true));
        if (cccLines != null)
        {
            // Creation Club plugins that are in Data: in plugins.txt's order where it names them, masters first
            var ccc = cccLines.Select(l => l.Trim()).Where(l => IsPluginFileName(l) && inData.ContainsKey(l))
                .Select((l, i) => (Name: l, Index: plugins.FindIndex(p => p.Name.Equals(l, StringComparison.OrdinalIgnoreCase)), I: i))
                .OrderBy(x => TypeRank(x.Name)).ThenBy(x => x.Index).ThenBy(x => x.I);
            foreach (var c in ccc) listed.Add((c.Name, true));
        }
        listed.AddRange(plugins);

        var result = new List<LoadOrderEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The name as the file spells it, when the file is there
        var spelled = inData.Keys.ToDictionary(k => k, k => k, StringComparer.OrdinalIgnoreCase);
        int pos = 0;
        foreach (var (name, enabled) in listed)
        {
            if (!seen.Add(name)) continue;
            result.Add(new LoadOrderEntry { Name = spelled.GetValueOrDefault(name, name), Position = pos++, Enabled = enabled });
        }
        foreach (var name in inData.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            if (seen.Add(name)) result.Add(new LoadOrderEntry { Name = name, Position = -1, Enabled = false });
        int full = 0, medium = 0, small = 0;
        foreach (var e in result)
        {
            if (!inData.TryGetValue(e.Name, out var file)) { e.Enabled = false; continue; }
            e.InData = true; e.Size = file.Size; e.Length = file.Length;
            if (!e.Enabled) continue;
            e.Slot = e.Size == MasterStyle.Small ? $"FE:{small++:X3}" : e.Size == MasterStyle.Medium ? $"FD:{medium++:X2}" : $"{full++:X2}";
        }
        return result;
    }

    /// <summary>The lines of a list file, or null when it isn't there. Read with sharing, so a mod manager writing it isn't blocked.</summary>
    public static string[]? ReadLines(string? path, out ulong stamp)
    {
        stamp = 0;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        byte[] bytes;
        using (var fs = Files.OpenShared(path)) { bytes = new byte[fs.Length]; fs.ReadExactly(bytes); }
        stamp = XxHash3.HashToUInt64(bytes) | 1;
        using var reader = new StreamReader(new MemoryStream(bytes));
        var lines = new List<string>();
        while (reader.ReadLine() is string l) lines.Add(l);
        return lines.ToArray();
    }
}
