"""Measure all PLATEAU use records against a snapshot of CS1 roads.

The report is an inventory and a placement preflight. A nearby road does not
establish a valid CS1 zone cell, service connection, residence or workplace.
"""

import argparse
import csv
import json
import math
import re
from collections import Counter, defaultdict
from dataclasses import dataclass
from pathlib import Path


CELL = 250  # Original project coordinates, in metres.
REACH = 48  # Coarse search radius, including road width and four zone cells.
TILE_SIZE = 1920
TILE_COUNT_PER_AXIS = 5
NATIVE_HALF_SPAN = TILE_SIZE * TILE_COUNT_PER_AXIS // 2
SOURCE_ID = re.compile(r"bldg_[0-9a-fA-F-]{36}$")


@dataclass(frozen=True)
class Road:
    id: int
    points: tuple

    def distance_squared(self, x, z):
        best = math.inf
        for (ax, az), (bx, bz) in zip(self.points, self.points[1:]):
            dx, dz = bx - ax, bz - az
            length_squared = dx * dx + dz * dz
            if not length_squared:
                continue
            t = max(0, min(1, ((x - ax) * dx + (z - az) * dz) / length_squared))
            best = min(best, (x - ax - t * dx) ** 2 + (z - az - t * dz) ** 2)
        return best


def grid(value):
    return math.floor(value / CELL)


def native_roads(snapshot):
    if not snapshot["ok"] or snapshot["total"] != len(snapshot["segments"]):
        raise ValueError("The native road snapshot must contain every road segment")
    sectors = defaultdict(list)
    roads = []
    for segment in snapshot["segments"]:
        if segment["prefab"] != "Basic Road":
            continue
        points = tuple((segment[p]["x"], segment[p]["z"]) for p in ("start", "middle", "end"))
        road = Road(segment["id"], points)
        roads.append(road)
        xs, zs = zip(*points)
        for gx in range(grid(min(xs) - REACH), grid(max(xs) + REACH) + 1):
            for gz in range(grid(min(zs) - REACH), grid(max(zs) + REACH) + 1):
                sectors[gx, gz].append(road)
    return roads, sectors


def render_sources(path):
    state = json.loads(path.read_text(encoding="utf-8-sig"))
    result, unresolved = {}, []
    duplicates = 0
    for model in state["models"]:
        if not model["active"]:
            continue
        render_id = model["id"]
        catalog = Path(model["sourceCatalogPath"] or ".")
        if catalog.is_absolute() and catalog.is_file():
            with catalog.open(encoding="utf-8-sig", newline="") as stream:
                ids = [row["id"] for row in csv.DictReader(stream, delimiter="\t")]
            if len(ids) != model["sourceBuildingCount"]:
                raise ValueError(f"Catalog count mismatch for {render_id}")
        elif model["sourceBuildingCount"] == 1 and SOURCE_ID.fullmatch(render_id):
            ids = [render_id]
        else:
            unresolved.append(render_id)
            continue
        for source in ids:
            if source in result:
                duplicates += 1
            result[source] = render_id
    return result, duplicates, unresolved, state["revision"]


def write_csv(path, headings, rows):
    with path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(headings)
        writer.writerows(rows)


