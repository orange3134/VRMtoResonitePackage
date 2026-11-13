using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class ExpressionDefaultChecks
{
    public static void Run(Func<string, string, string, string> asset, string selected)
    {
        string Curve(string name, params int[] values) => "  - curve:\n      m_Curve:\n" +
            string.Concat(values.Select((v, i) => $"      - time: {i}\n        value: {v}\n")) +
            $"    attribute: blendShape.{name}\n    path: Face/Body\n    classID: 137\n";
        var state = UnityYaml.ParseFlatDocument("m_Name: Neutral\n");
        Dictionary<ExpressionBinding, float> Read(string curves) =>
            VrchatExpressionDefaults.Read(state, UnityYaml.ParseFlatDocument("m_FloatCurves:\n" + curves));
        var mouth = new ExpressionBinding("Face/Body", "Mouth.Open");
        var defaults = Read(Curve("Mouth.Open", 0, 0) + Curve("Smile", 37) + Curve("Moving", 0, 100));
        Check(defaults.Count == 2 && defaults[mouth] == 0 && defaults[new("Face/Body", "Smile")] == .37f,
            "explicit zero and nonzero normalized defaults retained, animated shapes excluded");
        Check(!Read(Curve("Mouth.Open", 0, 0).Replace("value: 0", "value: 0\n        outSlope: 1")).ContainsKey(mouth),
            "equal endpoints with nonzero tangents are not constant");
        Check(!Read(Curve("Mouth.Open", 0) + Curve("Mouth.Open", 0)).ContainsKey(mouth), "duplicate binding rejected");
        Check(Read(Curve("Mouth.Open", 0).Replace("value: 0", "value: NaN")).Count == 0, "nonfinite value rejected");
        Check(Read(Curve("Mouth.Open")).Count == 0, "empty curve rejected");
        var clip = UnityYaml.ParseFlatDocument("m_FloatCurves:\n" + Curve("Mouth.Open", 0));
        Check(VrchatExpressionDefaults.Read(UnityYaml.ParseFlatDocument("m_TimeParameterActive: 1\n"), clip).Count == 0,
            "parameter-timed state excluded");
        Check(VrchatExpressionDefaults.Read(null, clip).Count == 0 && VrchatExpressionDefaults.Read(state, null).Count == 0,
            "unresolved startup or motion excluded");

        const string controllerGuid = "acdf1400000000000000000000000001", clipGuid = "acdf1400000000000000000000000002";
        asset("Assets/BaseExpression.anim", clipGuid, "--- !u!74 &7400000\nAnimationClip:\n" +
            string.Join("\n", ("m_FloatCurves:\n" + Curve("Mouth.Open", 0)).Split('\n').Select(l => "  " + l)));
        VrchatAvatar Parse(bool first, string extra = "", string stateExtra = "", string layerType = "5")
        {
            string layer = "  - m_StateMachine: {fileID: 100}\n    m_DefaultWeight: 0\n" + extra;
            string empty = "  - m_StateMachine: {fileID: 200}\n    m_DefaultWeight: 1\n";
            asset("Assets/BaseExpression.controller", controllerGuid,
                "--- !u!91 &91\nAnimatorController:\n  m_AnimatorLayers:\n" + (first ? layer + empty : empty + layer) +
                "--- !u!1107 &100\nAnimatorStateMachine:\n  m_DefaultState: {fileID: 101}\n" +
                "--- !u!1102 &101\nAnimatorState:\n" + stateExtra + $"  m_Motion: {{fileID: 7400000, guid: {clipGuid}}}\n" +
                "--- !u!1107 &200\nAnimatorStateMachine:\n  m_DefaultState: {fileID: 201}\n" +
                "--- !u!1102 &201\nAnimatorState:\n  m_Motion: {fileID: 0}\n" +
                "--- !u!1101 &400\nAnimatorStateTransition:\n  m_DstState: {fileID: 201}\n" +
                "  m_Conditions:\n  - m_ConditionEvent: CustomParameter\n    m_ConditionMode: 1\n");
            using var package = UnityPackage.Open(selected);
            var avatar = new VrchatAvatar();
            VrchatAnimatorFaceParser.Apply(package, UnityYaml.ParseFlatDocument(
                $"baseAnimationLayers:\n- type: {layerType}\n  animatorController: {{guid: {controllerGuid}}}\n"), avatar);
            return avatar;
        }
        Check(Parse(true).BaseExpressionInitialValues[mouth] == 0, "FX first layer has full weight even when serialized as zero");
        Check(Parse(false).BaseExpressionInitialValues.Count == 0, "later layer does not define neutral stream");
        Check(Parse(true, "    m_AvatarMask: {fileID: 31900000, guid: missing}\n").BaseExpressionInitialValues.Count == 0,
            "masked base layer excluded");
        Check(Parse(true, "    m_SyncedLayerIndex: 0\n").BaseExpressionInitialValues.Count == 0, "synced base excluded");
        Check(Parse(true, stateExtra: "  m_TimeParameterActive: 1\n").BaseExpressionInitialValues.Count == 0,
            "parameter-timed base excluded through parser");
        Check(Parse(true, stateExtra: "  m_Transitions:\n  - {fileID: 400}\n").BaseExpressionInitialValues.Count == 0,
            "parameter-gated baseline excluded instead of assuming parameter defaults");
        Check(Parse(true, layerType: "4").BaseExpressionInitialValues.Count == 0, "Action layer excluded");
        Console.WriteLine("PASS: constant FX base expression defaults, first-layer weight, motion and mask guards");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Expression defaults: " + message);
    }
}
