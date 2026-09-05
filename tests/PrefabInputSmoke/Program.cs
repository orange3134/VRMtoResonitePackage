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
    string selectedGuid = new('a', 32);
    string otherGuid = new('b', 32);
    string materialGuid = new('c', 32);
    string selected = Asset("Assets/Selected.prefab", selectedGuid, Avatar("Selected"));
    Asset("Assets/Other.prefab", otherGuid, Avatar("Other"));
    Asset("Library/PackageCache/com.example.materials@123/Surface.mat", materialGuid, "Material:\n  m_Name: Surface\n");
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
