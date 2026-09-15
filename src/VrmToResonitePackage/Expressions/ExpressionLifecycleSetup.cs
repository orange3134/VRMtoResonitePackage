using FrooxEngine;
using static VrmToResonitePackage.Expressions.ExpressionFlux;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    // The previous owner gets one final reset/update when the avatar changes users.
    private IWorldElement CanUpdate(ExpressionFlux g) => g.Or(g.IsOwner(_root),
        g.Node("IsLocalUser", null, ("User", g.Read<User>(g.Ref(_core), "PreviousOwner"))));

    private void ReceiveUpdate(ExpressionFlux g, string tag, IWorldElement action)
    {
        var receiver = g.Receiver(tag, false);
        Link(receiver, "OnTriggered", g.If(CanUpdate(g), action));
    }

    private void BuildLifecycle()
    {
        var g = new ExpressionFlux(_lifecycle);
        var core = g.Ref(_core);
        // StoredValue is local and not serialized, so clone/load starts with fresh hand inputs.
        var initialized = g.Node("StoredValue", typeof(bool));
        var cleanup = new List<IWorldElement>();
        foreach (string hand in new[] { "Left", "Right" })
        {
            cleanup.Add(g.Write<int>(core, hand + "Gesture", g.Constant(0)));
            cleanup.Add(g.Write<Slot>(core, hand + "Input", g.Ref<Slot>(null)));
        }
        cleanup.Add(g.Write<Slot>(core, "Override", g.Ref<Slot>(null)));
        cleanup.Add(g.Write<Slot>(core, "CurrentExpression", g.Ref<Slot>(null)));
        cleanup.Add(g.Each(g.Ref(_outputs), output => g.Sequence(
            g.Write<float>(output, "Result", g.Read<float>(output, "Base")),
            g.Write<float>(output, "Snapshot", g.Read<float>(output, "Base")))));
        cleanup.Add(g.Each(g.Ref(_inputs.FindChild("Keyboard").FindChild("Bindings")),
            shortcut => g.Write<bool>(shortcut, "Held", g.Constant(false))));
        cleanup.Add(g.Set<bool>(initialized, g.Constant(true)));
        var reset = g.If(g.Or(g.Not(initialized), g.Not(g.Equal<User>(g.Owner(_root), g.Read<User>(core, "PreviousOwner")))),
            g.Sequence(cleanup.ToArray()));
        var update = g.Node("LocalUpdate");
        // DynamicImpulseTrigger is synchronous. Keep reset -> selection/snapshot -> playback order,
        // while each target board owns all of its nodes and can be unpacked independently.
        // Preserve PreviousOwner until both guarded stages finish, including the old owner's
        // final update. Each stage also rejects direct impulses from an unrelated client.
        Link(update, "OnUpdate", g.If(CanUpdate(g), g.Sequence(reset,
            g.Trigger(g.Ref(_selection), SelectionTickTag), g.Trigger(g.Ref(_playback), PlaybackTickTag),
            g.Write<User>(core, "PreviousOwner", g.Owner(_root)))));
    }
}
