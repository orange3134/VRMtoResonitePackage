using FrooxEngine;

// Keep the IWorldElement generic constraint off the top-level Program type.
// Program must load before Main can register the external Resonite DLL resolver.
internal static class ExpressionTestFields
{
    // Discover the saved record name so snapshots can also read legacy Expr packages.
    public static string VariablePath(Slot slot, string name)
    {
        var space = slot.GetComponentInParents<DynamicVariableSpace>();
        if (space.SpaceName.Value == "ExpressionSystem")
        {
            for (var ancestor = slot; ancestor != space.Slot; ancestor = ancestor.Parent)
                if (ancestor.Parent == space.Slot && ancestor.Name is "Core" or "GestureTable")
                    return "ExpressionSystem/" + ancestor.Name + "." + name;
        }
        return space.SpaceName.Value + "/" + name;
    }
    public static T Reference<T>(Slot slot, string name) where T : class, IWorldElement =>
        slot.GetComponents<DynamicReferenceVariable<T>>()
            .Single(v => v.VariableName.Value == ExpressionTestFields.VariablePath(slot, name)).Reference.Target;
}
