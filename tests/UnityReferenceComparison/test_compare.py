"""Synthetic tests only; no avatar assets or Unity installation required."""
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'scripts' / 'compare-unity-reference.py'
spec = importlib.util.spec_from_file_location('comparison', SCRIPT)
comparison = importlib.util.module_from_spec(spec)
spec.loader.exec_module(comparison)


def snapshot():
    return {'format': comparison.FORMAT, 'schemaVersion': 1,
            'prefabGuid': '00000000000000000000000000000001', 'projection': comparison.PROJECTION, 'sections': {
                'renderers': [{'id': '1001',
                    'path': 'Body', 'type': 'UnityEngine.SkinnedMeshRenderer',
                    'activeSelf': True, 'activeInHierarchy': True,
                    'mesh': {'guid': '00000000000000000000000000000003', 'fileId': '4300000'},
                    'materials': [{'guid': '00000000000000000000000000000002', 'fileId': '2100000'}]}],
                'physBones': [{'id': '2001', 'path': 'Hair',
                    'type': 'VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone', 'enabled': True, 'activeInHierarchy': True,
                    'root': '2000', 'colliders': ['3001']}],
                'colliders': [{'id': '3001', 'path': 'Head',
                    'type': 'VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider', 'enabled': True,
                    'activeInHierarchy': True, 'root': '3000'}]}}


class CompareTests(unittest.TestCase):
    def test_empty_capture_rejected(self):
        empty = snapshot()
        empty["sections"] = {key: [] for key in comparison.SECTIONS}
        with self.assertRaisesRegex(ValueError, "no comparable"):
            comparison.compare(empty, empty)

    def test_missing_mesh_rejected(self):
        data = snapshot()
        del data["sections"]["renderers"][0]["mesh"]
        with self.assertRaises(ValueError):
            comparison.validate_snapshot(data)

    def test_large_file_id_is_exact(self):
        left, right = snapshot(), snapshot()
        left["sections"]["renderers"][0]["mesh"]["fileId"] = "9223372036854775806"
        right["sections"]["renderers"][0]["mesh"]["fileId"] = "9223372036854775807"
        self.assertTrue(comparison.compare(left, right))

    def test_wrong_projection_rejected(self):
        data = snapshot()
        data["projection"] = "all-imported-components"
        with self.assertRaises(ValueError):
            comparison.validate_snapshot(data)

    def test_identical(self):
        self.assertEqual([], comparison.compare(snapshot(), snapshot()))

    def test_record_order_independent(self):
        left = snapshot()
        record = copy.deepcopy(left['sections']['renderers'][0])
        record['id'] = '1002'
        left['sections']['renderers'].append(record)
        right = copy.deepcopy(left)
        right['sections']['renderers'].reverse()
        self.assertEqual([], comparison.compare(left, right))

    def test_identity_not_name(self):
        right = snapshot()
        right['sections']['renderers'][0]['id'] = '9999'
        result = comparison.compare(snapshot(), right)
        self.assertEqual(2, len(result))
        self.assertTrue(any('missing' in x for x in result))
        self.assertTrue(any('unexpected' in x for x in result))

    def test_material_slots_ordered(self):
        left = snapshot()
        left['sections']['renderers'][0]['materials'].append({'guid': '00000000000000000000000000000004', 'fileId': '2100000'})
        right = copy.deepcopy(left)
        right['sections']['renderers'][0]['materials'].reverse()
        self.assertEqual(2, len(comparison.compare(left, right)))

    def test_disabled_mismatch(self):
        right = snapshot()
        right['sections']['physBones'][0]['enabled'] = False
        self.assertIn('.enabled:', comparison.compare(snapshot(), right)[0])

    def test_missing_collection_fails(self):
        right = snapshot()
        del right['sections']['colliders']
        with self.assertRaises(ValueError):
            comparison.compare(snapshot(), right)

    def test_duplicate_record_fails(self):
        right = snapshot()
        right['sections']['colliders'] *= 2
        with self.assertRaisesRegex(ValueError, 'duplicate id'):
            comparison.compare(snapshot(), right)

    def test_numeric_file_id_rejected(self):
        right = snapshot()
        right['sections']['renderers'][0]['materials'][0]['fileId'] = 2100000
        with self.assertRaises(ValueError):
            comparison.compare(snapshot(), right)

    def test_float_tolerance(self):
        left, right = snapshot(), snapshot()
        left['sections']['physBones'][0]['futureNumericField'] = 0.2
        right['sections']['physBones'][0]['futureNumericField'] = 0.20000001
        self.assertEqual([], comparison.compare(left, right))
        self.assertTrue(comparison.compare(left, right, 0, 0))

    def test_bool_is_not_number(self):
        right = snapshot()
        right['sections']['physBones'][0]['enabled'] = 1
        with self.assertRaises(ValueError):
            comparison.compare(snapshot(), right)

    def test_unknown_field_not_ignored(self):
        right = snapshot()
        right['sections']['physBones'][0]['futureValue'] = 1
        self.assertTrue(comparison.compare(snapshot(), right))

    def test_cli_success_failure_and_invalid(self):
        with tempfile.TemporaryDirectory() as directory:
            expected, actual = [Path(directory) / name for name in ('expected.json', 'actual.json')]
            expected.write_text(json.dumps(snapshot()), encoding='utf-8')
            actual.write_text(json.dumps(snapshot()), encoding='utf-8')
            def run(*args):
                return subprocess.run([sys.executable, str(SCRIPT), str(expected), str(actual), *args], capture_output=True, text=True)
            self.assertEqual(0, run().returncode)
            changed = snapshot()
            changed['sections']['colliders'] = []
            actual.write_text(json.dumps(changed), encoding='utf-8')
            result = run()
            self.assertEqual(1, result.returncode)
            self.assertIn('missing in actual', result.stdout)
            actual.write_text('{', encoding='utf-8')
            self.assertEqual(2, run().returncode)

    def test_duplicate_json_property_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'input.json'
            path.write_text('{"a":1,"a":2}', encoding='utf-8')
            with self.assertRaisesRegex(ValueError, 'duplicate JSON'):
                comparison.load_snapshot(path)

    def test_report_cannot_overwrite_input(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'input.json'
            data = json.dumps(snapshot())
            path.write_text(data, encoding='utf-8')
            result = subprocess.run([sys.executable, str(SCRIPT), str(path), str(path), '--report', str(path)], capture_output=True)
            self.assertEqual(2, result.returncode)
            self.assertEqual(data, path.read_text(encoding='utf-8'))

    def test_report_cannot_overwrite_hardlinked_input(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'input.json'
            path.write_text(json.dumps(snapshot()), encoding='utf-8')
            alias = path.with_name('report.json')
            alias.hardlink_to(path)
            before = path.read_bytes()
            result = subprocess.run([sys.executable, str(SCRIPT), str(path), str(path), '--report', str(alias)], capture_output=True)
            self.assertEqual(2, result.returncode)
            self.assertEqual(before, path.read_bytes())

    def test_invalid_tolerance_rejected(self):
        result = subprocess.run([sys.executable, str(SCRIPT), 'unused', 'unused', '--abs-tol', 'nan'], capture_output=True)
        self.assertEqual(2, result.returncode)


if __name__ == '__main__':
    unittest.main()
