"""Make connected CS1 roads continuous at bridge and tunnel transitions."""

import argparse
import heapq
import json
import math
from collections import Counter, defaultdict
from pathlib import Path


def gradient_envelope(values, graph, limit):
    envelope = values.copy()
    queue = [(height, node) for node, height in values.items()]
    heapq.heapify(queue)
    while queue:
        altitude, node = heapq.heappop(queue)
        if altitude != envelope[node]:
            continue
        for end, length in graph[node]:
            adjusted = altitude + length * limit
            if adjusted < envelope[end]:
                envelope[end] = adjusted
                heapq.heappush(queue, (adjusted, end))
    return envelope


def crossing(a, b, c, d):
    ux, uz = b[0] - a[0], b[1] - a[1]
    vx, vz = d[0] - c[0], d[1] - c[1]
    wx, wz = c[0] - a[0], c[1] - a[1]
    determinant = ux * vz - uz * vx
    if abs(determinant) < 1e-8:
        return None
    t = (wx * vz - wz * vx) / determinant
    s = (wx * uz - wz * ux) / determinant
    return (t, s) if .001 < t < .999 and .001 < s < .999 else None


def iter_crossings(points, edges):
    grid = defaultdict(list)
    for i, (_, start, end, _, _, _) in enumerate(edges):
        p, q = points[start], points[end]
        x0, x1 = sorted((p[0], q[0]))
        z0, z1 = sorted((p[1], q[1]))
        keys = [(x, z) for x in range(math.floor(x0 / 96), math.floor(x1 / 96) + 1)
                for z in range(math.floor(z0 / 96), math.floor(z1 / 96) + 1)]
        neighbors = {n for key in keys for n in grid[key]}
        for previous in sorted(neighbors):
            _, a, b, _, _, _ = edges[previous]
            if start in (a, b) or end in (a, b):
                continue
            hit = crossing(p, q, points[a], points[b])
            if hit is None:
                continue
            t, s = hit
            yield i, previous, t, s
        for key in keys:
            grid[key].append(i)


def check_crossings(points, edges, final):
    low_clearance = []
    source_low_clearance = 0
    for i, previous, t, s in iter_crossings(points, edges):
        source, start, end, _, _, _ = edges[i]
        other, a, b, _, _, _ = edges[previous]
        p, q = points[start], points[end]
        now = abs(final[start] * (1 - t) + final[end] * t - final[a] * (1 - s) - final[b] * s)
        prior = abs(p[2] * (1 - t) + q[2] * t - points[a][2] * (1 - s) - points[b][2] * s)
        source_low_clearance += prior < 5
        if now < 5:
            low_clearance.append({
                "first": source, "second": other, "firstOrdinal": i, "secondOrdinal": previous,
                "x": round(p[0] * (1 - t) + q[0] * t, 3), "z": round(p[1] * (1 - t) + q[1] * t, 3),
                "clearanceM": round(now, 3), "priorClearanceM": round(prior, 3),
            })
    return source_low_clearance, low_clearance


def road_tag(tag, start, end):
    if abs(start) < .01 and abs(end) < .01:
        if tag.startswith("highway"):
            return "highway"
        return "arterial" if tag == "arterial" else "basic"
    prefix = "highway_" if tag.startswith("highway") else "arterial_" if tag == "arterial" else ""
    if start < -.5 and end < -.5:
        return prefix + "tunnel"
    if start > 1 and end > 1:
        return prefix + ("bridge" if tag == "bridge" else "elevated")
    return prefix + "slope"


def normalize(node_file, segment_file, output, max_grade):
    points = {}
    for row in node_file.read_text(encoding="utf-8").splitlines():
        kind, key, x, z, y = row.split("\t")
        if kind != "N" or key in points:
            raise ValueError(f"Invalid or duplicate node {key}")
        points[key] = float(x), float(z), float(y)
    graph, edges = defaultdict(list), []
    for row in segment_file.read_text(encoding="utf-8").splitlines():
        kind, source, start, end, tag, layer, forward = row.split("\t")
        if kind != "E" or start == end:
            raise ValueError(f"Invalid segment {source}")
        length = math.dist(points[start][:2], points[end][:2])
        if length < 4:
            raise ValueError(f"Short native segment {source}: {length}")
        graph[start].append((end, length))
        graph[end].append((start, length))
        edges.append((source, start, end, tag, layer, forward))

    old = {key: p[2] for key, p in points.items()}
    hi = gradient_envelope(old, graph, max_grade - .002)
    lo = gradient_envelope({key: -y for key, y in old.items()}, graph, max_grade - .002)
    final = {key: round((hi[key] - lo[key]) / 2, 3) for key in points}
    old_max = new_max = 0.
    tags = Counter()
    for _, start, end, tag, _, _ in edges:
        length = math.dist(points[start][:2], points[end][:2])
        old_max = max(old_max, abs(old[start] - old[end]) / length)
        new_max = max(new_max, abs(final[start] - final[end]) / length)
        tags[road_tag(tag, final[start], final[end])] += 1
    if new_max > max_grade:
        raise ValueError("Normalized output exceeds declared maximum grade")
    prior_crossings, conflicts = check_crossings(points, edges, final)
    report = {
        "status": "graded_candidate_with_clearance_conflicts" if conflicts else "graded_candidate",
        "nodes": len(points), "segments": len(edges), "nativePrefabTags": dict(tags),
        "nodeHeightsChanged": sum(final[k] != old[k] for k in points),
        "priorMaximumGrade": round(old_max, 5), "newMaximumGrade": round(new_max, 5),
        "previousCrossingsBelow5M": prior_crossings, "crossingsBelow5M": len(conflicts), "writesToGame": False,
    }
    destination = output / "native-roads-graded.tsv"
    with destination.open("w", encoding="utf-8", newline="\n") as out:
        for key, (x, z, _) in points.items():
            out.write(f"N\t{key}\t{x:.3f}\t{z:.3f}\t{final[key]:.3f}\n")
        for source, start, end, tag, layer, forward in edges:
            out.write(f"E\t{source}\t{start}\t{end}\t{road_tag(tag, final[start], final[end])}\t{layer}\t{forward}\n")
    (output / "native-road-clearance-conflicts.json").write_text(
        json.dumps(conflicts, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    (output / "native-road-grades-audit.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--nodes", type=Path, required=True)
    parser.add_argument("--segments", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--max-grade", type=float, default=.16)
    args = parser.parse_args()
    if not .05 <= args.max_grade <= .25:
        parser.error("Expected a maximum grade between 5% and 25%")
    print(json.dumps(normalize(args.nodes, args.segments, args.output, args.max_grade), ensure_ascii=False))


if __name__ == "__main__":
    main()
