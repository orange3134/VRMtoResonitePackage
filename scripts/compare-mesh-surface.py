"""Compare base surfaces with independently established, shared position IDs."""
import argparse
from collections import Counter, defaultdict
from itertools import product
import json
import math
from pathlib import Path


def subtract(a, b):
    return tuple(x - y for x, y in zip(a, b))


def cross(a, b):
    return (a[1]*b[2] - a[2]*b[1], a[2]*b[0] - a[0]*b[2], a[0]*b[1] - a[1]*b[0])


def dot(a, b):
    return sum(x*y for x, y in zip(a, b))


def edges(face):
    a, b, c = face
    return ((a, b), (b, c), (c, a))


def validate_positions(points):
    if not all(len(p) == 3 and all(math.isfinite(n) for n in p) for p in points):
        raise ValueError('Expected finite 3D positions')


def validate_face(face, count):
    if len(face) != 3 or any(type(i) != int or not 0 <= i < count for i in face):
        raise ValueError('Invalid triangle position ID')


def compare_positioned(data):
    """Match triangle occurrences without requiring a unique nearest vertex ID."""
    points, saved = data['positions'], data['savedPositions']
    validate_positions(points)
    validate_positions(saved)
    def drawn_faces(positions, triangles):
        result = []
        for face in triangles:
            validate_face(face, len(positions))
            a, b, c = (positions[i] for i in face)
            if any(cross(subtract(b, a), subtract(c, a))):
                result.append(face)
        return result
    unity_faces = drawn_faces(points, data['unityTriangles'])
    saved_faces = drawn_faces(saved, data['savedTriangles'])
    if not unity_faces and not saved_faces:
        return dict(status='unverified', reason='no drawable faces')
    if not unity_faces or not saved_faces:
        return dict(status='different', matchedTriangles=0,
                    unmatchedUnity=len(unity_faces), unmatchedSaved=len(saved_faces))
    used = {i for face in unity_faces for i in face}
    tolerance = extent(points, used) * 1e-5
    def cell(p):
        return tuple(math.floor(n / tolerance) for n in p)
    grid = defaultdict(list)
    for i in used:
        grid[cell(points[i])].append(i)
    candidates = {}
    for j in {i for face in saved_faces for i in face}:
        p = saved[j]
        x, y, z = cell(p)
        candidates[j] = [i for a, b, c in product((-1, 0, 1), repeat=3)
                         for i in grid.get((x+a, y+b, z+c), ()) if math.dist(p, points[i]) <= tolerance]
    # Each source triangle occurrence can be claimed once. An augmenting path
    # can revise an earlier choice when several near-coincident corners compete.
    source = defaultdict(list)
    for i, face in enumerate(unity_faces):
        source[tuple(sorted(face))].append(i)
    choices = []
    for a, b, c in saved_faces:
        options = {index for corners in product(candidates[a], candidates[b], candidates[c])
                   for index in source.get(tuple(sorted(corners)), ())}
        choices.append(options)
    owner = {}
    def assign(start):
        stack, parents, visited = [start], {start: None}, set()
        while stack:
            current = stack.pop()
            for index in choices[current] - visited:
                visited.add(index)
                if index not in owner:
                    while True:
                        owner[index] = current
                        if parents[current] is None:
                            return True
                        current, index = parents[current]
                occupant = owner[index]
                if occupant not in parents:
                    parents[occupant] = (current, index)
                    stack.append(occupant)
        return False
    for i in sorted(range(len(choices)), key=lambda i: len(choices[i])):
        assign(i)
    # When direct matching fails there may still be a planar retriangulation.
    # That requires a common position table before the boundary test below.
    missing = len(unity_faces) - len(owner)
    extra = len(saved_faces) - len(owner)
    return dict(status='unverified' if missing or extra else 'matched',
                matchedTriangles=len(owner), unmatchedUnity=missing, unmatchedSaved=extra)


