using System.Text.Json;
using Vec3 = System.Numerics.Vector3;
using Vec4 = System.Numerics.Vector4;

namespace VrmToResonitePackage.Vrm;

/// <summary>
/// Reads the JSON chunk of a VRM (GLB container) file and extracts the VRM-specific data.
/// Supports both VRM 0.x ("VRM" extension) and VRM 1.0 ("VRMC_vrm" extension).
/// </summary>
public static class VrmParser
{
    public static VrmModel Parse(string vrmPath)
    {
        using FileStream stream = File.OpenRead(vrmPath);
        using JsonDocument doc = JsonDocument.Parse(ReadGlbJsonChunk(stream));
        JsonElement root = doc.RootElement;

        var model = new VrmModel();
        ParseGltfCommon(root, model);

        if (root.TryGetProperty("extensions", out JsonElement extensions))
        {
            if (extensions.TryGetProperty("VRMC_vrm", out JsonElement vrm1))
            {
                model.SpecVersionMajor = 1;
                model.Source = ModelSource.Vrm1;
                ParseVrm1(extensions, vrm1, root, model);
                return model;
            }
            if (extensions.TryGetProperty("VRM", out JsonElement vrm0))
            {
                model.SpecVersionMajor = 0;
                model.Source = ModelSource.Vrm0;
                ParseVrm0(vrm0, model);
                return model;
            }
        }
        throw new InvalidDataException("VRM拡張が見つかりません。VRM 0.x / 1.0 のファイルか確認してください。");
    }

    private static byte[] ReadGlbJsonChunk(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        uint magic = reader.ReadUInt32();
        if (magic != 0x46546C67) // "glTF"
        {
            throw new InvalidDataException("GLBファイルではありません（マジックナンバー不一致）。");
        }
        reader.ReadUInt32(); // container version
        reader.ReadUInt32(); // total length
        while (stream.Position < stream.Length)
        {
            uint chunkLength = reader.ReadUInt32();
            uint chunkType = reader.ReadUInt32();
            if (chunkType == 0x4E4F534A) // "JSON"
            {
                return reader.ReadBytes(checked((int)chunkLength));
            }
            stream.Seek(chunkLength, SeekOrigin.Current);
        }
        throw new InvalidDataException("GLBのJSONチャンクが見つかりません。");
    }

