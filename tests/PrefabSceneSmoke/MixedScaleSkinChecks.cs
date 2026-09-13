using System.Reflection;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using VrmToResonitePackage.Vrchat;

internal static class MixedScaleSkinChecks
{
    public static async Task Run(Slot parent)
    {
        CheckModelPlacement(parent);
        foreach (var (sourceScale, targetScale) in new[]
        {
            (float3.One, float3.One * 0.01f),
            (float3.One * 0.01f, float3.One),
            (float3.One, float3.One),
            (new float3(0.7f, 1.4f, 0.9f), new float3(0.01f, 0.02f, 0.03f)),
        })
        {
            var root = parent.AddSlot("Mixed scale regression");
            root.LocalPosition = new float3(2, 3, 4);
            var source = root.AddSlot("Source");
            source.LocalScale = sourceScale;
            source.LocalPosition = new float3(0.2f, 0.3f, -0.1f);
            source.LocalRotation = floatQ.Euler(0, 25, 0);
            var sourceHip = source.AddSlot("Hips");
            sourceHip.LocalPosition = new float3(0, 0.5f, 0);
            var sourceChest = sourceHip.AddSlot("Chest");
            sourceChest.LocalPosition = new float3(0, 0.2f, 0);
            var helper = sourceChest.AddSlot("Unmatched helper");
            helper.LocalPosition = new float3(0.12f, 0.1f, 0.05f);
            var target = root.AddSlot("Target");
            target.LocalScale = targetScale;
            target.LocalRotation = floatQ.Euler(0, -15, 0);
            var targetHip = target.AddSlot("Hips");
            targetHip.LocalPosition = new float3(0, 20, 0);
            var targetChest = targetHip.AddSlot("Chest");
            targetChest.LocalPosition = new float3(0, 10, 0);
            var final = root.AddSlot("Final");
            final.LocalScale = float3.One * 0.5f;
            var finalHip = final.AddSlot("Hips");
            var finalChest = finalHip.AddSlot("Chest");
            finalChest.LocalPosition = new float3(0, 2, 0);

            var mesh = new MeshX { HasBoneBindings = true };
            var bones = new[] { sourceHip, sourceChest, helper };
            for (int i = 0; i < bones.Length; i++)
            {
                mesh.AddBone(bones[i].Name).BindPose = bones[i].GlobalToLocal * root.LocalToGlobal;
                var vertex = mesh.AddVertex(new float3(i * 0.1f, 0.4f + i * 0.2f, 0.15f));
                vertex.BoneBinding = new BoneBinding { boneIndex0 = i, weight0 = 1 };
            }
            mesh.AddSubmesh<TriangleSubmesh>().AddTriangle(0, 1, 2);
            var frame = mesh.AddBlendShape("Fit").AddFrame(1);
            frame.SetPositionDelta(0, new float3(0.03f, 0.02f, 0));
            var provider = root.AttachComponent<StaticMesh>();
            provider.URL.Value = await root.Engine.LocalDB.SaveAssetAsync(mesh);
            await default(ToWorld);
            var renderer = root.AddSlot("Clothing").AttachComponent<SkinnedMeshRenderer>();
            renderer.Mesh.Target = provider;
            foreach (var bone in bones) renderer.Bones.Add(bone);
            var untouched = root.AddSlot("Shared mesh occurrence").AttachComponent<SkinnedMeshRenderer>();
            untouched.Mesh.Target = provider;
            foreach (var bone in bones)
            {
                var copy = root.AddSlot("Untouched " + bone.Name);
                copy.LocalToGlobal = bone.LocalToGlobal;
                untouched.Bones.Add(copy);
            }
            await Wait(provider);
            await default(NextUpdate);
            renderer.BlendShapeWeights[0] = 0.35f;
            float3[] before = Vertices(renderer);
            float3[] otherBefore = Vertices(untouched);
            float3 helperPosition = helper.GlobalPosition;
            float3 helperScale = helper.GlobalScale;
            var avatar = new VrchatAvatar();
            avatar.ModularMergeArmatures.Add(new() { SourceName = "Source", TargetName = "Target" });
            avatar.ModularMergeArmatures.Add(new() { SourceName = "Target", TargetName = "Final" });
            var method = typeof(VrchatAvatar).Assembly.GetType("VrmToResonitePackage.Vrchat.VrchatSceneSetup")!
                .GetMethod("ApplyModularAvatar", BindingFlags.Public | BindingFlags.Static)!;
            await (Task)method.Invoke(null, new object[] { root, avatar, null, null, null })!;
            Check(source.IsDestroyed && target.IsDestroyed && renderer.Bones[0] == finalHip &&
                  renderer.Bones[1] == finalChest && renderer.Bones[2] == helper,
                "Successive merges retain the surviving skin and helper identities");
            Check(MathX.Distance(helper.GlobalPosition, helperPosition) < 0.0001f &&
                  MathX.Distance(helper.GlobalScale, helperScale) < 0.0001f,
                $"Unmatched bones retain world pose: position {helper.GlobalPosition} vs {helperPosition}, scale {helper.GlobalScale} vs {helperScale}");
            Check(before.Zip(Vertices(renderer)).All(p => MathX.Distance(p.First, p.Second) < 0.0001f),
                $"Skinned vertices and blendshapes survive {sourceScale} -> {targetScale} -> 0.5");
            Check(untouched.Mesh.Target == provider && renderer.Mesh.Target != provider &&
                  otherBefore.Zip(Vertices(untouched)).All(p => MathX.Distance(p.First, p.Second) < 0.0001f) &&
                  MathF.Abs(renderer.BlendShapeWeights[0] - 0.35f) < 0.0001f,
                "Retargeting preserves shared source assets and initial expression weights");
            root.Destroy();
        }
    }

