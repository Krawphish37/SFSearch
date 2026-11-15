// The inputs: the restored Mutagen package (its record XML, version and commit), the xEdit name pairs, the
// evidence for unknown fields, and the hand-kept annotations.
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace SFSearch.SchemaGen;

public sealed class PairRow
{
    public string Sig = "", Subrecord = "", XEditPath = "", MutagenPath = "", Method = "", Translation = "";
    public int Checked, Agree, Disagree;
    // Filled by the matcher
    public string? Type, Match, Object, Part, SchemaSubrecord, Reason, Detail;
    /// <summary>The path as the pairs file gives it, when it named a field Mutagen has since renamed (MutagenPath is then the new one).</summary>
    public string? RenamedFrom;
    public int? Ord;
}

public sealed class EvidenceRow
{
    public string Sig = "", RecordClass = "", Object = "", Field = "", XmlType = "", Subrecord = "", Kinds = "", NeverSet = "", Priority = "", Note = "", Evidence = "", TopValues = "";
    public int RecordsWithField, RecordsNonDefault, Instances, InstancesNonDefault, DistinctValues, RecordsNonDefaultOfficial;
    public bool Matched;
}

public sealed record Annotation(string Object, string Field, string Unit, string Remark);

public static class Inputs
{
    public const string PairsHeader = "Sig\tSubrecord\txEditPath\tMutagenPath\tMethod\tRecordsChecked\tAgree\tDisagree\tTranslation";
    public const string EvidenceHeader = "RecordType\tRecordClass\tObject\tField\tXmlType\tSubrecord\tKinds\tRecordsWithField\tRecordsNonDefault\tInstances\tInstancesNonDefault\tDistinctValues\tRecordsNonDefaultOfficial32\tNeverSet\tPriority\tNote\tEvidence\tTopValues";

    // ---- the record definitions, read from the restored Mutagen package ----

    /// <summary>The package's id, as NuGet names its folder and its files.</summary>
    public const string PackageId = "mutagen.bethesda.starfield";

    /// <summary>Where the record definitions sit inside the package (C182, fixes.14): the Loqui XML of the source tree.</summary>
    public const string LoquiFolder = "loqui", RecordsPath = "loqui/Mutagen.Bethesda.Starfield/Records", LayoutsPath = "loqui/Mutagen.Bethesda.Starfield/Layouts/HandReadLayouts.xml";

