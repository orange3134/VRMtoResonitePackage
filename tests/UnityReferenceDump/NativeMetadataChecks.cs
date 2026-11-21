using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using VrmToResonitePackage.Unity;

// Invented minimal SerializedFile type trees, using the same fixture-writing
// approach as PrefabInputSmoke/NativeAssetChecks. No external model data.
internal static class NativeMetadataChecks
{
    public static void Run(string directory)
    {
        if (Directory.Exists(directory)) throw new IOException("Use a new fixture directory.");
        Directory.CreateDirectory(Path.Combine(directory, "Assets"));
        Directory.CreateDirectory(Path.Combine(directory, "ProjectSettings"));
        const string prefabGuid = "11111111111111111111111111111111", meshGuid = "22222222222222222222222222222222";
        string prefab = Path.Combine(directory, "Assets", "Selected.prefab"), asset = Path.Combine(directory, "Assets", "Generated.asset");
        File.WriteAllText(prefab + ".meta", "guid: " + prefabGuid);
        File.WriteAllText(asset + ".meta", "guid: " + meshGuid);
        var file = new AssetsFile { Header = new() { Version = 22, Endianness = false }, Metadata = new()
        { UnityVersion = "2022.3.22f1", TargetPlatform = 19, TypeTreeEnabled = true, TypeTreeTypes = new(),
            AssetInfos = new List<AssetFileInfo>(), ScriptTypes = new(), Externals = new(), RefTypes = new(), UserInformation = "" } };
        // The selected signed fileID is not the first Mesh. Names have significant
        // punctuation/non-ASCII and must retain channel order, not be normalized.
        Add(43, 43, Mesh("Wrong"));
        Add(-4300001, 43, Mesh("Smile.left", "まばたき"));
        Add(44, 43, Mesh());
        Add(21, 21, Field("Base", "Material", null, Field("m_Name", "string", new("Not a mesh"))));
        using (var output = new AssetsFileWriter(asset)) file.Write(output);
        byte[] binary = File.ReadAllBytes(asset);
        Check(-4300001, new[] { "Smile.left", "まばたき" }, new[] { 25f, 75f });
        Check(43, new[] { "Wrong" }, new[] { 25f });
        Check(44, Array.Empty<string>(), Array.Empty<float>());
        Reject(21); Reject(999);
        if (!binary.SequenceEqual(File.ReadAllBytes(asset))) throw new Exception("Binary input changed.");
        File.WriteAllText(asset, "--- !u!43 &-4300001\nMesh:\n  m_Shapes:\n    channels:\n    - name: Smile.left\n    - name: まばたき\n");
        string yaml = File.ReadAllText(asset);
        Check(-4300001, new[] { "Smile.left", "まばたき" }, new[] { 25f, 75f });
        if (File.ReadAllText(asset) != yaml) throw new Exception("YAML input changed.");
        Console.WriteLine("PASS: native v2 metadata: binary/YAML, signed subasset ID, channel order, stale weights, empty mesh, missing/wrong-type rejection, unchanged inputs");

        object Capture(long id)
        {
            File.WriteAllText(prefab, $$"""
--- !u!1 &1
GameObject:
  m_Name: Selected
  m_IsActive: 1
  m_Component:
  - component: {fileID: 2}
  - component: {fileID: 3}
--- !u!4 &2
Transform:
  m_GameObject: {fileID: 1}
  m_Father: {fileID: 0}
  m_Children: []
  m_LocalPosition: {x: 0, y: 0, z: 0}
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalScale: {x: 1, y: 1, z: 1}
--- !u!137 &3
SkinnedMeshRenderer:
  m_GameObject: {fileID: 1}
  m_Enabled: 1
  m_Mesh: {fileID: {{id}}, guid: {{meshGuid}}, type: 2}
  m_Materials: []
  m_Bones: []
  m_RootBone: {fileID: 0}
  m_BlendShapeWeights: [25, 75, 99]
""");
            using var package = UnityPackage.Open(prefab);
            using var view = UnityPrefabInstances.CreateView(package, prefabGuid, null);
            return ResolvedSnapshot.Capture(view, prefabGuid);
        }
        void Check(long id, string[] names, float[] weights)
        {
            var snapshot = JsonSerializer.SerializeToElement(Capture(id));
            var renderer = snapshot.GetProperty("sections").GetProperty("renderers")[0];
            var shapes = renderer.GetProperty("blendShapes").EnumerateArray().ToArray();
            if (renderer.GetProperty("mesh").GetProperty("FileId").GetString() != id.ToString() ||
                !shapes.Select(s => s.GetProperty("name").GetString()).SequenceEqual(names) ||
                !shapes.Select(s => s.GetProperty("weight").GetSingle()).SequenceEqual(weights) ||
                !shapes.Select(s => s.GetProperty("index").GetInt32()).SequenceEqual(Enumerable.Range(0, names.Length)))
                throw new Exception("Native metadata differs from independent fixture expectations.");
        }
        void Reject(long id)
        {
            try { Capture(id); } catch (NotSupportedException) { return; }
            throw new Exception("Missing/wrong-type native mesh silently accepted.");
        }
        void Add(long id, int type, AssetTypeValueField field)
        {
            if (!file.Metadata.TypeTreeTypes.Any(t => t.TypeId == type)) file.Metadata.TypeTreeTypes.Add(Tree(field.TemplateField, type));
            var info = AssetFileInfo.Create(file, id, type); info.SetNewData(field); file.Metadata.AssetInfos.Add(info);
        }
    }

