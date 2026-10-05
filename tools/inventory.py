#!/usr/bin/env python3
"""Inventory the pinned upstream catalogue; never infer compatibility from names.

The method/reference index is a review aid, not a semantic verifier. In particular,
an empty override list does not establish that Harmony patches leave a rune inert.
"""
import collections
import hashlib
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / ".work/HextechUpstreamCurrent/HextechRunes/src"


def code_only(text):
    # Preserve newlines and offsets while excluding comments and literals.
    pattern = r'//[^\n]*|/\*[\s\S]*?\*/|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\''
    return re.sub(pattern, lambda m: re.sub(r"[^\n]", " ", m.group()), text)


def class_index(files):
    result = collections.defaultdict(list)
    for path, text in files.items():
        code = code_only(text)
        for match in re.finditer(r"\bclass\s+(\w+)(?:\s*<[^{}]+?>)?(?:\s*\([^{}]*?\))?(?:\s*:\s*([^\{]+))?\s*\{", code):
            start = match.end()
            end, depth = start, 1
            while end < len(code) and depth:
                depth += (code[end] == "{") - (code[end] == "}")
                end += 1
            if depth:
                raise ValueError(f"Unclosed class {match[1]} in {path}")
            body = code[start:end - 1]
            methods = sorted(set(re.findall(
                r"\boverride\s+(?:async\s+)?[^;{}=]+?\b(\w+)\s*\(", body)))
            result[match[1]].append({
                "file": str(path), "line": text.count("\n", 0, match.start()) + 1,
                "bases": base_names(match[2] or ""),
                "declaredOverrides": methods,
                "savedProperties": sorted(set(re.findall(
                    r"\[SavedProperty\b[^\]]*\]\s*public\s+[\w<>?]+\s+(\w+)", body))),
            })
    return result


def base_names(clause):
    # Generic arguments are not base classes. In particular SpiralForge is a
    # relic through EnchantmentForgeBase<T>, not an EnchantmentModel through T.
    clause = re.split(r"\bwhere\b", clause, maxsplit=1)[0]
    plain, depth = [], 0
    for char in clause:
        if char == "<":
            depth += 1
        elif char == ">":
            depth -= 1
        elif depth == 0:
            plain.append(char)
    if depth != 0:
        raise ValueError(f"Unbalanced generic base clause: {clause}")
    return [part.strip().rsplit(".", 1)[-1] for part in "".join(plain).split(",") if part.strip()]


