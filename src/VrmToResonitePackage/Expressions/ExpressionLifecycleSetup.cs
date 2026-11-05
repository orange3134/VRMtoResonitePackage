using FrooxEngine;
using static VrmToResonitePackage.Expressions.ExpressionFlux;
using static VrmToResonitePackage.Expressions.ExpressionSpaces;

namespace VrmToResonitePackage.Expressions;

internal sealed partial class ExpressionSystemSetup
{
    private const string AvatarWornTag = "ExpressionSystem/Event.OnAvatarWorn";
    private const string AvatarRemovedTag = "ExpressionSystem/Event.OnAvatarRemoved";

    private void ReceiveUpdate(ExpressionFlux g, string tag, IWorldElement action)
    {
        var receiver = g.Receiver(tag, false);
        Link(receiver, "OnTriggered", g.If(g.AvatarWornLocal, action));
    }

    private void BuildResetState()
    {
        var g = new ExpressionFlux(_resetState);
        var core = g.Ref(_api);
        var cleanup = new List<IWorldElement>();
        foreach (string hand in new[] { "Left", "Right" })
        {
            cleanup.Add(g.Write<int>(core, SystemSpace, hand + "Gesture", g.Constant(0)));
        }
        cleanup.AddRange(HandGesturePermissionWrites(g, g.Constant(true)));
        cleanup.Add(g.Trigger<Slot>(g.Ref(_api), g.Text(PlaybackTickTag), g.Ref<Slot>(null)));
        // Reset is shared by explicit requests, wear initialization and departure.
        // It does not select the neutral table entry or depend on local wear state.
        var receiver = g.Receiver(ResetStateTag, false);
        Link(receiver, "OnTriggered", g.Sequence(cleanup.ToArray()));
    }

    private void BuildLifecycle()
    {
        BuildAvatarWornEvent();
        BuildAvatarRemovedEvent();
    }

    private void BuildAvatarWornEvent()
    {
        var g = new ExpressionFlux(_events.AddSlot("OnAvatarWorn"));
        // Local, unserialized state: no User reference survives cloning or loading.
        var initialized = g.Node("StoredValue", typeof(bool));
        var start = g.Sequence(g.Set<bool>(initialized, g.Constant(true)),
            g.Trigger(g.Ref(_api), AvatarWornTag),
            g.Trigger(g.Ref(_api), ResetStateTag));
        var removed = g.Receiver(AvatarRemovedTag, false);
        Link(removed, "OnTriggered", g.If(g.Not(g.AvatarWornLocal),
            g.Set<bool>(initialized, g.Constant(false))));
        var update = g.If(g.And(g.AvatarWornLocal, g.Not(initialized)), start);
        g.OnChanged<bool>(g.AvatarWornLocal, update);
        g.OnChanged<bool>(g.AvatarWorn, update);
        g.OnStart(update);
    }

    private void BuildAvatarRemovedEvent()
    {
        var g = new ExpressionFlux(_events.AddSlot("OnAvatarRemoved"));
        // Coordinate through impulses, so each event retains its own Flux group.
        var armed = g.Node("StoredValue", typeof(bool));
        var worn = g.Receiver(AvatarWornTag, false);
        Link(worn, "OnTriggered", g.If(g.AvatarWornLocal,
            g.Set<bool>(armed, g.Constant(true))));
        // Only the current local wearer runs selection/playback. On departure, the client
        // that initialized this instance clears it once, provided nobody else is wearing it.
        // Observers never initialize and therefore never write this cleanup state.
        var stop = g.Sequence(
            // Preserve the handoff guard while sharing reset through an impulse.
            g.If(g.Not(g.AvatarWorn), g.Trigger(g.Ref(_api), ResetStateTag)),
            g.Set<bool>(armed, g.Constant(false)),
            g.Trigger(g.Ref(_api), AvatarRemovedTag));
        var shouldStop = g.And(g.Not(g.AvatarWornLocal), armed);
        // The local drive can report departure before the shared AvatarWorn field updates.
        // Allow two updates for the drive chain and variable notifications to settle.
        // Recheck before discarding initialization or clearing shared state.
        var delayedStop = g.Node("StartAsyncTask", null, ("TaskStart",
            g.Node("DelayUpdates", null, ("Updates", g.Constant(2)),
                ("Next", g.If(shouldStop, stop)))));
        var update = g.If(shouldStop, delayedStop);
        g.OnChanged<bool>(g.AvatarWornLocal, update);
        g.OnChanged<bool>(g.AvatarWorn, update);
    }
}
