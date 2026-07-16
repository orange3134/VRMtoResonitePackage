using System.Globalization;
using System.Text.Json;

namespace VrmToResonitePackage;

internal static class AppLocalization
{
    private const string FallbackLanguage = "en";
    private static readonly IReadOnlyDictionary<string, AppLocale> Locales = LoadLocales();
    private static AppLocale _current = ResolveLocale(null);

    public static string CurrentLanguageCode => _current.Code;

    public static IReadOnlyList<AppLocaleInfo> AvailableLocales { get; } = Locales.Values
        .Select(locale => new AppLocaleInfo(locale.Code, locale.Name))
        .OrderBy(locale => locale.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    public static void Initialize(string languageCode)
    {
        _current = ResolveLocale(languageCode);
        CultureInfo.CurrentUICulture = GetCulture(_current.Code);
    }

    public static string Get(string key)
    {
        if (_current.Strings.TryGetValue(key, out string value))
        {
            return value;
        }
        if (Locales[FallbackLanguage].Strings.TryGetValue(key, out value))
        {
            return value;
        }
        return key;
    }

    public static string Format(string key, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), values);

    private static AppLocale ResolveLocale(string languageCode)
    {
        string requested = string.IsNullOrWhiteSpace(languageCode)
            ? CultureInfo.CurrentUICulture.Name
            : languageCode;
        if (TryFindLocale(requested, out AppLocale locale))
        {
            return locale;
        }
        return Locales[FallbackLanguage];
    }

    private static bool TryFindLocale(string languageCode, out AppLocale locale)
    {
        string candidate = languageCode?.Trim().Replace('_', '-');
        while (!string.IsNullOrWhiteSpace(candidate))
        {
            if (Locales.TryGetValue(candidate, out locale))
            {
                return true;
            }
            int separator = candidate.LastIndexOf('-');
            candidate = separator > 0 ? candidate[..separator] : null;
        }
        locale = null;
        return false;
    }

    private static CultureInfo GetCulture(string languageCode)
    {
        try
        {
            return CultureInfo.GetCultureInfo(languageCode);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo(FallbackLanguage);
        }
    }

    private static IReadOnlyDictionary<string, AppLocale> LoadLocales()
    {
        var locales = new Dictionary<string, AppLocale>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(AppLocalization).Assembly;
        foreach (string resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.Contains(".Locales.", StringComparison.Ordinal) &&
                                    name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            using Stream stream = assembly.GetManifestResourceStream(resourceName);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            string code = root.GetProperty("code").GetString();
            string name = root.GetProperty("name").GetString();
            var strings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.GetProperty("strings").EnumerateObject())
            {
                strings[property.Name] = property.Value.GetString() ?? string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(name))
            {
                locales[code] = new AppLocale(code, name, strings);
            }
        }
        if (!locales.ContainsKey(FallbackLanguage))
        {
            throw new InvalidOperationException("The embedded English locale is missing.");
        }
        return locales;
    }

    private sealed record AppLocale(string Code, string Name, IReadOnlyDictionary<string, string> Strings);
}

internal sealed record AppLocaleInfo(string Code, string Name)
{
    public override string ToString() => Name;
}
