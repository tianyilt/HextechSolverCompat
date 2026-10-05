#!/usr/bin/env python3
"""Merge only the two approved galvanic sources into the user's ban list."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import tempfile

BANS = ["GlobeHead", "SolidTime"]
DEFAULT = Path.home() / "Library/Application Support/SlayTheSpire2/HextechRunes/rune_config.json"


def apply(path: Path, write: bool):
    original = path.read_bytes()
    before = json.loads(original)
    after = dict(before)
    old_bans = before.get("disabled_monster_hex_ids", [])
    if not isinstance(old_bans, list) or not all(isinstance(x, str) for x in old_bans):
        raise ValueError("disabled_monster_hex_ids must be a string array")
    after["disabled_monster_hex_ids"] = list(dict.fromkeys(old_bans + BANS))
    changed = after != before
    report = {"config": str(path), "changed": changed, "applied": write,
              "before": old_bans, "after": after["disabled_monster_hex_ids"]}
    if write and changed:
        stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S-%f")
        backup = path.with_name(path.name + f".hextech-compat-{stamp}.bak")
        with backup.open("xb") as stream:
            stream.write(original)
        fd, temporary = tempfile.mkstemp(prefix=path.name + ".", dir=path.parent)
        try:
            with os.fdopen(fd, "w") as stream:
                json.dump(after, stream, ensure_ascii=False, indent=2)
                stream.write("\n")
                stream.flush()
                os.fsync(stream.fileno())
            os.chmod(temporary, path.stat().st_mode)
            if path.read_bytes() != original:
                raise RuntimeError("Config changed concurrently; refusing to replace it")
            os.replace(temporary, path)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)
        actual = json.loads(path.read_text())
        if actual != after or backup.read_bytes() != original:
            raise RuntimeError("Config read-back verification failed")
        assert {k: v for k, v in actual.items() if k != "disabled_monster_hex_ids"} == {
            k: v for k, v in before.items() if k != "disabled_monster_hex_ids"}
        report.update(backup=str(backup), beforeSha256=hashlib.sha256(original).hexdigest(),
                      afterSha256=hashlib.sha256(path.read_bytes()).hexdigest(), verified=True)
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, default=DEFAULT)
    parser.add_argument("--apply", action="store_true", help="Without this flag, only preview the merge")
    args = parser.parse_args()
    print(json.dumps(apply(args.config, args.apply), ensure_ascii=False, indent=2))
