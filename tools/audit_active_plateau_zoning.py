"""Count PLATEAU source buildings represented by currently valid CS1 zoning."""

import argparse
import csv
import hashlib
import json
from collections import Counter, defaultdict
from pathlib import Path


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def audit(roster, members, accounting, operational=False):
    live = read_json(roster)
    group_field = "operationalNativeSiteGroups" if operational else "activeNativeSiteGroups"
    site_field = "operationalNativeZonedSites" if operational else "activeNativeZonedSites"
    if group_field not in live or site_field not in live:
        raise ValueError(f"CS1 must report {group_field} and {site_field}")
    active = set(live[group_field])
    if len(active) > live[site_field] or (not active and not operational):
        raise ValueError("CS1 must report validated zoning groups")
    inventory = read_json(accounting)["sourceAccounting"]
    by_source = defaultdict(lambda: [0, 0])
    by_role, extensions = Counter(), {}
    with members.open(encoding="utf-8-sig", newline="") as stream:
        for row in csv.DictReader(stream, delimiter="\t"):
            group = row["roleGroup"]
            if group not in extensions:
                extensions[group] = "extension_" + hashlib.sha256(group.encode("utf-8")).hexdigest()[:16]
            current = row["key"] in active or extensions[group] in active
            record = by_source[row["sourceId"]]
            record[0] += 1
            record[1] += current
            by_role["active" if current else "uncovered"] += 1
    if len(by_source) != inventory["sourcesWithRoles"] or by_role.total() != inventory["sourceRoleAssignments"]:
        raise ValueError("Source membership does not match the catalog audit")
    counts = Counter("allRolesActive" if total == attached else "someRolesActive" if attached else "noActiveRole"
                     for total, attached in by_source.values())
    counts["withoutClassifiedRoles"] = inventory["uniqueSourceIds"] - len(by_source)
    result = {"city": live["city"], "rosterUtc": live["utc"], site_field: live[site_field],
              "nativeSiteGroupCount": len(active), "plateauSourceIds": inventory["uniqueSourceIds"],
              "sourceCoverage": dict(counts), "roleMemberships": dict(by_role), "writesToGame": False}
    if operational:
        result["coverageBasis"] = "completed active native buildings"
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("roster", "members", "accounting"):
        parser.add_argument("--" + key, required=True, type=Path)
    parser.add_argument("--operational", action="store_true", help="Count completed, active native buildings only.")
    print(json.dumps(audit(**vars(parser.parse_args())), ensure_ascii=False))
