using Elements.Core;
using System.Globalization;
using System.Text.Json;

internal static class StructureSnapshot
{
    public static void Write(DataTreeDictionary root, string[] types, string hash, string output)
    {
        var nodes = new List<(DataTreeDictionary Data, string Id, string Parent, int Index, string[] Path, bool Active)>();
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        Index(root, "r", null, 0, System.Array.Empty<string>(), true);
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
            slots
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions { WriteIndented = true });
        using var file = new FileStream(output, FileMode.CreateNew);
        file.Write(json);
        Console.WriteLine($"Captured structure: {slots.Length} slots; package SHA-256 {hash}");

        void Index(DataTreeDictionary data, string id, string parent, int index, string[] parentPath, bool active)
        {
            string[] path = parentPath.Append(Scalar(data.TryGetNode("Name")) ?? "").ToArray();
            active &= Value<bool>(data.TryGetNode("Active"));
            nodes.Add((data, id, parent, index, path, active));
            ids.Add(Scalar(data.TryGetNode("ID")) ?? throw new InvalidDataException("Missing slot ID."), id);
            int childIndex = 0;
            foreach (var child in Array(data.TryGetNode("Children")))
            {
                if (child is not DataTreeDictionary dictionary) throw new InvalidDataException("Invalid child slot.");
                Index(dictionary, id + "/" + childIndex, id, childIndex++, path, active);
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
            object typeValue = Value<object>(component.TryGetNode("Type"));
            string type = typeValue switch
            {
                int i when i >= 0 && i < types.Length => types[i],
                long i when i >= 0 && i < types.Length => types[(int)i],
                string s => s,
                _ => throw new InvalidDataException("Invalid component type.")
            };
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
