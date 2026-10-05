#!/usr/bin/env python3
"""Package only an adapter binary backed by passing fixtures for matching inputs."""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile
from release_matrix import MATRIX, load_matrix
from inventory import inventory
from fixture_evidence import validated_request, validated_logs

ROOT = Path(__file__).resolve().parents[1]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--reports", type=Path, nargs="+", default=[ROOT / "artifacts/latest-suite.json"])
    parser.add_argument("--fixture-dir", type=Path, default=ROOT / "tests/fixtures",
                        help="Exact reviewed input directory used by these native reports")
    args = parser.parse_args()
    _, required_fixtures = load_matrix()
    catalogue = inventory()
    # Default-disabled runes can be enabled, appear in rewards and remain in
    # saves. The full-compatibility release gate covers every registered rune.
    missing_runes = [r["type"] for r in catalogue["playerRunes"]
                     if r["adapterStatus"] != "implemented"]
    missing_hexes = [h["id"] for h in catalogue["enemyHexes"]
                    if not h["upstreamDisabled"] and h["adapterStatus"] not in {"implemented", "banned"}]
    missing_models = [m["type"] for m in catalogue["dependentModels"]
                      if m["adapterStatus"] not in {"implemented", "reviewed-inert", "reviewed-unreachable"}]
    if missing_runes or missing_hexes or missing_models:
        raise RuntimeError(f"Full Hextech compatibility is unfinished: {len(missing_runes)} player runes and "
                           f"{len(missing_hexes)} enemy hexes, plus {len(missing_models)} dependent model classes, still need implementation or validation. "
                           "A passing subset must not be packaged as the requested complete compatibility release.")
    release_path = ROOT / "docs/release-validation.json"
    if not release_path.exists():
        raise RuntimeError("Official workshop binaries and visible Steam gameplay have not been validated; no release validation receipt exists.")
    release = json.loads(release_path.read_text())
    if release.get("releaseMatrixSha256") != digest(MATRIX):
        raise RuntimeError("Release acceptance does not match the reviewed mechanism matrix")
    adapter = ROOT / "bin/Release/net9.0/HextechSolverCompat.dll"
    adapter_hash = digest(adapter)
    versions = json.loads((ROOT / "versions.lock.json").read_text())
    if release.get("adapterSha256") != adapter_hash or release.get("dependencies") != versions["sha256"]:
        raise RuntimeError("Release validation belongs to different adapter or dependency binaries")
    for required in ["officialWorkshopBinaries", "visibleSteamGameplay", "completeSinglePlayerRun"]:
        check = release.get(required, {})
        if check.get("status") != "Passed" or not check.get("evidenceSha256"):
            raise RuntimeError(f"Release validation is incomplete: {required}")
        for filename, expected_hash in check["evidenceSha256"].items():
            file = (ROOT / filename).resolve()
            if not file.is_relative_to(ROOT) or not file.is_file() or digest(file) != expected_hash:
                raise RuntimeError(f"Release evidence missing or changed: {required}/{filename}")
    cases = {}
    for report in args.reports:
        for case in json.loads(report.read_text()):
            cases[case["fixture"]] = case
    if set(required_fixtures) - cases.keys():
        raise RuntimeError(f"Missing mechanism or boundary fixtures: {sorted(set(required_fixtures) - cases.keys())}")
    evidence_rows = []
    for name in required_fixtures:
        case = cases[name]
        if case["status"] != "Passed":
            raise RuntimeError(f"Fixture did not pass: {name}")
        evidence = Path(case["evidence"])
        request = validated_request(evidence, json.loads((args.fixture_dir / f"{name}.json").read_text()))
        hashes = json.loads((evidence / "assemblies.json").read_text())
        expected = {
            "HextechSolverCompat/HextechSolverCompat.dll": adapter_hash,
            "CombatSolver/CombatSolver.dll": versions["sha256"]["CombatSolver"],
            f"STS2-RitsuLib/compat/{versions['game']}/STS2-RitsuLib.Runtime.dll": versions["sha256"]["RitsuRuntime"],
            f"HextechRunes/lib/{versions['game']}/HextechRunes.dll": versions["sha256"]["HextechRunes"],
            "HextechRunes/HextechRunes.dll": versions["sha256"]["HextechLoader"],
            "HextechRunes/HextechRunes.pck": versions["sha256"]["HextechPck"],
        }
        for filename, expected_hash in expected.items():
            if hashes.get(filename) != expected_hash:
                raise RuntimeError(f"Untested dependency file for {name}: {filename}")
        host = json.loads((evidence / "host.json").read_text())
        if host["sha256"]["sts2"] != versions["sha256"]["sts2"]:
            raise RuntimeError(f"Untested game binary for {name}")
        result = json.loads((evidence / "result.json").read_text())
        rejection = request.get("hextechExpectedRejection")
        if rejection:
            if result["status"] != "Failed" or rejection not in (result.get("error") or ""):
                raise RuntimeError(f"Rejection evidence is not valid: {name}")
        elif result["status"] != "Passed":
            raise RuntimeError(f"Native result is not Passed: {name}")
        log, startup = validated_logs(evidence, request)
        if "Exception thrown when calling mod initializer" in startup:
            raise RuntimeError(f"Mod initialization failed: {name}")
        if any(mod.startswith("HextechSolverCompat@") for mod in request.get("expectedLoadedMods", [])):
            if "[HextechSolverCompat] Ready:" not in startup:
                raise RuntimeError(f"Adapter initialization was not verified: {name}")
        markers = [line for line in log.splitlines() if line.startswith("HEXTECH_") and "VERIFIED" in line]
        for option, marker in [("hextechForkProbe", "HEXTECH_FORK_VERIFIED"),
                           ("hextechNativeRuneForkProbe", "HEXTECH_NATIVE_RUNE_FORK_VERIFIED"),
                           ("hextechGeneratedOnPlayProbe", "HEXTECH_GENERATED_ONPLAY_VERIFIED"),
                           ("hextechGeneratedOnPlayProbe", "HEXTECH_HAND_SIZE_PATCH_VERIFIED"),
                           ("hextechNeutralNegativeProbe", "HEXTECH_NEUTRAL_NEGATIVE_VERIFIED"),
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
                           ("hextechNativeTransientAutoPlayProbe", "HEXTECH_NATIVE_TRANSIENT_AUTOPLAY_VERIFIED"),
                           ("hextechNativeEnemyPeriodicProbe", "HEXTECH_NATIVE_ENEMY_PERIODIC_VERIFIED"),
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
            if request.get(option) and not any(marker in value for value in markers):
                raise RuntimeError(f"Missing assertion in {name}: {marker}")
        for prefix, values in [("HEXTECH_EFFECT_VERIFIED", request.get("hextechExpected", {})),
                               ("HEXTECH_INITIAL_VERIFIED", request.get("hextechInitialExpected", {}))]:
            for field, value in values.items():
                if f"{prefix} {field}={value}" not in markers:
                    raise RuntimeError(f"Missing effect observation in {name}: {field}")
        evidence_rows.append({"fixture": name, "status": "Passed", "gameStatus": result["status"],
                              "expectedRejection": rejection, "evidence": str(evidence.relative_to(ROOT)),
                              "requestSha256": digest(evidence / "request.json"),
                              "resultSha256": digest(evidence / "result.json"),
                              "labSha256": hashes["HextechCompatLab/HextechCompatLab.dll"],
                              "finishedAtUtc": result.get("finishedAtUtc"),
                              "completedChecks": result.get("completedChecks", []), "observations": markers})
    docs = ROOT / "docs"
    docs.mkdir(exist_ok=True)
    receipt = {"adapterSha256": adapter_hash, "versions": versions, "passed": len(evidence_rows),
               "total": len(required_fixtures), "fixtureDirectory": str(args.fixture_dir.relative_to(ROOT)),
               "cases": evidence_rows}
    (docs / "verification.json").write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n")
    rows = [f"| `{row['fixture']}` | {'按预期拒绝' if row['expectedRejection'] else '通过'} | `{row['evidence']}` |" for row in evidence_rows]
    text = f"""# 验证记录

本次验收 **{len(evidence_rows)}/{len(required_fixtures)}** 用例通过，其中 {sum(bool(r['expectedRejection']) for r in evidence_rows)} 个用例验证未适配/禁用效果被明确拒绝。所有用例运行真实游戏进程；无窗口测试不等于已完成可视界面验收或整局通关。

游戏 {versions['game']}、CombatSolver {versions['CombatSolver']}、官方工坊 HextechRunes {versions['HextechRunes']}、RitsuLib {versions['RitsuLib']}、macOS ARM64。原生用例以短批复用进程或独立进程执行，单项结果超时75秒；进程复用时还须取得同一请求的空闲确认。每项实际加载的Mod、seed、卡牌、血量、敌方行动与搜索参数见 `{args.fixture_dir.relative_to(ROOT)}/*.json` 及验收单中的原始请求；依赖摘要和源码提交在 `versions.lock.json`。

适配器 SHA256：`{adapter_hash}`。

| 用例 | 结果 | 本地完整证据目录 |
|---|---|---|
""" + "\n".join(rows) + """

机器可读验收单在 [verification.json](verification.json)，包含每项请求/结果 SHA256、测试专用 DLL SHA256、实际断言输出和结束时间。原始 `result.json` 对拒绝用例保留 `Failed`；验收只在错误文本匹配指定拒绝原因时记通过。

## 原始故障与限制

未装适配器的基线证据：`artifacts/20260928-010020-baseline/result.json`，报 `No BeforeSideTurnStart mirror is registered for HextechRunes.HextechMayhemModifier`。适配器启用后的 `adapted-baseline` 正常求解并击杀。

本机旧版本日志中的流电错误来自 `HextechGalvanicPower.AfflictionTitle`。本版通过禁用两项来源并拒绝已有流电状态来隔离，未实现流电语义。原配置备份在日常游戏配置文件旁，后缀 `.hextech-compat-20260928-012900-895061.bak`；修改仅为加入 `GlobeHead`、`SolidTime`，已经回读验证其余字段相同。

发行验收单 `release-validation.json` 必须包含官方工坊二进制、可见 Steam 战斗和完整单人局的证据，且对应本适配器与依赖摘要。无窗口测试修改的路径接口、遥测启动和测试 Mod 均不分发。Windows/Linux、联机、战前预测及其他第三方玩法 Mod 不属于本单人 macOS 验收。

术语：镜像是模拟战斗中的效果实现；状态指纹用于搜索去重；续算标识判断真实战斗是否仍匹配原路线；RNG 指伪随机数状态；SHA256 用于确认二进制/请求/结果文件一致。正文首次出现时已给出解释或此处定义。
"""
    (docs / "VERIFICATION.md").write_text(text)
    dist = ROOT / "dist"
    dist.mkdir(exist_ok=True)
    archive = dist / "HextechSolverCompat-0.1.0-experimental.zip"
    files = [adapter, *[ROOT / name for name in ["HextechSolverCompat.json", "README.md", "coverage.json",
             "versions.lock.json", "LICENSE", "THIRD_PARTY_NOTICES.md", "docs/VERIFICATION.md", "docs/verification.json"]]]
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as bundle:
        for path in files:
            target = path.name if path == adapter else str(path.relative_to(ROOT))
            bundle.write(path, "HextechSolverCompat/" + target)
    with zipfile.ZipFile(archive) as bundle:
        if bundle.testzip() is not None:
            raise RuntimeError("Archive CRC verification failed")
        if hashlib.sha256(bundle.read("HextechSolverCompat/HextechSolverCompat.dll")).hexdigest() != adapter_hash:
            raise RuntimeError("Packaged adapter differs from the tested binary")
        if any(name.endswith(".dll") and name != "HextechSolverCompat/HextechSolverCompat.dll" for name in bundle.namelist()):
            raise RuntimeError("Dependency or test DLL leaked into package")
    checksum = digest(archive)
    archive.with_suffix(".zip.sha256").write_text(f"{checksum}  {archive.name}\n")
    source_archive = dist / "HextechSolverCompat-0.1.0-source.zip"
    source_files = [p for pattern in ["*.csproj", "*.props", "*.example", "*.json", "*.md",
                    "src/**/*.cs", "tools/*.py", "tests/**/*.cs", "tests/**/*.csproj", "tests/**/*.json",
                    "docs/*.md", "docs/*.json"] for p in ROOT.glob(pattern)
                    if not any(part in {"bin", "obj", ".work", ".local"} for part in p.relative_to(ROOT).parts)
                    and p.name != "local.props"]
    source_files += [ROOT / "LICENSE", ROOT / ".gitignore"]
    with zipfile.ZipFile(source_archive, "w", zipfile.ZIP_DEFLATED) as bundle:
        for path in sorted(set(source_files)):
            bundle.write(path, "hextech-solver-compat/" + str(path.relative_to(ROOT)))
    with zipfile.ZipFile(source_archive) as bundle:
        if bundle.testzip() is not None or any(name.endswith(".dll") or name.endswith("/local.props") for name in bundle.namelist()):
            raise RuntimeError("Source archive verification failed")
    source_archive.with_suffix(".zip.sha256").write_text(f"{digest(source_archive)}  {source_archive.name}\n")
    print(json.dumps({"archive": str(archive), "sha256": checksum, "sourceArchive": str(source_archive),
                      "passed": len(evidence_rows)}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
