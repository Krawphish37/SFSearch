# The SFSearch tools in depth

Each tool with its arguments and what it prints. Every answer below is real: it was run on a Data folder of 370 plugins (224 loaded) and cut with "…" where long. Calls are written as a tool name and its arguments. On the command line the same call is the verb with the arguments as options (`SKILL.md`, "The command line").

**What every tool shares:** `cursor`, `limit` or `rows`, `max_chars`, `language`, `format`, `allow_heavy` (`SKILL.md`, "How to read an answer").

**An argument the MCP tool doesn't list** still works through `sfs_run` with the verb: `{"args": ["refs2", "0076BE:kgcdoom.esm", "--in", "kgcdoom.esm", "--search", "bytes"]}`. Those are: `describe` `--all` and `--max-chars`; `query2` `--max-chars`; `saved` `--plugins`, `--scope`, `--language`, `--max-chars`, `--overwrite`; `refs2` `--search`; `compare` `--plugin`, `--max-chars`, `--language`; `loadorder2` `--since`, `--limit`; `check2` `--max-chars`, `--language`.

## sfs_find: what is this?
Arguments: `query`, `type`, `in`, `limit`. It answers from the header index: no record is opened.

| `query` | Finds |
|---|---|
| `0476C4:Starfield.esm` | A FormKey: the record, every plugin that holds it, the winner first |
| `000CF2` | A FormID with no plugin: every record with that ID, in every plugin. None is picked |
| `0A0003E6`, `FE09A123` | A FormID with its runtime slot, read with today's load order |
| `Eon` | An EditorID (letter case doesn't count) |
| `Super Shotgun` | Words that are no EditorID: every record whose in-game name holds all of them, as whole words |
| `kgcDoom_WEAP_*` | A pattern, held against EditorIDs and in-game names: `*` is any text, `?` one character |

- Several at once: separate them with spaces or commas. An `Asked` column says which row answers which. Words separated by spaces only are also read as one in-game name (`New Atlantis`, `Akila City`): records whose name is exactly those words come first. Commas make a list of EditorIDs only.
- `type` is a signature (`WEAP`) or a Mutagen name (`Weapon`). Alone, it lists every record of the type.
- `in` keeps to records these plugins hold: names in Data (wildcards allowed), or the path of a plugin file elsewhere.
- **A word that is a record's EditorID is that record.** It isn't also looked for in names. To search names for such a word, use a pattern: `*Eon*`. One exception: a word that is only layers' EditorIDs (LAYR: `Neon`, `Akila`, `City`, `new`) is looked for in names too, and the named records come first.
- **In-game names come from the link index**, or from the plugin's own bytes while its link file isn't made. Cells and worldspaces are searched by name too, from their own records' bytes (never what they hold): a cell's row says `interior`, or its worldspace and grid in the `Inside` column.

```text
> sfs_find {"query": "0003E6 000AA2", "in": "darkstar.esm"}
| Asked | Record | Type | Name | Held by (winner first) |
| :--- | :--- | :--- | :--- | :--- |
| 0003E6 | ammo_DarkStar_Volatile40mmXPL (0003E6:darkstar.esm) | AMMO Ammunition | DarkStar Vol. 40mm XPL | darkstar.esm |
| 000AA2 | lmod_Legendary_Wep_2_Poison (000AA2:darkstar.esm) | OMOD ObjectModification* | Poison | darkstar.esm |
[notes] * OMOD holds 8 Mutagen types (ArmorModification, ContainerModification, FloraModification, NpcModification, ObjectModification, SpaceshipModification, UnknownObjectModification, WeaponModification). The header index doesn't say which one a record is.
[source] darkstar.esm 25.2 MB, key 19fae728; load order (224 loaded, plugins.txt) 9 KB, key b730711a
[cost] 26 ms; read the header index of 370 plugins (checked in 17 ms)

> sfs_find {"query": "000CF2"}
| Record | Type | Name | Inside | Held by (winner first) |
| :--- | :--- | :--- | :--- | :--- |
| [no EditorID] (000CF2:Starfield.esm) | REFR PlacedObject |  | CELL LC008SlaytonAerospace (06A716:Starfield.esm) | Starfield.esm |
…
| SFBGS003_MiscPointer (000CF2:SFBGS003.esm) | QUST Quest | The Trackers Alliance |  | SFBGS003.esm |
…
[more] 40 of 48 rows shown. Next: cursor "40.cc8a".
[notes] 48 records have the ID 000CF2. They are different records: name the plugin (FormID:plugin, or in) to mean one.

> sfs_find {"query": "Super Shotgun", "in": "kgcdoom.esm"}
| Asked | Record | Type | Name | Held by (winner first) |
| :--- | :--- | :--- | :--- | :--- |
| Super Shotgun | kgcDoom_MGEF_EnchApplyPerkDmg (0076F2:kgcdoom.esm) | MGEF MagicEffect | Super Shotgun Ench | kgcdoom.esm |
| Super Shotgun | kgcDoom_ENCH_ConstSelfApplyDmgBonus (007710:kgcdoom.esm) | ENCH ObjectEffect | Super Shotgun | kgcdoom.esm |
| Super Shotgun | kgcDoom_Book_ShotgunNote (007720:kgcdoom.esm) | BOOK Book | Notes on Super Shotgun | kgcdoom.esm |
| Super Shotgun | kgcDoom_WEAP_SuperShotgun (007751:kgcdoom.esm) | WEAP Weapon | Super Shotgun | kgcdoom.esm > darkstarpatch_athellsgate.esm (not loaded) |
[notes] No record has the EditorID "Super Shotgun": the row(s) are the 4 record(s) whose in-game name holds those words.

> sfs_find {"query": "00039E", "in": "Starfield.esm"}
No record has the ID 00039E in Starfield.esm (3,829,246 records looked at), and no EditorID is "00039E".
```
`Held by` lists the plugins that hold the record, the winner first. A plugin marked `(not loaded)` is in Data but the game doesn't load it.

## sfs_record: one record
Arguments: `record`, `plugin`, `view`, `fields`. The record is read live from the plugin file.
- `record` is a FormKey, an EditorID, or a FormID with `plugin`. A name that fits several records lists them and picks none. When one record of that EditorID is in a loaded plugin and the others are only in plugins that aren't loaded, the loaded one is read and a note names the others.
- `plugin` says whose version to read: a name in Data or a file path. Left out, it is the version the game uses. **The third line of every answer says which version was read, and why.**
- A record of over 16 MB isn't read without `allow_heavy`.

| `view` | Gives |
|---|---|
| `card` (default) | The fields that are set, in file order, with readable names and the subrecord beside them. Links as `EditorID (FormID:plugin)`. Unknown fields with their bytes |
| `fields` | The fields named in `fields`, by Mutagen path (`AttackDamage`, `Keywords[7]`, `Components[*].SpaceshipAIActor`). A field the record leaves out shows its default |
| `json` | The record as Mutagen's JSON: every value, under the names SQL uses |
| `raw` | The record as the file has it: the header and every subrecord's bytes. The header's FormID line says how it is stored and what it means ("FE001863: the Small (light) form, Small number 001, own record 0x863"); `check` counts the forms over the whole plugin |
| `versions` | Every plugin that holds the record, in load order, the winner marked |
| `evidence` | For the fields named: the file's bytes beside the Mutagen field and the xEdit path |
| `verify` | The JSON turned back into a record, written by Mutagen and compared with the file's bytes. "equal" proves the JSON holds everything |

```text
> sfs_record {"record": "0076BE:kgcdoom.esm"}
kgcDoom_Tag_WeaponTypeDoom (0076BE:kgcdoom.esm)
KYWD Keyword in kgcdoom.esm (its own record), at 0x29D, 63 bytes
Read from kgcdoom.esm: the only plugin that holds it.
  Form Key: 0076BE:kgcdoom.esm
  Version Control: 2110538
  Editor ID (EDID): kgcDoom_Tag_WeaponTypeDoom
  Form Version: 582
  Version2: 2
  Color (CNAM): #00FFFFFF
  Type (TNAM): None
  Form Link Target Type (FNAM): 0
[source] kgcdoom.esm 3.5 MB, key 9561e0c8; load order (224 loaded, plugins.txt) 9 KB, key b730711a
[cost] 32 ms; read 1 KYWD of kgcdoom.esm

> sfs_record {"record": "kgcDoom_WEAP_SuperShotgun", "view": "fields", "fields": "AttackDamage,HasScope,Keywords[7],Name"}
kgcDoom_WEAP_SuperShotgun (007751:kgcdoom.esm)
WEAP Weapon in kgcdoom.esm (its own record), at 0xD313, 2,403 bytes
Read from kgcdoom.esm: the plugin that wins it (the last loaded of the 2 that hold it). Held by (winner first): kgcdoom.esm > darkstarpatch_athellsgate.esm (not loaded).
  AttackDamage = 80.0
  HasScope = false   [the default: the record's JSON leaves it out, and the schema says the field is always there]
  Keywords[7] = kgcDoom_Tag_WeaponTypeDoom (0076BE:kgcdoom.esm)
  Name = Super Shotgun

> sfs_record {"record": "kgcDoom_WEAP_SuperShotgun", "view": "fields", "fields": "Name", "language": "German"}
…
  Name = Super-Shotgun

> sfs_record {"record": "kgcDoom_WEAP_SuperShotgun", "view": "evidence", "fields": "AttackDamage"}
…
AttackDamage = 80.0
  file     WEAP/WDMG, offset 0, 4 bytes: 00 00 A0 42
           WDMG is 62 bytes: 00 00 A0 42 00 00 A0 41 00 00 0C 42 …
  Mutagen  Weapon.AttackDamage (Float)
  xEdit    \WDMG - Damage\Base Damage

> sfs_record {"record": "0076BE:kgcdoom.esm", "view": "raw"}
kgcDoom_Tag_WeaponTypeDoom (0076BE:kgcdoom.esm)
KYWD in kgcdoom.esm at 0x29D: a 24-byte header and 63 bytes
Read from kgcdoom.esm: the only plugin that holds it.
header  4B 59 57 44 3F 00 00 00 00 00 00 00 BE 76 00 FD 4A 34 20 00 46 02 02 00
        flags 00000000, FormID FD0076BE → kgcDoom_Tag_WeaponTypeDoom (0076BE:kgcdoom.esm), version control 0020344A, form version 582, second version number 2
EDID      27  6B 67 63 44 6F 6F 6D 5F 54 61 67 5F 57 65 61 70 6F 6E 54 79 70 65 44 6F 6F 6D 00
CNAM       4  FF FF FF 00
TNAM       4  00 00 00 00
FNAM       4  00 00 00 00
4 subrecords, 63 bytes

> sfs_record {"record": "2377CF:Starfield.esm", "view": "versions"}
dn_CommonGun (2377CF:Starfield.esm) is held by 2 plugins. The game uses the version in SFBGS00D.esm.
| Order | Plugin | Slot | Wins | EditorID there | Type | Bytes | Notes |
| :--- | :--- | :--- | :--- | :--- | :--- | ---: | :--- |
| 0 | Starfield.esm | 00 |  | dn_CommonGun | INNR InstanceNamingRules | 3,211 | its own record |
| 10 | SFBGS00D.esm | 03 | yes | dn_CommonGun | INNR InstanceNamingRules | 3,401 | an override |

> sfs_record {"record": "0076BE:kgcdoom.esm", "view": "verify"}
…
  kgcDoom_Tag_WeaponTypeDoom (0076BE:kgcdoom.esm) KYWD: equal (87 bytes: the 24-byte header and the subrecords, uncompressed)

> sfs_record {"record": "007751", "plugin": "kgcdoom.esm", "view": "fields", "fields": "BaseDamage"}
Error: Weapon has no field "BaseDamage". Fix: the nearest real names: AttackDamage (Float, WDMG, xEdit name "Base Damage"); MeleeBashDamage (Float, WMEL, xEdit name "Bash Damage"); BaseValue (UInt32, WGEN). The card view shows every field that is set, with its Mutagen name.
```
`versions` says who holds a record. What each version changes is `sfs_compare` with `kind: "versions"`.

## sfs_describe: field names
Arguments: `what`, `filter`. It reads the schema only, never a plugin. The schema is made from Mutagen's own record definitions, with xEdit's names beside them.

| `what` | Gives |
|---|---|
| `WEAP` or `Weapon` | The record type's fields. `filter` keeps the fields whose path, readable name or subrecord holds a text |
| `OMOD` | A signature of several types lists them |
| `Weapon.AttackDamage` | One field, by its Mutagen path |
| `WEAP/WDMG`, `WEAP/WDMG@16` | The fields of a subrecord, or the one at an offset |
| `Base Damage` | A field by its readable (xEdit) name |
| `SoundLevel` | An enum: its values, and the fields that use it |
| any other word | The fields named so, in every record type; when no field is named so, the fields whose name holds the word |
| `*ammo*` | Every field whose name holds the word. This is the way to search field names for a word that is itself a record type's signature or name: `ammo` alone is the type `AMMO`, and its answer's note says so |

```text
> sfs_describe {"what": "WEAP", "filter": "damage"}
# Weapon (WEAP): 14 fields matching "damage"

| Path | Sig | Type | xEdit name | Notes |
|---|---|---|---|---|
…
| AttackDamage | WDMG | Float, 4 B @0 | Base Damage |  |
| OutOfRangeDamageMult | WDMG | Float, 4 B @12 | Out of Range Damage Mult |  |
| CritDamageMult | WDMG | Float, 4 B @16 | Crit Damage Mult |  |
| DamageTypes | DAMA | List |  | may be absent |
| DamageTypes[*] | DAMA | Object WeaponDamageType |  |  |
| DamageTypes[*].DamageType | DAMA | FormLink → DamageType, 4 B @0 | Type |  |
…
[source] schema c7f341f33eee61e7, made from Mutagen 0.55.0-fixes.11's record definitions (no plugin read)
[cost] 6 ms

> sfs_describe {"what": "Weapon.AttackDamage"}
# Weapon.AttackDamage

- Mutagen path: Weapon.AttackDamage
- File address: WEAP/WDMG, offset 0, 4 bytes
- Readable name: Base Damage (xEdit's name; xEdit path \WDMG - Damage\Base Damage; checked on 406 records, 406 agree)
- Type: Float, 4 B @0
- Always there: a value the JSON leaves out is the default.
- Defined in: Weapon (Major Records/Weapon.xml)

> sfs_describe {"what": "SoundLevel"}
# SoundLevel: an enum of 6 values (a field holds one)
Used by 9 field(s): AcousticSpace.SoundDetectionLevel, Door.SoundLevel, Explosion.SoundLevel, Impact.SoundLevel, MagicEffect.CastingSoundLevel, Projectile.SoundLevel, SoundMarker.Level, Weapon.SoundLevel, Weapon.MeleeDetectionSoundLevel.

| Name | Value | xEdit name |
|---|---|---|
| None | 0 |  |
| Silent | 1 |  |
…

> sfs_describe {"what": "Weapon.BaseDamage"}
Error: Weapon has no field "BaseDamage". Fix: the nearest real names: AttackDamage (Float, WDMG, xEdit name "Base Damage"); MeleeBashDamage (Float, WMEL, xEdit name "Bash Damage"); BaseValue (UInt32, WGEN).
```
How to read a row:
- **Path** is the name to use: a column in SQL (`AttackDamage`), a path in `sfs_record`'s `fields`, a key in the JSON. `[*]` marks a list. `{DestructibleObjectComponent}` marks one kind of a field that can hold several kinds.
- **Type** gives the kind, the size and the offset inside the subrecord (`Float, 4 B @0`). `FormLink → DamageType` is a link, with the record type it points at.
- **Notes:** "may be absent" means the field can be missing; without it, a value the record leaves out is the default. A field Mutagen has no real name for yet is marked unknown and is still shown everywhere, with its bytes.

## sfs_refs: links
Arguments: `record`, `direction`, `in`, `types`, `world`, `plugin`.
- **`direction: "in"`** (default): the records that point at it. One row per record and field. The field is the link's path in the record's JSON, with list positions written `[*]`.
- **`direction: "out"`**: what the record points at, field by field. `plugin` says whose version to read.
- **`direction: "both"`**: both, one after the other.
- `in`: the plugins to search. Left out: the record's own plugin and every plugin in Data that lists it as a master.
- `types`: only records of these types.
- **The `Searched:` line says what was and wasn't searched.** World records (cells' content, placed references, navmeshes, terrain) aren't in the link index. `world: true` searches their bytes too, and the line says what that would cost.
- `Wins` says whether that plugin's version of the pointing record is the one the game uses.

