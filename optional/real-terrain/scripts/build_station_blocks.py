"""Create a separately loadable scene patch for each station comparison block."""
import json
import math

import numpy as np
from PIL import Image
from shapely import LineString, MultiPoint, Point, Polygon, box, constrained_delaunay_triangles, union_all
from shapely.affinity import rotate, translate
from shapely.geometry.polygon import orient
from shapely.ops import nearest_points

from build_station_density import AREA, OSM, OUT, osm_geometry, rendered_footprints
from build_station_precinct import Mesh, ORIGIN, coordinates, polygons, road_clearance
from nagoya_citywide import prepare, terrain_height
from nagoya_map import ROOT

BLOCKS = ROOT / 'station-precinct' / 'blocks'
ATLAS = 'StationBlock_d.png'
HEADER = 'id\tassetDirectory\tassetName\texpectedTriangles\tx\tz\tdiffuseMap'


def atlas():
    colors = [(51, 55, 57), (98, 101, 101), (158, 161, 150), (44, 45, 42),
              (66, 78, 77), (85, 89, 84), (49, 75, 71), (69, 81, 95)]
    sheet = Image.new('RGB', (512, 256))
    rng = np.random.default_rng(737)
    for tile, color in enumerate(colors):
        pixels = np.clip(np.array(color) + rng.normal(0, 3.1, (128, 128, 1)), 0, 255).astype('uint8')
        sheet.paste(Image.fromarray(pixels), ((tile % 4) * 128, (tile // 4) * 128))
    sheet.save(BLOCKS / ATLAS)


def strip(middle, angle, width, depth):
    x, z = middle
    return rotate(box(x - width / 2, z - depth / 2, x + width / 2, z + depth / 2), angle, origin=middle)


def crossing_marks(ids, points, segments, drive, extent):
    marks = []
    for osm_id in ids:
        x, z = points[osm_id]
        point = Point(x, z)
        nearby = sorted((line for line in segments if line.distance(point) < .65), key=lambda line: line.distance(point))
        if not nearby:
            continue
        line = nearby[0]
        along = line.project(point)
        a, b = line.interpolate(max(0, along - 1)), line.interpolate(along + 1)
        if a.equals(b):
            continue
        angle = math.degrees(math.atan2(b.y - a.y, b.x - a.x))
        for offset in np.arange(-3.1, 3.12, .43):
            stripe = rotate(translate(strip((x, z), 0, .84, .22), yoff=float(offset)), angle, origin=(x, z))
            marks.append(stripe)
    return union_all(marks).intersection(drive).intersection(extent)


def bays(mask, limit):
    stripes = []
    for lot in polygons(mask):
        if lot.area < 5:
            continue
        rect = lot.minimum_rotated_rectangle
        ring = list(rect.exterior.coords)
        a, b = max(zip(ring[:-1], ring[1:]), key=lambda edge: math.dist(*edge))
        x, z = lot.representative_point().coords[0]
        angle = math.degrees(math.atan2(b[1] - a[1], b[0] - a[0]))
        if math.dist(a, b) < 2:
            continue
        stripes.append(strip((x, z), angle, min(7, math.dist(a, b) * .7), .10))
        for offset in np.arange(-min(3.2, math.dist(a, b) * .35), min(3.2, math.dist(a, b) * .35), .94):
            shape = translate(strip((x, z), 0, .10, 1.7), xoff=float(offset), yoff=.72)
            stripes.append(rotate(shape, angle, origin=(x, z)))
    return union_all(stripes).intersection(mask.buffer(-.17)).intersection(limit)


def flat(mesh, shape, height, tile):
    for polygon in polygons(shape):
        for tri in constrained_delaunay_triangles(polygon).geoms:
            if tri.area > .0001:
                mesh.triangle([(x, height, z) for x, z in orient(tri, sign=1).exterior.coords[:3]], tile)


def roof_patch(mesh, osm_id, footprints, roof_vertices):
    footprint = footprints[osm_id]
    pixels = [(x, y, z) for x, y, z in roof_vertices if footprint.buffer(.015).covers(Point(x, z))]
    if not pixels:
        raise ValueError(f'No roof surface for OSM building {osm_id}')
    top = max(y for _, y, _ in pixels)
    outline = MultiPoint([(x, z) for x, y, z in pixels if y >= top - .23]).convex_hull.intersection(footprint).buffer(-.55)
    if outline.area < 10:
        raise ValueError(f'Cannot locate a usable roof surface for {osm_id}')
    x, z = outline.representative_point().coords[0]
    ring = list(outline.minimum_rotated_rectangle.exterior.coords)
    a, b = max(zip(ring[:-1], ring[1:]), key=lambda edge: math.dist(*edge))
    angle = math.degrees(math.atan2(b[1] - a[1], b[0] - a[0]))
    flat(mesh, outline, top + .065, 5)
    if osm_id == 51080883:
        pad = strip((x, z), angle, 6.4, 5.5).intersection(outline)
        flat(mesh, pad, top + .09, 6)
        flat(mesh, pad.boundary.buffer(.074).intersection(outline), top + .12, 2)
        shapes = [box(x - 1.05, z - .7, x - .88, z + .7), box(x + .88, z - .7, x + 1.05, z + .7),
                  box(x - 1.05, z - .09, x + 1.05, z + .09)]
        flat(mesh, rotate(union_all(shapes), angle, origin=(x, z)).intersection(pad), top + .14, 2)
    else:
        for offset in (-4.2, -2.5, -.8, .9, 2.6):
            equipment = translate(strip((x, z), 0, .94, 6), xoff=offset)
            flat(mesh, rotate(equipment, angle, origin=(x, z)).intersection(outline), top + .24, 7)
    return round(outline.area, 2)


def main():
    BLOCKS.mkdir(exist_ok=True)
    atlas()
    views = json.loads((OUT / 'block-views.json').read_bytes())
    patches = json.loads((OUT / 'block-patches.json').read_bytes())
    doc = json.loads(OSM.read_bytes())
    points = coordinates(doc)
    buildings, drive, _, tracks, _, protected = osm_geometry()
    footprint = {key: shape for key, _, shape in buildings}
    parking, bus, segments = [], [], []
    for item in doc['elements']:
        tags = item.get('tags', {})
        if item['type'] == 'node' and tags.get('highway') == 'bus_stop':
            bus.append(Point(points[item['id']]))
        if item['type'] != 'way' or any(node not in points for node in item['nodes']):
            continue
        coords = [points[node] for node in item['nodes']]
        if len(coords) < 2 or not AREA.intersects(LineString(coords)):
            continue
        if tags.get('highway') in ('primary','secondary','tertiary','unclassified','residential','service') and tags.get('tunnel') in (None,'no') and tags.get('bridge') in (None,'no'):
            segments.append(LineString(coords))
        if len(coords) >= 4 and coords[0] == coords[-1] and tags.get('amenity') == 'parking' and tags.get('parking') not in ('underground','multi-storey'):
            outline = Polygon(coords)
            if outline.is_valid:
                parking.append(outline)

    raw, roads, sites, _ = prepare(True, True)
    native = road_clearance(raw, roads, sites, AREA)
    occupied = rendered_footprints().union(union_all([footprint[k] for k in (51080883, 445879758, 51061019)]))
    grid = np.frombuffer(raw, dtype='<u2').reshape(1081, 1081) / 64

    def ground(x, z):
        return terrain_height(grid, x, z, True)

    roof_vertices = [(ORIGIN[0] + x, ground(*ORIGIN) + y, ORIGIN[1] - z)
                     for x, y, z in (map(float, line.split()[1:4]) for line in
                         (OUT / 'StationPrecinct_SurveyInfill.obj').read_text().splitlines() if line.startswith('v '))]
    for view in views:
        block = view['id']
        patch = patches[block]
        extent = box(*view['bounds'])
        mesh = Mesh(f'Block{block}', ground)
        changes = {}
        if 'crossings' in patch:
            marks = crossing_marks(patch['crossings'], points, segments, drive.difference(native).difference(occupied), extent)
            mesh.surface(marks, .48, 2, cell=12)
            changes['crossingMarkingAreaGameM2'] = round(marks.area, 2)
        if 'walks' in patch:
            walks = union_all([LineString(points).buffer(.40) for points in patch['walks']])
            walks = walks.intersection(extent).difference(occupied.union(drive).union(native).union(protected))
            mesh.surface(walks, .50, 1, cell=12)
            changes['walkBandAreaGameM2'] = round(walks.area, 2)
        if patch.get('parking'):
            lots = union_all([p for p in parking if extent.covers(p.representative_point())]).intersection(extent)
            lots = lots.difference(occupied.union(native).union(drive)).buffer(-.10)
            mesh.surface(lots, .45, 0, cell=12)
            mesh.surface(bays(lots, extent), .50, 2, cell=12)
            changes['parkingAreaGameM2'] = round(lots.area, 2)
        if patch.get('bus'):
            waiting = extent.difference(occupied.union(native).union(drive).union(tracks.buffer(2)))
            islands = waiting.buffer(-.78)
            island_rims = []
            count = 0
            for stop in (stop for stop in bus if extent.covers(stop)):
                landing = nearest_points(stop, islands)[1]
                if landing.distance(stop) > 6.0:
                    continue
                x, z = landing.coords[0]
                mesh.cuboid(x, z, 1.55, .85, .12, 7, lift=1.68)
                for shift in (-.55, .55):
                    mesh.cuboid(x + shift, z, .09, .09, 1.21, 4, lift=.47)
                island = strip((x, z), 17, 1.7, 2.7).intersection(waiting)
                mesh.surface(island, .49, 4, cell=9)
                island_rims.append(island.boundary.buffer(.105).intersection(island))
                count += 1
            rims = union_all(island_rims)
            mesh.surface(rims, .53, 2, cell=9)
            changes['busStopShelters'] = count
            changes['waitingIslandMarkingAreaGameM2'] = round(rims.area, 2)
        if 'rail' in patch:
            seam = LineString(patch['rail']['seam'])
            safe = extent.difference(native.union(drive).union(occupied)).intersection(tracks.buffer(9))
            beds = seam.buffer(patch['rail']['halfWidth'], cap_style='flat').intersection(safe).difference(tracks.buffer(.72))
            mesh.surface(beds, .365, 3, cell=13)
            mesh.surface(seam.buffer(.16, cap_style='flat').intersection(beds), .39, 5, cell=13)
            changes['railMarginAreaGameM2'] = round(beds.area, 2)
        if 'roof' in patch:
            changes['roofDetailAreaGameM2'] = roof_patch(mesh, patch['roof'], footprint, roof_vertices)
        row = mesh.export(out=BLOCKS, frame=extent, atlas=ATLAS)
        (BLOCKS / f'block-{block}.tsv').write_text(HEADER + '\n' + '\t'.join(row) + '\n', encoding='utf-8')
        plan = {**view, **{k: patch[k] for k in ('compare','work')}, 'changes': changes, 'triangles': int(row[3]), 'renderObjects': 1}
        (BLOCKS / f'block-{block}-plan.json').write_text(json.dumps(plan, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        print(block, row[3], changes)


if __name__ == '__main__':
    main()
