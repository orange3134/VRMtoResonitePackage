using System.Reflection;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.ProtoFlux;
using ProtoFlux.Core;
using Nodes = FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes;

namespace VrmToResonitePackage.Expressions;

/// <summary>Builds stock ProtoFlux components. No converter callback is retained in the exported graph.</summary>
internal sealed class ExpressionFlux
{
    private readonly Slot _root;
    private readonly bool _useDynamicInputs;
    private Slot _section;
    private int _sectionIndex, _nodeIndex;
    private readonly Dictionary<(Type, object), IWorldElement> _constants = new();
    private readonly Dictionary<(Type, IWorldElement, IWorldElement), IWorldElement> _reads = new();
    private readonly Dictionary<(Type, string), IWorldElement> _dynamicInputs = new();
    private readonly Dictionary<(Type, IWorldElement), IWorldElement> _references = new();
    private readonly Dictionary<Slot, IWorldElement> _owners = new();
    private readonly Dictionary<Slot, IWorldElement> _ownerChecks = new();
    private IWorldElement _now;
    private static Dictionary<string, Type[]> _types;
    public ExpressionFlux(Slot root, bool useDynamicInputs = true)
    {
        _root = root;
        _useDynamicInputs = useDynamicInputs;
        BeginSection("Shared inputs");
    }

    public void BeginSection(string name)
    {
        _section = _root.AddSlot($"{_sectionIndex++:D2} {name}");
        _nodeIndex = 0;
        // Keep constants near their consumers instead of wiring every section back to the first one.
        _constants.Clear();
        _reads.Clear();
        _dynamicInputs.Clear();
    }

    private Slot NodeSlot(Type type, string detail = null)
    {
        string name = type.Name.Split('`')[0];
        if (type.IsGenericType) name += "<" + string.Join(", ", type.GetGenericArguments().Select(t => t.Name)) + ">";
        if (!string.IsNullOrEmpty(detail)) name += " : " + detail.Replace('\n', ' ').Replace('\r', ' ');
        return _section.AddSlot($"{_nodeIndex++:D3} {name}");
    }

    /// <summary>Lay out each logic board without changing its functional parent hierarchy.</summary>
    public static void Arrange(Slot expressions)
    {
        const float columnSpacing = 0.65f;
        var sections = expressions.GetComponentsInChildren<ProtoFluxNode>().GroupBy(n => n.Slot.Parent).ToArray();
        float boardOffset = 0;
        foreach (var board in sections.GroupBy(s => s.Key.Parent))
        {
            var columns = DependencyColumns(board.SelectMany(s => s).ToArray());
            board.Key.GlobalPosition = expressions.LocalPointToGlobal(new float3(boardOffset, 0, 0));
            float sectionOffset = 0;
            foreach (var section in board)
            {
                section.Key.LocalPosition = new float3(0, -sectionOffset, 0);
                float sectionHeight = 0;
                foreach (var column in section.GroupBy(node => columns[node]))
                {
                    float rowOffset = 0;
                    foreach (var node in column)
                    {
                        node.Slot.LocalPosition = new float3(column.Key * columnSpacing, -rowOffset, 0);
                        // Sequence and other variable-port nodes need more vertical space.
                        int leftPorts = node.AllInputs.Count() + node.NodeReferenceCount + node.NodeGlobalRefCount + node.AllTargetOperations.Count();
                        int rightPorts = node.AllSourceOutputs.Count() + node.AllImpulses.Count();
                        rowOffset += Math.Max(0.3f, 0.18f + 0.045f * Math.Max(leftPorts, rightPorts));
                    }
                    sectionHeight = Math.Max(sectionHeight, rowOffset);
                }
                sectionOffset += sectionHeight + 0.6f;
            }
            boardOffset += (columns.Values.Max() + 1) * columnSpacing + 1;
        }
    }

