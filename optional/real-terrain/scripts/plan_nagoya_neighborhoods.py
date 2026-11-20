"""Select connected surface streets and building approaches from the unused Nagoya OSM graph."""
import argparse
from collections import Counter, defaultdict
import csv
from hashlib import sha256
from heapq import heappop, heappush
import json
import math

import numpy as np
from shapely.geometry import LineString, Point
from shapely.strtree import STRtree

from nagoya_citywide import BUILDINGS, INPUT, ROOT, prepare, road_plan_sha256, road_topology, terrain_height
from prepare_refined_visuals import world_xz
from smooth_citywide_roads import prepare_smooth_roads

SOURCE = ROOT.parent / "citywide-roads-v1" / "city-source-road-graph.json"
OUTPUT = ROOT / "roads" / "neighborhood-infill-plan.json"
LOCAL_CLASSES = {"living_street", "residential", "tertiary", "unclassified"}
MIN_LENGTH = 4.05
MAX_LENGTH = 64


def coordinates(point):
    return float(point["x"]), float(point["z"])


def street_points(edge):
    return [world_xz(*coordinates(point)) for point in edge["points"]]


def graph_anchors(roads, edges):
    """Reuse only the endpoint of a matching OSM street and a ground-level native node."""
    matches = {}
    for road in roads:
        if road["roadPrefab"] not in {"Basic Road", "Medium Road"}:
            continue
        edge = edges.get(road["osmSource"])
        if not edge:
            continue
        a, b = edge["points"][0], edge["points"][-1]
        original_ends = [(edge["fromNode"], world_xz(*coordinates(a))),
                         (edge["toNode"], world_xz(*coordinates(b)))]
        for tag in ("start", "end"):
            if abs(road[tag + "HeightOffset"]) > .2:
                continue
            location = coordinates(road[tag])
            node = road["planned" + tag.title()]
            for original_id, source_position in original_ends:
                distance = math.dist(location, source_position)
                if distance <= 1.25 and (original_id not in matches or distance < matches[original_id][2]):
                    matches[original_id] = (node, location, distance, road[tag + "HeightOffset"])
    return {key: (node, xy, dy) for key, (node, xy, _, dy) in matches.items()}


def fit_street(points, ground, start_offset, end_offset):
    simplified = list(LineString(points).simplify(.7).coords)
    fitted = [simplified[0]]
    for a, b in zip(simplified, simplified[1:]):
        n = max(1, math.ceil(math.dist(a, b) / MAX_LENGTH))
        fitted.extend(tuple((x * (n - k) + y * k) / n for x, y in zip(a, b)) for k in range(1, n + 1))
    adjusted = [ground(p) for p in fitted]
    adjusted[0] += start_offset
    adjusted[-1] += end_offset
    for i, (a, b) in enumerate(zip(fitted, fitted[1:])):
        length = math.dist(a, b)
        if length < MIN_LENGTH or abs(adjusted[i] - adjusted[i + 1]) > length * .14:
            return None
        if min(ground(a), ground(b)) <= 135:
            return None
    return fitted


def road_shape(road, ground):
    a, b = coordinates(road["start"]), coordinates(road["end"])
    return (LineString((a, b)), {road["plannedStart"], road["plannedEnd"]},
            ground(a) + road["startHeightOffset"], ground(b) + road["endHeightOffset"])


def conflict(line, endpoint_ids, other, ground):
    other_line, other_ids, ya, yb = other
    intersection = line.intersection(other_line)
    if intersection.is_empty:
        return False
    if intersection.geom_type != "Point":
        return True
    coordinates_at_crossing = tuple(intersection.coords[0])
    if endpoint_ids & other_ids and all(
        min(math.dist(coordinates_at_crossing, xy) for xy in (coords[0], coords[-1])) < .02
        for coords in (line.coords, other_line.coords)
    ):
        return False
    ratio = other_line.project(intersection, normalized=True)
    return abs(ya + ratio * (yb - ya) - ground(coordinates_at_crossing)) < 5


