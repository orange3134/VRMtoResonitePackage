using Elements.Core;
using FrooxEngine;
using FrooxEngine.CommonAvatar;
using FrooxEngine.ProtoFlux;
using FrooxEngine.Store;
using Renderite.Shared;
using SkyFrost.Base;
using VrmToResonitePackage;

internal static class GeneratedAvatarChecks
{
    public static async Task Run(Slot parent, string artifacts)
    {
        var avatar = parent.AddSlot("Generated avatar helpers");
        Slot restored = null;
        try
        {
            Check(!typeof(AvatarSetup).Assembly.GetManifestResourceNames().Any(n => n.EndsWith(".resonitepackage")),
                "converter embeds no avatar helper packages");
            AvatarSetup.EnsureAvatarRootIdentification(avatar);
            AvatarSetup.EnsureAvatarRootIdentification(avatar);
            Check(avatar.Children.Count(s => s.Name == "Avatar Root Identification") == 1, "identification is idempotent");
            var rig = avatar.AttachComponent<BipedRig>();
            foreach (BodyNode bone in new[] { BodyNode.Hips, BodyNode.Spine, BodyNode.Head,
                BodyNode.LeftUpperArm, BodyNode.LeftLowerArm, BodyNode.LeftHand,
                BodyNode.RightUpperArm, BodyNode.RightLowerArm, BodyNode.RightHand,
                BodyNode.LeftUpperLeg, BodyNode.LeftLowerLeg, BodyNode.LeftFoot,
                BodyNode.RightUpperLeg, BodyNode.RightLowerLeg, BodyNode.RightFoot })
                rig[bone] = avatar.AddSlot(bone.ToString());
            rig[BodyNode.Head].GlobalPosition = new float3(0, 1.6f, 0);
            var head = avatar.AddSlot("Head pose");
            head.LocalPosition = new float3(0, 1.6f, 0);
            head.AttachComponent<AvatarPoseNode>().Node.Value = BodyNode.Head;
            var meshSlot = avatar.AddSlot("Body mesh");
            var renderer = meshSlot.AttachComponent<MeshRenderer>();
            var pendingMesh = meshSlot.AttachComponent<StaticMesh>();
            renderer.Mesh.Target = pendingMesh;
            await MeshLoadingSetup.Apply(avatar);
            await Frames(60);
            var standIn = avatar.FindChild("<color=#00ffff>Avatar Settings</color>")
                .FindChild("Avatar Loading Display").FindChild("LoadingStandin");
            Check(!renderer.Enabled && standIn.ActiveSelf, "pending mesh shows stand-in and hides avatar renderer");
            Check(avatar.GetComponentInChildren<MeshRendererLoadStatus>().Renderers.Count == 1,
                "stand-in renderer is excluded from the mesh loading gate");
            await Verify(avatar, standIn, parent);
            renderer.Mesh.Target = meshSlot.AttachComponent<QuadMesh>();
            pendingMesh.Destroy();
            for (int i = 0; i < 1200 && !renderer.Enabled; i++) await default(NextUpdate);
            Check(renderer.Enabled && !standIn.ActiveSelf, "loaded mesh hides stand-in and enables avatar renderer");

            // Save while dequipped: no synthetic wearer references should escape into the package.
            var graph = avatar.SaveObject(DependencyHandling.CollectAssets);
            var record = RecordHelper.CreateForObject<SkyFrost.Base.Record>(avatar.Name, avatar.World.LocalUser.MachineID, null);
            string package = Path.Combine(artifacts, "GeneratedAvatar.resonitepackage");
            var engine = avatar.Engine;
            await default(ToBackground);
            using (var stream = File.Create(package))
                await PackageCreator.BuildPackage(engine, record, graph, stream, includeVariants: false);
            await default(ToWorld);
            Check(PackageInspector.Inspect(package, verbose: false) == 0, "generated helper package inspection succeeds");
            restored = parent.AddSlot("Restored generated avatar");
            await PackageImporter.ImportPackage(package, restored);
            await default(ToWorld);
            await Frames(120);
            var restoredStandIn = restored.FindChild("<color=#00ffff>Avatar Settings</color>")
                .FindChild("Avatar Loading Display").FindChild("LoadingStandin");
            // Exercise the hidden thumbnail graph as if a fresh client was still loading meshes.
            var activeDriver = restoredStandIn.ActiveSelf_Field.ActiveLink as ISyncRef;
            if (activeDriver != null) activeDriver.Target = null;
            restoredStandIn.ActiveSelf = true;
            await Frames(30);
            await Verify(restored, restoredStandIn, parent);
        }
        finally { restored?.Destroy(); avatar.Destroy(); }
    }

