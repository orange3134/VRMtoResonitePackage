using System.Numerics;
using FrooxEngine;
using Elements.Core;
using Renderite.Shared;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

// Source positions are in Unity metres relative to the descriptor origin.
internal sealed record VrchatViewpoint(Vector3 View, Vector3[] Anchors)
{
    private static readonly (string Name, BodyNode Node)[] Bones =
    {
        ("hips", BodyNode.Hips), ("head", BodyNode.Head),
        ("leftUpperArm", BodyNode.LeftUpperArm), ("rightUpperArm", BodyNode.RightUpperArm)
    };

    internal static VrchatViewpoint Capture(UnityPackage package, VrchatAvatar avatar, string guid, long owner)
    {
        if (avatar.ViewPosition is not {} view || owner == 0 || guid != avatar.FbxGuid || avatar.FbxParentFbxGuid != null ||
            avatar.FbxParentTransforms.Count != 0) return null;
        var resolver = package.ModelFileIds(guid);
        var hierarchy = resolver.Hierarchy;
        if (hierarchy == null || hierarchy.Count == 0 || !resolver.IsRootFileId(owner)) return null;
        var byPath = hierarchy.ToLookup(n => n.Path);
        var overrides = package.PrefabGraph.Modifications.Where(m => m.Target.Occurrence == guid &&
                m.Value["propertyPath"]?.AsString()?.Split('.')[0] is "m_LocalPosition" or "m_LocalScale" or "m_LocalRotation" or "m_Father")
            .ToLookup(m => resolver.IsRootFileId(m.Target.FileId) ? hierarchy[0].Path : resolver.ResolveNodePath(m.Target.FileId));
        var anchors = new Vector3[Bones.Length];
        for (int i = 0; i < Bones.Length; i++)
        {
            if (!avatar.HumanBoneTargets.TryGetValue(Bones[i].Name, out var target) || target.Path == null) return null;
            string path = target.Path;
            Vector3 point = Vector3.Zero;
            while (!string.IsNullOrEmpty(path))
            {
                if (byPath[path].Count() != 1) return null;
                var node = byPath[path].Single();
                Vector3 position = node.Position * avatar.FbxImportScale, scale = node.Scale;
                Quaternion rotation = node.Rotation;
                foreach (var change in overrides[path])
                {
                    string property = change.Value["propertyPath"]?.AsString();
                    if (property == "m_Father") return null;
                    if (property == null || property.Length < 3 || property[^2] != '.') continue;
                    int axis = "xyzw".IndexOf(property[^1]);
                    if (axis < 0) continue;
                    float value = change.Value["value"]?.AsFloat(float.NaN) ?? float.NaN;
                    switch (property[..^2])
                    {
                        case "m_LocalPosition" when axis < 3: position[axis] = value; break;
                        case "m_LocalScale" when axis < 3: scale[axis] = value; break;
                        case "m_LocalRotation": rotation[axis] = value; break;
                    }
                }
                point = Vector3.Transform(point * scale, Quaternion.Normalize(rotation));
                if (node != hierarchy[0]) point += position;
                path = node.ParentPath;
            }
            anchors[i] = point;
        }
        return new(view, anchors);
    }

    internal static VrchatViewpoint CaptureUnpacked(VrchatAvatar avatar, UnityScene scene, string guid, long owner)
    {
        var root = scene?.TransformOfGameObject(owner);
        if (avatar.ViewPosition is not {} view || root?.Root?["m_Father"]?.FileID != 0) return null;

        var bonesByPath = avatar.AuthoredImportedBones
            .Where(b => b.PrefabGuid == guid && b.FbxGuid == avatar.FbxGuid && b.Path != null)
            .ToLookup(b => UnityModelPath.Normalize(b.Path));
        var anchors = new Vector3[Bones.Length];
        for (int i = 0; i < Bones.Length; i++)
        {
            if (!avatar.HumanBoneTargets.TryGetValue(Bones[i].Name, out var bone) || bone.Path == null) return null;
            var matches = bonesByPath[UnityModelPath.Normalize(bone.Path)];
            if (matches.Count() != 1) return null;

            var transform = scene.Doc(matches.Single().TransformFileId);
            var visited = new HashSet<long>();
            Vector3 point = Vector3.Zero;
            while (true)
            {
                if (transform?.ClassId != 4 || transform.Stripped || !visited.Add(transform.FileId)) return null;
                var pose = scene.GetLocalTransform(transform.Root?["m_GameObject"]?.FileID ?? 0);
                if (!pose.Found) return null;
                point = Vector3.Transform(point * pose.Scale, Quaternion.Normalize(pose.Rotation));
                // ViewPosition is a world-axis offset from the descriptor origin.
                // Keep the root rotation and scale, but omit its translation.
                if (transform.FileId == root.FileId) break;
                point += pose.Position;
                var parent = transform.Root["m_Father"];
                if (parent?.Guid != null && parent.Guid != guid) return null;
                transform = scene.Doc(parent?.FileID ?? 0);
            }
            anchors[i] = point;
        }
        return new(view, anchors);
    }

    internal float3? Resolve(BipedRig rig)
    {
        var positions = new Vector3[Bones.Length];
        for (int i = 0; i < Bones.Length; i++)
        {
            if (rig.TryGetBone(Bones[i].Node) is not {} bone) return null;
            var p = bone.GlobalPosition;
            positions[i] = new(p.x, p.y, p.z);
        }
        return Map(positions);
    }

    internal float3? Map(Vector3[] positions)
    {
        // Construct equivalent body frames from the bones in each system. Check
        // all four landmarks before accepting the similarity transform.
        static Matrix4x4 Frame(Vector3[] a)
        {
            var right = Vector3.Normalize(a[3] - a[2]);
            var forward = Vector3.Normalize(Vector3.Cross(right, a[1] - a[0]));
            var up = Vector3.Cross(forward, right);
            return new(right.X, right.Y, right.Z, 0,
                up.X, up.Y, up.Z, 0,
                forward.X, forward.Y, forward.Z, 0,
                0, 0, 0, 1);
        }
        var relative = Matrix4x4.Transpose(Frame(Anchors)) * Frame(positions);
        float scale = Vector3.Distance(positions[1], positions[0]) / Vector3.Distance(Anchors[1], Anchors[0]);
        Vector3 Convert(Vector3 p) => positions[0] + Vector3.TransformNormal(p - Anchors[0], relative) * scale;
        for (int i = 1; i < Bones.Length; i++)
            if (!(Vector3.Distance(Convert(Anchors[i]), positions[i]) <= .003f)) return null;
        var v = Convert(View);
        return float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) ? new float3(v.X, v.Y, v.Z) : null;
    }
}
