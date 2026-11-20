"""Build the compiled Nagoya road network and PLATEAU-derived zoning sites on the native terrain."""
import argparse
from collections import Counter, defaultdict
import hashlib
import json
import math
from pathlib import Path
from time import monotonic, sleep

import numpy as np

from nagoya_map import ROOT, call, check_empty, closest_point, read_json

INPUT = ROOT.parent / "citywide-zoning-v3"
ROAD_PREFABS = {
    "arterial": "Medium Road", "basic": "Basic Road", "bridge": "Basic Road Bridge",
    "elevated": "Basic Road Elevated", "highway": "Highway", "tunnel": "Basic Road Tunnel",
    "slope": "Basic Road Slope", "highway_slope": "Highway Slope",
    "highway_tunnel": "Highway Tunnel", "highway_elevated": "Highway Elevated",
    "arterial_slope": "Medium Road Slope", "arterial_tunnel": "Medium Road Tunnel",
    "arterial_elevated": "Medium Road Elevated",
}
BUILDINGS = {
    "ResidentialLow": "L1 1x1 Detached", "ResidentialHigh": "H1 1x1 Tenement",
    "CommercialLow": "L1 1x1 Shop", "CommercialHigh": "H1 1x1 Shop07",
    "Office": "H1 1x1 Office", "Industrial": "H1 1x1 Facility01",
}
SIDEWALK_WIDTH = {"Basic Road": 8, "Medium Road": 16}
STREET_PLANS = (
    "native-street-extension-oriented.tsv", "next-local-streets-after-service-capacity.tsv",
    "third-local-streets.tsv", "fourth-local-streets-nagoya.tsv",
)
ZONING_PLANS = (
    "native-zoning-requests.tsv", "extension-zoning-requests.tsv", "second-local-streets-zoning-requests.tsv",
    "third-local-streets-zoning-requests.tsv", "fourth-local-served-sites.tsv",
)


def terrain_height(grid, x, z, interpolate):
    col = (x + 8640) / 16
    row = (8640 - z) / 16
    if not (0 <= col <= 1080 and 0 <= row <= 1080):
        raise ValueError("Road node is outside the game map")
    if not interpolate:
        return float(grid[round(row), round(col)])
    left, top = min(int(col), 1079), min(int(row), 1079)
    dx, dz = col - left, row - top
    near = grid[top, left] * (1 - dx) + grid[top, left + 1] * dx
    far = grid[top + 1, left] * (1 - dx) + grid[top + 1, left + 1] * dx
    return float(near * (1 - dz) + far * dz)


def prepare(interpolate=False, extended=False):
    raw = (ROOT / "height-north-up.bin").read_bytes()
    if len(raw) != 1081 ** 2 * 2:
        raise ValueError("Unexpected DEM raster size")
    terrain = np.frombuffer(raw, dtype="<u2").reshape(1081, 1081)
    heights = terrain.astype(np.float64) / 64
    nodes = {}
    roads = []
    skipped = Counter()
    # Correct the two short ramp approaches on OSM way 193435122 without moving the junctions.
    ramp_offsets = {"n012615": -.28, "n012617": .34} if interpolate else {}
    for line in (INPUT / "native-roads-clearance-adjusted.tsv").read_text(encoding="utf-8-sig").splitlines():
        fields = line.split("\t")
        if fields[0] == "N":
            _, key, x, z, offset = fields
            point = {"x": float(x), "z": float(z)}
            y = terrain_height(heights, point["x"], point["z"], interpolate)
            nodes[key] = (point, float(offset) + ramp_offsets.get(key, 0), y)
            continue
        kind, source, start_id, end_id, prefab, layer, _ = fields
        if kind != "E":
            raise ValueError(f"Unexpected road row {kind}")
        start, start_offset, start_height = nodes[start_id]
        end, end_offset, end_height = nodes[end_id]
        length = math.hypot(start["x"] - end["x"], start["z"] - end["z"])
        grade = abs(start_height + start_offset - end_height - end_offset) / max(length, 1)
        if length < 4 or grade > .23:
            skipped["length_or_grade"] += 1
            continue
        if min(start_height, end_height) == 135 and prefab in {"basic", "arterial", "highway"}:
            skipped["missing_land_elevation"] += 1
            continue
        roads.append({"type": "build-road", "roadPrefab": ROAD_PREFABS[prefab], "start": start, "end": end,
                      "plannedStart": start_id, "plannedEnd": end_id, "startHeightOffset": start_offset,
                      "endHeightOffset": end_offset, "osmSource": source})

    if extended:
        roads.extend(add_local_streets(nodes, heights, roads, skipped))
    sites = []
    known_sites = set()
    for plan in ZONING_PLANS if extended else ZONING_PLANS[:1]:
        for line in (INPUT / plan).read_text(encoding="utf-8-sig").splitlines():
            fields = line.split("\t")
            if fields[0] != "S":
                continue
            _, site, _, x, z, zone, source_count, ward = fields[:8]
            if site in known_sites:
                continue
            known_sites.add(site)
            cmd = {"buildingPrefab": BUILDINGS[zone], "x": float(x), "z": float(z), "zone": zone}
            sites.append((site, ward, int(source_count), cmd))
    return raw, roads, sites, skipped


