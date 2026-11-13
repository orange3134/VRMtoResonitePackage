using System.Reflection;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.CommonAvatar;
using FrooxEngine.ProtoFlux;
using FrooxEngine.Store;
using Renderite.Shared;
using SkyFrost.Base;
using VrmToResonitePackage;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Vrchat;
using static ExpressionTestFields;

internal static class StartupBlinkChecks
{
    public static async Task Run(Slot parent, string artifacts)
    {
        var avatar = parent.AddSlot("Startup blink");
        Slot restored = null;
        try
        {
            var face = await ExpressionMeshDriverChecks.Mesh(avatar, new[] { "blink_L", "blink_R", "blink", "Mouth.Open", "V.AA", "Untouched" });
            face.GetBlendShape("blink").Value = 1;
            face.GetBlendShape("Mouth.Open").Value = 1;
            face.GetBlendShape("V.AA").Value = .2f;
            face.GetBlendShape("Untouched").Value = .37f;
            var clothes = await ExpressionMeshDriverChecks.Mesh(avatar, new[] { "Mouth.Open" });
            clothes.Slot.Name = "Clothes";
            clothes.GetBlendShape("Mouth.Open").Value = .62f;
            var source = new VrchatAvatar { Blink = new VrchatBlink { MeshGameObjectPath = "Face",
                MeshGameObjectName = "Face", BlendShapeName = "blink_L", Preset = "blinkLeft" }, ExpressionFacePath = "Face" };
            source.AdditionalBlinks.Add(new VrchatBlink { MeshGameObjectPath = "Face", MeshGameObjectName = "Face",
                BlendShapeName = "blink_R", Preset = "blinkRight" });
            foreach (string shape in new[] { "blink_L", "blink_R", "blink" }) source.BlinkInitialValues.Add(new("Face", shape), 0);
            source.BaseExpressionInitialValues.Add(new("Face", "Mouth.Open"), 0);
            source.BaseExpressionInitialValues.Add(new("Clothes", "Mouth.Open"), 0);
            source.BaseExpressionInitialValues.Add(new("Face", "V.AA"), 0);
            source.ExpressionVisemeNames.Add("V.AA");
            var model = VrchatModelAdapter.ToVrmModel(source);
            var resolver = new BlendshapeResolver(avatar, model);
            var initialValues = resolver.ApplyBaseExpressionDefaults(source.BaseExpressionInitialValues);
            Check(initialValues.Count == 1, "only the actual face non-viseme default is applied");
            foreach (var entry in source.BlinkInitialValues) initialValues[entry.Key] = entry.Value;
            resolver.ApplyInitialOverrides(source.BlinkInitialValues, updateBaseline: true);
            Near(resolver.InitialWeight(face.GetBlendShape("blink")).Value, 0, "reset precedes baseline capture");
            Near(resolver.InitialWeight(face.GetBlendShape("Mouth.Open")).Value, 0, "base-layer neutral precedes baseline capture");
            var rig = avatar.AttachComponent<BipedRig>();
            var head = avatar.AddSlot("Head"); rig[BodyNode.Head] = head;
            typeof(AvatarSetup).GetMethod("SetupEyesAndBlink", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { avatar, rig, model, head, new Dictionary<string, Slot>(), resolver });
            // The final prefab pass can reapply the FBX value after tracking setup.
            face.GetBlendShape("blink").Value = 1;
            face.GetBlendShape("Mouth.Open").Value = 1;
            resolver.ApplyInitialOverrides(initialValues);
            var expression = new ExpressionClip { Id = "closed", Name = "Closed", Duration = 1 };
            foreach (string shape in new[] { "blink_L", "blink_R", "blink", "Mouth.Open" })
            {
                var curve = new ExpressionCurve { Binding = new("Face", shape) };
                curve.Keys.Add(new(0, shape is "blink" or "Mouth.Open" ? 1 : 0, 0, 0)); expression.Curves.Add(curve);
            }
            source.Expressions.Clips.Add(expression);
            await ExpressionSystemSetup.BuildAsync(avatar, source.Expressions, resolver.ResolveExpression, initialWeight: resolver.InitialWeight);
            await Verify(avatar);
            var graph = avatar.SaveObject(DependencyHandling.CollectAssets);
            var record = RecordHelper.CreateForObject<SkyFrost.Base.Record>(avatar.Name, avatar.World.LocalUser.MachineID, null);
            string package = Path.Combine(artifacts, "StartupBlink.resonitepackage");
            var engine = avatar.Engine;
            await default(ToBackground);
            using (var stream = File.Create(package)) await PackageCreator.BuildPackage(engine, record, graph, stream, includeVariants: false);
            await default(ToWorld);
            restored = parent.AddSlot("Restored startup blink");
            await PackageImporter.ImportPackage(package, restored); await default(ToWorld);
            await Frames(120); await Verify(restored);
        }
        finally { restored?.Destroy(); avatar.Destroy(); }
        Console.WriteLine("STARTUP BLINK: adapter, native eye setup, initial reset, independent eyes, expression release and package reload passed");
    }

