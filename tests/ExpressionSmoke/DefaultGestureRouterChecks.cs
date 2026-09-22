using System.Text;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class DefaultGestureRouterChecks
{
    public static void Run(string artifacts)
    {
        string root = Path.Combine(artifacts, "DefaultGestureRouterFixture");
        Directory.CreateDirectory(Path.Combine(root, "Assets")); Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        string Guid(int n) => n.ToString("x32");
        void Asset(string name, int id, string text)
        {
            File.WriteAllText(Path.Combine(root, "Assets", name), text);
            File.WriteAllText(Path.Combine(root, "Assets", name + ".meta"), "guid: " + Guid(id));
        }
        string descriptor = "--- !u!114 &1\nMonoBehaviour:\n  expressionParameters: {fileID: 11400000, guid: " + Guid(3) +
            "}\n  baseAnimationLayers:\n  - type: 5\n    isDefault: 0\n    animatorController: {fileID: 91, guid: " + Guid(2) + "}\n";
        Asset("Avatar.prefab", 1, descriptor);
        void Defaults(int bank) => Asset("Parameters.asset", 3,
            "--- !u!114 &11400000\nMonoBehaviour:\n  parameters:\n  - name: Bank\n    valueType: 0\n    defaultValue: " + bank + "\n");
        Defaults(1); // ExpressionParameters override controller zero, without an expression menu.
        var yaml = new StringBuilder("--- !u!91 &91\nAnimatorController:\n  m_AnimatorParameters:\n  - m_Name: Bank\n    m_Type: 3\n    m_DefaultInt: 0\n  - m_Name: Gate\n    m_Type: 1\n    m_DefaultFloat: 0.35\n  m_AnimatorLayers:\n  - m_Name: Face\n    m_StateMachine: {fileID: 100}\n    m_SyncedLayerIndex: -1\n--- !u!1107 &100\nAnimatorStateMachine:\n  m_DefaultState: {fileID: 200}\n  m_ChildStates:\n  - m_State: {fileID: 200}\n");
        for (int bank = 0; bank < 2; bank++)
        {
            yaml.Append($"  - m_State: {{fileID: {300 + bank}}}\n");
            for (int g = 0; g < 8; g++) yaml.Append($"  - m_State: {{fileID: {1000 + bank * 10 + g}}}\n");
        }
        yaml.Append("  - m_State: {fileID: 400}\n--- !u!1102 &200\nAnimatorState:\n  m_Name: Gate\n  m_WriteDefaultValues: 0\n  m_Motion: {fileID: 0}\n  m_Transitions:\n  - {fileID: 500}\n  - {fileID: 501}\n");
        void Transition(int id, int destination, string conditions) => yaml.Append($"--- !u!1101 &{id}\nAnimatorStateTransition:\n  m_DstState: {{fileID: {destination}}}\n  m_Conditions:\n{conditions}");
        string Condition(string name, int mode, string threshold) => $"  - m_ConditionEvent: {name}\n    m_ConditionMode: {mode}\n    m_EventTreshold: {threshold}\n";
        for (int bank = 0; bank < 2; bank++)
        {
            Transition(500 + bank, 300 + bank, Condition("Bank", 6, bank.ToString()) + Condition("Gate", 3, "0.25"));
            yaml.Append($"--- !u!1102 &{300 + bank}\nAnimatorState:\n  m_Name: Router{bank}\n  m_WriteDefaultValues: 0\n  m_Motion: {{fileID: 0}}\n  m_Transitions:\n");
            int start = 600 + bank * 20;
            for (int t = 0; t < 15; t++) yaml.Append($"  - {{fileID: {start + t}}}\n");
            for (int g = 1; g < 8; g++) Transition(start + g - 1, 1000 + bank * 10 + g, Condition("GestureLeft", 6, g.ToString()));
            for (int g = 1; g < 8; g++) Transition(start + 7 + g - 1, 1000 + bank * 10 + g, Condition("GestureRight", 6, g.ToString()));
            Transition(start + 14, 1000 + bank * 10, "");
            for (int g = 0; g < 8; g++)
            {
                int id = 1000 + bank * 10 + g;
                Asset($"Pose{id}.anim", id, $"--- !u!74 &7400000\nAnimationClip:\n  m_Name: Pose{id}\n  m_FloatCurves:\n  - attribute: blendShape.Smile\n    path: Face\n    classID: 137\n    curve:\n      m_Curve:\n      - time: 0\n        value: {10 + bank * 50 + g * 5}\n");
                yaml.Append($"--- !u!1102 &{id}\nAnimatorState:\n  m_Name: Pose{id}\n  m_WriteDefaultValues: 0\n  m_Motion: {{fileID: 7400000, guid: {Guid(id)}}}\n  m_StateMachineBehaviours:\n  - {{fileID: 900}}\n  m_Transitions:\n  - {{fileID: {2000 + id}}}\n");
                Transition(2000 + id, 400, Condition("Bank", 7, bank.ToString()));
            }
        }
        yaml.Append("--- !u!1102 &400\nAnimatorState:\n  m_Name: Relay\n  m_Motion: {fileID: 0}\n  m_StateMachineBehaviours:\n  - {fileID: 900}\n  m_Transitions:\n  - {fileID: 401}\n--- !u!1101 &401\nAnimatorStateTransition:\n  m_IsExit: 1\n  m_DstState: {fileID: 0}\n--- !u!114 &900\nMonoBehaviour:\n  m_Script: {fileID: -706344726, guid: 67cc4cb7839cd3741b63733d5adf0442}\n  parameters:\n  - name: Bank\n    type: 0\n    value: 0\n");
        string source = yaml.ToString();
        ExpressionModel Parse(string text)
        {
            Asset("Face.controller", 2, text);
            using var package = UnityPackage.Open(Path.Combine(root, "Assets", "Avatar.prefab"));
            return VrchatExpressionParser.Parse(package, UnityScene.Parse(descriptor).Doc(1).Root);
        }
        void CheckBank(int bank, string text)
        {
            var model = Parse(text);
            Check(model.Layers.Count == 1, "default-mode router imported");
            var table = new GesturePairCompiler(model, model.Clips, _ => 0.23f);
            for (int left = 0; left < 8; left++) for (int right = 0; right < 8; right++)
            {
                var pose = model.Clips.Concat(table.Generated).Single(c => c.Id == table.Pairs[left * 8 + right]);
                float expected = (10 + bank * 50 + (left > 0 ? left : right) * 5) / 100f;
                Check(Math.Abs(pose.Curves.Single().Sample(0) - expected) < 0.0001f, "all64 pairs select authored bank and ordered hand priority");
            }
            Check(model.Diagnostics.Any(d => d.Contains("Bank=" + bank) && d.Contains("Gate=0.35")), "frozen defaults diagnosed");
        }
        CheckBank(1, source);
        Defaults(0); CheckBank(0, source);
        File.WriteAllText(Path.Combine(root, "Assets", "Pose1011.anim"), "invalid");
        CheckBank(0, source); // Unselected bank must not block the default bank.
        Defaults(1); Check(Parse(source).Layers.Count == 0, "unavailable selected clip rejected"); Defaults(0);
        Check(Parse(source.Replace("m_ConditionEvent: Gate", "m_ConditionEvent: Unknown")).Layers.Count == 0, "undeclared default not guessed");
        Check(Parse(source.Replace("name: Bank\n    type: 0", "name: GestureLeft\n    type: 0")).Layers.Count == 0, "hand-mutating driver rejected");
        Check(Parse(source.Replace("m_DstState: {fileID: 300}", "m_DstState: {fileID: 200}")).Layers.Count == 0, "self loop without a face cannot create assignments");
        Check(Parse(source.Replace("m_DstState: {fileID: 300}", "m_DstState: {fileID: 300}\n  m_HasExitTime: 1")).Layers.Count == 0, "matching timed transition rejected");
        Check(Parse(source.Replace("m_DstState: {fileID: 1001}", "m_DstState: {fileID: 200}")).Layers.Count == 0, "multi-state cycle rejected");
        Console.WriteLine("PASS: authored default banks, descriptor precedence, float guards, multi-step routing, 64 pairs, inactive unsupported clips and unsafe routes");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
