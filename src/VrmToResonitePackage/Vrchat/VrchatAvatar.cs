using Vec3 = System.Numerics.Vector3;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>
/// Engine-independent representation of a VRChat avatar parsed from a .unitypackage, mirroring
/// the role <see cref="Vrm.VrmModel"/> plays for VRM. Model paths and authored object identities
/// distinguish same-named objects; display names are retained for diagnostics and legacy input.
/// </summary>
public sealed class VrchatAvatar
{
    public List<VrchatPhysicsPlacement> PhysicsPlacements { get; } = new();
    /// <summary>Root prefab GameObject name (also the avatar/display name).</summary>
    public string Name { get; set; }

    public string PrefabPath { get; set; }
    public string DescriptorRootKey { get; set; }
    public VrchatBoneTarget DescriptorRootTarget { get; set; }

    /// <summary>Disk path of the humanoid FBX to import.</summary>
    public string FbxPath { get; set; }

    public string FbxGuid { get; set; }

    /// <summary>Scale baked during Assimp import to reproduce Unity ModelImporter file-unit conversion.</summary>
    public float FbxImportScale { get; set; } = 1f;

    public string FbxUpAxis { get; set; } = "unknown";

    /// <summary>Prefab-authored placement for the primary humanoid FBX instance.</summary>
    public string FbxInstanceName { get; set; }
    public string FbxParentFbxGuid { get; set; }
    public string FbxParentNodeName { get; set; }
    public string FbxTransformNodeName { get; set; }
    public Vec3 FbxLocalPosition { get; set; }
    public System.Numerics.Quaternion FbxLocalRotation { get; set; } = System.Numerics.Quaternion.Identity;
    public Vec3 FbxLocalScale { get; set; } = Vec3.One;
    public List<VrchatPrefabTransform> FbxParentTransforms { get; } = new();

    /// <summary>Additional FBXs composed into the selected prefab, such as separate hair/accessory models.</summary>
    public List<VrchatFbxAsset> AdditionalFbxs { get; } = new();
    public List<VrchatMeshCopy> MeshCopies { get; } = new();

