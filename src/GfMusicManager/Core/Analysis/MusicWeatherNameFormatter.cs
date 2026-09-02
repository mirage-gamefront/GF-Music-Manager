using System.Text.RegularExpressions;
using GfMusicManager.Core.Localization;

namespace GfMusicManager.Core.Analysis;

/// <summary>
/// Adds a localized explanation to Weather EditorIDs without replacing the
/// original identifier shown to the user.  The legacy method name is retained
/// for snapshot and API compatibility.
/// </summary>
public static class MusicWeatherNameFormatter
{
    private static readonly Regex TokenPattern = new(
        "[A-Z]+(?=[A-Z][a-z]|[0-9]|$)|[A-Z]?[a-z]+|[0-9]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly IReadOnlySet<string> IgnoredTokens =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DLC",
            "USKP",
            "RE",
            "CS",
            "Skyrim",
            "No",
            "Precip",
            "Weather"
        };

    public static string? InferJapaneseName(string? editorId)
    {
        if (string.IsNullOrWhiteSpace(editorId))
        {
            return null;
        }

        var normalized = editorId.Trim();
        var exactKey = $"Analysis.Weather.Exact.{normalized}";
        if (UiText.Keys.Contains(exactKey, StringComparer.Ordinal))
        {
            return UiText.Get(exactKey);
        }

        var tokens = TokenPattern.Matches(normalized)
            .Select(match => match.Value)
            .ToArray();
        if (tokens.Length == 0)
        {
            return null;
        }

        var translated = new List<string>();
        var hasUnknownToken = false;
        for (var index = 0; index < tokens.Length; index++)
        {
            if (tokens[index].Equals("No", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < tokens.Length &&
                tokens[index + 1].Equals("Precip", StringComparison.OrdinalIgnoreCase))
            {
                translated.Add(UiText.Get("Analysis.Weather.NoPrecipitation"));
                index++;
                continue;
            }

            if (IgnoredTokens.Contains(tokens[index]))
            {
                continue;
            }

            var tokenKey = $"Analysis.Weather.Token.{tokens[index]}";
            if (UiText.Keys.Contains(tokenKey, StringComparer.Ordinal))
            {
                var localized = UiText.Get(tokenKey);
                if (!string.IsNullOrWhiteSpace(localized))
                {
                    translated.Add(localized);
                }

                continue;
            }

            if (int.TryParse(tokens[index], out _))
            {
                translated.Add(tokens[index]);
                continue;
            }

            hasUnknownToken = true;
        }

        return !hasUnknownToken && translated.Count > 0
            ? string.Join(
                UiText.Get("Analysis.Weather.TokenSeparator"),
                translated.Distinct(StringComparer.Ordinal))
            : null;
    }

    public static string? FormatLabel(string? editorId, string? displayName)
    {
        var cleanEditorId = Clean(editorId);
        var cleanDisplayName = Clean(displayName);
        var inferredName = cleanDisplayName ?? InferJapaneseName(cleanEditorId);
        if (!string.IsNullOrWhiteSpace(cleanEditorId) &&
            !string.IsNullOrWhiteSpace(inferredName) &&
            !cleanEditorId.Equals(inferredName, StringComparison.OrdinalIgnoreCase))
        {
            return UiText.Format(
                "Analysis.Weather.LabelWithTechnicalName",
                cleanEditorId,
                inferredName);
        }

        return cleanEditorId ?? inferredName;
    }

    private static string? Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) || value.TrimStart().StartsWith("$", StringComparison.Ordinal)
            ? null
            : value.Trim();
    }
}
