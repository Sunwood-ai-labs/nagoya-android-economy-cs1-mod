"""Fit native road heights without adding artificial road connections."""

import argparse
import json
import math
from collections import Counter
from pathlib import Path

import numpy as np
from scipy.optimize import linprog
from scipy.sparse import coo_matrix

from normalize_native_city_grades import check_crossings, iter_crossings, road_tag


MAX_GRADE = .16
MIN_CLEARANCE = 5.05


def preference(segment):
    source, _, _, tag, layer, _ = segment
    level = int(layer)
    category = -1 if "tunnel" in tag else 1 if ("bridge" in tag or "elevated" in tag) else 0
    return (level, category, "highway" in tag, source)


def fit(nodes, segments, output):
    positions, edges = {}, []
    for line in nodes.read_text(encoding="utf-8").splitlines():
        tag, key, x, z, y = line.split("\t")
        if tag != "N" or key in positions:
            raise ValueError(f"Invalid native road node {key}")
        positions[key] = float(x), float(z), float(y)
    for line in segments.read_text(encoding="utf-8").splitlines():
        tag, *row = line.split("\t")
        if tag != "E":
            raise ValueError(f"Invalid native road segment {line}")
        edges.append(tuple(row))

    node_index = {key: i for i, key in enumerate(positions)}
    n = len(node_index)
    intersections = list(iter_crossings(positions, edges))
    columns, rows, data, limits = [], [], [], []

    def constrain(terms, bound):
        for column, coefficient in terms:
            rows.append(len(limits))
            columns.append(column)
            data.append(coefficient)
        limits.append(bound)

    weights = np.ones(n)
    for _, start, end, tag, _, _ in edges:
        a, b = node_index[start], node_index[end]
        distance = math.dist(positions[start][:2], positions[end][:2])
        if distance < 4:
            raise ValueError("Road plan contains a shorter-than-4m edge")
        limit = distance * (MAX_GRADE - .002)
        constrain(((a, 1), (b, -1)), limit)
        constrain(((a, -1), (b, 1)), limit)
        if tag in ("basic", "arterial"):
            weights[a] = max(weights[a], 3)
            weights[b] = max(weights[b], 3)
    for key, (_, _, height) in positions.items():
        i = node_index[key]
        constrain(((i, 1), (n + i, -1)), height)
        constrain(((i, -1), (n + i, -1)), -height)

    for k, (first, second, t, s) in enumerate(intersections):
        _, a, b, _, _, _ = edges[first]
        _, c, d, _, _, _ = edges[second]
        first_prior = positions[a][2] * (1 - t) + positions[b][2] * t
        second_prior = positions[c][2] * (1 - s) + positions[d][2] * s
        # Preserve every discernible vertical ordering in the source plan.
        sign = 1 if first_prior > second_prior else -1
        if abs(first_prior - second_prior) < .05:
            sign = 1 if preference(edges[first]) > preference(edges[second]) else -1
        constrain(((node_index[a], -sign * (1 - t)), (node_index[b], -sign * t),
                   (node_index[c], sign * (1 - s)), (node_index[d], sign * s),
                   (n * 2 + k, -1)), -MIN_CLEARANCE)

    total = n * 2 + len(intersections)
    objective = np.r_[np.zeros(n), weights, np.full(len(intersections), 10000.)]
    matrix = coo_matrix((data, (rows, columns)), shape=(len(limits), total)).tocsr()
    solution = linprog(objective, A_ub=matrix, b_ub=np.array(limits),
                       bounds=[(-22, 28)] * n + [(0, None)] * (total - n),
                       method="highs", options={"time_limit": 25})
    if not solution.success:
        raise RuntimeError(f"Native height fitting stopped: {solution.message}")

    result = {key: round(float(solution.x[i]), 3) for key, i in node_index.items()}
    prior_conflicts, conflicts = check_crossings(positions, edges, result)
    upper_grade = max(abs(result[start] - result[end]) / math.dist(positions[start][:2], positions[end][:2])
                      for _, start, end, _, _, _ in edges)
    if upper_grade > MAX_GRADE:
        raise ValueError("Rounded native heights exceeded the gradient limit")
    classes = Counter()
    with (output / "native-roads-clearance-adjusted.tsv").open("w", encoding="utf-8", newline="\n") as stream:
        for key, (x, z, _) in positions.items():
            stream.write(f"N\t{key}\t{x:.3f}\t{z:.3f}\t{result[key]:.3f}\n")
        for source, start, end, tag, layer, forward in edges:
            variant = road_tag(tag, result[start], result[end])
            classes[variant] += 1
            stream.write(f"E\t{source}\t{start}\t{end}\t{variant}\t{layer}\t{forward}\n")
    report = {
        "status": "height_fit_has_clearance_conflicts" if conflicts else "all_tested_centerline_crossings_clear",
        "segments": len(edges), "nodes": n, "crossingsTested": len(intersections),
        "crossingsBelowFiveMetresBefore": prior_conflicts, "crossingsBelowFiveMetresAfter": len(conflicts),
        "remainingConflicts": conflicts, "maximumGrade": round(upper_grade, 5),
        "prefabTags": dict(classes), "writesToGame": False,
    }
    (output / "native-road-height-fit-audit.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    return {key: value for key, value in report.items() if key != "remainingConflicts"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--nodes", type=Path, required=True)
    parser.add_argument("--segments", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(fit(args.nodes, args.segments, args.output), ensure_ascii=False))


if __name__ == "__main__":
    main()
