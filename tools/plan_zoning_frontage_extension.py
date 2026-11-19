"""Find unused first-floor zoning frontages on the roads imported into CS1."""

import argparse
import csv
import hashlib
import json
import math
from collections import Counter, defaultdict
from pathlib import Path


BUCKET_M = 32


def bucket(value):
    return math.floor(value / BUCKET_M)


def index_points(points):
    index = defaultdict(list)
    for i, (x, z) in enumerate(points):
        index[bucket(x), bucket(z)].append(i)
    return index


def within(index, points, center, radius):
    x, z = center
    for bx in range(bucket(x - radius), bucket(x + radius) + 1):
        for bz in range(bucket(z - radius), bucket(z + radius) + 1):
            for i in index.get((bx, bz), ()):
                d2 = (points[i][0] - x) ** 2 + (points[i][1] - z) ** 2
                if d2 <= radius * radius:
                    yield d2, i


def read_tsv(path):
    with path.open(encoding="utf-8-sig", newline="") as source:
        return list(csv.DictReader(source, delimiter="\t"))


def frontage_slots(plan, receipt):
    active = {(row["source"], int(row["ordinal"])) for row in read_tsv(receipt)
              if int(row["nativeId"]) and row["prefabTag"] in {"basic", "arterial"}}
    nodes, slots = {}, []
    ordinal = -1
    with plan.open(encoding="utf-8-sig") as source:
        for line in source:
            part, name, *fields = line.rstrip("\n").split("\t")
            if part == "N":
                nodes[name] = tuple(map(float, fields))
                continue
            ordinal += 1
            a, b, road, *_ = fields
            if (name, ordinal) not in active:
                continue
            x, z, y = nodes[a]
            bx, bz, by = nodes[b]
            length = math.hypot(bx - x, bz - z)
            if length < 16 or abs(y) > 1 or abs(by) > 1:
                continue
            ux, uz = (bx - x) / length, (bz - z) / length
            offset = 15 if road == "basic" else 21
            for distance in range(8, int(length) - 7, 8):
                for side in (-1, 1):
                    slots.append((x + ux * distance + uz * offset * side,
                                  z + uz * distance - ux * offset * side, name))
    return slots


def plan_extension(missing, roads, receipt, growables, output, radius, limit):
    slots = frontage_slots(roads, receipt)
    occupied = json.loads(growables.read_text(encoding="utf-8-sig"))
    if not occupied["ok"] or occupied["returned"] != occupied["total"]:
        raise ValueError("A complete growable read-back is required")
    occupied_points = [(lot["position"]["x"], lot["position"]["z"])
                       for lot in occupied["growables"]]
    if not slots or not occupied_points:
        raise ValueError("Expected actual road and growable inputs")
    coords = [(x, z) for x, z, _ in slots]
    index = index_points(coords)
    consumed = {i for point in occupied_points for _, i in within(index, coords, point, 22)}
    with (output / "extension-zoning-requests.tsv").open("w", encoding="utf-8", newline="") as requests, \
         (output / "extension-zoning-groups.tsv").open("w", encoding="utf-8", newline="") as links:
        emit = csv.writer(requests, delimiter="\t", lineterminator="\n")
        link = csv.writer(links, delimiter="\t", lineterminator="\n")
        link.writerow(("roleGroup", "site", "sourceMembers", "distanceM"))
        tally, wards = Counter(), Counter()
        for row in read_tsv(missing):
            if tally["proposed"] >= limit:
                break
            if not row["projected_centroid_x"] or not row["projected_centroid_z"]:
                tally["missing_centroid"] += 1
                continue
            center = float(row["projected_centroid_x"]), float(row["projected_centroid_z"])
            candidates = ((d2, i) for d2, i in within(index, coords, center, radius) if i not in consumed)
            best = min(candidates, default=None)
            if best is None:
                tally["no_free_frontage"] += 1
                continue
            d2, choice = best
            x, z, road = slots[choice]
            group = row["group"]
            site = f"extension_{hashlib.sha256(group.encode('utf-8')).hexdigest()[:16]}"
            count = int(row["model_id_count"])
            emit.writerow(("S", site, road, f"{x:.3f}", f"{z:.3f}", row["use"], count, row["ward"]))
            link.writerow((group, site, count, f"{math.sqrt(d2):.3f}"))
            consumed.update(i for _, i in within(index, coords, coords[choice], 18))
            tally["proposed"] += 1
            tally["proposed_role_memberships"] += count
            wards[row["ward"]] += 1
    print(json.dumps({"proposals": tally, "wards": wards, "writesToGame": False}, ensure_ascii=False))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for argument in ("missing", "roads", "receipt", "growables", "output"):
        parser.add_argument(f"--{argument}", type=Path, required=True)
    parser.add_argument("--radius", type=float, default=160)
    parser.add_argument("--limit", type=int, default=2700)
    args = parser.parse_args()
    if not 0 < args.radius <= 160 or not 0 < args.limit <= 3000:
        parser.error("Use a positive radius up to 160m and at most 3,000 sites per run")
    plan_extension(**vars(args))


if __name__ == "__main__":
    main()
