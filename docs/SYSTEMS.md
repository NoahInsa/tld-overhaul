# Systems reference

One section per system, in build order. Each answers the three questions asked before any code was written:
**what TLD does now** (read from the decompiled interop stubs), **what is patched and why** (prefix vs postfix), and
**what new state exists and how it persists**.

Conventions that apply everywhere:

* One namespace per system (`TLDOverhaul.Degradation`, `.Nutrition`, ...), shared contracts in `TLDOverhaul.Core`
  (`Services.cs`: `ISkillService`, `IToolService`, `IScrapService`, `ICraftingService`, `IRegionalMaterials`).
  A system consumes services of the systems built before it; if a system is disabled its service falls back to a neutral
  null-object so nothing else breaks.
* Every patch calls `PatchLog.Fire(id)` first, which prints `[patch fired] <System>.<PatchClass>` once. F10 in game lists the
  patches that applied but never fired (the tell-tale of an IL2CPP-inlined target).
* Prefix/postfix only. No transpilers, no IL manipulation.
* All persistent state lives in **one** JSON sidecar per save slot:
  `UserData/TLDOverhaul/saves/<slot>.json` = `{ "version":1, "systems": { "degradation": {...}, "nutrition": {...}, ... } }`.
  Each system registers one `ISaveSection`; none touch files. The sidecar is written after `SaveGameSystem.SaveGame`, read in a
  prefix on `SaveGameSystem.RestoreGame`, deleted with the slot and copied with `SaveGameSlots.CopyData`. A vanilla save with no
  sidecar simply starts every system fresh.
* Tuning knobs are `MelonPreferences` (`UserData/MelonPreferences.cfg`, category `TLDOverhaul_<System>`), every system has an
  `Enabled` switch, and two data files can be edited without recompiling: `UserData/TLDOverhaul/recipes.json` (recipe skill/tier
  overrides) and `UserData/TLDOverhaul/exploration.json` (where skill-book volumes are scattered).

Patch targets with `CallerCount(0)` in the stubs (possibly inlined by IL2CPP, so they may never fire; each has either a second
hook or a graceful fallback): `ClothingItem.GetDailyHPDecay`, `CraftingOperation.HandleSuccess` (delegate-invoked),
`Panel_BreakDown.DegradeToolUsed`, `Panel_Repair.DegradeToolUsedForRepair`, `Hunger.Update` (Unity message, safe),
`PlayerClimbRope.BeginClimbing`, `RopeClimbPoint.PerformInteraction`, `Skill.IncrementPoints`,
`ResearchItem.NoBenefitAtCurrentSkillLevel`, `PlayerInVehicle.GetTempIncrease`, `BaseAi.ProcessGunshotAudioEvent`.

---

## 1. Tool / item degradation  (`TLDOverhaul.Degradation`)

**TLD now.** Wear is flat. `GearItem.Degrade(float hp)` (30 callers) is the single choke point that subtracts a fixed amount
(`DegradeOnUse.m_DegradeHP`, `ToolsItem.m_DegradePerHourCrafting`, `Panel_Repair.DegradeToolUsedForRepair`,
`Panel_BreakDown.DegradeToolUsed`). `Panel_Repair.RepairSuccessful` restores a fixed `Repairable.m_ConditionIncrease` up to
`m_RepairConditionCap`; nothing remembers an item's history.

**Patches.**
| Patch | Kind | Why |
|---|---|---|
| `GearItem.Degrade` | Prefix (`ref hp`) | rewrite the wear before vanilla applies it: grade multiplier x accelerating condition curve x skill x "misuse" |
| `CraftingOperation.DegradeTools`, `Panel_BreakDown.DegradeToolUsed`, `Panel_Repair.DegradeToolUsedForRepair` | Prefix+Postfix | tag the wear with the trade (skill) it belongs to, valid one frame only |
| `Panel_Repair.RepairSuccessful` | Prefix+Postfix | capture HP before, then rescale the gain by skill/tool, cap it, and apply permanent max-condition loss |
| `Repairable.GetRepairConditionCap` | Postfix | only ever lowers vanilla's answer (item max-loss and the tool's repair ceiling) |
| `Panel_Repair.GetChanceSuccess`, `GetModifiedRepairDuration` | Postfix | tool performance makes repairs less likely and slower |

