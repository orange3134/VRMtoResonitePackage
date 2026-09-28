namespace VrmToResonitePackage.Expressions;

// Singleton modules share SystemSpace with dotted variable prefixes (Core.*, GestureTable.*).
// Repeated records use hierarchical space names shared by instances of the same schema.
internal static class ExpressionSpaces
{
    public const string SystemSpace = "ExpressionSystem";
    public const string ClipSpace = "ExpressionSystem.Catalog.Clip";
    public const string BindingPrefix = "Binding.";
    public const string OutputSpace = "ExpressionSystem.Output";
    public const string KeyboardSpace = "ExpressionSystem.Input.Keyboard";
    public const string GestureSettingsSpace = "ExpressionSystem.Input.HandGestures";
    public const string GestureHandSpace = "ExpressionSystem.Input.HandGestures.Hand";
}
