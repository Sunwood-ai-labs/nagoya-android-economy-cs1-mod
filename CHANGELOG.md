# Changelog

This file records user-visible repository changes. Historical experiment numbers belong in the linked evidence pages, not in release notes that imply a packaged benchmark.

## [Unreleased]

### Documentation

- Added a bilingual public README with explicit scope, build inputs, data-root configuration, and the boundary between native CS1 systems and MOD-owned simulation.
- Added the docs landing page, getting-started guide, architecture notes, troubleshooting, data/licensing guidance, contribution policy, and security policy.
- Added issue templates for bug reports, feature requests, and questions.
- Preserved the historical life-simulation and real-terrain records while labeling their different evidence status and external dependencies.
- Added 80 original development images to the READMEs and searchable documentation gallery, with source labels and lazy loading.
- Added GitHub Pages publishing and a dated evidence index.

### Tooling notes

- The offline test workflow is documented with its synthetic fixture as the default and explicit graph/region arguments for production data.
- The live verifier is documented through its `uv run --python 3.11 ... --help` contract; no live-game result is implied by documentation alone.
- Added configurable runtime roots, preserving the historical defaults, with standalone validation checks.
- Added portable fleet fixtures and CI, parameterized live expectations, and verification that remains active with Python optimization enabled.
- Preserved the optional real-terrain bridge and station/terrain scripts separately from the main MOD assembly, with their own build and provenance.

### Limitations

- No release artifact, compatible save, large terrain/building dataset, or one-click Nagoya map package is published by this repository.
- Optional real-terrain bridge source, when present, still requires external data and other map loaders.

## History

Earlier experiment observations are maintained in [the documentation index](docs/index.md) and its linked evidence records. They are intentionally not rewritten into semantic version claims because they depend on named saves, external data revisions, and live-game conditions.
