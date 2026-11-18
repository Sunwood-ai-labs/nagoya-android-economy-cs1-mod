"""Read public-in-game counters into a local Nagoya coexistence dashboard."""
import argparse
import json
import re
import urllib.request
from datetime import datetime, timezone
from pathlib import Path


BRIDGE = "http://127.0.0.1:32123"
STANDARD_GRID_SIZE = 5
TILE_METRES = 1920
SOURCES = {
    "summary": "/state/summary",
    "zones": "/state/zones",
    "growables": "/state/growables?limit=1",
    "transport": "/state/transport-lines?limit=100",
    "connections": "/state/external-connections?limit=10",
    "areas": "/state/map-areas",
}


def query(path):
    with urllib.request.urlopen(BRIDGE + path, timeout=2.5) as response:
        result = json.load(response)
    if result.get("ok") is not True:
        raise ValueError("CS1 did not return a valid reading")
    return result


def read_file(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def age(timestamp, now):
    if not timestamp:
        return None
    return round((now - datetime.fromisoformat(timestamp.replace("Z", "+00:00"))).total_seconds(), 1)


def observe(root):
    now = datetime.now(timezone.utc)
    native, failures = {}, []
    for name, route in SOURCES.items():
        try:
            native[name] = query(route)
        except (ValueError, OSError, TimeoutError):
            failures.append(name)
            if name == "summary":
                break
    counts = native.get("growables", {}).get("countsByService")
    summary = native.get("summary", {})
    transport = native.get("transport", {})
    lines = transport.get("lines")
    bus_lines = None if lines is None else [line for line in lines if line.get("transportType") == "Bus"]
    document = {
        "capturedUtc": now.isoformat(),
        "unavailableNativeReadings": failures,
        "native": {
            "gameTime": summary.get("gameTime"),
            "paused": summary.get("simulation", {}).get("paused"),
            "citizenRecords": summary.get("citizens", {}).get("count"),
            "zonedCells": native.get("zones", {}).get("zonedCells"),
            "growables": native.get("growables", {}).get("total"),
            "homes": None if counts is None else counts.get("Residential", 0),
            "shops": None if counts is None else counts.get("Commercial", 0),
            "industry": None if counts is None else counts.get("Industrial", 0),
            "offices": None if counts is None else counts.get("Office", 0),
            "outsideConnected": native.get("connections", {}).get("cityConnectedToOutside"),
            "unlockedTiles": native.get("areas", {}).get("areaCount"),
            "demand": summary.get("demand"),
            "lineCount": transport.get("total"),
            "linePassengers": None if lines is None else sum(row.get("passengers", 0) for row in lines),
            "linesRead": None if lines is None else len(lines),
            "busLines": None if bus_lines is None else len(bus_lines),
            "busVehicles": None if bus_lines is None else sum(line["vehicles"] for line in bus_lines),
            "busPassengers": None if bus_lines is None else sum(line["passengers"] for line in bus_lines),
        },
        "mod": None,
        "scenery": None,
        "nativeStationWalk": None,
    }
    try:
        scene = read_file(root / "runtime" / "block-runtime.json")
        document["scenery"] = {
            "revision": scene["revision"],
            "sourceBuildings": scene.get("sourceBuildingCount"),
            "cataloguedBuildings": scene.get("cataloguedBuildings"),
            "sceneObjects": scene.get("sceneObjectCount"),
        }
    except (OSError, KeyError, ValueError):
        pass
    try:
        state = read_file(root / "citywide-live-v1" / "state.json")
        seconds = age(state.get("utc"), now)
        agents = state.get("simulation", {}).get("regions", [])
        by_node = {row["node"]: row["sourceID"] for row in state.get("entranceBuildingTargets", [])}
        regions = []
        for region in agents:
            grid = re.search(r"(-?\d+),\s*(-?\d+)$", region["name"])
            targets = [
                dict(row, sourceID=by_node[row["node"]])
                for row in region["targets"] if row["node"] in by_node
            ]
            couriers = [row for row in region["agents"] if row["kind"] == "android"]
            batteries = [row["battery"] for row in couriers]
            regions.append({
                "id": region["id"], "grid": list(map(int, grid.groups())) if grid else None,
                "deliveries": region["deliveryCount"], "collections": region["collectionCount"],
                "charges": sum(row["charges"] for row in couriers),
                "batteryLow": min(batteries, default=None), "batteryHigh": max(batteries, default=None),
                "targets": targets, "error": region["error"],
                "humanState": next((a["state"] for a in region["agents"] if a["kind"] == "human"), None),
                "androidCount": len(couriers),
                "charging": sum(row["state"] == "charging" for row in couriers),
                "waitingForCharger": sum(row["state"] == "waiting_for_charger" for row in couriers),
            })
        document["native"]["citizenInstances"] = state.get("nativeCitizenInstances")
        document["mod"] = {
            "runId": state["runId"], "observedUtc": state["utc"], "stateAgeSeconds": seconds,
            "fresh": seconds is not None and -3 <= seconds <= 12 and bool(summary),
            "phase": state["phase"], "network": state["networkName"],
            "activeAndroid": sum(row["active"] and row["kind"] == "android" for row in state.get("visuals", [])),
            "activeHuman": sum(row["active"] and row["kind"] == "human" for row in state.get("visuals", [])),
            "buildingCount": len(set(by_node.values())), "roadChunks": state["roadChunksLoaded"],
            "totals": state.get("simulation", {}).get("parcelTotals"),
            "regions": regions,
        }
    except (OSError, KeyError, ValueError):
        pass
    try:
        state = read_file(root / "simulation" / "humans-state.json")
        people = state.get("humans", [])
        if people:
            document["nativeStationWalk"] = {
                "observedUtc": state.get("utc"),
                "tracked": len(people),
                "active": sum(row.get("active", False) for row in people),
                "nativePath": sum(row.get("active", False) and (row.get("pathId") or 0) > 0 for row in people),
                "economicIsolation": state.get("residentIsolationInstalled") is True,
            }
    except (OSError, ValueError):
        pass
    try:
        city = root / "citywide-live-v1"
        config = read_file(city / "network.json")
        if document["mod"] and config["name"] == document["mod"]["network"]:
            hubs = read_file(city / config["regions"])["regions"]
            half_size = STANDARD_GRID_SIZE * TILE_METRES / 2
            document["mod"]["standardMapHubs"] = sum(
                -half_size <= hub["x"] < half_size and -half_size <= hub["z"] < half_size
                for hub in hubs
            )
    except (OSError, KeyError, ValueError):
        pass
    return document


def write_dashboard(root, path):
    document = observe(root)
    encoded = json.dumps(document, ensure_ascii=False).replace("<", "\\u003c")
    template = Path(__file__).with_name("society-dashboard.html").read_text(encoding="utf-8")
    tmp = path.with_suffix(".pending.html")
    with tmp.open("x", encoding="utf-8") as target:
        target.write(template.replace("__NAGOYA_OBSERVATION__", encoded, 1))
    tmp.replace(path)
    return document


if __name__ == "__main__":
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--root", type=Path, required=True)
    cli.add_argument("--output", type=Path, required=True)
    args = cli.parse_args()
    result = write_dashboard(args.root, args.output)
    print(json.dumps({"file": str(args.output), "capturedUtc": result["capturedUtc"],
                      "unavailableNativeReadings": result["unavailableNativeReadings"],
                      "modStateRead": result["mod"] is not None}))
