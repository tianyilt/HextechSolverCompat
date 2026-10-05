#!/usr/bin/env python3
"""Remove our disposable isolated instance after validation; keep evidence/source."""
import fcntl
import json
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[1]
LAB = ROOT / ".local/headless-instances/hextech"
APP = LAB / "SlayTheSpire2.app"

if __name__ == "__main__":
    if not LAB.exists():
        print("Isolated instance is already absent.")
        raise SystemExit(0)
    if LAB.is_symlink() or LAB.resolve().parent != (ROOT / ".local/headless-instances").resolve():
        raise RuntimeError("Instance path is not owned by this project")
    with (LAB / "fixture.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        paths = json.loads((LAB / "paths.json").read_text())
        if Path(paths["data"]).resolve() != (LAB / "user-data").resolve():
            raise RuntimeError("Isolated profile marker missing; refusing removal")
        if APP.exists():
            if APP.is_symlink() or APP.resolve().parent != LAB.resolve():
                raise RuntimeError("Clone path is not owned by this project")
            config = (APP / "Contents/MacOS/override.cfg").read_text()
            if 'config/custom_user_dir_name="HextechSolverCompat/isolated-lab"' not in config:
                raise RuntimeError("Isolation marker missing; refusing removal")
        shutil.rmtree(LAB)
        if LAB.exists():
            raise RuntimeError("Isolated instance still exists after cleanup")
        print("Disposable instance removed; source, SDK and artifacts retained. Recreate with tools/prepare_lab.py before testing.")
