using Elements.Core;
using Vec3 = System.Numerics.Vector3;
using Quat = System.Numerics.Quaternion;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>
/// Reads a VRChat avatar out of an extracted .unitypackage: locates the primary avatar prefab
/// (the one carrying a VRCAvatarDescriptor), then extracts the humanoid bone map (from the FBX
/// importer meta), visemes/blink/view position (from the descriptor), spring physics (PhysBones)
/// and per-renderer material assignments into an engine-independent <see cref="VrchatAvatar"/>.
/// </summary>
public static class VrchatAvatarParser
{
    private sealed class Candidate
    {
        public UnityAsset Prefab;
        public UnityScene Scene;
        public YamlDocument Root;       // root GameObject
        public YamlDocument Descriptor; // VRCAvatarDescriptor MonoBehaviour
        public int Size;                // GameObject count (hierarchy size)
    }

    /// <summary>
    /// Parses the primary avatar. <paramref name="avatarOverride"/> selects a specific avatar by
    /// prefab/root name (case-insensitive, substring) when the package holds more than one.
    /// </summary>
    public static VrchatAvatar Parse(UnityPackage package, string avatarOverride = null)
    {
        List<Candidate> candidates = FindCandidates(package);
        if (candidates.Count == 0)
        {
            throw new InvalidDataException(
                "VRCAvatarDescriptor を持つアバターのprefabが見つかりませんでした。VRChatアバターのUnityPackageか確認してください。");
        }

        Candidate selected = SelectPrimary(candidates, avatarOverride);
        foreach (Candidate c in candidates.OrderByDescending(c => c.Size))
        {
            string mark = c == selected ? "=> 選択" : "   スキップ";
            UniLog.Log($"VRChatアバター候補 {mark}: {Path.GetFileName(c.Prefab.LogicalPath)} (GameObject {c.Size})");
        }

        var avatar = new VrchatAvatar
        {
            Name = selected.Scene.GameObjectName(selected.Root.FileId) ?? Path.GetFileNameWithoutExtension(selected.Prefab.LogicalPath),
            PrefabPath = selected.Prefab.LogicalPath,
        };

        ResolveFbx(package, selected.Scene, avatar);
        ParseHumanoid(package, avatar);
        ParseDescriptor(selected.Scene, selected.Descriptor, avatar);
        ParsePhysBones(selected.Scene, avatar);
        ParseRendererMaterials(selected.Scene, avatar);
        ParseInactiveGameObjects(selected.Scene, avatar);
        return avatar;
    }

    private static void ParseInactiveGameObjects(UnityScene scene, VrchatAvatar avatar)
    {
        foreach (YamlDocument go in scene.GameObjects)
        {
            if ((go.Root?["m_IsActive"]?.AsInt(1) ?? 1) == 0)
            {
                string name = go.Root?["m_Name"]?.AsString();
                if (!string.IsNullOrEmpty(name))
                {
                    avatar.InactiveGameObjectNames.Add(name);
                }
            }
        }
        if (avatar.InactiveGameObjectNames.Count > 0)
        {
            UniLog.Log($"非アクティブGameObjectを {avatar.InactiveGameObjectNames.Count} 個検出しました。");
        }
    }

    // ---------------------------------------------------------------- candidate discovery

    private static List<Candidate> FindCandidates(UnityPackage package)
    {
        var candidates = new List<Candidate>();
        foreach (UnityAsset prefab in package.ByExtension(".prefab"))
        {
            string text = package.ReadText(prefab);
            if (text == null || !text.Contains(VrchatConstants.AvatarDescriptorScriptGuid, StringComparison.Ordinal))
            {
                continue;
            }
            UnityScene scene;
            try
            {
                scene = UnityScene.Parse(text);
            }
            catch (Exception ex)
            {
                UniLog.Warning($"prefab の解析に失敗しました ({prefab.LogicalPath}): {ex.Message}");
                continue;
            }
            foreach (YamlDocument root in scene.RootGameObjects())
            {
                // Skip prefab-variant roots (they reference a source object and only carry overrides).
                if ((root.Root?["m_CorrespondingSourceObject"]?.FileID ?? 0) != 0)
                {
                    continue;
                }
                YamlDocument descriptor = scene.FindMonoBehaviour(root, VrchatConstants.AvatarDescriptorScriptGuid);
                if (descriptor != null)
                {
                    candidates.Add(new Candidate
                    {
                        Prefab = prefab,
                        Scene = scene,
                        Root = root,
                        Descriptor = descriptor,
                        Size = scene.GameObjectCount,
                    });
                }
            }
        }
        return candidates;
    }

