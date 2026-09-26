using FrooxEngine;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private readonly Dictionary<SkinnedMeshRenderer, DynamicBlendShapeDriver> _meshDrivers = new();
    private Slot _drivers;

    private static string UniqueChildName(Slot parent, string name)
    {
        string candidate = name;
        for (int suffix = 2; parent.Children.Any(s => s.Name == candidate); suffix++)
            candidate = $"{name} ({suffix})";
        return candidate;
    }

    private IField<float> BuildOutputTarget(IField<float> field, float initialValue)
    {
        var renderer = field.FindNearestParent<SkinnedMeshRenderer>();
        // The generic resolver also supports standalone fields. Write those directly;
        // all mesh outputs are grouped by renderer identity, never by slot name.
        if (renderer == null) return field;
        int index = Enumerable.Range(0, renderer.BlendShapeWeights.Count)
            .Single(i => renderer.BlendShapeWeights.GetElement(i) == field);
        string name = renderer.BlendShapeName(index);
        if (string.IsNullOrEmpty(name) || renderer.TryGetBlendShape(name) != field)
            throw new InvalidOperationException($"Cannot resolve a unique blendshape name: {renderer.Slot.Name}[{index}]");
        if (!_meshDrivers.TryGetValue(renderer, out var driver))
        {
            _drivers ??= _root.AddSlot("Drivers");
            driver = _drivers.AddSlot(UniqueChildName(_drivers, renderer.Slot.Name)).AttachComponent<DynamicBlendShapeDriver>();
            driver.Renderer.Target = renderer;
            _meshDrivers.Add(renderer, driver);
        }
        var shape = driver.BlendShapes.Add();
        shape.BlendShapeName.Value = name;
        shape.Value.Value = initialValue;
        // Adding an entry does not refresh native targets when Renderer is unchanged.
        // Link each entry explicitly, including entries added after an asset await.
        if (!shape._drive.TryLink(field))
            throw new InvalidOperationException($"Cannot drive blendshape: {renderer.Slot.Name}/{name}");
        var smooth = driver.Slot.AddSlot(UniqueChildName(driver.Slot, name)).AttachComponent<SmoothValue<float>>();
        smooth.Speed.Value = 20f;
        smooth.WriteBack.Value = false;
        smooth.TargetValue.Value = initialValue;
        if (!smooth.Value.TryLink(shape.Value))
            throw new InvalidOperationException($"Cannot smooth blendshape: {renderer.Slot.Name}/{name}");
        // Selection writes only the destination. SmoothValue owns interpolation,
        // including retargeting mid-transition; the native driver keeps name binding.
        return smooth.TargetValue;
    }
}
