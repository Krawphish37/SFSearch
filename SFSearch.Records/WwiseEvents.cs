// The names of Wwise events by their GUID (GAPS S191).
//
// A sound reference holds an event's GUID. Wwise's own description of each sound bank is shipped beside the bank:
// sound\soundbanks\<bank id>.json, and the game's soundbanksinfo.json for all of its banks (Starfield - WwiseSounds05.ba2
// and WwiseSoundsPatch.ba2). Each lists the bank's events: {"Name": "Play_WPN_Pistol_Eon_Equip_Up",
// "ObjectPath": "\\Events\\Starfield_WPN\\WPN_Hand\\Pistol\\Eon\\Play_WPN_Pistol_Eon_Equip_Up",
// "GUID": "{59A5EC16-836F-4D41-A2D7-FC1B91F934D7}"}: the name and path xEdit shows beside the GUID.
// They are read here from every archive of the Data folder that has such a file (32 of 457 on the folder this was
// written on) and from the loose files, once for a process; the list is kept in the scratch folder for the next one,
// under a key of the archives' names, sizes and dates.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace SFSearch.Records;

public sealed class WwiseEvents
{
    /// <summary>An event: its name, its path in the Wwise project and the bank that holds it.</summary>
    public readonly record struct Event(string Name, string Path, string Bank);

    readonly Dictionary<Guid, Event> _events = new();
    /// <summary>The files the events were read from ("Starfield - WwiseSoundsPatch.ba2", or a loose file's name).</summary>
    public List<string> Sources { get; } = new();
    public int Count => _events.Count;
    /// <summary>How long reading took; 0 when the list came from the scratch folder.</summary>
    public double ReadSeconds { get; private set; }