def inventory():
    files = {p.relative_to(SOURCE): p.read_text() for p in sorted(SOURCE.rglob("*.cs"))}
    classes = class_index(files)
    references_by_name = collections.defaultdict(list)
    for path, text in files.items():
        occurrences = collections.defaultdict(list)
        for line_number, line in enumerate(code_only(text).splitlines(), 1):
            for name in set(re.findall(r"\b\w+\b", line)) & classes.keys():
                occurrences[name].append(line_number)
        for name, lines in occurrences.items():
            references_by_name[name].append({"file": str(path), "lines": lines})
    coverage = json.loads((ROOT / "coverage.json").read_text())
    players = {x["type"]: x for x in coverage["playerRunes"]}
    enemies = {x["id"]: x for x in coverage["enemyHexes"]}
    bans = {x["id"]: x for x in coverage["bannedEnemyHexes"]}
    reviewed_models = {x["type"]: x for x in coverage.get("dependentModels", [])}

    def describe(name):
        if name not in classes:
            raise ValueError(f"Registered content has no class definition: {name}")
        definitions = classes[name]
        declared_files = {x["file"] for x in definitions}
        references = []
        # Include every source area: implementations can hide outside Hooks/.
        for ref in references_by_name[name]:
            p = Path(ref["file"])
            if str(p) in declared_files or p.parts[0] in {"Content", "Config"}:
                continue
            references.append(ref)
        return {"definitions": definitions, "externalReferences": references}

    source = files[Path("Content/HextechPlayerRuneRegistry.cs")]
    runes = []
    for match in re.finditer(r"\bRune<(\w+)>\(([^\n]+)\)", source):
        name, args = match.groups()
        if name == "TRune":
            continue
        declaration = players.get(name)
        runes.append({
            "type": name, "rarity": re.search(r"HextechRarityTier\.(\w+)", args)[1],
            "upstreamDisabled": "PlayerRuneFlags.Disabled" in args,
            "selectionExcluded": "PlayerRuneFlags.SelectionExcluded" in args,
            "registrationLine": source.count("\n", 0, match.start()) + 1,
            "adapterStatus": declaration["status"] if declaration else "pending",
            "tests": declaration.get("tests", []) if declaration else [],
            **describe(name),
        })
    source = files[Path("Content/HextechMonsterHexRegistry.cs")]
    hexes = []
    for match in re.finditer(r"\bMonster<(\w+)>\(MonsterHexKind\.(\w+),\s*([^\n]+)\)", source):
        name, kind, args = match.groups()
        declaration = enemies.get(kind) or bans.get(kind)
        hexes.append({
            "id": kind, "relicType": name, "upstreamDisabled": "disabled: true" in args,
            "registrationLine": source.count("\n", 0, match.start()) + 1,
            "adapterStatus": declaration["status"] if declaration else "pending",
            "tests": declaration.get("tests", []) if declaration else [],
            "effectDefinitions": classes.get(kind + "EnemyHex", []),
        })
    for entries, key in [(runes, "type"), (hexes, "id")]:
        assert len(entries) == len({x[key] for x in entries}), "Duplicate registration"
    assert set(players) <= {x["type"] for x in runes}, "Coverage includes unknown rune"
    assert set(enemies) | set(bans) <= {x["id"] for x in hexes}, "Coverage includes unknown hex"

    dependent_models = []
    model_bases = {"PowerModel", "CardModel", "EnchantmentModel", "AfflictionModel", "RelicModel"}

    def model_kind(name, seen=None):
        seen = set() if seen is None else seen
        if name in model_bases:
            return name
        if name in seen:
            return None
        seen.add(name)
        for d in classes.get(name, []):
            for b in d["bases"]:
                kind = model_kind(b, seen)
                if kind:
                    return kind
        return None

    registered = {x["type"] for x in runes}
    for name in sorted(classes):
        kind = model_kind(name)
        if kind and name not in registered:
            dependent_models.append({"type": name, "kind": kind,
                                     "adapterStatus": reviewed_models.get(name, {}).get("status", "pending-review"),
                                     "review": reviewed_models.get(name), "definitions": classes[name]})
    result = {
        "schemaVersion": 1,
        "scope": "All upstream registered single-player Hextech content; only two user-approved enemy bans",
        "warning": "Static inventory is not runtime verification; compiled-unverified is not supported.",
        "sourceCommit": json.loads((ROOT / "versions.lock.json").read_text())["sources"]["HextechRunes"]["commit"],
        "sourceSha256": hashlib.sha256("".join(str(p) + "\n" + t for p, t in files.items()).encode()).hexdigest(),
        "counts": {"registeredPlayerRunes": len(runes), "selectablePlayerRunes": sum(
            not x["upstreamDisabled"] and not x["selectionExcluded"] for x in runes),
            "registeredEnemyHexes": len(hexes), "enabledEnemyHexes": sum(not x["upstreamDisabled"] for x in hexes),
            "userBannedEnemyHexes": len(bans), "otherModelClassesIncludingBases": len(dependent_models)},
        "playerRunes": runes, "enemyHexes": hexes, "dependentModels": dependent_models,
    }
    target = ROOT / "docs/content-inventory.json"
    target.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps(result["counts"], ensure_ascii=False))
    return result


if __name__ == "__main__":
    inventory()
