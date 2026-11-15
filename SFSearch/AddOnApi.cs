using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Strings;
using SFSearch.Compare;
using SFSearch.Engine;
using SFSearch.Index;
using SFSearch.Query;
using SFSearch.Records;
using SFSearch.Reports;

namespace SFSearch.AddOns
{
    // =====================================================================================================================
    // The add-on API (ARCHITECTURE.md section 8, "Extending it without a rebuild"). An add-on is handed the engine: find a
    // record, read it as JSON or as Mutagen's own object, run SQL in a scope, ask for links, winners and comparisons, and
    // print under the output contract. It stands on the header index, the record layer, SFSearch.Query, SFSearch.Compare
    // and SFSearch.Reports only (the older commands, their tables and helpers were removed in 2.0). Nothing in it knows
    // about any one mod.
    // =====================================================================================================================

    /// <summary>
    /// The game's plugins as SFSearch's engine sees them: the Data folder, the load order, each plugin's header, any record
    /// (by FormKey or EditorID), a plugin with its masters as one view, SQL, comparisons and reports.
    /// Calls are for one command at a time, like the engine's own tools.
    /// </summary>
    public static class GameData
    {
        static EngineIndex Engine()
        {
            var eng = EngineIndex.Get();
            if (!_checked.TryGetValue(eng, out _)) { eng.Check(); _checked.Add(eng, new object()); }
            return eng;
        }
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EngineIndex, object> _checked = new();

        /// <summary>The Data folder in use (SFSEARCH_DATA, the remembered folder, or the game's).</summary>
        public static string Folder => Engine().DataFolder;

        /// <summary>SFSearch's home folder (its settings, scratch and extensions).</summary>
        public static string Home => SFSearch.Home.Dir;

        /// <summary>A path the user typed, read from the folder the user was in (not the service worker's).</summary>
        public static string UserPath(string path) => SFSearch.Home.UserPath(path);

        /// <summary>Brings the header index in line with the files on disk. Done once per command by itself; call it again after a file changed.</summary>
        public static void Refresh() => Engine().Check();

        /// <summary>The plugins the game loads (enabled in the load order and present in Data), in load order.</summary>
        public static List<string> LoadOrder()
        {
            var ix = Engine().Index;
            return ix.Plugins.Where(p => p.Order.Enabled && p.Segment != null).OrderBy(p => EngineIndex.Rank(p)).Select(p => p.Name).ToList();
        }

        /// <summary>Every plugin in Data, loaded or not, in load order (plugins that aren't in the load order last, by name).</summary>
        public static List<string> AllPlugins()
        {
            var ix = Engine().Index;
            return ix.Plugins.Where(p => p.Segment != null).OrderBy(p => EngineIndex.Rank(p)).Select(p => p.Name).ToList();
        }

        /// <summary>A plugin by its file name in Data or by a path (a file outside Data is read with its masters from Data). Null when there is none.</summary>
        public static PluginInfo? Plugin(string nameOrPath)
        {
            var found = Find(nameOrPath);
            return found == null ? null : new PluginInfo(found.Value.Ix, found.Value.Plugin, found.Value.ByPath);
        }

