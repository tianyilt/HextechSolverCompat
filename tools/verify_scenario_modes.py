#!/usr/bin/env python3
"""Audit scenario label predicates against the reviewed native test source.

Version metadata must not change native dispatch or card/round modes. Four
lexical suffix collisions belong to unrelated, guarded upstream scenarios;
their call paths are checked explicitly rather than renaming valid inputs.
"""
import hashlib
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/20261003-release-preparation'
SOURCE = ROOT / '.work/CombatSolver0481Reviewed/src/Testing'


def main(out=None):
    global OUT
    if out is not None:
        OUT = Path(out)
    plan = json.loads((OUT / 'native-release-plan.json').read_text())
    names = [name for batch in plan['normalBatches'] for name in batch]
    names += plan['coldGuards']
    # Inputs retained from completed sessions still belong to the same matrix.
    for report in plan.get('preverifiedReports', []):
        names += [row['fixture'] for row in json.loads((ROOT / report).read_text())]
    assert len(names) == len(set(names)) == plan['fixtures']
    sources = {p: p.read_text() for p in SOURCE.rglob('*.cs')}
    predicates = sorted(set(
        match for text in sources.values() for match in re.findall(
            r'ScenarioId\.(StartsWith|EndsWith|Contains)\("([^"]+)"', text)))
    executor = (SOURCE / 'Host/UnattendedTestRunner.Executor.cs').read_text()
    lab = (ROOT / 'tests/LabMod/FixtureAssertions.cs').read_text()
    guarded = {
        'ultimate-unstoppable-overflow': ('EndsWith', '-OVERFLOW', 'REPORT-CARDS-',
            'ENERGY-RESET-POWER-ORDER-OVERFLOW', 'AssertEnergyResetPowerOrderAsync'),
        'death-harvest-partial': ('EndsWith', '-PARTIAL', 'REPORT-CARDS-',
            'TURN-SETUP-UI-', None),
        'native-nature-budget': ('EndsWith', 'BUDGET', 'NATIVE-NATURE-',
            'EXECUTION-CHOICE-SETUP-BUDGET', 'hextechNativeNatureProbe'),
        'native-solid-time-replay': ('EndsWith', '-REPLAY', 'NATIVE-SOLID-TIME-',
            'NORMALITY-AUTOPLAY-REPLAY', 'hextechNativeSolidTimeProbe'),
    }
    reviewed = []
    for name in names:
        original = json.loads((ROOT / 'tests/fixtures' / (name + '.json')).read_text())
        current = json.loads((ROOT / plan['fixtureDir'] / (name + '.json')).read_text())
        before, after = original['scenarioId'], current['scenarioId']
        changes = []
        for method, token in predicates:
            evaluate = lambda value: {'StartsWith': value.startswith,
                'EndsWith': value.endswith, 'Contains': value.__contains__}[method](token)
            if evaluate(before) != evaluate(after):
                changes.append((method, token))
        if not changes:
            continue
        if name in {'unknown-rune', 'unknown-enemy'} and current.get('hextechUnknownGuardProbe') in {'relic', 'enemy'}:
            assert changes == [('StartsWith', 'REPORT-ROUND-')]
            assert '__result = VerifyUnknownGuard(scenario, unknownGuard.GetString()!);\n            return false;' in lab
            reviewed.append({'fixture': name, 'lexicalPredicateChange': changes,
                             'reason': 'Explicit native production-capture guard probe bypasses upstream Executor label dispatch'})
            continue
        if name in {'banned-globe', 'banned-solid'} and current.get('hextechUnsupportedManualProbe'):
            assert changes == [('StartsWith', 'REPORT-ROUND-')]
            assert '__result = VerifyUnsupportedManual(scenario, unsupported.GetString()!);\n            return false;' in lab
            reviewed.append({'fixture': name, 'lexicalPredicateChange': changes,
                             'reason': 'Explicit unavailable-stamp, production capture refusal and native manual-turn probe bypasses upstream Executor label dispatch'})
            continue
        assert name in guarded, (name, changes)
        method, token, prefix, guard, route = guarded[name]
        assert changes == [(method, token)] and before.startswith(prefix) and after.startswith(prefix)
        assert guard in executor and (route is None or route in executor or route in lab)
        if name in {'ultimate-unstoppable-overflow', 'death-harvest-partial'}:
            assert 'request.ScenarioId.StartsWith("REPORT-CARDS-", StringComparison.Ordinal)' in executor
            assert 'AssertReportCardSequenceAsync(combatState, player)' in executor
            reason = 'REPORT-CARDS dispatch; changed suffix is only read in an unrelated guarded test method'
        else:
            assert current.get(route) is True and f'TryGetProperty("{route}"' in lab
            # Harmony prefix returns false after dispatching this explicit probe.
            assert ('__result = VerifyNativeNatureTimer(runner, scenario);\n            return false;'
                    if name == 'native-nature-budget' else
                    '__result = VerifyNativeSolidTime(runner, scenario);\n            return false;') in lab
            reason = 'Explicit Lab JSON probe bypasses upstream Executor; its behavior is independent of this suffix'
        reviewed.append({'fixture': name, 'lexicalPredicateChange': [method, token],
                         'unreachableGuard': guard, 'reason': reason})
    assert {row['fixture'] for row in reviewed} - {'unknown-rune', 'unknown-enemy', 'banned-globe', 'banned-solid'} == set(guarded)
    receipt = {'status': 'Passed', 'fixtures': len(names), 'nativePredicates': len(predicates),
               'scope': 'Scenario label routing audit; native behavioral acceptance remains separately required',
               'guardedUnreachableSuffixCollisions': reviewed,
               'sources': {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest()
                           for p in sources if 'ScenarioId' in sources[p]},
               'labDispatchSha256': hashlib.sha256(lab.encode()).hexdigest()}
    (OUT / 'scenario-mode-verification.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps({key: receipt[key] for key in ('status', 'fixtures', 'nativePredicates')}))


if __name__ == '__main__':
    main()
