# TLD Overhaul

A single MelonLoader mod that layers Project Zomboid's simulation systems under The Long Dark.
Design spec: *TLD x PZ Systems Integration*. Build/implementation notes: see `docs/`.

## Build

```
dotnet build -c Release
```

Output: `bin/Release/net6.0/TLDOverhaul.dll` -> copy into `<TheLongDark>/Mods/`.
Game path defaults to the default Steam location; override with `-p:GameDir="D:\Games\TheLongDark"`.
Add `-p:DeployToGame=true` to copy the DLL into `Mods/` automatically.

Requires MelonLoader 0.7.x with the Il2Cpp assemblies already generated (launch the game once).
