"""Record the real CS1 window after the Nagoya camera acknowledges a shot."""

import argparse
import hashlib
import json
import math
import os
import re
import subprocess
import time
from pathlib import Path


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def replace_bytes(path, content):
    staging = path.with_suffix(".capture.tmp")
    with staging.open("xb") as target:
        target.write(content)
    os.replace(staging, path)


def capture_screenshot(args, command, edited):
    name = args.output.name
    if not re.fullmatch(r"[a-z0-9-]+\.png", name):
        raise ValueError("Use a lowercase PNG name")
    source = args.data / "runtime" / "captures" / name
    if source.exists():
        raise FileExistsError(source)
    payload = edited + ("\ncapture=" + name + "\n").encode("ascii")
    replace_bytes(command, payload)
    try:
        for delay in (4, 3):
            time.sleep(delay)
            if source.is_file():
                pixels = source.read_bytes()
                if pixels[-8:-4] == b"IEND":
                    with args.output.open("xb") as output:
                        output.write(pixels)
                    return
        raise RuntimeError("Native CS1 screenshot did not complete")
    finally:
        if command.read_bytes() == payload:
            replace_bytes(command, edited)


def record(args):
    runtime = args.data / "runtime"
    command = runtime / "block-command.ini"
    current = runtime / "block-runtime.json"
    life = args.data / "citywide-live-v1" / "android-life-state.json"
    if args.output.exists() or args.output.with_suffix(".json").exists():
        raise FileExistsError(args.output)
    before = read_json(current)
    if not before["revision"] or not before["count"]:
        raise RuntimeError("Nagoya is still loading")
    goal = {"lookX": args.x, "lookZ": args.z, "lookLift": args.lift,
            "zoom": args.zoom, "height": 0, "angleX": args.yaw, "angleY": args.pitch}
    original = command.read_bytes()
    edited = original.decode("utf-8-sig")
    if re.search(r"^capture=", edited, re.M):
        raise RuntimeError("Another capture is active")
    for key, value in goal.items():
        edited, count = re.subn(rf"^{key}=.*$", f"{key}={value}", edited, flags=re.M)
        if count != 1:
            raise RuntimeError(f"Missing or duplicate camera setting {key}")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    replace_bytes(command, edited.encode("utf-8"))
    try:
        camera = None
        for delay in (2, 3):
            time.sleep(delay)
            after = read_json(current)
            pose = after["camera"]
            if (after["revision"] == before["revision"] and abs(pose["currentSize"] - args.zoom) < .01
                    and math.dist(pose["currentPosition"],(args.x, 140 + args.lift, args.z)) < .04):
                camera = pose
                break
        if camera is None:
            raise RuntimeError("Game camera did not acknowledge the shot")
        if args.output.suffix.lower() == ".png":
            capture_screenshot(args, command, edited.encode("utf-8"))
        else:
            subprocess.run([
            "ffmpeg", "-hide_banner", "-loglevel", "error", "-n", "-f", "gdigrab",
            "-framerate", "30", "-draw_mouse", "0", "-i", "title=Cities: Skylines",
            "-t", str(args.seconds), "-an", "-vf", "scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "19", "-pix_fmt", "yuv420p",
            "-movflags", "+faststart", str(args.output)
            ],check=True,timeout=args.seconds + 16)
        with args.output.with_suffix(".json").open("x", encoding="utf-8") as output:
            json.dump({"source":"CS1 game capture","sceneRevision":before["revision"],
                       "camera":camera,"life":read_json(life) if life.exists() else None,
                       "sha256":hashlib.sha256(args.output.read_bytes()).hexdigest()},output,indent=2)
    finally:
        if command.read_bytes() == edited.encode("utf-8"):
            replace_bytes(command, original)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data",required=True,type=Path)
    parser.add_argument("--output",required=True,type=Path)
    parser.add_argument("--x",required=True,type=float)
    parser.add_argument("--z",required=True,type=float)
    parser.add_argument("--zoom",type=float,default=90)
    parser.add_argument("--lift",type=float,default=1)
    parser.add_argument("--yaw",type=float,default=145)
    parser.add_argument("--pitch",type=float,default=40)
    parser.add_argument("--seconds",type=int,choices=range(4,31),default=12)
    record(parser.parse_args())


if __name__ == "__main__":
    main()
