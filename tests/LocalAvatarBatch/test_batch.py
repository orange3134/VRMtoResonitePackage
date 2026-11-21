"""Exercise registry inputs across the batch runner's per-case working directories."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "scripts"))
spec = importlib.util.spec_from_file_location("avatar_batch", REPO / "scripts/convert-all-local-avatars.py")
batch = importlib.util.module_from_spec(spec)
spec.loader.exec_module(batch)


class BatchFixture:
    def setUp(self):
        (REPO / ".tmp_verify").mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=REPO / ".tmp_verify", prefix="batch-path-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.repo = self.root / "repo"
        (self.repo / ".local").mkdir(parents=True)
        self.caller = self.root / "caller"
        self.caller.mkdir()
        self.input = self.repo / ".local" / "avatar with spaces 日本語.vrm"
        self.input.write_bytes(b"self-authored fixture")
        self.converted = []
        self.after_inspect = None

    def invoke(self, command, *, cwd, **kwargs):
        if command[1] == "build":
            output = Path(command[command.index("-o") + 1])
            output.mkdir()
            (output / "ResoPon.dll").write_bytes(b"test converter")
            (output / "UnityReferenceDump.dll").write_bytes(b"test reference producer")
        elif command[2] == "--inspect":
            self.assertEqual(b"package fixture", (Path(cwd) / command[3]).read_bytes())
            if self.after_inspect:
                self.after_inspect()
        else:
            # Resolve exactly as a child process would, against its supplied cwd.
            source = Path(cwd) / command[2]
            if not source.is_file():
                return subprocess.CompletedProcess(command, 1)
            self.assertEqual(self.input.read_bytes(), source.read_bytes())
            self.assertTrue(Path(command[2]).is_absolute())
            self.converted.append(command[2])
            output = Path(command[command.index("--output") + 1])
            output.mkdir()
            (output / "Avatar.resonitepackage").write_bytes(b"package fixture")
        return subprocess.CompletedProcess(command, 0)

    def run_batch(self, paths, metadata=None):
        registry = self.repo / ".local/avatar-tests.json"
        batch.write_json(registry, {"version": 1, "cases": [
            {"id": f"case-{index}", "inputPath": path, "arguments": [], **(metadata or {})}
            for index, path in enumerate(paths)]})
        original = registry.read_bytes()
        with (mock.patch.object(batch, "__file__", str(self.repo / "scripts/batch.py")),
              mock.patch.object(batch.subprocess, "run", side_effect=self.invoke),
              mock.patch.object(batch.subprocess, "check_output", return_value=""),
              mock.patch("sys.argv", ["batch.py", "--jobs", "1"]),
              contextlib.chdir(self.caller), contextlib.redirect_stdout(io.StringIO())):
            code = batch.main()
        self.assertEqual(original, registry.read_bytes())
        report_path = next(self.repo.glob(".tmp_verify/all-avatar-conversions/*/report.json"))
        return code, json.loads(report_path.read_text(encoding="utf-8"))


class BatchInputTests(BatchFixture, unittest.TestCase):
    def test_saved_registry_describes_the_executed_arguments(self):
        # Simulate an editor replacing the working registry after parsing and before
        # the run directory is created. Keep that edit visible until build starts.
        registry = self.repo / ".local/avatar-tests.json"
        mint_id, invoke = batch.uuid.uuid4, self.invoke
        original = None

        def edit_registry():
            nonlocal original
            original = registry.read_bytes()
            updated = json.loads(original)
            updated["cases"][0]["arguments"] = ["--no-avatar"]
            batch.write_json(registry, updated)
            return mint_id()

        def build_and_convert(command, **kwargs):
            if command[1] == "build":
                registry.write_bytes(original)
            return invoke(command, **kwargs)

        with mock.patch.object(batch.uuid, "uuid4", side_effect=edit_registry), \
             mock.patch.object(self, "invoke", side_effect=build_and_convert):
            code, report = self.run_batch([self.input.name])
        self.assertEqual(0, code)
        saved = Path(report["packageDirectory"]).parent / "registry.json"
        self.assertEqual(original, saved.read_bytes())
        self.assertEqual(batch.sha256(saved), report["registrySha256"])
        self.assertNotIn("--no-avatar", report["cases"][0]["commands"][0]["arguments"])

    def test_selection_follows_cli_option_consumption(self):
        select = batch.avatar_registry.selected_avatar
        self.assertIsNone(select(["--transparent-cutout", "--avatar"]))
        self.assertEqual("LastName", select(["--AvAtAr", "First", "-O", "--avatar", "--AVATAR", "LastName"]))
        self.assertEqual("--avatar", select(["--avatar", "--avatar"]))
        self.assertIsNone(select(["--avatar", "First", "--Avatar", ""]))
        with self.assertRaises(ValueError):
            select(["--AVATAR"])

    def test_changed_input_is_not_collected(self):
        self.after_inspect = lambda: self.input.write_bytes(b"replaced input")
        code, report = self.run_batch([str(self.input)])
        self.assertEqual(1, code)
        self.assertEqual("failed", report["cases"][0]["validationStatus"])
        self.assertIn("input changed", report["cases"][0]["error"])
        self.assertEqual([], list(Path(report["packageDirectory"]).iterdir()))

    def test_relative_input_uses_registry_directory_for_hash_and_conversion(self):
        (self.caller / self.input.name).write_bytes(b"different avatar at the same relative path")
        code, report = self.run_batch([self.input.name])
        self.assertEqual(0, code)
        self.assertEqual([str(self.input)], self.converted)
        self.assertEqual(str(self.input), report["cases"][0]["inputPath"])
        self.assertEqual(batch.sha256(self.input), report["cases"][0]["inputSha256"])
        self.assertEqual(1, len(list(Path(report["packageDirectory"]).glob("*.resonitepackage"))))

    def test_absolute_input_preserved(self):
        code, report = self.run_batch([str(self.input)])
        self.assertEqual(0, code)
        self.assertEqual([str(self.input)], self.converted)
        self.assertEqual("passed", report["cases"][0]["status"])

    def test_missing_relative_input_does_not_prevent_next_conversion(self):
        code, report = self.run_batch(["missing.vrm", self.input.name])
        self.assertEqual(1, code)
        self.assertEqual(["failed", "passed"], [item["status"] for item in report["cases"]])
        self.assertEqual([str(self.input)], self.converted)
        self.assertEqual((1, 1), (report["passed"], report["failed"]))


class UnityBatchTests(BatchFixture, unittest.TestCase):
    def setUp(self):
        super().setUp()
        self.input = self.input.with_suffix(".unitypackage")
        self.input.write_bytes(b"invented package; no external assets")
        self.coverage = self.root / "coverage.json"
        self.reference = {"id": "case-0", "inputPath": str(self.input), "status": "captured",
                          "inputSha256": batch.sha256(self.input)}
        self.parity = "matched"
        self.replay_metadata = {}
        self.replayed = []

    def invoke(self, command, *, cwd, **kwargs):
        if command[0] == batch.sys.executable:
            manifest = json.loads(Path(command[3]).read_text(encoding="utf-8"))
            self.replayed.append(manifest["cases"][0])
            output = Path(command[command.index("--output") + 1])
            output.mkdir()
            binary = Path(command[command.index("--reference-dump") + 1])
            batch.write_json(output / "report.json", {
                "converterSha256": batch.sha256(binary.with_name("ResoPon.dll")),
                "cases": [{"id": "case-0", "status": self.parity, "differences": int(self.parity == "different"),
                           **self.replay_metadata}]})
            return subprocess.CompletedProcess(command, int(self.parity != "matched"))
        return super().invoke(command, cwd=cwd, **kwargs)

    def compare(self):
        batch.write_json(self.coverage, {"format": "resopon.unity-resolved-coverage", "schemaVersion": 1,
                                         "cases": [self.reference]})
        before = self.coverage.read_bytes()
        code, report = self.run_batch([str(self.input)], {
            "unityResolvedCollection": {"coveragePath": str(self.coverage)}, "arguments": ["--avatar", "Chosen"]})
        self.assertEqual(before, self.coverage.read_bytes())
        return code, report

    def test_match_includes_selection_and_current_converter(self):
        code, report = self.compare()
        self.assertEqual(0, code)
        self.assertEqual({"matched": 1}, report["unityComparisonSummary"])
        self.assertEqual(["--avatar", "Chosen"], self.replayed[0]["conversionArguments"])
        self.assertEqual("passed", report["status"])

    def test_difference_fails_validation_but_collects_converted_package(self):
        self.parity = "different"
        code, report = self.compare()
        self.assertEqual(1, code)
        self.assertEqual("passed", report["conversionStatus"])
        self.assertEqual("failed", report["status"])
        self.assertTrue(Path(report["cases"][0]["outputPackage"]).is_file())

    def test_external_dependency_match_is_incomplete_for_the_converted_archive(self):
        self.replay_metadata["dependencyScope"] = "archive plus captured project dependencies"
        code, report = self.compare()
        self.assertEqual(1, code)
        self.assertEqual("incomplete", report["status"])
        self.assertEqual("passed", report["conversionStatus"])
        self.assertEqual({"matched": 1}, report["unityComparisonSummary"])
        self.assertEqual({"incomplete": 1}, report["validationSummary"])

    def test_changed_dependency_after_match_is_not_collected(self):
        dependency = self.root / "Model.fbx"
        dependency.write_bytes(b"original model")
        self.reference["dependencyFiles"] = {str(dependency): batch.sha256(dependency)}
        self.after_inspect = lambda: dependency.write_bytes(b"edited model")
        code, report = self.compare()
        self.assertEqual(1, code)
        self.assertIn("dependency changed", report["cases"][0]["error"])
        self.assertEqual([], list(Path(report["packageDirectory"]).iterdir()))

    def test_created_project_file_after_match_is_not_collected(self):
        manifest = self.root / "packages-lock.json"
        self.reference["projectFiles"] = {str(manifest): None}
        self.after_inspect = lambda: manifest.write_text("{}")
        code, report = self.compare()
        self.assertEqual(1, code)
        self.assertIn("configuration changed", report["cases"][0]["error"])
        self.assertEqual([], list(Path(report["packageDirectory"]).iterdir()))

    def test_missing_capture_is_incomplete(self):
        self.reference.update(status="failed", reason="Missing script")
        code, report = self.compare()
        self.assertEqual(1, code)
        self.assertEqual("incomplete", report["status"])
        self.assertEqual("Missing script", report["cases"][0]["unityComparison"]["reason"])
        self.assertEqual([], self.replayed)

    def test_wrong_input_cannot_pass_using_same_case_id(self):
        self.reference["inputPath"] = str(self.root / "different.unitypackage")
        code, report = self.compare()
        self.assertEqual(1, code)
        self.assertEqual("failed", report["cases"][0]["unityComparison"]["status"])
        self.assertEqual([], self.replayed)

    def test_unregistered_capture_is_not_a_match(self):
        code, report = self.run_batch([str(self.input)])
        self.assertEqual(1, code)
        self.assertEqual({"not-captured": 1}, report["unityComparisonSummary"])
        self.assertEqual("incomplete", report["status"])

    def test_unsupported_is_incomplete(self):
        self.parity = "unsupported"
        code, report = self.compare()
        self.assertEqual(1, code)
        self.assertEqual("incomplete", report["status"])


if __name__ == "__main__":
    unittest.main()