def gap_cells(sites):
    existing_sites = {site for site, _, _, _ in sites}
    with (INPUT / "extension-zoning-groups.tsv").open(encoding="utf-8-sig", newline="") as source:
        served = {row["roleGroup"] for row in csv.DictReader(source, delimiter="\t") if row["site"] in existing_sites}
    path = INPUT / "native-zoning-gap-build-input.tsv"
    with path.open(encoding="utf-8-sig", newline="") as source:
        return [row for row in csv.DictReader(source, delimiter="\t")
                if row["use"] in BUILDINGS and row["ward"] != "unassigned" and row["projected_centroid_x"]
                and row["group"] not in served]


def gap_position(row):
    return float(row["projected_centroid_x"]), float(row["projected_centroid_z"])


def source_candidates(roads, edges, anchors, ground, gap_weights):
    known = {r["osmSource"] for r in roads}
    existing = [road_shape(road, ground) for road in roads]
    installed = STRtree([row[0] for row in existing])
    candidates = []
    for number, edge in enumerate(edges.values()):
        if (edge["id"] in known or edge["kind"] not in LOCAL_CLASSES or not edge["pedestrianAllowed"]
                or edge["layer"] != "0" or edge["bridge"] != "no" or edge["tunnel"] != "no"
                or edge["oneway"] in {"yes", "-1"}
                or edge["tags"].get("access") in {"private", "no"}):
            continue
        a, b = edge["fromNode"], edge["toNode"]
        if a == b:
            continue
        points = street_points(edge)
        if any(max(map(abs, xy)) > 4650 for xy in points):
            continue
        p, q = anchors.get(a, (a, points[0], 0)), anchors.get(b, (b, points[-1], 0))
        points[0], points[-1] = p[1], q[1]
        points = fit_street(points, ground, p[2], q[2])
        if not points:
            continue
        line = LineString(points)
        ids = {p[0], q[0]}
        if any(conflict(line, ids, existing[k], ground) for k in installed.query(line, predicate="intersects")):
            continue
        center = line.interpolate(.5, normalized=True)
        ix, iz = int(center.x // 135), int(center.y // 135)
        weight = gap_weights[ix, iz] + sum(gap_weights[ix + x, iz + z] for x, z in ((1, 0), (-1, 0), (0, 1), (0, -1))) / 4
        candidates.append((number, edge["id"], a, b, points, ids, line, weight))
    return candidates


def connect_streets(roads, candidates, anchors, ground, budget):
    adjacency = defaultdict(list)
    degree = Counter(key for r in roads for key in (r["plannedStart"], r["plannedEnd"]))
    for i, (_, _, a, b, *_) in enumerate(candidates):
        adjacency[a].append(i)
        adjacency[b].append(i)
    connected = set(anchors)
    added = {}
    seen = set()
    heap = []
    index = STRtree([row[6] for row in candidates])

    def rank(i):
        _, _, a, b, points, _, line, weight = candidates[i]
        return ((a not in connected) + (b not in connected), -(line.length + weight * 2) / (len(points) - 1), i)

    for i, row in enumerate(candidates):
        if row[2] in connected or row[3] in connected:
            heappush(heap, rank(i))
    built = []
    for _ in range(len(candidates) * 4):
        if not heap or len(built) >= budget:
            break
        priority = heappop(heap)
        i = priority[-1]
        if i in seen:
            continue
        actual = rank(i)
        if actual != priority:
            heappush(heap, actual)
            continue
        seen.add(i)
        number, source, a, b, points, ids, line, _ = candidates[i]
        start_id, _, start_offset = anchors.get(a, (a, None, 0))
        end_id, _, end_offset = anchors.get(b, (b, None, 0))
        if len(built) + len(points) - 1 > budget or max(degree[start_id], degree[end_id]) >= 8:
            continue
        if any(conflict(line, ids, added[k], ground) for k in index.query(line, predicate="intersects") if k in added):
            continue
        keys = [start_id, *(f"infill{number}_{j}" for j in range(1, len(points) - 1)), end_id]
        offsets = [start_offset, *([0] * (len(points) - 2)), end_offset]
        rows = []
        for key_a, key_b, p, q, da, db in zip(keys, keys[1:], points, points[1:], offsets, offsets[1:]):
            rows.append({"type": "build-road", "roadPrefab": "Basic Road", "osmSource": source,
                         "plannedStart": key_a, "plannedEnd": key_b, "startHeightOffset": da, "endHeightOffset": db,
                         "start": {"x": p[0], "z": p[1]}, "end": {"x": q[0], "z": q[1]}})
        built.extend(rows)
        added[i] = (line, ids, ground(points[0]) + start_offset, ground(points[-1]) + end_offset)
        degree.update((start_id, end_id))
        for node in (a, b):
            if node not in connected:
                connected.add(node)
                for option in adjacency[node]:
                    heappush(heap, rank(option))
    return built


def zoning_candidates(streets, sites, gaps):
    lines = [LineString((coordinates(r["start"]), coordinates(r["end"]))) for r in streets]
    index = STRtree(lines)
    occupied = {tuple(round(v / 16) for v in coordinates(cmd)) for _, _, _, cmd in sites}
    proposals = []
    for row in gaps:
        center = Point(gap_position(row))
        street_no = index.nearest(center)
        line = lines[street_no]
        if line.distance(center) > 55 or line.length < 16:
            continue
        distance = min(max(line.project(center), 8), line.length - 8)
        access = line.interpolate(distance)
        (a, b) = line.coords
        dx, dz = (b[0] - a[0]) / line.length, (b[1] - a[1]) / line.length
        side = 1 if (center.x - access.x) * -dz + (center.y - access.y) * dx > 0 else -1
        x, z = access.x - 12 * side * dz, access.y + 12 * side * dx
        cell = round(x / 16), round(z / 16)
        if cell in occupied:
            continue
        occupied.add(cell)
        zone = row["use"]
        key = "infill_" + sha256(row["group"].encode()).hexdigest()[:16]
        proposals.append({"site": key, "ward": row["ward"], "sourceGroup": row["group"],
                          "sourceBuildings": int(row["model_id_count"]), "roadSource": streets[street_no]["osmSource"],
                          "command": {"buildingPrefab": BUILDINGS[zone], "x": x, "z": z, "zone": zone}})
    return proposals


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--segment-budget", type=int, default=7500, choices=range(500, 12001), metavar="500..12000")
    args = parser.parse_args()
    raw, base, sites, _ = prepare(True, True)
    edges = {e["id"]: e for e in json.loads(SOURCE.read_text(encoding="utf-8-sig"))["edges"]}
    heightmap = np.frombuffer(raw, dtype="<u2").reshape(1081, 1081) / 64

    def ground(p):
        return terrain_height(heightmap, *p, True)

    gaps = gap_cells(sites)
    counts = Counter()
    for row in gaps:
        x, z = gap_position(row)
        counts[int(x // 135), int(z // 135)] += math.sqrt(int(row["model_id_count"]))
    anchors = graph_anchors(base, edges)
    eligible = source_candidates(base, edges, anchors, ground, counts)
    streets = connect_streets(base, eligible, anchors, ground, args.segment_budget)
    if not streets:
        raise RuntimeError("No new streets connected to a known OSM junction.")
    proposals = zoning_candidates(streets, sites, gaps)
    before = road_topology(base)
    base_sha256 = road_plan_sha256(base)
    # Smoothing assigns prefab types in place. Keep the serialized plan in its unsmoothed form.
    smoothed, stats = prepare_smooth_roads([dict(row) for row in base + streets], raw, True)
    after = road_topology(smoothed)
    if after["components"] != 1 or after["nodes"] >= 32768 or len(smoothed) >= 36864 or after["maximumJunctionDegree"] > 8:
        raise RuntimeError("Infill plan is disconnected or exceeds native network capacity.")
    report = {"status": "offline plan", "terrainSHA256": sha256(raw).hexdigest(), "baseRoadPlanSHA256": base_sha256,
              "mappedAnchors": len(anchors), "eligibleOSMStreetParts": len(eligible),
              "addedNativeInputSegments": len(streets), "addedOSMStreetParts": len({r["osmSource"] for r in streets}),
              "addedZoningProposals": len(proposals), "buildingProposalsByWard": dict(Counter(p["ward"] for p in proposals)),
              "baseTopology": before, "smoothedTopology": after, "smoothing": stats}
    OUTPUT.write_text(json.dumps({"report": report, "roads": streets, "sites": proposals}, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
