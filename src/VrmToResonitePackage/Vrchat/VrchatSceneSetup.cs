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

    private static void ApplyInitialBlendShapes(Slot root, VrchatAvatar avatar)
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
                Sync<float> field = renderer.BlendShapeWeights.GetElement(index);
                field.Value = weight / 100f;
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
