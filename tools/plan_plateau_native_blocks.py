"""Group Nagoya's PLATEAU uses into prospective native CS1 building sites."""

import argparse
import csv
import json
import math
from collections import Counter, defaultdict
from pathlib import Path


BLOCK_M = 500
MAX_FRONTAGE_DISTANCE = 160
REACH_SQUARED = MAX_FRONTAGE_DISTANCE ** 2
SEARCH_BUCKET_M = 128
ZONING_ROADS = {
    "primary", "primary_link", "secondary", "secondary_link", "tertiary", "tertiary_link",
    "unclassified", "residential", "living_street", "service",
}
USE_TO_ZONES = {
    "office": ("Office",), "commercial": ("CommercialLow",), "hospitality": ("CommercialLow",),
    "mixed_commercial": ("CommercialHigh",), "residential": ("ResidentialLow",),
    "apartments": ("ResidentialHigh",),
    "mixed_residential_commercial": ("ResidentialHigh", "CommercialLow"),
    "mixed_residential_industrial": ("ResidentialLow", "Industrial"),
    "logistics": ("Industrial",), "industrial": ("Industrial",),
}


def frontage_index(edges):
    locations = defaultdict(list)
    for edge in edges:
        if (edge["kind"] not in ZONING_ROADS or not edge["pedestrianAllowed"]
                or edge["layer"] != "0" or edge["bridge"] != "no" or edge["tunnel"] != "no"):
            continue
        for part, (a, b) in enumerate(zip(edge["pointsXZ"], edge["pointsXZ"][1:])):
            length = math.dist(a, b)
            if length < 24:
                continue
            ux, uz = (b[0] - a[0]) / length, (b[1] - a[1]) / length
            slots = int(length // 24)
            for slot in range(slots):
                distance = (slot + .5) / slots * length
                for side in (-1, 1):
                    x = a[0] + ux * distance + uz * 16 * side
                    z = a[1] + uz * distance - ux * 16 * side
                    if max(abs(x), abs(z)) < 4784:
                        locations[bucket(x), bucket(z)].append((x, z, edge["source"], part, slot, side))
    return locations


def bucket(value):
    return math.floor(value / SEARCH_BUCKET_M)


def next_frontage(x, z, locations, occupied):
    hits = []
    for sx in range(bucket(x - MAX_FRONTAGE_DISTANCE), bucket(x + MAX_FRONTAGE_DISTANCE) + 1):
        for sz in range(bucket(z - MAX_FRONTAGE_DISTANCE), bucket(z + MAX_FRONTAGE_DISTANCE) + 1):
            for loc in locations.get((sx, sz), ()):
                distance = (loc[0] - x) ** 2 + (loc[1] - z) ** 2
                if distance <= REACH_SQUARED:
                    hits.append((distance, loc))
    for _, loc in sorted(hits):
        px, pz = math.floor(loc[0] / 16), math.floor(loc[1] / 16)
        if any((px + dx, pz + dz) in occupied for dx in (-1, 0, 1) for dz in (-1, 0, 1)):
            continue
        occupied.add((px, pz))
        return loc
    return None


def role_key(ward, x, z, zone):
    return f"{ward or 'unassigned'}:{math.floor(x / BLOCK_M)}:{math.floor(z / BLOCK_M)}:{zone}"


def plan(inventory, network, destination):
    transform = network["audit"]["transform"]
    scale = transform["scale"]
    ox, oz = transform["originalCenterXZ"]
    locations = frontage_index(network["edges"])
    groups = defaultdict(lambda: [0, 0., 0.])
    assignments = []
    stats = Counter()
    with inventory.open(encoding="utf-8-sig", newline="") as stream:
        for row in csv.DictReader(stream):
            source, ward, use = (row[k] for k in ("source", "ward", "use"))
            x, z = float(row["x"]), float(row["z"])
            xs, zs = (x - ox) * scale, (z - oz) * scale
            roles = USE_TO_ZONES.get(use, ())
            hint = row["cs1_zone_hint"]
            inferred = False
            if not roles and use == "unknown" and hint:
                roles = (hint,)
                inferred = True
            keys = []
            for zone in roles:
                key = role_key(ward, x, z, zone)
                tally = groups[key]
                tally[0] += 1
                tally[1] += xs
                tally[2] += zs
                keys.append(key)
            stats["dataset_records"] += 1
            stats["district_inferred" if inferred else "source_use" if roles else "no_growable_role"] += 1
            assignments.append((source, inferred, keys))

    occupied = set()
    blocks = {}
    shared = defaultdict(list)
    for key, (n, x, z) in sorted(groups.items(), key=lambda item: (-item[1][0], item[0])):
        ward, _, _, zone = key.split(":")
        center_x, center_z = x / n, z / n
        loc = next_frontage(center_x, center_z, locations, occupied)
        if loc is None:
            nearby = min(
                (((old[0] - center_x) ** 2 + (old[1] - center_z) ** 2, old)
                 for old in shared[ward, zone]),
                default=(math.inf, None),
            )
            if nearby[0] <= REACH_SQUARED:
                loc = nearby[1]
        else:
            shared[ward, zone].append(loc)
        blocks[key] = loc
    destination.mkdir(parents=True, exist_ok=True)
    with (destination / "building-block-links.csv").open("w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(("source", "district_used_as_fallback", "role_groups", "groups_with_frontage_proposal"))
        for source, inferred, keys in assignments:
            matched = sum(blocks[k] is not None for k in keys)
            stats["all_roles_have_frontage_proposal" if keys and matched == len(keys)
                  else "some_roles_have_frontage_proposal" if matched else "no_frontage_proposal"] += 1
            writer.writerow((source, int(inferred), "|".join(keys), matched))
    with (destination / "block-frontages.csv").open("w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(("group", "source_building_uses", "proposed_x", "proposed_z", "road_source", "part", "slot", "side"))
        for key, loc in blocks.items():
            writer.writerow((key, groups[key][0], *(loc or ("",) * 6)))
    report = {
        "status": "building use aggregation and road frontage proposals; native placement unverified",
        "blockSizeOriginalM": BLOCK_M,
        "maximumFrontageDistanceProjectedM": MAX_FRONTAGE_DISTANCE,
        "groupRoles": len(groups),
        "groupsWithFrontageProposal": sum(loc is not None for loc in blocks.values()),
        "uniqueProposedNativeSites": sum(len(bucket) for bucket in shared.values()),
        "sourceBuildingCoverage": dict(stats),
        "writesToGame": False,
    }
    (destination / "building-block-audit.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inventory", type=Path, required=True)
    parser.add_argument("--planned-roads", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    network = json.loads(args.planned_roads.read_text(encoding="utf-8-sig"))
    print(json.dumps(plan(args.inventory, network, args.output), ensure_ascii=False))


if __name__ == "__main__":
    main()