    /// <summary>
    /// The restored package's folder for a version: NuGet's global packages folder (<c>NUGET_PACKAGES</c>, else
    /// <c>%UserProfile%\.nuget\packages</c>), then <c>mutagen.bethesda.starfield\&lt;version&gt;</c>.
    /// </summary>
    public static string PackageDir(string version)
    {
        string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } p ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        return Path.Combine(packages, PackageId, version.ToLowerInvariant());
    }

    /// <summary>What the schema is made from: the package, its version and commit (from its nuspec), and its record XML.</summary>
    public sealed record PackageSource(string Dir, string Version, string Commit, string RecordsDir, string? LayoutsFile, string Sha256, int Files);

    /// <summary>One hash over the package's XML files (relative path from <c>loqui\</c> and content, line endings ignored).</summary>
    public static (string Hash, int Files) HashXml(string loquiDir)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(loquiDir, "*.xml", SearchOption.AllDirectories).OrderBy(f => Path.GetRelativePath(loquiDir, f).Replace('\\', '/'), StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(loquiDir, f).Replace('\\', '/') + "\n"));
            sha.AppendData(Encoding.UTF8.GetBytes(File.ReadAllText(f).Replace("\r\n", "\n")));
            n++;
        }
        return (Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), n);
    }

    /// <summary>
    /// Reads the restored Mutagen.Bethesda.Starfield package's folder and says what doesn't fit: the nuspec must name
    /// the version and the commit of the compiled assembly the generator runs with, the package must carry its record
    /// XML (fixes.14 on), and every XML file in the folder must be the one in the package's own .nupkg (so a file
    /// edited after the restore is caught).
    /// </summary>
    /// <param name="assemblyVersion">The compiled assembly's own version text ("0.55.0-fixes.14+3577da0118…").</param>
    public static (PackageSource? Source, List<string> Problems) CheckPackage(string packageDir, string assemblyVersion)
    {
        var problems = new List<string>();
        string nuspec = Path.Combine(packageDir, PackageId + ".nuspec");
        if (!File.Exists(nuspec))
            return (null, new() { $"There is no restored Mutagen.Bethesda.Starfield package at {packageDir} ({PackageId}.nuspec is missing). Restore the project first." });
        var doc = XDocument.Load(nuspec);
        XNamespace ns = doc.Root!.Name.Namespace;
        var meta = doc.Root.Element(ns + "metadata");
        string version = meta?.Element(ns + "version")?.Value ?? "", commit = meta?.Element(ns + "repository")?.Attribute("commit")?.Value ?? "";
        if (version.Length == 0 || commit.Length < 10) problems.Add($"The package's nuspec must name its version and the full commit it was built from ({nuspec}).");

        var (asmVersion, asmCommit) = assemblyVersion.Split('+') is { Length: > 1 } p ? (p[0], p[1]) : (assemblyVersion, "");
        if (asmVersion != version)
            problems.Add($"The package at {packageDir} is Mutagen {version}, but the compiled package is {asmVersion}. Change MutagenVersion, or restore again.");
        if (asmCommit.Length > 0 && commit.Length > 0 && !asmCommit.StartsWith(commit, StringComparison.OrdinalIgnoreCase) && !commit.StartsWith(asmCommit, StringComparison.OrdinalIgnoreCase))
            problems.Add($"The package's nuspec says it was built from commit {commit}, but the compiled package was built from {asmCommit}.");

        string loqui = Path.Combine(packageDir, LoquiFolder), records = Path.Combine(packageDir, RecordsPath), layouts = Path.Combine(packageDir, LayoutsPath);
        if (!Directory.Exists(records) || !Directory.EnumerateFiles(records, "*.xml", SearchOption.AllDirectories).Any())
        {
            problems.Add($"Mutagen {version} doesn't carry its record definitions ({RecordsPath} is missing; they ship from 0.55.0-fixes.14 on, C182).");
            return (null, problems);
        }

        // Every XML file must be the package's own: compared with the entries of the .nupkg NuGet keeps beside it
        string nupkg = Path.Combine(packageDir, $"{PackageId}.{version.ToLowerInvariant()}.nupkg");
        if (!File.Exists(nupkg)) problems.Add($"The package's .nupkg isn't in {packageDir}, so the XML files can't be checked against it.");
        else
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(nupkg);
            var entries = zip.Entries.Where(e => e.FullName.StartsWith(LoquiFolder + "/", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(e => Uri.UnescapeDataString(e.FullName), StringComparer.Ordinal);
            var onDisk = Directory.EnumerateFiles(loqui, "*.xml", SearchOption.AllDirectories)
                .Select(f => (Rel: LoquiFolder + "/" + Path.GetRelativePath(loqui, f).Replace('\\', '/'), File: f)).ToList();
            foreach (var (rel, file) in onDisk)
            {
                if (!entries.TryGetValue(rel, out var entry)) { problems.Add($"{rel} is in the package's folder but not in its .nupkg."); continue; }
                using var s = entry.Open(); using var m = new MemoryStream(); s.CopyTo(m);
                if (!m.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(file))) problems.Add($"{rel} differs from the package's .nupkg: it was edited after the restore.");
            }
            foreach (var missing in entries.Keys.Except(onDisk.Select(d => d.Rel), StringComparer.Ordinal))
                problems.Add($"{missing} is in the package's .nupkg but missing from its folder.");
        }
        var (hash, files) = HashXml(loqui);
        return (new PackageSource(packageDir, version, commit, records, File.Exists(layouts) ? layouts : null, hash, files), problems);
    }

    // ---- the snapshot files kept in the repo ----

    public static List<PairRow> ReadPairs(string file)
    {
        var rows = new List<PairRow>();
        foreach (var line in File.ReadLines(file))
        {
            if (line.Length == 0 || line.StartsWith('#') || line == PairsHeader) continue;
            var c = line.Split('\t');
            if (c.Length < 9) throw new SchemaException($"{file}: a row with {c.Length} columns, 9 expected: {line}");
            rows.Add(new PairRow { Sig = c[0], Subrecord = c[1], XEditPath = c[2], MutagenPath = c[3], Method = c[4], Checked = Int(c[5]), Agree = Int(c[6]), Disagree = Int(c[7]), Translation = c[8] });
        }
        return rows;
    }

    public static List<EvidenceRow> ReadEvidence(string file)
    {
        var rows = new List<EvidenceRow>();
        foreach (var line in File.ReadLines(file))
        {
            if (line.Length == 0 || line.StartsWith('#') || line == EvidenceHeader) continue;
            var c = line.Split('\t');
            if (c.Length < 18) throw new SchemaException($"{file}: a row with {c.Length} columns, 18 expected: {line}");
            rows.Add(new EvidenceRow
            {
                Sig = c[0], RecordClass = c[1], Object = c[2], Field = c[3], XmlType = c[4], Subrecord = c[5], Kinds = c[6],
                RecordsWithField = Int(c[7]), RecordsNonDefault = Int(c[8]), Instances = Int(c[9]), InstancesNonDefault = Int(c[10]), DistinctValues = Int(c[11]),
                RecordsNonDefaultOfficial = Int(c[12]), NeverSet = c[13], Priority = c[14], Note = c[15], Evidence = c[16], TopValues = c[17],
            });
        }
        return rows;
    }

    public static List<Annotation> ReadAnnotations(string file)
    {
        var rows = new List<Annotation>();
        if (!File.Exists(file)) return rows;
        foreach (var line in File.ReadLines(file))
        {
            if (line.Trim().Length == 0 || line.StartsWith('#') || line.StartsWith("Object\t", StringComparison.Ordinal)) continue;
            var c = line.Split('\t');
            if (c.Length < 3) throw new SchemaException($"{file}: a row needs Object, Field and Unit (tab separated), and may have a Remark: {line}");
            rows.Add(new Annotation(c[0].Trim(), c[1].Trim(), c[2].Trim(), c.Length > 3 ? c[3].Trim() : ""));
        }
        return rows;
    }

    static int Int(string s) => int.TryParse(s, out int v) ? v : 0;
    static string Clean(string s, int max) => (s.Length > max ? s[..max] + "…" : s).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>
    /// Copies what the generator needs from the coverage project's inventory folder into two small files in the repo,
    /// so a build never depends on that folder. Example values with record names are left behind.
    /// </summary>
    public static (int Pairs, int Folders, int Evidence, List<(string Dir, string Why)> LeftOut) ImportSnapshot(string inventory, string pairsOut, string evidenceCsv, string evidenceOut)
    {
        var sb = new StringBuilder();
        sb.Append("# xEdit's name for each Mutagen field, from the coverage project's xedit-<type>-<date> folders (pairs.tsv).\n");
        sb.Append("# Written by `schemagen import`. Don't edit by hand: rerun the import.\n");
        sb.Append(PairsHeader).Append('\n');
        int pairs = 0, folders = 0;
        var (kept, leftOut) = PairFolders(inventory);
        foreach (var (sig, dir) in kept.OrderBy(k => k.Dir, StringComparer.Ordinal))
        {
            string pf = Path.Combine(dir, "pairs.tsv");
            folders++;
            foreach (var line in File.ReadLines(pf).Skip(1))
            {
                var c = line.Split('\t');
                if (c.Length < 8) continue;
                sb.Append(sig).Append('\t').Append(string.Join('\t', c.Take(7))).Append('\t').Append(Clean(c[7], 1000)).Append('\n');
                pairs++;
            }
        }
        File.WriteAllText(pairsOut, sb.ToString());

        sb.Clear();
        sb.Append($"# Evidence for fields still unknown, from the coverage project's {Path.GetFileName(evidenceCsv)}.\n");
        sb.Append("# Written by `schemagen import`. Don't edit by hand: rerun the import.\n");
        sb.Append(EvidenceHeader).Append('\n');
        int ev = 0;
        foreach (var c in Csv.Read(evidenceCsv).Skip(1))
        {
            if (c.Count < 20) continue;
            // 1 RecordType .. 17 Note, 18 Evidence, 19 TopValues; 0 Rank, 8 XmlFile and 20 Samples are left out
            var keep = new[] { c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[9], c[10], c[11], c[12], c[13], c[14], c[15], c[16], c[17], c[18], Clean(c[19], 300) };
            sb.Append(string.Join('\t', keep.Select(k => Clean(k, 1000)))).Append('\n');
            ev++;
        }
        File.WriteAllText(evidenceOut, sb.ToString());
        return (pairs, folders, ev, leftOut);
    }

    /// <summary>
    /// The pairs folders of an inventory, one per record type: <c>xedit-&lt;type&gt;-&lt;yyyy-mm-dd&gt;</c>, or
    /// <c>xedit-&lt;type&gt;-sample-&lt;date&gt;</c> when only a sample of the records was exported. When a type has several
    /// (an older export left behind: <c>xedit-npc-…</c> beside <c>xedit-npc_-…</c>, <c>xedit-cell-sample-…</c> beside
    /// <c>xedit-cell-…</c>), the one named by the full signature wins over a short name padded to it, a full export over a
    /// sample, and then the newest date. The others are returned as left out, so the import can name them: before
    /// 2026-11-13 both were read, and their old pairs (FLEE, RDSA, XCLL) showed as unmatched.
    /// </summary>
    public static (List<(string Sig, string Dir)> Kept, List<(string Dir, string Why)> LeftOut) PairFolders(string inventory)
    {
        var all = new List<(string Sig, string Dir, bool Padded, bool Sample, string Date)>();
        foreach (var dir in Directory.EnumerateDirectories(inventory, "xedit-*"))
        {
            if (!File.Exists(Path.Combine(dir, "pairs.tsv"))) continue;
            var parts = Path.GetFileName(dir).Split('-');
            if (parts.Length < 2) continue;
            string type = parts[1];
            bool sample = parts.Skip(2).Any(x => x.Equals("sample", StringComparison.OrdinalIgnoreCase));
            string date = parts.Length >= 5 ? string.Join('-', parts[^3..]) : "";
            all.Add((type.ToUpperInvariant().PadRight(4, '_'), dir, type.Length < 4, sample, date));
        }
        var kept = new List<(string, string)>();
        var leftOut = new List<(string, string)>();
        foreach (var g in all.GroupBy(f => f.Sig, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var order = g.OrderBy(f => f.Padded).ThenBy(f => f.Sample).ThenByDescending(f => f.Date, StringComparer.Ordinal).ThenBy(f => f.Dir, StringComparer.Ordinal).ToList();
            var win = order[0];
            kept.Add((g.Key, win.Dir));
            foreach (var f in order.Skip(1))
                leftOut.Add((f.Dir, f.Padded && !win.Padded ? $"{Path.GetFileName(win.Dir)} names the full signature {g.Key}"
                    : f.Sample && !win.Sample ? $"{Path.GetFileName(win.Dir)} is the full export of {g.Key}"
                    : $"{Path.GetFileName(win.Dir)} is newer"));
        }
        return (kept, leftOut);
    }
}

public sealed class SchemaException(string message) : Exception(message);

public static class Csv
{
    public static IEnumerable<List<string>> Read(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            var o = new List<string>(); var sb = new StringBuilder(); bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (q) { if (ch == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; } else sb.Append(ch); }
                else if (ch == '"') q = true;
                else if (ch == ',') { o.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(ch);
            }
            o.Add(sb.ToString());
            yield return o;
        }
    }
}
