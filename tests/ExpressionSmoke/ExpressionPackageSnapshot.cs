using System.Security.Cryptography;
using System.Text.Json;
using FrooxEngine;

// Compare authored data independently of runtime graph layout and session-local IDs.
// Re-saving loaded AnimX data includes every key, interpolation mode and tangent;
// sampling just a few times could miss a changed curve between those samples.
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
            var data = entry.GetComponent<StaticAnimationProvider>()?.Asset?.Data
                ?? throw new InvalidOperationException("Baseline comparison needs a loaded animation: " + id);
            string animationPath = Path.Combine(artifacts, clips.Count.ToString("D4") + ".animx");
            data.SaveToFile(animationPath);
            clips.Add(id, new
            {
                Name = Value<string>(entry, "DisplayName"), Duration = Value<float>(entry, "Duration"),
                Loop = Value<bool>(entry, "Loop"), Enabled = Value<bool>(entry, "Enabled"),
                FadeIn = Value<float>(entry, "FadeIn"), FadeOut = Value<float>(entry, "FadeOut"),
                AnimationSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(animationPath))),
                Bindings = entry.FindChild("Bindings").GetComponentsInChildren<DynamicReferenceVariable<Slot>>()
                    .Where(v => v.VariableName.Value == "Expr/Output")
                    .Select(v => Value<string>(v.Reference.Target, "Id")).OrderBy(v => v, StringComparer.Ordinal).ToArray()
            });
        }
        var table = root.FindChild("GestureTable").GetComponentsInChildren<DynamicReferenceVariable<Slot>>()
            .Where(v => v.VariableName.Value.StartsWith("Expr/Pair.", StringComparison.Ordinal))
            .ToDictionary(v => int.Parse(v.VariableName.Value["Expr/Pair.".Length..]), v => v.Reference.Target);
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

    private static T Value<T>(Slot slot, string name) => slot.GetComponents<DynamicValueVariable<T>>()
        .Single(v => v.VariableName.Value == "Expr/" + name).Value.Value;
}
