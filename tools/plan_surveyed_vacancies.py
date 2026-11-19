"""Match uncovered PLATEAU uses to native vacant cells in the same Nagoya ward."""

import argparse
import csv
import hashlib
import json
from collections import Counter, defaultdict
from pathlib import Path

import numpy as np
from shapely import points

from assign_nagoya_wards import ward_tree
from audit_active_plateau_zoning import audit
from plan_native_utility_extension import Grid, snapshot
from plan_serviced_frontages import read_json
from plan_zoning_frontage_extension import index_points, read_tsv, within


def missing_roles(roster, members, inventory, models, transform):
    active = set(read_json(roster)["activeNativeSiteGroups"])
    uncovered = defaultdict(list)
    aliases = {}
    centers = defaultdict(lambda: [0, 0., 0.])
    with members.open(encoding="utf-8-sig", newline="") as stream:
        for row in csv.DictReader(stream, delimiter="\t"):
            role = row["roleGroup"]
            if role not in aliases:
                aliases[role] = "extension_" + hashlib.sha256(role.encode()).hexdigest()[:16]
            if row["key"] not in active and aliases[role] not in active:
                uncovered[row["sourceId"]].append(role)

    origin_x, origin_z = transform["originalCenterXZ"]
    scale = transform["scale"]
    with models.open(encoding="utf-8-sig", newline="") as stream:
        for row in csv.DictReader(stream):
            for role in uncovered.get(row["source"], ()):
                center = centers[role]
                center[0] += 1
                center[1] += (float(row["x"]) - origin_x) * scale
                center[2] += (float(row["z"]) - origin_z) * scale
    coverage = audit(roster, members, inventory)
    if sum(c[0] for c in centers.values()) != coverage["roleMemberships"]["uncovered"]:
        raise ValueError("Uncovered role group counts disagree with the active city audit")
    return centers, aliases, coverage


def utility_grid(path):
    grid = Grid(35)
    for segment in snapshot(path, "segments"):
        start, middle, end = [(segment[k]["x"], segment[k]["z"]) for k in ("start", "middle", "end")]
        grid.put_line(start, middle)
        grid.put_line(middle, end)
    return grid


def plan(args):
    survey = read_json(args.survey)
    if survey["phase"] != "complete" or survey["survey"]["writesToCity"]:
        raise ValueError("Expected a complete read-only native survey")
    survey = survey["survey"]
    checkpoint = read_json(args.summary)
    if (not checkpoint["simulation"]["paused"] or checkpoint["buildIndex"] != survey["buildIndex"] or
            checkpoint["network"]["segments"] != survey["segments"] or args.vacancies.name != survey["file"]):
        raise ValueError("Vacancies must come from the recorded paused city")
    mask_by_use = {name: 1 << i for i, name in enumerate(survey["zoneMaskOrder"])}
    transform = read_json(args.transform)["transform"]
    centers, aliases, baseline = missing_roles(args.roster, args.members, args.inventory, args.models, transform)
    receipts = {int(r["nativeId"]): r for r in read_tsv(args.roads) if int(r["nativeId"]) > 0}
    water, power = utility_grid(args.water), utility_grid(args.power)
    vacancies = read_tsv(args.vacancies)
    if len(vacancies) != survey["records"]:
        raise ValueError("Native vacancy result is incomplete")
    coordinates, utility_access = [], []
    for v in vacancies:
        native = receipts.get(int(v["roadId"]))
        if native is None or native["source"] != v["source"] or native["buildIndex"] != v["buildIndex"]:
            raise ValueError(f'Survey frontage does not match the compiled road {v["roadId"]}')
        x, z = float(v["x"]), float(v["z"])
        coordinates.append((x, z))
        utility_access.append((water.covered((x, z)), power.covered((x, z))))

    anchor = read_json(args.district)["anchor_projected_epsg6675"]
    tree, names = ward_tree(args.wards, anchor["east_m"], anchor["north_m"])
    world = np.asarray(coordinates) / transform["scale"] + np.asarray(transform["originalCenterXZ"])
    hits = tree.query(points(world), predicate="within")
    wards = {}
    for pos, ward in zip(hits[0], hits[1]):
        wards[pos] = names[ward] if pos not in wards else None

    index = index_points(coordinates)
    options, deferred = [], []
    for role, (count, sum_x, sum_z) in centers.items():
        ward, _, _, use = role.rsplit(":", 3)
        nearby = sorted((d, i) for d, i in within(index, coordinates, (sum_x / count, sum_z / count), 160)
                        if wards.get(i) == ward and int(vacancies[i]["useMask"]) & mask_by_use[use])
        served = [(d, i) for d, i in nearby if all(utility_access[i])]
        if served:
            options.append((role, ward, use, count, served))
            continue
        gap = dict(role=role, ward=ward, use=use, members=count, reason="no compatible frontage")
        if nearby:
            distance, i = nearby[0]
            has_water, has_power = utility_access[i]
            gap.update(reason="missing nearby utilities", distance=round(distance ** .5, 2),
                       frontage=vacancies[i], nearbyWater=has_water, nearbyPower=has_power)
        deferred.append(gap)

    chosen, reserved = [], set()
    for role, ward, use, count, nearby in sorted(options, key=lambda item: (-item[3], len(item[4]), item[0])):
        for distance, i in nearby:
            v = vacancies[i]
            start = int(v["row"])
            cells = {(int(v["block"]), row, col) for row in range(start, start + int(v["width"]))
                     for col in range(int(v["length"]))}
            if cells & reserved:
                continue
            reserved.update(cells)
            chosen.append(dict(group=aliases[role], role=role, ward=ward, use=use, members=count,
                               distance=round(distance ** .5, 2), frontage=v))
            break
        else:
            deferred.append(dict(role=role, ward=ward, use=use, members=count, reason="surveyed cells already reserved"))
    if not 0 < len(chosen) <= 3000:
        raise ValueError("The vacancy plan exceeds the native batch size or has no matches")

    with args.output.with_name(args.output.name + "-plan.tsv").open("x", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream, delimiter="\t", lineterminator="\n")
        for row in chosen:
            f = row["frontage"]
            writer.writerow(("S", row["group"], f["source"], f["x"], f["z"], row["use"], row["members"], row["ward"]))
    summary = dict(surveyed=len(vacancies), nearBothUtilities=sum(all(row) for row in utility_access),
                   uncoveredRoleGroups=len(centers), deferredReasons=Counter(row["reason"] for row in deferred),
                   proposed=len(chosen), deferred=len(deferred), memberships=sum(r["members"] for r in chosen),
                   byWard=Counter(r["ward"] for r in chosen), byUse=Counter(r["use"] for r in chosen))
    with args.output.with_suffix(".json").open("x", encoding="utf-8") as stream:
        json.dump(dict(summary=summary, baseline=baseline, candidates=chosen, deferred=deferred), stream,
                  ensure_ascii=False, indent=2)
    print(json.dumps(summary, ensure_ascii=False))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("survey", "vacancies", "summary", "roster", "members", "inventory", "models", "transform",
                "district", "wards", "roads", "water", "power", "output"):
        parser.add_argument("--" + key, type=Path, required=True)
    plan(parser.parse_args())
