"""Fill surveyed station street surfaces and gaps in the displayed building footprint."""
import csv
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw
from shapely import LineString, MultiPoint, Point, Polygon, box, constrained_delaunay_triangles, make_valid, union_all
from shapely.affinity import scale
from shapely.geometry.polygon import orient

from audit_station_orthophoto import BOUNDS, OUT, PIXELS_PER_METER
from build_station_precinct import FRAME, Mesh, coordinates, polygons, road_clearance
from nagoya_citywide import prepare, terrain_height
from nagoya_map import ROOT
from prepare_refined_visuals import SCALE

AREA = box(*BOUNDS)
ATLAS = 'StationDensity_d.png'
SURVEY = OUT / 'aerial-survey.json'
OSM = OUT / 'station-osm.json'
MANIFEST = ROOT / 'refined-visuals-citywide-station.tsv'
WIDTHS = {'primary': 14.5, 'secondary': 11.5, 'tertiary': 7.8, 'tertiary_link': 6.6,
          'unclassified': 5.7, 'residential': 5.5, 'service': 4.2, 'living_street': 5,
          'pedestrian': 5, 'footway': 2.6, 'path': 1.7, 'cycleway': 2.8, 'steps': 2.8}
SECTORS = {'east': box(-1397, 903, -1255, 1000), 'west': box(-1593, 894, -1470, 1008),
           'north': box(-1560, 1008, -1280, 1094), 'south': box(-1545, 798, -1265, 894)}


def rendered_footprints():
    """Project the same OBJ/meshbin coordinates that the live loader reads."""
    shapes = []
    with (ROOT / 'refined-visuals-citywide.tsv').open(encoding='utf-8-sig', newline='') as manifest:
        for row in csv.DictReader(manifest, delimiter='\t'):
            x, z = float(row['x']), float(row['z'])
            if not AREA.buffer(260).covers(Point(x, z)):
                continue
            asset = Path(row['assetDirectory']) / row['assetName']
            binary, catalog = asset.with_suffix('.meshbin'), asset.with_suffix('.sources.tsv')
            if binary.exists() and catalog.exists():
                vertices = np.frombuffer(binary.read_bytes()[12:], dtype='<f4').reshape(-1, 8)
                with catalog.open(encoding='utf-8-sig') as entries:
                    next(entries)
                    for entry in entries:
                        _, first, count, cx, cz = entry.rstrip('\n').split('\t')
                        if not AREA.buffer(25).covers(Point(float(cx), float(cz))):
                            continue
                        first, count = 3 * int(first), 3 * int(count)
                        span = vertices[first:first + count]
                        if len(span):
                            shapes.append(MultiPoint(np.column_stack((x + span[:, 0], z - span[:, 2]))).convex_hull)
            elif not row['id'].startswith('bldg_authored_publicrealm_'):
                vertices = []
                faces = []
                for line in asset.with_suffix('.obj').read_text(encoding='utf-8-sig').splitlines():
                    if line.startswith('v '):
                        dx, _, dz = map(float, line.split()[1:4])
                        vertices.append((x + dx, z - dz))
                    elif line.startswith('f '):
                        indices = [int(corner.split('/')[0]) for corner in line.split()[1:]]
                        triangle = Polygon([vertices[i - 1 if i > 0 else i] for i in indices])
                        if triangle.area > .004 and AREA.intersects(triangle):
                            faces.append(triangle)
                if faces:
                    shapes.append(union_all(faces))
    return union_all(shapes).intersection(AREA)


def osm_geometry():
    doc = json.loads(OSM.read_bytes())
    points = coordinates(doc)
    buildings, driving, walking, tracks, paved, green = [], [], [], [], [], []
    for way in doc['elements']:
        tag = way.get('tags', {})
        if way['type'] != 'way' or any(node not in points for node in way['nodes']):
            continue
        if tag.get('tunnel') not in (None, 'no') or tag.get('indoor') == 'yes' or tag.get('location') == 'underground':
            continue
        path = [points[node] for node in way['nodes']]
        if len(path) < 2:
            continue
        line = LineString(path).intersection(AREA)
        if line.is_empty:
            continue
        if tag.get('railway') == 'rail':
            tracks.append(line)
        if tag.get('bridge') == 'yes' or float_or(tag.get('layer'), 0) != 0:
            continue
        highway = tag.get('highway')
        closed = len(path) >= 4 and path[0] == path[-1]
        if closed:
            area = make_valid(Polygon(path)).intersection(AREA)
            if tag.get('building') not in (None, 'no', 'roof', 'construction') and area.area >= 4:
                buildings.append((way['id'], tag, area))
            if tag.get('amenity') in {'parking', 'bus_station'} and tag.get('parking') not in {'underground', 'multi-storey'} or tag.get('landuse') == 'construction' or highway == 'pedestrian' and tag.get('area') == 'yes':
                paved.append(area)
            if tag.get('leisure') in {'park', 'garden', 'playground', 'pitch'} or tag.get('landuse') in {'grass', 'cemetery'} or tag.get('natural') in {'water', 'wood'}:
                green.append(area)
        if highway in WIDTHS and tag.get('area') != 'yes':
            width = float_or(tag.get('width'), WIDTHS[highway])
            lanes = float_or(tag.get('lanes'), 0)
            if highway in ('primary', 'secondary', 'tertiary') and lanes:
                width = max(width if lanes >= 3 else 0, lanes * 3.25 + 1.8)
            target = walking if highway in {'footway', 'pedestrian', 'path', 'cycleway', 'steps'} else driving
            target.append(line.buffer(width * SCALE / 2, cap_style='flat'))
    return buildings, union_all(driving), union_all(walking), union_all(tracks), union_all(paved), union_all(green)


