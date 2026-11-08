using System;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Plugins;

namespace SFSearch
{
    public static class MetadataExtractor
    {
        public static bool IsJunkWeapon(IWeaponGetter wpn, Mutagen.Bethesda.Plugins.Cache.ILinkCache linkCache)
        {
            return !ItemFilter.IsValidWeapon(wpn, linkCache);
        }

        public static bool IsJunkArmor(IArmorGetter armor)
        {
            return !ItemFilter.IsValidArmor(armor);
        }

        public static string GetWeaponGroup(IWeaponGetter wpn)
        {
            return ItemFilter.GetWeaponGroup(wpn).ToString();
        }

        public static string GetArmorArchetype(IArmorGetter armor)
        {
            return ItemFilter.GetArmorArchetype(armor).ToString();
        }
    }
}
