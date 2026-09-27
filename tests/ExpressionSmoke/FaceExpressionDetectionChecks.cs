using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class FaceExpressionDetectionChecks
{
    public static void Run(string artifacts)
    {
        string root = Path.Combine(artifacts, "FaceDetectionFixture");
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        string Guid(int id) => id.ToString("x32");
        void Asset(string name, int id, string content)
        {
            File.WriteAllText(Path.Combine(root, "Assets", name), content);
            File.WriteAllText(Path.Combine(root, "Assets", name + ".meta"), "guid: " + Guid(id));
        }
        var descriptor = "--- !u!114 &1\nMonoBehaviour:\n  baseAnimationLayers:\n  - type: 5\n    isDefault: 0\n    animatorController: {fileID: 91, guid: " + Guid(2) + "}\n";
        Asset("Avatar.prefab", 1, descriptor);
        string State(int id, int clip, string extra = "") =>
            $"--- !u!1102 &{id}\nAnimatorState:\n  m_Name: State{id}\n  m_WriteDefaultValues: 1\n  m_Motion: {{fileID: 7400000, guid: {Guid(clip)}}}\n" + extra;
        string Transition(int id, int target, string parameter, int mode, string extra = "") =>
            $"--- !u!1101 &{id}\nAnimatorStateTransition:\n  m_DstState: {{fileID: {target}}}\n  m_Conditions:\n  - m_ConditionEvent: {parameter}\n    m_ConditionMode: {mode}\n    m_EventTreshold: 1\n" + extra;
        var controller = "--- !u!91 &91\nAnimatorController:\n  m_AnimatorLayers:\n  - m_Name: NoParameters\n    m_StateMachine: {fileID: 100}\n    m_SyncedLayerIndex: -1\n" +
            "  - m_Name: Disabled\n    m_DefaultWeight: 0\n    m_StateMachine: {fileID: 110}\n    m_SyncedLayerIndex: -1\n" +
            "--- !u!1107 &100\nAnimatorStateMachine:\n  m_DefaultState: {fileID: 200}\n  m_ChildStates:\n  - m_State: {fileID: 200}\n  - m_State: {fileID: 299}\n" +
            "  m_ChildStateMachines:\n  - m_StateMachine: {fileID: 120}\n" +
            "--- !u!1107 &110\nAnimatorStateMachine:\n  m_DefaultState: {fileID: 210}\n" +
            "--- !u!1107 &120\nAnimatorStateMachine:\n  m_AnyStateTransitions:\n" +
            string.Concat(Enumerable.Range(300, 8).Select(id => $"  - {{fileID: {id}}}\n")) +
            State(200, 3) + State(210, 4) + State(220, 5) + State(221, 6) + State(222, 7) +
            State(223, 8) + State(224, 9) + State(225, 10) + State(226, 11) + State(227, 12) + State(299, 13) +
            Transition(300, 220, "GestureLeft", 6) + Transition(301, 221, "Toggle", 1) +
            Transition(302, 222, "Touch", 3) + Transition(303, 223, "Hair_IsGrabbed", 3) +
            Transition(304, 224, "GestureRight", 6, "  m_Mute: 1\n") +
            Transition(305, 225, "GestureLeft", 6) + Transition(306, 226, "GestureLeft", 6) +
            Transition(307, 227, "Mode", 6);
        Asset("Face.controller", 2, controller);
        string Curve(string path, string shape, float value) =>
            $"  - classID: 137\n    attribute: blendShape.{shape}\n    path: {path}\n    curve:\n      m_Curve:\n      - time: 0\n        value: {value}\n";
        for (int i = 3; i <= 13; i++)
            Asset($"Clip{i}.anim", i, "--- !u!74 &7400000\nAnimationClip:\n  m_Name: Clip" + i + "\n  m_FloatCurves:\n" +
                (i == 10 ? Curve("Body", "vrc.v_aa", 80) : i == 11 ? Curve("Clothes", "Smile", 80) :
                Curve("Body", "Smile", i == 12 ? 25 : 80) + Curve("Body", "Rest", 30)));
        var components = new[] {
            UnityYaml.ParseFlatDocument("receiverType: 1\ncollisionTags: []\nparameter: Touch\n"),
            UnityYaml.ParseFlatDocument("m_Script: {fileID: 1661641543, guid: 2a2c05204084d904aa4945ccff20d8e5}\nparameter: Hair\n") };
        using var package = UnityPackage.Open(Path.Combine(root, "Assets", "Avatar.prefab"));
        var model = VrchatExpressionParser.Parse(package, UnityScene.Parse(descriptor).Doc(1).Root, modularComponents: components);
        var candidates = model.Clips.Select(c => c.Name).ToHashSet();
        Check(candidates.SetEquals(new[] { "Clip3", "Clip4", "Clip5", "Clip11", "Clip12" }),
            "default/nested/zero-weight candidates included; toggles, contacts, physics, mute, visemes and disconnected states excluded");
        var values = new Dictionary<ExpressionBinding, float> { [new("Body", "Smile")] = 0.25f, [new("Body", "Rest")] = 0.3f };
        VrchatExpressionDetection.FilterFaceCurves(model, values);
        Check(model.DetectedExpressions.Select(c => c.Name).ToHashSet().SetEquals(new[] { "Clip3", "Clip4", "Clip5" }),
            "same-named non-face shape and authored baseline-only candidates excluded");
        Check(model.DetectedExpressions.All(c => c.Curves.Count == 1 && c.Curves[0].Binding.Shape == "Smile"),
            "baseline curves omitted from candidate poses");
        Check(model.Clips.Single(c => c.Name == "Clip5").Curves.Count == 2,
            "complete reset curves remain available for validated layer composition");
        Check(model.Clips.All(c => c.Name != "Clip11"),
            "non-face curves never reach automatic gesture outputs");
        Check(VrchatExpressionDetection.IsViseme("Prefix.VRC.V_AA") && !VrchatExpressionDetection.IsViseme("Smile"),
            "FaceEmo case-insensitive viseme name exclusion");
        // Different values at any key make a candidate, even if its final value is baseline.
        var animated = new ExpressionClip { Id = "animated" };
        var curve = new ExpressionCurve { Binding = new("Body", "Smile") };
        curve.Keys.Add(new(0, 0.8f, 0, 0)); curve.Keys.Add(new(1, 0.25f, 0, 0)); animated.Curves.Add(curve);
        var standalone = new ExpressionModel { DetectedExpressions = new() { animated } }; standalone.Clips.Add(animated);
        VrchatExpressionDetection.FilterFaceCurves(standalone, values);
        Check(standalone.DetectedExpressions.Single().Curves.Single().Keys.Count == 2, "animated differences preserve all keys");
        Console.WriteLine("PASS: FaceEmo candidate discovery, event exclusions, exact face bindings and authored-baseline differences");
    }
    public static async Task RunCatalog(FrooxEngine.Slot parent)
    {
        var root = parent.AddSlot("Detected expression catalog");
        try
        {
            var smile = new ExpressionBinding("Body", "Smile");
            var rest = new ExpressionBinding("Body", "Rest");
            var smileField = root.AttachComponent<FrooxEngine.ValueField<float>>().Value;
            var restField = root.AttachComponent<FrooxEngine.ValueField<float>>().Value;
            smileField.Value = 0.25f; restField.Value = 0.3f;
            var model = new ExpressionModel { DetectedExpressions = new() };
            void Clip(string name, float value, bool candidate)
            {
                var clip = new ExpressionClip { Id = name, Name = name };
                foreach (var (binding, weight) in new[] { (smile, value), (rest, 0.3f) })
                {
                    var curve = new ExpressionCurve { Binding = binding }; curve.Keys.Add(new(0, weight, 0, 0)); clip.Curves.Add(curve);
                }
                model.Clips.Add(clip); if (candidate) model.DetectedExpressions.Add(clip);
            }
            Clip("Smile", 0.8f, true); Clip("Reset", 0.25f, true); Clip("Unassigned", 0.6f, true); Clip("HiddenToggle", 0.9f, false);
            var layer = new ExpressionLayer { Id = "Hands", Name = "Hands", DefaultState = 0 };
            layer.States.Add(new("Reset", "Reset", 1, true)); layer.States.Add(new("Smile", "Smile", 1, true));
            var neutral = new ExpressionTransition { Destination = 0 }; neutral.Conditions.Add(new("GestureLeft", 6, 0));
            var active = new ExpressionTransition { Destination = 1 }; active.Conditions.Add(new("GestureLeft", 3, 0));
            layer.Transitions.Add(neutral); layer.Transitions.Add(active); model.Layers.Add(layer);
            VrchatExpressionDetection.FilterFaceCurves(model, new Dictionary<ExpressionBinding, float> { [smile] = 0.25f, [rest] = 0.3f });
            var expressions = await ExpressionSystemSetup.BuildAsync(root, model, b => b == smile ? smileField : restField,
                initialWeight: field => field == smileField ? 0.25f : 0.3f);
            var catalog = expressions.FindChild("Catalog");
            Check(catalog.FindChild("HiddenToggle") == null, "excluded candidate does not leak back into Catalog through internal routing clips");
            Check(catalog.FindChild("Unassigned").FindChild("Bindings").ChildrenCount == 1, "unmapped catalog candidates contain only baseline differences");
            Check(catalog.FindChild("Smile").FindChild("Bindings").ChildrenCount == 2 &&
                catalog.FindChild("Reset").FindChild("Bindings").ChildrenCount == 2, "mapped expressions preserve explicit baseline reset curves");
            var mappings = expressions.FindChild("DV").FindChild("GestureTable").ExpressionVariables<FrooxEngine.DynamicReferenceVariable<FrooxEngine.Slot>>()
                .Where(v => v.VariableName.Value.StartsWith("ExpressionSystem/GestureTable.", StringComparison.Ordinal)).ToArray();
            Check(mappings.Length == 64 && mappings.All(v => v.Reference.Target != null), "all gesture combinations retain an assigned table entry");
            Console.WriteLine("PASS: detected Catalog filters unrelated candidates while preserving mapped reset poses");
        }
        finally { root.Destroy(); }
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
