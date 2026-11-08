# SFSearch commands in depth

Full flag lists: `sfs.cmd --help`. This file explains what the bigger commands print and how to read it. `sfs` below is short for `& "<SFSearch folder>\SFSearch\sfs.cmd"`.

## Blueprint research: MkDiff, OmodCensus, RecipeCensus
They answer "how does mod X build its Mk.II / quality tiers / recipes" in about a second each, on any plugin (DSM, darkstar.esm, a third-party mod). Each looks records up through the plugin's masters by itself, and takes `--with p1,p2` (more plugins to look in) and `--out report.md`. Run these first for blueprint questions. Write SQL only for what they don't cover.

### `MkDiff <plugin> [--sig Armor|Weapon] [--by ...] [--list Field] [--pairs] [--num f1,f2] [--all]`
Pairs each new armor/weapon with its base: the one item of the same type among the components of the recipe that makes it. So a Mk.II pairs with its vanilla base and a Mk.III with its Mk.II; extra components (such as DSM's particle core) are reported. It prints:
- **Pairing quality:** new records, paired, clean 1:1, recipes whose only component is the base, shared bases, unpaired items.
- **Values every created item carries**, and whether the base already had them.
- **Per category:** the Name/EditorID pattern ("<base> Mk.II" ×N), which top-level fields differ (count, plus the new value when it's the same everywhere), and the fields that never differ.
- **List fields as sets**, with ADD / DEL / KEEP per value and EditorIDs resolved: Keywords, AttachParentSlots, every other FormKey list, the Default template's Includes/Keywords (`Template[0]`), all later templates (`Template[1+]`), and `Components (kinds)` (e.g. QualityMods removed). Also template counts base→created and the created template names.
- **Numbers** (created − base): ArmorRating, Weight, Value, Health, Resistances per damage type, AttackDamage, DamageTypes per type, AmmoCapacity, BaseValue, BaseWeight (`--num` adds more). Each shows the spread and the ratio range. When one change is the rule, the exceptions are named; when every item differs it says "set per item".

Options:
- `--by` sets the categories: `prefix:N` (first N `_` parts of the created EditorID; armor default `prefix:3`), `kind` (Melee/Ranged; weapon default `prefix:2,kind`), `archetype`, `bench`, `none`. Combine with commas.
- `--list ArmorRating` adds a base-vs-created table for one field. `--pairs` lists every pair. `--all` shows everything (by default values changed on under 5 % of pairs are hidden and list tables stop at 15 rows).

```powershell
sfs MkDiff DarkStar_Manufacturing.esm --sig Armor --out mkdiff_armor.md
sfs MkDiff darkstar.esm --sig Weapon --list AttackDamage
```

### `OmodCensus <plugin> <EditorID glob> [--top N] [--all]`
Censuses quality OMODs whose EditorID matches the glob (`*` and `?`, any case). The tier comes from the `_Quality_N` / `_Quality_TiersAny` suffix (`Quality_08` counts as Q8). A *set* is the tier OMODs that share their target keywords; EditorIDs can't define sets, because DSM names TiersAny records differently from their tiers. It prints:
- Sets and whether each has every tier once.
- Per tier: AttachPoint, target keyword count, flags, MaxRank/LTSO, Name, and how many property shapes there are.
- Per tier properties, grouped by exact signature (`Rating Add 12 · DamageResistance[dtEnergy] Add 8 · Keyword[dn_Armor_HasQuality_2] Add · Value MultAndAdd 0.25`). When values differ per item (weapons), the shape with min…max per property.
- TiersAny ladders: MinimumLevel lists, ladder shapes (first level + steps, e.g. `Q1 1, steps +7 +6 +6 +6 +6 +15 +30`), tier order, include flags. Problems are flagged: repeated or missing tiers, out of order, levels going down, includes from another set.

```powershell
sfs OmodCensus DarkStar_Manufacturing.esm "mod_DSM_Mk2_Helmet_*"
sfs OmodCensus DarkStar_Manufacturing.esm "mod_DSM_Mk?_Weapon_*" --out w.md
```

### `RecipeCensus <plugin> [--bench <EditorID|FormKey>] [--created <Signature>] [--filter <glob>] [--exclude <glob>] [--all]`
Groups every COBJ by bench keyword → created record type. Per group it prints:
- What every recipe has in common, including shared conditions (e.g. `GetValue(DSM_DarkStarDetected) == 0 on Reference PlayerRef`).
- The GetLevel spread.
- A per-tier table of only what varies: extra conditions, components (`<Armor>×1` for an item component), RequiredPerks, RecipeFilters, IFK, MenuSortOrder, the Description pattern (e.g. `= created name + "⏎⏎WARNING: Will consume original"`).
- Checks: created tier ≠ recipe tier, objects made by several recipes, and outliers (recipes that differ from an 80 %+ majority of their tier).

```powershell
sfs RecipeCensus DarkStar_Manufacturing.esm --out rc.md
sfs RecipeCensus DarkStar_Manufacturing.esm --bench WorkbenchArmorKeyword --filter "co_qmod_*" --exclude "*ForceArmor*"
```

## `Profile <plugin> [--against <plugin>] [--out dossier.md]`
A dossier on a mod before patching it:
1. Header flags, Light/Medium room left, masters, and the file counts of its `.ba2` archives (`.nif`/`.mesh`, `.mat`, `.dds`, `.pex`).
2. Record census and the most common EditorID prefixes.
3. What its overrides change (Mesh/Visual, Stats, List, Keywords/APPR, Templates/Quality, ITM).
4. Playable equipment and how the mod hands it out.
5. Asset check across loose files, mod BA2s and vanilla BA2s.
6. Only with `--against`: its overlap with that plugin (records both change, and what each changes) and its new playable equipment, which a patch for that plugin would need to cover.

An add-on can build on it: `ProfileCommand.GenerateDossier` prints sections 1 to 6, and the add-on adds its mod's own sections.

## Lookup and compare commands
- **`Inspect` / `Read` / `Dump <plugin> [Sig|*] <record>`:** fields with FormKeys labelled, xEdit layout, raw JSON. The record type is optional.
- **`Live <plugin|path> <record> [--sig T]`:** reads one record straight from the plugin and its masters, with no cache. `--field Path[0].X` for one field, `--chain` for the override chain with changed fields, `--links` to resolve outbound links, `--vs-cache` to compare with the cache, `--subrecords` for raw subrecords, `--with p1,p2` for plugins loaded after it. Use it for record types the cache leaves out, or to double-check a cache answer. `--links` takes each link's type, EditorID and source plugin from the caches' `Forms` tables, or from a header pass when a cache is missing, stale or the file was read by path.
- **`Live <plugin|path> --sig T --field "Path[*].X"`:** one field from every record of a type, read from the file. In `--field`, `*` matches anything. Field names are the ones in `Live`'s own JSON (the EditorID field is `EDID - Editor ID`). `--filter "EDID*"` narrows by EditorID, `--value text` keeps values containing the text (a link's value is its FormKey, not its name), `--new` skips overrides, `--empty` also lists records with no match, `--all` lifts the 1,000-row limit. About 1 to 4 s on most types; Cell is the slow one (24 s on SFBGS050.esm).
- **`Live <plugin|path> <record> --refs [--with p1,p2]`:** what points at a record, from the plugin's raw bytes, each hit confirmed through Mutagen. It searches only the named plugins. It names the real record (a scene or topic), where `Refs` files those under their parent Quest with a path like `Scenes[3]...`. It doesn't count a topic's own `FormKey` row the way `Refs` does. "Not confirmed as links" lists bytes that matched but aren't a link Mutagen reads, or records inside a cell other than placed references (navmeshes): usually coincidence, never counted. Many hits in Cell records can take about 15 s.
- **`Diff <p1> <p2> [Sig|*] <rec1> [rec2]`:** `--summary` (changed top-level fields only), `--ignore f1,f2`, `--expect f1,f2` (exit code 1 when any other field changed).
- **`Delta <plugin> [record] [--detail]`:** compares every override with the version before it in the masters, in master order, and labels the change (ITM, Mesh/Visual, Stats, List, Keywords/APPR, Templates/Quality, Scripts). `--detail` or a record gives a side-by-side field table.
- **`Conflicts <record> [--in plugins]`:** every plugin with that FormKey in load order, the origin and the winner, and the fields each one changes. With no `--in` it reads every plugin in Data that has the record (found from record headers) and builds their databases as needed, so a plugin with no database can't hide the winner.
- **`Refs <record> [--in plugins]`:** every record that points at it, with the exact field path. With no `--in` it searches every plugin in Data that lists the record's plugin as a master. One with no database gets one only when its file mentions the record's bytes, so the first search for a Starfield.esm record can take about a minute; later ones take about a second. Plugins read by path that have a database are searched too.
- **`Names <FormIDs> [--in p1,p2]`:** a full FormKey is looked up in its own plugin. A bare ID is tried as a new record of each `--in` plugin (default darkstar.esm, Starfield.esm, DarkStar_Manufacturing.esm), and every plugin that has it is listed. Columns: Input, Written as (`EditorID (FormID:plugin)` or `[no EditorID] (…)`), Signature, Name.
- **`Header` / `Stats <plugin|path>`:** size flag, header flags, `MaxLocalFormID` room, masters in order / record counts per type, including types not cached.
- **`Assets <plugin> [--missing]`:** checks every `.nif` / `.mesh` / `.mat` path on the records and their ArmorAddons against loose files, the mod's `.ba2` archives (read with `bsa-ba2-tool.exe`, listed once into `Cache\ba2_*.txt`) and vanilla `Starfield - Meshes*.ba2`.
- **`PluginDiff <old> <new> [--detail] [--shifts]`:** compares two versions of a plugin (`.bak` files too). New records are matched by type + EditorID, and local FormIDs are normalized so shifted FormIDs don't show as changes. When one file masters the other, every record is matched by FormKey instead: each override is compared with the record it overrides, and what the patch doesn't contain isn't listed as removed. Watch out for repeated EditorIDs (SKILL.md trap 8).

## Saved queries (`Q`)
`sfs Q --list` shows them. `Q flst <plugin> [list]` lists FormLists or expands one (0-based index, item FormKey, EditorID, type, name). `vmad` scripts and properties, `appr` attach slots, `kw` keywords, `templates` ObjectTemplates and their OMODs, `cobj` recipe summary, `omod` OMOD summary, `arma` armor → ArmorAddon → male/female meshes and slots, `new` / `overrides` by type. They need no PowerShell `$` escaping.
