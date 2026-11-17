// The tests of fix J65 (GAPS S91): the Plugins view, every plugin's header in one table, read from the header's bytes.
// Each row is held against Mutagen's own ModHeader of that plugin (its overlay of the header alone), for every plugin
// in Data, and the view reproduces what J62's Python scan of the 370 headers found (C:/mtg/sfs-fix-work/j62/scripts/heads.py).
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda.Starfield;
using SFSearch.Index;
using static SFSearch.Query.Tests.Ctx;

namespace SFSearch.Query.Tests;

static class PluginsTests
{
    /// <summary>
    /// One query over Plugins, its rows against Mutagen's IStarfieldModHeaderGetter for each plugin of Data (flags,
    /// form version, HEDR's three, masters in order, author, description, the overridden forms' count, INCC), against
    /// SFSearch's raw reader of the TES4 record (its size and subrecords) and the header index (load order, slot,
    /// file size); then J62's scan done again here by its own reading of the bytes, and the view gives the same.
    /// </summary>
    public static string Run(Ctx c)
    {
        QueryAnswer Q(string sql) => c.OkLo(sql, ScopeKind.Winners);
        var a = Q("SELECT * FROM Plugins ORDER BY Plugin");
        Check(a.Columns.SequenceEqual(c.Engine.ColumnsOf("Plugins", ScopeKind.Plugins)!), $"Plugins' columns aren't the ones the engine names: {string.Join(", ", a.Columns)}");
        // describe Plugins: the special view, every column said
        var doc = ViewDocs.Find(c.Engine.Views, "plugins");
        Check(doc != null && doc.Name == "Plugins" && doc.Kind == "special view", "describe Plugins doesn't find the special view");
        Check(doc!.Columns.Select(x => x.Name).SequenceEqual(a.Columns) && doc.Columns.All(x => x.About.Length > 0), $"describe Plugins gives the columns {string.Join(", ", doc.Columns.Select(x => x.Name))}");
        Check(ViewDocs.SpecialNames.Contains("Plugins"), "Plugins isn't among the special views");
        int col(string n) => a.Columns.ToList().IndexOf(n);
        var rows = a.Rows.ToDictionary(r => (string)r[col("Plugin")]!, StringComparer.OrdinalIgnoreCase);
        var inData = c.Index.Plugins.Select(p => p.Name).ToList();
        Check(rows.Count == inData.Count && inData.All(rows.ContainsKey), $"Plugins has {rows.Count} rows; Data has {inData.Count} plugins");
        // The same rows in another scope: the view isn't narrowed by it
        var one = c.Ok("SELECT count(*) FROM Plugins", "kgcdoom.esm");
        Check((long)one.Rows[0][0]! == inData.Count, $"Plugins with the scope kgcdoom.esm has {one.Rows[0][0]} rows, not {inData.Count}");

        int checkedFields = 0;
        var bad = new List<string>();
        void Same(string plugin, string what, object? view, object? want)
        {
            checkedFields++;
            bool eq = (view, want) switch
            {
                (null, null) => true,
                (double d, float f) => d == double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                (long l, _) when want is IConvertible => l == Convert.ToInt64(want, CultureInfo.InvariantCulture),
                _ => Equals(view, want),
            };
            if (!eq && bad.Count < 20) bad.Add($"{plugin} {what}: the view says {view ?? "NULL"}, Mutagen {want ?? "NULL"}");
        }
        foreach (var p in c.Index.Plugins)
        {
            var r = rows[p.Name];
            object? V(string n) => r[col(n)];
            var h = c.Records.LoadHeader(p.Name);
            var m = h.Header;
            uint flags = (uint)m.Flags;
            Same(p.Name, "Flags", V("Flags"), flags);
            Same(p.Name, "Size", V("Size"), (flags & 0x100) != 0 ? "Light" : (flags & 0x400) != 0 ? "Medium" : "Full");
            Same(p.Name, "FlagNames", V("FlagNames"), QueryEngine.FlagNames(flags));
            Same(p.Name, "FormVersion", V("FormVersion"), m.FormVersion);
            Same(p.Name, "Version", V("Version"), m.Stats.Version);
            Same(p.Name, "NumRecords", V("NumRecords"), m.Stats.NumRecords);
            Same(p.Name, "NextFormID", V("NextFormID"), m.Stats.NextFormID);
            var masters = m.MasterReferences.Select(x => x.Master.FileName.String).ToList();
            Same(p.Name, "MasterCount", V("MasterCount"), masters.Count);
            Same(p.Name, "Masters", V("Masters"), JsonSerializer.Serialize(masters, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Same(p.Name, "Author", V("Author"), m.Author);
            Same(p.Name, "Description", V("Description"), m.Description);
            Same(p.Name, "OverriddenCount", V("OverriddenCount"), m.OverriddenForms?.Count ?? 0);
            Same(p.Name, "InteriorCellCount", V("InteriorCellCount"), m.InteriorCellCount);
            // Not Mutagen's: the TES4 record as SFSearch's raw reader cuts it, and the header index
            Same(p.Name, "HeaderSize", V("HeaderSize"), h.Raw.StoredSize);
            Same(p.Name, "Subrecords", V("Subrecords"), string.Join(" ", h.Raw.Subrecords.Select(s => s.Signature).Distinct()));
            Same(p.Name, "FileSize", V("FileSize"), new FileInfo(p.Path).Length);
            Same(p.Name, "LoadOrder", V("LoadOrder"), p.Order.Enabled ? p.Order.Position : null);
            Same(p.Name, "Slot", V("Slot"), p.Order.Enabled ? p.Order.Slot : null);
        }
        Check(bad.Count == 0, $"{bad.Count} field(s) differ: {string.Join("; ", bad)}");

        // J62's scan again, by its own reading of each file's first record: the plugins with the most masters, the flag
        // values, the header's subrecord kinds. The view must say the same.
        var scan = new List<(int Masters, string Name, uint Flags, HashSet<string> Subs)>();
        foreach (var p in c.Index.Plugins)
        {
            using var fs = new FileStream(p.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[24];
            fs.ReadExactly(head);
            var body = new byte[BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4))];
            fs.ReadExactly(body);
            int masters = 0, i = 0; uint big = 0;
            var subs = new HashSet<string>(StringComparer.Ordinal);
            while (i + 6 <= body.Length)
            {
                string sig = Encoding.ASCII.GetString(body, i, 4);
                uint len = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(i + 4));
                if (sig == "XXXX") { big = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i + 6)); i += 10; continue; }
                if (big != 0) { len = big; big = 0; }
                subs.Add(sig);
                if (sig == "MAST") masters++;
                i += 6 + (int)len;
            }
            scan.Add((masters, p.Name, BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8)), subs));
        }
        int most = scan.Max(s => s.Masters);
        var mostScan = scan.Where(s => s.Masters == most).Select(s => s.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var mostView = Q("SELECT Plugin FROM Plugins WHERE MasterCount = (SELECT max(MasterCount) FROM Plugins) ORDER BY Plugin COLLATE NOCASE").Rows.Select(r => (string)r[0]!).ToList();
        Check(mostScan.SequenceEqual(mostView), $"the most masters ({most}): the scan finds {string.Join(", ", mostScan)}; the view {string.Join(", ", mostView)}");
        var x800Scan = scan.Where(s => (s.Flags & 0x800) != 0).Select(s => s.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var x800View = Q("SELECT Plugin FROM Plugins WHERE Flags & 0x800 ORDER BY Plugin COLLATE NOCASE").Rows.Select(r => (string)r[0]!).ToList();
        Check(x800Scan.SequenceEqual(x800View), $"flag 0x800: the scan finds {string.Join(", ", x800Scan)}; the view {string.Join(", ", x800View)}");
        var flagKinds = scan.Select(s => s.Flags).Distinct().Order().ToList();
        var flagKindsView = Q("SELECT DISTINCT Flags FROM Plugins ORDER BY Flags").Rows.Select(r => (uint)(long)r[0]!).ToList();
        Check(flagKinds.SequenceEqual(flagKindsView), $"flag values: the scan {string.Join(", ", flagKinds.Select(f => f.ToString("X")))}; the view {string.Join(", ", flagKindsView.Select(f => f.ToString("X")))}");
        long withData = (long)Q("SELECT count(*) FROM Plugins WHERE ' ' || Subrecords || ' ' LIKE '% DATA %'").Rows[0][0]!;
        Check(withData == scan.Count(s => s.Subs.Contains("DATA")), $"headers with a DATA: the view {withData}, the scan {scan.Count(s => s.Subs.Contains("DATA"))}");
        // Who needs a plugin: the masters as JSON, read with json_each
        var needDark = Q("SELECT count(*) FROM Plugins p, json_each(p.Masters) m WHERE m.value = 'darkstar.esm' COLLATE NOCASE").Rows[0][0];
        var dark = c.Index.Plugins.Count(p => p.Header!.Masters.Contains("darkstar.esm", StringComparer.OrdinalIgnoreCase));
        Check((long)needDark! == dark, $"plugins whose masters hold darkstar.esm: the view {needDark}, the header index {dark}");
        // Masters is JSON inside SQLite's JSON functions, not text (GAPS S80; views-all asks the same)
        var asText = Q("SELECT count(*) FROM Plugins WHERE json_type(json_object('j', Masters), '$.j') IS NOT 'array'").Rows[0][0];
        Check((long)asText! == 0, $"{asText} Masters values come back as text inside json_object");
        Check(a.Notes.All(n => !n.StartsWith("No rows:", StringComparison.Ordinal)), "Plugins says it has no rows");
        // The typed view of the header has no rows, and says where the headers are
        var typed = c.Ok("SELECT count(*) FROM StarfieldModHeader", "Starfield.esm");
        Check(typed.Notes.Any(n => n.Contains("The view Plugins has every plugin's header")), "the empty StarfieldModHeader view doesn't point at Plugins");

        c.Save("plugins.tsv", new[] { string.Join("\t", a.Columns) }.Concat(a.Rows.Select(r => string.Join("\t", r.Select(v => v?.ToString() ?? "")))));
        return $"{rows.Count} plugins in Data, {checkedFields:N0} fields held against Mutagen's ModHeader, the raw TES4 record and the header index, all equal. "
            + $"J62's scan again: the most masters {most} ({string.Join(", ", mostScan)}); flag 0x800 on {x800Scan.Count} ({string.Join(", ", x800Scan)}); "
            + $"{flagKinds.Count} flag values; {withData} headers with a DATA; {dark} plugins need darkstar.esm";
    }
}
