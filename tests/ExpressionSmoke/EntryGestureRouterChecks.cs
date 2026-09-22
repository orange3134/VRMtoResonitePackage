using System.Text;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class EntryGestureRouterChecks
{
    public static void Run(string artifacts)
    {
        string root = Path.Combine(artifacts, "EntryRouterFixture");
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        const string controller = "11111111111111111111111111111111", mask = "22222222222222222222222222222222";
        string GuidFor(int i) => i.ToString("x32");
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
        Asset("Avatar.prefab", GuidFor(999), descriptor);
        string maskYaml = "--- !u!319 &31900000\nAvatarMask:\n  m_Mask: 00000000\n  m_Elements: []\n";
        Asset("Hands.mask", mask, maskYaml);
        var yaml = new StringBuilder("--- !u!91 &91\nAnimatorController:\n  m_AnimatorLayers:\n");
        for (int hand = 0; hand < 2; hand++)
            yaml.Append($"  - m_Name: Hand{hand}\n    m_DefaultWeight: 1\n    m_StateMachine: {{fileID: {100 + hand}}}\n    m_SyncedLayerIndex: -1\n    m_Mask: {{fileID: 31900000, guid: {mask}}}\n");
        for (int hand = 0; hand < 2; hand++)
        {
            string parameter = hand == 0 ? "GestureLeft" : "GestureRight";
            int first = 1000 + hand * 100;
            yaml.Append($"--- !u!1107 &{100 + hand}\nAnimatorStateMachine:\n  m_DefaultState: {{fileID: {first}}}\n  m_ChildStates:\n");
            for (int g = 0; g < 8; g++) yaml.Append($"  - m_State: {{fileID: {first + g}}}\n");
            yaml.Append("  m_EntryTransitions:\n");
            for (int g = 0; g < 8; g++) yaml.Append($"  - {{fileID: {first + g + 10}}}\n");
            for (int g = 0; g < 8; g++)
            {
                int id = first + g;
                string clip = GuidFor(id);
                Asset($"Pose{id}.anim", clip, $"--- !u!74 &7400000\nAnimationClip:\n  m_Name: Pose{id}\n  m_AnimationClipSettings:\n    m_StopTime: 1\n" +
                    (g < 2 ? "  m_FloatCurves: []\n" : $"  m_FloatCurves:\n  - attribute: blendShape.Smile\n    path: Face\n    classID: 137\n    curve:\n      m_Curve:\n      - time: 0\n        value: {hand * 40 + g * 5}\n"));
                yaml.Append($"--- !u!1102 &{id}\nAnimatorState:\n  m_Name: Pose{id}\n  m_WriteDefaultValues: 0\n  m_Speed: 1\n  m_TimeParameterActive: 1\n  m_TimeParameter: {parameter}Weight\n  m_Motion: {{fileID: 7400000, guid: {clip}}}\n  m_StateMachineBehaviours:\n  - {{fileID: 400}}\n  m_Transitions:\n  - {{fileID: {id + 20}}}\n");
                yaml.Append($"--- !u!1109 &{id + 10}\nAnimatorTransition:\n  m_DstState: {{fileID: {id}}}\n  m_Conditions:\n  - m_ConditionEvent: {parameter}\n    m_ConditionMode: 6\n    m_EventTreshold: {g}\n");
                yaml.Append($"--- !u!1101 &{id + 20}\nAnimatorStateTransition:\n  m_IsExit: 1\n  m_DstState: {{fileID: 0}}\n  m_Conditions:\n  - m_ConditionEvent: {parameter}\n    m_ConditionMode: 7\n    m_EventTreshold: {g}\n");
            }
        }
        yaml.Append("""
            --- !u!114 &400
            MonoBehaviour:
              m_Script: {fileID: -646210727, guid: 67cc4cb7839cd3741b63733d5adf0442}
              trackingHead: 0
              trackingEyes: 1
              trackingMouth: 2
            """);
        string source = yaml.ToString();
        ExpressionModel Parse(string text)
        {
            Asset("Face.controller", controller, text);
            using var package = UnityPackage.Open(Path.Combine(root, "Assets", "Avatar.prefab"));
            return VrchatExpressionParser.Parse(package, UnityScene.Parse(descriptor).Doc(1).Root);
        }
        var model = Parse(source);
        Check(model.Layers.Count == 2 && model.Layers.All(l => l.EmptyStatesUseBaseStream), "both entry selectors imported explicitly");
        var table = new GesturePairCompiler(model, model.Clips, _ => 0.23f);
        for (int l = 0; l < 8; l++)
            for (int r = 0; r < 8; r++)
            {
                var pose = model.Clips.Concat(table.Generated).Single(c => c.Id == table.Pairs[l * 8 + r]);
                float expected = r >= 2 ? (40 + r * 5) / 100f : l >= 2 ? l * 5 / 100f : 0.23f;
                Check(Math.Abs(pose.Curves.Single().Sample(0) - expected) < 0.0001f, "right layer priority and empty pass-through for all 64 pairs");
            }
        void Reject(string text, string name) => Check(Parse(text).Layers.Count == 0, name);
        Reject(source.Replace("m_ConditionMode: 7", "m_ConditionMode: 6"), "Entry/Exit cycles rejected");
        Reject(source.Replace("m_EventTreshold: 0", "m_EventTreshold: 1"), "latched or uncovered selections rejected");
        Reject(source.Replace("m_IsExit: 1", "m_IsExit: 1\n  m_HasExitTime: 1"), "timed exits rejected");
        Reject(source.Replace("m_IsExit: 1", "m_IsExit: 0"), "unresolved non-exit routes rejected");
        Reject(source.Replace("trackingHead: 0", "trackingHead: 2"), "body tracking override rejected");
        Reject(source.Replace("trackingEyes: 1", "trackingEyes: 8"), "invalid tracking enum rejected");
        Reject(source.Replace("fileID: -646210727", "fileID: -706344726"), "parameter driver rejected");
        Reject(source.Replace("m_WriteDefaultValues: 0", "m_WriteDefaultValues: 0\n  m_SpeedParameterActive: 1"), "unsupported playback rejected");
        Reject(source.Replace("m_SyncedLayerIndex: -1", "m_SyncedLayerIndex: -1\n    m_BlendingMode: 1"), "additive layer rejected");
        Asset("Hands.mask", mask, maskYaml.Replace("m_Elements: []", "m_Elements:\n  - m_Path: Face\n    m_Weight: 0"));
        Reject(source, "transform mask rejected");
        Asset("Hands.mask", mask, maskYaml.Replace("!u!319", "!u!114"));
        Reject(source, "wrong mask asset type rejected");
        Asset("Hands.mask", mask, maskYaml);
        Reject(source.Replace(mask, GuidFor(888)), "missing mask rejected");
        foreach (int id in new[] { 1002, 1102 })
        {
            string path = Path.Combine(root, "Assets", $"Pose{id}.anim");
            File.WriteAllText(path, File.ReadAllText(path).Replace("blendShape.Smile", "blendShape.Other"));
        }
        Reject(source, "partial nonempty poses remain unsupported");
        Console.WriteLine("PASS: Entry/Exit selectors, humanoid-only masks, 64 layered poses and unsafe-selector rejection");
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
