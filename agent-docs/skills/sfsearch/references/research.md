# The SFSearch tools in depth

Each tool with its arguments and what it prints. Every answer below is real: it was run on a Data folder of 370 plugins (224 loaded) and cut with "…" where long. Calls are written as a tool name and its arguments. On the command line the same call is the verb with the arguments as options (`SKILL.md`, "The command line").

**What every tool shares:** `cursor`, `limit` or `rows`, `max_chars`, `language`, `format`, `allow_heavy` (`SKILL.md`, "How to read an answer").

**An argument the MCP tool doesn't list** still works through `sfs_run` with the verb: `{"args": ["refs2", "0076BE:kgcdoom.esm", "--in", "kgcdoom.esm", "--search", "bytes"]}`. Those are: `describe` `--all` and `--max-chars`; `query2` `--max-chars`; `saved` `--plugins`, `--scope`, `--language`, `--max-chars`, `--overwrite`; `refs2` `--search`; `compare` `--plugin`, `--max-chars`, `--language`, `--all`; `loadorder2` `--since`, `--limit`; `check2` `--max-chars`, `--language`, `--all` (`--all`: every row on one page, for scripts).

## sfs_find: what is this?
Arguments: `query`, `type`, `in`, `limit`; for a search of texts `text`, `from`, `world`, `plain`, `language`. It answers from the header index: no record is opened (a text search reads the texts).

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
| Super Shotgun | kgcDoom_ENCH_ConstSelfApplyDmgBonus (007710:kgcdoom.esm) | ENCH ObjectEffect | Super Shotgun | kgcdoom.esm |
| Super Shotgun | kgcDoom_WEAP_SuperShotgun (007751:kgcdoom.esm) | WEAP Weapon | Super Shotgun | kgcdoom.esm > darkstarpatch_athellsgate.esm (not loaded) |
| Super Shotgun | kgcDoom_MGEF_EnchApplyPerkDmg (0076F2:kgcdoom.esm) | MGEF MagicEffect | Super Shotgun Ench | kgcdoom.esm |
| Super Shotgun | kgcDoom_Book_ShotgunNote (007720:kgcdoom.esm) | BOOK Book | Notes on Super Shotgun | kgcdoom.esm |
[notes] No record has the EditorID "Super Shotgun": the row(s) are the 4 record(s) whose in-game name holds those words.