    private static void ParseGltfCommon(JsonElement root, VrmModel model)
    {
        if (root.TryGetProperty("nodes", out JsonElement nodes))
        {
            int index = 0;
            foreach (JsonElement node in nodes.EnumerateArray())
            {
                string name = node.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                model.NodeNames.Add(name ?? $"node_{index}");
                int meshIndex = node.TryGetProperty("mesh", out JsonElement m) ? m.GetInt32() : -1;
                model.NodeMeshIndices.Add(meshIndex);
                if (meshIndex >= 0)
                {
                    if (!model.MeshToNodes.TryGetValue(meshIndex, out List<int> list))
                    {
                        list = new List<int>();
                        model.MeshToNodes[meshIndex] = list;
                    }
                    list.Add(index);
                }
                index++;
            }
        }

        if (root.TryGetProperty("meshes", out JsonElement meshes))
        {
            foreach (JsonElement mesh in meshes.EnumerateArray())
            {
                model.MeshTargetNames.Add(ExtractTargetNames(mesh));
            }
        }

        // glTF texture index -> image index (needed to locate MToon-only textures).
        if (root.TryGetProperty("textures", out JsonElement textures))
        {
            foreach (JsonElement texture in textures.EnumerateArray())
            {
                model.TextureToImage.Add(texture.TryGetProperty("source", out JsonElement source)
                    ? source.GetInt32()
                    : -1);
            }
        }
        int ResolveImage(int textureIndex)
        {
            return textureIndex >= 0 && textureIndex < model.TextureToImage.Count
                ? model.TextureToImage[textureIndex]
                : -1;
        }

        // Base glTF material info (alpha mode etc.) — applies to both VRM versions.
        if (root.TryGetProperty("materials", out JsonElement materials))
        {
            int matIndex = 0;
            foreach (JsonElement mat in materials.EnumerateArray())
            {
                string name = mat.TryGetProperty("name", out JsonElement n) ? n.GetString() : $"material_{matIndex}";
                var info = new VrmMaterialInfo { Name = name };
                if (mat.TryGetProperty("alphaMode", out JsonElement alphaMode))
                {
                    info.AlphaMode = alphaMode.GetString()?.ToLowerInvariant() ?? "opaque";
                }
                if (mat.TryGetProperty("alphaCutoff", out JsonElement cutoff))
                {
                    info.AlphaCutoff = cutoff.GetSingle();
                }
                if (mat.TryGetProperty("doubleSided", out JsonElement doubleSided))
                {
                    info.DoubleSided = doubleSided.GetBoolean();
                }
                if (mat.TryGetProperty("emissiveFactor", out JsonElement emissive))
                {
                    Vec3 e = ReadVec3Array(emissive);
                    info.EmissionColor = new Vec4(e.X, e.Y, e.Z, 1f);
                }
                if (mat.TryGetProperty("pbrMetallicRoughness", out JsonElement pbr) &&
                    pbr.TryGetProperty("baseColorFactor", out JsonElement baseColor))
                {
                    info.BaseColor = ReadVec4Array(baseColor);
                }
                if (mat.TryGetProperty("extensions", out JsonElement matExt) &&
                    matExt.TryGetProperty("VRMC_materials_mtoon", out JsonElement mtoon))
                {
                    info.IsMToon = true;
                    if (mtoon.TryGetProperty("shadeColorFactor", out JsonElement shade))
                    {
                        Vec3 s = ReadVec3Array(shade);
                        info.ShadeColor = new Vec4(s.X, s.Y, s.Z, 1f);
                    }
                    // VRM1 stores the outline width in meters already.
                    if (mtoon.TryGetProperty("outlineWidthFactor", out JsonElement ow))
                    {
                        info.OutlineWidth = ow.GetSingle();
                    }
                    if (mtoon.TryGetProperty("outlineColorFactor", out JsonElement oc))
                    {
                        Vec3 o = ReadVec3Array(oc);
                        info.OutlineColor = new Vec4(o.X, o.Y, o.Z, 1f);
                    }
                    info.OutlineWidthMode = (mtoon.TryGetProperty("outlineWidthMode", out JsonElement owm)
                        ? owm.GetString() : null) switch
                    {
                        "worldCoordinates" => "world",
                        "screenCoordinates" => "screen",
                        _ => "none",
                    };
                    if (info.OutlineWidthMode == "none")
                    {
                        info.OutlineWidth = 0f;
                    }
                    if (mtoon.TryGetProperty("outlineWidthMultiplyTexture", out JsonElement owt) &&
                        owt.TryGetProperty("index", out JsonElement owtIndex))
                    {
                        int image = ResolveImage(owtIndex.GetInt32());
                        if (image >= 0)
                        {
                            info.OutlineWidthImageIndex = image;
                            info.OutlineWidthImageUsesGreenChannel = true;
                        }
                    }
                    info.OutlineLightingMix = mtoon.TryGetProperty("outlineLightingMixFactor", out JsonElement olm)
                        ? olm.GetSingle() : 1f;

                    info.TransparentWithZWrite =
                        mtoon.TryGetProperty("transparentWithZWrite", out JsonElement zw) && zw.GetBoolean();
                    if (mtoon.TryGetProperty("renderQueueOffsetNumber", out JsonElement rqo))
                    {
                        info.RenderQueueOffset = rqo.GetInt32();
                    }

                    info.ShadingShift = mtoon.TryGetProperty("shadingShiftFactor", out JsonElement ssf)
                        ? ssf.GetSingle() : 0f;
                    info.ShadingToony = mtoon.TryGetProperty("shadingToonyFactor", out JsonElement stf)
                        ? stf.GetSingle() : 0.9f;
                    if (mtoon.TryGetProperty("shadingShiftTexture", out JsonElement sst))
                    {
                        if (sst.TryGetProperty("index", out JsonElement sstIndex))
                        {
                            int image = ResolveImage(sstIndex.GetInt32());
                            if (image >= 0)
                            {
                                info.ShadingShiftImageIndex = image;
                            }
                        }
                        info.ShadingShiftTextureScale = sst.TryGetProperty("scale", out JsonElement sstScale)
                            ? sstScale.GetSingle() : 1f;
                    }

                    if (mtoon.TryGetProperty("parametricRimColorFactor", out JsonElement rim))
                    {
                        Vec3 r = ReadVec3Array(rim);
                        info.RimColor = new Vec4(r.X, r.Y, r.Z, 1f);
                    }
                    info.RimLightingMix = mtoon.TryGetProperty("rimLightingMixFactor", out JsonElement rlm)
                        ? rlm.GetSingle() : 1f;
                    info.RimFresnelPower = mtoon.TryGetProperty("parametricRimFresnelPowerFactor", out JsonElement rfp)
                        ? rfp.GetSingle() : 5f;
                    info.RimLift = mtoon.TryGetProperty("parametricRimLiftFactor", out JsonElement rlf)
                        ? rlf.GetSingle() : 0f;

                    if (mtoon.TryGetProperty("matcapTexture", out JsonElement matcap) &&
                        matcap.TryGetProperty("index", out JsonElement matcapIndex))
                    {
                        int image = ResolveImage(matcapIndex.GetInt32());
                        if (image >= 0)
                        {
                            info.MatcapImageIndex = image;
                        }
                    }
                    if (mtoon.TryGetProperty("matcapFactor", out JsonElement matcapFactor))
                    {
                        Vec3 m = ReadVec3Array(matcapFactor);
                        info.MatcapColor = new Vec4(m.X, m.Y, m.Z, 1f);
                    }
                }
                model.Materials[name] = info;
                matIndex++;
            }
        }
    }

