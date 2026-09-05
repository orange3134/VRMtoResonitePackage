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
