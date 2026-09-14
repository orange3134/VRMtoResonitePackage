using System.Reflection;
using System.Text;
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
    private Slot _section;
    private int _sectionIndex, _nodeIndex;
    private readonly Dictionary<(Type, object), IWorldElement> _constants = new();
    private static Dictionary<string, Type[]> _types;
    public ExpressionFlux(Slot root)
    {
        _root = root;
        BeginSection("Shared inputs");
    }

    public void BeginSection(string name)
    {
        _section = _root.AddSlot($"{_sectionIndex++:D2} {name}");
        _nodeIndex = 0;
        // Keep constants near their consumers instead of wiring every section back to the first one.
        _constants.Clear();
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
        const int columns = 8;
        const float columnSpacing = 0.65f;
        var sections = expressions.GetComponentsInChildren<ProtoFluxNode>().GroupBy(n => n.Slot.Parent).ToArray();
        int boardIndex = 0;
        foreach (var board in sections.GroupBy(s => s.Key.Parent))
        {
            board.Key.GlobalPosition = expressions.LocalPointToGlobal(new float3(boardIndex++ * (columns * columnSpacing + 1), 0, 0));
            float sectionOffset = 0;
            foreach (var section in board)
            {
                section.Key.LocalPosition = new float3(0, -sectionOffset, 0);
                var nodes = section.ToArray();
                float rowOffset = 0;
                for (int first = 0; first < nodes.Length; first += columns)
                {
                    float rowHeight = 0.3f;
                    for (int column = 0; column < columns && first + column < nodes.Length; column++)
                    {
                        var node = nodes[first + column];
                        node.Slot.LocalPosition = new float3(column * columnSpacing, -rowOffset, 0);
                        // Sequence and other variable-port nodes need more vertical space.
                        rowHeight = Math.Max(rowHeight, 0.18f + 0.045f * Math.Max(node.NodeInputCount, node.NodeOutputCount + node.NodeImpulseCount));
                    }
                    rowOffset += rowHeight;
                }
                sectionOffset += rowOffset + 0.6f;
            }
        }
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
        var node = NodeSlot(typeof(Nodes.RefObjectInput<T>), value is Slot slot ? slot.Name : typeof(T).Name).AttachComponent<Nodes.RefObjectInput<T>>();
        node.Target.Target = value;
        return node;
    }
    public static string Path(string key)
    {
        int separator = key.IndexOf('/');
        if (separator < 0) return "Expr/" + key;
        string suffix = key[(separator + 1)..];
        return "Expr/" + key[..separator] + "." + (key.StartsWith("Own/", StringComparison.Ordinal) ? suffix : Convert.ToHexString(Encoding.UTF8.GetBytes(suffix)));
    }
    public IWorldElement Read<T>(IWorldElement source, string key) => Read<T>(source, Text(Path(key)));
    public IWorldElement Read<T>(IWorldElement source, IWorldElement path)
    {
        var node = Node(typeof(T).IsValueType ? "ReadDynamicValueVariable" : "ReadDynamicObjectVariable", typeof(T),
            ("Source", source), ("Path", path));
        return Out(node, "Value");
    }
    public Component Write<T>(IWorldElement target, string key, IWorldElement value) => Write<T>(target, Text(Path(key)), value);
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
    public IWorldElement And(params IWorldElement[] terms) => terms.Aggregate(Constant(true), (a, b) => Node("AND_Bool", null, ("A", a), ("B", b)));
    public IWorldElement Or(params IWorldElement[] terms) => terms.Aggregate(Constant(false), (a, b) => Node("OR_Bool", null, ("A", a), ("B", b)));
    public IWorldElement Add(IWorldElement a, IWorldElement b) => Binary<float>("ValueAdd", a, b);
    public IWorldElement Sub(IWorldElement a, IWorldElement b) => Binary<float>("ValueSub", a, b);
    public IWorldElement Mul(IWorldElement a, IWorldElement b) => Binary<float>("ValueMul", a, b);
    public IWorldElement Div(IWorldElement a, IWorldElement b) => Binary<float>("ValueDiv", a, b);
    public IWorldElement Greater(IWorldElement a, IWorldElement b) => Binary<float>("ValueGreaterThan", a, b);
    public IWorldElement Lerp(IWorldElement a, IWorldElement b, IWorldElement t) => Add(a, Mul(Sub(b, a), t));
    public IWorldElement Clamp01(IWorldElement value) => Choose<float>(Greater(value, Constant(1f)), Constant(1f),
        Choose<float>(Greater(Constant(0f), value), Constant(0f), value));
    public IWorldElement Active(IWorldElement slot) => Node("GetSlotActive", null, ("Instance", slot));
    public Component Each(IWorldElement parent, Func<IWorldElement, IWorldElement> body)
    {
        var loop = Node("For", null, ("Count", Node("ChildrenCount", null, ("Instance", parent))));
        var child = Node("GetChild", null, ("Instance", parent), ("ChildIndex", Out(loop, "Iteration")));
        Link(loop, "LoopIteration", body(child));
        return loop;
    }
    public Component Receiver(string tag, bool withSlot = true)
    {
        var node = Node(withSlot ? "DynamicImpulseReceiverWithObject" : "DynamicImpulseReceiver", withSlot ? typeof(Slot) : null);
        var global = node.Slot.AddSlot("Tag").AttachComponent<GlobalValue<string>>();
        global.Value.Value = tag;
        Link(node, "Tag", global);
        return node;
    }
    public Component Trigger(IWorldElement destination, string tag, IWorldElement payload) =>
        Node("DynamicImpulseTriggerWithObject", typeof(Slot), ("TargetHierarchy", destination), ("Tag", Text(tag)),
            ("ExcludeDisabled", Constant(true)), ("Value", payload));

    public static Slot Record(Slot parent, string name)
    {
        var slot = parent.AddSlot(name);
        var space = slot.AttachComponent<DynamicVariableSpace>();
        space.SpaceName.Value = "Expr";
        space.OnlyDirectBinding.Value = true;
        return slot;
    }
    public static DynamicValueVariable<T> Data<T>(Slot slot, string name, T value)
    {
        var variable = slot.AttachComponent<DynamicValueVariable<T>>();
        variable.VariableName.Value = Path(name);
        variable.Value.Value = value;
        return variable;
    }
    public static DynamicReferenceVariable<T> Reference<T>(Slot slot, string name, T value) where T : class, IWorldElement
    {
        var variable = slot.AttachComponent<DynamicReferenceVariable<T>>();
        variable.VariableName.Value = Path(name);
        variable.Reference.Target = value;
        return variable;
    }
}