> sfs_find {"query": "00039E", "in": "Starfield.esm"}
No record has the ID 00039E in Starfield.esm (3,829,246 records looked at), and no EditorID is "00039E".
```
`Held by` lists the plugins that hold the record, the winner first. A plugin marked `(not loaded)` is in Data but the game doesn't load it.

**Searching texts** (`text`, used instead of `query`): a .NET regular expression held against every text the game shows (names, descriptions, book texts, dialogue lines, quest objectives and log entries, game settings, perk entry texts) of each plugin's own records. `(?i)` ignores letter case; `[^\x00-\x7F]` finds text outside plain ASCII. A row gives the record, the field, the text around the first match, and where the text is.
- `from`: `stored` (text stored in plugins without strings files, read as Mutagen reads it, code page 1252), `strings` (the strings files of localized plugins, in `language`), or `both` (the default).
- `world: true` adds placed references' own texts (map markers' names): 0.1 to 1.7 s more over the whole load order.
- `plain: true` adds the plain text fields that are never translated and are stored in every plugin: EditorIDs, a quest's summary and its log entries' notes, components' node names and file paths, script names, property names and string properties. Such a row says `plain text`. Not with `from: "strings"`.
```text
> sfs_find {"text": "LLInjection", "plain": true, "in": "atom74.esm"}
8 text(s) in 1 plugin(s) fit /LLInjection/ (the stored text of 0 plugin(s) with no strings files and the English strings files of 1 localized plugin(s), and the plain text fields of all 1): atom74.esm 8.
| Record | Type | Field | Text | In |
| :--- | :--- | :--- | :--- | :--- |
| ccAtom74_LLInjection (0008DF:atom74.esm) | QUST Quest | EditorID | ccAtom74_LLInjection | atom74.esm, stored, plain text |
| ccAtom74_LLInjection (0008DF:atom74.esm) | QUST Quest | VirtualMachineAdapter.Script.Name | Fragments:Quests:QF_ccAtom74_LLInjection_010008DF | atom74.esm, stored, plain text |
…
| ccAtom74_LLInjection_Uniques (000A1E:atom74.esm) | QUST Quest | VirtualMachineAdapter.Fragments[1].ScriptName | Fragments:Quests:QF_ccAtom74_LLInjection_Uniq_01000A1E | atom74.esm, stored, plain text |
```

## sfs_record: one record
Arguments: `record`, `plugin`, `view`, `fields`. The record is read live from the plugin file.
- `record` is a FormKey, an EditorID, or a FormID with `plugin`. A name that fits several records lists them and picks none. When one record of that EditorID is in a loaded plugin and the others are only in plugins that aren't loaded, the loaded one is read and a note names the others.
- `plugin` says whose version to read: a name in Data or a file path. Left out, it is the version the game uses. **The third line of every answer says which version was read, and why.**
- **A record of over 16 MB** (Starfield.esm's navmesh info map) is read in parts without `allow_heavy`: the card and json views give it a part at a time (each item of a big list is a part; the cursor goes on), verify checks it part by part, fields and evidence read only the fields named. `allow_heavy` reads it whole.
- **A plugin's name** (`darkstar.esm`), or a plugin file's path, **is its header** (TES4): its flags, HEDR (version, number of records, next object ID), masters, author, description and overridden forms. The card, fields, json, raw and evidence views take it; evidence names e.g. `Flags`, `Stats.NumRecords`, `MasterReferences[0].Master`. Every plugin's header at once is `sfs_query`'s `Plugins` view (`sql.md`).

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
KYWD Keyword in kgcdoom.esm (its own record), at 0x29D, 63 bytes, its FormID stored as FD0076BE (the Medium form)
Read from kgcdoom.esm: the only plugin that holds it.
  Form Key: 0076BE:kgcdoom.esm
  Version Control: 2110538
  Editor ID (EDID): kgcDoom_Tag_WeaponTypeDoom
  Form Version: 582
  Version2: 2
  Color (CNAM): #00FFFFFF
  Type (TNAM): None
  Form Link Target Type (FNAM): 0
[source] kgcdoom.esm 3.5 MB, key 9561e0c8, strings 2d590079; load order (224 loaded, plugins.txt) 9 KB, key b730711a
[cost] 32 ms; read 1 KYWD of kgcdoom.esm

> sfs_record {"record": "kgcDoom_WEAP_SuperShotgun", "view": "fields", "fields": "AttackDamage,HasScope,Keywords[7],Name"}
kgcDoom_WEAP_SuperShotgun (007751:kgcdoom.esm)
WEAP Weapon in kgcdoom.esm (its own record), at 0xD313, 2,403 bytes, its FormID stored as FD007751 (the Medium form)
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

A plugin's header, and evidence in a list Mutagen reads with hand-written code (the item's own subrecord):
```text
> sfs_record {"record": "darkstar.esm"}
darkstar.esm's plugin header (TES4)
TES4 StarfieldModHeader of darkstar.esm: the plugin header, at 0x0, 60,336 bytes
Read from darkstar.esm: the plugin you named.
  Record Flags (Flags): [Master]
  Form Version: 582
  Stats (HEDR)
    Number of Records (NumRecords, HEDR): 47867
    Next Object ID (NextFormID, HEDR): 33556480
  Author (CNAM): WykkydGaming
  Description (SNAM): "DarkStar 2.1.0\r\n\r\nTotal Gameplay Overhaul. Extends combat, crafting and shipbuilding to level 999+.\n\r\n\r\nKomplettüberarbeitung des Gameplays. Erweitert Kampf, Herstellung und Schiffsbau bis Level 999+.\n\r\n\r\nRefonte complète du gameplay. Étend le combat, l’artisanat et la construction de vaisseaux jusqu’au niveau 999+.\r\n\r\nRevisión completa del juego. Amplía el combate, la fabricación y la construcción de naves hasta el nivel 999+."
  Master References: 3 items
    [0]
      FileName (Master, MAST): Starfield.esm
…

> sfs_record {"record": "darkstar.esm", "view": "evidence", "fields": "Flags,Stats.NumRecords,MasterReferences[0].Master"}
…
Flags = [Master]
  file     TES4 record header, offset 8, 4 bytes: 01 00 00 00
           the header is 24 bytes: 54 45 53 34 B0 EB 00 00 01 00 00 00 00 00 00 00 00 00 00 00 46 02 00 00
  found    Flags is the record header's 4 bytes at offset 8 (after the signature and the data size), as Mutagen's generated reader reads them (StarfieldModHeaderBinaryCreateTranslation.FillBinaryStructs). The bytes read 0x1, Mutagen's value.
  Mutagen  StarfieldModHeader.Flags (Enum)
  xEdit    \Record Header\Record Flags
Stats.NumRecords = 47867
  file     TES4/HEDR, offset 4, 4 bytes: FB BA 00 00
           HEDR is 12 bytes: 8F C2 75 3F FB BA 00 00 00 08 00 02
  Mutagen  StarfieldModHeader.Stats.NumRecords (UInt32)
  xEdit    \HEDR - Header\Number of Records
MasterReferences[0].Master = Starfield.esm
  file     TES4/MAST, offset 0, 14 bytes (the 1st of 3 MAST subrecords): 53 74 61 72 66 69 65 6C 64 2E 65 73 6D 00
           MAST is 14 bytes: 53 74 61 72 66 69 65 6C 64 2E 65 73 6D 00
  item     MasterReferences[0] is subrecords MAST; Master is at offset 0 of MAST (14 bytes), as Mutagen's hand-read layout (Record TES4 in HandReadLayouts.xml) lays it out. Checked: the bytes read "Starfield.esm", Mutagen's value.
  Mutagen  StarfieldModHeader.MasterReferences[0].Master (String)
  xEdit    \Master Files\Master File\MAST - FileName

