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
    System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(
        Environment.GetEnvironmentVariable("RESONITE_PATH") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Resonite",
        "runtimes", "win-x64", "native", "assimp.dll"));
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
    Asset("Library/PackageCache/com.example.materials@stale/Surface.mat", materialGuid, "STALE");
    string embeddedGuid = "abababababababababababababababab";
    Asset("Packages/com.example.embedded/Embedded.mat", embeddedGuid, "Material:\n");
    using (var package = UnityPackage.Open(selected))
    {
        Check(package.ByGuid(materialGuid) == null && VrchatAvatarParser.ListAvatars(package).Single().Name == "Selected",
            "Missing package lock ignores all stale cache versions and preserves Assets-only prefab input");
        Check(package.ByGuid(embeddedGuid)?.HasContent == true, "Embedded packages remain available without a lock");
    }
    string lockFile = Path.Combine(root, "Packages", "packages-lock.json");
    File.WriteAllText(lockFile, """{"dependencies":{"com.example.materials":{"version":"123","source":"registry"}}}""");
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
    path: Branch{{i}}/Body
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
    string soloViseme = controller.ToString().Replace("--- !u!1109 &-101\nAnimatorTransition:\n",
        "--- !u!1109 &-101\nAnimatorTransition:\n  m_Solo: 1\n");
    Asset("Assets/Face.controller", controllerGuid, soloViseme);
    using (var package = UnityPackage.Open(selected))
    {
        var face = new VrchatAvatar();
        VrchatAnimatorFaceParser.Apply(package, UnityYaml.ParseFlatDocument($"lipSync: 4\nbaseAnimationLayers:\n- type: 5\n  animatorController: {{guid: {controllerGuid}}}\n"), face);
        Check(face.Visemes.Count == 1 && face.Visemes.Single().ResonitePreset == "PP",
            "Solo viseme entry suppresses non-solo phonemes and the silence fallback");
    }
    Asset("Assets/Face.controller", controllerGuid, soloViseme.Replace("m_Solo: 1", "m_Solo: 1\n  m_Mute: 1"));
    using (var package = UnityPackage.Open(selected))
    {
        var face = new VrchatAvatar();
        VrchatAnimatorFaceParser.Apply(package, UnityYaml.ParseFlatDocument($"lipSync: 4\nbaseAnimationLayers:\n- type: 5\n  animatorController: {{guid: {controllerGuid}}}\n"), face);
        Check(face.Visemes.Count == 13 && face.Visemes.All(v => v.ResonitePreset != "PP" && v.ResonitePreset != "sil"),
            "Muted solo does not suppress other visemes or count toward the complete silence fallback");
    }
    Asset("Assets/Face.controller", controllerGuid, controller.ToString());
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
        Check(face.Visemes.Select(v => v.MeshGameObjectPath).Distinct().Count() == 15 &&
              model.MeshBindingPaths.Values.ToHashSet().SetEquals(Enumerable.Range(0, 15).Select(i => $"Branch{i}/Body")),
            "Same-named Animator renderers retain full distinct binding paths through adaptation");
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
    // A supported binding must not hide a conflicting binding that cannot be represented.
    string conflictGuid = "aa001122334455667788990011223344";
    string conflictClip = File.ReadAllText(Path.Combine(root, "Assets/Face1.anim"))
        .Replace("blendShape.reset", "blendShape.second").Replace("      - value: 0", "      - value: 100");
    Asset("Assets/Conflict.anim", conflictGuid, conflictClip);
    string conflictingController = controller.ToString().Replace("  m_EntryTransitions:\n",
        "  m_EntryTransitions:\n  - {fileID: -501}\n") + $$"""

--- !u!1109 &-501
AnimatorTransition:
  m_Conditions:
  - m_ConditionMode: 6
    m_ConditionEvent: Viseme
    m_EventTreshold: 1
  m_DstState: {fileID: -502}
--- !u!1102 &-502
AnimatorState:
  m_Motion: {fileID: 7400000, guid: {{conflictGuid}}}
""";
    Asset("Assets/Face.controller", controllerGuid, conflictingController);
    using (UnityPackage package = UnityPackage.Open(selected))
    {
        var face = new VrchatAvatar();
        VrchatAnimatorFaceParser.Apply(package, UnityYaml.ParseFlatDocument($"lipSync: 4\nbaseAnimationLayers:\n- type: 5\n  animatorController: {{guid: {controllerGuid}}}\n"), face);
        Check(face.Visemes.All(v => v.ResonitePreset != "PP"), "Unsupported competing viseme binding does not select a misleading single shape");
        Check(face.Visemes.Count == 14, "Conflicting viseme does not discard unrelated phonemes");
    }
    Asset("Assets/Face.controller", controllerGuid, controller.ToString());
    Asset("Assets/Conflict.anim", conflictGuid, File.ReadAllText(Path.Combine(root, "Assets/Face1.anim"))
        .Replace("Branch1/Body", "Other/Body"));
    Asset("Assets/Face.controller", controllerGuid, conflictingController);
    using (UnityPackage package = UnityPackage.Open(selected))
    {
        var face = new VrchatAvatar();
        VrchatAnimatorFaceParser.Apply(package, UnityYaml.ParseFlatDocument($"lipSync: 4\nbaseAnimationLayers:\n- type: 5\n  animatorController: {{guid: {controllerGuid}}}\n"), face);
        Check(face.Visemes.Count == 14 && face.Visemes.All(v => v.ResonitePreset != "PP"),
            "Same-named shapes on different hierarchy paths cannot collapse into one viseme candidate");
    }
    Asset("Assets/Face.controller", controllerGuid, controller.ToString());
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
    string branchesGuid = "1234567890abcdef1234567890abcdef";
    Asset("Assets/Branches.fbx", branchesGuid, """
; FBX 7.4.0 project file
FBXHeaderExtension: {
    FBXHeaderVersion: 1003
    FBXVersion: 7400
}
Objects: {
    Model: 1, "Model::Left", "Null" { }
    Model: 2, "Model::Right", "Null" { }
    Model: 3, "Model::Shared", "Null" { }
    Model: 4, "Model::Shared", "Null" { }
    Model: 5, "Model::Body", "Mesh" { }
    Model: 6, "Model::Body", "Mesh" { }
    Geometry: 7, "Geometry::Triangle", "Mesh" {
        Vertices: *9 { a: 0,0,0,1,0,0,0,1,0 }
        PolygonVertexIndex: *3 { a: 0,1,-3 }
    }
}
Connections: {
    C: "OO",1,0
    C: "OO",2,0
    C: "OO",3,1
    C: "OO",4,2
    C: "OO",5,3
    C: "OO",6,4
    C: "OO",7,5
    C: "OO",7,6
}
""");
    using (var package = UnityPackage.Open(selected))
    {
        var resolver = new UnityModelFileIdResolver(package.ByGuid(branchesGuid));
        long leftId = (long)typeof(UnityModelFileIdResolver).GetMethod("Compute",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, new object[] { "GameObject", "//RootNode/Left/Shared", 0 })!;
        var paths = resolver.NodePathsUnder(leftId).ToArray();
        Check(paths.Length == 2 && paths.All(p => p.Contains("/Left/Shared")),
            "File ID subtree lookup isolates one of two same-named branches");
        Check(resolver.RendererPathsUnder(leftId).Single().EndsWith("/Left/Shared/Body"),
            "Renderer subtree lookup retains the full path of a same-named mesh");
        Check(!resolver.IsUniqueNodeName("Body") && !resolver.IsUniqueNodeName("Shared"),
            "Duplicate names cannot become model-wide exclusions");
        long meshId = (long)typeof(UnityModelFileIdResolver).GetMethod("Compute",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, new object[] { "Mesh", "//RootNode/Right/Shared/Body/Mesh", 0 })!;
        string pathCopy = Asset("Assets/PathCopy.prefab", "12340000000000000000000000000003",
            RendererPrefab(branchesGuid, "Untagged").Replace("4300000", meshId.ToString()));
        using var copyPackage = UnityPackage.Open(pathCopy);
        var copyAvatar = ReadFilter(pathCopy);
        typeof(VrchatAvatarParser).GetMethod("ParseVariantRendererOverrides",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { copyPackage, copyPackage.InputPrefab.Guid, copyAvatar });
        Check(copyAvatar.MeshCopies.Single().SourcePath.EndsWith("/Right/Shared/Body"),
            "Copied renderer retains the source path resolved from its mesh file ID");
    }
    string regular = Asset("Assets/Regular.prefab", "abcdefabcdefabcdefabcdefabcdefab",
        Avatar("Regular").Replace("m_Father: {fileID: 0}",
            "m_Father: {fileID: 0}\n  m_Children:\n  - {fileID: 11}\n  - {fileID: 21}") + $$"""

--- !u!1 &10
GameObject:
  m_Name: Body
  m_Component:
  - component: {fileID: 12}
--- !u!4 &11
Transform:
  m_GameObject: {fileID: 10}
  m_Father: {fileID: 2}
--- !u!137 &12
SkinnedMeshRenderer:
  m_GameObject: {fileID: 10}
  m_Mesh: {fileID: 4300000, guid: {{bodyModel}}}
--- !u!1 &20
GameObject:
  m_Name: EditorParent
  m_TagString: EditorOnly
--- !u!4 &21
Transform:
  m_GameObject: {fileID: 20}
  m_Father: {fileID: 2}
  m_Children:
  - {fileID: 31}
--- !u!1 &30
GameObject:
  m_Name: Preview
  m_Component:
  - component: {fileID: 32}
--- !u!4 &31
Transform:
  m_GameObject: {fileID: 30}
  m_Father: {fileID: 21}
--- !u!137 &32
SkinnedMeshRenderer:
  m_GameObject: {fileID: 30}
  m_Mesh: {fileID: 4300000, guid: {{bodyModel}}}
  m_Materials:
  - {fileID: 2100000, guid: {{materialGuid}}}
--- !u!114 &33
MonoBehaviour:
  m_GameObject: {fileID: 30}
  m_Script: {fileID: 1661641543, guid: 2a2c05204084d904aa4945ccff20d8e5}
  rootTransform: {fileID: 31}
""");
    using (var package = UnityPackage.Open(regular))
    {
        var avatar = VrchatAvatarParser.Parse(package);
        Check(avatar.ShouldKeepRenderer(bodyModel, "Body") && !avatar.ShouldKeepRenderer(bodyModel, "Preview"),
            "Regular prefab excludes an EditorOnly descendant while retaining Body");
        Check(avatar.RendererMaterials.All(r => r.RendererGameObjectName != "Preview") && avatar.PhysBones.Count == 0,
            "Regular prefab excludes EditorOnly renderer materials and PhysBones");
    }
    string copySource = Asset("Assets/CopySource.fbx", "88000000000000000000000000000001", "");
    File.AppendAllText(copySource + ".meta", "ModelImporter:\n  internalIDToNameTable:\n  - first:\n      43: -1079801745714767569\n    second: Body_Base\n");
    string copyPrefab = Asset("Assets/Copy.prefab", "99000000000000000000000000000001",
        RendererPrefab("88000000000000000000000000000001", "Untagged").Replace("m_Name: Body", "m_Name: Body_Base_pants")
            .Replace("4300000", "-1079801745714767569") + """

--- !u!4 &31
Transform:
  m_GameObject: {fileID: 1}
  m_Father: {fileID: 33}
  m_LocalPosition: {x: 1, y: 2, z: 3}
  m_LocalRotation: {x: 0, y: 0, z: 1, w: 0}
  m_LocalScale: {x: 2, y: 3, z: 4}
--- !u!1 &32
GameObject:
  m_Name: CopyParent
  m_IsActive: 0
--- !u!4 &33
Transform:
  m_GameObject: {fileID: 32}
  m_Father: {fileID: 0}
  m_LocalPosition: {x: 4, y: 5, z: 6}
""");
    using (UnityPackage package = UnityPackage.Open(copyPrefab))
    {
        var avatar = ReadFilter(copyPrefab);
        typeof(VrchatAvatarParser).GetMethod("ParseVariantRendererOverrides",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { package, package.InputPrefab.Guid, avatar });
        Check(avatar.MeshCopies.Single().SourceName == "Body_Base" && avatar.MeshCopies.Single().Name == "Body_Base_pants",
            "Prefab renderer copy resolves its mesh reference independently of its GameObject name");
        var copy = avatar.MeshCopies.Single();
        Check(!copy.ParentTransforms.Single().Active,
            "Copied mesh retains its inactive prefab-authored parent");
        Check(copy.Transform.LocalPosition.X == 1 && copy.Transform.LocalScale.Z == 4 && copy.Transform.LocalRotation.Z == 1 &&
            copy.ParentTransforms.Single().Name == "CopyParent" && copy.ParentTransforms.Single().LocalPosition.X == 4,
            "Copied renderer retains authored parent, position, rotation and scale");
        avatar.MeshCopies.Clear();
        avatar.PrefabRendererStates[new VrchatGameObjectReference("88000000000000000000000000000001", "Body_Base_pants")] = false;
        typeof(VrchatAvatarParser).GetMethod("ParseVariantRendererOverrides",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { package, package.InputPrefab.Guid, avatar });
        Check(avatar.MeshCopies.Count == 0, "Excluded renderer copies are not recreated");
    }
    string sameNameText = File.ReadAllText(copyPrefab).Replace("Body_Base_pants", "Body_Base");
    string sameNameCopy = Asset("Assets/SameNameCopy.prefab", "12340000000000000000000000000001", sameNameText);
    using (var package = UnityPackage.Open(sameNameCopy))
    {
        var avatar = ReadFilter(sameNameCopy);
        typeof(VrchatAvatarParser).GetMethod("ParseVariantRendererOverrides",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { package, package.InputPrefab.Guid, avatar });
        Check(avatar.MeshCopies.Single().Name == "Body_Base" && avatar.MeshCopies.Single().RendererFileId == 2,
            "An authored renderer with the source mesh name is still collected by component ID");
    }
    string regularCopy = Asset("Assets/RegularCopy.prefab", "12340000000000000000000000000002",
        sameNameText.Replace("m_Name: CopyParent", "m_Name: CopyParent\n  m_Component:\n  - component: {fileID: 34}")
            .Replace("m_GameObject: {fileID: 32}", "m_GameObject: {fileID: 32}\n  m_Children:\n  - {fileID: 31}")
        + "\n--- !u!114 &34\nMonoBehaviour:\n  m_GameObject: {fileID: 32}\n  m_Script: {fileID: 11500000, guid: 67cc4cb7839cd3741b63733d5adf0442}\n");
    using (var package = UnityPackage.Open(regularCopy))
    {
        var avatar = VrchatAvatarParser.Parse(package);
        var copy = avatar.MeshCopies.Single();
        Check(copy.Name == "Body_Base" && copy.Transform.LocalPosition.X == 1 && copy.Transform.LocalScale.Z == 4 &&
              copy.ParentTransforms.Single().Name == "CopyParent" && copy.ReplaceSourceRenderer,
            "Regular descriptor prefab collects authored renderer placement and replaces the imported template");
    }
    CheckCopiedBoneReferences(Asset, branchesGuid, bodyModel);
    CheckNestedComponents(Asset, regularCopy);
    string staticGuid = "14141414141414141414141414141414";
    string staticText = File.ReadAllText(regularCopy)
        .Replace("--- !u!137 &2\nSkinnedMeshRenderer:", "--- !u!23 &2\nMeshRenderer:")
        .Replace("  m_Mesh: {fileID: -1079801745714767569, guid: 88000000000000000000000000000001, type: 3}",
            "  m_Enabled: 0\n  m_Materials:\n  - {fileID: 2100000, guid: " + materialGuid + "}")
        + "\n--- !u!33 &35\nMeshFilter:\n  m_GameObject: {fileID: 1}\n  m_Mesh: {fileID: -1079801745714767569, guid: 88000000000000000000000000000001, type: 3}\n";
    string staticPrefab = Asset("Assets/Static.prefab", staticGuid, staticText);
    using (var package = UnityPackage.Open(staticPrefab))
    {
        var avatar = VrchatAvatarParser.Parse(package);
        var copy = avatar.MeshCopies.Single();
        Check(!copy.IsSkinned && !copy.Enabled && copy.ReplaceSourceRenderer && copy.SourceName == "Body_Base" &&
              copy.Transform.LocalPosition.X == 1 && copy.ParentTransforms.Single().Name == "CopyParent",
            "Regular static renderer uses its own MeshFilter and preserves authored placement and enabled state");
        Check(avatar.RendererMaterials.Single().MaterialGuids.Single() == materialGuid,
            "Regular static renderer retains its material assignment");
    }
    string staticVariant = Asset("Assets/StaticVariant.prefab", "15151515151515151515151515151515", $$"""
--- !u!1001 &99
PrefabInstance:
  m_SourcePrefab: {fileID: 100100000, guid: {{staticGuid}}}
  m_Modification:
    m_Modifications:
    - target: {fileID: 2, guid: {{staticGuid}}}
      propertyPath: m_Enabled
      value: 1
    - target: {fileID: 31, guid: {{staticGuid}}}
      propertyPath: m_LocalPosition.x
      value: 9
""");
    using (var package = UnityPackage.Open(staticVariant))
    {
        var avatar = ReadFilter(staticVariant);
        typeof(VrchatAvatarParser).GetMethod("ParseVariantRendererOverrides",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { package, package.InputPrefab.Guid, avatar });
        Check(!avatar.MeshCopies.Single().IsSkinned && avatar.MeshCopies.Single().Enabled &&
              avatar.MeshCopies.Single().Transform.LocalPosition.X == 9,
            "Variant static renderer applies enabled and transform overrides");
    }
    string extraGuid = "12340000000000000000000000000004";
    string extraFbx = Asset("Assets/Extra.fbx", extraGuid, "");
    File.AppendAllText(extraFbx + ".meta", "ModelImporter:\n  internalIDToNameTable:\n  - first:\n      43: 4300000\n    second: Body\n");
    string multiCopy = Asset("Assets/MultiCopy.prefab", "12340000000000000000000000000005",
        File.ReadAllText(regularCopy).Replace("  - {fileID: 31}", "  - {fileID: 31}\n  - {fileID: 103}") +
        "\n" + RendererPrefab(extraGuid, "Untagged").Replace("&1", "&101").Replace("&2", "&102")
            .Replace("fileID: 1}", "fileID: 101}").Replace("fileID: 2}", "fileID: 102}") +
        "\n--- !u!4 &103\nTransform:\n  m_GameObject: {fileID: 101}\n  m_Father: {fileID: 33}\n");
    using (var package = UnityPackage.Open(multiCopy))
    {
        var avatar = VrchatAvatarParser.Parse(package);
        Check(avatar.MeshCopies.Count == 2 && avatar.AdditionalFbxs.Single().Guid == extraGuid &&
              Path.GetFullPath(avatar.AdditionalFbxs.Single().Path) == Path.GetFullPath(extraFbx),
            "Regular prefab schedules the secondary mesh source FBX for import");
    }
    string staticMulti = Asset("Assets/StaticMulti.prefab", "16161616161616161616161616161616",
        File.ReadAllText(multiCopy).Replace("--- !u!137 &102\nSkinnedMeshRenderer:", "--- !u!23 &102\nMeshRenderer:")
        .Replace("  m_Mesh: {fileID: 4300000, guid: " + extraGuid + ", type: 3}", "")
        + "\n--- !u!33 &104\nMeshFilter:\n  m_GameObject: {fileID: 101}\n  m_Mesh: {fileID: 4300000, guid: " + extraGuid + "}\n");
    using (var package = UnityPackage.Open(staticMulti))
    {
        var avatar = VrchatAvatarParser.Parse(package);
        Check(avatar.AdditionalFbxs.Single().Guid == extraGuid && avatar.MeshCopies.Any(c => !c.IsSkinned && c.FbxGuid == extraGuid),
            "Static accessories schedule their secondary FBX without replacing the primary skinned model");
    }
    File.WriteAllText(staticMulti, File.ReadAllText(staticMulti).Replace("m_Name: Body\n  m_TagString: Untagged",
        "m_Name: Body\n  m_TagString: EditorOnly"));
    using (var package = UnityPackage.Open(staticMulti))
    {
        var avatar = VrchatAvatarParser.Parse(package);
        Check(avatar.AdditionalFbxs.Count == 0 && avatar.MeshCopies.All(c => c.IsSkinned) &&
              avatar.RendererMaterials.All(r => r.RendererGameObjectName != "Body"),
            "EditorOnly static renderer is excluded from source imports, mesh copies and material assignments");
    }
    File.WriteAllText(multiCopy, File.ReadAllText(multiCopy).Replace("m_Name: Body\n  m_TagString: Untagged",
        "m_Name: Body\n  m_TagString: EditorOnly"));
    using (var package = UnityPackage.Open(multiCopy))
    {
        var avatar = VrchatAvatarParser.Parse(package);
        Check(avatar.AdditionalFbxs.Count == 0 && avatar.MeshCopies.Count == 1,
            "EditorOnly renderer does not schedule an unused secondary FBX");
    }
    string movedCopy = Asset("Assets/MovedCopy.prefab", "99112233445566778899001122334455", """
--- !u!1001 &99
PrefabInstance:
  m_SourcePrefab: {fileID: 100100000, guid: 99000000000000000000000000000001}
  m_Modification:
    m_Modifications:
    - target: {fileID: 31, guid: 99000000000000000000000000000001}
      propertyPath: m_LocalPosition.x
      value: 7
    - target: {fileID: 31, guid: 99000000000000000000000000000001}
      propertyPath: m_LocalPosition.x
      value: 9
    - target: {fileID: 31, guid: 99000000000000000000000000000001}
      propertyPath: m_LocalScale.z
      value: 2
    - target: {fileID: 31, guid: 99000000000000000000000000000001}
      propertyPath: m_LocalRotation.w
      value: 0.5
    - target: {fileID: 33, guid: 99000000000000000000000000000001}
      propertyPath: m_LocalPosition.y
      value: 8
""");
    using (var package = UnityPackage.Open(movedCopy))
    {
        var avatar = ReadFilter(movedCopy);
        typeof(VrchatAvatarParser).GetMethod("ParseVariantRendererOverrides", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object[] { package, package.InputPrefab.Guid, avatar });
        Check(avatar.MeshCopies.Single().Transform.LocalPosition.X == 9, "Variant overrides copied renderer position");
        var copy = avatar.MeshCopies.Single();
        Check(copy.Transform.LocalPosition.Y == 2 && copy.Transform.LocalScale.Z == 2 &&
              copy.Transform.LocalRotation.W == 0.5f && copy.ParentTransforms.Single().LocalPosition.Y == 8,
            "Variant overrides rotation, scale and authored parents without changing unrelated axes");
    }
    string originalCopy = File.ReadAllText(copyPrefab);
    foreach (bool initial in new[] { false, true })
    {
        int initialValue = initial ? 1 : 0;
        int overrideValue = initial ? 0 : 1;
        File.WriteAllText(copyPrefab, originalCopy
            .Replace("m_Name: Body_Base_pants", $"m_Name: Body_Base_pants\n  m_IsActive: {initialValue}")
            .Replace("m_Name: CopyParent\n  m_IsActive: 0", $"m_Name: CopyParent\n  m_IsActive: {initialValue}")
            .Replace("SkinnedMeshRenderer:", $"SkinnedMeshRenderer:\n  m_Enabled: {initialValue}")
            + "\n--- !u!114 &45\nMonoBehaviour:\n  m_GameObject: {fileID: 1}\n  m_Enabled: 1\n");
        string stateCopy = Asset("Assets/StateCopy.prefab", "99112233445566778899001122334466", $$"""
--- !u!1001 &99
PrefabInstance:
  m_SourcePrefab: {fileID: 100100000, guid: 99000000000000000000000000000001}
  m_Modification:
    m_Modifications:
    - target: {fileID: 1, guid: 99000000000000000000000000000001}
      propertyPath: m_IsActive
      value: {{overrideValue}}
    - target: {fileID: 2, guid: 99000000000000000000000000000001}
      propertyPath: m_Enabled
      value: {{overrideValue}}
    - target: {fileID: 45, guid: 99000000000000000000000000000001}
      propertyPath: m_Enabled
      value: {{initialValue}}
    - target: {fileID: 32, guid: 99000000000000000000000000000001}
      propertyPath: m_IsActive
      value: {{overrideValue}}
""");
        using var package = UnityPackage.Open(stateCopy);
        var avatar = ReadFilter(stateCopy);
        typeof(VrchatAvatarParser).GetMethod("ParseVariantRendererOverrides", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { package, package.InputPrefab.Guid, avatar });
        var copy = avatar.MeshCopies.Single();
        Check(copy.Active == !initial, $"Variant overrides copied GameObject active state {initial} -> {!initial}");
        Check(copy.Enabled == !initial, $"Variant overrides copied renderer enabled state {initial} -> {!initial}");
        Check(copy.ParentTransforms.Single().Active == !initial,
            $"Variant overrides authored parent active state {initial} -> {!initial}");
        Check(copy.Transform.LocalPosition.X == 1 && copy.ParentTransforms.Single().Name == "CopyParent",
            "State overrides preserve copied mesh placement");
    }
    File.WriteAllText(copyPrefab, originalCopy);
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
        --- !u!1 &50
        GameObject:
          m_Name: ClothingRoot
          m_TagString: Untagged
        --- !u!4 &51
        Transform:
          m_GameObject: {fileID: 50}
          m_Father: {fileID: 0}
        """);
    VrchatAvatar excluded = ReadFilter(parentPath);
    Check(!excluded.ShouldKeepRenderer(bodyModel, "Body"), "EditorOnly parent excludes an Untagged child");
    Check(!excluded.ShouldKeepRenderer(clothingModel, "Body"), "EditorOnly parent excludes nested prefab meshes");
    Check(excluded.EditorOnlyFbxGuids.SetEquals(new[] { bodyModel, clothingModel }),
        "Entire EditorOnly models are omitted before importing their armatures");
    Check(excluded.EditorOnlyPrefabObjects[parentGuid].IsSupersetOf(new long[] { 1, 2 }),
        "EditorOnly subtree includes parent and child objects, not only renderers");
    Check(!excluded.EditorOnlyPrefabObjects[parentGuid].Contains(50),
        "Same-named Untagged helper neither clears EditorOnly nor becomes excluded");

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

    Asset("Library/PackageCache/com.example.materials@old/Surface.mat", materialGuid, "OLD");
    File.WriteAllText(lockFile, """{"dependencies":{"com.example.materials":{"version":"123","source":"registry"}}}""");
    using (var package = UnityPackage.Open(selected))
        Check(package.ByGuid(materialGuid).DiskPath.Contains("@123"), "Lock file selects active version over stale cache");
    File.WriteAllText(lockFile, """{"dependencies":{"com.example.materials":{"version":"missing","source":"registry"}}}""");
    ExpectInvalid(() => UnityPackage.Open(selected));
    File.WriteAllText(lockFile, """{"dependencies":{}}""");
    using (var package = UnityPackage.Open(selected))
        Check(package.ByGuid(materialGuid) == null, "Unused cached packages are ignored");
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

static void CheckNestedComponents(Func<string, string, string, string> asset, string regularCopy)
{
    const string leafGuid = "17171717171717171717171717171717";
    const string wrapperGuid = "18181818181818181818181818181818";
    const string excludedGuid = "19191919191919191919191919191919";
    const string outsideGuid = "20202020202020202020202020202020";
    string leaf = """