```text
> sfs_refs {"record": "0076BE:kgcdoom.esm", "in": "kgcdoom.esm"}
4 records point at kgcDoom_Tag_WeaponTypeDoom (0076BE:kgcdoom.esm): 4 links in 1 plugin.
Searched: the link index of 1 plugin (every record outside cells' content, navmeshes and terrain: 731 records). Not searched: world records (cells' content, navmeshes, terrain): 14,597 records, 3.2 MB in 1 plugin. Add world: true to search them (about 1 s, more when many records have to be confirmed).
| Record | Type | Field | Read from | Wins |
| :--- | :--- | :--- | :--- | :--- |
| kgcDoom_MGEF_EnchShotgunCloak (0076FA:kgcdoom.esm) | MGEF MagicEffect | VirtualMachineAdapter.Scripts[*].Properties[*].Object | kgcdoom.esm | yes |
| kgcDoom_Perk_BonusDamageToPredators (00B999:kgcdoom.esm) | PERK Perk | Ranks[*].Effects[*].Conditions[*].Conditions[*].Data.FirstParameter | kgcdoom.esm | yes |
| kgcDoom_WEAP_CrucibleBlade (007750:kgcdoom.esm) | WEAP Weapon | Keywords[*] | kgcdoom.esm | yes |
| kgcDoom_WEAP_SuperShotgun (007751:kgcdoom.esm) | WEAP Weapon | Keywords[*] | kgcdoom.esm | yes |
[source] kgcdoom.esm 3.5 MB, key 9561e0c8; load order (224 loaded, plugins.txt) 9 KB, key b730711a
[cost] 31 ms; read the link index of 1 plugin

> sfs_refs {"record": "0076BE:kgcdoom.esm", "in": "kgcdoom.esm", "world": true}
4 records point at kgcDoom_Tag_WeaponTypeDoom (0076BE:kgcdoom.esm): 4 links in 1 plugin.
Searched: the link index of 1 plugin (every record outside cells' content, navmeshes and terrain: 731 records); world records (cells' content, navmeshes, terrain) by their bytes: 14,597 records of 1 plugin.
…

> sfs_refs {"record": "kgcDoom_WEAP_CrucibleBlade", "direction": "out"}
kgcDoom_WEAP_CrucibleBlade (007750:kgcdoom.esm) points at 40 records through 40 links (its version in kgcdoom.esm: the plugin that wins it (the last loaded of the 2 that hold it)).
| Field | Points at | Type |
| :--- | :--- | :--- |
| Transforms.InventoryIcon | Inv_Melee_Inventory_02 (2EFD82:Starfield.esm) | TRNS Transform |
…
| Keywords[7] | kgcDoom_Tag_WeaponTypeDoom (0076BE:kgcdoom.esm) | KYWD Keyword |
…
| ObjectTemplates[0].Includes[0].Mod | kgcDoom_mod_Crucible_Blade_Standard (00BA2C:kgcdoom.esm) | OMOD ObjectModification* |
…
```
Until the link index is built, a plugin with no link file yet is answered by a search of its bytes, and the `Searched:` line says so. The links are the same.

