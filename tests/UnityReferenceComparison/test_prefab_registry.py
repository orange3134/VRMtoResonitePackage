"""Registry batches must select prefab inputs and retain individual failures."""
import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
from test_prefab_capture import capture, REPO


class RegistryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=REPO / ".tmp_verify", prefix="prefab-registry-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.registry = self.root / "avatar-tests.json"
        capture.write(self.registry, dict(version=1, cases=[
            dict(id="first", inputPath="missing project/first.prefab", arguments=["--avatar", "First Variant"]),
            dict(id="package", inputPath="avatar.unitypackage"),
            dict(id="second", inputPath="missing project/second.prefab")]))
        self.original = self.registry.read_bytes()
        self.binary = self.root / "UnityReferenceDump.dll"
        self.binary.write_bytes(b"mock binary")
        self.output = self.root / "results"
        self.args = ["--registry", str(self.registry), "--reference-dump", str(self.binary), "--output", str(self.output)]
        self.invoked = []
        self.builds = 0
        self.outcomes = {}

    def invoke(self, command, log, timeout, env=None):
        if command[0] == "dotnet":
            self.builds += 1
            return 0
        key = command[command.index("--case-id") + 1]
        self.assertEqual(["--avatar=First Variant"] if key == "first" else [],
                         [arg for arg in command if arg.startswith("--avatar=")])
        self.invoked.append(key)
        outcome = self.outcomes.get(key, "matched")
        if outcome == "preflight-failed":
            return 2
        folder = Path(command[command.index("--output") + 1])
        folder.mkdir()
        report = dict(captureStatus="captured", parityStatus=outcome, status=outcome,
                      inputAndProjectFilesUnchanged=outcome != "changed")
        capture.write(folder / "report.json", report)
        (folder / "unity").mkdir()
        capture.write(folder / "unity/coverage.json", dict(cases=[dict(id=key, status="captured")]))
        (folder / "parity").mkdir()
        capture.write(folder / "parity/report.json", dict(cases=[dict(status=outcome, differences=2 if outcome == "different" else 0)]))
        return 0 if outcome == "matched" else 1

    def run_batch(self):
        with mock.patch.object(capture, "invoke", side_effect=self.invoke), contextlib.redirect_stdout(io.StringIO()):
            code = capture.main(self.args)
        report = capture.read(self.output / "report.json")
        work = Path(report["workPath"])
        self.addCleanup(lambda: work.rmdir())  # Mock build leaves this directory empty.
        self.assertEqual(self.original, self.registry.read_bytes())
        return code, report

    def test_selects_only_prefabs_in_registry_order(self):
        code, report = self.run_batch()
        self.assertEqual(0, code)
        self.assertEqual(["first", "second"], self.invoked)
        self.assertEqual({"matched": 2}, report["summary"])
        self.assertEqual(str((self.root / "missing project/first.prefab").resolve()), report["cases"][0]["inputPath"])

    def test_default_build_runs_once_for_multiple_cases(self):
        index = self.args.index("--reference-dump")
        del self.args[index:index + 2]
        code, _ = self.run_batch()
        self.assertEqual(0, code)
        self.assertEqual(1, self.builds)
        self.assertEqual(["first", "second"], self.invoked)

    def test_continues_after_preflight_failure_and_keeps_failed_case_in_coverage(self):
        self.outcomes["first"] = "preflight-failed"
        code, report = self.run_batch()
        self.assertEqual(1, code)
        self.assertEqual(["first", "second"], self.invoked)
        self.assertEqual({"preflight-failed": 1, "matched": 1}, report["summary"])
        self.assertEqual(["failed", "captured"], [c["status"] for c in capture.read(self.output / "coverage.json")["cases"]])

    def test_difference_and_producer_failure_preserve_unity_captures(self):
        self.outcomes.update(first="different", second="producer-failed")
        code, report = self.run_batch()
        self.assertEqual(1, code)
        self.assertEqual({"captured": 2}, report["captureSummary"])
        self.assertEqual({"different": 1, "producer-failed": 1}, report["summary"])
        self.assertTrue(all(c["status"] == "captured" for c in capture.read(self.output / "coverage.json")["cases"]))

    def test_changed_input_excluded_from_replayable_captures(self):
        self.outcomes["first"] = "changed"
        code, report = self.run_batch()
        self.assertEqual(1, code)
        self.assertEqual("failed", report["cases"][0]["status"])
        self.assertEqual("failed", capture.read(self.output / "coverage.json")["cases"][0]["status"])

    def test_case_selection(self):
        self.args += ["--case", "second"]
        code, report = self.run_batch()
        self.assertEqual(0, code)
        self.assertEqual(["second"], self.invoked)

    def test_invalid_selection_fails_before_execution(self):
        for key in ("package", "unknown"):
            with self.subTest(key=key), contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
                capture.main(self.args + ["--case", key])
            self.assertEqual(2, error.exception.code)
            self.assertFalse(self.output.exists())

    def test_cannot_mix_prefab_and_registry(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            capture.main(self.args + ["other.prefab"])


if __name__ == "__main__":
    unittest.main()
