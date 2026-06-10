namespace VrmToResonitePackage.Vrm;

/// <summary>
/// Engine-independent representation of the VRM data we care about,
/// normalized across VRM 0.x and VRM 1.0.
/// </summary>
public sealed class VrmModel
{
    /// <summary>"0" or "1" (major spec version).</summary>
    public int SpecVersionMajor { get; set; }

    public string Title { get; set; }
    public string Author { get; set; }

    /// <summary>glTF node index -> node name.</summary>
    public List<string> NodeNames { get; } = new();

    /// <summary>glTF node index -> mesh index (or -1).</summary>
    public List<int> NodeMeshIndices { get; } = new();

    /// <summary>glTF mesh index -> morph target names (may be empty).</summary>
    public List<List<string>> MeshTargetNames { get; } = new();

    /// <summary>glTF mesh index -> node indices that reference the mesh.</summary>
    public Dictionary<int, List<int>> MeshToNodes { get; } = new();

    /// <summary>glTF texture index -> image index.</summary>
    public List<int> TextureToImage { get; } = new();

    /// <summary>VRM humanoid bone name (normalized VRM1 style, camelCase) -> node index.</summary>
    public Dictionary<string, int> HumanBones { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Offset of the first-person viewpoint from the head bone, in model space (meters).</summary>
    public System.Numerics.Vector3? FirstPersonOffset { get; set; }

    public List<VrmExpression> Expressions { get; } = new();

    public List<VrmSpringChain> SpringChains { get; } = new();

    public List<VrmSpringCollider> SpringColliders { get; } = new();

    /// <summary>Material name -> MToon-ish display info.</summary>
    public Dictionary<string, VrmMaterialInfo> Materials { get; } = new();

    public string GetNodeName(int index)
    {
        if (index < 0 || index >= NodeNames.Count)
        {
            return null;
        }
        return NodeNames[index];
    }
}

public sealed class VrmExpression
{
    /// <summary>Normalized preset name (VRM1 style): aa, ih, ou, ee, oh, blink, blinkLeft, blinkRight,
    /// happy, angry, sad, relaxed, surprised, neutral, lookUp, lookDown, lookLeft, lookRight — or custom name.</summary>
    public string Preset { get; set; }

    /// <summary>Original display name from the file.</summary>
    public string Name { get; set; }

    public bool IsBinary { get; set; }

    public List<VrmExpressionBind> Binds { get; } = new();
}

public sealed class VrmExpressionBind
{
    /// <summary>glTF mesh index the morph target lives on (resolved for both VRM versions).</summary>
    public int MeshIndex { get; set; }

    /// <summary>Morph target index within the mesh.</summary>
    public int MorphIndex { get; set; }

    /// <summary>Normalized weight 0..1.</summary>
    public float Weight { get; set; }
}

public sealed class VrmSpringChain
{
    public string Name { get; set; }

    /// <summary>Node indices that are roots of the chain (VRM0) or the explicit joint list (VRM1).</summary>
    public List<int> RootNodes { get; } = new();

    /// <summary>Explicit joint chain (VRM1). Empty for VRM0.</summary>
    public List<int> JointNodes { get; } = new();

    public float Stiffness { get; set; } = 1f;
    public float GravityPower { get; set; }
    public System.Numerics.Vector3 GravityDir { get; set; } = new(0f, -1f, 0f);
    public float DragForce { get; set; } = 0.4f;
    public float HitRadius { get; set; } = 0.02f;

    /// <summary>Indices into VrmModel.SpringColliders (already flattened from collider groups).</summary>
    public List<int> ColliderIndices { get; } = new();
}

public sealed class VrmSpringCollider
{
    public int NodeIndex { get; set; }
    public System.Numerics.Vector3 Offset { get; set; }
    public float Radius { get; set; }

    /// <summary>Capsule tail offset (VRM1 only); null for spheres.</summary>
    public System.Numerics.Vector3? Tail { get; set; }
}

public sealed class VrmMaterialInfo
{
    public string Name { get; set; }
    public bool IsMToon { get; set; }
    /// <summary>opaque / mask / blend.</summary>
    public string AlphaMode { get; set; } = "opaque";
    public float AlphaCutoff { get; set; } = 0.5f;
    public bool DoubleSided { get; set; }

    /// <summary>Outline width normalized to meters (MToon 0.x stores centimeters).</summary>
    public float OutlineWidth { get; set; }

    /// <summary>none / world / screen.</summary>
    public string OutlineWidthMode { get; set; } = "none";

    /// <summary>glTF image index of the outline width multiply texture, if any.</summary>
    public int? OutlineWidthImageIndex { get; set; }

    public System.Numerics.Vector4? OutlineColor { get; set; }
    public System.Numerics.Vector4? ShadeColor { get; set; }
    public System.Numerics.Vector4? EmissionColor { get; set; }
}
