using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

public static partial class VrchatAvatarParser
{
    private static void ParseModelHierarchyState(UnityPackage package, VrchatAvatar avatar)
    {
        // Prefab Transform documents cover unpacked nodes, but imported model nodes
        // live in the FBX. Preserve the importer's source order policy before applying
        // explicit prefab child arrays. Use original paths even when objects are renamed.
        foreach (string guid in new[] { avatar.FbxGuid }.Concat(avatar.AdditionalFbxs.Select(f => f.Guid)).Distinct())
        {
            var asset = package.ByGuid(guid);
            if (asset?.Extension != ".fbx") continue;
            var resolver = package.ModelFileIds(guid);
            var hierarchy = resolver.Hierarchy;
            if (hierarchy == null) continue;
            var importer = asset.MetaPath == null ? null :
                UnityYaml.ParseFlatDocument(File.ReadAllText(asset.MetaPath))?["ModelImporter"];
            bool sortNames = importer?["meshes"]?["sortHierarchyByName"]?.AsBool(true) ?? true;
            var unique = hierarchy.GroupBy(n => n.Path).Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.Single());
            VrchatBoneTarget Target(UnityModelNode node) => new(guid, node.Name, node.Path);
            var childrenByParent = UnityModelNode.ChildrenByParent(hierarchy.Where(n => unique.ContainsKey(n.Path)), sortNames);
            foreach (var (parentPath, children) in childrenByParent)
            {
                if (children.Length > 1 && unique.TryGetValue(parentPath, out var parent))
                    avatar.HierarchyOrders.Add(new(Target(parent), children.Select(Target).ToArray()));
            }
            foreach (var modification in package.PrefabGraph.Modifications.Where(m => m.Target.Occurrence == guid &&
                         m.Value["propertyPath"]?.AsString() == "m_IsActive"))
            {
                string path = resolver.ResolveNodePath(modification.Target.FileId);
                if (resolver.IsRootFileId(modification.Target.FileId))
                    avatar.HierarchyActiveStates[new(guid, resolver.ResolveName(modification.Target.FileId), "")] =
                        modification.Value["value"]?.AsBool(true) ?? true;
                else if (path != null && unique.TryGetValue(path, out var node))
                    avatar.HierarchyActiveStates[Target(node)] = modification.Value["value"]?.AsBool(true) ?? true;
            }
        }
    }
}
