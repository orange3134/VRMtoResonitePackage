using FrooxEngine;
using static VrmToResonitePackage.Expressions.ExpressionFlux;
using static VrmToResonitePackage.Expressions.ExpressionSpaces;

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
        // Only the looped output records below need runtime Source reads.
        var g = new ExpressionFlux(_lifecycle);
        var core = g.Ref(_core);
        // Local, unserialized state: no User reference survives cloning or loading.
        var initialized = g.Node("StoredValue", typeof(bool));
        var cleanup = new List<IWorldElement>();
        foreach (string hand in new[] { "Left", "Right" })
        {
            cleanup.Add(g.Write<int>(core, CoreSpace, hand + "Gesture", g.Constant(0)));
        }
        cleanup.Add(g.Write<bool>(core, CoreSpace, "AllowExternalInput", g.Constant(true)));
        cleanup.Add(g.Write<Slot>(core, CoreSpace, "CurrentExpression", g.Ref<Slot>(null)));
        cleanup.Add(g.Write<int>(core, CoreSpace, "PairIndex", g.Constant(0)));
        cleanup.Add(g.Each(g.Ref(_outputs), output => g.Sequence(
            g.Write<float>(output, OutputSpace, "Result", g.Read<float>(output, OutputSpace, "Base")),
            g.Write<float>(output, OutputSpace, "Snapshot", g.Read<float>(output, OutputSpace, "Base")))));

        var clear = g.Sequence(cleanup.ToArray());
        var initialize = g.If(g.Not(initialized), g.Sequence(clear, g.Set<bool>(initialized, g.Constant(true))));
        // API events may arrive before the wearer-change event; initialize synchronously.
        ReceiveUpdate(g, InitializeTag, initialize);
        // Only the current local wearer runs selection/playback. On departure, the client
        // that initialized this instance clears it once, provided nobody else is wearing it.
        // Observers never initialize and therefore never write this cleanup state.
        var stop = g.If(initialized, g.Sequence(
            g.If(g.IsNull<User>(g.Owner(_root)), clear),
            g.Set<bool>(initialized, g.Constant(false))));
        var update = g.If(g.IsOwner(_root), g.Sequence(initialize,
            g.Trigger(g.Ref(_selection), SelectionTickTag), g.Trigger(g.Ref(_playback), PlaybackTickTag),
            _menuAvailability == null ? null : g.Trigger(g.Ref(_menuAvailability), MenuRefreshTag)), stop);
        g.OnChanged<bool>(g.IsOwner(_root), update);
        g.OnStart(update);
    }
}
