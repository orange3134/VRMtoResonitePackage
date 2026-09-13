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
        if (failures.Count != 0) throw new Exception(string.Join("\n", failures));
    }
}
