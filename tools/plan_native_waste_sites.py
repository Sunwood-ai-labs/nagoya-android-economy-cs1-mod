"""Site the stock incinerator on live Nagoya streets with utility access."""

import argparse
import json
import math
from pathlib import Path

from shapely import LineString, Point, STRtree

from plan_native_city_bus import edges, read
from plan_native_facility_frontage import footprint, live_spur, planar


DISTRICTS = {
    "South": (-1406, -3017), "Central": (-634, 1445),
    "East": (2900, 450), "West": (-3180, -310),
}


def centerline(row):
    return LineString([planar(row[k]) for k in ("start", "middle", "end")])


def candidates(roads, street, area, occupied, houses, rail, utilities):
    for index in street.query(area.buffer(1100), predicate="intersects"):
        road = roads[index]
        if road.get("tag") not in ("basic", "arterial"):
            continue
        if road["line"].length < 36 or max(abs(row[1] - 140) for row in (road["a"], road["b"])) > .1:
            continue
        dx, dz = (road["b"][k] - road["a"][k] for k in (0, 2))
        length = math.hypot(dx, dz)
        for fraction in (.35, .5, .65):
            at = road["line"].interpolate(fraction, normalized=True)
            for side in (-1, 1):
                nx, nz = dz * side / length, -dx * side / length
                offset = 16 + (19 if road["tag"] == "arterial" else 11)
                spot = Point(at.x + nx * offset, at.y + nz * offset)
                if max(abs(spot.x), abs(spot.y)) > 4530:
                    continue
                angle = math.degrees(math.atan2(nx, -nz)) % 360
                record = {"prefab": "Combustion Plant", "roadId": road["id"],
                          "expectedNodes": [road["startNode"], road["endNode"]],
                          "angleDegrees": round(angle, 3),
                          "position": {"x": round(spot.x, 3), "z": round(spot.y, 3)}}
                shape = footprint({**record, "width": 5, "length": 4})
                if len(occupied.query(shape.buffer(7), predicate="intersects")):
                    continue
                if len(rail.query(shape.buffer(8), predicate="intersects")):
                    continue
                if len(street.query(shape.buffer(9), predicate="intersects")):
                    continue
                if len(utilities["power"].query(shape.buffer(4), predicate="intersects")):
                    continue
                if len(houses.query(spot.buffer(120), predicate="intersects")):
                    continue
                limits = {key: round(spot.distance(tree.geometries[tree.nearest(spot)]), 2)
                          for key, tree in utilities.items()}
                if limits["water"] > 46 or limits["power"] > 54:
                    continue
                yield spot.distance(area) + limits["power"] * .5, record | {"estimatedUtilityDistances": limits}


def run(args):
    city, folder = args.city, args.snapshot
    roads = [
        *edges(city / "native-roads-clearance-adjusted.tsv", city / "d408ff775e27426a9a1921f98659f5e6-roads.tsv"),
        *edges(city / "native-street-extension-oriented.tsv", city / "0fbd95d7a3294db2a549b2f6af0a372d-roads.tsv"),
    ]
    roads.extend(live_spur({"actual": segment}) for route in read(args.frontage) for segment in route["segments"])
    buildings = [*read(folder / "frontage-growables.json")["growables"],
                 *read(folder / "before-facilities.json")["facilities"],
                 *read(folder / "before-transit-facilities.json")["facilities"]]
    occupied = STRtree([footprint(row) for row in buildings])
    houses = STRtree([Point(planar(row["position"])) for row in buildings
                      if row["service"] == "Residential" and row["active"] and not row.get("abandoned")])
    street = STRtree([row["line"] for row in roads])
    rail = STRtree([centerline(row) for row in read(args.rail)["segments"]
                    if row["subService"] == "PublicTransportTrain"])
    utilities = {kind: STRtree([centerline(row) for row in read(folder / f"before-{kind}.json")["segments"]])
                 for kind in ("power", "water")}
    sites = []
    for label, position in DISTRICTS.items():
        choices = candidates(roads, street, Point(position), occupied, houses, rail, utilities)
        selected = min(choices, default=None, key=lambda row: (row[0], row[1]["roadId"]))
        if selected is None:
            raise ValueError(f"No clear operating site in the {label} search area")
        sites.append({"district": label, **selected[1]})
    args.output.write_text(json.dumps(sites, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for field in ("city", "snapshot", "frontage", "rail", "output"):
        parser.add_argument("--" + field, required=True, type=Path)
    run(parser.parse_args())
