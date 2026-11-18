// Why the value walk leaves a record to Mutagen, counted by the tests that hold the byte reads against Mutagen's
// object (lists-all, typed-all): the byte readers say it with each record they leave (RawList.WalkLeft), and the count
// is kept here, in the tests. Saved as lists-all-why.txt and typed-all-why.txt: where to look first when moving more
// reads to the bytes.
using System.Collections.Concurrent;
using SFSearch.Query;

namespace SFSearch.Query.Tests;

static class LeftReasons
{
    static ConcurrentDictionary<(string What, string Why), int>? s_counts;

    /// <summary>Starts a new count.</summary>
    public static void Start() => s_counts = new ConcurrentDictionary<(string, string), int>();

    /// <summary>One record left to Mutagen for a list view or a typed column; nothing when the walk gave no reason
    /// (another reader left it) or no count is running.</summary>
    public static void Note(string what, RawList.WalkLeft left)
    {
        if (s_counts is not { } counts) return;
        string? why = left.Unread != null ? $"unread {left.Unread}: {left.Why}"
            : left.Column != null ? (left.NoReader ? "no reader for column " : "no value for column ") + left.Column + (left.Kind is { Length: > 0 } ? " of a " + left.Kind : "")
            : null;
        if (why != null) counts.AddOrUpdate((what, why), 1, (_, n) => n + 1);
    }

    /// <summary>Ends the count: its lines (what, how many records, why), by what and then the most first.</summary>
    public static List<string> Stop()
    {
        var counts = s_counts ?? new ConcurrentDictionary<(string, string), int>();
        s_counts = null;
        return counts.OrderBy(k => k.Key.What, StringComparer.Ordinal).ThenByDescending(k => k.Value).Select(k => $"{k.Key.What}\t{k.Value}\t{k.Key.Why}").ToList();
    }
}
