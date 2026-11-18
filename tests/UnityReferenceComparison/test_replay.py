"""Replay must not silently pass unsupported or changed local baselines."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import types
import unittest
from unittest import mock
from test_resolved import snapshot

REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("replay", REPO / "scripts/test-unity-resolved.py")
replay = importlib.util.module_from_spec(spec)
spec.loader.exec_module(replay)


class ReplayTests(unittest.TestCase):
    def setUp(self):
        scratch = REPO / ".tmp_verify"
        scratch.mkdir(exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="parity-runner-", dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        baseline = self.root / "baseline"
        baseline.mkdir()
        self.expected = baseline / "expected.json"
        replay.write_json(self.expected, snapshot())
        source = baseline / "invented.unitypackage"
        source.write_bytes(b"invented input, producer is mocked")
        self.coverage = baseline / "coverage.json"
        replay.write_json(self.coverage, dict(format="resopon.unity-resolved-coverage", schemaVersion=1, cases=[
            dict(id="example", status="captured", prefabGuid="b" * 32, inputPath=str(source),
                 inputSha256=replay.sha256(source), expectedPath=str(self.expected), expectedSha256=replay.sha256(self.expected)),
            dict(id="uncaptured", status="failed", reason="Missing script"),
        ]))
        binary = self.root / "UnityReferenceDump.dll"
        binary.write_bytes(b"mock")
        binary.with_name("ResoPon.dll").write_bytes(b"mock production")
        self.output = self.root / "output"
        self.arguments = [str(self.coverage), "--reference-dump", str(binary), "--output", str(self.output),
                          "--work", str(self.root / "work")]

    def run_replay(self, producer):
        with mock.patch.object(replay.subprocess, "run", side_effect=producer) as invoked, contextlib.redirect_stdout(io.StringIO()):
            code = replay.main(self.arguments)
        report = json.loads((self.output / "report.json").read_text(encoding="utf-8"))
        return code, report, invoked

    def test_match_does_not_count_uncaptured_as_match(self):
        def producer(command, **kwargs):
            replay.write_json(Path(command[-1]), snapshot())
            return types.SimpleNamespace(returncode=0)
        code, report, _ = self.run_replay(producer)
        self.assertEqual(0, code)
        self.assertEqual({"matched": 1, "not-captured": 1}, report["summary"])

    def test_unsupported_is_not_success(self):
        def producer(command, **kwargs):
            kwargs["stdout"].write(b"UNSUPPORTED_MODEL_HIERARCHY: invented.fbx\n")
            return types.SimpleNamespace(returncode=1)
        code, report, _ = self.run_replay(producer)
        self.assertEqual(1, code)
        self.assertEqual("unsupported", report["cases"][0]["status"])

    def test_batch_selection_is_verified_by_producer(self):
        manifest = json.loads(self.coverage.read_text(encoding="utf-8"))
        manifest["cases"][0]["conversionArguments"] = ["--avatar", "Other avatar"]
        self.coverage.write_text(json.dumps(manifest), encoding="utf-8")
        def producer(command, **kwargs):
            self.assertEqual(["--verify-selection", "Other avatar"], command[-2:])
            kwargs["stdout"].write(b"SELECTION_MISMATCH: different prefab\n")
            return types.SimpleNamespace(returncode=1)
        code, report, _ = self.run_replay(producer)
        self.assertEqual(1, code)
        self.assertEqual("selection-mismatch", report["cases"][0]["status"])

    def test_batch_default_selection_is_also_verified(self):
        manifest = json.loads(self.coverage.read_text(encoding="utf-8"))
        manifest["cases"][0]["conversionArguments"] = []
        self.coverage.write_text(json.dumps(manifest), encoding="utf-8")
        def producer(command, **kwargs):
            self.assertEqual(["--verify-selection", ""], command[-2:])
            replay.write_json(Path(command[4]), snapshot())
            return types.SimpleNamespace(returncode=0)
        code, report, _ = self.run_replay(producer)
        self.assertEqual(0, code)
        self.assertEqual("matched", report["cases"][0]["status"])

    def test_new_package_lock_invalidates_prefab_replay(self):
        manifest = json.loads(self.coverage.read_text(encoding="utf-8"))
        lock = self.root / "packages-lock.json"
        manifest["cases"][0]["projectFiles"] = {str(lock): None}
        self.coverage.write_text(json.dumps(manifest), encoding="utf-8")
        lock.write_text("{}", encoding="utf-8")
        code, report, producer = self.run_replay(None)
        self.assertEqual(1, code)
        self.assertIn("project configuration changed", report["cases"][0]["error"])
        producer.assert_not_called()

    def test_prefab_capture_without_dependency_inventory_requires_recapture(self):
        manifest = json.loads(self.coverage.read_text(encoding="utf-8"))
        source = Path(manifest["cases"][0]["inputPath"])
        prefab = source.with_suffix(".prefab")
        source.rename(prefab)
        manifest["cases"][0]["inputPath"] = str(prefab)
        self.coverage.write_text(json.dumps(manifest), encoding="utf-8")
        code, report, producer = self.run_replay(None)
        self.assertEqual(1, code)
        self.assertEqual("recapture-required", report["cases"][0]["status"])
        producer.assert_not_called()

    def test_missing_dependency_is_not_success(self):
        def producer(command, **kwargs):
            kwargs["stdout"].write(b"MISSING_PREFAB_SOURCE: abc\n")
            return types.SimpleNamespace(returncode=1)
        code, report, _ = self.run_replay(producer)
        self.assertEqual(1, code)
        self.assertEqual("missing-dependency", report["cases"][0]["status"])

    def test_archive_dependency_overlay_is_explicit_and_hashed(self):
        manifest = json.loads(self.coverage.read_text(encoding="utf-8"))
        dependency = self.root / "template.prefab"
        dependency.write_bytes(b"self-authored dependency")
        files = {str(dependency): replay.sha256(dependency)}
        manifest["cases"][0]["dependencyFiles"] = files
        self.coverage.write_text(json.dumps(manifest), encoding="utf-8")
        def producer(command, **kwargs):
            self.assertEqual("--dependencies", command[-2])
            self.assertEqual(files, json.loads(Path(command[-1]).read_text(encoding="utf-8")))
            replay.write_json(Path(command[4]), snapshot())
            return types.SimpleNamespace(returncode=0)
        code, report, _ = self.run_replay(producer)
        self.assertEqual(0, code)
        self.assertIn("archive plus", report["cases"][0]["dependencyScope"])

    def test_changed_archive_dependency_requires_recapture(self):
        manifest = json.loads(self.coverage.read_text(encoding="utf-8"))
        dependency = self.root / "template.prefab"
        dependency.write_bytes(b"before")
        manifest["cases"][0]["dependencyFiles"] = {str(dependency): replay.sha256(dependency)}
        self.coverage.write_text(json.dumps(manifest), encoding="utf-8")
        dependency.write_bytes(b"after")
        code, report, producer = self.run_replay(None)
        self.assertEqual(1, code)
        self.assertEqual("recapture-required", report["cases"][0]["status"])
        producer.assert_not_called()

    def test_actual_difference_fails_replay(self):
        def producer(command, **kwargs):
            actual = snapshot()
            actual["sections"]["renderers"][0]["enabled"] = False
            replay.write_json(Path(command[-1]), actual)
            return types.SimpleNamespace(returncode=0)
        code, report, _ = self.run_replay(producer)
        self.assertEqual(1, code)
        self.assertEqual("different", report["cases"][0]["status"])
        self.assertEqual(1, report["cases"][0]["differences"])

    def test_changed_baseline_rejected_before_producer(self):
        with self.expected.open("a", encoding="utf-8") as stream:
            stream.write("\n")
        code, report, invoked = self.run_replay(lambda *a, **k: None)
        invoked.assert_not_called()
        self.assertEqual(1, code)
        self.assertEqual("failed", report["cases"][0]["status"])
        self.assertIn("checksum", report["cases"][0]["error"])

    def test_existing_output_is_never_reused(self):
        self.output.mkdir()
        sentinel = self.output / "report.json"
        sentinel.write_text("keep", encoding="utf-8")
        with self.assertRaises(FileExistsError):
            self.run_replay(lambda *a, **k: None)
        self.assertEqual("keep", sentinel.read_text(encoding="utf-8"))
