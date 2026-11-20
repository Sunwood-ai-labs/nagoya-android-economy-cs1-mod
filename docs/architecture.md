# Architecture

The project deliberately contains both native CS1 integration and MOD-owned experiments. They share a repository and sometimes a map, but they do not share the same simulation semantics. Use the implementation boundary below when interpreting a state file, screenshot, or historical result.

## Load-time branching

`src/AndroidDemo.cs` installs one host object when a level is loaded. The city name selects the major branch:

```text
OnLevelLoaded
├─ city name starts with NagoyaCitywide-
│  ├─ enter the native save ledger
│  ├─ attach native roster, finance, road, zoning, and water components
│  └─ attach CitywideScene with NativeConstruction = true
└─ any other city
   ├─ attach the small AndroidDemo waypoint loop
   ├─ attach district and human pilot controllers
   └─ attach CitywideScene with NativeConstruction = false
```

`NativeCityFlatStart`, `NativeCityRoadBuilder`, and `NativeCityZoneBuilder` are attached in both cases, but their native construction work is gated by the selected city/runtime state. The exact active branch should therefore be confirmed from the game city name and generated runtime state, not from the presence of the DLL alone.

## Component boundaries

| Component or group | Owns | Does not prove |
| --- | --- | --- |
| `AndroidDemo` | A small waypoint delivery/charging loop and its JSON/event output | Native citizen pathfinding, native demand, or native inventory transfers |
| `CitywideScene` | External graph loading, route-owned road/actor visuals, charger visuals, and state read-back | That its Androids are CS1 citizens or that its roads are native `NetSegment`s |
| `CitywideSimulation` and `CitywideRoutingCore` | Independent parcel, target, route, and fleet rules used by the citywide experiment | A change in native household, job, or economic buffers |
| `NativeCityRoadBuilder` / `NativeCityZoneBuilder` | Creation and audit of CS1 roads and zone/building placements in a named construction city | Successful placement of every external mesh or a one-click map installation |
| `NativeCityLedger` / `NativeCityRoster` | Save-backed associations and read-back of roads, lots, residents, jobs, and identities | That a ledger association is still an operational building after CS1 replaces or abandons it |
| `NativeAndroidDeliveries`, `NativeGoodsMonitor`, `NativeEmploymentTrial` | Hooks and trials that inspect or use CS1 buildings, residents, workplaces, requests, and inventory | A causal economic result unless the native request, arrival, and buffer changes are all read back |
| `PlateauZoning` | A bounded pilot that binds selected PLATEAU model IDs to compatible native building prefabs/zones | That an arbitrary PLATEAU mesh is itself a native lot or job provider |
| `TerrainSampleOnce`, `UrbanTerrainAppearance`, `SiteWalkTerrainMask` | Bounded terrain sampling, material appearance, and public-realm masks | A replacement terrain-height model or a complete real-terrain map |

Source links: [load entry point](https://github.com/Sunwood-ai-labs/nagoya-android-economy-cs1-mod/blob/main/src/AndroidDemo.cs), [citywide scene](https://github.com/Sunwood-ai-labs/nagoya-android-economy-cs1-mod/blob/main/src/CitywideScene.cs), [native road builder](https://github.com/Sunwood-ai-labs/nagoya-android-economy-cs1-mod/blob/main/src/NativeCityRoadBuilder.cs), [native ledger](https://github.com/Sunwood-ai-labs/nagoya-android-economy-cs1-mod/blob/main/src/NativeCityLedger.cs), and [PLATEAU zoning pilot](https://github.com/Sunwood-ai-labs/nagoya-android-economy-cs1-mod/blob/main/src/PlateauZoning.cs).

## Native and independent paths

### Native CS1 path

The native path calls into CS1 managers and data structures for roads, zoning, buildings, citizens, workplaces, utilities, and inventory. A save-backed ledger records associations that need to survive reloads. The roster and audit components read the game back after construction and exclude stale or non-operational entries from the relevant counts.

Native evidence should identify, at minimum, the exact city/save, the source or building IDs, the game-time window, and the read-back fields. A count of planned roads, a visual mesh, or a static JSON plan is not a native runtime result.

### MOD-owned route path

`CitywideScene` loads a graph and region/target data from the external `citywide-live-v1` family. `CitywideSimulation` owns parcel inventory, actor assignment, charging ownership, and movement rules. The scene can draw native human meshes for presentation, but the route and parcel model remain MOD-owned. The state output identifies implementation details, hashes selected input files, and records actor/parcel totals so an offline or live check can distinguish this path.

### Waypoint demo path

`AndroidDemo` creates three simple robot actors, advances them between pickup, delivery, and charging waypoints, and writes `events.jsonl` and `state.json` under the separate demo root. It is a smoke-test visualization and control path. It is intentionally not used as evidence of native citizen movement.

## Runtime data layout

The source reads external files; the repository does not contain the large inputs or a self-contained launcher that assembles them. An optional real-terrain bridge source may be kept under a separate package, but it still depends on external data and other loaders. Paths are resolved once at process startup:

| Logical area | Default location | Typical contents |
| --- | --- | --- |
| Main Nagoya root | `H:\CS1-PLATEAU\nagoya-station` | External assets, maps, simulation controls, and generated runtime state |
| Citywide scene | `...\citywide-live-v1` | Network/region manifests, routing graph, `state.json`, and building-service inputs |
| Native construction | `...\citywide-zoning-v3` | Road/zone commands, finance and employment requests, and native read-back files |
| Visual/terrain pilots | `...\simulation`, `...\publicrealm-v1` | Material, mask, and terrain-sampling controls |
| Legacy waypoint demo | `H:\CS1-PLATEAU\android` | `demo-command.ini`, `state.json`, and `events.jsonl` |

Override the first and last rows with `NAGOYA_DATA_ROOT` and `NAGOYA_ANDROID_DEMO_ROOT`. Unset variables retain the defaults above. Restart the game after changing them.

## Build and verification boundaries

`build.ps1` compiles every `src/*.cs` file into `.build/NagoyaAndroidDemo.dll`; it does not package external data or a save. `test.ps1` exercises the offline fleet code with its synthetic fixture by default, or with explicit graph/region paths for a production dataset. `tools/verify_live.py` is a separate live-state read-back tool; its result depends on a fresh running game state and, optionally, a bridge endpoint.

At this source state, `src/` contains 52 C# files, `tests/` contains three C# checks, and `tools/` contains the planning/audit helpers. These counts describe repository contents, not a game-wide asset or building count.

## Reading the evidence correctly

The historical life/economy notes in [native-economy](native-economy.md), [android-life](android-life.md), and [citywide-zoning](citywide-zoning.md) describe named runs with external saves. The newer [real-terrain QA](real-terrain-road-building-qa.md) and [Nagoya Station records](nagoya-station-aerial-comparison.md) describe a separate launcher/bridge and visual-map workflow. Neither set should be collapsed into a claim that this source tree alone ships a complete Nagoya map or that every visible actor participates in the native economy.
