using System.Text;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class DrivenGestureRouterChecks
{
    internal static void Run(string artifacts)
    {
        string root = Path.Combine(artifacts, "DrivenGestureFixture");
        Directory.CreateDirectory(Path.Combine(root, "Assets")); Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        string Guid(int i) => i.ToString("x32");
        void Asset(string name, int id, string value)
        {
            File.WriteAllText(Path.Combine(root, "Assets", name), value);
            File.WriteAllText(Path.Combine(root, "Assets", name + ".meta"), "guid: " + Guid(id));
        }
        var descriptor = UnityScene.Parse("--- !u!114 &1\nMonoBehaviour:\n  baseAnimationLayers: []\n").Doc(1).Root;
        Asset("Avatar.prefab", 1, "--- !u!1 &1\nGameObject:\n  m_Name: Avatar\n");
        var component = UnityScene.Parse("--- !u!114 &1\nMonoBehaviour:\n  m_Script: {guid: " + VrchatModularExpressionInputs.MergeAnimatorGuid +
            "}\n  m_Enabled: 1\n  layerType: 5\n  pathMode: 1\n  animator: {fileID: 91, guid: " + Guid(2) + "}\n").Doc(1).Root;
        var effective = VrchatModularExpressionInputs.Create(descriptor, new[] { component });
        Check(effective["baseAnimationLayers"].Seq.Count == 1 && descriptor["baseAnimationLayers"].Seq.Count == 0, "merge does not mutate source descriptor");
        foreach (var (key, value) in new[] { ("pathMode", "0"), ("mergeAnimatorMode", "1"), ("m_Enabled", "0"), ("layerType", "2") })
        {
            var bad = UnityPropertyOverrides.Clone(component); bad.Map[key] = new() { ScalarValue = value };
            Check(VrchatModularExpressionInputs.Create(descriptor, new[] { bad })["baseAnimationLayers"].Seq.Count == 0, "unsupported or disabled merge not imported");
        }
        var defaults = UnityScene.Parse("--- !u!114 &1\nMonoBehaviour:\n  m_Script: {guid: 71a96d4ea0c344f39e277d82035bf9bd}\n  parameters:\n  - nameOrPrefix: Bank\n    syncType: 1\n    defaultValue: 2\n").Doc(1).Root;
        var defaultsModel = new ExpressionModel(); defaultsModel.Parameters["Bank"] = new("Bank", 3, 1);
        VrchatModularExpressionInputs.ApplyDefaults(defaultsModel, new[] { defaults });
        Check(defaultsModel.Parameters["Bank"].Default == 2, "nonzero MA default applies even without explicit flag");
        defaults["parameters"].Seq[0].Map["defaultValue"] = new() { ScalarValue = "0" };
        VrchatModularExpressionInputs.ApplyDefaults(defaultsModel, new[] { defaults });
        Check(defaultsModel.Parameters["Bank"].Default == 2, "unspecified zero does not override controller default");
        defaults["parameters"].Seq[0].Map["hasExplicitDefaultValue"] = new() { ScalarValue = "1" };
        VrchatModularExpressionInputs.ApplyDefaults(defaultsModel, new[] { defaults });
        Check(defaultsModel.Parameters["Bank"].Default == 0, "explicit zero overrides default");        var b = new StringBuilder("--- !u!91 &91\nAnimatorController:\n  m_AnimatorParameters:\n");
        foreach (string name in new[] { "Input", "Selected", "TrackingFlag" }) b.Append($"  - m_Name: {name}\n    m_Type: 3\n    m_DefaultInt: 0\n");
        b.Append("  m_AnimatorLayers:\n  - m_Name: Base\n    m_StateMachine: {fileID: 0}\n");
        // Reversed producer order requires fixed-point propagation, not one sequential pass.
        foreach (var (name, id, weight) in new[] { ("Map", 200, 0), ("Input", 100, 0), ("Face", 300, 1) })
            b.Append($"  - m_Name: {name}\n    m_StateMachine: {{fileID: {id}}}\n    m_DefaultWeight: {weight}\n    m_SyncedLayerIndex: -1\n");
        string Condition(string name, int value) => $"  - m_ConditionEvent: {name}\n    m_ConditionMode: 6\n    m_EventTreshold: {value}\n";
        void Machine(int id, int first, int count)
        {
            b.Append($"--- !u!1107 &{id}\nAnimatorStateMachine:\n  m_DefaultState: {{fileID: {first}}}\n  m_ChildStates:\n");
            for (int i = 0; i < count; i++) b.Append($"  - m_State: {{fileID: {first + i}}}\n");
            b.Append("  m_EntryTransitions:\n");
            for (int i = 0; i < count; i++) b.Append($"  - {{fileID: {first + 100 + i}}}\n");
        }
        void Driver(int id, string name, int value) => b.Append($"--- !u!114 &{id}\nMonoBehaviour:\n  m_Script: {{fileID: -706344726, guid: 67cc4cb7839cd3741b63733d5adf0442}}\n  parameters:\n  - type: 0\n    name: {name}\n    value: {value}\n");
        Machine(100, 1000, 8);
        for (int i = 0; i < 8; i++)
        {
            b.Append($"--- !u!1102 &{1000 + i}\nAnimatorState:\n  m_Name: Hand{i}\n  m_StateMachineBehaviours:\n  - {{fileID: {1200 + i}}}\n");
            Driver(1200 + i, "Input", i);
            b.Append($"--- !u!1109 &{1100 + i}\nAnimatorTransition:\n  m_DstState: {{fileID: {1000 + i}}}\n  m_Conditions:\n" + Condition("GestureLeft", i));
        }
        Machine(200, 2000, 64);
        for (int i = 0; i < 64; i++)
        {
            b.Append($"--- !u!1102 &{2000 + i}\nAnimatorState:\n  m_Name: Map{i}\n  m_StateMachineBehaviours:\n  - {{fileID: {2200 + i}}}\n");
            Driver(2200 + i, "Selected", i);
            b.Append($"--- !u!1109 &{2100 + i}\nAnimatorTransition:\n  m_DstState: {{fileID: {2000 + i}}}\n  m_Conditions:\n" + Condition("Input", i / 8) + Condition("GestureRight", i % 8));
        }
        b.Append("--- !u!1107 &300\nAnimatorStateMachine:\n  m_ChildStateMachines:\n  - m_StateMachine: {fileID: 301}\n  m_EntryTransitions:\n  - {fileID: 302}\n--- !u!1109 &302\nAnimatorTransition:\n  m_DstStateMachine: {fileID: 301}\n  m_Conditions:\n" + Condition("IsLocal", 1));
        Machine(301, 3000, 64);
        for (int i = 0; i < 64; i++)
        {
            Asset($"Pose{i}.anim", 4000 + i, $"--- !u!74 &7400000\nAnimationClip:\n  m_Name: Pose{i}\n  m_FloatCurves:\n  - path: Face\n    attribute: blendShape.Smile\n    classID: 137\n    curve:\n      m_Curve:\n      - time: 0\n        value: {i}\n");
            b.Append($"--- !u!1102 &{3000 + i}\nAnimatorState:\n  m_Name: Pose{i}\n  m_Motion: {{fileID: 7400000, guid: {Guid(4000 + i)}}}\n  m_StateMachineBehaviours:\n  - {{fileID: 3300}}\n");
            b.Append($"--- !u!1109 &{3100 + i}\nAnimatorTransition:\n  m_DstState: {{fileID: {3000 + i}}}\n  m_Conditions:\n" + Condition("Selected", i));
        }
        Driver(3300, "TrackingFlag", 1);
        string source = b.ToString();
        ExpressionModel Parse(string yaml)
        {
            Asset("Face.controller", 2, yaml);
            using var p = UnityPackage.Open(Path.Combine(root, "Assets", "Avatar.prefab"));
            return VrchatExpressionParser.Parse(p, effective);
        }
        var model = Parse(source); var table = new GesturePairCompiler(model, model.Clips, _ => 0);
        Check(model.Layers.Count == 1, "cascade imports consuming layer only");
        for (int i = 0; i < 64; i++)
        {
            var pose = model.Clips.Concat(table.Generated).Single(c => c.Id == table.Pairs[i]);
            Check(Math.Abs(pose.Curves.Single().Keys[^1].Value - i / 100f) < 0.00001f, "all64 cascaded final poses");
        }
        void Reject(string yaml, string reason) => Check(Parse(yaml).Layers.Count == 0, reason);
        Reject(source.Replace("  - type: 0", "  - type: 1"), "Add is not Set");
        Reject(source.Replace("  - type: 0", "  - type: 2"), "Random is not Set");
        Reject(source.Replace("    name: Input", "    name: GestureLeft"), "hand mutation rejected");
        Reject(source.Replace("    name: TrackingFlag", "    name: Selected"), "consumer feedback rejected");
        Reject(source.Replace("m_DstStateMachine: {fileID: 301}", "m_DstStateMachine: {fileID: 300}"), "machine cycle rejected");
        Reject(source.Replace("m_DstState: {fileID: 3001}", "m_DstState: {fileID: 1001}"), "foreign state rejected");
        Reject(source.Replace("m_DstState: {fileID: 3001}", "m_DstState: {fileID: 3001}\n  m_HasExitTime: 1"), "timed selection rejected");
        Reject(source.Replace("guid: " + Guid(4001), "guid: " + Guid(9999)), "missing active pose rejected");
        Reject(source.Replace("  - type: 0", "  - type: 3"), "Copy is not Set");
        Reject(source.Replace("m_ConditionEvent: Input", "m_ConditionEvent: Selected"), "non-converging control cycle rejected");
        Reject(source.Replace("    value: 1\n", "    value: NaN\n"), "non-finite Set rejected");
        Asset("AnimatedParameter.anim", 5000, "--- !u!74 &7400000\nAnimationClip:\n  m_FloatCurves:\n  - attribute: Selected\n    classID: 95\n");
        Reject(source.Replace("  m_Name: Hand", "  m_Motion: {fileID: 7400000, guid: " + Guid(5000) + "}\n  m_Name: Hand"), "animated Animator parameters rejected");
        Console.WriteLine("PASS: MA absolute append, cascaded zero-weight Set selectors, nested routes, local defaults, all64 poses and unsafe-route rejection");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}