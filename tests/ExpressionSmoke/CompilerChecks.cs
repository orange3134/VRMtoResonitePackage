using VrmToResonitePackage.Expressions;

internal static class CompilerChecks
{
    public static void Run()
    {
        var model = new ExpressionModel();
        ExpressionClip Clip(string id, string shape, float value)
        {
            var clip = new ExpressionClip { Id = id, Name = id, Duration = 1 };
            var curve = new ExpressionCurve { Binding = new("Face", shape) };
            curve.Keys.Add(new(0, value, 0, 0)); clip.Curves.Add(curve); model.Clips.Add(clip); return clip;
        }
        ExpressionLayer Layer(string hand, string id, float weight = 1)
        {
            var layer = new ExpressionLayer { Id = hand, Name = hand, Weight = weight };
            layer.States.Add(new("Neutral", null, 1, true)); layer.States.Add(new(id, id, 1, true));
            var on = new ExpressionTransition { Destination = 1 }; on.Conditions.Add(new(hand, 6, 1));
            var off = new ExpressionTransition { Destination = 0 }; off.Conditions.Add(new(hand, 7, 1));
            layer.Transitions.Add(on); layer.Transitions.Add(off); model.Layers.Add(layer); return layer;
        }
        Clip("Left", "Smile", 1); Clip("Right", "Smile", 0.6f);
        var left = Layer("GestureLeft", "Left"); var right = Layer("GestureRight", "Right", 0.5f);
        var table = new GesturePairCompiler(model, model.Clips, _ => 0.2f);
        ExpressionClip Result(string id) => model.Clips.Concat(table.Generated).Single(c => c.Id == id);
        for (int l = 0; l < 8; l++)
            for (int r = 0; r < 8; r++)
            {
                float expected = r == 1 ? ((l == 1 ? 1 : 0.2f) + 0.6f) * 0.5f : l == 1 ? 1 : 0.2f;
                Check(Math.Abs(Result(table.Pairs[l * 8 + r]).Curves.Single().Sample(0) - expected) < 0.0001f,
                    "layer order, weight and Write Defaults for pair " + l + "," + r);
            }
        Check(table.Pairs.Distinct().Count() <= 4, "64 cells share equal catalog poses");
        left.Transitions.RemoveAt(1);
        var history = new GesturePairCompiler(model, model.Clips, _ => 0.2f);
        Check(history.Pairs[0] == null && history.Pairs[8] != null, "history-dependent cells are unassigned, deterministic cells retained");
        left.Transitions.Add(new ExpressionTransition { Source = 1, Destination = 0, HasExitTime = true });
        var timed = new GesturePairCompiler(model, model.Clips, _ => 0.2f);
        Check(timed.Pairs[0] == timed.Pairs[8] && model.Diagnostics.Any(d => d.Contains("exit time")), "exit-time layer is diagnosed and excluded");

        var menuClip = Clip("MenuPose", "Smile", 0.9f);
        Layer("FaceMenu", "MenuPose");
        var control = new ExpressionMenuControl { Name = "Face", Parameter = "FaceMenu", Value = 1, Type = 1 };
        model.Menu.Add(control);
        var menu = new GesturePairCompiler(model, model.Clips, _ => 0.2f);
        Check(menu.Menu[control] == menuClip.Id, "independent ExpressionMenu pose maps to direct override");

        // Tangents and positive fixed state speed survive compilation without frame sampling.
        model.Layers.Clear(); var animated = Clip("Animated", "Smile", 0);
        animated.Curves[0].Keys.Clear(); animated.Curves[0].Keys.Add(new(0, 0, 0, 4)); animated.Curves[0].Keys.Add(new(1, 0, -4, 0));
        var animationLayer = Layer("GestureLeft", "Animated");
        animationLayer.States[1] = animationLayer.States[1] with { Speed = 2 };
        var animation = new GesturePairCompiler(model, model.Clips, _ => 0.2f);
        var generated = animation.Generated.Single(c => c.Id == animation.Pairs[8]);
        Check(generated.Duration == 0.5f && Math.Abs(generated.Curves[0].Sample(0.25f) - 1) < 0.0001f,
            "Hermite curve and playback speed survive table compilation");
        // Opposite infinity signs both mean hold; their sum must not introduce NaN tangents.
        var stepped = Clip("Stepped", "Smile", 0);
        animated.Curves[0].Keys.Clear(); animated.Curves[0].Keys.Add(new(0, 0, 0, float.PositiveInfinity));
        animated.Curves[0].Keys.Add(new(1, 1, 0, 0));
        stepped.Curves[0].Keys.Clear(); stepped.Curves[0].Keys.Add(new(0, 1, 0, float.NegativeInfinity));
        stepped.Curves[0].Keys.Add(new(1, 0, 0, 0));
        animationLayer.States[1] = animationLayer.States[1] with { Speed = 1 };
        Layer("GestureRight", "Stepped", 0.5f);
        var hold = new GesturePairCompiler(model, model.Clips, _ => 0.2f);
        var combined = model.Clips.Concat(hold.Generated).Single(c => c.Id == hold.Pairs[9]);
        Check(Math.Abs(combined.Curves[0].Sample(0.5f) - 0.5f) < 0.0001f &&
            combined.Curves[0].Keys.All(k => !float.IsNaN(k.InSlope) && !float.IsNaN(k.OutSlope)), "stepped curve composition has no NaN tangents");
        model.Layers.Clear(); model.Menu.Clear();
        model.Parameters["FacialSet"] = new("FacialSet", 3, 0);
        model.Menu.Add(new() { Name = "Other bank", Parameter = "FacialSet", Value = 1, Type = 1 });
        left = Layer("GestureLeft", "Animated"); right = Layer("GestureRight", "Right");
        left.States[1] = left.States[1] with { TimeParameter = "GestureLeftWeight" };
        foreach (var layer in model.Layers)
        {
            layer.States.Add(new("Alternate bank", "MenuPose", 1, true));
            layer.Transitions[0].Conditions.Add(new("FacialSet", 6, 0));
            var alternate = new ExpressionTransition { Destination = 2 };
            alternate.Conditions.Add(new(layer.Id, 6, 1)); alternate.Conditions.Add(new("FacialSet", 6, 1));
            layer.Transitions.Add(alternate);
        }
        var banks = new GesturePairCompiler(model, model.Clips, _ => 0.2f);
        float Pose(GesturePairCompiler c, int index) => model.Clips.Concat(c.Generated).Single(p => p.Id == c.Pairs[index]).Curves[0].Sample(0);
        Check(banks.Pairs.All(p => p != null) && Pose(banks, 8) == 1 && Pose(banks, 1) == 0.6f && Pose(banks, 9) == 0.6f,
            "declared menu default specializes both gesture layers; idle upper hand preserves lower hand; active upper hand wins");
        model.Parameters["FacialSet"] = new("FacialSet", 3, 1);
        var bank1 = new GesturePairCompiler(model, model.Clips, _ => 0.2f);
        Check(Pose(bank1, 8) == 0.9f, "use authored default rather than hardcoding bank zero");
        model.Menu.Clear();
        var unknown = new GesturePairCompiler(model, model.Clips, _ => 0.2f);
        Check(unknown.Pairs.All(p => p == null), "non-menu parameters are not silently frozen");
        // Unity 2022.3: an upper WD-on state with a different binding also preserves the lower stream.
        model.Layers.Clear(); var other = Clip("Other", "OtherShape", 0.8f);
        left = Layer("GestureLeft", "Left"); right = Layer("GestureRight", "Right");
        right.States[0] = new("Other property", other.Id, 1, true);
        var sparse = new GesturePairCompiler(model, model.Clips, _ => 0.2f);
        var sparsePose = model.Clips.Concat(sparse.Generated).Single(c => c.Id == sparse.Pairs[8]);
        Check(sparsePose.Curves.Single(c => c.Binding.Shape == "Smile").Sample(0) == 1,
            "unanimated property in non-empty upper state preserves lower stream");
        Console.WriteLine("Gesture pair compiler checks passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
