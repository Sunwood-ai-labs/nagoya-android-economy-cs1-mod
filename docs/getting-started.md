# Getting started

This page covers the reproducible part of the repository: compiling the MOD and running its offline check. A complete Nagoya game run needs external data and a compatible save that are deliberately not included.

## Prerequisites

Install or locate the following on Windows:

- Cities: Skylines (2015), with its `Cities_Data/Managed` directory.
- A CitiesHarmony DLL compatible with the game and the source references.
- The .NET Framework 3.5 x64 C# compiler (`csc.exe`). `build.ps1` checks the standard Windows location.
- PowerShell and, for the live verifier, `uv` with a Python 3.11 runtime.
- External Nagoya data only if you intend to run the game or a production dataset. The repository does not download or package it.

The source currently expects the external `nagoya-station` data root and the separate legacy demo root. Set them before starting the game when your layout differs from the defaults:

```powershell
$env:NAGOYA_DATA_ROOT = 'D:\datasets\nagoya-station'
$env:NAGOYA_ANDROID_DEMO_ROOT = 'D:\datasets\android-demo'
```

The paths are resolved once at process startup. Restart the game after changing either variable. An unset variable keeps the historical defaults:

```text
NAGOYA_DATA_ROOT          H:\CS1-PLATEAU\nagoya-station
NAGOYA_ANDROID_DEMO_ROOT  H:\CS1-PLATEAU\android
```

## Build the DLL

From the repository root:

```powershell
$game = 'G:\SteamLibrary\steamapps\common\Cities_Skylines'
$data = if ($env:NAGOYA_DATA_ROOT) { $env:NAGOYA_DATA_ROOT } else { 'H:\CS1-PLATEAU\nagoya-station' }

./build.ps1 `
  -ManagedDirectory "$game\Cities_Data\Managed" `
  -HarmonyAssembly "$data\runtime\lib\CitiesHarmonyOfficial\CitiesHarmony\CitiesHarmony.Harmony.dll"
```

`build.ps1` discovers all `src/*.cs` files, sorts them by filename, and emits `.build/NagoyaAndroidDemo.dll`. At the documented source state there are 52 C# source files. The compiler references the four core game assemblies plus the supplied Harmony assembly.

If your Harmony DLL is elsewhere, pass that exact path. Do not copy a DLL from an unrelated game version just to satisfy the parameter: a successful compiler invocation does not establish runtime compatibility.

## Run the offline check

The default command uses the repository's synthetic fixture and does not require the external Nagoya dataset:

```powershell
./test.ps1
```

This compiles the fleet check into `.build/FleetCheck.exe` and exercises the routing/simulation invariants, including parcel conservation, regional actor ownership, charger capacity, and delivery/collection progress.

For a production dataset, pass both inputs explicitly:

```powershell
$service = 'H:\CS1-PLATEAU\nagoya-station\citywide-live-v1\building-service-v1'
./test.ps1 `
  -GraphPath "$service\routing.ncg" `
  -RegionsPath "$service\regions.json"
```

The production command is only meaningful when those files belong to the same dataset revision. Keep the graph, region manifest, and any report together; do not infer a live-game result from an offline pass.

The standalone path check covers legacy fallbacks, configured absolute roots, and invalid values without starting the game:

```powershell
./test-paths.ps1
```

## Build and install manually for a game run

1. Close the game and preserve a copy of any existing MOD DLL.
2. Run `build.ps1` and confirm `.build/NagoyaAndroidDemo.dll` exists.
3. Copy that DLL to `%LOCALAPPDATA%\Colossal Order\Cities_Skylines\Addons\Mods\NagoyaAndroidDemo\`.
4. Start the game with CitiesHarmony enabled, then enable **Nagoya Android Economy Demo**.
5. Load a compatible external save. A city name beginning with `NagoyaCitywide-` selects the native construction branch; other cities select the legacy/demo controllers where their external command files are present.
6. Close the game before replacing the DLL again.

This is a manual developer workflow. No save, map, or large asset bundle is provided, so a fresh installation will not automatically produce the Nagoya map described by the evidence notes. An optional real-terrain bridge source may exist separately, but it still requires the external data and other loaders used by that workflow.

## Inspect live-check options

Use `uv` to run the verifier in the same Python environment as the project tools:

```powershell
uv run --python 3.11 tools/verify_live.py --help
```

The verifier reads a fresh `citywide-live-v1/state.json`, checks the expected live-state invariants, waits for a bounded observation window, and writes a new report path. It may optionally query a running CS1 bridge for native transport data. It is intentionally stricter than a static file check; a stale or synthetic state file should fail rather than be presented as a live observation.

## Where to look next

- Read [Architecture](architecture.md) before interpreting a screenshot or state record.
- Read [Data and licenses](data-and-licenses.md) before collecting PLATEAU, OSM, GSI, or game assets.
- Read [Troubleshooting](troubleshooting.md) if paths, compiler references, or live-state checks fail.
