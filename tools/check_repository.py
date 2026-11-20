"""Fail on broken Markdown file links, invalid evidence JSON or leaked media."""

import argparse
from html.parser import HTMLParser
import json
from pathlib import Path
import re
import subprocess
import urllib.parse


def check_site(site):
    class Links(HTMLParser):
        def __init__(self):
            super().__init__()
            self.links = []

        def handle_starttag(self, tag, attrs):
            for key, value in attrs:
                if value and key in {"href", "src"}:
                    self.links.append(value)

    failures = []
    pages = list(site.rglob("*.html"))
    if not pages:
        raise SystemExit("Built site contains no HTML")
    for page in pages:
        parser = Links()
        parser.feed(page.read_text(encoding="utf-8"))
        for address in parser.links:
            parts = urllib.parse.urlsplit(address)
            if parts.scheme or parts.netloc or not parts.path or parts.path.startswith("/"):
                continue
            target = (page.parent / urllib.parse.unquote(parts.path)).resolve()
            if target.is_dir():
                target /= "index.html"
            if not target.exists():
                failures.append(f"{page.relative_to(site)}: broken built link {address}")
    if failures:
        raise SystemExit("\n".join(failures))
    print(json.dumps({"builtHtmlPages": len(pages), "siteLinks": "passed"}))


def check(root):
    failures = []
    pages = list(root.glob("*.md")) + list((root / "docs").rglob("*.md"))
    for page in pages:
        text = page.read_text(encoding="utf-8-sig")
        text = re.sub(r"```.*?```", "", text, flags=re.S)
        for address in re.findall(r"\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)", text):
            address = address.strip("<>")
            parts = urllib.parse.urlsplit(address)
            if parts.scheme or parts.netloc or not parts.path:
                continue
            target = (page.parent / urllib.parse.unquote(parts.path)).resolve()
            if not target.exists():
                failures.append(f"{page.relative_to(root)}: missing link {address}")
            if target.suffix.lower() in {".png", ".jpg", ".jpeg", ".mp4", ".mp3"} and not target.is_relative_to(root / "docs" / "images"):
                failures.append(f"{page.relative_to(root)}: media must use Tailnet HTTPS: {address}")
    records = list((root / "docs" / "evidence").rglob("*.json"))
    for path in records:
        try:
            json.loads(path.read_text(encoding="utf-8-sig"))
        except (ValueError, UnicodeError) as error:
            failures.append(f"{path.relative_to(root)}: {error}")
    tracked = subprocess.check_output(["git", "-C", str(root), "ls-files", "-z"]).decode().split("\0")
    for name in tracked:
        if name.startswith("docs/evidence/") and Path(name).suffix.lower() in {".png", ".jpg", ".jpeg", ".mp4", ".mp3"}:
            failures.append("Public evidence media tracked: " + name)
        if name and Path(name).suffix.lower() in {".dll", ".exe", ".crp"}:
            failures.append("Generated/proprietary artifact tracked: " + name)
    manifest = root / "docs/evidence/media-manifest.json"
    if manifest.exists():
        data = json.loads(manifest.read_text(encoding="utf-8"))
        for item in data["images"]:
            url = urllib.parse.urlsplit(item["url"])
            if url.scheme != "https" or not (url.hostname or "").endswith(".ts.net"):
                failures.append("Non-Tailnet evidence URL: " + item["source"])
    catalog = root / "docs/images/catalog.json"
    if catalog.exists():
        import hashlib
        data = json.loads(catalog.read_text(encoding="utf-8"))
        for item in data["images"]:
            path = root / "docs" / item["path"]
            if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != item["sha256"]:
                failures.append("Screenshot catalog hash mismatch: " + item["path"])
    if failures:
        raise SystemExit("\n".join(failures))
    print(json.dumps({"markdownPages": len(pages), "evidenceJson": len(records), "repositoryChecks": "passed"}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--site", type=Path, help="Also check a generated MkDocs site")
    args = parser.parse_args()
    check(args.root)
    if args.site:
        check_site(args.site.resolve())
