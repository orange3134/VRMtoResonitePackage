using FrooxEngine;

namespace VrmToResonitePackage;

/// <summary>Preserves attribution when adapted graphs and assets leave the converter.</summary>
internal static class ThirdPartyNotices
{
    private static readonly Lazy<string> ModularAvatarNotice = new(() =>
    {
        using var stream = typeof(ThirdPartyNotices).Assembly
            .GetManifestResourceStream("ResoPon.ControllerGestureNotice")
            ?? throw new InvalidOperationException("Third-party notices resource was not found.");
        using var reader = new StreamReader(stream);
        string text = reader.ReadToEnd().Replace("\r\n", "\n");
        const string heading = "## Modular Avatar for Resonite\n";
        int start = text.IndexOf(heading, StringComparison.Ordinal);
        if (start < 0) throw new InvalidDataException("Modular Avatar for Resonite notice was not found.");
        int end = text.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return (end < 0 ? text[start..] : text[start..end]).Trim();
    });

    public static void AddModularAvatar(Slot parent)
    {
        // Keep the notice within each adapted subtree so copying that subsystem retains it.
        Slot notice = parent.FindChild("THIRD-PARTY-NOTICES")
            ?? parent.AddSlot("THIRD-PARTY-NOTICES");
        var comment = notice.GetComponent<Comment>() ?? notice.AttachComponent<Comment>();
        comment.Text.Value = ModularAvatarNotice.Value;
    }
}