## sfs_compare: five kinds of comparison
Arguments: `kind`, `a`, `b`, `record`, `record_b`, `expect`, `match`, `as`, `filter`, `detail`. A plugin is a name in Data or a file path.

| `kind` | Question | Arguments |
|---|---|---|
| `versions` | Who holds this record, who wins, what does each change against the one before? | `record` |
| `records` | How do two records differ? | `a` and `b` as two FormKeys; or `a` and `b` as two plugins with `record` (and `record_b` for another record in `b`). `expect`: the fields allowed to differ |
| `plugin_vs_masters` | What does a plugin change in its masters? Which overrides change nothing (ITMs), which override nothing? | `a`. `filter`: a text of the EditorID, one FormKey, or one type (`WEAP`) |
| `plugins` | What changed between two versions of a mod? Or: what does a patch change in the plugin it masters? | `a`, `b`. `match: "editorid"` pairs a plugin's own records by type and EditorID. `as` reads `a` under another plugin name |
| `patch_vs_reference` | Two patches for the same masters: what does each change, where do they agree? | `a` (the patch), `b` (the reference). `match` |

- **Without `detail`** an answer names the fields that differ. **With `detail`** every difference is a line with both values, and the record header's own numbers.
- A translated text counts in every language. A patch that holds a text in 1 language over a master that holds it in 9 changes that field, even with the same English: players in the other languages see the English text. No text against an empty text is a change too. Only a text every language shows the same, held another way, is "the same value, written another way": it is listed with the fields, never counted as a change.
- Lists of links are compared as sets; lists of objects by their key field, or by position.

