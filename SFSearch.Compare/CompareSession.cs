// What every comparison stands on: the header index and the record engine over the Data folder, and one more index
// and engine for each plugin file that sits outside Data (an older copy, a .bak, a patch being built elsewhere).
// A file outside Data is read with its masters taken from Data. Nothing is ever written beside a plugin: a file
// whose name isn't a plugin's name (x.esm.bak), or that is read under another name on purpose, is copied into the
// scratch folder first.

using System.Text.Json.Nodes;
using Mutagen.Bethesda.Strings;
using SFSearch.Index;
using SFSearch.Records;

namespace SFSearch.Compare;

/// <summary>Something about a comparison that the caller must be told as one line: <c>Error: what. Fix: fix.</c></summary>
public sealed class CompareException(string what, string? fix = null) : Exception(what)
{
    public string? Fix { get; } = fix;
    /// <summary>The line of the output contract.</summary>
    public string Line => "Error: " + Message.TrimEnd('.') + "." + (Fix == null ? "" : " Fix: " + Fix.TrimEnd('.') + ".");
}

/// <summary>One plugin file taking part in a comparison.</summary>
public sealed class PluginSide
{
    /// <summary>The name the file is read under: the name its own records' FormKeys carry.</summary>
    public required string Name { get; init; }
    /// <summary>The file as it was named by the caller.</summary>
    public required string Given { get; init; }
    /// <summary>The file itself.</summary>
    public required string Path { get; init; }
    /// <summary>The file Mutagen reads: the file itself, or its copy in the scratch folder.</summary>
    public required string ReadPath { get; init; }
    /// <summary>True when the file is the plugin of that name in the Data folder.</summary>
    public required bool InData { get; init; }
    public required HeaderIndex Index { get; init; }
    public required RecordEngine Engine { get; init; }
    public IndexedPlugin Plugin => Index.Plugin(Name) ?? throw new CompareException($"{Name} is no longer among the indexed plugins", "run the command again");
    public Segment Segment => Plugin.Segment ?? throw new CompareException($"{Shown} can't be read: {Plugin.Problems.ReadError ?? "it has no header index"}");
    public IReadOnlyList<string> Masters => Plugin.Header?.Masters ?? Array.Empty<string>();
    /// <summary>The name for people: the file name, and where it is when it isn't the one in Data.</summary>
    public string Shown => InData ? Name
        : string.Equals(System.IO.Path.GetFileName(Path), Name, StringComparison.OrdinalIgnoreCase) ? $"{Name} ({System.IO.Path.GetDirectoryName(Path)})"
        : $"{System.IO.Path.GetFileName(Path)} read as {Name} ({System.IO.Path.GetDirectoryName(Path)})";
    public bool HasMaster(string name) => Masters.Any(m => m.Equals(name, StringComparison.OrdinalIgnoreCase));
    public override string ToString() => Shown;
}

/// <summary>One record of one plugin, read and turned into the one record form.</summary>
public sealed class RecordView
{
    public required RecordRef Ref { get; init; }
    public required RecordInfo Info { get; init; }
    /// <summary>The Mutagen type: Weapon, WeaponModification.</summary>
    public required string Type { get; init; }
    public required JsonObject Json { get; init; }
    public required RecordEngine Engine { get; init; }
    public string Plugin => Info.Plugin;
    public string Label => Info.Label;
    /// <summary>Set when this is a partial form merged over the version before it (CompareSession.Merge): says so,
    /// as "SFBGS00D.esm's partial form merged over Starfield.esm". Null for a record as it is written.</summary>
    public string? Merged { get; init; }
    /// <summary>The plugins whose versions a merged record was made from, below this one.</summary>
    public IReadOnlyList<string> Through { get; init; } = Array.Empty<string>();
}

public sealed partial class CompareSession : IDisposable
{
    public string DataFolder { get; }
    public string ScratchFolder { get; }
    public RecordSchema Schema { get; }
    public HeaderIndex Index { get; }
    public RecordEngine Engine { get; }
    public RecordDiffer Differ { get; }
    public Language Language { get; }
    public string VersionText { get; }
    /// <summary>What the first look at the Data folder took.</summary>
    public RefreshReport FirstRefresh { get; }

