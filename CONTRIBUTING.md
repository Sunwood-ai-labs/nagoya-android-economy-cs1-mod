# Contributing

Contributions are welcome when they make the experiment easier to reproduce, audit, or extend without hiding the boundary between native CS1 behavior and MOD-owned behavior.

## Before opening a change

- Read [Getting started](docs/getting-started.md) and [Architecture](docs/architecture.md).
- Check the current branch and `git status`; preserve unrelated user work, especially existing evidence and real-terrain notes.
- Keep game saves, copied proprietary assemblies, large terrain/building datasets, meshes, textures, credentials, and private bridge URLs out of commits.
- Prefer a small fixture or a sanitized manifest when a test needs data. Document the source, version, and license of external data instead of checking it in.

## Code changes

The MOD targets the older Cities: Skylines 1 API and .NET Framework 3.5-era compiler/runtime. Keep new code compatible with the existing compiler and managed references. Avoid changing a path, save format, or native hook without documenting the migration and the read-back evidence needed to validate it.

When adding a feature, state which boundary it belongs to:

- native CS1 road/zoning/citizen/economy integration;
- MOD-owned routing, actors, parcels, or visuals; or
- tooling and evidence collection.

Do not describe a MOD-owned visual or independent simulation as native merely because it appears on the same map.

## Documentation and evidence

- Keep English and Japanese top-level README sections materially parallel.
- Use relative links within `docs/`; use stable repository links for files outside the docs tree when required by the documentation site.
- Describe historical experiment results as named-run evidence. Include the exact save/data context when a number matters.
- Keep real-terrain visual/map records separate from the native economy trials.
- Do not embed private media or expose local/Tailnet paths as public URLs. Link only to approved, verified evidence locations.
- Update [Data and licenses](docs/data-and-licenses.md) when a new external dataset, image, mesh, or provider is introduced.

## Checks to run

For source changes, run the offline check with the repository fixture:

```powershell
./test.ps1
```

For a production dataset, pass the graph and region files explicitly and record the dataset revision. If Python tooling changes, inspect the CLI contract with:

```powershell
uv run --python 3.11 tools/verify_live.py --help
```

Build the MOD when managed references are available:

```powershell
./build.ps1 -ManagedDirectory "$game\Cities_Data\Managed" -HarmonyAssembly $harmony
```

Do not claim a live game result from a compiler pass or an offline fixture. If you cannot run a game check, state that limitation in the pull request.

## Pull request checklist

- [ ] The change is scoped and does not include unrelated generated files.
- [ ] Native and MOD-owned behavior are described separately.
- [ ] New paths, environment variables, save formats, and external inputs are documented.
- [ ] `./test.ps1` passes, or the failure and exact limitation are explained.
- [ ] User-visible docs and language links are updated where appropriate.
- [ ] No proprietary binaries, large datasets, saves, credentials, or unapproved media are included.
- [ ] Evidence links and attribution are present for any external source.