--- !u!1 &1
GameObject:
  m_Name: HairRoot
--- !u!4 &2
Transform:
  m_GameObject: {fileID: 1}
  m_Father: {fileID: 0}
--- !u!114 &3
MonoBehaviour:
  m_GameObject: {fileID: 1}
  m_Script: {fileID: 1661641543, guid: 2a2c05204084d904aa4945ccff20d8e5}
  rootTransform: {fileID: 2}
  pull: 0.7
  colliders:
  - {fileID: 8}
--- !u!1 &7
GameObject:
  m_Name: Collider
--- !u!4 &9
Transform:
  m_GameObject: {fileID: 7}
  m_Father: {fileID: 2}
  m_LocalPosition: {x: 1, y: 0, z: 0}
  m_LocalScale: {x: 1, y: 1, z: 1}
--- !u!114 &8
MonoBehaviour:
  m_GameObject: {fileID: 7}
  rootTransform: {fileID: 11}
  radius: 0.1
--- !u!1 &10
GameObject:
  m_Name: OverrideBone
--- !u!4 &11
Transform:
  m_GameObject: {fileID: 10}
  m_Father: {fileID: 2}
--- !u!114 &12
MonoBehaviour:
  m_GameObject: {fileID: 1}
  m_Script: {fileID: 11500000, guid: 42581d8044b64899834d3d515ab3a144}
  subPath: Head
