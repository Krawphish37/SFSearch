// schemagen: builds and reads SFSearch's schema (design/agent-first/ARCHITECTURE.md, section 4). It never reads a plugin.
//
//   schemagen build --package <restored package folder> --pairs <tsv> --evidence <tsv> --annotations <tsv> --ck-names <tsv | none> --out <schema.db> [--report <tsv>]
//   schemagen describe <schema.db> <WEAP | Weapon | Weapon.AttackDamage | WEAP/WDMG | "Base Damage"> [filter] [--all]
//   schemagen stats <schema.db>
//   schemagen check [--package <restored package folder>]          do the package's record XML, nuspec and the compiled assembly belong together?
//   schemagen import <coverage inventory folder> <inputs folder> [--date yyyy-mm-dd]   refresh the pairs and evidence snapshots
//   schemagen ck-import <the CK's reflection dump .json> <inputs folder>   make inputs\ck-names-<CK version>.tsv from a new dump
using System.Diagnostics;

namespace SFSearch.SchemaGen;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("schemagen build | describe | stats | check | import | ck-import: see the top of Program.cs"); return 2; }
        var sw = Stopwatch.StartNew();
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        try
        {
            string Opt(string name)
            {
                int i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new SchemaException($"{args[0]} needs {name} <value>.");
            }
            string? OptOrNull(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

            switch (args[0])
            {
                case "build":
                    Generator.Build(new BuildOptions
                    {
                        PackageDir = Opt("--package"), PairsFile = Opt("--pairs"), EvidenceFile = Opt("--evidence"), AnnotationsFile = Opt("--annotations"),
                        OutDb = Opt("--out"), ReportFile = OptOrNull("--report"),
                        // The Creation Kit's names (to-do F30): the data file in inputs\; "none" builds without them
                        CkNamesPath = Opt("--ck-names") is { } ckNames && !ckNames.Equals("none", StringComparison.OrdinalIgnoreCase) ? ckNames : null,
                    });
                    break;
                case "check":
                {
                    string dir = OptOrNull("--package") ?? Inputs.PackageDir(Reflect.AssemblyVersion().Split('+')[0]);
                    var (src, problems) = Inputs.CheckPackage(dir, Reflect.AssemblyVersion());
                    if (problems.Count > 0 || src == null) throw new SchemaException("The Mutagen package's record definitions can't be used:\n  " + string.Join("\n  ", problems));
                    Console.WriteLine($"The package's record XML belongs to the compiled package ({Reflect.AssemblyVersion()}): {src.Files} XML files, sha256 {src.Sha256}, layouts {(src.LayoutsFile != null ? "present" : "missing")}.");
                    break;
                }
                case "import":
                {
                    if (args.Length < 3) throw new SchemaException("import needs the coverage inventory folder and the inputs folder.");
                    Directory.CreateDirectory(args[2]);
                    string csv = Directory.EnumerateFiles(args[1], "unknown-fields-r*.csv").OrderBy(f => f, StringComparer.Ordinal).Last();
                    // The file is named by the day the pairs were made: --date when the folders were rerun after the date in their names
                    string date = OptOrNull("--date") ?? Inputs.PairFolders(args[1]).Kept.Select(k => Path.GetFileName(k.Dir)[^10..]).Max()!;
                    var (pairs, folders, ev, leftOut) = Inputs.ImportSnapshot(args[1], Path.Combine(args[2], $"xedit-pairs-{date}.tsv"), csv, Path.Combine(args[2], Path.GetFileNameWithoutExtension(csv) + ".tsv"));
                    Console.WriteLine($"{pairs} pairs from {folders} folders (dated {date}); {ev} evidence rows from {Path.GetFileName(csv)}");
                    foreach (var (dir, why) in leftOut) Console.WriteLine($"  left out {Path.GetFileName(dir)}: {why}");
                    break;
                }
                case "ck-import":
                {
                    // The only reader of the CK's dump: a new Creation Kit means a new dump, and this makes the data file
                    if (args.Length < 3) throw new SchemaException("ck-import needs the CK's reflection dump (.json) and the inputs folder.");
                    var ck = CkReflection.Load(args[1]);
                    Directory.CreateDirectory(args[2]);
                    string file = Path.Combine(args[2], CkNamesFile.FileName(ck));
                    CkNamesFile.Write(ck, file);
                    var t = ck.Types.Values;
                    Console.WriteLine($"{file}: {ck.Source}, dump sha256 {ck.DumpSha256}; {t.Count(x => x.Kind == "Class")} classes, {t.Count(x => x.Kind != "Class")} enums, {t.Sum(x => x.Fields.Count)} fields, {t.Sum(x => x.Members.Count)} enum members; {new FileInfo(file).Length:N0} bytes");
                    foreach (var old in Directory.EnumerateFiles(args[2], CkNamesFile.Prefix + "*" + CkNamesFile.Extension).Where(f => !Path.GetFileName(f).Equals(Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)))
                        Console.WriteLine($"  an older file is still there: {Path.GetFileName(old)}. Delete it, and point SchemaCkNames in SFSearch.SchemaGen.csproj at {Path.GetFileName(file)}.");
                    break;
                }
                case "describe":
                    if (args.Length < 3) throw new SchemaException("describe needs the schema.db and what to describe.");
                    return Describe.Run(args[1], args[2], args.Skip(3).FirstOrDefault(a => !a.StartsWith("--")), args.Contains("--all"), Console.Out);
                case "sql":
                    if (args.Length < 3) throw new SchemaException("sql needs the schema.db and a query.");
                    Describe.Sql(args[1], args[2], Console.Out);
                    break;
                case "stats":
                    if (args.Length < 2) throw new SchemaException("stats needs the schema.db.");
                    Describe.Stats(args[1], Console.Out);
                    break;
                default:
                    Console.Error.WriteLine($"Unknown command \"{args[0]}\".");
                    return 2;
            }
        }
        catch (Exception e) when (e is SchemaException or IOException)
        {
            Console.Error.WriteLine("schemagen: " + e.Message);
            return 1;
        }
        Console.Error.WriteLine($"[{args[0]}] {sw.Elapsed.TotalSeconds:F2} s");
        return 0;
    }
}
