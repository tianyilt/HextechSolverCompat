#!/usr/bin/env python3
"""Lossless local Steam installation; public-release gates stay independent."""
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import shutil
import subprocess

from build_state import source_inputs
from verify_registered_runes import main as verify_native

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/20261004-registered-runes'


def read(path):
    return json.loads(path.read_text())


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def files(path):
    return {str(f.relative_to(path)): digest(f) for f in path.rglob('*') if f.is_file()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    verify_native()
    proof = read(OUT / 'native-verification.json')
    build = read(ROOT / '.work/current-build.json')
    if (proof['status'] != 'TargetedNativePassed' or proof['total'] != 68
            or len(proof['registeredRuneWitnesses']) != 26
            or proof['build'] != build or source_inputs() != build['inputs']):
        raise RuntimeError('The reviewed local candidate acceptance is incomplete')
    steam = Path.home() / 'Library/Application Support/Steam'
    app = steam / 'steamapps/common/Slay the Spire 2/SlayTheSpire2.app'
    exe = app / 'Contents/MacOS/Slay the Spire 2'
    mods = exe.parent / 'mods'
    target = mods / 'HextechSolverCompat/HextechSolverCompat.dll'
    candidate = ROOT / 'bin/Release/net9.0/HextechSolverCompat.dll'
    previous = read(ROOT / 'artifacts/20261004-warmogs-fix/installation.json')['adapterSha256']
    new = build['assemblies']['HextechSolverCompat']
    if digest(candidate) != new or digest(target) != previous:
        raise RuntimeError('Candidate or actual installed baseline changed')
    if (mods / 'HextechCompatLab').exists():
        raise RuntimeError('Lab must never enter the daily game')
    reference = read(ROOT / proof['cases'][0]['evidence'] / 'assemblies.json')
    # Daily Steam loads seven subscribed Mods directly from Workshop. The
    # isolated host has copies beneath mods/, so its relative paths differ.
    # Resolve the known source layouts and compare their full pinned contents.
    daily_sources = {}
    extras = read(ROOT / 'artifacts/20261003-expanded-compat-tests/lab-full-mod-installation.json')
    for extra in extras:
        choices = [entry / extra['mod'] if (entry / extra['mod']).is_dir() else entry
                   for entry in (steam / 'steamapps/workshop/content/2868840').iterdir()
                   if (entry / extra['mod']).is_dir() or (entry / (extra['mod'] + '.json')).is_file()]
        if len(choices) != 1:
            raise RuntimeError('Daily Workshop source is ambiguous: ' + extra['mod'])
        ignore = {'sts2_god_mod.log', 'sts2_god_mod.previous.log'} if extra['mod'] == 'sts2_god_mod' else set()
        if ({k: v for k, v in files(choices[0]).items() if k not in ignore}
                != {k: v for k, v in extra['files'].items() if k not in ignore}):
            raise RuntimeError('Other daily Mod changed: ' + extra['mod'])
        daily_sources[extra['mod']] = choices[0]
    for relative, expected in reference.items():
        if relative.startswith(('HextechCompatLab/', 'HextechSolverCompat/')):
            continue
        parts = Path(relative).parts
        actual = (daily_sources[parts[0]].joinpath(*parts[1:])
                  if parts[0] in daily_sources else mods / relative)
        if digest(actual) != expected:
            raise RuntimeError('Daily Mod differs from the actual tested composition: ' + relative)
    if digest(app / 'Contents/Resources/data_sts2_macos_arm64/sts2.dll') != proof['dependencies']['sts2']:
        raise RuntimeError('Daily game changed')
    plan = {'status': 'PreparedForLocalInstallation', 'adapterSha256': new,
            'previousAdapterSha256': previous, 'nativeVerificationSha256': digest(OUT / 'native-verification.json'),
            'nativeCases': proof['total'], 'registeredRunesAdded': 26, 'publicRelease': False,
            'policy': 'Only atomic replacement of the compatibility DLL after the daily game closes; no settings, saves, other Mods or subscription changes.'}
    (OUT / 'installation-plan.json').write_text(json.dumps(plan, ensure_ascii=False, indent=2) + '\n')
    if not args.apply:
        print(json.dumps(plan, ensure_ascii=False))
        return

    def require_closed():
        processes = subprocess.check_output(['ps', '-axo', 'pid=,comm='], text=True).splitlines()
        if any(line.strip().endswith(str(exe)) for line in processes):
            raise RuntimeError('Daily game is running; preserve the process and close it normally before installation')

    require_closed()
    data = Path.home() / 'Library/Application Support/SlayTheSpire2'
    before = files(data)
    before_mods = files(mods)
    backup = ROOT / 'artifacts' / (datetime.datetime.now().strftime('%Y%m%d-%H%M%S') + '-registered-steam-update')
    backup.mkdir()
    shutil.copytree(data, backup / 'before-user-data')
    shutil.copy2(target, backup / 'previous-HextechSolverCompat.dll')
    for cache in (steam / 'userdata').glob('*/2868840'):
        shutil.copytree(cache, backup / ('before-steam-cache-' + cache.parent.name))
    if files(backup / 'before-user-data') != before or files(data) != before:
        raise RuntimeError('User data changed while preparing backup')
    require_closed()
    pending = target.with_name(target.name + '.registered-pending')
    if pending.exists():
        raise RuntimeError('Unresolved staged replacement')
    shutil.copy2(candidate, pending)
    if digest(pending) != new:
        raise RuntimeError('Pending copy readback differs')
    pending.replace(target)
    expected = {**before_mods, 'HextechSolverCompat/HextechSolverCompat.dll': new}
    if files(mods) != expected or files(data) != before:
        restore = target.with_name(target.name + '.restore-pending')
        shutil.copy2(backup / 'previous-HextechSolverCompat.dll', restore)
        restore.replace(target)
        raise RuntimeError('Post-install readback differs; compatibility DLL restored')
    receipt = {**plan, 'status': 'InstalledAwaitingSteamAcceptance',
               'backup': str(backup.relative_to(ROOT)), 'userDataFilesBackedUp': len(before),
               'savesModified': False, 'settingsModified': False, 'otherModsModified': False,
               'workshopSubscriptionsChanged': False, 'filesChanged': ['HextechSolverCompat.dll']}
    for path in [OUT / 'installation.json', backup / 'installation.json']:
        path.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + '\n')
    print(json.dumps(receipt, ensure_ascii=False))


if __name__ == '__main__':
    main()
