using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Resolves fixed gesture inputs through Animator graphs; never discovers or adds Catalog clips.</summary>
internal sealed class FaceEmoGestureConditions
{
    internal sealed record Route(string Layer, int Pair, string Motion, string Name, string GripHand);
    private sealed class Layer
    {
        public UnityScene Scene;
        public string Id, Name;
        public long Root;
        public bool Invalid;
        public Dictionary<long, long> Parent = new();
        public HashSet<string> Reads = new(StringComparer.Ordinal), Writes = new(StringComparer.Ordinal);
        public HashSet<long> Behaviours = new();
        public YamlNode Node(long id) => Scene.Doc(id)?.Root;
        public IEnumerable<YamlNode> Transitions(IEnumerable<YamlNode> refs) =>
            VrchatAnimatorDefaults.ActiveTransitions(Scene, refs).Select(r => Node(r.FileID ?? 0));
    }
    private readonly List<Layer> _layers = new();
    private readonly Dictionary<string, int> _types = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unownedWrites = new(StringComparer.Ordinal);
    private readonly Func<YamlNode, bool> _safeMotion;
    private readonly Dictionary<string, List<Route>> _cache = new();
    internal List<string> Diagnostics { get; } = new();

    internal FaceEmoGestureConditions(IEnumerable<(UnityScene Scene, string Id)> controllers, Func<YamlNode, bool> safeMotion)
    {
        _safeMotion = safeMotion;
        foreach (var (scene, id) in controllers)
        {
            var controller = scene.Documents.Values.FirstOrDefault(d => d.ClassId == 91)?.Root;
            foreach (var p in controller?["m_AnimatorParameters"]?.Seq ?? new())
            {
                string name = p["m_Name"]?.AsString(); int type = p["m_Type"]?.AsInt() ?? 0;
                if (!string.IsNullOrEmpty(name)) _types[name] = _types.TryGetValue(name, out int previous) && previous != type ? 0 : type;
            }
            int index = 0;
            foreach (var setting in controller?["m_AnimatorLayers"]?.Seq ?? new())
            {
                var layer = new Layer { Scene = scene, Id = id + ":" + index++, Name = setting["m_Name"]?.AsString(),
                    Root = setting["m_StateMachine"]?.FileID ?? 0, Invalid = (setting["m_SyncedLayerIndex"]?.AsInt(-1) ?? -1) >= 0 };
                Index(layer.Root, 0); _layers.Add(layer);
                void Index(long nodeId, long parent)
                {
                    if (nodeId == 0 || !layer.Parent.TryAdd(nodeId, parent) || scene.Doc(nodeId) is not { } doc)
                    { layer.Invalid = true; return; }
                    var node = doc.Root;
                    foreach (var reference in node["m_StateMachineBehaviours"]?.Seq ?? new())
                    {
                        long bid = reference.FileID ?? 0; layer.Behaviours.Add(bid);
                        var b = layer.Node(bid);
                        if (!IsDriver(b)) continue;
                        foreach (var write in b["parameters"]?.Seq ?? new())
                        {
                            Add(layer.Writes, write["name"]?.AsString());
                            if (write["type"]?.AsInt() == 3) Add(layer.Reads, write["source"]?.AsString());
                            if (write["type"]?.AsInt() == 1) Add(layer.Reads, write["name"]?.AsString());
                        }
                    }
                    foreach (string key in new[] { "m_EntryTransitions", "m_AnyStateTransitions", "m_Transitions" }) ReadTransitions(node[key]?.Seq);
                    foreach (var group in node["m_StateMachineTransitions"]?.Seq ?? new()) ReadTransitions(group["second"]?.Seq);
                    ReadTree(node["m_Motion"], new());
                    foreach (var c in node["m_ChildStates"]?.Seq ?? new()) Index(c["m_State"]?.FileID ?? 0, nodeId);
                    foreach (var c in node["m_ChildStateMachines"]?.Seq ?? new()) Index(c["m_StateMachine"]?.FileID ?? 0, nodeId);
                }
                void ReadTransitions(IEnumerable<YamlNode> refs)
                {
                    if (refs?.Any(r => layer.Node(r.FileID ?? 0) == null) == true) layer.Invalid = true;
                    foreach (var t in layer.Transitions(refs))
                        foreach (var c in t["m_Conditions"]?.Seq ?? new()) Add(layer.Reads, c["m_ConditionEvent"]?.AsString());
                }
                void ReadTree(YamlNode motion, HashSet<long> seen)
                {
                    if (motion?.Guid != null || !seen.Add(motion?.FileID ?? 0) || scene.Doc(motion?.FileID ?? 0) is not { ClassId: 206 } tree) return;
                    Add(layer.Reads, tree.Root["m_BlendParameter"]?.AsString());
                    foreach (var child in tree.Root["m_Childs"]?.Seq ?? new()) ReadTree(child["m_Motion"], seen);
                }
            }
            var owned = _layers.Where(l => l.Scene == scene).SelectMany(l => l.Behaviours).ToHashSet();
            foreach (var doc in scene.Documents.Values.Where(d => IsDriver(d.Root) && !owned.Contains(d.FileId)))
                foreach (var w in doc.Root["parameters"]?.Seq ?? new()) Add(_unownedWrites, w["name"]?.AsString());
        }
    }

