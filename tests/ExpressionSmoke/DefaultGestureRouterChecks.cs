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
        string paused = source.Replace("  m_Name: Pose", "  m_Speed: 0\n  m_TimeParameterActive: 1\n  m_TimeParameter: GestureLeftWeight\n  m_Name: Pose");
        CheckBank(1, paused);
        CheckBank(1, paused.Replace("GestureLeftWeight", "GestureRightWeight"));
        Check(Parse(paused.Replace("m_TimeParameterActive: 1", "m_TimeParameterActive: 0")).Layers.Count == 0,
            "zero speed without supported motion time is rejected");
        Check(Parse(paused.Replace("GestureLeftWeight", "UnknownWeight")).Layers.Count == 0,
            "unknown motion time cannot bypass speed validation");
        Defaults(0); CheckBank(0, source);
        File.WriteAllText(Path.Combine(root, "Assets", "Pose1011.anim"), "invalid");
        CheckBank(0, source); // Unselected bank must not block the default bank.
        Defaults(1); Check(Parse(source).Layers.Count == 0, "unavailable selected clip rejected"); Defaults(0);
        Check(Parse(source.Replace("m_ConditionEvent: Gate", "m_ConditionEvent: Unknown")).Layers.Count == 0, "undeclared default not guessed");
        Check(Parse(source.Replace("name: Bank\n    type: 0", "name: GestureLeft\n    type: 0")).Layers.Count == 0, "hand-mutating driver rejected");
        Check(Parse(source.Replace("m_DstState: {fileID: 300}", "m_DstState: {fileID: 200}")).Layers.Count == 0, "self loop without a face cannot create assignments");
        Check(Parse(source.Replace("m_DstState: {fileID: 300}", "m_DstState: {fileID: 300}\n  m_HasExitTime: 1")).Layers.Count == 0, "matching timed transition rejected");
        Check(Parse(source.Replace("m_DstState: {fileID: 1001}", "m_DstState: {fileID: 200}")).Layers.Count == 0, "multi-state cycle rejected");
        CheckMissingNeutral(artifacts);
        GesturePoseGraphChecks.Run(artifacts);
        Console.WriteLine("PASS: authored default banks, descriptor precedence, float guards, multi-step routing, 64 pairs, inactive unsupported clips and unsafe routes");
    }
    private static void CheckMissingNeutral(string artifacts)
    {
        string root = Path.Combine(artifacts, "MissingNeutralFixture");
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        File.Delete(Path.Combine(root, "Assets", "Bad.anim"));
        File.Delete(Path.Combine(root, "Assets", "Bad.anim.meta"));
        string Guid(int id) => id.ToString("x32");
        void Asset(string name, int id, string text)
        {
            File.WriteAllText(Path.Combine(root, "Assets", name), text);
            File.WriteAllText(Path.Combine(root, "Assets", name + ".meta"), "guid: " + Guid(id));
        }
        string descriptor = "--- !u!114 &1\nMonoBehaviour:\n  baseAnimationLayers:\n  - type: 5\n    isDefault: 0\n    animatorController: {fileID: 91, guid: " + Guid(2) + "}\n";
        Asset("Avatar.prefab", 1, descriptor);
        var yaml = new StringBuilder("--- !u!91 &91\nAnimatorController:\n  m_AnimatorLayers:\n");
        for (int hand = 0; hand < 2; hand++)
            yaml.Append($"  - m_Name: Hand{hand}\n    m_StateMachine: {{fileID: {100 + hand}}}\n    m_DefaultWeight: 1\n    m_SyncedLayerIndex: -1\n");
        for (int hand = 0; hand < 2; hand++)
        {
            int start = 1000 + hand * 100;
            string parameter = hand == 0 ? "GestureLeft" : "GestureRight";
            yaml.Append($"--- !u!1107 &{100 + hand}\nAnimatorStateMachine:\n  m_DefaultState: {{fileID: {start}}}\n  m_ChildStates:\n");
            for (int g = 0; g < 8; g++) yaml.Append($"  - m_State: {{fileID: {start + g}}}\n");
            yaml.Append("  m_AnyStateTransitions:\n");
            for (int g = 0; g < 8; g++) yaml.Append($"  - {{fileID: {start + 10 + g}}}\n");
            for (int g = 0; g < 8; g++)
            {
                if (g > 0) Asset($"Pose{start + g}.anim", start + g, $"--- !u!74 &7400000\nAnimationClip:\n  m_Name: Pose{start + g}\n  m_FloatCurves:\n  - attribute: blendShape.Smile\n    path: Face\n    classID: 137\n    curve:\n      m_Curve:\n      - time: 0\n        value: {hand * 10 + g * 10}\n");
                yaml.Append($"--- !u!1102 &{start + g}\nAnimatorState:\n  m_Name: Pose{start + g}\n  m_WriteDefaultValues: 0\n  m_Motion: {{fileID: 7400000, guid: {Guid(g == 0 ? 999 : start + g)}}}\n  m_StateMachineBehaviours:\n  - {{fileID: 900}}\n");
                yaml.Append($"--- !u!1101 &{start + 10 + g}\nAnimatorStateTransition:\n  m_DstState: {{fileID: {start + g}}}\n  m_CanTransitionToSelf: 0\n  m_Conditions:\n  - m_ConditionEvent: {parameter}\n    m_ConditionMode: 6\n    m_EventTreshold: {g}\n");
            }
        }
        yaml.Append("--- !u!114 &900\nMonoBehaviour:\n  m_Script: {fileID: -646210727, guid: 67cc4cb7839cd3741b63733d5adf0442}\n  trackingEyes: 2\n  trackingMouth: 1\n");
        string source = yaml.ToString();
        ExpressionModel Parse(string text, IReadOnlySet<string> names = null)
        {
            Asset("Face.controller", 2, text);
            using var package = UnityPackage.Open(Path.Combine(root, "Assets", "Avatar.prefab"));
            return VrchatExpressionParser.Parse(package, UnityScene.Parse(descriptor).Doc(1).Root, names);
        }
        var model = Parse(source);
        Check(model.Layers.Count == 2, "missing neutral does not discard either hand layer");
        Check(model.Diagnostics.Count(d => d.Contains("missing neutral/default motion(s)")) == 2, "missing reference fallback diagnosed");
        var table = new GesturePairCompiler(model, model.Clips, _ => 0.23f);
        for (int left = 0; left < 8; left++) for (int right = 0; right < 8; right++)
        {
            var pose = model.Clips.Concat(table.Generated).Single(c => c.Id == table.Pairs[left * 8 + right]);
            float expected = right > 0 ? (10 + right * 10) / 100f : left > 0 ? left / 10f : 0.23f;
            Check(Math.Abs(pose.Curves.Single().Sample(0) - expected) < 0.0001f,
                "right layer priority, neutral passes lower layer, both neutral restore base");
        }
        Check(Parse(source.Replace("m_EventTreshold: 7", "m_EventTreshold: 8")).Layers.Count == 0,
            "missing default cannot stand in for an active hand even after caching neutral");
        Check(Parse(source.Replace(Guid(1001), Guid(998))).Layers.Count == 1, "missing non-neutral pose rejects only affected layer");
        Check(Parse(source.Replace("trackingEyes: 2", "trackingHead: 2\n  trackingEyes: 2")).Layers.Count == 0, "body tracking behaviour rejected");
        Check(Parse(source.Replace("guid: " + Guid(999), "guid: " + Guid(2))).Layers.Count == 0, "existing unsupported asset is not treated as missing");
        Asset("Bad.anim", 999, "--- !u!74 &7400000\nAnimationClip:\n  m_FloatCurves:\n  - classID: 1\n    attribute: m_IsActive\n    path: Face\n");
        Check(Parse(source).Layers.Count == 0, "existing invalid neutral clip is not silently cleared");
        var knownNames = new HashSet<string>(StringComparer.Ordinal) { "Face" };
        string activity = "--- !u!74 &7400000\nAnimationClip:\n  m_FloatCurves:\n  - classID: 1\n    attribute: m_IsActive\n    path: Dummy\n    curve:\n      m_Curve:\n      - time: 0\n        value: 1\n";
        Asset("Bad.anim", 999, activity);
        Check(Parse(source).Layers.Count == 0, "unknown target hierarchy does not authorize dropping tracks");
        Check(Parse(source, new HashSet<string> { "Dummy" }).Layers.Count == 0, "existing activity target still rejected");
        Check(Parse(source, knownNames).Layers.Count == 2, "proven absent target becomes empty motion");
        Asset("Bad.anim", 999, activity.Replace("path: Dummy", "path: Face"));
        Check(Parse(source, knownNames).Layers.Count == 0, "live activity curve not ignored");
        Asset("Bad.anim", 999, activity.Replace("classID: 1", "classID: 137"));
        Check(Parse(source, knownNames).Layers.Count == 0, "unsupported component curve not ignored");
        Asset("Bad.anim", 999, activity + "  m_Events:\n  - functionName: Test\n");
        Check(Parse(source, knownNames).Layers.Count == 0, "animation events still reject empty placeholder");
        Asset("Bad.anim", 999, activity);
        string both = source.Replace("--- !u!1107 &100\n", "  - m_Name: Both\n    m_StateMachine: {fileID: 102}\n    m_DefaultWeight: 1\n    m_SyncedLayerIndex: -1\n--- !u!1107 &100\n") +
            "--- !u!1107 &102\nAnimatorStateMachine:\n  m_DefaultState: {fileID: 2000}\n  m_ChildStates:\n  - m_State: {fileID: 2000}\n  - m_State: {fileID: 2001}\n  m_AnyStateTransitions:\n  - {fileID: 3000}\n" +
            "--- !u!1102 &2000\nAnimatorState:\n  m_Name: BothNeutral\n  m_Motion: {fileID: 7400000, guid: " + Guid(999) + "}\n" +
            "--- !u!1102 &2001\nAnimatorState:\n  m_Name: BothPeace\n  m_Motion: {fileID: 7400000, guid: " + Guid(1007) + "}\n  m_Transitions:\n  - {fileID: 3001}\n  - {fileID: 3002}\n" +
            "--- !u!1101 &3000\nAnimatorStateTransition:\n  m_DstState: {fileID: 2001}\n  m_Conditions:\n  - m_ConditionEvent: GestureLeft\n    m_ConditionMode: 6\n    m_EventTreshold: 4\n  - m_ConditionEvent: GestureRight\n    m_ConditionMode: 6\n    m_EventTreshold: 4\n";
        foreach (var (hand, id) in new[] { ("Left", 3001), ("Right", 3002) })
            both += $"--- !u!1101 &{id}\nAnimatorStateTransition:\n  m_IsExit: 1\n  m_Conditions:\n  - m_ConditionEvent: Gesture{hand}\n    m_ConditionMode: 7\n    m_EventTreshold: 4\n";
        // Open hand is deliberately empty even though it is not the default state.
        both = both.Replace("guid: " + Guid(1002), "guid: " + Guid(999)).Replace("guid: " + Guid(1102), "guid: " + Guid(999));
        var bothModel = Parse(both, knownNames);
        Check(bothModel.Layers.Count == 3, "paired Any State selector with Exit routes imported");
        var bothTable = new GesturePairCompiler(bothModel, bothModel.Clips, _ => 0.23f);
        for (int left = 0; left < 8; left++) for (int right = 0; right < 8; right++)
        {
            var pose = bothModel.Clips.Concat(bothTable.Generated).Single(c => c.Id == bothTable.Pairs[left * 8 + right]);
            float expected = left == 4 && right == 4 ? 0.7f : right is not (0 or 2) ? (10 + right * 10) / 100f :
                left is not (0 or 2) ? left / 10f : 0.23f;
            Check(Math.Abs(pose.Curves.Single().Sample(0) - expected) < 0.0001f, "all64 paired overrides and open-hand pass-through");
        }
        Check(Parse(both.Replace("m_IsExit: 1", "m_DstState: {fileID: 2001}"), knownNames).Layers.Count == 2,
            "state-to-state latch is not accepted as paired Exit selector");
        Console.WriteLine("PASS: absent activity targets, real-target rejection, both-hand overrides and empty open poses");        Console.WriteLine("PASS: missing neutral fallback, all64 outputs, right priority and unsupported/missing active motion guards");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
