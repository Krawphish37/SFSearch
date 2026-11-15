// How a FormID is stored in a plugin's bytes (GAPS S39, fix F27). Starfield numbers a plugin's own records in one of
// three forms, and the game reads all three as the same record:
//  - the full index: the plugin's place after its Full masters (03000866), as the Creation Kit writes a small .esm;
//  - the Small (light) plugin form: FE + the plugin's place after its Small masters + 3 hex digits (FE001866);
//  - the Medium plugin form: FD + its place after its Medium masters + 4 hex digits (FD010866).
// xEdit and Mutagen write the form the plugin's header gives (FE for Small, FD for Medium). A file edited by both kinds
// of tool can hold both forms, even in one record (12 darkstarpatch_* files; Mutagen's C158/C163).
// check2's "FormID forms" rules count the forms; the raw view says which one a record header uses and what it means.
// The header's FormID is read from the file (4 bytes at 12); where a record's links to its own records sit, and in which
// form, Mutagen says since fixes.16 (GetStoredFormIds, C203; GAPS Part 2 row 35).
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using Mutagen.Bethesda.Plugins.Records;
using SFSearch.Index;

namespace SFSearch.Records;

/// <summary>The three ways a FormID can be stored.</summary>
public enum StoredForm : byte { FullIndex = 0, Small = 1, Medium = 2 }

/// <summary>One stored FormID of a link to the plugin's own record: the subrecord it sits in, where, its bytes and its
/// form (Mutagen's <see cref="StoredFormIdForm"/>; one past the list is told by its top byte, as for the headers).</summary>
public readonly record struct OwnLinkPlace(string Subrecord, int Offset, uint Raw, StoredForm Form);

public static class FormIdForms
{
    public const int Forms = 3;

    /// <summary>The form of a stored FormID, by its top byte.</summary>
    public static StoredForm FormOf(uint raw) => (raw >> 24) switch { 0xFE => StoredForm.Small, 0xFD => StoredForm.Medium, _ => StoredForm.FullIndex };

    /// <summary>True when a stored FormID names the plugin's own record (its master number is at or past the plugin's
    /// masters of that numbering, as Mutagen and the header index read it). <paramref name="atOwnNumber"/>: the number is
    /// exactly the plugin's own (not past it).</summary>
    public static bool IsOwn(uint raw, FormIdResolver ids, out uint localId, out bool atOwnNumber)
    {
        uint top = raw >> 24;
        if (top == 0xFE)
        {
            uint i = (raw >> 12) & 0xFFF; localId = raw & 0xFFF; atOwnNumber = i == (uint)ids.SmallMasters;
            return i >= (uint)ids.SmallMasters;
        }
        if (top == 0xFD)
        {
            uint i = (raw >> 16) & 0xFF; localId = raw & 0xFFFF; atOwnNumber = i == (uint)ids.MediumMasters;
            return i >= (uint)ids.MediumMasters;
        }
        localId = raw & 0xFFFFFF; atOwnNumber = top == (uint)ids.FullMasters;
        return raw != 0 && top >= (uint)ids.FullMasters;
    }

    /// <summary>The form the plugin's header gives its own records: FE for Small, FD for Medium, the full index for Full.</summary>
    public static StoredForm Implied(MasterStyle self) => self switch { MasterStyle.Small => StoredForm.Small, MasterStyle.Medium => StoredForm.Medium, _ => StoredForm.FullIndex };

    /// <summary>The full index is read the same in a plugin of any size; FE only in a Small one, FD only in a Medium one.</summary>
    public static bool Allowed(StoredForm form, MasterStyle self) => form == StoredForm.FullIndex || form == Implied(self);

    /// <summary>The plugin's own records in a form, as a pattern: 03xxxxxx, FE001xxx, FD01xxxx.</summary>
    public static string Pattern(StoredForm form, FormIdResolver ids) => form switch
    {
        StoredForm.Small => $"FE{ids.SmallMasters:X3}xxx",
        StoredForm.Medium => $"FD{ids.MediumMasters:X2}xxxx",
        _ => $"{ids.FullMasters:X2}xxxxxx",
    };

    /// <summary>"the full index", "the Small (light) form", "the Medium form".</summary>
    public static string Name(StoredForm form) => form switch { StoredForm.Small => "the Small (light) form", StoredForm.Medium => "the Medium form", _ => "the full index" };

