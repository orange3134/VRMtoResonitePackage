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
            output => Value<string>(output, "Id"), output => new
            {
                Path = Value<string>(output, "Path"), Shape = Value<string>(output, "Shape"),
                Baseline = Value<float>(output, "Baseline"), TrackingWeight = Value<float>(output, "TrackingWeight")
            }, StringComparer.Ordinal);
        var clips = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var entry in root.FindChild("Catalog").Children)
        {
            string id = Value<string>(entry, "Id");
            clips.Add(id, new
            {
                Name = Value<string>(entry, "DisplayName"), Enabled = Value<bool>(entry, "Enabled"),
                Values = Pose(entry),
                Bindings = entry.FindChild("Bindings").GetComponentsInChildren<DynamicReferenceVariable<Slot>>()
                    .Where(v => v.VariableName.Value == ExpressionTestFields.VariablePath(v.Slot, "Output"))
                    .Select(v => Value<string>(v.Reference.Target, "Id")).OrderBy(v => v, StringComparer.Ordinal).ToArray()
            });
        }
        string pairPrefix = ExpressionTestFields.VariablePath(root.FindChild("GestureTable"), "Pair.");
        var table = root.FindChild("GestureTable").ExpressionVariables<DynamicReferenceVariable<Slot>>()
            .Where(v => v.VariableName.Value.StartsWith(pairPrefix, StringComparison.Ordinal))
            .ToDictionary(v => int.Parse(v.VariableName.Value[pairPrefix.Length..]), v => v.Reference.Target);
        if (table.Count != 64 || Enumerable.Range(0, 64).Any(i => !table.ContainsKey(i)))
            throw new InvalidOperationException("Baseline comparison needs exactly 64 gesture table rows");
        string json = JsonSerializer.Serialize(new
        {
            Outputs = new SortedDictionary<string, object>(outputs.ToDictionary(p => p.Key, p => (object)p.Value), StringComparer.Ordinal),
            Catalog = clips,
            Pairs = Enumerable.Range(0, 64).Select(i => table[i] == null ? null : Value<string>(table[i], "Id")).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(artifacts, "expressions.json"), json);
        return json;
    }

    public static SortedDictionary<string, float> Pose(Slot entry)
    {
        var values = new SortedDictionary<string, float>(StringComparer.Ordinal);
        // Reading legacy AnimX is only for old/new regression comparisons, never generation.
        var legacy = entry.GetComponent<StaticAnimationProvider>();
        foreach (var binding in entry.FindChild("Bindings").Children)
        {
            string id = Value<string>(ExpressionTestFields.Reference<Slot>(binding, "Output"), "Id");
            if (legacy == null) values.Add(id, Value<float>(binding, "Value"));
            else
            {
                var data = legacy.Asset?.Data ?? throw new InvalidOperationException("Legacy asset not loaded");
                int index = data.FindTrackIndex("Expression", id);
                values.Add(id, ((Elements.Assets.IAnimationTrack<float>)data[index]).Sample(float.MaxValue));
            }
        }
        return values;
    }

    private static T Value<T>(Slot slot, string name) => slot.ExpressionVariables<DynamicValueVariable<T>>()
        .Single(v => v.VariableName.Value == ExpressionTestFields.VariablePath(slot, name)).Value.Value;
}
