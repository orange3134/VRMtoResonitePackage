namespace VrmToResonitePackage.Expressions;

// Singleton modules share SystemSpace with State.*, Config.*, Reference.* and GestureTable.* keys.
// Repeated records use hierarchical space names shared by instances of the same schema.
internal static class ExpressionSpaces
{
    public const string SystemSpace = "ExpressionSystem";
    public const string ClipSpace = "ExpressionSystem.Clip";
    public const string OutputSpace = "ExpressionSystem.Output";
    public const string KeyboardSpace = "ExpressionSystem.Input.Keyboard";
    public const string GestureSettingsSpace = "ExpressionSystem.Input.HandGesture";
}
