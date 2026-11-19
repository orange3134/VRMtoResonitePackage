#!/usr/bin/env python3
"""Compare Unity Descriptor settings with saved VRIK Head Proxy and Target positions.

The proxy is the headset pose; its Target is the IK head-bone pose. Align the two
coordinate systems using four independently captured humanoid bones, never using
the viewpoint being tested. Missing/ambiguous data is unverified, not a match.
"""
import argparse
import hashlib
import importlib.util
import json
import math
import re
from pathlib import Path


spec = importlib.util.spec_from_file_location('structure', Path(__file__).with_name('compare-converted-structure.py'))
structure = importlib.util.module_from_spec(spec)
spec.loader.exec_module(structure)
BONES = ('Hips', 'Head', 'LeftUpperArm', 'RightUpperArm')


def vector(value):
    result = [value[k] for k in 'xyz'] if isinstance(value, dict) else value
    if not isinstance(result, list) or len(result) != 3 or any(
            type(v) not in (int, float) or not math.isfinite(v) for v in result):
        raise ValueError('Expected a finite three-dimensional position')
    return result


def add(a, b): return [x + y for x, y in zip(a, b)]
def sub(a, b): return [x - y for x, y in zip(a, b)]
def mul(a, s): return [x * s for x in a]
def dot(a, b): return sum(x * y for x, y in zip(a, b))
def cross(a, b): return [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]]


def unit(a):
    length = math.sqrt(dot(a, a))
    if length < 1e-8: raise ValueError('Degenerate humanoid reference frame')
    return mul(a, 1 / length)


def frame(anchors):
    x = unit(sub(anchors['RightUpperArm'], anchors['LeftUpperArm']))
    up = sub(anchors['Head'], anchors['Hips'])
    y = unit(sub(up, mul(x, dot(up, x))))
    return x, y, unit(cross(x, y))


def world(slots, sid, point=None):
    point = [0, 0, 0] if point is None else point
    while sid:
        slot = slots[sid]
        p = [v * s for v, s in zip(point, slot['localScale'])]
        q = slot['localRotation']
        t = mul(cross(q[:3], p), 2)
        point = add(add(add(p, mul(t, q[3])), cross(q[:3], t)), slot['localPosition'])
        sid = slot['parent']
    return point


def one(values, label):
    values = list(values)
    if len(values) != 1: raise ValueError('Expected exactly one ' + label + '; found ' + str(len(values)))
    return values[0]


