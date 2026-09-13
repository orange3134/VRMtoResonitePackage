using System.Reflection;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using VrmToResonitePackage.Vrchat;

internal static class FbxTransformHelperChecks
{
    public static async Task Run(Slot parent)
    {
        var root = parent.AddSlot("FBX transform helper regression");
        var model = root.AddSlot("Model");
        model.LocalScale = float3.One * 0.01f;
        var preRotation = model.AddSlot("Armature_$AssimpFbx$_PreRotation");
        preRotation.LocalRotation = floatQ.Euler(90, 0, 0);
        var rotation = preRotation.AddSlot("Armature_$AssimpFbx$_Rotation");
        rotation.LocalRotation = floatQ.Euler(0, 30, 0);
        var armature = rotation.AddSlot("Armature");
        var hips = armature.AddSlot("Hips");
        hips.LocalPosition = new float3(0, 80, 0);
        var unrelated = model.AddSlot("Other").AddSlot("Hips");
        var meshRotation = model.AddSlot("Body_$AssimpFbx$_PreRotation");
        meshRotation.LocalRotation = floatQ.Euler(90, 0, 0);
        var renderer = meshRotation.AddSlot("Body").AttachComponent<SkinnedMeshRenderer>();
        var mesh = new MeshX { HasBoneBindings = true };
        mesh.AddBone("Hips").BindPose = hips.GlobalToLocal * renderer.Slot.LocalToGlobal;
        foreach (var position in new[] { new float3(0, 0, 0), new float3(20, 0, 0), new float3(0, 140, 0) })
        {
            var vertex = mesh.AddVertex(position);
            vertex.BoneBinding = new BoneBinding { boneIndex0 = 0, weight0 = 1 };
        }
        mesh.AddSubmesh<TriangleSubmesh>().AddTriangle(0, 1, 2);
        var provider = root.AttachComponent<StaticMesh>();
        provider.URL.Value = await root.Engine.LocalDB.SaveAssetAsync(mesh);
        await default(ToWorld);
        renderer.Mesh.Target = provider;
        renderer.Bones.Add(hips);
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (provider.Asset?.Data == null || !provider.IsAssetAvailable)
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Helper fixture mesh did not load");
            await default(NextUpdate);
        }
        float3[] Positions(SkinnedMeshRenderer skin) => Enumerable.Range(0, 3).Select(i =>
            skin.Bones[0].LocalPointToGlobal(mesh.GetBone(0).BindPose.TransformPoint3x4(mesh.GetPosition(i)))).ToArray();
        var before = Positions(renderer);
        var roots = new Dictionary<string, Slot> { ["model"] = model };
        var sources = VrchatSceneSetup.CaptureImportedObjects(roots);
        var paths = VrchatSceneSetup.CaptureImportedPaths(roots);
        if (paths[hips] != "Armature/Hips" || paths[renderer.Slot] != "Body" || paths[preRotation] == paths[armature])
            throw new Exception("Captured paths must match Unity metadata without aliasing helper slots");
        var target = new VrchatBoneTarget("model", "Hips", "Armature/Hips", "prefab", 2);
        var avatar = new VrchatAvatar { FbxGuid = "model", FbxImportScale = 0.01f };
        var copy = new VrchatMeshCopy("model", "Body", "AuthoredBody", true, true)
        {
            SourcePath = "RootNode/Body",
            Transform = new VrchatPrefabTransform { Key = "prefab:body" },
        };
        copy.SourceBoneNames.Add("Hips");
        copy.BoneTargets[0] = target;
        avatar.MeshCopies.Add(copy);
        var placement = new VrchatPhysicsPlacement();
        placement.Transforms.Add(new VrchatPrefabTransform { Key = "prefab:2", Name = "Hips", ImportedBone = target });
        avatar.PhysicsPlacements.Add(placement);
        object[] args = { root, avatar, roots, sources, paths, null, null, null };
        typeof(VrchatAvatar).Assembly.GetType("VrmToResonitePackage.Converter")!
            .GetMethod("ApplyVrchatPrefabHierarchy", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
        var slots = (Dictionary<string, Slot>)args[6];
        var result = slots["prefab:body"].GetComponent<SkinnedMeshRenderer>();
        if (result.Bones[0] != hips || slots["prefab:2"] != hips ||
            before.Zip(Positions(result)).Any(pair => MathX.Distance(pair.First, pair.Second) > 0.00001f))
            throw new Exception("FBX helper paths must preserve the shared skeleton and centimeter skin deformation");
        if (VrchatSceneSetup.ResolveImportedTarget(new("model", "Armature", "Armature"), sources, paths) != armature ||
            VrchatSceneSetup.ResolveImportedTarget(new("model", "Hips", "Other/Hips"), sources, paths) != unrelated ||
            VrchatSceneSetup.ResolveImportedTarget(new("model", "Hips"), sources, paths) != null)
            throw new Exception("Helper normalization must retain exact branches and ambiguous-name protection");
        Console.WriteLine("PASS: Generated FBX helper paths preserve model identity, physics bindings and skin dimensions");
        root.Destroy();
    }
}
