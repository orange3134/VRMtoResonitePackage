#if UNITY_EDITOR
// Independently authored Unity reference capture for ResoPon regression comparisons.
// Concept reference only (no copied implementation): halby24/vrctom at
// 983995e2372c30b07828b88644b971912ffb68a1/tools/unity/{Renderer,Physics}SidecarExporter.cs.
// Copy into Assets/Editor of a trusted Unity project with the avatar dependencies installed.
// No runtime converter dependency. Edit-mode initial state, not Animator/VRChat simulation.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class ResoPonReferenceExporter
{
    private const string BoneType = "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone";
    private const string ColliderType = "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider";

    [Serializable] private sealed class Capture
    {
        public string format = "resopon.unity-reference";
        public int schemaVersion = 1;
        public string prefabGuid;
        public string projection = "selected-prefab-authored-components";
        public Sections sections = new Sections();
    }
    [Serializable] private sealed class Sections
    {
        public List<RendererRecord> renderers = new List<RendererRecord>();
        public List<BoneRecord> physBones = new List<BoneRecord>();
        public List<ColliderRecord> colliders = new List<ColliderRecord>();
    }
    [Serializable] private sealed class AssetReference
    {
        public string guid;
        // Strings retain the full signed 64-bit Unity local ID in every JSON consumer.
        public string fileId;
    }
    [Serializable] private sealed class RendererRecord
    {
        public string id, path, type;
        public bool activeSelf, activeInHierarchy;
        public AssetReference mesh;
        public List<AssetReference> materials = new List<AssetReference>();
    }
    [Serializable] private sealed class BoneRecord
    {
        public string id, path, type, root;
        public bool enabled, activeInHierarchy;
        public List<string> colliders = new List<string>();
    }
    [Serializable] private sealed class ColliderRecord
    {
        public string id, path, type, root;
        public bool enabled, activeInHierarchy;
    }

    [MenuItem("Tools/ResoPon/Export Unity Reference")]
    public static void ExportFromMenu()
    {
        try { ExportFromEnvironment(); }
        catch (Exception error) { Debug.LogException(error); }
    }

    // Unity -batchmode -projectPath ... -executeMethod ResoPonReferenceExporter.Run
    // Required environment variables: RESOPON_REFERENCE_PREFAB, RESOPON_REFERENCE_OUT.
    public static void Run()
    {
        if (!Application.isBatchMode)
            throw new InvalidOperationException("Run is batch-only; use the menu in interactive mode.");
        try { ExportFromEnvironment(); EditorApplication.Exit(0); }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }

    private static void ExportFromEnvironment()
    {
        Export(Environment.GetEnvironmentVariable("RESOPON_REFERENCE_PREFAB"),
            Environment.GetEnvironmentVariable("RESOPON_REFERENCE_OUT"),
            Environment.GetEnvironmentVariable("RESOPON_REFERENCE_PROJECTION"),
            Environment.GetEnvironmentVariable("RESOPON_REFERENCE_SCENE_ROOT"),
            Environment.GetEnvironmentVariable("RESOPON_REFERENCE_PREFAB_CHILD_ID"));
    }

    // Resolved is the collection default; authored retains the existing parser comparison schema.
    public static void Export(string prefabPath, string outputPath, string projection = "resolved", string sceneRootPath = null,
        string prefabChildId = null)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Reference capture requires Edit Mode.");
        if (string.IsNullOrEmpty(projection)) projection = "resolved";
        if (projection != "resolved" && projection != "authored" && projection != "viewpoint")
            throw new InvalidOperationException("Projection must be resolved, authored or viewpoint.");
        if (string.IsNullOrWhiteSpace(prefabPath) || string.IsNullOrWhiteSpace(outputPath))
            throw new InvalidOperationException("Set RESOPON_REFERENCE_PREFAB and RESOPON_REFERENCE_OUT.");
        prefabPath = prefabPath.Replace('\\', '/');
        bool sceneInput = prefabPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
        if (!prefabPath.StartsWith("Assets/", StringComparison.Ordinal) ||
            !(prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || sceneInput))
            throw new InvalidOperationException("Input must be an Assets/... .prefab or .unity asset path.");
        if (sceneInput && (projection == "authored" || string.IsNullOrEmpty(sceneRootPath)))
            throw new InvalidOperationException("Scene capture requires resolved/viewpoint projection and an exact scene root path.");
        if (sceneInput && !string.IsNullOrEmpty(prefabChildId))
            throw new InvalidOperationException("A prefab child ID cannot select a scene root.");
        if (sceneInput && (!Application.isBatchMode || SceneManager.GetSceneByPath(prefabPath).isLoaded))
            throw new InvalidOperationException("Scene capture requires a closed scene in a batch Editor.");
        var prefab = sceneInput ? null : AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (!sceneInput && prefab == null) throw new InvalidOperationException("Prefab not found: " + prefabPath);
        if (projection == "authored" && PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.Variant)
            throw new InvalidOperationException("Prefab variants are outside this initial authored-component projection.");
        string guid = AssetDatabase.AssetPathToGUID(prefabPath);
        if (string.IsNullOrEmpty(guid)) throw new InvalidOperationException("Prefab GUID unavailable.");

        // Prefabs use a preview scene; scene inputs open additively in batch mode only.
        // Neither path saves the source scene.
        // Instantiation can invoke ExecuteAlways callbacks: use a trusted project only.
        Scene preview = sceneInput ? EditorSceneManager.OpenScene(prefabPath, OpenSceneMode.Additive) : EditorSceneManager.NewPreviewScene();
        string json;
        try
        {
            var instance = sceneInput ? SelectSceneRoot(preview, sceneRootPath)
                : PrefabUtility.InstantiatePrefab(prefab, preview) as GameObject;
            if (instance == null) throw new InvalidOperationException("Prefab instantiation failed.");
            if (!string.IsNullOrEmpty(prefabChildId))
            {
                long fileId = long.Parse(prefabChildId, CultureInfo.InvariantCulture);
                instance = instance.GetComponentsInChildren<Transform>(true).Select(t => t.gameObject).Single(child =>
                {
                    var source = PrefabUtility.GetCorrespondingObjectFromSource(child);
                    return source != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string sourceGuid, out long id)
                        && sourceGuid == guid && id == fileId;
                });
            }
            json = projection == "viewpoint" ? ResoPonResolvedReference.CaptureViewpoint(instance.transform, guid) : projection == "resolved"
                ? ResoPonResolvedReference.Capture(instance.transform, guid)
                : JsonUtility.ToJson(BuildCapture(instance.transform, guid), true) + "\n";
        }
        finally
        {
            if (sceneInput) EditorSceneManager.CloseScene(preview, true);
            else EditorSceneManager.ClosePreviewScene(preview);
        }

        // Require a new output path, never overwrite a project asset or an earlier capture.
        string fullOutput = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(fullOutput), ".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Output must use a .json extension.");
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput));
        using (var file = new FileStream(fullOutput, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(file, new UTF8Encoding(false))) writer.Write(json);
        Debug.Log("ResoPon Unity reference written: " + fullOutput);
    }

    private static GameObject SelectSceneRoot(Scene scene, string path)
    {
        var parts = path.Split('/');
        var roots = scene.GetRootGameObjects().Where(o => o.name == parts[0]).ToArray();
        if (roots.Length != 1) throw new InvalidOperationException("Scene root path is missing or ambiguous: " + path);
        Transform selected = roots[0].transform;
        foreach (string part in parts.Skip(1))
        {
            var children = selected.Cast<Transform>().Where(t => t.name == part).ToArray();
            if (children.Length != 1) throw new InvalidOperationException("Scene root path is missing or ambiguous: " + path);
            selected = children[0];
        }
        return selected.gameObject;
    }

    private static Capture BuildCapture(Transform root, string guid)
    {
        var result = new Capture { prefabGuid = guid };
        foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
        {
            var components = transform.GetComponents<Component>();
            foreach (Component component in components)
            {
                if (component == null)
                    throw new InvalidOperationException("Missing script at " + NamePath(root, transform));
                string type = component.GetType().FullName;
                if (type != BoneType && type != ColliderType && !(component is MeshRenderer) && !(component is SkinnedMeshRenderer)) continue;
                if (PersistentGuid(component) != guid) continue;
                RequireAuthoredAncestry(root, transform, guid);
                string id = LocalId(component, guid);
                string path = NamePath(root, transform);
                var renderer = component as Renderer;
                if (renderer != null)
                {
                    var meshFilter = renderer.GetComponent<MeshFilter>();
                    if (renderer is MeshRenderer && meshFilter != null) LocalId(meshFilter, guid);
                    var record = new RendererRecord
                    {
                        id = id, path = path, type = type,
                        activeSelf = transform.gameObject.activeSelf,
                        activeInHierarchy = transform.gameObject.activeInHierarchy,
                        mesh = AssetId(renderer is SkinnedMeshRenderer ?
                            ((SkinnedMeshRenderer)renderer).sharedMesh :
                            (renderer.GetComponent<MeshFilter>() == null ? null : renderer.GetComponent<MeshFilter>().sharedMesh))
                    };
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        record.materials.Add(AssetId(material));
                    }
                    result.sections.renderers.Add(record);
                }
                if (type != BoneType && type != ColliderType) continue;
                var behaviour = component as Behaviour;
                if (behaviour == null) throw new InvalidOperationException("Unexpected PhysBone component type.");
                var serialized = new SerializedObject(component);
                var rootProperty = RequiredProperty(serialized, "rootTransform", SerializedPropertyType.ObjectReference);
                var rootValue = rootProperty.objectReferenceValue as Transform;
                if (rootProperty.objectReferenceValue != null && rootValue == null)
                    throw new InvalidOperationException("rootTransform is not a Transform: " + id);
                Transform rootTarget = rootValue == null ? transform : rootValue;
                RequireAuthoredAncestry(root, rootTarget, guid);
                string rootId = LocalId(rootTarget, guid);
                if (type == ColliderType)
                {
                    result.sections.colliders.Add(new ColliderRecord
                    {
                        id = id, path = path, type = type, root = rootId, enabled = behaviour.enabled,
                        activeInHierarchy = transform.gameObject.activeInHierarchy
                    });
                    continue;
                }
                var bone = new BoneRecord
                {
                    id = id, path = path, type = type, root = rootId, enabled = behaviour.enabled,
                    activeInHierarchy = transform.gameObject.activeInHierarchy
                };
                var colliderArray = serialized.FindProperty("colliders");
                if (colliderArray == null || !colliderArray.isArray)
                    throw new InvalidOperationException("SDK colliders array unavailable: " + id);
                for (int i = 0; i < colliderArray.arraySize; ++i)
                {
                    var item = colliderArray.GetArrayElementAtIndex(i);
                    if (item.propertyType != SerializedPropertyType.ObjectReference)
                        throw new InvalidOperationException("SDK collider entry is not an object reference: " + id);
                    var reference = item.objectReferenceValue;
                    if (reference == null) { bone.colliders.Add(""); continue; }
                    var collider = reference as Component;
                    if (collider == null || collider.GetType().FullName != ColliderType)
                        throw new InvalidOperationException("Unexpected collider reference: " + id);
                    RequireAuthoredAncestry(root, collider.transform, guid);
                    bone.colliders.Add(LocalId(collider, guid));
                }
                result.sections.physBones.Add(bone);
            }
        }
        if (result.sections.renderers.Count + result.sections.physBones.Count + result.sections.colliders.Count == 0)
            throw new InvalidOperationException("No selected-prefab-authored supported components found; an empty reference is not valid.");
        result.sections.renderers.Sort((a, b) => StringComparer.Ordinal.Compare(a.id, b.id));
        result.sections.physBones.Sort((a, b) => StringComparer.Ordinal.Compare(a.id, b.id));
        result.sections.colliders.Sort((a, b) => StringComparer.Ordinal.Compare(a.id, b.id));
        return result;
    }

    private static SerializedProperty RequiredProperty(SerializedObject source, string name, SerializedPropertyType type)
    {
        var value = source.FindProperty(name);
        if (value == null || value.propertyType != type)
            throw new InvalidOperationException("Required SDK field unavailable: " + name);
        return value;
    }

    // Capture identities are the selected prefab's signed local fileIDs, encoded as strings.
    // Imported or nested-prefab components are excluded; references from an authored component
    // into those excluded hierarchies fail rather than produce a misleading partial comparison.
    private static string PersistentGuid(UnityEngine.Object instance)
    {
        var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(instance);
        string guid;
        long id;
        return source != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out guid, out id)
            ? guid : "";
    }

    private static string LocalId(UnityEngine.Object instance, string selectedGuid)
    {
        var source = PrefabUtility.GetCorrespondingObjectFromSource(instance);
        string guid;
        long id;
        if (PersistentGuid(instance) != selectedGuid || source == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out guid, out id) || guid != selectedGuid)
            throw new InvalidOperationException("Reference is not authored in the selected prefab: " + instance.name);
        return id.ToString(CultureInfo.InvariantCulture);
    }

    private static AssetReference AssetId(UnityEngine.Object asset)
    {
        if (asset == null) return new AssetReference { guid = "", fileId = "0" };
        string guid;
        long id;
        if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out guid, out id) || string.IsNullOrEmpty(guid))
            throw new InvalidOperationException("Asset has no persistent identity: " + asset.name);
        return new AssetReference { guid = guid, fileId = id.ToString(CultureInfo.InvariantCulture) };
    }

    private static void RequireAuthoredAncestry(Transform root, Transform value, string guid)
    {
        for (var cursor = value; ; cursor = cursor.parent)
        {
            if (cursor == null) throw new InvalidOperationException("Reference lies outside the avatar root.");
            LocalId(cursor, guid);
            if (cursor == root) return;
        }
    }

    private static string NamePath(Transform root, Transform value)
    {
        var segments = new List<string>();
        for (var cursor = value; cursor != root; cursor = cursor.parent)
        {
            if (cursor == null) throw new InvalidOperationException("Object lies outside the avatar root.");
            segments.Add(cursor.name);
        }
        segments.Reverse();
        return string.Join("/", segments.ToArray());
    }
}
#endif
