"""Bind native test evidence to a successful build of the current C# inputs."""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / '.work/current-build.json'


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def source_inputs():
    paths = list((ROOT / 'src').rglob('*.cs'))
    paths += [p for p in (ROOT / 'tests/LabMod').rglob('*.cs')
              if not {'bin', 'obj'}.intersection(p.relative_to(ROOT).parts)]
    paths += list(ROOT.glob('*.props')) + list(ROOT.glob('*.csproj'))
    paths += list((ROOT / 'tests/LabMod').glob('*.csproj'))
    paths += [ROOT / 'versions.lock.json', ROOT / 'HextechSolverCompat.json',
              ROOT / 'tests/LabMod/HextechCompatLab.json']
    return {str(p.relative_to(ROOT)): digest(p) for p in sorted(set(paths))}


def record_build(inputs):
    if source_inputs() != inputs:
        raise RuntimeError('Build inputs changed during compilation; rebuild before deployment or tests')
    assemblies = {name: digest(folder / 'bin/Release/net9.0' / (name + '.dll'))
                  for name, folder in [('HextechSolverCompat', ROOT), ('HextechCompatLab', ROOT / 'tests/LabMod')]}
    manifest = {'schemaVersion': 1, 'inputs': inputs, 'assemblies': assemblies}
    temporary = MANIFEST.with_suffix('.tmp')
    temporary.write_text(json.dumps(manifest, indent=2) + '\n')
    temporary.replace(MANIFEST)
    return manifest


def require_deployed_current(mods):
    if not MANIFEST.is_file():
        raise RuntimeError('No successful build manifest; run tools/build.py --lab first')
    manifest = json.loads(MANIFEST.read_text())
    if source_inputs() != manifest['inputs']:
        raise RuntimeError('C# sources differ from the last successful build; refusing tests of stale DLLs')
    for name, expected in manifest['assemblies'].items():
        actual = Path(mods) / name / (name + '.dll')
        if not actual.is_file() or digest(actual) != expected:
            raise RuntimeError('Isolated deployed DLL differs from the successful build: ' + name)
    return manifest
