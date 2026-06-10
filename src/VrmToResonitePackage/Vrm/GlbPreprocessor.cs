using System.Text;
using System.Text.Json.Nodes;

namespace VrmToResonitePackage.Vrm;

/// <summary>
/// Rewrites the GLB JSON chunk before handing the file to Resonite's importer.
///
/// Older UniVRM exporters (e.g. UniGLTF-1.28 / AliciaSolid) store morph target names
/// only as primitives[].extras.targetNames, which Assimp does not read — every
/// blendshape comes out unnamed. FrooxEngine's duplicate-name guard treats empty
/// names as "not present" (MeshX.HasBlendShape returns false for empty strings),
/// so the second unnamed blendshape crashes the import with
/// "BlendShape with given key already exists".
///
/// This preprocessor guarantees every mesh with morph targets has unique, non-empty
/// mesh.extras.targetNames (taken from primitive extras when available, generated
/// otherwise).
/// </summary>
internal static class GlbPreprocessor
{
    private const uint GlbMagic = 0x46546C67;   // "glTF"
    private const uint JsonChunkType = 0x4E4F534A; // "JSON"

    /// <summary>Copies the VRM to <paramref name="targetPath"/> as an import-ready GLB.</summary>
    public static void CreateImportableGlb(string sourcePath, string targetPath)
    {
        byte[] data = File.ReadAllBytes(sourcePath);
        if (data.Length < 12 || BitConverter.ToUInt32(data, 0) != GlbMagic)
        {
            throw new InvalidDataException("GLBファイルではありません（マジックナンバー不一致）。");
        }
        uint containerVersion = BitConverter.ToUInt32(data, 4);

        // Decode the chunk list.
        var chunks = new List<(uint type, int offset, int length)>();
        int position = 12;
        while (position + 8 <= data.Length)
        {
            int length = checked((int)BitConverter.ToUInt32(data, position));
            uint type = BitConverter.ToUInt32(data, position + 4);
            chunks.Add((type, position + 8, length));
            position += 8 + length;
        }

        int jsonIndex = chunks.FindIndex(c => c.type == JsonChunkType);
        if (jsonIndex < 0)
        {
            throw new InvalidDataException("GLBのJSONチャンクが見つかりません。");
        }
        (uint _, int jsonOffset, int jsonLength) = chunks[jsonIndex];

        JsonNode root = JsonNode.Parse(new ReadOnlySpan<byte>(data, jsonOffset, jsonLength));
        bool changed = false;
        if (root != null)
        {
            changed |= FixTargetNames(root);
            changed |= FixMorphTargetAttributes(root);
        }
        if (!changed)
        {
            File.Copy(sourcePath, targetPath, overwrite: true);
            return;
        }

        // Re-encode the JSON chunk, padded with spaces to 4-byte alignment per spec.
        byte[] jsonBytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        int paddedJsonLength = (jsonBytes.Length + 3) & ~3;

        using var stream = new FileStream(targetPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        long totalLength = 12 + 8 + paddedJsonLength;
        for (int i = 0; i < chunks.Count; i++)
        {
            if (i != jsonIndex)
            {
                totalLength += 8 + chunks[i].length;
            }
        }
        writer.Write(GlbMagic);
        writer.Write(containerVersion);
        writer.Write(checked((uint)totalLength));

        writer.Write((uint)paddedJsonLength);
        writer.Write(JsonChunkType);
        writer.Write(jsonBytes);
        for (int i = jsonBytes.Length; i < paddedJsonLength; i++)
        {
            writer.Write((byte)0x20);
        }

        foreach ((uint type, int offset, int length) in chunks)
        {
            if (type == JsonChunkType)
            {
                continue;
            }
            writer.Write((uint)length);
            writer.Write(type);
            writer.Write(data, offset, length);
        }
    }

    /// <summary>Returns true when the JSON was modified.</summary>
    private static bool FixTargetNames(JsonNode root)
    {
        if (root["meshes"] is not JsonArray meshes)
        {
            return false;
        }
        bool changed = false;
        foreach (JsonNode meshNode in meshes)
        {
            if (meshNode is not JsonObject mesh ||
                mesh["primitives"] is not JsonArray primitives ||
                primitives.Count == 0)
            {
                continue;
            }
            int targetCount = 0;
            if (primitives[0] is JsonObject firstPrimitive &&
                firstPrimitive["targets"] is JsonArray targets)
            {
                targetCount = targets.Count;
            }
            if (targetCount == 0)
            {
                continue;
            }

            List<string> existing = ReadNames(mesh["extras"]) ?? ReadPrimitiveNames(primitives);
            var unique = new List<string>(targetCount);
            var used = new HashSet<string>(StringComparer.Ordinal);
            bool meshChanged = existing == null || existing.Count < targetCount;
            for (int i = 0; i < targetCount; i++)
            {
                string name = existing != null && i < existing.Count ? existing[i] : null;
                if (string.IsNullOrEmpty(name))
                {
                    name = $"morph_{i}";
                    meshChanged = true;
                }
                // Same disambiguation FrooxEngine itself uses for duplicate names.
                while (!used.Add(name))
                {
                    name += "_";
                    meshChanged = true;
                }
                unique.Add(name);
            }
            // mesh.extras.targetNames missing entirely also counts as a change.
            if (mesh["extras"] is not JsonObject || ReadNames(mesh["extras"]) == null)
            {
                meshChanged = true;
            }
            if (!meshChanged)
            {
                continue;
            }

            if (mesh["extras"] is not JsonObject extras)
            {
                extras = new JsonObject();
                mesh["extras"] = extras;
            }
            var array = new JsonArray();
            foreach (string name in unique)
            {
                array.Add(JsonValue.Create(name));
            }
            extras["targetNames"] = array;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// FrooxEngine merges all primitives of a node into one mesh and assumes morph
    /// target attributes are uniform across primitives: it takes HasNormals from the
    /// first primitive's target and then indexes the other primitives' normal lists,
    /// crashing when a primitive lacks NORMAL on that target. Combined/merged VRMs
    /// (e.g. face-tracking bodies) frequently mix per-target NORMAL presence, so this
    /// strips NORMAL/TANGENT from targets where the primitives disagree.
    /// It also trims target counts to the minimum across primitives for the same
    /// reason (the merge loop iterates the first primitive's count).
    /// </summary>
    private static bool FixMorphTargetAttributes(JsonNode root)
    {
        if (root["meshes"] is not JsonArray meshes)
        {
            return false;
        }
        bool changed = false;
        foreach (JsonNode meshNode in meshes)
        {
            if (meshNode is not JsonObject mesh ||
                mesh["primitives"] is not JsonArray primitives ||
                primitives.Count < 2)
            {
                continue;
            }
            var targetArrays = new List<JsonArray>();
            foreach (JsonNode primitive in primitives)
            {
                if (primitive is JsonObject primitiveObject &&
                    primitiveObject["targets"] is JsonArray targets)
                {
                    targetArrays.Add(targets);
                }
                else
                {
                    targetArrays.Add(null);
                }
            }
            if (targetArrays.All(t => t == null || t.Count == 0))
            {
                continue;
            }

            // Uniform target count across primitives (glTF requires it, tools violate it).
            int minCount = targetArrays.Min(t => t?.Count ?? 0);
            foreach (JsonArray targets in targetArrays)
            {
                if (targets == null)
                {
                    continue;
                }
                while (targets.Count > minCount)
                {
                    targets.RemoveAt(targets.Count - 1);
                    changed = true;
                }
            }

            // Per-target attribute consistency.
            foreach (string attribute in new[] { "NORMAL", "TANGENT" })
            {
                for (int k = 0; k < minCount; k++)
                {
                    bool all = true;
                    bool any = false;
                    foreach (JsonArray targets in targetArrays)
                    {
                        bool has = targets?[k] is JsonObject target && target[attribute] != null;
                        all &= has;
                        any |= has;
                    }
                    if (!any || all)
                    {
                        continue;
                    }
                    foreach (JsonArray targets in targetArrays)
                    {
                        if (targets?[k] is JsonObject target && target[attribute] != null)
                        {
                            target.Remove(attribute);
                            changed = true;
                        }
                    }
                }
            }
        }
        return changed;
    }

    private static List<string> ReadNames(JsonNode extras)
    {
        if (extras is JsonObject extrasObject &&
            extrasObject["targetNames"] is JsonArray names)
        {
            return names.Select(n => n?.GetValue<string>() ?? "").ToList();
        }
        return null;
    }

    private static List<string> ReadPrimitiveNames(JsonArray primitives)
    {
        foreach (JsonNode primitive in primitives)
        {
            if (primitive is JsonObject primitiveObject)
            {
                List<string> names = ReadNames(primitiveObject["extras"]);
                if (names != null)
                {
                    return names;
                }
            }
        }
        return null;
    }
}