""";
    asset("Assets/PhysicsLeaf.prefab", leafGuid, leaf);
    asset("Assets/PhysicsExcluded.prefab", excludedGuid, leaf.Replace("HairRoot", "ExcludedRoot"));
    asset("Assets/PhysicsOutside.prefab", outsideGuid, leaf.Replace("HairRoot", "OutsideRoot"));
    string Instance(long id, string guid, long parent) => $"\n--- !u!1001 &{id}\nPrefabInstance:\n  m_SourcePrefab: {{fileID: 100100000, guid: {guid}}}\n  m_Modification:\n    m_TransformParent: {{fileID: {parent}}}\n";
    asset("Assets/PhysicsWrapper.prefab", wrapperGuid, Instance(20, leafGuid, 0));
    string rootText = File.ReadAllText(regularCopy).Replace("  - {fileID: 31}", "  - {fileID: 31}\n  - {fileID: 401}") + """

--- !u!114 &300
MonoBehaviour:
  m_GameObject: {fileID: 32}
  m_Script: {fileID: 1661641543, guid: 2a2c05204084d904aa4945ccff20d8e5}
  rootTransform: {fileID: 33}
--- !u!1 &400
GameObject:
  m_Name: EditorOnlyParent
  m_TagString: EditorOnly
--- !u!4 &401
Transform:
  m_GameObject: {fileID: 400}
  m_Father: {fileID: 33}
