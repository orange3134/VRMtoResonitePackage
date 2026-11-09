using System.Text;
using System.Text.Json;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.CommonAvatar;
using FrooxEngine.ProtoFlux;
using FrooxEngine.Store;
using SkyFrost.Base;
using VrmToResonitePackage;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Vrm;
using static ExpressionTestFields;

internal static class VrmExpressionChecks
{
    private static readonly string[] Shapes = { "Rest", "Smile", "Anger", "Sadness", "Relax", "Surprise", "Blink", "Custom", "TrackingOnly" };
    private static readonly string[] Presets = { "neutral", "happy", "angry", "sad", "relaxed", "surprised" };

    public static async Task Run(Slot parent, string artifacts)
    {
        foreach (var (version, sparse) in new[] { (0, false), (0, true), (1, false), (1, true) })
        {
            string name = $"VRM{version}-{(sparse ? "missing" : "full")}";
            var vrm = Fixture(Path.Combine(artifacts, name + ".vrm"), version, sparse);
            var avatar = parent.AddSlot(name);
            Slot restored = null;
            try
            {
                var mesh = new MeshX();
                mesh.AddVertex(new float3(0, 0, 0)); mesh.AddVertex(new float3(1, 0, 0)); mesh.AddVertex(new float3(0, 1, 0));
                mesh.AddSubmesh<TriangleSubmesh>().AddTriangle(0, 1, 2);
                foreach (string shape in Shapes) mesh.AddBlendShape(shape).AddFrame(1).SetPositionDelta(0, new float3(0, 0, 0.1f));
                var provider = avatar.AttachComponent<StaticMesh>();
                provider.URL.Value = await avatar.Engine.LocalDB.SaveAssetAsync(mesh); await default(ToWorld);
                var face = avatar.AddSlot("Face").AttachComponent<SkinnedMeshRenderer>(); face.Mesh.Target = provider;
                for (int i = 0; i < 7200 && face.BlendShapeWeights.Count != Shapes.Length; i++) await default(NextUpdate);
                Check(face.BlendShapeWeights.Count == Shapes.Length, "VRM fixture mesh loaded");
                face.GetBlendShape("Rest").Value = 0.12f;
                var resolver = new BlendshapeResolver(avatar, vrm);
                var eyes = avatar.AttachComponent<EyeManager>();
                eyes.MinBlinkInterval.Value = eyes.MaxBlinkInterval.Value = 1000000;
                eyes.LeftEyeCloseOverride.Value = 0;
                var driver = avatar.AttachComponent<EyeLinearDriver>(); driver.EyeManager.Target = eyes;
                var eye = driver.Eyes.Add(); eye.Side.Value = EyeSide.Left;
                eye.OpenCloseTarget.Target = face.GetBlendShape("Blink");
                var root = await VrmExpressionAdapter.BuildAsync(avatar, vrm, resolver, menu: true);
                await Frames(30);
                await Verify(avatar, vrm, version, sparse);
                string snapshot = ExpressionPackageSnapshot.Capture(root, Path.Combine(artifacts, name + "-before"));
                var graph = avatar.SaveObject(DependencyHandling.CollectAssets);
                var record = RecordHelper.CreateForObject<SkyFrost.Base.Record>(avatar.Name, avatar.World.LocalUser.MachineID, null);
                string package = Path.Combine(artifacts, name + ".resonitepackage");
                await default(ToBackground);
                using (var stream = File.Create(package)) await PackageCreator.BuildPackage(parent.Engine, record, graph, stream, includeVariants: false);
                Check(PackageInspector.Inspect(package, verbose: false) == 0, "VRM expression package inspect succeeds");
                await default(ToWorld);
                restored = parent.AddSlot(name + " restored");
                await PackageImporter.ImportPackage(package, restored); await default(ToWorld); await Frames(120);
                await Verify(restored, vrm, version, sparse);
                Check(snapshot == ExpressionPackageSnapshot.Capture(restored.FindChild("Expressions"), Path.Combine(artifacts, name + "-after")),
                    "VRM catalog, poses, outputs and all 81 mappings survive package reload");
            }
            finally { restored?.Destroy(); avatar.Destroy(); }
        }
        Console.WriteLine("VRM EXPRESSIONS: VRM0/1 presets, missing/custom presets, all 81 cells, outputs, blink, menus and package reload passed");
    }

