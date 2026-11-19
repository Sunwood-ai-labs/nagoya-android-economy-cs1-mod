"""Export original building centers from the active PLATEAU rendering catalogs."""

import argparse
import csv
import json
from pathlib import Path

from plan_citywide_zoning import SOURCE_ID


def export(state, destination):
    models = state["models"]
    total = 0
    sources = set()
    transient = destination.with_suffix(destination.suffix + ".tmp")
    with transient.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(("source", "x", "z", "use", "render_id"))
        for model in models:
            if not model["active"]:
                continue
            render_id = model["id"]
            count = model["sourceBuildingCount"]
            catalog = Path(model["sourceCatalogPath"] or ".")
            total += count
            if not count:
                continue
            if catalog.is_absolute() and catalog.is_file():
                with catalog.open(encoding="utf-8-sig", newline="") as group:
                    rows = [(row["id"], row["x"], row["z"]) for row in csv.DictReader(group, delimiter="\t")]
            elif count == 1 and SOURCE_ID.fullmatch(render_id):
                x, _, z = model["boundsCenter"]
                rows = [(render_id, x, z)]
            else:
                raise ValueError(f"Missing a source catalog for {render_id}")
            if len(rows) != count:
                raise ValueError(f"Source count mismatch in {render_id}: {len(rows)} vs {count}")
            for source, x, z in rows:
                if not SOURCE_ID.fullmatch(source) or source in sources:
                    raise ValueError(f"Unexpected or duplicate source ID: {source}")
                sources.add(source)
                writer.writerow((source, x, z, "unknown", render_id))
    if total != len(sources):
        raise ValueError(f"Source count mismatch: {len(sources)} vs {total}")
    transient.replace(destination)
    return {"renderRevision": state["revision"], "inventoryRows": total, "source": str(destination)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--models", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    state = json.loads(args.models.read_text(encoding="utf-8-sig"))
    print(json.dumps(export(state, args.output), ensure_ascii=False))


if __name__ == "__main__":
    main()
