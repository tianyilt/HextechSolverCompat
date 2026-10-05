#!/usr/bin/env python3
"""Verify the complete reviewed native matrix, then export sanitized evidence."""
import hashlib
import json
import os
import argparse
from pathlib import Path
from collections import Counter
from build_state import source_inputs
from fixture_evidence import validated_logs, validated_request
from release_matrix import load_matrix
from run_fixture import verify_result
from verify_scenario_modes import main as verify_scenario_modes

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/20261003-release-preparation'


def read(path):
    return json.loads(path.read_text())


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    global OUT
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--partial', action='store_true',
                        help='Audit completed evidence into a separate InProgress receipt; never satisfy release gates')
    parser.add_argument('--preparation', type=Path, default=OUT,
                        help='Candidate journal, independent of historical receipts')
    args = parser.parse_args()
    OUT = args.preparation.resolve()
    verify_scenario_modes(OUT)
    matrix, names = load_matrix()
    plan = read(OUT / 'native-release-plan.json')
    build = read(ROOT / '.work/current-build.json')
    if source_inputs() != build['inputs']:
        raise RuntimeError('Sources changed after the successful build')
    batches = read(OUT / 'native-batches.json')['completed']
    reports = [ROOT / row['report'] for row in batches]
    reports += [ROOT / path for path in plan.get('preverifiedReports', [])]
    reused_prefix_sessions = {}
    for approved in plan.get('reusedSuccessfulPrefixes', []):
        source = ROOT / approved['sourceReport']
        if digest(source) != approved['sourceReportSha256']:
            raise RuntimeError('Audited failed-session source report changed')
        original = read(source)
        prefix = read(ROOT / approved['prefixReport'])
        count = approved['sourcePrefixLength']
        expected_prefix = [r for r in original[:count] if r['fixture'] in approved['fixtures']]
        if (prefix != expected_prefix or len(original) != count+1
                or [r['fixture'] for r in prefix] != approved['fixtures']
                or not all(r['status']=='Passed' for r in original[:count])
                or original[count]['fixture'] != approved['failedFixture']
                or original[count]['status'] != 'Failed'):
            raise RuntimeError('Only the completed prefix before the exact audited failure may be reused')
        sessions = {Path(r['evidence']).parent for r in prefix}
        if len(sessions) != 1:
            raise RuntimeError('Prefix belongs to multiple native sessions')
        session = sessions.pop()
        completion = read(session/'completion.json')
        if (completion['status']!='Failed' or completion['returned']!=count+1
                or Path(completion['report'])!=source):
            raise RuntimeError('Audited later failure does not match native session completion')
        reused_prefix_sessions[session] = set(approved['fixtures'])
    cases = {}
    for report in reports:
        for row in read(report):
            if row['fixture'] in cases:
                raise RuntimeError('Duplicate matrix fixture: ' + row['fixture'])
            cases[row['fixture']] = row
    missing = sorted(set(names) - set(cases))
    if set(cases) - set(names) or (missing and not args.partial):
        raise RuntimeError(f'Incomplete matrix: missing={sorted(set(names)-set(cases))}, extra={sorted(set(cases)-set(names))}')
    pins = read(ROOT / 'versions.lock.json')['sha256']
    extras = read(ROOT / 'artifacts/20261003-expanded-compat-tests/lab-full-mod-installation.json')
    rows = []
    public = OUT / ('native-review-bundle-partial' if args.partial else 'native-review-bundle')
    for name in names:
        if name not in cases:
            continue
        case = cases[name]
        evidence = Path(case['evidence']).resolve()
        if not evidence.is_relative_to(ROOT):
            raise RuntimeError('Evidence outside project')
        fixture = read(ROOT / plan['fixtureDir'] / (name + '.json'))
        request = validated_request(evidence, fixture)
        result = read(evidence / 'result.json')
        log, startup = validated_logs(evidence, request)
        process = read(evidence / 'process-start.json')
        if case['status'] != 'Passed' or result.get('processId') != process['pid']:
            raise RuntimeError('Wrong native result or process: ' + name)
        if result.get('scenarioId') != fixture['scenarioId'] or verify_result(request, result, log, startup) != 'Passed':
            raise RuntimeError('Native assertion failed: ' + name)
        if len(fixture['expectedLoadedMods']) != 12:
            raise RuntimeError('Incomplete full Mod composition')
        assembly = read(evidence / 'assemblies.json')
        expected = {f'{key}/{key}.dll': value for key, value in build['assemblies'].items()}
        expected.update({'CombatSolver/CombatSolver.dll': pins['CombatSolver'],
                         'HextechRunes/lib/0.111.0/HextechRunes.dll': pins['HextechRunes'],
                         'HextechRunes/HextechRunes.dll': pins['HextechLoader'],
                         'HextechRunes/HextechRunes.pck': pins['HextechPck'],
                         'STS2-RitsuLib/compat/0.111.0/STS2-RitsuLib.Runtime.dll': pins['RitsuRuntime']})
        for extra in extras:
            expected.update({extra['mod']+'/'+path: value for path, value in extra['files'].items()
                             if Path(path).suffix in {'.dll', '.pck', '.manifest'}})
        for path, value in expected.items():
            if assembly.get(path) != value:
                raise RuntimeError('Different dependency: ' + name + '/' + path)
        if read(evidence / 'host.json')['sha256']['sts2'] != pins['sts2']:
            raise RuntimeError('Different game binary')
        if request.get('exitOnComplete') is False:
            session_passed = read(evidence.parent / 'completion.json')['status']=='Passed'
            audited_prefix = name in reused_prefix_sessions.get(evidence.parent, set())
            if not (session_passed or audited_prefix) or read(evidence.parent / 'build.json') != build:
                raise RuntimeError('Incomplete session or different build')
        elif read(evidence / 'build.json') != build:
            raise RuntimeError('Cold fixture belongs to different build')
        disposition = read(evidence / 'process.json')
        if disposition['pid'] != process['pid'] or disposition.get('exitCode') is None:
            raise RuntimeError('Test process not reclaimed')
        try:
            os.kill(process['pid'], 0)
        except ProcessLookupError:
            pass
        else:
            raise RuntimeError('Native PID remains live or has been reused; inspect ownership')
        markers = [line for line in log.splitlines() if line.startswith('HEXTECH_')
                   and not any(token in line.lower() for token in ('http', 'token=', '/users/', 'password', 'cookie'))]
        target = public / name
        target.mkdir(parents=True, exist_ok=True)
        (target / 'fixture.json').write_text(json.dumps(fixture, ensure_ascii=False, indent=2)+'\n')
        (target / 'assertions.txt').write_text('\n'.join(markers)+'\n')
        rows.append({'fixture': name, 'status': 'ExpectedRejectionVerified' if fixture.get('hextechExpectedRejection') else 'NativePassed',
                     'nativeStatus': result['status'], 'nativePid': process['pid'],
                     'nativeExitCode': disposition['exitCode'], 'evidence': str(evidence.relative_to(ROOT)),
                     'requestSha256': digest(evidence/'request.json'), 'resultSha256': digest(evidence/'result.json'),
                     'nativeMilliseconds': result['elapsedMilliseconds'], 'assertionLines': len(markers)})
    receipt = {'status': 'InProgress' if args.partial else 'Passed',
               'scope': 'Completed evidence audit; not release acceptance' if args.partial else 'Full reviewed mechanism matrix; not Steam UI, complete-run or Windows acceptance',
               'families': len(matrix['families']), 'cases': rows, 'total': len(rows),
               'expectedTotal': len(names), 'missingFixtures': missing,
               'counts': dict(Counter(row['status'] for row in rows)), 'build': build['assemblies'], 'dependencies': pins,
               'releaseMatrixSha256': digest(ROOT/'docs/release-matrix.json'),
               'headlessShutdownNote': 'Headless host exit -6 after native completion is recorded separately; it is not clean Steam exit evidence.'}
    (OUT/('native-verification-partial.json' if args.partial else 'native-verification.json')).write_text(json.dumps(receipt, ensure_ascii=False, indent=2)+'\n')
    safe = {**receipt, 'cases': [{k:v for k,v in row.items() if k not in {'nativePid','evidence'}} for row in rows]}
    (public/'verification.json').write_text(json.dumps(safe, ensure_ascii=False, indent=2)+'\n')
    monitor_path = OUT/'monitor.json'
    if monitor_path.exists():
        monitor = read(monitor_path)
        monitor.update(strictlyVerified=receipt['total'],
                       remainingToVerify=len(names)-receipt['total'])
        if not args.partial:
            monitor.update(status='Completed',stage='FullNativeMatrixVerified',activeNativeJob=None)
        monitor_path.write_text(json.dumps(monitor,ensure_ascii=False,indent=2)+'\n')
    print(json.dumps({k:receipt[k] for k in ['status','families','total','counts','build']}))


if __name__ == '__main__':
    main()
