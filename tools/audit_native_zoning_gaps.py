"""Audit native-zoning groups that have no frontage proposal.

This is a read-only bridge between the v3 member read-back, the v2 frontage
plan, the source building inventory, and the source-road plan.  It does not
assign a missing group to a road and it never writes to Cities: Skylines.

The emitted TSV is a deterministic *next-build input*: each row is still a
coverage-gap request.  ``nearest_source_edge`` is evidence for prioritisation
only; it is deliberately not a proposed parent/road assignment.
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import re
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any, Iterable


DEFAULT_ROOT = Path(r"H:\CS1-PLATEAU\nagoya-station")
DEFAULT_V2 = DEFAULT_ROOT / "citywide-zoning-v2"
DEFAULT_V3 = DEFAULT_ROOT / "citywide-zoning-v3"
DEFAULT_MEMBERS = DEFAULT_V3 / "native-zoning-members.tsv"
DEFAULT_AUDIT = DEFAULT_V3 / "native-zoning-audit.json"
DEFAULT_FRONTAGES = DEFAULT_V2 / "block-frontages.csv"
DEFAULT_INVENTORY = DEFAULT_V2 / "models-with-use-and-district.csv"
DEFAULT_ROAD_PLAN = DEFAULT_V2 / "native-road-plan.json"
DEFAULT_OUTPUT = DEFAULT_V3

BLOCK_M = 500.0
FRONTAGE_RADIUS_M = 160.0
SEARCH_CELL_M = 250.0
ZONING_ROADS = {
    "primary",
    "primary_link",
    "secondary",
    "secondary_link",
    "tertiary",
    "tertiary_link",
    "unclassified",
    "residential",
    "living_street",
    "service",
}
GROUP_RE = re.compile(r"^(?P<ward>.+):(?P<gx>-?\d+):(?P<gz>-?\d+):(?P<use>[^:]+)$")


def _text(value: Any) -> str:
    return "" if value is None else str(value).strip()


def _float(value: Any) -> float | None:
    try:
        result = float(value)
    except (TypeError, ValueError):
        return None
    return result if math.isfinite(result) else None


def _fmt(value: float | None) -> str:
    return "" if value is None else f"{value:.3f}"


def _parse_group(group: str) -> tuple[str, int, int, str]:
    match = GROUP_RE.fullmatch(group)
    if not match:
        raise ValueError(f"Invalid role group: {group!r}")
    return (
        match.group("ward"),
        int(match.group("gx")),
        int(match.group("gz")),
        match.group("use"),
    )


def _counter_dict(counter: Counter[str]) -> dict[str, int]:
    return dict(sorted(counter.items()))


def _nested_summary(values: dict[tuple[str, str], dict[str, Any]]) -> list[dict[str, Any]]:
    rows = []
    for (ward, use), value in sorted(values.items()):
        row = {"ward": ward, "use": use}
        row.update(value)
        rows.append(row)
    return rows


def _load_frontage_gaps(path: Path) -> tuple[dict[str, dict[str, Any]], dict[str, Any]]:
    gaps: dict[str, dict[str, Any]] = {}
    all_groups: set[str] = set()
    with path.open("r", encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        required = {"group", "source_building_uses", "proposed_x", "proposed_z", "road_source"}
        missing = sorted(required - set(reader.fieldnames or ()))
        if missing:
            raise ValueError(f"Missing frontage columns: {missing}")
        for line_number, row in enumerate(reader, 2):
            group = _text(row.get("group"))
            if not group:
                raise ValueError(f"Empty frontage group at line {line_number}")
            if group in all_groups:
                raise ValueError(f"Duplicate frontage group: {group!r}")
            all_groups.add(group)
            ward, gx, gz, use = _parse_group(group)
            declared = int(row.get("source_building_uses") or 0)
            proposed_x = _float(row.get("proposed_x"))
            proposed_z = _float(row.get("proposed_z"))
            road_source = _text(row.get("road_source"))
            if proposed_x is None or proposed_z is None or not road_source:
                gaps[group] = {
                    "group": group,
                    "ward": ward,
                    "grid_x": gx,
                    "grid_z": gz,
                    "use": use,
                    # v2 calls this source_building_uses; it is a role
                    # assignment count, not necessarily a unique model count.
                    "declared_role_assignments": declared,
                }
    return gaps, {
        "frontage_groups": len(all_groups),
        "groups_without_proposal": len(gaps),
    }


def _load_members(path: Path) -> tuple[dict[str, dict[str, Any]], dict[str, Any]]:
    groups: dict[str, dict[str, Any]] = {}
    source_ids: set[str] = set()
    statuses = Counter()
    role_assignments = 0
    with path.open("r", encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream, delimiter="\t")
        required = {"sourceId", "roleGroup", "status"}
        missing = sorted(required - set(reader.fieldnames or ()))
        if missing:
            raise ValueError(f"Missing member columns: {missing}")
        for line_number, row in enumerate(reader, 2):
            status = _text(row.get("status"))
            statuses[status] += 1
            if status != "uncovered_no_proposal":
                continue
            source = _text(row.get("sourceId"))
            group = _text(row.get("roleGroup"))
            if not source or not group:
                raise ValueError(f"Empty source or role group at members line {line_number}")
            ward, gx, gz, use = _parse_group(group)
            record = groups.setdefault(
                group,
                {
                    "group": group,
                    "ward": ward,
                    "grid_x": gx,
                    "grid_z": gz,
                    "use": use,
                    "model_ids": set(),
                    "role_assignments": 0,
                },
            )
            record["model_ids"].add(source)
            record["role_assignments"] += 1
            source_ids.add(source)
            role_assignments += 1
    return groups, {
        "member_rows_by_status": _counter_dict(statuses),
        "uncovered_groups": len(groups),
        "uncovered_role_assignments": role_assignments,
        "uncovered_unique_model_ids": len(source_ids),
        "uncovered_source_ids": source_ids,
    }


def _load_source_positions(path: Path, source_ids: set[str]) -> tuple[dict[str, tuple[float, float]], dict[str, Any]]:
    positions: dict[str, tuple[float, float]] = {}
    with path.open("r", encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        required = {"source", "x", "z"}
        missing = sorted(required - set(reader.fieldnames or ()))
        if missing:
            raise ValueError(f"Missing inventory columns: {missing}")
        for row in reader:
            source = _text(row.get("source"))
            if source not in source_ids or source in positions:
                continue
            x, z = _float(row.get("x")), _float(row.get("z"))
            if x is not None and z is not None:
                positions[source] = (x, z)
    return positions, {
        "requested_model_ids": len(source_ids),
        "model_ids_with_source_geometry": len(positions),
        "model_ids_missing_source_geometry": len(source_ids - positions.keys()),
    }


def _load_road_graph(path: Path) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    with path.open("r", encoding="utf-8-sig") as stream:
        payload = json.load(stream)
    audit = payload.get("audit") or {}
    transform = audit.get("transform") or {}
    scale = _float(transform.get("scale"))
    center = transform.get("originalCenterXZ")
    if scale is None or not isinstance(center, list) or len(center) != 2:
        raise ValueError("Road plan is missing audit.transform.scale/originalCenterXZ")
    ox, oz = _float(center[0]), _float(center[1])
    if ox is None or oz is None:
        raise ValueError("Road plan transform center is not finite")
    edges = []
    for edge in payload.get("edges", []):
        points = edge.get("pointsXZ") or []
        if len(points) < 2:
            continue
        normalized = []
        for point in points:
            if not isinstance(point, list) or len(point) < 2:
                continue
            x, z = _float(point[0]), _float(point[1])
            if x is not None and z is not None:
                normalized.append((x, z))
        if len(normalized) >= 2:
            edges.append({
                "source": _text(edge.get("source")),
                "kind": _text(edge.get("kind")) or "unclassified",
                "layer": _text(edge.get("layer")) or "0",
                "bridge": _text(edge.get("bridge")) or "no",
                "tunnel": _text(edge.get("tunnel")) or "no",
                "pedestrian_allowed": bool(edge.get("pedestrianAllowed", False)),
                "points": tuple(normalized),
            })
    if not edges:
        raise ValueError("Road plan contains no usable source edges")
    return edges, {
        "source_edge_count": len(edges),
        "transform_scale": scale,
        "transform_original_center_x": ox,
        "transform_original_center_z": oz,
        "source_road_plan_audit": str(audit.get("status", "")),
    }


def _project(x: float, z: float, road_meta: dict[str, Any]) -> tuple[float, float]:
    return (
        (x - road_meta["transform_original_center_x"]) * road_meta["transform_scale"],
        (z - road_meta["transform_original_center_z"]) * road_meta["transform_scale"],
    )


def _cell(value: float) -> int:
    return math.floor(value / SEARCH_CELL_M)


def _segment_distance_squared(point: tuple[float, float], a: tuple[float, float], b: tuple[float, float]) -> float:
    px, pz = point
    ax, az = a
    bx, bz = b
    dx, dz = bx - ax, bz - az
    length_squared = dx * dx + dz * dz
    if length_squared <= 0:
        return (px - ax) ** 2 + (pz - az) ** 2
    t = max(0.0, min(1.0, ((px - ax) * dx + (pz - az) * dz) / length_squared))
    qx, qz = ax + t * dx, az + t * dz
    return (px - qx) ** 2 + (pz - qz) ** 2


def _edge_is_zonable(edge: dict[str, Any]) -> bool:
    return (
        edge["kind"] in ZONING_ROADS
        and edge["pedestrian_allowed"]
        and edge["layer"] == "0"
        and edge["bridge"].lower() == "no"
        and edge["tunnel"].lower() == "no"
    )


def _index_edges(edges: Iterable[dict[str, Any]]) -> dict[tuple[int, int], list[dict[str, Any]]]:
    buckets: dict[tuple[int, int], list[dict[str, Any]]] = defaultdict(list)
    for edge in edges:
        xs = [point[0] for point in edge["points"]]
        zs = [point[1] for point in edge["points"]]
        for gx in range(_cell(min(xs)), _cell(max(xs)) + 1):
            for gz in range(_cell(min(zs)), _cell(max(zs)) + 1):
                buckets[gx, gz].append(edge)
    return buckets


def _nearest_edge(point: tuple[float, float], edges: list[dict[str, Any]], buckets: dict[tuple[int, int], list[dict[str, Any]]]) -> tuple[dict[str, Any] | None, float | None]:
    """Find an exact nearest edge using an expanding spatial bucket window."""
    gx, gz = _cell(point[0]), _cell(point[1])
    candidates: dict[str, dict[str, Any]] = {}
    best_squared = math.inf
    # The finite road plan is bounded; radius 32 covers its largest expected
    # extent while still avoiding a full graph scan for ordinary groups.
    for radius in range(0, 33):
        for bx in range(gx - radius, gx + radius + 1):
            for bz in range(gz - radius, gz + radius + 1):
                if radius and abs(bx - gx) != radius and abs(bz - gz) != radius:
                    continue
                for edge in buckets.get((bx, bz), ()):
                    candidates[edge["source"]] = edge
        # ``_best`` is kept in the same map for deterministic tie-breaking;
        # iterate over a snapshot because the best marker may be replaced in
        # this pass.
        for edge in list(candidates.values()):
            distance_squared = min(
                _segment_distance_squared(point, a, b)
                for a, b in zip(edge["points"], edge["points"][1:])
            )
            if distance_squared < best_squared or (
                math.isclose(distance_squared, best_squared) and edge["source"] < (candidates.get("_best", {}).get("source", "~"))
            ):
                best_squared = distance_squared
                candidates["_best"] = edge
        # Once the current search square's outer boundary is farther than the
        # best edge, no edge in an unseen bucket can be closer.  The half-cell
        # diagonal is a conservative lower bound.
        if best_squared < math.inf and radius * SEARCH_CELL_M - math.sqrt(2) * SEARCH_CELL_M >= math.sqrt(best_squared):
            break
    best = candidates.get("_best")
    return best, None if best is None else math.sqrt(best_squared)


def _load_audit(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8-sig") as stream:
        audit = json.load(stream)
    if not isinstance(audit, dict):
        raise ValueError("native-zoning-audit.json must contain an object")
    return audit


def audit_gaps(
    members_path: Path,
    audit_path: Path,
    frontages_path: Path,
    inventory_path: Path,
    road_plan_path: Path,
    output_dir: Path,
) -> dict[str, Any]:
    source_audit = _load_audit(audit_path)
    frontage_gaps, frontage_stats = _load_frontage_gaps(frontages_path)
    member_groups, member_stats = _load_members(members_path)
    member_gap_names = set(member_groups)
    frontage_gap_names = set(frontage_gaps)
    if member_gap_names != frontage_gap_names:
        raise ValueError(
            "Member/frontage gap group sets differ: "
            f"members_only={len(member_gap_names - frontage_gap_names)}, "
            f"frontages_only={len(frontage_gap_names - member_gap_names)}"
        )

    positions, position_stats = _load_source_positions(inventory_path, member_stats["uncovered_source_ids"])
    edges, road_meta = _load_road_graph(road_plan_path)
    zonable_edges = [edge for edge in edges if _edge_is_zonable(edge)]
    all_buckets = _index_edges(edges)
    zonable_buckets = _index_edges(zonable_edges)

    per_ward_use: dict[tuple[str, str], dict[str, Any]] = {}
    per_ward_sets: dict[str, set[str]] = defaultdict(set)
    per_use_sets: dict[str, set[str]] = defaultdict(set)
    extension_sets_by_ward: dict[str, set[str]] = defaultdict(set)
    extension_sets_by_use: dict[str, set[str]] = defaultdict(set)
    extension_sets_by_ward_use: dict[tuple[str, str], set[str]] = defaultdict(set)
    all_rows: list[dict[str, Any]] = []
    for group in sorted(member_groups):
        info = member_groups[group]
        source_positions = [positions[source] for source in sorted(info["model_ids"]) if source in positions]
        if source_positions:
            original_x = sum(point[0] for point in source_positions) / len(source_positions)
            original_z = sum(point[1] for point in source_positions) / len(source_positions)
            projected_x, projected_z = _project(original_x, original_z, road_meta)
            centroid_status = "source_model_centroid"
            nearest_edge, nearest_distance = _nearest_edge((projected_x, projected_z), edges, all_buckets)
            nearest_zonable_edge, nearest_zonable_distance = _nearest_edge(
                (projected_x, projected_z), zonable_edges, zonable_buckets
            )
        else:
            original_x = original_z = projected_x = projected_z = None
            centroid_status = "missing_source_geometry"
            nearest_edge = nearest_zonable_edge = None
            nearest_distance = nearest_zonable_distance = None
        if nearest_zonable_edge is None:
            extension_reason = "no_zonable_source_edge"
        elif nearest_zonable_distance > FRONTAGE_RADIUS_M:
            extension_reason = "nearest_zonable_edge_outside_frontage_radius"
        else:
            extension_reason = "within_frontage_radius_but_no_frontage_proposal"
        ward, use = info["ward"], info["use"]
        key = (ward, use)
        summary = per_ward_use.setdefault(
            key,
            {
                "aggregate_group_count": 0,
                "role_assignment_count": 0,
                "model_id_count": 0,
                "groups_with_source_geometry": 0,
                "nearest_zonable_distance_max_m": None,
                "nearest_zonable_distance_sum_m": 0.0,
                "groups_outside_frontage_radius": 0,
                "road_extension_role_assignment_count": 0,
            },
        )
        model_count = len(info["model_ids"])
        summary["aggregate_group_count"] += 1
        summary["role_assignment_count"] += info["role_assignments"]
        summary["model_id_count"] += model_count
        per_ward_sets[ward].update(info["model_ids"])
        per_use_sets[use].update(info["model_ids"])
        if source_positions:
            summary["groups_with_source_geometry"] += 1
        if nearest_zonable_distance is not None:
            summary["nearest_zonable_distance_sum_m"] += nearest_zonable_distance
            summary["nearest_zonable_distance_max_m"] = max(
                summary["nearest_zonable_distance_max_m"] or 0.0, nearest_zonable_distance
            )
            if nearest_zonable_distance > FRONTAGE_RADIUS_M:
                summary["groups_outside_frontage_radius"] += 1
        road_extension_need = int(
            nearest_zonable_distance is None or nearest_zonable_distance > FRONTAGE_RADIUS_M
        )
        if road_extension_need:
            extension_sets_by_ward[ward].update(info["model_ids"])
            extension_sets_by_use[use].update(info["model_ids"])
            extension_sets_by_ward_use[key].update(info["model_ids"])
            summary["road_extension_role_assignment_count"] += info["role_assignments"]
        all_rows.append(
            {
                "group": group,
                "ward": ward,
                "use": use,
                "grid_x": info["grid_x"],
                "grid_z": info["grid_z"],
                "model_id_count": model_count,
                "role_assignment_count": info["role_assignments"],
                "declared_role_assignments": frontage_gaps[group]["declared_role_assignments"],
                "source_geometry_count": len(source_positions),
                "centroid_status": centroid_status,
                "original_centroid_x": original_x,
                "original_centroid_z": original_z,
                "projected_centroid_x": projected_x,
                "projected_centroid_z": projected_z,
                # This is a source-graph proximity fact, never a build parent.
                "nearest_source_edge": None if nearest_edge is None else nearest_edge["source"],
                "nearest_source_edge_kind": None if nearest_edge is None else nearest_edge["kind"],
                "nearest_source_edge_distance_m": nearest_distance,
                "nearest_zonable_source_edge": None if nearest_zonable_edge is None else nearest_zonable_edge["source"],
                "nearest_zonable_source_edge_kind": None if nearest_zonable_edge is None else nearest_zonable_edge["kind"],
                "nearest_zonable_source_edge_distance_m": nearest_zonable_distance,
                "extension_reason": extension_reason,
                "road_extension_need": road_extension_need,
            }
        )

    # Stable priority: coverage impact first, then the measured road gap, then
    # source identity.  No synthetic road parent or guessed placement is made.
    all_rows.sort(
        key=lambda row: (
            -row["road_extension_need"],
            -row["model_id_count"],
            -(row["nearest_zonable_source_edge_distance_m"] or -1.0),
            row["ward"],
            row["use"],
            row["group"],
        )
    )
    for rank, row in enumerate(all_rows, 1):
        row["priority_rank"] = rank

    for summary in per_ward_use.values():
        count = summary["groups_with_source_geometry"]
        summary["nearest_zonable_distance_mean_m"] = (
            summary["nearest_zonable_distance_sum_m"] / count if count else None
        )
        summary.pop("nearest_zonable_distance_sum_m", None)
        ward, use = next(key for key, value in per_ward_use.items() if value is summary)
        summary["road_extension_group_count"] = sum(
            1 for row in all_rows if row["ward"] == ward and row["use"] == use and row["road_extension_need"]
        )
        summary["road_extension_model_id_count"] = len(extension_sets_by_ward_use[ward, use])

    per_ward = []
    for ward in sorted(per_ward_sets):
        values = [value for (key_ward, _), value in per_ward_use.items() if key_ward == ward]
        per_ward.append({
            "ward": ward,
            "aggregate_group_count": sum(item["aggregate_group_count"] for item in values),
            "role_assignment_count": sum(item["role_assignment_count"] for item in values),
            "model_id_count": len(per_ward_sets[ward]),
            "groups_outside_frontage_radius": sum(item["groups_outside_frontage_radius"] for item in values),
            "road_extension_group_count": sum(item["road_extension_group_count"] for item in values),
            "road_extension_role_assignment_count": sum(item["road_extension_role_assignment_count"] for item in values),
            "road_extension_model_id_count": len(extension_sets_by_ward[ward]),
        })
    per_use = []
    for use in sorted(per_use_sets):
        values = [value for (key_ward, key_use), value in per_ward_use.items() if key_use == use]
        per_use.append({
            "use": use,
            "aggregate_group_count": sum(item["aggregate_group_count"] for item in values),
            "role_assignment_count": sum(item["role_assignment_count"] for item in values),
            "model_id_count": len(per_use_sets[use]),
            "groups_outside_frontage_radius": sum(item["groups_outside_frontage_radius"] for item in values),
            "road_extension_group_count": sum(item["road_extension_group_count"] for item in values),
            "road_extension_role_assignment_count": sum(item["road_extension_role_assignment_count"] for item in values),
            "road_extension_model_id_count": len(extension_sets_by_use[use]),
        })

    output_dir.mkdir(parents=True, exist_ok=True)
    input_path = output_dir / "native-zoning-gap-build-input.tsv"
    fields = (
        "priority_rank", "group", "ward", "use", "grid_x", "grid_z",
        "model_id_count", "role_assignment_count", "declared_role_assignments",
        "source_geometry_count", "centroid_status", "original_centroid_x", "original_centroid_z",
        "projected_centroid_x", "projected_centroid_z", "nearest_source_edge",
        "nearest_source_edge_kind", "nearest_source_edge_distance_m", "nearest_zonable_source_edge",
        "nearest_zonable_source_edge_kind", "nearest_zonable_source_edge_distance_m", "extension_reason",
        "road_extension_need",
    )
    with input_path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream, delimiter="\t", lineterminator="\n")
        writer.writerow(fields)
        float_fields = {
            "original_centroid_x", "original_centroid_z", "projected_centroid_x", "projected_centroid_z",
            "nearest_source_edge_distance_m", "nearest_zonable_source_edge_distance_m",
        }
        for row in all_rows:
            writer.writerow(
                _fmt(row[field]) if field in float_fields
                else row[field]
                for field in fields
            )

    audit_result = {
        "status": "verified read-only native zoning gap audit; road parents intentionally unassigned",
        "writesToGame": False,
        "inputs": {
            "members": str(members_path),
            "nativeZoningAudit": str(audit_path),
            "frontages": str(frontages_path),
            "sourceInventory": str(inventory_path),
            "sourceRoadPlan": str(road_plan_path),
        },
        "outputs": {"deterministicBuildInput": str(input_path)},
        "sourceAuditCrossChecks": {
            "auditRoleAssignmentsWithoutProposal": source_audit.get("sourceAccounting", {}).get("roleAssignmentsWithoutProposal"),
            "auditFrontageGroupsWithoutProposal": source_audit.get("frontages", {}).get("groups_without_proposal"),
            "auditUniqueProposedSites": source_audit.get("matching", {}).get("uniqueProposedSites"),
        },
        "frontages": frontage_stats,
        "members": {key: value for key, value in member_stats.items() if key != "uncovered_source_ids"},
        "sourceGeometry": position_stats,
        "sourceRoadGraph": {
            **road_meta,
            "zonable_source_edge_count": len(zonable_edges),
            "frontage_radius_m": FRONTAGE_RADIUS_M,
        },
        "uncovered": {
            "aggregate_group_count": len(all_rows),
            "role_assignment_count": sum(row["role_assignment_count"] for row in all_rows),
            "unique_model_id_count": len(member_stats["uncovered_source_ids"]),
            "road_extension_group_count": sum(row["road_extension_need"] for row in all_rows),
            "road_extension_role_assignment_count": sum(
                row["role_assignment_count"] for row in all_rows if row["road_extension_need"]
            ),
            "road_extension_unique_model_id_count": len(
                set().union(*extension_sets_by_ward.values()) if extension_sets_by_ward else set()
            ),
            "groups_by_ward": per_ward,
            "groups_by_use": per_use,
            "groups_by_ward_use": _nested_summary(per_ward_use),
        },
        "priority": {
            "ordering": ["road_extension_need_desc", "model_id_count_desc", "nearest_zonable_source_edge_distance_m_desc", "ward_asc", "use_asc", "group_asc"],
            "top_groups": [
                {
                    "priority_rank": row["priority_rank"],
                    "group": row["group"],
                    "ward": row["ward"],
                    "use": row["use"],
                    "model_id_count": row["model_id_count"],
                    "nearest_zonable_source_edge_distance_m": row["nearest_zonable_source_edge_distance_m"],
                    "extension_reason": row["extension_reason"],
                }
                for row in all_rows[:25]
            ],
        },
        "assumptions": [
            "An uncovered member row is counted once per sourceId/roleGroup assignment; model_id_count is unique sourceId count within that aggregate group.",
            "Ward/use are parsed from the roleGroup key emitted by the v2 planner; no use or ward is inferred from proximity.",
            "Source model centroids use the exact source inventory coordinates for the uncovered source IDs.",
            "Road distances use the transformed source-road plan geometry. Nearest edges are evidence only and are not build assignments.",
            "A road extension need means no current zonable source edge within the planner frontage radius; native zoning-cell availability remains unverified.",
        ],
    }
    audit_output = output_dir / "native-zoning-gap-audit.json"
    audit_output.write_text(json.dumps(audit_result, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    audit_result["outputs"]["audit"] = str(audit_output)
    audit_output.write_text(json.dumps(audit_result, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return audit_result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--members", type=Path, default=DEFAULT_MEMBERS)
    parser.add_argument("--audit", type=Path, default=DEFAULT_AUDIT)
    parser.add_argument("--frontages", type=Path, default=DEFAULT_FRONTAGES)
    parser.add_argument("--source-inventory", type=Path, default=DEFAULT_INVENTORY)
    parser.add_argument("--road-plan", type=Path, default=DEFAULT_ROAD_PLAN)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args()
    result = audit_gaps(args.members, args.audit, args.frontages, args.source_inventory, args.road_plan, args.output)
    print(json.dumps({"status": result["status"], "outputs": result["outputs"], "uncovered": result["uncovered"]}, ensure_ascii=False))


if __name__ == "__main__":
    main()
