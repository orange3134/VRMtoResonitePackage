#!/usr/bin/env python3
"""Replay captured Unity v2 expectations against ResoPon's production prefab parser.

Never updates expectations. Exit 0 means all selected captured cases matched;
1 includes differences, unsupported producers or failures; 2 is invalid invocation.
"""
import argparse
import collections
import datetime
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write_json(path, value):
    with path.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)
        stream.write("\n")


def load_comparator():
    spec = importlib.util.spec_from_file_location("unity_compare", Path(__file__).with_name("compare-unity-reference.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("coverage", type=Path)
    parser.add_argument("--reference-dump", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--work", required=True, type=Path, help="new directory under .tmp_verify for extraction/temp files")
    parser.add_argument("--case", action="append", dest="case_ids")
    parser.add_argument("--timeout", type=int, default=600)
    args = parser.parse_args(argv)
    coverage_path = args.coverage.resolve()
    coverage = json.loads(coverage_path.read_text(encoding="utf-8-sig"))
    if coverage.get("format") != "resopon.unity-resolved-coverage" or coverage.get("schemaVersion") != 1:
        parser.error("Unsupported coverage manifest")
    cases = coverage["cases"]
    ids = [c["id"] for c in cases]
    if len(set(ids)) != len(ids) or any(not re.fullmatch(r"[a-z0-9][a-z0-9_-]*", key) for key in ids):
        parser.error("Invalid or duplicate case ID")
    if args.case_ids:
        if set(args.case_ids) - set(ids):
            parser.error("Unknown case ID")
        cases = [c for c in cases if c["id"] in args.case_ids]
    if not any(c["status"] == "captured" for c in cases) or args.timeout <= 0:
        parser.error("No captured cases selected, or invalid timeout")
    output, work = args.output.resolve(), args.work.resolve()
    repo = Path(__file__).resolve().parents[1]
    if repo / ".tmp_verify" not in work.parents:
        parser.error("Work must be a new directory under .tmp_verify")
    baseline_dirs = {Path(c["expectedPath"]).resolve().parent for c in cases if c["status"] == "captured"}
    if any(output == p or p in output.parents for p in baseline_dirs | {coverage_path.parent}):
        parser.error("Output must be outside the baseline collection")
    binary = args.reference_dump.resolve()
    binary_hash = sha256(binary)
    converter = binary.with_name("ResoPon.dll")
    converter_hash = sha256(converter)
    comparator = load_comparator()
    output.mkdir(parents=True, exist_ok=False)
    work.mkdir(parents=True, exist_ok=False)
    results = []
    for case in cases:
        result = {"id": case["id"], "captureStatus": case["status"]}
        results.append(result)
        if case["status"] != "captured":
            result.update(status="not-captured", reason=case.get("reason", case.get("error", "")))
            continue
        directory = output / case["id"]
        directory.mkdir()
        temporary = work / case["id"]
        temporary.mkdir()
        expected_path = Path(case["expectedPath"]).resolve()
        input_path = Path(case["inputPath"]).resolve()
        actual_path = directory / "actual.json"
        try:
            if sha256(expected_path) != case["expectedSha256"]:
                raise ValueError("Unity baseline checksum mismatch")
            if sha256(input_path) != case["inputSha256"]:
                raise ValueError("Source input checksum mismatch")
            expected = comparator.load_snapshot(expected_path)
            if expected["schemaVersion"] != 2 or expected["prefabGuid"] != case["prefabGuid"]:
                raise ValueError("Expected schema/prefab mismatch")
            result.update(expectedPath=str(expected_path), expectedSha256=case["expectedSha256"],
                          inputPath=str(input_path), inputSha256=case["inputSha256"])
            command = ["dotnet", str(binary), str(input_path), str(expected_path), str(actual_path)]
            env = dict(os.environ, RESOPON_NOPAUSE="1", TEMP=str(temporary), TMP=str(temporary))
            with (directory / "producer.log").open("xb") as log:
                run = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, env=env, cwd=repo,
                                     timeout=args.timeout, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            result["exitCode"] = run.returncode
            if run.returncode:
                log = (directory / "producer.log").read_text(encoding="utf-8-sig", errors="replace")
                status = "unsupported" if "UNSUPPORTED_MODEL_HIERARCHY:" in log else (
                    "missing-dependency" if "MISSING_PREFAB_SOURCE:" in log else "producer-failed")
                result.update(status=status,
                              error=log.strip().splitlines()[-1] if log.strip() else "No producer diagnostic")
            else:
                actual = comparator.load_snapshot(actual_path)
                differences = comparator.compare(expected, actual)
                write_json(directory / "comparison.json", {"equal": not differences, "differences": differences})
                result.update(status="different" if differences else "matched", differences=len(differences),
                              actualPath=str(actual_path), actualSha256=sha256(actual_path))
            if sha256(expected_path) != case["expectedSha256"] or sha256(input_path) != case["inputSha256"]:
                raise ValueError("Baseline/input changed during replay")
        except (ValueError, OSError, subprocess.TimeoutExpired) as error:
            result.update(status="failed", error=str(error))
        write_json(directory / "result.json", result)
        print(case["id"] + ": " + result["status"], flush=True)
    source_paths = [Path(__file__), Path(__file__).with_name("compare-unity-reference.py"),
                    *sorted((repo / "tests/UnityReferenceDump").glob("*.cs"))]
    report = {"format": "resopon.unity-resolved-parity", "schemaVersion": 1,
              "finishedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "sourceFiles": {str(p.relative_to(repo)): sha256(p) for p in source_paths},
              "coveragePath": str(coverage_path), "coverageSha256": sha256(coverage_path),
              "referenceDump": str(binary), "referenceDumpSha256": binary_hash,
              "converterSha256": converter_hash,
              "summary": dict(collections.Counter(r["status"] for r in results)), "cases": results}
    write_json(output / "report.json", report)
    print(json.dumps(report["summary"], sort_keys=True))
    return 0 if all(r["status"] in ("matched", "not-captured") for r in results) else 1


if __name__ == "__main__":
    sys.exit(main())
