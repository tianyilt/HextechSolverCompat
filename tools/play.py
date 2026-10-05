#!/usr/bin/env python3
"""Open the pinned interactive game copy while keeping its existing profile."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def verify():
    receipt = json.loads((ROOT / 'docs/playable-build.json').read_text())
    lab = ROOT / '.local/visible-instances/corrupted-regression'
    paths = json.loads((lab / 'paths.json').read_text())
    app = lab / 'SlayTheSpire2.app'
    mods = Path(paths['mods'])
    expected_data = Path.home() / 'Library/Application Support/HextechSolverCompat/corrupted-regression'
    if paths['data'] != str(expected_data) or not Path(paths['binary']).is_relative_to(app):
        raise RuntimeError('Interactive profile does not match the verified copy')
    if any(mods.rglob('HextechCompatLab.dll')):
        raise RuntimeError('The fixture mod cannot be used for interactive play')
    actual = {str(p.relative_to(app)): hashlib.sha256(p.read_bytes()).hexdigest()
              for p in app.rglob('*.dll') if '/mods/' in str(p)
              or p.name in {'sts2.dll', 'GodotSharp.dll'}}
    if actual != receipt['assemblies']:
        raise RuntimeError('Game or mod files changed; refusing to launch unverified binaries')
    return app, expected_data, receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    app, data, receipt = verify()
    old_game = False
    if receipt.get('previousActiveGamePid'):
        process = subprocess.run(['ps', '-p', str(receipt['previousActiveGamePid']),
                                  '-o', 'lstart='], capture_output=True, text=True)
        old_game = process.returncode == 0 and process.stdout.strip() == receipt.get('previousActiveGameStartedAt')
    if args.check:
        print(json.dumps({'status': 'Verified', 'app': str(app), 'profile': str(data),
                          'adapterSha256': receipt['adapterSha256'], 'steamEnabled': False, 'previousVersionStillRunning': old_game}, ensure_ascii=False, indent=2))
    elif old_game:
        print('当前窗口仍是刚才通关的版本。正常退出游戏后，再双击此入口即可加载修复版；游戏进度会保留。')
    else:
        print('正在打开海克斯与求解器兼容副本，沿用刚才的游戏进度。')
        subprocess.run(['open', '-a', str(app), '--args', '--force-steam=off'], check=True)


if __name__ == '__main__':
    main()
