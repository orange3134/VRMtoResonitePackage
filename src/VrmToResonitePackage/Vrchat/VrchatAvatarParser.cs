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
                result.Add(new VrchatAvatarChoice(c.Name, c.Source.LogicalPath, c.Size));
            }
        }
        return result;
    }

    /// <summary>
    /// Parses the primary avatar. <paramref name="avatarOverride"/> selects a specific avatar by
    /// root/file name (exact match preferred, otherwise substring) when the package holds more than one.
    /// </summary>
    public static VrchatAvatar Parse(UnityPackage package, string avatarOverride = null)
    {
        List<Candidate> candidates = FindCandidates(package);
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
            ParseDescriptor(selected.Scene, selected.Descriptor, avatar);
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
        ParseHumanoid(package, avatar);
        ParseDescriptor(selected.Scene, selected.Descriptor, avatar);
        ParsePhysBones(selected.Scene, selected.Subtree, avatar);
        ParseRendererMaterials(selected.Scene, selected.Subtree, avatar);
        ParseInactiveGameObjects(selected.Scene, selected.Subtree, avatar);
        return avatar;
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
                    avatar.InactiveGameObjectNames.Add(name);
                }
            }
        }
        if (avatar.InactiveGameObjectNames.Count > 0)
        {
            UniLog.Log($"非アクティブGameObjectを {avatar.InactiveGameObjectNames.Count} 個検出しました。");
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
                scene = UnityScene.Parse(text);
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
                        FbxPlacements = CollectFbxPlacements(package, source.Guid),
                        Name = VariantAvatarName(scene, descriptor, source),
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
        AddComposedPrefabCandidates(package, candidates);
        return candidates;
    }

    /// <summary>
    /// Adds outer prefab compositions that contain a descriptor-bearing avatar prefab plus separate
    /// hair/accessory prefabs. These files do not serialize their own descriptor, but are the actual
    /// complete avatars users expect to choose.
    /// </summary>
    private static void AddComposedPrefabCandidates(UnityPackage package, List<Candidate> candidates)
    {
        var existingSources = new HashSet<string>(
            candidates.Select(c => c.Source.Guid), StringComparer.OrdinalIgnoreCase);
        foreach (UnityAsset source in package.ByExtension(".prefab"))
        {
            if (existingSources.Contains(source.Guid))
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
            if (fbxGuids.Count <= (descriptorSource.FbxGuidOverrides?.Count ?? 0))
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
                FbxPlacements = CollectFbxPlacements(package, source.Guid),
                Name = Path.GetFileNameWithoutExtension(source.LogicalPath),
            });
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
            scene = UnityScene.Parse(text);
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
            scene = UnityScene.Parse(text);
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
            scene = UnityScene.Parse(text);
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
        string transformNodeName = FindModificationTransformNodeName(package, instance, "m_LocalPosition.x")
                                   ?? FindModificationTransformNodeName(package, instance, "m_LocalRotation.w");
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
                FindLastModificationFloat(instance, "m_LocalPosition.x"),
                FindLastModificationFloat(instance, "m_LocalPosition.y"),
                FindLastModificationFloat(instance, "m_LocalPosition.z")),
            LocalRotation = new Quat(
                FindLastModificationFloat(instance, "m_LocalRotation.x"),
                FindLastModificationFloat(instance, "m_LocalRotation.y"),
                FindLastModificationFloat(instance, "m_LocalRotation.z"),
                FindLastModificationFloat(instance, "m_LocalRotation.w", 1f)),
            LocalScale = new Vec3(
                FindLastModificationFloat(instance, "m_LocalScale.x", 1f),
                FindLastModificationFloat(instance, "m_LocalScale.y", 1f),
                FindLastModificationFloat(instance, "m_LocalScale.z", 1f)),
        };
    }

    private static string FindModificationTransformNodeName(UnityPackage package, YamlDocument instance,
        string propertyPath)
    {
        string result = null;
        YamlNode modifications = instance.Root?["m_Modification"]?["m_Modifications"];
        if (modifications?.Seq == null)
        {
            return null;
        }
        foreach (YamlNode entry in modifications.Seq)
        {
            if (entry?["propertyPath"]?.AsString() != propertyPath)
            {
                continue;
            }
            YamlNode target = entry["target"];
            (_, string name) = ResolveAssetReferenceNode(package, target?.Guid, target?.FileID ?? 0, 0);
            if (!string.IsNullOrEmpty(name))
            {
                result = name == "//RootNode" ? "RootNode" : name;
            }
        }
        return result;
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
                return ResolveReferenceNode(package, UnityScene.Parse(text), fileId, depth + 1);
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
                    return ResolveReferenceNode(package, UnityScene.Parse(text), sourceId, depth + 1);
                }
            }
        }
        return (null, scene.ResolveGameObjectName(fileId));
    }

    private static string ResolveFbxNodeName(UnityAsset fbx, long fileId)
    {
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
                scene = UnityScene.Parse(text);
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

    private static float FindLastModificationFloat(YamlDocument prefabInstance, string propertyPath, float fallback = 0f)
    {
        string value = FindLastModificationValue(prefabInstance, propertyPath);
        return value != null &&
               float.TryParse(value, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out float result)
            ? result
            : fallback;
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
        ParseFbxMaterialMappings(meta, avatar.FbxMaterialGuids);
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
            LocalScale = placement?.LocalScale ?? Vec3.One,
        };
        ParseFbxMaterialMappings(meta, additional.MaterialGuids);
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

    private static void ParseFbxMaterialMappings(YamlNode meta, Dictionary<string, string> materialGuids)
    {
        YamlNode externalObjects = meta?["ModelImporter"]?["externalObjects"];
        if (externalObjects?.Seq == null)
        {
            return;
        }
        foreach (YamlNode entry in externalObjects.Seq)
        {
            string type = entry?["first"]?["type"]?.AsString();
            string name = entry?["first"]?["name"]?.AsString();
            string guid = entry?["second"]?.Guid;
            if (type == "UnityEngine:Material" && !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(guid))
            {
                materialGuids[name] = guid;
            }
        }
        if (materialGuids.Count > 0)
        {
            UniLog.Log($"FBX external material mappings: {materialGuids.Count}");
        }
    }

    // ---------------------------------------------------------------- descriptor (viseme/blink/view)

    private static void ParseDescriptor(UnityScene scene, YamlDocument descriptor, VrchatAvatar avatar)
    {
        YamlNode d = descriptor.Root;

        YamlNode view = d?["ViewPosition"];
        if (view != null && view.IsMap)
        {
            avatar.ViewPosition = new Vec3(view.Vec("x"), view.Vec("y"), view.Vec("z"));
        }

        // Visemes: only when the descriptor uses VisemeBlendShape lip sync (lipSync == 3).
        int lipSync = d?["lipSync"]?.AsInt(-1) ?? -1;
        YamlNode visemeShapes = d?["VisemeBlendShapes"];
        long visemeMeshId = d?["VisemeSkinnedMesh"]?.FileID ?? 0;
        // May be null for a variant-of-FBX avatar (stripped mesh reference); visemes still resolve by
        // blendshape name across the imported renderers, so a null mesh name is allowed here.
        string visemeMesh = scene.ResolveGameObjectName(visemeMeshId);
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
            avatar.LeftEyeBoneName = scene.ResolveGameObjectName(eye["leftEye"]?.FileID ?? 0);
            avatar.RightEyeBoneName = scene.ResolveGameObjectName(eye["rightEye"]?.FileID ?? 0);

            int eyelidType = eye["eyelidType"]?.AsInt(0) ?? 0;
            if (eyelidType == 2)
            {
                string eyelidMesh = scene.ResolveGameObjectName(eye["eyelidsSkinnedMesh"]?.FileID ?? 0);
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
