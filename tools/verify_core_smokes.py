#!/usr/bin/env python3
"""Verify separate current-version core-only native checks; never merge with the full matrix."""
import argparse
import hashlib
import json
import os
from pathlib import Path

from build_state import source_inputs
from fixture_evidence import validated_logs, validated_request
from run_fixture import verify_result

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/20261003-release-preparation'
NAMES = ['generated-onplay-regent', 'native-generation-tags-knife-steam-order', 'auto-cross-turn']


def read(path):
    return json.loads(path.read_text())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('report', type=Path)
    args = parser.parse_args()
    build = read(ROOT / '.work/current-build.json')
    assert source_inputs() == build['inputs']
    pins = read(ROOT / 'versions.lock.json')['sha256']
    rows = read(args.report)
    assert [row['fixture'] for row in rows] == NAMES
    verified = []
    for row in rows:
        evidence = Path(row['evidence']).resolve()
        assert evidence.is_relative_to(ROOT) and row['status'] == 'Passed'
        fixture = read(ROOT / '.work/solver0481-core-smokes' / (row['fixture'] + '.json'))
        request = validated_request(evidence, fixture)
        log, startup = validated_logs(evidence, request)
        result = read(evidence / 'result.json')
        assert verify_result(request, result, log, startup) == 'Passed'
        assert len(fixture['expectedLoadedMods']) == 5
        assert read(evidence.parent / 'completion.json')['status'] == 'Passed'
        assert read(evidence.parent / 'build.json') == build
        actual = read(evidence / 'assemblies.json')
        for name, value in build['assemblies'].items():
            assert actual[f'{name}/{name}.dll'] == value
        for path, key in [('CombatSolver/CombatSolver.dll', 'CombatSolver'),
                          ('HextechRunes/lib/0.111.0/HextechRunes.dll', 'HextechRunes'),
                          ('HextechRunes/HextechRunes.dll', 'HextechLoader'),
                          ('HextechRunes/HextechRunes.pck', 'HextechPck'),
                          ('STS2-RitsuLib/compat/0.111.0/STS2-RitsuLib.Runtime.dll', 'RitsuRuntime')]:
            assert actual[path] == pins[key]
        assert not any(path.startswith('BaseLib/') for path in actual)
        assert read(evidence / 'host.json')['sha256']['sts2'] == pins['sts2']
        process = read(evidence / 'process-start.json')
        assert result['processId'] == process['pid']
        assert read(evidence / 'process.json')['exitCode'] is not None
        try:
            os.kill(process['pid'], 0)
        except ProcessLookupError:
            pass
        else:
            raise RuntimeError('Test PID remains running or was reused')
        verified.append({'fixture': row['fixture'], 'status': 'NativePassed',
                         'requestSha256': hashlib.sha256((evidence / 'request.json').read_bytes()).hexdigest(),
                         'resultSha256': hashlib.sha256((evidence / 'result.json').read_bytes()).hexdigest(),
                         'evidence': str(evidence.relative_to(ROOT))})
    receipt = {'status': 'Passed', 'scope': 'Separate core-only 0481/065 baseline without BaseLib; not full matrix or Steam proof',
               'total': len(verified), 'build': build['assemblies'], 'dependencies': pins, 'cases': verified}
    (OUT / 'native-core-smokes-verification.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps({key: receipt[key] for key in ['status', 'scope', 'total']}))


if __name__ == '__main__':
    main()
