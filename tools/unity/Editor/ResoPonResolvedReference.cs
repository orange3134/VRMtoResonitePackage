#if UNITY_EDITOR
// Independently implemented against Unity 2022.3 Editor APIs. No external source copied.
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

public static class ResoPonResolvedReference
{
    private const string BoneType = "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone";
    private const string ColliderType = "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider";

    [Serializable] private sealed class ViewpointSnapshot
    {
        public string format = "resopon.unity-avatar-viewpoint";
        public int schemaVersion = 1;
        public string prefabGuid, unityVersion = Application.unityVersion;
        public string coordinateSystem = "Unity world metres; descriptor.position + ViewPosition";
        public List<DescriptorRecord> descriptors = new List<DescriptorRecord>();
    }
    [Serializable] private sealed class DescriptorRecord : Identity
    {
        public string transform;
        public Vector3 viewPosition, descriptorWorldPosition, viewWorldPosition;
        public Vector3 descriptorWorldScale;
        public Quaternion descriptorWorldRotation;
        public List<ViewpointAnchor> anchors = new List<ViewpointAnchor>();
    }
    [Serializable] private sealed class ViewpointAnchor
    {
        public string bone, transform, path;
        public Vector3 worldPosition;
    }

    // A separate projection keeps older hierarchy captures explicitly viewpoint-unverified.
    // SDK settings are read via SerializedObject; no SDK implementation is copied or bundled.
    public static string CaptureViewpoint(Transform root, string guid)
    {
        var snapshot = new ViewpointSnapshot { prefabGuid = guid };
        var ids = new Dictionary<UnityEngine.Object, string>();
        var ordered = new List<Transform>();
        Index(root, "r", ids, ordered);
        foreach (Transform transform in ordered)
        foreach (Component component in transform.GetComponents<Component>())
        {
            if (component.GetType().FullName != "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor") continue;
            var serialized = new SerializedObject(component);
            Vector3 view = Required(serialized, "ViewPosition", SerializedPropertyType.Vector3).vector3Value;
            var record = new DescriptorRecord { transform = ids[transform], viewPosition = view,
                descriptorWorldPosition = transform.position, viewWorldPosition = transform.position + view,
                descriptorWorldRotation = transform.rotation, descriptorWorldScale = transform.lossyScale };
            Identify(record, component, root, ids);
            var animator = transform.GetComponent<Animator>();
            if (animator != null && animator.avatar != null && animator.avatar.isHuman)
                foreach (HumanBodyBones bone in new[] { HumanBodyBones.Hips, HumanBodyBones.Head,
                             HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm })
                {
                    Transform target = animator.GetBoneTransform(bone);
                    if (target == null) continue;
                    record.anchors.Add(new ViewpointAnchor { bone = bone.ToString(), transform = Ref(target, ids),
                        path = AnimationUtility.CalculateTransformPath(target, root), worldPosition = target.position });
                }
            snapshot.descriptors.Add(record);
        }
        return JsonUtility.ToJson(snapshot, true) + "\n";
    }

    [Serializable] private sealed class Snapshot
    {
        public string format = "resopon.unity-reference";
        public int schemaVersion = 2;
        public string projection = "unity-resolved-prefab";
        public string prefabGuid;
        public Sections sections = new Sections();
        public List<SkinSize> skinSizes = new List<SkinSize>();
        public List<LipSyncRecord> lipSync = new List<LipSyncRecord>();
    }
    [Serializable] private sealed class Sections
    {
        public List<TransformRecord> transforms = new List<TransformRecord>();
        public List<RendererRecord> renderers = new List<RendererRecord>();
        public List<PhysicsRecord> physBones = new List<PhysicsRecord>();
        public List<PhysicsRecord> colliders = new List<PhysicsRecord>();
    }
    [Serializable] private sealed class AssetRef { public string guid, fileId; }
    [Serializable] private class Identity
    {
        public string id, path, type;
        public AssetRef source, originalSource;
    }
    [Serializable] private sealed class TransformRecord : Identity
    {
        public string parent, name;
        public int siblingIndex;
        public bool activeSelf, activeInHierarchy;
        public Vector3 localPosition, localScale;
        public Quaternion localRotation;
    }
    [Serializable] private sealed class RendererRecord : Identity
    {
        public string transform, rootBone;
        public bool activeSelf, activeInHierarchy;
        public AssetRef mesh;
        public List<AssetRef> materials = new List<AssetRef>();
        public List<string> bones = new List<string>();
        public List<Weight> blendShapes = new List<Weight>();
    }
    [Serializable] private sealed class SkinSize
    {
        public string rendererId, reason;
        public Vector3 sizeMeters;
        public int weightedVertices;
    }

