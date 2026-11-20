"""Build the screenshot gallery from its checked-in catalog.

The optional import switches copy user-approved development captures only.
Original standalone aerial photos remain Tailnet-only. Images are copied byte
for byte: no generation, retouching, resizing or stretching is performed.
"""

import argparse
import hashlib
import html
import json
from pathlib import Path
import shutil


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--import-local-evidence", action="store_true")
    parser.add_argument("--import-proposal", type=Path)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    docs = repo / "docs"
    images = docs / "images"
    images.mkdir(exist_ok=True)
    catalog_path = images / "catalog.json"
    catalog = json.loads(catalog_path.read_text(encoding="utf-8")) if catalog_path.exists() else {"schemaVersion": 1, "publicationApproval": "User authorized public development screenshots on 2026-11-21 JST", "images": []}
    records = {row["sha256"]: row for row in catalog["images"]}

    def add(path, caption, kind, group, source, tailnet_url=None):
        content = path.read_bytes()
        digest = hashlib.sha256(content).hexdigest()
        if digest in records:
            return
        name = digest[:24] + path.suffix.lower()
        target = images / name
        if target.exists() and target.read_bytes() != content:
            raise ValueError("Screenshot hash collision")
        shutil.copy2(path, target)
        records[digest] = {"path": "images/" + name, "sha256": digest, "caption": caption,
                           "kind": kind, "group": group, "source": source, "tailnetUrl": tailnet_url}

    evidence_manifest = json.loads((docs / "evidence/media-manifest.json").read_text(encoding="utf-8"))
    tailnet_by_hash = {row["sha256"]: row["url"] for row in evidence_manifest["images"]}
    dimensions_by_hash = {row["sha256"]: row["dimensions"] for row in evidence_manifest["images"]}
    if args.import_proposal:
        proposal = json.loads((args.import_proposal / "proposal.json").read_text(encoding="utf-8"))
        provenance = {row["sha256"]: row for row in json.loads((args.import_proposal / "asset-provenance.json").read_text(encoding="utf-8"))}
        for post in proposal["payload"]["posts"]:
            for number, media in enumerate(post["media"], 1):
                origin = provenance[media["sha256"]]
                group = {"main": "overview", "models": "models", "life": "life", "wards": "wards",
                         "terrain": "terrain", "roads": "roads", "utilities": "utilities",
                         "station": "station", "aerial": "comparisons", "density": "station"}.get(post["key"], "station")
                caption = media["altText"] + (" " + str(number) if group == "models" else "")
                add(Path(origin["source"]), caption, media["provenance"], group,
                    "proposal/" + post["key"] + "/" + str(number), tailnet_by_hash.get(media["sha256"]))
    if args.import_local_evidence:
        for row in evidence_manifest["images"]:
            path = docs / row["source"]
            if path.name == "gsi-august-2020.jpg":
                continue
            group = "blocks" if "/block-by-block/" in row["source"] else "station"
            if "ramp" in path.stem or "citywide" in path.stem or "neighborhood" in path.stem:
                group = "roads"
            if "utilities" in path.stem:
                group = "utilities"
            if "gsi-cs1" in path.stem or path.name == "comparison.jpg":
                kind = "航空写真・実機比較（国土地理院、2020年8月撮影）"
            else:
                kind = "実機スクショ・実地形版（2026-11-20の記録）"
            caption = path.stem.replace("nagoya-", "").replace("-", " ")
            if group == "blocks":
                caption = "街区 " + path.parent.name + " / " + caption
            add(path, caption, kind, group, row["source"], row["url"])
    catalog["images"] = list(records.values())
    for row in catalog["images"]:
        if hashlib.sha256((docs / row["path"]).read_bytes()).hexdigest() != row["sha256"]:
            raise ValueError("Screenshot bytes changed: " + row["path"])
        row["dimensions"] = dimensions_by_hash[row["sha256"]]
        if "航空写真" in row["caption"]:
            row["kind"] = "航空写真・実機比較（国土地理院、2020年8月撮影）"
    catalog_path.write_text(json.dumps(catalog, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    groups = [("overview", "企画を追う / Project overview"), ("life", "人・配送・充電 / Early life experiments"),
              ("models", "Blenderで加工した10棟 / Model checks"), ("wards", "名古屋16区 / The 16 wards"),
              ("terrain", "実地形 / Real terrain"), ("roads", "道路の修正 / Road development"),
              ("utilities", "開発用インフラ / Utility mode"), ("station", "名古屋駅前 / Station precinct"),
              ("comparisons", "航空写真との比較 / Aerial comparisons"), ("blocks", "8街区の整備前後 / Block records")]
    lines = ["# 開発スクショギャラリー / Development gallery", "",
             f"{len(records)}枚の重複しない原画像を収録。撮影時点と実験の段階を添えて、実機とBlenderの画像を区別しています。", "",
             "These are historical development captures, not verification of the newly compiled DLL. Native building functionality and visible meshes are separate. Images keep their original aspect ratio and bytes; expand an image to inspect the full resolution.", "",
             "航空写真を含む比較画像の出典：国土地理院（2020年8月撮影）。単独の航空写真はTailnet側に保管しています。", "",
             "[Image catalog and SHA-256 hashes](images/catalog.json) · [Tailnet image manifest](evidence/media-manifest.json)", ""]
    for group, title in groups:
        selected = [row for row in catalog["images"] if row["group"] == group]
        if group == "overview":
            selected.sort(key=lambda row: {"proposal/main/3": 0, "proposal/main/4": 1, "proposal/main/1": 2, "proposal/main/2": 3, "proposal/main/5": 4}.get(row["source"], 5))
        if group == "blocks":
            selected.sort(key=lambda row: (row["source"].split("/")[-2], {"comparison.jpg": 0, "before.png": 1, "after.png": 2}.get(row["source"].split("/")[-1], 3)))
        if not selected:
            continue
        lines += [f'<a id="{group}"></a>', "", "## " + title, ""]
        for row in selected:
            path = html.escape(row["path"], quote=True)
            caption = html.escape(row["caption"])
            kind = html.escape(row["kind"])
            width, height = row["dimensions"]
            lines += [f'<figure><a href="{path}"><img src="{path}" alt="{caption}" width="{width}" height="{height}" loading="lazy" style="max-width:100%;height:auto"></a><figcaption>{caption}<br><small>{kind}</small></figcaption></figure>', ""]
    (docs / "gallery.md").write_text("\n".join(lines), encoding="utf-8")
    print(json.dumps({"galleryImages": len(records), "copiedBytes": sum((docs / row["path"]).stat().st_size for row in records.values())}))


if __name__ == "__main__":
    main()
