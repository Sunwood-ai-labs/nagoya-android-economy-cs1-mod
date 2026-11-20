"""Build Nagoya Station rail and pedestrian scenery from surveyed OSM geometry."""
import hashlib
import json
import math

import numpy as np
from PIL import Image, ImageDraw
from pyproj import Transformer
from shapely import LineString, Point, Polygon, box, constrained_delaunay_triangles, make_valid, union_all
from shapely.geometry.polygon import orient

from nagoya_citywide import load_neighborhood_infill, prepare, terrain_height
from nagoya_map import ROOT
from prepare_refined_visuals import world_xz
from smooth_citywide_roads import prepare_smooth_roads

OUT = ROOT / "station-precinct"
SOURCE = ROOT / "roads" / "nagoya-station-osm-full.json"
ORIGIN = (-1438., 948.)
FRAME = box(-1520, 850, -1330, 1080)
ATLAS = "StationPrecinct_d.png"
COLORS = [(108, 106, 101), (90, 95, 99), (38, 39, 37), (116, 120, 119),
          (94, 83, 70), (104, 108, 112), (44, 67, 74), (73, 91, 59)]


def polygons(item):
    if item.is_empty:
        return
    if item.geom_type == "Polygon":
        yield item
    elif hasattr(item, "geoms"):
        for child in item.geoms:
            yield from polygons(child)


def coordinates(doc):
    anchor = json.loads((ROOT.parent / "source" / "district-ground-footprints.json").read_bytes())
    transform = Transformer.from_crs(4326, 6675, always_xy=True)
    nodes = [e for e in doc["elements"] if e["type"] == "node"]
    east, north = transform.transform([e["lon"] for e in nodes], [e["lat"] for e in nodes])
    return {e["id"]: world_xz(x - anchor["anchor_east_m"], z - anchor["anchor_north_m"])
            for e, x, z in zip(nodes, east, north)}


def classify(doc, points):
    tracks, platforms, plazas, walking, bus_stops = [], [], [], [], []
    for item in doc["elements"]:
        tag = item.get("tags", {})
        if item["type"] == "node" and tag.get("highway") == "bus_stop":
            point = Point(points[item["id"]])
            if FRAME.covers(point):
                bus_stops.append((item["id"], point))
        if item["type"] != "way" or any(node not in points for node in item["nodes"]):
            continue
        path = [points[node] for node in item["nodes"]]
        if len(path) < 2 or not FRAME.intersects(LineString(path)):
            continue
        if int(tag.get("layer", "0")) < 0 or tag.get("tunnel") not in (None, "no") or tag.get("indoor") == "yes":
            continue
        if tag.get("railway") == "rail":
            gauge = .52 if tag.get("gauge") == "1435" else .385
            tracks.append((item["id"], LineString(path).intersection(FRAME), gauge))
        if len(path) >= 4 and path[0] == path[-1]:
            area = make_valid(Polygon(path)).intersection(FRAME)
            if tag.get("railway") == "platform":
                platforms.append((item["id"], area))
            elif tag.get("highway") in {"pedestrian", "footway"} or tag.get("area:highway") == "footway" or tag.get("amenity") == "bus_station":
                if tag.get("bridge") != "yes" and int(tag.get("layer", "0")) == 0:
                    plazas.append((item["id"], area))
        elif tag.get("highway") in {"footway", "pedestrian"} and tag.get("bridge") != "yes":
            walking.append(LineString(path).intersection(FRAME).buffer(.68, cap_style="flat"))
    return tracks, platforms, plazas, walking, bus_stops


def road_clearance(raw, roads, sites, frame=FRAME):
    load_neighborhood_infill(raw, roads, sites)
    roads, _ = prepare_smooth_roads(roads, raw, True)
    shapes = []
    for road in roads:
        if road["roadPrefab"] not in {"Basic Road", "Medium Road", "Highway", "HighwayRamp"}:
            continue
        a, b = (road[k] for k in ("start", "end"))
        line = LineString([(a["x"], a["z"]), (b["x"], b["z"])])
        if frame.buffer(18).intersects(line):
            width = {"Basic Road": 8, "Medium Road": 16, "Highway": 12, "HighwayRamp": 4}[road["roadPrefab"]]
            shapes.append(line.buffer(width + .45))
    return union_all(shapes)


