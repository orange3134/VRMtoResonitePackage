using System.Reflection;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class ReviewRegressionChecks
{
    public static void Run(Func<string, string, string, string> asset, string regularCopy, string branchesGuid)
    {
        var failures = new List<string>();
        void Case(string name, Action check)
        {
            try { check(); Console.WriteLine("PASS: Review regression: " + name); }
            catch (Exception error) { failures.Add(name); Console.WriteLine("FAIL: Review regression: " + name + " / " + error.GetBaseException().Message); }
        }
        void Require(bool value) { if (!value) throw new Exception("Assertion failed"); }
        const string baseGuid = "ab120000000000000000000000000001";
        const string variantGuid = "ab120000000000000000000000000002";
        const string outerGuid = "ab120000000000000000000000000003";
        string baseText = File.ReadAllText(regularCopy);
        string baseFile = asset("Assets/ReviewBase.prefab", baseGuid, baseText);
        string variantText = $"--- !u!1001 &99\nPrefabInstance:\n  m_SourcePrefab: {{guid: {baseGuid}}}\n";
        string variantFile = asset("Assets/ReviewVariant.prefab", variantGuid, variantText);
        Case("selected subtree rebases local skeleton without mutating source", () =>
        {
            const string nestedGuid = "ab120000000000000000000000000007";
            string nested = asset("Assets/ReviewNested.prefab", nestedGuid,
                baseText.Replace("m_Father: {fileID: 0}", "m_Father: {fileID: 802}") +
                "\n--- !u!1 &801\nGameObject:\n  m_Name: Container\n--- !u!4 &802\nTransform:\n  m_GameObject: {fileID: 801}\n  m_Father: {fileID: 0}\n");
            using var source = UnityPackage.Open(nested);
            var scene = source.ReadScene(source.InputPrefab);
            using var view = (UnityPackage)typeof(UnityPackage).Assembly.GetType("VrmToResonitePackage.Unity.UnityPrefabInstances")!
                .GetMethod("CreateView")!.Invoke(null, new object[] { source, nestedGuid,
                    scene.GameObjects.Where(go => go.FileId != 801).Select(go => go.FileId).ToHashSet() })!;
            var bone = (VrchatBoneTarget)Call("ResolveCopiedBoneTarget", view, nestedGuid, 31L,
                branchesGuid, new Dictionary<string, UnityModelFileIdResolver>(), new HashSet<(string, long)>());
            Require(bone?.Path == "Body_Base" && scene.Doc(33).Root["m_Father"].FileID == 802);
        });
        foreach (string modelGuid in new[] { branchesGuid, null })
        Case("same-path authored physics identities " + modelGuid, () =>
        {
            var avatar = new VrchatAvatar();
            foreach (long id in new[] { 31L, 41L })
                avatar.PhysBones.Add(new VrchatPhysBone { RootBoneName = "Shared",
                    RootBoneTarget = new VrchatBoneTarget(modelGuid, "Shared", "Shared", baseGuid, id) });
            var model = VrchatModelAdapter.ToVrmModel(avatar);
            Require(model.SpringChains.SelectMany(c => c.RootNodes).Distinct().Count() == 2 &&
                model.NodeTargets.Values.Select(t => t.TransformFileId).ToHashSet().SetEquals(new[] { 31L, 41L }));
        });
        VrchatAvatar Copies(string file)
        {
            using var package = UnityPackage.Open(file);
            var avatar = new VrchatAvatar();
            Call("ParseVariantRendererOverrides", package, package.InputPrefab.Guid, avatar);
            return avatar;
        }
        Case("unpacked variant replaces same-named template", () =>
            Require(Copies(variantFile).MeshCopies.Single().ReplaceSourceRenderer));

        Case("authored copy retains a separately instantiated FBX renderer", () =>
        {
            var scene = UnityScene.Parse(baseText);
            string modelGuid = scene.RendererMesh(scene.MeshRenderers.Single()).Guid;
            string composed = asset("Assets/ReviewOuter.prefab", outerGuid, variantText +
                $"\n--- !u!1001 &101\nPrefabInstance:\n  m_SourcePrefab: {{guid: {modelGuid}}}\n");
            Require(!Copies(composed).MeshCopies.Single().ReplaceSourceRenderer);
        });

        Case("scene-local copied bone reference", () =>
        {
            const string sceneGuid = "ab120000000000000000000000000004";
            asset("Assets/ReviewScene.unity", sceneGuid, baseText);
            using var package = UnityPackage.Open(baseFile);
            var bone = (VrchatBoneTarget)Call("ResolveCopiedBoneTarget", package, sceneGuid, 31L,
                branchesGuid, new Dictionary<string, UnityModelFileIdResolver>(), new HashSet<(string, long)>());
            Require(bone?.Name == "Body_Base" && bone.Path == "Body_Base");
        });

        Case("same-named copies retain separate material records", () =>
        {
            var scene = UnityScene.Parse(baseText);
            var mesh = scene.RendererMesh(scene.MeshRenderers.Single());
            string Renderer(long id, string material) => $$"""

--- !u!1 &{{id}}
GameObject:
  m_Name: Shared
  m_IsActive: {{(id == 501 ? 0 : 1)}}
--- !u!4 &{{id + 1}}
Transform:
  m_GameObject: {fileID: {{id}}}
  m_Father: {fileID: 0}
--- !u!137 &{{id + 2}}
SkinnedMeshRenderer:
  m_GameObject: {fileID: {{id}}}
  m_Mesh: {guid: {{mesh.Guid}}, fileID: {{mesh.FileID}}}
  m_Materials:
  - {guid: {{material}}}
""";
            string file = asset("Assets/ReviewMaterials.prefab", "ab120000000000000000000000000005",
                Renderer(501, "materialA") + Renderer(601, "materialB"));
            var parsed = Copies(file);
            Require(parsed.RendererMaterials.Count == 2 &&
                parsed.RendererMaterials.Select(r => r.PrefabObjectKey).Distinct().Count() == 2 &&
                parsed.RendererMaterials.SelectMany(r => r.MaterialGuids).ToHashSet().SetEquals(new[] { "materialA", "materialB" }));
            using var package = UnityPackage.Open(file);
            var authoredScene = package.ReadScene(package.InputPrefab);
            parsed.FbxGuid = mesh.Guid;
            Call("ParseInactiveGameObjects", authoredScene, authoredScene.GameObjects.Select(go => go.FileId).ToHashSet(),
                parsed, package.InputPrefab.Guid);
            Require(parsed.MeshCopies.Count(c => c.Active) == 1 && parsed.InactiveGameObjects.Count == 0);
            foreach (bool bothExcluded in new[] { false, true })
            {
                File.WriteAllText(file, Renderer(501, "materialA").Replace("m_Name: Shared", "m_Name: Shared\n  m_TagString: EditorOnly") +
                    Renderer(601, "materialB").Replace("m_Name: Shared", "m_Name: Shared\n  m_TagString: " + (bothExcluded ? "EditorOnly" : "Untagged")));
                string taggedWrapper = asset("Assets/ReviewTaggedWrapper.prefab", "ab120000000000000000000000000006", """
--- !u!1001 &100
PrefabInstance:
  m_SourcePrefab: {guid: ab120000000000000000000000000005}
  m_Modification:
    m_Modifications:
    - target: {guid: ab120000000000000000000000000005, fileID: 503}
      propertyPath: m_Materials.Array.data[0]
      objectReference: {guid: excludedOverride}
""");
                using var taggedPackage = UnityPackage.Open(taggedWrapper);
                var tagged = new VrchatAvatar();
                typeof(VrchatAvatarParser).GetMethod("CollectVariantPrefabGameObjectNames", BindingFlags.NonPublic | BindingFlags.Static,
                    new[] { typeof(UnityPackage), typeof(string), typeof(VrchatAvatar) })!
                    .Invoke(null, new object[] { taggedPackage, taggedPackage.InputPrefab.Guid, tagged });
                Call("ParseVariantRendererOverrides", taggedPackage, taggedPackage.InputPrefab.Guid, tagged);
                Require(tagged.MeshCopies.Count == (bothExcluded ? 0 : 1) &&
                    tagged.ShouldKeepRenderer(mesh.Guid, "Shared") == !bothExcluded &&
                    tagged.RendererMaterials.Count == (bothExcluded ? 0 : 1) &&
                    tagged.RendererMaterials.All(r => r.MaterialGuids.Single() == "materialB"));
            }
        });

        Case("copy retains enclosing translated and rotated attachment", () =>
        {
            string composed = asset("Assets/ReviewOuter.prefab", outerGuid, variantText.Replace(
                $"  m_SourcePrefab: {{guid: {baseGuid}}}", $"  m_SourcePrefab: {{guid: {baseGuid}}}\n  m_Modification:\n    m_TransformParent: {{fileID: 502}}") + """

--- !u!1 &501
GameObject:
  m_Name: Attachment
--- !u!4 &502
Transform:
  m_GameObject: {fileID: 501}
  m_Father: {fileID: 0}
  m_LocalPosition: {x: 7, y: 8, z: 9}
  m_LocalRotation: {x: 0, y: 0, z: 1, w: 0}
  m_LocalScale: {x: 2, y: 2, z: 2}
""");
            var parents = Copies(composed).MeshCopies.Single().ParentTransforms;
            Require(parents.Count == 2 && parents[0].Name == "Attachment" &&
                parents[0].LocalPosition.X == 7 && parents[0].LocalRotation.Z == 1 && parents[1].Name == "CopyParent");
        });

        foreach (bool explicitAlias in new[] { false, true })
        Case("outer renderer, GameObject and transform aliases " + explicitAlias, () =>
        {
            long rendererId = explicitAlias ? 900 : 2 ^ 99;
            long transformId = explicitAlias ? 901 : 31 ^ 99;
            long parentId = explicitAlias ? 902 : 32 ^ 99;
            string aliases = explicitAlias ?
                $"\n--- !u!23 &900 stripped\nMeshRenderer:\n  m_CorrespondingSourceObject: {{guid: {baseGuid}, fileID: 2}}\n  m_PrefabInstance: {{fileID: 99}}\n" +
                $"--- !u!4 &901 stripped\nTransform:\n  m_CorrespondingSourceObject: {{guid: {baseGuid}, fileID: 31}}\n  m_PrefabInstance: {{fileID: 99}}\n" +
                $"--- !u!1 &902 stripped\nGameObject:\n  m_CorrespondingSourceObject: {{guid: {baseGuid}, fileID: 32}}\n  m_PrefabInstance: {{fileID: 99}}\n" : "";
            File.WriteAllText(variantFile, variantText + aliases);
            string outer = asset("Assets/ReviewOuter.prefab", outerGuid, $$"""
--- !u!1001 &199
PrefabInstance:
  m_SourcePrefab: {guid: {{variantGuid}}}
  m_Modification:
    m_Modifications:
    - target: {guid: {{variantGuid}}, fileID: {{rendererId}}}
      propertyPath: m_Enabled
      value: 0
    - target: {guid: {{variantGuid}}, fileID: {{transformId}}}
      propertyPath: m_LocalPosition.x
      value: 42
    - target: {guid: {{variantGuid}}, fileID: {{parentId}}}
      propertyPath: m_IsActive
      value: 1
""");
            var copy = Copies(outer).MeshCopies.Single();
            Require(!copy.Enabled && copy.Transform.LocalPosition.X == 42 && copy.ParentTransforms.Single().Active);
        });

        Case("same-named model tags retain independent identities", () =>
        {
            long Id(string path) => (long)typeof(UnityModelFileIdResolver).GetMethod("Compute", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { "GameObject", path, 0 })!;
            string tags = asset("Assets/ReviewOuter.prefab", outerGuid, $$"""
--- !u!1001 &100
PrefabInstance:
  m_SourcePrefab: {guid: {{branchesGuid}}}
  m_Modification:
    m_Modifications:
    - target: {guid: {{branchesGuid}}, fileID: {{Id("//RootNode/Left/Shared")}}}
      propertyPath: m_TagString
      value: EditorOnly
    - target: {guid: {{branchesGuid}}, fileID: {{Id("//RootNode/Right/Shared")}}}
      propertyPath: m_TagString
      value: Untagged
""");
            using var package = UnityPackage.Open(tags);
            var avatar = new VrchatAvatar();
            typeof(VrchatAvatarParser).GetMethod("CollectVariantPrefabGameObjectNames", BindingFlags.NonPublic | BindingFlags.Static,
                new[] { typeof(UnityPackage), typeof(string), typeof(VrchatAvatar) })!.Invoke(null, new object[] { package, outerGuid, avatar });
            Require(avatar.EditorOnlyModelPaths.TryGetValue(branchesGuid, out var paths) &&
                paths.Any(p => p.EndsWith("Left/Shared")) && paths.All(p => !p.Contains("Right/Shared")));
        });

        Case("repeated prefab physics retains model identity through adaptation", () =>
        {
            File.WriteAllText(baseFile, baseText + $$"""

--- !u!114 &777
MonoBehaviour:
  m_GameObject: {fileID: 1}
  m_Script: {fileID: 1661641543, guid: 2a2c05204084d904aa4945ccff20d8e5}
  rootTransform: {fileID: 31}
""");
            string repeated = asset("Assets/ReviewOuter.prefab", outerGuid, variantText +
                $"\n--- !u!1001 &100\nPrefabInstance:\n  m_SourcePrefab: {{guid: {baseGuid}}}\n");
            using var source = UnityPackage.Open(repeated);
            using var view = (UnityPackage)typeof(UnityPackage).Assembly.GetType("VrmToResonitePackage.Unity.UnityPrefabInstances")!
                .GetMethod("CreateView")!.Invoke(null, new object[] { source, outerGuid, null })!;
            var avatar = new VrchatAvatar();
            Call("ParseVariantPhysBones", view, outerGuid, avatar, null!);
            var model = VrchatModelAdapter.ToVrmModel(avatar);
            Require(model.SpringChains.Count == 2 && model.SpringChains.SelectMany(c => c.RootNodes).Distinct().Count() == 2);
        });
        if (failures.Count > 0) throw new Exception("Review regressions failed: " + string.Join(", ", failures));
    }

    private static object Call(string method, params object[] args) => typeof(VrchatAvatarParser)
        .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
}
