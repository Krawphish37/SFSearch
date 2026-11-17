// formid-forms (J98, C58 W8): the header index's FormID rule is Mutagen's. FormIdResolver adapts Mutagen's
// SeparatedMasterPackage (FromMasterStyles) for the header pass; this holds the adapter to Mutagen itself, every stored
// value of a few IDs in all three numberings against Mutagen's GetFormKey, Raw against the form Mutagen writes, and the
// byte search's matcher (FormIdPattern, SFSearch's speed code) against Mutagen's EnumerateStoredForms with the indexes
// past the list. No plugin is read.
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Masters;
using Mutagen.Bethesda.Plugins.Records;
using SFSearch.Index;
using IndexStyle = SFSearch.Index.MasterStyle;

namespace SFSearch.Index.Tests;

static class FormIdFormsTests
{
    static void Check(bool ok, string what) => Ctx.Check(ok, what);

    static Mutagen.Bethesda.Plugins.MasterStyle M(IndexStyle s) => s switch
    {
        IndexStyle.Small => Mutagen.Bethesda.Plugins.MasterStyle.Small,
        IndexStyle.Medium => Mutagen.Bethesda.Plugins.MasterStyle.Medium,
        _ => Mutagen.Bethesda.Plugins.MasterStyle.Full,
    };

    public static string Run(Ctx ctx)
    {
        // Masters of every size in a mixed order, the plugin itself of each size
        var masterStyles = new[] { IndexStyle.Full, IndexStyle.Small, IndexStyle.Full, IndexStyle.Medium, IndexStyle.Small, IndexStyle.Small, IndexStyle.Full };
        var names = masterStyles.Select((_, i) => $"Master{i}.esm").ToList();
        long values = 0, forms = 0, raws = 0;
        foreach (var self in new[] { IndexStyle.Full, IndexStyle.Small, IndexStyle.Medium })
        {
            var ids = new FormIdResolver(masterStyles, self, names.Append("Plugin.esm").ToList());
            var mod = ModKey.FromFileName("Plugin.esm");
            var package = SeparatedMasterPackage.FromMasterStyles(mod, M(self), masterStyles.Select((s, i) => new KeyedMasterStyle(ModKey.FromFileName(names[i]), M(s))).ToList());
            Check(ids.FullMasters == package.OwnIndex(Mutagen.Bethesda.Plugins.MasterStyle.Full) && ids.MediumMasters == package.OwnIndex(Mutagen.Bethesda.Plugins.MasterStyle.Medium)
                && ids.SmallMasters == package.OwnIndex(Mutagen.Bethesda.Plugins.MasterStyle.Small), $"{self}: the own indexes differ from Mutagen's");
            int Owner(ModKey k) => k == mod ? ids.SelfOwner : names.FindIndex(n => ModKey.FromFileName(n) == k);
            foreach (uint id in new uint[] { 0x1, 0x800, 0xFFF, 0x1000, 0xFFFF, 0x10000, 0xFFFFFF })
            {
                // Every stored value with this ID in each numbering: the owner and local ID are Mutagen's
                var all = new List<uint>();
                if (id <= 0xFFFFFF) for (uint i = 0; i < 0xFD; i++) all.Add(i << 24 | id);
                if (id <= 0xFFFF) for (uint i = 0; i <= 0xFF; i++) all.Add(0xFD000000u | i << 16 | id);
                if (id <= 0xFFF) for (uint i = 0; i <= 0xFFF; i++) all.Add(0xFE000000u | i << 12 | id);
                foreach (uint raw in all)
                {
                    var want = package.GetFormKey(new FormID(raw), reference: true);
                    Check(ids.TryResolve(raw, out int owner, out uint local) && owner == Owner(want.ModKey) && local == want.ID,
                        $"{self}: {raw:X8} resolves to {owner}/{local:X} here, {want} in Mutagen");
                    values++;
                }
                // Each owner's forms: a master's one form is the one Raw gives; the plugin's own forms are the ones the byte
                // search's matcher finds among all those values, no more, no fewer
                for (int owner = 0; owner <= ids.SelfOwner; owner++)
                {
                    var key = new FormKey(owner == ids.SelfOwner ? mod : ModKey.FromFileName(names[owner]), id);
                    var want = package.EnumerateStoredForms(key, includePastList: owner == ids.SelfOwner).Select(f => f.Raw).ToHashSet();
                    var raw = ids.Raw(owner, id);
                    Check(raw == (want.Count > 0 ? package.EnumerateStoredForms(key).First().Raw : null), $"{self}: owner {owner} ID {id:X}: Raw gives {raw:X8}");
                    raws++;
                    var pattern = FormIdPattern.For(ids, owner, id);
                    var found = all.Where(r => pattern != null && pattern.Matches(BitConverter.GetBytes(r))).ToHashSet();
                    Check(found.SetEquals(want), $"{self}: owner {owner} ID {id:X}: the byte search finds {found.Count} forms, Mutagen stores {want.Count} ({string.Join(" ", want.Except(found).Take(3).Select(x => x.ToString("X8")))} missed, {string.Join(" ", found.Except(want).Take(3).Select(x => x.ToString("X8")))} extra)");
                    forms += want.Count;
                }
            }
        }
        // A missing master: the masters before it resolve as Mutagen reads them, everything at or past their places is
        // unresolved, and nothing is written for the plugin's own records
        var missing = new FormIdResolver(new[] { IndexStyle.Full, IndexStyle.Missing, IndexStyle.Small }, IndexStyle.Small, new[] { "A.esm", "B.esm", "C.esm", "Plugin.esm" });
        Check(missing.TryResolve(0x00000800, out int o0, out _) && o0 == 0, "missing master: 00000800 isn't A.esm's");
        Check(!missing.TryResolve(0x01000800, out int _, out _) && !missing.TryResolve(0xFE000800, out int _, out _), "missing master: a place past A.esm resolved");
        Check(missing.Raw(missing.SelfOwner, 0x800) == null && missing.Raw(2, 0x800) == null && missing.Raw(0, 0x800) == 0x00000800, "missing master: Raw wrote a form it can't know");
        return $"{values:N0} stored values resolve as Mutagen's GetFormKey; {raws} forms written as Mutagen writes them; the byte search finds exactly Mutagen's {forms:N0} stored forms (3 own sizes, 7 masters of mixed sizes, 7 IDs); a missing master ends what resolves";
    }
}
