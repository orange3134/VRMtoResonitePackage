#!/usr/bin/env python3
"""Check decoded packages against explicit, independently chosen expectations.

Exit 0: matched, 1: differences, 2: invalid input. No engine or avatar SDK needed.
"""
import argparse
import json
import math
import re
import sys
from pathlib import Path

RENDERER = "[FrooxEngine]FrooxEngine.SkinnedMeshRenderer"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def load(path):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, "duplicate JSON key: " + key)
            result[key] = value
        return result
    def invalid(value):
        raise ValueError("non-finite JSON number: " + value)
    with Path(path).open(encoding="utf-8-sig") as stream:
        return json.load(stream, object_pairs_hook=unique, parse_constant=invalid)


def keys(value, allowed, required=()):
    require(isinstance(value, dict), "expected an object")
    require(set(value) <= set(allowed), "unknown keys: " + str(set(value) - set(allowed)))
    require(set(required) <= set(value), "missing required keys: " + str(set(required) - set(value)))


def number(value):
    return type(value) in (int, float) and math.isfinite(value)


def integer(value):
    return type(value) is int and value >= 0


def strings(value):
    return isinstance(value, list) and bool(value) and all(isinstance(s, str) for s in value)


def compare(snapshot, expected):
    keys(snapshot, ("format", "schemaVersion", "packageSha256", "slots"),
         ("format", "schemaVersion", "packageSha256", "slots"))
    require(snapshot["format"] == "resopon.package-snapshot" and type(snapshot["schemaVersion"]) is int
            and snapshot["schemaVersion"] == 1, "unsupported snapshot schema")
    require(isinstance(snapshot["packageSha256"], str) and
            re.fullmatch(r"[0-9a-f]{64}", snapshot["packageSha256"]), "invalid package hash")
    slots = snapshot["slots"]
    require(isinstance(slots, list) and slots, "snapshot has no slots")
    seen = {}
    counts = {}
    for slot in slots:
        keys(slot, ("id", "path", "tag", "activeSelf", "activeInHierarchy", "components"),
             ("id", "path", "tag", "activeSelf", "activeInHierarchy", "components"))
        sid = slot["id"]
        require(isinstance(sid, str) and re.fullmatch(r"r(?:/(?:0|[1-9][0-9]*))*", sid)
                and sid not in seen, "invalid or duplicate slot id")
        require(strings(slot["path"]) and isinstance(slot["tag"], str), "invalid path or tag")
        require(type(slot["activeSelf"]) is bool and type(slot["activeInHierarchy"]) is bool,
                "invalid active state")
        parent = seen.get(sid.rsplit("/", 1)[0]) if "/" in sid else None
        require((sid == "r" and len(slot["path"]) == 1) or
                (parent is not None and slot["path"][:-1] == parent["path"]), "invalid slot ancestry/order")
        require(slot["activeInHierarchy"] == (slot["activeSelf"] and
                (parent["activeInHierarchy"] if parent else True)), "inconsistent active hierarchy")
        require(isinstance(slot["components"], list), "invalid components")
        for component in slot["components"]:
            keys(component, ("type", "blendShapeWeights"), ("type", "blendShapeWeights"))
            kind, weights = component["type"], component["blendShapeWeights"]
            require(isinstance(kind, str) and kind, "invalid component type")
            require((kind == RENDERER and isinstance(weights, list) and all(number(w) for w in weights))
                    or (kind != RENDERER and weights is None), "invalid blendshape weights")
            counts[kind] = counts.get(kind, 0) + 1
        seen[sid] = slot
    keys(expected, ("format", "schemaVersion", "componentCounts", "slotRules"), ("format", "schemaVersion"))
    require(expected["format"] == "resopon.package-expectations" and type(expected["schemaVersion"]) is int
            and expected["schemaVersion"] == 1, "unsupported expectation schema")
    limits, rules = expected.get("componentCounts", {}), expected.get("slotRules", [])
    require(isinstance(limits, dict) and isinstance(rules, list) and (limits or rules), "empty expectations")
    differences = []
    def check(ok, message):
        if not ok:
            differences.append(message)
    for kind, limit in limits.items():
        require(isinstance(kind, str) and kind, "invalid expected type")
        keys(limit, ("min", "max"))
        require(limit and all(integer(n) for n in limit.values()), "invalid component count limit")
        require(limit.get("min", 0) <= limit.get("max", math.inf), "reversed component count limit")
        count = counts.get(kind, 0)
        check(limit.get("min", 0) <= count <= limit.get("max", math.inf), f"{kind}: count {count}, expected {limit}")
    for index, rule in enumerate(rules):
        keys(rule, ("selector", "count", "activeSelf", "activeInHierarchy", "weights"), ("selector", "count"))
        require(integer(rule["count"]), "invalid slot count")
        selector = rule["selector"]
        keys(selector, ("pathSuffix", "tag", "componentType"))
        require(selector, "empty selector")
        if "pathSuffix" in selector:
            require(strings(selector["pathSuffix"]), "invalid path suffix")
        for field in ("tag", "componentType"):
            if field in selector:
                require(isinstance(selector[field], str) and selector[field], "invalid selector " + field)
        for field in ("activeSelf", "activeInHierarchy"):
            if field in rule:
                require(type(rule[field]) is bool, "invalid expected active state")
        weights = rule.get("weights", [])
        require(isinstance(weights, list), "invalid weight rules")
        for weight in weights:
            keys(weight, ("componentIndex", "index", "value", "absoluteTolerance"),
                 ("componentIndex", "index", "value"))
            require(integer(weight["componentIndex"]) and integer(weight["index"]), "invalid weight index")
            require(number(weight["value"]) and number(weight.get("absoluteTolerance", 0))
                    and weight.get("absoluteTolerance", 0) >= 0, "invalid weight value/tolerance")
        selected = [s for s in slots if
                    ("pathSuffix" not in selector or s["path"][-len(selector["pathSuffix"]):] == selector["pathSuffix"])
                    and ("tag" not in selector or s["tag"] == selector["tag"])
                    and ("componentType" not in selector or any(c["type"] == selector["componentType"] for c in s["components"]))]
        check(len(selected) == rule["count"], f"rule {index}: selected {len(selected)} slots, expected {rule['count']}")
        for slot in selected:
            label = f"rule {index}, {slot['id']} {slot['path']}"
            for field in ("activeSelf", "activeInHierarchy"):
                if field in rule:
                    check(slot[field] == rule[field], f"{label}: {field} = {slot[field]}, expected {rule[field]}")
            renderers = [c for c in slot["components"] if c["type"] == RENDERER]
            for weight in weights:
                ci, wi = weight["componentIndex"], weight["index"]
                if ci >= len(renderers) or wi >= len(renderers[ci]["blendShapeWeights"]):
                    check(False, f"{label}: missing renderer {ci} or weight {wi}")
                else:
                    actual = renderers[ci]["blendShapeWeights"][wi]
                    check(abs(actual - weight["value"]) <= weight.get("absoluteTolerance", 0),
                          f"{label}: renderer {ci} weight {wi} = {actual}, expected {weight['value']}")
    return {"format": "resopon.package-regression", "schemaVersion": 1,
            "packageSha256": snapshot["packageSha256"], "status": "different" if differences else "matched",
            "componentCounts": counts, "differences": differences}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("snapshot", type=Path)
    parser.add_argument("expectations", type=Path)
    parser.add_argument("--report", type=Path, help="new report path; existing files are never overwritten")
    args = parser.parse_args()
    try:
        result = compare(load(args.snapshot), load(args.expectations))
        output = json.dumps(result, ensure_ascii=False, indent=2)
        if args.report:
            with args.report.open("x", encoding="utf-8") as stream:
                stream.write(output + "\n")
        print(output)
        return int(bool(result["differences"]))
    except (ValueError, OSError, TypeError, OverflowError) as error:
        print(str(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