def compare(unity, parser, converted, tolerance=.001, anchor_tolerance=.003):
    result = {'status': 'unverified', 'positionToleranceMetres': tolerance,
              'anchorToleranceMetres': anchor_tolerance, 'checks': {}}
    try:
        if unity.get('format') != 'resopon.unity-avatar-viewpoint' or unity.get('schemaVersion') != 1:
            raise ValueError('Unity viewpoint capture is missing or unsupported; recapture with --viewpoints')
        if parser.get('format') != 'resopon.parser-avatar-viewpoint' or parser.get('schemaVersion') != 1:
            raise ValueError('Parser viewpoint capture is missing or unsupported')
        if not re.fullmatch('[0-9a-fA-F]{32}', unity.get('prefabGuid', '')) or unity.get('prefabGuid') != parser.get('prefabGuid'):
            raise ValueError('Selected prefab GUID differs between Unity and parser')
        descriptor = one(unity.get('descriptors', []), 'VRC Avatar Descriptor')
        view = vector(descriptor['viewPosition'])
        source_view = vector(descriptor['viewWorldPosition'])
        if math.dist(source_view, add(vector(descriptor['descriptorWorldPosition']), view)) > 1e-5:
            raise ValueError('Unity viewpoint does not use the declared world-offset convention')
        result['descriptor'] = descriptor
        result['checks']['parserViewPosition'] = math.dist(view, vector(parser['viewPosition'])) <= 1e-6
        source = {bone: vector(one((a for a in descriptor['anchors'] if a['bone'] == bone), bone)['worldPosition'])
                  for bone in BONES}
        slots = structure.validate_structure(converted)
        if any(abs(sum(q*q for q in s['localRotation']) - 1) > 1e-4 for s in slots.values()):
            raise ValueError('Saved rotation is not a unit quaternion')
        components = [(s, c) for s in slots.values() for c in s['components']]
        _, rig = one(((s, c) for s, c in components if c['type'] == '[FrooxEngine]FrooxEngine.BipedRig'), 'BipedRig')
        _, avatar = one(((s, c) for s, c in components if c['type'] == '[FrooxEngine]FrooxEngine.FinalIK.VRIKAvatar'), 'VRIKAvatar')
        target_ids = {}
        for bone in BONES:
            ref = rig['rigBones'].get(bone, {})
            if ref.get('status') != 'resolved': raise ValueError('Unresolved rig bone: ' + bone)
            target_ids[bone] = ref['target']
        target = {bone: world(slots, target_ids[bone]) for bone in BONES}
        source_basis, target_basis = frame(source), frame(target)
        scales = [math.dist(target[b], target['Hips']) / math.dist(source[b], source['Hips']) for b in BONES[1:]]
        scale = sorted(scales)[1]
        def convert(point):
            p = sub(point, source['Hips'])
            return add(target['Hips'], [scale * sum(dot(p, source_basis[j]) * target_basis[j][i]
                       for j in range(3)) for i in range(3)])
        residuals = {b: math.dist(convert(source[b]), target[b]) for b in BONES}
        result['alignment'] = {'method': 'humanoid frame; hips origin, head up, left/right upper arms',
            'scale': scale, 'sourceBasis': source_basis, 'convertedBasis': target_basis,
            'sourceAnchors': source, 'convertedAnchors': target, 'residualsMetres': residuals}
        target_id = one((r['target'] for r in avatar['slotReferences'] if r['field'] == '_headProxy'), 'VRIK head Target reference')
        proxy_id = slots[target_id]['parent']
        if proxy_id is None or not any(c['type'] == '[FrooxEngine]FrooxEngine.CommonAvatar.AvatarPoseNode'
                                      for c in slots[proxy_id]['components']):
            raise ValueError('VRIK head Target has no AvatarPoseNode parent')
        expected_proxy = convert(source_view)
        def position(sid, expected):
            actual = world(slots, sid)
            return {'slot': sid, 'path': slots[sid]['path'], 'localPosition': slots[sid]['localPosition'],
                    'packageWorldPosition': actual, 'expectedPackageWorldPosition': expected,
                    'deltaMetres': sub(actual, expected), 'distanceMetres': math.dist(actual, expected)}
        result['headProxy'] = position(proxy_id, expected_proxy)
        result['headTarget'] = position(target_id, target['Head'])
        result['targetMeaning'] = 'IK head bone anchor; expected to coincide with Head, not the headset viewpoint'
        result['checks']['headProxy'] = result['headProxy']['distanceMetres'] <= tolerance
        result['checks']['headTarget'] = result['headTarget']['distanceMetres'] <= tolerance
        if max(residuals.values()) > anchor_tolerance:
            raise ValueError('Humanoid anchors do not support a single rigid orientation and uniform scale; position match is unverified')
        result['status'] = 'matched' if all(result['checks'].values()) else 'different'
    except (KeyError, TypeError, ValueError, ZeroDivisionError) as error:
        result['reason'] = str(error)
    return result


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('unity', type=Path)
    ap.add_argument('parser', type=Path)
    ap.add_argument('converted', type=Path)
    ap.add_argument('--report', type=Path, required=True)
    ap.add_argument('--tolerance', type=float, default=.001)
    ap.add_argument('--anchor-tolerance', type=float, default=.003)
    args = ap.parse_args()
    if not all(math.isfinite(v) and v > 0 for v in [args.tolerance, args.anchor_tolerance]):
        ap.error('Tolerances must be finite and positive')
    if args.report.exists(): ap.error('Report must be a new file')
    inputs = {}
    def read(path, name):
        raw = path.read_bytes()
        inputs[name] = {'path': str(path.resolve()), 'sha256': hashlib.sha256(raw).hexdigest()}
        return json.loads(raw.decode('utf-8-sig'))
    try:
        result = compare(read(args.unity, 'unity'), read(args.parser, 'parser'), read(args.converted, 'converted'),
                         args.tolerance, args.anchor_tolerance)
        result.update(format='resopon.avatar-viewpoint-comparison', schemaVersion=1, inputs=inputs,
                      comparisonSourceSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                      scope='saved initial pose; runtime tracking, viewpoint overrides and package provenance require separate verification')
        with args.report.open('x', encoding='utf-8') as f: json.dump(result, f, ensure_ascii=False, indent=2)
        print(json.dumps({'status': result['status'], 'checks': result['checks'], 'report': str(args.report)}, ensure_ascii=False))
        return 0 if result['status'] == 'matched' else 1
    except (OSError, ValueError) as error:
        ap.error(str(error))


if __name__ == '__main__':
    raise SystemExit(main())
