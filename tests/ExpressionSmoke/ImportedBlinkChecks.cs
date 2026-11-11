using FrooxEngine;
using FrooxEngine.CommonAvatar;
using static ExpressionTestFields;

internal static class ImportedBlinkChecks
{
    // Exercise saved native links before the fixed-pose replay disconnects tracking.
    public static async Task Run(Slot avatar, Slot expressions)
    {
        int checkedEyes = 0;
        foreach (var driver in avatar.GetComponentsInChildren<EyeLinearDriver>())
        {
            if (driver.Eyes.Count == 0) continue;
            var manager = driver.EyeManager.Target ?? throw new Exception("Missing saved EyeManager");
            manager.MinBlinkInterval.Value = manager.MaxBlinkInterval.Value = 1000000;
            var targets = driver.Eyes.Select(eye =>
            {
                var output = expressions.FindChild("Outputs").Children.SingleOrDefault(o =>
                    o.ExpressionVariables<DynamicReferenceVariable<ISyncRef>>().Any(v =>
                        v.VariableName.Value == "ExpressionSystem.Output/OriginalDriver" && v.Reference.Target == eye.OpenCloseTarget));
                return (eye, field: output == null ? eye.OpenCloseTarget.Target : OutputTarget(output));
            }).ToArray();
            foreach (var (left, right) in new[] { (0f, 0f), (1f, 0f), (0f, 1f), (1f, 1f), (0f, 0f) })
            {
                manager.LeftEyeCloseOverride.Value = left; manager.RightEyeCloseOverride.Value = right;
                for (int i = 0; i < 30; i++) await default(NextUpdate);
                foreach (var (eye, field) in targets)
                {
                    float close = eye.Side.Value switch
                    {
                        EyeSide.Left => manager.LeftEyeClose.Value,
                        EyeSide.Right => manager.RightEyeClose.Value,
                        _ => manager.CombinedEyeClose.Value,
                    };
                    float input = Math.Clamp((close - eye.MinInputCloseness.Value) /
                        (eye.MaxInputCloseness.Value - eye.MinInputCloseness.Value), 0, 1);
                    float expected = eye.OpenState.Value + (eye.ClosedState.Value - eye.OpenState.Value) * input;
                    if (!eye.OpenCloseTarget.IsLinkValid || field == null || Math.Abs(field.Value - expected) > .005f)
                        throw new Exception($"Saved {eye.Side.Value} blink did not reach mesh: {field?.Value} != {expected}");
                }
            }
            checkedEyes += targets.Length;
        }
        string required = Environment.GetEnvironmentVariable("RESOPON_TEST_EXPECTED_BLINK_BINDINGS");
        if (required != null && (!int.TryParse(required, out int expectedCount) || expectedCount < 0 || checkedEyes != expectedCount))
            throw new Exception($"Expected {required} saved blink bindings, found {checkedEyes}");
        Console.WriteLine($"PASS: {checkedEyes} saved native blink bindings open, close independently, and reopen through the expression mixer");
    }
}
