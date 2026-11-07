using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class SceneInstanceSelectionChecks
{
    public static void Run(Func<string, string, string, string> asset, string regularCopy)
    {
        const string baseGuid = "ad130000000000000000000000000081";
        const string sceneGuid = "ad130000000000000000000000000082";
        const string attachmentGuid = "ad130000000000000000000000000083";
        const string controllerGuid = "ad130000000000000000000000000084";
        const string clipGuid = "ad130000000000000000000000000085";
        string original = File.ReadAllText(regularCopy) + $$"""

  ViewPosition: {x: 0, y: 1.5, z: 0.1}
  lipSync: 3
  VisemeSkinnedMesh: {fileID: 2}
  VisemeBlendShapes: [sil, pp, ff, th, dd, kk, ch, ss, nn, rr, aa, e, ih, oh, ou]
  baseAnimationLayers:
  - type: 5
    isDefault: 0
    animatorController: {fileID: 91, guid: {{controllerGuid}}}
""";
        asset("Assets/SceneFace.anim", clipGuid, """
--- !u!74 &7400000
AnimationClip:
  m_Name: SceneExpression
  m_FloatCurves:
  - curve:
      m_Curve:
      - time: 0
        value: 100
    attribute: blendShape.Smile
    path: Body_Base
    classID: 137
""");
        asset("Assets/SceneFace.controller", controllerGuid, $$"""
--- !u!91 &91
AnimatorController:
  m_AnimatorLayers:
  - m_Name: Face
    m_StateMachine: {fileID: 10}
--- !u!1107 &10
AnimatorStateMachine:
  m_DefaultState: {fileID: 20}
  m_ChildStates:
  - m_State: {fileID: 20}
--- !u!1102 &20
AnimatorState:
  m_Motion: {fileID: 7400000, guid: {{clipGuid}}}
""");
        string basePath = asset("Assets/SceneAvatar.prefab", baseGuid, original);
        var baseScene = UnityScene.Parse(original);
        var renderer = baseScene.SkinnedMeshRenderers.Single();
        var mesh = baseScene.RendererMesh(renderer);
        long rendererGo = renderer.Root["m_GameObject"].FileID.Value;
        asset("Assets/SceneAttachment.prefab", attachmentGuid, $$"""
--- !u!1 &1
GameObject:
  m_Name: AttachedMesh
--- !u!4 &2
Transform:
  m_GameObject: {fileID: 1}
  m_Father: {fileID: 0}
--- !u!137 &3
SkinnedMeshRenderer:
  m_GameObject: {fileID: 1}
  m_Mesh: {fileID: {{mesh.FileID}}, guid: {{mesh.Guid}}}
""");
        foreach (bool omittedParent in new[] { false, true })
        foreach (bool strippedDescriptor in new[] { false, true })
        {
            string Instance(long id, string name)
            {
                long rootTransform = omittedParent ? (33 ^ id) & long.MaxValue : id + 2;
                string text = $$"""
--- !u!1001 &{{id}}
PrefabInstance:
  m_SourcePrefab: {fileID: 100100000, guid: {{baseGuid}}}
  m_Modification:
    m_TransformParent: {fileID: 9001}
    m_Modifications:
    - target: {fileID: 32, guid: {{baseGuid}}}
      propertyPath: m_Name
      value: {{name}}
    - target: {fileID: {{rendererGo}}, guid: {{baseGuid}}}
      propertyPath: m_Name
      value: {{name}}Body
    - target: {fileID: 34, guid: {{baseGuid}}}
      propertyPath: ViewPosition.y
      value: {{id / 1000}}
--- !u!1 &{{id + 1}} stripped
GameObject:
  m_CorrespondingSourceObject: {fileID: 32, guid: {{baseGuid}}}
  m_PrefabInstance: {fileID: {{id}}}
--- !u!114 &{{id + 3}}{{(strippedDescriptor ? " stripped" : "")}}
MonoBehaviour:
  m_CorrespondingSourceObject: {fileID: {{(strippedDescriptor ? 34 : 0)}}, guid: {{baseGuid}}}
  m_PrefabInstance: {fileID: {{(strippedDescriptor ? id : 0)}}}
  m_GameObject: {fileID: {{(strippedDescriptor ? 0 : id + 1)}}}
  m_Script: {fileID: 11500000, guid: 67cc4cb7839cd3741b63733d5adf0442}
--- !u!1 &{{id + 10}}
GameObject:
  m_Name: {{name}}Helper
--- !u!4 &{{id + 11}}
Transform:
  m_GameObject: {fileID: {{id + 10}}}
  m_Father: {fileID: {{rootTransform}}}
--- !u!114 &{{id + 12}}
MonoBehaviour:
  m_GameObject: {fileID: {{id + 10}}}
  m_Script: {fileID: 1661641543, guid: 2a2c05204084d904aa4945ccff20d8e5}
  rootTransform: {fileID: {{id + 11}}}
  pull: 0.3
  spring: 0.2
--- !u!1001 &{{id + 20}}
PrefabInstance:
  m_SourcePrefab: {fileID: 100100000, guid: {{attachmentGuid}}}
  m_Modification:
    m_TransformParent: {fileID: {{id + 11}}}
    m_Modifications:
    - target: {fileID: 1, guid: {{attachmentGuid}}}
      propertyPath: m_Name
      value: {{name}}Attachment
""" + "\n";
                if (!omittedParent)
                    text += $"--- !u!4 &{rootTransform} stripped\nTransform:\n  m_CorrespondingSourceObject: {{fileID: 33, guid: {baseGuid}}}\n  m_PrefabInstance: {{fileID: {id}}}\n";
                return text;
            }
            string sceneText = Instance(1000, "Other") + Instance(2000, "Selected") + """
--- !u!1 &9000
GameObject:
  m_Name: SceneContainer
--- !u!4 &9001
Transform:
  m_GameObject: {fileID: 9000}
  m_Father: {fileID: 0}
""";
            string scenePath = asset("Assets/MultipleAvatars.unity", sceneGuid, sceneText);
            using var project = UnityPackage.Open(basePath);
            using var package = project.CreateView(sceneGuid);
            foreach (var (selected, other, id) in new[] { ("Selected", "Other", 2000L), ("Other", "Selected", 1000L) })
            {
                var avatar = VrchatAvatarParser.Parse(package, selected);
                Check(avatar.Name == selected && avatar.MeshCopies.Count == 2 &&
                      avatar.MeshCopies.Any(c => c.Name == selected + "Body") &&
                      avatar.MeshCopies.Any(c => c.Name == selected + "Attachment") &&
                      avatar.MeshCopies.All(c => !c.Name.StartsWith(other)),
                    $"Scene selection isolates {selected} and retains its attached prefab (omitted={omittedParent}, stripped={strippedDescriptor})");
                Check(avatar.PhysBones.Any(b => b.RootBoneName == selected + "Helper") &&
                      avatar.PhysBones.All(b => b.RootBoneName != other + "Helper"),
                    "Scene selection keeps added physics only on the selected avatar");
                if (strippedDescriptor)
                {
                    Check(avatar.ViewPosition?.Y == id / 1000 && avatar.Visemes.Count > 0 &&
                          avatar.Visemes.All(v => v.MeshGameObjectName == selected + "Body") &&
                          avatar.ExpressionFaceTarget?.Name == selected + "Body" &&
                          avatar.ExpressionFacePath == null,
                        "Stripped scene descriptor inherits face settings and selected-instance overrides");
                    Check(avatar.Expressions.Clips.Any(c => c.Name == "SceneExpression") &&
                          avatar.DescriptorRootTarget?.Name == selected,
                        "Stripped scene descriptor retains inherited FX clips and canonical avatar root");
                }
                using var view = UnityPrefabInstances.CreateSceneInstanceView(package, sceneGuid, id);
                var selectedScene = view.ReadScene(view.ByGuid(sceneGuid));
                Check(selectedScene.Doc(9000) == null && selectedScene.Doc(id + 10) != null &&
                      selectedScene.Doc(id).Root["m_Modification"]["m_TransformParent"].FileID == 0,
                    "Scene instance selection retains local helpers and detaches the selected root from scene ancestors");
            }
            Check(File.ReadAllText(scenePath) == sceneText && File.ReadAllText(basePath) == original &&
                  package.ReadScene(package.ByGuid(sceneGuid)).Doc(1000) != null &&
                  package.ReadScene(package.ByGuid(sceneGuid)).Doc(2000) != null,
                "Repeated selection preserves both source scene occurrences and input files");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
