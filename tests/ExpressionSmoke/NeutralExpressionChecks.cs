using FrooxEngine;
using FrooxEngine.CommonAvatar;
using FrooxEngine.ProtoFlux;
using FrooxEngine.Store;
using SkyFrost.Base;
using VrmToResonitePackage.Expressions;
using static ExpressionTestFields;

internal static class NeutralExpressionChecks
{
    public static async Task Run(Slot parent, string artifacts)
    {
        var avatar = parent.AddSlot("Both hands neutral defaults");
        Slot restored = null;
        try
        {
            var mesh = await ExpressionMeshDriverChecks.Mesh(avatar, new[] { "Mouth", "Cheek", "Blink", "Untouched" });
            mesh.GetBlendShape("Untouched").Value = .37f;
            var manager = avatar.AttachComponent<EyeManager>();
            manager.MinBlinkInterval.Value = manager.MaxBlinkInterval.Value = 1000000;
            var eyeDriver = avatar.AttachComponent<EyeLinearDriver>(); eyeDriver.EyeManager.Target = manager;
            var eye = eyeDriver.Eyes.Add(); eye.Side.Value = EyeSide.Left;
            eye.OpenCloseTarget.Target = mesh.GetBlendShape("Blink");
            var model = new ExpressionModel { DetectedExpressions = new(), GestureTable = new string[8, 8] };
            ExpressionClip Clip(string name, params (string Shape, float Value)[] values)
            {
                var clip = new ExpressionClip { Id = name, Name = name, Duration = 1 };
                foreach (var (shape, value) in values)
                {
                    var curve = new ExpressionCurve { Binding = new("Face", shape) };
                    curve.Keys.Add(new(0, value, 0, 0)); clip.Curves.Add(curve);
                }
                return clip;
            }
            model.Clips.Add(Clip("NeutralPose", ("Mouth", 1), ("Cheek", 0), ("Blink", .35f)));
            model.Clips.Add(Clip("Other", ("Mouth", .4f), ("Cheek", 0), ("Blink", 0)));
            model.Clips.Add(Clip("Manual", ("Mouth", 0), ("Cheek", .7f), ("Blink", 0)));
            model.Clips.Add(Clip("Sparse", ("Cheek", .6f)));
            // Detection against source zeros omitted these explicit resets. Export
            // must recover them from the full clip after adopting the neutral pose.
            model.DetectedExpressions.Add(Clip("Manual", ("Cheek", .7f)));
            model.DetectedExpressions.Add(Clip("Sparse", ("Cheek", .6f)));
            model.GestureTable[0, 0] = "NeutralPose";
            model.GestureTable[1, 0] = "Other";
            model.GestureTable[2, 0] = "NeutralPose";
            await ExpressionSystemSetup.BuildAsync(avatar, model, b => mesh.GetBlendShape(b.Shape), initialWeight: _ => 0);
            await Verify(avatar);
            var graph = avatar.SaveObject(DependencyHandling.CollectAssets);
            var record = RecordHelper.CreateForObject<SkyFrost.Base.Record>(avatar.Name, avatar.World.LocalUser.MachineID, null);
            string package = Path.Combine(artifacts, "NeutralDefaults.resonitepackage");
            var engine = avatar.Engine;
            await default(ToBackground);
            using (var stream = File.Create(package)) await PackageCreator.BuildPackage(engine, record, graph, stream, includeVariants: false);
            await default(ToWorld);
            restored = parent.AddSlot("Restored neutral defaults");
            await PackageImporter.ImportPackage(package, restored); await default(ToWorld);
            await Frames(120); await Verify(restored);
        }
        finally { restored?.Destroy(); avatar.Destroy(); }
        Console.WriteLine("NEUTRAL DEFAULTS: load, wear, gestures, sparse menu reset curves, blink mixing, reset and package reload passed");
    }

    private static async Task Verify(Slot avatar)
    {
        var mesh = avatar.FindChild("Face").GetComponent<SkinnedMeshRenderer>();
        var root = avatar.FindChild("Expressions"); var api = root.FindChild("API");
        var manager = avatar.GetComponentInChildren<EyeManager>();
        manager.LeftEyeCloseOverride.Value = 0;
        root.WriteDynamicVariable("ExpressionSystem/Config.SmoothingSpeed", 1000f);
        await Frames(40);
        Near("Mouth", 1); Near("Blink", .35f); Near("Untouched", .37f);
        EquipAvatar(avatar); await Frames(40); Near("Mouth", 1); Near("Blink", .35f);
        Check(root.FindChild("Clips").FindChild("NeutralPose") == null, "neutral pose is the default, not a menu entry");
        var table = root.FindChild("DV").FindChild("GestureTable");
        Check(Reference<Slot>(table, "L0R0") == null && Reference<Slot>(table, "L2R0") == null, "neutral aliases release to default");
        void Left(int value) => ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
            ExpressionSystemSetup.SetGestureLeftTag, true, value);
        Left(1); await Frames(40); Near("Mouth", .4f); Near("Blink", 0);
        Left(0); await Frames(40); Near("Mouth", 1); Near("Blink", .35f);
        manager.LeftEyeCloseOverride.Value = 1; await Frames(10); Near("Blink", 1);
        manager.LeftEyeCloseOverride.Value = 0; await Frames(10); Near("Blink", .35f);
        var manual = root.FindChild("Clips").FindChild("Manual");
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.MenuSelectTag, true, manual);
        await Frames(40); Near("Mouth", 0); Near("Cheek", .7f); Near("Blink", 0);
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, ExpressionSystemSetup.ResetTag, true);
        await Frames(40); Near("Mouth", 1); Near("Cheek", 0); Near("Blink", .35f); Near("Untouched", .37f);
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.MenuSelectTag, true,
            root.FindChild("Clips").FindChild("Sparse"));
        await Frames(40); Near("Mouth", 0); Near("Cheek", .6f); Near("Blink", 0);
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, ExpressionSystemSetup.ResetTag, true);
        await Frames(40); Near("Mouth", 1); Near("Cheek", 0); Near("Blink", .35f);
        void Near(string name, float expected) => Check(float.IsFinite(mesh.GetBlendShape(name).Value) &&
            Math.Abs(mesh.GetBlendShape(name).Value - expected) < .005f, $"{name}: {mesh.GetBlendShape(name).Value} != {expected}");
    }

    private static async Task Frames(int count) { for (int i = 0; i < count; i++) await default(NextUpdate); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