    private static List<string> ExtractTargetNames(JsonElement mesh)
    {
        // UniVRM convention: mesh.extras.targetNames, or per-primitive extras.targetNames.
        if (mesh.TryGetProperty("extras", out JsonElement extras) &&
            extras.TryGetProperty("targetNames", out JsonElement names))
        {
            return names.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        }
        if (mesh.TryGetProperty("primitives", out JsonElement primitives))
        {
            foreach (JsonElement primitive in primitives.EnumerateArray())
            {
                if (primitive.TryGetProperty("extras", out JsonElement pExtras) &&
                    pExtras.TryGetProperty("targetNames", out JsonElement pNames))
                {
                    return pNames.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                }
            }
        }
        return new List<string>();
    }

    // ---------------------------------------------------------------- VRM 0.x

    private static void ParseVrm0(JsonElement vrm, VrmModel model)
    {
        if (vrm.TryGetProperty("meta", out JsonElement meta))
        {
            model.MetaJson = meta.GetRawText();
            model.Title = GetString(meta, "title");
            model.Author = GetString(meta, "author");

            // VRM0 stores the thumbnail as a glTF *texture* index; resolve it to an image index.
            if (meta.TryGetProperty("texture", out JsonElement thumbTex) &&
                thumbTex.ValueKind == JsonValueKind.Number)
            {
                int textureIndex = thumbTex.GetInt32();
                int imageIndex = textureIndex >= 0 && textureIndex < model.TextureToImage.Count
                    ? model.TextureToImage[textureIndex]
                    : -1;
                if (imageIndex >= 0)
                {
                    model.ThumbnailImageIndex = imageIndex;
                }
            }
        }

        if (vrm.TryGetProperty("humanoid", out JsonElement humanoid) &&
            humanoid.TryGetProperty("humanBones", out JsonElement humanBones))
        {
            foreach (JsonElement entry in humanBones.EnumerateArray())
            {
                string bone = GetString(entry, "bone");
                if (bone != null && entry.TryGetProperty("node", out JsonElement node))
                {
                    model.HumanBones[NormalizeVrm0BoneName(bone)] = node.GetInt32();
                }
            }
        }

        if (vrm.TryGetProperty("firstPerson", out JsonElement firstPerson))
        {
            if (firstPerson.TryGetProperty("firstPersonBoneOffset", out JsonElement fpOffset))
            {
                model.FirstPersonOffset = ReadVec3Object(fpOffset);
            }
            if (firstPerson.TryGetProperty("meshAnnotations", out JsonElement meshAnnotations))
            {
                foreach (JsonElement annotation in meshAnnotations.EnumerateArray())
                {
                    if (!annotation.TryGetProperty("mesh", out JsonElement meshEl) ||
                        !annotation.TryGetProperty("firstPersonFlag", out JsonElement flagEl))
                    {
                        continue;
                    }
                    VrmFirstPersonFlag? flag = ParseFirstPersonFlag(flagEl.GetString());
                    if (!flag.HasValue)
                    {
                        continue;
                    }
                    model.FirstPersonMeshAnnotations.Add(new VrmFirstPersonMeshAnnotation
                    {
                        MeshIndex = meshEl.GetInt32(),
                        Flag = flag.Value,
                    });
                }
            }
        }
        AddDefaultFirstPersonAutoAnnotations(model, nodeBased: false);

        if (vrm.TryGetProperty("blendShapeMaster", out JsonElement master) &&
            master.TryGetProperty("blendShapeGroups", out JsonElement groups))
        {
            foreach (JsonElement group in groups.EnumerateArray())
            {
                var expression = new VrmExpression
                {
                    Name = GetString(group, "name"),
                    Preset = NormalizeVrm0Preset(GetString(group, "presetName"), GetString(group, "name")),
                    IsBinary = group.TryGetProperty("isBinary", out JsonElement binary) && binary.GetBoolean(),
                };
                if (group.TryGetProperty("binds", out JsonElement binds))
                {
                    foreach (JsonElement bind in binds.EnumerateArray())
                    {
                        if (!bind.TryGetProperty("mesh", out JsonElement meshEl) ||
                            !bind.TryGetProperty("index", out JsonElement indexEl))
                        {
                            continue;
                        }
                        float weight = bind.TryGetProperty("weight", out JsonElement w) ? w.GetSingle() : 100f;
                        expression.Binds.Add(new VrmExpressionBind
                        {
                            MeshIndex = meshEl.GetInt32(),
                            MorphIndex = indexEl.GetInt32(),
                            Weight = weight / 100f,
                        });
                    }
                }
                model.Expressions.Add(expression);
            }
        }

        if (vrm.TryGetProperty("secondaryAnimation", out JsonElement secondary))
        {
            // Flatten collider groups: group index -> list of model.SpringColliders indices.
            var groupToColliders = new List<List<int>>();
            if (secondary.TryGetProperty("colliderGroups", out JsonElement colliderGroups))
            {
                foreach (JsonElement colliderGroup in colliderGroups.EnumerateArray())
                {
                    var indices = new List<int>();
                    int nodeIndex = colliderGroup.TryGetProperty("node", out JsonElement node) ? node.GetInt32() : -1;
                    if (colliderGroup.TryGetProperty("colliders", out JsonElement colliders))
                    {
                        foreach (JsonElement collider in colliders.EnumerateArray())
                        {
                            indices.Add(model.SpringColliders.Count);
                            model.SpringColliders.Add(new VrmSpringCollider
                            {
                                NodeIndex = nodeIndex,
                                Offset = collider.TryGetProperty("offset", out JsonElement off)
                                    ? ReadVec3Object(off) : Vec3.Zero,
                                Radius = collider.TryGetProperty("radius", out JsonElement r) ? r.GetSingle() : 0f,
                            });
                        }
                    }
                    groupToColliders.Add(indices);
                }
            }

            if (secondary.TryGetProperty("boneGroups", out JsonElement boneGroups))
            {
                foreach (JsonElement boneGroup in boneGroups.EnumerateArray())
                {
                    var chain = new VrmSpringChain
                    {
                        Name = GetString(boneGroup, "comment"),
                        // Note: the VRM 0.x spec really does spell it "stiffiness".
                        Stiffness = boneGroup.TryGetProperty("stiffiness", out JsonElement st) ? st.GetSingle() : 1f,
                        GravityPower = boneGroup.TryGetProperty("gravityPower", out JsonElement gp) ? gp.GetSingle() : 0f,
                        GravityDir = boneGroup.TryGetProperty("gravityDir", out JsonElement gd)
                            ? ReadVec3Object(gd) : new Vec3(0f, -1f, 0f),
                        DragForce = boneGroup.TryGetProperty("dragForce", out JsonElement df) ? df.GetSingle() : 0.4f,
                        HitRadius = boneGroup.TryGetProperty("hitRadius", out JsonElement hr) ? hr.GetSingle() : 0.02f,
                    };
                    if (boneGroup.TryGetProperty("bones", out JsonElement bones))
                    {
                        foreach (JsonElement bone in bones.EnumerateArray())
                        {
                            chain.RootNodes.Add(bone.GetInt32());
                        }
                    }
                    if (boneGroup.TryGetProperty("colliderGroups", out JsonElement cg))
                    {
                        foreach (JsonElement groupIndex in cg.EnumerateArray())
                        {
                            int gi = groupIndex.GetInt32();
                            if (gi >= 0 && gi < groupToColliders.Count)
                            {
                                chain.ColliderIndices.AddRange(groupToColliders[gi]);
                            }
                        }
                    }
                    if (chain.RootNodes.Count > 0)
                    {
                        model.SpringChains.Add(chain);
                    }
                }
            }
        }

        // VRM0 material properties override the base glTF info (MToon lives here).
        if (vrm.TryGetProperty("materialProperties", out JsonElement materialProperties))
        {
            foreach (JsonElement mat in materialProperties.EnumerateArray())
            {
                string name = GetString(mat, "name");
                if (name == null)
                {
                    continue;
                }
                if (!model.Materials.TryGetValue(name, out VrmMaterialInfo info))
                {
                    info = new VrmMaterialInfo { Name = name };
                    model.Materials[name] = info;
                }
                string shader = GetString(mat, "shader") ?? "";
                info.IsMToon = shader.Contains("MToon", StringComparison.OrdinalIgnoreCase);

                if (mat.TryGetProperty("renderQueue", out JsonElement renderQueue) &&
                    renderQueue.ValueKind == JsonValueKind.Number)
                {
                    int queue = renderQueue.GetInt32();
                    if (queue > 0)
                    {
                        info.RenderQueue = queue;
                    }
                }

                if (mat.TryGetProperty("keywordMap", out JsonElement keywords))
                {
                    if (keywords.TryGetProperty("_ALPHABLEND_ON", out JsonElement ab) && ab.GetBoolean())
                    {
                        info.AlphaMode = "blend";
                    }
                    else if (keywords.TryGetProperty("_ALPHATEST_ON", out JsonElement at) && at.GetBoolean())
                    {
                        info.AlphaMode = "mask";
                    }
                }
                if (mat.TryGetProperty("floatProperties", out JsonElement floats))
                {
                    if (floats.TryGetProperty("_Cutoff", out JsonElement cutoff))
                    {
                        info.AlphaCutoff = cutoff.GetSingle();
                    }
                    if (floats.TryGetProperty("_OutlineWidth", out JsonElement ow))
                    {
                        // MToon 0.x stores the width in centimeters; normalize to meters.
                        info.OutlineWidth = ow.GetSingle() * 0.01f;
                    }
                    if (floats.TryGetProperty("_OutlineWidthMode", out JsonElement owm))
                    {
                        info.OutlineWidthMode = (int)owm.GetSingle() switch
                        {
                            1 => "world",
                            2 => "screen",
                            _ => "none",
                        };
                        info.OutlineWidthUsesLegacyScreenDamping = info.OutlineWidthMode == "screen";
                    }
                    if (info.OutlineWidthMode == "none")
                    {
                        info.OutlineWidth = 0f;
                    }
                    if (floats.TryGetProperty("_CullMode", out JsonElement cull))
                    {
                        // 0 = off (double sided), 1 = front, 2 = back.
                        info.DoubleSided = (int)cull.GetSingle() == 0;
                    }
                    info.TransparentWithZWrite = info.AlphaMode == "blend" &&
                        (!floats.TryGetProperty("_ZWrite", out JsonElement zwrite) || zwrite.GetSingle() >= 0.5f);

                    // MToon 0.x shading params -> MToon 1.0 semantics, per UniVRM's
                    // MToon10Migrator (range min/max -> toony = margin, shift = -center).
                    float shadeShift0 = floats.TryGetProperty("_ShadeShift", out JsonElement ss0)
                        ? ss0.GetSingle() : 0f;
                    float shadeToony0 = floats.TryGetProperty("_ShadeToony", out JsonElement st0)
                        ? st0.GetSingle() : 0.9f;
                    float rangeMin = shadeShift0;
                    float rangeMax = shadeShift0 + (1f - shadeShift0) * (1f - shadeToony0);
                    info.ShadingToony = Math.Clamp((2f - (rangeMax - rangeMin)) * 0.5f, 0f, 1f);
                    info.ShadingShift = Math.Clamp((rangeMax + rangeMin) * 0.5f * -1f, -1f, 1f);

                    // Outline color mode Fixed (0) means unlit fixed color -> lighting mix 0.
                    float outlineColorMode = floats.TryGetProperty("_OutlineColorMode", out JsonElement ocm)
                        ? ocm.GetSingle() : 0f;
                    float outlineLightingMix = floats.TryGetProperty("_OutlineLightingMix", out JsonElement olm0)
                        ? olm0.GetSingle() : 1f;
                    info.OutlineLightingMix = (int)outlineColorMode == 0 ? 0f : outlineLightingMix;

                    info.RimLightingMix = floats.TryGetProperty("_RimLightingMix", out JsonElement rlm0)
                        ? rlm0.GetSingle() : 0f;
                    info.RimFresnelPower = floats.TryGetProperty("_RimFresnelPower", out JsonElement rfp0)
                        ? rfp0.GetSingle() : 1f;
                    info.RimLift = floats.TryGetProperty("_RimLift", out JsonElement rlf0)
                        ? rlf0.GetSingle() : 0f;
                }
                if (mat.TryGetProperty("textureProperties", out JsonElement textureProps))
                {
                    int ResolveVrm0Image(string property)
                    {
                        if (!textureProps.TryGetProperty(property, out JsonElement texture))
                        {
                            return -1;
                        }
                        int textureIndex = texture.GetInt32();
                        return textureIndex >= 0 && textureIndex < model.TextureToImage.Count
                            ? model.TextureToImage[textureIndex]
                            : -1;
                    }
                    int outlineImage = ResolveVrm0Image("_OutlineWidthTexture");
                    if (outlineImage >= 0)
                    {
                        info.OutlineWidthImageIndex = outlineImage;
                    }
                    int matcapImage = ResolveVrm0Image("_SphereAdd");
                    if (matcapImage >= 0)
                    {
                        info.MatcapImageIndex = matcapImage;
                    }
                }
                if (mat.TryGetProperty("vectorProperties", out JsonElement vectors))
                {
                    if (vectors.TryGetProperty("_ShadeColor", out JsonElement shade))
                    {
                        info.ShadeColor = ReadVec4Array(shade);
                    }
                    if (vectors.TryGetProperty("_Color", out JsonElement baseColor))
                    {
                        info.BaseColor = ReadVec4Array(baseColor);
                    }
                    if (vectors.TryGetProperty("_OutlineColor", out JsonElement outline))
                    {
                        info.OutlineColor = ReadVec4Array(outline);
                    }
                    if (vectors.TryGetProperty("_EmissionColor", out JsonElement emission))
                    {
                        info.EmissionColor = ReadVec4Array(emission);
                    }
                    if (vectors.TryGetProperty("_RimColor", out JsonElement rim))
                    {
                        info.RimColor = ReadVec4Array(rim);
                    }
                }
            }
        }
    }

