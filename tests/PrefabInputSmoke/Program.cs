using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Formats.Tar;
using System.IO.Compression;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

string resonite = Environment.GetEnvironmentVariable("RESONITE_PATH")
    ?? @"C:\Program Files (x86)\Steam\steamapps\common\Resonite";
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string path = Path.Combine(resonite, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
Run();

[MethodImpl(MethodImplOptions.NoInlining)]
static void Run()
{
    var rootResolver = new UnityModelFileIdResolver(null);
    Check(rootResolver.ResolveName(919132149155446097L) == "RootNode" &&
          rootResolver.ResolveName(-8679921383154817045L) == "RootNode",
        "Unity synthetic root IDs cannot resolve to an arbitrary clothing or armature node");
    // Keep fixtures for inspection; never recursively delete a computed directory.
    string root = Path.Combine(Path.GetTempPath(), "ResoPonPrefabSmoke", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
    var signedIds = UnityScene.Parse("--- !u!1102 &-9223372036854775808\nAnimatorState:\n  m_Name: Negative\n--- !u!1102 &42\nAnimatorState:\n  m_Name: Positive\n");
    Check(signedIds.Doc(long.MinValue)?.Root["m_Name"]?.AsString() == "Negative" && signedIds.Doc(42) != null,
        "Signed 64-bit YAML document IDs remain distinct");
    string selectedGuid = new('a', 32);
    string otherGuid = new('b', 32);
    string materialGuid = new('c', 32);
    string selected = Asset("Assets/Selected.prefab", selectedGuid, Avatar("Selected"));
    Asset("Assets/Other.prefab", otherGuid, Avatar("Other"));
    Asset("Library/PackageCache/com.example.materials@123/Surface.mat", materialGuid, "Material:\n  m_Name: Surface\n");
    string controllerGuid = new('e', 32);
    var controller = new System.Text.StringBuilder("--- !u!91 &91\nAnimatorController:\n  m_AnimatorLayers:\n  - m_StateMachine: {fileID: -100}\n--- !u!1107 &-100\nAnimatorStateMachine:\n  m_EntryTransitions:\n");
    for (int i = 1; i <= 15; i++) controller.Append($"  - {{fileID: {-100 - i}}}\n");
    for (int i = 0; i < 15; i++)
    {
        string clipGuid = i.ToString("x32");
        Asset($"Assets/Face{i}.anim", clipGuid, $$"""
--- !u!74 &7400000
AnimationClip:
  m_FloatCurves:
  - curve:
      m_Curve:
      - value: 0
      - value: 100
    attribute: blendShape.face{{i}}
    path: Body
    classID: 137
  - curve:
      m_Curve:
      - value: 0
    attribute: blendShape.reset
    path: Body
    classID: 137
""");
        int entry = i == 0 ? 15 : i;
        controller.Append($"--- !u!1109 &{-100 - entry}\nAnimatorTransition:\n");
        controller.Append(i == 0 ? "  m_Conditions: []\n" : $"  m_Conditions:\n  - m_ConditionMode: 6\n    m_ConditionEvent: Viseme\n    m_EventTreshold: {i}\n");
        controller.Append($"  m_DstState: {{fileID: {-200 - i}}}\n--- !u!1102 &{-200 - i}\nAnimatorState:\n  m_Motion: {{fileID: 7400000, guid: {clipGuid}}}\n");
    }
    Asset("Assets/Face.controller", controllerGuid, controller.ToString());
    CheckAnimatorBlink(Asset, selected);
    using (UnityPackage package = UnityPackage.Open(selected))
    {
        var descriptor = UnityYaml.ParseFlatDocument($"lipSync: 4\nbaseAnimationLayers:\n- type: 5\n  isDefault: 0\n  animatorController: {{fileID: 91, guid: {controllerGuid}}}\n");
        var face = new VrchatAvatar();
        VrchatAnimatorFaceParser.Apply(package, descriptor, face);
        Check(face.Visemes.Count == 15 && face.Visemes.All(v => v.MeshGameObjectName == "Body"),
            "Animator visemes resolve all fifteen shapes through negative state and transition IDs");
        foreach (var (preset, slot) in new[] { "sil", "PP", "FF", "TH", "DD", "kk", "CH", "SS", "nn", "RR", "aa", "E", "ih", "oh", "ou" }.Select((p, i) => (p, i)))
            Check(face.Visemes.Single(v => v.ResonitePreset == preset).BlendShapeName == $"face{slot}", "Animator mapping " + preset);
        face.Blink = new VrchatBlink { MeshGameObjectName = "Body", BlendShapeIndex = 0 };
        face.FbxBlendShapeNames["Body"] = new List<string> { "blink" };
        var model = VrchatModelAdapter.ToVrmModel(face);
        var blinkBind = model.Expressions.Single(e => e.Preset == "blink").Binds.Single();
        Check(model.MeshTargetNames[blinkBind.MeshIndex][blinkBind.MorphIndex] == "blink",
            "Blink index does not alias the first synthetic viseme on Body");
        face.HumanBones["leftEye"] = "HumanoidEye";
        face.LeftEyeBoneName = "DescriptorEye.L";
        face.RightEyeBoneName = "DescriptorEye.R";
        var eyes = VrchatModelAdapter.ToVrmModel(face);
        Check(eyes.GetNodeName(eyes.HumanBones["leftEye"]) == "DescriptorEye.L" &&
              eyes.GetNodeName(eyes.HumanBones["rightEye"]) == "DescriptorEye.R",
            "Descriptor eye references override and supplement humanoid eye mappings");
        var ordinary = new VrchatAvatar();
        VrchatAnimatorFaceParser.Apply(package, UnityYaml.ParseFlatDocument($"lipSync: 3\nbaseAnimationLayers:\n- type: 5\n  animatorController: {{guid: {controllerGuid}}}\n"), ordinary);
        Check(ordinary.Visemes.Count == 0, "Animator inference preserves descriptor-driven lip sync");
    }
    using (UnityPackage package = UnityPackage.Open(selected))
    {
        Check(package.InputPrefab.Guid == selectedGuid, "Selected prefab GUID");
        Check(package.ByGuid(otherGuid).HasContent, "Other prefab remains available for dependencies");
        Check(package.ByGuid(materialGuid).LogicalPath == "Packages/com.example.materials/Surface.mat", "Package dependency path");
        var choices = VrchatAvatarParser.ListAvatars(package);
        Check(choices.Count == 1 && choices[0].Name == "Selected", "Only selected prefab is an avatar candidate");
    }
    Check(File.ReadAllText(selected) == Avatar("Selected") && File.Exists(selected + ".meta"), "Dispose preserves project files");

    string invalid = Asset("Assets/NoDescriptor.prefab", new string('d', 32), "%YAML 1.1\n");
    using (UnityPackage package = UnityPackage.Open(invalid))
    {
        Check(VrchatAvatarParser.ListAvatars(package).Count == 0, "No fallback to unrelated project avatar");
        ExpectInvalid(() => VrchatAvatarParser.Parse(package));
    }
    string missingMeta = Path.Combine(root, "Assets", "Missing.prefab");
    File.WriteAllText(missingMeta, Avatar("Missing"));
    ExpectInvalid(() => UnityPackage.Open(missingMeta));
    string outside = Path.Combine(root, "..", Guid.NewGuid() + ".prefab");
    File.WriteAllText(outside, Avatar("Outside"));
    ExpectInvalid(() => UnityPackage.Open(outside));

    string archive = Path.Combine(root, "avatar.unitypackage");
    using (var gzip = new GZipStream(File.Create(archive), CompressionMode.Compress))
    using (var writer = new TarWriter(gzip))
    {
        Entry(selectedGuid + "/pathname", "Assets/Selected.prefab");
        Entry(selectedGuid + "/asset", Avatar("Selected"));
        Entry(selectedGuid + "/asset.meta", "guid: " + selectedGuid);
        void Entry(string name, string contents)
        {
            using var data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(contents));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data });
        }
    }
    string extracted;
    using (UnityPackage package = UnityPackage.Open(archive))
    {
        Check(package.InputPrefab == null, "Unitypackage remains unscoped");
        Check(VrchatAvatarParser.ListAvatars(package).Single().Name == "Selected", "Unitypackage avatar selection regression");
        extracted = package.ByGuid(selectedGuid).DiskPath;
    }
    Check(!File.Exists(extracted) && File.Exists(archive), "Only extracted temporary files are cleaned up");

    // A clothing prefab can include an EditorOnly copy of the avatar's Body. That copy must
    // not exclude the Body renderer in a different FBX, even when the composition loads it last.
    string bodyModel = new('1', 32);
    string clothingModel = new('2', 32);
    string bodyPrefab = new('3', 32);
    string clothingPrefab = new('4', 32);
    Asset("Assets/Body.fbx", bodyModel, "");
    string copySource = Asset("Assets/CopySource.fbx", "88000000000000000000000000000001", "");
    File.AppendAllText(copySource + ".meta", "ModelImporter:\n  internalIDToNameTable:\n  - first:\n      43: -1079801745714767569\n    second: Body_Base\n");
    string copyPrefab = Asset("Assets/Copy.prefab", "99000000000000000000000000000001",
        RendererPrefab("88000000000000000000000000000001", "Untagged").Replace("m_Name: Body", "m_Name: Body_Base_pants")
            .Replace("4300000", "-1079801745714767569"));
    using (UnityPackage package = UnityPackage.Open(copyPrefab))
    {
        var avatar = ReadFilter(copyPrefab);
        typeof(VrchatAvatarParser).GetMethod("ParseVariantRendererOverrides",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { package, package.InputPrefab.Guid, avatar });
        Check(avatar.MeshCopies.Single().SourceName == "Body_Base" && avatar.MeshCopies.Single().Name == "Body_Base_pants",
            "Prefab renderer copy resolves its mesh reference independently of its GameObject name");
        avatar.MeshCopies.Clear();
        avatar.PrefabRendererStates[new VrchatGameObjectReference("88000000000000000000000000000001", "Body_Base_pants")] = false;
        typeof(VrchatAvatarParser).GetMethod("ParseVariantRendererOverrides",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { package, package.InputPrefab.Guid, avatar });
        Check(avatar.MeshCopies.Count == 0, "Excluded renderer copies are not recreated");
    }
    Asset("Assets/Clothing.fbx", clothingModel, "");
    Asset("Assets/Body.prefab", bodyPrefab, RendererPrefab(bodyModel, "Untagged"));
    Asset("Assets/Clothing.prefab", clothingPrefab, RendererPrefab(clothingModel, "EditorOnly"));
    string composed = Asset("Assets/Composed.prefab", new string('5', 32), $$"""
        %YAML 1.1
        --- !u!1001 &10
        PrefabInstance:
          m_SourcePrefab: {fileID: 100100000, guid: {{bodyPrefab}}, type: 3}
        --- !u!1001 &20
        PrefabInstance:
          m_SourcePrefab: {fileID: 100100000, guid: {{clothingPrefab}}, type: 3}
        """);
    using (UnityPackage package = UnityPackage.Open(composed))
    {
        var avatar = new VrchatAvatar();
        typeof(VrchatAvatarParser).GetMethod("CollectVariantPrefabGameObjectNames",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            new[] { typeof(UnityPackage), typeof(string), typeof(VrchatAvatar) })!
            .Invoke(null, new object[] { package, package.InputPrefab.Guid, avatar });
        Check(avatar.ShouldKeepRenderer(bodyModel, "Body"), "Body survives another model's EditorOnly Body");
        Check(!avatar.ShouldKeepRenderer(clothingModel, "Body"), "EditorOnly clothing Body stays excluded");
        Check(!avatar.ShouldKeepRenderer(bodyModel, "Absent"), "Unreferenced meshes stay excluded");
        avatar.PrefabRendererStates.Clear();
        avatar.PrefabGameObjectNames.Clear();
        avatar.PrefabRendererStates[new VrchatGameObjectReference(bodyModel, "Body")] = false;
        Check(!avatar.ShouldKeepRenderer(bodyModel, "Body"), "An all-excluded filter still removes meshes");
        avatar.PrefabRendererStates.Clear();
        avatar.PrefabGameObjectNames.Add("Body");
        Check(avatar.ShouldKeepRenderer(bodyModel, "Body") && !avatar.ShouldKeepRenderer(bodyModel, "Absent"),
            "Regular prefab name filtering remains supported");
    }

    string parentGuid = new('6', 32);
    string nestedGuid = new('7', 32);
    Asset("Assets/Nested.prefab", nestedGuid, RendererPrefab(clothingModel, "Untagged"));
    string parentPath = Asset("Assets/EditorParent.prefab", parentGuid, $$"""
        %YAML 1.1
        --- !u!1 &1
        GameObject:
          m_Name: ClothingRoot
          m_TagString: EditorOnly
        --- !u!4 &10
        Transform:
          m_GameObject: {fileID: 1}
          m_Father: {fileID: 0}
        --- !u!1 &2
        GameObject:
          m_Name: Body
          m_TagString: Untagged
          m_Component:
          - component: {fileID: 3}
        --- !u!4 &20
        Transform:
          m_GameObject: {fileID: 2}
          m_Father: {fileID: 10}
        --- !u!137 &3
        SkinnedMeshRenderer:
          m_GameObject: {fileID: 2}
          m_Mesh: {fileID: 4300000, guid: {{bodyModel}}, type: 3}
        --- !u!1001 &100
        PrefabInstance:
          m_SourcePrefab: {fileID: 100100000, guid: {{nestedGuid}}, type: 3}
          m_Modification:
            m_TransformParent: {fileID: 20}
        """);
    VrchatAvatar excluded = ReadFilter(parentPath);
    Check(!excluded.ShouldKeepRenderer(bodyModel, "Body"), "EditorOnly parent excludes an Untagged child");
    Check(!excluded.ShouldKeepRenderer(clothingModel, "Body"), "EditorOnly parent excludes nested prefab meshes");
    Check(excluded.EditorOnlyFbxGuids.SetEquals(new[] { bodyModel, clothingModel }),
        "Entire EditorOnly models are omitted before importing their armatures");
    Check(excluded.EditorOnlyPrefabObjects[parentGuid].IsSupersetOf(new long[] { 1, 2 }),
        "EditorOnly subtree includes parent and child objects, not only renderers");

    string restoredPath = Asset("Assets/Restored.prefab", new string('8', 32), $$"""
        %YAML 1.1
        --- !u!1001 &100
        PrefabInstance:
          m_SourcePrefab: {fileID: 100100000, guid: {{parentGuid}}, type: 3}
          m_Modification:
            m_Modifications:
            - target: {fileID: 1, guid: {{parentGuid}}, type: 3}
              propertyPath: m_TagString
              value: Untagged
        """);
    VrchatAvatar restored = ReadFilter(restoredPath);
    Check(restored.ShouldKeepRenderer(bodyModel, "Body") && restored.ShouldKeepRenderer(clothingModel, "Body"),
        "Variant Untagged override restores the formerly EditorOnly subtree");
    Check(restored.EditorOnlyFbxGuids.Count == 0 && restored.EditorOnlyPrefabObjects.Count == 0,
        "Restored objects and armatures are eligible for import again");

    string childTaggedPath = Asset("Assets/ChildTagged.prefab", new string('9', 32), $$"""
        %YAML 1.1
        --- !u!1001 &100
        PrefabInstance:
          m_SourcePrefab: {fileID: 100100000, guid: {{parentGuid}}, type: 3}
          m_Modification:
            m_Modifications:
            - target: {fileID: 2, guid: {{parentGuid}}, type: 3}
              propertyPath: m_TagString
              value: Untagged
        """);
    Check(!ReadFilter(childTaggedPath).ShouldKeepRenderer(bodyModel, "Body"),
        "Child tag override cannot escape an EditorOnly ancestor");

    Asset("Assets/Duplicate.mat", materialGuid, "Material:\n");
    Asset("Assets/Duplicate2.mat", materialGuid, "Material:\n");
    ExpectInvalid(() => UnityPackage.Open(selected));
    Console.WriteLine("Prefab input smoke checks passed. Fixtures: " + root);

    string Asset(string relative, string guid, string contents)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        File.WriteAllText(path + ".meta", "fileFormatVersion: 2\nguid: " + guid + "\n");
        return path;
    }
}