    private static void CheckModelPlacement(Slot parent)
    {
        foreach (float bodyUnits in new[] { 0.01f, 1f, 0.5f })
        foreach (float clothingUnits in new[] { 0.01f, 1f })
        {
            var root = parent.AddSlot("Model unit boundary");
            var body = root.AddSlot("Body");
            var bodyNode = body.AddSlot("RootNode");
            bodyNode.LocalPosition = new float3(10, 20, 30);
            var clothing = root.AddSlot("Clothing");
            clothing.AddSlot("Payload");
            var avatar = new VrchatAvatar { FbxGuid = "body", FbxImportScale = bodyUnits };
            var additional = new VrchatFbxAsset
            {
                Guid = "clothing", ImportScale = clothingUnits,
                ParentFbxGuid = "body", ParentNodeName = "RootNode",
                LocalPosition = new System.Numerics.Vector3(0.1f, 0.2f, 0.3f),
                LocalScale = new System.Numerics.Vector3(0.75f),
            };
            additional.ParentTransforms.Add(new() { Key = "holder", Name = "Authored holder",
                LocalScale = new System.Numerics.Vector3(2f) });
            avatar.AdditionalFbxs.Add(additional);
            var roots = new Dictionary<string, Slot> { ["body"] = body, ["clothing"] = clothing };
            var converter = typeof(VrchatAvatar).Assembly.GetType("VrmToResonitePackage.Converter")!;
            converter.GetMethod("ApplyVrchatPrefabHierarchy", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { root, avatar, roots, new Dictionary<Slot, string>(),
                    new Dictionary<Slot, string>(), null, null, null });
            Check(MathX.Distance(clothing.GlobalScale, float3.One * (clothingUnits * 0.75f * 2f)) < 0.00001f &&
                  MathX.Distance(clothing.GlobalPosition - bodyNode.GlobalPosition, new float3(0.2f, 0.4f, 0.6f)) < 0.00001f,
                $"Model placement {clothingUnits} / {bodyUnits}: scale={clothing.GlobalScale}, offset={clothing.GlobalPosition - bodyNode.GlobalPosition}, parentScale={bodyNode.GlobalScale}");
            root.Destroy();
        }
    }

    private static float3[] Vertices(SkinnedMeshRenderer renderer)
    {
        var mesh = renderer.Mesh.Asset.Data;
        return Enumerable.Range(0, mesh.VertexCount).Select(index =>
        {
            float3 position = mesh.GetPosition(index) +
                mesh.BlendShapes.First().Frames.First().GetPositionDelta(index) * renderer.BlendShapeWeights[0];
            var binding = mesh.RawBoneBindings[index];
            float3 result = float3.Zero;
            for (int j = 0; j < 4; j++)
            {
                float weight = binding.GetWeight(j);
                if (weight == 0) continue;
                int bone = binding.GetBoneIndex(j);
                result += renderer.Bones[bone].LocalPointToGlobal(
                    mesh.GetBone(bone).BindPose.TransformPoint3x4(position)) * weight;
            }
            return result;
        }).ToArray();
    }

    private static async Task Wait(StaticMesh provider)
    {
        for (int i = 0; i < 7200; i++)
        {
            if (provider.IsAssetAvailable && provider.Asset?.Data != null) return;
            await default(NextUpdate);
        }
        throw new TimeoutException("Synthetic skin mesh did not load");
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
