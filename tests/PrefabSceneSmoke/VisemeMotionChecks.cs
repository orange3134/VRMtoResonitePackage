using System.Text.Json;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.Store;

internal static class VisemeMotionChecks
{
    public static async Task CheckPackage(Slot parent, string package, string expectations)
    {
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(expectations));
        if (expected == null || expected.Count == 0) throw new InvalidDataException("Missing phoneme expectations");
        await PackageImporter.ImportPackage(package, parent);
        await default(ToWorld);
        var driver = parent.GetComponentsInChildren<DirectVisemeDriver>().Single();
        if (!driver.Enabled || driver.Source.Target == null) throw new InvalidDataException("Missing active voice source");
        var skin = driver.Slot.GetComponent<SkinnedMeshRenderer>();
        for (int i = 0; i < 7200 && !skin.Mesh.IsAssetAvailable; i++) await default(NextUpdate);
        if (skin.Mesh.Asset?.Data == null) throw new InvalidDataException("Face mesh did not load: " + skin.Slot.Name);
        var weights = Enumerable.Range(0, skin.MeshBlendshapeCount)
            .ToDictionary(skin.BlendShapeName, i => skin.BlendShapeWeights.GetElement(i));

        // Substitute a repeatable signal at the driver's outputs in this in-memory
        // instance. The saved mixer, proxies and mesh targets remain connected.
        var fields = new Dictionary<string, IField<float>>();
        foreach (var phoneme in Enum.GetValues<Viseme>())
        {
            var link = driver[phoneme];
            if (link?.Target == null) continue;
            fields.Add(phoneme.ToString(), link.Target);
            link.Target = null;
        }
        if (!fields.Keys.ToHashSet().SetEquals(expected.Keys)) throw new InvalidDataException("Unexpected phoneme set");
        var signal = fields.Keys.ToDictionary(k => k, _ => 0f);
        foreach (string selected in expected.Keys)
        {
            signal[selected] = 0.75f;
            await Check(selected);
            signal[selected] = 0;
        }
        await Check("reset");
        foreach (var group in expected.GroupBy(p => p.Value).Where(g => g.Count() > 1))
        {
            signal[group.First().Key] = 0.625f;
            signal[group.Last().Key] = 0.25f;
        }
        await Check("simultaneous");
        Console.WriteLine($"Verified {expected.Count} phoneme outputs and reset on {skin.Slot.Name}");

        async Task Check(string selected)
        {
            foreach (var (name, field) in fields) field.Value = signal[name];
            for (int frame = 0; frame < 5; frame++) await default(NextUpdate);
            foreach (var group in expected.GroupBy(p => p.Value))
            {
                float wanted = group.Max(p => signal[p.Key]);
                float actual = weights[group.Key].Value;
                if (!float.IsFinite(actual) || Math.Abs(actual - wanted) > 0.001f)
                    throw new InvalidDataException($"{selected} -> {group.Key}: expected {wanted}, got {actual}");
            }
        }
    }
}