    /// <summary>
    /// A program that already has a header index and a record engine over the Data folder (SFSearch itself) lends them
    /// here, so a comparison or a report doesn't make a second copy: given the Data folder and the language, it
    /// returns its own, or null to let the session make one. The session never closes what it was lent. The scratch
    /// folder and the version text should then be the host's too, so a file outside Data shares its masters' segments.
    /// (Added with milestone M8. It is a static hook so that SFSearch.Reports' session, which makes this one, needs no change.)
    /// </summary>
    public static Func<string, Language, (HeaderIndex Index, RecordEngine Engine)?>? Host { get; set; }

    /// <summary>
    /// How the host words what the header index knows is wrong with a plugin: one note for the plugin, the same in
    /// every tool of the host (SFSearch's ProblemNotes), or null when the host has nothing to say. Given the index
    /// that holds the plugin (null when the caller doesn't have it), the plugin, and the name to show it by. With no
    /// host the library words the problems itself, one note each.
    /// </summary>
    public static Func<HeaderIndex?, IndexedPlugin, string, string?>? ProblemNote { get; set; }

    /// <summary>
    /// How the host turns a path a caller gave into a full path: SFSearch reads a relative path from the caller's own
    /// folder, which isn't the folder its service's worker runs in. With no host: from the current folder.
    /// </summary>
    public static Func<string, string>? UserPath { get; set; }

    /// <summary>What the header index knows is wrong with a plugin, as notes: in the host's one wording when there is a host.</summary>
    public static IEnumerable<string> ProblemNotes(HeaderIndex? index, IndexedPlugin p, string shownAs)
    {
        if (!p.Problems.Any) yield break;
        if (ProblemNote != null)
        {
            if (ProblemNote(index, p, shownAs) is { } note) yield return note;
            yield break;
        }
        foreach (var line in p.Problems.Lines()) yield return $"{shownAs}: {line}{(line.EndsWith('?') ? "" : ".")}";
    }

    readonly Dictionary<string, PluginSide> _sides = new(StringComparer.OrdinalIgnoreCase);
    readonly List<IDisposable> _open = new();

    /// <param name="scratchFolder">Where index segments and copies go. Never the Data folder or a folder inside it.</param>
    public CompareSession(string dataFolder, string scratchFolder, RecordSchema schema, string? versionText = null, Language language = Language.English)
    {
        DataFolder = Path.GetFullPath(dataFolder); ScratchFolder = Path.GetFullPath(scratchFolder); Schema = schema; Language = language;
        VersionText = versionText ?? $"sfscompare / mutagen {schema.MutagenVersion} / schema {schema.Version}";
        if (Host?.Invoke(DataFolder, language) is { } lent)
        {
            // The host's own index and engine: one copy of each in the program. They are the host's to close.
            Index = lent.Index; Engine = lent.Engine;
            FirstRefresh = Index.Refresh();
        }
        else
        {
            Index = new HeaderIndex(PluginSource.Folder(DataFolder), new IndexOptions { ScratchFolder = Path.Combine(ScratchFolder, "index"), VersionText = VersionText, StringFiles = new StringFiles(DataFolder, Path.Combine(ScratchFolder, "index")), Refusals = new MutagenRefusals() });
            _open.Add(Index);
            FirstRefresh = Index.Refresh();
            Engine = new RecordEngine(Index, schema, Path.Combine(ScratchFolder, "verify"), DataFolder, language);
            _open.Add(Engine);
        }
        Differ = new RecordDiffer(schema);
    }

    public void Dispose()
    {
        for (int i = _open.Count - 1; i >= 0; i--) _open[i].Dispose();
        _open.Clear(); _sides.Clear();
    }

    // ------------------------------------------------------------------ Plugins
    /// <summary>The name a file is read under when nothing says otherwise: its own, cut after .esm, .esp or .esl
    /// (darkstar.esm.bak is darkstar.esm). Null when the name holds no plugin name.</summary>
    public static string? PluginNameOf(string fileName)
    {
        int best = -1;
        foreach (var ext in new[] { ".esm", ".esp", ".esl" })
        {
            int at = fileName.LastIndexOf(ext, StringComparison.OrdinalIgnoreCase);
            if (at > 0 && (at + 4 == fileName.Length || fileName[at + 4] == '.') && at > best) best = at;
        }
        return best < 0 ? null : fileName[..(best + 4)];
    }

