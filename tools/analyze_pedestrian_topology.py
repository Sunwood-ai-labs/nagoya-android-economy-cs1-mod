import argparse
import csv
import hashlib
import heapq
import json
import math
from collections import defaultdict, deque
from pathlib import Path


def read_network(path):
    segments, positions = {}, {}
    with path.open(encoding="utf-8-sig", newline="") as stream:
        for row in csv.DictReader(stream, delimiter="\t"):
            key = int(row["segment"])
            first, last = int(row["startNode"]), int(row["endNode"])
            start = tuple(float(row[f"start{axis}"]) for axis in "XYZ")
            end = tuple(float(row[f"end{axis}"]) for axis in "XYZ")
            if key in segments or first == last or not all(map(math.isfinite, start + end)):
                raise ValueError(f"Invalid native network segment {key}")
            if positions.setdefault(first, start) != start or positions.setdefault(last, end) != end:
                raise ValueError(f"Inconsistent native endpoint for segment {key}")
            length = float(row["length"])
            if not math.isfinite(length) or length <= 0:
                raise ValueError(f"Invalid native length for segment {key}")
            segments[key] = (first, last, length, int(row["walkLanes"]), int(row["vehicleLanes"]), row["service"])
    if not segments:
        raise ValueError("The native network snapshot is empty")
    return segments, positions


def adjacency(segments, lane_index):
    graph = defaultdict(list)
    for row in segments.values():
        first, last, length = row[:3]
        if row[lane_index] and (lane_index != 4 or row[5] == "Road"):
            graph[first].append((last, length))
            graph[last].append((first, length))
    return graph


def components(graph):
    groups, members = {}, {}
    for start in sorted(graph):
        if start in groups:
            continue
        queue = deque([start])
        groups[start] = start
        members[start] = []
        for _ in range(len(graph)):
            if not queue:
                break
            node = queue.popleft()
            members[start].append(node)
            for neighbor, _ in graph[node]:
                if neighbor not in groups:
                    groups[neighbor] = start
                    queue.append(neighbor)
    return groups, members


def distances(graph, edge):
    first, last, length = edge[:3]
    half = length / 2
    scores = {first: half, last: half}
    queue = [(half, first), (half, last)]
    heapq.heapify(queue)
    for _ in range(2 * sum(map(len, graph.values())) + 2):
        if not queue:
            break
        cost, node = heapq.heappop(queue)
        if cost != scores[node]:
            continue
        for neighbor, length in graph.get(node, ()):
            candidate = cost + length
            if candidate < scores.get(neighbor, math.inf):
                scores[neighbor] = candidate
                heapq.heappush(queue, (candidate, neighbor))
    return scores


def eligible_junctions(scores, walk, street, positions, ground):
    return [node for node in scores
            if node in street and len(street[node]) <= 3 and len(walk[node]) <= 3
            and abs(positions[node][1] - ground) <= 0.5]


def candidate_links(start, target, walk, street, positions, ground, max_gap):
    start_dist, target_dist = distances(walk, start), distances(walk, target)
    local_start = eligible_junctions(start_dist, walk, street, positions, ground)
    local_target = eligible_junctions(target_dist, walk, street, positions, ground)
    bins = defaultdict(list)
    for node in local_target:
        x, _, z = positions[node]
        bins[(math.floor(x / max_gap), math.floor(z / max_gap))].append(node)
    candidates = []
    for source in local_start:
        x, _, z = positions[source]
        gx, gz = math.floor(x / max_gap), math.floor(z / max_gap)
        for dx in (-1, 0, 1):
            for dz in (-1, 0, 1):
                for destination in bins[(gx + dx, gz + dz)]:
                    length = math.dist(positions[source], positions[destination])
                    if 12 <= length <= max_gap:
                        candidates.append(dict(fromNode=source, toNode=destination,
                            gapMeters=round(length, 2), fromMeters=round(start_dist[source], 2),
                            toMeters=round(target_dist[destination], 2),
                            routeMeters=round(start_dist[source] + length + target_dist[destination], 2)))
    return sorted(candidates, key=lambda row: (row["routeMeters"], row["gapMeters"], row["fromNode"], row["toNode"]))[:12]


def analyze(network, trials, ground, max_gap):
    segments, positions = read_network(network)
    walk = adjacency(segments, 3)
    street = adjacency(segments, 4)
    walk_groups, walk_members = components(walk)
    street_groups, street_members = components(street)
    reports, seen = [], set()
    for path in trials:
        trial = json.loads(path.read_text(encoding="utf-8-sig"))
        for probe in trial["commuteRouteAudit"]["probes"]:
            a, b = probe["startSidewalkSegment"], probe["endSidewalkSegment"]
            key = (probe["citizenId"], a, b)
            if key in seen:
                continue
            seen.add(key)
            first, last = segments.get(a), segments.get(b)
            row = dict(citizenId=key[0], homeWard=probe.get("homeWard"), workWard=probe.get("workWard"),
                       startSegment=a, endSegment=b, stockResult=probe["stock"]["result"],
                       walkResult=probe["pedestrian"]["result"])
            reports.append(row)
            if not first or not last or not first[3] or not last[3]:
                row["topology"] = "missingSidewalk"
                continue
            source_group, target_group = walk_groups[first[0]], walk_groups[last[0]]
            row["walkComponents"] = [source_group, target_group]
            row["walkComponentNodes"] = [len(walk_members[group]) for group in row["walkComponents"]]
            row["sameRoadComponent"] = first[0] in street_groups and street_groups.get(first[0]) == street_groups.get(last[0])
            row["topology"] = "sharedWalkComponent" if source_group == target_group else "separateWalkComponents"
            if source_group != target_group and row["walkResult"] != "ready":
                row["shortGroundLinkCandidates"] = candidate_links(first, last, walk, street, positions, ground, max_gap)
    with network.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    return dict(snapshot=network.name, sha256=digest, totalSegments=len(segments),
                sidewalkSegments=sum(bool(row[3]) for row in segments.values()), pedestrianComponents=len(walk_members),
                vehicleRoadComponents=len(street_members), routes=reports)


def main():
    parser = argparse.ArgumentParser(description="Find geometric gaps in a live Cities: Skylines pedestrian network export.")
    parser.add_argument("network", type=Path)
    parser.add_argument("trials", nargs="+", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--ground", type=float, default=140)
    parser.add_argument("--max-gap", type=float, default=100)
    args = parser.parse_args()
    if not math.isfinite(args.ground) or not 12 <= args.max_gap <= 300:
        parser.error("Specify a finite ground height and a maximum gap from 12 to 300 meters.")
    result = analyze(args.network, args.trials, args.ground, args.max_gap)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
