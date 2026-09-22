using System.Text;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class GesturePoseGraphChecks
{
    public static void Run(string artifacts)
    {
        var fixture = new Fixture(Path.Combine(artifacts, "GesturePoseGraphFixture"));
        foreach (bool gated in new[] { true, false })
        {
            string source = AnyStateGraph(gated);
            var model = fixture.Parse(source);
            AllPairs(model, "Any State relay cycle");
            Check(model.Diagnostics.Any(d => d.Contains("single-pose routing cycles")), "neutral cycle reduction is diagnosed");
            Check(model.Diagnostics.Any(d => d.Contains(gated ? "FaceFix=0" : "all reachable states converge")),
                "frozen gate or previous-state convergence is diagnosed");
            Reject(source.Replace("m_Name: EmptyRelay\n", "m_Name: EmptyRelay\n  m_StateMachineBehaviours:\n  - {fileID: 9001}\n") +
                "--- !u!114 &9001\nMonoBehaviour:\n  m_Script: {fileID: 11400000, guid: " + Guid(999) + "}\n",
                "unknown relay behaviour cannot be hidden by a static pose cycle");
            Reject(source.Replace("m_Name: EmptyRelay\n", "m_Name: EmptyRelay\n  m_StateMachineBehaviours:\n  - {fileID: 9001}\n") +
                "--- !u!114 &9001\nMonoBehaviour:\n  m_Script: {fileID: -706344726, guid: 67cc4cb7839cd3741b63733d5adf0442}\n  parameters:\n  - name: GestureLeft\n    type: 0\n    value: 1\n",
                "hand-mutating relay driver cannot be hidden by a static pose cycle");
            Reject(source.Replace("m_Name: EmptyRelay\n", "m_Name: EmptyRelay\n  m_Speed: 0\n"),
                "invalid playback on an empty cycle member is rejected");
            Reject(source.Replace("m_Motion: {fileID: 0}", Motion(17)), "two different poses in the neutral cycle are rejected");
            Reject(source.Replace(Motion(10), "m_Motion: {fileID: 0}"), "all-empty cycle cannot supply a neutral face");
            Reject(source.Replace("m_Motion: {fileID: 0}", Motion(999)), "missing relay motion cannot be interpreted as empty");
            Reject(source.Replace("--- !u!1101 &6000\nAnimatorStateTransition:\n", "--- !u!1101 &6000\nAnimatorStateTransition:\n  m_HasExitTime: 1\n"),
                "timed transition in a pose cycle is rejected");
            void Reject(string text, string message) => Check(fixture.Parse(text).Layers.Count == 0, message);
        }

        // Arbitrary state counts and names, ordered comparisons and multiple empty
        // relays must be recognized by their resolved face pose, not graph shape.
        foreach (int relays in new[] { 2, 4 })
            foreach (bool entryExit in new[] { false, true })
                AllPairs(fixture.Parse(RelayGraph(relays, entryExit)), "multi-relay and same-clip cycle");

        string handOnly = AnyStateGraph(false);
        Check(fixture.Parse(handOnly.Replace("  - {fileID: 5107}\n", "").Replace("  - {fileID: 5207}\n", "")).Layers.Count == 0,
            "an uncovered pair retaining a previous face is rejected even if Entry resolves");
        string relay = RelayGraph(3, false);
        Check(fixture.Parse(relay.Replace("  m_Name: Alias_7\n", "  m_Name: Alias_7\n  m_Speed: 2\n")).Layers.Count == 0,
            "same clip with different playback is not the same pose");
        Console.WriteLine("PASS: Any State neutral cycles, authored FaceFix default, arbitrary relay graphs, same-clip states, ordered comparisons, Entry/Exit, all64 outputs and unsafe-cycle rejection");
    }

    private static string AnyStateGraph(bool gated)
    {
        var graph = new Graph(1000, gated);
        graph.State(2000, "EmptyRelay", null);
        for (int g = 0; g < 8; g++) graph.State(1000 + g, g == 0 ? "NeutralFace" : "Face_" + g, 10 + g, g == 0);
        string gate = gated ? Condition("FaceFix", 6, 0) : "";
        for (int g = 1; g < 8; g++) graph.Transition(5100 + g, null, 1000 + g, Condition("GestureLeft", 6, g) + gate);
        for (int g = 1; g < 8; g++) graph.Transition(5200 + g, null, 1000 + g, Condition("GestureLeft", 6, 0) + Condition("GestureRight", 6, g) + gate);
        graph.Transition(5000, null, 2000, Condition("GestureLeft", 6, 0) + Condition("GestureRight", 6, 0) + gate);
        graph.Transition(6000, 2000, 1000);
        return graph.Source();
    }

    private static string RelayGraph(int relayCount, bool entryExit)
    {
        var graph = new Graph(2000, false);
        for (int i = 0; i < relayCount; i++)
        {
            graph.State(2000 + i, "Unrelated_" + (91 - i * 13), null);
            if (i + 1 < relayCount) graph.Transition(6000 + i, 2000 + i, 2001 + i);
        }
        for (int g = 0; g < 8; g++)
        {
            graph.State(1000 + g, "Destination_" + (57 - g * 3), 10 + g, g == 0);
            graph.State(3000 + g, "Alias_" + g, 10 + g);
            graph.Transition(7000 + g, 3000 + g, 1000 + g);
            graph.Transition(7100 + g, 1000 + g, entryExit ? 0 : 2000, exit: entryExit);
        }
        // Greater/Less express each integer value without Equals. Right routes
        // require a nonzero hand with NotEqual, after all left-hand priorities.
        for (int g = 1; g < 8; g++)
            graph.Transition(5100 + g, 1999 + relayCount, 3000 + g,
                Condition("GestureLeft", 3, g - 1) + Condition("GestureLeft", 4, g + 1));
        for (int g = 1; g < 8; g++)
            graph.Transition(5200 + g, 1999 + relayCount, 3000 + g,
                Condition("GestureRight", 7, 0) + Condition("GestureRight", 3, g - 1) + Condition("GestureRight", 4, g + 1));
        graph.Transition(5000, 1999 + relayCount, 3000);
        if (entryExit) graph.Transition(8000, -1, 2000, Condition("GestureLeft", 4, 8));
        return graph.Source();
    }

    private static void AllPairs(ExpressionModel model, string context)
    {
        Check(model.Layers.Count == 1, context + ": one face layer imported; " + string.Join("; ", model.Diagnostics));
        var table = new GesturePairCompiler(model, model.Clips, _ => 0.93f);
        var clips = model.Clips.Concat(table.Generated).ToDictionary(c => c.Id);
        for (int left = 0; left < 8; left++) for (int right = 0; right < 8; right++)
        {
            Check(clips.TryGetValue(table.Pairs[left * 8 + right], out var pose), context + ": every pair has a pose");
            float expected = (12 + (left > 0 ? left : right) * 9) / 100f;
            Check(pose.Curves.Count == 1 && Math.Abs(pose.Curves.Single().Sample(0) - expected) < 0.0001f,
                context + $": pair ({left}, {right}) has its authored pose, including neutral and hand priority");
        }
    }

    private static string Guid(int id) => id.ToString("x32");
    private static string Motion(int clip) => $"m_Motion: {{fileID: 7400000, guid: {Guid(clip)}}}";
    private static string Condition(string name, int mode, int value) =>
        $"  - m_ConditionEvent: {name}\n    m_ConditionMode: {mode}\n    m_EventTreshold: {value}\n";
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Fixture
    {
        private readonly string root;
        private readonly string descriptor = "--- !u!114 &1\nMonoBehaviour:\n  baseAnimationLayers:\n  - type: 5\n    isDefault: 0\n    animatorController: {fileID: 91, guid: " + Guid(2) + "}\n";
        public Fixture(string root)
        {
            this.root = root;
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            Asset("Avatar.prefab", 1, descriptor);
            for (int g = 0; g < 8; g++) Asset($"Pose{g}.anim", 10 + g,
                $"--- !u!74 &7400000\nAnimationClip:\n  m_Name: Pose{g}\n  m_FloatCurves:\n  - attribute: blendShape.Smile\n    path: Face\n    classID: 137\n    curve:\n      m_Curve:\n      - time: 0\n        value: {12 + g * 9}\n");
        }
        private void Asset(string name, int id, string text)
        {
            File.WriteAllText(Path.Combine(root, "Assets", name), text);
            File.WriteAllText(Path.Combine(root, "Assets", name + ".meta"), "guid: " + Guid(id));
        }
        public ExpressionModel Parse(string controller)
        {
            Asset("Face.controller", 2, controller);
            using var package = UnityPackage.Open(Path.Combine(root, "Assets", "Avatar.prefab"));
            return VrchatExpressionParser.Parse(package, UnityScene.Parse(descriptor).Doc(1).Root);
        }
    }

    private sealed class Graph(int defaultState, bool gated)
    {
        private readonly Dictionary<int, string> states = new();
        private readonly List<(int Id, int? Owner, string Text)> transitions = new();
        public void State(int id, string name, int? clip, bool tracking = false) => states.Add(id,
            $"--- !u!1102 &{id}\nAnimatorState:\n  m_Name: {name}\n  m_WriteDefaultValues: 0\n  " +
            (clip.HasValue ? Motion(clip.Value) : "m_Motion: {fileID: 0}") + "\n" +
            (tracking ? "  m_StateMachineBehaviours:\n  - {fileID: 9000}\n" : ""));
        public void Transition(int id, int? owner, int destination, string conditions = "", bool exit = false) => transitions.Add((id, owner,
            $"--- !u!{(owner == -1 ? 1109 : 1101)} &{id}\n{(owner == -1 ? "AnimatorTransition" : "AnimatorStateTransition")}:\n  m_DstState: {{fileID: {destination}}}\n  m_CanTransitionToSelf: 0\n" +
            (exit ? "  m_IsExit: 1\n" : "") + "  m_Conditions:\n" + conditions));
        public string Source()
        {
            var yaml = new StringBuilder("--- !u!91 &91\nAnimatorController:\n" +
                (gated ? "  m_AnimatorParameters:\n  - m_Name: FaceFix\n    m_Type: 3\n    m_DefaultInt: 0\n" : "") +
                "  m_AnimatorLayers:\n  - m_Name: Faces\n    m_StateMachine: {fileID: 100}\n    m_SyncedLayerIndex: -1\n--- !u!1107 &100\nAnimatorStateMachine:\n" +
                $"  m_DefaultState: {{fileID: {defaultState}}}\n  m_ChildStates:\n");
            foreach (int id in states.Keys) yaml.Append($"  - m_State: {{fileID: {id}}}\n");
            References(null, "m_AnyStateTransitions");
            References(-1, "m_EntryTransitions");
            foreach (var state in states) { yaml.Append(state.Value); References(state.Key, "m_Transitions"); }
            foreach (var transition in transitions) yaml.Append(transition.Text);
            yaml.Append("--- !u!114 &9000\nMonoBehaviour:\n  m_Script: {fileID: -646210727, guid: 67cc4cb7839cd3741b63733d5adf0442}\n  trackingEyes: 2\n  trackingMouth: 1\n");
            return yaml.ToString();
            void References(int? owner, string key)
            {
                var matching = transitions.Where(t => t.Owner == owner).ToArray();
                if (matching.Length == 0) return;
                yaml.Append("  " + key + ":\n");
                foreach (var transition in matching) yaml.Append($"  - {{fileID: {transition.Id}}}\n");
            }
        }
    }
}
