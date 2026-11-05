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

    private IWorldElement[] ResetStateActions(ExpressionFlux g)
    {
        var core = g.Ref(_api);
        var cleanup = new List<IWorldElement>();
        foreach (string hand in new[] { "Left", "Right" })
        {
            cleanup.Add(g.Write<int>(core, SystemSpace, hand + "Gesture", g.Constant(0)));
        }
        cleanup.AddRange(HandGesturePermissionWrites(g, g.Constant(true)));
        cleanup.Add(g.Trigger<Slot>(g.Ref(_api), g.Text(PlaybackTickTag), g.Ref<Slot>(null)));
        return cleanup.ToArray();
    }

    private void BuildResetState()
    {
        var g = new ExpressionFlux(_resetState);
        // Explicit reset remains wearer-only and does not select the neutral table entry.
        ReceiveUpdate(g, ResetStateTag, g.Sequence(ResetStateActions(g)));
    }

    private void BuildLifecycle()
    {
        var g = new ExpressionFlux(_events.AddSlot("OnAvatarWorn"));
        // Local, unserialized state: no User reference survives cloning or loading.
        var initialized = g.Node("StoredValue", typeof(bool));
        var start = g.Sequence(g.Set<bool>(initialized, g.Constant(true)),
            g.Trigger(g.Ref(_api), ResetStateTag));
        // Only the current local wearer runs selection/playback. On departure, the client
        // that initialized this instance clears it once, provided nobody else is wearing it.
        // Observers never initialize and therefore never write this cleanup state.
        var stop = g.Sequence(
            // Departure cannot use the wearer-only ResetState receiver. Build its
            // cleanup locally, without connecting Flux groups across the two boards.
            g.If(g.Not(g.AvatarWorn), g.Sequence(ResetStateActions(g))),
            g.Set<bool>(initialized, g.Constant(false)));
        var shouldStop = g.And(g.Not(g.AvatarWornLocal), initialized);
        // The local drive can report departure before the shared AvatarWorn field updates.
        // Allow two updates for the drive chain and variable notifications to settle.
        // Recheck before discarding initialization or clearing shared state.
        var delayedStop = g.Node("StartAsyncTask", null, ("TaskStart",
            g.Node("DelayUpdates", null, ("Updates", g.Constant(2)),
                ("Next", g.If(shouldStop, stop)))));
        var update = g.If(g.And(g.AvatarWornLocal, g.Not(initialized)), start,
            g.If(shouldStop, delayedStop));
        g.OnChanged<bool>(g.AvatarWornLocal, update);
        g.OnChanged<bool>(g.AvatarWorn, update);
        g.OnStart(update);
    }
}
