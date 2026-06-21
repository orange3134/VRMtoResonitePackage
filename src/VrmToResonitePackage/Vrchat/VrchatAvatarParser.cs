using Elements.Core;
using Vec3 = System.Numerics.Vector3;
using Quat = System.Numerics.Quaternion;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>
/// Reads a VRChat avatar out of an extracted .unitypackage: locates the primary avatar prefab
/// (the one carrying a VRCAvatarDescriptor), then extracts the humanoid bone map (from the FBX
/// importer meta), visemes/blink/view position (from the descriptor), spring physics (PhysBones)
/// and per-renderer material assignments into an engine-independent <see cref="VrchatAvatar"/>.
/// </summary>
public static class VrchatAvatarParser
{
    // Unity's synthetic Transform for an imported model's //RootNode. This stable local ID is
    // shared by model assets and is the target of the prefab instance's placement overrides.
    private const long UnityModelRootTransformFileId = -8679921383154817045L;

    private static readonly System.Text.RegularExpressions.Regex BlendShapeWeightPath =
        new(@"^m_BlendShapeWeights\.Array\.data\[(\d+)\]$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex MaterialSlotPath =
        new(@"^m_Materials\.Array\.data\[(\d+)\]$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private sealed class Candidate
    {
        public UnityAsset Source;        // the .prefab or .unity file the avatar was found in
        public UnityScene Scene;
        public YamlDocument Root;        // avatar root GameObject (null for a variant-of-FBX avatar)
        public YamlDocument Descriptor;  // VRCAvatarDescriptor MonoBehaviour
        public HashSet<long> Subtree;    // GameObject fileIds belonging to this avatar (empty for variant-of-FBX)
        public string Name;              // resolved avatar/display name
        public List<string> FbxGuidOverrides; // FBX models composed by a prefab variant
        public Dictionary<string, FbxPlacement> FbxPlacements;
        public bool HasOwnDescriptor;
        public bool IsComposedPrefab;

        /// <summary>A prefab variant whose hierarchy lives in an FBX (descriptor added on a stripped root).</summary>
        public bool IsVariantOfFbx => FbxGuidOverrides?.Count > 0;
        public int Size => IsVariantOfFbx ? FbxGuidOverrides.Count : Subtree?.Count ?? 0;
    }

    private sealed class FbxPlacement
    {
        public string InstanceName;
        public string ParentFbxGuid;
        public string ParentNodeName;
        public string TransformNodeName;
        public Vec3 LocalPosition;
        public Quat LocalRotation = Quat.Identity;
        public Vec3 LocalScale = Vec3.One;
    }

    /// <summary>
    /// Lists the distinct avatars (by root GameObject name) a package contains, ordered with the
    /// recommended primary first. Engine-independent, so the GUI can prompt for a choice without
    /// booting the engine. Empty when the package has no VRChat avatar.
    /// </summary>
    public static IReadOnlyList<VrchatAvatarChoice> ListAvatars(UnityPackage package)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<VrchatAvatarChoice>();
        foreach (Candidate c in OrderByPrimary(FindCandidates(package)))
        {
            if (!string.IsNullOrEmpty(c.Name) && seen.Add(c.Name))
            {
                result.Add(new VrchatAvatarChoice(
                    c.Name, c.Source.LogicalPath, c.Size, c.HasOwnDescriptor,
                    c.IsVariantOfFbx, c.IsComposedPrefab));
            }
        }
        AddComposedPrefabChoices(package, result, seen);
        return result
            .OrderBy(choice => choice.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Parses the primary avatar. <paramref name="avatarOverride"/> selects a specific avatar by
    /// root/file name (exact match preferred, otherwise substring) when the package holds more than one.
    /// </summary>
    public static VrchatAvatar Parse(UnityPackage package, string avatarOverride = null)
    {
        List<Candidate> candidates = FindCandidates(package);
        AddRequestedComposedCandidate(package, candidates, avatarOverride);
        if (candidates.Count == 0)
        {
            throw new InvalidDataException(
                "VRCAvatarDescriptor を持つアバターが見つかりませんでした。VRChatアバターのUnityPackageか確認してください。" +
                "（prefab/シーンを走査しましたが、VRCAvatarDescriptorコンポーネントを検出できませんでした）");
        }

        Candidate selected = SelectPrimary(candidates, avatarOverride);
        foreach (Candidate c in OrderByPrimary(candidates))
        {
            string mark = c == selected ? "=> 選択" : "   スキップ";
            string kind = c.IsVariantOfFbx ? ", FBX variant" : $", GameObject {c.Size}";
            UniLog.Log($"VRChatアバター候補 {mark}: {c.Name} ({Path.GetFileName(c.Source.LogicalPath)}{kind})");
        }

        var avatar = new VrchatAvatar
        {
            Name = selected.Name ?? Path.GetFileNameWithoutExtension(selected.Source.LogicalPath),
            PrefabPath = selected.Source.LogicalPath,
        };

        if (selected.IsVariantOfFbx)
        {
            // Geometry/skeleton come from the FBX the variant sources; the prefab only adds the
            // descriptor. Materials / PhysBones / deletions can't be resolved from the stripped
            // references here, so the avatar imports with rig + visemes + view (bare materials).
            UniLog.Log($"FBXモデルのPrefab Variantとして処理します（基礎マテリアル対応、揺れもの等は制限あり）: {selected.Name}");
            string primaryFbxGuid = SelectHumanoidFbxGuid(package, selected.FbxGuidOverrides);
            UnityAsset fbx = package.ByGuid(primaryFbxGuid);
            if (fbx?.HasContent != true)
            {
                throw new InvalidDataException($"variantが参照するFBX (guid {primaryFbxGuid}) がパッケージに含まれていません。");
            }
            avatar.FbxGuid = primaryFbxGuid;
            avatar.FbxPath = fbx.DiskPath;
            selected.FbxPlacements ??= CollectFbxPlacements(package, selected.Source.Guid);
            FbxPlacement primaryPlacement = null;
            selected.FbxPlacements?.TryGetValue(primaryFbxGuid, out primaryPlacement);
            ApplyPrimaryFbxPlacement(avatar, fbx, primaryPlacement);
            ParseHumanoid(package, avatar);
            foreach (string guid in selected.FbxGuidOverrides.Where(g =>
                         !string.Equals(g, primaryFbxGuid, StringComparison.OrdinalIgnoreCase)))
            {
                FbxPlacement placement = null;
                selected.FbxPlacements?.TryGetValue(guid, out placement);
                AddAdditionalFbx(package, avatar, guid, placement);
            }
            ParseFbxBlendShapeNames(package, avatar);
            ParseDescriptor(package, selected.Scene, selected.Descriptor, avatar);
            ParseVariantRendererOverrides(
                package, selected.Source.Guid, selected.Scene, selected.Descriptor,
                selected.HasOwnDescriptor, avatar);
            ApplyFbxDefaultBlendShapeWeights(avatar);
            return avatar;
        }

        // Record every GameObject the prefab keeps, so the importer's extra (deleted) meshes can be dropped.
        foreach (long goId in selected.Subtree)
        {
            string name = selected.Scene.GameObjectName(goId);
            if (!string.IsNullOrEmpty(name))
            {
                avatar.PrefabGameObjectNames.Add(name);
            }
        }

        ResolveFbx(package, selected.Scene, selected.Subtree, avatar);
        ParseFbxBlendShapeNames(package, avatar);
        ParseHumanoid(package, avatar);
        ParseDescriptor(package, selected.Scene, selected.Descriptor, avatar);
        ParsePhysBones(selected.Scene, selected.Subtree, avatar);
        ParseRendererMaterials(selected.Scene, selected.Subtree, avatar);
        ParseInactiveGameObjects(selected.Scene, selected.Subtree, avatar);
        ApplyFbxDefaultBlendShapeWeights(avatar);
        return avatar;
    }

    private static void ParseFbxBlendShapeNames(UnityPackage package, VrchatAvatar avatar)
    {
        IEnumerable<string> guids = new[] { avatar.FbxGuid }
            .Concat(avatar.AdditionalFbxs.Select(fbx => fbx.Guid));
        foreach (string guid in guids.Where(g => !string.IsNullOrEmpty(g)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            UnityAsset asset = package.ByGuid(guid);
            if (asset?.Extension != ".fbx")
            {
                continue;
            }
            var resolver = new UnityModelFileIdResolver(asset);
            foreach ((string rendererName, IReadOnlyList<string> names) in resolver.BlendShapeNames)
            {
                avatar.FbxBlendShapeNames.TryAdd(rendererName, names);
            }
            foreach ((string rendererName, IReadOnlyList<float> weights) in
                     resolver.BlendShapeDefaultWeights)
            {
                avatar.FbxBlendShapeDefaultWeights.TryAdd(rendererName, weights);
            }
        }
        if (avatar.FbxBlendShapeNames.Count > 0)
        {
            UniLog.Log($"FBX blendshape order captured for {avatar.FbxBlendShapeNames.Count} renderer(s).");
        }
    }

    private static void ApplyFbxDefaultBlendShapeWeights(VrchatAvatar avatar)
    {
        foreach ((string rendererName, IReadOnlyList<float> weights) in
                 avatar.FbxBlendShapeDefaultWeights)
        {
            VrchatRendererMaterials renderer = avatar.RendererMaterials.FirstOrDefault(candidate =>
                string.Equals(candidate.RendererGameObjectName, rendererName,
                    StringComparison.Ordinal));
            if (renderer == null)
            {
                renderer = new VrchatRendererMaterials
                {
                    RendererGameObjectName = rendererName,
                };
                avatar.RendererMaterials.Add(renderer);
            }
            for (int index = 0; index < weights.Count; index++)
            {
                float weight = weights[index];
                if (MathF.Abs(weight) <= 0.001f ||
                    renderer.InitialBlendShapes.Any(entry => entry.Index == index))
                {
                    continue;
                }
                renderer.InitialBlendShapes.Add((index, weight));
            }
        }
    }

    private static void ParseInactiveGameObjects(UnityScene scene, HashSet<long> subtree, VrchatAvatar avatar)
    {
        foreach (YamlDocument go in scene.GameObjects)
        {
            if (!subtree.Contains(go.FileId))
            {
                continue;
            }
            if ((go.Root?["m_IsActive"]?.AsInt(1) ?? 1) == 0)
            {
                string name = go.Root?["m_Name"]?.AsString();
                if (!string.IsNullOrEmpty(name))
                {
                    avatar.InactiveGameObjects.Add(new VrchatGameObjectReference(
                        avatar.FbxGuid, name));
                }
            }
        }
        if (avatar.InactiveGameObjects.Count > 0)
        {
            UniLog.Log($"非アクティブGameObjectを {avatar.InactiveGameObjects.Count} 個検出しました。");
        }
    }

    // ---------------------------------------------------------------- candidate discovery

    private static List<Candidate> FindCandidates(UnityPackage package)
    {
        var candidates = new List<Candidate>();
        int scanned = 0;
        // VRChat avatars normally ship as a .prefab, but some packages place the avatar in a .unity
        // scene; scan both. The descriptor may sit on the file root, on a nested GameObject, or be
        // inline in a prefab variant — find it by component, not by requiring a root match. It is
        // detected by script GUID or, GUID-independently, by its characteristic field signature.
        foreach (UnityAsset source in package.ByExtension(".prefab").Concat(package.ByExtension(".unity")))
        {
            string text = package.ReadText(source);
            if (text == null ||
                !(text.Contains(VrchatConstants.AvatarDescriptorScriptGuid, StringComparison.Ordinal) ||
                  text.Contains("baseAnimationLayers", StringComparison.Ordinal)))
            {
                continue;
            }
            scanned++;
            UnityScene scene;
            try
            {
                scene = package.ReadScene(source);
            }
            catch (Exception ex)
            {
                UniLog.Warning($"ファイルの解析に失敗しました ({source.LogicalPath}): {ex.Message}");
                continue;
            }
            foreach (YamlDocument descriptor in scene.MonoBehaviours.Where(IsAvatarDescriptor))
            {
                YamlDocument root = scene.OwnerGameObject(descriptor);
                string rootName = root != null ? scene.GameObjectName(root.FileId) : null;
                if (root != null && !string.IsNullOrEmpty(rootName))
                {
                    // Regular avatar: the descriptor's GameObject is a real, named root in this file.
                    candidates.Add(new Candidate
                    {
                        Source = source,
                        Scene = scene,
                        Root = root,
                        Descriptor = descriptor,
                        Subtree = scene.SubtreeGameObjectIds(root.FileId),
                        Name = rootName,
                        HasOwnDescriptor = true,
                    });
                    continue;
                }

                // Variant-of-FBX: the descriptor is an added component on a stripped root that
                // sources an FBX model. The geometry/skeleton come from that FBX, not this file.
                List<string> fbxGuids = ResolveVariantFbxGuids(package, scene, descriptor);
                if (fbxGuids.Count > 0)
                {
                    candidates.Add(new Candidate
                    {
                        Source = source,
                        Scene = scene,
                        Descriptor = descriptor,
                        Subtree = new HashSet<long>(),
                        FbxGuidOverrides = fbxGuids,
                        Name = VariantAvatarName(scene, descriptor, source),
                        HasOwnDescriptor = true,
                    });
                }
            }
        }
        if (candidates.Count == 0 && scanned > 0)
        {
            UniLog.Warning($"VRCAvatarDescriptorらしきデータは {scanned} 件のファイルに含まれていましたが、" +
                           "コンポーネントとして解決できませんでした（SDKバージョン差異やprefab構造の可能性）。");
            DiagnoseCandidates(package);
        }
        AddInheritedVariantCandidates(package, candidates);
        return candidates;
    }

    /// <summary>
    /// Adds pure prefab variants that inherit a descriptor from one source prefab. Prefabs that
    /// compose multiple independent prefab instances are intentionally excluded: recursively
    /// promoting every composition is expensive and can produce many ambiguous avatar candidates.
    /// </summary>
    private static void AddInheritedVariantCandidates(UnityPackage package, List<Candidate> candidates)
    {
        var existingSources = new HashSet<string>(
            candidates.Select(c => c.Source.Guid), StringComparer.OrdinalIgnoreCase);
        foreach (UnityAsset source in package.ByExtension(".prefab"))
        {
            if (existingSources.Contains(source.Guid))
            {
                continue;
            }
            UnityScene sourceScene;
            try
            {
                sourceScene = package.ReadScene(source);
            }
            catch
            {
                continue;
            }
            List<YamlDocument> sourceInstances = sourceScene.Documents.Values
                .Where(d => d.ClassId == ClassPrefabInstance)
                .ToList();
            if (sourceInstances.Count != 1)
            {
                continue;
            }
            Candidate descriptorSource = FindNestedDescriptorCandidate(package, source.Guid,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (descriptorSource == null)
            {
                continue;
            }
            var fbxGuids = new List<string>();
            CollectFbxGuidsFromSource(package, source.Guid, 0, fbxGuids,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            int inheritedFbxCount = descriptorSource.FbxGuidOverrides?.Count ?? 0;
            if (fbxGuids.Count == 0 || fbxGuids.Count != inheritedFbxCount ||
                HasRemovedGameObjects(package, source))
            {
                continue;
            }
            candidates.Add(new Candidate
            {
                Source = source,
                Scene = descriptorSource.Scene,
                Descriptor = descriptorSource.Descriptor,
                Subtree = new HashSet<long>(),
                FbxGuidOverrides = fbxGuids,
                Name = Path.GetFileNameWithoutExtension(source.LogicalPath),
                HasOwnDescriptor = false,
            });
        }
    }

    /// <summary>
    /// Adds descriptor-bearing outer prefab compositions to the GUI list without resolving FBX
    /// placements, renderer overrides or model metadata. Those details are deferred until selection.
    /// </summary>
    private static void AddComposedPrefabChoices(
        UnityPackage package, List<VrchatAvatarChoice> result, HashSet<string> seenNames)
    {
        var existingSources = new HashSet<string>(
            result.Select(choice => choice.SourcePath), StringComparer.OrdinalIgnoreCase);
        foreach (UnityAsset source in package.ByExtension(".prefab"))
        {
            string name = Path.GetFileNameWithoutExtension(source.LogicalPath);
            if (existingSources.Contains(source.LogicalPath) || seenNames.Contains(name))
            {
                continue;
            }
            Candidate candidate = TryCreateComposedCandidate(package, source);
            if (candidate == null || !seenNames.Add(name))
            {
                continue;
            }
            result.Add(new VrchatAvatarChoice(
                name, source.LogicalPath, candidate.Size, HasOwnDescriptor: false,
                IsPrefabVariant: true, IsComposedPrefab: true));
        }
    }

    /// <summary>
    /// Promotes only the requested outer composition to a full conversion candidate. This keeps
    /// normal conversion from resolving every color/FaceEmo composition in the package.
    /// </summary>
    private static void AddRequestedComposedCandidate(
        UnityPackage package, List<Candidate> candidates, string avatarOverride)
    {
        if (string.IsNullOrWhiteSpace(avatarOverride) ||
            candidates.Any(candidate =>
                string.Equals(candidate.Name, avatarOverride, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        IEnumerable<UnityAsset> sources = package.ByExtension(".prefab")
            .Where(source => candidates.All(candidate =>
                !string.Equals(candidate.Source.Guid, source.Guid, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(source => string.Equals(
                Path.GetFileNameWithoutExtension(source.LogicalPath), avatarOverride,
                StringComparison.OrdinalIgnoreCase))
            .ThenBy(source => source.LogicalPath.Length);
        foreach (UnityAsset source in sources)
        {
            string name = Path.GetFileNameWithoutExtension(source.LogicalPath);
            if (!string.Equals(name, avatarOverride, StringComparison.OrdinalIgnoreCase) &&
                !name.Contains(avatarOverride, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Candidate candidate = TryCreateComposedCandidate(package, source);
            if (candidate != null)
            {
                candidates.Add(candidate);
                return;
            }
        }
    }

    private static Candidate TryCreateComposedCandidate(UnityPackage package, UnityAsset source)
    {
        UnityScene sourceScene;
        try
        {
            sourceScene = package.ReadScene(source);
        }
        catch
        {
            return null;
        }
        List<YamlDocument> sourceInstances = sourceScene.Documents.Values
            .Where(document => document.ClassId == ClassPrefabInstance)
            .ToList();
        if (sourceInstances.Count < 2)
        {
            return null;
        }
        Candidate descriptorSource = FindNestedDescriptorCandidate(
            package, source.Guid, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (descriptorSource == null)
        {
            return null;
        }
        var fbxGuids = new List<string>();
        CollectFbxGuidsFromSource(package, source.Guid, 0, fbxGuids,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (fbxGuids.Count == 0)
        {
            return null;
        }
        return new Candidate
        {
            Source = source,
            Scene = descriptorSource.Scene,
            Descriptor = descriptorSource.Descriptor,
            Subtree = new HashSet<long>(),
            FbxGuidOverrides = fbxGuids,
            Name = Path.GetFileNameWithoutExtension(source.LogicalPath),
            HasOwnDescriptor = false,
            IsComposedPrefab = true,
        };
    }

    private static bool HasRemovedGameObjects(UnityPackage package, UnityAsset source)
    {
        string text = package.ReadText(source);
        if (text == null)
        {
            return false;
        }
        try
        {
            UnityScene scene = package.ReadScene(source);
            return scene.Documents.Values
                .Where(d => d.ClassId == ClassPrefabInstance)
                .Any(instance => instance.Root?["m_Modification"]?["m_RemovedGameObjects"]?.Seq?.Count > 0);
        }
        catch
        {
            return false;
        }
    }

    private static Candidate FindNestedDescriptorCandidate(UnityPackage package, string guid, HashSet<string> visited)
    {
        if (string.IsNullOrEmpty(guid) || !visited.Add(guid))
        {
            return null;
        }
        UnityAsset asset = package.ByGuid(guid);
        if (asset?.Extension != ".prefab")
        {
            return null;
        }
        string text = package.ReadText(asset);
        if (text == null)
        {
            return null;
        }
        UnityScene scene;
        try
        {
            scene = package.ReadScene(asset);
        }
        catch
        {
            return null;
        }
        YamlDocument descriptor = scene.MonoBehaviours.FirstOrDefault(IsAvatarDescriptor);
        if (descriptor != null)
        {
            List<string> fbxGuids = ResolveVariantFbxGuids(package, scene, descriptor);
            return fbxGuids.Count > 0
                ? new Candidate { Source = asset, Scene = scene, Descriptor = descriptor, FbxGuidOverrides = fbxGuids }
                : null;
        }
        foreach (YamlDocument instance in scene.Documents.Values.Where(d => d.ClassId == ClassPrefabInstance))
        {
            Candidate nested = FindNestedDescriptorCandidate(package, instance.Root?["m_SourcePrefab"]?.Guid, visited);
            if (nested != null)
            {
                return nested;
            }
        }
        return null;
    }

    private const int ClassPrefabInstance = 1001;

    /// <summary>
    /// For a descriptor added in a prefab variant, collects every FBX composed by the source prefab,
    /// recursing through nested prefab variants.
    /// </summary>
    private static List<string> ResolveVariantFbxGuids(UnityPackage package, UnityScene scene, YamlDocument descriptor)
    {
        YamlDocument prefabInstance = VariantPrefabInstance(scene, descriptor);
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectFbxGuidsFromSource(package, prefabInstance?.Root?["m_SourcePrefab"]?.Guid, 0, result, visited);
        foreach (YamlDocument instance in scene.Documents.Values.Where(d => d.ClassId == ClassPrefabInstance))
        {
            CollectFbxGuidsFromSource(package, instance.Root?["m_SourcePrefab"]?.Guid, 0, result, visited);
        }
        return result;
    }

    /// <summary>
    /// Resolves the PrefabInstance behind a descriptor on a stripped variant root. Added components
    /// can have m_PrefabInstance=0 while their stripped owner GameObject carries the reference.
    /// </summary>
    private static YamlDocument VariantPrefabInstance(UnityScene scene, YamlDocument descriptor)
    {
        long instanceId = descriptor.Root?["m_PrefabInstance"]?.FileID ?? 0;
        if (instanceId == 0)
        {
            YamlDocument owner = scene.OwnerGameObject(descriptor);
            instanceId = owner?.Root?["m_PrefabInstance"]?.FileID ?? 0;
        }
        return scene.Doc(instanceId);
    }

    private static void CollectFbxGuidsFromSource(UnityPackage package, string guid, int depth,
        List<string> result, HashSet<string> visited)
    {
        if (string.IsNullOrEmpty(guid) || depth > 8 || !visited.Add(guid))
        {
            return;
        }
        UnityAsset asset = package.ByGuid(guid);
        if (asset == null)
        {
            return;
        }
        if (asset.Extension == ".fbx")
        {
            result.Add(guid);
            return;
        }
        if (asset.Extension != ".prefab")
        {
            return;
        }
        // A composed avatar prefab can reference several nested prefab/FBX instances.
        string text = package.ReadText(asset);
        if (text == null)
        {
            return;
        }
        UnityScene scene;
        try
        {
            scene = package.ReadScene(asset);
        }
        catch
        {
            return;
        }
        foreach (YamlDocument prefabInstance in scene.Documents.Values.Where(d => d.ClassId == ClassPrefabInstance))
        {
            CollectFbxGuidsFromSource(package, prefabInstance.Root?["m_SourcePrefab"]?.Guid, depth + 1,
                result, visited);
        }
    }

    private static Dictionary<string, FbxPlacement> CollectFbxPlacements(UnityPackage package, string sourceGuid)
    {
        var result = new Dictionary<string, FbxPlacement>(StringComparer.OrdinalIgnoreCase);
        CollectFbxPlacements(package, sourceGuid, null, result,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
        return result;
    }

    private static void CollectFbxPlacements(UnityPackage package, string guid, FbxPlacement inherited,
        Dictionary<string, FbxPlacement> result, HashSet<string> visited, int depth)
    {
        if (string.IsNullOrEmpty(guid) || depth > 8 || !visited.Add(guid))
        {
            return;
        }
        UnityAsset asset = package.ByGuid(guid);
        if (asset?.Extension == ".fbx")
        {
            result.TryAdd(guid, inherited ?? new FbxPlacement
            {
                InstanceName = Path.GetFileNameWithoutExtension(asset.LogicalPath),
            });
            return;
        }
        if (asset?.Extension != ".prefab")
        {
            return;
        }
        string text = package.ReadText(asset);
        if (text == null)
        {
            return;
        }
        UnityScene scene;
        try
        {
            scene = package.ReadScene(asset);
        }
        catch
        {
            return;
        }
        foreach (YamlDocument instance in scene.Documents.Values.Where(d => d.ClassId == ClassPrefabInstance))
        {
            string childGuid = instance.Root?["m_SourcePrefab"]?.Guid;
            long parentId = instance.Root?["m_Modification"]?["m_TransformParent"]?.FileID ?? 0;
            FbxPlacement placement = parentId == 0 && inherited != null
                ? inherited
                : PlacementFromInstance(package, scene, instance);
            CollectFbxPlacements(package, childGuid, placement, result, visited, depth + 1);
        }
    }

    private static FbxPlacement PlacementFromInstance(UnityPackage package, UnityScene scene, YamlDocument instance)
    {
        long parentId = instance.Root?["m_Modification"]?["m_TransformParent"]?.FileID ?? 0;
        (string parentGuid, string parentName) = ResolveReferenceNode(package, scene, parentId, 0);
        string instanceName = FindLastModificationValue(instance, "m_Name");
        YamlNode transformTarget = FindPlacementTransformTarget(package, instance);
        (_, string transformNodeName) = ResolveAssetReferenceNode(
            package, transformTarget?.Guid, transformTarget?.FileID ?? 0, 0);
        if (transformTarget?.FileID == UnityModelRootTransformFileId ||
            transformNodeName == "//RootNode")
        {
            transformNodeName = "RootNode";
        }
        if (transformNodeName == null && instanceName?.StartsWith("Siro_", StringComparison.Ordinal) == true)
        {
            transformNodeName = instanceName["Siro_".Length..];
        }
        return new FbxPlacement
        {
            InstanceName = instanceName,
            ParentFbxGuid = parentGuid,
            ParentNodeName = parentName,
            TransformNodeName = transformNodeName,
            LocalPosition = new Vec3(
                FindModificationFloat(instance, transformTarget, "m_LocalPosition.x"),
                FindModificationFloat(instance, transformTarget, "m_LocalPosition.y"),
                FindModificationFloat(instance, transformTarget, "m_LocalPosition.z")),
            LocalRotation = new Quat(
                FindModificationFloat(instance, transformTarget, "m_LocalRotation.x"),
                FindModificationFloat(instance, transformTarget, "m_LocalRotation.y"),
                FindModificationFloat(instance, transformTarget, "m_LocalRotation.z"),
                FindModificationFloat(instance, transformTarget, "m_LocalRotation.w", 1f)),
            LocalScale = new Vec3(
                FindModificationFloat(instance, transformTarget, "m_LocalScale.x", 1f),
                FindModificationFloat(instance, transformTarget, "m_LocalScale.y", 1f),
                FindModificationFloat(instance, transformTarget, "m_LocalScale.z", 1f)),
        };
    }

    private static YamlNode FindPlacementTransformTarget(UnityPackage package, YamlDocument instance)
    {
        YamlNode modifications = instance.Root?["m_Modification"]?["m_Modifications"];
        if (modifications?.Seq == null)
        {
            return null;
        }

        var groups = new Dictionary<(string Guid, long FileId), List<YamlNode>>();
        var order = new List<(string Guid, long FileId)>();
        var modelResolvers = new Dictionary<string, UnityModelFileIdResolver>(
            StringComparer.OrdinalIgnoreCase);
        foreach (YamlNode entry in modifications.Seq)
        {
            string propertyPath = entry?["propertyPath"]?.AsString();
            if (propertyPath?.StartsWith("m_LocalPosition.", StringComparison.Ordinal) != true &&
                propertyPath?.StartsWith("m_LocalRotation.", StringComparison.Ordinal) != true &&
                propertyPath?.StartsWith("m_LocalScale.", StringComparison.Ordinal) != true)
            {
                continue;
            }
            YamlNode target = entry["target"];
            (string Guid, long FileId) key = (target?.Guid, target?.FileID ?? 0);
            if (string.IsNullOrEmpty(key.Guid) || key.FileId == 0)
            {
                continue;
            }
            if (!groups.TryGetValue(key, out List<YamlNode> entries))
            {
                entries = new List<YamlNode>();
                groups.Add(key, entries);
                order.Add(key);
            }
            entries.Add(entry);
        }

        (string Guid, long FileId) best = default;
        int bestScore = int.MinValue;
        foreach ((string Guid, long FileId) key in order)
        {
            List<YamlNode> entries = groups[key];
            var paths = new HashSet<string>(
                entries.Select(entry => entry?["propertyPath"]?.AsString()),
                StringComparer.Ordinal);
            string name = null;
            UnityAsset asset = package.ByGuid(key.Guid);
            if (asset?.Extension == ".fbx")
            {
                if (!modelResolvers.TryGetValue(key.Guid, out UnityModelFileIdResolver resolver))
                {
                    resolver = new UnityModelFileIdResolver(asset);
                    modelResolvers.Add(key.Guid, resolver);
                }
                name = resolver.ResolveName(key.FileId);
            }
            else
            {
                (_, name) = ResolveAssetReferenceNode(package, key.Guid, key.FileId, 0);
            }
            int score = key.FileId == UnityModelRootTransformFileId
                ? 200
                : IsUnityRootNodeName(name) ? 100 : 0;
            score += CountPresent(paths, "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z") * 10;
            score += CountPresent(paths, "m_LocalRotation.x", "m_LocalRotation.y",
                "m_LocalRotation.z", "m_LocalRotation.w") * 10;
            score += CountPresent(paths, "m_LocalScale.x", "m_LocalScale.y", "m_LocalScale.z");
            if (score > bestScore)
            {
                best = key;
                bestScore = score;
            }
        }

        return bestScore == int.MinValue
            ? null
            : groups[best][0]?["target"];
    }

    private static int CountPresent(HashSet<string> paths, params string[] propertyPaths)
        => propertyPaths.Count(paths.Contains);

    private static bool IsUnityRootNodeName(string name)
        => string.Equals(name, "RootNode", StringComparison.Ordinal) ||
           string.Equals(name, "//RootNode", StringComparison.Ordinal);

    private static float FindModificationFloat(YamlDocument instance, YamlNode target,
        string propertyPath, float fallback = 0f)
    {
        if (target == null)
        {
            return fallback;
        }
        string result = null;
        YamlNode modifications = instance.Root?["m_Modification"]?["m_Modifications"];
        if (modifications?.Seq == null)
        {
            return fallback;
        }
        foreach (YamlNode entry in modifications.Seq)
        {
            YamlNode candidate = entry?["target"];
            if (entry?["propertyPath"]?.AsString() == propertyPath &&
                candidate?.FileID == target.FileID &&
                string.Equals(candidate.Guid, target.Guid, StringComparison.OrdinalIgnoreCase))
            {
                result = entry["value"]?.AsString();
            }
        }
        return result != null &&
               float.TryParse(result, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out float value)
            ? value
            : fallback;
    }

    private static (string Guid, string Name) ResolveAssetReferenceNode(UnityPackage package,
        string guid, long fileId, int depth)
    {
        if (string.IsNullOrEmpty(guid) || fileId == 0 || depth > 8)
        {
            return default;
        }
        UnityAsset asset = package.ByGuid(guid);
        if (asset?.Extension == ".fbx")
        {
            return (guid, ResolveFbxNodeName(asset, fileId));
        }
        if (asset?.Extension == ".prefab")
        {
            string text = package.ReadText(asset);
            if (text != null)
            {
                return ResolveReferenceNode(package, package.ReadScene(asset), fileId, depth + 1);
            }
        }
        return default;
    }

    private static (string Guid, string Name) ResolveReferenceNode(UnityPackage package, UnityScene scene,
        long fileId, int depth)
    {
        if (fileId == 0 || depth > 8)
        {
            return default;
        }
        YamlDocument doc = scene.Doc(fileId);
        if (doc == null)
        {
            return default;
        }
        YamlNode source = doc.Root?["m_CorrespondingSourceObject"];
        string sourceGuid = source?.Guid;
        long sourceId = source?.FileID ?? 0;
        if (!string.IsNullOrEmpty(sourceGuid))
        {
            UnityAsset asset = package.ByGuid(sourceGuid);
            if (asset?.Extension == ".fbx")
            {
                return (sourceGuid, ResolveFbxNodeName(asset, sourceId));
            }
            if (asset?.Extension == ".prefab")
            {
                string text = package.ReadText(asset);
                if (text != null)
                {
                    return ResolveReferenceNode(package, package.ReadScene(asset), sourceId, depth + 1);
                }
            }
        }
        return (null, scene.ResolveGameObjectName(fileId));
    }

    private static string ResolveFbxNodeName(UnityAsset fbx, long fileId)
    {
        string resolved = new UnityModelFileIdResolver(fbx).ResolveName(fileId);
        if (!string.IsNullOrEmpty(resolved))
        {
            return resolved;
        }
        if (fbx?.MetaPath == null || !File.Exists(fbx.MetaPath))
        {
            return null;
        }
        YamlNode importer = UnityYaml.ParseFlatDocument(File.ReadAllText(fbx.MetaPath))?["ModelImporter"];
        YamlNode table = importer?["internalIDToNameTable"];
        if (table?.Seq != null)
        {
            foreach (YamlNode entry in table.Seq)
            {
                if (entry?["first"]?.Map?.Values.Any(v => v.AsLong() == fileId) == true)
                {
                    return entry["second"]?.AsString();
                }
            }
        }

        YamlNode recycleNames = importer?["fileIDToRecycleName"];
        if (recycleNames?.Map == null)
        {
            return null;
        }
        string key = fileId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (recycleNames.Map.TryGetValue(key, out YamlNode name))
        {
            return name.AsString();
        }
        return null;
    }

    /// <summary>The display name of a variant-of-FBX avatar: the PrefabInstance's m_Name override, else the file name.</summary>
    private static string VariantAvatarName(UnityScene scene, YamlDocument descriptor, UnityAsset source)
    {
        YamlDocument prefabInstance = VariantPrefabInstance(scene, descriptor);
        string name = prefabInstance != null ? FindModificationValue(prefabInstance, "m_Name") : null;
        return !string.IsNullOrEmpty(name) ? name : Path.GetFileNameWithoutExtension(source.LogicalPath);
    }

    /// <summary>
    /// Logs, per descriptor-bearing file, why a candidate was or wasn't resolved, so prefab-variant
    /// / stripped-object structures can be identified from a user's log without the package. Called
    /// automatically on failure and exposed for the --vrchat-dump diagnostic.
    /// </summary>
    public static void DiagnoseCandidates(UnityPackage package)
    {
        const int classPrefabInstance = 1001;
        foreach (UnityAsset source in package.ByExtension(".prefab").Concat(package.ByExtension(".unity")))
        {
            string text = package.ReadText(source);
            if (text == null ||
                !(text.Contains(VrchatConstants.AvatarDescriptorScriptGuid, StringComparison.Ordinal) ||
                  text.Contains("baseAnimationLayers", StringComparison.Ordinal)))
            {
                continue;
            }
            UnityScene scene;
            try
            {
                scene = package.ReadScene(source);
            }
            catch
            {
                UniLog.Warning($"  診断 {Path.GetFileName(source.LogicalPath)}: 解析失敗");
                continue;
            }

            int byGuid = 0, bySignature = 0, strippedOwner = 0;
            foreach (YamlDocument mono in scene.MonoBehaviours)
            {
                if (mono.Root?["m_Script"]?.Guid == VrchatConstants.AvatarDescriptorScriptGuid)
                {
                    byGuid++;
                }
                else if (IsAvatarDescriptor(mono))
                {
                    bySignature++;
                }
                else
                {
                    continue;
                }
                YamlDocument owner = scene.OwnerGameObject(mono);
                if (owner == null || string.IsNullOrEmpty(scene.GameObjectName(owner.FileId)))
                {
                    strippedOwner++;
                }
            }

            var bases = new List<string>();
            foreach (YamlDocument doc in scene.Documents.Values.Where(d => d.ClassId == classPrefabInstance))
            {
                string baseGuid = doc.Root?["m_SourcePrefab"]?.Guid;
                UnityAsset baseAsset = package.ByGuid(baseGuid);
                string baseExt = baseAsset?.Extension ?? "";
                string nameOverride = FindModificationValue(doc, "m_Name");
                YamlNode modification = doc.Root?["m_Modification"];
                int added = modification?["m_AddedComponents"]?.Seq?.Count ?? 0;
                int removedGo = modification?["m_RemovedGameObjects"]?.Seq?.Count ?? 0;
                bases.Add(baseGuid == null ? "?"
                    : $"{(baseAsset != null ? Path.GetFileName(baseAsset.LogicalPath) : baseGuid + "(パッケージ外)")}{baseExt}" +
                      $" name='{nameOverride ?? "-"}' addedComp={added} removedGO={removedGo}");
            }

            UniLog.Warning($"  診断 {Path.GetFileName(source.LogicalPath)}: descriptor(guid={byGuid}, signature={bySignature}, " +
                           $"strippedOwner={strippedOwner}), variant={bases.Count > 0}" +
                           (bases.Count > 0 ? $", source=[{string.Join(" | ", bases)}]" : ""));
        }
    }

    /// <summary>Returns the value a prefab-variant override sets for the given property path, or null.</summary>
    private static string FindModificationValue(YamlDocument prefabInstance, string propertyPath)
    {
        YamlNode modifications = prefabInstance.Root?["m_Modification"]?["m_Modifications"];
        if (modifications?.Seq == null)
        {
            return null;
        }
        foreach (YamlNode entry in modifications.Seq)
        {
            if (entry?["propertyPath"]?.AsString() == propertyPath)
            {
                return entry["value"]?.AsString();
            }
        }
        return null;
    }

    private static string FindLastModificationValue(YamlDocument prefabInstance, string propertyPath)
    {
        string result = null;
        YamlNode modifications = prefabInstance.Root?["m_Modification"]?["m_Modifications"];
        if (modifications?.Seq == null)
        {
            return null;
        }
        foreach (YamlNode entry in modifications.Seq)
        {
            if (entry?["propertyPath"]?.AsString() == propertyPath)
            {
                result = entry["value"]?.AsString();
            }
        }
        return result;
    }

    /// <summary>
    /// True if a MonoBehaviour is a VRCAvatarDescriptor — either by its known script GUID or, so that
    /// SDK-version GUID differences still work, by its characteristic serialized field signature.
    /// </summary>
    private static bool IsAvatarDescriptor(YamlDocument mono)
    {
        if (mono.Root?["m_Script"]?.Guid == VrchatConstants.AvatarDescriptorScriptGuid)
        {
            return true;
        }
        YamlNode r = mono.Root;
        return r?["ViewPosition"] != null &&
               (r["baseAnimationLayers"] != null || r["VisemeBlendShapes"] != null);
    }

    private static Candidate SelectPrimary(List<Candidate> candidates, string avatarOverride)
    {
        if (!string.IsNullOrWhiteSpace(avatarOverride))
        {
            // Exact root-name match first (so "Tolass4.5" never resolves to "Tolass4.5_BodyModel"),
            // then fall back to a substring match on the name or file.
            Candidate exact = OrderByPrimary(candidates).FirstOrDefault(c =>
                string.Equals(c.Name, avatarOverride, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact;
            }
            Candidate match = OrderByPrimary(candidates).FirstOrDefault(c =>
                (c.Name ?? "").Contains(avatarOverride, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileNameWithoutExtension(c.Source.LogicalPath).Contains(avatarOverride, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }
            UniLog.Warning($"--avatar '{avatarOverride}' に一致する候補がないため、最大のアバターを使用します。");
        }
        return OrderByPrimary(candidates).First();
    }

    /// <summary>
    /// Orders candidates with the recommended primary first: largest hierarchy, then non-"OLD"
    /// folders, then shorter paths so duplicate/legacy copies don't win ties.
    /// </summary>
    private static IEnumerable<Candidate> OrderByPrimary(IEnumerable<Candidate> candidates)
        => candidates
            .OrderByDescending(c => c.Size)
            .ThenBy(c => c.Source.LogicalPath.Contains("OLD", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(c => c.Source.LogicalPath.Length);

    /// <summary>True when a component's owning GameObject is part of the selected avatar subtree.</summary>
    private static bool InSubtree(UnityScene scene, HashSet<long> subtree, YamlDocument component)
        => subtree.Contains(component?.Root?["m_GameObject"]?.FileID ?? 0);

    // ---------------------------------------------------------------- FBX + humanoid

    private static void ResolveFbx(UnityPackage package, UnityScene scene, HashSet<long> subtree, VrchatAvatar avatar)
    {
        // The humanoid FBX is the mesh referenced by the most skinned renderers (within this avatar).
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (YamlDocument smr in scene.SkinnedMeshRenderers)
        {
            if (!InSubtree(scene, subtree, smr))
            {
                continue;
            }
            string guid = smr.Root?["m_Mesh"]?.Guid;
            if (!string.IsNullOrEmpty(guid))
            {
                counts[guid] = counts.GetValueOrDefault(guid) + 1;
            }
        }
        string fbxGuid = counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        if (fbxGuid == null)
        {
            throw new InvalidDataException("アバターのメッシュ(FBX)参照が見つかりませんでした。");
        }
        UnityAsset fbx = package.ByGuid(fbxGuid);
        if (fbx?.HasContent != true)
        {
            throw new InvalidDataException($"FBX (guid {fbxGuid}) がパッケージに含まれていません。");
        }
        avatar.FbxGuid = fbxGuid;
        avatar.FbxPath = fbx.DiskPath;
    }

    private static void ParseHumanoid(UnityPackage package, VrchatAvatar avatar)
    {
        UnityAsset fbx = package.ByGuid(avatar.FbxGuid);
        if (fbx?.MetaPath == null || !File.Exists(fbx.MetaPath))
        {
            UniLog.Warning("FBXの.metaが見つからないため、ヒューマノイドボーンを取得できません。");
            return;
        }
        YamlNode meta = UnityYaml.ParseFlatDocument(File.ReadAllText(fbx.MetaPath));
        ParseFbxImportScale(fbx, meta, avatar);
        ParseFbxMaterialMappings(package, fbx, meta, avatar.FbxMaterialGuids);
        YamlNode human = meta["ModelImporter"]?["humanDescription"]?["human"];
        if (human?.Seq == null)
        {
            UniLog.Warning("FBXの.metaにhumanDescription.humanがありません（Humanoidリグでない可能性）。");
            return;
        }
        foreach (YamlNode entry in human.Seq)
        {
            string boneName = entry?["boneName"]?.AsString();
            string humanName = entry?["humanName"]?.AsString();
            string vrmName = VrchatConstants.NormalizeHumanName(humanName);
            if (!string.IsNullOrEmpty(boneName) && vrmName != null)
            {
                avatar.HumanBones[vrmName] = boneName;
            }
        }
        UniLog.Log($"ヒューマノイドボーンを {avatar.HumanBones.Count} 個取得しました。");
    }

    private static string SelectHumanoidFbxGuid(UnityPackage package, IEnumerable<string> guids)
    {
        foreach (string guid in guids)
        {
            UnityAsset fbx = package.ByGuid(guid);
            if (fbx?.MetaPath == null || !File.Exists(fbx.MetaPath))
            {
                continue;
            }
            YamlNode meta = UnityYaml.ParseFlatDocument(File.ReadAllText(fbx.MetaPath));
            if ((meta["ModelImporter"]?["humanDescription"]?["human"]?.Seq?.Count ?? 0) > 0)
            {
                return guid;
            }
        }
        return guids.First();
    }

    private static void AddAdditionalFbx(UnityPackage package, VrchatAvatar avatar, string guid, FbxPlacement placement)
    {
        UnityAsset fbx = package.ByGuid(guid);
        if (fbx?.HasContent != true)
        {
            UniLog.Warning($"追加FBX (guid {guid}) がパッケージに含まれていないためスキップします。");
            return;
        }
        YamlNode meta = fbx.MetaPath != null && File.Exists(fbx.MetaPath)
            ? UnityYaml.ParseFlatDocument(File.ReadAllText(fbx.MetaPath))
            : null;
        var additional = new VrchatFbxAsset
        {
            Guid = guid,
            Path = fbx.DiskPath,
            ImportScale = GetFbxImportScale(fbx, meta),
            InstanceName = placement?.InstanceName ?? Path.GetFileNameWithoutExtension(fbx.LogicalPath),
            ParentFbxGuid = placement?.ParentFbxGuid,
            ParentNodeName = placement?.ParentNodeName,
            TransformNodeName = placement?.TransformNodeName,
            LocalPosition = placement?.LocalPosition ?? default,
            LocalRotation = placement?.LocalRotation ?? Quat.Identity,
            LocalScale = NormalizeImportedModelRootScale(
                fbx, meta, placement?.TransformNodeName, placement?.ParentFbxGuid,
                placement?.LocalScale ?? Vec3.One),
        };
        ParseFbxMaterialMappings(package, fbx, meta, additional.MaterialGuids);
        avatar.AdditionalFbxs.Add(additional);
        UniLog.Log($"追加FBXを検出しました: {Path.GetFileNameWithoutExtension(fbx.LogicalPath)}");
    }

    private static void ApplyPrimaryFbxPlacement(VrchatAvatar avatar, UnityAsset fbx, FbxPlacement placement)
    {
        avatar.FbxInstanceName = placement?.InstanceName ?? Path.GetFileNameWithoutExtension(fbx.LogicalPath);
        avatar.FbxParentFbxGuid = placement?.ParentFbxGuid;
        avatar.FbxParentNodeName = placement?.ParentNodeName;
        avatar.FbxTransformNodeName = placement?.TransformNodeName;
        avatar.FbxLocalPosition = placement?.LocalPosition ?? default;
        avatar.FbxLocalRotation = placement?.LocalRotation ?? Quat.Identity;
        avatar.FbxLocalScale = placement?.LocalScale ?? Vec3.One;
    }

    private static void ParseFbxImportScale(UnityAsset fbx, YamlNode meta, VrchatAvatar avatar)
    {
        avatar.FbxImportScale = GetFbxImportScale(fbx, meta);
        YamlNode meshes = meta?["ModelImporter"]?["meshes"];
        float globalScale = meshes?["globalScale"]?.AsFloat(1f) ?? 1f;
        bool useFileUnits = meshes?["useFileUnits"]?.AsBool(
            meshes?["useFileScale"]?.AsBool(true) ?? true) ?? true;
        float fileScale = useFileUnits ? FbxUnits.MetersPerUnit(fbx.DiskPath) : 1f;
        avatar.FbxLocalScale = NormalizeImportedModelRootScale(
            fbx, meta, avatar.FbxTransformNodeName, avatar.FbxParentFbxGuid, avatar.FbxLocalScale);
        avatar.FbxUpAxis = FbxUnits.UpAxisDescription(fbx.DiskPath);
        UniLog.Log($"FBX import scale: {avatar.FbxImportScale:G6} " +
                   $"(globalScale={globalScale:G6}, fileUnits={(useFileUnits ? fileScale.ToString("G6") : "off")})");
        UniLog.Log($"FBX up axis: {avatar.FbxUpAxis}");
    }

    private static float GetFbxImportScale(UnityAsset fbx, YamlNode meta)
    {
        YamlNode meshes = meta?["ModelImporter"]?["meshes"];
        float globalScale = meshes?["globalScale"]?.AsFloat(1f) ?? 1f;
        bool useFileUnits = meshes?["useFileUnits"]?.AsBool(
            meshes?["useFileScale"]?.AsBool(true) ?? true) ?? true;
        float fileScale = useFileUnits ? FbxUnits.MetersPerUnit(fbx.DiskPath) : 1f;
        return globalScale * fileScale;
    }

    private static Vec3 NormalizeImportedModelRootScale(UnityAsset fbx, YamlNode meta,
        string transformNodeName, string parentFbxGuid, Vec3 scale)
    {
        // Some meter-unit FBXs (Kipfel is the reference case) serialize Unity's generated model
        // root at 0.01 scale. Assimp has already applied the FBX unit metadata and our import Scale
        // reproduces ModelImporter's unit conversion, so applying that generated root scale again
        // makes the avatar 100x too small. Only normalize a top-level wrapper with the exact
        // uniform 0.01 signature; nested/authored transforms remain untouched.
        if (!string.IsNullOrEmpty(transformNodeName) || !string.IsNullOrEmpty(parentFbxGuid) ||
            MathF.Abs(scale.X - 0.01f) > 1e-5f ||
            MathF.Abs(scale.Y - 0.01f) > 1e-5f ||
            MathF.Abs(scale.Z - 0.01f) > 1e-5f)
        {
            return scale;
        }

        YamlNode meshes = meta?["ModelImporter"]?["meshes"];
        bool useFileUnits = meshes?["useFileUnits"]?.AsBool(
            meshes?["useFileScale"]?.AsBool(true) ?? true) ?? true;
        float metersPerUnit = useFileUnits ? FbxUnits.MetersPerUnit(fbx.DiskPath) : 1f;
        if (MathF.Abs(metersPerUnit - 1f) > 1e-4f)
        {
            return scale;
        }

        UniLog.Log($"Unity-generated 0.01 model root scaleを正規化します: {Path.GetFileName(fbx.LogicalPath)}");
        return Vec3.One;
    }

    private static void ParseFbxMaterialMappings(UnityPackage package, UnityAsset fbx, YamlNode meta,
        Dictionary<string, string> materialGuids)
    {
        YamlNode externalObjects = meta?["ModelImporter"]?["externalObjects"];
        if (externalObjects?.Seq != null)
        {
            foreach (YamlNode entry in externalObjects.Seq)
            {
                string type = entry?["first"]?["type"]?.AsString();
                string name = entry?["first"]?["name"]?.AsString();
                string guid = entry?["second"]?.Guid;
                if (type == "UnityEngine:Material" && !string.IsNullOrEmpty(name) &&
                    !string.IsNullOrEmpty(guid))
                {
                    materialGuids[name] = guid;
                }
            }
        }

        // When externalObjects is empty, Unity's ModelImporter can still resolve imported FBX
        // materials through its material search. Reproduce that deterministically from model
        // metadata: match an exact .mat filename to the embedded material name, or to its main
        // texture filename (e.g. N00...Body -> texture/tx_body.psd -> tx_body.mat).
        var matsByName = package.ByExtension(".mat")
            .GroupBy(asset => Path.GetFileNameWithoutExtension(asset.LogicalPath),
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Guid,
                StringComparer.OrdinalIgnoreCase);
        var resolver = new UnityModelFileIdResolver(fbx);
        foreach (UnityModelFileIdResolver.ModelMaterial material in resolver.Materials)
        {
            string name = NormalizeFbxMaterialName(material.Name);
            if (string.IsNullOrEmpty(name) || materialGuids.ContainsKey(name))
            {
                continue;
            }
            if (matsByName.TryGetValue(name, out string guid))
            {
                materialGuids[name] = guid;
                continue;
            }
            string textureName = Path.GetFileNameWithoutExtension(
                material.MainTexturePath?.Replace('\\', Path.DirectorySeparatorChar));
            if (!string.IsNullOrEmpty(textureName) &&
                matsByName.TryGetValue(textureName, out guid))
            {
                materialGuids[name] = guid;
            }
        }
        if (materialGuids.Count > 0)
        {
            UniLog.Log($"FBX material mappings: {materialGuids.Count}");
        }
    }

    private static string NormalizeFbxMaterialName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }
        int separator = name.IndexOf('\0');
        if (separator >= 0)
        {
            name = name[..separator];
        }
        const string instanceSuffix = " (Instance)";
        return name.EndsWith(instanceSuffix, StringComparison.Ordinal)
            ? name[..^instanceSuffix.Length]
            : name;
    }

    // ---------------------------------------------------------------- descriptor (viseme/blink/view)

    private static void ParseDescriptor(UnityPackage package, UnityScene scene, YamlDocument descriptor,
        VrchatAvatar avatar)
    {
        YamlNode d = descriptor.Root;
        var modelResolvers = new Dictionary<string, UnityModelFileIdResolver>(StringComparer.OrdinalIgnoreCase);

        YamlNode view = d?["ViewPosition"];
        if (view != null && view.IsMap)
        {
            avatar.ViewPosition = new Vec3(view.Vec("x"), view.Vec("y"), view.Vec("z"));
        }

        // Visemes: only when the descriptor uses VisemeBlendShape lip sync (lipSync == 3).
        int lipSync = d?["lipSync"]?.AsInt(-1) ?? -1;
        YamlNode visemeShapes = d?["VisemeBlendShapes"];
        // Variant descriptors often reference a local stripped renderer; follow it to the source
        // FBX GUID/fileID so the imported renderer name is preserved.
        string visemeMesh = ResolveReferenceGameObjectName(package, scene, d?["VisemeSkinnedMesh"], modelResolvers);
        if (lipSync == 3 && visemeShapes?.Seq != null)
        {
            foreach ((string preset, int idx) in VrchatConstants.VisemeToVrcSlot())
            {
                if (idx < 0 || idx >= visemeShapes.Seq.Count)
                {
                    continue;
                }
                string shape = visemeShapes.Seq[idx]?.AsString();
                if (string.IsNullOrEmpty(shape) || shape == "-none-")
                {
                    continue;
                }
                avatar.Visemes.Add(new VrchatViseme
                {
                    ResonitePreset = preset,
                    BlendShapeName = shape,
                    MeshGameObjectName = visemeMesh,
                });
            }
        }

        // Blink: customEyeLookSettings with blendshape eyelids (eyelidType == 2).
        YamlNode eye = d?["customEyeLookSettings"];
        bool eyeLook = (d?["enableEyeLook"]?.AsBool() ?? false);
        if (eyeLook && eye != null)
        {
            avatar.LeftEyeBoneName = ResolveReferenceGameObjectName(package, scene, eye["leftEye"], modelResolvers);
            avatar.RightEyeBoneName = ResolveReferenceGameObjectName(package, scene, eye["rightEye"], modelResolvers);

            int eyelidType = eye["eyelidType"]?.AsInt(0) ?? 0;
            if (eyelidType == 2)
            {
                string eyelidMesh = ResolveReferenceGameObjectName(
                    package, scene, eye["eyelidsSkinnedMesh"], modelResolvers);
                // eyelidsBlendshapes = [Blink, LookingUp, LookingDown]. Resonite drives blink via the
                // EyeLinearDriver only, so take element [0] (Blink) and deliberately ignore the
                // LookingUp/LookingDown shapes (they would otherwise be mis-wired as blink).
                int[] eyelids = VrchatConstants.DecodeIntArrayHex(eye["eyelidsBlendshapes"]?.AsString());
                int blinkIndex = eyelids.Length > 0 ? eyelids[0] : -1;
                if (eyelidMesh != null && blinkIndex >= 0)
                {
                    avatar.Blink = new VrchatBlink { MeshGameObjectName = eyelidMesh, BlendShapeIndex = blinkIndex };
                }
            }
        }
    }

    private static string ResolveReferenceGameObjectName(UnityPackage package, UnityScene scene,
        YamlNode reference, Dictionary<string, UnityModelFileIdResolver> modelResolvers)
    {
        long fileId = reference?.FileID ?? 0;
        if (fileId == 0)
        {
            return null;
        }

        string guid = reference.Guid;
        if (string.IsNullOrEmpty(guid))
        {
            YamlDocument local = scene.Doc(fileId);
            string localName = scene.ResolveGameObjectName(fileId);
            if (!string.IsNullOrEmpty(localName))
            {
                return localName;
            }
            YamlNode source = local?.Root?["m_CorrespondingSourceObject"];
            guid = source?.Guid;
            fileId = source?.FileID ?? 0;
        }
        if (string.IsNullOrEmpty(guid) || fileId == 0)
        {
            return null;
        }

        UnityAsset asset = package.ByGuid(guid);
        if (asset?.Extension == ".fbx")
        {
            if (!modelResolvers.TryGetValue(guid, out UnityModelFileIdResolver resolver))
            {
                resolver = new UnityModelFileIdResolver(asset);
                modelResolvers.Add(guid, resolver);
            }
            return resolver.ResolveName(fileId);
        }
        if (asset?.Extension == ".prefab")
        {
            string text = package.ReadText(asset);
            if (text != null)
            {
                return package.ReadScene(asset).ResolveGameObjectName(fileId);
            }
        }
        return null;
    }

    private static void ParseVariantRendererOverrides(UnityPackage package, string sourceGuid,
        UnityScene descriptorScene, YamlDocument descriptor, bool hasOwnDescriptor,
        VrchatAvatar avatar)
    {
        var modificationBlocks = new List<YamlNode>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        UnityScene selectedScene = descriptorScene;
        YamlDocument selectedInstance;
        if (hasOwnDescriptor)
        {
            selectedInstance = VariantPrefabInstance(selectedScene, descriptor);
        }
        else
        {
            UnityAsset selectedAsset = package.ByGuid(sourceGuid);
            try
            {
                selectedScene = selectedAsset?.Extension == ".prefab"
                    ? package.ReadScene(selectedAsset)
                    : null;
            }
            catch
            {
                selectedScene = null;
            }
            selectedInstance = selectedScene?.Documents.Values
                .Where(document => document.ClassId == ClassPrefabInstance)
                .SingleOrDefault();
        }
        string selectedSourceGuid = selectedInstance?.Root?["m_SourcePrefab"]?.Guid;
        if (selectedInstance != null)
        {
            CollectVariantModificationBlocks(
                package, selectedSourceGuid, modificationBlocks, visited);
            YamlNode selectedModifications =
                selectedInstance.Root?["m_Modification"]?["m_Modifications"];
            if (selectedModifications?.Seq != null)
            {
                modificationBlocks.Add(selectedModifications);
            }
        }
        else
        {
            CollectVariantModificationBlocks(package, sourceGuid, modificationBlocks, visited);
        }
        var renderers = new Dictionary<string, VrchatRendererMaterials>(StringComparer.Ordinal);
        var modelResolvers = new Dictionary<string, UnityModelFileIdResolver>(
            StringComparer.OrdinalIgnoreCase);
        var prefabScenes = new Dictionary<string, UnityScene>(StringComparer.OrdinalIgnoreCase);
        int materialAssignments = 0;
        int activeAssignments = 0;

        var inheritedScenes = new List<UnityScene>();
        CollectVariantPrefabScenes(package, selectedSourceGuid ?? sourceGuid, inheritedScenes,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        foreach (UnityScene scene in inheritedScenes)
        {
            foreach (YamlDocument smr in scene.SkinnedMeshRenderers)
            {
                string rendererName = scene.ResolveGameObjectName(smr.FileId);
                YamlNode materials = smr.Root?["m_Materials"];
                if (string.IsNullOrEmpty(rendererName) || materials?.Seq == null)
                {
                    continue;
                }
                if (!renderers.TryGetValue(rendererName, out VrchatRendererMaterials renderer))
                {
                    renderer = new VrchatRendererMaterials { RendererGameObjectName = rendererName };
                    renderers.Add(rendererName, renderer);
                }
                renderer.MaterialGuids.Clear();
                foreach (YamlNode material in materials.Seq)
                {
                    string guid = material?.Guid;
                    renderer.MaterialGuids.Add(guid);
                    if (!string.IsNullOrEmpty(guid))
                    {
                        materialAssignments++;
                    }
                }
            }
        }

        foreach (YamlNode modifications in modificationBlocks)
        {
            foreach (YamlNode modification in modifications.Seq)
            {
                string propertyPath = modification?["propertyPath"]?.AsString();
                if (string.Equals(propertyPath, "m_IsActive", StringComparison.Ordinal))
                {
                    YamlNode activeTarget = modification["target"];
                    VariantObjectReference gameObject = ResolveVariantObjectReference(
                        package, activeTarget?.Guid, activeTarget?.FileID ?? 0,
                        modelResolvers, prefabScenes);
                    if (!string.IsNullOrEmpty(gameObject.Name))
                    {
                        var reference = new VrchatGameObjectReference(
                            gameObject.FbxGuid, gameObject.Name);
                        if (modification["value"]?.AsBool(true) == false)
                        {
                            if (!avatar.InactiveGameObjects.Contains(reference))
                            {
                                avatar.InactiveGameObjects.Add(reference);
                            }
                        }
                        else
                        {
                            avatar.InactiveGameObjects.Remove(reference);
                        }
                        activeAssignments++;
                    }
                    continue;
                }
                var blendShapeMatch = propertyPath != null ? BlendShapeWeightPath.Match(propertyPath) : null;
                var materialMatch = propertyPath != null ? MaterialSlotPath.Match(propertyPath) : null;
                bool isBlendShape = blendShapeMatch?.Success == true;
                bool isMaterial = materialMatch?.Success == true;
                if (!isBlendShape && !isMaterial)
                {
                    continue;
                }
                System.Text.RegularExpressions.Match match = isBlendShape ? blendShapeMatch : materialMatch;
                if (!int.TryParse(match.Groups[1].Value, out int index))
                {
                    continue;
                }

                YamlNode target = modification["target"];
                string rendererName = ResolveVariantRendererName(
                    package, target?.Guid, target?.FileID ?? 0, modelResolvers, prefabScenes);
                if (string.IsNullOrEmpty(rendererName))
                {
                    continue;
                }

                if (!renderers.TryGetValue(rendererName, out VrchatRendererMaterials renderer))
                {
                    renderer = new VrchatRendererMaterials { RendererGameObjectName = rendererName };
                    renderers.Add(rendererName, renderer);
                }
                if (isMaterial)
                {
                    while (renderer.MaterialGuids.Count <= index)
                    {
                        renderer.MaterialGuids.Add(null);
                    }
                    renderer.MaterialGuids[index] = modification["objectReference"]?.Guid;
                    if (!string.IsNullOrEmpty(renderer.MaterialGuids[index]))
                    {
                        materialAssignments++;
                    }
                }
                else
                {
                    float weight = modification["value"]?.AsFloat(0f) ?? 0f;
                    int existing = renderer.InitialBlendShapes.FindIndex(x => x.Index == index);
                    if (existing >= 0)
                    {
                        renderer.InitialBlendShapes[existing] = (index, weight);
                    }
                    else
                    {
                        renderer.InitialBlendShapes.Add((index, weight));
                    }
                }
            }
        }

        avatar.RendererMaterials.AddRange(renderers.Values);
        if (renderers.Count > 0)
        {
            int blendShapeRenderers = renderers.Values.Count(r => r.InitialBlendShapes.Count > 0);
            UniLog.Log($"Prefab Variant renderer overrides: {materialAssignments} material assignment(s), " +
                       $"{blendShapeRenderers} renderer(s) with initial blendshape weights, " +
                       $"{activeAssignments} active-state assignment(s).");
        }
    }

    private static string ResolveVariantRendererName(UnityPackage package, string guid, long fileId,
        Dictionary<string, UnityModelFileIdResolver> modelResolvers,
        Dictionary<string, UnityScene> prefabScenes)
        => ResolveVariantObjectReference(
            package, guid, fileId, modelResolvers, prefabScenes).Name;

    private readonly record struct VariantObjectReference(string FbxGuid, string Name);

    private static VariantObjectReference ResolveVariantObjectReference(
        UnityPackage package, string guid, long fileId,
        Dictionary<string, UnityModelFileIdResolver> modelResolvers,
        Dictionary<string, UnityScene> prefabScenes)
    {
        if (string.IsNullOrEmpty(guid) || fileId == 0)
        {
            return default;
        }
        UnityAsset asset = package.ByGuid(guid);
        if (asset?.Extension == ".fbx")
        {
            if (!modelResolvers.TryGetValue(guid, out UnityModelFileIdResolver resolver))
            {
                resolver = new UnityModelFileIdResolver(asset);
                modelResolvers.Add(guid, resolver);
            }
            return new VariantObjectReference(guid, resolver.ResolveName(fileId));
        }
        if (asset?.Extension == ".prefab")
        {
            if (!prefabScenes.TryGetValue(guid, out UnityScene scene))
            {
                string text = package.ReadText(asset);
                if (text == null)
                {
                    return default;
                }
                try
                {
                    scene = package.ReadScene(asset);
                }
                catch
                {
                    return default;
                }
                prefabScenes.Add(guid, scene);
            }
            YamlDocument document = scene.Doc(fileId);
            YamlNode source = document?.Root?["m_CorrespondingSourceObject"];
            long sourceFileId = source?.FileID ?? 0;
            if (!string.IsNullOrEmpty(source?.Guid) && sourceFileId != 0)
            {
                return ResolveVariantObjectReference(
                    package, source.Guid, sourceFileId, modelResolvers, prefabScenes);
            }
            string directName = scene.ResolveGameObjectName(fileId);
            if (!string.IsNullOrEmpty(directName))
            {
                return new VariantObjectReference(null, directName);
            }

            // Unity omits most stripped documents from prefab assets. Their local fileID can still
            // be reversed to the source object's fileID because Unity derives it by XORing the
            // source ID with the owning PrefabInstance ID and clearing the sign bit.
            VariantObjectReference resolved = default;
            foreach (YamlDocument instance in scene.Documents.Values.Where(
                         candidate => candidate.ClassId == ClassPrefabInstance))
            {
                string childGuid = instance.Root?["m_SourcePrefab"]?.Guid;
                if (string.IsNullOrEmpty(childGuid))
                {
                    continue;
                }
                foreach (long childFileId in ReversePrefabInstanceFileId(fileId, instance.FileId))
                {
                    VariantObjectReference candidate = ResolveVariantObjectReference(
                        package, childGuid, childFileId, modelResolvers, prefabScenes);
                    if (string.IsNullOrEmpty(candidate.Name))
                    {
                        continue;
                    }
                    if (!string.IsNullOrEmpty(resolved.Name) && resolved != candidate)
                    {
                        return default;
                    }
                    resolved = candidate;
                }
            }
            return resolved;
        }
        return default;
    }

    private static IEnumerable<long> ReversePrefabInstanceFileId(long localFileId,
        long prefabInstanceFileId)
    {
        const ulong signBit = 1UL << 63;
        ulong sourceWithoutSign =
            (unchecked((ulong)localFileId) ^ unchecked((ulong)prefabInstanceFileId)) &
            long.MaxValue;
        yield return unchecked((long)sourceWithoutSign);
        yield return unchecked((long)(sourceWithoutSign | signBit));
    }

    private static void CollectVariantModificationBlocks(UnityPackage package, string guid,
        List<YamlNode> result, HashSet<string> visited)
    {
        if (string.IsNullOrEmpty(guid) || !visited.Add(guid))
        {
            return;
        }
        UnityAsset asset = package.ByGuid(guid);
        if (asset?.Extension != ".prefab")
        {
            return;
        }
        string text = package.ReadText(asset);
        if (text == null)
        {
            return;
        }
        UnityScene scene;
        try
        {
            scene = package.ReadScene(asset);
        }
        catch
        {
            return;
        }
        foreach (YamlDocument instance in scene.Documents.Values.Where(d => d.ClassId == ClassPrefabInstance))
        {
            CollectVariantModificationBlocks(package, instance.Root?["m_SourcePrefab"]?.Guid, result, visited);
            YamlNode modifications = instance.Root?["m_Modification"]?["m_Modifications"];
            if (modifications?.Seq != null)
            {
                result.Add(modifications);
            }
        }
    }

    private static void CollectVariantPrefabScenes(UnityPackage package, string guid,
        List<UnityScene> result, HashSet<string> visited)
    {
        if (string.IsNullOrEmpty(guid) || !visited.Add(guid))
        {
            return;
        }
        UnityAsset asset = package.ByGuid(guid);
        if (asset?.Extension != ".prefab" || package.ReadText(asset) == null)
        {
            return;
        }
        UnityScene scene;
        try
        {
            scene = package.ReadScene(asset);
        }
        catch
        {
            return;
        }
        foreach (YamlDocument instance in scene.Documents.Values.Where(
                     document => document.ClassId == ClassPrefabInstance))
        {
            CollectVariantPrefabScenes(
                package, instance.Root?["m_SourcePrefab"]?.Guid, result, visited);
        }
        result.Add(scene);
    }

    // ---------------------------------------------------------------- physbones

    private static void ParsePhysBones(UnityScene scene, HashSet<long> subtree, VrchatAvatar avatar)
    {
        foreach (YamlDocument pb in scene.MonoBehavioursByScript(
                     VrchatConstants.PhysBoneDllGuid, VrchatConstants.PhysBoneScriptFileId))
        {
            if (!InSubtree(scene, subtree, pb))
            {
                continue;
            }
            YamlNode r = pb.Root;
            long rootId = r?["rootTransform"]?.FileID ?? 0;
            string rootBone = rootId != 0
                ? scene.ResolveGameObjectName(rootId)
                : scene.ResolveGameObjectName(pb.FileId); // 0 => the component's own GameObject
            if (string.IsNullOrEmpty(rootBone))
            {
                continue;
            }

            var bone = new VrchatPhysBone
            {
                RootBoneName = rootBone,
                Pull = r?["pull"]?.AsFloat(0.2f) ?? 0.2f,
                Spring = r?["spring"]?.AsFloat(0.2f) ?? 0.2f,
                Stiffness = r?["stiffness"]?.AsFloat(0.2f) ?? 0.2f,
                Gravity = r?["gravity"]?.AsFloat(0f) ?? 0f,
                GravityFalloff = r?["gravityFalloff"]?.AsFloat(0f) ?? 0f,
                Immobile = r?["immobile"]?.AsFloat(0f) ?? 0f,
                Radius = r?["radius"]?.AsFloat(0f) ?? 0f,
            };
            YamlNode ignore = r?["ignoreTransforms"];
            if (ignore?.Seq != null)
            {
                foreach (YamlNode t in ignore.Seq)
                {
                    string name = scene.ResolveGameObjectName(t?.FileID ?? 0);
                    if (name != null)
                    {
                        bone.IgnoreBoneNames.Add(name);
                    }
                }
            }
            YamlNode colliders = r?["colliders"];
            if (colliders?.Seq != null)
            {
                foreach (YamlNode c in colliders.Seq)
                {
                    VrchatPhysBoneCollider collider = ParseCollider(scene, c?.FileID ?? 0);
                    if (collider != null)
                    {
                        bone.Colliders.Add(collider);
                    }
                }
            }
            avatar.PhysBones.Add(bone);
        }
        if (avatar.PhysBones.Count > 0)
        {
            UniLog.Log($"PhysBone を {avatar.PhysBones.Count} 個取得しました。");
        }
    }

    private static VrchatPhysBoneCollider ParseCollider(UnityScene scene, long fileId)
    {
        YamlDocument doc = scene.Doc(fileId);
        if (doc == null)
        {
            return null;
        }
        YamlNode r = doc.Root;

        // insideBounds colliders keep bones *inside* the shape (inverted collision). Resonite's
        // DynamicBoneCollider only pushes bones out, so converting these is wrong — skip them.
        if (r?["insideBounds"]?.AsBool(false) ?? false)
        {
            return null;
        }

        int shapeType = r?["shapeType"]?.AsInt(0) ?? 0;
        if (shapeType == 2) // plane: no DynamicBoneCollider equivalent.
        {
            return null;
        }
        float radius = r?["radius"]?.AsFloat(0f) ?? 0f;
        float height = r?["height"]?.AsFloat(0f) ?? 0f;
        Vec3 position = ReadVec3(r?["position"]);
        Quat rotation = ReadQuat(r?["rotation"]);

        // The shape is defined relative to rootTransform when set (the collider GameObject can live
        // outside the armature and merely follow that bone); otherwise relative to its own transform,
        // which is a child of a bone, so fold the GameObject's local transform into the bone space.
        long rootId = r?["rootTransform"]?.FileID ?? 0;
        string attachBone;
        Vec3 center;
        Quat orient;
        if (rootId != 0)
        {
            attachBone = scene.ResolveGameObjectName(rootId);
            center = position;
            orient = rotation;
        }
        else
        {
            YamlDocument ownerGo = scene.OwnerGameObject(doc);
            UnityScene.LocalTransform local = ownerGo != null ? scene.GetLocalTransform(ownerGo.FileId) : default;
            if (!local.Found)
            {
                return null;
            }
            attachBone = local.ParentName;
            center = local.Position + Vec3.Transform(position * local.Scale, local.Rotation);
            orient = local.Rotation * rotation;
        }
        if (string.IsNullOrEmpty(attachBone))
        {
            return null;
        }

        var collider = new VrchatPhysBoneCollider
        {
            AttachBoneName = attachBone,
            Radius = MathX.Max(0.001f, radius),
        };
        if (shapeType == 1 && height > radius * 2f) // capsule: endpoints along the (oriented) local Y axis.
        {
            Vec3 axis = Vec3.Transform(new Vec3(0f, height * 0.5f - radius, 0f), orient);
            collider.Offset = center - axis;
            collider.Tail = center + axis;
        }
        else
        {
            collider.Offset = center;
        }
        return collider;
    }

    private static Vec3 ReadVec3(YamlNode node)
        => node != null && node.IsMap ? new Vec3(node.Vec("x"), node.Vec("y"), node.Vec("z")) : Vec3.Zero;

    private static Quat ReadQuat(YamlNode node)
        => node != null && node.IsMap
            ? new Quat(node.Vec("x"), node.Vec("y"), node.Vec("z"), node["w"] != null ? node.Vec("w") : 1f)
            : Quat.Identity;

    // ---------------------------------------------------------------- material assignments

    private static void ParseRendererMaterials(UnityScene scene, HashSet<long> subtree, VrchatAvatar avatar)
    {
        foreach (YamlDocument smr in scene.SkinnedMeshRenderers)
        {
            if (!InSubtree(scene, subtree, smr))
            {
                continue;
            }
            string name = scene.ResolveGameObjectName(smr.FileId);
            if (name == null)
            {
                continue;
            }
            var entry = new VrchatRendererMaterials { RendererGameObjectName = name };
            YamlNode materials = smr.Root?["m_Materials"];
            if (materials?.Seq != null)
            {
                foreach (YamlNode m in materials.Seq)
                {
                    entry.MaterialGuids.Add(m?.Guid); // may be null for a missing slot
                }
            }
            // Initial (non-zero) blendshape weights authored in the prefab (Unity 0-100 scale).
            YamlNode weights = smr.Root?["m_BlendShapeWeights"];
            if (weights?.Seq != null)
            {
                for (int i = 0; i < weights.Seq.Count; i++)
                {
                    float w = weights.Seq[i]?.AsFloat(0f) ?? 0f;
                    if (MathF.Abs(w) > 0.001f)
                    {
                        entry.InitialBlendShapes.Add((i, w));
                    }
                }
            }
            avatar.RendererMaterials.Add(entry);
        }
    }
}
