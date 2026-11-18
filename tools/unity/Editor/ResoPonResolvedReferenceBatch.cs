#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

// Local-only collection. A captured result is a Unity oracle, not a ResoPon parity pass.
public static class ResoPonResolvedReferenceBatch
{
    [Serializable] private class Manifest { public Item[] cases; }
    [Serializable] private class Item { public string id, prefabPath, sceneRootPath; }
    [Serializable] private class Results
    {
        public string format = "resopon.unity-resolved-collection";
        public int schemaVersion = 1;
        public string unityVersion = Application.unityVersion;
        public string projection = "unity-resolved-prefab";
        public List<Result> cases = new List<Result>();
    }
    [Serializable] private class Result
    {
        public string id, prefabPath, sceneRootPath, prefabGuid, assetType, status, error;
        public string inputSha256, inputSha256After, dependencyHash, dependencyHashAfter;
        public string expectedPath, expectedSha256, repeatSha256;
        public bool repeatMatches, sourceUnchanged;
        public List<DependencyFile> dependencyFiles;
    }
    [Serializable] private class DependencyFile { public string path, sha256; }

    public static void Run()
    {
        try
        {
            string manifestPath = Environment.GetEnvironmentVariable("RESOPON_RESOLVED_MANIFEST");
            string output = Environment.GetEnvironmentVariable("RESOPON_RESOLVED_OUT");
            if (string.IsNullOrEmpty(manifestPath) || string.IsNullOrEmpty(output))
                throw new InvalidOperationException("Set RESOPON_RESOLVED_MANIFEST and RESOPON_RESOLVED_OUT.");
            var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath));
            if (manifest.cases == null || manifest.cases.Length == 0) throw new InvalidOperationException("No capture cases.");
            if (Directory.Exists(output)) throw new IOException("Collection output must be a new directory.");
            var ids = new HashSet<string>();
            foreach (var item in manifest.cases)
                if (string.IsNullOrEmpty(item.id) || !System.Text.RegularExpressions.Regex.IsMatch(item.id, "^[a-z0-9][a-z0-9_-]*$") || !ids.Add(item.id))
                    throw new InvalidOperationException("Invalid or duplicate case ID.");
            Directory.CreateDirectory(output);
            var results = new Results();
            bool failed = false;
            foreach (var item in manifest.cases)
            {
                var result = new Result { id = item.id, prefabPath = item.prefabPath, sceneRootPath = item.sceneRootPath, status = "failed" };
                results.cases.Add(result);
                string folder = Path.Combine(output, item.id);
                Directory.CreateDirectory(folder);
                try
                {
                    result.prefabGuid = AssetDatabase.AssetPathToGUID(item.prefabPath);
                    result.assetType = item.prefabPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ? "Scene"
                        : PrefabUtility.GetPrefabAssetType(AssetDatabase.LoadAssetAtPath<GameObject>(item.prefabPath)).ToString();
                    result.inputSha256 = Hash(item.prefabPath);
                    result.dependencyHash = AssetDatabase.GetAssetDependencyHash(item.prefabPath).ToString();
                    result.dependencyFiles = DependencyFiles(item.prefabPath);
                    result.expectedPath = item.id + "/expected.json";
                    string expected = Path.Combine(output, result.expectedPath);
                    string repeat = Path.Combine(folder, "repeat.json");
                    ResoPonReferenceExporter.Export(item.prefabPath, expected, "resolved", item.sceneRootPath);
                    ResoPonReferenceExporter.Export(item.prefabPath, repeat, "resolved", item.sceneRootPath);
                    result.expectedSha256 = Hash(expected);
                    result.repeatSha256 = Hash(repeat);
                    result.inputSha256After = Hash(item.prefabPath);
                    result.dependencyHashAfter = AssetDatabase.GetAssetDependencyHash(item.prefabPath).ToString();
                    result.repeatMatches = result.expectedSha256 == result.repeatSha256;
                    result.sourceUnchanged = result.inputSha256 == result.inputSha256After && result.dependencyHash == result.dependencyHashAfter &&
                        result.dependencyFiles.All(file => FileDigest(file.path) == file.sha256);
                    if (!result.repeatMatches || !result.sourceUnchanged)
                        throw new InvalidOperationException("Capture or source changed across instantiations; baseline not accepted.");
                    result.status = "captured";
                }
                catch (Exception error) { failed = true; result.error = error.ToString(); }
                File.WriteAllText(Path.Combine(folder, "result.json"), JsonUtility.ToJson(result, true));
                Debug.Log("ResoPon resolved " + item.id + ": " + result.status);
            }
            File.WriteAllText(Path.Combine(output, "index.json"), JsonUtility.ToJson(results, true));
            EditorApplication.Exit(failed ? 1 : 0);
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }

    private static List<DependencyFile> DependencyFiles(string prefab)
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var asset in AssetDatabase.GetDependencies(prefab, true))
        {
            // These virtual assets are supplied by the selected Editor.
            if (asset == "Resources/unity_builtin_extra" || asset == "Library/unity default resources" ||
                asset == "Library/unity editor resources") continue;
            string physical = asset;
            if (asset.StartsWith("Packages/", StringComparison.Ordinal))
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(asset);
                if (package == null) throw new IOException("Cannot locate package dependency: " + asset);
                physical = Path.Combine(package.resolvedPath, asset.Substring(package.assetPath.Length).TrimStart('/'));
            }
            physical = Path.GetFullPath(physical);
            if (File.Exists(physical)) files.Add(physical);
            else if (!Directory.Exists(physical)) throw new FileNotFoundException("Missing Unity dependency", physical);
            files.Add(physical + ".meta");
        }
        return files.Select(path => new DependencyFile { path = path, sha256 = FileDigest(path) }).ToList();
    }

    // An absent meta is also an input state (for example in a package).
    private static string FileDigest(string path) => File.Exists(path) ? Hash(path) : "";

    private static string Hash(string path)
    {
        using (var hash = SHA256.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
}
#endif
