using System.Numerics;
using System.Reflection;
using Elements.Assets;
using Elements.Core;
using Renderite.Shared;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class MaterialBakeResolutionChecks
{
    public static void Run()
    {
        string folder = Path.Combine(Environment.CurrentDirectory, ".tmp_verify", "bake-resolution", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "Assets"));
        Directory.CreateDirectory(Path.Combine(folder, "ProjectSettings"));
        string prefab = Path.Combine(folder, "Assets/Test.prefab");
        File.WriteAllText(prefab, "%YAML 1.1\n");
        File.WriteAllText(prefab + ".meta", "guid: aabbccddeeff11223344556677889900\n");
        const string stripeGuid = "11111111111111111111111111111111";
        string imagePath = Path.Combine(folder, "Assets/Stripe.png");
        var stripe = new Bitmap2D(2, 1, TextureFormat.RGBA32, false, ColorProfile.sRGB);
        stripe.SetPixel(0, 0, new color(0, 0, 0, 1));
        stripe.SetPixel(1, 0, new color(1, 1, 1, 1));
        if (!stripe.Save(imagePath)) throw new Exception("Could not save stripe fixture");
        File.WriteAllText(imagePath + ".meta", $"guid: {stripeGuid}\nTextureImporter:\n  textureSettings:\n    filterMode: 0\n");
        string Texture(string name, color left, color right, bool srgb, int wrap, int filter)
        {
            string guid = Guid.NewGuid().ToString("N");
            string path = Path.Combine(folder, "Assets", name + ".png");
            var bitmap = new Bitmap2D(2, 2, TextureFormat.RGBA32, false, ColorProfile.sRGB);
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 2; x++) bitmap.SetPixel(x, y, x == y ? left : right);
            if (!bitmap.Save(path)) throw new Exception("Could not save " + name);
            File.WriteAllText(path + ".meta", $"guid: {guid}\nTextureImporter:\n  mipmaps:\n    sRGBTexture: {(srgb ? 1 : 0)}\n  textureSettings:\n    wrapU: {wrap}\n    wrapV: {wrap}\n    filterMode: {filter}\n");
            return guid;
        }
        string mainPoint = Texture("MainPoint", new color(1, 1, 1, 0.8f), new color(1, 1, 1, 0.8f), true, 0, 0);
        string mainLinear = Texture("MainLinear", new color(1, 1, 1, 0.8f), new color(1, 1, 1, 0.8f), true, 0, 1);
        string clampedMask = Texture("ClampedMask", new color(0, 0, 0, 1), new color(1, 1, 1, 1), false, 1, 1);
        string pointMask = Texture("PointMask", new color(0, 0, 0, 1), new color(1, 1, 1, 1), false, 1, 0);
        string grayLinear = Texture("GrayLinear", new color(0.3f, 0.3f, 0.3f, 1), new color(0.3f, 0.3f, 0.3f, 1), false, 1, 0);
        string graySrgb = Texture("GraySrgb", new color(0.3f, 0.3f, 0.3f, 1), new color(0.3f, 0.3f, 0.3f, 1), true, 1, 0);
        using var package = UnityPackage.Open(prefab);
        var method = typeof(VrchatAvatar).Assembly.GetType("VrmToResonitePackage.Vrchat.VrchatMaterialBuilder")!
            .GetMethod("CompositeMainLayers", BindingFlags.Static | BindingFlags.NonPublic)!;
        Bitmap2D Bake(LilToonInfo info) => (Bitmap2D)method.Invoke(null,
            new object[] { package, info, LilToonMainTextureBakePlan.Create(info) })!;
        var failures = new List<string>();
        void Check(bool passed, string message)
        {
            Console.WriteLine((passed ? "PASS: " : "FAIL: ") + message);
            if (!passed) failures.Add(message);
        }
        var tiled = new LilToonInfo { MainTexGuid = stripeGuid, MainTexScale = new Vector2(2, 1),
            Main2nd = new() { Enabled = true, Color = Vector4.Zero } };
        var baked = Bake(tiled);
        Check(baked.Size.x >= 4 && Enumerable.Range(0, 4).All(x =>
            Math.Abs(baked.GetPixel(x * baked.Size.x / 4, 0).r - x % 2) < 0.01f),
            $"Tiled main retains black/white/black/white with transparent overlay (size {baked.Size})");
        tiled.MainTexScale = new Vector2(-2, 1);
        tiled.MainTexOffset = new Vector2(1, 0);
        baked = Bake(tiled);
        Check(baked.Size.x >= 4 && Enumerable.Range(0, 4).All(x =>
            Math.Abs(baked.GetPixel(x * baked.Size.x / 4, 0).r - (1 - x % 2)) < 0.01f),
            "Negative main tiling and offset preserve reversed stripes");
        foreach (bool third in new[] { false, true })
        {
            var rotated = new LilToonInfo();
            var layer = third ? rotated.Main3rd : rotated.Main2nd;
            layer.Enabled = true;
            layer.TextureGuid = stripeGuid;
            layer.Angle = MathF.PI / 2;
            baked = Bake(rotated);
            Check(baked.Size.y >= 2 && baked.GetPixel(0, 0).r > 0.99f &&
                baked.GetPixel(0, baked.Size.y - 1).r < 0.01f,
                $"Rotated rectangular {(third ? "3rd" : "2nd")} layer retains detail on the vertical axis (size {baked.Size})");
        }
        var masked = new LilToonInfo { MainTexScale = new Vector2(2, 1),
            Main2nd = new() { Enabled = true, Color = new Vector4(0, 0, 0, 1), MaskGuid = stripeGuid,
                Scale = new Vector2(100, 100) } };
        baked = Bake(masked);
        Check(baked.Size.x == 4 && baked.GetPixel(0, 0).r > 0.99f && baked.GetPixel(1, 0).r < 0.01f,
            "Layer blend mask density follows main UV, not the color-only layer transform");
        var alpha = new LilToonInfo { MainTexScale = new Vector2(2, 1),
            AlphaMaskGuid = stripeGuid, AlphaMaskMode = 1, AlphaMaskTexScale = new Vector2(3, 1) };
        baked = Bake(alpha);
        Check(baked.Size.x == 12 && Enumerable.Range(0, 12).All(x =>
            Math.Abs(baked.GetPixel(x, 0).a - x % 2) < 0.01f),
            "Alpha mask density includes both main and mask tiling");
        var adjusted = new LilToonInfo { MainTexScale = new Vector2(2, 1),
            MainTexHSVG = new Vector4(0, 1, 0, 1), MainColorAdjustMaskGuid = stripeGuid };
        baked = Bake(adjusted);
        Check(baked.Size.x == 4 && baked.GetPixel(0, 0).r > 0.99f && baked.GetPixel(1, 0).r < 0.01f,
            "Color-adjust mask density includes main tiling");
        tiled.MainTexScale = Vector2.One;
        tiled.MainTexOffset = Vector2.Zero;
        baked = Bake(tiled);
        Check(baked.Size == new int2(2, 1) && baked.GetPixel(0, 0).r < 0.01f && baked.GetPixel(1, 0).r > 0.99f,
            "Identity transforms preserve original resolution and pixels");
        foreach (float scale in new[] { 5000f, float.PositiveInfinity, float.NaN })
        {
            tiled.MainTexScale = new Vector2(scale, 1);
            bool rejected = false;
            try { Bake(tiled); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
            {
                rejected = true;
            }
            Check(rejected && tiled.MainTexGuid == stripeGuid && tiled.MainTexScale.X.Equals(scale),
                $"Unrepresentable density ({scale}) rejects the bake without mutating the source texture or transform");
        }
        foreach (var (mode, expected) in new[] { (1, 0.3f), (2, 0.12f), (3, 0.7f), (4, 0.1f) })
        {
            var tinted = new LilToonInfo { MainTexGuid = mainPoint, AlphaMode = "transparent",
                Color = new Vector4(0.2f, 0.4f, 0.6f, 0.5f), AlphaMaskGuid = grayLinear, AlphaMaskMode = mode };
            var alphaOnly = Bake(tinted).GetPixel(0, 0);
            var materialColor = LilToonMainTextureBakePlan.Create(tinted).MaterialColorAfterBake(tinted.Color);
            Check(materialColor == new Vector4(0.2f, 0.4f, 0.6f, 1) &&
                Math.Abs(alphaOnly.a * materialColor.W - expected) < 0.01f,
                $"Alpha mask mode {mode} retains RGB material tint and does not multiply alpha twice");
            tinted.Main2nd = new() { Enabled = true, Color = Vector4.Zero };
            var withLayer = Bake(tinted).GetPixel(0, 0);
            Check(LilToonMainTextureBakePlan.Create(tinted).MaterialColorAfterBake(tinted.Color) == Vector4.One,
                "Color bake resets the entire material tint");
            Check(Math.Abs(alphaOnly.a - expected) < 0.01f && Math.Abs(withLayer.a - expected) < 0.01f,
                $"Alpha mask mode {mode} applies tint before mask with or without an invisible layer (expected {expected}, got {alphaOnly.a}/{withLayer.a})");
            Check(alphaOnly.r > 0.99f && alphaOnly.g > 0.99f && alphaOnly.b > 0.99f,
                "Alpha-only bake leaves RGB tint for the material");
        }
        foreach (string kind in new[] { "alpha", "blend", "adjust" })
        {
            LilToonInfo Masked(string mainGuid, string maskGuid) => new()
            {
                MainTexGuid = mainGuid, MainTexScale = new Vector2(2, 2),
                AlphaMaskGuid = kind == "alpha" ? maskGuid : null, AlphaMaskMode = kind == "alpha" ? 1 : 0,
                Main2nd = new() { Enabled = kind == "blend", Color = new Vector4(0, 0, 0, 1), MaskGuid = maskGuid },
                MainTexHSVG = new Vector4(0, 1, kind == "adjust" ? 0 : 1, 1),
                MainColorAdjustMaskGuid = kind == "adjust" ? maskGuid : null,
            };
            float Value(color pixel) => kind == "alpha" ? pixel.a : pixel.r;
            var repeat = Bake(Masked(mainPoint, clampedMask));
            float blackMaskResult = kind == "alpha" ? 0 : 1;
            Check(Math.Abs(Value(repeat.GetPixel(2, 0)) - blackMaskResult) < 0.01f &&
                Math.Abs(Value(repeat.GetPixel(0, 2)) - blackMaskResult) < 0.01f,
                $"{kind} mask inherits main Repeat on both axes despite its own Clamp");
            foreach (bool point in new[] { false, true })
            {
                var filtered = Masked(point ? mainPoint : mainLinear, point ? clampedMask : pointMask);
                filtered.MainTexOffset = new Vector2(0.125f, 0);
                float maskValue = point ? 0 : 0.25f;
                float expected = kind == "alpha" ? maskValue :
                    1.055f * MathF.Pow(1 - maskValue, 1 / 2.4f) - 0.055f;
                float actual = Value(Bake(filtered).GetPixel(0, 0));
                Check(Math.Abs(actual - expected) < 0.01f,
                    $"{kind} mask inherits main {(point ? "Point" : "Bilinear")} filtering (expected {expected}, got {actual})");
            }
        }
        foreach (var (maskGuid, expected) in new[] { (grayLinear, 0.3f), (graySrgb, 0.0732f) })
        {
            float actual = Bake(new LilToonInfo { MainTexGuid = mainPoint,
                AlphaMaskGuid = maskGuid, AlphaMaskMode = 1 }).GetPixel(0, 0).a;
            Check(Math.Abs(actual - expected) < 0.01f, "Shared main sampler retains the mask's own color space");
        }
        if (failures.Count != 0) throw new Exception(string.Join("\n", failures));
    }
}