**Model.** Three tool grades (Improvised / Manufactured / HighQuality) with different wear multipliers, performance knees,
performance floors and repair ceilings. `Services.Tools` exposes `GetPerformance`, `GetGrade`, `GetRepairCeiling` to crafting,
vehicles, building. A tool used outside its trade (a knife for carpentry) wears 1.6x faster. Other systems may scale wear via
`DegradationSystem.ExtraWear` (weapon mods use it).

**State.** Per item (keyed by `ObjectGuid`): `[repairs, maxConditionLoss, lifetimeWear]` -> sidecar section `degradation`.

## 2. Nutrition  (`TLDOverhaul.Nutrition`)

**TLD now.** One calorie reserve (`Hunger.m_CurrentReserveCalories`), burned per activity and refilled by
`AddReserveCaloriesOverTime` / `AddReserveCalories`; starving drains condition. `FoodItem` has meat/fish/natural/fat flags but
the player has no notion of what a meal was made of.

**Patches.**
| Patch | Kind | Why |
|---|---|---|
| `Hunger.Update` | Prefix+Postfix | Unity message (reliable). Detect progressive eating as `m_CaloriesLeftToAdd` shrinking while a `FoodItem` provides calories; credit macros; take back calories lost to a monotonous diet |
| `Hunger.AddReserveCalories` | Prefix | catch instant intake that bypasses the over-time path (ignored while inside `Hunger.Update` to avoid double counting) |
| `Fatigue.AddFatigue(float, FatigueFlags)` | Prefix | scale fatigue *gains* for carb/protein deficit and body weight |
| `Freezing.AddFreezing` | Prefix | scale freezing *gains* for lipid deficit / underweight |

**Model.** Food names -> macro profiles (`FoodProfiles.cs`, real item names from the addressables catalog; flag-based fallback).
Three body stores (protein / carbohydrate / lipid, grams around a maintenance baseline) are filled by eating and drained by
the calorie burn rate in fixed shares. A body-weight value follows the calorie bar and the stores. Consequences: cold,
fatigue, slower healing, max-condition ceiling ("wasting"), and a calorie-absorption penalty for a monotonous diet. Weight
and stores are integrated in <= 1 h steps so sleeping behaves like real time.

**State.** `protein, carbs, lipids, weight, meals[]` -> sidecar section `nutrition`.

## 3. Clothing  (`TLDOverhaul.Clothing`)

**TLD now.** `ClothingItem` has warmth, windproof, wetness/frozen state and layered regions (Face/Neck/Head/Hands/Chest/Legs/
Feet); `PlayerManager.UpdateBonusValuesFromWornClothing` sums them. No dirt or blood; condition does not model holes.

