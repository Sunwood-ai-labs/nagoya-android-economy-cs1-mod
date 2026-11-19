"""Plan connected CS1 utility lines from the constructed Nagoya road geometry."""

import argparse
import json
import math
from collections import defaultdict
from pathlib import Path


def point_segment(point, a, b):
    x, z = b[0] - a[0], b[1] - a[1]
    width = x * x + z * z
    if width == 0:
        return math.dist(point, a)
    f = max(0, min(1, ((point[0] - a[0]) * x + (point[1] - a[1]) * z) / width))
    return math.hypot(point[0] - a[0] - f * x, point[1] - a[1] - f * z)


def chains(graph):
    junctions = {node for node, ends in graph.items() if len(ends) != 2}
    visited = set()
    for start in sorted(junctions):
        for neighbor in sorted(graph[start]):
            pair = frozenset((start, neighbor))
            if pair in visited:
                continue
            visited.add(pair)
            strand = [start, neighbor]
            for _ in range(len(graph)):
                if strand[-1] in junctions:
                    break
                a, b = strand[-2:]
                c, = graph[b] - {a}
                edge = frozenset((b, c))
                if edge in visited:
                    raise ValueError("Road chain recurs before it reaches a junction")
                visited.add(edge)
                strand.append(c)
            yield strand
    if len(visited) * 2 != sum(map(len, graph.values())):
        raise ValueError("Not all source roads occur in the utility corridors")


def simplify(chain, coordinates, tolerance):
    keep = {0, len(chain) - 1}
    pending = [(0, len(chain) - 1)]
    while pending:
        a, b = pending.pop()
        if b - a < 2:
            continue
        start, end = coordinates[chain[a]], coordinates[chain[b]]
        distance, mid = max((point_segment(coordinates[chain[i]], start, end), i) for i in range(a + 1, b))
        if distance > tolerance or math.dist(start, end) < 4.002:
            keep.add(mid)
            pending.extend(((a, mid), (mid, b)))
    return [chain[i] for i in sorted(keep)]


def utility_graph(road_file, tolerance):
    coordinates, adjacency = {}, defaultdict(set)
    for line in road_file.read_text(encoding="utf-8").splitlines():
        kind, *row = line.split("\t")
        if kind == "N":
            coordinates[row[0]] = (float(row[1]), float(row[2]))
        elif kind == "E":
            a, b = row[1:3]
            adjacency[a].add(b)
            adjacency[b].add(a)

    edges = set()
    for chain in chains(adjacency):
        path = simplify(chain, coordinates, tolerance)
        for a, b in zip(path, path[1:]):
            start, end = coordinates[a], coordinates[b]
            length = math.dist(start, end)
            if length < 4:
                raise ValueError(f"Utility segment is too short: {a}/{b}")
            pieces = math.ceil(length / 120)
            previous = a
            for k in range(1, pieces):
                key = f"u{len(coordinates)}"
                coordinates[key] = tuple(round(x + (y - x) * k / pieces, 3) for x, y in zip(start, end))
                edges.add(tuple(sorted((previous, key))))
                previous = key
            edges.add(tuple(sorted((previous, b))))
    used = {node for edge in edges for node in edge}
    return {key: coordinates[key] for key in sorted(used)}, sorted(edges)


def measure_buildings(coordinates, edges, buildings):
    grid = defaultdict(list)
    for ak, bk in edges:
        a, b = coordinates[ak], coordinates[bk]
        xs, zs = zip(a, b)
        for ix in range(math.floor((min(xs)-96)/128), math.floor((max(xs)+96)/128)+1):
            for iz in range(math.floor((min(zs)-96)/128), math.floor((max(zs)+96)/128)+1):
                grid[ix, iz].append((a, b))
    distances = []
    for row in buildings:
        point = row["position"]["x"], row["position"]["z"]
        group = math.floor(point[0]/128), math.floor(point[1]/128)
        distances.append(min((point_segment(point, a, b) for a, b in grid.get(group, ())), default=math.inf))
    return {
        "examinedNativeBuildings": len(distances),
        "maximumPlanDistanceM": round(max(distances), 3) if all(map(math.isfinite, distances)) else None,
        "within40M": sum(d <= 40 for d in distances),
        "within64M": sum(d <= 64 for d in distances),
    }


def generate(roads, city_path, summary_path, output):
    city = json.loads(city_path.read_text(encoding="utf-8-sig"))
    buildings = city["growables"]
    if not city["ok"] or city["returned"] != city["total"] or not buildings:
        raise ValueError("A complete live native buildings snapshot is needed")
    coordinates, edges = utility_graph(roads, 12)
    metrics = measure_buildings(coordinates, edges, buildings)
    counts = {"nodes": len(coordinates) * 2, "segments": len(edges) * 2}
    summary = json.loads(summary_path.read_text(encoding="utf-8-sig"))
    stock = summary["network"]
    if counts["nodes"] + stock["nodes"] >= 32767 or counts["segments"] + stock["segments"] >= 36863:
        raise ValueError(f"Road-derived utility network does not fit the active native grid: {counts}")
    output.mkdir(parents=True, exist_ok=True)
    with (output / "native-utilities.tsv").open("w", encoding="utf-8", newline="\n") as stream:
        for prefix, height in (("wat", -8), ("pwr", 0)):
            for key, (x, z) in coordinates.items():
                stream.write(f"N\t{prefix}_{key}\t{x:.3f}\t{z:.3f}\t{height}\n")
        for prefix, tag in (("wat", "water"), ("pwr", "power")):
            for i, (a, b) in enumerate(edges):
                stream.write(f"E\t{prefix}_{i}\t{prefix}_{a}\t{prefix}_{b}\t{tag}\t0\t1\n")
    report = {"status": "planned stock utility geometry; service not yet verified", "writesToGame": False,
              "counts": counts, "stockBefore": stock, "distanceFromBuildings": metrics}
    (output / "native-utilities-plan.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--roads", type=Path, required=True)
    parser.add_argument("--native-buildings", type=Path, required=True)
    parser.add_argument("--native-summary", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(generate(args.roads, args.native_buildings, args.native_summary, args.output)))


if __name__ == "__main__":
    main()