    private static Candidate SelectPrimary(List<Candidate> candidates, string avatarOverride)
    {
        if (!string.IsNullOrWhiteSpace(avatarOverride))
        {
            Candidate match = candidates.FirstOrDefault(c =>
                (c.Scene.GameObjectName(c.Root.FileId) ?? "").Contains(avatarOverride, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileNameWithoutExtension(c.Prefab.LogicalPath).Contains(avatarOverride, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }
            UniLog.Warning($"--avatar '{avatarOverride}' に一致する候補がないため、最大のアバターを使用します。");
        }
        // Primary = the most complete avatar (largest hierarchy); prefer non-"OLD" prefab folders and
        // shorter paths so duplicate/legacy copies don't win ties.
        return candidates
            .OrderByDescending(c => c.Size)
            .ThenBy(c => c.Prefab.LogicalPath.Contains("OLD", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(c => c.Prefab.LogicalPath.Length)
            .First();
    }

    // ---------------------------------------------------------------- FBX + humanoid

    private static void ResolveFbx(UnityPackage package, UnityScene scene, VrchatAvatar avatar)
    {
        // The humanoid FBX is the mesh referenced by the most skinned renderers.
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (YamlDocument smr in scene.SkinnedMeshRenderers)
        {
            string guid = smr.Root?["m_Mesh"]?.Guid;
            if (!string.IsNullOrEmpty(guid))
            {
                counts[guid] = counts.GetValueOrDefault(guid) + 1;
            }
        }
        string fbxGuid = counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        if (fbxGuid == null)
        {
            throw new InvalidDataException("アバターのメッシュ(FBX)参照が見つかりませんでした。");
        }
        UnityAsset fbx = package.ByGuid(fbxGuid);
        if (fbx?.HasContent != true)
        {
            throw new InvalidDataException($"FBX (guid {fbxGuid}) がパッケージに含まれていません。");
        }
        avatar.FbxGuid = fbxGuid;
        avatar.FbxPath = fbx.DiskPath;
    }

    private static void ParseHumanoid(UnityPackage package, VrchatAvatar avatar)
    {
        UnityAsset fbx = package.ByGuid(avatar.FbxGuid);
        if (fbx?.MetaPath == null || !File.Exists(fbx.MetaPath))
        {
            UniLog.Warning("FBXの.metaが見つからないため、ヒューマノイドボーンを取得できません。");
            return;
        }
        YamlNode meta = UnityYaml.ParseFlatDocument(File.ReadAllText(fbx.MetaPath));
        YamlNode human = meta["ModelImporter"]?["humanDescription"]?["human"];
        if (human?.Seq == null)
        {
            UniLog.Warning("FBXの.metaにhumanDescription.humanがありません（Humanoidリグでない可能性）。");
            return;
        }
        foreach (YamlNode entry in human.Seq)
        {
            string boneName = entry?["boneName"]?.AsString();
            string humanName = entry?["humanName"]?.AsString();
            string vrmName = VrchatConstants.NormalizeHumanName(humanName);
            if (!string.IsNullOrEmpty(boneName) && vrmName != null)
            {
                avatar.HumanBones[vrmName] = boneName;
            }
        }
        UniLog.Log($"ヒューマノイドボーンを {avatar.HumanBones.Count} 個取得しました。");
    }

    // ---------------------------------------------------------------- descriptor (viseme/blink/view)

    private static void ParseDescriptor(UnityScene scene, YamlDocument descriptor, VrchatAvatar avatar)
    {
        YamlNode d = descriptor.Root;

        YamlNode view = d?["ViewPosition"];
        if (view != null && view.IsMap)
        {
            avatar.ViewPosition = new Vec3(view.Vec("x"), view.Vec("y"), view.Vec("z"));
        }

        // Visemes: only when the descriptor uses VisemeBlendShape lip sync (lipSync == 3).
        int lipSync = d?["lipSync"]?.AsInt(-1) ?? -1;
        YamlNode visemeShapes = d?["VisemeBlendShapes"];
        long visemeMeshId = d?["VisemeSkinnedMesh"]?.FileID ?? 0;
        string visemeMesh = scene.ResolveGameObjectName(visemeMeshId);
        if (lipSync == 3 && visemeShapes?.Seq != null && visemeMesh != null)
        {
            foreach ((string preset, int idx) in VrchatConstants.VisemeToVrcSlot())
            {
                if (idx < 0 || idx >= visemeShapes.Seq.Count)
                {
                    continue;
                }
                string shape = visemeShapes.Seq[idx]?.AsString();
                if (string.IsNullOrEmpty(shape) || shape == "-none-")
                {
                    continue;
                }
                avatar.Visemes.Add(new VrchatViseme
                {
                    ResonitePreset = preset,
                    BlendShapeName = shape,
                    MeshGameObjectName = visemeMesh,
                });
            }
        }

        // Blink: customEyeLookSettings with blendshape eyelids (eyelidType == 2).
        YamlNode eye = d?["customEyeLookSettings"];
        bool eyeLook = (d?["enableEyeLook"]?.AsBool() ?? false);
        if (eyeLook && eye != null)
        {
            avatar.LeftEyeBoneName = scene.ResolveGameObjectName(eye["leftEye"]?.FileID ?? 0);
            avatar.RightEyeBoneName = scene.ResolveGameObjectName(eye["rightEye"]?.FileID ?? 0);

            int eyelidType = eye["eyelidType"]?.AsInt(0) ?? 0;
            if (eyelidType == 2)
            {
                string eyelidMesh = scene.ResolveGameObjectName(eye["eyelidsSkinnedMesh"]?.FileID ?? 0);
                // eyelidsBlendshapes = [Blink, LookingUp, LookingDown]. Resonite drives blink via the
                // EyeLinearDriver only, so take element [0] (Blink) and deliberately ignore the
                // LookingUp/LookingDown shapes (they would otherwise be mis-wired as blink).
                int[] eyelids = VrchatConstants.DecodeIntArrayHex(eye["eyelidsBlendshapes"]?.AsString());
                int blinkIndex = eyelids.Length > 0 ? eyelids[0] : -1;
                if (eyelidMesh != null && blinkIndex >= 0)
                {
                    avatar.Blink = new VrchatBlink { MeshGameObjectName = eyelidMesh, BlendShapeIndex = blinkIndex };
                }
            }
        }
    }

    // ---------------------------------------------------------------- physbones

    private static void ParsePhysBones(UnityScene scene, VrchatAvatar avatar)
    {
        foreach (YamlDocument pb in scene.MonoBehavioursByScript(
                     VrchatConstants.PhysBoneDllGuid, VrchatConstants.PhysBoneScriptFileId))
        {
            YamlNode r = pb.Root;
            long rootId = r?["rootTransform"]?.FileID ?? 0;
            string rootBone = rootId != 0
                ? scene.ResolveGameObjectName(rootId)
                : scene.ResolveGameObjectName(pb.FileId); // 0 => the component's own GameObject
            if (string.IsNullOrEmpty(rootBone))
            {
                continue;
            }

            var bone = new VrchatPhysBone
            {
                RootBoneName = rootBone,
                Pull = r?["pull"]?.AsFloat(0.2f) ?? 0.2f,
                Spring = r?["spring"]?.AsFloat(0.2f) ?? 0.2f,
                Stiffness = r?["stiffness"]?.AsFloat(0.2f) ?? 0.2f,
                Gravity = r?["gravity"]?.AsFloat(0f) ?? 0f,
                GravityFalloff = r?["gravityFalloff"]?.AsFloat(0f) ?? 0f,
                Immobile = r?["immobile"]?.AsFloat(0f) ?? 0f,
                Radius = r?["radius"]?.AsFloat(0f) ?? 0f,
            };
            YamlNode ignore = r?["ignoreTransforms"];
            if (ignore?.Seq != null)
            {
                foreach (YamlNode t in ignore.Seq)
                {
                    string name = scene.ResolveGameObjectName(t?.FileID ?? 0);
                    if (name != null)
                    {
                        bone.IgnoreBoneNames.Add(name);
                    }
                }
            }
            YamlNode colliders = r?["colliders"];
            if (colliders?.Seq != null)
            {
                foreach (YamlNode c in colliders.Seq)
                {
                    VrchatPhysBoneCollider collider = ParseCollider(scene, c?.FileID ?? 0);
                    if (collider != null)
                    {
                        bone.Colliders.Add(collider);
                    }
                }
            }
            avatar.PhysBones.Add(bone);
        }
        if (avatar.PhysBones.Count > 0)
        {
            UniLog.Log($"PhysBone を {avatar.PhysBones.Count} 個取得しました。");
        }
    }

    private static VrchatPhysBoneCollider ParseCollider(UnityScene scene, long fileId)
    {
        YamlDocument doc = scene.Doc(fileId);
        if (doc == null)
        {
            return null;
        }
        YamlNode r = doc.Root;

        // insideBounds colliders keep bones *inside* the shape (inverted collision). Resonite's
        // DynamicBoneCollider only pushes bones out, so converting these is wrong — skip them.
        if (r?["insideBounds"]?.AsBool(false) ?? false)
        {
            return null;
        }

        int shapeType = r?["shapeType"]?.AsInt(0) ?? 0;
        if (shapeType == 2) // plane: no DynamicBoneCollider equivalent.
        {
            return null;
        }
        float radius = r?["radius"]?.AsFloat(0f) ?? 0f;
        float height = r?["height"]?.AsFloat(0f) ?? 0f;
        Vec3 position = ReadVec3(r?["position"]);
        Quat rotation = ReadQuat(r?["rotation"]);

        // The shape is defined relative to rootTransform when set (the collider GameObject can live
        // outside the armature and merely follow that bone); otherwise relative to its own transform,
        // which is a child of a bone, so fold the GameObject's local transform into the bone space.
        long rootId = r?["rootTransform"]?.FileID ?? 0;
        string attachBone;
        Vec3 center;
        Quat orient;
        if (rootId != 0)
        {
            attachBone = scene.ResolveGameObjectName(rootId);
            center = position;
            orient = rotation;
        }
        else
        {
            YamlDocument ownerGo = scene.OwnerGameObject(doc);
            UnityScene.LocalTransform local = ownerGo != null ? scene.GetLocalTransform(ownerGo.FileId) : default;
            if (!local.Found)
            {
                return null;
            }
            attachBone = local.ParentName;
            center = local.Position + Vec3.Transform(position * local.Scale, local.Rotation);
            orient = local.Rotation * rotation;
        }
        if (string.IsNullOrEmpty(attachBone))
        {
            return null;
        }

        var collider = new VrchatPhysBoneCollider
        {
            AttachBoneName = attachBone,
            Radius = MathX.Max(0.001f, radius),
        };
        if (shapeType == 1 && height > radius * 2f) // capsule: endpoints along the (oriented) local Y axis.
        {
            Vec3 axis = Vec3.Transform(new Vec3(0f, height * 0.5f - radius, 0f), orient);
            collider.Offset = center - axis;
            collider.Tail = center + axis;
        }
        else
        {
            collider.Offset = center;
        }
        return collider;
    }

    private static Vec3 ReadVec3(YamlNode node)
        => node != null && node.IsMap ? new Vec3(node.Vec("x"), node.Vec("y"), node.Vec("z")) : Vec3.Zero;

    private static Quat ReadQuat(YamlNode node)
        => node != null && node.IsMap
            ? new Quat(node.Vec("x"), node.Vec("y"), node.Vec("z"), node["w"] != null ? node.Vec("w") : 1f)
            : Quat.Identity;

    // ---------------------------------------------------------------- material assignments

    private static void ParseRendererMaterials(UnityScene scene, VrchatAvatar avatar)
    {
        foreach (YamlDocument smr in scene.SkinnedMeshRenderers)
        {
            string name = scene.ResolveGameObjectName(smr.FileId);
            if (name == null)
            {
                continue;
            }
            var entry = new VrchatRendererMaterials { RendererGameObjectName = name };
            YamlNode materials = smr.Root?["m_Materials"];
            if (materials?.Seq != null)
            {
                foreach (YamlNode m in materials.Seq)
                {
                    entry.MaterialGuids.Add(m?.Guid); // may be null for a missing slot
                }
            }
            // Initial (non-zero) blendshape weights authored in the prefab (Unity 0-100 scale).
            YamlNode weights = smr.Root?["m_BlendShapeWeights"];
            if (weights?.Seq != null)
            {
                for (int i = 0; i < weights.Seq.Count; i++)
                {
                    float w = weights.Seq[i]?.AsFloat(0f) ?? 0f;
                    if (MathF.Abs(w) > 0.001f)
                    {
                        entry.InitialBlendShapes.Add((i, w));
                    }
                }
            }
            avatar.RendererMaterials.Add(entry);
        }
    }
}