    // Both projections measure the base mesh in its authored skeletal pose, before morphs.
    // Renderer bounds can be authored/cached, and include neither the saved binding nor its error.
    private static SkinSize MeasureSkin(SkinnedMeshRenderer skin, string rendererId)
    {
        var result = new SkinSize { rendererId = rendererId };
        try
        {
            var mesh = skin.sharedMesh;
            if (mesh == null) throw new InvalidOperationException("missing mesh");
            var bones = skin.bones;
            var poses = mesh.bindposes;
            var frames = new Matrix4x4[poses.Length];
            for (int i = 0; i < poses.Length && i < bones.Length; i++)
                if (bones[i] != null) frames[i] = bones[i].localToWorldMatrix * poses[i];
            var points = mesh.vertices;
            var counts = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            if (counts.Length != points.Length) throw new InvalidOperationException("missing vertex weights");
            var bounds = new Bounds();
            int index = 0;
            for (int v = 0; v < points.Length; v++)
            {
                Vector3 point = Vector3.zero;
                float sum = 0;
                for (int j = 0; j < counts[v]; j++)
                {
                    var binding = weights[index++];
                    if (binding.weight == 0) continue;
                    int i = binding.boneIndex;
                    if (i < 0 || i >= frames.Length || i >= bones.Length || bones[i] == null)
                        throw new InvalidOperationException("missing weighted bone");
                    point += frames[i].MultiplyPoint3x4(points[v]) * binding.weight;
                    sum += binding.weight;
                }
                if (sum == 0) continue;
                if (float.IsNaN(point.sqrMagnitude) || float.IsInfinity(point.sqrMagnitude))
                    throw new InvalidOperationException("nonfinite vertex");
                if (result.weightedVertices++ == 0) bounds = new Bounds(point, Vector3.zero);
                else bounds.Encapsulate(point);
            }
            if (result.weightedVertices == 0) throw new InvalidOperationException("no weighted vertices");
            result.sizeMeters = bounds.size;
        }
        catch (Exception e) { result.reason = e.Message; }
        return result;
    }

    [Serializable] private sealed class Weight { public int index; public string name; public float weight; }
    [Serializable] private sealed class LipSyncRecord
    {
        public string descriptor, renderer, reason, fxController, sampledSwitch;
        public bool sampledSwitchValue;
        public int mode;
        public List<string> declaredShapes = new List<string>();
        public List<LipShape> bindings = new List<LipShape>();
    }
    [Serializable] private sealed class LipShape { public int phoneme; public string renderer, shape; }

