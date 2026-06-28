namespace VrmToResonitePackage.Vrm;

/// <summary>Where the avatar data originated; selects coordinate-system handling downstream.</summary>
public enum ModelSource
{
    Vrm0,
    Vrm1,
    VrchatFbx,
}

/// <summary>
/// Engine-independent representation of the avatar data we care about, normalized across
/// VRM 0.x, VRM 1.0 and VRChat (.unitypackage) sources. VRChat data is adapted into this
/// same shape so the rig / viseme / blink / spring-bone setup can be reused unchanged.
/// </summary>
public sealed class VrmModel
{
    /// <summary>"0" or "1" (major spec version). For VRChat sources this stays 0 and is unused.</summary>
    public int SpecVersionMajor { get; set; }

    /// <summary>Origin of the data; selects coordinate handling for spring colliders etc.</summary>
    public ModelSource Source { get; set; } = ModelSource.Vrm0;

    /// <summary>
    /// True when <see cref="GlbPreprocessor.CreateImportableGlb"/> baked a Y180 rotation
    /// into the VRM0 glTF so the imported model faces +Z (engine space == Unity space).
    /// Only ever set for VRM0; VRM1 always leaves this false.
    /// </summary>
    public bool OrientationBaked { get; set; }

    /// <summary>
    /// True when <see cref="GlbPreprocessor.CreateImportableGlb"/> applied an X-axis mirror to
    /// a "proper-handed" VRM1 (e.g. exported by the Blender VRM add-on, anatomical left at -X)
    /// so it matches the ReverseX convention UniVRM/VRoid use and Resonite's importer expects.
    /// Without it the importer's X mirror leaves the avatar mirror-flipped and VRIK spins it
    /// 180°, leaving the body/head facing backwards. Only ever set for VRM1.
    /// </summary>
    public bool OrientationMirroredX { get; set; }

    public string Title { get; set; }
    public string Author { get; set; }

    /// <summary>Raw JSON text of the VRM meta block, preserved for Resonite-side metadata use.</summary>
    public string MetaJson { get; set; }

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

    /// <summary>glTF image index of the avatar thumbnail embedded in the VRM meta, if any.</summary>
    public int? ThumbnailImageIndex { get; set; }

    /// <summary>VRM humanoid bone name (normalized VRM1 style, camelCase) -> node index.</summary>
    public Dictionary<string, int> HumanBones { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Offset of the first-person viewpoint from the head bone, in model space (meters).</summary>
    public System.Numerics.Vector3? FirstPersonOffset { get; set; }

    public List<VrmFirstPersonMeshAnnotation> FirstPersonMeshAnnotations { get; } = new();

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

public enum VrmFirstPersonFlag
{
    Auto,
    Both,
    ThirdPersonOnly,
    FirstPersonOnly,
}

public sealed class VrmFirstPersonMeshAnnotation
{
    /// <summary>glTF mesh index for VRM 0.x annotations, or resolved from the node for VRM 1.0.</summary>
    public int MeshIndex { get; set; } = -1;

    /// <summary>glTF node index for VRM 1.0 annotations, or -1 for mesh-wide VRM 0.x annotations.</summary>
    public int NodeIndex { get; set; } = -1;

    public VrmFirstPersonFlag Flag { get; set; }
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

    /// <summary>
    /// Roots of child subtrees that must not participate in this chain. VRChat PhysBone's
    /// ignoreTransforms are stored here; VRM spring chains leave this empty.
    /// </summary>
    public List<int> ExcludedRootNodes { get; } = new();

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

    /// <summary>MToon "transparent with ZWrite" flag (VRM0: _ZWrite on a blend material).</summary>
    public bool TransparentWithZWrite { get; set; }

    /// <summary>Explicit Unity render queue (VRM0 stores it directly).</summary>
    public int? RenderQueue { get; set; }

    /// <summary>VRM1 renderQueueOffsetNumber.</summary>
    public int RenderQueueOffset { get; set; }

    /// <summary>Outline width normalized to meters (MToon 0.x stores centimeters).</summary>
    public float OutlineWidth { get; set; }

    /// <summary>none / world / screen.</summary>
    public string OutlineWidthMode { get; set; } = "none";

    /// <summary>glTF image index of the outline width multiply texture, if any.</summary>
    public int? OutlineWidthImageIndex { get; set; }

    /// <summary>VRM1 stores outline width in the G channel of the shared MToon parameter texture.</summary>
    public bool OutlineWidthImageUsesGreenChannel { get; set; }

    /// <summary>VRM0 screen-coordinate outlines are damped when approximated as object-space outlines.</summary>
    public bool OutlineWidthUsesLegacyScreenDamping { get; set; }

    /// <summary>MToon10 outlineLightingMixFactor (VRM0: 0 when color mode is Fixed).</summary>
    public float OutlineLightingMix { get; set; }

    public System.Numerics.Vector4? OutlineColor { get; set; }
    public System.Numerics.Vector4? ShadeColor { get; set; }
    public System.Numerics.Vector4? BaseColor { get; set; }
    public System.Numerics.Vector4? EmissionColor { get; set; }

    /// <summary>Shading parameters normalized to MToon 1.0 semantics.</summary>
    public float ShadingToony { get; set; } = 0.9f;
    public float ShadingShift { get; set; }
    public int? ShadingShiftImageIndex { get; set; }
    public float ShadingShiftTextureScale { get; set; } = 1f;

    public System.Numerics.Vector4 RimColor { get; set; }
    public float RimLightingMix { get; set; }
    public float RimFresnelPower { get; set; } = 5f;
    public float RimLift { get; set; }

    public int? MatcapImageIndex { get; set; }
    public System.Numerics.Vector4 MatcapColor { get; set; } = new(1f, 1f, 1f, 1f);
}
