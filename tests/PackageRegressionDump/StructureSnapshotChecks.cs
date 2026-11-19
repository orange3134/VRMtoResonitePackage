using Elements.Core;
using System.Text.Json;

internal static class StructureSnapshotChecks
{
    public static void Run(string output)
    {
        const string rootId = "00000000-0000-0000-0000-000000000000";
        const string firstId = "00000001-0000-0000-0000-000000000000";
        const string lastId = "00000002-0000-0000-0000-000000000000";
        const string absentId = "ffffffff-ffff-ffff-ffff-ffffffffffff";
        var root = Slot(rootId, "Avatar", true);
        var first = Slot(firstId, "Twin", false);
        var last = Slot(lastId, "Twin", true);
        root.AddOrUpdate("Children", List(first, last));
        root.AddOrUpdate("Components", Field(List(Dict(("Type", Value(0)), ("Data", Dict(
            ("Enabled", Field(Value(true))), ("Bones", Field(Dict(("Hips", Field(Value(lastId))))))))))));
        first.AddOrUpdate("Components", Field(List(Dict(("Type", Value(1)), ("Data", Dict(
            ("Enabled", Field(Value(true))), ("BlendShapeWeights", Field(List(Value(0.25f)))),
            ("Bones", Field(List(Field(Value(lastId)), Field(Value(lastId)), Field(Value(rootId)),
                                 Field(Value(null)), Field(Value(absentId)))))))))));
        StructureSnapshot.Write(root, new[] { "[FrooxEngine]FrooxEngine.BipedRig",
            "[FrooxEngine]FrooxEngine.SkinnedMeshRenderer" }, new string('a', 64), output);
        using var json = JsonDocument.Parse(File.ReadAllBytes(output));
        var slots = json.RootElement.GetProperty("slots");
        Check(slots.GetArrayLength() == 3, "slot count");
        Check(slots[1].GetProperty("id").GetString() == "r/0" && slots[2].GetProperty("id").GetString() == "r/1", "duplicate names");
        Check(!slots[1].GetProperty("activeInHierarchy").GetBoolean(), "inactive slot");
        Check(slots[0].GetProperty("components")[0].GetProperty("rigBones").GetProperty("Hips")
            .GetProperty("target").GetString() == "r/1", "forward rig reference");
        var skin = slots[1].GetProperty("components")[0];
        var bones = skin.GetProperty("bones");
        Check(bones.GetArrayLength() == 5 && bones[0].GetProperty("target").GetString() == "r/1" &&
            bones[1].GetProperty("target").GetString() == "r/1", "ordered repeated forward references");
        Check(bones[2].GetProperty("target").GetString() == "r", "zero GUID is a valid root, not null");
        Check(bones[3].GetProperty("status").GetString() == "null" &&
            bones[4].GetProperty("status").GetString() == "unresolved", "null versus missing reference");
        Check(skin.GetProperty("slotReferences").GetArrayLength() == 3, "incoming slot references");
        byte[] original = File.ReadAllBytes(output);
        try
        {
            StructureSnapshot.Write(root, new[] { "[FrooxEngine]FrooxEngine.BipedRig",
                "[FrooxEngine]FrooxEngine.SkinnedMeshRenderer" }, new string('a', 64), output);
            throw new Exception("Existing snapshot was overwritten.");
        }
        catch (IOException) { Check(original.SequenceEqual(File.ReadAllBytes(output)), "output preservation"); }
        Console.WriteLine("Structure snapshot checks passed.");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static DataTreeValue Value(IConvertible value) => new(value);
    private static DataTreeDictionary Field(DataTreeNode data) => Dict(("Data", data));
    private static DataTreeDictionary Dict(params (string Key, DataTreeNode Value)[] fields)
    {
        var result = new DataTreeDictionary();
        foreach (var field in fields) result.AddOrUpdate(field.Key, field.Value);
        return result;
    }
    private static DataTreeList List(params DataTreeNode[] nodes)
    {
        var list = new DataTreeList();
        foreach (var node in nodes) list.Children.Add(node);
        return list;
    }
    private static DataTreeDictionary Slot(string id, string name, bool active) => Dict(
        ("ID", Value(id)), ("Name", Field(Value(name))), ("Tag", Field(Value(null))), ("Active", Field(Value(active))),
        ("Position", Field(List(Value(0f), Value(0f), Value(0f)))),
        ("Rotation", Field(List(Value(0f), Value(0f), Value(0f), Value(1f)))),
        ("Scale", Field(List(Value(1f), Value(1f), Value(1f)))), ("Children", List()), ("Components", Field(List())));
}
