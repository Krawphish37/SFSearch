// GAPS S4 (fix-s4s5-world-reads): every typed column and list view of the world types read from the records' own
// bytes, or straight from Mutagen's object, never through the whole record's JSON (the Json column aside: it is the
// answer's form). The census says, for each world type, how each column is read; the checks hold each way against
// the record's JSON value for value, record by record, as the engine reads both. GAPS S123 (J103): list views from
// the bytes too (RawLists.cs), held the same way, and on a sample of other types.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using SFSearch.Index;
using SFSearch.Records;
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class WorldReadTests
{
    /// <summary>The world types: what sits in cells and worldspaces, the worldspaces and cells themselves, surface
    /// trees, blocks and patterns, and the navigation mesh info map.</summary>
    public static readonly string[] WorldTypes =
    {
        "Cell", "Worldspace", "PlacedObject", "PlacedNpc", "PlacedArrow", "PlacedBarrier", "PlacedBeam", "PlacedCone", "PlacedFlame",
        "PlacedHazard", "PlacedMissile", "PlacedTrap", "NavigationMesh", "NavigationMeshInfoMap", "SurfaceTree", "SurfaceBlock", "SurfacePattern",
    };

    /// <summary>Other types whose list views are read from the bytes too (GAPS S123): a sample held the same way.</summary>
    public static readonly string[] SampleTypes = { "Weapon", "Armor", "Book", "Container", "Furniture", "Activator", "Npc" };

    /// <summary>Types whose values Mutagen's hand-read layouts lay out, or whose layout cuts runs for Mutagen's generated
    /// reader (GAPS S164, J123): their list views from the bytes held the same way.</summary>
    public static readonly string[] HandTypes =
    {
        "Quest", "Perk", "Scene", "Race", "Package", "InstanceNamingRules", "LegendaryItem", "GameplayOptionsGroup", "Flora", "GenericBaseForm",
        "Weather", "FootstepSet", "IdleMarker", "Region",
    };

    const string Language = "English";

    public enum How { Bytes, Mutagen, Json }

    public static string Run(Ctx c)
    {
        var said = new List<string> { Census(c) };
        // sfta03.esm holds every world type Starfield has (cells, worldspaces, placed objects, NPCs, traps and hazards,
        // navmeshes and their map, surface trees, blocks and patterns): every column of each, straight from the record and
        // from the bytes, against the record's JSON
        said.Add(DirectAgainstJson(c, "sfta03.esm", WorldTypes, bytesOnly: false));
        // Other types' list views from the bytes (GAPS S123): Starfield.esm's records of a sample of types, the list views
        // the bytes read (their reads straight from the records are another item's: GAPS S151)
        said.Add(DirectAgainstJson(c, "Starfield.esm", SampleTypes, bytesOnly: true));
        // GAPS S164 (J123): the types whose values Mutagen's hand-read layouts lay out (a quest's aliases, a perk's ranks
        // and effects, a scene's actions, a race's biped objects, VMAD's scripts …), their list views from the bytes
        said.Add(DirectAgainstJson(c, "Starfield.esm", HandTypes, bytesOnly: true));
        // GAPS S151: a weapon's object templates (ObjectTemplate<Weapon.Property>, read through Mutagen's generic overlay,
        // AddonIndex's default -1) every column, straight from the records and from the bytes, against the JSON
        said.Add(DirectAgainstJson(c, "Starfield.esm", new[] { "Weapon" }, bytesOnly: false));
        said.Add(DirectAgainstJson(c, "darkstar.esm", new[] { "Weapon" }, bytesOnly: false));
        said.Add(AddonIndex(c));
        // GAPS S125: the placed objects with a patrol and no grouped pack-in (XPRD, an empty XPPA, INAM, PDTO): every one
        // of kgcdoom.esm's placed objects from the bytes against its JSON, and the four records C65 named, by name
        said.Add(DirectAgainstJson(c, "kgcdoom.esm", new[] { "PlacedObject" }, bytesOnly: true));
        said.Add(PatrolNotPackIn(c));
        said.Add(VolumesType11(c));
        // With --heavy, every column of Starfield.esm's world types read from the bytes against Mutagen's object, record by
        // record (the record's JSON as well would take most of an hour: its 3.29 million placed objects' 69 list views)
        if (c.Heavy)
        {
            // The navmesh info map is one record of 51 MB, which the records layer reads only when heavy records are allowed
            bool heavy = c.Records.AllowHeavy;
            c.Records.AllowHeavy = true;
            try
            {
                foreach (string type in WorldTypes)
                    if (c.Index.Plugin("Starfield.esm")!.Segment!.EntriesOf(c.Schema.SignatureOf(type)!).Any())
                    {
                        said.Add(GSqlTests.RawAgainstMutagen(c, "Starfield.esm", type));
                        said.Add(ListsAgainstMutagen(c, "Starfield.esm", type));
                    }
            }
            finally { c.Records.AllowHeavy = heavy; }
        }
        return string.Join("; ", said);
    }

    /// <summary>How a typed view's column is read when a query names it alone.</summary>
    public static How Typed(Ctx c, ViewSpec v, ColumnSpec col)
    {
        if (RawColumn.For(v, col, c.Schema) != null) return How.Bytes;
        return DirectColumn.For(col, Language) != null ? How.Mutagen : How.Json;
    }

    /// <summary>How a list view's column is read when a query names it alone.</summary>
    public static How List(Ctx c, ViewSpec v, string col) =>
        DirectList.For(v, new[] { col }, Language) == null ? How.Json
        : RawList.For(v, new[] { col }, c.Schema, c.Engine.Views.Catalog(v.Root).Typed) != null ? How.Bytes : How.Mutagen;

    /// <summary>The census: each world type's typed columns and list views' columns, by how they are read.</summary>
    public static string Census(Ctx c)
    {
        var lines = new List<string>();
        var said = new List<string>();
        foreach (string type in WorldTypes.Concat(SampleTypes).Concat(HandTypes))
        {
            var cat = c.Engine.Views.Catalog(type);
            var typed = cat.Typed.Columns.Select(col => (col, How: Typed(c, cat.Typed, col))).ToList();
            var lists = cat.Lists.Values.SelectMany(v => v.Columns.Select(col => (v, col, How: List(c, v, col.Name)))).ToList();
            string Count(IEnumerable<How> h) { var l = h.ToList(); return $"{l.Count(x => x == How.Bytes)}/{l.Count(x => x == How.Mutagen)}/{l.Count(x => x == How.Json)}"; }
            said.Add($"{type} typed {Count(typed.Select(x => x.How))} lists {Count(lists.Select(x => x.How))}");
            foreach (var (col, how) in typed) lines.Add($"{type}\t{type}\t{col.Name}\t{col.Kind}\t{how}\t{Why(col)}");
            foreach (var (v, col, how) in lists) lines.Add($"{type}\t{v.Name}\t{col.Name}\t{col.Kind}\t{how}\t{Why(col)}");
        }
        c.Save("world-reads-census.tsv", lines.Prepend("Type\tView\tColumn\tKind\tHow\tShape"));
        return "bytes/Mutagen/JSON: " + string.Join("; ", said);
    }

    /// <summary>What makes a column what it is: its kind, guards, translated text.</summary>
    static string Why(ColumnSpec col)
    {
        var sb = new StringBuilder(col.Kind);
        if (!col.FromBase || col.VariantGuards.Count > 0) sb.Append(" under a kind of object");
        if (col.Expr.Contains("sfs_text", StringComparison.Ordinal)) sb.Append(" translated");
        if (col.SchemaPath.Contains('[') || col.SchemaPath.Contains('{')) sb.Append(" in a list or kind");
        if (col.Row == null) sb.Append(" no single row");
        return sb.ToString();
    }

    /// <summary>
    /// Every record of each world type of a plugin, read through Mutagen once: each typed column and each list view's
    /// columns read straight from the object (DirectColumn, DirectList) against the same record's JSON through the
    /// view's own expressions (what the engine reads otherwise: JsonbMaker.Project and ProjectRows), value for value
    /// and in SQLite's kind, and a list view's rows in the same number and order.
    /// </summary>
    /// <param name="only">The list columns to hold (and no typed column); null: every column read straight from the object.</param>
    internal static string DirectAgainstJson(Ctx c, string plugin, IEnumerable<string> types, bool bytesOnly, Func<ColumnSpec, bool>? only = null)
    {
        var p = c.Index.Plugin(plugin) ?? throw new TestFailed($"{plugin} isn't in the Data folder");
        c.Records.Sessions.Get(plugin);
        var said = new List<string>();
        var clock = Stopwatch.StartNew();
        long records = 0, values = 0, rows = 0, rawRows = 0;
        foreach (string type in types)
        {
            string sig = c.Schema.SignatureOf(type)!;
            var entries = p.Segment!.EntriesOf(sig).ToArray();
            if (entries.Length == 0) continue;
            var cat = c.Engine.Views.Catalog(type);
            var typed = cat.Typed;
            var tCols = typed.Columns.Where(col => only == null && DirectColumn.For(col, Language) != null && (!bytesOnly || RawColumn.For(typed, col, c.Schema) != null)).Select(col => col.Name).ToList();
            var tDirect = tCols.Select(n => DirectColumn.For(typed.ByName[n], Language)!).ToArray();
            string tSql = JsonbMaker.ProjectSql(ViewBuilder.ProjectionExprs(typed, tCols, Language));
            // The columns read from the bytes, each alone (one read through Mutagen doesn't hide the others)
            var tRaw = tCols.Select(n => RawColumn.For(typed, typed.ByName[n], c.Schema) is { } rc ? new RawRecordColumns(new[] { rc }) : null).ToArray();
            var rawPlugin = new RawPlugin(p, c.Records.Sessions.Get(plugin), Language);
            var toMutagen = new long[tCols.Count];
            long rawValues = 0;
            var lists = new List<(ViewSpec View, List<string> Cols, DirectList Direct, string Sql, RawList?[] Raw, RawList? Places)>();
            foreach (var v in cat.Lists.Values)
            {
                var cols = v.Columns.Where(col => only == null || only(col)).Select(col => col.Name).Where(n => DirectList.For(v, new[] { n }, Language) != null).ToList();
                if (cols.Count == 0) continue;
                // The JSON's entries of a flags list are its Value: the same column through json_each. Each column the
                // bytes give, alone, and the list's places alone (a count(*))
                var raws = cols.Select(n => RawList.For(v, new[] { n }, c.Schema, typed)).ToArray();
                if (bytesOnly)
                {
                    cols = cols.Where((n, i) => raws[i] != null).ToList();
                    raws = raws.Where(x => x != null).ToArray();
                    if (RawList.For(v, Array.Empty<string>(), c.Schema, typed) == null) continue;
                }
                lists.Add((v, cols, DirectList.For(v, cols, Language)!, ViewBuilder.ListProjectSql(v, cols, Language), raws, RawList.For(v, Array.Empty<string>(), c.Schema, typed)));
            }
            long listRaw = 0, listLeft = 0;
            c.Records.OpenForThreads(entries.Select(e => new RecordRef(p.Id, e)));
            var makers = new ConcurrentBag<JsonbMaker>();
            var maker = new ThreadLocal<JsonbMaker>(() => { var m = new JsonbMaker(con => Functions.RegisterPure(con, Language)); makers.Add(m); return m; });
            long typeRecords = 0, typeValues = 0, typeRows = 0, problems = 0;
            var first = new List<string>();
            try
            {
                foreach (string problem in RecordPipeline.Read<string>(new[] { new ReadSet { Plugin = p, Entries = entries } }, (in PipelineRecord r, List<string> output) =>
                {
                    Interlocked.Increment(ref typeRecords);
                    LoadedRecord rec;
                    try { rec = c.Records.Load(r.Ref); }
                    catch (Exception e) { output.Add($"{c.Index.Describe(r.Ref).Label}: Mutagen couldn't read it ({e.Message})"); return; }
                    string label = c.Index.Describe(r.Ref).Label;
                    // The record's JSON as the engine makes it: a record that holds others with its children split out
                    byte[] utf8 = c.Schema.HoldsRecords(rec.Type) ? Encoding.UTF8.GetBytes(c.Records.Json(rec).ToJsonString()) : RecordSerializer.ToUtf8(rec.Record).ToArray();
                    var m = maker.Value!;
                    if (tCols.Count > 0)
                    {
                        var json = m.Project(utf8, tSql, tCols.Count);
                        for (int i = 0; i < tCols.Count; i++)
                        {
                            object? d;
                            try { d = tDirect[i].Read(rec.Record); }
                            catch (Exception e) { output.Add($"{label}, {type}.{tCols[i]}: the direct read failed: {e.Message}"); continue; }
                            if (!Same(d, json[i])) output.Add($"{label}, {type}.{tCols[i]}: straight from the record {Show(d)}, the JSON {Show(json[i])}");
                            if (tRaw[i] is not { } raw) continue;
                            var one = new object?[1];
                            if (r.Problem != PipelineProblem.None || !raw.Read(r.Head, r.Data, rawPlugin, one)) { Interlocked.Increment(ref toMutagen[i]); continue; }
                            Interlocked.Increment(ref rawValues);
                            if (!Same(one[0], json[i])) output.Add($"{label}, {type}.{tCols[i]}: from the bytes {Show(one[0])}, the JSON {Show(json[i])}");
                        }
                        Interlocked.Add(ref typeValues, tCols.Count);
                    }
                    foreach (var (v, cols, dl, sql, raws, places) in lists)
                    {
                        List<object?[]> got;
                        try { got = dl.Read(rec.Record); }
                        catch (Exception e) { output.Add($"{label}, {v.Name}: the direct read failed: {e.Message}"); continue; }
                        var want = m.ProjectRows(utf8, sql, v.Lists.Count + cols.Count + 1);
                        // From the bytes: each column alone, and the places alone, against the JSON's rows
                        for (int k = -1; k < cols.Count; k++)
                        {
                            var raw = k < 0 ? places : raws[k];
                            if (raw == null) continue;
                            var fromBytes = new List<object?[]>();
                            if (r.Problem != PipelineProblem.None || !raw.Read(r.Head, r.Data, rawPlugin, fromBytes)) { Interlocked.Increment(ref listLeft); continue; }
                            Interlocked.Increment(ref listRaw);
                            Interlocked.Add(ref rawRows, fromBytes.Count);
                            string what = k < 0 ? "places" : cols[k];
                            if (fromBytes.Count != want.Count) { output.Add($"{label}, {v.Name} ({what}): {fromBytes.Count} rows from the bytes, {want.Count} from the JSON"); continue; }
                            for (int row = 0; row < want.Count; row++)
                            {
                                int lv = v.Lists.Count;
                                if (Enumerable.Range(0, lv).FirstOrDefault(i => !Same(fromBytes[row][i], want[row][i]), -1) is int bi and >= 0)
                                    output.Add($"{label}, {v.Name} row {row} ({what}): place {bi} {Show(fromBytes[row][bi])} from the bytes, {Show(want[row][bi])} the JSON");
                                else if (k >= 0 && !Same(fromBytes[row][lv], want[row][lv + k])) output.Add($"{label}, {v.Name} row {row}, {what}: from the bytes {Show(fromBytes[row][lv])}, the JSON {Show(want[row][lv + k])}");
                            }
                        }
                        if (got.Count != want.Count) { output.Add($"{label}, {v.Name}: {got.Count} rows straight from the record, {want.Count} from the JSON"); continue; }
                        for (int k = 0; k < got.Count; k++)
                            for (int i = 0; i < v.Lists.Count + cols.Count; i++)
                                if (!Same(got[k][i], want[k][i]))
                                {
                                    string name = i < v.Lists.Count ? (i == 0 ? "Idx" : $"Idx{i + 1}") : cols[i - v.Lists.Count];
                                    output.Add($"{label}, {v.Name} row {k}, {name}: straight from the record {Show(got[k][i])}, the JSON {Show(want[k][i])}");
                                }
                        Interlocked.Add(ref typeRows, got.Count);
                        Interlocked.Add(ref typeValues, (long)got.Count * cols.Count);
                    }
                }, new PipelineOptions { Data = PipelineData.Plain, CostPerByte = PipelineOptions.MutagenCostPerByte, PieceRecords = 64, PieceBytes = 1 << 20, Ahead = 16 }))
                {
                    problems++;
                    if (first.Count < 6) first.Add(problem);
                }
            }
            finally
            {
                maker.Dispose();
                foreach (var m in makers) m.Dispose();
            }
            Check(problems == 0, $"{type} of {plugin}: {problems:N0} difference(s) between the direct reads and the JSON. The first: {string.Join(" | ", first)}");
            var left = tCols.Select((n, i) => (n, toMutagen[i])).Where(x => x.Item2 > 0).Select(x => $"{x.n} {x.Item2:N0}").ToList();
            int rawCols = lists.Sum(x => x.Raw.Count(r => r != null));
            said.Add($"{type} {typeRecords:N0} × ({tCols.Count} typed, {tRaw.Count(x => x != null)} from the bytes: {rawValues:N0} values{(left.Count == 0 ? "" : ", left to Mutagen: " + string.Join(", ", left))}; "
                + $"{lists.Count} lists: {typeRows:N0} rows; {rawCols} list columns of {lists.Count(x => x.Places != null)} lists from the bytes: {listRaw:N0} reads equal{(listLeft == 0 ? "" : $", {listLeft:N0} left to Mutagen")})");
            records += typeRecords; values += typeValues; rows += typeRows;
        }
        return $"{plugin}: {records:N0} records, {values:N0} values and {rows:N0} list rows straight from the records, {rawRows:N0} list rows from the bytes, equal to their JSON ({string.Join(", ", said)}) in {clock.Elapsed.TotalSeconds:F0} s";
    }

    /// <summary>GAPS S151: Razorback (000FD6:Starfield.esm)'s fifth object template stores AddonIndex 0, which Mutagen gives
    /// as 0 (its default is -1): straight from the record, from the bytes and through a query.</summary>
    static string AddonIndex(Ctx c)
    {
        var p = c.Index.Plugin("Starfield.esm")!;
        var session = c.Records.Sessions.Get("Starfield.esm");
        var entry = p.Segment!.EntriesOf("WEAP").First(e => c.Index.FormKeyOf(new RecordRef(p.Id, e)) == "000FD6:Starfield.esm");
        var rec = c.Records.Load(new RecordRef(p.Id, entry)).Record;
        var v = c.Engine.Views.Catalog("Weapon").Lists["Weapon_ObjectTemplates"];
        var templates = ((System.Collections.IEnumerable)rec.GetType().GetProperty("ObjectTemplates")!.GetValue(rec)!).Cast<object>().ToList();
        var mutagen = templates.Select(t => (long)(short)t.GetType().GetProperty("AddonIndex")!.GetValue(t)!).ToList();
        Check(mutagen.Count > 4 && mutagen[4] == 0, $"Razorback's fifth template: Mutagen's AddonIndex {(mutagen.Count > 4 ? mutagen[4] : null)}");
        var direct = DirectList.For(v, new[] { "AddonIndex" }, Language)!.Read(rec).Select(r => r[1]).ToList();
        Check(direct.SequenceEqual(mutagen.Cast<object?>()), $"Razorback's AddonIndex straight from the record {string.Join(",", direct)}, Mutagen {string.Join(",", mutagen)}");
        var sql = c.Ok("SELECT Idx, AddonIndex FROM Weapon_ObjectTemplates WHERE FormKey = '000FD6:Starfield.esm' ORDER BY Idx", "Starfield.esm");
        Check(sql.Rows.Select(r => r[1]).SequenceEqual(mutagen.Cast<object?>()), $"Razorback's AddonIndex by a query {string.Join(",", sql.Rows.Select(r => r[1]))}, Mutagen {string.Join(",", mutagen)}");
        if (RawList.For(v, new[] { "AddonIndex" }, c.Schema, c.Engine.Views.Catalog("Weapon").Typed) is { } raw)
        {
            var rows = new List<object?[]>();
            foreach (var _ in RecordPipeline.Read<string>(new[] { new ReadSet { Plugin = p, Entries = new[] { entry } } }, (in PipelineRecord r, List<string> o) =>
            {
                if (raw.Read(r.Head, r.Data, new RawPlugin(p, session, Language), rows)) o.Add("");
            }, new PipelineOptions { Data = PipelineData.Plain })) { }
            Check(rows.Select(r => r[1]).SequenceEqual(mutagen.Cast<object?>()), $"Razorback's AddonIndex from the bytes {string.Join(",", rows.Select(r => r[1]))}, Mutagen {string.Join(",", mutagen)}");
        }
        return $"S151: Razorback's {mutagen.Count} templates' AddonIndex ({string.Join(",", mutagen)}) equal to Mutagen's straight from the record, by a query and from the bytes";
    }

    /// <summary>
    /// GAPS S164 (J123): the five volumes items of type 11 in Data (ase5.esm's 00BEDB, 00BEDC, 00BEDD, 84-byte VLMS: an
    /// empty ender; kinggathcreations_spaceship.esm's 03AA43, 03AA44, 88 bytes: an ender of one uint32). The layout's
    /// TypeSwitch picks by the class Mutagen made, which Mutagen tells by what still fits in the VLMS: the walk takes it
    /// from Mutagen's own read of that component alone. From the bytes, every column of the items equal to Mutagen's.
    /// </summary>
    static string VolumesType11(Ctx c)
    {
        var typed = c.Engine.Views.Catalog("PlacedObject").Typed;
        var v = c.Engine.Views.Catalog("PlacedObject").Lists["PlacedObject_Components_Items"];
        var cols = new[] { "Ender_ObjectType", "Ender_Unknown1", "Row0_X", "Translation_W", "Unknown3" };
        var raw = RawList.For(v, cols, c.Schema, typed) ?? throw new TestFailed("PlacedObject_Components_Items' ender columns aren't read from the bytes");
        var direct = DirectList.For(v, cols, Language)!;
        var said = new List<string>();
        foreach (var (plugin, ids, kind) in new[] { ("ase5.esm", new uint[] { 0x00BEDB, 0x00BEDC, 0x00BEDD }, "VolumesUnknownEnderEmpty"), ("kinggathcreations_spaceship.esm", new uint[] { 0x03AA43, 0x03AA44 }, "VolumesUnknownEnderEmptyPadded") })
        {
            var p = c.Index.Plugin(plugin) ?? throw new TestFailed($"{plugin} isn't in the Data folder");
            var rawPlugin = new RawPlugin(p, c.Records.Sessions.Get(plugin), Language);
            foreach (uint id in ids)
            {
                string key = $"{id:X6}:{p.Name}";
                var entry = p.Segment!.EntriesOf("REFR").FirstOrDefault(e => string.Equals(c.Index.FormKeyOf(new RecordRef(p.Id, e)), key, StringComparison.OrdinalIgnoreCase), -1);
                Check(entry >= 0, $"{key} isn't a placed object of {plugin}");
                var want = direct.Read(c.Records.Load(new RecordRef(p.Id, entry)).Record);
                var rows = new List<object?[]>();
                bool fromBytes = false;
                foreach (var _ in RecordPipeline.Read<string>(new[] { new ReadSet { Plugin = p, Entries = new[] { entry } } }, (in PipelineRecord r, List<string> o) =>
                {
                    fromBytes = raw.Read(r.Head, r.Data, rawPlugin, rows);
                    o.Add("");
                }, new PipelineOptions { Data = PipelineData.Plain })) { }
                Check(fromBytes, $"{key}: its component items went to Mutagen");
                Check(rows.Count == want.Count && rows.Count > 0 && rows.Zip(want).All(x => x.First.Zip(x.Second).All(y => Same(y.First, y.Second))),
                    $"{key}: from the bytes {string.Join(" / ", rows.Select(r => string.Join(",", r.Select(Show))))}, Mutagen {string.Join(" / ", want.Select(r => string.Join(",", r.Select(Show))))}");
                Check(rows.Any(r => (r[v.Lists.Count] as string)?.StartsWith(kind, StringComparison.Ordinal) == true), $"{key}: no {kind} ender: {string.Join(" / ", rows.Select(r => Show(r[v.Lists.Count])))}");
                said.Add($"{key} {kind}");
            }
        }
        return $"S164: the volumes items of type 11 from the bytes equal to Mutagen's ({string.Join(", ", said)})";
    }

    /// <summary>
    /// GAPS S125 (C65): sfta03.esm 0020F9, 0020FB and kgcdoom.esm 008B9A, 008B9C hold a patrol (XPRD, an empty XPPA, INAM,
    /// PDTO) and no grouped pack-in. From their bytes: every GroupedPackIn column NULL (no XWPK), the patrol's idle time
    /// there, each equal to Mutagen's object.
    /// </summary>
    static string PatrolNotPackIn(Ctx c)
    {
        var view = c.Engine.Views.Catalog("PlacedObject").Typed;
        var cols = view.Columns.Where(col => col.Name.StartsWith("GroupedPackIn_", StringComparison.Ordinal) || col.Name.StartsWith("Patrol_", StringComparison.Ordinal)).ToList();
        var said = new List<string>();
        foreach (var (plugin, ids) in new[] { ("sfta03.esm", new uint[] { 0x0020F9, 0x0020FB }), ("kgcdoom.esm", new uint[] { 0x008B9A, 0x008B9C }) })
        {
            var p = c.Index.Plugin(plugin) ?? throw new TestFailed($"{plugin} isn't in the Data folder");
            var rawPlugin = new RawPlugin(p, c.Records.Sessions.Get(plugin), Language);
            foreach (uint id in ids)
            {
                string key = $"{id:X6}:{p.Name}";
                var entry = p.Segment!.EntriesOf("REFR").FirstOrDefault(e => string.Equals(c.Index.FormKeyOf(new RecordRef(p.Id, e)), key, StringComparison.OrdinalIgnoreCase), -1);
                Check(entry >= 0, $"{key} isn't a placed object of {plugin}");
                var rec = c.Records.Load(new RecordRef(p.Id, entry)).Record;
                int fromBytes = 0;
                foreach (string got in RecordPipeline.Read<string>(new[] { new ReadSet { Plugin = p, Entries = new[] { entry } } }, (in PipelineRecord r, List<string> output) =>
                {
                    foreach (var col in cols)
                    {
                        if (RawColumn.For(view, col, c.Schema) is not { } rc) continue;
                        var one = new object?[1];
                        if (!new RawRecordColumns(new[] { rc }).Read(r.Head, r.Data, rawPlugin, one)) continue;
                        object? m = DirectColumn.For(col, Language)!.Read(rec);
                        output.Add(Same(one[0], m) && (!col.Name.StartsWith("GroupedPackIn_", StringComparison.Ordinal) || one[0] == null) ? "" : $"{key} {col.Name}: from the bytes {Show(one[0])}, Mutagen {Show(m)}");
                    }
                }, new PipelineOptions { Data = PipelineData.Plain }))
                {
                    Check(got.Length == 0, got);
                    fromBytes++;
                }
                Check(fromBytes > 0, $"{key}: no GroupedPackIn or Patrol column read from the bytes");
                said.Add($"{key} {fromBytes}");
            }
        }
        return $"S125: the patrols with no grouped pack-in, GroupedPackIn and Patrol columns from the bytes equal to Mutagen's ({string.Join(", ", said)} values)";
    }

    /// <summary>
    /// GAPS S123 with --heavy: every list view of a type whose columns the bytes give, read from each record's bytes
    /// (RawList, the columns together) against Mutagen's object of the same record walked by DirectList, row for row;
    /// or the record left to Mutagen (the bytes don't follow the layout exactly). <paramref name="only"/>: the list
    /// views held (all the bytes read, when null).
    /// </summary>
    internal static string ListsAgainstMutagen(Ctx c, string plugin, string type, Func<ViewSpec, bool>? only = null)
    {
        var cat = c.Engine.Views.Catalog(type);
        var lists = new List<(ViewSpec View, RawList Raw, DirectList Direct, int Columns)>();
        foreach (var v in cat.Lists.Values)
        {
            if (only != null && !only(v)) continue;
            var cols = v.Columns.Select(col => col.Name).Where(n => RawList.For(v, new[] { n }, c.Schema, cat.Typed) != null).ToList();
            if (RawList.For(v, Array.Empty<string>(), c.Schema, cat.Typed) is null) continue;
            var raw = RawList.For(v, cols, c.Schema, cat.Typed) ?? throw new TestFailed($"{v.Name}: each of {string.Join(", ", cols)} is read from the bytes alone, but not together");
            lists.Add((v, raw, DirectList.For(v, cols, Language) ?? throw new TestFailed($"{v.Name}: no direct reader of {string.Join(", ", cols)}"), cols.Count));
        }
        if (lists.Count == 0) return $"{type} of {plugin}: no list view from the bytes";
        var p = c.Index.Plugin(plugin)!;
        var entries = p.Segment!.EntriesOf(c.Schema.SignatureOf(type)!).ToArray();
        var session = c.Records.Sessions.Get(plugin);
        c.Records.OpenForThreads(entries.Select(e => new RecordRef(p.Id, e)));
        var rawPlugin = new RawPlugin(p, session, Language);
        var left = new long[lists.Count];
        long records = 0, listRows = 0, values = 0, badCount = 0;
        var bad = new List<string>();
        var clock = Stopwatch.StartNew();
        // A type that shares its signature with others (GMST, OMOD): only its own records are held, and what the bytes
        // say each record of the signature is (RecordClasses) is held against the class Mutagen made
        var shared = SharedSignature.Of(c, type);
        foreach (string problem in RecordPipeline.Read<string>(new[] { new ReadSet { Plugin = p, Entries = entries } }, (in PipelineRecord r, List<string> output) =>
        {
            if (r.Problem != PipelineProblem.None) { Interlocked.Increment(ref records); output.Add($"{c.Index.Describe(r.Ref).Label}: its bytes can't be read ({r.Problem})"); return; }
            object rec;
            try
            {
                var loaded = c.Records.Load(r.Ref);
                rec = loaded.Record;
                if (shared != null && !shared.Mine(c, r, loaded.Type, type, output)) return;
            }
            catch (Exception e) { Interlocked.Increment(ref records); output.Add($"{c.Index.Describe(r.Ref).Label}: Mutagen couldn't read it ({e.Message})"); return; }
            Interlocked.Increment(ref records);
            for (int k = 0; k < lists.Count; k++)
            {
                var got = new List<object?[]>();
                if (!lists[k].Raw.Read(r.Head, r.Data, rawPlugin, got, out var why)) { LeftReasons.Note(lists[k].View.Name, why); Interlocked.Increment(ref left[k]); continue; }
                var want = lists[k].Direct.Read(rec);
                string label = c.Index.Describe(r.Ref).Label;
                if (got.Count != want.Count) { output.Add($"{label}, {lists[k].View.Name}: {got.Count} rows from the bytes, {want.Count} from Mutagen"); continue; }
                for (int row = 0; row < got.Count; row++)
                    for (int i = 0; i < got[row].Length; i++)
                        if (!Same(got[row][i], want[row][i])) output.Add($"{label}, {lists[k].View.Name} row {row}, value {i}: from the bytes {Show(got[row][i])}, Mutagen {Show(want[row][i])}");
                Interlocked.Add(ref listRows, got.Count);
                Interlocked.Add(ref values, (long)got.Count * lists[k].Columns);
            }
        }, new PipelineOptions { Data = PipelineData.Plain, CostPerByte = PipelineOptions.MutagenCostPerByte, PieceRecords = 512 }))
        {
            badCount++;
            if (bad.Count < 5) bad.Add(problem);
        }
        Check(badCount == 0, $"{type} of {plugin}: {badCount:N0} difference(s) between the list rows from the bytes and Mutagen's. The first: {string.Join("; ", bad)}");
        var leftSaid = lists.Select((x, k) => (x.View.Name, N: left[k])).Where(x => x.N > 0).Select(x => $"{x.Name} {x.N:N0}").ToList();
        return $"{type} of {plugin}: {records:N0} records × {lists.Count} list views from the bytes: {listRows:N0} rows, {values:N0} values equal to Mutagen's, "
            + $"{(leftSaid.Count == 0 ? "none left to Mutagen" : "records left to Mutagen: " + string.Join(", ", leftSaid))}, in {clock.Elapsed.TotalSeconds:F1} s";
    }

    internal static bool Same(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (long x, long y) => x == y,
        (double x, double y) => BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y),
        (string x, string y) => string.Equals(x, y, StringComparison.Ordinal),
        _ => false,
    };

    internal static string Show(object? v) => v == null ? "NULL" : $"{Cut(v.ToString() ?? "", 160)} ({v.GetType().Name})";
}
