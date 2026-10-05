#!/usr/bin/env python3
"""Run a bounded fixture in the prepared clone and preserve its actual result/log."""
import argparse
import fcntl
import hashlib
import json
import os
import pathlib
import shutil
import subprocess
import time
from build_state import require_deployed_current

ROOT = pathlib.Path(__file__).resolve().parents[1]
_LOCK = None


def verify_result(request, result, log, initialization_log=None):
    status = result.get("status")
    if "Exception thrown when calling mod initializer" in (initialization_log or log):
        status = "Failed"
        result["error"] = "A mod initializer failed; fixture instrumentation may be incomplete; " + (result.get("error") or "")
    if "HEXTECH_LAB_PATH_VERIFIED" not in (initialization_log or log):
        status = "Failed"
        result["error"] = "Managed path isolation was not verified; " + (result.get("error") or "")
    if any(mod.startswith("HextechSolverCompat@") for mod in request.get("expectedLoadedMods", [])):
        if "[HextechSolverCompat] Ready:" not in (initialization_log or log):
            status = "Failed"
            result["error"] = "Adapter did not finish initialization; " + (result.get("error") or "")
    for option, marker in [("hextechForkProbe", "HEXTECH_FORK_VERIFIED"),
                           ("hextechNativeGrowthProbe", "HEXTECH_NATIVE_GROWTH_FORK_VERIFIED"),
                           ("hextechNativeGrowthProbe", "HEXTECH_NATIVE_GROWTH_MODELS_VERIFIED"),
                           ("hextechNeutralNegativeProbe", "HEXTECH_NEUTRAL_NEGATIVE_VERIFIED"),
                           ("hextechNativeRuneForkProbe", "HEXTECH_NATIVE_RUNE_FORK_VERIFIED"),
                           ("hextechGeneratedOnPlayProbe", "HEXTECH_GENERATED_ONPLAY_VERIFIED"),
                           ("hextechGeneratedOnPlayProbe", "HEXTECH_HAND_SIZE_PATCH_VERIFIED"),
                           ("hextechNativeMaxHandSize", "HEXTECH_NATIVE_HAND_SIZE_VERIFIED"),
                           ("hextechNativeScalarProbe", "HEXTECH_NATIVE_SCALAR_VERIFIED"),
                           ("hextechEnemyAttackCounterProbe", "HEXTECH_ENEMY_ATTACK_COUNTER_VERIFIED"),
                           ("hextechEnemyExhaustSlotProbe", "HEXTECH_ENEMY_EXHAUST_SLOT_VERIFIED"),
                           ("hextechNativeCostQueryProbe", "HEXTECH_NATIVE_COST_QUERY_VERIFIED"),
                           ("hextechNativeLocalCosts", "HEXTECH_NATIVE_LOCAL_COST_VERIFIED"),
                           ("hextechNativeSlyCards", "HEXTECH_NATIVE_SLY_SETUP_VERIFIED"),
                           ("hextechSpawnFurCoat", "HEXTECH_FUR_COAT_SPAWN_VERIFIED"),
                           ("hextechSummonProbe", "HEXTECH_SUMMON_VERIFIED"),
                           ("hextechNativeRounds", "HEXTECH_NATIVE_ROUNDS_VERIFIED"),
                           ("hextechEnemyRepeatProbe", "HEXTECH_ENEMY_REPEAT_ROOT_VERIFIED"),
                           ("hextechPowerExpiryContractProbe", "HEXTECH_POWER_EXPIRY_CONTRACT_VERIFIED"),
                           ("hextechTemporaryDexterityBoundaryProbe", "HEXTECH_TEMPORARY_DEXTERITY_BOUNDARY_VERIFIED"),
                           ("hextechNativeOrbSetup", "HEXTECH_NATIVE_ORB_PASSIVE_SETUP_VERIFIED"),
                           ("hextechNativeEnemyTurnProbe", "HEXTECH_NATIVE_ENEMY_TURN_ROOT_VERIFIED"),
                           ("hextechNativeEventContractProbe", "HEXTECH_NATIVE_EVENT_CONTRACT_VERIFIED"),
                           ("hextechNativeExpectedAfterSequence", "HEXTECH_NATIVE_SEQUENCE_EFFECTS_VERIFIED"),
                           ("hextechNativeExpectedAfterRounds", "HEXTECH_NATIVE_FUTURE_EFFECTS_VERIFIED"),
                           ("hextechNativeMentalShieldProbe", "HEXTECH_NATIVE_MENTAL_SHIELD_VERIFIED"),
                           ("hextechNativeBeforeHandDrawProbe", "HEXTECH_NATIVE_BEFORE_HAND_DRAW_VERIFIED"),
                           ("hextechNativeTurnResourceProbe", "HEXTECH_NATIVE_TURN_RESOURCE_VERIFIED"),
                           ("hextechNativeCardAcquisitionProbe", "HEXTECH_NATIVE_CARD_ACQUISITION_VERIFIED"),
                           ("hextechNativeSoulProjectionProbe", "HEXTECH_NATIVE_SOUL_PROJECTION_VERIFIED"),
                           ("hextechNativeInfernalStateProbe", "HEXTECH_NATIVE_INFERNAL_STATE_VERIFIED"),
                           ("hextechGeneratedReactionSteps", "HEXTECH_GENERATED_REACTIONS_VERIFIED"),
                           ("hextechGeneratedReactionRounds", "HEXTECH_GENERATED_REACTION_ROUNDS_VERIFIED"),
                           ("hextechGeneratedReactionPlayDefend", "HEXTECH_GENERATED_REACTION_NEXT_PLAY_VERIFIED"),
                           ("hextechOpeningFormProbe", "HEXTECH_OPENING_FORM_ROOT_VERIFIED"),
                           ("hextechNativeDupeProbe", "HEXTECH_NATIVE_DUPE_VERIFIED"),
                           ("hextechAutomationDrawProbe", "HEXTECH_AUTOMATION_DRAW_VERIFIED"),
                           ("hextechSetupChoices", "HEXTECH_SETUP_CHOICES_VERIFIED"),
                           ("hextechNativeRoundsAfterPlay", "HEXTECH_NATIVE_POST_PLAY_ROUNDS_VERIFIED"),
                           ("hextechNativePlaySequenceAfterRounds", "HEXTECH_NATIVE_POST_ROUND_SEQUENCE_VERIFIED"),
                           ("hextechExpectedRelics", "HEXTECH_NATIVE_RELIC_ACQUISITION_VERIFIED"),
                           ("hextechExpectedCardVariable", "HEXTECH_CARD_VARIABLE_VERIFIED"),
                           ("hextechNativeTokenForkProbe", "HEXTECH_NATIVE_TOKEN_FORK_VERIFIED"),
                           ("hextechNativeTokenActualProbe", "HEXTECH_NATIVE_TOKEN_ACTUAL_VERIFIED"),
                           ("hextechNativeDoubleVisionRewardProbe", "HEXTECH_NATIVE_DOUBLE_VISION_REWARDS_VERIFIED"),
                           ("hextechNativeHailOpeningProbe", "HEXTECH_NATIVE_HAIL_OPENING_VERIFIED"),
                           ("hextechNativeInstantProbe", "HEXTECH_NATIVE_INSTANT_DEATH_VERIFIED"),
                           ("hextechNativeColorDiscoveryProbe", "HEXTECH_NATIVE_COLOR_DISCOVERY_VERIFIED"),
                           ("hextechNativeCeremonialProbe", "HEXTECH_NATIVE_CEREMONIAL_VERIFIED"),
                           ("hextechNativeEnemyDualProbe", "HEXTECH_NATIVE_ENEMY_DUAL_VERIFIED"),
                           ("hextechNativeVitalSparkProbe", "HEXTECH_NATIVE_VITAL_SPARK_VERIFIED"),
                           ("hextechNativeMysteryProbe", "HEXTECH_NATIVE_MYSTERY_VERIFIED"),
                           ("hextechNativeVakuuProbe", "HEXTECH_NATIVE_VAKUU_VERIFIED"),
                           ("hextechNativeNatureProbe", "HEXTECH_NATIVE_NATURE_VERIFIED"),
                           ("hextechNativeNatureInterruptionProbe", "HEXTECH_NATIVE_NATURE_INTERRUPTION_VERIFIED"),
                           ("hextechNativeLegacyPlatingProbe", "HEXTECH_NATIVE_LEGACY_PLATING_VERIFIED"),
                           ("hextechNativeNatureBudgetProbe", "HEXTECH_NATIVE_NATURE_BUDGET_VERIFIED"),
                           ("hextechNativeDamageBoundaryProbe", "HEXTECH_NATIVE_DAMAGE_BOUNDARY_VERIFIED"),
                           ("hextechNativeSweepingProbe", "HEXTECH_NATIVE_SWEEPING_VERIFIED"),
                           ("hextechNativeGeneratedTokensProbe", "HEXTECH_NATIVE_GENERATED_TOKENS_VERIFIED"),
                           ("hextechNativeAuxiliaryProbe", "HEXTECH_NATIVE_AUXILIARY_VERIFIED"),
                           ("hextechNativeSolidTimeProbe", "HEXTECH_NATIVE_SOLID_TIME_VERIFIED"),
                           ("hextechNativeTheftProbe", "HEXTECH_NATIVE_THIEVING_VERIFIED"),
                           ("hextechNativeScopeCounterProbe", "HEXTECH_NATIVE_SCOPE_COUNTERS_VERIFIED"),
                           ("hextechNativeAttributeChangeProbe", "HEXTECH_NATIVE_ATTRIBUTE_CHANGE_VERIFIED"),
                           ("hextechNativeProjectionFreezeProbe", "HEXTECH_NATIVE_PROJECTION_FREEZE_VERIFIED"),
                           ("hextechNativeManualGateProbe", "HEXTECH_NATIVE_MANUAL_GATES_VERIFIED"),
                           ("hextechNativeDiscardQueueProbe", "HEXTECH_NATIVE_DISCARD_QUEUE_VERIFIED"),
                           ("hextechNativeDiscardQueueProbe", "HEXTECH_NATIVE_DISCARD_COUNTERS_VERIFIED"),
                           ("hextechNativeTransientAutoPlayProbe", "HEXTECH_NATIVE_TRANSIENT_AUTOPLAY_VERIFIED"),
                           ("hextechNativeEnemyPeriodicProbe", "HEXTECH_NATIVE_ENEMY_PERIODIC_VERIFIED"),
                           ("hextechNativeEnemyDebuffTurnsProbe", "HEXTECH_NATIVE_ENEMY_DEBUFF_TURNS_VERIFIED"),
                           ("hextechNativeCallbackChoiceCard", "HEXTECH_NATIVE_CALLBACK_CHOICE_VERIFIED"),
                           ("hextechNativePowerQueryProbe", "HEXTECH_NATIVE_POWER_QUERY_VERIFIED"),
                           ("hextechJudicatorBranchProbe", "HEXTECH_JUDICATOR_BRANCH_VERIFIED"),
                           ("hextechNativeOrbForkProbe", "HEXTECH_NATIVE_ORB_FORK_VERIFIED"),
                           ("hextechAttackResultProbe", "HEXTECH_NATIVE_ATTACK_RESULTS_VERIFIED"),
                           ("hextechOverkillSurvivorProbe", "HEXTECH_OVERKILL_SURVIVOR_VERIFIED"),
                           ("hextechOwnerDebuffProbe", "HEXTECH_OWNER_DEBUFF_VERIFIED"),
                           ("hextechEnemyDebuffProbe", "HEXTECH_ENEMY_DEBUFF_VERIFIED"),
                           ("hextechEnemyDamageProcProbe", "HEXTECH_ENEMY_DAMAGE_PROC_VERIFIED"),
                           ("hextechPlayerDamageProbe", "HEXTECH_PLAYER_DAMAGE_VERIFIED"),
                           ("hextechPlayerDamageRounds", "HEXTECH_PLAYER_DAMAGE_ROUND_VERIFIED"),
                           ("hextechPlayerDamagePostCard", "HEXTECH_PLAYER_DAMAGE_POST_CARD_VERIFIED"),
                           ("hextechProtectiveTurnProbe", "HEXTECH_PROTECTIVE_TURNS_VERIFIED"),
                           ("hextechWhiteHoleDrawEnergy", "HEXTECH_WHITE_HOLE_VERIFIED"),
                           ("hextechMountainSoulForkProbe", "HEXTECH_MOUNTAIN_FORK_VERIFIED"),
                           ("hextechProjectionProbe", "HEXTECH_PROJECTION_VERIFIED"),
                           ("hextechEnemySustainProbe", "HEXTECH_ENEMY_SUSTAIN_VERIFIED"),
                           ("hextechEnemyCoefficientProbe", "HEXTECH_ENEMY_COEFFICIENTS_VERIFIED"),
                           ("hextechEnemyOpeningProbe", "HEXTECH_ENEMY_OPENING_VERIFIED"),
                           ("hextechEnemyRevivalProbe", "HEXTECH_ENEMY_REVIVAL_VERIFIED"),
                           ("hextechEnemySpawnProbe", "HEXTECH_ENEMY_SPAWN_VERIFIED"),
                           ("hextechPatchCompositionProbe", "HEXTECH_PATCH_COMPOSITION_VERIFIED"),
                           ("hextechUnsupportedManualProbe", "HEXTECH_UNSUPPORTED_MANUAL_VERIFIED"),
                           ("hextechOneTurn", "HEXTECH_ONE_TURN_VERIFIED"),
                           ("hextechExpectedHealingMultiplier", "HEXTECH_EFFECT_VERIFIED healing_hp=")]:
        if (request.get(option) or option == "hextechNativeMentalShieldProbe" and option in request) and marker not in log:
            status = "Failed"
            result["error"] = f"Required assertion did not run: {marker}; game error: {result.get('error')}"
    # Negative fixtures require the exact rejection. Timeout/crash never passes.
    if expected := request.get("hextechExpectedRejection"):
        status = "Passed" if status == "Failed" and expected in (result.get("error") or "") else "Failed"
    return status


