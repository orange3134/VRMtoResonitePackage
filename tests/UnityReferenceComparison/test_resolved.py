"""Invented resolved hierarchy, including duplicate names and repeated source IDs."""
import copy
import unittest
from test_compare import comparison, snapshot as authored


def snapshot():
    source = {"guid": "a" * 32, "fileId": "9223372036854775806"}
    def identity(id, path, type):
        return dict(id=id, path=path, type=type, source=source.copy(), originalSource=source.copy(), activeInHierarchy=True)
    transforms = []
    for id, parent, index, name in [("r", "", 0, "Root"), ("r/0", "r", 0, "Twin"), ("r/1", "r", 1, "Twin")]:
        transforms.append(dict(identity(id, name, "UnityEngine.Transform"), parent=parent, name=name, siblingIndex=index,
                               activeSelf=True, localPosition=dict(x=0., y=0., z=0.), localScale=dict(x=1., y=1., z=1.),
                               localRotation=dict(x=0., y=0., z=0., w=1.)))
    colliders = [dict(identity(id + "/c:Collider:0", "Twin", "Collider"), transform=id, root=id,
                      enabled=True, colliders=[], parameters=[]) for id in ["r/0", "r/1"]]
    bone = dict(identity("r/c:Bone:0", "", "Bone"), transform="r", root="r", enabled=True,
                colliders=[colliders[1]["id"], colliders[0]["id"]], parameters=[dict(name="pull", value=.2)])
    renderer = dict(identity("r/0/c:Renderer:0", "Twin", "Renderer"), transform="r/0", rootBone="r",
                    enabled=True, activeSelf=True, mesh=source.copy(), materials=[source.copy()], bones=["r/0", "r/1"],
                    blendShapes=[dict(index=0, name="Smile", weight=20.)])
    return dict(format=comparison.FORMAT, schemaVersion=2, projection="unity-resolved-prefab", prefabGuid="b" * 32,
                sections=dict(transforms=transforms, renderers=[renderer], physBones=[bone], colliders=colliders))


class ResolvedTests(unittest.TestCase):
    def test_quaternion_sign_is_equivalent_but_rotation_changes_fail(self):
        original = snapshot()
        changed = copy.deepcopy(original)
        changed["sections"]["transforms"][0]["localRotation"]["w"] = -1.
        self.assertEqual([], comparison.compare(original, changed))
        changed["sections"]["transforms"][0]["localRotation"]["x"] = .1
        self.assertTrue(comparison.compare(original, changed))

    def test_repeated_instances_are_distinct(self):
        original = snapshot()
        changed = copy.deepcopy(original)
        changed["sections"]["colliders"][1]["enabled"] = False
        self.assertEqual([], comparison.compare(original, original))
        self.assertTrue(any("r/1/c:Collider:0" in d for d in comparison.compare(original, changed)))

    def test_order_of_records_is_irrelevant_but_references_are_ordered(self):
        original = snapshot()
        changed = copy.deepcopy(original)
        changed["sections"]["transforms"].reverse()
        self.assertEqual([], comparison.compare(original, changed))
        changed["sections"]["physBones"][0]["colliders"].reverse()
        self.assertTrue(comparison.compare(original, changed))

    def test_missing_component_is_difference(self):
        original = snapshot()
        changed = copy.deepcopy(original)
        changed["sections"]["renderers"] = []
        self.assertTrue(comparison.compare(original, changed))

    def test_dangling_references_fail(self):
        for section, key, value in [("physBones", "colliders", ["r/7/c:Collider:0"]),
                                    ("renderers", "bones", ["r/7"]), ("colliders", "root", "r/7")]:
            with self.subTest(section=section):
                data = snapshot()
                data["sections"][section][0][key] = value
                with self.assertRaises(ValueError):
                    comparison.validate_snapshot(data)

    def test_duplicate_and_invalid_hierarchy_fail(self):
        for key, value in [("id", "r/1"), ("parent", "r/1"), ("siblingIndex", True)]:
            with self.subTest(key=key):
                data = snapshot()
                data["sections"]["transforms"][1][key] = value
                with self.assertRaises(ValueError):
                    comparison.validate_snapshot(data)

    def test_mixed_projections_rejected(self):
        with self.assertRaises(ValueError):
            comparison.compare(authored(), snapshot())

    def test_transform_and_weight_changes_detected(self):
        for section, key, value in [("transforms", "localScale", dict(x=2., y=2., z=2.)),
                                    ("renderers", "blendShapes", [dict(index=0, name="Smile", weight=80.)])]:
            with self.subTest(section=section):
                original = snapshot()
                changed = copy.deepcopy(original)
                changed["sections"][section][0][key] = value
                self.assertTrue(comparison.compare(original, changed))

    def test_invalid_numeric_state_rejected(self):
        for value in [float("nan"), True, "0"]:
            data = snapshot()
            data["sections"]["transforms"][0]["localPosition"]["x"] = value
            with self.assertRaises(ValueError):
                comparison.validate_snapshot(data)


if __name__ == "__main__":
    unittest.main()
