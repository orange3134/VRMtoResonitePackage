using Vec3 = System.Numerics.Vector3;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>
/// Engine-independent representation of a VRChat avatar parsed from a .unitypackage, mirroring
/// the role <see cref="Vrm.VrmModel"/> plays for VRM. Everything references bones/meshes by their
/// GameObject name, which is the slot name the model importer assigns after FBX import.
/// </summary>
public sealed class VrchatAvatar
{
    /// <summary>Root prefab GameObject name (also the avatar/display name).</summary>
    public string Name { get; set; }

    public string PrefabPath { get; set; }

    /// <summary>Disk path of the humanoid FBX to import.</summary>
    public string FbxPath { get; set; }

    public string FbxGuid { get; set; }

    /// <summary>Scale baked during Assimp import to reproduce Unity ModelImporter file-unit conversion.</summary>
    public float FbxImportScale { get; set; } = 1f;

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
    /// FBX embedded material name -> external Unity .mat guid, from ModelImporter.externalObjects.
    /// Used when a prefab variant keeps the FBX renderer hierarchy as stripped objects.
    /// </summary>
    public Dictionary<string, string> FbxMaterialGuids { get; } = new(StringComparer.Ordinal);

    /// <summary>GameObject names that start inactive in the prefab (m_IsActive = 0), e.g. costume swaps.</summary>
    public List<string> InactiveGameObjectNames { get; } = new();

    /// <summary>
    /// All GameObject names present in the selected prefab's hierarchy. Used to drop imported FBX
    /// meshes that the prefab deleted (avatars built by removing mesh objects from a shared FBX).
    /// </summary>
    public HashSet<string> PrefabGameObjectNames { get; } = new(StringComparer.Ordinal);
}

/// <summary>One avatar a package offers for conversion (root GameObject name + source + hierarchy size).</summary>
public sealed record VrchatAvatarChoice(string Name, string SourcePath, int Size);

public sealed class VrchatViseme
{
    public string ResonitePreset { get; set; } // aa / ih / ou / ee / oh
    public string BlendShapeName { get; set; }
    public string MeshGameObjectName { get; set; }
}

public sealed class VrchatBlink
{
    public string MeshGameObjectName { get; set; }
    public int BlendShapeIndex { get; set; }
}

public sealed class VrchatPhysBone
{
    public string RootBoneName { get; set; }
    public List<string> IgnoreBoneNames { get; } = new();

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

    /// <summary>Collider centre (sphere) or first endpoint (capsule), in the attach bone's local space.</summary>
    public Vec3 Offset { get; set; }

    /// <summary>Capsule second endpoint in the attach bone's local space; null for spheres.</summary>
    public Vec3? Tail { get; set; }

    public float Radius { get; set; }
}

public sealed class VrchatRendererMaterials
{
    public string RendererGameObjectName { get; set; }
    public List<string> MaterialGuids { get; } = new();

    /// <summary>Initial blendshape weights that are non-zero in the prefab (index, Unity 0-100 weight).</summary>
    public List<(int Index, float Weight)> InitialBlendShapes { get; } = new();
}
