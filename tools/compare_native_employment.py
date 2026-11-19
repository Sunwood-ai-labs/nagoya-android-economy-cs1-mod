"""Compare two observations from the same CS1 checkpoint and game-clock window."""

import argparse
import json
from pathlib import Path


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def totals(sample):
    return {key: sample[key] for key in (
        "nativeHumanResidents", "humansWithWorkplace", "androidHomeMembers",
        "androidWorkMembers", "androidIdentities",
    )}


def identity(sample):
    keys = ("citizenId", "homeBuilding", "homeUnit", "workBuilding")
    return sorted(tuple(row[key] for key in keys) for row in sample["androids"])


def summarize(trial):
    base, admitted, end = (trial[k] for k in ("baseline", "afterAdmission", "end"))
    old_ids = {row["citizenId"] for row in base["androids"]}
    new_ids = {row["citizenId"] for row in admitted["androids"]} - old_ids
    observed = [row for row in end["androids"] if row["citizenId"] in new_ids]
    deliveries = end["nativeDeliveries"]
    opening_deliveries = base["nativeDeliveries"]
    opening_wards = opening_deliveries.get("deliveriesByWard", {})
    deliveries_by_ward = deliveries.get("deliveriesByWard", {})
    return {
        "caseId": trial["caseId"],
        "requestedExtraAndroids": trial["requestedAdditionalAndroids"],
        "baseline": totals(base), "afterAdmission": totals(admitted), "end": totals(end),
        "newAndroidsAdmitted": len(new_ids),
        "newAndroidsAtEnd": len(observed),
        "newAndroidsWithWorkplace": sum(row["workUnitConfirmed"] for row in observed),
        "newAndroidsWithCommutePath": sum(row["commuteSamples"] > 0 for row in observed),
        "newAndroidsWithWorkObservation": sum(row["workSamples"] > 0 for row in observed),
        "newAndroidsWithNativeWorkArrival": sum(row.get("nativeWorkArrivals", 0) > 0 for row in observed),
        "newAndroidsWithChargeObservation": sum(row["chargeStarts"] > 0 for row in observed),
        "shopDeliveries": deliveries["delivered"],
        "shopDeliveriesByWard": deliveries_by_ward,
        "shopDeliveriesDuringTrial": deliveries["delivered"] - opening_deliveries["delivered"],
        "shopDeliveriesDuringTrialByWard": {
            ward: count - opening_wards.get(ward, 0) for ward, count in deliveries_by_ward.items()
            if count > opening_wards.get(ward, 0)
        },
    }


def compare(control, treatment):
    if control["status"] != "completed" or treatment["status"] != "completed":
        raise ValueError("Both native cases must finish at their planned game deadline")
    window_keys = ("startTicks", "endTicks", "startFrame", "endFrame", "durationHours")
    if any(control[key] != treatment[key] for key in window_keys):
        raise ValueError("The two native cases did not use the same game ticks and frame window")
    baseline_a, baseline_b = control["baseline"], treatment["baseline"]
    if totals(baseline_a) != totals(baseline_b) or identity(baseline_a) != identity(baseline_b):
        raise ValueError("Residents and workplaces did not restore identically before the trial")
    if baseline_a["runId"] == baseline_b["runId"]:
        raise ValueError("The native cases must be separate loads of the saved city")
    a, b = summarize(control), summarize(treatment)
    if a["requestedExtraAndroids"] != 0 or a["newAndroidsAdmitted"] != 0 or b["newAndroidsAdmitted"] <= 0:
        raise ValueError("The requested change in Android admissions did not occur")
    result = {
        "startGameTime": baseline_a["gameTime"],
        "endGameTime": control["end"]["gameTime"],
        "window": {key: control[key] for key in window_keys},
        "control": a, "treatment": b,
        "endDifferenceTreatmentMinusControl": {key: b["end"][key] - a["end"][key] for key in a["end"]},
    }
    economy_a, economy_b = control.get("economy"), treatment.get("economy")
    if economy_a is not None or economy_b is not None:
        if economy_a is None or economy_b is None:
            raise ValueError("Both native cases must use the same opening reserve setup")
        start_fields = ("openingCashTargetRaw", "openingCreditRaw", "beforeOpeningCredit")
        if any(economy_a[key] != economy_b[key] for key in start_fields):
            raise ValueError("The two cases did not share the same opening cash or operating budget")
        result["economy"] = {
            "openingCashRaw": economy_a["openingCashTargetRaw"],
            "oneTimeOpeningCreditRaw": economy_a["openingCreditRaw"],
            "controlEnd": economy_a["end"],
            "treatmentEnd": economy_b["end"],
            "endDifferenceTreatmentMinusControl": {
                key: economy_b["end"][key] - value for key, value in economy_a["end"].items()
            },
        }
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("control", "treatment", "output"):
        parser.add_argument("--" + key, type=Path, required=True)
    args = parser.parse_args()
    result = compare(read(args.control), read(args.treatment))
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result["endDifferenceTreatmentMinusControl"]))
