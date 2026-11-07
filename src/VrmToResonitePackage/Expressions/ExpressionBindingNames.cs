using System.Globalization;
using System.Text;

namespace VrmToResonitePackage.Expressions;

internal static class ExpressionBindingNames
{
    public static string Create(string mesh, string shape, ISet<string> used)
    {
        string stem = Sanitize(mesh, "Mesh") + "." + Sanitize(shape, "Shape");
        string name = stem;
        for (int suffix = 2; !used.Add(name); suffix++) name = stem + "." + suffix.ToString(CultureInfo.InvariantCulture);
        return name;
    }

    private static string Sanitize(string text, string fallback)
    {
        var result = new StringBuilder();
        foreach (char c in (text ?? "").Trim())
        {
            // Resonite permits these four punctuation/whitespace exceptions.
            // Also replace invisible controls and UTF-16 surrogate code units.
            bool allowed = c is ' ' or '-' or '.' or '_' ||
                !(char.IsSymbol(c) || char.IsPunctuation(c) || char.IsWhiteSpace(c) ||
                  char.IsControl(c) || char.IsSurrogate(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format);
            result.Append(allowed ? c : '_');
        }
        return result.Length == 0 ? fallback : result.ToString();
    }
}
