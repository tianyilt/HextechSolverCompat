#!/usr/bin/env python3
"""Prepare pinned sources, a local SDK, and references from the installed Mac game."""
import hashlib
import json
from pathlib import Path
import subprocess
import shutil
import tarfile
import urllib.request
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
WORK = ROOT / ".work"
SDK_VERSION = "9.0.318"


def main():
    WORK.mkdir(exist_ok=True)
    lock = json.loads((ROOT / "versions.lock.json").read_text())
    steam = Path.home() / "Library/Application Support/Steam/steamapps"
    game = steam / "common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64"
    solver = steam / "workshop/content/2868840/3790899961"
    official = steam / "workshop/content/2868840/3747501308"
    selected_hex = official / "lib" / lock["game"] / "HextechRunes.dll"
    for dependency in [game / "sts2.dll", solver / "CombatSolver.dll", selected_hex]:
        if not dependency.is_file():
            raise FileNotFoundError(f"Install the reviewed game and workshop dependencies first: {dependency}")
    for name, dependency in [("sts2", game / "sts2.dll"), ("CombatSolver", solver / "CombatSolver.dll"),
                             ("HextechRunes", selected_hex), ("HextechLoader", official / "HextechRunes.dll"),
                             ("HextechPck", official / "HextechRunes.pck")]:
        if hashlib.sha256(dependency.read_bytes()).hexdigest() != lock["sha256"][name]:
            raise RuntimeError(f"{name} differs from the reviewed lock; review the update before bootstrapping")
    sources = {
        "CombatSolverReviewedSource": lock["sources"]["CombatSolver"],
        "HextechUpstreamCurrent": lock["sources"]["HextechRunes"],
    }
    for folder, pin in sources.items():
        url, revision = pin["url"] + ".git", pin["commit"]
        destination = WORK / folder
        if not destination.exists():
            subprocess.run(["git", "clone", "--filter=blob:none", "--no-checkout", url, str(destination)], check=True)
            subprocess.run(["git", "-C", str(destination), "fetch", "--depth=1", "origin", revision], check=True)
            subprocess.run(["git", "-C", str(destination), "checkout", "--detach", revision], check=True)
        current = subprocess.check_output(["git", "-C", str(destination), "rev-parse", "HEAD"], text=True).strip()
        if current != revision:
            raise RuntimeError(f"{folder} is at another revision; preserve it and use a fresh workspace")
    sdk = WORK / "dotnet/dotnet"
    if not sdk.exists():
        metadata_url = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/9.0/releases.json"
        with urllib.request.urlopen(metadata_url, timeout=30) as response:
            releases = json.load(response)
        package = next(f for r in releases["releases"] for s in r.get("sdks", [])
                       if s["version"] == SDK_VERSION for f in s["files"] if f["rid"] == "osx-arm64")
        archive = WORK / "dotnet-sdk.tar.gz"
        subprocess.run(["curl", "--fail", "--location", "--retry", "2", "--max-time", "240",
                        package["url"], "--output", str(archive)], check=True)
        if hashlib.sha512(archive.read_bytes()).hexdigest().lower() != package["hash"].lower():
            raise RuntimeError("Official SDK SHA512 mismatch")
        sdk.parent.mkdir(exist_ok=True)
        with tarfile.open(archive) as tar:
            tar.extractall(sdk.parent, filter="data")
    if subprocess.check_output([str(sdk), "--version"], text=True).strip() != SDK_VERSION:
        raise RuntimeError("Unexpected SDK version")
    source = WORK / "HextechUpstreamCurrent/HextechRunes"
    output = WORK / "hex-build-097-current"
    subprocess.run([str(sdk), "build", str(source / "src/HextechRunes.csproj"), "-c", "Release",
                    "-p:ImportDirectoryBuildProps=false",
                    f"-p:HextechSts2Target={lock['game']}", f"-p:GameDataDir={game}", "-o", str(output)],
                   check=True, timeout=120)
    # Build source for review, but reference the exact official binaries that
    # were compared with it. A source build is not an official workshop pin.
    refs = {}
    for name, original in [("CombatSolver", solver / "CombatSolver.dll"), ("HextechRunes", selected_hex)]:
        directory = WORK / "pinned-dependencies" / (name + "-" + lock["sha256"][name][:16])
        directory.mkdir(parents=True, exist_ok=True)
        target = directory / (name + ".dll")
        if target.exists() and hashlib.sha256(target.read_bytes()).hexdigest() != lock["sha256"][name]:
            raise RuntimeError(f"Existing {name} pin is corrupted; preserving it for inspection")
        if not target.exists():
            shutil.copy2(original, target)
        if hashlib.sha256(target.read_bytes()).hexdigest() != lock["sha256"][name]:
            raise RuntimeError(f"{name} pin copy did not match")
        refs[name] = directory
    # Existing local overrides belong to the developer; do not replace them.
    local = ROOT / "local.props"
    if not local.exists():
        project = ET.Element("Project")
        group = ET.SubElement(project, "PropertyGroup")
        for name, value in [("GameDataDir", game), ("SolverDir", refs["CombatSolver"]), ("HextechDir", refs["HextechRunes"])]:
            ET.SubElement(group, name).text = str(value)
        ET.ElementTree(project).write(local, encoding="unicode")
    print("Dependencies ready. Next: python3 tools/prepare_lab.py && python3 tools/build.py --lab")


if __name__ == "__main__":
    main()
