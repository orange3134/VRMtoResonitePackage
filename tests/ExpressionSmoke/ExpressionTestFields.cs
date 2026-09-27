using FrooxEngine;

// Keep the IWorldElement generic constraint off the top-level Program type.
// Program must load before Main can register the external Resonite DLL resolver.
internal static class ExpressionTestFields
{
    // Parenting below UserRoot alone is not equip. Exercise the identification's real equip hook.
    public static void EquipAvatar(Slot avatar)
    {
        var equipSlot = avatar.GetComponent<FrooxEngine.CommonAvatar.AvatarObjectSlot>()
            ?? avatar.AttachComponent<FrooxEngine.CommonAvatar.AvatarObjectSlot>();
        avatar.FindChild("Avatar Root Identification")
            .GetComponent<FrooxEngine.CommonAvatar.AvatarUserReferenceAssigner>().OnEquip(equipSlot);
    }

    // Discover the saved record name so snapshots can also read legacy Expr packages.
    public static string VariablePath(Slot slot, string name)
    {
        var space = slot.GetComponentInParents<DynamicVariableSpace>();
        if (space.SpaceName.Value == "ExpressionSystem")
        {
            for (var ancestor = slot; ancestor != space.Slot; ancestor = ancestor.Parent)
                if ((ancestor.Parent == space.Slot && ancestor.Name is "Core" or "GestureTable") ||
                    (ancestor.Parent == space.Slot.FindChild("DV") && ancestor.Name == "GestureTable"))
                    return "ExpressionSystem/" + ancestor.Name + "." + name;
        }
        return space.SpaceName.Value + "/" + name;
    }
    // Read current DV containers and older packages with variables on the record itself.
    public static List<T> ExpressionVariables<T>(this Slot context) where T : Component
    {
        var space = context.GetComponentInParents<DynamicVariableSpace>();
        var data = space.Slot.FindChild("DV");
        var variables = data != null ? data.GetComponentsInChildren<T>() : context.GetComponentsInChildren<T>();
        string prefix = VariablePath(context, "");
        return variables.Where(variable =>
            ((IField<string>)variable.GetType().GetField("VariableName").GetValue(variable))
                .Value.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    public static T Reference<T>(Slot slot, string name) where T : class, IWorldElement =>
        slot.ExpressionVariables<DynamicReferenceVariable<T>>()
            .Single(v => v.VariableName.Value == ExpressionTestFields.VariablePath(slot, name)).Reference.Target;
}
