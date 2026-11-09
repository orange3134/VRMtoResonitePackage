using FrooxEngine;

namespace VrmToResonitePackage.Expressions;

/// <summary>Icons shared by the expression menu and VRM preset clips.</summary>
internal static class ExpressionMenuIcons
{
    internal const string NeutralIconUrl =
        "resdb:///5e825a7a76c784575d4a17d87eb406793ad5c0bfb26d3e9d48c6e07d284d0038.webp";

    internal const string ResetIconUrl =
        "resdb:///391de2844bdd33eb12ccdeb7162edb9b5033f01946672cac59dc541440ba7f0d.webp";

    internal static string PresetIconUrl(string preset) => preset switch
    {
        "happy" => "resdb:///a3b67e86407b719fc5023f03c8fc5ab6486ebe8834b9d12dbacaf7347578aab5.webp",
        "angry" => "resdb:///11a4635a39119c4204c848f1e42ddafa009ad70eb04b666d246e2d73307c6126.webp",
        "sad" => "resdb:///fc79689c99ab5c5828c943492a3ec3158e088b64dcdbe6f804934316f8112a22.webp",
        "relaxed" => "resdb:///b3f3319d6e206116101500d9307cbb6699119f93ecee72c05a393654a0227a20.webp",
        "surprised" => "resdb:///e9d013f28bdfeb29ad7f5f49ae39d670293f37f89c48735c808fc9b30d1a0a72.webp",
        "neutral" => NeutralIconUrl,
        _ => null,
    };

    internal static void Apply(Slot itemSlot, ContextMenuItemSource itemSource, string iconUrl)
    {
        var texture = itemSlot.AttachComponent<StaticTexture2D>();
        texture.URL.Value = new Uri(iconUrl);
        var sprite = itemSlot.AttachComponent<SpriteProvider>();
        sprite.Texture.Target = texture;
        itemSource.Sprite.Target = sprite;
    }
}
