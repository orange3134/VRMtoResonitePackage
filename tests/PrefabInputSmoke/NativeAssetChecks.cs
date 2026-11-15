using System.Globalization;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Elements.Assets;
using Elements.Core;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class NativeAssetChecks
{
    public static void Run()
    {
        MeshChecks();
        BinaryChecks();
        Console.WriteLine("PASS: native Unity assets (binary subasset identity, texture pixels, mesh streams, skins and morphs)");
    }

    private static void MeshChecks()
    {
        foreach (bool wide in new[] { false, true })
        foreach (bool rigid in new[] { false, true })
        {
            var root = MeshFixture(wide, rigid);
            root["m_Shapes"]["shapes"].Items[0].Map["hasNormals"] = S(1);
            root["m_Shapes"]["vertices"].Items[0].Map["normal"] = Map(("x", S(.4f)), ("y", S(.5f)), ("z", S(.6f)));
            var mesh = UnityNativeMesh.Decode(root);
            Check(mesh.VertexCount == 4 && mesh.SubmeshCount == 2 && mesh.BoneCount == 2, "mesh dimensions");
            Check(mesh.GetVertex(2).Position == new float3(2, 3, 4) && mesh.GetVertex(2).UV0 == new float2(.25f, .75f), "independent aligned streams and half UVs");
            Check(mesh.GetSubmesh(0).RawIndicies.Take(3).SequenceEqual(new[] { 1, 2, 3 }) &&
                mesh.GetSubmesh(1).RawIndicies.Take(3).SequenceEqual(new[] { 2, 1, 0 }), "index size, byte offset, baseVertex and winding");
            Check(mesh.RawBoneBindings[0].boneIndex0 == 1 && Math.Abs(mesh.RawBoneBindings[0].weight0 - (rigid ? 1 : .75f)) < .0001f, "original bone indices and implicit rigid weights");
            Check(mesh.GetBone(1).BindPose == new float4x4(1, 0, 0, -.5f, 0, 1, 0, -1, 0, 0, 1, 0, 0, 0, 0, 1), "bind pose matrix layout");
            Check(mesh.BlendShapes.Single().Frames.Count() == 2 &&
                mesh.BlendShapes.Single().Frames.First().RawPositions[2] == new float3(.1f, .2f, .3f), "sparse multi-frame blendshape deltas");
            Check(mesh.BlendShapes.Single().Frames.First().RawNormals[2] == new float3(.4f, .5f, .6f) &&
                mesh.BlendShapes.Single().Frames.Last().RawNormals[2] == float3.Zero, "per-frame normal flags exclude unused serialized deltas");
            var bad = MeshFixture(wide, rigid);
            bad["m_SubMeshes"].Items[0].Map["indexCount"] = S(9999);
            Reject(() => UnityNativeMesh.Decode(bad), "out of range index buffer");
            bad = MeshFixture(wide, rigid);
            bad["m_VertexData"].Map["_typelessdata"] = new() { Bytes = new byte[4] };
            Reject(() => UnityNativeMesh.Decode(bad), "truncated vertex buffer");
        }
    }

    internal static YamlNode MeshFixture(bool wide, bool rigid)
    {
        var channels = Enumerable.Range(0, 14).Select(_ => Map(("dimension", S(0)))).ToArray();
        channels[0] = Channel(0, 0, 0, 3);
        channels[4] = Channel(1, 0, 1, 2);
        channels[12] = Channel(2, 0, 0, rigid ? 0 : 2);
        channels[13] = Channel(2, rigid ? 0 : 8, 10, rigid ? 1 : 2);
        using var buffer = new MemoryStream(); using var writer = new BinaryWriter(buffer);
        for (int i = 0; i < 4; i++) { writer.Write((float)i); writer.Write(i + 1f); writer.Write(i + 2f); }
        for (int i = 0; i < 4; i++) { writer.Write(BitConverter.HalfToUInt16Bits((Half).25f)); writer.Write(BitConverter.HalfToUInt16Bits((Half).75f)); }
        for (int i = 0; i < 4; i++)
        {
            if (!rigid) { writer.Write(.25f); writer.Write(.75f); writer.Write(0u); }
            writer.Write(1u);
        }
        byte[] indices = new[] { 0, 1, 2, 2, 1, 0 }.SelectMany(i => wide ? BitConverter.GetBytes(i) : BitConverter.GetBytes((ushort)i)).ToArray();
        var pose = Map(Enumerable.Range(0, 16).Select(i => ($"e{i / 4}{i % 4}", S(i % 5 == 0 ? 1 : i == 3 ? -.5f : i == 7 ? -1 : 0))).ToArray());
        return Map(("m_VertexData", Map(("m_VertexCount", S(4)), ("m_Channels", Seq(channels)), ("_typelessdata", new() { Bytes = buffer.ToArray() }))),
            ("m_IndexFormat", S(wide ? 1 : 0)), ("m_IndexBuffer", new() { Bytes = indices }),
            ("m_SubMeshes", Seq(Map(("firstByte", S(0)), ("indexCount", S(3)), ("topology", S(0)), ("baseVertex", S(1))),
                Map(("firstByte", S(wide ? 12 : 6)), ("indexCount", S(3)), ("topology", S(0)), ("baseVertex", S(0))))),
            ("m_BindPose", Seq(pose, pose)),
            ("m_Shapes", Map(("channels", Seq(Map(("name", S("Smile")), ("frameIndex", S(0)), ("frameCount", S(2))))),
                ("shapes", Seq(Map(("firstVertex", S(0)), ("vertexCount", S(1))), Map(("firstVertex", S(0)), ("vertexCount", S(1))))),
                ("fullWeights", Seq(S(50), S(100))),
                ("vertices", Seq(Map(("index", S(2)), ("vertex", Map(("x", S(.1f)), ("y", S(.2f)), ("z", S(.3f))))))))));
    }

    private static void BinaryChecks()
    {
        string root = Path.GetFullPath(Path.Combine(".tmp_verify", "native-assets", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(root, "Assets")); Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        string prefab = Path.Combine(root, "Assets", "Test.prefab"), binaryPath = Path.Combine(root, "Assets", "Generated.asset");
        const string guid = "11223344556677889900112233445566";
        File.WriteAllText(prefab + ".meta", "guid: aabbccddeeff00112233445566778899");
        File.WriteAllText(binaryPath + ".meta", "guid: " + guid);
        File.WriteAllText(prefab, $"--- !u!137 &1\nSkinnedMeshRenderer:\n  m_Materials:\n  - {{fileID: -21, guid: {guid}, type: 2}}\n  - {{fileID: 22, guid: {guid}, type: 2}}\n");
        var file = new AssetsFile { Header = new() { Version = 22, Endianness = false }, Metadata = new()
        { UnityVersion = "2022.3.22f1", TargetPlatform = 19, TypeTreeEnabled = true, TypeTreeTypes = new(),
            AssetInfos = new List<AssetFileInfo>(), ScriptTypes = new(), Externals = new(), RefTypes = new(), UserInformation = "" } };
        Add(-21, 21, Material("First", .25f));
        Add(22, 21, Material("Second", .75f));
        Add(28, 28, B("Base", "Texture2D", null, B("m_Width", "int", new(1)), B("m_Height", "int", new(2)),
            B("m_TextureFormat", "int", new(4)), B("m_ColorSpace", "int", new(1)),
            B("image data", "TypelessData", new(new byte[] { 255, 0, 0, 255, 0, 0, 255, 128 }, false))));
        Add(91, 91, B("Base", "AnimatorController", null, B("m_Name", "string", new("Selected")), Pointer("clip", -74)));
        Add(92, 91, B("Base", "AnimatorController", null, B("m_Name", "string", new("Other")), Pointer("clip", 74)));
        Add(-74, 74, B("Base", "AnimationClip", null, B("m_Name", "string", new("ClipA"))));
        Add(74, 74, B("Base", "AnimationClip", null, B("m_Name", "string", new("ClipB"))));
        using (var output = new AssetsFileWriter(binaryPath)) file.Write(output);
        byte[] before = File.ReadAllBytes(binaryPath);
        using (var package = UnityPackage.Open(prefab))
        {
            var materials = package.ReadScene(package.InputPrefab).Doc(1).Root["m_Materials"].Items;
            Check(materials[0].Guid != materials[1].Guid, "two material subassets retain distinct identity");
            var first = package.ReadScene(package.ByGuid(materials[0].Guid)).Doc(-21);
            var second = package.ReadScene(package.ByGuid(materials[1].Guid)).Doc(22);
            Check(first.Root["m_Name"].AsString() == "First" && second.Root["m_Name"].AsString() == "Second", "requested material fileID");
            Check(first.Root["m_SavedProperties"]["m_Floats"].Items[0]["_Cutoff"].AsFloat() == .25f &&
                second.Root["m_SavedProperties"]["m_Floats"].Items[0]["_Cutoff"].AsFloat() == .75f, "binary material string-keyed maps");
            var texture = package.ByGuid(first.Root["texture"].Guid);
            var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(new Uri(texture.DiskPath), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var rgba = new byte[8]; decoder.Frames[0].CopyPixels(rgba, 4, 0);
            Check(rgba.SequenceEqual(new byte[] { 255, 0, 0, 128, 0, 0, 255, 255 }), "Texture2D orientation and straight alpha");
            var controller = package.ReadController(Map(("guid", S(guid)), ("fileID", S(91))));
            Check(controller.Doc(92) == null && controller.Doc(91).Root["clip"].FileID == -74 &&
                controller.Doc(91).Root["clip"].Guid == guid && controller.Doc(-74).Root["m_Name"].AsString() == "ClipA", "controller selection and same-file clip references");
        }
        Check(before.SequenceEqual(File.ReadAllBytes(binaryPath)), "source binary remains unchanged");
        File.WriteAllText(binaryPath, "--- !u!21 &-21\nMaterial:\n  m_Name: FirstYaml\n--- !u!21 &22\nMaterial:\n  m_Name: SecondYaml\n  m_Parent: {fileID: -21}\n");
        using (var package = UnityPackage.Open(prefab))
        {
            var materials = package.ReadScene(package.InputPrefab).Doc(1).Root["m_Materials"].Items;
            var second = package.ReadScene(package.ByGuid(materials[1].Guid)).Doc(22);
            Check(second.Root["m_Name"].AsString() == "SecondYaml" && second.Root["m_Parent"].Guid == materials[0].Guid,
                "YAML subassets select exact fileID and qualify same-file parent references");
        }
        YamlAnimationChecks(prefab, binaryPath, guid);

        AssetTypeValueField Material(string name, float cutoff)
        {
            var pair = B("data", "pair", null, B("first", "string", new("_Cutoff")), B("second", "float", new(cutoff)));
            var array = B("Array", "Array", new(AssetValueType.Array), pair);
            array.TemplateField.IsArray = true;
            array.TemplateField.Children = new() { B("size", "int", new(1)).TemplateField, pair.TemplateField };
            return B("Base", "Material", null, B("m_Name", "string", new(name)), Pointer("texture", 28),
                B("m_SavedProperties", "UnityPropertySheet", null, B("m_Floats", "map", null, array)));
        }

        void Add(long id, int type, AssetTypeValueField field)
        {
            if (!file.Metadata.TypeTreeTypes.Any(t => t.TypeId == type)) file.Metadata.TypeTreeTypes.Add(TypeTree(field.TemplateField, type));
            var info = AssetFileInfo.Create(file, id, type); info.SetNewData(field); file.Metadata.AssetInfos.Add(info);
        }
    }

    private static void YamlAnimationChecks(string prefab, string assetPath, string guid)
    {
        string yaml = """
--- !u!91 &91
AnimatorController:
  m_AnimatorLayers:
  - m_Name: Base
    m_StateMachine: {fileID: 100}
  - m_Name: Expressions
    m_DefaultWeight: 1
    m_StateMachine: {fileID: 200}
--- !u!1107 &100
AnimatorStateMachine:
  m_DefaultState: {fileID: 10}
  m_ChildStates:
  - m_State: {fileID: 10}
--- !u!1107 &200
AnimatorStateMachine:
  m_DefaultState: {fileID: 20}
  m_ChildStates:
  - m_State: {fileID: 20}
--- !u!1102 &10
AnimatorState:
  m_Motion: {fileID: -74}
--- !u!1102 &20
AnimatorState:
  m_Motion: {fileID: 206}
  m_Transitions:
  - {fileID: 30}
--- !u!1101 &30
AnimatorStateTransition:
  m_DstState: {fileID: 20}
  m_Conditions: []
--- !u!206 &206
BlendTree:
  m_Childs:
  - m_Motion: {fileID: -74}
  - m_Motion: {fileID: 74}
  - m_Motion: {fileID: 0}
  - m_Motion: {fileID: 74, guid: ffeeddccbbaa99887766554433221100}
  - m_Motion: {fileID: 74}
""";
        foreach (long id in new long[] { -74, 74 })
            yaml += $"\n--- !u!74 &{id}\nAnimationClip:\n  m_Name: Clip{id}\n  m_FloatCurves:\n  - attribute: blendShape.Smile\n    path: Face/Body\n    classID: 137\n    curve:\n      m_Curve:\n      - time: 0\n        value: {(id < 0 ? 25 : 75)}\n";
        File.WriteAllText(assetPath, yaml);
        using (var package = UnityPackage.Open(prefab))
        {
            var descriptor = UnityYaml.ParseFlatDocument($"baseAnimationLayers:\n- type: 5\n  animatorController: {{fileID: 91, guid: {guid}}}\n");
            var avatar = new VrchatAvatar();
            VrchatAnimatorFaceParser.Apply(package, descriptor, avatar);
            Check(avatar.BaseExpressionInitialValues.TryGetValue(new("Face/Body", "Smile"), out float value) && value == .25f,
                "YAML same-file clip reaches Animator base-expression parsing");
            var expressions = VrchatExpressionParser.Parse(package, descriptor);
            Check(expressions.Clips.Count == 2 && expressions.Clips.Select(c => c.Curves.Single().Keys.Single().Value).Order().SequenceEqual(new[] { .25f, .75f }),
                "YAML direct and BlendTree clips reach FaceEmo with distinct subasset identities");
            var scene = package.ReadController(descriptor["baseAnimationLayers"].Items[0]["animatorController"]);
            Check(scene.Doc(100).Root["m_DefaultState"].Guid == null && scene.Doc(20).Root["m_Motion"].Guid == null &&
                scene.Doc(20).Root["m_Transitions"].Items[0].Guid == null && scene.Doc(30).Root["m_DstState"].Guid == null,
                "YAML states, transitions and BlendTrees keep local identities");
            var children = scene.Doc(206).Root["m_Childs"].Items;
            Check(children[0]["m_Motion"].Guid == guid && children[0]["m_Motion"].FileID == -74 &&
                children[2]["m_Motion"].Guid == null && children[3]["m_Motion"].Guid == "ffeeddccbbaa99887766554433221100",
                "YAML clip qualification preserves signed IDs, null and external references");
        }
        Check(File.ReadAllText(assetPath) == yaml, "source YAML animation asset remains unchanged");
        Console.WriteLine("PASS: YAML native animation clips, FaceEmo and local graph references");
    }

    private static TypeTreeType TypeTree(AssetTypeTemplateField root, int id)
    {
        var nodes = new List<TypeTreeNode>();
        using var strings = new MemoryStream();
        uint String(string value) { uint offset = (uint)strings.Position; strings.Write(System.Text.Encoding.UTF8.GetBytes(value + "\0")); return offset; }
        void Add(AssetTypeTemplateField f, byte level)
        {
            nodes.Add(new() { Level = level, Version = 1, Index = (uint)nodes.Count, NameStrOffset = String(f.Name),
                TypeStrOffset = String(f.Type), ByteSize = f.Type switch { "int" => 4, "SInt64" => 8, "char" => 1, _ => -1 },
                TypeFlags = f.IsArray || f.Type == "TypelessData" ? TypeTreeNodeFlags.Array : 0, MetaFlags = f.IsAligned ? 0x4000u : 0 });
            foreach (var child in f.Children) Add(child, (byte)(level + 1));
            if (f.Type == "string") Add(new() { Name = "Array", Type = "Array", IsArray = true, IsAligned = true, Children = new()
                { new() { Name = "size", Type = "int", Children = new() }, new() { Name = "data", Type = "char", Children = new() } } }, (byte)(level + 1));
            if (f.Type == "TypelessData")
            {
                Add(new() { Name = "size", Type = "int", Children = new() }, (byte)(level + 1));
                Add(new() { Name = "data", Type = "UInt8", Children = new() }, (byte)(level + 1));
            }
        }
        Add(root, 0);
        return new() { TypeId = id, ScriptTypeIndex = 0xffff, ScriptIdHash = Hash128.NewBlankHash(), TypeHash = Hash128.NewBlankHash(),
            TypeDependencies = Array.Empty<int>(), TypeBlob = new() { Nodes = nodes, StringBufferBytes = strings.ToArray() } };
    }

    private static AssetTypeValueField Pointer(string name, long id) => B(name, "PPtr<Object>", null, B("m_FileID", "int", new(0)), B("m_PathID", "SInt64", new(id)));
    private static AssetTypeValueField B(string name, string type, AssetTypeValue value, params AssetTypeValueField[] children) => new()
    {
        TemplateField = new() { Name = name, Type = type, ValueType = value?.ValueType ?? AssetValueType.None,
            HasValue = value != null, Version = 1, IsArray = type == "TypelessData", IsAligned = type is "string" or "TypelessData", Children = children.Select(c => c.TemplateField).ToList() },
        Value = value, Children = children.ToList(),
    };
    private static YamlNode Channel(int stream, int offset, int format, int dimension) => Map(("stream", S(stream)), ("offset", S(offset)), ("format", S(format)), ("dimension", S(dimension)));
    private static YamlNode S(object value) => new() { ScalarValue = Convert.ToString(value, CultureInfo.InvariantCulture) };
    private static YamlNode Seq(params YamlNode[] values) => new() { Seq = values.ToList() };
    private static YamlNode Map(params (string Key, YamlNode Value)[] values) => new() { Map = values.ToDictionary(v => v.Key, v => v.Value) };
    private static void Check(bool condition, string label) { if (!condition) throw new Exception("Native asset regression: " + label); }
    private static void Reject(Action action, string label) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Expected rejection: " + label); }
}