def run():
    global _LOCK
    parser = argparse.ArgumentParser()
    parser.add_argument("request", type=pathlib.Path)
    parser.add_argument("--timeout", type=int, default=90)
    args = parser.parse_args()
    if not 1 <= args.timeout <= 120:
        parser.error("timeout must be 1..120 seconds")
    paths = json.loads((ROOT / ".local/headless-instances/hextech/paths.json").read_text())
    _LOCK = (ROOT / ".local/headless-instances/hextech/fixture.lock").open("w")
    try:
        fcntl.flock(_LOCK, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        raise RuntimeError("Another fixture owns this isolated game profile")
    data = pathlib.Path(paths["data"])
    if data.resolve() != (ROOT / ".local/headless-instances/hextech/user-data").resolve():
        raise RuntimeError("Unexpected lab data path")
    request = json.loads(args.request.read_text())
    # The pinned native protocol uses exact, case-sensitive selectors. Fail
    # before launching a host when a fixture invents a name or casing.
    for power in request.get("powers", []):
        for field, default in [("target", "Enemy"), ("powerTarget", None), ("applier", None)]:
            selector = power.get(field, default)
            if selector and selector not in {"Player", "Osty", "CheckedEnemy", "Enemy"}:
                raise ValueError(f"Invalid native power selector {field}={selector!r}")
    if request.get("hextechNativeScalarProbe") and not request.get("scenarioId", "").startswith("REPORT-CARDS-"):
        raise ValueError("Native scalar probe must run in the bounded REPORT-CARDS scenario mode")
    if any(not isinstance(relic, dict) or not isinstance(relic.get("relicId"), str)
           for relic in request.get("relics", [])):
        raise ValueError("Fixture relics must contain objects with a string relicId")
    evidence = ROOT / "artifacts" / (time.strftime("%Y%m%d-%H%M%S") + "-" + args.request.stem)
    evidence.mkdir(parents=True)
    (evidence / "request.json").write_text(json.dumps(request, indent=2))
    mods = pathlib.Path(paths["mods"])
    (evidence / "build.json").write_text(json.dumps(require_deployed_current(mods), indent=2))
    hashes = {str(path.relative_to(mods)): hashlib.sha256(path.read_bytes()).hexdigest()
              for path in mods.rglob("*") if path.is_file() and
              (path.suffix in {".dll", ".pck", ".manifest"} or path.name == "HextechRunes.json")}
    (evidence / "assemblies.json").write_text(json.dumps(hashes, indent=2))
    # The isolated host relocates managed file paths and disables telemetry.
    # Preserve those inputs too; an adapter hash alone cannot identify this run.
    lab = ROOT / ".local/headless-instances/hextech"
    redirect = json.loads((lab / "path-redirect.json").read_text())
    godot = pathlib.Path(redirect["assemblyPath"])
    host_files = {"nativeGame": pathlib.Path(paths["binary"]), "sts2": godot.parent / "sts2.dll",
                  "GodotSharpLab": godot, "GodotSharpOriginal": godot.with_name("GodotSharp.dll.lab-original"),
                  "pathRedirectManifest": lab / "path-redirect.json", "telemetryStub": lab / "disabled-telemetry.gd"}
    (evidence / "host.json").write_text(json.dumps({"managedData": str(data), "sha256": {
        name: hashlib.sha256(path.read_bytes()).hexdigest() for name, path in host_files.items()}}, indent=2))
    for filename in ["combat_solver_test_result.json", "combat_solver_test_running.json", "combat_solver_test_ready.json"]:
        (data / filename).unlink(missing_ok=True)
    (data / "combat_solver_test_request.json").write_text(json.dumps(request))
    env = os.environ.copy()
    env["COMBATSOLVER_HEADLESS"] = "1"
    env["HEXTECH_COMPAT_LAB"] = "1"
    env["HEXTECH_COMPAT_LAB_DATA"] = str(data)
    env["HEXTECH_COMPAT_FIXTURE"] = str(evidence / "request.json")
    binary = pathlib.Path(paths["binary"])
    with (evidence / "launcher.log").open("w") as stream:
        process = subprocess.Popen([str(binary), "--headless", "--disable-vsync", "--max-fps", "0",
                                    "--force-steam=off", "--log-file", str(evidence / "godot.log")],
                                   cwd=binary.parent, env=env, stdout=stream, stderr=subprocess.STDOUT)
        (evidence / "process-start.json").write_text(json.dumps({"pid": process.pid,
            "binary": str(binary), "cwd": str(binary.parent), "godotLog": str(evidence / "godot.log"),
            "timeoutSeconds": args.timeout}, indent=2))
        result_path = data / "combat_solver_test_result.json"
        try:
            deadline = time.monotonic() + args.timeout
            while process.poll() is None and time.monotonic() < deadline and not result_path.exists():
                time.sleep(0.25)
            if result_path.exists():
                result = json.loads(result_path.read_text())
            else:
                timed_out = process.poll() is None and time.monotonic() >= deadline
                result = {"status": "HarnessFailed", "error":
                          f"No result; exit={process.poll()} timedOut={timed_out} limitSeconds={args.timeout}"}
            (evidence / "result.json").write_text(json.dumps(result, ensure_ascii=False, indent=2))
        finally:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
    (evidence / "process.json").write_text(json.dumps({"pid": process.pid, "exitCode": process.returncode,
                                                       "nativeResultPresent": result_path.exists()}, indent=2))
    # Solver diagnostics use their own per-process journal, not Godot stdout.
    # Preserve it before later fixtures reuse the isolated profile.
    journals = list((data / "logs/CombatSolver").glob(f"{process.pid}-*"))
    if len(journals) == 1:
        shutil.copytree(journals[0], evidence / "solver-logs")
        if not result_path.exists():
            for line in (journals[0] / "process.jsonl").read_text().splitlines():
                entry = json.loads(line)
                if "PROCESS_NOT_REUSABLE" in entry.get("Message", ""):
                    result["error"] += "; " + entry["Message"]
            (evidence / "result.json").write_text(json.dumps(result, ensure_ascii=False, indent=2))
    log = (evidence / "launcher.log").read_text(errors="replace")
    status = verify_result(request, result, log)
    print(json.dumps({"evidence": str(evidence), "status": status, "gameStatus": result.get("status"), "error": result.get("error"),
                      "completedChecks": result.get("completedChecks")}, ensure_ascii=False, indent=2))
    return 0 if status == "Passed" else 1


if __name__ == "__main__":
    raise SystemExit(run())
