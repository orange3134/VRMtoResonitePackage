using System.Text.Json;
using System.Text.Json.Serialization;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.Store;

internal static class SkinMotionChecks
{
    private sealed record Rules(string[] RootBonePathSuffix, Target[] Renderers);
    private sealed record Target(string[] PathSuffix, int MinimumWeightedBones);

    // The expectation selects an authored attachment contract: every weighted bone
    // of these renderers must move with the chosen bone. This is not a visual test.
    public static async Task CheckPackage(Slot parent, string package, string expectations)
    {
        var rules = JsonSerializer.Deserialize<Rules>(File.ReadAllText(expectations), new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        });
        bool ValidPath(string[] path) => path?.Length > 0 && path.All(s => !string.IsNullOrEmpty(s));
        if (rules == null || !ValidPath(rules.RootBonePathSuffix) || rules.Renderers?.Length is not > 0 ||
            rules.Renderers.Any(r => r == null || !ValidPath(r.PathSuffix) || r.MinimumWeightedBones < 1))
            throw new InvalidDataException("Skin motion expectations need a root path and nonempty renderer rules.");
        if (rules.Renderers.Select(r => JsonSerializer.Serialize(r.PathSuffix)).Distinct().Count() != rules.Renderers.Length)
            throw new InvalidDataException("Duplicate renderer rule.");

        await PackageImporter.ImportPackage(package, parent);
        await default(ToWorld);
        IEnumerable<Slot> Slots(Slot slot) => new[] { slot }.Concat(slot.Children.SelectMany(Slots));
        bool Matches(Slot slot, string[] suffix)
        {
            for (int i = suffix.Length - 1; i >= 0; i--, slot = slot.Parent)
                if (slot == null || slot.Name != suffix[i]) return false;
            return true;
        }
        var root = Slots(parent).Where(s => Matches(s, rules.RootBonePathSuffix)).Single();
        var all = parent.GetComponentsInChildren<SkinnedMeshRenderer>().ToArray();
        var selected = rules.Renderers.Select(rule =>
            (rule, renderer: all.Where(r => Matches(r.Slot, rule.PathSuffix)).Single())).ToArray();
        for (int frame = 0; frame < 7200 && selected.Any(s => !s.renderer.Mesh.IsAssetAvailable); frame++)
            await default(NextUpdate);
        var targets = selected.Select(s =>
        {
            var mesh = s.renderer.Mesh.Asset?.Data ?? throw new InvalidDataException("Mesh not loaded: " + s.renderer.Slot.Name);
            var used = new HashSet<int>();
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                var b = mesh.RawBoneBindings[v];
                if (b.weight0 > 0) used.Add(b.boneIndex0);
                if (b.weight1 > 0) used.Add(b.boneIndex1);
                if (b.weight2 > 0) used.Add(b.boneIndex2);
                if (b.weight3 > 0) used.Add(b.boneIndex3);
            }
            if (used.Count < s.rule.MinimumWeightedBones || used.Any(i => i < 0 ||
                    i >= s.renderer.Bones.Count || s.renderer.Bones[i] == null))
                throw new InvalidDataException("Missing weighted bones: " + string.Join("/", s.rule.PathSuffix));
            return (s.rule, bones: used.Select(i => s.renderer.Bones[i]).Distinct().ToArray());
        }).ToArray();
        var bones = targets.SelectMany(t => t.bones).Append(root).Distinct().ToArray();
        Slot originalParent = root.Parent;
        var pivot = originalParent.AddSlot("Skin motion probe");
        var delta = new float3(.1f, .05f, -.03f);
        var failures = new List<string>();
        try
        {
            // IK can drive the root's own position. Move a temporary parent instead
            // and verify the control root really moved before judging other bones.
            root.Parent = pivot;
            var before = bones.ToDictionary(b => b, b => b.GlobalPosition);
            float3 oldPosition = pivot.GlobalPosition;
            pivot.GlobalPosition += delta;
            try
            {
                const float tolerance = .00001f;
                if (!(MathX.Distance(root.GlobalPosition - before[root], delta) <= tolerance))
                    throw new InvalidOperationException("Control root did not move; motion test is invalid.");
                foreach (var target in targets)
                {
                    float error = target.bones.Max(b =>
                    {
                        float distance = MathX.Distance(b.GlobalPosition - before[b], delta);
                        return float.IsFinite(distance) ? distance : float.PositiveInfinity;
                    });
                    string message = $"Skin motion {string.Join("/", target.rule.PathSuffix)}: weighted bones={target.bones.Length}, max error={error:G6} m";
                    Console.WriteLine((error <= tolerance ? "PASS: " : "FAIL: ") + message);
                    if (error > tolerance) failures.Add(message);
                }
            }
            finally { pivot.GlobalPosition = oldPosition; }
        }
        finally { root.Parent = originalParent; pivot.Destroy(); }
        if (failures.Count > 0) throw new Exception(string.Join("\n", failures));
    }
}
