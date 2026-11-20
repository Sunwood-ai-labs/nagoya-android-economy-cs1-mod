"""Publish existing evidence images to a supplied Tailnet HTTPS static directory.

No server, background process or Tailscale configuration is created. Every file
is fetched once from the supplied URL and checked before the manifest is saved.
"""

import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
from io import BytesIO
import json
import os
from pathlib import Path
import shutil
import urllib.parse
import urllib.request

from PIL import Image


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--serve-directory", type=Path, required=True)
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--rewrite-links", action="store_true")
    args = parser.parse_args()
    url = urllib.parse.urlsplit(args.base_url)
    if url.scheme != "https" or not (url.hostname or "").endswith(".ts.net") or url.query or url.fragment or url.username:
        parser.error("Use the exact HTTPS *.ts.net static-directory URL, without credentials, query or fragment")
    repo = Path(__file__).resolve().parents[1]
    evidence = repo / "docs" / "evidence"
    images = sorted(p for p in evidence.rglob("*") if p.suffix.lower() in {".png", ".jpg", ".jpeg"})
    public_by_hash = {}
    catalog_path = repo / "docs/images/catalog.json"
    if catalog_path.exists():
        catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
        public_by_hash = {row["sha256"]: row["path"] for row in catalog["images"]}
        known = {hashlib.sha256(path.read_bytes()).hexdigest() for path in images}
        images += [repo / "docs" / row["path"] for row in catalog["images"] if row["sha256"] not in known]
    if not images:
        parser.error("No local evidence images found; restore original local evidence first")
    args.serve_directory.mkdir(parents=True, exist_ok=True)

    def publish(path):
        data = path.read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        name = digest[:24] + path.suffix.lower()
        destination = args.serve_directory / name
        if destination.exists() and destination.read_bytes() != data:
            raise ValueError("Content-address collision")
        if not destination.exists():
            shutil.copy2(path, destination)
        address = args.base_url.rstrip("/") + "/" + name
        with urllib.request.urlopen(address, timeout=8) as response:
            content = response.read(32 * 1024 * 1024 + 1)
            mime = response.headers.get_content_type()
            if response.status != 200 or not mime.startswith("image/"):
                raise ValueError("Media response/MIME mismatch: " + address)
        if hashlib.sha256(content).hexdigest() != digest:
            raise ValueError("Published media hash mismatch: " + address)
        with Image.open(BytesIO(content)) as decoded:
            dimensions = list(decoded.size)
            decoded.load()
        return {"source": path.relative_to(repo / "docs").as_posix(), "url": address,
                "sha256": digest, "mime": mime, "dimensions": dimensions,
                "publicPath": public_by_hash.get(digest)}

    with ThreadPoolExecutor(max_workers=8) as executor:
        records = list(executor.map(publish, images))
    manifest = {"schemaVersion": 1, "visibility": "tailnet-only", "images": records}
    (evidence / "media-manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    if catalog_path.exists():
        addresses = {row["sha256"]: row["url"] for row in records}
        for row in catalog["images"]:
            row["tailnetUrl"] = addresses[row["sha256"]]
        catalog_path.write_text(json.dumps(catalog, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    if args.rewrite_links:
        for page in (repo / "docs").rglob("*.md"):
            text = page.read_text(encoding="utf-8")
            for record in records:
                source = repo / "docs" / record["source"]
                relative = Path(os.path.relpath(source, page.parent)).as_posix()
                text = text.replace("](" + relative + ")", "](" + record["url"] + ")")
            page.write_text(text, encoding="utf-8")
    print(json.dumps({"verifiedImages": len(records), "visibility": "tailnet-only"}))


if __name__ == "__main__":
    main()
