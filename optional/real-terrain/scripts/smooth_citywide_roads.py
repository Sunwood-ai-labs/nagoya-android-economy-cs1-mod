"""Simplify native road geometry while preserving the planned junction topology."""
from collections import defaultdict
import json
import math
import numpy as np

from nagoya_citywide import terrain_height
from nagoya_map import ROOT

SURFACE_PREFABS = {"Basic Road", "Medium Road"}
RAMP_PREFABS = {
    "Basic Road": "HighwayRamp", "Highway": "HighwayRamp",
    "Basic Road Bridge": "HighwayRampElevated", "Highway Elevated": "HighwayRampElevated",
    "Basic Road Tunnel": "HighwayRamp Tunnel", "Highway Tunnel": "HighwayRamp Tunnel",
    "Highway Slope": "HighwayRampElevated", "Basic Road Slope": "HighwayRampElevated",
}
PLAN = ROOT.parent / "citywide-zoning-v2" / "native-road-plan.json"


def prepare_smooth_roads(roads, raw, ramp_profiles=False):
    sources = {row["source"]: row for row in json.loads(
        PLAN.read_text(encoding="utf-8-sig"))["edges"]}
    by_node = defaultdict(list)
    points = {}
    heights = np.frombuffer(raw, dtype="<u2").reshape(1081, 1081) / 64
    fitted_prefabs = SURFACE_PREFABS | ({"HighwayRamp", "HighwayRampElevated"} if ramp_profiles else set())
    narrowed = 0
    ramp_segments = 0
    for i, road in enumerate(roads):
        a, b = road["plannedStart"], road["plannedEnd"]
        source = sources.get(road["osmSource"], {})
        if road["roadPrefab"] == "Medium Road":
            if source.get("kind") in {"tertiary", "residential", "living_street", "unclassified"}:
                road["roadPrefab"] = "Basic Road"
                narrowed += 1
        if ramp_profiles and source.get("kind") == "motorway_link" and source.get("oneway") == "yes":
            original = road["roadPrefab"]
            if source.get("tunnel") == "yes" and original.endswith(" Slope"):
                road["roadPrefab"] = "HighwayRamp Slope"
            else:
                road["roadPrefab"] = RAMP_PREFABS.get(original, original)
            ramp_segments += road["roadPrefab"] != original
        by_node[a].append(i)
        by_node[b].append(i)
        for tag, key in (("start", a), ("end", b)):
            pos = road[tag]
            height = terrain_height(heights, pos["x"], pos["z"], ramp_profiles)
            points[key] = (pos["x"], pos["z"], height + road[tag + "HeightOffset"])
    visited = set()
    out = []
    for i, road in enumerate(roads):
        if i in visited:
            continue
        prefab = road["roadPrefab"]
        if prefab not in fitted_prefabs:
            visited.add(i)
            out.append(road)
            continue
        chain = [road["plannedStart"], road["plannedEnd"]]
        visited.add(i)
        for across in (True, False):
            if across:
                chain.reverse()
            for _ in range(len(roads)):
                end = chain[-1]
                if len(by_node[end]) != 2:
                    break
                next_index = next((j for j in by_node[end] if j not in visited), None)
                if next_index is None or roads[next_index]["roadPrefab"] != prefab:
                    break
                next_road = roads[next_index]
                if prefab not in SURFACE_PREFABS:
                    required_end = "plannedEnd" if across else "plannedStart"
                    if next_road[required_end] != end:
                        break
                other = next_road["plannedEnd"] if next_road["plannedStart"] == end else next_road["plannedStart"]
                if other in chain:
                    break
                chain.append(other)
                visited.add(next_index)
            if across:
                chain.reverse()
        # Closed chains remain closed because the skipped final edge gets processed separately.
        offsets = {}
        for key in chain:
            sample = roads[by_node[key][0]]
            tag = "start" if sample["plannedStart"] == key else "end"
            offsets[key] = sample[tag + "HeightOffset"]
        start = 0
        for _ in range(len(chain) - 1):
            if start >= len(chain) - 1:
                break
            end = start + 1
            for trial in range(start + 2, len(chain)):
                if not fits_chord([points[key] for key in chain[start:trial + 1]]):
                    break
                end = trial
            a, b = chain[start], chain[end]
            x1, z1, _ = points[a]
            x2, z2, _ = points[b]
            out.append({"type": "build-road", "roadPrefab": road["roadPrefab"],
                        "plannedStart": a, "plannedEnd": b, "start": {"x": x1, "z": z1},
                        "end": {"x": x2, "z": z2}, "startHeightOffset": offsets[a],
                        "endHeightOffset": offsets[b]})
            start = end

    new_adjacency = defaultdict(list)
    for row in out:
        new_adjacency[row["plannedStart"]].append(row)
        new_adjacency[row["plannedEnd"]].append(row)
    smooth_nodes = 0
    for node, incident in new_adjacency.items():
        if len(incident) != 2 or incident[0]["roadPrefab"] != incident[1]["roadPrefab"]:
            continue
        if incident[0]["roadPrefab"] not in fitted_prefabs:
            continue
        if not assign_tangents(node, incident, points):
            continue
        smooth_nodes += 1

    audit = {"inputSegments": len(roads), "outputSegments": len(out), "narrowedLocalStreets": narrowed,
             "nativeOneLaneRampSourceSegments": ramp_segments,
             "continuousCurveNodes": smooth_nodes, "horizontalToleranceMeters": 1.5,
             "verticalToleranceMeters": 0.50, "maximumChordMeters": 78}
    return out, audit


def fits_chord(points):
    ax, az, ay = points[0]
    bx, bz, by = points[-1]
    dx, dz = bx - ax, bz - az
    length_sq = dx * dx + dz * dz
    if not 16 <= length_sq <= 78 ** 2:
        return False
    for x, z, y in points[1:-1]:
        t = ((x - ax) * dx + (z - az) * dz) / length_sq
        if not 0 < t < 1 or math.hypot(x - ax - t * dx, z - az - t * dz) > 1.5:
            return False
        if abs(y - ay - t * (by - ay)) > .50:
            return False
    return True


def assign_tangents(node, incident, points):
    x, z, _ = points[node]
    vectors = []
    tags = []
    for line in incident:
        tag = "start" if line["plannedStart"] == node else "end"
        other = "end" if tag == "start" else "start"
        px, pz = line[other]["x"], line[other]["z"]
        length = math.hypot(px - x, pz - z)
        if length < 7:
            return False
        vectors.append(((px - x) / length, (pz - z) / length))
        tags.append(tag + "Direction")
    a, b = vectors
    if a[0] * b[0] + a[1] * b[1] > -0.50:
        return False
    vx, vz = a[0] - b[0], a[1] - b[1]
    scale = math.hypot(vx, vz)
    incident[0][tags[0]] = {"x": vx / scale, "z": vz / scale}
    incident[1][tags[1]] = {"x": -vx / scale, "z": -vz / scale}
    return True
