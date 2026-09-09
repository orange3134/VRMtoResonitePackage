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
        Check(original.Bones.Count > 0 && original.Bones[0] != null, "Fixture has a source skeleton");
        Slot primaryLeft = primary.AddSlot("Left").AddSlot("Hips");
        Slot primaryRight = primary.AddSlot("Right").AddSlot("Hips");
        Slot clothingRight = additional.AddSlot("Right").AddSlot("Hips");
        var boneRoots = new Dictionary<string, Slot> { ["primary"] = primary, ["additional"] = additional };
        var boneSources = (Dictionary<Slot, string>)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CaptureImportedObjects", boneRoots);
        var bonePaths = (Dictionary<Slot, string>)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CaptureImportedPaths", boneRoots);
        primaryRight.SetParent(primaryLeft, false);
        var reboundAvatar = new VrchatAvatar { FbxGuid = "primary" };
        var rebound = new VrchatMeshCopy("additional", rendererName, "ReboundCopy", true, true)
            { Transform = new VrchatPrefabTransform() };
        rebound.BoneTargets[0] = new VrchatBoneTarget("primary", "Hips", "Right/Hips", "prefab", 10);
        reboundAvatar.MeshCopies.Add(rebound);
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CreateMeshCopies", reboundAvatar, boneSources,
            (Func<VrchatMeshCopy, Slot>)(_ => primary), bonePaths);
        Check(primary.FindChild("ReboundCopy").GetComponent<SkinnedMeshRenderer>().Bones[0] == primaryRight,
            "Copied bone selects the exact primary branch despite same-named clothing bones and reparenting");
        var modelRebound = rebound with { Name = "ModelReboundCopy" };
        modelRebound.BoneTargets[0] = new VrchatBoneTarget("additional", "Hips", "RootNode/Right/Hips");
        reboundAvatar.MeshCopies.Clear();
        reboundAvatar.MeshCopies.Add(modelRebound);
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CreateMeshCopies", reboundAvatar, boneSources,
            (Func<VrchatMeshCopy, Slot>)(_ => primary), bonePaths);
        Check(primary.FindChild("ModelReboundCopy").GetComponent<SkinnedMeshRenderer>().Bones[0] == clothingRight,
            "Explicit FBX bone references retain their additional model scope and normalize the synthetic root");
        Slot indexedSource = additional.AddSlot("IndexedSource");
        var indexedRenderer = indexedSource.AttachComponent<SkinnedMeshRenderer>();
        indexedRenderer.Bones.Add(primaryLeft);
        indexedRenderer.Bones.Add(primaryRight);
        indexedRenderer.Bones.Add(null);
        var indexedSources = new Dictionary<Slot, string>(boneSources) { [indexedSource] = "additional" };
        var indexedCopy = new VrchatMeshCopy("additional", "IndexedSource", "IndexedCopy", true, true)
            { Transform = new VrchatPrefabTransform() };
        indexedCopy.BoneTargets[0] = new VrchatBoneTarget("primary", "Hips", "Right/Hips");
        indexedCopy.BoneTargets[1] = new VrchatBoneTarget("primary", "Hips", "Left/Hips");
        indexedCopy.BoneTargets[2] = new VrchatBoneTarget("additional", "Hips", "Right/Hips");
        reboundAvatar.MeshCopies.Clear();
        reboundAvatar.MeshCopies.Add(indexedCopy);
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CreateMeshCopies", reboundAvatar, indexedSources,
            (Func<VrchatMeshCopy, Slot>)(_ => primary), bonePaths);
        var indexedResult = primary.FindChild("IndexedCopy").GetComponent<SkinnedMeshRenderer>();
        Check(indexedResult.Bones[0] == primaryRight && indexedResult.Bones[1] == primaryLeft &&
              indexedResult.Bones[2] == clothingRight,
            "Bone overrides retain separate indices for identical source names and restore null source bindings");
        indexedResult.Slot.Destroy();
        indexedSource.Destroy();
        foreach (var material in original.Materials)
        {
            string name = ((Component)material).Slot.Name;
            model.MaterialGuids[name.StartsWith("Material: ") ? name[10..] : name] = defaultMaterialGuid;
        }
        foreach (string name in new[] { "ExplicitCopy", "DefaultCopy" })
            avatar.MeshCopies.Add(new VrchatMeshCopy("additional", rendererName, name, true, true)
                { Transform = new VrchatPrefabTransform() });
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CreateMeshCopies", avatar, sources,
            (Func<VrchatMeshCopy, Slot>)(_ => primary), new Dictionary<Slot, string>());
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
        Slot staticSource = additional.AddSlot("StaticSource");
        var staticRenderer = staticSource.AttachComponent<MeshRenderer>();
        staticRenderer.Mesh.Target = original.Mesh.Target;
        var staticMesh = staticRenderer.Mesh.Target;
        foreach (var material in original.Materials) staticRenderer.Materials.Add().Target = material;
        sources[staticSource] = "additional";
        var staticAvatar = new VrchatAvatar { FbxGuid = "primary" };
        staticAvatar.AdditionalFbxs.Add(model);
        staticAvatar.MeshCopies.Add(new VrchatMeshCopy("additional", "StaticSource", "StaticCopy", false, false)
        {
            IsSkinned = false, ReplaceSourceRenderer = true,
            Transform = new VrchatPrefabTransform { LocalPosition = new System.Numerics.Vector3(3, 4, 5) },
        });
        staticAvatar.MeshCopies.Add(new VrchatMeshCopy("additional", rendererName, "StaticFromSkin", true, true)
            { IsSkinned = false, Transform = new VrchatPrefabTransform() });
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CreateMeshCopies", staticAvatar, sources,
            (Func<VrchatMeshCopy, Slot>)(_ => primary), new Dictionary<Slot, string>());
        var staticCopy = primary.FindChild("StaticCopy").GetComponent<MeshRenderer>();
        Check(staticCopy != null && staticCopy is not SkinnedMeshRenderer && !staticCopy.Enabled &&
              !staticCopy.Slot.ActiveSelf && staticCopy.Slot.LocalPosition.x == 3 && staticCopy.Mesh.Target == staticMesh,
            "Static mesh copy retains mesh, authored placement, active state and enabled state");
        Check(staticSource.GetComponent<MeshRenderer>() == null && !staticSource.IsDestroyed,
            "Regular static prefab removes the imported template renderer while keeping its slot");
        Check(primary.FindChild("StaticFromSkin").GetComponent<MeshRenderer>() is not SkinnedMeshRenderer,
            "A skinned FBX mesh used by a static prefab becomes a static renderer");
        var staticMaterials = new VrchatRendererMaterials { FbxGuid = "additional", RendererGameObjectName = "StaticCopy" };
        staticMaterials.MaterialGuids.Add(materialGuid);
        staticAvatar.RendererMaterials.Add(staticMaterials);
        await (Task)Call("VrmToResonitePackage.Vrchat.VrchatMaterialBuilder", "Apply", root, assets, staticAvatar, package, sources);
        Check(((Component)staticCopy.Materials[0]).Slot.Name == "Material: ReviewMaterial",
            "Reparented static mesh copy receives its prefab material assignment");
        avatar.InactiveGameObjects.Add(new VrchatGameObjectReference("additional", "ExplicitCopy"));
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "Apply", root, avatar, sources);
        Check(!copy.Slot.ActiveSelf && unrelated.Slot.ActiveSelf, "Inactive override retains model identity after reparenting");
        var sameNameAvatar = new VrchatAvatar();
        Slot parentA = primary.AddSlot("Copy parent A"), parentB = primary.AddSlot("Copy parent B");
        for (int i = 0; i < 2; i++)
        {
            var authored = new VrchatMeshCopy("additional", rendererName, rendererName, true, true)
            {
                PrefabGuid = "regular", RendererFileId = i + 1, ReplaceSourceRenderer = true,
                Transform = new VrchatPrefabTransform { LocalPosition = new System.Numerics.Vector3(i + 1, 2, 3) },
            };
            if (original.Bones.Count > 0 && original.Bones[0] != null)
                authored.BoneTargets[0] = new VrchatBoneTarget(null, null);
            sameNameAvatar.MeshCopies.Add(authored);
        }
        Slot originalSlot = original.Slot;
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CreateMeshCopies", sameNameAvatar, sources,
            (Func<VrchatMeshCopy, Slot>)(c => c.RendererFileId == 1 ? parentA : parentB), new Dictionary<Slot, string>());
        var sameA = parentA.FindChild(rendererName).GetComponent<SkinnedMeshRenderer>();
        var sameB = parentB.FindChild(rendererName).GetComponent<SkinnedMeshRenderer>();
        Check(sameA != null && sameB != null && sameA.Slot.LocalPosition.x == 1 && sameB.Slot.LocalPosition.x == 2,
            "Two same-named authored renderers retain separate parents and transforms");
        var faceAvatar = new VrchatAvatar();
        faceAvatar.Visemes.Add(new VrchatViseme { ResonitePreset = "aa", MeshGameObjectName = rendererName,
            MeshGameObjectPath = "Copy parent B/" + rendererName, BlendShapeName = sameB.BlendShapeName(0) });
        var faceModel = VrchatModelAdapter.ToVrmModel(faceAvatar);
        var resolverType = typeof(VrchatAvatar).Assembly.GetType("VrmToResonitePackage.BlendshapeResolver")!;
        var resolver = Activator.CreateInstance(resolverType, root, faceModel)!;
        var bind = faceModel.Expressions.Single().Binds.Single();
        object ResolveFace() => resolverType.GetMethod("Resolve")!.Invoke(resolver, new object[] { bind })!;
        Check(ReferenceEquals(ResolveFace(), sameB.BlendShapeWeights.GetElement(0)),
            "Animator binding selects the second same-named renderer using every hierarchy segment");
        string shapeName = faceModel.MeshTargetNames[bind.MeshIndex][bind.MorphIndex];
        faceModel.MeshTargetNames[bind.MeshIndex][bind.MorphIndex] = "MissingShape";
        Check(ResolveFace() == null, "Missing Animator shape cannot use a synthetic index as an FBX shape index");
        faceModel.MeshTargetNames[bind.MeshIndex][bind.MorphIndex] = shapeName;
        faceModel.MeshBindingPaths[bind.MeshIndex] = "Missing/" + rendererName;
        Check(ResolveFace() == null, "Missing Animator path cannot fall back to a namesake renderer");
        faceModel.MeshBindingPaths[bind.MeshIndex] = rendererName;
        Check(ResolveFace() == null, "Ambiguous Animator path cannot select the first matching renderer");
        faceModel.MeshBindingPaths[bind.MeshIndex] = "";
        Check(ResolveFace() == null, "Root Animator path cannot fall back to a child renderer");
        var rootResolver = Activator.CreateInstance(resolverType, sameB.Slot, faceModel)!;
        Check(ReferenceEquals(resolverType.GetMethod("Resolve")!.Invoke(rootResolver, new object[] { bind }),
                sameB.BlendShapeWeights.GetElement(0)),
            "Empty Animator path resolves the renderer on the root itself");
        Check(sameA.Bones.Count > 0 && sameA.Bones[0] == null && sameB.Bones[0] == null,
            "Same-named renderer copies apply authored bone overrides");
        Check(originalSlot.GetComponent<SkinnedMeshRenderer>() == null && !originalSlot.IsDestroyed,
            "Regular prefab replaces the imported renderer while retaining its referenced slot");
        Slot modelRoot = root.AddSlot("Duplicate source model");
        Slot leftSource = sameA.Slot.Duplicate(modelRoot.AddSlot("Left"));
        Slot rightSource = sameB.Slot.Duplicate(modelRoot.AddSlot("Right"));
        leftSource.GetComponent<SkinnedMeshRenderer>().BlendShapeWeights[0] = 0.12f;
        rightSource.GetComponent<SkinnedMeshRenderer>().BlendShapeWeights[0] = 0.89f;
        var pathSources = new Dictionary<Slot, string> { [leftSource] = "model", [rightSource] = "model" };
        var sourcePaths = (Dictionary<Slot, string>)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CaptureImportedPaths",
            new Dictionary<string, Slot> { ["model"] = modelRoot });
        rightSource.SetParent(primary, false);
        var pathAvatar = new VrchatAvatar();
        pathAvatar.MeshCopies.Add(new VrchatMeshCopy("model", rendererName, "RightCopy", true, true)
            { SourcePath = "RootNode/Right/" + rendererName, Transform = new VrchatPrefabTransform() });
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CreateMeshCopies", pathAvatar, pathSources,
            (Func<VrchatMeshCopy, Slot>)(_ => primary), sourcePaths);
        Check(Math.Abs(primary.FindChild("RightCopy").GetComponent<SkinnedMeshRenderer>().BlendShapeWeights[0] - 0.89f) < 0.001f,
            "Captured source path selects the second same-named renderer after reparenting");
    });
}

static object Call(string type, string method, params object[] args) => typeof(VrchatAvatar).Assembly
    .GetType(type)!.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, args)!;
static void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    Console.WriteLine("PASS: " + label);
}