    /// <summary>VRM0 bone names are already camelCase like VRM1, except the thumb chain.</summary>
    private static string NormalizeVrm0BoneName(string bone)
    {
        return bone switch
        {
            "leftThumbProximal" => "leftThumbMetacarpal",
            "leftThumbIntermediate" => "leftThumbProximal",
            "rightThumbProximal" => "rightThumbMetacarpal",
            "rightThumbIntermediate" => "rightThumbProximal",
            _ => bone,
        };
    }

    private static string NormalizeVrm0Preset(string preset, string fallbackName)
    {
        return preset switch
        {
            "a" => "aa",
            "i" => "ih",
            "u" => "ou",
            "e" => "ee",
            "o" => "oh",
            "blink" => "blink",
            "blink_l" => "blinkLeft",
            "blink_r" => "blinkRight",
            "joy" => "happy",
            "angry" => "angry",
            "sorrow" => "sad",
            "fun" => "relaxed",
            "neutral" => "neutral",
            "lookup" => "lookUp",
            "lookdown" => "lookDown",
            "lookleft" => "lookLeft",
            "lookright" => "lookRight",
            _ => fallbackName ?? preset,
        };
    }

    // ---------------------------------------------------------------- VRM 1.0

    private static void ParseVrm1(JsonElement extensions, JsonElement vrm, JsonElement gltfRoot, VrmModel model)
    {
        if (vrm.TryGetProperty("meta", out JsonElement meta))
        {
            model.MetaJson = meta.GetRawText();
            model.Title = GetString(meta, "name");
            if (meta.TryGetProperty("authors", out JsonElement authors) &&
                authors.ValueKind == JsonValueKind.Array)
            {
                model.Author = string.Join(", ", authors.EnumerateArray().Select(a => a.GetString()));
            }

            // VRM1 stores the thumbnail directly as a glTF image index.
            if (meta.TryGetProperty("thumbnailImage", out JsonElement thumbImage) &&
                thumbImage.ValueKind == JsonValueKind.Number)
            {
                int imageIndex = thumbImage.GetInt32();
                if (imageIndex >= 0)
                {
                    model.ThumbnailImageIndex = imageIndex;
                }
            }
        }

        if (vrm.TryGetProperty("humanoid", out JsonElement humanoid) &&
            humanoid.TryGetProperty("humanBones", out JsonElement humanBones))
        {
            foreach (JsonProperty bone in humanBones.EnumerateObject())
            {
                if (bone.Value.TryGetProperty("node", out JsonElement node))
                {
                    model.HumanBones[bone.Name] = node.GetInt32();
                }
            }
        }

        if (vrm.TryGetProperty("lookAt", out JsonElement lookAt) &&
            lookAt.TryGetProperty("offsetFromHeadBone", out JsonElement offset))
        {
            model.FirstPersonOffset = ReadVec3Array(offset);
        }

        if (vrm.TryGetProperty("firstPerson", out JsonElement firstPerson) &&
            firstPerson.TryGetProperty("meshAnnotations", out JsonElement meshAnnotations))
        {
            foreach (JsonElement annotation in meshAnnotations.EnumerateArray())
            {
                if (!annotation.TryGetProperty("node", out JsonElement nodeEl) ||
                    !annotation.TryGetProperty("type", out JsonElement typeEl))
                {
                    continue;
                }
                VrmFirstPersonFlag? flag = ParseFirstPersonFlag(typeEl.GetString());
                if (!flag.HasValue)
                {
                    continue;
                }
                int nodeIndex = nodeEl.GetInt32();
                int meshIndex = nodeIndex >= 0 && nodeIndex < model.NodeMeshIndices.Count
                    ? model.NodeMeshIndices[nodeIndex]
                    : -1;
                model.FirstPersonMeshAnnotations.Add(new VrmFirstPersonMeshAnnotation
                {
                    NodeIndex = nodeIndex,
                    MeshIndex = meshIndex,
                    Flag = flag.Value,
                });
            }
        }
        AddDefaultFirstPersonAutoAnnotations(model, nodeBased: true);

        if (vrm.TryGetProperty("expressions", out JsonElement expressions))
        {
            if (expressions.TryGetProperty("preset", out JsonElement preset))
            {
                foreach (JsonProperty entry in preset.EnumerateObject())
                {
                    ParseVrm1Expression(entry.Name, entry.Name, entry.Value, model);
                }
            }
            if (expressions.TryGetProperty("custom", out JsonElement custom))
            {
                foreach (JsonProperty entry in custom.EnumerateObject())
                {
                    ParseVrm1Expression(entry.Name, entry.Name, entry.Value, model);
                }
            }
        }

        if (extensions.TryGetProperty("VRMC_springBone", out JsonElement springBone))
        {
            ParseVrm1SpringBones(springBone, model);
        }
    }

