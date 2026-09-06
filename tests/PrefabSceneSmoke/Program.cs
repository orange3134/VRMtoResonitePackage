using System.Reflection;
using System.Runtime.CompilerServices;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.Store;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

if (args.Length != 2) throw new ArgumentException("Usage: PrefabSceneSmoke <FBX path> <skinned renderer with blendshapes>");
string fbxPath = Path.GetFullPath(args[0]);
string resonite = Environment.GetEnvironmentVariable("RESONITE_PATH")
    ?? @"C:\Program Files (x86)\Steam\steamapps\common\Resonite";
typeof(VrchatAvatar).Assembly.GetType("VrmToResonitePackage.ResoniteLocator")!
    .GetMethod("InstallAssemblyResolver")!.Invoke(null, new object[] { resonite });
Environment.CurrentDirectory = resonite;
try
{
    await Run(fbxPath, args[1]);
    Console.WriteLine("Prefab scene smoke checks passed.");
    // This is a standalone test process; do not run the engine's asynchronous shutdown
    // callbacks after disposing its services (headless shutdown can throw on background threads).
    Environment.Exit(0);
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.Exit(1);
}

[MethodImpl(MethodImplOptions.NoInlining)]
static async Task Run(string fbxPath, string rendererName)
{
    string temp = Path.Combine(Path.GetTempPath(), "ResoPonSceneSmoke", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(temp, "Assets"));
    Directory.CreateDirectory(Path.Combine(temp, "ProjectSettings"));
    const string materialGuid = "11223344556677889900112233445566";
    File.WriteAllText(Path.Combine(temp, "Assets/Test.mat"), "--- !u!21 &2100000\nMaterial:\n  m_Name: ReviewMaterial\n  m_SavedProperties:\n    m_Floats: []\n    m_Colors: []\n    m_TexEnvs: []\n");
    File.WriteAllText(Path.Combine(temp, "Assets/Test.mat.meta"), "guid: " + materialGuid);
    const string defaultMaterialGuid = "22334455667788990011223344556677";
    File.WriteAllText(Path.Combine(temp, "Assets/Default.mat"),
        File.ReadAllText(Path.Combine(temp, "Assets/Test.mat")).Replace("ReviewMaterial", "ReviewDefault"));
    File.WriteAllText(Path.Combine(temp, "Assets/Default.mat.meta"), "guid: " + defaultMaterialGuid);
    string prefab = Path.Combine(temp, "Assets/Test.prefab");
    File.WriteAllText(prefab, "%YAML 1.1\n");
    File.WriteAllText(prefab + ".meta", "guid: aabbccddeeff00112233445566778899");
    using var package = UnityPackage.Open(prefab);
    var runner = new StandaloneFrooxEngineRunner();
    await runner.Initialize(new LaunchOptions
    {
        DataDirectory = Path.Combine(temp, "Data"), CacheDirectory = Path.Combine(temp, "Cache"),
        LogsDirectory = Path.Combine(temp, "Logs"), DoNotAutoLoadHome = true,
        StartInvisible = true, NeverSaveSettings = true, NeverSaveDash = true, DisablePlatformInterfaces = true,
    });
    var world = await Userspace.OpenWorld(new WorldStartSettings { AutoFocus = true, CreateLoadIndicator = false, InitWorld = delegate { } });
    await world.Coroutines.StartTask(async () =>
    {
        await default(ToWorld);
        Slot root = world.AddSlot("Test avatar"), assets = root.AddSlot("Assets");
        Slot primary = root.AddSlot("Primary"), additional = root.AddSlot("Additional");
        Slot branches = root.AddSlot("Branches");
        Slot left = branches.AddSlot("Left").AddSlot("Shared");
        Slot right = branches.AddSlot("Right").AddSlot("Shared");
        left.AddSlot("Body");
        Slot survivingBody = right.AddSlot("Body");
        var branchRoots = new Dictionary<string, Slot> { ["branches"] = branches };
        var branchSources = (Dictionary<Slot, string>)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup",
            "CaptureImportedObjects", branchRoots);
        var branchPaths = (Dictionary<Slot, string>)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup",
            "CaptureImportedPaths", branchRoots);
        var excludedBranch = new VrchatAvatar();
        excludedBranch.EditorOnlyModelPaths["branches"] = new HashSet<string>
            { "RootNode/Left/Shared", "RootNode/Left/Shared/Body" };
        left.SetParent(primary, false);
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "RemoveEditorOnlyObjects",
            excludedBranch, branchSources, branchPaths);
        Check(left.IsDestroyed && !right.IsDestroyed && !survivingBody.IsDestroyed,
            "EditorOnly removal follows captured paths after reparenting and preserves the other same-named branch");
        var settings = ModelImportSettings.XiexeToon(false, true, false);
        settings.SetupIK = false;
        settings.ForceTpose = false;
        await ModelImporter.ImportModelAsync(fbxPath, additional, settings, assets);
        await default(ToWorld);
        await (Task)Call("VrmToResonitePackage.Converter", "WaitForAssets", assets);
        var avatar = new VrchatAvatar { FbxGuid = "primary" };
        var model = new VrchatFbxAsset { Guid = "additional", InstanceName = additional.Name };
        avatar.AdditionalFbxs.Add(model);
        var sources = (Dictionary<Slot, string>)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CaptureImportedObjects",
            new Dictionary<string, Slot> { ["primary"] = primary, ["additional"] = additional });
        var original = additional.GetComponentsInChildren<SkinnedMeshRenderer>().Single(r => r.Slot.Name == rendererName);
        Check(original.MeshBlendshapeCount > 0, "Fixture has morph data");
        foreach (var material in original.Materials)
        {
            string name = ((Component)material).Slot.Name;
            model.MaterialGuids[name.StartsWith("Material: ") ? name[10..] : name] = defaultMaterialGuid;
        }
        foreach (string name in new[] { "ExplicitCopy", "DefaultCopy" })
            avatar.MeshCopies.Add(new VrchatMeshCopy("additional", rendererName, name, true, true)
                { Transform = new VrchatPrefabTransform() });
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CreateMeshCopies", avatar, sources,
            (Func<VrchatMeshCopy, Slot>)(_ => primary));
        var copy = primary.FindChild("ExplicitCopy").GetComponent<SkinnedMeshRenderer>();
        var defaultCopy = primary.FindChild("DefaultCopy").GetComponent<SkinnedMeshRenderer>();
        // A primary-model renderer with the same name must not receive the additional model's overrides.
        var unrelated = primary.AddSlot("ExplicitCopy").AttachComponent<SkinnedMeshRenderer>();
        sources[unrelated.Slot] = "primary";
        var rm = new VrchatRendererMaterials { FbxGuid = "additional", RendererGameObjectName = "ExplicitCopy" };
        rm.MaterialGuids.Add(materialGuid);
        rm.InitialBlendShapes.Add((0, 37f));
        avatar.RendererMaterials.Add(rm);
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "ApplyInitialBlendShapes", root, avatar, sources);
        Check(Math.Abs(copy.BlendShapeWeights[0] - 0.37f) < 0.001f, "Reparented copy receives initial morph override");
        await (Task)Call("VrmToResonitePackage.Vrchat.VrchatMaterialBuilder", "Apply", root, assets, avatar, package, sources);
        Check(((Component)copy.Materials[0]).Slot.Name == "Material: ReviewMaterial", "Reparented copy receives explicit material");
        Check(((Component)defaultCopy.Materials[0]).Slot.Name == "Material: ReviewDefault", "Reparented copy uses source model's default material mapping");
        Check(unrelated.Materials.Count == 0, "Same-named primary renderer remains untouched");
        avatar.InactiveGameObjects.Add(new VrchatGameObjectReference("additional", "ExplicitCopy"));
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "Apply", root, avatar, sources);
        Check(!copy.Slot.ActiveSelf && unrelated.Slot.ActiveSelf, "Inactive override retains model identity after reparenting");
    });
}

static object Call(string type, string method, params object[] args) => typeof(VrchatAvatar).Assembly
    .GetType(type)!.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, args)!;
static void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    Console.WriteLine("PASS: " + label);
}
