#!/usr/bin/env python3
"""Switch only reviewed extra Mod copies in the isolated native test host."""
import argparse
import fcntl
import hashlib
import json
from pathlib import Path
import shutil

from build_state import require_deployed_current
from run_fixture import ROOT


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--composition', choices=('core', 'baselib', 'full'), required=True)
    parser.add_argument('--check', action='store_true', help='Read-only composition and pin check')
    args = parser.parse_args()
    lab = ROOT / '.local/headless-instances/hextech'
    mods = Path(json.loads((lab / 'paths.json').read_text())['mods'])
    if mods.resolve() != (lab / 'SlayTheSpire2.app/Contents/MacOS/mods').resolve():
        raise RuntimeError('Refusing to change a non-Lab directory')
    require_deployed_current(mods)
    receipt = ROOT / 'artifacts/20261003-expanded-compat-tests/lab-full-mod-installation.json'
    reviewed = {row['mod']: row['files'] for row in json.loads(receipt.read_text())}
    folders = {'BaseLib': '3737335127/BaseLib', 'QuickRestart': '3737322022/QuickRestart',
               'MintySpire2': '3737336234/MintySpire2', 'RelicRewardChoices': '3795496596/RelicRewardChoices',
               'ImportVanillaSaves': '3747503308', 'sts2_god_mod': '3747719229', 'RandomForeseer': '3747531952'}
    if set(reviewed) != set(folders):
        raise RuntimeError('Unexpected reviewed extra Mod inventory')
    desired = set(folders) if args.composition == 'full' else {'BaseLib'} if args.composition == 'baselib' else set()
    backup = lab / 'expanded-test-extras-off'
    workshop = Path.home() / 'Library/Application Support/Steam/steamapps/workshop/content/2868840'

    def verify(folder, name):
        # This Mod writes its own runtime log inside its installation folder.
        # Retain it, but compare every other reviewed file byte for byte.
        ignored = {'sts2_god_mod.log', 'sts2_god_mod.previous.log'} if name == 'sts2_god_mod' else set()
        actual = {str(path.relative_to(folder)): hashlib.sha256(path.read_bytes()).hexdigest()
                  for path in folder.rglob('*') if path.is_file() and str(path.relative_to(folder)) not in ignored}
        expected = {path: digest for path, digest in reviewed[name].items() if path not in ignored}
        if actual != expected:
            raise RuntimeError('Extra Mod files differ from reviewed copies: ' + name)

    with (lab / 'fixture.lock').open('w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        # Complete all read-only checks before moving or copying anything.
        sources = {}
        for name, relative in folders.items():
            active, parked = mods / name, backup / name
            if active.exists() and parked.exists():
                raise RuntimeError('Duplicate active/parked extra Mod: ' + name)
            source = active if active.exists() else parked if parked.exists() else workshop / relative
            if not source.is_dir():
                raise RuntimeError('Missing reviewed extra Mod: ' + name)
            verify(source, name)
            sources[name] = source
        if args.check:
            actual = {name for name in folders if (mods / name).exists()}
            if actual != desired:
                raise RuntimeError('Unexpected active test Mod composition')
        else:
            backup.mkdir(exist_ok=True)
            for name, source in sources.items():
                if name in desired:
                    if source == backup / name:
                        source.rename(mods / name)
                    elif source != mods / name:
                        shutil.copytree(source, mods / name)
                elif source == mods / name:
                    source.rename(backup / name)
        print(json.dumps({'status': 'Verified' if args.check else 'Configured',
                          'composition': args.composition, 'extraMods': sorted(desired), 'dailyChanged': False}))


if __name__ == '__main__':
    main()