    private static void ParseVrm1Expression(string preset, string name, JsonElement expression, VrmModel model)
    {
        var result = new VrmExpression
        {
            Preset = preset,
            Name = name,
            IsBinary = expression.TryGetProperty("isBinary", out JsonElement binary) && binary.GetBoolean(),
        };
        if (expression.TryGetProperty("morphTargetBinds", out JsonElement binds))
        {
            foreach (JsonElement bind in binds.EnumerateArray())
            {
                if (!bind.TryGetProperty("node", out JsonElement nodeEl) ||
                    !bind.TryGetProperty("index", out JsonElement indexEl))
                {
                    continue;
                }
                int nodeIndex = nodeEl.GetInt32();
                int meshIndex = nodeIndex >= 0 && nodeIndex < model.NodeMeshIndices.Count
                    ? model.NodeMeshIndices[nodeIndex] : -1;
                if (meshIndex < 0)
                {
                    continue;
                }
                result.Binds.Add(new VrmExpressionBind
                {
                    MeshIndex = meshIndex,
                    MorphIndex = indexEl.GetInt32(),
                    Weight = bind.TryGetProperty("weight", out JsonElement w) ? w.GetSingle() : 1f,
                });
            }
        }
        model.Expressions.Add(result);
    }

    private static void ParseVrm1SpringBones(JsonElement springBone, VrmModel model)
    {
        if (springBone.TryGetProperty("colliders", out JsonElement colliders))
        {
            foreach (JsonElement collider in colliders.EnumerateArray())
            {
                var result = new VrmSpringCollider
                {
                    NodeIndex = collider.TryGetProperty("node", out JsonElement node) ? node.GetInt32() : -1,
                };
                if (collider.TryGetProperty("shape", out JsonElement shape))
                {
                    if (shape.TryGetProperty("sphere", out JsonElement sphere))
                    {
                        result.Offset = sphere.TryGetProperty("offset", out JsonElement so)
                            ? ReadVec3Array(so) : Vec3.Zero;
                        result.Radius = sphere.TryGetProperty("radius", out JsonElement sr) ? sr.GetSingle() : 0f;
                    }
                    else if (shape.TryGetProperty("capsule", out JsonElement capsule))
                    {
                        result.Offset = capsule.TryGetProperty("offset", out JsonElement co)
                            ? ReadVec3Array(co) : Vec3.Zero;
                        result.Radius = capsule.TryGetProperty("radius", out JsonElement cr) ? cr.GetSingle() : 0f;
                        result.Tail = capsule.TryGetProperty("tail", out JsonElement tail)
                            ? ReadVec3Array(tail) : null;
                    }
                }
                model.SpringColliders.Add(result);
            }
        }

        var groupToColliders = new List<List<int>>();
        if (springBone.TryGetProperty("colliderGroups", out JsonElement colliderGroups))
        {
            foreach (JsonElement group in colliderGroups.EnumerateArray())
            {
                var indices = new List<int>();
                if (group.TryGetProperty("colliders", out JsonElement groupColliders))
                {
                    indices.AddRange(groupColliders.EnumerateArray().Select(c => c.GetInt32()));
                }
                groupToColliders.Add(indices);
            }
        }

        if (!springBone.TryGetProperty("springs", out JsonElement springs))
        {
            return;
        }
        foreach (JsonElement spring in springs.EnumerateArray())
        {
            var chain = new VrmSpringChain { Name = GetString(spring, "name") };
            float stiffnessSum = 0f, gravitySum = 0f, dragSum = 0f, radiusSum = 0f;
            Vec3 gravityDir = new(0f, -1f, 0f);
            int jointCount = 0;
            if (spring.TryGetProperty("joints", out JsonElement joints))
            {
                foreach (JsonElement joint in joints.EnumerateArray())
                {
                    if (joint.TryGetProperty("node", out JsonElement node))
                    {
                        chain.JointNodes.Add(node.GetInt32());
                    }
                    stiffnessSum += joint.TryGetProperty("stiffness", out JsonElement st) ? st.GetSingle() : 1f;
                    gravitySum += joint.TryGetProperty("gravityPower", out JsonElement gp) ? gp.GetSingle() : 0f;
                    dragSum += joint.TryGetProperty("dragForce", out JsonElement df) ? df.GetSingle() : 0.4f;
                    radiusSum += joint.TryGetProperty("hitRadius", out JsonElement hr) ? hr.GetSingle() : 0.02f;
                    if (joint.TryGetProperty("gravityDir", out JsonElement gd))
                    {
                        gravityDir = ReadVec3Array(gd);
                    }
                    jointCount++;
                }
            }
            if (jointCount == 0)
            {
                continue;
            }
            chain.Stiffness = stiffnessSum / jointCount;
            chain.GravityPower = gravitySum / jointCount;
            chain.DragForce = dragSum / jointCount;
            chain.HitRadius = radiusSum / jointCount;
            chain.GravityDir = gravityDir;
            chain.RootNodes.Add(chain.JointNodes[0]);
            if (spring.TryGetProperty("colliderGroups", out JsonElement springGroups))
            {
                foreach (JsonElement groupIndex in springGroups.EnumerateArray())
                {
                    int gi = groupIndex.GetInt32();
                    if (gi >= 0 && gi < groupToColliders.Count)
                    {
                        chain.ColliderIndices.AddRange(groupToColliders[gi]);
                    }
                }
            }
            model.SpringChains.Add(chain);
        }
    }

