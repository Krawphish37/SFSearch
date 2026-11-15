using System.IO.Hashing;
using System.Text;

namespace SFSearch.Index;

/// <summary>
/// One string file a localized plugin's texts are read from. A loose file carries its own quick stamp; a file inside
/// an archive carries the archive's, since the archive is the file on disk that changes.
/// </summary>
/// <param name="Name">The file's name, lower case: <c>starfield_en.strings</c>.</param>
/// <param name="From">The loose file's full path, or the archive's.</param>
/// <param name="Stamp">The quick stamp of that file (size, date, a hash of its first and last 64 KB): the same check
/// as a plugin's, so a change in place that keeps the size and date is seen when it touches those ends (all of a file
/// up to 128 KB).</param>
public sealed record StringFile(string Name, string From, bool InArchive, QuickStamp Stamp)
{
    /// <summary>"Strings\x_en.strings" for a loose file, "x_en.strings in X - Main.ba2" for an archived one.</summary>
    public string Where => InArchive ? $"{Name} in {Path.GetFileName(From)}" : Path.Combine(Path.GetFileName(Path.GetDirectoryName(From) ?? ""), Path.GetFileName(From));
}

/// <summary>
/// The string files of a localized plugin as one stamp: each file the plugin's texts are read from, for every kind
/// (.strings, .dlstrings, .ilstrings) and language, with its quick stamp, or the quick stamp of the archive it is
/// read from. Compared by <see cref="Key"/> only, like a quick stamp: a date that went backwards is a change.
/// </summary>
public sealed class StringsStamp
{
    /// <summary>The stamp of a plugin that isn't localized, or of an index that doesn't look for string files.</summary>
    public static readonly StringsStamp NotStamped = new(false, Array.Empty<StringFile>());

    /// <summary>The plugin is localized and its string files were looked for (whether or not any were found).</summary>
    public bool Stamped { get; }
    /// <summary>By name. Empty when none was found.</summary>
    public IReadOnlyList<StringFile> Files { get; }
    /// <summary>XXH3 (128 bit) over every file's name, place and quick stamp. Zero when there is no file.</summary>
    public Key128 Key { get; }

    StringsStamp(bool stamped, IReadOnlyList<StringFile> files)
    {
        Stamped = stamped;
        Files = files.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        if (Files.Count == 0) return;
        var sb = new StringBuilder();
        foreach (var f in Files)
            sb.Append(f.Name).Append('|').Append(f.From.ToLowerInvariant()).Append('|').Append(f.InArchive ? 'a' : 'l').Append('|')
              .Append(f.Stamp.Length).Append('|').Append(f.Stamp.WriteTicksUtc).Append('|').Append(f.Stamp.EndsHash.ToString("x16")).Append('\n');
        Key = Key128.From(XxHash128.HashToUInt128(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>The stamp of a localized plugin's string files as they were found.</summary>
    public static StringsStamp Of(IReadOnlyList<StringFile> files) => new(true, files);

    /// <summary>The same files, sizes and dates (or both not stamped).</summary>
    public bool SameAs(StringsStamp other) => Stamped == other.Stamped && Key == other.Key;

    /// <summary>As the [source] line shows it: the first 8 digits of the key, "none" when the plugin has no string
    /// files, empty when it isn't stamped.</summary>
    public string Short => !Stamped ? "" : Key.IsZero ? "none" : Key.Short;

    /// <summary>What changed from one stamp to the next, in words: the files that came, went or changed (up to three
    /// named).</summary>
    public static string Changes(StringsStamp before, StringsStamp after)
    {
        var old = before.Files.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var now = after.Files.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var changed = new List<string>();
        foreach (var f in after.Files)
            if (!old.TryGetValue(f.Name, out var o)) changed.Add($"{f.Where} is new");
            else if (o != f) changed.Add(o.From.Equals(f.From, StringComparison.OrdinalIgnoreCase) ? $"{f.Where} changed" : $"{f.Name} is now read from {f.Where}, not {o.Where}");
        foreach (var f in before.Files)
            if (!now.ContainsKey(f.Name)) changed.Add($"{f.Where} is gone");
        if (changed.Count == 0) return "the same files";
        return string.Join("; ", changed.Take(3)) + (changed.Count > 3 ? $"; {changed.Count - 3} more" : "");
    }
}

/// <summary>
/// Where a localized plugin's string files are found, worked out the way its texts are read (the record layer does it
/// with Mutagen's own rules: loose files first, then the archives in the order Mutagen reads them). The header index
/// takes one look per refresh and asks it for the stamp of each localized plugin.
/// </summary>
public abstract class StringFilesSource
{
    /// <summary>One look at the folders the string files can be in. Several threads may ask the look at once.</summary>
    public abstract IStringFilesLook Look();
}

/// <summary>One look at the folders. Disposing it keeps what was learned (archives read) for the next look.</summary>
public interface IStringFilesLook : IDisposable
{
    StringsStamp Stamp(string pluginPath, string pluginName);
}