    private static async Task Verify(Slot avatar, VrmModel source, int version, bool sparse)
    {
        EquipAvatar(avatar); await Frames(30);
        var root = avatar.FindChild("Expressions");
        var face = avatar.FindChild("Face").GetComponent<SkinnedMeshRenderer>();
        for (int i = 0; i < 7200 && face.BlendShapeWeights.Count != Shapes.Length; i++) await default(NextUpdate);
        Check(face.BlendShapeWeights.Count == Shapes.Length, "saved VRM mesh loaded");
        Check(root.WriteDynamicVariable("ExpressionSystem/Config.SmoothingSpeed", 1000f) == DynamicVariableWriteResult.Success, "set fixture speed");
        var table = root.FindChild("DV").FindChild("GestureTable");
        var clips = root.FindChild("Clips");
        var api = root.FindChild("API");
        Check(table.Children.Count == 81 && table.FindChild("L8R8") != null, "VRM exports 0..8 on each hand");
        Check(clips.Children.Count == (version == 0 ? sparse ? 4 : 6 : sparse ? 5 : 7), "only resolved emotional/custom clips, without neutral or tracking presets");
        var customSurprised = clips.Children.SingleOrDefault(c => c.Name == "Surprised");
        if (customSurprised != null)
        {
            var surpriseSprite = customSurprised.GetComponent<ContextMenuItemSource>().Sprite.Target;
            Check((surpriseSprite != null) == (version == 0), "custom Surprised gets an icon only for VRM0 compatibility");
            var surpriseButton = root.FindChild("Inputs").FindChild("ContextMenu").FindChild("Select expression")
                .GetComponentsInChildren<ContextMenuItemSource>().Single(c => c.Label.Value == "Surprised");
            Check(surpriseButton.Sprite.Target == surpriseSprite, "visible Surprised button retains the clip icon after reload");
        }
        void Gesture(string hand, int value) => ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
            hand == "Left" ? ExpressionSystemSetup.SetGestureLeftTag : ExpressionSystemSetup.SetGestureRightTag, true, value);
        for (int left = 0; left <= 8; left++)
        for (int right = 0; right <= 8; right++)
        {
            // Independent oracle from XWear's Point/Peace/RockNRoll/Gun/Thumbs up states.
            int PresetFor(int gesture) => gesture switch { 3 => 3, 4 => 1, 5 => 2, 6 => 5, 7 => 4, _ => 0 };
            bool Available(int preset) => preset > 0 && !(sparse && preset is 3 or 5);
            int selected = Available(PresetFor(right)) ? PresetFor(right) : Available(PresetFor(left)) ? PresetFor(left) : 0;
            bool assigned = selected > 0;
            var target = Reference<Slot>(table, $"L{left}R{right}");
            Check(assigned ? target != null : target == null, $"VRM mapping availability {left},{right}");
            if (assigned)
                Check(target.Name == (version == 0 && selected == 5 ? "Surprised" :
                    source.Expressions.Single(e => !e.IsCustom && e.Preset == Presets[selected]).Name),
                    "normalized preset, independent of display name and source order");
            Gesture("Left", left); Gesture("Right", right); await Frames(12);
            Check(Reference<Slot>(root, "State.CurrentExpression") == target, "VRM gesture selects table target");
            for (int shape = 0; shape < Shapes.Length; shape++)
            {
                float expected = shape == 0 ? 0.12f : assigned && shape == selected ? 0.2f + selected * 0.1f :
                    assigned && selected == 1 && shape == 6 ? 0.3f : 0;
                Near(face.GetBlendShapeWeight(Shapes[shape]), expected, $"VRM pair {left},{right}: {Shapes[shape]}");
            }
        }
        Gesture("Left", 0); Gesture("Right", 4); await Frames(12);
        var eyes = avatar.GetComponent<EyeManager>();
        eyes.LeftEyeCloseOverride.Value = 0.8f; await Frames(6);
        Near(face.GetBlendShapeWeight("Blink"), 0.8f, "native blink composes with the VRM happy preset");
        eyes.LeftEyeCloseOverride.Value = 0; await Frames(6);
        Near(face.GetBlendShapeWeight("Blink"), 0.3f, "blink reopening preserves the VRM happy eyelid pose");
        var buttons = ExpressionMenuGroupChecks.CheckMenu(root);
        foreach (var button in buttons)
        {
            var target = button.Slot.GetComponent<BooleanReferenceDriver<Slot>>().FalseTarget.Target;
            button.Pressed(null, default); await Frames(12);
            Check(ContextMenuSelected(root) && Reference<Slot>(root, "State.CurrentExpression") == target, "VRM menu selects every clip");
            Gesture("Left", 8); Gesture("Right", 8);
            Check(Reference<Slot>(root, "State.CurrentExpression") == target, "menu hold rejects VRM gesture overwrite");
            ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, ExpressionSystemSetup.ResetTag, true);
        }
        await Frames(12);
        Near(face.GetBlendShapeWeight("Rest"), 0.12f, "reset uses authored Base, not neutral expression weight");
        Check(Reference<Slot>(root, "State.CurrentExpression") == null && !ContextMenuSelected(root), "VRM reset clears menu and expression");
        var custom = clips.Children.Single(c => c.ExpressionVariables<DynamicValueVariable<float>>().Any(v => Math.Abs(v.Value.Value - 0.9f) < 0.0001f));
        Check(table.WriteDynamicVariable("ExpressionSystem/GestureTable.L8R8", custom) == DynamicVariableWriteResult.Success,
            "precreated L8R8 can be assigned to a custom expression");
        Gesture("Left", 8); Gesture("Right", 8); await Frames(12);
        Check(Reference<Slot>(root, "State.CurrentExpression") == custom, "extended pair selects custom expression");
        Near(face.GetBlendShapeWeight("Custom"), 0.9f, "extended pair writes custom shape");
        Check(table.WriteDynamicVariable<Slot>("ExpressionSystem/GestureTable.L8R8", null) == DynamicVariableWriteResult.Success,
            "restore default spare cell");
        ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulse(api, ExpressionSystemSetup.ResetTag, true);
        await Frames(12);
    }

    // Exercise the real parser for VRM0 percentage weights/aliases and VRM1 node binds.
    private static VrmModel Fixture(string path, int version, bool sparse)
    {
        var preset = new Dictionary<string, object>();
        var custom = new Dictionary<string, object>();
        var groups = new List<object>();
        void Add(string key, string v0Key, int index, float weight, bool isCustom = false)
        {
            var binds = new[] { new { node = 0, index, weight } }.ToList();
            if (key == "happy") binds.Add(new { node = 0, index = 6, weight = 0.3f });
            (isCustom ? custom : preset).Add(key, new { morphTargetBinds = binds });
            groups.Add(new { name = isCustom ? key : "Display " + v0Key, presetName = isCustom ? "unknown" : v0Key,
                binds = binds.Select(b => new { mesh = 0, b.index, weight = b.weight * 100 }).ToArray() });
        }
        // Put a custom namesake before standard groups to catch name-based assignment.
        Add("sad", "unknown", 7, 0.9f, isCustom: true);
        if (version != 0 || !sparse) Add("Surprised", "unknown", 5, 0.7f, isCustom: true);
        string[] v0 = { "neutral", "joy", "angry", "sorrow", "fun", "unknown" };
        for (int i = 5; i >= 0; i--)
        {
            if (version == 0 && i == 5 || sparse && i == 3) continue;
            Add(Presets[i], v0[i], sparse && i == 5 ? 999 : i, i == 0 ? 0.45f : 0.2f + i * 0.1f);
        }
        Add("aa", "a", 8, 1); Add("blink", "blink", 8, 1); Add("lookUp", "lookup", 8, 1);
        var extension = version == 0 ? (object)new { blendShapeMaster = new { blendShapeGroups = groups } } :
            new { expressions = new { preset, custom } };
        var json = JsonSerializer.Serialize(new {
            asset = new { version = "2.0" }, nodes = new[] { new { name = "Face", mesh = 0 } },
            meshes = new[] { new { extras = new { targetNames = Shapes } } },
            extensions = new Dictionary<string, object> { [version == 0 ? "VRM" : "VRMC_vrm"] = extension }
        });
        byte[] bytes = Encoding.UTF8.GetBytes(json.PadRight((json.Length + 3) / 4 * 4));
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(0x46546C67u); writer.Write(2u); writer.Write((uint)(20 + bytes.Length));
            writer.Write((uint)bytes.Length); writer.Write(0x4E4F534Au); writer.Write(bytes);
        }
        return VrmParser.Parse(path);
    }

    private static async Task Frames(int count) { for (int i = 0; i < count; i++) await default(NextUpdate); }
    private static void Near(float actual, float expected, string message) => Check(Math.Abs(actual - expected) < 0.005f, $"{message}: {actual} ~= {expected}");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
