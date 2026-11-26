#if UNITY_EDITOR
// Synthetic Unity-authored fixtures; no third-party avatar data.
// Copy to Assets/Editor with the two exporter files, then invoke Run in batch mode.
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class ResoPonResolvedReferenceSmoke
{
    [Serializable] private class Snapshot { public int schemaVersion; public Sections sections; }
    [Serializable] private class Sections { public Record[] transforms, renderers, physBones, colliders; }
    [Serializable] private class Record
    {
        public string id, path, name, root;
        public bool activeInHierarchy;
        public Vector3 localScale;
        public Asset[] materials;
        public string[] colliders;
    }
    [Serializable] private class Asset { public string guid; }

    public static void Run()
    {
        try { Test(); Debug.Log("ResoPon resolved smoke PASS"); EditorApplication.Exit(0); }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }

    private static void Test()
    {
        string output = Environment.GetEnvironmentVariable("RESOPON_REFERENCE_SMOKE_OUT");
        if (string.IsNullOrEmpty(output)) throw new InvalidOperationException("Set RESOPON_REFERENCE_SMOKE_OUT.");
        Directory.CreateDirectory(output);
        string directory = "Assets/ResoPonReferenceSmoke_" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", directory.Substring("Assets/".Length));
        var mesh = new Mesh { name = "Synthetic triangle" };
        mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
        mesh.triangles = new[] { 0, 1, 2 };
        AssetDatabase.CreateAsset(mesh, directory + "/mesh.asset");
        var a = new Material(Shader.Find("Standard"));
        var b = new Material(Shader.Find("Standard"));
        AssetDatabase.CreateAsset(a, directory + "/a.mat");
        AssetDatabase.CreateAsset(b, directory + "/b.mat");
        Type boneType = FindType("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone");
        Type colliderType = FindType("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider");
        var source = new GameObject("Base");
        AddRenderer(source.transform, "Face", mesh, a, b);
        AddRenderer(source.transform, "RemoveRenderer", mesh, a);
        new GameObject("Removed").transform.SetParent(source.transform, false);
        var collision = new GameObject("Collision");
        collision.transform.SetParent(source.transform, false);
        Component collider = collision.AddComponent(colliderType);
        Component bone = source.AddComponent(boneType);
        SetCollider(bone, collider);
        string basePath = directory + "/base.prefab";
        var prefab = PrefabUtility.SaveAsPrefabAsset(source, basePath);
        UnityEngine.Object.DestroyImmediate(source);
        var variant = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        variant.transform.localScale = Vector3.one * 2;
        var face = variant.transform.Find("Face").GetComponent<MeshRenderer>();
        face.enabled = false;
        face.sharedMaterials = new[] { b, a };
        PrefabUtility.RecordPrefabInstancePropertyModifications(face);
        PrefabUtility.RecordPrefabInstancePropertyModifications(variant.transform);
        UnityEngine.Object.DestroyImmediate(variant.transform.Find("Removed").gameObject);
        UnityEngine.Object.DestroyImmediate(variant.transform.Find("RemoveRenderer").GetComponent<MeshRenderer>());
        AddRenderer(variant.transform, "Added", mesh, b);
        GameObject first = null;
        for (int i = 0; i < 2; ++i)
        {
            var nested = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            nested.name = "Twin";
            nested.transform.SetParent(variant.transform, false);
            if (i == 0) first = nested;
            if (i == 1) nested.SetActive(false);
        }
        SetCollider(variant.GetComponent(boneType), first.transform.Find("Collision").GetComponent(colliderType));
        string variantPath = directory + "/variant.prefab";
        var saved = PrefabUtility.SaveAsPrefabAsset(variant, variantPath);
        UnityEngine.Object.DestroyImmediate(variant);
        Check(PrefabUtility.GetPrefabAssetType(saved) == PrefabAssetType.Variant, "Fixture must be a Variant");
        byte[] before = File.ReadAllBytes(variantPath);
        string one = Path.Combine(output, "first.json"), two = Path.Combine(output, "second.json");
        ResoPonReferenceExporter.Export(variantPath, one, "resolved");
        ResoPonReferenceExporter.Export(variantPath, two, "resolved");
        Check(File.ReadAllText(one) == File.ReadAllText(two), "Repeated instantiations must produce identical snapshots");
        Check(before.SequenceEqual(File.ReadAllBytes(variantPath)), "Source prefab was modified");
        var snapshot = JsonUtility.FromJson<Snapshot>(File.ReadAllText(one));
        Check(snapshot.schemaVersion == 2, "Wrong schema");
        var transforms = snapshot.sections.transforms;
        Check(transforms.Single(r => r.id == "r").localScale == Vector3.one * 2, "Root scale override missing");
        Check(!transforms.Any(r => r.path == "Removed"), "Removed object present");
        Check(!snapshot.sections.renderers.Any(r => r.path == "RemoveRenderer"), "Removed component present");
        Check(snapshot.sections.renderers.Any(r => r.path == "Added"), "Added renderer missing");
        var twins = transforms.Where(r => r.path == "Twin").ToArray();
        Check(twins.Length == 2 && twins[0].id != twins[1].id, "Repeated instance identity collision");
        Check(twins.Count(r => !r.activeInHierarchy) == 1, "Inactive nested prefab missing");
        var capturedFace = snapshot.sections.renderers.Single(r => r.path == "Face");
        Check(capturedFace.materials[0].guid == AssetDatabase.AssetPathToGUID(directory + "/b.mat"), "Material order override missing");
        Check(snapshot.sections.physBones.Length == 3 && snapshot.sections.colliders.Length == 3, "Inherited/nested physics missing");
        var rootBone = snapshot.sections.physBones.Single(r => r.path == "");
        Check(rootBone.colliders[0].StartsWith(twins[0].id + "/", StringComparison.Ordinal), "Cross-instance collider override missing");
        bool rejected = false;
        try { ResoPonReferenceExporter.Export(variantPath, Path.Combine(output, "authored.json"), "authored"); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && !File.Exists(Path.Combine(output, "authored.json")), "Legacy projection must remain strict");
        rejected = false;
        try { ResoPonReferenceExporter.Export(variantPath, one, "resolved"); }
        catch (IOException) { rejected = true; }
        Check(rejected && File.ReadAllText(one) == File.ReadAllText(two), "Existing baseline overwritten");
    }

    private static void AddRenderer(Transform parent, string name, Mesh mesh, params Material[] materials)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterials = materials;
    }
    private static Type FindType(string name)
    {
        return AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null);
    }
    private static void SetCollider(Component bone, Component collider)
    {
        var serialized = new SerializedObject(bone);
        var refs = serialized.FindProperty("colliders");
        refs.arraySize = 1;
        refs.GetArrayElementAtIndex(0).objectReferenceValue = collider;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
#endif
