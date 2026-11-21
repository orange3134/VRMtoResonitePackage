using System.Numerics;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Engine-independent decisions matching the SDK's GetMainTexture bake stages.</summary>
public sealed record LilToonMainTextureBakePlan(bool Main, bool Main2nd, bool Main3rd, bool Alpha,
    IReadOnlyList<string> Warnings)
{
    public bool Color => Main || Main2nd || Main3rd;
    public bool Required => Color || Alpha;

    // Only use after a successful bake; failed bakes retain the original RGBA.
    public Vector4 MaterialColorAfterBake(Vector4 color) => new(
        Color ? Vector3.One : new Vector3(color.X, color.Y, color.Z), Required ? 1f : color.W);

    public static LilToonMainTextureBakePlan Create(LilToonInfo info, Func<string, bool> textureExists = null,
        Func<string, bool> isRenderTexture = null)
    {
        var warnings = new List<string>();
        if (!info.IsLilToon) return new(false, false, false, false, warnings);
        bool Dynamic(string role, string guid)
        {
            if (string.IsNullOrEmpty(guid) || textureExists?.Invoke(guid) == false ||
                isRenderTexture?.Invoke(guid) != true) return false;
            warnings.Add($"{role} uses a RenderTexture; runtime camera/render output cannot be baked into a static image.");
            return true;
        }
        // A dynamic base affects every stage. Additional layers are independent
        // unsupported stages, just like view-dependent layers below.
        if (Dynamic("Main texture", info.MainTexGuid)) return new(false, false, false, false, warnings);
        bool Layer(string name, LilToonMainLayer layer)
        {
            if (!layer.Enabled) return false;
            if (Dynamic(name + " texture", layer.TextureGuid) || Dynamic(name + " mask", layer.MaskGuid)) return false;
            // The UV selector has no effect on a missing texture: lilToon's default
            // is white, so a color-only layer (and its UV0 blend mask) can still bake.
            if (!string.IsNullOrEmpty(layer.TextureGuid) && textureExists?.Invoke(layer.TextureGuid) != false && layer.UVMode != 0)
            {
                warnings.Add($"{name} texture uses UV mode {layer.UVMode}; a single UV0 bake cannot preserve it.");
                return false;
            }
            if (layer.UnsupportedFeatures.Length != 0 || layer.Lighting != 1f ||
                layer.BlendMode is < 0 or > 3 || layer.AlphaMode is < 0 or > 4)
            {
                warnings.Add($"{name} texture requires static UV0 with lighting enabled; " +
                    $"features={string.Join(",", layer.UnsupportedFeatures)}, lighting={layer.Lighting}, " +
                    $"blend={layer.BlendMode}, alpha={layer.AlphaMode}.");
                return false;
            }
            return true;
        }
        bool main = info.MainTexHSVG != new Vector4(0, 1, 1, 1) || info.MainGradationStrength != 0;
        if (main && (Dynamic("Main adjustment mask", info.MainColorAdjustMaskGuid) ||
            (info.MainGradationStrength != 0 && Dynamic("Main gradation texture", info.MainGradationTexGuid)))) main = false;
        bool second = Layer("2nd", info.Main2nd), third = Layer("3rd", info.Main3rd);
        bool alpha = info.AlphaMaskMode != 0 && !string.IsNullOrEmpty(info.AlphaMaskGuid);
        if (alpha && Dynamic("Alpha mask", info.AlphaMaskGuid)) alpha = false;
        if (alpha && textureExists?.Invoke(info.AlphaMaskGuid) == false)
        {
            // A dangling Unity reference is null to Material.GetTexture. It must not
            // prevent otherwise valid color stages from baking.
            warnings.Add($"Alpha mask {info.AlphaMaskGuid} was not found; skipping alpha mask bake.");
            alpha = false;
        }
        if (alpha && info.AlphaMaskMode is < 1 or > 4)
        {
            warnings.Add($"Unsupported alpha mask mode {info.AlphaMaskMode}.");
            alpha = false;
        }
        // _Color alone is already expressible on XiexeToon. If RGB is baked for
        // another reason, include _Color once and return white to the material.
        // An alpha-only bake includes tint alpha before the mask and leaves RGB on the material.
        return new(main, second, third, alpha, warnings);
    }
}
