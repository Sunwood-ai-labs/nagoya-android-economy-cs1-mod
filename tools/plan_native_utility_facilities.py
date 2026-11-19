"""Select dry-land utility plant sites beside the live Nagoya street plan."""

import argparse
import json
import math
from collections import defaultdict
from pathlib import Path

from plan_native_city_utilities import point_segment


FACILITIES = [
    ("generation", "Nuclear Power Plant", 15, 9, (-1150, -1650)),
    ("water_north", "Large Water Tower 01", 3, 3, (100, 2900)),
    ("water_south", "Large Water Tower 01", 3, 3, (-850, -3000)),
    ("water_east", "Large Water Tower 01", 3, 3, (3000, 800)),
    ("water_west", "Large Water Tower 01", 3, 3, (-3150, 300)),
    ("sewage_north", "Eco Advanced Inland Water Treatment Plant 01", 10, 9, (1400, 2700)),
    ("sewage_south", "Eco Advanced Inland Water Treatment Plant 01", 10, 9, (-800, -3400)),
]


def index(edges, distance=160):
    grid = defaultdict(list)
    for a, b in edges:
        for x in range(math.floor((min(a[0], b[0])-distance)/128), math.floor((max(a[0], b[0])+distance)/128)+1):
            for z in range(math.floor((min(a[1], b[1])-distance)/128), math.floor((max(a[1], b[1])+distance)/128)+1):
                grid[x, z].append((a, b))
    return grid


def closest(point, grid):
    square = math.floor(point[0]/128), math.floor(point[1]/128)
    return min((point_segment(point, a, b) for a, b in grid.get(square, ())), default=math.inf)


def networks(path):
    points, segments = {}, []
    for line in path.read_text(encoding="utf-8").splitlines():
        kind, *v = line.split("\t")
        if kind == "N":
            points[v[0]] = float(v[1]), float(v[2])
        else:
            segments.append((points[v[1]], points[v[2]], v[3]))
    return segments


def make_sites(roads, utilities, existing):
    occupied = [
        ((row["position"]["x"], row["position"]["z"]), 4 * math.hypot(row["width"], row["length"]))
        for row in existing["growables"]
    ]
    road_segments = networks(roads)
    crossing_roads = index([(a, b) for a, b, _ in road_segments])
    lines = index([(a, b) for a, b, tag in networks(utilities) if tag == "water"])
    sites = []
    for identifier, prefab, width, depth, target in FACILITIES:
        needed = 4 * math.hypot(width, depth)
        candidates = []
        for a, b, tag in road_segments:
            length = math.dist(a, b)
            if tag not in ("basic", "arterial") or length < 32:
                continue
            px, pz = (a[0]+b[0])/2, (a[1]+b[1])/2
            if math.dist((px, pz), target) > 1800:
                continue
            dx, dz = (b[0]-a[0])/length, (b[1]-a[1])/length
            for side in (-1, 1):
                nx, nz = dz * side, -dx * side
                offset = 4 * depth + (19 if tag == "arterial" else 11)
                point = (px + nx * offset, pz + nz * offset)
                if max(map(abs, point)) >= 4670 or closest(point, crossing_roads) < offset - 4:
                    continue
                network_distance = closest(point, lines)
                if network_distance > 4 * depth + 45:
                    continue
                separation = min(math.dist(point, location) - radius for location, radius in occupied)
                if separation < needed + 5:
                    continue
                # CS1's front is (-sin(angle), +cos(angle)).
                angle = math.degrees(math.atan2(nx, -nz)) % 360
                candidates.append((math.dist(target, point), point, angle, network_distance, separation))
        if not candidates:
            raise ValueError(f"Could not find unobstructed native frontage for {prefab} near {target}")
        _, point, angle, distance, separation = min(candidates)
        occupied.append((point, needed))
        sites.append({
            "facility":identifier, "buildingPrefab":prefab, "angleDegrees":round(angle, 3),
            "position":{"x":round(point[0], 3), "z":round(point[1], 3)},
            "nearestUtilityCenterlineM":round(distance, 3), "nearestOccupiedFootprintBoundM":round(separation, 3),
        })
    return sites


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--roads", type=Path, required=True)
    parser.add_argument("--utilities", type=Path, required=True)
    parser.add_argument("--native-buildings", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    existing = json.loads(args.native_buildings.read_text(encoding="utf-8-sig"))
    result = make_sites(args.roads, args.utilities, existing)
    args.output.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps({"facilityCandidates":len(result), "types":[row["buildingPrefab"] for row in result]}))


if __name__ == "__main__":
    main()
