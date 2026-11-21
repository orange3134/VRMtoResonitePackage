"""Prefab orchestration must preserve sources and distinguish capture from parity."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import types
import unittest
from unittest import mock
from test_resolved import snapshot

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "scripts"))
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
        self.editor_version = self.enterContext(mock.patch.object(capture, "editor_product_version", return_value="2022.3.22f1"))
        self.model_meta = self.project / "Assets/Source.fbx.meta"
        self.model_meta.write_text("importBlendShapes: 1")
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
        if "--prefab-root" in command:
            avatar = self.args[self.args.index("--avatar") + 1] if "--avatar" in self.args else None
            self.assertEqual(["--avatar", avatar] if avatar else [], command[5:])
            capture.write(Path(command[4]), dict(prefabGuid="b" * 32, prefabChildId="123" if "--avatar" in command else None))
        elif command[0] == str(self.unity):
            item, = capture.read(Path(kwargs["env"]["RESOPON_RESOLVED_MANIFEST"]))["cases"]
            target = Path(kwargs["env"]["RESOPON_RESOLVED_OUT"])
            (target / "prefab").mkdir(parents=True)
            expected = target / "prefab/expected.json"
            capture.write(expected, snapshot())
            capture.write(expected.with_name("repeat.json"), {} if self.bad_repeat else snapshot())
            capture.write(target / "index.json", dict(unityVersion="2022.3.22f1", cases=[dict(
                id="prefab", prefabPath="Assets/Avatar 日本語.prefab", prefabChildId=item["prefabChildId"], status="captured",
                repeatMatches=True, sourceUnchanged=True, expectedPath="prefab/expected.json",
                expectedSha256=capture.sha(expected), repeatSha256=capture.sha(expected), inputSha256=capture.sha(self.prefab),
                inputSha256After=capture.sha(self.prefab), prefabGuid="b" * 32,
                dependencyHash="same", dependencyHashAfter="same",
                dependencyFiles=[dict(path=str(p), sha256=capture.sha(p)) for p in
                                 (self.prefab, Path(str(self.prefab) + ".meta"), self.model_meta)])]))
        else:
            self.called_producer = True
            value = snapshot()
            if self.different:
                value["sections"]["renderers"][0]["enabled"] = False
            self.assertEqual(["--verify-selection", self.args[self.args.index("--avatar") + 1]
                              if "--avatar" in self.args else ""], command[5:])
            capture.write(Path(command[4]), value)
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
        case = capture.read(self.output / "unity/coverage.json")["cases"][0]
        self.assertEqual(str(self.prefab), case["inputPath"])
        self.assertEqual([], case["conversionArguments"])
        self.assertEqual(capture.sha(self.model_meta), case["dependencyFiles"][str(self.model_meta)])
        self.model_meta.write_text("importBlendShapes: 0")
        with self.assertRaisesRegex(ValueError, "dependency changed"):
            capture.load_replay().verify_project_files(case)

    def test_explicit_selection_is_verified_and_saved(self):
        self.args.extend(["--avatar", "Variant with spaces"])
        code, _ = self.run_capture()
        self.assertEqual(0, code)
        case, = capture.read(self.output / "unity/coverage.json")["cases"]
        self.assertEqual(["--avatar", "Variant with spaces"], case["conversionArguments"])
        self.assertEqual("123", capture.read(self.output / "manifest.json")["cases"][0]["prefabChildId"])

    def test_editor_capturing_another_child_cannot_publish_coverage(self):
        self.args.extend(["--avatar", "Right"])
        def wrong_child(command, **kwargs):
            result = self.run_process(command, **kwargs)
            if command[0] == str(self.unity):
                index_path = Path(kwargs["env"]["RESOPON_RESOLVED_OUT"]) / "index.json"
                index = capture.read(index_path)
                index["cases"][0]["prefabChildId"] = "456"
                index_path.write_text(json.dumps(index), encoding="utf-8")
            return result
        code, report = self.run_capture(wrong_child)
        self.assertEqual(1, code)
        self.assertIn("different prefab or avatar root", report["error"])
        self.assertFalse((self.output / "unity/coverage.json").exists())

    def viewpoint_process(self, command, **kwargs):
        result = self.run_process(command, **kwargs)
        if command[0] == str(self.unity):
            self.assertEqual('1', kwargs['env']['RESOPON_REFERENCE_VIEWPOINTS'])
            folder = Path(kwargs['env']['RESOPON_RESOLVED_OUT'])
            point = folder / 'prefab/viewpoint.json'
            capture.write(point, {'format': 'resopon.unity-avatar-viewpoint', 'descriptors': []})
            capture.write(point.with_name('viewpoint-repeat.json'), capture.read(point))
            index = capture.read(folder / 'index.json')
            index['cases'][0].update(viewpointPath='prefab/viewpoint.json',
                                     viewpointSha256=capture.sha(point), viewpointRepeatSha256=capture.sha(point))
            (folder / 'index.json').write_text(json.dumps(index), encoding='utf-8')
        return result

    def test_viewpoint_capture_records_independent_repeat_hash(self):
        self.args += ['--viewpoints']
        code, report = self.run_capture(self.viewpoint_process)
        self.assertEqual(0, code)
        self.assertEqual(capture.sha(Path(report['viewpointPath'])), report['viewpointSha256'])

    def test_viewpoint_changed_during_parser_invalidates_capture(self):
        self.args += ['--viewpoints']
        def mutate(command, **kwargs):
            result = self.viewpoint_process(command, **kwargs)
            if self.called_producer:
                (self.output / 'unity/prefab/viewpoint.json').write_text('{}')
            return result
        code, report = self.run_capture(mutate)
        self.assertEqual(1, code)
        self.assertEqual('failed', report['captureStatus'])
        self.assertFalse((self.output / 'unity/coverage.json').exists())

    def test_viewpoint_false_repeat_claim_is_rejected(self):
        self.args += ['--viewpoints']
        def mutate(command, **kwargs):
            result = self.viewpoint_process(command, **kwargs)
            if command[0] == str(self.unity):
                (self.output / 'unity/prefab/viewpoint-repeat.json').write_text('{}')
            return result
        code, report = self.run_capture(mutate)
        self.assertEqual(1, code)
        self.assertFalse(self.called_producer)

    def test_wrong_editor_rejected_before_project_or_output_changes(self):
        self.editor_version.return_value = "2022.3.62f3"
        with mock.patch.object(capture, "invoke") as invoked, contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            capture.main(self.args)
        invoked.assert_not_called()
        self.assertFalse(self.output.exists())
        self.assertFalse(list((self.project / "Assets").glob("ResoPonReference_*")))

    def test_dependency_changed_during_parser_invalidates_capture(self):
        def change(command, **kwargs):
            result = self.run_process(command, **kwargs)
            if self.called_producer:
                self.model_meta.unlink()
            return result
        code, report = self.run_capture(change)
        self.assertEqual(1, code)
        self.assertEqual("recapture-required", report["parityStatus"])
        self.assertEqual("failed", report["captureStatus"])
        self.assertFalse((self.output / "unity/coverage.json").exists())

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
        self.assertFalse((self.output / "unity/coverage.json").exists())

    def test_unity_failure_cleans_up_and_does_not_compare(self):
        def fail_editor(command, **kwargs):
            if command[0] == str(self.unity):
                return types.SimpleNamespace(returncode=1)
            return self.run_process(command, **kwargs)
        code, report = self.run_capture(fail_editor)
        self.assertEqual(1, code)
        self.assertEqual("failed", report["captureStatus"])
        self.assertEqual("not-run", report["parityStatus"])
        self.assertEqual([], list((self.project / "Assets").glob("ResoPonReference_*")))

    def test_changed_repeat_capture_cannot_publish_coverage(self):
        def changed_repeat(command, **kwargs):
            result = self.run_process(command, **kwargs)
            if self.called_producer:
                (self.output / "unity/prefab/repeat.json").write_text("{}")
            return result
        code, report = self.run_capture(changed_repeat)
        self.assertEqual(1, code)
        self.assertEqual("failed", report["captureStatus"])
        self.assertFalse((self.output / "unity/coverage.json").exists())

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
