// The one-record verify: a record's JSON, back to a record, written by Mutagen, compared with the file's bytes for
// that record (uncompressed). It proves that the JSON an agent sees is the whole record. No database is involved:
// everything happens in memory, and the only file written is a small test plugin in the scratch folder.
//
// The JSON carries what a byte-exact write needs beside the fields as Mutagen.Bethesda.Serialization's round-trip notes
// (1.38.6-fixes.3/4: the subrecord order C211, a compressed record's original stream C213, the string IDs of translated
// texts C214, a cell's child-group layout C215), so the record is rebuilt from the JSON alone. One thing it still lacks
// is taken from the record as Mutagen read it: a package's data order (WORKAROUNDS W13, until the serializer carries it).
using System.Buffers.Binary;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Strings;
using SFSearch.Index;

namespace SFSearch.Records;

public sealed class VerifyResult
{
    public required RecordRef Ref { get; init; }
    public required string Label { get; init; }
    public required string Signature { get; init; }
    public string Type { get; set; } = "";
    public bool Equal { get; set; }
    /// <summary>A short line that is the same for every record that fails the same way: results are grouped by it.</summary>
    public string? Cause { get; set; }
    /// <summary>The first difference: which subrecord, and both byte strings.</summary>
    public string? Detail { get; set; }
    public string? FileBytes { get; set; }
    public string? WrittenBytes { get; set; }
    /// <summary>Bytes compared: the record header and its uncompressed subrecords.</summary>
    public int Bytes { get; set; }

    public string ToText() => Equal
        ? $"{Label} {Signature}: equal ({Bytes:N0} bytes: the 24-byte header and the subrecords, uncompressed)"
        : $"{Label} {Signature}: NOT equal. {Cause}\n  {Detail}" + (FileBytes != null ? $"\n  file     {FileBytes}\n  written  {WrittenBytes}" : "");

    public JsonObject ToJson() => new()
    {
        ["record"] = Label, ["signature"] = Signature, ["type"] = Type, ["equal"] = Equal, ["bytes"] = Bytes,
        ["cause"] = Cause, ["detail"] = Detail, ["fileBytes"] = FileBytes, ["writtenBytes"] = WrittenBytes,
    };
}

public sealed partial class Verifier
{
    readonly RecordEngine _engine;
    /// <summary>The most records written into one test plugin. Each is still read, turned to JSON, turned back and
    /// compared on its own; only the write is shared.</summary>
    public int BatchSize { get; init; } = 2000;
    /// <summary>The most bytes of records (as stored in the file) written into one test plugin.</summary>
    public long BatchBytes { get; init; } = 4 << 20;
    /// <summary>The most bytes of a differing subrecord that a result quotes from each side.</summary>
    public int MaxBytes { get; init; } = 48;

    public Verifier(RecordEngine engine) { _engine = engine; }

    public VerifyResult Verify(RecordRef record) => Verify(new[] { record })[0];

    /// <summary>Verifies records of one plugin. The plugin's session must be current.</summary>
    public List<VerifyResult> Verify(IReadOnlyList<RecordRef> records)
    {
        var results = new List<VerifyResult>(records.Count);
        var batch = new List<RecordRef>();
        long bytes = 0;
        foreach (var r in records)
        {
            batch.Add(r);
            bytes += _engine.Index.Plugins[r.Plugin].Segment![r.Entry].Size;
            if (batch.Count >= BatchSize || bytes >= BatchBytes) { results.AddRange(Batch(batch)); batch = new List<RecordRef>(); bytes = 0; }
        }
        if (batch.Count > 0) results.AddRange(Batch(batch));
        return results;
    }

    sealed class Item
    {
        public required RecordRef Ref; public required RecordInfo Info; public required VerifyResult Result;
        public IMajorRecord? Rebuilt; public uint RawFormId; public RawRecord? File;
    }

