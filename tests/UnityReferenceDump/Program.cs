using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using VrmToResonitePackage;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

if (args.Length == 2 && args[0] == "--check-native-metadata")
{
    try { NativeMetadataChecks.Run(Path.GetFullPath(args[1])); return 0; }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
}

if (args.Length == 2 && args[0] == "--check-shared-mesh")
{
    try
    {
        ResoniteLocator.InstallAssemblyResolver(ResoniteLocator.Locate(null));
        SharedMeshChecks.Run(Path.GetFullPath(args[1]));
        return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
}

if (args.Length == 2 && args[0] == "--write-fbx-fixture")
{
    try
    {
        ResoniteLocator.InstallAssemblyResolver(ResoniteLocator.Locate(null));
        FbxReferenceFixture.Write(Path.GetFullPath(args[1]));
        return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
}

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: UnityReferenceDump <input.prefab|input.unitypackage> <unity-expected.json> <resopon-actual.json>");
    return 2;
}
try
{
    string input = Path.GetFullPath(args[0]);
    string expected = Path.GetFullPath(args[1]);
    string output = Path.GetFullPath(args[2]);
    if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(expected, output, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Write actual JSON to a separate, new file.");
    using var reference = JsonDocument.Parse(File.ReadAllBytes(expected));
    JsonElement root = reference.RootElement;
    int version = root.GetProperty("schemaVersion").GetInt32();
    if (root.GetProperty("format").GetString() != ReferenceSnapshot.Format ||
        !((version == 1 && root.GetProperty("projection").GetString() == ReferenceSnapshot.Projection) ||
          (version == 2 && root.GetProperty("projection").GetString() == ResolvedSnapshot.Projection)))
        throw new InvalidDataException("Unsupported Unity reference format, version or projection.");
    string guid = root.GetProperty("prefabGuid").GetString();
    if (guid?.Length != 32 || !guid.All(Uri.IsHexDigit))
        throw new InvalidDataException("Missing or invalid prefab GUID in the Unity reference.");
    ResoniteLocator.InstallAssemblyResolver(ResoniteLocator.Locate(null));
    Run(input, guid.ToLowerInvariant(), output, version);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Unity reference dump failed: {error.Message}");
    return 1;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static void Run(string input, string guid, string output, int version)
{
    using var source = UnityPackage.Open(input);
    if (source.InputPrefab != null && !string.Equals(source.InputPrefab.Guid, guid, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Selected prefab does not match the Unity reference GUID.");
    UnityAsset selected = source.ByGuid(guid);
    if (selected?.HasContent != true || selected.Extension != ".prefab")
        throw new InvalidDataException($"Expected prefab {guid} is not present in the input.");
    using var view = UnityPrefabInstances.CreateView(source, guid, null);
    var snapshot = version == 2 ? ResolvedSnapshot.Capture(view, guid) : ReferenceSnapshot.Capture(view, guid);
    // CreateNew protects captured expectations and previous test outputs, even when paths
    // alias one another via links or differ only in spelling on the current filesystem.
    byte[] json = JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    });
    using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
    file.Write(json);
    file.WriteByte((byte)'\n');
}

internal sealed record AssetReference(string Guid, string FileId);
internal sealed record RendererRecord(string Id, string Path, string Type, bool Enabled,
    bool ActiveSelf, bool ActiveInHierarchy, AssetReference Mesh, AssetReference[] Materials);
internal sealed record PhysBoneRecord(string Id, string Path, string Type, bool Enabled,
    bool ActiveInHierarchy, string Root, string[] Colliders);
internal sealed record ColliderRecord(string Id, string Path, string Type, bool Enabled,
    bool ActiveInHierarchy, string Root);

internal static class ReferenceSnapshot
{
    public const string Format = "resopon.unity-reference";
    public const string Projection = "selected-prefab-authored-components";

    public static object Capture(UnityPackage view, string guid)
    {
        UnityScene scene = view.PrefabGraph.Scene(guid)
            ?? throw new InvalidDataException("The selected prefab did not resolve to a scene.");
        var transforms = scene.Documents.Values.Where(d => !d.Stripped && (d.ClassId is 4 or 224))
            .ToDictionary(d => d.FileId);
        var gameObjects = scene.GameObjects.Where(d => !d.Stripped).ToDictionary(d => d.FileId);
        var renderers = new List<RendererRecord>();
        var bones = new List<PhysBoneRecord>();
        var colliders = new List<ColliderRecord>();
        // The exported projection is selected-prefab-authored components, not implicit FBX
        // renderers or nested prefab components. Read the same composed scenes as production.
        foreach (YamlDocument renderer in scene.MeshRenderers.Where(d => !d.Stripped))
        {
            var owner = Owner(renderer);
            var materials = renderer.Root?["m_Materials"];
            if (materials != null && !materials.IsSeq)
                throw new InvalidDataException($"Renderer {renderer.FileId}: invalid material list.");
            renderers.Add(new(Id(renderer.FileId), owner.Path,
                renderer.ClassId == 137 ? "UnityEngine.SkinnedMeshRenderer" : "UnityEngine.MeshRenderer",
                Enabled(renderer), owner.ActiveSelf, owner.ActiveInHierarchy,
                Asset(scene.RendererMesh(renderer)), (materials?.Items ?? Array.Empty<YamlNode>())
                    .Select(Asset).ToArray()));
        }
        foreach (YamlDocument component in scene.MonoBehaviours.Where(d => !d.Stripped))
        {
            var script = component.Root?["m_Script"];
            if (!string.Equals(script?.Guid, VrchatConstants.PhysBoneDllGuid, StringComparison.OrdinalIgnoreCase))
                continue;
            long type = script.FileID ?? 0;
            if (type != VrchatConstants.PhysBoneScriptFileId && type != -1631200402)
                continue;
            var owner = Owner(component);
            string root = Id(LocalRef(component.Root?["rootTransform"], component, transforms, owner.Transform));
            const string ns = "VRC.SDK3.Dynamics.PhysBone.Components.";
            if (type == VrchatConstants.PhysBoneScriptFileId)
            {
                var refs = component.Root?["colliders"];
                if (refs != null && !refs.IsSeq)
                    throw new InvalidDataException($"PhysBone {component.FileId}: invalid collider list.");
                bones.Add(new(Id(component.FileId), owner.Path, ns + "VRCPhysBone", Enabled(component),
                    owner.ActiveInHierarchy, root, (refs?.Items ?? Array.Empty<YamlNode>())
                        .Select(node => Id(LocalRef(node, component, scene.Documents, 0), allowNull: true))
                        .ToArray()));
            }
            else
                colliders.Add(new(Id(component.FileId), owner.Path, ns + "VRCPhysBoneCollider", Enabled(component),
                    owner.ActiveInHierarchy, root));
        }
        var colliderIds = colliders.Select(c => c.Id).ToHashSet();
        if (bones.Any(b => b.Colliders.Any(c => c.Length != 0 && !colliderIds.Contains(c))))
            throw new InvalidDataException("A PhysBone refers to a collider outside the comparison scope.");
        if (renderers.Count + bones.Count + colliders.Count == 0)
            throw new InvalidDataException("No selected-prefab-authored components were captured.");
        return new
        {
            format = Format,
            schemaVersion = 1,
            projection = Projection,
            prefabGuid = guid,
            sections = new
            {
                renderers = renderers.OrderBy(e => e.Id, StringComparer.Ordinal).ToArray(),
                physBones = bones.OrderBy(e => e.Id, StringComparer.Ordinal).ToArray(),
                colliders = colliders.OrderBy(e => e.Id, StringComparer.Ordinal).ToArray(),
            },
        };

        (string Path, long Transform, bool ActiveSelf, bool ActiveInHierarchy) Owner(YamlDocument component)
        {
            long go = LocalRef(component.Root?["m_GameObject"], component, gameObjects, 0);
            if (!gameObjects.TryGetValue(go, out var owner))
                throw new InvalidDataException($"Component {component.FileId}: missing owner.");
            var transform = scene.TransformOfGameObject(go);
            if (transform == null || !transforms.ContainsKey(transform.FileId))
                throw new InvalidDataException($"Component {component.FileId}: no selected-prefab-authored Transform.");
            bool self = IsActive(owner);
            bool active = self;
            var names = new Stack<string>();
            var seen = new HashSet<long>();
            for (YamlDocument current = transform; ;)
            {
                if (!seen.Add(current.FileId))
                    throw new InvalidDataException($"Component {component.FileId}: cyclic prefab hierarchy.");
                long currentGo = LocalRef(current.Root?["m_GameObject"], component, gameObjects, 0);
                active &= IsActive(gameObjects[currentGo]);
                long parent = LocalRef(current.Root?["m_Father"], component, transforms, 0);
                if (parent == 0) break;
                names.Push(gameObjects[currentGo].Root?["m_Name"]?.AsString()
                    ?? throw new InvalidDataException($"Missing GameObject name ({currentGo})."));
                current = transforms[parent];
            }
            return (string.Join("/", names), transform.FileId, self, active);
        }

        long LocalRef(YamlNode node, YamlDocument component,
            IReadOnlyDictionary<long, YamlDocument> accepted, long fallback)
        {
            long id = node == null ? 0 : node.FileID
                ?? throw new InvalidDataException($"Component {component.FileId}: invalid Unity object reference.");
            if (id == 0) return fallback;
            if ((node.Guid == null || string.Equals(node.Guid, guid, StringComparison.OrdinalIgnoreCase))
                && accepted.TryGetValue(id, out var target) && !target.Stripped)
                return id;
            throw new InvalidDataException($"Component {component.FileId}: reference {id} is outside the selected-prefab-authored scope.");
        }

        AssetReference Asset(YamlNode node)
        {
            if (node == null) return new("", "0");
            long id = node.FileID ?? throw new InvalidDataException("Invalid mesh or material reference.");
            if (id == 0) return new("", "0");
            string assetGuid = node.Guid;
            if (assetGuid == null && !scene.Documents.ContainsKey(id))
                throw new InvalidDataException($"Unresolved local mesh or material {id}.");
            assetGuid = assetGuid == null ? guid : view.ByGuid(assetGuid)?.SourceGuid ?? assetGuid;
            // Unity's built-in resources use synthetic GUIDs; keep the original reference
            // even when the .unitypackage does not contain a separate asset for it.
            return new(assetGuid.ToLowerInvariant(), Id(id));
        }
    }

    private static string Id(long id, bool allowNull = false) => id == 0
        ? allowNull ? "" : throw new InvalidDataException("Missing required object ID.")
        : id.ToString(CultureInfo.InvariantCulture);
    private static bool Enabled(YamlDocument document) => document.Root?["m_Enabled"]?.AsBool(true) ?? true;
    private static bool IsActive(YamlDocument document) => document.Root?["m_IsActive"]?.AsBool(true) ?? true;
}