def patch_edges(points, faces):
    # Represent the input coordinates on one exact binary grid. Parallel lines that
    # are merely close together must stay separate, even on a very narrow triangle.
    vertices = {i for face in faces for i in face}
    ratios = {i: [n.as_integer_ratio() for n in points[i]] for i in vertices}
    scale = max((den for point in ratios.values() for _, den in point), default=1)
    grid = {i: tuple(num * (scale // den) for num, den in point) for i, point in ratios.items()}
    lines = {}
    intervals = defaultdict(list)
    for face in faces:
        for a, b in edges(face):
            key = min(a, b), max(a, b)
            start, end = sorted((grid[a], grid[b]))
            if key not in lines:
                delta = subtract(end, start)
                divisor = math.gcd(*delta)
                direction = tuple(d // divisor for d in delta)
                # The cross product is constant along the line with this direction.
                lines[key] = direction, cross(start, direction)
            intervals[lines[key]].append((start, end, face))

    # Connect faces sharing any positive length, including a long edge meeting
    # two shorter edges. A single shared endpoint does not connect two patches.
    edge_groups = []
    memberships = defaultdict(list)
    for spans in intervals.values():
        if len(spans) < 2:
            continue
        events = defaultdict(lambda: ([], []))
        for start, end, face in spans:
            events[start][0].append(face)
            events[end][1].append(face)
        active = set()
        added = False
        for _, (starts, ends) in sorted(events.items()):
            if ends:
                # Record faces covering the same open interval as a group. This
                # keeps every plane reachable at a crease without storing every
                # pair of neighbours. Successive ends only shrink the last group.
                if added and len(active) > 1:
                    index = len(edge_groups)
                    edge_groups.append(tuple(active))
                    for face in active:
                        memberships[face].append(index)
                active.difference_update(ends)
                added = False
            if starts:
                active.update(starts)
                added = True
    return lines, memberships, edge_groups


def extent(points, indices):
    return max(max(points[i][k] for i in indices) - min(points[i][k] for i in indices) for k in range(3))


def connected_patch(seed, remaining, memberships, edge_groups, accepts=None):
    remaining.remove(seed)
    stack, patch = [seed], []
    visited = set()
    while stack:
        face = stack.pop()
        patch.append(face)
        for index in memberships[face]:
            if index in visited:
                continue
            visited.add(index)
            for other in edge_groups[index]:
                if other in remaining and (accepts is None or accepts(other)):
                    remaining.remove(other)
                    stack.append(other)
    return patch


def planar_patches(points, faces, memberships, edge_groups, normals):
    remaining = set(faces)
    while remaining:
        region = connected_patch(next(iter(remaining)), remaining, memberships, edge_groups)
        region_extent = extent(points, {i for f in region for i in f})
        available = set(region)
        # A large face gives a stable reference plane. First preserve the usual
        # tolerance for a single, nearly planar region. At a fold, grow each
        # patch on its own seed plane so a chain of faces cannot turn a corner.
        for seed in sorted(region, key=lambda f: (math.hypot(*normals[f]), f), reverse=True):
            if seed not in available:
                continue
            axis, origin = normals[seed], points[seed[0]]
            length = math.hypot(*axis)

            def fits(face, scale):
                return dot(axis, normals[face]) != 0 and all(
                    abs(dot(axis, subtract(points[i], origin))) <= length * scale * 1e-5 for i in face)

            if len(available) == len(region) and all(fits(face, region_extent) for face in region):
                yield region, axis
                break
            seed_extent = extent(points, seed)
            yield connected_patch(seed, available, memberships, edge_groups,
                                  lambda f: fits(f, max(seed_extent, extent(points, f)))), axis


def compare(data):
    if 'savedPositions' in data:
        return compare_positioned(data)
    points = data['positions']
    validate_positions(points)
    def normal(face):
        a, b, c = (points[i] for i in face)
        return cross(subtract(b, a), subtract(c, a))
    def faces(triangles):
        result = Counter()
        for face in triangles:
            validate_face(face, len(points))
            if any(normal(face)):
                result[tuple(sorted(face))] += 1
        return result
    unity, saved = (faces(data[key]) for key in ('unityTriangles', 'savedTriangles'))
    left, right = unity - saved, saved - unity
    # Connected residual patches must lie on one plane and cover the same area.
    # After orienting each triangle toward that plane, its signed boundary counts
    # encode the jump in coverage (including overlaps and holes) across each edge.
    residual = set(left) | set(right)
    normals = {face: normal(face) for face in residual}
    lines, memberships, edge_groups = patch_edges(points, residual)
    unmatched = [0, 0]
    equivalent = 0
    for patch, axis in planar_patches(points, residual, memberships, edge_groups, normals):
        boundary = Counter()
        for face in patch:
            count = (left[face] - right[face]) * (1 if dot(axis, normals[face]) > 0 else -1)
            for a, b in edges(face):
                # Splitting an edge adds equal and opposite changes at its new vertex.
                line = lines[min(a, b), max(a, b)]
                boundary[line, tuple(points[a])] += count
                boundary[line, tuple(points[b])] -= count
        if not any(boundary.values()):
            equivalent += 1
        else:
            unmatched[0] += sum(left[f] for f in patch)
            unmatched[1] += sum(right[f] for f in patch)
    return dict(status='different' if any(unmatched) else 'matched', equivalentPlanarPatches=equivalent,
                unmatchedUnity=unmatched[0], unmatchedSaved=unmatched[1],
                degenerateUnity=len(data['unityTriangles']) - unity.total(),
                degenerateSaved=len(data['savedTriangles']) - saved.total())


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mesh', type=Path)
    result = compare(json.loads(parser.parse_args().mesh.read_bytes()))
    print(json.dumps(result))
    raise SystemExit(result['status'] != 'matched')
