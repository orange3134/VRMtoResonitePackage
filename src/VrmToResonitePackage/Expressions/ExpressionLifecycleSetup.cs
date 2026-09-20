using FrooxEngine;
using static VrmToResonitePackage.Expressions.ExpressionFlux;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private void ReceiveUpdate(ExpressionFlux g, string tag, IWorldElement action)
    {
        var receiver = g.Receiver(tag, false);
        Link(receiver, "OnTriggered", g.If(g.IsOwner(_root), action));
    }

    private void BuildLifecycle()
    {
        // Core state must remain live: cached Dynamic Inputs regress clone/reset playback.
        var g = new ExpressionFlux(_lifecycle, useDynamicInputs: false);
        var core = g.Ref(_core);
        // Local, unserialized state: no User reference survives cloning or loading.
        var initialized = g.Node("StoredValue", typeof(bool));
        var cleanup = new List<IWorldElement>();
        foreach (string hand in new[] { "Left", "Right" })
        {
            cleanup.Add(g.Write<int>(core, hand + "Gesture", g.Constant(0)));
            cleanup.Add(g.Write<int>(core, hand + "Revision", g.Constant(0)));
        }
        cleanup.Add(g.Write<bool>(core, "AllowExternalInput", g.Constant(true)));
        cleanup.Add(g.Write<Slot>(core, "CurrentExpression", g.Ref<Slot>(null)));
        cleanup.Add(g.Write<int>(core, "PairIndex", g.Constant(0)));
        cleanup.Add(g.Write<Slot>(core, "MappedExpression", g.Ref<Slot>(null)));
        cleanup.Add(g.Write<Slot>(core, "CandidateExpression", g.Ref<Slot>(null)));
        cleanup.Add(g.Write<int>(core, "SelectionStatus", g.Constant(0)));
        cleanup.Add(g.Each(g.Ref(_outputs), output => g.Sequence(
            g.Write<float>(output, "Result", g.Read<float>(output, "Base")),
            g.Write<float>(output, "Snapshot", g.Read<float>(output, "Base")))));
        cleanup.Add(g.Each(g.Ref(_inputs.FindChild("Keyboard").FindChild("Bindings")),
            shortcut => g.Write<bool>(shortcut, "Held", g.Constant(false))));
        var clear = g.Sequence(cleanup.ToArray());
        var initialize = g.If(g.Not(initialized), g.Sequence(clear, g.Set<bool>(initialized, g.Constant(true))));
        // API events may arrive before LocalUpdate; initialize once before accepting either hand.
        ReceiveUpdate(g, InitializeTag, initialize);
        var update = g.Node("LocalUpdate");
        // Only the current local wearer runs selection/playback. On departure, the client
        // that initialized this instance clears it once, provided nobody else is wearing it.
        // Observers never initialize and therefore never write this cleanup state.
        var stop = g.If(initialized, g.Sequence(
            g.If(g.Equal<User>(g.Owner(_root), g.Ref<User>(null)), clear),
            g.Set<bool>(initialized, g.Constant(false))));
        Link(update, "OnUpdate", g.If(g.IsOwner(_root), g.Sequence(initialize,
            g.Trigger(g.Ref(_selection), SelectionTickTag), g.Trigger(g.Ref(_playback), PlaybackTickTag)), stop));
    }
}
