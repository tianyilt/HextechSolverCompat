# Testing / 测试记录

Current native-tested binary: `8ae924216c665a6c4e7957cf508e3039f97d5f9ffdfce9be72947d49d4fe7ddf`.
Published DLL: `e83ccf3c625c0f612f6c34cfc377f5b64c032868d27be7e19385e371cb076c52`.
Only its external PDB debug-link path was replaced to remove the developer's personal path:128 bytes within offset840204..840332. All other PE bytes, code and metadata are unchanged. Metadata-only comparison verifies3,326/3,326 method contracts identical. See [artifact derivation](../evidence/artifact.json).

## Environment and method / 环境与方法

macOS ARM64, game0.111.0, official Solver0.48.1, Hextech0.9.7, RitsuLib0.6.5, SDK9.0.318. Exact dependency hashes/commits: [version lock](../versions.lock.json).

The11 daily Mods are CombatSolver, HextechRunes, RitsuLib, this adapter, BaseLib v3.4.7, MintySpire2 v1.2.0, QuickRestart v2.0.0, RelicRewardChoices0.3.9, ImportVanillaSaves v0.2.1, sts2_god_mod1.7.10, RandomForeseer0.13.18. Headless tests add the test-only HextechCompatLab; visible/daily games do not. The Lab uses the native Godot engine and an independently redirected user directory, with Steam off. It does not replace gameplay with a toy simulator.

Each native test compares original-callback results against solver branch state, including ordered card instances, powers, relic private fields, HP/resources, RNG, fingerprint and continuation. Fork checks verify parent/sibling isolation. Unknown effects must produce the exact recorded rejection. Two excluded enemy Hexes additionally exercise native manual Strike/EndTurn continuation. A result is accepted only after request identity, input hash, actual process/result, Ready completion, full Mod hashes, all required assertions and process reclamation agree.

Full reviewed matrix: **234/234 Passed**,98 mechanism families. [Machine-readable receipt and per-input native assertions](../evidence/native-matrix/verification.json); [mechanism-to-fixture mapping](release-matrix.json). Targeted newly registered content:68/68 (29 basic,10 combination/boundary,29 affected shared inputs), plus4 guards. Overlapping inputs are not summed. All371 registered player runes are implemented (333 default-selectable);136 enabled enemy Hexes after the two exclusions. Catalogue counts are code scope, not exhaustive combination testing. [Registered evidence](../evidence/registered/native-verification.json).

## Reproduction / 复现

Obtain your own game and official pinned dependencies. Do not distribute them. Install .NET9 SDK, copy `local.props.example` to `local.props`, fill the three dependency directories, and point `.work/dotnet/dotnet` at your installed dotnet executable. Restore packages before the build script's `--no-restore` builds:

```sh
dotnet restore HextechSolverCompat.csproj
dotnet restore tests/LabMod/HextechCompatLab.csproj
python3 tools/build.py
python3 tools/prepare_lab.py
```

`prepare_lab.py` creates an isolated Mac copy and verifies its independent user directory. It initially copies Workshop dependency folders; these may now be newer than the pinned versions. Before deploying, use its `.local/headless-instances/hextech/paths.json` to locate the isolated `mods` directory. Put the official pinned CombatSolver/HextechRunes/RitsuLib bundles there (preserving loader/runtime/PCK assets), and copy the seven additional pinned Mod folders listed above into that same isolated directory. Rename a bundle's `mod_manifest.json` to its `<id>.json` if that is its only manifest. Each input explicitly lists the required12 Mod IDs/versions; do not weaken that list. This setup requires your own legally obtained copies of those exact versions. Then run `python3 tools/build.py --lab` to deploy only the adapter and test Lab. Never install Lab in daily gameplay.

Run individual source fixtures, for example:

```sh
python3 tools/run_reused_suite.py --fixture-dir tests/registered-fixtures scapegoat soul-calling player-nature
python3 tools/run_reused_suite.py --fixture-dir tests/registered-boundaries scapegoat-mixed-artifact card-inspection-nested-draw
```

The released matrix's exact gameplay inputs are each stored as `evidence/native-matrix/<fixture>/fixture.json`, with sanitized original native assertions beside them. Run selected files with `tools/run_reused_suite.py --fixture-dir <directory> <names>`. This tool expects all selected JSON files in one directory, so copy selected receipt fixtures there before rerunning. Requests finish at the first failure; each test waits at most75seconds and a reused host at most1200seconds. Inspect the saved request/result/Ready/assembly/log records in your local `artifacts/` before accepting results. No credentials, account IDs, real saves, game/third-party binaries or unsanitized original logs are exported.

## Retained failures / 真实失败与修复

- SoulCalling: inlined native AddSoulToPile initially leaked a Soul into the live hand. Patch the exact leaf before the caller is JIT-compiled; retest the native-versus-branch state.
- Scapegoat combination: simulated player Poison originally ran before native start-turn relic transfer (7HP mismatch). Preserve native callback order using the existing Poison implementation. A later9HP mismatch showed cloned delayed damage lost `AmountOnTurnStart`; preserve the original copied value on creation and existing destination value on stacking. Both failures remain private original records and are described here; strict assertions were retained.
- Warmogs: default-disabled registered runes were incorrectly excluded from adaptation. Default selection is not save/reward reachability. The full registered catalogue now includes all26 previously omitted player runes; no new player bans.
- Incorrect fixture IDs and an earlier manual-guard timeout are retained separately, corrected and rerun; they are not counted as successful gameplay effects.

## Gameplay and limits / 实机与边界

Current native-tested8ae924 completed an isolated genuine Silent A8 saved battle in5turns/21card plays, no HP loss,67/70HP, all11Mods/noLab/Steam off. The two screenshots show its route and actual reward checkpoint. Ordinary Steam startup, normal quit/restart, and resume were checked separately and preserve the original save/unclaimed rewards. The native current-build fresh Ironclad A0 run passed its first battle in4turns,80/80HP; it was closed on request, so this is not complete-run acceptance.

The headless host sometimes exits-6 after native completion; recorded process reclamation is not a clean Steam exit claim. The ordinary Steam quit log includes native resource-release warnings; no zero-warning/leak-free claim. Windows, a fresh complete run on this candidate, multiplayer, Tony algorithm, newer dependency versions and all arbitrary Mod combinations remain unverified. Only enemyGlobeHead/SolidTime are excluded; playerSolidTime is supported. Unreviewed effects stop solving while native manual play can continue.

## 术语 / Terms

Lab is the isolated test-only Mod. Headless means the real native engine without a visible window. A fingerprint distinguishes search states; continuation matches live combat to a retained route. Fork is an independent simulated branch. SHA-256 is a file digest; PID identifies a native process; Ready confirms request completion and quiescence. Counts describe checks at named versions, not all possible rune combinations or guaranteed wins.
