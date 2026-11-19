"""Check straight ground-road corridors against the PLATEAU geometry shown in game."""

import argparse
import csv
import hashlib
import json
import math
import struct
from collections import Counter
from pathlib import Path


def checksum(content, expected, path):
    if not expected or hashlib.sha256(content).hexdigest().lower() != expected.lower():
        raise ValueError(f"The runtime hash does not match {path.name}")


def obj_corners(content):
    vertices = []
    for line in content.decode("utf-8-sig").splitlines():
        fields = line.split()
        if fields and fields[0] == "v":
            vertices.append((float(fields[1]), -float(fields[3])))
        elif fields and fields[0] == "f":
            if len(fields) != 4:
                raise ValueError("Expected triangulated runtime geometry")
            for field in fields[1:]:
                index = int(field.split("/", 1)[0])
                yield vertices[index - 1 if index > 0 else index]


def mesh_triangles(model):
    binary = model["meshFormat"] == "NCM1"
    path = Path(model["directory"]) / (model["name"] + (".meshbin" if binary else ".obj"))
    content = path.read_bytes()
    checksum(content, model["actualMeshSHA256"], path)
    expected = model["triangles"]
    if binary:
        magic, vertices, triangles = struct.unpack_from("<4sII", content)
        if magic != b"NCM1" or triangles != expected or vertices != triangles * 3 or len(content) != 12 + vertices * 32:
            raise ValueError(f"Invalid runtime mesh {path.name}")
        points = ((x, -z) for x, _, z, *_ in struct.iter_unpack("<8f", memoryview(content)[12:]))
    else:
        points = obj_corners(content)
    corners = iter(points)
    return zip(corners, corners, corners, strict=True)


def sources(model):
    path = model.get("sourceCatalogPath")
    if not path:
        return [(model["id"], model["triangles"])]
    path = Path(path)
    content = path.read_bytes()
    checksum(content, model["sourceCatalogSHA256"], path)
    rows, covered = [], 0
    for row in csv.DictReader(content.decode("utf-8-sig").splitlines(), delimiter="\t"):
        count = int(row["triangleCount"])
        if int(row["triangleStart"]) != covered or count <= 0:
            raise ValueError(f"Invalid source span in {path.name}")
        covered += count
        rows.append((row["id"], covered))
    if covered != model["triangles"]:
        raise ValueError(f"Source spans do not cover {path.name}")
    return rows


class Corridor:
    def __init__(self, road):
        start, end = road["start"], road["end"]
        self.x, self.z = float(start["x"]), float(start["z"])
        ex, ez = float(end["x"]), float(end["z"])
        self.half_width = float(road["halfWidth"])
        self.length = math.hypot(ex - self.x, ez - self.z)
        if not all(map(math.isfinite, (self.x, self.z, ex, ez, self.half_width))) or not (0 < self.half_width < 40 and self.length > 0):
            raise ValueError("Expected a finite, nonzero ground-road corridor")
        self.dx, self.dz = (ex - self.x) / self.length, (ez - self.z) / self.length
        w = self.half_width
        self.bounds = min(self.x, ex) - w, min(self.z, ez) - w, max(self.x, ex) + w, max(self.z, ez) + w

    def overlaps(self, model):
        x, _, z = model["boundsCenter"]
        w, _, h = model["bounds"]
        left, bottom, right, top = self.bounds
        return x + w / 2 >= left and x - w / 2 <= right and z + h / 2 >= bottom and z - h / 2 <= top

    def intersects(self, triangle, ox, oz, scale):
        # Separating axes for a triangle and a rectangle aligned to the road.
        mx, mz = ox - self.x, oz - self.z
        points = [((x * scale + mx) * self.dx + (z * scale + mz) * self.dz,
                   (z * scale + mz) * self.dx - (x * scale + mx) * self.dz) for x, z in triangle]
        along, across = zip(*points)
        w = self.half_width
        if max(along) < 0 or min(along) > self.length or max(across) < -w or min(across) > w:
            return False
        for (ax, az), (bx, bz) in zip(points, points[1:] + points[:1]):
            nx, nz = az - bz, bx - ax
            values = [nx * x + nz * z for x, z in points]
            side = abs(nz) * w
            end = nx * self.length
            if max(values) < min(0, end) - side or min(values) > max(0, end) + side:
                return False
        return True


def audit(runtime, roads, transform=None):
    scale = 1.0 if transform is None else float(transform["scale"])
    ox, oz = (0.0, 0.0) if transform is None else map(float, transform["originalCenterXZ"])
    if not all(map(math.isfinite, (ox, oz, scale))) or scale <= 0:
        raise ValueError("Expected a finite map center and a positive scale")
    corridors = [(road, Corridor(road), Counter()) for road in roads]
    inspected = 0
    for model in runtime["models"]:
        if not model["active"]:
            continue
        x, y, z = model["boundsCenter"]
        w, h, d = model["bounds"]
        bounds = {"boundsCenter": ((x - ox) * scale, y, (z - oz) * scale), "bounds": (w * scale, h, d * scale)}
        eligible = [(corridor, hits) for _, corridor, hits in corridors if corridor.overlaps(bounds)]
        if not eligible:
            continue
        model_x, model_z = (model["x"] - ox) * scale, (model["z"] - oz) * scale
        inspected += 1
        limits = iter(sources(model))
        source, stop = next(limits)
        triangle_count = 0
        for index, points in enumerate(mesh_triangles(model)):
            if index == stop:
                source, stop = next(limits)
            triangle_count += 1
            for corridor, hits in eligible:
                if corridor.intersects(points, model_x, model_z, scale):
                    hits[source] += 1
        if triangle_count != model["triangles"]:
            raise ValueError(f"Unexpected triangle count for {model['id']}")
    return {"revision": runtime["revision"], "appliedTransform": transform,
            "activeMeshCount": sum(model["active"] for model in runtime["models"]),
            "inspectedMeshCount": inspected, "roads": [dict(road, overlappingSources=dict(sorted(hits.items())))
            for road, _, hits in corridors]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("runtime", type=Path, help="block-runtime.json exported by the scenery loader")
    parser.add_argument("roads", type=Path, help="array of start, end, halfWidth and optional road metadata")
    parser.add_argument("--transform-plan", type=Path, help="optionally project the read-back meshes using the native road plan")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    runtime = json.loads(args.runtime.read_text(encoding="utf-8-sig"))
    roads = json.loads(args.roads.read_text(encoding="utf-8-sig"))
    if not isinstance(roads, list) or not 1 <= len(roads) <= 100:
        parser.error("Supply between 1 and 100 straight ground-road candidates")
    transform = None
    if args.transform_plan is not None:
        transform = json.loads(args.transform_plan.read_text(encoding="utf-8-sig"))["audit"]["transform"]
    result = audit(runtime, roads, transform)
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(result, output, ensure_ascii=False, indent=2)
        output.write("\n")


if __name__ == "__main__":
    main()