def add_local_streets(nodes, heights, roads, skipped):
    def coordinate(point):
        return round(point["x"], 3), round(point["z"], 3)

    degree = Counter(key for row in roads for key in (row["plannedStart"], row["plannedEnd"]))
    position_keys = defaultdict(list)
    for key in degree:
        position_keys[coordinate(nodes[key][0])].append(key)
    occupied = {frozenset((row["plannedStart"], row["plannedEnd"])) for row in roads}
    extended = []
    for stage, filename in enumerate(STREET_PLANS, 1):
        rename = {}
        pending = defaultdict(list)
        candidates = []
        for line in (INPUT / filename).read_text(encoding="utf-8-sig").splitlines():
            fields = line.split("\t")
            tag, name = fields[:2]
            if tag in {"A", "N"}:
                x, z, offset = map(float, fields[2:5])
                point = {"x": x, "z": z}
                if tag == "A":
                    matches = [key for key in position_keys[coordinate(point)] if abs(nodes[key][1] - offset) <= .4]
                    if len(matches) == 1:
                        rename[name] = matches[0]
                    else:
                        skipped[f"extension_{stage}_unresolved_anchor"] += 1
                else:
                    key = f"s{stage}_{name}"
                    rename[name] = key
                    nodes[key] = (point, offset, terrain_height(heights, x, z, True))
                continue
            _, source, a, b, prefab, _, _ = fields
            if a not in rename or b not in rename:
                skipped[f"extension_{stage}_missing_endpoint"] += 1
                continue
            a, b = rename[a], rename[b]
            start, da, ya = nodes[a]
            end, db, yb = nodes[b]
            length = math.dist(coordinate(start), coordinate(end))
            if length < 4 or abs(ya + da - yb - db) / length > .23:
                skipped[f"extension_{stage}_length_or_grade"] += 1
                continue
            row = {"type": "build-road", "roadPrefab": ROAD_PREFABS[prefab], "start": start, "end": end,
                   "plannedStart": a, "plannedEnd": b, "startHeightOffset": da, "endHeightOffset": db,
                   "osmSource": source}
            pending[a].append(row)
            pending[b].append(row)
            candidates.append(row)

        # Traverse each stage from already connected and geographically matched endpoints.
        reachable = [key for key in pending if key in degree]
        appended = 0
        for key in reachable:
            for row in pending[key]:
                a, b = row["plannedStart"], row["plannedEnd"]
                pair = frozenset((a, b))
                if pair in occupied or max(degree[a], degree[b]) >= 8:
                    continue
                for node in (a, b):
                    if not degree[node]:
                        reachable.append(node)
                        position_keys[coordinate(nodes[node][0])].append(node)
                    degree[node] += 1
                occupied.add(pair)
                extended.append(row)
                appended += 1
        if len(candidates) > appended:
            skipped[f"extension_{stage}_unreachable_or_already_present"] += len(candidates) - appended
    return extended


def road_topology(roads):
    adjacency = defaultdict(set)
    for road in roads:
        a, b = road["plannedStart"], road["plannedEnd"]
        adjacency[a].add(b)
        adjacency[b].add(a)
    seen = set()
    components = []
    for node in adjacency:
        if node in seen:
            continue
        component = [node]
        seen.add(node)
        for neighbor in component:
            additions = adjacency[neighbor] - seen
            component.extend(additions)
            seen.update(additions)
        components.append(len(component))
    return {"nodes": len(adjacency), "components": len(components),
            "largestComponentNodes": max(components, default=0),
            "maximumJunctionDegree": max(map(len, adjacency.values()), default=0),
            "cycleRank": len(roads) - len(adjacency) + len(components)}


