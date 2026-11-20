"""Verify a saved Nagoya map in a running CS1 map editor and request evidence screenshots."""
import argparse
import hashlib
import json
import math
from pathlib import Path

from nagoya_map import ROOT, call, read_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("map_name")
    args = parser.parse_args()
    name = args.map_name
    if not name.startswith("Nagoya-Real-") or not name.replace("-", "").isalnum():
        parser.error("Expected the unique Nagoya-Real- map name.")

    placed = read_json(ROOT / f"{name}-readback.json")
    saved = Path(placed["saveRequested"]["path"])
    package = ROOT / f"{name}.crp"
    if package.read_bytes() != saved.read_bytes():
        raise RuntimeError("The original and copied native map packages differ.")

    samples = {}
    for site, expected in placed["terrainSamples"].items():
        actual = call("/state/nagoya-terrain", {"x": expected["x"], "z": expected["z"]})
        if actual["mode"] != "LoadMap" or actual["sourceSha256"] != placed["sourceHash"]:
            raise RuntimeError("The saved map's native height grid is not loaded.")
        if abs(actual["ground"] - expected["actualGameHeight"]) > 0.05:
            raise RuntimeError(f"Reloaded terrain height differs at {site}.")
        samples[site] = actual["ground"]

    networks = call("/state/networks?limit=200")
    roads = {row["id"]: row for row in networks["segments"]}
    old_roads = {row["id"]: row for row in placed["networkReadback"]["segments"]}
    for road_id in placed["roads"]:
        current = roads.get(road_id)
        former = old_roads[road_id]
        if current is None or current["prefab"] != former["prefab"]:
            raise RuntimeError(f"Road {road_id} is missing from the reloaded map.")
        if (current["start"] != former["start"] or current["end"] != former["end"]):
            raise RuntimeError(f"Road {road_id} has different endpoints.")

    buildings = call("/state/growables?limit=100")["growables"]
    by_id = {row["id"]: row for row in buildings}
    for entry in placed["buildings"]:
        placement = entry["placement"]
        building_id = placement["buildingId"]
        actual = by_id.get(building_id)
        if actual is None or actual["prefab"] != placement["buildingPrefab"]:
            raise RuntimeError(f"Building {building_id} is absent from the reloaded map.")
        source = entry["actualPosition"]
        if math.dist([source[k] for k in ("x", "y", "z")],
                     [actual["position"][k] for k in ("x", "y", "z")]) > 0.05:
            raise RuntimeError(f"Building {building_id} moved during reloading.")

    props = []
    for entry in placed["props"]:
        prop = call(f"/state/nagoya-prop?id={entry['id']}")
        if prop["prefab"] != entry["prefab"] or any(abs(prop[k] - entry[k]) > 0.05 for k in ("x", "y", "z")):
            raise RuntimeError(f"Prop {entry['id']} differs from the placed instance.")
        props.append(prop)

    zones = call("/state/zones")
    if zones["zonedCells"] < sum(row["placement"]["assignedCells"] for row in placed["buildings"]):
        raise RuntimeError("Native zones on the building footprints were lost.")
    proof = {"map": str(package), "packageSha256": hashlib.sha256(package.read_bytes()).hexdigest(),
             "terrainGameHeights": samples, "roadSegments": len(placed["roads"]),
             "dedicatedBuildingIds": [e["placement"]["buildingId"] for e in placed["buildings"]],
             "nativeProps": props, "zonedCells": zones["zonedCells"]}
    evidence = ROOT / f"{name}-reload-verified.json"
    evidence.write_text(json.dumps(proof, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(proof, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
