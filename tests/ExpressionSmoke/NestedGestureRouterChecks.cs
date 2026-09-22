using System.Text;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class NestedGestureRouterChecks
{
    public static void Run(string artifacts)
    {
        string root = Path.Combine(artifacts, "NestedGestureRouterFixture");
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        string Guid(int id) => id.ToString("x32");
        void Asset(string name, int id, string yaml)
        {
            File.WriteAllText(Path.Combine(root, "Assets", name), yaml);
            File.WriteAllText(Path.Combine(root, "Assets", name + ".meta"), "guid: " + Guid(id));
        }
        string descriptor = "--- !u!114 &1\nMonoBehaviour:\n  expressionParameters: {fileID: 11400000, guid: " + Guid(3) + "}\n  baseAnimationLayers:\n  - type: 5\n    isDefault: 0\n    animatorController: {fileID: 91, guid: " + Guid(2) + "}\n";
        Asset("Avatar.prefab", 1, descriptor);
        void Bank(int bank) => Asset("Parameters.asset", 3, "--- !u!114 &11400000\nMonoBehaviour:\n  parameters:\n  - name: Bank\n    valueType: 0\n    defaultValue: " + bank + "\n");
        Bank(2);
        const string empty = "--- !u!74 &7400000\nAnimationClip:\n  m_Name: Empty\n  m_FloatCurves: []\n";
        Asset("Empty.anim", 4, empty);
        var yaml = new StringBuilder("--- !u!91 &91\nAnimatorController:\n  m_AnimatorParameters:\n  - m_Name: Bank\n    m_Type: 3\n    m_DefaultInt: 0\n  - m_Name: EyeClose\n    m_Type: 4\n    m_DefaultBool: 1\n  m_AnimatorLayers:\n  - m_Name: Face\n    m_StateMachine: {fileID: 100}\n    m_SyncedLayerIndex: -1\n--- !u!1107 &100\nAnimatorStateMachine:\n  m_DefaultState: {fileID: 101}\n  m_ChildStates:\n  - m_State: {fileID: 101}\n  m_ChildStateMachines:\n  - m_StateMachine: {fileID: 110}\n  - m_StateMachine: {fileID: 120}\n  m_EntryTransitions:\n  - {fileID: 111}\n  - {fileID: 121}\n--- !u!1102 &101\nAnimatorState:\n  m_Name: Disabled\n  m_Motion: {fileID: 0}\n");
        string Condition(string parameter, int mode, int value) => $"  - m_ConditionEvent: {parameter}\n    m_ConditionMode: {mode}\n    m_EventTreshold: {value}\n";
        void Transition(int id, int dest, string conditions, bool timed = false, bool exit = false) =>
            yaml.Append($"--- !u!1101 &{id}\nAnimatorStateTransition:\n  m_DstState: {{fileID: {dest}}}\n  m_IsExit: {(exit ? 1 : 0)}\n  m_HasExitTime: {(timed ? 1 : 0)}\n  m_ExitTime: 0.75\n  m_Conditions:\n{conditions}");
        for (int bank = 1; bank <= 2; bank++)
        {
            int hub = bank * 1000, machine = 100 + bank * 10;
            yaml.Append($"--- !u!1109 &{machine + 1}\nAnimatorTransition:\n  m_DstStateMachine: {{fileID: {machine}}}\n  m_Conditions:\n{Condition("Bank", 6, bank)}");
            yaml.Append($"--- !u!1107 &{machine}\nAnimatorStateMachine:\n  m_Name: Bank{bank}\n  m_DefaultState: {{fileID: {hub}}}\n  m_ChildStates:\n  - m_State: {{fileID: {hub}}}\n");
            for (int g = 0; g <= 8; g++) yaml.Append($"  - m_State: {{fileID: {hub + 10 + g}}}\n");
            yaml.Append($"--- !u!1102 &{hub}\nAnimatorState:\n  m_Name: Dispatch{bank}\n  m_Motion: {{fileID: 7400000, guid: {Guid(4)}}}\n  m_Transitions:\n  - {{fileID: {hub + 100}}}\n  - {{fileID: {hub + 101}}}\n");
            for (int g = 1; g < 8; g++)
                yaml.Append($"  - {{fileID: {hub + 110 + g}}}\n  - {{fileID: {hub + 120 + g}}}\n");
            Transition(hub + 100, hub + 10, "", timed: true);
            Transition(hub + 101, hub + 18, Condition("GestureLeft", 6, 4) + Condition("GestureRight", 6, 4));
            for (int g = 1; g < 8; g++)
            {
                Transition(hub + 110 + g, hub + 10 + g, Condition("GestureLeft", 6, g) + (g == 1 ? Condition("EyeClose", 2, 0) : ""));
                Transition(hub + 120 + g, hub + 10 + g, Condition("GestureRight", 6, g));
            }
            for (int g = 0; g <= 8; g++)
            {
                int id = hub + 10 + g;
                Asset($"Pose{id}.anim", id, $"--- !u!74 &7400000\nAnimationClip:\n  m_Name: Pose{id}\n  m_FloatCurves:\n  - classID: 137\n    attribute: blendShape.Smile\n    path: Face\n    curve:\n      m_Curve:\n      - time: 0\n        value: {bank * 10 + g * 5}\n");
                yaml.Append($"--- !u!1102 &{id}\nAnimatorState:\n  m_Name: Pose{id}\n  m_WriteDefaultValues: 0\n  m_Motion: {{fileID: 7400000, guid: {Guid(id)}}}\n");
                if (g == 0)
                {
                    yaml.Append($"  m_Transitions:\n  - {{fileID: {hub + 200}}}\n");
                    Transition(hub + 200, 0, Condition("GestureLeft", 7, 0), exit: true);
                }
            }
        }
        string source = yaml.ToString();
        ExpressionModel Parse(string text)
        {
            Asset("Face.controller", 2, text);
            using var package = UnityPackage.Open(Path.Combine(root, "Assets", "Avatar.prefab"));
            return VrchatExpressionParser.Parse(package, UnityScene.Parse(descriptor).Doc(1).Root);
        }
        void AllPairs(int bank)
        {
            Bank(bank);
            var model = Parse(source);
            Check(model.Layers.Count == 1, "nested authored bank imported");
            Check(model.Diagnostics.Any(d => d.Contains("nested hand bank") && d.Contains("Bank=" + bank)), "bank defaults diagnosed");
            var table = new GesturePairCompiler(model, model.Clips, _ => 0.23f);
            for (int left = 0; left < 8; left++) for (int right = 0; right < 8; right++)
            {
                int selected = left == 4 && right == 4 ? 8 : Enumerable.Range(1, 7).FirstOrDefault(g => (left == g && g != 1) || right == g);
                float expected = (bank * 10 + selected * 5) / 100f;
                var pose = model.Clips.Concat(table.Generated).Single(c => c.Id == table.Pairs[left * 8 + right]);
                Check(Math.Abs(pose.Curves.Single().Sample(0) - expected) < 0.0001f,
                    "all64 bank values, both-hand override, authored route priority and neutral fallback loop collapse");
            }
        }
        AllPairs(1); AllPairs(2);
        Check(Parse(source.Replace("m_ConditionEvent: Bank", "m_ConditionEvent: Unknown")).Layers.Count == 0, "unknown bank not guessed");
        Check(Parse(source.Replace("m_ConditionEvent: Bank", "m_ConditionEvent: GestureLeft")).Layers.Count == 0, "hand-dependent outer bank not frozen");
        Check(Parse(source.Replace("m_DstStateMachine: {fileID: 120}", "m_DstStateMachine: {fileID: 100}")).Layers.Count == 0, "cyclic/outside child rejected");
        Check(Parse(source.Replace("m_DstStateMachine: {fileID: 120}", "m_HasExitTime: 1\n  m_DstStateMachine: {fileID: 120}")).Layers.Count == 0, "timed bank entry rejected");
        Check(Parse(source.Replace("m_DstState: {fileID: 2010}", "m_DstState: {fileID: 2010}\n  m_TransitionOffset: 0.2")).Layers.Count == 0, "offset fallback rejected");
        Check(Parse(source.Replace("m_ExitTime: 0.75", "m_ExitTime: .nan")).Layers.Count == 0, "invalid timed fallback rejected");
        string fallback = "m_DstState: {fileID: 2010}\n  m_IsExit: 0\n  m_HasExitTime: 1\n  m_ExitTime: 0.75\n  m_Conditions:\n";
        Check(Parse(source.Replace(fallback, fallback + Condition("Bank", 6, 2))).Layers.Count == 0, "conditional delayed selection rejected");
        Asset("Empty.anim", 4, File.ReadAllText(Path.Combine(root, "Assets", "Pose2010.anim")));
        Check(Parse(source).Layers.Count == 0, "animated dispatcher cannot use delayed fallback exception");
        Asset("Empty.anim", 4, empty);
        string feedback = source.Replace("  m_Name: Pose2010\n", "  m_Name: Pose2010\n  m_StateMachineBehaviours:\n  - {fileID: 900}\n") +
            "--- !u!114 &900\nMonoBehaviour:\n  m_Script: {fileID: -706344726, guid: 67cc4cb7839cd3741b63733d5adf0442}\n  parameters:\n  - name: Bank\n    type: 0\n    value: 1\n";
        Check(Parse(feedback).Layers.Count == 0, "driver cannot change frozen bank selection");
        Asset("Pose1012.anim", 1012, "invalid"); AllPairs(2);
        Bank(1); Check(Parse(source).Layers.Count == 0, "invalid active bank rejected while inactive bank does not block selection");
        Console.WriteLine("PASS: nested default banks, all64 routes, delayed empty fallback, one-shot neutral endpoint and unsafe route rejection");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}