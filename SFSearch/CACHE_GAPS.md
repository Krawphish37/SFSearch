# SFSearch cache gaps

Record types and fields the cache doesn't hold, written down when they came up so they can be added later. Until then, `sfs.cmd Live` reads them straight from the file.

Always not cached in full (by design; see trap 9 in the `sfsearch-pipeline` skill): dialogue, scenes, navmeshes, cells, worldspaces, planets, biomes, statics, lights, sounds, visual records, ship-builder parts (GenericBaseForm named SM + letters + _). In a plugin with more than 50,000 placed references (Starfield.esm, ShatteredSpace.esm and any big mod, e.g. nfvc0003vigilance.esm; `Meta` `PlacedMode` tells), only placed objects with scripts are cached in full. Placed NPCs are always cached in full.

Since 2026-11-06, any of these can be stored for one plugin with `sfs.cmd Add <plugin> <Type>`, with no rebuild. A gap only needs adding to the list here, and to `CachedSignatures`, if every plugin should store it.

## Open
| Type / field | Plugin where it came up | Needed for | `Live` that worked | Date |
|---|---|---|---|---|

## Done
| Type / field | Added | Notes |
|---|---|---|
| GenericBaseForm ships (GBFM) and LeveledBaseForm lists | 2026-11-04 | Came up in the TGO pilot check (SFBGS050.esm), which took 43 min 50 s by hand. Whole ships are cached in full; ship-builder parts (names matching `^SM[A-Z]{1,3}_`) keep only a `Forms` row, and `Stats` shows the type as "Partly". Digits are left out of the pattern because SM015_DeimosStaryard, SM016_, SM018_ and SM020_TheDen (Starfield.esm) are real stations with pilots. No part in the three plugins measured has a pilot. `Refs` on SFTER_MQ03_ShieldAMPTest_AutopilotAI (02C512:SFBGS050.esm) now finds SFTER_MQ03_ShieldAMPTest (02C510:SFBGS050.esm).  |
| Location (LCTN) | 2026-11-04 | Came up in the TGO level-boost check. Encounter levels are at `$.Owner.EncounterMinLevel` / `EncounterMaxLevel`, not at the top level. SKavnyk (003752:ShatteredSpace.esm) reads min 35, max -1, parent 01A53A:Starfield.esm.  |
| ResearchProject (RSPJ) with its conditions (CTDA) | before 2026-10-20 (cache schema v4); checked 2026-11-03 | Never really a gap. The old RSPJ crash (no `CITC` count in DSM's conditions) only hit `StarfieldMod.CreateFromBinary`, which the patcher uses. SFSearch reads every plugin through `CreateFromBinaryOverlay`, which never needed `CITC`, and `ResearchProject` has been in `CachedSignatures` since schema v4. Checked 2026-11-03: DSM 81 RSPJ with 197 conditions (rspj_DSM_Codex_Weapon_Cornered (00173C) has 3), darkstar.esm 83 with 83; `Live --vs-cache` 0 values differ; `Refs` finds 1,134 links out of DSM's RSPJ. No code change.  |
| PlacedNpc (ACHR) in plugins over the 50,000 placed-reference limit | 2026-11-01 | Came up in nfvc0003vigilance.esm (the TGO corpse check needed Starts Dead on placed NPCs). New `AlwaysFullPlaced` set in `Program.cs`: placed NPCs are cached in full whatever the count, the limit stays for PlacedObject. Vigilance 286/286, Starfield.esm 9,530 (+22 MB), ShatteredSpace.esm 3,551 (+8 MB). `Live --vs-cache` 0 values differ.  |
| PackIn (PKIN) records | 2026-11-01 | Added to `CachedSignatures` (Program.cs). The pack-in's own VMAD and Keywords are now in Records and `Refs`; `Q vmad` / `Q kw` read them. darkstar.esm: 279 pack-ins; `Refs` on kw_DarkStar_LocationHasAtmosphereGenerator (00239C) now finds the 6 landing pads plus 5 `deprecated_pi_DarkStar_*` ones. The objects a pack-in places were already cached as placed references in its storage cell (`Forms.Parent`). Matches `Live --vs-cache` (0 values differ). |
| MoveableStatic (MSTT) records | 2026-11-01 | Added to `CachedSignatures` with PackIn. darkstar.esm 9, Starfield.esm 1,771. |
| WeaponModification / ArmorModification model path (MODL) | 2026-10-27 | Was cached all along at `json_extract(Data,'$.Model.File.GivenPath')` (NULL when the OMOD has no MODL). An OMOD without a mesh shows only `{"LightLayer":"1"}`, which made it look missing. `Q omod` now has a `Mesh` column; the path is documented in the skill's `references/database.md`. |
