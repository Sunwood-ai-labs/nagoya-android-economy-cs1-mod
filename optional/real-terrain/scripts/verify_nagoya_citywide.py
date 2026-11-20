"""Read back the saved citywide map, zoning and street furniture from CS1."""
import argparse
from collections import Counter
import hashlib
import json
import math
from pathlib import Path

from nagoya_map import ROOT, SOURCE, call, read_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("map_name")
    args = parser.parse_args()
    name = args.map_name
    if not name.startswith("Nagoya-Real-Citywide-") or not name.replace("-", "").isalnum():
        parser.error("Use the exact name of a citywide package.")
    build = read_json(ROOT / f"{name}-build.json")
    package = Path(build["save"]["path"])
    saved_copy = ROOT / package.name
    data = package.read_bytes()
    if data[:4] != b"CRAP" or data != saved_copy.read_bytes():
        raise RuntimeError("Native citywide package verification failed.")

    terrain_points = read_json(SOURCE / "terrain-dem-probe.json")["samples"]
    heights = {}
    for label, point in terrain_points.items():
        row = call("/state/nagoya-terrain", {"x": point["x"], "z": point["z"]})
        if row["mode"] != "LoadMap" or row["sourceSha256"] != build["sourceSha256"]:
            raise RuntimeError("The saved citywide terrain is not loaded.")
        heights[label] = row["ground"]

    city = call("/state/summary")
    if city["network"]["segments"] != build["roads"]:
        raise RuntimeError("Road count changed after reload.")
    growables = call("/state/growables?limit=5000")
    buildings = growables["growables"]
    if growables["returned"] < growables["total"]:
        buildings = []
        for service in sorted(growables["countsByService"]):
            partial = call(f"/state/growables?limit=5000&service={service}")
            if partial["returned"] != partial["total"]:
                raise RuntimeError(f"The building response for {service} is incomplete.")
            buildings.extend(partial["growables"])
    ids = {row["id"]: row["prefab"] for row in buildings}
    present = [row for row in build["lots"] if ids.get(row["buildingId"]) == row["buildingPrefab"]]
    claimed = {row["buildingId"] for row in present}
    replacements = []
    lost = []
    for lot in build["lots"]:
        if lot["buildingId"] in claimed:
            continue
        matches = [row for row in buildings if row["id"] not in claimed and row["subService"] == lot["zone"]
                   and math.dist((row["position"]["x"], row["position"]["z"]), (lot["x"], lot["z"])) <= .1]
        if len(matches) != 1:
            lost.append(lot["buildingId"])
            continue
        actual = matches[0]
        claimed.add(actual["id"])
        present.append(lot)
        replacements.append({"site": lot["site"], "originalId": lot["buildingId"], "currentId": actual["id"],
                             "originalPrefab": lot["buildingPrefab"], "currentPrefab": actual["prefab"], "zone": lot["zone"]})
    props = []
    for item in build.get("props", []):
        actual = call(f"/state/nagoya-prop?id={item['id']}")
        if actual["prefab"] != item["prefab"]:
            raise RuntimeError(f"Prop {item['id']} was replaced.")
        props.append({**actual, "ward": item["ward"]})
    zoning = call("/state/zones")
    components = call("/state/external-connections?limit=10")
    evidence = {"map": str(saved_copy), "packageSha256": hashlib.sha256(data).hexdigest(),
                "terrainGameHeights": heights, "roads": city["network"]["segments"],
                "growableCount": growables["total"], "verifiedPlacedBuildings": len(present),
                "expectedPlacedBuildings": len(build["lots"]), "missingBuildingIds": lost,
                "sameSiteReplacements": replacements,
                "verifiedBuildingsByWard": dict(Counter(row["ward"] for row in present)),
                "props": len(props), "propWards": sorted({row["ward"] for row in props}),
                "zonedCells": zoning["zonedCells"], "roadComponents": components["roadComponents"],
                "outsideConnected": components["cityConnectedToOutside"]}
    (ROOT / f"{name}-reload-verified.json").write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n",
                                                      encoding="utf-8")
    print(json.dumps(evidence, ensure_ascii=False, indent=2))
    if lost:
        raise RuntimeError("The saved city's zoned buildings changed after reload.")


if __name__ == "__main__":
    main()
