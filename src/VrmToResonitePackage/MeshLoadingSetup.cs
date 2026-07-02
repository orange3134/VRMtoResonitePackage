using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.CommonAvatar;
using FrooxEngine.Store;
using Renderite.Shared;
using SkyFrost.Base;

namespace VrmToResonitePackage;

/// <summary>
/// Keeps imported renderers hidden until all of their meshes are available on the client.
/// A small procedural stand-in remains visible while the assets are loading.
/// </summary>
internal static class MeshLoadingSetup
{
    private const string DynamicVariableSpaceName = "modular_avatar";
    private const string MeshNotLoadedVariable = "modular_avatar/MeshNotLoaded";
    private const string HeadBoneVariable = "modular_avatar/HumanBone.head";
    private const string HeadPoseVariable = "modular_avatar/HumanBonePose.head";
    private const string AvatarPoseNodeHeadVariable = "modular_avatar/AvatarPoseNode.Head";

    public static void Apply(Slot root)
    {
        List<MeshRenderer> renderers = root.GetComponentsInChildren<MeshRenderer>()
            .Where(renderer => renderer.Mesh.Target != null)
            .ToList();
        if (renderers.Count == 0)
        {
            return;
        }

        EnsureDynamicVariableSpace(root);
        PublishHeadPose(root);

        Slot loadCheck = root.AddSlot("<color=cyan>ResoPon</color> - Mesh loaded check");
        MeshRendererLoadStatus loadStatus = loadCheck.AttachComponent<MeshRendererLoadStatus>();
        DynamicValueVariable<bool> loadVariable = loadCheck.AttachComponent<DynamicValueVariable<bool>>();
        loadVariable.Value.Value = false;

        BooleanValueDriver<string> variableNameDriver = loadCheck.AttachComponent<BooleanValueDriver<string>>();
        variableNameDriver.FalseValue.Value = MeshNotLoadedVariable;
        variableNameDriver.TrueValue.Value = null;
        variableNameDriver.TargetField.Target = loadVariable.VariableName;
        variableNameDriver.State.DriveFrom(loadStatus.IsLoaded);

        Slot avatarSettings = root.FindChild("<color=#00ffff>Avatar Settings</color>")
                              ?? root.FindChild("Avatar Settings")
                              ?? root.AddSlot("<color=#00ffff>Avatar Settings</color>");
        Slot loadingDisplay = avatarSettings.AddSlot("Avatar Loading Display");
        Slot standIn = ImportLoadingStandIn(loadingDisplay);

        foreach (MeshRenderer renderer in renderers)
        {
            DynamicValueVariableDriver<bool> driver = renderer.Slot.AttachComponent<DynamicValueVariableDriver<bool>>();
            driver.VariableName.Value = MeshNotLoadedVariable;
            driver.DefaultValue.Value = true;
            driver.Target.Target = renderer.EnabledField;
            loadStatus.Renderers.Add().Target = renderer;
        }

        DynamicValueVariableDriver<bool> standInDriver = loadingDisplay.AttachComponent<DynamicValueVariableDriver<bool>>();
        BooleanValueDriver<bool> invertDriver = loadingDisplay.AttachComponent<BooleanValueDriver<bool>>();
        invertDriver.TargetField.Target = standIn.ActiveSelf_Field;
        invertDriver.FalseValue.Value = true;
        invertDriver.TrueValue.Value = false;
        standInDriver.VariableName.Value = MeshNotLoadedVariable;
        standInDriver.Target.Target = invertDriver.State;
        standInDriver.DefaultValue.Value = true;
    }

    private static void EnsureDynamicVariableSpace(Slot root)
    {
        DynamicVariableSpace space = root.GetComponents<DynamicVariableSpace>()
            .FirstOrDefault(candidate => candidate.SpaceName.Value == DynamicVariableSpaceName);
        if (space == null)
        {
            space = root.AttachComponent<DynamicVariableSpace>();
            space.SpaceName.Value = DynamicVariableSpaceName;
            space.OnlyDirectBinding.Value = true;
        }
    }

