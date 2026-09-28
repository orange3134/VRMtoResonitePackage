using FrooxEngine;
using FrooxEngine.ProtoFlux;
using VrmToResonitePackage.Expressions;
using static ExpressionTestFields;

internal static class ExpressionBindingNameChecks
{
    public static async Task Run(Slot parent, string artifacts)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        Check(ExpressionBindingNames.Create("Body/Face", "Smile(1)", used) == "Body_Face.Smile_1_", "forbidden characters become underscores");
        Check(ExpressionBindingNames.Create("Body_Face", "Smile_1_", used) == "Body_Face.Smile_1_.2", "replacement collisions receive readable suffixes");
        Check(ExpressionBindingNames.Create("\u9854", "\u7B11\u9854", used) == "\u9854.\u7B11\u9854", "Japanese names stay readable");
        Check(ExpressionBindingNames.Create(" ", "", used) == "Mesh.Shape", "empty parts have readable defaults");
        foreach (char c in Enumerable.Range(0, 128).Select(i => (char)i).Concat("\u3000\uFF08\uFF09\u2665\u200B\uD83D\uDE00"))
        {
            string name = ExpressionBindingNames.Create("A" + c + "B", "C" + c + "D", new HashSet<string>());
            Check(DynamicVariableHelper.IsValidName(name) && DynamicVariableHelper.ProcessName(name) == name,
                "generated key survives the installed DLL's validation: " + (int)c);
        }
        var bindings = new[] {
            new ExpressionBinding("GroupA/Face?", "Smile(1)"),
            new ExpressionBinding("GroupB/Face_", "Smile_1_"),
            new ExpressionBinding("GroupC/Face_", "Smile_1_.2"),
            new ExpressionBinding("GroupD/\u9854", "\u7B11\u9854") };
        Dictionary<string, string> firstNames = null;
        for (int reverse = 0; reverse < 2; reverse++)
        {
            var avatar = parent.AddSlot("Named binding regression");
            try
            {
                var fields = bindings.ToDictionary(b => b, b => avatar.AddSlot(b.Path).AttachComponent<ValueField<float>>().Value);
                foreach (var field in fields.Values) field.Value = 0.1f;
                var model = new ExpressionModel();
                var clip = new ExpressionClip { Id = "named", Name = "Named" };
                foreach (var binding in reverse == 0 ? bindings : bindings.Reverse())
                {
                    var curve = new ExpressionCurve { Binding = binding };
                    curve.Keys.Add(new(0, 0.2f + Array.IndexOf(bindings, binding) * 0.15f, 0, 0));
                    clip.Curves.Add(curve);
                }
                model.Clips.Add(clip);
                var root = await ExpressionSystemSetup.BuildAsync(avatar, model, b => fields[b], menu: false);
                EquipAvatar(avatar);
                await Frames(60);
                var outputs = root.FindChild("Outputs").Children;
                var names = outputs.ToDictionary(o => Text(o, "Path"), o => Text(o, "Id"));
                Check(names.Values.Distinct().Count() == bindings.Length, "all colliding sources get distinct keys");
                if (firstNames == null) firstNames = names;
                else Check(names.All(p => firstNames[p.Key] == p.Value), "keys are stable when source curve order changes");
                var api = root.FindChild("API").FindChild("Receivers");
                Check(ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api,
                    ExpressionSystemSetup.SelectTag, true, "named") == 1, "select named expression");
                for (int i = 0; i < bindings.Length; i++)
                    Check(Math.Abs(fields[bindings[i]].Value - (0.2f + i * 0.15f)) < 0.001f, "colliding names drive their own output");
                var values = root.FindChild("Catalog").FindChild("Named").FindChild("Bindings");
                var firstOutput = outputs.Single(o => Text(o, "Path") == bindings[0].Path);
                var variable = BindingVariable(values, firstOutput);
                variable.Slot.Name = "Display label is independent";
                variable.Value.Value = 0.91f;
                await Frames(3);
                Check(Math.Abs(fields[bindings[0]].Value - 0.2f) < 0.001f, "static edits wait for selection");
                ProtoFluxHelper.DynamicImpulseHandler.TriggerDynamicImpulseWithArgument(api, ExpressionSystemSetup.SelectTag, true, "named");
                Check(Math.Abs(fields[bindings[0]].Value - 0.91f) < 0.001f, "variable name binds independently of slot label");
                variable.Slot.Name = Text(firstOutput, "Id");
                Check(ExpressionPackageSnapshot.Pose(root.FindChild("Catalog").FindChild("Named")).Count == bindings.Length,
                    "snapshot comparisons retain every source binding");
                ExpressionGraphChecks.CheckLayout(root);
                ExpressionPackageSnapshot.Capture(root, Path.Combine(artifacts, "NamedBindings" + reverse));
            }
            finally { avatar.Destroy(); }
        }
        Console.WriteLine("BINDING NAMES: readable keys, forbidden characters, collisions, stable ordering and direct dynamic lookup passed");
    }

    private static string Text(Slot slot, string key) => slot.ExpressionVariables<DynamicValueVariable<string>>()
        .Single(v => v.VariableName.Value == VariablePath(slot, key)).Value.Value;
    private static async Task Frames(int count) { for (int i = 0; i < count; i++) await default(NextUpdate); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
