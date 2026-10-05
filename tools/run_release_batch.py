#!/usr/bin/env python3
"""Run one bounded release batch and record its actual native report."""
import argparse
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
PREPARATION = ROOT / 'artifacts/20261003-release-preparation'


def main():
    global PREPARATION
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('index', type=int, help='Zero-based normal batch, or 12 for cold guards')
    parser.add_argument('--preparation', type=Path, default=PREPARATION,
                        help='Separate immutable candidate journal; preserve historical releases')
    args = parser.parse_args()
    PREPARATION = args.preparation.resolve()
    plan = json.loads((PREPARATION / 'native-release-plan.json').read_text())
    batches = plan['normalBatches'] + [plan['coldGuards']]
    if args.index < 0 or args.index >= len(batches):
        parser.error('Batch index outside reviewed plan')
    journal = PREPARATION / 'native-batches.json'
    state = json.loads(journal.read_text()) if journal.exists() else {'completed': []}
    if args.index in {row['index'] for row in state['completed']}:
        raise RuntimeError('Batch already recorded; do not repeat successful evidence')
    if set(range(args.index)) - {row['index'] for row in state['completed']}:
        raise RuntimeError('Finish preceding batches first')
    runner = 'run_suite.py' if args.index == len(plan['normalBatches']) else 'run_reused_suite.py'
    command = [sys.executable, str(ROOT / 'tools' / runner), '--fixture-dir',
               str(ROOT / plan['fixtureDir'])]
    if runner == 'run_suite.py':
        command.append('--fail-fast')
    command += batches[args.index]
    monitor_path = PREPARATION / 'monitor.json'
    monitor = json.loads(monitor_path.read_text())
    monitor.update(status='Active', stage=f'native-matrix-batch-{args.index}-active',
                   activeNativeJob=f'Bounded release batch {args.index}; {len(batches[args.index])} fixtures; fail fast with owned host cleanup')
    monitor_path.write_text(json.dumps(monitor, ensure_ascii=False, indent=2) + '\n')
    reports_before = set((ROOT / 'artifacts').glob('*-suite.json'))
    result = subprocess.run(command, cwd=ROOT)
    reports = set((ROOT / 'artifacts').glob('*-suite.json')) - reports_before
    if len(reports) != 1:
        raise RuntimeError('Cannot identify the unique batch report')
    report = reports.pop()
    rows = json.loads(report.read_text())
    passed = result.returncode == 0 and [r['fixture'] for r in rows] == batches[args.index] and all(
        r['status'] == 'Passed' for r in rows)
    record = {'index': args.index, 'report': str(report.relative_to(ROOT)),
              'cases': len(rows), 'status': 'Passed' if passed else 'Failed'}
    if passed:
        state['completed'].append(record)
    else:
        state.setdefault('failures', []).append(record)
    journal.write_text(json.dumps(state, indent=2) + '\n')
    monitor = json.loads(monitor_path.read_text())
    preverified_count = sum(len(json.loads((ROOT / report).read_text()))
                            for report in plan.get('preverifiedReports', []))
    monitor.update(nativeBatchesCompleted=len(state['completed']),
                   nativeCasesReturned=sum(r['cases'] for r in state['completed']) + preverified_count,
                   lastBatch=record, activeNativeJob=None,
                   stage=f'native-matrix-batch-{args.index}-'+('passed' if passed else 'failed'),
                   status='Active' if passed else 'Failed')
    monitor_path.write_text(json.dumps(monitor, ensure_ascii=False, indent=2) + '\n')
    if not passed:
        raise RuntimeError('Affected batch failed; preserve evidence and diagnose before proceeding')


if __name__ == '__main__':
    main()
