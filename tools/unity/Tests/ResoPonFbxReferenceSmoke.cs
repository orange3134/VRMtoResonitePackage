#if UNITY_EDITOR
// Self-authored FBX and prefab fixtures. No third-party model data.
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class ResoPonFbxReferenceSmoke
{
    public static void Run()
    {
        try { Test(); Debug.Log("ResoPon FBX reference smoke PASS"); EditorApplication.Exit(0); }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }
    private static void Test()
    {
        string output = Environment.GetEnvironmentVariable("RESOPON_REFERENCE_SMOKE_OUT");
        if (string.IsNullOrEmpty(output) || Directory.Exists(output)) throw new InvalidOperationException("Set a new RESOPON_REFERENCE_SMOKE_OUT directory.");
        string source = Environment.GetEnvironmentVariable("RESOPON_FBX_SMOKE_SOURCE");
        if (!File.Exists(source)) throw new InvalidOperationException("Set RESOPON_FBX_SMOKE_SOURCE to the self-authored --write-fbx-fixture output.");
        Directory.CreateDirectory(output);
        string folder = "Assets/ResoPonFbxSmoke_" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", folder.Substring(7));
        string modelPath = folder + "/source.fbx";
        File.Copy(source, modelPath);
        AssetDatabase.ImportAsset(modelPath, ImportAssetOptions.ForceSynchronousImport);
        var importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
        if (importer == null) throw new InvalidOperationException("FBX import failed.");
        importer.globalScale = 1;
        importer.importAnimation = false;
        importer.SaveAndReimport();
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
        if (model == null || model.GetComponentsInChildren<MeshRenderer>(true).Length != 4)
            throw new InvalidOperationException("Expected four implicit FBX renderers: " +
                (model == null ? "null model" : string.Join(",", model.GetComponentsInChildren<Transform>(true).Select(t =>
                    t.name + ":" + string.Join("/", t.GetComponents<Component>().Select(c => c.GetType().Name))))));
        var material = new Material(Shader.Find("Standard"));
        AssetDatabase.CreateAsset(material, folder + "/material.mat");
        var root = new GameObject("Wrapper");
        GameObject first = null, second = null;
        for (int i = 0; i < 2; ++i)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            instance.transform.SetParent(root.transform, false);
            instance.name = "Twin";
            foreach (var renderer in instance.GetComponentsInChildren<MeshRenderer>(true))
            {
                renderer.sharedMaterials = new[] { material };
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            }
            if (i == 0) first = instance; else second = instance;
        }
        first.transform.localScale = Vector3.one * 1.25f;
        PrefabUtility.RecordPrefabInstancePropertyModifications(first.transform);
        second.SetActive(false);
        UnityEngine.Object.DestroyImmediate(first.GetComponentsInChildren<MeshRenderer>(true)[0]);
        UnityEngine.Object.DestroyImmediate(first.transform.Find("Hairpin").gameObject);
        Type boneType = FindType("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone");
        Type colliderType = FindType("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider");
        var collider = second.AddComponent(colliderType);
        var bone = root.AddComponent(boneType);
        var serialized = new SerializedObject(bone);
        serialized.FindProperty("rootTransform").objectReferenceValue = first.transform;
        var refs = serialized.FindProperty("colliders");
        refs.arraySize = 1;
        refs.GetArrayElementAtIndex(0).objectReferenceValue = collider;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        var added = new GameObject("Added");
        added.transform.SetParent(first.transform, false);
        string prefabPath = folder + "/wrapper.prefab";
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        UnityEngine.Object.DestroyImmediate(root);
        var variant = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        variant.transform.localPosition = new Vector3(.1f, .2f, .3f);
        PrefabUtility.RecordPrefabInstancePropertyModifications(variant.transform);
        string variantPath = folder + "/variant.prefab";
        PrefabUtility.SaveAsPrefabAsset(variant, variantPath);
        UnityEngine.Object.DestroyImmediate(variant);
        // Renamed prefab files can retain an old serialized root name. Unity's
        // importer exposes the file name as the asset root without rewriting YAML.
        string yaml = File.ReadAllText(variantPath);
        if (!yaml.Contains("value: variant\n")) throw new InvalidOperationException("Missing root name override in fixture.");
        yaml = yaml.Replace("value: variant\n", "value: StaleSerializedRoot\n");
        File.WriteAllText(variantPath, yaml);
        AssetDatabase.ImportAsset(variantPath, ImportAssetOptions.ForceSynchronousImport);
        if (AssetDatabase.LoadAssetAtPath<GameObject>(variantPath).name != "variant")
            throw new InvalidOperationException("Prefab root does not follow the asset file name.");
        byte[] before = File.ReadAllBytes(variantPath);
        ResoPonReferenceExporter.Export(variantPath, Path.Combine(output, "first.json"), "resolved");
        ResoPonReferenceExporter.Export(variantPath, Path.Combine(output, "second.json"), "resolved");
        if (File.ReadAllText(Path.Combine(output, "first.json")) != File.ReadAllText(Path.Combine(output, "second.json")) ||
            !before.SequenceEqual(File.ReadAllBytes(variantPath))) throw new InvalidOperationException("Unstable capture or changed prefab.");
        File.WriteAllText(Path.Combine(output, "prefab-path.txt"), Path.GetFullPath(variantPath));
    }
    private static Type FindType(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null);

}
#endif
