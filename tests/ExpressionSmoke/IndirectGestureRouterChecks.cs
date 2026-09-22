using System.Text;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class IndirectGestureRouterChecks
{
    public static void Run(string artifacts)
    {
        string root = Path.Combine(artifacts, "IndirectGestureFixture");
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        string Guid(int n) => n.ToString("x32");
        void Asset(string name, int id, string text)
        {
            File.WriteAllText(Path.Combine(root, "Assets", name), text);
            File.WriteAllText(Path.Combine(root, "Assets", name + ".meta"), "guid: " + Guid(id));
        }
        string descriptor = "--- !u!114 &1\nMonoBehaviour:\n  expressionParameters: {fileID: 11400000, guid: " + Guid(3) +
            "}\n  baseAnimationLayers:\n  - type: 5\n    isDefault: 0\n    animatorController: {fileID: 91, guid: " + Guid(2) + "}\n";
        Asset("Avatar.prefab", 1, descriptor);
        Asset("Parameters.asset", 3, "--- !u!114 &11400000\nMonoBehaviour:\n  parameters:\n  - name: Bank\n    valueType: 0\n    defaultValue: 1\n");
        var yaml = new StringBuilder("--- !u!91 &91\nAnimatorController:\n  m_AnimatorParameters:\n  - m_Name: SelectedFace\n    m_Type: 3\n    m_DefaultInt: 0\n  - m_Name: Bank\n    m_Type: 3\n    m_DefaultInt: 0\n  m_AnimatorLayers:\n");
        // Layer zero is unrelated. Both input layers have zero motion weight.
        yaml.Append("  - m_Name: Base\n    m_StateMachine: {fileID: 90}\n    m_SyncedLayerIndex: -1\n");
        for (int hand = 0; hand < 2; hand++)
            yaml.Append($"  - m_Name: Input{hand}\n    m_StateMachine: {{fileID: {100 + hand}}}\n    m_DefaultWeight: 0\n    m_SyncedLayerIndex: -1\n");
        yaml.Append("  - m_Name: Face\n    m_StateMachine: {fileID: 500}\n    m_DefaultWeight: 1\n    m_SyncedLayerIndex: -1\n");
        string Condition(string name, int mode, int threshold) => $"  - m_ConditionEvent: {name}\n    m_ConditionMode: {mode}\n    m_EventTreshold: {threshold}\n";
        for (int hand = 0; hand < 2; hand++)
        {
            int first = 1000 + hand * 100;
            string parameter = hand == 0 ? "GestureLeft" : "GestureRight";
            yaml.Append($"--- !u!1107 &{100 + hand}\nAnimatorStateMachine:\n  m_DefaultState: {{fileID: {first}}}\n  m_ChildStates:\n");
            for (int g = 0; g < 8; g++) yaml.Append($"  - m_State: {{fileID: {first + g}}}\n");
            yaml.Append("  m_EntryTransitions:\n");
            for (int g = 1; g < 8; g++) yaml.Append($"  - {{fileID: {first + 20 + g}}}\n");
            for (int g = 0; g < 8; g++)
            {
                int value = g == 0 ? 0 : hand * 8 + g;
                yaml.Append($"--- !u!1102 &{first + g}\nAnimatorState:\n  m_Name: Input{hand}-{g}\n  m_Motion: {{fileID: 0}}\n  m_StateMachineBehaviours:\n  - {{fileID: {first + 10 + g}}}\n  m_Transitions:\n  - {{fileID: {first + 30 + g}}}\n");
                yaml.Append($"--- !u!114 &{first + 10 + g}\nMonoBehaviour:\n  m_Script: {{fileID: -706344726, guid: 67cc4cb7839cd3741b63733d5adf0442}}\n  localOnly: 1\n  parameters:\n  - type: 0\n    name: SelectedFace\n    value: {value}\n");
                if (g > 0) yaml.Append($"--- !u!1109 &{first + 20 + g}\nAnimatorTransition:\n  m_DstState: {{fileID: {first + g}}}\n  m_Conditions:\n" + Condition(parameter, 6, g));
                yaml.Append($"--- !u!1101 &{first + 30 + g}\nAnimatorStateTransition:\n  m_IsExit: 1\n  m_Conditions:\n" + Condition(parameter, g == 0 ? 3 : 7, g));
            }
        }
        yaml.Append("--- !u!1107 &500\nAnimatorStateMachine:\n  m_DefaultState: {fileID: 600}\n  m_ChildStates:\n  - m_State: {fileID: 600}\n  m_ChildStateMachines:\n  - m_StateMachine: {fileID: 501}\n  m_EntryTransitions:\n  - {fileID: 700}\n--- !u!1109 &700\nAnimatorTransition:\n  m_DstStateMachine: {fileID: 501}\n  m_Conditions:\n" + Condition("SelectedFace", 3, 0) + Condition("Bank", 6, 1));
        yaml.Append("--- !u!1107 &501\nAnimatorStateMachine:\n  m_DefaultState: {fileID: 601}\n  m_ChildStates:\n");
        for (int v = 1; v < 16; v++) yaml.Append($"  - m_State: {{fileID: {600 + v}}}\n");
        yaml.Append("  m_EntryTransitions:\n");
        for (int v = 1; v < 16; v++) yaml.Append($"  - {{fileID: {700 + v}}}\n");
        for (int v = 0; v < 16; v++)
        {
            Asset($"Pose{v}.anim", 2000 + v, $"--- !u!74 &7400000\nAnimationClip:\n  m_Name: Pose{v}\n  m_FloatCurves:\n  - attribute: blendShape.Face\n    path: Face\n    classID: 137\n    curve:\n      m_Curve:\n      - time: 0\n        value: {v * 5}\n");
            yaml.Append($"--- !u!1102 &{600 + v}\nAnimatorState:\n  m_Name: Pose{v}\n  m_Motion: {{fileID: 7400000, guid: {Guid(2000 + v)}}}\n  m_WriteDefaultValues: 0\n  m_TimeParameterActive: 1\n  m_TimeParameter: GestureRightWeight\n  m_Transitions:\n  - {{fileID: {800 + v}}}\n");
            yaml.Append($"--- !u!1101 &{800 + v}\nAnimatorStateTransition:\n  m_IsExit: 1\n  m_Conditions:\n" + Condition("SelectedFace", v == 0 ? 3 : 7, v));
            if (v > 0) yaml.Append($"--- !u!1109 &{700 + v}\nAnimatorTransition:\n  m_DstState: {{fileID: {600 + v}}}\n  m_Conditions:\n" + Condition("SelectedFace", 6, v));
        }
        string source = yaml.ToString();
        ExpressionModel Parse(string text)
        {
            Asset("Face.controller", 2, text);
            using var p = UnityPackage.Open(Path.Combine(root, "Assets", "Avatar.prefab"));
            return VrchatExpressionParser.Parse(p, UnityScene.Parse(descriptor).Doc(1).Root);
        }
        var model = Parse(source);
        Check(model.Layers.Count == 1, "zero-weight Set layers project only their consuming face layer");
        var table = new GesturePairCompiler(model, model.Clips, _ => 0);
        for (int l = 0; l < 8; l++) for (int r = 0; r < 8; r++)
        {
            int expected = r > 0 ? r + 8 : l;
            var pose = model.Clips.Concat(table.Generated).Single(c => c.Id == table.Pairs[l * 8 + r]);
            Check(Math.Abs(pose.Curves.Single().Keys[^1].Value - expected * 0.05f) < 0.00001f, "all64 indirect poses, right priority and neutral fallback");
        }
        void Reject(string text, string message) => Check(Parse(text).Layers.Count == 0, message);
        Reject(source.Replace("  - type: 0", "  - type: 1"), "Add drivers are not guessed as Set");
        Reject(source.Replace("  - type: 0", "  - type: 2"), "Random drivers rejected");
        Reject(source.Replace("    name: SelectedFace", "    name: GestureLeft"), "hand-mutating drivers rejected");
        Reject(source.Replace("m_Motion: {fileID: 0}", "m_Motion: {fileID: 7400000, guid: " + Guid(2001) + "}"), "input layers with animated output rejected");
        Reject(source.Replace("m_DstStateMachine: {fileID: 501}", "m_DstStateMachine: {fileID: 500}"), "foreign/cyclic machine target rejected");
        Reject(source.Replace("m_DstState: {fileID: 609}", "m_DstState: {fileID: 1001}"), "state outside consumer machine rejected");
        Reject(source.Replace("m_DstState: {fileID: 609}", "m_DstState: {fileID: 609}\n  m_HasExitTime: 1"), "timed consumer route rejected");
        Reject(source.Replace("m_TimeParameter: GestureRightWeight", "m_TimeParameter: Unsupported"), "unknown motion clock rejected");
        Reject(source.Replace("guid: " + Guid(2009), "guid: " + Guid(9999)), "missing selected motion rejected");
        Reject(source.Replace("m_EventTreshold: 0\n", "m_EventTreshold: -1\n"), "active Exit route is not silently treated as stable");
        Reject(source + "--- !u!114 &9999\nMonoBehaviour:\n  m_Script: {fileID: -706344726, guid: 67cc4cb7839cd3741b63733d5adf0442}\n  parameters:\n  - type: 0\n    name: SelectedFace\n    value: 1\n", "additional unrecognized writer rejected");
        Reject(source.Replace("  - {fileID: 700}\n", "  m_AnyStateTransitions:\n  - {fileID: 700}\n"), "Any State override not ignored");
        Console.WriteLine("PASS: indirect Set parameter selectors, zero-weight inputs, nested Entry, defaults, all64 poses and unsafe-route guards");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}