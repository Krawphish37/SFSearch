# Weapon (WEAP)

Record type. Signature WEAP, Mutagen name Weapon, defined in Major Records/Weapon.xml.
297 rows below; 251 fields, 2 of them unknown, 199 with a name from xEdit.
Links to these also accept it: IBaseObject, IBindableEquipment, IConstructibleObjectTarget, IExplodeSpawn, IItem, IPlaceableObject, IResearchResourceTarget, IStaticTarget.
Fields that can hold several kinds of object show one row. Add --all to spell every kind out, or describe the kind.

| Path | Sig | Type | xEdit name | Notes |
|---|---|---|---|---|
| FormKey |  | FormKey, 4 B |  | the record's own ID, in the record header |
| VersionControl |  | UInt32, 4 B |  | record header |
| EditorID | EDID | String | Editor ID | may be absent |
| MajorRecordFlagsRaw |  | Int32, 4 B |  | record header flags |
| IsCompressed |  | Bool |  | not declared in Mutagen's XML: MajorRecord (Mutagen.Bethesda.Core, hand-written); the record header's flag 0x40000 (compressed): a view of MajorRecordFlagsRaw |
| IsDeleted |  | Bool |  | not declared in Mutagen's XML: MajorRecord (Mutagen.Bethesda.Core, hand-written); the record header's flag 0x20 (deleted): a view of MajorRecordFlagsRaw |
| FormVersion |  | UInt16, 2 B |  |  |
| Version2 |  | UInt16, 2 B |  |  |
| StarfieldMajorRecordFlags |  | Enum StarfieldMajorRecord.StarfieldMajorRecordFlag, 4 B |  | read by hand-written Mutagen code |
| VirtualMachineAdapter | VMAD | Object VirtualMachineAdapter |  | may be absent; may continue in XXXX |
| VirtualMachineAdapter.Version | VMAD | Int16, 2 B @0 | Version | default 6 |
| VirtualMachineAdapter.ObjectFormat | VMAD | UInt16, 2 B @2 | Object Format | default 2 |
| VirtualMachineAdapter.Scripts | VMAD | List @4 |  | binary=Custom |
| VirtualMachineAdapter.Scripts[*] | VMAD | Object ScriptEntry |  |  |
| VirtualMachineAdapter.Scripts[*].Name | VMAD | String | ScriptName | stored with a 2-byte length before it; read by hand-written Mutagen code |
| VirtualMachineAdapter.Scripts[*].Flags | VMAD | Enum ScriptEntry.Flag, 1 B | Flags | read by hand-written Mutagen code |
| VirtualMachineAdapter.Scripts[*].Properties | VMAD | List |  | read by hand-written Mutagen code |
| VirtualMachineAdapter.Scripts[*].Properties[*] | VMAD | Object ScriptProperty |  | one of 14 kinds of ScriptProperty: `describe ScriptProperty` lists them |
| VirtualMachineAdapter.Scripts[*].Properties[*].Name | VMAD | String | propertyName | stored with a 2-byte length before it; read by hand-written Mutagen code |
| VirtualMachineAdapter.Scripts[*].Properties[*].Flags | VMAD | Enum ScriptProperty.Flag, 1 B | Flags | default ScriptProperty.Flag.Edited; read by hand-written Mutagen code |
| ObjectBounds | OBND | Object ObjectBounds, 24 B |  |  |
| ObjectBounds.First | OBND | P3Float, 12 B @0 | Min |  |
| ObjectBounds.Second | OBND | P3Float, 12 B @12 | Max |  |
| DirtinessScale | ODTY | Float, 4 B | Dirtiness Scale |  |
| ObjectPaletteDefaults | OPDS | Object ObjectPaletteDefaults, 80 B |  | may be absent |
| ObjectPaletteDefaults.Flags | OPDS | Enum ObjectPaletteDefaults.Flag, 4 B @0 | Flags |  |
| ObjectPaletteDefaults.SinkMeters | OPDS | Float, 4 B @4 | Sink (Meters) | unit: meters |
| ObjectPaletteDefaults.SinkVariance | OPDS | Float, 4 B @8 | Sink Variance +/- |  |
| ObjectPaletteDefaults.XYOffsetVariance | OPDS | Float, 4 B @12 | X/Y Offset Variance +/- |  |
| ObjectPaletteDefaults.FootprintSize | OPDS | Enum ObjectPaletteDefaults.FootprintSizes, 4 B @16 | Footprint (Meters) |  |
| ObjectPaletteDefaults.ScalePercent | OPDS | Float, 4 B @20 | Scale % +/- |  |
| ObjectPaletteDefaults.ScaleVariance | OPDS | Float, 4 B @24 | Scale Variance +/- |  |
| ObjectPaletteDefaults.AngleX | OPDS | Float, 4 B @28 | Angle X (Deg) | unit: degrees |
| ObjectPaletteDefaults.AngleXVariance | OPDS | Float, 4 B @32 | Angle X Variance +/- |  |
| ObjectPaletteDefaults.AngleY | OPDS | Float, 4 B @36 | Angle Y (Deg) | unit: degrees |
| ObjectPaletteDefaults.AngleYVariance | OPDS | Float, 4 B @40 | Angle Y Variance +/- |  |
| ObjectPaletteDefaults.AngleZ | OPDS | Float, 4 B @44 | Angle Z (Deg) | unit: degrees |
| ObjectPaletteDefaults.AngleZVariance | OPDS | Float, 4 B @48 | Angle Z Variance +/- |  |
| ObjectPaletteDefaults.SlopePercent | OPDS | Float, 4 B @52 | Slope % |  |
| ObjectPaletteDefaults.SlopePercentVariance | OPDS | Float, 4 B @56 | Slope % Variance +/- |  |
| ObjectPaletteDefaults.Density | OPDS | Float, 4 B @60 | Density |  |
| ObjectPaletteDefaults.Frequency | OPDS | Float, 4 B @64 | Frequency % |  |
| ObjectPaletteDefaults.SlopeLimit | OPDS | Float, 4 B @68 | Slope Limit |  |
| ObjectPaletteDefaults.DistanceBelowWater | OPDS | Float, 4 B @72 | Distance Below Water |  |
| ObjectPaletteDefaults.DistanceAboveWater | OPDS | Float, 4 B @76 | Distance Above Water |  |
| Transforms | PTT2 | Object Transforms, 32 B |  | may be absent |
| Transforms.Versioning | PTT2 | Enum Transforms.VersioningBreaks |  | not declared in Mutagen's XML: Mutagen's generator adds it for <Break /> in the object's fields (VersioningModule); which of the object's 1 version break its data ended at (Break0, in the XML's order); the writer stops there |
| Transforms.InventoryIcon | PTT2 | FormLink → Transform, 4 B @0 | Inventory Icon Transform |  |
| Transforms.Outpost | PTT2 | FormLink → Transform, 4 B @4 | Workshop Icon Transform |  |
| Transforms.Ship | PTT2 | FormLink → Transform, 4 B @8 | Ship Builder Icon Transform |  |
| Transforms.Preview | PTT2 | FormLink → Transform, 4 B @12 | Preview Transform |  |
| Transforms.Inventory | PTT2 | FormLink → Transform, 4 B @16 | Inventory 3D Override Transform |  |
| Transforms.Workbench | PTT2 | FormLink → Transform, 4 B @20 | Workbench 3D Override Transform  |  |
| Transforms.MainGameUI | PTT2 | FormLink → Transform, 4 B @24 | Data Menu 3D Override Transform |  |
| Transforms.#break | PTT2 | Break |  | older files end here (version break) |
| Transforms.ResearchMenu | PTT2 | FormLink → Transform, 4 B @28 | Research Menu 3D Override Transform |  |
| XalgFlags | XALG | Enum XalgFlag, 8 B |  | may be absent |
| Components |  | List |  |  |
| Components[*] |  | Object AComponent |  | one of 66 kinds of AComponent: `describe AComponent` lists them |
| Name | FULL | String | Name | may be absent; translated text (.strings) |
| Model |  | Object Model |  | may be absent |
| Model.File | MODL | AssetLink | Model | may be absent |
| Model.TextureFileHashes | MODT | ByteArray |  | may be absent; evidence: in no record of Starfield.esm |
| Model.MaterialSwaps | MOLM | List |  | may be absent; its count (2 bytes) comes before the entries |
| Model.MaterialSwaps[*] | MOLM | FormLink → LayeredMaterialSwap, 4 B | Layered Material Swap |  |
| Model.MaterialSwaps2 | MOLM | List |  | may be absent; its count (2 bytes) comes before the entries |
| Model.MaterialSwaps2[*] | MOLM | FormLink → LayeredMaterialSwap, 4 B |  |  |
| Model.LightLayer | FLLD | Enum Light.LightLayer, 4 B |  | may be absent |
| Model.Flags | XFLG | Enum Model.Flag, 1 B |  | may be absent |
| Model.ColorRemappingIndex | MODC | Float, 4 B | Color Remapping Index | may be absent |
| Model.FlagsVestigial | MODF | Enum Model.Flag, 1 B |  | may be absent |
| ObjectEffect | EITM | FormLink → ObjectEffect, 4 B | Effect | may be absent |
| EnchantmentAmount | EAMT | UInt16, 2 B | Capacity | may be absent |
| EquipmentType | ETYP | FormLink → EquipType, 4 B | Equipment Type | may be absent |
| BlockBashImpactDataSet | BIDS | FormLink → ImpactDataSet, 4 B | Block Bash Impact Data Set | may be absent |
| AlternateBlockMaterial | BAMT | FormLink → MaterialType, 4 B | Alternate Block Material | may be absent |
| PickupSound | PUSH | Object SoundReference, 40 B |  | may be absent |
| PickupSound.StartEvent | PUSH | Guid, 16 B @0 | Start Event/Form |  |
| PickupSound.StopEvent | PUSH | Guid, 16 B @16 | Stop |  |
| PickupSound.ConditionForm | PUSH | FormLink → ConditionRecord, 4 B @32 | Condition | CK: "Condition Form" |
| PickupSound.EventForm | PUSH | FormLink → WWiseEventData, 4 B @36 | Start Form | CK: "Event Form" |
| DropdownSound | PDSH | Object SoundReference, 40 B |  | may be absent |
| DropdownSound.StartEvent | PDSH | Guid, 16 B @0 | Start Event/Form |  |
| DropdownSound.StopEvent | PDSH | Guid, 16 B @16 | Stop |  |
| DropdownSound.ConditionForm | PDSH | FormLink → ConditionRecord, 4 B @32 | Condition | CK: "Condition Form" |
| DropdownSound.EventForm | PDSH | FormLink → WWiseEventData, 4 B @36 | Start Form | CK: "Event Form" |
| Keywords | KWDA | List |  | may be absent; counted by KSIZ |
| Keywords[*] | KWDA | FormLink → Keyword, 4 B | Keyword |  |
| Description | DESC | String |  | may be absent; translated text (.dlstrings) |
| InstanceNaming | INRD | FormLink → InstanceNamingRules, 4 B | Instance Naming | may be absent |
| AttachParentSlots | APPR | List |  | may be absent |
| AttachParentSlots[*] | APPR | FormLink → Keyword, 4 B | Keyword |  |
| ObjectTemplates |  | List |  | may be absent; counted by OBTE |
| ObjectTemplates[*] |  | Object ObjectTemplate |  |  |
| ObjectTemplates[*].IsEditorOnly | OBTF | Bool, 0 B |  | true when the subrecord is present |
| ObjectTemplates[*].Name | FULL | String | Name | may be absent; translated text (.strings) |
| ObjectTemplates[*].OBTSLogic | OBTS | CustomLogic |  | may be absent; read by hand-written Mutagen code; a parsing step, not a value: no property |
| ObjectTemplates[*].AddonIndex | OBTS | Int16, 2 B | Parent Combination Index | default -1; read by hand-written Mutagen code; signature from xEdit's pairs (the XML doesn't give one) |
| ObjectTemplates[*].LevelMin | OBTS | UInt16, 2 B | Level Min | read by hand-written Mutagen code; signature from xEdit's pairs (the XML doesn't give one) |
| ObjectTemplates[*].LevelMax | OBTS | UInt16, 2 B | Level Max | read by hand-written Mutagen code; signature from xEdit's pairs (the XML doesn't give one) |
| ObjectTemplates[*].Default | OBTS | Bool, 1 B | Default | read by hand-written Mutagen code; signature from xEdit's pairs (the XML doesn't give one) |
| ObjectTemplates[*].Keywords | OBTS | List |  | read by hand-written Mutagen code; its count (1 byte) comes before the entries; signature from xEdit's pairs (the XML doesn't give one) |
| ObjectTemplates[*].Keywords[*] | OBTS | FormLink → Keyword, 4 B | Keyword #* | signature from xEdit's pairs (the XML doesn't give one) |
| ObjectTemplates[*].MinLevelForRanks | OBTS | UInt8, 1 B | Min Level For Ranks | read by hand-written Mutagen code; signature from xEdit's pairs (the XML doesn't give one) |
| ObjectTemplates[*].AltLevelsPerTier | OBTS | UInt8, 1 B | Alt Levels Per Tier | read by hand-written Mutagen code; signature from xEdit's pairs (the XML doesn't give one) |
| ObjectTemplates[*].Includes | OBTS | List |  | read by hand-written Mutagen code; signature from xEdit's pairs (the XML doesn't give one) |
| ObjectTemplates[*].Includes[*] | OBTS | Object ObjectTemplateInclude |  | signature from xEdit's pairs (the XML doesn't give one) |
| ObjectTemplates[*].Includes[*].Mod | OBTS | FormLink → AObjectModification, 4 B | Mod |  |
| ObjectTemplates[*].Includes[*].AttachPointIndex | OBTS | UInt8, 1 B | Attach Point Index |  |
| ObjectTemplates[*].Includes[*].Optional | OBTS | Bool, 1 B | Optional |  |
| ObjectTemplates[*].Includes[*].DontUseAll | OBTS | Bool, 1 B | Don't Use All |  |
| ObjectTemplates[*].Properties | OBTS | List |  | read by hand-written Mutagen code; signature from xEdit's pairs (the XML doesn't give one) |
| ObjectTemplates[*].Properties[*] | OBTS | Object AObjectModProperty |  | one of 7 kinds of AObjectModProperty: `describe AObjectModProperty` lists them; signature from xEdit's pairs (the XML doesn't give one) |
| STOP | STOP | Marker, 0 B |  | empty marker subrecord, not a value; evidence: second pass: marker only; in no record of Starfield.esm; empty marker; nothing to name |
| EmbeddedWeaponMod | NNAM | FormLink → AObjectModification, 4 B | Embedded Weapon Mod | may be absent |
| IsEmbeddedWeapon | BNAM | Bool, 0 B |  | true when the subrecord is present |
| SightedTransitionSeconds | WAIM | Float, 4 B @0 | Sighted Transition (Seconds) | unit: seconds |
| AimDownSightTemplate | WAIM | FormLink → Zoom, 4 B @4 | Aim Down Sight Template |  |
| AimModel | WAIM | FormLink → AimModel, 4 B @8 | Aim Model |  |
| AccuracyBonus | WAIM | UInt8, 1 B @12 | Accuracy Bonus |  |
| HasScope | WAIM | Bool, 1 B @13 | Has Scope |  |
| AimAssistTemplate | WAIM | FormLink → AimAssistModel, 4 B @14 | Aim Assist Template |  |
| AimOpticalSightModel | WAIM | FormLink → AimOpticalSightMarker, 4 B @18 | Aim Optical Sight Model |  |
| MeleeAimAssistModel | WAIM | FormLink → MeleeAimAssistModel, 4 B @22 | Melee Aim Assist Model |  |
| UpdateAdvancedAimInfo | WAIM | Bool, 1 B @26 | Update Advanced Aim Info |  |
| EnableOrientFireNodesToAimingDirection | WAIM | Bool, 1 B @27 | Enable Orient Fire Nodes To Aiming Direction |  |
| EnableMarkingTargets | WAIM | Bool, 1 B @28 | Enable Marking Targets |  |
| ReticleType | WAIM | Enum Weapon.ReticleTypeEnum, 4 B @29 | Reticle Type |  |
| InverseReticleSpread | WAIM | Bool, 1 B @33 | Inverse Reticle Spread |  |
| DisplayedReticleIgnoresPlayerMovement | WAIM | Bool, 1 B @34 | Displayed Reticle on UI Ignores Player Movement |  |
| AmmoType | WAM2 | FormLink → Ammunition, 4 B @0 | Ammo Type |  |
| AmmoCapacity | WAM2 | UInt32, 4 B @4 | Ammo Capacity |  |
| AmmoList | WAM2 | FormLink → LeveledItem, 4 B @8 | Ammo List |  |
| OverrideProjectile | WAM2 | FormLink → Projectile, 4 B @12 | Override Projectile |  |
| OverrideShellCasing | WAM2 | FormLink → ArtObject, 4 B @16 | Override Shell Casing |  |
| ProjectilesCount | WAM2 | UInt8, 1 B @20 | Projectiles Count |  |
| NpcsUseAmmo | WAM2 | Bool, 1 B @21 | NPCs Use Ammo |  |
| NumberOfImpactSounds | WAM2 | UInt8, 1 B @22 | Number of Impact Sounds |  |
| WAMM | WAMM | ByteArray |  | **unknown**; may be absent; evidence: in no record of Starfield.esm |
| MeleeOrCreature | WAUD | Object SoundReference, 40 B @0 |  |  |
| MeleeOrCreature.StartEvent | WAUD | Guid, 16 B @0 | Start Event/Form |  |
| MeleeOrCreature.StopEvent | WAUD | Guid, 16 B @16 | Stop |  |
| MeleeOrCreature.ConditionForm | WAUD | FormLink → ConditionRecord, 4 B @32 | Condition | CK: "Condition Form" |
| MeleeOrCreature.EventForm | WAUD | FormLink → WWiseEventData, 4 B @36 | Start Form | CK: "Event Form" |
| PrimedExplosive | WAUD | Object SoundReference, 40 B @40 |  |  |
| PrimedExplosive.StartEvent | WAUD | Guid, 16 B @40 | Start Event/Form |  |
| PrimedExplosive.StopEvent | WAUD | Guid, 16 B @56 | Stop |  |
| PrimedExplosive.ConditionForm | WAUD | FormLink → ConditionRecord, 4 B @72 | Condition | CK: "Condition Form" |
| PrimedExplosive.EventForm | WAUD | FormLink → WWiseEventData, 4 B @76 | Start Form | CK: "Event Form" |
| DryFire | WAUD | Object SoundReference, 40 B @80 |  |  |
| DryFire.StartEvent | WAUD | Guid, 16 B @80 | Start Event/Form |  |
| DryFire.StopEvent | WAUD | Guid, 16 B @96 | Stop |  |
| DryFire.ConditionForm | WAUD | FormLink → ConditionRecord, 4 B @112 | Condition | CK: "Condition Form" |
| DryFire.EventForm | WAUD | FormLink → WWiseEventData, 4 B @116 | Start Form | CK: "Event Form" |
| Idle | WAUD | Object SoundReference, 40 B @120 |  |  |
| Idle.StartEvent | WAUD | Guid, 16 B @120 | Start Event/Form |  |
| Idle.StopEvent | WAUD | Guid, 16 B @136 | Stop |  |
| Idle.ConditionForm | WAUD | FormLink → ConditionRecord, 4 B @152 | Condition | CK: "Condition Form" |
| Idle.EventForm | WAUD | FormLink → WWiseEventData, 4 B @156 | Start Form | CK: "Event Form" |
| Equip | WAUD | Object SoundReference, 40 B @160 |  |  |
| Equip.StartEvent | WAUD | Guid, 16 B @160 | Start Event/Form |  |
| Equip.StopEvent | WAUD | Guid, 16 B @176 | Stop |  |
| Equip.ConditionForm | WAUD | FormLink → ConditionRecord, 4 B @192 | Condition | CK: "Condition Form" |
| Equip.EventForm | WAUD | FormLink → WWiseEventData, 4 B @196 | Start Form | CK: "Event Form" |
| Unequip | WAUD | Object SoundReference, 40 B @200 |  |  |
| Unequip.StartEvent | WAUD | Guid, 16 B @200 | Start Event/Form |  |
| Unequip.StopEvent | WAUD | Guid, 16 B @216 | Stop |  |
| Unequip.ConditionForm | WAUD | FormLink → ConditionRecord, 4 B @232 | Condition | CK: "Condition Form" |
| Unequip.EventForm | WAUD | FormLink → WWiseEventData, 4 B @236 | Start Form | CK: "Event Form" |
| FastEquip | WAUD | Object SoundReference, 40 B @240 |  |  |
| FastEquip.StartEvent | WAUD | Guid, 16 B @240 | Start Event/Form |  |
| FastEquip.StopEvent | WAUD | Guid, 16 B @256 | Stop |  |
| FastEquip.ConditionForm | WAUD | FormLink → ConditionRecord, 4 B @272 | Condition | CK: "Condition Form" |
| FastEquip.EventForm | WAUD | FormLink → WWiseEventData, 4 B @276 | Start Form | CK: "Event Form" |
| SoundLevel | WAUD | Enum SoundLevel, 4 B @280 | Sound Level |  |
| MeleeDetectionSoundLevel | WAUD | Enum SoundLevel, 4 B @284 | Melee Detection Sound Level |  |
| TurretEnabled | WTUR | Bool, 1 B @0 | Enabled |  |
| TurretHeadingMin | WTUR | Float, 4 B @1 | Min |  |
| TurretHeadingMax | WTUR | Float, 4 B @5 | Max |  |
| TurretPitchMin | WTUR | Float, 4 B @9 | Min |  |
| TurretPitchMax | WTUR | Float, 4 B @13 | Max |  |
| TurretHeadingSpeed | WTUR | Float, 4 B @17 | Heading Speed |  |
| TurretPitchSpeed | WTUR | Float, 4 B @21 | Pitch Speed |  |
| TurretAccuracy | WTUR | Float, 4 B @25 | Accuracy |  |
| ChargeFullPowerSeconds | WCHG | Float, 4 B @0 | Full Power (Seconds) | unit: seconds |
| ChargeMinPowerPerShot | WCHG | Float, 4 B @4 | Min Power Per Shot |  |
| ChargeCritBonus | WCHG | Float, 4 B @8 | Crit Charge Bonus |  |
| ChargeHoldInput | WCHG | Bool, 1 B @12 | Hold Input |  |
| ChargingAttack | WCHG | Bool, 1 B @13 | Charging Attack Enabled |  |
| AttackDamage | WDMG | Float, 4 B @0 | Base Damage |  |
| MinRange | WDMG | Float, 4 B @4 | Range - Min |  |
| MaxRange | WDMG | Float, 4 B @8 | Range - Max |  |
| OutOfRangeDamageMult | WDMG | Float, 4 B @12 | Out of Range Damage Mult |  |
| CritDamageMult | WDMG | Float, 4 B @16 | Crit Damage Mult |  |
| CriticalHitSpell | WDMG | FormLink → Spell, 4 B @20 | Crit Effect (Spell) |  |
| CritEffectOnDeathOnly | WDMG | Bool, 1 B @24 | Crit Effect on Death Only |  |
| HitBehavior | WDMG | Enum HitBehavior, 1 B @25 | Hit Behavior |  |
| Resistance | WDMG | FormLink → ActorValueInformation, 4 B @26 | Resistance |  |
| Skill | WDMG | FormLink → ActorValueInformation, 4 B @30 | Skill |  |
| LowGravityScaleControlPoint | WDMG | Float, 4 B @34 | Scale Control Point |  |
| LowGravityRangeMultMin | WDMG | Float, 4 B @38 | Min |  |
| LowGravityRangeMultMax | WDMG | Float, 4 B @42 | Max |  |
| HighGravityScaleControlPoint | WDMG | Float, 4 B @46 | Scale Control Point |  |
| HighGravityRangeMultMin | WDMG | Float, 4 B @50 | Min |  |
| HighGravityRangeMultMax | WDMG | Float, 4 B @54 | Max |  |
| CritChanceIncMult | WDMG | Float, 4 B @58 | Crit Chance Inc Mult |  |
| DamageTypes | DAMA | List |  | may be absent |
| DamageTypes[*] | DAMA | Object WeaponDamageType |  |  |
| DamageTypes[*].DamageType | DAMA | FormLink → DamageType, 4 B @0 | Type |  |
| DamageTypes[*].Value | DAMA | UInt32, 4 B @4 | Amount |  |
| DamageTypes[*].CurveTable | DAMA | FormLink → CurveTable, 4 B @8 | Curve Table |  |
| FiringType | WFIR | Enum Weapon.FiringTypeEnum, 1 B @0 | Firing Type |  |
| BurstCount | WFIR | UInt8, 1 B @1 | Burst count |  |
| RepeatableFire | WFIR | Bool, 1 B @2 | Repeatable Fire |  |
| AttackSeconds | WFIR | Float, 4 B @3 | Attack (Seconds) | unit: seconds |
| FireSeconds | WFIR | Float, 4 B @7 | Fire (Seconds) | unit: seconds |
| AttackDelaySeconds | WFIR | Float, 4 B @11 | Attack Delay (Seconds) | unit: seconds |
| BoltChargeSeconds | WFIR | Float, 4 B @15 | Bolt Charge (Seconds) | unit: seconds |
| BoltAction | WFIR | Bool, 1 B @19 | Bolt Action |  |
| DisableShellCaseEject | WFIR | Bool, 1 B @20 | Disable ShellCase Eject |  |
| ShotsPerSecond | WFIR | Float, 4 B @21 | Shots Per Second |  |
| WeaponAttackAnimationDuration | WFIR | Float, 4 B @25 | Weapon Attack Animation Duration |  |
| OverrideRateOfFire | WFIR | Bool, 1 B @29 | Override Rate of Fire |  |
| TriggerThresholdPrimaryTrigger | WFIR | Float, 4 B @30 | Trigger Threshold - Primary Trigger |  |
| OverrideTriggerThreshold | WFIR | Bool, 1 B @34 | Override Trigger Threshold |  |
| TriggerThresholdSecondStage | WFIR | Float, 4 B @35 | Trigger Threshold - Second Stage |  |
| HasStagedTrigger | WFIR | Bool, 1 B @39 | Use Staged Trigger |  |
| HasDualTrigger | WFIR | Bool, 1 B @40 | Has Dual Trigger |  |
| BurstDelaySeconds | WFIR | Float, 4 B @41 | Burst Delay (Seconds) | unit: seconds |
| NonPlayable | WFLG | Bool, 1 B @0 | Non-Playable |  |
| PlayerOnly | WFLG | Bool, 1 B @1 | Player Only |  |
| PreventUseInNormalCombat | WFLG | Bool, 1 B @2 | Prevent Use In Normal Combat |  |
| IsSecondary | WFLG | Bool, 1 B @3 | Is Secondary |  |
| CannotDrop | WFLG | Bool, 1 B @4 | Can't Drop |  |
| MinorCrime | WFLG | Bool, 1 B @5 | Minor Crime |  |
| NonHostile | WFLG | Bool, 1 B @6 | Non-Hostile |  |
| Bound | WFLG | Bool, 1 B @7 | Bound |  |
| NpcPrefersLongBursts | WFLG | Bool, 1 B @8 | NPC Prefers Long Bursts |  |
| NpcsOnlyFireWhenMagazineFull | WFLG | Bool, 1 B @9 | NPCs Only Fire When Magazine Full |  |
| UseSequentialFireNodes | WFLG | Bool, 1 B @10 | Use Sequential Fire Nodes |  |
| UseRandomFireNodes | WFLG | Bool, 1 B @11 | Use Random Fire Nodes |  |
| AnimationType | WGEN | Enum Weapon.AnimationTypes, 4 B @0 | Type |  |
| BaseWeight | WGEN | Float, 4 B @4 | Base weight |  |
| BaseValue | WGEN | UInt32, 4 B @8 | Base value |  |
| BaseSpeed | WGEN | Float, 4 B @12 | Base Speed |  |
| AttackOxygenCost | WGEN | Float, 4 B @16 | Attack Oxygen Cost |  |
| WeaponBarrel | WGEN | FormLink → WeaponBarrelModel, 4 B @20 | Weapon Barrel |  |
| General | WABB | String | Abbreviation | may be absent; translated text (.strings) |
| AttackSpeed | WMEL | Enum Weapon.AttackSpeedEnum, 1 B @0 | Attack Speed |  |
| MeleeBashDamage | WMEL | Float, 4 B @1 | Bash Damage |  |
| MeleeReach | WMEL | Float, 4 B @5 | Reach |  |
| MeleeStagger | WMEL | Enum Stagger, 4 B @9 | Stagger |  |
| Power | QNAM | FormLink → ActorValueInformation, 4 B @0 | Power |  |
| PowerRechargeTime | QNAM | Float, 4 B @4 | Recharge time |  |
| PowerRechargeDelay | QNAM | Float, 4 B @8 | Recharge delay |  |
| ConsumeAmmo | QNAM | Bool, 1 B @12 | Consume Ammo |  |
| PowerBonus | QNAM | FormLink → ActorValueInformation, 4 B @13 | Power - Bonus |  |
| UsePower | QNAM | Bool, 1 B @17 | Use Power |  |
| VariablePowerEnabled | QNAM | Bool, 1 B @18 | Variable Power Enabled |  |
| VariablePowerTransitionDuration | QNAM | Float, 4 B @19 | Variable Power Transition Duration |  |
| UseChargeOnlyInFocusMode | QNAM | Bool, 1 B @23 | Use Charge Only In Focus Mode |  |
| PowerIntensityCurve | QNAM | FormLink → CurveTable, 4 B @24 | Power Intensity Curve |  |
| PowerDamageMultiplier | QNAM | Float, 4 B @28 | Power Damage Multiplier |  |
| PowerConsumption | QNAM | FormLink → CurveTable, 4 B @32 | Power Consumption Multiplier Curve |  |
| ReloadSeconds | WRLO | Float, 4 B @0 | Reload Seconds | unit: seconds |
| ReloadSpeed | WRLO | Float, 4 B @4 | Reload Speed |  |
| ReloadCharging | WRLO | Bool, 1 B @8 | Charging |  |
| ReloadSingle | WRLO | Bool, 1 B @9 | Single |  |
| WRUM | WRUM | ByteArray |  | **unknown**; may be absent; evidence: in no record of Starfield.esm |
| ApertureValueMin | WVAR | Float, 4 B @0 | Value - Min |  |
| ApertureValueMax | WVAR | Float, 4 B @4 | Value - Max |  |
| ApertureInputMin | WVAR | Float, 4 B @8 | Input - Min |  |
| ApertureInputMax | WVAR | Float, 4 B @12 | Input - Max |  |
| ApertureAcceleration | WVAR | Float, 4 B @16 | Acceleration |  |
| ApertureDeceleration | WVAR | Float, 4 B @20 | Deceleration |  |
| DistanceValueMin | WVAR | Float, 4 B @24 | Value - Min |  |
| DistanceValueMax | WVAR | Float, 4 B @28 | Value - Max |  |
| DistanceInputMin | WVAR | Float, 4 B @32 | Input - Min |  |
| DistanceInputMax | WVAR | Float, 4 B @36 | Input - Max |  |
| DistanceAcceleration | WVAR | Float, 4 B @40 | Acceleration |  |
| DistanceDeceleration | WVAR | Float, 4 B @44 | Deceleration |  |
| UseVariableRange | WVAR | Bool, 1 B @48 | Use Variable Range |  |
| FirstPersonModel |  | Object FirstPersonModel |  | may be absent |
| FirstPersonModel.File | MOD4 | AssetLink | 1st Person Model | may be absent |
| FirstPersonModel.LightLayer | FLLD | Enum Light.LightLayer, 4 B |  | may be absent |
| FirstPersonModel.MaterialSwap | MO4S | FormLink → LayeredMaterialSwap, 4 B |  | may be absent |
| FirstPersonModel.ColorRemappingIndex | MO4C | Float, 4 B |  | may be absent |
| FirstPersonOffset | WVIS | P3Float, 12 B @0 | First Person Offset |  |
| ImpactDataSet | WVIS | FormLink → ImpactDataSet, 4 B @12 | Impact Data Set |  |
| ColorRemappingIndex | WVIS | Float, 4 B @16 |  |  |
| ImageSpaceAdapter | WVIS | FormLink → ImageSpaceAdapter, 4 B @20 | Image Space Adapter |  |
| AttackActionPointCost | WTRM | Float, 4 B @0 | Attack Action Point Cost |  |
| OverrideDefaultCameraShake | WTRM | Bool, 1 B @4 | Override Default Camera Shake |  |
| FiringCameraShakeIntensity | WTRM | Float, 4 B @5 | Firing Camera Shake Intensity |  |
| FiringCameraShakeDuration | WTRM | Float, 4 B @9 | Firing Camera Shake Duration |  |
| AiFiringArc | WTRM | Float, 4 B @13 | AI Firing Arc |  |
| MajorFlags |  | Enum Weapon.MajorFlag |  | not declared in Mutagen's XML: Mutagen's generator adds it for majorFlag="true" (MajorRecordFlagModule); the record header's flags (MajorRecordFlagsRaw) as this record type names them |
