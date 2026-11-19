using Elements.Core;
using FrooxEngine;

namespace VrmToResonitePackage.Vrchat;

internal static class VrchatHierarchyCleanup
{
    // Run only after path-based setup and sibling ordering. These are captured
    // converter/import identities, not arbitrary slots with familiar names.
    public static void CollapseUnpackedWrappers(Slot root, VrchatAvatar avatar, Slot descriptorRoot,
        Slot primaryWrapper, IReadOnlyDictionary<Slot, string> sources, IReadOnlyDictionary<Slot, string> paths)
    {
        if (!avatar.HasUnpackedRootPlacement || !avatar.UninstantiatedFbxGuids.Contains(avatar.FbxGuid)) return;
        var candidates = sources.Where(pair => pair.Value == avatar.FbxGuid &&
                paths.TryGetValue(pair.Key, out string path) && path.TrimStart('/') == "RootNode")
            .Select(pair => pair.Key).Append(primaryWrapper).Append(descriptorRoot)
            .Where(slot => slot != null && !slot.IsDestroyed && slot != root && slot.IsChildOf(root))
            .Distinct().OrderByDescending(Depth).ToArray();
        int removed = 0;
        foreach (var wrapper in candidates)
            if (TryCollapse(root, wrapper)) removed++;
        if (candidates.Length > 0)
            UniLog.Log($"Unpacked hierarchy wrappers: {removed} collapsed, {candidates.Length - removed} retained for transforms, state or references.");
    }

    private static bool TryCollapse(Slot root, Slot wrapper)
    {
        bool Retain(string reason) { UniLog.Log($"Retained hierarchy wrapper: {wrapper.Name} ({reason})"); return false; }
        if (wrapper.IsDestroyed || wrapper.Parent == null) return false;
        if (!wrapper.ActiveSelf || !string.IsNullOrEmpty(wrapper.Tag)) return Retain("active state or tag");
        // A uniform positive scale composes with every child rotation without shear.
        // Keep nonuniform/reflected/degenerate wrappers rather than approximate them.
        float3 scale = wrapper.LocalScale;
        if (!float.IsFinite(scale.x) || scale.x <= 0 || scale.x != scale.y || scale.x != scale.z) return Retain($"nonuniform or invalid scale: {scale}");
        var components = wrapper.GetComponents<Component>().ToArray();
        if (components.Any(c => c is not (Rig or MeshRendererMaterialRelay))) return Retain("behavior component");
        if (components.OfType<MeshRendererMaterialRelay>().Any(relay =>
                relay.Renderers.Any(renderer => renderer != null && !renderer.IsDestroyed))) return Retain("live material relay");
        if (components.OfType<Rig>().Any(rig => rig.Bones.Any(bone =>
                bone != null && !bone.IsDestroyed && bone != root && !bone.IsChildOf(root)))) return Retain("external rig bone");

        // A reference to the slot, one of its fields, or its registry components
        // gives the wrapper an observable identity. Child bone references are safe.
        var references = new List<IWorldElement>();
        foreach (var component in root.GetComponentsInChildren<Component>())
            component.GetReferencedObjects(references, assetRefOnly: false, persistentOnly: false);
        foreach (var reference in references)
            for (IWorldElement element = reference; element != null; element = element.Parent)
                if (element is Slot owner)
                {
                    if (owner == wrapper) return Retain($"referenced {reference.GetType().Name}");
                    break;
                }

        Slot parent = wrapper.Parent;
        var children = wrapper.Children.ToArray();
        var desired = parent.Children.SelectMany(slot => slot == wrapper ? children : new[] { slot }).ToArray();
        foreach (var child in children)
        {
            float3 position = wrapper.LocalPosition + wrapper.LocalRotation * (child.LocalPosition * scale.x);
            floatQ rotation = wrapper.LocalRotation * child.LocalRotation;
            float3 childScale = child.LocalScale * scale.x;
            child.SetParent(parent, false);
            child.LocalPosition = position;
            child.LocalRotation = rotation;
            child.LocalScale = childScale;
        }
        foreach (var rig in components.OfType<Rig>())
        {
            var destination = root.AttachComponent<Rig>();
            foreach (Slot bone in rig.Bones)
                if (bone != null && !bone.IsDestroyed) destination.Bones.Add(bone);
        }
        UniLog.Log($"Collapsed unused hierarchy wrapper: {wrapper.Name}");
        wrapper.Destroy();
        var ranks = desired.Select((slot, index) => (slot, index)).ToDictionary(pair => pair.slot, pair => pair.index);
        parent.SortChildren((a, b) => ranks[a].CompareTo(ranks[b]));
        return true;
    }

    private static int Depth(Slot slot)
    {
        int depth = 0;
        for (; slot.Parent != null; slot = slot.Parent) depth++;
        return depth;
    }
}
