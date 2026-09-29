# MarkAllSeen

A Risk of Rain 2 (BepInEx) mod that adds a **Mark all as seen** button to the Risk of Options settings: it clears the "New!" markers on your profile (logbook entries, and survivors, skills and skins in character select) in one click.

- Store page: [revoreverse/MarkAllSeen](https://thunderstore.io/c/riskofrain2/p/revoreverse/MarkAllSeen/). Its source, [thunderstore/MarkAllSeen/README.md](thunderstore/MarkAllSeen/README.md), describes what it does for players; this file is for development.
- [Modding guide](https://github.com/T-Jayay/DroneImprovements/blob/main/docs/MODDING_GUIDE.md) (in the DroneImprovements repo): how this mod, [DroneImprovements](https://github.com/T-Jayay/DroneImprovements) and [SprintImprovements](https://github.com/T-Jayay/SprintImprovements) are set up, built, tested and published.
- License: [the Unlicense](LICENSE) (public domain).

## Building

You need:
- the [.NET SDK](https://dotnet.microsoft.com/download) 8 or later;
- Risk of Rain 2;
- a mod manager profile (Thunderstore Mod Manager, r2modman, ...) with BepInExPack and Risk of Options installed. The build references BepInEx, Harmony and Risk of Options from it and deploys the plugin into it, so use a profile for testing.

```
dotnet build MarkAllSeen.sln -c Release
```

`Directory.Build.props` says where the game and the profile are. If yours are elsewhere, pass these properties on the command line or set them as environment variables:

| Property | Default | Example |
|---|---|---|
| `GameDir` | `C:\Program Files (x86)\Steam\steamapps\common\Risk of Rain 2` | `-p:GameDir="D:\SteamLibrary\steamapps\common\Risk of Rain 2"` |
| `ProfileDir` | Thunderstore Mod Manager's `Test` profile: `%APPDATA%\Thunderstore Mod Manager\DataFolder\RiskOfRain2\profiles\Test` | an r2modman profile named `Dev`: `-p:ProfileDir="C:\Users\<you>\AppData\Roaming\r2modmanPlus-local\RiskOfRain2\profiles\Dev"` |
| `DeployToProfile` | `true` | `-p:DeployToProfile=false` builds without deploying |

A path that doesn't match stops the build with an error naming the property to set. Each build copies `MarkAllSeen.dll` and its PDB into `<ProfileDir>\BepInEx\plugins\MarkAllSeen\`. Close the game before building: it keeps the plugin locked, and the deploy then fails with an error saying so. The build takes the version from `thunderstore/MarkAllSeen/manifest.json` and embeds `thunderstore/MarkAllSeen/icon.png` for the Risk of Options mod list.

## Releasing

1. Set the new version in `thunderstore/MarkAllSeen/manifest.json` (`version_number`) and in `PluginVersion` in `src/MarkAllSeen/MarkAllSeenPlugin.cs`, and list the changes under `## <version>` in `thunderstore/MarkAllSeen/CHANGELOG.md`. An uploaded version can't be changed, so any change, even to the manifest alone, needs a new version.
2. Commit.
3. `python tools/package.py` (Python 3.8 or later) checks the package against Thunderstore's rules and the version against `PluginVersion` and the changelog, builds the mod and writes `dist/MarkAllSeen-<version>.zip`. It refuses to package uncommitted changes or to overwrite an existing zip; `--force` skips both checks, for test builds only.
4. Upload the zip at https://thunderstore.io/package/create/ with the team **revoreverse** and the community Risk of Rain 2, and tick the **AI Generated** category.

The full checklist is under Publishing in the [modding guide](https://github.com/T-Jayay/DroneImprovements/blob/main/docs/MODDING_GUIDE.md#publishing).

## How it works

- Everything is in `src/MarkAllSeen/MarkAllSeenPlugin.cs`. Every "New!" marker in the game shows a node of the game's `ViewablesCatalog`, and each node decides whether it is new, so content unlocked later is still flagged. For each local player, the button marks the nodes showing "New!" as viewed and asks the game to save the profile.
- It leaves alone the names the profile can't save, and the entries of survivors the player can't play yet (locked, or from a DLC they don't own): the game counts skills without an unlock as unlocked, so those loadouts already report "new", and marking them would hide the markers the player should see once the survivor is available.
- It lists the nodes through `ViewablesCatalog`'s private `fullNameToNodeMap`. If a game update renames it, the button isn't added and an error is logged.
- Risk of Options is a hard dependency (`[BepInDependency]` and `Rune580-Risk_Of_Options` in the manifest), because the button is the whole mod.
- Client-side: it only changes the local players' profiles, so it isn't on the game's network mod list and other players don't need it.

## Testing

Marking can't be undone: test with a separate game profile (created on the title screen), not your main one.

## Icon

The store icon `thunderstore/MarkAllSeen/icon.png` has no generator script in this repo; edit the PNG directly and keep it 256x256.
