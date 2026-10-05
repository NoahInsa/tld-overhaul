# Playtest guide

The mod has been compiled and statically validated against the game's v2.55 Il2Cpp assemblies (every Harmony target and every
Prefix/Postfix parameter resolved by name and type, see `tools/PatchValidator`). **It has not been run in the game.** Everything
below is what to check first.

## Install

Build (`dotnet build -c Release`) and copy `bin/Release/net6.0/TLDOverhaul.dll` to `<TheLongDark>/Mods/`. Back up your save first
(`um backup` or copy the save folder).

## Hotkeys

| Key | What |
|---|---|
| **F7** | Station menu (Up/Down, Enter, Esc). Near a **forge**: smelt junk, forge hardware. **Ammo bench**: swage hollow points, craft mods. **Workbench**: install/remove weapon mods, craft mods. **Vehicle wreck**: inspect, repair, fuel, start, strip. **Anywhere else** ("Field"): clean/clear/inspect weapons, load special rounds, put on/take off skis. |
| **F8** | Status overlay: what every system is tracking right now. |
| **F9** | Debug menu: give skill books and XP, scrap, parts, mods, skis, materials, schematics, try a mini-game. |
| **F10** | Patch status. Prints to the MelonLoader console which patches applied, which have fired, and which never have. |

## First five minutes: verify the patches loaded

1. Launch; the console should print `TLD Overhaul 0.1.0 starting` and one `[<System>] ready.` line per system.
   `Ready ... Patch failures: 0` is the number to look at.
2. Load a save and play normally. Each patch prints `[patch fired] <System>.<PatchClass>` the first time it runs. Walk, eat,
   swing a hatchet, open the crafting menu, repair something, fire a gun.
3. Press **F10**. Anything listed under `not fired yet` after you have exercised that feature is a candidate for an IL2CPP-inlined
   method (see the cc=0 list in `docs/SYSTEMS.md`). Send me the console log and I will move the hook.

## Per-system smoke tests

1. **Degradation** - F8 lists tools with grade/condition/performance. Swing a hatchet: wear should accelerate as it gets low.
   Repair something: HUD says the best possible condition dropped.
2. **Nutrition** - eat rabbit then deer; F8 shows protein/carb/lipid and weight moving. Eat only cattail for a few in-game days.
3. **Clothing** - F8 lists worn garments with warmth/wind multipliers. Harvest a carcass: gloves/coat get `[bloody]`.
4. **Skills** - F9 > "give next book" for Cooking, read it (vanilla read panel), then F8: ceiling rises. XP stops at the ceiling.
5. **Crafting** - open the crafting menu: recipes above your tier are hidden/greyed with a HUD reason; craft something and watch
   for the workmanship message.
6. **Forge** - F9 > give tin cans / worn hatchet; light a forge, F7 at it.
7. **Building** - F9 > add hardware; craft a furniture piece (needs hardware); break down a shelf (hardware recovered message).
8. **Interactive** - F9 > "Try smithing session" (Space = bellows/strike, Enter = pull). Real forge recipes trigger it after the bar.
9. **Weapons** - fire a rifle several times, F8 shows fouling; F7 anywhere > Clean. Try cleaning while loaded at low skill (this is
   the fatal path: test on a throwaway save).
10. **Vehicles** - F7 beside a parked car (any `VehicleDoor`). Add parts from F9, repair, siphon, start it in the cold.
11. **Skiing** - F9 > grant skis; F7 > put on skis; walk on snow (watch the console for `ground material tag seen`).
12. **Exploration** - open containers in a region; book/unique placement is logged as `[Exploration] placed ...`.

## Things I could not verify from the decompiled source (tune from the log)

* **Scene names -> regions** (`Core/GameUtil.cs`, `World.Map`): guessed from TLD naming. The log prints `scene '<name>' is not
  mapped` for any scene that misses.
* **Break-down object names** (`Forge/ForgePatches.cs`): the metal and wood keyword lists are guesses; every distinct object name
  you break down is logged once (`break-down object name: '...'`).
* **Ground material tags for skis**: logged as seen; adjust `SkiingSystem.Skiable` to the real tags.
* Units of `Repairable.GetRepairConditionCap` (percent vs fraction) are handled heuristically.
* All balance numbers are placeholders by design (MelonPreferences, per system).

## Known limits (deliberate, with reasons)

* **Vehicles do not drive** and there are no new region transfer zones: TLD has no vehicle controller and a code-only mod cannot
  create the physics or the transfer-zone assets.
* **No new item prefabs.** Skis, schematics, unique finds, special ammo and weapon mods are overhaul-owned ledger entries; where
  they must be "found", a vanilla item is placed as a stand-in and converted on pickup. Skill-book volumes use vanilla readable books
  as shells.
* **UI** is IMGUI (the station menu, mini-games, overlay) because TLD's NGUI panels cannot be extended from code.
* Not part of the requested build order and **not implemented**: wounds/healing, afflictions, first aid, cooking recipes,
  butchering, mental health, camping/sleeping pads, rappelling sites, weather prediction, propane, the Harbinger generator,
  greenhouse. (Hide quality by climate from the butchering/exploration sections *is* in.)
