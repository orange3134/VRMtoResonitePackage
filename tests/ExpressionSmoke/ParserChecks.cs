using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class ParserChecks
{
    public static void Run(string artifacts)
    {
        string root = Path.Combine(artifacts, "ParserFixture");
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        void Asset(string name, string guid, string yaml)
        {
            File.WriteAllText(Path.Combine(root, "Assets", name), yaml);
            File.WriteAllText(Path.Combine(root, "Assets", name + ".meta"), "fileFormatVersion: 2\nguid: " + guid + "\n");
        }
        const string controller = "11111111111111111111111111111111", clip = "22222222222222222222222222222222";
        const string menu = "33333333333333333333333333333333", submenu = "44444444444444444444444444444444";
        string descriptor = $$"""
            --- !u!114 &1
            MonoBehaviour:
              baseAnimationLayers:
              - type: 5
                isDefault: 0
                animatorController: {fileID: 91, guid: {{controller}}, type: 2}
              expressionsMenu: {fileID: 11400000, guid: {{menu}}, type: 2}
            """;
        Asset("Avatar.prefab", "55555555555555555555555555555555", descriptor);
        Asset("Face.controller", controller, $$"""
            --- !u!91 &91
            AnimatorController:
              m_AnimatorParameters:
              - m_Name: GestureLeft
                m_Type: 3
                m_DefaultInt: 0
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
              m_Speed: 1
              m_Motion: {fileID: 0}
            --- !u!1102 &201
            AnimatorState:
              m_Name: Smile
              m_WriteDefaultValues: 1
              m_Speed: 1
              m_Motion: {fileID: 7400000, guid: {{clip}}, type: 2}
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
        Asset("Smile.anim", clip, """
            --- !u!74 &7400000
            AnimationClip:
              m_Name: Smile
              m_FloatCurves:
              - curve:
                  m_Curve:
                  - time: 0
                    value: 25
                    inSlope: 0
                    outSlope: 100
                  - time: 1
                    value: 75
                    inSlope: 0
                    outSlope: 0
                attribute: blendShape.Smile
                path: Face
                classID: 137
              m_AnimationClipSettings:
                m_StopTime: 1
            """);
        Asset("Menu.asset", menu, $$"""
            --- !u!114 &11400000
            MonoBehaviour:
              controls:
              - name: Face
                type: 103
                subMenu: {fileID: 11400000, guid: {{submenu}}, type: 2}
            """);
        Asset("Submenu.asset", submenu, """
            --- !u!114 &11400000
            MonoBehaviour:
              controls:
              - name: Smile toggle
                type: 102
                parameter:
                  name: GestureLeft
                value: 1
              - name: Smile button
                type: 101
                parameter:
                  name: GestureLeft
                value: 1
            """);
        using var package = UnityPackage.Open(Path.Combine(root, "Assets", "Avatar.prefab"));
        var model = VrchatExpressionParser.Parse(package, UnityScene.Parse(descriptor).Doc(1).Root);
        if (model.Layers.Count != 1 || model.Clips.Count != 1 || model.Layers[0].Transitions.Count != 2 ||
            model.Menu.Single().Type != 2 || !model.Menu[0].Children.Select(c => c.Type).SequenceEqual(new[] { 1, 0 }) ||
            model.Clips[0].Curves[0].Keys[0].Value != 0.25f || model.Clips[0].Curves[0].Keys[0].OutSlope != 1f)
            throw new InvalidOperationException("VRChat expression parser fixture failed");
        Console.WriteLine("PASS: real Unity menu enum values, Animator gesture routes, and normalized animation curves");
        string controllerPath = Path.Combine(root, "Assets", "Face.controller");
        string timed = File.ReadAllText(controllerPath).Replace("m_Name: Smile", "m_Name: Smile\n  m_TimeParameterActive: 1\n  m_TimeParameter: GestureLeftWeight");
        File.WriteAllText(controllerPath, timed);
        using (var timedPackage = UnityPackage.Open(Path.Combine(root, "Assets", "Avatar.prefab")))
        {
            var timedModel = VrchatExpressionParser.Parse(timedPackage, UnityScene.Parse(descriptor).Doc(1).Root);
            if (timedModel.Layers.Count != 1 || timedModel.Layers[0].States[1].TimeParameter != "GestureLeftWeight")
                throw new InvalidOperationException("Gesture-weight motion time must remain importable");
            var table = new VrmToResonitePackage.Expressions.GesturePairCompiler(timedModel, timedModel.Clips, _ => 0);
            var pose = timedModel.Clips.Concat(table.Generated).Single(c => c.Id == table.Pairs[8]);
            if (pose.Curves[0].Keys.Count != 1 || pose.Curves[0].Sample(0) != 0.75f || timedModel.Clips[0].Curves[0].Keys.Count != 2)
                throw new InvalidOperationException("Discrete gesture samples full weight while preserving the original animated clip");
        }
        File.WriteAllText(controllerPath, timed.Replace("m_TimeParameter: GestureLeftWeight", "m_TimeParameter: CustomClock"));
        using (var unsupported = UnityPackage.Open(Path.Combine(root, "Assets", "Avatar.prefab")))
        {
            var unsupportedModel = VrchatExpressionParser.Parse(unsupported, UnityScene.Parse(descriptor).Doc(1).Root);
            if (unsupportedModel.Layers.Count != 0 || unsupportedModel.Clips.Count != 1)
                throw new InvalidOperationException("Unknown motion-time parameters must remain diagnosed instead of guessed");
        }
        Console.WriteLine("PASS: gesture-weight time sampling and unsupported-clock diagnostic");
    }
}
