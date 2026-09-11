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
        foreach (string mode in new[] { "blink override", "blink partial override", "blink unrelated", "blink zero weight", "neutral viseme", "single viseme" })
        {
            bool blink = mode.StartsWith("blink");
            string Curve(string name, int value) =>
                $"  - curve:\n      m_Curve:\n      - value: {value}\n    attribute: blendShape.{name}\n    path: Face/Body\n    classID: 137\n";
            asset("Assets/ConflictCheck.anim", clipGuid,
                "--- !u!74 &7400000\nAnimationClip:\n  m_AnimationClipSettings:\n    m_LoopTime: 1\n  m_FloatCurves:\n" +
                Curve(blink ? "blink" : "mouth", 100) + (mode == "neutral viseme" ? Curve("Smile", 0) : ""));
            asset("Assets/ConflictReset.anim", resetGuid,
                "--- !u!74 &7400000\nAnimationClip:\n  m_FloatCurves:\n" + Curve(mode == "blink unrelated" ? "Smile" : "blink", 0));
            string controller = "--- !u!91 &91\nAnimatorController:\n  m_AnimatorLayers:\n  - m_StateMachine: {fileID: 100}\n" +
                (blink ? "  - m_StateMachine: {fileID: 300}\n    m_DefaultWeight: " + (mode == "blink zero weight" ? "0" : mode == "blink partial override" ? "0.5" : "1") + "\n" : "") +
                "--- !u!1107 &100\nAnimatorStateMachine:\n  m_DefaultState: {fileID: 200}\n  m_EntryTransitions:\n  - {fileID: 101}\n" +
                "--- !u!1109 &101\nAnimatorTransition:\n  m_DstState: {fileID: 200}\n  m_Conditions:\n  - m_ConditionEvent: Viseme\n    m_ConditionMode: 6\n    m_EventTreshold: 10\n" +
                "--- !u!1102 &200\nAnimatorState:\n  m_Motion: {fileID: 7400000, guid: " + clipGuid + "}\n" +
                "--- !u!1107 &300\nAnimatorStateMachine:\n  m_DefaultState: {fileID: 400}\n--- !u!1102 &400\nAnimatorState:\n  m_Motion: {fileID: 7400000, guid: " + resetGuid + "}\n";
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
            bool expected = mode is "blink unrelated" or "blink zero weight" or "single viseme";
            if (inferred != expected) failures.Add($"{mode}: expected inference={expected}, got {inferred}");
            else Console.WriteLine($"PASS: Animator face conflict ({mode})");
        }
        if (failures.Count > 0) throw new Exception(string.Join("\n", failures));
    }
}
