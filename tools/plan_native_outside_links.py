"""Find approach roads from the saved map's exterior network to the Nagoya plan."""

import argparse
import json
import math
from collections import Counter
from pathlib import Path

from normalize_native_city_grades import crossing


def shape(point):
    return point["x"], point["z"], point["y"]


def read_plan(path):
    points, segments, local = {}, [], set()
    for line in path.read_text(encoding="utf-8").splitlines():
        identifier, *data = line.split("\t")
        if identifier == "N":
            node, x, z, dy = data
            points[node] = float(x), float(z), 140 + float(dy)
        elif identifier == "E":
            _, a, b, tag, _, _ = data
            segments.append((points[a], points[b]))
            if tag in ("basic", "arterial") and abs(points[a][2]-140) < .05 and abs(points[b][2]-140) < .05:
                local.update((a, b))
    return points, segments, local


def find_links(plan, stock):
    nodes, segments, street_nodes = read_plan(plan)
    ends, degree = {}, Counter()
    preexisting = []
    for row in stock["segments"]:
        if row["service"] != "Road":
            continue
        a, b = row["startNodeId"], row["endNodeId"]
        start, mid, end = (shape(row[k]) for k in ("start", "middle", "end"))
        degree.update((a, b))
        ends[a] = (start, "outbound")
        ends[b] = (end, "inbound")
        preexisting.extend(((start, mid), (mid, end)))
    crossing_context = segments + preexisting

    def has_low_crossing(start, end):
        minx, maxx = sorted((start[0], end[0]))
        minz, maxz = sorted((start[1], end[1]))
        for a, b in crossing_context:
            if max(a[0], b[0]) < minx or min(a[0], b[0]) > maxx or max(a[1], b[1]) < minz or min(a[1], b[1]) > maxz:
                continue
            hit = crossing(start, end, a, b)
            if hit is not None:
                t, s = hit
                y1 = start[2] + (end[2] - start[2]) * t
                y2 = a[2] + (b[2] - a[2]) * s
                if abs(y1 - y2) < 5:
                    return True
        return False

    proposals = []
    for stock_id, (point, direction) in ends.items():
        if degree[stock_id] != 1 or max(abs(point[0]), abs(point[1])) > 4250:
            continue
        suburbs = []
        for key in street_nodes:
            goal = nodes[key]
            distance = math.dist(point[:2], goal[:2])
            if 12 <= distance <= 320 and abs(point[2] - goal[2]) / distance <= .13:
                suburbs.append((distance, key))
        for distance, key in sorted(suburbs)[:40]:
            goal = nodes[key]
            if has_low_crossing(point, goal):
                continue
            proposals.append({
                "startStockNode": stock_id, "startStockXYZ": (point[0], point[2], point[1]),
                "endPlanKey": key, "endPlanXYZ": (goal[0], goal[2], goal[1]),
                "stockConnectionDirection": direction, "distanceM": round(distance, 3),
                "incline": round(abs(point[2] - goal[2]) / distance, 4),
            })
            break
    return sorted(proposals, key=lambda x: (x["distanceM"], x["startStockNode"]))


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--plan", type=Path, required=True)
    p.add_argument("--stock", type=Path, required=True)
    p.add_argument("--output", type=Path, required=True)
    args = p.parse_args()
    stock = json.loads(args.stock.read_text(encoding="utf-8-sig"))
    candidates = find_links(args.plan, stock)
    report = {
        "status": "native outside connection geometry candidates; lane links and traffic unverified",
        "snapshotRoadCount": sum(s["service"] == "Road" for s in stock["segments"]),
        "candidateCount": len(candidates), "connections": candidates, "writesToGame": False,
    }
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"candidateCount":len(candidates),"shortest":candidates[:2]}, ensure_ascii=False))


if __name__ == "__main__":
    main()
