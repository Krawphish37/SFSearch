using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace SFSearch
{
    public class Program
    {
        static readonly HashSet<string> ExcludedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
        { 
            // The user explicitly requested we stop filtering data since we are using targeted SQL queries now.
            // All Mutagen object data will now be serialized into the JSON payload for comprehensive querying.
        };

        static readonly Dictionary<string, string> MutagenToxEditMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "ObjectBounds", "OBND" },
            { "DirtinessScale", "ODTY" },
            { "Transforms", "PTT2" },
            { "Components", "Base Form Components" },
            { "WorldModel", "Model" },
            { "FirstPersonModel", "1st Person Model" },
            { "BlockBashImpactDataSet", "BIDS" },
            { "AlternateBlockMaterial", "BAMT" },
            { "PickupSound", "PUSH" },
            { "DropdownSound", "PDSH" },
            { "InstanceNaming", "INRD" },
            { "AttachParentSlots", "APPR" },
            { "AimModel", "WAIM" },
            { "AimAssistTemplate", "WAIM" },
            { "AmmoType", "WAM2" },
            { "AmmoCapacity", "WAM2" },
            { "OverrideProjectile", "WAM2" },
            { "SoundLevel", "WAUD" },
            { "WTUR", "WTUR" },
            { "AttackDamage", "WDMG" },
            { "DamageTypes", "DAMA" },
            { "FiringType", "WFIR" },
            { "AttackSeconds", "WFIR" },
            { "ShotsPerSecond", "WFIR" },
            { "PlayerOnly", "WFLG" },
            { "NonHostile", "WFLG" },
            { "BaseWeight", "WGEN" },
            { "BaseValue", "WGEN" },
            { "BaseSpeed", "WGEN" },
            { "MeleeBashDamage", "WMEL" },
            { "MeleeReach", "WMEL" },
            { "MeleeStagger", "WMEL" },
            { "ReloadSpeed", "WRLO" },
            { "FirstPersonFlags", "BO64" },
            { "Armatures", "Models" },
            { "ArmorRating", "FNAM" },
            { "Resistances", "DAMA" },
            { "ObjectTemplates", "Object Template" },
            { "Voice", "VTCK" },
            { "Factions", "SNAM" },
            { "UseTemplateActors", "ACBS - Template Flags" },
            { "DefaultTemplate", "TPLT" },
            { "LegendaryTemplate", "LTPT" },
            { "LegendaryChance", "LTPC" },
            { "TemplateActors", "TPTA" },
            { "Race", "RNAM" },
            { "ActorEffect", "SPLO" },
            { "Skin", "WNAM" },
            { "CombatOverridePackageList", "ECOR" },
            { "Properties", "PRPS" },
            { "RDSAs", "RDSA" },
            { "Class", "CNAM" },
            { "InheritsSoundsFrom", "CSCR" },
            { "DefaultPackageList", "DPLT" },
            { "DefaultOutfit", "DOFT" },
            { "SpaceOutfit", "SOFT" },
            { "CrimeFaction", "CRIF" },
            { "CombatStyle", "CSGD" },
            { "Items", "CNTO" },
            { "Packages", "PKID" },
            { "Perks", "PRKR" },
            { "DeathItem", "INAM" }
        };

        static string GetDisplayName(string propName)
        {
            if (propName == "Item") return "**Item**";
            if (MutagenToxEditMap.TryGetValue(propName, out string? xedit)) return $"`{xedit}` ({propName})";
            return $"**{propName}**";
        }

        static readonly Dictionary<string, string> xEditToMutagenTypeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "WEAP", "Weapon" }, { "ARMO", "Armor" }, { "ARMA", "ArmorAddon" },
            { "OMOD", "ObjectModification" }, { "INNR", "InstanceNamingRules" },
            { "KYWD", "Keyword" }, { "COBJ", "ConstructibleObject" }, { "FLST", "FormList" },
            { "LVLI", "LeveledItem" }, { "LVLN", "LeveledNpc" }, { "NPC_", "Npc" }, { "NPC", "Npc" }, { "FACT", "Faction" },
            { "RACE", "Race" }, { "CLAS", "Class" }, { "OTFT", "Outfit" },
            { "MISC", "MiscItem" }, { "AMMO", "Ammunition" }, { "CMPO", "Component" },
            { "ALCH", "Ingestible" }, { "PERK", "Perk" }, { "SPEL", "Spell" }, { "MGEF", "MagicEffect" },
            { "ENCH", "Enchantment" }, { "PROJ", "Projectile" }, { "GLOB", "Global" },
            { "QUST", "Quest" }, { "DMGT", "DamageType" }, { "AVIF", "ActorValueInformation" }
        };

        static string NormalizeRecordType(string input)
        {
            if (xEditToMutagenTypeMap.TryGetValue(input, out string? mutagenType)) return mutagenType;
            return input;
        }

        public static string NormalizeRecordTypePublic(string input) => NormalizeRecordType(input);
        public static void EnsureCacheUpToDatePublic(string target) => EnsureCacheUpToDate(target);
        public static string GetDataFolderPathPublic() => GetDataFolderPath();
        /// <summary>
        /// True when a cache exists but needs a rebuild: older format, or (when its plugin file can be found) built from a
        /// different copy of the plugin. Checked by Sfs.OpenCache even with ensureUpToDate: false, so master lookups refresh too.
        /// </summary>
        public static bool IsCacheStale(string target)
        {
            string dbPath = GetCachePath(target);
            if (!File.Exists(dbPath)) return false;
            string? src = CacheSourcePath(target, dbPath);
            return src != null ? CacheStaleReason(dbPath, src) != null : !IsCacheFormatCurrent(dbPath);
        }

        internal static void Main(string[] args)
        {
            // Numbers are stored and shown the same way whatever language the computer is set to (1.5, never 1,5).
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            // The service runs as "service run <home>" from a copy of the program, and its workers as "service worker <home> <pipe>"
            bool host = !SessionServer.Active && args.Length >= 3 && args[0].Equals("service", StringComparison.OrdinalIgnoreCase)
                && (args[1].Equals("run", StringComparison.OrdinalIgnoreCase) || args[1].Equals("worker", StringComparison.OrdinalIgnoreCase));
            if (host) Environment.SetEnvironmentVariable("SFSEARCH_HOME", args[2]);
            Sfs.PinHomeDirectory();
            if (host && args[1].Equals("run", StringComparison.OrdinalIgnoreCase)) { Service.ServiceHost.Run(); return; }
            if (host && args.Length >= 4) { Service.ServiceWorker.Run(args[3]); return; }
            // While the service runs, every command goes to it
            if (!SessionServer.Active && args.Length > 0 && !args[0].Equals("session", StringComparison.OrdinalIgnoreCase) && !args[0].Equals("service", StringComparison.OrdinalIgnoreCase) && SessionServer.IsRunning())
            {
                Environment.ExitCode = SessionServer.Client(args.Where(a => !a.Equals("--live", StringComparison.OrdinalIgnoreCase)).ToArray());
                return;
            }
            if (!SessionServer.Active) try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch { } // reports use →, ×, …

            var argList = Sfs.ParseGlobalOptions(args.ToList());
            if (argList.Count < 1)
            {
                PrintHelp();
                return;
            }

            bool useLive = Sfs.TakeFlag(argList, "--live");
            if (useLive && !SessionServer.Active && !LiveDb.Enabled)
            {
                // --live: build only what this command needs, in a folder of its own, and delete it afterwards
                LiveDb.Enabled = true;
                Sfs.DbFolder = Path.Combine(Path.GetTempPath(), "sfsearch_live", Environment.ProcessId.ToString());
                AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Sfs.CloseResolver(); Directory.Delete(Sfs.DbFolder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } };
            }
            int quality = 0;
            string? qOpt = Sfs.TakeOption(argList, "--quality");
            if (qOpt != null && int.TryParse(qOpt, out int qVal)) quality = qVal;

            args = argList.ToArray();
            string command = args[0].ToLowerInvariant();
            var restArgs = argList.Skip(1).ToList();

            try
            {
                if (LiveDb.Enabled && command is "names" or "inspect" or "dump" or "read" or "refs" or "conflicts") LiveDb.HintFromArgs(command, restArgs);
                if (command == "version" || command == "--version" || command == "-v")
                {
                    string asmPath = typeof(Program).Assembly.Location;
                    var built = File.Exists(asmPath) ? File.GetLastWriteTime(asmPath) : DateTime.MinValue;
                    Console.WriteLine($"SFSearch {Extensions.ApiVersion.ToString(3)} | Schema v{CacheSchemaVersion} | Built: {built:yyyy-MM-dd HH:mm:ss} | Home: {Sfs.HomeDir}");
                }
                else if (command == "help" || command == "--help" || command == "-h")
                {
                    PrintHelp();
                }
                else if (command == "buildcache")
                {
                    if (args.Length < 2) return;
                    var targets = TargetResolver(args[1], true);
                    foreach (var t in targets)
                    {
                        if (t.StartsWith("_file", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine($"Cache ready: {t}"); continue; } // a path: TargetResolver already built it
                        BuildCache(t);
                    }
                }
                else if (command == "list")
                {
                    if (args.Length < 3) return;
                    var targets = TargetResolver(args[1], useLive);
                    foreach (var t in targets) ListRecords(t, NormalizeRecordType(args[2]));
                }
                else if (command == "inspect")
                {
                    if (args.Length < 3) return;
                    string sig = args.Length >= 4 ? NormalizeRecordType(args[2]) : "*";
                    string key = args.Length >= 4 ? args[3] : args[2];
                    string field = args.Length >= 5 ? args[4] : "*";
                    var targets = TargetResolver(args[1], useLive);
                    foreach (var t in targets) InspectRecord(t, sig, key, field);
                }
                else if (command == "databases")
                {
                    ListDatabases();
                }
                else if (command == "diff")
                {
                    RunDiffCommand(restArgs);
                }
                else if (command == "dump")
                {
                    if (args.Length < 3) return;
                    string sig = args.Length >= 4 ? NormalizeRecordType(args[2]) : "*";
                    string key = args.Length >= 4 ? args[3] : args[2];
                    var targets = TargetResolver(args[1], useLive);
                    foreach (var t in targets) DumpRecord(t, sig, key);
                }
                else if (command == "read")
                {
                    if (args.Length < 3) return;
                    string sig = args.Length >= 4 ? NormalizeRecordType(args[2]) : "*";
                    string key = args.Length >= 4 ? args[3] : args[2];
                    var targets = TargetResolver(args[1], useLive);
                    foreach (var t in targets) ReadRecord(t, sig, key);
                }
                else if (command == "report")
                {
                    if (args.Length < 3)
                    {
                        Console.WriteLine("Usage: SFSearch report [--live] [--quality <number>] --weapon|--armor <Target>");
                        return;
                    }
                    bool isWeapon = args[1].Equals("--weapon", StringComparison.OrdinalIgnoreCase);
                    var targets = TargetResolver(args[2], useLive);
                    ReportGenerator.GenerateReport(isWeapon, targets, useLive, quality);
                }
                else if (command == "session" || command == "service")
                {
                    if (SessionServer.Active && !(restArgs.Count > 0 && restArgs[0].Equals("config", StringComparison.OrdinalIgnoreCase))) { Console.WriteLine("You're already in the service: this command is for starting and stopping it."); return; }
                    SessionServer.Command(restArgs);
                }
                else if (command == "query")
                {
                    string? sqlFile = Sfs.TakeOption(restArgs, "--file");
                    string? outFile = Sfs.TakeOption(restArgs, "--out");
                    string? withOpt = Sfs.TakeOption(restArgs, "--with");
                    bool withMasters = Sfs.TakeFlag(restArgs, "--with-masters");
                    bool timing = Sfs.TakeFlag(restArgs, "--timing");
                    bool loadOrder = Sfs.TakeFlag(restArgs, "--loadorder");
                    bool scripts = Sfs.TakeFlag(restArgs, "--scripts");
                    // "--stdin" reads piped SQL: sfs.cmd (cmd.exe) cuts an inline argument at its first line break,
                    // and a bare "-" can't be used because powershell.exe -File rejects it as a parameter name.
                    bool fromStdin = Sfs.TakeFlag(restArgs, "--stdin");
                    // Query --loadorder "<SQL>": the load-order index alone, no plugin database
                    if (loadOrder && (sqlFile != null || fromStdin ? restArgs.Count == 0 : restArgs.Count == 1)) restArgs.Insert(0, LoadOrderTarget);
                    if (restArgs.Count < 1 || (sqlFile == null && !fromStdin && restArgs.Count < 2))
                    {
                        Console.WriteLine("Usage: SFSearch Query <Target> <SQL | --file path.sql | --stdin (SQL piped in)> [--with-masters] [--with p1,p2] [--out report.md] [--timing]");
                        return;
                    }
                    string sql = fromStdin ? ReadPipedSql()
                        : sqlFile != null ? File.ReadAllText(Sfs.ResolveUserPath(sqlFile)) : restArgs[1];
                    LiveDb.CurrentSql = sql; // --live and the session read only what this SQL needs
                    var targets = restArgs[0] == LoadOrderTarget ? new List<string> { LoadOrderTarget } : TargetResolver(restArgs[0], useLive);
                    LiveDb.SetPrimary(targets);
                    var extra = withOpt == null ? new List<string>() : TargetResolver(withOpt);
                    using (Sfs.ReportOut.Begin(outFile))
                        QueryDatabases(targets, sql, withMasters, extra, timing, loadOrder, scripts);
                }
                else if (command == "mkdiff")
                {
                    ResearchCommands.MkDiff(restArgs);
                }
                else if (command == "omodcensus")
                {
                    ResearchCommands.OmodCensus(restArgs);
                }
                else if (command == "recipecensus")
                {
                    ResearchCommands.RecipeCensus(restArgs);
                }
                else if (command == "q")
                {
                    SavedQueries.Run(restArgs, useLive);
                }
                else if (command == "header")
                {
                    InspectCommands.Header(restArgs);
                }
                else if (command == "stats")
                {
                    InspectCommands.Stats(restArgs);
                }
                else if (command == "names")
                {
                    InspectCommands.Names(restArgs);
                }
                else if (command == "refs")
                {
                    InspectCommands.Refs(restArgs);
                }
                else if (command == "sources")
                {
                    SourcesCommand.Run(restArgs);
                }
                else if (command == "conflicts")
                {
                    InspectCommands.Conflicts(restArgs);
                }
                else if (command == "loadorder")
                {
                    LoadOrderCommand.Run(restArgs);
                }
                else if (command == "changes")
                {
                    History.Command(restArgs);
                }
                else if (command == "scripts")
                {
                    ScriptsCommand.Run(restArgs);
                }
                else if (command == "setdata")
                {
                    if (restArgs.Count < 1) { Console.WriteLine("Usage: SFSearch SetData <path to Starfield's Data folder>"); return; }
                    SetDataFolderPath(restArgs[0]);
                }
                else if (command == "fields")
                {
                    FieldsCommand.Run(restArgs);
                }
                else if (command == "__warmup")
                {
                    Service.Warmup.Run(restArgs);
                }
                else if (command == "delta")
                {
                    InspectCommands.Delta(restArgs);
                }
                else if (command == "assets")
                {
                    AssetsCommand.Run(restArgs);
                }
                else if (command == "add")
                {
                    AddCommand.Run(restArgs);
                }
                else if (command == "verifydb")
                {
                    VerifyDbCommand.Run(restArgs);
                }
                else if (command == "losscheck")
                {
                    LossCheckCommand.Run(restArgs);
                }
                else if (command == "plugindiff")
                {
                    PluginDiffCommand.Run(restArgs);
                }
                else if (command == "profile")
                {
                    ProfileCommand.Run(restArgs);
                }
                else if (command == "calculatepercentiles")
                {
                    CalculatePercentiles();
                }
                else if (command == "live")
                {
                    LiveCommand.Run(restArgs);
                }
                else if (command == "livescancheck")
                {
                    LiveCommand.ScanCheck(restArgs);
                }
                else if (command == "inspectform")
                {
                    if (args.Length < 3) return;
                    InspectFormDirectly(args[1], args[2]);
                }
                else if (command == "extensions")
                {
                    Console.Write(Extensions.Describe());
                }
                else if (!Extensions.TryRun(command, restArgs))
                {
                    Console.WriteLine($"Unknown command '{command}'. Run 'SFSearch --help' for usage.");
                }
            }
            catch (FileNotFoundException ex)
            {
                // A plugin or file that isn't there: say so, no stack trace
                Console.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                // Mutagen's load-order import wraps the real cause (which plugin, what failed) several levels deep.
                var root = ex;
                while (root.InnerException != null) root = root.InnerException;
                if (root != ex) Console.WriteLine($"Cause: {root.GetType().Name}: {root.Message}");
                Console.WriteLine(ex.StackTrace);
            }
            finally
            {
                Sfs.CloseResolver();
            }
        }

        static void PrintHelp()
        {
            Console.WriteLine(@"SFSearch - Starfield ESM Reverse-Engineering & SQLite Analysis CLI
Global flags: --format md|json|csv | --json | --csv | --maxwidth <N> | --raw (disable EditorID annotations)
For agents: --live (any command) builds only what that command needs from the plugin files and keeps nothing;
  Service start | stop | restart | status | config (or Session ...) runs SFSearch as a service (settings: sfsearch.json): many commands at once, from many agents
  (the MCP adapter Tools\SFSearch.Mcp is the main way in). While it runs, every command goes to it.

Load order and history:
  LoadOrder [plugin] [--all] [--quick]           The real load order: position, runtime slot (console prefix), size, records,
                                                missing or late masters, overrides of a different type than their base
  Query --loadorder [<Target>] <SQL>             Attach the load-order index as lo: lo.Plugins, lo.Versions (every record in every
                                                plugin), lo.Winners (last loaded version), lo.WinningRecords (full data of the
                                                winners of the types the SQL names); functions winner(fk), versions(fk)
  Scripts <plugin> [--find text] [--show name]   A plugin's Papyrus scripts as source (decompiled once with Champollion, kept
                                                in Cache\Scripts.db); Query --scripts attaches them as scr (scr.Scripts, scr.Lines)
  Changes [plugin] [--since yyyy-mm-dd] [--all]  What changed in Data since history began (mod updates, size changes), and
                                                patches at risk because a master changed size (trap 15)

Core Commands:
  Query <Target> <SQL | --file f.sql>           Execute SQL across 1 or more plugins (supports globs & --cached).
        [--with-masters] [--with p1,p2]         Attach master/extra caches (aliases printed, e.g. sf = Starfield.esm) + temp table AllRecords
        [--out report.md] [--timing]            Every SELECT prints its own table; '-- ## Title' before a statement names it
                                                SQL functions: edid(fk), fkname(fk), en(localizedJson), tier(edid)
                                                Multi-line SQL: --file f.sql, or pipe it in with --stdin (sfs.cmd cuts inline SQL at its first line break)
  SetData <path>                               Remember where Starfield's Data folder is (needed only when it isn't found by itself)
  Fields <Target> <Type> [--filter text]        Every JSON path a record type has in Data, with counts and a sample: use before json_extract
  Q <macro> <Target> [filter] | Q --list        Run built-in query macros (flst, vmad, appr, templates, kw, cobj, overrides, new, arma, omod)
  Inspect <Target> [Sig|*] <FormKey|EDID> [fld] Inspect record fields (annotates FormKeys with EditorIDs)
  Dump <Target> [Sig|*] <FormKey|EDID>          Dump raw JSON of a record
  Read <Target> [Sig|*] <FormKey|EDID>          Format record in xEdit layout
  Diff <T1> <T2> [Sig|*] <K1> [K2] [options]    Compare two records (--summary, --ignore f1,f2, --expect f1,f2)

