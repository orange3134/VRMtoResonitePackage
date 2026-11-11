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
        VrchatBlinkLoop Neutral(string curves, bool loop = true) => VrchatBlinkLoop.ReadNeutral(UnityYaml.ParseFlatDocument(Clip(curves, loop)));
        var neutralFace = Neutral(Curve("まばたき", 0) + Curve("Smile", 37) + Curve("ウィンク", 0) +
            "  - attribute: m_IsActive\n    path: Tears\n    classID: 1\n");
        Check(neutralFace?.Bindings.Single().BlendShapeName == "まばたき" && neutralFace.InitialValues.Count == 1,
            "neutral loop selects only the explicit binocular blink, not unrelated face defaults");
        foreach (string curves in new[] { Curve("Smile", 0), Curve("blink_L", 0), Curve("eye_blink_1", 0),
            Curve("blink", 100), Curve("blink", 20), Curve("blink", 0, 100, 0),
            Curve("blink", 0) + Curve("まばたき", 0), Curve("blink", 0) + Curve("Smile", 0, 100, 0),
            Curve("blink", 0) + "  - attribute: m_IsActive\n    path: Face\n    classID: 1\n",
            Curve("blink", 0) + "  - attribute: m_Enabled\n    path: Face/Body\n    classID: 137\n" })
            Check(Neutral(curves) == null, "neutral fallback excludes ambiguous, animated, nonzero and hidden bindings");
        Check(Neutral(Curve("blink", 0), false) == null, "non-loop neutral motion is not a fallback");
        Check(Neutral(Curve("blink", 0, 0).Replace("value: 0", "value: 0\n        outSlope: 1")) == null,
            "zero endpoints with moving tangents are not neutral");

        asset("Assets/StartupBlink.anim", clipGuid, "--- !u!74 &7400000\nAnimationClip:\n" + Indent(Clip(pulses)));
        asset("Assets/StartupReset.anim", resetGuid, "--- !u!74 &7400000\nAnimationClip:\n" + Indent(Clip(Curve("まばたき左", 0), false)));
        string Machine(int id, string guid, long motionId = 7400000) => $"--- !u!1107 &{id}\nAnimatorStateMachine:\n  m_DefaultState: {{fileID: {id + 1}}}\n" +
            $"--- !u!1102 &{id + 1}\nAnimatorState:\n  m_Motion: {{fileID: {motionId}, guid: {guid}}}\n";
        VrchatAvatar Parse(string layers, bool descriptor = false, string laterGuid = resetGuid, long laterFileId = 7400000)
        {
            asset("Assets/StartupBlink.controller", controllerGuid,
                "--- !u!91 &91\nAnimatorController:\n  m_AnimatorLayers:\n" + layers + Machine(100, clipGuid) + Machine(200, laterGuid, laterFileId));
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
        asset("Assets/StartupBlink.anim", clipGuid, "--- !u!74 &7400000\nAnimationClip:\n" + Indent(Clip(Curve("まばたき", 0) + Curve("Smile", 37))));
        const string handIdle = "14980fc5fe40191418954549174fe63e";
        const string laterLayers = "  - m_StateMachine: {fileID: 100}\n  - m_StateMachine: {fileID: 200}\n    m_DefaultWeight: 1\n";
        var fallback = Parse(laterLayers, laterGuid: handIdle);
        Check(fallback.Blink?.BlendShapeName == "まばたき" && fallback.BlinkInitialValues.Count == 1,
            "missing SDK hand-only idle does not override the neutral face fallback");
        Check(Parse(laterLayers, true, handIdle).Blink.BlendShapeName == "explicit", "descriptor beats neutral fallback");
        Check(Parse(laterLayers, laterGuid: "abcd1300000000000000000000000099").Blink == null,
            "unknown missing later motion conservatively blocks fallback");
        // A present asset wins over the known SDK GUID; do not assume it is still hand-only.
        asset("Assets/ReplacedHandIdle.anim", handIdle, "--- !u!74 &7400000\nAnimationClip:\n" + Indent(Clip(Curve("まばたき", 100), false)));
        Check(Parse(laterLayers, laterGuid: handIdle).Blink == null, "packaged SDK-GUID replacement is inspected and blocks fallback");
        const string treeGuid = "abcf1300000000000000000000000004";
        foreach (string childrenKey in new[] { "m_Childs", "m_Children" })
        {
            string tree = $"--- !u!206 &20600000\nBlendTree:\n  {childrenKey}:\n  - m_Motion: {{fileID: 20600001}}\n" +
                $"--- !u!206 &20600001\nBlendTree:\n  {childrenKey}:\n  - m_Motion: {{fileID: 7400000, guid: {resetGuid}}}\n";
            asset("Assets/LaterTree.asset", treeGuid, tree);
            asset("Assets/StartupReset.anim", resetGuid, "--- !u!74 &7400000\nAnimationClip:\n" + Indent(Clip(Curve("Tail", 0), false)));
            Check(Parse(laterLayers, laterGuid: treeGuid, laterFileId: 20600000).Blink?.BlendShapeName == "まばたき",
                "unrelated nested external BlendTree does not suppress blink: " + childrenKey);
            asset("Assets/StartupReset.anim", resetGuid, "--- !u!74 &7400000\nAnimationClip:\n" + Indent(Clip(Curve("まばたき", 0), false)));
            Check(Parse(laterLayers, laterGuid: treeGuid, laterFileId: 20600000).Blink == null,
                "nested external BlendTree face override blocks blink: " + childrenKey);
            asset("Assets/LaterTree.asset", treeGuid, tree.Replace($"{{fileID: 7400000, guid: {resetGuid}}}", "{fileID: 20600000}"));
            Check(Parse(laterLayers, laterGuid: treeGuid, laterFileId: 20600000).Blink == null, "cyclic external BlendTree is not treated as empty");
        }
        Console.WriteLine("PASS: startup blink loop detection, reset curves, layer order, sides and adapter");
    }

    private static string Indent(string text) => string.Join("\n", text.Split('\n').Select(l => "  " + l));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
