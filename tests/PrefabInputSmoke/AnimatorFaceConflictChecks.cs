using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class AnimatorFaceConflictChecks
{
    public static void Run(Func<string, string, string, string> asset, string selected)
    {
        const string controllerGuid = "abcd1300000000000000000000000001";
        const string clipGuid = "abcd1300000000000000000000000002";
        const string resetGuid = "abcd1300000000000000000000000003";
        var failures = new List<string>();
        foreach (string mode in new[] { "blink override", "blink partial override", "blink unrelated", "blink zero weight", "neutral viseme", "single viseme", "blink additive", "viseme additive", "disconnected gate", "connected gate",
            "hidden renderer", "enabled renderer", "inactive object", "inactive ancestor", "inactive root", "inactive sibling", "inactive child", "enabled ancestor renderer", "blink hidden renderer",
            "blink disconnected conflict", "blink reachable conflict", "viseme disconnected conflict", "viseme reachable conflict" })
        {
            bool blink = mode.StartsWith("blink");
            string Curve(string name, int value) =>
                $"  - curve:\n      m_Curve:\n      - value: {value}\n    attribute: blendShape.{name}\n    path: Face/Body\n    classID: 137\n";
            var visibility = mode switch
            {
                "hidden renderer" or "blink hidden renderer" => ("m_Enabled", "Face/Body", 137, 0),
                "enabled renderer" => ("m_Enabled", "Face/Body", 137, 1),
                "inactive object" => ("m_IsActive", "Face/Body", 1, 0),
                "inactive ancestor" => ("m_IsActive", "Face", 1, 0),
                "inactive root" => ("m_IsActive", "", 1, 0),
                "inactive sibling" => ("m_IsActive", "Face/Bod", 1, 0),
                "inactive child" => ("m_IsActive", "Face/Body/Child", 1, 0),
                "enabled ancestor renderer" => ("m_Enabled", "Face", 137, 0),
                _ => (null, "", 0, 0),
            };
            string visibilityCurve = visibility.Item1 == null ? "" :
                $"  - curve:\n      m_Curve:\n      - value: {visibility.Item4}\n    attribute: {visibility.Item1}\n    path: {visibility.Item2}\n    classID: {visibility.Item3}\n";
            asset("Assets/ConflictCheck.anim", clipGuid,
                "--- !u!74 &7400000\nAnimationClip:\n  m_AnimationClipSettings:\n    m_LoopTime: 1\n  m_FloatCurves:\n" +
                (blink ? Curve("blink", 100).Replace("      - value: 100", "      - value: 0\n      - value: 100\n      - value: 0") : Curve("mouth", 100)) +
                (mode == "neutral viseme" ? Curve("Smile", 0) : "") + visibilityCurve);
            asset("Assets/ConflictReset.anim", resetGuid,
                "--- !u!74 &7400000\nAnimationClip:\n  m_FloatCurves:\n" + Curve(mode is "blink unrelated" or "blink hidden renderer" ? "Smile" : mode.StartsWith("viseme") ? "mouth" : "blink", 0));
            string controller = "--- !u!91 &91\nAnimatorController:\n  m_AnimatorLayers:\n  - m_StateMachine: {fileID: 100}\n" +
                (blink || mode.EndsWith("conflict") ? "  - m_StateMachine: {fileID: 300}\n    m_DefaultWeight: " + (mode == "blink zero weight" ? "0" : mode == "blink partial override" ? "0.5" : "1") + "\n" : "") +
                "--- !u!1107 &100\nAnimatorStateMachine:\n  m_DefaultState: {fileID: 200}\n  m_EntryTransitions:\n  - {fileID: 101}\n" +
                "--- !u!1109 &101\nAnimatorTransition:\n  m_DstState: {fileID: 200}\n  m_Conditions:\n  - m_ConditionEvent: Viseme\n    m_ConditionMode: 6\n    m_EventTreshold: 10\n" +
                "--- !u!1102 &200\nAnimatorState:\n  m_Motion: {fileID: 7400000, guid: " + clipGuid + "}\n" +
                "--- !u!1107 &300\nAnimatorStateMachine:\n  m_DefaultState: {fileID: 400}\n--- !u!1102 &400\nAnimatorState:\n  m_Motion: {fileID: 7400000, guid: " + resetGuid + "}\n";
            if (mode.EndsWith("additive"))
                controller = controller.Replace("  - m_StateMachine: {fileID: 100}\n",
                    "  - m_StateMachine: {fileID: 999}\n  - m_StateMachine: {fileID: 100}\n    m_DefaultWeight: 1\n    m_BlendingMode: 1\n")
                    .Replace("    m_DefaultWeight: 1\n--- !u!1107 &100", "    m_DefaultWeight: 0\n--- !u!1107 &100");
            if (mode.EndsWith("conflict"))
                controller = controller.Replace("  m_DefaultState: {fileID: 400}\n", "  m_DefaultState: {fileID: 600}\n  m_ChildStates:\n  - m_State: {fileID: 400}\n  - m_State: {fileID: 600}\n") +
                    "--- !u!1102 &600\nAnimatorState:\n  m_WriteDefaultValues: 0\n" +
                    (mode.Contains("reachable") ? "  m_Transitions:\n  - {fileID: 601}\n--- !u!1101 &601\nAnimatorStateTransition:\n  m_DstState: {fileID: 400}\n  m_Conditions: []\n" : "");
            if (mode.EndsWith("gate"))
            {
                controller = controller.Replace("--- !u!1107 &100\nAnimatorStateMachine:\n",
                    "--- !u!1107 &100\nAnimatorStateMachine:\n  m_ChildStates:\n  - m_State: {fileID: 500}\n") +
                    "--- !u!1102 &500\nAnimatorState:\n  m_Transitions:\n  - {fileID: 501}\n" +
                    "--- !u!1101 &501\nAnimatorStateTransition:\n  m_DstState: {fileID: 200}\n  m_Conditions:\n  - m_ConditionEvent: Toggle\n    m_ConditionMode: 1\n";
                if (mode == "connected gate")
                    controller = controller.Replace("--- !u!1102 &200\nAnimatorState:\n",
                        "--- !u!1102 &200\nAnimatorState:\n  m_Transitions:\n  - {fileID: 502}\n") +
                        "--- !u!1101 &502\nAnimatorStateTransition:\n  m_DstState: {fileID: 500}\n  m_Conditions:\n  - m_ConditionEvent: Toggle\n    m_ConditionMode: 1\n";
            }
            asset("Assets/ConflictCheck.controller", controllerGuid, controller);
            using var package = UnityPackage.Open(selected);
            var avatar = new VrchatAvatar();
            var renderer = new VrchatRendererMaterials { RendererGameObjectName = "Body" };
            renderer.InitialBlendShapes.Add((1, 100));
            avatar.RendererMaterials.Add(renderer);
            avatar.FbxBlendShapeNames["Body"] = new[] { "mouth", "Smile" };
            VrchatAnimatorFaceParser.Apply(package, UnityYaml.ParseFlatDocument(
                $"lipSync: 4\nbaseAnimationLayers:\n- type: 5\n  animatorController: {{guid: {controllerGuid}}}\n"), avatar);
            bool inferred = blink ? avatar.Blink != null : avatar.Visemes.Count != 0;
            bool expected = mode is "blink unrelated" or "blink zero weight" or "single viseme" or "disconnected gate"
                or "inactive sibling" or "inactive child" or "enabled ancestor renderer"
                or "blink disconnected conflict" or "viseme disconnected conflict";
            if (inferred != expected) failures.Add($"{mode}: expected inference={expected}, got {inferred}");
            else Console.WriteLine($"PASS: Animator face conflict ({mode})");
        }
        if (failures.Count > 0) throw new Exception(string.Join("\n", failures));
    }
}