""" + Instance(100, wrapperGuid, 33) + Instance(500, excludedGuid, 401) + Instance(600, outsideGuid, 0);
    string input = asset("Assets/NestedPhysics.prefab", "21212121212121212121212121212121", rootText);
    VrchatAvatar Read()
    {
        using var package = UnityPackage.Open(input);
        return VrchatAvatarParser.Parse(package);
    }
    var parsed = Read();
    Check(parsed.PhysBones.Count == 2 && parsed.PhysBones.Single(p => p.RootBoneName == "HairRoot").Pull == 0.7f &&
          parsed.PhysBones.Count(p => p.RootBoneName == "CopyParent") == 1,
        "Regular prefab imports nested PhysBones and local PhysBones exactly once within the selected subtree");
    Check(parsed.ModularBoneProxies.Count == 1 && parsed.ModularBoneProxies[0].SourceName == "HairRoot",
        "Nested Modular Avatar operations are included while EditorOnly and unrelated prefab instances are excluded");
    string modifications = $$"""
    m_Modifications:
    - target: {fileID: 3, guid: {{leafGuid}}}
      propertyPath: rootTransform
      objectReference: {fileID: 11, guid: {{leafGuid}}}
    - target: {fileID: 8, guid: {{leafGuid}}}
      propertyPath: rootTransform
      objectReference: {fileID: 0}