    // ---------------------------------------------------------------- image extraction

    /// <summary>
    /// Extracts an embedded image from the GLB binary chunk (used for MToon-only
    /// textures like the outline width mask, which Assimp never imports because no
    /// standard glTF material slot references them).
    /// </summary>
    public static (byte[] data, string extension) ExtractImage(string vrmPath, int imageIndex)
    {
        using FileStream stream = File.OpenRead(vrmPath);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != 0x46546C67)
        {
            throw new InvalidDataException("GLBファイルではありません。");
        }
        reader.ReadUInt32(); // container version
        reader.ReadUInt32(); // total length

        byte[] jsonBytes = null;
        long binOffset = -1;
        long binLength = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            uint chunkLength = reader.ReadUInt32();
            uint chunkType = reader.ReadUInt32();
            if (chunkType == 0x4E4F534A) // "JSON"
            {
                jsonBytes = reader.ReadBytes(checked((int)chunkLength));
            }
            else if (chunkType == 0x004E4942) // "BIN\0"
            {
                binOffset = stream.Position;
                binLength = chunkLength;
                stream.Seek(chunkLength, SeekOrigin.Current);
            }
            else
            {
                stream.Seek(chunkLength, SeekOrigin.Current);
            }
        }
        if (jsonBytes == null || binOffset < 0)
        {
            throw new InvalidDataException("GLBチャンクの解析に失敗しました。");
        }

        using JsonDocument doc = JsonDocument.Parse(jsonBytes);
        JsonElement root = doc.RootElement;
        JsonElement image = root.GetProperty("images")[imageIndex];
        string mimeType = image.TryGetProperty("mimeType", out JsonElement mime) ? mime.GetString() : null;
        if (!image.TryGetProperty("bufferView", out JsonElement bufferViewIndex))
        {
            throw new InvalidDataException($"画像 {imageIndex} はバイナリチャンクに格納されていません。");
        }
        JsonElement bufferView = root.GetProperty("bufferViews")[bufferViewIndex.GetInt32()];
        long byteOffset = bufferView.TryGetProperty("byteOffset", out JsonElement off) ? off.GetInt64() : 0;
        int byteLength = bufferView.GetProperty("byteLength").GetInt32();
        if (byteOffset + byteLength > binLength)
        {
            throw new InvalidDataException($"画像 {imageIndex} のbufferViewがバイナリチャンク外を指しています。");
        }

        stream.Seek(binOffset + byteOffset, SeekOrigin.Begin);
        byte[] data = reader.ReadBytes(byteLength);
        string extension = mimeType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            _ => ".png",
        };
        return (data, extension);
    }

    // ---------------------------------------------------------------- helpers

    private static string GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static Vec3 ReadVec3Object(JsonElement element)
    {
        float x = element.TryGetProperty("x", out JsonElement xe) ? xe.GetSingle() : 0f;
        float y = element.TryGetProperty("y", out JsonElement ye) ? ye.GetSingle() : 0f;
        float z = element.TryGetProperty("z", out JsonElement ze) ? ze.GetSingle() : 0f;
        return new Vec3(x, y, z);
    }

    private static Vec3 ReadVec3Array(JsonElement element)
    {
        float[] values = element.EnumerateArray().Take(3).Select(e => e.GetSingle()).ToArray();
        return new Vec3(
            values.Length > 0 ? values[0] : 0f,
            values.Length > 1 ? values[1] : 0f,
            values.Length > 2 ? values[2] : 0f);
    }

    private static Vec4 ReadVec4Array(JsonElement element)
    {
        float[] values = element.EnumerateArray().Take(4).Select(e => e.GetSingle()).ToArray();
        return new Vec4(
            values.Length > 0 ? values[0] : 0f,
            values.Length > 1 ? values[1] : 0f,
            values.Length > 2 ? values[2] : 0f,
            values.Length > 3 ? values[3] : 1f);
    }

    private static VrmFirstPersonFlag? ParseFirstPersonFlag(string value)
    {
        string normalized = new string((value ?? "")
            .Where(c => c != ' ' && c != '_' && c != '-')
            .Select(char.ToLowerInvariant)
            .ToArray());
        return normalized switch
        {
            "auto" => VrmFirstPersonFlag.Auto,
            "both" => VrmFirstPersonFlag.Both,
            "thirdpersononly" => VrmFirstPersonFlag.ThirdPersonOnly,
            "firstpersononly" => VrmFirstPersonFlag.FirstPersonOnly,
            _ => null,
        };
    }

    private static void AddDefaultFirstPersonAutoAnnotations(VrmModel model, bool nodeBased)
    {
        if (nodeBased)
        {
            var explicitNodes = model.FirstPersonMeshAnnotations
                .Where(a => a.NodeIndex >= 0)
                .Select(a => a.NodeIndex)
                .ToHashSet();
            for (int nodeIndex = 0; nodeIndex < model.NodeMeshIndices.Count; nodeIndex++)
            {
                int meshIndex = model.NodeMeshIndices[nodeIndex];
                if (meshIndex < 0 || explicitNodes.Contains(nodeIndex))
                {
                    continue;
                }
                model.FirstPersonMeshAnnotations.Add(new VrmFirstPersonMeshAnnotation
                {
                    NodeIndex = nodeIndex,
                    MeshIndex = meshIndex,
                    Flag = VrmFirstPersonFlag.Auto,
                });
            }
            return;
        }

        var explicitMeshes = model.FirstPersonMeshAnnotations
            .Where(a => a.MeshIndex >= 0)
            .Select(a => a.MeshIndex)
            .ToHashSet();
        foreach (int meshIndex in model.MeshToNodes.Keys.OrderBy(i => i))
        {
            if (explicitMeshes.Contains(meshIndex))
            {
                continue;
            }
            model.FirstPersonMeshAnnotations.Add(new VrmFirstPersonMeshAnnotation
            {
                MeshIndex = meshIndex,
                Flag = VrmFirstPersonFlag.Auto,
            });
        }
    }
}
