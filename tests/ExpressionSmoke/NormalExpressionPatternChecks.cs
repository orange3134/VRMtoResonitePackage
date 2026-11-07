using System.Text;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class NormalExpressionPatternChecks
{
    public static void Run(string artifacts)
    {
        string root = Path.Combine(artifacts, "NormalPatternFixture");
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        string Guid(int id) => id.ToString("x32");
        void Asset(string name, int id, string text)
        {
            File.WriteAllText(Path.Combine(root, "Assets", name), text);
            File.WriteAllText(Path.Combine(root, "Assets", name + ".meta"), "guid: " + Guid(id));
        }
        string descriptor = "--- !u!114 &1\nMonoBehaviour:\n  baseAnimationLayers:\n  - type: 5\n    isDefault: 0\n    animatorController: {fileID: 91, guid: " + Guid(2) + "}\n  expressionsMenu: {fileID: 114, guid: " + Guid(3) + "}\n";
        Asset("Avatar.prefab", 1, descriptor);
        Asset("Menu.asset", 3, "--- !u!114 &114\nMonoBehaviour:\n  controls:\n  - name: Old toggle\n    type: 102\n    parameter: {name: FacialSet}\n    value: 1\n");
        string Curve(string path, float value) => $"  - classID: 137\n    path: {path}\n    attribute: blendShape.Smile\n    curve:\n      m_Curve:\n      - time: 0\n        value: 12\n      - time: 1\n        value: {value}\n";
        for (int i = 1; i <= 32; i++)
            Asset($"Pose{i}.anim", 1000 + i, $"--- !u!74 &7400000\nAnimationClip:\n  m_Name: Pose{i}\n  m_FloatCurves:\n" +
                Curve(i == 31 ? "Clothes" : "Face", i == 30 ? 12 : i * 3 + 1));
        var yaml = new StringBuilder("--- !u!91 &91\nAnimatorController:\n  m_AnimatorParameters:\n  - m_Name: FacialSet\n    m_Type: 3\n    m_DefaultInt: 999\n  m_AnimatorLayers:\n  - m_Name: Lower\n    m_StateMachine: {fileID: 100}\n  - m_Name: Upper\n    m_DefaultWeight: 0\n    m_BlendingMode: 1\n    m_Mask: {fileID: 319, guid: " + Guid(99) + "}\n    m_StateMachine: {fileID: 101}\n");
        yaml.Append("--- !u!1107 &100\nAnimatorStateMachine:\n  m_AnyStateTransitions:\n");
        for (int i = 14; i >= 1; i--) yaml.Append($"  - {{fileID: {300 + i}}}\n");
        yaml.Append("--- !u!1107 &101\nAnimatorStateMachine:\n  m_DefaultState: {fileID: 224}\n  m_ChildStateMachines:\n  - m_StateMachine: {fileID: 102}\n" +
            "--- !u!1107 &102\nAnimatorStateMachine:\n  m_DefaultState: {fileID: 224}\n  m_EntryTransitions:\n");
        // Duplicate conditions at the top precede real entries; baseline/non-face clips must be filtered BEFORE splitting sets.
        foreach (int i in new[] { 30, 31 }.Concat(Enumerable.Range(15, 13).Reverse().Where(i => i != 22)).Concat(new[] { 22, 29 })) yaml.Append($"  - {{fileID: {300 + i}}}\n");
        string Condition(string hand, int gesture) => $"  - m_ConditionEvent: {hand}\n    m_ConditionMode: 6\n    m_EventTreshold: {gesture}\n";
        for (int i = 1; i <= 32; i++)
        {
            string conditions = i switch
            {
                <= 7 => Condition("GestureLeft", i), <= 14 => Condition("GestureRight", i - 7),
                <= 21 => Condition("GestureRight", i - 14), 22 => Condition("GestureRight", 1),
                23 => Condition("GestureLeft", 0), 24 => "", 25 => "  - m_ConditionEvent: Toggle\n    m_ConditionMode: 1\n",
                26 => Condition("Touch", 1), 27 => Condition("Hair_IsGrabbed", 1),
                29 => Condition("GestureLeft", 2) + Condition("GestureRight", 2),
                30 => Condition("GestureRight", 3), 31 => Condition("GestureRight", 4), _ => ""
            };
            yaml.Append($"--- !u!1109 &{300 + i}\nAnimatorTransition:\n  m_DstState: {{fileID: {200 + i}}}\n  m_Conditions:\n" + conditions +
                $"--- !u!1102 &{200 + i}\nAnimatorState:\n  m_Name: Pose{i}\n  m_WriteDefaultValues: 0\n  m_StateMachineBehaviours:\n  - {{fileID: 900}}\n  m_Motion: {{fileID: 7400000, guid: {Guid(1000 + i)}}}\n" +
                (i == 15 ? "  m_TimeParameterActive: 1\n  m_TimeParameter: ArbitraryGrip\n" : ""));
        }
        yaml.Append("--- !u!114 &900\nMonoBehaviour:\n  parameters:\n  - name: FacialSet\n    type: 2\n    value: 999\n");
        string source = yaml.ToString();
        var components = new[] { UnityYaml.ParseFlatDocument("receiverType: 1\ncollisionTags: []\nparameter: Touch\n"),
            UnityYaml.ParseFlatDocument("m_Script: {fileID: 1661641543, guid: 2a2c05204084d904aa4945ccff20d8e5}\nparameter: Hair\n") };
        var binding = new ExpressionBinding("Face", "Smile");
        ExpressionModel Parse(string text, IEnumerable<YamlNode> extra = null)
        {
            Asset("Face.controller", 2, text);
            using var package = UnityPackage.Open(Path.Combine(root, "Assets", "Avatar.prefab"));
            var model = VrchatExpressionParser.Parse(package, UnityScene.Parse(descriptor).Doc(1).Root, extra ?? components);
            VrchatExpressionDetection.FilterFaceCurves(model, new Dictionary<ExpressionBinding, float> { [binding] = 0.12f });
            return model;
        }
        var model = Parse(source);
        var names = model.Clips.Select(c => c.Name).ToHashSet();
        Check(names.SetEquals(Enumerable.Range(1, 7).Concat(Enumerable.Range(15, 7)).Concat(new[] { 23, 24, 29 }).Select(i => "Pose" + i)),
            "first normal pattern excludes shadowed, non-face, baseline, toggle, contact, physics and disconnected clips; unique manual candidate retained");
        Check(model.Menu.Count == 0 && !model.Parameters.ContainsKey("FacialSet"), "Expression Menu and parameter-default routing removed");
        Check(model.Layers.Single().States.Count == 17, "sixteen gesture branches plus authored baseline; manual candidate is not a catch-all");
        var compiler = new GesturePairCompiler(model, model.Clips, _ => 0.12f);
        for (int l = 0; l < 8; l++) for (int r = 0; r < 8; r++)
        {
            int index = l == 0 && r == 0 ? 23 : l == 2 && r == 2 ? 29 : r > 0 ? r + 14 : l;
            var pose = model.Clips.Concat(compiler.Generated).Single(c => c.Id == compiler.Pairs[l * 8 + r]);
            Check(Math.Abs(pose.Curves.Single().Keys[^1].Value - (index * 0.03f + 0.01f)) < 0.00001f,
                $"FaceEmo upper-layer/within-layer first-match table: {l},{r} -> {index}");
        }
        Check(model.Layers.Single().States.Single(s => s.Name == "Pose15").TimeParameter == "GestureRightWeight", "Fist accepts arbitrary authored Motion Time parameter and samples full grip");
        var noNeutral = Parse(source.Replace("m_DstState: {fileID: 223}", "m_Mute: 1\n  m_DstState: {fileID: 223}"));
        var neutralTable = new GesturePairCompiler(noNeutral, noNeutral.Clips, _ => 0.12f);
        Check(noNeutral.Clips.Concat(neutralTable.Generated).Single(c => c.Id == neutralTable.Pairs[0]).Curves.Single().Keys[^1].Value == 0.12f,
            "unconditioned candidate does not fill missing gesture cells");
        // The old router families depended on these state-graph details. FaceEmo extraction must not.
        foreach (string variation in new[] {
            source.Replace("m_WriteDefaultValues: 0", "m_WriteDefaultValues: 1"),
            source.Replace("m_DefaultInt: 999", "m_DefaultInt: 0"),
            source.Replace("    type: 2\n    value: 999", "    type: 0\n    value: 0"),
            source.Replace("AnimatorTransition:\n", "AnimatorTransition:\n  m_HasExitTime: 1\n  m_TransitionDuration: 10\n"),
            source.Replace("m_Name: Pose15\n", "m_Name: Pose15\n  m_Speed: 0\n  m_Transitions:\n  - {fileID: 999}\n") +
                "--- !u!1101 &999\nAnimatorStateTransition:\n  m_IsExit: 1\n",
            source.Replace("AnimatorStateMachine:\n", "AnimatorStateMachine:\n  m_StateMachineBehaviours:\n  - {fileID: 900}\n") })
        {
            var changed = Parse(variation);
            var table = new GesturePairCompiler(changed, changed.Clips, _ => 0.12f);
            Check(table.Pairs.SequenceEqual(compiler.Pairs), "Animator history, default gates, nested machines, drivers, exits and timing do not change FaceEmo pattern selection");
        }
        // MA merely supplies appended FX layers; the same FaceEmo ordering applies to them.
        var ma = UnityYaml.ParseFlatDocument("m_Script: {guid: 1bb122659f724ebf85fe095ac02dc339}\nlayerType: 5\npathMode: 1\nmergeAnimatorMode: 0\nlayerPriority: 1\nanimator: {fileID: 91, guid: " + Guid(2) + "}\n");
        var merged = VrchatModularExpressionInputs.Create(UnityScene.Parse(descriptor).Doc(1).Root, new[] { ma });
        Check(merged["baseAnimationLayers"].Seq.Count == 2, "MA Absolute/Append continues to supply FX for unified import");
        // Ordinary BlendTree uses its last endpoint; an all-nested tree is not IsFaceMotion in FaceEmo.
        string tree = source.Replace($"m_Motion: {{fileID: 7400000, guid: {Guid(1016)}}}", "m_Motion: {fileID: 206}") +
            "--- !u!206 &206\nBlendTree:\n  m_Childs:\n  - m_Motion: {fileID: 7400000, guid: " + Guid(1001) + "}\n  - m_Motion: {fileID: 7400000, guid: " + Guid(1032) + "}\n";
        var treeModel = Parse(tree); var treeTable = new GesturePairCompiler(treeModel, treeModel.Clips, _ => 0.12f);
        Check(Math.Abs(treeModel.Clips.Concat(treeTable.Generated).Single(c => c.Id == treeTable.Pairs[2]).Curves.Single().Keys[^1].Value - 0.97f) < 0.00001,
            "normal BlendTree selects last child without evaluating parameters");
        string emptyGrip = source.Replace($"m_Motion: {{fileID: 7400000, guid: {Guid(1015)}}}", "m_Motion: {fileID: 207}") +
            "--- !u!206 &207\nBlendTree:\n  m_Childs:\n  - m_Motion: {fileID: 7400000, guid: " + Guid(1001) + "}\n  - m_Motion: {fileID: 7400000, guid: " + Guid(1031) + "}\n";
        var gripModel = Parse(emptyGrip); var gripTable = new GesturePairCompiler(gripModel, gripModel.Clips, _ => 0.12f);
        Check(gripModel.Clips.Concat(gripTable.Generated).Single(c => c.Id == gripTable.Pairs[7 * 8 + 1]).Curves.Single().Keys[^1].Value == 0.12f,
            "full-grip endpoint with no face uses baseline and still shadows the other hand");
        Console.WriteLine("PASS: unified FaceEmo normal first pattern, all64 independent oracle, shadowed sets, source exclusions, neutral, grip, MA and old-router independence");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}