    static readonly ConcurrentDictionary<string, Lazy<WwiseEvents>> ByFolder = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Data folder whose events the views that have no engine at hand ask (a card's text, a query's
    /// functions): the one of the record engine made last. Null: no names.</summary>
    public static string? DefaultFolder { get; set; }
    /// <summary>Where the list is kept between processes; null: nowhere.</summary>
    public static string? CacheFolder { get; set; }

    /// <summary>The events of the Data folder's archives and loose files, read at the first ask.</summary>
    public static WwiseEvents For(string dataFolder) =>
        ByFolder.GetOrAdd(Path.GetFullPath(dataFolder), f => new Lazy<WwiseEvents>(() => Load(f), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>The events of <see cref="DefaultFolder"/>; null when there is none.</summary>
    public static WwiseEvents? Default => DefaultFolder is { } f && Directory.Exists(f) ? For(f) : null;

    /// <summary>The event with a GUID given in Wwise's form (as the files write it, with or without braces).</summary>
    public bool TryGet(string? wwiseGuid, out Event found)
    {
        found = default;
        return wwiseGuid != null && Guid.TryParse(wwiseGuid, out var g) && _events.TryGetValue(g, out found);
    }

    static WwiseEvents Load(string folder)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var archives = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.ba2").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
        string looseFolder = Path.Combine(folder, "sound", "soundbanks");
        var loose = Directory.Exists(looseFolder) ? Directory.EnumerateFiles(looseFolder, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
        string key = Key(archives.Concat(loose));
        if (FromCache(folder, key) is { } kept) return kept;

        var events = new WwiseEvents();
        // Loose files first: the game reads a loose file in an archive's place
        foreach (string path in loose)
            try { if (events.Add(File.ReadAllBytes(path)) > 0) events.Sources.Add(Path.GetFileName(path)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        // Then the archives, the game's patch archive before its base ones (a later description of the same bank)
        foreach (string path in archives.OrderByDescending(p => Path.GetFileName(p).Contains("Patch", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var reader = Archive.CreateReader(GameRelease.Starfield, path);
                int added = 0;
                foreach (var f in reader.Files)
                {
                    string p = f.Path.Replace('/', '\\');
                    if (!p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !p.StartsWith("sound\\soundbanks\\", StringComparison.OrdinalIgnoreCase)) continue;
                    try { added += events.Add(f.GetBytes()); }
                    catch (Exception e) when (e is JsonException or InvalidDataException) { }
                }
                if (added > 0) events.Sources.Add(Path.GetFileName(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException) { }
        }
        events.ReadSeconds = clock.Elapsed.TotalSeconds;
        ToCache(folder, key, events);
        return events;
    }

    /// <summary>Adds the events a sound bank description lists (every "IncludedEvents" in it); gives how many were new.</summary>
    int Add(ReadOnlyMemory<byte> json)
    {
        using var doc = JsonDocument.Parse(json);
        int added = 0;
        Walk(doc.RootElement, "", ref added);
        return added;
    }

    void Walk(JsonElement e, string bank, ref int added)
    {
        if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) Walk(x, bank, ref added); return; }
        if (e.ValueKind != JsonValueKind.Object) return;
        // A bank: its short name, for the events it includes
        if (e.TryGetProperty("IncludedEvents", out _) && e.TryGetProperty("ShortName", out var sn) && sn.ValueKind == JsonValueKind.String) bank = sn.GetString() ?? bank;
        foreach (var p in e.EnumerateObject())
        {
            if (p.NameEquals("IncludedEvents") && p.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var ev in p.Value.EnumerateArray())
                {
                    if (ev.ValueKind != JsonValueKind.Object || !ev.TryGetProperty("GUID", out var g) || !Guid.TryParse(g.GetString(), out var guid)) continue;
                    string name = ev.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                    string path = ev.TryGetProperty("ObjectPath", out var o) ? o.GetString() ?? "" : "";
                    if (name.Length > 0 && _events.TryAdd(guid, new Event(name, path, bank))) added++;
                }
            }
            else if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && !p.NameEquals("ReferencedStreamedFiles") && !p.NameEquals("StreamedFiles")) Walk(p.Value, bank, ref added);
        }
    }

    // ------------------------------------------------------------------ the list kept in the scratch folder
    static string Key(IEnumerable<string> files)
    {
        var sb = new StringBuilder("wwise-events 1\n");
        foreach (string path in files)
        {
            var fi = new FileInfo(path);
            sb.Append(fi.Name.ToLowerInvariant()).Append('|').Append(fi.Exists ? fi.Length : -1).Append('|').Append(fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0).Append('\n');
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())), 0, 12);
    }

    static string? CacheFile(string folder) => CacheFolder == null ? null
        : Path.Combine(CacheFolder, "wwise-events-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(folder.ToLowerInvariant())), 0, 6) + ".tsv");

    static WwiseEvents? FromCache(string folder, string key)
    {
        try
        {
            if (CacheFile(folder) is not { } file || !File.Exists(file)) return null;
            using var r = new StreamReader(file, Encoding.UTF8);
            if (r.ReadLine() != key) return null;
            var events = new WwiseEvents();
            if (r.ReadLine() is { Length: > 0 } sources) events.Sources.AddRange(sources.Split('\t'));
            while (r.ReadLine() is { } line)
            {
                var c = line.Split('\t');
                if (c.Length == 4 && Guid.TryParseExact(c[0], "N", out var g)) events._events[g] = new Event(c[1], c[2], c[3]);
            }
            return events;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static void ToCache(string folder, string key, WwiseEvents events)
    {
        try
        {
            if (CacheFile(folder) is not { } file) return;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            using (var w = new StreamWriter(temp, false, new UTF8Encoding(false)))
            {
                w.Write(key); w.Write('\n');
                w.Write(string.Join('\t', events.Sources)); w.Write('\n');
                foreach (var (g, e) in events._events)
                {
                    if (e.Name.Contains('\t') || e.Path.Contains('\t') || e.Bank.Contains('\t') || e.Name.Contains('\n') || e.Path.Contains('\n')) continue;
                    w.Write(g.ToString("N")); w.Write('\t'); w.Write(e.Name); w.Write('\t'); w.Write(e.Path); w.Write('\t'); w.Write(e.Bank); w.Write('\n');
                }
            }
            File.Move(temp, file, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
