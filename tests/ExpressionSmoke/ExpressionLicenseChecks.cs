using Elements.Core;
using FrooxEngine;
using FrooxEngine.Store;
using SkyFrost.Base;
using VrmToResonitePackage;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Vrm;
using Comment = FrooxEngine.Comment;

internal static class ExpressionLicenseChecks
{
    public static void Verify(Slot root)
    {
        Check(!root.GetComponentsInChildren<License>().Any(), "no generated License components");
        Check(!root.GetComponentsInChildren<Comment>().Any(comment =>
            comment.Slot.Name == "THIRD-PARTY-NOTICES" ||
            comment.Text.Value?.Contains("MIT License", StringComparison.Ordinal) == true),
            "no generated license comments");
        static bool HasNoticeSlot(Slot slot) => slot.Name == "THIRD-PARTY-NOTICES" || slot.Children.Any(HasNoticeSlot);
        Check(!HasNoticeSlot(root),
            "no generated notice slots");
    }

    public static async Task Run(Slot parent, string artifacts)
    {
        foreach (bool menu in new[] { false, true })
        {
            var avatar = parent.AddSlot("License fixture");
            Slot clone = null, restored = null;
            try
            {
                var field = avatar.AttachComponent<ValueField<float>>().Value;
                var model = new ExpressionModel();
                var clip = new ExpressionClip { Id = "smile", Name = "Smile", Duration = 1 };
                var curve = new ExpressionCurve { Binding = new("Face", "Smile") };
                curve.Keys.Add(new(0, 1, 0, 0)); clip.Curves.Add(curve); model.Clips.Add(clip);
                var expressions = await ExpressionSystemSetup.BuildAsync(avatar, model, _ => field, menu: menu);
                var hair = avatar.AddSlot("Hair");
                hair.AddSlot("HairTip").LocalPosition = new float3(0, 0.1f, 0);
                var vrm = new VrmModel();
                vrm.NodeNames.Add("Hair");
                var chain = new VrmSpringChain();
                chain.RootNodes.Add(0);
                vrm.SpringChains.Add(chain);
                SpringBoneSetup.Apply(avatar, vrm, new Dictionary<int, Slot> { [0] = hair });
                var mesh = avatar.AddSlot("Mesh").AttachComponent<MeshRenderer>();
                mesh.Mesh.Target = mesh.Slot.AttachComponent<QuadMesh>();
                await MeshLoadingSetup.Apply(avatar);
                for (int i = 0; i < 5; i++) await default(NextUpdate);
                Verify(avatar);
                Verify(expressions);
                clone = avatar.Duplicate(parent);
                Verify(clone.FindChild("Expressions"));
                Verify(clone);
                var graph = avatar.SaveObject(DependencyHandling.CollectAssets);
                var record = RecordHelper.CreateForObject<SkyFrost.Base.Record>(avatar.Name, avatar.World.LocalUser.MachineID, null);
                string package = Path.Combine(artifacts, $"License-{menu}.resonitepackage");
                var engine = avatar.Engine;
                await default(ToBackground);
                using (var output = File.Create(package))
                    await PackageCreator.BuildPackage(engine, record, graph, output, includeVariants: false);
                await default(ToWorld);
                Check(PackageInspector.Inspect(package, verbose: false) == 0, "package inspection");
                restored = parent.AddSlot("Restored license fixture");
                await PackageImporter.ImportPackage(package, restored);
                await default(ToWorld);
                Verify(restored.FindChild("Expressions"));
                Verify(restored);
                Console.WriteLine($"LICENSE: menu={menu}: generation, clone, package inspection and reload contain no generated license annotations");
            }
            finally { restored?.Destroy(); clone?.Destroy(); avatar.Destroy(); }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("License: " + message);
    }
}
