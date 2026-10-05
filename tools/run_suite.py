#!/usr/bin/env python3
"""Sequential bounded fixtures; every subprocess owns and cleans up its game PID."""
import json
import argparse
import signal
from pathlib import Path
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("fixtures", nargs="*", help="Fixture stems, without .json; default: all except baseline")
    parser.add_argument("--fail-fast", action="store_true", help="Stop after the first failed native fixture, preserving its report")
    parser.add_argument("--build-lab", action="store_true", help="Require a successful build/deploy before starting any fixture")
    parser.add_argument("--fixture-dir", type=Path, default=ROOT / "tests/fixtures", help="Fixture directory for an explicitly prepared Mod composition")
    args = parser.parse_args()
    names = args.fixtures or sorted(p.stem for p in args.fixture_dir.glob("*.json") if p.stem != "baseline")
    if not names:
        parser.error("No fixtures in " + str(args.fixture_dir))
    missing = [name for name in names if not (args.fixture_dir / f"{name}.json").is_file()]
    if missing:
        parser.error("Unknown fixtures: " + ", ".join(missing))
    if args.build_lab:
        subprocess.run([sys.executable, str(ROOT / "tools/build.py"), "--lab"], cwd=ROOT, check=True)
    results = []
    report = ROOT / "artifacts" / (time.strftime("%Y%m%d-%H%M%S") + "-suite.json")
    for name in names:
        print(f"RUN {name}", flush=True)
        process = subprocess.Popen([sys.executable, str(ROOT / "tools/run_fixture.py"),
                                    str(args.fixture_dir / f"{name}.json"), "--timeout", "75"],
                                   cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            stdout, stderr = process.communicate(timeout=90)
        except (KeyboardInterrupt, subprocess.TimeoutExpired) as error:
            # Let run_fixture's finally block stop its owned native host. Killing
            # only the Python child can leave that host and profile behind.
            process.send_signal(signal.SIGINT)
            try:
                stdout, stderr = process.communicate(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                stdout, stderr = process.communicate()
            interrupted = isinstance(error, KeyboardInterrupt)
            result = {"status": "Interrupted" if interrupted else "HarnessFailed",
                      "error": "Suite interrupted" if interrupted else "Fixture wrapper exceeded 90 seconds",
                      "cleanupOutput": stderr[-2000:]}
            results.append({"fixture": name, **result})
            report.write_text(json.dumps(results, ensure_ascii=False, indent=2))
            (ROOT / "artifacts/latest-suite.json").write_text(json.dumps(results, ensure_ascii=False, indent=2))
            print(f"Report: {report}", flush=True)
            return 130 if interrupted else 1
        try:
            result = json.loads(stdout)
        except json.JSONDecodeError:
            result = {"status": "HarnessFailed", "error": stdout + stderr}
        results.append({"fixture": name, **result})
        report.write_text(json.dumps(results, ensure_ascii=False, indent=2))
        print(json.dumps({"fixture": name, "status": result["status"], "error": result.get("error")}, ensure_ascii=False), flush=True)
        if args.fail_fast and result["status"] != "Passed":
            break
    print(f"Report: {report}", flush=True)
    (ROOT / "artifacts/latest-suite.json").write_text(json.dumps(results, ensure_ascii=False, indent=2))
    return 0 if all(r["status"] == "Passed" for r in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
