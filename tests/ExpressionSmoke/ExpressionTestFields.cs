using FrooxEngine;

// Keep the IWorldElement generic constraint off the top-level Program type.
// Program must load before Main can register the external Resonite DLL resolver.
internal static class ExpressionTestFields
{
    public static bool HandGesturesAllowed(Slot slot, string hand) => slot.ExpressionVariables<DynamicValueVariable<bool>>()
        .Single(v => v.VariableName.Value == VariablePath(slot, "AllowHandGestures." + hand)).Value.Value;
    public static bool BothHandsAllowed(Slot slot) => HandGesturesAllowed(slot, "Left") && HandGesturesAllowed(slot, "Right");
    public static bool NoHandsAllowed(Slot slot) => !HandGesturesAllowed(slot, "Left") && !HandGesturesAllowed(slot, "Right");

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

    // Inspect actual output wiring; legacy packages also expose a Target reference.
    public static IField<float> OutputTarget(Slot output)
    {
        var legacy = output.ExpressionVariables<DynamicReferenceVariable<IField<float>>>()
            .SingleOrDefault(v => v.VariableName.Value == VariablePath(output, "Target"));
        if (legacy != null) return legacy.Reference.Target;
        var result = output.ExpressionVariables<DynamicField<float>>()
            .Single(v => v.VariableName.Value == VariablePath(output, "Result")).TargetField.Target;
        if (result.Parent is not SmoothValue<float> smooth) return result;
        return ((DynamicBlendShapeDriver.BlendShape)smooth.Value.Target.Parent)._drive.Target;
    }

    public static DynamicValueVariable<float> BindingVariable(Slot bindings, Slot output)
    {
        string id = output.ExpressionVariables<DynamicValueVariable<string>>()
            .Single(v => v.VariableName.Value == VariablePath(output, "Id")).Value.Value;
        return bindings.ExpressionVariables<DynamicValueVariable<float>>()
            .Single(v => v.VariableName.Value == VariablePath(bindings, id));
    }

    public static bool TryReadSelectedValue(Slot output, out float value)
    {
        value = 0;
        var current = Reference<Slot>(output.Parent.Parent.FindChild("Internal"), "CurrentExpression");
        if (current == null) return false;
        string id = output.ExpressionVariables<DynamicValueVariable<string>>()
            .Single(v => v.VariableName.Value == VariablePath(output, "Id")).Value.Value;
        return current.GetComponent<DynamicVariableSpace>().TryReadValue(id, out value);
    }

    public static float SelectedValue(Slot output) => TryReadSelectedValue(output, out float value) ? value :
        output.ExpressionVariables<DynamicValueVariable<float>>()
            .Single(v => v.VariableName.Value == VariablePath(output, "Base")).Value.Value;
    public static T Reference<T>(Slot slot, string name) where T : class, IWorldElement =>
        slot.ExpressionVariables<DynamicReferenceVariable<T>>()
            .Single(v => v.VariableName.Value == ExpressionTestFields.VariablePath(slot, name)).Reference.Target;
}
