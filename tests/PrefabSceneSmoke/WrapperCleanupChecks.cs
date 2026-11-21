using Elements.Core;
using FrooxEngine;
using VrmToResonitePackage.Vrchat;

internal static class WrapperCleanupChecks
{
    public static void Run(Slot parent)
    {
        var root = parent.AddSlot("Wrapper cleanup");
        root.LocalScale = new float3(2, 3, 4);
        var centered = root.AddSlot("CenteredRoot");
        centered.AttachComponent<CapsuleCollider>();
        var descriptor = centered.AddSlot("Avatar");
        descriptor.LocalPosition = new float3(.1f, .2f, -.3f);
        descriptor.LocalRotation = floatQ.Euler(3, 180, 7);
        descriptor.LocalScale = new float3(.7f, .7f, .7f);
        var mesh = descriptor.AddSlot("Mesh").AttachComponent<SkinnedMeshRenderer>();
        var model = descriptor.AddSlot("Model");
        model.LocalScale = new float3(.01f, .01f, .01f);
        var rig = model.AttachComponent<Rig>();
        model.AttachComponent<MeshRendererMaterialRelay>();
        var sourceRoot = model.AddSlot("RootNode");
        sourceRoot.LocalRotation = floatQ.Euler(20, 30, 40);
        var bone = sourceRoot.AddSlot("Armature");
        bone.LocalScale = new float3(2, 1, 3);
        bone.LocalPosition = new float3(1, 2, 3);
        bone.LocalRotation = floatQ.Euler(13, 17, 19);
        var end = bone.AddSlot("End");
        end.LocalPosition = new float3(.3f, .4f, .5f);
        rig.Bones.Add(bone); rig.Bones.Add(end);
        mesh.Bones.Add(end); mesh.Bones.Add(bone);
        var sameName = descriptor.AddSlot("RootNode");
        var sources = new Dictionary<Slot, string> { [model] = "model", [sourceRoot] = "model", [bone] = "model" };
        var paths = new Dictionary<Slot, string> { [model] = "", [sourceRoot] = "RootNode", [bone] = "RootNode/Armature" };
        var avatar = new VrchatAvatar { FbxGuid = "model", HasUnpackedRootPlacement = true };
        avatar.UninstantiatedFbxGuids.Add("model");
        var points = new[] { float3.Zero, float3.Right, float3.Up, float3.Forward };
        var watched = new[] { mesh.Slot, bone, end, sameName };
        var before = watched.Select(slot => points.Select(point => slot.LocalPointToGlobal(point)).ToArray()).ToArray();
        VrchatHierarchyCleanup.CollapseUnpackedWrappers(root, avatar, descriptor, model, sources, paths);
        Require(descriptor.IsDestroyed && model.IsDestroyed && sourceRoot.IsDestroyed, "known wrappers should collapse");
        Require(centered.Children.SequenceEqual(new[] { mesh.Slot, bone, sameName }), "source sibling order and unrelated names must survive");
        Require(!centered.IsDestroyed && centered.GetComponent<CapsuleCollider>() != null, "functional center must survive");
        Require(mesh.Bones.SequenceEqual(new[] { end, bone }) && root.GetComponent<Rig>().Bones.SequenceEqual(new[] { bone, end }),
            "skin indices and imported bone registry must survive");
        for (int i = 0; i < watched.Length; i++)
            for (int j = 0; j < points.Length; j++)
                Require(MathX.Distance(before[i][j], watched[i].LocalPointToGlobal(points[j])) < .00002f,
                    "wrapper composition must preserve affine bases even below a nonuniform ancestor");

        foreach (string mode in new[] { "component", "slot", "field", "rig", "scale", "inactive", "relay", "tag", "instance" })
        {
            var testRoot = parent.AddSlot(mode);
            var wrapper = testRoot.AddSlot("Avatar");
            var child = wrapper.AddSlot("Bone");
            switch (mode)
            {
                case "component": wrapper.AttachComponent<ValueField<int>>(); break;
                case "slot": testRoot.AttachComponent<ReferenceField<Slot>>().Reference.Target = wrapper; break;
                case "field": testRoot.AttachComponent<ReferenceField<IField>>().Reference.Target = wrapper.Position_Field; break;
                case "rig": testRoot.AttachComponent<ReferenceField<Rig>>().Reference.Target = wrapper.AttachComponent<Rig>(); break;
                case "scale": wrapper.LocalScale = new float3(1, 2, 1); break;
                case "inactive": wrapper.ActiveSelf = false; break;
                case "relay": wrapper.AttachComponent<MeshRendererMaterialRelay>().Renderers.Add(child.AttachComponent<MeshRenderer>()); break;
                case "tag": wrapper.Tag = "Authored"; break;
            }
            avatar.HasUnpackedRootPlacement = mode != "instance";
            VrchatHierarchyCleanup.CollapseUnpackedWrappers(testRoot, avatar, wrapper, null, new Dictionary<Slot, string>(), new Dictionary<Slot, string>());
            Require(!wrapper.IsDestroyed && child.Parent == wrapper, "meaningful wrapper must be retained: " + mode);
        }
        Console.WriteLine("PASS: wrapper cleanup preserves affine bases, skin indices, order, registries and observable wrapper identities");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
