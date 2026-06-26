namespace VrmToResonitePackage.Vrchat;

/// <summary>
/// Fixed identifiers and lookup tables for reading VRChat avatars. These are well-known
/// constants (script GUIDs, the VRChat viseme order, the Unity humanoid bone names) — no
/// VRChat SDK files are bundled; only their observable serialized shape is referenced.
/// </summary>
internal static class VrchatConstants
{
    /// <summary>VRCSDK3A.dll script guid used by VRCAvatarDescriptor MonoBehaviours.</summary>
    public const string AvatarDescriptorScriptGuid = "67cc4cb7839cd3741b63733d5adf0442";

    /// <summary>VRC.SDK3.Dynamics.PhysBone.dll script guid (shared by PhysBone and its collider).</summary>
    public const string PhysBoneDllGuid = "2a2c05204084d904aa4945ccff20d8e5";

    /// <summary>The in-DLL type id for VRCPhysBone (distinguishes it from the collider type).</summary>
    public const long PhysBoneScriptFileId = 1661641543;

    /// <summary>liltoon shader guids (lts / multi / etc.). Used to recognise liltoon materials.</summary>
    public static readonly HashSet<string> LilToonShaderGuids = new(StringComparer.OrdinalIgnoreCase)
    {
        "165365ab7100a044ca85fc8c33548a62", // lilToon (lts)
        "f1e76ce29f4467e478e25fd3b6d1fa90", // lilToonMulti / variants seen in the wild
    };

    /// <summary>
    /// VRChat viseme slot order (VRC_AvatarDescriptor.VisemeBlendShapes is indexed by this enum).
    /// </summary>
    public static readonly string[] VisemeOrder =
    {
        "sil", "PP", "FF", "TH", "DD", "kk", "CH", "SS", "nn", "RR",
        "aa", "E", "ih", "oh", "ou",
    };

    /// <summary>
    /// Maps every VRChat VisemeBlendShapes slot to the matching Resonite viseme preset. Resonite's
    /// <c>FrooxEngine.Viseme</c> enum (sil,PP,FF,TH,DD,kk,CH,SS,nn,RR,aa,E,ih,oh,ou) is the same set
    /// and order as VRChat's, so the preset name is just the slot name.
    /// </summary>
    public static IEnumerable<(string resonitePreset, int vrcIndex)> VisemeToVrcSlot()
    {
        for (int i = 0; i < VisemeOrder.Length; i++)
        {
            yield return (VisemeOrder[i], i);
        }
    }

    private static readonly Dictionary<string, string> ThumbRemap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["leftThumbProximal"] = "leftThumbMetacarpal",
        ["leftThumbIntermediate"] = "leftThumbProximal",
        ["rightThumbProximal"] = "rightThumbMetacarpal",
        ["rightThumbIntermediate"] = "rightThumbProximal",
    };

    /// <summary>
    /// Normalizes a Unity humanoid bone name (as found in <c>*.fbx.meta humanDescription.human[].humanName</c>,
    /// e.g. "LeftUpperArm" or "Left Thumb Proximal") into the VRM-1 camelCase scheme that
    /// <see cref="AvatarSetup"/>'s bone table expects, including Unity→VRM thumb index shift.
    /// </summary>
    public static string NormalizeHumanName(string unityHumanName)
    {
        if (string.IsNullOrWhiteSpace(unityHumanName))
        {
            return null;
        }
        string token = unityHumanName.Replace(" ", "");
        string camel = char.ToLowerInvariant(token[0]) + token[1..];
        return ThumbRemap.TryGetValue(camel, out string remapped) ? remapped : camel;
    }

    /// <summary>
    /// Infers a VRM humanoid bone from common Blender/Unity bone names when an FBX is marked
    /// humanoid but its ModelImporter meta contains an empty humanDescription.human list.
    /// </summary>
    public static string InferHumanNameFromSkeletonName(string skeletonName)
    {
        if (string.IsNullOrWhiteSpace(skeletonName))
        {
            return null;
        }

        string name = skeletonName.Trim();
        string side = null;
        if (name.EndsWith(".L", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("_L", StringComparison.OrdinalIgnoreCase))
        {
            side = "left";
            name = name[..^2];
        }
        else if (name.EndsWith(".R", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith("_R", StringComparison.OrdinalIgnoreCase))
        {
            side = "right";
            name = name[..^2];
        }

        name = name.Replace(" ", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .ToLowerInvariant();

        if (side == null)
        {
            return name switch
            {
                "hips" or "pelvis" => "hips",
                "spine" => "spine",
                "chest" => "chest",
                "upperchest" => "upperChest",
                "neck" => "neck",
                "head" => "head",
                "jaw" => "jaw",
                _ => null,
            };
        }

        return name switch
        {
            "eye" => side + "Eye",
            "shoulder" or "clavicle" => side + "Shoulder",
            "upperarm" or "arm" => side + "UpperArm",
            "lowerarm" or "forearm" => side + "LowerArm",
            "hand" or "wrist" => side + "Hand",
            "upperleg" or "thigh" => side + "UpperLeg",
            "lowerleg" or "shin" or "calf" => side + "LowerLeg",
            "foot" => side + "Foot",
            "toe" or "toes" => side + "Toes",
            "thumbproximal" => side + "ThumbMetacarpal",
            "thumbintermediate" => side + "ThumbProximal",
            "thumbdistal" => side + "ThumbDistal",
            "indexproximal" => side + "IndexProximal",
            "indexintermediate" => side + "IndexIntermediate",
            "indexdistal" => side + "IndexDistal",
            "middleproximal" => side + "MiddleProximal",
            "middleintermediate" => side + "MiddleIntermediate",
            "middledistal" => side + "MiddleDistal",
            "ringproximal" => side + "RingProximal",
            "ringintermediate" => side + "RingIntermediate",
            "ringdistal" => side + "RingDistal",
            "littleproximal" or "pinkyproximal" => side + "LittleProximal",
            "littleintermediate" or "pinkyintermediate" => side + "LittleIntermediate",
            "littledistal" or "pinkydistal" => side + "LittleDistal",
            _ => null,
        };
    }

    /// <summary>
    /// Decodes Unity's compact int-array serialization (e.g. "f1000000ffffffffffffffff" -> [241, -1, -1]).
    /// Used for VRCAvatarDescriptor.customEyeLookSettings.eyelidsBlendshapes.
    /// </summary>
    public static int[] DecodeIntArrayHex(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length % 8 != 0)
        {
            return Array.Empty<int>();
        }
        var result = new int[hex.Length / 8];
        for (int i = 0; i < result.Length; i++)
        {
            // Little-endian 32-bit words.
            uint word = 0;
            for (int b = 0; b < 4; b++)
            {
                string byteHex = hex.Substring(i * 8 + b * 2, 2);
                word |= (uint)Convert.ToInt32(byteHex, 16) << (b * 8);
            }
            result[i] = unchecked((int)word);
        }
        return result;
    }
}
