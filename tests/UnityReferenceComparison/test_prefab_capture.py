"""Prefab orchestration must preserve sources and distinguish capture from parity."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tempfile
import types
import unittest
from unittest import mock
from test_resolved import snapshot

REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("prefab_capture", REPO / "scripts/check-prefab-unity.py")
capture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(capture)


class PrefabCaptureTests(unittest.TestCase):
    def setUp(self):
        (REPO / ".tmp_verify").mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="prefab-capture-", dir=REPO / ".tmp_verify")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.project = self.root / "Unity project"
        (self.project / "Assets").mkdir(parents=True)
        (self.project / "ProjectSettings").mkdir()
        (self.project / "ProjectSettings/ProjectVersion.txt").write_text("m_EditorVersion: 2022.3.22f1\n")
        self.prefab = self.project / "Assets/Avatar 日本語.prefab"
        self.prefab.write_text("invented prefab")
        Path(str(self.prefab) + ".meta").write_text("guid: " + "b" * 32)
        self.unity = self.root / "Unity.exe"
        self.unity.touch()
        self.binary = self.root / "UnityReferenceDump.dll"
        self.binary.write_bytes(b"mock producer")
        self.binary.with_name("ResoPon.dll").write_bytes(b"mock converter")
        self.output = self.root / "result"
        self.args = [str(self.prefab), "--unity", str(self.unity), "--reference-dump", str(self.binary),
                     "--output", str(self.output)]
        self.different = False
        self.bad_repeat = False
        self.mutate = False
        self.called_producer = False

    def run_process(self, command, **kwargs):
        if command[0] == str(self.unity):
            target = Path(kwargs["env"]["RESOPON_RESOLVED_OUT"])
            (target / "prefab").mkdir(parents=True)
            expected = target / "prefab/expected.json"
            capture.write(expected, snapshot())
            capture.write(expected.with_name("repeat.json"), {} if self.bad_repeat else snapshot())
            capture.write(target / "index.json", dict(unityVersion="2022.3.22f1", cases=[dict(
                id="prefab", prefabPath="Assets/Avatar 日本語.prefab", status="captured",
                repeatMatches=True, sourceUnchanged=True, expectedPath="prefab/expected.json",
                expectedSha256=capture.sha(expected), inputSha256=capture.sha(self.prefab),
                inputSha256After=capture.sha(self.prefab), prefabGuid="b" * 32,
                dependencyHash="same", dependencyHashAfter="same")]))
        else:
            self.called_producer = True
            value = snapshot()
            if self.different:
                value["sections"]["renderers"][0]["enabled"] = False
            capture.write(Path(command[-1]), value)
            if self.mutate:
                self.prefab.write_text("changed source")
        return types.SimpleNamespace(returncode=0)

    def run_capture(self, side_effect=None):
        with mock.patch.object(subprocess, "run", side_effect=side_effect or self.run_process), contextlib.redirect_stdout(io.StringIO()):
            code = capture.main(self.args)
        report = capture.read(self.output / "report.json")
        self.addCleanup(lambda: self.remove_work(Path(report["workPath"])))
        return code, report

    @staticmethod
    def remove_work(path):
        import shutil
        assert REPO / ".tmp_verify/prefab-reference" in path.parents
        shutil.rmtree(path)

    def test_capture_and_compare_same_prefab_cleanup_and_replay_manifest(self):
        code, report = self.run_capture()
        self.assertEqual(0, code)
        self.assertEqual("captured", report["captureStatus"])
        self.assertEqual("matched", report["parityStatus"])
        self.assertTrue(report["inputAndProjectFilesUnchanged"])
        self.assertEqual([], list((self.project / "Assets").glob("ResoPonReference_*")))
        self.assertEqual(str(self.prefab), capture.read(self.output / "unity/coverage.json")["cases"][0]["inputPath"])

    def test_difference_keeps_valid_unity_capture(self):
        self.different = True
        code, report = self.run_capture()
        self.assertEqual(1, code)
        self.assertEqual("captured", report["captureStatus"])
        self.assertEqual("different", report["status"])

    def test_false_repeat_claim_rejected_before_producer(self):
        self.bad_repeat = True
        code, report = self.run_capture()
        self.assertEqual(1, code)
        self.assertFalse(self.called_producer)
        self.assertEqual("not-run", report["parityStatus"])

    def test_source_changed_during_parser_cannot_pass(self):
        self.mutate = True
        code, report = self.run_capture()
        self.assertEqual(1, code)
        self.assertEqual("failed", report["status"])
        self.assertFalse(report["inputAndProjectFilesUnchanged"])

    def test_unity_failure_cleans_up_and_does_not_compare(self):
        code, report = self.run_capture(lambda *a, **k: types.SimpleNamespace(returncode=1))
        self.assertEqual(1, code)
        self.assertEqual("failed", report["captureStatus"])
        self.assertEqual("not-run", report["parityStatus"])
        self.assertEqual([], list((self.project / "Assets").glob("ResoPonReference_*")))

    def test_timeout_is_recorded_and_scripts_cleaned_up(self):
        def timeout(command, **kwargs):
            raise subprocess.TimeoutExpired(command, 1)
        code, report = self.run_capture(timeout)
        self.assertEqual(1, code)
        self.assertIn("timed out", report["error"])
        self.assertEqual([], list((self.project / "Assets").glob("ResoPonReference_*")))

    def test_existing_output_is_never_reused(self):
        self.output.mkdir()
        marker = self.output / "keep.json"
        marker.write_text("keep")
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
            capture.main(self.args)
        self.assertEqual(2, error.exception.code)
        self.assertEqual("keep", marker.read_text())

    def test_open_project_rejected_without_adding_scripts(self):
        (self.project / "Temp").mkdir()
        (self.project / "Temp/UnityLockfile").touch()
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            capture.main(self.args)
        self.assertFalse(self.output.exists())

    def test_installed_different_exporter_not_overwritten(self):
        directory = self.project / "Assets/Editor"
        directory.mkdir()
        source = directory / "ResoPonReferenceExporter.cs"
        source.write_text("user's other version")
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            capture.main(self.args)
        self.assertEqual("user's other version", source.read_text())
        self.assertFalse(self.output.exists())

    def test_installed_identical_exporters_preserved(self):
        directory = self.project / "Assets/Editor"
        directory.mkdir()
        for source in capture.EXPORTERS.glob("*.cs"):
            (directory / source.name).write_bytes(source.read_bytes())
        code, _ = self.run_capture()
        self.assertEqual(0, code)
        self.assertEqual(3, len(list(directory.glob("*.cs"))))

    def test_changed_temporary_script_is_not_deleted(self):
        def modified(command, **kwargs):
            result = self.run_process(command, **kwargs)
            if command[0] == str(self.unity):
                next((self.project / "Assets").rglob("ResoPonReferenceExporter.cs")).write_text("edited")
            return result
        code, report = self.run_capture(modified)
        self.assertEqual(1, code)
        self.assertIn("retained", report["cleanupOrSourceError"])
        self.assertEqual("edited", next((self.project / "Assets").rglob("ResoPonReferenceExporter.cs")).read_text())


if __name__ == "__main__":
    unittest.main()