    static bool IsPluginName(string name) =>
        name.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".esl", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A plugin by its name in Data, or a file by its path. A file outside Data gets a header index of its own, with
    /// its masters taken from Data (or, for a master that isn't in Data, from beside the file).
    /// </summary>
    /// <param name="readAs">Read the file under this plugin name on purpose (its own records then carry that name).</param>
    public PluginSide Side(string nameOrPath, string? readAs = null)
    {
        string text = nameOrPath.Trim().Trim('"');
        if (text.Length == 0) throw new CompareException("no plugin was named", "give a plugin's file name in Data, or the path of a plugin file");
        bool looksLikePath = text.IndexOfAny(new[] { '\\', '/' }) >= 0 || Path.IsPathRooted(text);
        var inData = looksLikePath ? null : Index.Plugin(text);
        string full;
        if (inData != null) full = inData.Path;
        else
        {
            full = UserPath?.Invoke(text) ?? Path.GetFullPath(text);
            if (!File.Exists(full))
                throw new CompareException(looksLikePath ? $"there is no file {full}" : $"there is no plugin named {text} in the Data folder, and no file of that name in the folder the call was made from",
                    "give a plugin's file name in Data, or the full path of a plugin file" + Nearest(text));
            // A path that is the plugin in Data is that plugin
            if (Index.Plugin(Path.GetFileName(full)) is { } same && string.Equals(Path.GetFullPath(same.Path), full, StringComparison.OrdinalIgnoreCase)) inData = same;
        }
        if (inData != null && (readAs == null || readAs.Equals(inData.Name, StringComparison.OrdinalIgnoreCase)))
            return new PluginSide { Name = inData.Name, Given = nameOrPath, Path = inData.Path, ReadPath = inData.Path, InData = true, Index = Index, Engine = Engine };

        string fileName = Path.GetFileName(full);
        string name = readAs ?? PluginNameOf(fileName)
            ?? throw new CompareException($"{fileName} doesn't have a plugin's name (.esm, .esp or .esl), so its own records have no name to go by", "add as=\"<the plugin's name>\"");
        if (!IsPluginName(name)) throw new CompareException($"\"{name}\" isn't a plugin's name", "a plugin's name ends in .esm, .esp or .esl");
        string sideKey = full + "|" + name;
        if (_sides.TryGetValue(sideKey, out var have)) { have.Index.Refresh(); return have; }

        // A file that isn't read under its own file name is copied: nothing is renamed or written where plugins are
        string readPath = full;
        if (!fileName.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            var info = new FileInfo(full);
            string folder = Path.Combine(ScratchFolder, "copies", $"{(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(full):x8}-{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}");
            Directory.CreateDirectory(folder);
            readPath = Path.Combine(folder, name);
            if (!File.Exists(readPath) || new FileInfo(readPath).Length != info.Length)
            {
                using var from = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var to = new FileStream(readPath, FileMode.Create, FileAccess.Write, FileShare.None);
                from.CopyTo(to);
            }
        }

        // The file and its masters, each master from Data, or from beside the file when Data has none of that name
        var files = new List<string> { readPath };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
        var queue = new Queue<string>();
        foreach (var m in PluginHeader.Read(readPath)?.Masters ?? Array.Empty<string>()) queue.Enqueue(m);
        while (queue.Count > 0)
        {
            string m = queue.Dequeue();
            if (!seen.Add(m)) continue;
            string? path = Index.Plugin(m)?.Path;
            if (path == null)
            {
                string beside = Path.Combine(Path.GetDirectoryName(full)!, m);
                if (File.Exists(beside)) path = beside;
            }
            if (path == null) continue; // missing: the record engine refuses the plugin with a line that names the master
            files.Add(path);
            foreach (var mm in PluginHeader.Read(path)?.Masters ?? Array.Empty<string>()) queue.Enqueue(mm);
        }
        var index = new HeaderIndex(PluginSource.FromFiles(files), new IndexOptions { ScratchFolder = Path.Combine(ScratchFolder, "index"), VersionText = VersionText, PluginsTxt = "", Ccc = "", StringFiles = new StringFiles(DataFolder, Path.Combine(ScratchFolder, "index")), Refusals = new MutagenRefusals() });
        _open.Add(index);
        index.Refresh();
        if (index.Plugin(name) is not { Header: not null })
            throw new CompareException($"{full} isn't a plugin: it doesn't start with a plugin header (TES4)", "give a plugin file");
        var engine = new RecordEngine(index, Schema, Path.Combine(ScratchFolder, "verify"), DataFolder, Language);
        _open.Add(engine);
        return _sides[sideKey] = new PluginSide { Name = name, Given = nameOrPath, Path = full, ReadPath = readPath, InData = false, Index = index, Engine = engine };
    }