    private static void PublishHeadPose(Slot root)
    {
        BipedRig rig = root.GetComponentsInChildren<BipedRig>()
            .FirstOrDefault(candidate => candidate.IsBiped && candidate[BodyNode.Head] != null);
        Slot head = rig?[BodyNode.Head];
        if (head == null)
        {
            UniLog.Warning($"{HeadBoneVariable} and {HeadPoseVariable} could not be configured because the head bone was not found.");
            return;
        }

        DynamicReferenceVariable<Slot> bone = head.AttachComponent<DynamicReferenceVariable<Slot>>();
        bone.VariableName.Value = HeadBoneVariable;
        bone.OverrideOnLink.Value = true;
        bone.Reference.Target = head;

        DynamicValueVariable<float4x4> pose = head.AttachComponent<DynamicValueVariable<float4x4>>();
        pose.VariableName.Value = HeadPoseVariable;
        pose.OverrideOnLink.Value = true;
        pose.Value.Value = head.LocalToGlobal;

        AvatarPoseNode poseNode = root.GetComponentsInChildren<AvatarPoseNode>()
            .FirstOrDefault(candidate => candidate.Node.Value == BodyNode.Head);
        if (poseNode != null)
        {
            ReferenceField<Slot> poseNodeReference = poseNode.Slot.AttachComponent<ReferenceField<Slot>>();
            poseNodeReference.Reference.Target = poseNode.Slot;
            DynamicReferenceVariable<Slot> poseNodeVariable =
                poseNode.Slot.AttachComponent<DynamicReferenceVariable<Slot>>();
            poseNodeVariable.VariableName.Value = AvatarPoseNodeHeadVariable;
            poseNodeVariable.Reference.DriveFrom(poseNodeReference.Reference);
        }
    }

    private static Slot ImportLoadingStandIn(Slot parent)
    {
        Slot standIn = parent.AddSlot("LoadingSpinner");
        string tempPath = Path.Combine(Path.GetTempPath(),
            "ResoPon_LoadingStandIn_" + Guid.NewGuid().ToString("N") + ".resonitepackage");
        try
        {
            using System.IO.Stream resource = typeof(MeshLoadingSetup).Assembly.GetManifestResourceStream(
                "VrmToResonitePackage.Resources.loading_standin.resonitepackage");
            if (resource == null)
            {
                throw new InvalidOperationException("Loading stand-in resource was not found.");
            }

            using (FileStream file = File.Create(tempPath))
            {
                resource.CopyTo(file);
            }

            using RecordPackage package = RecordPackage.Decode(tempPath);
            SkyFrost.Base.Record record = package.MainRecord
                ?? throw new InvalidDataException("Loading stand-in package has no main record.");
            string signature = RecordPackage.GetAssetSignature(new Uri(record.AssetURI));
            using System.IO.Stream asset = package.ReadAsset(signature);
            DataTreeDictionary graph = DataTreeConverter.LoadAuto(asset)
                ?? throw new InvalidDataException("Loading stand-in package has no DataTree.");

            standIn.LoadObject(graph, record);
            standIn.ForeachComponentInChildren(delegate(IPackageImportEventReceiver receiver)
            {
                receiver.OnPackageImported();
            }, includeLocal: false, cacheItems: true);
            standIn.LocalRotation = floatQ.Identity;
            standIn.LocalScale = float3.One;

            DynamicReferenceVariableDriver<Slot> overrideParentDriver =
                standIn.GetComponentInChildren<DynamicReferenceVariableDriver<Slot>>();
            if (overrideParentDriver == null)
            {
                UniLog.Warning("LoadingStandin VirtualParent OverrideParent DV driver was not found.");
            }
            else
            {
                overrideParentDriver.VariableName.Value = AvatarPoseNodeHeadVariable;
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Temp cleanup is best-effort.
            }
        }
        return standIn;
    }
}
