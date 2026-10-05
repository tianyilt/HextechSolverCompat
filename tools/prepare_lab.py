#!/usr/bin/env python3
"""Create an APFS game clone with its own Godot user directory, without changing HOME."""
import json
import os
import pathlib
import shutil
import subprocess
import argparse

ROOT = pathlib.Path(__file__).resolve().parents[1]
STEAM = pathlib.Path.home() / "Library/Application Support/Steam/steamapps"
LAB = ROOT / ".local/headless-instances/hextech"
APP = LAB / "SlayTheSpire2.app"
MACOS = APP / "Contents/MacOS"
DATA_NAME = "HextechSolverCompat/isolated-lab"
NATIVE_DATA = pathlib.Path.home() / "Library/Application Support" / DATA_NAME
DATA = LAB / "user-data"


def prepare(visible=False):
    global LAB, APP, MACOS, DATA_NAME, NATIVE_DATA, DATA
    if visible:
        LAB = ROOT / ".local/visible-instances/hextech"
        APP = LAB / "SlayTheSpire2.app"
        MACOS = APP / "Contents/MacOS"
        DATA_NAME = "HextechSolverCompat/visible-lab"
        NATIVE_DATA = pathlib.Path.home() / "Library/Application Support" / DATA_NAME
        DATA = NATIVE_DATA
    LAB.mkdir(parents=True, exist_ok=True)
    if not APP.exists():
        subprocess.run(["cp", "-cR", str(STEAM / "common/Slay the Spire 2/SlayTheSpire2.app"), str(APP)], check=True)
    # Exported Godot reads this beside the executable. The probe below must
    # confirm the path before the game's main scene is ever started.
    NATIVE_DATA.mkdir(parents=True, exist_ok=True)
    # This native directory already exists and is read only in the restricted
    # runner. All managed user:// I/O is relocated below before the game starts.
    sentry_stub = LAB / "disabled-telemetry.gd"
    sentry_stub.write_text("extends Node\n")
    (MACOS / "override.cfg").write_text(
        '[application]\nconfig/use_custom_user_dir=true\n'
        f'config/custom_user_dir_name="{DATA_NAME}"\n'
        f'[autoload]\nSentryBootstrap="*{sentry_stub}"\n', encoding="utf-8")
    probe = LAB / "probe.gd"
    probe.write_text('extends SceneTree\nfunc _init():\n\tprint("COMPAT_USER_DIR=" + OS.get_user_data_dir())\n\tquit()\n')
    binary = MACOS / "Slay the Spire 2"
    with (LAB / "isolation-probe.log").open("w") as log:
        result = subprocess.run([str(binary), "--headless", "--force-steam=off",
                                 "--log-file", str(LAB / "native-probe-godot.log"), "--script", str(probe)],
                                cwd=MACOS, stdout=log, stderr=subprocess.STDOUT, timeout=30)
    if result.returncode or f"COMPAT_USER_DIR={NATIVE_DATA}" not in (LAB / "isolation-probe.log").read_text():
        raise RuntimeError(f"Isolation probe failed; see {LAB / 'isolation-probe.log'}")
    env = os.environ.copy()
    env.update(DOTNET_CLI_HOME=str(ROOT / ".work/dotnet-home"), DOTNET_CLI_TELEMETRY_OPTOUT="1",
               DOTNET_GENERATE_ASPNET_CERTIFICATE="false")
    sdk = ROOT / ".work/dotnet/dotnet"
    redirector = ROOT / "tools/LabPathRedirector"
    if not visible:
        subprocess.run([str(sdk), "build", str(redirector / "LabPathRedirector.csproj"), "-c", "Release",
                        "--nologo", f"-p:UserProfile={pathlib.Path.home()}", "-p:NuGetAudit=false"], env=env, check=True)
        subprocess.run([str(sdk), str(redirector / "bin/Release/net9.0/LabPathRedirector.dll"),
                        str(APP / "Contents/Resources/data_sts2_macos_arm64/GodotSharp.dll"), str(DATA)], check=True)
    mods = MACOS / "mods"
    mods.mkdir(exist_ok=True)
    workshop = STEAM / "workshop/content/2868840"
    for folder, source in [("CombatSolver", workshop / "3790899961"),
                           ("STS2-RitsuLib", workshop / "3747602295")]:
        target = mods / folder
        shutil.copytree(source, target, dirs_exist_ok=True)
        manifest = target / "mod_manifest.json"
        if manifest.exists():
            manifest.rename(target / f"{folder}.json")
    hexmod = mods / "HextechRunes"
    official = workshop / "3747501308"
    if not (official / "hextech-runes-variants.manifest").is_file():
        raise RuntimeError("Download the official Hextech workshop bundle before preparing the lab.")
    # Preserve the official loader, versioned assemblies and imported textures.
    # No source-built stand-in or raw-asset test pack is used in current runs.
    shutil.copytree(official, hexmod, dirs_exist_ok=True)
    if visible:
        target = mods / "HextechSolverCompat"
        target.mkdir(exist_ok=True)
        shutil.copy2(ROOT / "bin/Release/net9.0/HextechSolverCompat.dll", target)
        shutil.copy2(ROOT / "HextechSolverCompat.json", target)
    settings = DATA / "default/1/settings.save"
    settings.parent.mkdir(parents=True, exist_ok=True)
    # Re-preparing a profile must retain its window, accessibility and other
    # preferences, including the progress imported for visible testing.
    settings_values = json.loads(settings.read_text()) if settings.exists() else {}
    settings_values.setdefault("mod_settings", {})["mods_enabled"] = True
    settings_values["mod_settings"].setdefault("mod_list", [])
    settings.write_text(json.dumps(settings_values, ensure_ascii=False, indent=2))
    config = DATA / "HextechRunes/rune_config.json"
    config.parent.mkdir(parents=True, exist_ok=True)
    live = pathlib.Path.home() / "Library/Application Support/SlayTheSpire2/HextechRunes/rune_config.json"
    values = json.loads(live.read_text()) if live.exists() else {}
    values["disabled_monster_hex_ids"] = list(dict.fromkeys(
        values.get("disabled_monster_hex_ids", []) + ["GlobeHead", "SolidTime"]))
    config.write_text(json.dumps(values, ensure_ascii=False, indent=2) + "\n")
    (LAB / "paths.json").write_text(json.dumps({"binary": str(binary), "data": str(DATA), "mods": str(mods)}, indent=2))
    print(f"Native profile probe verified: {NATIVE_DATA}\nManaged lab data configured: {DATA}\nGame clone: {APP}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--visible", action="store_true", help="Use stock managed game libraries and a separate native profile; no fixture mod.")
    prepare(parser.parse_args().visible)
