using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.CommonAvatar;
using FrooxEngine.ProtoFlux;
using FrooxEngine.Store;
using SkyFrost.Base;
using VrmToResonitePackage.Expressions;
using static ExpressionTestFields;

internal static class ExpressionMeshDriverChecks
{
    public static async Task Run(Slot parent, string artifacts)
    {
        var avatar = parent.AddSlot("Mesh driver regression");
        Slot clone = null, restored = null;
        try
        {
            var first = await Mesh(avatar.AddSlot("First"), new[] { "ActualSmile", "ActualBlink", "Untouched" });
            var second = await Mesh(avatar.AddSlot("Second"), new[] { "ActualBlink", "ActualSmile", "Untouched" });
            first.GetBlendShape("Untouched").Value = 0.37f; second.GetBlendShape("Untouched").Value = 0.63f;
            second.GetBlendShape("ActualBlink").Value = 0.21f;
            var manager = avatar.AttachComponent<EyeManager>();
            manager.MinBlinkInterval.Value = manager.MaxBlinkInterval.Value = 1000000;
            var eyeDriver = avatar.AttachComponent<EyeLinearDriver>(); eyeDriver.EyeManager.Target = manager;
            eyeDriver.Eyes.Add().OpenCloseTarget.Target = first.GetBlendShape("ActualBlink");
            var smileA = new ExpressionBinding("First/Face", "0");
            var smileB = new ExpressionBinding("Second/Face", "1");
            var blinkA = new ExpressionBinding("First/Face", "1");
            var targets = new Dictionary<ExpressionBinding, IField<float>> {
                [smileA] = first.GetBlendShape("ActualSmile"), [smileB] = second.GetBlendShape("ActualSmile"),
                [blinkA] = first.GetBlendShape("ActualBlink") };
            var model = new ExpressionModel();
            void Clip(string name, params (ExpressionBinding Binding, float Value)[] values)
            {
                var clip = new ExpressionClip { Id = name, Name = name, Duration = 1 };
                foreach (var (binding, value) in values)
                {
                    var curve = new ExpressionCurve { Binding = binding };
                    curve.Keys.Add(new(0, value, 0, 0)); clip.Curves.Add(curve);
                }
                model.Clips.Add(clip);
            }
            Clip("First pose", (smileA, 0.2f), (smileB, 0.8f));
            // The blink entry is added after BuildCatalog has awaited the first clip asset.
            Clip("Second pose", (smileA, 0.7f), (smileB, 0.4f), (blinkA, 0.3f));
            await ExpressionSystemSetup.BuildAsync(avatar, model, b => targets[b], initialWeight: _ => 0f);
            await Frames(90); await Verify(avatar);
            await CheckSmoothingSpeed(avatar, 10f, 37f);
            clone = avatar.Duplicate(parent); await Frames(90); await Verify(clone);
            await CheckSmoothingSpeed(clone, 37f, 53f);
            CheckSpeeds(avatar, 37f);
            var graph = avatar.SaveObject(DependencyHandling.CollectAssets);
            var record = RecordHelper.CreateForObject<SkyFrost.Base.Record>(avatar.Name, avatar.World.LocalUser.MachineID, null);
            var engine = avatar.Engine;
            string package = Path.Combine(artifacts, "MeshDrivers.resonitepackage");
            await default(ToBackground);
            using (var stream = File.Create(package)) await PackageCreator.BuildPackage(engine, record, graph, stream, includeVariants: false);
            await default(ToWorld);
            restored = parent.AddSlot("Restored mesh drivers");
            await PackageImporter.ImportPackage(package, restored); await default(ToWorld); await Frames(120);
            await Verify(restored);
            await CheckSmoothingSpeed(restored, 37f, 71f);
            CheckSpeeds(avatar, 37f); CheckSpeeds(clone, 53f);
        }
        finally { restored?.Destroy(); clone?.Destroy(); avatar.Destroy(); }
        Console.WriteLine("MESH DRIVERS: per-renderer grouping, numeric bindings, late entries, DynamicField reads, blink, shared speed edits, clone isolation and reload passed");
    }