    private static AssetTypeValueField Mesh(params string[] names)
    {
        var values = names.Select(n => Field("data", "Channel", null, Field("name", "string", new(n)))).ToArray();
        var array = Field("Array", "Array", null, values);
        array.TemplateField.IsArray = true;
        array.TemplateField.Children = new() { Field("size", "int", new(0)).TemplateField,
            Field("data", "Channel", null, Field("name", "string", new(""))).TemplateField };
        return Field("Base", "Mesh", null, Field("m_Shapes", "BlendShapeData", null, Field("channels", "vector", null, array)));
    }
    private static AssetTypeValueField Field(string name, string type, AssetTypeValue value, params AssetTypeValueField[] children) => new()
    {
        TemplateField = new() { Name = name, Type = type, ValueType = value?.ValueType ?? AssetValueType.None,
            HasValue = value != null, Version = 1, IsAligned = type == "string", Children = children.Select(c => c.TemplateField).ToList() },
        Value = value, Children = children.ToList(),
    };
    private static TypeTreeType Tree(AssetTypeTemplateField root, int id)
    {
        var nodes = new List<TypeTreeNode>(); using var strings = new MemoryStream();
        uint Text(string value) { uint offset = (uint)strings.Position; strings.Write(System.Text.Encoding.UTF8.GetBytes(value + "\0")); return offset; }
        void Add(AssetTypeTemplateField f, byte level)
        {
            nodes.Add(new() { Level = level, Version = 1, Index = (uint)nodes.Count, NameStrOffset = Text(f.Name),
                TypeStrOffset = Text(f.Type), ByteSize = f.Type == "int" ? 4 : f.Type == "char" ? 1 : -1,
                TypeFlags = f.IsArray ? TypeTreeNodeFlags.Array : 0, MetaFlags = f.IsAligned ? 0x4000u : 0 });
            foreach (var child in f.Children) Add(child, (byte)(level + 1));
            if (f.Type == "string") Add(new() { Name = "Array", Type = "Array", IsArray = true, IsAligned = true, Children = new()
                { new() { Name = "size", Type = "int", Children = new() }, new() { Name = "data", Type = "char", Children = new() } } }, (byte)(level + 1));
        }
        Add(root, 0);
        return new() { TypeId = id, ScriptTypeIndex = 0xffff, ScriptIdHash = Hash128.NewBlankHash(), TypeHash = Hash128.NewBlankHash(),
            TypeDependencies = Array.Empty<int>(), TypeBlob = new() { Nodes = nodes, StringBufferBytes = strings.ToArray() } };
    }
}
