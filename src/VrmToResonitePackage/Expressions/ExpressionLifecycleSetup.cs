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
        // Only the looped output records below need runtime Source reads.
        var g = new ExpressionFlux(_lifecycle);
        var core = g.Ref(_core);
        // Local, unserialized state: no User reference survives cloning or loading.
        var initialized = g.Node("StoredValue", typeof(bool));
        var cleanup = new List<IWorldElement>();
        foreach (string hand in new[] { "Left", "Right" })
        {
            cleanup.Add(g.Write<int>(core, SystemSpace, "Core." + hand + "Gesture", g.Constant(0)));
        }
        cleanup.Add(g.Write<bool>(core, SystemSpace, "Core.AllowHandGestures", g.Constant(true)));
        cleanup.Add(g.Write<Slot>(core, SystemSpace, "Core.CurrentExpression", g.Ref<Slot>(null)));
        cleanup.Add(g.Write<string>(core, SystemSpace, "Core.PairKey", g.Text("L0R0")));
        cleanup.Add(g.Each(g.Ref(_outputs), output => g.Sequence(
            g.Write<Slot>(output, OutputSpace, "Binding", g.Ref<Slot>(null)),
            g.If(g.IsNull<ISyncRef>(g.Read<ISyncRef>(output, OutputSpace, "OriginalDriver")),
                g.Write<float>(output, OutputSpace, "Result", g.Read<float>(output, OutputSpace, "Base"))))));

        var clear = g.Sequence(cleanup.ToArray());
        var reset = g.Sequence(clear, g.Set<bool>(initialized, g.Constant(true)));
        var initialize = g.If(g.Not(initialized), reset);
        // Explicit reset also works after initialization and does not select the neutral table entry.
        ReceiveUpdate(g, ResetStateTag, reset);
        // API events may arrive before the wearer-change event; initialize synchronously.
        ReceiveUpdate(g, InitializeTag, initialize);
        // Only the current local wearer runs selection/playback. On departure, the client
        // that initialized this instance clears it once, provided nobody else is wearing it.
        // Observers never initialize and therefore never write this cleanup state.
        var stop = g.If(initialized, g.Sequence(
            g.If(g.Not(g.AvatarWorn), clear),
            g.Set<bool>(initialized, g.Constant(false))));
        // The local drive can report departure before the shared AvatarWorn field updates.
        // Allow two updates for the drive chain and variable notifications to settle.
        // Recheck before discarding initialization or clearing shared state.
        var delayedStop = g.Node("StartAsyncTask", null, ("TaskStart",
            g.Node("DelayUpdates", null, ("Updates", g.Constant(2)),
                ("Next", g.If(g.Not(g.AvatarWornLocal), stop)))));
        var update = g.If(g.AvatarWornLocal, initialize, g.If(initialized, delayedStop));
        g.OnChanged<bool>(g.AvatarWornLocal, update);
        g.OnChanged<bool>(g.AvatarWorn, update);
        g.OnStart(update);
    }
}
