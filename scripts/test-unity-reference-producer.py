#!/usr/bin/env python3
"""Integration check using a self-authored Unity fixture and its captured v2 JSON.

Run ResoPonResolvedReferenceSmoke in Unity first. Supply that generated variant.prefab
and first.json. This checks the production parser, including nested occurrences,
against Unity; expectations are never copied into actual values or overwritten.
"""
import argparse
import copy
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("prefab", type=Path)
    parser.add_argument("expected", type=Path)
    parser.add_argument("--reference-dump", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    output = args.output.resolve()
    if root / ".tmp_verify" not in output.parents:
        parser.error("Use a new output directory under .tmp_verify")
    spec = importlib.util.spec_from_file_location("replay", Path(__file__).with_name("test-unity-resolved.py"))
    replay = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(replay)
    comparison = replay.load_comparator()
    expected_path, prefab = args.expected.resolve(), args.prefab.resolve()
    expected = comparison.load_snapshot(expected_path)
    if expected["schemaVersion"] != 2:
        parser.error("Expected v2 Unity smoke reference")
    baseline_hash = replay.sha256(expected_path)
    source_hashes = {p: replay.sha256(p) for p in prefab.parent.rglob("*") if p.is_file()}
    output.mkdir(parents=True, exist_ok=False)
    env = dict(os.environ, RESOPON_NOPAUSE="1", TEMP=str(output), TMP=str(output))
    checks = []

    def run(reference, actual, log_name):
        with (output / log_name).open("xb") as log:
            return subprocess.run(["dotnet", str(args.reference_dump.resolve()), str(prefab), str(reference), str(actual)],
                                  cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=120,
                                  creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)).returncode

    def check(condition, label):
        if not condition:
            raise AssertionError(label)
        checks.append(label)

    actual_path = output / "actual.json"
    check(run(expected_path, actual_path, "capture.log") == 0, "v2 producer succeeds")
    actual = comparison.load_snapshot(actual_path)
    differences = comparison.compare(expected, actual)
    replay.write_json(output / "comparison.json", {"equal": not differences, "differences": differences})
    check(not differences, "all Unity fixture fields match")

    # Deliberately feed wrong expected values. The resulting actual file must remain
    # byte-identical, and comparison must detect the false expectation.
    changed = copy.deepcopy(expected)
    next(r for r in changed["sections"]["transforms"] if r["id"] == "r")["localScale"]["x"] += 10
    changed["sections"]["physBones"][0]["colliders"].reverse()
    changed_path = output / "wrong-expectation.json"
    replay.write_json(changed_path, changed)
    independent = output / "independent.json"
    check(run(changed_path, independent, "independence.log") == 0, "producer accepts selection metadata independently of field values")
    check(actual_path.read_bytes() == independent.read_bytes(), "actual values do not depend on expected fields")
    check(bool(comparison.compare(changed, actual)), "incorrect expectation is detected")
    before = actual_path.read_bytes()
    check(run(expected_path, actual_path, "overwrite.log") != 0, "existing actual output is rejected")
    check(actual_path.read_bytes() == before, "existing actual output is preserved")
    check(run(expected_path, expected_path, "baseline-protection.log") != 0, "baseline output path is rejected")
    check(replay.sha256(expected_path) == baseline_hash, "Unity baseline is unchanged")
    check(all(p.is_file() and replay.sha256(p) == digest for p, digest in source_hashes.items()), "fixture assets are unchanged")
    replay.write_json(output / "report.json", {"passed": len(checks), "checks": checks,
                                              "referenceDumpSha256": replay.sha256(args.reference_dump.resolve())})
    print(f"PASS: {len(checks)} producer integration checks")
    return 0


if __name__ == "__main__":
    sys.exit(main())
