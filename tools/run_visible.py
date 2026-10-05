#!/usr/bin/env python3
"""Bounded visible game host; records its own PID and cleans up on exit."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--timeout", type=int, default=1200)
    parser.add_argument("--instance", choices=["hextech", "corrupted-regression", "warmogs-regression"], default="hextech")
    args = parser.parse_args()
    if not 1 <= args.timeout <= 1800:
        parser.error("visible sessions must be bounded to 1..1800 seconds")
    lab = ROOT / ".local/visible-instances" / args.instance
    paths = json.loads((lab / "paths.json").read_text())
    binary = Path(paths["binary"])
    data_name = "visible-lab" if args.instance == "hextech" else args.instance
    if lab not in binary.parents or paths["data"] != str(Path.home() / "Library/Application Support/HextechSolverCompat" / data_name):
        raise RuntimeError("Visible host is not the prepared isolated profile")
    if any(p.name == "HextechCompatLab.dll" for p in Path(paths["mods"]).rglob("*.dll")):
        raise RuntimeError("Visible profile must not contain the fixture mod")
    env = os.environ.copy()
    for name in ["COMBATSOLVER_HEADLESS", "HEXTECH_COMPAT_LAB", "HEXTECH_COMPAT_LAB_DATA", "HEXTECH_COMPAT_FIXTURE"]:
        env.pop(name, None)
    evidence = ROOT / "artifacts" / (time.strftime("%Y%m%d-%H%M%S") + "-visible-" + args.instance)
    evidence.mkdir()
    game = binary.parent.parent / "Resources/data_sts2_macos_arm64"
    hashes = {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in
              [game / "GodotSharp.dll", game / "sts2.dll", *Path(paths["mods"]).rglob("*.dll")]}
    (evidence / "assemblies.json").write_text(json.dumps(hashes, indent=2))
    with (evidence / "launcher.log").open("w") as log:
        process = subprocess.Popen([str(binary), "--force-steam=off", "--log-file", str(evidence / "godot.log")],
                                   cwd=binary.parent, env=env, stdout=log, stderr=subprocess.STDOUT)
        info = {"pid": process.pid, "evidence": str(evidence), "data": paths["data"],
                "timeout": args.timeout, "status": "running", "steamEnabled": False}
        (lab / "process.json").write_text(json.dumps(info, indent=2))
        print(json.dumps(info), flush=True)
        deadline = time.monotonic() + args.timeout
        try:
            while process.poll() is None and time.monotonic() < deadline:
                (evidence / "monitor.json").write_text(json.dumps({**info, "checkedAt": time.time()}))
                time.sleep(1)
        finally:
            timed_out = process.poll() is None
            if timed_out:
                process.terminate()
                try: process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
        info.update(status="timed_out" if timed_out else "exited", exitCode=process.returncode)
        (evidence / "process.json").write_text(json.dumps(info, indent=2))
        (lab / "process.json").write_text(json.dumps(info, indent=2))
        print(json.dumps(info), flush=True)
        return process.returncode or 0

if __name__ == "__main__":
    raise SystemExit(main())
