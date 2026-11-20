"""Locate possible low-clearance crossings and save camera targets for the editor."""
from collections import Counter
import json

import numpy as np
from shapely.strtree import STRtree

from nagoya_citywide import ROOT, load_neighborhood_infill, prepare, terrain_height
from plan_nagoya_neighborhoods import road_shape
from smooth_citywide_roads import prepare_smooth_roads


def audit(infill):
    raw, roads, sites, _ = prepare(True, True)
    if infill:
        load_neighborhood_infill(raw, roads, sites)
    roads, _ = prepare_smooth_roads(roads, raw, True)
    grid = np.frombuffer(raw, dtype="<u2").reshape(1081, 1081) / 64

    def ground(point):
        return terrain_height(grid, *point, True)

    shapes = [road_shape(road, ground) for road in roads]
    lines = [row[0] for row in shapes]
    pairs = STRtree(lines).query(lines, predicate="intersects").T
    findings = []
    for a, b in pairs:
        if a >= b or shapes[a][1] & shapes[b][1]:
            continue
        intersection = lines[a].intersection(lines[b])
        if intersection.geom_type != "Point":
            continue
        along_a = lines[a].project(intersection, normalized=True)
        along_b = lines[b].project(intersection, normalized=True)
        if not (.03 <= along_a <= .97 and .03 <= along_b <= .97):
            continue
        ya = shapes[a][2] + (shapes[a][3] - shapes[a][2]) * along_a
        yb = shapes[b][2] + (shapes[b][3] - shapes[b][2]) * along_b
        if abs(ya - yb) >= 5:
            continue
        segments = [{key: roads[i][key] for key in ("roadPrefab", "plannedStart", "plannedEnd")} for i in (a, b)]
        findings.append({"x": round(intersection.x, 3), "z": round(intersection.y, 3),
                         "estimatedHeightDifferenceM": round(abs(ya - yb), 4), "segments": segments})
    counts = Counter(" / ".join(sorted(s["roadPrefab"] for s in f["segments"])) for f in findings)
    findings.sort(key=lambda f: (f["estimatedHeightDifferenceM"], f["x"], f["z"]))
    return {"roadSegments": len(roads), "candidatesByPrefab": dict(counts), "candidates": findings}


def main():
    previous, current = audit(False), audit(True)
    evidence = {"method": "offline straight chords, interior crossings, no common node, estimated height difference under 5 m",
                "streetPlan": previous, "neighborhoodPlan": current}
    out = ROOT / "roads" / "neighborhood-crossing-qa.json"
    out.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"before": len(previous["candidates"]), "after": len(current["candidates"]),
                      "sameCandidates": previous["candidates"] == current["candidates"], "output": str(out)}))


if __name__ == "__main__":
    main()
