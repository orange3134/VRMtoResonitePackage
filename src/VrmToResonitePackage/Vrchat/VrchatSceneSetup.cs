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
    public static Dictionary<string, Slot> CreateMeshCopies(VrchatAvatar avatar, Dictionary<Slot, string> sources,
        Func<VrchatMeshCopy, Slot> resolveParent, IReadOnlyDictionary<Slot, string> importedPaths,
        Dictionary<string, Slot> prefabSlots = null)
    {
        prefabSlots ??= new(StringComparer.Ordinal);
        var importedSources = sources.ToArray();
        var authoredObjects = new Dictionary<string, Slot>();
        var copies = new List<(VrchatMeshCopy Copy, Slot Slot)>();
        var staticScaleCorrections = new Dictionary<Slot, float>();
        var replacedRenderers = new HashSet<MeshRenderer>();
        foreach (VrchatMeshCopy copy in avatar.MeshCopies)
        {
            var matchesBySource = importedSources.Where(entry => !entry.Key.IsDestroyed && entry.Value == copy.FbxGuid &&
                entry.Key.GetComponent<MeshRenderer>() != null &&
                (!copy.IsSkinned || entry.Key.GetComponent<SkinnedMeshRenderer>() != null) &&
                (copy.SourcePath != null
                    ? importedPaths.TryGetValue(entry.Key, out string path) &&
                      (copy.SourcePath == path || copy.SourcePath.EndsWith("/" + path, StringComparison.Ordinal))
                    : entry.Key.Name == copy.SourceName)).Select(entry => entry.Key).ToArray();
            if (matchesBySource.Length > 1)
                throw new InvalidDataException($"Ambiguous prefab mesh copy source: {copy.Name} <- {copy.SourcePath ?? copy.SourceName}");
            Slot source = matchesBySource.SingleOrDefault();
            if (source == null)
            {
                UniLog.Warning($"Prefab mesh copy source missing: {copy.Name} <- {copy.SourceName}");
                continue;
            }
            if (copy.ReplaceSourceRenderer) replacedRenderers.Add(source.GetComponent<MeshRenderer>());
            // Duplicate the renderer slot, retaining external mesh, material and skeleton references.
            Slot duplicate = source.Duplicate(source.Parent, settings: new DuplicationSettings
            {
                SlotFilter = slot => slot == source,
            });
            copies.Add((copy, duplicate));
            duplicate.Name = copy.Name;
            if (copy.Transform?.GameObjectKey != null) authoredObjects[copy.Transform.GameObjectKey] = duplicate;
            duplicate.ActiveSelf = copy.Active;
            var meshRenderer = duplicate.GetComponent<MeshRenderer>();
            if (copy.RendererRemoved)
            {
                meshRenderer.Destroy();
                // Preserve the GameObject slot for children, descriptor and attachment paths.
                continue;
            }
            if (!copy.IsSkinned && meshRenderer is SkinnedMeshRenderer)
            {
                // Unity can use an imported skinned mesh as a static mesh.
                // Reproduce the authored component type while preserving its asset references.
                var mesh = meshRenderer.Mesh.Target;
                var materials = meshRenderer.Materials.ToArray();
                meshRenderer.Destroy();
                meshRenderer = duplicate.AttachComponent<MeshRenderer>();
                meshRenderer.Mesh.Target = mesh;
                foreach (var material in materials) meshRenderer.Materials.Add().Target = material;
            }
            meshRenderer.Enabled = copy.Enabled;
            foreach (Slot slot in EnumerateSlots(duplicate)) sources[slot] = copy.FbxGuid;
            UniLog.Log($"Prefab mesh copy: {copy.Name} <- {copy.SourceName} (fbx={copy.FbxGuid})");
        }
        // Register every authored renderer before resolving parents: a renderer can live on
        // the descriptor root and own other renderer objects, regardless of document order.
        foreach (var (copy, slot) in copies)
        {
            if (copy.Transform?.Key == null) continue;
            if (prefabSlots.TryGetValue(copy.Transform.Key, out Slot placeholder) && placeholder != slot)
            {
                // The mesh template can be in an FBX instance beneath this placeholder.
                // Detach its duplicate before moving that instance under the replacement.
                slot.SetParent(placeholder.Parent, false);
                foreach (Slot child in placeholder.Children.ToArray()) child.SetParent(slot, false);
                placeholder.Destroy();
            }
            prefabSlots[copy.Transform.Key] = slot;
        }
        foreach (var (copy, slot) in copies)
        {
            if (copy.Transform is not {} transform) continue;
            slot.Parent = resolveParent(copy);
            slot.LocalPosition = new float3(transform.LocalPosition.X, transform.LocalPosition.Y, transform.LocalPosition.Z);
            slot.LocalRotation = new floatQ(transform.LocalRotation.X, transform.LocalRotation.Y, transform.LocalRotation.Z, transform.LocalRotation.W);
            slot.LocalScale = new float3(transform.LocalScale.X, transform.LocalScale.Y, transform.LocalScale.Z);
            if (!copy.IsSkinned && !copy.RendererRemoved)
            {
                float correction = ImportScale(copy.FbxGuid) / ImportScale(copy.ParentFbxGuid);
                if (!float.IsFinite(correction) || correction == 0)
                    throw new InvalidDataException($"Invalid prefab mesh import scale: {copy.Name}");
                slot.LocalScale *= correction;
                staticScaleCorrections[slot] = correction;
            }
        }
        // Parent creation and renderer registration can provide authored bone identities.
        // Resolve skins only after all of those slots are available.
        var capturedSources = importedSources.ToDictionary(entry => entry.Key, entry => entry.Value);
        foreach (var (copy, slot) in copies)
        {
            var renderer = slot.GetComponent<SkinnedMeshRenderer>();
            for (int i = 0; i < (renderer?.Bones.Count ?? 0); i++)
            {
                if (!copy.BoneTargets.TryGetValue(i, out var target)) continue;
                if (target.Name == null)
                {
                    renderer.Bones[i] = null;
                    continue;
                }
                Slot bone = ResolveImportedTarget(target, capturedSources, importedPaths, prefabSlots);
                if (bone != null)
                {
                    renderer.Bones[i] = bone;
                    continue;
                }
                // Unpacking loses the FBX identity of local transforms. A renamed bone's
                // authored path cannot identify its original imported slot. Retain the
                // copied skin's existing binding instead of guessing by name or aborting.
                if (target.PrefabGuid != null && target.TransformFileId != 0 && renderer.Bones[i] != null)
                {
                    UniLog.Warning($"Prefab bone keeps imported skin binding: {copy.Name} / {target.Name}");
                    continue;
                }
                throw new InvalidDataException($"複製メッシュのボーン参照を特定できません: {copy.Name} / {target.Name}");
            }
        }
        // Import units correct this renderer's mesh only. Unity-authored child transforms
        // must not inherit the extra factor, including attachments created before the copy.
        foreach (var (slot, correction) in staticScaleCorrections)
            foreach (Slot child in slot.Children)
            {
                child.LocalPosition /= correction;
                child.LocalScale /= correction;
            }
        // Unpacked prefabs explicitly describe their renderers. Keep imported bones and
        // slots as references, but do not also render the FBX template at its old location.
        foreach (var renderer in replacedRenderers) renderer.Destroy();
        return authoredObjects;

        float ImportScale(string guid) => guid == null ? 1f : guid == avatar.FbxGuid ? avatar.FbxImportScale :
            avatar.AdditionalFbxs.FirstOrDefault(model => model.Guid == guid)?.ImportScale ?? 1f;
    }

    public static Slot ResolveImportedTarget(VrchatBoneTarget target, IReadOnlyDictionary<Slot, string> sources,
        IReadOnlyDictionary<Slot, string> paths, IReadOnlyDictionary<string, Slot> prefabSlots = null)
    {
        // Copies and authored attachments have prefab identity but no captured import path.
        if (target?.PrefabGuid != null && target.TransformFileId != 0 &&
            prefabSlots?.TryGetValue($"{target.PrefabGuid}:{target.TransformFileId}", out Slot authored) == true)
            return authored.IsDestroyed ? null : authored;
        if (target?.FbxGuid == null) return null;
        var matches = sources.Where(entry => !entry.Key.IsDestroyed && entry.Value == target.FbxGuid &&
            (target.Path != null ? paths.TryGetValue(entry.Key, out string path) && BonePathMatches(path, target.Path)
                : entry.Key.Name == target.Name)).Select(entry => entry.Key).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool BonePathMatches(string importedPath, string targetPath)
    {
        // Assimp may retain its synthetic root in one representation but omit it in the other.
        static string Normalize(string path)
        {
            path = path.TrimStart('/');
            return path == "RootNode" ? "" : path.StartsWith("RootNode/", StringComparison.Ordinal) ? path[9..] : path;
        }
        return Normalize(importedPath) == Normalize(targetPath);
    }

    public static Dictionary<Slot, string> CaptureImportedObjects(IReadOnlyDictionary<string, Slot> roots)
    {
        var sources = new Dictionary<Slot, string>();
        foreach ((string guid, Slot root) in roots)
            foreach (Slot slot in EnumerateSlots(root)) sources[slot] = guid;
        return sources;
    }

    public static Dictionary<Slot, string> CaptureImportedPaths(IReadOnlyDictionary<string, Slot> roots)
    {
        var paths = new Dictionary<Slot, string>();
        foreach (Slot root in roots.Values)
        {
            paths[root] = "";
            foreach (Slot child in root.Children) Visit(child, child.Name);
        }
        return paths;

        void Visit(Slot slot, string path)
        {
            paths[slot] = path;
            foreach (Slot child in slot.Children) Visit(child, path + "/" + child.Name);
        }
    }

    public static void RemapImportedRoot(Slot source, Slot target, Dictionary<Slot, string> sources,
        Dictionary<Slot, string> paths)
    {
        if (sources.Remove(source, out string guid)) sources[target] = guid;
        if (paths.Remove(source, out string path)) paths[target] = path;
    }

    public static void RemoveEditorOnlyObjects(VrchatAvatar avatar, IReadOnlyDictionary<Slot, string> sources,
        IReadOnlyDictionary<Slot, string> importedPaths)
    {
        foreach ((Slot slot, string guid) in sources)
        {
            if (slot.IsDestroyed) continue;
            bool excluded = avatar.EditorOnlyModelObjects.Contains(new VrchatGameObjectReference(guid, slot.Name));
            if (importedPaths.TryGetValue(slot, out string path) && avatar.EditorOnlyModelPaths.TryGetValue(guid, out var excludedPaths))
                excluded |= excludedPaths.Any(p => p == path || p.EndsWith("/" + path, StringComparison.Ordinal));
            if (!excluded) continue;
            UniLog.Log($"Removing EditorOnly object subtree: {slot.Name} (fbx={guid})");
            slot.Destroy();
        }
    }

    public static void Apply(Slot root, VrchatAvatar avatar, IReadOnlyDictionary<Slot, string> sources,
        IReadOnlyDictionary<string, Slot> authoredObjects = null)
    {
        ApplyInactiveStates(root, avatar, sources);
        ApplyInitialBlendShapes(root, avatar, sources, authoredObjects);
    }

    /// <summary>
    /// Removes imported meshes that the selected prefab deleted. Ukon-style avatars share one FBX
    /// across several prefabs, each built by deleting mesh GameObjects in Unity; the FBX import
    /// brings them all back, so any renderer whose GameObject is not in the prefab is dropped.
    /// Must run before avatar/material setup so nothing downstream references the removed meshes.
    /// </summary>
    public static void RemoveDeletedMeshes(Slot root, VrchatAvatar avatar,
        IReadOnlyDictionary<Slot, string> importedMeshSources)
    {
        if (avatar.PrefabGameObjectNames.Count == 0 && avatar.PrefabRendererStates.Count == 0)
        {
            return;
        }
        string SourceGuid(Slot slot) => FbxGuidForSlot(root, slot, avatar, importedMeshSources);
        bool Keep(Slot slot) => avatar.ShouldKeepRenderer(SourceGuid(slot), slot.Name);

        // Renderer slots whose GameObject name the prefab does not contain.
        var extras = new List<Slot>();
        foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>())
        {
            Slot slot = renderer.Slot;
            if (slot.Name != null && !Keep(slot))
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
                .Any(r => r.Slot != slot && r.Slot.Name != null && Keep(r.Slot));
            UniLog.Log($"Removing prefab-excluded mesh: {slot.Name} (fbx={SourceGuid(slot)})");
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
        foreach (var group in root.GetComponentsInChildren<MeshRenderer>().GroupBy(r => SourceGuid(r.Slot)))
        {
            UniLog.Log($"Retained prefab meshes (fbx={group.Key}): " +
                       string.Join(", ", group.Select(r => r.Slot.Name).Distinct()));
        }
    }

    public static void ApplyModularAvatar(Slot root, VrchatAvatar avatar, IDictionary<int, Slot> physicsNodes = null)
    {
        int merged = 0;
        foreach (VrchatModularMergeArmature merge in avatar.ModularMergeArmatures)
        {
            int rewritten = ApplyMergeArmature(root, merge, physicsNodes);
            merged += rewritten;
            UniLog.Log($"Modular Avatar Merge Armature: {merge.SourceName} -> {merge.TargetName}, " +
                       $"{rewritten} bone reference(s) rewritten.");
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

    private static int ApplyMergeArmature(Slot root, VrchatModularMergeArmature merge, IDictionary<int, Slot> physicsNodes)
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
        // Resolve identities before merging, then remap every physics role (root, ignore,
        // collider) with the same mapping as the skin. Updating each merge also handles
        // destinations that become sources of a later Merge Armature component.
        if (physicsNodes != null)
            foreach (int node in physicsNodes.Keys.ToArray())
                if (physicsNodes[node] == source)
                    physicsNodes[node] = target;
                else if (physicsNodes[node] is {} slot && mappings.TryGetValue(slot, out Slot mapped))
                    physicsNodes[node] = mapped;
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
            // A composed clothing FBX can be prefab-parented under the target armature before
            // Modular Avatar merges it. Keep descendant candidates; the matching-child score
            // below distinguishes the nested source armature from unrelated same-name slots.
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
            // Source and target represent the same semantic bone. Preserve the authored local
            // pose of accessory children when moving between them; preserving world pose bakes
            // any source/target armature coordinate-system difference into the accessory chain.
            float3 position = child.LocalPosition;
            floatQ rotation = child.LocalRotation;
            float3 scale = child.LocalScale;
            child.Parent = target;
            child.LocalPosition = position;
            child.LocalRotation = rotation;
            child.LocalScale = scale;
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

    private static void ApplyInactiveStates(Slot root, VrchatAvatar avatar, IReadOnlyDictionary<Slot, string> sources)
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
            string fbxGuid = FbxGuidForSlot(root, slot, avatar, sources);
            if (inactive.Contains(new VrchatGameObjectReference(fbxGuid, slot.Name)) ||
                inactive.Contains(new VrchatGameObjectReference(null, slot.Name)))
            {
                slot.ActiveSelf = false;
                applied++;
            }
        }
        UniLog.Log($"非アクティブ状態を {applied} スロットに反映しました。");
    }

    internal static string FbxGuidForSlot(Slot root, Slot slot, VrchatAvatar avatar, IReadOnlyDictionary<Slot, string> sources)
    {
        // Prefab placement and Modular Avatar can move objects into another model's hierarchy.
        if (sources.TryGetValue(slot, out string sourceGuid)) return sourceGuid;
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

    public static void ApplyInitialBlendShapes(Slot root, VrchatAvatar avatar, IReadOnlyDictionary<Slot, string> sources,
        IReadOnlyDictionary<string, Slot> authoredObjects = null)
    {
        List<SkinnedMeshRenderer> renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>().ToList();
        var assignedRendererSlots = new HashSet<Slot>();

        int applied = 0;
        foreach (VrchatRendererMaterials rm in avatar.RendererMaterials)
        {
            SkinnedMeshRenderer renderer = renderers.FirstOrDefault(candidate =>
                !assignedRendererSlots.Contains(candidate.Slot) &&
                (rm.PrefabObjectKey == null || authoredObjects?.GetValueOrDefault(rm.PrefabObjectKey) == candidate.Slot) &&
                string.Equals(candidate.Slot.Name, rm.RendererGameObjectName, StringComparison.Ordinal) &&
                (string.IsNullOrEmpty(rm.FbxGuid) || string.Equals(
                    FbxGuidForSlot(root, candidate.Slot, avatar, sources), rm.FbxGuid,
                    StringComparison.OrdinalIgnoreCase)));
            if (renderer == null)
            {
                continue;
            }
            assignedRendererSlots.Add(renderer.Slot);
            if (rm.InitialBlendShapes.Count == 0)
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
