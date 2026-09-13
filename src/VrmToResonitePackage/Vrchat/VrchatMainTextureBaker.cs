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
    private static async Task<StaticTexture2D> BakeMainLayers(Slot assets, UnityPackage package, LilToonInfo info)
    {
        if (!info.IsLilToon || (!info.Main2nd.Enabled && !info.Main3rd.Enabled)) return null;
        var layers = new List<LilToonMainLayer>();
        foreach (var (name, layer) in new[] { ("2nd", info.Main2nd), ("3rd", info.Main3rd) })
        {
            if (!layer.Enabled) continue;
            if (layer.UnsupportedFeatures.Length != 0 || layer.Lighting != 1f ||
                layer.BlendMode is < 0 or > 3 || layer.AlphaMode is < 0 or > 4)
            {
                UniLog.Warning($"Cannot bake {name} main texture on {info.Name}: " +
                    $"requires static UV0 with lighting enabled; features={string.Join(",", layer.UnsupportedFeatures)}, " +
                    $"lighting={layer.Lighting}, blend={layer.BlendMode}, alpha={layer.AlphaMode}.");
                continue;
            }
            layers.Add(layer);
        }
        if (layers.Count == 0) return null;
        var engine = assets.Engine;
        Uri uri = null;
        try
        {
            await default(ToBackground);
            Bitmap2D bitmap = CompositeMainLayers(package, info, layers);
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
        UniLog.Log($"Baked {layers.Count} main texture layer(s) on {info.Name}.");
        return texture;
    }

    // Like lilToon's baker, composite main -> 2nd -> 3rd in linear light. Bake
    // the material tint once and retain main alpha unless a layer explicitly changes it.
    // Output covers one UV0 tile; transforms are included, so its consumer uses identity ST.
    private static Bitmap2D CompositeMainLayers(UnityPackage package, LilToonInfo info,
        IReadOnlyList<LilToonMainLayer> layers)
    {
        var main = ReadBakeTexture(package, info.MainTexGuid);
        var inputs = layers.Select(layer => (layer,
            texture: ReadBakeTexture(package, layer.TextureGuid),
            mask: ReadBakeTexture(package, layer.MaskGuid))).ToArray();
        int width = Math.Max(main?.Width ?? 1, inputs.Max(i => i.texture?.Width ?? 1));
        int height = Math.Max(main?.Height ?? 1, inputs.Max(i => i.texture?.Height ?? 1));
        var output = new Bitmap2D(width, height, TextureFormat.RGBA32, mipmaps: false, ColorProfile.sRGB);
        Vector4 tint = LinearColor(info.Color);
        var tints = inputs.Select(i => LinearColor(i.layer.Color)).ToArray();
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                var uv = new Vector2((x + 0.5f) / width, (y + 0.5f) / height);
                Vector2 mainUv = uv * info.MainTexScale + info.MainTexOffset;
                Vector4 pixel = (main?.Sample(mainUv) ?? Vector4.One) * tint;
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
                var result = new color(ToSrgb(pixel.X), ToSrgb(pixel.Y), ToSrgb(pixel.Z), pixel.W);
                output.SetPixel(x, y, in result);
            }
        });
        return output;
    }

    private static BakeTexture ReadBakeTexture(UnityPackage package, string guid)
    {
        if (string.IsNullOrEmpty(guid)) return null; // Shader's default white texture.
        var asset = package.ByGuid(guid);
        var bitmap = DecodeBitmap(asset) ?? throw new FileNotFoundException($"Missing texture {guid}");
        var importer = asset.MetaPath != null && File.Exists(asset.MetaPath)
            ? UnityYaml.ParseFlatDocument(File.ReadAllText(asset.MetaPath))?["TextureImporter"] : null;
        bool srgb = (importer?["mipmaps"]?["sRGBTexture"]?.AsInt(1) ?? 1) != 0;
        var settings = importer?["textureSettings"];
        return new BakeTexture(bitmap, srgb, settings?["wrapU"]?.AsInt() ?? 0,
            settings?["wrapV"]?.AsInt() ?? 0, settings?["filterMode"]?.AsInt(1) == 0);
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
