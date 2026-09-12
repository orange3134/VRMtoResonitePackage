using VrmToResonitePackage.Unity;

internal static class PrefabGraphChecks
{
    public static void Run(Func<string, string, string, string> asset)
    {
        const string leafGuid = "ac140000000000000000000000000001";
        const string variantGuid = "ac140000000000000000000000000002";
        const string outerGuid = "ac140000000000000000000000000003";
        string leafText = """
--- !u!1 &1
GameObject:
  m_Name: Root
--- !u!4 &2
Transform:
  m_GameObject: {fileID: 1}
  m_Father: {fileID: 0}
--- !u!1 &3
GameObject:
  m_Name: Body
--- !u!4 &4
Transform:
  m_GameObject: {fileID: 3}
  m_Father: {fileID: 2}
  m_LocalPosition: {x: 2, y: 3, z: 4}
--- !u!114 &5
MonoBehaviour:
  m_GameObject: {fileID: 3}
  m_Script: {fileID: 1661641543, guid: 2a2c05204084d904aa4945ccff20d8e5}
  rootTransform: {fileID: 4}
  pull: 0.2
--- !u!114 &6
MonoBehaviour:
  m_GameObject: {fileID: 3}
  m_Script: {guid: unknownScript}
  arbitrary:
    amount: 1
    title: original
    references:
    - {fileID: 4}
    packed: 0100000002000000
    emptyPacked:
--- !u!137 &7
SkinnedMeshRenderer:
  m_GameObject: {fileID: 3}
  m_Bones:
  - {fileID: 4}
  m_Materials:
  - {fileID: 2100000, guid: originalMaterial}
  m_BlendShapeWeights: [90, 10]

""";
        string leaf = asset("Assets/GraphBase.prefab", leafGuid, leafText);
        string Modification(long id, string path, string value, string reference = "{fileID: 0}") =>
            $"    - target: {{guid: {leafGuid}, fileID: {id}}}\n      propertyPath: {path}\n      value: {value}\n      objectReference: {reference}\n";
        string variant = $"--- !u!1001 &100\nPrefabInstance:\n  m_SourcePrefab: {{guid: {leafGuid}}}\n  m_Modification:\n    m_Modifications:\n" +
            Modification(5, "pull", "0.4") + Modification(6, "arbitrary.amount", "7") +
            Modification(6, "arbitrary.title", "") + Modification(6, "arbitrary.references.Array.size", "2") +
            Modification(6, "arbitrary.packed.Array.data[1]", "-1") +
            Modification(6, "arbitrary.emptyPacked.Array.size", "2") +
            Modification(6, "arbitrary.emptyPacked.Array.data[0]", "-1") +
            Modification(7, "m_Bones.Array.data[0]", "") + Modification(7, "m_Materials.Array.size", "0") +
            Modification(7, "m_BlendShapeWeights.Array.data[0]", "0") + Modification(4, "m_LocalPosition.x", "1.5");
        asset("Assets/GraphVariant.prefab", variantGuid, variant);
        string outerText = $"--- !u!1001 &200\nPrefabInstance:\n  m_SourcePrefab: {{guid: {variantGuid}}}\n  m_Modification:\n    m_Modifications:\n" +
            Modification(5, "pull", "0.8") + Modification(6, "arbitrary.references.Array.data[1]", "", "{fileID: 901}") +
            Modification(4, "m_Father", "", "{fileID: 901}") +
            $"--- !u!1001 &201\nPrefabInstance:\n  m_SourcePrefab: {{guid: {variantGuid}}}\n" +
            "--- !u!1 &900\nGameObject:\n  m_Name: ExternalTarget\n--- !u!4 &901\nTransform:\n  m_GameObject: {fileID: 900}\n  m_Father: {fileID: 0}\n";
        string outer = asset("Assets/GraphOuter.prefab", outerGuid, outerText);
        using (var source = UnityPackage.Open(outer))
        {
            var cached = source.ReadScene(source.ByGuid(leafGuid));
            using var view = UnityPrefabInstances.CreateView(source, outerGuid, null);
            var leaves = view.PrefabGraph.Scenes.Where(entry => view.ByGuid(entry.Guid).SourceGuid == leafGuid).ToArray();
            Require(leaves.Length == 2 && leaves.Select(e => view.ByGuid(e.Guid).OccurrencePath).Distinct().Count() == 2,
                "Asset provenance and instance identity remain separate for repeated nested prefabs");
            Require(leaves[0].Scene.Doc(5).Root["pull"].AsFloat() == 0.8f && leaves[1].Scene.Doc(5).Root["pull"].AsFloat() == 0.4f,
                "One shared composition applies base-before-variant values independently per occurrence");
            var data = leaves[0].Scene.Doc(6).Root["arbitrary"];
            Require(data["amount"].AsInt() == 7 && data["title"].AsString() == "" &&
                data["packed"].AsString() == "01000000ffffffff",
                "Unknown component fields, empty strings and packed arrays use the same property resolver");
            Require(VrmToResonitePackage.Vrchat.VrchatConstants.DecodeIntArray(data["emptyPacked"])
                .SequenceEqual(new[] { -1, 0 }),
                "Growing an initially empty integer array preserves assigned values and default elements");
            var reference = data["references"].Seq[1];
            Require(reference.Guid == outerGuid && reference.FileID == 901 &&
                view.PrefabGraph.Identity(reference.Guid, reference.FileID.Value) == new UnityObjectId(outerGuid, 901),
                "An outer variant's local object reference retains its declaring scene");
            var parser = typeof(VrmToResonitePackage.Vrchat.VrchatAvatarParser);
            var placementType = parser.GetNestedType("FbxPlacement", System.Reflection.BindingFlags.NonPublic)!;
            var placement = Activator.CreateInstance(placementType)!;
            parser.GetMethod("CaptureLocalPlacementParents", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object[] { view, leaves[0].Scene, leaves[0].Guid, 4L, placement, null, null, false, null });
            var parents = (List<VrmToResonitePackage.Vrchat.VrchatPrefabTransform>)placementType.GetProperty("ParentTransforms")!.GetValue(placement)!;
            Require(parents.Select(p => p.Name).SequenceEqual(new[] { "ExternalTarget", "Body" }) &&
                parents[0].Key == outerGuid + ":901",
                "Hierarchy extraction follows an overridden parent reference into the declaring outer scene");
            var renderer = leaves[0].Scene.Doc(7).Root;
            Require(renderer["m_Bones"].Seq[0].FileID == 0 && renderer["m_Materials"].Seq.Count == 0 &&
                renderer["m_BlendShapeWeights"].Seq.Select(v => v.AsInt()).SequenceEqual(new[] { 0, 10 }),
                "Null references, array shrinking and explicit zero overrides survive composition");
            var position = leaves[0].Scene.Doc(4).Root["m_LocalPosition"];
            Require(position["x"].AsFloat() == 1.5f && position["y"].AsFloat() == 3 && position["z"].AsFloat() == 4,
                "Transform property overrides preserve the other axes");
            using var second = UnityPrefabInstances.CreateView(source, outerGuid, null);
            Require(second.PrefabGraph.Scenes.Select(e => e.Guid).SequenceEqual(view.PrefabGraph.Scenes.Select(e => e.Guid)) &&
                cached.Doc(5).Root["pull"].AsFloat() == 0.2f && cached.Doc(7).Root["m_Materials"].Seq.Count == 1 && File.ReadAllText(leaf) == leafText,
                "Repeated resolution is deterministic and leaves cached source scenes and project files intact");
        }

        // Every component type is removed by the graph, including types unknown to the converter.
        foreach (long component in new[] { 5L, 6L, 7L })
        {
            string removed = $"--- !u!1001 &100\nPrefabInstance:\n  m_SourcePrefab: {{guid: {leafGuid}}}\n  m_Modification:\n    m_RemovedComponents:\n    - {{guid: {leafGuid}, fileID: {component}}}\n";
            asset("Assets/GraphVariant.prefab", variantGuid, removed);
            using var source = UnityPackage.Open(outer);
            using var view = UnityPrefabInstances.CreateView(source, outerGuid, null);
            Require(view.PrefabGraph.Scenes.Where(e => view.ByGuid(e.Guid).SourceGuid == leafGuid)
                .All(e => e.Scene.Doc(component) == null && e.Scene.Doc(3) != null && e.Scene.Documents.Values.Count(d => d.ClassId is 114 or 137) == 2),
                $"Removing component {component} cannot be forgotten by an individual converter");
        }
        asset("Assets/GraphVariant.prefab", variantGuid, $"--- !u!1001 &100\nPrefabInstance:\n  m_SourcePrefab: {{guid: {leafGuid}}}\n" +
            $"--- !u!1 &800 stripped\nGameObject:\n  m_CorrespondingSourceObject: {{guid: {leafGuid}, fileID: 3}}\n  m_PrefabInstance: {{fileID: 100}}\n" +
            "--- !u!114 &801\nMonoBehaviour:\n  m_GameObject: {fileID: 800}\n  m_Script: {guid: addedUnknownScript}\n");
        string deletedOuter = outerText.Replace("    m_Modifications:\n", $"    m_RemovedGameObjects:\n    - {{guid: {leafGuid}, fileID: 3}}\n    m_Modifications:\n");
        File.WriteAllText(outer, deletedOuter);
        using (var source = UnityPackage.Open(outer))
        using (var view = UnityPrefabInstances.CreateView(source, outerGuid, null))
        {
            var variants = view.PrefabGraph.Scenes.Where(e => view.ByGuid(e.Guid).SourceGuid == variantGuid).ToArray();
            Require(variants[0].Scene.Doc(801) == null && variants[1].Scene.Doc(801) != null,
                "Deleting a nested object also removes outer-added components without affecting its sibling occurrence");
        }
        asset("Assets/GraphVariant.prefab", variantGuid, variant);
        File.WriteAllText(outer, outerText);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
