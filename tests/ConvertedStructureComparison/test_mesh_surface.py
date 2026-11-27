import runpy
import unittest
from pathlib import Path

compare = runpy.run_path(str(Path(__file__).resolve().parents[2] / 'scripts/compare-mesh-surface.py'))['compare']


class MeshSurfaceTests(unittest.TestCase):
    def test_unused_and_degenerate_vertices_do_not_set_position_tolerance(self):
        points = [[0, 0, 0], [1, 0, 0], [0, 1, 0]]
        mesh = dict(positions=points.copy(), savedPositions=[[x, y, z+1] for x, y, z in points],
                    unityTriangles=[[0, 1, 2]], savedTriangles=[[0, 1, 2]])
        original = compare(mesh)
        self.assertEqual(('unverified', 0), (original['status'], original['matchedTriangles']))
        mesh['positions'].append([200000, 0, 0])
        mesh['savedPositions'].append([-200000, 0, 0])
        self.assertEqual(original, compare(mesh))
        mesh['unityTriangles'].append([0, 1, 3])
        self.assertEqual(original, compare(mesh))
        mesh['savedPositions'][:3] = points
        self.assertEqual('matched', compare(mesh)['status'])
        mesh['unityTriangles'] = [[0, 1, 3]]
        mesh['savedTriangles'] = []
        self.assertEqual('unverified', compare(mesh)['status'])

    def test_close_vertices_need_distinct_triangle_occurrences(self):
        mesh = dict(positions=[[0, 0, 0], [.000009, 0, 0], [0, 1, 0], [0, 0, 1]],
                    savedPositions=[[.000004, 0, 0], [-.000008, 0, 0], [0, 1, 0], [0, 0, 1]],
                    unityTriangles=[[0, 2, 3], [1, 2, 3]], savedTriangles=[[0, 2, 3], [1, 2, 3]])
        self.assertEqual('matched', compare(mesh)['status'])
        mesh['savedPositions'][0] = [-.000008, 0, 0]
        self.assertEqual(1, compare(mesh)['unmatchedSaved'])

    def test_diagonal_and_degenerate_faces(self):
        mesh = dict(positions=[[0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0]],
                    unityTriangles=[[0, 1, 2], [2, 3, 0]], savedTriangles=[[0, 1, 3], [1, 2, 3], [1, 1, 2]])
        self.assertEqual('matched', compare(mesh)['status'])
        mesh['positions'][2][2] = .01
        self.assertEqual('different', compare(mesh)['status'])
        mesh['positions'][2][2] = 0
        mesh['savedTriangles'][1] = [0, 1, 3]
        self.assertEqual('different', compare(mesh)['status'])

    def test_multiset_and_missing_area(self):
        mesh = dict(positions=[[0, 0, 0], [1, 0, 0], [0, 1, 0]],
                    unityTriangles=[[0, 1, 2], [0, 1, 2]], savedTriangles=[[0, 1, 2]])
        self.assertEqual(1, compare(mesh)['unmatchedUnity'])

    def test_retriangulated_fold_checks_each_side_and_reports_missing_area(self):
        points = [[0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0], [1, 0, 1], [1, 1, 1]]
        original = [[0, 1, 2], [2, 3, 0], [1, 4, 5], [5, 2, 1]]
        refaced = [[0, 1, 3], [1, 2, 3], [1, 4, 2], [4, 5, 2]]
        mesh = dict(positions=points, unityTriangles=original, savedTriangles=refaced)
        result = compare(mesh)
        self.assertEqual(('matched', 2), (result['status'], result['equivalentPlanarPatches']))
        # Split the crease on one side only. This must still join the right faces.
        points.append([1, .5, 0])
        refaced[2:3] = [[1, 4, 6], [6, 4, 2]]
        self.assertEqual('matched', compare(mesh)['status'])
        refaced.pop()
        result = compare(mesh)
        self.assertEqual(('different', 1), (result['status'], result['equivalentPlanarPatches']))

    def test_collapsed_triangle_cannot_claim_a_drawn_triangle(self):
        thin = [[0, 0, 0], [1, 0, 0], [.5, .000005, 0]]
        flat = [[0, 0, 0], [1, 0, 0], [.5, 0, 0]]
        for before, after, missing, extra in [(thin, flat, 1, 0), (flat, thin, 0, 1)]:
            mesh = dict(positions=before, savedPositions=after, unityTriangles=[[0, 1, 2]], savedTriangles=[[0, 1, 2]])
            self.assertEqual(dict(status='different', matchedTriangles=0, unmatchedUnity=missing,
                                  unmatchedSaved=extra), compare(mesh))
            # A second, surviving face must not hide the loss in a non-empty mesh.
            mesh['positions'] = before + [[0, 1, 0]]
            mesh['savedPositions'] = after + [[0, 1, 0]]
            mesh['unityTriangles'].append([0, 1, 3])
            mesh['savedTriangles'].append([0, 1, 3])
            self.assertEqual(dict(status='unverified', matchedTriangles=1, unmatchedUnity=missing,
                                  unmatchedSaved=extra), compare(mesh))

    def test_planar_boundary_subdivision_preserves_coverage(self):
        mesh = dict(positions=[[0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0], [.5, 0, 0]],
                    unityTriangles=[[0, 1, 2], [0, 2, 3]], savedTriangles=[[0, 4, 2], [4, 1, 2], [0, 2, 3]])
        self.assertEqual('matched', compare(mesh)['status'])
        mesh['positions'][4][1] = .000001
        self.assertEqual('different', compare(mesh)['status'])

    def test_subdivide_all_edges_on_an_oblique_plane(self):
        mesh = dict(positions=[[0, 0, 0], [2, 2, 0], [0, 2, 2], [1, 1, 0], [1, 2, 1], [0, 1, 1]],
                    unityTriangles=[[0, 1, 2]], savedTriangles=[[0, 3, 5], [1, 4, 3], [2, 5, 4], [3, 4, 5]])
        self.assertEqual('matched', compare(mesh)['status'])
        mesh['savedTriangles'].pop()
        self.assertEqual('different', compare(mesh)['status'])
        mesh['savedTriangles'].append(mesh['savedTriangles'][0])
        self.assertEqual('different', compare(mesh)['status'])
