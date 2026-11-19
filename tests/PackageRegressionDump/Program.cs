using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Elements.Assets;
using Elements.Core;
using VrmToResonitePackage;

bool structure = args.Length == 3 && args[0] == "--structure";
bool checkStructure = args.Length == 2 && args[0] == "--check-structure";
if (args.Length != 2 && !structure)
{
    Console.Error.WriteLine("Usage: PackageRegressionDump [--structure] input.resonitepackage new-snapshot.json | --check-structure new-snapshot.json");
    return 2;
}
try
{
    ResoniteLocator.InstallAssemblyResolver(ResoniteLocator.Locate(null));
    if (checkStructure) { CheckStructure(Path.GetFullPath(args[1])); return 0; }
    Dump(Path.GetFullPath(args[structure ? 1 : 0]), Path.GetFullPath(args[structure ? 2 : 1]), structure);
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

[MethodImpl(MethodImplOptions.NoInlining)]
static void CheckStructure(string output) => StructureSnapshotChecks.Run(output);

[MethodImpl(MethodImplOptions.NoInlining)]
static void Dump(string input, string output, bool structure)
{
    // This is a developer tool: decode saved data without starting FrooxEngine or
    // loading user components. Never overwrite an avatar, reference or earlier run.
    if (File.Exists(output)) throw new IOException("Snapshot output already exists.");
    string hash;
    using (var file = File.OpenRead(input)) hash = Convert.ToHexStringLower(SHA256.HashData(file));
    using var package = RecordPackage.Decode(input);
    var record = package.MainRecord ?? throw new InvalidDataException("Missing main record.");
    using var asset = package.ReadAsset(RecordPackage.GetAssetSignature(new Uri(record.AssetURI)));
    using var memory = new MemoryStream(); asset.CopyTo(memory); memory.Position = 0;
    var root = DataTreeConverter.LoadAuto(memory) ?? throw new InvalidDataException("Invalid main asset.");
    var types = List(root.TryGetNode("Types")).Select(n => Value<string>(n)).ToArray();
    if (root.TryGetNode("Object") is not DataTreeDictionary objectRoot) throw new InvalidDataException("Missing object root.");
    if (structure)
    {
        StructureSnapshot.Write(objectRoot, types, hash, output, package, root.TryGetNode("Assets"));
        return;
    }
    var slots = new List<object>();
    Walk(objectRoot, "r", Array.Empty<string>(), true);
    var snapshot = new { format = "resopon.package-snapshot", schemaVersion = 1, packageSha256 = hash, slots };
    byte[] json = JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions { WriteIndented = true });
    using var destination = new FileStream(output, FileMode.CreateNew);
    destination.Write(json);
    Console.WriteLine($"Captured {slots.Count} slots; package SHA-256 {hash}");

    void Walk(DataTreeDictionary slot, string id, string[] parentPath, bool parentActive)
    {
        string name = Value<string>(slot.TryGetNode("Name"));
        string[] path = parentPath.Append(name).ToArray();
        bool active = Value<bool>(slot.TryGetNode("Active"));
        // An unset SyncString tag is serialized as null; both represent no tag.
        string tag = slot.TryGetNode("Tag") is {} tagNode ? Value<string>(tagNode) ?? "" : "";
        var components = new List<object>();
        if (slot.TryGetNode("Components") is DataTreeDictionary container)
        foreach (var node in List(container.TryGetNode("Data")))
        {
            if (node is not DataTreeDictionary component) throw new InvalidDataException("Invalid component.");
            object typeValue = Value<object>(component.TryGetNode("Type"));
            string type = typeValue switch
            {
                int i when i >= 0 && i < types.Length => types[i],
                long i when i >= 0 && i < types.Length => types[(int)i],
                string s => s,
                _ => throw new InvalidDataException("Invalid component type index.")
            };
            float[] weights = null;
            if (type == "[FrooxEngine]FrooxEngine.SkinnedMeshRenderer")
            {
                if (component.TryGetNode("Data") is not DataTreeDictionary data)
                    throw new InvalidDataException("Missing renderer data.");
                weights = List(data.TryGetNode("BlendShapeWeights")).Select(n => Value<float>(n)).ToArray();
                if (weights.Any(w => !float.IsFinite(w))) throw new InvalidDataException("Non-finite shape weight.");
            }
            components.Add(new { type, blendShapeWeights = weights });
        }
        slots.Add(new { id, path, tag, activeSelf = active, activeInHierarchy = parentActive && active, components });
        int index = 0;
        foreach (var child in List(slot.TryGetNode("Children")))
        {
            if (child is not DataTreeDictionary dictionary) throw new InvalidDataException("Invalid child slot.");
            Walk(dictionary, id + "/" + index++, path, parentActive && active);
        }
    }
}

static DataTreeNode Unwrap(DataTreeNode node) => node is DataTreeDictionary dictionary ? dictionary.TryGetNode("Data") : node;
static T Value<T>(DataTreeNode node) => Unwrap(node) is DataTreeValue value ? value.Extract<T>()
    : throw new InvalidDataException("Missing or malformed scalar field.");
static IEnumerable<DataTreeNode> List(DataTreeNode node) => Unwrap(node) switch
{
    null => Array.Empty<DataTreeNode>(),
    DataTreeList list => list.Children,
    _ => throw new InvalidDataException("Malformed array field.")
};
