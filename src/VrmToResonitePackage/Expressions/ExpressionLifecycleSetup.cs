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

    private void BuildResetState()
    {
        var g = new ExpressionFlux(_resetState);
        var core = g.Ref(_api);
        var cleanup = new List<IWorldElement>();
        cleanup.Add(g.Write<bool>(core, SystemSpace, ContextMenuSelected, g.Constant(false)));
        foreach (string hand in new[] { "Left", "Right" })
        {
            cleanup.Add(g.Write<int>(core, SystemSpace, "State." + hand + "Gesture", g.Constant(0)));
        }
        cleanup.Add(g.Trigger<Slot>(g.Ref(_api), g.Text(PlaybackTickTag), g.Ref<Slot>(null)));
        // Reset is shared by explicit requests and wear initialization.
        // It preserves hand permissions, does not select the neutral table entry,
        // and does not depend on local wear state.
        var receiver = g.Receiver(ResetStateTag, false);
        Link(receiver, "OnTriggered", g.Sequence(cleanup.ToArray()));
    }

    private void BuildLifecycle()
    {
        var g = new ExpressionFlux(_events.AddSlot("OnAvatarWorn"));
        // Local, unserialized state: no User reference survives cloning or loading.
        var initialized = g.Node("StoredValue", typeof(bool));
        var start = g.Sequence(g.Set<bool>(initialized, g.Constant(true)),
            g.Trigger(g.Ref(_api), ResetStateTag));
        // Losing local wear only rearms initialization for the next wear.
        // Shared state is retained; the next wear resets gestures and selection only.
        var update = g.If(g.AvatarWornLocal, g.If(g.Not(initialized), start),
            g.Set<bool>(initialized, g.Constant(false)));
        g.OnChanged<bool>(g.AvatarWornLocal, update);
        g.OnStart(update);
    }
}