def plan(inventory, network, areas, models, output):
    snapshot = json.loads(network.read_text(encoding="utf-8-sig"))
    roads, sectors = native_roads(snapshot)
    area_snapshot = json.loads(areas.read_text(encoding="utf-8-sig"))
    if not area_snapshot["ok"] or area_snapshot["totalAreaCount"] != TILE_COUNT_PER_AXIS ** 2:
        raise ValueError("Expected a CS1 stock 25-tile area snapshot")
    unlocked = {(item["x"], item["z"]) for item in area_snapshot["areas"] if item["unlocked"]}
    if len(unlocked) != area_snapshot["areaCount"]:
        raise ValueError("The map area count and unlocked tile rows differ")
    render, repeated, unresolved, revision = render_sources(models)
    cohort, blocks, wards = Counter(), Counter(), Counter()
    seen = set()
    candidates = []
    bounds = [math.inf, math.inf, -math.inf, -math.inf]
    reach_squared = REACH ** 2
    with inventory.open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        required = {"source", "x", "z", "use"}
        if not required <= set(reader.fieldnames or ()):
            raise ValueError(f"Missing inventory columns: {sorted(required - set(reader.fieldnames or ()))}")
        for row in reader:
            source = row["source"]
            if not source or source in seen:
                raise ValueError(f"Missing or duplicate CityGML source ID: {source!r}")
            seen.add(source)
            use, ward = row["use"].strip() or "unknown", (row.get("ward") or "").strip() or "unassigned"
            try:
                x, z = float(row["x"]), float(row["z"])
            except ValueError:
                x = z = math.nan
            if not (math.isfinite(x) and math.isfinite(z)):
                status, gx, gz = "missing_geometry", None, None
            else:
                gx, gz = grid(x), grid(z)
                bounds = [min(bounds[0], x), min(bounds[1], z), max(bounds[2], x), max(bounds[3], z)]
                if max(abs(x), abs(z)) >= NATIVE_HALF_SPAN:
                    status = "outside_original_scale_native_grid"
                elif (int((x + NATIVE_HALF_SPAN) // TILE_SIZE),
                      int((z + NATIVE_HALF_SPAN) // TILE_SIZE)) not in unlocked:
                    status = "native_tile_locked"
                else:
                    best = min(
                        ((road.distance_squared(x, z), road.id) for road in sectors.get((gx, gz), ())),
                        default=(math.inf, 0),
                    )
                    if best[0] > reach_squared:
                        status = "no_native_zoning_road_in_search_radius"
                    else:
                        status = "native_road_nearby_cells_unverified"
                        candidates.append((source, ward, use, x, z, best[1], round(math.sqrt(best[0]), 2),
                                           render.get(source, "")))
            cohort[status] += 1
            blocks[gx, gz, ward, use, status] += 1
            wards[ward, use, status] += 1

    if not seen:
        raise ValueError("Empty PLATEAU inventory")
    output.mkdir(parents=True, exist_ok=True)
    write_csv(output / "blocks.csv", ("grid_x", "grid_z", "ward", "use", "coverage", "records"),
              ((*key, count) for key, count in blocks.items()))
    write_csv(output / "wards.csv", ("ward", "use", "coverage", "records"),
              ((*key, count) for key, count in sorted(wards.items())))
    write_csv(output / "native-road-candidates.csv",
              ("source", "ward", "use", "x", "z", "native_road", "distance_m", "active_render_id"),
              sorted(candidates, key=lambda row: (row[1], row[2], row[6], row[0])))
    report = {
        "status": "read-only inventory and road proximity audit",
        "inventory": str(inventory),
        "roadInventory": str(network),
        "uniqueSourceRecords": len(seen),
        "gameRenderRevision": revision,
        "sourceRecordsMatchedToActiveRenderId": len(seen.intersection(render)),
        "activeRenderSourceIdsMissingFromInventory": len(render.keys() - seen),
        "repeatedRenderSourceMemberships": repeated,
        "unresolvedActiveRenderObjects": unresolved,
        "nativeRoadSegments": snapshot["total"],
        "nativeRoadSegmentsInZoningSearch": len(roads),
        "nativeGridHalfSpanM": NATIVE_HALF_SPAN,
        "nativeUnlockedTiles": len(unlocked),
        "searchRadiusM": REACH,
        "reportGridM": CELL,
        "centerBoundsOriginalCoordinates": bounds if all(map(math.isfinite, bounds)) else None,
        "coverageByStatus": dict(sorted(cohort.items())),
        "wardsWithRecords": len({key[0] for key in wards if key[0] != "unassigned"}),
        "blockUseStatusRows": len(blocks),
        "writesToGame": False,
    }
    (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inventory", type=Path, required=True)
    parser.add_argument("--roads", type=Path, required=True)
    parser.add_argument("--areas", type=Path, required=True)
    parser.add_argument("--models", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(plan(args.inventory, args.roads, args.areas, args.models, args.output), ensure_ascii=False))


if __name__ == "__main__":
    main()
