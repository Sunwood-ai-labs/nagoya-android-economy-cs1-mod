"""Build a connected, budgeted road plan from the Nagoya source graph."""

import argparse
import heapq
import json
import math
from collections import Counter, defaultdict
from pathlib import Path


FAMILY = {
    "motorway": 0, "motorway_link": 0, "trunk": 0, "trunk_link": 0,
    "primary": 0, "primary_link": 0, "secondary": 1, "secondary_link": 1,
    "tertiary": 2, "tertiary_link": 2, "unclassified": 3,
    "residential": 4, "living_street": 4, "service": 5,
}
SECTOR_M = 1250


def make_plan(source, build_span, segment_budget):
    bounds = [
        min(n["x"] for n in source["nodes"]), min(n["z"] for n in source["nodes"]),
        max(n["x"] for n in source["nodes"]), max(n["z"] for n in source["nodes"]),
    ]
    scale = min(1.0, build_span / max(bounds[2] - bounds[0], bounds[3] - bounds[1]))
    center = ((bounds[0] + bounds[2]) / 2, (bounds[1] + bounds[3]) / 2)

    def project(point):
        return [round((point["x"] - center[0]) * scale, 3), round((point["z"] - center[1]) * scale, 3)]

    coordinates = {n["id"]: (n["x"], n["z"]) for n in source["nodes"]}
    edges, adjacency = [], defaultdict(list)
    omissions = Counter()
    for row in source["edges"]:
        family = FAMILY.get(row["kind"])
        if family is None or row["tags"].get("access") in ("no", "private"):
            omissions["unsupported_or_restricted_road_type"] += 1
            continue
        a, b = row["fromNode"], row["toNode"]
        points = [project(point) for point in row["points"]]
        if a == b or len(points) < 2 or points[0] == points[-1]:
            omissions["loop_or_zero_length"] += 1
            continue
        subdivisions = sum(max(1, math.ceil(math.dist(p, q) / 80)) for p, q in zip(points, points[1:]))
        edge = (family, subdivisions, row, points)
        i = len(edges)
        edges.append(edge)
        adjacency[a].append(i)
        adjacency[b].append(i)

    if not edges:
        raise ValueError("No eligible source roads")
    visited = set()
    largest = set()
    for node in adjacency:
        if node in visited:
            continue
        component = {node}
        visited.add(node)
        stack = [node]
        while stack:
            for idx in adjacency[stack.pop()]:
                edge = edges[idx][2]
                for key in ("fromNode", "toNode"):
                    next_node = edge[key]
                    if next_node not in visited:
                        visited.add(next_node)
                        component.add(next_node)
                        stack.append(next_node)
        if len(component) > len(largest):
            largest = component

    def sector(node):
        x, z = coordinates[node]
        return math.floor(x / SECTOR_M), math.floor(z / SECTOR_M)

    def anchor_key(node):
        x, z = coordinates[node]
        sx, sz = sector(node)
        return (min(edges[i][0] for i in adjacency[node]),
                (x - (sx + .5) * SECTOR_M) ** 2 + (z - (sz + .5) * SECTOR_M) ** 2, node)

    anchors = {}
    for node in largest:
        key = sector(node)
        if key not in anchors or anchor_key(node) < anchor_key(anchors[key]):
            anchors[key] = node
    start = min(largest, key=lambda n: (coordinates[n][0] ** 2 + coordinates[n][1] ** 2, n))
    remaining = set(anchors.values())
    distance, previous, reachable = {start: 0}, {}, [(0, start)]
    while reachable and remaining:
        travelled, node = heapq.heappop(reachable)
        if travelled != distance[node]:
            continue
        remaining.discard(node)
        for i in adjacency[node]:
            family, _, row, _ = edges[i]
            other = row["toNode"] if node == row["fromNode"] else row["fromNode"]
            estimate = travelled + row["lengthMeters"] * (1 + .35 * family)
            if estimate < distance.get(other, math.inf):
                distance[other] = estimate
                previous[other] = (node, i)
                heapq.heappush(reachable, (estimate, other))

    backbone = set()
    for node in anchors.values():
        while node != start:
            node, i = previous[node]
            if i in backbone:
                break
            backbone.add(i)
    backbone_cost = sum(edges[i][1] for i in backbone)
    if backbone_cost > segment_budget:
        raise ValueError(f"Connecting the source sectors requires {backbone_cost} native segments")

    # Additional streets grow from the same connected road backbone.
    queued, connected, heap = set(), {start}, []

    def enqueue(node):
        for i in adjacency[node]:
            if i in queued:
                continue
            queued.add(i)
            family, cost, row, _ = edges[i]
            heapq.heappush(heap, (family, -row["lengthMeters"], cost, i))

    selected = []
    consumed = 0

    def accept(i):
        nonlocal consumed
        _, cost, row, points = edges[i]
        selected.append({
            "source": row["id"], "way": row["sourceWayId"], "kind": row["kind"],
            "from": row["fromNode"], "to": row["toNode"], "oneway": row["oneway"],
            "layer": row["layer"], "bridge": row["bridge"], "tunnel": row["tunnel"],
            "pedestrianAllowed": row["pedestrianAllowed"],
            "pointsXZ": points, "segmentBudget": cost,
        })
        consumed += cost
        for key in ("fromNode", "toNode"):
            node = row[key]
            if node not in connected:
                connected.add(node)
                enqueue(node)

    queued.update(backbone)
    enqueue(start)
    for i in sorted(backbone):
        accept(i)
    while heap and consumed < segment_budget:
        _, _, cost, i = heapq.heappop(heap)
        if consumed + cost <= segment_budget:
            accept(i)

    selected_points = [p for row in selected for p in row["pointsXZ"]]
    degree = Counter(node for row in selected for node in (row["from"], row["to"]))
    short = sum(
        math.dist(a, b) < 4
        for row in selected for a, b in zip(row["pointsXZ"], row["pointsXZ"][1:])
    )
    planned_bounds = [
        min(p[0] for p in selected_points), min(p[1] for p in selected_points),
        max(p[0] for p in selected_points), max(p[1] for p in selected_points),
    ]
    audit = {
        "status": "connected source-road plan, native construction unverified",
        "sourceRoadEdges": len(source["edges"]),
        "eligibleSurfaceRoadEdges": len(edges),
        "largestEligibleComponentNodes": len(largest),
        "plannedSourceRoadEdges": len(selected),
        "plannedConnectedSourceNodes": len(connected),
        "sourceRoadSectors": len({sector(node) for node in adjacency}),
        "connectedRoadSectors": len(anchors),
        "roadSectorSizeOriginalM": SECTOR_M,
        "estimatedBackboneSegments": backbone_cost,
        "estimatedNativeSegments": consumed,
        "geometryPartsUnderBridgeMinimum4M": short,
        "junctionsWithMoreThanEightEdges": sum(count > 8 for count in degree.values()),
        "reservedSegmentBudget": segment_budget,
        "originalBoundsXZ": bounds,
        "projectedPlanBoundsXZ": planned_bounds,
        "transform": {"scale": scale, "originalCenterXZ": center},
        "plannedRoadClasses": dict(Counter(row["kind"] for row in selected)),
        "excludedEdgesByReason": dict(omissions),
        "writesToGame": False,
    }
    return audit, selected


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--graph", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--build-span", type=float, default=9000)
    parser.add_argument("--segment-budget", type=int, default=16000)
    args = parser.parse_args()
    if not (0 < args.build_span <= 9600 and 0 < args.segment_budget <= 32000):
        parser.error("Expected a positive budget and a span within the CS1 25-tile grid")
    audit, rows = make_plan(json.loads(args.graph.read_text(encoding="utf-8-sig")),
                            args.build_span, args.segment_budget)
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "native-road-plan.json").write_text(
        json.dumps({"audit": audit, "edges": rows}, ensure_ascii=False, separators=(",", ":")) + "\n",
        encoding="utf-8",
    )
    (args.output / "native-road-plan-audit.json").write_text(
        json.dumps(audit, ensure_ascii=False, indent=2) + "\n", encoding="utf-8",
    )
    print(json.dumps(audit, ensure_ascii=False))


if __name__ == "__main__":
    main()
