using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace VrmToResonitePackage;

/// <summary>
/// Minimal SVG renderer for the bundled UI artwork. The logo and loading graphics are pure
/// single-fill <c>&lt;path&gt;</c> documents, so we extract their <c>viewBox</c> and path data and
/// build a recolorable <see cref="DrawingImage"/> with WPF's path mini-language (SVG-compatible).
/// This keeps the marks crisply vector and lets us tint them to the current theme.
/// </summary>
internal static class SvgImage
{
    private static readonly Regex ViewBoxRegex = new(
        "viewBox\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled);

    // Matches a standalone d="..." path attribute (\b keeps it from matching id=, data-name=, width=).
    private static readonly Regex PathDataRegex = new(
        "\\bd\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>Loads an embedded SVG resource (e.g. "logo.svg") and tints every path with <paramref name="fill"/>.</summary>
    public static DrawingImage Load(string resourceFileName, Color fill)
    {
        string svg = ReadEmbedded(resourceFileName);
        return Build(svg, fill);
    }

    private static string ReadEmbedded(string resourceFileName)
    {
        string resourceName = $"VrmToResonitePackage.Resources.{resourceFileName}";
        using Stream stream = typeof(SvgImage).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"埋め込みリソースが見つかりません: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static DrawingImage Build(string svg, Color fill)
    {
        Rect bounds = ParseViewBox(svg);
        var brush = new SolidColorBrush(fill);
        brush.Freeze();

        var group = new DrawingGroup();
        // A transparent rectangle over the full viewBox locks the image's intrinsic size/aspect so the
        // Image control scales it correctly even when the paths don't reach the viewBox edges.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(bounds)));

        foreach (Match match in PathDataRegex.Matches(svg))
        {
            // "F1" selects the nonzero fill rule (SVG default) so nested subpaths form holes correctly.
            Geometry geometry = Geometry.Parse("F1 " + match.Groups[1].Value);
            group.Children.Add(new GeometryDrawing(brush, null, geometry));
        }

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static Rect ParseViewBox(string svg)
    {
        Match match = ViewBoxRegex.Match(svg);
        if (match.Success)
        {
            string[] parts = match.Groups[1].Value.Split(
                new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double w) &&
                double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double h))
            {
                return new Rect(x, y, w, h);
            }
        }
        return new Rect(0, 0, 100, 100);
    }
}
