"""Match Nagoya GTFS stops and a depot site to the constructed CS1 road network."""

import argparse
import csv
import io
import json
import math
from pathlib import Path
from zipfile import ZipFile

from pyproj import Transformer
from shapely import LineString, Point, STRtree, box
from shapely.affinity import rotate, translate


STOPS = ("41200504", "52310301", "52309301", "05059501", "21068501")
DEPOT_SEARCH_M = 1600
GTFS_SOURCE = "https://data.bodik.jp/dataset/231002_7109030000_bus-gtfs-jp/resource/125a1d12-7df6-489c-abde-911856e05d1b"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def point(position):
    return Point(position["x"], position["z"])


def edges(tsv, receipt):
    points = {}
    ordinal = -1
    with receipt.open(encoding="utf-8-sig", newline="") as stream:
        accepted = {(row["source"], int(row["ordinal"])): row
                    for row in csv.DictReader(stream, delimiter="\t") if not row["error"]}
    for line in tsv.read_text(encoding="utf-8-sig").splitlines():
        kind, *values = line.split("\t")
        if kind in ("N", "A"):
            key, x, z, y = values[:4]
            points[key] = (float(x), float(y) + 140, float(z))
        elif kind == "E":
            key, start, end, tag, _, forward = values
            ordinal += 1
            original = accepted.pop((key, ordinal), None)
            if not original:
                continue
            a, b = points[start], points[end]
            if forward == "0":
                a, b = b, a
            flat = LineString(((a[0], a[2]), (b[0], b[2])))
            if flat.length > 0:
                yield {"id": int(original["nativeId"]), "a": a, "b": b,
                       "startNode": int(original["startNode"]), "endNode": int(original["endNode"]),
                       "tag": tag, "line": flat}
    if accepted:
        raise ValueError(f"{len(accepted)} native road receipts do not match {tsv.name}")


def stop_targets(gtfs, anchor, transform):
    project = Transformer.from_crs(4326, 6675, always_xy=True)
    base = anchor["anchor_projected_epsg6675"]
    ox, oz = transform["originalCenterXZ"]
    scale = transform["scale"]
    with ZipFile(gtfs) as archive, archive.open("stops.txt") as stream:
        by_id = {row["stop_id"]: row for row in csv.DictReader(io.TextIOWrapper(stream, encoding="utf-8-sig"))}
    for stop_id in STOPS:
        row = by_id[stop_id]
        east, north = project.transform(float(row["stop_lon"]), float(row["stop_lat"]))
        yield {"gtfsId": stop_id, "name": row["stop_name"],
               "lat": float(row["stop_lat"]), "lon": float(row["stop_lon"]),
               "target": Point((east - base["east_m"] - ox) * scale, (north - base["north_m"] - oz) * scale)}


def near_rooms(road, target, occupied, obstruction, utility, needed):
    ax, _, az = road["a"]
    bx, _, bz = road["b"]
    dx, dz = bx - ax, bz - az
    length = road["line"].length
    if length < needed + 6:
        return
    for fraction in (.3, .5, .7):
        px, pz = ax + dx * fraction, az + dz * fraction
        for side in (-1, 1):
            nx, nz = dz * side / length, -dx * side / length
            offset = needed / 2 + (16 if road["tag"] == "arterial" else 10)
            center = Point(px + nx * offset, pz + nz * offset)
            if center.distance(target) > DEPOT_SEARCH_M:
                continue
            footprint = box(-needed / 2, -needed / 2, needed / 2, needed / 2)
            footprint = translate(rotate(footprint, math.degrees(math.atan2(dz, dx))), center.x, center.y)
            if len(occupied.query(footprint.buffer(4), predicate="intersects")):
                continue
            if len(obstruction.query(footprint.buffer(7), predicate="intersects")):
                continue
            if any(center.distance(tree.geometries[tree.nearest(center)]) > 65 for tree in utility):
                continue
            angle = math.degrees(math.atan2(nx, -nz)) % 360
            yield (center.distance(target), center, angle)


