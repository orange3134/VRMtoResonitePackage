using System.Text;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class IndirectFaceEmoChecks
{
    public static void Run(string artifacts)
    {
        string root = Path.Combine(artifacts, "IndirectFaceEmoFixture");
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        string Guid(int id) => id.ToString("x32");
        void Asset(string name, int id, string text)
        {
            File.WriteAllText(Path.Combine(root, "Assets", name), text);
            File.WriteAllText(Path.Combine(root, "Assets", name + ".meta"), "guid: " + Guid(id));
        }
        string descriptor = "--- !u!114 &1\nMonoBehaviour:\n  baseAnimationLayers:\n  - type: 5\n    animatorController: {fileID: 91, guid: " + Guid(2) + "}\n";
        Asset("Avatar.prefab", 1, descriptor);
        const string empty = "--- !u!74 &7400000\nAnimationClip:\n  m_Name: Empty\n  m_FloatCurves: []\n";
        Asset("Empty.anim", 3, empty);
        var binding = new ExpressionBinding("Face", "Smile");
        for (int i = 1; i <= 15; i++)
            Asset($"Pose{i}.anim", 1000 + i, $"--- !u!74 &7400000\nAnimationClip:\n  m_Name: Pose{i}\n  m_FloatCurves:\n  - classID: 137\n    path: Face\n    attribute: blendShape.Smile\n    curve:\n      m_Curve:\n      - time: 0\n        value: 12\n      - time: 1\n        value: {i * 5 + 12}\n");
        string Condition(string name, int mode, int value) => $"  - m_ConditionEvent: {name}\n    m_ConditionMode: {mode}\n    m_EventTreshold: {value}\n";
        int Number(int hand, int gesture) => gesture == 0 ? 0 : hand * 100 + gesture * 11;
        string Layer(int hand) => $"  - m_Name: Input{hand}\n    m_DefaultWeight: 0\n    m_StateMachine: {{fileID: {100 + hand}}}\n";
        var yaml = new StringBuilder("--- !u!91 &91\nAnimatorController:\n  m_AnimatorParameters:\n  - m_Name: Selector\n    m_Type: 3\n    m_DefaultInt: 777\n  m_AnimatorLayers:\n" + Layer(0) + Layer(1) +
            "  - m_Name: Faces\n    m_StateMachine: {fileID: 102}\n");
        string Driver(long id, int value, int type = 0) => $"--- !u!114 &{id}\nMonoBehaviour:\n  m_Enabled: 1\n  m_Script: {{fileID: -706344726, guid: 67cc4cb7839cd3741b63733d5adf0442}}\n  parameters:\n  - name: Selector\n    type: {type}\n    value: {value}\n";
        for (int hand = 0; hand < 2; hand++)
        {
            string name = hand == 0 ? "GestureLeft" : "GestureRight";
            yaml.Append($"--- !u!1107 &{100 + hand}\nAnimatorStateMachine:\n  m_DefaultState: {{fileID: {200 + hand * 10}}}\n  m_ChildStates:\n");
            for (int g = 0; g < 8; g++) yaml.Append($"  - m_State: {{fileID: {200 + hand * 10 + g}}}\n");
            yaml.Append("  m_EntryTransitions:\n");
            for (int g = 1; g < 8; g++) yaml.Append($"  - {{fileID: {300 + hand * 10 + g}}}\n");
            for (int g = 0; g < 8; g++)
            {
                int id = hand * 10 + g;
                yaml.Append($"--- !u!1109 &{300 + id}\nAnimatorTransition:\n  m_DstState: {{fileID: {200 + id}}}\n  m_Conditions:\n" + Condition(name, 6, g));
                yaml.Append($"--- !u!1102 &{200 + id}\nAnimatorState:\n  m_Name: Input state {id}\n  m_Motion: {{fileID: 7400000, guid: {Guid(3)}}}\n  m_StateMachineBehaviours:\n  - {{fileID: {500 + id}}}\n  m_Transitions:\n  - {{fileID: {400 + id}}}\n");
                yaml.Append($"--- !u!1101 &{400 + id}\nAnimatorStateTransition:\n  m_IsExit: 1\n  m_Conditions:\n" + Condition(name, 7, g));
                yaml.Append(Driver(500 + id, Number(hand, g)));
            }
        }
        yaml.Append("--- !u!1107 &102\nAnimatorStateMachine:\n  m_DefaultState: {fileID: 600}\n  m_ChildStates:\n  - m_State: {fileID: 600}\n  m_ChildStateMachines:\n  - m_StateMachine: {fileID: 103}\n  - m_StateMachine: {fileID: 104}\n  - m_StateMachine: {fileID: 105}\n  m_EntryTransitions:\n  - {fileID: 700}\n  - {fileID: 701}\n");
        yaml.Append("--- !u!1109 &700\nAnimatorTransition:\n  m_DstStateMachine: {fileID: 103}\n  m_Conditions:\n" + Condition("Selector", 3, 0) + Condition("Selector", 4, 100));
        yaml.Append("--- !u!1109 &701\nAnimatorTransition:\n  m_DstStateMachine: {fileID: 104}\n  m_Conditions:\n" + Condition("Selector", 3, 100));
        yaml.Append("--- !u!1102 &600\nAnimatorState:\n  m_Name: Neutral\n  m_Motion: {fileID: 0}\n");
        for (int hand = 0; hand < 2; hand++)
        {
            yaml.Append($"--- !u!1107 &{103 + hand}\nAnimatorStateMachine:\n  m_DefaultState: {{fileID: {601 + hand * 7}}}\n  m_ChildStates:\n");
            for (int g = 1; g < 8; g++) yaml.Append($"  - m_State: {{fileID: {600 + hand * 7 + g}}}\n");
            yaml.Append("  m_EntryTransitions:\n");
            for (int g = 1; g < 8; g++) yaml.Append($"  - {{fileID: {710 + hand * 7 + g}}}\n");
            for (int g = 1; g < 8; g++)
            {
                int i = hand * 7 + g;
                yaml.Append($"--- !u!1109 &{710 + i}\nAnimatorTransition:\n  m_DstState: {{fileID: {600 + i}}}\n  m_Conditions:\n" + Condition("Selector", 6, Number(hand, g)));
                yaml.Append($"--- !u!1102 &{600 + i}\nAnimatorState:\n  m_Name: Candidate{i}\n  m_Motion: {{fileID: 7400000, guid: {Guid(1000 + i)}}}\n" +
                    (g == 1 ? $"  m_TimeParameterActive: 1\n  m_TimeParameter: {(hand == 0 ? "GestureLeftWeight" : "GestureRightWeight")}\n" : "") +
                    $"  m_Transitions:\n  - {{fileID: {800 + i}}}\n--- !u!1101 &{800 + i}\nAnimatorStateTransition:\n  m_IsExit: 1\n  m_Conditions:\n" + Condition("Selector", 7, Number(hand, g)));
            }
        }
        // FaceEmo still finds this disconnected/manual candidate, but hand inference must not assign it.
        yaml.Append($"--- !u!1107 &105\nAnimatorStateMachine:\n  m_DefaultState: {{fileID: 615}}\n  m_ChildStates:\n  - m_State: {{fileID: 615}}\n--- !u!1102 &615\nAnimatorState:\n  m_Name: Manual\n  m_Motion: {{fileID: 7400000, guid: {Guid(1015)}}}\n");
        string source = yaml.ToString();
        ExpressionModel Parse(string text, string extraController = null)
        {
            Asset("Face.controller", 2, text);
            if (extraController != null) Asset("Extra.controller", 4, extraController);
            using var package = UnityPackage.Open(Path.Combine(root, "Assets", "Avatar.prefab"));
            var model = VrchatExpressionParser.Parse(package, UnityScene.Parse(descriptor + (extraController == null ? "" :
                "  - type: 5\n    animatorController: {fileID: 91, guid: " + Guid(4) + "}\n")).Doc(1).Root);
            VrchatExpressionDetection.FilterFaceCurves(model, new Dictionary<ExpressionBinding, float> { [binding] = 0.12f });
            return model;
        }
        void Table(ExpressionModel model, bool leftWins = false)
        {
            var compiler = new GesturePairCompiler(model, model.Clips, _ => 0.12f);
            for (int l = 0; l < 8; l++) for (int r = 0; r < 8; r++)
            {
                int expected = leftWins && l > 0 ? l : r > 0 ? r + 7 : l;
                var pose = model.Clips.Concat(compiler.Generated).Single(c => c.Id == compiler.Pairs[l * 8 + r]);
                Check(Math.Abs(pose.Curves.Single().Keys[^1].Value - (expected * 0.05f + 0.12f)) < 0.00001f, $"indirect pair {l},{r} -> {expected}");
            }
            Check(model.Clips.Any(c => c.Name == "Pose15") && !model.Parameters.ContainsKey("Selector"), "manual candidate retained without exporting the parameter");
        }
        var model = Parse(source); Table(model);
        var states = model.Layers.Single().States;
        Check(states.Where(s => s.Name == "Candidate8").All(s => s.TimeParameter == "GestureRightWeight"), "right fist keeps its grip hand even with left fist");
        Check(states.Where(s => s.Name == "Candidate9").All(s => s.TimeParameter == null), "opposite-hand fist does not change a non-fist motion");
        Table(Parse(source.Replace(Layer(0) + Layer(1), Layer(1) + Layer(0))), leftWins: true);
        Table(Parse(source.Replace("Selector", "Renamed input").Replace("m_DefaultInt: 777", "m_DefaultInt: 0").Replace("m_DefaultWeight: 0", "m_DefaultWeight: 1")));
        void ManualOnly(ExpressionModel m, string reason)
        {
            Check(m.Clips.Any(c => c.Name == "Pose15") && m.DetectedExpressions.Count > 0 &&
                m.Layers.Single().Transitions.All(t => t.Conditions.Count == 0), reason + ": retain candidates without guessing assignments");
        }
        foreach (var (text, reason) in new[] {
            (source.Replace("    type: 0", "    type: 1"), "Add"),
            (source.Replace("    type: 0", "    type: 2"), "Random"),
            (source.Replace("    type: 0", "    type: 3"), "Copy"),
            (source + Driver(999, 42), "unknown writer"),
            (source.Replace(Driver(510, 0), Driver(510, 2)), "inconsistent neutral"),
            (source.Replace("  - {fileID: 400}\n", ""), "stalled input"),
            (source.Replace("  m_DstStateMachine: {fileID: 103}", "  m_HasExitTime: 1\n  m_DstStateMachine: {fileID: 103}"), "timed selector"),
            (source.Replace(Condition("Selector", 4, 100), Condition("Unknown toggle", 4, 100)), "unknown parent gate"),
            (source.Replace("m_Name: Candidate1\n", "m_Name: Candidate1\n  m_StateMachineBehaviours:\n  - {fileID: 999}\n"), "consumer behaviour") }) ManualOnly(Parse(text), reason);
        ManualOnly(Parse(source, "--- !u!91 &91\nAnimatorController:\n  m_AnimatorLayers: []\n" + Driver(999, 42)), "writer in appended FX");
        Asset("Empty.anim", 3, empty.Replace("m_FloatCurves: []", "m_FloatCurves:\n  - classID: 95\n    path: ''\n    attribute: Selector"));
        ManualOnly(Parse(source), "animated input is not empty just because it has no face curve");
        Asset("Empty.anim", 3, empty);
        Console.WriteLine("PASS: FaceEmo indirect hand conditions, all64 authored values, nested selectors, source priority, grip, manual fallback and unsupported writers/gates");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