    private static async Task Verify(Slot avatar, Slot standIn, Slot parent)
    {
        Check(avatar.GetComponentsInChildren<ProtoFluxNode>().All(n => n.Group?.IsValid == true), "generated Flux groups are valid");
        Check(avatar.GetComponent<DynamicVariableSpace>().TryReadValue<Slot>("AvatarRoot", out var root) && root == avatar,
            "AvatarRoot reference points at this avatar");
        var visual = standIn.FindChild("Visual");
        var head = avatar.FindChild("Head pose");
        var virtualParent = standIn.GetComponent<VirtualParent>();
        Check(virtualParent.OverrideParent.Target == head && virtualParent.VirtualChild == standIn,
            "thumbnail follows AvatarPoseNode.Head");
        Check(Math.Abs(visual.LocalScale.x - 0.4f) < 0.001f, "thumbnail size is one quarter of saved head height");
        var transform = visual.GetComponent<RenderTransformOverride>();
        var renderer = visual.GetComponent<MeshRenderer>();
        var material = (UnlitMaterial)renderer.Materials[0];
        Check(renderer.Mesh.Target is QuadMesh && material.BlendMode.Value == BlendMode.Alpha &&
            material.Sidedness.Value == Sidedness.Double, "thumbnail retains its quad and transparent double-sided material");
        var texture = standIn.FindChild("UserIconTex").GetComponent<StaticTexture2D>();
        var fallback = standIn.GetComponentsInChildren<FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.ValueObjectInput<Uri>>().Single();
        Check((fallback.Value.Value?.Scheme is "local" or "resdb") && texture.URL.Value == fallback.Value.Value,
            "offline profile uses an imported fallback texture");
        var assigner = avatar.FindChild("Avatar Root Identification").GetComponent<AvatarUserReferenceAssigner>();
        Check(assigner.References.Count == 1, "one wearer reference is assigned by avatar equip events");
        assigner.OnDequip(null); await Frames(20);
        Check(!Value(avatar, "AvatarWorn") && !Value(avatar, "AvatarWornLocal") && transform.ScaleOverride.Value == null,
            "unassigned avatar stays unworn even beneath UserRoot");
        var equipSlot = avatar.AttachComponent<AvatarObjectSlot>();
        assigner.OnEquip(equipSlot); await Frames(30);
        Check(Value(avatar, "AvatarWorn") && Value(avatar, "AvatarWornLocal") && transform.ScaleOverride.Value == float3.Zero,
            "local equip publishes worn flags and hides thumbnail only in UserView");
        Check(transform.Context.Value == RenderingContext.UserView, "mirror and third-person rendering remain visible");
        avatar.SetParent(avatar.World.RootSlot, false); await Frames(30);
        Check(!Value(avatar, "AvatarWorn") && !Value(avatar, "AvatarWornLocal"),
            "stale assigned user does not mark a detached avatar as worn");
        avatar.SetParent(parent, false); await Frames(30);
        Check(Value(avatar, "AvatarWornLocal"), "reattachment restores wearer detection");
        assigner.OnDequip(equipSlot); await Frames(30);
        Check(!Value(avatar, "AvatarWorn") && transform.ScaleOverride.Value == null, "dequip clears flags and first-person override");
        equipSlot.Destroy();
        float3 previousHeadPosition = head.LocalPosition;
        head.LocalPosition += new float3(0.2f, 0.1f, 0);
        await Frames(20);
        Check((standIn.GlobalPosition - head.LocalPointToGlobal(virtualParent.LocalPosition.Value)).Magnitude < 0.001f,
            "thumbnail follows head motion");
        head.LocalPosition = previousHeadPosition;
    }

    private static bool Value(Slot root, string name)
    {
        Check(root.GetComponent<DynamicVariableSpace>().TryReadValue<bool>(name, out var value), "worn variable exists: " + name);
        return value;
    }
    private static async Task Frames(int count) { for (int i = 0; i < count; i++) await default(NextUpdate); }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}