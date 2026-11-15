using System.Reflection;
using Elements.Core;
using FrooxEngine;
using VrmToResonitePackage.Vrchat;
using Vec3 = System.Numerics.Vector3;
using Quat = System.Numerics.Quaternion;

internal static class AuthoredBoneTransformChecks
{
    public static void Run(Slot parent)
    {
        foreach (float units in new[] { .01f, 1f })
        foreach (bool retainedRoot in new[] { false, true })
        {
            var root = parent.AddSlot("Authored skeleton pose regression");
            var wrapper = root.AddSlot("Model");
            var model = retainedRoot ? wrapper.AddSlot("RootNode") : wrapper;
            var helper = model.AddSlot("Armature_$AssimpFbx$_PreRotation");
            helper.LocalRotation = floatQ.Euler(90, 0, 0);
            var armature = helper.AddSlot("Armature");
            var bone = armature.AddSlot("Shared");
            bone.LocalPosition = new float3(0, 1 / units, 0);
            var untouched = model.AddSlot("Other").AddSlot("Shared");
            untouched.LocalPosition = new float3(3, 4, 5);
            var skin = model.AddSlot("Skin").AttachComponent<SkinnedMeshRenderer>();
            skin.Bones.Add(bone);
            var roots = new Dictionary<string, Slot> { ["model"] = wrapper };
            var sources = VrchatSceneSetup.CaptureImportedObjects(roots);
            var paths = VrchatSceneSetup.CaptureImportedPaths(roots);
            var avatar = new VrchatAvatar { FbxGuid = "model", FbxImportScale = units,
                FbxTransformNodeName = retainedRoot ? "RootNode" : null };
            avatar.AuthoredBoneTransforms[new("model", "Armature", "Armature")] =
                new(new Vec3(.1f, .2f, .3f), Quat.CreateFromAxisAngle(Vec3.UnitZ, .3f), new Vec3(1.2f, .8f, 1.1f));
            avatar.AuthoredBoneTransforms[new("model", "Shared", "Armature/Shared")] =
                new(new Vec3(-.2f, .7f, 0), Quat.CreateFromAxisAngle(Vec3.UnitX, -.2f), new Vec3(1.15f));
            var converter = typeof(VrchatAvatar).Assembly.GetType("VrmToResonitePackage.Converter")!;
            object[] args = { root, avatar, roots, sources, paths, null, null, null };
            converter.GetMethod("ApplyVrchatPrefabHierarchy", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);

            var oracleRoot = root.AddSlot("Unity pose oracle");
            var oracleArmature = oracleRoot.AddSlot("Armature");
            var oracleBone = oracleArmature.AddSlot("Bone");
            oracleArmature.LocalPosition = new float3(.1f, .2f, .3f);
            oracleArmature.LocalRotation = new floatQ(0, 0, MathF.Sin(.15f), MathF.Cos(.15f));
            oracleArmature.LocalScale = new float3(1.2f, .8f, 1.1f);
            oracleBone.LocalPosition = new float3(-.2f, .7f, 0);
            oracleBone.LocalRotation = new floatQ(MathF.Sin(-.1f), 0, 0, MathF.Cos(-.1f));
            oracleBone.LocalScale = float3.One * 1.15f;
            foreach (var point in new[] { float3.Zero, float3.Right, float3.Up, float3.Forward })
                if (MathX.Distance(bone.LocalPointToGlobal(point / units), oracleBone.LocalPointToGlobal(point)) > .00001f)
                    throw new Exception($"Authored local pose lost hierarchy, FBX units or helper collapse: units={units}, root={retainedRoot}, point={point}, actual={bone.LocalPointToGlobal(point / units)}, expected={oracleBone.LocalPointToGlobal(point)}");
            var before = bone.LocalToGlobal;
            VrchatSceneSetup.ApplyAuthoredBoneTransforms(avatar, sources, paths);
            if (before != bone.LocalToGlobal || skin.Bones[0] != bone || untouched.LocalPosition != new float3(3, 4, 5))
                throw new Exception("Authored pose must preserve skin references, namesakes and repeated application.");
            root.Destroy();
        }
        Console.WriteLine("PASS: authored bone position/rotation/scale preserve units, ancestors, helpers and skin identity");
    }
}
