using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class MixedExpressionClipChecks
{
    public static void Run(string artifacts)
    {
        string root = Path.Combine(artifacts, "MixedExpressionClipFixture");
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        string Guid(int id) => id.ToString("x32");
        void Asset(string name, int id, string yaml)
        {
            File.WriteAllText(Path.Combine(root, "Assets", name), yaml);
            File.WriteAllText(Path.Combine(root, "Assets", name + ".meta"), "guid: " + Guid(id));
        }
        string descriptor = "--- !u!114 &1\nMonoBehaviour:\n  baseAnimationLayers:\n  - type: 5\n    isDefault: 0\n    animatorController: {fileID: 91, guid: " + Guid(2) + "}\n";
        Asset("Avatar.prefab", 1, descriptor);
        Asset("Face.controller", 2, $$"""
            --- !u!91 &91
            AnimatorController:
              m_AnimatorLayers:
              - m_Name: Hands
                m_StateMachine: {fileID: 100}
                m_SyncedLayerIndex: -1
            --- !u!1107 &100
            AnimatorStateMachine:
              m_DefaultState: {fileID: 200}
              m_ChildStates:
              - m_State: {fileID: 200}
              - m_State: {fileID: 201}
              m_AnyStateTransitions:
              - {fileID: 300}
              - {fileID: 301}
            --- !u!1102 &200
            AnimatorState:
              m_Name: Neutral
              m_WriteDefaultValues: 1
              m_Motion: {fileID: 0}
            --- !u!1102 &201
            AnimatorState:
              m_Name: Smile
              m_WriteDefaultValues: 1
              m_Motion: {fileID: 7400000, guid: {{Guid(3)}}}
            --- !u!1101 &300
            AnimatorStateTransition:
              m_DstState: {fileID: 201}
              m_Conditions:
              - m_ConditionMode: 6
                m_ConditionEvent: GestureLeft
                m_EventTreshold: 1
            --- !u!1101 &301
            AnimatorStateTransition:
              m_DstState: {fileID: 200}
              m_Conditions:
              - m_ConditionMode: 6
                m_ConditionEvent: GestureLeft
                m_EventTreshold: 0
            """);
        const string header = "--- !u!74 &7400000\nAnimationClip:\n  m_Name: Mixed\n";
        const string face = "  - classID: 137\n    attribute: blendShape.Smile\n    path: Face\n    curve:\n      m_Curve:\n      - time: 0\n        value: 20\n        outSlope: 60\n      - time: 1\n        value: 80\n        inSlope: 60\n";
        string FloatTrack(int classId, string attribute, string path) =>
            $"  - classID: {classId}\n    attribute: {attribute}\n    path: {path}\n    curve:\n      m_Curve:\n      - time: 0\n        value: 1\n";
        ExpressionModel Parse(string body)
        {
            Asset("Mixed.anim", 3, header + body);
            using var package = UnityPackage.Open(Path.Combine(root, "Assets", "Avatar.prefab"));
            return VrchatExpressionParser.Parse(package, UnityScene.Parse(descriptor).Doc(1).Root);
        }
        void Accepted(ExpressionModel model, string reason, string diagnostic)
        {
            Check(model.Clips.Count == 1 && model.Layers.Count == 1, reason + ": clip and layer retained");
            var curve = model.Clips.Single().Curves.Single();
            Check(curve.Binding == new ExpressionBinding("Face", "Smile") &&
                Math.Abs(curve.Sample(0.5f) - 0.5f) < 0.0001f && curve.Keys.Count == 2,
                reason + ": exact face binding and animation retained");

        }
        void Rejected(ExpressionModel model, string reason)
        {
            Check(model.Clips.Count == 0 && model.Layers.Count == 0, reason);

        }
        foreach (string kind in new[] { "m_CompressedRotationCurves", "m_RotationCurves", "m_EulerCurves",
                     "m_PositionCurves", "m_ScaleCurves", "m_PPtrCurves" })
        {
            string track = "  " + kind + ":\n  - path: Armature/Head\n    attribute: m_Materials.Array.data[0]\n";
            Accepted(Parse("  m_FloatCurves:\n" + face + track), kind, kind);
            Rejected(Parse(track), kind + " alone must not become a neutral face pose");
        }
        foreach (var (classId, attribute, path) in new[] {
                     (4, "m_LocalPosition.x", "Armature/Head"),
                     (137, "material._Color.r", "Face"),
                     (1, "m_IsActive", "Accessory"),
                     (23, "m_Enabled", "Accessory") })
        {
            string track = FloatTrack(classId, attribute, path);
            Accepted(Parse("  m_FloatCurves:\n" + face + track), attribute, path + "/" + attribute);
            Rejected(Parse("  m_FloatCurves:\n" + track), attribute + " alone must not become a neutral face pose");
        }
        Rejected(Parse(""), "Empty and non-face motions are not FaceEmo expressions");
        string mixed = "  m_FloatCurves:\n" + face + FloatTrack(137, "material._Color.r", "Face");
        foreach (string invalid in new[] {
                     mixed.Replace("time: 1", "time: 0"),
                     mixed.Replace("value: 20", "value: NaN"),
                     mixed.Replace("value: 20", "value: Infinity"),
                     mixed.Replace("outSlope: 60", "outSlope: NaN"),
                     mixed.Replace("    path: Face\n    curve:", "    curve:"),
                     mixed.Replace("blendShape.Smile", "blendShape."),
                     "  m_FloatCurves:\n" + face + face,
                     "  m_FloatCurves:\n" + FloatTrack(137, "blendShape.Smile", "Face").Replace("      - time: 0\n        value: 1\n", ""),
                     mixed.Replace("value: 20", "value: -Infinity") })
            Rejected(Parse(invalid), "invalid face curves, bindings and unsupported timing remain rejected");
        Accepted(Parse(mixed + "  m_Events:\n  - functionName: Test\n  m_AnimationClipSettings:\n    m_StartTime: 0.5\n"),
            "FaceEmo ignores unrelated events and playback settings for face pose extraction", "");
        Console.WriteLine("PASS: FaceEmo mixed face extraction, unrelated effects ignored, malformed face curves rejected");
    }

    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
