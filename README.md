# MarkAllSeen

Risk of Rain 2 (BepInEx) mod: adds a **Mark all as seen** button to the [Risk of Options](https://thunderstore.io/package/Rune580/Risk_Of_Options/) menu that clears every "New!" marker on your profile (logbook, items, skills, skins, survivors). Only entries currently showing as new are marked, so content unlocked later is still flagged.

## Building

Requires the .NET SDK (8.0 used). Game and Thunderstore profile paths are set in `Directory.Build.props`; override them on the command line if yours differ, e.g. `-p:GameDir="D:\Games\Risk of Rain 2"`.

```
dotnet build MarkAllSeen.sln -c Release
```

The build copies the plugin into the profile's `BepInEx/plugins/MarkAllSeen/` folder (skip with `-p:DeployToProfile=false`). Risk of Options must be installed in that profile, as the project references its DLL.

## Releasing to Thunderstore

1. Bump `version_number` in `thunderstore/MarkAllSeen/manifest.json` and the version in `[BepInPlugin(...)]` in `src/MarkAllSeen/MarkAllSeenPlugin.cs`, and add a changelog entry.
2. `python tools/package.py` → `dist/MarkAllSeen-<version>.zip` (needs Pillow).
3. Upload the zip at https://thunderstore.io/c/riskofrain2/create/.

`thunderstore/MarkAllSeen/README.md` is the player-facing store page; this file is for development.
