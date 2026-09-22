using System.Text;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class CompleteHandDispatcherChecks
{
    public static void Run(string artifacts)
    {
        string root = Path.Combine(artifacts, "CompleteHandDispatcherFixture");
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        string Guid(int id) => id.ToString("x32");
        void Asset(string name, string guid, string yaml)
        {
            File.WriteAllText(Path.Combine(root, "Assets", name), yaml);
            File.WriteAllText(Path.Combine(root, "Assets", name + ".meta"), "guid: " + guid);
        }
        const string mask = "903ce375d5f609d44b9f00b425d6eda9";
        const string eye = "Armature/Head/LeftEye";
        var eyes = new HashSet<string>(StringComparer.Ordinal) { eye };
        string descriptor = "--- !u!114 &1\nMonoBehaviour:\n  baseAnimationLayers:\n  - type: 5\n    isDefault: 0\n    animatorController: {fileID: 91, guid: " + Guid(2) + "}\n";
        Asset("Avatar.prefab", Guid(1), descriptor);
        string Clip(int gesture) => $"--- !u!74 &7400000\nAnimationClip:\n  m_Name: Pose{gesture}\n  m_FloatCurves:\n  - attribute: blendShape.Smile\n    path: Face\n    classID: 137\n    curve:\n      m_Curve:\n      - time: 0\n        value: {10 + gesture * 10}\n";
        for (int gesture = 0; gesture < 8; gesture++) Asset($"Pose{gesture}.anim", Guid(1000 + gesture), Clip(gesture));
        string rotations = "  m_RotationCurves:\n  - path: " + eye + "\n    curve:\n      m_Curve:\n      - time: 0\n        value: {x: 0, y: 0.1, z: 0, w: 0.99}\n";
        Asset("Pose2.anim", Guid(1002), Clip(2) + rotations);
        var yaml = new StringBuilder("--- !u!91 &91\nAnimatorController:\n  m_AnimatorLayers:\n  - m_Name: Hands\n    m_StateMachine: {fileID: 100}\n    m_SyncedLayerIndex: -1\n    m_Mask: {fileID: 31900000, guid: " + mask + "}\n--- !u!1107 &100\nAnimatorStateMachine:\n  m_DefaultState: {fileID: 200}\n  m_ChildStates:\n  - m_State: {fileID: 200}\n");
        for (int g = 0; g < 8; g++) yaml.Append($"  - m_State: {{fileID: {300 + g}}}\n");
        yaml.Append("--- !u!1102 &200\nAnimatorState:\n  m_Name: Blink\n  m_WriteDefaultValues: 1\n  m_Motion: {fileID: 0}\n  m_Transitions:\n");
        for (int g = 0; g < 8; g++) yaml.Append($"  - {{fileID: {400 + g}}}\n");
        for (int g = 0; g < 8; g++)
        {
            yaml.Append($"--- !u!1102 &{300 + g}\nAnimatorState:\n  m_Name: Pose{g}\n  m_WriteDefaultValues: 0\n  m_Motion: {{fileID: 7400000, guid: {Guid(1000 + g)}}}\n  m_Transitions:\n  - {{fileID: {500 + g}}}\n");
            foreach (var (id, dest, mode) in new[] { (400 + g, 300 + g, 6), (500 + g, 200, 7) })
                yaml.Append($"--- !u!1101 &{id}\nAnimatorStateTransition:\n  m_DstState: {{fileID: {dest}}}\n  m_Conditions:\n  - m_ConditionEvent: GestureRight\n    m_ConditionMode: {mode}\n    m_EventTreshold: {g}\n");
        }
        string source = yaml.ToString();
        ExpressionModel Parse(string text, IReadOnlySet<string> trackedEyes = null, IReadOnlySet<string> possibleShapes = null)
        {
            Asset("Face.controller", Guid(2), text);
            using var package = UnityPackage.Open(Path.Combine(root, "Assets", "Avatar.prefab"));
            return VrchatExpressionParser.Parse(package, UnityScene.Parse(descriptor).Doc(1).Root, trackedEyePaths: trackedEyes, possibleShapeNames: possibleShapes);
        }
        void AllPairs(string text)
        {
            var model = Parse(text, eyes);
            Check(model.Layers.Count == 1, "complete hub imports one hand layer");
            var table = new GesturePairCompiler(model, model.Clips, _ => 0.23f);
            for (int left = 0; left < 8; left++) for (int right = 0; right < 8; right++)
            {
                var pose = model.Clips.Concat(table.Generated).Single(c => c.Id == table.Pairs[left * 8 + right]);
                Check(Math.Abs(pose.Curves.Single().Sample(0) - (right + 1) / 10f) < 0.0001f,
                    "all64 pairs choose hub destination including neutral and mixed eye clip");
            }
            Check(model.Diagnostics.Any(d => d.Contains("eye-bone rotations are omitted")), "eye approximation diagnosed");
        }
        AllPairs(source);
        foreach (string sdkMask in new[] { "b2b8bad9583e56a46a3e21795e96ad92", "7ff0199655202a04eb175de45a6e078a" })
            AllPairs(source.Replace(mask, sdkMask));
        Check(Parse(source).Layers.Count == 0, "unknown eye identity does not permit rotation omission");
        Check(Parse(source.Replace(mask, Guid(999)), eyes).Layers.Count == 0, "unknown missing mask rejected");
        Check(Parse(source.Replace("fileID: 31900000", "fileID: 31900001"), eyes).Layers.Count == 0, "known mask with wrong local ID rejected");
        Check(Parse(source.Replace("m_EventTreshold: 7", "m_EventTreshold: 6"), eyes).Layers.Count == 0, "incomplete gesture coverage rejected");
        Check(Parse(source.Replace("m_DstState: {fileID: 307}", "m_DstState: {fileID: 306}"), eyes).Layers.Count == 0, "duplicate destinations rejected");
        Check(Parse(source.Replace("m_ConditionMode: 7", "m_ConditionMode: 6"), eyes).Layers.Count == 0, "latched return predicate rejected");
        Check(Parse(source.Replace("m_DstState: {fileID: 200}", "m_DstState: {fileID: 201}"), eyes).Layers.Count == 0, "return to another state rejected");
        Check(Parse(source.Replace("m_DstState: {fileID: 307}", "m_HasExitTime: 1\n  m_DstState: {fileID: 307}"), eyes).Layers.Count == 0, "timed dispatch rejected");
        Check(Parse(source.Replace("m_DstState: {fileID: 200}", "m_HasExitTime: 1\n  m_DstState: {fileID: 200}"), eyes).Layers.Count == 0, "timed returns rejected");
        Asset("Pose2.anim", Guid(1002), Clip(2) + rotations.Replace(eye, "Armature/Head/OtherEye"));
        Check(Parse(source, eyes).Layers.Count == 0, "similar bone name is not tracked eye identity");
        Asset("Pose2.anim", Guid(1002), Clip(2) + rotations.Replace("m_RotationCurves", "m_PositionCurves"));
        Check(Parse(source, eyes).Layers.Count == 0, "eye translation still rejected");
        Asset("Pose2.anim", Guid(1002), Clip(2) + rotations.Replace("m_RotationCurves", "m_EulerCurves"));
        AllPairs(source);
        string floatRotation = "  - classID: 4\n    attribute: m_LocalRotation.x\n    path: " + eye + "\n    curve:\n      m_Curve:\n      - time: 0\n        value: 0.1\n";
        Asset("Pose2.anim", Guid(1002), Clip(2) + floatRotation);
        AllPairs(source);
        Asset("Pose2.anim", Guid(1002), Clip(2) + floatRotation.Replace("m_LocalRotation.x", "m_LocalPosition.x"));
        Check(Parse(source, eyes).Layers.Count == 0, "float eye position still rejected");
        Asset("Pose2.anim", Guid(1002), Clip(2) + rotations + "  m_Events:\n  - functionName: Test\n");
        Check(Parse(source, eyes).Layers.Count == 0, "eye exception does not bypass events");
        Asset("Pose2.anim", Guid(1002), Clip(2) + rotations);
        Asset("Override.mask", mask, "--- !u!319 &31900000\nAvatarMask:\n  m_Elements:\n  - m_Path: Face\n    m_Weight: 0\n");
        Check(Parse(source, eyes).Layers.Count == 0, "packaged SDK GUID override inspected rather than trusted");
        Asset("Override.mask", mask, "--- !u!319 &31900000\nAvatarMask:\n  m_Elements: []\n");
        AllPairs(source);
        string stale = "  - classID: 137\n    attribute: blendShape.RemovedShape\n    path: Face\n    curve:\n      m_Curve:\n      - time: 0\n        value: 100\n";
        Asset("Pose2.anim", Guid(1002), Clip(2) + stale);
        var knownShapes = new HashSet<string>(StringComparer.Ordinal) { "Smile" };
        var filtered = Parse(source, eyes, knownShapes);
        Check(filtered.Clips.Single(c => c.Name == "Pose2").Curves.Single().Binding.Shape == "Smile",
            "proven absent shape is a no-op without removing the live curve");
        Check(filtered.Diagnostics.Any(d => d.Contains("absent from all source meshes: Face/RemovedShape")),
            "removed source shape diagnosed");
        Check(Parse(source, eyes).Clips.Single(c => c.Name == "Pose2").Curves.Count == 2,
            "unknown source mesh inventory preserves unresolved bindings for strict resolution");
        knownShapes.Add("RemovedShape");
        Check(Parse(source, eyes, knownShapes).Clips.Single(c => c.Name == "Pose2").Curves.Count == 2,
            "shape present anywhere in source inventory is never assumed absent");
        Asset("Pose2.anim", Guid(1002), Clip(2) + stale.Replace("classID: 137", "classID: 4"));
        Check(Parse(source, eyes, new HashSet<string> { "Smile" }).Layers.Count == 0,
            "shape filtering does not bypass unsupported component tracks");
        Console.WriteLine("PASS: complete hand dispatcher, all64 pairs, SDK masks, tracked-eye mixed clips and unsafe graph/track rejection");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}