    /// <summary>VRM-style humanoid bone name (camelCase) -> bone GameObject/transform name.</summary>
    public Dictionary<string, string> HumanBones { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>VRChat ViewPosition, avatar-root-local, Unity coordinates (meters).</summary>
    public Vec3? ViewPosition { get; set; }

    public string LeftEyeBoneName { get; set; }
    public string RightEyeBoneName { get; set; }

    public List<VrchatViseme> Visemes { get; } = new();

    /// <summary>Blink shape, resolved by blendshape index on a named mesh (VRChat stores an index).</summary>
    public VrchatBlink Blink { get; set; }

    public List<VrchatPhysBone> PhysBones { get; } = new();

    /// <summary>Per-renderer material assignment (GameObject name -> ordered liltoon .mat guids).</summary>
    public List<VrchatRendererMaterials> RendererMaterials { get; } = new();

    /// <summary>
    /// Original FBX blendshape order by renderer name, before Resonite strips sub-threshold shapes.
    /// </summary>
    public Dictionary<string, IReadOnlyList<string>> FbxBlendShapeNames { get; } =
        new(StringComparer.Ordinal);
    public Dictionary<VrchatGameObjectReference, IReadOnlyList<string>> ModelBlendShapeNames { get; } = new();
    public Dictionary<VrchatModelRendererReference, IReadOnlyList<string>> ModelBlendShapeNamesByPath { get; } = new();

    public IReadOnlyList<string> BlendShapeNamesFor(string fbxGuid, string rendererName, string objectKey = null,
        string rendererPath = null)
    {
        if (objectKey != null)
        {
            var copy = MeshCopies.FirstOrDefault(c => c.Transform?.GameObjectKey == objectKey);
            if (copy != null) return copy.BlendShapeNames;
        }
        if (rendererPath != null)
            return ModelBlendShapeNamesByPath.GetValueOrDefault(new(fbxGuid, rendererPath));
        return fbxGuid != null
            ? ModelBlendShapeNames.GetValueOrDefault(new VrchatGameObjectReference(fbxGuid, rendererName))
            : FbxBlendShapeNames.GetValueOrDefault(rendererName);
    }
    public Dictionary<VrchatModelRendererReference, IReadOnlyList<float>> FbxBlendShapeDefaultWeights { get; } = new();

    /// <summary>
    /// FBX embedded material name -> Unity .mat guid, from ModelImporter.externalObjects or
    /// deterministic ModelImporter material search metadata.
    /// Used when a prefab variant keeps the FBX renderer hierarchy as stripped objects.
    /// </summary>
    public Dictionary<string, string> FbxMaterialGuids { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// GameObjects that start inactive in the prefab. FBX GUID scopes duplicate names across
    /// composed models, such as a base and replacement hair both containing "HairFront".
    /// </summary>
    public List<VrchatGameObjectReference> InactiveGameObjects { get; } = new();

    /// <summary>
    /// All GameObject names present in the selected prefab's hierarchy. Used to drop imported FBX
    /// meshes that the prefab deleted (avatars built by removing mesh objects from a shared FBX).
    /// </summary>
    public HashSet<string> PrefabGameObjectNames { get; } = new(StringComparer.Ordinal);

    /// <summary>Variant renderer inclusion, scoped by source model so clothing cannot delete a
    /// same-named renderer in the avatar's body model. False entries retain explicit exclusions.</summary>
    public Dictionary<VrchatGameObjectReference, bool> PrefabRendererStates { get; } = new();
    /// <summary>Whole excluded models; never import their geometry or skeletons.</summary>
    public HashSet<string> EditorOnlyFbxGuids { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Excluded model nodes, including bones and non-rendering objects.</summary>
    public HashSet<VrchatGameObjectReference> EditorOnlyModelObjects { get; } = new();
    /// <summary>Excluded model paths, preserving identity when different branches share node names.</summary>
    public Dictionary<string, HashSet<string>> EditorOnlyModelPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Excluded authored/stripped GameObject IDs for filtering component conversion.</summary>
    public Dictionary<string, HashSet<long>> EditorOnlyPrefabObjects { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool ShouldKeepRenderer(string fbxGuid, string name)
    {
        if (PrefabRendererStates.Count == 0)
            return PrefabGameObjectNames.Count == 0 || PrefabGameObjectNames.Contains(name);
        return PrefabRendererStates.TryGetValue(new VrchatGameObjectReference(fbxGuid, name), out bool keep)
            ? keep
            : PrefabRendererStates.GetValueOrDefault(new VrchatGameObjectReference(null, name));
    }

    /// <summary>Subset of Modular Avatar build operations that affect imported hierarchy/bone bindings.</summary>
    public List<VrchatModularMergeArmature> ModularMergeArmatures { get; } = new();
    public List<VrchatModularBoneProxy> ModularBoneProxies { get; } = new();
}

public sealed class VrchatFbxAsset
{
    public string Path { get; set; }
    public string Guid { get; set; }
    public float ImportScale { get; set; } = 1f;
    public string InstanceName { get; set; }
    public string ParentFbxGuid { get; set; }
    public string ParentNodeName { get; set; }
    public string TransformNodeName { get; set; }
    public Vec3 LocalPosition { get; set; }
    public System.Numerics.Quaternion LocalRotation { get; set; } = System.Numerics.Quaternion.Identity;
    public Vec3 LocalScale { get; set; } = Vec3.One;
    public List<VrchatPrefabTransform> ParentTransforms { get; } = new();
    public Dictionary<string, string> MaterialGuids { get; } = new(StringComparer.Ordinal);
}

/// <summary>An ordinary GameObject authored in a prefab between imported FBX hierarchies.</summary>
public sealed class VrchatPrefabTransform
{
    public VrchatBoneTarget ImportedBone { get; set; }
    public string Key { get; set; }
    public string GameObjectKey { get; set; }
    public string Name { get; set; }
    public bool Active { get; set; } = true;
    public Vec3 LocalPosition { get; set; }
    public System.Numerics.Quaternion LocalRotation { get; set; } = System.Numerics.Quaternion.Identity;
    public Vec3 LocalScale { get; set; } = Vec3.One;
}

public sealed class VrchatPhysicsPlacement
{
    public string ParentFbxGuid { get; set; }
    public string ParentName { get; set; }
    public List<VrchatPrefabTransform> Transforms { get; } = new();
}

public sealed record VrchatGameObjectReference(string FbxGuid, string Name);

/// <summary>A renderer in one imported model occurrence, using its full source path.</summary>
public sealed record VrchatModelRendererReference
{
    public string FbxGuid { get; }
    public string Path { get; }
    public VrchatModelRendererReference(string fbxGuid, string path)
    {
        FbxGuid = fbxGuid?.ToLowerInvariant();
        path = path?.TrimStart('/');
        Path = path == null || path == "RootNode" || path.StartsWith("RootNode/", StringComparison.Ordinal)
            ? path : path.Length == 0 ? "RootNode" : "RootNode/" + path;
    }
}

public sealed record VrchatMeshCopy(string FbxGuid, string SourceName, string Name, bool Active, bool Enabled)
{
    public IReadOnlyList<string> BlendShapeNames { get; set; } = Array.Empty<string>();
    public bool IsSkinned { get; init; } = true;
    public string SourcePath { get; init; }
    public string PrefabGuid { get; init; }
    public long RendererFileId { get; init; }
    public long GameObjectFileId { get; init; }
    public bool ReplaceSourceRenderer { get; init; }
    /// <summary>Keep the authored object/children, but omit a removed renderer or MeshFilter.</summary>
    public bool RendererRemoved { get; set; }
    public VrchatPrefabTransform Transform { get; set; }
    public string ParentFbxGuid { get; set; }
    public string ParentName { get; set; }
    public List<VrchatPrefabTransform> ParentTransforms { get; } = new();
    public Dictionary<int, VrchatBoneTarget> BoneTargets { get; } = new();
    public List<string> SourceBoneNames { get; } = new();
}

/// <summary>A serialized bone reference, scoped to its model and complete transform path.</summary>
public sealed record VrchatBoneTarget(string FbxGuid, string Name, string Path = null,
    string PrefabGuid = null, long TransformFileId = 0);

public sealed class VrchatModularMergeArmature
{
    public string SourceName { get; set; }
    public string TargetName { get; set; }
    public VrchatBoneTarget SourceBoneTarget { get; set; }
    public VrchatBoneTarget TargetBoneTarget { get; set; }
    public string TargetPath { get; set; }
    public string Prefix { get; set; } = "";
    public string Suffix { get; set; } = "";
    public bool MangleNames { get; set; } = true;
}

public sealed class VrchatModularBoneProxy
{
    public string SourceName { get; set; }
    public string TargetName { get; set; }
    public int AttachmentMode { get; set; }
    public bool MatchScale { get; set; }
}

/// <summary>One avatar prefab a package offers for conversion.</summary>
public sealed record VrchatAvatarChoice(
    string Name,
    string SourcePath,
    int Size,
    bool HasOwnDescriptor,
    bool IsPrefabVariant,
    bool IsComposedPrefab);

public sealed class VrchatViseme
{
    public VrchatBoneTarget MeshTarget { get; set; }
    public string MeshGameObjectPath { get; set; }
    public string ResonitePreset { get; set; } // aa / ih / ou / ee / oh
    public string BlendShapeName { get; set; }
    public string MeshGameObjectName { get; set; }
}

public sealed class VrchatBlink
{
    public VrchatBoneTarget MeshTarget { get; set; }
    public string MeshGameObjectPath { get; set; }
    public string BlendShapeName { get; set; }
    public string MeshGameObjectName { get; set; }
    public int BlendShapeIndex { get; set; }
}

public sealed class VrchatPhysBone
{
    public string RootBoneName { get; set; }
    public VrchatBoneTarget RootBoneTarget { get; set; }
    public List<string> IgnoreBoneNames { get; } = new();
    public List<VrchatBoneTarget> IgnoreBoneTargets { get; } = new();

    public float Pull { get; set; } = 0.2f;
    public float Spring { get; set; } = 0.2f;
    public float Stiffness { get; set; } = 0.2f;
    public float Gravity { get; set; }
    public float GravityFalloff { get; set; }
    public float Immobile { get; set; }
    public float Radius { get; set; }

    public List<VrchatPhysBoneCollider> Colliders { get; } = new();
}

public sealed class VrchatPhysBoneCollider
{
    /// <summary>The imported bone the collider is attached under (its parent in the prefab).</summary>
    public string AttachBoneName { get; set; }
    public VrchatBoneTarget AttachBoneTarget { get; set; }

    /// <summary>Collider centre (sphere) or first endpoint (capsule), in the attach bone's local space.</summary>
    public Vec3 Offset { get; set; }

    /// <summary>Capsule second endpoint in the attach bone's local space; null for spheres.</summary>
    public Vec3? Tail { get; set; }

    public float Radius { get; set; }
}

public sealed class VrchatRendererMaterials
{
    public string PrefabObjectKey { get; set; }
    /// <summary>Original imported renderer path; authored copies resolve by PrefabObjectKey first.</summary>
    public string SourcePath { get; set; }
    /// <summary>
    /// FBX that owns this renderer. Null keeps name-only matching for prefab-authored renderers
    /// that cannot be traced back to a model asset.
    /// </summary>
    public string FbxGuid { get; set; }
    public string RendererGameObjectName { get; set; }
    public List<string> MaterialGuids { get; } = new();

    /// <summary>Initial blendshape weights, including explicit zero overrides (index, Unity 0-100 weight).</summary>
    public List<(int Index, float Weight)> InitialBlendShapes { get; } = new();
}
