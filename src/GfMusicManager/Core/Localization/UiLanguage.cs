namespace GfMusicManager.Core.Localization;

public static class UiLanguage
{
    public const string Japanese = "ja-JP";
    public const string English = "en-US";

    public static readonly IReadOnlyList<string> Supported = new[] { Japanese, English };

    /// <summary>
    /// Picks the supported language that matches the operating system's
    /// language: Japanese for any Japanese culture, otherwise English.
    /// </summary>
    public static string FromCulture(System.Globalization.CultureInfo? culture) =>
        string.Equals(culture?.TwoLetterISOLanguageName, "ja", StringComparison.OrdinalIgnoreCase)
            ? Japanese
            : English;

    public static string Normalize(string? language)
    {
        return Supported.Any(code =>
            string.Equals(code, language, StringComparison.OrdinalIgnoreCase))
            ? Supported.First(code =>
                string.Equals(code, language, StringComparison.OrdinalIgnoreCase))
            : Japanese;
    }
}