```text
> sfs_compare {"kind": "versions", "record": "2377CF:Starfield.esm"}
dn_CommonGun (2377CF:Starfield.esm), INNR InstanceNamingRules: 2 loaded plugins hold it.
Winner: SFBGS00D.esm.
- Starfield.esm (load order 0, slot 00; the record's own plugin): the first version.
- SFBGS00D.esm (load order 10, slot 03; override, WINNER): changes 1 field against Starfield.esm:
    Rules [matched by Index, by position]: Rules[0].Names[10000 #2].Text: "Tesla" (9 languages) → "Tesla" (Polish changes); Rules[0].Names[10000 #4].Text: "Shattering" (9 languages) → "Shattering" (Japanese changes); … 19 more
[source] Starfield.esm 1.36 GB, key d1c0e07a; SFBGS00D.esm 97.3 MB, key 18a5181b; load order of 2026-11-10 06:48 (224 plugins loaded, 370 in Data)
[cost] 101 ms; read 2 versions of one INNR

> sfs_compare {"kind": "records", "a": "219447:Starfield.esm", "b": "0082B0:darkstar.esm"}
a: csShip_Ecliptic (219447:Starfield.esm) in Starfield.esm
b: cs_DarkStar_Elite_Piloting (0082B0:darkstar.esm) in darkstar.esm
28 fields differ (43 differences; CSTY CombatStyle), a → b:
  Editor ID (EDID): csShip_Ecliptic → cs_DarkStar_Elite_Piloting
  Group Offensive Mult (CSGD): 0.19999999 → 0.59999996
  Ranged Accuracy Mult (CSRA): 0.5 → 0.8
…

> sfs_compare {"kind": "records", "a": "atom74.esm", "record": "ccAtom74Mk2", "b": "darkstarpatch_atom74.esm", "expect": "Keywords"}
a: ccAtom74Mk2 (0009B7:atom74.esm) in atom74.esm
b: ccAtom74Mk2 (0009B7:atom74.esm) in darkstarpatch_atom74.esm
3 fields differ (9 differences; WEAP Weapon), a → b:
  Name (FULL): "Atom-74 Mk.2" (9 languages) → "Atom-74M2" (one text for every language)
  Description (DESC): (no text) → ""
  Object Templates [matched by position]: [0].Name: "Fallback" (9 languages) → "Fallback" (one text for every language; German, F…; …
Expect: FAIL. Unexpected differences in: Name, Description, ObjectTemplates. Allowed: Keywords.

> sfs_compare {"kind": "plugin_vs_masters", "a": "darkstarpatch_atom74.esm", "detail": true}
darkstarpatch_atom74.esm against its masters (Starfield.esm, sfbgs00d.esm, darkstar.esm, atom74.esm, sfbgs003.esm):
1 override: 1 changes something, 0 change nothing (ITM), 0 override nothing. 73 new records of its own (not compared).
Summary by field (how many overrides change it):
| Type | Field | Overrides |
| :--- | :--- | ---: |
| WEAP | Description (DESC) | 1 |
| WEAP | Name (FULL) | 1 |
| WEAP | Object Templates | 1 |
Overrides that change something, 1 (base → darkstarpatch_atom74.esm):
  ccAtom74Mk2 (0009B7:atom74.esm) WEAP, against atom74.esm:
      Name (FULL): "Atom-74 Mk.2" (9 languages) → "Atom-74M2" (one text for every language)   [a text for each language, then one text for every language; …]
      Description (DESC): (no text) → ""   [no text, then an empty text]
      Object Templates [matched by position]: 7 differences
        [0].Name: "Fallback" (9 languages) → "Fallback" (one text for every language; German, French and 5 more change)   [the same English text; …]
…

> sfs_compare {"kind": "plugins", "a": "atom74.esm", "b": "darkstarpatch_atom74.esm"}
a: atom74.esm (317 records)
b: darkstarpatch_atom74.esm (74 records)
darkstarpatch_atom74.esm masters atom74.esm, so each record of the patch is held against the record with the same FormKey in atom74.esm. 316 records of atom74.esm that the patch doesn't hold are left out (not removals), and so are 73 records of the patch that aren't atom74.esm's.
Added: 0. Removed: 0. Changed: 1. Unchanged: 0.
CHANGED  ccAtom74Mk2 (0009B7:atom74.esm) WEAP: Name
```
Two versions of one mod: give the older file by its path. This compares a copy of atom74.esm outside Data with the one in Data:
```text
> sfs_compare {"kind": "plugins", "a": "C:/Modding/copies/atom74.esm", "b": "atom74.esm"}
a: atom74.esm (C:\Modding\copies) (317 records)
b: atom74.esm (317 records)
Two versions of one plugin, records paired by FormKey.
Added: 0. Removed: 0. Changed: 0. Unchanged: 317.
…
[source] C:/Modding/copies/atom74.esm 82 KB, key 99071b6c; atom74.esm 82 KB, key 99071b6c
```
A patch against a reference patch. Here `mypatch_atom74.esm` is a copy of darkstarpatch_atom74.esm made for this example, so every change is the same:
```text
> sfs_compare {"kind": "patch_vs_reference", "a": "C:/Modding/copies/mypatch_atom74.esm", "b": "darkstarpatch_atom74.esm"}
a: mypatch_atom74.esm (C:\Modding\copies) (masters Starfield.esm, sfbgs00d.esm, darkstar.esm, atom74.esm, sfbgs003.esm)
b: darkstarpatch_atom74.esm (masters Starfield.esm, sfbgs00d.esm, darkstar.esm, atom74.esm, sfbgs003.esm)
Summary: 1 record overridden by at least one of the two: 1 by both (1 the same, 0 differ), 0 only in a, 0 only in b. Field changes: 1 the same change, 0 only in a, 0 only in b, 0 different.
New records of their own (not overrides): 73 in a, 73 in b (by type, a/b: COBJ 26/26, FLST 7/7, KYWD 2/2, MISC 12/12, OMOD 20/20, WEAP 6/6). They have different FormKeys in the two patches, so they aren't compared; match="editorid" pairs them by record type and EditorID.
SAME     (1 in all; detail shows their fields) ccAtom74Mk2 (0009B7:atom74.esm) WEAP
```
The records are marked `DIFFER`, `ONLY A`, `ONLY B` or `SAME`. A changed field shows the base value and each patch's value.

