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

    private static void ApplyInactiveStates(Slot root, VrchatAvatar avatar)
    {
        if (avatar.InactiveGameObjectNames.Count == 0)
        {
            return;
        }
        var inactive = new HashSet<string>(avatar.InactiveGameObjectNames, StringComparer.Ordinal);
        int applied = 0;
        foreach (Slot slot in EnumerateSlots(root))
        {
            if (slot.Name != null && inactive.Contains(slot.Name) && slot.ActiveSelf)
            {
                slot.ActiveSelf = false;
                applied++;
            }
        }
        UniLog.Log($"非アクティブ状態を {applied} スロットに反映しました。");
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
                renderer.BlendShapeWeights[index] = weight / 100f;
                applied++;
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
