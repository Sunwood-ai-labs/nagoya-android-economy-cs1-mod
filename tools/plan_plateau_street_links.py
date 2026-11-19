"""Find ground links between live walking networks along original OSM streets."""

import argparse
import heapq
import json
import math
from collections import defaultdict
from itertools import product
from pathlib import Path

from analyze_pedestrian_topology import adjacency, distances, read_network
from audit_plateau_road_clearance import Corridor, mesh_triangles


STREET_TYPES = {"residential", "living_street", "service", "unclassified", "tertiary", "secondary",
                "primary", "footway", "path", "pedestrian", "steps", "cycleway", "track"}


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def source_streets(source, project, bounds):
    left, bottom, right, top = bounds
    edges, links = {}, defaultdict(list)
    for edge in source["edges"]:
        if (edge["kind"] not in STREET_TYPES or not edge["pedestrianAllowed"] or
                edge["layer"] not in ("0", "") or edge["bridge"] not in ("no", "") or edge["tunnel"] not in ("no", "")):
            continue
        points = [project(p["x"], p["z"]) for p in edge["points"]]
        if not all(left <= x <= right and bottom <= z <= top for x, z in points):
            continue
        length = sum(math.dist(a, b) for a, b in zip(points, points[1:]))
        if not 1 <= length <= 240 or len(points) > 40:
            continue
        key, a, b = edge["id"], edge["fromNode"], edge["toNode"]
        edges[key] = dict(points=points, kind=edge["kind"], way=edge["sourceWayId"], endpoints=(a, b))
        links[a].append((b, length, key))
        links[b].append((a, length, key))
    return edges, links


def cells(x0, z0, x1, z1, size):
    return product(range(math.floor(x0 / size), math.floor(x1 / size) + 1),
                   range(math.floor(z0 / size), math.floor(z1 / size) + 1))


def find_obstructions(runtime, edges, project, scale, width):
    index = defaultdict(list)
    for key, edge in edges.items():
        for (x, z), (ex, ez) in zip(edge["points"], edge["points"][1:]):
            if math.hypot(ex - x, ez - z) < .01:
                continue
            corridor = Corridor(dict(start=dict(x=x, z=z), end=dict(x=ex, z=ez), halfWidth=width / 2))
            for cell in cells(*corridor.bounds, 16):
                index[cell].append((key, corridor))
    blocked = set()
    inspected = 0
    for model in runtime["models"]:
        if not model["active"]:
            continue
        cx, _, cz = model["boundsCenter"]
        cx, cz = project(cx, cz)
        w, _, d = model["bounds"]
        w, d = w * scale / 2, d * scale / 2
        if not any(cell in index for cell in cells(cx - w, cz - d, cx + w, cz + d, 16)):
            continue
        inspected += 1
        x, z = project(model["x"], model["z"])
        for triangle in mesh_triangles(model):
            tx, tz = zip(*triangle)
            covered = cells(min(tx) * scale + x, min(tz) * scale + z,
                            max(tx) * scale + x, max(tz) * scale + z, 16)
            for key, corridor in {entry for cell in covered for entry in index.get(cell, ())}:
                if key not in blocked and corridor.intersects(triangle, x, z, scale):
                    blocked.add(key)
    return blocked, inspected


def anchors(source, links, project, positions, street, scores):
    available = defaultdict(list)
    for n, distance in scores.items():
        x, y, z = positions[n]
        if distance <= 690 and abs(y - 140) <= .4 and n in street and len(street[n]) <= 4:
            available[math.floor(x / 3), math.floor(z / 3)].append((n, distance))
    found = {}
    for node in source["nodes"]:
        if node["id"] not in links:
            continue
        x, z = project(node["x"], node["z"])
        options = []
        for cell in cells(x - 1.3, z - 1.3, x + 1.3, z + 1.3, 3):
            for n, distance in available.get(cell, ()):
                px, _, pz = positions[n]
                gap = math.hypot(px - x, pz - z)
                if gap <= 1.3:
                    options.append((gap + distance, n, distance, gap))
        if options:
            _, n, distance, gap = min(options)
            found[node["id"]] = n, distance, gap
    return found


