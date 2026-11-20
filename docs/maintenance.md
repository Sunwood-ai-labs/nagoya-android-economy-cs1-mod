# Repository maintenance

This page records the scope of the November 21, 2026 (JST) repository polish. It is not a new gameplay experiment.

## Acceptance criteria

- Publish the effective development tree to this repository's existing public `main` without rewriting history.
- Give readers English and Japanese introductions, setup, architecture, data attribution and limitations.
- Build the complete MOD against a locally installed CS1 and compatible Harmony; do not redistribute those assemblies.
- Run offline regression tests from a clean checkout without the private Nagoya dataset, plus an optional real-data regression.
- Reject invalid configured runtime roots, retain legacy defaults and keep command-file guards intact.
- Publish searchable text documentation and run CI without proprietary game dependencies.
- Include the existing real-terrain and station records. Publish the 80 development images authorized by the user on November 21; keep standalone reference aerial photos on Tailnet HTTPS.
- Verify the pushed commit, public visibility, CI and documentation deployment independently.

## Surfaces and evidence

| Surface | Required check |
| --- | --- |
| README in both languages | Matching sections, correct source count, working relative links, documented prerequisites |
| `build.ps1` | Full compilation against local game and Harmony assemblies |
| `test.ps1`, `test-paths.ps1` | Offline fleet invariants and root validation; real graph run when available |
| Python tools | Unit tests and syntax compilation; no gameplay claim from these checks |
| Documentation | Strict MkDocs build, navigation and generated internal-link check |
| Evidence | Valid JSON, source-relative links, separate historical experiments and newer map records |
| Images | Public catalog hashes and built image paths; Tailnet HTTPS certificate, MIME, hash and decoded dimensions; visual check of representative existing images |
| GitHub | Staged file audit, commit read-back on `main`, successful Actions and site response |

## Local checks

```powershell
./test.ps1
./test-paths.ps1
uv run --python 3.11 --with-requirements requirements-dev.txt -m pytest -q
uv run --python 3.11 tools/check_repository.py
uv run --python 3.11 --with-requirements requirements-docs.txt mkdocs build --strict
uv run --python 3.11 tools/check_repository.py --site site
```

The source-level root change is covered by standalone checks and compilation. A fresh in-game run after that change is a separate acceptance step. Historical experiment records remain historical evidence and are not relabeled as verification of the new binary.

## Documentation deployment

`mkdocs.yml` defines the navigation. The Pages workflow builds on pull requests and pushes; only `main` deploys. Game assemblies, saves and external map data are excluded from the Pages artifact. The 80 approved development images in `docs/images/` are included. The original submitted PDF stays in Git history, but is not deployed with the documentation.

## Media maintenance

`tools/build_screenshot_gallery.py` rebuilds the public gallery from `docs/images/catalog.json`. The catalog records the user's publication authorization and preserves the original screenshot bytes and aspect ratio. Images are loaded lazily in the gallery.

`tools/publish_evidence_media.py` copies declared evidence and approved catalog images to an existing static directory. Supply its corresponding Tailnet HTTPS base URL; it does not start a server or change Tailscale configuration. It verifies each published asset once and writes `docs/evidence/media-manifest.json` with hashes and dimensions. All 80 public development images also have a Tailnet copy; eight standalone reference aerial photos remain Tailnet-only.
