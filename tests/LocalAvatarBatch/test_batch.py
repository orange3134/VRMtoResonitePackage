"""Exercise registry inputs across the batch runner's per-case working directories."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("avatar_batch", REPO / "scripts/convert-all-local-avatars.py")
batch = importlib.util.module_from_spec(spec)
spec.loader.exec_module(batch)


class BatchInputTests(unittest.TestCase):
    def setUp(self):
        (REPO / ".tmp_verify").mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=REPO / ".tmp_verify", prefix="batch-path-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.repo = self.root / "repo"
        (self.repo / ".local").mkdir(parents=True)
        self.caller = self.root / "caller"
        self.caller.mkdir()
        self.input = self.caller / "avatar with spaces 日本語.vrm"
        self.input.write_bytes(b"self-authored fixture")
        self.converted = []

    def invoke(self, command, *, cwd, **kwargs):
        if command[1] == "build":
            output = Path(command[command.index("-o") + 1])
            output.mkdir()
            (output / "ResoPon.dll").write_bytes(b"test converter")
        elif command[2] == "--inspect":
            self.assertEqual(b"package fixture", (Path(cwd) / command[3]).read_bytes())
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

    def run_batch(self, paths):
        registry = self.repo / ".local/avatar-tests.json"
        batch.write_json(registry, {"version": 1, "cases": [
            {"id": f"case-{index}", "inputPath": path, "arguments": []}
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

    def test_relative_input_uses_caller_directory_for_hash_and_conversion(self):
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


if __name__ == "__main__":
    unittest.main()
