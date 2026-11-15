using System.Text;
using System.Text.RegularExpressions;

namespace SFSearch.Harness;

/// <summary>
/// A small Data folder of plugin copies under the work folder, for the tests that must change a plugin: the soak test
/// (it changes a copy's date) and the replay's deliberate change (a different copy under the same name).
/// The game's Data folder is only ever read.
/// </summary>
public static class TestData
{
    /// <summary>The plugins the service test's commands name (tests\SFSearch.ServiceTest\Capture.cs), and their masters.</summary>
    public static readonly string[] Plugins =
    {
        "Starfield.esm", "sfbgs003.esm", "sfbgs00d.esm", "darkstar.esm", "atom74.esm", "darkstarpatch_atom74.esm", "kgcdoom.esm",
        "N12_EnergyShields.esm", "N12_EnergyShields_Fixes.esm", "DarkStar_Manufacturing.esm",
    };
    /// <summary>In the load order of the test folder, in this order.</summary>
    static readonly string[] Listed = { "darkstar.esm", "atom74.esm", "darkstarpatch_atom74.esm", "kgcdoom.esm", "N12_EnergyShields.esm", "N12_EnergyShields_Fixes.esm", "DarkStar_Manufacturing.esm", "zz_copy_test.esm" };
    const string CopyOf = "N12_EnergyShields_Fixes.esm", CopyName = "zz_copy_test.esm";
    static readonly string[] Others = { "Starfield - Localization.ba2" };

    public static string Folder => Paths.Out("testdata");
    public static string PluginsTxt => Paths.Out("testdata-plugins.txt");
    public static string TouchFile => Path.Combine(Folder, CopyName);

    /// <summary>
    /// Makes the folder: copies what is missing. With restore, also puts back any copy that differs from the file in Data
    /// (after a deliberate change). Returns what it had to copy.
    /// </summary>
    public static string Make(bool restore = false)
    {
        Directory.CreateDirectory(Folder);
        var copied = new List<string>(); var absent = new List<string>();
        foreach (var name in Plugins.Concat(Others))
        {
            string from = Path.Combine(Paths.Data, name), to = Path.Combine(Folder, name);
            if (!File.Exists(from)) { absent.Add(name); continue; }
            if (restore ? SameFile(from, to) : File.Exists(to)) continue;
            File.Copy(from, to, true);
            copied.Add(name);
        }
        string src = Path.Combine(Paths.Data, CopyOf), zz = Path.Combine(Folder, CopyName);
        if (File.Exists(src) && (!File.Exists(zz) || new FileInfo(zz).Length != new FileInfo(src).Length)) { File.Copy(src, zz, true); copied.Add(CopyName); }
        File.WriteAllText(PluginsTxt, "# sfsharness test load order\n" + string.Join("\n", Listed.Select(p => "*" + p)) + "\n", Json.Utf8);
        return $"test Data folder {Folder}: {Plugins.Length + 1 - absent.Count} plugin copies" + (copied.Count > 0 ? $", copied now: {string.Join(", ", copied)}" : ", all already there")
            + (absent.Count > 0 ? $"; not in Data: {string.Join(", ", absent)}" : "");
    }

    static bool SameFile(string a, string b)
    {
        if (!File.Exists(b)) return false;
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        return fa.Length == fb.Length && fa.LastWriteTimeUtc == fb.LastWriteTimeUtc;
    }

    /// <summary>Overwrites every place the ASCII text stands in the bytes with another of the same length. Returns how many.</summary>
    public static int ReplaceAscii(byte[] bytes, string find, string with)
    {
        if (find.Length != with.Length || find.Length == 0) throw new ArgumentException("The two texts must be the same length.");
        byte[] f = Encoding.ASCII.GetBytes(find), w = Encoding.ASCII.GetBytes(with);
        int hits = 0;
        for (int i = bytes.AsSpan().IndexOf(f); i >= 0;)
        {
            w.CopyTo(bytes, i);
            hits++;
            int next = bytes.AsSpan(i + f.Length).IndexOf(f);
            i = next < 0 ? -1 : i + f.Length + next;
        }
        return hits;
    }

    /// <summary>
    /// The deliberate change: the copy of a plugin in the test folder gets one text overwritten (an EditorID, say), so
    /// it is a different file under the same name. The file in the game's Data folder is read, never written.
    /// </summary>
    public static string Alter(string plugin, string find, string with)
    {
        string from = Path.Combine(Paths.Data, plugin), to = Path.Combine(Folder, plugin);
        if (!Paths.Under(to, Paths.Work)) throw new InvalidOperationException("Refused: only copies under the work folder are changed.");
        byte[] bytes = File.ReadAllBytes(from);
        int hits = ReplaceAscii(bytes, find, with);
        if (hits == 0) return $"'{find}' isn't in {plugin}: nothing changed.";
        File.WriteAllBytes(to, bytes);
        return $"{to}: '{find}' overwritten with '{with}' in {hits} place(s). The file in Data is untouched.";
    }

    /// <summary>The recorded commands the test folder can answer: those that name at least one plugin, and only plugins it holds.</summary>
    public static bool Answerable(Recorded r)
    {
        var have = Plugins.Append(CopyName).Select(p => p.ToLowerInvariant()).ToHashSet();
        var named = Replay.NamedPlugins(r.Args);
        if (named.Count == 0 || !named.All(have.Contains)) return false;
        // A FormKey inside SQL may point into a plugin the folder doesn't hold
        string text = string.Join(" ", r.Args) + " " + r.Stdin;
        return Regex.Matches(text, @"[0-9A-Fa-f]{6}:([A-Za-z0-9_ \-]+\.es[mpl])").All(m => have.Contains(m.Groups[1].Value.ToLowerInvariant()));
    }
}