    List<VerifyResult> Batch(List<RecordRef> records)
    {
        var index = _engine.Index;
        var items = new List<Item>(records.Count);
        PluginSession? session = null;
        foreach (var r in records)
        {
            var info = index.Describe(r);
            session ??= _engine.Sessions.Get(info.Plugin);
            if (!session.Name.Equals(info.Plugin, StringComparison.OrdinalIgnoreCase)) throw new RecordException("One verify call takes records of one plugin.");
            var item = new Item { Ref = r, Info = info, Result = new VerifyResult { Ref = r, Label = info.Label, Signature = info.Signature } };
            items.Add(item);
            if (_engine.HeavyNote(r) is { } heavy)
            {
                item.Result.Cause = $"{info.Signature}: a heavy record, not read";
                item.Result.Detail = heavy;
                continue;
            }
            try
            {
                // 1. The record as Mutagen reads it, and its JSON: the form an agent sees
                var loaded = _engine.Load(r);
                item.Result.Type = loaded.Type;
                var json = RecordSplit.WithoutChildren(_engine.Json(loaded));
                // 2. The JSON back to a record: everything a byte-exact write needs comes with the JSON, a package's
                // procedure tree data order too since serializer 1.38.6-fixes.5 (C59 W13; sfsrecord-tests
                // verify-json-notes holds that)
                var rebuilt = (IMajorRecord)RecordSerializer.FromJson(json.ToJsonString(), loaded.Type);
                item.File = _engine.Raw(r);
                item.RawFormId = item.File.FormId;
                item.Rebuilt = rebuilt;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                var inner = e is System.Reflection.TargetInvocationException { InnerException: { } ie } ? ie : e;
                item.Result.Cause = $"{info.Signature}: the JSON couldn't be turned back into a record ({inner.GetType().Name})";
                item.Result.Detail = Short(inner.Message);
            }
        }
        if (session == null) return items.Select(i => i.Result).ToList();
        Write(session, items.Where(i => i.Rebuilt != null).ToList());
        return items.Select(i => i.Result).ToList();
    }

    static string Short(string s) => s.Length <= 300 ? s.ReplaceLineEndings(" ") : s[..300].ReplaceLineEndings(" ") + "…";

    // ------------------------------------------------------------------ Writing and comparing
    void Write(PluginSession session, List<Item> items)
    {
        if (items.Count == 0) return;
        var later = new List<Item>();
        WriteOnce(session, items, later);
        Write(session, later);
    }

    void WriteOnce(PluginSession session, List<Item> items, List<Item> later)
    {
        string folder = Path.Combine(_engine.ScratchFolder, Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, session.Name);
        try
        {
            var mod = new StarfieldMod(session.ModKey, StarfieldRelease.Starfield);
            mod.ModHeader.DeepCopyIn(session.Mod.ModHeader);
            mod.SetOwnFormIdStyle(session.Mod.GetOwnFormIdStyle());
            var placed = Place(mod, session, items, later);
            Directory.CreateDirectory(folder);
            try
            {
                mod.WriteToBinary(path, new BinaryWriteParameters
                {
                    // Master sizes as the header index has them; the header's own master list, kept as it is
                    MasterFlagsLookup = new LoadOrder<IModMasterStyledGetter>(session.MasterStyles),
                    ModKey = ModKeyOption.CorrectToPath,
                    MastersListContent = MastersListContentOption.NoCheck,
                    MastersListOrdering = new MastersListOrderingEnumOption { Option = MastersListOrderingOption.NoCheck },
                    RecordCount = RecordCountOption.NoCheck,
                    NextFormID = NextFormIDOption.NoCheck,
                    FormIDUniqueness = FormIDUniquenessOption.NoCheck,
                    FormIDCompaction = FormIDCompactionOption.NoCheck,
                    // The size flags (and so the own FormIDs' form) as the plugin's header has them. Mutagen 0.55.0-fixes.13's
                    // default (Auto) would raise the size of this new mod when its records don't fit, and give it the
                    // Master flag for an .esm name: the test plugin must be the plugin as it is.
                    HeaderSizeFlags = HeaderSizeFlagsOption.NoCheck,
                    LowerRangeDisallowedHandler = new NoCheckIfLowerRangeDisallowed(),
                    CleanNulls = false,
                    Parallel = new ParallelWriteParameters { MaxDegreeOfParallelism = 1 },
                });
            }
            catch (Exception e) when (e is not OutOfMemoryException && placed.Count > 1)
            {
                // One record Mutagen can't write stops the whole test plugin: halve until it is found
                Cleanup(folder);
                Write(session, placed.Take(placed.Count / 2).ToList());
                Write(session, placed.Skip(placed.Count / 2).ToList());
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                var inner = e.InnerException ?? e;
                placed[0].Result.Cause = $"{placed[0].Info.Signature}: Mutagen couldn't write the rebuilt record ({inner.GetType().Name})";
                placed[0].Result.Detail = Short(inner.Message);
                return;
            }
            byte[] bytes = File.ReadAllBytes(path);
            var written = RecordsOf(bytes);
            foreach (var item in placed) Compare(item, bytes, written);
        }
        finally { Cleanup(folder); }
    }