    /// <summary>
    /// What a stored FormID means, for the raw view: "FE001863: the Small (light) form, own record 0x863",
    /// "0003C2B1: the full index 00, record 0x3C2B1 of its master Starfield.esm". A form the plugin's header doesn't give
    /// is said, and so is an own record named by a number past the plugin's own.
    /// </summary>
    /// <param name="ids">The plugin's resolver, with its names (<see cref="FormIdResolver.For"/>).</param>
    public static string Describe(uint raw, FormIdResolver ids, MasterStyle self)
    {
        if (raw == 0) return "00000000: null";
        uint top = raw >> 24;
        var form = FormOf(raw);
        string how = form switch
        {
            StoredForm.Small => $"the Small (light) form, Small number {(raw >> 12) & 0xFFF:X3}",
            StoredForm.Medium => $"the Medium form, Medium number {(raw >> 16) & 0xFF:X2}",
            _ => $"the full index {top:X2}",
        };
        if (!ids.TryResolve(raw, out string owner, out uint local))
            return $"{raw:X8}: {how}; whose record it is can't be told (a master before it is missing)";
        if (!IsOwn(raw, ids, out _, out bool atOwn))
            return $"{raw:X8}: {how}, record 0x{local:X} of its master {owner}";
        string text = $"{raw:X8}: {how}, own record 0x{local:X}";
        if (!atOwn) text += $" (the number is past the plugin's own, {Pattern(form, ids)}: it reads as the plugin's own record)";
        if (!Allowed(form, self))
            text += $"; the plugin's header ({self}) doesn't give this form: it gives {Pattern(Implied(self), ids)}";
        else if (form != Implied(self))
            text += $", as the Creation Kit writes a {self} plugin; its header gives {Pattern(Implied(self), ids)}, and the game reads both alike";
        return text;
    }

    /// <summary>The FormIDs stored in the headers of the records at these file positions (each record's 4 bytes at 12),
    /// read through one mapping of the file.</summary>
    public static unsafe uint[] HeaderFormIds(string path, IReadOnlyList<long> offsets)
    {
        var ids = new uint[offsets.Count];
        if (offsets.Count == 0) return ids;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = fs.Length;
        if (length == 0) return ids;
        using var mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
        using var view = mmf.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
        byte* p = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        try
        {
            p += view.PointerOffset;
            for (int i = 0; i < ids.Length; i++)
            {
                long at = offsets[i] + 12;
                if (at >= 0 && at + 4 <= length) ids[i] = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(p + at, 4));
            }
        }
        finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
        return ids;
    }

    /// <summary>
    /// Where a record's FormIDs that name the plugin's own records are stored, and in which form: Mutagen's
    /// GetStoredFormIds (C203, fixes.16), which writes the record into a scratch buffer as it would write it back (the
    /// stored forms kept) and notes where each link and FormKey goes, from the record definitions. The header's FormID
    /// isn't one of them. A FormID Mutagen writes but couldn't find in the bytes is <paramref name="notFound"/>. The
    /// places come in the record's order (by its subrecords in <paramref name="raw"/>, then the offset).
    /// </summary>
    public static List<OwnLinkPlace> OwnLinks(LoadedRecord rec, RawRecord raw, out int notFound)
    {
        notFound = 0;
        IReadOnlyList<StoredFormIdPosition> stored;
        // A record read from its own bytes isn't the overlay's: Mutagen needs the masters' sizes, as the session read it
        try { stored = rec.Record.GetStoredFormIds(rec.Session.Mod, rec.Route == ReadRoute.Direct ? rec.Session.MasterFlags : null); }
        catch (Exception e) when (e is not RecordException)
        {
            throw new RecordException($"Mutagen couldn't tell where {rec.Label} stores its FormIDs: {e.GetType().Name}: {e.Message}");
        }
        // Each subrecord's place in the record, by its type and its turn among that type (as Mutagen gives them)
        var order = new Dictionary<(string, int), int>();
        var turns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < raw.Subrecords.Count; i++)
        {
            string sig = raw.Subrecords[i].Signature;
            int turn = turns.GetValueOrDefault(sig);
            turns[sig] = turn + 1;
            order[(sig, turn)] = i;
        }
        var all = new List<(int Sub, OwnLinkPlace Place)>();
        foreach (var f in stored)
        {
            if (f.Location == StoredFormIdLocation.Header || !f.IsOwn) continue;
            if (f.Location == StoredFormIdLocation.NotFound || f.Subrecord is not { } type) { notFound++; continue; }
            var form = f.Form switch { StoredFormIdForm.Small => StoredForm.Small, StoredFormIdForm.Medium => StoredForm.Medium, StoredFormIdForm.Full => StoredForm.FullIndex, _ => FormOf(f.Raw) };
            all.Add((order.GetValueOrDefault((type.Type, f.SubrecordIndex), int.MaxValue), new OwnLinkPlace(type.Type, f.Offset, f.Raw, form)));
        }
        all.Sort((a, b) => a.Sub != b.Sub ? a.Sub.CompareTo(b.Sub) : a.Place.Offset.CompareTo(b.Place.Offset));
        return all.Select(x => x.Place).ToList();
    }
}
