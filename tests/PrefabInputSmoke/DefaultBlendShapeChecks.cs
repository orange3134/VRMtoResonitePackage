using System.Reflection;
using System.Text;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class DefaultBlendShapeChecks
{
    public static void Run(Func<string, string, string, string> asset)
    {
        CheckChannelNames(asset);
        const string modelGuid = "adef0000000000000000000000000001";
        string model = asset("Assets/DefaultWeightChannels.fbx", modelGuid, "");
        foreach (bool reverse in new[] { false, true })
        {
            WriteFbx(model, reverse);
            var channels = UnityFbxBlendShapeDefaults.Read(model);
            Require(channels.Count == 3 && channels.Single(c => c.RendererPath == "RootNode/Left/Body").Weight == 25 &&
                channels.Single(c => c.RendererPath == "RootNode/Right/Body").Weight == 75 &&
                channels.Single(c => c.RendererPath == "RootNode/Twin/Body").Weight == 25,
                "FBX connections scope identical channel names independently of object order, including shared geometry");
            var resolver = new UnityModelFileIdResolver(null);
            typeof(UnityModelFileIdResolver).GetField("_defaultWeightChannels", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(resolver, channels);
            var scene = new Assimp.Scene();
            foreach (string branch in new[] { "Right", "Zero", "Left", "Twin" })
            {
                var mesh = new Assimp.Mesh();
                mesh.MeshAnimationAttachments.Add(new Assimp.MeshAnimationAttachment { Name = "Smile" });
                scene.Meshes.Add(mesh);
                var node = new Assimp.Node("Body");
                node.MeshIndices.Add(scene.Meshes.Count - 1);
                typeof(UnityModelFileIdResolver).GetMethod("AddBlendShapeNames", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(resolver, new object[] { scene, node, "RootNode/" + branch + "/Body" });
            }
            Require(resolver.BlendShapeDefaultWeightsByPath.Count == 3 &&
                resolver.BlendShapeDefaultWeightsByPath["RootNode/Left/Body"].Single() == 25 &&
                resolver.BlendShapeDefaultWeightsByPath["RootNode/Right/Body"].Single() == 75,
                "Model resolution keeps per-path weights without consuming a namesake channel on another renderer");

            string prefab = asset("Assets/DefaultWeights.prefab", "adef0000000000000000000000000002", "%YAML 1.1\n");
            using var package = UnityPackage.Open(prefab);
            ((Dictionary<string, UnityModelFileIdResolver>)typeof(UnityPackage).GetField("_modelIds",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(package)!)[modelGuid] = resolver;
            var avatar = new VrchatAvatar { FbxGuid = modelGuid };
            Call("ParseFbxBlendShapeNames", package, avatar);
            Require(avatar.FbxBlendShapeDefaultWeights[new(modelGuid, "Left/Body")].Single() == 25 &&
                avatar.FbxBlendShapeDefaultWeights[new(modelGuid, "Right/Body")].Single() == 75,
                "Avatar metadata preserves the full renderer path from the model resolver");
            foreach (string branch in new[] { "Right", "Left", "Zero" })
            {
                var copy = new VrchatMeshCopy(modelGuid, "Body", "Renamed", true, true)
                {
                    SourcePath = "RootNode/" + branch + "/Body",
                    Transform = new VrchatPrefabTransform { GameObjectKey = branch, Key = branch + ":transform" },
                };
                avatar.MeshCopies.Add(copy);
            }
            var explicitZero = new VrchatRendererMaterials { FbxGuid = modelGuid, RendererGameObjectName = "Renamed",
                PrefabObjectKey = "Right", SourcePath = "RootNode/Right/Body" };
            explicitZero.InitialBlendShapes.Add((0, 0));
            avatar.RendererMaterials.Add(explicitZero);
            Call("ApplyFbxDefaultBlendShapeWeights", avatar);
            Require(avatar.RendererMaterials.Single(r => r.PrefabObjectKey == "Left").InitialBlendShapes.Single().Weight == 25 &&
                explicitZero.InitialBlendShapes.Single().Weight == 0 &&
                !avatar.RendererMaterials.Any(r => r.PrefabObjectKey == "Zero") &&
                avatar.RendererMaterials.Single(r => r.PrefabObjectKey == null && r.SourcePath == "RootNode/Right/Body")
                    .InitialBlendShapes.Single().Weight == 75,
                "Renamed copies inherit only their source path, explicit zero wins, and untouched models retain defaults");
            avatar.ModelBlendShapeNames[new(modelGuid, "Body")] = new[] { "Wrong branch" };
            avatar.ModelBlendShapeNamesByPath[new(modelGuid, "Right/Body")] = new[] { "Right only" };
            Require(avatar.BlendShapeNamesFor(modelGuid, "Body", rendererPath: "Right/Body").Single() == "Right only" &&
                avatar.BlendShapeNamesFor(modelGuid, "Body", rendererPath: "Missing/Body") == null,
                "Indexed shape repair uses the matching path and cannot fall back to a namesake");
        }
    }

    private static void CheckChannelNames(Func<string, string, string, string> asset)
    {
        string path = asset("Assets/ChannelNames.fbx", "adef0000000000000000000000000003", "");
        foreach (bool reverse in new[] { false, true })
        {
            WriteFbx(path, reverse, channelNames: true);
            var channels = UnityFbxBlendShapeDefaults.Read(path);
            Require(channels.Count == 3 && channels.All(c => c.ShapeName == "Smile") &&
                channels.Single(c => c.RendererPath == "RootNode/Left/Body").Name == "Left.Smile" &&
                channels.Single(c => c.RendererPath == "RootNode/Twin/Body").Name == "Left.Smile",
                "Shape geometry connects to the correct named channel and shared model regardless of object order");
            var resolver = new UnityModelFileIdResolver(null);
            typeof(UnityModelFileIdResolver).GetField("_defaultWeightChannels", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(resolver, channels);
            var scene = new Assimp.Scene();
            foreach (string branch in new[] { "Left", "Right", "Twin", "Missing" })
            {
                var mesh = new Assimp.Mesh();
                mesh.MeshAnimationAttachments.Add(new Assimp.MeshAnimationAttachment { Name = "Smile.Smile" });
                scene.Meshes.Add(mesh);
                var node = new Assimp.Node("Body"); node.MeshIndices.Add(scene.Meshes.Count - 1);
                typeof(UnityModelFileIdResolver).GetMethod("AddBlendShapeNames", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(resolver, new object[] { scene, node, "RootNode/" + branch + "/Body" });
            }
            Require(resolver.BlendShapeNamesByPath["RootNode/Left/Body"].Single() == "Left.Smile" &&
                resolver.BlendShapeNamesByPath["RootNode/Right/Body"].Single() == "Right.Smile" &&
                resolver.BlendShapeNamesByPath["RootNode/Missing/Body"].Single() == "Smile" &&
                resolver.BlendShapeAliasesByPath["RootNode/Right/Body"]["Smile"] == "Right.Smile" &&
                resolver.BlendShapeDefaultWeightsByPath["RootNode/Right/Body"].Single() == 75,
                "Canonical channel names, aliases and default weights remain scoped to model paths");
        }
        Dictionary<string, string> Map(string[] imported, params (string, string)[] channels) =>
            VrmToResonitePackage.BlendShapeNameNormalizer.ChannelAliases(imported, channels);
        Require(Map(new[] { "Smile" }, ("eye.Smile", "Smile"))["Smile"] == "eye.Smile", "connected channel alias restored");
        Require(Map(new[] { "Smile" }, ("eye.Smile", null)).Count == 0, "dotted suffix alone never establishes identity");
        Require(Map(new[] { "Smile" }, ("eye.Smile", "Smile"), ("mouth.Smile", "Smile")).Count == 0, "ambiguous geometry alias rejected");
        Require(Map(new[] { "Smile", "eye.Smile" }, ("eye.Smile", "Smile")).Count == 0, "existing channel name is not overwritten");
        Require(Map(new[] { "Smile" }, ("Smile", "Other"), ("eye.Smile", "Smile")).Count == 0, "exact channel identity takes priority");
    }

    private static void Call(string method, params object[] arguments) => typeof(VrchatAvatarParser)
        .GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments);

    private sealed record Node(string Name, object[] Properties, params Node[] Children);

    private static void WriteFbx(string path, bool reverse, bool channelNames = false)
    {
        // Binary FBX nodes exercise the production reader. Channels deliberately have the
        // same name; their object order disagrees with the renderer traversal above.
        var objects = new List<Node>();
        var connections = new List<Node>();
        void Connect(long child, long parent) => connections.Add(new("C", new object[] { "OO", child, parent }));
        foreach (var (id, branch, percent) in new[] { (10L, "Left", 25d), (20L, "Right", 75d), (30L, "Twin", 0d) })
        {
            objects.Add(new("Model", new object[] { id, branch + "\0\u0001Model", "Null" }));
            objects.Add(new("Model", new object[] { id + 1, "Body\0\u0001Model", "Mesh" }));
            Connect(id, 0);
            Connect(id + 1, id);
            if (branch == "Twin") { Connect(12, id + 1); continue; }
            objects.Add(new("Geometry", new object[] { id + 2, "Mesh\0\u0001Geometry", "Mesh" }));
            objects.Add(new("Deformer", new object[] { id + 3, "Shape\0\u0001Deformer", "BlendShape" }));
            objects.Add(new("Deformer", new object[] { id + 4, (channelNames ? branch + "." : "") + "Smile\0\u0001Deformer", "BlendShapeChannel" },
                new Node("DeformPercent", new object[] { percent })));
            Connect(id + 2, id + 1);
            Connect(id + 3, id + 2);
            Connect(id + 4, id + 3);
            objects.Add(new("Geometry", new object[] { id + 5, "Smile\0\u0001Geometry", "Shape" }));
            Connect(id + 5, id + 4);
        }
        if (reverse) { objects.Reverse(); connections.Reverse(); }
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\u001a\0"));
        writer.Write(7400);
        WriteNode(new("Objects", Array.Empty<object>(), objects.ToArray()));
        WriteNode(new("Connections", Array.Empty<object>(), connections.ToArray()));
        writer.Write(new byte[13]);

        void WriteNode(Node node)
        {
            long start = stream.Position;
            writer.Write(0u);
            writer.Write((uint)node.Properties.Length);
            writer.Write(0u);
            byte[] name = Encoding.UTF8.GetBytes(node.Name);
            writer.Write((byte)name.Length);
            writer.Write(name);
            long propertiesStart = stream.Position;
            foreach (object value in node.Properties)
                switch (value)
                {
                    case long integer: writer.Write((byte)'L'); writer.Write(integer); break;
                    case double number: writer.Write((byte)'D'); writer.Write(number); break;
                    case string text:
                        byte[] bytes = Encoding.UTF8.GetBytes(text);
                        writer.Write((byte)'S'); writer.Write(bytes.Length); writer.Write(bytes); break;
                }
            long propertiesEnd = stream.Position;
            foreach (var child in node.Children) WriteNode(child);
            writer.Write(new byte[13]);
            long end = stream.Position;
            stream.Position = start;
            writer.Write((uint)end);
            stream.Position = start + 8;
            writer.Write((uint)(propertiesEnd - propertiesStart));
            stream.Position = end;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
