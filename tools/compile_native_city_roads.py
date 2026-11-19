#!/usr/bin/env python3
"""Compile the selected Nagoya road plan into constructible native TSV.

The input plan contains source-road polylines and the source OSM topology.  A
Cities: Skylines road segment must have a reasonably sized span, and a native
junction cannot expose more than eight arms.  This compiler therefore expands
long source spans to 80m pieces, interns topology anchors, contracts short
pieces with a bounded union-find pass, and removes only links/components that
cannot satisfy the native constraints.

The compiler is deliberately file-only: it never opens a game process or
writes to a save.  The two output TSVs have no header and no UTF-8 BOM.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable, Iterator, Sequence


MIN_SEGMENT_M = 4.0
MAX_SEGMENT_M = 80.0
MAX_ARMS = 8
COORD_DECIMALS = 3

SURFACE = "surface"
ELEVATED = "elevated"
TUNNEL = "tunnel"

# The order is also the culling priority at an overloaded junction: local
# streets are expendable before arterial/highway links.
ROAD_CLASS = {
    "service": (0, "basic"),
    "living_street": (0, "basic"),
    "residential": (0, "basic"),
    "unclassified": (0, "basic"),
    "tertiary": (1, "arterial"),
    "tertiary_link": (1, "arterial"),
    "secondary": (1, "arterial"),
    "secondary_link": (1, "arterial"),
    "primary": (2, "arterial"),
    "primary_link": (2, "arterial"),
    "trunk": (3, "highway"),
    "trunk_link": (3, "highway"),
    "motorway": (4, "highway"),
    "motorway_link": (4, "highway"),
}


def _as_layer(value: object) -> str:
    """Return a stable numeric-ish layer string while accepting old plans."""

    try:
        number = float(value)
    except (TypeError, ValueError):
        return str(value or "0")
    if number.is_integer():
        return str(int(number))
    return format(number, "g")


def _layer_number(layer: str) -> float:
    try:
        return float(layer)
    except ValueError:
        return 0.0


def layer_scope(edge: dict) -> str:
    """Classify the physical layer used for node identity and height."""

    layer = _layer_number(_as_layer(edge.get("layer", "0")))
    tunnel = str(edge.get("tunnel", "no")).lower() in {"yes", "true", "1"}
    bridge = str(edge.get("bridge", "no")).lower() not in {"", "no", "false", "0"}
    if tunnel or layer < 0:
        return TUNNEL
    if bridge or layer > 0:
        return ELEVATED
    return SURFACE


def height_for_scope(scope: str) -> int:
    if scope == ELEVATED:
        return 8
    if scope == TUNNEL:
        return -12
    return 0


def prefab_tag(edge: dict, scope: str) -> str:
    """Map source type/layer to the small native prefab vocabulary."""

    # Explicit physical-layer tags take precedence over road class.  The
    # numeric layer is retained in the E row, so motorway/arterial identity is
    # still auditable even when a bridge or tunnel tag is used.
    if scope == TUNNEL:
        return "tunnel"
    bridge = str(edge.get("bridge", "no")).lower() not in {"", "no", "false", "0"}
    if bridge:
        return "bridge"
    if scope == ELEVATED:
        return "elevated"
    return ROAD_CLASS.get(str(edge.get("kind", "unclassified")), (0, "basic"))[1]


def class_rank(kind: str) -> int:
    return ROAD_CLASS.get(kind, (0, "basic"))[0]


def _point(point: Sequence[object]) -> tuple[float, float]:
    if len(point) < 2:
        raise ValueError(f"point must contain x,z: {point!r}")
    x, z = float(point[0]), float(point[1])
    if not math.isfinite(x) or not math.isfinite(z):
        raise ValueError(f"non-finite point: {point!r}")
    return x, z


def _quantized(point: tuple[float, float]) -> tuple[float, float]:
    return (round(point[0], COORD_DECIMALS), round(point[1], COORD_DECIMALS))


@dataclass
class Node:
    node_id: int
    x: float
    z: float
    identity: tuple
    source_id: str | None = None
    scopes: set[str] = field(default_factory=set)


@dataclass
class RawEdge:
    edge_id: int
    source: str
    start: int
    end: int
    kind: str
    layer: str
    scope: str
    tag: str
    forward: int
    length: float
    points_index: int = 0


@dataclass
class OutputEdge:
    source: str
    start: int
    end: int
    kind: str
    layer: str
    scope: str
    tag: str
    forward: int
    length: float


class DisjointSet:
    """Union-find with a representative coordinate and layer profiles."""

    def __init__(self, nodes: list[Node], edges: list[RawEdge]) -> None:
        count = len(nodes)
        self.parent = list(range(count))
        self.size = [1] * count
        self.x = [node.x for node in nodes]
        self.z = [node.z for node in nodes]
        self.identity = [node.identity for node in nodes]
        self.source_ids = [
            {node.source_id} if node.source_id is not None else set()
            for node in nodes
        ]
        self.scopes = [set(node.scopes) for node in nodes]
        # Incident raw-edge IDs are maintained at roots.  This makes the
        # degree guard cheap without rebuilding the whole graph per contraction.
        self.incident: list[set[int]] = [set() for _ in nodes]
        for edge in edges:
            if edge.start != edge.end:
                self.incident[edge.start].add(edge.edge_id)
                self.incident[edge.end].add(edge.edge_id)

    def find(self, value: int) -> int:
        parent = self.parent
        root = value
        while parent[root] != root:
            root = parent[root]
        while parent[value] != value:
            next_value = parent[value]
            parent[value] = root
            value = next_value
        return root

    def _would_have_degree(self, left: int, right: int, edges: list[RawEdge]) -> int:
        merged = self.incident[left] | self.incident[right]
        degree = 0
        for edge_id in merged:
            edge = edges[edge_id]
            a, b = self.find(edge.start), self.find(edge.end)
            if a == left and b == right or a == right and b == left:
                continue
            if a in {left, right} or b in {left, right}:
                degree += 1
        return degree

    def can_union(self, left: int, right: int, edge: RawEdge, edges: list[RawEdge]) -> bool:
        left, right = self.find(left), self.find(right)
        if left == right:
            return False
        # A short edge may only contract into a node that already represents
        # the same physical layer.  Source junction nodes can intentionally
        # carry several profiles (a bridge/tunnel transition), which keeps the
        # source topology connected while avoiding same-XZ layer crossings.
        if edge.scope not in self.scopes[left] or edge.scope not in self.scopes[right]:
            return False
        return self._would_have_degree(left, right, edges) <= MAX_ARMS

    def union(self, left: int, right: int) -> int:
        left, right = self.find(left), self.find(right)
        if left == right:
            return left
        # Prefer source anchors, then the higher-degree representative, then a
        # stable ID.  Keeping an existing anchor coordinate limits drift.
        left_anchor = bool(self.source_ids[left])
        right_anchor = bool(self.source_ids[right])
        if (not left_anchor, -len(self.incident[left]), left) > (
            not right_anchor,
            -len(self.incident[right]),
            right,
        ):
            left, right = right, left
        self.parent[right] = left
        self.size[left] += self.size[right]
        self.source_ids[left].update(self.source_ids[right])
        self.scopes[left].update(self.scopes[right])
        self.incident[left].update(self.incident[right])
        return left

    def coordinate(self, root: int) -> tuple[float, float]:
        root = self.find(root)
        return self.x[root], self.z[root]

    def profiles(self, root: int) -> set[str]:
        return self.scopes[self.find(root)]


def _iter_clean_points(raw: Sequence[Sequence[object]]) -> list[tuple[float, float]]:
    points: list[tuple[float, float]] = []
    for value in raw:
        point = _point(value)
        if not points or point != points[-1]:
            points.append(point)
    return points


def _expanded_points(
    points: list[tuple[float, float]],
) -> Iterator[tuple[tuple[float, float], tuple[float, float], bool, bool]]:
    """Yield each source span split into <= MAX_SEGMENT_M pieces."""

    for index, (start, end) in enumerate(zip(points, points[1:])):
        distance = math.dist(start, end)
        if distance <= 0:
            continue
        pieces = max(1, math.ceil(distance / MAX_SEGMENT_M))
        for piece in range(pieces):
            a = piece / pieces
            b = (piece + 1) / pieces
            yield (
                (start[0] + (end[0] - start[0]) * a, start[1] + (end[1] - start[1]) * a),
                (start[0] + (end[0] - start[0]) * b, start[1] + (end[1] - start[1]) * b),
                index == 0 and piece == 0,
                index == len(points) - 2 and piece == pieces - 1,
            )


def _node_key(
    point: tuple[float, float],
    scope: str,
    layer: str,
    source_id: str | None,
) -> tuple:
    if source_id is not None:
        # Source IDs are globally unique in the plan and intentionally ignore
        # layer: a common OSM node is the connection at a bridge/tunnel ramp.
        return ("source", source_id)
    return ("coord", *_quantized(point), scope, layer)


def build_raw(plan: dict) -> tuple[list[Node], list[RawEdge], Counter, dict[str, set[str]]]:
    if not isinstance(plan, dict) or not isinstance(plan.get("edges"), list):
        raise ValueError("input plan must contain an edges array")
    nodes: list[Node] = []
    node_by_identity: dict[tuple, int] = {}
    source_node_by_id: dict[str, int] = {}
    raw_edges: list[RawEdge] = []
    omitted = Counter()
    source_profiles: dict[str, set[str]] = defaultdict(set)

    def intern(
        point: tuple[float, float],
        scope: str,
        layer: str,
        source_id: str | None,
    ) -> int:
        identity = _node_key(point, scope, layer, source_id)
        node_id = node_by_identity.get(identity)
        if node_id is None:
            node_id = len(nodes)
            node_by_identity[identity] = node_id
            nodes.append(
                Node(
                    node_id=node_id,
                    x=point[0],
                    z=point[1],
                    identity=identity,
                    source_id=source_id,
                    scopes={scope},
                )
            )
        else:
            nodes[node_id].scopes.add(scope)
        if source_id is not None:
            source_node_by_id[source_id] = node_id
            source_profiles[source_id].add(scope)
        return node_id

    for row in plan["edges"]:
        if not isinstance(row, dict):
            omitted["invalid_edge_record"] += 1
            continue
        source = str(row.get("source", ""))
        raw_points = row.get("pointsXZ")
        if not source or not isinstance(raw_points, list):
            omitted["invalid_edge_record"] += 1
            continue
        try:
            points = _iter_clean_points(raw_points)
        except ValueError:
            omitted["invalid_point"] += 1
            continue
        if len(points) < 2:
            omitted["zero_length_or_single_point"] += 1
            continue
        kind = str(row.get("kind", "unclassified"))
        layer = _as_layer(row.get("layer", "0"))
        scope = layer_scope(row)
        tag = prefab_tag(row, scope)
        start_source = str(row.get("from", "")) or None
        end_source = str(row.get("to", "")) or None
        if start_source:
            source_profiles[start_source].add(scope)
        if end_source:
            source_profiles[end_source].add(scope)

        # Each expanded piece is first interned by topology anchor or exact
        # coordinate/layer.  The original endpoint IDs are retained through
        # every subdivision so adjacent ways share a native node.
        for segment_index, (start, end, is_first, is_last) in enumerate(_expanded_points(points)):
            start_id = intern(
                start,
                scope,
                layer,
                start_source if is_first else None,
            )
            end_id = intern(
                end,
                scope,
                layer,
                end_source if is_last else None,
            )
            length = math.dist(start, end)
            if length <= 0:
                omitted["zero_length_segment"] += 1
                continue
            raw_edges.append(
                RawEdge(
                    edge_id=len(raw_edges),
                    source=source,
                    start=start_id,
                    end=end_id,
                    kind=kind,
                    layer=layer,
                    scope=scope,
                    tag=tag,
                    forward=1 if str(row.get("oneway", "yes")).lower() not in {"no", "false", "0"} else 0,
                    length=length,
                    points_index=segment_index,
                )
            )
    # Source endpoint nodes can have several physical profiles.  That is
    # intentional at ramps; keeping the set allows the short-link guard to
    # contract a transition without conflating unrelated same-XZ crossings.
    for source_id, scopes in source_profiles.items():
        node_id = source_node_by_id.get(source_id)
        if node_id is not None:
            nodes[node_id].scopes.update(scopes)
    return nodes, raw_edges, omitted, source_profiles


def _recompute_degree(dsu: DisjointSet, edges: list[RawEdge]) -> dict[int, int]:
    degree: Counter[int] = Counter()
    for edge in edges:
        start, end = dsu.find(edge.start), dsu.find(edge.end)
        if start == end:
            continue
        degree[start] += 1
        degree[end] += 1
    return dict(degree)


def _contract_short_links(
    nodes: list[Node], raw_edges: list[RawEdge]
) -> tuple[DisjointSet, Counter, list[tuple[RawEdge, str]]]:
    dsu = DisjointSet(nodes, raw_edges)
    stats = Counter()
    # At most three bounded passes: the first contracts original short spans;
    # later passes catch a short span exposed by a representative move.
    for pass_index in range(3):
        changed = 0
        degree = _recompute_degree(dsu, raw_edges)
        candidates = sorted(
            (
                edge
                for edge in raw_edges
                if edge.length < MIN_SEGMENT_M
                and dsu.find(edge.start) != dsu.find(edge.end)
            ),
            key=lambda edge: (edge.length, edge.edge_id),
        )
        for edge in candidates:
            left, right = dsu.find(edge.start), dsu.find(edge.end)
            if left == right:
                continue
            # The dynamic degree test uses all current raw incidences.  The
            # cheap degree snapshot keeps an obvious >8 junction from entering
            # the slower union path, while can_union handles exact roots.
            if degree.get(left, 0) > MAX_ARMS or degree.get(right, 0) > MAX_ARMS:
                continue
            if dsu.can_union(left, right, edge, raw_edges):
                dsu.union(left, right)
                stats["shortLinksContracted"] += 1
                changed += 1
        if not changed:
            break
        stats[f"contractionPass{pass_index + 1}"] = changed

    residual: list[tuple[RawEdge, str]] = []
    for edge in raw_edges:
        start, end = dsu.find(edge.start), dsu.find(edge.end)
        if start == end:
            if edge.length < MIN_SEGMENT_M:
                stats["shortLinksCollapsed"] += 1
            else:
                residual.append((edge, "collapsed_nonshort_loop"))
            continue
        a = dsu.coordinate(start)
        b = dsu.coordinate(end)
        if math.dist(a, b) < MIN_SEGMENT_M:
            residual.append((edge, "short_after_contraction"))
    return dsu, stats, residual


def _make_output_edges(
    dsu: DisjointSet,
    raw_edges: list[RawEdge],
    residual: list[tuple[RawEdge, str]],
) -> tuple[list[OutputEdge], list[dict[str, object]]]:
    residual_ids = {edge.edge_id: reason for edge, reason in residual}
    output: list[OutputEdge] = []
    culled: list[dict[str, object]] = []
    for edge in raw_edges:
        start, end = dsu.find(edge.start), dsu.find(edge.end)
        if start == end:
            # Short rows are represented by their contracted topology.  A
            # non-short loop is never constructible and is explicitly audited.
            if edge.edge_id in residual_ids:
                culled.append({"sourceEdgeId": edge.source, "reason": residual_ids[edge.edge_id]})
            continue
        a, b = dsu.coordinate(start), dsu.coordinate(end)
        length = math.dist(a, b)
        if edge.edge_id in residual_ids or length < MIN_SEGMENT_M:
            culled.append(
                {
                    "sourceEdgeId": edge.source,
                    "reason": residual_ids.get(edge.edge_id, "short_after_contraction"),
                    "lengthM": round(length, 3),
                }
            )
            continue
        output.append(
            OutputEdge(
                source=edge.source,
                start=start,
                end=end,
                kind=edge.kind,
                layer=edge.layer,
                scope=edge.scope,
                tag=edge.tag,
                forward=edge.forward,
                length=length,
            )
        )
    return output, culled


def _components(edges: list[OutputEdge]) -> list[set[int]]:
    adjacency: dict[int, set[int]] = defaultdict(set)
    for edge in edges:
        adjacency[edge.start].add(edge.end)
        adjacency[edge.end].add(edge.start)
    seen: set[int] = set()
    result: list[set[int]] = []
    for node in sorted(adjacency):
        if node in seen:
            continue
        seen.add(node)
        stack = [node]
        component: set[int] = set()
        while stack:
            current = stack.pop()
            component.add(current)
            for other in adjacency[current]:
                if other not in seen:
                    seen.add(other)
                    stack.append(other)
        result.append(component)
    return result


def _prune_overloaded_junctions(
    edges: list[OutputEdge], culled: list[dict[str, object]]
) -> tuple[list[OutputEdge], Counter]:
    stats = Counter()
    # Two bounded rounds are sufficient because each round removes an edge at
    # every overloaded node.  The final assertion in compile_plan is the hard
    # gate if malformed input violates this assumption.
    for _ in range(2):
        incident: dict[int, list[OutputEdge]] = defaultdict(list)
        for edge in edges:
            incident[edge.start].append(edge)
            incident[edge.end].append(edge)
        overloaded = [node for node, rows in incident.items() if len(rows) > MAX_ARMS]
        if not overloaded:
            break
        remove_ids: set[int] = set()
        edge_index = {id(edge): index for index, edge in enumerate(edges)}
        for node in overloaded:
            rows = sorted(
                incident[node],
                key=lambda edge: (class_rank(edge.kind), edge.length, edge.source),
            )
            for edge in rows[: len(rows) - MAX_ARMS]:
                remove_ids.add(edge_index[id(edge)])
        if not remove_ids:
            break
        next_edges: list[OutputEdge] = []
        for index, edge in enumerate(edges):
            if index in remove_ids:
                culled.append(
                    {"sourceEdgeId": edge.source, "reason": "degree_over_eight"}
                )
                stats["degreeLinksCulled"] += 1
            else:
                next_edges.append(edge)
        edges = next_edges
    return edges, stats


def _keep_largest_component(
    edges: list[OutputEdge], culled: list[dict[str, object]]
) -> tuple[list[OutputEdge], list[int], Counter]:
    components = _components(edges)
    if len(components) <= 1:
        return edges, [len(components[0]) if components else 0], Counter()
    # Preserve the component carrying the most road length; tie-break by node
    # count so tiny coordinate artifacts cannot win.
    edge_length = Counter()
    for index, component in enumerate(components):
        edge_length[index] = sum(
            edge.length for edge in edges if edge.start in component and edge.end in component
        )
    largest_index = max(
        range(len(components)), key=lambda index: (edge_length[index], len(components[index]))
    )
    keep = components[largest_index]
    next_edges: list[OutputEdge] = []
    stats = Counter(disconnectedComponentsCulled=len(components) - 1)
    for edge in edges:
        if edge.start in keep and edge.end in keep:
            next_edges.append(edge)
        else:
            culled.append({"sourceEdgeId": edge.source, "reason": "disconnected_sliver"})
            stats["disconnectedSliverLinksCulled"] += 1
    sizes = sorted((len(component) for component in components), reverse=True)
    return next_edges, sizes, stats


def _assign_native_keys(
    dsu: DisjointSet, edges: list[OutputEdge]
) -> tuple[dict[int, str], dict[str, tuple[float, float, int]], Counter]:
    roots = {dsu.find(edge.start) for edge in edges} | {dsu.find(edge.end) for edge in edges}

    def sort_key(root: int) -> tuple:
        x, z = dsu.coordinate(root)
        profiles = dsu.profiles(root)
        # A transition node uses ground when any surface arm is present, then
        # elevated, then tunnel.  Edge layer remains authoritative in E rows.
        scope = SURFACE if SURFACE in profiles else ELEVATED if ELEVATED in profiles else TUNNEL
        return (round(x, COORD_DECIMALS), round(z, COORD_DECIMALS), height_for_scope(scope), root)

    root_to_key: dict[int, str] = {}
    node_rows: dict[str, tuple[float, float, int]] = {}
    heights = Counter()
    for index, root in enumerate(sorted(roots, key=sort_key), start=1):
        root = dsu.find(root)
        profiles = dsu.profiles(root)
        scope = SURFACE if SURFACE in profiles else ELEVATED if ELEVATED in profiles else TUNNEL
        height = height_for_scope(scope)
        key = f"n{index:06d}"
        x, z = dsu.coordinate(root)
        # Coordinates are rounded only at serialization.  Keeping the float in
        # DSU avoids accumulating quantization error during contractions.
        root_to_key[root] = key
        node_rows[key] = (x, z, height)
        heights[str(height)] += 1
    return root_to_key, node_rows, heights


def _bounds(rows: Iterable[tuple[float, float]]) -> list[float] | None:
    points = list(rows)
    if not points:
        return None
    return [
        min(point[0] for point in points),
        min(point[1] for point in points),
        max(point[0] for point in points),
        max(point[1] for point in points),
    ]


def compile_plan(plan: dict, input_path: Path) -> tuple[dict, list[str], list[str]]:
    nodes, raw_edges, omitted, source_profiles = build_raw(plan)
    dsu, contraction_stats, residual = _contract_short_links(nodes, raw_edges)
    output_edges, culled = _make_output_edges(dsu, raw_edges, residual)
    output_edges, degree_stats = _prune_overloaded_junctions(output_edges, culled)
    output_edges, component_sizes, component_stats = _keep_largest_component(output_edges, culled)

    # A final bounded cleanup is intentionally a hard validation rather than a
    # silent repair.  It prevents reporting a file that the runtime cannot
    # construct, while preserving the exact reason in the exception.
    final_degree: Counter[int] = Counter()
    for edge in output_edges:
        final_degree[edge.start] += 1
        final_degree[edge.end] += 1
    if any(value > MAX_ARMS for value in final_degree.values()):
        raise ValueError("native arm limit remained after bounded degree pruning")

    root_to_key, node_rows, height_counts = _assign_native_keys(dsu, output_edges)
    node_lines = [
        "N\t{}\t{}\t{}\t{}".format(
            key,
            format(x, ".3f"),
            format(z, ".3f"),
            height,
        )
        for key, (x, z, height) in node_rows.items()
    ]
    edge_lines: list[str] = []
    for edge in output_edges:
        start = root_to_key[dsu.find(edge.start)]
        end = root_to_key[dsu.find(edge.end)]
        edge_lines.append(
            "E\t{}\t{}\t{}\t{}\t{}\t{}".format(
                edge.source,
                start,
                end,
                edge.tag,
                edge.layer,
                edge.forward,
            )
        )

    node_values = list(node_rows.values())
    bounds = _bounds((value[0], value[1]) for value in node_values)
    degree_histogram = {str(value): count for value, count in sorted(Counter(final_degree.values()).items())}
    tag_counts = Counter(edge.tag for edge in output_edges)
    layer_counts = Counter(edge.layer for edge in output_edges)
    scope_counts = Counter(edge.scope for edge in output_edges)
    source_component_count = int(plan.get("audit", {}).get("sourceComponents", 1))
    audit = {
        "status": "compiled_file_only_native_tsv_game_construction_unverified",
        "writesToGame": False,
        "sourcePlan": str(input_path),
        "input": {
            "sourceEdges": len(plan["edges"]),
            "sourceNodes": len(source_profiles),
            "sourceConnectedComponents": source_component_count,
        },
        "raw": {
            "expandedNodes": len(nodes),
            "expandedSegments": len(raw_edges),
            "shortSegmentsUnder4m": sum(edge.length < MIN_SEGMENT_M for edge in raw_edges),
            "omitted": dict(omitted),
        },
        "output": {
            "nodes": len(node_rows),
            "segments": len(output_edges),
            "prefabTags": dict(sorted(tag_counts.items())),
            "layers": dict(sorted(layer_counts.items(), key=lambda pair: float(pair[0]) if pair[0].replace("-", "", 1).replace(".", "", 1).isdigit() else pair[0])),
            "physicalScopes": dict(sorted(scope_counts.items())),
            "heightOffsets": dict(sorted(height_counts.items())),
            "maxSegmentM": round(max((edge.length for edge in output_edges), default=0.0), 3),
            "minSegmentM": round(min((edge.length for edge in output_edges), default=0.0), 3),
        },
        "constraints": {
            "minimumSegmentM": MIN_SEGMENT_M,
            "maximumSplitSegmentM": MAX_SEGMENT_M,
            "maximumArms": MAX_ARMS,
            "shortLinksContracted": contraction_stats.get("shortLinksContracted", 0),
            "shortLinksCollapsed": contraction_stats.get("shortLinksCollapsed", 0),
            "shortLinksCulled": sum(
                1 for row in culled if row["reason"] in {"short_after_contraction", "collapsed_nonshort_loop"}
            ),
        },
        "connectedComponents": {
            "countBeforeSliverPrune": len(component_sizes),
            "count": 1 if output_edges else 0,
            "sizesBeforeSliverPrune": component_sizes,
            "sourceComponents": source_component_count,
            **dict(component_stats),
        },
        "degree": {
            "maximum": max(final_degree.values(), default=0),
            "histogram": degree_histogram,
            "overEight": sum(value > MAX_ARMS for value in final_degree.values()),
            **dict(degree_stats),
        },
        "culledLinks": {
            "count": len(culled),
            "byReason": dict(sorted(Counter(row["reason"] for row in culled).items())),
            "rows": culled,
        },
        "boundsXZ": bounds,
        "boundsByHeightOffset": {
            str(height): _bounds((x, z) for x, z, node_height in node_values if node_height == height)
            for height in sorted({value[2] for value in node_values})
        },
        "validation": {
            "allSegmentsAtLeast4m": all(edge.length >= MIN_SEGMENT_M for edge in output_edges),
            "allJunctionsAtMost8Arms": max(final_degree.values(), default=0) <= MAX_ARMS,
            "allEdgesHaveKnownPrefabTag": all(edge.tag in {"basic", "arterial", "highway", "elevated", "bridge", "tunnel"} for edge in output_edges),
            "noHeaderOrBom": True,
        },
    }
    return audit, node_lines, edge_lines


def _write_lines(path: Path, lines: Sequence[str]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as stream:
        if lines:
            stream.write("\n".join(lines))
            stream.write("\n")


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", "--plan", dest="input_path", type=Path, required=True)
    parser.add_argument("--output-dir", "--output", dest="output_dir", type=Path, required=True)
    args = parser.parse_args(argv)
    input_path = args.input_path.expanduser()
    output_dir = args.output_dir.expanduser()
    if not input_path.is_file():
        parser.error(f"input plan does not exist: {input_path}")
    try:
        plan = json.loads(input_path.read_text(encoding="utf-8-sig"))
        audit, node_lines, edge_lines = compile_plan(plan, input_path)
        output_dir.mkdir(parents=True, exist_ok=True)
        nodes_path = output_dir / "native-road-nodes.tsv"
        edges_path = output_dir / "native-road-segments.tsv"
        audit_path = output_dir / "native-road-compile-audit.json"
        _write_lines(nodes_path, node_lines)
        _write_lines(edges_path, edge_lines)
        audit["artifacts"] = {
            "nodes": str(nodes_path),
            "segments": str(edges_path),
            "audit": str(audit_path),
        }
        audit_path.write_text(json.dumps(audit, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
        print(json.dumps({"audit": str(audit_path), "nodes": len(node_lines), "segments": len(edge_lines), "culled": audit["culledLinks"]["count"]}, ensure_ascii=False))
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"compile_native_city_roads: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
