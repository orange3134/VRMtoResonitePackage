namespace VrmToResonitePackage.Unity;

/// <summary>
/// A parsed Unity prefab/scene: documents indexed by fileID with helpers to walk the
/// GameObject/Transform hierarchy and resolve component references to GameObject names
/// (the names the model importer gives the corresponding slots after FBX import).
/// </summary>
public sealed class UnityScene
{
    private const int ClassGameObject = 1;
    private const int ClassTransform = 4;
    private const int ClassMonoBehaviour = 114;
    private const int ClassSkinnedMeshRenderer = 137;

    private readonly Dictionary<long, YamlDocument> _byFileId = new();

    /// <summary>GameObject fileId -> its Transform document.</summary>
    private readonly Dictionary<long, YamlDocument> _transformOfGameObject = new();

    public IReadOnlyDictionary<long, YamlDocument> Documents => _byFileId;

    public static UnityScene Parse(string text)
    {
        var scene = new UnityScene();
        foreach (YamlDocument doc in UnityYaml.ParseDocuments(text))
        {
            scene._byFileId[doc.FileId] = doc;
        }
        foreach (YamlDocument doc in scene._byFileId.Values)
        {
            if (doc.ClassId == ClassTransform || IsRectTransform(doc))
            {
                long go = doc.Root?["m_GameObject"]?.FileID ?? 0;
                if (go != 0)
                {
                    scene._transformOfGameObject[go] = doc;
                }
            }
        }
        return scene;
    }

    private static bool IsRectTransform(YamlDocument doc) => doc.TypeName == "RectTransform";

    public YamlDocument Doc(long fileId) => fileId != 0 && _byFileId.TryGetValue(fileId, out YamlDocument d) ? d : null;

    public IEnumerable<YamlDocument> GameObjects => _byFileId.Values.Where(d => d.ClassId == ClassGameObject);

    public IEnumerable<YamlDocument> SkinnedMeshRenderers =>
        _byFileId.Values.Where(d => d.ClassId == ClassSkinnedMeshRenderer);

    public int GameObjectCount => GameObjects.Count();

    public string GameObjectName(long gameObjectFileId) => Doc(gameObjectFileId)?.Root?["m_Name"]?.AsString();

    /// <summary>
    /// Resolves any reference (to a GameObject, a Transform, or any component) to the name of the
    /// owning GameObject. After FBX import the matching Resonite slot carries that name.
    /// </summary>
    public string ResolveGameObjectName(long fileId)
    {
        YamlDocument doc = Doc(fileId);
        if (doc == null)
        {
            return null;
        }
        if (doc.ClassId == ClassGameObject)
        {
            return doc.Root?["m_Name"]?.AsString();
        }
        long go = doc.Root?["m_GameObject"]?.FileID ?? 0;
        return go != 0 ? GameObjectName(go) : null;
    }

    public YamlDocument TransformOfGameObject(long gameObjectFileId)
        => _transformOfGameObject.TryGetValue(gameObjectFileId, out YamlDocument t) ? t : null;

    /// <summary>The root GameObjects (their Transform has no parent).</summary>
    public IEnumerable<YamlDocument> RootGameObjects()
    {
        foreach ((long gameObjectFileId, YamlDocument transform) in _transformOfGameObject)
        {
            long father = transform.Root?["m_Father"]?.FileID ?? 0;
            if (father == 0)
            {
                YamlDocument go = Doc(gameObjectFileId);
                if (go != null)
                {
                    yield return go;
                }
            }
        }
    }

    /// <summary>Enumerates the component documents referenced by a GameObject's m_Component list.</summary>
    public IEnumerable<YamlDocument> ComponentsOf(YamlDocument gameObject)
    {
        YamlNode components = gameObject?.Root?["m_Component"];
        if (components?.Seq == null)
        {
            yield break;
        }
        foreach (YamlNode entry in components.Seq)
        {
            long fileId = entry?["component"]?.FileID ?? 0;
            YamlDocument doc = Doc(fileId);
            if (doc != null)
            {
                yield return doc;
            }
        }
    }

    /// <summary>Finds the first component of the GameObject that is a MonoBehaviour with the given script guid.</summary>
    public YamlDocument FindMonoBehaviour(YamlDocument gameObject, string scriptGuid)
    {
        foreach (YamlDocument doc in ComponentsOf(gameObject))
        {
            if (doc.ClassId == ClassMonoBehaviour && doc.Root?["m_Script"]?.Guid == scriptGuid)
            {
                return doc;
            }
        }
        return null;
    }

    /// <summary>All MonoBehaviour documents in the scene whose m_Script matches the given guid (and optional fileID).</summary>
    public IEnumerable<YamlDocument> MonoBehavioursByScript(string scriptGuid, long? scriptFileId = null)
    {
        foreach (YamlDocument doc in _byFileId.Values)
        {
            if (doc.ClassId != ClassMonoBehaviour)
            {
                continue;
            }
            YamlNode script = doc.Root?["m_Script"];
            if (script?.Guid != scriptGuid)
            {
                continue;
            }
            if (scriptFileId.HasValue && (script.FileID ?? 0) != scriptFileId.Value)
            {
                continue;
            }
            yield return doc;
        }
    }

    /// <summary>The GameObject that owns a component document, or null.</summary>
    public YamlDocument OwnerGameObject(YamlDocument component)
    {
        long go = component?.Root?["m_GameObject"]?.FileID ?? 0;
        return Doc(go);
    }

    /// <summary>Local position/rotation/scale of a GameObject's Transform, plus its parent GameObject name.</summary>
    public readonly struct LocalTransform
    {
        public System.Numerics.Vector3 Position { get; init; }
        public System.Numerics.Quaternion Rotation { get; init; }
        public System.Numerics.Vector3 Scale { get; init; }
        public string ParentName { get; init; }
        public bool Found { get; init; }
    }

    public LocalTransform GetLocalTransform(long gameObjectFileId)
    {
        YamlDocument t = TransformOfGameObject(gameObjectFileId);
        if (t == null)
        {
            return default;
        }
        YamlNode pos = t.Root?["m_LocalPosition"];
        YamlNode rot = t.Root?["m_LocalRotation"];
        YamlNode scale = t.Root?["m_LocalScale"];
        long father = t.Root?["m_Father"]?.FileID ?? 0;
        return new LocalTransform
        {
            Position = pos != null ? new System.Numerics.Vector3(pos.Vec("x"), pos.Vec("y"), pos.Vec("z")) : default,
            Rotation = rot != null
                ? new System.Numerics.Quaternion(rot.Vec("x"), rot.Vec("y"), rot.Vec("z"), rot["w"] != null ? rot.Vec("w") : 1f)
                : System.Numerics.Quaternion.Identity,
            Scale = scale != null ? new System.Numerics.Vector3(scale.Vec("x", 1f), scale.Vec("y", 1f), scale.Vec("z", 1f)) : System.Numerics.Vector3.One,
            ParentName = ResolveGameObjectName(father),
            Found = true,
        };
    }
}