        internal static (HeaderIndex Ix, IndexedPlugin Plugin, bool ByPath)? Find(string nameOrPath)
        {
            var eng = Engine();
            string text = nameOrPath.Trim().Trim('"');
            if (text.Contains('\\') || text.Contains('/'))
            {
                string full = SFSearch.Home.UserPath(text);
                if (Path.GetDirectoryName(full) is { } dir && string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(eng.DataFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    text = Path.GetFileName(full);
                else
                {
                    var side = eng.ByPath(full);
                    return side == null ? null : (side.Value.Ix, side.Value.Plugin, true);
                }
            }
            var p = eng.Index.Plugin(text);
            return p?.Segment == null ? null : (eng.Index, p, false);
        }

        /// <summary>A plugin and its masters as one view: its own records, and any record as this plugin sees it.</summary>
        public static PluginView Open(string nameOrPath, IEnumerable<string>? extraPlugins = null, Language language = Language.English)
        {
            var found = Find(nameOrPath) ?? throw new AddOnException($"There is no plugin {nameOrPath} in Data ({Folder}).", "check the name; a file outside Data is named by its path");
            return new PluginView(Engine(), found.Ix, found.Plugin, found.ByPath, extraPlugins ?? Array.Empty<string>(), language);
        }

        /// <summary>
        /// Every plugin the game loads, opened through the record layer, in load order: Mutagen's mod objects and a link
        /// cache over all of them (the version the game uses wins). Dispose it when the command ends.
        /// </summary>
        public static LoadOrderView OpenLoadOrder(Language language = Language.English) => new(Engine(), LoadOrder(), language);

        /// <summary>The EditorID of a FormKey ("0003C2:darkstar.esm"): the record's own plugin's, else the winner's. Null when no plugin holds it or it has none.</summary>
        public static string? EditorId(string formKey) => Header(formKey)?.EditorId;

        /// <summary>The header index's line for a record: the version in its own plugin, else the first holder. Null when nobody holds it.</summary>
        public static RecordInfo? Header(string formKey)
        {
            var ix = Engine().Index;
            var v = ix.Versions(FormKeys.Normalize(formKey));
            if (v == null || v.Holders.Count == 0) return null;
            return ix.Describe(v.Holders[0]);
        }

        /// <summary>The 4-letter signature of a record ("WEAP"), or null when no plugin holds it.</summary>
        public static string? Signature(string formKey) => Header(formKey)?.Signature;

        /// <summary>Every plugin that holds a record, in load order, and which of them wins (-1: none of them is loaded).</summary>
        public static (List<string> Holders, int Winner) Versions(string formKey)
        {
            var ix = Engine().Index;
            var v = ix.Versions(FormKeys.Normalize(formKey));
            if (v == null) return (new List<string>(), -1);
            return (v.Holders.Select(h => ix.Plugins[h.Plugin].Name).ToList(), v.Winner);
        }

        /// <summary>Every record with this EditorID, letter case aside: (plugin, FormKey, signature).</summary>
        public static List<(string Plugin, string FormKey, string Signature)> FindEditorId(string editorId)
        {
            var ix = Engine().Index;
            return ix.FindEditorId(editorId).Select(r => ix.Describe(r)).Select(d => (d.Plugin, d.FormKey, d.Signature)).ToList();
        }

        /// <summary>Reads one record through the record layer: as the plugin holds it (its own record or its override).</summary>
        public static AddOnRecord? Read(string plugin, string formKey, Language language = Language.English)
        {
            using var view = Open(plugin, null, language);
            var r = view.Own(formKey);
            r?.LoadJson();
            return r;
        }

        /// <summary>The Mutagen type name of a signature ("OMOD" has several; the first is given). Record types by name or signature are accepted.</summary>
        public static string? TypeOf(string signatureOrType)
        {
            var s = EngineRecords.Schema;
            if (s.SignatureOf(signatureOrType) != null) return signatureOrType;
            var t = s.TypesOf(signatureOrType.ToUpperInvariant());
            return t.Count > 0 ? t[0] : s.Signatures.SelectMany(x => s.TypesOf(x)).FirstOrDefault(n => n.Equals(signatureOrType, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The signature of a Mutagen type name ("Weapon" → "WEAP"), or the signature itself.</summary>
        public static string? SignatureOf(string typeOrSignature)
        {
            var s = EngineRecords.Schema;
            if (s.TypesOf(typeOrSignature.ToUpperInvariant()).Count > 0) return typeOrSignature.ToUpperInvariant();
            string? type = TypeOf(typeOrSignature);
            return type == null ? null : s.SignatureOf(type);
        }

        // ---------------------------------------------------------------- SQL
        /// <summary>
        /// Runs SQL through SFSearch.Query (the typed views, Records, Forms, Links). Scope: "plugins" (the plugins named),
        /// "winners" (the versions the game uses) or "versions" (every copy). Every row is returned (no pages).
        /// An error or a heavy read that wasn't allowed is an <see cref="AddOnException"/>.
        /// </summary>
        public static QueryRows Query(string sql, IEnumerable<string>? plugins = null, string scope = "plugins", bool allowHeavy = false, Language language = Language.English)
        {
            var eng = Engine();
            var qe = EngineLibs.Query(eng, eng.Index, language);
            var names = plugins?.ToList() ?? new List<string>();
            var kind = scope.ToLowerInvariant() switch { "winners" => ScopeKind.Winners, "versions" => ScopeKind.Versions, _ => names.Count == 0 ? ScopeKind.Winners : ScopeKind.Plugins };
            var rows = new QueryRows();
            string? cursor = null;
            do
            {
                var a = qe.Run(new QueryRequest { Sql = sql, Plugins = names, Scope = kind, AllowHeavy = allowHeavy, PageRows = 100000, MaxChars = int.MaxValue, Cursor = cursor, Language = language.ToString() });
                if (a.Error != null) throw new AddOnException(a.Error, a.Fix ?? "check the SQL");
                if (a.NotRun != null) throw new AddOnException($"Not run: {a.NotRun.What}: about {a.NotRun.Seconds:0} s and {a.NotRun.MemoryMB:N0} MB", "pass allowHeavy: true to run it anyway");
                if (rows.Columns.Count == 0) rows.Columns.AddRange(a.Columns);
                rows.Rows.AddRange(a.Rows);
                rows.Notes.AddRange(a.Notes.Where(n => !rows.Notes.Contains(n)));
                cursor = a.Cursor;
            } while (cursor != null);
            return rows;
        }

        // ---------------------------------------------------------------- Comparisons and reports
        /// <summary>A comparison session on the engine's index (SFSearch.Compare). Dispose it when the command ends.</summary>
        public static CompareSession CompareSession(Language language = Language.English) => EngineLibs.Compare(Engine(), language);

        /// <summary>A report session on the engine's index (SFSearch.Reports). Dispose it when the command ends.</summary>
        public static ReportSession Reports(Language language = Language.English) => EngineLibs.Reports(Engine(), language);

        /// <summary>
        /// The files inside a plugin's own archives, read the way <c>archive2 &lt;plugin&gt; list</c> reads them (SFSearch.Reports'
        /// archive reader, Mutagen's archive reader under it; nothing is stored). A plugin's own archives are the ones in Data
        /// named "&lt;plugin name&gt; - &lt;part&gt;.ba2", the names the game loads with it; a plugin named by a path outside Data
        /// gets the ones in Data named for it, or the ones beside the file with <paramref name="beside"/>. A .ba2 (a name in Data
        /// or a path) or "all" (every archive in Data) is taken as well. Each archive lists every file: its path inside the
        /// archive, its size (null for a texture: the archive's table doesn't hold it) and whether Data also holds it as a loose
        /// file (the game then uses the loose one). No loose file is tied to a plugin: the game loads loose files for every plugin.
        /// No archive is no error: <see cref="ArchiveContents.Archives"/> is empty and a note says why.
        /// </summary>
        public static ArchiveContents Archives(string pluginOrArchive, bool beside = false)
        {
            using var session = Reports();
            try { return new ArchiveReport(session).Contents(pluginOrArchive, beside); }
            catch (ReportException e) { throw new AddOnException(e.Message, e.Fix ?? "give a plugin's name, a .ba2 file, or all"); }
        }

        /// <summary>
        /// A record's conditions as SFSearch.Reports reads them from the JSON (defaults filled in from the schema): the
        /// function, its parameters (links as FormKeys), what it runs on, the operator, the value or global, the flags.
        /// Parameters are labelled with their FormKey; label them yourself as the answer needs.
        /// </summary>
        public static List<ConditionInfo> Conditions(JsonNode? conditions)
        {
            if (conditions is not JsonArray { Count: > 0 }) return new List<ConditionInfo>();
            using var session = Reports();
            return new ConditionReader(session, fk => fk).Read(conditions);
        }

        /// <summary>
        /// What a plugin changes in its masters (SFSearch.Compare's plugin_vs_masters): each override against the version
        /// before it, with the record's own fields that differ. An ITM is an override with no field changed.
        /// </summary>
        public static List<OverrideChange> OverrideChanges(string plugin)
        {
            using var session = CompareSession();
            var answer = new Comparer(session).PluginVsMasters(plugin);
            return answer.Overrides.Select(r => new OverrideChange(r.FormKey, r.Label, r.Signature, r.Type, r.Class.ToString(), r.Base,
                r.Diff?.All.Where(f => f.Class == DiffClass.Value).Select(f => f.Top).Distinct(StringComparer.Ordinal).ToList() ?? new List<string>())).ToList();
        }
    }

    /// <summary>The plugins the game loads as Mutagen mod objects (overlays held by the record layer), in load order.</summary>
    public sealed class LoadOrderView : IDisposable
    {
        readonly RecordEngine _records;
        SessionUse? _use;
        /// <summary>The mods in load order.</summary>
        public List<IStarfieldModGetter> Mods { get; }
        /// <summary>A link cache over every loaded plugin: the last one that holds a record wins.</summary>
        public ILinkCache<IStarfieldMod, IStarfieldModGetter> LinkCache { get; }
        /// <summary>Notes for the answer (a plugin that changed on disk while it was read).</summary>
        public List<string> Notes { get; } = new();

        internal LoadOrderView(EngineIndex eng, List<string> plugins, Language language)
        {
            _records = EngineRecords.For(eng, eng.Index, language);
            try { _use = _records.Sessions.Use(plugins.ToArray()); }
            catch (RecordException e) { throw new AddOnException(e.Message, "check the load order: every master must be in Data"); }
            Notes.AddRange(_use.Notes);
            Mods = plugins.Select(p => (IStarfieldModGetter)_records.Sessions.Get(p).Mod).ToList();
            LinkCache = Mods.ToImmutableLinkCache<IStarfieldMod, IStarfieldModGetter>();
        }

        public void Dispose()
        {
            if (_use == null) return;
            try { _records.Sessions.Confirm(_use); }
            catch (RecordException e) { Console.Error.WriteLine("[sfs] " + e.Message); }
            _use = null;
        }
    }

    /// <summary>One override of a plugin against the version before it in its masters: Class is Changed, Itm, AnotherType, OfNothing or NotRead; Fields the record's own fields that differ (values only).</summary>
    public sealed record OverrideChange(string FormKey, string Label, string Signature, string? Type, string Class, string? Base, List<string> Fields)
    {
        public bool IsItm => Class == "Itm";
    }

    /// <summary>Every row of a query: column names and values (a number is a long or a double, text a string, NULL null).</summary>
    public sealed class QueryRows
    {
        public List<string> Columns { get; } = new();
        public List<object?[]> Rows { get; } = new();
        public List<string> Notes { get; } = new();
        public int Column(string name) => Columns.FindIndex(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An add-on's error under the output contract: printed as "Error: … Fix: …".</summary>
    public sealed class AddOnException(string what, string fix) : Exception(what)
    {
        public string Fix { get; } = fix;
        public string Line => $"Error: {Message.TrimEnd('.')}. Fix: {Fix.TrimEnd('.')}.";
    }

    /// <summary>A plugin's header as the header index read it.</summary>
    public sealed class PluginInfo
    {
        readonly HeaderIndex _ix;
        readonly IndexedPlugin _p;
        internal PluginInfo(HeaderIndex ix, IndexedPlugin p, bool byPath) { _ix = ix; _p = p; ByPath = byPath; }

        /// <summary>The plugin's file name.</summary>
        public string Name => _p.Name;
        /// <summary>The full path of the file.</summary>
        public string Path => _p.Path;
        /// <summary>True for a file named by its path outside Data.</summary>
        public bool ByPath { get; }
        /// <summary>True when the game loads it (enabled in the load order).</summary>
        public bool Loaded => _p.Order.Enabled;
        /// <summary>The file's size in bytes.</summary>
        public long Size => _p.Segment?.PluginLength ?? new FileInfo(_p.Path).Length;
        /// <summary>The masters in the header's order.</summary>
        public List<string> Masters => _p.Header?.Masters.ToList() ?? new List<string>();
        /// <summary>"Full", "Medium" or "Small" (Light), from the header flags.</summary>
        public string Style => EngineIndex.SizeName(_p.Header?.Style ?? MasterStyle.Full);
        /// <summary>The header flags as a number (0x1 master, 0x100 small, 0x400 medium).</summary>
        public uint Flags => _p.Header?.Flags ?? 0;
        /// <summary>Every record in the file (its own and its overrides).</summary>
        public int RecordCount => _p.Segment?.Count ?? 0;
        /// <summary>Records that are overrides of a master's record.</summary>
        public int OverrideCount
        {
            get
            {
                var seg = _p.Segment; if (seg == null) return 0;
                int n = 0, self = seg.SelfOwner;
                foreach (var e in seg.Entries) if (e.Owner != self) n++;
                return n;
            }
        }
        /// <summary>The highest local ID of the plugin's own records (what decides whether it fits Small or Medium).</summary>
        public uint MaxLocalId
        {
            get
            {
                var seg = _p.Segment; if (seg == null) return 0;
                uint max = 0; int self = seg.SelfOwner;
                foreach (var e in seg.Entries) if (e.Owner == self && e.LocalId > max) max = e.LocalId;
                return max;
            }
        }
        /// <summary>Records and bytes of each signature in the file.</summary>
        public IReadOnlyList<SignatureStats> Signatures => _ix.Stats(_p.Name);
        /// <summary>What the header index found wrong with the plugin (missing masters and the like), one line each.</summary>
        public List<string> Problems => _p.Problems.Lines().ToList();
    }

    /// <summary>
    /// A plugin with its masters (and any extra plugins) as one view, the way the plugin sees records: its own record or
    /// override first, then its masters (the last master first), then the extra plugins. Records are read live through
    /// the record layer and kept for the command.
    /// </summary>
    public sealed class PluginView : IDisposable
    {
        readonly EngineIndex _eng;
        readonly HeaderIndex _ix;
        readonly IndexedPlugin _plugin;
        readonly RecordEngine _records;
        readonly List<SessionUse> _uses = new();
        readonly Dictionary<string, AddOnRecord?> _cache = new(StringComparer.OrdinalIgnoreCase);
        readonly List<string> _chain = new();

        /// <summary>The plugin's file name.</summary>
        public string Target => _plugin.Name;
        /// <summary>True when the plugin was named by a path outside Data.</summary>
        public bool ByPath { get; }
        /// <summary>The plugins records are looked up in, in order: the plugin, its masters from the last, the extra plugins.</summary>
        public IReadOnlyList<string> Chain => _chain;
        /// <summary>The plugin's masters in the header's order.</summary>
        public List<string> Masters => _plugin.Header?.Masters.ToList() ?? new List<string>();
        /// <summary>Notes for the answer: a plugin of the chain that couldn't be read, a file that changed while it was read.</summary>
        public List<string> Notes { get; } = new();
        /// <summary>The header of the plugin.</summary>
        public PluginInfo Info => new(_ix, _plugin, ByPath);

        internal PluginView(EngineIndex eng, HeaderIndex ix, IndexedPlugin plugin, bool byPath, IEnumerable<string> extra, Language language)
        {
            _eng = eng; _ix = ix; _plugin = plugin; ByPath = byPath;
            _records = EngineRecords.For(eng, ix, language);
            _chain.Add(plugin.Name);
            foreach (var p in Enumerable.Reverse(Masters).Concat(extra))
            {
                if (_chain.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
                if (_ix.Plugin(p)?.Segment == null) { Notes.Add($"{p} isn't in Data: records from it can't be read."); continue; }
                _chain.Add(p);
            }
            try { _uses.Add(_records.Sessions.Use(_chain.ToArray())); }
            catch (RecordException e) { throw new AddOnException(e.Message, "check that every master of the plugin is in Data"); }
            foreach (var u in _uses) Notes.AddRange(u.Notes);
        }

        public void Dispose()
        {
            foreach (var u in _uses)
            {
                try { _records.Sessions.Confirm(u); }
                catch (RecordException e) { Console.Error.WriteLine("[sfs] " + e.Message); }
            }
            _uses.Clear();
        }

        /// <summary>The Mutagen mod object of a plugin of the chain (an overlay, read on demand), for work the record views don't cover.</summary>
        public IStarfieldModGetter Mod(string plugin) => _records.Sessions.Get(plugin).Mod;

        /// <summary>A Mutagen link cache over the chain: the extra plugins lowest, then the masters in the header's order, then the plugin (it wins).</summary>
        public ILinkCache<IStarfieldMod, IStarfieldModGetter> LinkCache => _linkCache ??=
            Enumerable.Reverse(_chain).Select(Mod).ToList().ToImmutableLinkCache<IStarfieldMod, IStarfieldModGetter>();
        ILinkCache<IStarfieldMod, IStarfieldModGetter>? _linkCache;

        /// <summary>The plugin's own records (new records and overrides) of these types, in FormKey order. Types are Mutagen names ("Weapon") or signatures ("WEAP").
        /// Each record is read on first use of its <see cref="AddOnRecord.Json"/> or <see cref="AddOnRecord.Record"/>.</summary>
        public List<AddOnRecord> Records(params string[] types) => Records(false, types);

        /// <summary>The same, in the order the records sit in the file when <paramref name="fileOrder"/> is true.</summary>
        public List<AddOnRecord> Records(bool fileOrder, params string[] types)
        {
            var list = new List<AddOnRecord>();
            var sigs = types.Select(t => GameData.SignatureOf(t) ?? throw new AddOnException($"There is no record type {t}", "name a Mutagen record type (Weapon) or a signature (WEAP)")).Distinct().ToList();
            var wanted = new HashSet<string>(types.Select(t => GameData.TypeOf(t) ?? t), StringComparer.OrdinalIgnoreCase);
            foreach (var sig in sigs)
                foreach (var r in _ix.RecordsOfType(_plugin.Name, sig))
                {
                    var rec = Make(r);
                    // A signature with several types (OMOD): keep the types asked for. Asking by signature keeps them all.
                    if (EngineRecords.Schema.TypesOf(sig).Count > 1 && !types.Any(t => t.Equals(sig, StringComparison.OrdinalIgnoreCase)) && !wanted.Contains(rec.Type)) continue;
                    list.Add(rec);
                    _cache.TryAdd(rec.FormKey, rec);
                }
            return fileOrder ? list.OrderBy(r => r.Offset).ToList() : list.OrderBy(r => r.FormKey, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Every record of the plugin itself, in FormKey order (types Mutagen doesn't read are left out).</summary>
        public List<AddOnRecord> AllRecords()
        {
            var sigs = _ix.Stats(_plugin.Name).Select(s => s.Signature).Where(s => EngineRecords.Schema.TypesOf(s).Count > 0).ToArray();
            return sigs.Length == 0 ? new List<AddOnRecord>() : Records(sigs);
        }

        /// <summary>A record as this plugin sees it (its own version first, then its masters from the last, then the extra plugins). Null when none of them holds it.</summary>
        public AddOnRecord? Get(string? formKey)
        {
            if (string.IsNullOrEmpty(formKey) || formKey.IndexOf(':') < 0) return null;
            string fk = FormKeys.Normalize(formKey);
            if (_cache.TryGetValue(fk, out var hit)) return hit;
            AddOnRecord? found = null;
            foreach (var p in _chain)
                if (Ref(p, fk) is { } r) { found = Make(r); break; }
            _cache[fk] = found;
            return found;
        }

        /// <summary>The plugin's own version of a record (its record or its override), or null.</summary>
        public AddOnRecord? Own(string formKey) => Ref(_plugin.Name, FormKeys.Normalize(formKey)) is { } r ? Make(r) : null;

        /// <summary>The version of a record before this plugin: the last of its masters (header order) that holds it. Null for the plugin's own records.</summary>
        public AddOnRecord? Previous(string formKey)
        {
            string fk = FormKeys.Normalize(formKey);
            foreach (var m in Enumerable.Reverse(Masters))
                if (Ref(m, fk) is { } r) return Make(r);
            return null;
        }

        /// <summary>A record of another plugin of the chain, as that plugin holds it.</summary>
        public AddOnRecord? In(string plugin, string formKey) => _chain.Contains(plugin, StringComparer.OrdinalIgnoreCase) && Ref(plugin, FormKeys.Normalize(formKey)) is { } r ? Make(r) : null;

        /// <summary>The EditorID of a FormKey as the plugin sees it, else from the header index; "" when unknown.</summary>
        public string Edid(string? formKey)
        {
            if (string.IsNullOrEmpty(formKey) || formKey.IndexOf(':') < 0) return "";
            var r = Get(formKey);
            if (r != null && r.EditorID.Length > 0) return r.EditorID;
            return GameData.EditorId(formKey) ?? "";
        }

        /// <summary>"ma_Armor_Spacesuit (001234:MyMod)", or the short FormKey when the EditorID is unknown; "none" for nothing.</summary>
        public string Show(string? formKey)
        {
            if (string.IsNullOrEmpty(formKey)) return "none";
            if (formKey.IndexOf(':') < 0) return formKey;
            string e = Edid(formKey);
            return e.Length > 0 ? $"{e} ({FormKeys.Short(formKey)})" : FormKeys.Short(formKey);
        }

        RecordRef? Ref(string plugin, string formKey)
        {
            var p = _ix.Plugin(plugin);
            var seg = p?.Segment;
            if (seg == null || !HeaderIndex.TryParseFormKey(formKey, out var owner, out var id)) return null;
            int o = seg.OwnerOf(owner);
            if (o < 0) return null;
            int at = seg.Find(o, id);
            return at < 0 ? null : new RecordRef(p!.Id, at);
        }

        AddOnRecord Make(RecordRef r)
        {
            var info = _ix.Describe(r);
            return new AddOnRecord(this, r, info);
        }

        internal LoadedRecord Load(RecordRef r, RecordInfo info) => _records.Load(r, info);
        internal JsonObject Json(LoadedRecord rec) => _records.Json(rec);
        internal string TypeFor(string sig) => _records.TypeFor(sig);
    }

    /// <summary>
    /// One record of a plugin, read live: the header index's line (FormKey, signature, EditorID, plugin, override or not),
    /// Mutagen's own object (<see cref="Record"/>) and the record layer's JSON (<see cref="Json"/>: Mutagen's field names,
    /// Mutagen.Bethesda.Serialization's full profile, defaults left out, nested records as {"$Child": FormKey} markers).
    /// </summary>
    public sealed class AddOnRecord
    {
        readonly PluginView _view;
        readonly RecordRef _ref;
        readonly RecordInfo _info;
        LoadedRecord? _loaded;
        JsonObject? _json;

        internal AddOnRecord(PluginView view, RecordRef r, RecordInfo info) { _view = view; _ref = r; _info = info; }

        /// <summary>"0003C2:darkstar.esm".</summary>
        public string FormKey => _info.FormKey;
        /// <summary>The 4-letter signature ("WEAP").</summary>
        public string Signature => _info.Signature;
        /// <summary>Mutagen's type name ("Weapon", "WeaponModification"); for a signature with several types the record is read to tell.</summary>
        public string Type => _type ??= EngineRecords.Schema.TypesOf(_info.Signature).Count > 1 ? Loaded.Type : _view.TypeFor(_info.Signature);
        string? _type;
        /// <summary>The EditorID, or "".</summary>
        public string EditorID => _info.EditorId ?? "";
        /// <summary>The plugin that holds this version.</summary>
        public string Plugin => _info.Plugin;
        /// <summary>True when this is an override of a master's record.</summary>
        public bool IsOverride => _info.IsOverride;
        /// <summary>Where the record sits in its file (bytes from the start).</summary>
        public long Offset => _info.Offset;
        /// <summary>The record's flags from its header (0x4000 partial form, 0x20 deleted, …).</summary>
        public uint Flags => _info.Flags;
        /// <summary>Mutagen's own object, read through the record layer.</summary>
        public IMajorRecordGetter Record => Loaded.Record;
        /// <summary>The record layer's JSON of the record (fields at their default left out). The record header's
        /// FormVersion is always there; a byte field is "0x…", with no bytes "0x" (read it with <see cref="JsonText.Bytes"/>).</summary>
        public JsonObject Json => _json ??= _view.Json(Loaded);
        /// <summary>The same JSON with every field the schema says is always there spelled out at its default, each object's fields in the schema's order.</summary>
        public JsonObject JsonWithDefaults => _full ??= JsonText.WithDefaults(Json, Type);
        JsonObject? _full;
        /// <summary>The in-game name in the view's language, as stored (with any Creation Kit tag), or "".</summary>
        public string Name => JsonText.Text(Json["Name"]) ?? "";

        LoadedRecord Loaded => _loaded ??= _view.Load(_ref, _info);
        internal void LoadJson() => _ = Json;

        public override string ToString() => _info.Label;
    }

    /// <summary>Reading values out of the record layer's JSON.</summary>
    public static class JsonText
    {
        /// <summary>
        /// A copy of a record's (or an object's) JSON with every field the schema says is always there and the JSON leaves
        /// out spelled out at its default: an enum's zero member, false, 0, an empty list of flags. Nested objects and list
        /// entries get theirs too, by their own type. Each object's fields are put in the schema's order; fields the
        /// schema doesn't know (FormKey, $Child markers) keep their place in front.
        /// </summary>
        public static JsonObject WithDefaults(JsonObject json, string objectType)
        {
            var copy = (JsonObject)json.DeepClone();
            return Fill(EngineRecords.Schema, copy, objectType);
        }

        static JsonObject Fill(RecordSchema s, JsonObject o, string? objectType)
        {
            string? obj = s.ObjectFor(objectType, Str(o[RecordSplit.TypeKey]));
            if (obj == null) return o;
            var props = s.Props(obj);
            var known = new HashSet<string>(props.Select(p => p.Name), StringComparer.Ordinal);
            var result = new JsonObject();
            foreach (var kv in o.ToList())
                if (!known.Contains(kv.Key)) { o.Remove(kv.Key); result[kv.Key] = kv.Value; }
            foreach (var p in props)
            {
                if (o.TryGetPropertyValue(p.Name, out var v))
                {
                    o.Remove(p.Name);
                    if (v is JsonObject child && !child.ContainsKey("TargetLanguage")) v = Fill(s, child, p.Main.RefObject);
                    else if (v is JsonArray arr && p.Item != null)
                        for (int i = 0; i < arr.Count; i++)
                            if (arr[i] is JsonObject item) { arr[i] = null; arr[i] = Fill(s, item, p.Item.RefObject); }
                    result[p.Name] = v;
                }
                else if (!p.Main.Nullable && FieldLookup.DefaultOf(s, p.Main).Json is { } d)
                    result[p.Name] = d is JsonObject dob ? Fill(s, (JsonObject)dob.DeepClone(), p.Main.RefObject) : d.DeepClone();
            }
            return result;
        }

        static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var t) ? t : null;

        /// <summary>A translated text ({"TargetLanguage", "Value"} or {"TargetLanguage", "Values": [each language]}) in its target language, or a plain string; null for anything else.</summary>
        public static string? Text(JsonNode? n)
        {
            if (n is JsonObject o && o.ContainsKey("TargetLanguage"))
            {
                // One text for every language, or the text of each language (a plugin with strings files): the target language's, else the first
                if (o["Value"] is JsonValue one) return one.GetValue<string>();
                if (o["Values"] is JsonArray each)
                {
                    string target = Str(o["TargetLanguage"]) ?? "English";
                    var hit = each.OfType<JsonObject>().FirstOrDefault(e => string.Equals(Str(e["Language"]), target, StringComparison.OrdinalIgnoreCase)) ?? each.OfType<JsonObject>().FirstOrDefault();
                    return hit == null ? "" : Str(hit["String"]) ?? "";
                }
                return ""; // no text
            }
            if (n is JsonObject v1 && v1.Count == 1 && v1["Value"] is JsonValue only) return only.GetValue<string>();
            if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s;
            return null;
        }

        /// <summary>A value as plain text: a string as it is, a number in its shortest form (a float as .NET writes a float: 0.3, not 0.30000001192092896), true/false as True/False. Null for an object or array.</summary>
        public static string? Value(JsonNode? n)
        {
            if (n is not JsonValue v) return null;
            switch (v.GetValueKind())
            {
                case JsonValueKind.String: return v.GetValue<string>();
                case JsonValueKind.True: return "True";
                case JsonValueKind.False: return "False";
                case JsonValueKind.Number:
                    if (v.TryGetValue<long>(out long l)) return l.ToString(CultureInfo.InvariantCulture);
                    if (v.TryGetValue<ulong>(out ulong ul)) return ul.ToString(CultureInfo.InvariantCulture);
                    double d = v.GetValue<double>();
                    float f = (float)d;
                    string ft = f.ToString(CultureInfo.InvariantCulture);
                    // A float field written as the float's shortest text, or as the exact double of the float: print it as a float
                    return (double)f == d || double.Parse(ft, CultureInfo.InvariantCulture) == d ? ft : d.ToString("R", CultureInfo.InvariantCulture);
                default: return v.ToString();
            }
        }

        /// <summary>A byte field's bytes: "0x0A1B…" as its bytes; "0x" (and "[]", as JSON written before
        /// Mutagen.Bethesda.Serialization 1.38.6-fixes.2 had it) as no bytes. Null for anything else.</summary>
        public static byte[]? Bytes(JsonNode? n)
        {
            if (n is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
            if (FieldLookup.IsNoBytes(s)) return Array.Empty<byte>();
            if (!s.StartsWith("0x", StringComparison.Ordinal) || s.Length % 2 != 0) return null;
            try { return Convert.FromHexString(s.AsSpan(2)); } catch (FormatException) { return null; }
        }

        /// <summary>A number, or null.</summary>
        public static double? Number(JsonNode? n) =>
            n is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>()
            : Value(n) is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>FormKey text: "0003C2:darkstar.esm".</summary>
    public static class FormKeys
    {
        static readonly Regex Exact = new(@"^\s*(?:0x)?([0-9A-Fa-f]{1,8})\s*:\s*(.+?\.(?:esm|esp|esl))\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        /// <summary>A FormKey anywhere in a text (the first one).</summary>
        public static readonly Regex Embedded = new(@"(?<![0-9A-Fa-f])[0-9A-Fa-f]{6}:[^""<>|,\[\]{}\r\n]+?\.(?:esm|esp|esl)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>"3C2:darkstar.esm", "0x3c2 : DarkStar.esm" → "0003C2:darkstar.esm": six hex digits, the plugin spelled as its file in Data. Anything else is returned trimmed.</summary>
        public static string Normalize(string s)
        {
            var m = Exact.Match(s);
            if (!m.Success) return s.Trim();
            uint id = uint.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) & 0xFFFFFF;
            return $"{id:X6}:{PluginName(m.Groups[2].Value)}";
        }

        /// <summary>A plugin name spelled as its file in Data ("starfield.esm" → "Starfield.esm"); a name that isn't there as it is.</summary>
        public static string PluginName(string plugin) => EngineIndex.Get().Index.Plugin(plugin)?.Name ?? plugin;

        /// <summary>True for a FormKey and nothing else.</summary>
        public static bool Is(string? s) => s != null && Exact.IsMatch(s);
        /// <summary>The plugin of a FormKey ("darkstar.esm"), or "".</summary>
        public static string PluginOf(string formKey) { int c = formKey.IndexOf(':'); return c < 0 ? "" : formKey[(c + 1)..].Trim(); }
        /// <summary>"0003C2:darkstar" (no extension).</summary>
        public static string Short(string formKey) => Regex.Replace(Normalize(formKey), @"\.(esm|esp|esl)$", "", RegexOptions.IgnoreCase);
    }

    /// <summary>A command's options: "--name value" and bare "--flag", taken out of the argument list.</summary>
    public static class Args
    {
        public static string? Option(List<string> args, string name) => CommandLine.TakeOption(args, name);
        public static bool Flag(List<string> args, string name) => CommandLine.TakeFlag(args, name);
    }

    /// <summary>
    /// Printing under SFSearch's output contract: lines, Markdown tables, a summary echoed to the console when the output
    /// goes to a file (--out), and the error line. FormKeys in a table can be labelled with their EditorIDs from the header index.
    /// </summary>
    public static class Out
    {
        /// <summary>The longest a table cell may be (the global --maxwidth option); 0 for no limit.</summary>
        public static int MaxWidth => CommandLine.MaxWidth;
        static bool Markdown => CommandLine.Format == OutputFormat.Markdown;

        /// <summary>A line (Markdown output only: JSON and CSV output stay parseable).</summary>
        public static void Say(string line = "") { if (Markdown) Console.WriteLine(line); }

        /// <summary>A line that is also echoed to the console at the end when the output goes to a file.</summary>
        public static void Summary(string line) { Say(line); _active?.SummaryLines.Add(line); }

        /// <summary>A line for the console summary only, when the output goes to a file.</summary>
        public static void ConsoleOnly(string line) => _active?.SummaryLines.Add(line);

        /// <summary>The error line of the contract.</summary>
        public static void Error(string what, string fix) { Console.WriteLine(new AddOnException(what, fix).Line); Environment.ExitCode = 2; }

        /// <summary>A table: Markdown (cells cut at --maxwidth, "|" escaped), or JSON or CSV with the global options.</summary>
        public static void Print(Table t, bool labelFormKeys = false)
        {
            switch (CommandLine.Format)
            {
                case OutputFormat.Json:
                    var arr = new JsonArray();
                    foreach (var row in t.Rows)
                    {
                        var o = new JsonObject();
                        for (int i = 0; i < t.Columns.Count; i++)
                        {
                            string v = i < row.Count ? row[i] : "";
                            JsonNode? parsed = null;
                            if (v.Length > 1 && (v[0] == '{' || v[0] == '[')) { try { parsed = JsonNode.Parse(v); } catch (JsonException) { } }
                            o[t.Columns[i]] = parsed ?? JsonValue.Create(v);
                        }
                        arr.Add(o);
                    }
                    Console.WriteLine(arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    break;
                case OutputFormat.Csv:
                    Console.WriteLine(string.Join(",", t.Columns.Select(Csv)));
                    foreach (var row in t.Rows) Console.WriteLine(string.Join(",", row.Select(Csv)));
                    break;
                default:
                    Console.WriteLine("| " + string.Join(" | ", t.Columns) + " |");
                    Console.WriteLine("| " + string.Join(" | ", t.Columns.Select(_ => ":---")) + " |");
                    foreach (var row in t.Rows)
                    {
                        var cells = row.Select(v =>
                        {
                            string s = v.Replace("\r", "").Replace("\n", " ");
                            if (labelFormKeys && CommandLine.ResolveNames) s = Label(s);
                            if (MaxWidth > 0 && s.Length > MaxWidth) s = s.Substring(0, MaxWidth) + $"…(+{s.Length - MaxWidth})";
                            return s.Replace("|", "\\|");
                        });
                        Console.WriteLine("| " + string.Join(" | ", cells) + " |");
                    }
                    break;
            }
        }

        /// <summary>Every FormKey in a text followed by its EditorID: "000CF2:MyMod.esm [MyMod_Helmet]".</summary>
        public static string Label(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf(':') < 0) return text;
            return FormKeys.Embedded.Replace(text, m => GameData.EditorId(m.Value) is { Length: > 0 } e ? $"{m.Value} [{e}]" : m.Value);
        }

        static string Csv(string v) => v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

        /// <summary>A table to print.</summary>
        public sealed class Table
        {
            public List<string> Columns { get; } = new();
            public List<List<string>> Rows { get; } = new();
            public Table(params string[] cols) { Columns.AddRange(cols); }
            public void Add(params object?[] vals) => Rows.Add(vals.Select(v => v?.ToString() ?? "").ToList());
        }

        static ToFile? _active;

        /// <summary>Sends everything printed to a Markdown file until disposed (nothing changes without a path). Summary lines and the file's path are printed on the console at the end.</summary>
        public static IDisposable ToReport(string? path) { var r = new ToFile(path); if (r.Active) _active = r; return r; }

        sealed class ToFile : IDisposable
        {
            readonly TextWriter _orig = Console.Out;
            readonly StreamWriter? _file;
            readonly string? _path;
            internal readonly List<string> SummaryLines = new();
            public bool Active => _file != null;

            public ToFile(string? outFile)
            {
                if (string.IsNullOrWhiteSpace(outFile)) return;
                _path = SFSearch.Home.UserPath(outFile);
                string? dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _file = new StreamWriter(_path, append: false, new UTF8Encoding(false));
                Console.SetOut(_file);
            }

            public void Dispose()
            {
                if (_file == null) return;
                _file.Flush();
                _file.Dispose();
                Console.SetOut(_orig);
                if (ReferenceEquals(_active, this)) _active = null;
                foreach (var l in SummaryLines) Console.WriteLine(l);
                Console.WriteLine($"Full report ({File.ReadLines(_path!).Count()} lines): {_path}");
            }
        }
    }
}