""";
    File.WriteAllText(input, rootText.Replace("    m_TransformParent: {fileID: 33}\n",
        "    m_TransformParent: {fileID: 33}\n" + modifications + "\n"));
    parsed = Read();
    var physics = parsed.PhysBones.Single(p => p.RootBoneName == "OverrideBone");
    Check(physics.Colliders.Single().AttachBoneName == "HairRoot" && physics.Colliders.Single().Offset.X == 1,
        "Nested PhysBone root override honors external GUID and null collider override restores its local attachment");
    const string sceneGuid = "22222222222222222222222222222223";
    asset("Assets/PhysicsScene.unity", sceneGuid, leaf);
    using (var package = UnityPackage.Open(input))
    {
        var sceneAvatar = new VrchatAvatar();
        foreach (string method in new[] { "ParseVariantPhysBones", "ParseVariantModularAvatar" })
            typeof(VrchatAvatarParser).GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object?[] { package, sceneGuid, sceneAvatar, null });
        Check(sceneAvatar.PhysBones.Single().RootBoneName == "HairRoot" && sceneAvatar.ModularBoneProxies.Count == 1,
            "Shared component traversal preserves existing Unity scene source support");
    }
}

static void CheckCopiedBoneReferences(Func<string, string, string, string> asset, string modelGuid, string primaryGuid)
{
    const string prefabGuid = "12121212121212121212121212121212";
    string prefab = asset("Assets/BoneReferences.prefab", prefabGuid, """