    private static void CheckSpeeds(Slot avatar, float expected)
    {
        var smoothers = avatar.FindChild("Expressions").GetComponentsInChildren<SmoothValue<float>>();
        Check(smoothers.Count == 3, "speed check covers both renderers and tracked blink output");
        foreach (var smooth in smoothers)
        {
            Near(smooth.Speed.Value, expected, "all mesh SmoothValues follow this avatar's shared speed");
            var driver = smooth.Slot.GetComponent<DynamicValueVariableDriver<float>>();
            Check(driver?.VariableName.Value == "ExpressionSystem/SmoothingSpeed" && driver.Target.Target == smooth.Speed &&
                driver.Target.IsLinkValid, "shared speed uses a bound native DynamicVariableDriver");
        }
    }

    private static async Task CheckSmoothingSpeed(Slot avatar, float initial, float updated)
    {
        var expressions = avatar.FindChild("Expressions");
        var speed = expressions.GetComponentsInChildren<DynamicValueVariable<float>>()
            .Single(v => v.VariableName.Value == "ExpressionSystem/SmoothingSpeed");
        Check(speed.Slot == expressions.FindChild("DV").FindChild("SmoothingSpeed"), "one shared speed is editable in Expressions/DV");
        Near(speed.Value.Value, initial, "shared speed retains its value across clone/reload");
        CheckSpeeds(avatar, initial);
        speed.Value.Value = updated;
        await Frames(3);
        CheckSpeeds(avatar, updated);
    }

    private static async Task<SkinnedMeshRenderer> Mesh(Slot parent, string[] shapes)
    {
        var mesh = new MeshX();
        mesh.AddVertex(new float3(0, 0, 0)); mesh.AddVertex(new float3(1, 0, 0)); mesh.AddVertex(new float3(0, 1, 0));
        mesh.AddSubmesh<TriangleSubmesh>().AddTriangle(0, 1, 2);
        foreach (string name in shapes) mesh.AddBlendShape(name).AddFrame(1).SetPositionDelta(0, new float3(0, 0, 0.1f));
        var provider = parent.AttachComponent<StaticMesh>();
        provider.URL.Value = await parent.Engine.LocalDB.SaveAssetAsync(mesh); await default(ToWorld);
        var renderer = parent.AddSlot("Face").AttachComponent<SkinnedMeshRenderer>(); renderer.Mesh.Target = provider;
        for (int i = 0; i < 7200 && renderer.BlendShapeWeights.Count != shapes.Length; i++) await default(NextUpdate);
        Check(renderer.BlendShapeWeights.Count == shapes.Length, "synthetic mesh loads its blendshapes");
        return renderer;
    }