## sfs_loadorder: the load order and its problems
Arguments: `view`, `plugin`, `all`.

| `view` | Gives |
|---|---|
| `order` (default) | Every loaded plugin: position, runtime slot, size (Full, Medium, Small), file size, records, new records, overrides, problems. `all: true` adds the plugins in Data that aren't loaded |
| `plugin` | One plugin: size, loaded or not, slot, room left for new records, its masters with their sizes, its records per type (records, new, overrides, bytes) |
| `problems` | Every problem the header index found, plugin by plugin, with examples |
| `changes` | What changed in Data since last time: plugins added, removed, back and updated, and the patches at risk. With `plugin`: its history. `all: true`: every change, not only the last 30 days |

- **Slot** is the runtime prefix of a plugin's own records: `0A`, `FD:03`, `FE:09A` (FormIDs `FE09Axxx` in the console).
- **The problems:** a missing master; a master that isn't loaded or loads late; overrides of a different type than their base (a patch built for an older version or size of a master); overrides of a record the master doesn't have; a FormKey held twice.
- **The first `changes` call begins the history** (a small log under `kept\history` in the SFSearch home) and has nothing to compare with.

```text
> sfs_loadorder {"view": "plugin", "plugin": "atom74.esm"}
atom74.esm: Small, 82 KB (84,868 bytes), loaded at position 170, slot FE:043.
317 records in 12 groups: 317 new, 0 overrides.
Room for new records: its highest own ID is 000B0C. A Small plugin's IDs go up to 000FFF, so 1,267 are left above it.

Masters (1), in the header's order:
| # | Master | Size | Loaded | Slot | File | Overridden here |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| 0 | Starfield.esm | Full | yes | 00 | 1.36 GB | 0 |

Records by type (12 types):
| Type | Records | New | Overrides | Bytes |
| :--- | :--- | :--- | :--- | :--- |
| OMOD ObjectModification* | 140 | 140 | 0 | 38,662 |
| COBJ ConstructibleObject | 110 | 110 | 0 | 24,955 |
…

> sfs_loadorder {"view": "plugin", "plugin": "darkstarpatch_crimsonx.esm"}
darkstarpatch_crimsonx.esm: Small, 51 KB (53,235 bytes), in the load order at position 290 but not enabled.
122 records in 6 groups: 0 new, 122 overrides.
…
[notes] darkstarpatch_crimsonx.esm: 105 override(s) have a different type than their base: it was built for an older version of a master, so its overrides may sit on the wrong records. Example: its ARMO DarkStar_Mk2_Apparel_crx_CrimsonX_a01 (000806:CrimsonX_Z.esm) overrides the ARMA AA_CrimX_Body1 (000806:CrimsonX_Z.esm). 13 override(s) of a record their master doesn't have.

> sfs_loadorder {"view": "changes"}
Changes in Data since 2026-10-13 00:49 (history began 2026-11-12 00:45; 370 plugins in Data, 370 plugin copies remembered; compared with the look of 2026-11-12 00:45 by the service's idle check):
No plugin was added, removed or changed in that time.
Patches at risk (5): plugins whose master changed size after they were built, or whose overrides have a different type than their base.
| Plugin | Why |
| :--- | :--- |
| darkstarpatch_bountyhunteroutfitz.esm | 92 overrides of a different type than their base, and 55 of a record their master doesn't have: built for another version or size of a master |
…
[notes] A plugin's FormIDs count its masters by size. When a master changes between Full, Medium and Small after a patch was built, the patch's records land on other records. Don't trust or copy such a patch until it is rebuilt.
```
That note about darkstarpatch_crimsonx.esm leads the notes of every answer that reads the plugin, whichever tool is asked.

## sfs_check: is this plugin built right?
Arguments: `plugin`, `what`, `world`, `beside`, `detail`. Several parts at once: `"what": "records,roundtrip"`.

| `what` | Does |
|---|---|
| `records` (default) | 19 checks, each PASS, WARN or ERROR, then the records each found (below) |
| `itm` | Only the overrides that change nothing, every one |
| `assets` | Every file path on the plugin's records, held against the loose files and the archives in Data: found, missing, or only in an archive the game doesn't load |
| `materials` | The plugin's meshes, the materials they use, and those materials' textures: what is missing |
| `roundtrip` | Reads the plugin and writes it through Mutagen into the scratch folder, then compares the two files byte for byte |
| `all` | `records`, `assets`, `materials` and `roundtrip`, one after another |

