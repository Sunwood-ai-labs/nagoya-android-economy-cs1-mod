"""Compile OSM streets that can attach to the existing native Nagoya network."""

import argparse
import json
import math
from collections import Counter, defaultdict, deque
from pathlib import Path

from shapely.geometry import LineString
from shapely.strtree import STRtree

from compile_native_city_roads import build_raw, _contract_short_links, _make_output_edges, class_rank
from plan_zoning_frontage_extension import read_tsv


def read_existing(plan, receipt):
    poses, segments = {}, []
    for line in plan.read_text(encoding="utf-8-sig").splitlines():
        row = line.split("\t")
        if row[0] in {"N", "A"}:
            poses[row[1]] = tuple(map(float, row[2:5]))
        else:
            segments.append(row)
    edges, degrees = [], Counter()
    for record in read_tsv(receipt):
        if not int(record["nativeId"]):
            continue
        _, source, a, b, _, _, forward = segments[int(record["ordinal"])]
        if source != record["source"]:
            raise ValueError("Road receipt and geometry do not match")
        if forward == "0":
            a, b = b, a
        start, end = ("live:" + record[k] for k in ("startNode", "endNode"))
        edges.append((start, end, poses[a], poses[b]))
        degrees.update((start, end))
    return edges, degrees


def add_base_network(existing, degrees, baseline, outward):
    if baseline:
        saved = json.loads(baseline.read_text(encoding="utf-8-sig"))
        if saved["total"] != saved["returned"]:
            raise ValueError("The stock map network read-back must be complete")
        for seg in saved["segments"]:
            if seg["service"] not in {"Road", "PublicTransport"}:
                continue
            a, b = (f'live:{seg[field]}' for field in ("startNodeId", "endNodeId"))
            m = f'base-middle:{seg["id"]}'
            points = [(seg[k]["x"], seg[k]["z"], seg[k]["y"] - 140) for k in ("start", "middle", "end")]
            for left, right, p, q in ((a, m, points[0], points[1]), (m, b, points[1], points[2])):
                if p[:2] != q[:2]:
                    existing.append((left, right, p, q))
            degrees.update((a, b))
    if outward:
        for row in json.loads(outward.read_text(encoding="utf-8-sig")):
            a, b = f'live:{row["startStockNode"]}', f'live:{row["endNativeNode"]}'
            x1, y1, z1 = row["startStockXYZ"]
            x2, y2, z2 = row["endPlanXYZ"]
            existing.append((a, b, (x1, z1, y1 - 140), (x2, z2, y2 - 140)))
            degrees.update((a, b))


def graph_with_anchors(proposal):
    anchored = {source: f'live:{record["nodeId"]}' for source, record in proposal["anchors"].items()}
    anchors = {anchored[source]: record for source, record in proposal["anchors"].items()}
    edges = []
    for road in proposal["streets"]:
        a, b = road["fromNode"], road["toNode"]
        a, b = anchored.get(a, a), anchored.get(b, b)
        points = road["pointsXZ"].copy()
        if a in anchors:
            points[0] = anchors[a]["pose"][:2]
        if b in anchors:
            points[-1] = anchors[b]["pose"][:2]
        edges.append({"source": road["source"], "from": a, "to": b, "kind": road["kind"], "pointsXZ": points})
    nodes, raw, _, _ = build_raw({"edges": edges})
    fixed = {node.node_id: node.source_id for node in nodes if node.source_id in anchors}
    dsu, contractions, short = _contract_short_links(nodes, raw, set(fixed))
    output, _ = _make_output_edges(dsu, raw, short)
    def key(node):
        root = dsu.find(node)
        return fixed.get(root, f"local{root:05d}")

    poses = {}
    for edge in output:
        for node in (edge.start, edge.end):
            k = key(node)
            x, z = dsu.coordinate(dsu.find(node))
            poses[k] = round(x, 3), round(z, 3), anchors[k]["pose"][2] if k in anchors else 0.
    segments = [(edge.source, key(edge.start), key(edge.end), edge.tag, edge.kind) for edge in output]
    return poses, segments, anchors, contractions


def geometry(a, b):
    return LineString((a[:2], b[:2]))


def overlaps(first, first_line, second, second_line):
    shared = set(first[:2]) & set(second[:2])
    meeting = first_line.intersection(second_line)
    if meeting.is_empty:
        return False
    if meeting.geom_type == "Point" and shared:
        p0 = first[3] if first[0] in shared else first[2]
        p1 = second[3] if second[0] in shared else second[2]
        a = p0[0] - meeting.x, p0[1] - meeting.y
        b = p1[0] - meeting.x, p1[1] - meeting.y
        return a[0] * b[0] + a[1] * b[1] > .95 * math.hypot(*a) * math.hypot(*b)
    if meeting.geom_type != "Point":
        return True

    def height(points, line):
        t = line.project(meeting, normalized=True)
        return points[2][2] * (1 - t) + points[3][2] * t

    return abs(height(first, first_line) - height(second, second_line)) < 5.05


