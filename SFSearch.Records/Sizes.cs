// The one way a size is written in every answer of the new tools (the size of a plugin, an archive, a file inside
// an archive, a scratch folder, a record). The comparisons, the reports, the SQL layer and the engine all call it,
// so one file never shows as two sizes.
using System.Globalization;

namespace SFSearch.Records;

public static class Sizes
{
    /// <summary>
    /// A size in bytes as text. 1 KB is 1,024 bytes, 1 MB is 1,024 KB, 1 GB is 1,024 MB.
    /// Under 1 KB: the bytes ("312 bytes"). Under 1 MB: whole KB, rounded down ("51 KB" for 53,235 bytes).
    /// From 1 MB: one decimal, rounded to the nearest ("25.2 MB"). From 1 GB: two decimals ("1.36 GB").
    /// A decimal that is zero isn't written ("7 MB").
    /// </summary>
    public static string Text(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
        if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        if (bytes >= 1024) return (bytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB";
        return bytes == 1 ? "1 byte" : bytes.ToString(CultureInfo.InvariantCulture) + " bytes";
    }
}

/// <summary>
/// A plugin's size as an answer names it. The game's files and Mutagen say Small; the game's menus, xEdit and
/// modders say Light. An answer writes "Light (Small)" the first time and "Light" after it. Medium and Full have one
/// name everywhere. One instance per answer. (design\agent-first\notes\fix-tools.md, item B11.)
/// </summary>
public sealed class SizeWords
{
    /// <summary>Where a size sits in a text made before the answer is written (a report's rows): resolved in order by <see cref="Resolve"/>.</summary>
    public const char Mark = '⁣';
    bool _lightSaid;

    /// <summary>"Full", "Medium", "Light (Small)" the first time and "Light" after; anything else as it is.</summary>
    public string Name(string size)
    {
        if (!size.Equals("Small", StringComparison.OrdinalIgnoreCase)) return size;
        if (_lightSaid) return "Light";
        _lightSaid = true;
        return "Light (Small)";
    }

    /// <summary>A size kept for later naming: <see cref="Resolve"/> turns it into its name in the order the answer prints it.</summary>
    public static string Marked(string size) => $"{Mark}{size}{Mark}";

    /// <summary>Every marked size of the text named, in order.</summary>
    public string Resolve(string text)
    {
        if (text.IndexOf(Mark) < 0) return text;
        var sb = new System.Text.StringBuilder(text.Length + 16);
        int at = 0;
        while (true)
        {
            int a = text.IndexOf(Mark, at);
            int b = a < 0 ? -1 : text.IndexOf(Mark, a + 1);
            if (b < 0) { sb.Append(text, at, text.Length - at); break; }
            sb.Append(text, at, a - at).Append(Name(text[(a + 1)..b]));
            at = b + 1;
        }
        return sb.ToString();
    }

    /// <summary>The text with every marked size in its full name ("Light (Small)"): for a value read alone (JSON).</summary>
    public static string Plain(string text) => text.Replace($"{Mark}Small{Mark}", "Light (Small)").Replace(Mark.ToString(), "");
}
