using System.Text.RegularExpressions;
using GfMusicManager.Core.Localization;

namespace GfMusicManager.Core.Analysis;

/// <summary>
/// Creates a localized explanation for a condition Keyword when the plugin's
/// record does not contain a localized display name.  This deliberately works
/// on generic EditorID tokens; individual Keyword FormIDs are not embedded in
/// the application.  The legacy method name is retained for snapshot and API
/// compatibility.
/// </summary>
public static class MusicKeywordNameFormatter
{
    private static readonly Regex EditorIdTokenPattern = new(
        "[A-Z]+(?=[A-Z][a-z]|[0-9]|$)|[A-Z]?[a-z]+|[0-9]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string? InferJapaneseName(string? editorId)
    {
        if (string.IsNullOrWhiteSpace(editorId))
        {
            return null;
        }

        var tokens = EditorIdTokenPattern.Matches(editorId.Trim())
            .Select(match => match.Value)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToArray();
        if (tokens.Length == 0)
        {
            return null;
        }

        var translated = new List<string>(tokens.Length);
        foreach (var token in tokens)
        {
            if (!TryGetTokenName(token, out var localized))
            {
                return null;
            }

            if (!string.IsNullOrEmpty(localized))
            {
                translated.Add(localized);
            }
        }

        var result = string.Join(
            UiText.Get("Analysis.Keyword.TokenSeparator"),
            translated);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static bool TryGetTokenName(string token, out string localized)
    {
        var key = $"Analysis.Keyword.Token.{token}";
        if (!UiText.Keys.Contains(key, StringComparer.Ordinal))
        {
            localized = string.Empty;
            return false;
        }

        localized = UiText.Get(key);
        return true;
    }
}