def station_frontage(doc, points):
    # The Towers retail base, mapped pedestrian square and terrace define the east frontage.
    shapes = {}
    for item in doc["elements"]:
        if item["type"] == "way" and item["id"] in (199768172, 680256831, 835253292, 272513944):
            shapes[item["id"]] = make_valid(Polygon([points[node] for node in item["nodes"]]))
    retail = shapes[199768172]
    east_bounds = box(-1391, 913, -1349, 993)
    east = union_all([retail.buffer(10, join_style="mitre"), shapes[835253292].buffer(2), shapes[680256831].buffer(4)])
    east = east.intersection(east_bounds).difference(retail)
    west = shapes[272513944].buffer(1.15, join_style="mitre").intersection(FRAME)
    return east.union(west)


def texture():
    rng = np.random.default_rng(13528)
    sheet = Image.new("RGB", (1024, 512))
    for i, color in enumerate(COLORS):
        grain = rng.normal(0, 3.7, (256, 256, 1))
        raster = np.clip(np.array(color) + grain, 0, 255).astype(np.uint8)
        tile = Image.fromarray(raster)
        paint = ImageDraw.Draw(tile)
        if i in (0, 1):
            for y in range(0, 256, 32):
                paint.line((0, y, 255, y), fill=tuple(c - 15 for c in color))
                for x in range(-32 + (y % 64), 256, 64):
                    paint.line((x, y, x, y + 32), fill=tuple(c - 12 for c in color))
        elif i == 2:
            for _ in range(2700):
                x, y = map(int, rng.integers(0, 256, 2))
                tint = int(rng.integers(-18, 25))
                paint.line((x, y, x + 1, y + 1), fill=tuple(c + tint for c in color))
        sheet.paste(tile, ((i % 4) * 256, (i // 4) * 256))
    sheet.save(OUT / ATLAS)


class Mesh:
    def __init__(self, label, ground):
        self.label = label
        self.ground = ground
        self.faces = []

    def triangle(self, vertices, tile):
        self.faces.append((tuple(vertices), tile))

    def surface(self, shape, height, tile, cell=7):
        if shape.is_empty:
            return
        xmin, zmin, xmax, zmax = shape.bounds
        for x in range(math.floor(xmin), math.ceil(xmax), cell):
            for z in range(math.floor(zmin), math.ceil(zmax), cell):
                piece = shape.intersection(box(x, z, x + cell, z + cell))
                for poly in polygons(piece):
                    for tri in constrained_delaunay_triangles(poly).geoms:
                        if tri.area <= .0001:
                            continue
                        contour = orient(tri, sign=1).exterior.coords[:3]
                        self.triangle([(px, self.ground(px, pz) + height, pz) for px, pz in contour], tile)

    def cuboid(self, x, z, width, depth, height, tile, lift=0):
        y = self.ground(x, z) + lift
        corners = [(x + dx * width / 2, y + up * height, z + dz * depth / 2)
                   for up in (0, 1) for dx, dz in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
        for indices in ((4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)):
            a, b, c, d = (corners[i] for i in indices)
            self.triangle((a, b, c), tile)
            self.triangle((a, c, d), tile)

    def export(self, out=OUT, frame=FRAME, atlas=ATLAS):
        if not 0 < len(self.faces) < 19000:
            raise ValueError(f"Unexpected triangle count for {self.label}: {len(self.faces)}")
        name = "StationPrecinct_" + self.label
        xmin, zmin, xmax, zmax = frame.bounds
        ground_at_origin = self.ground(*ORIGIN)
        vertex_lines, uv_lines, normals, face_lines = [], [], [], []
        for normal_id, (vertices, tile) in enumerate(self.faces, 1):
            first = len(vertex_lines) + 1
            obj = []
            for x, y, z in vertices:
                local = (x - ORIGIN[0], y - ground_at_origin, ORIGIN[1] - z)
                obj.append(local)
                vertex_lines.append("v {:.5f} {:.5f} {:.5f}".format(*local))
                u = ((tile % 4) + .02 + .96 * ((x - xmin) / (xmax - xmin))) / 4
                v = 1 - ((tile // 4) + .02 + .96 * ((z - zmin) / (zmax - zmin))) / 2
                uv_lines.append(f"vt {u:.6f} {v:.6f}")
            a, b, c = map(np.asarray, obj)
            normal = np.cross(b - a, c - a)
            normal /= np.linalg.norm(normal)
            normals.append("vn {:.6f} {:.6f} {:.6f}".format(*normal))
            face_lines.append("f " + " ".join(f"{i}/{i}/{normal_id}" for i in range(first, first + 3)))
        (out / (name + ".obj")).write_text("\n".join(vertex_lines + uv_lines + normals + face_lines) + "\n", encoding="utf-8")
        return (f"bldg_authored_publicrealm_station_{self.label}", str(out), name, str(len(self.faces)),
                str(ORIGIN[0]), str(ORIGIN[1]), atlas)


def main():
    OUT.mkdir(exist_ok=True)
    doc = json.loads(SOURCE.read_bytes())
    points = coordinates(doc)
    tracks, platforms, plazas, walking, stops = classify(doc, points)
    raw, roads, sites, _ = prepare(True, True)
    grid = np.frombuffer(raw, dtype="<u2").reshape(1081, 1081) / 64

    def ground(x, z):
        return terrain_height(grid, x, z, True)

    clearance = road_clearance(raw, roads, sites)
    platform = union_all([shape for _, shape in platforms]).difference(clearance)
    track_shape = union_all([shape.buffer(1.2, cap_style="flat") for _, shape, _ in tracks]).difference(clearance)
    plaza = union_all([shape for _, shape in plazas] + [station_frontage(doc, points)]).difference(clearance.union(track_shape).union(platform))
    paths = union_all(walking).difference(clearance.union(track_shape).union(platform).union(plaza))
    groups = {label: Mesh(label, ground) for label in ("Plazas", "Paths", "Ballast", "Rails", "Platforms", "StreetFurniture")}
    groups["Plazas"].surface(plaza, .34, 0)
    groups["Paths"].surface(paths, .29, 1)
    groups["Ballast"].surface(track_shape, .36, 2)
    groups["Platforms"].surface(platform, .51, 5)
    # Two rails follow each surveyed centerline. Constrain their width to the ballast footprint.
    rail_shapes = []
    for _, track, gauge in tracks:
        for line in ([track] if track.geom_type == "LineString" else getattr(track, "geoms", ())):
            if line.geom_type != "LineString" or line.length < 1:
                continue
            rail_shapes.extend(line.offset_curve(offset).buffer(.048, cap_style="flat") for offset in (-gauge / 2, gauge / 2))
    rails = union_all(rail_shapes).intersection(track_shape).difference(platform)
    groups["Rails"].surface(rails, .415, 3, cell=12)
    # Keep benches and shelters at the edges of actual mapped plazas and bus platforms.
    furniture = groups["StreetFurniture"]
    sheltered = []
    for stop_id, point in stops:
        if not plaza.buffer(-.65).covers(point):
            continue
        x, z = point.coords[0]
        furniture.cuboid(x, z, 1.35, .16, .13, 6, lift=.82)
        furniture.cuboid(x, z, 1.55, .95, .14, 5, lift=1.45)
        for dx in (-.61, .61):
            furniture.cuboid(x + dx, z + .27, .08, .08, 1.15, 6, lift=.31)
        sheltered.append(stop_id)
    furnishings = []
    edges = union_all([part.boundary for part in polygons(plaza)])
    for x in range(-1495, -1350, 7):
        for z in range(891, 1028, 7):
            point = Point(x, z)
            if plaza.buffer(-1.1).covers(point) and edges.distance(point) < 3.1 and clearance.distance(point) > 1.1:
                furniture.cuboid(x, z, 1.38, .58, .22, 6, lift=.34)
                furniture.cuboid(x, z, 1.19, .43, .12, 7, lift=.55)
                furnishings.append([x, z])
    texture()
    rows = [mesh.export() for mesh in groups.values()]
    base = (ROOT / "refined-visuals-citywide.tsv").read_text(encoding="utf-8-sig").rstrip("\n")
    manifest = ROOT / "refined-visuals-citywide-station.tsv"
    manifest.write_text(base + "\n" + "\n".join("\t".join(row) for row in rows) + "\n", encoding="utf-8")
    report = {"sourceOsmSha256": hashlib.sha256(SOURCE.read_bytes()).hexdigest(), "manifest": manifest.name,
              "osmTrackWays": len(tracks), "osmPlatformWays": len(platforms), "osmPlazaWays": [i for i, _ in plazas],
              "assetTriangles": {row[2]: int(row[3]) for row in rows}, "plazaAreaGameM2": round(plaza.area, 1),
              "railbedAreaGameM2": round(track_shape.area, 1), "shelterStopIds": sheltered, "planterPositions": furnishings}
    (OUT / "import-plan.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False))


if __name__ == "__main__":
    main()