    static void Cleanup(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Puts the rebuilt records into a new mod so Mutagen can write them. A record with a top-level group of its own
    /// goes there. A record that sits inside another in the file is put inside an empty stand-in of that kind of
    /// record: a record's own bytes don't depend on what holds it, and only its own bytes are compared.
    /// </summary>
    List<Item> Place(StarfieldMod mod, PluginSession session, List<Item> items, List<Item> later)
    {
        var placed = new List<Item>(items.Count);
        var taken = new HashSet<FormKey>();
        // FormKeys for the stand-ins: the plugin's own, counted down from the top, past any record being verified
        var own = new HashSet<uint>(items.Select(i => i.Rebuilt!.FormKey).Where(k => k.ModKey == session.ModKey).Select(k => k.ID));
        uint next = 0xFFFFFE;
        FormKey StandInKey() { while (own.Contains(next)) next--; return new FormKey(session.ModKey, next--); }
        Cell? hostCell = null; Quest? hostQuest = null; DialogTopic? hostTopic = null; CellSubBlock? cells = null;
        CellSubBlock Cells()
        {
            if (cells != null) return cells;
            cells = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
            var block = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
            block.SubBlocks.Add(cells);
            mod.Cells.Records.Add(block);
            return cells;
        }
        Cell HostCell() { if (hostCell == null) { hostCell = new Cell(StandInKey(), StarfieldRelease.Starfield); Cells().Cells.Add(hostCell); } return hostCell; }
        Quest HostQuest() { if (hostQuest == null) { hostQuest = new Quest(StandInKey(), StarfieldRelease.Starfield); mod.Quests.Add(hostQuest); } return hostQuest; }
        DialogTopic HostTopic() { if (hostTopic == null) { hostTopic = new DialogTopic(StandInKey(), StarfieldRelease.Starfield); HostQuest().DialogTopics.Add(hostTopic); } return hostTopic; }

        foreach (var item in items)
        {
            var rec = item.Rebuilt!;
            // A plugin can hold a FormKey twice, a test plugin once: the second copy goes into a test plugin of its own
            if (!taken.Add(rec.FormKey)) { later.Add(item); continue; }
            try
            {
                switch (rec)
                {
                    case Cell c: Cells().Cells.Add(c); break;
                    case NavigationMesh n: HostCell().NavigationMeshes.Add(n); break;
                    case IPlaced p: HostCell().Temporary.Add(p); break;
                    case DialogResponses d: HostTopic().Responses.Add(d); break;
                    case DialogTopic t: HostQuest().DialogTopics.Add(t); break;
                    case Scene s: HostQuest().Scenes.Add(s); break;
                    case DialogBranch b when item.Info.Parent != null: HostQuest().DialogBranches.Add(b); break;
                    default:
                        var group = mod.TryGetTopLevelGroup(rec.GetType()) ?? throw new RecordException($"Mutagen has no top-level group for a {rec.GetType().Name}.");
                        group.AddUntyped(rec);
                        break;
                }
                placed.Add(item);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                item.Result.Cause = $"{item.Info.Signature}: the rebuilt record couldn't be put into a test plugin ({e.GetType().Name})";
                item.Result.Detail = Short(e.Message);
            }
        }
        return placed;
    }

    /// <summary>Every record of a written plugin, by signature and FormID as written.</summary>
    static Dictionary<(string, uint), (int Start, int Length)> RecordsOf(byte[] file)
    {
        var found = new Dictionary<(string, uint), (int, int)>();
        int pos = 24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        while (pos + 24 <= file.Length)
        {
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos + 4));
            if (sig == 0x50555247) { pos += 24; continue; } // GRUP: step into it
            string s = System.Text.Encoding.ASCII.GetString(file, pos, 4);
            found.TryAdd((s, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos + 12))), (pos, 24 + size));
            pos += 24 + size;
        }
        return found;
    }

    /// <summary>The record as written against the record in the file: the header (the size aside when either is
    /// compressed), then subrecord by subrecord, uncompressed. The first difference is reported.</summary>
    void Compare(Item item, byte[] file, Dictionary<(string, uint), (int Start, int Length)> written)
    {
        var res = item.Result; var orig = item.File!; string sig = item.Info.Signature;
        if (!written.TryGetValue((sig, item.RawFormId), out var at))
        {
            var other = written.Keys.Where(k => k.Item1 == sig).Take(3).Select(k => k.Item2.ToString("X8")).ToList();
            res.Cause = $"{sig}: the written plugin has no {sig} with the file's FormID";
            res.Detail = $"The file writes the FormID as {item.RawFormId:X8}. {sig} records written: {(other.Count == 0 ? "none" : string.Join(", ", other))}.";
            return;
        }
        var wr = RawRecord.FromBytes(file.AsSpan(at.Start, at.Length));
        res.Bytes = RawRecord.HeaderLength + orig.Data.Length;
        if (orig.Flags != wr.Flags) { Header("flags", orig.Flags.ToString("X8"), wr.Flags.ToString("X8")); return; }
        if (orig.VersionControl != wr.VersionControl) { Header("version control", orig.VersionControl.ToString("X8"), wr.VersionControl.ToString("X8")); return; }
        if (orig.FormVersion != wr.FormVersion) { Header("form version", orig.FormVersion.ToString(), wr.FormVersion.ToString()); return; }
        if (orig.Version2 != wr.Version2) { Header("second version number", orig.Version2.ToString(), wr.Version2.ToString()); return; }

        var a = orig.Subrecords; var b = wr.Subrecords;
        for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            if (i >= b.Count || i >= a.Count || a[i].Signature != b[i].Signature)
            {
                // Which kind of difference: a subrecord missing, one too many, or the same subrecords in another order
                var fileSigs = a.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.Count());
                var writtenSigs = b.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.Count());
                string? missing = fileSigs.Keys.FirstOrDefault(k => writtenSigs.GetValueOrDefault(k) < fileSigs[k]);
                string? extra = writtenSigs.Keys.FirstOrDefault(k => fileSigs.GetValueOrDefault(k) < writtenSigs[k]);
                res.Cause = missing != null ? $"{sig}/{missing}: in the file, missing from the written record"
                    : extra != null ? $"{sig}/{extra}: written, but not in the file"
                    : $"{sig}: the same subrecords in another order";
                res.Detail = $"Subrecord {i + 1}: the file has {(i < a.Count ? a[i].Signature : "nothing more")}, the written record has {(i < b.Count ? b[i].Signature : "nothing more")}. File: {a.Count} subrecords, written: {b.Count}.";
                if (missing != null) { var m = a.First(s => s.Signature == missing); res.FileBytes = $"{missing} ({m.Length:N0} bytes) {RawRecord.Hex(orig.Content(m), MaxBytes)}"; res.WrittenBytes = "(none)"; }
                else if (extra != null) { var m = b.First(s => s.Signature == extra); res.FileBytes = "(none)"; res.WrittenBytes = $"{extra} ({m.Length:N0} bytes) {RawRecord.Hex(wr.Content(m), MaxBytes)}"; }
                else { res.FileBytes = string.Join(' ', a.Select(s => s.Signature).Take(40)); res.WrittenBytes = string.Join(' ', b.Select(s => s.Signature).Take(40)); }
                return;
            }
            var x = orig.Content(a[i]); var y = wr.Content(b[i]);
            if (x.SequenceEqual(y)) continue;
            int at2 = 0; while (at2 < x.Length && at2 < y.Length && x[at2] == y[at2]) at2++;
            int from = Math.Max(0, at2 - 8);
            res.Cause = $"{sig}/{a[i].Signature}: bytes differ" + (x.Length != y.Length ? " (another length)" : "");
            res.Detail = $"Subrecord {i + 1} ({a[i].Signature}): {x.Length:N0} bytes in the file, {y.Length:N0} as written; the first difference is at byte {at2}. Shown from byte {from}.";
            res.FileBytes = RawRecord.Hex(x[Math.Min(from, x.Length)..], MaxBytes);
            res.WrittenBytes = RawRecord.Hex(y[Math.Min(from, y.Length)..], MaxBytes);
            return;
        }
        if (orig.Trailing != wr.Trailing) { res.Cause = $"{sig}: bytes after the last subrecord differ"; res.Detail = $"{orig.Trailing} in the file, {wr.Trailing} as written."; return; }
        res.Equal = true;

        void Header(string what, string fileValue, string writtenValue)
        {
            res.Cause = $"{sig}: the record header differs ({what})";
            res.Detail = $"The record header's {what}: {fileValue} in the file, {writtenValue} as written.";
            res.FileBytes = RawRecord.Hex(orig.Header); res.WrittenBytes = RawRecord.Hex(wr.Header);
        }
    }
}
