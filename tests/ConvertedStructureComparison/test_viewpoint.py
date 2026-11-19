"""Independent invented avatar geometry; no proprietary assets or engine needed."""
import copy
import importlib.util
import math
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('viewpoint', Path(__file__).resolve().parents[2] / 'scripts/compare-avatar-viewpoint.py')
v = importlib.util.module_from_spec(spec)
spec.loader.exec_module(v)


def fixtures():
    anchors = dict(Hips=[0, 1, 0], Head=[0, 1.8, 0], LeftUpperArm=[-.25, 1.5, .02], RightUpperArm=[.25, 1.5, .02])
    view = [0, 1.9, .1]
    unity = dict(format='resopon.unity-avatar-viewpoint', schemaVersion=1, prefabGuid='a'*32,
        descriptors=[dict(viewPosition=view, descriptorWorldPosition=[0, 0, 0], viewWorldPosition=view,
                          anchors=[dict(bone=b, worldPosition=p) for b, p in anchors.items()])])
    parser = dict(format='resopon.parser-avatar-viewpoint', schemaVersion=1, prefabGuid='a'*32, viewPosition=view.copy())
    def component(sid, index, name, refs=None, bones=None):
        return dict(id=sid+':'+str(index), type='[FrooxEngine]FrooxEngine.'+name, enabled=True,
                    bones=None, rigBones=bones, blendShapeWeights=None, slotReferences=refs or [])
    def slot(sid, name, position, parent='r', path=None):
        return dict(id=sid, parent=parent, siblingIndex=int(sid.split('/')[-1]) if parent else 0,
                    path=path or ['Avatar', name], serializedId='saved:'+sid, tag='', activeSelf=True, activeInHierarchy=True,
                    localPosition=position, localRotation=[0, 0, 0, 1], localScale=[1, 1, 1], components=[])
    root = slot('r', 'Avatar', [3, -1, 7], None, ['Avatar'])
    # A different coordinate system: translation, a 180-degree turn and uniform scale.
    root['localRotation'] = [0, 1, 0, 0]
    root['localScale'] = [2, 2, 2]
    slots = [root] + [slot('r/'+str(i), b, p.copy()) for i, (b, p) in enumerate(anchors.items())]
    proxy = slot('r/4', 'Arbitrary camera name', view.copy())
    proxy['components'] = [component('r/4', 0, 'CommonAvatar.AvatarPoseNode')]
    target = slot('r/4/0', 'Arbitrary IK name', [0, -.1, -.1], 'r/4', ['Avatar', 'Arbitrary camera name', 'Arbitrary IK name'])
    slots.extend([proxy, target])
    bones = {b: dict(target='r/'+str(i), status='resolved', serializedId='saved:r/'+str(i)) for i, b in enumerate(anchors)}
    root['components'] = [component('r', 0, 'BipedRig', bones=bones),
                          component('r', 1, 'FinalIK.VRIKAvatar', [dict(field='_headProxy', target='r/4/0')])]
    return unity, parser, dict(format='resopon.converted-structure', schemaVersion=1, packageSha256='a'*64, slots=slots)


class ViewpointTests(unittest.TestCase):
    def test_rotation_translation_scale_and_distinct_target_role(self):
        r = v.compare(*fixtures())
        self.assertEqual('matched', r['status'], r)
        self.assertAlmostEqual(2, r['alignment']['scale'])
        self.assertNotEqual(r['headProxy']['packageWorldPosition'], r['headTarget']['packageWorldPosition'])

    def test_proxy_offset_does_not_train_alignment(self):
        u, p, c = fixtures()
        c['slots'][-2]['localPosition'][2] += .1
        # Retain the IK head position while moving the camera, as in the real bug.
        c['slots'][-1]['localPosition'][2] -= .1
        r = v.compare(u, p, c)
        self.assertEqual('different', r['status'])
        self.assertAlmostEqual(.2, r['headProxy']['distanceMetres'])
        self.assertTrue(r['checks']['headTarget'])

    def test_target_offset_is_independent(self):
        u, p, c = fixtures(); c['slots'][-1]['localPosition'][1] += .1
        r = v.compare(u, p, c)
        self.assertTrue(r['checks']['headProxy']); self.assertFalse(r['checks']['headTarget'])

    def test_wrong_parser_setting_is_detected(self):
        u, p, c = fixtures(); p['viewPosition'][2] += .1
        self.assertFalse(v.compare(u, p, c)['checks']['parserViewPosition'])

    def test_missing_or_ambiguous_data_never_passes(self):
        mutations = [lambda u, p, c: u.pop('descriptors'),
                     lambda u, p, c: u['descriptors'].append(copy.deepcopy(u['descriptors'][0])),
                     lambda u, p, c: u['descriptors'][0]['anchors'].pop(),
                     lambda u, p, c: c['slots'][0]['components'][1].update(slotReferences=[]),
                     lambda u, p, c: p.update(prefabGuid='b'*32),
                     lambda u, p, c: p.update(viewPosition=None)]
        for mutate in mutations:
            with self.subTest(mutate=mutate):
                values = fixtures(); mutate(*values)
                self.assertEqual('unverified', v.compare(*values)['status'])

    def test_changed_pose_and_nonfinite_positions_are_unverified(self):
        u, p, c = fixtures(); c['slots'][3]['localPosition'][2] += .1
        self.assertEqual('unverified', v.compare(u, p, c)['status'])
        u, p, c = fixtures(); u['descriptors'][0]['anchors'][0]['worldPosition'][0] = math.nan
        self.assertEqual('unverified', v.compare(u, p, c)['status'])

    def test_equal_raw_numbers_cannot_bypass_coordinate_conversion(self):
        u, p, c = fixtures(); c['slots'][-2]['localPosition'] = [-1.5, 1.45, 3.45]
        r = v.compare(u, p, c)
        self.assertEqual('different', r['status'])
        self.assertFalse(r['checks']['headProxy'])


if __name__ == '__main__': unittest.main()