**Patches.** `ClothingItem.GetWarmth` / `GetWindproof` (8 callers each) **Postfix**: scale one garment's contribution by its
condition curve, dirt and blood. `ClothingItem.GetDailyHPDecay` **Postfix** (dirt abrades). `Inventory.GetExtraScentIntensity`
**Postfix** (blood scent -> predators, replacing PZ's zombie attraction). `GearItem.GetItemPostFixForInventoryInterfaces`
**Postfix** ("[bloody] [dirty]" in the inventory). `Condition.AddHealth(float, DamageSource, bool)` **Prefix** and
`Panel_BodyHarvest.HarvestSuccessful` / `QuarterSuccessful` **Postfix**: sources of blood. `Panel_Repair.GetChanceSuccess`
**Postfix**: warmer/technical garments need a higher Tailoring tier to repair.

**State.** Per garment (`ObjectGuid`): `[dirt, blood]` -> sidecar section `clothing`. Dirt accrues per worn hour outdoors,
blood from harvesting, predator damage and bleeding wounds; both fade slowly, and rain/snow soaking rinses dirt.

## 4. Skills and book-gating  (`TLDOverhaul.Skills`)

**TLD now.** Ten vanilla skills (`Skill`, `m_CurrentPoints`, `m_TierPoints`) advanced through
`SkillsManager.IncrementPointsAndNotify` (13 callers). Books are `ResearchItem`s: reading for `m_TimeRequirementHours` grants
`m_SkillPoints`. No tier ceiling, no rust; no Carpentry/Mechanics/Blacksmithing/Tanning/Foraging/First Aid/Skiing.

**Patches.** `SkillsManager.IncrementPointsAndNotify` **Prefix** (rust scaling + clamp to the tier ceiling, return false to
suppress the notification); `Skill.IncrementPoints` **Prefix** (clamp only); `ResearchItem.OnResearchComplete`
**Prefix+Postfix** (record the volume *before* vanilla grants points, zero the book's own XP, ease cabin fever);
`ResearchItem.NoBenefitAtCurrentSkillLevel` **Postfix**; `GearItem.GetBasicDisplayNameForInventoryInterfaces` **Postfix**
(volume shown on the book).

**Model.** Tiers I-V = Beginner..Master. Practice XP cannot cross the tier ceiling; **volume N is required to leave tier N**
(N = 1..4); volume V is the capstone that unlocks the last quarter of Master XP. Vanilla books map to volume I/II of their skill
(`BookRegistry.cs`); every further volume is a vanilla readable "shell" prefab tagged by GUID, so books placed by the
exploration system read like real ones. Skill rust: after a grace period, efficiency inside the tier decays (tiers never lost).
Overhaul-owned skills keep their own XP (`OverhaulSkillTierXp` pref). Existing saves are grandfathered at their current tier.

**State.** `xp{}, volumes{}, practice{} (last-practised hours), grandfather{}, frac{}, tags{guid->[skill,volume]}` -> `skills`.

## 5. Crafting  (`TLDOverhaul.Crafting`)

**TLD now.** `Panel_Crafting` lists `BlueprintData` filtered by `CraftingLocation`; `CraftingOperation` runs a timed bar,
consumes materials progressively, `HandleSuccess` creates the item. Quality is always full; duration only has tool/workstation
modifiers; there is no failure.

**Patches.** `Panel_Crafting.CanCraftBlueprint`, `CanCraftSelectedBlueprint`, `BlueprintData.CanCraftBlueprint` **Postfix**
(AND a skill gate onto vanilla's answer; never overrides a "no"); `Panel_Crafting.ItemPassesFilter` **Postfix** (recipes two
tiers above you are undiscovered); `Panel_Crafting.OnBeginCrafting` **Prefix** (explain why); `RefreshSelectedBlueprint`
**Postfix** (HUD reason); `CraftingOperation.GetModifiedCraftingDuration` and `Panel_Crafting.GetFinalCraftingTimeWithAllModifiers`
**Prefix+Postfix** with a shared depth guard so the time scale is applied exactly once; `CraftingOperation.HandleSuccess`
**Prefix+Postfix** (snapshot the pack, then find the new items and apply quality or failure).

**Model.** Each blueprint -> (skill, tier) derived from its own data (`BlueprintInfo`: applied skill, workstation, duration,
material count) with `recipes.json` overrides. Time scales with tool performance (system 1) and skill. Product quality scales
with skill, tool and how many times you have made that recipe; failure wastes half the materials. Extension points used by
later systems: `CraftingSystem.ExtraGates`, `ExtraHidden`, `CraftedHooks`, `InteractiveClaims`.

**State.** crafts-per-recipe counts -> `crafting`.

## 6. Forge recycling  (`TLDOverhaul.Forge`)

**TLD now.** `Forge` is a crafting location (`CraftingLocation.Forge`) that needs a lit fire (`ForgeHotEnoughForUse`).
Scrap metal is a plain item; metal junk is set dressing.

**Patches.** `BreakDown.DoBreakDown` **Postfix**: metal objects broken down in the world also yield scrap. Everything else is
a shared keyboard station menu (F7, `Core/BenchMenu.cs`) because TLD's real panels cannot be extended from a code-only mod.

**Model.** Junk and ruined tools are classified (`ForgeSystem.Table`), smelted at a hot forge via TLD's own progress bar into
three grades: **Low = real `GEAR_ScrapMetal` items** (so every vanilla recipe still works and hauling has weight), **Mid/High
= overhaul ledger** (automotive metal, tool steel). Yield scales with Blacksmithing and forge class; the small coastal forge
(Desolation Point) caps High-grade at Mid and batches at 10 units, the industrial works (Forlorn Muskeg) does not.
`IScrapService` is what building/weapons/vehicles consume. A new `BenchLocator` finds Forge/AmmoWorkBench/WorkBench/vehicles.

**State.** `mid, high, frac[low,mid,high], smelted` -> `forge`.

## 7. Building / construction  (`TLDOverhaul.Building`)

**TLD now.** Furniture is crafted at the furniture workbench (`BlueprintData.m_CraftedResultDecoration`), placed as
`DecorationItem`s, and taken apart with `BreakDown`/`Panel_BreakDown` (fixed yields). Architect is a separate mod and is not
part of the decompiled source.

**Patches.** `BreakDown.DoBreakDown` **Prefix+Postfix** (forges/ammo benches cannot be taken apart; yield units scaled by
carpentry skill, tool and the piece's build quality for the duration of vanilla's call, restored afterwards; recovered hardware
and carpentry XP); `DecorationItem.GetCraftingDisplayName` **Postfix** ("[rough build]").

**Model.** Forges and ammo benches cannot be crafted (crafting gate). Furniture costs **hardware** (nails/hinges/brackets) on
top of vanilla materials: salvaged from structures (skill and prying tool decide how many survive intact) or forged from scrap.
Plank-processing recipes (Softwood/Hardwood -> `ReclaimedWoodB`) are added through the game's own
`BlueprintManager.LoadUserBlueprint` loader; yield depends on carpentry and the saw. Build quality is recorded per piece.

**State.** `hardware`, `builds{guid->[quality,day]}` -> `building`.

## 8. Interactive smithing / tanning / tailoring  (`TLDOverhaul.Interactive`)

**TLD now.** Crafting is a progress bar. **Approach.** Vanilla flow is untouched (menu, materials, preparation); on
completion (`CraftedHooks`) a hands-on IMGUI session decides quality. No new Harmony patches (it uses the crafting hooks).

**Sessions.** Smithing: heat to colour (bellows/pull), strike with timing + placement, quench. Tanning: scrape (rhythm),
stretch (tracking), smoke (window). Tailoring: cut (tracking), stitch (rhythm). Skill widens windows and sharpens cues; it never
blocks an attempt. Score -> product condition; a botched session ruins the item and refunds part of the materials. The building
system's hardware forging runs through the same smithing session (`BuildingSystem.InteractiveForge`).

**State.** None persistent (sessions are transient).

## 9. Weapons maintenance and ammo  (`TLDOverhaul.Weapons`)

**TLD now.** Rifle/revolver/flare gun (`GunItem`: `m_IsJammed`, `SetJammed`, misfire table, `RegisterOnFiredAction`), bow with
two arrow types (`WeaponSource.Arrow` / `HardenedArrow`), a cleaning kit. No fouling, no mods, no negligent discharge.

**Patches.** `ArrowItem.InflictDamage` **Prefix** (arrow type and bow mods vs the animal hit); `BaseAi.ProcessGunshotAudioEvent`
**Prefix** (suppressed shots mostly unheard); `GunItem.PressReloadAmmo` **Prefix** (no mixed clips). Shots are observed through
the **vanilla `GunItem.RegisterOnFiredAction`** event rather than a patch.

**Model.** Fouling per shot -> jams and accuracy loss *before* usability fails. Cleaning needs the kit, takes time, and has
the doc's negligent-discharge ladder (Rifle/Revolver tier I-II: no thought, real fatal risk; III: auto-clears with a message; IV+:
clears by habit). Mods per the design table (scope, light stock, recoil pad, forged receiver, suppressor, extended cylinder,
improved limbs, reinforced string) are installed at a workbench with a toolkit and skill, quality scales with skill, removal can
destroy them. Ammo: FMJ / hollow point / soft point multipliers vs rabbit, wolf, stag, moose, bear, cougar; hollow points are
swaged at the ammo bench (needs the schematic found by exploration); soft points are find-only. Broadhead = `Arrow`, field point =
`HardenedArrow`.

**State.** per weapon: fouling, shots, loaded ammo kind, suppressor life, installed mods; mod stock; special-ammo stock -> `weapons`.

## 10. Vehicles  (`TLDOverhaul.Vehicles`)

**TLD now.** Vehicles are static, enterable wrecks (`VehicleDoor` with a built-in bed; `PlayerInVehicle`). There is **no driving
model**.

**Patches.** `PlayerInVehicle.GetTempIncrease` **Postfix** (a running engine heats the cab). Everything else is a station-menu
provider on the `VehicleDoor` locator.

**Model.** Each wreck is a dead machine: six components with their own condition, battery charge, fuel (quantity + quality that
decays, stabilizer slows it), diagnosed by Mechanics tier, repaired with salvaged parts / scrap / the right toolkit
(advanced repairs need the specialized tool kit), stripped for parts, started against the cold (a cold-soaked engine at -40 will
not catch), burning fuel and engine life while running, and loud: predators in earshot may investigate.
**Driving and region-spanning transport are not implemented**: they need vehicle physics and new transfer zones.

**State.** per vehicle (stable id from the wreck's GUIDs): components, charge, fuel, quality, running, stripped; parts stock,
carried fuel -> `vehicles`.

## 11. Skiing / traversal  (`TLDOverhaul.Skiing`)

**TLD now.** Ski boots are heavy, warm and slow; `PlayerMovement` multiplies speed by snow depth, wind, limp, etc.

**Patches.** `PlayerMovement.GetSnowDepthMovementMultiplier` **Postfix** (the ski factor replaces the wading penalty on snow and
drags on bare ground); `PlayerClimbRope.BeginClimbing`, `RopeClimbPoint.PerformInteraction` **Prefix** (no climbing on skis);
`Fatigue.AddFatigue` **Prefix** (skill-scaled stamina drain on skis). Combat penalty via `WeaponsSystem.SwayScale`.

**Model.** Cross-country skis: large bonus on flat snow/ice, worse than walking on bare ground, slow uphill, fast downhill;
ski boots keep the full bonus. Touring skis add skins (climb mode). Skis come off automatically indoors, take real time to
put on/off (TLD's own progress bar), and low-level skiers fall (fatigue spike, sprain chance). The XC->touring jump is
skill-gated (Skiing tier II). TLD has no ski item: they are overhaul stand-ins granted by exploration or the debug menu.

**State.** `owns xc/at, equipped, skins, metres` -> `skiing`.

## 12. Exploration pull  (`TLDOverhaul.Exploration`)

**TLD now.** Gear is pre-placed; there is no region-specific material or capability.

**Patches.** `Container.InstantiateContents` **Postfix** (slip one-time finds into freshly stocked containers);
`Inventory.AddGear` **Postfix** (convert stand-in items on pickup); `GearItem.GetBasicDisplayNameForInventoryInterfaces`
**Postfix** (show the stand-in's real name); `Panel_BodyHarvest.HarvestSuccessful` **Prefix+Postfix** (hide quality by climate).

**Model.** Mostly data (`ExplorationSystem` tables + `exploration.json`): book volumes per region; schematics (hollow-point,
expedition parka, snow pants, surgical manual); unique finds (XC skis, AT skis, the dam's specialized tool kit, crampons);
regional salvage materials (dam copper/steel/electrical, crash-site aluminium/cable/fasteners, farm cast iron/chain/parts,
mountaineering hardware) required by specific recipes (forged receiver, suppressor, major engine/electrical repair). Placement
is deterministic per container GUID and happens once, with a pity timer so each item does turn up if you search the region.
Forge specialization is implemented in the Forge system and surfaced here.

**State.** materials, placed keys, schematics, containers seen/processed, stand-in registry -> `exploration`.