def float_or(value, default):
    try:
        return float(value)
    except (ValueError, TypeError):
        return default


def extrude(mesh, shape, height, wall, roof, lift=0, fins=False):
    for poly in polygons(shape):
        poly = orient(poly, sign=1)
        p = poly.representative_point()
        floor = mesh.ground(p.x, p.y) - .35 + lift
        top = floor + height
        for tri in constrained_delaunay_triangles(poly).geoms:
            contour = orient(tri, sign=1).exterior.coords[:3]
            mesh.triangle([(x, top, z) for x, z in contour], roof)
        for ring in (poly.exterior, *poly.interiors):
            for (x, z), (xx, zz) in zip(ring.coords[:-1], ring.coords[1:]):
                for low, high in zip(np.linspace(floor, top, 11 if fins else 2)[:-1], np.linspace(floor, top, 11 if fins else 2)[1:]):
                    mid = high - min(.22, (high - low) / 5) if fins else high
                    mesh.triangle([(x, low, z), (x, mid, z), (xx, mid, zz)], wall)
                    mesh.triangle([(x, low, z), (xx, mid, zz), (xx, low, zz)], wall)
                    if fins:
                        mesh.triangle([(x, mid, z), (x, high, z), (xx, high, zz)], roof)
                        mesh.triangle([(x, mid, z), (xx, high, zz), (xx, mid, zz)], roof)


def add_building(mesh, osm_id, tag, footprint, observed):
    height = float_or(tag.get('height'), float_or(tag.get('building:levels'), 3 if footprint.area >= 20 else 2) * 3.3)
    profile = observed.get(str(osm_id))
    if profile is None:
        extrude(mesh, footprint, min(85, max(2, height * SCALE)), 4 + osm_id % 3, 7)
        return
    podium = profile['podiumM'] * SCALE
    extrude(mesh, footprint, podium, 4 + osm_id % 3, 7)
    if profile['heightM'] <= profile['podiumM']:
        return
    upper = footprint.intersection(Polygon(profile['upperFootprint']))
    if upper.area < footprint.area * .22:
        upper = scale(footprint, .76, .76).intersection(footprint)
    extrude(mesh, upper, (profile['heightM'] - profile['podiumM']) * SCALE, 6, 4, lift=podium - .2, fins=True)


