"""Prepare or apply the Nagoya DEM and station street plan through CS1's native bridge."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
from shutil import copyfile
from time import sleep
from urllib import error, request

import numpy as np
from PIL import Image

# Keep the historical in-place layout as the default, while allowing this
# source-only copy to use the existing external terrain data tree.
ROOT = Path(os.environ.get("NAGOYA_REAL_TERRAIN_DATA_ROOT", Path(__file__).resolve().parent)).expanduser().resolve()
SOURCE = ROOT.parent / "terrain-dem-probe-20261120"
RESOLUTION = 1081
GAME_URL = "http://127.0.0.1:32123"
PREFABS = {"basic": "Basic Road", "arterial": "Medium Road",
           "highway": "Highway", "bridge": "Basic Road Bridge"}
ROAD_HALF_WIDTH = {"Basic Road": 8, "Medium Road": 16, "Highway": 12, "Basic Road Bridge": 8}
ZONED_PREFABS = {
    "ResidentialHigh": ("H1 2x2 Tenement05", 8),
    "CommercialHigh": ("H1 2x2 Shop01", 8),
    "ResidentialLow": ("L1 1x1 Detached", 4),
}
JSON_HEADER = {"Content-Type": "application/json"}


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def call(path, payload=None):
    data = None if payload is None else json.dumps(payload, ensure_ascii=True).encode()
    command = request.Request(GAME_URL + path, data=data, headers=JSON_HEADER)
    try:
        with request.urlopen(command, timeout=12) as response:
            result = json.load(response)
    except error.HTTPError as exc:
        raise RuntimeError(f"{path}: HTTP {exc.code}: {exc.read(800).decode(errors='replace')}") from exc
    if not result.get("ok"):
        raise RuntimeError(f"{path}: {result.get('error', result.get('message', result))}")
    return result


def prepare():
    with Image.open(ROOT / "Nagoya-Real-Terrain-GSI-20261120.png") as png:
        image = np.asarray(png)
    if image.shape != (RESOLUTION, RESOLUTION) or not 0 < image.min() < image.max() <= 65535:
        raise ValueError("Expected a non-flat 1081 × 1081 unsigned 16-bit height map.")
    raw = image.astype("<u2").tobytes()
    raw_file = ROOT / "height-north-up.bin"
    if not raw_file.exists() or raw_file.read_bytes() != raw:
        raw_file.write_bytes(raw)
    meta = read_json(SOURCE / "terrain-dem-probe.json")
    samples = {name: {**point, "expectedGameHeight": meta["terrainGameBase"] + meta["scale"] * point["elevation"]}
               for name, point in meta["samples"].items()}

    compiled = ROOT / "roads" / "compiled"
    nodes = {}
    for line in (compiled / "native-road-nodes.tsv").read_text(encoding="utf-8-sig").splitlines():
        tag, node, x, z, height_above_ground = line.split("\t")
        if tag != "N" or node in nodes:
            raise ValueError("Invalid or duplicate native road node.")
        nodes[node] = ({"x": float(x), "z": float(z)}, float(height_above_ground))
    streets = []
    for line in (compiled / "native-road-segments.tsv").read_text(encoding="utf-8-sig").splitlines():
        tag, _, start, end, prefab, _, _ = line.split("\t")
        if tag != "E" or start == end:
            raise ValueError("Invalid native road segment.")
        (a, start_offset), (b, end_offset) = nodes[start], nodes[end]
        if not 4 <= math.hypot(a["x"] - b["x"], a["z"] - b["z"]) < 70:
            raise ValueError("Native road segment length outside the compiled plan.")
        streets.append({"type": "build-road", "start": a, "end": b, "roadPrefab": PREFABS[prefab],
                        "startHeightOffset": start_offset, "endHeightOffset": end_offset})
    buildings = prepare_matching_sites(streets)
    return raw, samples, streets, buildings


def closest_point(x, z, road):
    a, b = road["start"], road["end"]
    dx, dz = b["x"] - a["x"], b["z"] - a["z"]
    t = ((x - a["x"]) * dx + (z - a["z"]) * dz) / (dx * dx + dz * dz)
    t = min(1, max(0, t))
    return a["x"] + t * dx, a["z"] + t * dz


def prepare_matching_sites(streets):
    sites = ROOT / "buildings" / "native-building-batch-start20.tsv"
    buildings = []
    for line in sites.read_text(encoding="utf-8-sig").splitlines():
        tag, site, osm, x, z, zone, _, _ = line.split("\t")
        if tag != "S" or zone not in ZONED_PREFABS:
            continue
        x, z = float(x), float(z)
        prefab, half_footprint = ZONED_PREFABS[zone]
        road, street_x, street_z = min(
            ((road, *closest_point(x, z, road)) for road in streets),
            key=lambda row: math.hypot(x - row[1], z - row[2]),
        )
        distance = math.hypot(x - street_x, z - street_z)
        if not 5 <= distance <= 28 or road["roadPrefab"] in {"Highway", "Basic Road Bridge"}:
            continue
        setback = ROAD_HALF_WIDTH[road["roadPrefab"]] + half_footprint + 1
        x = street_x + (x - street_x) * setback / distance
        z = street_z + (z - street_z) * setback / distance
        if any(math.hypot(x - p["projectedX"], z - p["projectedZ"])
               < half_footprint + p["halfFootprint"] + 4 for p in buildings):
            continue
        if any(math.dist((x, z), closest_point(x, z, other))
               < ROAD_HALF_WIDTH[other["roadPrefab"]] + half_footprint
               for other in streets):
            continue
        angle = math.degrees(math.atan2(street_x - x, street_z - z)) % 360
        buildings.append({"source": site, "osmFrontage": osm, "prefab": prefab, "zone": zone,
                          "projectedX": x, "projectedZ": z, "angle": angle,
                          "halfFootprint": half_footprint})
    if not buildings:
        raise RuntimeError("No building sites are clear of the native street footprints.")
    return buildings


def check_empty():
    state = call("/state/summary")
    if state["buildIndex"] or state["network"]["segments"] or state["citizens"]["count"]:
        raise RuntimeError("The active map is not empty. Start the dedicated project map job.")


def apply(raw, samples, streets, buildings, name, props):
    check_empty()
    source_hash = hashlib.sha256(raw).hexdigest()
    call("/commands/nagoya-import-terrain", {"sha256": source_hash})
    native = np.fromfile(ROOT / "native-heights-south-up.bin", dtype="<u2").reshape((RESOLUTION, RESOLUTION))
    if native[::-1].astype("<u2").tobytes() != raw:
        raise RuntimeError("The game's actual terrain grid differs from the source.")
    report = {"name": name, "sourceHash": source_hash, "terrainSamples": {}, "roads": [], "buildings": [], "props": []}
    evidence = ROOT / f"{name}-readback.json"

    def record():
        evidence.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    for label, point in samples.items():
        reply = call("/state/nagoya-terrain", {"x": point["x"], "z": point["z"]})
        report["terrainSamples"][label] = {**point, "actualGameHeight": reply["ground"]}
        if abs(reply["ground"] - point["expectedGameHeight"]) > 1.5:
            record()
            raise RuntimeError(f"Game terrain differs at {label}.")
    record()

    for i in range(0, len(streets), 32):
        batch = streets[i:i + 32]
        call("/commands/nagoya-build-batch", {"commands": batch, "dryRun": True, "stopOnError": True})
    for i in range(0, len(streets), 32):
        batch = streets[i:i + 32]
        built = call("/commands/nagoya-build-batch", {"commands": batch, "dryRun": False, "stopOnError": True})
        report["roads"].extend(item["result"]["segmentId"] for item in built["results"])
        record()

    report["unplacedLots"] = []
    for row in buildings:
        body = {"buildingPrefab": row["prefab"], "x": row["projectedX"], "z": row["projectedZ"],
                "zone": row["zone"], "dryRun": True}
        try:
            call("/commands/nagoya-place-zoned-building", body)
        except RuntimeError as failure:
            if "No free road-facing zone cells" not in str(failure):
                raise
            report["unplacedLots"].append({"source": row["source"], "reason": str(failure)})
            record()
            continue
        body["dryRun"] = False
        result = call("/commands/nagoya-place-zoned-building", body)
        report["buildings"].append({"source": row["source"], "placement": result})
        record()

    for prop in props:
        call("/commands/nagoya-place-prop", {**prop, "dryRun": True})
        report["props"].append(call("/commands/nagoya-place-prop", prop))
        record()

    network = call("/state/networks?limit=200")
    actual = {row["id"]: row for row in network["segments"]}
    if not set(report["roads"]) <= actual.keys():
        raise RuntimeError("Road placement differs from the game's readback.")
    connections = {}
    for road_id in report["roads"]:
        segment = actual[road_id]
        start, end = segment["startNodeId"], segment["endNodeId"]
        connections.setdefault(start, set()).add(end)
        connections.setdefault(end, set()).add(start)
    if connections:
        reached = set()
        frontier = {next(iter(connections))}
        for _ in connections:
            if not frontier:
                break
            reached.update(frontier)
            frontier = set().union(*(connections[node] for node in frontier)) - reached
        if reached != connections.keys():
            raise RuntimeError("The built road network contains disconnected components.")
    facilities = call("/state/facilities?includeMapObjects=true&limit=200")
    growables = call("/state/growables?limit=200")
    report["networkReadback"] = network
    report["buildingReadback"] = {"facilities": facilities, "growables": growables}
    actual_buildings = {row["id"]: row for row in facilities["facilities"] + growables["growables"]}
    source_by_site = {row["source"]: row for row in buildings}
    for placed in report["buildings"]:
        source = source_by_site[placed["source"]]
        building_id = placed["placement"]["buildingId"]
        actual = actual_buildings.get(building_id)
        if actual is None or actual["prefab"] != source["prefab"]:
            record()
            raise RuntimeError(f"Building {building_id} differs from the game's readback.")
        position = actual["position"]
        ground = call("/state/nagoya-terrain", {"x": position["x"], "z": position["z"]})["ground"]
        placed["actualPosition"] = position
        placed["ground"] = ground
        if math.hypot(position["x"] - source["projectedX"],
                      position["z"] - source["projectedZ"]) > 42 or abs(position["y"] - ground) > 1:
            record()
            raise RuntimeError(f"Building {building_id} is displaced from its planned ground position.")
    record()
    report["saveRequested"] = call("/commands/nagoya-save-map", {"name": name})
    record()
    game_map = Path(report["saveRequested"]["path"])
    for _ in range(2):
        sleep(3)
        if game_map.is_file() and game_map.stat().st_size > 128:
            with game_map.open("rb") as stream:
                if stream.read(4) == b"CRAP":
                    destination = ROOT / game_map.name
                    copyfile(game_map, destination)
                    if destination.read_bytes() == game_map.read_bytes():
                        report["savedPackage"] = str(destination)
                        record()
                        print(f"Native map package saved. Readback: {evidence}")
                        return
    raise RuntimeError("Native map save was requested, but its package could not be verified.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", metavar="Nagoya-Real-Name", help="write to the dedicated empty game map")
    parser.add_argument("--props", type=Path, help="optional JSON array of approved native prop placements")
    args = parser.parse_args()
    raw, samples, streets, buildings = prepare()
    plan = {"heightSha256": hashlib.sha256(raw).hexdigest(), "sites": samples,
            "nativeRoadSegments": len(streets), "buildingSites": len(buildings),
            "buildingAppearance": "standard game prefabs at PLATEAU-sourced locations"}
    print(json.dumps(plan, ensure_ascii=False, indent=2))
    if args.apply:
        if not args.apply.startswith("Nagoya-Real-") or len(args.apply) > 60 or not args.apply.replace("-", "").isalnum():
            parser.error("--apply needs a unique Nagoya-Real-Name.")
        apply(raw, samples, streets, buildings, args.apply, read_json(args.props) if args.props else [])


if __name__ == "__main__":
    main()