    private static Dictionary<ProtoFluxNode, int> DependencyColumns(ProtoFluxNode[] nodes)
    {
        var outgoing = nodes.ToDictionary(node => node, _ => new HashSet<ProtoFluxNode>());
        void Edge(ProtoFluxNode source, ProtoFluxNode target)
        {
            if (source != null && target != null && source != target && outgoing.ContainsKey(source) && outgoing.ContainsKey(target))
                outgoing[source].Add(target);
        }
        foreach (var node in nodes)
        {
            // Data and node references enter on the left; impulses leave on the right.
            // AllInputs/AllImpulses also enumerate variable-sized ports such as Sequence.Calls.
            foreach (var input in node.AllInputs.Concat(node.NodeReferences)) Edge(OwningNode(input.Target), node);
            foreach (var impulse in node.AllImpulses) Edge(node, OwningNode(impulse.Target));
        }

        // Feedback cannot all point right. Collapse each strongly connected component into
        // one column, then layer the acyclic graph by its longest incoming dependency chain.
        var indices = new Dictionary<ProtoFluxNode, int>();
        var low = new Dictionary<ProtoFluxNode, int>();
        var component = new Dictionary<ProtoFluxNode, int>();
        var stack = new Stack<ProtoFluxNode>();
        var active = new HashSet<ProtoFluxNode>();
        int nextIndex = 0, componentCount = 0;
        void Visit(ProtoFluxNode node)
        {
            indices[node] = low[node] = nextIndex++;
            stack.Push(node); active.Add(node);
            foreach (var target in outgoing[node])
            {
                if (!indices.ContainsKey(target)) { Visit(target); low[node] = Math.Min(low[node], low[target]); }
                else if (active.Contains(target)) low[node] = Math.Min(low[node], indices[target]);
            }
            if (low[node] != indices[node]) return;
            ProtoFluxNode member;
            do { member = stack.Pop(); active.Remove(member); component[member] = componentCount; } while (member != node);
            componentCount++;
        }
        foreach (var node in nodes) if (!indices.ContainsKey(node)) Visit(node);

        var incoming = Enumerable.Range(0, componentCount).Select(_ => new HashSet<int>()).ToArray();
        foreach (var source in nodes)
            foreach (var target in outgoing[source])
                if (component[source] != component[target]) incoming[component[target]].Add(component[source]);
        var columns = new int?[componentCount];
        int Column(int group) => columns[group] ??= incoming[group].Select(parent => Column(parent) + 1).DefaultIfEmpty(0).Max();
        return nodes.ToDictionary(node => node, node => Column(component[node]));
    }

    private static ProtoFluxNode OwningNode(IWorldElement element)
    {
        for (; element != null; element = element.Parent)
            if (element is ProtoFluxNode node) return node;
        return null;
    }

