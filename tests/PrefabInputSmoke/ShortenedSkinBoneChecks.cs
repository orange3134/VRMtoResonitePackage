using System.Reflection;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class ShortenedSkinBoneChecks
{
    public static void Run(Func<string, string, string, string> asset)
    {
        const string modelGuid = "ad130000000000000000000000000071";
        const string otherGuid = "ad130000000000000000000000000072";
        const string prefabGuid = "ad130000000000000000000000000073";
        string model = asset("Assets/ShortenedSkin.fbx", modelGuid, Model());
        asset("Assets/OtherShortenedSkin.fbx", otherGuid, Model());
        long Bone(string name) => UnityModelFileIdResolver.Compute("Transform", "//RootNode/" + name + "/Transform", 0);
        string Ref(string name, string guid = modelGuid) => $"{{fileID: {Bone(name)}, guid: {guid}}}";
        string hips = Ref("Hips"), tail = Ref("Tail"), unused = Ref("Unused");

        VrchatMeshCopy Read(string[] references, bool stripped = false, bool local = false)
        {
            string text = $$"""
--- !u!1 &1
GameObject:
  m_Name: TailCopy
--- !u!137 &2
SkinnedMeshRenderer:
  m_GameObject: {fileID: 1}
  m_Mesh: {fileID: {{UnityModelFileIdResolver.Compute("Mesh", "//RootNode/TailMesh/Mesh", 0)}}, guid: {{modelGuid}}}
  m_Bones:
""" + "\n";
            for (int i = 0; i < references.Length; i++)
                text += "  - " + (stripped || local ? $"{{fileID: {100 + i}}}" : references[i]) + "\n";
            if (stripped)
            {
                text += $"--- !u!1001 &90\nPrefabInstance:\n  m_SourcePrefab: {{fileID: 100100000, guid: {modelGuid}}}\n";
                for (int i = 0; i < references.Length; i++)
                    text += $"--- !u!4 &{100 + i} stripped\nTransform:\n  m_CorrespondingSourceObject: {references[i]}\n  m_PrefabInstance: {{fileID: 90}}\n";
            }
            if (local)
                for (int i = 0; i < references.Length; i++)
                    text += $"--- !u!1 &{200 + i}\nGameObject:\n  m_Name: {(i == 0 ? "Hips" : "Tail")}\n" +
                            $"--- !u!4 &{100 + i}\nTransform:\n  m_GameObject: {{fileID: {200 + i}}}\n";
            string path = asset("Assets/ShortenedSkin.prefab", prefabGuid, text);
            using var package = UnityPackage.Open(path);
            using var view = UnityPrefabInstances.CreateView(package, prefabGuid, null);
            var scene = view.PrefabGraph.Scene(prefabGuid);
            var avatar = new VrchatAvatar { FbxGuid = scene.Doc(2).Root["m_Mesh"].Guid };
            var resolvers = view.ByExtension(".fbx").ToDictionary(m => m.Guid, m => new UnityModelFileIdResolver(m));
            var resolver = resolvers[avatar.FbxGuid];
            if (!resolver.MeshBoneNames["TailMesh"].SequenceEqual(new[] { "Hips", "Unused", "Tail" }) ||
                !resolver.MeshWeightedBonesByPath["RootNode/TailMesh"].SequenceEqual(new[] { true, false, true }))
                throw new Exception("Fixture must retain an unused bone between two weighted bones");
            try
            {
                typeof(VrchatAvatarParser).GetMethod("CollectAuthoredMeshCopy", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, new object[] { view, prefabGuid, scene, scene.Doc(2), avatar, resolvers,
                        new Dictionary<string, UnityScene>(), false });
            }
            catch (TargetInvocationException error) when (error.InnerException is InvalidDataException)
            { throw error.InnerException; }
            if (File.ReadAllText(path) != text || File.ReadAllText(model) != Model())
                throw new Exception("Bone table recovery must not change input assets");
            return avatar.MeshCopies.Single();
        }

        foreach (bool stripped in new[] { false, true })
        foreach (var references in new[] { new[] { hips, tail }, new[] { tail, hips } })
        {
            var copy = Read(references, stripped);
            if (copy.SourceBoneNames.Count != 3 || copy.BoneTargets.Count != 2 ||
                copy.BoneTargets[0].Name != "Hips" || copy.BoneTargets[2].Name != "Tail" ||
                copy.BoneTargets.ContainsKey(1)) throw new Exception("Shortened skin must map exact FBX identities to original indices");
        }
        void Reject(string[] references, bool local = false)
        {
            try { Read(references, local: local); }
            catch (InvalidDataException) { return; }
            throw new Exception("Unproven shortened bone arrays must not be accepted");
        }
        Reject(new[] { hips, unused }); // Tail has vertex weights.
        Reject(new[] { hips, "{fileID: 0}" });
        Reject(new[] { hips, Ref("Tail", otherGuid) });
        Reject(new[] { hips, hips });
        Reject(new[] { hips, tail }, local: true);
        Reject(new[] { hips, unused, tail, tail });
        var overridden = Read(new[] { tail, "{fileID: 0}", hips });
        if (overridden.BoneTargets[0].Name != "Tail" || overridden.BoneTargets[1].Name != null ||
            overridden.BoneTargets[2].Name != "Hips") throw new Exception("Full arrays must retain authored indexed overrides");
        Console.WriteLine("PASS: Shortened skins recover unused FBX bones through direct/stripped identities and reject unsafe mismatches");
    }

    private static string Model()
    {
        string text = """
; FBX 7.4.0 project file
FBXHeaderExtension: {
    FBXHeaderVersion: 1003
    FBXVersion: 7400
}
Objects: {
    Model: 5, "Model::TailMesh", "Mesh" { }
    Geometry: 7, "Geometry::Triangle", "Mesh" {
        Vertices: *9 { a: 0,0,0,1,0,0,0,1,0 }
        PolygonVertexIndex: *3 { a: 0,1,-3 }
    }
    Deformer: 10, "Deformer::Skin", "Skin" { }
""" + "\n";
        string[] names = { "Hips", "Unused", "Tail" };
        for (int i = 0; i < names.Length; i++)
            text += $$"""
    Model: {{i + 1}}, "Model::{{names[i]}}", "LimbNode" { }
    Deformer: {{11 + i}}, "SubDeformer::{{names[i]}}", "Cluster" {
        Indexes: *1 { a: {{i}} }
        Weights: *1 { a: {{(i == 1 ? 0 : 1)}} }
        Transform: *16 { a: 1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1 }
        TransformLink: *16 { a: 1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1 }
    }
""" + "\n";
        text += "}\nConnections: {\n    C: \"OO\",5,0\n    C: \"OO\",7,5\n    C: \"OO\",10,7\n";
        for (int i = 0; i < names.Length; i++)
            text += $"    C: \"OO\",{i + 1},0\n    C: \"OO\",{11 + i},10\n    C: \"OO\",{i + 1},{11 + i}\n";
        return text + "}\n";
    }
}
