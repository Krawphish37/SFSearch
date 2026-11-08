using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Starfield;

namespace SFSearch
{
    public enum ArmorArchetype { Spacesuit, Helmet, Pack, Neuroamp, Headwear, Apparel, Accessory, Unknown }
    public enum WeaponGroup { Pistol, Rifle, Shotgun, Heavy, Melee, Unknown }
    public enum WeaponKind { FullyAutomatic, SemiAutomatic, Burst, Unknown }
    public enum WeaponClass { Magnum, Sniper, DMR, Launcher, Minigun, Beam, DoubleBarrel, PumpAction, SMG, Polearm, Knife, Sword, Standard, Unknown }
    public enum WeaponType { Ballistic, Laser, Particle, EM, Explosive, Physical, Unknown }

    /// <summary>
    /// The `itemFilter` section of sfsearch.json (2026-11-07). Every list the item filter uses: which keywords make a weapon
    /// a pistol, rifle and so on, which body slots make an armor a spacesuit or a helmet, and what counts as junk (not a
    /// real player item). Missing entries keep the defaults, which are the All-in-One Patcher's own rules.
    /// </summary>
    public sealed class ItemFilterConfig
    {
        /// <summary>First match wins, in this order. FormKeys of keywords.</summary>
        public Dictionary<string, List<string>> WeaponGroups { get; set; } = new()
        {
            ["Melee"] = new() { "2811D0:Starfield.esm", "04A0A4:Starfield.esm", "04A0A5:Starfield.esm" },
            ["Heavy"] = new() { "2811CF:Starfield.esm", "04A0A3:Starfield.esm", "04C922:Starfield.esm" },
            ["Shotgun"] = new() { "2811CE:Starfield.esm", "298ED2:Starfield.esm", "226454:Starfield.esm" },
            ["Rifle"] = new() { "2811CC:Starfield.esm", "1C84DD:Starfield.esm", "298ECF:Starfield.esm", "298ED1:Starfield.esm", "04A0A1:Starfield.esm", "04A0A2:Starfield.esm" },
            ["Pistol"] = new() { "2811CA:Starfield.esm", "298ECE:Starfield.esm", "298ED0:Starfield.esm", "04A0A0:Starfield.esm", "00E6C5:Starfield.esm" },
        };

        /// <summary>Damage type by keyword, first match wins.</summary>
        public Dictionary<string, string> WeaponTypes { get; set; } = new()
        {
            ["Particle"] = "27BAFE:Starfield.esm",
            ["Ballistic"] = "092A86:Starfield.esm",
            ["Laser"] = "092A84:Starfield.esm",
            ["EM"] = "27DF95:Starfield.esm",
            ["Explosive"] = "04C922:Starfield.esm",
        };

        /// <summary>Keywords for weapon classes: Sniper (scoped rifle), Launcher, Minigun, Knife and Sword (melee animations).</summary>
        public Dictionary<string, string> WeaponClassKeywords { get; set; } = new()
        {
            ["Sniper"] = "1E325D:Starfield.esm",
            ["Launcher"] = "04C922:Starfield.esm",
            ["Minigun"] = "03165E:Starfield.esm",
            ["Knife"] = "00B0A1:Starfield.esm",
            ["Sword"] = "040DD3:Starfield.esm",
        };

        /// <summary>Words in a melee weapon's EditorID or name that make it a polearm.</summary>
        public List<string> PolearmWords { get; set; } = new() { "polearm", "lance", "staff", "trident", "spear", "halberd", "pike" };

        /// <summary>Armor type by body slot bit (biped flags), first match wins; anything else is an Accessory.</summary>
        public Dictionary<string, int> ArmorSlots { get; set; } = new()
        {
            ["Spacesuit"] = 35, ["Helmet"] = 36, ["Pack"] = 37, ["Neuroamp"] = 18, ["Apparel"] = 3, ["Headwear"] = 4,
        };

        public JunkRules JunkWeapons { get; set; } = new()
        {
            EditorIdStartsWith = new() { "OLD_" },
            EditorIdContains = new() { "Debug", "NOCLUTTER", "Clutter", "Ecliptic", "ModelA_", "SFFL_SE_", "Vortex", "NonEquippable", "NOTPLAYABLE" },
            NameContains = new() { "NOT PLAYABLE", "Ecliptic" },
            Keywords = new() { "04A0A6:Starfield.esm", "10C414:Starfield.esm" }, // grenades and mines
            RequireName = true,
            RequireEquipType = true,
            RequireDamage = true,
        };

