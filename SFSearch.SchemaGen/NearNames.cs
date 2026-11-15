// A wrong name answered with the nearest real ones: one way of choosing them and one wording, for every tool that
// names fields (describe, the SQL layer, the record layer, and through them the stored queries and the comparisons).
// The choice was the SQL layer's (SFSearch.Query, "Nearest"); it lives here because the schema is what every one of
// those tools already has.
using System.Text;

namespace SFSearch.SchemaGen;

public static class NearNames
{
    /// <summary>How the list of nearest names begins in an error's fix: "the nearest real names: A; B; C".</summary>
    public const string Lead = "the nearest real names: ";

    /// <summary>The fix text for a wrong name: the lead and the names, best first.</summary>
    public static string Fix(IEnumerable<string> names) => Lead + string.Join("; ", names);

    /// <summary>What a library writes after its error text, for the caller to turn into the fix: " Did you mean: A; B?" (empty when there is no name).</summary>
    public static string DidYouMean(IReadOnlyCollection<string> names) => names.Count == 0 ? "" : " Did you mean: " + string.Join("; ", names) + "?";

    /// <summary>One field as a candidate: its name, then its kind, its subrecord and xEdit's name for it when that is another word.</summary>
    public static string FieldText(string name, string kind, string? subrecord, string? readable)
    {
        var facts = new List<string> { kind };
        if (!string.IsNullOrEmpty(subrecord)) facts.Add(subrecord);
        if (!string.IsNullOrEmpty(readable) && Norm(readable) != Norm(name)) facts.Add($"xEdit name \"{readable}\"");
        return $"{name} ({string.Join(", ", facts)})";
    }

    /// <summary>The texts a field can be asked by: its name, its readable name, the last part of xEdit's path.</summary>
    public static string[] FieldNames(string name, string? readable, string? xeditPath)
    {
        string leaf = xeditPath == null ? "" : xeditPath[(xeditPath.LastIndexOf('\\') + 1)..];
        if (leaf.Contains(" - ", StringComparison.Ordinal)) leaf = leaf[(leaf.IndexOf(" - ", StringComparison.Ordinal) + 3)..];
        return new[] { name, readable ?? "", leaf };
    }

