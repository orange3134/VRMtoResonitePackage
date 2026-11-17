#!/usr/bin/env python3
"""Compare matching Unity reference projections offline (authored v1 or resolved v2).

Exit 0: equal, 1: differences, 2: invalid input or invocation. No Unity or SDK
is needed. Collection records are matched by id, never by display names.
"""
import argparse
import json
import math
import re
import sys
from pathlib import Path

FORMAT = "resopon.unity-reference"
SECTIONS = ("renderers", "physBones", "colliders")
PROJECTION = "selected-prefab-authored-components"


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON object key: " + key)
        result[key] = value
    return result


def load_snapshot(path):
    def reject_constant(value):
        raise ValueError("non-finite JSON number: " + value)
    with Path(path).open(encoding="utf-8-sig") as stream:
        data = json.load(stream, object_pairs_hook=_unique_object,
                         parse_constant=reject_constant)
    def finite_tree(value):
        if isinstance(value, float) and not math.isfinite(value):
            raise ValueError("non-finite JSON number")
        if isinstance(value, dict):
            for child in value.values():
                finite_tree(child)
        elif isinstance(value, list):
            for child in value:
                finite_tree(child)
    finite_tree(data)
    validate_snapshot(data)
    return data


def _file_id(value, allow_zero=True):
    if not isinstance(value, str) or not re.fullmatch(r"-?(0|[1-9][0-9]*)", value):
        return False
    number = int(value)
    return -(2 ** 63) <= number < 2 ** 63 and (allow_zero or number != 0)


def _asset_ref(value):
    return (isinstance(value, dict) and isinstance(value.get("guid"), str)
            and _file_id(value.get("fileId")))


def validate_snapshot(data):
    if not isinstance(data, dict) or data.get("format") != FORMAT:
        raise ValueError("expected format " + FORMAT)
    if type(data.get("schemaVersion")) is int and data["schemaVersion"] == 2:
        _validate_resolved(data)
        return
    if type(data.get("schemaVersion")) is not int or data["schemaVersion"] != 1:
        raise ValueError("unsupported schemaVersion (expected integer 1 or 2)")
    if not isinstance(data.get("prefabGuid"), str) or not re.fullmatch(r"[0-9a-fA-F]{32}", data["prefabGuid"]):
        raise ValueError("prefabGuid must be a 32-character hexadecimal GUID")
    if data.get("projection") != PROJECTION:
        raise ValueError("expected projection " + PROJECTION)
    sections = data.get("sections")
    if not isinstance(sections, dict) or set(sections) != set(SECTIONS):
        raise ValueError("sections must contain exactly " + ", ".join(SECTIONS))
    if all(records == [] for records in sections.values()):
        raise ValueError("capture contains no comparable component records")
    for name, records in sections.items():
        if not isinstance(records, list):
            raise ValueError(name + " must be an array")
        seen = set()
        for record in records:
            if not isinstance(record, dict):
                raise ValueError(name + " records must be objects")
            identity = record.get("id")
            if not _file_id(identity, allow_zero=False):
                raise ValueError(name + " record id must be a nonzero signed 64-bit fileID string")
            if identity in seen:
                raise ValueError(name + " duplicate id: " + identity)
            seen.add(identity)
            for field in ("path", "type"):
                if not isinstance(record.get(field), str):
                    raise ValueError(name + " " + identity + ": " + field + " must be a string")
            for field in ("enabled", "activeInHierarchy"):
                if type(record.get(field)) is not bool:
                    raise ValueError(name + " " + identity + ": " + field + " must be a boolean")
            if name == "renderers":
                if type(record.get("activeSelf")) is not bool:
                    raise ValueError("renderer activeSelf must be a boolean")
                materials = record.get("materials")
                if not isinstance(materials, list):
                    raise ValueError("renderer materials must be an array")
                mesh = record.get("mesh")
                if not _asset_ref(mesh):
                    raise ValueError("renderer mesh guid and fileId must be strings")
                for material in materials:
                    if not _asset_ref(material):
                        raise ValueError("material guid and fileId must be strings")
            else:
                if not _file_id(record.get("root"), allow_zero=False):
                    raise ValueError(name + " root must be a nonzero signed 64-bit fileID string")
            if name == "physBones":
                if not isinstance(record.get("colliders"), list) or any(x != "" and not _file_id(x, allow_zero=False) for x in record["colliders"]):
                    raise ValueError("PhysBone colliders must be a string array")


