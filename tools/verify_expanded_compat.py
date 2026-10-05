#!/usr/bin/env python3
"""Read back selected native receipts and export a sanitized review bundle."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path

from build_state import source_inputs
from fixture_evidence import validated_logs, validated_request
from run_fixture import ROOT, verify_result


def read(path):
    return json.loads(path.read_text())


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--batch', nargs=3, action='append', required=True,
                        metavar=('GROUP', 'FIXTURE_DIR', 'REPORT'))
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    build = read(ROOT / '.work/current-build.json')
    if source_inputs() != build['inputs']:
        raise RuntimeError('Current sources differ from the successful build')
    pins = read(ROOT / 'versions.lock.json')['sha256']
    reviewed_extras = read(ROOT / 'artifacts/20261003-expanded-compat-tests/lab-full-mod-installation.json')
    rows = []
    public = args.output / 'review-bundle'
    public.mkdir(parents=True, exist_ok=True)
    for group, fixture_dir, report in args.batch:
        report = Path(report)
        for item in read(report):
            name = item['fixture']
            evidence = Path(item['evidence'])
            fixture = read(Path(fixture_dir) / (name + '.json'))
            request = validated_request(evidence, fixture)
            case_log, startup = validated_logs(evidence, request)
            result = read(evidence / 'result.json')
            process = read(evidence / 'process-start.json')
            if result.get('processId') != process['pid']:
                raise RuntimeError('Native result belongs to another process')
            if result.get('scenarioId') != fixture['scenarioId']:
                raise RuntimeError('Native result belongs to another scenario')
            if item['status'] != 'Passed' or verify_result(request, result, case_log, startup) != 'Passed':
                raise RuntimeError('Native case did not pass: ' + name)
            assemblies = read(evidence / 'assemblies.json')
            for assembly, expected in build['assemblies'].items():
                if assemblies[assembly + '/' + assembly + '.dll'] != expected:
                    raise RuntimeError('Different adapter or Lab binary: ' + name)
            for relative, expected in {
                'CombatSolver/CombatSolver.dll': pins['CombatSolver'],
                'HextechRunes/lib/0.111.0/HextechRunes.dll': pins['HextechRunes'],
                'STS2-RitsuLib/compat/0.111.0/STS2-RitsuLib.Runtime.dll': pins['RitsuRuntime'],
            }.items():
                if assemblies.get(relative) != expected:
                    raise RuntimeError('Different dependency binary: ' + relative)
            if read(evidence / 'host.json')['sha256']['sts2'] != pins['sts2']:
                raise RuntimeError('Different game binary')
            expected_base = 'BaseLib@v3.4.7' in fixture['expectedLoadedMods']
            if expected_base != ('BaseLib/BaseLib.dll' in assemblies):
                raise RuntimeError('Unexpected BaseLib composition')
            if expected_base and assemblies['BaseLib/BaseLib.dll'] != '55863ca6adc30a61a7544874e157a519ffe148ccf2b18025947609c4bff813ce':
                raise RuntimeError('Different BaseLib binary')
            if group.startswith('full-mod-'):
                if len(fixture['expectedLoadedMods']) != 12:
                    raise RuntimeError('Incomplete full Mod fixture composition')
                for extra in reviewed_extras:
                    for relative, expected in extra['files'].items():
                        if Path(relative).suffix in {'.dll', '.pck', '.manifest'}:
                            if assemblies.get(extra['mod'] + '/' + relative) != expected:
                                raise RuntimeError('Different extra Mod binary: ' + extra['mod'] + '/' + relative)
            if request.get('exitOnComplete') is False:
                if read(evidence.parent / 'completion.json')['status'] != 'Passed':
                    raise RuntimeError('Native session did not complete successfully')
                if read(evidence.parent / 'build.json') != build:
                    raise RuntimeError('Native session used different build inputs')
            elif read(evidence / 'build.json') != build:
                raise RuntimeError('Cold native case used different build inputs')
            disposition = read(evidence / 'process.json')
            case = public / group / name
            case.mkdir(parents=True, exist_ok=True)
            (case / 'fixture.json').write_text(json.dumps(fixture, ensure_ascii=False, indent=2) + '\n')
            # Only test assertion lines; never copy initialization/auth logs.
            markers = [line for line in case_log.splitlines() if line.startswith('HEXTECH_')
                       and not any(word in line.lower() for word in ('http', 'token=', '/users/', 'password', 'cookie'))]
            (case / 'assertions.txt').write_text('\n'.join(markers) + '\n')
            row = {'group': group, 'fixture': name,
                   'status': 'ExpectedRejectionVerified' if fixture.get('hextechExpectedRejection') else 'NativePassed',
                   'nativeStatus': result['status'],
                   'expectedRejection': fixture.get('hextechExpectedRejection'),
                   'loadedMods': fixture['expectedLoadedMods'],
                   'nativeMilliseconds': result['elapsedMilliseconds'],
                   'nativePid': process['pid'], 'nativeExitCode': disposition.get('exitCode'),
                   'evidence': str(evidence.relative_to(ROOT)),
                   'requestSha256': sha(evidence / 'request.json'),
                   'resultSha256': sha(evidence / 'result.json'),
                   'assertionLines': len(markers)}
            rows.append(row)
    counts = dict(Counter(row['group'] for row in rows))
    if counts != {'dynamic-limits': 8, 'full-mod-normal': 17, 'full-mod-guards': 4, 'no-baselib': 3}:
        raise RuntimeError('Incomplete or unexpected planned batches: ' + str(counts))
    receipt = {'status': 'Passed', 'scope': 'Targeted extended native regression; not the 234-case release matrix',
               'build': build['assemblies'], 'coreDependencies': pins,
               'verifiedCases': len(rows), 'groupCounts': counts,
               'nativePassed': sum(row['status'] == 'NativePassed' for row in rows),
               'expectedRejectionsVerified': sum(row['status'] == 'ExpectedRejectionVerified' for row in rows),
               'nativeCaseMilliseconds': sum(row['nativeMilliseconds'] for row in rows),
               'headlessShutdownNote': 'Exit -6 after completed native result/ready remains a host teardown limitation; it is not a clean process exit or a Steam restart proof.',
               'cases': rows}
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / 'verification.json').write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + '\n')
    sanitized = {**receipt, 'cases': [{k: v for k, v in row.items() if k not in {'nativePid', 'evidence'}} for row in rows]}
    (public / 'verification.json').write_text(json.dumps(sanitized, ensure_ascii=False, indent=2) + '\n')
    print(json.dumps({k: receipt[k] for k in ('status', 'verifiedCases', 'groupCounts', 'nativePassed', 'expectedRejectionsVerified')}))


if __name__ == '__main__':
    main()