    /// <summary>
    /// The fields of one object (a record type, or the object a field holds) that are nearest to a wrong name, best
    /// first, each as <see cref="FieldText"/> writes it. Only the object's own fields are candidates, not the fields
    /// of the objects inside it: a name is wrong at one level of a path.
    /// </summary>
    public static List<string> Fields(SchemaDb db, IEnumerable<string> objects, string asked, int take = 4)
    {
        var candidates = new List<(string Text, string[] Names)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string obj in objects)
            foreach (var r in db.Model.Expand(obj))
            {
                var f = r.Def;
                if (r.Depth != 0 || f.Path != f.Name || f.Kind is "Break" or "Marker" || !seen.Add(f.Name)) continue;
                string? xedit = f.XEdit, readable = f.Readable ?? f.XEdit;
                if (r.Sig.Length > 0 && db.PairNames.TryGetValue((r.Sig, r.PlainPath), out var pair)) { xedit = pair.XEditPath; readable = pair.Readable; }
                candidates.Add((FieldText(f.Name, f.Kind, r.Subrecord, readable), FieldNames(f.Name, readable, xedit)));
            }
        return Pick(asked, candidates, c => c.Names, take, always: false).Select(c => c.Text).ToList();
    }

    /// <summary>
    /// The plugins whose file names are nearest to a name that isn't there. Names are held against each other without
    /// their extension, and a short name must be very near: "kgcdom" finds kgcdoom.esm, "nope" finds nothing.
    /// </summary>
    public static List<string> Plugins(string asked, IEnumerable<string> plugins, int take = 4)
    {
        string stem = Path.GetFileNameWithoutExtension(asked.Trim());
        if (stem.Length < 3) return new List<string>();
        int limit = Math.Max(1, Norm(stem).Length / 3);
        return plugins.Select((p, i) => (Name: p, Order: i, Score: Score(stem, Path.GetFileNameWithoutExtension(p))))
            .Where(x => x.Score <= limit).OrderBy(x => x.Score).ThenBy(x => x.Order).Take(take).Select(x => x.Name).ToList();
    }

    // ------------------------------------------------------------------ The choice
    public static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>The words of a name: AttackDamage and attack_damage are "attack", "damage".</summary>
    public static List<string> Words(string s)
    {
        var words = new List<string>();
        var w = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (!char.IsLetterOrDigit(c)) { if (w.Length > 0) { words.Add(w.ToString()); w.Clear(); } continue; }
            bool starts = w.Length > 0 && (char.IsUpper(c) && (!char.IsUpper(s[i - 1]) || i + 1 < s.Length && char.IsLower(s[i + 1])) || char.IsDigit(c) != char.IsDigit(s[i - 1]));
            if (starts) { words.Add(w.ToString()); w.Clear(); }
            w.Append(char.ToLowerInvariant(c));
        }
        if (w.Length > 0) words.Add(w.ToString());
        return words;
    }

    /// <summary>How far a candidate's name is from the name asked: by letters, and by words when every word asked
    /// starts a word of the candidate (CritMult for CritDamageMult).</summary>
    public static int Score(string asked, string candidate)
    {
        int d = Distance(Norm(candidate), Norm(asked));
        if (d <= 1) return d;
        var a = Words(asked); var c = Words(candidate);
        if (a.Count > 0 && a.All(x => x.Length >= 2 && c.Any(y => y.StartsWith(x, StringComparison.Ordinal))))
            d = Math.Min(d, 1 + Math.Max(0, c.Count - a.Count));
        return d;
    }

    /// <summary>0 for the same text; small for one inside the other; otherwise the edit distance.</summary>
    public static int Distance(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length > 3 && b.Length > 3 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))) return 1 + Math.Abs(a.Length - b.Length) / 4;
        var prev = new int[b.Length + 1]; var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>The nearest of <paramref name="names"/> to <paramref name="asked"/>, best first; equally near ones in
    /// the order they were given.</summary>
    /// <param name="always">True: when no name is near, the nearest one is still given. False: when no name is near, the
    /// nearest one is given only if it shares a word with the name asked (AmountProduced for AmountMade); a name that
    /// is nowhere near any real one (zzzz) gets none, not a far-fetched one.</param>
    public static List<T> Pick<T>(string asked, IEnumerable<T> names, Func<T, IEnumerable<string>> texts, int take = 4, bool always = true)
    {
        string key = Norm(asked);
        var scored = new List<(int Score, int Order, T Item)>();
        int order = 0;
        foreach (var n in names)
        {
            int best = int.MaxValue;
            foreach (var t in texts(n)) { if (string.IsNullOrEmpty(t)) continue; best = Math.Min(best, Score(asked, t)); }
            if (best != int.MaxValue) scored.Add((best, order++, n));
        }
        if (scored.Count == 0) return new List<T>();
        scored.Sort((x, y) => x.Score != y.Score ? x.Score.CompareTo(y.Score) : x.Order.CompareTo(y.Order));
        int limit = Math.Max(3, key.Length / 2);
        var good = scored.Where(s => s.Score <= limit).Take(take).Select(s => s.Item).ToList();
        if (good.Count > 0) return good;
        // Nothing near: still name the nearest real one. Where a far-fetched name would only mislead, it must share a
        // word with what was asked.
        if (always) return new List<T> { scored[0].Item };
        var asked3 = Words(asked).Where(w => w.Length >= 3).ToHashSet(StringComparer.Ordinal);
        foreach (var s in scored)
            if (texts(s.Item).Any(t => !string.IsNullOrEmpty(t) && Words(t).Any(asked3.Contains))) return new List<T> { s.Item };
        return new List<T>();
    }
}
