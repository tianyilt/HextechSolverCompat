#!/usr/bin/env python3
"""Bounded pilot of the solver's native request/ready process reuse protocol."""
import argparse
from build_state import require_deployed_current
import fcntl
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import uuid

from run_fixture import ROOT, verify_result


def atomic_json(path, value):
    temporary = path.with_name(path.name + ".reuse-tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2))
    temporary.replace(path)


def read_json(path):
    if not path.exists():
        return None
    try:
        return json.loads(path.read_text())
    except json.JSONDecodeError:
        return None


def wait_native(path, run_id, process, timeout, session_deadline):
    deadline = min(time.monotonic() + timeout, session_deadline)
    while time.monotonic() < deadline:
        value = read_json(path)
        if value and value.get("runId") == run_id:
            return value
        if process.poll() is not None:
            raise RuntimeError(f"Owned native process exited: {process.returncode}")
        time.sleep(0.1)
    raise TimeoutError(f"Native {path.name} did not acknowledge the current run within {timeout} seconds")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("fixtures", nargs="*", help="1..20 fixture stems; always stops on a failure")
    parser.add_argument("--family", help="Reviewed mechanism family from docs/test-families.json")
    parser.add_argument("--fixture-dir", type=Path, default=ROOT / "tests/fixtures", help="Explicit fixture directory for the prepared Mod composition")
    args = parser.parse_args()
    if args.family:
        families = json.loads((ROOT / "docs/test-families.json").read_text())["families"]
        if args.family not in families:
            parser.error("Unknown mechanism family: " + args.family)
        args.fixtures = list(dict.fromkeys(families[args.family]["fixtures"] + args.fixtures))
    if not 1 <= len(args.fixtures) <= 20:
        parser.error("Pilot batches must contain 1..20 fixtures")
    inputs = []
    for name in args.fixtures:
        path = args.fixture_dir / (name + ".json")
        if not path.is_file():
            parser.error("Unknown fixture: " + name)
        request = json.loads(path.read_text())
        if request.get("hextechExpectedRejection") or request.get("holdAfterInitialSearch"):
            parser.error("Reuse pilot accepts normal, completing native fixtures only")
        for power in request.get("powers", []):
            for field, default in [("target", "Enemy"), ("powerTarget", None), ("applier", None)]:
                if (selector := power.get(field, default)) and selector not in {"Player", "Osty", "CheckedEnemy", "Enemy"}:
                    parser.error(f"Invalid native power selector: {field}={selector}")
        inputs.append((name, request))
    lab = ROOT / ".local/headless-instances/hextech"
    paths = json.loads((lab / "paths.json").read_text())
    data = Path(paths["data"])
    if data.resolve() != (lab / "user-data").resolve():
        raise RuntimeError("Unexpected lab data path")
    # Same lock as cold-process fixtures; never share the isolated profile.
    with (lab / "fixture.lock").open("w") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        session = ROOT / "artifacts" / (time.strftime("%Y%m%d-%H%M%S") + "-reuse-session")
        session.mkdir()
        report = session.with_name(session.name + "-suite.json")
        mods = Path(paths["mods"])
        atomic_json(session / "build.json", require_deployed_current(mods))
        hashes = {str(path.relative_to(mods)): hashlib.sha256(path.read_bytes()).hexdigest()
                  for path in mods.rglob("*") if path.is_file()
                  and (path.suffix in {".dll", ".pck", ".manifest"} or path.name == "HextechRunes.json")}
        redirect = json.loads((lab / "path-redirect.json").read_text())
        godot = Path(redirect["assemblyPath"])
        host_files = {"nativeGame": Path(paths["binary"]), "sts2": godot.parent / "sts2.dll",
                      "GodotSharpLab": godot, "GodotSharpOriginal": godot.with_name("GodotSharp.dll.lab-original"),
                      "pathRedirectManifest": lab / "path-redirect.json", "telemetryStub": lab / "disabled-telemetry.gd"}
        host = {"managedData": str(data), "sha256": {
            name: hashlib.sha256(path.read_bytes()).hexdigest() for name, path in host_files.items()}}
        binary = Path(paths["binary"])
        request_path = data / "combat_solver_test_request.json"
        result_path = data / "combat_solver_test_result.json"
        ready_path = data / "combat_solver_test_ready.json"
        fixture_path = session / "active-fixture.json"
        for name in ["combat_solver_test_request.json", "combat_solver_test_running.json",
                     "combat_solver_test_result.json", "combat_solver_test_ready.json"]:
            (data / name).unlink(missing_ok=True)
        env = os.environ.copy()
        env.update(COMBATSOLVER_HEADLESS="1", HEXTECH_COMPAT_LAB="1",
                   HEXTECH_COMPAT_LAB_DATA=str(data), HEXTECH_COMPAT_FIXTURE=str(fixture_path))
        process = None
        results = []
        evidence_dirs = []
        session_deadline = time.monotonic() + 1200
        atomic_json(session / "monitor.json", {"maxSessionSeconds": 1200,
            "perResultTimeoutSeconds": 75, "perReadyTimeoutSeconds": 35,
            "done": "Every result passed, has the current runId/processId and matching ready acknowledgement",
            "failed": "Native failure, missing required assertions, wrong request/process, timeout or missing quiescent ready"})
        with (session / "launcher.log").open("w") as stream:
            try:
                for index, (name, original) in enumerate(inputs):
                    evidence = session / f"{index:02d}-{name}"
                    evidence.mkdir()
                    evidence_dirs.append(evidence)
                    request = {**original, "runId": str(uuid.uuid4()), "exitOnComplete": False}
                    atomic_json(evidence / "original-fixture.json", original)
                    atomic_json(evidence / "request.json", request)
                    atomic_json(evidence / "assemblies.json", hashes)
                    atomic_json(evidence / "host.json", host)
                    result_path.unlink(missing_ok=True)
                    ready_path.unlink(missing_ok=True)
                    atomic_json(fixture_path, request)
                    log_offset = (session / "launcher.log").stat().st_size
                    started = time.monotonic()
                    print(f"RUN {name} process_sequence={index + 1}", flush=True)
                    atomic_json(request_path, request)
                    if process is None:
                        process = subprocess.Popen([str(binary), "--headless", "--disable-vsync", "--max-fps", "0",
                            "--force-steam=off", "--log-file", str(session / "godot.log")],
                            cwd=binary.parent, env=env, stdout=stream, stderr=subprocess.STDOUT)
                        atomic_json(session / "process-start.json", {"pid": process.pid, "binary": str(binary),
                            "maxSessionSeconds": 1200, "nativeProtocol": "request/result/ready"})
                    atomic_json(evidence / "process-start.json", {"pid": process.pid, "sequence": index + 1,
                        "requestRunId": request["runId"], "timeoutSeconds": 75})
                    try:
                        result = wait_native(result_path, request["runId"], process, 75, session_deadline)
                        atomic_json(evidence / "result.json", result)
                        if result.get("status") != "Passed":
                            raise RuntimeError(result.get("error") or "Native fixture failed")
                        if result.get("processId") != process.pid or result.get("reusedProcess") != (index > 0):
                            raise RuntimeError("Native process identity/reuse declaration does not match")
                        # In 0.47.2, ProcessReusable is assigned only on the
                        # failed-input path; it remains false even for Passed.
                        # The request loop's successful, quiescent READY for
                        # this exact runId is the actual reuse acknowledgement.
                        ready = wait_native(ready_path, request["runId"], process, 35, session_deadline)
                        if ready.get("held"):
                            raise RuntimeError("Native process remained attached to held search")
                        atomic_json(evidence / "ready.json", ready)
                        whole_log = (session / "launcher.log").read_bytes()
                        log = whole_log[log_offset:].decode(errors="replace")
                        (evidence / "launcher.log").write_text(log)
                        status = verify_result(request, result, log, whole_log.decode(errors="replace"))
                        entry = {"fixture": name, "evidence": str(evidence), "status": status,
                            "gameStatus": result.get("status"), "error": result.get("error"),
                            "completedChecks": result.get("completedChecks"), "requestRunId": request["runId"],
                            "nativePid": process.pid, "reusedProcess": index > 0,
                            "requestAndQuiescenceSeconds": time.monotonic() - started}
                    except Exception as error:
                        log = (session / "launcher.log").read_bytes()[log_offset:].decode(errors="replace")
                        (evidence / "launcher.log").write_text(log)
                        atomic_json(evidence / "failure.json", {"error": str(error), "requestRunId": request["runId"]})
                        entry = {"fixture": name, "evidence": str(evidence), "status": "Failed",
                                 "error": str(error), "nativePid": process.pid,
                                 "requestAndQuiescenceSeconds": time.monotonic() - started}
                    results.append(entry)
                    atomic_json(report, results)
                    print(json.dumps({key: entry.get(key) for key in ["fixture", "status", "error",
                        "nativePid", "reusedProcess", "requestAndQuiescenceSeconds"]}, ensure_ascii=False), flush=True)
                    if entry["status"] != "Passed":
                        break
            finally:
                if process is not None:
                    if process.poll() is None:
                        process.terminate()
                        try:
                            process.wait(timeout=5)
                        except subprocess.TimeoutExpired:
                            process.kill()
                            process.wait(timeout=5)
                    for evidence in evidence_dirs:
                        atomic_json(evidence / "process.json", {"pid": process.pid,
                            "exitCode": process.returncode, "nativeResultPresent": (evidence / "result.json").exists(),
                            "sharedBoundedProcess": True, "session": str(session)})
                    journals = list((data / "logs/CombatSolver").glob(f"{process.pid}-*"))
                    if len(journals) == 1:
                        shutil.copytree(journals[0], session / "solver-logs")
                atomic_json(session / "completion.json", {"status": "Passed" if len(results) == len(inputs)
                    and all(r["status"] == "Passed" for r in results) else "Failed",
                    "requested": len(inputs), "returned": len(results), "report": str(report)})
        print(f"Report: {report}", flush=True)
        return 0 if len(results) == len(inputs) and all(r["status"] == "Passed" for r in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