Reverse-Engineering & Verification Commands:
  Header <plugin|path>                          Inspect TES4 flags (Light/Medium/Full), MaxLocalFormID, and ordered Masters
  Stats <plugin|path>                           Record counts by signature (Total, New, Overrides, CachedInDB)
  Delta <plugin> [FormKey|EDID] [--detail]      Compare every override in <plugin> against its preceding master record
        [--out file.md]                           (--detail adds a field-by-field table per override)
  Names <FormID...> [--in <p1,p2>]              Write FormIDs as EditorID (FormID:plugin); bare IDs list every plugin that has them
  Refs <FormKey|EDID> [--in <Target>]           Find all inbound references to a record across caches
  Sources <plugin> [--sig T] [--item X]         How the player gets each weapon/armor: follows Refs back through leveled lists to
        [--with p1,p2] [--depth N] [--scripts]    quests, recipes (with conditions), vendors, containers, placed copies, NPCs;
        [--out file.md]                           --scripts lists the plugin's scripts that call GetFormFromFile / AddItem
  Conflicts <FormKey|EDID> [--in <Target>]      Show full override chain, winner, and modified fields across plugins
  Assets <plugin> [--missing]                   Verify all .nif/.mesh/.mat paths against loose files and BA2 archives
  Add <plugin|path> <Type> ... | --list | --all     Store record types in full, with no rebuild (a rebuild adds them back)
  VerifyDb <plugin|path> [--report f] [--samples N]  Check a plugin's database against the plugin file
  LossCheck <plugin|path> ... [--all] [--report f]  Read + save through both readers (temp folder, never Data); list anything lost
  PluginDiff <old> <new> [--detail] [--shifts]  Compare two versions of a plugin (EditorID-matched, FormID-shift-aware)
  Profile <plugin> [--against <plugin>]         A full dossier on a mod before patching it (--against: its overlap with another mod)
  Live <plugin|path> <FormKey|EDID> [--sig T]   Read one record straight from the plugin + its masters (no cache, no load order)
        [--field Path[0].X] [--chain] [--links]   One field / override chain with changed fields / outbound links resolved
        [--vs-cache] [--subrecords] [--with p1,p2] Live vs cached fields / raw subrecords / extra plugins loaded after it
  Live <plugin|path> <record> --refs [--with p1,p2]  What points at it, read from the file: also reaches dialogue, navmeshes, statics and other types the cache skips
  Live <plugin|path> --sig T --field ""Path[*].X""   One field from every record of a type, no cache needed ([--filter EDID*] [--value text] [--new] [--empty] [--all])
  LiveScanCheck <plugin>                        Check Live's header scan (type + cell of every record) against the plugin's cache

