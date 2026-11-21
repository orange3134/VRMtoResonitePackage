using Elements.Core;
using Elements.Assets;
using System.Globalization;
using System.Text.Json;

internal static class StructureSnapshot
{
    public static void Write(DataTreeDictionary root, string[] types, string hash, string output,
        RecordPackage package = null, DataTreeNode assets = null)
    {
        var nodes = new List<(DataTreeDictionary Data, string Id, string Parent, int Index, string[] Path, bool Active)>();
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var world = new Dictionary<string, float4x4>();
        var meshes = new Dictionary<string, string>();
        Index(root, "r", null, 0, System.Array.Empty<string>(), true, float4x4.Identity);
        foreach (var asset in Array(assets).OfType<DataTreeDictionary>()) IndexMesh(asset);
        var slots = nodes.Select(n => new
        {
            id = n.Id, parent = n.Parent, siblingIndex = n.Index, path = n.Path,
            serializedId = Scalar(n.Data.TryGetNode("ID")),
            tag = Scalar(n.Data.TryGetNode("Tag")) ?? "",
            activeSelf = Value<bool>(n.Data.TryGetNode("Active")), activeInHierarchy = n.Active,
            localPosition = Vector(n.Data, "Position", 3), localRotation = Vector(n.Data, "Rotation", 4),
            localScale = Vector(n.Data, "Scale", 3),
            components = Array(n.Data.TryGetNode("Components")).Select((c, i) => Component(c, n.Id, i)).ToArray()
        }).ToArray();
        var snapshot = new
        {
            format = "resopon.converted-structure", schemaVersion = 1, packageSha256 = hash,
            coordinateSystem = "Resonite saved local TRS; quaternion xyzw; no Unity-space normalization",
            slots, visemeDrivers = VisemeDrivers()
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions { WriteIndented = true });
        using var file = new FileStream(output, FileMode.CreateNew);
        file.Write(json);
        Console.WriteLine($"Captured structure: {slots.Length} slots; package SHA-256 {hash}");

        object[] VisemeDrivers()
        {
            var weights = new Dictionary<string, (string Renderer, int Index, string Mesh)>();
            var analyzers = new Dictionary<string, string>();
            var drivers = new List<(string Id, DataTreeDictionary Data)>();
            foreach (var slot in nodes)
            {
                int index = 0;
                foreach (var component in Array(slot.Data.TryGetNode("Components")).Cast<DataTreeDictionary>())
                {
                    string id = slot.Id + ":" + index++;
                    var data = (DataTreeDictionary)component.TryGetNode("Data");
                    switch (TypeName(component))
                    {
                        case "[FrooxEngine]FrooxEngine.VisemeAnalyzer":
                            analyzers.Add(Scalar(data.TryGetNode("ID")), id);
                            break;
                        case "[FrooxEngine]FrooxEngine.DirectVisemeDriver":
                            drivers.Add((id, data));
                            break;
                        case "[FrooxEngine]FrooxEngine.SkinnedMeshRenderer":
                            string mesh = Scalar(data.TryGetNode("Mesh"));
                            int channel = 0;
                            foreach (var weight in Array(data.TryGetNode("BlendShapeWeights")))
                            {
                                if (weight is DataTreeDictionary field && Scalar(field.TryGetNode("ID")) is string fieldId)
                                    weights.Add(fieldId, (id, channel, mesh));
                                channel++;
                            }
                            break;
                    }
                }
            }
            var channels = new Dictionary<string, string[]>();
            string[] ShapeNames(string mesh)
            {
                if (mesh == null || package == null || !meshes.TryGetValue(mesh, out string url) ||
                    !Uri.TryCreate(url?.TrimStart('@'), UriKind.Absolute, out var uri) || uri.Scheme != "packdb") return null;
                if (channels.TryGetValue(url, out var names)) return names;
                using var source = package.ReadAsset(RecordPackage.GetAssetSignature(uri));
                using var buffer = new MemoryStream();
                source.CopyTo(buffer); buffer.Position = 0;
                var geometry = new MeshX();
                geometry.Decode(buffer);
                return channels[url] = geometry.BlendShapes.Select(s => s.Name).ToArray();
            }
            string[] phonemes = { "Silence", "PP", "FF", "TH", "DD", "kk", "CH", "SS", "nn", "RR", "aa", "E", "ih", "oh", "ou" };
            return drivers.Select(d =>
            {
                string source = Scalar(d.Data.TryGetNode("Source"));
                var bindings = new List<object>();
                foreach (string phoneme in phonemes)
                {
                    string field = Scalar(d.Data.TryGetNode(phoneme));
                    if (field == null) continue;
                    bool found = weights.TryGetValue(field, out var target);
                    var names = found ? ShapeNames(target.Mesh) : null;
                    bindings.Add(new { phoneme, field, renderer = target.Renderer, index = found ? (int?)target.Index : null,
                        shape = names != null && target.Index < names.Length ? names[target.Index] : null });
                }
                return (object)new { component = d.Id, enabled = Value<bool>(d.Data.TryGetNode("Enabled")), source,
                    analyzer = source == null ? null : analyzers.GetValueOrDefault(source), bindings };
            }).ToArray();
        }

        void Index(DataTreeDictionary data, string id, string parent, int index, string[] parentPath, bool active, float4x4 parentWorld)
        {
            string[] path = parentPath.Append(Scalar(data.TryGetNode("Name")) ?? "").ToArray();
            active &= Value<bool>(data.TryGetNode("Active"));
            nodes.Add((data, id, parent, index, path, active));
            string serialized = Scalar(data.TryGetNode("ID")) ?? throw new InvalidDataException("Missing slot ID.");
            ids.Add(serialized, id);
            var pos = Vector(data, "Position", 3);
            var rot = Vector(data, "Rotation", 4);
            var scale = Vector(data, "Scale", 3);
            var frame = parentWorld * float4x4.Translation(new float3(pos[0], pos[1], pos[2]))
                * float4x4.Rotation(new floatQ(rot[0], rot[1], rot[2], rot[3]))
                * float4x4.Scale(new float3(scale[0], scale[1], scale[2]));
            world.Add(serialized, frame);
            foreach (var component in Array(data.TryGetNode("Components")).OfType<DataTreeDictionary>()) IndexMesh(component);
            int childIndex = 0;
            foreach (var child in Array(data.TryGetNode("Children")))
            {
                if (child is not DataTreeDictionary dictionary) throw new InvalidDataException("Invalid child slot.");
                Index(dictionary, id + "/" + childIndex, id, childIndex++, path, active, frame);
            }
        }

        object Reference(DataTreeNode node)
        {
            string raw = Scalar(node);
            string target = raw == null ? null : ids.GetValueOrDefault(raw);
            return new { target, status = target != null ? "resolved" : raw == null ? "null" : "unresolved", serializedId = raw };
        }

        object Component(DataTreeNode node, string slotId, int index)
        {
            if (node is not DataTreeDictionary component || component.TryGetNode("Data") is not DataTreeDictionary data)
                throw new InvalidDataException("Invalid component.");
            string type = TypeName(component);
            bool skin = type == "[FrooxEngine]FrooxEngine.SkinnedMeshRenderer";
            bool rig = type == "[FrooxEngine]FrooxEngine.BipedRig";
            var references = new List<object>();
            Scan(data, "");
            var rigBones = new Dictionary<string, object>();
            if (rig)
            {
                if (Unwrap(data.TryGetNode("Bones")) is not DataTreeDictionary bones)
                    throw new InvalidDataException("Missing rig bones.");
                foreach (var bone in bones.Children) rigBones.Add(bone.Key, Reference(bone.Value));
            }
            return new
            {
                id = slotId + ":" + index, type,
                enabled = data.TryGetNode("Enabled") is {} enabled ? (bool?)Value<bool>(enabled) : null,
                bones = skin ? Array(data.TryGetNode("Bones")).Select(Reference).ToArray() : null,
                skinSize = skin && package != null ? SkinSize.Measure(package,
                    meshes.GetValueOrDefault(Scalar(data.TryGetNode("Mesh")) ?? ""),
                    Array(data.TryGetNode("Bones")).Select(Scalar).ToArray(), world) : null,
                rigBones = rig ? rigBones : null,
                blendShapeWeights = skin ? Array(data.TryGetNode("BlendShapeWeights")).Select(Value<float>).ToArray() : null,
                slotReferences = references
            };

            // Resolve only actual saved references to slots, never infer a bone from its name.
            // Field IDs are intentionally skipped. Preserve field paths and ordered array indices.
            void Scan(DataTreeNode field, string path)
            {
                if (field is DataTreeDictionary dictionary)
                {
                    if (dictionary.ContainsKey("Data")) { Scan(dictionary.TryGetNode("Data"), path); return; }
                    foreach (var child in dictionary.Children)
                        if (child.Key is not ("ID" or "persistent-ID"))
                            Scan(child.Value, path.Length == 0 ? child.Key : path + "." + child.Key);
                }
                else if (field is DataTreeList list)
                {
                    int i = 0;
                    foreach (var child in list.Children) Scan(child, path + "[" + i++ + "]");
                }
                else if (field is DataTreeValue { IsNull: false } value &&
                         ids.TryGetValue(Convert.ToString(value.Value, CultureInfo.InvariantCulture), out string target))
                    references.Add(new { field = path, target });
            }
        }

        string TypeName(DataTreeDictionary component)
        {
            return Value<object>(component.TryGetNode("Type")) switch
            {
                int i when i >= 0 && i < types.Length => types[i],
                long i when i >= 0 && i < types.Length => types[(int)i],
                string s => s,
                _ => throw new InvalidDataException("Invalid component type.")
            };
        }

        void IndexMesh(DataTreeDictionary component)
        {
            if (TypeName(component) != "[FrooxEngine]FrooxEngine.StaticMesh") return;
            if (component.TryGetNode("Data") is DataTreeDictionary data)
                meshes.Add(Scalar(data.TryGetNode("ID")), Scalar(data.TryGetNode("URL")));
        }
    }

    private static DataTreeNode Unwrap(DataTreeNode node) => node is DataTreeDictionary d && d.ContainsKey("Data") ? d.TryGetNode("Data") : node;
    private static T Value<T>(DataTreeNode node) => Unwrap(node) is DataTreeValue value ? value.Extract<T>()
        : throw new InvalidDataException("Missing scalar field.");
    private static string Scalar(DataTreeNode node) => node == null || Unwrap(node) is DataTreeValue { IsNull: true } ? null :
        Value<object>(node) is {} value ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
    private static IEnumerable<DataTreeNode> Array(DataTreeNode node) => Unwrap(node) switch
    {
        null => System.Array.Empty<DataTreeNode>(), DataTreeList list => list.Children,
        _ => throw new InvalidDataException("Invalid array field.")
    };
    private static float[] Vector(DataTreeDictionary slot, string field, int length)
    {
        float[] values = Array(slot.TryGetNode(field)).Select(Value<float>).ToArray();
        if (values.Length != length || values.Any(v => !float.IsFinite(v)))
            throw new InvalidDataException("Invalid transform: " + field);
        return values;
    }
}