def road_plan_sha256(roads):
    return hashlib.sha256(json.dumps(roads, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def load_neighborhood_infill(raw, roads, sites):
    plan = json.loads((ROOT / "roads" / "neighborhood-infill-plan.json").read_text(encoding="utf-8"))
    report = plan["report"]
    if report["terrainSHA256"] != hashlib.sha256(raw).hexdigest() or report["baseRoadPlanSHA256"] != road_plan_sha256(roads):
        raise ValueError("Neighborhood plan belongs to different roads or terrain. Recompile it first.")
    roads.extend(plan["roads"])
    sites.extend((row["site"], row["ward"], row["sourceBuildings"], row["command"]) for row in plan["sites"])


def apply(name, raw, roads, sites, skipped, seconds, refined, resume):
    deadline = monotonic() + seconds
    source_hash = hashlib.sha256(raw).hexdigest()
    plan_hash = road_plan_sha256(roads)
    evidence = ROOT / f"{name}-build.json"
    if resume:
        report = read_json(evidence)
        existing = report["roads"]
        if report["map"] != name or report["sourceSha256"] != source_hash or report.get("save") or not 0 < existing <= len(roads):
            raise RuntimeError("The checkpoint does not describe the intended unfinished map.")
        if report.get("roadPlanSha256", plan_hash) != plan_hash:
            raise RuntimeError("The road plan changed after the checkpoint.")
        terrain = call("/state/nagoya-terrain", {"x": -1438, "z": 948})
        city = call("/state/summary")
        nodes = {r[tag] for r in roads[:existing] for tag in ("plannedStart", "plannedEnd")}
        expected_build_index = len(nodes) + 2 * existing + len(report["lots"])
        if (terrain["mode"] != "NewMap" or terrain["sourceSha256"] != source_hash or
                city["network"]["segments"] != existing or city["network"]["nodes"] != len(nodes) or
                city["buildIndex"] != expected_build_index or city["citizens"]["count"] != 0):
            raise RuntimeError("The current editor does not match the recorded road and building checkpoint.")
    else:
        check_empty()
        call("/commands/nagoya-import-terrain", {"sha256": source_hash})
        report = {"map": name, "sourceSha256": source_hash, "roads": 0, "lots": [],
                  "offlineRoadExclusions": dict(skipped), "unplacedLots": []}
    report["roadPlanSha256"] = plan_hash

    def record():
        evidence.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    for offset in range(report["roads"], len(roads), 32):
        if monotonic() >= deadline:
            raise TimeoutError("Road build exceeded the specified job deadline.")
        answer = call("/commands/nagoya-build-batch", {"commands": roads[offset:offset + 32],
                                                       "dryRun": False, "stopOnError": True})
        report["roads"] += answer["executed"]
        if offset % 1024 == 0:
            print(f"Native roads placed {report['roads']}/{len(roads)}", flush=True)
        record()
    if call("/state/summary")["network"]["segments"] != len(roads):
        raise RuntimeError("Native network count differs from the prepared map.")
    sleep(1)

    attempted = {row["site"] for row in (*report["lots"], *report["unplacedLots"])}
    for site, ward, source_count, cmd in sites:
        if site in attempted:
            continue
        if monotonic() >= deadline:
            report["zoningDeadlineReached"] = True
            break
        try:
            placed = call("/commands/nagoya-place-zoned-building", cmd)
        except RuntimeError as failure:
            if "No free road-facing zone cells" not in str(failure):
                raise
            report["unplacedLots"].append({"site": site, "ward": ward, "reason": "no available road-facing cells"})
            continue
        report["lots"].append({"site": site, "ward": ward, "sourceBuildings": source_count, **placed})
        if len(report["lots"]) % 100 == 0:
            print(f"Native zoned lots placed {len(report['lots'])}", flush=True)
            record()

    report["nativeZoning"] = call("/state/zones")
    report["props"], report["trees"], report["treePlacementFailures"] = place_street_objects(
        roads, report["lots"], deadline, refined)
    report["nativeCity"] = call("/state/summary")
    report["save"] = call("/commands/nagoya-save-map", {"name": name})
    record()
    print(f"Native map save requested. Evidence: {evidence}", flush=True)
    if refined:
        report["visualScene"] = call("/commands/nagoya-place-visuals", {})
        record()


def place_street_objects(roads, lots, deadline, refined):
    surface = [road for road in roads if road["roadPrefab"] in SIDEWALK_WIDTH]
    by_ward = {}
    trees = []
    tree_failures = Counter()
    for lot in lots:
        ward = lot["ward"]
        if ward == "unassigned" or len(by_ward.get(ward, [])) >= 3:
            continue
        x, z = lot["x"], lot["z"]
        road, sx, sz = min(((road, *closest_point(x, z, road)) for road in surface),
                           key=lambda row: math.hypot(x - row[1], z - row[2]))
        dc = math.hypot(x - sx, z - sz)
        margin = SIDEWALK_WIDTH[road["roadPrefab"]] + .5
        if dc < margin + 2 or dc > margin + 20:
            continue
        vx, vz = (x - sx) / dc, (z - sz) / dc
        point = sx + vx * margin - vz * 4, sz + vz * margin + vx * 4
        near = by_ward.setdefault(ward, [])
        if any(math.dist(point, (item["x"], item["z"])) < 34 for item in near):
            continue
        if monotonic() >= deadline:
            break
        prop = call("/commands/nagoya-place-prop", {"prefab": "Park Bench 01", "x": point[0],
                    "z": point[1], "angleDegrees": math.degrees(math.atan2(vx, vz)) % 360})
        near.append({**prop, "ward": ward})
        if refined and not tree_failures["prefab unavailable"]:
            for distance in (0, 9):
                tree_x = sx + vx * (margin + 2) + vz * distance
                tree_z = sz + vz * (margin + 2) - vx * distance
                try:
                    tree = call("/commands/nagoya-place-tree", {"x": tree_x, "z": tree_z})
                except RuntimeError as failure:
                    if "A supported native tree prefab was not found" in str(failure):
                        tree_failures["prefab unavailable"] += 1
                        break
                    if "Native tree placement failed" not in str(failure):
                        raise
                    tree_failures["placement rejected"] += 1
                    continue
                trees.append({**tree, "ward": ward})
    return [item for items in by_ward.values() for item in items], trees, dict(tree_failures)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", metavar="Nagoya-Real-Citywide-Name")
    parser.add_argument("--seconds", type=int, default=160)
    parser.add_argument("--refined", action="store_true", help="smooth native roads and load real PLATEAU visual assets")
    parser.add_argument("--ramp-profiles", action="store_true", help="use native one-way ramp geometry for OSM motorway links")
    parser.add_argument("--local-streets", action="store_true", help="add the four surveyed citywide street and zoning extensions")
    parser.add_argument("--neighborhood-infill", action="store_true", help="apply the checked neighborhood road and zoning plan")
    parser.add_argument("--resume-build", action="store_true", help="continue an unfinished build in the same editor session")
    args = parser.parse_args()
    if args.ramp_profiles and not args.refined:
        parser.error("Ramp profiles require the refined terrain fit.")
    if args.local_streets and not args.ramp_profiles:
        parser.error("The street extensions require refined ramp profiles.")
    if args.neighborhood_infill and not args.local_streets:
        parser.error("The neighborhood plan requires the four street extensions.")
    if args.resume_build and not args.apply:
        parser.error("Resume requires the original --apply map name.")
    raw, roads, sites, skipped = prepare(args.refined, args.local_streets)
    if args.neighborhood_infill:
        load_neighborhood_infill(raw, roads, sites)
    if args.refined:
        from smooth_citywide_roads import prepare_smooth_roads
        roads, refinement = prepare_smooth_roads(roads, raw, args.ramp_profiles)
        print(json.dumps(refinement), flush=True)
    topology = road_topology(roads)
    if args.refined and topology["components"] != 1:
        parser.error("The refined road plan contains disconnected road groups.")
    if topology["nodes"] >= 32768 or len(roads) >= 36864 or topology["maximumJunctionDegree"] > 8:
        parser.error("The road plan exceeds the native network's capacity.")
    summary = {"roads": len(roads), "topology": topology, "roadExclusions": dict(skipped), "zoningSites": len(sites),
               "sitesByWard": dict(Counter(ward for _, ward, _, _ in sites))}
    print(json.dumps(summary, ensure_ascii=False, indent=2), flush=True)
    if args.apply:
        if not args.apply.startswith("Nagoya-Real-Citywide-") or not args.apply.replace("-", "").isalnum():
            parser.error("Choose a separate citywide map name.")
        if not 30 <= args.seconds <= 600:
            parser.error("Choose a job deadline between 30 and 600 seconds.")
        apply(args.apply, raw, roads, sites, skipped, args.seconds, args.refined, args.resume_build)


if __name__ == "__main__":
    main()
