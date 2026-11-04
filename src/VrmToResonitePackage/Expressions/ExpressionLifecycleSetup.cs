using FrooxEngine;
using static VrmToResonitePackage.Expressions.ExpressionFlux;
using static VrmToResonitePackage.Expressions.ExpressionSpaces;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private void ReceiveUpdate(ExpressionFlux g, string tag, IWorldElement action)
    {
        var receiver = g.Receiver(tag, false);
        Link(receiver, "OnTriggered", g.If(g.AvatarWornLocal, action));
    }

    private void BuildLifecycle()
    {
        // Clearing the selected expression invokes the shared playback operation.
        var g = new ExpressionFlux(_lifecycle);
        var core = g.Ref(_api);
        // Local, unserialized state: no User reference survives cloning or loading.
        var initialized = g.Node("StoredValue", typeof(bool));
        var cleanup = new List<IWorldElement>();
        foreach (string hand in new[] { "Left", "Right" })
        {
            cleanup.Add(g.Write<int>(core, SystemSpace, hand + "Gesture", g.Constant(0)));
        }
        cleanup.Add(WriteHandGesturePermissions(g, g.Constant(true)));
        cleanup.Add(g.Trigger<Slot>(g.Ref(_api), g.Text(PlaybackTickTag), g.Ref<Slot>(null)));

        var clear = g.Sequence(cleanup.ToArray());
        var reset = g.Sequence(clear, g.Set<bool>(initialized, g.Constant(true)));
        // Explicit reset also works after initialization and does not select the neutral table entry.
        ReceiveUpdate(g, ResetStateTag, reset);
        // Only the current local wearer runs selection/playback. On departure, the client
        // that initialized this instance clears it once, provided nobody else is wearing it.
        // Observers never initialize and therefore never write this cleanup state.
        var stop = g.Sequence(
            g.If(g.Not(g.AvatarWorn), clear),
            g.Set<bool>(initialized, g.Constant(false)));
        var shouldStop = g.And(g.Not(g.AvatarWornLocal), initialized);
        // The local drive can report departure before the shared AvatarWorn field updates.
        // Allow two updates for the drive chain and variable notifications to settle.
        // Recheck before discarding initialization or clearing shared state.
        var delayedStop = g.Node("StartAsyncTask", null, ("TaskStart",
            g.Node("DelayUpdates", null, ("Updates", g.Constant(2)),
                ("Next", g.If(shouldStop, stop)))));
        var update = g.If(g.And(g.AvatarWornLocal, g.Not(initialized)), reset,
            g.If(shouldStop, delayedStop));
        g.OnChanged<bool>(g.AvatarWornLocal, update);
        g.OnChanged<bool>(g.AvatarWorn, update);
        g.OnStart(update);
    }
}
