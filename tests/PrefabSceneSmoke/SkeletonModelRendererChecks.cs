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
        var sources = VrchatSceneSetup.CaptureImportedObjects(new Dictionary<string, Slot>
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
        VrchatSceneSetup.RemoveDeletedMeshes(root, avatar, sources,
            new Dictionary<string, Slot> { ["body"] = authored, ["attachment"] = attached.Slot });
        Require(importedRenderer.IsDestroyed && importedAccessory.IsDestroyed,
            "skeleton-only FBX must not render a second avatar or unplaced accessory");
        Require(!body.IsDestroyed && !bone.IsDestroyed && !field.IsDestroyed && authoredRenderer.Bones[0] == bone,
            "skeleton-only cleanup must retain bones, owners and fields");
        Require(!authoredRenderer.IsDestroyed && !attached.IsDestroyed && !realRenderer.IsDestroyed,
            "authored geometry and a real instance of the model must survive");
        Console.WriteLine("PASS: skeleton-only model renderer removal preserves authored meshes, skeleton references and real instances");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
