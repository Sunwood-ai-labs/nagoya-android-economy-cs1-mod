"""Compile native CS1 zoning requests against the roads that were actually built.

The v2 zoning planner writes a frontage proposal for a *source* road edge.  A
road compiler may split that edge into many native segments, omit it, or keep
it only as a non-zonable bridge/tunnel.  This tool is the small, read-only
join between those two products:

* a proposed site is joined to a zonable compiled segment with the same
  source parent when possible;
* an omitted parent may fall back to the closest zonable frontage in the same
  ward, within the original frontage search radius;
* a request is emitted only for a matched site.  Unmatched sites and every
  source/role membership are retained in the audit (and the optional members
  / unmatched TSVs) so that a missing model cannot be hidden by de-duplication.

No game or save-file writes are performed.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
import re
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable, Iterator


DEFAULT_FRONTAGES = Path(r"H:\CS1-PLATEAU\nagoya-station\citywide-zoning-v2\block-frontages.csv")
DEFAULT_LINKS = Path(r"H:\CS1-PLATEAU\nagoya-station\citywide-zoning-v2\building-block-links.csv")
DEFAULT_OUTPUT = Path(r"H:\CS1-PLATEAU\nagoya-station\citywide-zoning-v3")

ALLOWED_ZONES = {
    "ResidentialLow",
    "ResidentialHigh",
    "CommercialLow",
    "CommercialHigh",
    "Industrial",
    "Office",
}

# These are the only stock road families on which CS1 zoning can be attached.
# The values deliberately mirror plan_plateau_native_blocks.py.
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

MAX_FRONTAGE_DISTANCE = 160.0
DIRECT_PARENT_DISTANCE = 160.0
EPSILON = 1e-7
GROUP_RE = re.compile(r"^(?P<ward>.+):(?P<gx>-?\d+):(?P<gz>-?\d+):(?P<zone>[^:]+)$")


def _text(value: Any) -> str:
    return "" if value is None else str(value).strip()


def _bool(value: Any) -> bool | None:
    if value is None or value == "":
        return None
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)):
        return bool(value)
    text = _text(value).lower()
    if text in {"1", "true", "yes", "y", "on", "built", "compiled", "surface"}:
        return True
    if text in {"0", "false", "no", "n", "off", "removed", "omitted", "uncompiled"}:
        return False
    return None


def _float(value: Any) -> float | None:
    try:
        result = float(value)
    except (TypeError, ValueError):
        return None
    return result if math.isfinite(result) else None


def _read_json(path: Path) -> Any:
    with path.open("r", encoding="utf-8-sig") as stream:
        return json.load(stream)


def _candidate_files(directory: Path) -> list[Path]:
    """Find a compiled-road artifact without silently selecting a planning file."""

    names = (
        "compiled-native-roads.json",
        "compiled-roads.json",
        "native-roads-compiled.json",
        "native-road-compiled.json",
        "compiled-segments.json",
        "road-compile.json",
        "roads-compiled.json",
        "compiled-roads.tsv",
        "compiled-roads.csv",
    )
    found = [directory / name for name in names if (directory / name).is_file()]
    if found:
        return found
    return sorted(
        p
        for p in directory.iterdir()
        if p.is_file()
        and p.suffix.lower() in {".json", ".csv", ".tsv"}
        and any(token in p.stem.lower() for token in ("compiled", "compile", "native-road"))
        and "plan" not in p.stem.lower()
    )


def resolve_road_path(path: Path | None, output: Path) -> Path:
    if path is not None:
        if path.is_file():
            return path
        if path.is_dir():
            choices = _candidate_files(path)
            if len(choices) == 1:
                return choices[0]
            if not choices:
                raise FileNotFoundError(f"No compiled-road artifact found in {path}")
            raise ValueError(
                "More than one compiled-road artifact found; pass --compiled-roads explicitly: "
                + ", ".join(str(item) for item in choices)
            )
        raise FileNotFoundError(path)
    choices = _candidate_files(output)
    if len(choices) == 1:
        return choices[0]
    if not choices:
        raise FileNotFoundError(
            "No compiled-road artifact supplied. Pass --compiled-roads (or --roads)."
        )
    raise ValueError(
        "More than one compiled-road artifact found in the staging directory; pass --compiled-roads."
    )


def _iter_json_records(value: Any) -> Iterator[dict[str, Any]]:
    """Extract segment records from common compiler envelopes.

    This intentionally does not recursively treat every nested object as a
    road: metadata often contains point-like dictionaries and would otherwise
    become false segments.
    """

    if isinstance(value, list):
        for item in value:
            if isinstance(item, dict):
                yield item
        return
    if not isinstance(value, dict):
        return
    for key in (
        "segments",
        "compiledSegments",
        "compiled_segments",
        "roads",
        "edges",
        "compiled",
        "records",
        "items",
    ):
        nested = value.get(key)
        if isinstance(nested, list):
            for item in nested:
                if isinstance(item, dict):
                    yield item
            return
        if isinstance(nested, dict):
            # A map keyed by a native segment id is also a useful compiler
            # output shape.  Retain the key as a fallback id.
            for item_id, item in nested.items():
                if isinstance(item, dict):
                    row = dict(item)
                    row.setdefault("id", item_id)
                    yield row
            return
    # Some compilers use an envelope one level deeper, e.g. {"result": {...}}.
    for key in ("result", "network", "roadNetwork", "output", "data"):
        nested = value.get(key)
        if isinstance(nested, (dict, list)):
            yield from _iter_json_records(nested)
            return
    # A single segment object is accepted when it has a useful geometry key.
    if any(key in value for key in ("pointsXZ", "points", "start", "from", "x")):
        yield value


def _parse_point(value: Any) -> tuple[float, float] | None:
    if isinstance(value, dict):
        x = _float(value.get("x", value.get("X")))
        z = _float(value.get("z", value.get("Z")))
        if x is not None and z is not None:
            return x, z
        # Unity-like position wrappers occasionally use {position:{x,z}}.
        for key in ("position", "pos", "coordinate", "coordinates"):
            if key in value:
                point = _parse_point(value[key])
                if point is not None:
                    return point
        return None
    if isinstance(value, (list, tuple)) and len(value) >= 2:
        # pointsXZ are [x,z].  For a 3-vector, [x,y,z] is more likely.
        x = _float(value[0])
        z = _float(value[2] if len(value) >= 3 else value[1])
        if x is not None and z is not None:
            return x, z
    return None


def _points(record: dict[str, Any]) -> tuple[tuple[float, float], ...]:
    for key in ("pointsXZ", "points_xz", "polyline", "points", "geometry"):
        value = record.get(key)
        if isinstance(value, (list, tuple)):
            points = tuple(point for point in (_parse_point(item) for item in value) if point is not None)
            if len(points) >= 2:
                return points
    points = []
    for key in ("start", "fromPoint", "from_point", "middle", "mid", "end", "toPoint", "to_point"):
        point = _parse_point(record.get(key))
        if point is not None:
            points.append(point)
    if len(points) >= 2:
        # Keep source order when start/middle/end are present; otherwise the
        # two endpoint fields are enough for a distance test.
        return tuple(points)
    x, z = _float(record.get("x")), _float(record.get("z"))
    return ((x, z),) if x is not None and z is not None else ()


def _nested_values(value: Any) -> Iterator[Any]:
    if isinstance(value, (list, tuple, set)):
        for item in value:
            yield from _nested_values(item)
    elif isinstance(value, dict):
        for key in ("id", "source", "sourceEdgeId", "source_edge_id", "parent", "parentId"):
            if key in value:
                yield from _nested_values(value[key])
    else:
        text = _text(value)
        if text:
            yield text


def _parent_ids(record: dict[str, Any]) -> tuple[str, ...]:
    ids: list[str] = []
    # The first two are the canonical names used by the current road compiler.
    for key in (
        "parentSourceEdgeId",
        "parent_source_edge_id",
        "sourceEdgeId",
        "source_edge_id",
        "sourceParentId",
        "source_parent_id",
        "sourceParent",
        "source_parent",
        "sourceEdge",
        "source_edge",
        "sourceRoadId",
        "source_road_id",
        "source",
        "parent",
        "parentId",
        "parent_id",
    ):
        if key not in record:
            continue
        for value in _nested_values(record[key]):
            if value not in ids:
                ids.append(value)
    return tuple(ids)


def _record_id(record: dict[str, Any], ordinal: int) -> str:
    for key in (
        "nativeSegmentId",
        "native_segment_id",
        "segmentId",
        "segment_id",
        "compiledId",
        "compiled_id",
        "nativeId",
        "native_id",
        "id",
        "edgeId",
        "edge_id",
        "key",
    ):
        value = _text(record.get(key))
        if value:
            return value
    return f"compiled_segment_{ordinal:06d}"


def _record_ward(record: dict[str, Any]) -> tuple[str, ...]:
    values: list[str] = []
    for key in ("ward", "wards", "district", "districtName", "wardName", "ward_name"):
        if key not in record:
            continue
        for value in _nested_values(record[key]):
            if value and value not in values:
                values.append(value)
    return tuple(values)


def _record_is_compiled(record: dict[str, Any]) -> bool:
    # Explicit negative provenance always wins.  Missing fields are accepted
    # because the compiler's segment list itself is the compiled artifact.
    for key in ("compiled", "isCompiled", "is_compiled", "built", "actual", "exists"):
        value = _bool(record.get(key))
        if value is False:
            return False
    for key in ("status", "state", "phase"):
        text = _text(record.get(key)).lower()
        if text in {"removed", "omitted", "excluded", "planned", "uncompiled", "not_built", "missing"}:
            return False
    return True


def _record_is_zonable(record: dict[str, Any]) -> bool:
    for key in ("zoningEligible", "zoning_eligible", "zonable", "isZonable", "is_zonable", "frontage"):
        value = _bool(record.get(key))
        if value is not None:
            return value
    kind = _text(record.get("kind", record.get("roadKind", record.get("road_kind")))).lower()
    if kind and kind not in ZONING_ROADS:
        return False
    pedestrian = _bool(record.get("pedestrianAllowed", record.get("pedestrian_allowed")))
    if pedestrian is False:
        return False
    layer = _text(record.get("layer"))
    if layer and layer not in {"0", "0.0"}:
        return False
    for key in ("bridge", "tunnel"):
        value = _text(record.get(key)).lower()
        if value and value not in {"no", "false", "0", "none", ""}:
            return False
    # A record with no kind is accepted only when the compiler explicitly
    # declared it zonable (handled above).  This avoids treating arbitrary
    # native road inventories as zoning frontage.
    return bool(kind)


def _distance_squared_to_polyline(x: float, z: float, points: tuple[tuple[float, float], ...]) -> tuple[float, tuple[float, float]]:
    if not points:
        return math.inf, (x, z)
    if len(points) == 1:
        px, pz = points[0]
        return (x - px) ** 2 + (z - pz) ** 2, (px, pz)
    best_distance = math.inf
    best_point = points[0]
    for (ax, az), (bx, bz) in zip(points, points[1:]):
        dx, dz = bx - ax, bz - az
        length_squared = dx * dx + dz * dz
        if length_squared <= EPSILON:
            candidate = (ax, az)
        else:
            t = max(0.0, min(1.0, ((x - ax) * dx + (z - az) * dz) / length_squared))
            candidate = (ax + t * dx, az + t * dz)
        distance = (x - candidate[0]) ** 2 + (z - candidate[1]) ** 2
        if distance < best_distance:
            best_distance, best_point = distance, candidate
    return best_distance, best_point


@dataclass
class CompiledSegment:
    segment_id: str
    parent_ids: tuple[str, ...]
    points: tuple[tuple[float, float], ...]
    wards: tuple[str, ...]
    record: dict[str, Any] = field(repr=False)

    def distance(self, x: float, z: float) -> float:
        return math.sqrt(_distance_squared_to_polyline(x, z, self.points)[0])


@dataclass
class Site:
    ward: str
    zone: str
    x: float
    z: float
    groups: list[str] = field(default_factory=list)
    parent_ids: set[str] = field(default_factory=set)
    group_declared_members: int = 0
    role_members: int = 0
    sources: set[str] = field(default_factory=set)
    key: str = ""
    segment: CompiledSegment | None = None
    match_mode: str = "unmatched"
    match_distance_m: float | None = None
    match_reason: str = ""


def _group_parts(group: str) -> tuple[str, int, int, str]:
    match = GROUP_RE.match(group)
    if not match:
        raise ValueError(f"Invalid zoning group key: {group!r}")
    zone = match.group("zone")
    if zone not in ALLOWED_ZONES:
        raise ValueError(f"Unsupported stock zoning class in group {group!r}: {zone!r}")
    return match.group("ward"), int(match.group("gx")), int(match.group("gz")), zone


def _site_key(ward: str, zone: str, x: float, z: float) -> str:
    # A coordinate hash is stable if a preceding site is added or removed and
    # never leaks Japanese ward text into the ASCII identifier.
    digest = hashlib.sha1(f"{ward}\0{zone}\0{x:.9f}\0{z:.9f}".encode("utf-8")).hexdigest()[:12]
    return f"site_{digest}"


def load_frontages(path: Path) -> tuple[dict[str, dict[str, Any]], dict[str, Site], Counter[str]]:
    groups: dict[str, dict[str, Any]] = {}
    sites_by_identity: dict[tuple[str, str, str, str], Site] = {}
    stats: Counter[str] = Counter()
    with path.open("r", encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        required = {"group", "source_building_uses", "proposed_x", "proposed_z", "road_source"}
        missing = sorted(required - set(reader.fieldnames or ()))
        if missing:
            raise ValueError(f"Missing frontage columns: {missing}")
        for row_number, row in enumerate(reader, 2):
            group = _text(row.get("group"))
            if not group:
                raise ValueError(f"Empty frontage group at line {row_number}")
            ward, gx, gz, zone = _group_parts(group)
            try:
                declared = int(row.get("source_building_uses") or 0)
            except ValueError as exc:
                raise ValueError(f"Invalid source_building_uses at line {row_number}") from exc
            px, pz = _float(row.get("proposed_x")), _float(row.get("proposed_z"))
            road_source = _text(row.get("road_source"))
            if group in groups:
                raise ValueError(f"Duplicate frontage group: {group!r}")
            info = {
                "group": group,
                "ward": ward,
                "grid_x": gx,
                "grid_z": gz,
                "zone": zone,
                "declared_members": declared,
                "x": px,
                "z": pz,
                "road_source": road_source,
                "part": _text(row.get("part")),
                "slot": _text(row.get("slot")),
                "side": _text(row.get("side")),
            }
            groups[group] = info
            stats["frontage_groups"] += 1
            if px is None or pz is None or not road_source:
                stats["groups_without_proposal"] += 1
                continue
            identity = (ward, zone, f"{px:.9f}", f"{pz:.9f}")
            site = sites_by_identity.get(identity)
            if site is None:
                site = Site(ward=ward, zone=zone, x=px, z=pz)
                site.key = _site_key(ward, zone, px, pz)
                sites_by_identity[identity] = site
                stats["unique_proposed_sites"] += 1
            site.groups.append(group)
            site.parent_ids.add(road_source)
            site.group_declared_members += declared
            stats["proposal_rows"] += 1
    # The v2 planner's advertised number is a useful invariant, but do not
    # hard-code it: a future data refresh can legitimately change the count.
    return groups, {site.key: site for site in sites_by_identity.values()}, stats


def load_links(path: Path, groups: dict[str, dict[str, Any]], sites: dict[str, Site]) -> dict[str, Any]:
    """Join every source ID/role without retaining the 56 MB CSV in memory."""

    group_role_counts: Counter[str] = Counter()
    all_sources: set[str] = set()
    source_role_assignments = 0
    source_rows = 0
    sources_with_roles = 0
    malformed_roles = 0
    source_coverage = Counter()
    site_by_group: dict[str, Site] = {}
    for site in sites.values():
        for group in site.groups:
            site_by_group[group] = site

    with path.open("r", encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        required = {"source", "role_groups"}
        missing = sorted(required - set(reader.fieldnames or ()))
        if missing:
            raise ValueError(f"Missing link columns: {missing}")
        for row_number, row in enumerate(reader, 2):
            source = _text(row.get("source"))
            if not source:
                raise ValueError(f"Empty source at links line {row_number}")
            source_rows += 1
            all_sources.add(source)
            roles_text = _text(row.get("role_groups"))
            roles = [item for item in roles_text.split("|") if item]
            if roles:
                sources_with_roles += 1
            for group in roles:
                if group not in groups:
                    malformed_roles += 1
                    continue
                group_role_counts[group] += 1
                source_role_assignments += 1
                site = site_by_group.get(group)
                if site is None:
                    source_coverage["uncovered_no_proposal"] += 1
                else:
                    site.role_members += 1
                    site.sources.add(source)
                    source_coverage["covered_by_proposed_site"] += 1

    mismatches = []
    for group, info in groups.items():
        actual = group_role_counts[group]
        if actual != info["declared_members"]:
            mismatches.append({"group": group, "declared": info["declared_members"], "actual": actual})
    if mismatches:
        sample = mismatches[:5]
        raise ValueError(f"Frontage/link member counts disagree for {len(mismatches)} groups; sample={sample}")
    return {
        "source_rows": source_rows,
        "unique_source_ids": len(all_sources),
        "sources_with_roles": sources_with_roles,
        "source_role_assignments": source_role_assignments,
        "source_coverage": dict(source_coverage),
        "group_role_counts": group_role_counts,
        "mismatched_groups": mismatches,
        "malformed_roles": malformed_roles,
    }


def _read_compiled_records(path: Path) -> list[dict[str, Any]]:
    if path.suffix.lower() in {".csv", ".tsv"}:
        # NativeCityRoadBuilder's import contract is an intentionally small
        # N/E TSV: N<tab>node<tab>x<tab>z<tab>heightOffset and
        # E<tab>source<tab>from<tab>to<tab>prefab<tab>layer<tab>direction.
        # Turn it into the same coordinate-bearing record shape as the JSON
        # compiler output.  This also lets a dry-run road compiler hand this
        # tool its exact final plan without a second conversion step.
        with path.open("r", encoding="utf-8-sig", newline="") as probe:
            first = probe.readline().rstrip("\r\n").split("\t")
        if first and first[0] in {"N", "E"}:
            node_points: dict[str, tuple[float, float]] = {}
            edges: list[list[str]] = []
            def read_ne_file(source: Path) -> None:
                with source.open("r", encoding="utf-8-sig", newline="") as stream:
                    for row in csv.reader(stream, delimiter="\t"):
                        if not row:
                            continue
                        if row[0] == "N" and len(row) == 5:
                            x = _float(row[2])
                            z = _float(row[3])
                            height = _float(row[4])
                            if x is None or z is None or height is None or row[1] in node_points:
                                raise ValueError(f"Invalid or duplicate node: {row[1]}")
                            node_points[row[1]] = (x, z)
                        elif row[0] == "E" and len(row) == 7:
                            edges.append(row)

            read_ne_file(path)
            # The road compiler deliberately keeps nodes and segments in
            # separate bounded TSVs.  If the caller passed the segment file,
            # load its sibling node file to recover the coordinate schema.
            if not node_points:
                sibling_names = (
                    "native-road-nodes.tsv",
                    "compiled-road-nodes.tsv",
                    "road-nodes.tsv",
                )
                for name in sibling_names:
                    sibling = path.with_name(name)
                    if sibling.is_file():
                        read_ne_file(sibling)
                        if node_points:
                            break
            records: list[dict[str, Any]] = []
            for row in edges:
                start, end = node_points.get(row[2]), node_points.get(row[3])
                if start is None or end is None:
                    # Preserve the record so the normal audit explains the
                    # omitted coordinates instead of silently dropping it.
                    points: list[list[float]] = []
                else:
                    points = [list(start), list(end)]
                prefab = row[4].strip().lower()
                records.append({
                    "id": f"compiled_{len(records):06d}",
                    "sourceEdgeId": row[1],
                    "pointsXZ": points,
                    # Basic and arterial are the stock surface zoning roads;
                    # highway/elevated/bridge/tunnel are intentionally not.
                    "kind": "residential" if prefab == "basic" else "primary" if prefab == "arterial" else prefab,
                    "zoningEligible": prefab in {"basic", "arterial"},
                    "compiled": True,
                })
            return records
        with path.open("r", encoding="utf-8-sig", newline="") as stream:
            dialect = csv.excel_tab if path.suffix.lower() == ".tsv" else csv.excel
            return [dict(row) for row in csv.DictReader(stream, dialect=dialect)]
    return list(_iter_json_records(_read_json(path)))


def load_compiled_roads(path: Path, parent_wards: dict[str, set[str]]) -> tuple[list[CompiledSegment], Counter[str]]:
    records = _read_compiled_records(path)
    if not records:
        raise ValueError(f"Compiled-road artifact contains no segment records: {path}")
    stats: Counter[str] = Counter()
    segments: list[CompiledSegment] = []
    seen_ids: set[str] = set()
    for ordinal, record in enumerate(records, 1):
        if not _record_is_compiled(record):
            stats["records_excluded_not_compiled"] += 1
            continue
        stats["compiled_records"] += 1
        parents = _parent_ids(record)
        if not parents:
            stats["compiled_records_without_parent"] += 1
            continue
        points = _points(record)
        if not points:
            stats["compiled_records_without_coordinates"] += 1
            continue
        if not _record_is_zonable(record):
            stats["compiled_non_zonable_records"] += 1
            continue
        segment_id = _record_id(record, ordinal)
        if segment_id in seen_ids:
            # Stable but unambiguous ID for malformed duplicate records.
            segment_id = f"{segment_id}__{ordinal}"
        seen_ids.add(segment_id)
        explicit_wards = set(_record_ward(record))
        wards = set(explicit_wards)
        for parent in parents:
            wards.update(parent_wards.get(parent, ()))
        segment = CompiledSegment(
            segment_id=segment_id,
            parent_ids=parents,
            points=points,
            wards=tuple(sorted(wards)),
            record=record,
        )
        segments.append(segment)
        stats["zonable_compiled_segments"] += 1
        stats["segments_with_explicit_ward"] += bool(explicit_wards)
    if not segments:
        raise ValueError(
            f"No zonable compiled road segments found in {path}; records={len(records)} stats={dict(stats)}"
        )
    stats["raw_records"] = len(records)
    return segments, stats


def _choose_nearest(site: Site, candidates: Iterable[CompiledSegment]) -> tuple[CompiledSegment | None, float | None]:
    best: tuple[float, str, CompiledSegment] | None = None
    for segment in candidates:
        distance = segment.distance(site.x, site.z)
        # A point-less segment is not emitted by load_compiled_roads, but keep
        # this guard if the dataclass is constructed by a caller in a test.
        if not math.isfinite(distance):
            continue
        value = (distance, segment.segment_id, segment)
        if best is None or value[:2] < best[:2]:
            best = value
    return (best[2], best[0]) if best is not None else (None, None)


def match_sites(sites: dict[str, Site], segments: list[CompiledSegment]) -> Counter[str]:
    by_parent: dict[str, list[CompiledSegment]] = defaultdict(list)
    by_ward: dict[str, list[CompiledSegment]] = defaultdict(list)
    for segment in segments:
        for parent in segment.parent_ids:
            by_parent[parent].append(segment)
        for ward in segment.wards:
            by_ward[ward].append(segment)
    stats: Counter[str] = Counter()
    for site in sorted(sites.values(), key=lambda item: item.key):
        direct_candidates = []
        for parent in sorted(site.parent_ids):
            direct_candidates.extend(by_parent.get(parent, ()))
        direct, direct_distance = _choose_nearest(site, direct_candidates)
        if direct is not None and (direct_distance is None or direct_distance <= DIRECT_PARENT_DISTANCE):
            site.segment = direct
            site.match_mode = "same_parent"
            site.match_distance_m = direct_distance
            site.match_reason = "zonable compiled segment with original source parent"
            stats["same_parent"] += 1
            continue

        # A source edge can be present but all of its compiled pieces can be
        # non-zonable/removed.  Fall back only inside the source building's
        # administrative ward, never to an arbitrary city-wide road.
        fallback, fallback_distance = _choose_nearest(site, by_ward.get(site.ward, ()))
        if fallback is not None and fallback_distance is not None and fallback_distance <= MAX_FRONTAGE_DISTANCE:
            site.segment = fallback
            site.match_mode = "same_ward_fallback"
            site.match_distance_m = fallback_distance
            site.match_reason = "original parent absent/non-zonable; closest same-ward zonable frontage"
            stats["same_ward_fallback"] += 1
            continue

        site.segment = None
        site.match_mode = "unmatched"
        site.match_distance_m = fallback_distance
        site.match_reason = (
            "no same-parent zonable segment and no same-ward zonable frontage within "
            f"{MAX_FRONTAGE_DISTANCE:g}m"
        )
        stats["unmatched"] += 1
    return stats


def _output_source_edge(site: Site) -> str:
    if site.segment is None:
        return ""
    # Keep the original source edge when the match is direct.  For a fallback,
    # use the compiled segment's parent so the loader can trace the request to
    # an actual segment even though its original proposal was removed.
    if site.match_mode == "same_parent":
        shared = sorted(site.parent_ids.intersection(site.segment.parent_ids))
        if shared:
            return shared[0]
        # This should be unreachable because match_sites only selects a
        # segment from a same-parent candidate list.  Keep a deterministic
        # fallback for hand-constructed test records.
        return sorted(site.parent_ids)[0]
    return sorted(site.segment.parent_ids)[0] if site.segment.parent_ids else site.segment.segment_id


def _fmt(value: float) -> str:
    return f"{value:.6f}".rstrip("0").rstrip(".")


def _write_requests(path: Path, sites: Iterable[Site]) -> int:
    rows = [site for site in sites if site.segment is not None]
    rows.sort(key=lambda site: site.key)
    with path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream, delimiter="\t", lineterminator="\n")
        for site in rows:
            writer.writerow((
                "S",
                site.key,
                _output_source_edge(site),
                _fmt(site.x),
                _fmt(site.z),
                site.zone,
                site.role_members,
                site.ward,
            ))
    return len(rows)


def _write_unmatched(path: Path, sites: Iterable[Site]) -> int:
    rows = sorted((site for site in sites if site.segment is None), key=lambda site: site.key)
    with path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream, delimiter="\t", lineterminator="\n")
        writer.writerow(("U", "key", "originalSourceEdgeIds", "x", "z", "zone", "modelMemberCount", "ward", "reason", "nearestDistanceM"))
        for site in rows:
            writer.writerow((
                "U",
                site.key,
                ",".join(sorted(site.parent_ids)),
                _fmt(site.x),
                _fmt(site.z),
                site.zone,
                site.role_members,
                site.ward,
                site.match_reason,
                "" if site.match_distance_m is None else _fmt(site.match_distance_m),
            ))
    return len(rows)


def _write_members(path: Path, groups: dict[str, dict[str, Any]], sites: dict[str, Site], links_path: Path) -> dict[str, int]:
    """Write a compact role-level read-back useful for diagnosing omissions.

    This is deliberately separate from the loader TSV: the loader consumes
    deduplicated sites, while this file makes the source ID/role accounting
    auditable without bloating every loader row.
    """

    site_by_group = {group: site for site in sites.values() for group in site.groups}
    counts = Counter()
    with links_path.open("r", encoding="utf-8-sig", newline="") as source_stream, path.open("w", encoding="utf-8", newline="") as output_stream:
        reader = csv.DictReader(source_stream)
        writer = csv.writer(output_stream, delimiter="\t", lineterminator="\n")
        writer.writerow(("M", "sourceId", "roleGroup", "key", "status"))
        for row in reader:
            source = _text(row.get("source"))
            for group in (_text(row.get("role_groups"))).split("|"):
                if not group:
                    continue
                site = site_by_group.get(group)
                if site is None:
                    writer.writerow(("M", source, group, "", "uncovered_no_proposal"))
                    counts["uncovered_no_proposal"] += 1
                elif site.segment is None:
                    writer.writerow(("M", source, group, site.key, "unmatched_site"))
                    counts["unmatched_site"] += 1
                else:
                    writer.writerow(("M", source, group, site.key, site.match_mode))
                    counts[site.match_mode] += 1
    return dict(counts)


def _nested_count_dict(values: dict[str, Counter[str]]) -> dict[str, dict[str, int]]:
    return {key: dict(sorted(counter.items())) for key, counter in sorted(values.items())}


def compile_sites(frontages: Path, links: Path, compiled_roads: Path, output: Path) -> dict[str, Any]:
    groups, sites, frontage_stats = load_frontages(frontages)
    parent_wards: dict[str, set[str]] = defaultdict(set)
    for info in groups.values():
        road_source = info["road_source"]
        if road_source:
            parent_wards[road_source].add(info["ward"])
    link_audit = load_links(links, groups, sites)
    segments, road_stats = load_compiled_roads(compiled_roads, parent_wards)
    match_stats = match_sites(sites, segments)

    output.mkdir(parents=True, exist_ok=True)
    requests_path = output / "native-zoning-requests.tsv"
    unmatched_path = output / "native-zoning-unmatched.tsv"
    members_path = output / "native-zoning-members.tsv"
    request_count = _write_requests(requests_path, sites.values())
    unmatched_count = _write_unmatched(unmatched_path, sites.values())
    member_write_stats = _write_members(members_path, groups, sites, links)

    per_ward: dict[str, Counter[str]] = defaultdict(Counter)
    per_use: dict[str, Counter[str]] = defaultdict(Counter)
    per_ward_use: dict[str, Counter[str]] = defaultdict(Counter)
    uncovered_ward: Counter[str] = Counter()
    uncovered_use: Counter[str] = Counter()
    for site in sites.values():
        status = site.match_mode
        per_ward[site.ward]["proposedSites"] += 1
        per_ward[site.ward][status] += 1
        per_ward[site.ward]["modelRoleMembers"] += site.role_members
        per_use[site.zone]["proposedSites"] += 1
        per_use[site.zone][status] += 1
        per_use[site.zone]["modelRoleMembers"] += site.role_members
        per_ward_use[f"{site.ward}|{site.zone}"]["proposedSites"] += 1
        per_ward_use[f"{site.ward}|{site.zone}"][status] += 1
        per_ward_use[f"{site.ward}|{site.zone}"]["modelRoleMembers"] += site.role_members
        if site.segment is None:
            per_ward[site.ward]["uncoveredSites"] += 1
            per_use[site.zone]["uncoveredSites"] += 1
            per_ward_use[f"{site.ward}|{site.zone}"]["uncoveredSites"] += 1
            uncovered_ward[site.ward] += 1
            uncovered_use[site.zone] += 1
        else:
            per_ward[site.ward]["matchedSites"] += 1
            per_use[site.zone]["matchedSites"] += 1
            per_ward_use[f"{site.ward}|{site.zone}"]["matchedSites"] += 1

    role_uncovered_ward = Counter()
    role_uncovered_use = Counter()
    for site in sites.values():
        if site.segment is None:
            role_uncovered_ward[site.ward] += site.role_members
            role_uncovered_use[site.zone] += site.role_members
    # Roles without any frontage proposal are already measured by link_audit.

    audit: dict[str, Any] = {
        "status": "compiled native zoning requests; read-only artifact, game writes disabled",
        "inputs": {
            "frontages": str(frontages),
            "buildingBlockLinks": str(links),
            "compiledRoads": str(compiled_roads),
        },
        "outputs": {
            "requests": str(requests_path),
            "unmatched": str(unmatched_path),
            "members": str(members_path),
        },
        "frontages": dict(frontage_stats),
        "roads": dict(road_stats),
        "matching": {
            "uniqueProposedSites": len(sites),
            "requestsWritten": request_count,
            "unmatchedSites": unmatched_count,
            "sameParent": match_stats.get("same_parent", 0),
            "sameWardFallback": match_stats.get("same_ward_fallback", 0),
            "fallbackMaxDistanceM": MAX_FRONTAGE_DISTANCE,
            "directParentMaxDistanceM": DIRECT_PARENT_DISTANCE,
            "maxObservedMatchedDistanceM": max(
                (site.match_distance_m for site in sites.values() if site.match_distance_m is not None),
                default=None,
            ),
        },
        "sourceAccounting": {
            "sourceRows": link_audit["source_rows"],
            "uniqueSourceIds": link_audit["unique_source_ids"],
            "sourcesWithRoles": link_audit["sources_with_roles"],
            "sourceRoleAssignments": link_audit["source_role_assignments"],
            "groupRoleCountMismatches": len(link_audit["mismatched_groups"]),
            "malformedRoleGroups": link_audit["malformed_roles"],
            "roleAssignmentsCoveredByProposedSite": link_audit["source_coverage"].get("covered_by_proposed_site", 0),
            "roleAssignmentsWithoutProposal": link_audit["source_coverage"].get("uncovered_no_proposal", 0),
            "roleAssignmentsInUnmatchedSites": sum(
                site.role_members for site in sites.values() if site.segment is None
            ),
            "memberReadback": member_write_stats,
        },
        "perWard": _nested_count_dict(per_ward),
        "perUse": _nested_count_dict(per_use),
        "perWardUse": _nested_count_dict(per_ward_use),
        "uncovered": {
            "sitesByWard": dict(sorted(uncovered_ward.items())),
            "sitesByUse": dict(sorted(uncovered_use.items())),
            "roleAssignmentsByWardForUnmatchedSites": dict(sorted(role_uncovered_ward.items())),
            "roleAssignmentsByUseForUnmatchedSites": dict(sorted(role_uncovered_use.items())),
            "roleAssignmentsWithoutProposal": link_audit["source_coverage"].get("uncovered_no_proposal", 0),
        },
        "writesToGame": False,
    }
    audit_path = output / "native-zoning-audit.json"
    audit_path.write_text(json.dumps(audit, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    audit["outputs"]["audit"] = str(audit_path)
    # Persist the final audit path in the file as well; this avoids a stale
    # pre-write representation when a caller inspects the JSON directly.
    audit_path.write_text(json.dumps(audit, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return audit


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--frontages", type=Path, default=DEFAULT_FRONTAGES)
    parser.add_argument("--links", type=Path, default=DEFAULT_LINKS)
    parser.add_argument("--compiled-roads", "--roads", dest="compiled_roads", type=Path)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args()
    compiled_roads = resolve_road_path(args.compiled_roads, args.output)
    if not args.frontages.is_file():
        parser.error(f"Frontage CSV not found: {args.frontages}")
    if not args.links.is_file():
        parser.error(f"Building-block link CSV not found: {args.links}")
    audit = compile_sites(args.frontages, args.links, compiled_roads, args.output)
    print(json.dumps(audit, ensure_ascii=False))


if __name__ == "__main__":
    main()
