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
    private static async Task<StaticTexture2D> BakeMainLayers(Slot assets, UnityPackage package, LilToonInfo info,
        LilToonMainTextureBakePlan plan)
    {
        foreach (string warning in plan.Warnings) UniLog.Warning($"Main texture bake on {info.Name}: {warning}");
        if (!plan.Required) return null;
        var engine = assets.Engine;
        Uri uri = null;
        try
        {
            await default(ToBackground);
            Bitmap2D bitmap = CompositeMainLayers(package, info, plan);
            uri = await engine.LocalDB.SaveAssetAsync(bitmap);
        }
        catch (Exception ex)
        {
            UniLog.Warning($"Failed to bake main texture layers on {info.Name}: {ex.Message}");
        }
        await default(ToWorld);
        if (uri == null) return null;
        var texture = assets.AddSlot($"MainTex Baked: {info.Name}").AttachComponent<StaticTexture2D>();
        texture.URL.Value = uri;
        UniLog.Log($"Baked main texture on {info.Name}: main={plan.Main}, 2nd={plan.Main2nd}, " +
            $"3rd={plan.Main3rd}, alpha={plan.Alpha}, colorBaked={plan.Color}.");
        return texture;
    }

    // Like lilToon's baker, composite main -> 2nd -> 3rd in linear light. Bake
    // the material tint once and retain main alpha unless a layer explicitly changes it.
    // Output covers one UV0 tile; transforms are included, so its consumer uses identity ST.
    private static Bitmap2D CompositeMainLayers(UnityPackage package, LilToonInfo info,
        LilToonMainTextureBakePlan plan)
    {
        var layers = new List<LilToonMainLayer>();
        if (plan.Main2nd) layers.Add(info.Main2nd);
        if (plan.Main3rd) layers.Add(info.Main3rd);
        var main = ReadBakeTexture(package, info.MainTexGuid);
        var alphaMask = plan.Alpha ? ReadBakeTexture(package, info.AlphaMaskGuid) : null;
        var adjustMask = plan.Main ? ReadBakeTexture(package, info.MainColorAdjustMaskGuid) : null;
        var gradation = plan.Main && info.MainGradationStrength != 0
            ? ReadBakeTexture(package, info.MainGradationTexGuid, linearClamp: true) : null;
        var inputs = layers.Select(layer => (layer,
            texture: ReadBakeTexture(package, layer.TextureGuid),
            mask: ReadBakeTexture(package, layer.MaskGuid))).ToArray();
        double requiredWidth = 1, requiredHeight = 1;
        IncludeDensity(main, info.MainTexScale);
        IncludeDensity(adjustMask, info.MainTexScale);
        IncludeDensity(alphaMask, info.MainTexScale * info.AlphaMaskTexScale);
        foreach (var input in inputs)
        {
            IncludeDensity(input.texture, input.layer.Scale, input.layer.Angle);
            IncludeDensity(input.mask, info.MainTexScale);
        }
        // Never silently clamp density: the caller retains the original texture,
        // tint and ST when a bake fails. Bound allocation before converting to int.
        const int maxDimension = 8192;
        if (!double.IsFinite(requiredWidth) || !double.IsFinite(requiredHeight) ||
            requiredWidth > maxDimension || requiredHeight > maxDimension)
            throw new InvalidOperationException($"Transformed main texture bake requires {requiredWidth}x{requiredHeight} " +
                $"texels, exceeding the {maxDimension} per-axis limit; retaining the original main texture and transform.");
        int width = (int)Math.Ceiling(requiredWidth), height = (int)Math.Ceiling(requiredHeight);

        void IncludeDensity(BakeTexture texture, Vector2 scale, float angle = 0)
        {
            if (texture == null) return;
            // Sampling applies scale, then rotation. Project both source texel axes
            // onto each output UV axis; summing magnitudes also covers oblique detail.
            double sin = Math.Abs(Math.Sin(angle)), cos = Math.Abs(Math.Cos(angle));
            requiredWidth = Math.Max(requiredWidth,
                Math.Abs((double)scale.X) * (texture.Width * cos + texture.Height * sin));
            requiredHeight = Math.Max(requiredHeight,
                Math.Abs((double)scale.Y) * (texture.Width * sin + texture.Height * cos));
        }
        var output = new Bitmap2D(width, height, TextureFormat.RGBA32, mipmaps: false, ColorProfile.sRGB);
        Vector4 tint = plan.Color ? LinearColor(info.Color) : Vector4.One;
        var tints = inputs.Select(i => LinearColor(i.layer.Color)).ToArray();
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                var uv = new Vector2((x + 0.5f) / width, (y + 0.5f) / height);
                Vector2 mainUv = uv * info.MainTexScale + info.MainTexOffset;
                Vector4 pixel = main?.Sample(mainUv) ?? Vector4.One;
                if (plan.Main)
                {
                    var original = new Vector3(pixel.X, pixel.Y, pixel.Z);
                    Vector3 adjusted = ToneCorrect(original, info.MainTexHSVG);
                    if (info.MainGradationStrength != 0)
                    {
                        // lilGradationMap indexes each channel in sRGB, then linearizes
                        // its sampled result. The lookup uses the shader's linear-clamp sampler.
                        var mapped = new Vector3(
                            gradation?.Sample(new Vector2(ToSrgb(adjusted.X), 0.5f)).X ?? 1f,
                            gradation?.Sample(new Vector2(ToSrgb(adjusted.Y), 0.5f)).Y ?? 1f,
                            gradation?.Sample(new Vector2(ToSrgb(adjusted.Z), 0.5f)).Z ?? 1f);
                        mapped = new Vector3(ToLinear(mapped.X), ToLinear(mapped.Y), ToLinear(mapped.Z));
                        adjusted = Vector3.Lerp(adjusted, mapped, info.MainGradationStrength);
                    }
                    pixel = new Vector4(Vector3.Lerp(original, adjusted, adjustMask?.Sample(mainUv).X ?? 1f), pixel.W);
                }
                pixel *= tint;
                for (int index = 0; index < inputs.Length; index++)
                {
                    var (layer, texture, mask) = inputs[index];
                    Vector2 layerUv = uv * layer.Scale + layer.Offset - new Vector2(0.5f);
                    float sin = MathF.Sin(layer.Angle), cos = MathF.Cos(layer.Angle);
                    layerUv = new Vector2(layerUv.X * cos - layerUv.Y * sin,
                        layerUv.X * sin + layerUv.Y * cos) + new Vector2(0.5f);
                    Vector4 overlay = (texture?.Sample(layerUv) ?? Vector4.One) * tints[index];
                    // lil_common_frag samples the mask at uvMain, ignoring the mask's ST.
                    overlay.W *= mask?.Sample(mainUv).X ?? 1f;
                    if (info.AlphaMode != "opaque" && layer.AlphaMode != 0)
                    {
                        pixel.W = layer.AlphaMode switch
                        {
                            1 => overlay.W,
                            2 => pixel.W * overlay.W,
                            3 => Math.Clamp(pixel.W + overlay.W, 0f, 1f),
                            4 => Math.Clamp(pixel.W - overlay.W, 0f, 1f),
                            _ => pixel.W,
                        };
                        overlay.W = 1f;
                    }
                    var dst = new Vector3(pixel.X, pixel.Y, pixel.Z);
                    var src = new Vector3(overlay.X, overlay.Y, overlay.Z);
                    Vector3 blended = layer.BlendMode switch
                    {
                        1 => dst + src,
                        2 => Vector3.Max(dst + src - dst * src, dst),
                        3 => dst * src,
                        _ => src,
                    };
                    blended = Vector3.Lerp(dst, blended, overlay.W);
                    pixel = new Vector4(blended, pixel.W);
                }
                // SDK uses a separate alpha pass after the color bake, including when
                // neither 2nd nor 3rd is eligible. Do not multiply RGB/tint again here.
                if (plan.Alpha)
                {
                    float mask = alphaMask.Sample(mainUv * info.AlphaMaskTexScale + info.AlphaMaskTexOffset).X;
                    mask = Math.Clamp(mask * info.AlphaMaskScale + info.AlphaMaskValue, 0f, 1f);
                    pixel.W = ApplyAlphaMask(pixel.W, mask, info.AlphaMaskMode);
                }
                var result = new color(ToSrgb(pixel.X), ToSrgb(pixel.Y), ToSrgb(pixel.Z), pixel.W);
                output.SetPixel(x, y, in result);
            }
        });
        return output;
    }

    private static float ApplyAlphaMask(float alpha, float mask, int mode) => mode switch
    {
        1 => mask,
        2 => alpha * mask,
        3 => Math.Clamp(alpha + mask, 0f, 1f),
        4 => Math.Clamp(alpha - mask, 0f, 1f),
        _ => alpha,
    };

    private static Vector3 ToneCorrect(Vector3 color, Vector4 hsvg)
    {
        // lilToneCorrection: gamma, RGB -> HSV, HSV adjustment, HSV -> RGB.
        color = new Vector3(MathF.Pow(MathF.Abs(color.X), hsvg.W),
            MathF.Pow(MathF.Abs(color.Y), hsvg.W), MathF.Pow(MathF.Abs(color.Z), hsvg.W));
        float max = Math.Max(color.X, Math.Max(color.Y, color.Z));
        float min = Math.Min(color.X, Math.Min(color.Y, color.Z)), delta = max - min;
        float hue = delta <= 1e-10f ? 0f : max == color.X ? (color.Y - color.Z) / delta / 6f :
            max == color.Y ? ((color.Z - color.X) / delta + 2f) / 6f : ((color.X - color.Y) / delta + 4f) / 6f;
        float saturation = Math.Clamp(delta / (max + 1e-10f) * hsvg.Y, 0f, 1f);
        float value = Math.Clamp(max * hsvg.Z, 0f, 1f);
        hue += hsvg.X;
        float Channel(float shift)
        {
            float h = hue + shift;
            h -= MathF.Floor(h);
            return value * (1f - saturation + saturation * Math.Clamp(MathF.Abs(h * 6f - 3f) - 1f, 0f, 1f));
        }
        return new Vector3(Channel(1f), Channel(2f / 3f), Channel(1f / 3f));
    }

    private static BakeTexture ReadBakeTexture(UnityPackage package, string guid, bool linearClamp = false)
    {
        if (string.IsNullOrEmpty(guid)) return null; // Shader's default white texture.
        var asset = package.ByGuid(guid);
        if (asset?.HasContent != true)
        {
            UniLog.Warning($"Bake texture {guid} was not found; using the shader's default white texture.");
            return null;
        }
        var bitmap = DecodeBitmap(asset);
        var importer = asset.MetaPath != null && File.Exists(asset.MetaPath)
            ? UnityYaml.ParseFlatDocument(File.ReadAllText(asset.MetaPath))?["TextureImporter"] : null;
        bool srgb = (importer?["mipmaps"]?["sRGBTexture"]?.AsInt(1) ?? 1) != 0;
        var settings = importer?["textureSettings"];
        return new BakeTexture(bitmap, srgb, linearClamp ? 1 : settings?["wrapU"]?.AsInt() ?? 0,
            linearClamp ? 1 : settings?["wrapV"]?.AsInt() ?? 0, !linearClamp && settings?["filterMode"]?.AsInt(1) == 0);
    }

    private static Vector4 LinearColor(Vector4 c) => new(ToLinear(c.X), ToLinear(c.Y), ToLinear(c.Z), c.W);
    private static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    private static float ToSrgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;

    private sealed class BakeTexture
    {
        public int Width { get; }
        public int Height { get; }
        private readonly Vector4[] _pixels;
        private readonly int _wrapU, _wrapV;
        private readonly bool _point;

        public BakeTexture(Bitmap2D bitmap, bool srgb, int wrapU, int wrapV, bool point)
        {
            Width = bitmap.Size.x; Height = bitmap.Size.y;
            _wrapU = wrapU; _wrapV = wrapV; _point = point;
            _pixels = new Vector4[Width * Height];
            Parallel.For(0, Height, y =>
            {
                for (int x = 0; x < Width; x++)
                {
                    var c = bitmap.GetPixel(x, y);
                    var pixel = new Vector4(c.r, c.g, c.b, c.a);
                    _pixels[y * Width + x] = srgb ? LinearColor(pixel) : pixel;
                }
            });
        }

        public Vector4 Sample(Vector2 uv)
        {
            float x = uv.X * Width - 0.5f, y = uv.Y * Height - 0.5f;
            if (_point) return Pixel((int)MathF.Floor(x + 0.5f), (int)MathF.Floor(y + 0.5f));
            int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
            return Vector4.Lerp(Vector4.Lerp(Pixel(ix, iy), Pixel(ix + 1, iy), x - ix),
                Vector4.Lerp(Pixel(ix, iy + 1), Pixel(ix + 1, iy + 1), x - ix), y - iy);
        }

        private Vector4 Pixel(int x, int y) => _pixels[Wrap(y, Height, _wrapV) * Width + Wrap(x, Width, _wrapU)];
        private static int Wrap(int i, int size, int mode)
        {
            if (mode == 1) return Math.Clamp(i, 0, size - 1); // Clamp
            if (mode == 3) return Math.Clamp(i < 0 ? -i - 1 : i, 0, size - 1); // MirrorOnce
            int period = mode == 2 ? size * 2 : size;
            i = (i % period + period) % period;
            return i < size ? i : period - i - 1;
        }
    }
}
