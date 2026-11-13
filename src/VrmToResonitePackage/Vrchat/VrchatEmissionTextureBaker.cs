using System.Numerics;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using VrmToResonitePackage.Unity;
using ColorProfile = Renderite.Shared.ColorProfile;
using TextureFormat = Renderite.Shared.TextureFormat;

namespace VrmToResonitePackage.Vrchat;

internal static partial class VrchatMaterialBuilder
{
    private static async Task<StaticTexture2D> BakeEmissionMap(Slot assets, UnityPackage package,
        LilToonInfo info, Bitmap2D bakedMain)
    {
        bool Exists(string guid) => package.ByGuid(guid)?.HasContent == true;
        bool hasMask = Exists(info.EmissionBlendMaskGuid);
        bool usesMain = info.EmissionMainStrength > 0 && (bakedMain != null || Exists(info.MainTexGuid));
        // A map alone already supports its own ST and UV selection; keep the cached source texture.
        if (!hasMask && !usesMain) return null;
        if (Exists(info.EmissionMapGuid) && info.EmissionMapUVMode != 0)
        {
            UniLog.Warning($"Emission bake on {info.Name} skipped: UV mode {info.EmissionMapUVMode} " +
                "cannot be combined with UV0 mask/main textures. Retaining the emission map and transform.");
            return null;
        }

        var engine = assets.Engine;
        Uri uri = null;
        try
        {
            await default(ToBackground);
            var bitmap = CompositeEmissionMap(package, info, bakedMain);
            uri = await engine.LocalDB.SaveAssetAsync(bitmap);
        }
        catch (Exception ex)
        {
            UniLog.Warning($"Failed to bake emission on {info.Name}: {ex.Message}. Retaining the emission fallback.");
        }
        await default(ToWorld);
        if (uri == null) return null;
        var texture = assets.AddSlot($"EmissionMap Baked: {info.Name}").AttachComponent<StaticTexture2D>();
        texture.URL.Value = uri;
        texture.PreferredProfile.Value = ColorProfile.sRGB;
        UniLog.Log($"Baked emission on {info.Name}: mask={hasMask}, main={usesMain}, bakedMain={bakedMain != null}.");
        return texture;
    }

    private static Bitmap2D CompositeEmissionMap(UnityPackage package, LilToonInfo info, Bitmap2D bakedMain)
    {
        var emission = ReadBakeTexture(package, info.EmissionMapGuid);
        var mask = ReadBakeTexture(package, info.EmissionBlendMaskGuid);
        float strength = Math.Clamp(info.EmissionMainStrength, 0f, 1f);
        var main = strength > 0
            ? bakedMain != null ? new BakeTexture(bakedMain, srgb: true, default) : ReadBakeTexture(package, info.MainTexGuid)
            : null;
        Vector2 mainScale = bakedMain != null ? Vector2.One : info.MainTexScale;
        Vector2 mainOffset = bakedMain != null ? Vector2.Zero : info.MainTexOffset;
        double requiredWidth = 1, requiredHeight = 1;
        Include(emission, info.EmissionMapScale, info.EmissionMapOffset, "emission map");
        Include(mask, info.EmissionBlendMaskScale, info.EmissionBlendMaskOffset, "blend mask");
        Include(main, mainScale, mainOffset, "main image");
        const int maxDimension = 8192;
        if (!double.IsFinite(requiredWidth) || !double.IsFinite(requiredHeight) ||
            requiredWidth > maxDimension || requiredHeight > maxDimension)
            throw new InvalidOperationException($"Transformed emission bake requires {requiredWidth}x{requiredHeight} " +
                $"texels, exceeding the {maxDimension} per-axis limit");
        int width = (int)Math.Ceiling(requiredWidth), height = (int)Math.Ceiling(requiredHeight);
        var output = new Bitmap2D(width, height, TextureFormat.RGBA32, mipmaps: false, ColorProfile.sRGB);
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                var uv = new Vector2((x + 0.5f) / width, (y + 0.5f) / height);
                var source = emission?.Sample(uv * info.EmissionMapScale + info.EmissionMapOffset) ?? Vector4.One;
                var rgb = new Vector3(source.X, source.Y, source.Z);
                // Match the SDK's Multiply layers: layer alpha is the blend weight,
                // and only the base emission map contributes output alpha.
                if (mask != null)
                {
                    var value = mask.Sample(uv * info.EmissionBlendMaskScale + info.EmissionBlendMaskOffset);
                    rgb *= Vector3.Lerp(Vector3.One, new Vector3(value.X, value.Y, value.Z), value.W);
                }
                if (main != null)
                {
                    var value = main.Sample(uv * mainScale + mainOffset);
                    rgb *= Vector3.Lerp(Vector3.One, new Vector3(value.X, value.Y, value.Z), strength * value.W);
                }
                // The SDK applies Opacify after its sRGB bake. Match the existing
                // GetRgbTimesAlphaTexture path exactly once; emission tint stays on the material.
                var pixel = new color(ToSrgb(rgb.X) * source.W, ToSrgb(rgb.Y) * source.W,
                    ToSrgb(rgb.Z) * source.W, source.W);
                output.SetPixel(x, y, in pixel);
            }
        });
        return output;

        void Include(BakeTexture texture, Vector2 scale, Vector2 offset, string input)
        {
            if (texture == null) return;
            if (!float.IsFinite(scale.X) || !float.IsFinite(scale.Y) ||
                !float.IsFinite(offset.X) || !float.IsFinite(offset.Y))
                throw new InvalidOperationException($"Emission bake {input} has a nonfinite texture transform");
            // The output repeats every mesh-UV unit, including outside [0,1]. Each
            // input must have that period too; more pixels cannot fix a different domain.
            if (!RepeatsPerTile(texture.Width, scale.X, texture.Sampler.WrapU) ||
                !RepeatsPerTile(texture.Height, scale.Y, texture.Sampler.WrapV))
                throw new InvalidOperationException($"Emission bake {input} is not periodic over one UV0 tile " +
                    $"(scale={scale}, wrapU={texture.Sampler.WrapU}, wrapV={texture.Sampler.WrapV})");
            requiredWidth = Math.Max(requiredWidth, Math.Abs((double)scale.X) * texture.Width);
            requiredHeight = Math.Max(requiredHeight, Math.Abs((double)scale.Y) * texture.Height);
        }

        static bool RepeatsPerTile(int size, float scale, int wrap) => size == 1 || scale == 0 || wrap switch
        {
            1 or 3 => false, // Clamp / MirrorOnce are not periodic for a varying axis.
            2 => scale % 2 == 0, // Mirror needs a full forward + reverse cycle.
            _ => scale == MathF.Truncate(scale), // Repeat, including the default sampler.
        };
    }
}
