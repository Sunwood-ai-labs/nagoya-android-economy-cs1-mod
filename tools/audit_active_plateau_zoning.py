"""Count PLATEAU source buildings represented by currently valid CS1 zoning."""

import argparse
import csv
import hashlib
import json
from collections import Counter, defaultdict
from pathlib import Path


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def audit(roster, members, accounting):
    live = read_json(roster)
    active = set(live["activeNativeSiteGroups"])
    if len(active) > live["activeNativeZonedSites"] or not active:
        raise ValueError("CS1 must report active validated zoning groups")
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
    return {"city": live["city"], "rosterUtc": live["utc"], "activeNativeZonedSites": live["activeNativeZonedSites"],
            "nativeSiteGroupCount": len(active), "plateauSourceIds": inventory["uniqueSourceIds"],
            "sourceCoverage": dict(counts), "roleMemberships": dict(by_role), "writesToGame": False}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("roster", "members", "accounting"):
        parser.add_argument("--" + key, required=True, type=Path)
    print(json.dumps(audit(**vars(parser.parse_args())), ensure_ascii=False))
