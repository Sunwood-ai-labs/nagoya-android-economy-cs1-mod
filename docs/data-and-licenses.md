# Data and licenses

This repository is intentionally source-first. The code and documentation are public, while the large datasets, game files, saves, and map-building pipeline used by the historical Nagoya runs remain external. Do not treat a successful build as permission to redistribute those inputs.

## What is included

| Category | Repository status | Applicable terms |
| --- | --- | --- |
| C# MOD source, PowerShell, Python, tests, and small helper files | Included | [MIT License](https://github.com/Sunwood-ai-labs/nagoya-android-economy-cs1-mod/blob/main/LICENSE) for repository code unless a file says otherwise |
| Markdown guides and experiment notes | Included | Copyright follows the repository contribution and publication terms; external quotations/links retain their source terms |
| Small JSON evidence/manifests | Included where present | Read as run records, not as a replacement for the source dataset or save |
| Development screenshots and Blender checks | 80 approved original images in `docs/images/` | Historical captures, with per-image provenance. GSI aerial/game comparisons credit the August 2020 imagery; standalone aerial photos remain Tailnet-only |
| Large terrain/building datasets, mesh catalogs, textures, saves, and game logs | Not included | Obtain and use them under each provider's current terms |
| Cities: Skylines and managed game assemblies | Not included | Third-party proprietary software; install from the proper distribution and do not commit or redistribute copied binaries |
| CitiesHarmony | Not included | Separate open-source third-party project with its own upstream license; obtain the compatible build from [CitiesHarmony](https://github.com/boformer/CitiesHarmony) and follow its terms |

The historical submission PDF is a record of an earlier project state. It is not a statement that all embedded or referenced third-party material may be extracted and redistributed. An optional real-terrain bridge source, when present, is still only source code; it does not grant rights to external map data or game assets.

## External source families

The following sources are referenced by the experiment notes and/or their external generation workflow. Follow the current terms at the time you obtain data:

- **Project PLATEAU:** [MLIT Project PLATEAU](https://www.mlit.go.jp/plateau/). PLATEAU building data and meshes are not bundled here. Check the dataset-specific license, attribution, and redistribution conditions for the exact release you use.
- **OpenStreetMap:** [OpenStreetMap copyright and licence](https://www.openstreetmap.org/copyright). If you use OSM-derived geometry, consult the current ODbL terms for attribution and derivative-database obligations applicable to your exact dataset and output. The repository does not publish a complete OSM extract.
- **Geospatial Information Authority of Japan:** [GSI](https://www.gsi.go.jp/) and [GSI legal/usage information](https://www.gsi.go.jp/LAW/). The station and real-terrain notes refer to aerial imagery or elevation sources; verify the exact product's attribution and reuse conditions before copying it into a map or build.
- **Nagoya public materials:** The notes may link to Nagoya City or other public documents for historical comparison. A link in a Markdown page is not a grant to copy the document, photographs, or derived assets into a release.
- **Game/platform assets:** Cities: Skylines, Colossal Order, Paradox Interactive, CitiesHarmony, and any workshop or launcher assets have their own terms. This project is not affiliated with or endorsed by those providers.

The [real-terrain QA](real-terrain-road-building-qa.md) and [Nagoya Station comparison](nagoya-station-aerial-comparison.md) pages are especially careful to separate a source link or visual comparison from an asset redistribution claim.

## Attribution when publishing a derivative run

If you publish a map, screenshot set, report, or build produced with external inputs:

1. Record the provider, dataset/product name, release or access date, and the exact terms that applied.
2. Preserve required attribution in the release and in any site or video where the derived data is visible.
3. Keep the source geometry, textures, and game files out of this repository unless you have explicit permission and the repository policy is updated accordingly.
4. State which parts are native CS1 objects, which are MOD-owned visuals/simulation, and which are only reference imagery.
5. Remove credentials, private save paths, bridge URLs, and personal data from logs and evidence before sharing.

## Code license boundary

The [MIT License](https://github.com/Sunwood-ai-labs/nagoya-android-economy-cs1-mod/blob/main/LICENSE) grants permissions for the repository's own code. It does not grant rights to the Cities: Skylines assemblies, external PLATEAU/OSM/GSI data, photographs, meshes, textures, maps, or saves that the code can read. A derivative build must satisfy both the MIT notice requirement and every applicable third-party term.
