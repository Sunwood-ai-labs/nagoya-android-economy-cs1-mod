"""Find perimeter roads to the actual CS1 service entrances on a flat city map."""

import argparse
import json
import math
from collections import defaultdict
from pathlib import Path

from shapely import LineString, Point, STRtree, box
from shapely.affinity import rotate, translate

from plan_native_city_bus import edges, read


def planar(row):
    return row["x"], row["z"]


def xyz_as_planar(row):
    return row[0], row[2]


def footprint(row):
    half_width, half_depth = 4 * row["width"], 4 * row["length"]
    shape = rotate(box(-half_width, -half_depth, half_width, half_depth), row["angleDegrees"])
    return translate(shape, *planar(row["position"]))


def live_spur(report):
    row = report["actual"]
    if row["service"] != "Road":
        return None
    a, b = row["start"], row["end"]
    return {"id": row["id"], "a": (a["x"], a["y"], a["z"]), "b": (b["x"], b["y"], b["z"]),
            "startNode": row["startNodeId"], "endNode": row["endNodeId"],
            "line": LineString((planar(a), planar(b)))}


def alignment(origin, a, b):
    ax, az = a[0] - origin[0], a[1] - origin[1]
    bx, bz = b[0] - origin[0], b[1] - origin[1]
    return (ax * bx + az * bz) / (math.hypot(ax, az) * math.hypot(bx, bz))


def select_route(facility, roads, road_index, rail_index, building_index):
    cx, cz = planar(facility["position"])
    angle = math.radians(facility["angleDegrees"])
    # CalculateSidewalkPosition uses (-sin(angle), +cos(angle)) for the front.
    fx, fz = -math.sin(angle), math.cos(angle)
    tx, tz = math.cos(angle), math.sin(angle)
    width, depth = 4 * facility["width"], 4 * facility["length"]

    def site(across, forward):
        return cx + tx * across + fx * forward, cz + tz * across + fz * forward

    entry = site(0, depth + 4)
    candidates = []
    nodes = defaultdict(list)
    for index in road_index.query(Point(cx, cz).buffer(220), predicate="intersects"):
        road = roads[index]
        for key, a, b in ((road["startNode"], road["a"], road["b"]),
                          (road["endNode"], road["b"], road["a"])):
            if abs(a[1] - 140) < .01:
                nodes[key].append((xyz_as_planar(a), xyz_as_planar(b)))

    for anchor, arms in nodes.items():
        if not 1 <= len(arms) <= 4:
            continue
        start = arms[0][0]
        for sign in (-1, 1):
            for margin in (12, 18, 27, 38):
                across = sign * (width + margin)
                for setback in (13, 17):
                    front = site(across, depth + setback)
                    end = site(0, depth + setback)
                    paths = ((start, end), (start, front, end),
                             (start, site(across, -depth - 13), front, end))
                    for route in paths:
                        if any(math.dist(a, b) < 14 for a, b in zip(route, route[1:])):
                            continue
                        if any(alignment(start, route[1], a) > .78 for _, a in arms):
                            continue
                        if any(alignment(b, a, c) > .55 for a, b, c in zip(route, route[1:], route[2:])):
                            continue
                        line = LineString(route)
                        if not line.is_simple or line.length > 310:
                            continue
                        corridor = line.buffer(8)
                        if len(building_index.query(corridor, predicate="intersects")) or len(rail_index.query(corridor, predicate="intersects")):
                            continue
                        for idx in road_index.query(line, predicate="intersects"):
                            cut = road_index.geometries[idx].intersection(line)
                            if cut.geom_type != "Point" or cut.distance(Point(start)) > .01 or anchor not in (roads[idx]["startNode"], roads[idx]["endNode"]):
                                break
                        else:
                            candidates.append((line.length, anchor, route))
    if not candidates:
        raise ValueError(f'No safe route to the entrance of building {facility["id"]}')
    length, anchor, route = min(candidates)
    return {"buildingId": facility["id"], "prefab": facility["prefab"], "existingNode": anchor,
            "entrance": dict(zip(("x", "z"), entry)), "lengthM": round(length, 3),
            "points": [{"x": round(x, 3), "z": round(z, 3)} for x, z in route]}


def run(args):
    city, snapshot = args.city, args.snapshot
    roads = [
        *edges(city / "native-roads-clearance-adjusted.tsv", city / "d408ff775e27426a9a1921f98659f5e6-roads.tsv"),
        *edges(city / "native-street-extension-oriented.tsv", city / "0fbd95d7a3294db2a549b2f6af0a372d-roads.tsv"),
    ]
    roads.extend(spur for receipt in args.receipt for row in read(receipt) if (spur := live_spur(row)))
    road_index = STRtree([row["line"] for row in roads])
    rail_index = STRtree([LineString([planar(row[k]) for k in ("start", "middle", "end")])
                         for row in read(args.rail)["segments"] if row["subService"] == "PublicTransportTrain"])
    facilities = read(snapshot / "before-facilities.json")["facilities"]
    buildings = [*facilities, *read(snapshot / "before-growables.json")["growables"]]
    building_index = STRtree([footprint(row) for row in buildings])
    by_id = {row["id"]: row for row in facilities}
    result = [select_route(by_id[building_id], roads, road_index, rail_index, building_index)
              for building_id in args.building]
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("city", "snapshot", "rail", "output"):
        parser.add_argument("--" + name, required=True, type=Path)
    parser.add_argument("--receipt", action="append", default=[], type=Path)
    parser.add_argument("--building", action="append", required=True, type=int)
    run(parser.parse_args())
