#!/usr/bin/env python3
"""Bind the registered-rune repair and affected regressions to one native build.

This receipt authorizes local testing/install only. It cannot satisfy the
complete-release, whole-run or Windows gates in package.py.
"""
import hashlib
import json
import os
from pathlib import Path

from build_state import source_inputs
from fixture_evidence import validated_logs, validated_request
from run_fixture import verify_result

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/20261004-registered-runes'


def read(path):
    return json.loads(path.read_text())


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    plan = read(OUT / 'verification-plan.json')
    build = read(ROOT / '.work/current-build.json')
    if source_inputs() != build['inputs']:
        raise RuntimeError('Sources changed after build')
    baseline = read(ROOT / 'artifacts/20261003-release-preparation/native-verification.json')
    reference = read(ROOT / baseline['cases'][0]['evidence'] / 'assemblies.json')
    expected = {**reference, **{f'{key}/{key}.dll': value for key, value in build['assemblies'].items()}}
    selected = {}
    for group in plan['groups']:
        names = set(group['fixtures'])
        returned = set()
        for report_name in group['reports']:
            report = ROOT / report_name
            for row in read(report):
                name = row['fixture']
                if name not in names or name in selected or row['status'] != 'Passed':
                    raise RuntimeError('Unexpected/duplicate/failed case: ' + name)
                evidence = Path(row['evidence']).resolve()
                if not evidence.is_relative_to(ROOT):
                    raise RuntimeError('Evidence outside workspace')
                fixture = read(ROOT / group['fixtureDir'] / (name + '.json'))
                request = validated_request(evidence, fixture)
                result = read(evidence / 'result.json')
                log, startup = validated_logs(evidence, request)
                pid = read(evidence / 'process-start.json')['pid']
                if (len(fixture['expectedLoadedMods']) != 12 or result.get('processId') != pid
                        or result.get('scenarioId') != fixture['scenarioId']
                        or verify_result(request, result, log, startup) != 'Passed'):
                    raise RuntimeError('Native assertion/identity mismatch: ' + name)
                if (read(evidence.parent / 'build.json') != build
                        or read(evidence.parent / 'completion.json')['status'] != 'Passed'):
                    raise RuntimeError('Not a completed session of the current build')
                if read(evidence / 'assemblies.json') != expected:
                    raise RuntimeError('Actual Mod composition changed: ' + name)
                if read(evidence / 'host.json')['sha256']['sts2'] != baseline['dependencies']['sts2']:
                    raise RuntimeError('Native game changed')
                process = read(evidence / 'process.json')
                if process['pid'] != pid or process.get('exitCode') is None:
                    raise RuntimeError('Missing owned process cleanup')
                try:
                    os.kill(pid, 0)
                except ProcessLookupError:
                    pass
                else:
                    raise RuntimeError('Native PID is live or reused; inspect ownership')
                markers = [line for line in log.splitlines() if line.startswith('HEXTECH_')
                           and not any(x in line.lower() for x in ('/users/', 'token=', 'password', 'cookie', 'http'))]
                public = OUT / 'review-bundle' / name
                public.mkdir(parents=True, exist_ok=True)
                (public / 'input.json').write_text(json.dumps(fixture, ensure_ascii=False, indent=2) + '\n')
                (public / 'assertions.log').write_text('\n'.join(markers) + '\n')
                selected[name] = {'fixture': name, 'group': group['name'], 'status': 'NativePassed',
                                  'inputSha256': digest(ROOT / group['fixtureDir'] / (name + '.json')),
                                  'evidence': str(evidence.relative_to(ROOT)), 'nativePid': pid,
                                  'nativeExitCode': process['exitCode'], 'assertionLines': len(markers),
                                  'requestSha256': digest(evidence / 'request.json'),
                                  'resultSha256': digest(evidence / 'result.json')}
                returned.add(name)
        if returned != names:
            raise RuntimeError('Incomplete group: ' + group['name'] + '/' + str(names - returned))
    receipt = {'status': 'TargetedNativePassed', 'build': build, 'dependencies': baseline['dependencies'],
               'total': len(selected), 'groupCounts': {g['name']: len(g['fixtures']) for g in plan['groups']},
               'cases': list(selected.values()), 'registeredRuneWitnesses': plan['registeredRuneWitnesses'],
               'scope': 'All 26 omitted registered player runes, selected combination boundaries and affected shared native mechanisms. Local candidate acceptance, not a rerun of the historical full matrix.',
               'baseline': {'status': baseline['status'], 'total': baseline['total'],
                            'build': baseline['build'], 'retestedFullMatrix': False},
               'visibleSteamReceipt': 'steam-save-acceptance/verification.json',
               'headlessShutdownNote': 'Native completion and quiescent Ready are checked before owned host cleanup. Exit -6 during headless shutdown remains recorded; this is not clean visible-game exit evidence.',
               'publicReleaseAuthorized': False}
    (OUT / 'native-verification.json').write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + '\n')
    print(json.dumps({'status': receipt['status'], 'total': receipt['total'], 'groups': receipt['groupCounts'], 'adapter': build['assemblies']['HextechSolverCompat']}))


if __name__ == '__main__':
    main()
