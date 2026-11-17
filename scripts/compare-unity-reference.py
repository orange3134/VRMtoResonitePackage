#!/usr/bin/env python3
"""Compare Unity-resolved reference JSON with ResoPon's parser snapshot, offline.

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
    if type(data.get("schemaVersion")) is not int or data["schemaVersion"] != 1:
        raise ValueError("unsupported schemaVersion (expected integer 1)")
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


def compare(expected, actual, abs_tol=1e-6, rel_tol=1e-6):
    """Return deterministic field-level differences. All non-record arrays are ordered."""
    validate_snapshot(expected)
    validate_snapshot(actual)
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
    for section in SECTIONS:
        left = {r["id"]: r for r in expected["sections"][section]}
        right = {r["id"]: r for r in actual["sections"][section]}
        walk(left, right, "$.sections." + section)
    return differences


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("expected", help="Unity-exported reference JSON")
    parser.add_argument("actual", help="ResoPon parser snapshot JSON")
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