--- !u!1 &1
GameObject:
  m_Name: Avatar
--- !u!4 &2
Transform:
  m_GameObject: {fileID: 1}
  m_Father: {fileID: 0}
--- !u!1 &3
GameObject:
  m_Name: Left
--- !u!4 &4
Transform:
  m_GameObject: {fileID: 3}
  m_Father: {fileID: 2}
--- !u!1 &5
GameObject:
  m_Name: Hips
--- !u!4 &6
Transform:
  m_GameObject: {fileID: 5}
  m_Father: {fileID: 4}
--- !u!1 &7
GameObject:
  m_Name: Right
--- !u!4 &8
Transform:
  m_GameObject: {fileID: 7}
  m_Father: {fileID: 2}
--- !u!1 &9
GameObject:
  m_Name: Hips
--- !u!4 &10
Transform:
  m_GameObject: {fileID: 9}
  m_Father: {fileID: 8}
""");
    var method = typeof(VrchatAvatarParser).GetMethod("ResolveCopiedBoneTarget",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
    VrchatBoneTarget? Resolve(UnityPackage package, string guid, long id) => (VrchatBoneTarget?)method.Invoke(null,
        new object[] { package, guid, id, primaryGuid, new Dictionary<string, UnityModelFileIdResolver>(), new HashSet<(string, long)>() });
    using (var package = UnityPackage.Open(prefab))
    {
        var left = Resolve(package, prefabGuid, 6)!;
        var right = Resolve(package, prefabGuid, 10)!;
        Check(left.FbxGuid == primaryGuid && right.FbxGuid == primaryGuid && left.Path == "Left/Hips" && right.Path == "Right/Hips",
            "Local bone references preserve full paths and primary skeleton scope despite duplicate names");
        Check(right.PrefabGuid == prefabGuid && right.TransformFileId == 10,
            "Copied bone retains serialized prefab and transform identity");
        Check(Resolve(package, prefabGuid, 0)?.Name == null, "Explicit null copied bone remains null");
        Check(Resolve(package, prefabGuid, 9) == null, "A GameObject cannot masquerade as a bone transform");
    }
    const string variantGuid = "13131313131313131313131313131313";
    long modelId = (long)typeof(UnityModelFileIdResolver).GetMethod("Compute",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(null, new object[] { "Transform", "//RootNode/Right/Shared/Transform", 0 })!;
    string variant = asset("Assets/BoneVariant.prefab", variantGuid, $$"""