    private static LipSyncRecord ReadLipSync(Component descriptor, Dictionary<UnityEngine.Object, string> ids)
    {
        var data = new SerializedObject(descriptor);
        var skin = Required(data, "VisemeSkinnedMesh", SerializedPropertyType.ObjectReference).objectReferenceValue as SkinnedMeshRenderer;
        var record = new LipSyncRecord { descriptor = ids[descriptor], renderer = Ref(skin, ids),
            mode = data.FindProperty("lipSync").intValue };
        var names = data.FindProperty("VisemeBlendShapes");
        for (int i = 0; i < names.arraySize; i++) record.declaredShapes.Add(names.GetArrayElementAtIndex(i).stringValue);
        if (record.mode == 3)
        {
            for (int i = 0; i < Math.Min(15, record.declaredShapes.Count); i++)
            {
                if (string.IsNullOrEmpty(record.declaredShapes[i])) continue;
                if (skin != null && skin.sharedMesh != null && skin.sharedMesh.GetBlendShapeIndex(record.declaredShapes[i]) >= 0)
                    record.bindings.Add(new LipShape { phoneme = i, renderer = ids[skin], shape = record.declaredShapes[i] });
                else record.reason = "Configured viseme renderer, mesh or shape unavailable";
            }
        }
        else if (record.mode == 4)
        {
            var layers = data.FindProperty("baseAnimationLayers");
            RuntimeAnimatorController fx = null;
            for (int i = 0; i < layers.arraySize; i++)
            {
                var layer = layers.GetArrayElementAtIndex(i);
                if (layer.FindPropertyRelative("type").intValue == 5 && !layer.FindPropertyRelative("isDefault").boolValue)
                    fx = layer.FindPropertyRelative("animatorController").objectReferenceValue as RuntimeAnimatorController;
            }
            if (fx == null) record.reason = "No custom FX controller to sample";
            else
            {
                record.fxController = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(fx));
                SampleVisemeParameter(descriptor.transform, ids, fx, record, null, false);
                // A boolean Any State transition can park the entire viseme layer in an
                // off state. Probe its other setting separately and record that condition.
                if (record.bindings.Count == 0 && fx is AnimatorController controller)
                {
                    var switches = new HashSet<string>();
                    foreach (var layer in controller.layers)
                    {
                        var machine = layer.stateMachine;
                        if (!System.Array.Exists(machine.entryTransitions, t =>
                            System.Array.Exists(t.conditions, c => c.parameter == "Viseme"))) continue;
                        foreach (var transition in machine.anyStateTransitions)
                        foreach (var c in transition.conditions)
                            if (c.mode == AnimatorConditionMode.If || c.mode == AnimatorConditionMode.IfNot) switches.Add(c.parameter);
                    }
                    if (switches.Count == 1)
                        foreach (var p in controller.parameters)
                            if (switches.Contains(p.name) && p.type == AnimatorControllerParameterType.Bool)
                            {
                                record.sampledSwitch = p.name;
                                record.sampledSwitchValue = !p.defaultBool;
                                SampleVisemeParameter(descriptor.transform, ids, fx, record, p.name, !p.defaultBool);
                            }
                }
            }
        }
        else record.reason = "Lip sync mode outside blendshape comparison";
        return record;
    }

    // Evaluate the selected FX controller on a mesh-only hierarchy. No avatar behaviours run
    // and no curves are evaluated on the source prefab. Keep the other FX parameters at startup defaults.
    private static void SampleVisemeParameter(Transform root, Dictionary<UnityEngine.Object, string> ids,
        RuntimeAnimatorController fx, LipSyncRecord record, string switchName, bool switchValue)
    {
        var preview = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
        try
        {
            var proxy = new GameObject("Viseme sample");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(proxy, preview);
            var animator = proxy.AddComponent<Animator>();
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var transforms = new Dictionary<Transform, Transform> { [root] = proxy.transform };
            Transform CopyPath(Transform original)
            {
                if (transforms.TryGetValue(original, out var target)) return target;
                var parent = CopyPath(original.parent);
                target = new GameObject(original.name).transform;
                target.SetParent(parent, false);
                transforms.Add(original, target);
                return target;
            }
            var samples = new List<(SkinnedMeshRenderer original, SkinnedMeshRenderer copy, float[,] weights)>();
            foreach (var original in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (original.sharedMesh == null || original.sharedMesh.blendShapeCount == 0) continue;
                var copy = CopyPath(original.transform).gameObject.AddComponent<SkinnedMeshRenderer>();
                copy.sharedMesh = original.sharedMesh;
                samples.Add((original, copy, new float[15, copy.sharedMesh.blendShapeCount]));
            }
            for (int phoneme = 0; phoneme < 15; phoneme++)
            {
                foreach (var s in samples)
                    for (int j = 0; j < s.weights.GetLength(1); j++) s.copy.SetBlendShapeWeight(j, s.original.GetBlendShapeWeight(j));
                var graph = PlayableGraph.Create("Viseme FX");
                try
                {
                    graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                    var controller = AnimatorControllerPlayable.Create(graph, fx);
                    AnimationPlayableOutput.Create(graph, "FX", animator).SetSourcePlayable(controller);
                    graph.Play();
                    graph.Evaluate(0);
                    bool viseme = false;
                    for (int k = 0; k < controller.GetParameterCount(); k++)
                    {
                        var p = controller.GetParameter(k);
                        if (p.name == "Viseme" && p.type == AnimatorControllerParameterType.Int)
                        { controller.SetInteger(p.nameHash, phoneme); viseme = true; }
                        if (p.name == "Voice" && p.type == AnimatorControllerParameterType.Float) controller.SetFloat(p.nameHash, 1);
                    }
                    if (!viseme) { record.reason = "FX has no integer Viseme parameter"; return; }
                    if (switchName != null) controller.SetBool(switchName, switchValue);
                    for (int step = 0; step < 60; step++) graph.Evaluate(1f / 60f);
                    foreach (var s in samples)
                        for (int j = 0; j < s.weights.GetLength(1); j++) s.weights[phoneme, j] = s.copy.GetBlendShapeWeight(j);
                }
                finally { graph.Destroy(); }
            }
            foreach (var s in samples)
                for (int j = 0; j < s.weights.GetLength(1); j++)
                {
                    int active = -1;
                    for (int i = 0; i < 15; i++)
                    {
                        if (Mathf.Abs(s.weights[i, j]) < 0.01f) continue;
                        if (active >= 0 || Mathf.Abs(s.weights[i, j] - 100) >= 0.01f) { active = -1; break; }
                        active = i;
                    }
                    if (active >= 0) record.bindings.Add(new LipShape { phoneme = active, renderer = ids[s.original],
                        shape = s.copy.sharedMesh.GetBlendShapeName(j) });
                }
            record.reason = record.bindings.Count == 0 ? "No independent full-weight shape observed at full Voice" : "";
        }
        finally { UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(preview); }
    }
    [Serializable] private sealed class Number { public string name; public float value; }
    [Serializable] private sealed class PhysicsRecord : Identity
    {
        public string transform, root;
        public bool enabled, activeInHierarchy;
        public List<string> colliders = new List<string>();
        public List<Number> parameters = new List<Number>();
    }

    public static string Capture(Transform root, string guid)
    {
        if (root == null) throw new ArgumentNullException("root");
        var snapshot = new Snapshot { prefabGuid = guid };
        var ids = new Dictionary<UnityEngine.Object, string>();
        var ordered = new List<Transform>();
        Index(root, "r", ids, ordered);
        foreach (Transform transform in ordered)
        {
            var record = new TransformRecord
            {
                parent = transform == root ? "" : Ref(transform.parent, ids), name = transform.name,
                siblingIndex = transform == root ? 0 : transform.GetSiblingIndex(),
                activeSelf = transform.gameObject.activeSelf, activeInHierarchy = transform.gameObject.activeInHierarchy,
                localPosition = transform.localPosition, localRotation = transform.localRotation, localScale = transform.localScale,
            };
            Identify(record, transform, root, ids);
            snapshot.sections.transforms.Add(record);
            foreach (Component component in transform.GetComponents<Component>())
            {
                if (component == null) throw new InvalidOperationException("Missing script at " + record.path);
                var renderer = component as Renderer;
                if (renderer is MeshRenderer || renderer is SkinnedMeshRenderer)
                {
                    var skin = renderer as SkinnedMeshRenderer;
                    var filter = renderer.GetComponent<MeshFilter>();
                    Mesh mesh = skin != null ? skin.sharedMesh : filter == null ? null : filter.sharedMesh;
                    var r = new RendererRecord
                    {
                        transform = ids[transform], rootBone = skin == null ? "" : Ref(skin.rootBone, ids),
                        activeSelf = record.activeSelf, activeInHierarchy = record.activeInHierarchy,
                        mesh = Asset(mesh),
                    };
                    Identify(r, component, root, ids);
                    foreach (var material in renderer.sharedMaterials) r.materials.Add(Asset(material));
                    if (skin != null)
                    {
                        snapshot.skinSizes.Add(MeasureSkin(skin, r.id));
                        foreach (var bone in skin.bones) r.bones.Add(Ref(bone, ids));
                        if (mesh != null)
                            for (int i = 0; i < mesh.blendShapeCount; ++i)
                                r.blendShapes.Add(new Weight { index = i, name = mesh.GetBlendShapeName(i), weight = skin.GetBlendShapeWeight(i) });
                    }
                    snapshot.sections.renderers.Add(r);
                }
                string type = component.GetType().FullName;
                if (type == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor") snapshot.lipSync.Add(ReadLipSync(component, ids));
                if (type != BoneType && type != ColliderType) continue;
                var serialized = new SerializedObject(component);
                var rootProperty = Required(serialized, "rootTransform", SerializedPropertyType.ObjectReference);
                var rootTarget = rootProperty.objectReferenceValue as Transform;
                if (rootTarget == null && rootProperty.objectReferenceInstanceIDValue != 0)
                    throw new InvalidOperationException("Unresolved physics root at " + record.path);
                var physics = new PhysicsRecord
                {
                    transform = ids[transform], root = Ref(rootTarget == null ? transform : rootTarget, ids),
                    enabled = ((Behaviour)component).enabled, activeInHierarchy = record.activeInHierarchy,
                };
                Identify(physics, component, root, ids);
                if (type == BoneType)
                {
                    var array = serialized.FindProperty("colliders");
                    if (array == null || !array.isArray) throw new InvalidOperationException("Missing colliders array.");
                    for (int i = 0; i < array.arraySize; ++i)
                    {
                        var item = array.GetArrayElementAtIndex(i);
                        if (item.propertyType != SerializedPropertyType.ObjectReference)
                            throw new InvalidOperationException("Invalid collider reference.");
                        var value = item.objectReferenceValue;
                        if (value == null && item.objectReferenceInstanceIDValue != 0)
                            throw new InvalidOperationException("Unresolved collider at " + record.path);
                        if (value != null && value.GetType().FullName != ColliderType)
                            throw new InvalidOperationException("Unexpected collider type.");
                        physics.colliders.Add(Ref(value, ids));
                    }
                    foreach (string name in new[] { "pull", "spring", "stiffness", "gravity", "gravityFalloff", "immobile", "radius" })
                        physics.parameters.Add(new Number { name = name, value = Required(serialized, name, SerializedPropertyType.Float).floatValue });
                    snapshot.sections.physBones.Add(physics);
                }
                else snapshot.sections.colliders.Add(physics);
            }
        }
        if (snapshot.sections.renderers.Count + snapshot.sections.physBones.Count + snapshot.sections.colliders.Count == 0)
            throw new InvalidOperationException("No supported resolved components; empty capture rejected.");
        return JsonUtility.ToJson(snapshot, true) + "\n";
    }

    // A hierarchy address plus per-type component ordinal distinguishes repeated instances,
    // duplicate names and duplicate components. Never persist Unity's transient instance IDs.
    // Sibling insertion/reordering deliberately changes addresses and is a regression difference.
    private static void Index(Transform transform, string id, Dictionary<UnityEngine.Object, string> ids, List<Transform> ordered)
    {
        ids.Add(transform, id);
        ordered.Add(transform);
        var ordinals = new Dictionary<string, int>();
        foreach (Component component in transform.GetComponents<Component>())
        {
            if (component == null) throw new InvalidOperationException("Missing script at " + id);
            if (component is Transform) continue;
            string type = component.GetType().FullName;
            int index;
            ordinals.TryGetValue(type, out index);
            ordinals[type] = index + 1;
            ids.Add(component, id + "/c:" + type + ":" + index.ToString(CultureInfo.InvariantCulture));
        }
        for (int i = 0; i < transform.childCount; ++i)
            Index(transform.GetChild(i), id + "/" + i.ToString(CultureInfo.InvariantCulture), ids, ordered);
    }

    private static void Identify(Identity record, Component component, Transform root, Dictionary<UnityEngine.Object, string> ids)
    {
        record.id = ids[component];
        record.type = component.GetType().FullName;
        record.path = AnimationUtility.CalculateTransformPath(component.transform, root);
        record.source = Asset(PrefabUtility.GetCorrespondingObjectFromSource(component));
        record.originalSource = Asset(PrefabUtility.GetCorrespondingObjectFromOriginalSource(component));
    }

    private static string Ref(UnityEngine.Object target, Dictionary<UnityEngine.Object, string> ids)
    {
        if (target == null) return "";
        string id;
        if (!ids.TryGetValue(target, out id)) throw new InvalidOperationException("Reference outside captured hierarchy: " + target.name);
        return id;
    }

    private static AssetRef Asset(UnityEngine.Object asset)
    {
        if (asset == null) return new AssetRef { guid = "", fileId = "0" };
        string guid;
        long fileId;
        if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out guid, out fileId) || string.IsNullOrEmpty(guid))
            throw new InvalidOperationException("Non-persistent asset: " + asset.name);
        return new AssetRef { guid = guid, fileId = fileId.ToString(CultureInfo.InvariantCulture) };
    }

    private static SerializedProperty Required(SerializedObject source, string name, SerializedPropertyType type)
    {
        var property = source.FindProperty(name);
        if (property == null || property.propertyType != type) throw new InvalidOperationException("Required SDK field unavailable: " + name);
        return property;
    }
}
#endif
