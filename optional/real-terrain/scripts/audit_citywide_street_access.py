"""Estimate the distribution of building centroids around the native street plans."""
import argparse
import csv
import json
import math

import numpy as np
from scipy.spatial import cKDTree

from nagoya_citywide import SIDEWALK_WIDTH, load_neighborhood_infill, prepare, road_topology
from nagoya_map import ROOT
from prepare_refined_visuals import world_xz
from smooth_citywide_roads import prepare_smooth_roads


def sample_street_centers(roads):
    centers = []
    for street in roads:
        if street["roadPrefab"] not in SIDEWALK_WIDTH:
            continue
        a = street["start"]["x"], street["start"]["z"]
        b = street["end"]["x"], street["end"]["z"]
        step_count = max(1, math.ceil(math.dist(a, b) / 4))
        centers.extend(np.linspace(a, b, step_count + 1))
    return np.asarray(centers)


def summarize(distances):
    return {"PLATEAUSourceCentroids": len(distances), "nearbyWithin24m": int((distances <= 24).sum()),
            "medianCenterlineDistanceM": round(float(np.median(distances)), 2),
            "p90CenterlineDistanceM": round(float(np.quantile(distances, .9)), 2)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--include-infill", action="store_true")
    args = parser.parse_args()
    inventory = ROOT.parent / "citywide-zoning-v2" / "models-with-use-and-district.csv"
    with inventory.open(encoding="utf-8-sig", newline="") as source:
        rows = [(row["ward"], world_xz(row["x"], row["z"])) for row in csv.DictReader(source)]
    wards = np.array([ward for ward, _ in rows])
    coordinates = np.asarray([point for _, point in rows])
    result = {"status": "offline centerline distance, not proof of road access or native zoning"}
    plans = [("rampPlan", False), ("extendedPlan", True)]
    if args.include_infill:
        plans.append(("infillPlan", True))
    for label, extended in plans:
        raw, roads, sites, skipped = prepare(True, extended)
        if label == "infillPlan":
            load_neighborhood_infill(raw, roads, sites)
        roads, smoothing = prepare_smooth_roads(roads, raw, True)
        tree = cKDTree(sample_street_centers(roads))
        distances, _ = tree.query(coordinates, workers=2)
        result[label] = {"smoothing": smoothing, "topology": road_topology(roads), "zoningCandidates": len(sites),
                         "exclusions": dict(skipped), "unassigned": int((wards == "unassigned").sum()),
                         "allSourceCentroids": summarize(distances),
                         "wards": {str(ward): summarize(distances[wards == ward]) for ward in np.unique(wards)}}
    path = ROOT / "roads" / "citywide-street-access-preflight.json"
    path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({version: result[version]["allSourceCentroids"] for version, _ in plans}))
    print(path)


if __name__ == "__main__":
    main()
