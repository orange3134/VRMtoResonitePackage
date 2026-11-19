"""Synthetic structures only; no avatar assets or engine required."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('structure', ROOT / 'scripts/compare-converted-structure.py')
comparison = importlib.util.module_from_spec(spec)
spec.loader.exec_module(comparison)
spec = importlib.util.spec_from_file_location('runner', ROOT / 'scripts/check-converted-avatar.py')
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


def fixtures(names=('Bone', 'Mesh')):
    reference = {'guid': 'a' * 32, 'fileId': '1'}
    transforms, slots = [], []
    for index, name in enumerate(('Avatar', *names)):
        sid = 'r' if index == 0 else 'r/' + str(index - 1)
        parent = '' if index == 0 else 'r'
        transforms.append(dict(id=sid, parent=parent, name=name, path='' if not parent else name,
                               type='UnityEngine.Transform', source=reference.copy(), originalSource=reference.copy(),
                               siblingIndex=max(0, index - 1), activeSelf=True, activeInHierarchy=True,
                               localPosition=dict(x=0., y=0., z=0.), localRotation=dict(x=0., y=0., z=0., w=1.),
                               localScale=dict(x=1., y=1., z=1.)))
        slots.append(dict(id=sid, parent=parent or None, siblingIndex=max(0, index - 1),
                          path=['Avatar', name] if parent else ['Avatar'], serializedId='saved:' + sid,
                          tag='', activeSelf=True, activeInHierarchy=True, localPosition=[0, 0, 0],
                          localRotation=[0, 0, 0, 1], localScale=[1, 1, 1], components=[]))
    renderer = dict(id='r/1/c:UnityEngine.SkinnedMeshRenderer:0', transform='r/1', path=names[1],
                    type='UnityEngine.SkinnedMeshRenderer', source=reference.copy(), originalSource=reference.copy(),
                    enabled=True, activeSelf=True, activeInHierarchy=True, mesh=reference.copy(), materials=[],
                    rootBone='r/0', bones=['r/0', 'r/1'], blendShapes=[])
    refs = [dict(target=sid, status='resolved', serializedId='saved:' + sid) for sid in ['r/0', 'r/1']]
    slots[-1]['components'] = [dict(id='r/1:0', type='[FrooxEngine]FrooxEngine.SkinnedMeshRenderer',
                                   enabled=True, bones=refs, rigBones=None, blendShapeWeights=[],
                                   slotReferences=[dict(field='Bones[' + str(i) + ']', target=r['target']) for i, r in enumerate(refs)])]
    unity = dict(format='resopon.unity-reference', schemaVersion=2, projection='unity-resolved-prefab',
                 prefabGuid='b' * 32, sections=dict(transforms=transforms, renderers=[renderer], physBones=[], colliders=[]))
    converted = dict(format='resopon.converted-structure', schemaVersion=1, packageSha256='a' * 64, slots=slots)
    unity['skinSizes'] = [dict(rendererId=renderer['id'], weightedVertices=3, sizeMeters=dict(x=.3, y=.1, z=.2))]
    slots[-1]['components'][0]['skinSize'] = dict(weightedVertices=4, sizeMeters=[.1, .2, .3])
    return unity, converted


def remap_slots(converted, changes):
    for slot in converted['slots']:
        slot['id'] = changes.get(slot['id'], slot['id'])
        slot['parent'] = changes.get(slot['parent'], slot['parent'])
        slot['siblingIndex'] = int(slot['id'].split('/')[-1]) if slot['parent'] else 0
        for index, component in enumerate(slot['components']):
            component['id'] = slot['id'] + ':' + str(index)
            for ref in component['slotReferences'] + (component['bones'] or []):
                ref['target'] = changes.get(ref['target'], ref['target'])
    converted['slots'].sort(key=lambda slot: tuple(int(i) for i in slot['id'].split('/')[1:]))


class ComparisonTests(unittest.TestCase):
    def test_root_requires_reviewed_mapping_and_only_covered_fields_match(self):
        unity, converted = fixtures()
        self.assertEqual('needs-review', comparison.compare(unity, unity, converted)['status'])
        report = comparison.compare(unity, unity, converted, {'r': 'r'})
        self.assertEqual('matched-covered-fields', report['status'])
        self.assertIn('not compared', report['limitations'][1])

    def test_scale_detects_explosion_with_identical_bones(self):
        unity, converted = fixtures()
        parser = copy.deepcopy(unity)
        del parser['skinSizes']
        component = converted['slots'][-1]['components'][0]
        for size, status in (([20, 8, 3], 'expanded'), ([.001] * 3, 'shrunk'),
                             ([.1, .2, .3], 'no-scale-outlier')):
            component['skinSize']['sizeMeters'] = size
            report = comparison.compare(unity, parser, converted, {'r': 'r'})
            self.assertEqual('matched', report['parserParity']['status'])
            self.assertEqual({status: 1}, report['summary']['skinScale'])
            self.assertEqual([] if status == 'no-scale-outlier' else ['skin-scale'],
                             [diff['kind'] for diff in report['differences']])
        for missing in ('unity', 'converted', 'weighted bone'):
            u, c = fixtures()
            if missing == 'unity':
                del u['skinSizes']
            elif missing == 'converted':
                del c['slots'][-1]['components'][0]['skinSize']
            else:
                c['slots'][-1]['components'][0]['skinSize']['reason'] = 'missing weighted bone'
            report = comparison.compare(u, parser, c, {'r': 'r'})
            self.assertEqual({'unverified': 1}, report['summary']['skinScale'])
            self.assertEqual('needs-review', report['status'])

    def test_duplicate_source_names_are_not_matched(self):
        unity, converted = fixtures(('Twin', 'Twin'))
        report = comparison.compare(unity, unity, converted)
        self.assertEqual(['ambiguous', 'ambiguous'], [r['status'] for r in report['transforms'][1:]])

    def test_duplicate_output_names_are_not_first_match(self):
        unity, converted = fixtures()
        extra = copy.deepcopy(converted['slots'][1])
        extra.update(id='r/2', siblingIndex=2, serializedId='extra')
        converted['slots'].append(extra)
        report = comparison.compare(unity, unity, converted)
        self.assertEqual('ambiguous', report['transforms'][1]['status'])
        self.assertEqual(['r/0', 'r/2'], report['transforms'][1]['candidates'])

    def test_bone_order_and_repeated_entries_preserved(self):
        unity, converted = fixtures()
        converted['slots'][-1]['components'][0]['bones'].reverse()
        report = comparison.compare(unity, unity, converted, {'r': 'r'})
        self.assertEqual(2, sum(d['kind'] == 'bone-target' for d in report['differences']))

    def test_sibling_order_is_separate_from_skin_indices(self):
        unity, converted = fixtures()
        remap_slots(converted, {'r/0': 'r/1', 'r/1': 'r/0'})
        report = comparison.compare(unity, unity, converted, {'r': 'r'})
        self.assertEqual(['sibling-order'], [d['kind'] for d in report['differences']])
        self.assertEqual(['r/1', 'r/0'], report['siblingOrders'][0]['actual'])

    def test_helper_branch_order_and_missing_child(self):
        unity, converted = fixtures()
        helper = copy.deepcopy(converted['slots'][1])
        helper.update(serializedId='helper', path=['Avatar', 'Pivot'])
        bone = converted['slots'][1]
        bone.update(id='r/0/0', parent='r/0', path=['Avatar', 'Pivot', 'Bone'], siblingIndex=0)
        converted['slots'].insert(1, helper)
        skin = converted['slots'][-1]['components'][0]
        skin['bones'][0]['target'] = 'r/0/0'
        skin['slotReferences'][0]['target'] = 'r/0/0'
        mapping = {'r': 'r', 'r/0': 'r/0/0'}
        report = comparison.compare(unity, unity, converted, mapping)
        self.assertEqual('matched', report['siblingOrders'][0]['status'])
        remap_slots(converted, {'r/0': 'r/2', 'r/0/0': 'r/2/0'})
        mapping['r/0'] = 'r/2/0'
        report = comparison.compare(unity, unity, converted, mapping)
        self.assertEqual('different', report['siblingOrders'][0]['status'])
        next(s for s in converted['slots'] if s['path'][-1] == 'Mesh')['path'][-1] = 'Unknown'
        report = comparison.compare(unity, unity, converted, mapping)
        self.assertEqual('unverified', report['siblingOrders'][0]['status'])

    def test_null_and_unresolved_bones_are_distinct(self):
        unity, converted = fixtures()
        bones = converted['slots'][-1]['components'][0]['bones']
        bones[:] = [dict(target=None, status='null', serializedId=None),
                    dict(target=None, status='unresolved', serializedId='missing')]
        report = comparison.compare(unity, unity, converted)
        self.assertEqual(['null', 'unresolved'], [r['status'] for r in report['unresolvedOrNullBones']])

    def test_parser_differences_not_hidden_by_conversion(self):
        unity, converted = fixtures()
        parser = copy.deepcopy(unity)
        parser['sections']['transforms'][1]['localPosition']['x'] = 1.
        report = comparison.compare(unity, parser, converted, {'r': 'r'})
        self.assertEqual('different', report['parserParity']['status'])
        self.assertEqual('needs-review', report['status'])

    def test_numeric_transform_changes_are_observations_not_cross_space_assertions(self):
        unity, converted = fixtures()
        converted['slots'][1]['localPosition'] = [10, 0, 0]
        report = comparison.compare(unity, unity, converted, {'r': 'r'})
        self.assertEqual([], report['differences'])
        self.assertEqual([10, 0, 0], report['transforms'][1]['converted']['localPosition'])

    def test_malformed_hierarchy_and_references_rejected(self):
        for kind in ('parent', 'nan', 'target', 'serialized'):
            unity, converted = fixtures()
            if kind == 'parent':
                converted['slots'][1]['parent'] = 'r/1'
            elif kind == 'nan':
                converted['slots'][1]['localScale'][0] = float('nan')
            elif kind == 'target':
                converted['slots'][-1]['components'][0]['bones'][0]['target'] = 'r/99'
            else:
                converted['slots'][1]['serializedId'] = converted['slots'][0]['serializedId']
            with self.subTest(kind=kind), self.assertRaises(ValueError):
                comparison.compare(unity, unity, converted)

    def test_subtrees_do_not_double_count_across_mapped_nodes(self):
        unity, converted = fixtures()
        converted['slots'].append(dict(id='r/0/0', parent='r/0', siblingIndex=0, path=['Avatar', 'Bone', 'Helper'],
                                       serializedId='extra', tag='', activeSelf=True, activeInHierarchy=True,
                                       localPosition=[0, 0, 0], localRotation=[0, 0, 0, 1], localScale=[1, 1, 1], components=[]))
        report = comparison.compare(unity, unity, converted)
        self.assertEqual(2, sum(r['unmappedSlotCount'] for r in report['unmappedSubtrees']))

    def test_mapping_conflicts_do_not_silently_reuse_slot(self):
        unity, converted = fixtures()
        report = comparison.compare(unity, unity, converted, {'r': 'r/0'})
        self.assertEqual(['ambiguous', 'ambiguous'], [r['status'] for r in report['transforms'][:2]])

    def test_cli_preserves_reports_and_rejects_stale_mapping(self):
        unity, converted = fixtures()
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            for name, data in [('unity', unity), ('parser', unity), ('converted', converted)]:
                (work / (name + '.json')).write_text(json.dumps(data), encoding='utf-8')
            arguments = [str(work / (name + '.json')) for name in ('unity', 'parser', 'converted')]
            arguments += ['--report', str(work / 'report.json')]
            self.assertEqual(1, comparison.main(arguments))
            original = (work / 'report.json').read_bytes()
            self.assertEqual(2, comparison.main(arguments))
            self.assertEqual(original, (work / 'report.json').read_bytes())
            (work / 'mapping.json').write_text(json.dumps(dict(unitySha256='0' * 64,
                packageSha256=converted['packageSha256'], transforms={'r': 'r'})), encoding='utf-8')
            self.assertEqual(2, comparison.main(arguments + ['--mapping', str(work / 'mapping.json')]))


class RunnerTests(unittest.TestCase):
    def run_case(self, scenario):
        unity, converted = fixtures()
        temporary_root = ROOT / '.tmp_verify'
        temporary_root.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=temporary_root) as directory:
            work = Path(directory)
            source, expected, coverage = work / 'input.unitypackage', work / 'unity.json', work / 'coverage.json'
            source.write_bytes(b'synthetic package; engine is mocked')
            expected.write_text(json.dumps(unity), encoding='utf-8')
            case = dict(id='synthetic', status='captured', inputPath=str(source), expectedPath=str(expected),
                        inputSha256=comparison.sha256(source), expectedSha256=comparison.sha256(expected),
                        prefabGuid=unity['prefabGuid'], conversionArguments=[])
            coverage.write_text(json.dumps(dict(format='resopon.unity-resolved-coverage', schemaVersion=1,
                                                cases=[case])), encoding='utf-8')
            output = work / 'run'
            calls = []

            def execute(command, **kwargs):
                calls.append(command)
                if command[1] == 'build':
                    build = Path(command[-1])
                    build.mkdir(exist_ok=True)
                    for name in ('ResoPon', 'UnityReferenceDump', 'PackageRegressionDump'):
                        (build / (name + '.dll')).write_bytes(b'synthetic binary')
                elif Path(command[1]).name == 'UnityReferenceDump.dll':
                    if scenario == 'parser-failure':
                        return SimpleNamespace(returncode=1)
                    Path(command[4]).write_text(json.dumps(unity), encoding='utf-8')
                elif Path(command[1]).name == 'ResoPon.dll' and command[2] != '--inspect':
                    target = Path(command[-1])
                    target.mkdir()
                    (target / 'avatar.resonitepackage').write_bytes(b'converted package')
                    if scenario == 'input-changed':
                        source.write_bytes(b'changed during conversion')
                elif Path(command[1]).name == 'PackageRegressionDump.dll':
                    converted['packageSha256'] = comparison.sha256(command[3])
                    Path(command[4]).write_text(json.dumps(converted), encoding='utf-8')
                return SimpleNamespace(returncode=0)

            with mock.patch.object(runner.subprocess, 'run', side_effect=execute), \
                    mock.patch.object(runner.subprocess, 'check_output', return_value='synthetic-head\n'):
                code = runner.main([str(coverage), '--case', 'synthetic', '--output', str(output)])
            return code, json.loads((output / 'report.json').read_text(encoding='utf-8')), calls

    def test_parser_failure_still_captures_but_never_passes(self):
        code, report, calls = self.run_case('parser-failure')
        self.assertEqual(1, code)
        self.assertEqual('failed', report['status'])
        self.assertEqual('failed', report['stages']['parserParity'])
        self.assertEqual('captured', report['stages']['capture'])
        self.assertEqual('needs-review', report['stages']['structureComparison'])
        self.assertEqual({'no-scale-outlier': 1}, report['summary']['skinScale'])
        self.assertTrue(any('--inspect' in command for command in calls))

    def test_input_changes_during_conversion_invalidate_successful_stages(self):
        code, report, _ = self.run_case('input-changed')
        self.assertEqual(1, code)
        self.assertEqual('failed', report['status'])
        self.assertEqual('captured', report['stages']['capture'])
        self.assertTrue(any('input changed' in e for e in report['errors']))


if __name__ == '__main__':
    unittest.main()