    private static async Task Verify(Slot avatar)
    {
        EquipAvatar(avatar);
        await Frames(30);
        var expressions = avatar.FindChild("Expressions"); var core = expressions.FindChild("Internal");
        var catalog = expressions.FindChild("Catalog"); var table = expressions.FindChild("DV").FindChild("GestureTable");
        var first = avatar.FindChild("First").FindChild("Face").GetComponent<SkinnedMeshRenderer>();
        var second = avatar.FindChild("Second").FindChild("Face").GetComponent<SkinnedMeshRenderer>();
        var manager = avatar.GetComponent<EyeManager>();
        var eye = avatar.GetComponent<EyeLinearDriver>().Eyes[0];
        Slot Output(IField<float> target) => expressions.FindChild("Outputs").Children.Single(o => OutputTarget(o) == target);
        var smile = Output(first.GetBlendShape("ActualSmile")); var blink = Output(first.GetBlendShape("ActualBlink"));
        string Id(Slot output) => output.ExpressionVariables<DynamicValueVariable<string>>()
            .Single(v => v.VariableName.Value == "ExpressionSystem.Output/Id").Value.Value;
        Check(Id(smile) == "Face.ActualSmile" && Id(Output(second.GetBlendShape("ActualSmile"))) == "Face.ActualSmile.2" &&
            Id(blink) == "Face.ActualBlink", "keys use actual mesh shape names and distinguish same-named renderers");
        var baseValue = blink.ExpressionVariables<DynamicValueVariable<float>>().Single(v => v.VariableName.Value == "ExpressionSystem.Output/Base").Value;
        Check(eye.OpenCloseTarget.Target == baseValue && eye.OpenCloseTarget.IsLinkValid, "blink still drives its independent Base");
        var dynamicResult = smile.ExpressionVariables<DynamicField<float>>().Single(v => v.VariableName.Value == "ExpressionSystem.Output/Result");
        Set(table, "L0R1", catalog.FindChild("First pose")); Set(table, "L0R2", catalog.FindChild("Second pose"));
        void Select(int index) => Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(
            expressions.FindChild("API").FindChild("Receivers"), ExpressionSystemSetup.RightTag, true, index) == 1, "select mesh test pose");
        manager.LeftEyeCloseOverride.Value = manager.RightEyeCloseOverride.Value = 0.1f;
        Select(1); await Frames();
        Near(first.GetBlendShapeWeight("ActualSmile"), 0.2f, "first renderer drives its shape");
        Near(second.GetBlendShapeWeight("ActualSmile"), 0.8f, "same shape and slot names on second renderer remain independent");
        Near(first.GetBlendShapeWeight("ActualBlink"), 0.1f, "missing animation track follows blink Base");
        var smoother = (SmoothValue<float>)dynamicResult.TargetField.Target.Parent;
        var sharedSpeed = expressions.FindChild("DV").FindChild("SmoothingSpeed").GetComponent<DynamicValueVariable<float>>();
        float configuredSpeed = sharedSpeed.Value.Value;
        // Slow this transition so a long headless frame cannot skip every intermediate sample.
        sharedSpeed.Value.Value = 1f; await Frames(3); CheckSpeeds(avatar, 1f);
        float before = first.GetBlendShapeWeight("ActualSmile");
        Select(2);
        Near(smoother.TargetValue.Value, 0.7f, "selection immediately writes SmoothValue target");
        Near(first.GetBlendShapeWeight("ActualSmile"), before, "selection does not jump the actual mesh weight");
        bool intermediate = false;
        for (int i = 0; i < 90; i++)
        {
            await default(NextUpdate);
            float value = first.GetBlendShapeWeight("ActualSmile");
            if (value > before + 0.00001f && value < 0.7f - 0.00001f) { intermediate = true; break; }
        }
        Check(intermediate, "mesh traverses intermediate weights");
        before = first.GetBlendShapeWeight("ActualSmile");
        Select(1);
        Near(smoother.TargetValue.Value, 0.2f, "rapid selection replaces target mid-transition");
        Near(first.GetBlendShapeWeight("ActualSmile"), before, "retarget does not restart with a discontinuity");
        sharedSpeed.Value.Value = configuredSpeed;
        await Frames(); Near(first.GetBlendShapeWeight("ActualSmile"), 0.2f, "retarget reaches latest pose");
        Select(2); await Frames();
        Near(first.GetBlendShapeWeight("ActualSmile"), 0.7f, "mesh receives changed animation");
        Near(second.GetBlendShapeWeight("ActualSmile"), 0.4f, "second mesh receives changed animation");
        Near(first.GetBlendShapeWeight("ActualBlink"), 0.3f, "entry appended after asset await is linked");
        Near(dynamicResult.DynamicValue, 0.7f, "DynamicField Result exposes the smoothing target");
        Check(smile.GetComponent<DynamicVariableSpace>().TryReadValue<float>("Result", out var read), "Result is readable through the dynamic variable space");
        Near(read, 0.7f, "dynamic variable reads remain current after Drive");
        manager.LeftEyeCloseOverride.Value = manager.RightEyeCloseOverride.Value = 0.9f; await Frames();
        Near(first.GetBlendShapeWeight("ActualBlink"), 0.9f, "native mesh driver and eye blink compose");
        Select(1);
        Near(dynamicResult.DynamicValue, 0.2f, "Selection immediately writes the new DynamicField value");
        await Frames();
        Near(first.GetBlendShapeWeight("Untouched"), 0.37f, "unused first-mesh shape retains its weight");
        Near(second.GetBlendShapeWeight("Untouched"), 0.63f, "unused second-mesh shape retains its weight");
        Near(second.GetBlendShapeWeight("ActualBlink"), 0.21f, "same-name unused shape on another renderer stays untouched");
        ExpressionGraphChecks.CheckLayout(expressions);
    }

    private static void Set<T>(Slot slot, string name, T value) => Check(slot.WriteDynamicVariable(VariablePath(slot, name), value) == DynamicVariableWriteResult.Success, "write " + name);
    private static async Task Frames(int count = 90) { for (int i = 0; i < count; i++) await default(NextUpdate); }
    private static void Near(float actual, float expected, string message) => Check(Math.Abs(actual - expected) < 0.005f, $"{message}: {actual} ~= {expected}");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
