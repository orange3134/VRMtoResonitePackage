using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class BlinkLoopChecks
{
    public static void Run(Func<string, string, string, string> asset, string selected)
    {
        const string controllerGuid = "abcf1300000000000000000000000001";
        const string clipGuid = "abcf1300000000000000000000000002";
        const string resetGuid = "abcf1300000000000000000000000003";
        string Curve(string name, params int[] values) =>
            "  - curve:\n      m_Curve:\n" + string.Concat(values.Select((v, i) =>
                $"      - time: {i}\n        value: {v}\n")) +
            $"    attribute: blendShape.{name}\n    path: Face/Body\n    classID: 137\n";
        string Clip(string curves, bool loop = true) =>
            $"m_AnimationClipSettings:\n  m_LoopTime: {(loop ? 1 : 0)}\nm_FloatCurves:\n" + curves;
        string pulses = Curve("まばたき左", 0, 100, 0) + Curve("まばたき右", 0, 100, 0) + Curve("まばたき", 0);
        VrchatBlinkLoop Read(string curves, bool loop = true) => VrchatBlinkLoop.Read(UnityYaml.ParseFlatDocument(Clip(curves, loop)));
        var pair = Read(pulses);
        Check(pair?.Bindings.Count == 2 && pair.InitialValues.Count == 3 &&
            pair.Bindings[0].Preset == "blinkLeft" && pair.Bindings[1].Preset == "blinkRight",
            "Japanese left/right pulses retain the combined reset");
        Check(Read(Curve("eye_blink_1", 0, 100, 0))?.Bindings.Single().Preset == "blink", "numbered blink name");
        Check(Read(Curve("blink_L", 0, 100, 0) + Curve("lash_blink_L", 0, 100, 0) + Curve("blink_R", 0, 100, 0))
            ?.Bindings.Count(b => b.Preset == "blinkLeft") == 2, "multiple shapes per eye");
        foreach (var curves in new[] { Curve("Smile", 0, 100, 0), Curve("blink", 100), Curve("blink", 0),
            Curve("blink", 25, 100, 25), Curve("blink", 0, 25, 0), Curve("blink", 0, 100),
            Curve("blink", 0, 101, 0), pulses + Curve("まばたき", 0) })
            Check(Read(curves) == null, "unrelated/closed/partial/non-returning/duplicate curves rejected");
        Check(Read(pulses, false) == null, "non-loop rejected");
        Check(Read(pulses + "  - attribute: m_IsActive\n    path: Face\n    classID: 1\n") == null,
            "ancestor visibility rejected");
        Check(Read(pulses + "  - attribute: m_IsActive\n    path: Other\n    classID: 1\n") != null,
            "unrelated visibility retained");

        asset("Assets/StartupBlink.anim", clipGuid, "--- !u!74 &7400000\nAnimationClip:\n" + Indent(Clip(pulses)));
        asset("Assets/StartupReset.anim", resetGuid, "--- !u!74 &7400000\nAnimationClip:\n" + Indent(Clip(Curve("まばたき左", 0), false)));
        string Machine(int id, string guid) => $"--- !u!1107 &{id}\nAnimatorStateMachine:\n  m_DefaultState: {{fileID: {id + 1}}}\n" +
            $"--- !u!1102 &{id + 1}\nAnimatorState:\n  m_Motion: {{fileID: 7400000, guid: {guid}}}\n";
        VrchatAvatar Parse(string layers, bool descriptor = false)
        {
            asset("Assets/StartupBlink.controller", controllerGuid,
                "--- !u!91 &91\nAnimatorController:\n  m_AnimatorLayers:\n" + layers + Machine(100, clipGuid) + Machine(200, resetGuid));
            using var package = UnityPackage.Open(selected);
            var avatar = new VrchatAvatar();
            if (descriptor) avatar.Blink = new VrchatBlink { BlendShapeName = "explicit", MeshGameObjectName = "Body", BlendShapeIndex = 9 };
            VrchatAnimatorFaceParser.Apply(package, UnityYaml.ParseFlatDocument(
                $"baseAnimationLayers:\n- type: 5\n  animatorController: {{guid: {controllerGuid}}}\n"), avatar);
            return avatar;
        }
        var detected = Parse("  - m_StateMachine: {fileID: 200}\n  - m_StateMachine: {fileID: 100}\n    m_DefaultWeight: 1\n");
        Check(detected.Blink?.Preset == "blinkLeft" && detected.AdditionalBlinks.Single().Preset == "blinkRight" &&
            detected.BlinkInitialValues.Count == 3, "active loop overrides earlier baseline layer");
        var model = VrchatModelAdapter.ToVrmModel(detected);
        Check(model.Expressions.Count == 2 && model.Expressions.All(e => e.Binds.Count == 1 && e.Binds[0].Weight == 1),
            "both eyelid bindings reach the common avatar adapter");
        Check(Parse("  - m_StateMachine: {fileID: 100}\n  - m_StateMachine: {fileID: 200}\n    m_DefaultWeight: 1\n").Blink == null,
            "later active override blocks inference");
        Check(Parse("  - m_StateMachine: {fileID: 200}\n  - m_StateMachine: {fileID: 100}\n    m_DefaultWeight: 0.5\n").Blink == null,
            "partial layer blocked");
        Check(Parse("  - m_StateMachine: {fileID: 200}\n  - m_StateMachine: {fileID: 100}\n    m_DefaultWeight: 1\n    m_BlendingMode: 1\n").Blink == null,
            "additive layer blocked");
        var explicitAvatar = Parse("  - m_StateMachine: {fileID: 100}\n", true);
        Check(explicitAvatar.Blink.BlendShapeName == "explicit" && explicitAvatar.AdditionalBlinks.Count == 0 &&
            explicitAvatar.BlinkInitialValues.Count == 0, "descriptor stays authoritative");
        Console.WriteLine("PASS: startup blink loop detection, reset curves, layer order, sides and adapter");
    }

    private static string Indent(string text) => string.Join("\n", text.Split('\n').Select(l => "  " + l));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