def _validate_resolved(data):
    if data.get("projection") != "unity-resolved-prefab":
        raise ValueError("expected resolved projection")
    if not isinstance(data.get("prefabGuid"), str) or not re.fullmatch(r"[0-9a-fA-F]{32}", data["prefabGuid"]):
        raise ValueError("invalid prefabGuid")
    sections = data.get("sections")
    if not isinstance(sections, dict) or set(sections) != {"transforms", *SECTIONS}:
        raise ValueError("invalid resolved sections")
    identities = set()
    maps = {}
    for name, records in sections.items():
        if not isinstance(records, list):
            raise ValueError(name + " must be an array")
        maps[name] = {}
        for record in records:
            if not isinstance(record, dict):
                raise ValueError("record must be an object")
            identity = record.get("id")
            pattern = r"r(?:/[0-9]+)*" if name == "transforms" else r"r(?:/[0-9]+)*/c:[^/]+:[0-9]+"
            if not isinstance(identity, str) or not re.fullmatch(pattern, identity) or identity in identities:
                raise ValueError("invalid or duplicate resolved identity")
            identities.add(identity)
            maps[name][identity] = record
            for field in ("path", "type"):
                if not isinstance(record.get(field), str):
                    raise ValueError("missing " + field)
            for field in ("source", "originalSource"):
                if not _asset_ref(record.get(field)):
                    raise ValueError("invalid source identity")
            if type(record.get("activeInHierarchy")) is not bool:
                raise ValueError("invalid activeInHierarchy")
            if name != "transforms" and type(record.get("enabled")) is not bool:
                raise ValueError("invalid enabled")
    transforms = maps["transforms"]
    if "r" not in transforms or not any(maps[name] for name in SECTIONS):
        raise ValueError("empty resolved capture")
    def ref(value, allowed, nullable=False):
        if not isinstance(value, str) or (value not in allowed and not (nullable and value == "")):
            raise ValueError("dangling resolved reference: " + str(value))
    def numbers(value, keys):
        if not isinstance(value, dict) or set(value) != set(keys) or any(type(v) not in (int, float) or not math.isfinite(v) for v in value.values()):
            raise ValueError("invalid transform vector")
    for identity, record in transforms.items():
        parent = "" if identity == "r" else identity.rsplit("/", 1)[0]
        if record.get("parent") != parent:
            raise ValueError("invalid hierarchy parent")
        ref(parent, transforms, nullable=True)
        if not isinstance(record.get("name"), str) or type(record.get("activeSelf")) is not bool:
            raise ValueError("invalid transform state")
        index = 0 if identity == "r" else int(identity.rsplit("/", 1)[1])
        if type(record.get("siblingIndex")) is not int or record["siblingIndex"] != index:
            raise ValueError("invalid sibling index")
        numbers(record.get("localPosition"), "xyz")
        numbers(record.get("localScale"), "xyz")
        numbers(record.get("localRotation"), "xyzw")
    for name in SECTIONS:
        for record in maps[name].values():
            ref(record.get("transform"), transforms)
            if record["id"].split("/c:", 1)[0] != record["transform"]:
                raise ValueError("component owner does not match identity")
            if name == "renderers":
                if type(record.get("activeSelf")) is not bool or not _asset_ref(record.get("mesh")):
                    raise ValueError("invalid renderer state")
                if not isinstance(record.get("materials"), list) or not all(_asset_ref(v) for v in record["materials"]):
                    raise ValueError("invalid renderer materials")
                ref(record.get("rootBone"), transforms, nullable=True)
                if not isinstance(record.get("bones"), list):
                    raise ValueError("invalid bones")
                for bone in record["bones"]:
                    ref(bone, transforms, nullable=True)
                weights = record.get("blendShapes")
                if not isinstance(weights, list) or any(not isinstance(v, dict) or type(v.get("index")) is not int or v["index"] != i or not isinstance(v.get("name"), str) or type(v.get("weight")) not in (int, float) or not math.isfinite(v["weight"]) for i, v in enumerate(weights)):
                    raise ValueError("invalid blendshape weights")
            else:
                ref(record.get("root"), transforms)
                if not isinstance(record.get("colliders"), list):
                    raise ValueError("invalid collider list")
                for collider in record["colliders"]:
                    ref(collider, maps["colliders"], nullable=True)
                parameters = record.get("parameters")
                if not isinstance(parameters, list) or any(not isinstance(v, dict) or not isinstance(v.get("name"), str) or type(v.get("value")) not in (int, float) or not math.isfinite(v["value"]) for v in parameters):
                    raise ValueError("invalid physics parameters")


