#!/usr/bin/env python3
"""Capture Unity's resolved prefab and compare the same project input with ResoPon.

Uses a trusted, closed Unity project. Python 3.11+, .NET SDK and the project's
Unity Editor are required. Private results default to .local/prefab-reference.
Exit 0: matched, 1: differences or execution failure, 2: invalid invocation.
"""
import argparse
import datetime
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import uuid

REPO = Path(__file__).resolve().parents[1]
EXPORTERS = REPO / "tools/unity/Editor"


def load_replay():
    spec = importlib.util.spec_from_file_location("prefab_replay", REPO / "scripts/test-unity-resolved.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write(path, data):
    with path.open("x", encoding="utf-8") as stream:
        json.dump(data, stream, ensure_ascii=False, indent=2)
        stream.write("\n")


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def locate_project(prefab):
    if prefab.suffix.lower() != ".prefab" or not prefab.is_file():
        raise ValueError("Input must be an existing .prefab file")
    for project in prefab.parents:
        if (project / "Assets").is_dir() and (project / "ProjectSettings").is_dir():
            relative = prefab.relative_to(project).as_posix()
            if not relative.startswith("Assets/"):
                raise ValueError("Prefab must be under the project's Assets directory")
            if not Path(str(prefab) + ".meta").is_file():
                raise ValueError("Prefab .meta file is missing")
            return project, relative
    raise ValueError("Unity project not found for prefab")


def editor_path(project, explicit):
    version_file = project / "ProjectSettings/ProjectVersion.txt"
    match = re.search(r"^m_EditorVersion: (\S+)$", version_file.read_text(encoding="utf-8-sig"), re.M)
    if not match:
        raise ValueError("ProjectVersion.txt has no m_EditorVersion")
    version = match[1]
    editor = explicit or Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Unity/Hub/Editor" / version / "Editor/Unity.exe"
    if not editor.is_file():
        raise ValueError("Matching Unity Editor not found; specify --unity")
    return editor.resolve(), version


def exporter_plan(project):
    """Reuse identical installed exporters; never overwrite another version."""
    missing, installed = [], {}
    for source in sorted(EXPORTERS.glob("*.cs")):
        found = list((project / "Assets").rglob(source.name))
        if len(found) > 1 or any("Editor" not in p.relative_to(project / "Assets").parts[:-1] or
                                p.is_symlink() or sha(p) != sha(source) for p in found):
            raise ValueError("Conflicting installed exporter; update it explicitly: " + source.name)
        if found:
            installed[str(found[0])] = sha(source)
        else:
            missing.append(source)
    if len(missing) + len(installed) != 3:
        raise ValueError("Expected three repository Unity exporters")
    return missing, installed


def remove_installed(folder, copied):
    # Remove only this run's unchanged scripts. Leave unexpected files for review.
    for path, digest in copied.items():
        if path.is_file() and sha(path) == digest:
            path.unlink()
            Path(str(path) + ".meta").unlink(missing_ok=True)
    for path in (folder / "Editor", folder):
        if path.is_dir() and not any(path.iterdir()):
            path.rmdir()
            Path(str(path) + ".meta").unlink(missing_ok=True)
    if folder.exists():
        raise ValueError("Temporary exporter files changed; retained for review: " + str(folder))


def invoke(command, log, timeout, env=None):
    with log.open("xb") as stream:
        return subprocess.run(command, cwd=REPO, env=env, stdout=stream, stderr=subprocess.STDOUT,
                              timeout=timeout, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)).returncode


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("prefab", type=Path)
    parser.add_argument("--unity", type=Path, help="Unity.exe (default: Hub installation matching ProjectVersion.txt)")
    parser.add_argument("--reference-dump", type=Path, help="prebuilt UnityReferenceDump.dll; otherwise build it")
    parser.add_argument("--output", type=Path, help="new result directory; default .local/prefab-reference/<run>")
    parser.add_argument("--case-id", default="prefab", help="ID used in the replayable coverage manifest")
    parser.add_argument("--timeout", type=int, default=1800, help="timeout in seconds for each process")
    args = parser.parse_args(argv)
    token = datetime.datetime.now().strftime("%Y%m%d-%H%M%S-") + uuid.uuid4().hex[:8]
    output = (args.output or REPO / ".local/prefab-reference" / token).resolve()
    work = REPO / ".tmp_verify/prefab-reference" / token
    try:
        prefab = args.prefab.resolve()
        project, asset_path = locate_project(prefab)
        editor, version = editor_path(project, args.unity)
        if args.timeout <= 0 or not re.fullmatch(r"[a-z0-9][a-z0-9_-]*", args.case_id):
            raise ValueError("Invalid timeout or case ID")
        if output.exists() or output == project or project in output.parents:
            raise ValueError("Output must be new and outside the Unity project")
        if (project / "Temp/UnityLockfile").exists():
            raise ValueError("Close this project's Unity Editor before capture (UnityLockfile exists)")
        missing, installed = exporter_plan(project)
        if args.reference_dump and not args.reference_dump.resolve().is_file():
            raise ValueError("Reference dump does not exist")
    except (OSError, ValueError) as error:
        parser.error(str(error))

    output.mkdir(parents=True, exist_ok=False)
    work.mkdir(parents=True, exist_ok=False)
    watched = [prefab, Path(str(prefab) + ".meta"), project / "ProjectSettings/ProjectVersion.txt",
               project / "Packages/manifest.json", project / "Packages/packages-lock.json"]
    before = {str(p): sha(p) if p.is_file() else None for p in watched}
    report = dict(format="resopon.prefab-unity-check", schemaVersion=1, inputPath=str(prefab),
                  projectPath=str(project), prefabPath=asset_path, unityEditor=str(editor),
                  projectUnityVersion=version, status="failed", captureStatus="not-run", parityStatus="not-run",
                  beforeFiles=before, installedExporters=installed, workPath=str(work),
                  sourceFiles={str(p.relative_to(REPO)): sha(p) for p in
                               [Path(__file__), REPO / "scripts/test-unity-resolved.py",
                                REPO / "scripts/compare-unity-reference.py", *sorted(EXPORTERS.glob("*.cs"))]})
    folder = project / "Assets" / ("ResoPonReference_" + uuid.uuid4().hex)
    copied = {}
    exit_code = 1
    try:
        binary = args.reference_dump.resolve() if args.reference_dump else work / "bin/UnityReferenceDump.dll"
        if not args.reference_dump:
            print("Building ResoPon reference reader...", flush=True)
            code = invoke(["dotnet", "build", str(REPO / "tests/UnityReferenceDump"), "-c", "Release",
                           "-o", str(binary.parent)], output / "build.log", args.timeout)
            if code:
                raise ValueError("Reference reader build failed; see build.log")
        if missing:
            (folder / "Editor").mkdir(parents=True, exist_ok=False)
            for source in missing:
                target = folder / "Editor" / source.name
                with target.open("xb") as stream:
                    stream.write(source.read_bytes())
                copied[target] = sha(source)
        manifest = output / "manifest.json"
        write(manifest, {"cases": [{"id": args.case_id, "prefabPath": asset_path}]})
        capture = output / "unity"
        env = dict(os.environ, RESOPON_RESOLVED_MANIFEST=str(manifest), RESOPON_RESOLVED_OUT=str(capture))
        command = [str(editor), "-batchmode", "-nographics", "-projectPath", str(project),
                   "-executeMethod", "ResoPonResolvedReferenceBatch.Run", "-logFile", str(output / "unity.log")]
        report["unityCommand"] = command
        report["captureStatus"] = "failed"
        print("Capturing resolved Unity prefab (twice)...", flush=True)
        report["unityExitCode"] = invoke(command, output / "unity-process.log", args.timeout, env)
        if report["unityExitCode"]:
            report["captureStatus"] = "failed"
            raise ValueError("Unity capture failed; see unity.log and unity/index.json if present")
        index = read(capture / "index.json")
        if index["unityVersion"] != version:
            raise ValueError("Unity Editor version differs from ProjectVersion.txt")
        case, = index["cases"]
        if case["id"] != args.case_id or case["prefabPath"] != asset_path or case["status"] != "captured" or not (
                case["repeatMatches"] and case["sourceUnchanged"]) or case["dependencyHash"] != case["dependencyHashAfter"]:
            raise ValueError("Unity did not capture a stable reference for the selected prefab")
        expected = (capture / case["expectedPath"]).resolve()
        if capture not in expected.parents or sha(expected) != case["expectedSha256"] or (
                sha(expected.with_name("repeat.json")) != case["expectedSha256"]):
            raise ValueError("Unity capture hashes or paths do not match")
        if case["inputSha256"] != before[str(prefab)] or case["inputSha256After"] != before[str(prefab)]:
            raise ValueError("Prefab changed while Unity was starting or capturing")
        replay = load_replay()
        snapshot = replay.load_comparator().load_snapshot(expected)
        if snapshot["schemaVersion"] != 2 or snapshot["prefabGuid"] != case["prefabGuid"]:
            raise ValueError("Unity capture schema or prefab GUID mismatch")
        report.update(captureStatus="captured", unityVersion=index["unityVersion"],
                      dependencyHash=case["dependencyHash"], dependencyHashAfter=case["dependencyHashAfter"])
        coverage = capture / "coverage.json"
        write(coverage, dict(format="resopon.unity-resolved-coverage", schemaVersion=1, cases=[dict(
            id=args.case_id, status="captured", inputPath=str(prefab), inputSha256=sha(prefab),
            prefabGuid=case["prefabGuid"], expectedPath=str(expected), expectedSha256=sha(expected))]))
        report["coveragePath"] = str(coverage)
        print("Comparing ResoPon with Unity...", flush=True)
        exit_code = replay.main([str(coverage), "--reference-dump", str(binary),
                                      "--output", str(output / "parity"), "--work", str(work / "parity"),
                                      "--timeout", str(args.timeout)])
        parity = read(output / "parity/report.json")
        report.update(parityStatus=parity["cases"][0]["status"], parityReport=str(output / "parity/report.json"))
        report["status"] = report["parityStatus"]
    except (OSError, ValueError, KeyError, subprocess.TimeoutExpired) as error:
        report.update(status="failed", error=str(error))
        exit_code = 1
    finally:
        try:
            if copied or folder.exists():
                remove_installed(folder, copied)
            after = {str(p): sha(p) if p.is_file() else None for p in watched}
            report["afterFiles"] = after
            report["inputAndProjectFilesUnchanged"] = before == after
            if before != after or any(not Path(p).is_file() or sha(p) != digest for p, digest in installed.items()):
                raise ValueError("Input, project configuration or installed exporter changed during the run")
        except (OSError, ValueError) as error:
            report.update(status="failed", cleanupOrSourceError=str(error))
            exit_code = 1
        report["finishedUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        report["exitCode"] = exit_code
        write(output / "report.json", report)
    print("Result: " + report["status"] + "; " + str(output / "report.json"), flush=True)
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
