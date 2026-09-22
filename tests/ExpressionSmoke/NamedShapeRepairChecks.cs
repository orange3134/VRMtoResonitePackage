using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Vrchat;

internal static class NamedShapeRepairChecks
{
    public static async Task Run(Slot parent)
    {
        var root = parent.AddSlot("Named shape repair");
        try
        {
            var source = new MeshX();
            source.AddVertex(new float3(0, 0, 0));
            source.AddVertex(new float3(1, 0, 0));
            source.AddVertex(new float3(0, 1, 0));
            source.AddSubmesh<TriangleSubmesh>().AddTriangle(0, 1, 2);
            source.AddBlendShape("Existing").AddFrame(1).RawPositions[0] = new float3(0, 0.1f, 0);
            var uri = await root.Engine.LocalDB.SaveAssetAsync(source);
            await default(ToWorld);
            SkinnedMeshRenderer Mesh(Slot slot)
            {
                var provider = slot.AttachComponent<StaticMesh>(); provider.URL.Value = uri;
                var renderer = slot.AttachComponent<SkinnedMeshRenderer>(); renderer.Mesh.Target = provider;
                return renderer;
            }
            var descriptor = root.AddSlot("Descriptor");
            var face = Mesh(descriptor.AddSlot("Face"));
            var sibling = Mesh(descriptor.AddSlot("Other").AddSlot("Face"));
            var ambiguousA = Mesh(descriptor.AddSlot("Ambiguous"));
            var ambiguousB = Mesh(descriptor.AddSlot("Ambiguous"));
            var outside = Mesh(root.AddSlot("Face"));
            var renderers = new[] { face, sibling, ambiguousA, ambiguousB, outside };
            for (int i = 0; i < 7200 && renderers.Any(r => r.MeshBlendshapeCount != 1 || r.BlendShapeWeights.Count != 1); i++) await default(NextUpdate);
            Check(renderers.All(r => r.MeshBlendshapeCount == 1 && r.BlendShapeWeights.Count == 1), "fixture meshes loaded");
            face.GetBlendShape("Existing").Value = 0.37f;
            var avatar = new VrchatAvatar();
            string[] expected = { "Existing", "Stripped", "Unreferenced" };
            avatar.FbxBlendShapeNames["Face"] = expected;
            avatar.FbxBlendShapeNames["Ambiguous"] = expected;
            var clip = new ExpressionClip { Id = "test" };
            foreach (string path in new[] { "Face", "Ambiguous", "Missing/Face" })
                foreach (string name in new[] { "Stripped", "NotInFbx" })
                    clip.Curves.Add(new ExpressionCurve { Binding = new(path, name) });
            avatar.Expressions.Clips.Add(clip);
            int repaired = await VrchatBlendShapeRepair.Apply(root, avatar, new Dictionary<Slot, string>(),
                new Dictionary<string, Slot>(), expressionRoot: descriptor);
            for (int i = 0; i < 7200 && face.MeshBlendshapeCount != 2; i++) await default(NextUpdate);
            Check(repaired == 1 && face.MeshBlendshapeCount == 2, "only exact unique expression path repaired");
            Check(face.TryGetBlendShape("Stripped") != null && face.TryGetBlendShape("NotInFbx") == null &&
                face.TryGetBlendShape("Unreferenced") == null, "only requested source shapes restored");
            Check(Math.Abs(face.GetBlendShapeWeight("Existing") - 0.37f) < 0.0001f, "existing weight retained");
            Check(renderers.Skip(1).All(r => r.MeshBlendshapeCount == 1), "same names and ambiguous paths untouched");
            Check(face.Mesh.Asset.Data.GetBlendShape(1).Frames.Single().RawPositions.All(p => p == float3.Zero), "restored frame is empty");
            Check(await VrchatBlendShapeRepair.Apply(root, avatar, new Dictionary<Slot, string>(),
                new Dictionary<string, Slot>(), expressionRoot: descriptor) == 0, "repair is idempotent");
            avatar.ModelBlendShapeNamesByPath[new(null, "Face")] = new[] { "eye.Existing", "Stripped", "Unreferenced" };
            avatar.ModelBlendShapeAliasesByPath[new(null, "Face")] = new Dictionary<string, string> { ["Existing"] = "eye.Existing" };
            var paths = new Dictionary<Slot, string> { [face.Slot] = "RootNode/Face" };
            Check(await VrchatBlendShapeRepair.Apply(root, avatar, new Dictionary<Slot, string>(),
                new Dictionary<string, Slot>(), paths, descriptor) == 1, "channel name restored on the correct source path");
            for (int i = 0; i < 7200 && face.BlendShapeName(0) != "eye.Existing"; i++) await default(NextUpdate);
            Check(face.BlendShapeName(0) == "eye.Existing" && Math.Abs(face.GetBlendShapeWeight("eye.Existing") - 0.37f) < 0.0001f,
                "canonical name retains weight and original index");
            Check(face.Mesh.Asset.Data.GetBlendShape(0).Frames.Single().RawPositions[0] == new float3(0, 0.1f, 0),
                "channel rename preserves actual vertex deltas instead of inserting an empty frame");
            Check(renderers.Skip(1).All(r => r.BlendShapeName(0) == "Existing"), "same-named other meshes remain unchanged");
            var conflict = new MeshX(source); conflict.AddBlendShape("eye.Existing").AddFrame(1);
            Check(VrchatBlendShapeRepair.RestoreChannelNames(conflict, avatar.ModelBlendShapeAliasesByPath[new(null, "Face")]) == -1 &&
                conflict.GetBlendShape(0).Name == "Existing", "ambiguous rename is atomic and leaves original geometry intact");
            Console.WriteLine("PASS: named stripped-shape repair preserves weights, source identity, exact paths and ambiguity");
        }
        finally { root.Destroy(); }
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
