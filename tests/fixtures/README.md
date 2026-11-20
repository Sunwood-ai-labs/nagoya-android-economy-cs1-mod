# Portable routing fixture

`routing.ncg.b64` is a small, MOD-owned NCG1 graph (five nodes, six pedestrian
edges) stored as text so the fixture remains portable in source checkouts.
`test.ps1` materializes it under `.build` before compiling and running
`FleetCheck.cs`. `regions.json` exercises two hubs and three targets per hub.

The fixture contains no game assets, runtime DLLs, or copied CS1 data.