--- !u!1001 &100
PrefabInstance:
  m_SourcePrefab: {fileID: 100100000, guid: {{prefabGuid}}}
--- !u!4 &20 stripped
Transform:
  m_CorrespondingSourceObject: {fileID: 10, guid: {{prefabGuid}}}
--- !u!4 &21 stripped
Transform:
  m_CorrespondingSourceObject: {fileID: {{modelId}}, guid: {{modelGuid}}}
""");
    using (var package = UnityPackage.Open(variant))
    {
        Check(Resolve(package, variantGuid, 20)?.Path == "Right/Hips" &&
              Resolve(package, variantGuid, 10 ^ 100)?.Path == "Right/Hips",
            "Explicit and omitted stripped bone references retain the original transform path");
        var target = Resolve(package, variantGuid, 21)!;
        Check(target.FbxGuid == modelGuid && target.Path!.EndsWith("/Right/Shared"),
            "FBX bone aliases retain source model and full path for duplicate node names");
        var copy = new VrchatMeshCopy(modelGuid, "Body", "Copy", true, true);
        copy.SourceBoneNames.Add("SourceHips");
        var apply = typeof(VrchatAvatarParser).GetMethod("ApplyCopiedBoneOverride",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        void Override(long id, string? referenceGuid = null)
        {
            var modification = UnityYaml.ParseFlatDocument("propertyPath: m_Bones.Array.data[0]\nobjectReference: {fileID: " + id +
                (referenceGuid == null ? "" : ", guid: " + referenceGuid) + "}\n");
            apply.Invoke(null, new object[] { package, copy, variantGuid, modification, primaryGuid,
                new Dictionary<string, UnityModelFileIdResolver>() });
        }
        Override(20);
        Check(copy.BoneTargets["SourceHips"].Path == "Right/Hips" && copy.BoneTargets["SourceHips"].FbxGuid == primaryGuid,
            "Variant bone override resolves local objectReference in the overriding scene");
        Override(modelId, modelGuid);
        Check(copy.BoneTargets["SourceHips"].FbxGuid == modelGuid && copy.BoneTargets["SourceHips"].Path!.EndsWith("/Right/Shared"),
            "Variant bone override preserves explicit external model identity");
        Override(0);
        Check(copy.BoneTargets["SourceHips"].Name == null, "Variant bone override can explicitly clear a binding");
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
    path: Face/Body
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
    string anyStateDisabled = controller.Replace("m_DefaultBool: 0", "m_DefaultBool: 1")
        .Replace("m_DefaultState: {fileID: -31}", "m_DefaultState: {fileID: -33}\n  m_AnyStateTransitions:\n  - {fileID: -34}")
        + "\n--- !u!1101 &-34\nAnimatorStateTransition:\n  m_HasExitTime: 0\n  m_DstState: {fileID: -31}\n  m_Conditions:\n  - m_ConditionMode: 1\n    m_ConditionEvent: ForceDisable\n";
    string nestedAnyState = anyStateDisabled.Replace("m_DefaultState: {fileID: -33}\n  m_AnyStateTransitions:", "m_EntryTransitions:\n  - {fileID: -41}\n--- !u!1109 &-41\nAnimatorTransition:\n  m_Conditions: []\n  m_DstStateMachine: {fileID: -40}\n--- !u!1107 &-40\nAnimatorStateMachine:\n  m_DefaultState: {fileID: -33}\n  m_AnyStateTransitions:");
    Check(Read(nestedAnyState).Blink == null, "Active nested machine honors its Any State disable");
    string inactiveSibling = controller.Replace("m_DefaultState: {fileID: -31}",
        "m_DefaultState: {fileID: -31}\n  m_ChildStateMachines:\n  - m_StateMachine: {fileID: -50}")
        + "\n--- !u!1107 &-50\nAnimatorStateMachine:\n  m_AnyStateTransitions:\n  - {fileID: -51}\n"
        + "--- !u!1101 &-51\nAnimatorStateTransition:\n  m_Conditions: []\n  m_HasExitTime: 0\n  m_DstState: {fileID: -31}\n";
    Check(Read(inactiveSibling).Blink?.BlendShapeName == "blink", "Inactive sibling Any State cannot disable blink");
    Check(Read(anyStateDisabled).Blink == null, "Any State disable takes precedence over default blink state");
    Check(Read(controller).Blink?.BlendShapeName == "blink", "Startup parameter driver enables Body blink across layers");
    var blinkModel = VrchatModelAdapter.ToVrmModel(Read(controller));
    Check(blinkModel.MeshBindingPaths[blinkModel.Expressions.Single(e => e.Preset == "blink").Binds.Single().MeshIndex] == "Face/Body",
        "Animator blink retains its full renderer path through adaptation");
    Check(Read(controller.Replace("m_DefaultBool: 0", "m_DefaultBool: 1")).Blink == null,
        "Explicit blink disable is respected");
    Check(Read(controller.Replace("m_HasExitTime: 0", "m_HasExitTime: 1")).Blink == null,
        "Timed transitions are not treated as startup blink settings");
    string timedExit = controller.Replace("m_DefaultState: {fileID: -31}", "m_DefaultState: {fileID: -33}")
        .Replace("  m_Motion: {fileID: 7400000, guid: " + clipGuid + "}",
            "  m_Motion: {fileID: 7400000, guid: " + clipGuid + "}\n  m_Transitions:\n  - {fileID: -34}")
        + "\n--- !u!1101 &-34\nAnimatorStateTransition:\n  m_HasExitTime: 1\n  m_DstState: {fileID: -35}\n  m_Conditions: []\n"
        + "--- !u!1102 &-35\nAnimatorState:\n  m_Motion: {fileID: 0}\n";
    Check(Read(timedExit).Blink == null, "Timed exit from blink cannot become a permanent blink binding");
    Check(Read(timedExit.Replace("m_DstState: {fileID: -35}", "m_IsExit: 1")).Blink == null,
        "Timed exit from the state machine also makes blink indeterminate");
    string immediateExit = timedExit.Replace("m_DstState: {fileID: -35}", "m_IsExit: 1")
        .Replace("m_HasExitTime: 1", "m_HasExitTime: 0")
        .Replace("m_DefaultState: {fileID: -33}", "m_EntryTransitions:\n  - {fileID: -41}\n--- !u!1109 &-41\nAnimatorTransition:\n  m_Conditions: []\n  m_DstStateMachine: {fileID: -40}\n--- !u!1107 &-40\nAnimatorStateMachine:\n  m_DefaultState: {fileID: -33}");
    Check(Read(immediateExit).Blink == null, "Unconditional nested state machine Exit cannot become permanent blink");
    Check(Read(timedExit.Replace("m_HasExitTime: 1", "m_HasExitTime: 1\n  m_Mute: 1")).Blink?.BlendShapeName == "blink",
        "Muted timed exit does not suppress a stable blink binding");
    Check(Read(timedExit.Replace("  m_Conditions: []", "  m_Conditions:\n  - m_ConditionMode: 1\n    m_ConditionEvent: ForceDisable")).Blink?.BlendShapeName == "blink",
        "Timed exit with an unmet condition does not suppress a stable blink binding");
    Check(Read(controller.Replace("    value: 1", "    value: 0")).Blink == null,
        "Inactive blink animation is not imported as an always-on driver");
    string soloGraph = $$"""