    public Component Node(string name, Type generic = null, params (string Port, IWorldElement Value)[] inputs)
    {
        _types ??= new[] { typeof(Component).Assembly, typeof(Nodes.Sequence).Assembly }
            .Distinct().SelectMany(a => a.GetTypes()).Where(t => typeof(Component).IsAssignableFrom(t) && !t.IsAbstract &&
                t.Namespace?.Contains("ProtoFlux") == true).GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.ToArray());
        string key = generic == null ? name : name + "`1";
        if (!_types.TryGetValue(key, out var candidates) || candidates.Length != 1)
            throw new InvalidOperationException("Ambiguous or missing stock ProtoFlux node: " + key);
        var type = generic == null ? candidates[0] : candidates[0].MakeGenericType(generic);
        var node = NodeSlot(type).AttachComponent(type);
        foreach (var (port, value) in inputs) Link(node, port, value);
        return node;
    }

    public static object Member(object node, string field) => node.GetType().GetField(field)?.GetValue(node)
        ?? throw new InvalidOperationException($"Missing port {node.GetType().FullName}.{field}");
    public static IWorldElement Out(object node, string port) => (IWorldElement)Member(node, port);
    public static void Link(object node, string port, IWorldElement target)
    {
        var reference = (ISyncRef)Member(node, port);
        if (target != null && !reference.TargetType.IsInstanceOfType(target))
            throw new InvalidOperationException($"Cannot connect {target.GetType().Name} to {node.GetType().Name}.{port} ({reference.TargetType})");
        reference.Target = target;
    }
    public IWorldElement Constant<T>(T value) where T : unmanaged
    {
        if (_constants.TryGetValue((typeof(T), value), out var cached)) return cached;
        var node = NodeSlot(typeof(Nodes.ValueInput<T>), Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)).AttachComponent<Nodes.ValueInput<T>>();
        node.Value.Value = value;
        _constants[(typeof(T), value)] = node;
        return node;
    }
    public IWorldElement Text(string value)
    {
        value ??= "";
        if (_constants.TryGetValue((typeof(string), value), out var cached)) return cached;
        var node = NodeSlot(typeof(Nodes.ValueObjectInput<string>), value).AttachComponent<Nodes.ValueObjectInput<string>>();
        node.Value.Value = value;
        _constants[(typeof(string), value)] = node;
        return node;
    }
    public IWorldElement Ref<T>(T value) where T : class, IWorldElement
    {
        if (_references.TryGetValue((typeof(T), value), out var cached)) return cached;
        var node = NodeSlot(typeof(Nodes.RefObjectInput<T>), value is Slot slot ? slot.Name : typeof(T).Name).AttachComponent<Nodes.RefObjectInput<T>>();
        node.Target.Target = value;
        return _references[(typeof(T), value)] = node;
    }
    // Keep clock and owner nodes local to a board: shared node references join Flux groups.
    public IWorldElement Now => _now ??= Node("WorldTimeFloat");
    public IWorldElement Owner(Slot root) => _owners.TryGetValue(root, out var owner) ? owner :
        _owners[root] = _root == root || _root.IsChildOf(root)
            ? Node("GetActiveUserSelf")
            : Node("GetActiveUser", null, ("Instance", Ref(root)));
    public IWorldElement IsOwner(Slot root) => _ownerChecks.TryGetValue(root, out var check) ? check :
        _ownerChecks[root] = Node("IsLocalUser", null, ("User", Owner(root)));
    public static string Path(string spaceName, string key) => spaceName + "/" + key;
    public static string Path(Slot slot, string key) => Path(NamedSpace(slot)?.SpaceName.Value
        ?? throw new InvalidOperationException("Expression record has no variable space: " + slot.Name), key);

    public IWorldElement Read<T>(IWorldElement source, string spaceName, string key)
    {
        string path = Path(spaceName, key);
        // Dynamic Inputs bind from their own Slot, not from a runtime Source input.
        // Only replace fixed references resolving to the same named variable space.
        if (_useDynamicInputs && source is Nodes.RefObjectInput<Slot> reference && reference.Target.Target is Slot slot &&
            NamedSpace(slot) is { } space && space.SpaceName.Value == spaceName && space == NamedSpace(_section))
        {
            if (_dynamicInputs.TryGetValue((typeof(T), path), out var cached)) return cached;
            var node = Node(typeof(T).IsValueType ? "DynamicVariableValueInput" : "DynamicVariableObjectInput", typeof(T));
            var name = node.Slot.AddSlot("VariableName").AttachComponent<GlobalValue<string>>();
            name.Value.Value = path;
            Link(node, "VariableName", name);
            return _dynamicInputs[(typeof(T), path)] = Out(node, "Value");
        }
        return Read<T>(source, Text(path));
    }

    private static DynamicVariableSpace NamedSpace(Slot slot) =>
        slot.GetComponentInParents<DynamicVariableSpace>();
    public IWorldElement Read<T>(IWorldElement source, IWorldElement path)
    {
        if (_reads.TryGetValue((typeof(T), source, path), out var cached)) return cached;
        var node = Node(typeof(T).IsValueType ? "ReadDynamicValueVariable" : "ReadDynamicObjectVariable", typeof(T),
            ("Source", source), ("Path", path));
        return _reads[(typeof(T), source, path)] = Out(node, "Value");
    }
    public Component Write<T>(IWorldElement target, string spaceName, string key, IWorldElement value) =>
        Write<T>(target, Text(Path(spaceName, key)), value);
    public Component Write<T>(IWorldElement target, IWorldElement path, IWorldElement value) =>
        Node(typeof(T).IsValueType ? "WriteDynamicValueVariable" : "WriteDynamicObjectVariable", typeof(T),
            ("Target", target), ("Path", path), ("Value", value));
    public Component Local<T>() => Node(typeof(T).IsValueType ? "LocalValue" : "LocalObject", typeof(T));
    public Component Set<T>(IWorldElement variable, IWorldElement value) =>
        Node(typeof(T).IsValueType ? "ValueWrite" : "ObjectWrite", typeof(T), ("Variable", variable), ("Value", value));
    public Component Sequence(params IWorldElement[] actions)
    {
        var node = NodeSlot(typeof(Nodes.Sequence)).AttachComponent<Nodes.Sequence>();
        foreach (var action in actions.Where(a => a != null)) node.Calls.Add((ISyncNodeOperation)action);
        return node;
    }
    public Component If(IWorldElement condition, IWorldElement onTrue, IWorldElement onFalse = null) =>
        Node("If", null, ("Condition", condition), ("OnTrue", onTrue), ("OnFalse", onFalse));
    public IWorldElement Choose<T>(IWorldElement condition, IWorldElement onTrue, IWorldElement onFalse) =>
        Node(typeof(T).IsValueType ? "ValueConditional" : "ObjectConditional", typeof(T),
            ("Condition", condition), ("OnTrue", onTrue), ("OnFalse", onFalse));
    public IWorldElement Binary<T>(string op, IWorldElement a, IWorldElement b) => Node(op, typeof(T), ("A", a), ("B", b));
    public IWorldElement Equal<T>(IWorldElement a, IWorldElement b) => Binary<T>(typeof(T).IsValueType ? "ValueEquals" : "ObjectEquals", a, b);
    public IWorldElement Not(IWorldElement a) => Node("NOT_Bool", null, ("A", a));
    public IWorldElement And(params IWorldElement[] terms) => terms.Length == 0 ? Constant(true) : terms.Aggregate((a, b) => Node("AND_Bool", null, ("A", a), ("B", b)));
    public IWorldElement Or(params IWorldElement[] terms) => terms.Length == 0 ? Constant(false) : terms.Aggregate((a, b) => Node("OR_Bool", null, ("A", a), ("B", b)));
    public IWorldElement Add(IWorldElement a, IWorldElement b) => Binary<float>("ValueAdd", a, b);
    public IWorldElement Sub(IWorldElement a, IWorldElement b) => Binary<float>("ValueSub", a, b);
    public IWorldElement Mul(IWorldElement a, IWorldElement b) => Binary<float>("ValueMul", a, b);
    public IWorldElement Div(IWorldElement a, IWorldElement b) => Binary<float>("ValueDiv", a, b);
    public IWorldElement Greater(IWorldElement a, IWorldElement b) => Binary<float>("ValueGreaterThan", a, b);
    public IWorldElement Lerp(IWorldElement a, IWorldElement b, IWorldElement t) =>
        Node("ValueLerpUnclamped", typeof(float), ("From", a), ("To", b), ("Lerp", t));
    public IWorldElement Clamp01(IWorldElement value) => Node("Clamp01_Float", null, ("N", value));
    public IWorldElement Active(IWorldElement slot) => Node("GetSlotActive", null, ("Instance", slot));
    public Component Each(IWorldElement parent, Func<IWorldElement, IWorldElement> body)
    {
        // None of these loop bodies change the collection; preserve direct-child order.
        var loop = NodeSlot(typeof(Nodes.ForEachObject<IReadOnlyList<Slot>, Slot>))
            .AttachComponent<Nodes.ForEachObject<IReadOnlyList<Slot>, Slot>>();
        Link(loop, "Collection", Node("Children", null, ("Instance", parent)));
        Link(loop, "LoopIteration", body(Out(loop, "Element")));
        return loop;
    }
    public Component Receiver(string tag, bool withString = true) => Receiver(tag, withString ? typeof(string) : null);
    public Component Receiver<T>(string tag) => Receiver(tag, typeof(T));
    private Component Receiver(string tag, Type type)
    {
        var node = Node(type == null ? "DynamicImpulseReceiver" : type.IsValueType
            ? "DynamicImpulseReceiverWithValue" : "DynamicImpulseReceiverWithObject", type);
        var global = node.Slot.AddSlot("Tag").AttachComponent<GlobalValue<string>>();
        global.Value.Value = tag;
        Link(node, "Tag", global);
        return node;
    }
    public Component Trigger(IWorldElement destination, string tag) =>
        Node("DynamicImpulseTrigger", null, ("TargetHierarchy", destination), ("Tag", Text(tag)),
            ("ExcludeDisabled", Constant(true)));
    public Component Trigger(IWorldElement destination, string tag, IWorldElement payload) =>
        Trigger<string>(destination, Text(tag), payload);
    public Component Trigger<T>(IWorldElement destination, IWorldElement tag, IWorldElement payload) =>
        Node(typeof(T).IsValueType ? "DynamicImpulseTriggerWithValue" : "DynamicImpulseTriggerWithObject", typeof(T),
            ("TargetHierarchy", destination), ("Tag", tag),
            ("ExcludeDisabled", Constant(true)), ("Value", payload));

    public static Slot Record(Slot parent, string name, string spaceName)
    {
        var slot = parent.AddSlot(name);
        var space = slot.AttachComponent<DynamicVariableSpace>();
        space.SpaceName.Value = spaceName;
        space.OnlyDirectBinding.Value = true;
        return slot;
    }
    public static DynamicValueVariable<T> Data<T>(Slot slot, string name, T value)
    {
        var variable = slot.AttachComponent<DynamicValueVariable<T>>();
        variable.VariableName.Value = Path(slot, name);
        variable.Value.Value = value;
        return variable;
    }
    public static DynamicReferenceVariable<T> Reference<T>(Slot slot, string name, T value) where T : class, IWorldElement
    {
        var variable = slot.AttachComponent<DynamicReferenceVariable<T>>();
        variable.VariableName.Value = Path(slot, name);
        variable.Reference.Target = value;
        return variable;
    }
}
