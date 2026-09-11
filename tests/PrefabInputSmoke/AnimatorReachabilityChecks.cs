using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class AnimatorReachabilityChecks
{
    public static void Run(Func<string, string, string, string> asset, string selected)
    {
        const string guid = "abcd1200000000000000000000000001";
        foreach (string mode in new[] { "disabled toggle", "enabled toggle", "unreachable", "ungated" })
        {
            string controller = $$"""
--- !u!91 &91
AnimatorController:
  m_AnimatorParameters:
  - m_Name: LipSyncEnabled
    m_Type: 4
    m_DefaultBool: {{(mode == "enabled toggle" ? 1 : 0)}}
  m_AnimatorLayers:
  - m_StateMachine: {fileID: 100}
--- !u!1107 &100
AnimatorStateMachine:
  m_DefaultState: {fileID: 200}
  m_ChildStates:
  - m_State: {fileID: 200}
  m_ChildStateMachines:
  - m_StateMachine: {fileID: 300}
--- !u!1102 &200
AnimatorState:
  m_Transitions:
  - {fileID: 201}
--- !u!1101 &201
AnimatorStateTransition:
  m_DstStateMachine: {fileID: 300}
  m_Conditions:
  - m_ConditionEvent: LipSyncEnabled
    m_ConditionMode: 1
--- !u!1107 &300
AnimatorStateMachine:
  m_EntryTransitions:
  - {fileID: 301}
--- !u!1109 &301
AnimatorTransition:
  m_DstState: {fileID: 400}
  m_Conditions:
  - m_ConditionEvent: Viseme
    m_ConditionMode: 6
    m_EventTreshold: 10
--- !u!1102 &400
AnimatorState:
  m_Motion: {fileID: 7400000, guid: 0000000000000000000000000000000a}
""";
            if (mode is "unreachable" or "ungated")
                controller = controller.Replace("  m_Conditions:\n  - m_ConditionEvent: LipSyncEnabled\n    m_ConditionMode: 1",
                    "  m_Conditions: []");
            if (mode == "unreachable")
                controller = controller.Replace("  m_Transitions:\n  - {fileID: 201}", "  m_Transitions: []");
            asset("Assets/GatedFace.controller", guid, controller);
            using var package = UnityPackage.Open(selected);
            var avatar = new VrchatAvatar();
            VrchatAnimatorFaceParser.Apply(package, UnityYaml.ParseFlatDocument(
                $"lipSync: 4\nbaseAnimationLayers:\n- type: 5\n  animatorController: {{guid: {guid}}}\n"), avatar);
            int expected = mode == "ungated" ? 1 : 0;
            if (avatar.Visemes.Count != expected)
                throw new Exception($"Viseme reachability ({mode}): expected {expected}, got {avatar.Visemes.Count}");
            Console.WriteLine($"PASS: Viseme reachability ({mode})");
        }
    }
}
