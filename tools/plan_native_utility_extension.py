"""Plan extensions from existing stock water and electricity networks to actual new lots."""

import argparse
import json
import math
from collections import Counter, defaultdict
from pathlib import Path

from plan_native_city_utilities import point_segment
from plan_zoning_frontage_extension import read_tsv


def snapshot(path, collection):
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    if not data["ok"] or data["total"] != data["returned"] or not data[collection]:
        raise ValueError(f"Incomplete native snapshot: {path.name}")
    return data[collection]


def cell(point, span):
    return math.floor(point[0] / span), math.floor(point[1] / span)


class Grid:
    def __init__(self, threshold):
        self.lines = defaultdict(list)
        self.nodes = defaultdict(list)
        self.threshold = threshold

    def put_line(self, a, b):
        padding = self.threshold + 1
        lo = cell((min(a[0], b[0]) - padding, min(a[1], b[1]) - padding), 128)
        hi = cell((max(a[0], b[0]) + padding, max(a[1], b[1]) + padding), 128)
        for x in range(lo[0], hi[0] + 1):
            for z in range(lo[1], hi[1] + 1):
                self.lines[x, z].append((a, b))

    def covered(self, p):
        return any(point_segment(p, a, b) <= self.threshold for a, b in self.lines.get(cell(p, 128), ()))

    def put_node(self, key, point):
        self.nodes[cell(point, 128)].append((key, point))

    def closest(self, p, degree):
        bx, bz = cell(p, 128)
        options = ((math.dist(p, q[:2]), key) for x in range(bx - 5, bx + 6)
                   for z in range(bz - 5, bz + 6) for key, q in self.nodes.get((x, z), ()) if degree[key] < 8)
        return min(options, default=(math.inf, None))


def extend(tag, native, receipts, sites, threshold):
    grid = Grid(threshold)
    degree = Counter()
    poses, anchors, used = {}, {}, set()
    additions = []
    for segment in native:
        for name in ("start", "end"):
            degree[f'{tag}_live_{segment[name + "NodeId"]}'] += 1
        points = [tuple(segment[k][axis] for axis in ("x", "z", "y")) for k in ("start", "middle", "end")]
        for a, b in zip(points, points[1:]):
            grid.put_line(a[:2], b[:2])
        receipt = receipts.get(segment["id"])
        if not receipt or receipt["prefabTag"] != tag:
            continue
        for name, p in zip(("start", "end"), (points[0], points[-1])):
            node = int(segment[name + "NodeId"])
            if node not in (int(receipt["startNode"]), int(receipt["endNode"])):
                raise ValueError("Utility segment does not match the native receipt")
            key = f"{tag}_live_{node}"
            if key not in poses:
                poses[key] = (*p[:2], p[2] - 140.)
                anchors[key] = (node, segment["id"], int(receipt["buildIndex"]))
                grid.put_node(key, poses[key])
    if not anchors:
        raise ValueError(f"No native {tag} anchors")
    uncovered = [point for point in sites if not grid.covered(point)]
    neighborhoods = defaultdict(list)
    for point in uncovered:
        neighborhoods[cell(point, 48)].append(point)
    centers = [tuple(sum(p[i] for p in cluster) / len(cluster) for i in (0, 1))
               for _, cluster in sorted(neighborhoods.items(), key=lambda row: (-len(row[1]), row[0]))]
    height = -8. if tag == "water" else 0.
    for point in (*centers, *uncovered):
        if grid.covered(point):
            continue
        distance, root = grid.closest(point, degree)
        if root is None or distance > 520 or distance < 4.005:
            continue
        start = poses[root]
        if abs(start[2] - height) > .15 * distance:
            continue
        previous = root
        steps = math.ceil(distance / 110)
        for step in range(1, steps + 1):
            here = f"{tag}_new_{len(additions):05d}"
            t = step / steps
            pose = tuple(round(a + (b - a) * t, 3) for a, b in zip(start, (*point, height)))
            poses[here] = pose
            grid.put_node(here, pose)
            grid.put_line(poses[previous][:2], pose[:2])
            additions.append((previous, here))
            used.update((previous, here))
            degree.update((previous, here))
            previous = here
    remaining = sum(not grid.covered(p) for p in sites)
    return poses, anchors, additions, used, len(uncovered), remaining


def run(args):
    sites = snapshot(args.growables, "growables")
    points = [(row["position"]["x"], row["position"]["z"]) for row in sites]
    receipts = {int(row["nativeId"]): row for row in read_tsv(args.receipt) if int(row["nativeId"]) > 0}
    bank = [(tag, extend(tag, snapshot(path, "segments"), receipts, points, args.distance))
            for tag, path in (("water", args.water), ("power", args.power))]
    state = json.loads(args.summary.read_text(encoding="utf-8-sig"))["network"]
    count = sum(len(data[2]) for _, data in bank)
    nodes = sum(len(data[3] - data[1].keys()) for _, data in bank)
    if count == 0 or state["nodes"] + nodes >= 32767 or state["segments"] + count >= 36863:
        raise ValueError("Native utility proposal is empty or exceeds the available network slots")
    with args.output.open("w", encoding="utf-8", newline="\n") as stream:
        for _, (poses, anchors, _, used, _, _) in bank:
            for key in sorted(used):
                x, z, y = poses[key]
                row = f"{key}\t{x:.3f}\t{z:.3f}\t{y:.3f}"
                if key in anchors:
                    a, b, c = anchors[key]
                    stream.write(f"A\t{row}\t{a}\t{b}\t{c}\n")
                else:
                    stream.write(f"N\t{row}\n")
        for tag, (_, _, edges, _, _, _) in bank:
            for i, (a, b) in enumerate(edges):
                stream.write(f"E\t{tag}_extension_{i:05d}\t{a}\t{b}\t{tag}\t0\t1\n")
    print(json.dumps({"buildings": len(sites), "thresholdM": args.distance, "segments": count,
                      "junctions": nodes, "networks": {tag: {"segments": len(data[2]), "outsideBefore": data[4],
                          "outsideAfter": data[5]} for tag, data in bank}, "writesToGame": False}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ("growables", "water", "power", "receipt", "summary", "output"):
        parser.add_argument("--" + option, type=Path, required=True)
    parser.add_argument("--distance", type=float, default=45)
    args = parser.parse_args()
    if not 15 <= args.distance <= 75:
        parser.error("Planning distance must be between 15 and 75 metres")
    run(args)
