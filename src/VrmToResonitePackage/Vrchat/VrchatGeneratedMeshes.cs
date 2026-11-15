using Elements.Core;
using FrooxEngine;
using FrooxEngine.Store;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

internal static class VrchatGeneratedMeshes
{
    public static async Task AddTemplates(Slot root, Slot assets, VrchatAvatar avatar,
        Dictionary<Slot, string> sources, Dictionary<Slot, string> paths)
    {
        var providers = new List<StaticMesh>();
        foreach (var copy in avatar.MeshCopies.Where(c => c.GeneratedMesh != null)
                     .GroupBy(c => (c.FbxGuid, c.SourcePath)).Select(g => g.OrderByDescending(c => c.IsSkinned).First()))
        {
            var mesh = UnityNativeMesh.Decode(copy.GeneratedMesh);
            var provider = assets.AddSlot("Generated Mesh: " + copy.SourceName).AttachComponent<StaticMesh>();
            provider.URL.Value = await root.Engine.LocalDB.SaveAssetAsync(mesh);
            await default(ToWorld);
            providers.Add(provider);
            var slot = root.AddSlot(copy.SourceName);
            MeshRenderer renderer = copy.IsSkinned ? slot.AttachComponent<SkinnedMeshRenderer>() : slot.AttachComponent<MeshRenderer>();
            renderer.Mesh.Target = provider;
            if (renderer is SkinnedMeshRenderer skin)
            {
                for (int i = 0; i < mesh.BoneCount; i++) skin.Bones.Add();
                foreach (var shape in mesh.BlendShapes) skin.BlendShapeWeights.Add(0f);
            }
            sources[slot] = copy.FbxGuid;
            paths[slot] = copy.SourcePath;
            UniLog.Log($"Generated Unity mesh: {copy.SourceName}, vertices={mesh.VertexCount}, submeshes={mesh.SubmeshCount}, bones={mesh.BoneCount}, shapes={mesh.BlendShapes.Count()}");
        }
        for (int frame = 0; frame < 7200 && providers.Any(p => !p.IsAssetAvailable); frame++) await default(NextUpdate);
        if (providers.Any(p => !p.IsAssetAvailable)) throw new InvalidOperationException("Generated Unity meshes did not finish loading.");
    }
}
