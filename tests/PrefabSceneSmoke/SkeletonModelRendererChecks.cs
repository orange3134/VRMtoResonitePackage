using FrooxEngine;
using VrmToResonitePackage.Vrchat;

internal static class SkeletonModelRendererChecks
{
    public static void Run(Slot parent)
    {
        var root = parent.AddSlot("Skeleton-only FBX renderers");
        var model = root.AddSlot("Skeleton template");
        var body = model.AddSlot("Body");
        var importedRenderer = body.AttachComponent<SkinnedMeshRenderer>();
        var bone = body.AddSlot("Head");
        var field = body.AttachComponent<ValueField<float>>();
        var importedAccessory = bone.AddSlot("Imported accessory").AttachComponent<MeshRenderer>();
        var authored = root.AddSlot("Body");
        var authoredRenderer = authored.AttachComponent<SkinnedMeshRenderer>();
        authoredRenderer.Bones.Add(bone);
        var attached = bone.AddSlot("Authored attachment").AttachComponent<MeshRenderer>();
        var realInstance = root.AddSlot("Real FBX instance");
        var realRenderer = realInstance.AddSlot("Body").AttachComponent<SkinnedMeshRenderer>();
        var unused = model.AddSlot("Unused imported renderer");
        unused.AttachComponent<MeshRenderer>();
        var referenced = model.AddSlot("Referenced imported renderer");
        referenced.AttachComponent<MeshRenderer>();
        root.AttachComponent<ReferenceField<Slot>>().Reference.Target = referenced;
        var removedComponent = realInstance.AddSlot("Keep authored transform");
        removedComponent.AttachComponent<MeshRenderer>();
        var sources = VrchatSceneSetup.CaptureImportedObjects(new Dictionary<string, Slot>
            { ["skeleton-only"] = model, ["real-instance"] = realInstance });
        var paths = VrchatSceneSetup.CaptureImportedPaths(new Dictionary<string, Slot>
            { ["skeleton-only"] = model, ["real-instance"] = realInstance });
        // Capture happens before authored geometry is added in production. Exclude
        // the attachment from this synthetic capture and retain its exact identity.
        sources.Remove(attached.Slot);
        sources[authored] = "skeleton-only";
        var avatar = new VrchatAvatar();
        avatar.UninstantiatedFbxGuids.Add("skeleton-only");
        // The old name fallback incorrectly kept both copies of Body.
        avatar.PrefabRendererStates[new(null, "Body")] = true;
        avatar.PrefabRendererStates[new(null, "Imported accessory")] = true;
        avatar.PrefabRendererStates[new("real-instance", "Body")] = true;
        avatar.RemovedModelRenderers.Add(new("real-instance", paths[removedComponent]));
        var candidates = new HashSet<Slot>();
        VrchatSceneSetup.RemoveDeletedMeshes(root, avatar, sources,
            new Dictionary<string, Slot> { ["body"] = authored, ["attachment"] = attached.Slot }, paths, candidates);
        Require(importedRenderer.IsDestroyed && importedAccessory.IsDestroyed,
            "skeleton-only FBX must not render a second avatar or unplaced accessory");
        Require(!body.IsDestroyed && !bone.IsDestroyed && !field.IsDestroyed && authoredRenderer.Bones[0] == bone,
            "skeleton-only cleanup must retain bones, owners and fields");
        Require(!authoredRenderer.IsDestroyed && !attached.IsDestroyed && !realRenderer.IsDestroyed,
            "authored geometry and a real instance of the model must survive");
        VrchatSceneSetup.RemoveEmptyMeshTemplates(root, candidates, new[] { authored, attached.Slot });
        Require(unused.IsDestroyed && !referenced.IsDestroyed && !removedComponent.IsDestroyed,
            "only unreferenced template owners are pruned; referenced slots and authored component removals survive");
        Require(!body.IsDestroyed && !bone.IsDestroyed && !field.IsDestroyed && authoredRenderer.Bones[0] == bone,
            "template cleanup preserves the skin's skeleton, fields and parents");
        Console.WriteLine("PASS: skeleton-only model renderer removal preserves authored meshes, skeleton references and real instances");
        CheckUnusedBranches(parent);
        CheckHierarchyOrder(parent);
    }

