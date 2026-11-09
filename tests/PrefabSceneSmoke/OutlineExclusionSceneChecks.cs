using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class OutlineExclusionSceneChecks
{
    public static async Task Run(Slot parent, string temp)
    {
        string folder = Path.Combine(temp, "OutlineFixture");
        Directory.CreateDirectory(Path.Combine(folder, "Assets"));
        Directory.CreateDirectory(Path.Combine(folder, "ProjectSettings"));
        string prefab = Path.Combine(folder, "Assets/Test.prefab");
        File.WriteAllText(prefab, "%YAML 1.1\n");
        File.WriteAllText(prefab + ".meta", "guid: 11223344556677889900112233445566");
        const string maskGuid = "22334455667788990011223344556677";
        var bitmap = new Bitmap2D(2, 2, TextureFormat.RGBA32, false, ColorProfile.Linear);
        bitmap.SetPixel(0, 0, new color(0, 0, 0, 1)); bitmap.SetPixel(1, 0, new color(1, 1, 1, 1));
        bitmap.SetPixel(0, 1, new color(1, 1, 1, 1)); bitmap.SetPixel(1, 1, new color(1, 1, 1, 1));
        string path = Path.Combine(folder, "Assets/Mask.png");
        if (!bitmap.Save(path)) throw new Exception("Cannot save outline fixture");
        File.WriteAllText(path + ".meta", $"guid: {maskGuid}\nTextureImporter:\n  textureSettings:\n    filterMode: 0\n    wrapU: 1\n");
        using var package = UnityPackage.Open(prefab);
        Slot root = parent.AddSlot("Outline exclusion regression"), assets = root.AddSlot("Assets");
        var material = assets.AddSlot("Outlined").AttachComponent<XiexeToonMaterial>();
        material.Outline.Value = XiexeToonMaterial.OutlineStyle.Lit;
        material.OutlineWidth.Value = 0.15f;
        material.Color.Value = new colorX(0.3f, 0.4f, 0.5f, 1, ColorProfile.Linear);
        var controlMaterial = assets.AddSlot("Control").AttachComponent<XiexeToonMaterial>();
        controlMaterial.Outline.Value = XiexeToonMaterial.OutlineStyle.Lit;
        var mesh = new MeshX { HasUV0s = true };
        for (int i = 0; i < 6; i++)
        {
            mesh.AddVertex(new float3(i % 3, i / 3, 0));
            mesh.RawUV0s[i] = new float2(i < 3 ? 0.75f : 0.25f, 0.75f);
        }
        mesh.AddSubmesh<TriangleSubmesh>().AddTriangle(0, 1, 2);
        ((TriangleSubmesh)mesh.GetSubmesh(0)).AddTriangle(3, 4, 5);
        mesh.AddBlendShape("Eye").AddFrame(1).RawPositions[0] = new float3(0, 0, 1);
        var provider = assets.AddSlot("Shared mesh").AttachComponent<StaticMesh>();
        var engine = root.Engine;
        await default(ToBackground);
        var uri = await engine.LocalDB.SaveAssetAsync(mesh).ConfigureAwait(false);
        await default(ToWorld);
        provider.URL.Value = uri;
        var first = Skin("First", material); var second = Skin("Second", material);
        var control = Skin("Control", controlMaterial);
        for (int i = 0; i < 7200 && !provider.IsAssetAvailable; i++) await default(NextUpdate);
        if (!provider.IsAssetAvailable) throw new Exception("Outline source mesh not loaded");
        var info = new LilToonInfo { OutlineDeleteMesh = true, OutlineWidth = 0.15f,
            OutlineMaskGuid = maskGuid, MainTexOffset = new System.Numerics.Vector2(0.5f, 0) };
        await VrchatMaterialBuilder.ApplyOutlineExclusions(root, assets, package,
            new Dictionary<XiexeToonMaterial, LilToonInfo> { [material] = info });
        Check(first.Materials.Count == 2 && second.Materials.Count == 2 && control.Materials.Count == 1 &&
            control.Mesh.Target == provider && provider.Asset.Data.SubmeshCount == 1,
            "outline exclusions isolate mesh providers and preserve an unrelated shared mesh/material user");
        var plain = first.Materials[1] as XiexeToonMaterial;
        Check(plain != null && plain.Outline.Value == XiexeToonMaterial.OutlineStyle.None &&
            plain.Color.Value == material.Color.Value && plain.Culling.Value == material.Culling.Value &&
            second.Materials[1] == plain && material.Outline.Value == XiexeToonMaterial.OutlineStyle.Lit,
            "outline-free material preserves surface properties and is reused without changing the original");
        Check(first.Mesh.Asset.Data.GetSubmesh(1).RawIndicies.Take(3).SequenceEqual(new[] { 0, 1, 2 }) &&
            first.Mesh.Asset.Data.BlendShapes.Single().Frames.Single().RawPositions[0] == new float3(0, 0, 1),
            "outline mask sampling honors bottom-left UVs, main transform and repeat, with blendshapes preserved after asset reload");
        // No mask means shader white; width zero still discards the outline of the whole submesh.
        info.OutlineMaskGuid = null; info.OutlineWidth = 0;
        await VrchatMaterialBuilder.ApplyOutlineExclusions(root, assets, package,
            new Dictionary<XiexeToonMaterial, LilToonInfo> { [controlMaterial] = info });
        Check(control.Mesh.Target == provider && control.Materials.Count == 1 &&
            ((XiexeToonMaterial)control.Materials[0]).Outline.Value == XiexeToonMaterial.OutlineStyle.None,
            "fully excluded submesh needs no geometry rewrite");

        SkinnedMeshRenderer Skin(string name, XiexeToonMaterial mat)
        {
            var skin = root.AddSlot(name).AttachComponent<SkinnedMeshRenderer>();
            skin.Mesh.Target = provider; skin.Materials.Add(mat); return skin;
        }
        root.Destroy();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
