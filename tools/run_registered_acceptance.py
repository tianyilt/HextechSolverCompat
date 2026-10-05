#!/usr/bin/env python3
"""Four bounded native batches with monitored completion and first-failure stop."""
import json
from pathlib import Path
import signal
import subprocess
import sys
import time

from build_state import source_inputs

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/20261004-registered-runes'


def write(path, value):
    temporary = path.with_suffix('.acceptance-tmp')
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n')
    temporary.replace(path)


def main():
    plan_path = OUT / 'verification-plan.json'
    plan = json.loads(plan_path.read_text())
    build = json.loads((ROOT / '.work/current-build.json').read_text())
    if source_inputs() != build['inputs']:
        raise RuntimeError('Current sources differ from the built candidate')
    queue = [(group, group['fixtures'][i:i+20]) for group in plan['groups']
             if group['name'] != 'combination-boundaries' for i in range(0, len(group['fixtures']), 20)]
    state = {'status': 'Running', 'build': build['assemblies'], 'startedAt': time.time(),
             'expectedBatches': len(queue), 'completed': [],
             'done': 'All planned native cases strictly passed, owned processes reclaimed, verifier accepted exact build',
             'failed': 'Any native or assertion failure, timeout, changed source/build or missing completed report'}
    try:
        for group, names in queue:
            if source_inputs() != build['inputs'] or json.loads((ROOT / '.work/current-build.json').read_text()) != build:
                raise RuntimeError('Candidate changed during final acceptance')
            state['current'] = {'group': group['name'], 'fixtures': names, 'startedAt': time.time()}
            write(OUT / 'acceptance-monitor.json', state)
            cmd = [sys.executable, str(ROOT / 'tools/run_reused_suite.py'), '--fixture-dir', group['fixtureDir'], *names]
            process = subprocess.Popen(cmd, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
            try:
                stdout, _ = process.communicate(timeout=1250)
            except (subprocess.TimeoutExpired, KeyboardInterrupt):
                process.send_signal(signal.SIGINT)
                try:
                    stdout, _ = process.communicate(timeout=15)
                except subprocess.TimeoutExpired:
                    # A stuck runner must be inspected, not SIGKILLed while it
                    # may still own a native host.
                    raise RuntimeError('Runner cleanup did not finish; inspect owned process')
                raise RuntimeError('Bounded batch timed out or was interrupted')
            print(stdout, end='', flush=True)
            if process.returncode != 0:
                raise RuntimeError('Native batch failed; evidence retained')
            reports = [line.removeprefix('Report: ') for line in stdout.splitlines() if line.startswith('Report: ')]
            if len(reports) != 1:
                raise RuntimeError('Missing unique native report')
            report = Path(reports[0]).resolve()
            if not report.is_relative_to(ROOT / 'artifacts'):
                raise RuntimeError('Native report outside workspace')
            rows = json.loads(report.read_text())
            if [row['fixture'] for row in rows] != names or any(row['status'] != 'Passed' for row in rows):
                raise RuntimeError('Actual report differs from the planned batch')
            group['reports'].append(str(report.relative_to(ROOT)))
            write(plan_path, plan)
            state['completed'].append({'group': group['name'], 'total': len(names), 'report': str(report.relative_to(ROOT))})
            write(OUT / 'acceptance-monitor.json', state)
        subprocess.run([sys.executable, str(ROOT / 'tools/verify_registered_runes.py')], cwd=ROOT, check=True)
        state['status'] = 'Passed'
        state.pop('current', None)
    except Exception as error:
        state['status'] = 'Failed'
        state['error'] = str(error)
        raise
    finally:
        state['checkedAt'] = time.time()
        write(OUT / 'acceptance-monitor.json', state)


if __name__ == '__main__':
    main()
