using Elements.Core;
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
    private const uint BinChunkType = 0x004E4942; // "BIN\0"

    /// <summary>
    /// Copies the VRM to <paramref name="targetPath"/> as an import-ready GLB.
    /// Returns true when a Y180 orientation bake was applied (VRM0 only), which the caller
    /// records on the <see cref="VrmModel"/> so collider offsets are converted correctly.
    /// <paramref name="mirroredX"/> is set when a proper-handed VRM1 (Blender add-on style)
    /// was X-mirrored to match the UniVRM/VRoid ReverseX convention.
    /// </summary>
    public static bool CreateImportableGlb(string sourcePath, string targetPath, out bool mirroredX)
    {
        mirroredX = false;
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

        int binIndex = chunks.FindIndex(c => c.type == BinChunkType);
        int binOffset = binIndex >= 0 ? chunks[binIndex].offset : -1;
        int binLength = binIndex >= 0 ? chunks[binIndex].length : 0;

        JsonNode root = JsonNode.Parse(new ReadOnlySpan<byte>(data, jsonOffset, jsonLength));
        bool changed = false;
        bool baked = false;
        byte[] appendedBin = Array.Empty<byte>();
        if (root != null)
        {
            changed |= FixTargetNames(root);
            changed |= FixMorphTargetAttributes(root);
            // VRM0 imports facing -Z, which forces VRIK to spin CenteredRoot 180°. Baking a
            // Y180 (origin-centered) into the glTF makes the imported model face +Z with a
            // zero CenteredRoot rotation. Combined with the importer's X mirror and UniVRM's
            // ReverseZ export this collapses to identity, so engine space becomes Unity space.
            baked = BakeVrm0Y180(root, data, binOffset, binLength);
            changed |= baked;
            // A proper-handed VRM1 (Blender add-on: anatomical left at -X, no UniVRM ReverseX)
            // ends up mirror-flipped by the importer's X mirror; VRIK then spins it 180° so the
            // body/head face backwards. Pre-mirror it across X so it matches the UniVRM/VRoid
            // convention the importer expects, collapsing the round trip to identity.
            mirroredX = MirrorXForProperHandedVrm1(root, data, binOffset);
            changed |= mirroredX;
            // Stop JoinIdenticalVertices from collapsing morph-bearing vertices. Needs a BIN
            // chunk (GLB-embedded geometry) to append the guard channel's data to.
            if (binIndex >= 0)
            {
                appendedBin = AddMorphVertexGuardChannel(root, binLength, out bool guarded);
                changed |= guarded;
            }
        }
        if (!changed)
        {
            File.Copy(sourcePath, targetPath, overwrite: true);
            return false;
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
                // The guard channel's data is appended to the BIN chunk (already 4-aligned).
                if (i == binIndex)
                {
                    totalLength += appendedBin.Length;
                }
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

        for (int i = 0; i < chunks.Count; i++)
        {
            (uint type, int offset, int length) = chunks[i];
            if (type == JsonChunkType)
            {
                continue;
            }
            if (i == binIndex && appendedBin.Length > 0)
            {
                writer.Write((uint)(length + appendedBin.Length));
                writer.Write(type);
                writer.Write(data, offset, length);
                writer.Write(appendedBin);
            }
            else
            {
                writer.Write((uint)length);
                writer.Write(type);
                writer.Write(data, offset, length);
            }
        }
        return baked;
    }

    /// <summary>
    /// Assimp's JoinIdenticalVertices post-process step (which Resonite's importer always runs)
    /// merges any two vertices that are byte-identical across every attribute it compares —
    /// position, normal, tangent, all UV channels, all colour channels and bone weights — but
    /// it ignores morph target (blendshape) deltas entirely. VRM face meshes routinely contain
    /// vertices that are coincident in the rest pose (e.g. teeth/tongue collapsed to a point,
    /// "deployed" only by a blendshape) yet carry different morph deltas. The merge collapses
    /// them and keeps only one delta, so those vertices no longer move when the blendshape is
    /// driven — the exact symptom that does not occur in UniVRM, whose importer never joins.
    ///
    /// We defeat the merge from the data side: every primitive of a morph-bearing mesh gets an
    /// extra TEXCOORD channel whose value is unique per vertex. Two vertices that differ in any
    /// UV channel are never candidates to join, so morph-distinct vertices survive intact. The
    /// channel is unused by the imported (toon) materials, so it has no visual effect, and the
    /// data is shared per POSITION accessor to keep the appended buffer small.
    /// Returns the bytes to append to the BIN chunk; sets <paramref name="changed"/> when any
    /// channel was added.
    /// </summary>
    private static byte[] AddMorphVertexGuardChannel(JsonNode root, int binLength, out bool changed)
    {
        changed = false;
        if (root["meshes"] is not JsonArray meshes ||
            root["accessors"] is not JsonArray accessors ||
            root["bufferViews"] is not JsonArray bufferViews)
        {
            return Array.Empty<byte>();
        }

        var appended = new List<byte>();
        // One guard accessor per POSITION accessor, reused by every primitive that shares it.
        var guardForPosition = new Dictionary<int, int>();
        foreach (JsonNode meshNode in meshes)
        {
            if (meshNode is not JsonObject mesh ||
                mesh["primitives"] is not JsonArray primitives ||
                primitives.Count == 0)
            {
                continue;
            }
            bool hasMorphTargets = primitives[0] is JsonObject first &&
                first["targets"] is JsonArray firstTargets && firstTargets.Count > 0;
            if (!hasMorphTargets)
            {
                continue;
            }
            foreach (JsonNode primitiveNode in primitives)
            {
                if (primitiveNode is not JsonObject primitive ||
                    primitive["attributes"] is not JsonObject attributes ||
                    attributes["POSITION"] is not JsonValue positionRef)
                {
                    continue;
                }
                int positionAccessor = positionRef.GetValue<int>();
                if (!guardForPosition.TryGetValue(positionAccessor, out int guardAccessor))
                {
                    int count = (accessors[positionAccessor] as JsonObject)?["count"]?.GetValue<int>() ?? 0;
                    if (count <= 0)
                    {
                        continue;
                    }
                    // VEC2 float, u = vertex index (distinct, integer-spaced — beats any join
                    // epsilon), v = 0. byteOffset is the BIN chunk's current 4-aligned length.
                    int byteOffset = binLength + appended.Count;
                    var bytes = new byte[count * 8];
                    for (int v = 0; v < count; v++)
                    {
                        BitConverter.GetBytes((float)v).CopyTo(bytes, v * 8);
                    }
                    appended.AddRange(bytes);
                    bufferViews.Add(new JsonObject
                    {
                        ["buffer"] = 0,
                        ["byteOffset"] = byteOffset,
                        ["byteLength"] = count * 8,
                        ["target"] = 34962, // ARRAY_BUFFER
                    });
                    accessors.Add(new JsonObject
                    {
                        ["bufferView"] = bufferViews.Count - 1,
                        ["componentType"] = 5126, // FLOAT
                        ["count"] = count,
                        ["type"] = "VEC2",
                    });
                    guardAccessor = accessors.Count - 1;
                    guardForPosition[positionAccessor] = guardAccessor;
                    changed = true;
                }
                int channel = 0;
                while (attributes[$"TEXCOORD_{channel}"] != null)
                {
                    channel++;
                }
                attributes[$"TEXCOORD_{channel}"] = guardAccessor;
            }
        }
        // The new bufferViews extend past the original buffer; grow buffer[0] to cover them.
        if (changed && root["buffers"] is JsonArray buffers && buffers.Count > 0 &&
            buffers[0] is JsonObject buffer)
        {
            buffer["byteLength"] = binLength + appended.Count;
        }
        return appended.ToArray();
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

    /// <summary>
    /// Bakes a Y180 rotation R = diag(-1, 1, -1) (origin-centered) into a VRM0 glTF so the
    /// imported model faces +Z. Only safe — and only applied — when the file is a normalized,
    /// rotation-free VRM0; otherwise logs a warning and skips.
    ///
    /// Math: in a hierarchy with no node rotations, every joint's world transform is just its
    /// world translation T(p). Applying R to every node translation moves jointWorld to R·p,
    /// and left-multiplying each inverse bind matrix by R (IBM' = R·IBM) makes the skinned
    /// vertex world coordinates exactly R·(original world coordinate). Vertex, normal and morph
    /// data need no change. Returns true when the bake was applied.
    /// </summary>
    private static bool BakeVrm0Y180(JsonNode root, byte[] data, int binOffset, int binLength)
    {
        if (root["extensions"] is not JsonObject extensions || extensions["VRM"] == null)
        {
            return false; // Not VRM0 — bake is VRM0-specific.
        }

        if (!CanBakeVrm0(root, out string reason))
        {
            UniLog.Warning($"VRM0の向き正規化をスキップしました（{reason}）。");
            return false;
        }

        // 1) Mirror translations: [x, y, z] -> [-x, y, -z].
        if (root["nodes"] is JsonArray nodes)
        {
            foreach (JsonNode nodeNode in nodes)
            {
                if (nodeNode is JsonObject node && node["translation"] is JsonArray t && t.Count == 3)
                {
                    t[0] = JsonValue.Create(-t[0].GetValue<float>());
                    t[2] = JsonValue.Create(-t[2].GetValue<float>());
                }
            }
        }

        // 2) Left-multiply each skin's inverseBindMatrices by R. glTF mat4 is column-major,
        //    so R = diag(-1,1,-1) negates rows 0 and 2 = column-major indices 0,4,8,12 and
        //    2,6,10,14. Edit the float群 in place in the binary chunk.
        if (root["skins"] is JsonArray skins)
        {
            JsonArray accessors = root["accessors"] as JsonArray;
            JsonArray bufferViews = root["bufferViews"] as JsonArray;
            foreach (JsonNode skinNode in skins)
            {
                if (skinNode is not JsonObject skin ||
                    skin["inverseBindMatrices"] is not JsonValue ibmRef)
                {
                    continue;
                }
                int accessorIndex = ibmRef.GetValue<int>();
                JsonObject accessor = accessors?[accessorIndex] as JsonObject;
                int count = accessor?["count"]?.GetValue<int>() ?? 0;
                int bufferViewIndex = accessor?["bufferView"]?.GetValue<int>() ?? -1;
                int accessorByteOffset = accessor?["byteOffset"]?.GetValue<int>() ?? 0;
                JsonObject bufferView = bufferViewIndex >= 0 ? bufferViews?[bufferViewIndex] as JsonObject : null;
                int bufferViewByteOffset = bufferView?["byteOffset"]?.GetValue<int>() ?? 0;
                if (count <= 0 || bufferView == null || binOffset < 0)
                {
                    continue;
                }

                int matStart = binOffset + bufferViewByteOffset + accessorByteOffset;
                for (int m = 0; m < count; m++)
                {
                    int baseOffset = matStart + m * 64; // 16 floats * 4 bytes
                    foreach (int floatIndex in new[] { 0, 4, 8, 12, 2, 6, 10, 14 })
                    {
                        int byteIndex = baseOffset + floatIndex * 4;
                        float value = BitConverter.ToSingle(data, byteIndex);
                        BitConverter.GetBytes(-value).CopyTo(data, byteIndex);
                    }
                }
            }
        }

        UniLog.Log("VRM0の向きを+Zに正規化しました（CenteredRoot回転0）。");
        return true;
    }

    /// <summary>
    /// Verifies the strict preconditions under which the Y180 bake's "rotation-free hierarchy"
    /// math holds. Sets <paramref name="reason"/> to a Japanese explanation when it does not.
    /// </summary>
    private static bool CanBakeVrm0(JsonNode root, out string reason)
    {
        // Animations would be baked against the old orientation.
        if (root["animations"] is JsonArray animations && animations.Count > 0)
        {
            reason = "アニメーションを含むため";
            return false;
        }

        if (root["nodes"] is JsonArray nodes)
        {
            foreach (JsonNode nodeNode in nodes)
            {
                if (nodeNode is not JsonObject node)
                {
                    continue;
                }
                // A node matrix would not compose cleanly with our translation/IBM edit.
                if (node["matrix"] != null)
                {
                    reason = "ノードにmatrixが指定されているため";
                    return false;
                }
                // The math assumes every node rotation is identity.
                if (node["rotation"] is JsonArray rotation && rotation.Count == 4)
                {
                    if (MathX.Abs(rotation[0].GetValue<float>()) >= 1e-4f ||
                        MathX.Abs(rotation[1].GetValue<float>()) >= 1e-4f ||
                        MathX.Abs(rotation[2].GetValue<float>()) >= 1e-4f)
                    {
                        reason = "ノードに非ゼロ回転があるため";
                        return false;
                    }
                }
                // A mesh node without a skin has un-rotated vertices; the bake would not turn them.
                if (node["mesh"] != null && node["skin"] == null)
                {
                    reason = "スキンを持たないメッシュノードがあるため";
                    return false;
                }
            }
        }

        // Every skin's IBM accessor must be a plain float MAT4 (no sparse) so the in-place
        // binary edit applies cleanly.
        JsonArray accessors = root["accessors"] as JsonArray;
        if (root["skins"] is JsonArray skins)
        {
            foreach (JsonNode skinNode in skins)
            {
                if (skinNode is not JsonObject skin)
                {
                    continue;
                }
                if (skin["inverseBindMatrices"] is not JsonValue ibmRef)
                {
                    reason = "skinにinverseBindMatricesが無いため";
                    return false;
                }
                int accessorIndex = ibmRef.GetValue<int>();
                if (accessors?[accessorIndex] is not JsonObject accessor)
                {
                    reason = "inverseBindMatricesのaccessorが見つからないため";
                    return false;
                }
                int componentType = accessor["componentType"]?.GetValue<int>() ?? 0;
                string type = accessor["type"]?.GetValue<string>();
                if (componentType != 5126 || type != "MAT4" || accessor["sparse"] != null)
                {
                    reason = "inverseBindMatricesがfloat MAT4(非sparse)でないため";
                    return false;
                }
                // A byteStride other than the tight 64-byte mat4 size would break the in-place
                // packing assumption (the spec allows interleaving, though MAT4 IBMs never do).
                int bufferViewIndex = accessor["bufferView"]?.GetValue<int>() ?? -1;
                JsonObject bufferView = bufferViewIndex >= 0
                    ? (root["bufferViews"] as JsonArray)?[bufferViewIndex] as JsonObject : null;
                int byteStride = bufferView?["byteStride"]?.GetValue<int>() ?? 0;
                if (byteStride != 0 && byteStride != 64)
                {
                    reason = "inverseBindMatricesにbyteStrideが指定されているため";
                    return false;
                }
            }
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Mirrors a "proper-handed" VRM1 across the X axis (P = diag(-1, 1, 1)) so it matches the
    /// ReverseX encoding UniVRM/VRoid produce and Resonite's importer assumes.
    ///
    /// Background: the importer always mirrors the imported scene by scale(-1, 1, 1) (glTF RH ->
    /// engine LH). UniVRM exports VRM1 already X-mirrored (ReverseX, anatomical left at +X), so
    /// the importer's mirror undoes it and the avatar comes out correct. The Blender VRM add-on
    /// instead exports a "proper" glTF (anatomical left at -X), so the importer's mirror leaves it
    /// mirror-flipped; VRIK's CreateCenteredRoot then measures the body forward from the feet,
    /// finds it pointing backwards and injects a 180° Y rotation, which combined with the X mirror
    /// reads as a Z mirror: body and head face backwards while left/right is preserved.
    ///
    /// Applying the mirror here makes (our mirror) ∘ (importer mirror) collapse to identity, so the
    /// avatar imports faithfully and faces forward. The mirror is a full reflection done without any
    /// negative scale: node locals are conjugated by P (translation.x negated, quaternion
    /// (x,y,z,w) -> (x,-y,-z,w), scale unchanged), inverse bind matrices are conjugated (P·IBM·P),
    /// and every POSITION/NORMAL/TANGENT value (base mesh and morph deltas) has its X negated. The
    /// triangle winding is intentionally left untouched: the importer's own FlipWindingOrder treats
    /// it exactly as it does a UniVRM file.
    ///
    /// Returns true when the mirror was applied.
    /// </summary>
    private static bool MirrorXForProperHandedVrm1(JsonNode root, byte[] data, int binOffset)
    {
        if (binOffset < 0)
        {
            return false; // Need embedded geometry to flip vertex/IBM data.
        }
        if (root["extensions"] is not JsonObject extensions ||
            extensions["VRMC_vrm"] is not JsonObject vrm1)
        {
            return false; // VRM1-specific (VRM0 has its own Y180 bake path).
        }
        if (!IsProperHandedVrm1(root, vrm1))
        {
            return false; // VRoid/UniVRM ReverseX convention — the importer already handles it.
        }

        // 1) Conjugate every node's local transform by P = diag(-1, 1, 1).
        if (root["nodes"] is JsonArray nodes)
        {
            foreach (JsonNode nodeNode in nodes)
            {
                if (nodeNode is not JsonObject node)
                {
                    continue;
                }
                if (node["translation"] is JsonArray t && t.Count == 3)
                {
                    t[0] = JsonValue.Create(-t[0].GetValue<float>());
                }
                if (node["rotation"] is JsonArray r && r.Count == 4)
                {
                    r[1] = JsonValue.Create(-r[1].GetValue<float>());
                    r[2] = JsonValue.Create(-r[2].GetValue<float>());
                }
                // A baked node matrix (rare in VRM) gets the same P·M·P conjugation.
                if (node["matrix"] is JsonArray m && m.Count == 16)
                {
                    NegateMatrixConjugationX(m);
                }
            }
        }

        // 2) Negate X of every POSITION / NORMAL / TANGENT value (base attributes + morph targets),
        //    and reverse the winding of every triangle list. The X negation is a reflection, which
        //    flips triangle winding; reversing the indices keeps the glTF a valid, normals-consistent
        //    CCW mesh so Resonite imports it the same way it does a UniVRM file (not inside out).
        //    (The VRM0 Y180 bake is a proper rotation and so deliberately leaves winding alone.)
        var vecAccessors = new HashSet<int>();      // POSITION / NORMAL: negate X.
        var tangentAccessors = new HashSet<int>();  // TANGENT (VEC4): negate X and the w handedness sign.
        var indexAccessors = new HashSet<int>();
        if (root["meshes"] is JsonArray meshes)
        {
            foreach (JsonNode meshNode in meshes)
            {
                if (meshNode is not JsonObject mesh || mesh["primitives"] is not JsonArray primitives)
                {
                    continue;
                }
                foreach (JsonNode primitiveNode in primitives)
                {
                    if (primitiveNode is not JsonObject primitive)
                    {
                        continue;
                    }
                    CollectXMirrorAccessors(primitive["attributes"], vecAccessors, tangentAccessors);
                    if (primitive["targets"] is JsonArray targets)
                    {
                        foreach (JsonNode target in targets)
                        {
                            CollectXMirrorAccessors(target, vecAccessors, tangentAccessors);
                        }
                    }
                    // Default primitive mode is TRIANGLES (4); only triangle lists are reversed.
                    int mode = primitive["mode"]?.GetValue<int>() ?? 4;
                    if (mode == 4 && primitive["indices"] is JsonValue indexRef)
                    {
                        indexAccessors.Add(indexRef.GetValue<int>());
                    }
                }
            }
        }
        foreach (int accessorIndex in vecAccessors)
        {
            NegateAccessorComponents(root, data, binOffset, accessorIndex, negateW: false);
        }
        foreach (int accessorIndex in tangentAccessors)
        {
            NegateAccessorComponents(root, data, binOffset, accessorIndex, negateW: true);
        }
        foreach (int accessorIndex in indexAccessors)
        {
            ReverseTriangleWinding(root, data, binOffset, accessorIndex);
        }

        // 3) Conjugate every skin's inverse bind matrices: IBM' = P·IBM·P.
        if (root["skins"] is JsonArray skins)
        {
            foreach (JsonNode skinNode in skins)
            {
                if (skinNode is JsonObject skin && skin["inverseBindMatrices"] is JsonValue ibmRef)
                {
                    ConjugateMat4AccessorX(root, data, binOffset, ibmRef.GetValue<int>());
                }
            }
        }

        UniLog.Log("VRM1の手系をX鏡映でUniVRM規約に正規化しました（前後反転を解消）。");
        return true;
    }

    /// <summary>
    /// A VRM1 is "proper-handed" when its skeleton's anatomical left side sits at -X (the natural,
    /// non-mirrored orientation the Blender add-on exports). UniVRM/VRoid instead mirror-encode the
    /// model (ReverseX), placing the left side at +X. The legs are the reliable signal — arm chains
    /// are occasionally authored with unusual rest rotations.
    /// </summary>
    private static bool IsProperHandedVrm1(JsonNode root, JsonObject vrm1)
    {
        if (vrm1["humanoid"] is not JsonObject humanoid ||
            humanoid["humanBones"] is not JsonObject humanBones ||
            root["nodes"] is not JsonArray nodes)
        {
            return false;
        }
        (int left, int right) = (BoneNode(humanBones, "leftUpperLeg"), BoneNode(humanBones, "rightUpperLeg"));
        if (left < 0 || right < 0)
        {
            (left, right) = (BoneNode(humanBones, "leftFoot"), BoneNode(humanBones, "rightFoot"));
        }
        if (left < 0 || right < 0)
        {
            return false;
        }
        int[] parents = BuildParentMap(nodes);
        float leftX = GlobalNodeX(nodes, parents, left);
        float rightX = GlobalNodeX(nodes, parents, right);
        // Proper-handed: the right-side bone is on the +X side of the left-side bone.
        return rightX > leftX;
    }

    private static int BoneNode(JsonObject humanBones, string name)
    {
        return humanBones[name] is JsonObject bone && bone["node"] is JsonValue node
            ? node.GetValue<int>()
            : -1;
    }

    private static int[] BuildParentMap(JsonArray nodes)
    {
        var parents = new int[nodes.Count];
        Array.Fill(parents, -1);
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is JsonObject node && node["children"] is JsonArray children)
            {
                foreach (JsonNode child in children)
                {
                    int c = child.GetValue<int>();
                    if (c >= 0 && c < parents.Length)
                    {
                        parents[c] = i;
                    }
                }
            }
        }
        return parents;
    }

    /// <summary>Global X of a node via forward kinematics (translation + rotation; scale ignored).</summary>
    private static float GlobalNodeX(JsonArray nodes, int[] parents, int index)
    {
        var chain = new List<int>();
        for (int i = index; i >= 0; i = parents[i])
        {
            chain.Add(i);
        }
        chain.Reverse();
        float3 position = float3.Zero;
        floatQ rotation = floatQ.Identity;
        foreach (int ni in chain)
        {
            JsonObject node = nodes[ni] as JsonObject;
            float3 t = ReadFloat3(node?["translation"]);
            floatQ r = ReadQuaternion(node?["rotation"]);
            position += rotation * t;
            rotation *= r;
        }
        return position.x;
    }

    private static float3 ReadFloat3(JsonNode node)
    {
        return node is JsonArray a && a.Count == 3
            ? new float3(a[0].GetValue<float>(), a[1].GetValue<float>(), a[2].GetValue<float>())
            : float3.Zero;
    }

    private static floatQ ReadQuaternion(JsonNode node)
    {
        return node is JsonArray a && a.Count == 4
            ? new floatQ(a[0].GetValue<float>(), a[1].GetValue<float>(), a[2].GetValue<float>(), a[3].GetValue<float>())
            : floatQ.Identity;
    }

    private static void CollectXMirrorAccessors(JsonNode attributes, HashSet<int> vecSet, HashSet<int> tangentSet)
    {
        if (attributes is not JsonObject obj)
        {
            return;
        }
        if (obj["POSITION"] is JsonValue position)
        {
            vecSet.Add(position.GetValue<int>());
        }
        if (obj["NORMAL"] is JsonValue normal)
        {
            vecSet.Add(normal.GetValue<int>());
        }
        if (obj["TANGENT"] is JsonValue tangent)
        {
            tangentSet.Add(tangent.GetValue<int>());
        }
    }

    /// <summary>
    /// Negates component 0 (X) of every element of a float accessor, in place in the BIN chunk.
    /// For TANGENT (VEC4) also negates component 3 (the w handedness sign), which a reflection flips.
    /// </summary>
    private static void NegateAccessorComponents(JsonNode root, byte[] data, int binOffset, int accessorIndex, bool negateW)
    {
        if (root["accessors"] is not JsonArray accessors ||
            accessorIndex < 0 || accessorIndex >= accessors.Count ||
            accessors[accessorIndex] is not JsonObject accessor)
        {
            return;
        }
        if ((accessor["componentType"]?.GetValue<int>() ?? 0) != 5126) // float only
        {
            return;
        }
        int numComponents = (accessor["type"]?.GetValue<string>()) switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            _ => 0,
        };
        int count = accessor["count"]?.GetValue<int>() ?? 0;
        int bufferViewIndex = accessor["bufferView"]?.GetValue<int>() ?? -1;
        if (numComponents == 0 || count <= 0 || bufferViewIndex < 0 ||
            root["bufferViews"] is not JsonArray bufferViews || bufferViewIndex >= bufferViews.Count ||
            bufferViews[bufferViewIndex] is not JsonObject bufferView)
        {
            return;
        }
        int accessorByteOffset = accessor["byteOffset"]?.GetValue<int>() ?? 0;
        int bufferViewByteOffset = bufferView["byteOffset"]?.GetValue<int>() ?? 0;
        int stride = bufferView["byteStride"]?.GetValue<int>() ?? numComponents * 4;
        int start = binOffset + bufferViewByteOffset + accessorByteOffset;
        bool flipW = negateW && numComponents == 4;
        for (int i = 0; i < count; i++)
        {
            int elementStart = start + i * stride;
            NegateFloatAt(data, elementStart);              // component 0 (X)
            if (flipW)
            {
                NegateFloatAt(data, elementStart + 3 * 4);  // component 3 (w handedness sign)
            }
        }
    }

    private static void NegateFloatAt(byte[] data, int byteIndex)
    {
        float value = BitConverter.ToSingle(data, byteIndex);
        BitConverter.GetBytes(-value).CopyTo(data, byteIndex);
    }

    /// <summary>Reverses each triangle's winding (swaps the 2nd and 3rd index) of a scalar index accessor.</summary>
    private static void ReverseTriangleWinding(JsonNode root, byte[] data, int binOffset, int accessorIndex)
    {
        if (root["accessors"] is not JsonArray accessors ||
            accessorIndex < 0 || accessorIndex >= accessors.Count ||
            accessors[accessorIndex] is not JsonObject accessor)
        {
            return;
        }
        int componentType = accessor["componentType"]?.GetValue<int>() ?? 0;
        int componentSize = componentType switch
        {
            5121 => 1, // UNSIGNED_BYTE
            5123 => 2, // UNSIGNED_SHORT
            5125 => 4, // UNSIGNED_INT
            _ => 0,
        };
        int count = accessor["count"]?.GetValue<int>() ?? 0;
        int bufferViewIndex = accessor["bufferView"]?.GetValue<int>() ?? -1;
        if (componentSize == 0 || count < 3 || bufferViewIndex < 0 ||
            root["bufferViews"] is not JsonArray bufferViews || bufferViewIndex >= bufferViews.Count ||
            bufferViews[bufferViewIndex] is not JsonObject bufferView)
        {
            return;
        }
        int accessorByteOffset = accessor["byteOffset"]?.GetValue<int>() ?? 0;
        int bufferViewByteOffset = bufferView["byteOffset"]?.GetValue<int>() ?? 0;
        int start = binOffset + bufferViewByteOffset + accessorByteOffset;
        // Swap the 2nd and 3rd index of every triangle (tightly packed scalars).
        for (int tri = 0; tri + 2 < count; tri += 3)
        {
            int b = start + (tri + 1) * componentSize;
            int c = start + (tri + 2) * componentSize;
            for (int k = 0; k < componentSize; k++)
            {
                (data[b + k], data[c + k]) = (data[c + k], data[b + k]);
            }
        }
    }

    // Column-major mat4 indices negated by the X reflection conjugation P·M·P (P = diag(-1,1,1)):
    // the elements where exactly one of {row, col} is 0.
    private static readonly int[] XConjugationIndices = { 1, 2, 3, 4, 8, 12 };

    /// <summary>Conjugates a float MAT4 accessor by P (IBM' = P·IBM·P), in place in the BIN chunk.</summary>
    private static void ConjugateMat4AccessorX(JsonNode root, byte[] data, int binOffset, int accessorIndex)
    {
        if (root["accessors"] is not JsonArray accessors ||
            accessorIndex < 0 || accessorIndex >= accessors.Count ||
            accessors[accessorIndex] is not JsonObject accessor)
        {
            return;
        }
        if ((accessor["componentType"]?.GetValue<int>() ?? 0) != 5126 ||
            accessor["type"]?.GetValue<string>() != "MAT4" || accessor["sparse"] != null)
        {
            return;
        }
        int count = accessor["count"]?.GetValue<int>() ?? 0;
        int bufferViewIndex = accessor["bufferView"]?.GetValue<int>() ?? -1;
        if (count <= 0 || bufferViewIndex < 0 ||
            root["bufferViews"] is not JsonArray bufferViews || bufferViewIndex >= bufferViews.Count ||
            bufferViews[bufferViewIndex] is not JsonObject bufferView)
        {
            return;
        }
        int accessorByteOffset = accessor["byteOffset"]?.GetValue<int>() ?? 0;
        int bufferViewByteOffset = bufferView["byteOffset"]?.GetValue<int>() ?? 0;
        int start = binOffset + bufferViewByteOffset + accessorByteOffset;
        for (int m = 0; m < count; m++)
        {
            int baseOffset = start + m * 64; // 16 floats * 4 bytes
            foreach (int floatIndex in XConjugationIndices)
            {
                int byteIndex = baseOffset + floatIndex * 4;
                float value = BitConverter.ToSingle(data, byteIndex);
                BitConverter.GetBytes(-value).CopyTo(data, byteIndex);
            }
        }
    }

    /// <summary>Conjugates a column-major mat4 stored as a JSON number array by P (M' = P·M·P).</summary>
    private static void NegateMatrixConjugationX(JsonArray matrix)
    {
        foreach (int floatIndex in XConjugationIndices)
        {
            matrix[floatIndex] = JsonValue.Create(-matrix[floatIndex].GetValue<float>());
        }
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
