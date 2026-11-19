"""Plan local OSM approaches to Nagoya zoning groups still without road frontage."""

import argparse
import heapq
import json
import math
from collections import Counter, defaultdict
from pathlib import Path

from plan_plateau_native_blocks import ZONING_ROADS
from plan_zoning_frontage_extension import index_points, read_tsv, within


def road_anchors(compiled, receipt, source_edges, project):
    nodes, edges = {}, []
    with compiled.open(encoding="utf-8-sig") as source:
        for line in source:
            part, key, *fields = line.rstrip("\n").split("\t")
            if part in {"N", "A"}:
                nodes[key] = tuple(map(float, fields[:3]))
            else:
                a, b = fields[:2]
                if fields[-1] == "0":
                    a, b = b, a
                edges.append((key, a, b))
    anchors = {}
    seen_roads = set()
    for row in read_tsv(receipt):
        if not int(row["nativeId"]):
            continue
        edge_id, a, b = edges[int(row["ordinal"])]
        if row["source"] != edge_id:
            raise ValueError("Compiled road source and native receipt differ")
        seen_roads.add(edge_id)
        edge = source_edges.get(edge_id)
        if edge is None or row["prefabTag"] not in {"basic", "arterial"}:
            continue
        for native, key in ((int(row["startNode"]), a), (int(row["endNode"]), b)):
            x, z, y = nodes[key]
            if abs(y) > .4:
                continue
            for field, point in (("fromNode", edge["points"][0]), ("toNode", edge["points"][-1])):
                distance = math.dist((x, z), project(point))
                original = edge[field]
                if distance <= 4 and distance < anchors.get(original, {}).get("distanceM", math.inf):
                    anchors[original] = {"nodeId": native, "pose": [x, z, y], "distanceM": distance,
                                         "receiptSegment": int(row["nativeId"]), "receiptBuildIndex": int(row["buildIndex"])}
    if not anchors:
        raise ValueError("No original street junctions match the constructed road receipt")
    return anchors, seen_roads


def clearance_filter(imports, anchors, occupied, baseline, outward, rejected):
    from shapely.geometry import LineString
    from shapely.strtree import STRtree
    from compile_native_city_roads import prefab_tag
    from compile_native_local_road_extension import add_base_network, geometry, occupied_footprints, overlaps, read_existing

    installed, degrees = [], Counter()
    for geometry_file, receipt in imports:
        lines, nodes = read_existing(geometry_file, receipt)
        installed.extend(lines)
        degrees.update(nodes)
    add_base_network(installed, degrees, baseline, outward)
    outlines = occupied_footprints(occupied)
    centerlines = [geometry(a, b) for _, _, a, b in installed]
    index = STRtree(centerlines)

    def permits(edge, project):
        a, b = edge["fromNode"], edge["toNode"]
        ends = [anchors.get(a), anchors.get(b)]
        ids = [f'live:{end["nodeId"]}' if end else key for end, key in zip(ends, (a, b))]
        poses = [project(point) for point in edge["points"]]
        for i, end in zip((0, -1), ends):
            if end:
                poses[i] = end["pose"][:2]
        shape = LineString(poses)
        if shape.length < 1:
            rejected["short_source"] += 1
            return False
        span = 18 if prefab_tag(edge, "surface") == "arterial" else 10
        if outlines is not None and len(outlines.query(shape.buffer(span, cap_style="flat"), predicate="intersects")):
            rejected["occupied_frontage"] += 1
            return False
        ends = (*ids, (*poses[0], 0.), (*poses[-1], 0.))
        if any(overlaps(ends, shape, installed[k], centerlines[k]) for k in index.query(shape, predicate="intersects")):
            rejected["installed_road_overlap"] += 1
            return False
        return True

    return permits


def local_routes(edges, anchors, existing, project, permits=None):
    adjacency = defaultdict(list)
    streets, midpoints = [], []
    for edge in edges.values():
        if edge["id"] in existing or edge["kind"] not in ZONING_ROADS or not edge["pedestrianAllowed"] or \
                edge["layer"] != "0" or edge["bridge"] != "no" or edge["tunnel"] != "no":
            continue
        a, b = [project(p) for p in (edge["points"][0], edge["points"][-1])]
        if max(map(abs, (*a, *b))) >= 4680 or a == b:
            continue
        if permits and not permits(edge, project):
            continue
        start, end = edge["fromNode"], edge["toNode"]
        number = len(streets)
        distance = math.dist(a, b)
        streets.append((edge, start, end, max(1, math.ceil(distance / 80))))
        midpoints.append(tuple((left + right) / 2 for left, right in zip(a, b)))
        adjacency[start].append((end, number, distance))
        adjacency[end].append((start, number, distance))
    cost = dict.fromkeys(anchors, 0.)
    queue = [(0., node) for node in anchors]
    heapq.heapify(queue)
    previous = {}
    for _ in range(len(streets) * 2 + len(anchors)):
        if not queue:
            break
        distance, current = heapq.heappop(queue)
        if distance != cost[current]:
            continue
        for neighbor, street, length in adjacency[current]:
            candidate = distance + length
            if candidate < cost.get(neighbor, math.inf):
                cost[neighbor] = candidate
                previous[neighbor] = (current, street)
                heapq.heappush(queue, (candidate, neighbor))
    if queue:
        raise ValueError("Local road route search exceeded the static graph budget")
    return streets, midpoints, cost, previous


