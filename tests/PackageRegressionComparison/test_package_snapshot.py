import copy
import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[2] / "scripts/check-package-snapshot.py"
spec = importlib.util.spec_from_file_location("package_check", SCRIPT)
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)
R = check.RENDERER
CHAIN = "[FrooxEngine]FrooxEngine.DynamicBoneChain"


class PackageComparisonTests(unittest.TestCase):
    def setUp(self):
        self.snapshot = {"format": "resopon.package-snapshot", "schemaVersion": 1,
                         "packageSha256": "a" * 64, "slots": [
            {"id": "r", "path": ["Avatar"], "tag": "", "activeSelf": True, "activeInHierarchy": True,
             "components": []},
            {"id": "r/0", "path": ["Avatar", "socks"], "tag": "", "activeSelf": True,
             "activeInHierarchy": True, "components": [{"type": R, "blendShapeWeights": [0, 0.5]}]},
            {"id": "r/1", "path": ["Avatar", "catToy"], "tag": "", "activeSelf": False,
             "activeInHierarchy": False, "components": []},
            {"id": "r/2", "path": ["Avatar", "chain"], "tag": "avatar-chain", "activeSelf": True,
             "activeInHierarchy": True, "components": [{"type": CHAIN, "blendShapeWeights": None}]},
            {"id": "r/3", "path": ["Avatar", "template"], "tag": "", "activeSelf": True,
             "activeInHierarchy": True, "components": [{"type": CHAIN, "blendShapeWeights": None}]}]}
        self.expected = {"format": "resopon.package-expectations", "schemaVersion": 1,
                         "componentCounts": {R: {"min": 1, "max": 1}}, "slotRules": [
            {"selector": {"pathSuffix": ["socks"]}, "count": 1,
             "weights": [{"componentIndex": 0, "index": 0, "value": 0}]},
            {"selector": {"pathSuffix": ["catToy"]}, "count": 1, "activeSelf": False},
            {"selector": {"tag": "avatar-chain", "componentType": CHAIN}, "count": 1}]}

    def result(self):
        return check.compare(self.snapshot, self.expected)

    def test_matches_and_excludes_template_chain(self):
        self.assertEqual(self.result()["status"], "matched")
        self.assertEqual(self.result()["componentCounts"][CHAIN], 2)

    def test_old_nonzero_weight_regresses(self):
        self.snapshot["slots"][1]["components"][0]["blendShapeWeights"][0] = 1
        self.assertEqual(self.result()["status"], "different")

    def test_missing_weight_and_renderer_regress(self):
        for components in ([], [{"type": R, "blendShapeWeights": []}]):
            with self.subTest(components=components):
                self.snapshot["slots"][1]["components"] = components
                self.assertEqual(self.result()["status"], "different")

    def test_active_state_regresses(self):
        self.snapshot["slots"][2].update(activeSelf=True, activeInHierarchy=True)
        self.assertEqual(self.result()["status"], "different")

    def test_duplicate_display_name_cannot_silently_pass(self):
        slot = copy.deepcopy(self.snapshot["slots"][1]); slot["id"] = "r/4"
        self.snapshot["slots"].append(slot)
        self.assertEqual(self.result()["status"], "different")

    def test_path_segments_are_not_split_at_slash(self):
        self.snapshot["slots"][1]["path"][-1] = "outfit/socks"
        self.assertEqual(self.result()["status"], "different")

    def test_missing_slot_regresses(self):
        del self.snapshot["slots"][2]
        self.assertEqual(self.result()["status"], "different")

    def test_exact_component_type(self):
        self.snapshot["slots"][3]["components"][0]["type"] = "[Other]FrooxEngine.DynamicBoneChain"
        self.assertEqual(self.result()["status"], "different")

    def test_hierarchy_consistency(self):
        self.snapshot["slots"][2]["activeInHierarchy"] = True
        with self.assertRaises(ValueError): self.result()

    def test_bad_snapshot_identity_and_weights(self):
        original = copy.deepcopy(self.snapshot)
        for change in (lambda s: s.update(schemaVersion=True), lambda s: s.update(packageSha256="oops"),
                       lambda s: s["slots"][1].update(id="r"),
                       lambda s: s["slots"][1].update(path=["wrong", "socks"]),
                       lambda s: s["slots"][1]["components"][0].update(blendShapeWeights=[float("nan")])):
            self.snapshot = copy.deepcopy(original); change(self.snapshot)
            with self.assertRaises(ValueError): self.result()

    def test_bad_rules_are_errors_not_passes(self):
        original = copy.deepcopy(self.expected)
        for change in (lambda e: e.update(componentCounts={}, slotRules=[]),
                       lambda e: e["slotRules"][0].update(count=True),
                       lambda e: e["slotRules"][0].update(selector={}),
                       lambda e: e["slotRules"][0].update(activeSelf="false"),
                       lambda e: e["slotRules"][0]["weights"][0].update(index=-1),
                       lambda e: e["slotRules"][0]["weights"][0].update(absoluteTolerance=float("inf")),
                       lambda e: e["componentCounts"].update({R: {"min": 2, "max": 1}}),
                       lambda e: e["slotRules"][0].update(weigths=[])):
            self.expected = copy.deepcopy(original); change(self.expected)
            with self.assertRaises(ValueError): self.result()

    def test_explicit_absolute_weight_tolerance(self):
        self.snapshot["slots"][1]["components"][0]["blendShapeWeights"][0] = 0.0001
        self.assertEqual(self.result()["status"], "different")
        self.expected["slotRules"][0]["weights"][0]["absoluteTolerance"] = 0.001
        self.assertEqual(self.result()["status"], "matched")

    def test_cli_exit_codes_and_no_overwrite(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); snapshot = root / "snapshot.json"; expected = root / "expected.json"
            snapshot.write_text(json.dumps(self.snapshot), encoding="utf-8")
            expected.write_text(json.dumps(self.expected), encoding="utf-8")
            command = [sys.executable, "-B", str(SCRIPT), str(snapshot), str(expected)]
            def run(extra=()): return subprocess.run(command + list(extra), capture_output=True).returncode
            self.assertEqual(run(), 0)
            before = snapshot.read_bytes()
            self.assertEqual(run(["--report", str(snapshot)]), 2)
            self.assertEqual(snapshot.read_bytes(), before)
            self.expected["slotRules"][0]["count"] = 2
            expected.write_text(json.dumps(self.expected), encoding="utf-8")
            self.assertEqual(run(), 1)
            for invalid in ('{"format": 1, "format": 2}', '{"number": NaN}', '{"number": 1e999}'):
                snapshot.write_text(invalid, encoding="utf-8")
                self.assertEqual(run(), 2)


if __name__ == "__main__":
    unittest.main()