Blueprint Research Commands (all take --with p1,p2 and --out report.md):
  MkDiff <plugin> [--sig Armor|Weapon] [--by prefix:N,kind,archetype,bench,none] [--list Field] [--pairs] [--all]
                                                Pair each new ARMO/WEAP with its base via its upgrade recipe; per category
                                                field-change counts, ADD/DEL/KEEP for keyword/APPR/template lists, numeric deltas
  OmodCensus <plugin> <EditorID glob> [--all]   Group quality OMODs by tier: property signatures, AttachPoint, target keywords,
                                                TiersAny ladders and tier-order problems
  RecipeCensus <plugin> [--bench kw] [--created Sig] [--filter glob] [--all]
                                                Classify COBJs by bench + created type; per tier: conditions, perks, filters,
                                                IFK, sort order, components, GetLevel values, and outliers
  Version | Databases | BuildCache <Target>
  Extensions                                    List the add-ons in the extensions folder and their commands");
            foreach (var line in Extensions.HelpLines()) Console.WriteLine(line);
        }

        /// <summary>
        /// Splits "a.esm,b.esm" into plugins, keeping a comma that is part of a file's name or folder: pieces that join up into
        /// an existing file (a path, or a name in Data) stay one entry, longest match first.
        /// </summary>
        public static List<string> SplitPluginList(string input)
        {
            var pieces = input.Split(',');
            var result = new List<string>();
            string dataPath = GetDataFolderPath();
            for (int i = 0; i < pieces.Length; i++)
            {
                int end = i;
                for (int j = pieces.Length - 1; j > i; j--)
                {
                    string joined = string.Join(",", pieces[i..(j + 1)]).Trim();
                    if (File.Exists(Sfs.ResolveUserPath(joined)) || File.Exists(Path.Combine(dataPath, joined))) { end = j; break; }
                }
                string entry = string.Join(",", pieces[i..(end + 1)]).Trim();
                if (entry.Length > 0) result.Add(entry);
                i = end;
            }
            return result;
        }

        public static List<string> TargetResolver(string input, bool requireLiveEnv = false)
        {
            var targets = new List<string>();
            var lower = input.ToLowerInvariant();

            if (lower == "--cached")
                return Sfs.CachedPlugins();

            string dataPath = GetDataFolderPath();
            var allEsm = Directory.Exists(dataPath)
                ? Directory.GetFiles(dataPath)
                    .Where(x => x.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
                                x.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
                                x.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
                    .Select(x => Path.GetFileName(x)!).ToList()
                : new List<string>();

            if (lower == "--all") targets.AddRange(allEsm);
            else if (lower == "--allnods")
            {
                targets.AddRange(allEsm.Where(x =>
                {
                    if (x.IndexOf("darkstar", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                    var dbPath = GetCachePath(x);
                    if (File.Exists(dbPath))
                    {
                        try
                        {
                            using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
                            connection.Open();
                            using var cmd = connection.CreateCommand();
                            cmd.CommandText = "SELECT COUNT(1) FROM Masters WHERE MasterName COLLATE NOCASE = 'DarkStar.esm'";
                            var count = Convert.ToInt64(cmd.ExecuteScalar());
                            return count == 0;
                        }
                        catch { }
                    }

                    var p = Path.Combine(dataPath, x);
                    if (!File.Exists(p)) return true;
                    try
                    {
                        var readParams = new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryReadParameters { MasterFlagsLookup = new SimpleMasterFlagCache(), ThrowOnUnknownSubrecord = false };
                        var mod = Mutagen.Bethesda.Starfield.StarfieldMod.CreateFromBinaryOverlay(p, Mutagen.Bethesda.Starfield.StarfieldRelease.Starfield, readParams);
                        if (mod.ModHeader.MasterReferences.Any(m => m.Master.FileName.String.Equals("DarkStar.esm", StringComparison.OrdinalIgnoreCase))) return false;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Exception parsing {x}: {ex.Message}");
                    }
                    return true;
                }));
            }
            else if (lower == "--active" || lower == "--inactive")
            {
                var env = GameEnvironment.Typical.Starfield(StarfieldRelease.Starfield);
                var active = env.LoadOrder.ListedOrder.Select(x => x.ModKey.FileName.String).ToList();
                if (lower == "--active") targets.AddRange(active);
                else targets.AddRange(allEsm.Except(active, StringComparer.OrdinalIgnoreCase));
            }
            else if (lower == "--core")
            {
                var core = new[] { "Starfield.esm", "Constellation.esm", "OldMars.esm", "BlueprintShips-Starfield.esm", "ShatteredSpace.esm" };
                targets.AddRange(allEsm.Where(x => core.Contains(x, StringComparer.OrdinalIgnoreCase)));
            }
            else if (lower == "--official")
            {
                var core = new[] { "Starfield.esm", "Constellation.esm", "OldMars.esm", "BlueprintShips-Starfield.esm", "ShatteredSpace.esm", "kgcdoom.esm" };
                targets.AddRange(allEsm.Where(x =>
                    core.Contains(x, StringComparer.OrdinalIgnoreCase) ||
                    x.StartsWith("SFBGS", StringComparison.OrdinalIgnoreCase) ||
                    x.StartsWith("sfta", StringComparison.OrdinalIgnoreCase) ||
                    x.StartsWith("blueprintships-sfta", StringComparison.OrdinalIgnoreCase)
                ));
            }
            else
            {
                var pool = allEsm.Union(Sfs.CachedPlugins(), StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var p in SplitPluginList(input))
                {
                    string t = p.Trim();
                    if (t.IndexOfAny(new[] { '*', '?' }) >= 0)
                    {
                        string pattern = t;
                        if (!pattern.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) &&
                            !pattern.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) &&
                            !pattern.EndsWith(".esl", StringComparison.OrdinalIgnoreCase) &&
                            !pattern.EndsWith("*"))
                        {
                            pattern += "*";
                        }
                        var rx = new System.Text.RegularExpressions.Regex(
                            "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        targets.AddRange(pool.Where(x => rx.IsMatch(x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                        continue;
                    }

                    // A file path (or .bak): read that file into its own _file1_ cache, as Header/Stats/Lint do.
                    if (t.IndexOfAny(new[] { '\\', '/' }) >= 0 || t.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(Sfs.ResolveUserPath(t))) targets.Add(EnsureCacheForFile(t));
                        else Console.Error.WriteLine($"File not found: {t}");
                        continue;
                    }

                    if (!t.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) &&
                        !t.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) &&
                        !t.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(Path.Combine(dataPath, t + ".esm")) || Sfs.HasCache(t + ".esm")) t += ".esm";
                    }
                    targets.Add(t);
                }
            }
            return targets.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        static void ListDatabases()
        {
            if (!Directory.Exists(Sfs.DbFolder))
            {
                Console.WriteLine("No offline databases have been built yet. Use 'BuildCache <Target>' to create one.");
                return;
            }
            var files = Directory.GetFiles(Sfs.DbFolder, "*.db");
            if (files.Length == 0)
            {
                Console.WriteLine("No offline databases have been built yet. Use 'BuildCache <Target>' to create one.");
                return;
            }
            var t = new Sfs.Table("Database", "Size (MB)", "SchemaVersion", "MasterStyle", "CachedRecords");
            foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string name = Service.ServiceDb.PluginOf(file);
                double sizeMb = Math.Round(new FileInfo(file).Length / (1024.0 * 1024.0), 2);
                int ver = ReadCacheSchemaVersion(file);
                string style = "-", cnt = "-";
                try
                {
                    using var conn = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
                    conn.Open();
                    style = Sfs.Meta(conn, "MasterStyle") ?? "-";
                    cnt = Sfs.Meta(conn, "CachedRecordCount") ?? "-";
                }
                catch { }
                t.Add(name, sizeMb.ToString("0.00"), $"v{ver}", style, cnt);
            }
            Sfs.Print(t, annotate: false);
        }

        public static string GetCachePath(string target) => Path.Combine(Sfs.DbFolder, Sfs.DbFileName(target));

        static string? _dataFolderPath, _dataFolderHome;

        /// <summary>
        /// Where Starfield's Data folder is. In order: the SFSEARCH_DATA environment variable, the path remembered by SetData or
        /// an earlier run (Cache\DataPath.txt), then Mutagen's search for the game (registry, Steam, Xbox).
        /// </summary>
        static string GetDataFolderPath()
        {
            if (_dataFolderPath != null && _dataFolderHome == Sfs.HomeDir) return _dataFolderPath;
            string pathFile = Path.Combine(Sfs.HomeDir, "Cache", "DataPath.txt");
            string? fromEnv = Environment.GetEnvironmentVariable("SFSEARCH_DATA");
            string found;
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                if (!Directory.Exists(fromEnv.Trim())) throw new FileNotFoundException($"SFSEARCH_DATA points at a folder that doesn't exist: {fromEnv}");
                found = fromEnv.Trim();
            }
            else if (File.Exists(pathFile) && Directory.Exists(File.ReadAllText(pathFile).Trim())) found = File.ReadAllText(pathFile).Trim();
            else
            {
                if (File.Exists(pathFile)) Console.Error.WriteLine($"The remembered Data folder is gone ({File.ReadAllText(pathFile).Trim()}). Looking for Starfield again...");
                if (!Mutagen.Bethesda.Installs.GameLocations.TryGetDataFolder(GameRelease.Starfield, out var located) || !Directory.Exists(located.Path))
                    throw new FileNotFoundException("Couldn't find Starfield's Data folder. Tell SFSearch where it is, once:\n" +
                        "  sfs.cmd SetData \"C:\\Path\\To\\Starfield\\Data\"\n(or set the SFSEARCH_DATA environment variable to that folder).");
                found = located.Path;
                Directory.CreateDirectory(Path.GetDirectoryName(pathFile)!);
                File.WriteAllText(pathFile, found);
            }
            _dataFolderHome = Sfs.HomeDir;
            return _dataFolderPath = found;
        }

        static void SetDataFolderPath(string path)
        {
            string full = Sfs.ResolveUserPath(path.Trim().Trim('"'));
            if (!Directory.Exists(full)) { Console.Error.WriteLine($"That folder doesn't exist: {full}"); Environment.ExitCode = 2; return; }
            if (!File.Exists(Path.Combine(full, "Starfield.esm")))
                Console.Error.WriteLine($"Warning: no Starfield.esm in {full}. Saved anyway; check this is the game's Data folder.");
            Directory.CreateDirectory(Path.Combine(Sfs.HomeDir, "Cache"));
            File.WriteAllText(Path.Combine(Sfs.HomeDir, "Cache", "DataPath.txt"), full);
            _dataFolderPath = full; _dataFolderHome = Sfs.HomeDir;
            Console.WriteLine($"Data folder set to: {full}. If the SFSearch service is running, restart it: sfs.cmd Service restart");
        }

        /// <summary>
        /// Ensures a cache is built and up-to-date for either a plugin name in Data or an arbitrary file path on disk (e.g. .bak).
        /// Returns the cache key (used with GetCachePath / Sfs.OpenCache).
        /// </summary>
        public static string EnsureCacheForFile(string targetOrPath, bool isSecondArg = false)
        {
            string resolved = Sfs.ResolveUserPath(targetOrPath);
            string dataPath = GetDataFolderPath();

            // Check if the user supplied an explicit path to a file outside Data (or a .bak file)
            bool looksLikePath = targetOrPath.IndexOfAny(new[] { '\\', '/' }) >= 0
                              || targetOrPath.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                              || (File.Exists(resolved) && !File.Exists(Path.Combine(dataPath, targetOrPath)));

            if (looksLikePath && File.Exists(resolved))
            {
                string fileName = Path.GetFileName(resolved);
                string cacheKey = fileName.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
                                  fileName.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
                                  fileName.EndsWith(".esl", StringComparison.OrdinalIgnoreCase)
                    ? (isSecondArg ? $"_ file2_{fileName}".Replace(" ", "") : $"_file1_{fileName}")
                    : fileName + ".esm";

                string dbPath = GetCachePath(cacheKey);
                // In the service, one worker builds a given file's database at a time
                using var buildLock = SessionServer.Active ? new Service.ServiceDb.BuildLock(dbPath) : null;
                string? why = File.Exists(dbPath) ? CacheStaleReason(dbPath, resolved) : "not found";
                if (why != null)
                {
                    Console.Error.WriteLine($"Cache {cacheKey} {why}. Building it from {resolved}...");
                    var readParams = new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryReadParameters
                    {
                        MasterFlagsLookup = new SimpleMasterFlagCache(),
                        ThrowOnUnknownSubrecord = false
                    };
                    string modKeyName = fileName;
                    if (modKeyName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                        modKeyName = modKeyName.Substring(0, modKeyName.Length - 4);
                    var modKey = ModKey.FromNameAndExtension(modKeyName);
                    using var overlayMod = StarfieldMod.CreateFromBinaryOverlay(new ModPath(modKey, resolved), StarfieldRelease.Starfield, readParams);
                    BuildCacheForMod(overlayMod, overlayMod.ToUntypedImmutableLinkCache(), cacheKeyOverride: cacheKey, sourcePath: resolved);
                }
                return cacheKey;
            }

            string normTarget = targetOrPath;
            if (!normTarget.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) &&
                !normTarget.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) &&
                !normTarget.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(dataPath, normTarget + ".esm")) || Sfs.HasCache(normTarget + ".esm"))
                    normTarget += ".esm";
            }
            EnsureCacheUpToDate(normTarget);
            return normTarget;
        }

        static void EnsureCacheUpToDate(string target)
        {
            // In the agent session every use tops up the types this command needs
            if (SessionServer.Active && File.Exists(Path.Combine(GetDataFolderPath(), target))) { LiveDb.Build(target); return; }
            if (target.Equals("all", StringComparison.OrdinalIgnoreCase)) return;
            string dataPath = GetDataFolderPath();
            string esmPath = Path.Combine(dataPath, target);
            if (!esmPath.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) &&
                !esmPath.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) &&
                !esmPath.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(esmPath + ".esm"))
                {
                    esmPath += ".esm";
                    target += ".esm";
                }
            }
            string dbPath = GetCachePath(target);

            if (File.Exists(esmPath))
            {
                if (!File.Exists(dbPath))
                {
                    Console.Error.WriteLine($"Cache for {target} not found. Building automatically...");
                    BuildCache(target);
                }
                else if (CacheStaleReason(dbPath, esmPath) is string why)
                {
                    Console.Error.WriteLine($"Cache for {target} {why}. Rebuilding cache automatically...");
                    BuildCache(target);
                }
            }
            else if (File.Exists(dbPath))
            {
                // A cache built from a file path (_file1_ / _file2_ key): re-check it against the file it was built from.
                if (target.StartsWith("_file", StringComparison.OrdinalIgnoreCase) && CacheSourcePath(target, dbPath) is string src)
                    EnsureCacheForFile(src, isSecondArg: target.StartsWith("_file2_", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                Console.Error.WriteLine($"Warning: {target} not found in Data folder, and no cache exists.");
            }
        }

        // Bump whenever BuildCacheForMod / FlattenValue output changes so existing caches are rebuilt automatically.
        // v2: ArmorAddon + InstanceNamingRules cached, Meta table, Name column stripped of <ID=xxxxxxxx> tags.
        // v3: Clean FormLinks (no <...Getter> suffix), Origin/IsOverride columns, Refs table, SignatureCounts table, ordered Masters, Header metadata in Meta.
        // v4: ConditionRecord/Resource/ResearchProject/LegendaryItem/Book/Furniture/Container/Message/Terminal/TerminalMenu cached; condition data emits "Function" (CTDA type).
        // v4 (additive, no bump): Forms table (FormKey, Signature, EditorID, IsOverride, Base) for every record type. Older v4 caches
        // simply lack it; Query rebuilds a cache once when its SQL mentions Forms (no mass rebuild of every cache).
        // 2026-10-22 (no bump): the list of cached types is stored in Meta "CachedTypes" and checked on every use (CachedTypesFingerprint),
        // so a type-list change rebuilds old caches by itself. Forms gained Parent (cell of a placed reference) and HasScripts.
        const int CacheSchemaVersion = 5;

        /// <summary>Record types stored in full in Records (JSON Data + Refs). Every other type only gets a Forms row.</summary>
        static readonly HashSet<string> CachedSignatures = new(StringComparer.OrdinalIgnoreCase)
        {
            "Weapon", "Armor", "ObjectMod", "ObjectModification", "WeaponModification", "ArmorModification", "Keyword", "ConstructibleObject",
            "FormList", "LeveledItem", "LeveledNpc", "Npc", "Faction", "Race", "Class", "Outfit", "MiscItem", "Ammunition", "Ingestible",
            "Perk", "Spell", "MagicEffect", "Quest", "ObjectEffect", "Enchantment", "DamageType", "ActorValueInformation", "Projectile", "Explosion", "Global", "Component",
            "ArmorAddon", "InstanceNamingRules", "ConditionRecord", "Resource", "ResearchProject", "LegendaryItem", "Book", "Furniture", "Container", "Message", "Terminal", "TerminalMenu",
            // 2026-10-22: script properties (Codex machine, Void device), combat styles, game settings, limb data, aim models,
            // DarkStar's gameplay options, default objects, weapon colour/skin modulations, morphs, equip slots.
            "Activator", "CombatStyle", "GameSettingFloat", "GameSettingInt", "GameSettingUInt", "GameSettingBool", "GameSettingString",
            "BodyPartData", "AimModel", "GameplayOption", "GameplayOptionsGroup", "DefaultObject", "ActorValueModulation", "MorphableObject", "EquipType",
            // 2026-11-01 (CACHE_GAPS.md): pack-ins and moveable statics carry scripts and keywords of their own. Without them `Refs`
            // missed DarkStar's AtmosphereGen script on its landing-pad pack-ins.
            "PackIn", "MoveableStatic",
            // 2026-11-04 (CACHE_GAPS.md): locations (encounter levels, parent location), ships and their spawn lists.
            "Location", "GenericBaseForm", "LeveledBaseForm"
        };

        /// <summary>
        /// Placed references. A plugin with at most FullPlacedLimit of them caches them all in full (mods, darkstar.esm, DSM);
        /// a bigger one (Starfield.esm 3.3 M, ShatteredSpace.esm 0.9 M, nfvc0003vigilance.esm 168 k) caches only those that carry
        /// scripts, except the types in AlwaysFullPlaced. Forms always gets every reference with its cell (Parent) and a HasScripts flag.
        /// </summary>
        static readonly HashSet<string> PlacedSignatures = new(StringComparer.OrdinalIgnoreCase) { "PlacedObject", "PlacedNpc" };
        const int FullPlacedLimit = 50000;

        /// <summary>
        /// Placed types cached in full even over FullPlacedLimit: there are few of them (Starfield.esm 9,530 PlacedNpc next to
        /// 3.3 M PlacedObject) and their flags matter (Starts Dead, used by the TGO corpse check). Added 2026-11-01.
        /// </summary>
        static readonly HashSet<string> AlwaysFullPlaced = new(StringComparer.OrdinalIgnoreCase) { "PlacedNpc" };

        /// <summary>
        /// GenericBaseForm holds whole ships and also ship-builder parts, which share the type. The parts are named SM + letters + _
        /// (SMA_, SMB_, SMC_, SML_, SMM_, SMS_, SMX_, SMY_, SMVS_), have no pilot, and get only a Forms row: darkstar.esm has 14,059 of
        /// them, which would nearly double its cache (112 MB instead of 198 MB). Digits stay out of the pattern on purpose, because SM015_DeimosStaryard, SM016_,
        /// SM018_ and SM020_TheDen in Starfield.esm are real stations with pilots. Added 2026-11-04.
        /// </summary>
        const string ShipPartNamePattern = "^SM[A-Z]{1,3}_";
        static readonly System.Text.RegularExpressions.Regex ShipPartName = new(ShipPartNamePattern, System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// Stored in Meta "CachedTypes". A cache built with a different type list (or placed-reference rule) is rebuilt
        /// automatically, so adding a type no longer leaves old caches without it.
        /// </summary>
        // Plus the item filter's settings when they differ from the defaults: the junk flags and groups it decides are stored
        static string CachedTypesFingerprint => BaseTypesFingerprint + Service.ServiceConfig.Load(Sfs.HomeDir).ItemFilter.FingerprintPart();

        static readonly string BaseTypesFingerprint =
            string.Join(",", CachedSignatures.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            + $";placed={string.Join(",", PlacedSignatures.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))}:full<={FullPlacedLimit},else scripted"
            + $";alwaysfull={string.Join(",", AlwaysFullPlaced.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))}"
            + $";shipparts={ShipPartNamePattern}"
            + ";walk=2" // 2026-11-04: WalkRecords. Rebuilds caches the old walk cut short.
            + ";mutagen=fixes.5"  // 2026-11-05: new field names, kept text IDs. Rebuilds caches once, as they are used.
            + ";values=2";        // 2026-11-06: exact percents and times, hex bytes, grids and dictionaries as data, list slots kept, every record's parent.

        static int ReadCacheSchemaVersion(string dbPath)
        {
            string? v = ReadCacheMeta(dbPath, "SchemaVersion");
            return v != null && int.TryParse(v, out int n) ? n : 0; // Pre-versioning caches have no Meta table
        }

        internal static string? ReadCacheMetaInternal(string dbPath, string key) => ReadCacheMeta(dbPath, key);
        static string? ReadCacheMeta(string dbPath, string key)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Value FROM Meta WHERE Key = @k";
                cmd.Parameters.AddWithValue("@k", key);
                return cmd.ExecuteScalar()?.ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True when a cache was built by this serializer (schema version) with this list of cached types.</summary>
        static bool IsCacheFormatCurrent(string dbPath)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Key, Value FROM Meta WHERE Key IN ('SchemaVersion', 'CachedTypes')";
                string? ver = null, types = null;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.GetString(0) == "SchemaVersion") ver = r.GetString(1);
                    else types = r.GetString(1);
                }
                return ver == CacheSchemaVersion.ToString() && types == CachedTypesFingerprint;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Which copy of a plugin a cache was built from: "size|last write (UTC)|full path", stored in Meta "SourceStamp".
        /// A cache is fresh only while this matches exactly. Comparing dates alone failed when a mod manager installed an
        /// update with its archive's (older) file date: SpacesuitPack 1.8 over a 1.5 cache, 2026-10-25.
        /// </summary>
        static string SourceStamp(string path)
        {
            var fi = new FileInfo(path);
            return $"{fi.Length}|{fi.LastWriteTimeUtc:o}|{fi.FullName}";
        }

        /// <summary>Null when the cache is current for this plugin file; otherwise why it must be rebuilt (for the message).</summary>
        static string? CacheStaleReason(string dbPath, string sourcePath)
        {
            if (ReadCacheSchemaVersion(dbPath) != CacheSchemaVersion) return "was built by an older SFSearch serializer";
            if (ReadCacheMeta(dbPath, "CachedTypes") != CachedTypesFingerprint) return "was built with different settings (the record types it stores, or sfsearch.json's item filter)";
            if (ReadCacheMeta(dbPath, "MissingMasters")?.Split(',').FirstOrDefault(m => File.Exists(Path.Combine(GetDataFolderPath(), m))) is string back)
                return $"was built while its master {back} was missing, and it's in Data now";
            string? stamp = ReadCacheMeta(dbPath, "SourceStamp");
            if (stamp == null) return "has no record of which copy of the plugin it was built from (built before 2026-10-25)";
            if (!stamp.Equals(SourceStamp(sourcePath), StringComparison.OrdinalIgnoreCase)) return "was built from a different copy of the plugin (size, date or folder changed)";
            return null;
        }

        /// <summary>The plugin file a cache can be checked against: the Data folder copy, else the path in its stamp (null when neither exists).</summary>
        internal static string? CacheSourcePath(string target, string dbPath)
        {
            string inData = Path.Combine(GetDataFolderPath(), target);
            if (File.Exists(inData)) return inData;
            string? stamped = ReadCacheMeta(dbPath, "SourceStamp")?.Split('|').ElementAtOrDefault(2);
            return stamped != null && File.Exists(stamped) ? stamped : null;
        }

        static readonly System.Text.RegularExpressions.Regex StringIdTagRegex = new(@"^<ID=[0-9A-Fa-f]+>", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Removes the Creation Kit translation tag (e.g. "&lt;ID=0000835D&gt;") that prefixes some plugin strings.</summary>
        internal static string StripStringIdTagInternal(string? s) => StripStringIdTag(s);
        static string StripStringIdTag(string? s) => string.IsNullOrEmpty(s) ? "" : StringIdTagRegex.Replace(s, "");

        static void ExecuteNonQuery(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        // What LiveDb (--live and the agent session) needs from here
        internal static readonly HashSet<string> AllStoredTypes = new(CachedSignatures.Concat(PlacedSignatures), StringComparer.OrdinalIgnoreCase);
        internal static HashSet<string> PlacedTypes => PlacedSignatures;
        internal static bool StoredType(string sig) => CachedSignatures.Contains(sig);
        internal static bool IsShipPart(IMajorRecordGetter rec) => rec.Registration.Name.Equals("GenericBaseForm", StringComparison.OrdinalIgnoreCase) && rec.EditorID is { } n && ShipPartName.IsMatch(n);
        internal static int PlacedLimit => FullPlacedLimit;
        internal static bool AlwaysFull(string sig) => AlwaysFullPlaced.Contains(sig);
        internal static int SchemaVersionPublic => CacheSchemaVersion;
        internal static string FingerprintPublic => CachedTypesFingerprint;
        internal static string SourceStampPublic(string path) => SourceStamp(path);
        internal static string TypeNameOf(string sig4)
        {
            if (sig4 == "REFR") return "PlacedObject";
            if (sig4 == "ACHR") return "PlacedNpc";
            var g = HeaderScanner.GetterFor(sig4);
            if (g == null) return sig4;
            string n = g.Name;
            if (n.StartsWith("I")) n = n[1..];
            if (n.EndsWith("Getter")) n = n[..^6];
            return n;
        }

        public static void BuildCache(string target)
        {
            if (LiveDb.Enabled) { LiveDb.Build(target); return; }
            BuildCaches(new List<string> { target });
        }

        public static void BuildCaches(IEnumerable<string> targetsToBuild)
        {
            if (LiveDb.Enabled) { foreach (var t in targetsToBuild) LiveDb.Build(t); return; }
            Directory.CreateDirectory("Cache");
            Sfs.CloseResolver();

            var env = GameEnvironment.Typical.Starfield(StarfieldRelease.Starfield);

            foreach (var target in targetsToBuild)
            {
                var modListing = env.LoadOrder.ListedOrder.FirstOrDefault(x => x.ModKey.FileName.String.Equals(target, StringComparison.OrdinalIgnoreCase));
                if (modListing != null && modListing.Mod != null)
                {
                    BuildCacheForMod(modListing.Mod, env.LinkCache, sourcePath: Path.Combine(env.DataFolderPath, modListing.ModKey.FileName.String));
                    continue;
                }
                string esmPath = Path.Combine(env.DataFolderPath, target);
                if (File.Exists(esmPath))
                {
                    var readParams = new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryReadParameters { MasterFlagsLookup = new SimpleMasterFlagCache(), ThrowOnUnknownSubrecord = false };
                    using var overlayMod = Mutagen.Bethesda.Starfield.StarfieldMod.CreateFromBinaryOverlay(esmPath, StarfieldRelease.Starfield, readParams);
                    BuildCacheForMod(overlayMod, env.LinkCache, sourcePath: esmPath);
                    continue;
                }
                Console.Error.WriteLine($"Error: {target} not found in load order or Data folder.");
            }
        }

        /// <summary>
        /// Every major record of a plugin. Mutagen's own walk ends at the first record it can't read, which would silently lose the
        /// rest of the plugin. When that happens, the top-level groups are read record by record and the placed references type by
        /// type, so one unreadable record costs only itself. Each problem is added to <paramref name="problems"/>.
        /// </summary>
        internal static IEnumerable<IMajorRecordGetter> WalkRecordsInternal(IModGetter mod, List<string> problems) => WalkRecords(mod, problems);
        static IEnumerable<IMajorRecordGetter> WalkRecords(IModGetter mod, List<string> problems)
        {
            static string Why(Exception ex) => (ex.InnerException ?? ex).Message.Replace('\n', ' ').Replace('\r', ' ');
            var seen = new HashSet<FormKey>();
            string walkEnded;
            using (var main = mod.EnumerateMajorRecords().GetEnumerator())
            {
                while (true)
                {
                    IMajorRecordGetter? rec;
                    try
                    {
                        if (!main.MoveNext()) yield break;
                        rec = main.Current;
                    }
                    catch (Exception ex) { walkEnded = Why(ex); break; }
                    if (rec != null) { seen.Add(rec.FormKey); yield return rec; }
                }
            }

            var groups = mod.GetType().GetProperties().Where(p => typeof(IGroupGetter).IsAssignableFrom(p.PropertyType));
            foreach (var prop in groups)
            {
                IGroupGetter? group = null;
                List<FormKey> keys = new();
                try
                {
                    group = prop.GetValue(mod) as IGroupGetter;
                    if (group != null) keys = group.FormKeys.ToList();
                }
                catch (Exception ex) { problems.Add($"{prop.Name}: {Why(ex)}"); continue; }
                if (group == null) continue;
                foreach (var key in keys)
                {
                    if (seen.Contains(key)) continue;
                    IMajorRecordGetter? rec = null;
                    try { rec = group[key]; }
                    catch (Exception ex) { problems.Add($"{prop.Name} {key}: {Why(ex)}"); }
                    if (rec != null && seen.Add(key)) yield return rec;
                }
            }

            foreach (var type in new[] { typeof(ICellGetter), typeof(IPlacedObjectGetter), typeof(IPlacedNpcGetter) })
            {
                using var e = mod.EnumerateMajorRecords(type).GetEnumerator();
                while (true)
                {
                    IMajorRecordGetter? rec;
                    try
                    {
                        if (!e.MoveNext()) break;
                        rec = e.Current;
                    }
                    catch (Exception ex) { problems.Add($"{type.Name}: {Why(ex)}"); break; }
                    if (rec != null && seen.Add(rec.FormKey)) yield return rec;
                }
            }

            // The record that ended the main walk normally shows up again above. If it didn't, the walk's own error is the only clue.
            if (problems.Count == 0) problems.Add(walkEnded);
        }

        static void BuildCacheForMod(IModGetter mod, Mutagen.Bethesda.Plugins.Cache.ILinkCache linkCache, string? cacheKeyOverride = null, string? sourcePath = null)
        {
            string modFileName = mod.ModKey.FileName.String;
            string cacheKey = cacheKeyOverride ?? modFileName;
            // Taken before reading, so a file replaced mid-build is caught by the next freshness check.
            sourcePath ??= Path.Combine(GetDataFolderPath(), modFileName);
            string? sourceStamp = File.Exists(sourcePath) ? SourceStamp(sourcePath) : null;

            Sfs.CloseResolver();
            string dbPath = GetCachePath(cacheKey);
            var typesToAddBack = File.Exists(dbPath) ? AddCommand.ReadAddedTypes(dbPath) : new List<string>();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            foreach (var side in new[] { dbPath + "-wal", dbPath + "-shm" }) if (File.Exists(side)) File.Delete(side);

            using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            connection.Open();

            ExecuteNonQuery(connection, "PRAGMA journal_mode = WAL;");
            ExecuteNonQuery(connection, "PRAGMA synchronous = NORMAL;");

            ExecuteNonQuery(connection, @"
                CREATE TABLE Records (
                    FormKey TEXT PRIMARY KEY,
                    Signature TEXT,
                    EditorID TEXT,
                    Name TEXT,
                    Origin TEXT,
                    IsOverride INTEGER,
                    Data TEXT
                );
                CREATE TABLE Masters (
                    Idx INTEGER,
                    MasterName TEXT PRIMARY KEY
                );
                CREATE TABLE Meta (
                    Key TEXT PRIMARY KEY,
                    Value TEXT
                );
                CREATE TABLE SignatureCounts (
                    Signature TEXT PRIMARY KEY,
                    TotalCount INTEGER,
                    OverrideCount INTEGER,
                    Cached INTEGER
                );
                CREATE TABLE Refs (
                    FromFormKey TEXT,
                    FromSignature TEXT,
                    FromEditorID TEXT,
                    Path TEXT,
                    ToFormKey TEXT
                );
                CREATE TABLE Forms (
                    FormKey TEXT PRIMARY KEY,
                    Signature TEXT,
                    EditorID TEXT,
                    IsOverride INTEGER,
                    Base TEXT,
                    Parent TEXT,
                    HasScripts INTEGER
                );
            ");
            ExecuteNonQuery(connection, "CREATE INDEX idx_signature ON Records(Signature);");
            ExecuteNonQuery(connection, "CREATE INDEX idx_editorid ON Records(EditorID);");
            ExecuteNonQuery(connection, "CREATE INDEX idx_isoverride ON Records(IsOverride);");
            ExecuteNonQuery(connection, "CREATE INDEX idx_refs_to ON Refs(ToFormKey);");
            ExecuteNonQuery(connection, "CREATE INDEX idx_refs_from ON Refs(FromFormKey);");
            ExecuteNonQuery(connection, "CREATE INDEX idx_forms_sig ON Forms(Signature);");
            ExecuteNonQuery(connection, "CREATE INDEX idx_forms_base ON Forms(Base);");
            ExecuteNonQuery(connection, "CREATE INDEX idx_forms_parent ON Forms(Parent);");

            int count = 0;
            int totalRecords = 0;
            int totalOverrides = 0;
            uint maxLocalFormId = 0;
            var sigCounts = new Dictionary<string, (int Total, int Overrides)>(StringComparer.OrdinalIgnoreCase);

            var validSignatures = CachedSignatures;

            // Cell of every placed reference (cells list their references; the references don't name their cell).
            // The count also decides whether this plugin's placed references are cached in full (see PlacedSignatures).
            var parentCell = new Dictionary<FormKey, FormKey>();
            try
            {
                foreach (var cell in mod.EnumerateMajorRecords<ICellGetter>())
                {
                    foreach (var placed in cell.Persistent) parentCell[placed.FormKey] = cell.FormKey;
                    foreach (var placed in cell.Temporary) parentCell[placed.FormKey] = cell.FormKey;
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[sfs] Could not read the cells of {modFileName}: {ex.Message}"); }
            bool fullPlaced = parentCell.Count <= FullPlacedLimit;
            var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try { if (File.Exists(sourcePath)) parents = RawParents(sourcePath); }
            catch (Exception ex) { Console.Error.WriteLine($"[sfs] Could not read the groups of {modFileName}: {ex.Message}"); }
            TooDeepValues = 0;
            var cachedPlaced = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int skippedShipParts = 0;

            var readProblems = new List<string>();
            using var enumerator = WalkRecords(mod, readProblems).GetEnumerator();

            using var transaction = connection.BeginTransaction();
            using var insertCmd = connection.CreateCommand();
            insertCmd.CommandText = "INSERT INTO Records (FormKey, Signature, EditorID, Name, Origin, IsOverride, Data) VALUES (@formKey, @sig, @edid, @name, @origin, @isOvr, @data)";
            var pFormKey = insertCmd.Parameters.Add("@formKey", SqliteType.Text);
            var pSig = insertCmd.Parameters.Add("@sig", SqliteType.Text);
            var pEdid = insertCmd.Parameters.Add("@edid", SqliteType.Text);
            var pName = insertCmd.Parameters.Add("@name", SqliteType.Text);
            var pOrigin = insertCmd.Parameters.Add("@origin", SqliteType.Text);
            var pIsOvr = insertCmd.Parameters.Add("@isOvr", SqliteType.Integer);
            var pData = insertCmd.Parameters.Add("@data", SqliteType.Text);

            using var refCmd = connection.CreateCommand();
            refCmd.CommandText = "INSERT INTO Refs (FromFormKey, FromSignature, FromEditorID, Path, ToFormKey) VALUES (@ffk, @fsig, @fedid, @path, @tfk)";
            var pRefFromFk = refCmd.Parameters.Add("@ffk", SqliteType.Text);
            var pRefFromSig = refCmd.Parameters.Add("@fsig", SqliteType.Text);
            var pRefFromEdid = refCmd.Parameters.Add("@fedid", SqliteType.Text);
            var pRefPath = refCmd.Parameters.Add("@path", SqliteType.Text);
            var pRefToFk = refCmd.Parameters.Add("@tfk", SqliteType.Text);

            var refBuffer = new List<(string Path, string ToFormKey)>();

            // Forms: one row per major record of every type (also the uncached ones such as PlacedObject, Cell,
            // MorphableObject, ActorValueModulation), so FormID clashes and placed references can be checked.
            using var formCmd = connection.CreateCommand();
            formCmd.CommandText = "INSERT OR IGNORE INTO Forms (FormKey, Signature, EditorID, IsOverride, Base, Parent, HasScripts) VALUES (@fk, @sig, @edid, @ovr, @base, @parent, @scripts)";
            var pFormFk = formCmd.Parameters.Add("@fk", SqliteType.Text);
            var pFormSig = formCmd.Parameters.Add("@sig", SqliteType.Text);
            var pFormEdid = formCmd.Parameters.Add("@edid", SqliteType.Text);
            var pFormOvr = formCmd.Parameters.Add("@ovr", SqliteType.Integer);
            var pFormBase = formCmd.Parameters.Add("@base", SqliteType.Text);
            var pFormParent = formCmd.Parameters.Add("@parent", SqliteType.Text);
            var pFormScripts = formCmd.Parameters.Add("@scripts", SqliteType.Integer);
            var basePropCache = new Dictionary<Type, System.Reflection.PropertyInfo?>();

            while (true)
            {
                if (!enumerator.MoveNext()) break;
                IMajorRecordGetter? rec = enumerator.Current;
                if (rec == null) continue;

                string sig = rec.Registration.Name;
                string origin = Sfs.CanonicalPluginName(rec.FormKey.ModKey.FileName.String);
                bool isOverride = !origin.Equals(modFileName, StringComparison.OrdinalIgnoreCase);

                totalRecords++;
                if (isOverride) totalOverrides++;
                else if (rec.FormKey.ID > maxLocalFormId) maxLocalFormId = rec.FormKey.ID;

                sigCounts.TryGetValue(sig, out var sc);
                sigCounts[sig] = (sc.Total + 1, sc.Overrides + (isOverride ? 1 : 0));

                bool hasScripts = rec is IHaveVirtualMachineAdapterGetter vmHolder && vmHolder.VirtualMachineAdapter is { } vmad && vmad.Scripts.Count > 0;
                bool isPlaced = PlacedSignatures.Contains(sig);

                try
                {
                    // Placed references (PlacedObject, PlacedNpc, ...) expose their base object as "Base".
                    var recType = rec.GetType();
                    if (!basePropCache.TryGetValue(recType, out var baseProp))
                    {
                        baseProp = recType.GetProperty("Base");
                        if (baseProp != null && !typeof(IFormLinkGetter).IsAssignableFrom(baseProp.PropertyType)) baseProp = null;
                        basePropCache[recType] = baseProp;
                    }
                    string? baseFk = null;
                    if (baseProp?.GetValue(rec) is IFormLinkGetter baseLink && !baseLink.FormKey.IsNull)
                        baseFk = Sfs.NormalizeFormKey(baseLink.FormKey.ToString());
                    pFormFk.Value = Sfs.NormalizeFormKey(rec.FormKey.ToString());
                    pFormSig.Value = sig;
                    pFormEdid.Value = string.IsNullOrEmpty(rec.EditorID) ? DBNull.Value : rec.EditorID;
                    pFormOvr.Value = isOverride ? 1 : 0;
                    pFormBase.Value = (object?)baseFk ?? DBNull.Value;
                    pFormParent.Value = parents.TryGetValue(Sfs.NormalizeFormKey(rec.FormKey.ToString()), out var parentFk) ? parentFk : parentCell.TryGetValue(rec.FormKey, out var cellFk) ? Sfs.NormalizeFormKey(cellFk.ToString()) : DBNull.Value;
                    pFormScripts.Value = hasScripts ? 1 : 0;
                    formCmd.ExecuteNonQuery();
                }
                catch { }

                if (isPlaced)
                {
                    if (!fullPlaced && !hasScripts && !AlwaysFullPlaced.Contains(sig)) continue;
                    cachedPlaced[sig] = cachedPlaced.GetValueOrDefault(sig) + 1;
                }
                else if (!validSignatures.Contains(sig)) continue;
                else if (sig.Equals("GenericBaseForm", StringComparison.OrdinalIgnoreCase) && rec.EditorID is { } partName && ShipPartName.IsMatch(partName))
                {
                    skippedShipParts++;
                    continue;
                }

                string fkStr =Sfs.NormalizeFormKey(rec.FormKey.ToString());
                string edid = !string.IsNullOrEmpty(rec.EditorID) ? rec.EditorID : fkStr;
                var obj = RecordToJson(rec, linkCache, out string name);

                pFormKey.Value = fkStr;
                pSig.Value = sig;
                pEdid.Value = edid;
                pName.Value = StripStringIdTag(name);
                pOrigin.Value = origin;
                pIsOvr.Value = isOverride ? 1 : 0;
                pData.Value = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

                insertCmd.ExecuteNonQuery();

                // Extract outbound FormKey references
                refBuffer.Clear();
                foreach (var kvp in obj)
                {
                    if (kvp.Key == "Record Header" || kvp.Key == "EDID - Editor ID" || kvp.Key == "Signature") continue;
                    CollectRefsFromJson(kvp.Value, kvp.Key, refBuffer);
                }
                if (refBuffer.Count > 0)
                {
                    pRefFromFk.Value = fkStr;
                    pRefFromSig.Value = sig;
                    pRefFromEdid.Value = edid;
                    foreach (var (rPath, toFk) in refBuffer)
                    {
                        pRefPath.Value = rPath;
                        pRefToFk.Value = toFk;
                        refCmd.ExecuteNonQuery();
                    }
                }

                count++;
                if (count % 10000 == 0) Console.Error.WriteLine($"Parsed {count} records...");
            }

            using (var masterInsertCmd = connection.CreateCommand())
            {
                masterInsertCmd.CommandText = "INSERT OR IGNORE INTO Masters (Idx, MasterName) VALUES (@idx, @mname)";
                var pMIdx = masterInsertCmd.Parameters.Add("@idx", SqliteType.Integer);
                var pMName = masterInsertCmd.Parameters.Add("@mname", SqliteType.Text);
                int idx = 0;
                foreach (var m in mod.MasterReferences)
                {
                    pMIdx.Value = idx++;
                    pMName.Value = m.Master.FileName.String;
                    masterInsertCmd.ExecuteNonQuery();
                }
            }

            using (var sigCmd = connection.CreateCommand())
            {
                sigCmd.CommandText = "INSERT INTO SignatureCounts (Signature, TotalCount, OverrideCount, Cached) VALUES (@s, @t, @o, @c)";
                var ps = sigCmd.Parameters.Add("@s", SqliteType.Text);
                var pt = sigCmd.Parameters.Add("@t", SqliteType.Integer);
                var po = sigCmd.Parameters.Add("@o", SqliteType.Integer);
                var pc = sigCmd.Parameters.Add("@c", SqliteType.Integer);
                foreach (var kvp in sigCounts)
                {
                    ps.Value = kvp.Key;
                    pt.Value = kvp.Value.Total;
                    po.Value = kvp.Value.Overrides;
                    // 1 = every record cached, 2 = some (placed references with scripts in a big plugin, ships without their parts), 0 = none (Forms only)
                    int cachedN = cachedPlaced.GetValueOrDefault(kvp.Key);
                    pc.Value = kvp.Key.Equals("GenericBaseForm", StringComparison.OrdinalIgnoreCase) && skippedShipParts > 0
                        ? (skippedShipParts == kvp.Value.Total ? 0 : 2)
                        : validSignatures.Contains(kvp.Key) || (cachedN > 0 && cachedN == kvp.Value.Total) ? 1 : cachedN > 0 ? 2 : 0;
                    sigCmd.ExecuteNonQuery();
                }
            }

            string masterStyle = "Full";
            string headerFlags = "";
            string author = "";
            string description = "";
            if (mod is IStarfieldModGetter sfMod)
            {
                var flags = sfMod.ModHeader.Flags;
                headerFlags = flags.ToString();
                if (flags.HasFlag(StarfieldModHeader.HeaderFlag.Light)) masterStyle = "Light (Small)";
                else if (flags.HasFlag(StarfieldModHeader.HeaderFlag.Medium)) masterStyle = "Medium";
                author = sfMod.ModHeader.Author ?? "";
                description = sfMod.ModHeader.Description ?? "";
            }

            void SetMeta(string k, string v)
            {
                using var mcmd = connection.CreateCommand();
                mcmd.CommandText = "INSERT OR REPLACE INTO Meta (Key, Value) VALUES (@k, @v)";
                mcmd.Parameters.AddWithValue("@k", k);
                mcmd.Parameters.AddWithValue("@v", v);
                mcmd.ExecuteNonQuery();
            }

            SetMeta("SchemaVersion", CacheSchemaVersion.ToString());
            SetMeta("CachedTypes", CachedTypesFingerprint);
            if (sourceStamp != null) SetMeta("SourceStamp", sourceStamp);
            SetMeta("PlacedMode", fullPlaced ? $"full ({parentCell.Count} placed references)"
                : $"scripted only, except {string.Join(", ", AlwaysFullPlaced)} in full ({parentCell.Count} placed references)");
            SetMeta("FileName", modFileName);
            SetMeta("MasterStyle", masterStyle);
            var missingMasters = mod.MasterReferences.Select(m => m.Master.FileName.String).Where(m => !File.Exists(Path.Combine(GetDataFolderPath(), m))).ToList();
            if (missingMasters.Count > 0) SetMeta("MissingMasters", string.Join(",", missingMasters));
            SetMeta("HeaderFlags", headerFlags);
            SetMeta("Author", author);
            SetMeta("Description", description);
            SetMeta("RecordCount", totalRecords.ToString());
            if (readProblems.Count > 0)
            {
                SetMeta("ReadProblems", readProblems.Count.ToString());
                SetMeta("ReadProblemSample", readProblems[0]);
                Console.Error.WriteLine($"[sfs] WARNING: {readProblems.Count} problem(s) reading {modFileName}, so its cache is missing what couldn't be read. First: {readProblems[0]}");
            }
            SetMeta("CachedRecordCount", count.ToString());
            if (TooDeepValues > 0)
            {
                SetMeta("TooDeepValues", TooDeepValues.ToString());
                Console.Error.WriteLine($"[sfs] WARNING: {TooDeepValues} value(s) in {modFileName} were nested too deep and were stored as plain text.");
            }
            SetMeta("OverrideCount", totalOverrides.ToString());
            SetMeta("MaxLocalFormID", $"0x{maxLocalFormId:X6}");

            transaction.Commit();
            // WAL only speeds up the build. Fold it back into the .db and leave the cache in plain (rollback journal) mode:
            // a leftover -wal file must be re-indexed by every later connection (128 ms per open with a 1.2 GB Starfield.esm WAL).
            ExecuteNonQuery(connection, "PRAGMA journal_mode = DELETE;");
            Console.Error.WriteLine($"Built SQLite cache (schema v{CacheSchemaVersion}) with {count}/{totalRecords} records at {GetCachePath(cacheKey)}");
            if (typesToAddBack.Count > 0)
            {
                transaction.Dispose();
                connection.Close();
                Console.Error.WriteLine($"Adding back {typesToAddBack.Count} type(s) added to this database earlier...");
                AddCommand.AddTypes(dbPath, mod, linkCache, typesToAddBack);
            }
        }

        /// <summary>
        /// The JSON a cache stores in Records.Data for one record. With full = true (Live) nothing is skipped (face morphs,
        /// NAM5/ONA2, W*Unknown fields) and a property that throws shows as "&lt;error: ...&gt;" instead of being left out.
        /// </summary>
        internal static JsonObject RecordToJson(IMajorRecordGetter rec, Mutagen.Bethesda.Plugins.Cache.ILinkCache linkCache, out string name, bool full = false, bool skipChildren = false)
        {
            string sig = rec.Registration.Name;
            string fkStr = Sfs.NormalizeFormKey(rec.FormKey.ToString());
            string edid = !string.IsNullOrEmpty(rec.EditorID) ? rec.EditorID : fkStr;

            var obj = new JsonObject();
            obj["Record Header"] = $"[{sig}] {edid} [{fkStr}]";
            obj["EDID - Editor ID"] = edid;
            obj["Signature"] = sig;

            name = "";
            if (rec is IWeaponGetter wpn)
            {
                obj["IsJunk"] = MetadataExtractor.IsJunkWeapon(wpn, linkCache);
                obj["Archetype"] = MetadataExtractor.GetWeaponGroup(wpn);
                name = wpn.Name?.String ?? "";
            }
            else if (rec is IArmorGetter arm)
            {
                obj["IsJunk"] = MetadataExtractor.IsJunkArmor(arm);
                obj["Archetype"] = MetadataExtractor.GetArmorArchetype(arm);
                name = arm.Name?.String ?? "";
            }
            else if (rec is Mutagen.Bethesda.Plugins.Aspects.ITranslatedNamedGetter named)
            {
                name = named.Name?.String ?? "";
            }

            _captureFlattenErrors = full;
            _languageAsObject = full && !skipChildren;
            var props = rec.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            foreach (var prop in props)
            {
                if (prop.Name == "EditorID" || prop.Name == "FormKey" || prop.Name == "Signature") continue;
                if (!full && IsCacheSkippedProperty(prop.Name)) continue;
                if (skipChildren && IsChildRecords(prop)) continue;
                if (rec is IAObjectModificationGetter && MisreadOmodProperties.Contains(prop.Name)) continue;

                try
                {
                    var val = prop.GetValue(rec);
                    if (val != null)
                    {
                        var flattened = FlattenValue(val);
                        if (flattened != null)
                        {
                            if (flattened is string s && s != "NullReference [00000000]") obj[prop.Name] = s;
                            else if (flattened is JsonArray a) obj[prop.Name] = a;
                            else if (flattened is JsonObject o) obj[prop.Name] = o;
                        }
                    }
                }
                catch (Exception ex) { if (full) obj[prop.Name] = $"<error: {(ex.InnerException ?? ex).Message}>"; }
            }
            _captureFlattenErrors = false;
            return obj;
        }

        [ThreadStatic] static bool _captureFlattenErrors;
        [ThreadStatic] static bool _languageAsObject;

        /// <summary>Values nested too deep to store as data. Should stay 0; a build warns if it doesn't.</summary>
        internal static long TooDeepValues;

        static bool IsKeyValueType(Type t)
        {
            var def = t.GetGenericTypeDefinition();
            if (def == typeof(KeyValuePair<,>)) return true;
            return t.Namespace == "Noggog" && t.Name.StartsWith("KeyValue", StringComparison.Ordinal);
        }

        /// <summary>Records that sit under another one (a cell's references, a worldspace's cells, a topic's responses). `Add` gives them their own rows.</summary>
        internal static bool IsChildRecords(System.Reflection.PropertyInfo p)
        {
            if (p.Name == "SubCells") return true;
            var t = p.PropertyType;
            if (typeof(IFormLinkGetter).IsAssignableFrom(t)) return false;
            if (typeof(IMajorRecordGetter).IsAssignableFrom(t)) return true;
            return t.IsGenericType && t.GetGenericArguments().Any(a => typeof(IMajorRecordGetter).IsAssignableFrom(a));
        }

        /// <summary>
        /// Each record's parent, from the plugin's own groups: the world, cell, topic or quest it sits under.
        /// </summary>
        internal static Dictionary<string, string> RawParents(string path)
        {
            var ret = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var key = ModKey.FromFileName(Path.GetFileName(path));
            var package = Mutagen.Bethesda.Plugins.Masters.SeparatedMasterPackage.Factory(GameRelease.Starfield, new ModPath(key, path), new LossCheckCommand.HeaderFlagCache(GetDataFolderPath()), new System.IO.Abstractions.FileSystem());
            var b = File.ReadAllBytes(path);
            string Name(uint id) => Sfs.NormalizeFormKey(package.GetFormKey(FormID.Factory(id), reference: false).ToString());
            void Walk(int pos, int end, string? parent)
            {
                while (pos + 24 <= end)
                {
                    bool group = b[pos] == (byte)'G' && b[pos + 1] == (byte)'R' && b[pos + 2] == (byte)'U' && b[pos + 3] == (byte)'P';
                    int size = BitConverter.ToInt32(b, pos + 4);
                    if (group)
                    {
                        int g = BitConverter.ToInt32(b, pos + 12);
                        Walk(pos + 24, pos + size, g is 1 or 6 or 7 or 8 or 9 or 10 ? Name(BitConverter.ToUInt32(b, pos + 8)) : parent);
                        pos += size;
                        continue;
                    }
                    if (parent != null) ret[Name(BitConverter.ToUInt32(b, pos + 12))] = parent;
                    pos += 24 + size;
                }
            }
            Walk(24 + BitConverter.ToInt32(b, 4), b.Length, null);
            return ret;
        }

        /// <summary>
        /// OMOD getters that only the binary overlay has, left over from Fallout 4's DATA layout. They read fixed bytes inside the
        /// target name string: MaxRank 84 and LevelTierScaledOffset 69 are the "T" and "E" of "TESObjectWEAP_InstanceData",
        /// and DataName reads the wrong slice (always empty). Left out of the cache and Live (2026-10-27).
        /// </summary>
        static readonly HashSet<string> MisreadOmodProperties = new() { "MaxRank", "LevelTierScaledOffset", "DataName" };

        /// <summary>Top-level record properties the cache leaves out of Records.Data (bulky face data and unknown weapon blocks).</summary>
        internal static bool IsCacheSkippedField(string name) => IsCacheSkippedProperty(name);
        /// <summary>Fields left out of every row and of Live, because this Mutagen build misreads them. None on the private build.</summary>
        internal static bool IsAlwaysLeftOut(string name) => false;
        internal static bool IsCacheSkippedProperty(string name) =>
            ExcludedProperties.Contains(name) ||
            name == "FaceDialPositions" || name == "FaceMorphs" || name == "MorphBlends" || name == "Tints" ||
            name == "BodyMorphRegionValues" || name == "NAM5" || name == "ONA2" ||
            name.StartsWith("WAUDUnknown") || name.StartsWith("WDMGUnknown") ||
            name.StartsWith("WVISUnknown") || name.StartsWith("WFIRUnknown") ||
            name.StartsWith("WTRMUnknown") || name.StartsWith("QNAMUnknown") ||
            name.StartsWith("WFLGUnknown") || name.StartsWith("WMELUnknown") ||
            name.StartsWith("WGENUnknown") || name.StartsWith("WRLOUnknown") ||
            name.StartsWith("WAM2Unknown");

        internal static void CollectRefsFromJsonInternal(JsonNode? node, string path, List<(string Path, string ToFormKey)> list) => CollectRefsFromJson(node, path, list);
        static void CollectRefsFromJson(JsonNode? node, string path, List<(string Path, string ToFormKey)> list)
        {
            if (node == null) return;
            if (node is JsonValue v)
            {
                string s = v.ToString();
                if (s.IndexOf(':') > 0)
                {
                    foreach (System.Text.RegularExpressions.Match m in Sfs.EmbeddedFormKey.Matches(s))
                    {
                        string fk = Sfs.NormalizeFormKey(m.Value);
                        if (!fk.StartsWith("000000:", StringComparison.OrdinalIgnoreCase))
                            list.Add((path, fk));
                    }
                }
            }
            else if (node is JsonArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                    CollectRefsFromJson(arr[i], $"{path}[{i}]", list);
            }
            else if (node is JsonObject obj)
            {
                foreach (var kvp in obj)
                    CollectRefsFromJson(kvp.Value, $"{path}.{kvp.Key}", list);
            }
        }

        static object? FlattenValue(object? val, int depth = 0)
        {
            if (val == null) return null;
            if (val is string str) return str;
            if (val is Noggog.ReadOnlyMemorySlice<byte> roBytes) return roBytes.Length == 0 ? null : Convert.ToHexString(roBytes.Span);
            if (val is Noggog.MemorySlice<byte> msBytes) return msBytes.Length == 0 ? null : Convert.ToHexString(msBytes.Span);
            if (val is byte[] plainBytes) return plainBytes.Length == 0 ? null : Convert.ToHexString(plainBytes);
            if (val is Noggog.Percent percent) return percent.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture); // "33%" would round
            if (val is TimeOnly timeOfDay) return timeOfDay.ToString("O", System.Globalization.CultureInfo.InvariantCulture);         // the default text drops seconds
            if (val.GetType().IsPrimitive || val is decimal || val is Enum) return val.ToString();

            if (val is IFormLinkGetter link)
                return link.FormKey.IsNull ? null : Sfs.NormalizeFormKey(link.FormKey.ToString());
            if (val is FormKey fkVal)
                return fkVal.IsNull ? null : Sfs.NormalizeFormKey(fkVal.ToString());
            if (val.GetType().Name.StartsWith("FormLinkOrIndex"))
            {
                var linkProp = val.GetType().GetProperty("Link");
                if (linkProp?.GetValue(val) is IFormLinkGetter innerLink)
                    return innerLink.FormKey.IsNull ? null : Sfs.NormalizeFormKey(innerLink.FormKey.ToString());
            }

            if (depth > 40) { TooDeepValues++; return val.ToString(); }

            // Dictionary and grid entries (Package.Data, SurfaceBlocks, HeightMap) become {Key, Value}, not a .NET type name.
            if (val.GetType().IsGenericType && IsKeyValueType(val.GetType()))
            {
                // Text in a language stays "[English, The Lodge]" in the database: Sfs.English and saved queries read that form. Live shows Key and Value.
                if (!_languageAsObject && val.GetType().GetProperty("Key")!.GetValue(val) is Mutagen.Bethesda.Strings.Language)
                    return val.ToString();
                var kv = new JsonObject();
                foreach (var part in new[] { "Key", "Value" })
                {
                    var f = FlattenValue(val.GetType().GetProperty(part)!.GetValue(val), depth + 1);
                    if (f is string fs) kv[part] = fs;
                    else if (f is JsonNode fn) kv[part] = fn;
                }
                return kv;
            }

            if (val is IEnumerable enumerable)
            {
                var list = new JsonArray();
                foreach (var item in enumerable)
                {
                    if (item != null)
                    {
                        var flattened = FlattenValue(item, depth + 1);
                        if (flattened != null)
                        {
                            if (flattened is string s) list.Add(s);
                            else if (flattened is JsonObject o) list.Add(o);
                            else if (flattened is JsonArray inner) list.Add(inner);
                        }
                        else list.Add((JsonNode?)null); // an empty slot keeps its place, so later entries don't shift
                    }
                    else list.Add((JsonNode?)null);
                }
                // Nothing but empty slots (both genders of an absent list) is the same as no list
                if (list.Any(x => x != null)) return list;
                return null;
            }

            if (val.GetType().Namespace != null && val.GetType().Namespace?.StartsWith("Mutagen.Bethesda") == true)
            {
                var subObj = new JsonObject();
                var subProps = val.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Where(x => x.GetIndexParameters().Length == 0).ToArray();
                bool hasData = false;
                // Condition data blocks are polymorphic (GetLevelConditionData, GetValueConditionData, HasPerkConditionData...).
                // Record the concrete CTDA function type so condition semantics are recoverable from the cache.
                if (val is Mutagen.Bethesda.Starfield.IConditionDataGetter)
                    subObj["Function"] = val.GetType().Name.Replace("ConditionData", "");
                foreach (var sprop in subProps)
                {
                    if (ExcludedProperties.Contains(sprop.Name) ||
                        sprop.Name.StartsWith("WAUDUnknown") || sprop.Name.StartsWith("WDMGUnknown") ||
                        sprop.Name.StartsWith("WVISUnknown") || sprop.Name.StartsWith("WFIRUnknown") ||
                        sprop.Name.StartsWith("WTRMUnknown") || sprop.Name.StartsWith("QNAMUnknown") ||
                        sprop.Name.StartsWith("WFLGUnknown") || sprop.Name.StartsWith("WMELUnknown") ||
                        sprop.Name.StartsWith("WGENUnknown") || sprop.Name.StartsWith("WRLOUnknown") ||
                        sprop.Name.StartsWith("WAM2Unknown"))
                        continue;

                    try
                    {
                        var sval = sprop.GetValue(val);
                        if (sval != null)
                        {
                            var flattened = FlattenValue(sval, depth + 1);
                            if (flattened != null)
                            {
                                if (flattened is string s && s != "NullReference [00000000]") subObj[sprop.Name] = s;
                                else if (flattened is JsonArray a) subObj[sprop.Name] = a;
                                else if (flattened is JsonObject o) subObj[sprop.Name] = o;
                                hasData = true;
                            }
                        }
                    }
                    catch (Exception ex) { if (_captureFlattenErrors) { subObj[sprop.Name] = $"<error: {(ex.InnerException ?? ex).Message}>"; hasData = true; } }
                }
                if (hasData) return subObj;
                // An empty object (an empty naming rule set, a component with no fields) used to be stored as its .NET type name,
                // a string where JSON was expected: json_each over dn_CommonGun's rule sets failed with "malformed JSON" (2026-10-27).
                // The type is kept because it means something for components and terminal menu items.
                return new JsonObject { ["Type"] = ShortTypeName(val.GetType()) };
            }
            return val.ToString();
        }

        /// <summary>"InstanceNamingRuleSetBinaryOverlay`1" → "InstanceNamingRuleSet".</summary>
        static string ShortTypeName(Type t)
        {
            string n = t.Name;
            int tick = n.IndexOf('`');
            if (tick > 0) n = n.Substring(0, tick);
            return n.EndsWith("BinaryOverlay") ? n.Substring(0, n.Length - "BinaryOverlay".Length) : n;
        }

        static JsonNode? FetchRecord(string target, string recordType, string key)
        {
            string sig = string.IsNullOrWhiteSpace(recordType) || recordType == "*" ? "*" : NormalizeRecordType(recordType);
            if (Sfs.TryParseKey(key, target, out string fk))
            {
                var byFk = Sfs.FindRecord(target, fk, sig);
                if (byFk != null) return byFk.Data;
            }
            return Sfs.FindRecord(target, key, sig)?.Data;
        }

        static void ListRecords(string target, string recordType)
        {
            EnsureCacheUpToDate(target);
            string dbPath = GetCachePath(target);
            if (!File.Exists(dbPath)) { Console.WriteLine("Cache not found. Run BuildCache first."); return; }

            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            if (recordType == "*") cmd.CommandText = "SELECT FormKey, Signature, EditorID, Name FROM Records ORDER BY FormKey";
            else
            {
                cmd.CommandText = "SELECT FormKey, Signature, EditorID, Name FROM Records WHERE Signature = @sig";
                cmd.Parameters.AddWithValue("@sig", recordType);
            }

            var t = new Sfs.Table("FormKey", "Signature", "EditorID", "Name");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                t.Add(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2), reader.IsDBNull(3) ? "" : reader.GetString(3));
            }
            Sfs.Print(t, annotate: false);
        }

        static void DumpRecord(string modName, string signature, string edid)
        {
            var rec = FetchRecord(modName, signature, edid);
            if (rec == null) Console.WriteLine($"Could not find {signature} '{edid}' in {modName}.");
            else Console.WriteLine(rec.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        static void ReadRecord(string modName, string signature, string edid)
        {
            var rec = FetchRecord(modName, signature, edid);
            if (rec == null) Console.WriteLine($"Could not find {signature} '{edid}' in {modName}.");
            else Console.WriteLine(xEditFormatter.FormatRecord(rec));
        }

        static void InspectFormDirectly(string modName, string hexIdsCsv)
        {
            string dataFolder = GetDataFolderPath();
            string esmPath = Path.Combine(dataFolder, modName);
            if (!File.Exists(esmPath)) { Console.WriteLine($"File not found: {esmPath}"); return; }

            var readParams = new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryReadParameters
            {
                MasterFlagsLookup = new DirectMasterFlagCache(dataFolder),
                ThrowOnUnknownSubrecord = false
            };
            using var mod = StarfieldMod.CreateFromBinaryOverlay(esmPath, StarfieldRelease.Starfield, readParams);

            if (hexIdsCsv == "ALL_DMGT_AVIF")
            {
                Console.WriteLine("=== ALL DamageTypes ===");
                foreach (var dt in mod.DamageTypes)
                {
                    var obj = FlattenValue(dt) as JsonObject ?? new JsonObject();
                    Console.WriteLine($"[DMGT] {dt.FormKey} | {dt.EditorID} | {obj.ToJsonString()}");
                }
                Console.WriteLine("=== Matching ActorValues ===");
                foreach (var av in mod.ActorValueInformation)
                {
                    string edid = av.EditorID ?? "";
                    if (edid.Contains("EM", StringComparison.OrdinalIgnoreCase) ||
                        edid.Contains("Electro", StringComparison.OrdinalIgnoreCase) ||
                        edid.Contains("Para", StringComparison.OrdinalIgnoreCase) ||
                        edid.Contains("Stun", StringComparison.OrdinalIgnoreCase) ||
                        edid.Contains("Uncon", StringComparison.OrdinalIgnoreCase) ||
                        edid.Contains("Sleep", StringComparison.OrdinalIgnoreCase) ||
                        edid.Contains("Resist", StringComparison.OrdinalIgnoreCase) ||
                        av.FormKey.ID < 0x000400)
                    {
                        Console.WriteLine($"[AVIF] {av.FormKey} | {edid} | {av.Name?.String} | Flags: {av.Flags}");
                    }
                }
                return;
            }

            foreach (var rawHex in hexIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                uint id = Convert.ToUInt32(rawHex.Replace("0x", ""), 16);
                var fk = new FormKey(mod.ModKey, id);
                IMajorRecordGetter? rec =
                    (IMajorRecordGetter?)mod.DamageTypes.TryGetValue(fk) ??
                    (IMajorRecordGetter?)mod.ActorValueInformation.TryGetValue(fk) ??
                    (IMajorRecordGetter?)mod.Spells.TryGetValue(fk) ??
                    (IMajorRecordGetter?)mod.MagicEffects.TryGetValue(fk) ??
                    (IMajorRecordGetter?)mod.Perks.TryGetValue(fk) ??
                    (IMajorRecordGetter?)mod.ObjectEffects.TryGetValue(fk);

                if (rec != null)
                {
                    var obj = FlattenValue(rec) as JsonObject ?? new JsonObject();
                    Console.WriteLine($"=== [{rec.Registration.Name}] {rec.EditorID} ({rec.FormKey}) ===");
                    Console.WriteLine(obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
                else
                {
                    Console.WriteLine($"FormKey {fk} not found in checked groups of {modName}.");
                }
            }
        }

        static void InspectRecord(string target, string recordType, string edid, string field)
        {
            var targetRec = FetchRecord(target, recordType, edid);
            if (targetRec == null) { Console.WriteLine($"Record '{edid}' not found in '{target}'."); return; }

            Console.WriteLine();
            Console.WriteLine("| Field | Value |");
            Console.WriteLine("| :--- | :--- |");

            if (field != "*")
            {
                var val = targetRec[field];
                if (val != null) PrintNode(field, val, 0);
                else Console.WriteLine($"| **{field}** | Not Found |");
                return;
            }

            foreach (var kvp in targetRec.AsObject())
            {
                if (kvp.Key == "Signature") continue;
                PrintNode(kvp.Key, kvp.Value, 0);
            }
            Console.WriteLine();
        }

        static void PrintNode(string key, JsonNode? node, int indentLevel)
        {
            string indent = new string(' ', indentLevel * 4);
            string displayKey = GetDisplayName(key);

            if (node is JsonArray arr)
            {
                Console.WriteLine($"| {indent}{displayKey} | <{arr.Count} items> |");
                foreach (var item in arr)
                {
                    if (item is JsonObject objItem) PrintNode("Item", objItem, indentLevel + 1);
                    else
                    {
                        string itemStr = item?.ToString() ?? "";
                        if (Sfs.ResolveNames) itemStr = Sfs.AnnotateFormKeys(itemStr);
                        Console.WriteLine($"| {indent}&nbsp;&nbsp;&nbsp;&nbsp;**Item** | {itemStr} |");
                    }
                }
            }
            else if (node is JsonObject obj)
            {
                if (indentLevel == 0) Console.WriteLine($"| {displayKey} | |");
                else Console.WriteLine($"| {indent}{displayKey} | |");
                foreach (var kvp in obj) PrintNode(kvp.Key, kvp.Value, indentLevel + 1);
            }
            else
            {
                string valStr = node?.ToString().Replace("\n", " ").Replace("\r", "") ?? "None";
                if (Sfs.ResolveNames && key != "Record Header" && key != "EDID - Editor ID") valStr = Sfs.AnnotateFormKeys(valStr);
                if (indentLevel == 0) Console.WriteLine($"| {displayKey} | {valStr} |");
                else Console.WriteLine($"| {indent}{displayKey} | {valStr} |");
            }
        }

        static void CompareNodes(string key, JsonNode? node1, JsonNode? node2, int indentLevel)
        {
            string indent = new string(' ', indentLevel * 2);
            string displayKey = GetDisplayName(key);

            if (node1 == null && node2 == null) return;

            if (node1 == null)
            {
                Console.WriteLine($"{indent}- **[ADDED]** {displayKey}:");
                Console.WriteLine($"{indent}  ```json\n{indent}  {node2?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\n", "\n" + indent + "  ")}\n{indent}  ```");
                return;
            }
            if (node2 == null)
            {
                Console.WriteLine($"{indent}- **[REMOVED]** {displayKey}:");
                Console.WriteLine($"{indent}  ```json\n{indent}  {node1?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\n", "\n" + indent + "  ")}\n{indent}  ```");
                return;
            }

            if (node1 is JsonArray arr1 && node2 is JsonArray arr2)
            {
                var list1 = arr1.Select(x => x?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "").ToList();
                var list2 = arr2.Select(x => x?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "").ToList();
                var removed = list1.Except(list2).ToList();
                var added = list2.Except(list1).ToList();

                if (removed.Count > 0 || added.Count > 0)
                {
                    Console.WriteLine($"{indent}- **[MODIFIED]** {displayKey}:");
                    foreach (var r in removed)
                    {
                        Console.WriteLine($"{indent}  - **[REMOVED] Item**:");
                        Console.WriteLine($"{indent}    ```json\n{indent}    {r.Replace("\n", "\n" + indent + "    ")}\n{indent}    ```");
                    }
                    foreach (var a in added)
                    {
                        Console.WriteLine($"{indent}  - **[ADDED] Item**:");
                        Console.WriteLine($"{indent}    ```json\n{indent}    {a.Replace("\n", "\n" + indent + "    ")}\n{indent}    ```");
                    }
                }
            }
            else if (node1 is JsonObject obj1 && node2 is JsonObject obj2)
            {
                var allKeys = obj1.Select(x => x.Key).Union(obj2.Select(x => x.Key)).Distinct().ToList();
                bool hasDifferences = false;
                foreach (var k in allKeys) if (obj1[k]?.ToJsonString() != obj2[k]?.ToJsonString()) hasDifferences = true;

                if (hasDifferences)
                {
                    Console.WriteLine($"{indent}- **[MODIFIED]** {displayKey}:");
                    foreach (var k in allKeys) CompareNodes(k, obj1[k], obj2[k], indentLevel + 1);
                }
            }
            else
            {
                string val1 = node1.ToString();
                string val2 = node2.ToString();
                if (val1 != val2)
                {
                    if (Sfs.ResolveNames) { val1 = Sfs.AnnotateFormKeys(val1); val2 = Sfs.AnnotateFormKeys(val2); }
                    Console.WriteLine($"{indent}- **[MODIFIED]** {displayKey}: `{val1}`  ==>  `{val2}`");
                }
            }
        }

        static void RunDiffCommand(List<string> args)
        {
            bool summary = Sfs.TakeFlag(args, "--summary");
            string? ignoreCsv = Sfs.TakeOption(args, "--ignore");
            string? expectCsv = Sfs.TakeOption(args, "--expect");

            if (args.Count < 3)
            {
                Console.WriteLine("Usage: SFSearch Diff <Target1> <Target2> [RecordType|*] <Key1> [Key2] [--summary] [--ignore f1,f2] [--expect f1,f2]");
                return;
            }

            // Paths get their own _file1_ cache, as in every other command; a raw path here built Cache\<name>.db but opened
            // the cache next to the plugin (SQLite Error 14). Two paths to same-named files: the second gets _file2_, as in PluginDiff.
            static bool IsPath(string t) => t.IndexOfAny(new[] { '\\', '/' }) >= 0 || t.EndsWith(".bak", StringComparison.OrdinalIgnoreCase);
            bool sameNameCopies = IsPath(args[0]) && IsPath(args[1])
                && Path.GetFileName(args[0]).Equals(Path.GetFileName(args[1]), StringComparison.OrdinalIgnoreCase)
                && !Path.GetFullPath(Sfs.ResolveUserPath(args[0])).Equals(Path.GetFullPath(Sfs.ResolveUserPath(args[1])), StringComparison.OrdinalIgnoreCase);
            foreach (var t in args.Take(2).Where(IsPath).Where(t => !File.Exists(Sfs.ResolveUserPath(t))))
            {
                Console.WriteLine($"File not found: {t}");
                Environment.ExitCode = 1;
                return;
            }
            string target1 = EnsureCacheForFile(args[0]);
            string target2 = EnsureCacheForFile(args[1], isSecondArg: sameNameCopies);
            string recordType = "*";
            string key1, key2;

            if (args.Count == 3)
            {
                key1 = key2 = args[2];
            }
            else if (args.Count == 4)
            {
                // Could be Diff <T1> <T2> <Sig> <SameKey> OR Diff <T1> <T2> <Key1> <Key2>
                if (xEditToMutagenTypeMap.ContainsKey(args[2]) || args[2] == "*" || IsKnownSignatureName(args[2]))
                {
                    recordType = NormalizeRecordType(args[2]);
                    key1 = key2 = args[3];
                }
                else
                {
                    key1 = args[2];
                    key2 = args[3];
                }
            }
            else
            {
                recordType = NormalizeRecordType(args[2]);
                key1 = args[3];
                key2 = args[4];
            }

            var ignoreSet = new HashSet<string>(
                (ignoreCsv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);

            var expectSet = expectCsv == null ? null : new HashSet<string>(
                expectCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);

            var rec1 = FetchRecord(target1, recordType, key1)?.AsObject();
            var rec2 = FetchRecord(target2, recordType, key2)?.AsObject();

            if (rec1 == null) { Console.WriteLine($"Record '{key1}' not found in '{target1}'."); Environment.ExitCode = 1; return; }
            if (rec2 == null) { Console.WriteLine($"Record '{key2}' not found in '{target2}'."); Environment.ExitCode = 1; return; }

            var changedFields = InspectCommands.GetChangedTopFields(rec1, rec2, ignoreSet);

            Sfs.Say($"\n### Diff Report: {key1} ({target1}) vs {key2} ({target2})");

            if (summary || Sfs.Format != OutputFormat.Markdown)
            {
                var t = new Sfs.Table("Field", "Status", $"{key1} ({target1})", $"{key2} ({target2})");
                foreach (var f in changedFields)
                {
                    var n1 = rec1[f];
                    var n2 = rec2[f];
                    string st = n1 == null ? "ADDED" : n2 == null ? "REMOVED" : "MODIFIED";
                    t.Add(f, st, Sfs.Compact(n1), Sfs.Compact(n2));
                }
                Sfs.Print(t);
            }
            else
            {
                if (changedFields.Count == 0)
                {
                    Console.WriteLine("_Records are identical (0 changed fields)._");
                }
                else
                {
                    foreach (var key in changedFields)
                    {
                        CompareNodes(key, rec1[key], rec2[key], 0);
                    }
                }
                Console.WriteLine();
            }

            if (expectSet != null)
            {
                var unexpected = changedFields.Where(f => !expectSet.Contains(f)).ToList();
                if (unexpected.Count == 0)
                {
                    Sfs.Say($"**[EXPECT PASS]** Changed fields ({string.Join(", ", changedFields)}) are within expected set ({string.Join(", ", expectSet)}).");
                }
                else
                {
                    Console.WriteLine($"**[EXPECT FAIL]** Unexpected changed fields: {string.Join(", ", unexpected)} (Allowed: {string.Join(", ", expectSet)})");
                    Environment.ExitCode = 1;
                }
            }
        }

        static bool IsKnownSignatureName(string s) => s.Equals("Weapon", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Armor", StringComparison.OrdinalIgnoreCase)
            || s.Equals("ArmorAddon", StringComparison.OrdinalIgnoreCase)
            || s.Equals("FormList", StringComparison.OrdinalIgnoreCase)
            || s.Equals("ConstructibleObject", StringComparison.OrdinalIgnoreCase)
            || s.Equals("MiscItem", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Keyword", StringComparison.OrdinalIgnoreCase)
            || s.Equals("ObjectModification", StringComparison.OrdinalIgnoreCase)
            || s.Equals("LeveledItem", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Outfit", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Npc", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Splits a SQL script into single statements (SQLite's own sqlite3_complete decides where a statement ends,
        /// so semicolons inside strings, comments and trigger bodies are handled).
        /// </summary>
        public static List<string> SplitSqlStatements(string sql)
        {
            SQLitePCL.Batteries_V2.Init(); // sqlite3_complete is called before any SqliteConnection has loaded the native library
            var list = new List<string>();
            int start = 0;
            for (int i = 0; i < sql.Length; i++)
            {
                if (sql[i] != ';') continue;
                string chunk = sql.Substring(start, i + 1 - start);
                if (SQLitePCL.raw.sqlite3_complete(chunk) != 0)
                {
                    list.Add(chunk);
                    start = i + 1;
                }
            }
            string rest = sql.Substring(start);
            if (StripSqlComments(rest).Trim().Length > 0) list.Add(rest);
            return list;
        }

        /// <summary>SQL piped to "Query ... --stdin". Read as UTF-8 so the BOM PowerShell 5.1 puts in front is dropped.</summary>
        static string ReadPipedSql()
        {
            if (!Console.IsInputRedirected) throw new InvalidOperationException("Query --stdin needs the SQL piped in, e.g. $sql | & sfs.cmd Query <Target> --stdin");
            using var reader = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd().TrimStart('﻿');
        }

        static string StripSqlComments(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s, @"--[^\n]*|/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);

        /// <summary>A "-- # Title" / "-- ## Title" comment line before a statement becomes the heading of its result table.</summary>
        static string? StatementHeading(string stmt)
        {
            foreach (var raw in stmt.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (!line.StartsWith("--")) break;
                string c = line.Substring(2).Trim();
                if (c.StartsWith("#")) return c.TrimStart('#').Trim();
            }
            return null;
        }

        static string FirstSqlLine(string stmt)
        {
            string s = StripSqlComments(stmt).Trim();
            int nl = s.IndexOf('\n');
            if (nl > 0) s = s.Substring(0, nl).Trim();
            return s.Length > 90 ? s.Substring(0, 90) + "…" : s;
        }

        const string LoadOrderTarget = "@loadorder";

        static void QueryDatabases(List<string> targets, string sql, bool withMasters = false, List<string>? extra = null, bool timing = false, bool loadOrder = false, bool scripts = false)
        {
            if (targets.Count == 0)
            {
                Console.WriteLine("No matching target plugins found.");
                return;
            }

            var statements = SplitSqlStatements(loadOrder ? LoadOrderIndex.Rewrite(sql) : sql);
            if (statements.Count == 0) { Console.WriteLine("No SQL statements found."); return; }

            bool multi = targets.Count > 1;
            // One merged table per result-producing statement (statement index → table), in script order.
            var results = new SortedDictionary<int, (string? Heading, Sfs.Table Table)>();
            var errors = new List<string>();
            var swTotal = System.Diagnostics.Stopwatch.StartNew();

            foreach (var target in targets)
            {
                bool loOnly = target == LoadOrderTarget;
                string dbPath = loOnly ? "" : GetCachePath(target);
                if (!loOnly)
                {
                EnsureCacheUpToDate(target);
                dbPath = GetCachePath(target);
                if (!File.Exists(dbPath)) continue;

                // Old-schema cache (e.g. no Meta table) whose plugin is no longer in Data can't be rebuilt: skip it instead of a confusing SQL error.
                if (ReadCacheSchemaVersion(dbPath) != CacheSchemaVersion)
                {
                    Console.Error.WriteLine($"Skipped {target}: cache is from an older SFSearch version and the plugin is no longer in the Data folder, so it can't be rebuilt (delete Cache\\{target}.db to clean up).");
                    continue;
                }
                }

                using var conn = new SqliteConnection(loOnly ? "Data Source=:memory:" : $"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
                conn.Open();
                if (!loOnly) Sfs.WarnMissingMasters(target, Sfs.Meta(conn, "MissingMasters"));
                Sfs.TuneForAnalysis(conn);
                Sfs.RegisterSqlFunctions(conn);
                if (loadOrder) LoadOrderIndex.Attach(conn, sql);
                if (scripts) ScriptsCommand.Attach(conn, targets.Where(t => t != LoadOrderTarget).Concat(extra ?? new List<string>()));

                // The Forms table (every record incl. uncached types) was added without a schema bump: rebuild this cache once if a query needs it.
                if (!loOnly && System.Text.RegularExpressions.Regex.IsMatch(sql, @"\bForms\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    && !Sfs.TableExists(conn, "Forms") && File.Exists(Path.Combine(GetDataFolderPath(), target)))
                {
                    conn.Close();
                    Console.Error.WriteLine($"[sfs] {target}: cache has no Forms table yet. Rebuilding it once...");
                    BuildCache(target);
                    conn.Open();
                    Sfs.TuneForAnalysis(conn);
                    Sfs.RegisterSqlFunctions(conn);
                    if (loadOrder) LoadOrderIndex.Attach(conn, sql);
                    if (scripts) ScriptsCommand.Attach(conn, targets.Where(t => t != LoadOrderTarget).Concat(extra ?? new List<string>()));
                }

                var attachList = new List<string>();
                if (withMasters && !loOnly) attachList.AddRange(Sfs.Masters(conn));
                if (extra != null) attachList.AddRange(extra);
                if (attachList.Count > 0)
                {
                    var swA = System.Diagnostics.Stopwatch.StartNew();
                    var attached = Sfs.AttachCaches(conn, target, attachList);
                    Console.Error.WriteLine($"[sfs] {target}: attached {string.Join(", ", attached.Select(a => $"{a.Alias} = {a.Plugin}"))}; temp table AllRecords ready ({swA.Elapsed.TotalSeconds:0.0}s).");
                }

                for (int si = 0; si < statements.Count; si++)
                {
                    string stmt = statements[si];
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = stmt;
                        cmd.CommandTimeout = 0;
                        using var reader = cmd.ExecuteReader();
                        int rows = 0;
                        if (reader.FieldCount > 0)
                        {
                            if (!results.TryGetValue(si, out var entry))
                            {
                                var cols = new List<string>();
                                if (multi) cols.Add("Plugin");
                                for (int i = 0; i < reader.FieldCount; i++) cols.Add(reader.GetName(i));
                                entry = (StatementHeading(stmt), new Sfs.Table(cols.ToArray()));
                                results[si] = entry;
                            }
                            while (reader.Read())
                            {
                                var vals = new List<object?>();
                                if (multi) vals.Add(loOnly ? "(load order)" : target);
                                for (int i = 0; i < reader.FieldCount; i++)
                                    vals.Add(reader.IsDBNull(i) ? "NULL" : reader.GetValue(i)?.ToString() ?? "NULL");
                                entry.Table.Add(vals.ToArray());
                                rows++;
                            }
                        }
                        double secs = sw.Elapsed.TotalSeconds;
                        if (timing || secs >= 5)
                            Console.Error.WriteLine($"[sfs] {target} statement {si + 1}/{statements.Count}: {secs:0.0}s{(reader.FieldCount > 0 ? $", {rows} rows" : "")} — {FirstSqlLine(stmt)}");
                    }
                    catch (Exception ex)
                    {
                        string msg = $"SQL Error on {target}, statement {si + 1}/{statements.Count} ({FirstSqlLine(stmt)}): {ex.Message}";
                        if (ex.Message.Contains("incomplete input", StringComparison.OrdinalIgnoreCase))
                            msg += " (SQL typed inline is cut at its first line break by sfs.cmd: put it on one line, or use --file f.sql, or pipe it in with --stdin.)";
                        Console.Error.WriteLine(msg);
                        errors.Add(msg);
                        break; // later statements usually depend on this one (temp tables)
                    }
                }
            }

            int k = 0;
            bool many = results.Count > 1;
            foreach (var (si, (heading, table)) in results)
            {
                k++;
                if (heading != null) Sfs.Say($"\n## {heading}\n");
                else if (many) Sfs.Say($"\n#### Result {k} (statement {si + 1})\n");
                Sfs.Print(table);
                Sfs.Say($"\nReturned {table.Rows.Count} rows.");
                Sfs.ActiveSummary($"Result {k}{(heading != null ? $" \"{heading}\"" : "")}: {table.Rows.Count} rows");
            }
            foreach (var e in errors) Sfs.ActiveSummary(e);
            if (timing) Console.Error.WriteLine($"[sfs] Total {swTotal.Elapsed.TotalSeconds:0.0}s for {statements.Count} statement(s).");
        }
        static void CalculatePercentiles()
        {
            Console.WriteLine("Loading Environment...");
            var env = GameEnvironment.Typical.Starfield(StarfieldRelease.Starfield);
            var linkCache = env.LinkCache;
            var weapons = env.LoadOrder.PriorityOrder.Weapon().WinningOverrides().ToList();
            var groupSdps = new Dictionary<string, List<float>>();
            
            foreach (var wpn in weapons)
            {
                if (!ItemFilter.IsValidWeapon(wpn, linkCache)) continue;
                var wGroup = ItemFilter.GetWeaponGroup(wpn);
                var wClass = ItemFilter.GetWeaponClass(wpn, linkCache);
                
                string catName = wGroup.ToString();
                if (wClass != WeaponClass.Standard) {
                    catName = wClass.ToString();
                }
                
                float cap = wpn.AmmoCapacity > 0 ? wpn.AmmoCapacity : 1.0f;
                float attackSeconds = wpn.ShotsPerSecond > 0 ? (1.0f / wpn.ShotsPerSecond) : (wpn.AttackSeconds > 0 ? wpn.AttackSeconds : 0.1f);        
                float reloadSpeed = wpn.ReloadSpeed > 0 ? wpn.ReloadSpeed : 1.0f;
                float baseDmg = wpn.AttackDamage;
                if (wpn.DamageTypes != null) foreach (var dt in wpn.DamageTypes) if (dt.Value > 1f) baseDmg += dt.Value;
                
                if (wpn.ObjectTemplates != null) {
                    var defaultTemplate = wpn.ObjectTemplates.FirstOrDefault(t => t.Default) ?? wpn.ObjectTemplates.FirstOrDefault();
                    if (defaultTemplate != null && defaultTemplate.Includes != null) {
                        foreach (var inc in defaultTemplate.Includes) {
                            if (linkCache.TryResolve<IObjectModificationGetter>(inc.Mod.FormKey, out var defaultOmod) && defaultOmod.Properties != null) {
                                foreach (var prop in defaultOmod.Properties) {
                                    if (prop is ObjectModFormLinkFloatProperty<Mutagen.Bethesda.Starfield.Weapon.Property> dtProp && dtProp.Property == Mutagen.Bethesda.Starfield.Weapon.Property.DamageTypeValue && dtProp.FunctionType == ObjectModProperty.FloatFunctionType.Add && dtProp.Value > 1f)
                                        baseDmg += dtProp.Value;
                                    else if (prop is ObjectModFloatProperty<Mutagen.Bethesda.Starfield.Weapon.Property> fProp && fProp.Property == Mutagen.Bethesda.Starfield.Weapon.Property.DamagePhysical && fProp.FunctionType == ObjectModProperty.FloatFunctionType.Add && fProp.Value > 1f)
                                        baseDmg += fProp.Value;
                                    else if (prop is ObjectModFloatProperty<Mutagen.Bethesda.Starfield.Weapon.Property> atkProp && atkProp.Property == Mutagen.Bethesda.Starfield.Weapon.Property.AttackSeconds && atkProp.FunctionType == ObjectModProperty.FloatFunctionType.Set)
                                        attackSeconds = atkProp.Value;
                                    else if (prop is ObjectModFloatProperty<Mutagen.Bethesda.Starfield.Weapon.Property> spsProp && spsProp.Property == Mutagen.Bethesda.Starfield.Weapon.Property.ShotsperSecond) {
                                        if (spsProp.FunctionType == ObjectModProperty.FloatFunctionType.Set && spsProp.Value > 0) attackSeconds = 1.0f / spsProp.Value;
                                        else if (spsProp.FunctionType == ObjectModProperty.FloatFunctionType.Add && wpn.ShotsPerSecond + spsProp.Value > 0) attackSeconds = 1.0f / (wpn.ShotsPerSecond + spsProp.Value);
                                    }
                                    else if (prop is ObjectModIntProperty<Mutagen.Bethesda.Starfield.Weapon.Property> capProp && capProp.Property == Mutagen.Bethesda.Starfield.Weapon.Property.AmmoCapacity) {
                                        if (capProp.FunctionType.ToString() == "Set") cap = capProp.Value;
                                        else if (capProp.FunctionType.ToString() == "Add") cap += capProp.Value;
                                    }
                                    else if (prop is ObjectModFloatProperty<Mutagen.Bethesda.Starfield.Weapon.Property> rldProp && rldProp.Property == Mutagen.Bethesda.Starfield.Weapon.Property.ReloadSpeed) {
                                        if (rldProp.FunctionType == ObjectModProperty.FloatFunctionType.Set) reloadSpeed = rldProp.Value;
                                        else if (rldProp.FunctionType == ObjectModProperty.FloatFunctionType.Add) reloadSpeed += rldProp.Value;
                                    }
                                }
                            }
                        }
                    }
                }
                
                int projectiles = wpn.ProjectilesCount > 0 ? (int)wpn.ProjectilesCount : 1;
                float totalBaseDmg = (baseDmg > 0 ? baseDmg : 1.0f) * projectiles;
                
                float sdps = 0;
                if (wGroup == WeaponGroup.Melee) {
                    sdps = totalBaseDmg / attackSeconds;
                } else {
                    sdps = (cap * totalBaseDmg) / ((cap * attackSeconds) + (1.0f / reloadSpeed));
                }
                
                if (!groupSdps.ContainsKey(catName)) groupSdps[catName] = new List<float>();
                groupSdps[catName].Add(sdps);
            }
            
            Console.WriteLine("\n--- 80th Percentile BASE SDPS by Group ---");
            foreach (var kvp in groupSdps.OrderBy(x => x.Key))
            {
                var sorted = kvp.Value.OrderBy(x => x).ToList();
                if (sorted.Count == 0) continue;
                int index = (int)Math.Floor(0.80 * (sorted.Count - 1));
                float p80 = sorted[index];
                Console.WriteLine($"            {{ \"{kvp.Key}\", {p80:F2}f }}, // {sorted.Count} weapons");
            }
        }
    }

    public class DummyMasterStyle : Mutagen.Bethesda.Plugins.Records.IModMasterStyledGetter
    {
        public Mutagen.Bethesda.Plugins.MasterStyle MasterStyle { get; set; } = Mutagen.Bethesda.Plugins.MasterStyle.Full;
        public Mutagen.Bethesda.Plugins.ModKey ModKey { get; set; }
    }

    public class SimpleMasterFlagCache : Noggog.IReadOnlyCache<Mutagen.Bethesda.Plugins.Records.IModMasterStyledGetter, Mutagen.Bethesda.Plugins.ModKey>
    {
        private readonly Dictionary<Mutagen.Bethesda.Plugins.ModKey, Mutagen.Bethesda.Plugins.MasterStyle> _styles = new();

        public SimpleMasterFlagCache()
        {
        }

        public Mutagen.Bethesda.Plugins.Records.IModMasterStyledGetter TryGetValue(Mutagen.Bethesda.Plugins.ModKey key)
        {
            if (!_styles.TryGetValue(key, out var style))
            {
                style = Mutagen.Bethesda.Plugins.MasterStyle.Full;
                try
                {
                    string pathFile = Path.Combine("Cache", "DataPath.txt");
                    if (File.Exists(pathFile))
                    {
                        string dataPath = File.ReadAllText(pathFile).Trim();
                        string modPath = Path.Combine(dataPath, key.FileName);
                        if (File.Exists(modPath))
                        {
                            using var fs = File.OpenRead(modPath);
                            byte[] hdr = new byte[12];
                            if (fs.Read(hdr, 0, 12) == 12)
                            {
                                uint flags = BitConverter.ToUInt32(hdr, 8);
                                if ((flags & (uint)Mutagen.Bethesda.Starfield.StarfieldModHeader.HeaderFlag.Light) != 0)
                                    style = Mutagen.Bethesda.Plugins.MasterStyle.Small;
                                else if ((flags & (uint)Mutagen.Bethesda.Starfield.StarfieldModHeader.HeaderFlag.Medium) != 0)
                                    style = Mutagen.Bethesda.Plugins.MasterStyle.Medium;
                            }
                        }
                    }
                }
                catch { }
                _styles[key] = style;
            }
            return new DummyMasterStyle() { ModKey = key, MasterStyle = style };
        }
        
        public int Count => _styles.Count;
        public bool ContainsKey(Mutagen.Bethesda.Plugins.ModKey key) => true;
        public Mutagen.Bethesda.Plugins.Records.IModMasterStyledGetter this[Mutagen.Bethesda.Plugins.ModKey key] => TryGetValue(key);
        
        public IEnumerator<Noggog.IKeyValue<Mutagen.Bethesda.Plugins.ModKey, Mutagen.Bethesda.Plugins.Records.IModMasterStyledGetter>> GetEnumerator() { yield break; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { yield break; }
        public IEnumerable<Mutagen.Bethesda.Plugins.ModKey> Keys => _styles.Keys;
        public IEnumerable<Mutagen.Bethesda.Plugins.Records.IModMasterStyledGetter> Items { get { yield break; } }
    }
}
