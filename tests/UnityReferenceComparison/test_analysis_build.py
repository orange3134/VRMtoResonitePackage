import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


class AnalysisBuildTests(unittest.TestCase):
    def test_mixed_converter_builds_are_rejected_before_replay(self):
        repo = Path(__file__).resolve().parents[2]
        scratch = repo / ".tmp_verify"
        scratch.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as temp:
            root = Path(temp)
            baseline = root / "baseline"
            baseline.mkdir()
            manifest = baseline / "index.json"
            manifest.write_text(json.dumps({"format": "resopon.avatar-analysis", "schemaVersion": 1,
                "cases": [{"id": "example", "unity": {"status": "verified"}}]}))
            converter = root / "ResoPon.dll"
            converter.write_bytes(b"build A")
            (root / "producer").mkdir()
            dump = root / "producer/UnityReferenceDump.dll"
            dump.write_bytes(b"producer")
            dump.with_name("ResoPon.dll").write_bytes(b"build B")
            output = root / "result"
            run = subprocess.run([sys.executable, "-B", str(repo / "scripts/test-avatar-analysis.py"),
                str(manifest), "--converter", str(converter), "--reference-dump", str(dump),
                "--output", str(output)], capture_output=True)
            self.assertEqual(2, run.returncode)
            self.assertIn(b"--converter must match", run.stderr)
            self.assertFalse(output.exists())
