using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class GestureRouterChecks
{
    public static void Run(string artifacts)
    {
        string root = Path.Combine(artifacts, "RouterFixture");
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        const string controller = "11111111111111111111111111111111";
        const string empty = "22222222222222222222222222222222";
        const string neutral = "33333333333333333333333333333333";
        const string smile = "44444444444444444444444444444444";
        void Asset(string name, string guid, string yaml)
        {
            File.WriteAllText(Path.Combine(root, "Assets", name), yaml);
            File.WriteAllText(Path.Combine(root, "Assets", name + ".meta"), "guid: " + guid);
        }
        string descriptor = $$"""
            --- !u!114 &1
            MonoBehaviour:
              baseAnimationLayers:
              - type: 5
                isDefault: 0
                animatorController: {fileID: 91, guid: {{controller}}}
            """;
        Asset("Avatar.prefab", "55555555555555555555555555555555", descriptor);
        string Clip(string name, int value, bool noCurves = false) =>
            "--- !u!74 &7400000\nAnimationClip:\n  m_Name: " + name + "\n  m_AnimationClipSettings:\n    m_StopTime: 1\n" +
            (noCurves ? "  m_FloatCurves: []\n" : "  m_FloatCurves:\n  - attribute: blendShape.Smile\n    path: Face\n    classID: 137\n    curve:\n      m_Curve:\n      - time: 0\n        value: " + value + "\n");
        Asset("Empty.anim", empty, Clip("Empty", 0, true));
        Asset("Neutral.anim", neutral, Clip("Neutral", 0));
        Asset("Smile.anim", smile, Clip("Smile", 100));
        string State(int id, string name, string clip, string transitions, bool behaviour = false) =>
            $"--- !u!1102 &{id}\nAnimatorState:\n  m_Name: {name}\n  m_WriteDefaultValues: 0\n  m_Speed: 1\n  m_Motion: {{fileID: 7400000, guid: {clip}}}\n  m_Transitions:\n{transitions}" +
            (behaviour ? "  m_StateMachineBehaviours:\n  - {fileID: 400}\n" : "");
        string Transition(int id, int dst, string parameter, int mode, int threshold, bool exit = false) =>
            $"--- !u!1101 &{id}\nAnimatorStateTransition:\n  m_DstState: {{fileID: {dst}}}\n  m_IsExit: {(exit ? 1 : 0)}\n  m_Conditions:\n  - m_ConditionEvent: {parameter}\n    m_ConditionMode: {mode}\n    m_EventTreshold: {threshold}\n";
        string yaml = """
            --- !u!91 &91
            AnimatorController:
              m_AnimatorLayers:
              - m_Name: Router
                m_StateMachine: {fileID: 100}
                m_SyncedLayerIndex: -1
            --- !u!1107 &100
            AnimatorStateMachine:
              m_DefaultState: {fileID: 200}
              m_ChildStates:
              - m_State: {fileID: 200}
              - m_State: {fileID: 201}
              - m_State: {fileID: 202}
              m_AnyStateTransitions:
              - {fileID: 305}
            """ + "\n" +
            State(200, "Dispatch", empty, "  - {fileID: 300}\n  - {fileID: 301}\n  - {fileID: 302}\n", true) +
            State(201, "Smile", smile, "  - {fileID: 303}\n", true) +
            State(202, "Neutral", neutral, "  - {fileID: 304}\n") +
            Transition(300, 201, "GestureRight", 6, 1) +
            Transition(301, 202, "GestureLeft", 6, 1) +
            Transition(302, 202, "GestureLeft", 3, -1) +
            Transition(303, 0, "GestureLeft", 7, 1, true) +
            Transition(304, 0, "GestureLeft", 6, 1, true) +
            Transition(305, 202, "Contact", 1, 0) + """
            --- !u!114 &400
            MonoBehaviour:
              m_Script: {fileID: -706344726, guid: 67cc4cb7839cd3741b63733d5adf0442}
              parameters:
              - name: Ear
                type: 0
                value: 1
            """ + "\n";
        ExpressionModel Parse(string text)
        {
            Asset("Face.controller", controller, text);
            using var package = UnityPackage.Open(Path.Combine(root, "Assets", "Avatar.prefab"));
            return VrchatExpressionParser.Parse(package, UnityScene.Parse(descriptor).Doc(1).Root);
        }
        var model = Parse(yaml);
        Check(model.Layers.Count == 1 && model.Diagnostics.Any(d => d.Contains("gesture router projected")), "router projection diagnosed");
        var table = new GesturePairCompiler(model, model.Clips, _ => 0);
        for (int l = 0; l < 8; l++)
            for (int r = 0; r < 8; r++)
            {
                var pose = model.Clips.Concat(table.Generated).Single(c => c.Id == table.Pairs[l * 8 + r]);
                Check(pose.Curves.Single().Sample(0) == (r == 1 ? 1 : 0), "all 64 routed poses");
            }
        void Reject(string text, string name) => Check(Parse(text).Layers.Count == 0, name);
        Reject(yaml.Replace("name: Ear", "name: GestureLeft"), "selection-changing driver rejected");
        Reject(yaml.Replace("fileID: -706344726", "fileID: 123"), "unknown behaviour rejected");
        Reject(yaml.Replace("m_ConditionEvent: Contact", "m_ConditionEvent: GestureRight"), "gesture Any State override rejected");
        Reject(yaml.Replace("m_EventTreshold: -1", "m_EventTreshold: 5"), "incomplete gesture coverage rejected");
        Reject(yaml.Replace("m_IsExit: 1", "m_IsExit: 0"), "non-exit destinations rejected");
        Reject(yaml.Replace("m_IsExit: 1", "m_IsExit: 1\n  m_HasExitTime: 1"), "timed exits rejected");
        Reject(yaml.Replace("m_SyncedLayerIndex: -1", "m_SyncedLayerIndex: -1\n    m_Mask: {fileID: 123}"), "mask remains unsupported");
        Asset("Neutral.anim", neutral, Clip("Neutral", 0).Replace("blendShape.Smile", "blendShape.Other"));
        Reject(yaml, "partial pose bindings rejected");
        Console.WriteLine("PASS: empty-dispatch gesture projection, 64 pairs, unsafe router negative controls");
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
