"""Locate PLATEAU buildings in Nagoya's official wards and land-use zones."""

import argparse
import csv
import itertools
import json
from collections import Counter
from pathlib import Path
from xml.etree import ElementTree
from zipfile import ZipFile

import numpy as np
from pyproj import Transformer
from shapely import STRtree, make_valid, points, transform
from shapely.geometry import Polygon, shape


CS1_BY_DISTRICT = {
    "1": "ResidentialLow", "2": "ResidentialLow", "3": "ResidentialHigh",
    "4": "ResidentialHigh", "5": "ResidentialLow", "6": "ResidentialHigh",
    "7": "ResidentialLow", "8": "ResidentialLow", "9": "CommercialLow",
    "10": "CommercialHigh", "11": "Industrial", "12": "Industrial", "13": "Industrial",
}


def ward_tree(archive, east, north):
    with ZipFile(archive) as compressed:
        member, = [name for name in compressed.namelist() if name.endswith(".geojson")]
        with compressed.open(member) as stream:
            features = json.load(stream)["features"]
    projection = Transformer.from_crs(4326, 6675, always_xy=True)

    def project(lon, lat):
        x, z = projection.transform(lon, lat)
        return np.asarray(x) - east, np.asarray(z) - north

    geometries, names = [], []
    for feature in features:
        props = feature["properties"]
        if props.get("N03_003") == "名古屋市" and props.get("N03_004"):
            geom = shape(feature["geometry"])
            geometries.append(make_valid(transform(geom, project, interleaved=False)))
            names.append(props["N03_004"])
    if len(set(names)) != 16:
        raise ValueError("Expected official geometry for exactly 16 Nagoya wards")
    return STRtree(geometries), names


def district_tree(archive, east, north):
    transform_coords = Transformer.from_crs(6697, 6675, always_xy=True)
    geometries, codes = [], []
    with ZipFile(archive) as compressed:
        code_path = "codelists/Common_districtsAndZonesType.xml"
        with compressed.open(code_path) as stream:
            descriptions = {
                entry.findtext("{*}name"): entry.findtext("{*}description")
                for entry in ElementTree.parse(stream).iterfind(".//{*}Definition")
            }
        members = [name for name in compressed.namelist() if "/urf/" in name and name.endswith("_08_op.gml")]
        for member in members:
            with compressed.open(member) as stream:
                root = ElementTree.parse(stream)
            for district in root.iterfind(".//{*}UseDistrict"):
                if district.findtext("{*}city") != "23100":
                    continue
                code = district.findtext("{*}function")
                if code not in descriptions:
                    raise ValueError(f"Invalid Nagoya UseDistrict code in {member}: {code}")

                def ring(node):
                    value = node.findtext(".//{*}posList")
                    coords = np.fromstring(value or "", sep=" ").reshape(-1, 3)
                    if len(coords) < 4:
                        raise ValueError(f"Invalid Nagoya polygon ring in {member}")
                    x, z = transform_coords.transform(coords[:, 1], coords[:, 0])
                    return np.column_stack((x - east, z - north))

                for polygon in district.iterfind(".//{*}lod1MultiSurface//{*}Polygon"):
                    outside = ring(polygon.find("{*}exterior"))
                    holes = [ring(node) for node in polygon.findall("{*}interior")]
                    geom = make_valid(Polygon(outside, holes))
                    if not geom.is_empty and geom.area > 0:
                        geometries.append(geom)
                        codes.append(code)
    if not geometries:
        raise ValueError("No Nagoya UseDistrict geometry in CityGML archive")
    return STRtree(geometries), codes, descriptions


def matches(tree, labels, geometry, valid):
    results = {}
    for index, match in tree.query(geometry, predicate="covered_by").T:
        results.setdefault(int(valid[index]), set()).add(labels[match])
    return results


def assign(inventory, archive, districts, output, east, north):
    ward_shapes, names = ward_tree(archive, east, north)
    zone_shapes, district_codes, descriptions = district_tree(districts, east, north)
    counts = Counter()
    zones = Counter()
    ambiguous_wards = 0
    temp = output.with_suffix(output.suffix + ".tmp")
    with inventory.open(encoding="utf-8-sig", newline="") as stream, temp.open(
        "w", encoding="utf-8", newline=""
    ) as out:
        reader = csv.DictReader(stream)
        fieldnames = list(dict.fromkeys([
            *(reader.fieldnames or []), "ward", "city_use_district", "city_use_label", "cs1_zone_hint",
        ]))
        writer = csv.DictWriter(out, fieldnames)
        writer.writeheader()
        for batch in iter(lambda: list(itertools.islice(reader, 25000)), []):
            positions = np.array([[float(row[k] or "nan") for k in ("x", "z")] for row in batch])
            valid = np.flatnonzero(np.isfinite(positions).all(axis=1))
            centers = points(positions[valid])
            wards = matches(ward_shapes, names, centers, valid)
            districts = matches(zone_shapes, district_codes, centers, valid)
            for i, row in enumerate(batch):
                ward_names = wards.get(i, ())
                zone_codes = districts.get(i, ())
                zone = next(iter(zone_codes)) if len(zone_codes) == 1 else ""
                if len(ward_names) > 1:
                    ambiguous_wards += 1
                row["ward"] = next(iter(ward_names)) if len(ward_names) == 1 else ""
                row["city_use_district"] = zone
                row["city_use_label"] = descriptions.get(zone, "")
                row["cs1_zone_hint"] = CS1_BY_DISTRICT.get(zone, "")
                counts[row["ward"] or "unassigned"] += 1
                zones[zone or ("ambiguous" if zone_codes else "outside_or_unmapped")] += 1
                writer.writerow(row)
    temp.replace(output)
    return {
        "total": sum(counts.values()), "ambiguousWardMatches": ambiguous_wards,
        "districtPolygons": len(district_codes), "byWard": dict(counts), "byCityUseDistrict": dict(zones),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inventory", type=Path, required=True)
    parser.add_argument("--official-n03", type=Path, required=True)
    parser.add_argument("--citygml-archive", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--anchor-epsg6675", type=float, nargs=2, required=True, metavar=("EAST", "NORTH"))
    args = parser.parse_args()
    report = assign(args.inventory, args.official_n03, args.citygml_archive, args.output, *args.anchor_epsg6675)
    report["officialSource"] = str(args.official_n03)
    report["cityUseDistrictSource"] = str(args.citygml_archive)
    args.output.with_suffix(".json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(json.dumps(report, ensure_ascii=False))


if __name__ == "__main__":
    main()
