#!/usr/bin/env python3
"""Read back the fixed-input native PR search comparison; never benchmark cache hits."""
import hashlib
import json
import os
from pathlib import Path
import re
import statistics
import sys

ROOT = Path(__file__).resolve().parents[1]
PRIVATE = ROOT / '.work/upstream-pr-native'
OUT = ROOT / 'artifacts/20261003-release-preparation'
sys.path.insert(0, str(PRIVATE / 'tools'))
from fixture_evidence import validated_logs, validated_request
from run_fixture import verify_result


def read(path):
    return json.loads(path.read_text())


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    paired_build = read(OUT / 'pr0481-same-build-inputs.json')
    variants = [(label, paired_build[label.split('-')[0]]) for label in
                ['candidate-1', 'baseline-1', 'baseline-2', 'candidate-2']]
    rows = []
    stable_inputs = None
    semantic_fixture = None
    # The source candidate must stand on its own without our equivalent patch.
    entry = PRIVATE / 'src/Entry.cs'
    if 'HandSizeContinuationPatch.Install(harmony);' in entry.read_text():
        raise RuntimeError('Adapter hand-size fix must remain disabled')
    pins = read(ROOT / 'versions.lock.json')['sha256']
    extras = read(ROOT / 'artifacts/20261003-expanded-compat-tests/lab-full-mod-installation.json')
    for label, solver_hash in variants:
        driver = OUT / f'pr-sentinel-{label}-uncached-driver.log'
        text = driver.read_text()
        matches = re.findall(r'^Report: (.+)$', text, flags=re.M)
        if len(matches) != 1 or text.count('PR_SENTINEL_ROUTE_CACHE_CLEARED count=') != 3:
            raise RuntimeError('Missing unique native report or per-request cache clearing')
        report = Path(matches[0]).resolve()
        if not report.is_relative_to(PRIVATE / 'artifacts'):
            raise RuntimeError('Wrong owned PR Lab')
        cases = read(report)
        if [r['fixture'] for r in cases] != ['warmup', 'sample-1', 'sample-2']:
            raise RuntimeError('Incomplete fixed-input comparison')
        for case in cases:
            evidence = Path(case['evidence'])
            session = evidence.parent
            build = read(session / 'build.json')
            if build['inputs']['src/Entry.cs'] != digest(entry):
                raise RuntimeError('Adapter hand-size installation differs')
            comparable_inputs = {k:v for k,v in build['inputs'].items()
                                 if k not in {'src/PinnedAssemblies.g.cs', 'versions.lock.json', 'local.props'}}
            if stable_inputs is None:
                stable_inputs = comparable_inputs
            elif stable_inputs != comparable_inputs:
                raise RuntimeError('Other adapter or Lab source changed between variants')
            fixture = read(PRIVATE / 'tests/pr-sentinel-fixtures' / (case['fixture'] + '.json'))
            if semantic_fixture is None:
                semantic_fixture = fixture
            elif semantic_fixture != fixture:
                raise RuntimeError('Inputs differ between warmup, samples or variants')
            request = validated_request(evidence, fixture)
            result = read(evidence / 'result.json')
            log, startup = validated_logs(evidence, request)
            if case['status'] != 'Passed' or verify_result(request, result, log, startup) != 'Passed':
                raise RuntimeError('Native assertion failed')
            if read(session / 'completion.json')['status'] != 'Passed':
                raise RuntimeError('Incomplete native session')
            assembly = read(evidence / 'assemblies.json')
            expected = {'CombatSolver/CombatSolver.dll': solver_hash,
                        'HextechRunes/lib/0.111.0/HextechRunes.dll': pins['HextechRunes'],
                        'STS2-RitsuLib/compat/0.111.0/STS2-RitsuLib.Runtime.dll': pins['RitsuRuntime']}
            expected.update({f'{name}/{name}.dll': value for name,value in build['assemblies'].items()})
            for extra in extras:
                expected.update({extra['mod']+'/'+path: value for path,value in extra['files'].items()
                                 if Path(path).suffix in {'.dll','.pck','.manifest'}})
            if any(assembly.get(path) != value for path,value in expected.items()):
                raise RuntimeError('Wrong dependency composition')
            if read(evidence / 'host.json')['sha256']['sts2'] != pins['sts2']:
                raise RuntimeError('Wrong game binary')
            process = read(evidence / 'process.json')
            if process['pid'] != result['processId'] or process.get('exitCode') is None:
                raise RuntimeError('Native process not reclaimed')
            try:
                os.kill(process['pid'], 0)
            except ProcessLookupError:
                pass
            else:
                raise RuntimeError('PID still alive or reused; inspect ownership')
            metrics = result['solverMetrics']
            keys = ['totalElapsedMilliseconds','totalExpanded','totalTransitions','totalChoiceBranches',
                    'score','projectedBattleHpLost','potionCount','finalHp','finalEnemyHp','combatEndedTurn',
                    'totalWorkerAllocatedBytes','totalGcPauseMilliseconds','maxParallelConcurrency']
            rows.append({'variant': label, 'fixture': case['fixture'], 'warmup': case['fixture']=='warmup',
                         'metrics': {k:metrics[k] for k in keys}, 'portfolioMembers':metrics['portfolioMembers'],
                         'combatEnded': result['combatEnded'], 'finishedTurn':result['finishedTurn'],
                         'checks':result['completedChecks'], 'nativeExitCode':process['exitCode'],
                         'requestSha256':digest(evidence/'request.json'), 'resultSha256':digest(evidence/'result.json'),
                         'solverSha256':solver_hash, 'build':build['assemblies']})
    measured = [r for r in rows if not r['warmup']]
    quality_keys = ['totalExpanded','totalTransitions','totalChoiceBranches','score','projectedBattleHpLost',
                    'potionCount','finalHp','finalEnemyHp','combatEndedTurn']
    reference = {k:measured[0]['metrics'][k] for k in quality_keys}
    if any({k:r['metrics'][k] for k in quality_keys} != reference or not r['combatEnded']
           or 'UnexpectedReplans:0' not in r['checks'] for r in measured):
        raise RuntimeError('Fixed sentinel work or native route quality differs; inspect before publishing')
    # A2 and B2 also preserve actual last-sample serialized routes for action comparison.
    routes = [read(OUT / f'pr-sentinel-{label}-last-route.json') for label in ['baseline-2','candidate-2']]
    if any(route['WasRestoredFromCache'] for route in routes):
        raise RuntimeError('Timing sample used a saved route')
    if routes[0]['BestNode'] != routes[1]['BestNode']:
        raise RuntimeError('Actual selected actions differ')
    summary = {}
    for variant in ['baseline','candidate']:
        times = [r['metrics']['totalElapsedMilliseconds'] for r in measured if r['variant'].startswith(variant)]
        summary[variant] = {'samples':len(times), 'searchMilliseconds':times,
                            'medianMilliseconds':statistics.median(times), 'minMilliseconds':min(times),
                            'maxMilliseconds':max(times)}
    summary['medianDifferencePercent'] = 100*(summary['candidate']['medianMilliseconds']/summary['baseline']['medianMilliseconds']-1)
    receipt = {'status':'Verified', 'sourceCommit':'2ead87d9c9e35b1588a760efff0bd6154545a77c',
               'pairedBuild':paired_build,
               'order':'candidate-1, baseline-1, baseline-2, candidate-2',
               'adapterHandSizeFixInstalled':False, 'fixture':semantic_fixture,
               'cachePolicy':'Clear owned PR Lab route JSON files before every request; cache-on trial excluded.',
               'warmupPolicy':'First request of each process excluded; two measured requests per process.',
               'quality':reference, 'selectedActions':routes[0]['BestNode'], 'summary':summary, 'cases':rows,
               'performanceGate':'NotPassed' if summary['candidate']['medianMilliseconds'] > summary['baseline']['maxMilliseconds'] else 'NeedsReview',
               'performanceAssessment':'Evidence verification is separate from performance acceptance. Preserve all samples; no general speed or no-regression claim follows from this small sentinel.',
               'limits':'Small fixed native sentinel on macOS arm64; not general performance, FPS or Windows evidence. Headless teardown exit -6 is separate from completed native results.'}
    (OUT/'pr-sentinel-verification.json').write_text(json.dumps(receipt,ensure_ascii=False,indent=2)+'\n')
    print(json.dumps({'status':receipt['status'],'quality':reference,'summary':summary}))


if __name__ == '__main__':
    main()
