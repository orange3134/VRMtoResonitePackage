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
    span = max((max(axis) - min(axis) for axis in zip(*points)), default=0)
    if not span and not saved_faces:
        return dict(status='unverified', reason='no mesh extent')
    if not unity_faces or not saved_faces:
        return dict(status='different' if unity_faces or saved_faces else 'matched', matchedTriangles=0,
                    unmatchedUnity=len(unity_faces), unmatchedSaved=len(saved_faces))
    tolerance = span * 1e-5
    def cell(p):
        return tuple(math.floor(n / tolerance) for n in p)
    grid = defaultdict(list)
    for i, p in enumerate(points):
        grid[cell(p)].append(i)
    candidates = []
    for p in saved:
        x, y, z = cell(p)
        candidates.append([i for a, b, c in product((-1, 0, 1), repeat=3)
                           for i in grid.get((x+a, y+b, z+c), ()) if math.dist(p, points[i]) <= tolerance])
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
    neighbors = defaultdict(list)
    for spans in intervals.values():
        furthest = owner = None
        for start, end, face in sorted(spans):
            if furthest is not None and start < furthest:
                neighbors[owner].append(face)
                neighbors[face].append(owner)
                if end <= furthest:
                    continue
            furthest, owner = end, face
    return lines, neighbors


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
    remaining = set(left) | set(right)
    lines, neighbors = patch_edges(points, remaining)
    unmatched = [0, 0]
    equivalent = 0
    while remaining:
        seed = remaining.pop()
        stack, patch = [seed], []
        while stack:
            face = stack.pop()
            patch.append(face)
            for adjacent in neighbors[face]:
                if adjacent in remaining:
                    remaining.remove(adjacent)
                    stack.append(adjacent)
        ref = max(patch, key=lambda f: math.hypot(*normal(f)))
        axis = normal(ref)
        origin = points[ref[0]]
        vertices = {i for f in patch for i in f}
        # Use the patch's own size so a tiny detail on a large model is not flattened.
        extent = max(max(points[i][k] for i in vertices) - min(points[i][k] for i in vertices) for k in range(3))
        tolerance = math.hypot(*axis) * extent * 1e-5
        planar = all(abs(dot(axis, subtract(points[i], origin))) <= tolerance for i in vertices)
        boundary = Counter()
        if planar:
            for face in patch:
                orientation = dot(axis, normal(face))
                if orientation == 0:
                    planar = False
                    break
                count = (left[face] - right[face]) * (1 if orientation > 0 else -1)
                for a, b in edges(face):
                    # On each line, oriented coverage changes only at endpoints.
                    # Splitting an edge adds equal and opposite changes at its new vertex.
                    line = lines[min(a, b), max(a, b)]
                    boundary[line, tuple(points[a])] += count
                    boundary[line, tuple(points[b])] -= count
        if planar and not any(boundary.values()):
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
