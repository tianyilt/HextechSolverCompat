#!/usr/bin/env python3
"""Verify bounded Warmogs evidence without relabelling the old full matrix."""
import hashlib
import json
import os
from pathlib import Path

from build_state import source_inputs
from fixture_evidence import validated_logs, validated_request
from run_fixture import verify_result

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/20261004-warmogs-fix'
PLAYER = [f'native-draw-warmogs-{s}' for s in
          ['below', 'threshold', 'multiple', 'saved-progress', 'night-combined']]
SHARED = ['native-draw-night-threshold', 'native-draw-night-before',
          'native-draw-dizzy-shuffle', 'native-draw-death-poison',
          'native-draw-death-terminal', 'native-exhaust-rekindle-four',
          'native-exhaust-rekindle-three', 'native-exhaust-rekindle-ethereal',
          'native-turn-divine-period', 'native-enemy-draw-warmogs-1',
          'native-enemy-draw-warmogs-2', 'native-enemy-draw-warmogs-3']


def read(p):
    return json.loads(p.read_text())


def digest(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()


def main():
    build = read(ROOT / '.work/current-build.json')
    if source_inputs() != build['inputs']:
        raise RuntimeError('Sources changed after build')
    # This is an incremental repair of exactly one registered callback family.
    # Every other adapter source, the Lab and dependency remain unchanged.
    old = read(ROOT / 'artifacts/20261003-191536-reuse-session/build.json')
    changed = sorted(k for k in set(old['inputs']) | set(build['inputs'])
                     if old['inputs'].get(k) != build['inputs'].get(k))
    if changed != ['src/NativeRuneBridge.DrawExhaustPeriod.cs']:
        raise RuntimeError('Hotfix review no longer matches one changed source: ' + str(changed))
    if old['assemblies']['HextechCompatLab'] != build['assemblies']['HextechCompatLab']:
        raise RuntimeError('Lab changed; re-review the regression scope')
    old_matrix = read(ROOT / 'artifacts/20261003-release-preparation/native-verification.json')
    if old_matrix['status'] != 'Passed' or old_matrix['build'] != old['assemblies']:
        raise RuntimeError('Baseline matrix does not belong to reviewed old build')
    plan = read(OUT / 'regression-plan.json')
    selected = {}
    for report_name in plan['reports']:
        report = ROOT / report_name
        rows = read(report)
        # Keep the two valid, quiescent results before a fixture expectation
        # error. Do not reuse the failed case or silently accept any failure.
        prefix = report_name == plan['correctedExpectationPrefix']['report']
        if prefix:
            if digest(report) != plan['correctedExpectationPrefix']['sha256']:
                raise RuntimeError('Audited prefix changed')
            if (len(rows) != 3 or [r['fixture'] for r in rows] != PLAYER[:3]
                    or rows[2]['status'] != 'Failed'
                    or 'expected=2 actual=1' not in rows[2]['error']):
                raise RuntimeError('Different fixture failure; no prefix approval')
            rows = rows[:2]
        for row in rows:
            name = row['fixture']
            if name in selected or row['status'] != 'Passed':
                raise RuntimeError('Duplicate or failed case: ' + name)
            evidence = Path(row['evidence']).resolve()
            if not evidence.is_relative_to(ROOT):
                raise RuntimeError('Evidence outside workspace')
            fixture_path = ROOT / 'tests/fixtures' / (name + '.json') if name in PLAYER else OUT / 'shared-regression-inputs' / (name + '.json')
            fixture = read(fixture_path)
            request = validated_request(evidence, fixture)
            result = read(evidence / 'result.json')
            log, startup = validated_logs(evidence, request)
            pid = read(evidence / 'process-start.json')['pid']
            if (result.get('processId') != pid or result.get('scenarioId') != fixture['scenarioId']
                    or len(fixture['expectedLoadedMods']) != 12
                    or verify_result(request, result, log, startup) != 'Passed'):
                raise RuntimeError('Native result identity or assertions differ: ' + name)
            ready = read(evidence / 'ready.json')
            if ready.get('runId') != request['runId'] or ready.get('held'):
                raise RuntimeError('Missing quiescent acknowledgement')
            if read(evidence.parent / 'build.json') != build:
                raise RuntimeError('Native evidence belongs to another build')
            completion = read(evidence.parent / 'completion.json')
            if completion['status'] != ('Failed' if prefix else 'Passed'):
                raise RuntimeError('Unexpected session completion')
            process = read(evidence / 'process.json')
            if process['pid'] != pid or process.get('exitCode') is None:
                raise RuntimeError('Owned process not reclaimed')
            try:
                os.kill(pid, 0)
            except ProcessLookupError:
                pass
            else:
                raise RuntimeError('Test PID remains live or was reused')
            assemblies = read(evidence / 'assemblies.json')
            reference = read(ROOT / old_matrix['cases'][0]['evidence'] / 'assemblies.json')
            expected = {**reference, 'HextechSolverCompat/HextechSolverCompat.dll': build['assemblies']['HextechSolverCompat']}
            if assemblies != expected:
                raise RuntimeError('Mod composition differs from reviewed baseline: ' + name)
            if read(evidence / 'host.json')['sha256']['sts2'] != old_matrix['dependencies']['sts2']:
                raise RuntimeError('Game changed')
            markers = [line for line in log.splitlines() if line.startswith('HEXTECH_')
                       and not any(x in line.lower() for x in ['/users/', 'token=', 'password', 'cookie', 'http'])]
            public = OUT / 'review-bundle' / name
            public.mkdir(parents=True, exist_ok=True)
            (public / 'input.json').write_text(json.dumps(fixture, ensure_ascii=False, indent=2) + '\n')
            (public / 'assertions.log').write_text('\n'.join(markers) + '\n')
            selected[name] = {'fixture': name, 'status': 'NativePassed',
                              'evidence': str(evidence.relative_to(ROOT)),
                              'requestSha256': digest(evidence / 'request.json'),
                              'resultSha256': digest(evidence / 'result.json'),
                              'nativePid': pid, 'nativeExitCode': process['exitCode'],
                              'assertionLines': len(markers)}
    if set(selected) != set(PLAYER + SHARED):
        raise RuntimeError('Missing/excess cases: ' + str(set(PLAYER + SHARED) ^ set(selected)))
    receipt = {'status': 'TargetedNativePassed', 'build': build,
               'changedSources': changed, 'dependencies': old_matrix['dependencies'],
               'total': len(selected), 'cases': list(selected.values()),
               'baselineMatrix': {'adapterSha256': old['assemblies']['HextechSolverCompat'],
                                  'receiptSha256': digest(ROOT / 'artifacts/20261003-release-preparation/native-verification.json'),
                                  'total': old_matrix['total'], 'retestedOnHotfix': False},
               'scope': 'Player Warmogs and its affected shared draw/exhaust/period family, plus enemy Warmogs. This is not a new full matrix, save replay, Steam combat, full run or Windows acceptance.',
               'knownCoverageGap': 'Other registered default-disabled player runes are still under audit; no complete compatibility claim.',
               'headlessShutdownNote': old_matrix['headlessShutdownNote']}
    (OUT / 'native-verification.json').write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + '\n')
    print(json.dumps({'status': receipt['status'], 'total': receipt['total'],
                      'adapterSha256': build['assemblies']['HextechSolverCompat']}))


if __name__ == '__main__':
    main()
