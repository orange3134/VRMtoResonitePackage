#if UNITY_EDITOR
// Self-authored source fixture; Unity is the independent model-import oracle.
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class ResoPonSkinRootSmoke
{
    public static void Run()
    {
        try
        {
            string source = Environment.GetEnvironmentVariable("RESOPON_SKIN_ROOT_SOURCE");
            string output = Environment.GetEnvironmentVariable("RESOPON_REFERENCE_SMOKE_OUT");
            if (!File.Exists(source) || string.IsNullOrEmpty(output) || Directory.Exists(output))
                throw new InvalidOperationException("Set fixture source and new output directory.");
            Directory.CreateDirectory(output);
            string folder = "Assets/ResoPonSkinRootSmoke_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            string modelPath = folder + "/roots.fbx";
            File.Copy(source, modelPath);
            AssetDatabase.ImportAsset(modelPath, ImportAssetOptions.ForceSynchronousImport);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            var renderers = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length != 3) throw new InvalidOperationException("Expected three skinned renderers.");
            foreach (var renderer in renderers)
            {
                string expected = renderer.name == "Forward" ? "B" : renderer.name == "Reverse" ? "A" : "Skeleton";
                Debug.Log(renderer.name + " root=" + renderer.rootBone.name + " bones=" +
                    string.Join(",", renderer.bones.Select(b => b.name)));
                if (renderer.rootBone.name != expected)
                    throw new InvalidOperationException("Unexpected imported root bone: " + renderer.name);
            }
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            string prefabPath = folder + "/roots.prefab";
            PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
            UnityEngine.Object.DestroyImmediate(instance);
            ResoPonReferenceExporter.Export(prefabPath, Path.Combine(output, "expected.json"));
            File.WriteAllText(Path.Combine(output, "input.txt"), Path.GetFullPath(prefabPath));
            Debug.Log("ResoPon skin root smoke PASS");
            EditorApplication.Exit(0);
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }
}
#endif
