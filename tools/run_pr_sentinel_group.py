#!/usr/bin/env python3
"""Run one bounded group on the owned PR Lab, using the paired source builds."""
import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PRIVATE = ROOT / '.work/upstream-pr-native'
OUT = ROOT / 'artifacts/20261003-release-preparation'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('label', choices=['baseline-1','baseline-2','candidate-2'])
    args = parser.parse_args()
    variant = args.label.split('-')[0]
    paired = json.loads((OUT/'pr0481-same-build-inputs.json').read_text())
    folder = ROOT / '.work' / ('CombatSolverHandSizeBaseline0481' if variant=='baseline' else 'CombatSolverHandSizePR0481')
    source = folder / '.godot/mono/temp/bin/Release/CombatSolver.dll'
    if hashlib.sha256(source.read_bytes()).hexdigest() != paired[variant]:
        raise RuntimeError('Paired source binary changed')
    if 'HandSizeContinuationPatch.Install(harmony);' in (PRIVATE/'src/Entry.cs').read_text():
        raise RuntimeError('Adapter hand-size fix is unexpectedly installed')
    journal_path = OUT / 'pr-sentinel-groups.json'
    journal = json.loads(journal_path.read_text()) if journal_path.exists() else {'completed':[]}
    if args.label in {r['label'] for r in journal['completed']}:
        raise RuntimeError('Do not repeat a completed group')
    predecessors = {'baseline-1':set(), 'baseline-2':{'baseline-1'},
                    'candidate-2':{'baseline-1','baseline-2'}}
    if predecessors[args.label] - {r['label'] for r in journal['completed']}:
        raise RuntimeError('A preceding paired group did not complete; preserve and diagnose it first')
    lock_path = PRIVATE/'.local/headless-instances/hextech/fixture.lock'
    paths = json.loads((lock_path.parent/'paths.json').read_text())
    mods = Path(paths['mods'])
    data = Path(paths['data'])
    if not mods.is_relative_to(PRIVATE/'.local') or data != PRIVATE/'.local/headless-instances/hextech/user-data':
        raise RuntimeError('Unexpected owned Lab paths')
    lockfile = PRIVATE/'versions.lock.json'
    dependency = json.loads(lockfile.read_text())
    changed = dependency['sha256']['CombatSolver'] != paired[variant]
    if changed:
        with lock_path.open('w') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            props = PRIVATE/'local.props'
            tree = ET.parse(props)
            tree.getroot().find('PropertyGroup/SolverDir').text = str(source.parent)
            tree.write(props, encoding='unicode')
            dependency['sha256']['CombatSolver'] = paired[variant]
            lockfile.write_text(json.dumps(dependency,ensure_ascii=False,indent=2)+'\n')
            shutil.copy2(source,mods/'CombatSolver/CombatSolver.dll')
        with (OUT/f'pr-sentinel-{args.label}-adapter-build.log').open('w') as stream:
            subprocess.run([sys.executable,str(PRIVATE/'tools/build.py'),'--lab'],
                           stdout=stream,stderr=subprocess.STDOUT,check=True,cwd=ROOT)
    driver = OUT/f'pr-sentinel-{args.label}-uncached-driver.log'
    env = os.environ.copy()
    env['HEXTECH_PR_NO_ROUTE_CACHE'] = '1'
    print('RUN PR GROUP '+args.label,flush=True)
    with driver.open('w') as stream:
        subprocess.run([sys.executable,str(PRIVATE/'tools/run_reused_suite.py'),'--fixture-dir',
                        str(PRIVATE/'tests/pr-sentinel-fixtures'),'warmup','sample-1','sample-2'],
                       stdout=stream,stderr=subprocess.STDOUT,env=env,check=True,cwd=ROOT)
    routes = list((data/'combat-solver-routes').glob('*.json'))
    if len(routes)!=1:
        raise RuntimeError('Cannot identify the final native route')
    route = json.loads(routes[0].read_text())
    if route['WasRestoredFromCache']:
        raise RuntimeError('Final request used route cache')
    shutil.copy2(routes[0],OUT/f'pr-sentinel-{args.label}-last-route.json')
    journal['completed'].append({'label':args.label,'solverSha256':paired[variant],
                                 'driver':driver.name,'nativeRunner':'Passed; strict comparison pending'})
    journal_path.write_text(json.dumps(journal,indent=2)+'\n')
    print('COMPLETED PR GROUP '+args.label,flush=True)


if __name__=='__main__':
    main()
