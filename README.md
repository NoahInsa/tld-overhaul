# TLD Overhaul

A single MelonLoader mod that layers Project Zomboid's simulation systems under The Long Dark. Twelve systems, built in
dependency order: degradation, nutrition, clothing, skills + book-gating, crafting, forge recycling, building, interactive
smithing/tanning/tailoring, weapons maintenance + ammo, vehicles, skiing, exploration pull.

* Design spec: *TLD x PZ Systems Integration* (not in this repo).
* What each system does, what it patches and why, what it saves: [`docs/SYSTEMS.md`](docs/SYSTEMS.md)
* How to test it, hotkeys, known limits: [`docs/PLAYTEST.md`](docs/PLAYTEST.md)

## Build

```
dotnet build -c Release
```

Output: `bin/Release/net6.0/TLDOverhaul.dll` -> copy into `<TheLongDark>/Mods/`.
The game path defaults to the default Steam location; override with `-p:GameDir="D:\Games\TheLongDark"`.
`-p:DeployToGame=true` copies the DLL into `Mods/` after the build.

Requires MelonLoader 0.7.x with the Il2Cpp assemblies already generated (launch the game once).

## Checks that need no game

```
python tools/check_patch_targets.py            # target exists / not ambiguous / CallerCount (inlining risk)
dotnet build tools/PatchValidator -c Release
dotnet tools/PatchValidator/bin/Release/net8.0/PatchValidator.dll bin/Release/net6.0/TLDOverhaul.dll "<GameDir>"
```

The validator loads the mod and the game's interop assemblies as metadata and verifies every `[HarmonyPatch]` target and every
Prefix/Postfix parameter by name and type, exactly the checks Harmony makes at load time.

## Layout

```
src/Core          config, patch logging/runner, consolidated JSON sidecar, service interfaces, station menu, debug menu
src/<System>      one namespace per system (Degradation, Nutrition, Clothing, Skills, Crafting, Forge, Building,
                  Interactive, Weapons, Vehicles, Skiing, Exploration)
tools/            patch target checker + metadata-only patch validator
docs/             system reference, playtest guide
```
