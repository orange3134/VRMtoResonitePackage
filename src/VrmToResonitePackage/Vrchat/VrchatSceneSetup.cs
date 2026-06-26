using Elements.Core;
using FrooxEngine;

namespace VrmToResonitePackage.Vrchat;

/// <summary>
/// Applies prefab-authored scene state that the FBX import cannot carry: GameObjects that start
/// inactive (costume swaps etc.) and SkinnedMeshRenderer blendshape weights that are non-zero by
/// default. Both are matched to the imported hierarchy by GameObject/slot name.
/// </summary>
internal static class VrchatSceneSetup
{
    public static void Apply(Slot root, VrchatAvatar avatar)
    {
        ApplyInactiveStates(root, avatar);
        ApplyInitialBlendShapes(root, avatar);
    }

    /// <summary>
    /// Removes imported meshes that the selected prefab deleted. Ukon-style avatars share one FBX
    /// across several prefabs, each built by deleting mesh GameObjects in Unity; the FBX import
    /// brings them all back, so any renderer whose GameObject is not in the prefab is dropped.
    /// Must run before avatar/material setup so nothing downstream references the removed meshes.
    /// </summary>
    public static void RemoveDeletedMeshes(Slot root, VrchatAvatar avatar)
    {
        if (avatar.PrefabGameObjectNames.Count == 0)
        {
            return;
        }
        HashSet<string> keep = avatar.PrefabGameObjectNames;

        // Renderer slots whose GameObject name the prefab does not contain.
        var extras = new List<Slot>();
        foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>())
        {
            Slot slot = renderer.Slot;
            if (slot.Name != null && !keep.Contains(slot.Name))
            {
                extras.Add(slot);
            }
        }

        int removed = 0;
        foreach (Slot slot in extras.Distinct())
        {
            if (slot.IsDestroyed)
            {
                continue; // already removed as a descendant of an earlier extra slot
            }
            // Don't destroy a slot that still hosts a kept mesh somewhere below it; just strip its
            // own renderer/mesh in that (rare) case.
            bool hasKeptDescendant = slot.GetComponentsInChildren<MeshRenderer>()
                .Any(r => r.Slot != slot && r.Slot.Name != null && keep.Contains(r.Slot.Name));
            if (hasKeptDescendant)
            {
                foreach (MeshRenderer r in slot.GetComponents<MeshRenderer>())
                {
                    r.Destroy();
                }
            }
            else
            {
                slot.Destroy();
            }
            removed++;
        }