def routes(links, origins, destinations, blocked, maximum_total=990, maximum_new=325):
    result = []
    max_extractions = sum(map(len, links.values())) + 1
    for start, (native_from, before, from_gap) in origins.items():
        best = {start: 0.0}
        previous = {}
        queue = [(0.0, start)]
        for _ in range(max_extractions):
            if not queue:
                break
            cost, node = heapq.heappop(queue)
            if cost != best[node] or (node != start and node in origins):
                continue
            if node in destinations:
                native_to, after, to_gap = destinations[node]
                total = before + cost + after
                if cost >= 10 and total <= maximum_total:
                    path = []
                    for _ in range(len(best)):
                        if node == start:
                            break
                        node, edge = previous[node]
                        path.append(edge)
                    result.append(dict(fromNode=native_from, toNode=native_to, newLength=round(cost, 3),
                                       totalLength=round(total, 3), anchorGaps=[round(from_gap, 3), round(to_gap, 3)],
                                       sourceEdges=path[::-1]))
                continue
            for nearest, length, key in links[node]:
                next_cost = cost + length
                if key not in blocked and next_cost <= maximum_new and next_cost < best.get(nearest, math.inf):
                    best[nearest] = next_cost
                    previous[nearest] = node, key
                    heapq.heappush(queue, (next_cost, nearest))
    return sorted(result, key=lambda route: (route["totalLength"], route["fromNode"], route["toNode"]))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source_graph", type=Path)
    parser.add_argument("road_plan", type=Path)
    parser.add_argument("native_network", type=Path)
    parser.add_argument("scenery_runtime", type=Path)
    parser.add_argument("--segments", nargs=2, type=int, required=True)
    parser.add_argument("--bounds", nargs=4, type=float, required=True, metavar=("MIN_X", "MIN_Z", "MAX_X", "MAX_Z"))
    parser.add_argument("--width", type=float, required=True, help="full width in game meters")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    left, bottom, right, top = args.bounds
    if not (0 < args.width <= 32 and 0 < right - left <= 3000 and 0 < top - bottom <= 3000):
        parser.error("Provide a positive width and a street search window no larger than 3 km per side")
    source, runtime = read_json(args.source_graph), read_json(args.scenery_runtime)
    transform = read_json(args.road_plan)["audit"]["transform"]
    scale = float(transform["scale"])
    ox, oz = transform["originalCenterXZ"]
    if not all(map(math.isfinite, (scale, ox, oz))) or scale <= 0:
        parser.error("The road plan must use a finite, positive projection")

    def project(x, z):
        return (x - ox) * scale, (z - oz) * scale

    native, positions = read_network(args.native_network)
    walk, street = adjacency(native, 3), adjacency(native, 4)
    if any(s not in native or not native[s][3] for s in args.segments):
        parser.error("Select two segments with live pedestrian lanes")
    home, work = (native[s] for s in args.segments)
    home_distances, work_distances = distances(walk, home), distances(walk, work)
    if work[0] in home_distances:
        parser.error("The two segments already share a walking network by node")
    edges, links = source_streets(source, project, args.bounds)
    first = anchors(source, links, project, positions, street, home_distances)
    last = anchors(source, links, project, positions, street, work_distances)
    if first.keys() & last.keys():
        parser.error("Select endpoints in separate walking networks without a shared source anchor")
    blocked, inspected = find_obstructions(runtime, edges, project, scale, args.width)
    proposed = routes(links, first, last, blocked)
    used = {key for link in proposed for key in link["sourceEdges"]}
    result = dict(scene=runtime["revision"], proposedTransform=transform, fullWidth=args.width, bounds=args.bounds,
                  segmentIds=args.segments, sourceCount=len(edges), blockedCount=len(blocked), inspectedMeshes=inspected,
                  anchorCounts=[len(first), len(last)], candidateCount=len(proposed), routes=proposed,
                  sourceEdges={key: edges[key] for key in sorted(used)})
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(result, output, ensure_ascii=False, indent=2)
        output.write("\n")


if __name__ == "__main__":
    main()
