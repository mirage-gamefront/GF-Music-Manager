using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace GfMusicManager.Core.Localization;

public static class UiText
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalogs =
        UiLanguage.Supported.ToDictionary(
            language => language,
            LoadCatalog,
            StringComparer.OrdinalIgnoreCase);

    private static string _language = UiLanguage.Japanese;

    public static string Language => _language;

    public static IReadOnlyCollection<string> Keys =>
        Catalogs[UiLanguage.Japanese].Keys.ToArray();

    public static void SetLanguage(string? language)
    {
        _language = UiLanguage.Normalize(language);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(_language);
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(_language);
    }

    public static string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (Catalogs[_language].TryGetValue(key, out var localized))
        {
            return localized;
        }

        if (Catalogs[UiLanguage.Japanese].TryGetValue(key, out var fallback))
        {
            return fallback;
        }

        throw new KeyNotFoundException($"Localization key was not found: {key}");
    }

    public static string Format(string key, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
    }

    public static IReadOnlyList<string> ValidateCatalogs()
    {
        var expected = Catalogs[UiLanguage.Japanese].Keys.ToHashSet(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var language in UiLanguage.Supported)
        {
            var actual = Catalogs[language].Keys.ToHashSet(StringComparer.Ordinal);
            foreach (var missing in expected.Except(actual).OrderBy(value => value, StringComparer.Ordinal))
            {
                errors.Add($"{language}: missing {missing}");
            }

            foreach (var extra in actual.Except(expected).OrderBy(value => value, StringComparer.Ordinal))
            {
                errors.Add($"{language}: unexpected {extra}");
            }
        }

        return errors;
    }

    private static IReadOnlyDictionary<string, string> LoadCatalog(string language)
    {
        var assembly = typeof(UiText).Assembly;
        var resourceName = $"{assembly.GetName().Name}.Localization.Strings.{language}.json";
        using var stream = assembly.GetManifestResourceStream(resourceName) ??
                           throw new InvalidOperationException(
                               $"Localization resource was not found: {resourceName}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ??
               throw new InvalidOperationException(
                   $"Localization resource is empty: {resourceName}");
    }
}
