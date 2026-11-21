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
        if explicit:
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
    for row in rows:
        if row['target'] and targets[row['target']] > 1:
            mapping.pop(row['sourceId'])
            row.update(status='ambiguous', target=None, converted=None)
    reverse = {target: source for source, target in mapping.items()}
    differences, unverified = [], []
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
    source_children = collections.defaultdict(list)
    for source in sources.values():
        if source['parent']:
            source_children[source['parent']].append(source)
    for parent, children in source_children.items():
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
    for size in unity.get('skinSizes', []):
        require(isinstance(size, dict) and size.get('rendererId') in source_skins and
                size['rendererId'] not in sizes, 'unknown or duplicate skin size record')
        sizes[size['rendererId']] = size
    skin_counts = collections.Counter()
    supported_renderers = {'UnityEngine.MeshRenderer': '[FrooxEngine]FrooxEngine.MeshRenderer',
                           'UnityEngine.SkinnedMeshRenderer': '[FrooxEngine]FrooxEngine.SkinnedMeshRenderer'}
    expected_counts = collections.Counter((r['transform'], supported_renderers[r['type']])
                                          for r in unity['sections']['renderers'] if r['type'] in supported_renderers)
    for sid, target in mapping.items():
        for kind in supported_renderers.values():
            saved_ids = [c['id'] for c in slots[target]['components'] if c['type'] == kind]
            count = expected_counts[sid, kind]
            if len(saved_ids) != count:
                differences.append({'kind': 'renderer-count', 'sourceId': sid, 'target': target, 'type': kind,
                                    'expected': count, 'actual': len(saved_ids), 'convertedComponents': saved_ids})
    for renderer in unity['sections']['renderers']:
        target = mapping.get(renderer['transform'])
        kind = supported_renderers.get(renderer['type'])
        candidates = [c for c in slots[target]['components'] if c['type'] == kind] if target else []
        unique_pair = len(candidates) == 1 and expected_counts[renderer['transform'], kind] == 1
        row = {'sourceId': renderer['id'], 'targetSlot': target, 'sourceBones': renderer.get('bones'),
               'sourceRootBone': renderer.get('rootBone'), 'status': 'unverified'}
        renderer_rows.append(row)
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
        if renderer['enabled'] != component['enabled']:
            differences.append({'kind': 'renderer-enabled', 'sourceId': renderer['id'],
                                'expected': renderer['enabled'], 'actual': component['enabled']})
        if component['bones'] is not None:
            expected = [mapping.get(b) if b else None for b in renderer['bones']]
            actual = [b['target'] for b in component['bones']]
            if len(expected) != len(actual):
                differences.append({'kind': 'bone-count', 'sourceId': renderer['id'],
                                    'expected': len(expected), 'actual': len(actual)})
            for i, (bone, ref) in enumerate(zip(renderer['bones'], component['bones'])):
                if bone and bone not in mapping:
                    unverified.append({'kind': 'bone', 'sourceId': renderer['id'], 'index': i,
                                       'sourceBone': bone, 'reason': 'source bone is unmapped'})
                elif ref['status'] == 'unresolved' or expected[i] != ref['target']:
                    differences.append({'kind': 'bone-target', 'sourceId': renderer['id'], 'index': i,
                                        'expected': expected[i], 'actual': ref})
    incoming = collections.defaultdict(list)
    unresolved = []
    for slot in slots.values():
        for component in slot['components']:
            for ref in component['slotReferences']:
                incoming[ref['target']].append({'slot': slot['id'], 'component': component['id'],
                                                'type': component['type'], 'field': ref['field']})
            for index, ref in enumerate(component['bones'] or []):
                if ref['status'] != 'resolved':
                    unresolved.append({'component': component['id'], 'index': index, **ref})
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
    unmatched_sources = [r for r in rows if r['target'] is None]
    review = parser is None or bool(before_diffs or differences or unverified or extras or unmatched_sources or unresolved)
    return {'format': 'resopon.structure-comparison', 'schemaVersion': 1,
            'status': 'needs-review' if review else 'matched-covered-fields',
            'parserParity': {'status': 'unverified' if parser is None else 'different' if before_diffs else 'matched', 'differences': before_diffs},
            'summary': {'skinScale': dict(skin_counts), 'unityTransforms': len(sources), 'convertedSlots': len(slots),
                        'mappingCandidates': len(mapping), 'unmappedSources': len(unmatched_sources),
                        'unmappedSlots': len(extras), 'differences': len(differences),
                        'unverified': len(unverified), 'unresolvedOrNullBones': len(unresolved)},
            'limitations': ['Name paths are candidates, not source provenance; duplicate paths stay ambiguous.',
                            'Local TRS is captured but not compared across different coordinate spaces/parents.',
                            'Mesh bind poses, vertex weights, rootBone equivalence, physics behavior and visuals are unverified.',
                            'Extra slots can be required converter support; do not delete based on this report alone.'],
            'transforms': rows, 'siblingOrders': sibling_orders, 'renderers': renderer_rows,
            'differences': differences, 'unverified': unverified,
            'unmappedSlots': extras, 'unmappedSubtrees': branches, 'unresolvedOrNullBones': unresolved}


def compare_captures(unity, parser, converted, paths, mapping_path=None):
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
    args = ap.parse_args(argv)
    try:
        unity = unity_compare.load_snapshot(args.unity)
        parser = unity_compare.load_snapshot(args.parser) if args.parser else None
        converted = snapshot_check.load(args.converted)
        paths = {'unity': args.unity, 'converted': args.converted}
        if args.parser:
            paths['parser'] = args.parser
        report = compare_captures(unity, parser, converted, paths, args.mapping)
        with args.report.open('x', encoding='utf-8') as stream:
            json.dump(report, stream, ensure_ascii=False, indent=2, allow_nan=False)
        print(json.dumps(report['summary']))
        return 1 if report['status'] == 'needs-review' else 0
    except (OSError, ValueError, KeyError, TypeError) as error:
        print(str(error), file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