def make_atlas():
    colors = [(47, 49, 52), (91, 89, 84), (65, 63, 59), (55, 55, 52),
              (85, 89, 90), (98, 86, 73), (63, 77, 82), (111, 110, 101)]
    rng = np.random.default_rng(1148)
    atlas = Image.new('RGB', (1024, 512))
    for tile, color in enumerate(colors):
        material = np.clip(np.array(color) + rng.normal(0, 3.8, (256, 256, 1)), 0, 255).astype('uint8')
        atlas.paste(Image.fromarray(material), (256 * (tile % 4), 256 * (tile // 4)))
    atlas.save(OUT / ATLAS)


def draw_coverage(drive, walk, apron, rail, infill, protected):
    with Image.open(OUT / 'gsi-orthophoto.png') as photo:
        image = photo.convert('RGBA')
    xmin, _, _, zmax = BOUNDS

    def projected(coords):
        return [((x - xmin) * PIXELS_PER_METER, (zmax - z) * PIXELS_PER_METER) for x, z in coords]

    for shapes, color in ((rail, (210, 143, 66, 130)), (drive, (245, 98, 82, 155)),
                          (walk.union(apron), (105, 202, 245, 140)), (infill, (255, 233, 127, 220)),
                          (protected, (109, 174, 100, 90))):
        layer = Image.new('RGBA', image.size)
        draw = ImageDraw.Draw(layer)
        for poly in polygons(shapes):
            draw.polygon(projected(poly.exterior.coords), fill=color)
            for hole in poly.interiors:
                draw.polygon(projected(hole.coords), fill=(0, 0, 0, 0))
        image = Image.alpha_composite(image, layer)
    image.convert('RGB').save(OUT / 'survey-geometry-on-gsi.jpg', quality=92)


def main():
    source, drive, walk, rail_center, paved, protected = osm_geometry()
    survey = json.loads(SURVEY.read_bytes())
    observed_aprons = union_all([Polygon(item['polygon']) for item in survey['paved']])
    protected = protected.union(union_all([Polygon(item['polygon']) for item in survey['open']]))
    preexisting = rendered_footprints()
    raw, roads, sites, _ = prepare(True, True)
    native_roads = road_clearance(raw, roads, sites, AREA)
    native = json.loads((ROOT / 'Nagoya-Real-Citywide-Neighborhoods-20261120-build.json').read_bytes())
    lot_centers = MultiPoint([(lot['x'], lot['z']) for lot in native['lots'] if AREA.covers(Point(lot['x'], lot['z']))])
    grid = np.frombuffer(raw, dtype='<u2').reshape(1081, 1081) / 64

    def ground(x, z):
        return terrain_height(grid, x, z, True)

    labels = ('GapStreets', 'GapWalks', 'GapAprons', 'ExtendedRailbed', 'RailApproaches', 'SurveyInfill')
    street_mesh, walk_mesh, apron_mesh, rail_mesh, approach_mesh, buildings_mesh = (Mesh(label, ground) for label in labels)
    selected = []
    infill = []
    already = preexisting.union(lot_centers.buffer(3.0))
    original_buildings = union_all([shape for _, _, shape in source])
    potential_railbed = rail_center.buffer(3.3, cap_style='flat').buffer(-.6).intersection(AREA)
    blocked = union_all([already, native_roads, drive, potential_railbed, protected])
    clearance = blocked.buffer(.12)
    for osm_id, tag, shape in sorted(source, key=lambda record: -record[2].area):
        if shape.intersection(blocked).area > .34 * shape.area or blocked.covers(shape.representative_point()):
            continue
        recent = union_all([existing for existing in infill if existing.distance(shape) < .15])
        if recent.intersection(shape).area > .34 * shape.area or recent.covers(shape.representative_point()):
            continue
        footprint = shape.difference(clearance).difference(recent.buffer(.12))
        if footprint.area < 4:
            continue
        add_building(buildings_mesh, osm_id, tag, footprint, survey['buildings'])
        infill.append(footprint)
        selected.append(osm_id)
    infill = union_all(infill)
    exclude = preexisting.union(infill).union(native_roads).union(protected)
    previous_rails = rail_center.buffer(1.3, cap_style='flat').intersection(FRAME)
    rail = potential_railbed.difference(exclude.union(drive).union(previous_rails))
    approach = rail_center.difference(FRAME).buffer(.2).difference(exclude.union(drive))
    driving = drive.difference(exclude.union(potential_railbed)).intersection(AREA)
    pedestrian = walk.union(drive.buffer(.8).difference(drive)).difference(exclude.union(driving).union(potential_railbed))
    shoulders = original_buildings.buffer(.9).difference(original_buildings)
    apron = paved.union(observed_aprons).union(shoulders).difference(exclude.union(driving).union(pedestrian).union(potential_railbed)).intersection(AREA)
    street_mesh.surface(driving.simplify(.065), .41, 0, cell=15)
    walk_mesh.surface(pedestrian.simplify(.065), .39, 1, cell=15)
    apron_mesh.surface(apron.simplify(.065), .38, 2, cell=15)
    rail_mesh.surface(rail.simplify(.065), .37, 3, cell=15)
    approach_mesh.surface(approach.simplify(.065), .43, 1, cell=15)
    make_atlas()
    meshes = (street_mesh, walk_mesh, apron_mesh, rail_mesh, approach_mesh, buildings_mesh)
    rows = [mesh.export(out=OUT, frame=AREA, atlas=ATLAS) for mesh in meshes if mesh.faces]
    base = MANIFEST.read_text(encoding='utf-8-sig').splitlines()
    base = [line for line in base if not any(line.startswith(f'bldg_authored_publicrealm_station_{label}\t') for label in labels)]
    temporary = OUT / MANIFEST.name
    temporary.write_text('\n'.join(base + ['\t'.join(row) for row in rows]) + '\n', encoding='utf-8')
    draw_coverage(driving, pedestrian, apron, rail, infill, protected)
    report = {'boundsWorldXZ': BOUNDS, 'osmBuildingWaysAdded': selected, 'manifestRowsBefore': len(base) - 1,
              'manifestRowsAfter': len(base) - 1 + len(rows), 'trianglesAdded': sum(int(row[3]) for row in rows),
              'existingRenderedFootprintGameM2': round(preexisting.area, 1), 'mappedOsmFootprintGameM2': round(original_buildings.area, 1),
              'areasGameM2': {sector: {key: round(geom.intersection(bounds).area, 1) for key, geom in
                                        [('street', driving), ('walk', pedestrian), ('apron', apron),
                                         ('rail', rail), ('building', infill), ('preserved', protected)]}
                              for sector, bounds in {'total': AREA, **SECTORS}.items()}}
    (OUT / 'density-build.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(report))


if __name__ == '__main__':
    main()