def build_plan(args):
    base = args.city
    runtime = args.output.parent
    loaded = [*edges(base / "native-roads-clearance-adjusted.tsv", base / "d408ff775e27426a9a1921f98659f5e6-roads.tsv"),
              *edges(base / "native-street-extension-oriented.tsv", base / "0fbd95d7a3294db2a549b2f6af0a372d-roads.tsv")]
    streets = [row for row in loaded if row["tag"] in ("basic", "arterial") and row["line"].length >= 22]
    street_tree = STRtree([row["line"] for row in streets])
    anchor = read(base.parent / "source/district-source-manifest.json")
    transform = read(base.parent / "citywide-zoning-v2/native-road-plan-audit.json")["transform"]
    stops = []
    for row in stop_targets(args.gtfs, anchor, transform):
        target = row.pop("target")
        options = []
        for idx in street_tree.query(target.buffer(110), predicate="intersects"):
            road = streets[idx]
            line = road["line"]
            fraction = min(.75, max(.25, line.project(target, normalized=True)))
            snapped = line.interpolate(fraction, normalized=True)
            y = (1 - fraction) * road["a"][1] + fraction * road["b"][1]
            if all(math.dist((snapped.x, snapped.y), (s["position"]["x"], s["position"]["z"])) > 42 for s in stops):
                options.append((target.distance(snapped), road["id"], snapped, y, road))
        if not options:
            raise ValueError(f'No distinct CS1 bus street for {row["name"]}')
        delta, _, snapped, y, road = min(options, key=lambda a: (a[0] + (8 if a[4]["tag"] == "basic" else 0), a[1]))
        stops.append({**row, "roadId": road["id"], "roadNodeIds": [road["startNode"], road["endNode"]],
                      "distanceFromGtfsM": round(delta, 2),
                      "position": {"x": round(snapped.x, 3), "y": round(y, 3), "z": round(snapped.y, 3)}})

    if args.depot_plan:
        return {"source": GTFS_SOURCE, "lineName": "名古屋2050 都心循環", "writesToGame": False,
                "stops": stops, "depot": read(args.depot_plan)["depot"]}
    geom = lambda row: LineString([(row[key]["x"], row[key]["z"]) for key in ("start", "end")])
    rails = [geom(row) for row in read(runtime / "before-rail.json")["segments"]
             if row["subService"] == "PublicTransportTrain"]
    obstacles = STRtree([row["line"] for row in loaded] + rails)
    occupied = []
    for kind in ("growables", "facilities"):
        buildings = read(runtime / f"before-{kind}.json")
        if buildings["returned"] != buildings["total"]:
            raise ValueError(f"Incomplete building inventory for {kind}")
        for row in buildings[kind]:
            radius = 4 * math.hypot(row["width"], row["length"])
            occupied.append(point(row["position"]).buffer(radius))
    building_tree = STRtree(occupied)
    utilities = [STRtree([geom(row) for row in read(runtime / f"before-{name}.json")["segments"]])
                 for name in ("water", "power")]
    target = point(stops[0]["position"])
    candidates = []
    for index in street_tree.query(target.buffer(DEPOT_SEARCH_M), predicate="intersects"):
        road = streets[index]
        for delta, center, angle in near_rooms(road, target, building_tree, obstacles, utilities, 48):
            candidates.append((delta, road["id"], center, angle, road))
    if not candidates:
        raise ValueError("No unoccupied depot frontage near Nagoya Station")
    delta, _, center, angle, road = min(candidates, key=lambda a: a[:2])
    return {
        "source": GTFS_SOURCE, "lineName": "名古屋2050 都心循環", "writesToGame": False, "stops": stops,
        "depot": {"buildingPrefab": "Bus Depot", "roadId": road["id"],
                  "roadNodeIds": [road["startNode"], road["endNode"]], "angleDegrees": round(angle, 3),
                  "position": {"x": round(center.x, 3), "z": round(center.y, 3)},
                  "distanceFromStationStopM": round(delta, 2)},
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("city", "gtfs", "output"):
        parser.add_argument(f"--{key}", required=True, type=Path)
    parser.add_argument("--depot-plan", type=Path, help="Reuse the existing depot site from a prior plan.")
    args = parser.parse_args()
    args.output.write_text(json.dumps(build_plan(args), indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
