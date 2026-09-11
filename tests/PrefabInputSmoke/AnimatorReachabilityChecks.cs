using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class AnimatorReachabilityChecks
{
    public static void Run(Func<string, string, string, string> asset, string selected)
    {
        const string guid = "abcd1200000000000000000000000001";
        foreach (string mode in new[] { "disabled toggle", "enabled toggle", "unreachable", "ungated",
            "shadowed entry", "muted fallback", "later fallback", "alternate route", "shadowed any", "shadowed state", "range shadow" })
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
            if (mode is not ("disabled toggle" or "enabled toggle"))
                controller = controller.Replace("  m_Conditions:\n  - m_ConditionEvent: LipSyncEnabled\n    m_ConditionMode: 1",
                    "  m_Conditions: []");
            if (mode == "unreachable")
                controller = controller.Replace("  m_Transitions:\n  - {fileID: 201}", "  m_Transitions: []");
            if (mode is "shadowed entry" or "muted fallback" or "later fallback" or "alternate route" or "shadowed any" or "shadowed state" or "range shadow")
            {
                controller = controller.Replace("  m_EntryTransitions:\n  - {fileID: 301}",
                    mode == "later fallback" ? "  m_EntryTransitions:\n  - {fileID: 301}\n  - {fileID: 302}" :
                    "  m_EntryTransitions:\n  - {fileID: 302}\n  - {fileID: 301}");
                controller += "\n--- !u!1109 &302\nAnimatorTransition:\n  m_Conditions: []\n  m_DstState: {fileID: 500}\n"
                    + (mode == "muted fallback" ? "  m_Mute: 1\n" : "")
                    + "--- !u!1102 &500\nAnimatorState:\n  m_Motion: {fileID: 0}\n";
                if (mode == "alternate route")
                    controller += "  m_Transitions:\n  - {fileID: 301}\n";
                if (mode == "shadowed any")
                    controller = controller.Replace("  m_EntryTransitions:", "  m_AnyStateTransitions:");
                if (mode == "shadowed state")
                    controller = controller.Replace("  m_EntryTransitions:",
                        "  m_DefaultState: {fileID: 600}\n--- !u!1102 &600\nAnimatorState:\n  m_Transitions:");
                if (mode == "range shadow")
                    controller = controller.Replace("AnimatorTransition:\n  m_Conditions: []\n  m_DstState: {fileID: 500}",
                        "AnimatorTransition:\n  m_Conditions:\n  - m_ConditionEvent: Viseme\n    m_ConditionMode: 3\n    m_EventTreshold: 5\n  m_DstState: {fileID: 500}");
            }
            asset("Assets/GatedFace.controller", guid, controller);
            using var package = UnityPackage.Open(selected);
            var avatar = new VrchatAvatar();
            VrchatAnimatorFaceParser.Apply(package, UnityYaml.ParseFlatDocument(
                $"lipSync: 4\nbaseAnimationLayers:\n- type: 5\n  animatorController: {{guid: {guid}}}\n"), avatar);
            int expected = mode is "ungated" or "muted fallback" or "later fallback" or "alternate route" ? 1 : 0;
            if (avatar.Visemes.Count != expected)
                throw new Exception($"Viseme reachability ({mode}): expected {expected}, got {avatar.Visemes.Count}");
            Console.WriteLine($"PASS: Viseme reachability ({mode})");
        }
    }
}
