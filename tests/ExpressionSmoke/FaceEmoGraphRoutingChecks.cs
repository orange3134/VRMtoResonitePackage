using System.Text;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class FaceEmoGraphRoutingChecks
{
    public static void Run(string artifacts)
    {
        string root = Path.Combine(artifacts, "GraphRoutingFixture");
        Directory.CreateDirectory(Path.Combine(root, "Assets")); Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        string Guid(int i) => i.ToString("x32");
        void Asset(string name, int id, string text)
        {
            File.WriteAllText(Path.Combine(root, "Assets", name), text);
            File.WriteAllText(Path.Combine(root, "Assets", name + ".meta"), "guid: " + Guid(id));
        }
        string Descriptor(bool extra) => "--- !u!114 &1\nMonoBehaviour:\n  baseAnimationLayers:\n  - type: 5\n    animatorController: {fileID: 91, guid: " + Guid(2) + "}\n" +
            (extra ? "  - type: 5\n    animatorController: {fileID: 91, guid: " + Guid(4) + "}\n" : "");
        Asset("Avatar.prefab", 1, Descriptor(false));
        for (int i = 1; i <= 6; i++) Asset($"Pose{i}.anim", 1000 + i,
            $"--- !u!74 &7400000\nAnimationClip:\n  m_Name: Pose{i}\n  m_FloatCurves:\n  - classID: 137\n    path: Face\n    attribute: blendShape.Smile\n    curve:\n      m_Curve:\n      - time: 0\n        value: {i * 10 + 12}\n");
        string Doc(int cls, int id, string body) => $"--- !u!{cls} &{id}\nObject:\n" + body;
        string Refs(string key, params int[] ids) => "  " + key + ":\n" + string.Concat(ids.Select(id => $"  - {{fileID: {id}}}\n"));
        string C(string name, int mode, int value) => $"  - m_ConditionEvent: {name}\n    m_ConditionMode: {mode}\n    m_EventTreshold: {value}\n";
        string T(int id, int state, string conditions = "", int machine = 0, bool exit = false, string extra = "") =>
            Doc(1101, id, $"  m_DstState: {{fileID: {state}}}\n  m_DstStateMachine: {{fileID: {machine}}}\n  m_IsExit: {(exit ? 1 : 0)}\n" + extra + "  m_Conditions:\n" + conditions);
        string S(int id, int clip, int[] transitions = null, int[] behaviours = null) => Doc(1102, id,
            $"  m_Name: State{id}\n  m_Motion: {{fileID: {(clip == 0 ? "0" : "7400000, guid: " + Guid(1000 + clip))}}}\n" +
            Refs("m_Transitions", transitions ?? Array.Empty<int>()) + Refs("m_StateMachineBehaviours", behaviours ?? Array.Empty<int>()));
        string M(int id, int initial, int[] states, int[] children = null, int[] entries = null, int[] any = null, string extra = "") => Doc(1107, id,
            $"  m_Name: Machine{id}\n  m_DefaultState: {{fileID: {initial}}}\n  m_ChildStates:\n" + string.Concat(states.Select(s => $"  - m_State: {{fileID: {s}}}\n")) +
            "  m_ChildStateMachines:\n" + string.Concat((children ?? Array.Empty<int>()).Select(m => $"  - m_StateMachine: {{fileID: {m}}}\n")) +
            Refs("m_EntryTransitions", entries ?? Array.Empty<int>()) + Refs("m_AnyStateTransitions", any ?? Array.Empty<int>()) + extra);
        string Write(string name, int type, int value = 0, string source = "") => $"  - name: {name}\n    type: {type}\n    value: {value}\n    source: {source}\n";
        string Driver(int id, string writes) => Doc(114, id, "  m_Script: {fileID: -706344726, guid: 67cc4cb7839cd3741b63733d5adf0442}\n  parameters:\n" + writes);
        string Controller(string layers) => Doc(91, 91, "  m_AnimatorParameters:\n  - m_Name: A\n    m_Type: 3\n  - m_Name: B\n    m_Type: 3\n  - m_Name: Flag\n    m_Type: 4\n  m_AnimatorLayers:\n" + layers);
        string Layer(int machine) => $"  - m_Name: Layer{machine}\n    m_StateMachine: {{fileID: {machine}}}\n";
        string mainMachine = M(100, 200, new[] { 200, 201, 202, 203, 205, 207 }, new[] { 110 });
        string idle = S(200, 0, new[] { 300, 310 });
        string middle = S(201, 3, new[] { 301 });
        string source = Controller(Layer(800) + Layer(100)) + mainMachine + idle + middle + S(202, 1) + S(203, 2) + S(205, 5) + S(207, 0) +
            T(300, 201, C("GestureRight", 6, 1)) + T(310, 203, C("GestureRight", 3, 1)) + T(301, 202) +
            M(110, 204, new[] { 204 }) + S(204, 4) +
            M(800, 810, new[] { 810, 811 }, any: new[] { 812 }) + S(810, 0) + S(811, 6) + T(812, 811, C("GestureRight", 6, 1));
        ExpressionModel Parse(string text, string extra = null, bool sameController = false)
        {
            Asset("Graph.controller", 2, text);
            if (extra != null) Asset("Extra.controller", 4, extra);
            using var package = UnityPackage.Open(Path.Combine(root, "Assets", "Avatar.prefab"));
            var model = VrchatExpressionParser.Parse(package, UnityScene.Parse(Descriptor(extra != null || sameController).Replace(Guid(4), sameController ? Guid(2) : Guid(4))).Doc(1).Root);
            VrchatExpressionDetection.FilterFaceCurves(model, new Dictionary<ExpressionBinding, float> { [new("Face", "Smile")] = 0.12f });
            return model;
        }
        void Table(ExpressionModel model, Func<int, int, int> expected)
        {
            var compiler = new GesturePairCompiler(model, model.Clips, _ => 0.12f);
            for (int l = 0; l < 8; l++) for (int r = 0; r < 8; r++)
            {
                var pose = model.Clips.Concat(compiler.Generated).Single(c => c.Id == compiler.Pairs[l * 8 + r]);
                Check(Math.Abs(pose.Curves.Single().Keys[^1].Value - (0.12f + expected(l, r) * 0.1f)) < 0.00001f, $"graph oracle {l},{r}");
            }
        }
        int Expected(int l, int r) => r == 0 ? 0 : r == 1 ? 1 : 2;
        var model = Parse(source); Table(model, Expected);
        Table(Parse(source, sameController: true), Expected);
        Check(model.Clips.Select(c => c.Name).ToHashSet().SetEquals(new[] { "Pose1", "Pose2", "Pose3", "Pose4" }),
            "Catalog first: keep FaceEmo candidates, exclude unseen motion and shadowed later set");
        Check(model.Diagnostics.Any(d => d.Contains("56/64 gesture pairs")), "driver-free multi-hop routing is reported");
        string any = source.Replace(mainMachine, M(100, 200, new[] { 200, 201, 202, 203, 205, 207 }, new[] { 110 }, any: new[] { 300, 310 }))
            .Replace(idle, S(200, 0)).Replace(T(300, 201, C("GestureRight", 6, 1)), T(300, 202, C("GestureRight", 6, 1)));
        Table(Parse(any), Expected);
        string priorityTransition = T(311, 203, C("GestureRight", 3, 0), extra: "  m_CanTransitionToSelf: 1\n");
        string priority = any.Replace(Refs("m_AnyStateTransitions", 300, 310), Refs("m_AnyStateTransitions", 311, 300, 310)) + priorityTransition;
        Table(Parse(priority), (l, r) => r > 0 ? 2 : 0);
        Table(Parse(priority.Replace(priorityTransition, priorityTransition.Replace("  m_Conditions:", "  m_Mute: 1\n  m_Conditions:"))), Expected);
        Table(Parse(priority.Replace(T(300, 202, C("GestureRight", 6, 1)), T(300, 202, C("GestureRight", 6, 1), extra: "  m_Solo: 1\n"))),
            (l, r) => r == 1 ? 1 : 0);
        string entry = source.Replace(mainMachine, M(100, 200, new[] { 200, 201, 202, 203, 205, 207 }, new[] { 110 }, entries: new[] { 300, 310 }))
            .Replace(idle, S(200, 0, new[] { 350 })) + T(350, 0, C("GestureRight", 3, 0), exit: true);
        Table(Parse(entry), Expected);
        // A nested Exit uses the parent's explicit state-machine transition, then matches by clip identity.
        string nested = source.Replace(mainMachine, M(100, 200, new[] { 200, 202, 203, 205, 207 }, new[] { 101, 110, 111 },
            extra: "  m_StateMachineTransitions:\n  - first: {fileID: 101}\n    second:\n    - {fileID: 901}\n"))
            .Replace(T(300, 201, C("GestureRight", 6, 1)), T(300, 0, C("GestureRight", 6, 1), machine: 101))
            .Replace(T(301, 202), T(301, 0, exit: true)) + M(101, 201, new[] { 201 }) + T(901, 202) + M(111, 206, new[] { 206 }) + S(206, 1);
        Table(Parse(nested), Expected);
        string deep = nested.Replace(T(300, 0, C("GestureRight", 6, 1), machine: 101), T(300, 0, C("GestureRight", 6, 1), machine: 102))
            .Replace(M(101, 201, new[] { 201 }), M(101, 210, new[] { 210 }, new[] { 102 },
                extra: Refs("m_StateMachineBehaviours", 500) + "  m_StateMachineTransitions:\n  - first: {fileID: 102}\n    second:\n    - {fileID: 903}\n"))
            .Replace(T(901, 202), T(901, 202, C("B", 6, 11))) + S(210, 0) +
            M(102, 201, new[] { 201 }, extra: Refs("m_StateMachineBehaviours", 501)) + T(903, 0, exit: true) +
            Driver(500, Write("A", 0, 11)) + Driver(501, Write("B", 3, source: "A"));
        Table(Parse(deep), Expected);
        // Set -> Copy -> Add across intermediate states, not an eight-state input layer.
        string chain = source.Replace(middle, S(201, 0, new[] { 301 }, new[] { 500 }))
            .Replace(S(207, 0), S(207, 0, new[] { 307 }, new[] { 501 }))
            .Replace(T(301, 202), T(301, 207, C("A", 6, 11))) + T(307, 202, C("B", 6, 13)) +
            Driver(500, Write("A", 0, 11)) + Driver(501, Write("B", 3, source: "A") + Write("B", 1, 2));
        Table(Parse(chain), Expected);
        // A parameter producer can live in an appended FX and be visited after the consumer.
        string external = source.Replace(middle, S(201, 0, new[] { 301 })).Replace(T(301, 202), T(301, 202, C("A", 6, 11)));
        string producer = Controller(Layer(900)) + M(900, 910, new[] { 910, 911 }, any: new[] { 912 }) +
            S(910, 0, behaviours: new[] { 920 }) + S(911, 0, behaviours: new[] { 921 }) + T(912, 911, C("GestureRight", 6, 1)) +
            Driver(920, Write("A", 0, 0)) + Driver(921, Write("A", 0, 11));
        Table(Parse(external, producer), Expected);
        // A deterministic route to an excluded bool-only candidate must not expand the Catalog.
        string unseen = source.Replace(middle, S(201, 3, new[] { 301 }, new[] { 500 }))
            .Replace(T(301, 202), T(301, 205, C("Flag", 1, 0))) + Driver(500, Write("Flag", 0, 1));
        var restricted = Parse(unseen); Table(restricted, (l, r) => r > 1 ? 2 : 0);
        Check(!restricted.Clips.Any(c => c.Name is "Pose5" or "Pose6"), "known route cannot add unseen clips or later patterns");
        // Unknown graphs keep the original direct FaceEmo assignment at its layer priority.
        string cyclic = source.Replace(T(301, 202), T(301, 207)).Replace(S(207, 0), S(207, 0, new[] { 307 })) + T(307, 201);
        var cycle = Parse(cyclic); Table(cycle, (l, r) => r == 1 ? 3 : Expected(l, r));
        Check(cycle.Diagnostics.Any(d => d.Contains("cyclic transitions")), "cycles terminate and diagnose without discarding candidates");
        string broken = source.Replace(mainMachine, mainMachine.Replace("m_DefaultState: {fileID: 200}", "m_DefaultState: {fileID: 999}"));
        var fallback = Parse(broken); Table(fallback, (l, r) => r == 1 ? 3 : 0);
        // 1D BlendTrees select only exact/end child clips already recognized by FaceEmo.
        string tree = source.Replace(Controller(Layer(800) + Layer(100)), Controller(Layer(100))).Replace(idle, S(200, 0).Replace("m_Motion: {fileID: 0}", "m_Motion: {fileID: 990}")) +
            Doc(206, 990, "  m_BlendType: 0\n  m_BlendParameter: GestureRight\n  m_Childs:\n  - m_Threshold: 0\n    m_Motion: {fileID: 7400000, guid: " + Guid(1001) + "}\n  - m_Threshold: 7\n    m_Motion: {fileID: 7400000, guid: " + Guid(1002) + "}\n");
        var blended = Parse(tree); Table(blended, (l, r) => r == 0 ? 1 : r == 7 ? 2 : 0);
        Table(Parse(tree.Replace("m_BlendParameter: GestureRight", "m_BlendParameter: GestureRightWeight").Replace("m_Threshold: 7", "m_Threshold: 1")), (l, r) => r == 1 ? 2 : 1);
        Check(blended.Diagnostics.Any(d => d.Contains("blended pose is not a Catalog entry")), "interpolation does not invent a Catalog pose");
        Console.WriteLine("PASS: Catalog-first graph routing, driver-free/AnyState/ordinary/nested Exit paths, Set-Copy-Add chain, cross-FX writers, cycles, first-set boundaries and discrete BlendTrees");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
