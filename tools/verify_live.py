"""Check two actual CS1 readings for the installed Nagoya Android fleet."""

import argparse
import json
import math
import time
import urllib.request
from datetime import datetime, timezone
from pathlib import Path


class VerificationError(RuntimeError):
    """Raised when a live-state invariant is not met."""


def require(condition, message):
    if not condition:
        raise VerificationError(message)


def read_state(
    root,
    *,
    expected_road_chunks=None,
    expected_androids_per_region=None,
    expected_regions=None,
    expected_initial_parcels=None,
    expected_fleet_size=None,
):
    state = json.loads((root / "citywide-live-v1" / "state.json").read_text(encoding="utf-8"))
    observed = datetime.fromisoformat(state["utc"].replace("Z", "+00:00"))
    require(abs((datetime.now(timezone.utc) - observed).total_seconds()) < 12, "Stale game state")
    require(state["phase"] == "running" and not state["error"], "Game state is not running cleanly")
    require(state["networkName"] == "nagoya-citywide-building-service-v1", "Unexpected network name")
    require(
        state["roadChunksLoaded"] == state["roadChunksExpected"]
        and state["roadChunksLoaded"] >= 0,
        "Road chunk counts are inconsistent",
    )
    if expected_road_chunks is not None:
        require(state["roadChunksLoaded"] == expected_road_chunks, "Road chunk count did not match the explicit expectation")
    simulation = state["simulation"]
    androids_per_region = simulation["androidsPerRegion"]
    region_rows = simulation["regions"]
    require(androids_per_region > 0, "Simulation has no Androids per region")
    require(simulation["regionCount"] == len(region_rows) and len(region_rows) > 0, "Region count is inconsistent")
    if expected_androids_per_region is not None:
        require(androids_per_region == expected_androids_per_region, "Android count did not match the explicit expectation")
    if expected_regions is not None:
        require(len(region_rows) == expected_regions, "Region count did not match the explicit expectation")
    initial_parcels = simulation["parcelTotals"]["initial"]
    require(initial_parcels >= 0 and simulation["parcelTotals"]["conserved"], "Parcel totals are not conserved")
    if expected_initial_parcels is not None:
        require(initial_parcels == expected_initial_parcels, "Initial parcel count did not match the explicit expectation")
    require(initial_parcels % len(region_rows) == 0, "Initial parcels are not evenly allocated across regions")
    parcels_per_region = initial_parcels // len(region_rows)
    fleet = {}
    for region in region_rows:
        androids = [actor for actor in region["agents"] if actor["kind"] == "android"]
        require(len(androids) == androids_per_region and region["error"] is None, "Region has an error or unexpected Android count")
        require(region["hubStock"] >= 0, "Region has negative hub inventory")
        require(
            sum(target["inventory"] for target in region["targets"])
            + region["hubStock"]
            + sum(actor["carried"] for actor in androids)
            == parcels_per_region,
            "Region parcel inventory is not conserved",
        )
        require(all(0 <= target["inventory"] <= 1 for target in region["targets"]), "Target inventory is out of range")
        charging = [actor["id"] for actor in androids if actor["state"] == "charging"]
        require(charging == ([region["chargingActor"]] if region["chargingActor"] else []), "Charging owner is inconsistent")
        reserved = [actor["targetIndex"] for actor in androids if actor["currentJob"]]
        require(len(set(reserved)) == len(reserved), "A target has overlapping assignments")
        for actor in androids:
            require(actor["id"] not in fleet and actor["error"] is None, "Android identity or actor error is invalid")
            fleet[actor["id"]] = actor
    required_fleet_size = simulation["regionCount"] * androids_per_region
    if expected_fleet_size is not None:
        required_fleet_size = expected_fleet_size
    require(len(fleet) == required_fleet_size, "Fleet size did not match the explicit expectation or simulation")
    visuals = {row["id"]: (row["x"], row["z"]) for row in state["visuals"] if row["kind"] == "android" and row["active"]}
    require(visuals.keys() == fleet.keys(), "Active Android visuals did not match simulation actors")
    return state, fleet, visuals


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--bridge-url", help="Optional CS1 bridge URL for native bus verification")
    parser.add_argument("--expected-road-chunks", type=int)
    parser.add_argument("--expected-androids-per-region", type=int)
    parser.add_argument("--expected-regions", type=int)
    parser.add_argument("--expected-initial-parcels", type=int)
    parser.add_argument("--expected-fleet-size", type=int)
    parser.add_argument("--expected-bus-name")
    parser.add_argument("--expected-bus-stops", type=int)
    args = parser.parse_args()

    state_options = {
        "expected_road_chunks": args.expected_road_chunks,
        "expected_androids_per_region": args.expected_androids_per_region,
        "expected_regions": args.expected_regions,
        "expected_initial_parcels": args.expected_initial_parcels,
        "expected_fleet_size": args.expected_fleet_size,
    }
    before, before_fleet, before_visuals = read_state(args.root, **state_options)
    time.sleep(8)
    after, fleet, visuals = read_state(args.root, **state_options)
    require(before["runId"] == after["runId"] and fleet.keys() == before_fleet.keys(), "Live run identity changed during the sample")
    require(after["simulation"]["elapsed"] > before["simulation"]["elapsed"], "Simulation did not advance during the sample")
    moved = sorted(
        actor_id for actor_id, actor in fleet.items()
        if actor["meters"] > before_fleet[actor_id]["meters"]
        and math.dist(before_visuals[actor_id], visuals[actor_id]) > 0.2
    )
    require(len(moved) > 0, "No Android movement observed")
    bus_vehicles = None
    if args.bridge_url:
        endpoint = args.bridge_url.rstrip("/") + "/state/transport-lines?limit=20"
        with urllib.request.urlopen(endpoint, timeout=5) as response:
            transport = json.load(response)
        if args.expected_bus_name:
            buses = next((line for line in transport["lines"] if line["name"] == args.expected_bus_name), None)
            require(buses is not None, "Expected bus line was not found")
        else:
            buses = next((line for line in transport["lines"] if line.get("transportType") == "Bus"), None)
            require(buses is not None, "No bus line was returned by the bridge")
        require(buses["transportType"] == "Bus", "Expected transport line was not a bus")
        if args.expected_bus_stops is not None:
            require(buses["stops"] == args.expected_bus_stops, "Bus stop count did not match the explicit expectation")
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
