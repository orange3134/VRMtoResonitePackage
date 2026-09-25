using System.Text;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class CacExpressionChecks
{
    public static void Run(string artifacts)
    {
        string root = Path.Combine(artifacts, "CacExpressionFixture");
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
        string Clip(int index, string path) => $"--- !u!74 &7400000\nAnimationClip:\n  m_Name: Emote{index}\n  m_FloatCurves:\n" +
            $"  - classID: 137\n    path: {path}\n    attribute: blendShape.Smile\n    curve:\n      m_Curve:\n      - time: 0\n        value: 0\n      - time: 1\n        value: {index * 3}\n";
        for (int i = 1; i <= 28; i++) Asset($"Emote{i}.anim", 1000 + i, Clip(i, "Face"));
        // Reverse set and Entry storage order: FaceEmo sorts by emote number, not hierarchy order.
        var yaml = new StringBuilder("--- !u!91 &91\nAnimatorController:\n  m_AnimatorParameters:\n  - m_Name: SYNC_EM_EMOTE\n    m_Type: 3\n    m_DefaultInt: 15\n  m_AnimatorLayers:\n  - m_Name: Player\n    m_StateMachine: {fileID: 100}\n" +
            "--- !u!1107 &100\nAnimatorStateMachine:\n  m_ChildStateMachines:\n  - m_StateMachine: {fileID: 102}\n  - m_StateMachine: {fileID: 101}\n");
        for (int set = 1; set <= 2; set++)
        {
            yaml.Append($"--- !u!1107 &{100 + set}\nAnimatorStateMachine:\n  m_EntryTransitions:\n");
            for (int i = set * 14; i > (set - 1) * 14; i--) yaml.Append($"  - {{fileID: {300 + i}}}\n");
        }
        for (int i = 1; i <= 28; i++)
        {
            yaml.Append($"--- !u!1109 &{300 + i}\nAnimatorTransition:\n  m_DstState: {{fileID: {200 + i}}}\n  m_Conditions:\n  - m_ConditionEvent: SYNC_EM_EMOTE\n    m_ConditionMode: 6\n    m_EventTreshold: {i}\n" +
                $"--- !u!1102 &{200 + i}\nAnimatorState:\n  m_Name: Emote{i}\n  m_WriteDefaultValues: 0\n  m_TimeParameterActive: 1\n  m_TimeParameter: CustomGrip\n  m_StateMachineBehaviours:\n  - {{fileID: 900}}\n  m_Motion: {{fileID: 7400000, guid: {Guid(1000+i)}}}\n");
        }
        yaml.Append("--- !u!114 &900\nMonoBehaviour:\n  parameters:\n  - name: CN_BLINK_ENABLE\n    value: 0\n");
        string original = yaml.ToString();
        ExpressionModel Parse(string text)
        {
            Asset("Face.controller", 2, text);
            using var package = UnityPackage.Open(Path.Combine(root, "Assets", "Avatar.prefab"));
            return VrchatExpressionParser.Parse(package, UnityScene.Parse(descriptor).Doc(1).Root);
        }
        var face = new ExpressionBinding("Face", "Smile");
        var baseline = new Dictionary<ExpressionBinding, float> { [face] = 0.17f };
        ExpressionModel Filter(string text)
        {
            var model = Parse(text); VrchatExpressionDetection.FilterFaceCurves(model, baseline); return model;
        }
        var parsed = Parse(original);
        Check(parsed.ImportedPatterns.Patterns.Count == 2 && parsed.Layers.Single().Name.EndsWith(" 1"), "first numerically ordered CAC set is selected despite authored parameter default and storage order");
        var model = Filter(original);
        Check(model.Clips.Count == 14 && model.DetectedExpressions.Count == 14 && model.Layers.Single().States.Count == 15,
            "only first fourteen clips and baseline are retained for output");
        var compiled = new GesturePairCompiler(model, model.Clips, _ => 0.17f);
        for (int left = 0; left < 8; left++) for (int right = 0; right < 8; right++)
        {
            int index = right > 0 ? right : left > 0 ? 7 + left : 0;
            float expected = index == 0 ? 0.17f : index * 0.03f;
            var clip = model.Clips.Concat(compiled.Generated).Single(c => c.Id == compiled.Pairs[left * 8 + right]);
            Check(Math.Abs(clip.Curves.Single().Keys[^1].Value - expected) < 0.00001f,
                $"FaceEmo first-match hand mapping and last frame: left={left}, right={right}, emote={index}");
        }
        Check(model.Layers.Single().States.Where(s => s.TimeParameter != null).Select(s => s.TimeParameter)
            .ToHashSet().SetEquals(new[] { "GestureLeftWeight", "GestureRightWeight" }), "only fists use full-grip Motion Time");
        Check(model.Diagnostics.Any(d => d.Contains("FaceEmo CAC: selected first set")), "first-set approximation is diagnosed");
        var muted = Filter(original.Replace("m_DstState: {fileID: 201}", "m_Mute: 1\n  m_DstState: {fileID: 201}"));
        Check(muted.Clips.Count == 13, "muted first-set entries are omitted without filling from later sets");
        var mutedTable = new GesturePairCompiler(muted, muted.Clips, _ => 0.17f);
        var mutedPose = muted.Clips.Concat(mutedTable.Generated).Single(c => c.Id == mutedTable.Pairs[2 * 8 + 1]);
        Check(Math.Abs(mutedPose.Curves.Single().Keys[^1].Value - 0.27f) < 0.00001f, "missing right branch allows the left branch to win");
        Asset("Emote1.anim", 1001, Clip(1, "Clothes"));
        var nonFace = Filter(original);
        Check(nonFace.Clips.Count == 13 && nonFace.Layers.Single().States.All(s => s.Name != "Emote1"), "non-face branch does not shadow another hand after real binding resolution");
        for (int i = 2; i <= 14; i++) Asset($"Emote{i}.anim", 1000 + i, Clip(i, "Clothes"));
        var next = Filter(original);
        Check(next.Clips.Count == 14 && next.Layers.Single().Name.EndsWith(" 2"), "first actual face set is chosen after binding resolution");
        for (int i = 15; i <= 28; i++) Asset($"Emote{i}.anim", 1000 + i, Clip(i, "Clothes"));
        var empty = Filter(original);
        Check(empty.Clips.Count == 0 && empty.Layers.Count == 0 && empty.DetectedExpressions.Count == 0, "no face set does not invent mappings");
        Asset("Emote1.anim", 1001, Clip(1, "Face"));
        var fractional = Filter(original.Replace("m_EventTreshold: 1\n", "m_EventTreshold: 1.5\n"));
        Check(fractional.Clips.Count == 0, "malformed emote numbers cannot select a different gesture");
        Console.WriteLine("PASS: CAC first face set, all64 FaceEmo hand priorities, grip endpoint, exclusions and later-set isolation");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
