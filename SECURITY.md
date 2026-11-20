# Security policy

This project is an experimental Cities: Skylines 1 MOD and tooling repository. There is no released installer or hosted service, and no security-support SLA. The current default branch is the reference for security fixes.

## Reporting a vulnerability

Please do not open a public issue for a suspected vulnerability. If private vulnerability reporting is enabled for the repository, use the [GitHub Security Advisories report form](https://github.com/Sunwood-ai-labs/nagoya-android-economy-cs1-mod/security/advisories/new). If that form is unavailable, contact the repository maintainers through the [Sunwood-ai-labs GitHub organization](https://github.com/Sunwood-ai-labs) and ask for a private channel before sharing details.

Include only the minimum information needed to reproduce the problem:

- affected commit or file and the environment (game/API/tool version);
- a short impact description and bounded reproduction steps;
- sanitized input or a small fixture, if one is required;
- any mitigation you have already tested.

Never attach credentials, private bridge URLs, personal paths, unredacted game saves, player identifiers, or external datasets whose terms prohibit sharing. Redact logs before sending them.

## In scope

Examples include unsafe path handling in the MOD or tools, unexpected file overwrite outside the configured data root, parser vulnerabilities, credential leakage, or a way for an untrusted input to execute code or corrupt a save-backed record.

## Usually out of scope

Game crashes caused solely by an incompatible third-party build, missing external datasets, ordinary experiment failures, and licensing questions for PLATEAU/OSM/GSI or game assets should be reported as normal issues or handled with the relevant provider. If the issue crosses both boundaries, report the security-sensitive part privately first.

## Disclosure

Please allow maintainers reasonable time to investigate and prepare a fix before public disclosure. Do not assume that a successful offline test or a static evidence file proves that a live-game security issue is absent.
