"""Position existing PLATEAU meshes in the real-terrain project's coordinate system."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import struct

import numpy as np

from nagoya_map import ROOT

SOURCE = ROOT.parent / "runtime" / "block-models-1901-roof-g-split-v11.tsv"
SCALE = .3609817361802207
CENTER_X, CENTER_Z = 3983.025225638404, -2625.1867492695537
LANDMARKS = {"hero-finish-v1", "station-roofs-v5", "uv-ten-integrate-v1", "expansion-v2",
             "roof-G-split-material-v11", "appearance2050-station-roofs-v4"}
CHUNKS = {"city-full-lod1-v1", "mass-station-v1", "mass-station-v2"}


def world_xz(x, z):
    return (float(x) - CENTER_X) * SCALE, (float(z) - CENTER_Z) * SCALE


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--all-models", action="store_true", help="prepare the full citywide PLATEAU manifest separately")
    args = parser.parse_args()
    rows = [line.split("\t") for line in SOURCE.read_text(encoding="utf-8-sig").splitlines()]
    header = "\t".join(rows[0])
    items = []
    chosen = set()
    buckets = {}
    for row in rows[1:]:
        asset_id, directory, name, triangles, x, z, texture = row
        path = Path(directory)
        group = path.parts[3]
        wx, wz = world_xz(x, z)
        if args.all_models:
            # The two plazas were fitted against a different native terrain save.
            if group != "publicrealm-v1":
                items.append((row, path, wx, wz))
            continue
        if group not in CHUNKS and group not in LANDMARKS:
            continue
        count = int(triangles)
        if not 12 <= count <= 19000:
            continue
        if abs(wx) > 5200 or abs(wz) > 5000:
            continue
        if group in LANDMARKS and math.dist((wx, wz), (-1438, 948)) < 830:
            items.append((row, path, wx, wz))
            chosen.add(asset_id)
        elif group in CHUNKS and (path / (name + ".meshbin")).exists():
            grid = math.floor(wx / 780), math.floor(wz / 780)
            buckets.setdefault(grid, []).append((row, path, wx, wz))

    total = sum(int(p[0][3]) for p in items)
    for cursor in range(9):
        for grid, candidates in sorted(buckets.items()):
            ranked = sorted(candidates, key=lambda p: math.dist((p[2], p[3]), (-1438, 948)))
            local = math.dist((grid[0] * 780, grid[1] * 780), (-1438, 948)) < 1800
            if cursor >= len(ranked) or (cursor >= 2 and not local):
                continue
            row, path, wx, wz = ranked[cursor]
            if row[0] in chosen or total + int(row[3]) > 850000:
                continue
            chosen.add(row[0])
            total += int(row[3])
            items.append((row, path, wx, wz))

    assets = ROOT / ("visual-assets-citywide" if args.all_models else "visual-assets")
    assets.mkdir(exist_ok=True)
    manifest = [header]
    source_ids = set()
    for row, source, wx, wz in items:
        asset_id, _, name, triangles, _, _, diffuse = row
        dest = assets / asset_id
        dest.mkdir(exist_ok=True)
        bin_file = source / (name + ".meshbin")
        obj_file = source / (name + ".obj")
        catalog = source / (name + ".sources.tsv")
        if bin_file.exists() and catalog.exists():
            mesh = bin_file.read_bytes()
            magic, vertices, faces = struct.unpack("<4sII", mesh[:12])
            if magic != b"NCM1" or vertices != 3 * faces or faces != int(triangles):
                raise RuntimeError(f"Invalid source mesh: {bin_file}")
            data = np.frombuffer(mesh[12:], dtype="<f4").reshape(vertices, 8).copy()
            data[:, :3] *= SCALE
            (dest / bin_file.name).write_bytes(mesh[:12] + data.tobytes())
            # The installed loader checks for the OBJ before selecting the binary mesh.
            if not (dest / obj_file.name).exists():
                os.link(obj_file, dest / obj_file.name)
        else:
            edited = []
            for line in obj_file.read_text(encoding="utf-8-sig").splitlines():
                if line.startswith("v "):
                    vertex = [float(value) * SCALE for value in line.split()[1:4]]
                    line = "v " + " ".join(f"{n:.6f}" for n in vertex)
                edited.append(line)
            (dest / obj_file.name).write_text("\n".join(edited) + "\n", encoding="utf-8")
        if catalog.exists():
            records = catalog.read_text(encoding="utf-8-sig").splitlines()
            scaled = [records[0]]
            for record in records[1:]:
                source_id, start, face_count, x, z = record.split("\t")
                if source_id in source_ids:
                    raise RuntimeError(f"Duplicate PLATEAU source ID: {source_id}")
                source_ids.add(source_id)
                tx, tz = world_xz(x, z)
                scaled.append(f"{source_id}\t{start}\t{face_count}\t{tx:.6f}\t{tz:.6f}")
            (dest / catalog.name).write_text("\n".join(scaled) + "\n", encoding="utf-8")
        elif not asset_id.startswith("bldg_authored_publicrealm_"):
            if asset_id in source_ids:
                raise RuntimeError(f"Duplicate PLATEAU source ID: {asset_id}")
            source_ids.add(asset_id)
        if not (dest / diffuse).exists():
            shutil.copyfile(source / diffuse, dest / diffuse)
        manifest.append(f"{asset_id}\t{dest}\t{name}\t{triangles}\t{wx:.6f}\t{wz:.6f}\t{diffuse}")
    out = ROOT / ("refined-visuals-citywide.tsv" if args.all_models else "refined-visuals.tsv")
    out.write_text("\n".join(manifest) + "\n", encoding="utf-8")
    print(json.dumps({"manifest": str(out), "sha256": hashlib.sha256(out.read_bytes()).hexdigest(),
                      "models": len(items), "sourceBuildings": len(source_ids), "triangles": total}, indent=2))


if __name__ == "__main__":
    main()