    private static async Task Verify(Slot avatar)
    {
        var face = avatar.FindChild("Face").GetComponent<SkinnedMeshRenderer>();
        var driver = avatar.GetComponentInChildren<EyeLinearDriver>();
        var manager = driver.EyeManager.Target;
        manager.MinBlinkInterval.Value = manager.MaxBlinkInterval.Value = 1000000;
        manager.LeftEyeCloseOverride.Value = manager.RightEyeCloseOverride.Value = 0;
        EquipAvatar(avatar); await Frames(40);
        var root = avatar.FindChild("Expressions"); var api = root.FindChild("API");
        Check(driver.Eyes.Count == 2 && driver.Eyes.Any(e => e.Side.Value == EyeSide.Left) &&
            driver.Eyes.Any(e => e.Side.Value == EyeSide.Right) && driver.Eyes.All(e => e.OpenCloseTarget.IsLinkValid), "both native eye links");
        Near(face.GetBlendShape("blink").Value, 0, "combined blink opens initially");
        Near(face.GetBlendShape("Mouth.Open").Value, 0, "base-layer neutral closes mouth initially");
        Near(face.GetBlendShape("blink_L").Value, 0, "left opens initially");
        Near(face.GetBlendShape("blink_R").Value, 0, "right opens initially");
        manager.LeftEyeCloseOverride.Value = 1; await Frames(10);
        Near(face.GetBlendShape("blink_L").Value, 1, "left closes"); Near(face.GetBlendShape("blink_R").Value, 0, "right stays open");
        manager.LeftEyeCloseOverride.Value = 0; manager.RightEyeCloseOverride.Value = 1; await Frames(10);
        Near(face.GetBlendShape("blink_L").Value, 0, "left reopens"); Near(face.GetBlendShape("blink_R").Value, 1, "right closes");
        manager.RightEyeCloseOverride.Value = 0;
        root.WriteDynamicVariable("ExpressionSystem/Config.SmoothingSpeed", 1000f);
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, "ExpressionSystem/Internal.ApplyExpression", true, root.FindChild("Clips").FindChild("Closed"));
        await Frames(60); Near(face.GetBlendShape("blink").Value, 1, "expression can close combined shape");
        Near(face.GetBlendShape("Mouth.Open").Value, 1, "expression can open mouth");
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, ExpressionSystemSetup.ResetTag, true);
        await Frames(60); Near(face.GetBlendShape("blink").Value, 0, "release restores loop reset instead of FBX closed value");
        Near(face.GetBlendShape("Mouth.Open").Value, 0, "release restores FX neutral instead of prefab open mouth");
        Near(face.GetBlendShape("Untouched").Value, .37f, "unrelated authored shape preserved");
        Near(face.GetBlendShape("V.AA").Value, .2f, "descriptor viseme is not reset by base expression");
        Near(avatar.FindChild("Clothes").GetComponent<SkinnedMeshRenderer>().GetBlendShape("Mouth.Open").Value, .62f,
            "namesake clothing shape remains unchanged");
    }

    private static async Task Frames(int count) { for (int i = 0; i < count; i++) await default(NextUpdate); }
    private static void Near(float actual, float expected, string message) => Check(Math.Abs(actual - expected) < .005f, $"{message}: {actual} != {expected}");
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