> sfs_record {"record": "MQ_TutorialQuest_Misc06", "view": "evidence", "fields": "Aliases[1].Name"}
…
Aliases[1].Name = ResearchWorkbench
  file     QUST/ALID, offset 0, 18 bytes (the 2nd of 3 ALID subrecords): 52 65 73 65 61 72 63 68 57 6F 72 6B 62 65 6E 63 68 00
           ALID is 18 bytes: 52 65 73 65 61 72 63 68 57 6F 72 6B 62 65 6E 63 68 00
  item     Aliases[1] is item 2 of 3, subrecords ALST ALID FNAM ALFG ALFA ALRT VTCK ALED, as Mutagen's hand-read layout (Record QUST in HandReadLayouts.xml) cuts them; Name is the whole ALID. Checked: the bytes read "ResearchWorkbench", Mutagen's value.
  Mutagen  Quest.Aliases[1].Name (String)
  xEdit    \Aliases\Reference Alias\ALID - Alias Name
```

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
# Weapon (WEAP): 13 rows matching "damage" (14 paths: the ones that repeat are put together)
Fields named so come first, then fields whose readable or xEdit name holds it, then fields that only sit inside one that does.

| Path | Sig | Type | xEdit name | Notes |
| AttackDamage | WDMG | Float, 4 B @0 | Base Damage |  |
…
| OutOfRangeDamageMult | WDMG | Float, 4 B @12 | Out of Range Damage Mult |  |
| CritDamageMult | WDMG | Float, 4 B @16 | Crit Damage Mult |  |
| DamageTypes | DAMA | List |  | may be absent |
| DamageTypes[*] | DAMA | Object WeaponDamageType |  |  |
| MeleeBashDamage | WMEL | Float, 4 B @1 | Bash Damage |  |
| PowerDamageMultiplier | QNAM | Float, 4 B @28 | Power Damage Multiplier |  |
…
[source] schema b88edabef26c25a4, made from Mutagen 0.55.0-fixes.17's record definitions (no plugin read)
[cost] 14 ms

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
- **xEdit name** is xEdit's own name for the field. The readable name the card and the views use is xEdit's, except where xEdit's is no real name ("Unknown", "Unknown #2", "Unused", or only a signature such as "CTDA"): there SFSearch keeps its own (Mutagen's name, spaced out: COBJ's `Flags` reads "Flags", xEdit `\RECF - Unknown`), and xEdit's stays in this column.

## sfs_refs: links
Arguments: `record`, `direction`, `in`, `types`, `world`, `plugin`.
- **`world`**: world data (cells, placed references, navmeshes, terrain). Left out: it is read from the world link index of every plugin that has one, and searched by its bytes in the others when they hold 256 MB of world data or less (a mod's record: tens of ms); more (any record of Starfield.esm: 2.7 GB, 5 to 15 s) is left out, and the answer's first lines say so. `true`: always; `false`: never; `"save_index"`: search it now, then save the world link index of the plugins searched by their bytes.
- **`direction: "in"`** (default): the records that point at it. One row per record and field. The field is the link's path in the record's JSON, with list positions written `[*]`.
- **`direction: "out"`**: what the record points at, field by field. `plugin` says whose version to read.
- **`direction: "both"`**: both, one after the other.
- `in`: the plugins to search. Left out: the record's own plugin and every plugin in Data that lists it as a master.
- `types`: only records of these types.
- **World data left out offers the world link index.** The line under the count says how to search it now (`world: true`, its time) and, for plugins without a world link index, `world: "save_index"`: search it now and then save the index (its size and build time), after which refs into world data answers in about 0.2 s. `sources2` searches the placed references of a plugin with more than 300,000 of them by their bytes (seconds), says so, and offers `allow_heavy: "save_index"` to save its world link index for next time; it leaves them out, with `allow_heavy` to search them, only when that search would take over 10 s. Saving is the user's choice: tell them the size.
- **The `Searched:` line says what was and wasn't searched.** World records (cells' content, placed references, navmeshes, terrain) aren't in the link index. They are read from the world link index of a plugin that has one (made only when the user asks: `sfs_run ["indexes", "build", "world"]`); for the others `world: true` searches their bytes, and the line says what that would cost.
- `Wins` says whether that plugin's version of the pointing record is the one the game uses.
- **A form list's AddToList** (ANAM): the game adds that form list's entries to the list it names. A row at `AddToList` says so; a row at `Items[*]` of a form list that names another in AddToList says where its entries go. For a form list asked about, a note names the form lists that add their entries to it (and how many entries that adds) and the list its own entries go to. `sources2` follows the same links.

```text
> sfs_refs {"record": "0076BE:kgcdoom.esm", "in": "kgcdoom.esm"}
4 records point at kgcDoom_Tag_WeaponTypeDoom (0076BE:kgcdoom.esm): 4 links in 1 plugin.
Searched: the link index of 1 plugin (every record outside cells' content, navmeshes and terrain: 731 records); world data (cells, placed references, navmeshes, terrain) by its bytes, searched by default because it is small (at most 256 MB): 14,597 records of 1 plugin.
| Record | Type | Field | Read from | Wins |
| :--- | :--- | :--- | :--- | :--- |
| kgcDoom_MGEF_EnchShotgunCloak (0076FA:kgcdoom.esm) | MGEF MagicEffect | VirtualMachineAdapter.Scripts[*].Properties[*].Object: script kgcDoom:EnchSuperShotgunScript, property kgcDoom_Tag_WeaponTypeSuperShotgun. | kgcdoom.esm | yes |
| kgcDoom_Perk_BonusDamageToPredators (00B999:kgcdoom.esm) | PERK Perk | Ranks[*].Effects[*].Conditions[*].Conditions[*].Data.FirstParameter | kgcdoom.esm | yes |
| kgcDoom_WEAP_CrucibleBlade (007750:kgcdoom.esm) | WEAP Weapon | Keywords[*] | kgcdoom.esm | yes |
| kgcDoom_WEAP_SuperShotgun (007751:kgcdoom.esm) | WEAP Weapon | Keywords[*] | kgcdoom.esm | yes |
[source] kgcdoom.esm 3.5 MB, key 9561e0c8, strings 2d590079; load order (224 loaded, plugins.txt) 9 KB, key b730711a
[cost] 89 ms; read 1 record through Mutagen to name the script properties (2 ms), the compiled code of 1 script for AddForm calls (0 read from their files, 15 ms), the link index of 1 plugin, the bytes of 14,597 world records of 1 plugin (4 ms)

> sfs_refs {"record": "0076BE:kgcdoom.esm", "in": "kgcdoom.esm", "world": true}
4 records point at kgcDoom_Tag_WeaponTypeDoom (0076BE:kgcdoom.esm): 4 links in 1 plugin.
Searched: the link index of 1 plugin (every record outside cells' content, navmeshes and terrain: 731 records); world data (cells, placed references, navmeshes, terrain) by its bytes: 14,597 records of 1 plugin.
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

A record of Starfield.esm, with no world link index: its world data is left out, and the answer gives the three answers:
```text
> sfs_refs {"record": "00003B:Starfield.esm", "in": "Starfield.esm", "limit": 3}
21 records point at XMarker (00003B:Starfield.esm): 22 links in 1 plugin, world data left out.
World data wasn't searched (left out by default: more than 256 MB of it): 3,394,276 records (cells, placed references, navmeshes and terrain), 1.18 GB in 1 plugin. Add world: true (command line: --world) to include it: about 1 s more, longer when many records have to be confirmed.
Or world: "save_index" (command line: --world=save-index) to search it now and then save the world link index of Starfield.esm (about 91 MB on disk, made in about 7 s in the background after the answer): refs into world data then answers in about 0.2 s, from the index. Or leave it out, as this answer did.
…
```
After `sfs_run ["indexes", "build", "world"]` (14 s, 275 MB, once), the same record from every plugin, world data included:
```text
> sfs_refs {"record": "00003B:Starfield.esm", "limit": 3}
5,489 records point at XMarker (00003B:Starfield.esm): 5,493 links in 46 plugins.
Searched: the link index of 367 plugins (every record outside cells' content, navmeshes and terrain: 3,465,065 records); world data (cells, placed references, navmeshes, terrain) from the world link index of 119 plugins (8,752,362 records).
…
[cost] 68 ms; read the link index of 367 plugins, the world link index of 119 plugins
```

## sfs_compare: five kinds of comparison
Arguments: `kind`, `a`, `b`, `record`, `record_b`, `expect`, `match`, `as`, `filter`, `like_xedit`, `detail`. A plugin is a name in Data or a file path.

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
[source] Starfield.esm 1.36 GB, key d1c0e07a, strings 4b75842f; SFBGS00D.esm 97.3 MB, key 18a5181b, strings 9d905ea9; load order of 2026-11-10 06:48 (224 plugins loaded, 370 in Data)
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
CHANGED  ccAtom74Mk2 (0009B7:atom74.esm) WEAP: Name, Description, ObjectTemplates
```
Two versions of one mod: give the older file by its path. This compares a copy of atom74.esm outside Data with the one in Data:
```text
> sfs_compare {"kind": "plugins", "a": "C:/Modding/copies/atom74.esm", "b": "atom74.esm"}
a: atom74.esm (C:\Modding\copies) (317 records)
b: atom74.esm (317 records)
Two versions of one plugin, records paired by FormKey.
Added: 0. Removed: 0. Changed: 0. Unchanged: 317.
…
[source] C:/Modding/copies/atom74.esm 82 KB, key 99071b6c; atom74.esm 82 KB, key 99071b6c, strings 318e2853
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
| `order` (default) | Every loaded plugin: position, runtime slot, size (Full, Medium, Light: the Small size xEdit and the Creation Kit name), file size, records, new records, overrides, problems. `all: true` adds the plugins in Data that aren't loaded |
| `plugin` | One plugin: size, loaded or not, slot, room left for new records, its masters with their sizes, its records per type (records, new, overrides, bytes) |
| `problems` | Every problem the header index found, plugin by plugin, with examples |
| `changes` | What changed in Data since last time: plugins added, removed, back and updated, and the patches at risk. With `plugin`: its history. `all: true`: every change, not only the last 30 days |

- **Slot** is the runtime prefix of a plugin's own records: `0A`, `FD:03`, `FE:09A` (FormIDs `FE09Axxx` in the console).
- **The problems:** a missing master; a master that isn't loaded or loads late; overrides of a different type than their base (a patch built for an older version or size of a master); overrides of a record the master doesn't have; a FormKey held twice.
- **The first `changes` call begins the history** (a small log under `kept\history` in the SFSearch home) and has nothing to compare with.

```text
> sfs_loadorder {"view": "plugin", "plugin": "atom74.esm"}
atom74.esm: Light (Small), 82 KB (84,868 bytes), loaded at position 170, slot FE:043.
317 records in 12 groups: 317 new, 0 overrides.
Room for new records: its highest own ID is 000B0C. A Light plugin's IDs go up to 000FFF, so 1,267 are left above it.

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
darkstarpatch_crimsonx.esm: Light (Small), 51 KB (53,235 bytes), in the load order at position 290 but not enabled.
122 records in 6 groups: 0 new, 122 overrides.
Room for new records: it has no record of its own. A Light plugin's IDs go up to 000FFF.
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
[notes] A plugin's FormIDs count its masters by size. When a master changes between Full, Medium and Light (Small) after a patch was built, the patch's records land on other records. Don't trust or copy such a patch until it is rebuilt.
```
That note about darkstarpatch_crimsonx.esm leads the notes of every answer that reads the plugin, whichever tool is asked.

## sfs_check: is this plugin built right?
Arguments: `plugin`, `what`, `world`, `beside`, `like_xedit`, `detail`. Several parts at once: `"what": "records,roundtrip"`.

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
- **`like_xedit: true`** gives the ITM verdict the way SF1Edit does: it also counts as ITMs the overrides that differ only in unknown or unused bytes, in the order of lists the game sorts (keywords, outfit items, factions, script properties), in a worldspace's offset tables, by an added XLRL on a temporary reference, or in plain text against the same English text; each such row names the rule. The strict verdict is the default. `sfs_compare` with `kind: "plugin_vs_masters"` takes it too.
- **A big plugin is heavy.** Starfield.esm answers `Not run. The checks of Starfield.esm read 434,970 of its 3,829,246 records: about 30 s and 1.7 GB of memory.` and its round trip `about 71 s and 13.8 GB of memory`.
- On the command line the exit code is 0 when nothing is wrong, 1 when a check found errors or the round trip differs, 2 for an error line, 7 for a heavy check that wasn't allowed.

```text
> sfs_check {"plugin": "darkstarpatch_crimsonx.esm"}
Check of darkstarpatch_crimsonx.esm (Small; masters: Starfield.esm, sfbgs00d.esm, crimsonx_z.esm, darkstar.esm, SFBGS006.esm, sfbgs003.esm):
122 records; 122 read and 1,406 links checked; 4 of 122 overrides compared with their master.
Own FormIDs as the file stores them: no record of its own; 384 links to its own records (384 03xxxxxx). Its header (Small) gives FE001xxx, the Small (light) form; 03xxxxxx is the full index.
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
Overrides of darkstar.esm that change nothing (ITMs): 20 of the 699 overrides compared with their master (733 overrides in all).
World data was left out of the ITM check: 15,592 records (REFR 15,178, CELL 304, NAVM 96, WRLD 10, ACHR 4), 7.9 MB. Add world: true (command line: --world) to include it: about 3 s more. No saved index would make that faster: the checks read each record.
  An override is identical only when every value is: the EditorID, the record flags, and each text in every language. An override that renames a record or gives it an EditorID, drops or adds a language of a text, or holds an empty text where the master has none (or the reverse) changes something and isn't listed. Record headers' own numbers (form version, version control) don't count. A row says when the same value is written another way. SFSearch compares a record's data (its subrecords and flags), not its header; a row names the header numbers that differ (this plugin's first, then the master's). xEdit may not show such a record as an ITM.
  UC01_SimulationPlayerShip_ClassB_AutopilotAI (015B81:Starfield.esm) NPC_: identical to Starfield.esm in data; header differs: form version 582 vs 581; version control 0 vs 0x002034C1; second version number 0 vs 4
…

> sfs_check {"plugin": "atom74.esm", "what": "assets"}
Assets of atom74.esm: 104 file paths on 95 of 317 records read (317 in the plugin).
…
| Found | 104: loose 0, the plugin's archives 92, its masters' archives 12, other loaded archives 0 |
| MISSING | 0: the plugin's own 0 (0 in fields typed as paths, 0 by value), inherited from a master's version of the record 0 |
…
Nothing is missing: every one of the 104 paths that can be judged is a loose file or in a loaded archive. Detail lists every path.

> sfs_check {"plugin": "atom74.esm", "what": "materials"}
Materials of atom74.esm: 2 archives, 43 meshes, 1 mesh its records name elsewhere (1 in its masters' archives), 53 materials.
52 material files read, 1 compiled into a material database (1 the game's, 0 a mod's), 0 missing; 246 textures named (5 by compiled materials), 0 missing in 0 materials; 0 unreadable.
Nothing is missing: the 52 material files name 241 textures, each a loose file or in a loaded archive, and the other material is in a loaded compiled material database, naming 5 textures found the same way. Detail lists every material.
```
A material that is no `.mat` file is looked up in the compiled material databases (`materialsbeta.cdb`: the game's, the plugin's, other mods'). One in none of them is listed as `MISSING: no file, and in no compiled material database` (aa50_longwatch.esm: `materials\weapons\scopereticles\laser_ir_glow.mat`). A compiled material's textures are read from its database and checked like a `.mat` file's; a texture one of the game's own materials names and the game doesn't ship is noted, not counted as missing. The materials the plugin's records name (a biome's water, a landscape texture) are listed too, with the record. A database knows a material only by a hash of its path, so it can say whether a path is there but not list its materials.
```text

> sfs_check {"plugin": "atom74.esm", "what": "roundtrip"}
Round trip of atom74.esm: IDENTICAL. Read and written through Mutagen 0.55.0-fixes.17, the copy equals the file byte for byte: 84,868 bytes, 317 records.
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
| `sources2 <plugin>` | How the player gets each item a plugin adds: a recipe, a quest, a vendor, a container, a placed copy, an NPC, through leveled lists, form lists and outfits | `--types WEAP,ARMO`, `--item <record>`, `--with <plugins>`, `--only`, `--depth N`, `--scan`, `--allow-heavy`, `--allow-heavy=save-index` |
| `archive2 <plugin, .ba2 or all>` | What is inside BA2 archives: a summary, or `list [<pattern>]`, `find <text>`, `show <path>`, `extract <pattern> --to <folder>` (never into the game's folders); `grep <text>`: which files hold a text (a material or texture path, a script string): the contents of the archived files and of the loose files the game reads in their place, each hit with its file, offset and the text around it (a UTF-16 text file, such as the interface's `translate_*.txt`, is read as UTF-16). `--count`: no hits listed, the hits and files per kind of file (per archive with `--detail`): "how many meshes name this?". `encoding`: how each file's text is written (ASCII, UTF-8, Windows-1252, mixed, UTF-16, binary), a string table string by string with the strings not in UTF-8. It says the files, bytes and time first; over 10 s it is "Not run" until `allow_heavy` | `--beside`, `--all` (every row on one page); grep: `--regex` (`^`/`$` a line, `\A` the file's start), `--ext mat,nif` (textures, sounds, video only when named), `--folder materials`, `--count`, `--allow-heavy`; encoding: `--ext strings,txt`, `--folder`, `--count`, `--detail` (every file and string) |
| `assets2 <plugin>` | The same report as `sfs_check` with `what: "assets"` | `--world`, `--beside` |
| `materials2 <plugin>` | The same report as `sfs_check` with `what: "materials"` | `--beside` |
| `scripts2 <plugin>` | A plugin's Papyrus scripts as source (with loose libraries and parents its scripts use); each attached by which records, or to nothing; missing scripts; the code that reaches records without a link (GetFormFromFile, GetForm, IsPluginInstalled) and the records it hands out (AddItem, PlaceAtMe … on what they return or on a script property, with each attaching record's value) | `--find <text>`, `--show <script>`, `--calls` (only that code, every call, no decompiler), `--no-records` |
| `changes2 [<plugin>]` | The same report as `sfs_loadorder` with `view: "changes"` | `--since <day>`, `--all` |
| `profile <Type.Path ...>` | What a field holds across many plugins: per plugin the records and values; distinct values, the most common, the share of zeros; for 4-byte values how many read as a float, as a FormID of a record in Data (which types) or as a strings ID; samples with their record. A link field: what it points at (distinct records, their types, null links, links to records no plugin holds, the most used targets) | `--in` (names, `*`, `@official`, `@loaded`, `@winners` the default, `@all`), `--samples N`, `--detail`, `--allow-heavy` |
| `profile --unknown` | The same for every field the schema marks unknown, of `--type` or of every type | `--type`, `--in` |
| `profile --groups` | The GRUP headers: each group's label, type, stamp, version, the Unknown at bytes 20-23, size, what is right inside it | `--in` (default `@loaded`), `--type CELL`, `--group 0x2A3F0`, `--list` |
| `profile --conditions` | The condition census: every condition (CTDA) of every record that can hold one, counted by function (index and name): conditions, records, plugins, record types, samples. The rows themselves are `sfs_query`'s `Conditions` view | `--in`, `--type`, `--function 961` (or a name): that function alone |
| `profile --subrecords STRD[,XTV2]` | Which record types hold these subrecords, from the files' bytes: the records that hold each, how many times, the plugins, a sample, beside the types whose fields the schema puts there | `--in` (default `@official`), `--type` |
| `profile --subrecords A,B\|* --order` | In what orders a type's subrecords come, from the files' bytes on all cores: each record's subrecords in file order (the named ones, others between as `…`, a unit repeated in a row as `(A B)+`), records grouped by order, with plugins, a sample and how often each unit repeats | `--type PACK`, `--in` |
| `profile --subrecords *` (no `--order`) | What a type's records hold, from the files' bytes on all cores: records, bytes in the file and as read; every subrecord (records that hold it, times, bytes, largest); every link field whose place the bytes give (links, distinct targets, records), and the other link fields with the `profile` call that counts them | `--type CELL`, `--in` |
| `indexes [show]` | What SFSearch keeps on disk (`indexes\` in its home): each index's size, the plugins it covers, its age, what it speeds up, the parts kept for plugins no longer in Data | `build [header\|links\|world\|all] [--in <plugins>]`, `drop header\|links\|world\|all [--in <plugins>]`, `--allow-heavy` |

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
[notes] Papyrus code is read only to tell an injector: a script on a record that holds the item (or its list) in a property is one when its compiled code calls AddForm on a leveled list held in a property with it. …
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

> sfs_run {"args": ["archive2", "SFBGS007.esm", "grep", "MGoulet_Pattern05_Test.mat"]}
"MGoulet_Pattern05_Test.mat" in the files of SFBGS007.esm (29 files, 100.1 MB inflated, in 1 archive): 8 hits in 1 file.
Estimated before reading: 29 files, 100.1 MB, about 0.2 s. The search took 0.0 s for 29 files, 100.1 MB.
| Where | File | Offset | Text around it |
| :--- | :--- | ---: | :--- |
| SFBGS007 - Main.ba2 | materials\creations\sfbgs007\materialsbeta.cdb | 40,464,496 | Data\Materials\Test\Test\«MGoulet_Pattern05_Test.mat»_Material2 |
…

> sfs_run {"args": ["archive2", "darkstar.esm", "encoding", "--ext", "strings,dlstrings,ilstrings"]}
How the files of darkstar.esm (files .dlstrings, .ilstrings, .strings) (27 files, 5.8 MB, in 1 archive) are written: 21 ASCII only, 3 Windows-1252, 3 mixed UTF-8 and Windows-1252.
The string tables' 318,276 strings: 290,351 ASCII, 101 UTF-8, 27,824 Windows-1252 (a string that is valid UTF-8 counts as UTF-8).
Estimated before reading: 27 files, 5.8 MB, about 0.2 s. The reading took 0.0 s for 27 files, 5.8 MB.
| Where | File | Encoding | What it holds |
| :--- | :--- | :--- | :--- |
| darkstar - main.ba2 | strings\darkstar_de.dlstrings | mixed UTF-8 and Windows-1252 | a string table of 7,456 strings: 5,697 ASCII, 29 UTF-8, 1,730 Windows-1252; the first byte that isn't UTF-8 at 59,693 |
| darkstar - main.ba2 | strings\darkstar_de.dlstrings | Windows-1252 | string 000003A7 at 59,660: «Entdecken Sie, wie Sie den Legendären Effekt in die Ecke herstellen.» |
…

> sfs_run {"args": ["profile", "--subrecords", "XNAM,UNAM,BNAM,PNAM", "--order", "--type", "PACK", "--in", "@all"]}
The order of XNAM, UNAM, BNAM, PNAM in the PACK records of @all (370 plugins): 5,499 records of 370 plugins stepped through, from the files' bytes; 3 orders.
`…` is one or more other subrecords; `(A B)+` is A B one or more times in a row; a unit that repeats in some record is written so where it stands once too. Repeats: how many times in a row (least-most) and in all.

| Record type | Order | Records | Repeats | Plugins | Sample |
|---|---|---|---|---|---|
| PACK | … (UNAM)+ XNAM … | 5,260 | (UNAM)+: 1-45 in a row, 46,515 in all | 36: Starfield.esm 3,367, ShatteredSpace.esm 566, SFBGS00D.esm 244, … | CF03DelgadoMovesToUnlockTheOuterDoorPackage_UseIDCard (000F98:Starfield.esm) |
| PACK | … (UNAM)+ XNAM (… PNAM)+ … (UNAM BNAM PNAM)+ … | 237 | (UNAM)+: 1-45 in a row, 3,045 in all; (… PNAM)+: 1-48 in a row, 1,308 in all; (UNAM BNAM PNAM)+: 1-114 in a row, 4,719 in all | 14: Starfield.esm 179, kinggathcreations_spaceship.esm 17, non-lethal framework - free.esm 9, … | Travel (002CB0:Starfield.esm) |
| PACK | … XNAM … | 2 |  | 1: Starfield.esm 2 | UC_Den_BastienSandboxBar (032D69:Starfield.esm) |
[source] Starfield.esm 1.36 GB, key d1c0e07a, strings 4b75842f; … (370 plugins)
[cost] 671 ms; read the subrecords of 5,499 records (3.2 MB as stored, 0 inflated) of 370 plugins, from the files' bytes on 8 threads (no record read through Mutagen)

> sfs_run {"args": ["indexes"]}
SFSearch keeps 3 indexes in C:\Modding\SFSearch\SFSearch\indexes: 790.2 MB in all. They are made from the plugin files and hold no record data; each plugin's part is named by its content, so a changed plugin's part is never read.
The header and link indexes are kept between service starts (keepStartupIndexes is on in sfsearch.json): a start brings them up to date plugin by plugin (a new or changed plugin's part is made, the rest is reused) and then checks every plugin by a full hash, so they are ready in seconds instead of being made again.
The world link index is made only when you ask for it (indexes build world, or the answer save_index where a question offers it: allow_heavy="save_index", refs' world "save_index"), then kept whatever keepStartupIndexes says: a plugin you asked for gets a new part when it changes, until you drop it.

| Index | Size | Covers | Built | Made again from nothing | Speeds up |
|---|---|---|---|---|---|
| header | 480.7 MB | 370 of 370 plugins | 2026-11-15 11:46 (14 min ago) | about 3 s (3.96 GB of plugins read and hashed) | every command: what a FormID or EditorID is, who holds a record and who wins, the cost of a read |
| links | 309.5 MB | 370 of 370 plugins that can be walked | 2026-11-15 11:59 (2 min ago) | about 14 s on 8 cores and up to 2.2 GB (3,465,077 records walked) | sfs_refs (what points at a record, outside world data) and in-game names in sfs_find |
| world | 0 bytes | 0 of 119 plugins with world data (not asked for) | not built | about 16 s on 8 cores and up to 0.6 GB (8,752,362 world records of all 119 plugins with world data) | sfs_refs into world data (cells, placed objects, navmeshes, terrain) in a fraction of a second, and sfs_query's Links WHERE ToKey = '…' rows from world records |
…

> sfs_run {"args": ["scripts2", "atom74.esm"]}
Scripts of atom74.esm: 2 scripts, 95 lines, from atom74 - main.ba2 (2).
On records: 2 of the 2 scripts are attached by 2 records of the plugin; none is attached to nothing. 2 records with scripts among 317 looked at.
Code that reaches records without a link in a record, and the records it hands out: 27 calls in 2 scripts (AddForm on a script property 26, AddPerk on a script property 1), among the 2 compiled scripts read. 27 of them hand out a record named in a script property (a link the record holds), each with the value the plugin's records that attach the script give it.
| Script | Lines | From | Attached by |
| :--- | ---: | :--- | :--- |
| Fragments:Quests:QF_ccAtom74_LLInjection_010008DF | 73 | atom74 - main.ba2 | 1: ccAtom74_LLInjection (0008DF:atom74.esm) QUST |
| Fragments:Quests:QF_ccAtom74_LLInjection_Uniq_01000A1E | 22 | atom74 - main.ba2 | 1: ccAtom74_LLInjection_Uniques (000A1E:atom74.esm) QUST |
…

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
- **`sfs_status`**: the service's workers, queue and heavy lane; both start-up indexes and the indexes kept on disk (`indexes` says more); how many plugins each worker holds open; the scratch folder's size; the kept files; memory. The lines to look at when something seems slow or missing:
```text
> sfs_status {}
…
Header index: complete. 370 plugins in Data (224 loaded), 12,217,439 records, 480.6 MB of segments; checked against the files in 70 ms. 9 plugin(s) have problems (loadorder2 problems lists them).
Link index: complete. 370 of 370 plugins have a link file: 17,050,052 links, 1,413,847 in-game names, 309.5 MB.
Kept indexes: header 480.7 MB, links 309.5 MB, world none (made only when asked: indexes build world) in C:\Modding\SFSearch\SFSearch\indexes; kept between service starts (keepStartupIndexes), so a start reuses them. indexes shows them; indexes drop all deletes them (sfs_run).
Scratch folder: C:\Modding\SFSearch\SFSearch\scratch, 0 bytes in 0 file(s). The service empties it at every start; everything in it is made again when needed.
Kept files: C:\Modding\SFSearch\SFSearch\kept, 3.2 MB in 362 file(s) (decompiled scripts, the history log). They outlive a service start, and can be deleted.
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
- **At its start** the service empties its scratch folder and brings its two indexes up to date: the header index (every record's FormKey, type, EditorID and place in its file) and the link index (what points at what, and in-game names). They are kept between starts in `indexes\` of the SFSearch home, so a start reuses every plugin's part and makes only those of new or changed plugins (a first start: about 2 s and 15 s for 370 plugins; later ones about 2.5 s), then checks every plugin by a full hash. Tools answer before the link index is ready, and say what is missing. The world link index is there only when the user asked for it.
- **Before every answer** the plugins it reads are checked against their files (about 20 ms). A plugin that changed on disk is read again, and a note says so.
- **Nothing about a record is kept.** The scratch folder holds working files only while the service runs. What outlives a start: the indexes in `indexes\` (made from the plugin files, each plugin's part named by its content: `sfs_run ["indexes"]` shows them, their size and how to drop them) and, under `kept\`, decompiled scripts and the history log.