        // Drop the now-unreferenced mesh assets the removed renderers used, so they aren't packaged.
        var referencedMeshes = new HashSet<RefID>();
        foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>())
        {
            var mesh = renderer.Mesh.Target;
            if (mesh != null)
            {
                referencedMeshes.Add(mesh.ReferenceID);
            }
        }
        foreach (StaticMesh mesh in root.GetComponentsInChildren<StaticMesh>())
        {
            if (!referencedMeshes.Contains(mesh.ReferenceID))
            {
                mesh.Slot.Destroy();
            }
        }

        if (removed > 0)
        {
            UniLog.Log($"prefabに含まれないメッシュを {removed} 個削除しました。");
        }
    }

    public static void ApplyModularAvatar(Slot root, VrchatAvatar avatar)
    {
        int merged = 0;
        foreach (VrchatModularMergeArmature merge in avatar.ModularMergeArmatures)
        {
            merged += ApplyMergeArmature(root, merge);
        }

        int proxied = 0;
        foreach (VrchatModularBoneProxy proxy in avatar.ModularBoneProxies)
        {
            if (ApplyBoneProxy(root, proxy))
            {
                proxied++;
            }
        }

        if (merged > 0 || proxied > 0)
        {
            UniLog.Log($"Modular Avatar applied: {merged} Merge Armature bone mapping(s), {proxied} Bone Proxy move(s).");
        }
    }

    private static int ApplyMergeArmature(Slot root, VrchatModularMergeArmature merge)
    {
        Slot target = FindFirstSlot(root, merge.TargetName);
        if (target == null)
        {
            return 0;
        }

        Slot source = null;
        var mappings = new Dictionary<Slot, Slot>();
        foreach (Slot candidate in FindMergeSourceCandidates(root, merge, target))
        {
            var candidateMappings = new Dictionary<Slot, Slot>();
            CollectBoneMappings(candidate, target, merge, candidateMappings);
            if (candidateMappings.Count > mappings.Count)
            {
                source = candidate;
                mappings = candidateMappings;
            }
        }
        if (mappings.Count == 0)
        {
            return 0;
        }

        int rewritten = RewriteSkinnedMeshBones(root, mappings);
        foreach ((Slot src, Slot dst) in mappings.OrderByDescending(pair => Depth(pair.Key)))
        {
            MoveUnmappedChildren(src, dst, mappings);
            if (!HasRendererInSubtree(src))
            {
                src.Destroy();
            }
        }
        if (!source.IsDestroyed && !source.Children.Any() && !HasRendererInSubtree(source))
        {
            source.Destroy();
        }
        return rewritten;
    }

    private static IEnumerable<Slot> FindMergeSourceCandidates(Slot root, VrchatModularMergeArmature merge,
        Slot target)
    {
        string sourceName = merge.SourceName ?? "";
        string targetName = merge.TargetName ?? "";
        return EnumerateSlots(root).Where(slot =>
            slot != target &&
            !IsDescendantOf(slot, target) &&
            !IsDescendantOf(target, slot) &&
            (string.Equals(slot.Name, sourceName, StringComparison.Ordinal) ||
             string.Equals(slot.Name, targetName, StringComparison.Ordinal) ||
             (!string.IsNullOrEmpty(sourceName) && (slot.Name ?? "").StartsWith(sourceName, StringComparison.Ordinal)) ||
             (!string.IsNullOrEmpty(targetName) && (slot.Name ?? "").StartsWith(targetName, StringComparison.Ordinal))));
    }

    private static void CollectBoneMappings(Slot source, Slot target, VrchatModularMergeArmature merge,
        Dictionary<Slot, Slot> mappings)
    {
        foreach (Slot sourceChild in source.Children.ToList())
        {
            string targetName = StripAffixes(sourceChild.Name, merge.Prefix, merge.Suffix);
            Slot targetChild = target.Children.FirstOrDefault(child =>
                string.Equals(child.Name, targetName, StringComparison.Ordinal));
            if (targetChild == null)
            {
                continue;
            }
            mappings[sourceChild] = targetChild;
            CollectBoneMappings(sourceChild, targetChild, merge, mappings);
        }
    }

    private static string StripAffixes(string name, string prefix, string suffix)
    {
        name ??= "";
        prefix ??= "";
        suffix ??= "";
        if (name.Length <= prefix.Length + suffix.Length ||
            !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }
        return name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
    }

    private static int RewriteSkinnedMeshBones(Slot root, Dictionary<Slot, Slot> mappings)
    {
        int rewritten = 0;
        foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            for (int i = 0; i < renderer.Bones.Count; i++)
            {
                Slot bone = renderer.Bones[i];
                if (bone != null && mappings.TryGetValue(bone, out Slot mapped))
                {
                    renderer.Bones[i] = mapped;
                    rewritten++;
                }
            }
        }
        return rewritten;
    }

    private static void MoveUnmappedChildren(Slot source, Slot target, Dictionary<Slot, Slot> mappings)
    {
        foreach (Slot child in source.Children.ToList())
        {
            if (mappings.ContainsKey(child))
            {
                continue;
            }
            float3 position = child.GlobalPosition;
            floatQ rotation = child.GlobalRotation;
            float3 scale = child.GlobalScale;
            child.Parent = target;
            child.GlobalPosition = position;
            child.GlobalRotation = rotation;
            child.GlobalScale = scale;
        }
    }

    private static bool ApplyBoneProxy(Slot root, VrchatModularBoneProxy proxy)
    {
        Slot source = FindFirstSlot(root, proxy.SourceName);
        Slot target = FindFirstSlot(root, proxy.TargetName, candidate => candidate != source);
        if (source == null || target == null || source == root || target == source ||
            IsDescendantOf(target, source))
        {
            return false;
        }

        float3 position = source.GlobalPosition;
        floatQ rotation = source.GlobalRotation;
        source.Parent = target;

        switch (proxy.AttachmentMode)
        {
            case 2: // AsChildKeepWorldPose
                source.GlobalPosition = position;
                source.GlobalRotation = rotation;
                break;
            case 3: // AsChildKeepRotation
                source.LocalPosition = float3.Zero;
                source.GlobalRotation = rotation;
                break;
            case 4: // AsChildKeepPosition
                source.GlobalPosition = position;
                source.LocalRotation = floatQ.Identity;
                break;
            default:
                source.LocalPosition = float3.Zero;
                source.LocalRotation = floatQ.Identity;
                break;
        }
        if (proxy.MatchScale)
        {
            source.LocalScale = float3.One;
        }
        return true;
    }

    private static Slot FindFirstSlot(Slot root, string name, Func<Slot, bool> predicate = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        return EnumerateSlots(root).FirstOrDefault(slot =>
            string.Equals(slot.Name, name, StringComparison.Ordinal) &&
            (predicate == null || predicate(slot)));
    }

    private static bool IsDescendantOf(Slot slot, Slot ancestor)
    {
        for (Slot current = slot?.Parent; current != null; current = current.Parent)
        {
            if (current == ancestor)
            {
                return true;
            }
        }
        return false;
    }

    private static int Depth(Slot slot)
    {
        int depth = 0;
        for (Slot current = slot; current?.Parent != null; current = current.Parent)
        {
            depth++;
        }
        return depth;
    }

    private static bool HasRendererInSubtree(Slot slot)
        => slot.GetComponentsInChildren<MeshRenderer>().Any();

    private static void ApplyInactiveStates(Slot root, VrchatAvatar avatar)
    {
        if (avatar.InactiveGameObjects.Count == 0)
        {
            return;
        }
        var inactive = new HashSet<VrchatGameObjectReference>(avatar.InactiveGameObjects);
        int applied = 0;
        foreach (Slot slot in EnumerateSlots(root))
        {
            if (slot.Name == null || !slot.ActiveSelf)
            {
                continue;
            }
            string fbxGuid = FbxGuidForSlot(root, slot, avatar);
            if (inactive.Contains(new VrchatGameObjectReference(fbxGuid, slot.Name)) ||
                inactive.Contains(new VrchatGameObjectReference(null, slot.Name)))
            {
                slot.ActiveSelf = false;
                applied++;
            }
        }
        UniLog.Log($"非アクティブ状態を {applied} スロットに反映しました。");
    }

    private static string FbxGuidForSlot(Slot root, Slot slot, VrchatAvatar avatar)
    {
        for (Slot current = slot; current != null && current != root; current = current.Parent)
        {
            VrchatFbxAsset additional = avatar.AdditionalFbxs.FirstOrDefault(candidate =>
                string.Equals(current.Name, candidate.InstanceName, StringComparison.Ordinal));
            if (additional != null)
            {
                return additional.Guid;
            }
        }
        return avatar.FbxGuid;
    }

    public static void ApplyInitialBlendShapes(Slot root, VrchatAvatar avatar)
    {
        var renderersByName = new Dictionary<string, SkinnedMeshRenderer>(StringComparer.Ordinal);
        foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            string name = renderer.Slot.Name;
            if (name != null && !renderersByName.ContainsKey(name))
            {
                renderersByName[name] = renderer;
            }
        }

        int applied = 0;
        foreach (VrchatRendererMaterials rm in avatar.RendererMaterials)
        {
            if (rm.InitialBlendShapes.Count == 0 ||
                !renderersByName.TryGetValue(rm.RendererGameObjectName, out SkinnedMeshRenderer renderer))
            {
                continue;
            }
            int count = renderer.MeshBlendshapeCount;
            var appliedNames = new List<string>();
            foreach ((int index, float weight) in rm.InitialBlendShapes)
            {
                if (index < 0 || index >= count)
                {
                    continue;
                }
                while (renderer.BlendShapeWeights.Count <= index)
                {
                    renderer.BlendShapeWeights.Add();
                }
                // Unity blendshape weights are 0-100; Resonite's are 0-1 (1 = full shape).
                renderer.SetBlendShapeWeight(index, weight / 100f);
                appliedNames.Add($"{index}:{renderer.BlendShapeName(index)}={weight:G6}");
                applied++;
            }
            if (appliedNames.Count > 0)
            {
                UniLog.Log($"Initial blendshapes on {rm.RendererGameObjectName}: {string.Join(", ", appliedNames)}");
            }
        }
        if (applied > 0)
        {
            UniLog.Log($"初期ブレンドシェイプ値を {applied} 件反映しました。");
        }
    }

    private static IEnumerable<Slot> EnumerateSlots(Slot root)
    {
        yield return root;
        foreach (Slot child in root.Children)
        {
            foreach (Slot descendant in EnumerateSlots(child))
            {
                yield return descendant;
            }
        }
    }
}