def compare(expected, actual, abs_tol=1e-6, rel_tol=1e-6):
    """Return deterministic field-level differences. All non-record arrays are ordered."""
    validate_snapshot(expected)
    validate_snapshot(actual)
    if expected["schemaVersion"] != actual["schemaVersion"]:
        raise ValueError("cannot compare authored and resolved projections")
    differences = []
    def walk(left, right, path):
        if isinstance(left, dict) and isinstance(right, dict):
            for key in sorted(left.keys() - right.keys()):
                differences.append(f"{path}.{key}: missing in actual")
            for key in sorted(right.keys() - left.keys()):
                differences.append(f"{path}.{key}: unexpected in actual")
            for key in sorted(left.keys() & right.keys()):
                walk(left[key], right[key], path + "." + key)
        elif isinstance(left, list) and isinstance(right, list):
            if len(left) != len(right):
                differences.append(f"{path}: array length expected {len(left)}, actual {len(right)}")
            for index, (a, b) in enumerate(zip(left, right)):
                walk(a, b, f"{path}[{index}]")
        elif type(left) in (int, float) and type(right) in (int, float):
            if not (math.isfinite(left) and math.isfinite(right)):
                raise ValueError(path + ": non-finite number")
            # Integers represent exact identities/counts; don't apply float tolerance.
            equal = left == right if type(left) is int and type(right) is int else math.isclose(left, right, rel_tol=rel_tol, abs_tol=abs_tol)
            if not equal:
                differences.append(f"{path}: expected {left!r}, actual {right!r}")
        elif type(left) is not type(right) or left != right:
            differences.append(f"{path}: expected {left!r}, actual {right!r}")
    walk({k: v for k, v in expected.items() if k != "sections"},
         {k: v for k, v in actual.items() if k != "sections"}, "$")
    for section in expected["sections"]:
        left = {r["id"]: r for r in expected["sections"][section]}
        right = {r["id"]: r for r in actual["sections"][section]}
        walk(left, right, "$.sections." + section)
    return differences


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("expected", help="Unity-exported reference JSON")
    parser.add_argument("actual", help="matching snapshot JSON (ResoPon v1/v2 or repeated Unity v2)")
    parser.add_argument("--abs-tol", type=float, default=1e-6)
    parser.add_argument("--rel-tol", type=float, default=1e-6)
    parser.add_argument("--report", help="optional JSON comparison report path")
    args = parser.parse_args(argv)
    try:
        if any(not math.isfinite(v) or v < 0 for v in (args.abs_tol, args.rel_tol)):
            raise ValueError("tolerances must be finite and nonnegative")
        expected = load_snapshot(args.expected)
        actual = load_snapshot(args.actual)
        differences = compare(expected, actual, args.abs_tol, args.rel_tol)
        if args.report:
            inputs = {Path(args.expected).resolve(), Path(args.actual).resolve()}
            if Path(args.report).resolve() in inputs:
                raise ValueError("report must not overwrite an input")
            Path(args.report).write_text(json.dumps({"equal": not differences,
                "differences": differences}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        if differences:
            print("FAIL: " + str(len(differences)) + " difference(s)")
            for difference in differences:
                print(difference)
            return 1
        print("PASS: all captured fields match (not a conversion or runtime equivalence test)")
        return 0
    except (OSError, ValueError, OverflowError) as error:
        print("ERROR: " + str(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
