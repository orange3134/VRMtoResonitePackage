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
        {
            var exportRoot = root.AddSlot("Descriptor placement regression");
            var body = exportRoot.AddSlot("Body");
            var placementBodyArmature = body.AddSlot("Armature");
            var bodyBone = placementBodyArmature.AddSlot("Hips");
            var clothing = exportRoot.AddSlot("Clothing");
            var clothingArmature = clothing.AddSlot("Armature");
            var clothingBone = clothingArmature.AddSlot("Hips");
            var skin = clothing.AddSlot("Skin").AttachComponent<SkinnedMeshRenderer>();
            skin.Bones.Add(clothingBone);
            var descriptor = new VrchatPrefabTransform
                { Key = "wrapper:2", GameObjectKey = "wrapper:1", Name = "Descriptor" };
            var placementAvatar = new VrchatAvatar { FbxGuid = "body", DescriptorRootKey = descriptor.GameObjectKey,
                DescriptorRootTarget = new VrchatBoneTarget(null, "Descriptor", "", "wrapper", 2) };
            placementAvatar.FbxParentTransforms.Add(descriptor);
            var placementAdditional = new VrchatFbxAsset { Guid = "clothing" };
            placementAdditional.ParentTransforms.Add(descriptor);
            placementAvatar.AdditionalFbxs.Add(placementAdditional);
            placementAvatar.ModularMergeArmatures.Add(new VrchatModularMergeArmature
            {
                SourceName = "Armature", SourceBoneTarget = new VrchatBoneTarget("clothing", "Armature", "Armature"),
                TargetPath = "Body/Armature"
            });
            var roots = new Dictionary<string, Slot> { ["body"] = body, ["clothing"] = clothing };
            var placementSources = (Dictionary<Slot, string>)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CaptureImportedObjects", roots);
            var paths = (Dictionary<Slot, string>)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CaptureImportedPaths", roots);
            object[] placementHierarchyArgs = { exportRoot, placementAvatar, roots, placementSources, paths, null, null, null };
            var descriptorRoot = (Slot)typeof(VrchatAvatar).Assembly.GetType("VrmToResonitePackage.Converter")!
                .GetMethod("ApplyVrchatPrefabHierarchy", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, placementHierarchyArgs);
            var slots = (Dictionary<string, Slot>)placementHierarchyArgs[6];
            Func<VrchatBoneTarget, Slot> resolve = target => (Slot)Call(
                "VrmToResonitePackage.Vrchat.VrchatSceneSetup", "ResolveImportedTarget", target, placementSources, paths, slots);
            Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "ApplyModularAvatar", exportRoot, placementAvatar,
                new Dictionary<int, Slot>(), resolve, descriptorRoot ?? exportRoot);
            Check(skin.Bones[0] == bodyBone && clothingArmature.IsDestroyed,
                "Descriptor-relative Merge Armature connects intact FBX instances without authored mesh copies");
            Check(descriptorRoot == slots[descriptor.Key] && descriptorRoot.Parent == exportRoot,
                "FBX placement returns the authored descriptor wrapper as the binding root");
            exportRoot.Destroy();
        }
        {
            Slot rigRoot = root.AddSlot("Shared rig regression");
            Slot importedRig = rigRoot.AddSlot("Imported");
            Slot rigHips = importedRig.AddSlot("Hips");
            Slot rigBody = importedRig.AddSlot("Body");
            rigBody.AttachComponent<SkinnedMeshRenderer>().Bones.Add(rigHips);
            var rigRoots = new Dictionary<string, Slot> { ["rig"] = importedRig };
            var rigSources = (Dictionary<Slot, string>)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CaptureImportedObjects", rigRoots);
            var rigPaths = (Dictionary<Slot, string>)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CaptureImportedPaths", rigRoots);
            var rigAvatar = new VrchatAvatar { FbxGuid = "rig" };
            var rigCopy = new VrchatMeshCopy("rig", "Body", "AuthoredBody", true, true)
            { Transform = new VrchatPrefabTransform { Key = "rigPrefab:body", GameObjectKey = "rigPrefab:body-go" } };
            var rigTarget = new VrchatBoneTarget("rig", "Hips", "Hips", "rigPrefab", 2);
            rigCopy.BoneTargets[0] = rigTarget;
            rigAvatar.MeshCopies.Add(rigCopy);
            var rigPlacement = new VrchatPhysicsPlacement();
            rigPlacement.Transforms.Add(new VrchatPrefabTransform { Key = "rigPrefab:2", Name = "Hips", ImportedBone = rigTarget });
            rigAvatar.PhysicsPlacements.Add(rigPlacement);
            object[] rigArgs = { rigRoot, rigAvatar, rigRoots, rigSources, rigPaths, null, null, null };
            typeof(VrchatAvatar).Assembly.GetType("VrmToResonitePackage.Converter")!
                .GetMethod("ApplyVrchatPrefabHierarchy", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, rigArgs);
            var rigSlots = (Dictionary<string, Slot>)rigArgs[6];
            var rigNames = (Dictionary<string, Slot>)Call("VrmToResonitePackage.SlotIndex", "Build", rigRoot);
            Check(rigSlots["rigPrefab:body"].GetComponent<SkinnedMeshRenderer>().Bones[0] == rigHips &&
                  rigSlots["rigPrefab:2"] == rigHips && rigNames["Hips"] == rigHips,
                "Copied skin, physics and humanoid lookup share the primary imported skeleton");
            rigRoot.Destroy();
        }
        Slot alignment = root.AddSlot("Alignment"), wrapper = alignment.AddSlot("Model wrapper");
        Slot rootChild = wrapper.AddSlot("Physics child"), otherWrapper = root.AddSlot("Other model wrapper");
        var wrapperAvatar = new VrchatAvatar { FbxGuid = "wrapped" };
        var wrapperRoots = new Dictionary<string, Slot> { ["wrapped"] = wrapper, ["other"] = otherWrapper };
        var wrapperSources = (Dictionary<Slot, string>)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CaptureImportedObjects", wrapperRoots);
        var wrapperPaths = (Dictionary<Slot, string>)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CaptureImportedPaths", wrapperRoots);
        Call("VrmToResonitePackage.Converter", "CollapsePrimaryFbxWrapper", alignment, wrapperAvatar, wrapperRoots,
            wrapperSources, wrapperPaths);
        Call("VrmToResonitePackage.Converter", "RemoveImportAlignment", alignment, root, wrapperSources, wrapperPaths);
        Check((Slot)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "ResolveImportedTarget",
                new VrchatBoneTarget("wrapped", "RootNode", ""), wrapperSources, wrapperPaths) == root,
            "Model-root physics references survive both wrapper and alignment collapse");
        Check((Slot)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "ResolveImportedTarget",
                new VrchatBoneTarget("other", "RootNode", ""), wrapperSources, wrapperPaths) == otherWrapper &&
              rootChild.Parent == root, "Wrapper collapse retains other model identities and children");
        foreach (string targetPath in new[] { "", "RootNode", "//RootNode" })
        {
            var primaryAlignment = root.AddSlot("RootNode alignment");
            var primaryWrapper = primaryAlignment.AddSlot("Primary wrapper");
            var primaryNode = primaryWrapper.AddSlot("RootNode");
            primaryNode.AddSlot("Tip");
            var primaryRoots = new Dictionary<string, Slot> { ["primary-node"] = primaryWrapper };
            var primarySources = (Dictionary<Slot, string>)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CaptureImportedObjects", primaryRoots);
            var primaryPaths = (Dictionary<Slot, string>)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CaptureImportedPaths", primaryRoots);
            var target = new VrchatBoneTarget("primary-node", "RootNode", targetPath);
            void CheckRoot() => Check((Slot)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "ResolveImportedTarget",
                target, primarySources, primaryPaths) == primaryNode,
                "Primary model root resolves to RootNode instead of its synthetic wrapper: " + targetPath);
            CheckRoot();
            Call("VrmToResonitePackage.Converter", "CollapsePrimaryFbxWrapper", primaryAlignment,
                new VrchatAvatar { FbxGuid = "primary-node" }, primaryRoots, primarySources, primaryPaths);
            CheckRoot();
            Call("VrmToResonitePackage.Converter", "RemoveImportAlignment", primaryAlignment, root, primarySources, primaryPaths);
            CheckRoot();
            primaryNode.Destroy();
        }
        var additionalWrapper = root.AddSlot("Additional wrapper");
        var additionalNode = additionalWrapper.AddSlot("RootNode");
        additionalNode.AddSlot("Payload");
        var additionalAvatar = new VrchatAvatar { FbxGuid = "absent" };
        additionalAvatar.AdditionalFbxs.Add(new VrchatFbxAsset { Guid = "additional",
            TransformNodeName = "RootNode", InstanceName = "Accessory" });
        var additionalRoots = new Dictionary<string, Slot> { ["additional"] = additionalWrapper };
        var additionalSources = (Dictionary<Slot, string>)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CaptureImportedObjects", additionalRoots);
        var additionalPaths = (Dictionary<Slot, string>)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CaptureImportedPaths", additionalRoots);
        Call("VrmToResonitePackage.Converter", "ApplyVrchatPrefabHierarchy", root, additionalAvatar,
            additionalRoots, additionalSources, additionalPaths, null, null);
        Check(additionalWrapper.IsDestroyed && additionalRoots["additional"] == additionalNode &&
            (Slot)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "ResolveImportedTarget",
                new VrchatBoneTarget("additional", "RootNode", ""), additionalSources, additionalPaths) == additionalNode,
            "Additional synthetic-root physics identity survives wrapper collapse");
        var localBone = additionalNode.AddSlot("Head");
        additionalSources[localBone] = "additional";
        additionalPaths[localBone] = "Head";
        var localParents = new List<VrchatPrefabTransform> {
            new() { Key = "prefab:head", Active = false, ImportedBone = new VrchatBoneTarget("additional", "Head", "Head") },
            new() { Key = "prefab:attachment", Name = "Attachment", LocalPosition = new System.Numerics.Vector3(1, 0, 0) } };
        var localSlots = new Dictionary<string, Slot>();
        var attachment = (Slot)Call("VrmToResonitePackage.Converter", "ResolvePrefabParent", root,
            null, null, localParents, additionalRoots, localSlots, additionalSources, additionalPaths);
        Check(attachment.Parent == localBone && localSlots["prefab:head"] == localBone && attachment.LocalPosition.x == 1,
            "Unpacked attachment shares the imported skin bone and preserves its local placement");
        Check(!localBone.ActiveSelf && !attachment.IsActive,
            "Reused imported bone applies authored inactive state to its attachment hierarchy");
        var physicsOnlyAvatar = new VrchatAvatar { FbxGuid = "wrapped" };
        var physicsPlacement = new VrchatPhysicsPlacement();
        physicsPlacement.Transforms.Add(new VrchatPrefabTransform { Key = "physics:1", Name = "Physics only",
            LocalPosition = new System.Numerics.Vector3(0, 2, 0) });
        physicsPlacement.Transforms.Add(new VrchatPrefabTransform { Key = "physics:2", Name = "Tip",
            LocalPosition = new System.Numerics.Vector3(0, 1, 0) });
        physicsOnlyAvatar.PhysicsPlacements.Add(physicsPlacement);
        object[] hierarchyArgs = { root, physicsOnlyAvatar, new Dictionary<string, Slot>(),
            wrapperSources, wrapperPaths, null, null, null };
        typeof(VrchatAvatar).Assembly.GetType("VrmToResonitePackage.Converter")!
            .GetMethod("ApplyVrchatPrefabHierarchy", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, hierarchyArgs);
        var physicsOnlySlots = (Dictionary<string, Slot>)hierarchyArgs[6];
        var physicsOnlyTarget = new VrchatBoneTarget(null, "Physics only", "", "physics", 1);
        Check((Slot)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "ResolveImportedTarget",
                  physicsOnlyTarget, wrapperSources, wrapperPaths, physicsOnlySlots) == physicsOnlySlots["physics:1"] &&
              physicsOnlySlots["physics:1"].Parent == root && physicsOnlySlots["physics:1"].LocalPosition.y == 2 &&
              physicsOnlySlots["physics:2"].Parent == physicsOnlySlots["physics:1"],
            "Physics-only hierarchy is created without meshes and resolves independently of the avatar root");
        var mergeRoot = root.AddSlot("Physics merge regression");
        var cleanupRoot = root.AddSlot("Template cleanup regression");
        var templateRoot = cleanupRoot.AddSlot("Revan_Kipfel_underwear");
        var templateMesh = templateRoot.AddSlot("RootNode").AddSlot("underwear");
        var templateRenderer = templateMesh.AttachComponent<MeshRenderer>();
        templateRoot.AttachComponent<Rig>().Bones.Add((Slot)null);
        templateRoot.AttachComponent<MeshRendererMaterialRelay>().Renderers.Add(templateRenderer);
        var cleanupAvatar = new VrchatAvatar();
        cleanupAvatar.MeshCopies.Add(new VrchatMeshCopy("template", "underwear", "Authored underwear", true, true)
            { IsSkinned = false, ReplaceSourceRenderer = true,
              Transform = new VrchatPrefabTransform { Key = "clothing:1" } });
        var cleanupRoots = new Dictionary<string, Slot> { ["template"] = templateRoot };
        var cleanupSources = (Dictionary<Slot, string>)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CaptureImportedObjects", cleanupRoots);
        var cleanupPaths = (Dictionary<Slot, string>)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CaptureImportedPaths", cleanupRoots);
        var cleanupCandidates = new HashSet<Slot>();
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CreateMeshCopies", cleanupAvatar, cleanupSources,
            (Func<VrchatMeshCopy, Slot>)(_ => cleanupRoot), cleanupPaths, null, null, cleanupCandidates);
        var authoredEmpty = cleanupRoot.AddSlot("Authored empty");
        var referencedBone = cleanupRoot.AddSlot("Referenced bone");
        var referencedField = cleanupRoot.AddSlot("Referenced field");
        var untouchedEmpty = cleanupRoot.AddSlot("Unrelated empty");
        var liveRig = cleanupRoot.AddSlot("Live import rig");
        liveRig.AttachComponent<Rig>().Bones.Add(root.AddSlot("External bone"));
        var mergedSkeleton = cleanupRoot.AddSlot("Body skeleton");
        var destinationRig = mergedSkeleton.AttachComponent<Rig>();
        var movedHelper = mergedSkeleton.AddSlot("Bag.Root");
        var abandonedClothing = cleanupRoot.AddSlot("Merged clothing wrapper");
        abandonedClothing.AttachComponent<Rig>().Bones.Add(movedHelper);
        var rootHelper = cleanupRoot.AddSlot("Helper after primary wrapper collapse");
        var fallbackClothing = cleanupRoot.AddSlot("Clothing without destination rig");
        fallbackClothing.AttachComponent<Rig>().Bones.Add(rootHelper);
        cleanupRoot.AttachComponent<SkinnedMeshRenderer>().Bones.Add(referencedBone);
        cleanupRoot.AttachComponent<ReferenceField<IWorldElement>>().Reference.Target = referencedField.GetSyncMember("Position");
        cleanupCandidates.UnionWith(new[] { authoredEmpty, referencedBone, referencedField, liveRig, abandonedClothing, fallbackClothing });
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "RemoveEmptyMeshTemplates", cleanupRoot,
            cleanupCandidates, new[] { authoredEmpty });
        Check(templateRoot.IsDestroyed && templateMesh.IsDestroyed &&
              cleanupRoot.FindChild("Authored underwear").GetComponent<MeshRenderer>() != null,
            "Replaced mesh templates and their empty import parents are removed while the authored mesh remains");
        Check(!authoredEmpty.IsDestroyed && !referencedBone.IsDestroyed && !referencedField.IsDestroyed &&
              !untouchedEmpty.IsDestroyed && !liveRig.IsDestroyed && liveRig.GetComponent<Rig>() != null,
            "Template cleanup preserves authored objects, bone and field references, and unrelated empty slots");
        Check(abandonedClothing.IsDestroyed && !movedHelper.IsDestroyed && destinationRig.Bones.Contains(movedHelper),
            "Merged clothing helper registrations move to the containing body rig before the empty wrapper is removed");
        Check(fallbackClothing.IsDestroyed && cleanupRoot.GetComponent<Rig>().Bones.Contains(rootHelper),
            "Clothing helpers remain registered after the primary import wrapper and its rig have been collapsed");
        var targetHips = mergeRoot.AddSlot("AvatarArmature").AddSlot("Hips");
        var sourceHips = mergeRoot.AddSlot("ClothingArmature").AddSlot("Hips");
        var untouchedHips = mergeRoot.AddSlot("OtherArmature").AddSlot("Hips");
        var mergeSources = new Dictionary<Slot, string> { [sourceHips] = "clothing", [untouchedHips] = "other" };
        var mergePaths = new Dictionary<Slot, string> { [sourceHips] = "Hips", [untouchedHips] = "Hips" };
        var mergeAvatar = new VrchatAvatar();
        mergeAvatar.ModularMergeArmatures.Add(new VrchatModularMergeArmature
            { SourceName = "ClothingArmature", TargetName = "AvatarArmature" });
        var mergeNodes = new Dictionary<int, Slot>
            { [0] = sourceHips, [1] = sourceHips, [2] = untouchedHips,
              [3] = sourceHips.Parent, [4] = sourceHips.Parent };
        mergeNodes[0] = (Slot)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "ResolveImportedTarget",
            new VrchatBoneTarget("clothing", "Hips", "Hips"), mergeSources, mergePaths);
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "ApplyModularAvatar", mergeRoot, mergeAvatar, mergeNodes);
        Check(Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "ResolveImportedTarget",
                new VrchatBoneTarget("clothing", "Hips", "Hips"), mergeSources, mergePaths) == null,
            "Post-merge source identity lookup reproduces the destroyed physics target");
        Check(sourceHips.IsDestroyed && mergeNodes[0] == targetHips && mergeNodes[1] == targetHips &&
              mergeNodes[2] == untouchedHips,
            "Merged physics root and collider retain the surviving bone");
        Check(mergeNodes[3] == targetHips.Parent && mergeNodes[4] == targetHips.Parent,
            "Physics root and collider on the source armature follow the target armature");
        var finalHips = mergeRoot.AddSlot("FinalArmature").AddSlot("Hips");
        var secondMerge = new VrchatAvatar();
        secondMerge.ModularMergeArmatures.Add(new VrchatModularMergeArmature
            { SourceName = "AvatarArmature", TargetName = "FinalArmature" });
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "ApplyModularAvatar", mergeRoot, secondMerge, mergeNodes);
        Check(targetHips.IsDestroyed && mergeNodes[0] == finalHips && mergeNodes[1] == finalHips &&
              mergeNodes[2] == untouchedHips && mergeNodes[3] == finalHips.Parent && mergeNodes[4] == finalHips.Parent,
            "Successive armature merges remap physics targets without changing another instance");
        Slot primary = root.AddSlot("Primary"), additional = root.AddSlot("Additional");
        var scopedRoot = root.AddSlot("Scoped merge regression");
        var bodyArmature = scopedRoot.AddSlot("armature");
        var bodyHips = bodyArmature.AddSlot("Hips");
        bodyHips.AddSlot("Spine");
        var unrelatedArmature = scopedRoot.AddSlot("Other clothing").AddSlot("armature");
        unrelatedArmature.AddSlot("Hips").AddSlot("Spine");
        var underwearArmature = scopedRoot.AddSlot("Underwear").AddSlot("armature");
        var underwearHips = underwearArmature.AddSlot("Hips");
        var clothingHelper = underwearArmature.AddSlot("Clothing helper");
        var clothingRenderer = scopedRoot.AddSlot("Underwear mesh").AttachComponent<SkinnedMeshRenderer>();
        clothingRenderer.Bones.Add(underwearHips);
        clothingRenderer.Bones.Add(underwearArmature);
        var scopedAvatar = new VrchatAvatar();
        var sourceIdentity = new VrchatBoneTarget("underwear", "armature", "armature");
        var targetIdentity = new VrchatBoneTarget("body", "armature", "armature");
        scopedAvatar.ModularMergeArmatures.Add(new VrchatModularMergeArmature
            { SourceName = "armature", TargetName = "armature", SourceBoneTarget = sourceIdentity, TargetBoneTarget = targetIdentity });
        var scopedPhysics = new Dictionary<int, Slot> { [0] = underwearHips };
        Func<VrchatBoneTarget, Slot> resolveMerge = target => target == sourceIdentity ? underwearArmature : bodyArmature;
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "ApplyModularAvatar", scopedRoot, scopedAvatar,
            scopedPhysics, resolveMerge);
        Check(underwearHips.IsDestroyed && underwearArmature.IsDestroyed && !unrelatedArmature.IsDestroyed &&
              unrelatedArmature.Children.Single().Children.Count == 1 && scopedPhysics[0] == bodyHips &&
              clothingRenderer.Bones[0] == bodyHips && clothingRenderer.Bones[1] == bodyArmature &&
              clothingHelper.Parent == bodyArmature,
            "Merge Armature consumes its scoped source even when another same-name clothing has more matching bones");
        Slot branches = root.AddSlot("Branches");
        var chainRoot = root.AddSlot("Chained identity merges");
        var chainBody = chainRoot.AddSlot("armature");
        var chainBodyBone = chainBody.AddSlot("Hips");
        var chainA = chainRoot.AddSlot("armature");
        var chainABone = chainA.AddSlot("Hips");
        var chainB = chainRoot.AddSlot("armature");
        var chainBBone = chainB.AddSlot("Hips");
        var chainC = chainRoot.AddSlot("Hips");
        var chainRenderer = chainRoot.AddSlot("Skin").AttachComponent<SkinnedMeshRenderer>();
        chainRenderer.Bones.Add(chainBBone);
        chainRenderer.Bones.Add(chainC);
        var chainSlots = new Dictionary<VrchatBoneTarget, Slot>();
        VrchatBoneTarget Identity(string guid, string path, Slot slot)
        {
            var identity = new VrchatBoneTarget(guid, slot.Name, path);
            chainSlots.Add(identity, slot);
            return identity;
        }
        var bodyId = Identity("body", "armature", chainBody);
        var aId = Identity("a", "armature", chainA);
        var aBoneId = Identity("a", "armature/Hips", chainABone);
        var bId = Identity("b", "armature", chainB);
        var cId = Identity("c", "Hips", chainC);
        var chainAvatar = new VrchatAvatar();
        foreach (var (source, target) in new[] { (aId, bodyId), (bId, aId), (cId, aBoneId) })
            chainAvatar.ModularMergeArmatures.Add(new VrchatModularMergeArmature
                { SourceBoneTarget = source, TargetBoneTarget = target });
        Func<VrchatBoneTarget, Slot> chainResolver = t => chainSlots[t].IsDestroyed ? null : chainSlots[t];
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "ApplyModularAvatar", chainRoot, chainAvatar,
            null, chainResolver);
        Check(chainA.IsDestroyed && chainB.IsDestroyed && chainC.IsDestroyed &&
              chainRenderer.Bones.All(b => b == chainBodyBone),
            "Later merges follow consumed armature and descendant identities to the surviving body bones");
        Slot left = branches.AddSlot("Left").AddSlot("Shared");
        Slot right = branches.AddSlot("Right").AddSlot("Shared");
        left.AddSlot("Body");
        Slot survivingBody = right.AddSlot("Body");
        Slot topLevelBody = branches.AddSlot("Body");
        topLevelBody.AttachComponent<MeshRenderer>();
        Slot survivingBone = topLevelBody.AddSlot("Bone");
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
        Check(!topLevelBody.IsDestroyed && !survivingBone.IsDestroyed,
            "EditorOnly nested path preserves a top-level namesake renderer and its bones");
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
        // Multiple local mesh sources can leave physics bones as authored placements.
        // Exercise the converter order, so skin binding and scale see those later slots.
        foreach (bool lateStatic in new[] { true, false })
        {
            var lateAvatar = new VrchatAvatar { FbxGuid = "primary", FbxImportScale = 1f };
            lateAvatar.AdditionalFbxs.Add(new VrchatFbxAsset { Guid = "additional", ImportScale = 0.01f });
            var lateCopy = new VrchatMeshCopy("additional", rendererName, "LatePhysicsCopy", true, true)
            {
                IsSkinned = !lateStatic,
                Transform = new VrchatPrefabTransform { Key = "late:1", GameObjectKey = "late:go" },
            };
            lateCopy.BoneTargets[0] = new VrchatBoneTarget("primary", "Hips", "Right/Hips", "late", 2);
            lateAvatar.MeshCopies.Add(lateCopy);
            var latePlacement = new VrchatPhysicsPlacement();
            if (lateStatic) latePlacement.Transforms.Add(lateCopy.Transform);
            latePlacement.Transforms.Add(new VrchatPrefabTransform { Key = "late:2", Name = "Hips",
                LocalPosition = new System.Numerics.Vector3(1, 0, 0) });
            lateAvatar.PhysicsPlacements.Add(latePlacement);
            object[] lateArgs = { root, lateAvatar, boneRoots, boneSources, bonePaths, null, null, null };
            typeof(VrchatAvatar).Assembly.GetType("VrmToResonitePackage.Converter")!
                .GetMethod("ApplyVrchatPrefabHierarchy", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, lateArgs);
            var lateSlots = (Dictionary<string, Slot>)lateArgs[6];
            if (lateStatic)
                Check(MathF.Abs(lateSlots["late:2"].GlobalPosition.x - lateSlots["late:1"].GlobalPosition.x - 1f) < 0.001f &&
                      MathF.Abs(lateSlots["late:2"].GlobalScale.x - 1f) < 0.001f,
                    "Late physics child retains authored position and scale beneath a corrected static mesh");
            else
                Check(lateSlots["late:1"].GetComponent<SkinnedMeshRenderer>().Bones[0] == lateSlots["late:2"],
                    "Skin and later physics placement share the authored bone with multiple model sources");
        }
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
        indexedCopy.BoneTargets[0] = new VrchatBoneTarget("primary", "Pelvis", "Left/Pelvis", "prefab", 20);
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CreateMeshCopies", reboundAvatar, indexedSources,
            (Func<VrchatMeshCopy, Slot>)(_ => primary), bonePaths);
        var renamedResult = primary.FindChild("IndexedCopy").GetComponent<SkinnedMeshRenderer>();
        Check(renamedResult.Bones[0] == primaryLeft && renamedResult.Bones[2] == clothingRight,
            "Renamed unpacked bone preserves the original skin binding when no authored slot exists");
        renamedResult.Slot.Destroy();
        var authoredBones = new Dictionary<string, Slot>();
        Slot pelvis = primary.AddSlot("Pelvis");
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CreateMeshCopies", reboundAvatar, indexedSources,
            (Func<VrchatMeshCopy, Slot>)(_ => { authoredBones["prefab:20"] = pelvis; return primary; }), bonePaths, authoredBones);
        renamedResult = primary.FindChild("IndexedCopy").GetComponent<SkinnedMeshRenderer>();
        Check(renamedResult.Bones[0] == pelvis && renamedResult.Bones[2] == clothingRight,
            "Renamed bone resolves authored identity after parent creation without changing other model bindings");
        renamedResult.Slot.Destroy();
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
        model.ImportScale = 2f;
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
        Check(staticCopy.Slot.LocalScale == new float3(2f, 2f, 2f),
            $"Static copy preserves FBX import scale outside its model hierarchy (scale={staticCopy.Slot.LocalScale})");
        Slot scaledParent = primary.AddSlot("Scaled imported parent");
        scaledParent.LocalScale = new float3(2f, 2f, 2f);
        var attachedAvatar = new VrchatAvatar();
        attachedAvatar.AdditionalFbxs.Add(model);
        attachedAvatar.MeshCopies.Add(new VrchatMeshCopy("additional", "StaticCopy", "AttachedStatic", true, true)
        {
            IsSkinned = false, ParentFbxGuid = "additional", Transform = new VrchatPrefabTransform(),
        });
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CreateMeshCopies", attachedAvatar, sources,
            (Func<VrchatMeshCopy, Slot>)(_ => scaledParent), new Dictionary<Slot, string>());
        Check(scaledParent.FindChild("AttachedStatic").LocalScale == new float3(1f, 1f, 1f),
            "Static copy under a scaled imported parent does not apply import scale twice");
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
                Transform = new VrchatPrefabTransform { Key = $"regular:transform{i}", GameObjectKey = $"regular:{i}",
                    LocalPosition = new System.Numerics.Vector3(i + 1, 2, 3) },
            };
            if (original.Bones.Count > 0 && original.Bones[0] != null)
                authored.BoneTargets[0] = new VrchatBoneTarget(null, null);
            sameNameAvatar.MeshCopies.Add(authored);
        }
        Slot originalSlot = original.Slot;
        var authoredObjects = (Dictionary<string, Slot>)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CreateMeshCopies", sameNameAvatar, sources,
            (Func<VrchatMeshCopy, Slot>)(c => c.RendererFileId == 1 ? parentA : parentB), new Dictionary<Slot, string>());
        var sameA = parentA.FindChild(rendererName).GetComponent<SkinnedMeshRenderer>();
        var sameB = parentB.FindChild(rendererName).GetComponent<SkinnedMeshRenderer>();
        Check(sameA != null && sameB != null && sameA.Slot.LocalPosition.x == 1 && sameB.Slot.LocalPosition.x == 2,
            "Two same-named authored renderers retain separate parents and transforms");
        for (int i = 1; i >= 0; i--)
        {
            var distinct = new VrchatRendererMaterials { FbxGuid = "additional", RendererGameObjectName = rendererName,
                PrefabObjectKey = $"regular:{i}" };
            distinct.MaterialGuids.Add(i == 0 ? materialGuid : defaultMaterialGuid);
            distinct.InitialBlendShapes.Add((0, i == 0 ? 25f : 75f));
            sameNameAvatar.RendererMaterials.Add(distinct);
        }
        Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "ApplyInitialBlendShapes", root, sameNameAvatar, sources, authoredObjects);
        await (Task)Call("VrmToResonitePackage.Vrchat.VrchatMaterialBuilder", "Apply", root, assets, sameNameAvatar, package, sources, authoredObjects);
        Check(Math.Abs(sameA.BlendShapeWeights[0] - 0.25f) < 0.001f &&
              Math.Abs(sameB.BlendShapeWeights[0] - 0.75f) < 0.001f &&
              ((Component)sameA.Materials[0]).Slot.Name == "Material: ReviewMaterial" &&
              ((Component)sameB.Materials[0]).Slot.Name == "Material: ReviewDefault",
            "Same-named copies receive separate materials and morph weights independent of record order");
        var faceAvatar = new VrchatAvatar();
        faceAvatar.Visemes.Add(new VrchatViseme { ResonitePreset = "aa", MeshGameObjectName = rendererName,
            MeshGameObjectPath = "Copy parent B/" + rendererName, BlendShapeName = sameB.BlendShapeName(0) });
        var faceModel = VrchatModelAdapter.ToVrmModel(faceAvatar);
        var resolverType = typeof(VrchatAvatar).Assembly.GetType("VrmToResonitePackage.BlendshapeResolver")!;
        var descriptorAvatar = new VrchatAvatar();
        var descriptorTarget = new VrchatBoneTarget(null, rendererName, null, "descriptor-copy", 21);
        descriptorAvatar.Visemes.Add(new VrchatViseme { ResonitePreset = "aa", MeshGameObjectName = rendererName,
            MeshTarget = descriptorTarget, BlendShapeName = sameB.BlendShapeName(0) });
        descriptorAvatar.Blink = new VrchatBlink { MeshGameObjectName = rendererName,
            MeshTarget = descriptorTarget, BlendShapeIndex = 0 };
        var descriptorModel = VrchatModelAdapter.ToVrmModel(descriptorAvatar);
        var descriptorNodes = descriptorModel.NodeTargets.ToDictionary(pair => pair.Key, pair => sameB.Slot);
        var descriptorResolver = Activator.CreateInstance(resolverType, root, descriptorModel, descriptorNodes)!;
        foreach (var expression in descriptorModel.Expressions)
            Check(ReferenceEquals(resolverType.GetMethod("Resolve")!.Invoke(descriptorResolver,
                    new object[] { expression.Binds.Single() }), sameB.BlendShapeWeights.GetElement(0)),
                "Descriptor " + expression.Preset + " selects the second namesake by object identity");
        var missingDescriptorResolver = Activator.CreateInstance(resolverType, root, descriptorModel,
            new Dictionary<int, Slot>())!;
        Check(descriptorModel.Expressions.All(expression => resolverType.GetMethod("Resolve")!.Invoke(
                missingDescriptorResolver, new object[] { expression.Binds.Single() }) == null),
            "Missing descriptor identity cannot fall back to another renderer");
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
        var descriptorParts = new Stack<string>();
        for (Slot slot = sameB.Slot; slot != root; slot = slot.Parent) descriptorParts.Push(slot.Name);
        faceModel.MeshBindingRootPath = string.Join("/", descriptorParts);
        var exportResolver = Activator.CreateInstance(resolverType, root, faceModel)!;
        Check(ReferenceEquals(resolverType.GetMethod("Resolve")!.Invoke(exportResolver, new object[] { bind }),
            sameB.BlendShapeWeights.GetElement(0)), "Empty Animator path resolves the descriptor renderer below the export root");
        // Reproduce setup-time reparenting while a namesake renderer stays in place.
        Slot originalFaceParent = sameB.Slot.Parent;
        Slot movedFaceParent = root.AddSlot("Merged face parent");
        sameB.Slot.Parent = movedFaceParent;
        Check(descriptorModel.Expressions.All(expression => ReferenceEquals(resolverType.GetMethod("Resolve")!.Invoke(
                descriptorResolver, new object[] { expression.Binds.Single() }), sameB.BlendShapeWeights.GetElement(0))),
            "Descriptor renderer identity survives reparenting");
        var lateResolver = Activator.CreateInstance(resolverType, root, faceModel)!;
        Check(resolverType.GetMethod("Resolve")!.Invoke(lateResolver, new object[] { bind }) == null,
            "Reproduction: capturing Animator paths after reparenting loses the surviving face");
        Check(ReferenceEquals(resolverType.GetMethod("Resolve")!.Invoke(exportResolver, new object[] { bind }),
            sameB.BlendShapeWeights.GetElement(0)), "Captured descriptor renderer survives Merge Armature reparenting");
        string originalDescriptorPath = faceModel.MeshBindingRootPath;
        faceModel.MeshBindingRootPath = "";
        faceModel.MeshBindingPaths[bind.MeshIndex] = originalDescriptorPath;
        Check(ReferenceEquals(resolverType.GetMethod("Resolve")!.Invoke(exportResolver, new object[] { bind }),
            sameB.BlendShapeWeights.GetElement(0)), "Nonempty Animator path retains the moved renderer instead of its namesake");
        faceModel.MeshBindingRootPath = originalDescriptorPath;
        faceModel.MeshBindingPaths[bind.MeshIndex] = "";
        Slot eyePivot = movedFaceParent.AddSlot("Left Eye Pivot");
        sameB.Slot.Parent = eyePivot;
        Check(ReferenceEquals(resolverType.GetMethod("Resolve")!.Invoke(exportResolver, new object[] { bind }),
            sameB.BlendShapeWeights.GetElement(0)), "Captured face binding survives subsequent eye pivot insertion");
        sameB.Slot.Parent = originalFaceParent;
        movedFaceParent.Destroy();
        var physicsRoot = root.AddSlot("Repeated physics");
        var leftJoint = physicsRoot.AddSlot("Joint");
        var rightJoint = physicsRoot.AddSlot("Joint");
        leftJoint.AddSlot("Tip").LocalPosition = new float3(0, 0.1f, 0);
        rightJoint.AddSlot("Tip").LocalPosition = new float3(0, 0.1f, 0);
        var physicsAvatar = new VrchatAvatar();
        physicsAvatar.PhysBones.Add(new VrchatPhysBone { RootBoneName = "Joint",
            RootBoneTarget = new VrchatBoneTarget("shared", "Joint", "Joint", "prefab", 1) });
        physicsAvatar.PhysBones.Add(new VrchatPhysBone { RootBoneName = "Joint",
            RootBoneTarget = new VrchatBoneTarget("shared", "Joint", "Joint", "prefab", 2) });
        var physicsModel = VrchatModelAdapter.ToVrmModel(physicsAvatar);
        var physicsSources = new Dictionary<Slot, string> { [leftJoint] = "left", [rightJoint] = "right" };
        var physicsPaths = new Dictionary<Slot, string> { [leftJoint] = "", [rightJoint] = "" };
        var authoredPhysicsSlots = new Dictionary<string, Slot> { ["prefab:1"] = leftJoint, ["prefab:2"] = rightJoint };
        var physicsNodes = physicsModel.NodeTargets.ToDictionary(entry => entry.Key, entry =>
            (Slot)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "ResolveImportedTarget",
                entry.Value, physicsSources, physicsPaths, authoredPhysicsSlots));
        typeof(VrchatAvatar).Assembly.GetType("VrmToResonitePackage.SpringBoneSetup")!
            .GetMethod("Apply")!.Invoke(null, new object[] { physicsRoot, physicsModel, physicsNodes });
        Check(leftJoint.GetComponents<DynamicBoneChain>().Count() == 1 &&
              rightJoint.GetComponents<DynamicBoneChain>().Count() == 1,
            "Repeated instances attach one PhysBone chain to each same-named joint");
        Check(sameA.Bones.Count > 0 && sameA.Bones[0] == null && sameB.Bones[0] == null,
            "Same-named renderer copies apply authored bone overrides");
        Check(originalSlot.GetComponent<SkinnedMeshRenderer>() == null && !originalSlot.IsDestroyed,
            "Regular prefab replaces the imported renderer while retaining its referenced slot");
        Slot modelRoot = root.AddSlot("Duplicate source model");
        Slot leftSource = sameA.Slot.Duplicate(modelRoot.AddSlot("Left"));
        Slot rightSource = sameB.Slot.Duplicate(modelRoot.AddSlot("Right"));
        Slot topLevelSource = sameA.Slot.Duplicate(modelRoot);
        leftSource.GetComponent<SkinnedMeshRenderer>().BlendShapeWeights[0] = 0.12f;
        rightSource.GetComponent<SkinnedMeshRenderer>().BlendShapeWeights[0] = 0.89f;
        var pathSources = new Dictionary<Slot, string>
            { [leftSource] = "model", [rightSource] = "model", [topLevelSource] = "model" };
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
        var hierarchyAvatar = new VrchatAvatar { FbxGuid = "model", FbxImportScale = 2f };
        foreach (string name in new[] { "AuthoredChild", "AuthoredRoot" })
            hierarchyAvatar.MeshCopies.Add(new VrchatMeshCopy("model", rendererName, name, true, true)
            {
                SourcePath = "RootNode/Right/" + rendererName,
                IsSkinned = false,
                Transform = new VrchatPrefabTransform { Key = name == "AuthoredRoot" ? "prefab:10" : "other-prefab:10", GameObjectKey = name + "GO",
                    LocalPosition = name == "AuthoredChild" ? new System.Numerics.Vector3(3, 0, 0) : default },
            });
        var placeholder = primary.AddSlot("Old authored root placeholder");
        var nestedModel = placeholder.AddSlot("Nested imported model");
        rightSource.SetParent(nestedModel, false);
        var existingChild = placeholder.AddSlot("Existing attachment");
        existingChild.LocalPosition = new float3(4, 0, 0);
        var prefabSlots = new Dictionary<string, Slot> { ["prefab:10"] = placeholder };
        var hierarchyObjects = (Dictionary<string, Slot>)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CreateMeshCopies",
            hierarchyAvatar, pathSources, (Func<VrchatMeshCopy, Slot>)(c => c.Name == "AuthoredChild" ? prefabSlots["prefab:10"] : primary),
            sourcePaths, prefabSlots);
        Check(hierarchyObjects["AuthoredChildGO"].Parent == hierarchyObjects["AuthoredRootGO"] &&
              existingChild.Parent == hierarchyObjects["AuthoredRootGO"] && placeholder.IsDestroyed,
            "Renderer on the authored root owns its child renderer and existing attachments regardless of document order");
        var authoredTarget = new VrchatBoneTarget("model", rendererName, "Right/" + rendererName, "prefab", 10);
        Check((Slot)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "ResolveImportedTarget",
                authoredTarget, pathSources, sourcePaths, prefabSlots) == hierarchyObjects["AuthoredRootGO"],
            "Physics selects the authored static renderer with its children instead of the imported template");
        Check((Slot)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "ResolveImportedTarget",
                authoredTarget with { PrefabGuid = "other-prefab", FbxGuid = null }, pathSources, sourcePaths, prefabSlots)
                == hierarchyObjects["AuthoredChildGO"], "Authored physics identity includes the prefab instance without requiring an FBX");
        Check(hierarchyObjects["AuthoredRootGO"].LocalScale == new float3(2f, 2f, 2f) &&
              hierarchyObjects["AuthoredChildGO"].LocalScale == new float3(1f, 1f, 1f) &&
              hierarchyObjects["AuthoredChildGO"].LocalPosition.x == 1.5f && existingChild.LocalPosition.x == 2f,
            "Static renderer scale correction does not scale authored child geometry or attachment placement twice");
        var removalAvatar = new VrchatAvatar { FbxGuid = "model", FbxImportScale = 2f };
        foreach (string name in new[] { "SurvivingChild", "RemovedParent" })
            removalAvatar.MeshCopies.Add(new VrchatMeshCopy("model", rendererName, name, true, true)
            {
                IsSkinned = false, RendererRemoved = name == "RemovedParent", ReplaceSourceRenderer = true,
                SourcePath = "RootNode/Right/" + rendererName,
                Transform = new VrchatPrefabTransform { Key = name, GameObjectKey = name,
                    LocalPosition = new System.Numerics.Vector3(3, 0, 0) },
            });
        var removalSlots = new Dictionary<string, Slot>();
        var removedObjects = (Dictionary<string, Slot>)Call("VrmToResonitePackage.Vrchat.VrchatSceneSetup", "CreateMeshCopies",
            removalAvatar, pathSources, (Func<VrchatMeshCopy, Slot>)(c => c.Name == "SurvivingChild" ? removalSlots["RemovedParent"] : primary),
            sourcePaths, removalSlots);
        Check(removedObjects["RemovedParent"].GetComponent<MeshRenderer>() == null &&
              removedObjects["SurvivingChild"].GetComponent<MeshRenderer>() != null &&
              removedObjects["SurvivingChild"].Parent == removedObjects["RemovedParent"] &&
              removedObjects["SurvivingChild"].LocalPosition.x == 3 &&
              removedObjects["RemovedParent"].LocalScale == new float3(1f, 1f, 1f) &&
              rightSource.GetComponent<MeshRenderer>() == null && !rightSource.IsDestroyed,
            "Removed renderer leaves its authored object and child placement intact and cannot reappear as an imported template");
    });
}

static object Call(string type, string method, params object[] args)
{
    var info = typeof(VrchatAvatar).Assembly.GetType(type)!
        .GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
    return info.Invoke(null, args.Concat(info.GetParameters().Skip(args.Length).Select(p => p.DefaultValue)).ToArray())!;
}
static void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    Console.WriteLine("PASS: " + label);
}
