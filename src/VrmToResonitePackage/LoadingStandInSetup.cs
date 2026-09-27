using Elements.Core;
using FrooxEngine;
using FrooxEngine.FrooxEngine.ProtoFlux.CoreNodes;
using FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes;
using FrooxEngine.Store;
using Renderite.Shared;
using VrmToResonitePackage.Expressions;
using static VrmToResonitePackage.Expressions.ExpressionFlux;

namespace VrmToResonitePackage;

/// <summary>Builds the loading thumbnail from stock components and ProtoFlux nodes.</summary>
internal static class LoadingStandInSetup
{
    public static async Task<Slot> BuildAsync(Slot parent)
    {
        Uri fallbackUri = await ImportFallbackTexture(parent.Engine);
        Slot standIn = parent.AddSlot("LoadingStandin");
        var virtualParent = standIn.AttachComponent<VirtualParent>();
        // The original stand-in follows position/rotation only, retaining avatar scale.
        ((FieldDrive<float3>)virtualParent.GetSyncMember("_targetPos")).Target = standIn.Position_Field;
        ((FieldDrive<floatQ>)virtualParent.GetSyncMember("_targetRot")).Target = standIn.Rotation_Field;
        virtualParent.LocalPosition.Value = new float3(0, 0.05235994f, 0);
        virtualParent.LocalRotation.Value = floatQ.Identity;
        var headDriver = standIn.AttachComponent<DynamicReferenceVariableDriver<Slot>>();
        headDriver.VariableName.Value = "modular_avatar/AvatarPoseNode.Head";
        headDriver.Target.Target = virtualParent.OverrideParent;

        Slot visual = standIn.AddSlot("Visual");
        visual.LocalRotation = floatQ.Euler(0, 180, 0);
        var mesh = visual.AttachComponent<QuadMesh>();
        mesh.Size.Value = new float2(1, 1);
        var renderer = visual.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        var transformOverride = visual.AttachComponent<RenderTransformOverride>();
        transformOverride.Context.Value = RenderingContext.UserView;

        Slot textureSlot = standIn.AddSlot("UserIconTex");
        var userInfo = textureSlot.AttachComponent<CloudUserInfo>();
        var texture = textureSlot.AttachComponent<StaticTexture2D>();
        texture.URL.Value = fallbackUri;
        var material = standIn.AddSlot("UserIconMat").AttachComponent<UnlitMaterial>();
        material.Texture.Target = texture;
        material.BlendMode.Value = BlendMode.Alpha;
        material.Sidedness.Value = Sidedness.Double;
        material.ZWrite.Value = ZWrite.On;
        renderer.Materials.Add().Target = material;

        var iconGraph = new ExpressionFlux(standIn.AddSlot("Get Profile Icon"));
        var userId = iconGraph.Node("UserUserID", null, ("User", iconGraph.Owner(standIn)));
        var idDrive = (ObjectFieldDrive<string>)iconGraph.Node("ObjectFieldDrive", typeof(string), ("Value", userId));
        idDrive.GetRootProxy(addIfMissing: true).Drive.Target = userInfo.UserId;
        var icon = (ObjectValueSource<Uri>)iconGraph.Node("ObjectValueSource", typeof(Uri));
        icon.RootSourceReference.Target = userInfo.IconURL;
        var fallback = (ValueObjectInput<Uri>)iconGraph.Node("ValueObjectInput", typeof(Uri));
        fallback.Value.Value = fallbackUri;
        var urlDrive = (ObjectFieldDrive<Uri>)iconGraph.Node("ObjectFieldDrive", typeof(Uri),
            ("Value", iconGraph.Choose<Uri>(iconGraph.IsNull<Uri>(icon), fallback, icon)));
        urlDrive.GetRootProxy(addIfMissing: true).Drive.Target = texture.URL;

        var scaleGraph = new ExpressionFlux(standIn.AddSlot("AutoSetScale"));
        var wornLocal = scaleGraph.And(scaleGraph.IsOwner(standIn),
            scaleGraph.DynamicInput<bool>("modular_avatar", "AvatarWorn"));
        var hiddenScale = scaleGraph.Node("PackNullable", typeof(float3),
            ("Value", scaleGraph.Constant(float3.Zero)), ("HasValue", wornLocal));
        var hideDrive = (ObjectFieldDrive<float3?>)scaleGraph.Node("ObjectFieldDrive", typeof(float3?), ("Value", hiddenScale));
        hideDrive.GetRootProxy(addIfMissing: true).Drive.Target = transformOverride.ScaleOverride;

        // Match the original thumbnail size: one quarter of the saved head-pose height.
        var headPosition = scaleGraph.Node("Mul_Float4x4_Float3", null,
            ("A", scaleGraph.DynamicInput<float4x4>("modular_avatar", "HumanBonePose.head")),
            ("B", scaleGraph.Constant(float3.Zero)));
        var headCoordinates = scaleGraph.Node("Unpack_Float3", null, ("V", headPosition));
        var size = scaleGraph.Mul(Out(headCoordinates, "Y"), scaleGraph.Constant(0.25f));
        var scale = scaleGraph.Node("Pack_Float3", null, ("X", size), ("Y", size), ("Z", size));
        var scaleDrive = (ValueFieldDrive<float3>)scaleGraph.Node("ValueFieldDrive", typeof(float3), ("Value", scale));
        scaleDrive.GetRootProxy(addIfMissing: true).Drive.Target = visual.Scale_Field;
        ExpressionFlux.Arrange(standIn);
        return standIn;
    }

    private static async Task<Uri> ImportFallbackTexture(Engine engine)
    {
        string tempFile = engine.LocalDB.GetTempFilePath(".png");
        try
        {
            await default(ToBackground);
            using System.IO.Stream resource = typeof(LoadingStandInSetup).Assembly.GetManifestResourceStream(
                "VrmToResonitePackage.Resources.LoadingFallback.png")
                ?? throw new InvalidOperationException("Loading fallback texture was not found.");
            using (FileStream file = File.Create(tempFile))
            {
                await resource.CopyToAsync(file);
            }
            return await engine.LocalDB.ImportLocalAssetAsync(tempFile, LocalDB.ImportLocation.Move)
                ?? throw new InvalidDataException("Loading fallback texture could not be imported into LocalDB.");
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); }
            finally { await default(ToWorld); }
        }
    }
}