        public JunkRules JunkArmor { get; set; } = new()
        {
            EditorIdStartsWith = new() { "OLD_", "NPC_" },
            EditorIdContains = new() { "Debug", "NOCLUTTER", "Clutter", "Crew_Elite_", "Vortex", "NonEquippable", "NOTPLAYABLE", "Armor_Model_A_", "Skin_" },
            NameContains = new() { "NOT PLAYABLE" },
            RequireName = true,
            NameNotNeededForEditorIdStartingWith = new() { "DarkStar_" },
            PlayableRace = "00347D:Starfield.esm", // HumanRace: armor made for another race is a creature's
        };

        public sealed class JunkRules
        {
            public List<string> EditorIdStartsWith { get; set; } = new();
            public List<string> EditorIdContains { get; set; } = new();
            public List<string> NameContains { get; set; } = new();
            public List<string> Keywords { get; set; } = new();
            public bool RequireName { get; set; }
            public List<string> NameNotNeededForEditorIdStartingWith { get; set; } = new();
            public bool RequireEquipType { get; set; }
            public bool RequireDamage { get; set; }
            public string? PlayableRace { get; set; }
        }

        static readonly JsonSerializerOptions Canon = new() { WriteIndented = false };

        /// <summary>Empty when the settings are the defaults; otherwise a short hash, part of the database fingerprint, so
        /// databases holding the old junk flags and groups are built again.</summary>
        public string FingerprintPart()
        {
            string mine = JsonSerializer.Serialize(this, Canon);
            string dflt = JsonSerializer.Serialize(new ItemFilterConfig(), Canon);
            return mine == dflt ? "" : ";itemfilter=" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(mine)))[..8];
        }
    }

    /// <summary>
    /// SFSearch's item filter: weapon group, kind, class and damage type; armor type; and whether an item is junk. A copy of
    /// the All-in-One Patcher's UniversalItemFilter rules (2026-11-07), with every list read from sfsearch.json's `itemFilter`
    /// section so a user can change them. The patcher's own filter is separate and unchanged.
    /// </summary>
    public static class ItemFilter
    {
        static ItemFilterConfig C => Service.ServiceConfig.Load(Sfs.HomeDir).ItemFilter;

        static readonly Dictionary<string, FormKey?> _keys = new(StringComparer.OrdinalIgnoreCase);

        static FormKey? Key(string s)
        {
            lock (_keys)
            {
                if (_keys.TryGetValue(s, out var k)) return k;
                FormKey? parsed = FormKey.TryFactory(Sfs.NormalizeFormKey(s), out var fk) ? fk : null;
                return _keys[s] = parsed;
            }
        }

        static bool Has(IKeywordedGetter<IKeywordGetter> rec, string fk) => Key(fk) is { } k && rec.HasKeyword(k);
        static bool HasAny(IKeywordedGetter<IKeywordGetter> rec, IEnumerable<string> fks) => fks.Any(f => Has(rec, f));

        public static ArmorArchetype GetArmorArchetype(IArmorGetter armor)
        {
            ulong bipedFlags = armor.FirstPersonFlags != null ? (ulong)armor.FirstPersonFlags.Value : 0UL;
            foreach (var (name, bit) in C.ArmorSlots)
                if (bit is >= 0 and < 64 && (bipedFlags & (1UL << bit)) != 0 && Enum.TryParse<ArmorArchetype>(name, true, out var a)) return a;
            return ArmorArchetype.Accessory;
        }

        public static WeaponGroup GetWeaponGroup(IWeaponGetter wpn)
        {
            foreach (var (name, keywords) in C.WeaponGroups)
                if (HasAny(wpn, keywords) && Enum.TryParse<WeaponGroup>(name, true, out var g)) return g;
            return WeaponGroup.Unknown;
        }

        public static WeaponKind GetWeaponKind(IWeaponGetter wpn)
        {
            var group = GetWeaponGroup(wpn);
            if (group == WeaponGroup.Melee) return WeaponKind.Unknown; // Melee weapons bypass this tier
            if (wpn.BurstCount > 0 || wpn.FiringType == Weapon.FiringTypeEnum.Burst) return WeaponKind.Burst;
            if (group == WeaponGroup.Shotgun) // fast shotguns count as automatic
            {
                float sps = wpn.ShotsPerSecond > 0 ? wpn.ShotsPerSecond : (wpn.AttackSeconds > 0 ? (1.0f / wpn.AttackSeconds) : 0);
                if (sps >= 5.0f) return WeaponKind.FullyAutomatic;
            }
            if (wpn.FiringType == Weapon.FiringTypeEnum.Automatic) return WeaponKind.FullyAutomatic;
            if (wpn.FiringType == Weapon.FiringTypeEnum.SingleOrBinary) return WeaponKind.SemiAutomatic;
            return WeaponKind.Unknown;
        }

        public static IProjectileGetter? GetWeaponProjectile(IWeaponGetter wpn, ILinkCache linkCache)
        {
            if (wpn.OverrideProjectile != null && !wpn.OverrideProjectile.IsNull)
                return linkCache.TryResolve<IProjectileGetter>(wpn.OverrideProjectile.FormKey, out var p) ? p : null;
            if (wpn.AmmoType != null && !wpn.AmmoType.IsNull && linkCache.TryResolve<IAmmunitionGetter>(wpn.AmmoType.FormKey, out var ammo)
                && ammo.Projectile != null && !ammo.Projectile.IsNull && linkCache.TryResolve<IProjectileGetter>(ammo.Projectile.FormKey, out var ap))
                return ap;
            return null;
        }

        public static float GetTotalDamage(IWeaponGetter wpn, ILinkCache linkCache)
        {
            float dmg = wpn.AttackDamage;
            if (wpn.DamageTypes != null) foreach (var dt in wpn.DamageTypes) if (dt.Value > 1f) dmg += dt.Value;
            var proj = GetWeaponProjectile(wpn, linkCache);
            if (proj != null && proj.Explosion != null && !proj.Explosion.IsNull && linkCache.TryResolve<IExplosionGetter>(proj.Explosion.FormKey, out var explosion))
                dmg += explosion.Damage * (wpn.ProjectilesCount > 0 ? wpn.ProjectilesCount : 1f);
            return dmg;
        }

        public static float GetSDPS(IWeaponGetter wpn, ILinkCache linkCache)
        {
            float totalDmg = GetTotalDamage(wpn, linkCache);
            float attackSeconds = wpn.ShotsPerSecond > 0 ? (1.0f / wpn.ShotsPerSecond) : (wpn.AttackSeconds > 0 ? wpn.AttackSeconds : 0.1f);
            if (GetWeaponGroup(wpn) == WeaponGroup.Melee) return totalDmg / (attackSeconds > 0 ? attackSeconds : 1f);
            float ammoCap = wpn.AmmoCapacity > 0 ? wpn.AmmoCapacity : 1.0f;
            float reload = wpn.ReloadSpeed > 0 ? wpn.ReloadSpeed : 1.0f;
            return (ammoCap * totalDmg) / ((ammoCap * attackSeconds) + (1.0f / reload));
        }

        /// <summary>Melee speed class from the animation keyword; every weapon in a class swings at the same speed.</summary>
        public static WeaponClass GetMeleeSpeedClass(IWeaponGetter wpn)
        {
            var k = C.WeaponClassKeywords;
            if (k.TryGetValue("Knife", out var knife) && Has(wpn, knife)) return WeaponClass.Knife;
            if (k.TryGetValue("Sword", out var sword) && Has(wpn, sword)) return WeaponClass.Sword;
            return WeaponClass.Standard;
        }

        public static WeaponClass GetWeaponClass(IWeaponGetter wpn, ILinkCache linkCache)
        {
            var c = C;
            WeaponGroup group = GetWeaponGroup(wpn);
            WeaponKind kind = GetWeaponKind(wpn);
            float attackSec = wpn.ShotsPerSecond > 0 ? (1.0f / wpn.ShotsPerSecond) : (wpn.AttackSeconds > 0 ? wpn.AttackSeconds : 0.1f);
            float fireRate = wpn.ShotsPerSecond > 0 ? wpn.ShotsPerSecond : (1.0f / attackSec);
            bool Kw(string name) => c.WeaponClassKeywords.TryGetValue(name, out var fk) && Has(wpn, fk);
            switch (group)
            {
                case WeaponGroup.Pistol:
                    if (kind == WeaponKind.SemiAutomatic && GetTotalDamage(wpn, linkCache) >= 30.0f && fireRate <= 2.5f) return WeaponClass.Magnum;
                    break;
                case WeaponGroup.Rifle:
                    if (fireRate >= 15.0f) return WeaponClass.SMG;
                    if (Kw("Sniper")) return wpn.AmmoCapacity > 20 ? WeaponClass.DMR : WeaponClass.Sniper;
                    if (kind == WeaponKind.SemiAutomatic && fireRate <= 2.5f) return WeaponClass.DMR;
                    break;
                case WeaponGroup.Heavy:
                    if (Kw("Launcher")) return WeaponClass.Launcher;
                    if (Kw("Minigun")) return WeaponClass.Minigun;
                    var proj = GetWeaponProjectile(wpn, linkCache);
                    if (proj != null && (int)proj.Type == 5 && GetWeaponType(wpn) != WeaponType.Ballistic) return WeaponClass.Beam;
                    break;
                case WeaponGroup.Shotgun:
                    if (wpn.AmmoCapacity <= 2) return WeaponClass.DoubleBarrel;
                    if (fireRate <= 1.0f) return WeaponClass.PumpAction;
                    break;
                case WeaponGroup.Melee:
                    string edid = wpn.EditorID ?? "", name = wpn.Name?.String ?? "";
                    if (c.PolearmWords.Any(w => edid.Contains(w, StringComparison.OrdinalIgnoreCase) || name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                        return WeaponClass.Polearm;
                    return GetMeleeSpeedClass(wpn);
            }
            return WeaponClass.Standard;
        }

        public static WeaponType GetWeaponType(IWeaponGetter wpn)
        {
            if (GetWeaponGroup(wpn) == WeaponGroup.Melee) return WeaponType.Physical;
            foreach (var (name, fk) in C.WeaponTypes)
                if (Has(wpn, fk) && Enum.TryParse<WeaponType>(name, true, out var t)) return t;
            return WeaponType.Unknown;
        }

        static string Clean(string? s) => (s ?? "").Replace("\r", "").Replace("\n", " ").Trim();

        static bool JunkByText(ItemFilterConfig.JunkRules r, string editorId, string name) =>
            r.EditorIdStartsWith.Any(p => editorId.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || r.EditorIdContains.Any(p => editorId.Contains(p, StringComparison.OrdinalIgnoreCase))
            || r.NameContains.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));

        public static bool IsValidWeapon(IWeaponGetter wpn, ILinkCache linkCache)
        {
            var r = C.JunkWeapons;
            string editorId = wpn.EditorID ?? wpn.FormKey.ToString();
            string name = Clean(wpn.Name?.String);
            if (wpn.NonPlayable || wpn.MajorFlags.HasFlag(Weapon.MajorFlag.NonPlayable)) return false;
            if (r.RequireName && string.IsNullOrWhiteSpace(name)) return false;
            if (r.RequireEquipType && wpn.EquipmentType.IsNull) return false;
            if (JunkByText(r, editorId, name)) return false;
            // Junk keywords, also matched by their ID alone (a mod's copy of the keyword list)
            if (HasAny(wpn, r.Keywords)) return false;
            if (wpn.Keywords != null && r.Keywords.Count > 0)
            {
                var ids = r.Keywords.Select(k => k.Split(':')[0].PadLeft(6, '0').ToUpperInvariant()).ToHashSet();
                if (wpn.Keywords.Any(k => ids.Contains(k.FormKey.IDString()))) return false;
            }
            if (r.RequireDamage && GetTotalDamage(wpn, linkCache) <= 0) return false;
            return true;
        }

        public static bool IsValidArmor(IArmorGetter armor)
        {
            var r = C.JunkArmor;
            string editorId = armor.EditorID ?? armor.FormKey.ToString();
            string name = Clean(armor.Name?.String);
            if (armor.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable) || armor.MajorFlags.HasFlag((Armor.MajorFlag)0x40000000)) return false;
            if (r.RequireName && string.IsNullOrWhiteSpace(name) && !r.NameNotNeededForEditorIdStartingWith.Any(p => editorId.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return false;
            if (r.PlayableRace != null && !armor.Race.IsNull && Key(r.PlayableRace) is { } race && armor.Race.FormKey.ID != race.ID) return false;
            if (JunkByText(r, editorId, name)) return false;
            return true;
        }
    }
}
