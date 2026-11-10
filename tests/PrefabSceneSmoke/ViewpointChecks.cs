using System.Reflection;
using Elements.Core;
using FrooxEngine;
using VrmToResonitePackage.Vrchat;

internal static class ViewpointChecks
{
    public static void Run(Slot root)
    {
        var assembly = typeof(VrchatAvatar).Assembly;
        var resolve = assembly.GetType("VrmToResonitePackage.Vrchat.VrchatSceneSetup")!
            .GetMethod("ResolveViewPosition", BindingFlags.Static | BindingFlags.NonPublic)!;
        var avatar = new VrchatAvatar { FbxGuid = "body", FbxImportScale = .01f,
            ViewPosition = new System.Numerics.Vector3(.02f, .735f, .072f) };
        var sources = new Dictionary<Slot, string>();
        float3? Resolve(Slot descriptor) => (float3?)resolve.Invoke(null, new object[] { avatar, descriptor, sources });
        var parent = root.AddSlot("Viewpoint placement");
        parent.LocalPosition = new float3(1, 2, 3);
        parent.LocalRotation = floatQ.Euler(12, 73, 5);
        parent.LocalScale = new float3(2, 3, 4);
        var descriptor = parent.AddSlot("Descriptor");
        descriptor.LocalPosition = new float3(.1f, .2f, .3f);
        descriptor.LocalRotation = floatQ.Euler(5, 15, 25);
        descriptor.LocalScale = new float3(.8f, 1.2f, 1.1f);
        float3 expected = descriptor.LocalPointToGlobal(new float3(.02f, .735f, .072f));
        Near(Resolve(descriptor), expected, "Authored descriptor viewpoint retains translation, rotation and nonuniform scale");
        sources[descriptor] = "body";
        descriptor.LocalScale *= .01f;
        Near(Resolve(descriptor), expected, "Imported descriptor viewpoint crosses the centimeter boundary once");
        avatar.AdditionalFbxs.Add(new VrchatFbxAsset { Guid = "other", ImportScale = .1f });
        descriptor.LocalScale *= 10f;
        sources[descriptor] = "other";
        Near(Resolve(descriptor), expected, "Additional model descriptor uses its own import units");
        var actual = Resolve(descriptor);
        descriptor.Destroy();
        Check(Resolve(descriptor) == null && Resolve(null) == null,
            "Missing descriptor identity retains inferred viewpoint fallback");

        // The capture must survive root collapse. The full avatar setup uses this world point
        // even when both eye pivots exist far behind the visible eyes (NoraMiaree).
        var setupType = assembly.GetType("VrmToResonitePackage.AvatarSetupOptions")!;
        var options = Activator.CreateInstance(setupType)!;
        setupType.GetProperty("ViewPosition")!.SetValue(options, actual);
        var compute = assembly.GetType("VrmToResonitePackage.AvatarSetup")!
            .GetMethod("ComputeEyePosition", BindingFlags.Static | BindingFlags.NonPublic)!;
        var rig = parent.AttachComponent<BipedRig>();
        rig[Renderite.Shared.BodyNode.LeftEye] = parent.AddSlot("Left eye pivot");
        rig[Renderite.Shared.BodyNode.RightEye] = parent.AddSlot("Right eye pivot");
        float3 Compute() => (float3)compute.Invoke(null, new object[] { rig, null, float3.Up, float3.Forward, options })!;
        Near(Compute(), expected, "Authored viewpoint takes precedence over eye pivot positions without auto offsets");
        setupType.GetProperty("ViewForward")!.SetValue(options, -.02f);
        setupType.GetProperty("ViewUp")!.SetValue(options, .01f);
        Near(Compute(), expected + new float3(0, .01f, -.02f), "Explicit viewpoint offsets adjust the authored viewpoint in meters");
        setupType.GetProperty("ViewPosition")!.SetValue(options, null);
        Near(Compute(), parent.GlobalPosition + new float3(0, .01f, -.02f),
            "Absent authored viewpoint retains the eye-based path for VRM and incomplete descriptors");
        avatar.ViewPosition = null;
        Check(Resolve(parent) == null, "Missing ViewPosition retains fallback");
        avatar.ViewPosition = new System.Numerics.Vector3(float.NaN, 1, 0);
        Check(Resolve(parent) == null, "Nonfinite authored viewpoints retain fallback");
        parent.Destroy();
    }

    private static void Near(float3? actual, float3 expected, string label) =>
        Check(actual.HasValue && MathX.Distance(actual.Value, expected) < .00001f, label);

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine("PASS: " + label);
    }
}