static void CheckAnimatorBlink(Func<string, string, string, string> asset, string selected)
{
    const string controllerGuid = "aabbccddeeff00112233445566778899";
    const string clipGuid = "ffeeddccbbaa99887766554433221100";
    asset("Assets/Blink.anim", clipGuid, """
--- !u!74 &7400000
AnimationClip:
  m_AnimationClipSettings:
    m_LoopTime: 1
  m_FloatCurves:
  - curve:
      m_Curve:
      - value: 0
      - value: 100
      - value: 0
    attribute: blendShape.blink
    path: Body
    classID: 137
""");
    string controller = $$"""
--- !u!91 &91
AnimatorController:
  m_AnimatorParameters:
  - m_Name: BlinkEnabled
    m_Type: 4
    m_DefaultBool: 0
  - m_Name: ForceDisable
    m_Type: 4
    m_DefaultBool: 0
  m_AnimatorLayers:
  - m_StateMachine: {fileID: 10}
  - m_StateMachine: {fileID: 20}
    m_DefaultWeight: 0
  - m_StateMachine: {fileID: 30}
    m_DefaultWeight: 1
--- !u!1107 &10
AnimatorStateMachine:
  m_DefaultState: {fileID: 0}
--- !u!1107 &20
AnimatorStateMachine:
  m_DefaultState: {fileID: -21}
--- !u!1102 &-21
AnimatorState:
  m_StateMachineBehaviours:
  - {fileID: -22}
--- !u!114 &-22
MonoBehaviour:
  m_Enabled: 1
  m_Script: {fileID: -706344726, guid: 67cc4cb7839cd3741b63733d5adf0442}
  parameters:
  - type: 0
    name: BlinkEnabled
    value: 1
--- !u!1107 &30
AnimatorStateMachine:
  m_DefaultState: {fileID: -31}
--- !u!1102 &-31
AnimatorState:
  m_Transitions:
  - {fileID: -32}
--- !u!1101 &-32
AnimatorStateTransition:
  m_HasExitTime: 0
  m_DstState: {fileID: -33}
  m_Conditions:
  - m_ConditionMode: 1
    m_ConditionEvent: BlinkEnabled
  - m_ConditionMode: 2
    m_ConditionEvent: ForceDisable
--- !u!1102 &-33
AnimatorState:
  m_Motion: {fileID: 7400000, guid: {{clipGuid}}}
""";
    var descriptor = UnityYaml.ParseFlatDocument($"baseAnimationLayers:\n- type: 5\n  animatorController: {{guid: {controllerGuid}}}\n");
    VrchatAvatar Read(string yaml)
    {
        asset("Assets/Blink.controller", controllerGuid, yaml);
        using var package = UnityPackage.Open(selected);
        var avatar = new VrchatAvatar();
        VrchatAnimatorFaceParser.Apply(package, descriptor, avatar);
        return avatar;
    }
    Check(Read(controller).Blink?.BlendShapeName == "blink", "Startup parameter driver enables Body blink across layers");
    Check(Read(controller.Replace("m_DefaultBool: 0", "m_DefaultBool: 1")).Blink == null,
        "Explicit blink disable is respected");
    Check(Read(controller.Replace("m_HasExitTime: 0", "m_HasExitTime: 1")).Blink == null,
        "Timed transitions are not treated as startup blink settings");
    Check(Read(controller.Replace("    value: 1", "    value: 0")).Blink == null,
        "Inactive blink animation is not imported as an always-on driver");
}

