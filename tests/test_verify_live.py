import json
import sys
from datetime import datetime, timezone
from pathlib import Path

import pytest

from tools import verify_live


def _state():
    regions = []
    visuals = []
    for region_id, actor_id in ((0, 1), (1, 3)):
        regions.append(
            {
                "id": region_id,
                "error": None,
                "hubStock": 3,
                "chargingActor": 0,
                "targets": [
                    {"inventory": 0},
                    {"inventory": 0},
                    {"inventory": 0},
                ],
                "agents": [
                    {
                        "kind": "android",
                        "id": actor_id,
                        "state": "hub",
                        "currentJob": None,
                        "targetIndex": 0,
                        "carried": 0,
                        "error": None,
                        "meters": 10.0,
                        "deliveries": 1,
                        "collections": 1,
                        "charges": 1,
                    }
                ],
            }
        )
        visuals.append({"kind": "android", "id": actor_id, "active": True, "x": float(region_id), "z": 0.0})
    return {
        "utc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "phase": "running",
        "error": None,
        "networkName": "nagoya-citywide-building-service-v1",
        "roadChunksLoaded": 2,
        "roadChunksExpected": 2,
        "simulation": {
            "androidsPerRegion": 1,
            "regionCount": 2,
            "regions": regions,
            "parcelTotals": {"initial": 6, "conserved": True},
            "elapsed": 10.0,
        },
        "visuals": visuals,
        "runId": "fixture-run",
    }


def _write_state(tmp_path, state):
    path = tmp_path / "citywide-live-v1"
    path.mkdir()
    (path / "state.json").write_text(json.dumps(state), encoding="utf-8")
    return tmp_path


def test_read_state_uses_explicit_expected_counts(tmp_path):
    state, fleet, visuals = verify_live.read_state(
        _write_state(tmp_path, _state()),
        expected_road_chunks=2,
        expected_androids_per_region=1,
        expected_regions=2,
        expected_initial_parcels=6,
        expected_fleet_size=2,
    )

    assert state["runId"] == "fixture-run"
    assert set(fleet) == {1, 3}
    assert set(visuals) == {1, 3}


def test_read_state_rejects_wrong_explicit_count_even_with_optimized_python(tmp_path):
    root = _write_state(tmp_path, _state())
    with pytest.raises(verify_live.VerificationError, match="Road chunk count"):
        verify_live.read_state(root, expected_road_chunks=447)

    # The production check must remain active when callers use python -O; it
    # intentionally does not rely on the assert statement machinery.
    code = (
        "from pathlib import Path; import sys; sys.path.insert(0, r'%s'); "
        "from tools import verify_live; verify_live.require(False, 'sentinel')"
    ) % Path(__file__).resolve().parents[1]
    result = __import__("subprocess").run(
        [sys.executable, "-O", "-c", code], capture_output=True, text=True, timeout=10
    )
    assert result.returncode != 0
    assert "sentinel" in result.stderr
