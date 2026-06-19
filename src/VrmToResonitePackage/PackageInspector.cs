using Elements.Assets;
using Elements.Core;

namespace VrmToResonitePackage;

/// <summary>
/// Diagnostic utility: decodes a .resonitepackage and prints the saved slot/component
/// structure without launching the engine. Used to verify converter output.
/// </summary>
internal static class PackageInspector
{
    public static int Inspect(string packagePath, bool verbose)
    {
        using RecordPackage package = RecordPackage.Decode(packagePath);
        SkyFrost.Base.Record record = package.MainRecord;
        if (record == null)
        {
            Console.Error.WriteLine("R-Main.record が見つかりません。");
            return 1;
        }
        Console.WriteLine($"名前: {record.Name}  所有者: {record.OwnerId}");
        Console.WriteLine($"アセット数: {package.AssetCount}");

        string signature = RecordPackage.GetAssetSignature(new Uri(record.AssetURI));
        using Stream stream = package.ReadAsset(signature);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        memory.Position = 0;
        DataTreeDictionary root = DataTreeConverter.LoadAuto(memory);
        if (root == null)
        {
            Console.Error.WriteLine("メインアセットをDataTreeとしてデコードできませんでした。");
            return 1;
        }

        // Type table: list of type names referenced by index from components.
        var typeNames = new List<string>();
        if (root.TryGetNode("Types") is DataTreeList typesList)
        {
            foreach (DataTreeNode node in typesList.Children)
            {
                typeNames.Add((node as DataTreeValue)?.Extract<string>() ?? "?");
            }
        }

        var componentCounts = new Dictionary<string, int>();
        int slotCount = 0;

        void WalkSlot(DataTreeDictionary slot, int depth)
        {
            slotCount++;
            string name = ExtractField<string>(slot.TryGetNode("Name")) ?? "(unnamed)";
            if (verbose || depth <= 2)
            {
                string transform = verbose
                    ? $" active={ExtractField<bool>(slot.TryGetNode("Active"))}" +
                      $" pos={FormatVec(ExtractFloats(slot.TryGetNode("Position")))}" +
                      $" rot={FormatVec(ExtractFloats(slot.TryGetNode("Rotation")))}" +
                      $" scale={FormatVec(ExtractFloats(slot.TryGetNode("Scale")))}"
                    : "";
                Console.WriteLine($"{new string(' ', depth * 2)}- {name}{transform}");
            }
            if (slot.TryGetNode("Components") is DataTreeDictionary componentsDict &&
                componentsDict.TryGetNode("Data") is DataTreeList componentList)
            {
                foreach (DataTreeNode component in componentList.Children)
                {
                    if (component is not DataTreeDictionary componentDict)
                    {
                        continue;
                    }
                    string typeName = ResolveType(componentDict.TryGetNode("Type"), typeNames);
                    componentCounts[typeName] = componentCounts.GetValueOrDefault(typeName) + 1;
                }
            }
            if (slot.TryGetNode("Children") is DataTreeList children)
            {
                foreach (DataTreeNode child in children.Children)
                {
                    if (child is DataTreeDictionary childDict)
                    {
                        WalkSlot(childDict, depth + 1);
                    }
                }
            }
        }

        if (root.TryGetNode("Object") is DataTreeDictionary objectRoot)
        {
            Console.WriteLine();
            Console.WriteLine("スロットツリー (深さ2まで):");
            WalkSlot(objectRoot, 0);
        }

        // Collected asset providers (materials, textures, meshes) are stored separately.
        var assetCounts = new Dictionary<string, int>();
        if (root.TryGetNode("Assets") is DataTreeList assets)
        {
            foreach (DataTreeNode asset in assets.Children)
            {
                if (asset is DataTreeDictionary assetDict)
                {
                    string typeName = ResolveType(assetDict.TryGetNode("Type"), typeNames);
                    assetCounts[typeName] = assetCounts.GetValueOrDefault(typeName) + 1;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"スロット数: {slotCount}");
        Console.WriteLine("コンポーネント数:");
        foreach ((string type, int count) in componentCounts.OrderByDescending(kv => kv.Value))
        {
            Console.WriteLine($"  {count,5} x {Shorten(type)}");
        }
        Console.WriteLine();
        Console.WriteLine("収集アセット (Assetsノード):");
        foreach ((string type, int count) in assetCounts.OrderByDescending(kv => kv.Value))
        {
            Console.WriteLine($"  {count,5} x {Shorten(type)}");
        }

        if (root.TryGetNode("Object") is DataTreeDictionary objectRoot2)
        {
            ReportDynamicBones(objectRoot2, typeNames);
        }
        return 0;
    }

    /// <summary>
    /// Focused report of the modular_avatar DynamicBone setup: lists each chain slot
    /// (tagged with the controller tag), its Template Name value and binding field slots,
    /// plus the shared template container. Always printed so it survives without --inspect-verbose.
    /// </summary>
    private static void ReportDynamicBones(DataTreeDictionary objectRoot, List<string> typeNames)
    {
        var chains = new List<(string slotName, string templateName, List<string> bindings, int colliders)>();
        var templates = new List<string>();
        var colliderSlots = new List<(string parent, float[] pos)>();
        var transformNotes = new List<string>();
        bool sawSpace = false;
        bool sawSettingsRoot = false;

        void Walk(DataTreeDictionary slot, string parentName)
        {
            string name = ExtractField<string>(slot.TryGetNode("Name")) ?? "(unnamed)";
            string tag = ExtractField<string>(slot.TryGetNode("Tag"));
            var componentTypes = ComponentTypeNames(slot, typeNames);

            if (name == "VRM Collider")
            {
                colliderSlots.Add((parentName, ExtractFloats(slot.TryGetNode("Position"))));
            }
            if (name is "CenteredRoot" or "Root" || name.EndsWith("_L_Hand") || name == "LeftHand")
            {
                float[] rot = ExtractFloats(slot.TryGetNode("Rotation"));
                float[] pos = ExtractFloats(slot.TryGetNode("Position"));
                transformNotes.Add($"{parentName}/{name}: pos={FormatVec(pos)} rot={FormatVec(rot)}");
            }

            if (componentTypes.Any(t => t.Contains("DynamicVariableSpace")))
            {
                sawSpace = true;
            }
            if (componentTypes.Any(t => t.Contains("DynamicReferenceVariable")))
            {
                sawSettingsRoot = true;
            }

            // A chain slot is tagged and carries a DynamicBoneChain.
            if (tag == "modular_avatar/dynamic_bone_controller")
            {
                string templateName = null;
                var bindings = new List<string>();
                int colliders = 0;
                if (slot.TryGetNode("Children") is DataTreeList chainChildren)
                {
                    foreach (DataTreeNode child in chainChildren.Children)
                    {
                        if (child is not DataTreeDictionary cd)
                        {
                            continue;
                        }
                        string childName = ExtractField<string>(cd.TryGetNode("Name"));
                        if (childName == "Template Name")
                        {
                            templateName = FindValueFieldString(cd, typeNames);
                        }
                        else if (childName == "Bindings" && cd.TryGetNode("Children") is DataTreeList bindList)
                        {
                            foreach (DataTreeNode b in bindList.Children)
                            {
                                if (b is DataTreeDictionary bd)
                                {
                                    bindings.Add(ExtractField<string>(bd.TryGetNode("Name")) ?? "?");
                                }
                            }
                        }
                    }
                }
                colliders = CountInSubtree(slot, typeNames, "DynamicBoneSphereCollider");
                chains.Add((name, templateName, bindings, colliders));
            }

            // Template slots live directly under "Dynamic Bone Settings".
            if (name == "Dynamic Bone Settings" && slot.TryGetNode("Children") is DataTreeList templateChildren)
            {
                foreach (DataTreeNode t in templateChildren.Children)
                {
                    if (t is DataTreeDictionary td)
                    {
                        templates.Add(ExtractField<string>(td.TryGetNode("Name")) ?? "?");
                    }
                }
            }

            if (slot.TryGetNode("Children") is DataTreeList children)
            {
                foreach (DataTreeNode child in children.Children)
                {
                    if (child is DataTreeDictionary cd)
                    {
                        Walk(cd, name);
                    }
                }
            }
        }

        Walk(objectRoot, "");

        if (!sawSpace && chains.Count == 0 && templates.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("modular_avatar 揺れもの構成:");
        Console.WriteLine($"  DynamicVariableSpace(modular_avatar): {(sawSpace ? "あり" : "なし")}");
        Console.WriteLine($"  AvatarSettingsRoot 公開: {(sawSettingsRoot ? "あり" : "なし")}");
        Console.WriteLine($"  テンプレート ({templates.Count}): {string.Join(", ", templates)}");
        Console.WriteLine($"  チェーン ({chains.Count}):");
        foreach ((string slotName, string templateName, List<string> bindings, int colliders) in chains)
        {
            Console.WriteLine($"    - {slotName}: template={templateName ?? "?"}, bindings={bindings.Count}, colliders={colliders}");
        }
        Console.WriteLine($"  コライダースロット ({colliderSlots.Count}):");
        foreach ((string parent, float[] pos) in colliderSlots)
        {
            Console.WriteLine($"    - {parent}: localPos={FormatVec(pos)}");
        }
        Console.WriteLine("  主要トランスフォーム:");
        foreach (string note in transformNotes)
        {
            Console.WriteLine($"    - {note}");
        }
    }

    private static float[] ExtractFloats(DataTreeNode node)
    {
        if (node is DataTreeDictionary dict)
        {
            node = dict.TryGetNode("Data");
        }
        if (node is DataTreeList list)
        {
            return list.Children.Select(c =>
            {
                try { return (c as DataTreeValue)?.Extract<float>() ?? 0f; }
                catch { return 0f; }
            }).ToArray();
        }
        return null;
    }

    private static string FormatVec(float[] v)
    {
        return v == null ? "?" : "(" + string.Join(", ", v.Select(f => f.ToString("+0.0000;-0.0000"))) + ")";
    }

    private static List<string> ComponentTypeNames(DataTreeDictionary slot, List<string> typeNames)
    {
        var result = new List<string>();
        if (slot.TryGetNode("Components") is DataTreeDictionary componentsDict &&
            componentsDict.TryGetNode("Data") is DataTreeList componentList)
        {
            foreach (DataTreeNode component in componentList.Children)
            {
                if (component is DataTreeDictionary cd)
                {
                    result.Add(ResolveType(cd.TryGetNode("Type"), typeNames));
                }
            }
        }
        return result;
    }

    private static string FindValueFieldString(DataTreeDictionary slot, List<string> typeNames)
    {
        if (slot.TryGetNode("Components") is DataTreeDictionary componentsDict &&
            componentsDict.TryGetNode("Data") is DataTreeList componentList)
        {
            foreach (DataTreeNode component in componentList.Children)
            {
                if (component is DataTreeDictionary cd &&
                    ResolveType(cd.TryGetNode("Type"), typeNames).Contains("ValueField") &&
                    cd.TryGetNode("Data") is DataTreeDictionary data)
                {
                    return ExtractField<string>(data.TryGetNode("Value"));
                }
            }
        }
        return null;
    }

    private static int CountInSubtree(DataTreeDictionary slot, List<string> typeNames, string typeFragment)
    {
        int count = ComponentTypeNames(slot, typeNames).Count(t => t.Contains(typeFragment));
        if (slot.TryGetNode("Children") is DataTreeList children)
        {
            foreach (DataTreeNode child in children.Children)
            {
                if (child is DataTreeDictionary cd)
                {
                    count += CountInSubtree(cd, typeNames, typeFragment);
                }
            }
        }
        return count;
    }

    private static string ResolveType(DataTreeNode typeNode, List<string> typeNames)
    {
        if (typeNode is DataTreeValue value)
        {
            object raw = value.Extract<object>();
            if (raw is int index && index >= 0 && index < typeNames.Count)
            {
                return typeNames[index];
            }
            if (raw is long longIndex && longIndex >= 0 && longIndex < typeNames.Count)
            {
                return typeNames[(int)longIndex];
            }
            if (raw is string s)
            {
                return s;
            }
        }
        return "?";
    }

    private static T ExtractField<T>(DataTreeNode node)
    {
        // Sync fields are saved as dictionaries with a "Data" entry.
        if (node is DataTreeDictionary dict)
        {
            node = dict.TryGetNode("Data");
        }
        if (node is DataTreeValue value)
        {
            try
            {
                return value.Extract<T>();
            }
            catch
            {
                return default;
            }
        }
        return default;
    }

    private static string Shorten(string typeName)
    {
        // Strip assembly-qualified noise, keep readable generic names.
        int bracket = typeName.IndexOf('[');
        string core = bracket > 0 ? typeName[..bracket] : typeName;
        return core.Length > 100 ? core[..100] : typeName.Length > 120 ? typeName[..120] : typeName;
    }
}
