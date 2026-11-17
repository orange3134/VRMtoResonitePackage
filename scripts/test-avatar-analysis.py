#!/usr/bin/env python3
"""Replay private parser/Unity references without starting Unity or changing baselines."""
import argparse
import datetime
import difflib
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys


def sha256(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def parser_snapshot(log, kind):
    """Keep selected-avatar diagnostic fields; omit startup/version/import chatter."""
    lines = log.splitlines()
    marker = r"^meshes: \d+$" if kind == "vrm" else r"^アバター: .+"
    start = next((i for i, line in enumerate(lines) if re.match(marker, line)), None)
    if start is None:
        raise ValueError("Parser output has no selected-avatar diagnostic section")
    return {"format": "resopon.parser-text", "schemaVersion": 1, "kind": kind,
            "lines": [line.rstrip() for line in lines[start:] if line.strip()]}


def write_json(path, data):
    with path.open("x", encoding="utf-8") as stream:
        json.dump(data, stream, ensure_ascii=False, indent=2)
        stream.write("\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("--converter", required=True, type=Path)
    parser.add_argument("--reference-dump", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--case", action="append", dest="case_ids")
    parser.add_argument("--timeout", type=int, default=600)
    args = parser.parse_args()
    manifest_path = args.manifest.resolve()
    baseline = manifest_path.parent
    output = args.output.resolve()
    if output == baseline or baseline in output.parents:
        parser.error("Output must be outside the baseline collection")
    if args.timeout <= 0:
        parser.error("Timeout must be positive")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    if manifest.get("format") != "resopon.avatar-analysis" or manifest.get("schemaVersion") != 1:
        parser.error("Unsupported collection manifest")
    cases = manifest["cases"]
    ids = [case["id"] for case in cases]
    if len(set(ids)) != len(ids) or any(not re.fullmatch(r"[a-z0-9][a-z0-9_-]*", value) for value in ids):
        parser.error("Invalid or duplicate case IDs")
    if args.case_ids:
        if set(args.case_ids) - set(ids):
            parser.error("Unknown case ID")
        cases = [case for case in cases if case["id"] in args.case_ids]
    if not cases:
        parser.error("No cases selected")
    binaries = {"converter": args.converter.resolve(), "referenceDump": args.reference_dump.resolve()}
    binary_hashes = {name: sha256(path) for name, path in binaries.items()}
    # Never reuse output directories or overwrite an earlier result.
    output.mkdir(parents=True, exist_ok=False)
    repo = Path(__file__).resolve().parents[1]
    env = dict(os.environ, RESOPON_NOPAUSE="1")
    results = []

    def expected_path(reference):
        path = (baseline / reference["path"]).resolve()
        if baseline not in path.parents:
            raise ValueError("Expected snapshot is outside the collection")
        if sha256(path) != reference["sha256"]:
            raise ValueError("Expected snapshot changed: " + str(path))
        return path

    def run(command, log, case_output):
        temp = case_output / "temp"
        temp.mkdir(exist_ok=True)
        child_env = dict(env, TEMP=str(temp), TMP=str(temp))
        with log.open("xb") as stream:
            completed = subprocess.run(command, cwd=repo, env=child_env, stdout=stream,
                                       stderr=subprocess.STDOUT, timeout=args.timeout,
                                       creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        return completed.returncode

    for case in cases:
        folder = output / case["id"]
        folder.mkdir()
        result = {"id": case["id"], "status": "failed", "commands": [],
                  "unityBaselineStatus": case["unity"]["status"]}
        try:
            source = Path(case["inputPath"])
            if sha256(source) != case["inputSha256"]:
                raise ValueError("Input changed since capture; recollect explicitly")
            for dependency in case.get("inputDependencies", []):
                if sha256(dependency["path"]) != dependency["sha256"]:
                    raise ValueError("Prefab dependency changed: " + dependency["path"])
            reference = expected_path(case["parser"])
            kind = "vrm" if source.suffix.lower() == ".vrm" else "vrchat"
            command = ["dotnet", str(binaries["converter"]), "--assimp-dump" if kind == "vrm" else "--vrchat-dump",
                       str(source), *case.get("arguments", [])]
            code = run(command, folder / "parser.log", folder)
            result["commands"].append({"arguments": command, "exitCode": code})
            if code != 0:
                raise ValueError("Parser exited with code " + str(code))
            actual = parser_snapshot((folder / "parser.log").read_text(encoding="utf-8-sig"), kind)
            write_json(folder / "parser.actual.json", actual)
            expected = json.loads(reference.read_text(encoding="utf-8-sig"))
            if actual != expected:
                diff = list(difflib.unified_diff(json.dumps(expected, ensure_ascii=False, indent=2).splitlines(),
                                                json.dumps(actual, ensure_ascii=False, indent=2).splitlines(),
                                                fromfile="expected", tofile="actual", lineterm=""))
                (folder / "parser.diff.txt").write_text("\n".join(diff) + "\n", encoding="utf-8")
                raise ValueError("Parser snapshot differs")
            result["parser"] = "matched"
            if case["unity"]["status"] == "verified":
                unity_expected = expected_path(case["unity"])
                actual_path = folder / "unity.actual.json"
                command = ["dotnet", str(binaries["referenceDump"]), str(source), str(unity_expected), str(actual_path)]
                code = run(command, folder / "unity-dump.log", folder)
                result["commands"].append({"arguments": command, "exitCode": code})
                if code != 0:
                    raise ValueError("UnityReferenceDump exited with code " + str(code))
                command = [sys.executable, "-B", str(repo / "scripts/compare-unity-reference.py"),
                           str(unity_expected), str(actual_path), "--report", str(folder / "unity.diff.json")]
                code = run(command, folder / "unity-compare.log", folder)
                result["commands"].append({"arguments": command, "exitCode": code})
                if code != 0:
                    raise ValueError("Unity reference comparison exited with code " + str(code))
                result["unity"] = "matched"
            else:
                result["unity"] = "not-verified"
                result["unityReason"] = case["unity"].get("reason")
            result["status"] = "passed"
        except (OSError, ValueError, KeyError, subprocess.TimeoutExpired) as error:
            result["error"] = str(error)
        write_json(folder / "result.json", result)
        results.append(result)
        print(case["id"] + ": " + result["status"] + (" - " + result["error"] if "error" in result else ""), flush=True)
    report = {"format": "resopon.avatar-analysis-replay", "schemaVersion": 1,
              "manifest": str(manifest_path), "manifestSha256": sha256(manifest_path),
              "finishedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "binarySha256": binary_hashes, "cases": results,
              "passed": sum(r["status"] == "passed" for r in results),
              "failed": sum(r["status"] != "passed" for r in results),
              "unityMatched": sum(r.get("unity") == "matched" for r in results)}
    write_json(output / "report.json", report)
    return 1 if report["failed"] else 0


if __name__ == "__main__":
    sys.exit(main())
