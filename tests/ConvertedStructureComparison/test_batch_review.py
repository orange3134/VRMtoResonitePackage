import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock
from test_structure import fixtures

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('batch_review', ROOT / 'scripts/check-converted-batch.py')
batch = importlib.util.module_from_spec(spec)
spec.loader.exec_module(batch)


class BatchReviewTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=ROOT / '.tmp_verify')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / 'avatar.unitypackage'
        self.source.write_bytes(b'self authored source')
        self.package = self.root / 'avatar.resonitepackage'
        self.package.write_bytes(b'self authored output')
        unity, self.converted = fixtures()
        self.converted['packageSha256'] = batch.comparison.sha256(self.package)
        self.expected, self.actual, self.coverage = [self.root / name for name in ('expected.json', 'actual.json', 'coverage.json')]
        for p in (self.expected, self.actual): batch.write(p, unity)
        self.case = dict(caseId='example', status='passed', inputPath=str(self.source),
                         inputSha256=batch.comparison.sha256(self.source), outputPackage=str(self.package),
                         packageSha256=self.converted['packageSha256'])
        baseline = dict(id='example', inputPath=str(self.source), inputSha256=self.case['inputSha256'],
                        expectedSha256=batch.comparison.sha256(self.expected))
        batch.write(self.coverage, dict(cases=[baseline]))
        self.case['unityComparison'] = dict(status='matched', expectedPath=str(self.expected),
            expectedSha256=baseline['expectedSha256'], actualPath=str(self.actual), actualSha256=batch.comparison.sha256(self.actual),
            inputPath=str(self.source), inputSha256=self.case['inputSha256'], sourceCoveragePath=str(self.coverage),
            sourceCoverageSha256=batch.comparison.sha256(self.coverage))

    def check(self):
        def capture(command, **kwargs):
            batch.write(Path(command[-1]), self.converted)
            return subprocess.CompletedProcess(command, 0)
        with mock.patch.object(batch.subprocess, 'run', side_effect=capture):
            return batch.check_case(self.case, self.root / 'output', self.root / 'dump.dll', 10)

    def test_capture_and_comparison_keep_unmapped_root_as_review(self):
        result = self.check()
        self.assertEqual('captured', result['captureStatus'])
        self.assertEqual('needs-review', result['status'])
        self.assertEqual(1, result['summary']['unmappedSources'])

    def test_uncaptured_unity_still_dumps_but_never_matches(self):
        self.case['unityComparison'] = dict(status='not-captured', reason='Missing script')
        result = self.check()
        self.assertEqual('captured', result['captureStatus'])
        self.assertEqual('not-compared', result['status'])
        self.assertEqual('Missing script', result['unityReason'])

    def test_external_dependency_scope_cannot_prove_archive_conversion(self):
        self.case['unityComparison']['dependencyScope'] = 'archive plus project dependencies'
        result = self.check()
        self.assertEqual('captured', result['captureStatus'])
        self.assertEqual('not-compared', result['status'])

    def test_null_bones_are_reported_even_without_unity_baseline(self):
        self.case['unityComparison'] = dict(status='not-captured')
        component = self.converted['slots'][-1]['components'][0]
        component['bones'][0] = dict(target=None, status='null', serializedId=None)
        component['slotReferences'] = component['slotReferences'][1:]
        result = self.check()
        self.assertEqual('captured', result['captureStatus'])
        self.assertEqual({'null': 1, 'resolved': 1}, result['boneReferenceStatuses'])
        self.assertEqual('not-compared', result['status'])

    def test_changed_package_fails_before_capture(self):
        self.package.write_bytes(b'changed')
        result = self.check()
        self.assertEqual('failed', result['status'])
        self.assertEqual('not-run', result['captureStatus'])

    def test_changed_baseline_is_not_compared(self):
        self.expected.write_bytes(b'changed')
        result = self.check()
        self.assertEqual('failed', result['status'])
        self.assertIn('Unity baseline', result['error'])

    def test_changed_coverage_is_not_compared(self):
        self.coverage.write_bytes(b'changed')
        result = self.check()
        self.assertEqual('failed', result['status'])
        self.assertIn('Coverage manifest', result['error'])

    def test_conversion_failure_is_not_inspection_success(self):
        self.case.update(status='failed', error='inspect failed')
        result = self.check()
        self.assertEqual('not-run', result['captureStatus'])
        self.assertEqual('not-compared', result['status'])
