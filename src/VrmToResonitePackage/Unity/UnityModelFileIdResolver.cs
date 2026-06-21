using System.Text;
using Assimp;

namespace VrmToResonitePackage.Unity;

/// <summary>
/// Resolves Unity's stable 64-bit local file IDs for objects imported from a model asset.
/// ModelImporter fileIdsGeneration=1 hashes the object type and object name, while generation 2
/// hashes the object type and hierarchy path. Both use xxHash64.
/// </summary>
public sealed class UnityModelFileIdResolver
{
    private readonly Dictionary<long, string> _names = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _blendShapeNames =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<float>> _blendShapeDefaultWeights =
        new(StringComparer.Ordinal);
    private readonly List<ModelMaterial> _materials = new();
    private IReadOnlyList<UnityFbxBlendShapeDefaults.Channel> _defaultWeightChannels =
        Array.Empty<UnityFbxBlendShapeDefaults.Channel>();
    private bool[] _usedDefaultWeightChannels = Array.Empty<bool>();

    public UnityModelFileIdResolver(UnityAsset model)
    {
        if (model?.HasContent != true)
        {
            return;
        }

        AddMetaMappings(model);
        if (model.Extension == ".fbx")
        {
            AddFbxMappings(model.DiskPath);
        }
    }

    public string ResolveName(long fileId)
        => fileId != 0 && _names.TryGetValue(fileId, out string name) ? name : null;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> BlendShapeNames => _blendShapeNames;
    public IReadOnlyDictionary<string, IReadOnlyList<float>> BlendShapeDefaultWeights =>
        _blendShapeDefaultWeights;
    public IReadOnlyList<ModelMaterial> Materials => _materials;

    public readonly record struct ModelMaterial(string Name, string MainTexturePath);

    private void AddMetaMappings(UnityAsset model)
    {
        if (model.MetaPath == null || !File.Exists(model.MetaPath))
        {
            return;
        }
        YamlNode importer = UnityYaml.ParseFlatDocument(File.ReadAllText(model.MetaPath))?["ModelImporter"];
        YamlNode table = importer?["internalIDToNameTable"];
        if (table?.Seq != null)
        {
            foreach (YamlNode entry in table.Seq)
            {
                long fileId = entry?["first"]?.Map?.Values.Select(v => v.AsLong()).FirstOrDefault() ?? 0;
                string name = entry?["second"]?.AsString();
                if (fileId != 0 && !string.IsNullOrEmpty(name))
                {
                    _names[fileId] = NormalizeName(name);
                }
            }
        }

        YamlNode recycleNames = importer?["fileIDToRecycleName"];
        if (recycleNames?.Map != null)
        {
            foreach ((string key, YamlNode value) in recycleNames.Map)
            {
                if (long.TryParse(key, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out long fileId))
                {
                    _names[fileId] = NormalizeName(value.AsString());
                }
            }
        }
    }