- **The 19 checks:** masters present; masters load before the plugin; the header has one size; FormIDs fit the plugin's size; FormIDs name the plugin or one of its masters; own FormIDs are stored one way (a Small or Medium plugin names its own records by its full index, 03000866 as the Creation Kit writes it, or by its size's form, FE001866 or FD010866 as xEdit and Mutagen write it; the game reads both alike, and a file that mixes them is a WARN: darkstarpatch_bublumi.esm); own FormIDs use a form the plugin's size has (FE only in a Small plugin, FD only in a Medium one); links point at records that exist; links point at the right kind of record; links stay inside the plugin and its masters; overrides keep their base record's type; overrides have a record to override; no FormKey is used twice; no deleted records; EditorIDs are unique in the plugin; new EditorIDs don't repeat a master's; no identical-to-master overrides (ITMs); every record can be read; stored text reads one way (text stored in the plugin outside plain ASCII, which Mutagen reads as code page 1252 and a UTF-8 reader reads otherwise: economy.esm's Samson’s Art).
- **World data** (cells, placed references, navmeshes, terrain) is left out of the checks that read records unless `world: true`.
- **`beside`**: for a plugin file outside Data, read the archives that sit beside it, not the ones in Data.
- **`detail`** lists every finding, not the first of each.
- **A big plugin is heavy.** Starfield.esm answers `Not run. The checks of Starfield.esm read 434,970 of its 3,829,246 records: about 30 s and 1.7 GB of memory.` and its round trip `about 71 s and 13.8 GB of memory`.
- On the command line the exit code is 0 when nothing is wrong, 1 when a check found errors or the round trip differs, 2 for an error line, 7 for a heavy check that wasn't allowed.

```text
> sfs_check {"plugin": "darkstarpatch_crimsonx.esm"}
Check of darkstarpatch_crimsonx.esm (Small; masters: Starfield.esm, sfbgs00d.esm, crimsonx_z.esm, darkstar.esm, SFBGS006.esm, sfbgs003.esm):
122 records; 122 read and 1,406 links checked; 4 of 122 overrides compared with their master.
| Result | Check | Found | What it means |
| :--- | :--- | ---: | :--- |
| PASS | Masters present | 0 | every master's file is there; without one, FormIDs and links can't be told apart |
…
| ERROR | Links point at records that exist | 384 | the target is in the plugin or one of its masters |
| ERROR | Links point at the right kind of record | 145 | the schema says which record types a field may name (a keyword list holds keywords) |
…
| ERROR | Overrides keep their base record's type | 105 | a different type means the plugin was built for an older version of a master |
| WARN | Overrides have a record to override | 13 | the FormKey belongs to a master that has no such record: the game adds it as a new record under the master's name |
…
darkstarpatch_crimsonx.esm: 3 checks with errors, 1 with warnings.
ERROR: Links point at records that exist (384)
  DarkStar_Mk2_Apparel_crx_CrimsonX_a01 (000806:CrimsonX_Z.esm) ARMO: Keywords[3] → [no such record] (0001A7:darkstarpatch_crimsonx.esm): no such record in darkstarpatch_crimsonx.esm
…
[notes] darkstarpatch_crimsonx.esm: 105 override(s) have a different type than their base: it was built for an older version of a master, so its overrides may sit on the wrong records. Example: its ARMO DarkStar_Mk2_Apparel_crx_CrimsonX_a01 (000806:CrimsonX_Z.esm) overrides the ARMA AA_CrimX_Body1 (000806:CrimsonX_Z.esm). 13 override(s) of a record their master doesn't have.
[notes] 38 links to the engine's built-in forms (Starfield.esm below 0x800, such as 000014, the player) aren't in any file and aren't errors.

> sfs_check {"plugin": "darkstar.esm", "what": "itm"}
Overrides of darkstar.esm that change nothing (ITMs): 20 of the 699 overrides compared with their master (733 overrides in all; world data left out: add world to include it).
  An override is identical only when every value is: the EditorID, the record flags, and each text in every language. An override that renames a record or gives it an EditorID, drops or adds a language of a text, or holds an empty text where the master has none (or the reverse) changes something and isn't listed. Record headers' own numbers (form version, version control) don't count. A row says when the same value is written another way.
  UC01_SimulationPlayerShip_ClassB_AutopilotAI (015B81:Starfield.esm) NPC_: identical to Starfield.esm
…

> sfs_check {"plugin": "atom74.esm", "what": "assets"}
Assets of atom74.esm: 104 file paths on 95 of 317 records read (317 in the plugin).
…
| Found | 104: loose 0, the plugin's archives 92, its masters' archives 12, other loaded archives 0 |
| MISSING | 0: the plugin's own 0 (0 in fields typed as paths, 0 by value), inherited from a master's version of the record 0 |
…
Nothing is missing: every one of the 104 paths that can be judged is a loose file or in a loaded archive. Detail lists every path.

> sfs_check {"plugin": "atom74.esm", "what": "materials"}
Materials of atom74.esm: 2 archives, 43 meshes, 53 materials.
52 material files read, 1 compiled into a material database (1 the game's, 0 a mod's), 0 missing; 246 textures named (5 by compiled materials), 0 missing in 0 materials; 0 unreadable.
Nothing is missing: the 52 material files name 241 textures, each a loose file or in a loaded archive, and the other material is in a loaded compiled material database, naming 5 textures found the same way. Detail lists every material.
```
A material that is no `.mat` file is looked up in the compiled material databases (`materialsbeta.cdb`: the game's, the plugin's, other mods'). One in none of them is listed as `MISSING: no file, and in no compiled material database` (aa50_longwatch.esm: `materials\weapons\scopereticles\laser_ir_glow.mat`). A compiled material's textures are read from its database and checked like a `.mat` file's; a texture one of the game's own materials names and the game doesn't ship is noted, not counted as missing. The materials the plugin's records name (a biome's water, a landscape texture) are listed too, with the record. A database knows a material only by a hash of its path, so it can say whether a path is there but not list its materials.
```text

> sfs_check {"plugin": "atom74.esm", "what": "roundtrip"}
Round trip of atom74.esm: IDENTICAL. Read and written through Mutagen 0.55.0-fixes.11, the copy equals the file byte for byte: 84,868 bytes, 317 records.
```
On a difference the round trip names the record the first different byte is in.

## sfs_saved: the stored queries
Arguments: `action` (`list`, `show`, `run`, `check`, `save`), `name`, `args`, `sql`, `rows`. `references/sql.md` lists the queries and says how to write one.
```text
> sfs_saved {"action": "run", "name": "weapon-ammo", "args": {"plugin": "kgcdoom.esm"}}
shipped.weapon-ammo (plugins kgcdoom.esm; plugin=kgcdoom.esm): The weapons of a plugin with what they fire: ammunition, magazine size, the projectile they override, and their damage.
| FormKey | EditorID | Name | Ammo | AmmoKey | Capacity | AmmoList | Projectile | Projectiles | Damage | IsOverride | Plugin |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 007750:kgcdoom.esm | kgcDoom_WEAP_CrucibleBlade | Crucible Blade |  |  | 0 |  |  | 1 | 37 | 0 | kgcdoom.esm |
| 007751:kgcdoom.esm | kgcDoom_WEAP_SuperShotgun | Super Shotgun | AmmoShotshell | 2B4AFB:starfield.esm | 1 | LLW_Ammo_Coachman | kgcDoom_Projectile_Shotgun | 10 | 80 | 0 | kgcdoom.esm |
```

## sfs_run: the reports, and add-ons
`args` is the verb and its words, one item each. `{"args": ["help"]}` lists the verbs; `{"args": ["<verb>", "--help"]}` shows a verb's options. Each report also takes `--detail`, `--max-chars`, `--cursor` and `--language`.

| Verb | Answers | Arguments |
|---|---|---|
| `leveled2 <list>` | What a leveled list can give: every entry with level, count, chance of nothing and conditions, and the lists under it, each from the version the game uses | `--in <plugin>`, `--depth N` (default 8) |
| `dialogue2 <quest or topic>` | What a quest's characters say: topics and lines with the speaker, the text, the conditions, the scene a line starts and the stage it sets | `--in <plugin>` |
| `sources2 <plugin>` | How the player gets each item a plugin adds: a recipe, a quest, a vendor, a container, a placed copy, an NPC, through leveled lists, form lists and outfits | `--types WEAP,ARMO`, `--item <record>`, `--with <plugins>`, `--only`, `--depth N`, `--scan` |
| `archive2 <plugin, .ba2 or all>` | What is inside BA2 archives: a summary, or `list [<pattern>]`, `find <text>`, `show <path>`, `extract <pattern> --to <folder>` (never into the game's folders) | `--beside` |
| `assets2 <plugin>` | The same report as `sfs_check` with `what: "assets"` | `--world`, `--beside` |
| `materials2 <plugin>` | The same report as `sfs_check` with `what: "materials"` | `--beside` |
| `scripts2 <plugin>` | A plugin's Papyrus scripts as source (with loose libraries and parents its scripts use); each attached by which records, or to nothing; missing scripts; the code that reaches records without a link (GetFormFromFile, GetForm, IsPluginInstalled) and the records it hands out (AddItem, PlaceAtMe … on what they return or on a script property, with each attaching record's value) | `--find <text>`, `--show <script>`, `--calls` (only that code, every call, no decompiler), `--no-records` |
| `changes2 [<plugin>]` | The same report as `sfs_loadorder` with `view: "changes"` | `--since <day>`, `--all` |

```text
> sfs_run {"args": ["sources2", "kgcdoom.esm", "--item", "kgcDoom_WEAP_SuperShotgun"]}
Sources of the items kgcdoom.esm adds (WEAP, ARMO), item "kgcDoom_WEAP_SuperShotgun". Searched: kgcdoom.esm. Lists followed to depth 6.
1 item: the player can get 1, unclear for 0, no path found for 0. By verdict (an item with several counts under each): Quest 1, World loot 1.
| Item | Name | Type | Player can get it | Verdict | Gate |
| :--- | :--- | :--- | :--- | :--- | :--- |
| kgcDoom_WEAP_SuperShotgun (007751:kgcdoom.esm) | Super Shotgun | WEAP | yes | Quest, World loot |  |
kgcDoom_WEAP_SuperShotgun (007751:kgcdoom.esm) WEAP "Super Shotgun": Quest, World loot; the player can get it: yes
  - Quest: kgcDoom_MQ01 (00759C:kgcdoom.esm) script property (VMAD.Scripts[0].Properties[30].Object)
  - Placed: placed in cell kgcDoomHoldingCell (00762C:kgcdoom.esm)
…
[notes] Not looked at: Papyrus script code. A script that finds an item with GetFormFromFile, or adds it with AddItem on a FormID it builds itself, leaves no link in a record. …
[notes] Vendors are found the way the game finds them: a faction's merchant container (Faction.MerchantContainer, VENC) is a placed chest, its base container's items are the faction's stock, and the NPCs in the faction sell them. …

> sfs_run {"args": ["leveled2", "kgcDoom_LL_Weapon_SuperShotgun_HighestQuality"]}
Leveled list kgcDoom_LL_Weapon_SuperShotgun_HighestQuality (007772:kgcdoom.esm) LVLI LeveledItem:
5 lists, 8 entries: 4 give an item or form, 0 not found.
…
kgcDoom_LL_Weapon_SuperShotgun_HighestQuality (007772:kgcdoom.esm) LVLI, read from kgcdoom.esm: chance of nothing 0%; flags CalculateForEachItemInCount
  - level 1, count 1, 0% nothing: kgcDoom_LL_Weapon_SuperShotgun_Quality_01 (00777C:kgcdoom.esm) LVLI
    kgcDoom_LL_Weapon_SuperShotgun_Quality_01 (00777C:kgcdoom.esm) LVLI, read from kgcdoom.esm: chance of nothing 0%; flags UseAll
      - level 1, count 1, 0% nothing: kgcDoom_WEAP_SuperShotgun (007751:kgcdoom.esm) WEAP
  - level 40, count 1, 0% nothing: kgcDoom_LL_Weapon_SuperShotgun_Quality_02 (007777:kgcdoom.esm) LVLI
…

> sfs_run {"args": ["dialogue2", "kgcDoom_MQ01"]}
Dialogue of kgcDoom_MQ01 (00759C:kgcdoom.esm) QUST, each topic and line from the version that wins in the load order, texts in English:
99 topics, 130 lines.
…
Combat / Death (1 topic):
 [no EditorID] (0075AA:kgcdoom.esm):
  - kgcDoom_VT_Mitch (00B99C:kgcdoom.esm): "*gasp* Th--... Thank you..."; if GetStage(kgcDoom_MQ01 (00759C:kgcdoom.esm)) < 700   [00766B:kgcdoom.esm]
…

> sfs_run {"args": ["archive2", "atom74.esm"]}
Archives of atom74.esm: 2 archives, 477 files, 226.6 MB.
| Archive | Files | Size | Kinds | Loose copies in Data |
| :--- | ---: | ---: | :--- | ---: |
| atom74 - main.ba2 | 235 | 15 MB | .mesh 80, .mat 52, .nif 43, .wem 18, .dlstrings 9, .ilstrings 9, … |  |
| atom74 - textures.ba2 | 242 | 211.6 MB | .dds 242 |  |

> sfs_run {"args": ["archive2", "all", "find", "atom74mk2_receiver"]}
Files matching "atom74mk2_receiver" in all archives in Data (457 archives, 1,894,720 files, 161.07 GB): 1.
| Archive | File | Size | Loose copy in Data |
| :--- | :--- | ---: | :--- |
| atom74 - main.ba2 | meshes\weapons\atom74\atom74mk2_receiver.nif | 14 KB |  |

> sfs_run {"args": ["scripts2", "atom74.esm"]}
Scripts of atom74.esm: 2 scripts, 95 lines, from atom74 - main.ba2 (2).
On records: 2 of the 2 scripts are carried by 2 records of the plugin; 0 by none (used by other scripts, or attached by another plugin). 2 records with scripts among 317 looked at.
| Script | Lines | From | On records |
| :--- | ---: | :--- | :--- |
| Fragments:Quests:QF_ccAtom74_LLInjection_010008DF | 73 | atom74 - main.ba2 | 1: ccAtom74_LLInjection (0008DF:atom74.esm) QUST |
| Fragments:Quests:QF_ccAtom74_LLInjection_Uniq_01000A1E | 22 | atom74 - main.ba2 | 1: ccAtom74_LLInjection_Uniques (000A1E:atom74.esm) QUST |

> sfs_run {"args": ["scripts2", "atom74.esm", "--find", "AddForm"]}
Lines of the scripts of atom74.esm that contain "AddForm": 26 in 2 scripts (2 scripts, 95 lines looked at).
| Script | Line | Text |
| :--- | ---: | :--- |
| Fragments:Quests:QF_ccAtom74_LLInjection_010008DF | 43 | PLL_Weapon_Ranged_OldEarth_NoM1919.AddForm(PLL_Weapon_ccAtom74 as Form, 22, 1) |
…
```
- **`scripts2` needs Champollion** (the Papyrus decompiler) the first time it meets a plugin's scripts: in `tools\champollion` of the SFSearch home, or named by `champollion` in `sfsearch.json`, or by the `SFSEARCH_CHAMPOLLION` variable. The sources are then kept under `kept\scripts` in the home, so a later call needs no decompiler.
- **`sources2` doesn't read script code.** An item a script adds through a FormID it builds itself leaves no link in a record; the note says so.
- **Add-on commands** (a `.dll` in `extensions\` of the SFSearch home) run by their name: `{"args": ["<Command>", …]}`, or `<AddOn>.<Command>` when two add-ons share a name.

## sfs_job and sfs_status
- **`sfs_job`** (`job`, `wait_seconds`, `cancel`): a call that takes over 45 s answers `Still working on it (job …)`. `sfs_job` with that number collects the answer, says it is still working, or cancels it. An answer is kept for an hour, and not across a service restart.
- **`sfs_status`**: the service's workers, queue and heavy lane; both indexes; how many plugins each worker holds open; the scratch folder's size; the kept files; memory. The lines to look at when something seems slow or missing:
```text
> sfs_status {}
…
Header index: complete. 370 plugins in Data (224 loaded), 12,217,439 records, 480.6 MB of segments; checked against the files in 17 ms. 9 plugin(s) have problems (loadorder2 problems lists them).
Link index: complete. 370 of 370 plugins have a link file: 17,050,052 links, 1,405,939 in-game names, 309.1 MB.
Scratch folder: C:\Modding\SFSearch\SFSearch\scratch, 1.22 GB in 757 file(s): index 480.6 MB in 372 file(s); links 309.1 MB in 370 file(s); query 457.3 MB in 15 file(s). The service empties it at every start; everything in it is made again when needed.
Kept files: C:\Modding\SFSearch\SFSearch\kept, 3.2 MB in 363 file(s) (decompiled scripts, the history log). They outlive a service start, and can be deleted.
…
```

## When a plugin can't be read
A plugin whose master isn't in Data can't have its FormIDs read, so every tool that would read its records stops with the same line. Nothing is mapped by guess. (The file here is a copy of a patch whose header was changed to name a master that doesn't exist.)
```text
> sfs_query {"plugins": "C:/Modding/copies/zz_missingmaster.esm", "sql": "SELECT count(*) AS N FROM Forms"}
Error: C:\Modding\copies\zz_missingmaster.esm lists a master that isn't in Data: atom7Z.esm. 74 of its 74 records have FormIDs that can't be read without it, and nothing is mapped by guess. Fix: put atom7Z.esm in the Data folder (or name the right master in the plugin's header), then ask again.
```
`sfs_loadorder` with `view: "plugin"` still describes such a plugin: it lists the master as missing and counts the records that can't be read.

## What happens behind a call
- **The service** starts with the first MCP call and serves many agents at once: several worker programs, one command each at a time. A heavy command (2 GB or more) runs alone, and only when Windows has the memory; if not, it fails at once with one line.
- **Its limits come from the PC:** how many commands run at once and how much one command may hold (2.7 GB on a 16 GB PC, 6 GB on a 64 GB PC); `sfs_status` shows them. If the PC runs short of memory while commands run, the biggest is stopped with `Error: the PC was short of memory (N GB free), so this command was stopped. Fix: ask again; …` (exit code 8): ask again, or ask for less at once.
- **At its start** the service empties its scratch folder and builds two indexes: the header index (every record's FormKey, type, EditorID and place in its file; about 2 s for 370 plugins) and the link index (what points at what, and in-game names; about 15 s). Tools answer before the link index is ready, and say what is missing.
- **Before every answer** the plugins it reads are checked against their files (about 20 ms). A plugin that changed on disk is read again, and a note says so.
- **Nothing about a record is kept.** The scratch folder holds working files only while the service runs. Two small things outlive a start, under `kept\`: decompiled scripts and the history log.
