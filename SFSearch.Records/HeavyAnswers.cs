// The three answers at every point where SFSearch stops and asks (J33, step 7 of the J1/J12 plan; the user,
// 2026-11-15: "I say anything that needs permission should ask Yes?/No?/Yes, but save index to speed up future
// queries. All so the agent knows their options and can inform the user." and "Remember, this is an agent first
// tool."). Every "Not run" answer, and every answer that left something out to save time, says the same three things
// the same way, through this one place: run it now (allow_heavy=true, or world: true for refs), leave it, and, where an
// index would make the question fast, run it now and save that index (allow_heavy="save_index", or world: "save_index").
// Where no index would help, the answer says so plainly instead of offering one.
using System.Globalization;
using System.Text.Json.Nodes;

namespace SFSearch.Records;

/// <summary>
/// An index that would make a question fast, offered as the third answer: what it is (today only the world link index,
/// "world"), the plugins it would be made for, its size on disk, how long making it takes after the answer, and how long
/// a later question of the same kind then takes.
/// </summary>
/// <param name="Index">The index's name, as the indexes verb names it ("world").</param>
/// <param name="What">"the world link index of 119 plugins".</param>
/// <param name="Plugins">The plugins it would be made for (what WorldLinks.Want records).</param>
/// <param name="SizeMB">About how big it is on disk.</param>
/// <param name="BuildSeconds">About how long making it takes, in the indexer lane after the answer.</param>
/// <param name="LaterSeconds">About how long a later question of this kind takes with it.</param>
/// <param name="Later">That question and its time in words: "refs into world data then answers in about 0.2 s".</param>
public sealed record IndexOffer(string Index, string What, IReadOnlyList<string> Plugins, long SizeMB, double BuildSeconds, double LaterSeconds, string Later)
{
    /// <summary>The structured form, under a "Not run" answer's notRun (or heavy) object as saveIndex.</summary>
    public JsonObject ToJson(double nowSeconds, string argument) => new()
    {
        ["index"] = Index,
        ["what"] = What,
        ["plugins"] = Plugins.Count,
        ["sizeMB"] = SizeMB,
        ["buildSeconds"] = Math.Round(BuildSeconds, 1),
        ["laterSeconds"] = Math.Round(LaterSeconds, 2),
        ["savesSeconds"] = Math.Round(Math.Max(0, nowSeconds - LaterSeconds), 1),
        ["answer"] = argument,
    };
}

public static class HeavyAnswers
{
    /// <summary>The value of allow_heavy (and of refs' world) that takes the third answer.</summary>
    public const string SaveValue = "save_index";

    /// <summary>True for "save_index" (and "save-index", the command line's spelling).</summary>
    public static bool IsSave(string? value) =>
        value != null && (value.Trim().Equals(SaveValue, StringComparison.OrdinalIgnoreCase) || value.Trim().Equals("save-index", StringComparison.OrdinalIgnoreCase));

    /// <summary>The words that end a "Not run" line of a heavy read: run it now with allow_heavy=true, and either the
    /// third answer (save the index) or why no index would make it faster.</summary>
    public static string NotRunTail(IndexOffer? offer, string? noIndex) =>
        Options(offer, noIndex, "allow_heavy=true", "--allow-heavy", "allow_heavy=\"save_index\"", "--allow-heavy=save-index", "run it now");

    /// <summary>
    /// The answers after a cost, on the same line (an answer's notes and its "Not run" line are one line each): "Call
    /// again with {run} (command line: {runCli}) to {runWhat}." then, with an offer, "Or {save} (...) to {saveWhat} and
    /// then save {offer} (...): {later}. Or leave it: nothing more is read and nothing is saved." and without one "No saved
    /// index would make this faster: {noIndex}."
    /// </summary>
    public static string Options(IndexOffer? offer, string? noIndex, string run, string runCli, string save, string saveCli, string runWhat, string? saveWhat = null)
    {
        string first = $" Call again with {run} (command line: {runCli}) to {runWhat}.";
        if (offer != null)
            return first + " " + SaveLine(offer, save, saveCli, saveWhat ?? runWhat) + " Or leave it: nothing more is read and nothing is saved.";
        return first + (string.IsNullOrWhiteSpace(noIndex) ? "" : $" No saved index would make this faster: {noIndex.TrimEnd('.', ' ')}.");
    }

    /// <summary>"Or allow_heavy="save_index" (command line: --allow-heavy=save-index) to run it now and then save the world
    /// link index of 119 plugins (about 245 MB on disk, made in about 14 s in the background after the answer): refs into
    /// world data then answers in about 0.2 s."</summary>
    public static string SaveLine(IndexOffer offer, string save, string saveCli, string runWhat, string lead = "Or") =>
        $"{lead} {save} (command line: {saveCli}) to {runWhat} and then save {offer.What} (about {Mb(offer.SizeMB)} on disk, made in about {Secs(offer.BuildSeconds)} in the background after the answer): {offer.Later.TrimEnd('.')}.";

    /// <summary>The line of an answer that took the third answer: the index is being made, and how to see and drop it.</summary>
    public static string Saving(IndexOffer offer, bool inService) =>
        $"Saving {offer.What} (about {Mb(offer.SizeMB)}, about {Secs(offer.BuildSeconds)} to make), as asked (save_index): "
        + (inService ? "the service makes it in the background now that this answer is given, at low priority (longer while the PC is busy; indexes shows when it is there), and keeps it up to date when a plugin changes. " : "it is made right after this answer. ")
        + $"{char.ToUpperInvariant(offer.Later[0])}{offer.Later[1..].TrimEnd('.')}. indexes shows it; indexes drop world deletes it (sfs_run).";

    /// <summary>The line of an answer that asked for the third answer where no index would help: it ran as with a plain yes.</summary>
    public static string NothingToSave(string why) =>
        $"save_index was asked, but no saved index would make this question faster ({why.TrimEnd('.', ' ')}), so none is made: it ran as with allow_heavy=true.";

    static string Mb(long mb) => mb < 1 ? "1 MB" : mb >= 1024 ? (mb / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " GB" : mb.ToString("N0", CultureInfo.InvariantCulture) + " MB";

    static string Secs(double s) => s < 1 ? "1 s" : s < 90 ? Math.Ceiling(s).ToString("0", CultureInfo.InvariantCulture) + " s" : (s / 60).ToString("0.#", CultureInfo.InvariantCulture) + " min";
}