    private void AddFbxMappings(string path)
    {
        string importPath = path;
        string temporaryPath = null;
        try
        {
            if (!string.Equals(Path.GetExtension(path), ".fbx", StringComparison.OrdinalIgnoreCase))
            {
                temporaryPath = Path.Combine(Path.GetTempPath(), $"ResoPon_fileid_{Guid.NewGuid():N}.fbx");
                File.Copy(path, temporaryPath);
                importPath = temporaryPath;
            }
            using var context = new AssimpContext();
            _defaultWeightChannels = UnityFbxBlendShapeDefaults.Read(importPath);
            _usedDefaultWeightChannels = new bool[_defaultWeightChannels.Count];
            Scene scene = context.ImportFile(importPath, PostProcessSteps.None);
            if (scene?.RootNode == null)
            {
                return;
            }
            foreach (Material material in scene.Materials)
            {
                string texturePath = material.HasTextureDiffuse
                    ? material.TextureDiffuse.FilePath
                    : null;
                _materials.Add(new ModelMaterial(material.Name, texturePath));
            }

            var roots = new List<(Node Node, List<string> Path)>();
            CollectNodes(scene.RootNode, new List<string>(), roots);
            foreach ((Node node, List<string> nodePath) in roots)
            {
                AddPathVariants("GameObject", nodePath, node.Name);
                AddPathVariants("Transform", nodePath, node.Name);

                bool skinned = node.MeshIndices.Any(index =>
                    index >= 0 && index < scene.MeshCount && scene.Meshes[index].HasBones);
                if (node.MeshCount > 0)
                {
                    // Unity's FBX importer can classify a mesh differently from Assimp when skin
                    // data is optimized or stripped. Stable fileID resolution is exact, so include
                    // both possible renderer component types as candidates.
                    AddPathVariants("SkinnedMeshRenderer", nodePath, node.Name);
                    AddPathVariants("MeshRenderer", nodePath, node.Name);
                    if (skinned)
                    {
                        AddBlendShapeNames(scene, node);
                    }
                    else
                    {
                        AddPathVariants("MeshFilter", nodePath, node.Name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Some FBX variants are unsupported by Assimp. Explicit meta mappings still remain usable.
            Elements.Core.UniLog.Warning($"FBX fileID map generation failed ({Path.GetFileName(path)}): {ex.Message}");
        }
        finally
        {
            if (temporaryPath != null)
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    private void AddBlendShapeNames(Scene scene, Node node)
    {
        Mesh mesh = node.MeshIndices
            .Where(index => index >= 0 && index < scene.MeshCount)
            .Select(index => scene.Meshes[index])
            .FirstOrDefault(candidate => candidate.MeshAnimationAttachmentCount > 0);
        if (mesh == null)
        {
            return;
        }

        var names = new List<string>(mesh.MeshAnimationAttachmentCount);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (MeshAnimationAttachment attachment in mesh.MeshAnimationAttachments)
        {
            string name = attachment.Name ?? "";
            while (!used.Add(name))
            {
                name += "_";
            }
            names.Add(name);
        }
        _blendShapeNames.TryAdd(node.Name, names);
        var defaults = new float[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            string attachmentName = names[i];
            for (int channelIndex = 0; channelIndex < _defaultWeightChannels.Count; channelIndex++)
            {
                if (_usedDefaultWeightChannels[channelIndex])
                {
                    continue;
                }
                UnityFbxBlendShapeDefaults.Channel channel = _defaultWeightChannels[channelIndex];
                if (string.Equals(attachmentName, channel.Name, StringComparison.Ordinal) ||
                    string.Equals(attachmentName, $"{channel.Name}.{channel.Name}",
                        StringComparison.Ordinal))
                {
                    defaults[i] = channel.Weight;
                    _usedDefaultWeightChannels[channelIndex] = true;
                    break;
                }
            }
        }
        if (defaults.Any(weight => MathF.Abs(weight) > 0.001f))
        {
            _blendShapeDefaultWeights.TryAdd(node.Name, defaults);
        }
    }

    private static void CollectNodes(Node node, List<string> parentPath,
        List<(Node Node, List<string> Path)> result)
    {
        var path = new List<string>(parentPath);
        if (!string.IsNullOrEmpty(node.Name))
        {
            path.Add(node.Name);
        }
        result.Add((node, path));
        foreach (Node child in node.Children)
        {
            CollectNodes(child, path, result);
        }
    }

    private void AddPathVariants(string type, List<string> rawPath, string name)
    {
        // Generation 1 uses only the imported object name. This is still emitted for objects whose
        // old recycle ID cannot be reused (for example when an FBX node was renamed).
        for (int duplicateIndex = 0; duplicateIndex < 16; duplicateIndex++)
        {
            AddHashCandidate(type, name, duplicateIndex.ToString(
                System.Globalization.CultureInfo.InvariantCulture), name, Encoding.UTF8);
        }

        // Assimp can expose an artificial FBX root, while Unity always starts the imported path at
        // //RootNode and may fold a single model root. Try the equivalent path forms; only a hash
        // that appears in YAML will be queried, so the extra candidates are harmless.
        for (int skip = 0; skip <= Math.Min(2, rawPath.Count); skip++)
        {
            var parts = new List<string> { "//RootNode" };
            parts.AddRange(rawPath.Skip(skip));
            string nodePath = string.Join("/", parts);
            string rootedNodePath = nodePath == "//RootNode"
                ? "//RootNode/root"
                : nodePath.Replace("//RootNode/", "//RootNode/root/");
            var nodePaths = new List<string> { nodePath };
            // A sole imported mesh node can be folded into Unity's synthetic "root". Do not let
            // Assimp's artificial root claim that path first; it belongs to the real child node.
            if (nodePath != "//RootNode" || (rawPath.Count > 1 && skip > 0))
            {
                nodePaths.Add(rootedNodePath);
            }
            IEnumerable<string> objectPaths = type == "GameObject"
                ? nodePaths
                : nodePaths.Select(path => $"{path}/{type}");
            foreach (string objectPath in objectPaths)
            {
                // Unity increments the suffix when imported objects collide on type/path.
                // Generate a small candidate range so prefab overrides can resolve those stable
                // IDs even though Assimp doesn't expose Unity's chosen duplicate index.
                for (int duplicateIndex = 0; duplicateIndex < 16; duplicateIndex++)
                {
                    AddHashCandidate(type, objectPath, duplicateIndex.ToString(
                        System.Globalization.CultureInfo.InvariantCulture), name, Encoding.UTF8);
                }
            }
        }
    }

    private void AddHashCandidate(string type, string objectPath, string suffix, string name, Encoding encoding)
    {
        byte[] bytes = encoding.GetBytes($"Type:{type}->{objectPath}{suffix}");
        long fileId = unchecked((long)XxHash64(bytes));
        _names.TryAdd(fileId, NormalizeName(name));
    }

    private static string NormalizeName(string name)
        => name == "//RootNode" ? "RootNode" : name;

    internal static long Compute(string type, string objectPath, int duplicateIndex)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($"Type:{type}->{objectPath}{duplicateIndex}");
        return unchecked((long)XxHash64(bytes));
    }

    private static ulong XxHash64(ReadOnlySpan<byte> data)
    {
        const ulong p1 = 11400714785074694791UL;
        const ulong p2 = 14029467366897019727UL;
        const ulong p3 = 1609587929392839161UL;
        const ulong p4 = 9650029242287828579UL;
        const ulong p5 = 2870177450012600261UL;

        static ulong Round(ulong acc, ulong input)
        {
            const ulong p1 = 11400714785074694791UL;
            const ulong p2 = 14029467366897019727UL;
            acc += input * p2;
            acc = System.Numerics.BitOperations.RotateLeft(acc, 31);
            return acc * p1;
        }

        static ulong MergeRound(ulong acc, ulong value)
        {
            const ulong p1 = 11400714785074694791UL;
            const ulong p4 = 9650029242287828579UL;
            acc ^= Round(0, value);
            return acc * p1 + p4;
        }

        int offset = 0;
        ulong hash;
        if (data.Length >= 32)
        {
            ulong v1 = unchecked(p1 + p2);
            ulong v2 = p2;
            ulong v3 = 0;
            ulong v4 = unchecked(0UL - p1);
            int limit = data.Length - 32;
            do
            {
                v1 = Round(v1, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]));
                offset += 8;
                v2 = Round(v2, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]));
                offset += 8;
                v3 = Round(v3, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]));
                offset += 8;
                v4 = Round(v4, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]));
                offset += 8;
            } while (offset <= limit);
            hash = System.Numerics.BitOperations.RotateLeft(v1, 1)
                   + System.Numerics.BitOperations.RotateLeft(v2, 7)
                   + System.Numerics.BitOperations.RotateLeft(v3, 12)
                   + System.Numerics.BitOperations.RotateLeft(v4, 18);
            hash = MergeRound(hash, v1);
            hash = MergeRound(hash, v2);
            hash = MergeRound(hash, v3);
            hash = MergeRound(hash, v4);
        }
        else
        {
            hash = p5;
        }

        hash += (ulong)data.Length;
        while (offset <= data.Length - 8)
        {
            ulong lane = Round(0, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]));
            hash ^= lane;
            hash = System.Numerics.BitOperations.RotateLeft(hash, 27) * p1 + p4;
            offset += 8;
        }
        if (offset <= data.Length - 4)
        {
            long lane = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
            hash ^= unchecked((ulong)(lane * unchecked((long)p1)));
            hash = System.Numerics.BitOperations.RotateLeft(hash, 23) * p2 + p3;
            offset += 4;
        }
        while (offset < data.Length)
        {
            hash ^= data[offset] * p5;
            hash = System.Numerics.BitOperations.RotateLeft(hash, 11) * p1;
            offset++;
        }
        hash ^= hash >> 33;
        hash *= p2;
        hash ^= hash >> 29;
        hash *= p3;
        hash ^= hash >> 32;
        return hash;
    }
}