static VrchatAvatar ReadFilter(string path)
{
    using UnityPackage package = UnityPackage.Open(path);
    var avatar = new VrchatAvatar();
    typeof(VrchatAvatarParser).GetMethod("CollectVariantPrefabGameObjectNames",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
        new[] { typeof(UnityPackage), typeof(string), typeof(VrchatAvatar) })!
        .Invoke(null, new object[] { package, package.InputPrefab.Guid, avatar });
    return avatar;
}

static void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    Console.WriteLine("PASS: " + label);
}

static void ExpectInvalid(Action action)
{
    try { action(); }
    catch (InvalidDataException) { return; }
    throw new Exception("Expected InvalidDataException");
}

static string Avatar(string name) => $$"""
%YAML 1.1
--- !u!1 &1
GameObject:
  m_Name: {{name}}
--- !u!4 &2
Transform:
  m_GameObject: {fileID: 1}
  m_Father: {fileID: 0}
--- !u!114 &3
MonoBehaviour:
  m_GameObject: {fileID: 1}
  m_Script: {fileID: 11500000, guid: 67cc4cb7839cd3741b63733d5adf0442, type: 3}
""";

static string RendererPrefab(string modelGuid, string tag) => $$"""
%YAML 1.1
--- !u!1 &1
GameObject:
  m_Name: Body
  m_TagString: {{tag}}
  m_Component:
  - component: {fileID: 2}
--- !u!137 &2
SkinnedMeshRenderer:
  m_GameObject: {fileID: 1}
  m_Mesh: {fileID: 4300000, guid: {{modelGuid}}, type: 3}
""";
