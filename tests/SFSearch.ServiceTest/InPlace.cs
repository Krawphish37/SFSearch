namespace SFSearch.ServiceTest;

public static partial class T
{
    /// <summary>
    /// A plugin copy changed in place, keeping its size and its date (fix list C16): one EditorID in the middle of the
    /// file is overwritten with another of the same length, and the file's date is put back. The quick stamp (size, date,
    /// the first and last 64 KB) can't see that; the header index's content key can, once the index hashes the file again
    /// (its sweep, `__index sweep`). query2's tables follow the same content key, so after the sweep query2 must see the
    /// new name, and after the file is put back (and swept again) the old one. With no service. (Until 2.0 this asked
    /// the older Query command, removed with the older commands.)
    /// The copy must be in a test Data folder (SFSEARCH_DATA): the game's Data folder is never written.
    /// </summary>
    static async Task InPlace(string? copy)
    {
        if (copy == null || !File.Exists(copy)) { Console.WriteLine("inplace needs --copy <a plugin copy in a test Data folder>"); _fail++; return; }
        copy = Path.GetFullPath(copy);
        string? data = Environment.GetEnvironmentVariable("SFSEARCH_DATA");
        if (copy.Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase) || data == null || !string.Equals(Path.GetDirectoryName(copy), Path.GetFullPath(data).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        { Console.WriteLine("Refused: the copy must be in the test Data folder that SFSEARCH_DATA names, never in the game's."); _fail++; return; }
        string plugin = Path.GetFileName(copy);
        byte[] original = File.ReadAllBytes(copy);
        var date = File.GetLastWriteTimeUtc(copy);
        // An EditorID subrecord in the middle of the file (away from the first and last 64 KB the quick stamp hashes),
        // whose text is nowhere else in the file: its last letter is changed, so the copy keeps its size
        string Old = "", New = "";
        int at = -1;
        byte[] edid = "EDID"u8.ToArray();
        for (int i = original.AsSpan(70000).IndexOf(edid) + 70000; i > 0 && i < original.Length - 70000; i = original.AsSpan(i + 4).IndexOf(edid) is int n && n >= 0 ? i + 4 + n : -1)
        {
            int len = original[i + 4] | original[i + 5] << 8;
            if (len < 13 || len > 80 || original[i + 6 + len - 1] != 0) continue;
            string text = System.Text.Encoding.ASCII.GetString(original, i + 6, len - 1);
            if (!text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || !char.IsAsciiLetter(text[^1])) continue;
            byte[] tb = System.Text.Encoding.ASCII.GetBytes(text);
            if (original.AsSpan().IndexOf(tb) != i + 6 || original.AsSpan(i + 7).IndexOf(tb) >= 0) continue;
            string other = text[..^1] + (text[^1] == 'Q' ? 'R' : 'Q');
            if (original.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(other)) >= 0) continue;
            (Old, New, at) = (text, other, i + 6);
            break;
        }
        Check("inplace: an EditorID in the middle of the file, away from the 64 KB at each end the quick stamp hashes", at > 0, at > 0 ? $"{Old} at byte {at:N0} of {original.Length:N0}, to become {New}" : "none found");
        if (at < 0) return;

        // Forms lists every record of the plugin, whatever its type
        string sql = $"SELECT EditorID FROM Forms WHERE EditorID IN ('{Old}', '{New}');";
        async Task<string> Ask(string step)
        {
            var (o, e, code) = await RunExe(Exe, ["query2", plugin, sql]);
            string got = o.Contains(New) ? New : o.Contains(Old) ? Old : $"(neither: exit {code} {Cut((o + e).Trim())})";
            Say($"{step}: query2 reads {got}");
            return got;
        }
        async Task Sweep() { var (o, _, _) = await RunExe(Exe, ["__index", "sweep"]); Say("  " + Cut(o.Trim())); }

        try
        {
            Check("inplace: the copy as it is", await Ask("1. as it is") == Old);
            var changed = (byte[])original.Clone();
            changed[at + Old.Length - 1] = (byte)New[^1];
            File.WriteAllBytes(copy, changed);
            File.SetLastWriteTimeUtc(copy, date);
            Check("inplace: the changed copy has the size and date of the old one", new FileInfo(copy).Length == original.Length && File.GetLastWriteTimeUtc(copy) == date);
            await Sweep();
            Check("inplace: after the header index's sweep, query2 sees the change", await Ask("2. changed in place, swept") == New);
            File.WriteAllBytes(copy, original);
            File.SetLastWriteTimeUtc(copy, date);
            await Sweep();
            Check("inplace: put back and swept, query2 reads the old name again", await Ask("3. put back, swept") == Old);
        }
        finally
        {
            // Never leave the copy changed
            if (!File.ReadAllBytes(copy).AsSpan().SequenceEqual(original)) File.WriteAllBytes(copy, original);
            File.SetLastWriteTimeUtc(copy, date);
        }
    }
}
