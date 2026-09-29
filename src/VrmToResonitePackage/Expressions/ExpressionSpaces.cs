namespace VrmToResonitePackage.Expressions;

// Singleton modules share SystemSpace with unprefixed state names and grouped keys (References.*, GestureTable.*).
// Repeated records use hierarchical space names shared by instances of the same schema.
internal static class ExpressionSpaces
{
    public const string SystemSpace = "ExpressionSystem";
    public const string ClipSpace = "ExpressionSystem.Catalog.Clip";
    public const string OutputSpace = "ExpressionSystem.Output";
    public const string KeyboardSpace = "ExpressionSystem.Input.Keyboard";
    public const string GestureSettingsSpace = "ExpressionSystem.Input.HandGestures";
}