def retain_streets(poses, planned, existing, degrees, anchors, occupied=None):
    base_lines = [geometry(a, b) for _, _, a, b in existing]
    base_tree = STRtree(base_lines)
    rows = [(a, b, poses[a], poses[b]) for _, a, b, _, _ in planned]
    lines = [geometry(a, b) for _, _, a, b in rows]
    tree = STRtree(lines)
    keep, skipped = set(), Counter()
    for i in sorted(range(len(rows)), key=lambda i: (-class_rank(planned[i][4]), -lines[i].length, planned[i][0])):
        a, b, p, q = rows[i]
        if lines[i].length < 4.002 or abs(p[2] - q[2]) > lines[i].length * .16:
            skipped["grade_or_length"] += 1
        elif degrees[a] >= 8 or degrees[b] >= 8:
            skipped["native_junction_capacity"] += 1
        elif occupied is not None and len(occupied.query(
                lines[i].buffer(18 if planned[i][3] == "arterial" else 10, cap_style="flat"),
                predicate="intersects")):
            skipped["occupied_building_frontage"] += 1
        elif any(overlaps(rows[i], lines[i], existing[k], base_lines[k]) for k in base_tree.query(lines[i])):
            skipped["impinges_on_installed_road"] += 1
        elif any(overlaps(rows[i], lines[i], rows[k], lines[k]) for k in tree.query(lines[i]) if k in keep):
            skipped["impinges_on_another_extension"] += 1
        else:
            keep.add(i)
            degrees.update((a, b))
    adj = defaultdict(list)
    for i in keep:
        a, b = rows[i][:2]
        adj[a].append(b)
        adj[b].append(a)
    connected = anchors.keys() & adj.keys()
    queue = deque(connected)
    for _ in range(len(adj)):
        if not queue:
            break
        for other in adj[queue.popleft()]:
            if other not in connected:
                queue.append(other)
                connected.add(other)
    retained = [planned[i] for i in sorted(keep) if rows[i][0] in connected]
    skipped["disconnected_after_clearance"] = len(keep) - len(retained)
    return retained, skipped


def occupied_footprints(inventories):
    if not inventories:
        return None
    from plan_native_facility_frontage import footprint

    footprints = []
    for path in inventories:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        rows = data["growables"] if "growables" in data else data["facilities"]
        if not data["ok"] or data["returned"] != data["total"] or len(rows) != data["total"]:
            raise ValueError(f"Incomplete native building inventory: {path.name}")
        footprints.extend(footprint(row) for row in rows)
    return STRtree(footprints)


def run(args):
    proposal = json.loads(args.proposals.read_text(encoding="utf-8"))
    poses, planned, anchors, contracted = graph_with_anchors(proposal)
    existing, degrees = read_existing(args.existing, args.receipt)
    for plan, receipt in args.additional_import:
        imported, imported_degrees = read_existing(plan, receipt)
        existing.extend(imported)
        degrees.update(imported_degrees)
    add_base_network(existing, degrees, args.baseline_network, args.outward_links)
    selected, skipped = retain_streets(poses, planned, existing, degrees, anchors,
                                      occupied_footprints(args.occupied))
    if not selected:
        raise ValueError("No connected extension streets passed geometry checks")
    used = {node for _, a, b, _, _ in selected for node in (a, b)}
    source_ids = {row[0] for row in selected}
    groups = [row for row in proposal["groups"] if row["targetSource"] in source_ids]
    with args.output.open("w", encoding="utf-8", newline="\n") as out:
        for node in sorted(used):
            x, z, y = poses[node]
            if node in anchors:
                anchor = anchors[node]
                out.write(f'A\t{node.replace(":", "_")}\t{x:.3f}\t{z:.3f}\t{y:.3f}\t'
                          f'{anchor["nodeId"]}\t{anchor["receiptSegment"]}\t{anchor["receiptBuildIndex"]}\n')
            else:
                out.write(f'N\t{node}\t{x:.3f}\t{z:.3f}\t{y:.3f}\n')
        for source, a, b, tag, _ in selected:
            out.write(f'E\t{source}\t{a.replace(":", "_")}\t{b.replace(":", "_")}\t{tag}\t0\t1\n')
    print(json.dumps({"compiledSegments": len(selected), "checkedExistingCenterlines": len(existing), "nativeJunctionsReused": len(used & anchors.keys()),
                      "newJunctions": len(used - anchors.keys()), "skipped": skipped, "contractions": contracted,
                      "groupsWithRetainedStreet": len(groups), "wards": dict(Counter(r["ward"] for r in groups)),
                      "roleMembershipsWithRetainedStreet": sum(r["sourceMemberships"] for r in groups),
                      "writesToGame": False}, ensure_ascii=False))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ("proposals", "existing", "receipt", "output"):
        parser.add_argument("--" + option, type=Path, required=True)
    parser.add_argument("--additional-import", nargs=2, type=Path, action="append", default=[],
                        metavar=("PLAN", "RECEIPT"), help="Also check clearance against this imported road network")
    parser.add_argument("--occupied", type=Path, action="append", default=[],
                        help="Complete native growables or facilities snapshot for building clearance")
    parser.add_argument("--baseline-network", type=Path)
    parser.add_argument("--outward-links", type=Path)
    run(parser.parse_args())
