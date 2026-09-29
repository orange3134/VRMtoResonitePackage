using System.Text.Json;
using FrooxEngine;

// Compare authored data independently of runtime graph layout and session-local IDs.
// Legacy animation packages are compared by their endpoints, which are all the new system retains.
internal static class ExpressionPackageSnapshot
{
    public static string Capture(Slot root, string artifacts)
    {
        Directory.CreateDirectory(artifacts);
        var outputs = root.FindChild("Outputs").Children.ToDictionary(
            output => OutputIdentity(output), output => new
            {
                Baseline = AuthoredBase(output), TrackingWeight = Value<float>(output, "TrackingWeight")
            }, StringComparer.Ordinal);
        var clips = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var entry in root.FindChild("Catalog").Children)
        {
            string id = Value<string>(entry, "Id");
            clips.Add(id, new
            {
                Name = Value<string>(entry, "DisplayName"),
                Values = Pose(entry),
                Bindings = Pose(entry).Keys.ToArray()
            });
        }
        var tableSlot = root.FindChild("DV")?.FindChild("GestureTable") ?? root.FindChild("GestureTable");
        string pairPrefix = ExpressionTestFields.VariablePath(tableSlot, tableSlot.Parent.Name == "DV" ? "" : "Pair.");
        var table = tableSlot.ExpressionVariables<DynamicReferenceVariable<Slot>>()
            .Where(v => v.VariableName.Value.StartsWith(pairPrefix, StringComparison.Ordinal))
            .ToDictionary(v => NormalizePairKey(v.VariableName.Value[pairPrefix.Length..]), v => v.Reference.Target);
        var keys = (from left in Enumerable.Range(0, 8) from right in Enumerable.Range(0, 8)
                    select $"L{left}R{right}").ToArray();
        if (table.Count != 64 || keys.Any(key => !table.ContainsKey(key)))
            throw new InvalidOperationException("Baseline comparison needs exactly 64 gesture table rows");
        string json = JsonSerializer.Serialize(new
        {
            Outputs = new SortedDictionary<string, object>(outputs.ToDictionary(p => p.Key, p => (object)p.Value), StringComparer.Ordinal),
            Catalog = clips,
            Pairs = keys.Select(key => table[key] == null ? null : Value<string>(table[key], "Id")).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(artifacts, "expressions.json"), json);
        return json;
    }

    public static SortedDictionary<string, float> Pose(Slot entry)
    {
        var values = new SortedDictionary<string, float>(StringComparer.Ordinal);
        var bindings = entry.FindChild("Bindings");
        // Version 31 reads directly from Clip; retain v30 and earlier snapshot support.
        if (bindings == null || bindings.GetComponent<DynamicVariableSpace>()?.SpaceName.Value == "ExpressionSystem.Catalog.Clip.Binding")
        {
            string prefix = bindings == null ? "ExpressionSystem.Catalog.Clip/Binding." : "ExpressionSystem.Catalog.Clip.Binding/";
            var outputs = entry.Parent.Parent.FindChild("Outputs").Children.ToDictionary(o => Value<string>(o, "Id"));
            foreach (var variable in (bindings ?? entry).ExpressionVariables<DynamicValueVariable<float>>()
                .Where(v => v.VariableName.Value.StartsWith(prefix, StringComparison.Ordinal)))
            {
                string key = variable.VariableName.Value[prefix.Length..];
                values.Add(OutputIdentity(outputs[key]), variable.Value.Value);
            }
            return values;
        }
        // Reading legacy AnimX is only for old/new regression comparisons, never generation.
        var legacy = entry.GetComponent<StaticAnimationProvider>();
        foreach (var binding in entry.FindChild("Bindings").Children)
        {
            var output = ExpressionTestFields.Reference<Slot>(binding, "Output");
            string id = Value<string>(output, "Id");
            string identity = OutputIdentity(output);
            if (legacy == null) values.Add(identity, Value<float>(binding, "Value"));
            else
            {
                var data = legacy.Asset?.Data ?? throw new InvalidOperationException("Legacy asset not loaded");
                int index = data.FindTrackIndex("Expression", id);
                values.Add(identity, ((Elements.Assets.IAnimationTrack<float>)data[index]).Sample(float.MaxValue));
            }
        }
        return values;
    }

    public static string OutputIdentity(Slot output)
    {
        var target = ExpressionTestFields.OutputTarget(output);
        var renderer = target.FindNearestParent<SkinnedMeshRenderer>();
        var owner = renderer ?? target.FindNearestParent<Component>();
        var avatar = output.Parent.Parent.Parent;
        var path = new Stack<string>();
        for (var slot = owner.Slot; slot != avatar; slot = slot.Parent)
        {
            if (slot == null) throw new InvalidOperationException("Output target is outside the avatar");
            int sibling = slot.Parent.Children.Where(s => s.Name == slot.Name).ToList().IndexOf(slot);
            path.Push(JsonSerializer.Serialize(new { Name = slot.Name, Sibling = sibling }));
        }
        int component = owner.Slot.GetComponents<Component>().Where(c => c.GetType() == owner.GetType()).ToList().IndexOf(owner);
        string shape = renderer == null ? "Value" : renderer.BlendShapeName(Enumerable.Range(0, renderer.BlendShapeWeights.Count)
            .Single(i => renderer.BlendShapeWeights.GetElement(i) == target));
        return JsonSerializer.Serialize(new { Path = path.ToArray(), Component = owner.GetType().FullName, Index = component, Shape = shape });
    }

    public static float AuthoredBase(Slot output)
    {
        var legacy = output.ExpressionVariables<DynamicValueVariable<float>>()
            .SingleOrDefault(v => v.VariableName.Value == ExpressionTestFields.VariablePath(output, "Baseline"));
        if (legacy != null) return legacy.Value.Value;
        var neutral = output.Parent.Parent.FindChild("Catalog").Children.Single(c => Value<string>(c, "Id") == "resopon:neutral");
        return Pose(neutral)[OutputIdentity(output)];
    }

    // Baseline snapshots also accept pre-v22 numeric keys; generated avatars use only L/R keys.
    private static string NormalizePairKey(string key) => int.TryParse(key, out int index)
        ? $"L{index / 8}R{index % 8}" : key;

    private static T Value<T>(Slot slot, string name) => slot.ExpressionVariables<DynamicValueVariable<T>>()
        .Single(v => v.VariableName.Value == ExpressionTestFields.VariablePath(slot, name)).Value.Value;
}
