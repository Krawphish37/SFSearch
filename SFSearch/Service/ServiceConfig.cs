using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SFSearch.Service
{
    /// <summary>
    /// SFSearch's settings (2026-11-07): `sfsearch.json` in SFSearch's home folder: the service, its warm-up, and the item filter, made with every setting explained the
    /// first time the service starts. Comments and trailing commas are allowed; a missing line keeps its default. An
    /// environment variable of the same setting (SFSEARCH_WORKERS, SFSEARCH_WARMUP=0, ...) overrides the file. Read again
    /// whenever it changes, so most edits apply without a restart (the worker count applies to new workers).
    /// </summary>
    public sealed class ServiceConfig
    {
        public int Workers { get; set; } = 8;
        public int JobTimeoutMinutes { get; set; } = 10;
        public int WorkerRecycleMB { get; set; } = 3072;
        public int WorkerMaxMB { get; set; } = 12288;
        public int WorkerRecycleCommands { get; set; } = 500;
        public int QueueLimit { get; set; } = 500;
        public int DataCapGB { get; set; } = 40;
        public WarmupConfig Warmup { get; set; } = new();
        /// <summary>The item filter's lists (see ItemFilter.cs). Not a service setting: every way of running SFSearch uses it.</summary>
        public ItemFilterConfig ItemFilter { get; set; } = new();
        /// <summary>Champollion.exe (Papyrus decompiler) for the Scripts command; empty = look for it by itself.</summary>
        public string? Champollion { get; set; }

        public sealed class WarmupConfig
        {
            public bool Enabled { get; set; } = true;
            public int IdleSeconds { get; set; } = 30;
            public List<string> Plugins { get; set; } = new() { "@base", "@recent" };
            public int RecentCount { get; set; } = 15;
            public List<string> Types { get; set; } = new()
            {
                "Weapon", "Armor", "ArmorAddon", "ConstructibleObject", "WeaponModification", "ArmorModification", "ObjectModification",
                "Keyword", "FormList", "LeveledItem", "MiscItem", "Ammunition",
            };
            public bool AllRecordsList { get; set; } = true;
            public bool RewarmAfterUpdates { get; set; } = true;
            public int RewarmCheckMinutes { get; set; } = 10;
        }

        public static string PathFor(string home) => System.IO.Path.Combine(home, "sfsearch.json");

        static readonly JsonSerializerOptions Read = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        static ServiceConfig? _cached;
        static DateTime _cachedStamp;
        public static string? LastProblem { get; private set; }

        /// <summary>The settings now: the file (made if missing, read again when it changes), then environment overrides.</summary>
        public static ServiceConfig Load(string home)
        {
            string path = PathFor(home);
            try { if (!File.Exists(path)) File.WriteAllText(path, DefaultText); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (_cached == null || stamp != _cachedStamp)
            {
                ServiceConfig c;
                try
                {
                    c = File.Exists(path) ? JsonSerializer.Deserialize<ServiceConfig>(File.ReadAllText(path), Read) ?? new() : new();
                    c.Warmup ??= new();
                    c.ItemFilter ??= new();
                    LastProblem = null;
                }
                catch (Exception e) when (e is JsonException or IOException)
                {
                    c = _cached ?? new ServiceConfig(); // a broken edit keeps the last good settings
                    LastProblem = $"{path} couldn't be read ({e.Message}); using the last good settings.";
                }
                _cached = c;
                _cachedStamp = stamp;
            }
            return WithEnvironment(_cached);
        }

        static ServiceConfig WithEnvironment(ServiceConfig f)
        {
            int I(string name, int v) => int.TryParse(Environment.GetEnvironmentVariable(name), out int e) && e > 0 ? e : v;
            var w = f.Warmup;
            return new ServiceConfig
            {
                Workers = Math.Max(1, I("SFSEARCH_WORKERS", f.Workers)),
                JobTimeoutMinutes = Math.Max(1, I("SFSEARCH_JOB_MINUTES", f.JobTimeoutMinutes)),
                WorkerRecycleMB = Math.Max(256, I("SFSEARCH_WORKER_RECYCLE_MB", f.WorkerRecycleMB)),
                WorkerMaxMB = Math.Max(512, I("SFSEARCH_WORKER_MAX_MB", f.WorkerMaxMB)),
                WorkerRecycleCommands = Math.Max(1, I("SFSEARCH_WORKER_JOBS", f.WorkerRecycleCommands)),
                QueueLimit = Math.Max(1, I("SFSEARCH_QUEUE_LIMIT", f.QueueLimit)),
                DataCapGB = Math.Max(1, int.TryParse(Environment.GetEnvironmentVariable("SFSEARCH_DATA_CAP_MB"), out int mb) && mb > 0 ? (mb + 1023) / 1024 : f.DataCapGB),
                ItemFilter = f.ItemFilter,
                Champollion = f.Champollion,
                Warmup = new WarmupConfig
                {
                    Enabled = Environment.GetEnvironmentVariable("SFSEARCH_WARMUP") != "0" && w.Enabled,
                    IdleSeconds = Math.Max(5, w.IdleSeconds),
                    Plugins = w.Plugins ?? new(),
                    RecentCount = Math.Max(0, I("SFSEARCH_WARMUP_RECENT", w.RecentCount)),
                    Types = w.Types ?? new(),
                    AllRecordsList = w.AllRecordsList,
                    RewarmAfterUpdates = w.RewarmAfterUpdates,
                    RewarmCheckMinutes = Math.Max(1, w.RewarmCheckMinutes),
                },
            };
        }

        /// <summary>For `Service config`: where the file is and what's in effect.</summary>
        public static string Describe(string home)
        {
            var c = Load(home);
            var w = c.Warmup;
            return $@"Settings file: {PathFor(home)}{(LastProblem != null ? "\nWARNING: " + LastProblem : "")}
Workers: {c.Workers} at once. Each command may take {c.JobTimeoutMinutes} min. A worker is replaced after {c.WorkerRecycleCommands} commands or {c.WorkerRecycleMB} MB, and stopped above {c.WorkerMaxMB} MB.
Queue limit: {c.QueueLimit}. Data folder cap: {c.DataCapGB} GB.
Warm-up: {(w.Enabled ? $"on, after {w.IdleSeconds} s with no agent command" : "off")}.
  Plugins: {string.Join(", ", w.Plugins)}{(w.Plugins.Any(p => p.Equals("@recent", StringComparison.OrdinalIgnoreCase)) ? $" (recent = the {w.RecentCount} most recently asked about)" : "")}
  Types: {string.Join(", ", w.Types)}{(w.AllRecordsList ? ", plus the list of every record" : "")}
  After a game or mod update: {(w.RewarmAfterUpdates ? $"read again at the next quiet moment (checked every {w.RewarmCheckMinutes} min)" : "not read again until the service restarts")}
Item filter: {(c.ItemFilter.FingerprintPart() == "" ? "the default rules" : "your own rules (databases built with other rules are built again)")}.
Environment variables (SFSEARCH_WORKERS, SFSEARCH_WARMUP=0, ...) override the file.";
        }

        const string DefaultText = @"// SFSearch settings: the service, its warm-up, the item filter. Edit and save: SFSearch reads this again by itself.
// Delete a line to use its default. Environment variables of the same setting (SFSEARCH_WORKERS, SFSEARCH_WARMUP=0, ...)
// override this file. 'sfs.cmd Service config' shows what's in effect.
{
  // How many commands run at once (each in its own worker program).
  ""workers"": 8,
  // A command running longer than this is stopped with a clear error.
  ""jobTimeoutMinutes"": 10,
  // A worker is replaced after this much memory (MB) or this many commands, and stopped mid-command above workerMaxMB.
  ""workerRecycleMB"": 3072,
  ""workerMaxMB"": 12288,
  ""workerRecycleCommands"": 500,
  // More waiting commands than this are turned away with ""busy, try again"".
  ""queueLimit"": 500,
  // The service's databases (Service\data); the least recently used go first above this size.
  ""dataCapGB"": 40,

  // Warm-up: while no agent is asking anything, read plugins ahead so the first question about them is fast.
  ""warmup"": {
    ""enabled"": true,
    // How long nothing has to happen before it starts. Agents always go first.
    ""idleSeconds"": 30,
    // What to read, in this order. Plugin file names (""SFBGS050.esm""), patterns (""SFBGS*.esm""), and:
    //   ""@base""      Starfield.esm, and ShatteredSpace.esm when installed
    //   ""@official""  every Bethesda file that is loaded: the base game, Shattered Space, Terran Armada (SFBGS050.esm), Creations...
    //   ""@recent""    the plugins agents asked about most recently (how many: recentCount)
    //   ""@loaded""    every loaded plugin (slow the first time on a big load order)
    // Examples:  [""Starfield.esm""]   or   [""Starfield.esm"", ""ShatteredSpace.esm"", ""SFBGS050.esm""]   or   [""@official"", ""@recent""]
    ""plugins"": [""@base"", ""@recent""],
    ""recentCount"": 15,
    // Record types to read for each plugin. Only types SFSearch stores can be warmed; others are added with 'Add'.
    ""types"": [""Weapon"", ""Armor"", ""ArmorAddon"", ""ConstructibleObject"", ""WeaponModification"", ""ArmorModification"", ""ObjectModification"",
              ""Keyword"", ""FormList"", ""LeveledItem"", ""MiscItem"", ""Ammunition""],
    // Also the list of every record (the Forms table: needed by Names, Conflicts and 'every record' queries).
    ""allRecordsList"": true,
    // After a game update or a mod update, read the changed files again at the next quiet moment.
    ""rewarmAfterUpdates"": true,
    ""rewarmCheckMinutes"": 10
  },

  // Champollion.exe, the Papyrus decompiler the Scripts command uses (version 1.3.2 or newer). Leave empty to look for it in
  // SFSearch's tools\champollion folder.
  ""champollion"": """",

  // Item filter: how SFSearch sorts weapons and armor (Profile, MkDiff, reports, the Archetype and IsJunk fields it stores).
  // Leave this section out to use the default rules. A list you write here replaces that default list. FormKeys are
  // ""ID:Plugin.esm"". Changing anything here makes SFSearch build its databases again, once, as they're used.
  ""itemFilter"": {
    // A weapon's group comes from the first group with one of its keywords.
    ""weaponGroups"": {
      ""Melee"":   [""2811D0:Starfield.esm"", ""04A0A4:Starfield.esm"", ""04A0A5:Starfield.esm""],
      ""Heavy"":   [""2811CF:Starfield.esm"", ""04A0A3:Starfield.esm"", ""04C922:Starfield.esm""],
      ""Shotgun"": [""2811CE:Starfield.esm"", ""298ED2:Starfield.esm"", ""226454:Starfield.esm""],
      ""Rifle"":   [""2811CC:Starfield.esm"", ""1C84DD:Starfield.esm"", ""298ECF:Starfield.esm"", ""298ED1:Starfield.esm"", ""04A0A1:Starfield.esm"", ""04A0A2:Starfield.esm""],
      ""Pistol"":  [""2811CA:Starfield.esm"", ""298ECE:Starfield.esm"", ""298ED0:Starfield.esm"", ""04A0A0:Starfield.esm"", ""00E6C5:Starfield.esm""]
    },
    // Damage type by keyword (first match).
    ""weaponTypes"": { ""Particle"": ""27BAFE:Starfield.esm"", ""Ballistic"": ""092A86:Starfield.esm"", ""Laser"": ""092A84:Starfield.esm"", ""EM"": ""27DF95:Starfield.esm"", ""Explosive"": ""04C922:Starfield.esm"" },
    // Keywords behind some weapon classes (Sniper = scoped rifle; Knife and Sword = melee animations).
    ""weaponClassKeywords"": { ""Sniper"": ""1E325D:Starfield.esm"", ""Launcher"": ""04C922:Starfield.esm"", ""Minigun"": ""03165E:Starfield.esm"", ""Knife"": ""00B0A1:Starfield.esm"", ""Sword"": ""040DD3:Starfield.esm"" },
    // Words in a melee weapon's EditorID or name that make it a polearm.
    ""polearmWords"": [""polearm"", ""lance"", ""staff"", ""trident"", ""spear"", ""halberd"", ""pike""],
    // Armor type by body slot bit (first match); anything else is an Accessory.
    ""armorSlots"": { ""Spacesuit"": 35, ""Helmet"": 36, ""Pack"": 37, ""Neuroamp"": 18, ""Apparel"": 3, ""Headwear"": 4 },
    // What counts as junk (not a real player item). Non-playable records always are.
    ""junkWeapons"": {
      ""editorIdStartsWith"": [""OLD_""],
      ""editorIdContains"": [""Debug"", ""NOCLUTTER"", ""Clutter"", ""Ecliptic"", ""ModelA_"", ""SFFL_SE_"", ""Vortex"", ""NonEquippable"", ""NOTPLAYABLE""],
      ""nameContains"": [""NOT PLAYABLE"", ""Ecliptic""],
      ""keywords"": [""04A0A6:Starfield.esm"", ""10C414:Starfield.esm""],
      ""requireName"": true, ""requireEquipType"": true, ""requireDamage"": true
    },
    ""junkArmor"": {
      ""editorIdStartsWith"": [""OLD_"", ""NPC_""],
      ""editorIdContains"": [""Debug"", ""NOCLUTTER"", ""Clutter"", ""Crew_Elite_"", ""Vortex"", ""NonEquippable"", ""NOTPLAYABLE"", ""Armor_Model_A_"", ""Skin_""],
      ""nameContains"": [""NOT PLAYABLE""],
      ""requireName"": true, ""nameNotNeededForEditorIdStartingWith"": [""DarkStar_""],
      // Armor made for a race other than this one belongs to a creature.
      ""playableRace"": ""00347D:Starfield.esm""
    }
  }
}
";
    }
}
