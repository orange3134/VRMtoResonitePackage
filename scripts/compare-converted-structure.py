#!/usr/bin/env python3
"""Compare Unity, parser and saved package structures. Diagnostic, not visual parity.

Exit 0: covered structure matches; 1: differences/unmapped data require review;
2: invalid input. Name paths produce candidates, never provenance guarantees.
"""
import argparse
import collections
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import re
import sys


def module(name):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), Path(__file__).with_name(name + '.py'))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


unity_compare = module('compare-unity-reference')
snapshot_check = module('check-package-snapshot')
geometry = module('avatar_geometry')


def sha256(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def validate_structure(data):
    require(isinstance(data, dict), 'converted structure must be an object')
    require(data.get('format') == 'resopon.converted-structure' and
            type(data.get('schemaVersion')) is int and data['schemaVersion'] == 1, 'unsupported converted structure')
    require(isinstance(data.get('packageSha256'), str) and
            re.fullmatch('[0-9a-f]{64}', data['packageSha256']), 'invalid package hash')
    require(isinstance(data.get('slots'), list) and data['slots'], 'missing slots')
    slots, components, serialized = {}, set(), set()
    for slot in data['slots']:
        require(isinstance(slot, dict), 'invalid slot record')
        sid = slot.get('id')
        require(isinstance(sid, str) and re.fullmatch(r'r(?:/(?:0|[1-9][0-9]*))*', sid)
                and sid not in slots, 'invalid/duplicate slot id')
        parent = sid.rsplit('/', 1)[0] if '/' in sid else None
        require(slot.get('parent') == parent and (parent is None or parent in slots), 'invalid parent/order')
        require(type(slot.get('siblingIndex')) is int and slot['siblingIndex'] ==
                (int(sid.rsplit('/', 1)[1]) if parent else 0), 'invalid sibling index')
        path = slot.get('path')
        require(isinstance(path, list) and len(path) == sid.count('/') + 1 and
                all(isinstance(p, str) for p in path), 'invalid slot path')
        require(parent is None or path[:-1] == slots[parent]['path'], 'inconsistent slot path')
        require(isinstance(slot.get('serializedId'), str) and slot['serializedId'] not in serialized,
                'invalid/duplicate serialized slot id')
        serialized.add(slot['serializedId'])
        require(type(slot.get('activeSelf')) is bool and type(slot.get('activeInHierarchy')) is bool and
                slot['activeInHierarchy'] == (slot['activeSelf'] and
                (slots[parent]['activeInHierarchy'] if parent else True)), 'invalid active state')
        for field, length in [('localPosition', 3), ('localRotation', 4), ('localScale', 3)]:
            value = slot.get(field)
            require(isinstance(value, list) and len(value) == length and
                    all(type(v) in (float, int) and math.isfinite(v) for v in value), 'invalid ' + field)
        require(isinstance(slot.get('components'), list), 'invalid components')
        for index, component in enumerate(slot['components']):
            require(isinstance(component, dict), 'invalid component record')
            require(component.get('id') == sid + ':' + str(index) and component['id'] not in components,
                    'invalid component id')
            require(isinstance(component.get('type'), str) and component['type'], 'invalid component type')
            require(component.get('enabled') is None or type(component['enabled']) is bool, 'invalid enabled state')
            components.add(component['id'])
        slots[sid] = slot
    for slot in slots.values():
        for component in slot['components']:
            require(isinstance(component.get('slotReferences'), list), 'missing slot references')
            for ref in component['slotReferences']:
                require(isinstance(ref, dict), 'invalid slot reference record')
                require(isinstance(ref.get('field'), str) and isinstance(ref.get('target'), str) and
                        ref['target'] in slots, 'invalid slot reference')
            bones = component.get('bones')
            rig = component.get('rigBones')
            skin = component['type'] == '[FrooxEngine]FrooxEngine.SkinnedMeshRenderer'
            require(isinstance(bones, list) if skin else bones is None, 'invalid skin bones')
            require(isinstance(rig, dict) if component['type'] == '[FrooxEngine]FrooxEngine.BipedRig'
                    else rig is None, 'invalid rig bones')
            weights = component.get('blendShapeWeights')
            require(isinstance(weights, list) and all(type(v) in (int, float) and math.isfinite(v) for v in weights)
                    if skin else weights is None, 'invalid shape weights')
            for ref in (bones or []) + list((rig or {}).values()):
                require(isinstance(ref, dict), 'invalid bone reference record')
                target, status, raw = ref.get('target'), ref.get('status'), ref.get('serializedId')
                require((status == 'resolved' and isinstance(target, str) and target in slots and raw == slots[target]['serializedId']) or
                        (status == 'null' and target is None and raw is None) or
                        (status == 'unresolved' and target is None and isinstance(raw, str) and raw not in serialized),
                        'invalid bone reference')
    return slots


def compare_visemes(unity, converted, slots, transforms, paired, excluded):
    descriptors = unity.get('lipSync')
    drivers = converted.get('visemeDrivers')
    if descriptors is None or drivers is None:
        return {'status': 'unverified', 'reason': 'Viseme capture missing; recapture both sides'}
    if len(descriptors) != 1:
        return {'status': 'unverified', 'reason': 'Expected one selected avatar descriptor'}
    descriptor, = descriptors
    reason = descriptor.get('reason')
    if reason and descriptor['mode'] != 4:
        return {'status': 'unverified', 'reason': descriptor['reason']}
    phonemes = ('Silence', 'PP', 'FF', 'TH', 'DD', 'kk', 'CH', 'SS', 'nn', 'RR', 'aa', 'E', 'ih', 'oh', 'ou')
    skins = {s['id']: s for s in unity['sections']['renderers'] if s['type'] == 'UnityEngine.SkinnedMeshRenderer'}
    source_bindings = list(descriptor['bindings'])
    sampled = {b['phoneme'] for b in source_bindings}
    declared = []
    if descriptor['mode'] == 4:
        skin = skins.get(descriptor.get('renderer'))
        if skin:
            names = {b['name'] for b in skin['blendShapes']}
            for index, name in enumerate(descriptor.get('declaredShapes', [])[:len(phonemes)]):
                if index not in sampled and name and name != '-none-' and name in names:
                    declared.append(dict(phoneme=index, renderer=skin['id'], shape=name))
        source_bindings.extend(declared)
        if reason and not source_bindings:
            return {'status': 'unverified', 'reason': reason}
    owners = {t['sourceId']: t for t in transforms}
    actual = collections.Counter()
    for driver in drivers:
        for b in driver['bindings']:
            actual[(b['phoneme'], b['renderer'], b['shape'])] += 1
    expected = collections.Counter()
    unknown = []
    for b in source_bindings:
        skin = skins[b['renderer']]
        owner = owners[skin['transform']]
        if skin['transform'] in excluded:
            continue
        # Generated expression controls may share a name with the mesh. Resolve only if
        # exactly one candidate has a skin, independently of whether it has a viseme driver.
        candidates = [owner['target']] if owner['target'] else owner['candidates']
        matches = [c['id'] for sid in candidates for c in slots[sid]['components']
                   if c['type'] == '[FrooxEngine]FrooxEngine.SkinnedMeshRenderer']
        if skin['id'] in paired:
            matches = [paired[skin['id']]]
        if len(matches) != 1 or sum(s['path'] == skin['path'] and s['transform'] not in excluded for s in skins.values()) != 1:
            unknown.append(b)
        else:
            require(type(b['phoneme']) is int and 0 <= b['phoneme'] < len(phonemes), 'invalid Unity phoneme')
            expected[(phonemes[b['phoneme']], matches[0], b['shape'])] += 1
    def bindings(counter):
        return [dict(phoneme=p, renderer=r, shape=s) for p, r, s in counter.elements()]
    # The converter leaves a shared speech vowel undriven at rest. An independent
    # silence shape must still be connected, and a silence-to-vowel drive is an error.
    vowels = {(r, s) for p, r, s in expected if p in ('aa', 'E', 'ih', 'oh', 'ou')}
    shared_silence = {key: expected[key] for key in expected if key[0] == 'Silence' and key[1:] in vowels}
    for key in shared_silence:
        del expected[key]
    invalid = [d['component'] for d in drivers if not d['enabled'] or not d['analyzer']]
    missing, extra = bindings(expected - actual), bindings(actual - expected)
    incomplete = descriptor['mode'] == 4 and {b['phoneme'] for b in source_bindings} != set(range(len(phonemes)))
    forbidden_silence = actual.keys() & shared_silence.keys()
    status = ('different' if missing or invalid or forbidden_silence or (extra and not unknown and not incomplete) else
              'unverified' if unknown or incomplete else 'matched')
    return dict(status=status, mode=descriptor['mode'], expected=expected.total() + len(unknown),
                sharedSilence=bindings(collections.Counter(shared_silence)),
                matched=sum((expected & actual).values()), missing=missing, extra=extra, invalidDrivers=invalid,
                unmapped=unknown, incompleteSampling=descriptor['mode'] == 4 and len(sampled) < len(phonemes),
                declaredFallbacks=len(declared), samplingReason=reason)


def compare_skin_size(source, saved):
    def extent(record, unity):
        if not record or record.get('reason'):
            return None
        count = record.get('weightedVertices')
        require(type(count) is int and count > 0, 'invalid skin vertex count')
        size = record.get('sizeMeters')
        if unity and isinstance(size, dict) and set(size) == set('xyz'):
            size = [size[axis] for axis in 'xyz']
        require(isinstance(size, list) and len(size) == 3 and
                all(type(x) in (int, float) and math.isfinite(x) and x >= 0 for x in size),
                'invalid skinned extent')
        return size

    expected, actual = extent(source, True), extent(saved, False)
    result = {'unitySizeMeters': expected, 'convertedSizeMeters': actual}
    if expected is None or actual is None:
        return dict(result, status='unverified',
                    unityReason=(source or {}).get('reason') or ('not captured' if expected is None else None),
                    convertedReason=(saved or {}).get('reason') or ('not captured' if actual is None else None))
    # Any oriented AABB bounds the actual point-cloud diameter between its longest
    # side and its diagonal. Only flag a size change proved even after rotation.
    if max(actual) > 2 * math.hypot(*expected) + .001:
        return dict(result, status='expanded')
    if max(expected) > 2 * math.hypot(*actual) + .001:
        return dict(result, status='shrunk')
    if not max(expected) or not max(actual):
        return dict(result, status='unverified', reason='degenerate extent')
    return dict(result, status='no-scale-outlier')


AXES = ((1, 0, 0), (0, 1, 0), (0, 0, 1))


def sphere_frames(sources, slots, reverse):
    bones = ('Hips', 'Head', 'LeftUpperArm', 'RightUpperArm')
    rigs = [c['rigBones'] for s in slots.values() for c in s['components'] if c.get('rigBones')]
    if len(rigs) == 1 and all(rigs[0].get(b, {}).get('target') in reverse for b in bones):
        targets = {b: rigs[0][b]['target'] for b in bones}
        frames, landmarks = [], []
        for nodes, ids in ((sources, {b: reverse[t] for b, t in targets.items()}), (slots, targets)):
            anchors = {b: geometry.world(nodes, sid) for b, sid in ids.items()}
            basis = geometry.frame(anchors)
            frames.append((anchors['Hips'], basis))
            landmarks.append([[geometry.dot(geometry.sub(anchors[b], anchors['Hips']), axis)
                               for axis in basis] for b in bones])
        if any(math.dist(a, b) > .003 for a, b in zip(*landmarks)):
            raise ValueError('Humanoid anchors disagree by more than 3 mm')
        return 'humanoid bones', frames
    # Non-humanoid fixtures can supply a reviewed mapping for the authored root.
    root = next((target for target, source in reverse.items() if source == 'r'), None)
    if root is None:
        raise ValueError('Missing mapped humanoid anchors or an explicitly mapped avatar root')
    return 'mapped avatar root', [(geometry.world(nodes, sid),
            [geometry.world(nodes, sid, axis, rotation_only=True) for axis in AXES])
            for nodes, sid in ((sources, 'r'), (slots, root))]


def compare_sphere(sources, source_id, slots, slot_id, component, reverse):
    radius = component.get('sphereRadius')
    result = dict(expectedRadius=.5, actualRadius=radius)
    if 'sphereRadius' not in component:
        return dict(result, status='unverified', reason='sphere provider not captured')
    if radius is None:
        return dict(result, status='different', reason='renderer does not reference a SphereMesh')
    require(type(radius) in (int, float) and math.isfinite(radius) and radius >= 0, 'invalid sphere radius')
    if abs(radius - .5) > 1e-6:
        return dict(result, status='different', reason='sphere provider radius differs')
    try:
        geometry_data = []
        for nodes, sid, r in ((sources, source_id, .5), (slots, slot_id, radius)):
            center = geometry.world(nodes, sid)
            offsets = [geometry.sub(geometry.world(nodes, sid, geometry.mul(axis, r)), center) for axis in AXES]
            geometry_data.append((center, offsets))
    except ValueError as error:
        return dict(result, status='unverified', reason=str(error))
    try:
        method, frames = sphere_frames(sources, slots, reverse)
    except ValueError as error:
        # The mean squared radius is invariant under rotation. A difference
        # proves a size change even when we cannot align the centers.
        rms = [math.sqrt(sum(geometry.dot(v, v) for v in offsets) / 3) for _, offsets in geometry_data]
        return dict(result, status='unverified' if math.isclose(*rms, abs_tol=1e-6, rel_tol=1e-4) else 'different',
                    frameReason=str(error), unityRmsRadiusMeters=rms[0], savedRmsRadiusMeters=rms[1])
    result['referenceFrame'] = method
    # The three axes and three face diagonals determine the symmetric shape
    # matrix of an ellipsoid, including shear from rotated non-uniform parents.
    directions = (*AXES, *(geometry.unit(v) for v in ((1, 1, 0), (1, 0, 1), (0, 1, 1))))
    for field, (center, offsets), (origin, basis) in zip(('unity', 'saved'), geometry_data, frames):
        columns = [[geometry.dot(offset, axis) for axis in basis] for offset in offsets]
        result[field] = dict(centerMeters=[geometry.dot(geometry.sub(center, origin), axis) for axis in basis],
                             radiiMeters=[math.hypot(*(geometry.dot(col, d) for col in columns)) for d in directions])
    a, b = result['unity'], result['saved']
    result['centerDistanceMeters'] = math.dist(a['centerMeters'], b['centerMeters'])
    size_matches = all(math.isclose(x, y, abs_tol=1e-6, rel_tol=1e-4) for x, y in zip(a['radiiMeters'], b['radiiMeters']))
    return dict(result, status='matched' if result['centerDistanceMeters'] <= .001 and size_matches else 'different')


def weighted_palette(record):
    if not record or record.get('reason'):
        return None
    names, used = record.get('names'), record.get('used')
    require(isinstance(names, list) and all(n is None or isinstance(n, str) for n in names)
            and isinstance(used, list) and all(type(i) is int and 0 <= i < len(names) for i in used)
            and len(set(used)) == len(used), 'invalid weighted bone palette')
    maxima = record.get('maxWeights')
    if maxima is not None:
        require(isinstance(maxima, list) and len(maxima) == len(names)
                and all(type(w) in (float, int) and math.isfinite(w) and w >= 0 for w in maxima)
                and {i for i, w in enumerate(maxima) if w > 0} == set(used), 'invalid bone weight maxima')
    return names, used


def compare_bone_targets(renderer, component, source_record, mapping, differences, unverified):
    bones, refs = renderer['bones'], component['bones']
    expected = [mapping.get(b) for b in bones]
    actual = [r['target'] for r in refs]
    layout = {'sourceCount': len(bones), 'savedCount': len(refs),
              'sameIndexDifferences': sum(a != b for a, b in zip(expected, actual))}
    source = weighted_palette(source_record)
    saved_record = (component.get('skinSize') or {}).get('bindings')
    saved = weighted_palette(saved_record)
    issues, unknown = [], []
    if saved is not None:
        saved_names, saved_used = saved
        for j in saved_used:
            if j >= len(refs) or refs[j]['status'] != 'resolved':
                issues.append(dict(kind='bone-target', savedIndex=j, name=saved_names[j],
                                   reason='weighted saved bone has no resolved target'))
    if source is None or saved is None:
        unknown.append(dict(reason='missing positive-weight palette', missingUnity=source is None,
                            missingSaved=saved is None, unityReason=(source_record or {}).get('reason'),
                            savedReason=(component.get('skinSize') or {}).get('reason')))
    else:
        names, used = source
        source_counts = collections.Counter(names)
        saved_counts = collections.Counter(saved_names)
        saved_by_name = {saved_names[j]: j for j in saved_used if saved_counts[saved_names[j]] == 1}
        expected_targets = {expected[i] if i < len(expected) else None for i in used}
        actual_targets = {actual[j] for j in saved_used if j < len(actual)}
        paired = set()
        for i in used:
            name = names[i]
            target = expected[i] if i < len(expected) else None
            j = saved_by_name.get(name) if name and source_counts[name] == 1 else None
            detail = dict(sourceIndex=i, name=name, expected=target)
            if j is not None:
                paired.add(j)
            if target is None:
                unknown.append(dict(**detail, reason='weighted Unity bone has no mapped target'))
            elif j is not None:
                if j < len(actual) and actual[j] is not None and actual[j] != target:
                    issues.append(dict(kind='bone-target', **detail, savedIndex=j, actual=actual[j]))
            elif target not in actual_targets:
                issues.append(dict(kind='missing-weighted-bone', **detail))
            else:
                unknown.append(dict(**detail, reason='weighted palette entry cannot be uniquely paired'))
        for j in saved_used:
            if j in paired or j >= len(actual) or actual[j] is None:
                continue
            detail = dict(savedIndex=j, name=saved_names[j], actual=actual[j])
            if actual[j] not in expected_targets and None not in expected_targets:
                issues.append(dict(kind='extra-weighted-bone', **detail))
            else:
                unknown.append(dict(**detail, reason='saved weighted palette entry cannot be uniquely paired'))
    for issue in issues:
        if 'savedIndex' in issue and saved_record.get('maxWeights') is not None:
            issue['maxSavedWeight'] = saved_record['maxWeights'][issue['savedIndex']]
        differences.append(dict(sourceId=renderer['id'], **issue))
    if unknown:
        unverified.append(dict(kind='bone-target', sourceId=renderer['id'], entries=unknown))
    return dict(**layout, status='different' if issues else 'unverified' if unknown else 'matched')


def compare(unity, parser, converted, overrides=None):
    unity_compare.validate_snapshot(unity)
    require(unity['schemaVersion'] == 2, 'resolved v2 reference required')
    if parser is not None:
        unity_compare.validate_snapshot(parser)
        require(parser['schemaVersion'] == 2 and unity['prefabGuid'] == parser['prefabGuid'],
                'selected prefab or schema mismatch')
    slots = validate_structure(converted)
    before_diffs = unity_compare.compare(unity, parser) if parser is not None else None
    sources = {t['id']: t for t in unity['sections']['transforms']}
    source_paths = {}

    def source_path(sid):
        if sid not in source_paths:
            t = sources[sid]
            source_paths[sid] = source_path(t['parent']) + (t['name'],) if t['parent'] else ()
        return source_paths[sid]

    tagged = set(unity.get('editorOnlyTransforms', []))
    require(tagged <= sources.keys(), 'unknown EditorOnly transform')
    source_children = collections.defaultdict(list)
    for source in sources.values():
        if source['parent']:
            source_children[source['parent']].append(source)
    excluded = tagged.copy()
    pending = list(tagged)
    while pending:
        for child in source_children[pending.pop()]:
            if child['id'] not in excluded:
                excluded.add(child['id'])
                pending.append(child['id'])
    by_path = collections.defaultdict(list)
    for sid in sources:
        by_path[source_path(sid)].append(sid)
    suffixes = collections.defaultdict(list)
    template_slots = set()
    for slot in slots.values():
        # SpringBoneSetup creates disabled template chains under the avatar's settings.
        # A template or its bindings can have the same name as an excluded Unity object.
        path = slot['path']
        if slot['parent'] in template_slots or (
                len(path) == 4 and path[1] in ('Avatar Settings', '<color=#00ffff>Avatar Settings</color>')
                and path[2] == 'Dynamic Bone Settings' and any(
                    c['type'] == '[FrooxEngine]FrooxEngine.DynamicBoneChain' and c.get('enabled') is False
                    for c in slot['components'])):
            template_slots.add(slot['id'])
            continue
        for length in range(1, len(slot['path'])):
            suffixes[tuple(slot['path'][-length:])].append(slot['id'])
    overrides = overrides or {}
    require(isinstance(overrides, dict) and all(k in sources and v in slots for k, v in overrides.items()),
            'mapping must contain source transform ID -> converted slot ID')
    require(len(set(overrides.values())) == len(overrides), 'mapping must be one-to-one')
    mapping, rows = {}, []
    for sid, source in sources.items():
        path = source_path(sid)
        candidates = suffixes.get(path, []) if path else []
        explicit = sid in overrides
        if sid in excluded:
            target, status = None, 'excluded'
        elif explicit:
            target, status = overrides[sid], 'explicit'
        elif len(candidates) == 1 and len(by_path[path]) == 1:
            target, status = candidates[0], 'unique-path-candidate'
        else:
            target, status = None, 'ambiguous' if candidates else 'unmapped'
        if target:
            mapping[sid] = target
        rows.append({'sourceId': sid, 'sourcePath': list(path), 'status': status,
                     'target': target, 'candidates': candidates,
                     'source': source, 'converted': slots[target] if target else None})
    # A reviewed explicit assignment can conflict with a name candidate. Do not silently pick either.
    targets = collections.Counter(mapping.values())
    targets.update(overrides[sid] for sid in excluded if sid in overrides)
    for row in rows:
        if row['target'] and targets[row['target']] > 1:
            mapping.pop(row['sourceId'])
            row.update(status='ambiguous', target=None, converted=None)
    # A merged accessory collider can occupy the path of a discarded body collider.
    # A zero local offset is invariant to a change of units or axis conventions.
    # Leave identity unresolved if the saved offset fits the other collider alone.
    collider_owners = {c['transform'] for c in unity['sections']['colliders']} - excluded
    def at_parent_origin(v):
        return all(abs(n) <= 1e-6 for n in (v.values() if isinstance(v, dict) else v))
    for row in rows:
        sid = row['sourceId']
        if (sid not in collider_owners or row['status'] != 'unique-path-candidate' or len(source_path(sid)) < 2
                or at_parent_origin(sources[sid]['localPosition']) or not at_parent_origin(row['converted']['localPosition'])):
            continue
        if any(other not in mapping and source_path(other)[-2:] == source_path(sid)[-2:]
               and at_parent_origin(sources[other]['localPosition']) for other in collider_owners):
            del mapping[sid]
            row.update(status='ambiguous', target=None, converted=None, reason='competing collider at the parent origin')
    reverse = {target: source for source, target in mapping.items()}
    differences, unverified = [], []
    retained_editor_only = {}
    ambiguous_editor_only = set()
    for row in rows:
        if row['status'] != 'excluded':
            continue
        sid = row['sourceId']
        row['reason'] = 'EditorOnly hierarchy'
        if sid in overrides:
            candidates = [overrides[sid]]
        else:
            # A suffix can also occur under a different, already identified Unity
            # branch. Only consider slots below the same nearest mapped ancestor.
            parent = sources[sid]['parent']
            while parent and parent not in mapping:
                parent = sources[parent]['parent']
            candidates = []
            for target in row['candidates']:
                ancestor = target
                while ancestor and ancestor not in reverse:
                    ancestor = slots[ancestor]['parent']
                if target not in reverse and ancestor == mapping.get(parent):
                    candidates.append(target)
        row['candidates'] = candidates
        if candidates:
            detail = dict(kind='editor-only-transform', sourceId=sid, candidates=candidates)
            if sid in overrides or len(candidates) == 1 and len(by_path[source_path(sid)]) == 1:
                differences.append(detail)
                retained_editor_only[sid] = candidates[0]
                row.update(status='unexpected', target=candidates[0], converted=slots[candidates[0]])
            else:
                unverified.append(detail)
                row['status'] = 'ambiguous'
                ambiguous_editor_only.add(sid)
    for sid, target in mapping.items():
        source, slot = sources[sid], slots[target]
        for field in ('activeSelf', 'activeInHierarchy'):
            if source[field] != slot[field]:
                differences.append({'kind': field, 'sourceId': sid, 'target': target,
                                    'expected': source[field], 'actual': slot[field]})
        parent = slot['parent']
        while parent and parent not in reverse:
            parent = slots[parent]['parent']
        expected_parent = source['parent']
        if expected_parent in mapping:
            if parent != mapping[expected_parent]:
                differences.append({'kind': 'parent', 'sourceId': sid, 'target': target,
                                    'expected': mapping[expected_parent], 'actual': parent})
        elif expected_parent:
            unverified.append({'kind': 'parent', 'sourceId': sid, 'reason': 'source parent is unmapped'})
    # Compare source siblings by their positions below the mapped source parent.
    # Helper slots may intervene; their indices are part of the ordering path.
    # Missing children must not turn a partial match into a successful group.
    sibling_orders = []
    for parent, children in source_children.items():
        children = [child for child in children if child['id'] not in excluded]
        if len(children) < 2:
            continue
        expected = [c['id'] for c in sorted(children, key=lambda c: c['siblingIndex'])]
        row = {'sourceParent': parent, 'expected': expected, 'status': 'unverified'}
        sibling_orders.append(row)
        positions = {}
        if parent in mapping and all(child in mapping for child in expected):
            for child in expected:
                target, indices = mapping[child], []
                while target and target != mapping[parent]:
                    indices.append(slots[target]['siblingIndex'])
                    target = slots[target]['parent']
                    if target in reverse and target != mapping[parent]:
                        break
                if target == mapping[parent] and indices:
                    positions[child] = tuple(reversed(indices))
        if len(positions) != len(expected):
            row['reason'] = 'source parent/child is unmapped or has a different mapped parent'
            unverified.append({'kind': 'sibling-order', **row})
            continue
        actual = sorted(expected, key=lambda child: positions[child])
        row.update(actual=actual, status='matched' if actual == expected else 'different')
        if actual != expected:
            differences.append({'kind': 'sibling-order', **row})
    renderer_rows = []
    sizes = {}
    source_skins = {r['id'] for r in unity['sections']['renderers'] if r['type'] == 'UnityEngine.SkinnedMeshRenderer'}
    initial_pose = unity.get('initialPoseWeights', {})
    require(isinstance(initial_pose, dict) and initial_pose.keys() <= source_skins, 'unknown initial pose renderer')
    for size in unity.get('skinSizes', []):
        require(isinstance(size, dict) and size.get('rendererId') in source_skins and
                size['rendererId'] not in sizes, 'unknown or duplicate skin size record')
        sizes[size['rendererId']] = size
    skin_counts = collections.Counter()
    palettes = {}
    for record in unity.get('skinBindings', []):
        require(isinstance(record, dict) and record.get('rendererId') in source_skins and
                record['rendererId'] not in palettes, 'unknown or duplicate skin binding record')
        palettes[record['rendererId']] = record
    supported_renderers = {'UnityEngine.MeshRenderer': '[FrooxEngine]FrooxEngine.MeshRenderer',
                           'UnityEngine.SkinnedMeshRenderer': '[FrooxEngine]FrooxEngine.SkinnedMeshRenderer'}
    empty_meshes = set(unity.get('emptyMeshRenderers', []))
    require(empty_meshes <= {r['id'] for r in unity['sections']['renderers']}, 'unknown empty mesh renderer')
    exclusions = {}
    for renderer in unity['sections']['renderers']:
        mesh = renderer['mesh']
        if not mesh.get('guid') and mesh.get('fileId') == '0':
            exclusions[renderer['id']] = 'empty Unity mesh reference'
        elif renderer['id'] in empty_meshes:
            exclusions[renderer['id']] = 'Unity mesh has no geometry'
    expected_counts = collections.Counter((r['transform'], supported_renderers[r['type']])
                                          for r in unity['sections']['renderers'] if r['type'] in supported_renderers)
    optional_counts = collections.Counter((r['transform'], supported_renderers[r['type']])
                                          for r in unity['sections']['renderers'] if r['id'] in exclusions and r['type'] in supported_renderers)
    children = collections.defaultdict(list)
    for slot in slots.values():
        children[slot['parent']].append(slot)
    renderer_slots = {}
    for sid, target in (mapping | retained_editor_only).items():
        for kind in supported_renderers.values():
            count = expected_counts[sid, kind]
            owner = slots[target]
            if count == 1 and not any(c['type'] == kind for c in owner['components']):
                # An imported FBX can keep its same-named mesh directly below the
                # prefab object or under RootNode. Never claim another mapped object.
                candidates = [s for s in children[target] if s['id'] not in reverse]
                wrappers = [s for s in candidates if s['path'][-1] == 'RootNode']
                candidates.extend(s for w in wrappers for s in children[w['id']] if s['id'] not in reverse)
                matches = [s for s in candidates if s['path'][-1] == sources[sid]['name']
                           and s['activeInHierarchy'] == owner['activeInHierarchy']
                           and any(c['type'] == kind for c in s['components'])]
                competing = any(r['transform'] not in excluded and sources[r['transform']]['parent'] == sid and
                                sources[r['transform']]['name'] == sources[sid]['name'] and
                                supported_renderers.get(r['type']) == kind for r in unity['sections']['renderers'])
                if len(matches) == 1 and not competing:
                    owner = matches[0]
            renderer_slots[sid, kind] = owner['id']
            saved_ids = [c['id'] for c in owner['components'] if c['type'] == kind]
            if sid not in excluded and not count - optional_counts[sid, kind] <= len(saved_ids) <= count:
                differences.append({'kind': 'renderer-count', 'sourceId': sid, 'target': target, 'type': kind,
                                    'expected': count, 'actual': len(saved_ids), 'convertedComponents': saved_ids})
    for renderer in unity['sections']['renderers']:
        kind = supported_renderers.get(renderer['type'])
        target = renderer_slots.get((renderer['transform'], kind), mapping.get(renderer['transform']))
        candidates = [c for c in slots[target]['components'] if c['type'] == kind] if target else []
        unique_pair = len(candidates) == 1 and expected_counts[renderer['transform'], kind] == 1
        row = {'sourceId': renderer['id'], 'targetSlot': target, 'sourceBones': renderer.get('bones'),
               'sourceRootBone': renderer.get('rootBone'), 'status': 'unverified'}
        renderer_rows.append(row)
        if renderer['transform'] in excluded:
            row.update(status='excluded', reason='EditorOnly hierarchy')
            if candidates:
                row.update(status='unexpected')
                differences.append(dict(kind='editor-only-renderer', sourceId=renderer['id'], target=target))
            elif renderer['transform'] in ambiguous_editor_only:
                row.update(status='unverified', reason='ambiguous EditorOnly slot')
            continue
        if renderer['id'] in exclusions and not candidates:
            row.update(status='excluded', reason=exclusions[renderer['id']])
            continue
        if renderer['id'] in source_skins:
            saved = (candidates[0].get('skinSize') if unique_pair else
                     {'reason': 'renderer missing or ambiguous on mapped slot'})
            scale = compare_skin_size(sizes.get(renderer['id']), saved)
            row['skinScale'] = scale
            skin_counts[scale['status']] += 1
            issue = {'kind': 'skin-scale', 'sourceId': renderer['id'], 'path': renderer['path'],
                     'target': target, **scale}
            if scale['status'] == 'unverified':
                unverified.append(issue)
            elif scale['status'] in ('expanded', 'shrunk'):
                differences.append(issue)
        if not unique_pair:
            row.update(reason='renderer missing or ambiguous on mapped slot', candidateCount=len(candidates))
            unverified.append({'kind': 'renderer', **row})
            continue
        component = candidates[0]
        row.update(targetComponent=component['id'], convertedBones=component['bones'], status='compared')
        if renderer['type'] == 'UnityEngine.MeshRenderer' and renderer['mesh'] == {
                'guid': '0000000000000000e000000000000000', 'fileId': '10207'}:
            sphere = compare_sphere(sources, renderer['transform'], slots, target, component, reverse)
            row['sphere'] = sphere
            if sphere['status'] != 'matched':
                issues = differences if sphere['status'] == 'different' else unverified
                issues.append(dict(kind='builtin-sphere', sourceId=renderer['id'], **sphere))
        if component['bones'] is not None:
            shape_names = component.get('blendShapeNames')
            source_shapes = renderer['blendShapes']
            weights = component['blendShapeWeights']
            if shape_names is None:
                if weights or source_shapes:
                    unverified.append(dict(kind='blend-shapes', sourceId=renderer['id'], reason='saved channel names missing'))
            elif shape_names != [s['name'] for s in source_shapes] or len(weights) != len(source_shapes):
                differences.append(dict(kind='blend-shape-channels', sourceId=renderer['id'],
                                        unity=[s['name'] for s in source_shapes], saved=shape_names, weights=len(weights)))
            else:
                neutral = initial_pose.get(renderer['id'], {})
                require(isinstance(neutral, dict) and set(neutral).issubset(shape_names) and all(type(v) in (int, float) and math.isfinite(v)
                        for v in neutral.values()), 'invalid evaluated initial pose weights')
                for shape, weight in zip(source_shapes, weights):
                    name = shape['name']
                    expected = neutral.get(name, shape['weight'] / 100)
                    if abs(weight - expected) > 1e-5:
                        differences.append(dict(kind='blend-shape-weight', sourceId=renderer['id'], shape=name,
                                                expected=expected, actual=weight, evaluatedNeutral=name in neutral))
            row['boneTargets'] = compare_bone_targets(renderer, component, palettes.get(renderer['id']),
                                                      mapping, differences, unverified)
    incoming = collections.defaultdict(list)
    unresolved = []
    for slot in slots.values():
        for component in slot['components']:
            for ref in component['slotReferences']:
                incoming[ref['target']].append({'slot': slot['id'], 'component': component['id'],
                                                'type': component['type'], 'field': ref['field']})
            invalid_refs = [(i, ref) for i, ref in enumerate(component['bones'] or []) if ref['status'] != 'resolved']
            if invalid_refs:
                palette = weighted_palette((component.get('skinSize') or {}).get('bindings'))
                used = set(palette[1]) if palette else None
                unresolved.extend({'component': component['id'], 'index': i,
                                   'weighted': i in used if used is not None else None, **ref} for i, ref in invalid_refs)
            for bone, ref in (component['rigBones'] or {}).items():
                if ref['status'] != 'resolved':
                    unresolved.append({'component': component['id'], 'bone': bone, **ref})
    source_names = collections.defaultdict(list)
    for sid, source in sources.items():
        source_names[source['name']].append(sid)
    extras = [{'id': s['id'], 'parent': s['parent'], 'path': s['path'],
               'componentTypes': [c['type'] for c in s['components']], 'referencedBy': incoming[s['id']],
               'sameNameSourceIds': source_names[s['path'][-1]]}
              for s in slots.values() if s['id'] not in reverse]
    extra_ids = {s['id'] for s in extras}
    branches, groups = [], collections.defaultdict(list)
    for extra in extras:
        root = extra['id']
        while slots[root]['parent'] in extra_ids:
            root = slots[root]['parent']
        groups[root].append(extra)
    for root, descendants in groups.items():
        extra = slots[root]
        branches.append({'id': extra['id'], 'path': extra['path'], 'unmappedSlotCount': len(descendants),
                         'referencedSlotCount': sum(bool(s['referencedBy']) for s in descendants),
                         'componentCounts': dict(collections.Counter(t for s in descendants for t in s['componentTypes']))})
    unmatched_sources = [r for r in rows if r['target'] is None and r['sourceId'] not in excluded]
    paired = {r['sourceId']: r['targetComponent'] for r in renderer_rows if 'targetComponent' in r}
    visemes = compare_visemes(unity, converted, slots, rows, paired, excluded)
    if visemes['status'] == 'different':
        differences.append({'kind': 'viseme-bindings', **visemes})
    elif visemes['status'] == 'unverified' and 'lipSync' in unity:
        unverified.append({'kind': 'viseme-bindings', **visemes})
    review = parser is None or bool(before_diffs or differences or unverified or extras or unmatched_sources
                                    or any(r.get('weighted') is not False for r in unresolved))
    bone_counts = collections.Counter(r['boneTargets']['status'] if 'boneTargets' in r else
                                     'excluded' if r['status'] == 'excluded' else 'unpaired'
                                     for r in renderer_rows if r['sourceId'] in source_skins)
    return {'format': 'resopon.structure-comparison', 'schemaVersion': 1,
            'status': 'needs-review' if review else 'matched-covered-fields',
            'parserParity': {'status': 'unverified' if parser is None else 'different' if before_diffs else 'matched', 'differences': before_diffs},
            'visemes': visemes,
            'summary': {'visemeStatus': visemes['status'], 'skinScale': dict(skin_counts), 'boneTargets': dict(bone_counts),
                        'unityTransforms': len(sources), 'convertedSlots': len(slots),
                        'mappingCandidates': len(mapping), 'unmappedSources': len(unmatched_sources),
                        'unmappedSlots': len(extras), 'differences': len(differences),
                        'unverified': len(unverified), 'unresolvedOrNullBones': len(unresolved)},
            'limitations': ['Name paths are candidates, not source provenance; duplicate paths stay ambiguous.',
                            'Local TRS is captured but not compared across different coordinate spaces/parents.',
                            'Weighted bone targets are compared. Per-vertex weights, bind poses, rootBone equivalence, physics behavior and visuals are unverified.',
                            'Extra slots can be required converter support; do not delete based on this report alone.'],
            'transforms': rows, 'siblingOrders': sibling_orders, 'renderers': renderer_rows,
            'differences': differences, 'unverified': unverified,
            'unmappedSlots': extras, 'unmappedSubtrees': branches, 'unresolvedOrNullBones': unresolved}


def compare_captures(unity, parser, converted, paths, mapping_path=None, initial_pose_path=None):
    inputs = {name: {'path': str(path.resolve()), 'sha256': sha256(path)} for name, path in paths.items()}
    overrides = None
    if mapping_path is not None:
        digest = sha256(mapping_path)
        mapping = snapshot_check.load(mapping_path)
        require(mapping['unitySha256'] == inputs['unity']['sha256'] and
                mapping['packageSha256'] == converted['packageSha256'], 'stale mapping hashes')
        overrides = mapping['transforms']
        require(isinstance(overrides, dict), 'mapping transforms must be an object')
        require(sha256(mapping_path) == digest, 'mapping changed during comparison')
        inputs['mapping'] = {'path': str(mapping_path.resolve()), 'sha256': digest}
    if initial_pose_path is not None:
        digest = sha256(initial_pose_path)
        pose = snapshot_check.load(initial_pose_path)
        require(pose['unitySha256'] == inputs['unity']['sha256'] and sha256(initial_pose_path) == digest,
                'stale initial pose evidence')
        unity = dict(unity, initialPoseWeights=pose['initialPoseWeights'])
        inputs['initialPose'] = {'path': str(initial_pose_path.resolve()), 'sha256': digest}
    report = compare(unity, parser, converted, overrides)
    report.update(inputs=inputs, packageSha256=converted['packageSha256'])
    return report


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('unity', type=Path)
    ap.add_argument('parser', type=Path, nargs='?', help='optional pre-conversion snapshot')
    ap.add_argument('converted', type=Path)
    ap.add_argument('--report', type=Path, required=True)
    ap.add_argument('--mapping', type=Path, help='reviewed mapping bound to unitySha256 and packageSha256')
    ap.add_argument('--initial-pose', type=Path, help='source neutral weights by renderer ID and channel, bound to unitySha256')
    args = ap.parse_args(argv)
    try:
        unity = unity_compare.load_snapshot(args.unity)
        parser = unity_compare.load_snapshot(args.parser) if args.parser else None
        converted = snapshot_check.load(args.converted)
        paths = {'unity': args.unity, 'converted': args.converted}
        if args.parser:
            paths['parser'] = args.parser
        report = compare_captures(unity, parser, converted, paths, args.mapping, args.initial_pose)
        with args.report.open('x', encoding='utf-8') as stream:
            json.dump(report, stream, ensure_ascii=False, indent=2, allow_nan=False)
        print(json.dumps(report['summary']))
        return 1 if report['status'] == 'needs-review' else 0
    except (OSError, ValueError, KeyError, TypeError) as error:
        print(str(error), file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