    string Nearest(string text)
    {
        // The nearest plugins in Data, chosen and worded as every tool does it
        var near = SFSearch.SchemaGen.NearNames.Plugins(text, Index.Plugins.Select(p => p.Name));
        return near.Count == 0 ? "" : ". In Data, " + SFSearch.SchemaGen.NearNames.Fix(near);
    }

    /// <summary>The engine a plugin is read through, seen from one side: the Data folder's own engine for every
    /// plugin that is the file in Data (so a master is opened once), the side's engine for the rest.</summary>
    public RecordEngine EngineFor(PluginSide side, string plugin)
    {
        if (side.InData) return Engine;
        if (plugin.Equals(side.Name, StringComparison.OrdinalIgnoreCase)) return side.Engine;
        var mine = side.Index.Plugin(plugin); var data = Index.Plugin(plugin);
        return mine != null && data != null && string.Equals(mine.Path, data.Path, StringComparison.OrdinalIgnoreCase) ? Engine : side.Engine;
    }

    /// <summary>
    /// Before a comparison starts: the stamps of each plugin it names, and of that plugin's masters, are taken again
    /// and held against the header index. A plugin that changed is closed, so the next read opens the file as it is
    /// now. Masters are not opened here: a comparison opens a master when it reads a record from it. The Data
    /// folder's own engine is always checked, since masters of a file outside Data are read through it.
    /// </summary>
    public List<(RecordEngine Engine, SessionUse Use)> Use(params PluginSide[] sides)
    {
        var uses = new List<(RecordEngine, SessionUse)>();
        var byEngine = new Dictionary<RecordEngine, List<string>> { [Engine] = new() };
        foreach (var s in sides) Add(s.Engine, s.Name);
        foreach (var (engine, names) in byEngine)
        {
            try { uses.Add((engine, engine.Sessions.Use(names.ToArray()))); }
            catch (RecordException e) { throw new CompareException(e.Message); }
        }
        return uses;

        void Add(RecordEngine e, string n)
        {
            var l = byEngine.TryGetValue(e, out var have) ? have : byEngine[e] = new List<string>();
            if (!l.Contains(n, StringComparer.OrdinalIgnoreCase)) l.Add(n);
        }
    }

    /// <summary>At the end of a comparison: a plugin that changed while it was read makes the answer fail.</summary>
    public void Confirm(List<(RecordEngine Engine, SessionUse Use)> uses, CompareAnswer answer)
    {
        foreach (var (engine, use) in uses)
        {
            try { engine.Sessions.Confirm(use); }
            catch (RecordException e) { throw new CompareException(e.Message); }
            foreach (var n in use.Notes) if (!answer.Notes.Contains(n)) answer.Notes.Add(n);
        }
    }

    // ------------------------------------------------------------------ Records
    /// <summary>The entry of a FormKey in one plugin of an index, or null.</summary>
    public static RecordRef? Find(HeaderIndex index, string plugin, string owner, uint localId)
    {
        var p = index.Plugin(plugin);
        var seg = p?.Segment;
        if (seg == null) return null;
        int o = seg.OwnerOf(owner);
        if (o < 0) return null;
        int at = seg.Find(o, localId);
        return at < 0 ? null : new RecordRef(p!.Id, at);
    }