    private static void CheckHierarchyOrder(Slot parent)
    {
        var root = parent.AddSlot("Unity child order");
        var generated = root.AddSlot("Generated first");
        var second = root.AddSlot("Same name");
        var wrapper = root.AddSlot("Eye pivot");
        var third = wrapper.AddSlot("Eye");
        var first = root.AddSlot("Same name");
        var support = root.AddSlot("Generated second");
        first.LocalPosition = new Elements.Core.float3(1, 2, 3);
        var skin = root.AttachComponent<SkinnedMeshRenderer>();
        skin.Bones.Add(second); skin.Bones.Add(first); skin.Bones.Add(third);
        VrchatBoneTarget Target(long id) => new(null, null, null, "prefab", id);
        var avatar = new VrchatAvatar();
        avatar.HierarchyOrders.Add(new(Target(1), new[] { Target(2), Target(3), Target(4), Target(99) }));
        var slots = new Dictionary<string, Slot> { ["prefab:1"] = root, ["prefab:2"] = first,
            ["prefab:3"] = second, ["prefab:4"] = third };
        var empty = new Dictionary<Slot, string>();
        VrchatSceneSetup.ApplyHierarchyOrder(avatar, empty, empty, slots);
        var wanted = new[] { first, second, wrapper, generated, support };
        Require(root.Children.SequenceEqual(wanted), "source siblings must follow Unity order, with stable generated siblings afterward");
        Require(third.Parent == wrapper && first.LocalPosition == new Elements.Core.float3(1, 2, 3) &&
            skin.Bones.SequenceEqual(new[] { second, first, third }), "reordering must preserve hierarchy, transforms and skin indices");
        VrchatSceneSetup.ApplyHierarchyOrder(avatar, empty, empty, slots);
        Require(root.Children.SequenceEqual(wanted), "ordering must be idempotent");
        var another = wrapper.AddSlot("Merged source sibling");
        slots["prefab:5"] = another;
        avatar.HierarchyOrders.Clear();
        avatar.HierarchyOrders.Add(new(Target(1), new[] { Target(4), Target(5), Target(2) }));
        VrchatSceneSetup.ApplyHierarchyOrder(avatar, empty, empty, slots);
        Require(root.Children.SequenceEqual(wanted), "merged sibling branches must remain explicitly unresolved rather than arbitrarily ordered");
        Console.WriteLine("PASS: Unity sibling order preserves skin indices, duplicate-name identities, helper branches and generated order");
    }

    private static void CheckUnusedBranches(Slot parent)
    {
        var root = parent.AddSlot("Unused skeleton branches");
        var model = root.AddSlot("Template");
        var obsolete = model.AddSlot("Old parent");
        var obsoleteEnd = obsolete.AddSlot("End");
        var used = model.AddSlot("Skin bone");
        var authored = model.AddSlot("Authored unweighted end");
        var fieldTarget = model.AddSlot("Referenced field owner");
        root.AttachComponent<ReferenceField<IField>>().Reference.Target = fieldTarget.ActiveSelf_Field;
        root.AttachComponent<SkinnedMeshRenderer>().Bones.Add(used);
        var humanoid = model.AddSlot("Humanoid bone");
        root.AttachComponent<BipedRig>()[Renderite.Shared.BodyNode.Head] = humanoid;
        var rig = model.AttachComponent<Rig>();
        foreach (var bone in new[] { obsolete, obsoleteEnd, used, authored, fieldTarget, humanoid }) rig.Bones.Add(bone);
        var consumedRig = root.AddSlot("Consumed rig").AttachComponent<Rig>();
        var consumedBone = model.AddSlot("Consumer bone");
        consumedRig.Bones.Add(consumedBone);
        root.AttachComponent<ReferenceField<Rig>>().Reference.Target = consumedRig;
        var real = root.AddSlot("Real instance");
        var realEnd = real.AddSlot("Unweighted end");
        var sources = VrchatSceneSetup.CaptureImportedObjects(new Dictionary<string, Slot>
            { ["template"] = model, ["real"] = real });
        var avatar = new VrchatAvatar();
        avatar.UninstantiatedFbxGuids.Add("template");
        VrchatSceneSetup.RemoveUnusedSkeletonTemplateNodes(root, avatar, sources, new[] { authored });
        Require(obsolete.IsDestroyed && obsoleteEnd.IsDestroyed && rig.Bones.Count == 4,
            "unused branches must be pruned bottom-up and removed from the import registry");
        Require(new[] { used, authored, fieldTarget, humanoid, consumedBone, realEnd }.All(slot => !slot.IsDestroyed),
            "skin, humanoid, field, authored, consumed Rig and real-instance nodes must survive");
        Require(rig.Bones.All(bone => bone != null && !bone.IsDestroyed), "registry must not retain destroyed references");
        Console.WriteLine("PASS: unused skeleton templates are pruned while authored and consumed references survive");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
