import runpy
import unittest
from pathlib import Path

compare = runpy.run_path(str(Path(__file__).resolve().parents[2] / 'scripts/compare-mesh-surface.py'))['compare']


class MeshSurfaceTests(unittest.TestCase):
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
