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
