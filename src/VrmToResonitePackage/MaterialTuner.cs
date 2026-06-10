using Elements.Core;
using FrooxEngine;
using VrmToResonitePackage.Vrm;

namespace VrmToResonitePackage;

/// <summary>
/// Post-processes the XiexeToon materials created by the model importer with
/// MToon parameters from the VRM (blend mode, cutoff, outlines, double-sidedness).
/// </summary>
internal static class MaterialTuner
{
    public static void Apply(Slot root, Slot assetsSlot, VrmModel vrm)
    {
        int tuned = 0;
        foreach (XiexeToonMaterial material in assetsSlot.GetComponentsInChildren<XiexeToonMaterial>())
        {
            // XiexeToon attaches a default shadow ramp texture on creation; MToon's look
            // is closer to a rampless material with a soft shadow edge.
            material.ShadowRamp.Target = null;
            material.ShadowSharpness.Value = 0f;

            VrmMaterialInfo info = FindInfo(vrm, material.Slot.Name);
            if (info != null)
            {
                ApplyInfo(material, info);
            }
            tuned++;
        }
        UniLog.Log($"マテリアルを {tuned} 件調整しました。");
    }

    private static VrmMaterialInfo FindInfo(VrmModel vrm, string slotName)
    {
        if (string.IsNullOrEmpty(slotName))
        {
            return null;
        }
        // Importer names material slots "Material: <name>".
        const string prefix = "Material: ";
        string name = slotName.StartsWith(prefix, StringComparison.Ordinal)
            ? slotName.Substring(prefix.Length)
            : slotName;
        if (vrm.Materials.TryGetValue(name, out VrmMaterialInfo info))
        {
            return info;
        }
        // Assimp may dedupe names with suffixes like ".001"; try the base name.
        int dot = name.LastIndexOf('.');
        if (dot > 0 && vrm.Materials.TryGetValue(name.Substring(0, dot), out info))
        {
            return info;
        }
        return null;
    }

    private static void ApplyInfo(XiexeToonMaterial material, VrmMaterialInfo info)
    {
        switch (info.AlphaMode)
        {
            case "blend":
                material.BlendMode.Value = BlendMode.Alpha;
                // Alpha blending defaults to the transparent queue (3000), which can
                // break sorting badly on converted MToon avatars; the opaque queue
                // (2000) avoids the worst artifacts.
                material.RenderQueue.Value = 2000;
                break;
            case "mask":
                material.BlendMode.Value = BlendMode.Cutout;
                material.AlphaClip.Value = info.AlphaCutoff;
                break;
            default:
                material.BlendMode.Value = BlendMode.Opaque;
                break;
        }

        if (info.DoubleSided)
        {
            material.Culling.Value = Culling.Off;
        }

        if (info.OutlineWidth > 0.0001f)
        {
            material.Outline.Value = XiexeToonMaterial.OutlineStyle.Emissive;
            // MToon outline width is in centimeters; Xiexe's is in its own small unit.
            // 0.05cm (typical MToon) maps to a subtle outline of ~0.1.
            material.OutlineWidth.Value = MathX.Clamp(info.OutlineWidth * 2f, 0.01f, 1f);
            if (info.OutlineColor.HasValue)
            {
                System.Numerics.Vector4 c = info.OutlineColor.Value;
                material.OutlineColor.Value = new colorX(c.X, c.Y, c.Z, c.W);
            }
        }

        if (info.EmissionColor.HasValue && material.EmissionMap.Target != null)
        {
            System.Numerics.Vector4 e = info.EmissionColor.Value;
            if (e.X + e.Y + e.Z > 0.001f)
            {
                material.EmissionColor.Value = new colorX(e.X, e.Y, e.Z, 1f);
            }
        }

        if (info.ShadeColor.HasValue)
        {
            // Approximate MToon's shade tint with Xiexe's shadow rim color.
            System.Numerics.Vector4 s = info.ShadeColor.Value;
            material.ShadowRim.Value = new colorX(s.X, s.Y, s.Z, 1f);
        }
    }
}
