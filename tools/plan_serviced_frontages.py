"""Plan candidate road frontages for unrepresented Nagoya building uses."""

import argparse
import csv
import hashlib
import json
import math
from collections import Counter
from itertools import chain
from pathlib import Path

import numpy as np
from shapely import points

from assign_nagoya_wards import ward_tree
from plan_native_utility_extension import Grid, snapshot
from plan_zoning_frontage_extension import frontage_slots, index_points, read_tsv, within


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def plan(args):
    road_rows, slots = [], []
    for geometry, receipt in args.road:
        edges = [line.split("\t")[1] for line in geometry.read_text(encoding="utf-8-sig").splitlines()
                 if line.startswith("E\t")]
        rows = read_tsv(receipt)
        if any(edges[int(row["ordinal"])] != row["source"] for row in rows):
            raise ValueError(f"Road receipts do not match {geometry.name}")
        road_rows.extend(row for row in rows if int(row["nativeId"]) > 0)
        slots.extend(frontage_slots(geometry, receipt))
    if not slots or len(road_rows) != len({int(row["nativeId"]) for row in road_rows}):
        raise ValueError("Expected distinct live road receipts and at least one frontage")

    coords = [(x, z) for x, z, _ in slots]
    index = index_points(coords)
    transform = read_json(args.transform)["transform"]
    anchor = read_json(args.district_manifest)["anchor_projected_epsg6675"]
    wards, names = ward_tree(args.wards, anchor["east_m"], anchor["north_m"])
    xy = np.asarray(coords) / transform["scale"] + np.asarray(transform["originalCenterXZ"])
    hits = wards.query(points(xy), predicate="within")
    ward_by_slot = {}
    for i, ward in zip(hits[0], hits[1]):
        ward_by_slot[i] = names[ward] if i not in ward_by_slot else None

    taken = set()
    for path, collection in ((args.growables, "growables"), (args.facilities, "facilities")):
        for lot in snapshot(path, collection):
            position = lot["position"]
            clearance = max(16, math.hypot(lot["width"], lot["length"]) * 4 + 7)
            taken.update(i for _, i in within(index, coords, (position["x"], position["z"]), clearance))

    networks = []
    for path in (args.water, args.power):
        grid = Grid(35)
        for segment in snapshot(path, "segments"):
            pose = [(segment[k]["x"], segment[k]["z"]) for k in ("start", "middle", "end")]
            for a, b in zip(pose, pose[1:]):
                grid.put_line(a, b)
        networks.append(grid)
    available = {i for i in ward_by_slot if i not in taken and all(grid.covered(coords[i]) for grid in networks)}

    gaps = read_tsv(args.gaps)
    groups = {row["group"] for row in gaps}
    if len(groups) != len(gaps):
        raise ValueError("Expected one row per unrepresented use group")
    chosen, used_primary_roads = [], set()
    for row in gaps:
        center = float(row["projected_centroid_x"]), float(row["projected_centroid_z"])
        options = sorted((d, i) for d, i in within(index, coords, center, 160)
                         if i in available and ward_by_slot[i] == row["ward"])
        primary = next(((d, i) for d, i in options if i not in taken and slots[i][2] not in used_primary_roads), None)
        if primary is None:
            continue
        frontages, used_here = [], set()
        for d, i in chain((primary,), ((d, i) for d, i in options if i != primary[1])):
            x, z, road = slots[i]
            if road in used_here or any(math.dist((x, z), (a["x"], a["z"])) < 20 for a in frontages):
                continue
            frontages.append(dict(road=road, x=round(x, 3), z=round(z, 3), distanceM=round(math.sqrt(d), 2)))
            used_here.add(road)
            if len(frontages) == args.candidates:
                break
        x, z, road = slots[primary[1]]
        used_primary_roads.add(road)
        taken.update(i for _, i in within(index, coords, (x, z), 24))
        group = "extension_" + hashlib.sha256(row["group"].encode("utf-8")).hexdigest()[:16]
        chosen.append(dict(group=group, roleGroup=row["group"], ward=row["ward"], zone=row["use"],
                           frontages=frontages, modelMembers=int(row["model_id_count"])))
        if len(chosen) == 3000:
            break
    if not chosen or len({row["group"] for row in chosen}) != len(chosen):
        raise ValueError("Expected at least one frontage and distinct output group identities")

    output = args.output
    with output.with_name(output.name + "-roads.tsv").open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(road_rows[0]), delimiter="\t", lineterminator="\n")
        writer.writeheader()
        writer.writerows(road_rows)
    with output.with_name(output.name + "-plan.tsv").open("w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream, delimiter="\t", lineterminator="\n")
        for row in chosen:
            first, *alternates = row["frontages"]
            fields = [value for a in alternates for value in (a["road"], a["x"], a["z"])]
            writer.writerow(("S", row["group"], first["road"], first["x"], first["z"], row["zone"],
                             row["modelMembers"], row["ward"], *fields))
    summary = dict(eligible=len(gaps), proposed=len(chosen), deferred=len(gaps) - len(chosen),
                   candidatesByWard=Counter(row["ward"] for row in chosen),
                   frontageCounts=Counter(len(row["frontages"]) for row in chosen),
                   proposedMemberships=sum(row["modelMembers"] for row in chosen), writesToGame=False)
    output.with_suffix(".json").write_text(json.dumps(dict(summary=summary, candidates=chosen),
                                                    ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("gaps", "transform", "district-manifest", "wards", "growables", "facilities", "water", "power", "output"):
        parser.add_argument("--" + name, required=True, type=Path)
    parser.add_argument("--road", nargs=2, type=Path, action="append", required=True, metavar=("PLAN", "RECEIPT"))
    parser.add_argument("--candidates", type=int, choices=range(1, 9), default=8)
    plan(parser.parse_args())
