"""Check two actual CS1 readings for the installed Nagoya Android fleet."""

import argparse
import json
import math
import time
import urllib.request
from datetime import datetime, timezone
from pathlib import Path


def read_state(root):
    state = json.loads((root / "citywide-live-v1" / "state.json").read_text(encoding="utf-8"))
    observed = datetime.fromisoformat(state["utc"].replace("Z", "+00:00"))
    assert abs((datetime.now(timezone.utc) - observed).total_seconds()) < 12, "Stale game state"
    assert state["phase"] == "running" and not state["error"]
    assert state["networkName"] == "nagoya-citywide-building-service-v1"
    assert state["roadChunksLoaded"] == state["roadChunksExpected"] == 447
    simulation = state["simulation"]
    assert simulation["androidsPerRegion"] == 3 and simulation["regionCount"] == 56
    assert simulation["parcelTotals"]["initial"] == 168 and simulation["parcelTotals"]["conserved"]
    fleet = {}
    for region in simulation["regions"]:
        androids = [actor for actor in region["agents"] if actor["kind"] == "android"]
        assert len(androids) == 3 and region["error"] is None
        assert region["hubStock"] >= 0
        assert sum(target["inventory"] for target in region["targets"]) + region["hubStock"] + sum(actor["carried"] for actor in androids) == 3
        assert all(0 <= target["inventory"] <= 1 for target in region["targets"])
        charging = [actor["id"] for actor in androids if actor["state"] == "charging"]
        assert charging == ([region["chargingActor"]] if region["chargingActor"] else [])
        reserved = [actor["targetIndex"] for actor in androids if actor["currentJob"]]
        assert len(set(reserved)) == len(reserved)
        for actor in androids:
            assert actor["id"] not in fleet and actor["error"] is None
            fleet[actor["id"]] = actor
    assert len(fleet) == 168
    visuals = {row["id"]: (row["x"], row["z"]) for row in state["visuals"] if row["kind"] == "android" and row["active"]}
    assert visuals.keys() == fleet.keys()
    return state, fleet, visuals


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--bridge-url", help="Optional CS1 bridge URL for native bus verification")
    args = parser.parse_args()

    before, before_fleet, before_visuals = read_state(args.root)
    time.sleep(8)
    after, fleet, visuals = read_state(args.root)
    assert before["runId"] == after["runId"] and fleet.keys() == before_fleet.keys()
    assert after["simulation"]["elapsed"] > before["simulation"]["elapsed"]
    moved = sorted(
        actor_id for actor_id, actor in fleet.items()
        if actor["meters"] > before_fleet[actor_id]["meters"]
        and math.dist(before_visuals[actor_id], visuals[actor_id]) > 0.2
    )
    assert len(moved) > 0, "No Android movement observed"
    bus_vehicles = None
    if args.bridge_url:
        endpoint = args.bridge_url.rstrip("/") + "/state/transport-lines?limit=20"
        with urllib.request.urlopen(endpoint, timeout=5) as response:
            transport = json.load(response)
        buses = next(line for line in transport["lines"] if line["name"] == "Nagoya2050 Egawa Local")
        assert buses["transportType"] == "Bus" and buses["stops"] == 5
        bus_vehicles = buses["vehicles"]
    report = {
        "utc": after["utc"],
        "runId": after["runId"],
        "moduleMvid": after["assemblyMvid"],
        "androids": len(fleet),
        "androidsMovedInSample": len(moved),
        "movedIds": moved,
        "androidsWithDeliveries": sum(actor["deliveries"] > 0 for actor in fleet.values()),
        "androidsWithCollections": sum(actor["collections"] > 0 for actor in fleet.values()),
        "androidsWithCharges": sum(actor["charges"] > 0 for actor in fleet.values()),
        "modPedestrians": sum(row["kind"] == "human" and row["active"] for row in after["visuals"]),
        "nativeCitizenRecords": after["nativeCitizenRecords"],
        "nativeCitizenInstances": after["nativeCitizenInstances"],
        "nativeBusVehicles": bus_vehicles,
        "parcelTotals": after["simulation"]["parcelTotals"],
    }
    with args.report.open("x", encoding="utf-8") as target:
        json.dump(report, target, indent=2)
    print(json.dumps({key: value for key, value in report.items() if key != "movedIds"}))


if __name__ == "__main__":
    main()