def run(args):
    def project(p):
        return (p["x"] - args.center_x) * args.scale, (p["z"] - args.center_z) * args.scale

    original = json.loads(args.source.read_text(encoding="utf-8-sig"))
    edges = {e["id"]: e for e in original["edges"]}
    anchors, existing = road_anchors(args.compiled, args.receipt, edges, project)
    for compiled, receipt in args.additional_import:
        additions, sources = road_anchors(compiled, receipt, edges, project)
        existing.update(sources)
        for key, anchor in additions.items():
            if key not in anchors or anchor["distanceM"] < anchors[key]["distanceM"]:
                anchors[key] = anchor
    excluded = Counter()
    permits = None
    if args.occupied or args.baseline_network or args.outward_links:
        permits = clearance_filter([(args.compiled, args.receipt), *args.additional_import], anchors,
                                   args.occupied, args.baseline_network, args.outward_links, excluded)
    streets, midpoints, access, previous = local_routes(edges, anchors, existing, project, permits)
    index = index_points(midpoints)
    applied = {r["group"] for r in read_tsv(args.built) if int(r["buildingId"]) > 0} if args.built else set()
    fulfilled = {r["roleGroup"] for r in read_tsv(args.links) if r["site"] in applied} if args.links else set()
    selected, roots, plans, wards = {}, set(), [], Counter()
    budget = args.budget
    for row in read_tsv(args.gaps):
        if row["group"] in fulfilled or not row["projected_centroid_x"]:
            continue
        center = float(row["projected_centroid_x"]), float(row["projected_centroid_z"])
        options = []
        for d2, number in within(index, midpoints, center, args.radius):
            _, a, b, _ = streets[number]
            endpoint = min((a, b), key=lambda node: access.get(node, math.inf))
            distance = access.get(endpoint, math.inf)
            if math.isfinite(distance):
                options.append((math.sqrt(d2) + distance * .15, number, endpoint))
        for _, street, current in sorted(options)[:8]:
            path = [street]
            for _ in range(len(previous) + 1):
                if current in anchors:
                    break
                current, approach = previous[current]
                path.append(approach)
            additions = set(path) - selected.keys()
            cost = sum(streets[number][3] for number in additions)
            if cost > budget:
                continue
            budget -= cost
            for number in additions:
                edge = streets[number][0]
                selected[number] = {"source": edge["id"], "fromNode": edge["fromNode"], "toNode": edge["toNode"],
                                    "kind": edge["kind"], "pointsXZ": [project(p) for p in edge["points"]]}
            roots.add(current)
            plans.append({"group": row["group"], "ward": row["ward"], "sourceMemberships": int(row["model_id_count"]),
                          "targetSource": streets[street][0]["id"], "anchor": current})
            wards[row["ward"]] += 1
            break
    report = {"writesToGame": False, "roadSourceEdges": len(selected), "estimatedSegments": args.budget - budget,
              "existingJunctionMatches": len(anchors), "plannedRoleGroups": len(plans), "wards": wards,
              "sourceRoleMemberships": sum(p["sourceMemberships"] for p in plans), "excludedBeforeRouting": excluded}
    args.output.write_text(json.dumps({"summary": report, "anchors": {k: anchors[k] for k in sorted(roots)},
                                      "streets": [selected[k] for k in sorted(selected)], "groups": plans},
                                     ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("source", "compiled", "receipt", "gaps", "output"):
        parser.add_argument("--" + name, required=True, type=Path)
    parser.add_argument("--built", type=Path)
    parser.add_argument("--links", type=Path)
    parser.add_argument("--additional-import", nargs=2, type=Path, action="append", default=[],
                        metavar=("PLAN", "RECEIPT"), help="Also reuse and exclude this imported road network")
    parser.add_argument("--occupied", type=Path, action="append", default=[])
    parser.add_argument("--baseline-network", type=Path)
    parser.add_argument("--outward-links", type=Path)
    for name in ("center-x", "center-z", "scale"):
        parser.add_argument("--" + name, required=True, type=float)
    parser.add_argument("--budget", type=int, default=5500)
    parser.add_argument("--radius", type=float, default=105)
    args = parser.parse_args()
    if bool(args.built) != bool(args.links):
        parser.error("Provide both the optional built-site receipt and source links")
    if not 0 < args.scale <= 1 or not 0 < args.budget <= 7000 or not 0 < args.radius <= 160:
        parser.error("Scale, street budget and frontage radius must fit the construction plan")
    run(args)


if __name__ == "__main__":
    main()
