#!/usr/bin/env python3
"""Build once, convert every registered local avatar, and collect inspected packages."""
import argparse
from collections import Counter
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import uuid

import avatar_registry


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write_json(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def compare_unity(case, input_path, directory, build, repo, timeout):
    """Replay the registered capture, never an earlier parity result or a learned baseline."""
    if input_path.suffix.lower() == ".vrm":
        return {"status": "not-applicable", "reason": "Unity prefab projection does not cover VRM inputs"}
    capture = case.get("unityResolvedCollection", {})
    coverage_name = capture.get("coveragePath") or case.get("prefabUnityCheck", {}).get("coveragePath")
    if not coverage_name:
        return {"status": "not-captured", "reason": "No Unity coverage registered; capture Unity reference first"}
    provenance = {}
    try:
        coverage_path = Path(coverage_name).resolve()
        coverage = json.loads(coverage_path.read_text(encoding="utf-8-sig"))
        provenance = {"sourceCoveragePath": str(coverage_path), "sourceCoverageSha256": sha256(coverage_path)}
        if (coverage.get("format") != "resopon.unity-resolved-coverage"
                or coverage.get("schemaVersion") != 1):
            raise ValueError("Invalid Unity coverage manifest")
        matches = [c for c in coverage["cases"] if c["id"] == case["id"]]
        if len(matches) != 1:
            raise ValueError("Unity coverage must contain exactly one matching case ID")
        reference = dict(matches[0])
        if Path(reference["inputPath"]).resolve() != input_path:
            raise ValueError("Unity capture input differs from registered conversion input")
        if reference["status"] != "captured":
            return dict(provenance, status="not-captured", captureStatus=reference["status"],
                        reason=reference.get("reason", reference.get("error", "Unity reference not captured")))
        # Verify the production parser selects this same prefab, including default selection.
        reference["conversionArguments"] = case.get("arguments", [])
        baseline = directory / "unity-baseline"
        baseline.mkdir()
        manifest = baseline / "coverage.json"
        write_json(manifest, {"format": "resopon.unity-resolved-coverage", "schemaVersion": 1,
                              "cases": [reference]})
        output = directory / "unity-comparison"
        command = [sys.executable, "-B", str(repo / "scripts/test-unity-resolved.py"), str(manifest),
                   "--reference-dump", str(build / "UnityReferenceDump.dll"),
                   "--output", str(output), "--work", str(directory / "unity-work"),
                   "--timeout", str(timeout)]
        with (directory / "unity-comparison.log").open("wb") as log:
            run = subprocess.run(command, cwd=repo, stdout=log, stderr=subprocess.STDOUT, timeout=timeout + 120)
        report_path = output / "report.json"
        replay = json.loads(report_path.read_text(encoding="utf-8"))
        result, = replay["cases"]
        if result["id"] != case["id"] or replay["converterSha256"] != sha256(build / "ResoPon.dll"):
            raise ValueError("Unity replay case/converter identity mismatch")
        if run.returncode != 0 and result["status"] == "matched":
            raise ValueError("Unity replay failed despite a matched case")
        return dict(result, **provenance, reportPath=str(report_path), exitCode=run.returncode)
    except (OSError, ValueError, KeyError, TypeError, subprocess.TimeoutExpired) as error:
        return dict(provenance, status="failed", error=str(error))


def validation_status(conversion, comparison):
    unity = comparison["status"]
    if conversion == "failed" or unity in ("different", "failed", "producer-failed", "missing-dependency", "selection-mismatch"):
        return "failed"
    if comparison.get("dependencyScope"):
        return "incomplete"
    return "passed" if unity in ("matched", "not-applicable") else "incomplete"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--jobs", type=int, default=2)
    parser.add_argument("--timeout", type=int, default=1800, help="Seconds per conversion/inspection")
    args = parser.parse_args()
    if args.jobs < 1 or args.timeout < 1:
        parser.error("Jobs and timeout must be positive")
    repo = Path(__file__).resolve().parents[1]
    registry_path = repo / ".local/avatar-tests.json"
    registry = json.loads(registry_path.read_text(encoding="utf-8-sig"))
    cases = registry["cases"]
    ids = [case["id"] for case in cases]
    if (registry.get("version") != 1 or not ids or len(set(ids)) != len(ids)
            or any(not re.fullmatch(r"[a-z0-9][a-z0-9_-]*", value) for value in ids)):
        parser.error("Invalid registry version or case IDs")
    for case in cases:
        extra = case.get("arguments", [])
        if not isinstance(extra, list) or any(not isinstance(value, str) for value in extra):
            parser.error(f"Invalid arguments: {case['id']}")
    run_id = datetime.now().strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:8]
    root = repo / ".tmp_verify/all-avatar-conversions" / run_id
    packages = root / "packages"
    packages.mkdir(parents=True)
    shutil.copy2(registry_path, root / "registry.json")
    environment = dict(os.environ, RESOPON_NOPAUSE="1")
    report = {
        "startedUtc": datetime.now(timezone.utc).isoformat(),
        "gitHead": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip(),
        "hasUncommittedChanges": bool(subprocess.check_output(
            ["git", "status", "--porcelain"], cwd=repo, text=True).strip()),
        "registrySha256": sha256(root / "registry.json"),
        "packageDirectory": str(packages), "status": "running", "cases": [],
        "clientValidation": "not-performed",
    }
    write_json(root / "report.json", report)
    print(f"Output: {root}", flush=True)
    build = root / "build"
    with (root / "build.log").open("wb") as log:
        result = subprocess.run(["dotnet", "build", "tests/UnityReferenceDump", "-c", "Release",
                                 "-o", str(build)], cwd=repo, env=environment,
                                stdout=log, stderr=subprocess.STDOUT)
    if result.returncode:
        report.update(status="build-failed", buildExitCode=result.returncode)
        write_json(root / "report.json", report)
        return 1
    report["converterSha256"] = sha256(build / "ResoPon.dll")
    report["referenceDumpSha256"] = sha256(build / "UnityReferenceDump.dll")

    def convert(case):
        case_id = case["id"]
        directory = root / "cases" / case_id
        directory.mkdir(parents=True)
        item = {"caseId": case_id, "inputPath": case["inputPath"], "status": "running",
                "commands": [], "startedUtc": datetime.now(timezone.utc).isoformat(),
                "unityComparison": {"status": "not-run", "reason": "Input preparation did not complete"}}

        def invoke(arguments, log_name):
            command = ["dotnet", str(directory / "build/ResoPon.dll"), *arguments]
            record = {"arguments": command, "log": str(directory / log_name), "exitCode": None}
            item["commands"].append(record)
            with (directory / log_name).open("wb") as log:
                process = subprocess.run(command, cwd=directory, env=env,
                                         stdout=log, stderr=subprocess.STDOUT, timeout=args.timeout)
            record["exitCode"] = process.returncode
            if process.returncode:
                raise RuntimeError(f"{log_name} exited {process.returncode}")

        print(f"Starting {case_id}", flush=True)
        try:
            input_path = avatar_registry.input_path(registry_path, case)
            item["inputPath"] = str(input_path)
            item["inputSha256"] = sha256(input_path)
            item["unityComparison"] = compare_unity(case, input_path, directory, build, repo, args.timeout)
            print(f"Unity {case_id}: {item['unityComparison']['status']}", flush=True)
            shutil.copytree(build, directory / "build")
            temp = directory / "temp"
            temp.mkdir()
            env = dict(environment, TEMP=str(temp), TMP=str(temp))
            invoke([str(input_path), *case.get("arguments", []), "--output", str(directory / "output")],
                   "convert.log")
            outputs = list((directory / "output").glob("*.resonitepackage"))
            if len(outputs) != 1:
                raise RuntimeError(f"Expected one package, found {len(outputs)}")
            invoke(["--inspect", str(outputs[0])], "inspect.log")
            target = packages / f"{case_id}__{outputs[0].name}"
            source_hash = sha256(outputs[0])
            shutil.copy2(outputs[0], target)
            if sha256(target) != source_hash:
                target.unlink()
                raise RuntimeError("Collected package hash differs from inspected output")
            item.update(status="passed", outputPackage=str(target), packageSha256=source_hash,
                        packageBytes=target.stat().st_size)
        except Exception as error:
            item.update(status="failed", error=str(error))
        item["validationStatus"] = validation_status(item["status"], item["unityComparison"])
        item["finishedUtc"] = datetime.now(timezone.utc).isoformat()
        write_json(directory / "result.json", item)
        print(f"{item['status']}: {case_id}", flush=True)
        return item

    with ThreadPoolExecutor(max_workers=args.jobs) as executor:
        futures = [executor.submit(convert, case) for case in cases]
        for future in as_completed(futures):
            report["cases"].append(future.result())
            report["cases"].sort(key=lambda item: ids.index(item["caseId"]))
            write_json(root / "report.json", report)
    report["passed"] = sum(item["status"] == "passed" for item in report["cases"])
    report["failed"] = len(cases) - report["passed"]
    report["conversionStatus"] = "passed" if report["failed"] == 0 else "failed"
    report["unityComparisonSummary"] = dict(Counter(c["unityComparison"]["status"] for c in report["cases"]))
    report["validationSummary"] = dict(Counter(c["validationStatus"] for c in report["cases"]))
    report["status"] = next((s for s in ("failed", "incomplete") if report["validationSummary"].get(s)), "passed")
    report["finishedUtc"] = datetime.now(timezone.utc).isoformat()
    write_json(root / "report.json", report)
    print(f"Conversion passed {report['passed']}/{len(cases)}; validation: {report['status']}; packages: {packages}", flush=True)
    print(f"Unity comparison: {report['unityComparisonSummary']}", flush=True)
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
