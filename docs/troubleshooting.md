# Troubleshooting

Use the smallest check that matches the failure. A successful compile only proves that the source references resolved; it does not prove that a game save, external data root, or live map is compatible.

## Build failures

### “The .NET Framework 3.5 x64 C# compiler is required.”

`build.ps1` looks for `csc.exe` in the standard .NET Framework 3.5 x64 location. Enable/install the required Windows framework component, then rerun the command from the repository root. Do not replace it with an arbitrary compiler without checking the old CS1 API/reference compatibility.

### A game assembly or Harmony reference cannot be resolved

Confirm each path is a file and belongs to the same Cities: Skylines installation:

```powershell
Test-Path "$game\Cities_Data\Managed\ICities.dll"
Test-Path "$game\Cities_Data\Managed\Assembly-CSharp.dll"
Test-Path $harmony
```

Pass the actual Harmony file with `-HarmonyAssembly`. The repository does not download proprietary game assemblies or CitiesHarmony.

### The build succeeds but the game does not load the MOD

Check that the file copied to `%LOCALAPPDATA%\Colossal Order\Cities_Skylines\Addons\Mods\NagoyaAndroidDemo\` is the newly built `.build/NagoyaAndroidDemo.dll`. Close the game before replacing it, enable CitiesHarmony first, and inspect the game log for the first load exception. A DLL compile result is not a runtime compatibility test.

## Data-root and configuration failures

### The MOD reports that a data file is missing

The game reads external data. Check the process-start values and expected files:

```powershell
$env:NAGOYA_DATA_ROOT
$env:NAGOYA_ANDROID_DEMO_ROOT
Test-Path (Join-Path $env:NAGOYA_DATA_ROOT 'citywide-live-v1')
```

`NAGOYA_DATA_ROOT` must be an absolute `nagoya-station` root. `NAGOYA_ANDROID_DEMO_ROOT` is a separate root for the legacy waypoint demo. If a variable is unset, the historical defaults are `H:\CS1-PLATEAU\nagoya-station` and `H:\CS1-PLATEAU\android`. Paths are resolved once when the game starts; restart the game after changing them.

Do not “fix” a missing input by pointing the MOD at a similarly named old experiment. The graph, region manifest, asset manifests, command files, and save must belong to the same intended run.

Run `./test-paths.ps1` to check the root resolver independently of the game. It covers the historical fallback, an absolute configured root, and rejected relative/UNC/whitespace-prefixed values.

### The native construction branch does not run

The load entry point selects the native construction branch only when the city name starts with `NagoyaCitywide-`. Confirm the actual loaded city/save name in the game and in the generated state/read-back files. A `NagoyaCitywide-` prefix still does not supply the external road/zone commands or a compatible save.

### The scene is visible, but the result is not a native city simulation

Read [Architecture](architecture.md). `CitywideScene`/`CitywideSimulation` own an independent route and parcel model, and `AndroidDemo` is a waypoint smoke test. A visible actor or road is not evidence of a native citizen path, native lot, or native demand change.

## Offline test failures

### `./test.ps1` cannot find graph or region files

The default command is intended to use the repository's synthetic fixture. If you are running an older checkout or deliberately using production data, pass both inputs explicitly:

```powershell
./test.ps1 `
  -GraphPath 'D:\datasets\nagoya\routing.ncg' `
  -RegionsPath 'D:\datasets\nagoya\regions.json'
```

Do not mix a graph from one data release with a region manifest from another. Keep production paths explicit in scripts and CI.

### The offline test fails a conservation or charger assertion

This is a code/data invariant, not proof that the game run failed. Preserve the first failure output, note the graph/region paths, and rerun once with the same inputs. If the failure is reproducible, open an issue with the command, commit, and sanitized fixture metadata; do not attach a save or private external dataset.

## Live verification failures

Start by checking the command contract:

```powershell
uv run --python 3.11 tools/verify_live.py --help
```

The verifier expects a fresh `citywide-live-v1/state.json` from a running compatible game state. Typical causes are:

- **Stale game state:** stop and restart the intended game run, then wait for a new state timestamp.
- **Wrong root:** pass the exact external root that owns `citywide-live-v1`; do not use the repository root as a substitute.
- **Wrong implementation branch:** the verifier checks the route-owned citywide state and its invariants; a waypoint demo or native construction save is a different path.
- **Bridge unavailable:** native transport checks are optional only when the command allows it. A missing bridge must be reported as an unverified native-transport observation, not silently replaced by a screenshot.
- **Report already exists:** choose a new output path rather than overwriting an earlier observation.

If the state is fresh but an assertion fails, preserve the exact report context and classify the result as unverified until the intended save/data/code combination is checked. Do not turn an API response or static JSON file into a claim of live gameplay.

## Map and real-terrain expectations

The real-terrain pages describe a separate map-building and launcher/bridge workflow. External meshes, large datasets, saves, and other loaders are not packaged as a one-click map. An optional bridge source, if present, is not a substitute for those dependencies. See [Data and licenses](data-and-licenses.md) before obtaining or redistributing source data.
