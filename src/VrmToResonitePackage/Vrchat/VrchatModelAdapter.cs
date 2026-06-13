using Elements.Core;
using VrmToResonitePackage.Vrm;

namespace VrmToResonitePackage.Vrchat;

/// <summary>
/// Adapts a parsed <see cref="VrchatAvatar"/> into the <see cref="VrmModel"/> shape the downstream
/// avatar/rig/spring setup consumes. Everything resolves by bone/mesh GameObject name (the slot
/// names after FBX import) and by blendshape name/index, so synthetic glTF-style node/mesh indices
/// are just an indirection layer over those names.
/// </summary>
public static class VrchatModelAdapter
{
    public static VrmModel ToVrmModel(VrchatAvatar avatar)
    {
        var model = new VrmModel { Source = ModelSource.VrchatFbx, Title = avatar.Name };

        // Intern bone/mesh names into a synthetic node table (index -> name).
        var nodeIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        int NodeFor(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return -1;
            }
            if (!nodeIndexByName.TryGetValue(name, out int index))
            {
                index = model.NodeNames.Count;
                model.NodeNames.Add(name);
                model.NodeMeshIndices.Add(-1);
                nodeIndexByName[name] = index;
            }
            return index;
        }

        // Humanoid bones.
        foreach ((string vrmBone, string boneName) in avatar.HumanBones)
        {
            int node = NodeFor(boneName);
            if (node >= 0)
            {
                model.HumanBones[vrmBone] = node;
            }
        }

        // One synthetic mesh per face/eyelid GameObject that owns blendshapes.
        var meshIndexByGameObject = new Dictionary<string, int>(StringComparer.Ordinal);
        int MeshFor(string gameObjectName)
        {
            if (!meshIndexByGameObject.TryGetValue(gameObjectName, out int meshIndex))
            {
                meshIndex = model.MeshTargetNames.Count;
                model.MeshTargetNames.Add(new List<string>());
                model.MeshToNodes[meshIndex] = new List<int> { NodeFor(gameObjectName) };
                meshIndexByGameObject[gameObjectName] = meshIndex;
            }
            return meshIndex;
        }

        // Visemes (resolved by blendshape name on the viseme mesh).
        foreach (VrchatViseme viseme in avatar.Visemes)
        {
            int meshIndex = MeshFor(viseme.MeshGameObjectName);
            List<string> targetNames = model.MeshTargetNames[meshIndex];
            int morphIndex = targetNames.Count;
            targetNames.Add(viseme.BlendShapeName);
            var expression = new VrmExpression { Preset = viseme.ResonitePreset, Name = viseme.ResonitePreset };
            expression.Binds.Add(new VrmExpressionBind { MeshIndex = meshIndex, MorphIndex = morphIndex, Weight = 1f });
            model.Expressions.Add(expression);
        }

        // Blink (resolved by blendshape index on the eyelid mesh; VRChat stores an index, not a name).
        if (avatar.Blink != null)
        {
            int meshIndex = MeshFor(avatar.Blink.MeshGameObjectName);
            var blink = new VrmExpression { Preset = "blink", Name = "blink" };
            blink.Binds.Add(new VrmExpressionBind
            {
                MeshIndex = meshIndex,
                MorphIndex = avatar.Blink.BlendShapeIndex,
                Weight = 1f,
            });
            model.Expressions.Add(blink);
        }

        // Spring bones from PhysBones. Identical colliders shared by several PhysBones are
        // de-duplicated so chains reference one collider instance instead of a private copy each.
        var colliderIndexBySignature = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (VrchatPhysBone pb in avatar.PhysBones)
        {
            int rootNode = NodeFor(pb.RootBoneName);
            if (rootNode < 0)
            {
                continue;
            }
            var chain = new VrmSpringChain { Name = pb.RootBoneName, HitRadius = MathX.Max(0.001f, pb.Radius) };
            chain.RootNodes.Add(rootNode);
            foreach (VrchatPhysBoneCollider collider in pb.Colliders)
            {
                int node = NodeFor(collider.AttachBoneName);
                if (node < 0)
                {
                    continue;
                }
                string tail = collider.Tail.HasValue
                    ? FormattableString.Invariant($"{collider.Tail.Value.X:F4},{collider.Tail.Value.Y:F4},{collider.Tail.Value.Z:F4}")
                    : "-";
                string signature = FormattableString.Invariant(
                    $"{node}|{collider.Offset.X:F4},{collider.Offset.Y:F4},{collider.Offset.Z:F4}|{tail}|{collider.Radius:F4}");
                if (!colliderIndexBySignature.TryGetValue(signature, out int colliderIndex))
                {
                    colliderIndex = model.SpringColliders.Count;
                    model.SpringColliders.Add(new VrmSpringCollider
                    {
                        NodeIndex = node,
                        Offset = collider.Offset,
                        Tail = collider.Tail,
                        Radius = MathX.Max(0.001f, collider.Radius),
                    });
                    colliderIndexBySignature[signature] = colliderIndex;
                }
                if (!chain.ColliderIndices.Contains(colliderIndex))
                {
                    chain.ColliderIndices.Add(colliderIndex);
                }
            }
            model.SpringChains.Add(chain);
        }

        return model;
    }
}
