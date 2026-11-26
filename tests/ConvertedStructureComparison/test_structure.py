"""Synthetic structures only; no avatar assets or engine required."""
import copy
import hashlib
import importlib.util
import json
import sys
from pathlib import Path
import tempfile
import unittest
from unittest import mock
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
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
                    activeSelf=True, activeInHierarchy=True, mesh=reference.copy(), materials=[],
                    rootBone='r/0', bones=['r/0', 'r/1'], blendShapes=[])
    refs = [dict(target=sid, status='resolved', serializedId='saved:' + sid) for sid in ['r/0', 'r/1']]
    slots[-1]['components'] = [dict(id='r/1:0', type='[FrooxEngine]FrooxEngine.SkinnedMeshRenderer',
                                   enabled=True, bones=refs, rigBones=None, blendShapeWeights=[],
                                   slotReferences=[dict(field='Bones[' + str(i) + ']', target=r['target']) for i, r in enumerate(refs)])]
    unity = dict(format='resopon.unity-reference', schemaVersion=2, projection='unity-resolved-prefab',
                 prefabGuid='b' * 32, sections=dict(transforms=transforms, renderers=[renderer], physBones=[], colliders=[]))
    converted = dict(format='resopon.converted-structure', schemaVersion=1, packageSha256='a' * 64, slots=slots)
    unity['skinSizes'] = [dict(rendererId=renderer['id'], weightedVertices=3, sizeMeters=dict(x=.3, y=.1, z=.2))]
    palette = dict(names=['First', 'Second'], used=[0, 1])
    unity['skinBindings'] = [dict(rendererId=renderer['id'], **copy.deepcopy(palette))]
    slots[-1]['components'][0]['skinSize'] = dict(weightedVertices=4, sizeMeters=[.1, .2, .3], bindings=palette)
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
    def test_builtin_sphere_requires_the_referenced_provider_and_radius(self):
        unity, converted = fixtures()
        skin, = unity['sections']['renderers']
        skin.update(type='UnityEngine.MeshRenderer', id='r/1/c:UnityEngine.MeshRenderer:0',
                    mesh={'guid': '0000000000000000e000000000000000', 'fileId': '10207'}, bones=[], rootBone='')
        del unity['skinSizes'], unity['skinBindings']
        component, = converted['slots'][-1]['components']
        component.update(type='[FrooxEngine]FrooxEngine.MeshRenderer', bones=None, blendShapeWeights=None)
        def check():
            return comparison.compare(unity, unity, converted, {'r/1': 'r/1'})
        self.assertIn('builtin-sphere', [item['kind'] for item in check()['unverified']])
        with mock.patch.dict(component, sphereRadius=.5):
            self.assertEqual([], check()['differences'])
        with mock.patch.dict(component, sphereRadius=None):
            self.assertEqual('builtin-sphere', check()['differences'][0]['kind'])

    def test_merged_collider_at_origin_does_not_inherit_other_colliders_activation(self):
        unity, converted = fixtures()
        transforms = unity['sections']['transforms']
        def add(parent, name, also_saved):
            index = sum(t['parent'] == parent for t in transforms)
            sid = parent + '/' + str(index)
            path = next(t['path'] for t in transforms if t['id'] == parent)
            t = dict(copy.deepcopy(transforms[1]), id=sid, parent=parent, siblingIndex=index, name=name,
                     path=(path + '/' + name).lstrip('/'))
            transforms.append(t)
            if also_saved:
                owner = next(s for s in converted['slots'] if s['id'] == parent)
                converted['slots'].append(dict(copy.deepcopy(converted['slots'][1]), id=sid, parent=parent,
                    siblingIndex=index, path=owner['path'] + [name], serializedId='saved:' + sid, components=[]))
            return t
        body = add('r/0', 'Collider', True)
        other = add(add('r/1', 'Bone', False)['id'], 'Collider', False)
        for t in (body, other):
            unity['sections']['colliders'].append(dict(t, id=t['id']+'/c:Collider:0', transform=t['id'],
                root=t['id'], type='Collider', enabled=True, colliders=[], parameters=[]))
        body.update(activeSelf=False, activeInHierarchy=False)
        body['localPosition']['x'] = .05
        def check(overrides=None):
            return comparison.compare(unity, unity, converted, overrides)
        result = check()
        self.assertEqual('ambiguous', next(r for r in result['transforms'] if r['sourceId']==body['id'])['status'])
        self.assertEqual([], result['differences'])
        self.assertIn('activeSelf',[i['kind'] for i in check({body['id']: body['id']})['differences']])

    def test_only_proven_export_exclusions_allow_a_missing_renderer(self):
        unity, converted = fixtures()
        converted['slots'][-1]['components'] = []
        renderer, = unity['sections']['renderers']
        def check():
            return comparison.compare(unity, unity, converted, {'r/1': 'r/1'})
        self.assertEqual('renderer-count', check()['differences'][0]['kind'])
        with mock.patch.dict(unity, editorOnlyTransforms=['r']):
            self.assertEqual('excluded', check()['renderers'][0]['status'])
            self.assertEqual([], check()['differences'])
        with mock.patch.dict(renderer, mesh={'guid': '', 'fileId': '0'}):
            self.assertEqual('excluded', check()['renderers'][0]['status'])
        with mock.patch.dict(unity, emptyMeshRenderers=[renderer['id']]):
            self.assertEqual('excluded', check()['renderers'][0]['status'])
        renderer['activeInHierarchy'] = False
        self.assertEqual('renderer-count', check()['differences'][0]['kind'])

    def test_neutral_only_replaces_the_named_renderer_channel(self):
        unity, converted = fixtures()
        skin, = unity['sections']['renderers']
        component, = converted['slots'][-1]['components']
        skin['blendShapes'] = [dict(index=i, name=n, weight=0.) for i, n in enumerate(('Smile', 'Blink'))]
        component.update(blendShapeNames=['Smile', 'Blink'], blendShapeWeights=[1., .5])
        unity['initialPoseWeights'] = {skin['id']: {'Smile': 1.}}
        report = comparison.compare(unity, unity, converted)
        self.assertEqual(['Blink'], [d['shape'] for d in report['differences']])
        component['blendShapeWeights'][0] = .4
        self.assertEqual(2, len(comparison.compare(unity, unity, converted)['differences']))

    def test_silence_may_share_a_vowel_only_while_undriven(self):
        unity, converted = fixtures()
        renderer = unity['sections']['renderers'][0]['id']
        bindings = [dict(phoneme=p, renderer=renderer, shape='Open') for p in (0, 10)]
        unity['lipSync'] = [dict(mode=3, bindings=bindings)]
        actual = [dict(phoneme='aa', renderer='r/1:0', index=0, shape='Open')]
        converted['visemeDrivers'] = [dict(component='r/1:1', enabled=True, analyzer='r/0:2', bindings=actual)]
        def result():
            return comparison.compare(unity, unity, converted)['visemes']['status']
        self.assertEqual('matched', result())
        actual.append(dict(actual[0], phoneme='Silence'))
        self.assertEqual('different', result())
        actual.pop()
        bindings[0]['shape'] = 'Closed'
        self.assertEqual('different', result())

    def test_unique_imported_child_renderer_checks_activation_and_identity(self):
        unity, converted = fixtures()
        owner = converted['slots'][-1]
        child = dict(copy.deepcopy(owner), parent='r/1', id='r/1/0', siblingIndex=0,
                     serializedId='saved:child', path=owner['path'] + ['Mesh'])
        child['components'][0]['id'] = 'r/1/0:0'
        owner['components'] = []
        converted['slots'].append(child)
        def report():
            return comparison.compare(unity, unity, converted, {'r/1': 'r/1'})
        self.assertEqual('r/1/0:0', report()['renderers'][0]['targetComponent'])
        child.update(activeSelf=False, activeInHierarchy=False)
        self.assertEqual('renderer-count', report()['differences'][0]['kind'])

    def test_renderer_enabled_is_ignored_with_old_and_new_unity_captures(self):
        unity, converted = fixtures()
        converted['slots'][-1]['components'][0]['enabled'] = False
        self.assertEqual([], comparison.compare(unity, unity, converted)['differences'])
        unity['sections']['renderers'][0]['enabled'] = True
        self.assertEqual([], comparison.compare(unity, unity, converted)['differences'])

    def test_viseme_compares_the_field_owner_and_saved_mesh_channel(self):
        unity, converted = fixtures()
        renderer = unity['sections']['renderers'][0]['id']
        unity['lipSync'] = [dict(mode=3, bindings=[dict(phoneme=10, renderer=renderer, shape='Open')])]
        saved = dict(phoneme='aa', renderer='r/1:0', index=4, shape='Open')
        driver = dict(component='r/1:1', enabled=True, analyzer='r/0:2', bindings=[saved])
        converted['visemeDrivers'] = []
        def check():
            return comparison.compare(unity, unity, converted)['visemes']
        self.assertEqual('different', check()['status'])
        converted['visemeDrivers'].append(driver)
        self.assertEqual('matched', check()['status'])
        for field, wrong in [('renderer', 'r/0:0'), ('shape', 'Blink'), ('phoneme', 'PP')]:
            with self.subTest(field=field), mock.patch.dict(saved, {field: wrong}):
                self.assertEqual('different', check()['status'])
        with mock.patch.dict(driver, analyzer=None):
            self.assertEqual('different', check()['status'])
        del converted['visemeDrivers']
        self.assertEqual('unverified', check()['status'])

    def test_parameter_only_fills_unsampled_slots_from_the_declared_mesh(self):
        unity, converted = fixtures()
        skin, = unity['sections']['renderers']
        names = ['shape' + str(i) for i in range(15)]
        skin['blendShapes'] = [dict(index=i, name=n, weight=0.) for i, n in enumerate(names)]
        descriptor = dict(mode=4, renderer=skin['id'], declaredShapes=names.copy(),
                          bindings=[], reason='No independent full-weight shape observed at full Voice')
        unity['lipSync'] = [descriptor]
        phonemes = 'Silence PP FF TH DD kk CH SS nn RR aa E ih oh ou'.split()
        saved = [dict(phoneme=p, renderer='r/1:0', index=i, shape=n)
                 for i, (p, n) in enumerate(zip(phonemes, names))]
        converted['visemeDrivers'] = [dict(component='r/1:1', enabled=True, analyzer='r/0:2', bindings=saved)]
        def check():
            return comparison.compare(unity, unity, converted)['visemes']
        self.assertEqual(('matched', 15), (check()['status'], check()['declaredFallbacks']))
        saved.pop()
        self.assertEqual('different', check()['status'])
        descriptor['bindings'] = [dict(phoneme=0, renderer=skin['id'], shape=names[0])]
        descriptor['declaredShapes'][0] = names[1]
        descriptor['declaredShapes'][-3:] = ['', '-none-', 'unknown']
        result = check()
        self.assertEqual(('unverified', 11, 12), (result['status'], result['declaredFallbacks'], result['matched']))
        descriptor['renderer'] = 'missing'
        self.assertEqual(0, check()['declaredFallbacks'])

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

    def test_unexpected_saved_renderer_on_mapped_bone_needs_review(self):
        unity, converted = fixtures()
        converted['slots'][1]['components'].append(dict(id='r/0:0', type='[FrooxEngine]FrooxEngine.MeshRenderer',
            enabled=True, bones=None, rigBones=None, blendShapeWeights=None, slotReferences=[]))
        report = comparison.compare(unity, unity, converted, {'r': 'r'})
        self.assertEqual('needs-review', report['status'])
        issue, = report['differences']
        self.assertEqual(('renderer-count', 'r/0', 0, 1, ['r/0:0']),
                         (issue['kind'], issue['target'], issue['expected'], issue['actual'], issue['convertedComponents']))

    def test_two_source_renderers_cannot_claim_one_saved_component(self):
        unity, converted = fixtures()
        renderer = unity['sections']['renderers'][0]
        unity['sections']['renderers'].append(dict(renderer, id=renderer['id'][:-1] + '1'))
        report = comparison.compare(unity, unity, converted, {'r': 'r'})
        self.assertEqual('needs-review', report['status'])
        self.assertEqual(['unverified', 'unverified'], [r['status'] for r in report['renderers']])
        self.assertEqual(['renderer-count'], [issue['kind'] for issue in report['differences']])

    def test_duplicate_output_names_are_not_first_match(self):
        unity, converted = fixtures()
        extra = copy.deepcopy(converted['slots'][1])
        extra.update(id='r/2', siblingIndex=2, serializedId='extra')
        converted['slots'].append(extra)
        report = comparison.compare(unity, unity, converted)
        self.assertEqual('ambiguous', report['transforms'][1]['status'])
        self.assertEqual(['r/0', 'r/2'], report['transforms'][1]['candidates'])

    def test_generated_template_is_not_a_source_mesh_candidate(self):
        unity, converted = fixtures()
        slots = converted['slots']
        slots[2]['path'][-1] = 'OtherMesh'
        parent = slots[0]
        for sid, name in [('r/2', 'Avatar Settings'), ('r/2/0', 'Dynamic Bone Settings'), ('r/2/0/0', 'Mesh')]:
            slot = dict(slots[1], id=sid, parent=parent['id'], siblingIndex=int(sid.split('/')[-1]),
                        serializedId=sid, path=parent['path'] + [name], components=[])
            slots.append(slot)
            parent = slot
        chain = dict(id=slot['id'] + ':0', type='[FrooxEngine]FrooxEngine.DynamicBoneChain',
                     enabled=False, slotReferences=[], bones=None, rigBones=None, blendShapeWeights=None)
        slot['components'].append(chain)
        report = comparison.compare(unity, unity, converted)
        self.assertEqual('unmapped', report['transforms'][2]['status'])
        self.assertEqual('unverified', report['renderers'][0]['status'])
        slots[2]['path'][-1] = 'Mesh'
        report = comparison.compare(unity, unity, converted)
        self.assertEqual('r/1', report['transforms'][2]['target'])

    def test_bone_order_and_repeated_entries_preserved(self):
        unity, converted = fixtures()
        converted['slots'][-1]['components'][0]['bones'].reverse()
        report = comparison.compare(unity, unity, converted, {'r': 'r'})
        self.assertEqual(2, sum(d['kind'] == 'bone-target' for d in report['differences']))

    def test_weighted_targets_survive_reindex_and_unused_tail_removal(self):
        unity, converted = fixtures()
        renderer, = unity['sections']['renderers']
        renderer['bones'].append('r')
        unity['skinBindings'][0]['names'].append('Unused')
        component = converted['slots'][-1]['components'][0]
        component['bones'].reverse()
        component['skinSize']['bindings']['names'].reverse()
        report = comparison.compare(unity, unity, converted, {'r': 'r'})
        self.assertEqual('matched-covered-fields', report['status'])
        self.assertEqual(2, report['renderers'][0]['boneTargets']['sameIndexDifferences'])
        unity['skinBindings'][0]['used'].append(2)
        issue, = comparison.compare(unity, unity, converted, {'r': 'r'})['differences']
        self.assertEqual(('missing-weighted-bone', 2), (issue['kind'], issue['sourceIndex']))

    def test_positive_extra_target_and_ambiguous_bindings(self):
        unity, converted = fixtures()
        source = unity['skinBindings'][0]
        source['used'] = [0]
        report = comparison.compare(unity, unity, converted)
        self.assertEqual(['extra-weighted-bone'], [d['kind'] for d in report['differences']])
        source.update(names=['Twin', 'Twin'], used=[0, 1])
        converted['slots'][-1]['components'][0]['bones'].reverse()
        report = comparison.compare(unity, unity, converted)
        self.assertEqual([], report['differences'])
        self.assertEqual('unverified', report['renderers'][0]['boneTargets']['status'])
        del unity['skinBindings']
        report = comparison.compare(unity, unity, converted)
        self.assertTrue(report['unverified'][-1]['entries'][0]['missingUnity'])

    def test_null_reference_only_fails_when_weighted(self):
        unity, converted = fixtures()
        saved = converted['slots'][-1]['components'][0]
        saved['bones'][1] = dict(target=None, status='null', serializedId=None)
        for palette in (unity['skinBindings'][0], saved['skinSize']['bindings']):
            palette['used'] = [0]
        report = comparison.compare(unity, unity, converted, {'r': 'r'})
        self.assertEqual('matched-covered-fields', report['status'])
        self.assertFalse(report['unresolvedOrNullBones'][0]['weighted'])
        saved['skinSize']['bindings']['used'].append(1)
        issue, = comparison.compare(unity, unity, converted)['differences']
        self.assertEqual(('bone-target', 1), (issue['kind'], issue['savedIndex']))

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
                args = [str(coverage), '--case', 'synthetic', '--output', str(output)]
                if scenario in ('mapped', 'stale', 'review', 'tampered'):
                    mapping = work / 'mapping.json'
                    mapping.write_text(json.dumps(dict(unitySha256=comparison.sha256(expected),
                        packageSha256='0' * 64 if scenario == 'stale' else hashlib.sha256(b'converted package').hexdigest(),
                        transforms={'r': 'r'})), encoding='utf-8')
                    if scenario in ('review', 'tampered'):
                        self.assertEqual(1, runner.main(args))
                        if scenario == 'tampered':
                            (output / 'converted.json').write_text('{}')
                        reviewed = work / 'reviewed'
                        args[-1] = str(reviewed)
                        args += ['--review-run', str(output)]
                        output = reviewed
                        calls.clear()
                    args += ['--mapping', str(mapping)]
                code = runner.main(args)
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

    def test_reviewed_mapping_allows_match_and_stale_mapping_fails(self):
        code, report, _ = self.run_case('mapped')
        self.assertEqual((0, 'matched-covered-fields'), (code, report['status']))
        code, report, _ = self.run_case('stale')
        self.assertEqual((1, 'failed'), (code, report['status']))
        self.assertIn('stale mapping hashes', report['errors'])

    def test_review_saved_package_uses_original_capture_and_checks_its_hash(self):
        code, report, calls = self.run_case('review')
        self.assertEqual((0, 'matched-covered-fields', []), (code, report['status'], calls))
        self.assertIn('previousRun', report)
        code, report, calls = self.run_case('tampered')
        self.assertEqual((1, 'failed', []), (code, report['status'], calls))
        self.assertIn('Saved comparison input changed', report['errors'])

    def test_input_changes_during_conversion_invalidate_successful_stages(self):
        code, report, _ = self.run_case('input-changed')
        self.assertEqual(1, code)
        self.assertEqual('failed', report['status'])
        self.assertEqual('captured', report['stages']['capture'])
        self.assertTrue(any('input changed' in e for e in report['errors']))


if __name__ == '__main__':
    unittest.main()