    internal IEnumerable<Route> ForLayer(string id)
    {
        if (_cache.TryGetValue(id, out var cached)) return cached;
        var result = _cache[id] = new();
        var target = _layers.FirstOrDefault(l => l.Id == id);
        if (target == null) return result;
        // Include only the parameter-producing dependencies of this candidate layer.
        var needed = new HashSet<Layer> { target };
        bool added;
        do
        {
            var reads = needed.SelectMany(l => l.Reads).ToHashSet(); added = false;
            foreach (var layer in _layers.Where(l => l.Writes.Overlaps(reads))) added |= needed.Add(layer);
        } while (added);
        if (!needed.Any(l => l.Reads.Any(n => n is "GestureLeft" or "GestureRight" or "GestureLeftWeight" or "GestureRightWeight"))) return result;
        var failures = new HashSet<string>();
        for (int pair = 0; pair < 64; pair++)
        {
            var values = new Dictionary<string, float>(StringComparer.Ordinal);
            var active = _layers.Where(needed.Contains).Select(l => new Playback(l, values, _types, _unownedWrites, _safeMotion)).ToArray();
            SetHands(0, 0);
            // Enter neutral first, then change both hands. Neutral input layers do not re-enter
            // and overwrite an active hand. Simultaneous writes follow serialized layer order.
            if (!Settle()) continue;
            SetHands(pair / 8, pair % 8);
            if (!Settle()) continue;
            var selected = active.Single(a => a.Layer == target);
            try
            {
                string motion = selected.Motion();
                result.Add(new(id, pair, motion, target.Node(selected.State)?["m_Name"]?.AsString(), selected.GripHand()));
            }
            catch (Unsupported ex) { failures.Add(ex.Message); }

            void SetHands(int l, int r)
            {
                values["GestureLeft"] = l; values["GestureRight"] = r;
                values["GestureLeftWeight"] = l == 1 ? 1 : 0; values["GestureRightWeight"] = r == 1 ? 1 : 0;
            }
            string Signature() => string.Join(";", active.Select(a => a.State + ":" + a.Error)) + "|" +
                string.Join(";", values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + BitConverter.SingleToInt32Bits(p.Value)));
            bool Settle()
            {
                var seen = new HashSet<string>();
                for (int pass = 0; pass < 256; pass++)
                {
                    string before = Signature();
                    if (!seen.Add(before)) { failures.Add("cyclic transitions or parameter writes"); return false; }
                    foreach (var playback in active) playback.Step();
                    if (Signature() != before) continue;
                    foreach (var playback in active.Where(a => a.Error != null)) failures.Add(playback.Error);
                    return active.All(a => a.Error == null);
                }
                failures.Add("transition evaluation limit"); return false;
            }
        }
        if (failures.Count > 0) Diagnostics.Add($"Gesture routing '{target.Name}': unresolved paths ({string.Join(", ", failures.Order())}); FaceEmo candidates retained.");
        return result;
    }

    private sealed class Unsupported(string message) : Exception(message);
    private sealed class Playback(Layer layer, Dictionary<string, float> values, Dictionary<string, int> types,
        HashSet<string> unowned, Func<YamlNode, bool> safeMotion)
    {
        public Layer Layer => layer;
        public long State;
        public string Error;
        private int _hops;
        public void Step()
        {
            Error = null; _hops = 0;
            try
            {
                if (layer.Invalid) throw new Unsupported("invalid or synchronized graph");
                if (State == 0) EnterMachine(layer.Root, new());
                foreach (long machine in Path(State))
                    if (Choose(layer.Node(machine)["m_AnyStateTransitions"]?.Seq, out var any, true)) { Follow(any, machine); return; }
                if (Choose(layer.Node(State)?["m_Transitions"]?.Seq, out var transition)) Follow(transition, layer.Parent[State]);
            }
            catch (Unsupported ex) { Error = ex.Message; }
        }
        private void Limit() { if (++_hops > 256) throw new Unsupported("cyclic Entry/Exit routing"); }
        private IEnumerable<long> Path(long state)
        {
            var path = new List<long>();
            for (long id = layer.Parent.GetValueOrDefault(state); id != 0; id = layer.Parent.GetValueOrDefault(id)) path.Add(id);
            path.Reverse(); return path;
        }
        private float Value(string name)
        {
            if (string.IsNullOrEmpty(name) || unowned.Contains(name) || !values.TryGetValue(name, out float value))
                throw new Unsupported("unknown parameter: " + name);
            return value;
        }
        private bool Choose(IEnumerable<YamlNode> refs, out YamlNode selected, bool any = false)
        {
            selected = null;
            foreach (var t in layer.Transitions(refs))
            {
                if (any && t["m_DstState"]?.FileID == State && t["m_CanTransitionToSelf"]?.AsBool() != true) continue;
                bool matches = true, unknown = false;
                foreach (var c in t["m_Conditions"]?.Seq ?? new())
                {
                    int mode = c["m_ConditionMode"]?.AsInt() ?? 0;
                    float threshold = c["m_EventTreshold"]?.AsFloat(float.NaN) ?? 0;
                    if (mode is not (1 or 2 or 3 or 4 or 6 or 7) || !float.IsFinite(threshold)) throw new Unsupported("invalid condition");
                    try { matches &= new ExpressionCondition("", mode, threshold).Matches(Value(c["m_ConditionEvent"]?.AsString())); }
                    catch (Unsupported) { unknown = true; }
                }
                if (!matches) continue; // a known false condition proves the conjunction false
                if (unknown) throw new Unsupported("unknown transition condition");
                if (t["m_HasExitTime"]?.AsBool() == true || (t["m_TransitionOffset"]?.AsFloat() ?? 0) != 0 ||
                    (t["m_InterruptionSource"]?.AsInt() ?? 0) != 0 || !float.IsFinite(t["m_TransitionDuration"]?.AsFloat() ?? 0))
                    throw new Unsupported("timed or interrupted transition");
                selected = t; return true;
            }
            return false;
        }
        private void EnterMachine(long id, HashSet<long> entered)
        {
            Limit();
            if (!layer.Parent.ContainsKey(id) || layer.Scene.Doc(id) is not { ClassId: 1107 } doc || entered.Contains(id)) throw new Unsupported("invalid Entry target");
            var oldPath = Path(State).ToHashSet();
            foreach (long ancestor in Path(id))
                if (!oldPath.Contains(ancestor) && entered.Add(ancestor)) Behaviours(layer.Node(ancestor));
            entered.Add(id); Behaviours(doc.Root);
            if (Choose(doc.Root["m_EntryTransitions"]?.Seq, out var entry))
            {
                long destination = (entry["m_DstState"]?.FileID ?? 0) != 0 ? entry["m_DstState"].FileID.Value : entry["m_DstStateMachine"]?.FileID ?? 0;
                if (layer.Parent.GetValueOrDefault(destination) != id) throw new Unsupported("Entry target is not owned by its machine");
                Follow(entry, id, entered);
            }
            else
            {
                long initial = doc.Root["m_DefaultState"]?.FileID ?? 0;
                if (layer.Parent.GetValueOrDefault(initial) != id) throw new Unsupported("invalid default-state ownership");
                EnterState(initial, entered);
            }
        }
        private void EnterState(long id, HashSet<long> entered)
        {
            Limit();
            if (!layer.Parent.ContainsKey(id) || layer.Scene.Doc(id) is not { ClassId: 1102 } doc) throw new Unsupported("invalid state target");
            var oldPath = Path(State).ToHashSet();
            foreach (long machine in Path(id)) if (!oldPath.Contains(machine) && entered.Add(machine)) Behaviours(layer.Node(machine));
            if (!Safe(doc.Root["m_Motion"], new())) throw new Unsupported("animation writes parameters, events or unavailable motion");
            Behaviours(doc.Root); State = id;
        }
        private bool Safe(YamlNode motion, HashSet<long> seen)
        {
            if ((motion?.FileID ?? 0) == 0) return true;
            if (motion.Guid != null) return safeMotion(motion);
            if (!seen.Add(motion.FileID.Value) || layer.Scene.Doc(motion.FileID.Value) is not { ClassId: 206 } tree) return false;
            return (tree.Root["m_Childs"]?.Seq ?? new()).All(c => Safe(c["m_Motion"], new(seen)));
        }
        private void Follow(YamlNode t, long owner, HashSet<long> entered = null)
        {
            Limit(); entered ??= new();
            long state = t["m_DstState"]?.FileID ?? 0, machine = t["m_DstStateMachine"]?.FileID ?? 0;
            if (t["m_IsExit"]?.AsBool() == true)
            {
                if (state != 0 || machine != 0) throw new Unsupported("invalid Exit target");
                Exit(owner, entered); return;
            }
            if (state != 0 && machine == 0) EnterState(state, entered);
            else if (machine != 0 && state == 0) EnterMachine(machine, entered);
            else throw new Unsupported("missing transition target");
        }
        private void Exit(long machine, HashSet<long> entered)
        {
            Limit();
            if (machine == layer.Root) { State = 0; EnterMachine(machine, entered); return; }
            long parent = layer.Parent.GetValueOrDefault(machine);
            var refs = (layer.Node(parent)?["m_StateMachineTransitions"]?.Seq ?? new())
                .Where(g => g["first"]?.FileID == machine).SelectMany(g => g["second"]?.Seq ?? new());
            if (!Choose(refs, out var transition)) throw new Unsupported("unresolved parent Exit");
            Follow(transition, parent, entered);
        }
        private void Behaviours(YamlNode node)
        {
            foreach (var r in node["m_StateMachineBehaviours"]?.Seq ?? new())
            {
                var b = layer.Node(r.FileID ?? 0);
                if (b?["m_Enabled"]?.AsBool() == false) continue;
                if (!IsDriver(b)) throw new Unsupported("unsupported state behaviour");
                foreach (var w in b["parameters"]?.Seq ?? new())
                {
                    string name = w["name"]?.AsString(); int operation = w["type"]?.AsInt(-1) ?? -1;
                    if (string.IsNullOrEmpty(name) || name is "GestureLeft" or "GestureRight" or "GestureLeftWeight" or "GestureRightWeight" ||
                        !types.TryGetValue(name, out int type) || type is not (1 or 3 or 4) || unowned.Contains(name)) throw new Unsupported("unknown parameter writer");
                    float number = operation switch
                    {
                        0 => w["value"]?.AsFloat(float.NaN) ?? float.NaN,
                        1 => Value(name) + (w["value"]?.AsFloat(float.NaN) ?? float.NaN),
                        3 when w["convertRange"]?.AsBool() != true => Value(w["source"]?.AsString()),
                        _ => throw new Unsupported("random or unsupported parameter operation")
                    };
                    if (!float.IsFinite(number) || type == 3 && (number != MathF.Truncate(number) || number < int.MinValue || number >= 2147483648f))
                        throw new Unsupported("invalid parameter value");
                    values[name] = type == 4 ? (number == 0 ? 0 : 1) : number;
                }
            }
        }
        public string GripHand()
        {
            var state = layer.Node(State);
            string time = state?["m_TimeParameterActive"]?.AsBool() == true ? state["m_TimeParameter"]?.AsString() : null;
            return time is "GestureLeftWeight" or "GestureRightWeight" && Value(time[..^6]) == 1 ? time[..^6] : null;
        }
        public string Motion() => Resolve(layer.Node(State)?["m_Motion"], new());
        private string Resolve(YamlNode motion, HashSet<long> seen)
        {
            if ((motion?.FileID ?? 0) == 0) return null;
            if (motion.Guid != null) return motion.Guid + ":" + motion.FileID;
            if (!seen.Add(motion.FileID.Value) || layer.Scene.Doc(motion.FileID.Value) is not { ClassId: 206 } tree || tree.Root["m_BlendType"]?.AsInt() != 0)
                throw new Unsupported("unsupported BlendTree");
            float value = Value(tree.Root["m_BlendParameter"]?.AsString());
            var children = (tree.Root["m_Childs"]?.Seq ?? new()).OrderBy(c => c["m_Threshold"]?.AsFloat()).ToArray();
            if (children.Length == 0 || children.Any(c => !float.IsFinite(c["m_Threshold"]?.AsFloat(float.NaN) ?? float.NaN))) throw new Unsupported("invalid BlendTree thresholds");
            var child = value <= children[0]["m_Threshold"].AsFloat() ? children[0] : value >= children[^1]["m_Threshold"].AsFloat() ? children[^1] :
                children.FirstOrDefault(c => c["m_Threshold"].AsFloat() == value);
            if (child == null) throw new Unsupported("blended pose is not a Catalog entry");
            return Resolve(child["m_Motion"], seen);
        }
    }
    private static bool IsDriver(YamlNode node) => node?["m_Script"]?.Guid == VrchatConstants.AvatarDescriptorScriptGuid && node["m_Script"]?.FileID == -706344726;
    private static void Add(HashSet<string> set, string value) { if (!string.IsNullOrEmpty(value)) set.Add(value); }
}