    public static RecordRef? Find(HeaderIndex index, string plugin, string formKey) =>
        HeaderIndex.TryParseFormKey(formKey, out var owner, out var id) ? Find(index, plugin, owner, id) : null;

    /// <summary>Reads one record and gives it in the one record form.</summary>
    public RecordView View(RecordEngine engine, RecordRef r)
    {
        try
        {
            var rec = engine.Load(r);
            return new RecordView { Ref = r, Info = rec.Info, Type = rec.Type, Json = engine.Json(rec), Engine = engine };
        }
        catch (RecordException e) { throw new CompareException(e.Message); }
    }

    /// <summary>A record of a side named by FormKey, local ID or EditorID. A name that fits several records lists them and picks none.</summary>
    public RecordRef FindIn(PluginSide side, string record)
    {
        try { return side.Engine.Find(side.Name, record); }
        catch (RecordException e) { throw new CompareException(e.Message); }
    }

    /// <summary>The record's bytes as the file holds them (header and data), for telling two copies apart without reading them.</summary>
    public static byte[] RawBytes(RecordEngine engine, RecordRef r)
    {
        var p = engine.Index.Plugins[r.Plugin]; var e = p.Segment![r.Entry];
        var bytes = new byte[RawRecord.HeaderLength + e.Size];
        var file = engine.Sessions.Get(p.Name).File;
        int done = 0;
        while (done < bytes.Length)
        {
            int n = RandomAccess.Read(file, bytes.AsSpan(done), e.Offset + done);
            if (n <= 0) throw new CompareException($"{p.Name} ends inside the record at 0x{e.Offset:X}", "the plugin may have changed: run the command again");
            done += n;
        }
        return bytes;
    }

    /// <summary>True when two plugins write FormIDs the same way: the same masters in the same order with the same
    /// sizes, and the same name and size themselves. Then equal bytes are equal records.</summary>
    public static bool SameFormIdSpace(PluginSide a, PluginSide b)
    {
        var pa = a.Plugin; var pb = b.Plugin;
        if (pa.Header == null || pb.Header == null || !a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase) || pa.Header.Style != pb.Header.Style) return false;
        return pa.Header.Masters.SequenceEqual(pb.Header.Masters, StringComparer.OrdinalIgnoreCase) && pa.MasterStyles.SequenceEqual(pb.MasterStyles);
    }

    // ------------------------------------------------------------------ Names
    /// <summary>EditorID (FormID:plugin) for a FormKey, looked up in the sides first (the later one first), then in Data.</summary>
    public Func<string, string> Labeller(params PluginSide[] sides)
    {
        var outside = sides.Where(s => !s.InData).Reverse().ToList();
        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return key =>
        {
            if (cache.TryGetValue(key, out var have)) return have;
            string? label = null;
            if (outside.Count > 0 && HeaderIndex.TryParseFormKey(key, out var plugin, out _))
                foreach (var s in outside)
                {
                    if (!plugin.Equals(s.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    string l = s.Engine.Label(key);
                    if (!l.StartsWith("(no such record)", StringComparison.Ordinal)) { label = l; break; }
                    label ??= l;
                }
            return cache[key] = label ?? Engine.Label(key);
        };
    }

    // ------------------------------------------------------------------ Provenance
    /// <summary>One line of the answer's source: which copy of which plugin was read.</summary>
    public static string SourceLine(PluginSide side) => SourceLine(side.Plugin, side.InData ? null : side.Path);

    public static string SourceLine(IndexedPlugin p, string? path = null) =>
        $"{p.Name} {Size(p.Stamp.Length)}, key {p.ContentKey.Short}" + (path == null ? "" : $", read from {path}");

    static string Size(long bytes) => Sizes.Text(bytes);

    /// <summary>The load order the Data folder's index holds, as one line.</summary>
    public string LoadOrderLine()
    {
        int enabled = Index.Plugins.Count(p => p.Order.Enabled);
        string txt = LoadOrder.DefaultPluginsTxt;
        string when = File.Exists(txt) ? File.GetLastWriteTime(txt).ToString("yyyy-MM-dd HH:mm") : "(no plugins.txt)";
        return $"load order of {when} ({enabled} plugins loaded, {Index.Plugins.Count} in Data)";
    }
}