--- !u!91 &91
AnimatorController:
  m_AnimatorParameters:
  - m_Name: Disabled
    m_Type: 4
    m_DefaultBool: 0
  m_AnimatorLayers:
  - m_StateMachine: {fileID: 10}
--- !u!1107 &10
AnimatorStateMachine:
  m_DefaultState: {fileID: 11}
--- !u!1102 &11
AnimatorState:
  m_Transitions:
  - {fileID: 20}
  - {fileID: 21}
--- !u!1101 &20
AnimatorStateTransition:
  m_DstState: {fileID: 12}
  m_Conditions: []
--- !u!1101 &21
AnimatorStateTransition:
  m_Solo: 1
  m_DstState: {fileID: 13}
  m_Conditions: []
--- !u!1102 &12
AnimatorState:
  m_Motion: {fileID: 7400000, guid: {{clipGuid}}}
--- !u!1102 &13
AnimatorState:
  m_Motion: {fileID: 0}
""";
    const string refs = "  m_Transitions:\n  - {fileID: 20}\n  - {fileID: 21}";
    foreach (string kind in new[] { "State", "Entry", "AnyState" })
    {
        string graph = kind == "State" ? soloGraph : soloGraph.Replace(refs, "")
            .Replace("  m_DefaultState: {fileID: 11}", "  m_DefaultState: {fileID: 11}\n" +
                refs.Replace("m_Transitions", kind == "Entry" ? "m_EntryTransitions" : "m_AnyStateTransitions"));
        Check(Read(graph).Blink == null, kind + " Solo transition suppresses an earlier non-solo blink transition");
        if (kind != "AnyState") // Any State is reevaluated after entry and can then take its other transition.
            Check(Read(graph.Replace("  m_DstState: {fileID: 12}", "  m_Solo: 1\n  m_DstState: {fileID: 12}")).Blink?.BlendShapeName == "blink",
                kind + " multiple Solo transitions preserve their serialized priority");
        Check(Read(graph.Replace("m_Solo: 1", "m_Solo: 1\n  m_Mute: 1")).Blink?.BlendShapeName == "blink",
            kind + " muted Solo transition does not suppress the non-solo blink transition");
        Check(Read(graph.Replace("m_Solo: 1", "m_Solo: 1\n  m_Mute: 0")
            .Replace("m_DstState: {fileID: 13}\n  m_Conditions: []",
                "m_DstState: {fileID: 13}\n  m_Conditions:\n  - m_ConditionEvent: Disabled\n    m_ConditionMode: 1")).Blink == null,
            kind + " Solo suppression applies even when the solo condition is false");
    }
    string independent = soloGraph.Replace("  - {fileID: 21}", "")
        .Replace("  m_DefaultState: {fileID: 11}", "  m_DefaultState: {fileID: 11}\n  m_AnyStateTransitions:\n  - {fileID: 21}")
        .Replace("m_DstState: {fileID: 13}\n  m_Conditions: []",
            "m_DstState: {fileID: 13}\n  m_Conditions:\n  - m_ConditionEvent: Disabled\n    m_ConditionMode: 1");
    Check(Read(independent).Blink?.BlendShapeName == "blink", "Solo Any State transition does not suppress a separate state transition list");
    asset("Assets/Blink.anim", clipGuid, """
--- !u!74 &7400000
AnimationClip:
  m_AnimationClipSettings:
    m_LoopTime: 1
  m_FloatCurves:
  - curve:
      m_Curve:
      - value: 0
      - value: 25
    attribute: blendShape.blink
    path: Face/Body
    classID: 137
""");
    Check(Read(controller).Blink == null, "Partial-weight Animator blink cannot become a full-weight blink driver");
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
