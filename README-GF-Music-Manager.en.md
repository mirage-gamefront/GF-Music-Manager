# GF Music Manager

[English](README.md) | [日本語](README.ja.md)

Version: `0.9.0-beta.1`

GF Music Manager is a Windows application that analyzes music mods for Skyrim Special Edition / Anniversary Edition installed in Mod Organizer 2 (MO2), organizes the tracks to keep and where they apply, and generates the management mod `GF Music Product`.

## Key Features

- Scan MO2's enabled mods and, optionally, disabled mods
- Analyze XWM audio files in loose files and BSA archives
- Display relationships among Music Type, Music Track, Location, Region, Cell, and WorldSpace
- Review path conflicts, identical content, and similar candidates, then choose which tracks to keep or exclude
- Preview XWM audio without conversion
- Generate Music Track, MTD, Cell-specific SkyPatcher settings, and optional WorldSpace overrides
- Load an existing `GF Music Product`, restore its editing state, and regenerate it
- Automatically diagnose ESP, MTD, JSON, and audio references before confirming generation
- Switch between the Japanese and English UIs from the settings screen

## Requirements

- Windows 10 or Windows 11 (64-bit)
- Skyrim Special Edition or Anniversary Edition
- Mod Organizer 2
- Music Type Distributor (when applying generated Music Type, Location, and Region settings in the game)
- SkyPatcher (when applying generated Cell settings in the game)

The distribution is available in the following 2 variants:

- `win-x64-self-contained`: Includes the .NET runtime. Use this version normally
- `win-x64-framework-dependent`: Lightweight version. .NET 8 Desktop Runtime x64 must be installed separately

If Music Type Distributor or SkyPatcher cannot be found, GF Music Manager displays a warning on the generation confirmation screen. Scanning and reviewing in the application can continue, but install the corresponding prerequisite mod to apply the generated settings in the game.

## Installation

1. Extract the ZIP to any folder
2. Place it in a regular application folder, not inside MO2's `mods` folder
3. Launch `GfMusicManager.exe` (do not move or delete the adjacent `dll` folder)
4. On the settings screen at the top, select and save the MO2 root and the profile to use

When updating, exit the application before replacing it with the new distribution files. The generated `GF Music Product` is stored on the MO2 side and is managed separately from the application itself.

## Basic Usage

1. In the settings screen, check the MO2 root, profile, and whether WorldSpace output is enabled
2. If necessary, enable “Include disabled mods” and scan
3. Review the audio list, application targets, warnings, and duplicate candidates
4. Preview tracks and adjust keep/exclude choices and Music Type assignments
5. Choose whether to retain the vanilla audio
6. On the generation confirmation screen, confirm whether to enable the generated ESP and whether to disable the original ESPs for the selected tracks
7. Run “Generate and deploy to MO2”
8. Check that diagnostics are OK, then verify the generated count and placement in the MO2 left and right panes

The output is always written to the following fixed folder:

```text
<MO2 root>\mods\GF Music Product
```

Depending on the configuration, the generated mod contains the following files:

- Generated plugins with the ESL flag, such as `GF Music Product.esp`
- MTD configuration files
- SkyPatcher configuration for Cells
- A `Music` folder containing audio files that lose conflicts
- `GFMusicProduct.json` for editing again

## Important Notes

- Files in the original mods are not modified
- The enabled/disabled state of an original ESP is changed only when you select that option on the confirmation screen before generation
- If an original ESP contains records unrelated to music, those records are not carried over to `GF Music Product`. Review its contents before disabling it
- Enabling WorldSpace records may cause conflicts with mods that modify the same WorldSpace
- Post-generation diagnostics check the integrity of the generated files, but do not guarantee playback in the game
- `GF Music Product` is placed at the bottom of the MO2 left pane. The right-pane order follows the load-order management used by tools such as LOOT

## Troubleshooting

### Scan or Generation Fails

Check that the MO2 root and profile are correct, that the target drive allows reading and writing, and that no other process has locked files used by GF Music Manager or MO2.

### Music Type or Cell Settings Are Not Applied

Check that Music Type Distributor and SkyPatcher are installed and enabled. Also check that the generated mod contains MTD settings and Cell-specific SkyPatcher settings.

### The Original Music Mod Still Plays

If you left the original ESP enabled on the generation confirmation screen, the Music Type, Track, Cell, Location, and Region settings on the original ESP may conflict with the generated settings. Check the MO2 right pane and the conflict display.

### The Generated Mod Has Priority 0

Under normal generation, `GF Music Product` is placed at the bottom of the MO2 left pane with the highest priority. If it is still at priority 0 after regeneration, report the issue with the latest log and the profile's `modlist.txt`.

## Logs

Log file output is OFF by default. When investigating a problem, turn on “Output log files” in the settings screen and reproduce the operation.

When enabled, logs are saved in the following folder:

```text
%LOCALAPPDATA%\GF Music Manager\logs
```

When reporting a problem, include the latest log corresponding to the time of occurrence, the name of the MO2 profile used, the operations performed, and any error displayed on the screen.

## Uninstallation

Delete the application's folder. If the generated mod is no longer needed, disable `GF Music Product` in MO2 first, then delete it through MO2.

## License and Source Code

GF Music Manager is distributed under `GPL-3.0-only`. See the bundled `LICENSE.txt` for the license text.

The source code corresponding to this binary is available in `GF-Music-Manager-v0.9.0-beta.1-source.zip`, which is distributed together with the binary.

See `Documentation\THIRD-PARTY-NOTICES.txt` and
`Documentation\LICENSES` in the binary ZIP for the libraries used and their respective licenses. In the source ZIP, they are included in
`THIRD-PARTY-NOTICES-GF-MUSIC-MANAGER.txt` and `licenses\GfMusicManager`.

### Building from Source

Install the .NET 8 SDK, then run the following commands from the root of the source ZIP.

```powershell
dotnet build .\src\GfMusicManager\Desktop\GfMusicManager.Desktop.csproj --configuration Release
dotnet test .\tests\GfMusicManager.Core.Tests\GfMusicManager.Core.Tests.csproj --configuration Release
dotnet test .\tests\GfMusicManager.Desktop.Tests\GfMusicManager.Desktop.Tests.csproj --configuration Release
dotnet test .\tests\SkyrimScan.Core.Tests\SkyrimScan.Core.Tests.csproj --configuration Release
```
