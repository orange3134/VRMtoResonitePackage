using System.Text.Json;
using FrooxEngine;

internal static class ImportedInitialShapeChecks
{
    // An independent source-data oracle, rather than checking Result against its own Base.
    public static void Run(Slot avatar)
    {
        string json = Environment.GetEnvironmentVariable("RESOPON_TEST_INITIAL_SHAPES");
        if (json == null) return;
        var expected = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, float>>>(json);
        foreach (var (rendererName, shapes) in expected)
        {
            var renderer = avatar.GetComponentsInChildren<SkinnedMeshRenderer>().Single(r => r.Slot.Name == rendererName);
            foreach (var (shape, value) in shapes)
            {
                var field = renderer.TryGetBlendShape(shape);
                if (!float.IsFinite(value) || field == null || Math.Abs(field.Value - value) > .001f)
                    throw new Exception($"Saved initial shape {rendererName}/{shape}: {field?.Value} != {value}");
                Console.WriteLine($"PASS: saved neutral {rendererName}/{shape} = {value}");
            }
        }
    }
}
