"""Read the official Nagoya 2022 building-use codes and join by gml:id."""

import argparse
import csv
import json
import mmap
import re
from collections import Counter
from pathlib import Path
from xml.etree import ElementTree
from zipfile import ZipFile


# The versioned Nagoya package uses two serializers, indenting with tabs and
# two-space groups. Match only direct Building properties, omitting BuildingPart.
TOKENS = re.compile(
    rb'^(?:\t{2}| {4})<bldg:Building\s+gml:id="([^"]+)">|^(?:\t{2}| {4})</bldg:Building>'
    rb'|^(?:\t{3}| {6})<bldg:usage\b[^>]*>([^<]+)</bldg:usage>', re.MULTILINE
)
USE_TYPES = {
    "401": "office", "402": "commercial", "403": "hospitality",
    "404": "mixed_commercial", "411": "residential", "412": "apartments",
    "413": "mixed_residential_commercial", "414": "mixed_residential_commercial",
    "415": "mixed_residential_industrial", "421": "civic", "422": "education_health",
    "431": "logistics", "441": "industrial", "451": "agriculture",
    "452": "utility", "453": "defence", "454": "other", "461": "unknown",
}


def extract(path):
    found = {}
    with path.open("rb") as file, mmap.mmap(file.fileno(), 0, access=mmap.ACCESS_READ) as data:
        source = None
        for token in TOKENS.finditer(data):
            building, usage = token.groups()
            if building:
                if source is not None:
                    raise ValueError(f"Unclosed root building in {path}")
                source = building.decode("ascii")
                if source in found:
                    raise ValueError(f"Duplicate root building in {path}: {source}")
                found[source] = set()
            elif usage:
                if source is None:
                    raise ValueError(f"Detached building-use code in {path}")
                found[source].add(usage.decode("ascii").strip())
            else:
                source = None
        if source is not None or not found:
            raise ValueError(f"Incomplete building scan in {path}")
    return {source: tuple(sorted(codes)) for source, codes in found.items()}


def verify_sample(path, fast_records):
    bldg = "{http://www.opengis.net/citygml/building/2.0}"
    gml = "{http://www.opengis.net/gml}"
    reference = {}
    for _, element in ElementTree.iterparse(path, events=("end",)):
        if element.tag != bldg + "Building":
            continue
        reference[element.attrib[gml + "id"]] = tuple(
            sorted({child.text.strip() for child in element.findall(bldg + "usage")})
        )
        element.clear()
    if reference != fast_records:
        raise ValueError(f"Fast use scan differs from parsed CityGML in {path}")


def join(inventory, gml_root, citygml, counts_path, output):
    expected = json.loads(counts_path.read_text(encoding="utf-8-sig"))
    counts_by_path = {Path(item["file"]).name: item["buildings"] for item in expected["perFile"]}
    paths = sorted(gml_root.glob("*.gml"))
    if not expected["complete"] or {path.name for path in paths} != counts_by_path.keys():
        raise ValueError("The raw source files differ from the completed CityGML inventory")
    with ZipFile(citygml) as zip_file, zip_file.open("codelists/Building_usage.xml") as code_file:
        labels = {
            item.findtext("{*}name"): item.findtext("{*}description")
            for item in ElementTree.parse(code_file).iterfind(".//{*}Definition")
        }
    if labels.keys() != USE_TYPES.keys():
        raise ValueError("Building use code list differs from the versioned Nagoya package")

    uses = {}
    check_path = max(paths, key=lambda file: file.stat().st_size)
    for path in paths:
        records = extract(path)
        if counts_by_path[path.name] != len(records) or not records.keys().isdisjoint(uses):
            raise ValueError(f"Root building count or ID uniqueness differs: {path}")
        if path == check_path:
            verify_sample(path, records)
        uses.update(records)

    output_temp = output.with_suffix(output.suffix + ".tmp")
    joined, missing = Counter(), 0
    with inventory.open(encoding="utf-8-sig", newline="") as stream, output_temp.open(
        "w", encoding="utf-8", newline=""
    ) as temporary:
        reader = csv.DictReader(stream)
        writer = csv.DictWriter(temporary, [*(reader.fieldnames or []), "usage_code", "usage_label"])
        writer.writeheader()
        for row in reader:
            codes = uses.get(row["source"])
            if codes is None:
                missing += 1
                codes = ()
            category = "unknown"
            if len(codes) == 1:
                category = USE_TYPES[codes[0]]
            elif codes:
                category = "multiple_source_uses"
            row.update(use=category, usage_code="|".join(codes), usage_label="|".join(labels[c] for c in codes))
            writer.writerow(row)
            joined[category] += 1
    output_temp.replace(output)
    return {
        "sourceFiles": len(paths), "sourceBuildingIds": len(uses), "joinedModelRows": sum(joined.values()),
        "modelsWithoutSourceIdMatch": missing, "modelRowsByUse": dict(sorted(joined.items())),
        "independentXmlVerificationSample": check_path.name,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inventory", type=Path, required=True)
    parser.add_argument("--citygml-root", type=Path, required=True)
    parser.add_argument("--citygml-archive", type=Path, required=True)
    parser.add_argument("--source-counts", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = join(args.inventory, args.citygml_root, args.citygml_archive, args.source_counts, args.output)
    args.output.with_suffix(".json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(json.dumps(report, ensure_ascii=False))


if __name__ == "__main__":
    main()
